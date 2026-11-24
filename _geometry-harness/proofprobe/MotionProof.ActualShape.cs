using System.Numerics;
using System.Text.Json;
using HarnessShared;
using HousecarlCore;
using SkyWright.Engine;

/// <summary>Known answers for the actual-shape relation (SurfaceQueryOutcome.QueryPlaced) on placed members with zero-area triangles
/// (2026-10-13, lane B item 2). The relation used to refuse a whole pair at the first triangle with zero area, while --surface (MeshSurface)
/// skips triangles with zero area in their own model and measures a pair whose placement collapses a triangle in the pair frame. The
/// relation takes the same rule. Each case names the wrong answer it rejects. Red at the test commit except the guards.</summary>
static partial class MotionProof
{
    static SurfaceQueryOutcome AsQuery(TriangleSoup a, Vec3 posA, Vec3 rotA, float sA, TriangleSoup b, Vec3 posB, Vec3 rotB, float sB) =>
        SurfaceQueryOutcome.QueryPlaced(SpatialOverlap.TransformSoup(a, posA, rotA, sA), new(a, posA, rotA, sA),
                                        SpatialOverlap.TransformSoup(b, posB, rotB, sB), new(b, posB, rotB, sB),
                                        1e-5, 2_000_000, TimeSpan.FromSeconds(60));

    static string AsShow(SurfaceQueryOutcome q) =>
        $"{q.Status} {q.Reason} {q.SurfaceRelation} intersects {q.Intersects?.ToString() ?? "null"} frame {(q.Frame is null ? "null" : string.Join(",", q.Frame))} skipped {(q.ZeroAreaSkipped is null ? "null" : string.Join(",", q.ZeroAreaSkipped))}";

    static bool AsFrame(SurfaceQueryOutcome q, Vec3 o) => q.Frame is { Length: 3 } f && f[0] == o.X && f[1] == o.Y && f[2] == o.Z;

