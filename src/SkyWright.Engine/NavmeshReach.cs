using System.Numerics;
using Mutagen.Bethesda.Skyrim;
using static SkyWright.Engine.ExactDist;

using HousecarlCore;

namespace SkyWright.Engine;

// NAVMESH DISTANCE (G1 build list, Stage A, row 4): the exact 3D distance from an object's geometry (or a model-less
// anchor) to the nearest winning navmesh triangle not flagged deleted (Mutagen NavmeshTriangle.Flag.Deleted). A detail,
// not a class: it says how far the nearest NPC-walkable triangle is, never "where the player can stand".
// Distances are computed in double: point-triangle by the closest-point construction (Ericson, Real-Time Collision
// Detection §5.1.5), triangle-triangle as 0 when they intersect (MeshIntersection's exact predicate is not needed here:
// a crossing gives a segment-triangle distance of 0 below) and otherwise the minimum of the six edge-to-triangle
// distances, each the minimum over the edge of the point-triangle distance (convex in the edge parameter, minimised by
// ternary search to 1e-9 of the edge length, plus the endpoints) — the closest pair of two disjoint triangles always has
// one point on an edge of one of them.

public readonly record struct NavTriangle(Vector3 A, Vector3 B, Vector3 C, ushort Flags, string Navmesh, int Index);

/// <summary>Status <c>measured</c> or <c>refused</c>. Horizontal and Vertical are the components of the closest pair.</summary>
public sealed record NavDistance(string Status, string? Reason, double? Distance, double? Horizontal, double? Vertical,
                                 string? Navmesh, int? Triangle);

public static class NavmeshReach
{
    // the double geometry lives in ExactDist below

    public const string PolicyVersion = "navmesh_distance_v1";

    public static bool IsDeleted(ushort flags) => (flags & (ushort)NavmeshTriangle.Flag.Deleted) != 0;

    public static NavDistance FromPoint(Vector3 point, IReadOnlyList<NavTriangle> triangles)
    {
        var p = D(point);
        return Best(triangles, t => { var q = Closest(p, D(t.A), D(t.B), D(t.C)); return (p, q); });
    }

    public static NavDistance FromSoup(TriangleSoup soup, IReadOnlyList<NavTriangle> triangles)
    {
        if (soup is null || soup.TriangleCount == 0) return new("refused", "the object has no triangles", null, null, null, null, null);
        var v = soup.Vertices; var ix = soup.Indices;
        return Best(triangles, t =>
        {
            var (a, b, c) = (D(t.A), D(t.B), D(t.C));
            (DV P, DV Q) best = default; double bd = double.MaxValue;
            for (int k = 0; k < ix.Length; k += 3)
            {
                var (x, y, z) = (D(v[ix[k]]), D(v[ix[k + 1]]), D(v[ix[k + 2]]));
                var pair = TriTri(x, y, z, a, b, c);
                double d = (pair.P - pair.Q).Len;
                if (d < bd) { bd = d; best = pair; if (d == 0) break; }
            }
            return best;
        });
    }

    // ---------------- Stage B additions (G1 build list) ----------------
    // A model-less reference with an XPRM box (a trigger) is measured from its box; Bounds are the full extents, centred
    // on the reference origin (the convention NavcutGeometry.ModelBox uses). Spheres and planes are not boxes: null.
    public static TriangleSoup? PrimitiveBoxSoup(string type, Vector3 bounds)
    {
        if (!string.Equals(type, "Box", StringComparison.OrdinalIgnoreCase)) return null;
        var h = bounds / 2f;
        if (!(h.X > 0 && h.Y > 0 && h.Z > 0)) return null;
        var v = new[]
        {
            new Vector3(-h.X, -h.Y, -h.Z), new Vector3(h.X, -h.Y, -h.Z), new Vector3(h.X, h.Y, -h.Z), new Vector3(-h.X, h.Y, -h.Z),
            new Vector3(-h.X, -h.Y, h.Z), new Vector3(h.X, -h.Y, h.Z), new Vector3(h.X, h.Y, h.Z), new Vector3(-h.X, h.Y, h.Z),
        };
        var i = new[] { 0,2,1, 0,3,2, 4,5,6, 4,6,7, 0,1,5, 0,5,4, 2,3,7, 2,7,6, 1,2,6, 1,6,5, 3,0,4, 3,4,7 };
        return new TriangleSoup(v, i);
    }

    // THE REACHABLE BAND: each navmesh triangle swept straight up from +lo to +hi (a standing body's height over the
    // ground NPCs and the player walk on). The prism is convex, so the distance from a point to it is 0 inside and
    // otherwise the minimum over its eight face triangles; a soup is 0 when any vertex is inside or any of its
    // triangles meets a face (a triangle crossing a convex solid without a vertex inside crosses its boundary), else the
    // minimum face distance. Exact in double, like the distances above.
    public static NavDistance FromPointBand(Vector3 point, IReadOnlyList<NavTriangle> triangles, float lo, float hi)
    {
        var p = D(point);
        return Best(triangles, t =>
        {
            var (a, b, c) = (D(t.A), D(t.B), D(t.C));
            if (InPrism(p, a, b, c, lo, hi)) return (p, p);
            (DV, DV) best = default; double bd = double.MaxValue;
            foreach (var (x, y, z) in PrismFaces(a, b, c, lo, hi))
            {
                var q = Closest(p, x, y, z); double d = (p - q).Len;
                if (d < bd) { bd = d; best = (p, q); }
            }
            return best;
        });
    }

