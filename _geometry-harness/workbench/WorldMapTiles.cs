using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace Workbench;

/// <summary>The fixed grid the World tab's textured tiles sit on: one tile is one cell (4,096 world units a side) at one
/// of <see cref="Scales"/>, so tiles of any visit join without seams and can be kept on disk. One scene build draws a
/// block of <see cref="BlockCells"/> by <see cref="BlockCells"/> cells. +X is east and +Y north; a tile's picture has
/// north at the top.</summary>
internal static class MapTileGrid
{
    internal const int CellUnits = 4096;
    internal const int BlockCells = 5;
    /// <summary>World units a pixel: 256- and 512-pixel tiles. Every block is drawn at both.</summary>
    internal static readonly int[] Scales = { 16, 8 };
    /// <summary>Screen pixels across one cell from which tiles are shown: the zoom at which buildings get their real
    /// shapes, so the textured view takes over as soon as the detailed models do.</summary>
    internal const double TilesAppear = WorldMap.ShapesAppear;

    /// <summary>The scale to show at a zoom, or null when the view is too far out for tiles.</summary>
    internal static int? ScaleFor(double screenPixelsPerCell, double devicePixelsPerCell) =>
        screenPixelsPerCell < TilesAppear ? null : devicePixelsPerCell > CellUnits / Scales[0] ? Scales[1] : Scales[0];

    internal static int TilePixels(int scale) => CellUnits / scale;

    /// <summary>The grid square a world coordinate falls in.</summary>
    internal static int CellOf(double world) => (int)Math.Floor(world / CellUnits);

    /// <summary>A tile's world rectangle.</summary>
    internal static (double West, double South, double East, double North) RectOf(int gx, int gy) =>
        ((double)gx * CellUnits, (double)gy * CellUnits, (gx + 1.0) * CellUnits, (gy + 1.0) * CellUnits);

    /// <summary>The grid squares a world rectangle touches, inclusive; an edge on a cell border does not take the next cell.</summary>
    internal static (int X0, int Y0, int X1, int Y1) CellsIn(double west, double south, double east, double north) =>
        (CellOf(west), CellOf(south), (int)Math.Ceiling(east / CellUnits) - 1, (int)Math.Ceiling(north / CellUnits) - 1);

    /// <summary>The block a grid square belongs to.</summary>
    internal static (int Bx, int By) BlockOf(int gx, int gy) => ((int)Math.Floor(gx / (double)BlockCells), (int)Math.Floor(gy / (double)BlockCells));

    /// <summary>A block's south-west grid square.</summary>
    internal static (int X0, int Y0) BlockOrigin(int bx, int by) => (bx * BlockCells, by * BlockCells);

    /// <summary>The cell a block's scene is built around and the ring that reaches the whole block from it: the middle
    /// cell with ring 2, or, when the middle square has no cell, the cell nearest the middle with the smallest ring that
    /// still covers the block. Null when no square of the block has a cell.</summary>
    internal static (int X, int Y, int Ring)? TargetOf(int bx, int by, Func<int, int, bool> hasCell)
    {
        var (x0, y0) = BlockOrigin(bx, by);
        int half = BlockCells / 2;
        if (hasCell(x0 + half, y0 + half)) return (x0 + half, y0 + half, half);
        (int X, int Y, int Ring)? best = null;
        for (int y = y0; y < y0 + BlockCells; y++)
            for (int x = x0; x < x0 + BlockCells; x++)
            {
                if (!hasCell(x, y)) continue;
                int ring = Math.Max(Math.Max(x - x0, x0 + BlockCells - 1 - x), Math.Max(y - y0, y0 + BlockCells - 1 - y));
                if (best is null || ring < best.Value.Ring) best = (x, y, ring);
            }
        return best;
    }

