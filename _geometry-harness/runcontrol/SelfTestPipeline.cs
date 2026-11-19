using System.Reflection;
using System.Text;

namespace RunControl;

// The pipeline's rules: Python's own answers for a table of inputs (known-answers/pipeline.json), and the cases of
// tests/test_evalpipeline.py about arms, order and scope acceptance, each under its Python number.
public static partial class SelfTest
{
    static PyDict _pipeline = new();

    static PyDict Json(string text) => D(PyJson.Loads(text));

    // A refusal whose sentence holds `needle`, or the reason it is not one.
    static string? Refuses(Action act, string needle)
    {
        try { act(); }
        catch (Refusal r) { return r.Message.Contains(needle) ? null : $"refused, but the sentence lacks {needle}: {r.Message}"; }
        return $"expected a refusal naming {needle}; it was accepted";
    }

    // A row's answer against Python's: the value as JSON, or the sentence it refuses with.
    static string? Answer(PyDict row, Func<object?> act)
    {
        string got;
        try { got = "value " + PyJson.Dumps(act()); }
        catch (Refusal r) { got = "refusal " + r.Message; }
        return Same("answer", got, row.ContainsKey("refusal") ? "refusal " + S(row["refusal"]) : "value " + PyJson.Dumps(row["out"]));
    }

    static void PipelineCases()
    {
        using (var s = Assembly.GetExecutingAssembly().GetManifestResourceStream("pipeline.json")!)
        using (var r = new StreamReader(s, Encoding.UTF8))
            _pipeline = D(PyJson.Loads(r.ReadToEnd()));
        var pk = _pipeline;
        var headers = D(pk["headers"]);

        Case("pipeline: normalise_entry gives each plugin one spelling", () =>
            Rows(L(pk["normalise"]).Select(D), row => Answer(row, () => new PluginNames().Normalise(D(row["in"])))));
        Case("pipeline: derive_arms, arms and refusals", () =>
            Rows(L(pk["derive"]).Select(D), row => Answer(row, () => Arms.Derive(D(row["entry"]), headers).Cast<object?>().ToList())));
        Case("pipeline: order_arm, orders and refusals", () =>
            Rows(L(pk["order"]).Select(D), row =>
                Answer(row, () => Py.Objs(Arms.Order(D(row["arm"]), Strings(row["baseline"]), Strings(row["source"]), headers)))));
        Case("pipeline: accept_scope, receipts and refusals", () =>
            Rows(L(pk["accept"]).Select(D), row => Answer(row, () => ScopeRules.Accept(D(row["scope"]), D(row["expect"])))));
        Case("pipeline: scope_line, the sentences", () =>
            Rows(L(pk["scopeLine"]).Select(D), row =>
                Same("line", ScopeRules.Line(D(row["scope"]), D(row["res"]), D(row["disabled"])), S(row["out"]))));
        Case("pipeline: candidate_files, documents and file bytes", () =>
            Rows(L(pk["candidates"]).Select(D), row =>
            {
                using var t = new Temp();
                string id = S(row["id"]);
                var entry = (bool)row["inPlace"]!
                    ? new PyDict { ["id"] = id, ["inPlace"] = new PyDict { ["instance"] = "C:/i", ["profile"] = S(row["profile"]), ["game"] = "C:/g" } }
                    : new PyDict
                    {
                        ["id"] = id, ["source"] = new PyDict { ["instance"] = "C:/s", ["profile"] = "P" },
                        ["lab"] = new PyDict { ["instance"] = "C:/l", ["game"] = "C:/g" },
                    };
                var ctx = new RunContext(entry, new PyDict(), t.At("out"));
                var got = Candidates.Files(ctx, D(row["res"]));
                var want = L(row["out"]).Select(D).ToList();
                if (got.Count != want.Count) return $"{got.Count} candidates, Python has {want.Count}";
                for (int i = 0; i < got.Count; i++)
                {
                    string? why = Same("name", got[i].name, S(want[i]["name"])) ?? Same("arm", got[i].arm["id"] as string, S(want[i]["arm"]))
                        ?? Same("document", PyJson.Dumps(got[i].doc), PyJson.Dumps(want[i]["doc"]));
                    if (why != null) return why;
                    Candidates.Write(t.At(got[i].name), got[i].doc);
                    why = SameBytes(got[i].name, File.ReadAllBytes(t.At(got[i].name)), B64(want[i]["file"]));
                    if (why != null) return why;
                }
                return null;
            }));
        Case("pipeline: in_place_select, the closure over a registry", () =>
        {
            using var t = new Temp();
            PyJson.WritePipelineFile(t.At("registry.json"), D(pk["select"])["registry"]);
            return Rows(L(D(pk["select"])["rows"]).Select(D), row =>
            {
                var entry = new PyDict
                {
                    ["id"] = "x", ["inPlace"] = new PyDict { ["instance"] = "C:/i", ["profile"] = "P", ["game"] = "C:/g" },
                    ["evaluation"] = row["evaluation"],
                };
                var ctx = new RunContext(entry, new PyDict(), t.At("out"));
                return Answer(row, () =>
                {
                    var (select, depth) = InPlaceResolve.Select(ctx, t.At("registry.json"));
                    return new List<object?> { Py.Objs(select), depth };
                });
            });
        });
        Case("pipeline: _size, bytes in the unit that keeps them above 1", () =>
            Rows(L(pk["sizes"]).Select(D), row => Same(PyText.Repr(row["bytes"]), DiskForecast.Size(Py.Float(row["bytes"])), S(row["out"]))));
        Case("pipeline: evalprepreuse.key over a folder tree", () =>
        {
            var want = D(pk["prepKey"]);
            using var t = new Temp();
            BuildTree(want, t);
            var arms = L(want["arms"]).Select(D).Select(a =>
            {
                var arm = new PyDict(a);
                if (arm.ContainsKey("mods")) arm["mods"] = Strings(arm["mods"]).Select(m => (object?)m.Replace("<ROOT\\>", t.Root)).ToList();
                return arm;
            }).ToList();
            var key = PrepReuse.Key(t.At("instance"), "P", t.At("game"), Strings(want["selection"]), arms, D(want["tools"]),
                                    Strings(want["alsoScanned"]).Select(t.At));
            string text = PyJson.Dumps(key).Replace(PyJson.Dumps(PyText.Lower(t.Root)).Trim('"'), PyJson.Dumps("<ROOT\\>").Trim('"'))
                .Replace(PyText.Lower(PyOrder.Fwd(t.Root)), "<ROOT/>");
            return Same("key", text, PyJson.Dumps(want["key"]));
        });

        ArmCases(headers);
        OrderCases(headers);
        ScopeAcceptanceCases();
    }

