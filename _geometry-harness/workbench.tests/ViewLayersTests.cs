using System.Numerics;
using HousecarlCore;
using SkyWright.Engine;
using Mutagen.Bethesda.Plugins;
using SkyRender;
using ViewProbe;
using Workbench;
using Xunit;

namespace WorkbenchTests;

/// <summary>The 3D view's layer toolbar: the window's choice, each scene's availability and wording, the switches it
/// sets, the script step, and the layers' own overlays' geometry.</summary>
public class ViewLayersTests : IDisposable
{
    public ViewLayersTests() => ViewLayers.Reset();
    public void Dispose() => ViewLayers.Reset();

    sealed class Recorder
    {
        public readonly Dictionary<string, bool> Last = new();
        public readonly List<(List<GlMesh> Meshes, bool Hidden)> Hides = new();
        public LayerSwitches Switches(ViewState? v = null) => new(
            on => Last["navmesh"] = on, on => Last["planes"] = on, on => Last["borders"] = on, on => Last["markers"] = on, on => Last["marks"] = on, on => Last["boxes"] = on, on => Last["outlines"] = on,
            (m, hidden) => { Hides.Add((m.ToList(), hidden)); v?.SetLayerHidden(m, hidden); });
    }

    static readonly IReadOnlyDictionary<string, List<GlMesh>> NoMeshes = new Dictionary<string, List<GlMesh>>();

    static LayerFacts Exterior(IReadOnlyList<RefOcclusion>? drawn = null, IReadOnlyDictionary<FormKey, string>? types = null, IReadOnlyDictionary<string, List<GlMesh>>? meshes = null) =>
        new(Interior: false, NavBuilt: true, NavLeftOut: false, CanAskAgain: true, Planes: 3, LandCells: 9, Undrawn: 4,
            meshes ?? new Dictionary<string, List<GlMesh>> { ["terrain"] = new() { new GlMesh() }, ["water"] = new() { new GlMesh() }, ["grass"] = new() { new GlMesh() } },
            drawn ?? Array.Empty<RefOcclusion>(), types);

    [Fact]
    public void The_defaults_show_the_scene_as_built_and_leave_markers_and_planes_off()
    {
        foreach (var l in new[] { Layer.Navmesh, Layer.Borders, Layer.Terrain, Layer.Water, Layer.Grass, Layer.Trees, Layer.Clutter, Layer.Statics })
            Assert.True(ViewLayers.IsOn(l), $"{l} is off by default");
        Assert.False(ViewLayers.IsOn(Layer.Markers));
        Assert.False(ViewLayers.IsOn(Layer.Planes));
        Assert.Equal(Enum.GetValues<Layer>().Length, ViewLayers.All.Count);
        Assert.Equal(ViewLayers.All.Count, ViewLayers.All.Select(i => i.Name).Distinct().Count());
    }

    [Fact]
    public void A_choice_made_in_one_scene_is_applied_to_the_next()
    {
        var r1 = new Recorder();
        var a = SceneLayers.From(Exterior(), r1.Switches());
        a.ApplyAll();
        Assert.False(r1.Last["planes"]);
        Assert.Equal("Occlusion shown (3 plane(s))", a.Set(Layer.Planes, true));
        a.Set(Layer.Navmesh, false);
        Assert.True(r1.Last["planes"]); Assert.False(r1.Last["navmesh"]);

        // a cell switch: a new scene, built from its own facts, takes the window's choice
        var r2 = new Recorder();
        var b = SceneLayers.From(Exterior(), r2.Switches());
        b.ApplyAll();
        Assert.True(r2.Last["planes"], "the planes choice did not survive the cell switch");
        Assert.False(r2.Last["navmesh"], "the navmesh choice did not survive the cell switch");
        Assert.True(b.Shown(Layer.Planes));
    }

