using System.Buffers;
using System.Numerics;

using HousecarlCore;

namespace SkyWright.Engine;

/// <summary>
/// The result of a symmetric triangle-surface query. String values are a deliberately small wire vocabulary:
/// <c>Status</c> is <c>ok</c> or <c>unknown</c>; <c>SurfaceRelation</c> is <c>crossing</c>,
/// <c>coplanar_overlap</c>, <c>touching</c>, <c>separated</c>, or <c>unknown</c>; and
/// <c>Containment</c> is <c>a_inside_b</c>, <c>b_inside_a</c>, <c>none</c>,
/// <c>component_containment</c>, <c>not_applicable_open_mesh</c>, <c>unassessed</c>, or <c>unknown</c>.
/// </summary>
public sealed record MeshIntersectionResult(
    string Status,
    string? Reason,
    string SurfaceRelation,
    string Containment,
    bool? Intersects,
    long TrianglePairsTested)
{
    public string PolicyVersion => MeshIntersection.PolicyVersion;
}

/// <summary>
/// Symmetric intersection of actual triangle surfaces. Bounds are used only as conservative, tolerance-expanded
/// acceleration. This predicate deliberately does not use <see cref="SpatialOverlap.Contact"/>: that routine answers
/// an asymmetric support-facing question and is not a general surface-intersection oracle.
/// </summary>
public static partial class MeshIntersection
{
    // v2 (2026-10-20): the self-intersection scan tests every triangle pair across BVH leaves; v1 skipped one orientation.
    public const string PolicyVersion = "actual_triangle_mesh_v2";

    const string Unknown = "unknown";
    const int LeafSize = 8;
    const double MachineEpsilon = 2.2204460492503131e-16;

    public static MeshIntersectionResult Query(
        TriangleSoup a,
        TriangleSoup b,
        double tolerance = 1e-5,
        long maxTrianglePairs = 2_000_000,
        CancellationToken cancellationToken = default,
        MeshQueryTimer? timer = null)
    {
        if (a is null || b is null)
            return UnknownResult("null_mesh", 0);
        if (!double.IsFinite(tolerance) || tolerance < 0)
            return UnknownResult("invalid_tolerance", 0);
        if (maxTrianglePairs <= 0)
            return UnknownResult("invalid_triangle_pair_budget", 0);

        var budget = new WorkBudget(maxTrianglePairs, cancellationToken);
        if (timer is not null) timer.Queries++;
        Mesh? ma, mb;
        try
        {
            if (!TryPrepare(a, "a", budget, out ma, out var error, timer)) { if (timer is not null) timer.PrepareRefused++; return UnknownResult(error!, budget.Used); }
            if (!TryPrepare(b, "b", budget, out mb, out error, timer)) { if (timer is not null) timer.PrepareRefused++; return UnknownResult(error!, budget.Used); }
        }
        catch (OperationCanceledException)
        {
            if (timer is not null) timer.Cancelled++;
            return new MeshIntersectionResult(Unknown, "cancelled", Unknown, Unknown, null, budget.Used);
        }
        return Evaluate(ma!, mb!, null, null, tolerance, budget, timer);
    }

    /// <summary>The query on two prepared meshes, from the surface traversal on. <paramref name="memoA"/> and
    /// <paramref name="memoB"/> (prepared meshes) memoise each side's solid validation; null validates afresh.</summary>
    static MeshIntersectionResult Evaluate(Mesh ma, Mesh mb, Core? memoA, Core? memoB, double tolerance,
                                           WorkBudget budget, MeshQueryTimer? timer)
    {
        string completedSurface = Unknown;
        PhaseClock? clock = null;
        try
        {
            if (timer is not null) (clock = new PhaseClock(timer, budget)).Enter(Phase.Surface);
            string surface = QuerySurfaces(ma!, mb!, tolerance, budget);
            completedSurface = surface;
            if (surface != "separated")
                return new MeshIntersectionResult("ok", null, surface, "unassessed", true, budget.Used);

            clock?.Enter(Phase.Validate);
            var va = ValidateMemo(ma, memoA, tolerance, budget);
            var vb = ValidateMemo(mb, memoB, tolerance, budget);
            if (va.Kind == SolidKind.Open || vb.Kind == SolidKind.Open)
                return new MeshIntersectionResult("ok", "containment_requires_closed_manifold_meshes",
                    "separated", "not_applicable_open_mesh", false, budget.Used);
            if (va.Kind != SolidKind.Valid || vb.Kind != SolidKind.Valid)
            {
                string reason = va.Reason ?? vb.Reason ?? "solid_validation_failed";
                return new MeshIntersectionResult(Unknown, reason, "separated", Unknown, null, budget.Used);
            }

            clock?.Enter(Phase.Containment);
            var aInB = ComponentsInside(ma, va.Components!, mb, tolerance, budget);
            var bInA = ComponentsInside(mb, vb.Components!, ma, tolerance, budget);
            if (aInB == InsideResult.Unknown || bInA == InsideResult.Unknown)
                return new MeshIntersectionResult(Unknown, "containment_ray_ambiguous", "separated", Unknown, null, budget.Used);
            if (aInB == InsideResult.Inside && bInA == InsideResult.Inside)
                return new MeshIntersectionResult(Unknown, "mutual_containment_is_ambiguous", "separated", Unknown, null, budget.Used);

            if (aInB == InsideResult.Mixed || bInA == InsideResult.Mixed)
                return new MeshIntersectionResult("ok", null, "separated", "component_containment", true, budget.Used);

            string containment = aInB == InsideResult.Inside ? "a_inside_b" :
                bInA == InsideResult.Inside ? "b_inside_a" : "none";
            return new MeshIntersectionResult("ok", null, "separated", containment,
                containment != "none", budget.Used);
        }
        catch (BudgetExceededException)
        {
            if (timer is not null) timer.BudgetExhausted++;
            return new MeshIntersectionResult(Unknown, "triangle_pair_budget_exhausted", completedSurface, Unknown, null, budget.Used);
        }
        catch (OperationCanceledException)
        {
            if (timer is not null) timer.Cancelled++;
            return new MeshIntersectionResult(Unknown, "cancelled", completedSurface, Unknown, null, budget.Used);
        }
        finally
        {
            clock?.Enter(Phase.None);
        }
    }

    static MeshIntersectionResult UnknownResult(string reason, long tested) =>
        new(Unknown, reason, Unknown, Unknown, null, tested);

    /// <summary>Test hook only (overlapprobe --self-test): called with every soup before it is prepared, so a test can make
    /// preparation throw. Null in every real run.</summary>
    public static Action<TriangleSoup>? TestPrepareHook;

