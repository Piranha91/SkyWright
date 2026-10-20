using System.Diagnostics;
using System.Numerics;
using HarnessShared;
using HousecarlCore;

// overlapprobe --self-test, overlap acceleration (Track P): the pair-loop timers observe and never decide (P0).
static class AccelSelfTest
{
    static TriangleSoup Box(Vector3 min, Vector3 max)
    {
        var v = new[]
        {
            new Vector3(min.X, min.Y, min.Z), new Vector3(max.X, min.Y, min.Z), new Vector3(max.X, max.Y, min.Z), new Vector3(min.X, max.Y, min.Z),
            new Vector3(min.X, min.Y, max.Z), new Vector3(max.X, min.Y, max.Z), new Vector3(max.X, max.Y, max.Z), new Vector3(min.X, max.Y, max.Z),
        };
        return new(v, new[] { 0, 2, 1, 0, 3, 2, 4, 5, 6, 4, 6, 7, 0, 1, 5, 0, 5, 4, 1, 2, 6, 1, 6, 5, 2, 3, 7, 2, 7, 6, 3, 0, 4, 3, 4, 7 });
    }

    static TriangleSoup Combine(params TriangleSoup[] soups)
    {
        var v = new List<Vector3>(); var ix = new List<int>();
        foreach (var s in soups) { int o = v.Count; v.AddRange(s.Vertices); ix.AddRange(s.Indices.Select(i => i + o)); }
        return new(v.ToArray(), ix.ToArray());
    }

    /// <summary>A closed grid-tessellated box: n x n quads per face, so the BVH has several levels.</summary>
    static TriangleSoup GridBox(Vector3 min, Vector3 max, int n)
    {
        var v = new List<Vector3>(); var ix = new List<int>();
        void Face(Func<float, float, Vector3> at, bool flip)
        {
            int o = v.Count;
            for (int j = 0; j <= n; j++) for (int i = 0; i <= n; i++) v.Add(at(i / (float)n, j / (float)n));
            for (int j = 0; j < n; j++) for (int i = 0; i < n; i++)
            {
                int a = o + j * (n + 1) + i, b = a + 1, c = a + n + 1, d = c + 1;
                if (flip) ix.AddRange(new[] { a, c, b, b, c, d }); else ix.AddRange(new[] { a, b, c, b, d, c });
            }
        }
        Vector3 L(float x, float y, float z) => new(min.X + (max.X - min.X) * x, min.Y + (max.Y - min.Y) * y, min.Z + (max.Z - min.Z) * z);
        Face((s, t) => L(s, t, 0), true); Face((s, t) => L(s, t, 1), false);
        Face((s, t) => L(s, 0, t), false); Face((s, t) => L(s, 1, t), true);
        Face((s, t) => L(0, s, t), true); Face((s, t) => L(1, s, t), false);
        return new(v.ToArray(), ix.ToArray());
    }

    /// <summary>Named pairs reaching every phase and outcome: crossing, touching, separated, nested (containment),
    /// an open mesh, two shells, a degenerate triangle (preparation refusal).</summary>
    internal static List<(string Name, TriangleSoup A, TriangleSoup B)> Pairs()
    {
        var unit = Box(Vector3.Zero, Vector3.One);
        var tri = new TriangleSoup(new[] { new Vector3(5, 5, 5), new Vector3(6, 5, 5), new Vector3(5, 6, 5) }, new[] { 0, 1, 2 });
        var degenerate = new TriangleSoup(new[] { Vector3.Zero, Vector3.One, new Vector3(2, 2, 2) }, new[] { 0, 1, 2 });
        return new()
        {
            ("crossing", unit, Box(new(0.5f, 0.5f, 0.5f), new(1.5f, 1.5f, 1.5f))),
            ("touching", unit, Box(new(1, 0, 0), new(2, 1, 1))),
            ("separated", unit, Box(new(3, 0, 0), new(4, 1, 1))),
            ("nested grid", GridBox(Vector3.Zero, new(10, 10, 10), 6), GridBox(new(3, 3, 3), new(6, 6, 6), 4)),
            ("nested reversed", GridBox(new(3, 3, 3), new(6, 6, 6), 4), GridBox(Vector3.Zero, new(10, 10, 10), 6)),
            ("open mesh separated", unit, tri),
            ("two shells, one inside", Combine(Box(new(20, 0, 0), new(21, 1, 1)), Box(new(2, 2, 2), new(3, 3, 3))), GridBox(Vector3.Zero, new(10, 10, 10), 5)),
            ("degenerate triangle", degenerate, unit),
        };
    }

