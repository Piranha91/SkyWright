using HousecarlCore;
using Mutagen.Bethesda.Plugins;

namespace HarnessShared;

/// <summary>One byte budget shared by the per-placement geometry caches (ContactIndex's host grids, dependent soups and
/// radius shells): least-recently-used across all of them, evicted by bytes. The caches are memos of deterministic
/// functions of (reference, snap, base), so an evicted entry rebuilt later is the same value — eviction trades time for
/// memory and never changes an answer. The entry just inserted is never evicted; one larger than the whole budget is kept
/// (the current pair needs it) and counted in <see cref="OversizeInserts"/>.</summary>
public sealed class CacheBudget
{
    /// <summary>The smallest budget accepted. Below this the caches would thrash on every pair.</summary>
    public const long MinimumBytes = 64L << 20;
    /// <summary>The automatic budget's share of the memory the runtime may use (physical, or the heap hard limit when one is set).</summary>
    public const double AutoFraction = 0.20;

    /// <summary>The automatic budget for a process allowed <paramref name="availableBytes"/>: <see cref="AutoFraction"/> of it, never
    /// below <see cref="MinimumBytes"/>.</summary>
    public static long Auto(long availableBytes) => Math.Max(MinimumBytes, (long)(availableBytes * AutoFraction));
    public static long Auto() => Auto(GC.GetGCMemoryInfo().TotalAvailableMemoryBytes);
    /// <summary>The automatic budget's share of the memory free now beyond the live data, applied at the fit check.</summary>
    public const double FreeFraction = 0.40;
    /// <summary>The free-memory term of the automatic budget: <see cref="FreeFraction"/> x (free now − live), never below the minimum.</summary>
    public static long AutoFromFree(long freeNowBytes, long liveBytes) => Math.Max(MinimumBytes, (long)((freeNowBytes - liveBytes) * FreeFraction));

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

    /// <summary>Physical memory free on the machine right now (GlobalMemoryStatusEx ullAvailPhys). Windows only; null elsewhere or on failure.</summary>
    public static long? AvailablePhysicalBytes()
    {
        if (!OperatingSystem.IsWindows()) return null;
        var m = new MemStatus { Length = (uint)System.Runtime.InteropServices.Marshal.SizeOf<MemStatus>() };
        return GlobalMemoryStatusEx(ref m) ? (long)m.AvailPhys : null;
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
    public long Limit { get; private set; }
    /// <summary>Lower the limit (never below <see cref="MinimumBytes"/>); the next insert evicts down to it.</summary>
    public void LowerTo(long limit) { if (limit < Limit) Limit = Math.Max(limit, MinimumBytes); }
    public long Bytes { get; private set; }
    public long PeakBytes { get; private set; }
    public long OversizeInserts { get; private set; }
    readonly List<string> _names = new();
    readonly List<long[]> _stats = new();   // per kind: hits, misses, evictions, bytes now, peak bytes

    public CacheBudget(long limit) { Limit = limit; }

    internal int Register(string name) { _names.Add(name); _stats.Add(new long[5]); return _names.Count - 1; }

    internal void Hit(Node n) { _stats[n.Kind][0]++; if (_head != n) { Unlink(n); PushFront(n); } }
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
        while (_tail is { } t) { Unlink(t); t.Detach(); Bytes -= t.Bytes; _stats[t.Kind][3] -= t.Bytes; }
    }

    internal void Remove(Node n) { Unlink(n); Bytes -= n.Bytes; _stats[n.Kind][3] -= n.Bytes; }

    void PushFront(Node n) { n.Prev = null; n.Next = _head; if (_head is not null) _head.Prev = n; _head = n; _tail ??= n; }
    void Unlink(Node n)
    {
        if (n.Prev is not null) n.Prev.Next = n.Next; else _head = n.Next;
        if (n.Next is not null) n.Next.Prev = n.Prev; else _tail = n.Prev;
        n.Prev = n.Next = null;
    }

    public long Hits(string name) => _stats[_names.IndexOf(name)][0];
    public long Misses(string name) => _stats[_names.IndexOf(name)][1];
    public long Evictions(string name) => _stats[_names.IndexOf(name)][2];

    /// <summary>One line: the budget, the pool's peak, and per cache hits / misses / evictions / peak bytes.</summary>
    public string Report()
    {
        static string G(long b) => $"{b / 1e9:0.000} GB";
        var parts = _names.Select((n, i) => $"{n} {_stats[i][0]:N0} hit / {_stats[i][1]:N0} miss / {_stats[i][2]:N0} evicted / peak {G(_stats[i][4])}");
        return $"contact cache budget {G(Limit)}, peak {G(PeakBytes)}, oversize {OversizeInserts:N0}: " + string.Join("; ", parts);
    }
}

/// <summary>One cache in a <see cref="CacheBudget"/> pool, keyed by placement: (reference, snap, base). Null values (no
/// geometry) are cached and charged the entry overhead only.</summary>
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

    public int Count => _map.Count;
    /// <summary>The largest entry ever inserted: its key, bytes and triangle count (no reference to the value is kept).</summary>
    public ((FormKey Key, int Snap, FormKey Base) Key, long Bytes, int Triangles)? Largest { get; private set; }

    public bool TryGetValue((FormKey Key, int Snap, FormKey Base) key, out TValue? value)
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

    public TValue? this[(FormKey Key, int Snap, FormKey Base) key]
    {
        set
        {
            if (_map.Remove(key, out var old)) _budget.Remove(old);
            var e = new Entry { K = key, Value = value, Owner = this, Kind = _kind,
                                Bytes = CacheBudget.EntryOverhead + (value is null ? 0 : _bytesOf(value)) };
            _map[key] = e;
            if (Largest is not { } lg || e.Bytes > lg.Bytes)
                Largest = (key, e.Bytes, value switch { TriangleGrid g => g.TriangleCount, TriangleSoup so => so.TriangleCount, _ => 0 });
            _budget.Insert(e);
        }
    }

    public static long SoupBytes(TriangleSoup s)
        => s.Vertices.Length * 12L + s.Indices.Length * 4L + (s.TriangleMaterials?.Length ?? 0) * 8L + (s.TriangleRadii?.Length ?? 0) * 4L;
}
