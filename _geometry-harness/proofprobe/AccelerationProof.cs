using System.Numerics;
using HarnessShared;
using HousecarlCore;
using Mutagen.Bethesda.Plugins;

/// <summary>Known answers that hold the evaluator speedups to today's results (AX-*): ties and traversal order in the triangle grid,
/// InsideDepth by its documented definition, Query's relations and solid checks, and the same answers from many threads.
/// Each expected value comes from the definition, a hand calculation, or the reference traversal below, never from the grid.</summary>
static class AccelerationProof
{
    static TriangleSoup Soup(Vector3[] v, params int[] i) => new(v, i);

    // A box wound outward: triangles 0,1 are -x, 2,3 +x, 4,5 -y, 6,7 +y, 8,9 -z, 10,11 +z.
    static TriangleSoup OutwardBox(Vector3 lo, Vector3 hi)
    {
        Vector3 V(int x, int y, int z) => new(x == 0 ? lo.X : hi.X, y == 0 ? lo.Y : hi.Y, z == 0 ? lo.Z : hi.Z);
        return Soup([V(0, 0, 0), V(1, 0, 0), V(1, 1, 0), V(0, 1, 0), V(0, 0, 1), V(1, 0, 1), V(1, 1, 1), V(0, 1, 1)],
            0, 4, 7, 0, 7, 3, 1, 2, 6, 1, 6, 5, 0, 1, 5, 0, 5, 4, 3, 7, 6, 3, 6, 2, 0, 3, 2, 0, 2, 1, 4, 5, 6, 4, 6, 7);
    }

    static TriangleSoup Flip(TriangleSoup s)
    {
        var i = (int[])s.Indices.Clone();
        for (int k = 0; k + 2 < i.Length; k += 3) (i[k + 1], i[k + 2]) = (i[k + 2], i[k + 1]);
        return new((Vector3[])s.Vertices.Clone(), i);
    }

    static TriangleSoup Join(params TriangleSoup[] soups)
    {
        var v = new List<Vector3>(); var i = new List<int>();
        foreach (var s in soups) { int o = v.Count; v.AddRange(s.Vertices); i.AddRange(s.Indices.Select(x => x + o)); }
        return new(v.ToArray(), i.ToArray());
    }

    static TriangleSoup Tris(params (Vector3 A, Vector3 B, Vector3 C)[] t) =>
        Soup(t.SelectMany(x => new[] { x.A, x.B, x.C }).ToArray(), Enumerable.Range(0, t.Length * 3).ToArray());

    static TriangleSoup Tetra(Vector3 o, float s) =>
        Soup([o, o + new Vector3(s, 0, 0), o + new Vector3(0, s, 0), o + new Vector3(0, 0, s)], 0, 2, 1, 0, 1, 3, 0, 3, 2, 1, 2, 3);

    static Vector3 P(float x, float y, float z) => new(x, y, z);
    static string Hex(float f) => BitConverter.SingleToInt32Bits(f).ToString("x8");
    static string Hex(Vector3 v) => $"{Hex(v.X)},{Hex(v.Y)},{Hex(v.Z)}";

    // One case: an exception inside it fails that case by name, so a crash cannot hide the other cases or pass for a red on the assertion.
    static void Case(Action<string, bool, string> check, string name, Func<(bool Ok, string Detail)> body)
    {
        try { var (ok, detail) = body(); check(name, ok, detail); }
        catch (Exception ex) { check(name, false, $"THREW {ex.GetType().Name}: {ex.Message}"); }
    }

    public static void Run(Action<string, bool, string> check)
    {
        InsideDepthCases(check);
        TieCases(check);
        ReferenceTraversal(check);
        ManyThreadGrid(check);
        RelationCases(check);
        SolidCases(check);
        ContactIndexOrder(check, manyThreads: false);
        ContactIndexOrder(check, manyThreads: true);
        ManyThreadPreparedMesh(check);
    }

