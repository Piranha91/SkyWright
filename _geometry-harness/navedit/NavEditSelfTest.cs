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

        Console.WriteLine(fail == 0 ? "navedit self-test: all cases ok" : $"navedit self-test: {fail} failed");
        return fail == 0 ? 0 : 1;
    }
}
