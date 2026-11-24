using HousecarlCore.Progress;

namespace RunControl;

/// <summary>What `run`, `arms` and `status` were asked: every path absolute.</summary>
public sealed record PipelineArgs(string? Entry = null, string? Out = null, string? Machine = null, string? Through = null,
                                  string? ReusePreparationFrom = null);

/// <summary>The pipeline driver: every stage not yet accepted, in order, each a receipt in RUN/stages.json bound to
/// its inputs and outputs. An accepted stage whose bindings are unchanged is reused; a changed one runs again.</summary>
public sealed class Driver
{
    /// <summary>Every stage of the pipeline, in order. This controller runs census to bundle; the stages from report-plan
    /// on belong to the AI-driven evaluation, which starts this controller for the first ones.</summary>
    public static readonly string[] Stages =
    [
        "census", "prepare", "scope", "plan", "evaluate", "records", "scenes", "bundle", "precedent",
        "report-plan", "capture", "image-observations", "report", "reel", "deliver",
    ];

    /// <summary>Leaves room under Windows' 260-character limit for the probes' own file names.</summary>
    public const int MaxPath = 240;

    public Dictionary<string, StageBody> Bodies { get; init; } = StageBodies.Built();
    public Func<PipelineArgs, RunContext> MakeContext { get; init; } = a => new RunContext(a.Entry!, a.Machine, a.Out!);
    /// <summary>Opens the run-log record for a run of the given mode (the entry id).</summary>
    public Func<string, Run> OpenRun { get; init; } = mode => RunLog.Begin("evalpipeline", new RunOptions { Mode = mode, StageSource = StageSource.Counted });
    /// <summary>Whether the run-log folder is set for this process and its children (off in tests).</summary>
    public bool SetsRunLogFolder { get; init; } = true;

    static int At(string stage) => Array.IndexOf(Stages, stage);

    static void WriteStatus(string outDir, PyDict status) => PyJson.WritePipelineFile(PyPath.Join(outDir, "status.json"), status);

    /// <summary>The budget an existing plan froze, or null when there is no plan yet.</summary>
    public static object? PlannedBudget(string outDir)
    {
        string f = PyPath.Join(outDir, "evaluation", "plan.json");
        return File.Exists(f) ? Py.DictOrEmpty(Py.D(PyJson.ReadPipelineFile(f)).Get("budgets")).Get("minutes") : null;
    }

    /// <summary>The entry's budget when it differs from the existing plan's, else null: a budget is a limit on an
    /// invocation, so changing only it keeps the plan, and evaluate continues under the new limit.</summary>
    public static object? BudgetDiffers(Entry entry, string outDir)
    {
        object? planned = PlannedBudget(outDir);
        object? want = Py.DictOrEmpty(entry.Get("evaluation")).Get("budgetMinutes");
        return planned != null && want != null && Py.Float(want) != Py.Float(planned) ? want : null;
    }

