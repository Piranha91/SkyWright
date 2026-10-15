using HarnessShared;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Skyrim;
using F = HarnessShared.SeamFixture;

// seamprobe --self-test: _shared/CellBoundaryFixture.cs SeamFixture written (PD_FIXTURE_KEEP keeps it for a probe run),
// then the probe's own decisions over it (SeamFindings.cs), each against an answer worked out by hand. Exit 0 when every
// case passes. Arm 1 is Skyrim, SeamA, SeamB; arm 2 adds the patch SeamC (masters Skyrim and SeamA). eps 8 u.
static class SeamSelfTest
{
    const float Eps = 8f;

    public static int Run() => ProbeSelfTest.Run("seamprobe", "SeamFixture through the seam decisions (SeamFindings)", t =>
    {
        var w = SeamFixture.Write(t.Root);
        t.Check("PD2-F0 the fixture writes Skyrim.esm, SeamA.esp, SeamB.esp and the patch SeamC.esp",
                w.Order.Count == 3 && w.Order.Append(w.Fix).All(File.Exists), string.Join(", ", w.Order.Append(w.Fix).Select(Path.GetFileName)));

        var pairs1 = Measure(w.Order.ToList());
        string Key(Pair p) => $"{Name(p.A.Key)}|{Name(p.B.Key)}";
        var cls = SeamFindings.Class(pairs1);
        var got = cls.Select(Key).OrderBy(x => x, StringComparer.Ordinal).ToList();
        var want = new[] { "S00|S10", "S01|S11", "S60|S70" };
        t.Check("PD2-E1 the class is exactly S00|S10 (Blind), S01|S11 (Seen), S60|S70 (Seen)",
                got.SequenceEqual(want), string.Join(", ", got));
        var floor = pairs1.Where(p => p.BothBethesda && p.Verdict == LandSeamVerdict.Seam).Select(Key).ToList();
        t.Check("PD2-E1 the vanilla seam S20|S21 is the Bethesda floor: measured, withheld", floor.SequenceEqual(new[] { "S20|S21" }), string.Join(", ", floor));
        var blind = cls.FirstOrDefault(p => Key(p) == "S00|S10");
        t.Check("PD2-E1 S00|S10 is Introduced+Blind, later SeamB, 16 u",
                blind is { Origin: SeamOrigin.Introduced, Sight: SeamSight.Blind } && blind.MaxDelta == 16f && blind.Later == 2,
                blind is null ? "absent" : $"{blind.Origin}/{blind.Sight} {blind.MaxDelta} later {blind.Later}");
        t.Check("PD2-E2 a mod's seam against a vanilla neighbour is not the floor (S01|S11 and S60|S70 in the class)",
                got.Contains("S01|S11") && got.Contains("S60|S70"), string.Join(", ", got));
        var atEps = pairs1.FirstOrDefault(p => Key(p) == "S40|S50");
        t.Check("PD2-E5 S40|S50 at exactly eps (8 u) is measured and not a seam",
                atEps is { Verdict: LandSeamVerdict.None } && atEps.MaxDelta == 8f, atEps is null ? "absent" : $"{atEps.Verdict} {atEps.MaxDelta}");

        // SS-2: SeamA's S80 is a LAND override that declares no heights (no VHGT, DATA flag clear). The engine draws no
        // ground there (LandHeight.LandKind.DeclaresNone), so Skyrim's heights are not the winner's: the index counts the
        // statement, the cell's winner declares none, and its two edges (S70|S80, S80|S90) are counted as not measurable,
        // not measured against Skyrim's heights. New members are read by name so this file compiles before they exist.
        var (idxA, closureA, landCellsA) = Index(w.Order.ToList());
        var s80 = idxA.Cells[F.C(8, 0)];
        int? Member(object o, string name) => o.GetType().GetProperty(name)?.GetValue(o) is { } v ? Convert.ToInt32(v) : null;
        var declared = Member(s80, "LandDeclaresNone");
        var winnerNone = s80.GetType().GetProperty("LandWinnerDeclaresNone")?.GetValue(s80) as bool?;
        var indexTotal = Member(idxA, "LandDeclaresNoneProvisions");
        t.Check("SS-2 S80: SeamA's heightless LAND is counted (1 provision), the cell's winner declares no heights, index total 1",
                declared == 1 && winnerNone == true && indexTotal == 1,
                $"LandDeclaresNone {declared?.ToString() ?? "no member"}, LandWinnerDeclaresNone {winnerNone?.ToString() ?? "no member"}, "
                + $"index {indexTotal?.ToString() ?? "no member"}; unreadable {s80.LandUnreadable}, statements {s80.Land.Count}");
        var touching = pairs1.Where(p => Key(p) is "S70|S80" or "S80|S90").Select(p => $"{Key(p)} {p.Verdict} {p.MaxDelta}").ToList();
        t.Check("SS-2 S70|S80 and S80|S90 are not measured against Skyrim's heights (no pair row)",
                touching.Count == 0, string.Join(", ", touching));
        object measured = SeamFindings.MeasurePairs(idxA, landCellsA, closureA, Eps, p => idxA.KindOf(p));
        int? heightless = measured.GetType().GetField("Item3")?.GetValue(measured) is { } hv ? Convert.ToInt32(hv) : null;
        t.Check("SS-2 the pass counts those two edges as not measurable: heightless 2",
                heightless == 2, heightless?.ToString() ?? "no third element");
        var s40 = idxA.Cells[F.C(4, 0)];
        t.Check("SS-2 control: S40 (heights restated by SeamA) declares none 0, its winner has heights",
                Member(s40, "LandDeclaresNone") == 0 && s40.GetType().GetProperty("LandWinnerDeclaresNone")?.GetValue(s40) as bool? == false,
                $"{Member(s40, "LandDeclaresNone")?.ToString() ?? "no member"}");

        // Arm 2 re-measures arm 1's rows, shaped as the emit writes them (necessary = the later cell, alternative = the other).
        var baseRows = cls.Select(p => new Conflict("LAND-SEAM", "other", "later", Key(p),
            new List<ConflictRecord> { new(p.LaterCell.Key.ToString(), "cell", null) },
            new List<ConflictRecord> { new(p.OtherCell.Key.ToString(), "cell", null) })).ToList();
        if (blind is not null)
            baseRows.Add(new Conflict("WATER-SEAM", "other", "later", "a row of another kind over a measured pair",
                new List<ConflictRecord> { new(blind.LaterCell.Key.ToString(), "cell", null) },
                new List<ConflictRecord> { new(blind.OtherCell.Key.ToString(), "cell", null) }));
        var pairs2 = Measure(w.Order.Append(w.Fix).ToList());
        var preds = SeamFindings.Predicates(baseRows, pairs2, Eps);
        PhysicalState? P(string kind, Pair p) => preds.FirstOrDefault(x => x.Kind == kind && x.FormKey == p.LaterCell.Key.ToString())?.Physical;
        var seen = cls.FirstOrDefault(p => Key(p) == "S01|S11");
        var edge = cls.FirstOrDefault(p => Key(p) == "S60|S70");
        t.Check("PD2-E3 under SeamC: S00|S10 Holds (SeamC leaves it); S01|S11 Gone (joined, 0 u)",
                blind is not null && seen is not null && P("LAND-SEAM", blind) == PhysicalState.Holds && P("LAND-SEAM", seen) == PhysicalState.Gone,
                $"{(blind is null ? "-" : P("LAND-SEAM", blind))} {(seen is null ? "-" : P("LAND-SEAM", seen))}");
        t.Check("PD2-E3 under SeamC: S60|S70 re-cut to exactly eps (8 u) reads Gone (Holds only past eps)",
                edge is not null && P("LAND-SEAM", edge) == PhysicalState.Gone, edge is null ? "absent" : $"{P("LAND-SEAM", edge)}");
        t.Check("PD2-E4 a row of another kind over a measured pair: Unmeasured",
                blind is not null && P("WATER-SEAM", blind) == PhysicalState.Unmeasured, blind is null ? "absent" : $"{P("WATER-SEAM", blind)}");
        t.Check("PD2-E3 every arm-1 row and the foreign row get one predicate", preds.Count == cls.Count + 1, $"{preds.Count}");
    });