    [Fact]
    public void A_layer_a_scene_cannot_draw_is_refused_in_one_sentence_and_the_choice_is_kept()
    {
        var f = Exterior() with { Interior = true, LandCells = 0, Planes = 0, Undrawn = 0, Meshes = NoMeshes };
        var s = SceneLayers.From(f, new Recorder().Switches());
        Assert.False(s.Has(Layer.Borders));
        Assert.Equal("an interior has no cell grid, so there are no cell borders to draw.", s.Why(Layer.Borders));
        Assert.Equal("no occlusion planes stand in this cell or the cells around it.", s.Why(Layer.Planes));
        Assert.Equal("the scene met no editor marker and no object without a model.", s.Why(Layer.Markers));
        Assert.Equal("an interior has no landscape.", s.Why(Layer.Terrain));
        Assert.Equal("no tree or plant is drawn in this scene.", s.Why(Layer.Trees));
        var ex = Assert.Throws<InvalidOperationException>(() => s.Set(Layer.Borders, false));
        Assert.Equal("Cell borders cannot be shown here: an interior has no cell grid, so there are no cell borders to draw.", ex.Message);
        Assert.True(ViewLayers.IsOn(Layer.Borders), "a refused switch changed the window's choice");
        Assert.True(s.Has(Layer.Marks), "the marks are always the view's to switch");
    }

    [Fact]
    public void A_cell_opened_without_its_navmesh_offers_to_open_it_again_and_says_why()
    {
        var s = SceneLayers.From(Exterior() with { NavBuilt = false, NavLeftOut = true, CanAskAgain = true }, new Recorder().Switches());
        Assert.False(s.Has(Layer.Navmesh));
        Assert.True(s.NavLoadable);
        Assert.StartsWith("this cell was opened without its navmesh; press to open it again with it", s.Why(Layer.Navmesh));
        var cannot = SceneLayers.From(Exterior() with { NavBuilt = false, NavLeftOut = true, CanAskAgain = false }, new Recorder().Switches());
        Assert.False(cannot.NavLoadable);
        Assert.Contains("not opened from a tab that can ask for it again", cannot.Why(Layer.Navmesh));
        var none = SceneLayers.From(Exterior() with { NavBuilt = false }, new Recorder().Switches());
        Assert.Equal("no navmesh is loaded in this cell or the cells around it.", none.Why(Layer.Navmesh));

        // a cell request carries the navmesh only when it was asked for and the load order leaves it out
        var order = new LoadOrderSpec("i", "p", "g", null, "l") { Navmesh = false };
        Assert.Null(ViewLayers.NavFor(order));
        ViewLayers.WantsNavmesh = true;
        Assert.Equal("both", ViewLayers.NavFor(order));
        Assert.Null(ViewLayers.NavFor(order with { Navmesh = true }));
        ViewLayers.Choose(Layer.Navmesh, false);
        Assert.False(ViewLayers.WantsNavmesh, "hiding the navmesh kept asking for it in later cells");
        ViewLayers.Choose(Layer.Navmesh, true);
        Assert.True(ViewLayers.WantsNavmesh, "showing the navmesh by hand did not ask for it in later cells");
    }

    [Fact]
    public void The_script_step_and_the_state_names_parse_and_refuse_plainly()
    {
        Assert.Equal((Layer.Borders, true), ViewLayers.ParseStep(new[] { "layer", "borders", "on" }).Step);
        Assert.Equal((Layer.Planes, false), ViewLayers.ParseStep(new[] { "layer", "Occlusion", "off" }).Step);
        Assert.Equal((Layer.Statics, true), ViewLayers.ParseStep(new[] { "layer", "STATICS", "show" }).Step);
        var (bad, why) = ViewLayers.ParseStep(new[] { "layer", "roofs", "on" });
        Assert.Null(bad);
        Assert.StartsWith("'roofs' is not a layer of the view; use one of navmesh, borders, markers, planes,", why);
        Assert.Equal("'maybe' is not on or off.", ViewLayers.ParseStep(new[] { "layer", "water", "maybe" }).Why);
        Assert.StartsWith("the layer step is 'layer NAME on|off'", ViewLayers.ParseStep(new[] { "layer", "water" }).Why);
    }

    [Fact]
    public void The_cell_border_options_start_off_parse_and_reset()
    {
        ViewLayers.Reset();
        Assert.False(ViewLayers.IsOn(BorderOption.Through), "the outline is drawn through objects by default");
        Assert.False(ViewLayers.IsOn(BorderOption.Fence), "the fence is shown by default");
        Assert.Equal((BorderOption.Fence, true), ViewLayers.ParseBorderStep(new[] { "borders", "FENCE", "on" }).Step);
        Assert.Equal((BorderOption.Through, false), ViewLayers.ParseBorderStep(new[] { "borders", "through", "off" }).Step);
        Assert.Equal("'roof' is not an option of the cell borders; use through or fence.", ViewLayers.ParseBorderStep(new[] { "borders", "roof", "on" }).Why);
        ViewLayers.Choose(BorderOption.Fence, true);
        ViewLayers.Reset();
        Assert.False(ViewLayers.IsOn(BorderOption.Fence), "Reset kept the fence on");
    }

