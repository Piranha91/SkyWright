using System.Numerics;
using HousecarlCore;

static class MeshIntersectionProof
{
    readonly record struct P2(int X, int Y);

    static TriangleSoup Soup(Vector3[] vertices, params int[] indices) => new(vertices, indices);

    static TriangleSoup Triangle(Vector3 a, Vector3 b, Vector3 c) =>
        Soup(new[] { a, b, c }, 0, 1, 2);

    static TriangleSoup Reverse(TriangleSoup soup)
    {
        var indices = (int[])soup.Indices.Clone();
        for (var i = 0; i + 2 < indices.Length; i += 3)
            (indices[i + 1], indices[i + 2]) = (indices[i + 2], indices[i + 1]);
        return new((Vector3[])soup.Vertices.Clone(), indices,
                   soup.TriangleMaterials is null ? null : (string?[])soup.TriangleMaterials.Clone());
    }

    static TriangleSoup Transform(TriangleSoup soup, Matrix4x4 transform) =>
        new(soup.Vertices.Select(v => Vector3.Transform(v, transform)).ToArray(),
            (int[])soup.Indices.Clone(),
            soup.TriangleMaterials is null ? null : (string?[])soup.TriangleMaterials.Clone());

    static TriangleSoup Combine(params TriangleSoup[] soups)
    {
        var vertices = new List<Vector3>();
        var indices = new List<int>();
        foreach (var soup in soups)
        {
            var offset = vertices.Count;
            vertices.AddRange(soup.Vertices);
            indices.AddRange(soup.Indices.Select(i => i + offset));
        }
        return new(vertices.ToArray(), indices.ToArray());
    }

    static TriangleSoup Repeat(TriangleSoup soup, int count) =>
        Combine(Enumerable.Repeat(soup, count).ToArray());

    static TriangleSoup Box(Vector3 min, Vector3 max)
    {
        var v = new[]
        {
            new Vector3(min.X, min.Y, min.Z), new Vector3(max.X, min.Y, min.Z),
            new Vector3(max.X, max.Y, min.Z), new Vector3(min.X, max.Y, min.Z),
            new Vector3(min.X, min.Y, max.Z), new Vector3(max.X, min.Y, max.Z),
            new Vector3(max.X, max.Y, max.Z), new Vector3(min.X, max.Y, max.Z)
        };
        // Each geometric edge occurs exactly twice. Winding is deliberately irrelevant to the proof.
        return Soup(v,
            0, 2, 1, 0, 3, 2, 4, 5, 6, 4, 6, 7,
            0, 1, 5, 0, 5, 4, 1, 2, 6, 1, 6, 5,
            2, 3, 7, 2, 7, 6, 3, 0, 4, 3, 4, 7);
    }

    static TriangleSoup Tetra(Vector3 origin, float size)
    {
        var v = new[]
        {
            origin,
            origin + new Vector3(size, 0, 0),
            origin + new Vector3(0, size, 0),
            origin + new Vector3(0, 0, size)
        };
        return Soup(v, 0, 2, 1, 0, 1, 3, 0, 3, 2, 1, 2, 3);
    }

    // A closed square pyramid on the x axis: base square at x = baseX (half-width half), apex at (apexX, 0, 0); consistently wound.
    static TriangleSoup Pyramid(float baseX, float half, float apexX)
    {
        var v = new[]
        {
            new Vector3(baseX, -half, -half), new Vector3(baseX, half, -half),
            new Vector3(baseX, half, half), new Vector3(baseX, -half, half),
            new Vector3(apexX, 0, 0)
        };
        return Soup(v, 0, 1, 2, 0, 2, 3, 1, 0, 4, 2, 1, 4, 3, 2, 4, 0, 3, 4);
    }

    // A closed n-sided prism along z (radius 1, height 2): n bottom-fan, n top-fan and 2n side triangles; consistently wound.
    static TriangleSoup Prism(int n)
    {
        var v = new List<Vector3>();
        for (int k = 0; k < n; k++) v.Add(new(MathF.Cos(2 * MathF.PI * k / n), MathF.Sin(2 * MathF.PI * k / n), 0));
        for (int k = 0; k < n; k++) v.Add(new(MathF.Cos(2 * MathF.PI * k / n), MathF.Sin(2 * MathF.PI * k / n), 2));
        int cb = v.Count; v.Add(new(0, 0, 0));
        int ct = v.Count; v.Add(new(0, 0, 2));
        var idx = new List<int>();
        for (int k = 0; k < n; k++)
        {
            int b0 = k, b1 = (k + 1) % n, t0 = n + k, t1 = n + (k + 1) % n;
            idx.AddRange(new[] { cb, b1, b0, ct, t0, t1, b0, b1, t1, b0, t1, t0 });
        }
        return Soup(v.ToArray(), idx.ToArray());
    }

