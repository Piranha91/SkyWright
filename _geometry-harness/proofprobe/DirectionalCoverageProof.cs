using System.Numerics;
using HousecarlCore;
using SkyWright.Engine;

static class DirectionalCoverageProof
{
    static TriangleSoup Triangle(Vector3 a, Vector3 b, Vector3 c) =>
        new(new[] { a, b, c }, new[] { 0, 1, 2 });

    static TriangleSoup Combine(params TriangleSoup[] soups)
    {
        var vertices = new List<Vector3>(); var indices = new List<int>();
        foreach (var soup in soups)
        {
            int offset = vertices.Count;
            vertices.AddRange(soup.Vertices);
            indices.AddRange(soup.Indices.Select(i => i + offset));
        }
        return new(vertices.ToArray(), indices.ToArray());
    }

    // Counterclockwise viewed from +Z. The two triangles meet along a shared interior diagonal.
    static TriangleSoup Rectangle(float x0, float y0, float x1, float y1, float z) =>
        Combine(Triangle(new(x0, y0, z), new(x1, y0, z), new(x1, y1, z)),
                Triangle(new(x0, y0, z), new(x1, y1, z), new(x0, y1, z)));

    static TriangleSoup Transform(TriangleSoup soup, Matrix4x4 matrix) =>
        new(soup.Vertices.Select(v => Vector3.Transform(v, matrix)).ToArray(), (int[])soup.Indices.Clone());

    static bool Near(double value, double expected, double tolerance = 1e-4) => Math.Abs(value - expected) <= tolerance;
    static string Show(DirectionalCoverageResult r) =>
        $"area={r.ProjectedArea:R} covered={r.CoveredArea:R} uncovered={r.UncoveredArea:R} "
        + $"gap={r.MinGap:R}..{r.MaxGap:R} full={r.FullCoverReading} uncertainty={r.AreaUncertainty:R} "
        + $"complete={r.Complete} refusal={r.RefusalReason} pairs={r.TrianglePairs} pieces={r.Pieces}";

