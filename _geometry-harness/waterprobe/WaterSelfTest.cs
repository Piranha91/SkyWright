using HarnessShared;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Skyrim;
using Noggog;
using F = HarnessShared.WaterFixture;

// waterprobe --self-test: _shared/CellBoundaryFixture.cs WaterFixture written (PD_FIXTURE_KEEP keeps it for a probe run),
// then the probe's own decisions over it (WaterFindings.cs), each against an answer worked out by hand from the fixture's
// table (proofprobe checks the per-cell verdicts the answers rest on). Arm 1 is the nine-plugin order; arm 2 adds the patch
// WfFix.esp, which puts C20's water back. eps 0.5 u, visible floor 5%.
static class WaterSelfTest
{
    const float Eps = 0.5f, MinVisible = 0.05f;

    public static int Run() => ProbeSelfTest.Run("waterprobe", "WaterFixture through the water decisions (WaterFindings)", t =>
    {
        var w = WaterFixture.Write(t.Root);
        t.Check("PD2-F0 the fixture writes the nine-plugin order and the patch WfFix.esp",
                w.Order.Count == 9 && w.Order.Append(w.Fix).All(File.Exists), string.Join(", ", w.Order.Append(w.Fix).Select(Path.GetFileName)));

        var (idx1, ext1, byKey1) = Judge(w.Order.ToList());
        string Res(DetectorModel.ResolvedWater r) => r.Has ? $"{r.Water} z {r.Z}" : "no water";
        var missing = WaterFindings.Missing(ext1).Select(r => Name(r.Cell.Key)).OrderBy(x => x, StringComparer.Ordinal).ToList();
        t.Check("PD2-W5 missing water is exactly C20 (HasWater cleared over a visible sea); C60's nulled link is not",
                missing.SequenceEqual(new[] { "C20" }), string.Join(", ", missing));

        var seams = WaterFindings.Seams(ext1, idx1, byKey1, Eps, Res);
        var rows = WaterFindings.SeamRows(seams).ToList();
        string Pair(SeamRow s) => $"{Name(s.A.Cell.Key)}|{Name(s.B.Cell.Key)}";
        var got = rows.Select(Pair).OrderBy(x => x, StringComparer.Ordinal).ToList();
        // C00|C01: both cells forward a truth (C01 the water mod's), joined under Update's water, apart under the winners'.
        t.Check("PD2-W2 the seam rows are exactly C00|C01, C00|C10, C10|C20, C60|C70 (Inherited and Joined pairs are not rows)",
                got.SequenceEqual(new[] { "C00|C01", "C00|C10", "C10|C20", "C60|C70" }), string.Join(", ", got)
                + "; all pairs: " + string.Join(", ", seams.Select(s => $"{Pair(s)} {s.Verdict}")));
        t.Check("PD2-W4 every seam row is still apart under the winner: Physical Holds",
                rows.Count > 0 && rows.All(s => s.Physical == PhysicalState.Holds), string.Join(", ", rows.Select(s => $"{Pair(s)} {s.Physical}")));
        string Who(string pair) => rows.FirstOrDefault(s => Pair(s) == pair) is { } s ? Name(WaterFindings.Attribute(s).Attributed.Cell.Key) : "absent";
        t.Check("PD2-W1 C00|C10: C00 forwards the truth, so the row names C10", Who("C00|C10") == "C10", Who("C00|C10"));
        t.Check("PD2-W1 C10|C20: both changed, so the row names side A, C10", Who("C10|C20") == "C10", Who("C10|C20"));
        t.Check("PD2-W1 C00|C01: both forward, so neither is non-forwarding and the row names side B, C01", Who("C00|C01") == "C01", Who("C00|C01"));

        // Arm 2 re-measures arm 1's rows, shaped as the emit writes them.
        var baseRows = new List<Conflict>();
        foreach (var r in WaterFindings.Missing(ext1))
            baseRows.Add(new Conflict("WATER-MISSING", "truth", "winner", Name(r.Cell.Key), new List<ConflictRecord> { new(r.Cell.Key.ToString(), "cell", null) }));
        foreach (var s in rows)
        {
            var (a, o) = WaterFindings.Attribute(s);
            baseRows.Add(new Conflict("WATER-SEAM", "other", "attributed", Pair(s),
                new List<ConflictRecord> { new(a.Cell.Key.ToString(), "cell", null) }, new List<ConflictRecord> { new(o.Cell.Key.ToString(), "cell", null) }));
        }
        var (_, _, byKey2) = Judge(w.Order.Append(w.Fix).ToList());
        var preds = WaterFindings.Predicates(baseRows, byKey2, MinVisible, Eps, Res);
        PhysicalState? P(string kind, FormKey k) => preds.FirstOrDefault(x => x.Kind == kind && x.FormKey == k.ToString())?.Physical;
        t.Check("PD2-W3 under WfFix: C20 forwards Update's water again, visible, so its WATER-MISSING row reads Gone",
                P("WATER-MISSING", F.C(2)) == PhysicalState.Gone, $"{P("WATER-MISSING", F.C(2))}");
        t.Check("PD2-W3 under WfFix: the seam rows naming C10 still read Holds (C10 still reverted)",
                preds.Where(x => x.Kind == "WATER-SEAM").All(x => x.Physical == PhysicalState.Holds) && preds.Count(x => x.Kind == "WATER-SEAM") == rows.Count,
                string.Join(", ", preds.Where(x => x.Kind == "WATER-SEAM").Select(x => $"{x.FormKey} {x.Physical}")));

        HeightlessWinner(t);
        GridlessCell(t);
        PartialWorldspace(t);
    });