    static bool TryPrepare(TriangleSoup soup, string label, WorkBudget budget, out Mesh? mesh, out string? error,
        MeshQueryTimer? timer = null)
    {
        TestPrepareHook?.Invoke(soup);
        long start = timer is null ? 0 : System.Diagnostics.Stopwatch.GetTimestamp(), bvhStart = 0;
        try { return Prepare(soup, label, budget, out mesh, out error, timer is null ? null : () => bvhStart = System.Diagnostics.Stopwatch.GetTimestamp()); }
        finally
        {
            if (timer is not null)
            {
                long end = System.Diagnostics.Stopwatch.GetTimestamp();
                if (bvhStart == 0) timer.PrepareTicks += end - start;
                else { timer.PrepareTicks += bvhStart - start; timer.BvhTicks += end - bvhStart; }
            }
        }
    }

    static bool Prepare(TriangleSoup soup, string label, WorkBudget budget, out Mesh? mesh, out string? error, Action? beforeBvh)
    {
        mesh = null;
        error = null;
        if (soup.Vertices is null || soup.Indices is null)
        {
            error = $"{label}_missing_geometry";
            return false;
        }
        if (soup.Indices.Length == 0 || soup.Vertices.Length == 0)
        {
            error = $"{label}_empty_mesh";
            return false;
        }
        if (soup.Indices.Length % 3 != 0)
        {
            error = $"{label}_invalid_index_count";
            return false;
        }

        var vertices = new DVec3[soup.Vertices.Length];
        for (int i = 0; i < vertices.Length; i++)
        {
            budget.Checkpoint(i);
            Vector3 v = soup.Vertices[i];
            if (!float.IsFinite(v.X) || !float.IsFinite(v.Y) || !float.IsFinite(v.Z))
            {
                error = $"{label}_nonfinite_vertex";
                return false;
            }
            vertices[i] = new(v.X, v.Y, v.Z);
        }

        var welded = new int[vertices.Length];
        var exactVertices = new Dictionary<DVec3, int>();
        for (int i = 0; i < vertices.Length; i++)
        {
            budget.Checkpoint(i);
            if (!exactVertices.TryGetValue(vertices[i], out int representative))
                exactVertices.Add(vertices[i], representative = i);
            welded[i] = representative;
        }

        int triangleCount = soup.Indices.Length / 3;
        var triangles = new Triangle[triangleCount];
        for (int t = 0; t < triangleCount; t++)
        {
            budget.Checkpoint(t);
            int i0 = soup.Indices[t * 3];
            int i1 = soup.Indices[t * 3 + 1];
            int i2 = soup.Indices[t * 3 + 2];
            if ((uint)i0 >= (uint)vertices.Length || (uint)i1 >= (uint)vertices.Length || (uint)i2 >= (uint)vertices.Length)
            {
                error = $"{label}_index_out_of_range";
                return false;
            }
            if (i0 == i1 || i1 == i2 || i2 == i0)
            {
                error = $"{label}_degenerate_triangle";
                return false;
            }
            var tri = new Triangle(i0, i1, i2, vertices[i0], vertices[i1], vertices[i2]);
            if (tri.Normal.LengthSquared == 0 || !double.IsFinite(tri.Normal.LengthSquared))
            {
                error = $"{label}_degenerate_triangle";
                return false;
            }
            triangles[t] = tri;
        }

        beforeBvh?.Invoke();
        var order = Enumerable.Range(0, triangleCount).ToArray();
        var root = BuildBvh(triangles, order, 0, order.Length, budget);
        mesh = new Mesh(vertices, welded, triangles, order, root);
        return true;
    }

    static BvhNode BuildBvh(Triangle[] triangles, int[] order, int start, int count, WorkBudget budget)
    {
        BvhKey[] keys = ArrayPool<BvhKey>.Shared.Rent(count);
        try { return BuildBvhNode(triangles, order, start, count, keys, budget); }
        finally { ArrayPool<BvhKey>.Shared.Return(keys); }
    }

    static BvhNode BuildBvhNode(Triangle[] triangles, int[] order, int start, int count, BvhKey[] scratch, WorkBudget budget)
    {
        budget.Checkpoint(start + count);
        var bounds = Bounds.Empty;
        var centroids = Bounds.Empty;
        for (int i = start; i < start + count; i++)
        {
            bounds = bounds.Union(triangles[order[i]].Bounds);
            centroids = centroids.Add(triangles[order[i]].Centroid);
        }
        if (count <= LeafSize) return new(bounds, start, count, null, null);

        DVec3 span = centroids.Max - centroids.Min;
        int axis = span.X >= span.Y && span.X >= span.Z ? 0 : span.Y >= span.Z ? 1 : 2;
        Span<BvhKey> keys = scratch.AsSpan(0, count);
        for (int i = 0; i < count; i++) keys[i] = new(triangles[order[start + i]].Centroid[axis], order[start + i]);
        int leftCount = count / 2;
        // The order is total, so the sets are as a full sort makes them; a leaf child keeps this node's order, so sort for it.
        if (leftCount <= LeafSize) keys.Sort();
        else SelectLowest(keys, leftCount, budget);
        for (int i = 0; i < count; i++) order[start + i] = keys[i].Triangle;
        var left = BuildBvhNode(triangles, order, start, leftCount, scratch, budget);
        var right = BuildBvhNode(triangles, order, start + leftCount, count - leftCount, scratch, budget);
        return new(bounds, start, count, left, right);
    }

    /// <summary>Moves the <paramref name="k"/> lowest keys to the front, in no set order (quickselect, sorting when it degrades).</summary>
    static void SelectLowest(Span<BvhKey> keys, int k, WorkBudget budget)
    {
        int lo = 0, hi = keys.Length - 1, depth = 2 * (BitOperations.Log2((uint)keys.Length) + 1);
        while (lo < hi)
        {
            budget.Cancellation.ThrowIfCancellationRequested();
            if (hi - lo < 16 || depth-- == 0) { keys[lo..(hi + 1)].Sort(); return; }
            int mid = lo + (hi - lo) / 2;
            if (keys[mid].CompareTo(keys[lo]) < 0) (keys[mid], keys[lo]) = (keys[lo], keys[mid]);
            if (keys[hi].CompareTo(keys[lo]) < 0) (keys[hi], keys[lo]) = (keys[lo], keys[hi]);
            if (keys[hi].CompareTo(keys[mid]) < 0) (keys[hi], keys[mid]) = (keys[mid], keys[hi]);
            BvhKey pivot = keys[mid];
            int i = lo, j = hi;
            while (i <= j)
            {
                while (keys[i].CompareTo(pivot) < 0) i++;
                while (pivot.CompareTo(keys[j]) < 0) j--;
                if (i <= j) { (keys[i], keys[j]) = (keys[j], keys[i]); i++; j--; }
            }
            if (k <= j) hi = j;
            else if (k >= i) lo = i;
            else return;
        }
    }

    // A centroid along the split axis, ties by triangle index: a strict total order, so its sorted result is unique.
    readonly record struct BvhKey(double Key, int Triangle) : IComparable<BvhKey>
    {
        public int CompareTo(BvhKey other)
        {
            int c = Key.CompareTo(other.Key);
            return c != 0 ? c : Triangle.CompareTo(other.Triangle);
        }
    }