    public static void Run(ProbeSelfTest t)
    {
        // P0-1: a timed query returns exactly what an untimed one does, at every cap that changes the outcome.
        var timer = new MeshQueryTimer();
        int compared = 0, mismatched = 0; long queries = 0;
        var mismatches = new List<string>();
        foreach (var (name, a, b) in Pairs())
        {
            var full = MeshIntersection.Query(a, b);
            for (long cap = 1; cap <= full.TrianglePairsTested + 1; cap = cap < 64 ? cap + 1 : cap + Math.Max(1, cap / 8))
            {
                var plain = MeshIntersection.Query(a, b, 1e-5, cap);
                var timed = MeshIntersection.Query(a, b, 1e-5, cap, default, timer);
                compared++; queries++;
                if (plain != timed) { mismatched++; if (mismatches.Count < 3) mismatches.Add($"{name} cap {cap}: {plain} vs {timed}"); }
            }
        }
        t.Check("P0-1 a timed query decides exactly what an untimed one does (every pair, caps 1..tested+1)",
                compared > 100 && mismatched == 0 && timer.Queries == queries, $"{compared} compared, {mismatched} mismatched; {string.Join("; ", mismatches)}");
        t.Check("P0-2 the timer saw every phase, budget exhaustion and a preparation refusal",
                timer.PrepareTicks > 0 && timer.BvhTicks > 0 && timer.SurfaceTicks > 0 && timer.ValidateTicks > 0 && timer.ContainmentTicks > 0
                && timer.SurfaceUnits > 0 && timer.ValidateUnits > 0 && timer.ContainmentUnits > 0 && timer.BudgetExhausted > 0 && timer.PrepareRefused > 0,
                $"units {timer.SurfaceUnits}/{timer.ValidateUnits}/{timer.ContainmentUnits}, exhausted {timer.BudgetExhausted}, refused {timer.PrepareRefused}");

        // P0-3: phase time never exceeds the pair's own wall, measured outside.
        var pt = new PairTimer();
        long outside = 0;
        foreach (var (_, a, b) in Pairs())
        {
            long s = Stopwatch.GetTimestamp();
            SurfaceQueryOutcome.QueryPlaced(a, null, b, null, 1e-5, 2_000_000, TimeSpan.FromSeconds(60), default, pt);
            outside += Stopwatch.GetTimestamp() - s;
        }
        var c = pt.Core;
        long phases = pt.PairFrameTicks + c.PrepareTicks + c.BvhTicks + c.SurfaceTicks + c.ValidateTicks + c.ContainmentTicks;
        t.Check("P0-3 phase ticks <= the pair timer's wall <= the wall measured outside; one pair counted per call",
                phases <= pt.WallTicks && pt.WallTicks <= outside && pt.Pairs == Pairs().Count && pt.Paths[(int)PairPath.World] == pt.Pairs,
                $"phases {phases}, timer wall {pt.WallTicks}, outside {outside}, pairs {pt.Pairs}");

        // P0-4: the BVH node count rises with every prepared mesh.
        long before = MeshIntersection.BvhNodesBuilt;
        MeshIntersection.Query(Box(Vector3.Zero, Vector3.One), Box(new(3, 0, 0), new(4, 1, 1)));
        t.Check("P0-4 BVH nodes built are counted (two 12-triangle meshes: at least 6 nodes)",
                MeshIntersection.BvhNodesBuilt - before >= 6, $"{MeshIntersection.BvhNodesBuilt - before}");

        // P2-1: a prepared pair decides exactly what the soup pair does at every cap, with each side's validation memo filled
        // first by a full-budget query (so a hit must charge the units the validation consumed) and with it empty.
        int compared2 = 0, mismatched2 = 0, memoHits = 0;
        var bad2 = new List<string>();
        foreach (var (name, a, b) in Pairs())
        {
            var full = MeshIntersection.Query(a, b);
            var pa = MeshIntersection.PreparedMesh.Create(a); var pb = MeshIntersection.PreparedMesh.Create(b);
            var warm = MeshIntersection.Query(pa, pb);
            compared2++; if (warm != full) { mismatched2++; if (bad2.Count < 3) bad2.Add($"{name} full: {full} vs {warm}"); }
            if (pa.MemoisedValidations > 0) memoHits++;
            // and one prepared pair queried from the smallest cap up, so a validation the budget cut short comes first
            var ua = MeshIntersection.PreparedMesh.Create(a); var ub = MeshIntersection.PreparedMesh.Create(b);
            for (long cap = 1; cap <= full.TrianglePairsTested + 1; cap = cap < 64 ? cap + 1 : cap + Math.Max(1, cap / 8))
            {
                var soup = MeshIntersection.Query(a, b, 1e-5, cap);
                var hit = MeshIntersection.Query(pa, pb, 1e-5, cap);
                var cold = MeshIntersection.Query(MeshIntersection.PreparedMesh.Create(a), MeshIntersection.PreparedMesh.Create(b), 1e-5, cap);
                var up = MeshIntersection.Query(ua, ub, 1e-5, cap);
                compared2 += 3;
                if (up != soup) { mismatched2++; if (bad2.Count < 3) bad2.Add($"{name} cap {cap} ascending: {soup} vs {up}"); }
                if (hit != soup) { mismatched2++; if (bad2.Count < 3) bad2.Add($"{name} cap {cap} memo: {soup} vs {hit}"); }
                if (cold != soup) { mismatched2++; if (bad2.Count < 3) bad2.Add($"{name} cap {cap} cold: {soup} vs {cold}"); }
            }
        }
        t.Check("P2-1 a prepared pair decides exactly what the soup pair does (memo filled first, empty, and filled from the smallest cap up; every pair, caps 1..tested+1)",
                compared2 > 200 && mismatched2 == 0 && memoHits >= 3, $"{compared2} compared, {mismatched2} mismatched, {memoHits} pairs with a memo; {string.Join("; ", bad2)}");

        // P2-2: the sweep's cache, bounded so small it evicts, gives each placed pair the soup query's result.
        // One cache so small it evicts every mesh, one large enough to reuse each: both must leave every result unchanged.
        var tiny = new PreparedMeshCache(1); var roomy = new PreparedMeshCache(1L << 30);
        int compared3 = 0, mismatched3 = 0;
        var pairs = Pairs();
        foreach (var (_, a, b) in pairs.Concat(pairs))
        {
            var plain = SurfaceQueryOutcome.QueryPlaced(a, null, b, null, 1e-5, 2_000_000, TimeSpan.FromSeconds(60));
            foreach (var cache in new[] { tiny, roomy })
            {
                var viaCache = SurfaceQueryOutcome.QueryPlaced(a, null, b, null, 1e-5, 2_000_000, TimeSpan.FromSeconds(60), default, null, cache);
                compared3++; if (plain != viaCache) mismatched3++;
            }
        }
        t.Check("P2-2 a prepared-mesh cache, evicting every mesh or reusing each, leaves every placed pair's result unchanged",
                compared3 == 4 * pairs.Count && mismatched3 == 0 && tiny.Evictions >= 2 * pairs.Count && roomy.Hits > 0 && roomy.Evictions == 0,
                $"{compared3} compared, {mismatched3} mismatched; tiny hits {tiny.Hits}, evictions {tiny.Evictions}; roomy hits {roomy.Hits}, misses {roomy.Misses}");
    }
}
