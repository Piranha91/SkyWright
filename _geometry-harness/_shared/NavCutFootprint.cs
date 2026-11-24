using System.Numerics;
using HousecarlCore;
using SkyWright.Engine;

namespace HarnessShared;

// THE CUT OF A PLACED OBSTACLE (navedit's cut by reference; task 2026-11-16-navmesh-repair). Pure: from the obstacle's
// collision already placed in the world and the navmesh by it, the convex parts NavEdit.CutMany cuts, each tied to the
// navmesh triangle it was measured on.
//   the rule   a spot on the navmesh is blocked when an actor standing there would walk into the collision: some of it
//              lies between step height (DetectorModel.StepHeight, 48 u: what an actor steps onto is no obstacle) and a
//              scale-1 humanoid controller's top (ActorBand.ControllerTop of ActorBand.ScaleOneHeight, 108.8 u) above
//              THAT SPOT, or the spot lies under the inside of a solid across that whole band;
//   own height each navmesh triangle is measured against its own plane, so navmesh lying on a rock (vanilla drapes it
//              over low rocks) is not blocked by the rock it lies on, and ground beside the rock is. Measured from one
//              floor under the obstacle instead, rock pile 0E6420 lost 87 % of the ground vanilla keeps walkable inside
//              its hull, nearly all of it on the rocks' tops, where an actor standing finds nothing in its band;
//   inside     under the inside of a solid: an odd number of the collision's surfaces above the band's top, counted per
//              connected piece (triangles sharing a corner), so a buried rock open at the bottom counts as solid and two
//              overlapping rocks do not cancel. Known limit: a single connected mesh that overlaps itself counts its
//              overlap as outside (only where no surface of it is in the band there);
//   parts      the blocked region of each triangle, in a window round it, as convex parts: the plan is cut into
//              vertical slabs at every end and crossing of the collision's edges there, each gap between edges is
//              classified at its middle, and blocked gaps are merged while they stay convex; each part grown by the
//              margin (a square at each corner, then the hull) and cut from its triangle, and with a margin from the
//              triangles sharing its edges too.
// The collision itself (the reference's base mesh, its bodies on the layers an actor meets, its transform) comes from
// the owners navedit calls: OverlapIndex.ModelOf, AssetResolver, NifGeometry, CollisionLayers.RoleSoups and
// SpatialOverlap.TransformSoup.

/// <summary>A convex part to cut, measured on navmesh triangle <paramref name="Triangle"/> and cut from the triangles in
/// <paramref name="Applies"/>.</summary>
public sealed record NavCutPart(int Triangle, IReadOnlyList<(float X, float Y)> Outline, IReadOnlyList<int> Applies);

/// <summary>An obstacle's cut: "planned" with its parts, or "refused" with why; how many navmesh triangles were measured
/// and how many collision triangles stood in some triangle's band.</summary>
public sealed record NavCutPlan(string Status, string? Why, IReadOnlyList<NavCutPart> Parts, int Measured, int InBand)
{
    public static NavCutPlan Refused(string why, int measured = 0, int inBand = 0) => new("refused", why, Array.Empty<NavCutPart>(), measured, inBand);
}

public static class NavCutFootprint
{
    public static float SliceBottom => DetectorModel.StepHeight;
    public static float SliceTop => ActorBand.ControllerTop(ActorBand.ScaleOneHeight);
    /// <summary>A cut part smaller than this, in u², is left out: NavEdit.Cut refuses an outline with no area.</summary>
    public const double MinPiece = 1.0;
    /// <summary>How far, in u, a part's corner may sit off the line through its neighbours and still count as straight on: a
    /// little over a float step at world coordinates (0.0156 u past 131,072).</summary>
    const double StraightTol = 0.02;
    /// <summary>A gap between collision edges thinner than this, in u, takes a blocked neighbour's side (see Regions): the
    /// cut's own narrowest ground (NavEdit.Thin).</summary>
    const double Thin = NavEdit.Thin;

