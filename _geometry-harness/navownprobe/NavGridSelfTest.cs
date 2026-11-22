using HarnessShared;
using HousecarlCore;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Skyrim;

// `navownprobe --navgrid-selftest [SKYRIM_DATA_FOLDER]`: known answers for NavGrid (tests/test_navgrid_rebuild.py runs it and
// reads each line). Each case prints "ok NAME" or "FAIL NAME: why". The vanilla case needs the game's Data folder; without it
// that case fails and says so.
static class NavGridSelfTest
{
    // The hand-computed navmesh: a 3 x 3 block of squares on x = 0, 120, 240, 300 and y = 0, 100, 200, 300, each square cut
    // from its low corner to its high corner. Square (i, j) holds triangle 2(3j + i), its lower-right half, and 2(3j + i) + 1,
    // its upper-left half. Plus one vertex no triangle uses, far outside.
    static readonly float[] Xs = { 0, 120, 240, 300 }, Ys = { 0, 100, 200, 300 };

    static (List<Vec3> V, List<(int, int, int)> T) Block()
    {
        var v = new List<Vec3>();
        for (int j = 0; j < 4; j++) for (int i = 0; i < 4; i++) v.Add(new Vec3(Xs[i], Ys[j], i == 3 && j == 3 ? 10 : 0));
        v.Add(new Vec3(-500, -500, 99)); // unused: the box must leave it out
        int P(int i, int j) => 4 * j + i;
        var t = new List<(int, int, int)>();
        for (int j = 0; j < 3; j++)
            for (int i = 0; i < 3; i++)
            {
                t.Add((P(i, j), P(i + 1, j), P(i + 1, j + 1)));
                t.Add((P(i, j), P(i + 1, j + 1), P(i, j + 1)));
            }
        return (v, t);
    }

    // Worked by hand. 18 triangles → divisor 2; the box is 0..300 in x and y, 0..10 in z; a cell is 150 wide, so the cells'
    // edges are x = 150 and y = 150. A square in the middle column or row reaches both cells across that edge, and each of its
    // halves is listed in a cell only if the half itself meets it. The one half that does not: the middle square's lower-right
    // half (triangle 8, below the line from (120, 100) to (240, 200)) is 25 u below cell 2 (x <= 150, y >= 150) where it
    // crosses x = 150, though its box reaches that cell. A rule by boxes alone would list it there.
    static readonly ushort[][] BlockCells =
    {
        new ushort[] { 0, 1, 2, 3, 6, 7, 8, 9 },        // cell 0: x 0-150, y 0-150
        new ushort[] { 2, 3, 4, 5, 8, 9, 10, 11 },      // cell 1: x 150-300, y 0-150
        new ushort[] { 6, 7, 9, 12, 13, 14, 15 },       // cell 2: x 0-150, y 150-300 (not 8)
        new ushort[] { 8, 9, 10, 11, 14, 15, 16, 17 },  // cell 3: x 150-300, y 150-300
    };

    /// <summary>Null when the grid is the hand answer, else the first difference in one sentence.</summary>
    static string? AgainstHand(NavGridData g)
    {
        if (g.Divisor != 2) return $"divisor {g.Divisor}, want 2";
        if (g.Min != new Vec3(0, 0, 0) || g.Max != new Vec3(300, 300, 10)) return $"box {g.Min}..{g.Max}, want (0,0,0)..(300,300,10)";
        for (int k = 0; k < 4; k++)
            if (!g.Cells[k].SequenceEqual(BlockCells[k])) return $"cell {k} lists [{string.Join(",", g.Cells[k])}], want [{string.Join(",", BlockCells[k])}]";
        if (g.SectionX != 150 || g.SectionY != 150) return $"cell size {g.SectionX} x {g.SectionY}, want 150 x 150";
        return null;
    }