    public int Run(PipelineArgs a)
    {
        string outDir = PyPath.Str(a.Out!);
        string resolved = PyOrder.Fwd(PyPath.Resolve(outDir));
        if (resolved.Length > MaxPath - 80)
            throw new Refusal($"the run folder path is {resolved.Length} characters; probes write deeper files and "
                              + "Windows refuses past 260, so choose a shorter --out.");
        Directory.CreateDirectory(outDir);
        string runlog = PyOrder.Fwd(PyPath.Join(outDir, "runlog"));
        if (SetsRunLogFolder && string.IsNullOrEmpty(Environment.GetEnvironmentVariable("HOUSECARL_RUNLOG_DIR")))
            Environment.SetEnvironmentVariable("HOUSECARL_RUNLOG_DIR", runlog);
        var ctx = MakeContext(a);
        if (SetsRunLogFolder && ctx.InPlace != null)
            Environment.SetEnvironmentVariable("HOUSECARL_RUNLOG_DIR", runlog);   // a manual run's log stays in its work folder
        var ledger = new StageLedger(outDir);
        string through = a.Through ?? Stages[^1];
        var refusals = new List<object?>();   // non-blocking refusals: the run goes on, and its final status still names each one
        using var run = OpenRun(ctx.Id);
        var res = ctx.Resolve();
        PyJson.WritePipelineFile(PyPath.Join(outDir, "arms.json"), Py.L(res["arms"]).Select(arm => (object?)new PyDict(Py.D(arm))).ToList());
        if (At(through) >= At("report-plan"))
        {
            string why = $"the run goes through {through}, and this controller runs census to bundle only; the stages from report-plan on "
                         + "belong to the AI-driven evaluation, which runs this controller for census to bundle itself on the same run folder, "
                         + "so give --through bundle or an earlier stage here.";
            WriteStatus(outDir, new PyDict { ["status"] = "incomplete", ["stoppedAt"] = Stages[0], ["sentence"] = why, ["at"] = PyText.UtcNowPlus() });
            Report.Error($"REFUSED: {why}");
            return Exit.Refused;
        }
        var seeded = new PyDict();
        string? reusedFrom = null;
        if (ctx.InPlace != null)
        {
            var arms = Py.L(res["arms"]).Select(Py.D).ToList();
            var key = PrepReuse.Key(ctx.Instance, ctx.Profile, ctx.Game, Py.Strs(res["selection"]), arms, PrepReuse.ToolsDigest(),
                                    ScopeRules.DisabledPluginFiles(ctx, res));
            if (a.ReusePreparationFrom != null)
            {
                var (found, why) = PrepReuse.Find(a.ReusePreparationFrom, outDir, key, arms);
                if (found != null)
                {
                    seeded = PrepReuse.Seed(found, outDir);
                    reusedFrom = found;
                    Report.Line($"preparation: reused from {PyPath.Name(Path.GetDirectoryName(found)!)} (same profile, selection, files and probe build)");
                }
                else Report.Line($"preparation: not reused: {why}");
            }
            PyJson.WritePipelineFile(PyPath.Join(outDir, PrepReuse.KeyFile), key);
        }
        long? records = DiskForecast.EvaluatedRecords(res);
        string? forecastLine = null;
        long? sizeBefore = null;
        var todoStages = Stages.Take(At(through) + 1).ToList();
        run.Stage("stages", "pipeline stages", todoStages.Count, "stages");
        foreach (string name in todoStages)
        {
            if (!Bodies.TryGetValue(name, out var body))
            {
                Report.Line($"{name}: not built in this version; the run stops here.");
                WriteStatus(outDir, new PyDict { ["status"] = "incomplete", ["stoppedAt"] = name, ["at"] = PyText.UtcNowPlus() });
                return Exit.Refused;
            }
            string sdir = PyPath.Join(outDir, name);
            Directory.CreateDirectory(sdir);
            // which stage is going, and since when, for a reader of the run record (the workbench's Start tab)
            run.Note($"stage {name} started {PyText.UtcNowPlus()}");
            run.Liveness();
            // the entry and every earlier stage's binding decide reuse; the controller's own code is recorded, not
            // bound, because the measurements' tool identities are bound inside the plan
            var measured = ctx.Entry.Measured();
            // a budget-only change touches evaluate and what follows it: earlier stages hash the budget their plan froze
            object? limit = BudgetDiffers(ctx.Entry, outDir);
            if (limit != null)
            {
                double frozen = Py.Float(PlannedBudget(outDir));
                measured["evaluation"] = new PyDict(Py.D(measured["evaluation"])) { ["budgetMinutes"] = frozen == Math.Truncate(frozen) ? (object)(long)frozen : frozen };
            }
            var upstream = new PyDict();
            foreach (string s in Stages.Take(At(name))) upstream[s] = Py.DictOrEmpty(Py.D(ledger.Doc["stages"]).Get(s)).Get("inputsSha256");
            var inputs = new PyDict { ["entry"] = ctx.Entry.StageEntryHash(name, measured), ["upstream"] = upstream };
            if (limit != null && name == "evaluate") inputs["budgetInForce"] = limit;
            string lineFile = PyPath.Join(outDir, "scope", "scope-line.json");
            if (name == "plan" && File.Exists(lineFile))
                Report.Line("scope: " + (Py.D(PyJson.ReadPipelineFile(lineFile)).Get("line") is { } said && Py.Truthy(said) ? PyText.Str(said) : ""));
            if (seeded.Get(name) is PyDict old)
            {
                // an earlier run's preparation, taken over whole: recorded under this run's inputs, its own record kept
                ledger.Record(name, inputs, Py.DictOrEmpty(old.Get("outputs")).Keys, 0, new PyDict
                {
                    ["bound"] = old.Get("bound"), ["seconds"] = 0.0, ["driverSha256"] = ToolIdentity.DriverSha256(),
                    ["reusedFrom"] = PyOrder.Fwd(reusedFrom!), ["reusedSeconds"] = old.Get("seconds"),
                });
                Report.Line($"{name}: reused from an earlier run");
                run.Advance();
                continue;
            }
            if (ledger.Decide(name, inputs) == "reuse")
            {
                Report.Line($"{name}: reused (inputs and outputs unchanged)");
                run.Advance();
                continue;
            }
            if (forecastLine == null)
            {
                // once, before the first stage that will run: the disk this run still needs, from measured figures
                var todo = Stages.Skip(At(name)).Take(At(through) - At(name) + 1).ToList();
                (forecastLine, string? shortBy) = DiskForecast.Forecast(ctx, records, todo);
                PyJson.WritePipelineFile(PyPath.Join(outDir, "disk-forecast.json"), new PyDict
                {
                    ["line"] = forecastLine, ["refused"] = shortBy, ["stages"] = Py.Objs(todo), ["evaluatedRecords"] = records, ["at"] = PyText.UtcNowPlus(),
                });
                Report.Line(forecastLine);
                if (shortBy != null)
                {
                    WriteStatus(outDir, new PyDict { ["status"] = "incomplete", ["stoppedAt"] = name, ["sentence"] = shortBy, ["at"] = PyText.UtcNowPlus() });
                    Report.Error($"{name}: REFUSED: {shortBy}");
                    return Exit.Refused;
                }
            }
            sizeBefore ??= Hashing.UniqueBytes(outDir, topFiles: false);
            double t0 = ChildRunner.Now();
            ChildLaunch.TakeWaited();
            PyDict? bound;
            List<string> outputs;
            try { (bound, outputs) = body(ctx, res, sdir); }
            catch (Awaiting w)
            {
                ledger.Record(name, inputs, [], 3);
                Py.D(Py.L(ledger.Doc["history"])[^1])["reason"] = "awaiting: " + w.Message;
                ledger.Save();
                WriteStatus(outDir, new PyDict { ["status"] = "awaiting-judgement", ["stoppedAt"] = name, ["sentence"] = w.Message, ["at"] = PyText.UtcNowPlus() });
                Report.Line($"{name}: AWAITING: {w.Message}");
                return Exit.Judgement;
            }
            catch (Refusal r)
            {
                ledger.Record(name, inputs, [], 2);
                Py.D(Py.L(ledger.Doc["history"])[^1])["reason"] = "refused: " + r.Message;
                ledger.Save();
                sizeBefore = null;   // a refused stage's writes are not the next stage's
                bool blocks = (bool)RunControl.Stages.Outcome(name, 2, false, r.Message)["blocksLater"]! && r is not NonBlockingRefusal;
                WriteStatus(outDir, new PyDict { ["status"] = "incomplete", ["stoppedAt"] = name, ["sentence"] = r.Message, ["at"] = PyText.UtcNowPlus() });
                if (blocks)
                {
                    Report.Error($"{name}: REFUSED: {r.Message}");
                    return Exit.Refused;
                }
                Report.Line($"{name}: refused, not blocking: {r.Message}");
                refusals.Add(new PyDict { ["stage"] = name, ["sentence"] = r.Message });
                run.Advance();
                continue;
            }
            double secs = PyText.Round(ChildRunner.Now() - t0, 1);
            double waited = PyText.Round(ChildLaunch.TakeWaited(), 1);
            long sizeAfter = Hashing.UniqueBytes(outDir, topFiles: false);
            ledger.Record(name, inputs, outputs, 0, new PyDict
            {
                ["bound"] = bound, ["seconds"] = secs, ["waitedSeconds"] = waited, ["driverSha256"] = ToolIdentity.DriverSha256(),
                // what this stage added to the run folder, each file once, for later forecasts
                ["disk"] = new PyDict { ["addedBytes"] = sizeAfter - sizeBefore.Value, ["evaluatedRecords"] = records },
            });
            sizeBefore = sizeAfter;
            Report.Line($"{name}: accepted in {PyText.FloatRepr(secs)} s"
                        + (waited >= 1 ? $" ({PyText.FloatRepr(waited)} s of it waiting for the machine: a free slot or free memory)" : ""));
            run.Advance();
        }
        var status = refusals.Count > 0
            ? new PyDict
            {
                ["status"] = "incomplete", ["through"] = through, ["sentence"] = Py.D(refusals[0])["sentence"], ["refusals"] = refusals,
                ["at"] = PyText.UtcNowPlus(),
            }
            : new PyDict { ["status"] = "through " + through, ["at"] = PyText.UtcNowPlus() };
        if (!string.IsNullOrEmpty(forecastLine)) status["diskForecast"] = forecastLine;
        WriteStatus(outDir, status);
        return Exit.Ok;
    }

