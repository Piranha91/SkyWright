using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using HousecarlCore.Progress;

namespace RunControl;

// The known answers for the swept rows, the derived rows and the bundle stage: evalrun.py's and evalpipeline.py's own
// answers (known-answers/make_known_answers_rows.py), with the order sweep child and the stage's children stood in
// for. No probe and no Python runs here.
public static partial class SelfTestFinalize
{
    public static void RunRows(Action<string, Func<string?>> @case)
    {
        Case = @case;
        PyDict ka;
        using (var s = Assembly.GetExecutingAssembly().GetManifestResourceStream("rows.json")!)
        using (var r = new StreamReader(s, Encoding.UTF8))
            ka = D(PyJson.Loads(r.ReadToEnd()));
        SweepSmallCases(L(D(ka["small"])["rows"]));
        Case("sweep: conflictdiff_refusal, conflictdiff's own reason read from its job log", () => Rows(L(ka["refusal"]), row =>
            Same("refusal", SweepRows.ConflictdiffRefusal((string?)row["text"]), (string?)row["want"])));
        foreach (var c in L(ka["execute"]).Select(D)) ExecuteCase(c);
        BundleStageCases(L(ka["bundleStage"]));
        SuiteCases();
    }

    // Two cases of tests/test_evalrun.py that call a Python function directly, with the suite's own expected values.
    static void SuiteCases()
    {
        Case("case 55 the headline claim on a complete evaluation", () =>
        {
            using var t = new Temp();
            var plan = D(PyJson.Loads("{\"planId\": \"p55\", \"registry\": {\"registryVersion\": \"t\"}, \"explicitDecisions\": [], "
                + "\"states\": {\"notExplored\": []}, \"budgets\": {\"candidatesHeldBack\": []}, \"scope\": {\"unmeasured\": []}, "
                + "\"checks\": [{\"checkId\": \"geometry.x\", \"applicability\": \"yes\", \"applicabilityEvidence\": \"\", \"required\": true, "
                + "\"family\": \"geometry\", \"arms\": [{\"armId\": \"baseline\"}], \"adapter\": {\"kind\": \"probe-json\"}}], "
                + "\"arms\": [{\"armId\": \"baseline\", \"kind\": \"baseline\", \"candidateFile\": null, \"loadedInGameNote\": \"\"}]}"));
            var receipts = D(PyJson.Loads("{\"geometry.x__baseline\": {\"result\": {\"findings\": 1}, \"validation\": {\"accepted\": true}}}"));
            return Rows(new[] { ("finding", "findings"), ("no-finding", "no-findings-in-completed-checks") }.Select(x => (object?)new PyDict { ["name"] = x.Item1, ["claim"] = x.Item2 }), row =>
            {
                t.Write("ledger.json", PyJson.Utf8("{\"planId\": \"p55\", \"entries\": {\"geometry.x__baseline\": {\"checkId\": \"geometry.x\", \"armId\": \"baseline\", "
                    + "\"part\": null, \"status\": \"completed\", \"detector\": \"" + S(row["name"]) + "\"}}}"));
                var ev = D(Disposition.Build(t.Root, plan, new Ledger(t.Root, plan), receipts, new PyDict(), new PyDict())["evaluation"]);
                return Same("complete", Ev.Str(ev["complete"]), "True") ?? Same("claim", S(ev["claim"]), S(row["claim"]));
            });
        });
        // case 566, revised when the names read moved in process (step 4): the third choice is houseCARL's records tool
        // compiled in, no longer the build's server program
        Case("case 566 the reader's default order: the flag, the environment, houseCARL in process", () =>
        {
            string? before = Environment.GetEnvironmentVariable(BundleSteps.ReaderVariable);
            try
            {
                Environment.SetEnvironmentVariable(BundleSteps.ReaderVariable, "  E:/env-reader.exe --stdio  ");
                var flag = BundleSteps.DefaultReaderCmd("F:/flag-reader.exe");
                var env = BundleSteps.DefaultReaderCmd(null);
                Environment.SetEnvironmentVariable(BundleSteps.ReaderVariable, "   ");
                var inProcess = BundleSteps.DefaultReaderCmd("");
                return Same("the flag first", $"{flag.command} {flag.source}", "F:/flag-reader.exe --reader-cmd")
                       ?? Same("then the environment, stripped", $"{env.command} {env.source}", $"E:/env-reader.exe --stdio {BundleSteps.ReaderVariable}")
                       ?? Same("then houseCARL in process", $"{inProcess.command} {inProcess.source}", $"{InProcessReader.Command} {InProcessReader.Source}");
            }
            finally { Environment.SetEnvironmentVariable(BundleSteps.ReaderVariable, before); }
        });
    }

