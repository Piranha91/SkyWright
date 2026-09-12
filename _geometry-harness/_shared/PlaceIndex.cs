using System.Diagnostics;
using HousecarlCore;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Skyrim;

namespace HarnessShared;

// PLACE INDEX — where each contested record is FILED, read off the group tree of the first plugin in the load
// order that carries it (roadmap 4.20, place-based grouping, 2026-09-12).
//
// The resolver's index knows a record's winner and its carriers and nothing else: Mutagen's flat enumeration
// exposes no parent, so a record's cell is a fact about the FILE — the cell-children group it sits in — and it
// comes from a hierarchical walk of each plugin's Cells and Worldspaces trees, the walk CellBoundaryIndex already
// makes for water and land. This walk opens every plugin of the baseline once more, one at a time, through the
// core's OpenOverlay (the same strings-correct open the index used), and keeps only what the search consumes:
//
//   the PLACE of a record   filed under a CELL (persistent, temporary or children group) — a placed reference of
//                           any kind, a NAVM, the LAND — the place is that cell; a CELL record's place is the cell
//                           itself; a WRLD record's place is the worldspace itself; every other record (items,
//                           spells, keywords, quests, dialogue, NAVI, LCTN, …) has NO place.
//   FIRST SIGHTING wins     the place is where the FIRST plugin in the load order that carries the record files
//                           it. A later plugin filing the same record under another cell does not move it; the
//                           disagreement is counted (PlaceDisputed) and the first 20 are listed by text.
//   a cell's identity       its FormKey. Its KIND (interior / exterior with its XCLC grid / the worldspace's
//                           persistent cell, gridless whatever its XCLC says), its worldspace and its grid are its
//                           first sighting's. Its NAME is display only: the first non-empty EditorID any plugin in
//                           load order gives it, else the FormKey spelled XXXXXX:Plugin.esp.
//
// Values are COPIED out of the getters; every overlay is disposed at the end of its plugin's walk. A retained
// getter is a use-after-free that happens to work until it doesn't. A plugin the index excluded files nothing and
// is skipped by name; a plugin THIS walk cannot open or walk is counted and named (WalkFailures), its filings
// discarded whole so a half-walked plugin never leaves half its records placed — the same plugin-atomic rule the
// index build keeps.

public sealed class PlaceIndex
{
    sealed class PlaceRec
    {
        public FormKey Key;
        public string Kind = "";                 // interior | exterior | persistent | worldspace
        public FormKey Worldspace;               // Null for an interior and for a worldspace place
        public bool HasGrid;
        public int X, Y;
        public string? EditorId;                 // the first non-empty EditorID in load order
        public PlaceDescriptor? Descriptor;
    }

    readonly Dictionary<FormKey, (FormKey Place, int Plugin)> _first = new();
    readonly Dictionary<FormKey, PlaceRec> _places = new();
    readonly List<(FormKey Rec, int P1, FormKey Place1, int P2, FormKey Place2)> _disputes = new();
    readonly HashSet<FormKey> _disputed = new();
    readonly List<string> _names = new();
    readonly List<string> _failures = new();

    /// <summary>RECORDS whose place a later plugin disagreed with — each counted once, on its first disagreement,
    /// however many later plugins file it elsewhere; the record keeps its first sighting's place. The number of
    /// disagreeing filings is <see cref="DisputedFilings"/>.</summary>
    public int PlaceDisputed { get; private set; }
    /// <summary>Filings that disagreed with a record's first sighting — one per later plugin per record.</summary>
    public int DisputedFilings { get; private set; }
    /// <summary>Plugins this walk could not open or walk, named with the reason. Never skipped silently.</summary>
    public IReadOnlyList<string> WalkFailures => _failures;
    public int PluginsWalked { get; private set; }
    /// <summary>Plugins the index excluded (unopenable, unparseable) — they file nothing and were not opened here.</summary>
    public int PluginsSkippedAsExcluded { get; private set; }
    /// <summary>Records given a place (first sightings kept), and LAND records whose getter threw.</summary>
    public long RecordsPlaced { get; private set; }
    public int LandscapeUnreadable { get; private set; }
    public int CellsSeen => _places.Values.Count(p => p.Kind != "worldspace");
    public int WorldspacesSeen => _places.Values.Count(p => p.Kind == "worldspace");
    public TimeSpan WallTime { get; private set; }

