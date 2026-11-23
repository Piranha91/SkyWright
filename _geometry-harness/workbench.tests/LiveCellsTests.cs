using System.Numerics;
using HousecarlCore;
using Mutagen.Bethesda.Plugins;
using ViewProbe;
using Workbench;
using Xunit;

namespace WorkbenchTests;

/// <summary>The live scene's cell bookkeeping (LiveCells.cs, viewprobe's LiveScene.cs and ProfileIni.cs): which cells a
/// change of the loaded square builds and frees, how the walk's prepared inputs split by cell, and where the square's
/// width comes from.</summary>
public class LiveCellsTests
{
    static HashSet<(int X, int Y)> Reach((int X, int Y) c, int r)
        => LiveCells.Square(c, 2 * r + 1).ToHashSet();

    [Fact]
    public void A_square_lists_its_cells_nearest_first()
    {
        var sq = LiveCells.Square((10, -3), 5);
        Assert.Equal(25, sq.Count);
        Assert.Equal((10, -3), sq[0]);
        Assert.All(sq.Take(9), c => Assert.True(Math.Abs(c.X - 10) <= 1 && Math.Abs(c.Y + 3) <= 1));
        Assert.Equal(25, sq.Distinct().Count());
    }

    static Dictionary<(int X, int Y), bool> Whole(IEnumerable<(int X, int Y)> cells) => cells.ToDictionary(c => c, _ => false);

    [Fact]
    public void Widening_builds_only_the_new_ring_and_narrowing_frees_only_the_outer_cells()
    {
        var reach = Reach((0, 0), 5);
        var held = Whole(LiveCells.Square((0, 0), 5));
        var (add, drop) = LiveCells.Plan((0, 0), 7, 0, held, reach);
        Assert.Empty(drop);
        Assert.Equal(24, add.Count);
        Assert.All(add, a => { Assert.Equal(3, Math.Max(Math.Abs(a.Cell.X), Math.Abs(a.Cell.Y))); Assert.False(a.LargeOnly); });

        (add, drop) = LiveCells.Plan((0, 0), 3, 0, held, reach);
        Assert.Empty(add);
        Assert.Equal(16, drop.Count);
        Assert.All(drop, c => Assert.Equal(2, Math.Max(Math.Abs(c.X), Math.Abs(c.Y))));
    }

    [Fact]
    public void The_far_grid_holds_large_references_beyond_the_loaded_square_and_a_cell_that_crosses_is_swapped()
    {
        var reach = Reach((0, 0), 5);
        var (add, drop) = LiveCells.Plan((0, 0), 5, 9, new Dictionary<(int X, int Y), bool>(), reach);
        Assert.Empty(drop);
        Assert.Equal(25, add.Count(a => !a.LargeOnly));
        Assert.Equal(81 - 25, add.Count(a => a.LargeOnly));
        Assert.All(add.Take(25), a => Assert.False(a.LargeOnly));      // the loaded cells come first
        var held = add.ToDictionary(a => a.Cell, a => a.LargeOnly);

        // widening the loaded square turns the far ring at distance 3 into whole cells: freed, then built whole
        (add, drop) = LiveCells.Plan((0, 0), 7, 9, held, reach);
        Assert.Equal(24, drop.Count);
        Assert.Equal(24, add.Count);
        Assert.All(add, a => Assert.False(a.LargeOnly));

        // switching the far grid off frees only the large-only cells
        (add, drop) = LiveCells.Plan((0, 0), 5, 0, held, reach);
        Assert.Empty(add);
        Assert.Equal(56, drop.Count);
        Assert.All(drop, c => Assert.True(held[c]));
    }

    [Fact]
    public void The_widest_square_is_what_the_prepared_cells_cover_and_wider_is_refused()
    {
        Assert.Equal(11, LiveCells.MaxSquare((4, 4), Reach((4, 4), 5)));
        var holed = Reach((0, 0), 5); holed.Remove((3, -3));
        Assert.Equal(5, LiveCells.MaxSquare((0, 0), holed));
        // cells outside what was prepared are not planned, so a hole is never "built"
        var (add, _) = LiveCells.Plan((0, 0), 7, 0, new Dictionary<(int X, int Y), bool>(), holed);
        Assert.DoesNotContain(add, a => a.Cell == (3, -3));
        Assert.Equal(48, add.Count);
    }