    // ---- InsideDepth: the distance to the nearest face within reach when p is behind every face at that distance, else (0, zero).
    static void InsideDepthCases(Action<string, bool, string> check)
    {
        var box = TriangleGrid.Build(OutwardBox(P(0, 0, 0), P(64, 64, 64)));
        var inverted = TriangleGrid.Build(Flip(OutwardBox(P(0, 0, 0), P(64, 64, 64))));
        var plate = TriangleGrid.Build(Soup([P(0, 0, 0), P(64, 0, 0), P(64, 64, 0), P(0, 64, 0)], 0, 1, 2, 0, 2, 3));
        void Depth(string name, TriangleGrid g, Vector3 p, float reach, float depth, Vector3 normal) => Case(check, name, () =>
        {
            var r = g.InsideDepth(p, reach);
            return (MathF.Abs(r.Depth - depth) < 1e-3f && r.Normal == normal, $"depth {r.Depth}, normal {r.Normal}; expected {depth}, {normal}");
        });
        var none = Vector3.Zero;
        Depth("AX-1 just outside a closed host's box: in front of the nearest face, not inside", box, P(-0.5f, 20, 30), 128, 0, none);
        Depth("AX-2 exactly on the box face: distance zero is not inside", box, P(0, 20, 30), 128, 0, none);
        Depth("AX-3 inside a closed host: the distance to the nearest face and that face's outward normal", box, P(10, 20, 30), 128, 10, P(-1, 0, 0));
        Depth("AX-4 inside, reach shorter than the depth: no face within reach, so not inside", box, P(10, 20, 30), 9, 0, none);
        Depth("AX-5 inside, reach equal to the depth: a hit must be nearer than reach, so not inside", box, P(10, 20, 30), 10, 0, none);
        Depth("AX-6 inside, reach just past the depth: found", box, P(10, 20, 30), 10.5f, 10, P(-1, 0, 0));
        Depth("AX-7 at the centre, all six faces tie: inside, normal from the first cell visited (lowest x, the -x face)", box, P(32, 32, 32), 33, 32, P(-1, 0, 0));
        Depth("AX-8 inside a corner, two faces tie: behind both, so inside, normal from the lower-numbered face", box, P(1, 1, 20), 128, 1, P(-1, 0, 0));
        Depth("AX-9 outside a convex edge, two faces tie: in front of both, not inside", box, P(-1, -1, 20), 128, 0, none);
        Depth("AX-10 inward-wound closed host, point outside its box: behind the face, so inside by the definition; a box cull assuming outward winding says 0", inverted, P(-0.5f, 20, 30), 128, 0.5f, P(1, 0, 0));
        Depth("AX-11 inward-wound closed host, point within its box: in front of the face, not inside", inverted, P(10, 20, 30), 128, 0, none);
        Depth("AX-12 inward-wound closed host, outside its box past reach: nothing within reach", inverted, P(-0.5f, 20, 30), 0.4f, 0, none);
        Depth("AX-13 open plate, below it and outside its flat box: behind the face, so inside by the definition", plate, P(20, 30, -3), 128, 3, P(0, 0, 1));
        Depth("AX-14 open plate, above it and outside its flat box: in front, not inside", plate, P(20, 30, 3), 128, 0, none);
        Depth("AX-15 open plate, past its rim and below: nearest point on the rim at root 45, still behind the plane", plate, P(70, 30, -3), 128, MathF.Sqrt(45), P(0, 0, 1));
        Depth("AX-16 open plate, below it, reach short of the distance", plate, P(20, 30, -3), 2.5f, 0, none);
        var pair = TriangleGrid.Build(Tris((P(0, 0, 0), P(60, 0, 0), P(0, 60, 0)), (P(0, 0, 0), P(0, 60, 0), P(60, 0, 0))));
        Depth("AX-17 duplicate face wound both ways: behind one and in front of the other at one distance, so not inside", pair, P(10, 10, 5), 128, 0, none);
        Depth("AX-18 duplicate face wound both ways, from the other side: still not inside", pair, P(10, 10, -5), 128, 0, none);
        var dup = TriangleGrid.Build(Tris((P(0, 0, 0), P(60, 0, 0), P(0, 60, 0)), (P(0, 0, 0), P(60, 0, 0), P(0, 60, 0))));
        Depth("AX-19 exact duplicate face, from behind: inside, at the shared distance", dup, P(10, 10, -5), 128, 5, P(0, 0, 1));
    }

