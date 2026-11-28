using Mutagen.Bethesda.Plugins;

namespace SkyWright.Engine;

// ======================================================================
//  WorldRead — the SHARED, CACHED WORLD READ for given cells (shared world read, unit 1).
//
//  One prepared file per plugin (WorldReadFile), keyed by the plugin file's content hash, the file's schema and the
//  Engine's module version id, holds a directory of every placed record the plugin holds, those records filed by
//  cell, the cells it holds and the facts of its modeled base records. A read of some cells replays the wanted cell
//  sections in load order and asks the directories which plugin's record of each reference wins: the last plugin in
//  the order that holds a record of it at all, with or without placement data. Record headers carry no position, so
//  every "where" comes from placement data.
// ======================================================================

/// <summary>A problem the reader cannot work around: one plain sentence saying what went wrong and what to try.</summary>
public sealed class WorldReadException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>One cell as the last plugin in the order that holds its record states it.</summary>
/// <param name="Worldspace">Null for an interior.</param>
/// <param name="Grid">Null for an interior and for a worldspace's persistent cell.</param>
/// <param name="IsPersistentCell">The worldspace's persistent cell (its top cell), which holds persistent references
/// wherever they stand.</param>
public sealed record WorldCell(FormKey Cell, string? EditorId, bool Interior, FormKey? Worldspace, (int X, int Y)? Grid, bool IsPersistentCell);

/// <summary>The winning record of one reference.</summary>
/// <param name="Ref">Everything the record's placement says; null when the winning record carries no placement data
/// (a deletion, nearly always).</param>
/// <param name="Plugin">The load-order index of the plugin whose record wins.</param>
/// <param name="Cell">The cell the winning record is filed under.</param>
/// <param name="Persistent">The winning record is in its cell's persistent list.</param>
/// <param name="Deleted">The winning record is flagged deleted.</param>
public sealed record WorldRef(FormKey Reference, PlacedRef? Ref, int Plugin, string PluginName, FormKey Cell, bool Persistent, bool Deleted)
{
    public bool HasPlacement => Ref is not null;
}

/// <summary>A reference an earlier plugin placed in the requested area whose winning record stands elsewhere.</summary>
/// <param name="FromPlugin">The last plugin whose record of it is in the requested area.</param>
/// <param name="FromCell">The cell that plugin files it under.</param>
/// <param name="Winner">The winning record, and where it is filed.</param>
public sealed record MovedOutRef(FormKey Reference, int FromPlugin, string FromPluginName, FormKey FromCell, WorldRef Winner);

/// <summary>What stands in the requested cells.</summary>
/// <param name="Cells">The requested cells that some plugin holds.</param>
/// <param name="Winners">The references standing in the area: winning, not deleted, with placement data.</param>
/// <param name="MovedOut">References placed in the area by an earlier plugin whose winning record stands elsewhere.</param>
/// <param name="Deleted">References seen in the area whose winning record is deleted, with or without placement
/// data, wherever that record is filed.</param>
/// <param name="Unplaced">References seen in the area whose winning record carries no placement data and is not
/// flagged deleted: it has no position the reader could give.</param>
public sealed record WorldAnswer(
    IReadOnlyList<WorldCell> Cells,
    IReadOnlyList<WorldRef> Winners,
    IReadOnlyList<MovedOutRef> MovedOut,
    IReadOnlyList<WorldRef> Deleted,
    IReadOnlyList<WorldRef> Unplaced);

/// <summary>The facts of one modeled base record, as the winning plugin's record gives them.</summary>
/// <param name="TypeName">Mutagen's record type name ("Static", "Light", "Armor", ...).</param>
/// <param name="Model">The model it is drawn with in the world (<see cref="BaseModel.Of"/>).</param>
/// <param name="FemaleModel">An armour's female ground model, else its male one (<see cref="BaseModel.FemaleOf"/>); null
/// for any other record.</param>
/// <param name="Obnd">OBND extents, |second − first| per axis; null when the record has none.</param>
/// <param name="IsMarker">The Is-Marker header flag (0x00800000).</param>
/// <param name="LightCanBeCarried">A light flagged Can Be Carried.</param>
public sealed record BaseFacts(FormKey Key, string TypeName, string? EditorId, string? Model, string? FemaleModel, Vec3? Obnd,
                               bool IsMarker, bool LightCanBeCarried, int Plugin, string PluginName);

/// <summary>Where <see cref="WorldRead.Open"/> spent its time, in seconds.</summary>
public sealed record WorldReadTimings(double HashSeconds, double PrepareSeconds, double LoadSeconds);

