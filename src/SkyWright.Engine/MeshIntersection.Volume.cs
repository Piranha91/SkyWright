using System.Numerics;

using HousecarlCore;

namespace SkyWright.Engine;

// The volume half of MeshIntersection (patch-miss packet, 2026-09-28): the validated closed solid and the certified octree
// interval behind MeshVolume. It lives here, beside the validator and the ray-parity test it reuses, so the solid rule has
// one implementation. MeshVolume.cs is the public surface.
public static partial class MeshIntersection
{
    /// <summary>A validated solid: the prepared mesh (opaque outside this class), its exact volume and its world bounds.</summary>
    internal sealed class VolumeSolid
    {
        internal VolumeSolid(object mesh, double volume, int components) { Handle = mesh; Volume = volume; Components = components; }
        internal object Handle { get; }
        internal double Volume { get; }
        internal int Components { get; }
    }

    internal readonly record struct VolumePrep(VolumeSolid? Solid, string? Reason, bool Unknown);

    /// <summary>Validate a soup as a solid (the Query validator: closed, consistently oriented 2-manifold, one fan per
    /// vertex, no self-intersection, no nested shells) and require every component to be positively oriented — an
    /// inward (cavity) component is refused. The components are then pairwise disjoint, so the volume is the exact
    /// signed-tetrahedron sum.</summary>
    internal static VolumePrep PrepareVolumeSolid(TriangleSoup soup, long maxTrianglePairs, CancellationToken cancellationToken)
    {
        var budget = new WorkBudget(maxTrianglePairs, cancellationToken);
        try
        {
            if (!TryPrepare(soup, "mesh", budget, out var mesh, out var error))
                return new(null, error!.StartsWith("mesh_", StringComparison.Ordinal) ? error[5..] : error, false);
            var v = ValidateSolid(mesh!, 0, budget);
            if (v.Kind != SolidKind.Valid) return new(null, v.Reason ?? "solid_validation_failed", false);
            double total = 0;
            foreach (int[] component in v.Components!)
            {
                DVec3 origin = mesh!.Triangles[component[0]].A;
                double six = 0;
                foreach (int ti in component)
                {
                    Triangle t = mesh.Triangles[ti];
                    six += DVec3.Dot(t.A - origin, DVec3.Cross(t.B - origin, t.C - origin));
                }
                if (!(six > 0)) return new(null, "negatively_oriented_component", false);
                total += six / 6;
            }
            return new(new VolumeSolid(mesh!, total, v.Components!.Count), null, false);
        }
        catch (BudgetExceededException) { return new(null, "triangle_pair_budget_exhausted", true); }
        catch (OperationCanceledException) { return new(null, "cancelled", true); }
    }

    internal readonly record struct OctreeInterval(double Lower, double Upper, long Cells, int Depth, bool Converged, bool BudgetHit);

    // Per-cell state of one solid: wholly inside, wholly outside, or boundary with the triangles that may meet the cell.
    const sbyte SOut = 0, SIn = 1, SBoundary = 2;

    sealed class OctCell
    {
        public DVec3 Lo, Hi;
        public int Depth;
        public sbyte SA, SB;
        public int[]? TA, TB;
        public double Volume => (Hi.X - Lo.X) * (Hi.Y - Lo.Y) * (Hi.Z - Lo.Z);
    }

