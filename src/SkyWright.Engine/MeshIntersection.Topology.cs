using System.Buffers;
using System.Numerics;

using HousecarlCore;

namespace SkyWright.Engine;

public static partial class MeshIntersection
{
    /// <summary>A mesh's welded edges in first-use order, with pooled flat arrays for the checks that walk them. Holds at most
    /// the first two uses of an edge: solid validation stops on any edge whose use count is not two. Per call, never shared.</summary>
    struct MeshEdges : IDisposable
    {
        readonly int[] _slots;
        readonly int _mask, _shift;
        readonly long[] _keys;
        public readonly int[] Uses, UseA, UseB, CornerEdge;
        public int Count;

        public MeshEdges(Mesh mesh, WorkBudget budget)
        {
            int triangles = mesh.Triangles.Length;
            int size = (int)BitOperations.RoundUpToPowerOf2((uint)Math.Max(16, checked(triangles * 4)));
            _slots = ArrayPool<int>.Shared.Rent(size);
            Array.Clear(_slots, 0, size);
            _mask = size - 1;
            _shift = 64 - BitOperations.Log2((uint)size);
            int maxEdges = checked(triangles * 3);
            _keys = ArrayPool<long>.Shared.Rent(maxEdges);
            Uses = ArrayPool<int>.Shared.Rent(maxEdges);
            UseA = ArrayPool<int>.Shared.Rent(maxEdges);
            UseB = ArrayPool<int>.Shared.Rent(maxEdges);
            CornerEdge = ArrayPool<int>.Shared.Rent(maxEdges);
            Count = 0;
            int[] welded = mesh.Welded;
            for (int t = 0; t < triangles; t++)
            {
                budget.Checkpoint(t);
                ref readonly Triangle tri = ref mesh.Triangles[t];
                int w0 = welded[tri.I0], w1 = welded[tri.I1], w2 = welded[tri.I2];
                CornerEdge[t * 3] = Add(w0, w1, t);
                CornerEdge[t * 3 + 1] = Add(w1, w2, t);
                CornerEdge[t * 3 + 2] = Add(w2, w0, t);
            }
        }

        int Add(int from, int to, int triangle)
        {
            long key = ((long)Math.Min(from, to) << 32) | (uint)Math.Max(from, to);
            int use = (triangle << 1) | (from < to ? 1 : 0);
            int slot = (int)(((ulong)key * 0x9E3779B97F4A7C15UL) >> _shift);
            while (true)
            {
                int held = _slots[slot];
                if (held == 0)
                {
                    int edge = Count++;
                    _slots[slot] = edge + 1;
                    _keys[edge] = key;
                    Uses[edge] = 1;
                    UseA[edge] = use;
                    return edge;
                }
                if (_keys[held - 1] == key)
                {
                    int edge = held - 1;
                    if (++Uses[edge] == 2) UseB[edge] = use;
                    return edge;
                }
                slot = (slot + 1) & _mask;
            }
        }

        public readonly void Dispose()
        {
            ArrayPool<int>.Shared.Return(_slots);
            ArrayPool<long>.Shared.Return(_keys);
            ArrayPool<int>.Shared.Return(Uses);
            ArrayPool<int>.Shared.Return(UseA);
            ArrayPool<int>.Shared.Return(UseB);
            ArrayPool<int>.Shared.Return(CornerEdge);
        }
    }

    /// <summary>Whether every welded vertex has one connected incident-face fan, given every edge has exactly two uses.</summary>
    static bool VertexFansConnected(Mesh mesh, in MeshEdges edges, int[] stamp, int[] stack, WorkBudget budget)
    {
        int[] welded = mesh.Welded, cornerEdge = edges.CornerEdge, useA = edges.UseA, useB = edges.UseB;
        int triangles = mesh.Triangles.Length;
        int[] incident = ArrayPool<int>.Shared.Rent(welded.Length), first = ArrayPool<int>.Shared.Rent(welded.Length);
        try
        {
            Array.Clear(incident, 0, welded.Length);
            Array.Clear(stamp, 0, triangles);
            for (int t = 0; t < triangles; t++)
            {
                budget.Checkpoint(t);
                ref readonly Triangle tri = ref mesh.Triangles[t];
                int w0 = welded[tri.I0], w1 = welded[tri.I1], w2 = welded[tri.I2];
                if (incident[w0]++ == 0) first[w0] = t;
                if (incident[w1]++ == 0) first[w1] = t;
                if (incident[w2]++ == 0) first[w2] = t;
            }
            for (int vertex = 0; vertex < welded.Length; vertex++)
            {
                budget.Checkpoint(vertex);
                if (incident[vertex] == 0) continue;
                int mark = vertex + 1, reached = 1, top = 0;
                stack[top++] = first[vertex];
                stamp[first[vertex]] = mark;
                while (top > 0)
                {
                    budget.Cancellation.ThrowIfCancellationRequested();
                    int triangle = stack[--top];
                    ref readonly Triangle tri = ref mesh.Triangles[triangle];
                    int corner = welded[tri.I0] == vertex ? 0 : welded[tri.I1] == vertex ? 1 : 2;
                    for (int side = 0; side < 2; side++)
                    {
                        int edge = cornerEdge[triangle * 3 + (side == 0 ? corner : (corner + 2) % 3)];
                        int ta = useA[edge] >> 1, tb = useB[edge] >> 1;
                        if (stamp[ta] != mark) { stamp[ta] = mark; reached++; stack[top++] = ta; }
                        if (stamp[tb] != mark) { stamp[tb] = mark; reached++; stack[top++] = tb; }
                    }
                }
                if (reached != incident[vertex]) return false;
            }
            return true;
        }
        finally
        {
            ArrayPool<int>.Shared.Return(incident);
            ArrayPool<int>.Shared.Return(first);
        }
    }

    /// <summary>Face adjacency in edge order: three neighbours a triangle, given every edge has exactly two uses.</summary>
    static void FillAdjacency(in MeshEdges edges, int triangles, int[] adjacency, int[] filled, WorkBudget budget)
    {
        Array.Clear(filled, 0, triangles);
        for (int edge = 0; edge < edges.Count; edge++)
        {
            budget.Checkpoint(edge);
            int ta = edges.UseA[edge] >> 1, tb = edges.UseB[edge] >> 1;
            adjacency[ta * 3 + filled[ta]++] = tb;
            adjacency[tb * 3 + filled[tb]++] = ta;
        }
    }
}