    /// <summary>Where a tile sits on the map's sheet: its top-left corner in sheet pixels, for a terrain grid whose
    /// west column is <paramref name="minX"/>, whose north row is <paramref name="maxY"/> and whose cell is
    /// <paramref name="samples"/> sheet pixels. North is up, so a tile one cell north sits one cell higher.</summary>
    internal static (double Left, double Top) SheetPlace(int gx, int gy, int minX, int maxY, int samples) =>
        ((gx - minX) * (double)samples, (maxY - gy) * (double)samples);
}

/// <summary>Where tiles are kept: one folder per load order, named by a digest of its identity (each plugin's path,
/// size and last-write time in load order, the profile's mod list, the renderer's version and this tile format), then
/// the worldspace, the scale and the grid square. A changed load order gets a new folder, so it never shows stale tiles.</summary>
internal static class MapTileCache
{
    /// <summary>Bump when what a tile shows changes for the same load order.</summary>
    internal const string Format = "map-tiles-1";

    internal static string Root =>
        Environment.GetEnvironmentVariable("WB_MAP_TILES") is { Length: > 0 } p ? p
        : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "houseCARL", "workbench", "map-tiles");

    /// <summary>The renderer viewprobe draws tiles with, as its package names its version.</summary>
    internal static string RendererVersion
    {
        get
        {
            var a = typeof(SkyRender.GlRenderer).Assembly;
            return "SkyRender " + (a.GetCustomAttributes(typeof(System.Reflection.AssemblyInformationalVersionAttribute), false)
                                     .OfType<System.Reflection.AssemblyInformationalVersionAttribute>().FirstOrDefault()?.InformationalVersion
                                   ?? a.GetName().Version?.ToString() ?? "unknown");
        }
    }

    /// <summary>Each plugin file's path, size and last-write time (UTC ticks); a missing file has size -1.</summary>
    internal static IReadOnlyList<(string Path, long Size, long WriteTicks)> Identity(IEnumerable<string> pluginPaths) =>
        pluginPaths.Select(p => { var f = new FileInfo(p); return f.Exists ? (p, f.Length, f.LastWriteTimeUtc.Ticks) : (p, -1L, 0L); }).ToList();

    /// <summary>The digest naming a load order's tile folder.</summary>
    internal static string OrderKey(IEnumerable<(string Path, long Size, long WriteTicks)> plugins, string renderer, string? extra)
    {
        var sb = new StringBuilder();
        sb.Append(Format).Append('\n').Append(renderer).Append('\n').Append(extra ?? "").Append('\n');
        foreach (var (path, size, ticks) in plugins)
            sb.Append(path.Replace('/', '\\').ToLowerInvariant()).Append('|').Append(size.ToString(CultureInfo.InvariantCulture))
              .Append('|').Append(ticks.ToString(CultureInfo.InvariantCulture)).Append('\n');
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(sb.ToString())))[..20].ToLowerInvariant();
    }

    /// <summary>A tile's file under a load order's folder.</summary>
    internal static string TilePath(string orderDir, string worldKey, int scale, int gx, int gy) =>
        Path.Combine(orderDir, Safe(worldKey), scale.ToString(CultureInfo.InvariantCulture), FormattableString.Invariant($"{gx}_{gy}.png"));

    static string Safe(string s)
    {
        var bad = Path.GetInvalidFileNameChars();
        return new string(s.Select(c => c == ':' || bad.Contains(c) ? '_' : c).ToArray());
    }

    /// <summary>A load order's tile folder, made if missing, with a note naming what it was made for. Slow (it reads
    /// every plugin's file times); run it off the window's thread.</summary>
    internal static string FolderFor(LoadOrderSpec order)
    {
        var paths = LoadOrderIndexes.PathsOf(order);
        var modlist = Path.Combine(order.Instance, "profiles", order.Profile, "modlist.txt");
        string extra = (File.Exists(modlist) ? File.ReadAllText(modlist) : "") + "\n" + (order.OrderFile is { } f && File.Exists(f) ? File.ReadAllText(f) : "");
        var id = Identity(paths);
        var renderer = RendererVersion;
        var dir = Path.Combine(Root, OrderKey(id, renderer, extra));
        Directory.CreateDirectory(dir);
        var note = Path.Combine(dir, "order.txt");
        if (!File.Exists(note))
            File.WriteAllLines(note, new[] { $"{order.Label}; {renderer}; {Format}", "the mod list and order file are part of the key too" }
                .Concat(id.Select(p => FormattableString.Invariant($"{p.Path}\t{p.Size}\t{new DateTime(p.WriteTicks, DateTimeKind.Utc):O}"))));
        return dir;
    }
}

