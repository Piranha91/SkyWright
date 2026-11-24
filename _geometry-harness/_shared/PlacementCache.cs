using HousecarlCore;
using SkyWright.Engine;
using Mutagen.Bethesda.Plugins;

namespace HarnessShared;

/// <summary>One byte budget shared by the per-placement geometry caches (ContactIndex's host grids, dependent soups and
/// radius shells): least-recently-used across all of them, evicted by bytes. The caches are memos of deterministic
/// functions of (reference, snap, base), so an evicted entry rebuilt later is the same value — eviction trades time for
/// memory and never changes an answer. The entry just inserted is never evicted; one larger than the whole budget is kept
/// (the current pair needs it) and counted in <see cref="OversizeInserts"/>. Safe from many threads: one short lock covers the
/// recency list, the counters and every cache's map, never a build.</summary>
public sealed class CacheBudget
{
    /// <summary>The smallest budget accepted. Below this the caches would thrash on every pair.</summary>
    public const long MinimumBytes = 64L << 20;
    /// <summary>The automatic budget's share of the memory the runtime may use (physical, or the heap hard limit when one is set).</summary>
    public const double AutoFraction = 0.20;

    /// <summary>The automatic budget's ceiling: 1 GiB since 2026-10-14, when the budget began to hold contactprobe's decoded meshes as
    /// well (4 GiB before). Set from one case's measurements (2026-10-05, the lab whole order): host-grid misses barely changed
    /// between 1.72 GB and 13 GB (15,350 vs 15,314), and a larger budget bought no measured speed but cost memory. A case whose
    /// misses rise between 1 GiB and a larger budget would move it. An explicit budget may exceed it.</summary>
    public const long AutoCeilingBytes = 1L << 30;
    /// <summary>The automatic ceiling per worker thread when a pass runs on more than one: each thread keeps its own working set of grids.</summary>
    public const long PerThreadCeilingBytes = 2L << 30;
    /// <summary>The automatic ceiling for a pass on <paramref name="threads"/> threads; one thread keeps <see cref="AutoCeilingBytes"/>.</summary>
    public static long CeilingFor(int threads) => threads <= 1 ? AutoCeilingBytes : threads * PerThreadCeilingBytes;

    /// <summary>The automatic budget for a process allowed <paramref name="availableBytes"/>: <see cref="AutoFraction"/> of it, at most
    /// <see cref="AutoCeilingBytes"/>, never below <see cref="MinimumBytes"/>.</summary>
    public static long Auto(long availableBytes) => Math.Max(MinimumBytes, Math.Min(AutoCeilingBytes, (long)(availableBytes * AutoFraction)));
    public static long Auto() => Auto(GC.GetGCMemoryInfo().TotalAvailableMemoryBytes);
    /// <summary><see cref="Auto(long)"/> under the ceiling for <paramref name="threads"/> threads.</summary>
    public static long Auto(long availableBytes, int threads) => Math.Max(MinimumBytes, Math.Min(CeilingFor(threads), (long)(availableBytes * AutoFraction)));
    /// <summary>The automatic budget's share of the memory free now beyond the live data, applied at the fit check.</summary>
    public const double FreeFraction = 0.40;
    /// <summary>The free-memory term of the automatic budget: <see cref="FreeFraction"/> x (free now − live), never below the minimum.</summary>
    public static long AutoFromFree(long freeNowBytes, long liveBytes) => Math.Max(MinimumBytes, (long)((freeNowBytes - liveBytes) * FreeFraction));