    /// <summary>Certified interval for vol(A ∩ B). Root = the intersection of the two world boxes. A cell no triangle of a
    /// solid can meet is wholly inside or outside it, decided by ray parity at its centre (ambiguous → boundary). A boundary
    /// cell whose candidate triangles of each solid are all exactly coplanar (exact orientation predicate) is measured by
    /// clipping: each side of the plane is constant for that solid, its status read by ray parity at an interior point,
    /// and the clipped convex pieces are summed with a stated rounding margin. Any other boundary cell subdivides until
    /// the target width, depth or cell budget; what is left adds only to the upper bound. Lower ≤ true ≤ upper.</summary>
    internal static OctreeInterval IntersectionInterval(VolumeSolid a, VolumeSolid b, double target, int maxDepth, long maxCells,
                                                        double inflate, CancellationToken cancellationToken)
    {
        Mesh ma = (Mesh)a.Handle, mb = (Mesh)b.Handle;
        var lo = DVec3.Max(ma.Root.Bounds.Min, mb.Root.Bounds.Min);
        var hi = DVec3.Min(ma.Root.Bounds.Max, mb.Root.Bounds.Max);
        if (!(hi.X > lo.X && hi.Y > lo.Y && hi.Z > lo.Z)) return new(0, 0, 0, 0, true, false);

        var budget = new WorkBudget(long.MaxValue, cancellationToken);
        double lower = 0, resolvedUpper = 0, pending = 0;
        long cells = 0; int depthReached = 0;
        var queue = new Queue<OctCell>();

        void Place(OctCell c)
        {
            cells++;
            depthReached = Math.Max(depthReached, c.Depth);
            if (c.SA == SOut || c.SB == SOut) return;
            double vol = c.Volume;
            if (c.SA == SIn && c.SB == SIn) { lower += vol; resolvedUpper += vol; return; }
            if (TryPlanar(c, ma, mb, budget, out double lo2, out double hi2)) { lower += lo2; resolvedUpper += hi2; return; }
            pending += vol;
            queue.Enqueue(c);
        }

        var root = new OctCell { Lo = lo, Hi = hi, Depth = 0 };
        Classify(root, ma, null, inflate, budget, isA: true);
        Classify(root, mb, null, inflate, budget, isA: false);
        Place(root);

        while (queue.Count > 0 && (resolvedUpper + pending) - lower > target)
        {
            var c = queue.Dequeue();
            pending -= c.Volume;
            if (c.Depth >= maxDepth || cells + 8 > maxCells) { resolvedUpper += c.Volume; continue; }
            var mid = (c.Lo + c.Hi) * 0.5;
            for (int k = 0; k < 8; k++)
            {
                var child = new OctCell
                {
                    Lo = new DVec3((k & 1) == 0 ? c.Lo.X : mid.X, (k & 2) == 0 ? c.Lo.Y : mid.Y, (k & 4) == 0 ? c.Lo.Z : mid.Z),
                    Hi = new DVec3((k & 1) == 0 ? mid.X : c.Hi.X, (k & 2) == 0 ? mid.Y : c.Hi.Y, (k & 4) == 0 ? mid.Z : c.Hi.Z),
                    Depth = c.Depth + 1,
                };
                if (c.SA == SBoundary) Classify(child, ma, c.TA, inflate, budget, isA: true); else child.SA = c.SA;
                if (c.SB == SBoundary) Classify(child, mb, c.TB, inflate, budget, isA: false); else child.SB = c.SB;
                Place(child);
            }
        }
        bool budgetHit = queue.Count > 0 && (resolvedUpper + pending) - lower > target;
        double upper = resolvedUpper + pending;
        if (upper < lower) upper = lower;
        return new(lower, upper, cells, depthReached, upper - lower <= target, budgetHit);
    }

    static void Classify(OctCell c, Mesh mesh, int[]? candidates, double inflate, WorkBudget budget, bool isA)
    {
        var tris = new List<int>();
        if (candidates is null) { for (int i = 0; i < mesh.Triangles.Length; i++) if (MayMeetOpenCell(mesh.Triangles[i], c.Lo, c.Hi, inflate)) tris.Add(i); }
        else foreach (int i in candidates) if (MayMeetOpenCell(mesh.Triangles[i], c.Lo, c.Hi, inflate)) tris.Add(i);
        sbyte state;
        int[]? list = null;
        if (tris.Count > 0) { state = SBoundary; list = tris.ToArray(); }
        else
        {
            var r = PointInside((c.Lo + c.Hi) * 0.5, mesh, 0, budget);
            if (r == InsideResult.Inside) state = SIn;
            else if (r == InsideResult.Outside) state = SOut;
            else { state = SBoundary; list = Array.Empty<int>(); }          // ambiguous centre: a boundary cell
        }
        if (isA) { c.SA = state; c.TA = list; } else { c.SB = state; c.TB = list; }
    }

