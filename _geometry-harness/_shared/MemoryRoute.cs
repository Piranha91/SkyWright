using System.Runtime.InteropServices;

namespace HarnessShared;

/// <summary>What the user asked of the memory route (--memory-route): auto decides; fast and lean are the user's answer to a
/// contention warning; ask stops at the warning so a session can put the question to its user.</summary>
public enum RouteRequest { Auto, Fast, Lean, Ask }
public enum RouteName { Fast, Lean }
/// <summary>The memory case read before the host loop: free (fast fits), contended (the RAM exists, other programs hold it),
/// absent (a limit or the machine is below the fast route's need), nothing fits (even the lean route does not).</summary>
public enum MemoryCase { Free, Contended, Absent, NothingFits }

/// <summary>The machine and process readings the route is decided from. Bytes throughout. ProcessLimit is the smallest limit on
/// this process (GC heap hard limit or job object), null when none; OwnPrivate is what the process already holds after a
/// compacting collection, counted as available to it.</summary>
public sealed record MemoryReadings(long TotalPhysical, long? AvailablePhysical, long OwnPrivate, long? ProcessLimit, string LimitSource);

/// <summary>Another program and the memory it holds, for the contention warning.</summary>
public sealed record ProcessUse(string Name, int Pid, long Bytes);

/// <summary>Live data at the decision and each route's shape.</summary>
public sealed record RouteShape(long Live, int FastThreads, long FastBudget, int LeanThreads, long LeanBudget);

public sealed record RouteDecision(MemoryCase Case, RouteName? Route, int ExitCode, bool UnattendedDefault, long NeedFast, long NeedLean,
    int Threads, long Budget, long? HeapLimit, string Line, string? Warning, string? Refusal);

/// <summary>Piranha's memory rule (2026-11-05) for contactprobe's host loop: fast whenever the memory exists; when other programs
/// hold it, warn and name them; lean when the user refuses, when nobody can be asked (the unattended default), or when the memory
/// does not exist; one plain sentence when nothing fits. Pure: readings in, decision out.</summary>
public static class MemoryRoute
{
    /// <summary>Margin on the fast route's estimated need: the estimate is fitted on one order and one machine, and repeats of one
    /// configuration peaked up to 1.1 GB apart (Gate B D-1: 4 GiB budget, 15.03 and 16.11 GB). Over-stating it costs speed (lean
    /// is taken where fast might have fitted); under-stating it risks the machine paging.</summary>
    public const double Margin = 0.25;
    /// <summary>Exit code for a run stopped at a contention warning under --memory-route ask.</summary>
    public const int AskExit = 5;
    /// <summary>Memory the lean route's heap limit leaves outside the GC heap (native code, the runtime, file buffers).</summary>
    public const long NativeReserve = 256L << 20;
    /// <summary>The fast route's contact cache ceiling (contactprobe): 2 GiB, measured 2026-11-05 on the contact-baseline order at
    /// 8 threads, two rounds (Gate B D-1): 1 GiB 97.3 / 94.1 s at 8.7 / 8.5 GiB; 2 GiB 86.8 / 87.9 s at 10.6 / 10.5 GiB; 4 GiB 88.2 /
    /// 87.6 s; the old 2 GiB-per-thread automatic budget (11.8-12.0 GB) 88.7 / 85.7 s at 19.9 / 19.6 GiB. Rows identical in all.</summary>
    public const long FastBudgetCeiling = 2L << 30;
    /// <summary>The lean route's host-loop threads (contactprobe): the same 8 as the fast route. Measured 2026-11-05 on the
    /// contact-baseline order under one 5 GiB GC heap limit and a 1 GiB budget (Gate B D-2): 8 threads 112.3 s, 1 thread 211.9 s,
    /// both peaking at 5.139 GiB with identical rows; under 4 GiB both stopped out of memory at 37-38 s. Threads cost no memory
    /// that the limit lets through, so the lean route is the fast threads with a small budget and a heap limit.</summary>
    public const int LeanThreads = 8;
    /// <summary>The lean route's GC heap limit as a multiple of its estimated need, when the machine has more room than that: the
    /// collector then works inside about what the route needs instead of keeping gigabytes of headroom.</summary>
    public const double LeanHeapFactor = 1.25;