    /// <summary>Walk every plugin in load order. <paramref name="keep"/> restricts the records whose place is kept to
    /// the contested set (a record one plugin carries has no pair to weigh, and on a 1,700-plugin order the
    /// unrestricted map is millions of entries); null keeps every record. <paramref name="skip"/> names the plugins
    /// the index excluded, which file nothing.</summary>
    public static PlaceIndex Build(IReadOnlyList<string> paths, string? dataDir,
                                   IReadOnlySet<FormKey>? keep = null, IReadOnlySet<string>? skip = null)
    {
        var sw = Stopwatch.StartNew();
        var ix = new PlaceIndex();
        for (int p = 0; p < paths.Count; p++)
        {
            var name = Path.GetFileName(paths[p]);
            ix._names.Add(name);
            if (skip is not null && skip.Contains(name)) { ix.PluginsSkippedAsExcluded++; continue; }
            ISkyrimModGetter mod;
            try { mod = LoadOrderResolver.OpenOverlay(paths[p], dataDir); }
            catch (Exception ex)
            {
                ix._failures.Add($"'{name}' could not be opened for the place walk — {Concise(ex)}; it files nothing here, so a record whose first sighting is in it reads as the next carrier's filing, or as no place");
                continue;
            }
            var sightings = new List<(FormKey Rec, FormKey Place)>();
            var cells = new List<(FormKey Key, string Kind, FormKey Ws, bool HasGrid, int X, int Y, string? Edid)>();
            try
            {
                ix.WalkPlugin(mod, keep, sightings, cells);
            }
            catch (Exception ex)
            {
                ix._failures.Add($"'{name}' could not be walked for its places — {Concise(ex)}; its filings were discarded whole, so a record whose first sighting is in it reads as the next carrier's filing, or as no place");
                continue;
            }
            finally { (mod as IDisposable)?.Dispose(); }

            ix.PluginsWalked++;
            foreach (var c in cells) ix.Register(c.Key, c.Kind, c.Ws, c.HasGrid, c.X, c.Y, c.Edid);
            foreach (var (rec, place) in sightings)
            {
                if (!ix._first.TryGetValue(rec, out var prev)) { ix._first[rec] = (place, p); ix.RecordsPlaced++; }
                else if (prev.Place != place)
                {
                    ix.DisputedFilings++;
                    if (ix._disputed.Add(rec)) ix.PlaceDisputed++;          // a record counts once, on its first disagreement
                    if (ix._disputes.Count < 20) ix._disputes.Add((rec, prev.Plugin, prev.Place, p, place));
                }
            }
        }
        ix.WallTime = sw.Elapsed;
        return ix;
    }

    void WalkPlugin(ISkyrimModGetter mod, IReadOnlySet<FormKey>? keep,
                    List<(FormKey Rec, FormKey Place)> sightings,
                    List<(FormKey Key, string Kind, FormKey Ws, bool HasGrid, int X, int Y, string? Edid)> cells)
    {
        void Sight(FormKey rec, FormKey place)
        {
            if (keep is null || keep.Contains(rec)) sightings.Add((rec, place));
        }
        void Cell(ICellGetter c, string kind, FormKey ws)
        {
            bool hasGrid = false; int gx = 0, gy = 0;
            if (kind == "exterior" && c.Grid?.Point is { } gp) { hasGrid = true; gx = gp.X; gy = gp.Y; }
            cells.Add((c.FormKey, kind, ws, hasGrid, gx, gy, string.IsNullOrEmpty(c.EditorID) ? null : c.EditorID));
            Sight(c.FormKey, c.FormKey);                       // the CELL record lands on its own cell
            foreach (var r in c.Persistent) Sight(r.FormKey, c.FormKey);
            foreach (var r in c.Temporary) Sight(r.FormKey, c.FormKey);
            foreach (var n in c.NavigationMeshes) Sight(n.FormKey, c.FormKey);
            ILandscapeGetter? land = null;
            try { land = c.Landscape; } catch { LandscapeUnreadable++; }
            if (land is not null) Sight(land.FormKey, c.FormKey);
        }

        foreach (var ws in mod.Worldspaces.Records)
        {
            cells.Add((ws.FormKey, "worldspace", FormKey.Null, false, 0, 0, string.IsNullOrEmpty(ws.EditorID) ? null : ws.EditorID));
            Sight(ws.FormKey, ws.FormKey);                    // the WRLD record's place is the worldspace itself
            if (ws.TopCell is { } tc) Cell(tc, "persistent", ws.FormKey);
            foreach (var blk in ws.SubCells)
                foreach (var sub in blk.Items)
                    foreach (var c in sub.Items)
                        Cell(c, "exterior", ws.FormKey);
        }
        foreach (var blk in mod.Cells.Records)
            foreach (var sub in blk.SubBlocks)
                foreach (var c in sub.Cells)
                    Cell(c, "interior", FormKey.Null);
    }

