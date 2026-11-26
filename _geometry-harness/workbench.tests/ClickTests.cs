using System.Numerics;
using HousecarlCore;
using SkyWright.Engine;
using Mutagen.Bethesda.Plugins;
using SkyRender;
using ViewProbe;
using Workbench;
using Xunit;

namespace WorkbenchTests;

/// <summary>Piranha's rule "a plain click only selects", through the same press/move/release the window's mouse
/// messages call, with a synthetic top-down camera: a Skyrim point (x, y, z) is drawn at screen pixel (x, y), and the
/// eye ray through a pixel points straight down.</summary>
[Trait("tier", "unit")]
public class ClickTests
{
    static readonly Vector3 Down = new(0, 0, -1);
    static Vector2? Project(Vector3 p) => new Vector2(p.X, p.Y);
    static (Vector3 O, Vector3 D) Ray(Vector2 px) => (new Vector3(px.X, px.Y, 5000), Down);

    static readonly Vector3[] Box =
    {
        new(-40, -20, 0), new(40, -20, 0), new(40, 20, 0), new(-40, 20, 0),
        new(-40, -20, 60), new(40, -20, 60), new(40, 20, 60), new(-40, 20, 60),
    };
    static readonly int[] BoxTris = { 0,1,2, 0,2,3, 4,5,6, 4,6,7, 0,1,5, 0,5,4, 1,2,6, 1,6,5, 2,3,7, 2,7,6, 3,0,4, 3,4,7 };

    /// <summary>One drawn reference: an unrotated box at <paramref name="at"/>, its pick shape and a (never uploaded) mesh.</summary>
    static RefOcclusion Ref(uint id, Vec3 at)
    {
        var key = new FormKey(new ModKey("HcWbClick", ModType.Plugin), id);
        var r = new SceneRef("meshes\\test.nif", at, new Vec3(0, 0, 0), 1f, false, RefKey: key);
        var mesh = new GlMesh();
        var ps = new PickShape { Kind = PickKind.Ref, Verts = Box.Select(v => TexturedScene.YUp(v + new Vector3(at.X, at.Y, at.Z))).ToArray(), Tris = BoxTris, Ref = r, Mesh = mesh };
        ps.ComputeBounds();
        var ro = new RefOcclusion { Ref = r };
        ro.Meshes.Add(mesh); ro.Shapes.Add(ps);
        return ro;
    }

    static (ViewInput In, EditSession S, Gizmo G, RefOcclusion A, RefOcclusion B) Scene()
    {
        var a = Ref(0x800, new Vec3(1000, 2000, 300));
        var b = Ref(0x801, new Vec3(3000, 2000, 300));
        var shapes = a.Shapes.Concat(b.Shapes).ToList();
        var s = new EditSession(new[] { a, b });
        var g = new Gizmo();
        var input = new ViewInput(s, g, Project, Ray,
            px => { var (o, d) = Ray(px); return ScenePick.Pick(shapes, TexturedScene.YUp(o), TexturedScene.YUp(d), _ => true); },
            () => g.Size = 100f);
        return (input, s, g, a, b);
    }

    static void Click(ViewInput i, Vector2 press, params Vector2[] then)
    {
        i.OnPress(press);
        for (int k = 0; k < then.Length - 1; k++) i.OnMove(then[k]);
        i.OnRelease(then.Length > 0 ? then[^1] : press);
    }

    [Fact]
    public void A_press_and_release_on_the_handle_with_a_few_pixels_of_jitter_stages_nothing()
    {
        var (input, s, g, a, _) = Scene();
        input.Select(a.Ref.RefKey);
        // The X arrow runs from the centre (1000, 2000) to (1100, 2000) on screen; press near its tip.
        var onArrow = new Vector2(1090, 2000);
        input.OnPress(onArrow);
        Assert.Equal(Handle.MoveX, g.Hot);          // the press did grab the handle
        input.OnMove(new Vector2(1091, 2001));
        input.OnMove(new Vector2(1092, 2002));      // 2.8 px from the press
        input.OnRelease(new Vector2(1092, 2001));
        Assert.True(s.Pending.Count == 0, $"a click with jitter staged {s.Pending.Count} edit(s)");
        Assert.True(a.Meshes[0].ModelTransform is null, "a click with jitter moved the reference on screen");
        Assert.Equal(a.Ref.RefKey, s.Selected);
    }

    [Fact]
    public void A_click_on_another_ref_while_one_is_selected_selects_it_and_moves_nothing()
    {
        var (input, s, _, a, b) = Scene();
        input.Select(a.Ref.RefKey);
        Click(input, new Vector2(3000, 2000), new Vector2(3001, 2001), new Vector2(3002, 2000));
        Assert.True(s.Selected == b.Ref.RefKey, $"after a click on {b.Ref.RefKey} the selection is {s.Selected?.ToString() ?? "nothing"}");
        Assert.Empty(s.Pending);
        Assert.Null(a.Meshes[0].ModelTransform);
        Assert.Null(b.Meshes[0].ModelTransform);
    }

    [Fact]
    public void A_drag_on_the_arrow_past_the_click_slop_stages_one_move_along_it()
    {
        // The positive control: the same handle and the same harness do stage an edit once the press really moves.
        var (input, s, _, a, _) = Scene();
        input.Select(a.Ref.RefKey);
        Click(input, new Vector2(1090, 2000), new Vector2(1110, 2000), new Vector2(1130, 2000), new Vector2(1140, 2000));
        Assert.Single(s.Pending);
        Assert.Equal(1050f, s.Pending[0].After.Position.X, 2);
        Assert.Equal(2000f, s.Pending[0].After.Position.Y, 2);
        Assert.NotNull(a.Meshes[0].ModelTransform);
    }
}
