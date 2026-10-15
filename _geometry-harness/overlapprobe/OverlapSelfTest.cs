using HarnessShared;
using F = HarnessShared.BaseGameFixture;

// overlapprobe --self-test: _shared/BaseGameFixture.cs written (PD_FIXTURE_KEEP keeps it for a probe run). The rows the
// by-name population split decides come from probe runs on the written order; this checks the fixture is what its table says.
static class OverlapSelfTest
{
    public static int Run() => ProbeSelfTest.Run("overlapprobe", "BaseGameFixture (base-game plugin names)", t =>
    {
        var w = F.Write(t.Root);
        t.Check("CR-F0 the fixture writes Skyrim.esm, Dawnguard.esm, CrMod.esp, CrA.esp, CrB.esp and the render box",
                w.Overlap.Count == 5 && w.Overlap.All(File.Exists) && File.Exists(Path.Combine(w.OverlapMods, F.SkyrimKey.Name, "meshes", F.MeshRel)),
                string.Join(", ", w.Overlap.Select(Path.GetFileName)));
    });
}
