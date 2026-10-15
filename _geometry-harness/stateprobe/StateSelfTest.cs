using HarnessShared;
using F = HarnessShared.StateFixture;

// stateprobe --self-test: _shared/StateFixture.cs written (PD_FIXTURE_KEEP keeps it for a probe run), and the decisions the
// probe's top level hands to its classes checked on it. Expectations are worked out by hand from the fixture's table.
static class StateSelfTest
{
    public static int Run() => ProbeSelfTest.Run("stateprobe", "StateFixture (switches flipping collision boxes)", t =>
    {
        var w = F.Write(t.Root);
        t.Check("PD4-S-F0 the fixture writes Skyrim.esm, Dawnguard.esm, StMod.esp and the slab and cup meshes",
                w.Order.Count == 3 && w.Order.All(File.Exists)
                && File.Exists(Path.Combine(w.ModsDir, F.Skyrim.Name, "meshes", F.SlabMesh)) && File.Exists(Path.Combine(w.ModsDir, F.Skyrim.Name, "meshes", F.CupMesh)),
                string.Join(", ", w.Order.Select(Path.GetFileName)));
    });
}