    /// <summary>The cut of an obstacle whose collision, in the world, is <paramref name="world"/>, on navmesh
    /// <paramref name="g"/>: every triangle near enough to meet it is measured on its own plane (see the file's notes).
    /// Refused in one sentence when the obstacle has no collision, no triangle is near it, or nothing of it blocks an
    /// actor standing on any of them.</summary>
    public static NavCutPlan OnNavmesh(TriangleSoup world, NavmeshGeometry g, float margin = 0)
    {
        int n = world.TriangleCount;
        if (n == 0) return NavCutPlan.Refused("The obstacle has no collision triangles to cut by.");
        var W = world.Vertices; var I = world.Indices;

        // connected pieces of the collision, by shared corner position
        var parent = Enumerable.Range(0, n).ToArray();
        int Find(int x) { while (parent[x] != x) x = parent[x] = parent[parent[x]]; return x; }
        var firstAt = new Dictionary<Vector3, int>();
        for (int t = 0; t < n; t++)
            for (int k = 0; k < 3; k++)
                if (firstAt.TryGetValue(W[I[3 * t + k]], out var o)) parent[Find(t)] = Find(o); else firstAt[W[I[3 * t + k]]] = t;
        var piece = Enumerable.Range(0, n).Select(Find).ToArray();
        var pieceIx = piece.Distinct().Select((p, i) => (p, i)).ToDictionary(x => x.p, x => x.i);

        var lo = new Vector3(float.MaxValue); var hi = new Vector3(float.MinValue);
        foreach (var i in I) { lo = Vector3.Min(lo, W[i]); hi = Vector3.Max(hi, W[i]); }

        var parts = new List<NavCutPart>();
        int measured = 0, inPlan = 0; var inBand = new HashSet<int>();
        var tris = g.Triangles; var V = g.Vertices;
        for (int ti = 0; ti < tris.Count; ti++)
        {
            var T = tris[ti];
            Vec3 a = V[T.A], b = V[T.B], c = V[T.C];
            float tx0 = Math.Min(a.X, Math.Min(b.X, c.X)), tx1 = Math.Max(a.X, Math.Max(b.X, c.X)), ty0 = Math.Min(a.Y, Math.Min(b.Y, c.Y)), ty1 = Math.Max(a.Y, Math.Max(b.Y, c.Y));
            float tz0 = Math.Min(a.Z, Math.Min(b.Z, c.Z)), tz1 = Math.Max(a.Z, Math.Max(b.Z, c.Z));
            if (tx1 < lo.X - margin || tx0 > hi.X + margin || ty1 < lo.Y - margin || ty0 > hi.Y + margin) continue;
            inPlan++;
            if (hi.Z < tz0 + SliceBottom || lo.Z > tz1 + SliceTop) continue;   // wholly under its step, or wholly over an actor
            // its plane: z = pa x + pb y + pc
            double den = ((double)b.X - a.X) * ((double)c.Y - a.Y) - ((double)b.Y - a.Y) * ((double)c.X - a.X);
            if (Math.Abs(den) < 1e-9) continue;                                                // flat in plan: nothing to stand on
            double pa = (((double)b.Z - a.Z) * ((double)c.Y - a.Y) - ((double)b.Y - a.Y) * ((double)c.Z - a.Z)) / den;
            double pb = (((double)b.X - a.X) * ((double)c.Z - a.Z) - ((double)b.Z - a.Z) * ((double)c.X - a.X)) / den;
            double pc = a.Z - pa * a.X - pb * a.Y;
            double Pz(double x, double y) => pa * x + pb * y + pc;              // the triangle's own height at (x, y)
            double H(Vector3 p) => p.Z - Pz(p.X, p.Y);
            measured++;

            // the window round the triangle, and the collision triangles over it
            double wx0 = tx0 - 1, wx1 = tx1 + 1, wy0 = ty0 - 1, wy1 = ty1 + 1;
            var near = new List<int>();
            for (int t = 0; t < n; t++)
            {
                Vector3 p = W[I[3 * t]], q = W[I[3 * t + 1]], r = W[I[3 * t + 2]];
                if (Math.Max(p.X, Math.Max(q.X, r.X)) < wx0 || Math.Min(p.X, Math.Min(q.X, r.X)) > wx1 || Math.Max(p.Y, Math.Max(q.Y, r.Y)) < wy0 || Math.Min(p.Y, Math.Min(q.Y, r.Y)) > wy1) continue;
                if (Math.Max(H(p), Math.Max(H(q), H(r))) <= SliceBottom) continue;           // wholly under the step here
                near.Add(t);
            }
            if (near.Count == 0) continue;

            // the edges a classification can change across: each near triangle cut at the band's bottom and top (its
            // plan edges in the band and above it), clipped to the window, and the window's own sides
            var segs = new List<(double X1, double Y1, double X2, double Y2)>
            {
                (wx0, wy0, wx1, wy0), (wx1, wy0, wx1, wy1), (wx1, wy1, wx0, wy1), (wx0, wy1, wx0, wy0),
            };
            foreach (var t in near)
            {
                var tri = new List<Vector3> { W[I[3 * t]], W[I[3 * t + 1]], W[I[3 * t + 2]] };
                var above = Clip(tri, p => (float)(H(p) - SliceBottom));
                foreach (var part in new[] { Clip(above, p => (float)(SliceTop - H(p))), Clip(above, p => (float)(H(p) - SliceTop)) })
                    for (int k = 0; k < part.Count; k++)
                    {
                        var (p, q) = (part[k], part[(k + 1) % part.Count]);
                        if (Math.Abs(p.X - q.X) + Math.Abs(p.Y - q.Y) > 1e-6 && ClipToBox(p.X, p.Y, q.X, q.Y, wx0, wy0, wx1, wy1) is { } s) segs.Add(s);
                    }
            }
            var odd = new bool[pieceIx.Count];
            bool Blocked(double x, double y)
            {
                Array.Clear(odd);
                foreach (var t in near)
                {
                    Vector3 p = W[I[3 * t]], q = W[I[3 * t + 1]], r = W[I[3 * t + 2]];
                    double d = ((double)q.Y - r.Y) * (p.X - r.X) + ((double)r.X - q.X) * (p.Y - r.Y);
                    if (d == 0) continue;
                    double l1 = (((double)q.Y - r.Y) * (x - r.X) + ((double)r.X - q.X) * (y - r.Y)) / d, l2 = (((double)r.Y - p.Y) * (x - r.X) + ((double)p.X - r.X) * (y - r.Y)) / d;
                    if (l1 < 0 || l2 < 0 || 1 - l1 - l2 < 0) continue;
                    double h = l1 * p.Z + l2 * q.Z + (1 - l1 - l2) * r.Z - Pz(x, y);
                    if (h > SliceBottom && h < SliceTop) { inBand.Add(t); return true; }
                    if (h >= SliceTop) odd[pieceIx[piece[t]]] ^= true;
                }
                return odd.Any(o => o);
            }
            var triPlan = new List<(double X, double Y)> { (a.X, a.Y), (b.X, b.Y), (c.X, c.Y) };
            if (Area2(triPlan) < 0) triPlan.Reverse();
            var applies = new List<int> { ti };
            if (margin > 0)
                for (int e = 0; e < 3; e++)
                {
                    short f = e switch { 0 => T.EdgeLink01, 1 => T.EdgeLink12, _ => T.EdgeLink20 };
                    if ((T.Flags & (1 << e)) == 0 && f >= 0 && f < tris.Count) applies.Add(f);
                }
            foreach (var reg in Regions(segs, Blocked))
            {
                var p = margin > 0 ? Grow(reg, margin) : reg;
                var q = Tidy(p);
                if (q.Count < 3 || Math.Abs(Area2(q)) / 2 < MinPiece || Overlap(q, triPlan) <= NavEdit.CutDepth) continue;
                parts.Add(new NavCutPart(ti, q.Select(v => ((float)v.X, (float)v.Y)).ToList(), applies));
            }
        }
        if (inPlan == 0)
            return NavCutPlan.Refused($"{g.FormKey} has no triangle under or beside the obstacle, so there is nothing to cut; check the navmesh and the obstacle.");
        if (parts.Count == 0)
            return NavCutPlan.Refused($"Nothing of the obstacle's collision stands between step height ({SliceBottom:0.#} u) and an actor's top ({SliceTop:0.#} u) above the navmesh by it, so an actor walking there is not blocked and there is nothing to cut.", measured, inBand.Count);
        // largest first, so a thin part's lines run through ground its neighbours have already taken
        return new NavCutPlan("planned", null, parts.OrderByDescending(p => Math.Abs(Area2(p.Outline.Select(v => ((double)v.X, (double)v.Y)).ToList()))).ToList(), measured, inBand.Count);
    }