    static string QuerySurfaces(Mesh a, Mesh b, double tolerance, WorkBudget budget)
    {
        PairRelation strongest = PairRelation.Separated;
        var stack = new Stack<(BvhNode A, BvhNode B)>();
        stack.Push((a.Root, b.Root));
        while (stack.Count > 0)
        {
            budget.Cancellation.ThrowIfCancellationRequested();
            var pair = stack.Pop();
            if (!pair.A.Bounds.Overlaps(pair.B.Bounds, tolerance)) continue;
            if (pair.A.IsLeaf && pair.B.IsLeaf)
            {
                for (int ia = pair.A.Start; ia < pair.A.Start + pair.A.Count; ia++)
                for (int ib = pair.B.Start; ib < pair.B.Start + pair.B.Count; ib++)
                {
                    int ta = a.Order[ia], tb = b.Order[ib];
                    if (!a.Triangles[ta].Bounds.Overlaps(b.Triangles[tb].Bounds, tolerance)) continue;
                    budget.Consume();
                    strongest = Max(strongest, Classify(a.Triangles[ta], b.Triangles[tb], tolerance));
                    if (strongest == PairRelation.Crossing) return "crossing";
                }
                continue;
            }

            // Split the larger node. The tie-break is symmetric in geometry and does not affect the complete result.
            if (pair.B.IsLeaf || (!pair.A.IsLeaf && pair.A.Bounds.SurfaceArea >= pair.B.Bounds.SurfaceArea))
            {
                stack.Push((pair.A.Left!, pair.B));
                stack.Push((pair.A.Right!, pair.B));
            }
            else
            {
                stack.Push((pair.A, pair.B.Left!));
                stack.Push((pair.A, pair.B.Right!));
            }
        }

        return strongest switch
        {
            PairRelation.Crossing => "crossing",
            PairRelation.CoplanarOverlap => "coplanar_overlap",
            PairRelation.Touching => "touching",
            _ => "separated"
        };
    }

    static PairRelation Classify(in Triangle a, in Triangle b, double tolerance)
    {
        Span<int> bToA = stackalloc int[3];
        bToA[0] = Robust.Orient3Sign(a.A, a.B, a.C, b.A);
        bToA[1] = Robust.Orient3Sign(a.A, a.B, a.C, b.B);
        bToA[2] = Robust.Orient3Sign(a.A, a.B, a.C, b.C);
        Span<int> aToB = stackalloc int[3];
        aToB[0] = Robust.Orient3Sign(b.A, b.B, b.C, a.A);
        aToB[1] = Robust.Orient3Sign(b.A, b.B, b.C, a.B);
        aToB[2] = Robust.Orient3Sign(b.A, b.B, b.C, a.C);

        bool coplanar = bToA[0] == 0 && bToA[1] == 0 && bToA[2] == 0 && aToB[0] == 0 && aToB[1] == 0 && aToB[2] == 0;
        if (coplanar)
        {
            PairRelation projected = CoplanarProjectedRelation(a, b);
            if (projected != PairRelation.Separated) return projected;
            return TriangleDistanceSquared(a, b) <= tolerance * tolerance ? PairRelation.Touching : PairRelation.Separated;
        }

        bool straddleA = HasBothSigns(bToA);
        bool straddleB = HasBothSigns(aToB);
        if (straddleA && straddleB)
        {
            DVec3 line = DVec3.Cross(a.Normal, b.Normal);
            if (line.LengthSquared > 0)
            {
                line /= Math.Sqrt(line.LengthSquared);
                DVec3 projectionOrigin = a.A;
                var ai = PlaneCutInterval(a, aToB, b, line, projectionOrigin);
                var bi = PlaneCutInterval(b, bToA, a, line, projectionOrigin);
                if (ai.HasValue && bi.HasValue)
                {
                    double lo = Math.Max(ai.Value.Min, bi.Value.Min);
                    double hi = Math.Min(ai.Value.Max, bi.Value.Max);
                    double roundoff = 64 * MachineEpsilon * Math.Max(Math.Abs(lo), Math.Abs(hi));
                    if (hi > lo + roundoff) return PairRelation.Crossing;
                    if (hi >= lo - roundoff) return PairRelation.Touching;
                }
            }
        }

        return TriangleDistanceSquared(a, b) <= tolerance * tolerance ? PairRelation.Touching : PairRelation.Separated;
    }

    static bool HasBothSigns(ReadOnlySpan<int> signs)
    {
        bool negative = false, positive = false;
        foreach (int s in signs) { if (s < 0) negative = true; else if (s > 0) positive = true; }
        return negative && positive;
    }

    static (double Min, double Max)? PlaneCutInterval(
        in Triangle triangle, ReadOnlySpan<int> signs, in Triangle plane, DVec3 axis, DVec3 projectionOrigin) =>
        PlaneCut(triangle, signs, plane, axis, projectionOrigin) is { } c ? (c.Min, c.Max) : null;

    /// <summary>The triangle's cut by the other triangle's plane: the points where its edges cross that plane (and its vertices
    /// lying in it), each a point of the intersection line, with their parameters along <paramref name="axis"/> from
    /// <paramref name="projectionOrigin"/>. Returns the extreme parameters and the cut points that carry them.</summary>
    static (double Min, DVec3 MinPoint, double Max, DVec3 MaxPoint)? PlaneCut(
        in Triangle triangle, ReadOnlySpan<int> signs, in Triangle plane, DVec3 axis, DVec3 projectionOrigin)
    {
        Span<DVec3> p = stackalloc DVec3[3];
        p[0] = triangle.A; p[1] = triangle.B; p[2] = triangle.C;
        Span<double> d = stackalloc double[3];
        d[0] = Orient3Estimate(plane.A, plane.B, plane.C, p[0]);
        d[1] = Orient3Estimate(plane.A, plane.B, plane.C, p[1]);
        d[2] = Orient3Estimate(plane.A, plane.B, plane.C, p[2]);
        Span<double> values = stackalloc double[6];
        Span<DVec3> points = stackalloc DVec3[6];
        int n = 0;
        for (int i = 0; i < 3; i++)
        {
            if (signs[i] == 0) { points[n] = p[i]; values[n++] = DVec3.Dot(p[i] - projectionOrigin, axis); }
            int j = (i + 1) % 3;
            if (signs[i] * signs[j] < 0)
            {
                double denom = d[i] - d[j];
                if (denom != 0)
                {
                    double t = d[i] / denom;
                    DVec3 cut = p[i] + (p[j] - p[i]) * t;
                    points[n] = cut;
                    values[n++] = DVec3.Dot(cut - projectionOrigin, axis);
                }
            }
        }
        if (n == 0) return null;
        double min = values[0], max = values[0];
        for (int i = 1; i < n; i++) { min = Math.Min(min, values[i]); max = Math.Max(max, values[i]); }
        DVec3 minPoint = points[0], maxPoint = points[0];
        for (int i = n - 1; i >= 0; i--) { if (values[i] == min) minPoint = points[i]; if (values[i] == max) maxPoint = points[i]; }
        return (min, minPoint, max, maxPoint);
    }

