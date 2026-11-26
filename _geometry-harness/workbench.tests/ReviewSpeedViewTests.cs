using System.Numerics;
using HousecarlCore;
using SkyWright.Engine;
using Mutagen.Bethesda.Plugins;
using SkyRender;
using ViewProbe;
using Workbench;
using Xunit;

namespace WorkbenchTests;

/// <summary>Synthetic scenes for the review-speed tests: axis-aligned boxes placed as the build bakes them (Y-up pick
/// triangles, one never-uploaded mesh per part), and a flat terrain quad.</summary>
internal static class Boxes
{
    static readonly int[] Tris = { 0,1,2, 0,2,3, 4,5,6, 4,6,7, 0,1,5, 0,5,4, 1,2,6, 1,6,5, 2,3,7, 2,7,6, 3,0,4, 3,4,7 };

    internal static FormKey Key(uint id) => new(new ModKey("HcWbTest", ModType.Plugin), id);

    /// <summary>A box of half-size <paramref name="half"/> standing on <paramref name="at"/> (its base centre), in
    /// <paramref name="parts"/> stacked parts, each its own mesh and pick shape.</summary>
    internal static RefOcclusion Box(uint id, Vec3 at, Vector3 half, int parts = 1, string name = "TestBox")
    {
        var key = Key(id);
        var r = new SceneRef("meshes\\test.nif", at, new Vec3(0, 0, 0), 1f, false, RefKey: key, BaseEditorId: name);
        var ro = new RefOcclusion { Ref = r };
        float h = half.Z * 2 / parts;
        for (int p = 0; p < parts; p++)
        {
            float z0 = p * h, z1 = (p + 1) * h;
            var local = new[]
            {
                new Vector3(-half.X, -half.Y, z0), new Vector3(half.X, -half.Y, z0), new Vector3(half.X, half.Y, z0), new Vector3(-half.X, half.Y, z0),
                new Vector3(-half.X, -half.Y, z1), new Vector3(half.X, -half.Y, z1), new Vector3(half.X, half.Y, z1), new Vector3(-half.X, half.Y, z1),
            };
            var verts = local.Select(v => TexturedScene.YUp(new Vector3(v.X + at.X, v.Y + at.Y, v.Z + at.Z))).ToArray();
            var mesh = new GlMesh();
            var ps = new PickShape { Kind = PickKind.Ref, Verts = verts, Tris = Tris, Ref = r, Mesh = mesh };
            ps.ComputeBounds();
            ro.Meshes.Add(mesh); ro.Shapes.Add(ps);
        }
        var all = ro.Shapes.SelectMany(s => s.Verts).Select(y => new Vector3(y.X, -y.Z, y.Y)).ToList();
        ro.Min = all.Aggregate(Vector3.Min); ro.Max = all.Aggregate(Vector3.Max);
        return ro;
    }

    /// <summary>A flat terrain square at height <paramref name="z"/>, centred on (cx, cy).</summary>
    internal static PickShape Terrain(float z, float cx = 0, float cy = 0, float half = 4096)
    {
        var verts = new[]
        {
            new Vector3(cx - half, cy - half, z), new Vector3(cx + half, cy - half, z), new Vector3(cx + half, cy + half, z), new Vector3(cx - half, cy + half, z),
        }.Select(TexturedScene.YUp).ToArray();
        var ps = new PickShape { Kind = PickKind.Terrain, Verts = verts, Tris = new[] { 0, 1, 2, 0, 2, 3 }, Mesh = new GlMesh() };
        ps.ComputeBounds();
        return ps;
    }

    internal static RefPlacementEdit.Pose Moved(RefOcclusion ro, float dx, float dy = 0, float dz = 0)
    {
        var b = RefPlacementEdit.BakedPose(ro.Ref);
        return b with { Position = new Vec3(b.Position.X + dx, b.Position.Y + dy, b.Position.Z + dz) };
    }
}

