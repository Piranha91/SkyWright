using System.Collections.Concurrent;
using System.Security.Cryptography;
using HarnessShared;
using HousecarlCore;
using Mutagen.Bethesda.Plugins;

/// <summary>Decoded meshes. In the default pass bounded: the decoded geometry is held in the shared contact cache budget (least
/// recently used) and decoded again from the mesh's bytes when it was evicted. The first decode of each mesh is the only one
/// the decoder is told is first (it does the run's bookkeeping then), and it records the bytes' length and SHA-256; a later
/// decode of different bytes refuses (<see cref="MeshChangedOnDiskException"/>) rather than mix two versions of a mesh. Safe from many
/// threads: a held mesh is returned without waiting; reads and decodes run one at a time, so the read and decode delegates and their
/// bookkeeping need no locks of their own, and the first decode of a path happens once.</summary>
sealed class MeshGeometryReader
{
    sealed record First(bool Decoded, int Length, byte[] Sha);

    readonly Func<string, byte[]?> _read;
    readonly Func<string, byte[], bool, RefGeometry?> _decode;
    readonly ConcurrentDictionary<string, First> _first = new(StringComparer.OrdinalIgnoreCase);
    readonly ConcurrentDictionary<string, int> _ids = new(StringComparer.OrdinalIgnoreCase);
    int _lastId = -1;
    readonly object _decodeGate = new();
    PlacementCache<RefGeometry>? _cache;
    ConcurrentDictionary<string, RefGeometry?>? _all;   // the memoising modes' store: every mesh decoded once and kept for the run

    /// <summary>Decodes run (first decodes plus re-decodes) and re-decodes of an evicted mesh; written under the decode lock.</summary>
    public long Decodes { get; private set; }
    public long Redecodes { get; private set; }

    /// <param name="read">the mesh's bytes by path, or null when it cannot be read.</param>
    /// <param name="decode">(path, bytes, first) to the geometry, or null; a pure function of the bytes. <c>first</c> is true
    /// exactly once per path.</param>
    public MeshGeometryReader(Func<string, byte[]?> read, Func<string, byte[], bool, RefGeometry?> decode) { _read = read; _decode = decode; }

    /// <summary>Hold decoded meshes in <paramref name="budget"/> as the "meshGeoms" cache. Required before <see cref="Get"/>.</summary>
    public void Attach(CacheBudget budget) => _cache = new PlacementCache<RefGeometry>(budget, "meshGeoms", GeometryBytes);

    /// <summary>Choose the store for a run: <paramref name="bounded"/> (the default pass) holds decoded meshes in the shared budget;
    /// otherwise (the modes whose index memoises every base) each mesh is decoded once and kept, so bases sharing it share one object.</summary>
    public void Store(bool bounded, CacheBudget budget)
    {
        if (bounded) Attach(budget);
        else _all = new ConcurrentDictionary<string, RefGeometry?>(StringComparer.OrdinalIgnoreCase);
    }

    public RefGeometry? Get(string path)
    {
        if (_all is not null)
        {
            if (_all.TryGetValue(path, out var kept)) return kept;
            lock (_decodeGate)
            {
                if (_all.TryGetValue(path, out kept)) return kept;
                var b = _read(path);
                RefGeometry? g1 = null;
                if (b is not null) { Decodes++; g1 = _decode(path, b, true); }
                return _all[path] = g1;
            }
        }
        var cache = _cache ?? throw new InvalidOperationException("MeshGeometryReader.Store must be called before Get");
        int id = _ids.GetOrAdd(path, _ => Interlocked.Increment(ref _lastId));
        var key = (FormKey.Null, id, FormKey.Null);
        if (_first.TryGetValue(path, out var known))
        {
            if (!known.Decoded) return null;
            if (cache.TryGetValue(key, out var hit)) return hit;
        }
        lock (_decodeGate)
        {
            if (_first.TryGetValue(path, out var f))
            {
                if (!f.Decoded) return null;
                if (cache.TryPeek(key, out var held)) return held;
                var again = _read(path);
                if (again is null || again.Length != f.Length || !SHA256.HashData(again).AsSpan().SequenceEqual(f.Sha))
                    throw new MeshChangedOnDiskException(path);
                Decodes++; Redecodes++;
                var g = _decode(path, again, false);
                cache[key] = g;
                return g;
            }
            var bytes = _read(path);
            if (bytes is null) { _first[path] = new First(false, 0, Array.Empty<byte>()); return null; }
            Decodes++;
            var g0 = _decode(path, bytes, true);
            _first[path] = new First(g0 is not null, bytes.Length, SHA256.HashData(bytes));
            if (g0 is not null) cache[key] = g0;
            return g0;
        }
    }

    /// <summary>The bytes a decoded mesh holds: each distinct soup once (a render fallback can serve both roles).</summary>
    public static long GeometryBytes(RefGeometry g)
    {
        long bytes = 0;
        var seen = new HashSet<TriangleSoup>(ReferenceEqualityComparer.Instance);
        foreach (var so in new[] { g.HostSoup, g.DepSoup, g.HostShell, g.DepShell })
            if (so is not null && seen.Add(so)) bytes += PlacementCache<TriangleSoup>.SoupBytes(so);
        return bytes + g.SolidBodies.Count * 32L;
    }
}

/// <summary>A mesh's bytes differ from the bytes first decoded in this run.</summary>
sealed class MeshChangedOnDiskException(string path)
    : Exception($"mesh {path} changed on disk during the run; rerun the probe")
{
    public string MeshPath { get; } = path;
}