    static double Orient3Estimate(DVec3 a, DVec3 b, DVec3 c, DVec3 d) =>
        DVec3.Dot(a - d, DVec3.Cross(b - d, c - d));

    // Exact convex SAT in the dominant 2-D projection. Strict interval overlap on every edge normal means the
    // intersection has positive area; equality on any axis is boundary-only contact. Dyadic arithmetic represents
    // every input double exactly and avoids a scale-dependent shoelace-area cutoff.
    static PairRelation CoplanarProjectedRelation(in Triangle a, in Triangle b)
    {
        DVec3 n = a.Normal;
        int drop = Math.Abs(n.X) >= Math.Abs(n.Y) && Math.Abs(n.X) >= Math.Abs(n.Z) ? 0 :
            Math.Abs(n.Y) >= Math.Abs(n.Z) ? 1 : 2;
        Span<DVec2> av = stackalloc DVec2[3];
        av[0] = Project(a.A, drop); av[1] = Project(a.B, drop); av[2] = Project(a.C, drop);
        Span<DVec2> bv = stackalloc DVec2[3];
        bv[0] = Project(b.A, drop); bv[1] = Project(b.B, drop); bv[2] = Project(b.C, drop);
        bool boundaryOnly = false;
        for (int owner = 0; owner < 2; owner++)
        {
            Span<DVec2> axes = owner == 0 ? av : bv;
            for (int i = 0; i < 3; i++)
            {
                var ai = ExactProjectionInterval(av, axes[i], axes[(i + 1) % 3]);
                var bi = ExactProjectionInterval(bv, axes[i], axes[(i + 1) % 3]);
                int left = Dyadic.Compare(ai.Max, bi.Min);
                int right = Dyadic.Compare(bi.Max, ai.Min);
                if (left < 0 || right < 0) return PairRelation.Separated;
                if (left == 0 || right == 0) boundaryOnly = true;
            }
        }
        return boundaryOnly ? PairRelation.Touching : PairRelation.CoplanarOverlap;
    }

    static (Dyadic Min, Dyadic Max) ExactProjectionInterval(ReadOnlySpan<DVec2> vertices, DVec2 p, DVec2 q)
    {
        Dyadic dx = Dyadic.FromDouble(q.X) - Dyadic.FromDouble(p.X);
        Dyadic dy = Dyadic.FromDouble(q.Y) - Dyadic.FromDouble(p.Y);
        Dyadic negativeDy = -dy;
        Dyadic min = negativeDy * Dyadic.FromDouble(vertices[0].X) + dx * Dyadic.FromDouble(vertices[0].Y), max = min;
        for (int i = 1; i < vertices.Length; i++)
        {
            Dyadic value = negativeDy * Dyadic.FromDouble(vertices[i].X) + dx * Dyadic.FromDouble(vertices[i].Y);
            if (Dyadic.Compare(value, min) < 0) min = value;
            if (Dyadic.Compare(value, max) > 0) max = value;
        }
        return (min, max);
    }

    static DVec2 Project(DVec3 p, int drop) => drop switch
    {
        0 => new(p.Y, p.Z),
        1 => new(p.X, p.Z),
        _ => new(p.X, p.Y)
    };

    static double TriangleDistanceSquared(in Triangle a, in Triangle b)
    {
        double best = Math.Min(
            Math.Min(PointTriangleDistanceSquared(a.A, b), Math.Min(PointTriangleDistanceSquared(a.B, b), PointTriangleDistanceSquared(a.C, b))),
            Math.Min(PointTriangleDistanceSquared(b.A, a), Math.Min(PointTriangleDistanceSquared(b.B, a), PointTriangleDistanceSquared(b.C, a))));
        Span<DVec3> ap = stackalloc DVec3[3], bp = stackalloc DVec3[3];
        ap[0] = a.A; ap[1] = a.B; ap[2] = a.C;
        bp[0] = b.A; bp[1] = b.B; bp[2] = b.C;
        for (int i = 0; i < 3; i++)
        for (int j = 0; j < 3; j++)
            best = Math.Min(best, SegmentDistanceSquared(ap[i], ap[(i + 1) % 3], bp[j], bp[(j + 1) % 3]));
        return best;
    }

    // Real-Time Collision Detection, Christer Ericson, closest-point region tests, evaluated in double precision.
    static double PointTriangleDistanceSquared(DVec3 p, in Triangle t)
    {
        DVec3 ab = t.B - t.A, ac = t.C - t.A, ap = p - t.A;
        double d1 = DVec3.Dot(ab, ap), d2 = DVec3.Dot(ac, ap);
        if (d1 <= 0 && d2 <= 0) return ap.LengthSquared;
        DVec3 bp = p - t.B;
        double d3 = DVec3.Dot(ab, bp), d4 = DVec3.Dot(ac, bp);
        if (d3 >= 0 && d4 <= d3) return bp.LengthSquared;
        double vc = d1 * d4 - d3 * d2;
        if (vc <= 0 && d1 >= 0 && d3 <= 0) return (p - (t.A + ab * (d1 / (d1 - d3)))).LengthSquared;
        DVec3 cp = p - t.C;
        double d5 = DVec3.Dot(ab, cp), d6 = DVec3.Dot(ac, cp);
        if (d6 >= 0 && d5 <= d6) return cp.LengthSquared;
        double vb = d5 * d2 - d1 * d6;
        if (vb <= 0 && d2 >= 0 && d6 <= 0) return (p - (t.A + ac * (d2 / (d2 - d6)))).LengthSquared;
        double va = d3 * d6 - d5 * d4;
        if (va <= 0 && d4 - d3 >= 0 && d5 - d6 >= 0)
            return (p - (t.B + (t.C - t.B) * ((d4 - d3) / ((d4 - d3) + (d5 - d6))))).LengthSquared;
        double denom = 1.0 / (va + vb + vc);
        return (p - (t.A + ab * (vb * denom) + ac * (vc * denom))).LengthSquared;
    }

