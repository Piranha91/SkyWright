using HarnessShared;
using HousecarlCore;
using SkyWright.Engine;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Skyrim;

// THE WINNING STATE OF THE TARGET'S CELLS, as blockprobe's load-order walk builds it (Program.cs, step 2). Held in its
// own class so --moved-ref-selftest runs exactly the code a real run runs; Program.cs keeps every other job of the
// walk (the swap layer, the enable facts, the base records) in its own loop and hands each cell here.
sealed class BlockWalk
{
    public readonly Dictionary<FormKey, CellPlacements> TargetCells;
    /// <summary>The 3x3 grid neighbourhood of every exterior target cell: where the floor under them is filed from.</summary>
    public readonly HashSet<(FormKey Wrld, int X, int Y)> FloorGrids = new();
    /// <summary>The floor filing (FloorFeed.FloorIndex): floor candidates by position inside <see cref="FloorGrids"/>.</summary>
    public readonly FloorFeed.FloorIndex Floor;
    public Dictionary<FormKey, Dictionary<(int, int), Dictionary<FormKey, PlacedRef>>> FloorRefsByWorld => Floor.RefsByWorld;
    public Dictionary<FormKey, Dictionary<(int, int), float[,]>> FloorLandByWorld => Floor.LandByWorld;
    public IEnumerable<(FormKey Ws, int X, int Y)> UnreadableLand => Floor.UnreadableLand;
    /// <summary>The winning state of the target cells under the last-sighting rule (_shared/LastSighting.cs): a reference
    /// is held iff its last sighting stands in a target cell, at that sighting; a later plugin that moves it out of those
    /// cells or deletes it takes it out.</summary>
    readonly LastSightingSet _win = new();
    public Dictionary<FormKey, PlacedRef> WinRef => _win.Refs;                      // ref FormKey -> winning placement
    public Dictionary<FormKey, FormKey> WinRefCell => _win.CellOf;                  // ref FormKey -> winning cell
    /// <summary>References a target cell held until a later plugin moved them out or deleted them: not judged.</summary>
    public int TakenOutCount => _win.TakenOut.Count;
    /// <summary>The target cells' navmeshes under the same rule (LastSightingNavSet): a navmesh is held iff its last
    /// sighting stands in a target cell, with its geometry; moved out of those cells or deleted (with or without its
    /// geometry), it is gone. With the plugin of that sighting and its cell. A live table, kept in step with the set as
    /// the walk goes: Program.cs takes it before the walk.</summary>
    readonly LastSightingNavSet _navs = new();
    public readonly Dictionary<FormKey, (NavmeshGeometry Nav, string Plugin, FormKey Cell)> WinNav = new();
    public readonly Dictionary<FormKey, CellPlacements> CellMeta = new();
    public readonly Dictionary<FormKey, string> DefinedBy = new();     // ref -> the first plugin that placed it, for the {NAVCUT by …} line

    public BlockWalk(Dictionary<FormKey, CellPlacements> targetCells)
    {
        TargetCells = targetCells;
        foreach (var tc in targetCells.Values)
            if (!tc.Interior && tc.Grid is { } tg)
                for (int dx = -1; dx <= 1; dx++) for (int dy = -1; dy <= 1; dy++) FloorGrids.Add((tc.Worldspace, tg.X + dx, tg.Y + dy));
        Floor = new FloorFeed.FloorIndex((ws, g) => FloorGrids.Contains((ws, g.X, g.Y)));
    }

    /// <summary>Open one plugin of the order, or throw PluginUnreadableException (refused, never skipped).</summary>
    public static ISkyrimModGetter? Open(string path) => PluginOpen.Open(path, "blockprobe's walk of the load order");

    /// <summary>One plugin's cells, in load order. <paramref name="perCell"/> runs first for every cell (the caller's own
    /// per-cell work).</summary>
    public void Plugin(ISkyrimModGetter m, string name, Action<CellPlacements> perCell) =>
        PluginOpen.Guard(name, "blockprobe's walk of the load order", () => Walk(m, name, perCell));

    void Walk(ISkyrimModGetter m, string name, Action<CellPlacements> perCell)
    {
        Floor.ObserveMod(m);   // the floor reach's lists and base boxes, before the plugin's cells (FloorFeed)
        foreach (var cp in LastSighting.Cells(m))
        {
            perCell(cp);
            Floor.Observe(cp, name);
            bool targetCell = TargetCells.ContainsKey(cp.Cell);
            _win.Observe(cp, (c, _) => targetCell);
            foreach (var k in _navs.Observe(cp, (_, _) => targetCell))
                if (_navs.Navs.TryGetValue(k, out var nav)) WinNav[k] = (nav, name, _navs.CellOf[k]); else WinNav.Remove(k);
            if (!targetCell) continue;
            CellMeta[cp.Cell] = cp;
            foreach (var r in cp.Refs) DefinedBy.TryAdd(r.FormKey, name);
        }
    }

    /// <summary>HOUSECARL_WINSTATE_DUMP: the winning state and the floor filing, for an independent reader.</summary>
    public void Dump()
    {
        if (!WinStateDump.Asked) return;
        WinStateDump.Write("blockprobe", new
        {
            targetCells = TargetCells.Keys.Select(k => k.ToString()).OrderBy(k => k, StringComparer.Ordinal).ToList(),
            floorGrids = FloorGrids.OrderBy(g => g.Wrld.ToString()).ThenBy(g => g.X).ThenBy(g => g.Y)
                                   .Select(g => new { worldspace = g.Wrld.ToString(), x = g.X, y = g.Y }).ToList(),
            winNav = WinNav.OrderBy(n => n.Key.ToString(), StringComparer.Ordinal)
                           .Select(n => WinStateDump.Nav(n.Value.Nav, n.Value.Cell, n.Value.Plugin)).ToList(),
            takenOut = _win.TakenOut.Select(k => k.ToString()).OrderBy(k => k, StringComparer.Ordinal).ToList(),
            winRef = WinRef.Values.OrderBy(r => r.FormKey.ToString(), StringComparer.Ordinal).Select(r => WinStateDump.Entry(r, WinRefCell[r.FormKey])).ToList(),
            floor = FloorRefsByWorld.Select(w => WinStateDump.Floor(w.Key, w.Value, Floor.LandOf(w.Key), Floor.UnreadableLand.Where(u => u.Ws == w.Key).Select(u => (u.X, u.Y)))).ToList(),
        });
    }
}