    /// <summary>A worldspace override flagged partial form (record flag 0x4000) states only the fields it carries: the
    /// rest are the earlier version's. SR Exterior Cities' Tamriel override is EDID only with that flag; in game the sea
    /// stays (2026-11-21, `C:/hc-runs/2026-11-21-sw-overnight/ingame/PLAN.md`: the body swims at the same depth with and
    /// without it, the engine's water level -14,000), while waterprobe read "no water" and reported 20,748 seams. A
    /// whole-record override (no flag) still states what it carries, absent fields included.</summary>
    static void PartialWorldspace(ProbeSelfTest t)
    {
        var dir = Path.Combine(t.Root, "pd2-partial"); Directory.CreateDirectory(dir);
        var rel = SkyrimRelease.SkyrimSE;
        var sky = new ModKey("Skyrim", ModType.Master);
        FormKey W = new(sky, 0x800), Top = new(sky, 0x801), C = new(sky, 0x900), Wtr = new(sky, 0x802);
        Worldspace World(FormKey k) => new(k, rel) { EditorID = "PartWorld" };
        var skyPath = Path.Combine(dir, "Skyrim.esm");
        {
            var m = new SkyrimMod(sky, rel);
            m.Waters.Add(new Water(Wtr, rel) { EditorID = "DefaultWater" });
            var ws = World(W);
            ws.Water = new FormLinkNullable<IWaterGetter>(Wtr);
            ws.LodWater = new FormLinkNullable<IWaterGetter>(Wtr);
            ws.LodWaterHeight = -14000f;
            ws.LandDefaults = new WorldspaceLandDefaults { DefaultLandHeight = -27000f, DefaultWaterHeight = -14000f };
            ws.TopCell = new Cell(Top, rel) { Flags = default };
            // a sea cell that defers to the worldspace default (no height of its own)
            var cell = new Cell(C, rel) { EditorID = "P00", Flags = Cell.Flag.HasWater, Grid = new CellGrid { Point = new P2Int(0, 0) } };
            var blk = new WorldspaceBlock { BlockNumberX = 0, BlockNumberY = 0, GroupType = GroupTypeEnum.ExteriorCellBlock };
            var sub = new WorldspaceSubBlock { BlockNumberX = 0, BlockNumberY = 0, GroupType = GroupTypeEnum.ExteriorCellSubBlock };
            sub.Items.Add(cell); blk.Items.Add(sub); ws.SubCells.Add(blk); m.Worldspaces.Add(ws);
            m.BeginWrite.ToPath(skyPath).WithLoadOrder(Array.Empty<ISkyrimModGetter>()).Write();
        }
        string Over(string name, int flags)
        {
            var path = Path.Combine(dir, name + ".esp");
            var m = new SkyrimMod(new ModKey(name, ModType.Plugin), rel);
            var ws = World(W);
            ws.MajorRecordFlagsRaw = flags;
            m.Worldspaces.Add(ws);
            using var skyOv = SkyrimMod.CreateFromBinaryOverlay(skyPath, rel);
            m.BeginWrite.ToPath(path).WithLoadOrder(new[] { skyOv }).Write();
            return path;
        }
        var partial = Over("Partial", 0x4000);
        var full = Over("Full", 0);
        string Read(string over)
        {
            var (idx, _, byKey) = Judge(new List<string> { skyPath, over });
            var w = idx.WorldWinner(W);
            var r = byKey.TryGetValue(C, out var row) ? CellBoundaryIndex.Resolve(row.J.Winner, w) : default;
            return $"water {w?.Water ?? "none"}, default {w?.DefaultWaterHeight?.ToString() ?? "unset"}, lod {w?.LodWaterHeight?.ToString() ?? "unset"}; cell P00 z {r.Z?.ToString() ?? "?"}";
        }
        var gotPartial = Read(partial);
        t.Check("PD2-P3 a partial-form worldspace override (EDID only, flag 0x4000) keeps the earlier water: DefaultWater at -14000, and a deferring cell's surface at -14000",
                gotPartial == $"water {Wtr}, default -14000, lod -14000; cell P00 z -14000", gotPartial);
        var gotFull = Read(full);
        t.Check("PD2-P4 a whole-record worldspace override (no flag) states what it carries: no water, no default height",
                gotFull == "water none, default unset, lod unset; cell P00 z ?", gotFull);
    }

