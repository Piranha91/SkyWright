using System.Numerics;

using HousecarlCore;

namespace SkyWright.Engine;

/// <summary>A directional projection reading on the supplied triangles, never a volume or visibility reading.
/// FullCoverReading is nominal_whole_cover, partial, numerically_uncertain, or incomplete. AreaUncertainty is a
/// boundary-sensitivity and floating-point estimate, not a certified interval. Certified remains false. Areas sum
/// the dependent's projected triangle areas (including soup multiplicity); overlapping host faces are unioned.
/// An incomplete result's zero covered area and total uncovered area are unknown bounds, not a no-cover answer.</summary>
public sealed record DirectionalCoverageResult(
    double ProjectedArea, double CoveredArea, double UncoveredArea,
    double? MinGap, double? MaxGap, bool Complete, string? RefusalReason,
    double AreaUncertainty, string FullCoverReading, int TrianglePairs, int Pieces)
{
    public bool Certified => false;
    /// <summary>Names covered-area sensitivity at a positive-gap band boundary. A fixed uncovered footprint can
    /// still make the whole-cover reading partial; this metadata is not itself whole-cover uncertainty.</summary>
    public string? UncertaintyReason { get; init; }
    /// <summary>Remainder after a separate union over 0 &lt; gap &lt;= maxGap + tolerance. Complete readings only.
    /// It preserves missing footprints and holes while testing whether gap sensitivity could permit whole cover.</summary>
    public double? PossibleUncoveredArea { get; init; }
    /// <summary>Coordinate-boundary and floating-point sensitivity estimate, excluding uncertainty in covered area
    /// at the gap band. This is an approximation, never a certified area enclosure.</summary>
    public double? ArithmeticAreaUncertainty { get; init; }
}