    // ---- Ties: the first triangle in traversal order keeps the pick. Box queries go x, then y, then z over cells and a cell's list ascends.
    static void TieCases(Action<string, bool, string> check)
    {
        (Vector3, Vector3, Vector3) Flat(float z) => (P(10, 10, z), P(12, 10, z), P(10, 12, z));
        (Vector3, Vector3, Vector3) PlaneX(float x) => (P(x, 10, 10), P(x, 12, 10), P(x, 10, 12));
        void Pick(string name, TriangleSoup soup, (Vector3 A, Vector3 B, Vector3 C) q, int tri, float dist) => Case(check, name, () =>
        {
            var r = TriangleGrid.Build(soup).NearestTriangle(q.A, q.B, q.C, 64);
            return (r.Triangle == tri && MathF.Abs(r.Distance - dist) < 1e-3f, $"triangle {r.Triangle} at {r.Distance}; expected {tri} at {dist}");
        });
        Pick("AX-20 nearest triangle, same cell, equal distance: the lower number wins", Tris(Flat(4), Flat(16)), Flat(10), 0, 6);
        Pick("AX-21 the same pair listed the other way round: still the lower number, now the other face", Tris(Flat(16), Flat(4)), Flat(10), 0, 6);
        Pick("AX-22 nearest triangle, coplanar duplicates: the first copy", Tris(Flat(4), Flat(4), Flat(4)), Flat(10), 0, 6);
        Pick("AX-23 nearest triangle, different cells, equal distance: the earlier cell beats the lower number (triangle 1 in cell x=0, triangle 0 in cell x=1)",
             Tris(PlaneX(40), PlaneX(24)), PlaneX(32), 1, 8);
        Pick("AX-24 same with the tie along z: the earlier z cell wins", Tris(Flat(40), Flat(24)), Flat(32), 1, 8);
        // The query lies in the plane x = y, so each face is the other's mirror; x is the outer loop, so the low-x cell wins with the higher number.
        Pick("AX-25 tie across x and y cells: x is the outer loop, so the low-x cell wins",
             Tris((P(40, 20, 10), P(44, 20, 10), P(40, 24, 10)), (P(20, 40, 10), P(20, 44, 10), P(24, 40, 10))),
             (P(32, 32, 10), P(33, 33, 10), P(32, 32, 11)), 1, MathF.Sqrt(128));

        (Vector3, Vector3, Vector3) AtX(float x) => (P(x, 0, 0), P(x, 30, 0), P(x, 0, 30));
        void Point(string name, TriangleSoup soup, Vector3 p, int tri, float dist, Vector3 at) => Case(check, name, () =>
        {
            var r = TriangleGrid.Build(soup).NearestPoint(p, 64);
            return (r.Triangle == tri && MathF.Abs(r.Distance - dist) < 1e-3f && (r.Point - at).Length() < 1e-3f, $"triangle {r.Triangle} at {r.Distance}, point {r.Point}; expected {tri}, {dist}, {at}");
        });
        Point("AX-26 nearest point, equal distance: the nearer ring beats the lower number (triangle 1 in the point's own cell)", Tris(AtX(36), AtX(24)), P(30, 10, 10), 1, 6, P(24, 10, 10));
        Point("AX-27 nearest point, equal distance in one ring: the earlier cell beats the lower number", Tris(AtX(65), AtX(31)), P(48, 10, 10), 1, 17, P(31, 10, 10));
        Point("AX-28 nearest point, coplanar duplicates: the first copy", Tris(AtX(20), AtX(20)), P(25, 10, 10), 0, 5, P(20, 10, 10));
        Case(check, "AX-29 faces within a band come in traversal order, the earlier cell first, each duplicate reported", () =>
        {
            var seen = new List<int>();
            var q = PlaneX(32);
            TriangleGrid.Build(Tris(PlaneX(40), PlaneX(24), PlaneX(24))).ForEachWithin(q.Item1, q.Item2, q.Item3, 8.5f, (t, _, _, _, _) => seen.Add(t));
            return (seen.SequenceEqual([1, 2, 0]), $"visited {string.Join(",", seen)}; expected 1,2,0");
        });
    }

    // ---- The reference traversal: every cell a triangle's box covers, cells in loop order, a triangle once, at its first cell.
    sealed class Reference(TriangleSoup soup, float cell)
    {
        readonly Dictionary<(int, int, int), List<int>> _cells = Fill(soup, cell);
        readonly Vector3[] _n = Enumerable.Range(0, soup.TriangleCount).Select(t =>
        {
            var (a, b, c) = (soup.Vertices[soup.Indices[t * 3]], soup.Vertices[soup.Indices[t * 3 + 1]], soup.Vertices[soup.Indices[t * 3 + 2]]);
            var n = Vector3.Cross(b - a, c - a); float l = n.Length();
            return l < 1e-9f ? Vector3.Zero : n / l;
        }).ToArray();
        (Vector3, Vector3, Vector3) T(int t) => (soup.Vertices[soup.Indices[t * 3]], soup.Vertices[soup.Indices[t * 3 + 1]], soup.Vertices[soup.Indices[t * 3 + 2]]);
        int C(float v) => (int)MathF.Floor(v / cell);

        static Dictionary<(int, int, int), List<int>> Fill(TriangleSoup s, float size)
        {
            var d = new Dictionary<(int, int, int), List<int>>();
            int Axis(float v) => (int)MathF.Floor(v / size);
            for (int t = 0; t < s.TriangleCount; t++)
            {
                var vs = new[] { s.Vertices[s.Indices[t * 3]], s.Vertices[s.Indices[t * 3 + 1]], s.Vertices[s.Indices[t * 3 + 2]] };
                for (int x = Axis(vs.Min(v => v.X)); x <= Axis(vs.Max(v => v.X)); x++)
                for (int y = Axis(vs.Min(v => v.Y)); y <= Axis(vs.Max(v => v.Y)); y++)
                for (int z = Axis(vs.Min(v => v.Z)); z <= Axis(vs.Max(v => v.Z)); z++)
                {
                    if (!d.TryGetValue((x, y, z), out var l)) d[(x, y, z)] = l = [];
                    l.Add(t);
                }
            }
            return d;
        }

