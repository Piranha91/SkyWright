using RunControl;

namespace EvalControl;

// The known answers of the live switch check's rules (moved from runcontrol's self-test with the check, under the names
// they had there): the switch-flip reading rule and the re-reading of a live import's launches (SwitchFlipOutcome,
// LiveImport), the check's population on an arm (SwitchFlipScope, known-answers/live.json's populations, moved
// unchanged), and the two list rules only the live check's row meets (an opt-in row that is required, a live row
// without optIn), now asked of the shipped ai-checks.json with this module's vocabulary.
public static partial class SelfTest
{
    static void LiveCases(PyDict ka)
    {
        SwitchFlipCase();
        PopulationCases(L(ka["populations"]).Select(D).ToList());
        ListVariantCases(L(ka["variants"]).Select(D).ToList());
    }

    static void SwitchFlipCase() =>
        Case("switch flip: a launch is read from its events, a stored label that differs and an unsettled condition are refused", () =>
        {
            const double t0 = 1000;
            List<object?> Ev(params PyDict[] e) => e.Cast<object?>().ToList();
            PyDict E(string kind, double t, bool ok = true, string? err = null) => new() { ["kind"] = kind, ["name"] = "m0", ["ok"] = ok, ["t"] = t, ["err"] = err, ["target_reached"] = true };
            var flip = Ev(E("call", t0 + 1), E("readback", t0 + 1.3));
            var complete = flip.Concat(Enumerable.Range(0, 62).Select(i => (object?)E("heartbeat", t0 + 2 + i))).ToList();
            var crash = flip.Concat(Ev(new PyDict { ["kind"] = "alive", ["exe"] = false, ["t"] = t0 + 5 }, new PyDict { ["kind"] = "crashlog", ["path"] = "c.log", ["mtime"] = t0 + 4 })).ToList();
            var stall = flip.Concat(Ev(E("heartbeat", t0 + 3, false, "HTTP 504"), E("heartbeat", t0 + 4, false, "timed out"))).ToList();
            var shortWindow = flip.Concat(Enumerable.Range(0, 30).Select(i => (object?)E("heartbeat", t0 + 2 + i))).ToList();
            foreach (var (events, want, branch) in new (List<object?>, string, string?)[]
                     { (complete, "complete", null), (crash, "fail", "crash"), (stall, "fail", "stall"), (shortWindow, "not-measured", null) })
            {
                var got = SwitchFlipOutcome.Classify(events, t0);
                if (!PyValue.Eq(got["outcome"], want) || !PyValue.Eq(got["branch"], branch))
                    return $"a {want}/{branch ?? "-"} stream classified {PyText.Str(got["outcome"])}/{PyText.Str(got["branch"])} ({PyText.Str(got["why"])})";
            }
            if (SwitchFlipOutcome.Classify(stall, t0)["why"] is not "2 consecutive liveness timeouts ending at t=1004.0 with the game process alive")
                return "the stall's sentence is not Python's";
            PyDict Rc(int rep, string outcome, string? branch) => new()
            {
                ["condition"] = "C1", ["replicate"] = (long)rep, ["flipStart"] = t0, ["windowSeconds"] = 60L,
                ["classification"] = new PyDict { ["outcome"] = outcome, ["branch"] = branch },
            };
            var (errs, readings) = LiveImport.RereadLaunches([(Rc(1, "fail", "crash"), crash), (Rc(2, "fail", "crash"), crash)]);
            if (errs.Count > 0 || !PyValue.Eq(PyValue.Dict(readings["C1"])["reading"], "finding")) return $"two re-read crashes are not a finding: {string.Join("; ", errs)}";
            (errs, _) = LiveImport.RereadLaunches([(Rc(1, "complete", null), crash), (Rc(2, "fail", "crash"), crash)]);
            if (!errs.Any(e => e.Contains("never preferred"))) return "a stored completion over crash events was accepted";
            (errs, _) = LiveImport.RereadLaunches([(Rc(1, "fail", "crash"), crash), (Rc(2, "complete", null), complete)]);
            if (!errs.Any(e => e.Contains("unsettled"))) return "one crash beside one completion was read as settled";
            var tooShort = Rc(1, "complete", null);
            tooShort["windowSeconds"] = 30L;
            (errs, _) = LiveImport.RereadLaunches([(tooShort, complete)]);
            return errs.Any(e => e.Contains("windowSeconds 30 is below the rule's 60 s")) ? null : "a 30 s window was not refused";
        });

    // ---- the live check's population (runcontrol's SelfTestPlanModules, moved)

    static void PopulationCases(List<PyDict> cases)
    {
        Case($"switch scope: the population and its sentence on {cases.Count} census and scope pairs", () => Rows(cases, row =>
        {
            var result = SwitchFlipScope.Population(D(PyJson.Loads(S(row["census"]))), D(PyJson.Loads(S(row["scope"]))));
            return Same("population", PyJson.Canonical(result), S(row["population"])) ?? Same("sentence", SwitchFlipScope.Sentence(result), S(row["sentence"]));
        }));
        Case("switch scope: the cases reach every cause and both ways of touching", () =>
        {
            string all = string.Concat(cases.Select(r => S(r["population"])));
            foreach (string code in new[] { "no-trigger-map", "unreadable-plugins", "scope-incomplete", "no-targets", "cells-unknown", "\"by\":\"plugin\"", "\"by\":\"cell\"",
                         "\"status\":\"not-applicable\"", "\"status\":\"open\"" })
                if (!all.Contains(code)) return $"no case reaches {code}";
            return null;
        });
    }

    // ---- the list rules only the live row meets: runcontrol's validator with this module's vocabulary, over the
    // shipped list edited as each variant says (its ops name the row by its index in the list)

    static void ListVariantCases(List<PyDict> variants) =>
        Case($"list: validate names every problem of {variants.Count} broken lists", () =>
        {
            var shipped = D(PyJson.LoadEvalFile(AiList.Shipped));
            var clean = Registry.Validate(shipped, AiList.Vocabulary);
            if (clean.Count > 0) return "the shipped list is not clean: " + string.Join("; ", clean);
            return Rows(variants, row =>
            {
                var got = Registry.Validate(ApplyOps(shipped, row["ops"]), AiList.Vocabulary);
                return SameJson("problems", got, row["problems"]);
            });
        });

    // A copy of doc with each [path, "set", value] or [path, "del"] applied (the test harness's, as runcontrol's).
    static PyDict ApplyOps(PyDict doc, object? ops)
    {
        var copy = D(Py.Copy(doc));
        foreach (var op in L(ops).Select(L))
        {
            var path = L(op[0]);
            object? at = copy;
            foreach (object? step in path.Take(path.Count - 1)) at = step is string k ? D(at)[k] : L(at)[(int)(long)step!];
            object? last = path[^1];
            if (S(op[1]) == "set")
            {
                if (last is string key) D(at)[key] = Py.Copy(op[2]);
                else L(at)[(int)(long)last!] = Py.Copy(op[2]);
            }
            else D(at).Pop(S(last));
        }
        return copy;
    }
}