    // ---- test_evalpipeline.py, cases 1 to 5: the arm rule

    static PyDict AnEntry(string partners, string keys, string more = "") =>
        Json("{\"subject\": {\"plugin\": \"Subj.esp\"}, \"partners\": " + partners + ", \"keys\": " + keys
             + ", \"extraArms\": [], \"orderVariants\": []" + (more.Length > 0 ? ", " + more : "") + "}");

    // Each arm as "sorted evaluated plugins#variant", sorted.
    static List<string> Sets(List<PyDict> arms) =>
        PyOrder.SortedByCodePoint(arms.Select(a => string.Join(",", PyOrder.SortedByCodePoint(Strings(a["evaluated"]))) + "#" + (a.Get("variant") as string ?? "")));

    static void ArmCases(PyDict headers)
    {
        Case("case 1 one partner one key is five arms", () =>
        {
            var got = Sets(Arms.Derive(AnEntry("[{\"plugin\": \"PA.esp\"}]", "[{\"plugin\": \"KA.esp\"}]"), headers));
            var want = PyOrder.SortedByCodePoint(["#", "Subj.esp#", "PA.esp#", "PA.esp,Subj.esp#", "KA.esp,PA.esp,Subj.esp#"]);
            return got.SequenceEqual(want) ? null : $"five arms (pair + key collapses into combined + key), got {string.Join(" | ", got)}";
        });
        Case("case 2 two partners two keys is thirteen arms", () =>
        {
            var got = Sets(Arms.Derive(AnEntry("[{\"plugin\": \"PA.esp\"}, {\"plugin\": \"PB.esp\"}]", "[{\"plugin\": \"KA.esp\"}, {\"plugin\": \"KB.esp\"}]"), headers));
            if (got.Count != 13) return $"13 arms for two partners and two keys, got {got.Count}: {string.Join(" | ", got)}";
            foreach (string want in new[] { "PA.esp,PB.esp#", "PA.esp,Subj.esp#", "KA.esp,PA.esp,Subj.esp#", "KA.esp,KB.esp,PA.esp,PB.esp,Subj.esp#" })
                if (!got.Contains(want)) return $"missing arm {want}";
            return null;
        });
        Case("case 3 partner group is one unit", () =>
        {
            var got = Sets(Arms.Derive(AnEntry("[{\"plugin\": \"PA.esp\"}, {\"plugin\": \"PC1.esp\", \"group\": \"c\"}, {\"plugin\": \"PC2.esp\", \"group\": \"c\"}, "
                                               + "{\"plugin\": \"PCP.esp\", \"group\": \"c\"}]", "[]"), headers));
            if (!got.Contains("PC1.esp,PC2.esp,PCP.esp#")) return $"the group is one partner arm, got {string.Join(" | ", got)}";
            if (got.Contains("PC1.esp#")) return "a group member never stands alone";
            return got.Count == 8 ? null : $"8 arms for two partner units and no key, got {got.Count}";
        });
        Case("case 4 key without partner master has no pair arm", () =>
        {
            var got = Sets(Arms.Derive(AnEntry("[{\"plugin\": \"PA.esp\"}, {\"plugin\": \"PB.esp\"}]", "[{\"plugin\": \"KS.esp\"}]"), headers));
            return got.Count == 9 ? null : $"two units, one key mastering no partner: 8 + combined+key = 9, got {got.Count}: {string.Join(" | ", got)}";
        });
        Case("case 5 extra arm and order variant", () =>
        {
            var entry = AnEntry("[{\"plugin\": \"PA.esp\"}]", "[]");
            entry["extraArms"] = L(PyJson.Loads("[{\"id\": \"x\", \"plugins\": [\"Subj.esp\", \"PA.esp\"]}]"));
            entry["orderVariants"] = L(PyJson.Loads("[{\"id\": \"combined-reverse\", \"of\": \"combined\", \"reverse\": true}]"));
            var got = Sets(Arms.Derive(entry, headers));
            if (!got.Contains("PA.esp,Subj.esp#combined-reverse")) return $"the order variant is its own arm, got {string.Join(" | ", got)}";
            return got.Count == 5 ? null : $"the extra arm duplicates combined and collapses: 4 + 1 variant = 5, got {got.Count}";
        });
    }

