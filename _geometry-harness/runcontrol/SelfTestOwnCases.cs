using System.Text;

namespace RunControl;

// Known answers worked by hand for rules whose Python cases went with the Python controller (step 3 of its retirement):
// an arm bound to a same-name variant of a plugin (tests/test_same_name_variant.py case 455). (adviceprobe's neighbour
// list and the advice statements, tests/test_adviceprobe.py's two evalrun checks, moved with the advice row to the
// houseCARL integration module: evalcontrol's SelfTestAdvice.cs, by name.)
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
            var got = Fingerprints.Of(new List<object?> { cfg });
            if (got.Count != 1 || got[0] as string != want) return $"fingerprint gave {PyText.Repr(got)}; the owner's text hashes to {want}";
            foreach (var (bad, why) in new (PyDict, string)[]
            {
                (new PyDict { ["population"] = "load-time", ["state"] = "baseline", ["schemaVersion"] = 2L }, "schemaVersion 2"),
                (new PyDict { ["population"] = "load-time" }, "no population or state"),
            })
            {
                try { Fingerprints.Of(new List<object?> { bad }); return $"a configuration with {why} was fingerprinted, not refused"; }
                catch (Refusal r) when (r.Message.Contains(why)) { }
            }
            return null;
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
