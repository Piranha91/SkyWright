using System.Windows.Media.Imaging;
using Workbench;
using Xunit;

namespace WorkbenchTests;

/// <summary>The World tab's textured tiles: the fixed grid they sit on (world rectangle to grid square and back, blocks,
/// north up, +X east and +Y north), the picture's orientation as viewprobe writes it, and the cache key that keeps a
/// changed load order from showing old tiles.</summary>
[Trait("tier", "unit")]
public class MapTileTests
{
    [Theory]
    [InlineData(0.0, 0)]
    [InlineData(4095.5, 0)]
    [InlineData(4096.0, 1)]
    [InlineData(-0.5, -1)]
    [InlineData(-4096.0, -1)]
    [InlineData(-4096.5, -2)]
    public void A_world_coordinate_falls_in_the_square_west_or_south_of_it(double world, int cell)
        => Assert.Equal(cell, MapTileGrid.CellOf(world));

    [Fact]
    public void A_tile_is_one_cell_of_the_world()
        => Assert.Equal((-12288.0, 8192.0, -8192.0, 12288.0), MapTileGrid.RectOf(-3, 2));

    [Fact]
    public void A_tile_rectangle_maps_back_to_its_own_square_and_no_neighbour()
    {
        for (int gy = -4; gy <= 4; gy++)
            for (int gx = -4; gx <= 4; gx++)
            {
                var (w, s, e, n) = MapTileGrid.RectOf(gx, gy);
                Assert.Equal((gx, gy, gx, gy), MapTileGrid.CellsIn(w, s, e, n));
                Assert.Equal((gx, gy), (MapTileGrid.CellOf((w + e) / 2), MapTileGrid.CellOf((s + n) / 2)));
            }
    }

    [Fact]
    public void A_view_across_the_origin_takes_the_four_squares_it_touches()
        => Assert.Equal((-1, -1, 0, 0), MapTileGrid.CellsIn(-100, -100, 100, 100));

    [Theory]
    [InlineData(0, 0, 0, 0)]
    [InlineData(4, 4, 0, 0)]
    [InlineData(5, 0, 1, 0)]
    [InlineData(-1, -1, -1, -1)]
    [InlineData(-5, -6, -1, -2)]
    [InlineData(-6, 9, -2, 1)]
    public void A_square_belongs_to_one_block_of_five_by_five(int gx, int gy, int bx, int by)
    {
        Assert.Equal((bx, by), MapTileGrid.BlockOf(gx, gy));
        var (x0, y0) = MapTileGrid.BlockOrigin(bx, by);
        Assert.InRange(gx, x0, x0 + MapTileGrid.BlockCells - 1);
        Assert.InRange(gy, y0, y0 + MapTileGrid.BlockCells - 1);
    }

    [Fact]
    public void A_block_is_built_around_its_middle_cell_with_ring_two()
        => Assert.Equal((-3, 7, 2), MapTileGrid.TargetOf(-1, 1, (_, _) => true));

    [Fact]
    public void A_block_without_its_middle_cell_is_built_from_the_nearest_cell_with_the_ring_that_still_covers_it()
    {
        // block (0, 0) is squares 0..4; only (3, 2) and (0, 0) have cells: (3, 2) needs ring 3, the corner ring 4
        Assert.Equal((3, 2, 3), MapTileGrid.TargetOf(0, 0, (x, y) => (x, y) is (3, 2) or (0, 0)));
        Assert.Equal((0, 0, 4), MapTileGrid.TargetOf(0, 0, (x, y) => (x, y) is (0, 0)));
        Assert.Null(MapTileGrid.TargetOf(0, 0, (_, _) => false));
    }

