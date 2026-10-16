using System.Security.Cryptography;
using HarnessShared;
using HousecarlCore;
using Mutagen.Bethesda.Plugins;

/// <summary>The default pass's decoded meshes, bounded: the decoded geometry is held in the shared contact cache budget (least
/// recently used) and decoded again from the mesh's bytes when it was evicted. The first decode of each mesh is the only one
/// the decoder is told is first (it does the run's bookkeeping then), and it records the bytes' length and SHA-256; a later
/// decode of different bytes refuses (<see cref="MeshChangedOnDiskException"/>) rather than mix two versions of a mesh.</summary>
sealed class MeshGeometryReader
{
    sealed record First(bool Decoded, int Length, byte[] Sha);

    readonly Func<string, byte[]?> _read;
    readonly Func<string, byte[], bool, RefGeometry?> _decode;
    readonly Dictionary<string, First> _first = new(StringComparer.OrdinalIgnoreCase);
    readonly Dictionary<string, int> _ids = new(StringComparer.OrdinalIgnoreCase);
    PlacementCache<RefGeometry>? _cache;

    /// <summary>Decodes run (first decodes plus re-decodes) and re-decodes of an evicted mesh.</summary>
    public long Decodes { get; private set; }
    public long Redecodes { get; private set; }

    /// <param name="read">the mesh's bytes by path, or null when it cannot be read.</param>
    /// <param name="decode">(path, bytes, first) to the geometry, or null; a pure function of the bytes. <c>first</c> is true
    /// exactly once per path.</param>
    public MeshGeometryReader(Func<string, byte[]?> read, Func<string, byte[], bool, RefGeometry?> decode) { _read = read; _decode = decode; }

    /// <summary>Hold decoded meshes in <paramref name="budget"/> as the "meshGeoms" cache. Required before <see cref="Get"/>.</summary>
    public void Attach(CacheBudget budget) => _cache = new PlacementCache<RefGeometry>(budget, "meshGeoms", GeometryBytes);

    /// <summary>Choose the store for a run: <paramref name="bounded"/> (the default pass) holds decoded meshes in the shared budget.</summary>
    public void Store(bool bounded, CacheBudget budget) => Attach(budget);

    public RefGeometry? Get(string path)
    {
        var cache = _cache ?? throw new InvalidOperationException("MeshGeometryReader.Attach must be called before Get");
        if (!_ids.TryGetValue(path, out int id)) _ids[path] = id = _ids.Count;
        var key = (FormKey.Null, id, FormKey.Null);
        if (_first.TryGetValue(path, out var f))
        {
            if (!f.Decoded) return null;
            if (cache.TryGetValue(key, out var hit)) return hit;
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
