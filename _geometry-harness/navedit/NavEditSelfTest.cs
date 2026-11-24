using HarnessShared;
using HousecarlCore;
using SkyWright.Engine;
using Mutagen.Bethesda.Plugins;

// `navedit --selftest`: known answers for NavEdit.MoveVertices on small in-memory navmeshes (tests/test_navedit.py runs it
// and reads each line). Each case prints "ok NAME" or "FAIL NAME: why".
static class NavEditSelfTest
{
    static readonly FormKey K = new(new ModKey("SelfTest", ModType.Master), 0xE01);

    // A 200 x 200 square of 3 x 3 vertices (index = 3 * row + column, 100 u apart, z 0), each 100 u square cut into two
    // triangles; vertex 4 (100, 100) is the one interior vertex. Extra vertices and triangles are appended by each case.
    static (List<Vec3> V, List<NavTri> T) Square()
    {
        var v = new List<Vec3>();
        for (int r = 0; r < 3; r++) for (int c = 0; c < 3; c++) v.Add(new Vec3(100 * c, 100 * r, 0));
        var t = new List<NavTri>();
        for (int r = 0; r < 2; r++)
            for (int c = 0; c < 2; c++)
            {
                short a = (short)(3 * r + c), b = (short)(a + 1), d = (short)(a + 3), e = (short)(a + 4);
                t.Add(new NavTri(a, b, e, 0)); t.Add(new NavTri(a, e, d, 0));
            }
        return (v, t);
    }

    static NavmeshGeometry Geom(List<Vec3> v, List<NavTri> t, List<NavEdgeLink>? links = null)
    {
        var (min, max) = NavGrid.Bounds(v, t.Select(x => ((int)x.A, (int)x.B, (int)x.C)).ToList());
        return new NavmeshGeometry(K, v, t, min, max, links ?? new List<NavEdgeLink>());
    }