/// <summary>The shared world read over one load order. Thread-safe for reads once open.</summary>
public sealed class WorldRead
{
    /// <summary>The prepared file's layout version. A change to what the file holds bumps it.</summary>
    public const int Schema = 1;

    /// <summary>The Engine build the prepared files are made by: any change to the Engine's code prepares every plugin
    /// once more, so a reader never reads a file an older reader wrote.</summary>
    public static Guid EngineVersion => typeof(WorldRead).Module.ModuleVersionId;

    /// <summary>Plugins whose prepared file was made by this <see cref="Open"/>.</summary>
    public int Prepared { get; private set; }
    /// <summary>Plugins whose prepared file was already present and read.</summary>
    public int Reused { get; private set; }
    /// <summary>Prepared files rebuilt during a read because a cell's data failed its hash on disk after the open.</summary>
    public int Rebuilt { get; private set; }
    /// <summary>Schema, Engine version and the ordered plugin names with their content hashes, for receipts.</summary>
    public string Identity { get; private set; } = "";
    public WorldReadTimings Timings { get; private set; } = new(0, 0, 0);
    /// <summary>The plugins' file names, in load order.</summary>
    public IReadOnlyList<string> PluginNames { get; private set; } = Array.Empty<string>();

    readonly string[] _paths, _shas;
    readonly string _cache;
    readonly WorldFile[] _files;
    readonly object _rebuild = new();
    /// <summary>cell -> (plugin, row in that plugin's cell table), in load order.</summary>
    readonly Dictionary<FormKey, List<(int Plugin, int Row)>> _holders = new();
    readonly Dictionary<FormKey, WorldCell> _cells = new();
    readonly Dictionary<(FormKey Ws, int X, int Y), List<FormKey>> _grid = new();
    readonly Dictionary<FormKey, List<FormKey>> _persistentCells = new();

    WorldRead(string[] paths, string[] shas, string cache, WorldFile[] files)
    {
        _paths = paths; _shas = shas; _cache = cache; _files = files;
        PluginNames = paths.Select(Path.GetFileName).ToArray()!;
        Identity = $"world read schema {Schema}; engine {EngineVersion:D}; plugins: " + string.Join("; ", PluginNames.Select((n, i) => $"{n} {shas[i]}"));
        for (int p = 0; p < files.Length; p++)
            for (int r = 0; r < files[p].Cells.Length; r++)
            {
                var c = files[p].Cells[r];
                if (!_holders.TryGetValue(c.Cell, out var list)) _holders[c.Cell] = list = new List<(int, int)>(1);
                list.Add((p, r));
                _cells[c.Cell] = new WorldCell(c.Cell, c.EditorId, c.Interior, c.Worldspace, c.Grid, c.IsPersistentCell);   // the last holder states it
            }
        foreach (var c in _cells.Values)
        {
            if (c.Worldspace is not { } ws) continue;
            if (c.IsPersistentCell) { if (!_persistentCells.TryGetValue(ws, out var pl)) _persistentCells[ws] = pl = new(); pl.Add(c.Cell); }
            else if (c.Grid is { } g) { if (!_grid.TryGetValue((ws, g.X, g.Y), out var gl)) _grid[(ws, g.X, g.Y)] = gl = new(); gl.Add(c.Cell); }
        }
    }