    // ---- cases 7 to 16: the order rule

    static readonly string[] OrderBase = ["Base.esm", "Dlc.esm", "Fix.esp"];
    static readonly string[] OrderSource =
    [
        "Base.esm", "Dlc.esm", "PA.esp", "Fix.esp", "PB.esp", "PC1.esp", "PC2.esp", "PCP.esp", "Subj.esp", "KA.esp", "KB.esp", "KS.esp", "SubjM.esp",
    ];

    static void OrderCases(PyDict headers)
    {
        Case("case 7 order follows source with masters first", () =>
        {
            var got = Arms.Order(Json("{\"evaluated\": [\"Subj.esp\", \"PA.esp\", \"KA.esp\"], \"keys\": [\"KA.esp\"]}"), OrderBase, OrderSource, headers);
            return Same("order", string.Join(" ", got), "Base.esm Dlc.esm PA.esp Fix.esp Subj.esp KA.esp");
        });
        Case("case 9 esm flagged esp loads before esps", () =>
        {
            var got = Arms.Order(Json("{\"evaluated\": [\"SubjM.esp\", \"PA.esp\"], \"keys\": []}"), OrderBase, OrderSource, headers);
            return got.IndexOf("SubjM.esp") < got.IndexOf("Fix.esp") ? null : $"an ESM-flagged plugin loads with the masters, got {string.Join(" ", got)}";
        });
        Case("case 11 late master refused", () =>
        {
            var src = OrderSource.Where(p => p != "KA.esp").ToList();
            src.Insert(src.IndexOf("Subj.esp"), "KA.esp");   // key before its master
            return Refuses(() => Arms.Order(Json("{\"evaluated\": [\"Subj.esp\", \"PA.esp\", \"KA.esp\"], \"keys\": [\"KA.esp\"]}"), OrderBase, src, headers), "KA.esp");
        });
        Case("case 13 missing from source refused", () =>
        {
            var more = new PyDict(headers) { ["Gone.esp"] = Json("{\"masters\": [\"Base.esm\"], \"esm\": false}") };
            return Refuses(() => Arms.Order(Json("{\"evaluated\": [\"Subj.esp\", \"Gone.esp\"], \"keys\": []}"), OrderBase, OrderSource, more), "Gone.esp");
        });
        Case("case 15 reverse variant that breaks masters refused", () =>
            Refuses(() => Arms.Order(Json("{\"evaluated\": [\"PC1.esp\", \"PC2.esp\", \"PCP.esp\"], \"keys\": [], \"reverse\": true}"), OrderBase, OrderSource, headers), "PCP.esp"));
        Case("case 16 reverse variant swaps independent plugins", () =>
        {
            var got = Arms.Order(Json("{\"evaluated\": [\"Subj.esp\", \"PA.esp\"], \"keys\": [], \"reverse\": true}"), OrderBase, OrderSource, headers);
            if (got.IndexOf("Subj.esp") >= got.IndexOf("PA.esp")) return $"reverse puts the subject before the partner, got {string.Join(" ", got)}";
            return got[0] == "Base.esm" && got[1] == "Dlc.esm" ? null : $"the baseline keeps its place, got {string.Join(" ", got)}";
        });
    }