/// <summary>Items 1 and 3: the view-only state. Showing originals, the ghost, hiding (whole references, parts, what is in
/// front), see-through, isolation and restore, on synthetic references; none of it touches the edit list.</summary>
[Trait("tier", "unit")]
public class ReviewSpeedViewTests
{
    static (EditSession S, ViewState V, RefOcclusion A, RefOcclusion B) Two()
    {
        var a = Boxes.Box(0x800, new Vec3(0, 0, 0), new Vector3(40, 40, 60));
        var b = Boxes.Box(0x801, new Vec3(300, 0, 0), new Vector3(40, 40, 60));
        var s = new EditSession(new[] { a, b });
        return (s, new ViewState(s, () => new[] { a, b }), a, b);
    }

    // I1a
    [Fact]
    public void Showing_originals_draws_every_edited_reference_at_its_load_order_place_and_releasing_draws_the_edits()
    {
        var (s, v, a, b) = Two();
        s.Stage(a.Ref.RefKey, Boxes.Moved(a, 150));
        s.Stage(b.Ref.RefKey, Boxes.Moved(b, 0, 90));
        var pending = s.Pending.ToList();
        v.ShowOriginal(true);
        Assert.True(v.ShowingOriginal, "the view does not say it shows originals");
        Assert.All(new[] { a, b }, ro => Assert.True(ro.Meshes[0].ModelTransform is null, $"{ro.Ref.RefKey} is not drawn at its load-order place while originals show"));
        Assert.Equal(pending, s.Pending);                              // the hold never changes what a commit writes
        var bakedMin = Boxes.Box(0x800, new Vec3(0, 0, 0), new Vector3(40, 40, 60)).Shapes[0].Min;
        Assert.True(Vector3.Distance(bakedMin, a.Shapes[0].Min) < 0.01f, $"the pick shape is at {a.Shapes[0].Min} while originals show; the load-order place is {bakedMin}");
        v.ShowOriginal(false);
        Assert.False(v.ShowingOriginal);
        Assert.NotNull(a.Meshes[0].ModelTransform); Assert.NotNull(b.Meshes[0].ModelTransform);
        Assert.Equal(Boxes.Moved(a, 150), s.Current(a.Ref.RefKey));
    }

    // I1b
    [Fact]
    public void A_written_edit_is_drawn_at_its_load_order_place_while_originals_show()
    {
        var (s, v, a, _) = Two();
        s.Stage(a.Ref.RefKey, Boxes.Moved(a, -120));
        s.MarkWritten("WbTest.esp");
        v.ShowOriginal(true);
        Assert.True(a.Meshes[0].ModelTransform is null, "a written edit is still drawn at the patch's place while originals show");
        v.ShowOriginal(false);
        Assert.NotNull(a.Meshes[0].ModelTransform);
    }

    // I1c: one rule for every path that re-places a reference
    [Fact]
    public void Staging_or_undoing_while_originals_show_first_puts_the_edits_back_on_screen()
    {
        var (s, v, a, _) = Two();
        s.Stage(a.Ref.RefKey, Boxes.Moved(a, 150));
        v.ShowOriginal(true);
        Assert.True(v.ShowingOriginal, "the view does not say it shows originals");
                s.StageDelta(a.Ref.RefKey, new Vector3(10, 0, 0), Vector3.UnitZ, 0f);
        Assert.False(v.ShowingOriginal, "staging while originals show left the originals on screen");
        Assert.Equal(160f, s.Current(a.Ref.RefKey).Position.X, 3);
        Assert.NotNull(a.Meshes[0].ModelTransform);
        v.ShowOriginal(true);
        s.Undo();
        Assert.False(v.ShowingOriginal, "undo while originals show left the originals on screen");
        Assert.NotNull(a.Meshes[0].ModelTransform);                    // back to the first staged edit, drawn
    }

    // I1d: the hold key, its repeats and a lost focus
    [Fact]
    public void Space_held_shows_originals_repeats_are_ignored_and_a_lost_focus_releases_it()
    {
        var (s, v, a, _) = Two();
        s.Stage(a.Ref.RefKey, Boxes.Moved(a, 150));
        Assert.True(v.OnKey(ViewState.SpaceKey, down: true, repeat: false), "Space was not taken by the view");
        Assert.True(v.ShowingOriginal);
        Assert.True(v.OnKey(ViewState.SpaceKey, down: true, repeat: true));
        Assert.True(v.ShowingOriginal, "a key repeat toggled the hold off");
        v.OnFocusLost();
        Assert.False(v.ShowingOriginal, "a lost focus left originals showing");
        Assert.NotNull(a.Meshes[0].ModelTransform);
        Assert.False(v.OnKey('W', down: true, repeat: false), "the view took a key that is not its own");
    }