        IEnumerable<int> BoxOrder(Vector3 a, Vector3 b, Vector3 c, float grow)
        {
            var seen = new HashSet<int>();
            float Lo(Func<Vector3, float> f) => MathF.Min(f(a), MathF.Min(f(b), f(c))) - grow;
            float Hi(Func<Vector3, float> f) => MathF.Max(f(a), MathF.Max(f(b), f(c))) + grow;
            for (int x = C(Lo(v => v.X)); x <= C(Hi(v => v.X)); x++)
            for (int y = C(Lo(v => v.Y)); y <= C(Hi(v => v.Y)); y++)
            for (int z = C(Lo(v => v.Z)); z <= C(Hi(v => v.Z)); z++)
                if (_cells.TryGetValue((x, y, z), out var l))
                    foreach (int t in l) if (seen.Add(t)) yield return t;
        }

        // Stops at the first ring that cannot hold a nearer face, once a face is found: a ring r holds nothing nearer than (r - 1) cells.
        IEnumerable<int> RingOrder(Vector3 p, float reach, Func<(bool Found, float Best)> state)
        {
            var seen = new HashSet<int>();
            int cx = C(p.X), cy = C(p.Y), cz = C(p.Z), rings = (int)MathF.Ceiling(reach / cell) + 1;
            for (int r = 0; r <= rings; r++)
            {
                var (found, best) = state();
                if (found && (r - 1) * cell > best) yield break;
                for (int x = cx - r; x <= cx + r; x++)
                for (int y = cy - r; y <= cy + r; y++)
                for (int z = cz - r; z <= cz + r; z++)
                    if (Math.Max(Math.Abs(x - cx), Math.Max(Math.Abs(y - cy), Math.Abs(z - cz))) == r && _cells.TryGetValue((x, y, z), out var l))
                        foreach (int t in l) if (seen.Add(t)) yield return t;
            }
        }

        public string Nearest(Vector3 a, Vector3 b, Vector3 c, float reach)
        {
            float best = reach; int pick = -1;
            foreach (int t in BoxOrder(a, b, c, reach))
            {
                var (x, y, z) = T(t); float d = SpatialOverlap.TriangleDistance(a, b, c, x, y, z);
                if (d < best) { best = d; pick = t; if (best <= 0f) return Grid((0f, pick)); }
            }
            return Grid((best, pick));
        }

        public string Point(Vector3 p, float reach)
        {
            float best = reach; int pick = -1; var at = Vector3.Zero;
            foreach (int t in RingOrder(p, reach, () => (pick >= 0, best)))
            {
                var (x, y, z) = T(t); var q = SpatialOverlap.ClosestPointOnTriangle(p, x, y, z); float d = (p - q).Length();
                if (d < best) { best = d; pick = t; at = q; }
            }
            return pick < 0 ? Grid((float.NaN, Vector3.Zero, -1)) : Grid((best, at, pick));
        }

        public string Inside(Vector3 p, float reach)
        {
            float best = reach; int pick = -1; bool behindAll = true; var n = Vector3.Zero;
            foreach (int t in RingOrder(p, reach, () => (pick >= 0, best)))
            {
                if (_n[t] == Vector3.Zero) continue;
                var (x, y, z) = T(t); var q = SpatialOverlap.ClosestPointOnTriangle(p, x, y, z); float d = (p - q).Length();
                float side = Vector3.Dot(p - q, _n[t]);
                bool behind = side < -1e-4f || (d < 1e-4f && side <= 0f);
                if (d < best - 1e-4f) { best = d; pick = t; behindAll = behind; n = _n[t]; }
                else if (pick >= 0 && MathF.Abs(d - best) <= 1e-4f) behindAll &= behind;
            }
            return pick < 0 || !behindAll || best <= 1e-4f ? Grid((0f, Vector3.Zero)) : Grid((best, n));
        }

        public string Within(Vector3 a, Vector3 b, Vector3 c, float band)
        {
            var o = new List<string>();
            foreach (int t in BoxOrder(a, b, c, band))
            {
                if (_n[t] == Vector3.Zero) continue;
                var (x, y, z) = T(t); float d = SpatialOverlap.TriangleClosestPoints(a, b, c, x, y, z, out var p, out var q, out bool crosses);
                if (d <= band) o.Add($"{t}:{Hex(d)}:{Hex(p)}:{Hex(q)}:{crosses}");
            }
            return string.Join(";", o);
        }
    }

