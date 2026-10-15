using HarnessShared;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Skyrim;
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
    });

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
