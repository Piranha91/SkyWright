using HarnessShared;
using Mutagen.Bethesda.Skyrim;
using F = HarnessShared.DriftFixture;

// driftprobe --self-test: _shared/DriftFixture.cs written (PD_FIXTURE_KEEP keeps it for a probe run), then the probe's
// own CHECK 2 decisions over it, each against an answer worked out by hand from the fixture's table. Exit 0 when every
// case passes. (--moved-ref-selftest is CHECK 1's, DriftSelfTest.cs.)
static class DriftQuestSelfTest
{
    public static int Run() => ProbeSelfTest.Run("driftprobe", "DriftFixture through the alias-split decisions", t =>
    {
        var w = F.Write(t.Root);
        t.Check("PD3-D-F0 the fixture writes DfMaster.esm, DfQuest.esp, DfMover.esp, DfInject.esp and the fix DfFix.esp",
                w.Order.Count == 4 && w.Order.Append(w.Fix).All(File.Exists), string.Join(", ", w.Order.Append(w.Fix).Select(Path.GetFileName)));

        // Smoke (Gate A risk 1): a package's location slot reads back as the reach filter reads it (PackFacts).
        using (var m = SkyrimMod.CreateFromBinaryOverlay(w.Order[0], F.Rel))
        {
            string Slots(Mutagen.Bethesda.Plugins.FormKey k) =>
                m.Packages.FirstOrDefault(p => p.FormKey == k) is { } pk
                    ? string.Join(", ", PackFacts.From(pk).Locations.Select(l => $"{l.Arm} r={l.Radius}")) : "(no package)";
            t.Check("PD3-D-F1 DfStay600 reads back as one slot at its editor location, radius 600", Slots(F.Pk600) == "editor-location r=600", Slots(F.Pk600));
            t.Check("PD3-D-F2 DfStay100 reads back as one slot at its editor location, radius 100", Slots(F.Pk100) == "editor-location r=100", Slots(F.Pk100));
        }
    });
}