    public static void RunActualShape(Action<string, bool, string> check)
    {
        // AS-1: A carries one triangle with zero area in its own model (three collinear vertices) beside a sliver the quad B crosses; both
        // at the origin. Crossing, skipped [1, 0], world frame (no frame field). Rejects the refusal (a_degenerate_triangle), which
        // is what the relation answered before.
        Case(check, "actual-shape AS-1: a zero-area-in-model triangle is skipped, the pair crosses", () =>
        {
            var q = AsQuery(PcA(withModelZero: true), Zero3, Zero3, 1f, PcQuadB(0), Zero3, Zero3, 1f);
            return (q.Status == "ok" && q.Intersects == true && q.SurfaceRelation == "crossing" && q.Frame is null
                    && q.ZeroAreaSkipped is [1, 0], AsShow(q));
        });

        // AS-2: lane D's PC-1 pose: A's sliver collapses where placed centred on B's world position, and has area in the frame centred on
        // the pair's geometry (1065.5, -1307.5, 50). The quad crosses it: crossing, frame = that centre, nothing skipped. Rejects the
        // refusal and dropping the collapsed sliver (which would read separated).
        Case(check, "actual-shape AS-2: a sliver collapsed by placement - answered in the pair frame, crossing", () =>
        {
            var a = PcA(); var b = PcQuadB(700);
            if (PcPrecondition(a, PcPosA, Zero3, 1f, b, PcPosB, Zero3, 1f, PcCentre1) is { } bad) return (false, bad);
            if (!PairFrame.Collapses(a, PcPosA, Zero3, 1f)) return (false, "fixture: A does not collapse in the world frame");
            var q = AsQuery(a, PcPosA, Zero3, 1f, b, PcPosB, Zero3, 1f);
            return (q.Status == "ok" && q.Intersects == true && q.SurfaceRelation == "crossing" && AsFrame(q, PcCentre1) && q.ZeroAreaSkipped is null,
                    AsShow(q));
        });

        // AS-2s: the same members with B lifted 500 u, clear of A: separated, answered in a pair frame (A's sliver collapses in the world
        // frame). Rejects the refusal and an answer of crossing.
        Case(check, "actual-shape AS-2s: the collapsed sliver clear of its partner - answered, separated", () =>
        {
            var a = PcA(); var b = PcQuadB(700); var posB = new Vec3(PcPosB.X, PcPosB.Y, PcPosB.Z + 500);
            if (!PairFrame.Collapses(a, PcPosA, Zero3, 1f)) return (false, "fixture: A does not collapse in the world frame");
            var o = PairFrame.Origin(a, PcPosA, Zero3, 1f, b, posB, Zero3, 1f);
            if (PairFrame.IsWorld(o) || PairFrame.Collapses(a, PairFrame.Shift(PcPosA, o), Zero3, 1f)) return (false, $"fixture: no collapse-free frame ({o})");
            var q = AsQuery(a, PcPosA, Zero3, 1f, b, posB, Zero3, 1f);
            return (q.Status == "ok" && q.Intersects == false && q.SurfaceRelation == "separated" && AsFrame(q, o), AsShow(q));
        });

        // AS-3: both at once (PC-3's members): the model-zero triangle skipped and counted, the collapsed sliver kept and answered in
        // the pair frame. Crossing, skipped [1, 0], frame PcCentre1. Rejects counting the collapsed sliver as skipped ([2, 0]) and the
        // refusal.
        Case(check, "actual-shape AS-3: a model-zero triangle beside a collapsed sliver - skipped [1,0], crossing in the pair frame", () =>
        {
            var a = PcA(withModelZero: true); var b = PcQuadB(700);
            if (PcPrecondition(a, PcPosA, Zero3, 1f, b, PcPosB, Zero3, 1f, PcCentre1) is { } bad) return (false, bad);
            var q = AsQuery(a, PcPosA, Zero3, 1f, b, PcPosB, Zero3, 1f);
            return (q.Status == "ok" && q.Intersects == true && q.SurfaceRelation == "crossing" && AsFrame(q, PcCentre1)
                    && q.ZeroAreaSkipped is [1, 0], AsShow(q));
        });

        // AS-4 (guard): a member whose every triangle has zero area still refuses, naming member a. Rejects an answer of separated for
        // a member with nothing left to measure.
        Case(check, "actual-shape AS-4 (guard): a member with only zero-area triangles still refuses", () =>
        {
            var a = Soup(new[] { new Vector3(0, -30, 0), new Vector3(10, -30, 0), new Vector3(5, -30, 0) });
            var q = AsQuery(a, Zero3, Zero3, 1f, PcQuadB(0), Zero3, Zero3, 1f);
            return (q.Unknown && q.Intersects is null && (q.Reason ?? "").StartsWith("a_degenerate_triangle", StringComparison.Ordinal), AsShow(q));
        });

        // AS-5 (guard): no zero-area triangle anywhere: QueryPlaced is the world-soup query, field for field, and serializes to the same
        // bytes (no frame or skipped field). Rejects any change to a pair the old relation answered.
        Case(check, "actual-shape AS-5 (guard): an ordinary pair - identical outcome and identical JSON to the world-soup query", () =>
        {
            var a = PcA(); var b = PcQuadB(0);
            if (PairFrame.Collapses(a, Zero3, Zero3, 1f)) return (false, "fixture: A collapses at the origin");
            var wa = SpatialOverlap.TransformSoup(a, Zero3, Zero3, 1f); var wb = SpatialOverlap.TransformSoup(b, Zero3, Zero3, 1f);
            var old = SurfaceQueryOutcome.Query(wa, wb, 1e-5, 2_000_000, TimeSpan.FromSeconds(60));
            var now = SurfaceQueryOutcome.QueryPlaced(wa, new(a, Zero3, Zero3, 1f), wb, new(b, Zero3, Zero3, 1f), 1e-5, 2_000_000, TimeSpan.FromSeconds(60));
            string jo = JsonSerializer.Serialize(old), jn = JsonSerializer.Serialize(now);
            return (old.Intersects is not null && jo == jn && now.Frame is null && now.ZeroAreaSkipped is null, $"old {jo} | new {jn}");
        });
    }
}