    /// <summary>The regions of the plan that <paramref name="blocked"/> marks, as convex parts, counter-clockwise: the plan
    /// cut into vertical slabs at every segment end and crossing, each gap between two segments in a slab classified at its
    /// middle (no segment crosses a gap, so one point decides it), gaps stacked in a slab merged, and the stacks merged left
    /// to right while they stay convex. The segments must bound every region the classification can change across; the
    /// gaps outside them are not classified.</summary>
    public static List<List<(double X, double Y)>> Regions(IReadOnlyList<(double X1, double Y1, double X2, double Y2)> input, Func<double, double, bool> blocked)
    {
        const double Snap = 1e-4, Eps = 1e-7;
        List<(double X1, double Y1, double X2, double Y2)> S = input.Select(s => s.X1 < s.X2 || (s.X1 == s.X2 && s.Y1 <= s.Y2) ? s : (X1: s.X2, Y1: s.Y2, X2: s.X1, Y2: s.Y1)).Distinct().OrderBy(s => s.X1).ToList();
        if (S.Count == 0) return new();
        var xs = new List<double>();
        foreach (var s in S) { xs.Add(s.X1); xs.Add(s.X2); }
        for (int i = 0; i < S.Count; i++)
            for (int j = i + 1; j < S.Count && S[j].X1 <= S[i].X2 + Snap; j++)
                if (CrossX(S[i], S[j]) is { } x) xs.Add(x);
        xs.Sort();
        var X = new List<double>();
        foreach (var x in xs) if (X.Count == 0 || x > X[^1] + Snap) X.Add(x);
        int Ix(double x) { int k = X.BinarySearch(x); if (k >= 0) return k; k = ~k; return k == 0 ? 0 : k == X.Count ? X.Count - 1 : (x - X[k - 1] <= X[k] - x ? k - 1 : k); }
        double YAt((double X1, double Y1, double X2, double Y2) s, double x) => s.X2 - s.X1 < 1e-12 ? s.Y1 : s.Y1 + (s.Y2 - s.Y1) * Math.Clamp((x - s.X1) / (s.X2 - s.X1), 0, 1);

        var span = new List<int>[Math.Max(X.Count - 1, 0)];
        for (int k = 0; k < span.Length; k++) span[k] = new();
        for (int i = 0; i < S.Count; i++)
        {
            int a = Ix(S[i].X1), b = Ix(S[i].X2);
            for (int k = a; k < b; k++) span[k].Add(i);
        }

        var done = new List<List<(double X, double Y)>>();
        var open = new List<(List<(double X, double Y)> Lo, List<(double X, double Y)> Up)>();
        for (int k = 0; k < span.Length; k++)
        {
            double xl = X[k], xr = X[k + 1], xm = (xl + xr) / 2;
            var r = span[k].Select(i => (L: YAt(S[i], xl), R: YAt(S[i], xr), M: YAt(S[i], xm))).OrderBy(q => q.M).ThenBy(q => q.L + q.R).ToList();
            var isBlocked = new bool[r.Count + 1];
            for (int j = 1; j < r.Count; j++) isBlocked[j] = r[j].M - r[j - 1].M > Thin && blocked(xm, (r[j - 1].M + r[j].M) / 2);
            // a gap thinner than Thin lies between two nearly coinciding edges (a collision triangle's band top and the part
            // above it, a float step apart), where the point test is ill-posed: it goes with a blocked gap beside it
            for (int j = 1; j < r.Count; j++)
                if (r[j].M - r[j - 1].M <= Thin && r[j].M - r[j - 1].M > Eps)
                {
                    int dn = j - 1; while (dn >= 1 && r[dn].M - r[dn - 1].M <= Thin) dn--;
                    int up = j + 1; while (up < r.Count && r[up].M - r[up - 1].M <= Thin) up++;
                    isBlocked[j] = (dn >= 1 && isBlocked[dn]) || (up < r.Count && isBlocked[up]);
                }
            var next = new List<(List<(double X, double Y)> Lo, List<(double X, double Y)> Up)>();
            for (int j = 1; j < r.Count; j++)
            {
                if (!isBlocked[j]) continue;
                // a run of blocked gaps one above another is one trapezoid: the segments between them are inside it
                int top = j;
                while (top + 1 < r.Count && (isBlocked[top + 1] || r[top + 1].M - r[top].M <= Eps && top + 2 < r.Count && isBlocked[top + 2])) top++;   // a zero gap between blocked ones joins them
                var (bl, br) = (r[j - 1].L, r[j - 1].R); var (tl, tr) = (r[top].L, r[top].R);
                j = top;
                if ((tl - bl) + (tr - br) <= Eps) continue;
                (double, double) BL = (xl, bl), BR = (xr, br), TL = (xl, tl), TR = (xr, tr);
                int at = tl - bl > Eps ? open.FindIndex(o => Math.Abs(o.Lo[^1].Y - bl) <= Eps && Math.Abs(o.Up[^1].Y - tl) <= Eps) : -1;
                if (at >= 0 && Turn(open[at].Lo, BR, 1) && Turn(open[at].Up, TR, -1))
                {
                    var o = open[at]; open.RemoveAt(at);
                    Push(o.Lo, BR); Push(o.Up, TR); next.Add(o);
                }
                else next.Add((new() { BL, BR }, new() { TL, TR }));
            }
            foreach (var o in open) done.Add(Ring(o.Lo, o.Up));
            open = next;
        }
        foreach (var o in open) done.Add(Ring(o.Lo, o.Up));
        return done;

        static double Cr((double X, double Y) o, (double X, double Y) a, (double X, double Y) b) => (a.X - o.X) * (b.Y - o.Y) - (a.Y - o.Y) * (b.X - o.X);
        static double Tol((double X, double Y) o, (double X, double Y) a, (double X, double Y) b) =>
            1e-9 * Math.Sqrt(((a.X - o.X) * (a.X - o.X) + (a.Y - o.Y) * (a.Y - o.Y)) * ((b.X - a.X) * (b.X - a.X) + (b.Y - a.Y) * (b.Y - a.Y)));
        // a chain may go on to n while it keeps turning one way: left (sense 1) along the bottom, right (-1) along the top
        static bool Turn(List<(double X, double Y)> c, (double X, double Y) n, int sense) => sense * Cr(c[^2], c[^1], n) >= -Tol(c[^2], c[^1], n);
        static void Push(List<(double X, double Y)> c, (double X, double Y) n)
        {
            if (Math.Abs(Cr(c[^2], c[^1], n)) <= Tol(c[^2], c[^1], n)) c[^1] = n; else c.Add(n);   // straight on: the middle point goes
        }
        static List<(double X, double Y)> Ring(List<(double X, double Y)> lo, List<(double X, double Y)> up)
        {
            var ring = lo.Concat(Enumerable.Reverse(up)).ToList();
            return ring.Where((p, i) => Math.Abs(p.X - ring[(i + ring.Count - 1) % ring.Count].X) + Math.Abs(p.Y - ring[(i + ring.Count - 1) % ring.Count].Y) > 1e-9).ToList();
        }
    }

