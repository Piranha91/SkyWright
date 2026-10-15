using System.Numerics;
using System.Runtime.CompilerServices;
using HarnessShared;
using HousecarlCore;
using Mutagen.Bethesda.Plugins;

/// <summary>Known answers for the geometry summary and the bounded geometry (contactprobe default-pass memory, 2026-10-14):
/// a summary answers exactly what the full geometry answers (CM-2, suite 152), and an index that does not memoise geometry keeps
/// no soup once its non-builder questions are answered (CM-3, suite 153). CM-1 (suite 151) runs beside ct-synth in Program.cs.</summary>
static class GeometryRetentionProof
{
    static readonly ModKey M = new("GrProof", ModType.Plugin);
    static FormKey K(uint id) => new(M, id);

    static TriangleSoup Tetra(float size, float radius, string material)
    {
        var v = new[] { new Vector3(0, 0, 0), new Vector3(size, 0, 0), new Vector3(0, size, 0), new Vector3(0, 0, size) };
        var i = new[] { 0, 2, 1, 0, 1, 3, 0, 3, 2, 1, 2, 3 };
        return new TriangleSoup(v, i, Enumerable.Repeat<string?>(material, 4).ToArray(), new[] { radius, radius * 0.5f, radius, radius * 0.25f });
    }

    public static void Run(Action<string, bool, string> Check)
    {
        // CM-2: a collision host whose radii differ from the dependent's, and a render fallback that cannot host.
        var hostSoup = Tetra(100f, 2.5f, "SKY_HAV_MAT_STONE");
        var depSoup = Tetra(40f, 0.75f, "SKY_HAV_MAT_WOOD");
        var bodies = new List<(string, string)> { ("SKYL_STATIC", "MO_SYS_BOX_STABILIZED"), ("SKYL_CLUTTER", "MO_SYS_SPHERE_INERTIA") };
        var box = SpatialOverlap.SoupBounds(hostSoup).Union(SpatialOverlap.SoupBounds(depSoup));
        var full = new RefGeometry(box, hostSoup, depSoup, 'c', 'c', HostUsable: true, bodies, false);
        var render = Tetra(30f, 0f, "render");
        var renderOnly = new RefGeometry(SpatialOverlap.SoupBounds(render), render, render, 'r', 'r', HostUsable: false, Array.Empty<(string, string)>(), true);
        var empty = new RefGeometry(box, TriangleSoup.Empty, TriangleSoup.Empty, '-', '-', HostUsable: false, Array.Empty<(string, string)>(), false);
        bool Same(RefGeometry g, GeomSummary? s) => s is not null && s.Box.Equals(g.Box) && s.HostSrc == g.HostSrc && s.DepSrc == g.DepSrc
            && s.HostUsable == g.HostUsable && s.HasGeometry == g.HasGeometry && s.CanHost == g.CanHost && s.CanDepend == g.CanDepend
            && BitConverter.SingleToInt32Bits(s.HostRadiusMax) == BitConverter.SingleToInt32Bits(g.HostRadiusMax)
            && BitConverter.SingleToInt32Bits(s.DepRadiusMax) == BitConverter.SingleToInt32Bits(g.DepRadiusMax)
            && s.SolidBodies.SequenceEqual(g.SolidBodies) && s.PhantomOnly == g.PhantomOnly;
        var geo = new Dictionary<FormKey, RefGeometry> { [K(1)] = full, [K(2)] = renderOnly, [K(3)] = empty };
        var bounded = new ContactIndex(k => geo.GetValueOrDefault(k), long.MaxValue, memoiseGeometry: false);
        Check("CM-2 the geometry summary answers what the full geometry answers: collision host with radii (host 2.5, dependent 0.75), render fallback, no soup",
              Same(full, GeomSummary.Of(full)) && Same(renderOnly, GeomSummary.Of(renderOnly)) && Same(empty, GeomSummary.Of(empty))
           && Same(full, bounded.Summary(K(1))) && Same(renderOnly, bounded.Summary(K(2))) && bounded.Summary(K(9)) is null
           && full.HostRadiusMax == 2.5f && full.DepRadiusMax == 0.75f && full.CanHost && !renderOnly.CanHost && renderOnly.CanDepend && !empty.CanDepend,
              $"summary host radius {GeomSummary.Of(full).HostRadiusMax} / {full.HostRadiusMax}, dependent radius {GeomSummary.Of(full).DepRadiusMax} / {full.DepRadiusMax}");

        // CM-3: retention. Each call to the geometry delegate builds new soups; the proof keeps only weak references to them.
        // After the summary questions (box, can host, motion), a compacting collection must find every soup gone when the index
        // does not memoise geometry. The memoising index (control) must still hold its soups: the probe can see retention.
        var (boundedAlive, boundedMade) = Retained(memoise: false);
        var (memoAlive, memoMade) = Retained(memoise: true);
        Check("CM-3 an index that does not memoise geometry keeps no soup after its summary questions; the memoising index (control) keeps them",
              boundedMade > 0 && boundedAlive == 0 && memoMade > 0 && memoAlive > 0,
              $"not memoised: {boundedAlive} of {boundedMade} soups alive; memoised: {memoAlive} of {memoMade} alive");
    }

    /// <summary>Soups the delegate made, and how many survive a compacting collection while the index is still alive.</summary>
    static (int Alive, int Made) Retained(bool memoise)
    {
        var made = new List<WeakReference>();
        var index = Ask(memoise, made);
        Collect();
        int alive = made.Count(w => w.IsAlive);
        GC.KeepAlive(index);
        return (alive, made.Count);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    static ContactIndex Ask(bool memoise, List<WeakReference> made)
    {
        RefGeometry? Make(FormKey k)
        {
            if (k != K(0x10)) return null;
            var host = Tetra(100f, 1f, "SKY_HAV_MAT_STONE"); var dep = Tetra(100f, 1f, "SKY_HAV_MAT_STONE");
            made.Add(new WeakReference(host)); made.Add(new WeakReference(dep));
            return new RefGeometry(SpatialOverlap.SoupBounds(host), host, dep, 'c', 'c', HostUsable: true, new List<(string, string)> { ("SKYL_STATIC", "MO_SYS_FIXED") }, false);
        }
        var index = new ContactIndex(Make, long.MaxValue, memoiseGeometry: memoise);
        var at = new Snap(0, new Vec3(10, 20, 30), K(0xC0), false, false, FormKey.Null);
        _ = index.WorldBox(K(0x10), at);
        _ = index.Summary(K(0x10)) is { CanHost: true };
        _ = index.MotionOf(K(0x10));
        return index;
    }

    static void Collect()
    {
        for (int i = 0; i < 2; i++)
        {
            System.Runtime.GCSettings.LargeObjectHeapCompactionMode = System.Runtime.GCLargeObjectHeapCompactionMode.CompactOnce;
            GC.Collect(2, GCCollectionMode.Forced, blocking: true, compacting: true);
            GC.WaitForPendingFinalizers();
        }
    }
}
