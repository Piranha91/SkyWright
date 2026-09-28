using HarnessShared;
using HousecarlCore;
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
    public readonly Dictionary<FormKey, PlacedRef> WinRef = new();                  // ref FormKey -> winning placement
    public readonly Dictionary<FormKey, FormKey> WinRefCell = new();                // ref FormKey -> winning cell
    // The LAST cell any plugin placed a tracked reference in. `WinRefCell` remembers the last TARGET cell it was in, so a
    // later plugin that relocates it out of the target's cells leaves that stale; the navcut pass reads this instead, and a
    // box moved elsewhere is not measured against a navmesh it no longer stands on (the whole-order reading --state had).
    public readonly Dictionary<FormKey, FormKey> LastCellOf = new();
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

    /// <summary>Open one plugin of the order, or null when it cannot be read.</summary>
    public static ISkyrimModGetter? Open(string path)
    {
        try { return SkyrimMod.CreateFromBinaryOverlay(path, SkyrimRelease.SkyrimSE); } catch { return null; }
    }

    /// <summary>One plugin's cells, in load order. <paramref name="perCell"/> runs first for every cell (the caller's own
    /// per-cell work).</summary>
    public void Plugin(ISkyrimModGetter m, string name, Action<CellPlacements> perCell)
    {
        foreach (var cp in LastSighting.Cells(m))
        {
            perCell(cp);
            Floor.Observe(cp, name);
            bool targetCell = TargetCells.ContainsKey(cp.Cell);
            foreach (var r in cp.Refs) if (targetCell || LastCellOf.ContainsKey(r.FormKey)) LastCellOf[r.FormKey] = cp.Cell;
            if (!targetCell) continue;
            CellMeta[cp.Cell] = cp;
            foreach (var r in cp.Refs) { WinRef[r.FormKey] = r; WinRefCell[r.FormKey] = cp.Cell; DefinedBy.TryAdd(r.FormKey, name); }
            foreach (var nav in cp.Navmeshes) WinNav[nav.FormKey] = (nav, name, cp.Cell);
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
            winRef = WinRef.Values.OrderBy(r => r.FormKey.ToString(), StringComparer.Ordinal).Select(r => WinStateDump.Entry(r, WinRefCell[r.FormKey])).ToList(),
            floor = FloorRefsByWorld.Select(w => WinStateDump.Floor(w.Key, w.Value, Floor.LandOf(w.Key), Floor.UnreadableLand.Where(u => u.Ws == w.Key).Select(u => (u.X, u.Y)))).ToList(),
        });
    }
}
