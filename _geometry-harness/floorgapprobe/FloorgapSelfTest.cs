using HarnessShared;
using HousecarlCore;
using Mutagen.Bethesda.Plugins;
using Fx = HarnessShared.MovedRefFixture;

// floorgapprobe --moved-ref-selftest: the fixture's order walked by FloorgapWalk (the code a --winning run calls), with the
// fixture's E00 as the wanted cell. Expectations are worked out by hand from MovedRefFixture's table.
static class FloorgapSelfTest
{
    public static int Run() => MovedRefSelfTest.Run("floorgapprobe", t =>
    {
        var walk = new FloorgapWalk(winning: true, wantCell: Fx.E00.ToString());
        foreach (var path in t.Paths)
        {
            if (walk.Open(path) is not { } m) { t.Check($"open {Path.GetFileName(path)}", false, "not readable"); continue; }
            using (m as IDisposable) walk.Plugin(m, path, _ => { });
        }

        var expect = new Dictionary<FormKey, (FormKey? Cell, float X, float Y, bool Deleted)>
        {
            [Fx.M6] = (null, 1500, 1500, false), [Fx.C1] = (null, 1200, 1100, false), [Fx.C2] = (null, 210, 100, false),
            [Fx.P1] = (null, 300, 300, false), [Fx.P3] = (null, 320, 300, false), [Fx.NPC1] = (null, 400, 400, false),
            [Fx.U3] = (null, 600, 600, false),
        };
        t.CheckSet("G1  the merged E00: moved out and deleted references are gone; each at its last sighting",
                   walk.MergedRefs.Select(kv => (kv.Key, (FormKey?)null, kv.Value)), expect);
        string Where(FormKey k) => walk.MergedRefs.TryGetValue(k, out var r) ? $"at {r.Position.X:0},{r.Position.Y:0}" : "absent";
        t.Check("G1-M1 moved to a far cell: not merged", !walk.MergedRefs.ContainsKey(Fx.M1), Where(Fx.M1));
        t.Check("G1-D1 deleted with no placement data: not merged", !walk.MergedRefs.ContainsKey(Fx.D1), Where(Fx.D1));
        t.Check("C2 control: untouched, at its place" + (t.Canary ? " [CANARY: expectation planted wrong]" : ""),
                Where(Fx.C2) == (t.Canary ? "at 999,999" : "at 210,100"), Where(Fx.C2));
        // The merged cell's LAND: the last LAND is unreadable (L1), so there are no heights and the walk names it.
        var reason = walk.GetType().GetProperty("MergedLandUnreadable")?.GetValue(walk) ?? walk.GetType().GetField("MergedLandUnreadable")?.GetValue(walk);
        bool hasMember = walk.GetType().GetProperty("MergedLandUnreadable") is not null || walk.GetType().GetField("MergedLandUnreadable") is not null;
        t.Check("G1-L1 the merged cell's last LAND is unreadable: no heights, and the walk names it",
                walk.MergedLand is null && reason is string, $"{(walk.MergedLand is null ? "no heights" : $"heights {walk.MergedLand[16, 16]:0}")}; "
                + (hasMember ? $"reason '{reason}'" : "no MergedLandUnreadable member"));

        // The floor neighbourhood, filed from every exterior cell.
        t.CheckFloor("G2", walk.RefsByWorld.GetValueOrDefault(Fx.W), walk.LandByWorld.GetValueOrDefault(Fx.W),
                     MovedRefSelfTest.UnreadableLandOf(walk, Fx.W), new[] { (0, 0), (1, 0), (5, 0), (-1, 0), (2, 0) });

        t.CheckRefusesUnreadable("P1 ", p => walk.Open(p));
        t.CheckRefusesMidWalk("P1-mid", p => { var w2 = new FloorgapWalk(true, Fx.E00.ToString()); var m = w2.Open(p)!; using (m as IDisposable) w2.Plugin(m, p, _ => { }); });
    });
}
