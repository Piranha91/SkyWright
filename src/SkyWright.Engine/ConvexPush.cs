using System.Runtime.CompilerServices;

using HousecarlCore;

namespace SkyWright.Engine;

/// <summary>Convex components of a triangle soup and the overlap of two of them at a fixed pose, for the physics drop's contact at
/// the placed pose. A soup splits into its index-connected parts; a part that is closed (every edge in exactly two of its
/// triangles) and convex (every one of its vertices on the inner side of every face plane) is one component, and anything else
/// splits into single triangles. Two components overlap when no separating axis exists among the face normals of both and the
/// cross products of their edge directions; the overlap's minimum translation is the axis needing the least push among the
/// directions the support piece allows (a mesh slab does not push out through an internal edge, <see cref="PushAllowed"/>).</summary>
public static class ConvexPush
{
    public readonly record struct P3(double X, double Y, double Z);
    public readonly record struct T3(P3 A, P3 B, P3 C);

    /// <summary>The components of one soup: each a list of triangle ordinals (k / 3 of the index buffer) and its box.</summary>
    public sealed class Split
    {
        public required T3[] Tris { get; init; }
        public required List<int[]> Comps { get; init; }
        public required List<(P3 Min, P3 Max)> Boxes { get; init; }
        /// <summary>The component each triangle ordinal belongs to.</summary>
        public required int[] CompOf { get; init; }
        /// <summary>Per component: null, or the mesh-slab prism it is (a mesh triangle read with its radius, HavokCollision.MeshSlab).</summary>
        public required SlabInfo?[] Slab { get; init; }
    }

    /// <summary>A mesh-slab prism: its triangle's unit normal, the outward in-plane normal of each of its three rims, and whether each
    /// rim's mesh edge is internal (shared with a coplanar neighbouring triangle of the same soup, so not an edge of the surface).</summary>
    public sealed record SlabInfo(P3 Normal, P3[] RimOut, bool[] Internal);

    static readonly ConditionalWeakTable<TriangleSoup, Split> Cache = new();

    /// <summary>The soup's convex components (cached per soup instance).</summary>
    public static Split Of(TriangleSoup s) => Cache.GetValue(s, Build);

    static P3 V(TriangleSoup s, int i) { var v = s.Vertices[i]; return new P3(v.X, v.Y, v.Z); }

    static Split Build(TriangleSoup s)
    {
        int n = s.Indices.Length / 3;
        var tris = new T3[n];
        for (int k = 0; k < n; k++) tris[k] = new T3(V(s, s.Indices[3 * k]), V(s, s.Indices[3 * k + 1]), V(s, s.Indices[3 * k + 2]));
        var parent = new int[s.Vertices.Length];
        for (int i = 0; i < parent.Length; i++) parent[i] = i;
        int Find(int x) { while (parent[x] != x) { parent[x] = parent[parent[x]]; x = parent[x]; } return x; }
        for (int k = 0; k < n; k++)
        {
            int a = Find(s.Indices[3 * k]);
            parent[Find(s.Indices[3 * k + 1])] = a; parent[Find(s.Indices[3 * k + 2])] = Find(a);
        }
        var groups = new Dictionary<int, List<int>>();
        for (int k = 0; k < n; k++)
        {
            int r = Find(s.Indices[3 * k]);
            if (!groups.TryGetValue(r, out var g)) groups[r] = g = new List<int>();
            g.Add(k);
        }
        var comps = new List<int[]>();
        foreach (var g in groups.Values.OrderBy(g => g[0]))
        {
            if (IsClosed(s, g) && IsConvex(tris, g)) comps.Add(g.ToArray());
            else foreach (var k in g) comps.Add(new[] { k });
        }
        var boxes = comps.Select(c => BoxOf(tris, c)).ToList();
        var compOf = new int[n];
        for (int c = 0; c < comps.Count; c++) foreach (var k in comps[c]) compOf[k] = c;
        return new Split { Tris = tris, Comps = comps, Boxes = boxes, CompOf = compOf, Slab = Slabs(s, tris, comps) };
    }