    // I1e: the ghost is the load-order place, from the copy the placement edit keeps
    [Fact]
    public void The_ghost_is_each_edited_reference_at_its_load_order_place()
    {
        var (s, v, a, b) = Two();
        var baked = a.Shapes[0].Verts.ToArray();
        s.Stage(a.Ref.RefKey, Boxes.Moved(a, 150));
        v.Ghost = true;
        var g = v.GhostTriangles();
        Assert.True(g.Count == a.Shapes[0].Tris.Length * 6, $"the ghost holds {g.Count / 6} vertices; the edited reference has {a.Shapes[0].Tris.Length}");
        for (int i = 0; i < a.Shapes[0].Tris.Length; i++)
        {
            var want = baked[a.Shapes[0].Tris[i]];
            Assert.Equal(want.X, g[i * 6], 3); Assert.Equal(want.Y, g[i * 6 + 1], 3); Assert.Equal(want.Z, g[i * 6 + 2], 3);
        }
        v.Ghost = false;
        Assert.Empty(v.GhostTriangles());
    }

    // I1f: RefPlacementEdit gives the build's vertices back after a move
    [Fact]
    public void The_baked_vertices_of_a_moved_shape_are_where_the_build_put_them()
    {
        var a = Boxes.Box(0x800, new Vec3(0, 0, 0), new Vector3(40, 40, 60));
        var baked = a.Shapes[0].Verts.ToArray();
        RefPlacementEdit.Apply(a, Boxes.Moved(a, 150));
        Assert.Equal(baked, RefPlacementEdit.BakedVerts(a.Shapes[0]));
    }

    // I3a
    [Fact]
    public void Hide_takes_a_reference_out_of_every_pass_and_restore_brings_it_back()
    {
        var (s, v, a, _) = Two();
        v.Hide(a.Ref.RefKey);
        Assert.True(a.Meshes.All(m => m.IsHiddenByHost), "the hidden reference's meshes still render");
        Assert.True(v.Active);
        Assert.Contains("Restore visibility", v.Label);
        Assert.Contains("1 hidden", v.Label);
        Assert.Empty(s.Pending);
        v.Restore();
        Assert.True(a.Meshes.All(m => !m.IsHiddenByHost), "restore left a mesh hidden");
        Assert.False(v.Active);
        Assert.Equal("", v.Label);
    }

    // I3b: a part, such as a roof that is one shape of the building's model
    [Fact]
    public void Hiding_a_part_hides_that_mesh_only()
    {
        var house = Boxes.Box(0x810, new Vec3(0, 0, 0), new Vector3(200, 200, 300), parts: 3, name: "TestHouse");
        var s = new EditSession(new[] { house });
        var v = new ViewState(s, () => new[] { house });
        v.HidePart(house.Shapes[2]);
        Assert.True(house.Meshes[2].IsHiddenByHost, "the picked part still renders");
        Assert.False(house.Meshes[0].IsHiddenByHost || house.Meshes[1].IsHiddenByHost, "hiding a part hid the rest of the reference");
        Assert.Equal(1, v.HiddenParts);
        Assert.Contains("part", v.Label);
    }

    // I3c: a hidden reference cannot be clicked; what is behind it can
    [Fact]
    public void A_hidden_reference_is_not_pickable()
    {
        var (_, v, a, _) = Two();
        Assert.True(WorkbenchHost.Pickable(a.Shapes[0]));
        v.Hide(a.Ref.RefKey);
        Assert.False(WorkbenchHost.Pickable(a.Shapes[0]), "a hidden reference can still be clicked");
    }

