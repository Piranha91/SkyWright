using System.Numerics;

using HousecarlCore;

namespace SkyWright.Engine;

// THE CONSUMERS' PREDICATES for MemberMotion.Decide (lane M, GATE-A §1): each pair measure that reads a member under motion
// states only its per-pose test; the shared decision loop does the sampling and refinement. Each predicate is the per-sample
// test its swept consumer applies today, unchanged: the surface/volume contact (SweptGeometry.Contact), the anchor's body
// (AnchorMotion.TestSwept) and the supplied world box (MeshSurfaceBox.BoxContactSwept). A witness's measurements are kept in
// the capture.

/// <summary>What a predicate measured at its witness (the crossing's lower bound, the intersection volume's lower bound, the
/// crossing by material pair, the anchor test's reason) and a refusal raised by a pose the test cannot read.</summary>
public sealed class MemberCapture
{
    public double? CrossingLower { get; internal set; }
    public double? VolumeLower { get; internal set; }
    public IReadOnlyDictionary<string, double>? CrossingByTags { get; internal set; }
    public string? WitnessReason { get; internal set; }
    public string? Refusal { get; internal set; }
    /// <summary>The smallest (clearance - bound) over the poses cleared, for the anchor (null when no opaque surface was met).</summary>
    public double? MinMargin { get; internal set; }
}

public static class MemberPredicates
{
    static double Above(double d) => Math.BitIncrement(d);

    static bool Overlap(Aabb a, Aabb b, double grow) =>
        a.Min.X - grow <= b.Max.X && b.Min.X <= a.Max.X + grow && a.Min.Y - grow <= b.Max.Y && b.Min.Y <= a.Max.Y + grow
        && a.Min.Z - grow <= b.Max.Z && b.Min.Z <= a.Max.Z + grow;

    /// <summary>A placed partner (solid or open surface) against a posed member: crosses when the pose crosses or buries itself
    /// in the partner (a closed opaque member against a closed partner: a positive intersection volume), clear when no surface
    /// lies within the bound and neither is inside the other, else open. Null with the reason when the partner cannot be read.</summary>
    public static (MemberPredicate? Predicate, string? Refusal) Contact(TriangleSoup partner, string[]? partnerTags, bool partnerIsSolid, VolumeGeometry geometry,
                                                                       MemberCapture capture)
    {
        if (partner is null || partner.TriangleCount == 0) return (null, "partner: empty_mesh");
        // the clear test reads the drawn view of both members (ShapeDrawing); the crossing measure keeps the whole soups
        var (pd, partnerIsSolidDrawn) = SweptGeometry.DrawnPartner(partner, partnerTags, partnerIsSolid);
        partnerIsSolid = partnerIsSolidDrawn;
        if (pd.TriangleCount == 0) return ((_, _, _) => MemberTest.Clear(double.PositiveInfinity), null);
        var pMesh = MeshIntersection.PrepareSurfaceMesh(pd, out var why);
        if (pMesh is null) return (null, "partner: " + why);
        var pBox = SpatialOverlap.SoupBounds(pd);
        bool? movingSolid = null;
        return ((soup, tags, delta) =>
        {
            // the member's solidity is read once, from the first pose (every pose has the same triangles), as the swept region does
            movingSolid ??= MeshIntersection.PrepareVolumeSolid(soup, 50_000_000, default).Solid is not null
                            && !(geometry == VolumeGeometry.Render && tags is not null && tags.Any(x => x != SurfaceMaterial.Opaque));
            var (sd, _) = MeshSurface.Drawn(soup, tags);
            if (sd.TriangleCount == 0) return MemberTest.Clear(double.PositiveInfinity);
            var sBox = SpatialOverlap.SoupBounds(sd);
            if (!Overlap(sBox, pBox, delta)) return MemberTest.Clear(Above(delta));
            var sMesh = MeshIntersection.PrepareSurfaceMesh(sd, out _);
            if (sMesh is not null && !MeshIntersection.MeshesWithin(sMesh, pMesh, delta))
            {
                // no surface within the bound: each component is wholly inside or outside the other
                bool contained = false;
                if (partnerIsSolid && sd.Vertices.Length > 0) { var v = SweptGeometry.FirstVertex(sd, soup); contained |= MeshIntersection.PointInSolid(pMesh, v.X, v.Y, v.Z) != 0; }
                if (movingSolid == true && pd.Vertices.Length > 0) { var v = SweptGeometry.FirstVertex(pd, partner); contained |= MeshIntersection.PointInSolid(sMesh, v.X, v.Y, v.Z) != 0; }
                if (!contained) return MemberTest.Clear(Above(delta));
            }
            if (!Overlap(sBox, pBox, 0)) return MemberTest.Open;
            if (movingSolid == true && partnerIsSolid)
            {
                var vr = MeshVolume.Measure(soup, pd, geometry);
                if (vr.Status == "measured" && vr.Intersection is { Lower: > 0 } iv) { capture.VolumeLower = iv.Lower; capture.CrossingLower = null; capture.CrossingByTags = null; return MemberTest.Crosses(); }
            }
            var c = MeshSurface.Measure(soup, tags, partner, partnerTags, geometry);
            if (c.Status != "measured") return MemberTest.Open;
            double cross = c.CrossingLength?.Lower ?? 0, buried = Math.Max(c.AInB?.Inside?.Lower ?? 0, c.BInA?.Inside?.Lower ?? 0);
            if (cross > c.RoundingBound || buried > 0)
            {
                capture.CrossingLower = cross; capture.VolumeLower = null; capture.CrossingByTags = c.CrossingLengthByTags;
                // alpha-only: the crossing has length and none of it is opaque x opaque (a buried pose is not named alpha-only)
                var op = MeshSurface.OpaqueCrossing(c);
                return MemberTest.Crosses(cross > c.RoundingBound && op is not { Lower: > 0 } ? "alpha-only" : null);
            }
            return MemberTest.Open;
        }, null);
    }

