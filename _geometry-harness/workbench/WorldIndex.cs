using System.Diagnostics;
using System.Globalization;
using System.Text.RegularExpressions;
using HousecarlCore;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Plugins.Records;
using Mutagen.Bethesda.Skyrim;

namespace Workbench;

/// <summary>One worldspace at its winner. <paramref name="CellCount"/> counts its grid cells, not its persistent cell.</summary>
internal sealed record WorldspaceRow(string Key, string? EditorId, string? Name, string Winner, string? PersistentCell, int CellCount);

/// <summary>One cell at its winner. A cell a worldspace contains carries block X,Y and sub-block X,Y from its grid; any other
/// cell carries its block and sub-block numbers in A (B is 0). A worldspace's persistent cell has no grid and no block.</summary>
internal sealed record CellRow(string Key, string? EditorId, string? Name, bool Interior, string? Worldspace,
    int? GridX, int? GridY, string Winner, bool IsPersistentCell,
    int BlockA, int BlockB, int SubBlockA, int SubBlockB);

/// <summary>One record a cell holds, at its winner. <paramref name="Type"/> and <paramref name="BaseType"/> are four letters.</summary>
internal sealed record ObjectRow(string Key, string Type, string? EditorId,
    string? BaseKey, string? BaseEditorId, string? BaseType, string Winner,
    bool Persistent, bool Deleted, bool InitiallyDisabled, float[]? Position);

/// <summary>A cell's records, split as the winning plugin's own copy of the cell files them.</summary>
internal sealed record CellObjects(IReadOnlyList<ObjectRow> Persistent, IReadOnlyList<ObjectRow> Temporary, bool HasPlacedOrNavmesh)
{
    /// <summary>Records the index files under this cell that their winning plugin's copy of the cell does not list.</summary>
    public IReadOnlyList<ObjectRow> Unfiled { get; init; } = Array.Empty<ObjectRow>();

    /// <summary>What could not be read or did not agree, one sentence each.</summary>
    public IReadOnlyList<string> Notes { get; init; } = Array.Empty<string>();
}

/// <summary>One worldspace's terrain at its winners, sampled for a map: <paramref name="Samples"/> heights a side per
/// cell, west to east then north to south over the grid squares from <paramref name="MinX"/>, <paramref name="MinY"/>;
/// NaN where no cell gives heights. <paramref name="Cells"/> marks the squares a cell record stands on.
/// <paramref name="WaterHeight"/> is the worldspace's default water level (its parent's when it sets none), or null; a
/// cell's own water level is not read. A worldspace whose record says it uses its parent's land
/// (<paramref name="Parent"/>) has the parent's heights on the squares where it has none of its own, and
/// <paramref name="Borrowed"/> marks those squares.</summary>
internal sealed record WorldTerrain(string Worldspace, int MinX, int MinY, int Width, int Height, int Samples,
    float[] Heights, bool[] Cells, float? WaterHeight, int LandCells, int Unreadable, double Seconds)
{
    public string? Parent { get; init; }
    public bool[] Borrowed { get; init; } = Array.Empty<bool>();
    public int BorrowedCells => Borrowed.Count(b => b);
    public int MaxY => MinY + Height - 1;
}

/// <summary>A named map marker of a worldspace: the game's own marker type, and where it stands.</summary>
internal sealed record MapPlace(string Key, string Name, string Type, float X, float Y);

/// <summary>What a worldspace's map draws over its terrain: its named map markers, and the outline from above of each
/// placed building (<paramref name="Outlines"/>: one closed ring of X,Y pairs per building, in world units).</summary>
internal sealed record WorldFeatures(string Worldspace, IReadOnlyList<MapPlace> Places, IReadOnlyList<float[]> Outlines, int PlacedRead, double Seconds)
{
    /// <summary>The placed building behind each outline, in the same order: its mesh and where the reference puts it.</summary>
    public IReadOnlyList<MapBuilding> Buildings { get; init; } = Array.Empty<MapBuilding>();
}

/// <summary>One placed building: its base's mesh path as the record gives it, and the reference's placement.</summary>
internal sealed record MapBuilding(string Mesh, Vec3 Position, Vec3 Rotation, float Scale);

/// <summary>Where a record is: its cell and worldspace, and its winning position when it is a placed object.</summary>
internal sealed record Located(string Key, string RecordType, string? Cell, string? Worldspace, bool IsCell, float[]? Position);

/// <summary>The data behind the read-only World tree: worldspaces, cells and a cell's records over one load order. Winners
/// and containment come from housecarl-core's resolver index; nothing here decides them.</summary>
internal sealed class WorldIndex : IDisposable
{
    static readonly Type[] WorldAndCellTypes = { typeof(IWorldspaceGetter), typeof(ICellGetter) };
    static readonly Regex GridText = new(@"^\s*(-?\d+)\s*,\s*(-?\d+)\s*$", RegexOptions.Compiled);

    /// <summary>Everything the index holds, dropped as one reference on Dispose so a read under way keeps its own.</summary>
    sealed class Held(LoadOrderResolver resolver)
    {
        public readonly LoadOrderResolver Resolver = resolver;
        public readonly Dictionary<FormKey, CellRow> Cells = new();
        public readonly Dictionary<FormKey, IReadOnlyList<CellRow>> CellsOfWorld = new();
        public readonly Dictionary<(FormKey World, int X, int Y), FormKey> Grid = new();
        public readonly Dictionary<FormKey, FormKey> PersistentCellOf = new();   // worldspace to its persistent cell
    }

    Held? _held;
    readonly IReadOnlyDictionary<string, string> _signatures;   // catalog name ("PlacedObject") to four letters ("REFR")
    readonly List<string> _notes = new();

    WorldIndex(LoadOrderResolver resolver, IReadOnlyDictionary<string, string> signatures)
    {
        _held = new Held(resolver); _signatures = signatures;
        Plugins = resolver.PluginNames.ToArray();
    }

    Held Live() => Volatile.Read(ref _held) ?? throw new ObjectDisposedException(nameof(WorldIndex));

