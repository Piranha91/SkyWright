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
                ScriptFlipCalls.Parse(qs, 0).FlipsOn("Gate1").Any(c => c.Function == "Fragment_0"), qs.Replace("\r", "").Replace("\n", " | "));
        t.Check("PD3-S-F3 SwRefScript decompiles with Gate.Disable() in OnActivate",
                ScriptFlipCalls.Parse(rs, 0).FlipsOn("Gate").Any(c => c.Function == "OnActivate"), rs.Replace("\r", "").Replace("\n", " | "));

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

        // 4b: each bound script through the VFS of the fixture's mod folders (TriggerMap.Decompile).
        foreach (var d in new[] { "overwrite", "data" }) Directory.CreateDirectory(Path.Combine(t.Root, d));
        using var assets = AssetResolver.Build(Path.Combine(t.Root, "overwrite"), w.ModsDir, Path.Combine(t.Root, "data"),
                                               new[] { F.RefKey.Name, F.QuestKey.Name, F.MasterKey.Name }, Array.Empty<ActiveArchive>());
        var scripts = bindings.Values.SelectMany(l => l).Select(b => b.Script).Where(s => s.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var source = new Dictionary<string, ScriptFlipCalls?>(StringComparer.OrdinalIgnoreCase);
        var unreadWhy = new Dictionary<string, int>();
        int pexUnreadable = 0, pexDecompileFailed = 0, scriptsDecompiled = 0;
        TriggerMap.Decompile(assets.Capture(), scripts, source, unreadWhy, ref pexUnreadable, ref pexDecompileFailed, ref scriptsDecompiled);
        t.Check("PD3-S-P0 three scripts bound: two decompiled, SwMissing not in the VFS (null), no function refused",
                scripts.Count == 3 && scriptsDecompiled == 2 && pexUnreadable == 1 && pexDecompileFailed == 0
                && unreadWhy.GetValueOrDefault("pex not in the VFS") == 1 && source.TryGetValue(F.MissingScript, out var miss) && miss is null,
                $"bound {scripts.Count}, decompiled {scriptsDecompiled}, unreadable {pexUnreadable} ({string.Join(", ", unreadWhy.Select(kv => $"{kv.Key} {kv.Value}"))}), refused {pexDecompileFailed}");

        // 4c: per switch (TriggerMap.Status).
        var jointFlips = new Dictionary<string, HashSet<Mutagen.Bethesda.Plugins.FormKey>>(StringComparer.OrdinalIgnoreCase);
        var status = new Dictionary<Mutagen.Bethesda.Plugins.FormKey, (string Status, string Kind, List<string> By)>();
        TriggerMap.Status(sx.Switches.Values.ToList(), bindings, source, questFragmentStage, recordEdid, jointFlips, status);
        string S(Mutagen.Bethesda.Plugins.FormKey k) => status.TryGetValue(k, out var v) ? $"{v.Status} | {v.Kind} | {string.Join(" / ", v.By)}" : "(none)";
        t.Check("PD3-S-T1 M1 is Triggered by quest SwQ at stage 10 (SwQScript.Fragment_0)",
                status[F.M1].Status == "Triggered" && status[F.M1].Kind == "quest stage" && status[F.M1].By.SequenceEqual(new[] { "quest SwQ stage 10 (SwQScript.Fragment_0: Gate1.Enable()" }), S(F.M1));
        t.Check("PD3-S-J the joint flips are exactly SwQ:10 -> {M1}",
                jointFlips.Count == 1 && jointFlips.TryGetValue("SwQ:10", out var jf) && jf.SetEquals(new[] { F.M1 }),
                string.Join(", ", jointFlips.Select(kv => $"{kv.Key} {kv.Value.Count}")));
        t.Check("PD3-S-B1 M2 is BoundUnread: its script was read and makes no call on Gate2",
                status[F.M2].Status == "BoundUnread" && status[F.M2].Kind == "bound, no Enable/Disable call found on the property", S(F.M2));
        t.Check("PD3-S-B2 M3 is BoundUnread: its script could not be read",
                status[F.M3].Status == "BoundUnread" && status[F.M3].Kind == "bound, the script could not be read", S(F.M3));
        t.Check("PD3-S-T2 M4 is Triggered by a ref event, OnActivate",
                status[F.M4].Status == "Triggered" && status[F.M4].Kind == "ref event OnActivate", S(F.M4));
        t.Check("PD3-S-U M5 is Unknown: nothing binds it", status[F.M5].Status == "Unknown" && status[F.M5].Kind == "no binding", S(F.M5));

        // SW2: the call rules (_shared/ScriptFlipCalls.cs, FlipsOn as the trigger map reads them), over decompiled-source text.
        string Calls(string src, string prop) => string.Join(", ", ScriptFlipCalls.Parse(src, 0).FlipsOn(prop).Select(c => $"{c.Function}:{c.Text}"));
        t.Check("PD3-S-C1 Gate.Enable() in Fragment_0 is a call on Gate in Fragment_0",
                Calls("Function Fragment_0()\n    Gate.Enable()\nEndFunction\n", "Gate") == "Fragment_0:Gate.Enable(", Calls("Function Fragment_0()\n    Gate.Enable()\nEndFunction\n", "Gate"));
        t.Check("PD3-S-C2 EnableList[i].Enable() is a call on EnableList (a list-bound marker)",
                Calls("Event OnInit()\n    EnableList[i].Enable()\nEndEvent\n", "EnableList").StartsWith("OnInit:"), Calls("Event OnInit()\n    EnableList[i].Enable()\nEndEvent\n", "EnableList"));
        t.Check("PD3-S-C3 a call through a local (ObjectReference x = Gate; x.Disable()) is a call on Gate",
                Calls("Function F()\n    ObjectReference x = Gate\n    x.Disable()\nEndFunction\n", "Gate").StartsWith("F:"), Calls("Function F()\n    ObjectReference x = Gate\n    x.Disable()\nEndFunction\n", "Gate"));
        t.Check("PD3-S-C4 gate.enable() is found for property Gate (case does not matter)",
                Calls("Function F()\n    gate.enable()\nEndFunction\n", "Gate").StartsWith("F:"), Calls("Function F()\n    gate.enable()\nEndFunction\n", "Gate"));
        t.Check("PD3-S-C5 Alias_Door.GetReference().Enable() is a call on Alias_Door",
                Calls("Function F()\n    Alias_Door.GetReference().Enable()\nEndFunction\n", "Alias_Door").StartsWith("F:"), Calls("Function F()\n    Alias_Door.GetReference().Enable()\nEndFunction\n", "Alias_Door"));
        t.Check("PD3-S-C6 GetLinkedRef().Enable() is a call on GetLinkedRef",
                Calls("Event OnActivate()\n    GetLinkedRef().Enable()\nEndEvent\n", "GetLinkedRef").StartsWith("OnActivate:"), Calls("Event OnActivate()\n    GetLinkedRef().Enable()\nEndEvent\n", "GetLinkedRef"));
        var self = ScriptFlipCalls.Parse("Event OnActivate()\n    Enable()\nEndEvent\n", 0);
        t.Check("PD3-S-C7 a bare Enable() is a call on Self and sets SelfCall",
                self.SelfCall && self.FlipsOn("Self").Any(c => c.Function == "OnActivate"), string.Join(", ", self.Calls));
        t.Check("PD3-S-C8 Gate.EnableLinkChain() is not a flip of Gate (it flips Gate's linked chain, not Gate)",
                Calls("Function F()\n    Gate.EnableLinkChain()\nEndFunction\n", "Gate") == "", Calls("Function F()\n    Gate.EnableLinkChain()\nEndFunction\n", "Gate"));

        CopiedRules(t, Calls);
        RawDump(t);
    });

    // F4 (2026-10-15): a function the decompiler refuses keeps its calls only in the raw bytecode dump the decompiler prints.
    // The order with SwRaw.esp added (FF-S), and the dump itself (FF-T).
    static void RawDump(ProbeSelfTest t)
    {
        string Fn(string body) => "Function F()\n" + body + "\nEndFunction\n";
        var dir = Path.Combine(t.Root, "raw");
        var w = F.Write(dir);
        var raw = F.WriteRawDump(dir);
        var dec = PapyrusDecompiler.DecompileFile(PexFile.CreateFromFile(Path.Combine(w.ModsDir, F.RawKey.Name, "Scripts", F.RawScript + ".pex"), GameCategory.Skyrim));
        t.Check("FF-T0 SwRawScript's Fragment_0 is refused by the decompiler and printed as raw bytecode",
                dec.FunctionsFailed == 1 && dec.Source.Contains("DECOMPILE FAILED"), dec.Source.Replace("\r", "").Replace("\n", " | "));
        var sc = ScriptFlipCalls.Parse(dec.Source, dec.FunctionsFailed);
        t.Check("FF-T1 the dump's TryToEnable on ::Alias_Door9_var is a flip of property Alias_Door9 in Fragment_0",
                sc.FlipsOn("Alias_Door9").Count(c => c.Function == "Fragment_0" && c.Text.StartsWith("raw bytecode ")) == 1, string.Join(", ", sc.Calls));
        t.Check("FF-T2 the dump's EnableLinkChain on ::Alias_Gate10_var is a call on Alias_Gate10 named enablelinkchain, and not a flip",
                sc.CallsOn("Alias_Gate10").Select(c => c.Call).SequenceEqual(new[] { "enablelinkchain" }) && !sc.FlipsOn("Alias_Gate10").Any(),
                string.Join(", ", sc.Calls));

        // The dump line as the decompiler writes it, read back by the same class: every argument kind, and the names.
        var dumpArgs = new PexObjectVariableData[] { new() { VariableType = VariableType.Identifier, StringValue = "Disable" },
            new() { VariableType = VariableType.Identifier, StringValue = "::Gate_var" }, new() { VariableType = VariableType.String, StringValue = "a b" },
            new() { VariableType = VariableType.Integer, IntValue = -3 }, new() { VariableType = VariableType.Float, FloatValue = 1.5f },
            new() { VariableType = VariableType.Bool, BoolValue = true }, new() { VariableType = VariableType.Null } };
        var dumpLine = PapyrusDecompiler.RawDump.Line(InstructionOpcode.CALLMETHOD, dumpArgs);
        bool read = PapyrusDecompiler.RawDump.TryRead("        " + dumpLine, out var rop, out var rargs);
        var viaSelf = ScriptFlipCalls.Parse(Fn("    " + PapyrusDecompiler.RawDump.Line(InstructionOpcode.CALLMETHOD,
            new PexObjectVariableData[] { new() { VariableType = VariableType.Identifier, StringValue = "Enable" }, new() { VariableType = VariableType.Identifier, StringValue = "self" } })));
        t.Check("FF-T3 a dump line reads back to its opcode and arguments; ::Gate_var is Gate, self is Self; a plain comment is no dump line",
                read && rop == InstructionOpcode.CALLMETHOD && rargs.SequenceEqual(dumpArgs.Select(PapyrusDecompiler.RawDump.Arg))
                && PapyrusDecompiler.RawDump.Name(rargs[0]) == "Disable" && PapyrusDecompiler.RawDump.Name(rargs[2]) == "a b"
                && PapyrusDecompiler.RawDump.Identifier(rargs[2]) is null && PapyrusDecompiler.RawDump.SourceName(PapyrusDecompiler.RawDump.Identifier(rargs[1])!) == "Gate"
                && ScriptFlipCalls.Parse(Fn("    " + dumpLine)).FlipsOn("Gate").Count() == 1 && viaSelf.FlipsOn("Self").Count() == 1
                && !PapyrusDecompiler.RawDump.TryRead("    ; CALLMETHODS the gate", out _, out _) && !PapyrusDecompiler.RawDump.TryRead("    ; 5 id:Enable", out _, out _),
                $"{dumpLine} -> {read} {rop} [{string.Join(" | ", rargs)}]; self: {string.Join(", ", viaSelf.Calls)}");

        var order = w.Order.Append(raw).ToList();
        var idx = new OverlapIndex();
        for (int p = 0; p < order.Count; p++)
            using (var mod = SkyrimMod.CreateFromBinaryOverlay(order[p], F.Rel)) idx.Ingest(p, mod);
        var sx = SwitchIndex.Build(idx);
        var pluginKey = order.Select(p => Mutagen.Bethesda.Plugins.ModKey.FromFileName(Path.GetFileName(p))).ToArray();
        var questFragmentStage = new Dictionary<(Mutagen.Bethesda.Plugins.FormKey Quest, string Fragment), int>();
        var recordEdid = new Dictionary<Mutagen.Bethesda.Plugins.FormKey, string?>();
        var bindings = new Dictionary<Mutagen.Bethesda.Plugins.FormKey, List<Binding>>();
        TriggerMap.Bindings(order, pluginKey, sx, System.Diagnostics.Stopwatch.StartNew(), new(), questFragmentStage,
                            new(), new(), recordEdid, new(), bindings);
        foreach (var d in new[] { "overwrite", "data" }) Directory.CreateDirectory(Path.Combine(dir, d));
        using var assets = AssetResolver.Build(Path.Combine(dir, "overwrite"), w.ModsDir, Path.Combine(dir, "data"),
                                               new[] { F.RawKey.Name, F.RefKey.Name, F.QuestKey.Name, F.MasterKey.Name }, Array.Empty<ActiveArchive>());
        var scripts = bindings.Values.SelectMany(l => l).Select(b => b.Script).Where(s => s.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var source = new Dictionary<string, ScriptFlipCalls?>(StringComparer.OrdinalIgnoreCase);
        int u = 0, df = 0, dc = 0;
        TriggerMap.Decompile(assets.Capture(), scripts, source, new(), ref u, ref df, ref dc);
        var jointFlips = new Dictionary<string, HashSet<Mutagen.Bethesda.Plugins.FormKey>>(StringComparer.OrdinalIgnoreCase);
        var status = new Dictionary<Mutagen.Bethesda.Plugins.FormKey, (string Status, string Kind, List<string> By)>();
        TriggerMap.Status(sx.Switches.Values.ToList(), bindings, source, questFragmentStage, recordEdid, jointFlips, status);
        string S(Mutagen.Bethesda.Plugins.FormKey k) => status.TryGetValue(k, out var v) ? $"{v.Status} | {v.Kind} | {string.Join(" / ", v.By)}" : "(none)";
        t.Check("FF-S0 the census holds seven switches; M9 and M10 are bound through SwRawQ's forced aliases; one script refused a function",
                sx.Switches.Count == 7 && df == 1 && new[] { F.M9, F.M10 }.All(k => bindings.TryGetValue(k, out var l) && l.Any(b => b.Via == "forced-reference alias")),
                $"{sx.Switches.Count} switches, {df} refused; M9 {(bindings.TryGetValue(F.M9, out var b9) ? string.Join("; ", b9.Select(b => $"{b.Script}.{b.Property}")) : "(none)")}");
        t.Check("FF-S1 M9 is Triggered by quest SwRawQ at stage 10 from the raw bytecode of SwRawScript.Fragment_0",
                status[F.M9].Status == "Triggered" && status[F.M9].Kind == "quest stage"
                && status[F.M9].By.Count == 1 && status[F.M9].By[0].StartsWith("quest SwRawQ stage 10 (SwRawScript.Fragment_0: raw bytecode "), S(F.M9));
        t.Check("FF-S2 M10 stays BoundUnread: EnableLinkChain flips M10's linked chain, not M10",
                status[F.M10].Status == "BoundUnread" && status[F.M10].Kind == "bound, no Enable/Disable call found on the property", S(F.M10));
        t.Check("FF-S3 the joint flips are SwQ:10 -> {M1} and SwRawQ:10 -> {M9}",
                jointFlips.Count == 2 && jointFlips.TryGetValue("SwQ:10", out var j1) && j1.SetEquals(new[] { F.M1 })
                && jointFlips.TryGetValue("SwRawQ:10", out var j2) && j2.SetEquals(new[] { F.M9 }),
                string.Join(", ", jointFlips.Select(kv => $"{kv.Key} {kv.Value.Count}")));
    }

    // The copied-rules task (2026-10-15): the call rules over text (CR-T), then the order with SwCr.esp added (CR-S).
    static void CopiedRules(ProbeSelfTest t, Func<string, string, string> calls)
    {
        string Fn(string body) => "Function F()\n" + body + "\nEndFunction\n";
        t.Check("CR-T1 a whole-line comment '; Gate.Enable()' is not a call on Gate",
                calls(Fn("    ; Gate.Enable()"), "Gate") == "", calls(Fn("    ; Gate.Enable()"), "Gate"));
        // The old Raw rule's own form, which the decompiler never writes (F4): the dump is read as PapyrusDecompiler.RawDump
        // writes it, so this comment is no call (FF-T1..T3 hold the real form).
        var oldForm = ScriptFlipCalls.Parse(Fn("    ;   CallMethod Enable ::Gate_var ::NoneVar 0"));
        t.Check("CR-T2 a comment ';   CallMethod Enable ::Gate_var' (not the decompiler's dump form) is no call",
                oldForm.Calls.Count == 0, string.Join(", ", oldForm.Calls));
        t.Check("CR-T3 Alias_Door.TryToEnable() is a flip on Alias_Door (ReferenceAlias.TryToEnable enables the alias's reference)",
                calls(Fn("    Alias_Door.TryToEnable()"), "Alias_Door") == "F:Alias_Door.TryToEnable(", calls(Fn("    Alias_Door.TryToEnable()"), "Alias_Door"));
        var lc = ScriptFlipCalls.Parse(Fn("    Gate.EnableLinkChain()"));
        t.Check("CR-T4 Gate.EnableLinkChain() is a call on Gate named enablelinkchain, and not a flip",
                lc.CallsOn("Gate").Select(c => c.Call).SequenceEqual(new[] { "enablelinkchain" }) && !lc.FlipsOn("Gate").Any(),
                string.Join(", ", lc.Calls));
        var bareTry = ScriptFlipCalls.Parse(Fn("    TryToEnable()"));
        var bareChain = ScriptFlipCalls.Parse(Fn("    EnableLinkChain()"));
        t.Check("CR-T5 a bare TryToEnable() is a Self flip; a bare EnableLinkChain() is a Self call that sets no SelfCall",
                bareTry.SelfCall && bareTry.FlipsOn("Self").Any() && !bareChain.SelfCall && bareChain.CallsOn("Self").Any() && !bareChain.FlipsOn("Self").Any(),
                $"{string.Join(", ", bareTry.Calls)} / {string.Join(", ", bareChain.Calls)}");

        var w = F.Write(Path.Combine(t.Root, "cr"));
        var cr = F.WriteCopiedRules(Path.Combine(t.Root, "cr"));
        var order = w.Order.Append(cr).ToList();
        var idx = new OverlapIndex();
        for (int p = 0; p < order.Count; p++)
            using (var mod = SkyrimMod.CreateFromBinaryOverlay(order[p], F.Rel)) idx.Ingest(p, mod);
        var sx = SwitchIndex.Build(idx);
        var pluginKey = order.Select(p => Mutagen.Bethesda.Plugins.ModKey.FromFileName(Path.GetFileName(p))).ToArray();
        var recordBindings = new Dictionary<Mutagen.Bethesda.Plugins.FormKey, List<Binding>>();
        var questFragmentStage = new Dictionary<(Mutagen.Bethesda.Plugins.FormKey Quest, string Fragment), int>();
        var recordEdid = new Dictionary<Mutagen.Bethesda.Plugins.FormKey, string?>();
        var bindings = new Dictionary<Mutagen.Bethesda.Plugins.FormKey, List<Binding>>();
        TriggerMap.Bindings(order, pluginKey, sx, System.Diagnostics.Stopwatch.StartNew(), recordBindings, questFragmentStage,
                            new(), new(), recordEdid, new(), bindings);
        t.Check("CR-S0 the census holds eight switches; M6-M8 are bound through SwCrQ's forced aliases",
                sx.Switches.Count == 8 && new[] { F.M6, F.M7, F.M8 }.All(k => bindings.TryGetValue(k, out var l) && l.Any(b => b.Via == "forced-reference alias")),
                $"{sx.Switches.Count} switches; M6 {(bindings.TryGetValue(F.M6, out var b6) ? string.Join("; ", b6.Select(b => $"{b.Script}.{b.Property}")) : "(none)")}");
        foreach (var d in new[] { "overwrite", "data" }) Directory.CreateDirectory(Path.Combine(t.Root, "cr", d));
        using var assets = AssetResolver.Build(Path.Combine(t.Root, "cr", "overwrite"), w.ModsDir, Path.Combine(t.Root, "cr", "data"),
                                               new[] { F.CrKey.Name, F.RefKey.Name, F.QuestKey.Name, F.MasterKey.Name }, Array.Empty<ActiveArchive>());
        var scripts = bindings.Values.SelectMany(l => l).Select(b => b.Script).Where(s => s.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var source = new Dictionary<string, ScriptFlipCalls?>(StringComparer.OrdinalIgnoreCase);
        int u = 0, df = 0, dc = 0;
        TriggerMap.Decompile(assets.Capture(), scripts, source, new(), ref u, ref df, ref dc);
        var jointFlips = new Dictionary<string, HashSet<Mutagen.Bethesda.Plugins.FormKey>>(StringComparer.OrdinalIgnoreCase);
        var status = new Dictionary<Mutagen.Bethesda.Plugins.FormKey, (string Status, string Kind, List<string> By)>();
        TriggerMap.Status(sx.Switches.Values.ToList(), bindings, source, questFragmentStage, recordEdid, jointFlips, status);
        string S(Mutagen.Bethesda.Plugins.FormKey k) => status.TryGetValue(k, out var v) ? $"{v.Status} | {v.Kind} | {string.Join(" / ", v.By)}" : "(none)";
        t.Check("CR-S1 M6 is Triggered by quest SwCrQ at stage 10: Alias_Door.TryToEnable()",
                status[F.M6].Status == "Triggered" && status[F.M6].Kind == "quest stage"
                && status[F.M6].By.SequenceEqual(new[] { "quest SwCrQ stage 10 (SwCrScript.Fragment_0: Alias_Door.TryToEnable()" }), S(F.M6));
        t.Check("CR-S2 M8 is Triggered by quest SwCrQ at stage 10: Alias_Gate8.TryToDisableNoWait()",
                status[F.M8].Status == "Triggered" && status[F.M8].Kind == "quest stage"
                && status[F.M8].By.SequenceEqual(new[] { "quest SwCrQ stage 10 (SwCrScript.Fragment_0: Alias_Gate8.TryToDisableNoWait()" }), S(F.M8));
        t.Check("CR-S3 M7 stays BoundUnread: EnableLinkChain flips M7's linked chain, not M7",
                status[F.M7].Status == "BoundUnread" && status[F.M7].Kind == "bound, no Enable/Disable call found on the property", S(F.M7));
        t.Check("CR-S4 the joint flips are SwQ:10 -> {M1} and SwCrQ:10 -> {M6, M8}",
                jointFlips.Count == 2 && jointFlips.TryGetValue("SwQ:10", out var j1) && j1.SetEquals(new[] { F.M1 })
                && jointFlips.TryGetValue("SwCrQ:10", out var j2) && j2.SetEquals(new[] { F.M6, F.M8 }),
                string.Join(", ", jointFlips.Select(kv => $"{kv.Key} {kv.Value.Count}")));
    }
}
