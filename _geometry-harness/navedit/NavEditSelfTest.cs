using HarnessShared;
using HousecarlCore;
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
            foreach (var x in tt)
                foreach (var (a, b) in new[] { (x.A, x.B), (x.B, x.C), (x.C, x.A) })
                    foreach (var k in used)
                    {
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

        // ---- the cut outline of a placed obstacle (NavCutFootprint) ----
        TriangleSoup BoxSoup(float x0, float y0, float z0, float x1, float y1, float z1)
        {
            var c = new System.Numerics.Vector3[8];
            for (int i = 0; i < 8; i++) c[i] = new((i & 1) == 0 ? x0 : x1, (i & 2) == 0 ? y0 : y1, (i & 4) == 0 ? z0 : z1);
            int[] f = { 0, 2, 1, 1, 2, 3, 4, 5, 6, 5, 7, 6, 0, 1, 4, 1, 5, 4, 2, 6, 3, 3, 6, 7, 0, 4, 2, 2, 4, 6, 1, 3, 5, 3, 7, 5 };
            return new TriangleSoup(c, f);
        }
        string Ring(IReadOnlyList<(float X, float Y)> o) => string.Join(" ", o.OrderBy(p => p.X).ThenBy(p => p.Y).Select(p => $"({p.X:0.##},{p.Y:0.##})"));

        // C2535: a 40 x 20 box standing 200 u tall on a floor at 0: its outline is the box, and the band runs from step
        // height below the floor (-48) to an actor's top above it (108.8).
        Case("2535 footprint-box", () =>
        {
            var fp = NavCutFootprint.Of(BoxSoup(80, 90, -10, 120, 110, 190), 0);
            if (fp.Status != "planned") return "refused: " + fp.Why;
            if (Ring(fp.Outline) != "(80,90) (80,110) (120,90) (120,110)") return "outline " + Ring(fp.Outline);
            return fp.ZLo == -48 && Math.Abs(fp.ZHi - 108.8f) < 1e-3 ? null : $"band {fp.ZLo}..{fp.ZHi}";
        });

        // C2536: a margin of 8 grows the outline to 56 x 36.
        Case("2536 footprint-margin", () =>
        {
            var fp = NavCutFootprint.Of(BoxSoup(80, 90, -10, 120, 110, 190), 0, margin: 8);
            return fp.Status == "planned" && Ring(fp.Outline) == "(72,82) (72,118) (128,82) (128,118)" ? null : $"{fp.Status} {fp.Why} {Ring(fp.Outline)}";
        });

        // C2537: an obstacle wholly above an actor's top (z 150-300), and one below step height (z 0-30), block nobody:
        // both refused, naming the band.
        Case("2537 footprint-out-of-band", () =>
        {
            var high = NavCutFootprint.Of(BoxSoup(80, 90, 150, 120, 110, 300), 0);
            var low = NavCutFootprint.Of(BoxSoup(80, 90, 0, 120, 110, 30), 0);
            return high.Status == "refused" && high.Why!.Contains("step height") && low.Status == "refused" && low.Why!.Contains("step height") ? null : $"high {high.Status}, low {low.Status}";
        });

        // C2538: the floor under an obstacle is taken on the level nearest its height: with a bridge triangle 200 u over the
        // square, an obstacle at z 190 stands on the bridge, one at z 5 on the square.
        Case("2538 footprint-floor-level", () =>
        {
            var (v, t) = Linked();
            v.Add(new Vec3(50, 50, 200)); v.Add(new Vec3(150, 50, 200)); v.Add(new Vec3(100, 150, 200));
            t.Add(new NavTri(9, 10, 11, 0));
            var g = Geom(v, t);
            var lo = new System.Numerics.Vector2(90, 90); var hi = new System.Numerics.Vector2(110, 110);
            float? up = NavCutFootprint.FloorUnder(g, 100, 80, 190, lo, hi), down = NavCutFootprint.FloorUnder(g, 100, 80, 5, lo, hi);
            return up == 200 && down == 0 ? null : $"floor at 190: {up}, at 5: {down}";
        });

        // C2539: the footprint through the cut: the box's outline and band cut exactly its 800 u² out of the square.
        Case("2539 footprint-cut", () =>
        {
            var (v, t) = Linked();
            var fp = NavCutFootprint.Of(BoxSoup(80, 90, -10, 120, 110, 190), 0);
            return Sound(NavEdit.Cut(Geom(v, t), fp.Outline, fp.ZLo, fp.ZHi, none), 40000 - 800, new[] { (100.0, 100.0), (81.0, 91.0) });
        });

        // C2540: an obstacle of two separate rocks (20 x 20 boxes at (40-60, 40-60) and (140-160, 140-160)) is cut piece by
        // piece: two holes of 400 u² each, and the ground between them ((97.3, 103.1), off every lattice edge), which one hull
        // would take, stays.
        Case("2540 footprint-pieces", () =>
        {
            var (v, t) = Linked();
            var a = BoxSoup(40, 40, -10, 60, 60, 190); var b = BoxSoup(140, 140, -10, 160, 160, 190);
            var both = new TriangleSoup(a.Vertices.Concat(b.Vertices).ToArray(), a.Indices.Concat(b.Indices.Select(i => i + 8)).ToArray());
            var fps = NavCutFootprint.OfPieces(both, 0);
            if (fps.Count != 2) return $"{fps.Count} pieces, want 2";
            var r = NavEdit.CutMany(Geom(v, t), fps.Select(f => (f.Outline, f.ZLo, f.ZHi)).ToList(), none);
            if (Sound(r, 40000 - 800, new[] { (50.0, 50.0), (150.0, 150.0) }) is { } why) return why;
            var vv = r.After!.Vertices;
            bool between = r.After.Triangles.Any(x =>
            {
                double d(Vec3 p, Vec3 q) => ((double)q.X - p.X) * (103.1 - p.Y) - ((double)q.Y - p.Y) * (97.3 - p.X);
                double s1 = d(vv[x.A], vv[x.B]), s2 = d(vv[x.B], vv[x.C]), s3 = d(vv[x.C], vv[x.A]);
                return (s1 > 0 && s2 > 0 && s3 > 0) || (s1 < 0 && s2 < 0 && s3 < 0);
            });
            return between ? null : "the ground between the rocks was cut";
        });

        // C2528: bad input: a triangle index past the mesh, a corner that does not exist, nothing to do.
        Case("2528 bad-triangle-input", () =>
        {
            var (v, t) = Linked();
            return RefusedFor2(NavEdit.EditTriangles(Geom(v, t), new[] { 99 }, Array.Empty<Vec3>(), Array.Empty<(int, int, int)>(), none), "is not in")
                ?? RefusedFor2(NavEdit.EditTriangles(Geom(v, t), Array.Empty<int>(), Array.Empty<Vec3>(), new[] { (0, 1, 42) }, none), "does not exist")
                ?? RefusedFor2(NavEdit.EditTriangles(Geom(v, t), Array.Empty<int>(), Array.Empty<Vec3>(), Array.Empty<(int, int, int)>(), none), "No triangle");
        });

        Console.WriteLine(fail == 0 ? "navedit self-test: all cases ok" : $"navedit self-test: {fail} failed");
        return fail == 0 ? 0 : 1;
    }
}