    static TriangleSoup Frame()
    {
        static TriangleSoup Quad(float x0, float y0, float x1, float y1) =>
            Soup(new[] { new Vector3(x0, y0, 0), new Vector3(x1, y0, 0),
                         new Vector3(x1, y1, 0), new Vector3(x0, y1, 0) },
                 0, 1, 2, 0, 2, 3);
        return Combine(Quad(-3, -3, 3, -1), Quad(-3, 1, 3, 3),
                       Quad(-3, -1, -1, 1), Quad(1, -1, 3, 1));
    }

    static string Norm(object? value) =>
        (value?.ToString() ?? "").Trim().ToLowerInvariant().Replace('-', '_').Replace(' ', '_');

    static string Bits(float value) =>
        unchecked((uint)BitConverter.SingleToInt32Bits(value)).ToString("X8");

    static Vector3 HexVector(string x, string y, string z) => new(
        BitConverter.Int32BitsToSingle(unchecked((int)Convert.ToUInt32(x, 16))),
        BitConverter.Int32BitsToSingle(unchecked((int)Convert.ToUInt32(y, 16))),
        BitConverter.Int32BitsToSingle(unchecked((int)Convert.ToUInt32(z, 16))));

    static string SwapContainment(string value) => value switch
    {
        "a_inside_b" => "b_inside_a",
        "b_inside_a" => "a_inside_b",
        _ => value
    };

    static long Orient(P2 a, P2 b, P2 c) =>
        (long)(b.X - a.X) * (c.Y - a.Y) - (long)(b.Y - a.Y) * (c.X - a.X);

    static (P2 A, P2 B, P2 C) Tri2(P2 a, P2 b, P2 c) => (a, b, c);

    static bool Nondegenerate((P2 A, P2 B, P2 C) t) => Orient(t.A, t.B, t.C) != 0;

    static bool OnSegment(P2 a, P2 b, P2 p) => Orient(a, b, p) == 0
        && p.X >= Math.Min(a.X, b.X) && p.X <= Math.Max(a.X, b.X)
        && p.Y >= Math.Min(a.Y, b.Y) && p.Y <= Math.Max(a.Y, b.Y);

    static bool SegmentsMeet(P2 a, P2 b, P2 c, P2 d)
    {
        var abC = Orient(a, b, c); var abD = Orient(a, b, d);
        var cdA = Orient(c, d, a); var cdB = Orient(c, d, b);
        if ((abC > 0 && abD < 0 || abC < 0 && abD > 0)
            && (cdA > 0 && cdB < 0 || cdA < 0 && cdB > 0)) return true;
        return abC == 0 && OnSegment(a, b, c) || abD == 0 && OnSegment(a, b, d)
            || cdA == 0 && OnSegment(c, d, a) || cdB == 0 && OnSegment(c, d, b);
    }

    static bool PointInOrOn((P2 A, P2 B, P2 C) t, P2 p)
    {
        var a = Orient(t.A, t.B, p); var b = Orient(t.B, t.C, p); var c = Orient(t.C, t.A, p);
        return a >= 0 && b >= 0 && c >= 0 || a <= 0 && b <= 0 && c <= 0;
    }

    static bool PositiveProjectionOverlap((P2 A, P2 B, P2 C) a,
                                          (P2 A, P2 B, P2 C) b, P2 p, P2 q)
    {
        var nx = -(q.Y - p.Y); var ny = q.X - p.X;
        static long Dot(P2 v, int x, int y) => (long)v.X * x + (long)v.Y * y;
        var amin = Math.Min(Dot(a.A, nx, ny), Math.Min(Dot(a.B, nx, ny), Dot(a.C, nx, ny)));
        var amax = Math.Max(Dot(a.A, nx, ny), Math.Max(Dot(a.B, nx, ny), Dot(a.C, nx, ny)));
        var bmin = Math.Min(Dot(b.A, nx, ny), Math.Min(Dot(b.B, nx, ny), Dot(b.C, nx, ny)));
        var bmax = Math.Max(Dot(b.A, nx, ny), Math.Max(Dot(b.B, nx, ny), Dot(b.C, nx, ny)));
        return Math.Min(amax, bmax) > Math.Max(amin, bmin);
    }

    // Exact integer convex-SAT oracle. Positive overlap on every edge normal means positive area.
    // If that fails, inclusive edge/point tests distinguish boundary touch from separation.
    static string CoplanarOracle((P2 A, P2 B, P2 C) a, (P2 A, P2 B, P2 C) b)
    {
        var ae = new[] { (a.A, a.B), (a.B, a.C), (a.C, a.A) };
        var be = new[] { (b.A, b.B), (b.B, b.C), (b.C, b.A) };
        if (ae.Concat(be).All(e => PositiveProjectionOverlap(a, b, e.Item1, e.Item2)))
            return "coplanar_overlap";
        if (ae.Any(x => be.Any(y => SegmentsMeet(x.Item1, x.Item2, y.Item1, y.Item2)))
            || PointInOrOn(a, b.A) || PointInOrOn(b, a.A)) return "touching";
        return "separated";
    }

