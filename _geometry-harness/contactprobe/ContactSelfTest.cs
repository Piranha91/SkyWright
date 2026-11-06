using HarnessShared;

// contactprobe --self-test: the emit's class and the physical predicate (ContactCredit.cs), each against an answer
// worked out by hand. The predicate's re-measure over a written order (ContactCredit.Predicate) is proofprobe's ct-synth
// case PD-C3. Exit 0 when every case passes.
static class ContactSelfTest
{
    public static int Run()
    {
        int pass = 0, fail = 0;
        void Check(string name, bool ok, string detail)
        {
            if (ok) pass++; else fail++;
            Console.WriteLine($"   {(ok ? "PASS" : "FAIL"),-5} {name,-92} {detail}");
        }
        Console.WriteLine("contactprobe --self-test: the emit's class and the physical predicate (ContactCredit)");

        // PD-C1: the predicate as an emitted record states it, at eps = 4.
        const float eps = 4f;
        var un = ContactCredit.Physical(false, 100f, 0f, eps, "a table");
        var sunk = ContactCredit.Physical(true, 0f, 4.1f, eps, "a table");
        var atEps = ContactCredit.Physical(true, 4.0f, 0f, eps, "a table");
        var past = ContactCredit.Physical(true, 4.1f, 0f, eps, "a table");
        var penAtEps = ContactCredit.Physical(true, 50f, 4.0f, eps, "a table");
        Check("PD-C1 the winner arm not measured: Unmeasured", un.State == PhysicalState.Unmeasured, un.Detail);
        Check("PD-C1 sunk 4.1 u at eps 4: Gone (it rests in something)", sunk.State == PhysicalState.Gone, sunk.Detail);
        Check("PD-C1 touching at exactly eps (4.0 u): Gone: eps is inclusive", atEps.State == PhysicalState.Gone, atEps.Detail);
        Check("PD-C1 nearest thing 4.1 u away at eps 4: Holds (it floats)", past.State == PhysicalState.Holds, past.Detail);
        Check("PD-C1 penetration of exactly eps with nothing else within eps: Holds", penAtEps.State == PhysicalState.Holds, penAtEps.Detail);

        // PD-C2: the emit's class.
        var M = DependentMotion.Fixed;
        Check("PD-C2 Fixed + Stranded + ModMover: emitted", ContactCredit.Emitted(M, ContactVerdict.Stranded, DependencyPopulation.ModMover), "");
        Check("PD-C2 Fixed + Orphaned + UssepMover: emitted", ContactCredit.Emitted(M, ContactVerdict.Orphaned, DependencyPopulation.UssepMover), "");
        Check("PD-C2 Movable + Stranded + ModMover: not emitted (clutter the engine lets fall)",
              !ContactCredit.Emitted(DependentMotion.Movable, ContactVerdict.Stranded, DependencyPopulation.ModMover), "");
        Check("PD-C2 Unknown motion + Stranded: not emitted", !ContactCredit.Emitted(DependentMotion.Unknown, ContactVerdict.Stranded, DependencyPopulation.ModMover), "");
        Check("PD-C2 Fixed + Sunk: not emitted (not the class)", !ContactCredit.Emitted(M, ContactVerdict.Sunk, DependencyPopulation.ModMover), "");
        Check("PD-C2 Fixed + ReSupported: not emitted", !ContactCredit.Emitted(M, ContactVerdict.ReSupported, DependencyPopulation.ModMover), "");
        Check("PD-C2 Fixed + Orphaned + BethesdaFloor: withheld", !ContactCredit.Emitted(M, ContactVerdict.Orphaned, DependencyPopulation.BethesdaFloor), "");
        // Pinned as today's behaviour: Program.cs's emit comment says "a mod or USSEP mover", the code emits this population
        // too. The question is filed by the reviewer (Gate A Q5 / review L5); this case changes only when it is decided.
        Check("PD-C2 Fixed + Stranded + VanillaMoverUnderModEdge: emitted (pinned; comment and code disagree, question filed)",
              ContactCredit.Emitted(M, ContactVerdict.Stranded, DependencyPopulation.VanillaMoverUnderModEdge), "");

        // CM-4, CM-5: the bounded mesh store (MeshGeometryReader.cs). A 1-byte budget evicts every other mesh on each insert, so a
        // mesh asked for again is decoded again from its file. The file is rewritten (same length, other bytes) between a first
        // decode and a reload in this process: the reload must refuse. The decoder is told "first" once per mesh, never on a reload.
        var dir = Path.Combine(Path.GetTempPath(), "contactprobe-selftest-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            string a = Path.Combine(dir, "a.nif"), b = Path.Combine(dir, "b.nif");
            File.WriteAllBytes(a, new byte[] { 10, 20, 30 });
            File.WriteAllBytes(b, new byte[] { 40, 50, 60 });
            int firstCalls = 0, allCalls = 0;
            RefGeometry? Decode(string path, byte[] bytes, bool first)
            {
                allCalls++; if (first) firstCalls++;
                var box = new HousecarlCore.Aabb(new HousecarlCore.Vec3(0, 0, 0), new HousecarlCore.Vec3(bytes[0], bytes[1], bytes[2]), false);
                return new RefGeometry(box, ContactIndex.BoxSoup(box), ContactIndex.BoxSoup(box), 'c', 'c', HostUsable: true, Array.Empty<(string, string)>(), false);
            }
            var reader = new MeshGeometryReader(path => File.Exists(path) ? File.ReadAllBytes(path) : null, Decode);
            reader.Attach(new CacheBudget(1));
            var a1 = reader.Get(a); var b1 = reader.Get(b); var a2 = reader.Get(a); var b2 = reader.Get(b); var a3 = reader.Get(a);
            Check("CM-5 the decoder is told 'first' once per mesh; an evicted mesh is decoded again, never counted again",
                  firstCalls == 2 && allCalls == 5 && reader.Decodes == 5 && reader.Redecodes == 3,
                  $"first {firstCalls}, decodes {allCalls} (reader {reader.Decodes}, re-decodes {reader.Redecodes})");
            Check("CM-5 a mesh decoded again gives the same geometry as its first decode",
                  a1 is not null && a2 is not null && a3 is not null && a1.Box.Equals(a2.Box) && a1.Box.Equals(a3.Box) && b1!.Box.Equals(b2!.Box) && !a1.Box.Equals(b1.Box),
                  $"a {a1?.Box} / {a2?.Box} / {a3?.Box}");
            File.WriteAllBytes(a, new byte[] { 11, 20, 30 });          // same length, other bytes, mid-run
            reader.Get(b);                                              // evicts a
            string? refusal = null;
            try { reader.Get(a); } catch (MeshChangedOnDiskException ex) { refusal = ex.Message; }
            Check("CM-4 a mesh whose bytes changed on disk between its first decode and a reload refuses in one sentence",
                  refusal is not null && refusal.Contains("changed on disk during the run"), refusal ?? "(no refusal: the changed bytes were decoded)");

            // 470-472: the store a mode chooses (MeshGeometryReader.Store). The modes whose index memoises every base's geometry
            // (--empty-space and the other non-default modes) decode each mesh once and share it, as before the bounded pass;
            // a 1-byte budget must not make them decode a mesh twice and keep both copies. The default pass stays bounded.
            File.WriteAllBytes(a, new byte[] { 10, 20, 30 });
            int MeshDecodes(string path, List<string> log) => log.Count(p => p == path);
            var keepLog = new List<string>();
            var keep = new MeshGeometryReader(path => File.ReadAllBytes(path), (path, bytes, first) => { keepLog.Add(path); return Decode(path, bytes, first); });
            keep.Store(bounded: false, new CacheBudget(1));
            var k1 = keep.Get(a); keep.Get(b); var k2 = keep.Get(a); keep.Get(b); var k3 = keep.Get(a);
            Check("471 a memoising mode's store decodes each mesh once whatever the budget (a, b, a, b, a: 2 decodes) and returns one object",
                  keep.Decodes == 2 && keep.Redecodes == 0 && MeshDecodes(a, keepLog) == 1 && ReferenceEquals(k1, k2) && ReferenceEquals(k1, k3),
                  $"decodes {keep.Decodes} (again {keep.Redecodes}), a decoded {MeshDecodes(a, keepLog)}x, same object {ReferenceEquals(k1, k3)}");

            var mod = Mutagen.Bethesda.Plugins.ModKey.FromNameAndExtension("SelfTest.esp");
            Mutagen.Bethesda.Plugins.FormKey baseA = new(mod, 0x801), baseB = new(mod, 0x802), baseC = new(mod, 0x803);
            var meshOf = new Dictionary<Mutagen.Bethesda.Plugins.FormKey, string> { [baseA] = a, [baseB] = a, [baseC] = b };
            var idxLog = new List<string>();
            var idxReader = new MeshGeometryReader(path => File.ReadAllBytes(path), (path, bytes, first) => { idxLog.Add(path); return Decode(path, bytes, first); });
            var cidx = new ContactIndex(k => idxReader.Get(meshOf[k]), cacheBudgetBytes: 1, memoiseGeometry: true);
            idxReader.Store(bounded: false, cidx.Budget);
            var gA = cidx.GeomOf(baseA); cidx.GeomOf(baseC); var gB = cidx.GeomOf(baseB);
            Check("470 a memoising index: two bases sharing one mesh, another mesh asked between them on a 1-byte budget, hold one object decoded once",
                  gA is not null && ReferenceEquals(gA, gB) && MeshDecodes(a, idxLog) == 1,
                  $"same object {ReferenceEquals(gA, gB)}, mesh a decoded {MeshDecodes(a, idxLog)}x");

            var bndLog = new List<string>();
            var bnd = new MeshGeometryReader(path => File.ReadAllBytes(path), (path, bytes, first) => { bndLog.Add(path); return Decode(path, bytes, first); });
            bnd.Store(bounded: true, new CacheBudget(1));
            var n1 = bnd.Get(a); bnd.Get(b); var n2 = bnd.Get(a);
            Check("472 control: the default pass's store stays bounded: an evicted mesh is decoded again (a, b, a: 3 decodes, 1 again) with the same geometry",
                  bnd.Decodes == 3 && bnd.Redecodes == 1 && n1 is not null && n2 is not null && n1.Box.Equals(n2.Box),
                  $"decodes {bnd.Decodes} (again {bnd.Redecodes})");

            // CP-1..CP-4: the host loop's shared stores under eight threads, on a 1-byte budget so every insert evicts.
            var parLog = new List<(string Path, bool First)>();
            var par = new MeshGeometryReader(path => File.ReadAllBytes(path), (path, bytes, first) => { parLog.Add((path, first)); return Decode(path, bytes, first); });
            var parBudget = new CacheBudget(1);
            par.Store(bounded: true, parBudget);
            var parBoxes = new System.Collections.Concurrent.ConcurrentBag<bool>();
            var refBoxes = new Dictionary<string, RefGeometry?> { [a] = bnd.Get(a), [b] = bnd.Get(b) };
            Parallel.For(0, 4000, new ParallelOptions { MaxDegreeOfParallelism = 8 }, i =>
            {
                var path = i % 2 == 0 ? a : b;
                parBoxes.Add(par.Get(path)?.Box.Equals(refBoxes[path]!.Box) == true);
            });
            Check("CP-1 eight threads on one mesh store: every answer equals the sequential one", parBoxes.Count == 4000 && parBoxes.All(x => x), $"{parBoxes.Count(x => x)} of {parBoxes.Count}");
            Check("CP-2 eight threads on one mesh store: each mesh's first decode happens once",
                  parLog.Count(x => x.First && x.Path == a) == 1 && parLog.Count(x => x.First && x.Path == b) == 1 && par.Decodes == parLog.Count,
                  $"decodes {par.Decodes}, again {par.Redecodes}");

            int asked = 0;
            var once = new ContactIndex(k => { Interlocked.Increment(ref asked); return idxReader.Get(meshOf[k]); }, cacheBudgetBytes: 1, memoiseGeometry: true);
            Parallel.For(0, 2000, new ParallelOptions { MaxDegreeOfParallelism = 8 }, _ => { once.Summary(baseA); once.GeomOf(baseA); });
            Check("CP-3 eight threads on one base: its geometry is asked for once", asked == 1, $"asked {asked}x");

            var skipA = new ContactSkips { BoxesApart = 2, DepBodilessDoor = 3 };
            skipA.Add(new ContactSkips { BoxesApart = 5, HostVolume = 7 });
            var timerA = new ContactTimer(); var timerB = new ContactTimer();
            timerA.Best.Calls = 1; timerB.Best.Calls = 2; timerB.CoreShell.Calls = 4;
            timerA.Merge(timerB);
            Check("CP-4 per-thread counters and timers add up when merged",
                  skipA.BoxesApart == 7 && skipA.HostVolume == 7 && skipA.DepBodilessDoor == 3 && timerA.Best.Calls == 3 && timerA.CoreShell.Calls == 4,
                  $"apart {skipA.BoxesApart}, volume {skipA.HostVolume}, best {timerA.Best.Calls}, shell {timerA.CoreShell.Calls}");

            var early = (baseA, 0, baseA); var late = (baseB, 0, baseB);
            Mutagen.Bethesda.Plugins.FormKey LargestOf(bool stable, params (Mutagen.Bethesda.Plugins.FormKey, int, Mutagen.Bethesda.Plugins.FormKey)[] keys)
            {
                var pool = new CacheBudget(1L << 20) { StableLargestTie = stable };
                var tied = new PlacementCache<HousecarlCore.TriangleSoup>(pool, "tied", PlacementCache<HousecarlCore.TriangleSoup>.SoupBytes);
                foreach (var k in keys) tied[k] = HousecarlCore.TriangleSoup.Empty;
                return tied.Largest!.Value.Key.Key;
            }
            Check("CP-5 a tie for the largest entry: by key whichever arrives first when stable, first arrival when not",
                  LargestOf(true, early, late) == baseA && LargestOf(true, late, early) == baseA && LargestOf(false, late, early) == baseB,
                  $"{LargestOf(true, late, early)} / {LargestOf(false, late, early)}");
        }
        finally { Directory.Delete(dir, recursive: true); }

        MemoryRouteCases(Check);

        Console.WriteLine($"contactprobe --self-test: {pass} passed, {fail} failed");
        return fail == 0 ? 0 : 1;
    }