    /// <summary>Where two plan segments (left end first) meet: the x of their crossing or touching point, or null.</summary>
    static double? CrossX((double X1, double Y1, double X2, double Y2) a, (double X1, double Y1, double X2, double Y2) b)
    {
        double rx = a.X2 - a.X1, ry = a.Y2 - a.Y1, sx = b.X2 - b.X1, sy = b.Y2 - b.Y1;
        double den = rx * sy - ry * sx;
        if (Math.Abs(den) < 1e-12) return null;                                // parallel: their ends are breakpoints already
        double qx = b.X1 - a.X1, qy = b.Y1 - a.Y1;
        double t = (qx * sy - qy * sx) / den, u = (qx * ry - qy * rx) / den;
        if (t < -1e-9 || t > 1 + 1e-9 || u < -1e-9 || u > 1 + 1e-9) return null;
        return a.X1 + t * rx;
    }

    /// <summary>A plan segment clipped to a box (Liang-Barsky), or null when none of it is inside.</summary>
    static (double X1, double Y1, double X2, double Y2)? ClipToBox(double x1, double y1, double x2, double y2, double bx0, double by0, double bx1, double by1)
    {
        double t0 = 0, t1 = 1, dx = x2 - x1, dy = y2 - y1;
        foreach (var (p, q) in new[] { (-dx, x1 - bx0), (dx, bx1 - x1), (-dy, y1 - by0), (dy, by1 - y1) })
        {
            if (p == 0) { if (q < 0) return null; continue; }
            double r = q / p;
            if (p < 0) { if (r > t1) return null; if (r > t0) t0 = r; }
            else { if (r < t0) return null; if (r < t1) t1 = r; }
        }
        return (x1 + t0 * dx, y1 + t0 * dy, x1 + t1 * dx, y1 + t1 * dy);
    }

