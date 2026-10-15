using HarnessShared;
using HousecarlCore;
using Mutagen.Bethesda.Plugins;

// seamprobe's decisions, out of the top-level statements so --self-test calls the code the run calls: which edge-adjacent
// pairs are measured and which are the Bethesda floor, which pairs are the class, and how a --base row is re-measured.
static class SeamFindings
{
    /// <summary>Pass 2: every edge-adjacent pair (east and north of each LAND cell), skipping cells at a duplicate grid
    /// point, with both-winners-Bethesda marked.</summary>
    public static (List<Pair> Pairs, int DupSkipped) MeasurePairs(CellBoundaryIndex idx, List<CellInfo> landCells,
        IReadOnlyList<HashSet<int>> closure, float eps, Func<int, PluginKind> kind)
    {
        var pairs = new List<Pair>();
        int dupSkipped = 0;
        var dupGrid = new HashSet<(FormKey, int, int)>(idx.DuplicateGridPoints().Select(d => d.Grid));
        foreach (var a in landCells)
        {
            if (dupGrid.Contains((a.Worldspace, a.GX, a.GY))) { dupSkipped++; continue; }
            foreach (var (dx, dy, ea) in new[] { (1, 0, CellEdge.East), (0, 1, CellEdge.North) })
            {
                var b = idx.Single(a.Worldspace, a.GX + dx, a.GY + dy);
                if (b is null) continue;
                var m = CellBoundaryIndex.MeasurePair(a, b, ea, closure, eps);
                if (m is null) continue;
                bool bothBethesda = kind(a.Land[^1].Plugin) == PluginKind.Bethesda && kind(b.Land[^1].Plugin) == PluginKind.Bethesda;
                pairs.Add(new Pair(m, dx == 1 ? "E" : "N", a.Land[^1].Plugin, b.Land[^1].Plugin, bothBethesda));
            }
        }
        return (pairs, dupSkipped);
    }

    /// <summary>The class: mod-involved pairs whose shared edge mismatches by more than eps.</summary>
    public static List<Pair> Class(List<Pair> pairs) => pairs.Where(p => !p.BothBethesda && p.Verdict == LandSeamVerdict.Seam).ToList();

    /// <summary>--base: each base row re-measured under this order — Holds while the edge still mismatches by more than eps.</summary>
    public static List<Predicate> Predicates(IReadOnlyList<Conflict> baseRows, List<Pair> pairs, float eps)
    {
        var byPair = new Dictionary<string, Pair>();
        foreach (var p in pairs) { byPair[$"{p.A.Key}|{p.B.Key}"] = p; byPair[$"{p.B.Key}|{p.A.Key}"] = p; }
        var predicates = new List<Predicate>();
        foreach (var c in baseRows)
        {
            string? other = c.Alternative is { Count: > 0 } alt ? alt[0].FormKey : null;
            foreach (var rec in c.Necessary)
            {
                if (c.Kind != "LAND-SEAM" || other is null || !byPair.TryGetValue($"{rec.FormKey}|{other}", out var p))
                { predicates.Add(new Predicate(c.Kind, c.A, c.B, rec.FormKey, other, PhysicalState.Unmeasured, c.Kind != "LAND-SEAM" ? "not a land-seam row" : "the pair is not measured in this order")); continue; }
                predicates.Add(new Predicate(c.Kind, c.A, c.B, rec.FormKey, other, p.MaxDelta > eps ? PhysicalState.Holds : PhysicalState.Gone, $"the shared edge mismatches by {p.MaxDelta:0} u under this arm"));
            }
        }
        return predicates;
    }
}