    // I3d: hide what is in front repeats the pick until the subject is hit
    [Fact]
    public void Hide_what_is_in_front_hides_each_reference_between_the_eye_and_the_subject()
    {
        var subject = Boxes.Box(0x820, new Vec3(0, 0, 0), new Vector3(40, 40, 60));
        var near = Boxes.Box(0x821, new Vec3(0, -300, 0), new Vector3(60, 20, 80));
        var nearer = Boxes.Box(0x822, new Vec3(0, -600, 0), new Vector3(60, 20, 80));
        var all = new[] { subject, near, nearer };
        var s = new EditSession(all);
        var v = new ViewState(s, () => all);
        var shapes = all.SelectMany(r => r.Shapes).ToList();
        // the eye 1000 units south, at mid height, looking north
        var o = TexturedScene.YUp(new Vector3(0, -1000, 50)); var d = TexturedScene.YUp(new Vector3(0, 1, 0));
        var said = v.HideInFront(subject.Ref.RefKey, () => ScenePick.Pick(shapes, o, d, WorkbenchHost.Pickable));
        Assert.Equal(new[] { near.Ref.RefKey, nearer.Ref.RefKey }.OrderBy(k => k.ToString()), v.Hidden.OrderBy(k => k.ToString()));
        Assert.False(subject.Meshes[0].IsHiddenByHost, "the subject was hidden");
        Assert.Contains("hid 2 references", said);
    }

    // I3e: terrain in front is said, not hidden
    [Fact]
    public void Hide_what_is_in_front_names_terrain_and_does_not_hide_it()
    {
        var subject = Boxes.Box(0x830, new Vec3(0, 0, -200), new Vector3(40, 40, 60));
        var ground = Boxes.Terrain(0);
        var s = new EditSession(new[] { subject });
        var v = new ViewState(s, () => new[] { subject });
        var shapes = new List<PickShape>(subject.Shapes) { ground };
        var o = TexturedScene.YUp(new Vector3(0, 0, 500)); var d = TexturedScene.YUp(new Vector3(0, 0, -1));
        var said = v.HideInFront(subject.Ref.RefKey, () => ScenePick.Pick(shapes, o, d, WorkbenchHost.Pickable));
        Assert.Contains("terrain", said);
        Assert.Empty(v.Hidden);
        Assert.False(ground.Mesh!.IsHiddenByHost);
    }

    // I3f: see-through is a labelled flat tint over a hidden reference
    [Fact]
    public void See_through_hides_the_meshes_tints_the_triangles_and_says_so()
    {
        var (_, v, a, _) = Two();
        v.SeeThrough(a.Ref.RefKey);
        Assert.True(a.Meshes.All(m => m.IsHiddenByHost), "a see-through reference still draws its textured meshes");
        Assert.Equal(a.Shapes[0].Tris.Length * 6, v.TintTriangles().Count);
        Assert.Contains(a.Ref.RefKey, v.SeeThroughRefs);
        Assert.Contains("see-through", v.Label);
        v.Restore();
        Assert.Empty(v.TintTriangles());
        Assert.False(a.Meshes[0].IsHiddenByHost);
    }

    // I3g
    [Fact]
    public void Isolate_hides_every_drawn_reference_but_the_two_and_restore_undoes_it()
    {
        var a = Boxes.Box(0x840, new Vec3(0, 0, 0), new Vector3(40, 40, 60));
        var b = Boxes.Box(0x841, new Vec3(100, 0, 0), new Vector3(40, 40, 60));
        var c = Boxes.Box(0x842, new Vec3(200, 0, 0), new Vector3(40, 40, 60));
        var d = Boxes.Box(0x843, new Vec3(300, 0, 0), new Vector3(40, 40, 60));
        var all = new[] { a, b, c, d };
        var v = new ViewState(new EditSession(all), () => all);
        v.Isolate(a.Ref.RefKey, b.Ref.RefKey);
        Assert.Equal(new[] { c.Ref.RefKey, d.Ref.RefKey }.OrderBy(k => k.ToString()), v.Hidden.OrderBy(k => k.ToString()));
        Assert.False(a.Meshes[0].IsHiddenByHost || b.Meshes[0].IsHiddenByHost, "isolate hid one of the two");
        v.Restore();
        Assert.True(all.All(r => !r.Meshes[0].IsHiddenByHost));
    }