    static double SegmentDistanceSquared(DVec3 p1, DVec3 q1, DVec3 p2, DVec3 q2)
    {
        DVec3 d1 = q1 - p1, d2 = q2 - p2, r = p1 - p2;
        double a = DVec3.Dot(d1, d1), e = DVec3.Dot(d2, d2), f = DVec3.Dot(d2, r);
        double s, t;
        if (a == 0 && e == 0) return r.LengthSquared;
        if (a == 0) { s = 0; t = Math.Clamp(f / e, 0, 1); }
        else
        {
            double c = DVec3.Dot(d1, r);
            if (e == 0) { t = 0; s = Math.Clamp(-c / a, 0, 1); }
            else
            {
                double b = DVec3.Dot(d1, d2), denom = a * e - b * b;
                s = denom != 0 ? Math.Clamp((b * f - c * e) / denom, 0, 1) : 0;
                t = (b * s + f) / e;
                if (t < 0) { t = 0; s = Math.Clamp(-c / a, 0, 1); }
                else if (t > 1) { t = 1; s = Math.Clamp((b - c) / a, 0, 1); }
            }
        }
        return ((p1 + d1 * s) - (p2 + d2 * t)).LengthSquared;
    }

    static SolidValidation ValidateSolid(Mesh mesh, double tolerance, WorkBudget budget)
    {
        using var edges = new MeshEdges(mesh, budget);
        for (int edge = 0; edge < edges.Count; edge++)
        {
            budget.Checkpoint(edge);
            if (edges.Uses[edge] != 2) return new(SolidKind.Open, "open_or_nonmanifold_edges", null);
            if ((edges.UseA[edge] & 1) == (edges.UseB[edge] & 1))
                return new(SolidKind.Invalid, "inconsistent_surface_orientation", null);
        }

        int triangleCount = mesh.Triangles.Length;
        int[] walk = ArrayPool<int>.Shared.Rent(triangleCount), work = ArrayPool<int>.Shared.Rent(triangleCount);
        int[] adjacency = ArrayPool<int>.Shared.Rent(triangleCount * 3);
        try
        {
            return ValidateClosedSolid(mesh, edges, walk, work, adjacency, budget);
        }
        finally
        {
            ArrayPool<int>.Shared.Return(walk);
            ArrayPool<int>.Shared.Return(work);
            ArrayPool<int>.Shared.Return(adjacency);
        }
    }

    static SolidValidation ValidateClosedSolid(Mesh mesh, in MeshEdges edges, int[] walk, int[] work, int[] adjacency, WorkBudget budget)
    {
        // Every welded vertex must have one connected incident-face fan. Edge counts alone accept two closed shells
        // pinched together at a point (and some same-component pinches), which is not a 2-manifold boundary.
        if (!VertexFansConnected(mesh, edges, work, walk, budget))
            return new(SolidKind.Invalid, "nonmanifold_vertex_link", null);

        int triangleCount = mesh.Triangles.Length;
        FillAdjacency(edges, triangleCount, adjacency, work, budget);
        var components = new List<int[]>();
        int[] componentOfTriangle = work;
        Array.Fill(componentOfTriangle, -1, 0, triangleCount);
        int tail = 0;
        for (int seed = 0; seed < triangleCount; seed++)
        {
            if (componentOfTriangle[seed] != -1) continue;
            int start = tail, head = tail;
            componentOfTriangle[seed] = components.Count;
            walk[tail++] = seed;
            while (head < tail)
            {
                budget.Cancellation.ThrowIfCancellationRequested();
                int current = walk[head++];
                for (int k = 0; k < 3; k++)
                {
                    int next = adjacency[current * 3 + k];
                    if (componentOfTriangle[next] == -1) { componentOfTriangle[next] = components.Count; walk[tail++] = next; }
                }
            }
            DVec3 origin = mesh.Triangles[walk[start]].A;
            double sixVolume = 0, magnitude = 0;
            for (int i = start; i < tail; i++)
            {
                budget.Checkpoint(i - start);
                ref readonly Triangle tri = ref mesh.Triangles[walk[i]];
                double term = DVec3.Dot(tri.A - origin, DVec3.Cross(tri.B - origin, tri.C - origin));
                sixVolume += term; magnitude += Math.Abs(term);
            }
            if (!double.IsFinite(sixVolume) || Math.Abs(sixVolume) <= 128 * MachineEpsilon * magnitude)
                return new(SolidKind.Invalid, "zero_or_unstable_enclosed_volume", null);
            components.Add(walk.AsSpan(start, tail - start).ToArray());
        }

        // A closed edge graph can still be self-intersecting: test each unordered triangle pair whose bounds meet exactly once.
        var stack = new Stack<(BvhNode A, BvhNode B)>();
        stack.Push((mesh.Root, mesh.Root));
        while (stack.Count > 0)
        {
            budget.Cancellation.ThrowIfCancellationRequested();
            var nodes = stack.Pop();
            if (!nodes.A.Bounds.Overlaps(nodes.B.Bounds, 0)) continue;
            bool self = ReferenceEquals(nodes.A, nodes.B);
            if (nodes.A.IsLeaf && nodes.B.IsLeaf)
            {
                for (int ia = nodes.A.Start; ia < nodes.A.Start + nodes.A.Count; ia++)
                for (int ib = self ? ia + 1 : nodes.B.Start; ib < nodes.B.Start + nodes.B.Count; ib++)
                {
                    int ta = mesh.Order[ia], tb = mesh.Order[ib];
                    ref readonly Triangle x = ref mesh.Triangles[ta], y = ref mesh.Triangles[tb];
                    if (!x.Bounds.Overlaps(y.Bounds, 0)) continue;
                    budget.Consume();
                    int shared = SharedVertexCount(mesh, x, y);
                    if (shared == 2 && componentOfTriangle[ta] == componentOfTriangle[tb] && OppositeVertexOffPlane(mesh, x, y)) continue;
                    PairRelation relation = Classify(x, y, 0);
                    if (relation is PairRelation.Crossing or PairRelation.CoplanarOverlap ||
                        (relation == PairRelation.Touching &&
                         (shared == 0 || componentOfTriangle[ta] != componentOfTriangle[tb])))
                        return new(SolidKind.Invalid, "self_intersecting_or_self_touching_mesh", null);
                }
                continue;
            }
            if (self)
            {
                stack.Push((nodes.A.Left!, nodes.A.Left!)); stack.Push((nodes.A.Left!, nodes.A.Right!));
                stack.Push((nodes.A.Right!, nodes.A.Right!));
            }
            else if (nodes.A.IsLeaf && !nodes.B.IsLeaf)
            {
                stack.Push((nodes.A, nodes.B.Left!)); stack.Push((nodes.A, nodes.B.Right!));
            }
            else if (!nodes.A.IsLeaf && nodes.B.IsLeaf)
            {
                stack.Push((nodes.A.Left!, nodes.B)); stack.Push((nodes.A.Right!, nodes.B));
            }
            else
            {
                stack.Push((nodes.A.Left!, nodes.B.Left!)); stack.Push((nodes.A.Left!, nodes.B.Right!));
                stack.Push((nodes.A.Right!, nodes.B.Left!)); stack.Push((nodes.A.Right!, nodes.B.Right!));
            }
        }
        // The supported fill is a union of disjoint closed components. Nested shells need a separately declared
        // material/cavity rule; accepting them here would silently choose even-odd or winding fill semantics.
        if (components.Count > 1)
        {
            var componentMeshes = components.Select(c => ComponentMesh(mesh, c, budget)).ToArray();
            for (int i = 0; i < components.Count; i++)
            for (int j = i + 1; j < components.Count; j++)
            {
                InsideResult iInJ = PointInside(mesh.Triangles[components[i][0]].A, componentMeshes[j], 0, budget);
                InsideResult jInI = PointInside(mesh.Triangles[components[j][0]].A, componentMeshes[i], 0, budget);
                if (iInJ == InsideResult.Unknown || jInI == InsideResult.Unknown)
                    return new(SolidKind.Invalid, "component_nesting_unassessed", null);
                if (iInJ == InsideResult.Inside || jInI == InsideResult.Inside)
                    return new(SolidKind.Invalid, "nested_shell_fill_rule_unassessed", null);
            }
        }
        return new(SolidKind.Valid, null, components);
    }