    static SceneRef At(uint id, float x, float y) =>
        new("meshes\\test.nif", new Vec3(x, y, 0), new Vec3(0, 0, 0), 1f, false, RefKey: new FormKey(ModKey.FromNameAndExtension("Test.esp"), id));

    [Fact]
    public void Prepared_inputs_split_by_the_cell_each_falls_in()
    {
        var refs = new List<SceneRef> { At(1, 100, 100), At(2, 4095, 10), At(3, 4096, 10), At(4, -1, -1) };
        var patches = new Dictionary<string, List<(Vector3, Vector3, Vector3)>>
        {
            ["a.dds"] = new() { (new(0, 0, 0), new(128, 0, 0), new(0, 128, 0)), (new(4096, 0, 0), new(4224, 0, 0), new(4096, 128, 0)) },
        };
        var layers = new List<(string Diffuse, List<(Vector3 A, Vector3 B, Vector3 C, float Oa, float Ob, float Oc)> Tris)>
        {
            ("b.dds", new() { (new(-128, -128, 0), new(-1, -128, 0), new(-128, -1, 0), 1, 1, 1) }),
        };
        var slicer = new LiveSlicer(refs, patches, layers, Array.Empty<WaterPlane>());

        var own = slicer.For(new[] { (0, 0) });
        Assert.Equal(new uint[] { 1, 2 }, own.Refs.Select(r => r.RefKey.ID).OrderBy(i => i));
        Assert.Single(own.Patches["a.dds"]);
        Assert.Empty(own.Layers);

        var east = slicer.For(new[] { (1, 0) });
        Assert.Equal(3u, Assert.Single(east.Refs).RefKey.ID);

        var sw = slicer.For(new[] { (-1, -1) });
        Assert.Equal(4u, Assert.Single(sw.Refs).RefKey.ID);
        Assert.Equal("b.dds", Assert.Single(sw.Layers).Diffuse);

        var both = slicer.For(new[] { (0, 0), (1, 0) });
        Assert.Equal(3, both.Refs.Count);
        Assert.Equal(2, both.Patches["a.dds"].Count);
    }

    [Fact]
    public void A_dropped_cell_frees_its_references_but_a_held_one_is_reported()
    {
        static RefOcclusion Drawn(SceneRef r) { var ro = new RefOcclusion { Ref = r }; ro.Meshes.Add(new SkyRender.GlMesh()); return ro; }
        var a = Drawn(At(1, 0, 0)); var b = Drawn(At(2, 5000, 0));
        var s = new EditSession(new[] { a });
        s.Add(new[] { b });
        Assert.True(s.IsDrawn(b.Ref.RefKey));
        s.Select(b.Ref.RefKey);
        Assert.True(s.Holds(b.Ref.RefKey));
        Assert.False(s.Holds(a.Ref.RefKey));
        s.Remove(new[] { a });
        Assert.False(s.IsDrawn(a.Ref.RefKey));
        Assert.True(s.IsDrawn(b.Ref.RefKey));
    }

    [Fact]
    public void The_loaded_square_reads_the_profile_and_falls_back_to_the_game_default()
    {
        var dir = Directory.CreateTempSubdirectory("live-ini").FullName;
        var was = GrassLayer.ProfileDir;
        try
        {
            GrassLayer.ProfileDir = dir;
            Assert.Equal(5, ProfileIni.Int("General", "uGridsToLoad", 5, out var why));
            Assert.Contains("the game's default 5", why);

            File.WriteAllText(Path.Combine(dir, "Skyrim.ini"), "[General]\nuGridsToLoad=7\n[Display]\nuGridsToLoad=99\n");
            Assert.Equal(7, ProfileIni.Int("General", "uGridsToLoad", 5, out why));
            Assert.Equal("uGridsToLoad=7 from Skyrim.ini", why);

            // SkyrimPrefs.ini is read after Skyrim.ini, so it wins
            File.WriteAllText(Path.Combine(dir, "SkyrimPrefs.ini"), "[General]\nuGridsToLoad = 9\n");
            Assert.Equal(9, ProfileIni.Int("General", "uGridsToLoad", 5, out why));
            Assert.Equal("uGridsToLoad=9 from SkyrimPrefs.ini", why);
        }
        finally { GrassLayer.ProfileDir = was; Directory.Delete(dir, true); }
    }
}
