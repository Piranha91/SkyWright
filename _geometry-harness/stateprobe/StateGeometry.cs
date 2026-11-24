using HarnessShared;
using HousecarlCore;
using SkyWright.Engine;
using Mutagen.Bethesda.Plugins;

// THE CONTACT GEOMETRY per base record (Program.cs's resolver): the actor-solid collision as the host surface, every solid
// body as the dependent's, the render soup as a labelled fallback, OBND as the last resort. Held in its own class so
// --self-test builds the contact index with the probe's own rule; the asset lookup stays the caller's (a mesh path under
// meshes\ to the winning provider's bytes, or null when none supplies it).
sealed class StateGeometry
{
    readonly Func<string, byte[]?> _read;
    /// <summary>One holder at a time reads an asset and parses a NIF: neither the asset view nor the NIF library is known to be safe concurrently.</summary>
    public static readonly object MeshGate = new();
    readonly System.Collections.Concurrent.ConcurrentDictionary<string, RefGeometry?> _byMesh = new(StringComparer.OrdinalIgnoreCase);
    public long MeshesMeasured, ObndFallback, NoGeometry;
    /// <summary>The contact index this geometry serves; set once it is built (it reads the base records' meshes and bounds).</summary>
    public ContactIndex? Contact { get; set; }

    public StateGeometry(Func<string, byte[]?> read) => _read = read;

    public RefGeometry? Of(FormKey baseKey)
    {
        RefGeometry? g = null;
        if (Contact!.Index.BaseMesh.TryGetValue(baseKey, out var rel))
        {
            var full = rel.StartsWith("meshes", StringComparison.OrdinalIgnoreCase) ? rel : Path.Combine("meshes", rel);
            if (!_byMesh.TryGetValue(full, out g))
            lock (MeshGate)
            if (!_byMesh.TryGetValue(full, out g))
            {
                g = null;
                var bytes = _read(full);
                if (bytes is not null && NifGeometry.Measure(bytes, includeTriangles: true) is { Model: { } m })
                {
                    Interlocked.Increment(ref MeshesMeasured);
                    var hc = m.CollisionSoupFor(b => b.Solid && CollisionLayers.IsActorSolid(b.Layer));
                    var dc = m.CollisionSoupFor(b => b.Solid && b.Layer != "SKYL_NONCOLLIDABLE");
                    TriangleSoup host, dep; char hs, ds;
                    if (m.CollisionComplete && hc is { TriangleCount: > 0 }) { host = hc; hs = 'c'; }
                    else if (m.Soup is { TriangleCount: > 0 }) { host = m.Soup; hs = 'r'; }
                    else { host = TriangleSoup.Empty; hs = '-'; }
                    if (m.CollisionComplete && dc is { TriangleCount: > 0 }) { dep = dc; ds = 'c'; }
                    else if (m.Soup is { TriangleCount: > 0 }) { dep = m.Soup; ds = 'r'; }
                    else { dep = TriangleSoup.Empty; ds = '-'; }
                    var box = SpatialOverlap.SoupBounds(host).Union(SpatialOverlap.SoupBounds(dep));
                    var bodies = m.CollisionBodies?.Where(b => b.Solid).Select(b => (b.Layer, b.MotionSystem)).ToList() ?? new List<(string, string)>();
                    bool phantom = m.HasCollision && !m.HasSolidCollision;
                    g = box.IsEmpty ? null : new RefGeometry(box, host, dep, hs, ds, HostUsable: hs == 'c', bodies, phantom);
                }
                _byMesh[full] = g;
            }
        }
        if (g is null)
        {
            var ob = Contact.Index.ObndBox(baseKey);
            if (!ob.IsEmpty) { Interlocked.Increment(ref ObndFallback); g = new RefGeometry(ob, ContactIndex.BoxSoup(ob), ContactIndex.BoxSoup(ob), 'o', 'o', HostUsable: false, Array.Empty<(string, string)>(), false); }
            else Interlocked.Increment(ref NoGeometry);
        }
        return g;
    }
}