    static Mesh ComponentMesh(Mesh source, int[] component, WorkBudget budget)
    {
        Triangle[] triangles = new Triangle[component.Length];
        for (int i = 0; i < component.Length; i++)
        {
            budget.Checkpoint(i);
            triangles[i] = source.Triangles[component[i]];
        }
        int[] order = Enumerable.Range(0, triangles.Length).ToArray();
        BvhNode root = BuildBvh(triangles, order, 0, order.Length, budget);
        return new Mesh(source.Vertices, source.Welded, triangles, order, root);
    }

    // Triangles sharing an edge: x's other vertices sign 0 against y, so a nonzero third sign rules out coplanar and straddling.
    // Classify then answers Touching or Separated, which the scan accepts alike for a same-component shared edge.
    static bool OppositeVertexOffPlane(Mesh mesh, in Triangle x, in Triangle y)
    {
        int y0 = mesh.Welded[y.I0], y1 = mesh.Welded[y.I1], y2 = mesh.Welded[y.I2];
        int x0 = mesh.Welded[x.I0], x1 = mesh.Welded[x.I1];
        DVec3 opposite = x0 != y0 && x0 != y1 && x0 != y2 ? x.A : x1 != y0 && x1 != y1 && x1 != y2 ? x.B : x.C;
        return Robust.Orient3Sign(y.A, y.B, y.C, opposite) != 0;
    }

    static int SharedVertexCount(Mesh mesh, in Triangle a, in Triangle b)
    {
        int a0 = mesh.Welded[a.I0], a1 = mesh.Welded[a.I1], a2 = mesh.Welded[a.I2];
        int b0 = mesh.Welded[b.I0], b1 = mesh.Welded[b.I1], b2 = mesh.Welded[b.I2];
        int count = 0;
        if (a0 == b0 || a0 == b1 || a0 == b2) count++;
        if (a1 == b0 || a1 == b1 || a1 == b2) count++;
        if (a2 == b0 || a2 == b1 || a2 == b2) count++;
        return count;
    }

    static InsideResult ComponentsInside(Mesh source, IReadOnlyList<int[]> components, Mesh target, double tolerance, WorkBudget budget)
    {
        bool anyInside = false, anyOutside = false;
        foreach (int[] component in components)
        {
            budget.Cancellation.ThrowIfCancellationRequested();
            DVec3 sample = source.Triangles[component[0]].A;
            InsideResult result = PointInside(sample, target, tolerance, budget);
            if (result == InsideResult.Unknown) return result;
            if (result == InsideResult.Inside) anyInside = true;
            else anyOutside = true;
        }
        if (anyInside && anyOutside) return InsideResult.Mixed;
        return anyInside ? InsideResult.Inside : InsideResult.Outside;
    }

    static readonly DVec3[] RayDirections =
    [
        new(1, 0.3713906763541037, 0.694847539), new(-0.238947, 1, 0.512337),
        new(0.613117, -0.441921, 1), new(1, -0.754877666, 0.245122),
        new(-0.569841, -0.831147, 1), new(0.173205, 1, -0.782913),
        new(1, 0.6180339887498948, -0.414213562373095), new(-1, 0.2718281828459, -0.7071067811865),
        new(0.577350269, -1, -0.303577269)
    ];

    static InsideResult PointInside(DVec3 point, Mesh mesh, double tolerance, WorkBudget budget)
    {
        int? agreed = null;
        int agreements = 0;
        foreach (DVec3 raw in RayDirections)
        {
            DVec3 direction = raw / Math.Sqrt(raw.LengthSquared);
            int crossings = 0;
            bool ambiguous = false;
            var stack = new Stack<BvhNode>(); stack.Push(mesh.Root);
            while (stack.Count > 0 && !ambiguous)
            {
                budget.Cancellation.ThrowIfCancellationRequested();
                BvhNode node = stack.Pop();
                if (!node.Bounds.RayIntersects(point, direction, tolerance)) continue;
                if (!node.IsLeaf) { stack.Push(node.Left!); stack.Push(node.Right!); continue; }
                for (int i = node.Start; i < node.Start + node.Count; i++)
                {
                    budget.Consume();
                    var hit = RayTriangle(point, direction, mesh.Triangles[mesh.Order[i]], tolerance);
                    if (hit == RayHit.Ambiguous) { ambiguous = true; break; }
                    if (hit == RayHit.Hit) crossings++;
                }
            }
            if (ambiguous) continue;
            int parity = crossings & 1;
            if (agreed is null || agreed == parity) { agreed = parity; agreements++; }
            else return InsideResult.Unknown;
            if (agreements == 3) return parity == 1 ? InsideResult.Inside : InsideResult.Outside;
        }
        return InsideResult.Unknown;
    }

    static RayHit RayTriangle(DVec3 origin, DVec3 direction, in Triangle tri, double tolerance)
    {
        DVec3 e1 = tri.B - tri.A, e2 = tri.C - tri.A;
        DVec3 p = DVec3.Cross(direction, e2);
        double determinant = DVec3.Dot(e1, p);
        double determinantFloor = 64 * MachineEpsilon * Math.Sqrt(e1.LengthSquared * p.LengthSquared);
        if (Math.Abs(determinant) <= determinantFloor)
            return Robust.RayDeterminantSign(tri.A, tri.B, tri.C, direction) == 0 ? RayHit.Miss : RayHit.Ambiguous;
        double inverse = 1 / determinant;
        DVec3 s = origin - tri.A;
        double u = DVec3.Dot(s, p) * inverse;
        DVec3 q = DVec3.Cross(s, e1);
        double v = DVec3.Dot(direction, q) * inverse;
        double t = DVec3.Dot(e2, q) * inverse;
        double baryEpsilon = 256 * MachineEpsilon;
        if (u < -baryEpsilon || v < -baryEpsilon || u + v > 1 + baryEpsilon || t <= tolerance) return RayHit.Miss;
        if (u <= baryEpsilon || v <= baryEpsilon || 1 - u - v <= baryEpsilon) return RayHit.Ambiguous;
        return RayHit.Hit;
    }

