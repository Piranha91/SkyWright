using System.Numerics;
using HousecarlCore;

/// <summary>Known answers for triangles that collapse to zero area only once placed (2026-10-10, lane D). A member's sliver has
/// area in its own model space but its height is below the float spacing of the coordinates it is placed at, so it rounds to
/// collinear; the pair frame centred on member B cannot undo that when member A sits far from B's origin (an interior shell's model
/// is offset from its reference). The proposed rule also tries the frame centred on member A. Each case names the wrong answer it
/// rejects. Red at the test commit except the cases marked guard.</summary>
static partial class MotionProof
{
    // A sliver thin in model y (height 2^-16 at y = 5, x 55..75), crossed at x = 65.5 by a quad perpendicular to its long axis:
    // the crossing runs across the sliver from its base to its upper edge, length 0.95 * 2^-16 in A's model units.
    static readonly double PcHeight = Math.Pow(2, -16), PcCrossing = 0.95 * Math.Pow(2, -16);
    static readonly Vector3[] PcSliverY = { new(55, 5, 0), new(75, 5, 0), new(65, 5 + (float)Math.Pow(2, -16), 0) };
    // an ordinary triangle far from B, so the member is never all sliver
    static readonly Vector3[] PcOrdinary = { new(0, -50, 0), new(10, -50, 0), new(0, -40, 0) };
    // B's reference and A's 700 u above it in y; B's model holds its quad 700 u from its own origin (as an interior shell does)
    static readonly Vec3 PcPosB = new(1000, -2000, 50), PcPosA = new(1000, -1300, 50);

    static TriangleSoup Soup(params Vector3[][] tris)
    {
        var v = tris.SelectMany(t => t).ToArray();
        return new TriangleSoup(v, Enumerable.Range(0, v.Length).ToArray());
    }
    static TriangleSoup PcA(bool withModelZero = false) => withModelZero
        ? Soup(PcSliverY, PcOrdinary, new[] { new Vector3(0, -30, 0), new Vector3(10, -30, 0), new Vector3(5, -30, 0) })
        : Soup(PcSliverY, PcOrdinary);
    // the quad x = 65.5, y -20..20, z -10..10 about A's position, in B's model coordinates (offset +700 in y)
    static TriangleSoup PcQuadB(float yOffset) => new(new[] { new Vector3(65.5f, yOffset - 20, -10), new Vector3(65.5f, yOffset + 20, -10),
                                                                 new Vector3(65.5f, yOffset + 20, 10), new Vector3(65.5f, yOffset - 20, 10) },
                                                       new[] { 0, 1, 2, 0, 2, 3 });

    // the fixture's precondition: placement collapses A's sliver centred on B and nothing centred on A
    static string? PcPrecondition(TriangleSoup a, Vec3 posA, Vec3 rotA, float sA, TriangleSoup b, Vec3 posB, Vec3 rotB, float sB)
    {
        if (!PairFrame.Collapses(a, PairFrame.Shift(posA, posB), rotA, sA)) return "fixture: A's sliver does not collapse centred on B";
        if (PairFrame.Collapses(a, PairFrame.Shift(posA, posA), rotA, sA)) return "fixture: A's sliver collapses centred on A";
        if (PairFrame.Collapses(b, PairFrame.Shift(posB, posA), rotB, sB)) return "fixture: B collapses centred on A";
        return null;
    }

    static bool PcNear(double a, double b, double tol) => Math.Abs(a - b) <= tol;