    static string Grid((float D, int T) r) => $"{Hex(r.D)}|{r.T}";
    static string Grid((float D, Vector3 P, int T) r) => $"{Hex(r.D)}|{Hex(r.P)}|{r.T}";
    static string Grid((float D, Vector3 N) r) => $"{Hex(r.D)}|{Hex(r.N)}";

    // Lattice coordinates make ties common; every fifth face repeats the one before it, every seventh repeats it wound the other way.
    static TriangleSoup TieSoup(int seed, int count)
    {
        var rng = new Random(seed); var tris = new List<(Vector3 A, Vector3 B, Vector3 C)>();
        float L(int n) => rng.Next(-n, n + 1) * 8f;
        while (tris.Count < count)
        {
            if (tris.Count % 5 == 4) { tris.Add(tris[^1]); continue; }
            if (tris.Count % 7 == 6) { var (a0, b0, c0) = tris[^1]; tris.Add((a0, c0, b0)); continue; }
            var o = P(L(10), L(10), L(10)); float s = 8f * rng.Next(1, 6), u = 8f * rng.Next(1, 6);
            tris.Add(rng.Next(3) switch
            {
                0 => (o, o + P(s, 0, 0), o + P(0, u, 0)),
                1 => (o, o + P(0, s, 0), o + P(0, 0, u)),
                _ => (o, o + P(0, 0, s), o + P(u, 0, 0)),
            });
        }
        return Tris(tris.ToArray());
    }

    sealed record GridQuery(Vector3 A, Vector3 B, Vector3 C, float Reach);

    static GridQuery[] TieQueries(int seed, int count)
    {
        var rng = new Random(seed); var q = new GridQuery[count];
        for (int k = 0; k < count; k++)
        {
            var p = P(rng.Next(-20, 21) * 4f, rng.Next(-20, 21) * 4f, rng.Next(-20, 21) * 4f);
            q[k] = new(p, p + P(4, 0, 0), p + P(0, 4, 0), rng.Next(1, 7) * 8f);
        }
        return q;
    }

    static string Answer(TriangleGrid g, GridQuery q)
    {
        var within = new List<string>();
        g.ForEachWithin(q.A, q.B, q.C, q.Reach, (t, d, p, r, crosses) => within.Add($"{t}:{Hex(d)}:{Hex(p)}:{Hex(r)}:{crosses}"));
        return $"{Grid(g.NearestTriangle(q.A, q.B, q.C, q.Reach))} {Grid(g.NearestPoint(q.A, q.Reach))} {Grid(g.InsideDepth(q.A, q.Reach))} {string.Join(";", within)}";
    }

    static void ReferenceTraversal(Action<string, bool, string> check)
    {
        var soup = TieSoup(4301, 400); var queries = TieQueries(4302, 1500); var reference = new Reference(soup, 32f);
        var expected = queries.Select(q => $"{reference.Nearest(q.A, q.B, q.C, q.Reach)} {reference.Point(q.A, q.Reach)} {reference.Inside(q.A, q.Reach)} {reference.Within(q.A, q.B, q.C, q.Reach)}").ToArray();
        void Against(string name, Func<TriangleGrid> build) => Case(check, name, () =>
        {
            var g = build(); int wrong = 0, first = -1;
            for (int k = 0; k < queries.Length; k++)
                if (Answer(g, queries[k]) != expected[k]) { wrong++; if (first < 0) first = k; }
            string shown = first < 0 ? "" : $"; first at query {first}: got {Answer(g, queries[first])} expected {expected[first]}";
            return (wrong == 0, $"{wrong} of {queries.Length} queries differ from the reference traversal{shown}");
        });
        Against("AX-30 fixed cells on a soup full of ties: all four queries match the reference traversal bit for bit, picks and normals", () => TriangleGrid.Build(soup));
        Against("AX-31 adaptive grid at the default target: the same", () => TriangleGrid.BuildAdaptive(soup));
        Against("AX-32 adaptive grid coarsened by a one-byte target: storage changes, answers do not", () => TriangleGrid.BuildAdaptive(soup, 32f, 1));
        Case(check, "AX-33 a triangle spanning many cells is found from a query that starts inside its span, and once only", () =>
        {
            var g = TriangleGrid.Build(Tris((P(-400, -400, 0), P(400, -400, 0), P(0, 400, 0)))); int visits = 0;
            g.ForEachWithin(P(100, 100, 10), P(102, 100, 10), P(100, 102, 10), 32, (_, _, _, _, _) => visits++);
            var near = g.NearestPoint(P(100, 100, 10), 32);
            return (visits == 1 && near.Triangle == 0 && MathF.Abs(near.Distance - 10) < 1e-3f, $"visits {visits}, nearest {near.Triangle} at {near.Distance}");
        });
    }

