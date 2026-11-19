using System.Reflection;
using System.Text;
using HousecarlCore.Progress;

namespace RunControl;

// The known answers for `run` and `resume`: the validator, the result specs, the schedule, the generic rows' command
// lines, the prerequisite rule, reuse, revalidation and the executor's receipts, each against evalrun.py's own answer
// (known-answers/make_known_answers_evaluate.py). No probe and no Python runs here.
public static partial class SelfTestFinalize
{
    public static void RunEvaluate(Action<string, Func<string?>> @case)
    {
        Case = @case;
        PyDict ka;
        using (var s = Assembly.GetExecutingAssembly().GetManifestResourceStream("evaluate.json")!)
        using (var r = new StreamReader(s, Encoding.UTF8))
            ka = D(PyJson.Loads(r.ReadToEnd()));
        SmallTables(ka);
        ValidatorCases(ka);
        GenericCases(L(ka["generic"]));
        PrerequisiteCases(D(L(ka["prerequisites"])[0]));
        ReuseCases(D(ka["reuse"]), L(ka["revalidate"]));
        ExecutorCases(D(ka["executor"]));
        CrashCases(L(ka["crash"]));
    }

    const string ControllerLabelPython = "the controller (evalrun.py and the scripts it runs)";
    const string ControllerLabelHere = "the controller (runcontrol and the libraries beside it)";

    // A recorded plan (masked JSON text, deflated when long) as a plan under this root.
    static PyDict PlanOf(Temp t, object? packed) => D(PyJson.Loads(t.Unmask(Unpack(packed)!)));

    // A file's text with one line feed per line end, as Python's read_text gives it.
    static string? Lines(string path) => File.Exists(path) ? Encoding.UTF8.GetString(File.ReadAllBytes(path)).Replace("\r\n", "\n") : null;

    // The recorded folder rebuilt, each file with the time of writing the generator gave it.
    static void BuildTimed(Temp t, object? tree)
    {
        BuildTree(t, tree);
        foreach (var row in L(tree).Select(D).Where(r => r.ContainsKey("mtime")))
            File.SetLastWriteTimeUtc(t.At(S(row["path"])), DateTime.UnixEpoch.AddSeconds(Py.Float(row["mtime"])));
    }

    static Executor ExecutorOver(Temp t, PyDict? plan = null)
    {
        plan ??= D(PyJson.LoadEvalFile(t.At("plan.json")));
        return new Executor(t.Root, plan, new Ledger(t.Root, plan) { RunId = "run-2" }, resume: false) { ParentRunId = "parent-2" };
    }

    static PyDict CheckOf(PyDict plan, string id) => L(plan["checks"]).Select(D).First(c => S(c["checkId"]) == id);

    static void SmallTables(PyDict ka)
    {
        var resolve = D(L(ka["resolveSpec"])[0]);
        Case("evaluate: resolve_spec, a result spec read against an output and a log", () => Rows(L(resolve["rows"]), row =>
        {
            var (value, why) = Validator.ResolveSpec(row["spec"], row["doc"] is true ? resolve["doc"] : null, row["log"] is true ? S(resolve["log"]) : null);
            string want = S(row["why"]);
            // the words after "does not compile: " are each runtime's own
            return SameJson($"{PyJson.Dumps(row["spec"])} value", value, row["value"])
                   ?? (want.EndsWith("does not compile: ", StringComparison.Ordinal) ? (why.StartsWith(want, StringComparison.Ordinal) ? null : $"why: {why}")
                       : Same($"{PyJson.Dumps(row["spec"])} why", why, want));
        }));
        Case("evaluate: schedule, required checks first among those that are ready", () => Rows(L(ka["schedule"]), row =>
            Same("order", string.Join(" ", EvaluateRun.Schedule(new PyDict { ["checks"] = row["checks"] }).Select(c => S(c["checkId"]))),
                string.Join(" ", Strings(row["order"])))));
        Case("evaluate: states_for and wants_common_flags", () => Rows(L(ka["states"]), row =>
            row.ContainsKey("adapter")
                ? Same("common flags", GenericRows.WantsCommonFlags(D(row["adapter"])).ToString(), ((bool)row["common"]!).ToString())
                : SameJson("states", GenericRows.StatesFor(D(row["check"]), D(row["plan"])), row["states"])));
        Case("evaluate: identity_drift, a program against the plan's record of it", () => Rows(L(ka["drift"]), row =>
            Same(S(row["pid"]), ToolPreflight.Drift(S(row["pid"]), row["recorded"], D(row["now"]))?.Replace(ControllerLabelHere, ControllerLabelPython),
                (string?)row["want"])));
    }

    // ---- the validator