/// <summary>Whether the map draws textured tiles, remembered beside the workbench's settings file.</summary>
internal static class MapSettings
{
    static string PathOf => Path.Combine(Path.GetDirectoryName(Path.GetFullPath(LauncherSettings.DefaultPath))!, "workbench-map.json");

    internal static bool Textured
    {
        get
        {
            try { return !File.Exists(PathOf) || JsonDocument.Parse(File.ReadAllText(PathOf)).RootElement.GetProperty("textured").GetBoolean(); }
            catch (Exception ex) when (ex is IOException or JsonException or KeyNotFoundException or InvalidOperationException or UnauthorizedAccessException) { return true; }
        }
        set
        {
            try { File.WriteAllText(PathOf, JsonSerializer.Serialize(new { textured = value })); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { WorkbenchHost.Say($"the map's Textured choice is not remembered: {ex.Message}"); }
        }
    }
}

/// <summary>The World tab map's textured tiles: pictures of the scene from straight above, drawn by a viewprobe child
/// process (its own process and GL context, so the window and the 3D view are never held), one block of cells per run,
/// kept on disk per load order (<see cref="MapTileCache"/>) and laid on the map's sheet over the shaded shapes, which
/// show wherever no tile is ready. Tiles are asked for only for the cells in view, nearest the middle first, when the
/// map is zoomed in to <see cref="MapTileGrid.TilesAppear"/>. Everything here runs on the window's thread except the
/// child and the file reads.</summary>
internal sealed class MapTileLayer
{
    const int KeepPictures = 256;
    internal readonly Canvas Canvas = new() { IsHitTestVisible = false };
    readonly Dictionary<(int Scale, int X, int Y), BitmapSource> _pictures = new();
    readonly LinkedList<(int Scale, int X, int Y)> _recent = new();
    readonly HashSet<(int Scale, int X, int Y)> _reading = new();
    readonly Dictionary<(int X, int Y), (Image Image, int Scale)> _shown = new();
    readonly Dictionary<(int Bx, int By), string> _failed = new();
    readonly HashSet<(int Bx, int By)> _empty = new();      // blocks with no cell of the worldspace: nothing to draw
    readonly List<(int Bx, int By)> _queue = new();
    LoadOrderSpec? _order;
    string? _orderDir, _worldKey, _stopped;
    Func<int, int, string?>? _cellKeyAt;
    WorldTerrain? _terrain;
    int _gen, _runId, _scale, _inView, _misses;
    (int X0, int Y0, int X1, int Y1)? _view;
    (int Bx, int By, int X, int Y, int Ring)? _running;
    Process? _child;
    readonly Stopwatch _runClock = new();
    readonly Dispatcher _ui = Dispatcher.CurrentDispatcher;

    /// <summary>Raised when what the layer says or shows changed.</summary>
    internal event Action? Changed;
    internal bool Enabled { get; private set; } = true;
    /// <summary>One plain sentence on the tiles: what is shown, what is being drawn, or why none can be.</summary>
    internal string Status { get; private set; } = "";
    /// <summary>The last block drawn: seconds to its first tile (reading the load order and building the scene), its
    /// whole run, its tiles, and the mean milliseconds viewprobe took per tile to draw and to write.</summary>
    internal (double FirstTileSeconds, double Seconds, int Tiles, double DrawMs, double WriteMs, int Bx, int By)? LastBlock { get; private set; }
    /// <summary>Tiles read from disk this session and their mean read time in milliseconds.</summary>
    internal (int Count, double MeanMs) DiskReads => (_diskReads, _diskReads == 0 ? 0 : _diskMs / _diskReads);
    int _diskReads; double _diskMs;
    double _firstTile, _drawMs, _writeMs; int _runTiles;