    /// <summary>Hash each plugin, prepare the files that are missing or do not match, and load their directories.</summary>
    public static WorldRead Open(IReadOnlyList<string> orderedPluginPaths, string cacheFolder)
    {
        var paths = orderedPluginPaths.ToArray();
        foreach (var p in paths)
            if (!File.Exists(p)) throw new WorldReadException($"The plugin {Path.GetFileName(p)} was not found at {p}; check the load order's plugin paths.");
        try { Directory.CreateDirectory(cacheFolder); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        { throw new WorldReadException($"The world read's cache folder {cacheFolder} could not be made ({ex.Message}); choose a folder that can be written.", ex); }

        var sw = System.Diagnostics.Stopwatch.StartNew();
        var shas = new string[paths.Length];
        Parallel.For(0, paths.Length, i =>
        {
            try { shas[i] = WorldReadFile.ContentSha(paths[i]); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            { throw new WorldReadException($"The plugin {Path.GetFileName(paths[i])} could not be read ({ex.Message}); check that the file is not locked.", ex); }
        });
        double hash = sw.Elapsed.TotalSeconds;

        var files = new WorldFile[paths.Length];
        var missing = new List<int>();
        sw.Restart();
        Parallel.For(0, paths.Length, i => files[i] = TryLoad(cacheFolder, paths[i], shas[i])!);
        for (int i = 0; i < paths.Length; i++) if (files[i] is null) missing.Add(i);
        double load = sw.Elapsed.TotalSeconds;

        sw.Restart();
        try
        {
            Parallel.ForEach(missing, new ParallelOptions { MaxDegreeOfParallelism = Math.Clamp(Environment.ProcessorCount / 2, 1, 8) },
                             i => files[i] = Prepare(cacheFolder, paths[i], shas[i]));
        }
        catch (AggregateException ae) when (ae.InnerExceptions.FirstOrDefault(e => e is WorldReadException) is { } first) { throw first; }
        double prepare = sw.Elapsed.TotalSeconds;

        sw.Restart();
        var read = new WorldRead(paths, shas, cacheFolder, files) { Prepared = missing.Count, Reused = paths.Length - missing.Count };
        read.Timings = new WorldReadTimings(hash, prepare, load + sw.Elapsed.TotalSeconds);
        return read;
    }

    static WorldFile? TryLoad(string cache, string path, string sha)
    {
        try { return WorldReadFile.Load(WorldReadFile.PathFor(cache, sha, Path.GetFileName(path)), sha, Path.GetFileName(path), Schema, EngineVersion); }
        catch (Exception ex) when (ex is WorldFileUnreadable or IOException or UnauthorizedAccessException or EndOfStreamException
                                      or ArgumentException or FormatException or OverflowException or IndexOutOfRangeException)
        { return null; }
    }

    /// <summary>Make the plugin's prepared file and load it. An unreadable plugin is an error naming it.</summary>
    static WorldFile Prepare(string cache, string path, string sha)
    {
        string name = Path.GetFileName(path);
        string at, final = WorldReadFile.PathFor(cache, sha, name);
        try { at = WorldReadFile.WriteFor(path, sha, final, Schema, EngineVersion); }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            throw new WorldReadException($"The plugin {name} could not be read for the world read ({ex.GetType().Name}: {ex.Message}); check that it is a complete Skyrim Special Edition plugin, or take it out of the load order.", ex);
        }
        try
        {
            var f = WorldReadFile.Load(at, sha, name, Schema, EngineVersion);
            if (at != final)
            {
                // The rename failed (another reader holds the old file): keep this complete copy in memory and delete it,
                // so no temporary file is left; the next open tries the rename again.
                f.Bytes = File.ReadAllBytes(at);
                try { File.Delete(at); } catch (IOException) { } catch (UnauthorizedAccessException) { }
            }
            return f;
        }
        catch (WorldFileUnreadable why)
        { throw new WorldReadException($"The world read's prepared file for {name} could not be read back ({why.Message}); check the cache folder {cache} is not being changed by another program.", why); }
    }

    /// <summary>One cell blob; a blob that fails its hash rebuilds the plugin's prepared file once and reads again.</summary>
    List<WorldEntry> ReadCell(int plugin, int row)
    {
        var f = _files[plugin];
        try { return f.ReadCell(row); }
        catch (Exception ex) when (ex is WorldFileUnreadable or IOException or EndOfStreamException)
        {
            lock (_rebuild)
            {
                if (ReferenceEquals(_files[plugin], f)) { _files[plugin] = Prepare(_cache, _paths[plugin], _shas[plugin]); Rebuilt++; }
                var nf = _files[plugin];
                var cell = f.Cells[row].Cell;
                int nrow = Array.FindIndex(nf.Cells, c => c.Cell == cell);
                return nrow < 0 ? new List<WorldEntry>() : nf.ReadCell(nrow);
            }
        }
    }

    /// <summary>Every cell some plugin holds, as the last plugin holding it states it.</summary>
    public IReadOnlyList<WorldCell> AllCells => _cells.Values.ToList();

    /// <summary>One cell by its key, whole: an interior, a grid cell or a worldspace's persistent cell.</summary>
    public WorldAnswer Cell(FormKey cell) => Answer(new HashSet<FormKey> { cell }, new HashSet<FormKey>(), _ => false);

    /// <summary>The grid cells of <paramref name="worldspace"/> within <paramref name="ring"/> cells of (x, y), and
    /// with <paramref name="includePersistent"/> the worldspace's persistent references whose position lies in them.</summary>
    public WorldAnswer Exterior(FormKey worldspace, int x, int y, int ring, bool includePersistent)
    {
        if (ring < 0) throw new WorldReadException($"The ring {ring} is negative; ask for 0 (the one cell) or more.");
        var whole = new HashSet<FormKey>();
        for (int gx = x - ring; gx <= x + ring; gx++)
            for (int gy = y - ring; gy <= y + ring; gy++)
                if (_grid.TryGetValue((worldspace, gx, gy), out var cs)) whole.UnionWith(cs);
        var persistent = includePersistent && _persistentCells.TryGetValue(worldspace, out var pc) ? pc.ToHashSet() : new HashSet<FormKey>();
        return Answer(whole, persistent, p =>
        {
            int gx = (int)MathF.Floor(p.X / 4096f), gy = (int)MathF.Floor(p.Y / 4096f);
            return Math.Abs(gx - x) <= ring && Math.Abs(gy - y) <= ring;
        });
    }

    /// <summary>The answer for the cells read whole and the persistent cells read by position: a reference with any
    /// record in the area is answered from its winning record.</summary>
    WorldAnswer Answer(HashSet<FormKey> whole, HashSet<FormKey> persistent, Func<Vec3, bool> inRing)
    {
        bool InArea(FormKey cell, PlacedRef? r) => whole.Contains(cell) || (persistent.Contains(cell) && r is not null && inRing(r.Position));
        var cells = new List<WorldCell>();
        var read = new Dictionary<(int Plugin, FormKey Key), (WorldEntry E, FormKey Cell)>();
        var lastIn = new Dictionary<FormKey, (int Plugin, FormKey Cell)>();
        foreach (var cell in whole.Concat(persistent))
        {
            if (!_cells.TryGetValue(cell, out var wc)) continue;
            cells.Add(wc);
            foreach (var (p, row) in _holders[cell])
                foreach (var e in ReadCell(p, row))
                {
                    read[(p, e.Key)] = (e, cell);
                    if (InArea(cell, e.Ref) && (!lastIn.TryGetValue(e.Key, out var li) || li.Plugin <= p)) lastIn[e.Key] = (p, cell);
                }
        }
        var winners = new List<WorldRef>(); var moved = new List<MovedOutRef>(); var deleted = new List<WorldRef>(); var unplaced = new List<WorldRef>();
        foreach (var (key, from) in lastIn)
        {
            if (Winner(key, read) is not { } w) continue;
            if (w.Deleted) deleted.Add(w);
            else if (w.Ref is null) unplaced.Add(w);
            else if (InArea(w.Cell, w.Ref)) winners.Add(w);
            else moved.Add(new MovedOutRef(key, from.Plugin, PluginNames[from.Plugin], from.Cell, w));
        }
        return new WorldAnswer(cells, winners, moved, deleted, unplaced);
    }

    /// <summary>The plugin whose record of the reference wins: the last in the order that holds one at all.</summary>
    (int Plugin, int Row)? WinnerRow(FormKey key)
    {
        for (int p = _files.Length - 1; p >= 0; p--)
        {
            int row = _files[p].Find(key);
            if (row >= 0) return (p, row);
        }
        return null;
    }

    WorldRef? Winner(FormKey key, Dictionary<(int Plugin, FormKey Key), (WorldEntry E, FormKey Cell)>? read)
    {
        if (WinnerRow(key) is not { } at) return null;
        var f = _files[at.Plugin];
        int cellRow = f.DirCell[at.Row];
        var cell = f.Cells[cellRow].Cell;
        WorldEntry e;
        if (read is not null && read.TryGetValue((at.Plugin, key), out var hit) && hit.Cell == cell) e = hit.E;
        else
        {
            var found = ReadCell(at.Plugin, cellRow).Where(x => x.Key == key).ToList();
            if (found.Count == 0) return null;
            e = found[^1];
        }
        return new WorldRef(key, e.Ref, at.Plugin, PluginNames[at.Plugin], cell, e.Persistent, e.Deleted);
    }

    /// <summary>The winning record of a reference wherever it is filed; null when no plugin holds one.</summary>
    public WorldRef? Lookup(FormKey reference) => Winner(reference, null);

    /// <summary>The winning facts of a modeled base record wherever it is defined; null when no plugin holds one.</summary>
    public BaseFacts? Base(FormKey key)
    {
        for (int p = _files.Length - 1; p >= 0; p--)
        {
            int i = _files[p].FindBase(key);
            if (i < 0) continue;
            var b = _files[p].Bases[i];
            return new BaseFacts(key, b.TypeName, b.EditorId, b.Model, b.FemaleModel, b.Obnd, b.IsMarker, b.LightCanBeCarried, p, PluginNames[p]);
        }
        return null;
    }
}