    static PyDict ValidateStep(Temp t, Validator v, PyDict plan, PyDict step)
    {
        foreach (var f in L(step["files"]).Select(D))
        {
            string p = t.Write(S(f["path"]), PyJson.Utf8(t.Unmask(Unpack(f["text"])!)));
            File.SetLastWriteTimeUtc(p, DateTime.UnixEpoch.AddSeconds(Py.Float(f["mtime"])));
        }
        var arm = L(plan["arms"]).Select(D).First(a => S(a["armId"]) == S(step["arm"]));
        var pre = new Dictionary<string, string?>(StringComparer.Ordinal);
        // "same": the bytes that are there now were there before the launch
        foreach (var (rel, h) in D(step["pre"])) pre[t.At(rel)] = h is "same" ? Hashing.Sha256File(t.At(rel)) : (string?)h;
        return v.Validate(new PyDict { ["checkId"] = step["check"] }, arm, (string?)step["part"], D(step["adapter"]),
            step["exit"] == null ? null : (long)PyValue.Int(step["exit"]), S(step["log"]), Strings(step["outputs"]).Select(t.At).ToList(), pre,
            step["started"] == null ? null : Py.Float(step["started"]));
    }

    static void ValidatorCases(PyDict ka)
    {
        foreach (var c in L(ka["validate"]).Select(D))
            Case($"validate: {S(c["name"])}", () =>
            {
                using var t = new Temp();
                var plan = PlanOf(t, c["plan"]);
                var v = new Validator(t.Root, plan);
                int n = 0;
                foreach (var step in L(c["steps"]).Select(D))
                {
                    n++;
                    string? why = SameJson($"call {n}", t.Mask(ValidateStep(t, v, plan, step)), step["want"]);
                    if (why != null) return why;
                }
                return null;
            });
        var reuse = D(L(ka["reuseIdentity"])[0]);
        Case("validate: an import declared as a reuse keeps its own source name as a recorded limit", () =>
        {
            using var t = new Temp();
            var plan = PlanOf(t, reuse["plan"]);
            return Rows(L(reuse["rows"]), row =>
            {
                var doc = D(t.Unmask(row["doc"]));
                var limits = new List<object?>();
                var rej = new Validator(t.Root, plan).IdentityChecks(Validator.EmitIdentity(doc)!, D(L(plan["arms"])[0]), doc,
                    new PyDict { ["kind"] = "probe-emit", ["exe"] = "contactprobe" }, null, limits, row["declared"] as PyDict);
                return SameJson("rejections", t.Mask(rej), row["rejections"]) ?? SameJson("limits", t.Mask(limits), row["limits"]);
            });
        });
        var owner = D(L(ka["ownerDifferences"])[0]);
        Case("owner: a null population or state adds no text to the fingerprint (ConfigurationIdentity), where Python hashes the text None", () =>
        {
            using var t = new Temp();
            var plan = PlanOf(t, owner["plan"]);
            return Rows(L(owner["rows"]), row =>
            {
                // this controller's answer is the owner's: the emit hashed the owner's way is accepted, the other is not
                bool mine = S(row["name"]).Contains("the probes' owner", StringComparison.Ordinal);
                const string rel = "outputs/g.check/baseline.json";
                t.Write(rel, PyJson.Utf8(PyJson.Dumps(t.Unmask(row["doc"]))));
                var got = new Validator(t.Root, plan).Validate(new PyDict { ["checkId"] = "g.check" }, D(L(plan["arms"])[0]), null,
                    new PyDict { ["kind"] = "probe-emit", ["exe"] = "contactprobe" }, 0, "", [t.At(rel)], new Dictionary<string, string?>(), null);
                if ((bool)row["pythonAccepted"]! == mine) return "Python's answer is no longer the one recorded as differing";
                if ((bool)got["accepted"]! != mine) return $"accepted {got["accepted"]}, the owner's rule gives {mine}: {PyJson.Dumps(got["rejections"])}";
                return mine ? null : Same("cause", string.Join(",", L(got["rejections"]).Select(r => S(D(r)["cause"]))), "identity-mismatch");
            });
        });
    }

    // ---- one generic row: the command line it builds, or how it is accounted