    /// <summary>The fast route's need beyond live data and budget, in bytes: collector headroom of the server GC's heaps, grids in
    /// flight per thread, garbage from evicted grids. Fitted by hand on six readings of one order on one 16-thread machine; checked
    /// against eight more (Gate B D-1) under the 25 % margin (CR-10): 4.2 GB + 0.12 GB per thread + 0.10 of the budget.</summary>
    public static long FastExtra(int threads, long budget) => 4_200_000_000L + 120_000_000L * threads + budget / 10;
    public static long FastNeed(RouteShape s) => s.Live + s.FastBudget + FastExtra(s.FastThreads, s.FastBudget);
    /// <summary>What the lean route needs beyond live data and budget, as a share of them: grids in flight, garbage between
    /// collections. Calibrated 2026-11-06 on the contact-baseline order's floor runs (Gate B, CR-23): with live + budget 3.38 GB a
    /// 4.295 GB GC heap limit ran out of memory, with 3.41 GB a 4.429 GB limit completed, and with 3.73 GB a 4.832 GB limit completed,
    /// so the share lies between 0.271 and 0.294; 0.285 is taken. The band is about 0.1 GB wide on one order: a limit inside it can go
    /// either way, and the out-of-memory sentence covers that.</summary>
    public const double LeanExtraShare = 0.285;
    /// <summary>The lean route's need under a GC heap limit (the smallest limit it completes under), at least 64 MiB over live + budget.</summary>
    public static long LeanNeed(RouteShape s) => s.Live + s.LeanBudget + Math.Max(64L << 20, (long)((s.Live + s.LeanBudget) * LeanExtraShare));

    static string Gb(long b) => $"{b / 1e9:0.0} GB";

    /// <summary>The programs to close: the fewest, largest first, whose memory covers <paramref name="shortfall"/> (all of them, at
    /// most <paramref name="n"/>, when none suffices), as "name (pid N, X GB)".</summary>
    public static string Programs(IReadOnlyList<ProcessUse> others, long shortfall, int n = 5)
    {
        var pick = new List<ProcessUse>();
        long covered = 0;
        foreach (var o in others.OrderByDescending(o => o.Bytes))
        {
            if (pick.Count == n || covered >= shortfall) break;
            pick.Add(o); covered += o.Bytes;
        }
        return string.Join(", ", pick.Select(o => $"{o.Name} (pid {o.Pid}, {Gb(o.Bytes)})"));
    }