    internal MapTileLayer()
    {
        RenderOptions.SetEdgeMode(Canvas, EdgeMode.Aliased);   // neighbouring tiles meet without a soft seam
        AppDomain.CurrentDomain.ProcessExit += (_, _) => Kill();
    }

    /// <summary>Why tiles cannot be drawn by this build at all, or null.</summary>
    internal static string? CannotDraw =>
        ViewProbe.MapTiles.CannotDraw is { } why ? why
        : !File.Exists(Path.Combine(AppContext.BaseDirectory, "viewprobe.exe")) ? "viewprobe.exe is not beside the workbench; rebuild the workbench." : null;

    /// <summary>Switch tiles on or off; off hides them and stops any drawing.</summary>
    internal void SetEnabled(bool on)
    {
        Enabled = on;
        if (!on) { Kill(); _queue.Clear(); Canvas.Visibility = Visibility.Collapsed; }
        Say();
    }

    /// <summary>A load order opened (null: none). <paramref name="orderDir"/> is its tile folder, or null with
    /// <paramref name="why"/> saying why there is none.</summary>
    internal void SetOrder(LoadOrderSpec? order, string? orderDir, string? why)
    {
        Reset();
        _order = order; _orderDir = orderDir;
        _stopped = order is null ? null : CannotDraw ?? (orderDir is null ? why ?? "the tile folder could not be made." : null);
        _failed.Clear(); _misses = 0;
        Say();
    }

    /// <summary>The map shows a worldspace (null: none): tiles of another one are taken down.</summary>
    internal void SetWorld(string? worldKey, WorldTerrain? terrain, Func<int, int, string?>? cellKeyAt)
    {
        Reset();
        _worldKey = worldKey; _terrain = terrain; _cellKeyAt = cellKeyAt;
        _failed.Clear(); _empty.Clear();
        Say();
    }

    void Reset()
    {
        _gen++;
        Kill();
        _queue.Clear(); _pictures.Clear(); _recent.Clear(); _reading.Clear(); _shown.Clear(); Canvas.Children.Clear();
        _view = null; _inView = 0;
    }

    /// <summary>The view came to rest: show the tiles of the cells in it at the zoom's scale, reading them from disk or
    /// asking for the blocks that are not drawn yet. Below the zoom tiles appear at, they are hidden.</summary>
    internal void Update(double west, double south, double east, double north, double screenPixelsPerCell, double devicePixelsPerCell)
    {
        var scale = MapTileGrid.ScaleFor(screenPixelsPerCell, devicePixelsPerCell);
        if (!Enabled || scale is not { } s || _terrain is not { } t || _worldKey is null || _orderDir is null || _order is null)
        {
            Canvas.Visibility = Visibility.Collapsed; _view = null;
            _queue.Clear();
            Say(scale is null);
            return;
        }
        Canvas.Visibility = Visibility.Visible;
        _scale = s;
        var (x0, y0, x1, y1) = MapTileGrid.CellsIn(west, south, east, north);
        // only the squares the terrain grid has: the world's own extent
        x0 = Math.Max(x0, t.MinX); y0 = Math.Max(y0, t.MinY); x1 = Math.Min(x1, t.MinX + t.Width - 1); y1 = Math.Min(y1, t.MaxY);
        _view = (x0, y0, x1, y1);
        _inView = Math.Max(0, x1 - x0 + 1) * Math.Max(0, y1 - y0 + 1);
        // take down what left the view
        foreach (var k in _shown.Keys.Where(k => k.X < x0 - 1 || k.X > x1 + 1 || k.Y < y0 - 1 || k.Y > y1 + 1).ToList())
        { Canvas.Children.Remove(_shown[k].Image); _shown.Remove(k); }
        var want = new List<(int Bx, int By, double D)>();
        double cx = (x0 + x1) / 2.0, cy = (y0 + y1) / 2.0;
        for (int y = y0; y <= y1; y++)
            for (int x = x0; x <= x1; x++)
            {
                if (Place(x, y)) continue;
                if (File.Exists(MapTileCache.TilePath(_orderDir, _worldKey, s, x, y))) { Read(s, x, y); continue; }
                var other = MapTileGrid.Scales.FirstOrDefault(o => o != s && File.Exists(MapTileCache.TilePath(_orderDir, _worldKey, o, x, y)));
                if (other != 0) Read(other, x, y);         // the other scale stands in until this one is drawn
                var b = MapTileGrid.BlockOf(x, y);
                if (_failed.ContainsKey(b) || _empty.Contains(b) || (_running is { } r && (r.Bx, r.By) == b)) continue;
                double d = (x - cx) * (x - cx) + (y - cy) * (y - cy);
                int at = want.FindIndex(w => (w.Bx, w.By) == b);
                if (at < 0) want.Add((b.Bx, b.By, d)); else if (d < want[at].D) want[at] = (b.Bx, b.By, d);
            }
        _queue.Clear();
        if (_stopped is null) _queue.AddRange(want.OrderBy(w => w.D).Select(w => (w.Bx, w.By)));
        StartNext();
        Say();
    }