    /// <summary>The mesh-slab prisms among the components (8 triangles on 6 vertices of their own: the top face, the bottom face facing
    /// the opposite way, then the rims, as MeshSlab writes them), and which of their rims lie on internal edges: an edge of the top
    /// face that a coplanar face (top or bottom) of another prism also has, both ends within 1e-3 (by distance).</summary>
    static SlabInfo?[] Slabs(TriangleSoup s, T3[] tris, List<int[]> comps)
    {
        var info = new SlabInfo?[comps.Count];
        var faces = new List<(int Comp, P3 N, P3[] V)>();
        for (int c = 0; c < comps.Count; c++)
        {
            var g = comps[c];
            if (g.Length != 8) continue;
            var vs = new HashSet<int>();
            foreach (var k in g) { vs.Add(s.Indices[3 * k]); vs.Add(s.Indices[3 * k + 1]); vs.Add(s.Indices[3 * k + 2]); }
            if (vs.Count != 6 || Normal(tris[g[0]]) is not { } n0 || Normal(tris[g[1]]) is not { } n1) continue;
            if (n0.X * n1.X + n0.Y * n1.Y + n0.Z * n1.Z > -1 + 1e-4) continue;
            var top = tris[g[0]]; var tv = new[] { top.A, top.B, top.C };
            var outs = new P3[3];
            for (int e = 0; e < 3; e++)
            {
                var a = tv[e]; var b = tv[(e + 1) % 3];
                double ex = b.X - a.X, ey = b.Y - a.Y, ez = b.Z - a.Z;
                double ox = ey * n0.Z - ez * n0.Y, oy = ez * n0.X - ex * n0.Z, oz = ex * n0.Y - ey * n0.X, len = Math.Sqrt(ox * ox + oy * oy + oz * oz);
                outs[e] = len > 1e-12 ? new P3(ox / len, oy / len, oz / len) : new P3(0, 0, 0);
            }
            info[c] = new SlabInfo(n0, outs, new bool[3]);
            faces.Add((c, n0, tv));
            var bot = tris[g[1]]; faces.Add((c, n0, new[] { bot.A, bot.B, bot.C }));
        }
        // internal-edge match by distance (lab 4, 2026-10-04): both ends within 1e-3, in either orientation. Faces are indexed by the 1e-3
        // grid cell of each edge end and queried over the 27 cells around an end, so two copies of one edge that straddle a grid step
        // still match (a rounded-key match missed such edges, LI-K23).
        const double Tol = 1e-3;
        static (long, long, long) Cell(P3 p) => ((long)Math.Floor(p.X / Tol), (long)Math.Floor(p.Y / Tol), (long)Math.Floor(p.Z / Tol));
        static double D2(P3 a, P3 b) { double x = a.X - b.X, y = a.Y - b.Y, z = a.Z - b.Z; return x * x + y * y + z * z; }
        var byEnd = new Dictionary<(long, long, long), List<(int Comp, P3 N, P3 P, P3 Q)>>();
        foreach (var (c, nn, v) in faces)
            for (int e = 0; e < 3; e++)
            {
                var p = v[e]; var q = v[(e + 1) % 3];
                foreach (var (a, b) in new[] { (p, q), (q, p) })
                {
                    var key = Cell(a);
                    if (!byEnd.TryGetValue(key, out var l)) byEnd[key] = l = new();
                    l.Add((c, nn, a, b));
                }
            }
        for (int c = 0; c < comps.Count; c++)
        {
            if (info[c] is not { } si) continue;
            var top = tris[comps[c][0]]; var tv = new[] { top.A, top.B, top.C };
            for (int e = 0; e < 3; e++)
            {
                var ea = tv[e]; var eb = tv[(e + 1) % 3]; var (cx, cy, cz) = Cell(ea);
                bool found = false;
                for (long dx = -1; dx <= 1 && !found; dx++)
                    for (long dy = -1; dy <= 1 && !found; dy++)
                        for (long dz = -1; dz <= 1 && !found; dz++)
                            if (byEnd.TryGetValue((cx + dx, cy + dy, cz + dz), out var l))
                                found = l.Any(x => x.Comp != c && D2(x.P, ea) <= Tol * Tol && D2(x.Q, eb) <= Tol * Tol &&
                                                   Math.Abs(x.N.X * si.Normal.X + x.N.Y * si.Normal.Y + x.N.Z * si.Normal.Z) > 1 - 1e-4);
                si.Internal[e] = found;
            }
        }
        return info;
    }