    static (double Min, double Max, bool Interior)? LineInterval((P2 A, P2 B, P2 C) t, int ordinate)
    {
        var p = new[] { t.A, t.B, t.C };
        var xs = new List<double>();
        for (var i = 0; i < 3; i++)
        {
            var a = p[i]; var b = p[(i + 1) % 3];
            if (a.Y == ordinate) xs.Add(a.X);
            if ((a.Y < ordinate && b.Y > ordinate) || (a.Y > ordinate && b.Y < ordinate))
                xs.Add(a.X + (double)(ordinate - a.Y) * (b.X - a.X) / (b.Y - a.Y));
        }
        if (xs.Count == 0) return null;
        var min = xs.Min(); var max = xs.Max();
        return (min, max, ordinate > p.Min(x => x.Y) && ordinate < p.Max(x => x.Y));
    }

    // A is XY at z=0. B is XZ, placed in the plane y=ordinate. Their common line is x at
    // y=ordinate,z=0, so exact triangle/line intervals supply the independent answer.
    static string OrthogonalOracle((P2 A, P2 B, P2 C) xy,
                                   (P2 A, P2 B, P2 C) xz, int ordinate)
    {
        var a = LineInterval(xy, ordinate);
        var b = LineInterval(xz, 0);
        if (a is null || b is null) return "separated";
        var lo = Math.Max(a.Value.Min, b.Value.Min);
        var hi = Math.Min(a.Value.Max, b.Value.Max);
        if (hi < lo - 1e-12) return "separated";
        if (hi > lo + 1e-12 && a.Value.Interior && b.Value.Interior) return "crossing";
        return "touching";
    }

    static TriangleSoup Xy((P2 A, P2 B, P2 C) t) => Triangle(
        new(t.A.X, t.A.Y, 0), new(t.B.X, t.B.Y, 0), new(t.C.X, t.C.Y, 0));

    static TriangleSoup Xz((P2 A, P2 B, P2 C) t, int y) => Triangle(
        new(t.A.X, y, t.A.Y), new(t.B.X, y, t.B.Y), new(t.C.X, y, t.C.Y));