    /// <summary>Show the best picture held for a square; true when it is at the scale wanted.</summary>
    bool Place(int x, int y)
    {
        if (_terrain is not { } t) return false;
        int pick = _pictures.ContainsKey((_scale, x, y)) ? _scale : MapTileGrid.Scales.FirstOrDefault(o => _pictures.ContainsKey((o, x, y)));
        if (pick == 0) return false;
        if (_shown.TryGetValue((x, y), out var have) && have.Scale == pick) { Touch((pick, x, y)); return pick == _scale; }
        if (have.Image is { } old) Canvas.Children.Remove(old);
        var img = new Image { Source = _pictures[(pick, x, y)], Width = t.Samples, Height = t.Samples, Stretch = Stretch.Fill };
        RenderOptions.SetBitmapScalingMode(img, BitmapScalingMode.Linear);
        var (left, top) = MapTileGrid.SheetPlace(x, y, t.MinX, t.MaxY, t.Samples);
        Canvas.SetLeft(img, left); Canvas.SetTop(img, top);
        Canvas.Children.Add(img);
        _shown[(x, y)] = (img, pick);
        Touch((pick, x, y));
        return pick == _scale;
    }

    // the most recently used pictures are kept; the oldest not on the map is let go
    void Touch((int Scale, int X, int Y) k)
    {
        _recent.Remove(k); _recent.AddLast(k);
        while (_pictures.Count > KeepPictures)
        {
            var node = _recent.First;
            while (node is not null && _shown.TryGetValue((node.Value.X, node.Value.Y), out var on) && on.Scale == node.Value.Scale) node = node.Next;
            if (node is null) break;
            _pictures.Remove(node.Value); _recent.Remove(node);
        }
    }

    /// <summary>Read a tile from disk off the window's thread and show it if the view still wants it.</summary>
    void Read(int scale, int x, int y)
    {
        if (_orderDir is null || _worldKey is null || !_reading.Add((scale, x, y))) return;
        var path = MapTileCache.TilePath(_orderDir, _worldKey, scale, x, y);
        int gen = _gen;
        Task.Run(() =>
        {
            var clock = Stopwatch.StartNew();
            var bmp = new BitmapImage();
            bmp.BeginInit();
            bmp.CacheOption = BitmapCacheOption.OnLoad;
            bmp.CreateOptions = BitmapCreateOptions.IgnoreColorProfile;
            bmp.UriSource = new Uri(path);
            bmp.EndInit();
            bmp.Freeze();
            return (Picture: (BitmapSource)bmp, Ms: clock.Elapsed.TotalMilliseconds);
        }).ContinueWith(done => _ui.BeginInvoke(() =>
        {
            if (gen != _gen) return;
            _reading.Remove((scale, x, y));
            if (!done.IsCompletedSuccessfully) return;     // a tile being replaced as it was read: the next view reads it again
            _pictures[(scale, x, y)] = done.Result.Picture;
            Touch((scale, x, y));
            _diskReads++; _diskMs += done.Result.Ms;
            if (_view is { } v && x >= v.X0 - 1 && x <= v.X1 + 1 && y >= v.Y0 - 1 && y <= v.Y1 + 1) Place(x, y);
            Say();
        }), TaskScheduler.Default);
    }

