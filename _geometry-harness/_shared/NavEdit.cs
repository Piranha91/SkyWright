using System.Globalization;
using HousecarlCore;
using SkyWright.Engine;
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
// triangles, and the NAVI entry's neighbours. On an island of the navmesh info map the caller (which owns NAVI) asks
// NavIsland what the island data becomes (_shared/NavIsland.cs).

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

/// <summary>A use-version edit's outcome: "planned" with the carried navmesh as it will stand, its grid, every neighbour whose
/// links into it were re-pointed, the write ops, how many of its border links were matched (and how many of them changed
/// index), how many neighbour links were re-pointed, the neighbours it gains (for the navmesh info map) and its doors; or
/// "refused" with why.</summary>
public sealed record UseVersionResult(string Status, IReadOnlyList<string> Why, NavmeshGeometry? After, NavGridData? Grid,
                                      IReadOnlyDictionary<FormKey, NavmeshGeometry> Others, IReadOnlyList<RepairOp> Ops,
                                      int Matched, int Changed, int Repointed, IReadOnlyList<FormKey> Gained, IReadOnlyList<FormKey> Doors)
{
    /// <summary>The version's ledge links carried as it has them, unverified (a ledge link is not made by edge geometry).</summary>
    public int LedgesCarried { get; init; }
    /// <summary>The neighbours a triangle of the winner links into and none of the version's does (item 3): the navmesh info
    /// map's entry for the navmesh drops them (NavRepairNavi.ForVersion makes its neighbour lists the version's link-table
    /// targets, which also drops a table entry no triangle uses), and the plan row lists them (useVersion.lostNeighbours).</summary>
    public IReadOnlyList<FormKey> Lost { get; init; } = Array.Empty<FormKey>();
    /// <summary>The version's door entries the winner lacks, and the winner's the version lacks (set on a refusal too, so the
    /// caller can say which doors and where they lead).</summary>
    public IReadOnlyList<NavDoorTriangle> DoorsAdded { get; init; } = Array.Empty<NavDoorTriangle>();
    public IReadOnlyList<NavDoorTriangle> DoorsDropped { get; init; } = Array.Empty<NavDoorTriangle>();
    public static UseVersionResult Refused(params string[] why) =>
        new("refused", why, null, null, new Dictionary<FormKey, NavmeshGeometry>(), Array.Empty<RepairOp>(), 0, 0, 0, Array.Empty<FormKey>(), Array.Empty<FormKey>());
}

/// <summary>One member of a carried set (R10): the navmesh as the game takes it now, and the version carried over it.</summary>
public sealed record SetMember(NavmeshGeometry Winner, NavmeshGeometry Version);

/// <summary>A carried set's outcome (R10): "planned" with each member's result (its geometry, grid and own ops), every outside
/// navmesh whose links into the set were re-pointed (one merged change each), all the write ops, and how many neighbour links
/// were re-pointed; or "refused" with why. <see cref="Need"/> names every neighbour a border refusal named (growth by need
/// reads it).</summary>
public sealed record UseVersionSetResult(string Status, IReadOnlyList<string> Why, IReadOnlyList<UseVersionResult> Members,
                                         IReadOnlyDictionary<FormKey, NavmeshGeometry> Others, IReadOnlyList<RepairOp> Ops, int Repointed,
                                         IReadOnlyList<FormKey> Need);