    static string Name(FormKey k)
    {
        for (int x = 0; x < 10; x++) for (int y = 0; y < 2; y++) if (F.C(x, y) == k) return $"S{x}{y}";
        return k.ToString();
    }

    /// <summary>The order read as seamprobe's pass 1 reads it, then its pass 2 (SeamFindings.MeasurePairs).</summary>
    static List<Pair> Measure(List<string> paths)
    {
        var (idx, closure, landCells) = Index(paths);
        return SeamFindings.MeasurePairs(idx, landCells, closure, Eps, p => idx.KindOf(p)).Pairs;
    }

    /// <summary>The order read as seamprobe's pass 1 reads it: the index, the master closure, the LAND cells.</summary>
    static (CellBoundaryIndex Idx, IReadOnlyList<HashSet<int>> Closure, List<CellInfo> LandCells) Index(List<string> paths)
    {
        var idx = new CellBoundaryIndex();
        var keys = new ModKey[paths.Count];
        var masterIdx = new HashSet<int>[paths.Count];
        for (int p = 0; p < paths.Count; p++)
        {
            using var mod = SkyrimMod.CreateFromBinaryOverlay(paths[p], SkyrimRelease.SkyrimSE);
            keys[p] = mod.ModKey;
            var names = mod.ModHeader.MasterReferences.Select(m => m.Master).ToHashSet();
            masterIdx[p] = Enumerable.Range(0, p).Where(q => names.Contains(keys[q])).ToHashSet();
            idx.Ingest(p, mod, land: true);
        }
        var closure = DetectorModel.MasterClosure(masterIdx);
        var landCells = idx.Cells.Values.Where(c => !c.Interior && c.GX != int.MinValue && c.Land.Count > 0).ToList();
        return (idx, closure, landCells);
    }
}
