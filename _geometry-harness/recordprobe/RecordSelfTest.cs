using System.Reflection;
using System.Text.Json;
using HarnessShared;
using SkyWright.Engine;

/// <summary>recordprobe --self-test: the dangling check's known answers, each one assertion. KD1 is houseCARL's real answer
/// on the lab's game files alone (Bethesda's five masters, four Creation Club files, _ResourcePack.esl and USSEP, swept in
/// process at pin 0ddd0592, record checks Gate A); the rest are answers in the same shape, written here, so each rule clause
/// has a case that only it decides. A failure prints "FAIL  name  (rejects: what was wrong)"; a crash prints "FAIL  name — ".</summary>
static class RecordSelfTest
{
    const string Mods = @"X:\inst\mods";
    static string InMod(string folder, string plugin) => Path.Combine(Mods, folder, plugin);
    static string InData(string plugin) => Path.Combine(@"X:\game\Data", plugin);

    /// <summary>An order: (plugin, path, selected).</summary>
    static List<OrderPlugin> Order(params (string Name, string Path, bool Selected)[] ps) =>
        ps.Select(p => new OrderPlugin(p.Name, RecordClashRule.ModOf(p.Path, Mods), RecordChecks.IsGameFile(p.Name), p.Selected)).ToList();

    /// <summary>An errors-sweep answer with one plugin section per holder, each listing its dangling links (source, target).</summary>
    static string Answer(params (string Holder, (string Source, string Target)[] Links)[] sections)
    {
        var plugins = sections.Select(s => new Dictionary<string, object?>
        {
            ["plugin"] = s.Holder, ["scan_error"] = null, ["unscannable_records"] = 0,
            ["dangling"] = s.Links.Select(l => new Dictionary<string, object?> { ["source"] = l.Source, ["source_type"] = "Npc", ["source_editorid"] = "SelfTestNpc", ["target"] = l.Target, ["path"] = "Race" }).ToList(),
        }).ToList();
        return JsonSerializer.Serialize(new Dictionary<string, object?>
        {
            ["families"] = new Dictionary<string, object?>
            {
                ["errors"] = new Dictionary<string, object?>
                {
                    // every plugin of the largest order here (three) swept; KD14 and KD15 say otherwise themselves
                    ["scanned_plugins"] = 3, ["dangling"] = sections.Sum(s => s.Links.Length),
                    ["accounting"] = new Dictionary<string, object?> { ["dangling_missing"] = 0 }, ["plugins"] = plugins,
                },
            },
        });
    }

    static string Counts(CheckResult r) =>
        $"{r.Rows.Count} row(s); no row: {string.Join(", ", (r.Coverage[0].SetAside ?? new Dictionary<string, long>()).Select(kv => $"{kv.Key} {kv.Value}"))}; not judged: {string.Join(", ", (r.Coverage[0].Unjudgeable ?? new Dictionary<string, long>()).Select(kv => $"{kv.Key} {kv.Value}"))}";

    /// <summary>The result has exactly these rows (A, B) and these no-row reasons, and nothing else.</summary>
    static string? Expect(CheckResult r, (string A, string B)[] rows, params (string Why, long N)[] noRow)
    {
        var got = r.Rows.Select(c => (c.A, c.B)).ToList();
        var aside = r.Coverage[0].SetAside ?? new Dictionary<string, long>();
        bool ok = got.SequenceEqual(rows) && aside.Count == noRow.Length && noRow.All(x => aside.GetValueOrDefault(x.Why) == x.N)
                  && r.Coverage[0].Judged == rows.Length + noRow.Sum(x => x.N);
        return ok ? null : $"expected {rows.Length} row(s) [{string.Join("; ", rows)}] and no row: {string.Join(", ", noRow.Select(x => $"{x.Why} {x.N}"))}; got {Counts(r)} [{string.Join("; ", got)}]";
    }

    /// <summary>One sentence: one end of sentence, at the end (a dot inside a file name such as "Base.esp" is not one).</summary>
    static bool OneSentence(string s) => s.EndsWith('.') && System.Text.RegularExpressions.Regex.Matches(s, @"[.!?](\s|$)").Count == 1;

