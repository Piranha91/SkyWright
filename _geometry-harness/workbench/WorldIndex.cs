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
        say?.Report($"Indexing {paths.Length} plugins…");
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
        if (Environment.GetEnvironmentVariable("WB_CORPUS") is { Length: > 0 } env) return env;
        try { return Program.DefaultCorpus(); }
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
                    say.Report($"Reading cells: {source} ({pluginsRead} of {Plugins.Count} plugins)…");
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
