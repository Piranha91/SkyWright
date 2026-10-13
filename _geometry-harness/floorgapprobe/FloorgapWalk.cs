using HarnessShared;
using HousecarlCore;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Skyrim;

// THE WALK THAT FEEDS floorgapprobe: every cell of every walked plugin, filed for the floor neighbourhood, and under
// --winning the one wanted cell merged over the load order. Held in its own class so --moved-ref-selftest runs exactly
// the code a real run runs; Program.cs keeps the base records and worldspace names in its own loop.
sealed class FloorgapWalk
{
    public readonly bool Winning;
    public readonly string? WantCell;
    public FormKey? WantKey;
    /// <summary>The floor filing (FloorFeed.FloorIndex) over every exterior cell.</summary>
    public readonly FloorFeed.FloorIndex Floor = new();
    public Dictionary<FormKey, Dictionary<(int, int), Dictionary<FormKey, PlacedRef>>> RefsByWorld => Floor.RefsByWorld;
    public Dictionary<FormKey, Dictionary<(int, int), float[,]>> LandByWorld => Floor.LandByWorld;
    public IEnumerable<(FormKey Ws, int X, int Y)> UnreadableLand => Floor.UnreadableLand;
    public readonly List<CellPlacements> Cells = new();
    // --winning: only the named cell is merged. Its references under the last-sighting rule (_shared/LastSighting.cs): a
    // reference is held iff its last sighting stands in the wanted cell, so a later plugin that moves it out or deletes it
    // takes it out. The meta from the first plugin that
    // carries the cell; the LAND from the last that has one, and a last LAND that gives no heights is named
    // (MergedLandUnreadable), not replaced by an earlier one; a last LAND that declares no heights leaves none (no terrain
    // there) and no name. Its navmeshes under the same rule (LastSightingNavSet): a later plugin that moves one out or
    // deletes it, with or without its geometry, takes it out.
    public CellPlacements? MergedMeta;
    public float[,]? MergedLand;
    public string? MergedLandUnreadable;
    readonly LastSightingSet _merged = new();
    public Dictionary<FormKey, PlacedRef> MergedRefs => _merged.Refs;
    readonly LastSightingNavSet _mergedNavs = new();
    public Dictionary<FormKey, NavmeshGeometry> MergedNavs => _mergedNavs.Navs;
    public readonly List<string> PluginsWithCell = new();
    // A worldspace's PERSISTENT cell (its TopCell, read from the plugin structure, not from a grid: the record carries a
    // grid too) holds references the engine keeps loaded wherever they stand. Their last sightings, kept apart from the
    // merged cell: a later sighting in any grid cell takes one out (LastSightingSet), and PersistentStandingIn files them
    // by position for reach. The merged cell and the floor filing are unchanged by it.
    readonly HashSet<FormKey> _topCells = new();
    readonly Dictionary<FormKey, FormKey> _topCellWorld = new();
    readonly LastSightingSet _persistentTop = new();

    public FloorgapWalk(bool winning, string? wantCell)
    {
        Winning = winning; WantCell = wantCell;
        if (wantCell is not null) { try { WantKey = FormKey.Factory(wantCell); } catch { } }
    }

    /// <summary>Open one plugin, or throw PluginUnreadableException: refused, never skipped (an unreadable plugin under
    /// --winning would leave an earlier plugin's version of every record it overrides).</summary>
    public ISkyrimModGetter? Open(string path) => PluginOpen.Open(path, Winning ? "floorgapprobe's --winning walk" : "floorgapprobe");

    /// <summary>One plugin's cells, in load order. <paramref name="perCell"/> runs first for every cell.</summary>
    public void Plugin(ISkyrimModGetter mod, string path, Action<CellPlacements> perCell) =>
        PluginOpen.Guard(path, Winning ? "floorgapprobe's --winning walk" : "floorgapprobe", () => Walk(mod, path, perCell));