    /// <summary>Start drawing the next block asked for, when no child is running.</summary>
    void StartNext()
    {
        if (_child is not null || _stopped is not null || _order is not { } order || _orderDir is not { } dir || _worldKey is not { } world || _cellKeyAt is not { } cellAt) return;
        while (_queue.Count > 0)
        {
            var (bx, by) = _queue[0]; _queue.RemoveAt(0);
            if (MapTileGrid.TargetOf(bx, by, (x, y) => cellAt(x, y) is not null) is not { } target)
            { _empty.Add((bx, by)); continue; }
            var (ox, oy) = MapTileGrid.BlockOrigin(bx, by);
            var tiles = new List<object>();
            for (int y = oy; y < oy + MapTileGrid.BlockCells; y++)
                for (int x = ox; x < ox + MapTileGrid.BlockCells; x++)
                    foreach (var s in MapTileGrid.Scales)
                    {
                        var path = MapTileCache.TilePath(dir, world, s, x, y);
                        if (File.Exists(path)) continue;
                        var (w, so, e, n) = MapTileGrid.RectOf(x, y);
                        tiles.Add(new { west = w, south = so, east = e, north = n, unitsPerPixel = s, @out = path });
                    }
            if (tiles.Count == 0) continue;
            var runDir = Path.Combine(dir, "runs");
            Directory.CreateDirectory(runDir);
            var req = Path.Combine(runDir, FormattableString.Invariant($"block_{bx}_{by}.json"));
            File.WriteAllText(req, JsonSerializer.Serialize(new { tiles }));
            if (Start(order, cellAt(target.X, target.Y)!, target.Ring, (target.X + 0.5) * MapTileGrid.CellUnits, (target.Y + 0.5) * MapTileGrid.CellUnits, req, runDir))
                _running = (bx, by, target.X, target.Y, target.Ring);
            return;
        }
    }

