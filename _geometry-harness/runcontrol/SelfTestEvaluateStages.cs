using HousecarlCore.Progress;
using SkyWright.Engine;

namespace RunControl;

// The evaluate and scenes stages, the evaluation's own child stood in for: which command the stage starts,
// how long it holds the machine slot, when it starts it again, and what it leaves for the next run.
public static partial class SelfTest
{
    // What `run` or `resume` would leave: a ledger with one entry per status given, and one more invocation in runIds.
    static void StandInLedger(string run, params string[] statuses)
    {
        string file = PyPath.Join(run, "ledger.json");
        var doc = File.Exists(file) ? D(PyJson.ReadPipelineFile(file)) : new PyDict { ["runIds"] = new List<object?>() };
        L(doc["runIds"]).Add(new PyDict { ["runId"] = $"r{L(doc["runIds"]).Count + 1}" });
        var entries = new PyDict();
        for (int i = 0; i < statuses.Length; i++) entries[$"c{i}__baseline"] = new PyDict { ["status"] = statuses[i] };
        doc["entries"] = entries;
        PyJson.WriteEvalFile(file, doc);
    }

    // The pipeline through scenes over an in-place world, with `evaluate` standing in for each evaluation child:
    // (the evaluation children started, what the run printed).
    static (List<ChildJob> jobs, string outcome) EvaluateStages(Temp t, PyDict entry, Func<ChildJob, int, ChildResult> evaluate, PyDict? machine = null,
        string through = "scenes", Func<ChildJob, ChildResult?>? later = null)
    {
        var saved = ChildLaunch.RunJob;
        var jobs = new List<ChildJob>();
        try
        {
            var res = new RunContext(entry, new PyDict(), t.At("run")).Resolve();
            var launched = new List<string>();
            ChildLaunch.RunJob = (_, _, _, job) =>
            {
                if (job.Id == "plan") return StandInPlan(job);
                if (later?.Invoke(job) is { } stood) return stood;
                if (!job.Id.StartsWith("evaluate-", StringComparison.Ordinal)) return StandInScopeprobe(res, job, launched);
                jobs.Add(job);
                return evaluate(job, jobs.Count);
            };
            var driver = new Driver { OpenRun = QuietRun, SetsRunLogFolder = false, MakeContext = a => new RunContext(entry, machine ?? new PyDict(), a.Out!) };
            var (exit, stdout, stderr) = Drive(driver, new PipelineArgs(Entry: "e.json", Out: t.At("run"), Through: through));
            return (jobs, $"exit {exit}: {stderr}{stdout}");
        }
        finally { ChildLaunch.RunJob = saved; }
    }

    const string ScenesLine = "scenes: refused, not blocking: overlap scene acquisition is not built in this version (overlapprobe --geometry-manifest "
                              + "per arm, attribution and triage); no incident catalogue was made. The report and reel do not read it.";