    /// <summary>An actor's body at an anchor against a posed member (MeshAnchor's body reading): crosses when an opaque surface
    /// meets the body, clear by the body's clearance less a rounding allowance, open when the reading is undecided.</summary>
    public static MemberPredicate Anchor(Vector3 anchor, BodyExtent body, HalfSpace? exclude, MemberCapture capture)
    {
        double tol = 1e-6 * (1 + Math.Abs(anchor.X) + Math.Abs(anchor.Y) + Math.Abs(anchor.Z));
        return (soup, tags, delta) =>
        {
            var r = MeshAnchor.Test(soup, tags, anchor, body, exclude);
            if (r.Decision == "meets") { capture.WitnessReason = r.Reason; return MemberTest.Crosses(); }
            if (r.Decision == "undecided") return MemberTest.Open;
            if (r.BodyClearance is not { } cl) return MemberTest.Clear(double.PositiveInfinity);   // no opaque surface at all
            double margin = cl - delta - tol;
            if (margin > 0) capture.MinMargin = capture.MinMargin is { } m ? Math.Min(m, margin) : margin;   // a pose cleared by its bound
            return MemberTest.Clear(cl - tol);
        };
    }

    /// <summary>A world box against a posed member (opaque triangles decide): crosses when an opaque triangle meets the box,
    /// clear when none meets the box grown by the bound (rounded outward, plus a rounding allowance), else open.</summary>
    public static MemberPredicate Box(Aabb box, MemberCapture capture)
    {
        double tol = 1e-6 * (1 + Math.Abs(box.Min.X) + Math.Abs(box.Min.Y) + Math.Abs(box.Min.Z) + Math.Abs(box.Max.X) + Math.Abs(box.Max.Y) + Math.Abs(box.Max.Z));
        static float Down(double v) { float f = (float)v; return f > v ? MathF.BitDecrement(f) : f; }
        static float Up(double v) { float f = (float)v; return f < v ? MathF.BitIncrement(f) : f; }
        return (soup, tags, delta) =>
        {
            var hit = MeshSurfaceBox.BoxContact(soup, tags, box);
            if (hit.Reason is not null) { capture.Refusal ??= hit.Reason; return MemberTest.Open; }
            if (hit.OpaqueMeets) return MemberTest.Crosses();
            double d = delta + tol;
            var grown = new Aabb(new Vec3(Down(box.Min.X - d), Down(box.Min.Y - d), Down(box.Min.Z - d)), new Vec3(Up(box.Max.X + d), Up(box.Max.Y + d), Up(box.Max.Z + d)), false);
            return MeshSurfaceBox.BoxContact(soup, tags, grown).OpaqueMeets ? MemberTest.Open : MemberTest.Clear(Above(delta));
        };
    }
}
