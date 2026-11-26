using HousecarlCore;

// lightprobe's model cache (2026-11-26, Gate B): one cache for every arrangement a --check run reads. Not yet implemented: every source
// shares one key.
sealed class LightMeshCache
{
    public long Hits, Reads;

    public static string SourceKey(PlacementSource s) => "";

    public T Get<T>(string kind, PlacementSource s, Func<PlacementSource, T> read) => read(s);
}