    /// <summary>Conservative "may this triangle meet the OPEN cell". The three box axes are compared exactly (a triangle
    /// lying in a cell face does not meet the open cell, and coordinate comparisons of doubles are exact); the triangle
    /// normal and the nine edge axes use the cell inflated by <paramref name="inflate"/>, so rounding can only keep a
    /// triangle, never drop one. A degenerate axis is skipped (fewer axes, fewer separations — conservative).</summary>
    static bool MayMeetOpenCell(in Triangle t, DVec3 lo, DVec3 hi, double inflate) => MayMeetOpenCell(t.A, t.B, t.C, t.Normal, lo, hi, inflate);

    // the same test on a triangle's corners and its normal (Triangle's: Cross(B - A, C - A)), for callers that hold no Triangle
    static bool MayMeetOpenCell(DVec3 A, DVec3 B, DVec3 C, DVec3 normal, DVec3 lo, DVec3 hi, double inflate)
    {
        if (Math.Max(A.X, Math.Max(B.X, C.X)) <= lo.X || Math.Min(A.X, Math.Min(B.X, C.X)) >= hi.X) return false;
        if (Math.Max(A.Y, Math.Max(B.Y, C.Y)) <= lo.Y || Math.Min(A.Y, Math.Min(B.Y, C.Y)) >= hi.Y) return false;
        if (Math.Max(A.Z, Math.Max(B.Z, C.Z)) <= lo.Z || Math.Min(A.Z, Math.Min(B.Z, C.Z)) >= hi.Z) return false;
        var c = (lo + hi) * 0.5;
        var e = (hi - lo) * 0.5 + new DVec3(inflate, inflate, inflate);
        DVec3 v0 = A - c, v1 = B - c, v2 = C - c;
        if (Separated(normal, v0, v1, v2, e)) return false;
        DVec3 f0 = v1 - v0, f1 = v2 - v1, f2 = v0 - v2;
        foreach (var f in new[] { f0, f1, f2 })
        {
            if (Separated(new DVec3(0, -f.Z, f.Y), v0, v1, v2, e)) return false;
            if (Separated(new DVec3(f.Z, 0, -f.X), v0, v1, v2, e)) return false;
            if (Separated(new DVec3(-f.Y, f.X, 0), v0, v1, v2, e)) return false;
        }
        return true;
    }

    static bool Separated(DVec3 axis, DVec3 v0, DVec3 v1, DVec3 v2, DVec3 e)
    {
        if (axis.LengthSquared < 1e-30) return false;
        double p0 = DVec3.Dot(axis, v0), p1 = DVec3.Dot(axis, v1), p2 = DVec3.Dot(axis, v2);
        double r = e.X * Math.Abs(axis.X) + e.Y * Math.Abs(axis.Y) + e.Z * Math.Abs(axis.Z);
        return Math.Min(p0, Math.Min(p1, p2)) > r || Math.Max(p0, Math.Max(p1, p2)) < -r;
    }

    /// <summary>A boundary cell whose candidate triangles, per boundary solid, all lie exactly on one plane: the open cell
    /// minus that plane has two convex parts, each wholly inside or outside the solid (no surface of it meets them). Each
    /// part's status is read by ray parity at its vertex average; the pieces inside both are clipped and summed. A piece
    /// whose status is ambiguous adds only to the upper bound. Rounding margin: 1e-9 of the cell volume per piece.</summary>
    static bool TryPlanar(OctCell c, Mesh a, Mesh b, WorkBudget budget, out double lower, out double upper)
    {
        lower = upper = 0;
        var planes = new List<(DVec3 N, double D, Mesh Mesh)>();
        if (c.SA == SBoundary) { if (!PlaneOf(a, c.TA!, out var n, out var d)) return false; planes.Add((n, d, a)); }
        if (c.SB == SBoundary) { if (!PlaneOf(b, c.TB!, out var n, out var d)) return false; planes.Add((n, d, b)); }
        if (planes.Count == 0) return false;
        double cellVol = c.Volume;
        double margin = 1e-9 * cellVol;
        // Every sign combination of the (one or two) planes is a convex piece of the cell.
        int combos = 1 << planes.Count;
        for (int m = 0; m < combos; m++)
        {
            var clip = new List<(DVec3 N, double D)>();
            for (int p = 0; p < planes.Count; p++)
                clip.Add((m & (1 << p)) == 0 ? (planes[p].N, planes[p].D) : (planes[p].N * -1, -planes[p].D));
            var poly = ClipBox(c.Lo, c.Hi, clip);
            if (poly is null) continue;
            double vol = Math.Clamp(PolyVolume(poly, out var inner), 0, cellVol);
            if (vol <= margin) { upper += vol + margin; continue; }                      // a sliver: its status is not read
            bool inside = true, known = true;
            for (int p = 0; p < planes.Count && inside; p++)
            {
                var r = PointInside(inner, planes[p].Mesh, 0, budget);
                if (r == InsideResult.Outside) inside = false;
                else if (r != InsideResult.Inside) known = false;
            }
            if (!inside) continue;
            if (!known) { upper += Math.Min(cellVol, vol + margin); continue; }
            lower += Math.Max(0, vol - margin);
            upper += Math.Min(cellVol, vol + margin);
        }
        return true;
    }