    // ---- cases 17 to 25: scope acceptance

    const string ScopeDoc = """
        {"complete": true, "walkFailures": [], "unreadableRecords": [],
         "configuration": {"fingerprint": "f",
                           "plugins": [{"Name": "Base.esm", "Path": "X:/Base.esm", "Sha256": "hBase.esm"},
                                       {"Name": "Subj.esp", "Path": "X:/Subj.esp", "Sha256": "hSubj.esp"}],
                           "providers": [{"Name": "M1", "Kind": "mod"}]},
         "targets": [{"name": "Subj.esp", "sha256": "hSubj.esp",
                      "footprint": {"placed": {"cells": [{"cell": "000A:Base.esm", "kind": "interior"},
                                                         {"cell": "000B:Base.esm", "kind": "exterior"}]}}}]}
        """;
    const string ScopeExpect = """
        {"plugins": [{"name": "Base.esm", "path": "X:/Base.esm", "sha256": "hBase.esm"}, {"name": "Subj.esp", "path": "X:/Subj.esp", "sha256": "hSubj.esp"}],
         "mods": ["M1"], "targets": ["Subj.esp"], "censusCells": {"Subj.esp": ["000A:Base.esm", "000B:Base.esm"]}}
        """;

    static void ScopeAcceptanceCases()
    {
        Case("case 17 scope accepted", () =>
        {
            PyDict r;
            try { r = ScopeRules.Accept(Json(ScopeDoc), Json(ScopeExpect)); }
            catch (Refusal ex) { return $"a good scope must be accepted, refused: {ex.Message}"; }
            return L(D(D(r["targets"])["Subj.esp"])["missingCensusCells"]).Count == 0 ? null : PyJson.Dumps(r);
        });
        Case("case 19 incomplete scope refused", () =>
        {
            var a = Json(ScopeDoc);
            a["complete"] = false;
            var b = Json(ScopeDoc);
            b["walkFailures"] = L(PyJson.Loads("[{\"x\": 1}]"));
            return Refuses(() => ScopeRules.Accept(a, Json(ScopeExpect)), "incomplete") ?? Refuses(() => ScopeRules.Accept(b, Json(ScopeExpect)), "incomplete");
        });
        Case("case 21 provider or bytes swap refused", () =>
        {
            var a = Json(ScopeDoc);
            D(L(D(a["configuration"])["plugins"])[1])["Sha256"] = "other";
            var b = Json(ScopeDoc);
            D(b["configuration"])["providers"] = L(PyJson.Loads("[{\"Name\": \"M2\", \"Kind\": \"mod\"}]"));
            return Refuses(() => ScopeRules.Accept(a, Json(ScopeExpect)), "Subj.esp") ?? Refuses(() => ScopeRules.Accept(b, Json(ScopeExpect)), "provider");
        });
        Case("case 23 missing census cell refused", () =>
        {
            var expect = Json(ScopeExpect);
            expect["censusCells"] = Json("{\"Subj.esp\": [\"000A:Base.esm\", \"000C:Base.esm\"]}");
            return Refuses(() => ScopeRules.Accept(Json(ScopeDoc), expect), "000C:Base.esm");
        });
        Case("case 25 empty target footprint refused", () =>
        {
            var doc = Json(ScopeDoc);
            doc["targets"] = new List<object?>();
            return Refuses(() => ScopeRules.Accept(doc, Json(ScopeExpect)), "target");
        });
    }
}