    public static void Run(Action<string, bool, string> check)
    {
        var dependent = Triangle(new(-2, -2, 0), new(2, -2, 0), new(0, 2, 0)); // 8 square units.
        var wholeFloor = Rectangle(-3, -3, 3, 3, 1.5f);
        var whole = DirectionalCoverage.Measure(dependent, TriangleGrid.Build(wholeFloor), Vector3.UnitZ, 4);
        check("SC-1: whole projected face covered through a triangulation seam",
            whole.Complete && Near(whole.ProjectedArea, 8) && Near(whole.CoveredArea, 8)
            && whole.UncoveredArea == 0 && whole.FullCoverReading == "nominal_whole_cover"
            && whole.MinGap is { } min && Near(min, 1.5) && whole.MaxGap is { } max && Near(max, 1.5)
            && !whole.Certified, Show(whole));

        var repeat = DirectionalCoverage.Measure(dependent, TriangleGrid.Build(wholeFloor), Vector3.UnitZ, 4);
        check("SC-2: unchanged covering face measured twice has identical evidence",
            repeat == whole && repeat.CoveredArea > 0, Show(repeat));

        var high = DirectionalCoverage.Measure(dependent, TriangleGrid.Build(Rectangle(-3, -3, 3, 3, 12)), Vector3.UnitZ, 4);
        check("SC-3: overhead shelf outside the shallow band contributes no cover",
            high.Complete && high.CoveredArea == 0 && Near(high.UncoveredArea, 8)
            && high.FullCoverReading == "partial" && high.MinGap is null, Show(high));

        var partial = DirectionalCoverage.Measure(dependent, TriangleGrid.Build(Rectangle(-3, -3, 0, 3, 1.5f)), Vector3.UnitZ, 4);
        check("SC-4: incomplete cover preserves the uncovered half",
            partial.Complete && Near(partial.CoveredArea, 4) && Near(partial.UncoveredArea, 4)
            && partial.FullCoverReading == "partial", Show(partial));

        var ring = Combine(Rectangle(-3, -3, 3, -.2f, 1.5f), Rectangle(-3, .2f, 3, 3, 1.5f),
            Rectangle(-3, -.2f, -.2f, .2f, 1.5f), Rectangle(.2f, -.2f, 3, .2f, 1.5f));
        var hole = DirectionalCoverage.Measure(dependent, TriangleGrid.Build(ring), Vector3.UnitZ, 4);
        check("SC-5: central hole retains its positive uncovered area",
            hole.Complete && Near(hole.CoveredArea, 7.84) && Near(hole.UncoveredArea, .16)
            && hole.FullCoverReading == "partial", Show(hole));

        var columns = MeshColumnIndex.Build(ring.Vertices, ring.Indices);
        bool allVerticesCovered = dependent.Vertices.All(v =>
        {
            var surfaces = new List<(float Z, bool Up)>(); columns.SurfacesAt(v.X, v.Y, surfaces);
            return surfaces.Any(s => s.Up && s.Z > 0 && s.Z <= 4);
        });
        check("SC-6: all vertices covered does not conceal an interior hole",
            allVerticesCovered && hole.Complete && hole.UncoveredArea > .15 && hole.FullCoverReading == "partial",
            $"vertices={allVerticesCovered}; {Show(hole)}");

        var duplicates = DirectionalCoverage.Measure(dependent, TriangleGrid.Build(Combine(wholeFloor, wholeFloor)), Vector3.UnitZ, 4);
        check("SC-7: duplicate and overlapping faces do not double-count coverage",
            duplicates.Complete && Near(duplicates.CoveredArea, 8) && duplicates.CoveredArea <= duplicates.ProjectedArea
            && duplicates.UncoveredArea == 0 && duplicates.FullCoverReading == "nominal_whole_cover", Show(duplicates));

        var wall = Triangle(new(0, -3, 1), new(0, 3, 1), new(0, 0, 3));
        var edge = DirectionalCoverage.Measure(dependent, TriangleGrid.Build(wall), Vector3.UnitZ, 4);
        check("SC-8: a host face seen edge-on has no directional coverage",
            edge.Complete && edge.CoveredArea == 0 && Near(edge.UncoveredArea, 8), Show(edge));

        var limitedPairs = DirectionalCoverage.Measure(dependent, TriangleGrid.Build(wholeFloor), Vector3.UnitZ, 4, maxTrianglePairs: 0);
        check("SC-9: triangle-pair budget refuses instead of inventing no cover",
            !limitedPairs.Complete && limitedPairs.RefusalReason == "triangle_pair_budget"
            && limitedPairs.FullCoverReading == "incomplete", Show(limitedPairs));

        var limitedPieces = DirectionalCoverage.Measure(dependent, TriangleGrid.Build(ring), Vector3.UnitZ, 4, maxPieces: 1);
        check("SC-10: polygon-piece budget refuses without dropping fragments",
            !limitedPieces.Complete && limitedPieces.RefusalReason == "polygon_piece_budget"
            && limitedPieces.FullCoverReading == "incomplete", Show(limitedPieces));

        var zero = DirectionalCoverage.Measure(dependent, TriangleGrid.Build(Rectangle(-3, -3, 3, 3, 0)), Vector3.UnitZ, 4);
        check("SC-11: zero separation is contact rather than above-face coverage",
            zero.Complete && zero.CoveredArea == 0 && Near(zero.UncoveredArea, 8) && zero.MinGap is null, Show(zero));

        var rotated = Matrix4x4.CreateRotationY(MathF.PI / 2);
        var lateral = DirectionalCoverage.Measure(Transform(dependent, rotated), TriangleGrid.Build(Transform(wholeFloor, rotated)), Vector3.UnitX, 4);
        check("SC-12: the direction parameter is general rather than hard-coded vertical",
            lateral.Complete && Near(lateral.CoveredArea, 8) && lateral.UncoveredArea == 0
            && lateral.FullCoverReading == "nominal_whole_cover", Show(lateral));

        var boundaryOnly = DirectionalCoverage.Measure(dependent, TriangleGrid.Build(Rectangle(-3, -3, 3, -2, 1.5f)), Vector3.UnitZ, 4);
        check("SC-13: projected edge-only contact contributes zero area",
            boundaryOnly.Complete && boundaryOnly.CoveredArea == 0 && Near(boundaryOnly.UncoveredArea, 8), Show(boundaryOnly));

        using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
        var cancelled = DirectionalCoverage.Measure(dependent, TriangleGrid.Build(wholeFloor), Vector3.UnitZ, 4, cancellationToken: cancellation.Token);
        check("SC-14: cancellation remains a named incomplete measurement",
            !cancelled.Complete && cancelled.RefusalReason == "cancelled" && cancelled.FullCoverReading == "incomplete", Show(cancelled));

        // A small real hole must survive. Its dimensions are deliberately near the requested arithmetic tolerance.
        var tinyRing = Combine(Rectangle(-3, -3, 3, -0.000001f, 1.5f), Rectangle(-3, 0.000001f, 3, 3, 1.5f),
            Rectangle(-3, -0.000001f, -0.000001f, 0.000001f, 1.5f), Rectangle(0.000001f, -0.000001f, 3, 0.000001f, 1.5f));
        var tiny = DirectionalCoverage.Measure(dependent, TriangleGrid.Build(tinyRing), Vector3.UnitZ, 4);
        check("SC-15: tiny positive uncovered fragments are retained as numerical uncertainty",
            tiny.Complete && tiny.UncoveredArea > 0 && tiny.FullCoverReading == "numerically_uncertain"
            && tiny.AreaUncertainty > tiny.UncoveredArea && !tiny.Certified, Show(tiny));

        const double gapTolerance = 1e-5;
        var upperBoundary = new[] { 4f - .000005f, 4f, 4f + .000005f }
            .Select(z => DirectionalCoverage.Measure(dependent, TriangleGrid.Build(Rectangle(-3, -3, 3, 3, z)),
                Vector3.UnitZ, 4, tolerance: gapTolerance)).ToArray();
        check("SC-16: the upper shallow-band boundary has a numerical uncertainty reading",
            upperBoundary.All(r => r.Complete && r.FullCoverReading == "numerically_uncertain"
                && r.AreaUncertainty > 0 && !r.Certified), string.Join("; ", upperBoundary.Select(Show)));

        var nearZero = DirectionalCoverage.Measure(dependent,
            TriangleGrid.Build(Rectangle(-3, -3, 3, 3, .000005f)), Vector3.UnitZ, 4, tolerance: gapTolerance);
        check("SC-17: a near-zero positive gap is uncertain rather than silently no cover",
            nearZero.Complete && nearZero.FullCoverReading == "numerically_uncertain"
            && nearZero.AreaUncertainty > 0 && !nearZero.Certified, Show(nearZero));

        var folded = Combine(dependent, Triangle(new(-2, -2, 2), new(2, -2, 2), new(0, 2, 2)));
        var upperExposed = DirectionalCoverage.Measure(folded, TriangleGrid.Build(wholeFloor), Vector3.UnitZ, 4);
        check("SC-18: covering a lower triangle does not hide an exposed upper fold",
            upperExposed.Complete && Near(upperExposed.ProjectedArea, 16)
            && Near(upperExposed.CoveredArea, 8) && Near(upperExposed.UncoveredArea, 8)
            && upperExposed.FullCoverReading == "partial", Show(upperExposed));

        var halfUpper = DirectionalCoverage.Measure(dependent,
            TriangleGrid.Build(Rectangle(-3, -3, 0, 3, 4f - .000005f)), Vector3.UnitZ, 4);
        check("SC-19: upper-band sensitivity cannot fill an absent half footprint",
            halfUpper.Complete && Near(halfUpper.CoveredArea, 4) && Near(halfUpper.UncoveredArea, 4)
            && halfUpper.FullCoverReading == "partial"
            && halfUpper.UncertaintyReason == "gap_boundary_sensitivity", Show(halfUpper));

        var sloping = Rectangle(-3, -3, 3, 3, 0);
        sloping = new(sloping.Vertices.Select(v => new Vector3(v.X, v.Y, .5f * v.X)).ToArray(), sloping.Indices);
        var acrossZero = DirectionalCoverage.Measure(dependent, TriangleGrid.Build(sloping), Vector3.UnitZ, 4);
        check("SC-20: a gap crossing zero keeps the exposed side partial",
            acrossZero.Complete && Near(acrossZero.CoveredArea, 4) && Near(acrossZero.UncoveredArea, 4)
            && acrossZero.FullCoverReading == "partial"
            && acrossZero.UncertaintyReason == "gap_boundary_sensitivity", Show(acrossZero));

        var halfOutside = DirectionalCoverage.Measure(dependent,
            TriangleGrid.Build(Rectangle(-3, -3, 0, 3, 4f + .000005f)), Vector3.UnitZ, 4);
        check("SC-21: relaxed upper-band cover still leaves a fixed exposed half",
            halfOutside.Complete && halfOutside.CoveredArea == 0 && Near(halfOutside.UncoveredArea, 8)
            && halfOutside.FullCoverReading == "partial"
            && halfOutside.UncertaintyReason == "gap_boundary_sensitivity", Show(halfOutside));

        var halfZero = DirectionalCoverage.Measure(dependent,
            TriangleGrid.Build(Rectangle(-3, -3, 0, 3, .000005f)), Vector3.UnitZ, 4);
        check("SC-22: near-zero half footprint cannot become whole cover",
            halfZero.Complete && halfZero.CoveredArea == 0 && Near(halfZero.UncoveredArea, 8)
            && halfZero.FullCoverReading == "partial"
            && halfZero.UncertaintyReason == "gap_boundary_sensitivity", Show(halfZero));
    }
}