    public static NavDistance FromSoupBand(TriangleSoup soup, IReadOnlyList<NavTriangle> triangles, float lo, float hi)
    {
        if (soup is null || soup.TriangleCount == 0) return new("refused", "the object has no triangles", null, null, null, null, null);
        var v = soup.Vertices; var ix = soup.Indices;
        return Best(triangles, t =>
        {
            var (a, b, c) = (D(t.A), D(t.B), D(t.C));
            foreach (var w in v) { var dw = D(w); if (InPrism(dw, a, b, c, lo, hi)) return (dw, dw); }
            (DV P, DV Q) best = default; double bd = double.MaxValue;
            var faces = PrismFaces(a, b, c, lo, hi);
            for (int k = 0; k < ix.Length && bd > 0; k += 3)
            {
                var (x, y, z) = (D(v[ix[k]]), D(v[ix[k + 1]]), D(v[ix[k + 2]]));
                foreach (var (fa, fb, fc) in faces)
                {
                    var pair = TriTri(x, y, z, fa, fb, fc);
                    double d = (pair.P - pair.Q).Len;
                    if (d < bd) { bd = d; best = pair; if (d == 0) break; }
                }
            }
            return best;
        });
    }

    static (DV, DV, DV)[] PrismFaces(DV a, DV b, DV c, float lo, float hi)
    {
        DV L = new(0, 0, lo), H = new(0, 0, hi);
        DV a0 = a + L, b0 = b + L, c0 = c + L, a1 = a + H, b1 = b + H, c1 = c + H;
        return new[] { (a0, b0, c0), (a1, b1, c1), (a0, b0, b1), (a0, b1, a1), (b0, c0, c1), (b0, c1, b1), (c0, a0, a1), (c0, a1, c1) };
    }

    // Inside the prism: the point's plan position inside the triangle's plan projection, and its height between the
    // triangle's plane at that position +lo and +hi. A triangle seen edge-on from above (vertical) has no plan area and
    // no interior: never inside (its faces still give the distance).
    static bool InPrism(DV p, DV a, DV b, DV c, float lo, float hi)
    {
        double den = (b.Y - c.Y) * (a.X - c.X) + (c.X - b.X) * (a.Y - c.Y);
        if (!(Math.Abs(den) > 1e-12)) return false;
        double l1 = ((b.Y - c.Y) * (p.X - c.X) + (c.X - b.X) * (p.Y - c.Y)) / den;
        double l2 = ((c.Y - a.Y) * (p.X - c.X) + (a.X - c.X) * (p.Y - c.Y)) / den;
        double l3 = 1 - l1 - l2;
        if (l1 < 0 || l2 < 0 || l3 < 0) return false;
        double zg = l1 * a.Z + l2 * b.Z + l3 * c.Z;
        return p.Z >= zg + lo && p.Z <= zg + hi;
    }

    static NavDistance Best(IReadOnlyList<NavTriangle> triangles, Func<NavTriangle, (DV P, DV Q)> pairOf)
    {
        double best = double.MaxValue; (DV P, DV Q) bp = default; NavTriangle? bt = null;
        foreach (var t in triangles)
        {
            if (IsDeleted(t.Flags)) continue;
            var pair = pairOf(t);
            double d = (pair.P - pair.Q).Len;
            if (!double.IsFinite(d))   // never compared into an answer: a non-finite distance refuses by name
                return new("refused", $"non-finite distance to navmesh {t.Navmesh} triangle {t.Index}", null, null, null, t.Navmesh, t.Index);
            if (d < best) { best = d; bp = pair; bt = t; }
        }
        if (bt is not { } tri) return new("refused", triangles.Count == 0 ? "no navmesh triangle in the cells searched" : "every navmesh triangle in the cells searched is flagged deleted",
                                          null, null, null, null, null);
        var dv = bp.Q - bp.P;
        return new("measured", null, best, Math.Sqrt(dv.X * dv.X + dv.Y * dv.Y), Math.Abs(dv.Z), tri.Navmesh, tri.Index);
    }
}

/// <summary>Exact double-precision point/segment/triangle distances shared by the navmesh distance and the anchor test.</summary>
internal static class ExactDist
{
    // ---------------- double geometry ----------------
    internal readonly record struct DV(double X, double Y, double Z)
    {
        public static DV operator +(DV a, DV b) => new(a.X + b.X, a.Y + b.Y, a.Z + b.Z);
        public static DV operator -(DV a, DV b) => new(a.X - b.X, a.Y - b.Y, a.Z - b.Z);
        public static DV operator *(DV a, double s) => new(a.X * s, a.Y * s, a.Z * s);
        public double Len => Math.Sqrt(X * X + Y * Y + Z * Z);
    }
    internal static DV D(Vector3 v) => new(v.X, v.Y, v.Z);
    internal static double Dot(DV a, DV b) => a.X * b.X + a.Y * b.Y + a.Z * b.Z;
    internal static DV Cross(DV a, DV b) => new(a.Y * b.Z - a.Z * b.Y, a.Z * b.X - a.X * b.Z, a.X * b.Y - a.Y * b.X);