    public static void Run(Action<string, bool, string> check)
    {
        var policies = new HashSet<string>(StringComparer.Ordinal);

        void Expect(string name, TriangleSoup a, TriangleSoup b, string surface,
                    string containment, bool? intersects, double tolerance = 1e-5,
                    long budget = 2_000_000)
        {
            try
            {
                var result = MeshIntersection.Query(a, b, tolerance, budget);
                var actualSurface = Norm(result.SurfaceRelation);
                var actualContainment = Norm(result.Containment);
                var policy = result.PolicyVersion ?? "";
                policies.Add(policy);
                var expectedStatus = surface == "unknown" ? "unknown" : "ok";
                var reasonOk = expectedStatus == "unknown"
                    ? !string.IsNullOrWhiteSpace(result.Reason)
                    : containment == "not_applicable_open_mesh"
                        ? !string.IsNullOrWhiteSpace(result.Reason)
                        : string.IsNullOrWhiteSpace(result.Reason);
                check(name,
                      actualSurface == surface && actualContainment == containment
                      && result.Intersects == intersects && result.TrianglePairsTested <= budget
                      && result.TrianglePairsTested >= 0 && Norm(result.Status) == expectedStatus
                      && reasonOk && !string.IsNullOrWhiteSpace(policy),
                      $"surface={actualSurface} containment={actualContainment} intersects={result.Intersects} " +
                      $"status={result.Status} reason={result.Reason} pairs={result.TrianglePairsTested} policy={policy}");
            }
            catch (Exception ex)
            {
                check(name, false, $"threw {ex.GetType().Name}: {ex.Message}");
            }
        }

        var lower = Triangle(new(0, 0, 0), new(4, 0, 0), new(0, 4, 0));
        var upper = Triangle(new(4, 4, 0), new(4, 1, 0), new(1, 4, 0));
        Expect("mesh: overlapping boxes do not turn disjoint triangles into contact",
               lower, upper, "separated", "not_applicable_open_mesh", false);

        var center = Triangle(new(-.5f, -.5f, 0), new(.5f, -.5f, 0), new(0, .5f, 0));
        Expect("mesh: an open frame's empty aperture remains empty despite overlapping bounds",
               Frame(), center, "separated", "not_applicable_open_mesh", false);

        var outerBox = Box(new(-3), new(3));
        var innerBox = Box(new(-.5f), new(.5f));
        Expect("mesh: nested closed box surfaces are separated and containment is named",
               innerBox, outerBox, "separated", "a_inside_b", true);

        var tinyA = Triangle(new(0, 0, 0), new(.0001f, 0, 0), new(0, .0001f, 0));
        var tinyB = Triangle(new(.000025f, .000025f, -.00005f), new(.000025f, .000025f, .00005f),
                             new(.000075f, .000025f, 0));
        Expect("mesh: tiny real crossing survives former size and depth gates",
               tinyA, tinyB, "crossing", "unassessed", true);

        var shallowA = Triangle(new(-2, -2, 0), new(2, -2, 0), new(0, 2, 0));
        var shallowB = Triangle(new(-1, 0, -.00002f), new(1, 0, .00002f), new(0, 1, .00002f));
        Expect("mesh: shallow transverse crossing remains a crossing",
               shallowA, shallowB, "crossing", "unassessed", true, 1e-6);

        var crossA = Triangle(new(-2, -2, 0), new(2, -2, 0), new(0, 2, 0));
        var crossB = Triangle(new(0, -1, -1), new(0, 1, -1), new(0, 0, 1));
        Expect("mesh: non-coplanar interiors cross symmetrically",
               crossA, crossB, "crossing", "unassessed", true);

        var coplanarA = Triangle(new(0, 0, 0), new(3, 0, 0), new(0, 3, 0));
        var coplanarB = Triangle(new(1, 0, 0), new(2, 0, 0), new(1, 1, 0));
        Expect("mesh: coplanar positive area is distinct",
               coplanarA, coplanarB, "coplanar_overlap", "unassessed", true);
        Expect("mesh: identical flat nondegenerate triangles remain eligible",
               coplanarA, coplanarA, "coplanar_overlap", "unassessed", true);

        var edgeA = Triangle(new(0, 0, 0), new(1, 0, 0), new(0, 1, 0));
        var edgeB = Triangle(new(0, 0, 0), new(1, 0, 0), new(1, -1, 0));
        Expect("mesh: coplanar shared edge is boundary touch",
               edgeA, edgeB, "touching", "unassessed", true);
        var pointB = Triangle(new(1, 0, 0), new(2, 0, 0), new(2, -1, 0));
        Expect("mesh: coplanar shared point is boundary touch",
               edgeA, pointB, "touching", "unassessed", true);
        var vertexOnInterior = Triangle(new(.25f, .25f, 0), new(.25f, .25f, 1), new(.75f, .25f, 1));
        Expect("mesh: a non-coplanar vertex-only contact is touching",
               edgeA, vertexOnInterior, "touching", "unassessed", true);

        const float far = 1_000_000;
        var farA = Triangle(new(far, -far, 0), new(far + 4, -far, 0), new(far, -far + 4, 0));
        var farPoint = Triangle(new(far + 4, -far, 0), new(far + 4.25f, -far, 0),
                                new(far + 4, -far + .25f, 0));
        var farSliver = Triangle(new(far + 3.875f, -far, 0), new(far + 4.125f, -far, 0),
                                 new(far + 3.875f, -far + .25f, 0));
        Expect("mesh: large-offset coplanar point contact stays boundary-only",
               farA, farPoint, "touching", "unassessed", true);
        Expect("mesh: large-offset representable coplanar sliver retains positive area",
               farA, farSliver, "coplanar_overlap", "unassessed", true);

        var parallel = Transform(edgeA, Matrix4x4.CreateTranslation(0, 0, .001f));
        Expect("mesh: near-parallel surfaces beyond tolerance remain separated",
               edgeA, parallel, "separated", "not_applicable_open_mesh", false, 1e-5);
        Expect("mesh: near-parallel separation inside tolerance is touching",
               edgeA, parallel, "touching", "unassessed", true, .002);
        var farParallelA = Transform(edgeA, Matrix4x4.CreateTranslation(far, -far, 0));
        var farParallelB = Transform(edgeA, Matrix4x4.CreateTranslation(far, -far, .001f));
        Expect("mesh: large translation does not widen the near-parallel separation band",
               farParallelA, farParallelB, "separated", "not_applicable_open_mesh", false, 1e-5);

        var tetraA = Tetra(Vector3.Zero, 2);
        var tetraB = Tetra(new(10, 10, 10), 2);
        Expect("mesh: separated closed solids have containment none",
               tetraA, tetraB, "separated", "none", false);
        var inner = Tetra(new(1, 1, 1), .5f);
        var outer = Tetra(Vector3.Zero, 10);
        Expect("mesh: closed inner solid reports a-inside-b without surface contact",
               inner, outer, "separated", "a_inside_b", true);
        Expect("mesh: closed-solid containment swaps direction with arguments",
               outer, inner, "separated", "b_inside_a", true);

        var mixedComponents = Combine(
            Box(new(-.5f), new(.5f)),
            Box(new(5), new(6)));
        var componentTarget = Box(new(-2), new(2));
        Expect("mesh: one of two disjoint closed components inside reports component containment",
               mixedComponents, componentTarget, "separated", "component_containment", true);
        Expect("mesh: component containment is symmetric under A/B swap",
               componentTarget, mixedComponents, "separated", "component_containment", true);

        var nestedShells = Combine(Box(new(-3), new(3)), Box(new(-1), new(1)));
        var thirdSolid = Box(new(10), new(11));
        try
        {
            var fill = MeshIntersection.Query(nestedShells, thirdSolid);
            check("mesh: nested closed components refuse an undeclared material/cavity fill rule",
                  Norm(fill.Status) == "unknown" && Norm(fill.SurfaceRelation) == "separated"
                  && Norm(fill.Containment) == "unknown" && fill.Intersects is null
                  && fill.Reason == "nested_shell_fill_rule_unassessed",
                  $"surface={fill.SurfaceRelation} containment={fill.Containment} intersects={fill.Intersects} status={fill.Status} reason={fill.Reason}");
        }
        catch (Exception ex)
        {
            check("mesh: nested closed components refuse an undeclared material/cavity fill rule",
                  false, $"threw {ex.GetType().Name}: {ex.Message}");
        }

        var openOuter = new TriangleSoup(outer.Vertices, outer.Indices[..^3]);
        Expect("mesh: an open nested surface refuses solid containment",
               inner, openOuter, "separated", "not_applicable_open_mesh", false);
        var nonmanifoldOuter = Combine(outer,
            Triangle(outer.Vertices[0], outer.Vertices[1], new(5, -2, 0)));
        Expect("mesh: nonmanifold topology refuses solid containment",
               inner, nonmanifoldOuter, "separated", "not_applicable_open_mesh", false);

        // Each tetra is independently edge-manifold, but the two closed components meet at one
        // duplicated-position vertex. That pinched vertex has two disconnected incident fans,
        // so the union has no unambiguous solid interior even though every edge has two uses.
        var pointPinchedShell = Combine(
            Tetra(Vector3.Zero, 1),
            Tetra(Vector3.Zero, -1));
        try
        {
            var pinched = MeshIntersection.Query(pointPinchedShell, Box(new(-3), new(3)));
            check("mesh: closed tetra components joined only at one vertex refuse containment",
                  Norm(pinched.Status) == "unknown" && Norm(pinched.SurfaceRelation) == "separated"
                  && Norm(pinched.Containment) == "unknown" && pinched.Intersects is null
                  && pinched.Reason == "nonmanifold_vertex_link",
                  $"surface={pinched.SurfaceRelation} containment={pinched.Containment} intersects={pinched.Intersects} status={pinched.Status} reason={pinched.Reason}");
        }
        catch (Exception ex)
        {
            check("mesh: closed tetra components joined only at one vertex refuse containment",
                  false, $"threw {ex.GetType().Name}: {ex.Message}");
        }

        var selfIntersecting = Combine(Tetra(Vector3.Zero, 3), Tetra(new(.5f, .5f, .5f), 3));
        try
        {
            var self = MeshIntersection.Query(selfIntersecting, tetraB);
            var containment = Norm(self.Containment);
            check("mesh: self-intersecting closed-indexed soup never gains a solid containment answer",
                  Norm(self.SurfaceRelation) == "separated"
                  && containment is "not_applicable_open_mesh" or "unknown"
                  && self.Intersects is null,
                  $"surface={self.SurfaceRelation} containment={self.Containment} status={self.Status} reason={self.Reason}");
        }
        catch (Exception ex)
        {
            check("mesh: self-intersecting closed-indexed soup never gains a solid containment answer",
                  false, $"threw {ex.GetType().Name}: {ex.Message}");
        }

        // ValidateSolid's self-scan across BVH leaves (lane V, 2026-10-20). Two crossing pyramids, 6 triangles each, split 6/6
        // along x: the left leaf holds P (base x=0, apex x=3), the right leaf Q (base x=4, apex x=1). V1 gives P the lower
        // triangle indices, the orientation the old scan never tested; V1c gives Q the lower indices and must agree.
        var farSolid = Tetra(new(100, 0, 0), 1);
        var pyramidP = Pyramid(0, 1, 3);
        var pyramidQ = Pyramid(4, 1.5f, 1);
        const string selfReason = "self_intersecting_or_self_touching_mesh";
        bool IsSelfIntersecting(MeshIntersectionResult r) =>
            Norm(r.Status) == "unknown" && r.Reason == selfReason && Norm(r.SurfaceRelation) == "separated"
            && Norm(r.Containment) == "unknown" && r.Intersects is null;
        string Show(MeshIntersectionResult r) =>
            $"status={r.Status} reason={r.Reason} surface={r.SurfaceRelation} containment={r.Containment} intersects={r.Intersects} pairs={r.TrianglePairsTested}";
        foreach (var (name, soup) in new[]
        {
            ("mesh: V1 crossing components whose lower indices sit in the left leaf are self-intersecting", Combine(pyramidP, pyramidQ)),
            ("mesh: V1c crossing components whose lower indices sit in the right leaf are self-intersecting", Combine(pyramidQ, pyramidP)),
        })
        {
            try
            {
                var fresh = MeshIntersection.Query(soup, farSolid);
                var pa = MeshIntersection.PreparedMesh.Create(soup);
                var pb = MeshIntersection.PreparedMesh.Create(farSolid);
                var filling = MeshIntersection.Query(pa, pb);
                var memoHit = MeshIntersection.Query(pa, pb);
                check(name,
                      IsSelfIntersecting(fresh) && filling == fresh && memoHit == fresh,
                      $"soup: {Show(fresh)}; prepared: {Show(filling)}; memo hit: {Show(memoHit)}");
            }
            catch (Exception ex)
            {
                check(name, false, $"threw {ex.GetType().Name}: {ex.Message}");
            }
        }

        // V2: the self-scan tests every unordered triangle pair whose bounds meet exactly once. A closed 32-triangle prism (four
        // leaves) against a far open triangle: the surface traversal and the open side consume nothing, so the query's units are
        // the prism's scan alone, counted here by brute force.
        try
        {
            var prism = Prism(8);
            var farOpen = Triangle(new(100, 0, 0), new(101, 0, 0), new(100, 1, 0));
            int expected = 0;
            var boxes = Enumerable.Range(0, prism.Indices.Length / 3).Select(t =>
            {
                var v = new[] { prism.Vertices[prism.Indices[t * 3]], prism.Vertices[prism.Indices[t * 3 + 1]], prism.Vertices[prism.Indices[t * 3 + 2]] };
                return (Min: Vector3.Min(Vector3.Min(v[0], v[1]), v[2]), Max: Vector3.Max(Vector3.Max(v[0], v[1]), v[2]));
            }).ToArray();
            for (int i = 0; i < boxes.Length; i++)
            for (int j = i + 1; j < boxes.Length; j++)
                if (boxes[i].Min.X <= boxes[j].Max.X && boxes[j].Min.X <= boxes[i].Max.X &&
                    boxes[i].Min.Y <= boxes[j].Max.Y && boxes[j].Min.Y <= boxes[i].Max.Y &&
                    boxes[i].Min.Z <= boxes[j].Max.Z && boxes[j].Min.Z <= boxes[i].Max.Z)
                    expected++;
            var counted = MeshIntersection.Query(prism, farOpen);
            check("mesh: V2 a valid solid's self-scan tests each overlapping triangle pair exactly once",
                  Norm(counted.Status) == "ok" && Norm(counted.Containment) == "not_applicable_open_mesh"
                  && counted.TrianglePairsTested == expected,
                  $"pairs={counted.TrianglePairsTested} expected={expected} triangles={boxes.Length} {Show(counted)}");
        }
        catch (Exception ex)
        {
            check("mesh: V2 a valid solid's self-scan tests each overlapping triangle pair exactly once",
                  false, $"threw {ex.GetType().Name}: {ex.Message}");
        }

        Expect("mesh: negative triangle index is invalid, never false clean",
               new(edgeA.Vertices, new[] { 0, 1, -1 }), edgeB, "unknown", "unknown", null);
        Expect("mesh: out-of-range triangle index is invalid, never false clean",
               new(edgeA.Vertices, new[] { 0, 1, 3 }), edgeB, "unknown", "unknown", null);
        Expect("mesh: trailing partial triangle is invalid, never ignored",
               new(edgeA.Vertices, new[] { 0, 1, 2, 0 }), edgeB, "unknown", "unknown", null);
        Expect("mesh: nonfinite referenced vertex is invalid, never false clean",
               Triangle(new(float.NaN, 0, 0), new(1, 0, 0), new(0, 1, 0)), edgeB,
               "unknown", "unknown", null);
        Expect("mesh: infinite referenced vertex is invalid, never false clean",
               Triangle(new(float.PositiveInfinity, 0, 0), new(1, 0, 0), new(0, 1, 0)), edgeB,
               "unknown", "unknown", null);
        Expect("mesh: repeated-index triangle is degenerate, never repaired",
               new(edgeA.Vertices, new[] { 0, 1, 1 }), edgeB, "unknown", "unknown", null);
        Expect("mesh: duplicate-position triangle is degenerate, never repaired",
               Triangle(Vector3.Zero, Vector3.Zero, Vector3.UnitX), edgeB,
               "unknown", "unknown", null);
        Expect("mesh: collinear triangle is degenerate, never repaired",
               Triangle(Vector3.Zero, Vector3.UnitX, 2 * Vector3.UnitX), edgeB,
               "unknown", "unknown", null);

        Expect("mesh: exhausted work budget is unknown rather than a false clean result",
               Repeat(lower, 4), Repeat(upper, 4), "unknown", "unknown", null, budget: 1);
        try
        {
            using var cancelled = new CancellationTokenSource();
            cancelled.Cancel();
            var result = MeshIntersection.Query(lower, upper, cancellationToken: cancelled.Token);
            check("mesh: cancellation returns explicit unknown rather than a false clean result",
                  Norm(result.SurfaceRelation) == "unknown" && Norm(result.Containment) == "unknown"
                  && result.Intersects is null && Norm(result.Status) == "unknown"
                  && !string.IsNullOrWhiteSpace(result.Reason),
                  $"surface={result.SurfaceRelation} containment={result.Containment} status={result.Status} reason={result.Reason}");
        }
        catch (Exception ex)
        {
            check("mesh: cancellation returns explicit unknown rather than a false clean result",
                  false, $"threw {ex.GetType().Name}: {ex.Message}");
        }

        var invariantCases = new[]
        {
            ("crossing", crossA, crossB, "crossing", "unassessed", (bool?)true),
            ("coplanar", coplanarA, coplanarB, "coplanar_overlap", "unassessed", (bool?)true),
            ("edge-touch", edgeA, edgeB, "touching", "unassessed", (bool?)true),
            ("box-false-positive", lower, upper, "separated", "not_applicable_open_mesh", (bool?)false),
            ("nested", inner, outer, "separated", "a_inside_b", (bool?)true)
        };
        foreach (var c in invariantCases)
        {
            Expect($"mesh metamorphic: {c.Item1} survives A/B swap", c.Item3, c.Item2,
                   c.Item4, SwapContainment(c.Item5), c.Item6);
            Expect($"mesh metamorphic: {c.Item1} survives both reversed windings",
                   Reverse(c.Item2), Reverse(c.Item3), c.Item4, c.Item5, c.Item6);

            var rotation = Matrix4x4.CreateRotationX(31 * MathF.PI / 180)
                         * Matrix4x4.CreateRotationY(-47 * MathF.PI / 180)
                         * Matrix4x4.CreateRotationZ(19 * MathF.PI / 180);
            var posed = Matrix4x4.CreateScale(3.25f) * rotation
                       * Matrix4x4.CreateTranslation(17, -9, 4);
            var posedA = Transform(c.Item2, posed); var posedB = Transform(c.Item3, posed);
            if (c.Item1 == "coplanar")
            {
                var result = MeshIntersection.Query(posedA, posedB, tolerance: 3.25e-5);
                var relation = Norm(result.SurfaceRelation);
                check("mesh metamorphic: arbitrary-pose coplanar source retains represented-surface intersection",
                      Norm(result.Status) == "ok" && result.Intersects == true
                      && relation is "crossing" or "coplanar_overlap" or "touching",
                      $"surface={relation} intersects={result.Intersects} bits=[{string.Join(",", posedA.Vertices.Concat(posedB.Vertices).SelectMany(v => new[] { Bits(v.X), Bits(v.Y), Bits(v.Z) }))}]");
            }
            else
                Expect($"mesh metamorphic: {c.Item1} survives multi-axis pose and scale",
                       posedA, posedB, c.Item4, c.Item5, c.Item6, tolerance: 3.25e-5);

            var reflected = Matrix4x4.CreateScale(-2)
                          * Matrix4x4.CreateTranslation(-11, 6, 23);
            Expect($"mesh metamorphic: {c.Item1} survives negative scale/reflection",
                   Transform(c.Item2, reflected), Transform(c.Item3, reflected),
                   c.Item4, c.Item5, c.Item6, tolerance: 2e-5);
        }

        var exactPose = new Matrix4x4(
             0, 4, 0, 0,
            -4, 0, 0, 0,
             0, 0, 4, 0,
            16, -8, 4, 1);
        Expect("mesh metamorphic: exactly representable signed-axis pose preserves coplanar area",
               Transform(coplanarA, exactPose), Transform(coplanarB, exactPose),
               "coplanar_overlap", "unassessed", true);

        var literalCrossA = Triangle(
            HexVector("41880000", "C1100000", "40800000"),
            HexVector("41BA4C35", "C0DAB977", "41321757"),
            HexVector("412CE7DE", "C012CA9C", "40ED9774"));
        var literalCrossB = Triangle(
            HexVector("4198C412", "C104743E", "40CC0F90"),
            HexVector("41A98824", "C0F1D0FA", "410C0F90"),
            HexVector("4188400C", "C0C15F97", "40F09761"));
        Expect("mesh literal float fixture: exact rational cut intervals prove positive crossing",
               literalCrossA, literalCrossB, "crossing", "unassessed", true, tolerance: 3.25e-5);

        var literalTouchA = Triangle(
            HexVector("41880000", "C1100000", "40800000"),
            HexVector("41BA4C34", "C0DAB978", "41321758"),
            HexVector("412CE7DE", "C012CA9C", "40ED9774"));
        var literalTouchB = Triangle(
            HexVector("4198C412", "C104743F", "40CC0F90"),
            HexVector("41A98823", "C0F1D0FA", "410C0F90"),
            HexVector("4188400C", "C0C15F97", "40F09761"));
        Expect("mesh literal float fixture: one-sided planes within tolerance are touching",
               literalTouchA, literalTouchB, "touching", "unassessed", true, tolerance: 3.25e-5);

        foreach (var scale in new[] { .125f, 1f, 8f })
        {
            var transform = Matrix4x4.CreateScale(scale);
            Expect($"mesh metamorphic: crossing survives regenerated scale {scale}",
                   Transform(crossA, transform), Transform(crossB, transform),
                   "crossing", "unassessed", true, tolerance: 1e-5 * scale);
        }

        var grid = (from x in Enumerable.Range(-1, 3)
                    from y in Enumerable.Range(-1, 3)
                    select new P2(x, y)).ToArray();
        var triangles = (from i in Enumerable.Range(0, grid.Length)
                         from j in Enumerable.Range(i + 1, grid.Length - i - 1)
                         from k in Enumerable.Range(j + 1, grid.Length - j - 1)
                         let t = Tri2(grid[i], grid[j], grid[k])
                         where Nondegenerate(t)
                         select t).ToArray();

        var coplanarTotal = 0; var coplanarWrong = 0; string? firstCoplanarWrong = null;
        var boxFalsePositives = 0;
        for (var i = 0; i < triangles.Length; i++)
        for (var j = 0; j < triangles.Length; j++)
        {
            var expected = CoplanarOracle(triangles[i], triangles[j]);
            var actual = Norm(MeshIntersection.Query(Xy(triangles[i]), Xy(triangles[j])).SurfaceRelation);
            coplanarTotal++;
            if (expected != actual)
            {
                coplanarWrong++;
                firstCoplanarWrong ??= $"i={i} j={j} expected={expected} actual={actual}";
            }
            if (expected == "separated")
            {
                var ai = triangles[i]; var bj = triangles[j];
                var xOverlap = Math.Min(Math.Max(ai.A.X, Math.Max(ai.B.X, ai.C.X)), Math.Max(bj.A.X, Math.Max(bj.B.X, bj.C.X)))
                             >= Math.Max(Math.Min(ai.A.X, Math.Min(ai.B.X, ai.C.X)), Math.Min(bj.A.X, Math.Min(bj.B.X, bj.C.X)));
                var yOverlap = Math.Min(Math.Max(ai.A.Y, Math.Max(ai.B.Y, ai.C.Y)), Math.Max(bj.A.Y, Math.Max(bj.B.Y, bj.C.Y)))
                             >= Math.Max(Math.Min(ai.A.Y, Math.Min(ai.B.Y, ai.C.Y)), Math.Min(bj.A.Y, Math.Min(bj.B.Y, bj.C.Y)));
                if (xOverlap && yOverlap) boxFalsePositives++;
            }
        }
        check("mesh oracle: exhaustive integer coplanar lattice agrees without production helpers",
              coplanarWrong == 0 && coplanarTotal > 1000 && boxFalsePositives > 0,
              $"cases={coplanarTotal} wrong={coplanarWrong} boxFalsePositives={boxFalsePositives} first={firstCoplanarWrong}");

        var orthogonalTotal = 0; var orthogonalWrong = 0; var symmetryWrong = 0;
        string? firstOrthogonalWrong = null;
        for (var i = 0; i < triangles.Length; i++)
        for (var j = 0; j < triangles.Length; j++)
        foreach (var y in new[] { -1, 0, 1 })
        {
            var a = Xy(triangles[i]); var b = Xz(triangles[j], y);
            var expected = OrthogonalOracle(triangles[i], triangles[j], y);
            var actual = Norm(MeshIntersection.Query(a, b).SurfaceRelation);
            orthogonalTotal++;
            if (expected != actual)
            {
                orthogonalWrong++;
                firstOrthogonalWrong ??= $"i={i} j={j} y={y} expected={expected} actual={actual}";
            }
            // Deterministic symmetry sample is independent of production traversal order.
            if ((i * 97 + j * 31 + y + 2) % 29 == 0)
            {
                var swap = Norm(MeshIntersection.Query(b, a).SurfaceRelation);
                var winding = Norm(MeshIntersection.Query(Reverse(a), Reverse(b)).SurfaceRelation);
                if (swap != expected || winding != expected) symmetryWrong++;
            }
        }
        check("mesh oracle: exhaustive orthogonal lattice and symmetry sample agree",
              orthogonalWrong == 0 && symmetryWrong == 0 && orthogonalTotal > 5000,
              $"cases={orthogonalTotal} wrong={orthogonalWrong} symmetryWrong={symmetryWrong} first={firstOrthogonalWrong}");

        check("mesh policy: one stable nonempty policy labels all direct fixture results",
              policies.Count == 1 && !string.IsNullOrWhiteSpace(policies.SingleOrDefault()),
              $"policies=[{string.Join(",", policies)}]");
    }
}