    static void GenericCases(List<object?> cases)
    {
        foreach (var c in cases.Select(D))
            Case($"generic row: {S(c["name"])}", () =>
            {
                using var t = new Temp();
                BuildTree(t, c["tree"]);
                var ex = ExecutorOver(t);
                var check = CheckOf(ex.Plan, S(c["check"]));
                PyDict? launch = null;
                Clocked(() =>
                {
                    GenericRows.Build(ex, check, ex.Arm(c["arm"]), (argv, outputs, states) =>
                        launch = new PyDict { ["argv"] = Py.Objs(argv), ["outputs"] = Py.Objs(outputs), ["states"] = states });
                    return 0;
                });
                var want = D(c["want"]);
                string? why;
                if (launch != null)
                {
                    if (launch["states"] is PyDict record)
                        launch["states"] = new PyDict(record) { ["generatorSha256"] = "<the controller>", ["documentSha256"] = "<the controller>" };
                    why = want.ContainsKey("launch") ? SameJson("launch", t.Mask(launch), want["launch"]) : $"launched {PyJson.Dumps(launch["argv"])}, Python accounted {PyJson.Dumps(want["account"])}";
                }
                else if (!want.ContainsKey("account")) why = "accounted, where Python launched " + PyJson.Dumps(want["launch"]);
                else
                {
                    var e = ex.Ledger.Entry(c["check"], c["arm"], null)!;
                    var r = D(PyJson.LoadEvalFile(t.At($"receipts/{Ledger.Key(c["check"], c["arm"], null)}.json")));
                    string note = t.Mask(S(D(r["result"])["note"]))!;
                    if (S(e["status"]) == "blocked(census-unreadable)") note = note.Split('(')[0];   // the rest is each runtime's own words
                    why = SameJson("account", new PyDict { ["status"] = e["status"], ["cause"] = e["cause"], ["note"] = note }, want["account"]);
                }
                if (why != null) return why;
                foreach (var (rel, text) in D(c["written"]))
                {
                    string? got = Lines(t.At(rel));
                    if (got != null && rel.EndsWith(".json", StringComparison.Ordinal))
                    {
                        var doc = D(PyJson.Loads(got));
                        D(doc["generator"])["sha256"] = "<the controller>";
                        got = PyJson.Dumps(doc, indent: 1, ensureAscii: false) + "\n";
                    }
                    why = Same(rel, t.Mask(got), S(text));
                    if (why != null) return why;
                }
                return null;
            });
    }

    static void PrerequisiteCases(PyDict ka)
    {
        Case("evaluate: the prerequisite rule (decided_on_arm, prereq_answer_arms, prereq_block_detail, prereq_satisfied, reuse_blocked_by)", () =>
        {
            using var t = new Temp();
            BuildTree(t, ka["tree"]);
            var ex = ExecutorOver(t);
            return Rows(L(ka["rows"]), row =>
            {
                var check = CheckOf(ex.Plan, S(row["check"]));
                object? pre = row["pre"], arm = row["arm"];
                return Same("decided", Prerequisites.DecidedOnArm(ex, pre, arm), (string?)row["decided"])
                       ?? SameJson("arms", Prerequisites.AnswerArms(ex, pre, arm), row["arms"])
                       ?? Same("detail", Prerequisites.BlockDetail(ex, pre, arm), S(row["detail"]))
                       ?? Same("satisfied", Prerequisites.Satisfied(ex, check, arm), (string?)row["satisfied"])
                       ?? Same("reuse blocked", Prerequisites.ReuseBlockedBy(ex, check, arm), (string?)row["reuseBlocked"]);
            });
        });
    }

    // ---- reuse

    static void ReuseCases(PyDict ka, List<object?> revalidate)
    {
        Case("evaluate: reuse_gap and changed_input", () =>
        {
            using var t = new Temp();
            BuildTree(t, ka["tree"]);
            return Rows(L(ka["rows"]), row =>
            {
                var receipt = D(t.Unmask(row["receipt"]));
                return Same("gap", t.Mask(Revalidate.ReuseGap(receipt)), (string?)row["gap"])
                       ?? Same("changed", t.Mask(Revalidate.ChangedInput(t.Root, receipt)), (string?)row["changed"]);
            });
        });
        foreach (var c in revalidate.Select(D))
            Case($"revalidate: {S(c["name"])}", () =>
            {
                using var t = new Temp();
                BuildTree(t, c["tree"]);
                var plan = D(PyJson.LoadEvalFile(t.At("plan.json")));
                var ledger = new Ledger(t.Root, plan) { RunId = "run-2" };
                var (_, stdout, _) = Clocked(() => Captured(() => { Revalidate.Run(t.Root, plan, ledger); return 0; }));
                return Same("printed", stdout, S(c["stdout"])) ?? Same("ledger.json", t.Mask(Lines(t.At("ledger.json"))), Unpack(c["ledger"]))
                       ?? Same("journal.jsonl", t.Mask(Lines(t.At("journal.jsonl"))), Unpack(c["journal"]));
            });
    }

    // ---- the executor's receipts