    [Fact]
    public void On_the_sheet_north_is_up_and_east_is_right()
    {
        // a terrain grid whose west column is -2 and north row is 3, 32 sheet pixels a cell
        Assert.Equal((0.0, 0.0), MapTileGrid.SheetPlace(-2, 3, -2, 3, 32));
        Assert.Equal((64.0, 0.0), MapTileGrid.SheetPlace(0, 3, -2, 3, 32));     // two cells east: right
        Assert.Equal((0.0, 64.0), MapTileGrid.SheetPlace(-2, 1, -2, 3, 32));    // two cells south: down
        var (_, north) = MapTileGrid.SheetPlace(5, 6, -2, 9, 32);
        var (_, south) = MapTileGrid.SheetPlace(5, 5, -2, 9, 32);
        Assert.Equal(south - 32, north);
    }

    [Theory]
    [InlineData(100, 100, null)]
    [InlineData(128, 128, 16)]
    [InlineData(200, 256, 16)]
    [InlineData(200, 300, 8)]
    public void Tiles_show_from_a_zoom_at_the_scale_that_matches_the_screen(double screen, double device, int? scale)
        => Assert.Equal(scale, MapTileGrid.ScaleFor(screen, device));

    [Fact]
    public void A_tile_has_whole_pixels_at_every_scale()
    {
        foreach (var s in MapTileGrid.Scales) Assert.Equal(0, MapTileGrid.CellUnits % s);
        Assert.Equal(256, MapTileGrid.TilePixels(16));
    }

    [Fact]
    public void A_tile_picture_has_north_at_the_top_and_straight_alpha()
    {
        // GL's rows run bottom-up: row 0 is the rectangle's south edge. South red, north blue, one half-covered pixel.
        byte[] gl =
        {
            255, 0, 0, 255,   64, 0, 0, 128,       // south row: red, and red premultiplied by half coverage
            0, 0, 255, 255,   0, 0, 255, 255,      // north row: blue
        };
        var dir = Directory.CreateTempSubdirectory("hc-maptile-");
        try
        {
            var file = Path.Combine(dir.FullName, "t.png");
            ViewProbe.MapTiles.Save(gl, 2, 2, file);
            var frame = BitmapDecoder.Create(new Uri(file), BitmapCreateOptions.None, BitmapCacheOption.OnLoad).Frames[0];
            var bgra = new FormatConvertedBitmap(frame, System.Windows.Media.PixelFormats.Bgra32, null, 0);
            var px = new byte[16];
            bgra.CopyPixels(px, 8, 0);
            Assert.Equal(new byte[] { 255, 0, 0, 255 }, px[0..4]);              // top-left: blue (B, G, R, A)
            Assert.Equal(new byte[] { 0, 0, 255, 255 }, px[8..12]);             // bottom-left: red
            Assert.Equal(new byte[] { 0, 0, 128, 128 }, px[12..16]);            // half-covered red, straight alpha
        }
        finally { dir.Delete(true); }
    }

    [Fact]
    public void A_tile_request_with_an_empty_rectangle_is_refused()
    {
        var dir = Directory.CreateTempSubdirectory("hc-maptile-");
        try
        {
            var ok = Path.Combine(dir.FullName, "ok.json");
            File.WriteAllText(ok, """{"tiles":[{"west":0,"south":0,"east":4096,"north":4096,"unitsPerPixel":16,"out":"a.png"}]}""");
            var r = ViewProbe.MapTiles.Read(ok);
            Assert.Equal((0f, 0f, 4096f, 4096f, 16f), (r.Tiles[0].West, r.Tiles[0].South, r.Tiles[0].East, r.Tiles[0].North, r.Tiles[0].UnitsPerPixel));
            Assert.True(r.Ceiling > r.Floor);
            var bad = Path.Combine(dir.FullName, "bad.json");
            File.WriteAllText(bad, """{"tiles":[{"west":10,"south":0,"east":10,"north":4096,"unitsPerPixel":16,"out":"a.png"}]}""");
            Assert.Throws<InvalidDataException>(() => ViewProbe.MapTiles.Read(bad));
        }
        finally { dir.Delete(true); }
    }