    // I1h: a stage on one reference while originals show puts every edited reference back at its edit
    [Fact]
    public void Staging_one_reference_while_originals_show_redraws_the_other_edited_reference_at_its_edit()
    {
        var (s, v, a, b) = Two();
        s.Stage(a.Ref.RefKey, Boxes.Moved(a, 150));
        s.Stage(b.Ref.RefKey, Boxes.Moved(b, 0, 90));
        v.ShowOriginal(true);
        Assert.True(v.ShowingOriginal, "the view does not say it shows originals");
        s.StageDelta(a.Ref.RefKey, new Vector3(5, 0, 0), Vector3.UnitZ, 0f);
        Assert.NotNull(b.Meshes[0].ModelTransform);
        Assert.False(v.ShowingOriginal);
    }

    // I1i: releasing the key shows the edits
    [Fact]
    public void Releasing_Space_shows_the_edits_again()
    {
        var (s, v, a, _) = Two();
        s.Stage(a.Ref.RefKey, Boxes.Moved(a, 150));
        v.OnKey(ViewState.SpaceKey, down: true, repeat: false);
        Assert.True(v.ShowingOriginal, "Space down did not show originals");
        Assert.True(v.OnKey(ViewState.SpaceKey, down: false, repeat: false));
        Assert.False(v.ShowingOriginal, "Space up left originals showing");
        Assert.NotNull(a.Meshes[0].ModelTransform);
    }

    // I1j: revert, a write, forgetting a written edit and a session restore each clear the hold first
    [Fact]
    public void Revert_write_forget_and_restore_while_originals_show_clear_the_hold()
    {
        var steps = new (string Name, Action<EditSession, RefOcclusion, RefOcclusion> Do)[]
        {
            ("revert", (s, a, b) => s.Revert(a.Ref.RefKey)),
            ("write", (s, a, b) => s.MarkWritten("WbTest.esp")),
            ("forget", (s, a, b) => { s.MarkWritten("WbTest.esp"); }),
            ("restore", (s, a, b) => SessionFile.Restore(s, new SessionData("p", "arm", null, null, null,
                new[] { new SavedEdit(b.Ref.RefKey.ToString(), RefPlacementEdit.BakedPose(b.Ref), Boxes.Moved(b, 0, 30)) }, Array.Empty<SavedWritten>()), (_, _) => null)),
        };
        foreach (var (name, step) in steps)
        {
            var (s, v, a, b) = Two();
            s.Stage(a.Ref.RefKey, Boxes.Moved(a, 150));
            if (name == "forget") { s.MarkWritten("WbTest.esp"); v.ShowOriginal(true); s.ForgetWritten(a.Ref.RefKey); }
            else { v.ShowOriginal(true); Assert.True(v.ShowingOriginal, $"{name}: the view does not say it shows originals"); step(s, a, b); }
            Assert.False(v.ShowingOriginal, $"{name} while originals show left them showing");
        }
    }

    // I3i: a subject the centre ray never reaches: nothing is hidden, and that is said
    [Fact]
    public void Hide_what_is_in_front_hides_nothing_when_the_ray_never_reaches_the_subject()
    {
        var subject = Boxes.Box(0x850, new Vec3(2000, 0, 0), new Vector3(40, 40, 60));
        var wall = Boxes.Box(0x851, new Vec3(0, -300, 0), new Vector3(60, 20, 80));
        var all = new[] { subject, wall };
        var v = new ViewState(new EditSession(all), () => all);
        var shapes = all.SelectMany(r => r.Shapes).ToList();
        var o = TexturedScene.YUp(new Vector3(0, -1000, 50)); var d = TexturedScene.YUp(new Vector3(0, 1, 0));
        var said = v.HideInFront(subject.Ref.RefKey, () => ScenePick.Pick(shapes, o, d, WorkbenchHost.Pickable));
        Assert.Contains("does not reach", said);
        Assert.Empty(v.Hidden);
        Assert.False(wall.Meshes[0].IsHiddenByHost, "a reference was left hidden although the subject was never reached");
    }
}
