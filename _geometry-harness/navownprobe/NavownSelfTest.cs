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
        var npcs = c.NpcPlacedAnywhere.Select(k => k == F.N1 ? "N1" : k == F.N2 ? "N2" : k.ToString()).OrderBy(x => x).ToList();
        t.Check("N1-NPC NpcPlacedAnywhere: N2 only (N1's one reference was re-based to N2; N2's other was deleted)",
                npcs.SequenceEqual(new[] { "N2" }), string.Join(", ", npcs));
        t.Check("C2 control: a witness untouched in E00" + (t.Canary ? " [CANARY: expectation planted wrong]" : ""),
                t.Canary ? !c.Refs.ContainsKey(F.C2) : c.Refs.ContainsKey(F.C2), c.Refs.ContainsKey(F.C2) ? "present" : "absent");

        // ---- N2: the UNCUT population ----
        var uncut = new UncutPopulation();
        foreach (var (i, mod, _) in t.Plugins())
            foreach (var cp in UncutPopulation.Cells(mod)) uncut.Note(cp, i, mod.ModKey);
        var cellNewRefs = new Dictionary<(FormKey Cell, int Plugin), List<FormKey>>();
        uncut.Resolve(k => k == F.S ? "Static" : null, new HashSet<string> { "Static" }, _ => true, cellNewRefs, out _, out _);
        // A's introductions only (plugin 1): the master's own placements are introductions too, and are not this case.
        var counted = uncut.Counted.Where(x => x.Plugin == 1).Select(x => $"{MovedRefSelfTest.Name(x.Ref)}@{(x.Cell == F.E00 ? "E00" : x.Cell.ToString())}/{x.Plugin}").OrderBy(x => x).ToList();
        t.Check("N2  UNCUT counts A's introductions still standing in E00: U3 only (U1 moved out, U2 deleted by B)",
                counted.SequenceEqual(new[] { "U3@E00/1" }), string.Join(", ", counted));
    });
}