    static bool PlaneOf(Mesh mesh, int[] tris, out DVec3 normal, out double offset)
    {
        normal = default; offset = 0;
        if (tris.Length == 0) return false;
        Triangle t0 = mesh.Triangles[tris[0]];
        foreach (int i in tris)
        {
            Triangle t = mesh.Triangles[i];
            if (Robust.Orient3Sign(t0.A, t0.B, t0.C, t.A) != 0 || Robust.Orient3Sign(t0.A, t0.B, t0.C, t.B) != 0 ||
                Robust.Orient3Sign(t0.A, t0.B, t0.C, t.C) != 0) return false;
        }
        normal = t0.Normal;
        offset = DVec3.Dot(normal, t0.A);
        return normal.LengthSquared > 0;
    }

    /// <summary>The axis box clipped by half-spaces n·x ≤ d, as outward-wound faces; null when nothing is left.</summary>
    static List<List<DVec3>>? ClipBox(DVec3 lo, DVec3 hi, List<(DVec3 N, double D)> halfSpaces)
    {
        DVec3 P(int k) => new((k & 1) == 0 ? lo.X : hi.X, (k & 2) == 0 ? lo.Y : hi.Y, (k & 4) == 0 ? lo.Z : hi.Z);
        // Outward counter-clockwise faces of the box (corner k: bit0 x, bit1 y, bit2 z).
        int[][] faceIdx = [[0, 2, 3, 1], [4, 5, 7, 6], [0, 1, 5, 4], [2, 6, 7, 3], [0, 4, 6, 2], [1, 3, 7, 5]];
        var faces = faceIdx.Select(f => f.Select(P).ToList()).ToList();
        foreach (var (n, d) in halfSpaces)
        {
            var next = new List<List<DVec3>>();
            var cap = new List<DVec3>();
            // A plane that cuts nothing off (no vertex strictly outside) leaves the polytope as it is. Adding a cap then
            // would duplicate an existing face when the plane coincides with one - the second of two identical planes
            // (a solid measured against itself) - and count that face's cone twice.
            bool cuts = faces.Any(f => f.Any(p => DVec3.Dot(n, p) - d > 0));
            if (!cuts) continue;
            foreach (var face in faces)
            {
                var outp = new List<DVec3>();
                for (int i = 0; i < face.Count; i++)
                {
                    DVec3 p = face[i], q = face[(i + 1) % face.Count];
                    double dp = DVec3.Dot(n, p) - d, dq = DVec3.Dot(n, q) - d;
                    if (dp <= 0) { outp.Add(p); if (dp == 0) cap.Add(p); }
                    if ((dp < 0 && dq > 0) || (dp > 0 && dq < 0))
                    {
                        var x = p + (q - p) * (dp / (dp - dq));
                        outp.Add(x); cap.Add(x);
                    }
                }
                if (outp.Count >= 3) next.Add(outp);
            }
            if (next.Count == 0) return null;
            if (cap.Count >= 3)
            {
                var centre = cap.Aggregate(new DVec3(0, 0, 0), (s, p) => s + p) / cap.Count;
                var nn = n / Math.Sqrt(n.LengthSquared);
                var u = Math.Abs(nn.X) < 0.9 ? DVec3.Cross(nn, new DVec3(1, 0, 0)) : DVec3.Cross(nn, new DVec3(0, 1, 0));
                u = u / Math.Sqrt(u.LengthSquared);
                var w = DVec3.Cross(nn, u);
                next.Add(cap.OrderBy(p => Math.Atan2(DVec3.Dot(p - centre, w), DVec3.Dot(p - centre, u))).ToList());
            }
            faces = next;
        }
        return faces;
    }