    static double Area2(IReadOnlyList<(double X, double Y)> p)
    {
        double a = 0;
        for (int i = 0; i < p.Count; i++) { var (u, v) = (p[i], p[(i + 1) % p.Count]); a += u.X * v.Y - v.X * u.Y; }
        return a;
    }

    /// <summary>How deep two convex plan polygons overlap (separating axes; zero or less when they only touch or are apart).</summary>
    static double Overlap(IReadOnlyList<(double X, double Y)> P, IReadOnlyList<(double X, double Y)> Q)
    {
        double depth = double.MaxValue;
        foreach (var poly in new[] { P, Q })
            for (int i = 0; i < poly.Count; i++)
            {
                var (a, b) = (poly[i], poly[(i + 1) % poly.Count]);
                double nx = b.Y - a.Y, ny = a.X - b.X, len = Math.Sqrt(nx * nx + ny * ny);
                if (len == 0) continue;
                nx /= len; ny /= len;
                double pmin = P.Min(x => x.X * nx + x.Y * ny), pmax = P.Max(x => x.X * nx + x.Y * ny);
                double qmin = Q.Min(x => x.X * nx + x.Y * ny), qmax = Q.Max(x => x.X * nx + x.Y * ny);
                depth = Math.Min(depth, Math.Min(pmax, qmax) - Math.Max(pmin, qmin));
            }
        return depth;
    }

