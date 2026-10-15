using HarnessShared;
using HousecarlCore;
using Mutagen.Bethesda.Plugins;

// waterprobe's decisions, out of the top-level statements so --self-test calls the code the run calls: the seam pass and
// which seams are rows, which cells are missing water, who a seam row names, and how a --base row is re-measured.
static class WaterFindings
{
    /// <summary>Every edge-adjacent pair of exterior cells, truth arm against winner arm; pairs with a duplicate or
    /// unjudgeable side are skipped, and pairs with no disagreement in either arm are not kept.</summary>
    public static List<SeamRow> Seams(List<Row> ext, CellBoundaryIndex idx, Dictionary<FormKey, Row> byKey, float eps,
                                      Func<DetectorModel.ResolvedWater, string> res)
    {
        var seams = new List<SeamRow>();
        foreach (var a in ext)
        {
            if (a.Dup) continue;
            foreach (var (dx, dy) in new[] { (1, 0), (0, 1) })
            {
                var bc = idx.Single(a.Cell.Worldspace, a.Cell.GX + dx, a.Cell.GY + dy);
                if (bc is null || !byKey.TryGetValue(bc.Key, out var b)) continue;
                if (a.Verdict == WaterVerdict.Unjudgeable || b.Verdict == WaterVerdict.Unjudgeable) continue;
                var (sv, why) = DetectorModel.WaterSeam(a.RTruth, b.RTruth, a.RWin, b.RWin, a.Verdict, b.Verdict, eps);
                if (sv == WaterSeamVerdict.None) continue;
                // The physical half under this arm: the two resolved surfaces still differ.
                var phys = DetectorModel.WaterJoined(a.RWin, b.RWin, eps) ? PhysicalState.Gone : PhysicalState.Holds;
                string detail = $"winner {res(a.RWin)} | {res(b.RWin)}";
                seams.Add(new SeamRow(a, b, dx == 1 ? "E" : "N", sv, why, phys, detail));
            }
        }
        return seams;
    }

    /// <summary>The seam rows a run lists and emits: Seam only (Inherited is the floor, Joined a fixer's shape).</summary>
    public static IEnumerable<SeamRow> SeamRows(IEnumerable<SeamRow> seams) => seams.Where(s => s.Verdict == WaterSeamVerdict.Seam);

    /// <summary>The missing-water candidates.</summary>
    public static IEnumerable<Row> Missing(IEnumerable<Row> ext) => ext.Where(r => r.Missing);

    /// <summary>A seam row names the side that did not forward the truth; when both changed, side A.</summary>
    public static (Row Attributed, Row Other) Attribute(SeamRow s)
    {
        var attributed = s.A.Verdict != WaterVerdict.Forwarded ? s.A : s.B;
        var other = ReferenceEquals(attributed, s.A) ? s.B : s.A;
        return (attributed, other);
    }

    /// <summary>--base: each base row re-measured under this order.</summary>
    public static List<Predicate> Predicates(IReadOnlyList<Conflict> baseRows, Dictionary<FormKey, Row> byKey, float minVisible, float eps,
                                             Func<DetectorModel.ResolvedWater, string> res)
    {
        var predicates = new List<Predicate>();
        foreach (var c in baseRows)
        {
            string? other = c.Alternative is { Count: > 0 } alt ? alt[0].FormKey : null;
            foreach (var rec in c.Necessary)
            {
                if (!FormKey.TryFactory(rec.FormKey, out var fk) || !byKey.TryGetValue(fk, out var r))
                { predicates.Add(new Predicate(c.Kind, c.A, c.B, rec.FormKey, other, PhysicalState.Unmeasured, "the cell is not in this order")); continue; }
                if (c.Kind == "WATER-MISSING")
                {
                    // The predicate under THIS arm: the winner's surface is invisible (or absent) where the base row's truth showed water.
                    var (_, ph, why) = DetectorModel.MissingWater(r.Verdict == WaterVerdict.Forwarded ? WaterVerdict.Altered : r.Verdict, r.RTruth.Has, r.VisTruth, r.RWin.Has, r.VisWin, minVisible);
                    if (r.Verdict == WaterVerdict.Forwarded) ph = r.RWin.Has && (r.VisWin is null || r.VisWin >= minVisible) ? PhysicalState.Gone : r.VisWin is null ? PhysicalState.Unmeasured : PhysicalState.Holds;
                    predicates.Add(new Predicate(c.Kind, c.A, c.B, rec.FormKey, other, ph, why));
                }
                else if (c.Kind == "WATER-SEAM" && other is not null && FormKey.TryFactory(other, out var ok) && byKey.TryGetValue(ok, out var o))
                {
                    var ph = DetectorModel.WaterJoined(r.RWin, o.RWin, eps) ? PhysicalState.Gone : PhysicalState.Holds;
                    predicates.Add(new Predicate(c.Kind, c.A, c.B, rec.FormKey, other, ph, $"winner {res(r.RWin)} | {res(o.RWin)}"));
                }
                else predicates.Add(new Predicate(c.Kind, c.A, c.B, rec.FormKey, other, PhysicalState.Unmeasured, "not a water row"));
            }
        }
        return predicates;
    }
}
