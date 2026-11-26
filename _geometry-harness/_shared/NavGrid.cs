using HousecarlCore;
using SkyWright.Engine;

namespace HarnessShared;

// THE NAVMESH GRID REBUILD (task 2026-11-16-navmesh-repair, the grid question). A navmesh's spatial grid, as NVNM stores it
// after the cover triangles (xEdit's "Navmesh Grid"; the engine's BSNavmeshGrid; Mutagen's NavmeshGridDivisor, MaxDistanceX/Y,
// Min, Max and the raw NavmeshGrid bytes):
//   Divisor (u32)                     the grid is Divisor x Divisor cells;
//   Grid Size X, Y (float)            one cell's width in x and in y (Mutagen's MaxDistanceX/Y);
//   Bounds Min, Max (3 x float each)  the box the cells divide;
//   Cells                             Divisor² cells, row by row (row = y, column = x), each a u32 count and that many
//                                     u16 triangle indices.
// The layout is xEdit's; the decode of the cell bytes is houseCARL core's (NavmeshGridDecode). No format document says how
// the grid is filled. This file is that rule, fitted on every navmesh of the base game's five masters by
// `navownprobe --navgrid-oracle`, and its one owner:
//   bounds    the extent of the vertices triangles use, exactly (19,986 of 19,986);
//   divisor   from the triangle count (Divisor, 19,986 of 19,986);
//   size      the extent times the divisor's reciprocal, in float (19,986 of 19,986);
//   cells     each triangle, in index order, in every cell from the one its low x/y bound falls in to the one its high bound
//             falls in, that the triangle itself meets in plan (a separating-axis test; touching counts; a triangle flat in
//             plan is its line).
// The cells match byte for byte in 11,357 of 19,986 navmeshes. The rest differ at cell edges and where the game lists a
// triangle in a cell it does not reach; navownprobe/README.md gives the oracle's classes. Every triangle is listed in every
// cell it meets, which is what a point lookup needs.
// Pure: no plugin is read and nothing is written here.

/// <summary>A rebuilt grid: the header fields as Mutagen names them, the cell lists, and the cell bytes Mutagen's
/// <c>NavmeshGrid</c> holds.</summary>
public sealed record NavGridData(uint Divisor, float SectionX, float SectionY, Vec3 Min, Vec3 Max, ushort[][] Cells, byte[] CellBytes);

public static class NavGrid
{
    /// <summary>The grid of a navmesh with these vertices and triangles (each a triple of vertex indices).</summary>
    public static NavGridData Rebuild(IReadOnlyList<Vec3> vertices, IReadOnlyList<(int A, int B, int C)> triangles)
    {
        var (min, max) = Bounds(vertices, triangles);
        return Fill(vertices, triangles, Divisor(triangles.Count), min, max);
    }

    /// <summary>The box: the extent of the vertices the triangles use (an unused vertex is outside it).</summary>
    public static (Vec3 Min, Vec3 Max) Bounds(IReadOnlyList<Vec3> vertices, IReadOnlyList<(int A, int B, int C)> triangles)
    {
        if (triangles.Count == 0) throw new ArgumentException("A navmesh with no triangles has no grid; give it at least one triangle.");
        if (triangles.Count > ushort.MaxValue + 1) throw new ArgumentException($"A navmesh grid lists triangles by 16-bit index, so it holds at most 65,536; this one has {triangles.Count}, so split it.");
        float lx = float.MaxValue, ly = float.MaxValue, lz = float.MaxValue, hx = float.MinValue, hy = float.MinValue, hz = float.MinValue;
        foreach (var (a, b, c) in triangles)
            foreach (var i in new[] { a, b, c })
            {
                if (i < 0 || i >= vertices.Count) throw new ArgumentException($"A triangle names vertex {i}, but the navmesh has {vertices.Count}; fix the triangle before rebuilding the grid.");
                var v = vertices[i];
                lx = Math.Min(lx, v.X); ly = Math.Min(ly, v.Y); lz = Math.Min(lz, v.Z);
                hx = Math.Max(hx, v.X); hy = Math.Max(hy, v.Y); hz = Math.Max(hz, v.Z);
            }
        return (new Vec3(lx, ly, lz), new Vec3(hx, hy, hz));
    }

    /// <summary>The divisor for a navmesh of this many triangles: 1 up to 16 triangles, then 2 plus one per 50 triangles, at
    /// most 12 (17-49 → 2, 50-99 → 3, 100-149 → 4, ... 500 and over → 12). In the base game every divisor's triangle range
    /// meets its neighbours' with no gap and no overlap.</summary>
    public static uint Divisor(int triangleCount)
    {
        if (triangleCount <= 0) throw new ArgumentException("A navmesh with no triangles has no grid; give it at least one triangle.");
        return triangleCount <= 16 ? 1u : (uint)Math.Min(12, triangleCount / 50 + 2);
    }

    /// <summary>One cell's width: the extent times the divisor's reciprocal, in float. The plain quotient differs from the
    /// stored value in the last bit in 2,037 base-game navmeshes; this form in none.</summary>
    public static float Section(float lo, float hi, uint divisor) => (hi - lo) * (1f / divisor);

