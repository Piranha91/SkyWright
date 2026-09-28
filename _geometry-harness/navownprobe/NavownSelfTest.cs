using HarnessShared;
using HousecarlCore;
using Mutagen.Bethesda.Plugins;
using F = HarnessShared.MovedRefFixture;

// navownprobe --moved-ref-selftest: the fixture's order through the needs-witness census (NeedsWitnessJoin.Build, with E00
// as the pool's cell) and through the UNCUT population (UncutPopulation, as the main walk feeds it). Expectations are
// worked out by hand from MovedRefFixture's table.
static class NavownSelfTest
{
    public static int Run() => MovedRefSelfTest.Run("navownprobe", t =>
    {
        // ---- N1: the witness census ----
        var c = NeedsWitnessJoin.Build(t.Paths, new HashSet<FormKey> { F.E00 });
        var expect = new[] { F.M6, F.C1, F.C2, F.P1, F.P3, F.NPC1, F.U3 }.Select(MovedRefSelfTest.Name).OrderBy(x => x).ToList();
        var got = c.Refs.Keys.Select(MovedRefSelfTest.Name).OrderBy(x => x).ToList();
        t.Check("N1  witnesses in E00: exactly the references whose last sighting is there",
                got.SequenceEqual(expect), $"got [{string.Join(", ", got)}] expected [{string.Join(", ", expect)}]");
        t.Check("N1-M1 a witness moved out of the pool's cells is not a witness", !c.Refs.ContainsKey(F.M1),
                c.Refs.TryGetValue(F.M1, out var wm1) ? $"held in {wm1.Cell}" : "absent");
        string Cell(FormKey k) => c.RefCellAnywhere.TryGetValue(k, out var v) ? v.ToString() : "none";
        t.Check("N1-D1 RefCellAnywhere: a reference deleted with no placement data has no cell", !c.RefCellAnywhere.ContainsKey(F.D1), Cell(F.D1));
        t.Check("N1-D2 RefCellAnywhere: deleted in a copy holding only the deletion: no cell", !c.RefCellAnywhere.ContainsKey(F.D2), Cell(F.D2));
        t.Check("N1-M3 RefCellAnywhere: moved to an interior, that interior", Cell(F.M3) == F.I2.ToString(), Cell(F.M3));
        t.Check("N1-NPC4 RefCellAnywhere: a reference deleted WITH its data is placed nowhere", !c.RefCellAnywhere.ContainsKey(F.NPC4), Cell(F.NPC4));
        var c10 = NeedsWitnessJoin.Build(t.Paths, new HashSet<FormKey> { F.E10 });
        t.Check("N1-D3 a reference deleted WITH its data in a wanted cell is not a witness", !c10.Refs.ContainsKey(F.D3),
                c10.Refs.TryGetValue(F.D3, out var w3) ? $"held, state '{w3.State}'" : "absent");
        var npcs = c.NpcPlacedAnywhere.Select(k => k == F.N1 ? "N1" : k == F.N2 ? "N2" : k.ToString()).OrderBy(x => x).ToList();
        t.Check("N1-NPC NpcPlacedAnywhere: N2 only (N1's one reference was re-based to N2; N2's other was deleted)",
                npcs.SequenceEqual(new[] { "N2" }), string.Join(", ", npcs));
        t.Check("C2 control: a witness untouched in E00" + (t.Canary ? " [CANARY: expectation planted wrong]" : ""),
                t.Canary ? !c.Refs.ContainsKey(F.C2) : c.Refs.ContainsKey(F.C2), c.Refs.ContainsKey(F.C2) ? "present" : "absent");

        // ---- N3: the navmesh version history (NavVersions, as the main walk feeds it): whose winning version is a deletion.
        // NVD and NVK are deleted by A (1), NVK with its data; NVX by X (3), in a copy holding nothing else; NVR, deleted by
        // A, is restored by B (2) ----
        var hist = new NavVersions();
        foreach (var (i, mod, _) in t.Plugins())
        {
            var deletedHere = mod.EnumerateMajorRecords<Mutagen.Bethesda.Skyrim.INavigationMeshGetter>().Where(n => n.IsDeleted).Select(n => n.FormKey).ToHashSet();
            foreach (var cp in UncutPopulation.Cells(mod)) hist.Note(cp, i, deletedHere, _ => 0);
        }
        var del = hist.DeletedAtWinner().Select(kv => $"{MovedRefSelfTest.NavName(kv.Key)}/{kv.Value}").OrderBy(x => x).ToList();
        t.Check("N3  navmeshes whose winning version is a deletion, with or without data: NVD/1, NVK/1, NVX/3 (NVR restored by B)",
                del.SequenceEqual(new[] { "NVD/1", "NVK/1", "NVX/3" }), string.Join(", ", del));

        // ---- N4: the DELETED section's report, with the shipped-game deletions counted apart (default) or listed
        // (--list-master-deletions). The fixture has no Bethesda master, so plugin A (1) stands in for one: its deletions
        // (NVD, NVK) are the shipped game's; X's deletion (NVX, 3) is a mod's and a finding in both modes. ----
        var names = t.Plugins().ToDictionary(p => p.Index, p => p.Mod.ModKey.FileName.String);
        string CellOf(FormKey k) => k == F.E00 ? "E00" : k == F.E11 ? "E11" : k.ToString();
        List<string> Report(bool listShipped) => NavDeletions.Report(hist, _ => true, p => p == 1, p => names[p], CellOf,
                                                                     listN: 25, listShippedGame: listShipped);
        var off = Report(false);
        var on = Report(true);
        string Show(List<string> l) => string.Join(" | ", l.Select(x => x.Trim()));
        var nvx = $"   DELETED  {F.NVX} in E11 by {names[3]} (no geometry left)";
        t.Check("N4-off default: the findings are X's deletion only; the shipped-game deletions are a count, never listed",
                off.Contains(nvx) && off.Contains("   findings: 1")
                && off.Contains("   deleted by a Bethesda master (the shipped game; not findings): 2")
                && !off.Any(l => l.Contains(F.NVD.ToString()) || l.Contains(F.NVK.ToString())), Show(off));
        var shipped = on.Where(l => l.Contains(F.NVD.ToString()) || l.Contains(F.NVK.ToString())).ToList();
        t.Check("N4-on --list-master-deletions: NVD and NVK are listed, each line labelled a shipped-game deletion",
                shipped.Count == 2 && shipped.All(l => l.Contains("shipped-game deletion") && l.Contains($"by {names[1]}")),
                Show(on));
        t.Check("N4-on a mod's deletion stays a finding when the shipped game's are listed: findings 1, NVX in the findings",
                on.Contains(nvx) && on.Contains("   findings: 1")
                && on.IndexOf(nvx) < on.FindIndex(l => l.Contains(F.NVD.ToString()) || l.Contains(F.NVK.ToString())), Show(on));
        t.Check("N4-on the count line is unchanged by the toggle",
                on.Contains("   deleted by a Bethesda master (the shipped game; not findings): 2"), Show(on));
        t.Check("N4-off the default report is the toggle-on report minus the shipped-game listing, line for line",
                off.SequenceEqual(on.Where(l => !shipped.Contains(l) && !l.Contains("shipped-game deletions ("))), Show(off));

        // ---- N2: the UNCUT population ----
        var uncut = new UncutPopulation();
        foreach (var (i, mod, _) in t.Plugins())
            foreach (var cp in UncutPopulation.Cells(mod)) uncut.Note(cp, i, mod.ModKey);
        var cellNewRefs = new Dictionary<(FormKey Cell, int Plugin), List<FormKey>>();
        uncut.Resolve(k => k == F.S ? "Static" : null, new HashSet<string> { "Static" }, _ => true, cellNewRefs, out _, out _);
        // A's introductions only (plugin 1): the master's own placements are introductions too, and are not this case.
        var counted = uncut.Counted.Where(x => x.Plugin == 1).Select(x => $"{MovedRefSelfTest.Name(x.Ref)}@{(x.Cell == F.E00 ? "E00" : x.Cell.ToString())}/{x.Plugin}").OrderBy(x => x).ToList();
        t.Check("N2  UNCUT counts A's introductions still standing and still obstructing: U3 only (U1 moved out, U2 deleted by B, U4 re-based to a marker, U5 base cleared)",
                counted.SequenceEqual(new[] { "U3@E00/1" }), string.Join(", ", counted));
    });
}