    /// <summary>The automatic selection and its actual input readings. CurrentManagedBytes is not committed heap or
    /// process memory; the native reader labels it as current uncollected managed bytes, without forcing a collection.</summary>
    public sealed record AutomaticSelection(long RuntimeAvailableBytes, long? AvailablePhysicalBytes,
        long CurrentManagedBytes, string ManagedMeasurement, long RuntimeTermBytes, long? FreeTermBytes,
        long? SelectedBytes, string WinningTerm, string? Refusal)
    {
        public long RequireBytes() => SelectedBytes ?? throw new AutomaticBudgetUnavailableException(this);
        public string Describe()
            => $"runtime available {RuntimeAvailableBytes} bytes; free physical {AvailablePhysicalBytes?.ToString() ?? "unavailable"} bytes; "
             + $"{ManagedMeasurement} {CurrentManagedBytes} bytes; selected {SelectedBytes?.ToString() ?? "unavailable"} bytes "
             + $"({WinningTerm}{(SelectedBytes == MinimumBytes ? ", at the 64 MiB minimum" : "")})";

        /// <summary>A sentence when free memory held the budget below what this machine's runtime term allows, else null.</summary>
        public string? MemoryHeldWarning()
            => FreeTermBytes is { } free && SelectedBytes is { } sel && free < RuntimeTermBytes
                ? $"warning: free memory holds the contact cache to {sel / 1e9:0.00} GB of the {RuntimeTermBytes / 1e9:0.00} GB it could use; the run may be slower, its rows are the same; closing other programs frees more"
                : null;
    }

    /// <summary>A named memory refusal, retained with the readings that could not select an automatic budget.</summary>
    public sealed class AutomaticBudgetUnavailableException : InvalidOperationException
    {
        public AutomaticSelection Selection { get; }
        public AutomaticBudgetUnavailableException(AutomaticSelection selection)
            : base(selection.Refusal ?? "memory: automatic contact cache selection unavailable") => Selection = selection;
    }

    /// <summary>Pure automatic selection: the existing runtime/floor/ceiling term and free-minus-managed term.
    /// When free physical memory cannot be read on this platform, the runtime term alone is selected and the winning
    /// term says so (Describe prints it), so the reduced selection is named in the probe's output, never silent.</summary>
    public static AutomaticSelection SelectAutomatic(long runtimeAvailableBytes, long? availablePhysicalBytes,
        long currentManagedBytes, string managedMeasurement = "current uncollected managed bytes", int threads = 1)
    {
        if (availablePhysicalBytes is null) threads = 1;                      // no free-memory reading: the lean ceiling
        long runtimeTerm = Auto(runtimeAvailableBytes, threads);
        string runtimeWinner = (long)(runtimeAvailableBytes * AutoFraction) <= CeilingFor(threads) ? "runtime" : "ceiling";
        if (availablePhysicalBytes is not { } free)
            return new(runtimeAvailableBytes, null, currentManagedBytes, managedMeasurement, runtimeTerm, null,
                runtimeTerm, runtimeWinner + "; free physical memory unreadable on this platform, free term not applied", null);
        long freeTerm = AutoFromFree(free, currentManagedBytes);
        long selected = Math.Min(runtimeTerm, freeTerm);
        string winner = freeTerm <= runtimeTerm ? "free-now-minus-managed" : runtimeWinner;
        return new(runtimeAvailableBytes, free, currentManagedBytes, managedMeasurement, runtimeTerm, freeTerm,
            selected, winner, null);
    }

    /// <summary>Cheap current readings, with no forced collection. A failed native physical-memory read remains named.</summary>
    public static AutomaticSelection ReadAutomatic()
        => SelectAutomatic(GC.GetGCMemoryInfo().TotalAvailableMemoryBytes, AvailablePhysicalBytes(), GC.GetTotalMemory(false));
    /// <summary><see cref="ReadAutomatic()"/> for a pass on <paramref name="threads"/> threads.</summary>
    public static AutomaticSelection ReadAutomatic(int threads)
        => SelectAutomatic(GC.GetGCMemoryInfo().TotalAvailableMemoryBytes, AvailablePhysicalBytes(), GC.GetTotalMemory(false), threads: threads);

    /// <summary>Parse "auto", a byte count, or a count with a K/M/G suffix (binary). Null when the text is none of those.</summary>
    public static long? Parse(string text)
    {
        var t = text.Trim();
        if (t.Equals("auto", StringComparison.OrdinalIgnoreCase)) return Auto();
        long mul = 1;
        char last = t.Length > 0 ? char.ToUpperInvariant(t[^1]) : ' ';
        if (last is 'K' or 'M' or 'G') { mul = last == 'K' ? 1L << 10 : last == 'M' ? 1L << 20 : 1L << 30; t = t[..^1]; }
        return double.TryParse(t, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var v) && v >= 0
            ? (long)(v * mul) : null;
    }