    /// <summary>CR-1..CR-10, CR-12: the memory route (_shared/MemoryRoute.cs) on constructed readings (Piranha's rule, 2026-11-05).</summary>
    static void MemoryRouteCases(Action<string, bool, string> Check)
    {
        const long G = 1_000_000_000;
        var s = new RouteShape(Live: 2_660_000_000, FastThreads: 8, FastBudget: 1L << 30, LeanThreads: 1, LeanBudget: 1L << 30);
        long nf = (long)Math.Ceiling(MemoryRoute.FastNeed(s) * (1 + MemoryRoute.Margin));
        var others = new List<ProcessUse> { new("python", 4242, 26 * G), new("firefox", 77, 3_100_000_000) };
        var none = new List<ProcessUse>();
        MemoryReadings Free(long? limit = null, string src = "none") => new(64 * G, 30 * G, 3 * G, limit, src);
        MemoryReadings Held() => new(64 * G, 2 * G, 3 * G, null, "none");
        static string D(RouteDecision d) => $"{d.Case} {d.Route?.ToString() ?? "-"} exit {d.ExitCode} threads {d.Threads}; {d.Line} | {d.Warning} | {d.Refusal}";

        var free = MemoryRoute.Decide(Free(), s, RouteRequest.Auto, others);
        Check("CR-8 memory free: fast, on the fast threads, exit 0", free.Case == MemoryCase.Free && free.Route == RouteName.Fast && free.ExitCode == 0
              && free.Threads == s.FastThreads && free.Warning is null, D(free));

        var c1 = MemoryRoute.Decide(Held(), s, RouteRequest.Auto, others);
        Check("CR-1 contended, auto: the warning names the largest other program (name, pid, GB); lean as the unattended default",
              c1.Case == MemoryCase.Contended && c1.Route == RouteName.Lean && c1.UnattendedDefault && c1.ExitCode == 0
              && c1.Warning is { } w1 && w1.Contains("python") && w1.Contains("4242") && w1.Contains("26.0 GB")
              && c1.Line.Contains("unattended default") && c1.Threads == s.LeanThreads && c1.Budget == s.LeanBudget
              && c1.HeapLimit is { } h1 && h1 >= MemoryRoute.LeanNeed(s) && h1 <= 5 * G, D(c1));

        var c2 = MemoryRoute.Decide(Held(), s, RouteRequest.Ask, others);
        Check("CR-2 contended, ask: stop before the host loop, exit 5, one sentence naming the program and both answers",
              c2.Case == MemoryCase.Contended && c2.Route is null && c2.ExitCode == 5 && c2.Refusal is { } r2 && !r2.Contains('\n')
              && r2.Contains("python") && r2.Contains("--memory-route fast") && r2.Contains("--memory-route lean"), D(c2));

        var c3 = MemoryRoute.Decide(Held(), s, RouteRequest.Fast, others);
        Check("CR-3 contended, fast (the user accepts): fast, saying free memory is short", c3.Route == RouteName.Fast && c3.ExitCode == 0
              && c3.Threads == s.FastThreads && c3.Line.Contains("short") && !c3.UnattendedDefault, D(c3));

        var c4 = MemoryRoute.Decide(Free(nf - 1, "GC heap hard limit"), s, RouteRequest.Auto, others);
        Check("CR-4 absent by a GC heap limit: lean, the limit named, and no advice to close programs (closing frees nothing)",
              c4.Case == MemoryCase.Absent && c4.Route == RouteName.Lean && c4.Warning is null && c4.Line.Contains("GC heap hard limit")
              && !c4.Line.Contains("clos", StringComparison.OrdinalIgnoreCase) && !c4.UnattendedDefault, D(c4));

        var c5 = MemoryRoute.Decide(Free(nf - 1, "job object"), s, RouteRequest.Auto, none);
        Check("CR-5 absent by a job object's limit: lean, the job object named", c5.Case == MemoryCase.Absent && c5.Route == RouteName.Lean
              && c5.Line.Contains("job object"), D(c5));

        var c6 = MemoryRoute.Decide(new MemoryReadings(nf - 1, nf - 1, 0, null, "none"), s, RouteRequest.Auto, others);
        Check("CR-6 absent: the machine's physical memory is below the fast need: lean", c6.Case == MemoryCase.Absent && c6.Route == RouteName.Lean
              && c6.Warning is null, D(c6));

        var c7 = MemoryRoute.Decide(Free(2 * G, "job object"), s, RouteRequest.Auto, others);
        Check("CR-7 nothing fits (the limit is below even the lean need): refuse, exit 2, one sentence saying what to try",
              c7.Case == MemoryCase.NothingFits && c7.Route is null && c7.ExitCode == 2 && c7.Refusal is { } r7 && !r7.Contains('\n')
              && r7.Contains("lean") && r7.Contains("limit"), D(c7));

        var atEdge = MemoryRoute.Decide(new MemoryReadings(64 * G, nf - 3 * G, 3 * G, null, "none"), s, RouteRequest.Auto, others);
        var past = MemoryRoute.Decide(new MemoryReadings(64 * G, nf - 3 * G - 1, 3 * G, null, "none"), s, RouteRequest.Auto, others);
        Check("CR-9 boundary: free + own == need with margin is fast; one byte less is contended; own private counts as available",
              atEdge.Case == MemoryCase.Free && past.Case == MemoryCase.Contended, $"{D(atEdge)} // {D(past)}");

        // The recorded readings on the contact-baseline order (GATE-A 3.3 and Gate B D-1): threads, live, budget, peak private (GB).
        var recorded = new (int T, double L, double B, double Peak)[]
            { (1, 2.66, 1.07, 8.15), (8, 2.66, 1.07, 8.92), (3, 3.32, 6.44, 15.43), (8, 3.32, 11.68, 21.47), (8, 3.32, 11.29, 21.27), (16, 3.32, 11.51, 21.67),
              (8, 2.66, 1.074, 9.32), (8, 2.66, 1.074, 9.14), (8, 3.32, 2.147, 11.39), (8, 3.32, 2.147, 11.28), (8, 3.32, 4.295, 15.03),
              (8, 3.32, 4.295, 16.11), (8, 3.32, 11.96, 21.41), (8, 3.32, 11.76, 21.03) };
        var fits = recorded.Select(x => (x, est: MemoryRoute.FastNeed(new RouteShape((long)(x.L * G), x.T, (long)(x.B * G), 1, 1L << 30)) * (1 + MemoryRoute.Margin) / G)).ToList();
        Check("CR-10 the fast need with its margin covers each recorded peak and over-states none by more than 30 %",
              fits.All(f => f.est >= f.x.Peak && f.est <= f.x.Peak * 1.30),
              string.Join("; ", fits.Select(f => $"T{f.x.T} {f.est:0.00}/{f.x.Peak:0.00}")));

        // CR-21: the warning names the fewest programs, largest first, whose memory covers the shortfall (reviewer, 2026-11-06).
        var three = new List<ProcessUse> { new("c", 3, 1 * G), new("a", 1, 4 * G), new("b", 2, 3 * G) };
        long shortfall = nf - 5 * G;   // Held(): 2 GB free + 3 GB own
        var w3 = MemoryRoute.Decide(Held(), s, RouteRequest.Auto, three).Warning ?? "";
        Check("CR-21 the warning names the fewest programs, largest first, that cover the shortfall: python alone; then a and b, not c",
              c1.Warning is { } w21 && w21.Contains("python") && !w21.Contains("firefox")
              && shortfall > 4 * G && shortfall <= 7 * G && w3.Contains("a (pid 1") && w3.Contains("b (pid 2") && !w3.Contains("c (pid 3"),
              $"shortfall {shortfall / 1e9:0.00} GB; {c1.Warning} // {w3}");

        // CR-23: the lean need against every floor run on the contact-baseline order (Gate B D-2, hold C): live, lean budget, GC heap
        // limit, completed. A limit the lean route did not survive must be refused up front, one that it survived must run lean.
        var floor = new (string Run, double L, double B, double Limit, bool Completed)[]
        {
            ("cand GC 4 GiB", 2.52, 0.859, 4.295, false), ("cand job 5.5 GiB", 2.52, 0.886, 4.429, true),
            ("cand job 6 GiB", 2.60, 0.966, 4.832, true), ("prod GC 4.5 GiB, 1G", 2.66, 1.074, 4.832, true), ("prod GC 5 GiB, 1G", 2.66, 1.074, 5.369, true),
        };
        var floorOut = floor.Select(f => (f, d: MemoryRoute.Decide(new MemoryReadings(64 * G, 30 * G, 4 * G, (long)(f.Limit * G), "GC heap hard limit"),
            new RouteShape((long)(f.L * G), 8, 2L << 30, 8, (long)(f.B * G)), RouteRequest.Auto, none))).ToList();
        Check("CR-23 the lean need agrees with every floor run: refused where the lean route ran out, lean where it completed",
              floorOut.All(x => x.f.Completed ? x.d.Route == RouteName.Lean : x.d.Case == MemoryCase.NothingFits),
              string.Join("; ", floorOut.Select(x => $"{x.f.Run}: need {x.d.NeedLean / 1e9:0.000} vs {x.f.Limit:0.000} -> {x.d.Case}/{x.d.Route?.ToString() ?? "-"}")));

        // CR-24: an explicit shape that does not fit says so, and that dropping the flags lets the route run lean.
        var e3 = MemoryRoute.Decide(new MemoryReadings(64 * G, 30 * G, 3 * G, 3 * G, "GC heap hard limit"), s with { LeanBudget = 2L << 30 }, RouteRequest.Auto, others, explicitShape: true);
        Check("CR-24 an explicit --threads/--cache-budget that does not fit: the sentence blames the flags and says dropping them runs lean",
              e3.ExitCode == 2 && e3.Refusal is { } r24 && r24.Contains("--threads/--cache-budget") && r24.Contains("without them")
              && !r24.Contains("even on the lean route"), D(e3));

        var e1 = MemoryRoute.Decide(Held(), s, RouteRequest.Auto, others, explicitShape: true);
        var e2 = MemoryRoute.Decide(Free(nf - 1, "GC heap hard limit"), s, RouteRequest.Auto, others, explicitShape: true);
        Check("CR-12 an explicit --threads/--cache-budget is kept as asked: contended warns and keeps it; absent names the limit and keeps it",
              e1.Route == RouteName.Fast && e1.Threads == s.FastThreads && e1.Warning is not null && e1.Line.Contains("kept as asked")
              && e2.Route == RouteName.Fast && e2.Line.Contains("kept as asked") && e2.Line.Contains("GC heap hard limit"), $"{D(e1)} // {D(e2)}");
    }
}
