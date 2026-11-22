using System.Globalization;
using HousecarlCore;

namespace HarnessShared;

// NAVMESH EDITING (task 2026-11-16-navmesh-repair; plan gate C:/hc-runs/2026-11-21-navgrid/PLAN.md). The edit model: a pure
// function from a winning navmesh's geometry and a list of edits to the edited geometry, its rebuilt box and grid, and the
// write ops, or a refusal by name. It reads no plugin and writes nothing; `navedit` gathers the inputs and writes the ops
// through the core write engine. The grid is NavGrid's, never patched by hand.
//
// This slice: MOVE VERTICES. A vertex moves with every other index at the same position (a navmesh can repeat a point
// under several indices; moving one would tear the mesh). Refused, each in one sentence:
//   - a vertex index past the navmesh's vertices;
//   - a sideways move of a vertex on an edge that links into another navmesh, or on a cell border (relinking is the
//     relink repair's job, not an edit's; a height-only move of such a vertex is allowed and keeps the link);
//   - a move that folds a triangle (its winding flips) or collapses it (below a quarter of its area, or under 1 u²);
//   - a move that makes a moved triangle overlap another triangle of the navmesh in plan at the same level (their height
//     ranges within LevelGap of each other; a bridge over a floor is not an overlap).
// What a vertex move does not touch: the triangles, their edge fields and flags, the link table, cover and door
// triangles, and the NAVI entry (no neighbour changes). An island navmesh is refused by the caller, which owns NAVI.

/// <summary>Move vertex <paramref name="Vertex"/> to <paramref name="To"/>.</summary>
public sealed record VertexMove(int Vertex, Vec3 To);

/// <summary>An edit's outcome: "planned" with the edited geometry, its grid and the write ops, or "refused" with why.</summary>
public sealed record NavEditResult(string Status, IReadOnlyList<string> Why, NavmeshGeometry? After, NavGridData? Grid,
                                   IReadOnlyList<int> Moved, IReadOnlyList<RepairOp> Ops)
{
    public static NavEditResult Refused(params string[] why) => new("refused", why, null, null, Array.Empty<int>(), Array.Empty<RepairOp>());
}

public static class NavEdit
{
    /// <summary>Two triangles whose height ranges come within this of each other are on one level.</summary>
    public const float LevelGap = 32f;
    /// <summary>The overlap depth, in plan, below which two triangles only touch.</summary>
    public const double Touch = 0.5;
    const float CellSize = 4096f;

    static string F(float v) => v.ToString("R", CultureInfo.InvariantCulture);
    static string V(Vec3 v) => $"{F(v.X)},{F(v.Y)},{F(v.Z)}";

    public static NavEditResult MoveVertices(NavmeshGeometry g, IReadOnlyList<VertexMove> moves)
    {
        if (moves.Count == 0) return NavEditResult.Refused("No vertex move was given; name at least one vertex and where it goes.");
        var verts = g.Vertices.ToArray();
        var why = new List<string>();
        var moved = new SortedSet<int>();
        var target = new Dictionary<int, Vec3>();
        foreach (var m in moves)
        {
            if (m.Vertex < 0 || m.Vertex >= verts.Length) { why.Add($"Vertex {m.Vertex} is not in {g.FormKey}, which has {verts.Length}; name a vertex from 0 to {verts.Length - 1}."); continue; }
            var at = verts[m.Vertex];
            for (int i = 0; i < verts.Length; i++)
                if (verts[i].X == at.X && verts[i].Y == at.Y && verts[i].Z == at.Z)
                {
                    if (target.TryGetValue(i, out var prev) && prev != m.To) why.Add($"Vertex {i} is moved to two places; give each position one move.");
                    target[i] = m.To; moved.Add(i);
                }
        }
        if (why.Count > 0) return NavEditResult.Refused(why.ToArray());

        // a sideways move of a vertex that a link or a cell border depends on
        var linked = new HashSet<int>();
        foreach (var t in g.Triangles)
            for (int e = 0; e < 3; e++)
                if ((t.Flags & (1 << e)) != 0) { var (a, b) = NavBorderMatch.EdgeVerts(t, e); linked.Add(a); linked.Add(b); }
        static bool OnBorder(float c) => Math.Abs(c - MathF.Round(c / CellSize) * CellSize) <= 0.5f;
        foreach (var i in moved)
        {
            var (from, to) = (verts[i], target[i]);
            bool sideways = from.X != to.X || from.Y != to.Y;
            if (!sideways) continue;
            if (linked.Contains(i)) why.Add($"Vertex {i} lies on an edge that links into another navmesh, so moving it sideways would break the link; move it in height only, or relink the border first.");
            else if (OnBorder(from.X) || OnBorder(from.Y)) why.Add($"Vertex {i} lies on a cell border, where the next cell's navmesh meets this one; move it in height only.");
        }
        if (why.Count > 0) return NavEditResult.Refused(why.ToArray());

        var after = verts.ToArray();
        foreach (var i in moved) after[i] = target[i];
        var tris = g.Triangles;
        var touched = Enumerable.Range(0, tris.Count).Where(t => moved.Contains(tris[t].A) || moved.Contains(tris[t].B) || moved.Contains(tris[t].C)).ToList();

        foreach (var t in touched)
        {
            double before = Cross(verts, tris[t]), now = Cross(after, tris[t]);
            if (before == 0) continue; // already flat in plan: its bounds decide, as in the grid
            if (Math.Sign(before) != Math.Sign(now)) why.Add($"Moving it would fold triangle {t} (its corners would turn the other way); move the vertex less far, or toward the triangle.");
            else if (Math.Abs(now) < Math.Abs(before) / 4 || Math.Abs(now) / 2 < 1) why.Add($"Moving it would collapse triangle {t} to {Math.Abs(now) / 2:F1} u²; move the vertex less far.");
        }
        if (why.Count > 0) return NavEditResult.Refused(why.ToArray());

        var touchedSet = touched.ToHashSet();
        foreach (var t in touched)
            for (int o = 0; o < tris.Count; o++)
            {
                if (o == t || (touchedSet.Contains(o) && o < t)) continue;
                if (SharesCorner(after, tris[t], tris[o])) continue;
                if (!SameLevel(after, tris[t], tris[o])) continue;
                if (OverlapDepth(after, tris[t], tris[o]) > Touch)
                    why.Add($"Moving it would make triangle {t} overlap triangle {o} on the same level; move the vertex less far.");
            }
        if (why.Count > 0) return NavEditResult.Refused(why.Distinct().ToArray());

        var triples = tris.Select(t => ((int)t.A, (int)t.B, (int)t.C)).ToList();
        var grid = NavGrid.Rebuild(after, triples);
        var geom = g with { Vertices = after, Min = grid.Min, Max = grid.Max };
        return new NavEditResult("planned", Array.Empty<string>(), geom, grid, moved.ToList(), Ops(g.FormKey.ToString(), moved, after, grid));
    }

