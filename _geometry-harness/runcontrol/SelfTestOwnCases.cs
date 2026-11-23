using System.Text;

namespace RunControl;

// Known answers worked by hand for rules whose Python cases went with the Python controller (step 3 of its retirement):
// adviceprobe's neighbour list and the advice statements the disposition carries (tests/test_adviceprobe.py's two
// evalrun checks), and an arm bound to a same-name variant of a plugin (tests/test_same_name_variant.py case 455).
public static partial class SelfTestFinalize
{
    const string ShaA = "1111111111111111111111111111111111111111111111111111111111111111";
    const string ShaVariant = "2222222222222222222222222222222222222222222222222222222222222222";
    const string ShaMain = "3333333333333333333333333333333333333333333333333333333333333333";

    public static void RunOwnCases(Action<string, Func<string?>> @case)
    {
        Case = @case;
        Case("fingerprint: the command answers each configuration by the owner's text, and refuses another schema or a missing state", () =>
        {
            var cfg = new PyDict
            {
                ["plugins"] = new List<object?> { new PyDict { ["name"] = "Skyrim.ESM", ["sha256"] = "ab" }, new PyDict { ["name"] = "Beta.esp", ["sha256"] = null } },
                ["providers"] = new List<object?> { new PyDict { ["kind"] = "mod", ["name"] = "Beta Shrine" } },
                ["overlays"] = new List<object?> { new PyDict { ["kind"] = "bos", ["sha256"] = "cd" } },
                ["population"] = "load-time", ["state"] = "baseline", ["schemaVersion"] = 1L,
            };
            string want = Hashing.Sha256Text("schema:1\npopulation:load-time\nstate:baseline\nplugin:skyrim.esm:ab\nplugin:beta.esp:\n"
                                              + "provider:mod:beta shrine\noverlay:bos:cd\n");
            var got = BundleMedia.Fingerprints(new List<object?> { cfg });
            if (got.Count != 1 || got[0] as string != want) return $"fingerprint gave {PyText.Repr(got)}; the owner's text hashes to {want}";
            foreach (var (bad, why) in new (PyDict, string)[]
            {
                (new PyDict { ["population"] = "load-time", ["state"] = "baseline", ["schemaVersion"] = 2L }, "schemaVersion 2"),
                (new PyDict { ["population"] = "load-time" }, "no population or state"),
            })
            {
                try { BundleMedia.Fingerprints(new List<object?> { bad }); return $"a configuration with {why} was fingerprinted, not refused"; }
                catch (Refusal r) when (r.Message.Contains(why)) { }
            }
            return null;
        });
        Case("advice: the --with list is every other instance mod folder the configuration loads a plugin from, by its meta.ini id", () =>
        {
            using var t = new Temp();
            string inst = t.At("inst");
            static byte[] Meta(int id) => Encoding.UTF8.GetBytes($"[General]\nmodid={id}\nversion=1.0\n\n[installedFiles]\n1\\modid={id}\n1\\fileid={id * 10}\nsize=1\n");
            t.Write("inst/mods/Alpha Halls/meta.ini", Meta(9001));
            t.Write("inst/mods/Beta Shrine/meta.ini", Meta(9002));
            var plan = new PyDict
            {
                ["options"] = new PyDict { ["instance"] = inst },
                ["targets"] = new List<object?> { new PyDict { ["name"] = "Beta.esp", ["provider"] = "Beta Shrine", ["footprint"] = new PyDict { ["nexusId"] = 9002L } } },
                ["configuration"] = new PyDict
                {
                    ["plugins"] = new List<object?>
                    {
                        new PyDict { ["name"] = "Skyrim.esm", ["path"] = t.At("game/Data/Skyrim.esm") },
                        new PyDict { ["name"] = "AlphaHalls.esp", ["path"] = t.At("inst/mods/Alpha Halls/AlphaHalls.esp") },
                        new PyDict { ["name"] = "Beta.esp", ["path"] = t.At("inst/mods/Beta Shrine/Beta.esp") },
                    },
                },
            };
            var got = GenericRows.AdviceNeighbourIds(plan);
            return got.SequenceEqual(new long[] { 9001 }) ? null
                : $"the --with list is [{string.Join(", ", got)}]; it must be [9001]: the evaluated mod's own folder and id are not its neighbours, and the game's Data is no mod folder";
        });
        Case("advice: the disposition carries the report's rule count and each stated clash", () =>
        {
            using var t = new Temp();
            t.Write("outputs/report-9002.json", PyJson.Utf8(PyJson.Dumps(new PyDict
            {
                ["modId"] = 9002L,
                ["rules"] = new List<object?>
                {
                    new PyDict { ["before"] = "Beta.esp", ["after"] = "AlphaHalls.esp", ["hardness"] = "advice" },
                    new PyDict { ["before"] = "Beta.esp", ["after"] = "Skyrim.esm", ["hardness"] = "advice" },
                },
                ["clashes"] = new List<object?>
                {
                    new PyDict
                    {
                        ["namedModId"] = 9002L, ["namedModName"] = "Beta - A Shrine Overhaul", ["pageModId"] = 9001L, ["page"] = "https://example.invalid/9001",
                        ["readDate"] = "2026-11-08", ["line"] = "-Beta - A Shrine Overhaul: Trapdoor entrance is blocked", ["symptom"] = "Trapdoor entrance is blocked",
                        ["extra"] = "not carried",
                    },
                },
                ["pagesRead"] = new List<object?> { "mod 9002: archive" }, ["warnings"] = new List<object?>(),
            })));
            var rec = Discovery.AdviceStatements(t.Root, new PyDict { ["outputs"] = new List<object?> { new PyDict { ["path"] = "outputs/report-9002.json" } } });
            var want = new PyDict
            {
                ["rules"] = 2L, ["clashCount"] = 1L,
                ["clashes"] = new List<object?>
                {
                    new PyDict
                    {
                        ["namedModId"] = 9002L, ["namedModName"] = "Beta - A Shrine Overhaul", ["pageModId"] = 9001L, ["page"] = "https://example.invalid/9001",
                        ["readDate"] = "2026-11-08", ["line"] = "-Beta - A Shrine Overhaul: Trapdoor entrance is blocked", ["symptom"] = "Trapdoor entrance is blocked",
                    },
                },
            };
            var gone = Discovery.AdviceStatements(t.Root, new PyDict { ["outputs"] = new List<object?> { new PyDict { ["path"] = "outputs/missing.json" } } });
            return SameJson("statements", rec, want)
                   ?? (gone.Get("rules") == null && PyValue.Len(Ev.OrList(gone.Get("clashes"))) == 0 && gone.ContainsKey("statementsNote") ? null
                       : $"an unreadable report must give unknown rules and no clash, with a note; got {PyJson.Dumps(gone)}");
        });
        Case("case 455 an arm bound to a same-name variant accepts an emit that read the variant and rejects one that read the main file", () =>
        {
            using var t = new Temp();
            string variant = t.At("inst/mods/Mod Variant/Mod.esp"), main = t.At("inst/mods/Mod/Mod.esp");
            var arm = new PyDict
            {
                ["armId"] = "candidate-01-variant", ["kind"] = "candidate", ["candidateFile"] = "candidate-01-variant.json",
                ["requested"] = new PyDict
                {
                    ["plugins"] = new List<object?>
                    {
                        new PyDict { ["name"] = "Skyrim.esm", ["sha256"] = ShaA, ["path"] = t.At("game/Data/Skyrim.esm") },
                        new PyDict { ["name"] = "Mod.esp", ["sha256"] = ShaVariant, ["path"] = variant },
                    },
                },
            };
            var plan = new PyDict { ["arms"] = new List<object?> { arm } };
            List<string> Mismatch(string sha, string path)
            {
                var identity = new PyDict
                {
                    ["plugins"] = new List<object?>
                    {
                        new PyDict { ["name"] = "Skyrim.esm", ["sha256"] = ShaA, ["path"] = t.At("game/Data/Skyrim.esm") },
                        new PyDict { ["name"] = "Mod.esp", ["sha256"] = sha, ["path"] = path },
                    },
                    ["overlays"] = new List<object?>(), ["population"] = "load-time", ["state"] = "baseline",
                    ["fingerprint"] = new string('0', 64),   // not this arm's: its own rejection, not the one counted below
                };
                var rej = new Validator(t.Root, plan).IdentityChecks(identity, arm, new PyDict(), new PyDict { ["kind"] = "probe-emit", ["exe"] = "overlapprobe" }, null, []);
                return rej.Select(r => PyText.Str(Ev.G(r, "detail"))).Where(d => d.Contains("Mod.esp was requested at", StringComparison.Ordinal)).ToList();
            }
            var ofVariant = Mismatch(ShaVariant, variant);
            var ofMain = Mismatch(ShaMain, main);
            if (ofVariant.Count != 0) return $"an emit that read the variant was rejected: {string.Join(" | ", ofVariant)}";
            if (ofMain.Count != 1) return $"an emit that read the main file must be rejected once for this arm, got {ofMain.Count} rejection(s)";
            return null;
        });
    }
}