    /// <summary>File names, in load order.</summary>
    public IReadOnlyList<string> Plugins { get; }
    public TimeSpan BuildTime { get; private set; }

    /// <summary>The part of <see cref="BuildTime"/> spent reading every plugin's worldspace and cell records.</summary>
    public TimeSpan CellPassTime { get; private set; }
    public long BuildWorkingSetDeltaBytes { get; private set; }

    /// <summary>What the build could not read or found twice, one sentence each: plugins left out, cells sharing a grid.</summary>
    public IReadOnlyList<string> Notes => _notes;

    /// <summary>Winners, by cell count descending, then EditorID.</summary>
    public IReadOnlyList<WorldspaceRow> Worldspaces { get; private set; } = Array.Empty<WorldspaceRow>();

    /// <summary>Every cell winner: cells no worldspace contains, grid cells, and each worldspace's persistent cell.</summary>
    public IReadOnlyList<CellRow> Cells { get; private set; } = Array.Empty<CellRow>();

    /// <summary>The cells no worldspace contains.</summary>
    public IReadOnlyList<CellRow> Interiors { get; private set; } = Array.Empty<CellRow>();

    /// <summary>Build the index on a background thread. Cancelling returns at once; the plugin pass under way ends at its next check.</summary>
    public static Task<WorldIndex> BuildAsync(IReadOnlyList<string> orderedPluginPaths, IProgress<string>? say, CancellationToken ct,
                                              string? corpusPath = null)
    {
        var paths = orderedPluginPaths.ToArray();
        var work = Task.Run(() => Build(paths, say, ct, corpusPath));
        // The resolver's pass takes no token, so the caller is released on cancel and the abandoned pass is observed here.
        work.ContinueWith(t => _ = t.Exception, TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously);
        return work.WaitAsync(ct);
    }

    static WorldIndex Build(string[] paths, IProgress<string>? say, CancellationToken ct, string? corpusPath)
    {
        ct.ThrowIfCancellationRequested();
        if (paths.Length == 0)
            throw new InvalidOperationException("the load order has no plugins, so there is no world to list; check the profile's loadorder.txt and plugins.txt.");
        var clock = Stopwatch.StartNew();
        long before = WorkingSet();
        var signatures = Signatures(CorpusPathOr(corpusPath));
        say?.Report($"Indexing {paths.Length} plugins");
        ct.ThrowIfCancellationRequested();
        var resolver = LoadOrderResolver.Build(paths);   // one pass over every plugin, no token
        var index = new WorldIndex(resolver, signatures);
        try
        {
            ct.ThrowIfCancellationRequested();
            var cellPass = Stopwatch.StartNew();
            index.ReadCells(say, ct);
            index.CellPassTime = cellPass.Elapsed;
        }
        catch { index.Dispose(); throw; }
        index.BuildTime = clock.Elapsed;
        index.BuildWorkingSetDeltaBytes = WorkingSet() - before;
        say?.Report(string.Create(CultureInfo.InvariantCulture,
            $"World index ready: {index.Worldspaces.Count} worldspaces and {index.Cells.Count} cells from {paths.Length} plugins in {index.BuildTime.TotalSeconds:0.0} s."));
        return index;
    }

    static long WorkingSet()
    {
        using var p = Process.GetCurrentProcess();
        p.Refresh();
        return p.WorkingSet64;
    }

    // ---- record type names: the resolver's catalog name, then the rulebook's four letters for it ----

    static readonly object SignatureGate = new();
    static (string Path, DateTime Written, IReadOnlyDictionary<string, string> Table)? _signatureCache;

    static string CorpusPathOr(string? given)
    {
        if (given is { Length: > 0 }) return given;
        try { return Program.Corpus(); }
        catch (InvalidOperationException)
        {
            throw new InvalidOperationException("the houseCARL rulebook (corpus.json) was not found, so record types cannot be named; set WB_CORPUS to its path.");
        }
    }