    static PairRelation Max(PairRelation x, PairRelation y) => (PairRelation)Math.Max((int)x, (int)y);

    sealed class WorkBudget(long maximum, CancellationToken cancellation)
    {
        public long Used { get; private set; }
        public CancellationToken Cancellation { get; } = cancellation;
        public void Consume()
        {
            Cancellation.ThrowIfCancellationRequested();
            if (Used >= maximum) throw new BudgetExceededException();
            Used++;
        }
        public void Checkpoint(int iteration)
        {
            if ((iteration & 1023) == 0) Cancellation.ThrowIfCancellationRequested();
        }
        /// <summary>Consume <paramref name="units"/> at once, as that many <see cref="Consume"/> calls would: the budget is
        /// exhausted (Used = maximum, then the exception) exactly when they would have run past it. No cancellation check.</summary>
        public void Charge(long units)
        {
            if (units > maximum - Used) { Used = maximum; throw new BudgetExceededException(); }
            Used += units;
        }
    }

    sealed class BudgetExceededException : Exception { }
    enum PairRelation { Separated, Touching, CoplanarOverlap, Crossing }
    enum SolidKind { Valid, Open, Invalid }
    enum InsideResult { Outside, Inside, Mixed, Unknown }
    enum RayHit { Miss, Hit, Ambiguous }
    readonly record struct SolidValidation(SolidKind Kind, string? Reason, IReadOnlyList<int[]>? Components);
    readonly record struct Edge(int A, int B);

    sealed class Mesh(DVec3[] vertices, int[] welded, Triangle[] triangles, int[] order, BvhNode root)
    {
        public DVec3[] Vertices { get; } = vertices;
        public int[] Welded { get; } = welded;
        public Triangle[] Triangles { get; } = triangles;
        public int[] Order { get; } = order;
        public BvhNode Root { get; } = root;
    }

    sealed class BvhNode(Bounds bounds, int start, int count, BvhNode? left, BvhNode? right)
    {
        static int nextId;
        public int Id { get; } = NextId();
        static int NextId() { Interlocked.Increment(ref bvhNodesBuilt); return Interlocked.Increment(ref nextId); }
        public Bounds Bounds { get; } = bounds;
        public int Start { get; } = start;
        public int Count { get; } = count;
        public BvhNode? Left { get; } = left;
        public BvhNode? Right { get; } = right;
        public bool IsLeaf => Left is null;
    }

    readonly struct Triangle
    {
        public readonly int I0, I1, I2;
        public readonly DVec3 A, B, C, Normal, Centroid;
        public readonly Bounds Bounds;
        public Triangle(int i0, int i1, int i2, DVec3 a, DVec3 b, DVec3 c)
        {
            I0 = i0; I1 = i1; I2 = i2; A = a; B = b; C = c;
            Normal = DVec3.Cross(b - a, c - a);
            Centroid = (a + b + c) / 3;
            Bounds = Bounds.Empty.Add(a).Add(b).Add(c);
        }
    }

    readonly record struct Bounds(DVec3 Min, DVec3 Max)
    {
        public static readonly Bounds Empty = new(
            new(double.PositiveInfinity, double.PositiveInfinity, double.PositiveInfinity),
            new(double.NegativeInfinity, double.NegativeInfinity, double.NegativeInfinity));
        public Bounds Add(DVec3 p) => new(DVec3.Min(Min, p), DVec3.Max(Max, p));
        public Bounds Union(Bounds other) => new(DVec3.Min(Min, other.Min), DVec3.Max(Max, other.Max));
        public bool Overlaps(in Bounds other, double tolerance) =>
            Min.X <= other.Max.X + tolerance && other.Min.X <= Max.X + tolerance &&
            Min.Y <= other.Max.Y + tolerance && other.Min.Y <= Max.Y + tolerance &&
            Min.Z <= other.Max.Z + tolerance && other.Min.Z <= Max.Z + tolerance;
        public double SurfaceArea
        {
            get { DVec3 d = Max - Min; return 2 * (d.X * d.Y + d.X * d.Z + d.Y * d.Z); }
        }
        public bool RayIntersects(DVec3 origin, DVec3 direction, double tolerance)
        {
            double near = 0, far = double.PositiveInfinity;
            for (int axis = 0; axis < 3; axis++)
            {
                double lo = Min[axis] - tolerance, hi = Max[axis] + tolerance;
                double d = direction[axis];
                if (d == 0) { if (origin[axis] < lo || origin[axis] > hi) return false; continue; }
                double t0 = (lo - origin[axis]) / d, t1 = (hi - origin[axis]) / d;
                if (t0 > t1) (t0, t1) = (t1, t0);
                near = Math.Max(near, t0); far = Math.Min(far, t1);
                if (near > far) return false;
            }
            return far >= 0;
        }
    }

    readonly record struct DVec3(double X, double Y, double Z)
    {
        public double this[int axis] => axis == 0 ? X : axis == 1 ? Y : Z;
        public double LengthSquared => X * X + Y * Y + Z * Z;
        public static DVec3 operator +(DVec3 a, DVec3 b) => new(a.X + b.X, a.Y + b.Y, a.Z + b.Z);
        public static DVec3 operator -(DVec3 a, DVec3 b) => new(a.X - b.X, a.Y - b.Y, a.Z - b.Z);
        public static DVec3 operator *(DVec3 a, double b) => new(a.X * b, a.Y * b, a.Z * b);
        public static DVec3 operator /(DVec3 a, double b) => new(a.X / b, a.Y / b, a.Z / b);
        public static double Dot(DVec3 a, DVec3 b) => a.X * b.X + a.Y * b.Y + a.Z * b.Z;
        public static DVec3 Cross(DVec3 a, DVec3 b) => new(a.Y * b.Z - a.Z * b.Y, a.Z * b.X - a.X * b.Z, a.X * b.Y - a.Y * b.X);
        public static DVec3 Min(DVec3 a, DVec3 b) => new(Math.Min(a.X, b.X), Math.Min(a.Y, b.Y), Math.Min(a.Z, b.Z));
        public static DVec3 Max(DVec3 a, DVec3 b) => new(Math.Max(a.X, b.X), Math.Max(a.Y, b.Y), Math.Max(a.Z, b.Z));
    }

    readonly record struct DVec2(double X, double Y)
    {
        public static DVec2 operator +(DVec2 a, DVec2 b) => new(a.X + b.X, a.Y + b.Y);
        public static DVec2 operator -(DVec2 a, DVec2 b) => new(a.X - b.X, a.Y - b.Y);
        public static DVec2 operator *(DVec2 a, double b) => new(a.X * b, a.Y * b);
        public static double Cross(DVec2 a, DVec2 b) => a.X * b.Y - a.Y * b.X;
    }