    [Fact]
    public void Objects_split_by_base_type_and_a_hidden_layer_is_not_clickable_and_survives_restore()
    {
        var tree = Boxes.Box(0x901, new Vec3(0, 0, 0), new Vector3(50, 50, 300), name: "TestTree");
        var barrel = Boxes.Box(0x902, new Vec3(500, 0, 0), new Vector3(30, 30, 40), name: "TestBarrel");
        var wall = Boxes.Box(0x903, new Vec3(-500, 0, 0), new Vector3(200, 20, 200), name: "TestWall");
        static RefOcclusion WithBase(RefOcclusion ro, uint b) { ro.Ref = ro.Ref with { BaseKey = Boxes.Key(b) }; foreach (var s in ro.Shapes) s.Ref = ro.Ref; return ro; }
        WithBase(tree, 0xA01); WithBase(barrel, 0xA02); WithBase(wall, 0xA03);
        var types = new Dictionary<FormKey, string> { [Boxes.Key(0xA01)] = "Tree", [Boxes.Key(0xA02)] = "Container" };   // the wall's base is not listed: a static
        var drawn = new[] { tree, barrel, wall };
        var session = new EditSession(drawn);
        var view = new ViewState(session, () => drawn);
        var rec = new Recorder();
        var s = SceneLayers.From(Exterior(drawn, types), rec.Switches(view));
        Assert.Equal(1, s.Count(Layer.Trees)); Assert.Equal(1, s.Count(Layer.Clutter)); Assert.Equal(1, s.Count(Layer.Statics));

        s.Set(Layer.Trees, false);
        Assert.True(tree.Meshes[0].IsHiddenByHost);
        Assert.False(barrel.Meshes[0].IsHiddenByHost || wall.Meshes[0].IsHiddenByHost, "hiding trees hid something else");
        Assert.False(WorkbenchHost.Pickable(tree.Shapes[0]), "a hidden layer's object can still be clicked");
        Assert.True(WorkbenchHost.Pickable(wall.Shapes[0]));

        // the view's own hide and restore do not bring a switched-off layer back
        view.Hide(tree.Ref.RefKey); view.Hide(wall.Ref.RefKey);
        view.Restore();
        Assert.True(tree.Meshes[0].IsHiddenByHost, "restore showed a layer that is switched off");
        Assert.False(wall.Meshes[0].IsHiddenByHost);
        s.Set(Layer.Trees, true);
        Assert.False(tree.Meshes[0].IsHiddenByHost);
        // and a layer switched back on does not show what the view itself hid
        view.Hide(barrel.Ref.RefKey);
        s.Set(Layer.Clutter, false); s.Set(Layer.Clutter, true);
        Assert.True(barrel.Meshes[0].IsHiddenByHost, "switching clutter back on showed a hidden object");
    }

    [Fact]
    public void Hidden_terrain_is_not_clickable()
    {
        var t = Boxes.Terrain(0f);
        Assert.True(WorkbenchHost.Pickable(t, terrainShown: true));
        Assert.False(WorkbenchHost.Pickable(t, terrainShown: false));
    }

    [Fact]
    public void Base_types_fall_into_trees_clutter_or_statics()
    {
        Assert.Equal(Layer.Trees, ViewLayers.CategoryOf("Flora"));
        Assert.Equal(Layer.Clutter, ViewLayers.CategoryOf("MiscItem"));
        Assert.Equal(Layer.Clutter, ViewLayers.CategoryOf("Light"));
        Assert.Equal(Layer.Statics, ViewLayers.CategoryOf("Static"));
        Assert.Equal(Layer.Statics, ViewLayers.CategoryOf("Door"));
        Assert.Equal(Layer.Statics, ViewLayers.CategoryOf(null));
    }