    public static void RunPlacedCollapse(Action<string, bool, string> check)
    {
        var json = new System.Text.Json.JsonSerializerOptions { NumberHandling = System.Text.Json.Serialization.JsonNumberHandling.AllowNamedFloatingPointLiterals };

        // PC-1: A's sliver collapses where placed centred on B (A sits 700 u out along the sliver's thin axis, float spacing 2^-14
        // against a height of 2^-16) and survives centred on A. Measured in the frame centred on A (origin = A's position): crossing
        // [L, L] with L = 0.95 * 2^-16 on the sliver, nothing skipped, envelope back in world coordinates. Rejects the refusal
        // (A: a_degenerate_triangle), dropping the collapsed sliver (crossing 0), skipping it as zero-area (ZeroAreaSkipped A 1) and an
        // envelope left in the measuring frame.
        Case(check, "placed-collapse PC-1: a sliver collapsed centred on B, intact centred on A - measured, crossing 0.95*2^-16", () =>
        {
            var a = PcA(); var b = PcQuadB(700);
            if (PcPrecondition(a, PcPosA, Zero3, 1f, b, PcPosB, Zero3, 1f) is { } bad) return (false, bad);
            var (r, o) = MeshSurface.MeasurePlacedFramed(a, null, PcPosA, Zero3, 1f, b, null, PcPosB, Zero3, 1f, VolumeGeometry.Render);
            bool env = r.EnvelopeMin is { } mn && r.EnvelopeMax is { } mx && PcNear(mn[0], 1065.5, 1e-6) && PcNear(mn[1], -1295, 1e-6) && PcNear(mn[2], 50, 1e-6)
                       && PcNear(mx[1], -1295 + PcCrossing, 1e-6);
            return (r.Status == "measured" && r.CrossingLength is { } L && PcNear(L.Lower, PcCrossing, 1e-10) && PcNear(L.Upper, PcCrossing, 1e-10)
                    && r.ZeroAreaSkipped is null && o == PcPosA && env,
                    $"{r.Status} {r.Reason} L {r.CrossingLength} skipped {r.ZeroAreaSkipped} frame {o} env {(r.EnvelopeMin is null ? "-" : string.Join(",", r.EnvelopeMin))}");
        });

        // PC-2: the window's own placement (scale 0.18, z-rotation 1.5707964, A 677 u above B's origin as in the Group 6 repro), with a
        // sliver thin in model z (height 2^-16 at z = 5). B is the image of A's model plane x = 65.5 through A's placement, written in B's
        // model coordinates (double, cast once). Measured centred on A: crossing 0.18 * 0.95 * 2^-16 within 0.1 %. Rejects the refusal and
        // dropping the sliver (crossing 0); also rejects a frame that loses A's scale (crossing 0.95 * 2^-16).
        Case(check, "placed-collapse PC-2: the window's pose (0.18, Rz 90 deg, 677 u up) - measured, crossing 0.18*0.95*2^-16", () =>
        {
            var posB = new Vec3(1773.9773f, -95.17011f, 46.33487f); var posA = new Vec3(1680.8007f, -116.84483f, 723.40308f);
            var rotA = new Vec3(0, 0, 1.5707964f); const float sA = 0.18f;
            var a = Soup(new[] { new Vector3(55, 0, 5), new Vector3(75, 0, 5), new Vector3(65, 0, 5 + (float)Math.Pow(2, -16)) }, PcOrdinary);
            var m = SpatialOverlap.RefrRotation(rotA);
            Vector3 ToB(double x, double y, double z)
            {
                double px = x * sA, py = y * sA, pz = z * sA;
                double wx = px * m.M11 + py * m.M21 + pz * m.M31 + posA.X, wy = px * m.M12 + py * m.M22 + pz * m.M32 + posA.Y, wz = px * m.M13 + py * m.M23 + pz * m.M33 + posA.Z;
                return new Vector3((float)(wx - posB.X), (float)(wy - posB.Y), (float)(wz - posB.Z));
            }
            var b = new TriangleSoup(new[] { ToB(65.5, -20, -10), ToB(65.5, 20, -10), ToB(65.5, 20, 20), ToB(65.5, -20, 20) }, new[] { 0, 1, 2, 0, 2, 3 });
            if (PcPrecondition(a, posA, rotA, sA, b, posB, Zero3, 1f) is { } bad) return (false, bad);
            var (r, o) = MeshSurface.MeasurePlacedFramed(a, null, posA, rotA, sA, b, null, posB, Zero3, 1f, VolumeGeometry.Render);
            double want = 0.18 * PcCrossing;
            return (r.Status == "measured" && r.CrossingLength is { } L && PcNear(L.Lower, want, 1e-3 * want) && PcNear(L.Upper, want, 1e-3 * want)
                    && r.ZeroAreaSkipped is null && o == posA,
                    $"{r.Status} {r.Reason} L {r.CrossingLength} (want {want:G6}) frame {o}");
        });

        // PC-3: PC-1 with a zero-area triangle in A's own model as well (three collinear vertices): the model-zero triangle is skipped and
        // counted as before (D1) and the placement-collapsed sliver is kept and measured. Measured, crossing as PC-1, ZeroAreaSkipped
        // {A 1, B 0}. Rejects counting the collapsed sliver as skipped (A 2), not skipping the model-zero one (null) and the refusal.
        Case(check, "placed-collapse PC-3: a model-zero triangle beside the collapsed sliver - skipped A 1, sliver measured", () =>
        {
            var a = PcA(withModelZero: true); var b = PcQuadB(700);
            if (PcPrecondition(a, PcPosA, Zero3, 1f, b, PcPosB, Zero3, 1f) is { } bad) return (false, bad);
            var r = MeshSurface.MeasurePlaced(a, null, PcPosA, Zero3, 1f, b, null, PcPosB, Zero3, 1f, VolumeGeometry.Render);
            return (r.Status == "measured" && r.CrossingLength is { } L && PcNear(L.Lower, PcCrossing, 1e-10) && r.ZeroAreaSkipped is { A: 1, B: 0 },
                    $"{r.Status} {r.Reason} L {r.CrossingLength} skipped {r.ZeroAreaSkipped}");
        });

        // PC-4 (guard): a near-zero but non-zero triangle that survives placement is measured, in the world frame (pair at the origin)
        // and in the frame centred on B (both references at the same far position). Crossing [L, L] on the sliver, nothing skipped.
        // Rejects any area threshold that drops tiny triangles (crossing 0) and a refusal.
        Case(check, "placed-collapse PC-4 (guard): a 2^-16 sliver that survives placement - measured, world and B-centred", () =>
        {
            var a = PcA(); var b = PcQuadB(0);
            var w = MeshSurface.MeasurePlacedFramed(a, null, Zero3, Zero3, 1f, b, null, Zero3, Zero3, 1f, VolumeGeometry.Render);
            var f = MeshSurface.MeasurePlacedFramed(a, null, PcPosB, Zero3, 1f, b, null, PcPosB, Zero3, 1f, VolumeGeometry.Render);
            bool Ok(SurfaceContactResult r) => r.Status == "measured" && r.CrossingLength is { } L && PcNear(L.Lower, PcCrossing, 1e-10) && PcNear(L.Upper, PcCrossing, 1e-10) && r.ZeroAreaSkipped is null;
            return (Ok(w.Result) && w.Origin == Zero3 && Ok(f.Result) && f.Origin == PcPosB,
                    $"world {w.Result.Status} {w.Result.Reason} L {w.Result.CrossingLength} frame {w.Origin}; far {f.Result.Status} {f.Result.Reason} L {f.Result.CrossingLength} frame {f.Origin}");
        });

        // PC-5: the former PM-4 pair (A's sliver plus a triangle at FarOrigin, QuadB at -FarOrigin, about 250,000 u apart). Centred on A
        // nothing collapses, so it is measured there: crossing [0, 0] (the members are far apart), nothing skipped, frame FarOrigin.
        // Rejects the refusal the B-centred frame alone gives.
        Case(check, "placed-collapse PC-5: the old PM-4 pair - measured centred on A, crossing [0, 0]", () =>
        {
            var neg = new Vec3(-FarOrigin.X, -FarOrigin.Y, -FarOrigin.Z);
            var a = new TriangleSoup(SliverA.Vertices.Concat(new[] { new Vector3(0, 0, 50), new Vector3(10, 0, 50), new Vector3(0, 10, 50) }).ToArray(), new[] { 0, 1, 2, 3, 4, 5 });
            var (r, o) = MeshSurface.MeasurePlacedFramed(a, null, FarOrigin, Zero3, 1f, QuadB, null, neg, Zero3, 1f, VolumeGeometry.Render);
            return (r.Status == "measured" && r.CrossingLength is { Lower: 0, Upper: 0 } && r.ZeroAreaSkipped is null && o == FarOrigin,
                    $"{r.Status} {r.Reason} L {r.CrossingLength} frame {o}");
        });

        // PC-6: PairFrame.Origin. (i) the PC-1 pair: A's position (B's collapses A's sliver). (ii) SliverA at FarOrigin, QuadB 3 u above
        // it: B's position (both centred frames work; B's is tried first, as before). (iii) the PM-4 pair, where both centred frames
        // collapse a triangle: B's position, as before. (iv) a pair that collapses nothing: (0,0,0). Rejects always-B (i), trying A's
        // centre first (ii: would move rows that measure today) and changing the fallback (iii).
        Case(check, "placed-collapse PC-6: PairFrame.Origin - A's centre only when B's collapses and A's does not", () =>
        {
            var o1 = PairFrame.Origin(PcA(), PcPosA, Zero3, 1f, PcQuadB(700), PcPosB, Zero3, 1f);
            var up = new Vec3(FarOrigin.X, FarOrigin.Y, FarOrigin.Z + 3);
            var o2 = PairFrame.Origin(SliverA, FarOrigin, Zero3, 1f, QuadB, up, Zero3, 1f);
            var (pa, pb, neg) = Pm4Pair();
            var o3 = PairFrame.Origin(pa, FarOrigin, Zero3, 1f, pb, neg, Zero3, 1f);
            var o4 = PairFrame.Origin(Cube(0, 0, 0, 5), FarOrigin, Zero3, 1f, Cube(20, 0, 0, 5), FarOrigin, Zero3, 1f);
            return (o1 == PcPosA && o2 == up && o3 == neg && o4 == Zero3, $"{o1} {o2} {o3} {o4}");
        });

        // PC-7: the alpha-visibility path measures the PC-1 pair the same way: SurfaceVisibility.MeasurePlaced's contact serialises
        // identically to MeshSurface.MeasurePlacedFramed's, in the same frame, and is measured. Rejects the visibility path keeping the
        // B-centred frame (refused) while the plain path moves.
        Case(check, "placed-collapse PC-7: SurfaceVisibility.MeasurePlaced on the PC-1 pair - same contact, same frame, measured", () =>
        {
            var a = PcA(); var b = PcQuadB(700);
            var (v, vo) = SurfaceVisibility.MeasurePlaced(a, null, () => null, PcPosA, Zero3, 1f, b, null, () => null, PcPosB, Zero3, 1f);
            var (p, po) = MeshSurface.MeasurePlacedFramed(a, null, PcPosA, Zero3, 1f, b, null, PcPosB, Zero3, 1f, VolumeGeometry.Render);
            string sv = System.Text.Json.JsonSerializer.Serialize(v.Contact, json), sp = System.Text.Json.JsonSerializer.Serialize(p, json);
            return (sv == sp && vo == po && v.Contact!.Status == "measured", $"{v.Contact!.Status} {v.Contact.Reason} frame {vo} / {po} same {sv == sp}");
        });

        // PC-8: the overlap volume measure shares the frame: MeshVolume.MeasurePlacedFramed on the PC-1 pair reports A's position as its
        // origin. Rejects the volume path keeping B's centre while the surface path moves (the two would disagree on one pair).
        Case(check, "placed-collapse PC-8: MeshVolume.MeasurePlacedFramed on the PC-1 pair - frame centred on A", () =>
        {
            var (r, o) = MeshVolume.MeasurePlacedFramed(PcA(), PcPosA, Zero3, 1f, PcQuadB(700), PcPosB, Zero3, 1f, VolumeGeometry.Render);
            return (o == PcPosA, $"{r.Status} {r.Reason} frame {o}");
        });
    }

    /// <summary>The PM-4 pair: A = SliverA plus a triangle 50 u above, at FarOrigin; B = QuadB plus a sliver of its own (thin in y,
    /// 30 u above its quad), at -FarOrigin. Centred on B, A sits about 250,000 u out and its sliver collapses; centred on A, B does and
    /// its sliver collapses: no frame of the rule undoes the collapse.</summary>
    static (TriangleSoup A, TriangleSoup B, Vec3 PosB) Pm4Pair()
    {
        var a = new TriangleSoup(SliverA.Vertices.Concat(new[] { new Vector3(0, 0, 50), new Vector3(10, 0, 50), new Vector3(0, 10, 50) }).ToArray(), new[] { 0, 1, 2, 3, 4, 5 });
        var b = new TriangleSoup(QuadB.Vertices.Concat(new[] { new Vector3(0, 0, 30), new Vector3(100, 0, 30), new Vector3(100, 0.001f, 30) }).ToArray(),
                                 QuadB.Indices.Concat(new[] { 4, 5, 6 }).ToArray());
        return (a, b, new Vec3(-FarOrigin.X, -FarOrigin.Y, -FarOrigin.Z));
    }
}
