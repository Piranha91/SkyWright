using System.Numerics;
using HousecarlCore;
using HarnessShared;
using Mutagen.Bethesda.Plugins;

/// <summary>Independent analytic cases through the production placement, discovery and counterfactual pipeline.</summary>
static class MeshPipelineProof
{
    static readonly ModKey Master = new("SurfaceMaster", ModType.Master);
    static readonly ModKey AMod = new("SurfaceA", ModType.Plugin);
    static readonly ModKey BMod = new("SurfaceB", ModType.Plugin);
    static readonly FormKey Cell = new(Master, 0x800);
    static readonly FormKey BaseA = new(Master, 0x801), BaseB = new(Master, 0x802), PreviousBase = new(Master, 0x803);
    static readonly FormKey RefA = new(AMod, 0x900), RefB = new(BMod, 0x900);
    static readonly HashSet<string> Types = new(StringComparer.Ordinal) { "Static" };
    static readonly HashSet<int>[] Closure = [new(), new() { 0 }, new() { 0 }];

    static TriangleSoup Triangle(Vector3 a, Vector3 b, Vector3 c) => new([a, b, c], [0, 1, 2]);
    static readonly TriangleSoup Horizontal = Triangle(new(0, 0, 0), new(4, 0, 0), new(0, 4, 0));
    static readonly TriangleSoup Crossing = Triangle(new(.5f, .5f, -1), new(.5f, .5f, 1), new(2, .5f, 0));
    // Same XY enclosing box as part of Horizontal; the inequalities x+y<=4 and x+y>=5 prove separation.
    static readonly TriangleSoup Outside = Triangle(new(4, 4, 0), new(4, 1, 0), new(1, 4, 0));

    static PlacedRef Ref(FormKey key, FormKey baseKey, Vec3 position = default, Vec3 rotation = default, float scale = 1) =>
        new(key, "PlacedObject", null, position, rotation, scale, baseKey, false, false, false);

    static OverlapIndex Index(TriangleSoup other, float scale, out Func<FormKey, (TriangleSoup? Soup, char Src, string? Reason)> geometry)
    {
        var index = new OverlapIndex();
        index.BaseInfo[BaseA] = ("Static", "SurfaceA");
        index.BaseInfo[BaseB] = ("Static", "SurfaceB");
        index.Record(1, Cell, FormKey.Null, Ref(RefA, BaseA, scale: scale));
        index.Record(2, Cell, FormKey.Null, Ref(RefB, BaseB, scale: scale));
        geometry = key => key == BaseA ? (Horizontal, 'N', null) : key == BaseB ? (other, 'N', null) : (null, '-', "missing fixture geometry");
        return index;
    }