    // ---- Many threads on one object: each thread runs every query in its own rotation; each answer must equal the one-at-a-time answer.
    static (int Wrong, string First) ManyThreads<TQ>(int threads, int rounds, TQ[] queries, string[] expected, Func<TQ, string> ask)
    {
        int wrong = 0; string first = ""; var gate = new object();
        var start = new Barrier(threads);
        var workers = Enumerable.Range(0, threads).Select(w => new Thread(() =>
        {
            try
            {
                start.SignalAndWait();
                for (int round = 0; round < rounds; round++)
                    for (int k = 0; k < queries.Length; k++)
                    {
                        int i = (k + w * queries.Length / threads + round) % queries.Length;
                        string got = ask(queries[i]);
                        if (got != expected[i]) lock (gate) { wrong++; if (first.Length == 0) first = $"; first at query {i}: got {got} expected {expected[i]}"; }
                    }
            }
            catch (Exception ex) { lock (gate) { wrong++; if (first.Length == 0) first = $"; THREW {ex.GetType().Name}: {ex.Message}"; } }
        }) { IsBackground = true }).ToArray();
        foreach (var t in workers) t.Start();
        // A thread stuck on a corrupted table fails the case instead of hanging the proof.
        var deadline = DateTime.UtcNow.AddSeconds(45);
        foreach (var t in workers)
            if (!t.Join(TimeSpan.FromTicks(Math.Max(0, (deadline - DateTime.UtcNow).Ticks)))) lock (gate) { wrong++; first = "; a thread did not finish in 45 s"; }
        lock (gate) return (wrong, first);
    }

    static void ManyThreadGrid(Action<string, bool, string> check)
    {
        var soup = TieSoup(4303, 400); var queries = TieQueries(4304, 400);
        void Same(string name, Func<TriangleGrid> build) => Case(check, name, () =>
        {
            var alone = build(); var shared = build();
            var expected = queries.Select(q => Answer(alone, q)).ToArray();
            var (wrong, first) = ManyThreads(8, 12, queries, expected, q => Answer(shared, q));
            return (wrong == 0, $"{wrong} of {12 * queries.Length * 8} answers differ{first}");
        });
        Same("AX-34 one fixed-cell grid, 8 threads, a soup full of ties: every answer equals the one-at-a-time answer, picks and normals (red before claude/accel-contact-kernel)", () => TriangleGrid.Build(soup));
        Same("AX-35 one coarsened adaptive grid, 8 threads: the same (red before claude/accel-contact-kernel)", () => TriangleGrid.BuildAdaptive(soup, 32f, 1));
    }

    // ---- MeshIntersection.Query on single triangles reaches Classify, which is private; the relation names are the wire vocabulary.
    static void RelationCases(Action<string, bool, string> check)
    {
        void Relation(string name, (Vector3, Vector3, Vector3) a, (Vector3, Vector3, Vector3) b, string relation) => Case(check, name, () =>
        {
            var ab = MeshIntersection.Query(Tris(a), Tris(b)); var ba = MeshIntersection.Query(Tris(b), Tris(a));
            return (ab.Status == "ok" && ab.SurfaceRelation == relation && ba.SurfaceRelation == relation, $"{ab.Status} {ab.SurfaceRelation} / {ba.SurfaceRelation}; expected {relation} both ways");
        });
        var a0 = (P(0, 0, 0), P(4, 0, 0), P(0, 4, 0));
        Relation("AX-40 shared edge, not coplanar (a hinge): touching", a0, (P(0, 0, 0), P(4, 0, 0), P(0, 0, 4)), "touching");
        Relation("AX-41 shared edge given the other way round, not coplanar: touching", a0, (P(4, 0, 0), P(0, 0, 0), P(0, 0, 4)), "touching");
        Relation("AX-42 shared edge, coplanar, on opposite sides of it: touching, no area shared", a0, (P(0, 0, 0), P(4, 0, 0), P(0, -4, 0)), "touching");
        Relation("AX-43 shared edge, coplanar, on the same side (a fold): overlap, which an edge-adjacent shortcut must not call touching", a0, (P(0, 0, 0), P(4, 0, 0), P(4, 4, 0)), "coplanar_overlap");
        Relation("AX-44 one shared vertex, the second triangle pierces the first's interior: crossing", a0, (P(0, 0, 0), P(1, 1, 1), P(1, 1, -1)), "crossing");
        Relation("AX-45 one shared vertex, otherwise apart: touching", a0, (P(0, 0, 0), P(-4, 0, 0), P(0, 0, 4)), "touching");
        Relation("AX-46 identical triangles: overlap", a0, a0, "coplanar_overlap");
        Relation("AX-47 no shared vertex, a clean piercing: crossing", a0, (P(1, 1, -1), P(1, 1, 1), P(2, 1, 1)), "crossing");
        Relation("AX-48 apart: separated", a0, (P(0, 0, 9), P(4, 0, 9), P(0, 4, 9)), "separated");
    }