    static double PolyVolume(List<List<DVec3>> faces, out DVec3 inner)
    {
        int count = 0;
        var sum = new DVec3(0, 0, 0);
        foreach (var f in faces) foreach (var p in f) { sum += p; count++; }
        inner = sum / Math.Max(1, count);
        double six = 0;
        foreach (var f in faces)
            for (int i = 1; i + 1 < f.Count; i++)
                six += DVec3.Dot(f[0] - inner, DVec3.Cross(f[i] - inner, f[i + 1] - inner));
        return six / 6;
    }

    internal readonly record struct InsideSurface(string? Error, Vector3[] Pieces, int Whole, int Split, int Undecided, bool BudgetHit);

    const int MaxInsideSplitDepth = 12;

    /// <summary>The part of a surface inside a validated solid, as world-space triangles (three vertices each). A surface
    /// triangle that meets no triangle of the solid lies wholly inside or outside it, and such triangles joined by a shared
    /// point lie on one side: each such group is decided once, by ray parity at one triangle's centre. A triangle that
    /// meets the solid's surface is split in four, again and again, down to about <paramref name="resolution"/>; a piece
    /// that meets nothing, or a smallest piece, is decided by ray parity at its own centre (or, when the centre is on the
    /// solid's surface, at a point toward one of its corners). A piece the rays cannot decide is left out and counted. Degenerate surface triangles are skipped: they have no area to show.</summary>
    internal static InsideSurface SurfaceInside(TriangleSoup surface, VolumeSolid solid, double resolution, long maxWork, CancellationToken cancellationToken)
    {
        var mesh = (Mesh)solid.Handle;
        var budget = new WorkBudget(maxWork, cancellationToken);
        Vector3[] v = surface.Vertices; int[] ix = surface.Indices;
        if (v is null || ix is null || ix.Length % 3 != 0) return new("surface_invalid_index_count", [], 0, 0, 0, false);
        foreach (int i in ix) if ((uint)i >= (uint)v.Length) return new("surface_index_out_of_range", [], 0, 0, 0, false);
        foreach (var p in v) if (!float.IsFinite(p.X) || !float.IsFinite(p.Y) || !float.IsFinite(p.Z)) return new("surface_nonfinite_vertex", [], 0, 0, 0, false);

        var output = new List<Vector3>();
        int whole = 0, split = 0, undecided = 0;
        var point = new Dictionary<Vector3, int>();
        int Point(Vector3 p) { if (!point.TryGetValue(p, out int id)) point.Add(p, id = point.Count); return id; }
        var parent = new List<int>();
        int Root(int x) { while (parent[x] != x) x = parent[x] = parent[parent[x]]; return x; }
        var free = new List<(int Tri, int Point)>();
        var met = new List<(Triangle Tri, List<int> Meets)>();
        try
        {
            for (int t = 0; t < ix.Length / 3; t++)
            {
                budget.Checkpoint(t);
                Vector3 fa = v[ix[3 * t]], fb = v[ix[3 * t + 1]], fc = v[ix[3 * t + 2]];
                var tri = new Triangle(-1, -1, -1, new(fa.X, fa.Y, fa.Z), new(fb.X, fb.Y, fb.Z), new(fc.X, fc.Y, fc.Z));
                if (!(tri.Normal.LengthSquared > 0)) continue;
                var meets = Meets(tri, mesh, null, budget);
                if (meets.Count > 0) { met.Add((tri, meets)); continue; }
                int pa = Point(fa), pb = Point(fb), pc = Point(fc);
                while (parent.Count < point.Count) parent.Add(parent.Count);
                int r = Root(pa);
                parent[Root(pb)] = r; parent[Root(pc)] = r;
                free.Add((t, pa));
            }
            var side = new Dictionary<int, InsideResult>();
            foreach (var (t, p) in free)
            {
                int r = Root(p);
                if (!side.TryGetValue(r, out var s))
                {
                    Vector3 c = (v[ix[3 * t]] + v[ix[3 * t + 1]] + v[ix[3 * t + 2]]) / 3f;
                    side[r] = s = PointInside(new(c.X, c.Y, c.Z), mesh, 0, budget);
                }
                if (s == InsideResult.Inside) { output.Add(v[ix[3 * t]]); output.Add(v[ix[3 * t + 1]]); output.Add(v[ix[3 * t + 2]]); whole++; }
                else if (s == InsideResult.Unknown) undecided++;
            }
            foreach (var (tri, meets) in met)
            {
                split++;
                Piece(tri.A, tri.B, tri.C, meets, 0);
            }
        }
        catch (BudgetExceededException) { return new(null, output.ToArray(), whole, split, undecided, true); }
        return new(null, output.ToArray(), whole, split, undecided, false);

        void Piece(DVec3 a, DVec3 b, DVec3 c, List<int> candidates, int depth)
        {
            var tri = new Triangle(-1, -1, -1, a, b, c);
            if (!(tri.Normal.LengthSquared > 0)) return;
            var meets = depth == 0 ? candidates : Meets(tri, mesh, candidates, budget);
            double longest = Math.Sqrt(Math.Max((b - a).LengthSquared, Math.Max((c - b).LengthSquared, (a - c).LengthSquared)));
            if (meets.Count == 0 || longest <= resolution || depth >= MaxInsideSplitDepth)
            {
                // a centre on the solid's surface has no side: then three points toward the corners, each inside the piece
                var s = PointInside(tri.Centroid, mesh, 0, budget);
                for (int k = 0; k < 3 && s == InsideResult.Unknown; k++)
                    s = PointInside(k == 0 ? (a * 4 + b + c) / 6 : k == 1 ? (a + b * 4 + c) / 6 : (a + b + c * 4) / 6, mesh, 0, budget);
                if (s == InsideResult.Inside)
                {
                    output.Add(new((float)a.X, (float)a.Y, (float)a.Z)); output.Add(new((float)b.X, (float)b.Y, (float)b.Z)); output.Add(new((float)c.X, (float)c.Y, (float)c.Z));
                }
                else if (s == InsideResult.Unknown) undecided++;
                return;
            }
            DVec3 ab = (a + b) * 0.5, bc = (b + c) * 0.5, ca = (c + a) * 0.5;
            Piece(a, ab, ca, meets, depth + 1);
            Piece(ab, b, bc, meets, depth + 1);
            Piece(ca, bc, c, meets, depth + 1);
            Piece(ab, bc, ca, meets, depth + 1);
        }
    }