    public static int Run(string? dataFolder)
    {
        int fail = 0;
        void Case(string name, Func<string?> body)
        {
            string? why;
            try { why = body(); } catch (Exception ex) { why = $"threw {ex.GetType().Name}: {ex.Message}"; }
            Console.WriteLine(why is null ? $"ok {name}" : $"FAIL {name}: {why}");
            if (why is not null) fail++;
        }

        // C2507: the divisor at each side of its steps, and its cap.
        Case("2507 divisor-steps", () =>
        {
            var want = new (int Tris, uint Div)[] { (1, 1), (16, 1), (17, 2), (49, 2), (50, 3), (99, 3), (100, 4), (149, 4), (150, 5), (449, 10), (450, 11), (499, 11), (500, 12), (3342, 12), (65536, 12) };
            var bad = want.Where(w => NavGrid.Divisor(w.Tris) != w.Div).Select(w => $"{w.Tris} triangles → {NavGrid.Divisor(w.Tris)}, want {w.Div}").ToList();
            return bad.Count == 0 ? null : string.Join("; ", bad);
        });

        // C2508: the hand-computed navmesh, header and cells.
        Case("2508 hand-grid", () => { var (v, t) = Block(); return AgainstHand(NavGrid.Rebuild(v, t)); });

        // C2509: the sabotage arm. The same navmesh filled with an off-by-one cell size (the extent over divisor + 1) must fail
        // 2508's own assertion on a cell, not throw and not pass.
        Case("2509 sabotage-cell-size", () =>
        {
            var (v, t) = Block();
            var (min, max) = NavGrid.Bounds(v, t);
            float wrong = (max.X - min.X) / 3;
            var g = NavGrid.Fill(v, t, 2, min, max, wrong, wrong);
            var why = AgainstHand(g with { SectionX = 150, SectionY = 150 });
            return why is not null && why.StartsWith("cell ") ? null : $"the off-by-one size was not caught by a cell: {why ?? "it passed"}";
        });

        // C2510: the encoding is the inverse of houseCARL core's decode (the one reader of these bytes).
        Case("2510 encode-is-decode-inverse", () =>
        {
            var (v, t) = Block();
            var g = NavGrid.Rebuild(v, t);
            var back = NavmeshGridDecode.Cells(g.Divisor, g.CellBytes, out var why);
            if (back is null) return $"core decode refused the bytes: {why}";
            for (int k = 0; k < back.Length; k++) if (!back[k].SequenceEqual(g.Cells[k])) return $"cell {k} decodes to [{string.Join(",", back[k])}]";
            int want = 4 * 4 + 2 * BlockCells.Sum(c => c.Length);
            return g.CellBytes.Length == want ? null : $"{g.CellBytes.Length} bytes, want {want}";
        });

        // C2511: refusals, each one sentence: no triangles; a triangle naming a vertex that is not there.
        Case("2511 refusals", () =>
        {
            string? Refused(Action a) { try { a(); return null; } catch (ArgumentException ex) { return ex.Message; } }
            var none = Refused(() => NavGrid.Rebuild(new List<Vec3> { new(0, 0, 0) }, new List<(int, int, int)>()));
            var past = Refused(() => NavGrid.Rebuild(new List<Vec3> { new(0, 0, 0), new(1, 0, 0) }, new List<(int, int, int)> { (0, 1, 2) }));
            if (none is null || !none.Contains("no triangles")) return $"no triangles: {none ?? "not refused"}";
            if (past is null || !past.Contains("vertex 2")) return $"a missing vertex: {past ?? "not refused"}";
            return null;
        });

        // C2512: a real vanilla navmesh, 05CB2A in Skyrim.esm (2,474 triangles, divisor 12), reproduced byte for byte.
        Case("2512 vanilla-05CB2A", () =>
        {
            if (dataFolder is null) return "no Data folder given; pass the game's Data folder (navownprobe --navgrid-selftest DATA) to run this case.";
            var path = Path.Combine(dataFolder, "Skyrim.esm");
            if (!File.Exists(path)) return $"{path} is not there; pass the folder that holds Skyrim.esm.";
            using var mod = SkyrimMod.CreateFromBinaryOverlay(path, SkyrimRelease.SkyrimSE);
            var key = new FormKey(new ModKey("Skyrim", ModType.Master), 0x05CB2A);
            var nm = mod.EnumerateMajorRecords<INavigationMeshGetter>().FirstOrDefault(n => n.FormKey == key);
            if (nm?.Data is not { } d) return "Skyrim.esm has no navmesh 05CB2A with geometry; is this the shipped Skyrim.esm?";
            var g = NavGrid.Rebuild(d.Vertices.Select(x => new Vec3(x.X, x.Y, x.Z)).ToList(), d.Triangles.Select(x => ((int)x.Vertices.X, (int)x.Vertices.Y, (int)x.Vertices.Z)).ToList());
            if (g.Divisor != d.NavmeshGridDivisor) return $"divisor {g.Divisor}, stored {d.NavmeshGridDivisor}";
            if (g.SectionX != d.MaxDistanceX || g.SectionY != d.MaxDistanceY) return $"cell size {g.SectionX} x {g.SectionY}, stored {d.MaxDistanceX} x {d.MaxDistanceY}";
            if (g.Min != new Vec3(d.Min.X, d.Min.Y, d.Min.Z) || g.Max != new Vec3(d.Max.X, d.Max.Y, d.Max.Z)) return $"box {g.Min}..{g.Max}, stored {d.Min}..{d.Max}";
            if (!g.CellBytes.AsSpan().SequenceEqual(d.NavmeshGrid.Span)) return $"the cell bytes differ ({g.CellBytes.Length} rebuilt, {d.NavmeshGrid.Length} stored)";
            return null;
        });

        Console.WriteLine(fail == 0 ? "navgrid self-test: all cases ok" : $"navgrid self-test: {fail} failed");
        return fail == 0 ? 0 : 1;
    }
}