    /// <summary>A point set grown by a margin: a square of half-side margin at each point, then the hull.</summary>
    static List<(double X, double Y)> Grow(IEnumerable<(double X, double Y)> pts, float margin) =>
        Hull(pts.SelectMany(p => new[] { (p.X - margin, p.Y - margin), (p.X + margin, p.Y - margin), (p.X + margin, p.Y + margin), (p.X - margin, p.Y + margin) }).ToList());

    /// <summary>A convex part as NavEdit.Cut will read it: its corners rounded to float, and any corner that rounding left
    /// straight or turned slightly inward dropped (that widens the part by under a float step), so it stays convex.</summary>
    static List<(double X, double Y)> Tidy(List<(double X, double Y)> p)
    {
        var q = p.Select(v => (X: (double)(float)v.X, Y: (double)(float)v.Y)).ToList();
        if (Area2(q) < 0) q.Reverse();
        bool changed = true;
        while (changed && q.Count >= 3)
        {
            changed = false;
            for (int i = 0; i < q.Count && q.Count >= 3; i++)
            {
                var (o, a, b) = (q[(i + q.Count - 1) % q.Count], q[i], q[(i + 1) % q.Count]);
                double cr = (a.X - o.X) * (b.Y - a.Y) - (a.Y - o.Y) * (b.X - a.X);
                double ob = Math.Sqrt((b.X - o.X) * (b.X - o.X) + (b.Y - o.Y) * (b.Y - o.Y));
                // a corner within StraightTol of the line past it, either side, is straight on; one further in stays (the
                // cut then refuses the part as not convex, which is a fault here, not something to hide)
                if (ob == 0 || (a.X == o.X && a.Y == o.Y) || Math.Abs(cr) / ob <= StraightTol) { q.RemoveAt(i); changed = true; i--; }
            }
        }
        return q;
    }

    /// <summary>A convex polygon clipped to where <paramref name="side"/> is zero or more (Sutherland-Hodgman, one plane).</summary>
    static List<Vector3> Clip(List<Vector3> poly, Func<Vector3, float> side)
    {
        var o = new List<Vector3>();
        for (int i = 0; i < poly.Count; i++)
        {
            var (p, q) = (poly[i], poly[(i + 1) % poly.Count]);
            float sp = side(p), sq = side(q);
            if (sp >= 0) o.Add(p);
            if ((sp >= 0) != (sq >= 0)) o.Add(p + (q - p) * (sp / (sp - sq)));
        }
        return o;
    }

    /// <summary>The convex hull, counter-clockwise, without collinear points (Andrew's monotone chain).</summary>
    public static List<(double X, double Y)> Hull(List<(double X, double Y)> pts)
    {
        var p = pts.Distinct().OrderBy(a => a.X).ThenBy(a => a.Y).ToList();
        if (p.Count < 3) return p;
        double Cr((double X, double Y) o, (double X, double Y) a, (double X, double Y) b) => (a.X - o.X) * (b.Y - o.Y) - (a.Y - o.Y) * (b.X - o.X);
        var h = new List<(double X, double Y)>();
        foreach (var pass in new[] { p, Enumerable.Reverse(p).ToList() })
        {
            int start = h.Count;
            foreach (var q in pass)
            {
                while (h.Count >= start + 2 && Cr(h[^2], h[^1], q) <= 1e-9) h.RemoveAt(h.Count - 1);
                h.Add(q);
            }
            h.RemoveAt(h.Count - 1);
        }
        return h;
    }
}