    public static void Run(Action<string, bool, string> check)
    {
        foreach (float scale in new[] { .001f, 1f, -1f, 1_000_000f })
        {
            var index = Index(Crossing, scale, out var geometry);
            var candidates = index.SurfaceCandidates(geometry, Types, _ => false, out var skips);
            check($"surface pipeline: flat and crossing triangles remain candidates at scale {scale}",
                candidates.Count == 2 && skips.Volume == 0 && skips.DegenerateGeometry == 0,
                $"candidates={candidates.Count}, volume={skips.Volume}, degenerate={skips.DegenerateGeometry}");
            var sweep = OverlapIndex.SurfacePairs(candidates, Closure, pairTimeout: TimeSpan.FromSeconds(10));
            check($"surface pipeline: actual crossing survives tiny/large/mirrored geometry at scale {scale}",
                sweep.CrossHits.Count == 1 && sweep.CrossHits[0].Query.SurfaceRelation == "crossing",
                string.Join("; ", sweep.CrossResults.Select(r => r.Query)));
        }

        var separateIndex = Index(Outside, 1, out var separateGeometry);
        var separateCandidates = separateIndex.SurfaceCandidates(separateGeometry, Types, _ => false, out _);
        var separate = OverlapIndex.SurfacePairs(separateCandidates, Closure);
        check("surface pipeline: positive enclosing-box overlap does not emit an intersection",
            separate.CrossHits.Count == 0 && separate.CrossResults.Count == 1
            && separate.CrossResults[0].Query is { Intersects: false, SurfaceRelation: "separated" },
            string.Join("; ", separate.CrossResults.Select(r => r.Query)));

        var obndIndex = Index(Crossing, 1, out var obndGeometry);
        string? baseline = null;
        foreach (var extent in new[] { 0f, 1f, 30_000f, -300f })
        {
            obndIndex.BaseObnd.Clear();
            if (extent != 0) { obndIndex.BaseObnd[BaseA] = new(extent, extent, extent); obndIndex.BaseObnd[BaseB] = new(-extent, extent, 0); }
            var candidates = obndIndex.SurfaceCandidates(obndGeometry, Types, _ => false, out var skips);
            var results = OverlapIndex.SurfacePairs(candidates, Closure);
            var signature = string.Join("|", results.CrossResults.Select(r => $"{r.A.Key}/{r.B.Key}:{r.Query.SurfaceRelation}:{r.Query.Intersects}"));
            baseline ??= signature;
            check($"surface pipeline: contradictory OBND extent {extent} cannot change discovery or actual result",
                candidates.Count == 2 && skips.NoGeometry == 0 && signature == baseline, signature);
        }

        // A scale-only winning override must change the surface while retaining placement responsibility.
        var poseIndex = Index(Horizontal, 1, out var poseGeometry);
        poseIndex.Record(2, Cell, FormKey.Null, Ref(RefA, BaseA, scale: 2));
        var posed = poseIndex.SurfaceCandidates(poseGeometry, Types, _ => false, out _).Single(c => c.Key == RefA);
        check("surface pipeline: scale-only winner places actual vertices exactly once without changing responsibility",
            posed.Win.Plugin == 1 && posed.WorldSoup.Vertices[1] == new Vector3(8, 0, 0),
            $"responsible={posed.Win.Plugin}, vertex={posed.WorldSoup.Vertices[1]}");
        poseIndex.Record(2, Cell, FormKey.Null, Ref(RefA, BaseA, rotation: new(0, 0, MathF.PI / 2), scale: 2));
        posed = poseIndex.SurfaceCandidates(poseGeometry, Types, _ => false, out _).Single(c => c.Key == RefA);
        check("surface pipeline: rotation-only winner uses the placed-vertex convention",
            posed.Win.Plugin == 1 && Vector3.Distance(posed.WorldSoup.Vertices[1], new(0, -8, 0)) < 1e-5f,
            $"responsible={posed.Win.Plugin}, vertex={posed.WorldSoup.Vertices[1]}");

        // Previous geometry is an open separated surface despite positive XY bounds overlap. The later plugin
        // changes the base as it moves the object; the comparison must load that recorded previous base.
        var history = new OverlapIndex();
        foreach (var key in new[] { BaseA, BaseB, PreviousBase }) history.BaseInfo[key] = ("Static", "SurfaceFixture");
        history.Record(0, Cell, FormKey.Null, Ref(RefB, PreviousBase));
        history.Record(1, Cell, FormKey.Null, Ref(RefA, BaseA));
        history.Record(2, Cell, FormKey.Null, Ref(RefB, BaseB, new(.1f, 0, 0)));
        (TriangleSoup? Soup, char Src, string? Reason) HistoryGeometry(FormKey key) =>
            key == BaseA ? (Horizontal, 'N', null) : key == BaseB ? (Crossing, 'N', null) : (Outside, 'N', null);
        var hc = history.SurfaceCandidates(HistoryGeometry, Types, _ => false, out _);
        var hits = OverlapIndex.SurfacePairs(hc, Closure).CrossHits;
        check("surface pipeline: base-changing placement reaches actual-shape provenance", hits.Count == 1, $"hits={hits.Count}");
        if (hits.Count == 1)
        {
            var row = history.JudgeSurface(hits[0], Closure, HistoryGeometry);
            check("surface pipeline: actual previous base establishes introduced crossing despite overlapping previous boxes",
                row.CounterfactualStatus == "measured" && row.Counterfactual is { Intersects: false, SurfaceRelation: "separated" }
                && row.Origin == OverlapOrigin.Introduced, $"{row}");
            (TriangleSoup? Soup, char Src, string? Reason) MissingPrevious(FormKey key) =>
                key == PreviousBase ? (null, '-', "missing previous geometry") : HistoryGeometry(key);
            var unknown = history.JudgeSurface(hits[0], Closure, MissingPrevious);
            check("surface pipeline: missing earlier geometry cannot manufacture introduced provenance",
                unknown.CounterfactualStatus == "unknown" && unknown.Origin is null,
                $"{unknown}");
        }

        var bad = Index(Crossing, 1, out var knownGeometry);
        (TriangleSoup? Soup, char Src, string? Reason) Missing(FormKey key) =>
            key == BaseB ? (null, '-', "missing NIF") : knownGeometry(key);
        bad.BaseObnd[BaseB] = new(30_000, 30_000, 30_000);
        var incomplete = bad.SurfaceCandidates(Missing, Types, _ => false, out var unknownSkips);
        check("surface pipeline: missing NIF remains counted unknown despite supplied OBND",
            incomplete.Count == 1 && unknownSkips.NoGeometry == 1, $"candidates={incomplete.Count}, unknown={unknownSkips.NoGeometry}");

        // Retaining same-responsible query rows is an observation option only. It cannot change the
        // sweep population, counters, cross rows, or query outcomes already measured by the sweep.
        var retention = Index(Crossing, 1, out var retentionGeometry);
        var sameKey = new FormKey(AMod, 0x901);
        retention.Record(1, Cell, FormKey.Null, Ref(sameKey, BaseB));
        var retentionCandidates = retention.SurfaceCandidates(retentionGeometry, Types, _ => false, out _);
        var defaultRetention = OverlapIndex.SurfacePairs(retentionCandidates, Closure);
        var explicitRetention = OverlapIndex.SurfacePairs(retentionCandidates, Closure, retainSameResults: true);
        string Counters(SurfacePairSweep s) => string.Join("|", s.PairsChecked, s.SameChecked, s.SameIntersects,
            s.SameUnknown, s.CrossDeclaredChecked, s.CrossDeclaredIntersects, s.CrossUndeclaredChecked,
            s.CrossUndeclaredIntersects, s.CrossUnknown,
            string.Join(",", s.SameByDomain.OrderBy(x => x.Key).Select(x => $"{x.Key}:{x.Value}")));
        string Cross(SurfacePairSweep s) => string.Join("|", s.CrossResults.Select(x =>
            $"{x.A.Key}/{x.B.Key}:{x.Declared}:{x.Query.Status}:{x.Query.SurfaceRelation}:{x.Query.Intersects}:{x.Query.TrianglePairsTested}"));
        check("surface retention: default discards same-responsible rows without changing their counters",
            defaultRetention.SameResults.Count == 0 && defaultRetention.SameChecked > 0,
            $"sameResults={defaultRetention.SameResults.Count}, counters={Counters(defaultRetention)}");
        check("surface retention: opt-in preserves identical counters and cross query rows",
            Counters(defaultRetention) == Counters(explicitRetention) && Cross(defaultRetention) == Cross(explicitRetention),
            $"default={Counters(defaultRetention)} / {Cross(defaultRetention)}; retained={Counters(explicitRetention)} / {Cross(explicitRetention)}");
        check("surface retention: retained same rows exactly account for the measured same population",
            explicitRetention.SameResults.Count == explicitRetention.SameChecked
            && explicitRetention.SameResults.Count(x => x.Query.Intersects == true) == explicitRetention.SameIntersects
            && explicitRetention.SameResults.Count(x => x.Query.Unknown) == explicitRetention.SameUnknown,
            $"rows={explicitRetention.SameResults.Count}, checked={explicitRetention.SameChecked}, intersects={explicitRetention.SameIntersects}, unknown={explicitRetention.SameUnknown}");
    }
}