    static readonly (string, long, long)[] Order =
    {
        (@"C:\Game\Data\Skyrim.esm", 250_000_000, 637_000_000_000_000_000),
        (@"C:\MO2\mods\Town\Town.esp", 1_200, 638_000_000_000_000_000),
    };

    [Fact]
    public void The_cache_key_is_stable_for_the_same_load_order()
    {
        var copy = Order.Select(p => (p.Item1, p.Item2, p.Item3)).ToArray();
        Assert.Equal(MapTileCache.OrderKey(Order, "SkyRender 4.0.0", "mods"), MapTileCache.OrderKey(copy, "SkyRender 4.0.0", "mods"));
        // the same file named with other slashes or case is the same file
        var spelled = Order.Select(p => (p.Item1.ToUpperInvariant().Replace('\\', '/'), p.Item2, p.Item3)).ToArray();
        Assert.Equal(MapTileCache.OrderKey(Order, "SkyRender 4.0.0", "mods"), MapTileCache.OrderKey(spelled, "SkyRender 4.0.0", "mods"));
    }

    [Fact]
    public void The_cache_key_changes_when_a_plugin_its_order_the_renderer_or_the_mod_list_changes()
    {
        string k = MapTileCache.OrderKey(Order, "SkyRender 4.0.0", "mods");
        var bigger = Order.ToArray(); bigger[1] = (bigger[1].Item1, bigger[1].Item2 + 1, bigger[1].Item3);
        var touched = Order.ToArray(); touched[1] = (touched[1].Item1, touched[1].Item2, touched[1].Item3 + 1);
        var swapped = new[] { Order[1], Order[0] };
        Assert.NotEqual(k, MapTileCache.OrderKey(bigger, "SkyRender 4.0.0", "mods"));
        Assert.NotEqual(k, MapTileCache.OrderKey(touched, "SkyRender 4.0.0", "mods"));
        Assert.NotEqual(k, MapTileCache.OrderKey(swapped, "SkyRender 4.0.0", "mods"));
        Assert.NotEqual(k, MapTileCache.OrderKey(Order, "SkyRender 4.0.1", "mods"));
        Assert.NotEqual(k, MapTileCache.OrderKey(Order, "SkyRender 4.0.0", "mods changed"));
    }

    [Fact]
    public void A_plugin_file_rewritten_with_a_new_time_or_size_changes_the_key()
    {
        var dir = Directory.CreateTempSubdirectory("hc-maptile-");
        try
        {
            var f = Path.Combine(dir.FullName, "A.esp");
            File.WriteAllBytes(f, new byte[100]);
            File.SetLastWriteTimeUtc(f, new DateTime(2025, 1, 1, 0, 0, 0, DateTimeKind.Utc));
            string Key() => MapTileCache.OrderKey(MapTileCache.Identity(new[] { f }), "r", null);
            string first = Key();
            Assert.Equal(first, Key());
            File.SetLastWriteTimeUtc(f, new DateTime(2025, 1, 2, 0, 0, 0, DateTimeKind.Utc));
            string later = Key();
            Assert.NotEqual(first, later);
            File.WriteAllBytes(f, new byte[101]);
            File.SetLastWriteTimeUtc(f, new DateTime(2025, 1, 2, 0, 0, 0, DateTimeKind.Utc));
            Assert.NotEqual(later, Key());
            File.Delete(f);
            Assert.Equal(-1, MapTileCache.Identity(new[] { f })[0].Size);
        }
        finally { dir.Delete(true); }
    }

    [Fact]
    public void A_tile_file_is_named_by_worldspace_scale_and_square()
    {
        var p = MapTileCache.TilePath(@"C:\cache\abc", "00003C:Skyrim.esm", 16, -3, 7);
        Assert.Equal(Path.Combine(@"C:\cache\abc", "00003C_Skyrim.esm", "16", "-3_7.png"), p);
    }
}
