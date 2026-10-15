using HarnessShared;
using HousecarlCore;

// THE CLASS DECISIONS over the judged rows (Program.cs): which actual-surface intersections have an assessed provenance, and
// of those which are the class (Introduced + Blind, the Bethesda floor withheld), the floor, the knowing and the inherited.
// Held in their own class so --self-test runs exactly the decisions a real run makes; the console text stays in Program.cs.
static class OverlapClass
{
    /// <summary>The rows split by provenance and class. Every list keeps the rows' order.</summary>
    public sealed record Split(List<SurfaceOverlapRow> ProvenanceUnknown, List<SurfaceOverlapRow> Provenance,
                               List<SurfaceOverlapRow> Class, List<SurfaceOverlapRow> Floor,
                               List<SurfaceOverlapRow> Knowing, List<SurfaceOverlapRow> Inherited);

    /// <summary>An unknown counterfactual is counted, never classed; the class is Introduced + Blind and not Bethesda on
    /// Bethesda (<paramref name="popOf"/>, the probe's population split).</summary>
    public static Split Of(IReadOnlyList<SurfaceOverlapRow> rows, Func<SurfaceOverlapHit, OverlapPopulation> popOf)
    {
        var provenanceUnknown = rows.Where(r => r.CounterfactualStatus == "unknown").ToList();
        var provenanceRows = rows.Where(r => r.CounterfactualStatus != "unknown").ToList();
        var cls = provenanceRows.Where(r => r.Origin == OverlapOrigin.Introduced && r.Sight == OverlapSight.Blind
            && popOf(r.Hit) != OverlapPopulation.BethesdaFloor).ToList();
        var floorRows = provenanceRows.Where(r => popOf(r.Hit) == OverlapPopulation.BethesdaFloor).ToList();
        var knowing = provenanceRows.Where(r => r.Origin == OverlapOrigin.Introduced && r.Sight == OverlapSight.Seen).ToList();
        var inherited = provenanceRows.Where(r => r.Origin == OverlapOrigin.Inherited).ToList();
        return new(provenanceUnknown, provenanceRows, cls, floorRows, knowing, inherited);
    }
}