    static string GameFilesAnswer()
    {
        using var s = Assembly.GetExecutingAssembly().GetManifestResourceStream("game-files-errors.json")!;
        return new StreamReader(s).ReadToEnd();
    }

    static readonly string[] GameFiles = { "Skyrim.esm", "Update.esm", "Dawnguard.esm", "HearthFires.esm", "Dragonborn.esm", "ccbgssse001-fish.esm",
        "ccqdrsse001-survivalmode.esl", "ccbgssse037-curios.esl", "ccbgssse025-advdsgs.esm", "_ResourcePack.esl" };

    // the cross-mod case: a patch in its own mod uses a record its target mod's installed plugin no longer has
    static List<OrderPlugin> CrossOrder(bool patchSelected, bool baseSelected) => Order(("Skyrim.esm", InData("Skyrim.esm"), false),
        ("Base.esp", InMod("Base Mod", "Base.esp"), baseSelected), ("Patch.esp", InMod("Patch Mod", "Patch.esp"), patchSelected));
    static string CrossAnswer() => Answer(("Patch.esp", new[] { ("000D62:Patch.esp", "000801:Base.esp") }));

    public static readonly (string Name, Func<string?> Check)[] Cases =
    {
        ("KD1 the game's own files: a Bethesda, Creation Club and USSEP order gives 0 rows, its 398 links counted under the game's own files", () =>
            Expect(DanglingCheck.Judge(GameFilesAnswer(), Order(GameFiles.Select(g => (g, InData(g), true)).Append(("Unofficial Skyrim Special Edition Patch.esp",
                InMod("R2 SUP - Dependencies - USSEP", "Unofficial Skyrim Special Edition Patch.esp"), true)).ToArray())), [], (RecordClashRule.InGameFiles, 398))),
        ("KD2 finding 1: a patch outside the selection using a record the selected mod's plugin no longer has is exactly 1 row", () =>
            Expect(DanglingCheck.Judge(CrossAnswer(), CrossOrder(patchSelected: false, baseSelected: true)), [("Patch.esp", "Base.esp")])),
        ("KD3 the same link with the patch selected and its target mod not is exactly 1 row", () =>
            Expect(DanglingCheck.Judge(CrossAnswer(), CrossOrder(patchSelected: true, baseSelected: false)), [("Patch.esp", "Base.esp")])),
        ("KD4 a link into its own plugin is 0 rows, counted as one plugin's own defect", () =>
            Expect(DanglingCheck.Judge(Answer(("Base.esp", new[] { ("000D62:Base.esp", "000801:Base.esp") })), CrossOrder(true, true)), [], (RecordClashRule.SamePlugin, 1))),
        ("KD5 a link into another plugin of the same mod folder is 0 rows, counted as one mod's own defect", () =>
            Expect(DanglingCheck.Judge(Answer(("Base - Extra.esp", new[] { ("000D62:Base - Extra.esp", "000801:Base.esp") })),
                Order(("Base.esp", InMod("Base Mod", "Base.esp"), true), ("Base - Extra.esp", InMod("Base Mod", "Base - Extra.esp"), true))), [], (RecordClashRule.SameMod, 1))),
        ("KD6 a mod's link into the game's own files is 0 rows, counted as one mod against the game's version", () =>
            Expect(DanglingCheck.Judge(Answer(("Patch.esp", new[] { ("000D62:Patch.esp", "0136DB:Skyrim.esm") })), CrossOrder(true, true)), [], (RecordClashRule.IntoGameFiles, 1))),
        ("KD7 a patch made with its target mod whose links all resolve gives 0 rows and judges nothing (the sweep lists no link; this case cannot fail by the rule)", () =>
            Expect(DanglingCheck.Judge(Answer(("Patch.esp", Array.Empty<(string, string)>())), CrossOrder(true, true)), [])),
        ("KD8 a link with neither plugin in the selection is 0 rows, counted as outside the selection", () =>
            Expect(DanglingCheck.Judge(CrossAnswer(), CrossOrder(false, false)), [], (RecordClashRule.NotSelected, 1))),
        ("KD9 a link into a plugin that is not in the load order is 0 rows, left to the masters check", () =>
            Expect(DanglingCheck.Judge(Answer(("Patch.esp", new[] { ("000D62:Patch.esp", "000801:Gone.esp") })), CrossOrder(true, true)), [], (RecordClashRule.NotLoaded, 1))),
        ("KD10 finding 2: a plugin in the game's Data folder or MO2's overwrite is a mod of its own, so a link between two of them is 1 row", () =>
        {
            string loose = RecordClashRule.ModOf(InData("Loose.esp"), Mods), over = RecordClashRule.ModOf(@"X:\inst\overwrite\Other.esp", Mods),
                   folder = RecordClashRule.ModOf(InMod("Base Mod", "Base.esp"), Mods);
            if (loose != "plugin:Loose.esp" || over != "plugin:Other.esp" || folder != "folder:Base Mod")
                return $"the mods are '{loose}', '{over}', '{folder}', not plugin:Loose.esp, plugin:Other.esp, folder:Base Mod";
            return Expect(DanglingCheck.Judge(Answer(("Other.esp", new[] { ("000D62:Other.esp", "000801:Loose.esp") })),
                Order(("Loose.esp", InData("Loose.esp"), true), ("Other.esp", @"X:\inst\overwrite\Other.esp", true))), [("Other.esp", "Loose.esp")]);
        }),
        ("KD11 records houseCARL could not read and a plugin it could not scan are not judged, with their reasons", () =>
        {
            var answer = Answer(("Patch.esp", Array.Empty<(string, string)>())).Replace("\"unscannable_records\":0", "\"unscannable_records\":3").Replace("\"scan_error\":null", "\"scan_error\":\"bad header\"");
            var un = DanglingCheck.Judge(answer, CrossOrder(true, true)).Coverage[0].Unjudgeable ?? new Dictionary<string, long>();
            return un.GetValueOrDefault(DanglingCheck.Unscannable) == 3 && un.GetValueOrDefault(DanglingCheck.ScanFailed) == 1 && un.Count == 2 ? null
                : $"not judged: {string.Join(", ", un.Select(kv => $"{kv.Key} {kv.Value}"))}";
        }),
        ("KD12 a listing houseCARL cut short refuses in one sentence instead of giving a clean result", () =>
        {
            var answer = CrossAnswer().Replace("\"dangling_missing\":0", "\"dangling_missing\":2");
            try { DanglingCheck.Judge(answer, CrossOrder(true, true)); return "a cut listing gave a result"; }
            catch (CheckRefused ex) { return OneSentence(ex.Message) ? null : "not one sentence: " + ex.Message; }
        }),
        ("KD14 review U1-F1: a plugin of the order houseCARL could not parse is not judged, with its reason, not counted as swept", () =>
        {
            var answer = Answer(("Patch.esp", Array.Empty<(string, string)>())).Replace("\"scanned_plugins\":3", "\"scanned_plugins\":1").Replace("\"families\":", "\"excluded_plugins\":[{\"plugin\":\"Base.esp\",\"reason\":\"bad record header\"}],\"families\":");
            var un = DanglingCheck.Judge(answer, Order(("Base.esp", InMod("Base Mod", "Base.esp"), true), ("Patch.esp", InMod("Patch Mod", "Patch.esp"), true))).Coverage[0].Unjudgeable
                     ?? new Dictionary<string, long>();
            return un.GetValueOrDefault(DanglingCheck.Unparsed) == 1 && un.Count == 1 ? null : $"not judged: {string.Join(", ", un.Select(kv => $"{kv.Key} {kv.Value}"))}";
        }),
        ("KD15 review U1-F1: a sweep that covered fewer of the order's plugins than it names refuses in one sentence", () =>
        {
            try
            {
                DanglingCheck.Judge(Answer(("Patch.esp", Array.Empty<(string, string)>())).Replace("\"scanned_plugins\":3", "\"scanned_plugins\":1"), Order(("Base.esp", InMod("Base Mod", "Base.esp"), true), ("Patch.esp", InMod("Patch Mod", "Patch.esp"), true)));
                return "a sweep of 1 of 2 plugins, the other unexplained, gave a result";
            }
            catch (CheckRefused ex) { return OneSentence(ex.Message) ? null : "not one sentence: " + ex.Message; }
        }),
        ("KD13 the row says it in a modder's words: the plugins and their mods, no internal terms, one sentence", () =>
        {
            var r = DanglingCheck.Judge(CrossAnswer(), CrossOrder(true, true));
            if (r.Rows.Count != 1) return $"{r.Rows.Count} rows";
            var s = r.Rows[0].Summary;
            string[] banned = { "holder", "origin", "FormKey", "formid", "dangling", "000801", "selection" };
            if (banned.FirstOrDefault(b => s.Contains(b, StringComparison.OrdinalIgnoreCase)) is { } bad) return $"'{bad}' in: {s}";
            if (!s.Contains("Patch.esp (mod Patch Mod)") || !s.Contains("Base.esp (mod Base Mod)")) return "the plugins and their mods are not named: " + s;
            return OneSentence(s) ? null : "not one sentence: " + s;
        }),
    };