/// <summary>Bounded projected surface coverage by faces above the dependent along a supplied direction. Each dependent
/// triangle is partitioned by subtraction of qualifying convex host projections. Every positive-area remainder is
/// retained; shared triangulation edges and duplicate host faces add no area. This is labelled floating-point polygon
/// arithmetic, never a closed-solid, volume, material-opacity, or engine-visibility inference.</summary>
public static class DirectionalCoverage
{
    /// <summary>Faces whose normal cosine along direction reaches faceCos qualify where their signed separation is
    /// greater than tolerance and no greater than maxGap. Edge-on dependent triangles have no projected area.
    /// maxTrianglePairs bounds enumerated dependent/host triangle pairs; maxPieces bounds pending disjoint polygons
    /// across the nominal and relaxed positive-gap unions together.
    /// Budget exhaustion and cancellation are named incomplete results. The nominal whole-cover reading requires no
    /// positive-area polygon to remain; a small remaining polygon is retained and labelled numerically_uncertain.
    /// Positive gaps within tolerance of zero or maxGap carry gap_boundary_sensitivity; exact zero stays contact.
    /// Gap sensitivity changes the whole-cover label only when the relaxed union could cover the whole dependent.</summary>
    public static DirectionalCoverageResult Measure(TriangleSoup dependent, TriangleGrid host, Vector3 direction,
        float maxGap, float faceCos = 0.866f, int maxTrianglePairs = 2_000_000, int maxPieces = 4096,
        double tolerance = 1e-5, CancellationToken cancellationToken = default)
    {
        double area = 0, uncovered = 0, possibleUncovered = 0;
        double sensitivityBoundary = 0, coordinateScale = 1, gapBoundaryArea = 0;
        double? minGap = null, maxMeasuredGap = null;
        int pairs = 0, peakPieces = 0;
        long arithmeticOperations = 0;
        DirectionalCoverageResult Refuse(string reason) =>
            new(area, 0, area, null, null, false, reason, area, "incomplete", pairs, peakPieces);

        // Both unions use identical bounded subtraction. The other union's pending pieces count against the
        // same work cap; neither a nominal fragment nor a relaxed-band fragment is dropped to fit the budget.
        string? Subtract(ref List<List<P2>> remaining, List<HalfPlane> planes, Height gap,
            bool measureGaps, int otherPieces)
        {
            var next = new List<List<P2>>();
            for (int r = 0; r < remaining.Count; r++)
            {
                if (cancellationToken.IsCancellationRequested) return "cancelled";
                var inside = remaining[r];
                foreach (var plane in planes)
                {
                    var outside = Clip(inside, plane, false, ref arithmeticOperations);
                    if (!Valid(outside)) return "numeric_overflow";
                    if (Area(outside) > 0)
                    {
                        int pending = otherPieces + next.Count + 1 + remaining.Count - r - 1;
                        peakPieces = Math.Max(peakPieces, pending);
                        if (pending > maxPieces) return "polygon_piece_budget";
                        next.Add(outside);
                    }
                    inside = Clip(inside, plane, true, ref arithmeticOperations);
                    if (!Valid(inside)) return "numeric_overflow";
                    if (Area(inside) == 0) break;
                }
                // The inside polygon is removed once. Duplicate or overlapping faces cannot remove it again.
                if (measureGaps && Area(inside) > 0)
                    foreach (var p in inside)
                    {
                        double g = gap.At(p);
                        minGap = minGap is null ? g : Math.Min(minGap.Value, g);
                        maxMeasuredGap = maxMeasuredGap is null ? g : Math.Max(maxMeasuredGap.Value, g);
                    }
            }
            remaining = next;
            return null;
        }

        if (cancellationToken.IsCancellationRequested) return Refuse("cancelled");
        if (!Finite(maxGap) || maxGap < 0 || !Finite(faceCos) || faceCos < 0 || faceCos > 1
            || !Finite(tolerance) || tolerance < 0 || maxTrianglePairs < 0 || maxPieces < 0)
            return Refuse("invalid_parameters");
        var d = new P3(direction);
        if (!d.IsFinite || d.LengthSquared == 0) return Refuse("invalid_direction");
        d /= Math.Sqrt(d.LengthSquared);
        // A stable orthonormal frame: U x V = direction. The direction is general, rather than fixed at +Z.
        var axis = Math.Abs(d.X) <= Math.Abs(d.Y) && Math.Abs(d.X) <= Math.Abs(d.Z) ? new P3(1, 0, 0)
                 : Math.Abs(d.Y) <= Math.Abs(d.Z) ? new P3(0, 1, 0) : new P3(0, 0, 1);
        var u = P3.Cross(axis, d); u /= Math.Sqrt(u.LengthSquared);
        var v = P3.Cross(d, u);
        if (dependent.Indices.Length % 3 != 0) return Refuse("invalid_dependent_indices");

        var triangles = new List<DependentTriangle>();
        for (int t = 0; t < dependent.TriangleCount; t++)
        {
            if (cancellationToken.IsCancellationRequested) return Refuse("cancelled");
            int ia = dependent.Indices[t * 3], ib = dependent.Indices[t * 3 + 1], ic = dependent.Indices[t * 3 + 2];
            if ((uint)ia >= (uint)dependent.Vertices.Length || (uint)ib >= (uint)dependent.Vertices.Length
                || (uint)ic >= (uint)dependent.Vertices.Length) return Refuse("invalid_dependent_indices");
            var origin = new P3(dependent.Vertices[ia]);
            var b = new P3(dependent.Vertices[ib]) - origin;
            var c = new P3(dependent.Vertices[ic]) - origin;
            if (!origin.IsFinite || !b.IsFinite || !c.IsFinite) return Refuse("non_finite_geometry");
            var polygon = new List<P2> { new(0, 0), Project(b, u, v), Project(c, u, v) };
            double projected = Area(polygon);
            if (!Finite(projected)) return Refuse("numeric_overflow");
            if (projected == 0) continue; // Exactly zero projected area; no positive fragment is discarded.
            var n = P3.Cross(b, c);
            var height = HeightOf(n, new P3(0, 0, 0), d, u, v);
            if (!height.IsFinite) return Refuse("numeric_overflow");
            area += projected;
            sensitivityBoundary += Perimeter(polygon);
            coordinateScale = Math.Max(coordinateScale, Scale(polygon));
            triangles.Add(new(origin, polygon, height));
        }
        if (!Finite(area)) return Refuse("numeric_overflow");

        foreach (var dependentTriangle in triangles)
        {
            var remaining = new List<List<P2>> { dependentTriangle.Polygon };
            var possibleRemaining = new List<List<P2>> { dependentTriangle.Polygon };
            if (maxPieces < 2) return Refuse("polygon_piece_budget");
            peakPieces = Math.Max(peakPieces, 2);
            for (int h = 0; h < host.TriangleCount && (remaining.Count > 0 || possibleRemaining.Count > 0); h++)
            {
                if (cancellationToken.IsCancellationRequested) return Refuse("cancelled");
                if (pairs >= maxTrianglePairs) return Refuse("triangle_pair_budget");
                pairs++;
                var hostTriangle = host.GetTriangle(h);
                var a = new P3(hostTriangle.A) - dependentTriangle.Origin;
                var b = new P3(hostTriangle.B) - dependentTriangle.Origin;
                var c = new P3(hostTriangle.C) - dependentTriangle.Origin;
                if (!a.IsFinite || !b.IsFinite || !c.IsFinite) return Refuse("non_finite_geometry");
                var n = P3.Cross(b - a, c - a);
                double length = Math.Sqrt(n.LengthSquared);
                if (length == 0) continue;
                if (P3.Dot(n, d) / length < faceCos) continue;

                var polygon = new List<P2> { Project(a, u, v), Project(b, u, v), Project(c, u, v) };
                double winding = Cross(polygon[1] - polygon[0], polygon[2] - polygon[0]);
                if (winding == 0) continue;
                if (!Finite(winding)) return Refuse("numeric_overflow");
                if (!BoxesMeet(dependentTriangle.Polygon, polygon)) continue;
                var height = HeightOf(n, a, d, u, v);
                var gap = height - dependentTriangle.Height;
                if (!gap.IsFinite) return Refuse("numeric_overflow");
                var planes = EdgePlanes(polygon, winding > 0 ? 1 : -1);
                var projection = dependentTriangle.Polygon;
                foreach (var plane in planes)
                {
                    projection = Clip(projection, plane, true, ref arithmeticOperations);
                    if (projection.Count < 3) break;
                }
                if (!Valid(projection)) return Refuse("numeric_overflow");
                if (Area(projection) == 0) continue;
                // Before the nominal band can reject a face, name positive-area sensitivity at either boundary.
                // This never thickens the nominal cover or fills a real uncovered polygon.
                if (tolerance > 0)
                {
                    double boundary = GapWindowArea(projection, gap, 0, tolerance, ref arithmeticOperations)
                        + GapWindowArea(projection, gap, Math.Max(0, maxGap - tolerance), maxGap + tolerance,
                            ref arithmeticOperations);
                    if (!Finite(boundary)) return Refuse("numeric_overflow");
                    gapBoundaryArea += boundary;
                }

                // A separate relaxed union is evidence about possible whole cover, not added nominal coverage.
                // The strictly positive gap guard excludes constant exact-zero contact from both unions.
                if (possibleRemaining.Count > 0 && projection.Max(gap.At) > 0
                    && projection.Min(gap.At) <= maxGap + tolerance)
                {
                    var possiblePlanes = new List<HalfPlane>(planes)
                    {
                        new(gap.X, gap.Y, gap.C),
                        new(-gap.X, -gap.Y, maxGap + tolerance - gap.C)
                    };
                    var withinPossible = dependentTriangle.Polygon;
                    foreach (var plane in possiblePlanes)
                    {
                        withinPossible = Clip(withinPossible, plane, true, ref arithmeticOperations);
                        if (withinPossible.Count < 3) break;
                    }
                    if (!Valid(withinPossible)) return Refuse("numeric_overflow");
                    if (Area(withinPossible) > 0)
                    {
                        sensitivityBoundary += Perimeter(withinPossible);
                        coordinateScale = Math.Max(coordinateScale, Scale(withinPossible));
                        var refusal = Subtract(ref possibleRemaining, possiblePlanes, gap, false, remaining.Count);
                        if (refusal is not null) return Refuse(refusal);
                    }
                }
                // Strictly above the tolerance. A constant gap exactly on the lower boundary must not become cover.
                if (remaining.Count == 0) continue;
                if (projection.Max(gap.At) <= tolerance) continue;
                if (projection.Min(gap.At) > maxGap) continue;

                planes.Add(new(gap.X, gap.Y, gap.C - tolerance));
                planes.Add(new(-gap.X, -gap.Y, maxGap - gap.C));
                var withinDependent = dependentTriangle.Polygon;
                foreach (var plane in planes)
                {
                    withinDependent = Clip(withinDependent, plane, true, ref arithmeticOperations);
                    if (withinDependent.Count < 3) break;
                }
                if (!Valid(withinDependent)) return Refuse("numeric_overflow");
                if (Area(withinDependent) == 0) continue;
                sensitivityBoundary += Perimeter(withinDependent);
                coordinateScale = Math.Max(coordinateScale, Scale(withinDependent));

                var nominalRefusal = Subtract(ref remaining, planes, gap, true, possibleRemaining.Count);
                if (nominalRefusal is not null) return Refuse(nominalRefusal);
            }
            foreach (var polygon in remaining)
            {
                uncovered += Area(polygon);
                sensitivityBoundary += Perimeter(polygon);
            }
            foreach (var polygon in possibleRemaining)
            {
                possibleUncovered += Area(polygon);
                sensitivityBoundary += Perimeter(polygon);
            }
        }

        if (!Finite(uncovered) || !Finite(possibleUncovered) || !Finite(sensitivityBoundary)) return Refuse("numeric_overflow");
        // Sensitivity to the caller's coordinate tolerance plus a double-arithmetic estimate. This is deliberately
        // not a certified enclosure: every value and the nominal whole-cover label remain a labelled approximation.
        const double machineEpsilon = 2.2204460492503131e-16;
        double arithmeticUncertainty = 2 * tolerance * sensitivityBoundary
            + 64 * machineEpsilon * arithmeticOperations * coordinateScale * coordinateScale;
        double uncertainty = Math.Min(area, gapBoundaryArea) + arithmeticUncertainty;
        if (!Finite(uncertainty) || !Finite(arithmeticUncertainty)) return Refuse("numeric_overflow");
        if (uncovered > area + uncertainty || possibleUncovered > area + arithmeticUncertainty
            || possibleUncovered > uncovered + arithmeticUncertainty) return Refuse("area_accounting");
        double covered = Math.Clamp(area - uncovered, 0, area);
        bool possibleWhole = area > 0 && possibleUncovered <= arithmeticUncertainty;
        string reading = area > 0 && uncovered == 0 && gapBoundaryArea == 0 ? "nominal_whole_cover"
                       : possibleWhole ? "numerically_uncertain" : "partial";
        return new DirectionalCoverageResult(area, covered, uncovered, minGap, maxMeasuredGap, true, null,
            uncertainty, reading, pairs, peakPieces)
        {
            UncertaintyReason = gapBoundaryArea > 0 ? "gap_boundary_sensitivity" : null,
            PossibleUncoveredArea = possibleUncovered,
            ArithmeticAreaUncertainty = arithmeticUncertainty
        };
    }