    void Walk(ISkyrimModGetter mod, string path, Action<CellPlacements> perCell)
    {
        Floor.ObserveMod(mod);   // the floor reach's lists and base boxes, before the plugin's cells (FloorFeed)
        foreach (var wrld in mod.Worldspaces.Records)
            if (wrld.TopCell is { } top) { _topCells.Add(top.FormKey); _topCellWorld[top.FormKey] = wrld.FormKey; }
        foreach (var cp in LastSighting.Cells(mod))
        {
            perCell(cp);
            Floor.Observe(cp, Path.GetFileName(path));
            _persistentTop.Observe(cp, (c, _) => _topCells.Contains(c.Cell));
            // The single-plugin modes sweep the plugin's cells with content, as before: an empty cell copy has nothing to
            // measure.
            if (!Winning) { if (cp.Refs.Count > 0 || cp.Navmeshes.Count > 0) Cells.Add(cp); continue; }
            bool isWanted = (WantKey is { } wk && cp.Cell == wk) || (WantCell is not null && string.Equals(cp.CellEditorId, WantCell, StringComparison.OrdinalIgnoreCase));
            _merged.Observe(cp, (_, _) => isWanted);
            _mergedNavs.Observe(cp, (_, _) => isWanted);
            if (!isWanted) continue;
            WantKey ??= cp.Cell;
            MergedMeta ??= cp;
            if (cp.LandUnreadableReason is { } why) { MergedLand = null; MergedLandUnreadable = $"{why} ({Path.GetFileName(path)})"; }
            else if (cp.LandDeclaresNoHeights) { MergedLand = null; MergedLandUnreadable = null; }   // no terrain here (FloorIndex)
            else if (cp.Land is not null) { MergedLand = cp.Land; MergedLandUnreadable = null; }
            PluginsWithCell.Add(Path.GetFileName(path));
        }
    }

    /// <summary>The references whose last sighting is in <paramref name="cell"/>'s worldspace's persistent cell and whose
    /// position falls in <paramref name="cell"/>'s grid: they stand in that cell although no plugin files them there. Empty
    /// for an interior, a cell with no grid, or the persistent cell itself.</summary>
    public Dictionary<FormKey, PlacedRef> PersistentStandingIn(CellPlacements cell)
    {
        var result = new Dictionary<FormKey, PlacedRef>();
        if (cell.Interior || cell.Grid is not { } g || _topCells.Contains(cell.Cell)) return result;
        foreach (var (key, r) in _persistentTop.Refs)
            if (_persistentTop.CellOf.TryGetValue(key, out var top) && _topCellWorld.TryGetValue(top, out var ws)
                && ws == cell.Worldspace && FloorFeed.GridOf(r.Position) == g)
                result[key] = r;
        return result;
    }

    /// <summary>HOUSECARL_WINSTATE_DUMP: the merged cell and the floor filing, for an independent reader.</summary>
    public void Dump()
    {
        if (!WinStateDump.Asked) return;
        WinStateDump.Write("floorgapprobe", new
        {
            winning = Winning, wantCell = WantKey?.ToString() ?? WantCell,
            merged = MergedRefs.Values.OrderBy(r => r.FormKey.ToString(), StringComparer.Ordinal).Select(r => WinStateDump.Entry(r, WantKey)).ToList(),
            mergedNavs = MergedNavs.Values.OrderBy(n => n.FormKey.ToString(), StringComparer.Ordinal).Select(n => WinStateDump.Nav(n, WantKey, null)).ToList(),
            mergedLand = MergedLand is { } ml ? new { h1616 = ml[16, 16] } : null,
            floor = RefsByWorld.Select(w => WinStateDump.Floor(w.Key, w.Value, Floor.LandOf(w.Key), Floor.UnreadableLand.Where(u => u.Ws == w.Key).Select(u => (u.X, u.Y)))).ToList(),
        });
    }
}
