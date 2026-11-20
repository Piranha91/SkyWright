namespace RunControl;

/// <summary>The stage bodies after the plan: evaluate (this program's own `run` and `resume` as children, under the
/// machine slot) and scenes.</summary>
public static class StageEvaluate
{
    /// <summary>The ledger's entries counted by status word (the part before any bracket), in the order first met.</summary>
    public static PyDict LedgerCounts(string run)
    {
        string file = PyPath.Join(run, "ledger.json");
        var led = File.Exists(file) ? Py.D(PyJson.ReadPipelineFile(file)) : new PyDict();
        var counts = new PyDict();
        foreach (var (_, v) in Ev.OrEmpty(led.Get("entries")))
        {
            object? status = Ev.G(v, "status");
            string word = (PyValue.Truthy(status) ? (string)status! : "?").Split('(')[0];
            counts[word] = (long)(counts.Get(word) ?? 0L) + 1;
        }
        return counts;
    }

    /// <summary>The plan's checks run by `run`, then `resume` while a stop left rows open and the ledger still moves,
    /// five attempts at most. Each attempt holds the machine slot for the budget and ten minutes. An evaluation that
    /// stopped at its budget keeps the stage open, so the next run resumes it; the stages after it still run.</summary>
    public static (PyDict?, List<string>) Evaluate(RunContext ctx, PyDict res, string sdir)
    {
        string run = PyPath.Join(ctx.Out, "evaluation");
        var history = new List<object?>();
        string ledgerFile = PyPath.Join(run, "ledger.json");
        string verb = File.Exists(ledgerFile) && Py.Truthy(Py.D(PyJson.ReadPipelineFile(ledgerFile)).Get("runIds")) ? "resume" : "run";
        // the controller stops itself at the plan's budget and releases the slot; a stop from outside leaves a stale
        // lock, so the deadline and the slot's hold sit past the budget
        var ev = Py.DictOrEmpty(ctx.Entry.Get("evaluation"));
        long backstop = (long)PyValue.Int(ev.ContainsKey("budgetMinutes") ? ev["budgetMinutes"] : 90L) + 10;
        for (int attempt = 1; attempt <= 5; attempt++)
        {
            var before = LedgerCounts(run);
            object? limit = Driver.BudgetDiffers(ctx.Entry, ctx.Out);
            var args = new List<string> { "--run", PyOrder.Fwd(run) };
            if (limit != null) args.AddRange(["--budget-minutes", PyText.Str(limit)]);
            var r = ChildLaunch.Run(ctx, PyPath.Join(sdir, "commands"), $"evaluate-{attempt:00}", ChildLaunch.Controller(verb, args), heavy: true,
                purpose: $"evalpipeline {ctx.Id} evaluate", deadlineMin: backstop, holdMin: backstop);
            var after = LedgerCounts(run);
            history.Add(new PyDict
            {
                ["attempt"] = (long)attempt, ["verb"] = verb, ["exitCode"] = r.ExitCodeUnsigned, ["stopped"] = r.Stopped,
                ["before"] = before, ["after"] = after,
            });
            verb = "resume";
            bool stopped = !string.IsNullOrEmpty(r.Stopped);
            if (r.ExitCode != 0 && !stopped) break;
            if (PyValue.Eq(after, before) || !stopped) break;
        }
        string record = PyPath.Join(sdir, "evaluate.json");
        PyJson.WritePipelineFile(record, new PyDict { ["history"] = history, ["ledgerCounts"] = LedgerCounts(run) });
        var last = Py.D(history[^1]);
        if (!PyValue.Eq(last["exitCode"], 0L))
            throw new Refusal($"evalrun {PyText.Str(last["verb"])} ended with exit {PyText.Str(last["exitCode"])} (stopped: {PyText.Str(last["stopped"])}); "
                              + $"read {PyOrder.Fwd(PyPath.Join(sdir, "commands"))} and `evalrun status`.");
        // rows the controller stopped or never started keep the stage open, so the next run resumes them
        var counts = LedgerCounts(run);
        long stoppedRows = (long)(counts.Get("stopped") ?? 0L), skipped = (long)(counts.Get("skipped-budget") ?? 0L);
        if (stoppedRows > 0 || skipped > 0)
        {
            object? minutes = Driver.BudgetDiffers(ctx.Entry, ctx.Out) ?? Driver.PlannedBudget(ctx.Out);
            if (minutes is double d && d == Math.Truncate(d) && !double.IsInfinity(d)) minutes = (long)d;
            throw new NonBlockingRefusal($"evaluation stopped at its budget of {PyText.Str(minutes)} min with {stoppedRows + skipped} rows open "
                                         + $"({stoppedRows} stopped, {skipped} not started); run again to resume them (each run gets a "
                                         + "fresh budget), or raise the entry's budgetMinutes.");
        }
        // the ledger is not an output binding: imports and finalize change it in later stages
        return (new PyDict { ["plan"] = Hashing.Sha256File(PyPath.Join(run, "plan.json")) }, [record]);
    }

    /// <summary>Overlap scene acquisition is not built in this version: a stated refusal that does not block the stages after it.</summary>
    public static (PyDict?, List<string>) Scenes(RunContext ctx, PyDict res, string sdir) =>
        throw new NonBlockingRefusal("overlap scene acquisition is not built in this version (overlapprobe --geometry-manifest "
                                     + "per arm, attribution and triage); no incident catalogue was made. The report and reel "
                                     + "do not read it.");
}
