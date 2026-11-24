using System.Text.Json;
using HarnessShared;
using HousecarlCore;
using SkyWright.Engine;

/// <summary>Known answers for contact evidence on placed members with zero-area triangles (2026-10-13, day lane 1 item 2).
/// MeshIntersection.MeasureContactEvidence refuses a pair at its first zero-area triangle, where the actual-shape relation
/// (QueryPlaced) and --surface skip triangles with zero area in their own model and measure a pair whose placement collapses a
/// triangle in the pair frame. MeasureContactEvidencePlaced takes the same preparation, and its envelopes are world coordinates.
/// The fixtures are lane D's (MotionProof.PlacedCollapse). Each case names the wrong answer it rejects. Red at the test commit
/// except the guards.</summary>
static partial class MotionProof
{
    static MeshContactEvidence CeMeasure(TriangleSoup a, Vec3 posA, TriangleSoup b, Vec3 posB) =>
        MeshIntersection.MeasureContactEvidencePlaced(SpatialOverlap.TransformSoup(a, posA, Zero3, 1f), new(a, posA, Zero3, 1f),
                                                      SpatialOverlap.TransformSoup(b, posB, Zero3, 1f), new(b, posB, Zero3, 1f), 1e-5);

    static string CeShow(MeshContactEvidence e) =>
        $"{e.Status} {e.Reason} value {JsonSerializer.Serialize(e.Value)} frame {(e.Frame is null ? "null" : string.Join(",", e.Frame))} skipped {(e.ZeroAreaSkipped is null ? "null" : string.Join(",", e.ZeroAreaSkipped))}";

    static bool CeFrame(MeshContactEvidence e, Vec3 o) => e.Frame is { Length: 3 } f && f[0] == o.X && f[1] == o.Y && f[2] == o.Z;

    // the sliver's contact with the quad, world coordinates: x 1065.5, y from the sliver's base (-1295) to its top (+2^-16), z 50, each
    // widened by the tolerance (the envelope is the intersection of the two triangles' tolerance-expanded bounds)
    static bool CeSliverEnvelope(MeshContactEvidenceValue? v)
    {
        const double t = 1e-5;
        return v is { EnvelopeMin: { } mn, EnvelopeMax: { } mx }
            && PcNear(mn[0], 1065.5 - t, 1e-6) && PcNear(mn[1], -1295 - t, 1e-6) && PcNear(mn[2], 50 - t, 1e-6)
            && PcNear(mx[0], 1065.5 + t, 1e-6) && PcNear(mx[1], -1295 + PcHeight + t, 1e-6) && PcNear(mx[2], 50 + t, 1e-6);
    }

