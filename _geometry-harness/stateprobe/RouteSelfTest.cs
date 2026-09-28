using HarnessShared;
using HousecarlCore;
using Mutagen.Bethesda.Plugins;
using F = HarnessShared.MovedRefFixture;

// stateprobe --moved-ref-selftest: the fixture's order walked by RouteWalk (the code the --route oracle calls), with the
// fixture's E00 and E10 as the wanted cells. Expectations are worked out by hand from MovedRefFixture's table.
static class RouteSelfTest
{
    public static int Run() => MovedRefSelfTest.Run("stateprobe", t =>
    {
        var walk = new RouteWalk(new HashSet<FormKey> { F.E00, F.E10 });
        walk.Seed(t.Paths[0]);
        foreach (var path in t.Paths)
        {
            if (RouteWalk.Open(path) is not { } m) { t.Check($"open {Path.GetFileName(path)}", false, "not readable"); continue; }
            using (m as IDisposable) walk.Plugin(m);
        }

        var expect = new Dictionary<FormKey, (FormKey? Cell, float X, float Y, bool Deleted)>
        {
            [F.M6] = (F.E00, 1500, 1500, false), [F.C1] = (F.E00, 1200, 1100, false), [F.C2] = (F.E00, 210, 100, false),
            [F.P1] = (F.E00, 300, 300, false), [F.P3] = (F.E00, 320, 300, false), [F.NPC1] = (F.E00, 400, 400, false),
            [F.U3] = (F.E00, 600, 600, false),
            [F.M2] = (F.E10, 4200, 100, false), [F.D3] = (F.E10, 4400, 200, true), [F.B0] = (F.E10, 4500, 100, false),
        };
        var got = walk.CellRefs.SelectMany(c => c.Value.Select(r => (r.FormKey, (FormKey?)c.Key, r))).ToList();
        t.CheckSet("S1  the wanted cells' references: one cell each, at the last sighting; moved out and deleted are gone", got, expect);
        string Cells(FormKey k) => string.Join(" ", got.Where(x => x.FormKey == k).Select(x => $"{x.Item2}@{x.r.Position.X:0},{x.r.Position.Y:0}"));
        t.Check("S1-M2 moved between two wanted cells: in E10 only", got.Count(x => x.FormKey == F.M2) == 1 && got.Any(x => x.FormKey == F.M2 && x.Item2 == F.E10), Cells(F.M2));
        t.Check("S1-M1 moved out: in no wanted cell", got.All(x => x.FormKey != F.M1), Cells(F.M1));
        t.Check("S1-D1 deleted with no placement data: in no wanted cell", got.All(x => x.FormKey != F.D1), Cells(F.D1));
        t.Check("C2 control: untouched, in E00" + (t.Canary ? " [CANARY: expectation planted wrong]" : ""),
                Cells(F.C2) == (t.Canary ? "nowhere" : $"{F.E00}@210,100"), Cells(F.C2));

        t.CheckFloor("S2", walk.FloorRefsByWorld.GetValueOrDefault(F.W), walk.FloorLandByWorld.GetValueOrDefault(F.W),
                     MovedRefSelfTest.UnreadableLandOf(walk, F.W), new[] { (0, 0), (1, 0), (-1, 0), (2, 0) });

        t.CheckRefusesUnreadable("P1 open", p => RouteWalk.Open(p));
        t.CheckRefusesUnreadable("P1 seed", p => { walk.Seed(p); return null; });
        t.CheckRefusesMidWalk("P1-mid walk", p => { var m = RouteWalk.Open(p)!; using (m as IDisposable) new RouteWalk(new HashSet<FormKey> { F.E00 }).Plugin(m); });
        t.CheckRefusesMidWalk("P1-mid seed", p => new RouteWalk(new HashSet<FormKey> { F.E00 }).Seed(p));
    });
}
