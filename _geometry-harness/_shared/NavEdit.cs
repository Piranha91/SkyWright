using System.Globalization;
using HousecarlCore;
using Mutagen.Bethesda.Plugins;

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

/// <summary>A triangle edit's outcome: "planned" with this navmesh's edited geometry and grid, the old-to-new triangle
/// numbering (-1 for a removed triangle), every other navmesh whose links changed, and the write ops for all of them; or
/// "refused" with why.</summary>
public sealed record TriEditResult(string Status, IReadOnlyList<string> Why, NavmeshGeometry? After, NavGridData? Grid,
                                   IReadOnlyList<int> Map, IReadOnlyDictionary<FormKey, NavmeshGeometry> Others, IReadOnlyList<RepairOp> Ops)
{
    public static TriEditResult Refused(params string[] why) =>
        new("refused", why, null, null, Array.Empty<int>(), new Dictionary<FormKey, NavmeshGeometry>(), Array.Empty<RepairOp>());
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

    static readonly string[] EdgeName = { "EdgeLink_0_1", "EdgeLink_1_2", "EdgeLink_2_0" };
    static short Field(NavTri t, int e) => e switch { 0 => t.EdgeLink01, 1 => t.EdgeLink12, _ => t.EdgeLink20 };
    static NavTri WithField(NavTri t, int e, short f) => e switch { 0 => t with { EdgeLink01 = f }, 1 => t with { EdgeLink12 = f }, _ => t with { EdgeLink20 = f } };
    static bool Linked(NavTri t, int e) => (t.Flags & (1 << e)) != 0;
    /// <summary>A corner as an edge is matched by: its full position, so an edge directly above or below another (a
    /// stair, a bridge) is a different edge.</summary>
    static Vec3 P(IReadOnlyList<Vec3> v, int i) => v[i];

    /// <summary>REMOVE AND ADD TRIANGLES on navmesh <paramref name="g"/>. A removed triangle's place is taken by the last
    /// triangle (swap-remove, highest removal first), so only moved triangles change index; added vertices and triangles are
    /// appended. <paramref name="cover"/> is each triangle's cover fields as the winner has them (the geometry model does not
    /// carry them), written with a moved triangle; an added triangle has none. Every reference to a
    /// triangle index follows: this navmesh's neighbour fields (one naming a removed triangle becomes an open edge), its
    /// door table, and the link tables of <paramref name="inbound"/> — every other navmesh whose table names this one. A
    /// link into a removed triangle is cut on the other side (its table entry, edge field and flag go, later entries close
    /// up). An added triangle joins each open edge it shares by position, both ways.
    /// Refused by name: removing a triangle whose edge links into another navmesh or that stands before a door; a cut that
    /// leaves two navmeshes with no link between them (their navmesh info map entries would still name each other); an
    /// added triangle that is flat or under 1 u², wound against its neighbours (or, alone, against most of the mesh), names
    /// a vertex that does not exist, lands on an edge that already has two triangles or a link, or overlaps a triangle on
    /// the same level. Edges are matched by their corners' full positions, so a triangle directly above another is not
    /// its neighbour.</summary>
    public static TriEditResult EditTriangles(NavmeshGeometry g, IReadOnlyCollection<int> remove, IReadOnlyList<Vec3> addVerts,
                                              IReadOnlyList<(int A, int B, int C)> addTris, IReadOnlyDictionary<FormKey, NavmeshGeometry> inbound,
                                              IReadOnlyList<(bool IsCover, ushort CoverFlags)>? cover = null, IReadOnlyList<int>? addFlags = null)
    {
        var why = new List<string>();
        var tris = g.Triangles; int n = tris.Count;
        if (remove.Count == 0 && addTris.Count == 0) return TriEditResult.Refused("No triangle is removed or added; name at least one.");
        if (addVerts.Count > 0 && addTris.Count == 0) return TriEditResult.Refused("New vertices are given but no triangle uses them; add the triangles that use them.");
        foreach (var r in remove)
        {
            if (r < 0 || r >= n) { why.Add($"Triangle {r} is not in {g.FormKey}, which has {n}; name a triangle from 0 to {n - 1}."); continue; }
            for (int e = 0; e < 3; e++)
                if (Linked(tris[r], e)) { why.Add($"Triangle {r} links into another navmesh on one edge, so removing it would break that link; cut the link first (the relink repair)."); break; }
            if (g.DoorTriangles?.Any(d => d.TriangleBeforeDoor == r) == true) why.Add($"Triangle {r} stands before a door, which joins this navmesh to another cell; it stays.");
        }
        if (why.Count > 0) return TriEditResult.Refused(why.ToArray());

        // SWAP-REMOVE, highest first: the last triangle takes the removed one's place, so only the moved triangles change
        // index and the write touches their few neighbours, not every later triangle
        var gone = remove.ToHashSet();
        var slot = Enumerable.Range(0, n).ToList();               // slot -> old index
        foreach (var r in gone.OrderByDescending(x => x)) { int last = slot.Count - 1; if (r != last) slot[r] = slot[last]; slot.RemoveAt(last); }
        var map = new int[n]; Array.Fill(map, -1);
        for (int i = 0; i < slot.Count; i++) map[slot[i]] = i;
        var verts = g.Vertices.Concat(addVerts).ToList();
        int kept = slot.Count;

        // the kept triangles in their slots, their in-mesh neighbour fields following the new numbering
        var after = new List<NavTri>(kept + addTris.Count);
        foreach (var i in slot)
        {
            var t = tris[i];
            for (int e = 0; e < 3; e++)
                if (!Linked(t, e) && Field(t, e) >= 0) t = WithField(t, e, (short)(Field(t, e) < n ? map[Field(t, e)] : -1));
            after.Add(t);
        }

        // the added triangles: valid corners, area, then their edges joined to open edges
        int majority = Math.Sign(tris.Select(t => Math.Sign(Cross(g.Vertices, t))).Sum());
        int addIx = 0;
        foreach (var (a, b, c) in addTris)
        {
            if (new[] { a, b, c }.Any(i => i < 0 || i >= verts.Count)) { why.Add($"An added triangle ({a}, {b}, {c}) names a vertex that does not exist; there are {verts.Count} with the added ones."); continue; }
            if (a == b || b == c || a == c) { why.Add($"An added triangle ({a}, {b}, {c}) repeats a corner; give three different vertices."); continue; }
            // an added triangle carries no edge-link bits; other flags (walk preferences, water) may be given for it
            after.Add(new NavTri((short)a, (short)b, (short)c, addFlags is not null && addIx < addFlags.Count ? addFlags[addIx] & ~0b111 : 0));
            addIx++;
        }
        if (why.Count > 0) return TriEditResult.Refused(why.ToArray());
        if (verts.Count > short.MaxValue + 1 || after.Count > short.MaxValue + 1) return TriEditResult.Refused("The navmesh would pass 32,768 vertices or triangles, which its 16-bit indices cannot name; split it.");

        for (int j = kept; j < after.Count; j++)
        {
            var t = after[j];
            double area = Cross(verts, t);
            if (Math.Abs(area) / 2 < 1) { why.Add($"Added triangle {j} ({t.A}, {t.B}, {t.C}) is flat or under 1 u²; give three corners that enclose an area."); continue; }
            for (int e = 0; e < 3; e++)
            {
                var (p, q) = NavBorderMatch.EdgeVerts(t, e);
                var (pp, qq) = (P(verts, p), P(verts, q));
                for (int o = 0; o < after.Count; o++)
                {
                    if (o == j) continue;
                    for (int f = 0; f < 3; f++)
                    {
                        var (r, s) = NavBorderMatch.EdgeVerts(after[o], f);
                        var (rr, ss) = (P(verts, r), P(verts, s));
                        bool same = rr == qq && ss == pp, flipped = rr == pp && ss == qq;
                        if (!same && !flipped) continue;
                        if (flipped) why.Add($"Added triangle {j} is wound against triangle {o}, which shares its edge; list its corners in the other order.");
                        else if (Linked(after[o], f)) why.Add($"Added triangle {j} lands on an edge of triangle {o} that links into another navmesh; it cannot take a third side.");
                        else if (Field(after[o], f) >= 0 && Field(after[o], f) != j) why.Add($"Added triangle {j} lands on an edge that triangles {o} and {Field(after[o], f)} already share.");
                        else if (Field(after[j], e) >= 0 && Field(after[j], e) != o) why.Add($"Added triangle {j} would share one edge with two triangles.");
                        else { after[o] = WithField(after[o], f, (short)j); after[j] = WithField(after[j], e, (short)o); }
                    }
                }
            }
            bool hasNeighbour = Enumerable.Range(0, 3).Any(e => Field(after[j], e) >= 0);
            if (!hasNeighbour && majority != 0 && Math.Sign(area) != majority) why.Add($"Added triangle {j} is wound against most of the navmesh; list its corners in the other order.");
        }
        if (why.Count > 0) return TriEditResult.Refused(why.Distinct().ToArray());

        for (int j = kept; j < after.Count; j++)
            for (int o = 0; o < after.Count; o++)
            {
                if (o == j || (o >= kept && o < j)) continue;
                if (SharesCorner(verts, after[j], after[o]) || !SameLevel(verts, after[j], after[o])) continue;
                if (OverlapDepth(verts, after[j], after[o]) > Touch) why.Add($"Added triangle {j} overlaps triangle {o} on the same level; move it off, or remove triangle {o} first.");
            }
        if (why.Count > 0) return TriEditResult.Refused(why.Distinct().ToArray());

        // this navmesh's own table entries that name itself (a vanilla navmesh can link into its own triangles): follow the
        // numbering; one into a removed triangle is cut here, as on another navmesh
        var ownLinks = (g.EdgeLinks ?? Array.Empty<NavEdgeLink>()).ToList();
        var ownCut = new List<int>();
        for (int k = 0; k < ownLinks.Count; k++)
            if (ownLinks[k].Mesh == g.FormKey && ownLinks[k].TriangleIndex >= 0 && ownLinks[k].TriangleIndex < n)
            {
                int to = map[ownLinks[k].TriangleIndex];
                if (to < 0) ownCut.Add(k); else ownLinks[k] = ownLinks[k] with { TriangleIndex = (short)to };
            }
        if (ownCut.Count > 0)
        {
            var cs = ownCut.ToHashSet();
            for (int t = 0; t < after.Count; t++)
                for (int e = 0; e < 3; e++)
                    if (Linked(after[t], e))
                    {
                        int k = Field(after[t], e);
                        after[t] = cs.Contains(k) ? WithField(after[t], e, -1) with { Flags = after[t].Flags & ~(1 << e) } : WithField(after[t], e, (short)(k - ownCut.Count(c => c < k)));
                    }
            ownLinks = ownLinks.Where((_, k) => !cs.Contains(k)).ToList();
        }

        // the door table, and every other navmesh's links into this one
        var doors = (g.DoorTriangles ?? Array.Empty<NavDoorTriangle>()).Select(d => d with { TriangleBeforeDoor = (short)map[d.TriangleBeforeDoor] }).ToList();
        var others = new Dictionary<FormKey, NavmeshGeometry>();
        var ops = new List<RepairOp>();
        foreach (var (key, o) in inbound)
        {
            var links = (o.EdgeLinks ?? Array.Empty<NavEdgeLink>()).ToList();
            var otris = o.Triangles.ToArray();
            var cut = new List<int>();
            var oops = new List<RepairOp>();
            for (int k = 0; k < links.Count; k++)
                if (links[k].Mesh == g.FormKey && links[k].TriangleIndex >= 0 && links[k].TriangleIndex < n)
                {
                    int to = map[links[k].TriangleIndex];
                    if (to < 0) cut.Add(k);
                    else if (to != links[k].TriangleIndex) links[k] = links[k] with { TriangleIndex = (short)to };
                }
            if (cut.Count == 0 && links.SequenceEqual(o.EdgeLinks ?? Array.Empty<NavEdgeLink>())) continue;
            var cutSet = cut.ToHashSet();
            for (int t = 0; t < otris.Length; t++)
                for (int e = 0; e < 3; e++)
                    if (Linked(otris[t], e))
                    {
                        int k = Field(otris[t], e);
                        if (cutSet.Contains(k)) otris[t] = WithField(otris[t], e, -1) with { Flags = otris[t].Flags & ~(1 << e) };
                        else otris[t] = WithField(otris[t], e, (short)(k - cut.Count(c => c < k)));
                    }
            var newLinks = links.Where((_, k) => !cutSet.Contains(k)).ToList();
            bool stillLinked = newLinks.Any(l => l.Mesh == g.FormKey) || (g.EdgeLinks?.Any(l => l.Mesh == key) ?? false);
            if (!stillLinked) { why.Add($"Removing it would cut the last link between {key} and {g.FormKey}, and their navmesh info map entries would still name each other; that update is not supported yet."); continue; }
            others[key] = o with { Triangles = otris, EdgeLinks = newLinks };
            string id = key.ToString();
            foreach (var k in cut.OrderByDescending(x => x)) oops.Add(new RepairOp(id, "Data.EdgeLinks", "Remove", Key: k.ToString(CultureInfo.InvariantCulture)));
            var before = o.EdgeLinks ?? Array.Empty<NavEdgeLink>();
            var survivors = Enumerable.Range(0, before.Count).Where(k => !cutSet.Contains(k)).ToList();
            for (int k = 0; k < survivors.Count; k++)
                if (newLinks[k].TriangleIndex != before[survivors[k]].TriangleIndex)
                    oops.Add(new RepairOp(id, $"Data.EdgeLinks[{k}].TriangleIndex", "Set", Value: newLinks[k].TriangleIndex.ToString(CultureInfo.InvariantCulture)));
            for (int t = 0; t < otris.Length; t++)
                for (int e = 0; e < 3; e++)
                {
                    if (Field(otris[t], e) != Field(o.Triangles[t], e)) oops.Add(new RepairOp(id, $"Data.Triangles[{t}].{EdgeName[e]}", "Set", Value: Field(otris[t], e).ToString(CultureInfo.InvariantCulture)));
                    if (Linked(o.Triangles[t], e) && !Linked(otris[t], e)) oops.Add(new RepairOp(id, $"Data.Triangles[{t}].Flags", "Remove", Value: EdgeName[e]));
                }
            ops.AddRange(oops);
        }
        if (why.Count > 0) return TriEditResult.Refused(why.Distinct().ToArray());

        // this navmesh's ops: each moved triangle written whole into its new slot (corners, fields, flags, cover), the tail
        // removed from the end, the added vertices and triangles appended, then every field that changed on a triangle that
        // kept its slot
        string gid = g.FormKey.ToString();
        var gops = new List<RepairOp>();
        RepairOp Whole(string verb, string? key, NavTri t, (bool IsCover, ushort CoverFlags) c) => new(gid, "Data.Triangles", verb, Key: key, ComposeNode: new System.Text.Json.Nodes.JsonObject
        {
            ["type"] = "NavmeshTriangle",
            ["fields"] = new System.Text.Json.Nodes.JsonObject
            {
                ["Vertices"] = $"{t.A},{t.B},{t.C}", ["EdgeLink_0_1"] = t.EdgeLink01.ToString(CultureInfo.InvariantCulture),
                ["EdgeLink_1_2"] = t.EdgeLink12.ToString(CultureInfo.InvariantCulture), ["EdgeLink_2_0"] = t.EdgeLink20.ToString(CultureInfo.InvariantCulture),
                ["Flags"] = t.Flags.ToString(CultureInfo.InvariantCulture), ["IsCover"] = c.IsCover ? "true" : "false",
                ["CoverFlags"] = c.CoverFlags.ToString(CultureInfo.InvariantCulture),
            },
        });
        (bool, ushort) CoverOf(int old) => cover is not null && old < cover.Count ? cover[old] : (false, (ushort)0);
        for (int i = 0; i < kept; i++)
            if (slot[i] != i) gops.Add(Whole("SetAtIndex", i.ToString(CultureInfo.InvariantCulture), after[i], CoverOf(slot[i])));
        for (int i = n - 1; i >= kept; i--) gops.Add(new RepairOp(gid, "Data.Triangles", "Remove", Key: i.ToString(CultureInfo.InvariantCulture)));
        foreach (var v in addVerts) gops.Add(new RepairOp(gid, "Data.Vertices", "Add", Value: V(v)));
        for (int j = kept; j < after.Count; j++) gops.Add(Whole("Add", null, after[j], (false, 0)));
        for (int i = 0; i < kept; i++)
            if (slot[i] == i)
                for (int e = 0; e < 3; e++)
                {
                    if (Field(after[i], e) != Field(tris[i], e)) gops.Add(new RepairOp(gid, $"Data.Triangles[{i}].{EdgeName[e]}", "Set", Value: Field(after[i], e).ToString(CultureInfo.InvariantCulture)));
                    if (Linked(tris[i], e) && !Linked(after[i], e)) gops.Add(new RepairOp(gid, $"Data.Triangles[{i}].Flags", "Remove", Value: EdgeName[e]));
                }
        var ownBefore = g.EdgeLinks ?? Array.Empty<NavEdgeLink>();
        foreach (var k in ownCut.OrderByDescending(x => x)) gops.Add(new RepairOp(gid, "Data.EdgeLinks", "Remove", Key: k.ToString(CultureInfo.InvariantCulture)));
        var ownSurvivors = Enumerable.Range(0, ownBefore.Count).Where(k => !ownCut.Contains(k)).ToList();
        for (int k = 0; k < ownSurvivors.Count; k++)
            if (ownLinks[k].TriangleIndex != ownBefore[ownSurvivors[k]].TriangleIndex)
                gops.Add(new RepairOp(gid, $"Data.EdgeLinks[{k}].TriangleIndex", "Set", Value: ownLinks[k].TriangleIndex.ToString(CultureInfo.InvariantCulture)));
        for (int k = 0; k < doors.Count; k++)
            if (doors[k].TriangleBeforeDoor != g.DoorTriangles![k].TriangleBeforeDoor)
                gops.Add(new RepairOp(gid, $"Data.DoorTriangles[{k}].TriangleBeforeDoor", "Set", Value: doors[k].TriangleBeforeDoor.ToString(CultureInfo.InvariantCulture)));
        var triples = after.Select(t => ((int)t.A, (int)t.B, (int)t.C)).ToList();
        var grid = NavGrid.Rebuild(verts, triples);
        gops.Add(new RepairOp(gid, "Data.Min", "Set", Value: V(grid.Min)));
        gops.Add(new RepairOp(gid, "Data.Max", "Set", Value: V(grid.Max)));
        gops.Add(new RepairOp(gid, "Data.NavmeshGridDivisor", "Set", Value: grid.Divisor.ToString(CultureInfo.InvariantCulture)));
        gops.Add(new RepairOp(gid, "Data.MaxDistanceX", "Set", Value: F(grid.SectionX)));
        gops.Add(new RepairOp(gid, "Data.MaxDistanceY", "Set", Value: F(grid.SectionY)));
        gops.Add(new RepairOp(gid, "Data.NavmeshGrid", "Set", Value: Convert.ToHexString(grid.CellBytes)));
        var geom = g with { Vertices = verts, Triangles = after, EdgeLinks = ownLinks, DoorTriangles = doors, Min = grid.Min, Max = grid.Max };
        return new TriEditResult("planned", Array.Empty<string>(), geom, grid, map, others, gops.Concat(ops).ToList());
    }

    /// <summary>How close, in plan, a point must be to a cut line to count as on it.</summary>
    public const double OnLine = 0.01;

    /// <summary>NAVCUT: cut the walkable surface out of navmesh <paramref name="g"/> where an obstacle stands. The obstacle is
    /// a convex outline in plan (<paramref name="outline"/>, either winding) standing from <paramref name="zLo"/> to
    /// <paramref name="zHi"/>; a triangle whose height range meets that band and whose plan overlaps the outline is cut.
    /// A triangle wholly inside goes; one partly inside is clipped against each outline edge into the convex pieces outside
    /// it, each triangulated (a triangle as is; a larger piece fanned from its centre), every new point's height on the
    /// original triangle's plane (on an original edge, along that edge). Where a cut point lands on an edge an uncut
    /// neighbour shares, that neighbour is split at the same point, so no edge is left half-joined. Pieces keep their
    /// triangle's flags except the link bits, and no cover. The rest — numbering, links, doors, refusals — is
    /// <see cref="EditTriangles"/>'s, which this calls. Refused by name: an outline that is not convex, has under three
    /// corners or no area; an empty height band; a cut that touches no triangle; a cut across an edge that links into
    /// another navmesh.</summary>
    public static TriEditResult Cut(NavmeshGeometry g, IReadOnlyList<(float X, float Y)> outline, float zLo, float zHi,
                                    IReadOnlyDictionary<FormKey, NavmeshGeometry> inbound, IReadOnlyList<(bool IsCover, ushort CoverFlags)>? cover = null)
    {
        var C = outline.Select(p => (X: (double)p.X, Y: (double)p.Y)).ToList();
        if (C.Count < 3) return TriEditResult.Refused("A cut outline needs at least three corners.");
        double area2 = 0;
        for (int i = 0; i < C.Count; i++) { var (a, b) = (C[i], C[(i + 1) % C.Count]); area2 += a.X * b.Y - b.X * a.Y; }
        if (Math.Abs(area2) / 2 < 1) return TriEditResult.Refused("The cut outline encloses no area; give its corners in order around it.");
        if (area2 < 0) C.Reverse();                                           // counter-clockwise: inside is on each edge's left
        for (int i = 0; i < C.Count; i++)
        {
            var (a, b, c) = (C[i], C[(i + 1) % C.Count], C[(i + 2) % C.Count]);
            if ((b.X - a.X) * (c.Y - b.Y) - (b.Y - a.Y) * (c.X - b.X) < -1e-9) return TriEditResult.Refused("The cut outline is not convex; split a concave obstacle into convex parts and cut each.");
        }
        if (!(zHi >= zLo)) return TriEditResult.Refused("The cut's height band is empty; give its bottom below its top.");

        var verts = g.Vertices.ToList(); int nv0 = verts.Count;
        var tris = g.Triangles;
        // signed distance of a point from outline edge i: positive inside (left of the edge)
        double Dist(int i, double x, double y)
        {
            var (a, b) = (C[i], C[(i + 1) % C.Count]);
            double ex = b.X - a.X, ey = b.Y - a.Y;
            return (ex * (y - a.Y) - ey * (x - a.X)) / Math.Sqrt(ex * ex + ey * ey);
        }
        bool InBand(NavTri t) { float lo = Math.Min(verts[t.A].Z, Math.Min(verts[t.B].Z, verts[t.C].Z)), hi = Math.Max(verts[t.A].Z, Math.Max(verts[t.B].Z, verts[t.C].Z)); return lo <= zHi && hi >= zLo; }
        double PolyDepth(NavTri t)
        {
            var P = new[] { verts[t.A], verts[t.B], verts[t.C] };
            double depth = double.MaxValue;
            var axes = new List<(double, double)>();
            for (int i = 0; i < 3; i++) { var (a, b) = (P[i], P[(i + 1) % 3]); axes.Add((b.Y - (double)a.Y, a.X - (double)b.X)); }
            for (int i = 0; i < C.Count; i++) { var (a, b) = (C[i], C[(i + 1) % C.Count]); axes.Add((b.Y - a.Y, a.X - b.X)); }
            foreach (var (nx0, ny0) in axes)
            {
                double len = Math.Sqrt(nx0 * nx0 + ny0 * ny0); if (len == 0) continue;
                double nx = nx0 / len, ny = ny0 / len;
                double pmin = P.Min(x => x.X * nx + x.Y * ny), pmax = P.Max(x => x.X * nx + x.Y * ny);
                double qmin = C.Min(x => x.X * nx + x.Y * ny), qmax = C.Max(x => x.X * nx + x.Y * ny);
                depth = Math.Min(depth, Math.Min(pmax, qmax) - Math.Max(pmin, qmin));
            }
            return depth;
        }
        var cut = Enumerable.Range(0, tris.Count).Where(t => InBand(tris[t]) && PolyDepth(tris[t]) > Touch).ToList();
        if (cut.Count == 0) return TriEditResult.Refused("The cut touches no triangle of this navmesh in its height band; check the outline and the band.");
        foreach (var t in cut)
            for (int e = 0; e < 3; e++)
                if (Linked(tris[t], e))
                {
                    var (a, b) = NavBorderMatch.EdgeVerts(tris[t], e);
                    bool crosses = Enumerable.Range(0, C.Count).Any(i => Math.Sign(Math.Round(Dist(i, verts[a].X, verts[a].Y), 6)) != Math.Sign(Math.Round(Dist(i, verts[b].X, verts[b].Y), 6)))
                                   || Enumerable.Range(0, C.Count).All(i => Dist(i, verts[a].X, verts[a].Y) > -OnLine);
                    if (crosses) return TriEditResult.Refused($"The cut reaches triangle {t}'s edge that links into another navmesh; cutting across a navmesh border is not supported yet.");
                }

        // the new points: one per (original edge, outline edge), shared by both triangles on that edge; one per interior
        // position (an outline corner inside a triangle)
        var edgePt = new Dictionary<(int, int, int), int>();
        var posPt = new Dictionary<(long, long, long), int>();
        var splits = new Dictionary<(int, int), List<int>>();                  // original edge (low, high) -> points on it
        int EdgePoint(int a, int b, int i)
        {
            var (lo, hi) = a < b ? (a, b) : (b, a);
            if (edgePt.TryGetValue((lo, hi, i), out var k)) return k;
            Vec3 p = verts[lo], q = verts[hi];
            double dp = Dist(i, p.X, p.Y), dq = Dist(i, q.X, q.Y), s = dp / (dp - dq);
            var v = new Vec3((float)(p.X + (q.X - p.X) * s), (float)(p.Y + (q.Y - p.Y) * s), (float)(p.Z + (q.Z - p.Z) * s));
            verts.Add(v); k = verts.Count - 1; edgePt[(lo, hi, i)] = k;
            (splits.TryGetValue((lo, hi), out var l) ? l : splits[(lo, hi)] = new()).Add(k);
            return k;
        }
        int PlanePoint(NavTri t, double x, double y)
        {
            Vec3 A = verts[t.A], B = verts[t.B], Cc = verts[t.C];
            double den = (B.Y - Cc.Y) * (A.X - Cc.X) + (Cc.X - B.X) * (A.Y - Cc.Y);
            double l1 = ((B.Y - Cc.Y) * (x - Cc.X) + (Cc.X - B.X) * (y - Cc.Y)) / den, l2 = ((Cc.Y - A.Y) * (x - Cc.X) + (A.X - Cc.X) * (y - Cc.Y)) / den;
            float z = (float)(l1 * A.Z + l2 * B.Z + (1 - l1 - l2) * Cc.Z);
            var key = ((long)Math.Round(x * 1000), (long)Math.Round(y * 1000), (long)Math.Round(z * 10));
            if (posPt.TryGetValue(key, out var k)) return k;
            verts.Add(new Vec3((float)x, (float)y, z)); posPt[key] = verts.Count - 1;
            return verts.Count - 1;
        }

        var remove = new HashSet<int>(cut);
        var addT = new List<(int, int, int)>(); var addF = new List<int>();
        void Fan(NavTri t, List<int> poly)
        {
            var pts = poly.Where((v, k) => k == 0 || v != poly[k - 1]).ToList();
            if (pts.Count > 1 && pts[0] == pts[^1]) pts.RemoveAt(pts.Count - 1);
            if (pts.Count < 3) return;
            double a2 = 0;
            for (int k = 0; k < pts.Count; k++) { var (p, q) = (verts[pts[k]], verts[pts[(k + 1) % pts.Count]]); a2 += (double)p.X * q.Y - (double)q.X * p.Y; }
            if (Math.Abs(a2) / 2 < 1e-3) return;                               // a sliver of nothing
            if (pts.Count == 3) { addT.Add((pts[0], pts[1], pts[2])); addF.Add(t.Flags); return; }
            double cx = pts.Average(k => (double)verts[k].X), cy = pts.Average(k => (double)verts[k].Y);
            int c = PlanePoint(t, cx, cy);
            for (int k = 0; k < pts.Count; k++) { addT.Add((c, pts[k], pts[(k + 1) % pts.Count])); addF.Add(t.Flags); }
        }

        var pieces = new List<(NavTri T, List<int> Poly)>();
        foreach (var ti in cut)
        {
            var t = tris[ti];
            int[] corner = { t.A, t.B, t.C };
            // a point of the shrinking polygon: its vertex index and the original edge it lies on ((-1, -1) for none)
            var rem = new List<(int V, (int, int) E)>();
            for (int k = 0; k < 3; k++) rem.Add((corner[k], (-1, -1)));
            (int, int) EdgeOf((int V, (int, int) E) p, (int V, (int, int) E) q)
            {
                int pi = Array.IndexOf(corner, p.V), qi = Array.IndexOf(corner, q.V);
                if (pi >= 0 && qi >= 0) return (p.V, q.V);
                if (pi >= 0 && q.E != (-1, -1) && (q.E.Item1 == p.V || q.E.Item2 == p.V)) return q.E;
                if (qi >= 0 && p.E != (-1, -1) && (p.E.Item1 == q.V || p.E.Item2 == q.V)) return p.E;
                if (p.E != (-1, -1) && p.E == q.E) return p.E;
                return (-1, -1);
            }
            for (int i = 0; i < C.Count && rem.Count >= 3; i++)
            {
                var inside = new List<(int V, (int, int) E)>(); var outside = new List<(int V, (int, int) E)>();
                for (int k = 0; k < rem.Count; k++)
                {
                    var (p, q) = (rem[k], rem[(k + 1) % rem.Count]);
                    double dp = Dist(i, verts[p.V].X, verts[p.V].Y), dq = Dist(i, verts[q.V].X, verts[q.V].Y);
                    if (dp >= -OnLine) inside.Add(p);
                    if (dp <= OnLine) outside.Add(p);
                    if ((dp > OnLine && dq < -OnLine) || (dp < -OnLine && dq > OnLine))
                    {
                        var e = EdgeOf(p, q);
                        int x;
                        if (e != (-1, -1)) x = EdgePoint(e.Item1, e.Item2, i);
                        else
                        {
                            double s = dp / (dp - dq);
                            x = PlanePoint(t, verts[p.V].X + (verts[q.V].X - verts[p.V].X) * s, verts[p.V].Y + (verts[q.V].Y - verts[p.V].Y) * s);
                        }
                        inside.Add((x, e)); outside.Add((x, e));
                    }
                }
                if (outside.Count >= 3) pieces.Add((t, outside.Select(z => z.V).ToList()));
                rem = inside;
            }
            // what is left in rem lies inside the outline: dropped with the triangle
        }

        // pieces abut along the cut's lines, and one can end at a point another's edge runs past (a hole corner, a cut
        // point): put every such point into the edge it lies on, so neighbouring pieces share every vertex
        var onPieces = pieces.SelectMany(pc => pc.Poly).Distinct().ToList();
        foreach (var (t, poly) in pieces)
        {
            var full = new List<int>();
            for (int k = 0; k < poly.Count; k++)
            {
                int a = poly[k], b = poly[(k + 1) % poly.Count];
                full.Add(a);
                Vec3 A = verts[a], B = verts[b];
                double ex = B.X - (double)A.X, ey = B.Y - (double)A.Y, len2 = ex * ex + ey * ey;
                if (len2 == 0) continue;
                var mids = new List<(double S, int V)>();
                foreach (var m in onPieces)
                {
                    if (m == a || m == b) continue;
                    Vec3 M = verts[m];
                    double s = ((M.X - A.X) * ex + (M.Y - A.Y) * ey) / len2;
                    if (s <= 1e-9 || s >= 1 - 1e-9) continue;
                    double dx = A.X + ex * s - M.X, dy = A.Y + ey * s - M.Y;
                    if (dx * dx + dy * dy < OnLine * OnLine && Math.Abs(A.Z + (B.Z - A.Z) * s - M.Z) < 0.05) mids.Add((s, m));
                }
                full.AddRange(mids.OrderBy(x => x.S).Select(x => x.V));
            }
            Fan(t, full);
        }

        // split each uncut neighbour on an edge that got cut points, at the same points
        foreach (var ((lo, hi), pts) in splits)
        {
            var (P, Q) = (verts[lo], verts[hi]);
            for (int ni = 0; ni < tris.Count; ni++)
            {
                if (remove.Contains(ni)) continue;
                var n = tris[ni];
                for (int e = 0; e < 3; e++)
                {
                    var (a, b) = NavBorderMatch.EdgeVerts(n, e);
                    bool fwd = verts[a] == P && verts[b] == Q, back = verts[a] == Q && verts[b] == P;
                    if (!fwd && !back) continue;
                    int x = e switch { 0 => n.C, 1 => n.A, _ => n.B };
                    var start = verts[a];
                    var mid = pts.OrderBy(k => Math.Abs(verts[k].X - start.X) + Math.Abs(verts[k].Y - start.Y)).ToList();
                    var chain = new List<int> { a }; chain.AddRange(mid); chain.Add(b);
                    for (int k = 0; k + 1 < chain.Count; k++) { addT.Add((chain[k], chain[k + 1], x)); addF.Add(n.Flags); }
                    remove.Add(ni);
                }
            }
        }

        return EditTriangles(g, remove.ToList(), verts.Skip(nv0).ToList(), addT, inbound, cover, addF);
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
