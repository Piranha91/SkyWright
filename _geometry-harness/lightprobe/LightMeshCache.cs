using HousecarlCore;

// lightprobe's model cache (2026-11-26, Gate B): one cache for every arrangement a --check run reads (the Gate A review's finding 2).
// Entries are keyed by the RESOLVED SOURCE (the loose file, or the archive and its entry), never by the mesh path: two arrangements that
// resolve one path to different files (one mod's loose mesh overriding another's) each get their own file's reading, and two that resolve
// it to the same file share one read. Each arrangement resolves its paths through its own asset view; only the reading is shared.
sealed class LightMeshCache
{
    readonly Dictionary<string, object> _entries = new(StringComparer.OrdinalIgnoreCase);
    public long Hits, Reads;

    /// <summary>The identity of a resolved source: its loose file's full path, or its archive's full path and the entry inside it.</summary>
    public static string SourceKey(PlacementSource s) => s.Kind == AssetKind.Loose
        ? "loose|" + Path.GetFullPath(s.LooseFilePath ?? "")
        : "archive|" + Path.GetFullPath(s.ArchivePath ?? "") + "|" + s.EntryPath;

    /// <summary>The reading of kind <paramref name="kind"/> ("model", "emitters") of one resolved source, read once.</summary>
    public T Get<T>(string kind, PlacementSource s, Func<PlacementSource, T> read)
    {
        var key = kind + "|" + SourceKey(s);
        if (_entries.TryGetValue(key, out var hit)) { Hits++; return (T)hit; }
        Reads++;
        var v = read(s);
        _entries[key] = v!;
        return v;
    }
}