    static void EvaluateStageCases()
    {
        var saved = ChildLaunch.RunJob;
        static ChildResult Done(ChildJob job) => new(job.Id, 0, 0.1, null, 1);

        Case("case 41 unbuilt scene stage is a stated refusal", () =>
        {
            try { StageEvaluate.Scenes(null!, new PyDict(), "x"); }
            catch (NonBlockingRefusal r)
            {
                return r.Message.StartsWith("overlap scene acquisition is not built in this version", StringComparison.Ordinal) ? null : r.Message;
            }
            return "the scenes stage did not refuse";
        });
        Case("case 61 evaluate stops itself at its budget with the slot held that long", () =>
        {
            using var t = new Temp();
            var seen = new List<(string op, string dir, double? deadline, ChildJob job)>();
            string run = t.At("out/evaluation");
            Directory.CreateDirectory(run);
            File.WriteAllText(PyPath.Join(run, "plan.json"), "{\"budgets\": {\"minutes\": 30}}");
            double before = ChildRunner.Now();
            ChildLaunch.RunJob = (op, dir, deadline, job) =>
            {
                seen.Add((op, dir, deadline, job));
                StandInLedger(run, "completed", "skipped-user", "rejected(exit-nonzero)");
                return Done(job);
            };
            (PyDict? bound, List<string> outputs) got;
            try
            {
                var machine = new PyDict
                {
                    ["heavyPrefix"] = Py.Objs(["python", "idle_lock.py", "hold", "--lock", "{lock}", "--purpose", "{purpose}", "--max-min", "180",
                        "--wait-timeout-min", "20", "--slot-wait-min", "60"]),
                    ["lockDir"] = t.At("locks"),
                };
                var ctx = new RunContext(new PyDict { ["evaluation"] = Json("{\"budgetMinutes\": 30}") }, machine, t.At("out"), "m");
                got = StageEvaluate.Evaluate(ctx, new PyDict(), t.At("out/evaluate"));
            }
            finally { ChildLaunch.RunJob = saved; }
            if (seen.Count != 1) return $"{seen.Count} children were started, not one";
            var argv = seen[0].job.Argv.ToList();
            string After(string flag) => argv[argv.IndexOf(flag) + 1];
            double minutes = (seen[0].deadline!.Value - before) / 60;
            var record = D(PyJson.ReadPipelineFile(t.At("out/evaluate/evaluate.json")));
            return Same("op and job", $"{seen[0].op} {seen[0].job.Id}", "runcontrol-evaluate-01 evaluate-01")
                   ?? Same("log folder", seen[0].dir, t.At("out/evaluate/commands"))
                   ?? Same("hold", After("--max-min"), "40") ?? Same("purpose", After("--purpose"), "runcontrol m evaluate")
                   ?? Same("lock", After("--lock"), PyOrder.Fwd(t.At("locks/HEAVY-LOCK-m-evaluate-01")))
                   ?? Same("the command", string.Join(" ", argv.Skip(argv.IndexOf("--") + 1)), $"{Environment.ProcessPath} run --run {PyOrder.Fwd(run)}")
                   // the outer deadline sits past the slot's own waits and hold: 60 + 20 + 40 + the margin
                   ?? (minutes > 124.9 && minutes < 125.2 ? null : $"the outer deadline is {minutes:0.00} min from the launch, not 125")
                   ?? Same("evaluate.json", PyJson.Dumps(record),
                           "{\"history\": [{\"attempt\": 1, \"verb\": \"run\", \"exitCode\": 0, \"stopped\": null, \"before\": {}, "
                           + "\"after\": {\"completed\": 1, \"skipped-user\": 1, \"rejected\": 1}}], \"ledgerCounts\": {\"completed\": 1, \"skipped-user\": 1, \"rejected\": 1}}")
                   ?? Same("bound and outputs", PyJson.Dumps(got.bound) + " " + string.Join(" ", got.outputs),
                           $"{{\"plan\": \"{Hashing.Sha256File(PyPath.Join(run, "plan.json"))}\"}} {t.At("out/evaluate/evaluate.json")}");
        });
        Case("evaluate: a stopped child is started again as resume while the ledger still moves, five times at most; a child that failed is not", () =>
        {
            string Try(Func<ChildJob, int, string, ChildResult> child, out List<string> verbs)
            {
                using var t = new Temp();
                string run = t.At("out/evaluation");
                Directory.CreateDirectory(run);
                File.WriteAllText(PyPath.Join(run, "plan.json"), "{\"budgets\": {\"minutes\": 90}}");
                var started = new List<string>();
                ChildLaunch.RunJob = (_, _, _, job) => { started.Add(job.Argv[1]); return child(job, started.Count, run); };
                verbs = started;
                try
                {
                    StageEvaluate.Evaluate(new RunContext(new PyDict(), new PyDict(), t.At("out"), "m"), new PyDict(), t.At("out/evaluate"));
                    return "accepted";
                }
                catch (Refusal r) { return r.Message.Replace(PyOrder.Fwd(t.Root), "<T>"); }
                finally { ChildLaunch.RunJob = saved; }
            }
            ChildResult Stopped(ChildJob job) => new(job.Id, null, 1.0, null, 1, "deadline", true);
            string moving = Try((job, n, run) => { StandInLedger(run, Enumerable.Repeat("completed", n).ToArray()); return Stopped(job); }, out var movingVerbs);
            string still = Try((job, n, run) => { StandInLedger(run, "completed"); return Stopped(job); }, out var stillVerbs);
            string failed = Try((job, n, run) => { StandInLedger(run, "completed"); return new ChildResult(job.Id, 2, 0.1, null, 1); }, out var failedVerbs);
            string killed = Try((job, n, run) => new ChildResult(job.Id, -1, 0.1, null, 1), out _);
            string recovered = Try((job, n, run) => { StandInLedger(run, Enumerable.Repeat("completed", n).ToArray()); return n < 3 ? Stopped(job) : Done(job); }, out var recoveredVerbs);
            return Same("a ledger that keeps moving", string.Join(" ", movingVerbs), "run resume resume resume resume")
                   ?? Same("its refusal", moving, "evalrun resume ended with exit None (stopped: deadline); read <T>/out/evaluate/commands and `evalrun status`.")
                   ?? Same("a ledger that stands still", string.Join(" ", stillVerbs), "run resume")
                   ?? Same("a child that failed", string.Join(" ", failedVerbs) + ": " + failed,
                           "run: evalrun run ended with exit 2 (stopped: None); read <T>/out/evaluate/commands and `evalrun status`.")
                   ?? Same("a child that was killed", killed, "evalrun run ended with exit 4294967295 (stopped: None); read <T>/out/evaluate/commands and `evalrun status`.")
                   ?? Same("a child that finishes on its third start", string.Join(" ", recoveredVerbs) + ": " + recovered, "run resume resume: accepted");
        });
        Case("case 67 an evaluation stopped at its budget is resumed by the next run, not reused", () =>
        {
            using var t = new Temp();
            var (inst, game) = World(t, ShadowMods, ShadowEnabled, ShadowPlugins);
            var entry = InPlaceEntry(inst, game, ["Sel"], ["geometry.object-overlap"]);
            entry["evaluation"] = Json("{\"select\": [\"geometry.object-overlap\"], \"depth\": \"standard\", \"budgetMinutes\": 90}");
            string run = t.At("run/evaluation");
            var (jobs, first) = EvaluateStages(t, entry, (job, n) => { StandInLedger(run, "completed", "stopped(deadline)", "skipped-budget", "skipped-user"); return Done(job); });
            const string open = "evaluate: refused, not blocking: evaluation stopped at its budget of 90 min with 2 rows open (1 stopped, 1 not started); "
                                + "run again to resume them (each run gets a fresh budget), or raise the entry's budgetMinutes.";
            if (!first.StartsWith("exit 0") || !first.Contains(open) || !first.Contains(ScenesLine)) return "the first run: " + first;
            var status = D(PyJson.ReadPipelineFile(t.At("run/status.json")));
            string? why = Same("status", $"{status["status"]} {status["through"]} {L(status["refusals"]).Count}", "incomplete scenes 2")
                          ?? Same("the first run's command", string.Join(" ", jobs[0].Argv.Skip(1)), $"run --run {PyOrder.Fwd(run)}");
            if (why != null) return why;
            // the next run: evaluate is not reused, and its child is `resume`; a budget the entry changed is this run's limit
            var raised = new PyDict(entry) { ["evaluation"] = Json("{\"select\": [\"geometry.object-overlap\"], \"depth\": \"standard\", \"budgetMinutes\": 120}") };
            var (jobs2, second) = EvaluateStages(t, raised, (job, n) => { StandInLedger(run, "completed", "stopped(deadline)", "completed", "skipped-user"); return Done(job); });
            if (jobs2.Count != 1) return "the second run started no evaluation: " + second;
            why = Same("the second run's command", string.Join(" ", jobs2[0].Argv.Skip(1)), $"resume --run {PyOrder.Fwd(run)} --budget-minutes 120")
                  ?? (second.Contains("plan: reused (inputs and outputs unchanged)") ? null : "a budget-only change planned again: " + second)
                  ?? (second.Contains("evaluation stopped at its budget of 120 min with 1 rows open (1 stopped, 0 not started)") ? null : "the second run: " + second);
            if (why != null) return why;
            var (jobs3, third) = EvaluateStages(t, raised, (job, n) => { StandInLedger(run, "completed", "completed", "completed", "skipped-user"); return Done(job); });
            if (jobs3.Count != 1 || !third.Contains("evaluate: accepted in ")) return "the third run: " + third;
            var (jobs4, fourth) = EvaluateStages(t, raised, (job, n) => Done(job));
            return jobs4.Count == 0 && fourth.Contains("evaluate: reused (inputs and outputs unchanged)")
                ? null : "a finished evaluation was not reused: " + fourth;
        });
        Case("stages: evaluate and scenes run over an entry carrying every presentation key, and read none", () =>
        {
            using var t = new Temp();
            var (inst, game) = World(t, ShadowMods, ShadowEnabled, ShadowPlugins);
            var entry = InPlaceEntry(inst, game, ["Sel"], ["geometry.object-overlap"]);
            entry["modNames"] = Json("{\"Sel.esp\": \"The Selection\"}");
            entry["nexusNames"] = Json("{\"Sel.esp\": \"Sel on Nexus\"}");
            entry["patchSearch"] = "X:/patch-search.json";
            entry["report"] = Json("{\"mode\": \"full\", \"maxPages\": 30}");
            entry["captureSize"] = L(PyJson.Loads("[1920, 1080]"));
            string run = t.At("run/evaluation");
            var (jobs, first) = EvaluateStages(t, entry, (job, n) => { StandInLedger(run, "completed", "skipped-user"); return Done(job); });
            if (jobs.Count != 1 || !first.Contains("evaluate: accepted in ") || !first.Contains(ScenesLine)) return first;
            var ctx = new RunContext(entry, new PyDict(), t.At("run"));
            string? why = Rows(new[] { "evaluate", "scenes" }, s => Refuses(() => ctx.Entry.Presentation(s, "modNames"), s));
            if (why != null) return why;
            // other presentation values: neither stage is bound to them, so neither runs again
            var renamed = new PyDict(entry) { ["modNames"] = Json("{\"Sel.esp\": \"Renamed\"}"), ["report"] = Json("{\"mode\": \"short\"}") };
            renamed.Pop("captureSize");
            var (again, second) = EvaluateStages(t, renamed, (job, n) => Done(job));
            return again.Count == 0 && second.Contains("evaluate: reused (inputs and outputs unchanged)")
                ? null : "a presentation change ran a stage again: " + second;
        });
        Case("stages: bundle runs over an entry carrying every presentation key, reads modNames only, and is bound to it", () =>
        {
            using var t = new Temp();
            var (inst, game) = World(t, ShadowMods, ShadowEnabled, ShadowPlugins);
            var entry = InPlaceEntry(inst, game, ["Sel"], ["geometry.object-overlap"]);
            entry["modNames"] = Json("{\"Sel.esp\": \"The Selection\"}");
            entry["nexusNames"] = Json("{\"Sel.esp\": \"Sel on Nexus\"}");
            entry["patchSearch"] = "X:/patch-search.json";
            entry["report"] = Json("{\"mode\": \"full\", \"maxPages\": 30}");
            entry["captureSize"] = L(PyJson.Loads("[1920, 1080]"));
            string run = t.At("run/evaluation");
            var machine = new PyDict { ["readerCmd"] = "R:/reader.exe" };
            var steps = new List<ChildJob>();
            // finalize, bundle and verify-bundle stood in for: what each leaves for the stage to read
            ChildResult? Later(ChildJob job)
            {
                if (job.Id is not ("finalize" or "bundle" or "verify-bundle")) return null;
                steps.Add(job);
                if (job.Id == "finalize") File.WriteAllText(PyPath.Join(run, "disposition.json"), "{\"evaluation\": {\"claim\": \"incomplete\", \"complete\": false}, \"ledgerCounts\": {}}");
                if (job.Id == "bundle")
                {
                    string dir = job.Argv[job.Argv.ToList().IndexOf("--out") + 1];
                    Directory.CreateDirectory(dir);
                    File.WriteAllText(PyPath.Join(dir, "manifest.json"), "{}");
                }
                return Done(job);
            }
            ChildResult Evaluated(ChildJob job, int n) { StandInLedger(run, "completed", "skipped-user"); return Done(job); }
            var (_, first) = EvaluateStages(t, entry, Evaluated, machine, "bundle", Later);
            if (!first.StartsWith("exit 0") || !first.Contains("bundle: accepted in ")) return "the first run: " + first;
            var bundle = steps.FirstOrDefault(j => j.Id == "bundle")?.Argv.ToList() ?? [];
            string? why = Same("the stage's children", string.Join(" ", steps.Select(j => j.Id)), "finalize bundle verify-bundle")
                          ?? Same("the name the bundle is told", bundle.IndexOf("--mod-name") is var at and >= 0 ? bundle[at + 1] : "(no --mod-name)", "Sel.esp=The Selection")
                          ?? Same("the reader", bundle[bundle.IndexOf("--reader-cmd") + 1], "R:/reader.exe");
            if (why != null) return why;
            var ctx = new RunContext(entry, machine, t.At("run"));
            why = Rows(new[] { "nexusNames", "patchSearch", "report", "captureSize" }, key => Refuses(() => ctx.Entry.Presentation("bundle", key), key));
            if (why != null) return why;
            if (!PyValue.Eq(ctx.Entry.Presentation("bundle", "modNames"), entry["modNames"])) return "bundle could not read modNames";
            // a presentation key the stage does not read: the stage is not bound to it and is reused
            steps.Clear();
            var other = new PyDict(entry) { ["report"] = Json("{\"mode\": \"short\"}"), ["patchSearch"] = "X:/other.json" };
            var (_, second) = EvaluateStages(t, other, (job, n) => Done(job), machine, "bundle", Later);
            if (steps.Count > 0 || !second.Contains("bundle: reused (inputs and outputs unchanged)")) return "another presentation key ran the bundle stage again: " + second;
            // the key it reads: the stage runs again, and finds the bundle it made earlier
            var renamed = new PyDict(entry) { ["modNames"] = Json("{\"Sel.esp\": \"Renamed\"}") };
            var (_, third) = EvaluateStages(t, renamed, (job, n) => Done(job), machine, "bundle", Later);
            return third.Contains("exists from an earlier bundle; move it aside rather than overwrite it.") && third.Contains("evaluate: reused (inputs and outputs unchanged)")
                ? null : "a changed modNames did not run the bundle stage again: " + third;
        });
    }
}
