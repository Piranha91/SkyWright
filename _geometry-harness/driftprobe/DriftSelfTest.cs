using HarnessShared;
using Mutagen.Bethesda.Plugins;
using F = HarnessShared.MovedRefFixture;

// driftprobe --moved-ref-selftest: the fixture's order through AliasTargets (CHECK 1's question and the part of the walk
// that answers it), fed in the order driftprobe's pass 1 feeds it: each plugin's cells, then its records. Expectations
// are worked out by hand from MovedRefFixture's table (plugin indices: 0 master, 1 A, 2 B, 3 X, 4 L).
static class DriftSelfTest
{
    public static int Run() => MovedRefSelfTest.Run("driftprobe", t =>
    {
        var at = new AliasTargets(new PlacementHistory());
        foreach (var (i, mod, _) in t.Plugins())
        {
            foreach (var cp in AliasTargets.Cells(mod)) foreach (var r in cp.Refs) at.Placed(i, cp, r);
            foreach (var rec in mod.EnumerateMajorRecords()) at.Record(i, rec);
        }
        string B(FormKey k) => at.Break(k) is { } b ? $"{b.Kind} by {b.Plugin}" : "stands";
        t.Check("D1  deleted by A with no placement data: DELETED by A", B(F.D1) == "DELETED by 1", B(F.D1));
        t.Check("D1-D2 deleted by X in a copy holding only the deletion: DELETED by X", B(F.D2) == "DELETED by 3", B(F.D2));
        t.Check("D1-U2 introduced by A, deleted by B with no data: DELETED by B", B(F.U2) == "DELETED by 2", B(F.U2));
        t.Check("D1-NPC2 deleted by A with no data: DELETED by A", B(F.NPC2) == "DELETED by 1", B(F.NPC2));
        t.Check("D1-D4 deleted by A, placed again by B: stands", B(F.D4) == "stands", B(F.D4));
        t.Check("D1-D3 deleted by A WITH placement data: DELETED by A (control)", B(F.D3) == "DELETED by 1", B(F.D3));
        t.Check("D1-M1 moved: stands (control)" + (t.Canary ? " [CANARY: expectation planted wrong]" : ""),
                B(F.M1) == (t.Canary ? "DELETED by 1" : "stands"), B(F.M1));
        var none = new FormKey(F.MasterKey, 0xABC);
        t.Check("D1-none a reference nothing places: MISSING (control)", B(none) == "MISSING by -1", B(none));
    });
}
