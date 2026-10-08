using HarnessShared;
using HousecarlCore;
using Mutagen.Bethesda.Plugins;

/// <summary>Known answers for the byte-budgeted per-placement contact caches (_shared/PlacementCache.cs, 2026-10-05): the key
/// carries the base, the bytes are counted and bounded, evicted entries rebuild to the same answer, and the automatic budget
/// and fit check say what they claim. The Judge-level eviction answer lives beside ct-synth in Program.cs.</summary>
static class CacheBudgetProof
{
    static readonly ModKey M = new("CbProof", ModType.Plugin);
    static FormKey K(uint id) => new(M, id);
    static Aabb Box(float x0, float y0, float z0, float x1, float y1, float z1) => new(new Vec3(x0, y0, z0), new Vec3(x1, y1, z1), false);
    static RefGeometry Geo(Aabb b) => new(b, ContactIndex.BoxSoup(b), ContactIndex.BoxSoup(b), 'o', 'o', HostUsable: true, Array.Empty<(string, string)>(), false);
    static Snap At(float x, float y, float z) => new(0, new Vec3(x, y, z), K(0xC0), false, false, FormKey.Null);

    public static void Run(Action<string, bool, string> Check)
    {
        var shortT = K(0x801); var tallT = K(0x802); var bowl = K(0x803);
        var geo = new Dictionary<FormKey, RefGeometry>
        {
            [shortT] = Geo(Box(-50, -50, 0, 50, 50, 40)),
            [tallT] = Geo(Box(-50, -50, 0, 50, 50, 80)),
            [bowl] = Geo(Box(-10, -10, 0, 10, 10, 8)),
        };
        RefGeometry? G(FormKey k) => geo.TryGetValue(k, out var g) ? g : null;
        var host = K(0x900); var dep = K(0x901);
        var hostAt = At(0, 0, 0); var bowlOnTall = At(0, 0, 80);

        // T2: a base swap with no position step — one (reference, snap), two bases. Each base must read its own geometry.
        var warm = new ContactIndex(G, long.MaxValue);
        var viaShort = warm.PairContact(dep, 0, bowl, bowlOnTall, host, 0, shortT, hostAt);
        var viaTallWarm = warm.PairContact(dep, 0, bowl, bowlOnTall, host, 0, tallT, hostAt);
        var viaTallFresh = new ContactIndex(G, long.MaxValue).PairContact(dep, 0, bowl, bowlOnTall, host, 0, tallT, hostAt);
        Check("cache-budget T2: a base swap without a move reads the new base's geometry — the cache key carries the base (bowl resting on the tall table, 40 u above the short one)",
              viaShort is not null && viaTallWarm is not null && viaTallFresh is not null
           && viaTallWarm.Contact == viaTallFresh.Contact && MathF.Abs(viaTallWarm.Contact.FacingDistance) < 1f && viaShort.Contact.FacingDistance > 30f,
              $"short {viaShort?.Contact.FacingDistance:0.0}, tall after short {viaTallWarm?.Contact.FacingDistance:0.0}, tall fresh {viaTallFresh?.Contact.FacingDistance:0.0}");

        // T3: bytes counted. One pair = one host grid + one dependent soup, each with the entry overhead.
        var one = new ContactIndex(G, long.MaxValue);
        one.PairContact(dep, 0, bowl, bowlOnTall, host, 0, tallT, hostAt);
        var gridSoup = SpatialOverlap.TransformSoup(geo[tallT].HostSoup, hostAt.Pos, hostAt.Rot, hostAt.Scale);
        var countedGrid = TriangleGrid.BuildAdaptive(gridSoup);
        long gridBytes = countedGrid.ApproxBytes;
        long fixedBytes = TriangleGrid.Build(gridSoup).ApproxBytes;
        long soupBytes = PlacementCache<TriangleSoup>.SoupBytes(SpatialOverlap.TransformSoup(geo[bowl].DepSoup, bowlOnTall.Pos, bowlOnTall.Rot, bowlOnTall.Scale));
        long expect = 2 * CacheBudget.EntryOverhead + gridBytes + soupBytes;
        Check("cache-budget T3a: the pool's bytes after one pair are the grid's ApproxBytes + the soup's bytes + two entry overheads",
              gridBytes > 0 && soupBytes > 0 && countedGrid.Sizing is { GroupingPower: 0, TargetMet: true }
           && countedGrid.Sizing.LegacyOmittedIds.Length == 0 && countedGrid.Sizing.OverflowIds.Length == 0
           && gridBytes == fixedBytes + 256 // Approximate sizing-record/empty-array charge, with no query-buffer branch.
           && one.Budget.Bytes == expect && one.Budget.PeakBytes == expect,
              $"pool {one.Budget.Bytes}, expected {expect} (grid {gridBytes}, soup {soupBytes})");

        long gib = 1L << 30;
        long ceilingTarget = new ContactIndex(G, 4 * gib).GridBucketTarget, cappedTarget = new ContactIndex(G, (long)(1.6 * gib)).GridBucketTarget;
        long floorTarget = new ContactIndex(G, CacheBudget.MinimumBytes).GridBucketTarget;
        Check("cache-budget T3d: a host grid keeps fixed cells up to half the cache budget (2 GiB at the 4 GiB ceiling), coarsened only under a tight budget, never targeted below 64 MiB",
              ceilingTarget == 2 * gib && cappedTarget == (long)(1.6 * gib) / 2 && floorTarget == 64L << 20,
              $"ceiling {ceilingTarget}, 1.6 GiB budget {cappedTarget}, minimum budget {floorTarget}");

        // T3b: the bound holds — a budget of about three entries, forty placements looked up twice; never over the limit after an
        // insert (no entry here is larger than the limit), evictions happen, and every answer equals the unbounded index's.
        long limit = 3 * (gridBytes + CacheBudget.EntryOverhead);
        var small = new ContactIndex(G, limit); var big = new ContactIndex(G, long.MaxValue);
        bool within = true, same = true;
        for (int pass = 0; pass < 2; pass++)
            for (int i = 0; i < 40; i++)
            {
                var h = At(i * 300, 0, 0); var d = At(i * 300, 0, 80);
                var a = small.PairContact(K(0xA00 + (uint)i), 0, bowl, d, K(0xB00 + (uint)i), 0, tallT, h);
                var b = big.PairContact(K(0xA00 + (uint)i), 0, bowl, d, K(0xB00 + (uint)i), 0, tallT, h);
                within &= small.Budget.Bytes <= small.Budget.Limit;
                same &= a is not null && b is not null && a.Contact == b.Contact;
            }
        Check("cache-budget T3b: a three-entry budget stays within its limit, evicts, rebuilds, and answers as the unbounded cache does",
              within && same && small.Budget.Evictions("hostGrids") > 0 && small.Budget.Misses("hostGrids") > big.Budget.Misses("hostGrids")
           && small.Budget.OversizeInserts == 0 && big.Budget.Evictions("hostGrids") == 0,
              $"within {within}, same {same}; small: {small.Budget.Report()}; big: {big.Budget.Report()}");

        // T3c: an entry larger than the whole budget is kept (the pair needs it) and counted, never refused.
        var tiny = new ContactIndex(G, 1);
        var t = tiny.PairContact(dep, 0, bowl, bowlOnTall, host, 0, tallT, hostAt);
        Check("cache-budget T3c: an entry larger than the budget is kept for the pair and counted as oversize",
              t is not null && viaTallFresh is not null && t.Contact == viaTallFresh.Contact && tiny.Budget.OversizeInserts >= 2,
              tiny.Budget.Report());

        // T4: the automatic budget, the parser, and the fit check.
        Check("cache-budget T4e: auto never exceeds the 4 GiB ceiling - 20 % of 64 GiB would be 12.8 GiB",
              CacheBudget.Auto(64 * gib) == 4 * gib && CacheBudget.AutoCeilingBytes == 4 * gib && CacheBudget.Auto(20 * gib) == 4 * gib,
              $"64 GiB -> {CacheBudget.Auto(64 * gib)}, 20 GiB -> {CacheBudget.Auto(20 * gib)}");
        Check("cache-budget T4a: auto = 20 % of what the runtime may use, never below the 64 MiB minimum",
              CacheBudget.Auto(16 * gib) == (long)(16 * gib * 0.20) && CacheBudget.Auto(8 * gib) == (long)(8 * gib * 0.20)
           && CacheBudget.Auto(1L << 20) == CacheBudget.MinimumBytes && CacheBudget.MinimumBytes == 64L << 20,
              $"16 GiB -> {CacheBudget.Auto(16 * gib)}, 8 GiB -> {CacheBudget.Auto(8 * gib)}, 1 MiB -> {CacheBudget.Auto(1L << 20)}");
        Check("cache-budget T4d: the free-memory term is 40 % of (free now - live), never below the minimum",
              CacheBudget.AutoFromFree(30 * gib, 2 * gib) == (long)(28 * gib * 0.40) && CacheBudget.AutoFromFree(2 * gib, 2 * gib) == CacheBudget.MinimumBytes,
              $"30/2 GiB -> {CacheBudget.AutoFromFree(30 * gib, 2 * gib)}, 2/2 GiB -> {CacheBudget.AutoFromFree(2 * gib, 2 * gib)}");
        Check("cache-budget T4b: sizes parse with binary suffixes; nonsense does not",
              CacheBudget.Parse("2G") == 2 * gib && CacheBudget.Parse("512m") == 512L << 20 && CacheBudget.Parse("1048576") == 1L << 20
           && CacheBudget.Parse("1.5G") == (long)(1.5 * gib) && CacheBudget.Parse("lots") is null && CacheBudget.Parse("-1G") is null,
              $"2G {CacheBudget.Parse("2G")}, 512m {CacheBudget.Parse("512m")}");
        var refusal = CacheBudget.FitRefusal(10 * gib, 8 * gib, "--radius off lowers the need");
        Check("cache-budget T4c: the fit check refuses live data that cannot fit in the memory free now, naming both numbers and the option that lowers the need; it passes when it fits",
              refusal is not null && refusal.Contains("10.00 GiB") && refusal.Contains("8.00 GiB") && refusal.Contains("--radius off")
           && CacheBudget.FitRefusal(1 * gib, 16 * gib, "") is null,
              refusal ?? "(no refusal)");

        // T5: literal known readings through the pure selector AND the constructor/refresh seams.
        var freeWins = CacheBudget.SelectAutomatic(64 * gib, 3 * gib, 2 * gib);
        var runtimeWins = CacheBudget.SelectAutomatic(8 * gib, 30 * gib, 2 * gib);
        var ceilingWins = CacheBudget.SelectAutomatic(64 * gib, 30 * gib, 2 * gib);
        var floorWins = CacheBudget.SelectAutomatic(64 * gib, 1L << 20, 2L << 20);
        var unavailable = CacheBudget.SelectAutomatic(8 * gib, null, 2 * gib);
        long freeExpected = (long)(gib * 0.40), runtimeExpected = (long)(8 * gib * 0.20);
        Check("cache-budget T5a: the free term wins with literal runtime 64/free 3/managed 2 GiB readings",
              freeWins.SelectedBytes == freeExpected && freeWins.WinningTerm == "free-now-minus-managed"
           && freeWins.RuntimeAvailableBytes == 64 * gib && freeWins.AvailablePhysicalBytes == 3 * gib
           && freeWins.CurrentManagedBytes == 2 * gib && freeWins.ManagedMeasurement == "current uncollected managed bytes",
              freeWins.Describe());
        Check("cache-budget T5b: the runtime term wins with runtime 8/free 30/managed 2 GiB readings",
              runtimeWins.SelectedBytes == runtimeExpected && runtimeWins.WinningTerm == "runtime", runtimeWins.Describe());
        Check("cache-budget T5c: abundant runtime/free memory selects the existing 4 GiB ceiling",
              ceilingWins.SelectedBytes == 4 * gib && ceilingWins.WinningTerm == "ceiling", ceilingWins.Describe());
        Check("cache-budget T5d: free below managed selects the existing 64 MiB floor",
              floorWins.SelectedBytes == 64L << 20 && floorWins.WinningTerm == "free-now-minus-managed", floorWins.Describe());
        Check("cache-budget T5e: an unreadable physical reading selects the runtime term and names the missing free term",
              unavailable.SelectedBytes == runtimeExpected && unavailable.FreeTermBytes is null && unavailable.AvailablePhysicalBytes is null
           && unavailable.Refusal is null && unavailable.WinningTerm.StartsWith("runtime; free physical memory unreadable")
           && unavailable.Describe().Contains("free physical unavailable"),
              unavailable.Describe());
        Check("cache-budget T5l: MemAvailable is read from /proc/meminfo text in bytes; absent or malformed lines read as unavailable",
              CacheBudget.ParseMemAvailable("MemTotal:       16315664 kB\nMemFree:         1048576 kB\nMemAvailable:    8388608 kB\n") == 8L << 30
           && CacheBudget.ParseMemAvailable("MemTotal: 16315664 kB\nMemFree: 1048576 kB\n") is null
           && CacheBudget.ParseMemAvailable("MemAvailable: lots kB\n") is null
           && CacheBudget.ParseMemAvailable("MemAvailable: 8388608 MB\n") is null,
              $"{CacheBudget.ParseMemAvailable("MemAvailable:    8388608 kB")}");

        int constructorReads = 0;
        var automatic = new ContactIndex(G, automaticSelection: () => { constructorReads++; return freeWins; });
        var automaticContact = automatic.PairContact(dep, 0, bowl, bowlOnTall, host, 0, tallT, hostAt);
        Check("cache-budget T5f: default construction uses the injected free-wins selection and keeps the real pair answer",
              constructorReads == 1 && automatic.Budget.Limit == freeExpected
           && automatic.AutomaticBudgetSelection == freeWins && automatic.EffectiveAutomaticBudgetSelection == freeWins
           && automaticContact is not null && viaTallFresh is not null && automaticContact.Contact == viaTallFresh.Contact,
              automatic.AutomaticBudgetReport());
        var runtimeOnly = new ContactIndex(G, automaticSelection: () => unavailable);
        Check("cache-budget T5g: default construction with an unreadable physical reading runs on the runtime term and reports it",
              runtimeOnly.Budget.Limit == runtimeExpected && runtimeOnly.AutomaticBudgetSelection == unavailable
           && runtimeOnly.AutomaticBudgetReport().Contains("free physical memory unreadable"),
              runtimeOnly.AutomaticBudgetReport());

        var refreshing = new ContactIndex(G, automaticSelection: () => ceilingWins);
        bool lowered = refreshing.RefreshAutomaticBudget(() => freeWins);
        Check("cache-budget T5h: automatic refresh lowers the actual pool and records the limiting reading",
              lowered && refreshing.Budget.Limit == freeExpected && refreshing.AutomaticBudgetSelection == freeWins
           && refreshing.EffectiveAutomaticBudgetSelection == freeWins, refreshing.AutomaticBudgetReport());
        bool raised = refreshing.RefreshAutomaticBudget(() => ceilingWins);
        Check("cache-budget T5i: a later larger reading never raises the pool; the earlier limit remains identified",
              !raised && refreshing.Budget.Limit == freeExpected && refreshing.AutomaticBudgetSelection == ceilingWins
           && refreshing.EffectiveAutomaticBudgetSelection == freeWins
           && refreshing.AutomaticBudgetReport().Contains("retained earlier limit"), refreshing.AutomaticBudgetReport());
        bool refreshLowered = refreshing.RefreshAutomaticBudget(() => unavailable);
        Check("cache-budget T5j: a larger runtime-only refresh never raises the pool; the earlier free-term limit remains identified",
              !refreshLowered && refreshing.Budget.Limit == freeExpected && refreshing.AutomaticBudgetSelection == unavailable
           && refreshing.EffectiveAutomaticBudgetSelection == freeWins,
              $"limit {refreshing.Budget.Limit}; latest [{refreshing.AutomaticBudgetSelection?.Describe()}]; effective [{refreshing.EffectiveAutomaticBudgetSelection?.Describe()}]");

        int explicitReads = 0;
        CacheBudget.AutomaticSelection ForbiddenRead() { explicitReads++; throw new InvalidOperationException("explicit budget invoked an automatic reader"); }
        ContactIndex? explicitSmall = null, explicitLarge = null;
        bool explicitKept = false; string explicitError = "";
        try
        {
            explicitSmall = new ContactIndex(G, 1, ForbiddenRead);
            explicitLarge = new ContactIndex(G, long.MaxValue, ForbiddenRead);
            explicitKept = !explicitSmall.RefreshAutomaticBudget() && !explicitLarge.RefreshAutomaticBudget()
                && !explicitSmall.RefreshAutomaticBudget(ForbiddenRead) && !explicitLarge.RefreshAutomaticBudget(ForbiddenRead);
        }
        catch (InvalidOperationException ex) { explicitError = ex.Message; }
        Check("cache-budget T5k: explicit 1/long.MaxValue remain exact; constructor and refresh never call automatic readers",
              explicitKept && explicitReads == 0 && explicitSmall?.Budget.Limit == 1 && explicitLarge?.Budget.Limit == long.MaxValue
           && explicitSmall?.AutomaticBudgetSelection is null && explicitLarge?.AutomaticBudgetSelection is null,
              $"reader calls {explicitReads}, limits {explicitSmall?.Budget.Limit}/{explicitLarge?.Budget.Limit}; {explicitError}");
    }
}