    readonly record struct DependentTriangle(P3 Origin, List<P2> Polygon, Height Height);
    // Generated intersections retain their generating line. Reading that same line (either half-plane sense)
    // as zero prevents a triangulation seam acquiring a fictitious strip from a rounded intersection coordinate.
    // This records algebraic boundary provenance; it never snaps nearby, unrelated coordinates or deletes a fragment.
    readonly record struct P2(double X, double Y, HalfPlane? Boundary0 = null, HalfPlane? Boundary1 = null)
    {
        public bool On(HalfPlane plane) => Boundary0 is { } a && a.SameLine(plane)
                                           || Boundary1 is { } b && b.SameLine(plane);
        public static P2 operator +(P2 a, P2 b) => new(a.X + b.X, a.Y + b.Y);
        public static P2 operator -(P2 a, P2 b) => new(a.X - b.X, a.Y - b.Y);
        public static P2 operator *(P2 a, double b) => new(a.X * b, a.Y * b);
    }
    readonly record struct P3(double X, double Y, double Z)
    {
        public P3(Vector3 v) : this(v.X, v.Y, v.Z) { }
        public bool IsFinite => Finite(X) && Finite(Y) && Finite(Z);
        public double LengthSquared => X * X + Y * Y + Z * Z;
        public static P3 operator -(P3 a, P3 b) => new(a.X - b.X, a.Y - b.Y, a.Z - b.Z);
        public static P3 operator /(P3 a, double b) => new(a.X / b, a.Y / b, a.Z / b);
        public static double Dot(P3 a, P3 b) => a.X * b.X + a.Y * b.Y + a.Z * b.Z;
        public static P3 Cross(P3 a, P3 b) => new(a.Y * b.Z - a.Z * b.Y, a.Z * b.X - a.X * b.Z, a.X * b.Y - a.Y * b.X);
    }
    readonly record struct Height(double X, double Y, double C)
    {
        public bool IsFinite => Finite(X) && Finite(Y) && Finite(C);
        public double At(P2 p) => X * p.X + Y * p.Y + C;
        public static Height operator -(Height a, Height b) => new(a.X - b.X, a.Y - b.Y, a.C - b.C);
    }
    readonly record struct HalfPlane(double X, double Y, double C)
    {
        public bool SameLine(HalfPlane other) => X == other.X && Y == other.Y && C == other.C
                                                || X == -other.X && Y == -other.Y && C == -other.C;
        public double At(P2 p) => p.On(this) ? 0 : X * p.X + Y * p.Y + C;
    }
    static bool Finite(double x) => double.IsFinite(x);
    static P2 Project(P3 p, P3 u, P3 v) => new(P3.Dot(p, u), P3.Dot(p, v));
    static Height HeightOf(P3 n, P3 point, P3 direction, P3 u, P3 v)
    {
        double along = P3.Dot(n, direction);
        return new(-P3.Dot(n, u) / along, -P3.Dot(n, v) / along, P3.Dot(n, point) / along);
    }
    static double Cross(P2 a, P2 b) => a.X * b.Y - a.Y * b.X;
    static double Area(List<P2> polygon)
    {
        if (polygon.Count < 3) return 0;
        double twice = 0;
        for (int i = 1; i + 1 < polygon.Count; i++)
            twice += Cross(polygon[i] - polygon[0], polygon[i + 1] - polygon[0]);
        return .5 * Math.Abs(twice);
    }
    static double Perimeter(List<P2> polygon)
    {
        if (polygon.Count < 2) return 0;
        double length = 0;
        for (int i = 0; i < polygon.Count; i++)
        {
            var edge = polygon[(i + 1) % polygon.Count] - polygon[i];
            length += Math.Sqrt(edge.X * edge.X + edge.Y * edge.Y);
        }
        return length;
    }
    static double Scale(List<P2> polygon) => polygon.Count == 0 ? 0 : polygon.Max(p => Math.Max(Math.Abs(p.X), Math.Abs(p.Y)));
    static bool Valid(List<P2> polygon) => polygon.All(p => Finite(p.X) && Finite(p.Y)) && Finite(Area(polygon));
    static bool BoxesMeet(List<P2> a, List<P2> b) =>
        a.Min(p => p.X) <= b.Max(p => p.X) && a.Max(p => p.X) >= b.Min(p => p.X)
        && a.Min(p => p.Y) <= b.Max(p => p.Y) && a.Max(p => p.Y) >= b.Min(p => p.Y);
    static List<HalfPlane> EdgePlanes(List<P2> polygon, int sign)
    {
        var planes = new List<HalfPlane>(5);
        for (int i = 0; i < 3; i++)
        {
            var a = polygon[i]; var b = polygon[(i + 1) % 3]; var edge = b - a;
            double length = Math.Sqrt(edge.X * edge.X + edge.Y * edge.Y);
            // Endpoint cross is antisymmetric even in double arithmetic. A shared edge in reverse order therefore
            // presents exactly the inverse coefficients, so its generated cut points retain the shared boundary.
            planes.Add(new(-sign * edge.Y / length, sign * edge.X / length,
                sign * Cross(a, b) / length));
        }
        return planes;
    }
    // A positive-gap boundary region on the actual projected intersection, including affine (sloped) gaps.
    // A constant exact-zero gap is excluded, rather than changing the established contact boundary.
    static double GapWindowArea(List<P2> projection, Height gap, double lo, double hi, ref long operations)
    {
        double min = projection.Min(gap.At), max = projection.Max(gap.At);
        if (max <= 0 || max < lo || min > hi) return 0;
        var within = Clip(projection, new(gap.X, gap.Y, gap.C - lo), true, ref operations);
        within = Clip(within, new(-gap.X, -gap.Y, hi - gap.C), true, ref operations);
        return Valid(within) ? Area(within) : double.NaN;
    }
    // Sutherland–Hodgman. Outside is strict, so a polygon lying exactly on a cut plane is not duplicated.
    static List<P2> Clip(List<P2> polygon, HalfPlane plane, bool keepInside, ref long operations)
    {
        var output = new List<P2>(polygon.Count + 1);
        for (int i = 0; i < polygon.Count; i++)
        {
            var current = polygon[i]; var next = polygon[(i + 1) % polygon.Count];
            double dc = plane.At(current), dn = plane.At(next);
            operations += 8;
            bool currentIn = keepInside ? dc >= 0 : dc < 0;
            bool nextIn = keepInside ? dn >= 0 : dn < 0;
            if (currentIn) output.Add(current);
            if (currentIn != nextIn)
            {
                double fraction = dc / (dc - dn);
                var cut = fraction == 0 ? current : fraction == 1 ? next : current + (next - current) * fraction;
                HalfPlane? shared = null;
                if (fraction == 0)
                    shared = current.Boundary0 is { } ca && !ca.SameLine(plane) ? ca : current.Boundary1;
                else if (fraction == 1)
                    shared = next.Boundary0 is { } na && !na.SameLine(plane) ? na : next.Boundary1;
                else if (current.Boundary0 is { } first && next.On(first)) shared = first;
                else if (current.Boundary1 is { } second && next.On(second)) shared = second;
                output.Add(new(cut.X, cut.Y, plane, shared));
            }
        }
        return output;
    }
}
