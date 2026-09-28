using HarnessShared;
using HousecarlCore;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Skyrim;
using F = HarnessShared.MovedRefFixture;

// blockprobe --moved-ref-selftest: the fixture's order walked by BlockWalk (the code step 2 of a real run calls), with the
// fixture's E00 and E10 as the target's cells. Expectations are worked out by hand from MovedRefFixture's table.
static class BlockSelfTest
{
    public static int Run() => MovedRefSelfTest.Run("blockprobe", t =>
    {
        var targets = new Dictionary<FormKey, CellPlacements>();
        using (var m0 = SkyrimMod.CreateFromBinaryOverlay(t.Paths[0], F.Rel))
            foreach (var cp in PlacementExtract.FromMod(m0))
                if (cp.Cell == F.E00 || cp.Cell == F.E10) targets[cp.Cell] = cp;
        t.Check("precondition: the target cells are E00 and E10", targets.Count == 2, string.Join(", ", targets.Keys));

        var walk = new BlockWalk(targets);
        foreach (var (_, mod, path) in t.Plugins())
        {
            if (BlockWalk.Open(path) is not { } m) { t.Check($"open {Path.GetFileName(path)}", false, "not readable"); continue; }
            using (m as IDisposable) walk.Plugin(m, Path.GetFileName(path), _ => { });
        }

        // The winning state of the target cells: the last sighting of each reference, where it is admitted.
        var expect = new Dictionary<FormKey, (FormKey? Cell, float X, float Y, bool Deleted)>
        {
            [F.M6] = (F.E00, 1500, 1500, false), [F.C1] = (F.E00, 1200, 1100, false), [F.C2] = (F.E00, 210, 100, false),
            [F.P1] = (F.E00, 300, 300, false), [F.P3] = (F.E00, 320, 300, false), [F.NPC1] = (F.E00, 400, 400, false),
            [F.U3] = (F.E00, 600, 600, false),
            [F.M2] = (F.E10, 4200, 100, false), [F.D3] = (F.E10, 4400, 200, true), [F.B0] = (F.E10, 4500, 100, false),
        };
        t.CheckSet("B1  winning state of the target cells: moved out, deleted and moved elsewhere are gone; each at its last sighting",
                   walk.WinRef.Select(kv => (kv.Key, (FormKey?)walk.WinRefCell[kv.Key], kv.Value)), expect);
        string Where(FormKey k) => walk.WinRef.TryGetValue(k, out var r) ? $"at {r.Position.X:0},{r.Position.Y:0} cell {walk.WinRefCell[k]}" : "absent";
        t.Check("B1-M1 moved to a far cell: not judged in the old one", !walk.WinRef.ContainsKey(F.M1), Where(F.M1));
        t.Check("B1-M5 made persistent (another cell): not judged in the old one", !walk.WinRef.ContainsKey(F.M5), Where(F.M5));
        t.Check("B1-D1 deleted with no placement data: not judged", !walk.WinRef.ContainsKey(F.D1), Where(F.D1));
        t.Check("B1-D2 deleted in a copy holding only the deletion: not judged", !walk.WinRef.ContainsKey(F.D2), Where(F.D2));
        t.Check("B1-M2 moved between two target cells: in E10 only", walk.WinRefCell.TryGetValue(F.M2, out var c2) && c2 == F.E10, Where(F.M2));
        t.Check("C2 control: untouched, in E00 at its place" + (t.Canary ? " [CANARY: expectation planted wrong]" : ""),
                Where(F.C2) == (t.Canary ? "at 999,999 cell " + F.E00 : "at 210,100 cell " + F.E00), Where(F.C2));

        // The floor under the target cells (their 3x3 neighbourhoods), and the LAND of the grids inside them.
        t.CheckFloor("B2", walk.FloorRefsByWorld.GetValueOrDefault(F.W), walk.FloorLandByWorld.GetValueOrDefault(F.W),
                     MovedRefSelfTest.UnreadableLandOf(walk, F.W), new[] { (0, 0), (1, 0), (-1, 0), (2, 0) });

        // A plugin that cannot be read is refused, not skipped (P1).
        t.CheckRefusesUnreadable("P1 ", p => BlockWalk.Open(p));
        t.CheckRefusesMidWalk("P1-mid", p => { var m = BlockWalk.Open(p)!; using (m as IDisposable) new BlockWalk(targets).Plugin(m, Path.GetFileName(p), _ => { }); });
    });
}