    public static RouteDecision Decide(MemoryReadings r, RouteShape s, RouteRequest request, IReadOnlyList<ProcessUse> others, bool explicitShape = false)
    {
        long needF = (long)Math.Ceiling(FastNeed(s) * (1 + Margin)), needL = LeanNeed(s);
        long room = (r.AvailablePhysical ?? r.TotalPhysical) + r.OwnPrivate;   // what this process could have now
        long ceiling = Math.Min(r.TotalPhysical, r.ProcessLimit ?? long.MaxValue);   // what it could ever have
        string limitText = r.ProcessLimit is { } lim && lim < r.TotalPhysical ? $"{Gb(lim)} ({r.LimitSource})" : $"{Gb(r.TotalPhysical)} (physical memory)";
        string est = $"estimated need fast {Gb(needF)} (with {Margin:P0} margin), lean {Gb(needL)}; available to this process {Gb(room)}, limit {limitText}";
        RouteDecision Fast(MemoryCase c, string why, string? warning = null, bool kept = false)
            => new(c, RouteName.Fast, 0, false, needF, needL, s.FastThreads, s.FastBudget, null,
                   $"memory route: fast ({CaseWord(c)}) - {why}{(kept ? "; kept as asked (--threads/--cache-budget)" : "")}; {est}", warning, null);
        RouteDecision Lean(MemoryCase c, string why, bool unattended, string? warning = null)
        {
            // Under contention or a machine too small, the lean route's GC works inside what is there; an existing limit stays.
            long? heap = c == MemoryCase.Absent && r.ProcessLimit is not null && r.ProcessLimit < r.TotalPhysical ? null
                       : Math.Max(needL, Math.Min(Math.Min(ceiling, room) - NativeReserve, (long)(needL * LeanHeapFactor)));
            return new(c, RouteName.Lean, 0, unattended, needF, needL, s.LeanThreads, s.LeanBudget, heap,
                       $"memory route: lean ({CaseWord(c)}) - {why}; {s.LeanThreads} thread(s), contact cache {Gb(s.LeanBudget)}"
                       + (heap is { } h ? $", GC heap limit {Gb(h)}" : "") + $"; {est}", warning, null);
        }

        if (needL > ceiling)
        {
            string refusal = explicitShape
                ? $"memory: the asked --threads/--cache-budget need about {Gb(needL)} and this process may use {limitText}; "
                  + "run without them and the memory route takes the lean route, or raise the limit."
                : $"memory: this order needs about {Gb(needL)} even on the lean route and this process may use {limitText}; "
                  + "run it on a machine with more memory, or raise the limit.";
            return new(MemoryCase.NothingFits, null, 2, false, needF, needL, s.LeanThreads, s.LeanBudget, null,
                       $"memory route: none (nothing fits) - {est}", null, refusal);
        }
        if (needF > ceiling)
        {
            string why = r.ProcessLimit is { } pl && pl < r.TotalPhysical && pl < needF
                ? $"the fast route needs {Gb(needF)} and this process may use {limitText}"
                : $"the fast route needs {Gb(needF)} and this machine has {Gb(r.TotalPhysical)}";
            if (explicitShape) return Fast(MemoryCase.Absent, why + $" (limit {limitText})", kept: true);
            return request == RouteRequest.Fast
                ? Lean(MemoryCase.Absent, why + "; --memory-route fast cannot get more than the limit allows", false)
                : Lean(MemoryCase.Absent, why, false);
        }
        if (needF > room)
        {
            string progs = Programs(others, needF - room);
            string warning = $"memory: the fast route needs {Gb(needF)} and {Gb(room)} is free for it; other programs hold the rest"
                           + (progs.Length > 0 ? $" - closing {progs} frees it" : "")
                           + "; rerun with --memory-route fast after closing them, or --memory-route lean to accept the slower run";
            if (explicitShape) return Fast(MemoryCase.Contended, "free memory is short for the asked shape", warning, kept: true);
            switch (request)
            {
                case RouteRequest.Fast:
                    return Fast(MemoryCase.Contended, $"asked for (--memory-route fast) although free memory is short by {Gb(needF - room)}", warning);
                case RouteRequest.Ask:
                    string ask = $"memory: the fast route needs {Gb(needF)} and {Gb(room)} is free"
                               + (progs.Length > 0 ? $"; close {progs}" : "")
                               + " and rerun with --memory-route fast, or rerun with --memory-route lean for the slower run that fits.";
                    return new(MemoryCase.Contended, null, AskExit, false, needF, needL, s.FastThreads, s.FastBudget, null,
                               $"memory route: stopped to ask (contended) - {est}", warning, ask);
                case RouteRequest.Lean:
                    return Lean(MemoryCase.Contended, "asked for (--memory-route lean)", false, warning);
                default:
                    return Lean(MemoryCase.Contended, "other programs hold the memory the fast route needs; lean is the unattended default", true, warning);
            }
        }
        if (request == RouteRequest.Lean && !explicitShape) return Lean(MemoryCase.Free, "asked for (--memory-route lean)", false);
        return Fast(MemoryCase.Free, "the memory is there", kept: explicitShape);
    }

    static string CaseWord(MemoryCase c) => c switch
    {
        MemoryCase.Free => "memory free", MemoryCase.Contended => "contended", MemoryCase.Absent => "memory absent", _ => "nothing fits"
    };
}

/// <summary>The readings MemoryRoute decides from, from the operating system and the runtime. HC_MEMROUTE_TEST_READINGS replaces
/// the machine's readings for known-answer tests ("total=B;avail=B;limit=B;source=TEXT;others=name:pid:B,..."); the route line
/// names it whenever it is used.</summary>
public static class MemoryReadingsReader
{
    public const string TestVariable = "HC_MEMROUTE_TEST_READINGS";
    static readonly HashSet<string> SystemNames = new(StringComparer.OrdinalIgnoreCase)
        { "System", "Idle", "Registry", "Memory Compression", "Secure System", "vmmem", "vmmemWSL" };

