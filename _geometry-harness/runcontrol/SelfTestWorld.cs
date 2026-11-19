using System.Text;
using HarnessShared;
using HousecarlCore;

namespace RunControl;

// Cases over small synthetic MO2 instances built in a temporary folder: the in-place cases of
// tests/test_workbench_manual_pipeline.py, the lab cases 52 to 56 of tests/test_evalpipeline.py, preparation reuse,
// and one case per place where this controller follows the probes' own reader and Python's copy answers otherwise.
public static partial class SelfTest
{
    // ---- building an instance

    // A header-only plugin: HEDR with a record count, then one MAST (with its DATA) per master.
    static byte[] PluginBytes(IEnumerable<byte[]> masters, uint flags = 0, uint records = 0, byte[]? tail = null)
    {
        using var body = new MemoryStream();
        void Sub(string sig, byte[] data, int? length = null)
        {
            body.Write(Encoding.ASCII.GetBytes(sig));
            body.Write(BitConverter.GetBytes((ushort)(length ?? data.Length)));
            body.Write(data);
        }
        var hedr = new byte[12];
        BitConverter.GetBytes(1.71f).CopyTo(hedr, 0);
        BitConverter.GetBytes(records).CopyTo(hedr, 4);
        BitConverter.GetBytes(0x800u).CopyTo(hedr, 8);
        Sub("HEDR", hedr);
        foreach (byte[] m in masters)
        {
            Sub("MAST", m);
            Sub("DATA", new byte[8]);
        }
        if (tail != null) body.Write(tail);
        using var file = new MemoryStream();
        file.Write("TES4"u8);
        file.Write(BitConverter.GetBytes((uint)body.Length));
        file.Write(BitConverter.GetBytes(flags));
        file.Write(new byte[12]);
        body.WriteTo(file);
        return file.ToArray();
    }