    public static int Run()
    {
        int fail = 0;
        void Case(string name, Func<string?> body)
        {
            string? why;
            try { why = body(); } catch (Exception ex) { why = $"threw {ex.GetType().Name}: {ex.Message}"; }
            Console.WriteLine(why is null ? $"ok {name}" : $"FAIL {name}: {why}");
            if (why is not null) fail++;
        }
        string? Planned(NavEditResult r) => r.Status == "planned" ? null : "refused: " + string.Join(" ", r.Why);
        string? RefusedFor2(TriEditResult r, string word) => r.Status == "refused" && r.Why.Any(w => w.Contains(word)) ? null : $"want a refusal naming '{word}', got {r.Status}: {string.Join(" ", r.Why)}";
        string? RefusedFor(NavEditResult r, string word) => r.Status == "refused" && r.Why.Any(w => w.Contains(word)) ? null : $"want a refusal naming '{word}', got {r.Status}: {string.Join(" ", r.Why)}";

        // C2513: the interior vertex moves 20 u east and 10 u north; the box keeps, the grid is NavGrid's for the moved mesh,
        // and the ops set exactly that vertex, the box and the grid.
        Case("2513 interior-move-planned", () =>
        {
            var (v, t) = Square();
            var r = NavEdit.MoveVertices(Geom(v, t), new[] { new VertexMove(4, new Vec3(120, 110, 0)) });
            if (Planned(r) is { } why) return why;
            if (!r.Moved.SequenceEqual(new[] { 4 })) return $"moved [{string.Join(",", r.Moved)}], want [4]";
            var after = v.ToList(); after[4] = new Vec3(120, 110, 0);
            var g = NavGrid.Rebuild(after, t.Select(x => ((int)x.A, (int)x.B, (int)x.C)).ToList());
            if (!r.Grid!.CellBytes.SequenceEqual(g.CellBytes) || r.Grid.Min != new Vec3(0, 0, 0) || r.Grid.Max != new Vec3(200, 200, 0)) return "the grid or box is not NavGrid's for the moved mesh";
            var ops = r.Ops.Select(o => $"{o.FieldPath} {o.Op} {o.Key} {o.Value}").ToList();
            string[] want = { "Data.Vertices SetAtIndex 4 120,110,0", "Data.Min Set  0,0,0", "Data.Max Set  200,200,0", $"Data.NavmeshGridDivisor Set  {g.Divisor}",
                              $"Data.MaxDistanceX Set  {g.SectionX:R}", $"Data.MaxDistanceY Set  {g.SectionY:R}", $"Data.NavmeshGrid Set  {Convert.ToHexString(g.CellBytes)}" };
            return ops.SequenceEqual(want) ? null : "ops " + string.Join(" | ", ops);
        });

        // C2514: the interior vertex pushed past the square's east edge folds the triangles on its west side.
        Case("2514 fold-refused", () =>
        {
            var (v, t) = Square();
            return RefusedFor(NavEdit.MoveVertices(Geom(v, t), new[] { new VertexMove(4, new Vec3(250, 100, 0)) }), "fold");
        });

        // C2515: an edge that links into another navmesh holds its vertices: a sideways move of vertex 4 is refused, a
        // height-only move is planned.
        Case("2515 linked-edge", () =>
        {
            var (v, t) = Square();
            t[0] = t[0] with { Flags = 1 << 1, EdgeLink12 = 0 };   // triangle 0 (0, 1, 4): edge 1-4 links into another navmesh
            var links = new List<NavEdgeLink> { new(new FormKey(new ModKey("Other", ModType.Master), 0xE02), 0) };
            var side = NavEdit.MoveVertices(Geom(v, t, links), new[] { new VertexMove(4, new Vec3(110, 100, 0)) });
            var up = NavEdit.MoveVertices(Geom(v, t, links), new[] { new VertexMove(4, new Vec3(100, 100, 30)) });
            return RefusedFor(side, "links into another navmesh") ?? Planned(up);
        });

        // C2516: a point repeated under two indices moves as one (vertex 9 sits where vertex 4 does and triangle 3 uses it).
        Case("2516 coincident-move-together", () =>
        {
            var (v, t) = Square();
            v.Add(new Vec3(100, 100, 0));
            t[3] = new NavTri(t[3].A, 9, t[3].C, 0);
            var r = NavEdit.MoveVertices(Geom(v, t), new[] { new VertexMove(4, new Vec3(110, 105, 0)) });
            if (Planned(r) is { } why) return why;
            return r.Moved.SequenceEqual(new[] { 4, 9 }) && r.After!.Vertices[9] == new Vec3(110, 105, 0) ? null : $"moved [{string.Join(",", r.Moved)}], want [4,9] both at (110, 105, 0)";
        });

        // C2517: a separate triangle east of the square (x 230-260). Moving the square's east-middle vertex 5 to x 245 makes
        // its triangles overlap it on the same level: refused. The same triangle 200 u up (a bridge) is not an overlap.
        Case("2517 overlap-same-level", () =>
        {
            NavEditResult With(float z)
            {
                var (v, t) = Square();
                v.Add(new Vec3(230, 80, z)); v.Add(new Vec3(260, 100, z)); v.Add(new Vec3(230, 120, z));
                t.Add(new NavTri(9, 10, 11, 0));
                return NavEdit.MoveVertices(Geom(v, t), new[] { new VertexMove(5, new Vec3(245, 100, 0)) });
            }
            return RefusedFor(With(0), "overlap") ?? Planned(With(200));
        });

        // C2518: a vertex on a cell border (x = 4096) holds its place: sideways refused, height-only planned.
        Case("2518 cell-border", () =>
        {
            var v = new List<Vec3> { new(4000, 0, 0), new(4096, 50, 0), new(4000, 100, 0) };
            var t = new List<NavTri> { new(0, 1, 2, 0) };
            var side = NavEdit.MoveVertices(Geom(v, t), new[] { new VertexMove(1, new Vec3(4090, 50, 0)) });
            var up = NavEdit.MoveVertices(Geom(v, t), new[] { new VertexMove(1, new Vec3(4096, 50, 20)) });
            return RefusedFor(side, "cell border") ?? Planned(up);
        });

        // C2519: a vertex the navmesh does not have, and no move at all, are refused in one sentence each.
        Case("2519 bad-input", () =>
        {
            var (v, t) = Square();
            return RefusedFor(NavEdit.MoveVertices(Geom(v, t), new[] { new VertexMove(99, new Vec3(0, 0, 0)) }), "is not in")
                ?? RefusedFor(NavEdit.MoveVertices(Geom(v, t), Array.Empty<VertexMove>()), "No vertex move");
        });

        // ---- triangles: the square with its neighbour fields filled, as a real navmesh has them ----
        // Triangle 2(2r + c) is (a, b, e) and 2(2r + c) + 1 is (a, e, d) for the square at row r, column c (a its low corner).
        // So triangle 0 is (0, 1, 4), 1 is (0, 4, 3), 2 is (1, 2, 5), 3 is (1, 5, 4), 6 is (4, 5, 8), 7 is (4, 8, 7).
        (List<Vec3> V, List<NavTri> T) Linked()
        {
            var (v, t) = Square();
            for (int i = 0; i < t.Count; i++)
                for (int e = 0; e < 3; e++)
                {
                    var (p, q) = NavBorderMatch.EdgeVerts(t[i], e);
                    int o = Enumerable.Range(0, t.Count).FirstOrDefault(j => j != i && Enumerable.Range(0, 3).Any(f => NavBorderMatch.EdgeVerts(t[j], f) == (q, p)), -1);
                    t[i] = e == 0 ? t[i] with { EdgeLink01 = (short)o } : e == 1 ? t[i] with { EdgeLink12 = (short)o } : t[i] with { EdgeLink20 = (short)o };
                }
            return (v, t);
        }
        var none = new Dictionary<FormKey, NavmeshGeometry>();
        FormKey O = new(new ModKey("SelfTest", ModType.Master), 0xE03), Door = new(new ModKey("SelfTest", ModType.Master), 0xD00);
        string Fields(NavTri t) => $"({t.A},{t.B},{t.C}) {t.EdgeLink01},{t.EdgeLink12},{t.EdgeLink20} f{t.Flags}";

        // C2520: removing triangle 0 moves the last triangle (7: (4,8,7)) into slot 0: 7 triangles; the removed one's
        // neighbours' fields toward it open (-1), the moved one's neighbours point at 0; the ops write the moved triangle
        // whole into slot 0, then remove slot 7; the grid is NavGrid's for what is left.
        Case("2520 remove-renumbers", () =>
        {
            var (v, t) = Linked();
            var r = NavEdit.EditTriangles(Geom(v, t), new[] { 0 }, Array.Empty<Vec3>(), Array.Empty<(int, int, int)>(), none);
            if (r.Status != "planned") return "refused: " + string.Join(" ", r.Why);
            var a = r.After!.Triangles;
            // slot 0 = old 7 (4,8,7): edge 4-8 faces old 6 (4,5,8) -> 6; edge 8-7 the square's; edge 7-4 faces old 4 (3,4,7) -> 4.
            // old 1 (0,4,3): edge 0-4 faced old 0 -> -1; edge 4-3 faces old 4 -> 4. old 3 (1,5,4): 1-5 old 2, 5-4 old 6, 4-1 -> -1.
            // old 6 (4,5,8): edge 8-4 faced old 7 -> 0. old 4 (3,4,7): edge 4-7 faced old 7 -> 0.
            if (a.Count != 7 || Fields(a[0]) != "(4,8,7) 6,-1,4 f0" || Fields(a[1]) != "(0,4,3) -1,4,-1 f0" || Fields(a[3]) != "(1,5,4) 2,6,-1 f0"
                || a[6].EdgeLink20 != 0 || a[4].EdgeLink12 != 0) return $"after: {string.Join(" | ", a.Select(Fields))}";
            if (r.Ops[0] is not { FieldPath: "Data.Triangles", Op: "SetAtIndex", Key: "0" } || r.Ops[1] is not { FieldPath: "Data.Triangles", Op: "Remove", Key: "7" })
                return $"first ops {r.Ops[0]} / {r.Ops[1]}";
            var g = NavGrid.Rebuild(v, a.Select(x => ((int)x.A, (int)x.B, (int)x.C)).ToList());
            return r.Grid!.CellBytes.SequenceEqual(g.CellBytes) ? null : "the grid is not NavGrid's for the remaining triangles";
        });

        // C2521: removing triangle 0 and adding it back appends it as 7 and joins it to both neighbours, both ways.
        Case("2521 add-joins-neighbours", () =>
        {
            var (v, t) = Linked();
            var r = NavEdit.EditTriangles(Geom(v, t), new[] { 0 }, Array.Empty<Vec3>(), new[] { (0, 1, 4) }, none);
            if (r.Status != "planned") return "refused: " + string.Join(" ", r.Why);
            var a = r.After!.Triangles;
            // new 7 (0,1,4): edge 1-4 meets old 3 (slot 3); edge 4-0 meets old 1 (slot 1)
            if (Fields(a[7]) != "(0,1,4) -1,3,1 f0") return $"added {Fields(a[7])}";
            if (a[1].EdgeLink01 != 7 || a[3].EdgeLink20 != 7) return $"neighbours not joined back: {Fields(a[1])} | {Fields(a[3])}";
            var add = r.Ops.FirstOrDefault(o => o.FieldPath == "Data.Triangles" && o.Op == "Add");
            return add?.ComposeNode?["type"]?.ToString() == "NavmeshTriangle" && add.ComposeNode["fields"]!["Vertices"]!.ToString() == "0,1,4" ? null : "no NavmeshTriangle Add op with corners 0,1,4";
        });

        // C2522: an added triangle wound against its neighbour is refused; so is one flat or under 1 u².
        Case("2522 winding-and-area", () =>
        {
            var (v, t) = Linked();
            var wound = NavEdit.EditTriangles(Geom(v, t), new[] { 0 }, Array.Empty<Vec3>(), new[] { (0, 4, 1) }, none);
            var flat = NavEdit.EditTriangles(Geom(v, t), Array.Empty<int>(), new[] { new Vec3(300, 0, 0), new Vec3(400, 0, 0) }, new[] { (2, 9, 10) }, none);
            return RefusedFor2(wound, "wound") ?? RefusedFor2(flat, "flat");
        });

        // C2523: an added triangle on an edge two triangles already share (1-4: triangles 0 and 3) is refused.
        Case("2523 third-triangle-on-edge", () =>
        {
            var (v, t) = Linked();
            var r = NavEdit.EditTriangles(Geom(v, t), Array.Empty<int>(), new[] { new Vec3(150, 20, 0) }, new[] { (4, 1, 9) }, none);
            return RefusedFor2(r, "already share");
        });

        // C2524: a triangle whose edge links into another navmesh, and one before a door, are not removed.
        Case("2524 linked-and-door-stay", () =>
        {
            var (v, t) = Linked();
            t[0] = t[0] with { Flags = 1, EdgeLink01 = 0 };
            var g = Geom(v, t, new List<NavEdgeLink> { new(O, 0) }) with { DoorTriangles = new List<NavDoorTriangle> { new(Door, 5) } };
            return RefusedFor2(NavEdit.EditTriangles(g, new[] { 0 }, Array.Empty<Vec3>(), Array.Empty<(int, int, int)>(), none), "links into another navmesh")
                ?? RefusedFor2(NavEdit.EditTriangles(g, new[] { 5 }, Array.Empty<Vec3>(), Array.Empty<(int, int, int)>(), none), "door");
        });

        // C2525: another navmesh O links to this one's triangles 0 and 7 (its table entries 0 and 1, from its triangle 0's
        // edges 0 and 1). Removing triangle 0 cuts O's entry 0 (edge field -1, flag off) and re-points entry 1 to triangle 0,
        // where the last triangle 7 moved, which is now entry 0; the door before triangle 7 now stands before 0.
        Case("2525 inbound-relinked-and-cut", () =>
        {
            var (v, t) = Linked();
            var g = Geom(v, t) with { DoorTriangles = new List<NavDoorTriangle> { new(Door, 7) } };
            var ov = new List<Vec3> { new(0, -100, 0), new(100, -100, 0), new(0, 0, 0) };
            var o = new NavmeshGeometry(O, ov, new List<NavTri> { new(0, 1, 2, 0b011, 0, 1, -1) }, new Vec3(0, -100, 0), new Vec3(100, 0, 0),
                                        new List<NavEdgeLink> { new(K, 0), new(K, 7) });
            var r = NavEdit.EditTriangles(g, new[] { 0 }, Array.Empty<Vec3>(), Array.Empty<(int, int, int)>(), new Dictionary<FormKey, NavmeshGeometry> { [O] = o });
            if (r.Status != "planned") return "refused: " + string.Join(" ", r.Why);
            if (!r.Others.TryGetValue(O, out var oa)) return "O not changed";
            if (!oa.EdgeLinks!.SequenceEqual(new[] { new NavEdgeLink(K, 0) })) return "O's table " + string.Join(",", oa.EdgeLinks!);
            if (Fields(oa.Triangles[0]) != "(0,1,2) -1,0,-1 f2") return "O's triangle " + Fields(oa.Triangles[0]);
            if (r.After!.DoorTriangles![0].TriangleBeforeDoor != 0) return "the door still stands before " + r.After.DoorTriangles[0].TriangleBeforeDoor;
            var oops = r.Ops.Where(x => x.FormId == O.ToString()).Select(x => $"{x.FieldPath} {x.Op} {x.Key} {x.Value}").ToList();
            string[] want = { "Data.EdgeLinks Remove 0 ", "Data.EdgeLinks[0].TriangleIndex Set  0", "Data.Triangles[0].EdgeLink_0_1 Set  -1", "Data.Triangles[0].Flags Remove  EdgeLink_0_1", "Data.Triangles[0].EdgeLink_1_2 Set  0" };
            return oops.SequenceEqual(want) ? null : "O's ops " + string.Join(" | ", oops);
        });

        // C2526: a cut that leaves two navmeshes with no link between them is refused (their info map entries would go stale).
        Case("2526 last-link-refused", () =>
        {
            var (v, t) = Linked();
            var o = new NavmeshGeometry(O, new List<Vec3> { new(0, -100, 0), new(100, -100, 0), new(0, 0, 0) },
                                        new List<NavTri> { new(0, 1, 2, 0b001, 0, -1, -1) }, new Vec3(0, -100, 0), new Vec3(100, 0, 0), new List<NavEdgeLink> { new(K, 0) });
            return RefusedFor2(NavEdit.EditTriangles(Geom(v, t), new[] { 0 }, Array.Empty<Vec3>(), Array.Empty<(int, int, int)>(), new Dictionary<FormKey, NavmeshGeometry> { [O] = o }), "last link");
        });

        // C2527: an added triangle overlapping an existing one on the same level is refused; 200 u up it is a bridge.
        Case("2527 added-overlap", () =>
        {
            TriEditResult With(float z)
            {
                var (v, t) = Linked();
                return NavEdit.EditTriangles(Geom(v, t), Array.Empty<int>(), new[] { new Vec3(40, 40, z), new Vec3(160, 40, z), new Vec3(100, 160, z) }, new[] { (9, 10, 11) }, none);
            }
            var up = With(200);
            return RefusedFor2(With(0), "overlaps") ?? (up.Status == "planned" ? null : "the bridge was refused: " + string.Join(" ", up.Why));
        });

        // C2529: a navmesh's own table can name itself (vanilla 0F3349 does). Triangle 2's square edge 2-5 links into this
        // navmesh's triangle 7: removing triangle 0 moves 7 to slot 0, so the entry follows it. Linked into triangle 0
        // instead, the entry is cut: removed, triangle 2's field open and its flag off.
        Case("2529 self-link", () =>
        {
            var (v, t) = Linked();
            t[2] = t[2] with { Flags = 1 << 1, EdgeLink12 = 0 };
            var follow = NavEdit.EditTriangles(Geom(v, t, new List<NavEdgeLink> { new(K, 7) }), new[] { 0 }, Array.Empty<Vec3>(), Array.Empty<(int, int, int)>(), none);
            if (follow.Status != "planned") return "follow refused: " + string.Join(" ", follow.Why);
            if (!follow.After!.EdgeLinks!.SequenceEqual(new[] { new NavEdgeLink(K, 0) }) || !follow.Ops.Any(o => o.FieldPath == "Data.EdgeLinks[0].TriangleIndex" && o.Value == "0"))
                return "the entry did not follow triangle 7 to slot 0: " + string.Join(",", follow.After.EdgeLinks!);
            var cut = NavEdit.EditTriangles(Geom(v, t, new List<NavEdgeLink> { new(K, 0) }), new[] { 0 }, Array.Empty<Vec3>(), Array.Empty<(int, int, int)>(), none);
            if (cut.Status != "planned") return "cut refused: " + string.Join(" ", cut.Why);
            var ops = cut.Ops.Select(o => $"{o.FieldPath} {o.Op} {o.Key} {o.Value}").ToList();
            return cut.After!.EdgeLinks!.Count == 0 && Fields(cut.After.Triangles[2]) == "(1,2,5) -1,-1,3 f0"
                && ops.Contains("Data.EdgeLinks Remove 0 ") && ops.Contains("Data.Triangles[2].Flags Remove  EdgeLink_1_2") && ops.Contains("Data.Triangles[2].EdgeLink_1_2 Set  -1")
                ? null : $"cut: {Fields(cut.After.Triangles[2])}; ops {string.Join(" | ", ops)}";
        });

        // ---- NavCut ----
        // What every cut must leave: no triangle over the hole, the area outside it unchanged, no vertex part-way along
        // another triangle's edge (a half-joined edge), and the grid NavGrid's for the result.
        string? Sound(TriEditResult r, double wantArea, (double X, double Y)[] holePoints)
        {
            if (r.Status != "planned") return "refused: " + string.Join(" ", r.Why);
            var v = r.After!.Vertices; var tt = r.After.Triangles;
            double area = tt.Sum(x => Math.Abs(((double)v[x.B].X - v[x.A].X) * ((double)v[x.C].Y - v[x.A].Y) - ((double)v[x.B].Y - v[x.A].Y) * ((double)v[x.C].X - v[x.A].X)) / 2);
            if (Math.Abs(area - wantArea) > 0.5) return $"area {area:F2}, want {wantArea}";
            bool In((double X, double Y) p, NavTri x)
            {
                double d(Vec3 a, Vec3 b) => ((double)b.X - a.X) * (p.Y - a.Y) - ((double)b.Y - a.Y) * (p.X - a.X);
                double s1 = d(v[x.A], v[x.B]), s2 = d(v[x.B], v[x.C]), s3 = d(v[x.C], v[x.A]);
                return (s1 > 1e-6 && s2 > 1e-6 && s3 > 1e-6) || (s1 < -1e-6 && s2 < -1e-6 && s3 < -1e-6);
            }
            foreach (var p in holePoints) if (tt.Any(x => In(p, x))) return $"a triangle still covers ({p.X}, {p.Y}) in the hole";
            var used = tt.SelectMany(x => new[] { (int)x.A, x.B, x.C }).Distinct().ToList();
            // a half-joined edge: an OPEN edge (one triangle on it, by position) with a vertex part-way along it, so the other
            // side was split and this one not; an edge two triangles share is joined, however thin a triangle on it is
            var sides = new Dictionary<(Vec3, Vec3), int>();
            foreach (var x in tt)
                foreach (var (a, b) in new[] { (x.A, x.B), (x.B, x.C), (x.C, x.A) })
                {
                    var key = v[a].X < v[b].X || (v[a].X == v[b].X && v[a].Y <= v[b].Y) ? (v[a], v[b]) : (v[b], v[a]);
                    sides[key] = sides.GetValueOrDefault(key) + 1;
                }
            foreach (var x in tt)
                foreach (var (a, b) in new[] { (x.A, x.B), (x.B, x.C), (x.C, x.A) })
                    foreach (var k in used)
                    {
                        if (sides[v[a].X < v[b].X || (v[a].X == v[b].X && v[a].Y <= v[b].Y) ? (v[a], v[b]) : (v[b], v[a])] > 1) break;
                        if (v[k] == v[a] || v[k] == v[b]) continue;
                        double ex = v[b].X - v[a].X, ey = v[b].Y - v[a].Y, len2 = ex * ex + ey * ey;
                        double s = ((v[k].X - v[a].X) * ex + (v[k].Y - v[a].Y) * ey) / len2;
                        if (s <= 1e-6 || s >= 1 - 1e-6) continue;
                        double dx = v[a].X + ex * s - v[k].X, dy = v[a].Y + ey * s - v[k].Y;
                        if (dx * dx + dy * dy < 1e-6 && Math.Abs(v[a].Z + (v[b].Z - v[a].Z) * s - v[k].Z) < 0.01)
                            return $"vertex {k} ({v[k].X}, {v[k].Y}) lies part-way along edge {a}-{b}: a half-joined edge";
                    }
            var g = NavGrid.Rebuild(v, tt.Select(x => ((int)x.A, (int)x.B, (int)x.C)).ToList());
            return r.Grid!.CellBytes.SequenceEqual(g.CellBytes) ? null : "the grid is not NavGrid's for the result";
        }
        (float, float)[] Box(float x0, float y0, float x1, float y1) => new[] { (x0, y0), (x1, y0), (x1, y1), (x0, y1) };

        // C2530: a 40 x 40 hole round the square's centre (vertex 4): every triangle touches it; 200² - 40² remains.
        Case("2530 cut-centre-hole", () =>
        {
            var (v, t) = Linked();
            return Sound(NavEdit.Cut(Geom(v, t), Box(80, 80, 120, 120), -50, 50, none), 40000 - 1600, new[] { (100.0, 100.0), (85.0, 115.0), (119.0, 81.0) });
        });

        // C2531: a 20 x 10 hole inside triangle 0 alone (x 20-40, y 5-15). The cut's lines meet triangle 0's edges shared
        // with triangles 1 and 3, which are not cut: both are split at the same points, so nothing is left half-joined.
        Case("2531 cut-splits-neighbours", () =>
        {
            var (v, t) = Linked();
            var r = NavEdit.Cut(Geom(v, t), Box(20, 5, 40, 15), -50, 50, none);
            if (Sound(r, 40000 - 200, new[] { (30.0, 10.0), (21.0, 6.0) }) is { } why) return why;
            return r.Map[1] == -1 && r.Map[3] == -1 ? null : "triangles 1 and 3 should have been split (replaced)";
        });

        // C2532: an outline that is not convex, has two corners, or no area; an empty band; a cut that misses.
        Case("2532 cut-refusals", () =>
        {
            var (v, t) = Linked();
            var g = Geom(v, t);
            return RefusedFor2(NavEdit.Cut(g, new[] { (0f, 0f), (100f, 0f), (50f, 20f), (100f, 100f), (0f, 100f) }, -50, 50, none), "not convex")
                ?? RefusedFor2(NavEdit.Cut(g, new[] { (0f, 0f), (100f, 0f) }, -50, 50, none), "three corners")
                ?? RefusedFor2(NavEdit.Cut(g, new[] { (0f, 0f), (100f, 0f), (200f, 0f) }, -50, 50, none), "no area")
                ?? RefusedFor2(NavEdit.Cut(g, Box(80, 80, 120, 120), 50, -50, none), "height band is empty")
                ?? RefusedFor2(NavEdit.Cut(g, Box(500, 500, 600, 600), -50, 50, none), "touches no triangle");
        });

        // C2533: the band decides the level: the same hole 300 u above the navmesh cuts nothing.
        Case("2533 cut-height-band", () =>
        {
            var (v, t) = Linked();
            return RefusedFor2(NavEdit.Cut(Geom(v, t), Box(80, 80, 120, 120), 300, 400, none), "touches no triangle");
        });

        // C2534: a cut reaching an edge that links into another navmesh is refused, saying so (the triangle edit underneath
        // would refuse it too, less plainly).
        Case("2534 cut-linked-edge", () =>
        {
            var (v, t) = Linked();
            t[0] = t[0] with { Flags = 1, EdgeLink01 = 0 };                   // triangle 0's square edge 0-1 (y = 0) links out
            return RefusedFor2(NavEdit.Cut(Geom(v, t, new List<NavEdgeLink> { new(O, 0) }), Box(40, -10, 60, 10), -50, 50, none), "cutting across a navmesh border");
        });

        // ---- the cut of a placed obstacle (NavCutFootprint.OnNavmesh), each triangle measured on its own plane ----
        TriangleSoup BoxSoup(float x0, float y0, float z0, float x1, float y1, float z1)
        {
            var c = new System.Numerics.Vector3[8];
            for (int i = 0; i < 8; i++) c[i] = new((i & 1) == 0 ? x0 : x1, (i & 2) == 0 ? y0 : y1, (i & 4) == 0 ? z0 : z1);
            int[] f = { 0, 2, 1, 1, 2, 3, 4, 5, 6, 5, 7, 6, 0, 1, 4, 1, 5, 4, 2, 6, 3, 3, 6, 7, 0, 4, 2, 2, 4, 6, 1, 3, 5, 3, 7, 5 };
            return new TriangleSoup(c, f);
        }
        TriangleSoup Join(params TriangleSoup[] s)
        {
            var vs = new List<System.Numerics.Vector3>(); var ix = new List<int>();
            foreach (var x in s) { int o = vs.Count; vs.AddRange(x.Vertices); ix.AddRange(x.Indices.Select(i => i + o)); }
            return new TriangleSoup(vs.ToArray(), ix.ToArray());
        }
        // an upright prism over a plan outline: its walls, and a lid of rectangles at its top; the walls share corners, so
        // it is one connected mesh, which a hull would cut whole
        TriangleSoup Prism(IReadOnlyList<(float X, float Y)> ring, float z0, float z1, params (float X0, float Y0, float X1, float Y1)[] lid)
        {
            var vs = new List<System.Numerics.Vector3>(); var ix = new List<int>();
            foreach (var (x, y) in ring) { vs.Add(new(x, y, z0)); vs.Add(new(x, y, z1)); }
            for (int i = 0; i < ring.Count; i++)
            {
                int a = 2 * i, b = 2 * ((i + 1) % ring.Count);
                ix.AddRange(new[] { a, b, a + 1, b, b + 1, a + 1 });
            }
            foreach (var (x0, y0, x1, y1) in lid)
            {
                int o = vs.Count;
                vs.Add(new(x0, y0, z1)); vs.Add(new(x1, y0, z1)); vs.Add(new(x1, y1, z1)); vs.Add(new(x0, y1, z1));
                ix.AddRange(new[] { o, o + 1, o + 2, o, o + 2, o + 3 });
            }
            return new TriangleSoup(vs.ToArray(), ix.ToArray());
        }
        TriEditResult CutBy(NavmeshGeometry g, NavCutPlan fp) =>
            NavEdit.CutMany(g, fp.Parts.Select(f => (f.Outline, float.MinValue, float.MaxValue)).ToList(), none, null, fp.Parts.Select(f => (IReadOnlyCollection<int>?)f.Applies).ToList());
        bool Covers(NavmeshGeometry g, double px, double py, float? z = null)
        {
            var vv = g.Vertices;
            return g.Triangles.Any(x =>
            {
                if (z is { } zz && Math.Abs(vv[x.A].Z - zz) > 1) return false;
                double d(Vec3 p, Vec3 q) => ((double)q.X - p.X) * (py - p.Y) - ((double)q.Y - p.Y) * (px - p.X);
                double s1 = d(vv[x.A], vv[x.B]), s2 = d(vv[x.B], vv[x.C]), s3 = d(vv[x.C], vv[x.A]);
                return (s1 > 0 && s2 > 0 && s3 > 0) || (s1 < 0 && s2 < 0 && s3 < 0);
            });
        }
        double AreaAt(NavmeshGeometry g, float z) => g.Triangles.Where(x => Math.Abs(g.Vertices[x.A].Z - z) <= 1 && Math.Abs(g.Vertices[x.B].Z - z) <= 1 && Math.Abs(g.Vertices[x.C].Z - z) <= 1).Sum(x =>
        {
            var v = g.Vertices;
            return Math.Abs(((double)v[x.B].X - v[x.A].X) * ((double)v[x.C].Y - v[x.A].Y) - ((double)v[x.B].Y - v[x.A].Y) * ((double)v[x.C].X - v[x.A].X)) / 2;
        });
        string? CutPlanned(NavCutPlan fp) => fp.Status == "planned" ? null : "refused: " + fp.Why;

        // C2535: a 40 x 20 box standing 200 u tall on the floor: its walls cross the band, its inside is under its lid, so
        // exactly its 800 u² go.
        Case("2535 footprint-box", () =>
        {
            var (v, t) = Linked(); var g = Geom(v, t);
            var fp = NavCutFootprint.OnNavmesh(BoxSoup(80, 90, -10, 120, 110, 190), g);
            return CutPlanned(fp) ?? Sound(CutBy(g, fp), 40000 - 800, new[] { (100.0, 100.0), (81.0, 91.0), (119.0, 109.0) });
        });

        // C2536: a margin of 8 cuts the box grown to 56 x 36, 2,016 u², across the triangles it reaches.
        Case("2536 footprint-margin", () =>
        {
            var (v, t) = Linked(); var g = Geom(v, t);
            var fp = NavCutFootprint.OnNavmesh(BoxSoup(80, 90, -10, 120, 110, 190), g, margin: 8);
            return CutPlanned(fp) ?? Sound(CutBy(g, fp), 40000 - 2016, new[] { (73.0, 83.0), (127.0, 117.0), (100.0, 100.0) });
        });

        // C2537: a box wholly above an actor's top (z 150-300) and one below step height (z 0-30) block nobody: refused,
        // naming the band.
        Case("2537 footprint-out-of-band", () =>
        {
            var (v, t) = Linked(); var g = Geom(v, t);
            var high = NavCutFootprint.OnNavmesh(BoxSoup(80, 90, 150, 120, 110, 300), g);
            var low = NavCutFootprint.OnNavmesh(BoxSoup(80, 90, 0, 120, 110, 30), g);
            return high.Status == "refused" && high.Why!.Contains("step height") && low.Status == "refused" && low.Why!.Contains("step height") ? null : $"high {high.Status}, low {low.Status}";
        });

        // C2538: each level is measured on itself: with a bridge triangle 200 u over the square, a box standing on the bridge
        // cuts the bridge and not the floor; one standing on the floor under it cuts the floor and not the bridge.
        Case("2538 footprint-levels", () =>
        {
            var (v, t) = Linked();
            v.Add(new Vec3(50, 50, 200)); v.Add(new Vec3(150, 50, 200)); v.Add(new Vec3(100, 150, 200));
            t.Add(new NavTri(9, 10, 11, 0));
            var g = Geom(v, t);
            var up = NavCutFootprint.OnNavmesh(BoxSoup(90, 70, 190, 110, 90, 390), g);
            var down = NavCutFootprint.OnNavmesh(BoxSoup(90, 70, -10, 110, 90, 100), g);
            if ((CutPlanned(up) ?? CutPlanned(down)) is { } why) return why;
            var ru = CutBy(g, up); var rd = CutBy(g, down);
            if (ru.Status != "planned" || rd.Status != "planned") return $"cut: {string.Join(" ", ru.Why)} {string.Join(" ", rd.Why)}";
            string Levels(TriEditResult r) => $"bridge {AreaAt(r.After!, 200):0.#}, floor {AreaAt(r.After!, 0):0.#}";
            if (Levels(ru) != "bridge 4600, floor 40000") return "box on the bridge: " + Levels(ru);
            if (Levels(rd) != "bridge 5000, floor 39600") return "box on the floor: " + Levels(rd);
            return !Covers(ru.After!, 101.3, 81.7, 200) && Covers(ru.After!, 101.3, 81.7, 0) && Covers(rd.After!, 101.3, 81.7, 200) && !Covers(rd.After!, 101.3, 81.7, 0) ? null : "the wrong level was cut";
        });

        // C2539: two separate rocks (20 x 20 boxes at (40-60, 40-60) and (140-160, 140-160)): two holes of 400 u² each, and
        // the ground between them ((97.3, 103.1), off every lattice edge), which one hull would take, stays.
        Case("2539 footprint-pieces", () =>
        {
            var (v, t) = Linked(); var g = Geom(v, t);
            var fp = NavCutFootprint.OnNavmesh(Join(BoxSoup(40, 40, -10, 60, 60, 190), BoxSoup(140, 140, -10, 160, 160, 190)), g);
            if (CutPlanned(fp) is { } w) return w;
            var r = CutBy(g, fp);
            return Sound(r, 40000 - 800, new[] { (50.0, 50.0), (150.0, 150.0) }) ?? (Covers(r.After!, 97.3, 103.1) ? null : "the ground between the rocks was cut");
        });

        // C2540: an L-shaped obstacle (one connected mesh) is cut as the L, 3,900 u², not as its hull: the notch's ground
        // ((121.3, 117.9)) stays.
        Case("2540 footprint-L", () =>
        {
            var (v, t) = Linked(); var g = Geom(v, t);
            var L = new (float, float)[] { (60, 60), (140, 60), (140, 90), (90, 90), (90, 140), (60, 140) };
            var fp = NavCutFootprint.OnNavmesh(Prism(L, -10, 190, (60, 60, 140, 90), (60, 90, 90, 140)), g);
            if (CutPlanned(fp) is { } w) return w;
            var r = CutBy(g, fp);
            return Sound(r, 40000 - 3900, new[] { (70.0, 70.0), (130.0, 75.0), (75.0, 130.0) }) ?? (Covers(r.After!, 121.3, 117.9) ? null : "the ground in the L's notch was cut");
        });

        // C2541: two rocks joined by a thin bridge of collision (a dumbbell: 20 x 20 ends, a 2 u bar, one mesh): the rocks
        // and the bar go, 960 u², and the ground either side of the bar stays.
        Case("2541 footprint-bridge", () =>
        {
            var (v, t) = Linked(); var g = Geom(v, t);
            var D = new (float, float)[] { (40, 90), (60, 90), (60, 99), (140, 99), (140, 90), (160, 90), (160, 110), (140, 110), (140, 101), (60, 101), (60, 110), (40, 110) };
            var fp = NavCutFootprint.OnNavmesh(Prism(D, -10, 190, (40, 90, 60, 110), (60, 99, 140, 101), (140, 90, 160, 110)), g);
            if (CutPlanned(fp) is { } w) return w;
            var r = CutBy(g, fp);
            return Sound(r, 40000 - 960, new[] { (50.0, 100.0), (150.0, 100.0), (100.3, 100.1) })
                ?? (Covers(r.After!, 100.3, 95.1) && Covers(r.After!, 99.7, 104.9) ? null : "the ground beside the bar was cut");
        });

        // C2542: two overlapping boxes as separate bodies: their union, 800 + 1,200 - 200 = 1,800 u², goes, the overlap
        // included (counted body by body, two lids over it do not cancel).
        Case("2542 footprint-overlap", () =>
        {
            var (v, t) = Linked(); var g = Geom(v, t);
            var fp = NavCutFootprint.OnNavmesh(Join(BoxSoup(80, 90, -10, 120, 110, 190), BoxSoup(100, 100, -10, 140, 130, 190)), g);
            if (CutPlanned(fp) is { } w) return w;
            var r = CutBy(g, fp);
            return Sound(r, 40000 - 1800, new[] { (110.0, 105.0), (85.0, 95.0), (135.0, 125.0) }) ?? (Covers(r.After!, 131.2, 95.3) && Covers(r.After!, 85.3, 115.7) ? null : "ground outside both boxes was cut");
        });

        // C2543: an overhang counts: a slab from z 80 to 190 (its underside in the band) over open ground is cut; one from
        // z 120 up (wholly above an actor's top) is refused, and beside a post it is not cut.
        Case("2543 footprint-overhang", () =>
        {
            var (v, t) = Linked(); var g = Geom(v, t);
            var over = NavCutFootprint.OnNavmesh(BoxSoup(80, 90, 80, 120, 110, 190), g);
            var high = NavCutFootprint.OnNavmesh(BoxSoup(80, 90, 120, 120, 110, 190), g);
            if ((CutPlanned(over) ?? Sound(CutBy(g, over), 40000 - 800, new[] { (100.0, 100.0) })) is { } w) return "overhang: " + w;
            if (!(high.Status == "refused" && high.Why!.Contains("step height"))) return $"high: {high.Status}";
            // the high slab beside a post standing on the floor (one obstacle, two bodies): the post's 100 u² go, the ground
            // under the slab stays
            var both = NavCutFootprint.OnNavmesh(Join(BoxSoup(20, 20, -10, 30, 30, 190), BoxSoup(80, 90, 120, 120, 110, 190)), g);
            if (CutPlanned(both) is { } w2) return "post and slab: " + w2;
            var rb = CutBy(g, both);
            return Sound(rb, 40000 - 100, new[] { (25.0, 25.0) }) ?? (Covers(rb.After!, 101.3, 101.7) ? null : "the ground under the high slab was cut");
        });

        // C2544: navmesh lying on a rock stays: a box 60 u tall with navmesh on its top (two triangles at z 60). The floor
        // round it loses the box's 800 u² (its top is in the floor's band); the navmesh on top keeps all its 800 u².
        Case("2544 footprint-on-the-rock", () =>
        {
            var (v, t) = Linked();
            v.Add(new Vec3(80, 90, 60)); v.Add(new Vec3(120, 90, 60)); v.Add(new Vec3(120, 110, 60)); v.Add(new Vec3(80, 110, 60));
            t.Add(new NavTri(9, 10, 11, 0)); t.Add(new NavTri(9, 11, 12, 0));
            var g = Geom(v, t);
            var fp = NavCutFootprint.OnNavmesh(BoxSoup(80, 90, 0, 120, 110, 60), g);
            if (CutPlanned(fp) is { } w) return w;
            var r = CutBy(g, fp);
            if (r.Status != "planned") return "cut refused: " + string.Join(" ", r.Why);
            return $"top {AreaAt(r.After!, 60):0.#}, floor {AreaAt(r.After!, 0):0.#}" is var got && got == "top 800, floor 39200" ? null : got;
        });

        // C2545: on sloping navmesh (z = x / 2) each triangle is measured on its own plane: a box with its top at z 120 over
        // ground at z 40-60 blocks (its top is 60-80 u up), and one with its top at z 110 over ground at z 75-85 is a step
        // (25-35 u up): only the first is cut.
        Case("2545 footprint-slope", () =>
        {
            var (v, t) = Linked();
            for (int i = 0; i < v.Count; i++) v[i] = v[i] with { Z = v[i].X / 2 };
            var g = Geom(v, t);
            var fp = NavCutFootprint.OnNavmesh(Join(BoxSoup(80, 90, -10, 120, 110, 120), BoxSoup(150, 90, -10, 170, 110, 110)), g);
            if (CutPlanned(fp) is { } w) return w;
            var r = CutBy(g, fp);
            return Sound(r, 40000 - 800, new[] { (100.0, 100.0) }) ?? (Covers(r.After!, 160.3, 101.7) ? null : "the step was cut");
        });

        // C2550: a notched roof: an obstacle whose top dips (x 60-140, y 60 up to 100 at its sides and 80 in the middle,
        // 2,400 u²) is cut as two convex parts, not merged across the dip; the ground over the dip ((100.3, 90.7)) stays.
        Case("2550 footprint-notched", () =>
        {
            var (v, t) = Linked(); var g = Geom(v, t);
            var N = new (float, float)[] { (60, 60), (140, 60), (140, 100), (100, 80), (60, 100) };
            var lid = new TriangleSoup(new System.Numerics.Vector3[] { new(60, 60, 190), new(140, 60, 190), new(140, 100, 190), new(100, 80, 190), new(60, 100, 190) },
                                       new[] { 0, 1, 3, 1, 2, 3, 0, 3, 4 });
            var fp = NavCutFootprint.OnNavmesh(Join(Prism(N, -10, 190), lid), g);
            if (CutPlanned(fp) is { } w) return w;
            var r = CutBy(g, fp);
            return Sound(r, 40000 - 2400, new[] { (70.0, 90.0), (130.0, 90.0), (100.0, 70.0) }) ?? (Covers(r.After!, 100.3, 90.7) ? null : "the ground over the dip was cut");
        });

        // C2546: two outlines meeting the square's edge x = 100 (y 0-100) from its two sides, each touching one triangle:
        // A (x 60-100, y 50-70) on the left, B (x 100-140, y 50.02-70) on the right. Their bottom lines cross the edge 0.02 u
        // apart, each in its own triangle; no two vertices of the result lie within a weld of each other (the on-line
        // tolerance settles this layout; the weld's witness is C2550).
        Case("2546 cut-weld", () =>
        {
            var (v, t) = Linked();
            var r = NavEdit.CutMany(Geom(v, t), new[] { ((IReadOnlyList<(float X, float Y)>)Box(60, 50, 100, 70), -50f, 50f), (Box(100, 50.02f, 140, 70), -50f, 50f) }, none);
            if (Sound(r, 40000 - 1599.6, new[] { (80.0, 60.0), (120.0, 60.0) }) is { } w) return w;
            var vv = r.After!.Vertices; var used = r.After.Triangles.SelectMany(x => new[] { (int)x.A, x.B, x.C }).Distinct().ToList();
            foreach (var i in used) foreach (var j in used)
                    if (i < j && !(vv[i] == vv[j]) && Math.Abs(vv[i].X - vv[j].X) < NavEdit.OnLine && Math.Abs(vv[i].Y - vv[j].Y) < NavEdit.OnLine && Math.Abs(vv[i].Z - vv[j].Z) < 1)
                        return $"vertices {i} ({vv[i].X}, {vv[i].Y}) and {j} ({vv[j].X}, {vv[j].Y}) are within a weld of each other";
            return null;
        });

        // C2547: an outline reaching 0.3 u past an edge (x 60-100.3, y 20-40) cuts the triangle beyond it too: (100.15, 30)
        // is left uncovered.
        Case("2547 cut-shallow-overlap", () =>
        {
            var (v, t) = Linked();
            return Sound(NavEdit.Cut(Geom(v, t), Box(60, 20, 100.3f, 40), -50, 50, none), 40000 - 806, new[] { (100.15, 30.0), (80.0, 30.0) });
        });

        // C2548: an outline whose bottom edge runs 0.08 u inside the square's edge (y = 0.08) would leave a strip too narrow
        // to stand on: it goes with the hole, 80 x 40.08 u².
        Case("2548 cut-thin-strip", () =>
        {
            var (v, t) = Linked();
            return Sound(NavEdit.Cut(Geom(v, t), Box(60, 0.08f, 140, 40.08f), -50, 50, none), 40000 - 80 * 40.08, new[] { (100.0, 0.04), (100.0, 20.0) });
        });

        // C2549: an outline edge parallel to a triangle's long edge 0.08 u from it (the cell diagonal y = x; the outline's
        // slanted edge runs y = x - 0.113 over x 70-90) is taken last, so the hairline beside it is cut only along the
        // outline (400 u² go in all) and the ground along the rest of the diagonal ((20, 19.96)) stays.
        Case("2549 cut-parallel-edge-last", () =>
        {
            var (v, t) = Linked();
            var q = new (float, float)[] { (70, 69.887f), (70, 60), (90, 60), (90, 89.887f) };
            var r = NavEdit.Cut(Geom(v, t), q, -50, 50, none);
            return Sound(r, 40000 - 400, new[] { (80.0, 70.0), (80.0, 79.97) }) ?? (Covers(r.After!, 20, 19.96) ? null : "the hairline along the diagonal, far from the outline, was cut");
        });

        // ---- use a version (NavEdit.UseVersion, houseCARL's R2) ----
        // W, the winner: the linked square, its right border (x = 200: triangle 2's edge 1-2, triangle 6's edge 1-2) linking into
        // N, the same square 200 u east, whose left border (triangle 1's and 5's edge 2-0) links back.
        FormKey NK = new(new ModKey("SelfTest", ModType.Master), 0xE02), DoorRef = new(new ModKey("SelfTest", ModType.Master), 0xD01);
        (NavmeshGeometry W, NavmeshGeometry N) Pair(bool linkW = true, bool linkN = true)
        {
            var (v, t) = Linked();
            var wl = new List<NavEdgeLink>();
            if (linkW)
            {
                t[2] = t[2] with { Flags = t[2].Flags | 2, EdgeLink12 = 0 }; t[6] = t[6] with { Flags = t[6].Flags | 2, EdgeLink12 = 1 };
                wl.Add(new NavEdgeLink(NK, 1)); wl.Add(new NavEdgeLink(NK, 5));
            }
            var (nv, nt) = Linked();
            for (int i = 0; i < nv.Count; i++) nv[i] = nv[i] with { X = nv[i].X + 200 };
            var nl = new List<NavEdgeLink>();
            if (linkN)
            {
                nt[1] = nt[1] with { Flags = nt[1].Flags | 4, EdgeLink20 = 0 }; nt[5] = nt[5] with { Flags = nt[5].Flags | 4, EdgeLink20 = 1 };
                nl.Add(new NavEdgeLink(K, 2)); nl.Add(new NavEdgeLink(K, 6));
            }
            return (Geom(v, t, wl), Geom(nv, nt, nl) with { FormKey = NK });
        }
        // the same navmesh with its triangles in reverse order (every in-mesh field following): another numbering, same places
        NavmeshGeometry Reversed(NavmeshGeometry g)
        {
            int n = g.Triangles.Count;
            short M(NavTri x, int e, short f) => (x.Flags & (1 << e)) != 0 || f < 0 ? f : (short)(n - 1 - f);
            var t = g.Triangles.Reverse().Select(x => x with { EdgeLink01 = M(x, 0, x.EdgeLink01), EdgeLink12 = M(x, 1, x.EdgeLink12), EdgeLink20 = M(x, 2, x.EdgeLink20) }).ToList();
            return g with { Triangles = t };
        }
        var tol = NavRuleDefaults.Edge;
        string? UsePlanned(UseVersionResult r) => r.Status == "planned" ? null : "refused: " + string.Join(" ", r.Why);
        string? UseRefused(UseVersionResult r, string word) => r.Status == "refused" && r.Why.Any(w => w.Contains(word)) ? null : $"want a refusal naming '{word}', got {r.Status}: {string.Join(" ", r.Why)}";

        // C2556: a version with the winner's places in another numbering: its two border links match N again unchanged; N's two
        // links into it are re-pointed (2 -> 5, 6 -> 1) in the same patch; the ops copy the version's Data first, then rebuild
        // the grid, then set N's entries.
        Case("2556 use-version-renumbered", () =>
        {
            var (w, n) = Pair();
            var r = NavEdit.UseVersion(w, Reversed(w), "Ver.esp", new Dictionary<FormKey, NavmeshGeometry> { [NK] = n }, tol);
            if (UsePlanned(r) is { } why) return why;
            if (r.Matched != 2 || r.Changed != 0 || r.Repointed != 2) return $"matched {r.Matched}, changed {r.Changed}, re-pointed {r.Repointed}";
            var nl = r.Others[NK].EdgeLinks!;
            if (nl[0].TriangleIndex != 5 || nl[1].TriangleIndex != 1) return $"N's entries name {nl[0].TriangleIndex} and {nl[1].TriangleIndex}, want 5 and 1";
            var first = r.Ops[0];
            if (first.FieldPath != "Data" || first.Op != "CopyFrom" || first.FromPlugin != "Ver.esp") return $"first op {first.FieldPath} {first.Op} {first.FromPlugin}";
            var nOps = r.Ops.Where(o => o.FormId == NK.ToString()).Select(o => $"{o.FieldPath}={o.Value}").ToList();
            if (string.Join(" ", nOps) != "Data.EdgeLinks[0].TriangleIndex=5 Data.EdgeLinks[1].TriangleIndex=1") return "N's ops " + string.Join(" ", nOps);
            var g = NavGrid.Rebuild(r.After!.Vertices, r.After.Triangles.Select(x => ((int)x.A, (int)x.B, (int)x.C)).ToList());
            return r.Grid!.CellBytes.SequenceEqual(g.CellBytes) && r.Ops.Any(o => o.FieldPath == "Data.NavmeshGrid") ? null : "the grid is not NavGrid's for the version";
        });

        // C2557: a version whose border moved 10 u west no longer meets N: refused, listing its edges both ways, never cut.
        Case("2557 use-version-border-moved", () =>
        {
            var (w, n) = Pair();
            var v = w.Vertices.ToList();
            foreach (var i in new[] { 2, 5, 8 }) v[i] = v[i] with { X = 190 };
            var r = NavEdit.UseVersion(w, w with { Vertices = v }, "Ver.esp", new Dictionary<FormKey, NavmeshGeometry> { [NK] = n }, tol);
            return UseRefused(r, "no edge of its winning version lies along it") ?? UseRefused(r, "no edge of Ver.esp's version lies along it")
                ?? (r.Why.Count(x => x.Contains("triangle 2 edge") || x.Contains("triangle 6 edge")) == 2 ? null : "the refusal does not list both border edges: " + string.Join(" | ", r.Why));
        });

        // C2558: N's triangle 1 split at (200, 50) into two along W's edge: two candidates. The link naming one of them keeps
        // it; a link naming neither is refused as ambiguous.
        Case("2558 use-version-two-along", () =>
        {
            var (w, n) = Pair(linkN: false);
            var nv = n.Vertices.ToList(); nv.Add(new Vec3(200, 50, 0));
            var nt = n.Triangles.ToList();
            nt[1] = new NavTri(0, 4, 9, 0) with { EdgeLink01 = nt[1].EdgeLink01, EdgeLink12 = -1, EdgeLink20 = -1 };
            nt.Add(new NavTri(9, 4, 3, 0) with { EdgeLink01 = -1, EdgeLink12 = nt[1].EdgeLink12, EdgeLink20 = -1 });
            var n2 = n with { Vertices = nv, Triangles = nt };
            var near = new Dictionary<FormKey, NavmeshGeometry> { [NK] = n2 };
            var keep = NavEdit.UseVersion(w, w, "Ver.esp", near, tol);
            var bad = NavEdit.UseVersion(w, w with { EdgeLinks = new List<NavEdgeLink> { new(NK, 3), new(NK, 5) } }, "Ver.esp", near, tol);
            return (UsePlanned(keep) is { } k ? "naming one: " + k : null) ?? UseRefused(bad, "2 of its triangles lie along it");
        });

        // C2559: a version that no longer links into N, where N links nothing back, is planned, N named as lost (the navmesh
        // info map's entry drops it: NavRepairNavi.ForVersion, C2605). Where N still links into W, carrying the version would leave
        // a one-way border: refused, naming N. CORRECTED 2026-11-22 (houseCARL's item 3): this expected a refusal in both cases,
        // the behaviour before a dropped neighbour was carried; the review of item 3 kept the two-way case refused.
        Case("2559 use-version-lost-neighbour", () =>
        {
            var (w, n) = Pair(linkN: false);
            var (bare, _) = Pair(linkW: false, linkN: false);
            var r = NavEdit.UseVersion(w, bare, "Ver.esp", new Dictionary<FormKey, NavmeshGeometry> { [NK] = n }, tol);
            var (w2, n2) = Pair();
            var (bare2, _) = Pair(linkW: false);
            var twoWay = NavEdit.UseVersion(w2, bare2, "Ver.esp", new Dictionary<FormKey, NavmeshGeometry> { [NK] = n2 }, tol);
            return UsePlanned(r) ?? (r.Lost.SequenceEqual(new[] { NK }) && r.Gained.Count == 0 ? null : $"lost [{string.Join(",", r.Lost)}], gained [{string.Join(",", r.Gained)}]")
                ?? UseRefused(twoWay, "so carrying it would leave a one-way border")
                ?? (twoWay.Why.Any(x => x.StartsWith($"{NK} still links into")) ? null : "the one-way refusal does not name N: " + string.Join(" | ", twoWay.Why));
        });

        // C2560: a version standing before a door the winner has no door at is planned, the door named as added (the info map's
        // linked doors follow it: NavRepairNavi.ForVersion, C2605); a version that gains N as a neighbour is planned, N returned
        // for the info map. CORRECTED 2026-11-22 (houseCARL's item 2): the door half expected a refusal, the behaviour before
        // a different door table was carried.
        Case("2560 use-version-doors-and-gained", () =>
        {
            var (w, n) = Pair();
            var doors = NavEdit.UseVersion(w, w with { DoorTriangles = new List<NavDoorTriangle> { new(DoorRef, 0) } }, "Ver.esp", new Dictionary<FormKey, NavmeshGeometry> { [NK] = n }, tol);
            var (bare, nn) = Pair(linkW: false, linkN: false);
            var gained = NavEdit.UseVersion(bare, w, "Ver.esp", new Dictionary<FormKey, NavmeshGeometry> { [NK] = nn }, tol);
            return (UsePlanned(doors) is { } dw ? "doors: " + dw : null)
                ?? (doors.DoorsAdded.Select(d => d.Door).SequenceEqual(new[] { DoorRef }) && doors.DoorsDropped.Count == 0 && doors.After!.DoorTriangles!.Single().Door == DoorRef ? null
                    : $"doors added [{string.Join(",", doors.DoorsAdded)}], dropped [{string.Join(",", doors.DoorsDropped)}]")
                ?? UsePlanned(gained) ?? (gained.Gained.SequenceEqual(new[] { NK }) ? null : "gained " + string.Join(",", gained.Gained));
        });

        // C2564: each door is named: one the version adds (D1 before triangle 3), one it lacks (D2 before 5), and a door standing
        // before two triangles in the version and one in the winner (D3: the extra entry, before 7), on the result for the plan
        // row (its doors.added and doors.dropped). CORRECTED 2026-11-22 (houseCARL's item 2): these were a refusal's sentence;
        // the version is now carried, so the lists come back on a planned result and the carried door table is the version's.
        Case("2564 use-version-names-doors", () =>
        {
            var (w, n) = Pair();
            FormKey D(uint id) => new(new ModKey("SelfTest", ModType.Master), id);
            var win = w with { DoorTriangles = new List<NavDoorTriangle> { new(D(0xD02), 5), new(D(0xD03), 6) } };
            var ver = w with { DoorTriangles = new List<NavDoorTriangle> { new(D(0xD01), 3), new(D(0xD03), 6), new(D(0xD03), 7) } };
            var r = NavEdit.UseVersion(win, ver, "Ver.esp", new Dictionary<FormKey, NavmeshGeometry> { [NK] = n }, tol);
            if (UsePlanned(r) is { } why) return why;
            string L(IReadOnlyList<NavDoorTriangle> ds) => string.Join(" ", ds.Select(d => $"{d.Door.ID:X}@{d.TriangleBeforeDoor}"));
            if (L(r.DoorsAdded) != "D01@3 D03@7" || L(r.DoorsDropped) != "D02@5") return $"added {L(r.DoorsAdded)}, dropped {L(r.DoorsDropped)}";
            return L(r.After!.DoorTriangles!) == "D01@3 D03@6 D03@7" ? null : "the carried door table is not the version's: " + L(r.After.DoorTriangles!);
        });

        // C2565: a door goes on the triangle under its arrival marker, nearest in height (a bridge 200 u over the square: a marker
        // at z 190 takes the bridge, one at z 5 the floor); the triangle is flagged Door and the table entry carries the base
        // game's Unknown. Refused: a door already listed, a marker over no triangle (naming the nearest and how far), and one
        // past the height limit.
        Case("2565 add-door", () =>
        {
            var (v, t) = Linked();
            v.Add(new Vec3(50, 50, 200)); v.Add(new Vec3(150, 50, 200)); v.Add(new Vec3(100, 150, 200));
            t.Add(new NavTri(9, 10, 11, 0));
            var g = Geom(v, t);
            FormKey D = new(new ModKey("SelfTest", ModType.Master), 0xD10);
            var up = NavEdit.AddDoor(g, D, new Vec3(101.3f, 81.7f, 190));
            var down = NavEdit.AddDoor(g, D, new Vec3(101.3f, 81.7f, 5));
            if (up.Status != "planned" || down.Status != "planned") return $"{string.Join(" ", up.Why)} {string.Join(" ", down.Why)}";
            if (up.Triangle != 8 || down.Triangle == 8) return $"bridge marker on {up.Triangle}, floor marker on {down.Triangle}";
            if ((up.After!.Triangles[8].Flags & 0x400) == 0 || !up.After.DoorTriangles!.Contains(new NavDoorTriangle(D, 8))) return "the triangle is not flagged Door, or the table lacks the entry";
            var add = up.Ops.First(o => o.FieldPath == "Data.DoorTriangles");
            if ((string?)add.ComposeNode!["fields"]!["Unknown"] != NavRepairNavi.LinkedDoorUnknown.ToString() || (string?)add.ComposeNode["fields"]!["TriangleBeforeDoor"] != "8") return "the table entry's fields " + add.ComposeNode.ToJsonString();
            if (!up.Ops.Any(o => o.FieldPath == "Data.Triangles[8].Flags" && o.Op == "Add" && o.Value == "Door")) return "no Door flag op";
            var listed = NavEdit.AddDoor(up.After, D, new Vec3(101.3f, 81.7f, 190));
            var off = NavEdit.AddDoor(g, D, new Vec3(260, 100, 0));
            var high = NavEdit.AddDoor(Geom(Linked().V, Linked().T), D, new Vec3(101.3f, 81.7f, 900));
            if (listed.Status != "refused" || !listed.Why[0].Contains("already has")) return "a listed door was not refused";
            if (off.Status != "refused" || !off.Why[0].Contains("60 u off in plan")) return "off the floor: " + string.Join(" ", off.Why);
            return high.Status == "refused" && high.Why[0].Contains("past any base-game door") ? null : "past the height limit: " + string.Join(" ", high.Why);
        });

        // C2561: ledge links are not matched by edge geometry: the version's own (Type 2) is carried while it names a triangle N
        // has, and refused when it names one N lacks; N's ledge link into this navmesh is kept where the version's triangle of
        // that index stands on the winner's corners, and refused when the version renumbers it.
        Case("2561 use-version-ledges", () =>
        {
            var (w, n) = Pair(linkN: false);
            var ledge = w with { EdgeLinks = new List<NavEdgeLink> { new(NK, 1, 2), new(NK, 5) } };
            var ok = NavEdit.UseVersion(w with { EdgeLinks = new List<NavEdgeLink> { new(NK, 1, 2), new(NK, 5) } }, ledge, "Ver.esp", new Dictionary<FormKey, NavmeshGeometry> { [NK] = n }, tol);
            var past = NavEdit.UseVersion(w, w with { EdgeLinks = new List<NavEdgeLink> { new(NK, 99, 2), new(NK, 5) } }, "Ver.esp", new Dictionary<FormKey, NavmeshGeometry> { [NK] = n }, tol);
            var (w2, n2) = Pair();
            var nLedge = n2 with { EdgeLinks = new List<NavEdgeLink> { new(K, 2, 1), new(K, 6) } };
            var kept = NavEdit.UseVersion(w2, w2, "Ver.esp", new Dictionary<FormKey, NavmeshGeometry> { [NK] = nLedge }, tol);
            var moved = NavEdit.UseVersion(w2, Reversed(w2), "Ver.esp", new Dictionary<FormKey, NavmeshGeometry> { [NK] = nLedge }, tol);
            if (UsePlanned(ok) is { } a) return "own ledge in range: " + a;
            if (ok.LedgesCarried != 1) return $"ledges carried {ok.LedgesCarried}, want 1";
            return UseRefused(past, "ledge link into") ?? (UsePlanned(kept) is { } k ? "N's ledge kept in place: " + k : null) ?? UseRefused(moved, "cannot be re-pointed by edge position");
        });

        // C2562: a version that already matches this order (the winner itself) plans with nothing re-pointed and no index changed.
        Case("2562 use-version-same", () =>
        {
            var (w, n) = Pair();
            var r = NavEdit.UseVersion(w, w, "Ver.esp", new Dictionary<FormKey, NavmeshGeometry> { [NK] = n }, tol);
            return UsePlanned(r) ?? (r.Matched == 2 && r.Changed == 0 && r.Repointed == 0 && r.Others.Count == 0 ? null : $"matched {r.Matched}, changed {r.Changed}, re-pointed {r.Repointed}, others {r.Others.Count}");
        });

        // C2602: every refusal UseVersion gives is classified by NavUseVersionPlan.ReasonKind as the kind its cause is (a repair
        // plan's one sentence counts a refused row's reasons by kind, so a sentence reworded out of its kind must fail here).
        Case("2602 use-version-reason-kinds", () =>
        {
            var (w, n) = Pair();
            var near = new Dictionary<FormKey, NavmeshGeometry> { [NK] = n };
            var v = w.Vertices.ToList();
            foreach (var i in new[] { 2, 5, 8 }) v[i] = v[i] with { X = 190 };
            var moved = NavEdit.UseVersion(w, w with { Vertices = v }, "Ver.esp", near, tol);
            var (w0, n0) = Pair(linkN: false);
            var nv = n0.Vertices.ToList(); nv.Add(new Vec3(200, 50, 0));
            var nt = n0.Triangles.ToList();
            nt[1] = new NavTri(0, 4, 9, 0) with { EdgeLink01 = nt[1].EdgeLink01, EdgeLink12 = -1, EdgeLink20 = -1 };
            nt.Add(new NavTri(9, 4, 3, 0) with { EdgeLink01 = -1, EdgeLink12 = nt[1].EdgeLink12, EdgeLink20 = -1 });
            var two = NavEdit.UseVersion(w0, w0 with { EdgeLinks = new List<NavEdgeLink> { new(NK, 3), new(NK, 5) } }, "Ver.esp",
                                         new Dictionary<FormKey, NavmeshGeometry> { [NK] = n0 with { Vertices = nv, Triangles = nt } }, tol);
            var (w1, n1) = Pair(linkN: false);
            var past = NavEdit.UseVersion(w1, w1 with { EdgeLinks = new List<NavEdgeLink> { new(NK, 99, 2), new(NK, 5) } }, "Ver.esp", new Dictionary<FormKey, NavmeshGeometry> { [NK] = n1 }, tol);
            var (w2, n2) = Pair();
            var ledgeIn = NavEdit.UseVersion(w2, Reversed(w2), "Ver.esp", new Dictionary<FormKey, NavmeshGeometry> { [NK] = n2 with { EdgeLinks = new List<NavEdgeLink> { new(K, 2, 1), new(K, 6) } } }, tol);
            var (wl, nl2) = Pair();
            var (barel, _) = Pair(linkW: false);
            var oneWay = NavEdit.UseVersion(wl, barel, "Ver.esp", new Dictionary<FormKey, NavmeshGeometry> { [NK] = nl2 }, tol);
            string Kind(string why) => NavUseVersionPlan.ReasonKind(why);
            var want = new (string Case, UseVersionResult R, Func<string, string> Expect)[]
            {
                ("border moved", moved, why => why.Contains("that link cannot be re-pointed") ? "neighbour link into it that cannot be re-pointed" : "border link meeting no edge"),
                ("two along", two, _ => "border link meeting no edge"),
                ("ledge past its neighbour", past, _ => "other reason"),
                ("a dropped neighbour that still links in", oneWay, why => why.Contains("one-way border") ? "neighbour that still links in, dropped" : Kind(why)),
                ("neighbour's ledge renumbered", ledgeIn, _ => "neighbour link into it that cannot be re-pointed"),
            };
            var bad = new List<string>();
            foreach (var (c, r, expect) in want)
            {
                if (r.Status != "refused" || r.Why.Count == 0) { bad.Add($"{c}: not refused"); continue; }
                foreach (var why in r.Why) if (Kind(why) != expect(why)) bad.Add($"{c}: '{why}' read as '{Kind(why)}', want '{expect(why)}'");
            }
            if (!moved.Why.Any(x => Kind(x) == "border link meeting no edge") || !moved.Why.Any(x => Kind(x) == "neighbour link into it that cannot be re-pointed"))
                bad.Add("the border-moved case does not give both an outbound and an inbound reason");
            // CORRECTED 2026-11-22 (houseCARL's items 2 and 3): a different door table and a dropped neighbour are carried, not
            // refused, so their kinds are gone; the
            // info map's own refusal, NavUseVersionPlan.Plan's sentence over NavRepairNavi.ForVersion's reason, has its kind
            var naviWhy = $"The navmesh info map cannot follow the version (no plugin's NAVI states {K}); keep the winner's version, or give the map an entry the patch can change first.";
            if (Kind(naviWhy) != "navmesh info map entry that cannot follow") bad.Add($"'{naviWhy}' read as '{Kind(naviWhy)}'");
            return bad.Count == 0 ? null : string.Join(" | ", bad);
        });

        // C2528: bad input: a triangle index past the mesh, a corner that does not exist, nothing to do.
        Case("2528 bad-triangle-input", () =>
        {
            var (v, t) = Linked();
            return RefusedFor2(NavEdit.EditTriangles(Geom(v, t), new[] { 99 }, Array.Empty<Vec3>(), Array.Empty<(int, int, int)>(), none), "is not in")
                ?? RefusedFor2(NavEdit.EditTriangles(Geom(v, t), Array.Empty<int>(), Array.Empty<Vec3>(), new[] { (0, 1, 42) }, none), "does not exist")
                ?? RefusedFor2(NavEdit.EditTriangles(Geom(v, t), Array.Empty<int>(), Array.Empty<Vec3>(), Array.Empty<(int, int, int)>(), none), "No triangle");
        });

        // C2580-C2586: the station marker check (R5, _shared/NavMarker.cs). A square of 200 u, its triangles joined by their
        // shared edges (Joined), so only the outside of the square is open; a second square (key MK2) east of it at x 200-400.
        var MK2 = new FormKey(new ModKey("SelfTest", ModType.Master), 0xE02);
        List<NavTri> Joined(List<NavTri> t)
        {
            var o = t.ToList();
            for (int i = 0; i < t.Count; i++)
            {
                var c = new[] { (int)t[i].A, t[i].B, t[i].C }; var n = new short[] { -1, -1, -1 };
                for (int j = 0; j < t.Count; j++)
                {
                    if (j == i) continue;
                    var d = new[] { (int)t[j].A, t[j].B, t[j].C };
                    for (int e = 0; e < 3; e++) { int p = c[e], q = c[(e + 1) % 3]; if (d.Contains(p) && d.Contains(q)) n[e] = (short)j; }
                }
                o[i] = o[i] with { EdgeLink01 = n[0], EdgeLink12 = n[1], EdgeLink20 = n[2] };
            }
            return o;
        }
        (List<Vec3> V, List<NavTri> T) Shifted(float dx, float z) { var (v, t) = Square(); return (v.Select(p => new Vec3(p.X + dx, p.Y, z)).ToList(), Joined(t)); }
        NavmeshGeometry GeomK(FormKey k, List<Vec3> v, List<NavTri> t, List<NavEdgeLink>? links = null) => Geom(v, t, links) with { FormKey = k };
        // the east edge (x 200) of the west square links to the west edge (x 200) of the east one, both ways, by edge links
        Dictionary<FormKey, NavmeshGeometry> Linked2(float eastZ)
        {
            var (wv, wt) = Shifted(0, 0); var (ev, et) = Shifted(200, eastZ);
            var wl = new List<NavEdgeLink>(); var el = new List<NavEdgeLink>();
            void Tie(List<Vec3> v, List<NavTri> t, float x, List<NavEdgeLink> links, FormKey to, List<Vec3> ov, List<NavTri> ot, float ox)
            {
                for (int i = 0; i < t.Count; i++)
                {
                    var c = new[] { (int)t[i].A, t[i].B, t[i].C };
                    for (int e = 0; e < 3; e++)
                    {
                        Vec3 p = v[c[e]], q = v[c[(e + 1) % 3]];
                        if (p.X != x || q.X != x) continue;
                        float my = (p.Y + q.Y) / 2;
                        int other = Enumerable.Range(0, ot.Count).First(j => { var d = new[] { ov[ot[j].A], ov[ot[j].B], ov[ot[j].C] }; return d.Count(z => z.X == ox) == 2 && d.Where(z => z.X == ox).Average(z => z.Y) == my; });
                        links.Add(new NavEdgeLink(to, (short)other));
                        short idx = (short)(links.Count - 1);
                        t[i] = e switch { 0 => t[i] with { EdgeLink01 = idx, Flags = t[i].Flags | 1 }, 1 => t[i] with { EdgeLink12 = idx, Flags = t[i].Flags | 2 }, _ => t[i] with { EdgeLink20 = idx, Flags = t[i].Flags | 4 } };
                    }
                }
            }
            Tie(wv, wt, 200, wl, MK2, ev, et, 200); Tie(ev, et, 200, el, K, wv, wt, 200);
            return new Dictionary<FormKey, NavmeshGeometry> { [K] = GeomK(K, wv, wt, wl), [MK2] = GeomK(MK2, ev, et, el) };
        }
        Dictionary<FormKey, NavmeshGeometry> One() { var (v, t) = Shifted(0, 0); return new() { [K] = GeomK(K, v, t) }; }

        Case("2580 marker-centre-inside", () =>
        {
            var r = NavMarker.Check(One(), new Vec3(100, 100, 5));
            return r.Verdict != "inside" ? $"want inside, got {r.Verdict}: {string.Join(" ", r.Why)}"
                : r.Mesh != K || r.StandZ != 0 || r.Above != 5 ? $"standing {r.Mesh} z {r.StandZ} above {r.Above}, want K z 0 above 5"
                : r.OpenEdge is not { } o || Math.Abs(o.Distance - 100) > 1e-6 ? $"open edge {r.OpenEdge?.Distance}, want 100" : null;
        });
        Case("2581 marker-near-edge-refused", () =>
        {
            var r = NavMarker.Check(One(), new Vec3(20, 100, 0));
            return r.Verdict != "refused" || !r.Why.Any(w => w.Contains("20 u from an open edge") && w.Contains("no navmesh within 128 u")) ? $"want refused naming 20 u and a gap, got {r.Verdict}: {string.Join(" ", r.Why)}"
                : r.Beyond is not { Mesh: null } ? "want nothing beyond the edge" : null;
        });
        Case("2582 marker-off-navmesh", () =>
        {
            var r = NavMarker.Check(One(), new Vec3(500, 100, 0));
            return r.Verdict == "refused" && r.Mesh is null && r.Why.Any(w => w.Contains("stands on no walkable") && w.Contains("300 u away")) ? null : $"got {r.Verdict}: {string.Join(" ", r.Why)}";
        });
        Case("2583 marker-ledge-beyond-hole-edge", () =>
        {   // Moon and Star's north start: an open edge with lower ground 87 u below just beyond it, not linked
            var m = One(); var (ev, et) = Shifted(200, -87); m[MK2] = GeomK(MK2, ev, et);
            var r = NavMarker.Check(m, new Vec3(180, 100, 0));
            if (!(r.Verdict == "refused" && r.Beyond is { } b && b.Mesh == MK2 && Math.Abs(b.Step + 87) < 1e-6 && b.Gap == 0 && r.Why.Any(w => w.Contains("87 u lower"))))
                return $"got {r.Verdict}, beyond {r.Beyond}: {string.Join(" ", r.Why)}";
            // the real north start's shape: 70 u of no navmesh, then ground 87 u HIGHER (a slab): the gap's width and the step
            var m2 = One(); var (sv, stt) = Shifted(270, 87); m2[MK2] = GeomK(MK2, sv, stt);
            var r2 = NavMarker.Check(m2, new Vec3(195, 100, 0));
            return r2.Verdict == "refused" && r2.Beyond is { } b2 && b2.Mesh == MK2 && Math.Abs(b2.Step - 87) < 1e-6 && b2.Gap >= 64 && b2.Gap <= 72
                   && r2.Why.Any(w => w.Contains("87 u higher") && w.Contains("5 u from an open edge"))
                ? null : $"slab past a gap: {r2.Verdict}, beyond {r2.Beyond}: {string.Join(" ", r2.Why)}";
        });
        Case("2584 marker-linked-edge-not-open", () =>
        {
            var m = Linked2(0);
            var r = NavMarker.Check(m, new Vec3(190, 100, 0));
            return r.Verdict != "inside" ? $"want inside, got {string.Join(" ", r.Why)}"
                : r.OpenEdge is not { } o || Math.Abs(o.Distance - 100) > 1e-6 ? $"open edge {r.OpenEdge?.Distance}, want 100 (the linked x 200 edge is not open)"
                : r.LinkSteps.Count == 0 || r.LinkSteps.Any(s => Math.Abs(s.Step) > 1e-6) ? $"link steps [{string.Join(",", r.LinkSteps.Select(s => s.Step))}], want some at 0" : null;
        });
        Case("2585 marker-linked-step-over-limit", () =>
        {
            var m = Linked2(80);
            var r = NavMarker.Check(m, new Vec3(180, 100, 0));
            var m2 = Linked2(60);
            var r2 = NavMarker.Check(m2, new Vec3(180, 100, 0));
            return r.Verdict == "refused" && r.Why.Any(w => w.Contains("80 u apart")) && r2.Verdict == "inside"
                ? null : $"80 u: {r.Verdict} {string.Join(" ", r.Why)}; 60 u: {r2.Verdict} {string.Join(" ", r2.Why)}";
        });
        Case("2586 marker-deleted-neighbour-and-links-it-cannot-follow", () =>
        {
            var (v, t) = Shifted(0, 0);
            // triangle 1 (the south-west square's other half) deleted: the edge to it is open from triangle 0
            t[1] = t[1] with { Flags = t[1].Flags | 0x08 };
            var r = NavMarker.Check(new Dictionary<FormKey, NavmeshGeometry> { [K] = GeomK(K, v, t) }, new Vec3(70, 40, 0));
            if (r.Verdict != "refused" || r.OpenEdge is not { } o || o.Distance > 30) return $"deleted neighbour: {r.Verdict}, open edge {r.OpenEdge?.Distance} (want under 30)";
            // the east navmesh not given: its links 10 u away are unfollowed, never "inside"
            var m = Linked2(0); m.Remove(MK2);
            var u = NavMarker.Check(m, new Vec3(190, 100, 0));
            if (u.Verdict != "refused" || !u.Unfollowed.Any(x => x.Mesh == MK2 && Math.Abs(x.Distance - 10) < 1e-6) || !u.Why.Any(w => w.Contains("was not loaded")))
                return $"unfollowed link: {u.Verdict}, [{string.Join(",", u.Unfollowed)}]: {string.Join(" ", u.Why)}";
            // the east navmesh absent from the load order: the linked edge is open, 10 u away
            var a = NavMarker.Check(m, new Vec3(190, 100, 0), absent: new HashSet<FormKey> { MK2 });
            return a.Verdict == "refused" && a.OpenEdge is { } ao && Math.Abs(ao.Distance - 10) < 1e-6 && a.Unfollowed.Count == 0
                ? null : $"absent target: {a.Verdict}, open edge {a.OpenEdge?.Distance}, unfollowed {a.Unfollowed.Count}";
        });
        Case("2587 marker-far-above-the-navmesh", () =>
        {   // a marker on a roof or a floor with no navmesh, 300 u over the only navmesh at its XY
            var r = NavMarker.Check(One(), new Vec3(100, 100, 300));
            var ok = NavMarker.Check(One(), new Vec3(100, 100, 20));
            return r.Verdict == "refused" && r.Why.Any(w => w.Contains("not on the navmesh") && w.Contains("300 u above")) && ok.Verdict == "inside"
                ? null : $"300 u: {r.Verdict} {string.Join(" ", r.Why)}; 20 u: {ok.Verdict} {string.Join(" ", ok.Why)}";
        });
        Case("2588 marker-stale-record-bounds", () =>
        {   // the record's stated box is stale (far from its vertices): the box comes from the vertices
            var g = One()[K];
            var stale = g with { Min = new Vec3(5000, 5000, 0), Max = new Vec3(5001, 5001, 0) };
            var r = NavMarker.Check(new Dictionary<FormKey, NavmeshGeometry> { [K] = stale }, new Vec3(100, 100, 0));
            return r.Verdict == "inside" && r.Mesh == K ? null : $"got {r.Verdict}: {string.Join(" ", r.Why)}";
        });
        Case("2589 marker-link-to-a-triangle-out-of-range", () =>
        {   // a link entry naming a triangle the linked navmesh does not have is an open edge, not an unknown
            var m = Linked2(0);
            var w = m[K];
            m[K] = w with { EdgeLinks = w.EdgeLinks!.Select(l => l with { TriangleIndex = 999 }).ToList() };
            var r = NavMarker.Check(m, new Vec3(190, 100, 0));
            return r.Verdict == "refused" && r.OpenEdge is { } o && Math.Abs(o.Distance - 10) < 1e-6 && r.Unfollowed.Count == 0
                ? null : $"got {r.Verdict}, open edge {r.OpenEdge?.Distance}: {string.Join(" ", r.Why)}";
        });

        Console.WriteLine(fail == 0 ? "navedit self-test: all cases ok" : $"navedit self-test: {fail} failed");
        return fail == 0 ? 0 : 1;
    }
}