    static readonly Regex MtimeText = new("\"mtimeUtc\": \"[^\"]+\"");

    static void SweepSmallCases(List<object?> rows)
    {
        Case("sweep: sweep_parts, sweep_arms_allowed and sweep_needed over ledger states", () => Rows(rows, row =>
        {
            using var t = new Temp();
            BuildTimed(t, row["tree"]);
            var ex = ExecutorOver(t);
            var sweep = SweepRows.Planned(ex.Plan);
            var allowed = SweepRows.ArmsAllowed(ex, sweep);
            var parts = new PyDict();
            foreach (var arm in L(ex.Plan["arms"]).Select(D)) parts[S(arm["armId"])] = Py.Objs(SweepRows.SweepParts(ex, arm));
            return SameJson("allowed", new PyDict(allowed.Select(kv => new KeyValuePair<string, object?>(kv.Key, kv.Value))), row["allowed"])
                   ?? Same("needed, every arm allowed", SweepRows.Needed(ex, sweep, []).ToString(), ((bool)row["neededAll"]!).ToString())
                   ?? Same("needed", SweepRows.Needed(ex, sweep, allowed).ToString(), ((bool)row["needed"]!).ToString())
                   ?? SameJson("parts", parts, row["parts"]);
        }));
    }

    // The folder brought to a recorded state: files it does not list removed, files whose bytes differ written.
    static void ApplyState(Temp t, object? tree)
    {
        var rows = L(tree).Select(D).ToList();
        var listed = rows.Select(r => S(r["path"])).ToHashSet(StringComparer.Ordinal);
        foreach (string p in Directory.GetFiles(t.Root, "*", SearchOption.AllDirectories))
            if (!listed.Contains(PyOrder.Fwd(Path.GetRelativePath(t.Root, p)))) File.Delete(p);
        foreach (var row in rows.Where(r => !r.ContainsKey("planSha")))
        {
            byte[] want = RowBytes(row, t);
            string p = t.At(S(row["path"]));
            if (!File.Exists(p) || !File.ReadAllBytes(p).AsSpan().SequenceEqual(want)) t.Write(S(row["path"]), want);
        }
    }

    static ChildResult ResultOf(PyDict res) => new("ordersweep", res.Get("exitCode") == null ? null : unchecked((int)(uint)(long)res["exitCode"]!),
        res.ContainsKey("seconds") ? Py.Float(res["seconds"]) : 0.0, (string?)res.Get("runId"), 1, (string?)res.Get("stopped"), (bool?)res.Get("treeStopped"),
        res.Get("launchedExitCode") == null ? null : unchecked((int)(uint)(long)res["launchedExitCode"]!), (string?)res.Get("refused"));