    static void ExecutorCases(PyDict ka)
    {
        Case("executor: account_stop, account and finish, each step's receipt, ledger entry and journal line", () =>
        {
            using var t = new Temp();
            BuildTimed(t, ka["tree"]);
            var ex = ExecutorOver(t);
            ex.Preflight = new PyDict
            {
                ["controller"] = new PyDict { ["kind"] = "scripts", ["digest"] = "scripts:" + new string('c', 64) },
                ["probe:contactprobe"] = new PyDict { ["kind"] = "package", ["digest"] = "folder:" + new string('a', 64) },
            };
            var check = CheckOf(ex.Plan, "g.empty");
            string log = t.At("logs/g.empty__baseline.txt"), emit = t.At("outputs/g.empty/baseline.json");
            string? sentence = Same("the budget sentence", ex.BudgetSentence(), S(ka["budgetSentence"]));
            if (sentence != null) return sentence;
            return Clocked(() =>
            {
                foreach (var step in L(ka["steps"]).Select(D))
                {
                    var arm = ex.Arm(step["arm"]);
                    int before = PyText.SplitLines(Lines(t.At("journal.jsonl"))!).Count;
                    switch (S(step["op"]))
                    {
                        case "stop":
                            var res = D(step["res"]);
                            ex.AccountStop(check, arm, (string?)step["part"], new ChildResult("job", null, res.ContainsKey("seconds") ? Py.Float(res["seconds"]) : 0.0,
                                    (string?)res.Get("runId"), 1, (string?)res.Get("stopped"), (bool?)res.Get("treeStopped"),
                                    res.Get("launchedExitCode") == null ? null : unchecked((int)(uint)(long)res["launchedExitCode"]!), (string?)res.Get("refused")),
                                "check g.empty [candidate-01]", step["log"] is true ? log : null);
                            break;
                        case "account":
                            ex.Account(check, arm, S(step["status"]), (string?)step["cause"], S(step["detail"]), extra: step.Get("extra") as PyDict);
                            break;
                        default:
                            ex.Changed.Clear();
                            foreach (var (pid, said) in D(step["changed"])) ex.Changed[pid] = S(said);
                            ex.Checked = ["launch", "acceptance"];
                            ex.Measured.Pop("probe:contactprobe");
                            if (ex.Changed.Count > 0)
                                ex.Measured["probe:contactprobe"] = new PyDict { ["kind"] = "package", ["digest"] = "folder:" + new string('b', 64) };
                            var extra = D(step["extra"]);
                            ex.Finish(check, arm, null, D(check["adapter"]), Strings(t.Unmask(step["argv"])), new ChildResult("job", 0, 1.0, "child-9", 1),
                                "2026-01-01T00:00:00Z", "2026-01-01T00:00:09Z", [emit], log, D(t.Unmask(step["v"])), extra: extra.Count > 0 ? extra : null);
                            break;
                    }
                    string key = S(step["key"]);
                    string? why = Same("receipt", t.Mask(Lines(t.At($"receipts/{key}.json"))), Unpack(step["receipt"]))
                                  ?? SameJson("ledger entry", t.Mask(ex.Ledger.Entries[key]), step["entry"])
                                  ?? SameJson("journal lines", PyText.SplitLines(Lines(t.At("journal.jsonl"))!).Skip(before).Cast<object?>().ToList(), step["journal"]);
                    if (why != null) return $"step '{S(step["name"])}': {why}";
                }
                return null;
            });
        });
        Case("case h1 and h1b: consumed_for, what a child read as strongly as it was stated", () =>
        {
            using var t = new Temp();
            BuildTimed(t, ka["tree"]);
            return Rows(L(ka["consumed"]), row =>
            {
                var ex = ExecutorOver(t, PlanOf(t, row["plan"]));
                return SameJson("consumed", t.Mask(ex.ConsumedFor(ex.Arm(row["arm"]), D(CheckOf(ex.Plan, "g.empty")["adapter"]), identity: row["identity"] as PyDict)), row["want"]);
            });
        });
    }

    // ---- a run file of a shape the rules cannot read

    static void CrashCases(List<object?> cases)
    {
        foreach (var c in cases.Select(D))
            Case($"a file the rules cannot read is refused in one sentence, exit 2, where Python stops with a traceback: {S(c["name"])}", () =>
            {
                using var t = new Temp("b");
                BuildTree(t, c["tree"]);
                if (!Ev.Eq(c["pythonExit"], 1L) || c["pythonTraceback"] is not true) return "Python no longer stops with a traceback here";
                var (exit, _, stderr) = StoreKept(PyPath.Join(t.Base, "runlog"),
                    () => In(t.Root, () => Captured(() => EvalCommands.Run([.. Strings(c["argv"]), t.Root]))));
                return Same("exit", exit.ToString(), "2")
                       ?? (stderr.StartsWith("REFUSED: ", StringComparison.Ordinal) && stderr.TrimEnd().Split('\n').Length == 1
                           && stderr.Contains("a value of a shape its rule cannot read", StringComparison.Ordinal) ? null : $"standard error: {stderr}");
            });
    }
}