    public static void RunContactEvidencePlaced(Action<string, bool, string> check)
    {
        // CE-1: AS-1's members at the origin (A: a sliver, an ordinary triangle and one triangle with zero area in its own model; B: the
        // quad crossing the sliver). Complete, skipped [1, 0], world frame, and the value equal to the evidence on A without its
        // zero-area triangle. Rejects the refusal (a_degenerate_triangle) and counting the skipped triangle as participating.
        Case(check, "contact-evidence CE-1: a zero-area-in-model triangle is skipped, the value equals the pair without it", () =>
        {
            var e = CeMeasure(PcA(withModelZero: true), Zero3, PcQuadB(0), Zero3);
            var clean = MeshIntersection.MeasureContactEvidence(SpatialOverlap.TransformSoup(PcA(), Zero3, Zero3, 1f),
                                                                SpatialOverlap.TransformSoup(PcQuadB(0), Zero3, Zero3, 1f), 1e-5);
            string je = JsonSerializer.Serialize(e.Value), jc = JsonSerializer.Serialize(clean.Value);
            return (e.Status == "complete" && clean.Status == "complete" && e.Value is { CrossingPairs: > 0 } && je == jc
                    && e.Frame is null && e.ZeroAreaSkipped is [1, 0], CeShow(e) + " | clean " + jc);
        });

        // CE-2: lane D's PC-1 pose, A's sliver collapsed where placed centred on B. Complete, crossing, frame = the geometry centre
        // (1065.5, -1307.5, 50), nothing skipped, the envelope in world coordinates around the crossing at (1065.5, -1295, 50).
        // Rejects the refusal, an envelope left in the pair frame (off by the origin) and a shift back with the wrong sign.
        Case(check, "contact-evidence CE-2: a sliver collapsed by placement - measured in the pair frame, world envelope", () =>
        {
            var a = PcA(); var b = PcQuadB(700);
            if (PcPrecondition(a, PcPosA, Zero3, 1f, b, PcPosB, Zero3, 1f, PcCentre1) is { } bad) return (false, bad);
            var e = CeMeasure(a, PcPosA, b, PcPosB);
            return (e.Status == "complete" && e.Value is { CrossingPairs: > 0 } && CeFrame(e, PcCentre1) && e.ZeroAreaSkipped is null
                    && CeSliverEnvelope(e.Value), CeShow(e));
        });

        // CE-2s: the same members with B lifted 500 u: complete, no contact, no envelope, in the pair frame. Rejects the refusal and a
        // false contact.
        Case(check, "contact-evidence CE-2s: the collapsed sliver clear of its partner - complete, no contact", () =>
        {
            var a = PcA(); var b = PcQuadB(700); var posB = new Vec3(PcPosB.X, PcPosB.Y, PcPosB.Z + 500);
            var o = PairFrame.Origin(a, PcPosA, Zero3, 1f, b, posB, Zero3, 1f);
            if (PairFrame.IsWorld(o) || PairFrame.Collapses(a, PairFrame.Shift(PcPosA, o), Zero3, 1f)) return (false, $"fixture: no collapse-free frame ({o})");
            var e = CeMeasure(a, PcPosA, b, posB);
            return (e.Status == "complete" && e.Value is { CrossingPairs: 0, CoplanarPairs: 0, TouchingPairs: 0, EnvelopeMin: null }
                    && CeFrame(e, o), CeShow(e));
        });

        // CE-3: both at once (AS-3's members): the model-zero triangle skipped and counted, the collapsed sliver measured in the pair
        // frame, the same world envelope as CE-2. Rejects the refusal and a skip and frame that do not compose.
        Case(check, "contact-evidence CE-3: a model-zero triangle beside a collapsed sliver - skipped [1,0], world envelope", () =>
        {
            var a = PcA(withModelZero: true); var b = PcQuadB(700);
            if (PcPrecondition(a, PcPosA, Zero3, 1f, b, PcPosB, Zero3, 1f, PcCentre1) is { } bad) return (false, bad);
            var e = CeMeasure(a, PcPosA, b, PcPosB);
            return (e.Status == "complete" && e.Value is { CrossingPairs: > 0 } && CeFrame(e, PcCentre1) && e.ZeroAreaSkipped is [1, 0]
                    && CeSliverEnvelope(e.Value), CeShow(e));
        });

        // CE-4 (guard): a member made only of zero-area triangles still refuses, naming member a. Rejects "complete" with nothing measured.
        Case(check, "contact-evidence CE-4 (guard): a member with only zero-area triangles still refuses", () =>
        {
            var a = Soup(new[] { new System.Numerics.Vector3(0, -30, 0), new System.Numerics.Vector3(10, -30, 0), new System.Numerics.Vector3(5, -30, 0) });
            var e = CeMeasure(a, Zero3, PcQuadB(0), Zero3);
            return (e.Status == "unknown" && e.Value is null && (e.Reason ?? "").StartsWith("a_degenerate_triangle", StringComparison.Ordinal), CeShow(e));
        });

        // CE-5 (guard): no zero-area triangle anywhere: the placed entry is the world-soup measure and serializes to the same bytes (no
        // frame or skipped field). Rejects any change to a pair the evidence already answered.
        Case(check, "contact-evidence CE-5 (guard): an ordinary pair - identical evidence and identical JSON to the world-soup measure", () =>
        {
            var a = PcA(); var b = PcQuadB(0);
            var wa = SpatialOverlap.TransformSoup(a, Zero3, Zero3, 1f); var wb = SpatialOverlap.TransformSoup(b, Zero3, Zero3, 1f);
            var old = MeshIntersection.MeasureContactEvidence(wa, wb, 1e-5);
            var now = MeshIntersection.MeasureContactEvidencePlaced(wa, new(a, Zero3, Zero3, 1f), wb, new(b, Zero3, Zero3, 1f), 1e-5);
            string jo = JsonSerializer.Serialize(old), jn = JsonSerializer.Serialize(now);
            return (old.Status == "complete" && jo == jn && !jn.Contains("Frame") && !jn.Contains("ZeroAreaSkipped"), $"old {jo} | new {jn}");
        });

        // CE-6: the evidence and the relation prepare a pair the same way. On each placed fixture above (and AS-1/AS-2/AS-3's), the
        // evidence's frame and skipped counts equal QueryPlaced's, and the evidence has a crossing exactly when the relation is crossing.
        // Rejects two paths that prepare differently.
        Case(check, "contact-evidence CE-6: the evidence and the actual-shape relation share the frame, the skip and the crossing", () =>
        {
            var lifted = new Vec3(PcPosB.X, PcPosB.Y, PcPosB.Z + 500);
            var cases = new (string Name, TriangleSoup A, Vec3 PosA, TriangleSoup B, Vec3 PosB)[]
            {
                ("1", PcA(withModelZero: true), Zero3, PcQuadB(0), Zero3),
                ("2", PcA(), PcPosA, PcQuadB(700), PcPosB),
                ("2s", PcA(), PcPosA, PcQuadB(700), lifted),
                ("3", PcA(withModelZero: true), PcPosA, PcQuadB(700), PcPosB),
            };
            var bad = new List<string>();
            foreach (var c in cases)
            {
                var e = CeMeasure(c.A, c.PosA, c.B, c.PosB);
                var q = AsQuery(c.A, c.PosA, Zero3, 1f, c.B, c.PosB, Zero3, 1f);
                bool sameFrame = (e.Frame is null && q.Frame is null) || (e.Frame is { } ef && q.Frame is { } qf && ef.SequenceEqual(qf));
                bool sameSkip = (e.ZeroAreaSkipped is null && q.ZeroAreaSkipped is null)
                                || (e.ZeroAreaSkipped is { } es && q.ZeroAreaSkipped is { } qs && es.SequenceEqual(qs));
                bool sameCrossing = (e.Value is { CrossingPairs: > 0 }) == (q.SurfaceRelation == "crossing");
                if (!sameFrame || !sameSkip || !sameCrossing) bad.Add($"CE-{c.Name}: evidence [{CeShow(e)}] relation [{AsShow(q)}]");
            }
            return (bad.Count == 0, bad.Count == 0 ? "4 fixtures agree" : string.Join(" ; ", bad));
        });
    }
}