    // One recorded run folder and the invocations of execute over it, the sweep child stood in for.
    static void ExecuteCase(PyDict c)
    {
        Case($"sweep and derived rows: {S(c["name"])}", () =>
        {
            using var t = new Temp();
            BuildTimed(t, c["tree"]);
            int n = 1;
            foreach (var step in L(c["steps"]).Select(D))
            {
                n++;
                if (step["edits"] != null) ApplyState(t, step["edits"]);
                var plan = D(PyJson.LoadEvalFile(t.At("plan.json")));
                var programs = D(D(plan["toolIdentity"])["programs"]);
                string? raised = null;
                List<object?>? argv = null;
                PyDict? env = null;
                var measured = new List<object?>();
                int advanced = 0, children = 0, stops = 0;
                bool ran = false;
                var (_, stdout, _) = Clocked(() => Captured(() =>
                {
                    var ledger = new Ledger(t.Root, plan);
                    ledger.OpenInvocation("run", $"run-{n}", null);
                    var ex = new Executor(t.Root, plan, ledger, resume: false, budgetMinutes: step["budget"]) { ParentRunId = $"parent-{n}" };
                    ex.Preflight = D(Py.Copy(programs));
                    ex.MeasureProgram = pid =>
                    {
                        var now = new PyDict(D(programs[pid]));
                        if (ran && Ev.Eq(step["drift"], pid)) now["digest"] = "folder:" + new string('d', 64);   // it changed while the sweep ran
                        measured.Add(pid);
                        return now;
                    };
                    ex.RunJob = job =>
                    {
                        if (step["result"] == null) throw new InvalidOperationException("a sweep child was started where Python started none");
                        argv = job.Argv.Skip(2).Cast<object?>().ToList();
                        env = new PyDict((job.Env ?? new Dictionary<string, string>()).Select(kv => new KeyValuePair<string, object?>(kv.Key, kv.Value)));
                        foreach (var f in L(step["wrote"]).Select(D)) t.Write(S(f["path"]), PyJson.Utf8(t.Unmask(Unpack(f["text"])!)));
                        ran = true;
                        return ResultOf(D(step["result"]));
                    };
                    try { EvaluateRun.Execute(ex, () => { advanced++; }); }
                    catch (ToolChanged tc) { raised = tc.Message.Replace(ControllerLabelHere, ControllerLabelPython); }
                    (children, stops) = (ex.Children, ex.DeadlineStops);
                    return 0;
                }));
                if (argv != null && argv.IndexOf("--deadline-seconds") is var at and >= 0) argv[at + 1] = "<seconds>";
                string Text(string rel) => MtimeText.Replace(t.Mask(Lines(t.At(rel)))!, "\"mtimeUtc\": \"<mtime>\"");
                var want = D(step["want"]);
                string? why = Same("raised", raised, (string?)step["raised"])
                              ?? SameJson("the sweep's command line", t.Mask(argv), step["argv"])
                              ?? SameJson("the sweep's environment", env, step["env"])
                              ?? SameJson("programs measured", measured, step["measured"])
                              ?? Same("rows counted", advanced.ToString(), Ev.Str(step["advanced"]))
                              ?? Same("children", $"{children} {stops}", $"{Ev.Str(step["children"])} {Ev.Str(step["deadlineStops"])}")
                              ?? Same("printed", t.Mask(stdout), S(step["stdout"]))
                              ?? Same("ledger.json", Text("ledger.json"), Unpack(want["ledger"]))
                              ?? Same("journal.jsonl", Text("journal.jsonl"), Unpack(want["journal"]));
                if (why == null)
                {
                    var keys = Directory.GetFiles(t.At("receipts"), "*.json").Select(p => Path.GetFileNameWithoutExtension(p)).ToList();
                    why = Same("receipts written", string.Join(" ", PyOrder.SortedByCodePoint(keys)), string.Join(" ", PyOrder.SortedByCodePoint(D(want["receipts"]).Keys)));
                    foreach (var (key, text) in D(want["receipts"]))
                        why ??= Same($"receipt {key}", Text($"receipts/{key}.json"), Unpack(text));
                    var files = Directory.GetFiles(t.Root, "*", SearchOption.AllDirectories).Select(p => PyOrder.Fwd(Path.GetRelativePath(t.Root, p)))
                        .Where(rel => !rel.StartsWith("receipts/", StringComparison.Ordinal));
                    why ??= Same("files", string.Join(" ", PyOrder.SortedByCodePoint(files)), string.Join(" ", Strings(want["files"])));
                }
                if (why != null) return $"step '{S(step["name"])}': {why}";
            }
            return null;
        });
    }

