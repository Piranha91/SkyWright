using System.Numerics;

using HousecarlCore;

namespace SkyWright.Engine;

// The near test of a swept region without a combined mesh (task 2026-10-21-swept-instanced-pose-storage). A vertex whose float
// bits are the same in every sample is static, and a triangle with three static vertices is the same triangle in every sample:
// it is stored once. Every other triangle is stored once per sample, read from that sample's own soup. All stored copies share one
// compact tree. Every triangle tested is one the combined mesh of all samples held, built by the same Triangle constructor from
// the same coordinates and given the same two tests, so MeshMeetsBoxFirst's yes/no is unchanged; only the order of the walk and
// the count of tests differ.
public static partial class MeshIntersection
{
    /// <summary>Test switch (MV6's sabotage arm): true treats every vertex as static, so only sample 0's triangles are stored.</summary>
    internal static bool SweptNearSabotage;

    const int NearLeaf = 4;

    // A compact BVH over items: node boxes as floats (each the min/max of float coordinates, so exact), items in tree order.
    internal sealed class NearTree
    {
        public required float[] Box;      // 6 per node: min x y z, max x y z
        public required int[] Left;       // child node, or -1 for a leaf
        public required int[] Right;
        public required int[] Start;      // a leaf's range in Items
        public required int[] Count;
        public required int[] Items;

        // items' boxes (6 floats each) and centroids (3 doubles each); deterministic: ties broken by item id
        public static NearTree Build(int n, float[] itemBox, double[] centroid)
        {
            var items = Enumerable.Range(0, n).ToArray();
            var box = new List<float>(); var left = new List<int>(); var right = new List<int>(); var start = new List<int>(); var count = new List<int>();
            int Node(int s, int c)
            {
                int id = left.Count;
                left.Add(-1); right.Add(-1); start.Add(s); count.Add(c);
                float x0 = float.MaxValue, y0 = float.MaxValue, z0 = float.MaxValue, x1 = float.MinValue, y1 = float.MinValue, z1 = float.MinValue;
                double cx0 = double.MaxValue, cy0 = double.MaxValue, cz0 = double.MaxValue, cx1 = double.MinValue, cy1 = double.MinValue, cz1 = double.MinValue;
                for (int k = s; k < s + c; k++)
                {
                    int i = items[k] * 6, j = items[k] * 3;
                    x0 = MathF.Min(x0, itemBox[i]); y0 = MathF.Min(y0, itemBox[i + 1]); z0 = MathF.Min(z0, itemBox[i + 2]);
                    x1 = MathF.Max(x1, itemBox[i + 3]); y1 = MathF.Max(y1, itemBox[i + 4]); z1 = MathF.Max(z1, itemBox[i + 5]);
                    cx0 = Math.Min(cx0, centroid[j]); cy0 = Math.Min(cy0, centroid[j + 1]); cz0 = Math.Min(cz0, centroid[j + 2]);
                    cx1 = Math.Max(cx1, centroid[j]); cy1 = Math.Max(cy1, centroid[j + 1]); cz1 = Math.Max(cz1, centroid[j + 2]);
                }
                box.AddRange(new[] { x0, y0, z0, x1, y1, z1 });
                if (c <= NearLeaf) return id;
                double sx = cx1 - cx0, sy = cy1 - cy0, sz = cz1 - cz0;
                int axis = sx >= sy && sx >= sz ? 0 : sy >= sz ? 1 : 2;
                Array.Sort(items, s, c, Comparer<int>.Create((a, b) =>
                {
                    int r = centroid[a * 3 + axis].CompareTo(centroid[b * 3 + axis]);
                    return r != 0 ? r : a.CompareTo(b);
                }));
                int half = c / 2;
                int l = Node(s, half), rr = Node(s + half, c - half);
                left[id] = l; right[id] = rr;
                return id;
            }
            if (n > 0) Node(0, n);
            return new NearTree { Box = box.ToArray(), Left = left.ToArray(), Right = right.ToArray(), Start = start.ToArray(), Count = count.ToArray(), Items = items };
        }

        // the node box against the closed box [lo, hi] (Bounds.Overlaps with tolerance 0)
        public bool Overlaps(int node, double lx, double ly, double lz, double hx, double hy, double hz)
        {
            int b = node * 6;
            return Box[b] <= hx && lx <= Box[b + 3] && Box[b + 1] <= hy && ly <= Box[b + 4] && Box[b + 2] <= hz && lz <= Box[b + 5];
        }
    }