    /// <summary>Whether a push of the body along <paramref name="dir"/> may resolve its overlap with support component
    /// <paramref name="comp"/>: always, except a sideways push (|dir · normal| &lt; 0.5) out of a mesh-slab prism through a rim on an
    /// internal edge (the rim facing the push most), which is not a way out of the real surface.</summary>
    public static bool PushAllowed(Split s, int comp, P3 dir)
    {
        if (s.Slab[comp] is not { } si || !si.Internal.Any(x => x)) return true;
        if (Math.Abs(dir.X * si.Normal.X + dir.Y * si.Normal.Y + dir.Z * si.Normal.Z) >= 0.5) return true;
        int best = 0; double bv = double.NegativeInfinity;
        for (int e = 0; e < 3; e++)
        {
            double v = dir.X * si.RimOut[e].X + dir.Y * si.RimOut[e].Y + dir.Z * si.RimOut[e].Z;
            if (v > bv) { bv = v; best = e; }
        }
        return !si.Internal[best];
    }

    static bool IsClosed(TriangleSoup s, List<int> g)
    {
        var edges = new Dictionary<(int, int), int>();
        foreach (var k in g)
            for (int e = 0; e < 3; e++)
            {
                int p = s.Indices[3 * k + e], q = s.Indices[3 * k + (e + 1) % 3];
                var key = p < q ? (p, q) : (q, p);
                edges[key] = edges.GetValueOrDefault(key) + 1;
            }
        return edges.Count > 0 && edges.Values.All(c => c == 2);
    }

    static bool IsConvex(T3[] tris, List<int> g)
    {
        var verts = new HashSet<P3>();
        foreach (var k in g) { verts.Add(tris[k].A); verts.Add(tris[k].B); verts.Add(tris[k].C); }
        double cx = 0, cy = 0, cz = 0;
        foreach (var v in verts) { cx += v.X; cy += v.Y; cz += v.Z; }
        cx /= verts.Count; cy /= verts.Count; cz /= verts.Count;
        double x0 = double.MaxValue, y0 = double.MaxValue, z0 = double.MaxValue, x1 = double.MinValue, y1 = double.MinValue, z1 = double.MinValue;
        foreach (var v in verts) { x0 = Math.Min(x0, v.X); y0 = Math.Min(y0, v.Y); z0 = Math.Min(z0, v.Z); x1 = Math.Max(x1, v.X); y1 = Math.Max(y1, v.Y); z1 = Math.Max(z1, v.Z); }
        double size = Math.Sqrt((x1 - x0) * (x1 - x0) + (y1 - y0) * (y1 - y0) + (z1 - z0) * (z1 - z0));
        double tol = -1e-4 * Math.Max(size, 1.0);
        foreach (var k in g)
        {
            if (Normal(tris[k]) is not { } nn) continue;
            var p0 = tris[k].A;
            double sg = Math.Sign(nn.X * (cx - p0.X) + nn.Y * (cy - p0.Y) + nn.Z * (cz - p0.Z));   // orient the plane so the centre is inside
            foreach (var v in verts)
                if (((v.X - p0.X) * nn.X + (v.Y - p0.Y) * nn.Y + (v.Z - p0.Z) * nn.Z) * sg < tol) return false;
        }
        return true;
    }

    static (P3 Min, P3 Max) BoxOf(T3[] tris, IEnumerable<int> c)
    {
        double x0 = double.MaxValue, y0 = double.MaxValue, z0 = double.MaxValue, x1 = double.MinValue, y1 = double.MinValue, z1 = double.MinValue;
        foreach (var k in c)
            foreach (var v in new[] { tris[k].A, tris[k].B, tris[k].C })
            { x0 = Math.Min(x0, v.X); y0 = Math.Min(y0, v.Y); z0 = Math.Min(z0, v.Z); x1 = Math.Max(x1, v.X); y1 = Math.Max(y1, v.Y); z1 = Math.Max(z1, v.Z); }
        return (new P3(x0, y0, z0), new P3(x1, y1, z1));
    }

    /// <summary>Whether two boxes meet (touching within 1e-6 counts as meeting; the separating test decides).</summary>
    public static bool BoxesMeet((P3 Min, P3 Max) a, (P3 Min, P3 Max) b) =>
        a.Max.X >= b.Min.X - 1e-6 && a.Max.Y >= b.Min.Y - 1e-6 && a.Max.Z >= b.Min.Z - 1e-6 &&
        b.Max.X >= a.Min.X - 1e-6 && b.Max.Y >= a.Min.Y - 1e-6 && b.Max.Z >= a.Min.Z - 1e-6;