/// <summary>A door edit's outcome: "planned" with the navmesh as it will stand, the triangle the door is set on, the marker's
/// distance from it in plan (0: under it) and its height above it, and the write ops; or "refused" with why (and, when
/// no triangle lies under the marker, the nearest one and how far off it is).</summary>
public sealed record DoorAddResult(string Status, IReadOnlyList<string> Why, NavmeshGeometry? After, int Triangle, double PlanDistance, double Height, IReadOnlyList<RepairOp> Ops)
{
    public static DoorAddResult Refused(string why, double plan = double.NaN, int tri = -1) => new("refused", new[] { why }, null, tri, plan, double.NaN, Array.Empty<RepairOp>());
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
                                              IReadOnlyList<(bool IsCover, ushort CoverFlags)>? cover = null, IReadOnlyList<int>? addFlags = null,
                                              double minArea = 1)
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
            if (Math.Abs(area) / 2 < minArea) { why.Add($"Added triangle {j} ({t.A}, {t.B}, {t.C}) is flat or under {minArea:0.###} u²; give three corners that enclose an area."); continue; }
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

    /// <summary>How close, in plan, a point must be to a cut line to count as on it, and how close a new cut point must be
    /// to an existing one to be that point (welded): a few float steps at world coordinates (0.0156 u past 131,072).
    /// Without the weld, two outlines' lines crossing near one spot made two points a float step apart and pieces between
    /// them that turned over when rounded (rock pile 0E6420).</summary>
    public const double OnLine = 0.05;
    /// <summary>The smallest piece a cut makes, in u²: a smaller one is dropped as a sliver of nothing, and any larger one is
    /// kept, however thin. A cut splits each triangle along the full lines of its outline's edges, so the lines of cuts
    /// side by side (an obstacle cut as convex parts) cross and leave small, valid pieces; the 1 u² floor for a triangle
    /// added by hand is a check on input, not on these.</summary>
    public const double Sliver = 1e-3;
    /// <summary>How deep, in plan, a triangle must reach into a cut outline to be cut: a little over float noise at world
    /// coordinates. A triangle that only shares a line with the outline stays whole; a thin one left inside it by an
    /// earlier cut (Touch, 0.5 u, would spare it) goes.</summary>
    public const double CutDepth = 0.05;
    /// <summary>A cut piece narrower than this, in u, is ground too narrow to stand on: dropped. It comes from an outline
    /// edge whose line runs a hair from one of the triangle's edges, which the cut takes last so the line stays short.</summary>
    public const double Thin = 0.1;

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
        => CutMany(g, new[] { (outline, zLo, zHi) }, inbound, cover);

    /// <summary>SEVERAL CUTS AS ONE EDIT (an obstacle cut as convex parts), in ONE PASS: each triangle in a band is split
    /// into the convex pieces outside the first outline it meets, each piece in turn into those outside the next, and so
    /// on, before anything is triangulated; then the pieces are triangulated and uncut neighbours split once, as
    /// <see cref="Cut"/> does for one outline. A piece that does not reach into an outline passes it whole, so the
    /// outline's lines do not run on through it. A new point is made once per (original edge, outline edge), so every
    /// triangle on that edge shares it. Cutting part after part instead (re-cutting the last cut's triangulated result)
    /// rounds new points to float at every pass and split the rock pile 0E6420 into pieces that no longer joined. An
    /// outline that touches nothing is passed over; refused when none touches anything, and on any refusal of
    /// <see cref="Cut"/>'s. <paramref name="mayCut"/>, when given, names for each outline the triangles it may cut (an
    /// obstacle's part measured on one triangle's own plane is cut from that triangle, not from a floor under it).</summary>
    public static TriEditResult CutMany(NavmeshGeometry g, IReadOnlyList<(IReadOnlyList<(float X, float Y)> Outline, float ZLo, float ZHi)> cuts,
                                        IReadOnlyDictionary<FormKey, NavmeshGeometry> inbound, IReadOnlyList<(bool IsCover, ushort CoverFlags)>? cover = null,
                                        IReadOnlyList<IReadOnlyCollection<int>?>? mayCut = null)
    {
        if (mayCut is not null && mayCut.Count != cuts.Count) return TriEditResult.Refused("Each cut outline needs its own list of the triangles it may cut, or none at all.");
        if (cuts.Count == 0) return TriEditResult.Refused("No cut outline was given; give at least one.");
        var O = new List<List<(double X, double Y)>>();
        foreach (var (outline, zLo, zHi) in cuts)
        {
            var C = outline.Select(p => (X: (double)p.X, Y: (double)p.Y)).ToList();
            if (C.Count < 3) return TriEditResult.Refused("A cut outline needs at least three corners.");
            double area2 = 0;
            for (int i = 0; i < C.Count; i++) { var (a, b) = (C[i], C[(i + 1) % C.Count]); area2 += a.X * b.Y - b.X * a.Y; }
            if (Math.Abs(area2) / 2 < 1) return TriEditResult.Refused("The cut outline encloses no area; give its corners in order around it.");
            if (area2 < 0) C.Reverse();                                       // counter-clockwise: inside is on each edge's left
            for (int i = 0; i < C.Count; i++)
            {
                var (a, b, c) = (C[i], C[(i + 1) % C.Count], C[(i + 2) % C.Count]);
                if ((b.X - a.X) * (c.Y - b.Y) - (b.Y - a.Y) * (c.X - b.X) < -1e-9) return TriEditResult.Refused("The cut outline is not convex; split a concave obstacle into convex parts and cut each.");
            }
            if (!(zHi >= zLo)) return TriEditResult.Refused("The cut's height band is empty; give its bottom below its top.");
            O.Add(C);
        }

        var verts = g.Vertices.ToList(); int nv0 = verts.Count;
        var tris = g.Triangles;
        // signed distance of a point from edge i of outline o: positive inside (left of the edge)
        double Dist(int o, int i, double x, double y)
        {
            var C = O[o]; var (a, b) = (C[i], C[(i + 1) % C.Count]);
            double ex = b.X - a.X, ey = b.Y - a.Y;
            return (ex * (y - a.Y) - ey * (x - a.X)) / Math.Sqrt(ex * ex + ey * ey);
        }
        bool InBand(NavTri t, int o) { float lo = Math.Min(verts[t.A].Z, Math.Min(verts[t.B].Z, verts[t.C].Z)), hi = Math.Max(verts[t.A].Z, Math.Max(verts[t.B].Z, verts[t.C].Z)); return lo <= cuts[o].ZHi && hi >= cuts[o].ZLo; }
        // how deep a plan polygon reaches into outline o (separating axes)
        double Depth(IReadOnlyList<(double X, double Y)> P, int o)
        {
            var C = O[o];
            double depth = double.MaxValue;
            var axes = new List<(double, double)>();
            for (int i = 0; i < P.Count; i++) { var (a, b) = (P[i], P[(i + 1) % P.Count]); axes.Add((b.Y - a.Y, a.X - b.X)); }
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
        List<(double X, double Y)> Plan(IEnumerable<int> vs) => vs.Select(v => ((double)verts[v].X, (double)verts[v].Y)).ToList();
        var hits = new Dictionary<int, List<int>>();
        for (int t = 0; t < tris.Count; t++)
        {
            var P = Plan(new[] { (int)tris[t].A, tris[t].B, tris[t].C });
            for (int o = 0; o < O.Count; o++)
                if ((mayCut?[o] is not { } may || may.Contains(t)) && InBand(tris[t], o) && Depth(P, o) > CutDepth) (hits.TryGetValue(t, out var l) ? l : hits[t] = new()).Add(o);
        }
        if (hits.Count == 0)
            return TriEditResult.Refused(O.Count == 1 ? "The cut touches no triangle of this navmesh in its height band; check the outline and the band."
                                                      : "None of the cut's outlines touches a triangle of this navmesh in its band; that ground is cut already, or the outlines are elsewhere.");
        var cut = hits.Keys.OrderBy(t => t).ToList();
        foreach (var t in cut)
            for (int e = 0; e < 3; e++)
                if (Linked(tris[t], e))
                {
                    var (a, b) = NavBorderMatch.EdgeVerts(tris[t], e);
                    foreach (var o in hits[t])
                    {
                        bool crosses = Enumerable.Range(0, O[o].Count).Any(i => Math.Sign(Math.Round(Dist(o, i, verts[a].X, verts[a].Y), 6)) != Math.Sign(Math.Round(Dist(o, i, verts[b].X, verts[b].Y), 6)))
                                       || Enumerable.Range(0, O[o].Count).All(i => Dist(o, i, verts[a].X, verts[a].Y) > -OnLine);
                        if (crosses) return TriEditResult.Refused($"The cut reaches triangle {t}'s edge that links into another navmesh; cutting across a navmesh border is not supported yet.");
                    }
                }

        // the new points: one per (original edge, outline, outline edge), shared by both triangles on that edge; one per
        // interior position (an outline corner, or two outlines' lines crossing, inside a triangle)
        var edgePt = new Dictionary<(int, int, int, int), int>();
        var centres = new HashSet<int>();
        static bool Near(Vec3 a, Vec3 b) => Math.Abs(a.Z - b.Z) < 1 && ((double)a.X - b.X) * ((double)a.X - b.X) + ((double)a.Y - b.Y) * ((double)a.Y - b.Y) <= OnLine * OnLine;
        var splits = new Dictionary<(int, int), List<int>>();                  // original edge (low, high) -> points on it
        int EdgePoint(int a, int b, int o, int i)
        {
            var (lo, hi) = a < b ? (a, b) : (b, a);
            if (edgePt.TryGetValue((lo, hi, o, i), out var k)) return k;
            Vec3 p = verts[lo], q = verts[hi];
            double dp = Dist(o, i, p.X, p.Y), dq = Dist(o, i, q.X, q.Y), s = dp / (dp - dq);
            var v = new Vec3((float)(p.X + (q.X - p.X) * s), (float)(p.Y + (q.Y - p.Y) * s), (float)(p.Z + (q.Z - p.Z) * s));
            // welded along the edge only: to its ends, or a point already on it, so a point never leaves its edge
            splits.TryGetValue((lo, hi), out var on);
            foreach (var w in new[] { lo, hi }.Concat(on ?? new List<int>()))
                if (Near(verts[w], v)) { edgePt[(lo, hi, o, i)] = w; return w; }
            verts.Add(v); k = verts.Count - 1; edgePt[(lo, hi, o, i)] = k;
            (on ?? (splits[(lo, hi)] = new())).Add(k);
            return k;
        }
        int PlanePoint(NavTri t, double x, double y, bool weld = true)
        {
            Vec3 A = verts[t.A], B = verts[t.B], Cc = verts[t.C];
            double den = (B.Y - Cc.Y) * (A.X - Cc.X) + (Cc.X - B.X) * (A.Y - Cc.Y);
            double l1 = ((B.Y - Cc.Y) * (x - Cc.X) + (Cc.X - B.X) * (y - Cc.Y)) / den, l2 = ((Cc.Y - A.Y) * (x - Cc.X) + (A.X - Cc.X) * (y - Cc.Y)) / den;
            float z = (float)(l1 * A.Z + l2 * B.Z + (1 - l1 - l2) * Cc.Z);
            var v = new Vec3((float)x, (float)y, z);
            // welded to the triangle's corners or a new point near it on the same level; a fan's centre never is (it must
            // stay inside its own piece)
            if (weld)
            {
                foreach (var w in new[] { (int)t.A, t.B, t.C }) if (Near(verts[w], v)) return w;
                for (int w = nv0; w < verts.Count; w++) if (Near(verts[w], v) && !centres.Contains(w)) return w;
            }
            verts.Add(v);
            return verts.Count - 1;
        }

        // the outside piece of polygon P beyond edge i of outline o, in plan: its least width over its edges' directions
        // (a convex polygon's width is least across one of its edges), or null when nothing of P lies outside
        double? OutsideWidth(List<(double X, double Y)> P, int o, int i)
        {
            var outP = new List<(double X, double Y)>();
            for (int k = 0; k < P.Count; k++)
            {
                var (p, q) = (P[k], P[(k + 1) % P.Count]);
                double dp = Dist(o, i, p.X, p.Y), dq = Dist(o, i, q.X, q.Y);
                if (dp <= OnLine) outP.Add(p);
                if ((dp > OnLine && dq < -OnLine) || (dp < -OnLine && dq > OnLine)) { double s = dp / (dp - dq); outP.Add((p.X + (q.X - p.X) * s, p.Y + (q.Y - p.Y) * s)); }
            }
            if (outP.Count < 3) return null;
            double area2 = 0;
            for (int k = 0; k < outP.Count; k++) area2 += outP[k].X * outP[(k + 1) % outP.Count].Y - outP[(k + 1) % outP.Count].X * outP[k].Y;
            if (Math.Abs(area2) / 2 < Sliver) return null;
            double width = double.MaxValue;
            for (int k = 0; k < outP.Count; k++)
            {
                var (a, b) = (outP[k], outP[(k + 1) % outP.Count]);
                double ex = b.X - a.X, ey = b.Y - a.Y, len = Math.Sqrt(ex * ex + ey * ey);
                if (len < 1e-9) continue;
                width = Math.Min(width, outP.Max(v => Math.Abs(ex * (v.Y - a.Y) - ey * (v.X - a.X)) / len));
            }
            return width;
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
            if (Math.Abs(a2) / 2 < Sliver) return;                             // a sliver of nothing
            if (pts.Count == 3) { addT.Add((pts[0], pts[1], pts[2])); addF.Add(t.Flags); return; }
            double cx = pts.Average(k => (double)verts[k].X), cy = pts.Average(k => (double)verts[k].Y);
            int c = PlanePoint(t, cx, cy, weld: false); centres.Add(c);
            for (int k = 0; k < pts.Count; k++) { addT.Add((c, pts[k], pts[(k + 1) % pts.Count])); addF.Add(t.Flags); }
        }

        var pieces = new List<(NavTri T, List<int> Poly)>();
        foreach (var ti in cut)
        {
            var t = tris[ti];
            int[] corner = { t.A, t.B, t.C };
            // a point of a shrinking polygon: its vertex index and the original edge it lies on ((-1, -1) for none)
            (int, int) EdgeOf((int V, (int, int) E) p, (int V, (int, int) E) q)
            {
                int pi = Array.IndexOf(corner, p.V), qi = Array.IndexOf(corner, q.V);
                if (pi >= 0 && qi >= 0) return (p.V, q.V);
                if (pi >= 0 && q.E != (-1, -1) && (q.E.Item1 == p.V || q.E.Item2 == p.V)) return q.E;
                if (qi >= 0 && p.E != (-1, -1) && (p.E.Item1 == q.V || p.E.Item2 == q.V)) return p.E;
                if (p.E != (-1, -1) && p.E == q.E) return p.E;
                return (-1, -1);
            }
            var cur = new List<List<(int V, (int, int) E)>> { corner.Select(c => (c, (-1, -1))).ToList() };
            foreach (var o in hits[ti])
            {
                var next = new List<List<(int V, (int, int) E)>>();
                foreach (var piece in cur)
                {
                    if (Depth(Plan(piece.Select(z => z.V)), o) <= CutDepth) { next.Add(piece); continue; }   // clear of this outline
                    var rem = piece;
                    var todo = Enumerable.Range(0, O[o].Count).ToList();
                    while (todo.Count > 0 && rem.Count >= 3)
                    {
                        // the next outline edge: the first whose outside piece is empty or not thin, so a line that would run
                        // on through the triangle a hair from one of its edges is taken last, when the rest is pinned near
                        // the outline (tried in plan only: no point is made for a split not taken)
                        var plan = rem.Select(z => ((double)verts[z.V].X, (double)verts[z.V].Y)).ToList();
                        int pick = todo.FirstOrDefault(j => OutsideWidth(plan, o, j) is not { } w || w >= Thin, todo[0]);
                        bool thin = OutsideWidth(plan, o, pick) is { } pw && pw < Thin;
                        todo.Remove(pick);
                        int i = pick;
                        var inside = new List<(int V, (int, int) E)>(); var outside = new List<(int V, (int, int) E)>();
                        for (int k = 0; k < rem.Count; k++)
                        {
                            var (p, q) = (rem[k], rem[(k + 1) % rem.Count]);
                            double dp = Dist(o, i, verts[p.V].X, verts[p.V].Y), dq = Dist(o, i, verts[q.V].X, verts[q.V].Y);
                            if (dp >= -OnLine) inside.Add(p);
                            if (dp <= OnLine) outside.Add(p);
                            if ((dp > OnLine && dq < -OnLine) || (dp < -OnLine && dq > OnLine))
                            {
                                var e = EdgeOf(p, q);
                                int x;
                                if (e != (-1, -1)) x = EdgePoint(e.Item1, e.Item2, o, i);
                                else
                                {
                                    double s = dp / (dp - dq);
                                    x = PlanePoint(t, verts[p.V].X + (verts[q.V].X - verts[p.V].X) * s, verts[p.V].Y + (verts[q.V].Y - verts[p.V].Y) * s);
                                }
                                inside.Add((x, e)); outside.Add((x, e));
                            }
                        }
                        // a piece still thinner than Thin is ground too narrow to stand on: dropped, not fanned into slivers
                        if (outside.Count >= 3 && !thin) next.Add(outside);
                        rem = inside;
                    }
                    // what is left in rem lies inside the outline: dropped
                }
                cur = next;
            }
            foreach (var piece in cur) pieces.Add((t, piece.Select(z => z.V).ToList()));
        }

        // pieces abut along the cut's lines, and one can end at a point another's edge runs past (a hole corner, a cut
        // point): put every such point into the edge it lies on, so neighbouring pieces share every vertex
        var onPieces = pieces.SelectMany(pc => pc.Poly).Distinct().ToList();
        foreach (var (t, poly) in pieces)
        {
            // each point near the ring goes into the one edge it lies closest to (near a sharp corner it is near both)
            var best = new Dictionary<int, (int Edge, double S, double D)>();
            for (int k = 0; k < poly.Count; k++)
            {
                int a = poly[k], b = poly[(k + 1) % poly.Count];
                Vec3 A = verts[a], B = verts[b];
                double ex = B.X - (double)A.X, ey = B.Y - (double)A.Y, len2 = ex * ex + ey * ey;
                if (len2 == 0) continue;
                foreach (var m in onPieces)
                {
                    if (poly.Contains(m)) continue;
                    Vec3 M = verts[m];
                    double s = ((M.X - A.X) * ex + (M.Y - A.Y) * ey) / len2;
                    if (s <= 1e-9 || s >= 1 - 1e-9) continue;
                    double dx = A.X + ex * s - M.X, dy = A.Y + ey * s - M.Y, d2 = dx * dx + dy * dy;
                    if (d2 < OnLine * OnLine && Math.Abs(A.Z + (B.Z - A.Z) * s - M.Z) < 0.05 && (!best.TryGetValue(m, out var o) || d2 < o.D)) best[m] = (k, s, d2);
                }
            }
            var full = new List<int>();
            for (int k = 0; k < poly.Count; k++)
            {
                full.Add(poly[k]);
                full.AddRange(best.Where(x => x.Value.Edge == k).OrderBy(x => x.Value.S).Select(x => x.Key));
            }
            Fan(t, full);
        }

        // split each uncut neighbour at the cut points on its edges, on every edge at once (a neighbour can get points on
        // two edges, from two outlines' lines): points on one edge only, a fan from the opposite corner; on more, a fan
        // from its centre
        var byPos = new Dictionary<(Vec3, Vec3), List<int>>();
        foreach (var ((lo, hi), pts) in splits) { byPos[(verts[lo], verts[hi])] = pts; byPos[(verts[hi], verts[lo])] = pts; }
        for (int ni = 0; ni < tris.Count; ni++)
        {
            if (remove.Contains(ni)) continue;
            var n = tris[ni];
            var ring = new List<int>(); int withPts = 0, only = -1;
            for (int e = 0; e < 3; e++)
            {
                var (a, b) = NavBorderMatch.EdgeVerts(n, e);
                ring.Add(a);
                if (!byPos.TryGetValue((verts[a], verts[b]), out var pts)) continue;
                var start = verts[a];
                ring.AddRange(pts.OrderBy(k => Math.Abs(verts[k].X - start.X) + Math.Abs(verts[k].Y - start.Y)));
                withPts++; only = e;
            }
            if (withPts == 0) continue;
            remove.Add(ni);
            if (withPts == 1)
            {
                var (a, b) = NavBorderMatch.EdgeVerts(n, only);
                int x = only switch { 0 => n.C, 1 => n.A, _ => n.B };
                var start = verts[a];
                var chain = new List<int> { a }; chain.AddRange(byPos[(verts[a], verts[b])].OrderBy(k => Math.Abs(verts[k].X - start.X) + Math.Abs(verts[k].Y - start.Y))); chain.Add(b);
                for (int k = 0; k + 1 < chain.Count; k++) { addT.Add((chain[k], chain[k + 1], x)); addF.Add(n.Flags); }
            }
            else Fan(n, ring);
        }

        return EditTriangles(g, remove.ToList(), verts.Skip(nv0).ToList(), addT, inbound, cover, addF, minArea: Sliver);
    }

    /// <summary>USE A VERSION (houseCARL's R2): carry navmesh <paramref name="winner"/>'s key as <paramref name="fromPlugin"/>
    /// has it (<paramref name="version"/>: vertices, triangles, flags, cover and doors, by a CopyFrom of its Data), its links
    /// made true for this order. <paramref name="near"/> holds this order's winning version of every navmesh the version links
    /// into and of every navmesh that links into the winner.
    /// <list type="bullet">
    /// <item>Each of the version's border links is matched again, by edge position, against its neighbour's winning version:
    /// the one triangle of the neighbour with an open edge along it, within <paramref name="tol"/> (the relink's rule,
    /// NavBorderMatch); of several, the one the link already names. None, or several none of which it names, is refused,
    /// listing the edges: a link is never cut silently.</item>
    /// <item>Every neighbour's link into this navmesh is re-pointed to the version's triangle along that edge, in the same
    /// patch, the same way; otherwise its index names the old winner's numbering (an EDGE-IDX finding).</item>
    /// <item>The grid is rebuilt. A neighbour the version links into and the winner did not is returned for the navmesh info
    /// map; one the winner linked into and the version does not is refused (its NAVI entry would still name it).</item>
    /// <item>A LEDGE link (Type 1 or 2) is not made by edge geometry, so it is not matched: the version's own is carried while
    /// it names a triangle its neighbour has (counted, unverified); a neighbour's into this navmesh is kept while the version's
    /// triangle of that index stands on the winner's corners, else refused.</item>
    /// <item>Refused too: a link field past the table. A version whose doors differ from the winner's is carried: the doors
    /// added and dropped come back on the result, and the caller's navmesh info map change makes the entry's linked doors the
    /// version's door table (NavRepairNavi.ForVersion). So is a version that no longer links into a neighbour: the result names
    /// it (Lost), and the same change makes the entry's neighbour lists the version's link targets.</item>
    /// </list></summary>
    public static UseVersionResult UseVersion(NavmeshGeometry winner, NavmeshGeometry version, string fromPlugin,
                                              IReadOnlyDictionary<FormKey, NavmeshGeometry> near, EdgeTolerance tol,
                                              Func<FormKey, string?>? winnerOf = null)
    {
        // a set of one (R10): UseVersionSet is the one implementation of the rules above
        var s = UseVersionSet(new[] { new SetMember(winner, version) }, fromPlugin, near, tol, winnerOf);
        var m = s.Members[0];
        return s.Status == "planned" ? m with { Others = s.Others, Ops = s.Ops, Repointed = s.Repointed }
                                     : UseVersionResult.Refused(s.Why.ToArray()) with { DoorsAdded = m.DoorsAdded, DoorsDropped = m.DoorsDropped };
    }

    /// <summary>CARRY A SET OF NAVMESHES (R10, Gate A C:/hc-runs/2026-11-27-navmesh-r10/GATE-A.md section 1): every member's
    /// version from <paramref name="fromPlugin"/>, carried whole over its winner in one patch. The rules are use_version's, with
    /// one change of what a border is matched against: a link between two members is matched against the other member's
    /// version (the plugin saved them agreeing), and a link to anything else against the navmesh the game takes
    /// (<paramref name="near"/>, which holds no member). A ledge link into a member from a navmesh of the plugin's own layout
    /// (a member, or one it already wins: <paramref name="pWins"/>) is carried as the plugin has it while its index is in range;
    /// from anywhere else it is kept only where the version's triangle stands on the winner's corners. An outside navmesh
    /// linked into several members gets one merged change. Every refusal that names a neighbour for a border reason (a link
    /// meeting no edge, or several; one not returned; a link in that cannot be re-pointed; a dropped neighbour still linking in;
    /// a ledge into a triangle the neighbour lacks) ends with <paramref name="ending"/>(member, neighbour) and puts the
    /// neighbour in Need. In a set of more than one, each sentence starts with its member.</summary>
    public static UseVersionSetResult UseVersionSet(IReadOnlyList<SetMember> members, string fromPlugin, IReadOnlyDictionary<FormKey, NavmeshGeometry> near,
                                                    EdgeTolerance tol, Func<FormKey, string?>? winnerOf = null, Func<FormKey, bool>? pWins = null,
                                                    Func<FormKey, FormKey, string>? ending = null)
    {
        var set = new Dictionary<FormKey, SetMember>();
        foreach (var m in members) set[m.Winner.FormKey] = m;
        var why = new List<string>(); var need = new List<FormKey>();
        string Of(FormKey k) => members.Count > 1 ? $"{k}: " : "";
        string End(FormKey k, FormKey m) => ending?.Invoke(k, m) ?? $"keep the winner's version, or carry {fromPlugin}'s version of {m} with it (\"with_neighbours\": true).";
        void Border(FormKey k, FormKey m, string fact) { if (!need.Contains(m)) need.Add(m); why.Add($"{Of(k)}{fact}; {End(k, m)}"); }
        bool InLayout(FormKey m) => set.ContainsKey(m) || (pWins?.Invoke(m) ?? false);
        // what a border is matched against: the member's version inside the set, the navmesh the game takes outside it
        NavmeshGeometry? Facing(FormKey m) => set.TryGetValue(m, out var sm) ? sm.Version : near.TryGetValue(m, out var nm) ? nm : null;
        static bool Open(NavTri t, int e) => Linked(t, e) || Field(t, e) < 0;
        List<int> Along(NavmeshGeometry g, Vec3 a, Vec3 b)
        {
            var hit = new List<int>();
            for (int u = 0; u < g.Triangles.Count; u++)
                for (int f = 0; f < 3; f++)
                {
                    if (!Open(g.Triangles[u], f)) continue;
                    var (i, j) = NavBorderMatch.EdgeVerts(g.Triangles[u], f);
                    if (NavBorderMatch.Gap(a, b, g.Vertices[i], g.Vertices[j]).Within(tol)) { hit.Add(u); break; }
                }
            return hit;
        }
        // of several triangles along an edge (a long edge facing two short ones), the one the link already names stands
        static bool SameCorners(NavmeshGeometry a, NavmeshGeometry b, int x)
        {
            var ta = a.Triangles[x]; var tb = b.Triangles[x];
            var pa = new[] { a.Vertices[ta.A], a.Vertices[ta.B], a.Vertices[ta.C] }.OrderBy(v => v.X).ThenBy(v => v.Y).ThenBy(v => v.Z);
            var pb = new[] { b.Vertices[tb.A], b.Vertices[tb.B], b.Vertices[tb.C] }.OrderBy(v => v.X).ThenBy(v => v.Y).ThenBy(v => v.Z);
            return pa.SequenceEqual(pb);
        }
        static List<int> Keep(List<int> hit, int named) => hit.Count > 1 && hit.Contains(named) ? new List<int> { named } : hit;
        static string Where(Vec3 a, Vec3 b) => FormattableString.Invariant($"({a.X:0.#}, {a.Y:0.#}, {a.Z:0.#})-({b.X:0.#}, {b.Y:0.#}, {b.Z:0.#})");

        // each member's own links, matched again: against the other members' versions, and against the game's navmeshes
        var linksOf = new Dictionary<FormKey, List<NavEdgeLink>>();
        var counts = new Dictionary<FormKey, (int Matched, int Ledges, int Changed)>();
        foreach (var mem in members)
        {
            var key = mem.Winner.FormKey; var V = mem.Version;
            var links = (V.EdgeLinks ?? Array.Empty<NavEdgeLink>()).ToList();
            var assigned = new Dictionary<int, short>();
            var oneWay = new Dictionary<FormKey, List<(int T, int N)>>();
            int matched = 0, ledges = 0;
            for (int t = 0; t < V.Triangles.Count; t++)
                for (int e = 0; e < 3; e++)
                {
                    if (!Linked(V.Triangles[t], e)) continue;
                    int k = Field(V.Triangles[t], e);
                    if (k < 0 || k >= links.Count) { why.Add($"{Of(key)}{fromPlugin}'s triangle {t} edge {e} names link entry {k}, past its table of {links.Count}; that version is broken as it stands."); continue; }
                    var m = links[k].Mesh;
                    if (m == key) continue;                                            // a link into itself keeps its own numbering
                    bool inSet = set.ContainsKey(m);
                    if (Facing(m) is not { } n) { why.Add($"{Of(key)}{fromPlugin}'s triangle {t} links into {m}, which no plugin of this order provides; that link cannot be made true."); continue; }
                    if (links[k].Type is 1 or 2)
                    {
                        // a LEDGE link (Ledge Up / Down) is not made by edge geometry: carried as the version has it, unverified,
                        // while it names a triangle the navmesh it faces has
                        if (links[k].TriangleIndex < 0 || links[k].TriangleIndex >= n.Triangles.Count)
                            Border(key, m, $"{fromPlugin}'s triangle {t} has a ledge link into {m} triangle {links[k].TriangleIndex}, which {(inSet ? $"{fromPlugin}'s version of it" : $"{m}'s winning version")} does not have ({n.Triangles.Count})");
                        else ledges++;
                        continue;
                    }
                    var (pa, pb) = NavBorderMatch.EdgeVerts(V.Triangles[t], e);
                    var hit = Keep(Along(n, V.Vertices[pa], V.Vertices[pb]), links[k].TriangleIndex);
                    if (hit.Count != 1)
                    {
                        Border(key, m, $"{fromPlugin}'s triangle {t} edge {Where(V.Vertices[pa], V.Vertices[pb])} links into {m}, but "
                               + (hit.Count == 0 ? (inSet ? $"no edge of {fromPlugin}'s version of it lies along it" : "no edge of its winning version lies along it")
                                                 : $"{hit.Count} of its triangles lie along it ({string.Join(", ", hit.Take(4))})"));
                        continue;
                    }
                    if (assigned.TryGetValue(k, out var prev) && prev != hit[0]) { Border(key, m, $"{fromPlugin}'s link entry {k} serves two edges that now meet different triangles of {m} ({prev} and {hit[0]}), so that table cannot be kept as it is"); continue; }
                    assigned[k] = (short)hit[0]; matched++;
                    // the far edge must link back into this navmesh, or the carried link is one-way (the review of R7)
                    if (!LinksBack(n, hit[0], V.Vertices[pa], V.Vertices[pb], key, tol)) (oneWay.TryGetValue(m, out var ow) ? ow : oneWay[m] = new()).Add((t, hit[0]));
                }
            foreach (var (m, pairs) in oneWay.OrderBy(kv => kv.Key.ToString(), StringComparer.Ordinal))
                Border(key, m, $"{fromPlugin}'s version of {key} links {string.Join(", ", pairs.Select(p => $"triangle {p.T}"))} into {m} "
                        + $"{string.Join(", ", pairs.Select(p => $"triangle {p.N}"))}{(set.ContainsKey(m) ? $" ({fromPlugin}'s version, carried with it)" : winnerOf?.Invoke(m) is { } wp ? $" ({wp}'s version, the one the game takes)" : "")}, "
                        + $"whose edge there does not link back to {key}, so carrying it would make that border one-way");
            int changed = 0;
            foreach (var (k, to) in assigned) if (links[k].TriangleIndex != to) { links[k] = links[k] with { TriangleIndex = to }; changed++; }
            linksOf[key] = links;
            counts[key] = (matched, ledges, changed);
        }

        // every outside navmesh's links into a member, re-pointed to the member version's triangles: one merged change each
        var oNow = new Dictionary<FormKey, List<NavEdgeLink>>();
        foreach (var mem in members)
        {
            var key = mem.Winner.FormKey; var V = mem.Version;
            foreach (var (ok, o) in near)
            {
                if (set.ContainsKey(ok)) continue;
                var olinks = (oNow.TryGetValue(ok, out var acc) ? acc : o.EdgeLinks ?? Array.Empty<NavEdgeLink>()).ToList();
                if (!olinks.Any(l => l.Mesh == key)) continue;
                var oassigned = new Dictionary<int, short>();
                for (int u = 0; u < o.Triangles.Count; u++)
                    for (int f = 0; f < 3; f++)
                    {
                        if (!Linked(o.Triangles[u], f)) continue;
                        int j = Field(o.Triangles[u], f);
                        if (j < 0 || j >= olinks.Count || olinks[j].Mesh != key) continue;
                        int named = (o.EdgeLinks ?? Array.Empty<NavEdgeLink>())[j].TriangleIndex;
                        if (olinks[j].Type is 1 or 2)
                        {
                            // a ledge link into this navmesh: from the plugin's own layout it names the version's numbering and is
                            // kept while in range; from anywhere else, kept where the version's triangle of that index stands on the
                            // winner's corners. Never re-pointed by position
                            bool keep = InLayout(ok) ? named >= 0 && named < V.Triangles.Count
                                                     : named >= 0 && named < V.Triangles.Count && named < mem.Winner.Triangles.Count && SameCorners(V, mem.Winner, named);
                            if (!keep) Border(key, ok, $"{ok}'s triangle {u} has a ledge link into {key} triangle {named}, which {fromPlugin}'s version does not keep in place; a ledge link cannot be re-pointed by edge position");
                            continue;
                        }
                        var (pa, pb) = NavBorderMatch.EdgeVerts(o.Triangles[u], f);
                        var hit = Keep(Along(V, o.Vertices[pa], o.Vertices[pb]), named);
                        if (hit.Count != 1) { Border(key, ok, $"{ok}'s triangle {u} edge {Where(o.Vertices[pa], o.Vertices[pb])} links into {key}, but {(hit.Count == 0 ? $"no edge of {fromPlugin}'s version lies along it" : $"{hit.Count} triangles of {fromPlugin}'s version lie along it")}, so that link cannot be re-pointed"); continue; }
                        if (oassigned.TryGetValue(j, out var prev) && prev != hit[0]) { Border(key, ok, $"{ok}'s link entry {j} serves edges into {key} that meet different triangles ({prev} and {hit[0]}) of {fromPlugin}'s version, so that table cannot be kept as it is"); continue; }
                        // the member version's edge there must link back to the outside navmesh, or the re-pointed link is one-way
                        // (the review of Gate C, decision 2: 10925D into USSEP's 10925E on CV-clockwork)
                        if (!LinksBack(V, hit[0], o.Vertices[pa], o.Vertices[pb], ok, tol))
                        {
                            Border(key, ok, $"{ok}'s triangle {u} links into {key} triangle {hit[0]} of {fromPlugin}'s version, whose edge there does not link back to {ok}, so carrying it would leave that border one-way");
                            continue;
                        }
                        oassigned[j] = (short)hit[0];
                    }
                foreach (var (j, to) in oassigned) olinks[j] = olinks[j] with { TriangleIndex = to };
                oNow[ok] = olinks;
            }
        }

        // per member: the neighbours and doors the navmesh info map records
        var results = new List<UseVersionResult>();
        foreach (var mem in members)
        {
            var key = mem.Winner.FormKey; var V = mem.Version; var winner = mem.Winner;
            var after = V with { EdgeLinks = linksOf[key] };
            var was = NavRepairNavi.Neighbours(winner); var now = NavRepairNavi.Neighbours(after);
            var lost = was.Where(m => m != key && !now.Contains(m)).OrderBy(m => m.ToString()).ToList();
            // a neighbour the version no longer links into is carried, unless that neighbour, as it will stand, still links into
            // this navmesh: a one-way border, refused by name
            foreach (var m in lost.Where(m => (set.TryGetValue(m, out var sm) ? sm.Version : near.GetValueOrDefault(m))?.EdgeLinks is { } el && el.Any(l => l.Mesh == key)))
                Border(key, m, $"{m} still links into {key}, but {fromPlugin}'s version no longer links back into {m}, so carrying it would leave a one-way border between them");
            static List<NavDoorTriangle> Beyond(IReadOnlyList<NavDoorTriangle>? a, IReadOnlyList<NavDoorTriangle>? b)
            {
                var left = (b ?? Array.Empty<NavDoorTriangle>()).GroupBy(d => d.Door).ToDictionary(g => g.Key, g => g.Count());
                var o = new List<NavDoorTriangle>();
                foreach (var d in a ?? Array.Empty<NavDoorTriangle>())
                    if (left.TryGetValue(d.Door, out var n) && n > 0) left[d.Door] = n - 1; else o.Add(d);
                return o;
            }
            var added = Beyond(V.DoorTriangles, winner.DoorTriangles);
            var dropped = Beyond(winner.DoorTriangles, V.DoorTriangles);
            var doorsNow = (V.DoorTriangles ?? Array.Empty<NavDoorTriangle>()).Select(d => d.Door).OrderBy(d => d.ToString()).ToList();
            var (mc, lc, cc) = counts[key];
            results.Add(new UseVersionResult("planned", Array.Empty<string>(), after, null, new Dictionary<FormKey, NavmeshGeometry>(), Array.Empty<RepairOp>(), mc, cc, 0,
                                             now.Where(m => m != key && !was.Contains(m)).OrderBy(m => m.ToString()).ToList(), doorsNow)
                        { LedgesCarried = lc, DoorsAdded = added, DoorsDropped = dropped, Lost = lost });
        }
        if (why.Count > 0)
            return new UseVersionSetResult("refused", why.Distinct().ToArray(), results.Select(r => UseVersionResult.Refused() with { DoorsAdded = r.DoorsAdded, DoorsDropped = r.DoorsDropped }).ToList(),
                                           new Dictionary<FormKey, NavmeshGeometry>(), Array.Empty<RepairOp>(), 0, need);

        // planned: each member carried whole, its re-matched link indices set, its grid rebuilt; then every outside change
        var ops = new List<RepairOp>();
        for (int i = 0; i < results.Count; i++)
        {
            var V = members[i].Version; var after = results[i].After!; string id = members[i].Winner.FormKey.ToString();
            var grid = NavGrid.Rebuild(after.Vertices, after.Triangles.Select(t => ((int)t.A, (int)t.B, (int)t.C)).ToList());
            after = after with { Min = grid.Min, Max = grid.Max };
            var mops = new List<RepairOp> { new(id, "Data", "CopyFrom", FromPlugin: fromPlugin) };
            var links = after.EdgeLinks!;
            for (int k = 0; k < links.Count; k++)
                if (links[k].TriangleIndex != (V.EdgeLinks ?? Array.Empty<NavEdgeLink>())[k].TriangleIndex)
                    mops.Add(new RepairOp(id, $"Data.EdgeLinks[{k}].TriangleIndex", "Set", Value: links[k].TriangleIndex.ToString(CultureInfo.InvariantCulture)));
            mops.AddRange(Ops(id, Array.Empty<int>(), after.Vertices, grid));
            ops.AddRange(mops);
            results[i] = results[i] with { After = after, Grid = grid, Ops = mops };
        }
        var others = new Dictionary<FormKey, NavmeshGeometry>();
        int repointed = 0;
        foreach (var (ok, o) in near)
        {
            if (!oNow.TryGetValue(ok, out var olinks)) continue;
            var before = o.EdgeLinks ?? Array.Empty<NavEdgeLink>();
            var diff = Enumerable.Range(0, olinks.Count).Where(j => olinks[j].TriangleIndex != before[j].TriangleIndex).ToList();
            if (diff.Count == 0) continue;
            repointed += diff.Count;
            others[ok] = o with { EdgeLinks = olinks };
            foreach (var j in diff) ops.Add(new RepairOp(ok.ToString(), $"Data.EdgeLinks[{j}].TriangleIndex", "Set", Value: olinks[j].TriangleIndex.ToString(CultureInfo.InvariantCulture)));
        }
        return new UseVersionSetResult("planned", Array.Empty<string>(), results, others, ops, repointed, need);
    }

    /// <summary>Whether triangle <paramref name="tri"/> of <paramref name="n"/> has an open edge along a-b (within
    /// <paramref name="tol"/>) that links into <paramref name="back"/>: the far side of a border returns it. One owner, for the
    /// planner and the written patch's two-way read-back.</summary>
    public static bool LinksBack(NavmeshGeometry n, int tri, Vec3 a, Vec3 b, FormKey back, EdgeTolerance tol)
    {
        if (tri < 0 || tri >= n.Triangles.Count) return false;
        var nt = n.Triangles[tri]; var nl = n.EdgeLinks ?? Array.Empty<NavEdgeLink>();
        for (int f = 0; f < 3; f++)
        {
            if (!(Linked(nt, f) || Field(nt, f) < 0)) continue;
            var (i, j) = NavBorderMatch.EdgeVerts(nt, f);
            if (i >= n.Vertices.Count || j >= n.Vertices.Count || !NavBorderMatch.Gap(a, b, n.Vertices[i], n.Vertices[j]).Within(tol)) continue;
            int x = Field(nt, f);
            if (Linked(nt, f) && x >= 0 && x < nl.Count && nl[x].Mesh == back) return true;
        }
        return false;
    }

    /// <summary>A door's arrival marker may stand this far, in u, above or below the triangle it is set on: past the base game's
    /// furthest (781 u, of 1,703 door-table entries in Skyrim.esm, 2026-11-22).</summary>
    public const float DoorHeightLimit = 800f;

    /// <summary>ADD A DOOR (houseCARL's R9): navmesh <paramref name="g"/> gains <paramref name="door"/> in its door table, on
    /// the triangle under the door's ARRIVAL MARKER (<paramref name="marker"/>, where the door puts an actor arriving through
    /// it: the teleport position its partner door names). In the base game that triangle holds the marker in plan in 1,703
    /// of 1,703 door-table entries, and of the triangles that do (119 stand over more than one), the one whose plane is
    /// nearest the marker in height is the table's in all 1,703; the door itself stands a median 41 u off it. The triangle
    /// is flagged Door (every base-game door triangle is, and no other), and the table entry carries the base game's one
    /// Unknown value (NavRepairNavi.LinkedDoorUnknown). The geometry is untouched, so the grid and box stay the winner's.
    /// Refused by name: a door the table already has; no triangle under the marker (saying how far the nearest is, in
    /// plan); a marker further than <see cref="DoorHeightLimit"/> from the triangle under it.</summary>
    public static DoorAddResult AddDoor(NavmeshGeometry g, FormKey door, Vec3 marker)
    {
        if ((g.DoorTriangles ?? Array.Empty<NavDoorTriangle>()).Any(d => d.Door == door))
            return DoorAddResult.Refused($"{g.FormKey}'s door table already has {door}; there is nothing to add.");
        var V = g.Vertices; var T = g.Triangles;
        double? PlaneAt(NavTri t)
        {
            Vec3 a = V[t.A], b = V[t.B], c = V[t.C];
            double den = ((double)b.Y - c.Y) * (a.X - c.X) + ((double)c.X - b.X) * (a.Y - c.Y);
            if (den == 0) return null;
            double l1 = ((b.Y - (double)c.Y) * (marker.X - c.X) + (c.X - (double)b.X) * (marker.Y - c.Y)) / den, l2 = ((c.Y - (double)a.Y) * (marker.X - c.X) + (a.X - (double)c.X) * (marker.Y - c.Y)) / den;
            return l1 >= -1e-9 && l2 >= -1e-9 && 1 - l1 - l2 >= -1e-9 ? l1 * a.Z + l2 * b.Z + (1 - l1 - l2) * c.Z : null;
        }
        double PlanOff(NavTri t)
        {
            double Seg(Vec3 p, Vec3 q) { double ex = q.X - p.X, ey = q.Y - p.Y, L = ex * ex + ey * ey; double s = L == 0 ? 0 : Math.Clamp(((marker.X - p.X) * ex + (marker.Y - p.Y) * ey) / L, 0, 1); double dx = p.X + ex * s - marker.X, dy = p.Y + ey * s - marker.Y; return Math.Sqrt(dx * dx + dy * dy); }
            return Math.Min(Seg(V[t.A], V[t.B]), Math.Min(Seg(V[t.B], V[t.C]), Seg(V[t.C], V[t.A])));
        }
        string At = FormattableString.Invariant($"({marker.X:0.#}, {marker.Y:0.#}, {marker.Z:0.#})");
        var under = Enumerable.Range(0, T.Count).Where(i => PlaneAt(T[i]) is not null).ToList();
        if (under.Count == 0)
        {
            int near = Enumerable.Range(0, T.Count).MinBy(i => PlanOff(T[i]));
            double off = T.Count == 0 ? double.NaN : PlanOff(T[near]);
            return DoorAddResult.Refused($"No triangle of {g.FormKey} lies under {door}'s arrival marker at {At}; the nearest, triangle {near}, is {off:0.#} u off in plan. Extend the navmesh there first.", off, near);
        }
        int tri = under.MinBy(i => Math.Abs(PlaneAt(T[i])!.Value - marker.Z));
        double dz = marker.Z - PlaneAt(T[tri])!.Value;
        if (Math.Abs(dz) > DoorHeightLimit)
            return DoorAddResult.Refused($"{door}'s arrival marker at {At} stands {dz:0.#} u off the nearest triangle under it ({tri}), past any base-game door ({DoorHeightLimit:0} u); it is not on this navmesh's floor.", 0, tri) with { Height = dz };
        var t2 = T.ToList(); t2[tri] = t2[tri] with { Flags = t2[tri].Flags | 0x400 };
        var doors = (g.DoorTriangles ?? Array.Empty<NavDoorTriangle>()).ToList(); doors.Add(new NavDoorTriangle(door, (short)tri));
        string id = g.FormKey.ToString();
        var ops = new List<RepairOp>
        {
            new(id, "Data.DoorTriangles", "Add", ComposeNode: new System.Text.Json.Nodes.JsonObject
            {
                ["type"] = "DoorTriangle",
                ["fields"] = new System.Text.Json.Nodes.JsonObject
                {
                    ["TriangleBeforeDoor"] = tri.ToString(CultureInfo.InvariantCulture), ["Unknown"] = NavRepairNavi.LinkedDoorUnknown.ToString(CultureInfo.InvariantCulture), ["Door"] = door.ToString(),
                },
            }),
        };
        if ((T[tri].Flags & 0x400) == 0) ops.Add(new RepairOp(id, $"Data.Triangles[{tri}].Flags", "Add", Value: "Door"));
        return new DoorAddResult("planned", Array.Empty<string>(), g with { Triangles = t2, DoorTriangles = doors }, tri, 0, dz, ops);
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

/// <summary>A use-version edit planned on a load order: the refusal (one sentence) before UseVersion runs, or UseVersion's
/// own why; the ops (the navmesh info map's for any neighbour the version gains included); the expected state of every
/// navmesh it changes (the read-back's input: the carried navmesh, grid rebuilt, and each neighbour whose links into it
/// were re-pointed, grid kept); and the plan row's "useVersion" object (null when refused before UseVersion ran).</summary>
/// <summary>How a use_version edit names its navmeshes (R10): one navmesh, never grown; a stated set, carried exactly; or one
/// navmesh grown by need ("with_neighbours", and every use_version row of a repair plan).</summary>
public enum UseVersionSetMode { Single, Stated, Grown }

public sealed record UseVersionPlan(string? Refuse, IReadOnlyList<string> Why, List<RepairOp> Ops,
                                    Dictionary<FormKey, (NavmeshGeometry Geom, int[] OldIndex, string WinnerPath, bool Rebuilt)> Expect,
                                    System.Text.Json.Nodes.JsonObject? Row)
{
    public bool Planned => Refuse is null && Why.Count == 0;
    /// <summary>The flag and island data the carried navmesh's entry ends with, when either it or the version's is an island
    /// (R7; what the read-back holds the entry to); null otherwise.</summary>
    public NavIslandEntry? IslandAfter { get; init; }
    /// <summary>R10: each member's island data as the read-back holds it (members on or becoming an island only).</summary>
    public Dictionary<FormKey, NavIslandEntry> IslandsAfter { get; init; } = new();
}

/// <summary>THE LOAD-ORDER GLUE AROUND <see cref="NavEdit.UseVersion"/>: its one owner, for navedit's plan and write and for
/// navownprobe's repair plan (houseCARL 2026-11-22: "only by calling navedit's own UseVersion code, never a second
/// implementation"). Unlike NavEdit it reads plugins: the version, this order's winners of every navmesh the version links
/// into and of every navmesh linking into the winner, each door's reference, and the navmesh info map.</summary>
public static class NavUseVersionPlan
{
    /// <summary>Plan carrying <paramref name="from"/>'s version of <paramref name="key"/> over its winner
    /// <paramref name="geom"/> (from <paramref name="winnerPlugin"/>). <paramref name="inbound"/> gives every navmesh's
    /// winning link table on the order (read once per run, by the caller's cache).</summary>
    public static UseVersionPlan Plan(LoadOrderResolver.IndexView view, NaviView navi, IReadOnlyList<string> order, FormKey key,
                                      NavmeshGeometry geom, string winnerPlugin, string from,
                                      Func<Dictionary<FormKey, (string Path, List<NavEdgeLink> Links)>> inbound)
        => PlanSet(view, navi, order, new[] { key }, from, UseVersionSetMode.Single, inbound, (geom, winnerPlugin));

    /// <summary>THE SET PLANNER (R10, Gate A section 1), the one owner of every use_version: one navmesh (<paramref name="mode"/>
    /// Single, never grown), a stated set (exactly <paramref name="keys"/>), or one navmesh grown by need (Grown: while a member's
    /// border refusal names a neighbour <paramref name="from"/> has its own version of and does not already win, that neighbour
    /// joins; when nothing more can join, any refusal left refuses the whole set). Each member is carried whole from
    /// <paramref name="from"/> (NavEdit.UseVersionSet); the navmesh info map is changed once for the set
    /// (NavRepairNavi.ForVersions); the row masters every plugin whose version of any member the patch supersedes.
    /// <paramref name="seed"/> is the first navmesh's winning geometry and plugin when the caller has them.</summary>
    public static UseVersionPlan PlanSet(LoadOrderResolver.IndexView view, NaviView navi, IReadOnlyList<string> order, IReadOnlyList<FormKey> keys,
                                         string from, UseVersionSetMode mode, Func<Dictionary<FormKey, (string Path, List<NavEdgeLink> Links)>> inbound,
                                         (NavmeshGeometry Geom, string WinnerPlugin)? seed = null)
    {
        static UseVersionPlan Refused(string why) => new(why, Array.Empty<string>(), new List<RepairOp>(), new(), null);
        bool stated = mode == UseVersionSetMode.Stated;
        var fromPath = order.FirstOrDefault(p => string.Equals(Path.GetFileName(p), from, StringComparison.OrdinalIgnoreCase));
        if (fromPath is null) return Refused($"{from} is not in this load order, so its version of {keys[0]} (and its masters) cannot be read; enable it, or name a plugin of the order.");
        string PathOf(string plugin) => order.First(p => string.Equals(Path.GetFileName(p), plugin, StringComparison.OrdinalIgnoreCase));
        // the navmesh the game takes now, and its plugin
        (NavmeshGeometry Geom, string Plugin, string Path)? Taken(FormKey m)
            => view.ResolveWinner(m) is { } w && order.FirstOrDefault(p => string.Equals(Path.GetFileName(p), w.WinnerPlugin, StringComparison.OrdinalIgnoreCase)) is { } wp && Winning(wp, m) is { } g ? (g, w.WinnerPlugin, wp) : null;
        // what a border to a navmesh outside the set is matched against: the navmesh the game takes
        NavmeshGeometry? Game(FormKey m) => Taken(m)?.Geom;
        bool PWins(FormKey m) => string.Equals(view.ResolveWinner(m)?.WinnerPlugin, from, StringComparison.OrdinalIgnoreCase);
        var versionOf = new Dictionary<FormKey, NavmeshGeometry?>();
        NavmeshGeometry? VersionOf(FormKey m) => versionOf.TryGetValue(m, out var v) ? v : versionOf[m] = Winning(fromPath, m);
        // a neighbour joins a grown set when the plugin has its own version of it that the game does not already take
        bool CanGrow(FormKey m) => !PWins(m) && VersionOf(m) is not null;

        var members = new List<FormKey>();
        var winners = new Dictionary<FormKey, (NavmeshGeometry Geom, string Plugin, string Path)>();
        foreach (var k in keys)
        {
            if (members.Contains(k)) return Refused($"{k} is named twice in the set; name each navmesh once.");
            var w = members.Count == 0 && seed is { } s ? ((NavmeshGeometry Geom, string Plugin, string Path)?)(s.Geom, s.WinnerPlugin, PathOf(s.WinnerPlugin)) : Taken(k);
            if (w is null) return Refused($"{k} is in no plugin of this load order, or its winner deletes it; check the FormKey and the profile.");
            if (string.Equals(from, w.Value.Plugin, StringComparison.OrdinalIgnoreCase))
                return Refused(stated ? $"{from} already wins {k}, so there is nothing to carry; leave {k} out of the set." : $"{from} already wins {k}, so there is no other version to use.");
            if (VersionOf(k) is null) return Refused($"{from} has no version of {k}, or deletes it, so there is nothing to carry.");
            members.Add(k); winners[k] = w.Value;
        }
        string Ending(FormKey k, FormKey m) =>
            members.Contains(m) ? $"{from}'s own versions of {k} and {m} do not agree there, so keep the winner's version of {k}."
            : PWins(m) ? $"{from}'s own version of {m}, the one the game takes, does not fit it either, so keep the winner's version of {k}."
            : VersionOf(m) is null ? $"{from} has no version of {m} to carry with it, so keep the winner's version of {k}."
            : stated ? $"add {m} to the set, or keep the winner's version of {k}."
            : $"keep the winner's version, or carry {from}'s version of {m} with it (\"with_neighbours\": true).";

        // planned, and grown, until nothing more can join
        var inboundIndex = inbound();
        UseVersionSetResult r;
        var asked = members.ToList();
        while (true)
        {
            var near = new Dictionary<FormKey, NavmeshGeometry>();
            foreach (var k in members)
            {
                foreach (var m in (VersionOf(k)!.EdgeLinks ?? Array.Empty<NavEdgeLink>()).Select(l => l.Mesh).Distinct())
                    if (!members.Contains(m) && !near.ContainsKey(m) && Game(m) is { } g) near[m] = g;
                foreach (var (fk, (p, links)) in inboundIndex)
                    if (!members.Contains(fk) && !near.ContainsKey(fk) && links.Any(l => l.Mesh == k) && Game(fk) is { } g) near[fk] = g;
            }
            r = NavEdit.UseVersionSet(members.Select(k => new SetMember(winners[k].Geom, VersionOf(k)!)).ToList(), Path.GetFileName(fromPath), near,
                                      NavRuleDefaults.Edge, m => view.ResolveWinner(m)?.WinnerPlugin, PWins, Ending);
            if (mode != UseVersionSetMode.Grown || r.Status == "planned") break;
            var join = r.Need.Where(m => !members.Contains(m) && CanGrow(m)).ToList();
            if (join.Count == 0) break;
            foreach (var m in join) { members.Add(m); winners[m] = Taken(m)!.Value; }
        }
        IReadOnlyList<string> why = r.Why; var ops = r.Ops.ToList();
        string Of(FormKey k) => members.Count > 1 ? $"{k}: " : "";
        // each member's doors must resolve in this order, and its island data follow the version plugin's own entry (R7)
        var islandArgs = new Dictionary<FormKey, NavIslandEntry>(); var islandsAfter = new Dictionary<FormKey, NavIslandEntry>();
        if (r.Status == "planned")
        {
            var memberWhy = new List<string>();
            for (int i = 0; i < members.Count; i++)
            {
                var k = members[i]; var u = r.Members[i];
                var badDoors = u.Doors.Where(d => view.ResolveWinner(d) is null).ToList();
                if (badDoors.Count > 0) { memberWhy.Add($"{Of(k)}{from}'s version stands before door(s) {string.Join(", ", badDoors)}, which no plugin of this order provides."); continue; }
                if (navi.Problem is not null) continue;
                var taken = navi.Effective.TryGetValue(k, out var tk) ? NavIsland.Of(tk.Src.Map.MapInfos[tk.Index]) : new NavIslandEntry(0, null);
                var ve = navi.Stated(k, Path.GetFileName(fromPath));
                var verE = ve is null ? null : NavIsland.Of(ve);
                var (after, islWhy) = NavIsland.ForVersion(k, from, taken, verE);
                islWhy ??= verE?.Island is { } vi ? NavIsland.VersionBoxRefusal(k, from, vi, VersionOf(k)!.Min, VersionOf(k)!.Max) : null;
                if (islWhy is not null) { memberWhy.Add(Of(k) + islWhy); continue; }
                if (!NavIsland.Same(taken, after)) islandArgs[k] = after;
                if (taken.Island is not null || after.Island is not null) islandsAfter[k] = after;
            }
            if (memberWhy.Count > 0) { why = memberWhy; ops = new(); }
        }
        // the navmesh info map follows every member's version, in one change for the set (NavRepairNavi.ForVersions)
        System.Text.Json.Nodes.JsonObject? naviRow = null;
        if (why.Count == 0)
        {
            var versions = members.Select((k, i) => new NavVersionEntry(k, (r.Members[i].After!.DoorTriangles ?? Array.Empty<NavDoorTriangle>()).Select(d => d.Door).ToList(),
                                                                        (r.Members[i].After!.EdgeLinks ?? Array.Empty<NavEdgeLink>()).Select(l => l.Mesh).Where(m => m != k).Distinct().ToList(),
                                                                        r.Members[i].Gained, islandArgs.GetValueOrDefault(k))).ToList();
            var (np, refusedNavi) = NavRepairNavi.ForVersions(versions, navi);
            if (refusedNavi is not null) { why = new[] { $"The navmesh info map cannot follow the version ({refusedNavi}); keep the winner's version, or give the map an entry the patch can change first." }; ops = new(); }
            else if (np.Entries.Count > 0) { ops.AddRange(np.Ops); naviRow = NavRepairNavi.NaviJson(np); }
        }
        var expect = new Dictionary<FormKey, (NavmeshGeometry, int[], string, bool)>();
        if (why.Count == 0)
        {
            for (int i = 0; i < members.Count; i++) expect[members[i]] = (r.Members[i].After!, Enumerable.Range(0, r.Members[i].After!.Triangles.Count).ToArray(), fromPath, true);
            foreach (var (fk, og) in r.Others) expect[fk] = (og, Enumerable.Range(0, og.Triangles.Count).ToArray(), inboundIndex[fk].Path, false);   // a re-pointed neighbour keeps its own winner's grid
        }
        // the plugins whose versions of each member the patch supersedes (every one after the source, in load order): the patch
        // masters each, so nothing can sort it above them and let one win a member again (houseCARL, 2026-11-22)
        IEnumerable<string> MastersOf(FormKey k) => (view.TouchingPlugins(k) ?? Array.Empty<string>())
            .SkipWhile(n => !string.Equals(n, Path.GetFileName(fromPath), StringComparison.OrdinalIgnoreCase)).Skip(1);
        var masters = members.SelectMany(m => MastersOf(m)).Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(n => order.ToList().FindIndex(p => string.Equals(Path.GetFileName(p), n, StringComparison.OrdinalIgnoreCase))).ToList();

        using var session = view.OpenSession();
        static System.Text.Json.Nodes.JsonArray Arr<T>(IEnumerable<T> xs) => new(xs.Select(x => (System.Text.Json.Nodes.JsonNode?)System.Text.Json.Nodes.JsonValue.Create(x)).ToArray());
        System.Text.Json.Nodes.JsonObject Doors(UseVersionResult u) => new()
        {
            ["added"] = new System.Text.Json.Nodes.JsonArray(u.DoorsAdded.Select(d => (System.Text.Json.Nodes.JsonNode)DoorRow(view, session, d)).ToArray()),
            ["dropped"] = new System.Text.Json.Nodes.JsonArray(u.DoorsDropped.Select(d => (System.Text.Json.Nodes.JsonNode)DoorRow(view, session, d)).ToArray()),
        };
        var first = r.Members.Count > 0 ? r.Members[0] : UseVersionResult.Refused();
        var row = new System.Text.Json.Nodes.JsonObject
        {
            ["from"] = from, ["fromPath"] = fromPath,
            ["vertices"] = new System.Text.Json.Nodes.JsonObject { ["before"] = members.Sum(k => winners[k].Geom.Vertices.Count), ["after"] = members.Sum(k => VersionOf(k)!.Vertices.Count) },
            ["triangles"] = new System.Text.Json.Nodes.JsonObject { ["before"] = members.Sum(k => winners[k].Geom.Triangles.Count), ["after"] = members.Sum(k => VersionOf(k)!.Triangles.Count) },
            ["linksMatched"] = r.Members.Sum(u => u.Matched), ["linksChanged"] = r.Members.Sum(u => u.Changed), ["ledgeLinksCarriedUnverified"] = r.Members.Sum(u => u.LedgesCarried),
            ["linksCut"] = 0, ["neighbourLinksRepointed"] = r.Repointed,
            ["navmeshesRepointed"] = Arr(r.Others.Keys.Select(k => k.ToString())), ["doorsChecked"] = r.Members.Sum(u => u.Doors.Count),
            // ff83e12: each door the version adds or drops, by name, with who places it and where it leads
            ["doors"] = members.Count == 1 ? Doors(first) : null,
            ["gainedNeighbours"] = Arr(r.Members.SelectMany(u => u.Gained).Distinct().Select(k => k.ToString())),
            ["lostNeighbours"] = Arr(r.Members.SelectMany(u => u.Lost).Distinct().Select(k => k.ToString())),
            ["navi"] = naviRow?.DeepClone(), ["grid"] = "rebuilt",
            ["masters"] = Arr(masters),
            // R10: how the set was named, what growth added, and each member as it is carried
            ["set"] = new System.Text.Json.Nodes.JsonObject { ["mode"] = mode.ToString().ToLowerInvariant(), ["asked"] = Arr(asked.Select(k => k.ToString())), ["grown"] = Arr(members.Skip(asked.Count).Select(k => k.ToString())) },
            ["members"] = new System.Text.Json.Nodes.JsonArray(members.Select((k, i) =>
            {
                var u = i < r.Members.Count ? r.Members[i] : UseVersionResult.Refused();
                var mo = new System.Text.Json.Nodes.JsonObject
                {
                    ["navmesh"] = k.ToString(), ["winner"] = new System.Text.Json.Nodes.JsonObject { ["plugin"] = winners[k].Plugin, ["path"] = winners[k].Path },
                    ["vertices"] = new System.Text.Json.Nodes.JsonObject { ["before"] = winners[k].Geom.Vertices.Count, ["after"] = VersionOf(k)!.Vertices.Count },
                    ["triangles"] = new System.Text.Json.Nodes.JsonObject { ["before"] = winners[k].Geom.Triangles.Count, ["after"] = VersionOf(k)!.Triangles.Count },
                    ["doors"] = Doors(u), ["gainedNeighbours"] = Arr(u.Gained.Select(x => x.ToString())), ["lostNeighbours"] = Arr(u.Lost.Select(x => x.ToString())),
                    ["masters"] = Arr(MastersOf(k)),
                };
                if (why.Count == 0 && islandsAfter.TryGetValue(k, out var ia)) mo["islandExpect"] = NavIsland.Json(ia);
                return (System.Text.Json.Nodes.JsonNode)mo;
            }).ToArray()),
        };
        if (why.Count == 0 && islandsAfter.TryGetValue(members[0], out var i0)) row["island"] = NavIsland.Json(i0);
        return new UseVersionPlan(null, why, ops, expect, row) { IslandAfter = why.Count == 0 ? islandsAfter.GetValueOrDefault(members[0]) : null, IslandsAfter = why.Count == 0 ? islandsAfter : new() };
    }

    /// <summary>A door as a plan row names it: the door, who places it, its triangle, and where it leads (the partner door
    /// and its cell). One owner for navedit's door edit and the use_version rows (ff83e12).</summary>
    public static System.Text.Json.Nodes.JsonObject DoorRow(LoadOrderResolver.IndexView view, LoadOrderResolver.OverlaySession session, NavDoorTriangle d)
    {
        var w = view.ResolveWinner(d.Door);
        var rec = w is null ? null : view.GetRecord(session, w.Value.WinnerPlugin, d.Door, typeof(Mutagen.Bethesda.Skyrim.IPlacedObjectGetter)) as Mutagen.Bethesda.Skyrim.IPlacedObjectGetter;
        System.Text.Json.Nodes.JsonObject? to = null;
        if (rec is { TeleportDestination: { } dest } && !dest.Door.FormKey.IsNull)
        {
            var cell = view.ParentOf(dest.Door.FormKey);
            var cw = cell is { } c ? view.ResolveWinner(c) : null;
            var crec = cw is null ? null : view.GetRecord(session, cw.Value.WinnerPlugin, cell!.Value, typeof(Mutagen.Bethesda.Skyrim.ICellGetter)) as Mutagen.Bethesda.Skyrim.ICellGetter;
            to = new System.Text.Json.Nodes.JsonObject { ["door"] = dest.Door.FormKey.ToString(), ["cell"] = cell?.ToString(), ["cellEditorId"] = crec?.EditorID };
        }
        return new System.Text.Json.Nodes.JsonObject
        {
            ["door"] = d.Door.ToString(), ["placedBy"] = w?.WinnerPlugin, ["triangleBeforeDoor"] = d.TriangleBeforeDoor,
            ["leadsTo"] = to,
        };
    }

    /// <summary>The kind of one of <see cref="NavEdit.UseVersion"/>'s refusal sentences, read from that sentence's own wording
    /// (written above, in this file; the two change together, and navedit's self-test C2602 holds every kind): a border
    /// link of the version that meets no edge (or several) of its neighbour; a neighbour's link into the navmesh that cannot be
    /// re-pointed; a navmesh info map entry that cannot follow the version (<see cref="NavUseVersionPlan.Plan"/>'s sentence for
    /// NavRepairNavi.ForVersion's refusal); a dropped neighbour that still links in (a one-way border); or another reason (a
    /// broken table, a missing navmesh, a ledge). A different door table (item 2) and a neighbour dropped on both sides (item 3)
    /// are no longer reasons: both are carried. A repair plan names the kinds of a refused row in its one sentence, so a
    /// reader sees every cause, not only the first (the master's condition, 2026-11-22).</summary>
    public static string ReasonKind(string why) =>
        why.Contains("so carrying it would leave that border one-way") ? "neighbour link into it the version does not return"
        : why.Contains("whose edge there does not link back to") ? "border link the neighbour does not return"
        : why.Contains("no edge of its winning version lies along it") || why.Contains(" of its triangles lie along it") || why.Contains("'s version of it lies along it") ? "border link meeting no edge"
        : why.Contains("lies along it") || why.Contains("triangles of ") && why.Contains("'s version lie along it") || why.Contains("cannot be re-pointed") ? "neighbour link into it that cannot be re-pointed"
        : why.Contains("The navmesh info map cannot follow the version") ? "navmesh info map entry that cannot follow"
        : why.Contains("so carrying it would leave a one-way border") ? "neighbour that still links in, dropped"
        : "other reason";

    /// <summary>Why <paramref name="key"/> cannot be edited for the navmesh info map, in one sentence, or null: the map cannot be
    /// read, so whether the navmesh is an island in it is not known (R7: an island itself is no longer a refusal; NavIsland
    /// says what its island data becomes). For every navedit edit and navownprobe's use_version rows.</summary>
    public static string? NaviRefusal(NaviView navi, FormKey key)
        => navi.Problem is { } np ? $"The navmesh info map cannot be read ({np}), so whether {key} is an island is not known; check the plugins that carry it open in xEdit." : null;

    /// <summary>Every navmesh's winning link table on the order: FormKey to (winning plugin path, its links). Read once per
    /// plan, so a triangle edit finds every navmesh whose links name the one it renumbers.</summary>
    public static Dictionary<FormKey, (string Path, List<NavEdgeLink> Links)> InboundIndex(IReadOnlyList<string> order)
    {
        var win = new Dictionary<FormKey, (string, List<NavEdgeLink>)>();
        foreach (var p in order)
        {
            using var mod = Mutagen.Bethesda.Skyrim.SkyrimMod.CreateFromBinaryOverlay(p, Mutagen.Bethesda.Skyrim.SkyrimRelease.SkyrimSE);
            foreach (var nm in mod.EnumerateMajorRecords<Mutagen.Bethesda.Skyrim.INavigationMeshGetter>())
            {
                if (nm.IsDeleted || nm.Data is not { } d) { win.Remove(nm.FormKey); continue; }
                win[nm.FormKey] = (p, d.EdgeLinks.Select(l => new NavEdgeLink(l.Mesh.FormKey, l.TriangleIndex, l.Unknown)).ToList());
            }
        }
        return win;
    }

    /// <summary>The navmesh as one plugin has it, through the one NAVM reader (PlacementExtract); null when absent or deleted.</summary>
    public static NavmeshGeometry? Winning(string pluginPath, FormKey key)
    {
        using var mod = Mutagen.Bethesda.Skyrim.SkyrimMod.CreateFromBinaryOverlay(pluginPath, Mutagen.Bethesda.Skyrim.SkyrimRelease.SkyrimSE);
        foreach (var cp in PlacementExtract.FromMod(mod))
            foreach (var n in cp.Navmeshes)
                if (n.FormKey == key) return n.Deleted ? null : n;
        return null;
    }
}