    /// <summary>Physical memory free on the machine right now: GlobalMemoryStatusEx ullAvailPhys on Windows, MemAvailable
    /// from /proc/meminfo on Linux. Null on other platforms or when the reading fails.</summary>
    public static long? AvailablePhysicalBytes()
    {
        if (OperatingSystem.IsWindows())
        {
            var m = new MemStatus { Length = (uint)System.Runtime.InteropServices.Marshal.SizeOf<MemStatus>() };
            return GlobalMemoryStatusEx(ref m) ? (long)m.AvailPhys : null;
        }
        if (OperatingSystem.IsLinux())
        {
            try { return ParseMemAvailable(File.ReadAllText("/proc/meminfo")); }
            catch (IOException) { return null; }
            catch (UnauthorizedAccessException) { return null; }
        }
        return null;
    }

    /// <summary>The MemAvailable line of a /proc/meminfo text, in bytes (the file reports kB). Null when absent or malformed.</summary>
    public static long? ParseMemAvailable(string meminfo)
    {
        foreach (var line in meminfo.Split('\n'))
        {
            if (!line.StartsWith("MemAvailable:", StringComparison.Ordinal)) continue;
            var parts = line["MemAvailable:".Length..].Split(' ', StringSplitOptions.RemoveEmptyEntries);
            return parts.Length == 2 && parts[1] == "kB" && long.TryParse(parts[0], System.Globalization.NumberStyles.None,
                System.Globalization.CultureInfo.InvariantCulture, out var kb) ? kb * 1024 : null;
        }
        return null;
    }
    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    struct MemStatus { public uint Length, MemoryLoad; public ulong TotalPhys, AvailPhys, TotalPageFile, AvailPageFile, TotalVirtual, AvailVirtual, AvailExtendedVirtual; }
    [System.Runtime.InteropServices.DllImport("kernel32.dll", SetLastError = true)]
    [return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
    static extern bool GlobalMemoryStatusEx(ref MemStatus m);

    /// <summary>The fit check before a pass that fills the caches: <paramref name="liveBytes"/> (the live heap after a full
    /// collection) plus the minimum budget must fit in 90 % of <paramref name="availableNowBytes"/> (memory free now). Returns the
    /// refusal sentence, or null when it fits.</summary>
    public static string? FitRefusal(long liveBytes, long availableNowBytes, string lowerNeedHint)
    {
        if (liveBytes + MinimumBytes <= availableNowBytes * 0.9) return null;
        return $"memory: live data {liveBytes / (double)(1L << 30):0.00} GiB + minimum contact cache {MinimumBytes >> 20} MiB exceeds 90 % of the "
             + $"{availableNowBytes / (double)(1L << 30):0.00} GiB free now; {lowerNeedHint}";
    }

    /// <summary>Per-entry bookkeeping charged on top of the value: dictionary entry plus list node.</summary>
    public const long EntryOverhead = 96;

    internal abstract class Node
    {
        internal Node? Prev, Next;
        internal long Bytes;
        internal int Kind;
        internal abstract void Detach();   // remove from the owning cache's dictionary
    }

    Node? _head, _tail;                     // head = most recently used
    /// <summary>Guards the recency list, the counters and every cache map in this pool. Held briefly; never across a build.</summary>
    internal readonly object Gate = new();
    public long Limit { get; private set; }
    /// <summary>Settle a tie for a cache's <c>Largest</c> by key, not by first arrival, which threads make a race. Off keeps first arrival.</summary>
    public bool StableLargestTie { get; set; }
    /// <summary>Lower the limit (never below <see cref="MinimumBytes"/>); the next insert evicts down to it.</summary>
    public void LowerTo(long limit) { lock (Gate) { if (limit < Limit) Limit = Math.Max(limit, MinimumBytes); } }
    /// <summary>Set the limit up or down (never below <see cref="MinimumBytes"/>); a lower limit evicts on the next insert.</summary>
    public void SetLimit(long limit) { lock (Gate) Limit = Math.Max(limit, MinimumBytes); }
    public long Bytes { get; private set; }
    public long PeakBytes { get; private set; }
    public long OversizeInserts { get; private set; }
    readonly List<string> _names = new();
    readonly List<long[]> _stats = new();   // per kind: hits, misses, evictions, bytes now, peak bytes

    public CacheBudget(long limit) { Limit = limit; }

    internal int Register(string name) { lock (Gate) { _names.Add(name); _stats.Add(new long[5]); return _names.Count - 1; } }

    // Hit, Touch, Miss, Insert and Remove run under Gate, taken by the owning cache.
    internal void Hit(Node n) { _stats[n.Kind][0]++; Touch(n); }
    internal void Touch(Node n) { if (_head != n) { Unlink(n); PushFront(n); } }
    internal void Miss(int kind) => _stats[kind][1]++;

    internal void Insert(Node n)
    {
        PushFront(n);
        Bytes += n.Bytes; _stats[n.Kind][3] += n.Bytes;
        if (n.Bytes > Limit) OversizeInserts++;
        while (Bytes > Limit && _tail is { } t && t != n)
        {
            Unlink(t); t.Detach();
            Bytes -= t.Bytes; var ts = _stats[t.Kind]; ts[3] -= t.Bytes; ts[2]++;
        }
        if (Bytes > PeakBytes) PeakBytes = Bytes;                 // peaks after eviction: what the pool holds between inserts
        var s = _stats[n.Kind]; if (s[3] > s[4]) s[4] = s[3];
    }

    /// <summary>Drop every entry (a diagnostic: the end-of-run accuracy check measures what the drop frees). Counters are kept.</summary>
    public void EvictAll()
    {
        lock (Gate) { while (_tail is { } t) { Unlink(t); t.Detach(); Bytes -= t.Bytes; _stats[t.Kind][3] -= t.Bytes; } }
    }

    internal void Remove(Node n) { Unlink(n); Bytes -= n.Bytes; _stats[n.Kind][3] -= n.Bytes; }

    void PushFront(Node n) { n.Prev = null; n.Next = _head; if (_head is not null) _head.Prev = n; _head = n; _tail ??= n; }
    void Unlink(Node n)
    {
        if (n.Prev is not null) n.Prev.Next = n.Next; else _head = n.Next;
        if (n.Next is not null) n.Next.Prev = n.Prev; else _tail = n.Prev;
        n.Prev = n.Next = null;
    }

    public long Hits(string name) { lock (Gate) return _stats[_names.IndexOf(name)][0]; }
    public long Misses(string name) { lock (Gate) return _stats[_names.IndexOf(name)][1]; }
    public long Evictions(string name) { lock (Gate) return _stats[_names.IndexOf(name)][2]; }
    /// <summary>Bytes each cache holds now, by name (the HC_MEMSTAGES diagnostic).</summary>
    public IEnumerable<(string Name, long Bytes)> BytesNow() { lock (Gate) return _names.Select((n, i) => (n, _stats[i][3])).ToList(); }

    /// <summary>One line: the budget, the pool's peak, and per cache hits / misses / evictions / peak bytes.</summary>
    public string Report()
    {
        static string G(long b) => $"{b / 1e9:0.000} GB";
        lock (Gate)
        {
            var parts = _names.Select((n, i) => $"{n} {_stats[i][0]:N0} hit / {_stats[i][1]:N0} miss / {_stats[i][2]:N0} evicted / peak {G(_stats[i][4])}");
            return $"contact cache budget {G(Limit)}, peak {G(PeakBytes)}, oversize {OversizeInserts:N0}: " + string.Join("; ", parts);
        }
    }
}

/// <summary>One cache in a <see cref="CacheBudget"/> pool, keyed by placement: (reference, snap, base). Null values (no
/// geometry) are cached and charged the entry overhead only. Safe from many threads under the pool's <see cref="CacheBudget.Gate"/>;
/// a caller that must build a key once holds its own per-key lock and rechecks with <see cref="TryPeek"/>.</summary>
public sealed class PlacementCache<TValue> where TValue : class
{
    sealed class Entry : CacheBudget.Node
    {
        internal (FormKey Key, int Snap, FormKey Base) K;
        internal TValue? Value;
        internal PlacementCache<TValue> Owner = null!;
        internal override void Detach() => Owner._map.Remove(K);
    }