    static P3? Normal(T3 t)
    {
        double ux = t.B.X - t.A.X, uy = t.B.Y - t.A.Y, uz = t.B.Z - t.A.Z, vx = t.C.X - t.A.X, vy = t.C.Y - t.A.Y, vz = t.C.Z - t.A.Z;
        double nx = uy * vz - uz * vy, ny = uz * vx - ux * vz, nz = ux * vy - uy * vx, len = Math.Sqrt(nx * nx + ny * ny + nz * nz);
        return len > 1e-12 ? new P3(nx / len, ny / len, nz / len) : null;
    }

    sealed class LexCompare : IComparer<P3>
    {
        public static readonly LexCompare I = new();
        public int Compare(P3 a, P3 b) => a.X != b.X ? a.X.CompareTo(b.X) : a.Y != b.Y ? a.Y.CompareTo(b.Y) : a.Z.CompareTo(b.Z);
    }

    /// <summary>The distinct edge directions of a component: unit vectors with a non-negative x (sign-canonical), rounded to 1e-6.</summary>
    static SortedSet<P3> EdgeDirs(IEnumerable<T3> tris)
    {
        var set = new SortedSet<P3>(LexCompare.I);
        foreach (var t in tris)
            foreach (var (p, q) in new[] { (t.A, t.B), (t.B, t.C), (t.C, t.A) })
            {
                double x = q.X - p.X, y = q.Y - p.Y, z = q.Z - p.Z, len = Math.Sqrt(x * x + y * y + z * z);
                if (len <= 1e-12) continue;
                x /= len; y /= len; z /= len;
                if (x < 0) { x = -x; y = -y; z = -z; }
                set.Add(new P3(Math.Round(x, 6), Math.Round(y, 6), Math.Round(z, 6)));
            }
        return set;
    }

    /// <summary>The overlap of component A (the moving body's) with component B at the given pose: null when a separating axis
    /// exists (or they only touch, within 1e-6); otherwise the unit direction the minimum translation pushes A along, and its
    /// depth. Axes: A's face normals, B's face normals, then each A edge direction × each B edge direction.</summary>
    public static (P3 Dir, double Depth)? Push(IReadOnlyList<T3> a, IReadOnlyList<T3> b, Func<P3, bool>? allowed = null)
    {
        var va = a.SelectMany(t => new[] { t.A, t.B, t.C }).ToArray();
        var vb = b.SelectMany(t => new[] { t.A, t.B, t.C }).ToArray();
        double bestUp = double.PositiveInfinity, bestDown = double.PositiveInfinity; P3 upAx = default, downAx = default;
        bool Axis(P3 n)
        {
            double amin = double.PositiveInfinity, amax = double.NegativeInfinity, bmin = double.PositiveInfinity, bmax = double.NegativeInfinity;
            foreach (var p in va) { double d = p.X * n.X + p.Y * n.Y + p.Z * n.Z; if (d < amin) amin = d; if (d > amax) amax = d; }
            foreach (var p in vb) { double d = p.X * n.X + p.Y * n.Y + p.Z * n.Z; if (d < bmin) bmin = d; if (d > bmax) bmax = d; }
            double up = bmax - amin, down = amax - bmin;            // push A along +n by `up`, or along -n by `down`
            if (Math.Min(up, down) <= 1e-6) return false;          // separated (or touching) on this axis
            if (up < bestUp && (allowed is null || allowed(n))) { bestUp = up; upAx = n; }
            if (down < bestDown && (allowed is null || allowed(new P3(-n.X, -n.Y, -n.Z)))) { bestDown = down; downAx = n; }
            return true;
        }
        foreach (var t in a) if (Normal(t) is { } n && !Axis(n)) return null;
        foreach (var t in b) if (Normal(t) is { } n && !Axis(n)) return null;
        var ea = EdgeDirs(a); var eb = EdgeDirs(b);
        foreach (var e in ea)
            foreach (var f in eb)
            {
                double x = e.Y * f.Z - e.Z * f.Y, y = e.Z * f.X - e.X * f.Z, z = e.X * f.Y - e.Y * f.X, len = Math.Sqrt(x * x + y * y + z * z);
                if (len <= 1e-6) continue;
                if (!Axis(new P3(x / len, y / len, z / len))) return null;
            }
        if (double.IsPositiveInfinity(bestUp) && double.IsPositiveInfinity(bestDown)) return null;
        return bestUp <= bestDown ? (upAx, bestUp) : (new P3(-downAx.X, -downAx.Y, -downAx.Z), bestDown);
    }
}