    /// <summary>Prints the arms the entry derives, before anything runs.</summary>
    public static int PrintArms(PipelineArgs a)
    {
        var res = new RunContext(a.Entry!, a.Machine, a.Out ?? ".").Resolve();
        var baseline = Py.Strs(res["baseline"]);
        Report.Line($"baseline ({baseline.Count}): {string.Join(", ", baseline)}");
        foreach (var arm in Py.L(res["arms"]).Select(Py.D))
        {
            var evaluated = Py.Strs(arm["plugins"]).Where(p => !baseline.Contains(p)).ToList();
            Report.Line($"{PyText.Str(arm["id"]),-40} {PyText.Str(arm["role"]),-8} {(evaluated.Count > 0 ? string.Join("; ", evaluated) : "(baseline only)")}");
            Report.Line($"{"",-40} mods: {string.Join("; ", Py.Strs(arm["mods"]))}");
        }
        return Exit.Ok;
    }

    /// <summary>Prints each stage's state from the run's ledger, then the run's status.</summary>
    public static int PrintStatus(PipelineArgs a)
    {
        var ledger = new StageLedger(a.Out!);
        var history = Py.L(ledger.Doc["history"]).Select(Py.D).ToList();
        foreach (string s in Stages)
        {
            var rec = Py.D(ledger.Doc["stages"]).Get(s) as PyDict;
            var last = history.LastOrDefault(h => h.Get("stage") as string == s);
            if (Py.Truthy(rec)) Report.Line($"{s,-20} accepted {PyText.Str(rec!["at"])} ({PyText.Str(rec.Get("seconds"))} s)");
            else if (last != null)
                Report.Line($"{s,-20} not accepted ({PyText.Str(last["at"])}): {(Py.Truthy(last.Get("reason")) ? PyText.Str(last["reason"]) : "exit " + PyText.Str(last.Get("exitCode")))}");
            else Report.Line($"{s,-20} not run");
        }
        string st = PyPath.Join(a.Out!, "status.json");
        if (File.Exists(st)) Report.Line(PyJson.Dumps(PyJson.ReadPipelineFile(st)));
        return Exit.Ok;
    }
}
