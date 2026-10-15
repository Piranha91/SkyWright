using HarnessShared;
using F = HarnessShared.BaseGameFixture;

// depprobe --self-test: _shared/BaseGameFixture.cs written (PD_FIXTURE_KEEP keeps it for a probe run). The rows the
// by-name population split decides come from probe runs on the written order; this checks the fixture is what its table says.
static class DepSelfTest
{
    public static int Run() => ProbeSelfTest.Run("depprobe", "BaseGameFixture (base-game plugin names)", t =>
    {
        var w = F.Write(t.Root);
        t.Check("CR-F1 the fixture writes Skyrim.esm, Dawnguard.esm, CrMod.esp and the USSEP-named plugin",
                w.Dependency.Count == 4 && w.Dependency.All(File.Exists), string.Join(", ", w.Dependency.Select(Path.GetFileName)));
    });
}