    /// <summary>The held-out UME x SR Exterior Cities baseline: waterprobe died (exit 0xE0434352, OverflowException in Rank)
    /// when a worldspace's persistent cell, whose grid is the int.MinValue sentinel, reached the ranked lists. A gridless
    /// cell ranks after every cell with a grid, by either order, and never by its sentinel.</summary>
    static void GridlessCell(ProbeSelfTest t)
    {
        var ws = new FormKey(ModKey.FromNameAndExtension("Skyrim.esm"), 0x3C);
        CellInfo Cell(uint id, int x, int y) => new() { Key = new FormKey(ModKey.FromNameAndExtension("Skyrim.esm"), id), Worldspace = ws, GX = x, GY = y };
        var persistent = Cell(0xD74, int.MinValue, int.MinValue);
        var far = Cell(0x900, -60, 40);
        string Rank(CellInfo c, (int, int)? near)
        {
            try { return WaterFindings.Rank(c, near, false, true).ToString("0"); }
            catch (Exception ex) { return ex.GetType().Name; }
        }
        t.Check("PD2-P1 a persistent cell (no grid) is not a grid position: HasGrid false; a placed cell's true",
                !persistent.HasGrid && far.HasGrid, "");
        t.Check("PD2-P2 a persistent cell ranks after every cell with a grid, by distance from the origin and from --near",
                Rank(persistent, null) == "1000000000" && Rank(persistent, (5, -2)) == "1000000000"
                && double.Parse(Rank(far, null)) < 1e9 && double.Parse(Rank(far, (5, -2))) < 1e9,
                $"persistent {Rank(persistent, null)} / near {Rank(persistent, (5, -2))}; (-60,40) {Rank(far, null)} / near {Rank(far, (5, -2))}");
    }