    bool Start(LoadOrderSpec order, string cellKey, int ring, double cx, double cy, string req, string runDir)
    {
        var psi = new ProcessStartInfo(Path.Combine(AppContext.BaseDirectory, "viewprobe.exe"))
        {
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true,
            WorkingDirectory = runDir,
        };
        string F(double v) => v.ToString("0", CultureInfo.InvariantCulture);
        foreach (var a in new[] { cellKey, "--ring", ring.ToString(CultureInfo.InvariantCulture) }.Concat(order.Args()).Concat(new[]
                 {
                     "--no-nav", "--no-grass", "--no-weather", "--no-cell-outline", "--report-mode", "--size", "64",
                     "--eye", $"{F(cx)},{F(cy)},60000", "--look", $"{F(cx)},{F(cy + 10)},0", "--map-tiles", req, "--out", runDir,
                 }))
            psi.ArgumentList.Add(a);
        int gen = _gen, run = ++_runId;
        var log = new List<string>();
        var errors = new List<string>();
        _runClock.Restart(); _firstTile = 0; _drawMs = _writeMs = 0; _runTiles = 0;
        var p = new Process { StartInfo = psi, EnableRaisingEvents = true };
        p.OutputDataReceived += (_, e) =>
        {
            if (e.Data is not { } line) return;
            lock (log) log.Add(line);
            int at = line.IndexOf("map tile: ", StringComparison.Ordinal);
            if (at < 0) return;
            var rest = line[(at + 10)..];
            int end = rest.IndexOf("  X ", StringComparison.Ordinal);
            var path = end > 0 ? rest[..end] : rest;
            double Num(string after) { int i = line.IndexOf(after, StringComparison.Ordinal); if (i < 0) return 0; i += after.Length; int j = line.IndexOf(' ', i); return double.TryParse(line[i..(j < 0 ? line.Length : j)], NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : 0; }
            double draw = Num("drawn in "), write = Num("written in ");
            _ui.BeginInvoke(() => { if (gen == _gen) Arrived(path, draw, write); });
        };
        p.ErrorDataReceived += (_, e) => { if (e.Data is { Length: > 0 } line) lock (errors) errors.Add(line); };
        p.Exited += (_, _) =>
        {
            try { p.WaitForExit(); } catch (InvalidOperationException) { }   // the last output lines are read before the end is handled
            int code; try { code = p.ExitCode; } catch (InvalidOperationException) { code = -1; }
            try { File.WriteAllLines(Path.Combine(runDir, "last-run.log"), log.Concat(errors.Select(e => "ERR " + e))); } catch (IOException) { }
            string? why;
            lock (errors) why = errors.LastOrDefault(e => !string.IsNullOrWhiteSpace(e));
            lock (log) why ??= log.LastOrDefault(l => l.Contains("not found", StringComparison.OrdinalIgnoreCase) || l.Contains("refus", StringComparison.OrdinalIgnoreCase));
            _ui.BeginInvoke(() => { if (gen == _gen && run == _runId) Ended(code, why?.Trim()); });
        };
        try
        {
            p.Start();
            p.BeginOutputReadLine(); p.BeginErrorReadLine();
            _child = p;
            return true;
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            _stopped = $"viewprobe could not be started ({ex.Message.TrimEnd('.')}).";
            return false;
        }
    }

    /// <summary>A tile was written: read it and show it if its square is in view.</summary>
    void Arrived(string path, double drawMs, double writeMs)
    {
        if (_runTiles == 0) _firstTile = _runClock.Elapsed.TotalSeconds;
        _runTiles++; _drawMs += drawMs; _writeMs += writeMs;
        if (_orderDir is null || _worldKey is null) return;
        var name = Path.GetFileNameWithoutExtension(path).Split('_');
        if (name.Length != 2 || !int.TryParse(name[0], CultureInfo.InvariantCulture, out int x) || !int.TryParse(name[1], CultureInfo.InvariantCulture, out int y)
            || !int.TryParse(Path.GetFileName(Path.GetDirectoryName(path)), CultureInfo.InvariantCulture, out int scale)) return;
        if (_view is { } v && x >= v.X0 && x <= v.X1 && y >= v.Y0 && y <= v.Y1 && (scale == _scale || !_pictures.ContainsKey((_scale, x, y)))) Read(scale, x, y);
        Say();
    }

    void Ended(int code, string? why)
    {
        _child?.Dispose(); _child = null;
        if (_running is { } r)
        {
            LastBlock = (_firstTile, _runClock.Elapsed.TotalSeconds, _runTiles, _runTiles == 0 ? 0 : _drawMs / _runTiles, _runTiles == 0 ? 0 : _writeMs / _runTiles, r.Bx, r.By);
            if (code != 0 || _runTiles == 0)
            {
                var (ox, oy) = MapTileGrid.BlockOrigin(r.Bx, r.By);
                _failed[(r.Bx, r.By)] = FormattableString.Invariant($"the cells ({ox}, {oy}) to ({ox + MapTileGrid.BlockCells - 1}, {oy + MapTileGrid.BlockCells - 1}) could not be drawn: ")
                                        + (why ?? $"viewprobe stopped with exit {code} and said nothing; its output is in last-run.log under the tile folder.");
                // the same failure twice with nothing drawn in between is not about one place: stop asking
                if (++_misses >= 2) _stopped = _failed[(r.Bx, r.By)];
            }
            else _misses = 0;
        }
        _running = null;
        StartNext();
        Say();
    }

    void Kill()
    {
        _runId++;                                          // a killed run's end is not a failure
        var c = _child; _child = null; _running = null;
        if (c is null) return;
        try { if (!c.HasExited) c.Kill(entireProcessTree: true); } catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception) { }
        c.Dispose();
    }