    // ---- Solid validation: read through a query against a far closed box, where only the first mesh can fail.
    static void SolidCases(Action<string, bool, string> check)
    {
        var far = OutwardBox(P(1000, 1000, 1000), P(1010, 1010, 1010));
        void Solid(string name, TriangleSoup soup, string status, string? reason, string containment) => Case(check, name, () =>
        {
            var r = MeshIntersection.Query(soup, far);
            var swapped = MeshIntersection.Query(far, soup);
            return (r.Status == status && r.Reason == reason && r.Containment == containment && r.SurfaceRelation == "separated" && swapped.Reason == reason,
                    $"{r.Status} '{r.Reason}' {r.Containment} {r.SurfaceRelation}, swapped '{swapped.Reason}'; expected {status} '{reason}' {containment}");
        });
        var tetra = Tetra(P(0, 0, 0), 8);
        Solid("AX-50 closed box with coplanar neighbouring faces is a valid solid: no self-touch reported, apart from the other", OutwardBox(P(0, 0, 0), P(8, 8, 8)), "ok", null, "none");
        Solid("AX-51 closed tetrahedron: valid", tetra, "ok", null, "none");
        Solid("AX-52 a flap on a tetrahedron's edge (a fan of three faces on one edge): open or non-manifold",
              Join(tetra, Soup([P(0, 0, 0), P(8, 0, 0), P(4, -6, -6)], 0, 1, 2)), "ok", "containment_requires_closed_manifold_meshes", "not_applicable_open_mesh");
        Solid("AX-53 two tetrahedra sharing one edge (a fan of four): open or non-manifold", Join(tetra, BelowEdge()), "ok", "containment_requires_closed_manifold_meshes", "not_applicable_open_mesh");
        Solid("AX-54 two tetrahedra pinched at one vertex: every edge used twice, but two fans at that vertex", Join(tetra, Mirror(tetra)), "unknown", "nonmanifold_vertex_link", "unknown");
        Solid("AX-55 tetrahedron with one face wound against its neighbours", Soup(tetra.Vertices, 0, 1, 2, 0, 1, 3, 0, 3, 2, 1, 2, 3), "unknown", "inconsistent_surface_orientation", "unknown");
        Solid("AX-56 two interpenetrating tetrahedra in one mesh: self-crossing", Join(tetra, Tetra(P(2, 2, 2), 8)), "unknown", "self_intersecting_or_self_touching_mesh", "unknown");
        Solid("AX-57 a closed mesh whose two faces on one edge fold over each other: an edge-adjacent shortcut must still find the overlap", FoldedPyramid(), "unknown", "self_intersecting_or_self_touching_mesh", "unknown");
        Case(check, "AX-58 a tetrahedron inside a box: a_inside_b one way, b_inside_a the other", () =>
        {
            var inner = Tetra(P(10, 10, 10), 4); var outer = OutwardBox(P(0, 0, 0), P(64, 64, 64));
            var ab = MeshIntersection.Query(inner, outer); var ba = MeshIntersection.Query(outer, inner);
            return (ab.Containment == "a_inside_b" && ba.Containment == "b_inside_a" && ab.SurfaceRelation == "separated", $"{ab.Containment} / {ba.Containment}, {ab.SurfaceRelation}");
        });
    }

    static TriangleSoup Mirror(TriangleSoup s) => Flip(new(s.Vertices.Select(v => -v).ToArray(), s.Indices));

    // A second tetrahedron on the far side of Tetra(0, 8)'s edge from (0,0,0) to (8,0,0).
    static TriangleSoup BelowEdge() =>
        Soup([P(0, 0, 0), P(8, 0, 0), P(4, -8, 0), P(4, 0, -8)], 0, 1, 2, 0, 3, 1, 0, 2, 3, 1, 3, 2);

    // A, B, E and F lie in z = 0 and the faces ABE and BAF overlap there; every edge is used twice, consistently, and the apex gives it volume.
    static TriangleSoup FoldedPyramid() =>
        Soup([P(0, 0, 0), P(4, 0, 0), P(2, 2, 0), P(2, 4, 0), P(2, 1, -5)], 0, 1, 2, 1, 0, 3, 2, 1, 4, 0, 2, 4, 3, 0, 4, 1, 3, 4);