    /// <summary>Catalog name to four-letter signature for every record type, from the rulebook the houseCARL server reads.</summary>
    static IReadOnlyDictionary<string, string> Signatures(string corpusPath)
    {
        var full = Path.GetFullPath(corpusPath);
        if (!File.Exists(full))
            throw new InvalidOperationException($"the houseCARL rulebook is not at '{full}', so record types cannot be named; run the generator (src/housecarl-generator) or set WB_CORPUS to corpus.json.");
        var written = File.GetLastWriteTimeUtc(full);
        lock (SignatureGate)
        {
            if (_signatureCache is { } hit && hit.Written == written && string.Equals(hit.Path, full, StringComparison.OrdinalIgnoreCase))
                return hit.Table;
            var table = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var ts in CorpusRulebook.LoadCorpus(full).Types.Values)
                if (ts.Kind == "record" && ts.Signature is { Length: > 0 } sig) table[ts.Name] = sig;
            _signatureCache = (full, written, table);
            return table;
        }
    }

    string TypeOfName(string catalogName) => _signatures.TryGetValue(catalogName, out var sig) ? sig : catalogName;

    /// <summary>A record's four letters; a type the rulebook does not carry is shown by its Mutagen name and said in <paramref name="notes"/>.</summary>
    string TypeOf(IMajorRecordGetter body, List<string>? notes = null)
    {
        var name = RecordNaming.StripOverlay(body.GetType().Name);
        if (_signatures.TryGetValue(name, out var sig)) return sig;
        var note = $"the rulebook has no four-letter type for '{name}', so it is shown by that name.";
        if (notes is not null && !notes.Contains(note)) notes.Add(note);
        return name;
    }

    // ---- the cell index: one typed pass over every plugin's worldspace and cell records ----

    void ReadCells(IProgress<string>? say, CancellationToken ct)
    {
        var held = Live();
        var view = held.Resolver.Capture(null);
        foreach (var failure in view.LoadFailures) _notes.Add($"Left out of the index: {failure}");

        var worlds = new Dictionary<FormKey, (string? EditorId, string? Name)>();
        var drafts = new List<(FormKey Key, string? EditorId, string? Name, bool Interior, (int X, int Y)? Grid)>();
        string? reading = null;
        int pluginsRead = 0;
        var lastSaid = Stopwatch.StartNew();
        foreach (var (fk, _, body, source) in view.RecordsIn(Array.Empty<string>(), WorldAndCellTypes))
        {
            ct.ThrowIfCancellationRequested();
            if (!ReferenceEquals(source, reading))
            {
                reading = source; pluginsRead++;
                if (say is not null && lastSaid.ElapsedMilliseconds >= 200)
                {
                    say.Report($"Reading cells: {source} ({pluginsRead} of {Plugins.Count} plugins)");
                    lastSaid.Restart();
                }
            }
            bool wins = view.ResolveWinner(fk) is { } w && string.Equals(w.WinnerPlugin, source, StringComparison.OrdinalIgnoreCase);
            if (body is IWorldspaceGetter ws)
            {
                // Any plugin's copy names the persistent cell; the winner's copy need not carry it.
                if (ws.TopCell is { } top) held.PersistentCellOf[fk] = top.FormKey;
                if (wins) worlds[fk] = (ws.EditorID, ws.Name?.String);
            }
            else if (wins && body is ICellGetter cell)
            {
                bool interior = cell.Flags.HasFlag(Cell.Flag.IsInteriorCell);
                (int X, int Y)? grid = !interior && cell.Grid is { } g ? (g.Point.X, g.Point.Y) : null;
                drafts.Add((fk, cell.EditorID, cell.Name?.String, interior, grid));
            }
        }

        var persistentCells = held.PersistentCellOf.Values.ToHashSet();
        var byWorld = new Dictionary<FormKey, List<CellRow>>();
        int sharedGrids = 0;
        string? sharedExample = null;
        foreach (var d in drafts)
        {
            ct.ThrowIfCancellationRequested();
            var world = view.ParentOf(d.Key);
            bool persistent = persistentCells.Contains(d.Key);
            var grid = persistent ? null : d.Grid;
            int blockA = 0, blockB = 0, subA = 0, subB = 0;
            if (world is null)
            {
                // The write engine's rule for a cell outside a worldspace (WriteEngine.AddInteriorCell).
                blockA = (int)(d.Key.ID % 10); subA = (int)(d.Key.ID / 10 % 10);
            }
            else if (grid is { } at)
            {
                // The write engine's rule for a grid cell (WriteEngine.AddExteriorCell).
                blockA = FloorDiv(at.X, 32); blockB = FloorDiv(at.Y, 32); subA = FloorDiv(at.X, 8); subB = FloorDiv(at.Y, 8);
            }
            var row = new CellRow(FormIdToken.Of(d.Key), d.EditorId, d.Name, d.Interior,
                world is { } wk ? FormIdToken.Of(wk) : null, grid?.X, grid?.Y,
                view.ResolveWinner(d.Key)!.Value.WinnerPlugin, persistent, blockA, blockB, subA, subB);
            held.Cells[d.Key] = row;
            if (world is not { } inWorld) continue;
            if (!worlds.ContainsKey(inWorld)) worlds[inWorld] = (null, null);
            if (persistent) continue;
            if (!byWorld.TryGetValue(inWorld, out var list)) byWorld[inWorld] = list = new List<CellRow>();
            list.Add(row);
            if (grid is not { } gp) continue;
            if (!held.Grid.TryGetValue((inWorld, gp.X, gp.Y), out var first)) { held.Grid[(inWorld, gp.X, gp.Y)] = d.Key; continue; }
            // Two cells at one grid: position lookups answer the one defined earlier in the load order.
            sharedGrids++;
            if (Earlier(view, d.Key, first)) held.Grid[(inWorld, gp.X, gp.Y)] = d.Key;
            sharedExample ??= $"{FormIdToken.Of(first)} and {FormIdToken.Of(d.Key)} at {gp.X},{gp.Y} in {FormIdToken.Of(inWorld)}";
        }
        if (sharedGrids > 0)
            _notes.Add($"{sharedGrids} cell(s) share a grid square with another cell of the same worldspace (for example {sharedExample}); a position there is answered with the cell defined earlier in the load order.");

        foreach (var (world, list) in byWorld)
        {
            list.Sort((a, b) => Compare(a.GridX, b.GridX) is var x and not 0 ? x
                              : Compare(a.GridY, b.GridY) is var y and not 0 ? y
                              : string.CompareOrdinal(a.Key, b.Key));
            held.CellsOfWorld[world] = list;
        }
        Worldspaces = worlds.Select(kv => new WorldspaceRow(FormIdToken.Of(kv.Key), kv.Value.EditorId, kv.Value.Name,
                view.ResolveWinner(kv.Key)?.WinnerPlugin ?? "",
                held.PersistentCellOf.TryGetValue(kv.Key, out var pc) ? FormIdToken.Of(pc) : null,
                byWorld.TryGetValue(kv.Key, out var cells) ? cells.Count : 0))
            .OrderByDescending(r => r.CellCount).ThenBy(r => r.EditorId is null).ThenBy(r => r.EditorId, StringComparer.OrdinalIgnoreCase)
            .ThenBy(r => r.Key, StringComparer.Ordinal).ToList();
        Cells = held.Cells.Values.OrderBy(r => r.EditorId is null).ThenBy(r => r.EditorId, StringComparer.OrdinalIgnoreCase)
            .ThenBy(r => r.Key, StringComparer.Ordinal).ToList();
        Interiors = Cells.Where(r => r.Worldspace is null).ToList();

        static int Compare(int? a, int? b) => (a ?? int.MaxValue).CompareTo(b ?? int.MaxValue);
    }

    /// <summary>Signed floor division: the block number of a possibly negative grid coordinate, as the write engine computes it.</summary>
    static int FloorDiv(int a, int b) => (int)Math.Floor((double)a / b);

    /// <summary>Whether <paramref name="a"/> is defined by an earlier plugin than <paramref name="b"/>, or the same plugin and a lower FormID.</summary>
    static bool Earlier(LoadOrderResolver.IndexView view, FormKey a, FormKey b)
    {
        int ia = view.OrderIndexOf(a.ModKey.FileName.String), ib = view.OrderIndexOf(b.ModKey.FileName.String);
        return ia != ib ? ia < ib : a.ID < b.ID;
    }

    // ---- lookups over the built index ----

    static FormKey? ParseKey(string? text) =>
        text is not null && FormKey.TryFactory(text.Trim(), out var fk) && !fk.IsNull ? fk : null;

    /// <summary>A worldspace's grid cells, by grid. Its persistent cell is not among them.</summary>
    public IReadOnlyList<CellRow> CellsOf(string worldspaceKey)
    {
        return ParseKey(worldspaceKey) is { } wk && Live().CellsOfWorld.TryGetValue(wk, out var list) ? list : Array.Empty<CellRow>();
    }

    /// <summary>The cell record on a worldspace's grid square, or null.</summary>
    public CellRow? CellAt(string worldspaceKey, int x, int y)
    {
        var held = Live();
        return ParseKey(worldspaceKey) is { } wk && held.Grid.TryGetValue((wk, x, y), out var ck) && held.Cells.TryGetValue(ck, out var row) ? row : null;
    }

    static readonly Type[] LandType = { typeof(ILandscapeGetter) };

    /// <summary>A worldspace's terrain for a map, on a background thread: each grid cell's winning LAND, classified by
    /// the shared rule (<see cref="LandHeight.Classify"/>) and sampled. A LAND that cannot be read is counted, not drawn.</summary>
    public Task<WorldTerrain> TerrainOfAsync(string worldspaceKey, CancellationToken ct) => Task.Run(() => TerrainOf(worldspaceKey, ct), ct);

    WorldTerrain TerrainOf(string worldspaceKey, CancellationToken ct)
    {
        var clock = Stopwatch.StartNew();
        var held = Live();
        if (ParseKey(worldspaceKey) is not { } wk || !held.CellsOfWorld.TryGetValue(wk, out var cells) || !cells.Any(c => c.GridX is not null))
            throw new ArgumentException($"'{worldspaceKey}' is not a worldspace with grid cells in this load order.");
        var gridded = cells.Where(c => c.GridX is not null && c.GridY is not null).ToList();
        int minX = gridded.Min(c => c.GridX!.Value), maxX = gridded.Max(c => c.GridX!.Value);
        int minY = gridded.Min(c => c.GridY!.Value), maxY = gridded.Max(c => c.GridY!.Value);
        int w = maxX - minX + 1, h = maxY - minY + 1;
        // every height of a cell (32 a side) for a small worldspace, fewer as it widens: the picture stays within 2048 pixels
        int n = 32;
        while (n > 1 && Math.Max(w, h) * n > 2048) n /= 2;
        int step = (LandHeight.Side - 1) / n;
        var heights = new float[w * n * h * n];
        Array.Fill(heights, float.NaN);
        var has = new bool[w * h];
        foreach (var c in gridded) has[(maxY - c.GridY!.Value) * w + (c.GridX!.Value - minX)] = true;

        var view = held.Resolver.Capture(null);
        // the worldspace's winning record: its default water level (float.MaxValue there means "not set"), and the
        // parent whose land it says it uses
        float? water = null;
        FormKey? parent = null;
        using (var session = view.OpenSession())
        {
            float? WaterOf(IWorldspaceGetter ws) =>
                ws.LandDefaults?.DefaultWaterHeight is { } dwh && float.IsFinite(dwh) && MathF.Abs(dwh) < 1e30f ? dwh : null;
            IWorldspaceGetter? Winner(FormKey k) =>
                view.ResolveWinner(k) is { } win ? view.GetRecord(session, win.WinnerPlugin, k, typeof(IWorldspaceGetter)) as IWorldspaceGetter : null;
            if (Winner(wk) is { } own)
            {
                water = WaterOf(own);
                if (own.Parent is { } p && p.Worldspace.FormKeyNullable is { IsNull: false } pk)
                {
                    if (p.Flags.HasFlag(WorldspaceParent.Flag.UseLandData)) parent = pk;
                    if (water is null && Winner(pk) is { } up) water = WaterOf(up);
                }
            }
        }
        float[]? lent = parent is null ? null : new float[heights.Length];
        if (lent is not null) Array.Fill(lent, float.NaN);

        int land = 0, unreadable = 0;
        foreach (var (fk, _, body, source) in view.RecordsIn(Array.Empty<string>(), LandType))
        {
            ct.ThrowIfCancellationRequested();
            if (view.ParentOf(fk) is not { } ck || !held.Cells.TryGetValue(ck, out var row) || row.GridX is not { } gx || row.GridY is not { } gy) continue;
            var inWorld = view.ParentOf(ck);
            bool ownLand = inWorld == wk;
            if (!ownLand && (parent is null || inWorld != parent || gx < minX || gx > maxX || gy < minY || gy > maxY)) continue;
            if (view.ResolveWinner(fk) is not { } win || !string.Equals(win.WinnerPlugin, source, StringComparison.OrdinalIgnoreCase)) continue;
            if (body is not ILandscapeGetter ls) continue;
            var lc = LandHeight.Classify(ls);
            if (lc.Kind == LandHeight.LandKind.DeclaresNone) continue;
            if (lc.Kind != LandHeight.LandKind.Heights) { if (ownLand) unreadable++; continue; }
            var decoded = LandHeight.Decode(lc.Offset, lc.Gradients!);
            if (ownLand) land++;
            var into = ownLand ? heights : lent!;
            int px = (gx - minX) * n, py = (maxY - gy) * n;      // the square's top-left sample; north is up
            for (int j = 0; j < n; j++)
                for (int i = 0; i < n; i++)
                    into[(py + (n - 1 - j)) * (w * n) + px + i] = decoded[i * step, j * step];
        }
        // the parent's ground on the squares this worldspace gives no heights for
        var borrowed = new bool[w * h];
        if (lent is not null)
            for (int cy = 0; cy < h; cy++)
                for (int cx = 0; cx < w; cx++)
                {
                    int at = cy * n * (w * n) + cx * n;
                    if (!float.IsNaN(heights[at]) || float.IsNaN(lent[at])) continue;
                    borrowed[cy * w + cx] = true;
                    for (int j = 0; j < n; j++) Array.Copy(lent, at + j * (w * n), heights, at + j * (w * n), n);
                }
        return new WorldTerrain(FormIdToken.Of(wk), minX, minY, w, h, n, heights, has, water, land, unreadable, clock.Elapsed.TotalSeconds)
        { Parent = parent is { } pw ? FormIdToken.Of(pw) : null, Borrowed = borrowed };
    }

    static readonly Type[] StaticType = { typeof(IStaticGetter) }, PlacedType = { typeof(IPlacedObjectGetter) }, WorldType = { typeof(IWorldspaceGetter) };

    /// <summary>A worldspace's map markers and building outlines, on a background thread. A building here is a placed
    /// static whose mesh is filed under an "architecture" folder and whose box is architecture-scale (256 units and
    /// up on its longest side, the detectors' own scale rule, DetectorModel.ClassifyOverlapScale): a naming convention,
    /// so a mod's building filed elsewhere is not drawn, and rocks and trees never are. A reference that starts
    /// disabled is left out; whether an enable parent or a quest later shows or hides one is not read.
    /// A walled city is its own worldspace, and its marker and buildings are filed there. So a worldspace's map also
    /// takes the buildings of a child that says it uses the parent's land (the rule the terrain follows; such a child
    /// shares the parent's ground), and the parent's buildings and markers on the squares of such a child. It takes
    /// the markers of every child that says it uses the parent's map, each put where the child's world map offset
    /// places it (position times the scale, plus the offset); a child whose scale folds it to a point is left to the
    /// parent's own marker for its entrance. A city with its own land (Markarth) therefore gets its caption and not
    /// its buildings. The ground meshes of a city (a mesh path naming "terrain") and a box wider than two cells are
    /// not buildings and are not drawn.</summary>
    public Task<WorldFeatures> FeaturesOfAsync(string worldspaceKey, CancellationToken ct) => Task.Run(() => FeaturesOf(worldspaceKey, ct), ct);

    WorldFeatures FeaturesOf(string worldspaceKey, CancellationToken ct)
    {
        var clock = Stopwatch.StartNew();
        var held = Live();
        if (ParseKey(worldspaceKey) is not { } wk) throw new ArgumentException($"'{worldspaceKey}' is not a worldspace in this load order.");
        var view = held.Resolver.Capture(null);

        // the buildings among the statics, at their winners: the box each states
        var boxes = new Dictionary<FormKey, (Vec3 Min, Vec3 Max, string Mesh)>();
        foreach (var (fk, _, body) in view.WinnerRecordsOfType(StaticType))
        {
            ct.ThrowIfCancellationRequested();
            if (body is not IStaticGetter st || st.ObjectBounds is not { } b) continue;
            if (st.Model?.File is not { } file || string.IsNullOrWhiteSpace(file.GivenPath) || !file.GivenPath.Contains("architecture", StringComparison.OrdinalIgnoreCase)
                || file.GivenPath.Contains("terrain", StringComparison.OrdinalIgnoreCase)) continue;
            var min = new Vec3(Math.Min(b.First.X, b.Second.X), Math.Min(b.First.Y, b.Second.Y), Math.Min(b.First.Z, b.Second.Z));
            var max = new Vec3(Math.Max(b.First.X, b.Second.X), Math.Max(b.First.Y, b.Second.Y), Math.Max(b.First.Z, b.Second.Z));
            if (Math.Max(max.X - min.X, Math.Max(max.Y - min.Y, max.Z - min.Z)) >= 256f && Math.Max(max.X - min.X, max.Y - min.Y) <= 8192f) boxes[fk] = (min, max, file.GivenPath);
        }

        // the worldspaces on this one's coordinates: its children, and its parent (kept to this worldspace's own squares)
        // per related worldspace: whether its buildings are drawn, and where its markers go (scale, then offset)
        var related = new Dictionary<FormKey, (bool Buildings, float Scale, float OffX, float OffY)> { [wk] = (true, 1f, 0f, 0f) };
        FormKey? parent = null;
        foreach (var (fk, _, body) in view.WinnerRecordsOfType(WorldType))
        {
            if (body is not IWorldspaceGetter ws || ws.Parent is not { } link || link.Worldspace.FormKeyNullable is not { IsNull: false } up) continue;
            bool sharesLand = link.Flags.HasFlag(WorldspaceParent.Flag.UseLandData);
            if (fk == wk) { if (sharesLand) { parent = up; related[up] = (true, 1f, 0f, 0f); } continue; }
            if (up != wk) continue;
            if (sharesLand) related[fk] = (true, 1f, 0f, 0f);
            else if (link.Flags.HasFlag(WorldspaceParent.Flag.UseMapData) && ws.WorldMapOffsetScale is > 0.01f and var scale && float.IsFinite(scale))
                related[fk] = (false, scale, ws.WorldMapCellOffset.X, ws.WorldMapCellOffset.Y);
        }
        float minX = float.MinValue, maxX = float.MaxValue, minY = float.MinValue, maxY = float.MaxValue;
        if (parent is not null && held.CellsOfWorld.TryGetValue(wk, out var own) && own.Any(c => c.GridX is not null))
        {
            minX = own.Min(c => c.GridX ?? int.MaxValue) * 4096f; maxX = (own.Max(c => c.GridX ?? int.MinValue) + 1) * 4096f;
            minY = own.Min(c => c.GridY ?? int.MaxValue) * 4096f; maxY = (own.Max(c => c.GridY ?? int.MinValue) + 1) * 4096f;
        }

        var places = new List<MapPlace>();
        var outlines = new List<float[]>();
        var buildings = new List<MapBuilding>();
        int read = 0;
        foreach (var (fk, _, body, source) in view.RecordsIn(Array.Empty<string>(), PlacedType))
        {
            ct.ThrowIfCancellationRequested();
            if (view.ParentOf(fk) is not { } ck || view.ParentOf(ck) is not { } inWorld || !related.TryGetValue(inWorld, out var how)) continue;
            if (view.ResolveWinner(fk) is not { } win || !string.Equals(win.WinnerPlugin, source, StringComparison.OrdinalIgnoreCase)) continue;
            if (body is not IPlacedObjectGetter placed || PlacementExtract.RefOf(placed) is not { } r || r.Deleted) continue;
            if (inWorld == parent && (r.Position.X < minX || r.Position.X > maxX || r.Position.Y < minY || r.Position.Y > maxY)) continue;
            read++;
            if (placed.MapMarker is { } marker)
            {
                if (marker.Name?.String is { Length: > 0 } name)
                    places.Add(new MapPlace(FormIdToken.Of(fk), name, marker.Type.ToString(), r.Position.X * how.Scale + how.OffX, r.Position.Y * how.Scale + how.OffY));
                continue;
            }
            if (!how.Buildings || r.InitiallyDisabled || r.Base is not { } bk || !boxes.TryGetValue(bk, out var box)) continue;
            outlines.Add(OutlineOf(box.Min, box.Max, r));
            buildings.Add(new MapBuilding(box.Mesh, r.Position, r.Rotation, r.Scale));
        }
        return new WorldFeatures(FormIdToken.Of(wk), places, outlines, read, clock.Elapsed.TotalSeconds) { Buildings = buildings };
    }

    /// <summary>A placed box seen from above: the convex outline of its eight corners at the reference's scale,
    /// rotation (the shared REFR convention, <see cref="SpatialOverlap.RefrRotation"/>) and position, as X,Y pairs.</summary>
    internal static float[] OutlineOf(Vec3 min, Vec3 max, PlacedRef r)
    {
        var rot = SpatialOverlap.RefrRotation(r.Rotation);
        var pts = new List<(float X, float Y)>(8);
        for (int i = 0; i < 8; i++)
        {
            var local = new System.Numerics.Vector3((i & 1) == 0 ? min.X : max.X, (i & 2) == 0 ? min.Y : max.Y, (i & 4) == 0 ? min.Z : max.Z) * r.Scale;
            var world = System.Numerics.Vector3.Transform(local, rot);
            pts.Add((world.X + r.Position.X, world.Y + r.Position.Y));
        }
        // Andrew's monotone chain
        pts.Sort((a, b) => a.X != b.X ? a.X.CompareTo(b.X) : a.Y.CompareTo(b.Y));
        static float Cross((float X, float Y) o, (float X, float Y) a, (float X, float Y) b) => (a.X - o.X) * (b.Y - o.Y) - (a.Y - o.Y) * (b.X - o.X);
        var hull = new List<(float X, float Y)>(9);
        for (int pass = 0; pass < 2; pass++)
        {
            int start = hull.Count;
            foreach (var p in pass == 0 ? pts : Enumerable.Reverse(pts))
            {
                while (hull.Count - start >= 2 && Cross(hull[^2], hull[^1], p) <= 0) hull.RemoveAt(hull.Count - 1);
                hull.Add(p);
            }
            hull.RemoveAt(hull.Count - 1);
        }
        var ring = new float[hull.Count * 2];
        for (int i = 0; i < hull.Count; i++) { ring[2 * i] = hull[i].X; ring[2 * i + 1] = hull[i].Y; }
        return ring;
    }

    /// <summary>How many records the index files under a cell; 0 for a key that is not a cell.</summary>
    public int ChildCountOf(string cellKey)
    {
        var held = Live();
        return ParseKey(cellKey) is { } ck && held.Cells.ContainsKey(ck) ? held.Resolver.Capture(null).ChildrenOf(ck).Count : 0;
    }

    /// <summary>Cells by a case-insensitive part of EditorID or name, a FormKey (a cell's, or a record's in a cell), or an exact "x,y" grid.</summary>
    public IReadOnlyList<CellRow> Search(string text, int max = 200)
    {
        var held = Live();
        var cells = Cells;
        text = (text ?? "").Trim();
        if (text.Length == 0 || max <= 0) return Array.Empty<CellRow>();
        if (GridText.Match(text) is { Success: true } m
            && int.TryParse(m.Groups[1].Value, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out int gx)
            && int.TryParse(m.Groups[2].Value, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out int gy))
            return cells.Where(c => c.GridX == gx && c.GridY == gy).Take(max).ToList();
        if (ParseKey(text) is { } fk)
        {
            if (held.Cells.TryGetValue(fk, out var self)) return new[] { self };
            return held.Resolver.Capture(null).ParentOf(fk) is { } pk && held.Cells.TryGetValue(pk, out var holder) ? new[] { holder } : Array.Empty<CellRow>();
        }
        var starts = new List<CellRow>();
        var holds = new List<CellRow>();
        foreach (var c in cells)
        {
            if (c.EditorId is { } id && id.StartsWith(text, StringComparison.OrdinalIgnoreCase)) { starts.Add(c); if (starts.Count >= max) break; }
            else if (holds.Count < max
                     && ((c.EditorId?.Contains(text, StringComparison.OrdinalIgnoreCase) ?? false)
                         || (c.Name?.Contains(text, StringComparison.OrdinalIgnoreCase) ?? false))) holds.Add(c);
        }
        return starts.Concat(holds).Take(max).ToList();
    }

    /// <summary>The grid cell of a worldspace whose square holds that position, or null.</summary>
    public string? GridCellAt(string worldspaceKey, float x, float y)
    {
        var held = Live();
        return ParseKey(worldspaceKey) is { } wk && GridCellAt(held, wk, x, y) is { } ck ? FormIdToken.Of(ck) : null;
    }

    static FormKey? GridCellAt(Held held, FormKey world, float x, float y)
    {
        int gx = (int)Math.Floor(x / NearSelection.CellUnits), gy = (int)Math.Floor(y / NearSelection.CellUnits);
        return held.Grid.TryGetValue((world, gx, gy), out var ck) ? ck : null;
    }

    /// <summary>Where a record is: itself when it is a cell, else the cell its last filing is under. Null for a key the load
    /// order does not hold. Reads one record body, so call it off the UI thread.</summary>
    public Located? Locate(string formKeyText)
    {
        var held = Live();
        var view = held.Resolver.Capture(null);
        if (ParseKey(formKeyText) is not { } fk) return null;
        if (held.Cells.TryGetValue(fk, out var self))
            return new Located(self.Key, TypeOfName("Cell"), self.Key, self.Worldspace, true, null);
        if (view.ResolveWinner(fk) is not { } winner) return null;
        using var session = view.OpenSession();
        var body = WinnerBody(view, session, winner.WinnerPlugin, fk);
        string? cell = null, world = null;
        if (view.ParentOf(fk) is { } pk && held.Cells.TryGetValue(pk, out var holder)) { cell = holder.Key; world = holder.Worldspace; }
        else if (body is IWorldspaceGetter) world = FormIdToken.Of(fk);
        return new Located(FormIdToken.Of(fk), TypeOf(body), cell, world, false, PositionOf(body));
    }

    /// <summary>The plugins that hold a version of the record, in load order (the winner last), or null when the load order
    /// has no such record.</summary>
    public IReadOnlyList<string>? TouchingPlugins(string formKeyText)
    {
        var view = Live().Resolver.Capture(null);
        return ParseKey(formKeyText) is { } fk ? view.TouchingPlugins(fk) : null;
    }

    /// <summary>The cell a scene loads to show this reference: its own cell, or for one filed in a worldspace's persistent
    /// cell the grid cell its winning position falls in. Null when neither is known. May read one record body.</summary>
    public string? OpenableCellOf(string refKey)
    {
        var held = Live();
        var view = held.Resolver.Capture(null);
        if (ParseKey(refKey) is not { } fk) return null;
        if (held.Cells.TryGetValue(fk, out var self)) return self.IsPersistentCell ? null : self.Key;
        if (view.ParentOf(fk) is not { } pk || !held.Cells.TryGetValue(pk, out var holder)) return null;
        if (!holder.IsPersistentCell) return holder.Key;
        if (view.ParentOf(pk) is not { } world || view.ResolveWinner(fk) is not { } winner) return null;
        using var session = view.OpenSession();
        return PositionOf(WinnerBody(view, session, winner.WinnerPlugin, fk)) is { } at && GridCellAt(held, world, at[0], at[1]) is { } ck
            ? FormIdToken.Of(ck) : null;
    }

    static float[]? PositionOf(IMajorRecordGetter body) =>
        body is IPlacedGetter placed && PlacementExtract.RefOf(placed) is { } r ? new[] { r.Position.X, r.Position.Y, r.Position.Z } : null;

    static IMajorRecordGetter WinnerBody(LoadOrderResolver.IndexView view, LoadOrderResolver.OverlaySession session, string plugin, FormKey fk)
    {
        IMajorRecordGetter? body;
        try { body = view.GetRecord(session, plugin, fk); }
        catch (Exception ex) when (ex is not OperationCanceledException and not OutOfMemoryException)
        {
            throw new InvalidOperationException($"could not read {FormIdToken.Of(fk)} from '{plugin}' ({ex.Message}); close whatever holds the plugin open and try again.", ex);
        }
        return body ?? throw new InvalidOperationException(
            $"'{plugin}' is the winning plugin of {FormIdToken.Of(fk)} in the index but no longer holds it; the plugin changed on disk, so open the load order again.");
    }

    // ---- a cell's records, read when the cell is opened ----

    sealed record Draft(FormKey Key, string Type, string? EditorId, FormKey? Base, string Winner,
                        bool Persistent, bool Deleted, bool InitiallyDisabled, float[]? Position, bool PlacedOrNavmesh);

    /// <summary>A cell's records at their winners, on a background thread. Persistent or temporary is the list of the winning
    /// plugin's own copy of the cell that holds the record; navmeshes and terrain are listed under temporary.</summary>
    public Task<CellObjects> ObjectsOfAsync(string cellKey, CancellationToken ct) => Task.Run(() => ObjectsOf(cellKey, ct), ct);

    CellObjects ObjectsOf(string cellKey, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var live = Live();
        var view = live.Resolver.Capture(null);
        if (ParseKey(cellKey) is not { } cellFk || !live.Cells.ContainsKey(cellFk))
            throw new ArgumentException($"'{cellKey}' is not a cell in this load order; pick a cell from the list.");
        using var session = view.OpenSession();
        var notes = new List<string>();

        // Each record goes to its winning plugin, and that plugin's own copy of the cell is read once.
        var wantedOf = new Dictionary<string, HashSet<FormKey>>(StringComparer.OrdinalIgnoreCase);
        foreach (var child in view.ChildrenOf(cellFk))
        {
            if (view.ResolveWinner(child) is not { } w) { notes.Add($"{FormIdToken.Of(child)} is filed under this cell but has no winner in the index."); continue; }
            if (!wantedOf.TryGetValue(w.WinnerPlugin, out var set)) wantedOf[w.WinnerPlugin] = set = new HashSet<FormKey>();
            set.Add(child);
        }
        var copies = new Dictionary<string, ICellGetter?>(StringComparer.OrdinalIgnoreCase);
        var drafts = new List<Draft>();
        var unfiled = new List<Draft>();
        foreach (var (plugin, wanted) in wantedOf.OrderBy(kv => view.OrderIndexOf(kv.Key)))
        {
            ct.ThrowIfCancellationRequested();
            var copy = copies[plugin] = CellCopy(view, session, plugin, cellFk);
            if (copy is not null)
            {
                foreach (var p in copy.Persistent) if (wanted.Remove(p.FormKey)) drafts.Add(DraftOf(p, true, plugin, notes));
                foreach (var p in copy.Temporary) if (wanted.Remove(p.FormKey)) drafts.Add(DraftOf(p, false, plugin, notes));
                foreach (var n in copy.NavigationMeshes) if (wanted.Remove(n.FormKey)) drafts.Add(DraftOf(n, false, plugin, notes));
                if (copy.Landscape is { } land && wanted.Remove(land.FormKey)) drafts.Add(DraftOf(land, false, plugin, notes));
            }
            if (wanted.Count == 0) continue;
            // The index files these under this cell by this plugin, and the plugin's copy of the cell does not list them.
            notes.Add(copy is null
                ? $"'{plugin}' wins {wanted.Count} record(s) filed under this cell but holds no copy of the cell that could be read, so they are listed as unfiled."
                : $"'{plugin}' wins {wanted.Count} record(s) filed under this cell that its copy of the cell does not list (for example {FormIdToken.Of(wanted.First())}), so they are listed as unfiled.");
            var bodies = WinnerBodies.For(view, session, wanted, null, out var lost, ct);
            foreach (var (name, fault) in lost) notes.Add($"could not read '{name}': {fault.Message}");
            foreach (var fk in wanted)
                unfiled.Add(bodies.TryGetValue(fk, out var body)
                    ? DraftOf(body, false, plugin, notes)
                    : new Draft(fk, "?", null, null, plugin, false, false, false, null, false));
        }

        // The bases, each at its own winner.
        ct.ThrowIfCancellationRequested();
        var baseKeys = drafts.Concat(unfiled).Where(d => d.Base is not null).Select(d => d.Base!.Value).Distinct().ToList();
        var baseBodies = WinnerBodies.For(view, session, baseKeys, null, out var unreadable, ct);
        foreach (var (name, fault) in unreadable) notes.Add($"base objects in '{name}' could not be read: {fault.Message}");
        var bases = new Dictionary<FormKey, (string? EditorId, string Type)>(baseBodies.Count);
        foreach (var (fk, body) in baseBodies) bases[fk] = (EditorIdOf(body), TypeOf(body, notes));

        // A scene finds a cell when some plugin's copy has a placed object or a navmesh (PlacementExtract.FromMod's rule).
        bool findable = drafts.Concat(unfiled).Any(d => d.PlacedOrNavmesh);
        if (!findable)
            foreach (var plugin in view.TouchingPlugins(cellFk) ?? Array.Empty<string>())
            {
                ct.ThrowIfCancellationRequested();
                if (!copies.TryGetValue(plugin, out var copy)) copy = copies[plugin] = CellCopy(view, session, plugin, cellFk);
                if (copy is null) continue;
                var own = PlacementExtract.FromCell(copy, FormKey.Null);
                if (own.Refs.Count > 0 || own.Navmeshes.Count > 0) { findable = true; break; }
            }

        ct.ThrowIfCancellationRequested();
        ObjectRow Row(Draft d)
        {
            (string? EditorId, string Type)? b = d.Base is { } bk && bases.TryGetValue(bk, out var hit) ? hit : null;
            return new ObjectRow(FormIdToken.Of(d.Key), d.Type, d.EditorId, d.Base is { } k ? FormIdToken.Of(k) : null,
                b?.EditorId, b?.Type, d.Winner, d.Persistent, d.Deleted, d.InitiallyDisabled, d.Position);
        }
        List<ObjectRow> Rows(IEnumerable<Draft> of) =>
            of.OrderBy(d => view.OrderIndexOf(d.Key.ModKey.FileName.String)).ThenBy(d => d.Key.ID).Select(Row).ToList();
        return new CellObjects(Rows(drafts.Where(d => d.Persistent)), Rows(drafts.Where(d => !d.Persistent)), findable)
        {
            Unfiled = Rows(unfiled), Notes = notes,
        };
    }

    /// <summary>One plugin's own copy of a cell, its child lists with it; null when the plugin holds none.</summary>
    static ICellGetter? CellCopy(LoadOrderResolver.IndexView view, LoadOrderResolver.OverlaySession session, string plugin, FormKey cell)
    {
        try { return view.GetRecord(session, plugin, cell, typeof(ICellGetter)) as ICellGetter; }
        catch (Exception ex) when (ex is not OperationCanceledException and not OutOfMemoryException)
        {
            throw new InvalidOperationException($"could not read cell {FormIdToken.Of(cell)} from '{plugin}' ({ex.Message}); close whatever holds the plugin open and open the cell again.", ex);
        }
    }

    Draft DraftOf(IMajorRecordGetter rec, bool persistent, string winner, List<string> notes)
    {
        string type = TypeOf(rec, notes);
        bool placedOrNavmesh = rec is IPlacedGetter or INavigationMeshGetter;
        try
        {
            FormKey? baseKey = null;
            float[]? position = null;
            bool disabled = false;
            if (rec is IPlacedGetter placed)
            {
                baseKey = PlacementExtract.BaseOf(placed);
                if (PlacementExtract.RefOf(placed, persistent) is { } r)
                {
                    position = new[] { r.Position.X, r.Position.Y, r.Position.Z };
                    disabled = r.InitiallyDisabled;
                }
                else   // no placement (a deletion usually strips it): the flag is read as PlacementExtract reads it
                    disabled = placed is ISkyrimMajorRecordGetter smr
                               && smr.SkyrimMajorRecordFlags.HasFlag(SkyrimMajorRecord.SkyrimMajorRecordFlag.InitiallyDisabled);
            }
            return new Draft(rec.FormKey, type, rec.EditorID, baseKey, winner, persistent, rec.IsDeleted, disabled, position, placedOrNavmesh);
        }
        catch (Exception ex) when (ex is not OperationCanceledException and not OutOfMemoryException)
        {
            notes.Add($"{FormIdToken.Of(rec.FormKey)} in '{winner}' could not be read in full ({ex.Message}), so it is listed by type only.");
            return new Draft(rec.FormKey, type, null, null, winner, persistent, false, false, null, placedOrNavmesh);
        }
    }

    static string? EditorIdOf(IMajorRecordGetter body)
    {
        try { return body.EditorID; }
        catch (Exception ex) when (ex is not OperationCanceledException and not OutOfMemoryException) { return null; }
    }

    /// <summary>Drops the index. No plugin file is held open at rest, so this only releases memory.</summary>
    public void Dispose()
    {
        var held = Interlocked.Exchange(ref _held, null);
        if (held is null) return;
        Worldspaces = Array.Empty<WorldspaceRow>(); Cells = Array.Empty<CellRow>(); Interiors = Array.Empty<CellRow>();
        held.Resolver.Dispose();
    }
}