    void Say(bool zoomedOut = false)
    {
        string s;
        if (!Enabled || _order is null) s = "";
        else if (_stopped is { } why) s = "textured tiles cannot be drawn: " + why;
        else if (_view is null) s = zoomedOut && _terrain is not null ? "zoom in further to see textured tiles." : "";
        else
        {
            var v = _view.Value;
            int shown = _shown.Count(k => k.Key.X >= v.X0 && k.Key.X <= v.X1 && k.Key.Y >= v.Y0 && k.Key.Y <= v.Y1);
            s = $"textured: {shown} of {_inView} cell(s) in view shown";
            if (_running is { } r) s += $"; drawing the cells around ({r.X}, {r.Y}), {_runTiles} tile(s) so far";
            if (_queue.Count > 0) s += $"; {_queue.Count} more block(s) of {MapTileGrid.BlockCells} by {MapTileGrid.BlockCells} cells to draw";
            if (_failed.Count > 0) s += $"; {_failed.Count} block(s) not drawn, the first: {_failed.Values.First()}";
            s += ".";
        }
        // the loading strip: the share of the cells in view that are shown, while any is still being drawn or read
        bool busy = Enabled && _stopped is null && _view is not null && (_running is not null || _queue.Count > 0 || _reading.Count > 0);
        if (busy && _view is { } vv)
        {
            int have = _shown.Count(k => k.Key.X >= vv.X0 && k.Key.X <= vv.X1 && k.Key.Y >= vv.Y0 && k.Key.Y <= vv.Y1);
            Loading.Set("tiles", $"Drawing the map's textured tiles ({have} of {_inView} cells in view)", _inView > 0 ? (double)have / _inView : null);
        }
        else Loading.Clear("tiles");
        if (s == Status) return;
        Status = s;
        Changed?.Invoke();
    }

    /// <summary>What the state tools read.</summary>
    internal object State() => new
    {
        enabled = Enabled, status = Status, scale = _scale == 0 ? (int?)null : _scale, folder = _orderDir, renderer = MapTileCache.RendererVersion,
        view = _view is { } v ? new[] { v.X0, v.Y0, v.X1, v.Y1 } : null, inView = _inView,
        shown = _view is { } w ? _shown.Count(k => k.Key.X >= w.X0 && k.Key.X <= w.X1 && k.Key.Y >= w.Y0 && k.Key.Y <= w.Y1) : 0,
        shownAtScale = _view is { } u ? _shown.Count(k => k.Value.Scale == _scale && k.Key.X >= u.X0 && k.Key.X <= u.X1 && k.Key.Y >= u.Y0 && k.Key.Y <= u.Y1) : 0,
        reading = _reading.Count, drawing = _running is { } r ? new { block = new[] { r.Bx, r.By }, cell = new[] { r.X, r.Y }, ring = r.Ring, tiles = _runTiles, seconds = _runClock.Elapsed.TotalSeconds } : null,
        queued = _queue.Count, failed = _failed.Select(f => new { block = new[] { f.Key.Bx, f.Key.By }, why = f.Value }).ToList(), stopped = _stopped,
        lastBlock = LastBlock is { } b ? new { block = new[] { b.Bx, b.By }, firstTileSeconds = b.FirstTileSeconds, seconds = b.Seconds, tiles = b.Tiles, drawMs = b.DrawMs, writeMs = b.WriteMs } : null,
        diskReads = DiskReads.Count, diskReadMeanMs = DiskReads.MeanMs,
    };
}