    /// <summary>Closest point of triangle abc to p (Ericson §5.1.5).</summary>
    internal static DV Closest(DV p, DV a, DV b, DV c)
    {
        // A degenerate triangle (coincident or collinear vertices) has no interior: its closest point lies on an edge.
        // The barycentric construction below divides by zero there, so it is never used for one (G1 Stage A, A15/N6).
        var nrm = Cross(b - a, c - a);
        if (!(Dot(nrm, nrm) > 0)) return OnSegments(p, a, b, c);
        DV ab = b - a, ac = c - a, ap = p - a;
        double d1 = Dot(ab, ap), d2 = Dot(ac, ap);
        if (d1 <= 0 && d2 <= 0) return a;
        DV bp = p - b; double d3 = Dot(ab, bp), d4 = Dot(ac, bp);
        if (d3 >= 0 && d4 <= d3) return b;
        double vc = d1 * d4 - d3 * d2;
        if (vc <= 0 && d1 >= 0 && d3 <= 0) return a + ab * (d1 / (d1 - d3));
        DV cp = p - c; double d5 = Dot(ab, cp), d6 = Dot(ac, cp);
        if (d6 >= 0 && d5 <= d6) return c;
        double vb = d5 * d2 - d1 * d6;
        if (vb <= 0 && d2 >= 0 && d6 <= 0) return a + ac * (d2 / (d2 - d6));
        double va = d3 * d6 - d5 * d4;
        if (va <= 0 && d4 - d3 >= 0 && d5 - d6 >= 0) return b + (c - b) * ((d4 - d3) / ((d4 - d3) + (d5 - d6)));
        double den = 1 / (va + vb + vc);
        return a + ab * (vb * den) + ac * (vc * den);
    }

    static DV OnSegment(DV p, DV a, DV b)
    {
        DV ab = b - a; double l = Dot(ab, ab);
        if (!(l > 0)) return a;
        double t = Math.Clamp(Dot(p - a, ab) / l, 0, 1);
        return a + ab * t;
    }
    static DV OnSegments(DV p, DV a, DV b, DV c)
    {
        var best = OnSegment(p, a, b);
        foreach (var q in new[] { OnSegment(p, b, c), OnSegment(p, c, a) }) if ((p - q).Len < (p - best).Len) best = q;
        return best;
    }

    /// <summary>Where segment pq crosses triangle abc's plane inside the triangle, if it does.</summary>
    internal static DV? SegmentHit(DV p, DV q, DV a, DV b, DV c)
    {
        DV n = Cross(b - a, c - a);
        double dp = Dot(n, p - a), dq = Dot(n, q - a);
        if (dp > 0 && dq > 0 || dp < 0 && dq < 0 || dp == dq) return null;
        DV x = p + (q - p) * (dp / (dp - dq));
        double s1 = Dot(Cross(b - a, x - a), n), s2 = Dot(Cross(c - b, x - b), n), s3 = Dot(Cross(a - c, x - c), n);
        return (s1 >= 0 && s2 >= 0 && s3 >= 0) || (s1 <= 0 && s2 <= 0 && s3 <= 0) ? x : null;
    }

    internal static (DV P, DV Q) SegTri(DV p, DV q, DV a, DV b, DV c)
    {
        if (SegmentHit(p, q, a, b, c) is { } hit) return (hit, hit);
        double F(double t) { var x = p + (q - p) * t; return (x - Closest(x, a, b, c)).Len; }
        double lo = 0, hi = 1;
        for (int i = 0; i < 200 && hi - lo > 1e-12; i++)
        {
            double m1 = lo + (hi - lo) / 3, m2 = hi - (hi - lo) / 3;
            if (F(m1) <= F(m2)) hi = m2; else lo = m1;
        }
        (DV, DV) Pair(double t) { var x = p + (q - p) * t; return (x, Closest(x, a, b, c)); }
        var cands = new[] { Pair(0), Pair(1), Pair((lo + hi) / 2) };
        return cands.OrderBy(z => (z.Item1 - z.Item2).Len).First();
    }

    internal static (DV P, DV Q) TriTri(DV x, DV y, DV z, DV a, DV b, DV c)
    {
        var best = SegTri(x, y, a, b, c);
        void Try((DV P, DV Q) cand) { if ((cand.P - cand.Q).Len < (best.P - best.Q).Len) best = cand; }
        Try(SegTri(y, z, a, b, c)); Try(SegTri(z, x, a, b, c));
        // edges of the navmesh triangle against the object's triangle (pair returned object-first)
        foreach (var (s, t) in new[] { (a, b), (b, c), (c, a) }) { var r = SegTri(s, t, x, y, z); Try((r.Q, r.P)); }
        return best;
    }
}
