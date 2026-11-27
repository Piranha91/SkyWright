using HousecarlCore;

// lightprobe's model cache (2026-11-26, Gate B): one cache for every arrangement a --check run reads (the Gate A review's finding 2).
// Entries are keyed by the RESOLVED SOURCE (the loose file, or the archive and its entry), never by the mesh path: two arrangements that
// resolve one path to different files (one mod's loose mesh overriding another's) each get their own file's reading, and two that resolve
// it to the same file share one read. Each arrangement resolves its paths through its own asset view; only the reading is shared.
// The cache holds at most a budget of bytes (default: a quarter of the memory the runtime says it may use; measured: unbounded, a city
// set's four arrangements peaked at 7.8 GB and ran out of memory under a 2.5 GiB heap limit). Past it, the cache is emptied and models are
// read again: a reading is a function of its source, so the budget changes the time a run takes, never what it reads.
sealed class LightMeshCache
{
    readonly Dictionary<string, (object Value, long Bytes)> _entries = new(StringComparer.OrdinalIgnoreCase);
    public long Hits, Reads, Bytes, PeakBytes, Evictions;
    public long Budget { get; }

    public LightMeshCache(long? budgetBytes = null) =>
        Budget = budgetBytes ?? Math.Max(256L << 20, GC.GetGCMemoryInfo().TotalAvailableMemoryBytes / 4);

    /// <summary>Empty the cache (the lean mode between arrangements, when the runtime may use too little memory to keep models while the
    /// next arrangement is indexed).</summary>
    public void Clear() { if (_entries.Count > 0) Evictions++; _entries.Clear(); Bytes = 0; }

    /// <summary>The identity of a resolved source: its loose file's full path, or its archive's full path and the entry inside it.</summary>
    public static string SourceKey(PlacementSource s) => s.Kind == AssetKind.Loose
        ? "loose|" + Path.GetFullPath(s.LooseFilePath ?? "")
        : "archive|" + Path.GetFullPath(s.ArchivePath ?? "") + "|" + s.EntryPath;

    /// <summary>The reading of kind <paramref name="kind"/> ("model", "emitters") of one resolved source, read once while it fits the
    /// budget; <paramref name="size"/> estimates the bytes a reading holds.</summary>
    public T Get<T>(string kind, PlacementSource s, Func<PlacementSource, T> read, Func<T, long>? size = null)
    {
        var key = kind + "|" + SourceKey(s);
        if (_entries.TryGetValue(key, out var hit)) { Hits++; return (T)hit.Value; }
        Reads++;
        var v = read(s);
        long b = 256 + (size?.Invoke(v) ?? 0);
        if (Bytes + b > Budget && _entries.Count > 0) { _entries.Clear(); Bytes = 0; Evictions++; }
        _entries[key] = (v!, b);
        Bytes += b;
        PeakBytes = Math.Max(PeakBytes, Bytes);
        return v;
    }
}
