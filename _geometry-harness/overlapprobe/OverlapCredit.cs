using HarnessShared;
using HousecarlCore;
using SkyWright.Engine;
using Mutagen.Bethesda.Plugins;

// THE PHYSICAL PREDICATE of an overlap row under this run's order (Program.cs): does the pair's actual-surface intersection
// still hold, is it gone, or can it not be measured, and why. Every emitted row carries it, and --base re-measures every row
// of an earlier emit with it. Held in its own class so --self-test runs exactly the predicate a real run computes.
sealed record OverlapCredit(OverlapIndex Index, PopulationMode Mode, EnableArms? Arms,
                            Func<FormKey, (TriangleSoup? Soup, char Source, string? Reason)> Geometry,
                            double Tolerance, long MaxTrianglePairs, double PairSeconds, PairTimer? Timer = null,
                            PairResultCache? Results = null)
{
    public (PhysicalState State, string Detail) Predicate(FormKey laterKey, FormKey otherKey)
    {
        var sl = Index.WinningGeometryPose(laterKey); var so = Index.WinningGeometryPose(otherKey);
        if (sl is null || so is null) return (PhysicalState.Unmeasured, "a ref is not placed in this order");
        if (sl.Value.Deleted || Index.IsDeletedNow(laterKey) || so.Value.Deleted || Index.IsDeletedNow(otherKey)) return (PhysicalState.Gone, "a ref is deleted under this order");
        if (Mode != PopulationMode.Flags)
        {
            var el = Arms!.Winner(laterKey); var eo = Arms.Winner(otherKey);
            if (!el.Resolved || !eo.Resolved) return (PhysicalState.Unmeasured, "a ref's enable-parent chain is unresolved");
            if (!el.Enabled || !eo.Enabled) return (PhysicalState.Gone, "a ref is disabled at runtime under this state");
        }
        else if (sl.Value.Disabled || so.Value.Disabled) return (PhysicalState.Gone, "a ref is initially disabled under this order");
        if ((Index.BosSwaps.TryGetValue(laterKey, out var bosLater) && (bosLater.Random || bosLater.PerSession || bosLater.PropertiesRandom || bosLater.Alternatives.Count != 1))
         || (Index.BosSwaps.TryGetValue(otherKey, out var bosOther) && (bosOther.Random || bosOther.PerSession || bosOther.PropertiesRandom || bosOther.Alternatives.Count != 1)))
            return (PhysicalState.Unmeasured, "a ref has more than one possible BOS runtime outcome");
        if (sl.Value.Space != so.Value.Space) return (PhysicalState.Gone, "the refs are no longer in the same space");
        if (!Index.RefBase.TryGetValue(laterKey, out var bl) || !Index.RefBase.TryGetValue(otherKey, out var bo)) return (PhysicalState.Unmeasured, "a ref has no base record");
        var (modelL, srcL, whyL) = Geometry(bl); var (modelO, srcO, whyO) = Geometry(bo);
        if (modelL is not { TriangleCount: > 0 } || modelO is not { TriangleCount: > 0 })
            return (PhysicalState.Unmeasured, "a ref's strict decoded render geometry is unavailable");
        var worldL = SpatialOverlap.TransformSoup(modelL, sl.Value.Pos, sl.Value.Rot, sl.Value.Scale);
        var worldO = SpatialOverlap.TransformSoup(modelO, so.Value.Pos, so.Value.Rot, so.Value.Scale);
        var q = SurfaceQueryOutcome.QueryPlaced(worldL, new(modelL, sl.Value.Pos, sl.Value.Rot, sl.Value.Scale),
            worldO, new(modelO, so.Value.Pos, so.Value.Rot, so.Value.Scale), Tolerance, MaxTrianglePairs, TimeSpan.FromSeconds(PairSeconds), default, Timer, null, Results);
        if (q.Intersects == true)
            return (PhysicalState.Holds, $"actual render surfaces: {q.SurfaceRelation}; containment {q.Containment}; {q.TrianglePairsTested:N0} triangle pairs [{srcL}{srcO}]");
        if (q.Intersects == false)
            return (PhysicalState.Gone, $"actual render surfaces are separated; containment {q.Containment}; {q.TrianglePairsTested:N0} triangle pairs [{srcL}{srcO}]");
        return (PhysicalState.Unmeasured, q.Reason ?? whyL ?? whyO ?? "actual render-surface query is unknown");
    }
}
