using HousecarlCore;

namespace SkyWright.Engine;

// Internal access to the exact mesh machinery for SweptGeometry (animated-object swept volume): a mesh handle for an
// open surface, a box test with inflation, a point-in-solid test, and a lower bound on the distance between two
// triangles. Kept beside the other partials so the swept region uses the same predicates as the volume and surface code.
public static partial class MeshIntersection
{
    /// <summary>A prepared triangle mesh (closed or not) for box and distance queries, or null with a reason.</summary>
    internal static object? PrepareSurfaceMesh(TriangleSoup soup, out string? reason)
    {
        var budget = new WorkBudget(long.MaxValue, CancellationToken.None);
        if (!TryPrepare(soup, "mesh", budget, out var mesh, out var error)) { reason = error; return null; }
        reason = null;
        return mesh;
    }

    internal readonly record struct SweptBox(double MinX, double MinY, double MinZ, double MaxX, double MaxY, double MaxZ);

    static Mesh M(object handle) => handle as Mesh ?? ((VolumeSolid)handle).Handle as Mesh ?? throw new ArgumentException("not a mesh handle");

    internal static SweptBox MeshBounds(object handle)
    {
        var b = M(handle).Root.Bounds;
        return new(b.Min.X, b.Min.Y, b.Min.Z, b.Max.X, b.Max.Y, b.Max.Z);
    }

    /// <summary>Whether any triangle of the mesh may meet the open box [lo, hi] grown by <paramref name="grow"/> on every
    /// side (conservative: rounding can only keep a triangle).</summary>
    internal static bool MeshMeetsBox(object handle, SweptBox box, double grow)
    {
        var m = M(handle);
        var lo = new DVec3(box.MinX - grow, box.MinY - grow, box.MinZ - grow);
        var hi = new DVec3(box.MaxX + grow, box.MaxY + grow, box.MaxZ + grow);
        var hits = new List<int>();
        QueryBvh(m, new Bounds(lo, hi), hits);
        foreach (int i in hits) if (MayMeetOpenCell(m.Triangles[i], lo, hi, 1e-9 + 1e-12 * (Math.Abs(lo.X) + Math.Abs(hi.X)))) return true;
        return false;
    }

    /// <summary>The same answer as <see cref="MeshMeetsBox"/> (any triangle of the mesh may meet the open box grown by
    /// <paramref name="grow"/>), returning the index of one such triangle, or -1. The <paramref name="hint"/> triangle is
    /// tried first; the BVH walk stops at the first triangle that may meet. Same box, same inflation, same per-triangle
    /// test: only the order and the early stop differ, so the yes/no answer is MeshMeetsBox's. <paramref name="tested"/>
    /// counts the per-triangle tests made (animated motion follow-ups, item 2, E2).</summary>
    internal static int MeshMeetsBoxFirst(object handle, SweptBox box, double grow, int hint, ref long tested)
    {
        var m = M(handle);
        var lo = new DVec3(box.MinX - grow, box.MinY - grow, box.MinZ - grow);
        var hi = new DVec3(box.MaxX + grow, box.MaxY + grow, box.MaxZ + grow);
        double inflate = 1e-9 + 1e-12 * (Math.Abs(lo.X) + Math.Abs(hi.X));
        var bounds = new Bounds(lo, hi);
        if (hint >= 0 && hint < m.Triangles.Length && m.Triangles[hint].Bounds.Overlaps(bounds, 0))
        {
            tested++;
            if (MayMeetOpenCell(m.Triangles[hint], lo, hi, inflate)) return hint;
        }
        var stack = new Stack<BvhNode>(); stack.Push(m.Root);
        while (stack.Count > 0)
        {
            var n = stack.Pop();
            if (!n.Bounds.Overlaps(bounds, 0)) continue;
            if (!n.IsLeaf) { stack.Push(n.Left!); stack.Push(n.Right!); continue; }
            for (int i = n.Start; i < n.Start + n.Count; i++)
            {
                int t = m.Order[i];
                if (!m.Triangles[t].Bounds.Overlaps(bounds, 0)) continue;
                tested++;
                if (MayMeetOpenCell(m.Triangles[t], lo, hi, inflate)) return t;
            }
        }
        return -1;
    }

    /// <summary>1 inside, 0 outside, -1 undecided (ray parity on a closed mesh).</summary>
    internal static int PointInSolid(object handle, double x, double y, double z)
    {
        var r = PointInside(new DVec3(x, y, z), M(handle), 0, new WorkBudget(long.MaxValue, CancellationToken.None));
        return r == InsideResult.Inside ? 1 : r == InsideResult.Outside ? 0 : -1;
    }

    /// <summary>Whether any triangle of mesh <paramref name="a"/> may lie within distance <paramref name="d"/> of any triangle of
    /// mesh <paramref name="b"/>: false only when, for every candidate pair, some unit axis separates their projections
    /// by more than d (a projection is 1-Lipschitz, so that gap is a lower bound on their distance).</summary>
    internal static bool MeshesWithin(object a, object b, double d)
    {
        var ma = M(a); var mb = M(b);
        var hits = new List<int>();
        foreach (var ta in ma.Triangles)
        {
            var lo = ta.Bounds.Min - new DVec3(d, d, d); var hi = ta.Bounds.Max + new DVec3(d, d, d);
            hits.Clear();
            QueryBvh(mb, new Bounds(lo, hi), hits);
            foreach (int j in hits) if (!SeparatedBeyond(ta, mb.Triangles[j], d)) return true;
        }
        return false;
    }

    static bool SeparatedBeyond(in Triangle a, in Triangle b, double d)
    {
        var ea = new[] { a.B - a.A, a.C - a.B, a.A - a.C };
        var eb = new[] { b.B - b.A, b.C - b.B, b.A - b.C };
        var axes = new List<DVec3> { a.Normal, b.Normal };
        foreach (var x in ea) { axes.Add(DVec3.Cross(a.Normal, x)); foreach (var y in eb) axes.Add(DVec3.Cross(x, y)); }
        foreach (var y in eb) axes.Add(DVec3.Cross(b.Normal, y));
        foreach (var ax in axes)
        {
            double len = Math.Sqrt(ax.LengthSquared);
            if (!(len > 1e-12)) continue;
            var n = ax * (1 / len);
            double a0 = DVec3.Dot(a.A, n), a1 = DVec3.Dot(a.B, n), a2 = DVec3.Dot(a.C, n);
            double b0 = DVec3.Dot(b.A, n), b1 = DVec3.Dot(b.B, n), b2 = DVec3.Dot(b.C, n);
            double aMin = Math.Min(a0, Math.Min(a1, a2)), aMax = Math.Max(a0, Math.Max(a1, a2));
            double bMin = Math.Min(b0, Math.Min(b1, b2)), bMax = Math.Max(b0, Math.Max(b1, b2));
            double gap = Math.Max(bMin - aMax, aMin - bMax);
            double rounding = 1e-9 * (1 + Math.Abs(aMin) + Math.Abs(aMax) + Math.Abs(bMin) + Math.Abs(bMax));
            if (gap - rounding > d) return true;
        }
        return false;
    }
}
