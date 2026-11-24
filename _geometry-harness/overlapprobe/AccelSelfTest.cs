using System.Diagnostics;
using System.Numerics;
using HarnessShared;
using HousecarlCore;
using SkyWright.Engine;
using Mutagen.Bethesda.Plugins;

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
        // RF-F0: the --reverse-of fallback fixture (ReverseFallbackFixture.cs) that tests/test_reverse_reconcile.py runs on.
        var rf = ReverseFallbackFixture.Write(t.Root);
        t.Check("RF-F0 the fixture writes Skyrim.esm, CrA.esp and the render box under rf",
                rf.Count == 2 && rf.All(File.Exists) && File.Exists(Path.Combine(t.Root, "rf", "mods", "Skyrim", "meshes", ReverseFallbackFixture.MeshRel)),
                string.Join(", ", rf.Select(Path.GetFileName)));

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

        // P2-3: a soup whose preparation throws gives the soup path's own outcome through the cache, on either side and after a
        // refusing side, and the cache keeps nothing for it (the next pair prepares it again).
        var thrower = Box(new(50, 50, 50), new(51, 51, 51));
        var good = Box(new(50.5f, 50.5f, 50.5f), new(52, 52, 52));
        var refusing = Pairs().Last().A;   // the degenerate-triangle soup
        var cache4 = new PreparedMeshCache(1L << 30);
        int compared4 = 0, mismatched4 = 0; var bad4 = new List<string>();
        MeshIntersection.TestPrepareHook = s => { if (ReferenceEquals(s, thrower)) throw new InvalidOperationException("test: preparation throws"); };
        try
        {
            foreach (var (x, y) in new[] { (thrower, good), (good, thrower), (refusing, thrower), (thrower, good) })
            {
                var plain = SurfaceQueryOutcome.QueryPlaced(x, null, y, null, 1e-5, 2_000_000, TimeSpan.FromSeconds(60));
                var viaCache = SurfaceQueryOutcome.QueryPlaced(x, null, y, null, 1e-5, 2_000_000, TimeSpan.FromSeconds(60), default, null, cache4);
                compared4++; if (plain != viaCache) { mismatched4++; bad4.Add($"{plain} vs {viaCache}"); }
            }
        }
        finally { MeshIntersection.TestPrepareHook = null; }
        t.Check("P2-3 a preparation that throws is the pair's own 'mesh query failed' outcome through the cache too, and is not kept",
                compared4 == 4 && mismatched4 == 0 && !cache4.Holds(thrower) && cache4.Holds(good) && cache4.Misses == 5,
                $"{compared4} compared, {mismatched4} mismatched, misses {cache4.Misses}, holds thrower {cache4.Holds(thrower)}; {string.Join("; ", bad4)}");

        P3(t);
        P4(t);
    }

    // P4: the sweep on many threads gives one thread's rows, counters, callbacks and cache file, in one thread's order.
    static void P4(ProbeSelfTest t)
    {
        var space = FormKey.Factory("000D74:Skyrim.esm");
        var models = new[] { Box(Vector3.Zero, new(10, 10, 10)), GridBox(Vector3.Zero, new(14, 14, 14), 5), GridBox(Vector3.Zero, new(6, 6, 6), 3),
                             new TriangleSoup(new[] { Vector3.Zero, new Vector3(9, 0, 0), new Vector3(0, 9, 0) }, new[] { 0, 1, 2 }) };
        var candidates = new List<SurfaceOverlapCandidate>();
        for (int i = 0; i < 90; i++)
        {
            // Every tenth reference repeats an earlier one's model and pose exactly, so repeated cache keys are reached.
            int place = i % 10 == 9 ? i - 9 : i;
            var model = models[place % models.Length];
            var pos = new Vec3(place * 2.5f, (place % 7) * 3f, (place % 3) * 4f);
            var snap = new Snap(i % 3, pos, FormKey.Null, false, false, space);
            var world = SpatialOverlap.TransformSoup(model, pos, default, 1f);
            candidates.Add(new(new FormKey(ModKey.FromNameAndExtension("P4.esp"), (uint)(0x800 + i)), snap, FormKey.Null, SpatialOverlap.SoupBounds(world), world, 'm', model));
        }
        var closure = new[] { new HashSet<int> { 0 }, new HashSet<int> { 0, 1 }, new HashSet<int> { 2 } };

        (List<string> Rows, string Tallies, PairResultCache Cache, PairTimer Same, PairTimer Cross, SurfacePairSweep Sweep) Sweep(int threads, long preparedBytes, PairResultCache? cache)
        {
            var rows = new List<string>(); var same = new PairTimer(); var cross = new PairTimer();
            var prepared = preparedBytes > 0 ? new PreparedMeshCache(preparedBytes) : null;
            var results = cache ?? new PairResultCache("p4-probe", "p4-core", 60);
            long progress = 0;
            var s = OverlapIndex.SurfacePairs(candidates, closure, 1e-5, 2_000_000, TimeSpan.FromSeconds(60),
                onProgress: n => { if (n != ++progress) rows.Add($"progress out of order at {n}"); },
                retainSameResults: true, sameTimer: same, crossTimer: cross,
                onPair: (a, b, q, path, _) => rows.Add($"{a.Key} {b.Key} {q} {path}"), prepared: prepared, results: results, threads: threads);
            string H(IEnumerable<SurfaceOverlapHit> hits) => string.Join(";", hits.Select(h => $"{h.A.Key}/{h.B.Key}/{h.Query}/{h.Declared}"));
            string tallies = string.Join(",", s.PairsChecked, s.SameChecked, s.SameIntersects, s.SameUnknown, s.CrossDeclaredChecked, s.CrossDeclaredIntersects,
                s.CrossUndeclaredChecked, s.CrossUndeclaredIntersects, s.CrossUnknown, string.Join("+", s.SameByDomain.Select(d => $"{d.Key}={d.Value}")),
                H(s.SameResults), H(s.CrossResults), H(s.CrossHits), same.Pairs, cross.Pairs, same.Core.Queries, cross.Core.Queries,
                string.Join("/", same.Paths), string.Join("/", cross.Paths), same.CacheHits, cross.CacheHits, same.Timeouts, cross.Timeouts,
                same.Slowest.Count, cross.Slowest.Count, results.Hits, results.Misses, results.NotStored, results.Stored);
            return (rows, tallies, results, same, cross, s);
        }

        string FileLines(PairResultCache c)
        {
            var file = Path.Combine(Path.GetTempPath(), $"pd-p4-{Environment.ProcessId}-{Guid.NewGuid():N}.gz");
            c.Write(file);
            try
            {
                using var gz = new System.IO.Compression.GZipStream(File.OpenRead(file), System.IO.Compression.CompressionMode.Decompress);
                using var r = new StreamReader(gz);
                return r.ReadToEnd();
            }
            finally { File.Delete(file); }
        }

        var one = Sweep(1, 1L << 30, null);
        foreach (var (threads, bytes) in new[] { (8, 1L << 30), (8, 1L), (2, 0L), (3, 1L << 30) })
        {
            var many = Sweep(threads, bytes, null);
            t.Check($"P4-1 {threads} threads with a {bytes}-byte prepared cache give one thread's rows, tallies and cache file, in order",
                    one.Sweep.PairsChecked > 100 && one.Sweep.CrossHits.Count > 0 && one.Cache.Hits > 0
                    && one.Rows.SequenceEqual(many.Rows) && one.Tallies == many.Tallies && FileLines(one.Cache) == FileLines(many.Cache),
                    $"{one.Rows.Count} vs {many.Rows.Count} rows; {one.Sweep.PairsChecked} pairs, {one.Cache.Hits} cache hits; tallies equal {one.Tallies == many.Tallies}");
        }

        // A worker's time-out is measured again in order: every worker measurement cut short, one thread's rows and tallies all the same.
        using (var gone = new CancellationTokenSource())
        {
            gone.Cancel();
            OverlapIndex.TestWorkerToken = gone.Token;
            try
            {
                var redone = Sweep(8, 1L << 30, null);
                t.Check("P4-5 worker time-outs measured again on a fresh clock give one thread's rows, tallies and cache file",
                        redone.Sweep.Remeasured > 0 && one.Sweep.Remeasured == 0 && one.Rows.SequenceEqual(redone.Rows) && one.Tallies == redone.Tallies && FileLines(one.Cache) == FileLines(redone.Cache),
                        $"measured again {redone.Sweep.Remeasured}, tallies equal {one.Tallies == redone.Tallies}");
            }
            finally { OverlapIndex.TestWorkerToken = null; }
        }

        // The run-ahead window at its smallest still finishes, with one thread's rows.
        int window = OverlapIndex.PairRunAhead;
        OverlapIndex.PairRunAhead = 1;
        try
        {
            var bounded = Sweep(8, 1L << 30, null);
            t.Check("P4-6 a run-ahead window of one pair finishes with one thread's rows and tallies",
                    one.Rows.SequenceEqual(bounded.Rows) && one.Tallies == bounded.Tallies, $"{bounded.Rows.Count} rows");
        }
        finally { OverlapIndex.PairRunAhead = window; }

        // Out of memory stops a many-thread sweep; one thread keeps the shared query's row.
        var starving = candidates[10].WorldSoup;
        MeshIntersection.TestPrepareHook = s => { if (ReferenceEquals(s, starving)) throw new OutOfMemoryException("test: out of memory"); };
        try
        {
            string Starved(int threads)
            {
                try
                {
                    var r = OverlapIndex.SurfacePairs(candidates, closure, 1e-5, 2_000_000, TimeSpan.FromSeconds(60), threads: threads);
                    return r.CrossResults.Concat(r.SameResults).Any(h => h.Query.Reason?.Contains("OutOfMemoryException") == true) ? "row" : $"finished, {r.PairsChecked} pairs";
                }
                catch (OutOfMemoryException) { return "stopped"; }
            }
            string alone = Starved(1), many = Starved(8);
            t.Check("P4-7 out of memory stops a many-thread sweep instead of becoming a row; one thread is unchanged", many == "stopped" && alone == "row", $"one thread: {alone}; eight: {many}");
        }
        finally { MeshIntersection.TestPrepareHook = null; }

        // A second run served by the first run's file: every pair from the cache, the same rows.
        var warmFile = Path.Combine(Path.GetTempPath(), $"pd-p4-{Environment.ProcessId}-warm.gz");
        one.Cache.Write(warmFile);
        var warm = new PairResultCache("p4-probe", "p4-core", 60);
        string? warmWhy = warm.Read(warmFile);
        File.Delete(warmFile);
        var served = Sweep(8, 1L << 30, warm);
        t.Check("P4-2 a sweep served by an earlier run's cache file gives the same rows on many threads",
                warmWhy is null && served.Rows.SequenceEqual(one.Rows) && warm.Misses == 0, $"read {warmWhy ?? "ok"}, misses {warm.Misses}");

        // Cancellation surfaces at the same pair as one thread's sweep, with the same rows before it.
        int Cancelled(int threads)
        {
            using var cts = new CancellationTokenSource();
            int seen = 0;
            try
            {
                OverlapIndex.SurfacePairs(candidates, closure, 1e-5, 2_000_000, TimeSpan.FromSeconds(60), cancellationToken: cts.Token,
                    onPair: (_, _, _, _, _) => { if (++seen == 25) cts.Cancel(); }, threads: threads);
                return -1;
            }
            catch (OperationCanceledException) { return seen; }
        }
        t.Check("P4-3 a cancelled sweep stops at the same pair on one thread and on many", Cancelled(1) == 25 && Cancelled(8) == 25, $"{Cancelled(1)} / {Cancelled(8)}");

        // One prepared mesh, one validation: many threads on one soup share the preparation and the memo.
        var shared = new PreparedMeshCache(1L << 30);
        var soup = GridBox(Vector3.Zero, new(10, 10, 10), 8); var other = Box(new(40, 40, 40), new(41, 41, 41));
        var got = new MeshIntersection.PreparedMesh[16];
        long misses = MeshIntersection.ValidateMemoMisses;
        Parallel.For(0, got.Length, i =>
        {
            got[i] = shared.Get(soup, null);
            MeshIntersection.Query(got[i], got[i].Refused ? got[i] : shared.Get(other, null));
        });
        t.Check("P4-4 threads on one soup prepare it once and share the mesh",
                shared.Misses == 2 && got.All(g => ReferenceEquals(g, got[0])) && got[0].MemoisedValidations == 1 && MeshIntersection.ValidateMemoMisses - misses == 2,
                $"misses {shared.Misses}, memo {got[0].MemoisedValidations}, validations run {MeshIntersection.ValidateMemoMisses - misses}");
    }

    // P3: the pair-result cache (PairResultCache in _shared/OverlapIndex.cs).
    static void P3(ProbeSelfTest t)
    {
        // P3-1: every parameter of the cached call is keyed, recorded in the header, or named as unable to change a stored result.
        var parameters = typeof(SurfaceQueryOutcome).GetMethods().Where(m => m.Name == "QueryPlaced").SelectMany(m => m.GetParameters())
            .Select(p => p.Name!).Distinct().ToList();
        var accounted = PairResultCache.KeyedParameters.Concat(PairResultCache.HeaderParameters).Concat(PairResultCache.Unkeyed.Keys).ToList();
        t.Check("P3-1 every QueryPlaced parameter is in the key, the file header or the named unkeyed list (and nothing else is listed)",
                parameters.Count > 0 && parameters.ToHashSet().SetEquals(accounted) && accounted.Count == accounted.Distinct().Count(),
                $"parameters [{string.Join(", ", parameters)}]; accounted [{string.Join(", ", accounted)}]");

        // Placed members: each fixture soup as a model, placed at a pose; the world soup is that model transformed.
        var rot = new Vec3(0.1f, 0.2f, 0.3f);
        var placed = Pairs().Select(p => (p.Name,
            A: new SurfaceQueryOutcome.Placed(p.A, new Vec3(1000, 2000, 30), rot, 1f), B: new SurfaceQueryOutcome.Placed(p.B, new Vec3(1000, 2000, 30), rot, 1f))).ToList();
        SurfaceQueryOutcome Q(SurfaceQueryOutcome.Placed a, SurfaceQueryOutcome.Placed b, TriangleSoup wa, TriangleSoup wb, PairResultCache? c, PairTimer? timer = null)
            => SurfaceQueryOutcome.QueryPlaced(wa, a, wb, b, 1e-5, 2_000_000, TimeSpan.FromSeconds(60), default, timer, null, c);
        var worlds = placed.Select(p => (p.Name, p.A, p.B, WA: SpatialOverlap.TransformSoup(p.A.Model, p.A.Pos, p.A.Rot, p.A.Scale),
                                         WB: SpatialOverlap.TransformSoup(p.B.Model, p.B.Pos, p.B.Rot, p.B.Scale))).ToList();

        // P3-2: results written by one run and read by the next give every pair the computed outcome and path.
        const string probe = "self-test-probe", core = "self-test-core";
        var cold = new PairResultCache(probe, core, 60);
        var file = Path.Combine(Path.GetTempPath(), $"pd-p3-{Environment.ProcessId}.pair-results.jsonl.gz");
        int compared = 0, mismatched = 0; var bad = new List<string>();
        foreach (var w in worlds) Q(w.A, w.B, w.WA, w.WB, cold);
        cold.Write(file);
        var warm = new PairResultCache(probe, core, 60);
        string? why = warm.Read(file);
        foreach (var w in worlds)
        {
            var tp = new PairTimer(); var tw = new PairTimer();
            var plain = Q(w.A, w.B, w.WA, w.WB, null, tp);
            var hit = Q(w.A, w.B, w.WA, w.WB, warm, tw);
            compared++;
            if (plain != hit || tp.LastPath != tw.LastPath) { mismatched++; if (bad.Count < 3) bad.Add($"{w.Name}: {plain}/{tp.LastPath} vs {hit}/{tw.LastPath}"); }
        }
        t.Check("P3-2 a result written by one run and read by the next equals the computed outcome and path for every pair",
                why is null && compared == worlds.Count && mismatched == 0 && warm.Hits == worlds.Count && warm.Misses == 0 && cold.Stored == worlds.Count,
                $"read {(why ?? "ok")}; {compared} compared, {mismatched} mismatched, hits {warm.Hits}, misses {warm.Misses}, stored {cold.Stored}; {string.Join("; ", bad)}");

        // P3-3..5: the key moves with the mesh bytes and with the pose; a file of another core build is not read.
        var w0 = worlds[0];
        string k0 = warm.Key(w0.WA, w0.A, w0.WB, w0.B, 1e-5, 2_000_000);
        var moved = w0.WA with { Vertices = w0.WA.Vertices.Select((v, i) => i == 0 ? v + new Vector3(0.001f, 0, 0) : v).ToArray() };
        t.Check("P3-3 changing one vertex of a world soup (the mesh bytes as placed) changes the key",
                warm.Key(moved, w0.A, w0.WB, w0.B, 1e-5, 2_000_000) != k0 && warm.Key(w0.WA, w0.A, w0.WB, w0.B, 1e-5, 2_000_000) == k0, "");
        var otherPose = w0.A with { Pos = w0.A.Pos with { X = w0.A.Pos.X + 1 } };
        t.Check("P3-4 changing a member's pose, with the same world soup object, changes the key (PairFrame re-places from the pose)",
                warm.Key(w0.WA, otherPose, w0.WB, w0.B, 1e-5, 2_000_000) != k0, "");
        var otherCore = new PairResultCache(probe, "another-core", 60);
        string? coreWhy = otherCore.Read(file);
        var otherLimit = new PairResultCache(probe, core, 120);
        string? limitWhy = otherLimit.Read(file);
        t.Check("P3-5 a file written by another housecarl-core build, or under another pair time limit, is not read, and says why",
                coreWhy == "another housecarl-core build" && otherCore.ReadEntries == 0 && limitWhy is not null && limitWhy.StartsWith("pair time limit")
                && otherLimit.ReadEntries == 0 && otherCore.Key(w0.WA, w0.A, w0.WB, w0.B, 1e-5, 2_000_000) != k0,
                $"core: {coreWhy}; limit: {limitWhy}");

        // P3-6: a result a time limit cut short is never kept.
        var timed = new PairResultCache(probe, core, 60);
        var big = worlds.First(w => w.Name == "nested grid");
        var cut = SurfaceQueryOutcome.QueryPlaced(big.WA, big.A, big.WB, big.B, 1e-5, 2_000_000, TimeSpan.Zero, default, null, null, timed);
        t.Check("P3-6 a result cut short by the pair time limit is not kept",
                cut.Reason is "cancelled" or "pair time budget exhausted" && timed.NotStored == 1 && timed.Stored == 0, $"{cut.Reason}, not stored {timed.NotStored}");
        try { File.Delete(file); } catch { }
    }
}
