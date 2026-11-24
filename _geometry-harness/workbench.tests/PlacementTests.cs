using System.Numerics;
using HousecarlCore;
using SkyWright.Engine;
using Mutagen.Bethesda.Plugins;
using SkyRender;
using ViewProbe;
using Workbench;
using Xunit;

namespace WorkbenchTests;

/// <summary>The placement maths and the live edit, on synthetic geometry (no GL context, no load order).</summary>
[Trait("tier", "unit")]
public class PlacementTests
{
    const float AngleTol = 1e-5f;      // matrix entries, unitless
    const float UnitTol = 0.01f;       // game units

    static void SameRotation(Matrix4x4 a, Matrix4x4 b, string what)
    {
        float worst = 0f;
        for (int r = 0; r < 3; r++) for (int c = 0; c < 3; c++) worst = MathF.Max(worst, MathF.Abs(a[r, c] - b[r, c]));
        Assert.True(worst <= AngleTol, $"{what}: rotation matrices differ by {worst} (tolerance {AngleTol})");
    }

    [Fact]
    public void RefrAngles_inverts_RefrRotation_over_random_angles_and_both_poles()
    {
        var rng = new Random(20261114);
        var cases = new List<Vec3>();
        for (int i = 0; i < 500; i++)
            cases.Add(new Vec3((float)(rng.NextDouble() * 2 * Math.PI), (float)(rng.NextDouble() * 2 * Math.PI), (float)(rng.NextDouble() * 2 * Math.PI)));
        cases.Add(new Vec3(0.3f, MathF.PI / 2, 1.1f));          // the poles: Y = ±90°
        cases.Add(new Vec3(-0.7f, -MathF.PI / 2, 2.5f));
        foreach (var a in cases)
        {
            var back = SpatialOverlap.RefrAngles(SpatialOverlap.RefrRotation(a));
            SameRotation(SpatialOverlap.RefrRotation(back), SpatialOverlap.RefrRotation(a), $"angles {a}");
        }
    }

    [Fact]
    public void A_turn_about_world_Z_is_composed_with_a_tilted_rotation_not_added_to_one_angle()
    {
        var tilted = new Vec3(0.4f, -0.3f, 4.78319f);
        float turn = 0.7f;
        var got = RefPlacementEdit.Turned(tilted, Vector3.UnitZ, turn);
        var want = SpatialOverlap.RefrRotation(tilted) * Matrix4x4.CreateRotationZ(turn);
        SameRotation(SpatialOverlap.RefrRotation(got), want, "turned rotation");
    }

    // A box of local vertices, placed the way the scene build bakes them: YUp(v * scale * RefrRotation + position).
    static readonly Vector3[] Local =
    {
        new(-40, -20, 0), new(40, -20, 0), new(40, 20, 0), new(-40, 20, 0),
        new(-40, -20, 60), new(40, -20, 60), new(40, 20, 60), new(-40, 20, 60),
    };
    static readonly int[] BoxTris = { 0,1,2, 0,2,3, 4,5,6, 4,6,7, 0,1,5, 0,5,4, 1,2,6, 1,6,5, 2,3,7, 2,7,6, 3,0,4, 3,4,7 };

    static Vector3[] Baked(RefPlacementEdit.Pose p) =>
        Local.Select(v => TexturedScene.YUp(Vector3.Transform(v, RefPlacementEdit.World(p)))).ToArray();

    static readonly RefPlacementEdit.Pose Start = new(new Vec3(1000, 2000, 300), new Vec3(0.4f, -0.3f, 4.78319f), 1.25f);

    static RefPlacementEdit.Pose Moved() =>
        Start with
        {
            Position = new Vec3(1250, 1900, 340),
            Rotation = RefPlacementEdit.Turned(Start.Rotation, Vector3.UnitZ, 0.9f),
        };

    [Fact]
    public void The_preview_draws_the_vertices_where_a_rebuild_from_the_new_values_puts_them()
    {
        var now = Moved();
        var d = RefPlacementEdit.DeltaYUp(Start, now);
        var preview = Baked(Start).Select(v => Vector3.Transform(v, d)).ToArray();
        var rebuild = Baked(now);
        float worst = preview.Zip(rebuild, Vector3.Distance).Max();
        Assert.True(worst <= UnitTol, $"preview and rebuild differ by {worst} u (tolerance {UnitTol} u)");
    }

    /// <summary>A scene of one reference: a box, its pick shape and a (never uploaded) mesh.</summary>
    internal static RefOcclusion OneRef(out FormKey key)
    {
        key = new FormKey(new ModKey("HcWbTest", ModType.Plugin), 0x800);
        var r = new SceneRef("meshes\\test.nif", Start.Position, Start.Rotation, Start.Scale, false, RefKey: key);
        var mesh = new GlMesh();
        var ps = new PickShape { Kind = PickKind.Ref, Verts = Baked(Start), Tris = BoxTris, Ref = r, Mesh = mesh };
        ps.ComputeBounds();
        var ro = new RefOcclusion { Ref = r };
        ro.Meshes.Add(mesh); ro.Shapes.Add(ps);
        return ro;
    }

    static string? PickDown(RefOcclusion ro, Vec3 at)
    {
        // A ray straight down onto the Skyrim point, in the renderer's Y-up space.
        var o = TexturedScene.YUp(new Vector3(at.X, at.Y, at.Z + 2000));
        var hit = ScenePick.Pick(ro.Shapes, o, new Vector3(0, -1, 0), _ => true);
        return hit?.Shape.Ref.RefKey.ToString();
    }

    [Fact]
    public void After_a_move_a_pick_at_the_new_place_names_the_ref_and_a_pick_at_the_old_place_does_not()
    {
        var ro = OneRef(out var key);
        Assert.Equal(key.ToString(), PickDown(ro, Start.Position));          // the fixture picks before the move
        var now = Moved();
        RefPlacementEdit.Apply(ro, now);
        Assert.Equal(key.ToString(), PickDown(ro, now.Position));
        Assert.Null(PickDown(ro, Start.Position));
        Assert.NotNull(ro.Meshes[0].ModelTransform);
    }

    [Fact]
    public void Undo_puts_the_ref_back_on_screen_and_in_the_pending_list()
    {
        var ro = OneRef(out var key);
        var s = new EditSession(new[] { ro });
        var first = Moved();
        s.Stage(key, first);
        var afterFirst = ro.Meshes[0].ModelTransform;
        s.StageDelta(key, new Vector3(0, 0, 100), Vector3.UnitZ, 0.5f);
        Assert.Equal(2, s.Pending.Count);

        s.Undo();
        Assert.Single(s.Pending);
        Assert.Equal(first, s.Current(key));
        Assert.Equal(afterFirst, ro.Meshes[0].ModelTransform);
        Assert.Equal(key.ToString(), PickDown(ro, first.Position));

        s.Undo();
        Assert.Empty(s.Pending);
        Assert.Null(ro.Meshes[0].ModelTransform);
        Assert.Equal(key.ToString(), PickDown(ro, Start.Position));
        Assert.Null(PickDown(ro, first.Position));
    }
}