    // ---- the bundle stage, its three children stood in for

    static void BundleStageCases(List<object?> cases)
    {
        foreach (var c in cases.Select(D))
            Case($"bundle stage: {S(c["name"])}", () =>
            {
                using var t = new Temp();
                string run = t.At("run");
                t.Write("run/evaluation/disposition.json", PyJson.Utf8("{\"evaluation\": {\"claim\": \"findings\", \"complete\": false}, \"ledgerCounts\": {\"completed\": 3}}"));
                if (c["exists"] is true) Directory.CreateDirectory(t.At("run/bundle/evidence"));
                var entry = D(PyJson.Loads("{\"schema\": 1, \"id\": \"m\", \"inPlace\": {\"instance\": \"I:/i\", \"profile\": \"Default\", \"game\": \"G:/g\"}, \"selection\": {\"mods\": [\"S\"]}}"));
                if (c["modNames"] != null) entry["modNames"] = c["modNames"];
                var machine = c["machine"] as PyDict ?? D(PyJson.Loads("{\"readerCmd\": \"\\\"R:/reader.exe\\\" --stdio\"}"));
                var ctx = new RunContext(entry, machine, run);
                var started = new List<object?>();
                var exits = L(c["exits"]);
                var saved = ChildLaunch.RunJob;
                string? before = Environment.GetEnvironmentVariable("HOUSECARL_RECORD_READER_CMD");
                Environment.SetEnvironmentVariable("HOUSECARL_RECORD_READER_CMD", (string?)c["envReader"]);
                string? refusal = null, deadlines = null;
                (PyDict? bound, List<string> outputs) got = (null, []);
                ChildLaunch.RunJob = (op, dir, deadline, job) =>
                {
                    if (deadline != null || op != "runcontrol-" + job.Id) deadlines = $"{op} was started with a deadline or under another name";
                    started.Add(new PyDict { ["op"] = job.Id, ["dir"] = t.Mask(dir), ["argv"] = t.Mask(job.Argv.Skip(1).Cast<object?>().ToList()), ["more"] = new List<object?>() });
                    if (job.Id == "bundle" && c["log"] != null) t.Write(Path.GetRelativePath(t.Root, PyPath.Join(dir, "bundle.txt")), PyJson.Utf8("$ bundle\n(exit 0, 1.0s)\n\n" + S(c["log"])));
                    object? code = exits[started.Count - 1];
                    return new ChildResult(job.Id, code == null ? null : (int)(long)code, 0.1, null, 1);
                };
                try { got = StageBundle.Bundle(ctx, new PyDict(), t.At("run/bundle-stage")); }
                catch (Refusal r) { refusal = t.Mask(r.Message); }
                finally
                {
                    ChildLaunch.RunJob = saved;
                    Environment.SetEnvironmentVariable("HOUSECARL_RECORD_READER_CMD", before);
                }
                string instance = t.At("run/names-instance");
                object? listed = !Directory.Exists(instance) ? null
                    : PyOrder.SortedByCodePoint(Directory.GetFileSystemEntries(instance, "*", SearchOption.AllDirectories)
                        .Select(p => PyOrder.Fwd(Path.GetRelativePath(run, p)))).Cast<object?>().ToList();
                return deadlines ?? Same("refusal", refusal, (string?)c["refusal"])
                       ?? SameJson("children", started, c["started"])
                       ?? Same("bundle.json", t.Mask(Lines(t.At("run/bundle-stage/bundle.json"))), (string?)c["record"])
                       ?? SameJson("the names instance", listed, c["instance"])
                       ?? (refusal != null ? null
                           : SameJson("bound", got.bound, c["bound"]) ?? SameJson("outputs", t.Mask(got.outputs.Cast<object?>().ToList()), c["outputs"]));
            });
    }
}
