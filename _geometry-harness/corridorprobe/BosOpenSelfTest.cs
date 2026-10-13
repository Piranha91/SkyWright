using HarnessShared;

// --moved-ref-selftest for a probe whose only whole-order walk is the swap layer's index: a plugin that cannot be read is
// refused, naming it, rather than skipped (the swap index would otherwise miss that plugin's records with no word).
static class BosOpenSelfTest
{
    public static int Run(string probe) => MovedRefSelfTest.Run(probe, t =>
    {
        t.Check("control: a readable plugin opens" + (t.Canary ? " [CANARY: expectation planted wrong]" : ""),
                (BosWalkOpen.Open(t.Paths[0]) is not null) != t.Canary, t.Paths[0]);
        t.CheckRefusesUnreadable("P1 ", p => BosWalkOpen.Open(p));
        // The floor reach (FloorFeed.FloorReach) through the run's own single-plugin filing (FloorFeed.FileOnePlugin).
        t.CheckReachOne("C-R", p => { using var m = Mutagen.Bethesda.Skyrim.SkyrimMod.CreateFromBinaryOverlay(p, FloorReachFixture.Rel); return FloorFeed.FileOnePlugin(m); });
    });
}