    static class Robust
    {
        public static int Orient3Sign(DVec3 a, DVec3 b, DVec3 c, DVec3 d)
        {
            DVec3 ad = a - d, bd = b - d, cd = c - d;
            double bycz = bd.Y * cd.Z, bzcy = bd.Z * cd.Y;
            double bxcz = bd.X * cd.Z, bzcx = bd.Z * cd.X;
            double bxcy = bd.X * cd.Y, bycx = bd.Y * cd.X;
            double p1 = ad.X * (bycz - bzcy);
            double p2 = ad.Y * (bxcz - bzcx);
            double p3 = ad.Z * (bxcy - bycx);
            double det = p1 - p2 + p3;
            double permanent = Math.Abs(ad.X) * (Math.Abs(bycz) + Math.Abs(bzcy))
                             + Math.Abs(ad.Y) * (Math.Abs(bxcz) + Math.Abs(bzcx))
                             + Math.Abs(ad.Z) * (Math.Abs(bxcy) + Math.Abs(bycx));
            double bound = 16 * MachineEpsilon * permanent;
            if (Math.Abs(det) > bound) return Math.Sign(det);
            // A vertex equal to d zeroes a row (double subtraction is zero only on equal values), so the exact answer is 0.
            if (IsZero(ad) || IsZero(bd) || IsZero(cd)) return 0;
            Dyadic ax = Dyadic.FromDouble(a.X) - Dyadic.FromDouble(d.X);
            Dyadic ay = Dyadic.FromDouble(a.Y) - Dyadic.FromDouble(d.Y);
            Dyadic az = Dyadic.FromDouble(a.Z) - Dyadic.FromDouble(d.Z);
            Dyadic bx = Dyadic.FromDouble(b.X) - Dyadic.FromDouble(d.X);
            Dyadic by = Dyadic.FromDouble(b.Y) - Dyadic.FromDouble(d.Y);
            Dyadic bz = Dyadic.FromDouble(b.Z) - Dyadic.FromDouble(d.Z);
            Dyadic cx = Dyadic.FromDouble(c.X) - Dyadic.FromDouble(d.X);
            Dyadic cy = Dyadic.FromDouble(c.Y) - Dyadic.FromDouble(d.Y);
            Dyadic cz = Dyadic.FromDouble(c.Z) - Dyadic.FromDouble(d.Z);
            return (ax * (by * cz - bz * cy) - ay * (bx * cz - bz * cx) + az * (bx * cy - by * cx)).Mantissa.Sign;
        }

        static bool IsZero(DVec3 v) => v.X == 0 && v.Y == 0 && v.Z == 0;

        public static int RayDeterminantSign(DVec3 a, DVec3 b, DVec3 c, DVec3 direction)
        {
            Dyadic ax = Dyadic.FromDouble(b.X) - Dyadic.FromDouble(a.X);
            Dyadic ay = Dyadic.FromDouble(b.Y) - Dyadic.FromDouble(a.Y);
            Dyadic az = Dyadic.FromDouble(b.Z) - Dyadic.FromDouble(a.Z);
            Dyadic bx = Dyadic.FromDouble(direction.X), by = Dyadic.FromDouble(direction.Y), bz = Dyadic.FromDouble(direction.Z);
            Dyadic cx = Dyadic.FromDouble(c.X) - Dyadic.FromDouble(a.X);
            Dyadic cy = Dyadic.FromDouble(c.Y) - Dyadic.FromDouble(a.Y);
            Dyadic cz = Dyadic.FromDouble(c.Z) - Dyadic.FromDouble(a.Z);
            return (ax * (by * cz - bz * cy) - ay * (bx * cz - bz * cx) + az * (bx * cy - by * cx)).Mantissa.Sign;
        }

        public static int Orient2Sign(DVec2 a, DVec2 b, DVec2 c)
        {
            double x1 = b.X - a.X, y1 = b.Y - a.Y, x2 = c.X - a.X, y2 = c.Y - a.Y;
            double p = x1 * y2, q = y1 * x2, det = p - q;
            double bound = 16 * MachineEpsilon * (Math.Abs(p) + Math.Abs(q));
            if (Math.Abs(det) > bound) return Math.Sign(det);
            Dyadic ax = Dyadic.FromDouble(b.X) - Dyadic.FromDouble(a.X);
            Dyadic ay = Dyadic.FromDouble(b.Y) - Dyadic.FromDouble(a.Y);
            Dyadic bx = Dyadic.FromDouble(c.X) - Dyadic.FromDouble(a.X);
            Dyadic by = Dyadic.FromDouble(c.Y) - Dyadic.FromDouble(a.Y);
            return (ax * by - ay * bx).Mantissa.Sign;
        }
    }

    readonly record struct Dyadic(BigInteger Mantissa, int Exponent)
    {
        public static Dyadic FromDouble(double value)
        {
            if (value == 0) return new(BigInteger.Zero, 0);
            long bits = BitConverter.DoubleToInt64Bits(value);
            bool negative = bits < 0;
            int rawExponent = (int)((bits >> 52) & 0x7ff);
            long fraction = bits & 0x000f_ffff_ffff_ffffL;
            long mantissa;
            int exponent;
            if (rawExponent == 0) { mantissa = fraction; exponent = -1074; }
            else { mantissa = fraction | (1L << 52); exponent = rawExponent - 1023 - 52; }
            int zeros = BitOperations.TrailingZeroCount(mantissa);
            mantissa >>= zeros;
            return new Dyadic(new BigInteger(negative ? -mantissa : mantissa), exponent + zeros);
        }
        public static Dyadic operator +(Dyadic a, Dyadic b)
        {
            if (a.Mantissa.IsZero) return b;
            if (b.Mantissa.IsZero) return a;
            int exponent = Math.Min(a.Exponent, b.Exponent);
            return new Dyadic((a.Mantissa << (a.Exponent - exponent)) + (b.Mantissa << (b.Exponent - exponent)), exponent).Normalize();
        }
        public static Dyadic operator -(Dyadic value) => new(-value.Mantissa, value.Exponent);
        public static Dyadic operator -(Dyadic a, Dyadic b) => a + new Dyadic(-b.Mantissa, b.Exponent);
        public static Dyadic operator *(Dyadic a, Dyadic b) => new Dyadic(a.Mantissa * b.Mantissa, a.Exponent + b.Exponent).Normalize();
        public static int Compare(Dyadic a, Dyadic b)
        {
            if (a.Exponent == b.Exponent) return a.Mantissa.CompareTo(b.Mantissa);
            int exponent = Math.Min(a.Exponent, b.Exponent);
            return (a.Mantissa << (a.Exponent - exponent)).CompareTo(b.Mantissa << (b.Exponent - exponent));
        }
        Dyadic Normalize()
        {
            if (Mantissa.IsZero) return new(BigInteger.Zero, 0);
            if (!Mantissa.IsEven) return this;
            int zeros = (int)BigInteger.TrailingZeroCount(Mantissa);
            return new(Mantissa >> zeros, Exponent + zeros);
        }
    }
}