    /// <summary>The near-test index of a swept region's samples (Volume's): every stored triangle copy (static ones once, from
    /// sample 0; moving ones once per sample) as an item (soup, triangle) in one tree, so a cell walks one spatial structure as it
    /// walked the combined mesh. Immutable once built, so the parallel classifier reads it freely.</summary>
    internal sealed class SweptNear
    {
        internal Vector3[][] V = Array.Empty<Vector3[]>();
        internal int[][] I = Array.Empty<int[]>();
        internal int[] Soup = Array.Empty<int>();       // item -> soup
        internal int[] Tri = Array.Empty<int>();        // item -> triangle id in that soup
        internal NearTree Tree = null!;
        public int Instances { get; init; }             // the static block, plus each sample that stores triangles of its own
        public long StaticTriangles { get; init; }
        public long StoredTriangles => Soup.Length;
        public int OwnSamples { get; init; }
    }

    static bool SameBits(Vector3 a, Vector3 b) =>
        BitConverter.SingleToInt32Bits(a.X) == BitConverter.SingleToInt32Bits(b.X)
        && BitConverter.SingleToInt32Bits(a.Y) == BitConverter.SingleToInt32Bits(b.Y)
        && BitConverter.SingleToInt32Bits(a.Z) == BitConverter.SingleToInt32Bits(b.Z);

    /// <summary>Index the samples' soups (each already prepared on its own, so indices are in range). A sample whose index array
    /// or vertex count differs from sample 0's keeps all its triangles as its own. Null with a reason when there is nothing.</summary>
    internal static SweptNear? PrepareSweptNear(IReadOnlyList<TriangleSoup> soups, out string? reason)
    {
        reason = null;
        if (soups.Count == 0 || soups.All(s => s.Indices.Length == 0)) { reason = "mesh_empty_mesh"; return null; }
        var s0 = soups[0];
        int n = s0.Vertices.Length, tris = s0.Indices.Length / 3;
        bool Shares(TriangleSoup s) => s.Vertices.Length == n && (ReferenceEquals(s.Indices, s0.Indices) || s.Indices.AsSpan().SequenceEqual(s0.Indices));
        var shares = soups.Select(Shares).ToArray();
        var moving = new bool[n];
        if (!SweptNearSabotage)
            for (int k = 1; k < soups.Count; k++)
            {
                if (!shares[k]) continue;
                var v = soups[k].Vertices;
                for (int i = 0; i < n; i++) if (!moving[i] && !SameBits(v[i], s0.Vertices[i])) moving[i] = true;
            }
        var stat = new List<int>(); var mov = new List<int>();
        for (int t = 0; t < tris; t++)
            (moving[s0.Indices[t * 3]] || moving[s0.Indices[t * 3 + 1]] || moving[s0.Indices[t * 3 + 2]] ? mov : stat).Add(t);
        var soupOf = new List<int>(); var triOf = new List<int>();
        int instances = 0, own = 0;
        if (stat.Count > 0) { instances++; foreach (int t in stat) { soupOf.Add(0); triOf.Add(t); } }
        for (int k = 0; k < soups.Count; k++)
        {
            var s = soups[k];
            if (shares[k])
            {
                if (mov.Count == 0) continue;
                instances++;
                foreach (int t in mov) { soupOf.Add(k); triOf.Add(t); }
            }
            else if (s.Indices.Length > 0)
            {
                instances++; own++;
                for (int t = 0; t < s.Indices.Length / 3; t++) { soupOf.Add(k); triOf.Add(t); }
            }
        }
        var V = soups.Select(x => x.Vertices).ToArray(); var I = soups.Select(x => x.Indices).ToArray();
        int n2 = soupOf.Count;
        var box = new float[(long)n2 * 6]; var cen = new double[(long)n2 * 3];
        for (int k = 0; k < n2; k++)
        {
            var v = V[soupOf[k]]; var ix = I[soupOf[k]]; int t = triOf[k];
            Vector3 a = v[ix[t * 3]], b = v[ix[t * 3 + 1]], c = v[ix[t * 3 + 2]];
            var lo = Vector3.Min(a, Vector3.Min(b, c)); var hi = Vector3.Max(a, Vector3.Max(b, c));
            box[k * 6] = lo.X; box[k * 6 + 1] = lo.Y; box[k * 6 + 2] = lo.Z; box[k * 6 + 3] = hi.X; box[k * 6 + 4] = hi.Y; box[k * 6 + 5] = hi.Z;
            cen[k * 3] = ((double)a.X + b.X + c.X) / 3; cen[k * 3 + 1] = ((double)a.Y + b.Y + c.Y) / 3; cen[k * 3 + 2] = ((double)a.Z + b.Z + c.Z) / 3;
        }
        return new SweptNear { V = V, I = I, Soup = soupOf.ToArray(), Tri = triOf.ToArray(), Tree = NearTree.Build(n2, box, cen),
                               Instances = instances, StaticTriangles = stat.Count, OwnSamples = own };
    }