    static void WritePlugin(string path, IEnumerable<string>? masters = null, bool esm = false, uint records = 0)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, PluginBytes((masters ?? []).Select(m => Encoding.Latin1.GetBytes(m + "\0")), esm ? 1u : 0u, records));
    }

    sealed record ModFolder(string Name, params (string plugin, string[] masters)[] Files);

    static ModFolder Mod(string name, params string[] plugins) =>
        new(name, plugins.Select(p => (p, new[] { "Base.esm" })).ToArray());

    // An MO2 instance at t/instance with the game at t/game: the mod folders; the enabled ones strongest first;
    // the plugins in load order, all active; the game's own plugins. Returns (instance, game).
    static (string inst, string game) World(Temp t, ModFolder[] mods, string[] enabled, string[] plugins, string[]? data = null)
    {
        data ??= ["Base.esm"];
        string inst = t.At("instance"), game = t.At("game");
        foreach (string p in data) WritePlugin(PyPath.Join(game, "Data", p), esm: true);
        foreach (var m in mods)
        {
            Directory.CreateDirectory(PyPath.Join(inst, "mods", m.Name));
            foreach (var (plugin, masters) in m.Files)
                WritePlugin(PyPath.Join(inst, "mods", m.Name, plugin), masters, esm: plugin.EndsWith(".esm", StringComparison.OrdinalIgnoreCase));
        }
        string prof = PyPath.Join(inst, "profiles", "P");
        Directory.CreateDirectory(prof);
        File.WriteAllText(PyPath.Join(prof, "modlist.txt"),
            string.Concat(enabled.Select(m => $"+{m}\r\n")) + string.Concat(mods.Where(m => !enabled.Contains(m.Name)).Select(m => $"-{m.Name}\r\n")));
        File.WriteAllText(PyPath.Join(prof, "loadorder.txt"), string.Concat(data.Concat(plugins).Select(p => p + "\r\n")));
        File.WriteAllText(PyPath.Join(prof, "plugins.txt"), string.Concat(data.Concat(plugins).Select(p => "*" + p + "\r\n")));
        Directory.CreateDirectory(PyPath.Join(inst, "overwrite"));
        return (inst, game);
    }

    static PyDict InPlaceEntry(string inst, string game, string[] mods, string[] select) => new()
    {
        ["schema"] = 1L, ["id"] = "wb-test", ["inPlace"] = new PyDict { ["instance"] = inst, ["profile"] = "P", ["game"] = game },
        ["selection"] = new PyDict { ["mods"] = Py.Objs(mods) }, ["evaluation"] = new PyDict { ["select"] = Py.Objs(select) },
    };

    static RunContext InPlace(Temp t, string inst, string game, params string[] mods) =>
        new(InPlaceEntry(inst, game, mods, ["geometry.object-overlap"]), new PyDict(), t.At("out"));

    static readonly ModFolder[] ShadowMods = [Mod("Sel", "Sel.esp", "Dup.esp", "Off.esp"), Mod("Other", "Dup.esp"), Mod("A", "A.esp")];
    static readonly string[] ShadowEnabled = ["Sel", "Other", "A"];
    static readonly string[] ShadowPlugins = ["A.esp", "Sel.esp", "Dup.esp"];

    // Every file and folder under the roots with its size and last write, to show nothing changed.
    static string Listing(params string[] roots)
    {
        var rows = new List<string>();
        foreach (string root in roots)
            foreach (string p in PyOrder.SortedPaths(PyPath.RGlob(root)))
                rows.Add(File.Exists(p) ? $"{p} {new FileInfo(p).Length} {Hashing.MtimeNs(p)}" : $"{p} dir");
        return string.Join("\n", rows);
    }

    static Dictionary<string, PyDict> ArmsById(PyDict res) => L(res["arms"]).Select(D).ToDictionary(a => S(a["id"]), a => a);

    static string Plugins(PyDict arm) => string.Join(" ", Strings(arm["plugins"]));

    // ---- test_workbench_manual_pipeline.py: the arms of an in-place entry

    static void InPlaceCases()
    {
        Case("case b5 in-place arms are the profile and the profile without the selected folders", () =>
        {
            using var t = new Temp();
            var (inst, game) = World(t, ShadowMods, ShadowEnabled, ShadowPlugins);
            var ctx = InPlace(t, inst, game, "Sel");
            var res = ctx.Resolve();
            var by = ArmsById(res);
            if (by.Count != 2 || !by.ContainsKey("combined") || !by.ContainsKey("without-selection")) return $"arms {string.Join(", ", by.Keys)}";
            // Dup.esp is also provided by the unselected folder Other: MO2 with Sel disabled still loads it, from Other
            string? why = Same("combined", Plugins(by["combined"]), "Base.esm A.esp Sel.esp Dup.esp")
                          ?? Same("without-selection", Plugins(by["without-selection"]), "Base.esm A.esp Dup.esp");
            if (why != null) return why;
            var withoutMods = Strings(by["without-selection"]["mods"]);
            if (withoutMods.Contains("Sel") || !withoutMods.Contains("Other")) return $"without-selection mods {string.Join(", ", withoutMods)}";
            var cands = Candidates.Files(ctx, res);
            if (cands[0].name != "candidate-00-baseline.json" || cands[0].arm["id"] as string != "without-selection")
                return "the baseline candidate must be the profile without the selection";
            if (cands[1].name != "candidate-01.json" || cands[1].arm["id"] as string != "combined") return "candidate-01 must be the combined arm";
            return cands.All(c => c.doc["BaseProfile"] as string == "P") ? null : "candidates must name the chosen profile";
        });
        Case("case d1 a selected disabled mod is enabled in the candidate only, with its plugins last", () =>
        {
            using var t = new Temp();
            ModFolder[] mods = [Mod("Top", "Top.esp"), Mod("Off", "Off.esp", "OffLib.esm"), Mod("Low", "Low.esp")];
            var (inst, game) = World(t, mods, ["Top", "Low"], ["Low.esp", "Top.esp"]);
            string prof = PyPath.Join(inst, "profiles", "P");
            // MO2's own priority: Top strongest, then the disabled Off, then Low
            File.WriteAllText(PyPath.Join(prof, "modlist.txt"), "+Top\n-Off\n+Low\n");
            string before = Listing(prof);
            var res = InPlace(t, inst, game, "Off").Resolve();
            var by = ArmsById(res);
            return Same("combined mods", string.Join(" ", Strings(by["combined"]["mods"])), "Top Off Low")
                   ?? Same("combined plugins", Plugins(by["combined"]), "Base.esm OffLib.esm Low.esp Top.esp Off.esp")
                   ?? Same("switchedOn", string.Join(" ", Strings(by["combined"]["switchedOn"])), "Off")
                   ?? (Strings(by["without-selection"]["mods"]).Contains("Off") ? "the without-selection arm holds Off" : null)
                   ?? Same("without-selection plugins", Plugins(by["without-selection"]), "Base.esm Low.esp Top.esp")
                   ?? Same("subject", string.Join(" ", PyOrder.SortedByCodePoint(Strings(res["subject"]))), "Off.esp OffLib.esm")
                   ?? (Listing(prof) == before ? null : "the profile's files changed");
        });
        Case("case c8 a plugin plugins.txt does not list is force-loaded and an unchecked one is not", () =>
        {
            using var t = new Temp();
            var (inst, game) = World(t, ShadowMods, ShadowEnabled, ShadowPlugins, ["Base.esm", "ccFish.esm", "Update.esm"]);
            // A.esp is listed both ticked and unticked, Update.esm (an official master) unticked: the profile reader drops both
            File.WriteAllText(PyPath.Join(inst, "profiles", "P", "plugins.txt"), "*Base.esm\n*A.esp\nA.esp\n*Sel.esp\nDup.esp\nUpdate.esm\n");
            var by = ArmsById(InPlace(t, inst, game, "Sel").Resolve());
            var combined = Strings(by["combined"]["plugins"]);
            if (!combined.Contains("ccFish.esm") || !Strings(by["without-selection"]["plugins"]).Contains("ccFish.esm")) return $"ccFish.esm is not loaded: {Plugins(by["combined"])}";
            if (combined.Contains("Dup.esp")) return "an unchecked plugin was loaded";
            if (combined.Contains("A.esp")) return "a plugin listed both ticked and unticked was loaded";
            return combined.Contains("Update.esm") ? "an unticked official master was loaded" : null;
        });
        Case("case b4a prepare writes no profile into the instance", () =>
        {
            using var t = new Temp();
            var (inst, game) = World(t, ShadowMods, ShadowEnabled, ShadowPlugins);
            var ctx = InPlace(t, inst, game, "Sel");
            string before = Listing(inst, game);
            var res = ctx.Resolve();
            Directory.CreateDirectory(t.At("out/prepare"));
            StageBodies.Prepare(ctx, res, t.At("out/prepare"));
            return Listing(inst, game) == before ? null : "prepare wrote into the instance or game";
        });
        Case("case l4 an inactive plugin stays out and a plugin the game Data also provides stays in", () =>
        {
            using var t = new Temp();
            ModFolder[] mods = [Mod("Sel", "Sel.esp", "Off.esp", "Shared.esp"), Mod("A", "A.esp")];
            var (inst, game) = World(t, mods, ["Sel", "A"], ["A.esp", "Sel.esp", "Shared.esp"], ["Base.esm", "Shared.esp"]);
            // Off.esp is in loadorder.txt and unticked in plugins.txt
            File.AppendAllText(PyPath.Join(inst, "profiles", "P", "loadorder.txt"), "Off.esp\n");
            File.AppendAllText(PyPath.Join(inst, "profiles", "P", "plugins.txt"), "Off.esp\n");
            var by = ArmsById(InPlace(t, inst, game, "Sel").Resolve());
            if (Strings(by["combined"]["plugins"]).Contains("Off.esp")) return "an unticked plugin must not load";
            return Strings(by["without-selection"]["plugins"]).Contains("Shared.esp") ? null : "a plugin Data also provides stays loaded with Sel disabled";
        });
        Case("case c7 a missing loadorder or plugins file is refused, not read as an empty order", () =>
            Rows(new[] { "loadorder.txt", "plugins.txt" }, missing =>
            {
                using var t = new Temp();
                var (inst, game) = World(t, ShadowMods, ShadowEnabled, ShadowPlugins);
                File.Delete(PyPath.Join(inst, "profiles", "P", missing));
                return Refuses(() => InPlace(t, inst, game, "Sel").Resolve(), missing);
            }));
        Case("case b6b an asset-only selection has no target", () =>
        {
            using var t = new Temp();
            ModFolder[] mods = [new("Tex"), Mod("A", "A.esp")];
            var (inst, game) = World(t, mods, ["Tex", "A"], ["A.esp"]);
            var res = InPlace(t, inst, game, "Tex").Resolve();
            var by = ArmsById(res);
            if (L(res["subject"]).Count != 0 || L(res["keys"]).Count != 0) return $"targets {PyJson.Dumps(res["subject"])} {PyJson.Dumps(res["keys"])}";
            // the arms differ only by the folder
            var onlyCombined = Strings(by["combined"]["mods"]).Except(Strings(by["without-selection"]["mods"])).ToList();
            return Same("mods only the combined arm has", string.Join(" ", onlyCombined), "Tex")
                   ?? Same("plugins", Plugins(by["combined"]), Plugins(by["without-selection"]));
        });
    }

    // ---- test_evalpipeline.py, cases 52 to 56: a lab entry over a small synthetic lab

    sealed record LabFile(string? Mod, string Plugin, string[] Masters, bool Esm = false);

    // A lab at t/lab with the game at t/game and a source profile at t/src; a file with no mod is in the game's Data.
    static RunContext Lab(Temp t, LabFile[] files, string[] source, string entry)
    {
        foreach (var f in files)
            WritePlugin(f.Mod == null ? PyPath.Join(t.At("game"), "Data", f.Plugin) : PyPath.Join(t.At("lab"), "mods", f.Mod, f.Plugin), f.Masters, f.Esm);
        Directory.CreateDirectory(t.At("lab/mods"));
        Directory.CreateDirectory(t.At("src/profiles/P"));
        File.WriteAllText(t.At("src/profiles/P/loadorder.txt"), string.Concat(source.Select(p => p + "\n")));
        var doc = Json(entry);
        doc["schema"] = 1L;
        doc["id"] = "t";
        doc["source"] = new PyDict { ["instance"] = t.At("src"), ["profile"] = "P" };
        doc["lab"] = new PyDict { ["instance"] = t.At("lab"), ["game"] = t.At("game") };
        doc["extraArms"] = new List<object?>();
        doc["orderVariants"] = new List<object?>();
        return new RunContext(doc, new PyDict(), t.At("out"));
    }

    static readonly LabFile[] MixedFiles =
    [
        new(null, "Base.esm", [], true), new(null, "Dlc.esm", ["Base.esm"], true), new("MFix", "Fix.esp", ["Base.esm", "Dlc.esm"]),
        // two partner headers name the same baseline plugins in other spellings
        new("MA", "PA.esp", ["base.esm", "DLC.ESM", "fix.esp"]), new("MB", "PB.esp", ["BASE.ESM", "FIX.ESP"]),
        new("MS", "Subj.esp", ["Base.esm", "Fix.esp"]), new("MK", "KA.esp", ["Base.esm", "Subj.esp", "pa.esp"]),
    ];
    const string MixedEntry = """
        {"subject": {"plugin": "Subj.esp", "mod": "MS"},
         "partners": [{"plugin": "PA.esp", "mod": "MA"}, {"plugin": "PB.esp", "mod": "MB"}],
         "keys": [{"plugin": "KA.esp", "mod": "MK"}],
         "dependencies": [{"plugin": "Fix.esp", "mod": "MFix"}], "baseline": ["Base.esm", "Dlc.esm", "Fix.esp"]}
        """;
    static readonly LabFile[] PlacedFiles =
    [
        new(null, "Base.esm", [], true), new("MS", "Subj.esp", ["Base.esm"]), new("MA", "PA.esp", ["Base.esm"]),
        new("MB", "PB.esp", ["Base.esm"]), new("ML", "Lab.esp", ["Base.esm", "PB.esp"]),
    ];
    static readonly string[] PlacedSource = ["Base.esm", "PA.esp", "PB.esp", "Subj.esp"];

    static string PlacedEntry(string places) =>
        "{\"subject\": {\"plugin\": \"Subj.esp\", \"mod\": \"MS\"}, \"partners\": [{\"plugin\": \"PA.esp\", \"mod\": \"MA\"}, {\"plugin\": \"Lab.esp\", \"mod\": \"ML\"}], "
        + "\"keys\": [], \"dependencies\": [{\"plugin\": \"PB.esp\", \"mod\": \"MB\"}], \"baseline\": [\"Base.esm\", \"PB.esp\"], \"places\": " + places + "}";

    static void LabCases()
    {
        Case("case 52 a master named in another case is one plugin", () =>
        {
            using var t = new Temp();
            var res = Lab(t, MixedFiles, ["Base.esm", "Dlc.esm", "Fix.esp", "PA.esp", "PB.esp", "Subj.esp"], MixedEntry).Resolve();
            var baseline = Strings(res["baseline"]);
            if (baseline.Count != 3 || baseline.Select(PyText.Lower).Distinct().Count() != 3) return $"the baseline names each plugin once, got {string.Join(", ", baseline)}";
            var headers = D(res["headers"]);
            var want = PyOrder.SortedByCodePoint(MixedFiles.Select(f => f.Plugin));
            if (!PyOrder.SortedByCodePoint(headers.Keys).SequenceEqual(want)) return $"each plugin's header is read once, under one spelling, got {string.Join(", ", headers.Keys)}";
            var units = new Dictionary<string, string>();
            foreach (var arm in L(res["arms"]).Select(D))
            {
                var plugins = Strings(arm["plugins"]);
                if (plugins.Select(PyText.Lower).Distinct().Count() != plugins.Count) return $"arm {arm["id"]} loads a plugin twice: {Plugins(arm)}";
                if (!plugins.All(headers.ContainsKey)) return $"arm {arm["id"]} names a plugin in another spelling: {Plugins(arm)}";
                units[S(arm["id"])] = string.Join(" ", Strings(arm["evaluated"]));
            }
            if (units["unit:0"] != "PA.esp" || units["unit:1"] != "PB.esp") return "a partner unit carries a baseline plugin under another spelling";
            return units.ContainsKey("pair:0+key:KA.esp") ? null : $"the key's lower-case master still names partner PA, got {string.Join(", ", units.Keys)}";
        });
        Case("case 53 a plugin the source does not load takes its named place", () =>
        {
            using (var t = new Temp())
            {
                var res = Lab(t, PlacedFiles, PlacedSource, PlacedEntry("{\"lab.esp\": {\"after\": \"PB.esp\"}}")).Resolve();
                string? why = Same("combined", Plugins(ArmsById(res)["combined"]), "Base.esm PA.esp PB.esp Lab.esp Subj.esp");
                if (why != null) return why;
            }
            using var t2 = new Temp();
            return Refuses(() => Lab(t2, PlacedFiles, PlacedSource, PlacedEntry("{}")).Resolve(), "Lab.esp");
        });
        Case("case 54 a place before the plugin's own master is refused", () =>
        {
            using var t = new Temp();
            return Refuses(() => Lab(t, PlacedFiles, PlacedSource, PlacedEntry("{\"Lab.esp\": {\"after\": \"PA.esp\"}}")).Resolve(), "its master PB.esp");
        });
        Case("case 55 a place after a plugin the source does not load is refused", () =>
        {
            using var t = new Temp();
            return Refuses(() => Lab(t, PlacedFiles, PlacedSource, PlacedEntry("{\"Lab.esp\": {\"after\": \"Gone.esp\"}}")).Resolve(), "Gone.esp");
        });
        Case("case 56 a place for a plugin the source loads is refused", () =>
        {
            using var t = new Temp();
            return Refuses(() => Lab(t, PlacedFiles, PlacedSource, PlacedEntry("{\"Lab.esp\": {\"after\": \"PB.esp\"}, \"PA.esp\": {\"after\": \"PB.esp\"}}")).Resolve(),
                           "PA.esp is in the source profile's load order already");
        });
        Case("lab: prepare writes the base profile once and refuses another one under its name", () =>
        {
            using var t = new Temp();
            var ctx = Lab(t, PlacedFiles, PlacedSource, PlacedEntry("{\"Lab.esp\": {\"after\": \"PB.esp\"}}"));
            var res = ctx.Resolve();
            Directory.CreateDirectory(t.At("out/prepare"));
            StageBodies.Prepare(ctx, res, t.At("out/prepare"));
            string prof = t.At("lab/profiles/EP-t");
            string? why = Same("plugins.txt", File.ReadAllText(PyPath.Join(prof, "plugins.txt")), "*Base.esm\n*PA.esp\n*PB.esp\n*Lab.esp\n*Subj.esp\n")
                          ?? Same("loadorder.txt", File.ReadAllText(PyPath.Join(prof, "loadorder.txt")), "Base.esm\nPA.esp\nPB.esp\nLab.esp\nSubj.esp\n")
                          ?? Same("modlist.txt", File.ReadAllText(PyPath.Join(prof, "modlist.txt")), "# evalpipeline base profile; strongest first\n+MS\n+ML\n+MB\n+MA\n")
                          ?? Same("settings.ini", File.ReadAllText(PyPath.Join(prof, "settings.ini")), "[General]\r\nLocalSettings=true\r\n");
            if (why != null) return why;
            StageBodies.Prepare(ctx, res, t.At("out/prepare"));   // the same files again: accepted
            File.AppendAllText(PyPath.Join(prof, "plugins.txt"), "*Other.esp\n");
            return Refuses(() => StageBodies.Prepare(ctx, res, t.At("out/prepare")), "exists with other contents");
        });
    }

    // ---- case r1: an earlier preparation is reused only when every input matches

    static void PrepReuseCases()
    {
        Case("case r1 an earlier preparation is reused only when every input matches", () =>
        {
            using var t = new Temp();
            var (inst, game) = World(t, ShadowMods, ShadowEnabled, ShadowPlugins);
            string work = t.At("work");
            // a stand-in probe package and field schema, and a disabled mod's plugin the scope stage would also read
            Directory.CreateDirectory(t.At("pkg"));
            File.WriteAllBytes(t.At("pkg/scopeprobe.exe"), "exe"u8.ToArray());
            File.WriteAllBytes(t.At("pkg/housecarl-core.dll"), "core"u8.ToArray());
            File.WriteAllText(t.At("corpus.json"), "{}");
            string offplug = t.At("offmod/Off.esp");
            WritePlugin(offplug, ["Base.esm"]);

            (PyDict key, List<PyDict> arms) Keyed(params string[] sel)
            {
                var ctx = InPlace(t, inst, game, sel);
                var res = ctx.Resolve();
                var arms = L(res["arms"]).Select(D).ToList();
                return (PrepReuse.Key(ctx.Instance, ctx.Profile, ctx.Game, Strings(res["selection"]), arms,
                                      PrepReuse.ToolsDigest(t.At("pkg"), t.At("corpus.json")), [offplug]), arms);
            }
            (string? run, string why) Found(string run, params string[] sel)
            {
                var (key, arms) = Keyed(sel);
                return PrepReuse.Find(work, run, key, arms);
            }
            string Earlier(string name, params string[] sel)
            {
                string run = PyPath.Join(work, name, "run");
                var (key, arms) = Keyed(sel);
                var stages = new PyDict();
                foreach (string st in PrepReuse.Stages)
                {
                    string file = PyPath.Join(run, st, "out.json");
                    Directory.CreateDirectory(PyPath.Join(run, st));
                    File.WriteAllText(file, PyJson.Dumps(new PyDict { ["path"] = file }));
                    stages[st] = new PyDict { ["accepted"] = true, ["outputs"] = new PyDict { [file] = "x" }, ["bound"] = new PyDict { ["at"] = run } };
                }
                File.WriteAllText(PyPath.Join(run, "stages.json"), PyJson.Dumps(new PyDict { ["stages"] = stages }));
                File.WriteAllText(PyPath.Join(run, "arms.json"), PyJson.Dumps(arms.Cast<object?>().ToList()));
                File.WriteAllText(PyPath.Join(run, PrepReuse.KeyFile), PyJson.Dumps(key));
                return run;
            }

            string old = Earlier("20260101T000000Z", "Sel");
            string fresh = PyPath.Join(work, "20260101T000100Z", "run");
            Directory.CreateDirectory(fresh);
            var (found, why) = Found(fresh, "Sel");
            if (found == null || !PyOrder.SamePath(found, old)) return $"a matching run was not reused: {why}";
            var seeded = PrepReuse.Seed(found, fresh);
            string copied = S(D(PyJson.ReadPipelineFile(PyPath.Join(fresh, "census", "out.json")))["path"]);
            if (!copied.Contains(fresh) || copied.Contains(old)) return $"the earlier folder's path stayed: {copied}";
            if (!D(D(seeded["scope"])["outputs"]).Keys.All(p => p.Contains(fresh))) return "a seeded receipt names an output of the earlier folder";

            // another selection: refused, naming it
            (found, why) = Found(fresh, "A");
            if (found != null || !why.Contains("profile and selection")) return $"another selection: {why}";

            // a touched plugin in a loaded folder: refused; its time restored: reused again
            string plug = PyPath.Join(inst, "mods", "Sel", "Sel.esp");
            var stamp = File.GetLastWriteTimeUtc(plug);
            File.SetLastWriteTimeUtc(plug, stamp.AddSeconds(10));
            (found, why) = Found(fresh, "Sel");
            if (found != null || !why.Contains("mod folder")) return $"a touched plugin: {why}";
            File.SetLastWriteTimeUtc(plug, stamp);
            if (Found(fresh, "Sel").run == null) return "restoring the plugin's time did not match again";

            // a disabled mod's plugin the scope stage reads changed: refused
            File.WriteAllBytes(offplug, File.ReadAllBytes(offplug).Append((byte)0).ToArray());
            (found, why) = Found(fresh, "Sel");
            if (found != null || !why.Contains("disabled mod")) return $"a changed disabled plugin: {why}";
            WritePlugin(offplug, ["Base.esm"]);
            if (Found(fresh, "Sel").run == null) return "restoring the disabled mod's plugin did not match again";

            // another file of the probe's package changed (not the exe): refused
            File.WriteAllBytes(t.At("pkg/housecarl-core.dll"), "core2"u8.ToArray());
            (found, why) = Found(fresh, "Sel");
            if (found != null || !why.Contains("scopeprobe build")) return $"a changed probe package: {why}";
            File.WriteAllBytes(t.At("pkg/housecarl-core.dll"), "core"u8.ToArray());

            // a plugin replaced keeping its size and time: refused (plugins are hashed)
            byte[] data = File.ReadAllBytes(plug);
            data[^1] ^= 0xFF;
            File.WriteAllBytes(plug, data);
            File.SetLastWriteTimeUtc(plug, stamp);
            (found, why) = Found(fresh, "Sel");
            if (found != null || !why.Contains("mod folder")) return $"a replaced plugin: {why}";

            // a changed profile file: refused
            File.AppendAllText(PyPath.Join(inst, "profiles", "P", "loadorder.txt"), "\n");
            (found, why) = Found(fresh, "Sel");
            return found == null && why.Contains("profile file") ? null : $"a changed profile file: {why}";
        });
        Case("prep reuse: a seeded text file keeps its line ends and only the run path changes", () =>
        {
            using var t = new Temp();
            string old = t.At("work/a/run"), fresh = t.At("work/b/run");
            var stages = new PyDict();
            foreach (string st in PrepReuse.Stages)
            {
                Directory.CreateDirectory(PyPath.Join(old, st, "commands"));
                File.WriteAllBytes(PyPath.Join(old, st, "commands", "log.txt"), Encoding.UTF8.GetBytes($"$ probe --json {PyOrder.Fwd(old)}/{st}/x.json\n(exit 0, 1.0s)\r\n\r\ncafé {old}\n"));
                File.WriteAllBytes(PyPath.Join(old, st, "data.bin"), Encoding.UTF8.GetBytes(old));
                File.WriteAllBytes(PyPath.Join(old, st, "plain.json"), "{\"a\": 1}\n"u8.ToArray());
                stages[st] = new PyDict { ["accepted"] = true, ["outputs"] = new PyDict { [PyOrder.Fwd(PyPath.Join(old, st, "plain.json"))] = "x" } };
            }
            File.WriteAllText(PyPath.Join(old, "stages.json"), PyJson.Dumps(new PyDict { ["stages"] = stages }));
            Directory.CreateDirectory(fresh);
            PrepReuse.Seed(old, fresh);
            string log = Encoding.UTF8.GetString(File.ReadAllBytes(PyPath.Join(fresh, "scope", "commands", "log.txt")));
            return Same("log", log, $"$ probe --json {PyOrder.Fwd(fresh)}/scope/x.json\r\n(exit 0, 1.0s)\r\n\r\ncafé {fresh}\r\n")
                   ?? Same("a file that is not text", Encoding.UTF8.GetString(File.ReadAllBytes(PyPath.Join(fresh, "scope", "data.bin"))), old)
                   ?? SameBytes("a text file without the path", File.ReadAllBytes(PyPath.Join(fresh, "scope", "plain.json")), "{\"a\": 1}\n"u8.ToArray());
        });
    }

    // ---- where this controller follows the probes' readers and Python's copy answers otherwise
    //      (each is listed in the migration notes' slice2/intended-differences.md)

    static void OwnerDifferenceCases()
    {
        var python = D(_pipeline["ownerDifferences"]);

        Case("owner: the base-game test is BaseGamePlugins (SkyrimVR.esm, _ResourcePack.esl, any case)", () =>
        {
            var row = D(_pipeline["scopeLineBaseGame"]);
            string mine = ScopeRules.Line(D(row["scope"]), D(row["res"]), D(row["disabled"]));
            const string want = "Mod X edits 2 cells in Tamriel (WatchExterior02) and 1 interior (Watch01); no other enabled plugin besides the base game touches those cells.";
            return Same("line", mine, want) ?? (S(row["python"]).Contains("3 other enabled plugins") ? null : $"Python's line changed: {row["python"]}");
        });
        Case("owner: plugin names compare by the probes' invariant lower-casing", () =>
        {
            var rows = L(python["lower"]).Select(D).ToList();
            var names = new PluginNames();
            string first = names.Of(S(rows[0]["name"]))!, second = names.Of(S(rows[1]["name"]))!;
            if (first != second) return "two spellings that differ only by case are two plugins";
            return S(rows[0]["python"]) != S(rows[1]["python"]) ? null : "Python lower-cases the two spellings alike now; this is no longer a difference";
        });
        Case("owner: a header's masters are read by PluginHeader (Latin-1), its bits and record count as the file states them", () =>
        {
            using var t = new Temp();
            var want = D(python["header"]);
            File.WriteAllBytes(t.At("Odd.esp"), B64(want["bytes"]));
            var h = PluginFiles.Header(t.At("Odd.esp"));
            return Same("masters", string.Join("|", Strings(h["masters"])), "caf\u0080.esm|Plain.esm")
                   ?? Same("Python's masters", string.Join("|", Strings(want["pythonMasters"])), "caf€.esm|Plain.esm")
                   ?? Same("esm and esl", $"{h["esm"]} {h["esl"]}", $"{want["esm"]} {want["esl"]}")
                   ?? Same("records", PluginFiles.Records(t.At("Odd.esp")).ToString(), PyText.Str(want["records"]));
        });
        Case("owner: masters and record count are read before an oversized header subrecord", () =>
        {
            using var t = new Temp();
            var want = D(python["oversized"]);
            File.WriteAllBytes(t.At("Big.esm"), B64(want["bytes"]));
            var h = PluginFiles.Header(t.At("Big.esm"));
            return Same("masters", PyJson.Dumps(h["masters"]), PyJson.Dumps(want["pythonMasters"]))
                   ?? Same("esm and esl bits", $"{h["esm"]} {h["esl"]}", $"{want["esm"]} {want["esl"]}")
                   ?? Same("records", PluginFiles.Records(t.At("Big.esm")).ToString(), PyText.Str(want["records"]))
                   ?? (PluginFiles.IsMaster("Big.esm", new PyDict { ["Big.esm"] = h }) ? null : "an .esm without the header bit does not load as a master");
        });
        Case("owner: a file that is not a plugin is refused by name", () =>
        {
            using var t = new Temp();
            File.WriteAllText(t.At("Text.esp"), "not a plugin at all, only text");
            return Refuses(() => PluginFiles.Header(t.At("Text.esp")), "Text.esp is not a plugin (no TES4 header)");
        });
        Case("owner: modlist.txt is read by Mo2LoadOrder (a byte-order mark does not hide the first mod)", () =>
        {
            using var t = new Temp();
            var (inst, game) = World(t, [Mod("Top", "Top.esp")], ["Top"], ["Top.esp"]);
            File.WriteAllBytes(PyPath.Join(inst, "profiles", "P", "modlist.txt"), [0xEF, 0xBB, 0xBF, .. "+Top\r\n"u8]);
            var by = ArmsById(InPlace(t, inst, game, "Top").Resolve());
            return Same("combined", Plugins(by["combined"]), "Base.esm Top.esp")
                   ?? (D(python["bomModlist"]).Get("refusal") is string said && said.StartsWith("Top is not a mod folder") ? null : "Python reads this modlist now; this is no longer a difference");
        });
        Case("owner: an unmanaged (*) modlist line and a separator are not mod folders", () =>
        {
            using var t = new Temp();
            var (inst, game) = World(t, [Mod("Sel", "Sel.esp"), Mod("A", "A.esp")], ["Sel", "A"], ["A.esp", "Sel.esp"]);
            File.WriteAllText(PyPath.Join(inst, "profiles", "P", "modlist.txt"), "# header\n+Sel\n+Tools_separator\n*DLC: Dawnguard\n-Group_separator\n+A\n");
            var by = ArmsById(InPlace(t, inst, game, "Sel").Resolve());
            return Same("combined mods", string.Join("|", Strings(by["combined"]["mods"])), "Sel|A")
                   ?? Refuses(() => InPlace(t, inst, game, "DLC: Dawnguard").Resolve(), "is not a mod folder of profile P");
        });
        Case("owner: the game's Data is always a layer (a plugin only Data provides stays in both arms)", () =>
        {
            using var t = new Temp();
            var (inst, game) = World(t, [Mod("Sel", "Sel.esp")], ["Sel"], ["Sel.esp"], ["Base.esm", "OnlyData.esp"]);
            var res = InPlace(t, inst, game, "Sel").Resolve();
            var by = ArmsById(res);
            return Same("combined", Plugins(by["combined"]), "Base.esm OnlyData.esp Sel.esp")
                   ?? Same("without-selection", Plugins(by["without-selection"]), "Base.esm OnlyData.esp")
                   ?? Same("provider", PyText.Repr(D(res["providers"])["OnlyData.esp"]), "None");
        });
        Case("owner: a plugin only a folder outside the view provides is not loaded", () =>
        {
            using var t = new Temp();
            // Gone.esp is active in the profile, and only the disabled folder Off holds a copy
            var (inst, game) = World(t, [Mod("Sel", "Sel.esp"), Mod("Off", "Gone.esp")], ["Sel"], ["Sel.esp", "Gone.esp"]);
            var by = ArmsById(InPlace(t, inst, game, "Sel").Resolve());
            return Same("combined", Plugins(by["combined"]), "Base.esm Sel.esp") ?? Same("without-selection", Plugins(by["without-selection"]), "Base.esm");
        });
        Case("owner: a locked profile file is refused, not read as an empty order", () =>
        {
            using var t = new Temp();
            var (inst, game) = World(t, ShadowMods, ShadowEnabled, ShadowPlugins);
            using var held = new FileStream(PyPath.Join(inst, "profiles", "P", "loadorder.txt"), FileMode.Open, FileAccess.Read, FileShare.None);
            try { InPlace(t, inst, game, "Sel").Resolve(); }
            catch (ProfileUnreadableException ex) { return ex.Message.Contains("loadorder.txt") ? null : $"the refusal does not name the file: {ex.Message}"; }
            return "a locked loadorder.txt was read";
        });
        Case("owner: the source profile's load order is read by Mo2LoadOrder (an indented # line is a comment)", () =>
        {
            using var t = new Temp();
            var ctx = Lab(t, PlacedFiles, PlacedSource, PlacedEntry("{\"Lab.esp\": {\"after\": \"PB.esp\"}}"));
            File.WriteAllText(t.At("src/profiles/P/loadorder.txt"), "# written by MO2\r\nBase.esm\r\n  # a note\r\n\r\nPA.esp \r\nPB.esp\r\nSubj.esp\r\n");
            return Same("order", string.Join("|", LabResolve.SourceOrder(ctx)), "Base.esm|PA.esp|PB.esp|Subj.esp");
        });
        Case("owner: a candidate no probe can read (an arm that loads no plugin) is refused when it is written", () =>
        {
            using var t = new Temp();
            // the only plugin of the profile comes from the selection, so the profile without it loads nothing
            var (inst, game) = World(t, [new ModFolder("Sel", ("Sel.esp", []))], ["Sel"], ["Sel.esp"], []);
            var ctx = InPlace(t, inst, game, "Sel");
            var res = ctx.Resolve();
            Directory.CreateDirectory(t.At("out/prepare"));
            return Same("without-selection", Plugins(ArmsById(res)["without-selection"]), "")
                   ?? Refuses(() => StageBodies.Prepare(ctx, res, t.At("out/prepare")), "is not one a probe can read");
        });
        Case("owner: a candidate this controller writes is one CandidateFile reads back the same", () =>
        {
            using var t = new Temp();
            var (inst, game) = World(t, ShadowMods, ShadowEnabled, ShadowPlugins);
            var ctx = InPlace(t, inst, game, "Sel");
            var res = ctx.Resolve();
            Directory.CreateDirectory(t.At("out/prepare"));
            StageBodies.Prepare(ctx, res, t.At("out/prepare"));
            var read = CandidateFile.Read(t.At("out/prepare/candidates/candidate-01.json"));
            return Same("plugins", string.Join(" ", read.Plugins), "Base.esm A.esp Sel.esp Dup.esp")
                   ?? Same("mods", string.Join(" ", read.Mods ?? []), "Sel Other A") ?? Same("base profile", read.BaseProfile, "P");
        });
    }
}