    [Fact]
    public void The_cell_grid_draws_each_edge_once_and_marks_the_scenes_own_cell()
    {
        var ring = new List<(int, int)>();
        for (int x = -1; x <= 1; x++) for (int y = -1; y <= 1; y++) ring.Add((x + 4, y - 3));
        var edges = CellBorders.Edges(ring, (4, -3));
        Assert.Equal(24, edges.Count);                     // a 3x3 block: 4 lines of 3 edges each way
        Assert.Equal(4, edges.Count(e => e.Own));
        // flat ground: every curtain quad of the 24 edges is drawn, two faces of two triangles each
        var b = new CellBorders(ring, (4, -3), (_, _) => 0f);
        Assert.Equal(24 * (int)(CellBorders.Cell / CellBorders.Step) * 4, b.Triangles);
        // no ground: nothing stands
        Assert.Equal(0, new CellBorders(ring, (4, -3), (_, _) => null).Triangles);
    }

    [Fact]
    public void A_markers_arrow_points_the_way_it_faces()
    {
        static Vector3 Tip(float zRadians) => MarkerGizmos.MarkerLines(Vector3.Zero, new Vec3(0, 0, zRadians)).ElementAt(3).B;
        var north = Tip(0f);
        Assert.True(north.Y > 60 && MathF.Abs(north.X) < 1, $"unturned marker's arrow ends at {north}");
        var turned = Tip(MathF.PI / 2);
        Assert.True(turned.X > 60 && MathF.Abs(turned.Y) < 1, $"a marker turned to face east (90 degrees)'s arrow ends at {turned}");
        Assert.Equal(12, MarkerGizmos.DiamondLines(Vector3.Zero, 10f).Count());
        var g = new MarkerGizmos(new[] { (Boxes.Key(1), new Vec3(0, 0, 0), new Vec3(0, 0, 0), true), (Boxes.Key(2), new Vec3(9, 9, 9), new Vec3(0, 0, 0), false) });
        Assert.Equal(1, g.Markers); Assert.Equal(1, g.NoModel);
    }
}

/// <summary>The theme's toggle buttons drawn offscreen: a checked one is a dark blue with white text, an unchecked one the
/// button grey, both readable (before the theme styled them, a checked toggle was light grey-blue under white text).</summary>
[Collection("Shell"), Trait("tier", "render")]
public sealed class ThemeToggleTests
{
    static (double R, double G, double B) Px(byte[] px, int w, int x, int y) { int i = (y * w + x) * 4; return (px[i + 2], px[i + 1], px[i]); }
    static double Lum((double R, double G, double B) c)
    {
        static double L(double v) { v /= 255; return v <= 0.03928 ? v / 12.92 : Math.Pow((v + 0.055) / 1.055, 2.4); }
        return 0.2126 * L(c.R) + 0.7152 * L(c.G) + 0.0722 * L(c.B);
    }

    [Fact]
    public void A_checked_and_an_unchecked_toggle_both_read_white_text_on_a_dark_face()
    {
        Exception? failed = null; var found = new List<(bool Checked, double Contrast, (double, double, double) Face)>();
        ShellCollection.Ui.Value.Invoke(() =>
        {
            try
            {
                foreach (var on in new[] { true, false })
                {
                    var b = new System.Windows.Controls.Primitives.ToggleButton { Content = "Navmesh", IsChecked = on, Width = 120, Height = 30, FontSize = 14 };
                    var host = new System.Windows.Controls.Border { Width = 120, Height = 30, Child = b };
                    host.Resources.MergedDictionaries.Add(Theme.Dark);
                    host.Measure(new System.Windows.Size(120, 30)); host.Arrange(new System.Windows.Rect(0, 0, 120, 30)); host.UpdateLayout();
                    var bmp = new System.Windows.Media.Imaging.RenderTargetBitmap(120, 30, 96, 96, System.Windows.Media.PixelFormats.Pbgra32);
                    bmp.Render(host);
                    var px = new byte[120 * 30 * 4]; bmp.CopyPixels(px, 120 * 4, 0);
                    var face = Px(px, 120, 6, 15);                       // inside the border, left of the text
                    double brightest = 0;
                    for (int y = 6; y < 24; y++) for (int x = 20; x < 100; x++) brightest = Math.Max(brightest, Lum(Px(px, 120, x, y)));
                    found.Add((on, (brightest + 0.05) / (Lum(face) + 0.05), face));
                }
            }
            catch (Exception ex) { failed = ex; }
        });
        Assert.True(failed is null, "the toggle did not draw: " + failed);
        foreach (var (on, contrast, face) in found)
            Assert.True(contrast >= 4.5, $"a {(on ? "checked" : "unchecked")} toggle's text has contrast {contrast:0.0} on its face {face}");
        Assert.NotEqual(found[0].Face, found[1].Face);
    }
}