    /// <summary>The cells for a given divisor and box.</summary>
    public static NavGridData Fill(IReadOnlyList<Vec3> vertices, IReadOnlyList<(int A, int B, int C)> triangles, uint divisor, Vec3 min, Vec3 max) =>
        Fill(vertices, triangles, divisor, min, max, Section(min.X, max.X, divisor), Section(min.Y, max.Y, divisor));

    /// <summary>The cells for a given divisor, box and cell size. The self-test's sabotage arm passes a wrong size here;
    /// everything else goes through the overload that computes it.</summary>
    internal static NavGridData Fill(IReadOnlyList<Vec3> vertices, IReadOnlyList<(int A, int B, int C)> triangles, uint divisor, Vec3 min, Vec3 max, float sx, float sy)
    {
        if (divisor == 0 || divisor > 1024) throw new ArgumentException($"A grid divisor of {divisor} is outside 1..1024; use the divisor NavGrid.Divisor gives.");
        int n = (int)divisor;
        var ex = Edges(min.X, sx, n); var ey = Edges(min.Y, sy, n);
        var lists = new List<ushort>[n * n];
        for (int k = 0; k < lists.Length; k++) lists[k] = new List<ushort>();
        for (int t = 0; t < triangles.Count; t++)
        {
            var (a, b, c) = triangles[t];
            Vec3 p = vertices[a], q = vertices[b], r = vertices[c];
            float tx0 = Math.Min(p.X, Math.Min(q.X, r.X)), tx1 = Math.Max(p.X, Math.Max(q.X, r.X));
            float ty0 = Math.Min(p.Y, Math.Min(q.Y, r.Y)), ty1 = Math.Max(p.Y, Math.Max(q.Y, r.Y));
            int c0 = Cell(tx0, min.X, sx, n), c1 = Cell(tx1, min.X, sx, n), r0 = Cell(ty0, min.Y, sy, n), r1 = Cell(ty1, min.Y, sy, n);
            for (int row = r0; row <= r1; row++)
                for (int col = c0; col <= c1; col++)
                    if (Meets(p, q, r, ex[col], ey[row], ex[col + 1], ey[row + 1]))
                        lists[row * n + col].Add((ushort)t);
        }
        var cells = lists.Select(l => l.ToArray()).ToArray();
        return new NavGridData(divisor, sx, sy, min, max, cells, Encode(cells));
    }

    /// <summary>The cell a coordinate falls in on one axis: floor((v - lo) / size) in float, held to the grid.</summary>
    static int Cell(float v, float lo, float size, int n) => size <= 0 ? 0 : Math.Clamp((int)MathF.Floor((v - lo) / size), 0, n - 1);

    /// <summary>The n + 1 cell edges on one axis, each the last plus the size, in float.</summary>
    static double[] Edges(float lo, float size, int n)
    {
        var e = new double[n + 1];
        float acc = lo;
        for (int i = 0; i <= n; i++) { e[i] = acc; acc += size; }
        return e;
    }

    /// <summary>Whether a triangle meets an axis-aligned rectangle in plan, touching included: no triangle edge has the whole
    /// rectangle strictly on its outer side (the rectangle's own axes are settled by the cell range). A triangle flat in plan
    /// (its corners on one line) is that line: it misses a rectangle lying strictly to one side of it.</summary>
    static bool Meets(Vec3 p, Vec3 q, Vec3 r, double x0, double y0, double x1, double y1)
    {
        Span<(double X, double Y)> t = stackalloc (double, double)[] { (p.X, p.Y), (q.X, q.Y), (r.X, r.Y) };
        for (int i = 0; i < 3; i++)
        {
            var a = t[i]; var b = t[(i + 1) % 3]; var c = t[(i + 2) % 3];
            double nx = b.Y - a.Y, ny = a.X - b.X;
            if (nx == 0 && ny == 0) continue; // two corners in one place: the other edges decide
            double sc = (c.X - a.X) * nx + (c.Y - a.Y) * ny;
            double s0 = (x0 - a.X) * nx + (y0 - a.Y) * ny, s1 = (x1 - a.X) * nx + (y0 - a.Y) * ny,
                   s2 = (x1 - a.X) * nx + (y1 - a.Y) * ny, s3 = (x0 - a.X) * nx + (y1 - a.Y) * ny;
            double lo = Math.Min(Math.Min(s0, s1), Math.Min(s2, s3)), hi = Math.Max(Math.Max(s0, s1), Math.Max(s2, s3));
            if (sc > 0 ? hi < 0 : sc < 0 ? lo > 0 : hi < 0 || lo > 0) return false;
        }
        return true;
    }

    /// <summary>The cell lists as NVNM stores them: per cell a u32 count and that many u16 triangle indices. The inverse of
    /// houseCARL core's NavmeshGridDecode.Cells, which the self-test holds it to.</summary>
    public static byte[] Encode(ushort[][] cells)
    {
        var bytes = new byte[cells.Sum(c => 4 + 2 * c.Length)];
        int at = 0;
        foreach (var c in cells)
        {
            BitConverter.TryWriteBytes(bytes.AsSpan(at, 4), (uint)c.Length); at += 4;
            foreach (var t in c) { BitConverter.TryWriteBytes(bytes.AsSpan(at, 2), t); at += 2; }
        }
        return bytes;
    }
}