    /// <summary>SS-2 (water side): Skyrim.esm with one watered cell (0,0) at height 100 over flat land at 0, and Hole.esp
    /// overriding that cell's LAND with one that declares no heights (no VHGT, DATA flag clear). The winner's ground is
    /// not Skyrim's: its visible share is not measured from Skyrim's heights.</summary>
    static void HeightlessWinner(ProbeSelfTest t)
    {
        var dir = Path.Combine(t.Root, "ss2-hole"); Directory.CreateDirectory(dir);
        var rel = SkyrimRelease.SkyrimSE;
        var sky = new ModKey("Skyrim", ModType.Master); var hole = new ModKey("Hole", ModType.Plugin);
        FormKey W = new(sky, 0x800), Top = new(sky, 0x801), C = new(sky, 0x900), L = new(sky, 0xA00);
        SkyrimMod Mod(ModKey k, Landscape land)
        {
            var m = new SkyrimMod(k, rel);
            var ws = new Worldspace(W, rel) { EditorID = "HoleWorld" };
            ws.TopCell = new Cell(Top, rel) { Flags = default };
            var cell = new Cell(C, rel) { EditorID = "H00", Flags = Cell.Flag.HasWater, Grid = new CellGrid { Point = new P2Int(0, 0) }, WaterHeight = 100f, Landscape = land };
            var blk = new WorldspaceBlock { BlockNumberX = 0, BlockNumberY = 0, GroupType = GroupTypeEnum.ExteriorCellBlock };
            var sub = new WorldspaceSubBlock { BlockNumberX = 0, BlockNumberY = 0, GroupType = GroupTypeEnum.ExteriorCellSubBlock };
            sub.Items.Add(cell); blk.Items.Add(sub); ws.SubCells.Add(blk); m.Worldspaces.Add(ws);
            return m;
        }
        var skyPath = Path.Combine(dir, "Skyrim.esm");
        Mod(sky, new Landscape(L, rel) { VertexHeightMap = new LandscapeVertexHeightMap { Offset = 0f, HeightMap = new Array2d<sbyte>(new sbyte[33, 33]) } })
            .BeginWrite.ToPath(skyPath).WithLoadOrder(Array.Empty<ISkyrimModGetter>()).Write();
        var holePath = Path.Combine(dir, "Hole.esp");
        using (var skyOv = SkyrimMod.CreateFromBinaryOverlay(skyPath, rel))
            Mod(hole, new Landscape(L, rel)).BeginWrite.ToPath(holePath).WithLoadOrder(new[] { skyOv }).Write();
        var (idx, _, byKey) = Judge(new List<string> { skyPath, holePath });
        var row = byKey.TryGetValue(C, out var r) ? r : null;
        var cell = idx.Cells[C];
        var winnerNone = cell.GetType().GetProperty("LandWinnerDeclaresNone")?.GetValue(cell) as bool?;
        t.Check("SS-2 water: Hole.esp's heightless LAND wins: the winner's visible share is not measured from Skyrim's heights",
                row is not null && row.J.VisWin is null && row.J.VisTruth is not null && winnerNone == true,
                row is null ? "no row" : $"visWin {row.J.VisWin?.ToString() ?? "null"}, visTruth {row.J.VisTruth?.ToString() ?? "null"}, "
                + $"LandWinnerDeclaresNone {winnerNone?.ToString() ?? "no member"}");
    }

    static string Name(FormKey k)
    {
        for (int x = 0; x < 8; x++) if (F.C(x) == k) return $"C{x}0";
        if (k == F.C01) return "C01";
        if (k == F.C11) return "C11";
        if (k == F.C50) return "C50";
        if (k == F.C30b) return "C30b";
        return k.ToString();
    }

    /// <summary>The order read and judged as waterprobe's pass 1 and its per-cell pass do it.</summary>
    static (CellBoundaryIndex Idx, List<Row> Ext, Dictionary<FormKey, Row> ByKey) Judge(List<string> paths)
    {
        var idx = new CellBoundaryIndex();
        for (int p = 0; p < paths.Count; p++)
        {
            using var mod = SkyrimMod.CreateFromBinaryOverlay(paths[p], SkyrimRelease.SkyrimSE);
            idx.Ingest(p, mod, true);
        }
        PluginKind Kind(int p) => idx.KindOf(p, 10);
        var dupGrid = new HashSet<(FormKey, int, int)>(idx.DuplicateGridPoints().Select(d => d.Grid));
        var rows = new List<Row>();
        foreach (var c in idx.Cells.Values)
            if (idx.JudgeWater(c, Kind, MinVisible, dupGrid) is { } j)
                rows.Add(new Row(j, c.Water.Select(s => s.Plugin).ToArray()));
        return (idx, rows.Where(r => !r.Cell.Interior).ToList(), rows.ToDictionary(r => r.Cell.Key));
    }
}
