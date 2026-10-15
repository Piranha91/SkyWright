using HarnessShared;

// waterprobe --self-test: _shared/CellBoundaryFixture.cs WaterFixture written (PD_FIXTURE_KEEP keeps it for a probe run),
// then the probe's own decisions over it, each against an answer worked out by hand. Exit 0 when every case passes.
static class WaterSelfTest
{
    public static int Run() => ProbeSelfTest.Run("waterprobe", "WaterFixture through the water decisions", t =>
    {
        var w = WaterFixture.Write(t.Root);
        t.Check("PD2-F0 the fixture writes the nine-plugin order and the patch WfFix.esp",
                w.Order.Count == 9 && w.Order.Append(w.Fix).All(File.Exists), string.Join(", ", w.Order.Append(w.Fix).Select(Path.GetFileName)));
    });
}