    void Register(FormKey key, string kind, FormKey ws, bool hasGrid, int x, int y, string? edid)
    {
        if (!_places.TryGetValue(key, out var rec))
            _places[key] = rec = new PlaceRec { Key = key, Kind = kind, Worldspace = ws, HasGrid = hasGrid, X = x, Y = y };
        // kind, worldspace and grid are the first sighting's; the name is the first NON-EMPTY EditorID in load order
        if (rec.EditorId is null && edid is not null) rec.EditorId = edid;
    }

    /// <summary>The place of a record by its first sighting, or null when it has none (or was not kept).</summary>
    public PlaceDescriptor? PlaceOf(FormKey rec) => _first.TryGetValue(rec, out var f) ? Describe(f.Place) : null;

    /// <summary>The plugin whose filing gave the record its place — for a proof to read the first-sighting rule.</summary>
    public string? FirstSightingOf(FormKey rec) => _first.TryGetValue(rec, out var f) ? _names[f.Plugin] : null;

    /// <summary>D-1: whether a placed record is one of the two worldspace-wide CONTAINERS — a WRLD record, or a
    /// persistent cell's own CELL record — which any plugin filing anything in the worldspace carries, and which
    /// therefore count toward a pair's place contention but not its cell contention. A record is one when its place is
    /// of kind worldspace, or its place is a persistent cell and the record IS that cell.</summary>
    public bool IsWorldwideContainer(FormKey rec)
    {
        if (!_first.TryGetValue(rec, out var f) || !_places.TryGetValue(f.Place, out var p)) return false;
        return p.Kind == "worldspace" || (p.Kind == "persistent" && f.Place == rec);
    }

    /// <summary>A cell or worldspace as a place, by its FormKey; null when the walk never saw it.</summary>
    public PlaceDescriptor? Describe(FormKey place)
    {
        if (!_places.TryGetValue(place, out var rec)) return null;
        if (rec.Descriptor is not null) return rec.Descriptor;
        string? wsId = null, wsName = null;
        if (!rec.Worldspace.IsNull)
        {
            wsId = rec.Worldspace.ToString();
            wsName = _places.TryGetValue(rec.Worldspace, out var w) ? NameOf(w) : wsId;
        }
        return rec.Descriptor = new PlaceDescriptor(rec.Key.ToString(), rec.Kind, NameOf(rec), wsId, wsName,
                                                    rec.HasGrid ? rec.X : null, rec.HasGrid ? rec.Y : null);
    }

    static string NameOf(PlaceRec r) => r.EditorId ?? r.Key.ToString();

    /// <summary>The first 20 disputed FILINGS (a record two later plugins file elsewhere appears twice), spelled as the
    /// manifest carries them:
    /// <c>&lt;FormKey&gt;: '&lt;plugin1&gt;' files it in &lt;cell1&gt;, '&lt;plugin2&gt;' in &lt;cell2&gt;</c>.</summary>
    public IReadOnlyList<string> DisputeNotes => _disputes
        .Select(d => $"{d.Rec}: '{_names[d.P1]}' files it in {PlaceName(d.Place1)}, '{_names[d.P2]}' in {PlaceName(d.Place2)}")
        .ToList();

    string PlaceName(FormKey place) => _places.TryGetValue(place, out var r) ? NameOf(r) : place.ToString();

    static string Concise(Exception ex)
    {
        var s = ex.Message.Replace("\r", "").Replace("\n", " | ").Trim();
        return s.Length > 300 ? s[..300] + "…" : s;
    }

    // -----------------------------------------------------------------------------------------------------
    //  Weighing the contested set with its places — the one loop orderprobe and the proof both run.
    // -----------------------------------------------------------------------------------------------------

    /// <summary>Walk the index's contested set once: every record two or more plugins carry, identical copies
    /// included. For each, <paramref name="bumpRecord"/> is called per pair of carriers (the record edge, exactly as
    /// before), and the pair's place / no-place counts are kept in the returned evidence with the record's place
    /// (<see cref="PlaceOf"/>) — the second dimension on the same records, never a new contested set.</summary>
    public PlaceEvidence Weigh(LoadOrderResolver.IndexView view, Action<string, string> bumpRecord, out int contestedRecords)
    {
        var ev = new PlaceEvidence { PlaceDisputed = PlaceDisputed };
        contestedRecords = 0;
        foreach (var fk in view.ConflictKeys())
        {
            var touching = view.TouchingPlugins(fk);
            if (touching is null || touching.Count < 2) continue;
            contestedRecords++;
            var place = PlaceOf(fk);
            bool container = place is not null && IsWorldwideContainer(fk);
            ev.CountRecord(place, container);
            for (int a = 0; a < touching.Count; a++)
                for (int b = a + 1; b < touching.Count; b++)
                {
                    bumpRecord(touching[a], touching[b]);
                    ev.Add(touching[a], touching[b], place, container);
                }
        }
        return ev;
    }
}