    /// <summary>The readings, the other programs by memory held, and the name of where the readings came from.</summary>
    public static (MemoryReadings Readings, IReadOnlyList<ProcessUse> Others, string From) Read(long ownPrivate)
    {
        long gcAvailable = GC.GetGCMemoryInfo().TotalAvailableMemoryBytes;
        var test = Environment.GetEnvironmentVariable(TestVariable);
        if (!string.IsNullOrWhiteSpace(test)) return ParseTest(test, ownPrivate);
        var (total, avail) = Physical();
        long totalPhys = total ?? gcAvailable;
        long? job = JobMemoryLimit();
        long? limit = null; string source = "none";
        if (job is { } j) { limit = Math.Min(j, gcAvailable); source = JobSource(j, gcAvailable); }
        else if (gcAvailable < totalPhys) { limit = gcAvailable; source = "GC heap hard limit"; }
        return (new MemoryReadings(totalPhys, avail, ownPrivate, limit, source), Others(), "this machine");
    }

    /// <summary>How a job object's limit is named: the GC heap's share when the runtime took one (75 % of the job by default).</summary>
    public static string JobSource(long jobLimit, long gcAvailable)
        => gcAvailable < jobLimit ? $"the GC heap's share of the job object's {jobLimit / 1e9:0.0} GB limit" : $"job object, {jobLimit / 1e9:0.0} GB";

    static (MemoryReadings, IReadOnlyList<ProcessUse>, string) ParseTest(string spec, long ownPrivate)
    {
        var kv = spec.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                     .Select(p => p.Split('=', 2)).ToDictionary(p => p[0], p => p.Length > 1 ? p[1] : "");
        long? L(string k) => kv.TryGetValue(k, out var v) && long.TryParse(v, out var n) ? n : null;
        var others = new List<ProcessUse>();
        if (kv.TryGetValue("others", out var os))
            foreach (var o in os.Split(',', StringSplitOptions.RemoveEmptyEntries))
            {
                var f = o.Split(':');
                others.Add(new(f[0], int.Parse(f[1]), long.Parse(f[2])));
            }
        var r = new MemoryReadings(L("total") ?? throw new ArgumentException($"{TestVariable} needs total=BYTES"), L("avail"),
                                   ownPrivate, L("limit"), kv.GetValueOrDefault("source", "none"));
        return (r, others, $"{TestVariable} (test readings, not this machine's)");
    }

    /// <summary>Total and available physical memory; nulls where the platform does not say.</summary>
    public static (long? Total, long? Available) Physical()
    {
        if (OperatingSystem.IsWindows())
        {
            var m = new MemStatus { Length = (uint)Marshal.SizeOf<MemStatus>() };
            return GlobalMemoryStatusEx(ref m) ? ((long)m.TotalPhys, (long)m.AvailPhys) : (null, null);
        }
        if (OperatingSystem.IsLinux())
        {
            try
            {
                var text = File.ReadAllText("/proc/meminfo");
                long? Kb(string key) => text.Split('\n').Where(l => l.StartsWith(key + ":", StringComparison.Ordinal))
                    .Select(l => long.TryParse(l[(key.Length + 1)..].Trim().Split(' ')[0], out var kb) ? kb * 1024 : (long?)null).FirstOrDefault();
                return (Kb("MemTotal"), Kb("MemAvailable"));
            }
            catch (IOException) { return (null, null); }
            catch (UnauthorizedAccessException) { return (null, null); }
        }
        return (null, null);
    }

    /// <summary>The memory limit of the job object this process is in (the immediate job, as the runtime reads it), or null.</summary>
    public static long? JobMemoryLimit()
    {
        if (!OperatingSystem.IsWindows()) return null;
        var info = new ExtendedLimit();
        if (!QueryInformationJobObject(IntPtr.Zero, 9, ref info, (uint)Marshal.SizeOf<ExtendedLimit>(), IntPtr.Zero)) return null;
        const uint ProcessMemory = 0x100, JobMemory = 0x200;
        long? limit = null;
        if ((info.Basic.LimitFlags & ProcessMemory) != 0) limit = (long)info.ProcessMemoryLimit;
        if ((info.Basic.LimitFlags & JobMemory) != 0) limit = Math.Min(limit ?? long.MaxValue, (long)info.JobMemoryLimit);
        return limit;
    }

