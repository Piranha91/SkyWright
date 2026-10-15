using HarnessShared;

// seamprobe --self-test: _shared/CellBoundaryFixture.cs SeamFixture written (PD_FIXTURE_KEEP keeps it for a probe run),
// then the probe's own decisions over it, each against an answer worked out by hand. Exit 0 when every case passes.
static class SeamSelfTest
{
    public static int Run() => ProbeSelfTest.Run("seamprobe", "SeamFixture through the seam decisions", t =>
    {
        var w = SeamFixture.Write(t.Root);
        t.Check("PD2-F0 the fixture writes Skyrim.esm, SeamA.esp, SeamB.esp and the patch SeamC.esp",
                w.Order.Count == 3 && w.Order.Append(w.Fix).All(File.Exists), string.Join(", ", w.Order.Append(w.Fix).Select(Path.GetFileName)));
    });
}