    // the triangle's float box against the closed box [lo, hi]: Triangle.Bounds.Overlaps(bounds, 0) without building it
    static bool NearBoxMeets(Vector3 a, Vector3 b, Vector3 c, DVec3 lo, DVec3 hi) =>
        MathF.Min(a.X, MathF.Min(b.X, c.X)) <= hi.X && lo.X <= MathF.Max(a.X, MathF.Max(b.X, c.X))
        && MathF.Min(a.Y, MathF.Min(b.Y, c.Y)) <= hi.Y && lo.Y <= MathF.Max(a.Y, MathF.Max(b.Y, c.Y))
        && MathF.Min(a.Z, MathF.Min(b.Z, c.Z)) <= hi.Z && lo.Z <= MathF.Max(a.Z, MathF.Max(b.Z, c.Z));

    // item k's triangle when its box meets [lo, hi]: tested, then MayMeetOpenCell on the corners and normal the combined mesh's Triangle held
    static bool NearItemMeets(SweptNear near, int k, DVec3 lo, DVec3 hi, double inflate, ref long tested)
    {
        var v = near.V[near.Soup[k]]; var ix = near.I[near.Soup[k]]; int t = near.Tri[k];
        int i0 = ix[t * 3], i1 = ix[t * 3 + 1], i2 = ix[t * 3 + 2];
        Vector3 a = v[i0], b = v[i1], c = v[i2];
        if (!NearBoxMeets(a, b, c, lo, hi)) return false;
        tested++;
        DVec3 A = new(a.X, a.Y, a.Z), B = new(b.X, b.Y, b.Z), C = new(c.X, c.Y, c.Z);
        return MayMeetOpenCell(A, B, C, DVec3.Cross(B - A, C - A), lo, hi, inflate);
    }

    /// <summary>MeshMeetsBoxFirst's answer on the combined mesh of the indexed samples: whether any of their triangles may meet the
    /// open box grown by <paramref name="grow"/>, as the item of one such triangle, or -1. The hint item is tried first;
    /// <paramref name="tested"/> counts per-triangle tests.</summary>
    internal static long NearFirst(SweptNear near, SweptBox box, double grow, long hint, ref long tested)
    {
        var lo = new DVec3(box.MinX - grow, box.MinY - grow, box.MinZ - grow);
        var hi = new DVec3(box.MaxX + grow, box.MaxY + grow, box.MaxZ + grow);
        double inflate = 1e-9 + 1e-12 * (Math.Abs(lo.X) + Math.Abs(hi.X));
        if (hint >= 0 && hint < near.Soup.Length && NearItemMeets(near, (int)hint, lo, hi, inflate, ref tested)) return hint;
        if (near.Soup.Length == 0) return -1;
        var tree = near.Tree;
        var stack = new Stack<int>(); stack.Push(0);
        while (stack.Count > 0)
        {
            int n = stack.Pop();
            if (!tree.Overlaps(n, lo.X, lo.Y, lo.Z, hi.X, hi.Y, hi.Z)) continue;
            if (tree.Left[n] >= 0) { stack.Push(tree.Left[n]); stack.Push(tree.Right[n]); continue; }
            for (int p = tree.Start[n]; p < tree.Start[n] + tree.Count[n]; p++)
                if (NearItemMeets(near, tree.Items[p], lo, hi, inflate, ref tested)) return tree.Items[p];
        }
        return -1;
    }
}