    /// <summary>The write ops for an edited navmesh: each moved vertex, the box, and the whole grid header and cells.</summary>
    public static List<RepairOp> Ops(string id, IEnumerable<int> moved, IReadOnlyList<Vec3> after, NavGridData grid)
    {
        var ops = moved.Select(i => new RepairOp(id, "Data.Vertices", "SetAtIndex", Key: i.ToString(CultureInfo.InvariantCulture), Value: V(after[i]))).ToList();
        ops.Add(new RepairOp(id, "Data.Min", "Set", Value: V(grid.Min)));
        ops.Add(new RepairOp(id, "Data.Max", "Set", Value: V(grid.Max)));
        ops.Add(new RepairOp(id, "Data.NavmeshGridDivisor", "Set", Value: grid.Divisor.ToString(CultureInfo.InvariantCulture)));
        ops.Add(new RepairOp(id, "Data.MaxDistanceX", "Set", Value: F(grid.SectionX)));
        ops.Add(new RepairOp(id, "Data.MaxDistanceY", "Set", Value: F(grid.SectionY)));
        ops.Add(new RepairOp(id, "Data.NavmeshGrid", "Set", Value: Convert.ToHexString(grid.CellBytes)));
        return ops;
    }

    static double Cross(IReadOnlyList<Vec3> v, NavTri t)
    {
        Vec3 a = v[t.A], b = v[t.B], c = v[t.C];
        return ((double)b.X - a.X) * ((double)c.Y - a.Y) - ((double)b.Y - a.Y) * ((double)c.X - a.X);
    }

    static bool SharesCorner(IReadOnlyList<Vec3> v, NavTri p, NavTri q)
    {
        foreach (var i in new[] { (int)p.A, p.B, p.C })
            foreach (var j in new[] { (int)q.A, q.B, q.C })
                if (i == j || (v[i].X == v[j].X && v[i].Y == v[j].Y)) return true;
        return false;
    }

    static bool SameLevel(IReadOnlyList<Vec3> v, NavTri p, NavTri q)
    {
        float plo = Math.Min(v[p.A].Z, Math.Min(v[p.B].Z, v[p.C].Z)), phi = Math.Max(v[p.A].Z, Math.Max(v[p.B].Z, v[p.C].Z));
        float qlo = Math.Min(v[q.A].Z, Math.Min(v[q.B].Z, v[q.C].Z)), qhi = Math.Max(v[q.A].Z, Math.Max(v[q.B].Z, v[q.C].Z));
        return plo <= qhi + LevelGap && qlo <= phi + LevelGap;
    }

    /// <summary>How deep two triangles overlap in plan (separating axes; zero or less when they only touch or are apart).</summary>
    public static double OverlapDepth(IReadOnlyList<Vec3> v, NavTri p, NavTri q)
    {
        var P = new[] { v[p.A], v[p.B], v[p.C] }; var Q = new[] { v[q.A], v[q.B], v[q.C] };
        double depth = double.MaxValue;
        foreach (var tri in new[] { P, Q })
            for (int i = 0; i < 3; i++)
            {
                Vec3 a = tri[i], b = tri[(i + 1) % 3];
                double nx = b.Y - (double)a.Y, ny = a.X - (double)b.X, len = Math.Sqrt(nx * nx + ny * ny);
                if (len == 0) continue;
                nx /= len; ny /= len;
                double pmin = P.Min(x => x.X * nx + x.Y * ny), pmax = P.Max(x => x.X * nx + x.Y * ny);
                double qmin = Q.Min(x => x.X * nx + x.Y * ny), qmax = Q.Max(x => x.X * nx + x.Y * ny);
                depth = Math.Min(depth, Math.Min(pmax, qmax) - Math.Max(pmin, qmin));
            }
        return depth;
    }
}