    readonly Dictionary<(FormKey Key, int Snap, FormKey Base), Entry> _map = new();
    readonly CacheBudget _budget;
    readonly int _kind;
    readonly Func<TValue, long> _bytesOf;
    // Diagnostic (env HC_STALEBASE): the base last asked for at each (reference, snap), and how often a lookup asked for a
    // different one — the lookups a (reference, snap) key would have answered with another base's geometry.
    readonly Dictionary<(FormKey, int), FormKey>? _lastBase;
    public long StaleBaseLookups { get; private set; }
    public List<(FormKey Key, int Snap, FormKey Was, FormKey Now)>? StaleBaseKeys { get; }

    public PlacementCache(CacheBudget budget, string name, Func<TValue, long> bytesOf)
    {
        _budget = budget; _kind = budget.Register(name); _bytesOf = bytesOf;
        if (Environment.GetEnvironmentVariable("HC_STALEBASE") is not null) { _lastBase = new(); StaleBaseKeys = new(); }
    }

    public int Count { get { lock (_budget.Gate) return _map.Count; } }
    /// <summary>The largest entry ever inserted: its key, bytes and triangle count (no reference to the value is kept).</summary>
    public ((FormKey Key, int Snap, FormKey Base) Key, long Bytes, int Triangles)? Largest { get; private set; }