    // ---- One contact index asked in any order: an answer must not depend on the cache's state. The fixture for AX-59..61 and AX-63.
    static void ContactIndexOrder(Action<string, bool, string> check, bool manyThreads)
    {
        var mod = new ModKey("AxProof", ModType.Plugin);
        FormKey K(uint id) => new(mod, id);
        Aabb Cube(float at, float size) => new(new Vec3(at, at, 0), new Vec3(at + size, at + size, size), false);
        RefGeometry Geo(Aabb b) => new(b, ContactIndex.BoxSoup(b), ContactIndex.BoxSoup(b), 'o', 'o', HostUsable: true, Array.Empty<(string, string)>(), false);
        var geo = new Dictionary<FormKey, RefGeometry>();
        for (uint i = 0; i < 24; i++) geo[K(0x800 + i)] = Geo(Cube(-20 - i, 40 + 2 * i));
        RefGeometry? G(FormKey k) => geo.TryGetValue(k, out var g) ? g : null;
        Snap At(float x, float y, float z) => new(0, new Vec3(x, y, z), K(0xC0), false, false, FormKey.Null);
        var pairs = new List<(FormKey Dep, uint DepBase, Snap DepAt, FormKey Host, uint HostBase, Snap HostAt)>();
        for (uint d = 0; d < 24; d++)
            for (uint h = 0; h < 24; h += 3)
                pairs.Add((K(0x1000 + d * 24 + h), 0x800 + d, At(d, 2 * h, 40 + 2 * h), K(0xA00 + h), 0x800 + h, At(0, 0, 0)));
        var queries = pairs.ToArray();
        string Ask(ContactIndex c, (FormKey Dep, uint DepBase, Snap DepAt, FormKey Host, uint HostBase, Snap HostAt) p)
        {
            var r = c.PairContact(p.Dep, 0, K(p.DepBase), p.DepAt, p.Host, 0, K(p.HostBase), p.HostAt);
            return r is null ? "none" : $"{Hex(r.Contact.FacingDistance)} {r.Contact}";
        }
        void Same(string name, long budget, int threads) => Case(check, name, () =>
        {
            var alone = new ContactIndex(G, long.MaxValue);
            var expected = queries.Select(p => Ask(alone, p)).ToArray();
            var shared = new ContactIndex(G, budget);
            var (wrong, first) = ManyThreads(threads, 6, queries, expected, p => Ask(shared, p));
            return (wrong == 0, $"{wrong} of {6 * queries.Length * threads} answers differ{first}");
        });
        if (!manyThreads)
        {
            Same("AX-59 control: one contact index, one thread, pairs asked in rotated orders: the answers do not depend on the order", long.MaxValue, 1);
            Same("AX-63 control: the same with the smallest cache: eviction changes no answer", CacheBudget.MinimumBytes, 1);
            return;
        }
        Same("AX-60 one contact index, 8 threads, a roomy cache: every pair answers as it does alone (waits on claude/accel-contact-parallel)", long.MaxValue, 8);
        Same("AX-61 one contact index, 8 threads, the smallest cache: eviction under load changes no answer (waits on claude/accel-contact-parallel)", CacheBudget.MinimumBytes, 8);
    }

    static void ManyThreadPreparedMesh(Action<string, bool, string> check)
    {
        Case(check, "AX-62 prepared meshes shared by 8 threads: every query answers as it does alone, invalid meshes and memo hits included (waits on claude/accel-overlap-parallel)", () =>
        {
            var tetra = Tetra(P(0, 0, 0), 8);
            var soups = new[] { tetra, OutwardBox(P(0, 0, 0), P(4, 4, 4)), Join(tetra, Mirror(tetra)), Join(tetra, Tetra(P(2, 2, 2), 8)),
                                OutwardBox(P(100, 0, 0), P(120, 20, 20)), Tetra(P(105, 5, 5), 4), FoldedPyramid(), Tris((P(0, 0, 0), P(4, 0, 0), P(0, 4, 0))) };
            var pairs = (from a in Enumerable.Range(0, soups.Length) from b in Enumerable.Range(0, soups.Length) select (a, b)).ToArray();
            var expected = pairs.Select(p => MeshIntersection.Query(soups[p.a], soups[p.b]).ToString()).ToArray();
            var prepared = soups.Select(s => MeshIntersection.PreparedMesh.Create(s)).ToArray();
            var (wrong, first) = ManyThreads(8, 20, pairs, expected, p => MeshIntersection.Query(prepared[p.a], prepared[p.b]).ToString());
            return (wrong == 0, $"{wrong} of {20 * pairs.Length * 8} answers differ{first}");
        });
    }
}
