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

        // 4a: the bindings (TriggerMap.Bindings), over the order as pass 1 read it.
        var pluginKey = w.Order.Select(p => Mutagen.Bethesda.Plugins.ModKey.FromFileName(Path.GetFileName(p))).ToArray();
        var recordBindings = new Dictionary<Mutagen.Bethesda.Plugins.FormKey, List<Binding>>();
        var questFragmentStage = new Dictionary<(Mutagen.Bethesda.Plugins.FormKey Quest, string Fragment), int>();
        var recordEdid = new Dictionary<Mutagen.Bethesda.Plugins.FormKey, string?>();
        var bindings = new Dictionary<Mutagen.Bethesda.Plugins.FormKey, List<Binding>>();
        int records = TriggerMap.Bindings(w.Order, pluginKey, sx, System.Diagnostics.Stopwatch.StartNew(), recordBindings, questFragmentStage,
                                          new(), new(), recordEdid, new(), bindings);
        string B(Mutagen.Bethesda.Plugins.FormKey k) => bindings.TryGetValue(k, out var l)
            ? string.Join("; ", l.Select(b => $"{b.Kind} {recordEdid.GetValueOrDefault(b.Record)} {b.Script}.{b.Property} [{b.Via}]")) : "(none)";
        t.Check("PD3-S-B0 bindings: 4 on 3 records, naming M1-M4; M5 has none",
                records == 3 && bindings.Values.Sum(l => l.Count) == 4 && bindings.Keys.ToHashSet().SetEquals(new[] { F.M1, F.M2, F.M3, F.M4 }),
                $"{bindings.Values.Sum(l => l.Count)} on {records}: M5 {B(F.M5)}");
        t.Check("PD3-S-B0a M1 is bound by quest SwQ through SwQScript.Gate1, an object property",
                B(F.M1) == "quest SwQ SwQScript.Gate1 [script]", B(F.M1));
        t.Check("PD3-S-B0b M3 is bound by ref SwScriptedS through SwMissing.Target; M4 by SwScriptedT through SwRefScript.Gate",
                B(F.M3) == "ref SwScriptedS SwMissing.Target [script]" && B(F.M4) == "ref SwScriptedT SwRefScript.Gate [script]", $"{B(F.M3)} / {B(F.M4)}");
        t.Check("PD3-S-B0c the quest's fragment Fragment_0 is stage 10",
                questFragmentStage.TryGetValue((F.Q, "Fragment_0"), out var st) && st == 10, string.Join(", ", questFragmentStage.Select(kv => $"{kv.Key.Fragment}={kv.Value}")));
    });
}
