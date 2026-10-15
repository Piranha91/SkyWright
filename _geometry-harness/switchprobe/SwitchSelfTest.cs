using HarnessShared;
using HousecarlCore;
using Mutagen.Bethesda;
using Mutagen.Bethesda.Pex;
using Mutagen.Bethesda.Skyrim;
using F = HarnessShared.SwitchFixture;

// switchprobe --self-test: _shared/SwitchFixture.cs written (PD_FIXTURE_KEEP keeps it for a probe run), then the probe's
// own trigger-map decisions over it, each against an answer worked out by hand from the fixture's table. Exit 0 when every
// case passes.
static class SwitchSelfTest
{
    public static int Run() => ProbeSelfTest.Run("switchprobe", "SwitchFixture through the trigger map", t =>
    {
        var w = F.Write(t.Root);
        t.Check("PD3-S-F0 the fixture writes SwMaster.esm, SwQuest.esp, SwRef.esp and two scripts",
                w.Order.Count == 3 && w.Order.All(File.Exists) && File.Exists(Path.Combine(w.ModsDir, F.QuestKey.Name, "Scripts", F.QuestScript + ".pex"))
                && File.Exists(Path.Combine(w.ModsDir, F.RefKey.Name, "Scripts", F.RefScript + ".pex")),
                string.Join(", ", w.Order.Select(Path.GetFileName)));

        // The census over the fixture, built as the probe builds it (pass 1 and SwitchIndex).
        var idx = new OverlapIndex();
        for (int p = 0; p < w.Order.Count; p++)
            using (var mod = SkyrimMod.CreateFromBinaryOverlay(w.Order[p], F.Rel)) idx.Ingest(p, mod);
        var sx = SwitchIndex.Build(idx);
        t.Check("PD3-S-F1 the census finds exactly the five markers as switches",
                sx.Switches.Keys.ToHashSet().SetEquals(new[] { F.M1, F.M2, F.M3, F.M4, F.M5 }), string.Join(", ", sx.Switches.Keys));

        // Smoke (Gate A risk 2): the written function bodies decompile to the calls the trigger map looks for.
        string Decompiled(Mutagen.Bethesda.Plugins.ModKey mod, string script) =>
            PapyrusDecompiler.DecompileFile(PexFile.CreateFromFile(Path.Combine(w.ModsDir, mod.Name, "Scripts", script + ".pex"), GameCategory.Skyrim)).Source;
        var qs = Decompiled(F.QuestKey, F.QuestScript);
        var rs = Decompiled(F.RefKey, F.RefScript);
        t.Check("PD3-S-F2 SwQScript decompiles with Gate1.Enable() in Fragment_0",
                ScriptCalls.Parse(qs, 0).CallsOn("Gate1").Any(c => c.Function == "Fragment_0"), qs.Replace("\r", "").Replace("\n", " | "));
        t.Check("PD3-S-F3 SwRefScript decompiles with Gate.Disable() in OnActivate",
                ScriptCalls.Parse(rs, 0).CallsOn("Gate").Any(c => c.Function == "OnActivate"), rs.Replace("\r", "").Replace("\n", " | "));
    });
}