    /// <summary>The solid's triangles a surface triangle touches or crosses: from <paramref name="among"/> when given (a
    /// piece's parent's), otherwise found through the solid's tree.</summary>
    static List<int> Meets(in Triangle tri, Mesh mesh, List<int>? among, WorkBudget budget)
    {
        var found = new List<int>();
        if (among is not null)
        {
            foreach (int k in among)
                if (tri.Bounds.Overlaps(mesh.Triangles[k].Bounds, 0)) { budget.Consume(); if (Classify(tri, mesh.Triangles[k], 0) != PairRelation.Separated) found.Add(k); }
            return found;
        }
        var stack = new Stack<BvhNode>(); stack.Push(mesh.Root);
        while (stack.Count > 0)
        {
            var node = stack.Pop();
            if (!node.Bounds.Overlaps(tri.Bounds, 0)) continue;
            if (!node.IsLeaf) { stack.Push(node.Left!); stack.Push(node.Right!); continue; }
            for (int i = node.Start; i < node.Start + node.Count; i++)
            {
                int k = mesh.Order[i];
                if (!tri.Bounds.Overlaps(mesh.Triangles[k].Bounds, 0)) continue;
                budget.Consume();
                if (Classify(tri, mesh.Triangles[k], 0) != PairRelation.Separated) found.Add(k);
            }
        }
        return found;
    }
}