    /// <summary>Other programs by working set, largest first, leaving out this process, the processes that started it (closing
    /// them would stop this run) and the system's own.</summary>
    public static IReadOnlyList<ProcessUse> Others()
    {
        var list = new List<(string Name, int Pid, long Bytes)>();
        foreach (var p in System.Diagnostics.Process.GetProcesses())
        {
            try { list.Add((p.ProcessName, p.Id, p.WorkingSet64)); }
            catch (InvalidOperationException) { }
            catch (System.ComponentModel.Win32Exception) { }
            finally { p.Dispose(); }
        }
        return Select(list, Environment.ProcessId, ParentMap());
    }

    /// <summary>Pure part of <see cref="Others"/>: drop this process, its ancestors (by <paramref name="parentOf"/>), the system's own
    /// and anything under 100 MB; keep the largest eight.</summary>
    public static IReadOnlyList<ProcessUse> Select(IEnumerable<(string Name, int Pid, long Bytes)> all, int self, IReadOnlyDictionary<int, int> parentOf)
    {
        var skip = new HashSet<int> { 0, self };
        for (int p = self, guard = 0; parentOf.TryGetValue(p, out var parent) && parent != 0 && guard < 64; p = parent, guard++)
            if (!skip.Add(parent)) break;
        return all.Where(x => !skip.Contains(x.Pid) && !SystemNames.Contains(x.Name) && x.Bytes >= 100_000_000)
                  .OrderByDescending(x => x.Bytes).Take(8).Select(x => new ProcessUse(x.Name, x.Pid, x.Bytes)).ToList();
    }

    /// <summary>Each process's parent, from a Toolhelp snapshot (Windows); empty elsewhere or when the snapshot fails.</summary>
    static Dictionary<int, int> ParentMap()
    {
        var map = new Dictionary<int, int>();
        if (!OperatingSystem.IsWindows()) return map;
        IntPtr snap = CreateToolhelp32Snapshot(0x2, 0);
        if (snap == new IntPtr(-1)) return map;
        try
        {
            var e = new ProcessEntry { Size = (uint)Marshal.SizeOf<ProcessEntry>() };
            for (bool ok = Process32FirstW(snap, ref e); ok; ok = Process32NextW(snap, ref e)) map[(int)e.Pid] = (int)e.ParentPid;
        }
        finally { CloseHandle(snap); }
        return map;
    }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct ProcessEntry
    {
        public uint Size, Usage, Pid; public IntPtr DefaultHeapId; public uint ModuleId, Threads, ParentPid; public int PriClassBase; public uint Flags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string ExeFile;
    }
    [DllImport("kernel32.dll", SetLastError = true)] static extern IntPtr CreateToolhelp32Snapshot(uint flags, uint pid);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] static extern bool Process32FirstW(IntPtr snap, ref ProcessEntry e);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] static extern bool Process32NextW(IntPtr snap, ref ProcessEntry e);
    [DllImport("kernel32.dll", SetLastError = true)] static extern bool CloseHandle(IntPtr h);

    [StructLayout(LayoutKind.Sequential)]
    struct MemStatus { public uint Length, MemoryLoad; public ulong TotalPhys, AvailPhys, TotalPageFile, AvailPageFile, TotalVirtual, AvailVirtual, AvailExtendedVirtual; }
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    static extern bool GlobalMemoryStatusEx(ref MemStatus m);

    [StructLayout(LayoutKind.Sequential)]
    struct BasicLimit { public long PerProcessUserTimeLimit, PerJobUserTimeLimit; public uint LimitFlags; public UIntPtr MinimumWorkingSetSize, MaximumWorkingSetSize; public uint ActiveProcessLimit; public UIntPtr Affinity; public uint PriorityClass, SchedulingClass; }
    [StructLayout(LayoutKind.Sequential)]
    struct IoCounters { public ulong ReadOperationCount, WriteOperationCount, OtherOperationCount, ReadTransferCount, WriteTransferCount, OtherTransferCount; }
    [StructLayout(LayoutKind.Sequential)]
    struct ExtendedLimit { public BasicLimit Basic; public IoCounters Io; public UIntPtr ProcessMemoryLimit, JobMemoryLimit, PeakProcessMemoryUsed, PeakJobMemoryUsed; }
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    static extern bool QueryInformationJobObject(IntPtr job, int infoClass, ref ExtendedLimit info, uint length, IntPtr returned);
}
