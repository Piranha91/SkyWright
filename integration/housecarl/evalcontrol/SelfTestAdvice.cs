using System.Text;
using RunControl;

namespace EvalControl;

// The advice row's hand-worked cases, moved by name from runcontrol's SelfTestOwnCases.cs with patch.author-advice
// (work/move_advice_ka_9d1.py): adviceprobe's neighbour list and the advice statements the overlay's disposition
// carries; and its command-line known answers (known-answers/advice.json, moved from runcontrol's evaluate.json).
public static partial class SelfTest
{
    static void AdviceCases()
    {
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
            var got = ModuleRows.AdviceNeighbourIds(plan);
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
            var rec = ModuleRows.AdviceStatements(t.Root, new PyDict { ["outputs"] = new List<object?> { new PyDict { ["path"] = "outputs/report-9002.json" } } });
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
            var gone = ModuleRows.AdviceStatements(t.Root, new PyDict { ["outputs"] = new List<object?> { new PyDict { ["path"] = "outputs/missing.json" } } });
            return SameJson("statements", rec, want)
                   ?? (gone.Get("rules") == null && PyValue.Len(Ev.OrList(gone.Get("clashes"))) == 0 && gone.ContainsKey("statementsNote") ? null
                       : $"an unreadable report must give unknown rules and no clash, with a note; got {PyJson.Dumps(gone)}");
        });
    }

    // ---- the advice row's command line: runcontrol's generic row with this module's rules (the harness of runcontrol's
    // GenericCases, for the two answers these cases record: a launch, or how the row is accounted)

    static void AdviceGenericCases(PyDict ka)
    {
        foreach (var c in L(ka["generic"]).Select(D))
            Case($"generic row: {S(c["name"])}", () =>
            {
                using var t = new Temp();
                BuildTree(t, c["tree"]);
                var plan = D(PyJson.LoadEvalFile(t.At("plan.json")));
                var ex = new Executor(t.Root, plan, new Ledger(t.Root, plan) { RunId = "run-2" }, resume: false) { ParentRunId = "parent-2" };
                var check = L(ex.Plan["checks"]).Select(D).First(x => S(x["checkId"]) == S(c["check"]));
                PyDict? launch = null;
                var real = EvalStore.UtcNow;
                int n = 0;
                EvalStore.UtcNow = () => { n++; return $"2026-01-01T00:{n / 60:00}:{n % 60:00}Z"; };
                try
                {
                    GenericRows.Build(ex, check, ex.Arm(c["arm"]), (argv, outputs, states) =>
                            launch = new PyDict { ["argv"] = Py.Objs(argv), ["outputs"] = Py.Objs(outputs), ["states"] = states },
                        ModuleRows.Placeholders, ModuleRows.ArgvRules, ModuleRows.OutputRules);
                }
                finally { EvalStore.UtcNow = real; }
                var want = D(c["want"]);
                if (launch != null)
                    return want.ContainsKey("launch") ? SameJson("launch", t.Mask(launch), want["launch"]) : $"launched {PyJson.Dumps(launch["argv"])}, Python accounted {PyJson.Dumps(want["account"])}";
                if (!want.ContainsKey("account")) return "accounted, where Python launched " + PyJson.Dumps(want["launch"]);
                var e = ex.Ledger.Entry(c["check"], c["arm"], null)!;
                var r = D(PyJson.LoadEvalFile(t.At($"receipts/{Ledger.Key(c["check"], c["arm"], null)}.json")));
                string note = t.Mask(S(D(r["result"])["note"]))!;
                return SameJson("account", new PyDict { ["status"] = e["status"], ["cause"] = e["cause"], ["note"] = note }, want["account"]);
            });
    }
}