    public bool TryGetValue((FormKey Key, int Snap, FormKey Base) key, out TValue? value)
    {
        lock (_budget.Gate)
        {
            if (_lastBase is not null)
            {
                if (_lastBase.TryGetValue((key.Key, key.Snap), out var was) && was != key.Base)
                { StaleBaseLookups++; StaleBaseKeys!.Add((key.Key, key.Snap, was, key.Base)); }
                _lastBase[(key.Key, key.Snap)] = key.Base;
            }
            if (_map.TryGetValue(key, out var e)) { _budget.Hit(e); value = e.Value; return true; }
            _budget.Miss(_kind); value = null; return false;
        }
    }

    /// <summary>The recheck after a miss, once the caller holds its per-key lock: counts nothing, so one lookup stays one hit or one miss.</summary>
    public bool TryPeek((FormKey Key, int Snap, FormKey Base) key, out TValue? value)
    {
        lock (_budget.Gate)
        {
            if (_map.TryGetValue(key, out var e)) { _budget.Touch(e); value = e.Value; return true; }
            value = null; return false;
        }
    }

    public TValue? this[(FormKey Key, int Snap, FormKey Base) key]
    {
        set
        {
            var e = new Entry { K = key, Value = value, Owner = this, Kind = _kind,
                                Bytes = CacheBudget.EntryOverhead + (value is null ? 0 : _bytesOf(value)) };
            lock (_budget.Gate)
            {
                if (_map.Remove(key, out var old)) _budget.Remove(old);
                _map[key] = e;
                if (Largest is not { } lg || e.Bytes > lg.Bytes || (_budget.StableLargestTie && e.Bytes == lg.Bytes && KeyBefore(key, lg.Key)))
                    Largest = (key, e.Bytes, value switch { TriangleGrid g => g.TriangleCount, TriangleSoup so => so.TriangleCount, _ => 0 });
                _budget.Insert(e);
            }
        }
    }

    static bool KeyBefore((FormKey Key, int Snap, FormKey Base) a, (FormKey Key, int Snap, FormKey Base) b)
    {
        int c = string.CompareOrdinal(a.Key.ToString(), b.Key.ToString());
        if (c == 0) c = a.Snap.CompareTo(b.Snap);
        if (c == 0) c = string.CompareOrdinal(a.Base.ToString(), b.Base.ToString());
        return c < 0;
    }

    public static long SoupBytes(TriangleSoup s)
        => s.Vertices.Length * 12L + s.Indices.Length * 4L + (s.TriangleMaterials?.Length ?? 0) * 8L + (s.TriangleRadii?.Length ?? 0) * 4L;
}