    public static int Run()
    {
        int failed = 0;
        var all = Cases.Concat(LostEditSelfTest.Cases).Concat(MissingAssetSelfTest.Cases).ToList();
        foreach (var (name, check) in all)
        {
            string? why;
            try { why = check(); }
            catch (Exception ex) { failed++; Console.WriteLine($"FAIL  {name} — {ex.GetType().Name}: {ex.Message}"); continue; }
            if (why is null) Console.WriteLine($"PASS  {name}");
            else { failed++; Console.WriteLine($"FAIL  {name}  (rejects: {why})"); }
        }
        Console.WriteLine($"recordprobe self-test: {all.Count - failed} passed, {failed} failed");
        return failed == 0 ? 0 : 1;
    }

    /// <summary>KD3's emit, LE3's, LE25's and MA3's, for runcontrol's self-test.</summary>
    public static int WriteEmit(string dir)
    {
        Directory.CreateDirectory(dir);
        var r = DanglingCheck.Judge(CrossAnswer(), CrossOrder(true, true));
        var path = Path.Combine(dir, "dangling.json");
        ConflictIo.Write(path, new ConflictFile("recordprobe", @"X:\inst", DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm:ssZ"), r.Rows, r.Coverage, null, null, "load-time", "baseline"));
        Console.WriteLine($"wrote {r.Rows.Count} row(s) -> {path}");
        // LE3's emit: one lost edit between two mods (runcontrol's SelfTestLostEdits)
        var le = LostEditSelfTest.CrossModEmit();
        var lePath = Path.Combine(dir, "lostedit.json");
        ConflictIo.Write(lePath, new ConflictFile("recordprobe", @"X:\inst", DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm:ssZ"), le.Rows, le.Coverage, null, null, "load-time", "baseline"));
        Console.WriteLine($"wrote {le.Rows.Count} row(s) -> {lePath}");
        // LE25's emit: the setup without the selected mods (runcontrol's SelfTestLostEdits: it is accepted, not refused)
        var wo = LostEditSelfTest.WithoutSelectionEmit();
        var woPath = Path.Combine(dir, "lostedit-without.json");
        ConflictIo.Write(woPath, new ConflictFile("recordprobe", @"X:\inst", DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm:ssZ"), wo.Rows, wo.Coverage, null, null, "load-time", "baseline"));
        Console.WriteLine($"wrote {wo.Rows.Count} row(s) -> {woPath}");
        // MA3's emit: one missing file between two mods (runcontrol's SelfTestMissingAssets)
        var ma = MissingAssetSelfTest.CrossModEmit();
        var maPath = Path.Combine(dir, "missingasset.json");
        ConflictIo.Write(maPath, new ConflictFile("recordprobe", @"X:\inst", DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm:ssZ"), ma.Rows, ma.Coverage, null, null, "load-time", "baseline"));
        Console.WriteLine($"wrote {ma.Rows.Count} row(s) -> {maPath}");
        return 0;
    }
}
