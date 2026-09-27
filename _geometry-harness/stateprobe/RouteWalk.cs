using HarnessShared;
using HousecarlCore;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Skyrim;

// THE ROUTE ORACLE'S WALK (stateprobe --route on): the winning navmesh and the placed references of every wanted cell,
// and the floor neighbourhood of every wanted exterior cell. Held in its own class so --moved-ref-selftest runs exactly
// the code a real run runs; Program.cs keeps the actor records in its own loop.
sealed class RouteWalk
{
    public readonly HashSet<FormKey> WantedCells;
    public readonly Dictionary<FormKey, Dictionary<FormKey, NavmeshGeometry>> WinNav = new();      // cell -> navmesh key -> geometry (winner)
    public readonly Dictionary<FormKey, List<PlacedRef>> CellRefs = new();
    // The floor under a wanted exterior cell reaches across its borders (Q24, 2026-09-05 — _shared/FloorFeed.cs): the 3×3
    // neighbourhood's refs and LAND are filed by worldspace and grid during the walk. A wanted cell's grid is learned from
    // the first plugin that defines it (Skyrim.esm for all but a mod's own cells), read ahead of the walk below so the
    // first plugin's own neighbours are filed too.
    public readonly Dictionary<FormKey, (FormKey Wrld, int X, int Y)> CellGridOf = new();
    public readonly HashSet<(FormKey Wrld, int X, int Y)> FloorGrids = new();
    public readonly Dictionary<FormKey, Dictionary<(int, int), Dictionary<FormKey, PlacedRef>>> FloorRefsByWorld = new();
    public readonly Dictionary<FormKey, Dictionary<(int, int), float[,]>> FloorLandByWorld = new();

    public RouteWalk(HashSet<FormKey> wantedCells) { WantedCells = wantedCells; }

    void LearnGrid(CellPlacements cp)
    {
        if (cp.Interior || cp.Grid is not { } g || !WantedCells.Contains(cp.Cell) || CellGridOf.ContainsKey(cp.Cell)) return;
        CellGridOf[cp.Cell] = (cp.Worldspace, g.X, g.Y);
        for (int dx = -1; dx <= 1; dx++) for (int dy = -1; dy <= 1; dy++) FloorGrids.Add((cp.Worldspace, g.X + dx, g.Y + dy));
    }

    /// <summary>The first plugin read ahead, so its own neighbours are filed too.</summary>
    public void Seed(string firstPath)
    {
        try { using var m0 = SkyrimMod.CreateFromBinaryOverlay(firstPath, SkyrimRelease.SkyrimSE); foreach (var cp in PlacementExtract.FromMod(m0)) LearnGrid(cp); }
        catch { }
    }

    /// <summary>Open one plugin of the order, or null when it cannot be read.</summary>
    public static ISkyrimModGetter? Open(string path)
    {
        try { return SkyrimMod.CreateFromBinaryOverlay(path, SkyrimRelease.SkyrimSE); } catch { return null; }
    }

    /// <summary>One plugin's cells, in load order.</summary>
    public void Plugin(ISkyrimModGetter m)
    {
        foreach (var cp in PlacementExtract.FromMod(m))
        {
            LearnGrid(cp);
            if (!cp.Interior && cp.Grid is { } fg && FloorGrids.Contains((cp.Worldspace, fg.X, fg.Y)))
            {
                if (!FloorRefsByWorld.TryGetValue(cp.Worldspace, out var frg)) { FloorRefsByWorld[cp.Worldspace] = frg = new(); FloorLandByWorld[cp.Worldspace] = new(); }
                FloorFeed.FileByGrid(cp, frg, FloorLandByWorld[cp.Worldspace]);
            }
            if (!WantedCells.Contains(cp.Cell)) continue;
            if (cp.Navmeshes.Count > 0)
            {
                if (!WinNav.TryGetValue(cp.Cell, out var d)) WinNav[cp.Cell] = d = new Dictionary<FormKey, NavmeshGeometry>();
                foreach (var nav in cp.Navmeshes) d[nav.FormKey] = nav;
            }
            if (cp.Refs.Count > 0)
            {
                if (!CellRefs.TryGetValue(cp.Cell, out var l)) CellRefs[cp.Cell] = l = new List<PlacedRef>();
                var seen = new HashSet<FormKey>(l.Select(r => r.FormKey));
                l.RemoveAll(r => cp.Refs.Any(x => x.FormKey == r.FormKey));
                l.AddRange(cp.Refs);
            }
        }
    }

    /// <summary>HOUSECARL_WINSTATE_DUMP: the wanted cells' references and the floor filing, for an independent reader.</summary>
    public void Dump()
    {
        if (!WinStateDump.Asked) return;
        WinStateDump.Write("stateprobe-route", new
        {
            wantedCells = WantedCells.Select(k => k.ToString()).OrderBy(k => k, StringComparer.Ordinal).ToList(),
            cellRefs = CellRefs.OrderBy(c => c.Key.ToString(), StringComparer.Ordinal)
                               .SelectMany(c => c.Value.Select(r => WinStateDump.Entry(r, c.Key))).ToList(),
            floor = FloorRefsByWorld.Select(w => WinStateDump.Floor(w.Key, w.Value, FloorLandByWorld[w.Key])).ToList(),
        });
    }
}
