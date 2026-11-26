using HousecarlCore.Progress;

namespace RunControl;

/// <summary>`runcontrol run --run RUN` and `resume --run RUN` (evalrun.py's cmd_run): the plan's checks executed in
/// schedule order, each row accounted or measured, what an earlier invocation completed re-checked before it is reused.
/// Exit 0, or 2 on a refusal or a program that changed.</summary>
public static class EvaluateRun
{
    /// <summary>One line when the plan's probes are not this runcontrol's own build's (another build's plan, Debug and
    /// Release, or an explicit --harness): where they are and where this program is. Null when they match.</summary>
    public static string? LayoutNote(PyDict plan)
    {
        string harness = PyPath.Str(Ev.Str(PyValue.Dict(plan["options"])["harness"]));
        var own = HarnessShared.CheckoutLayout.Locate(null, out _);
        if (own is not null && string.Equals(Path.GetFullPath(harness).TrimEnd('\\', '/'), Path.GetFullPath(own.Harness).TrimEnd('\\', '/'), StringComparison.OrdinalIgnoreCase)) return null;
        return $"note: this plan's probes are at {PyOrder.Fwd(harness)} (its --harness), not in this runcontrol's build "
               + $"{(own is null ? "(none found)" : PyOrder.Fwd(own.Root))}; they run as the plan recorded them, and their identities are checked by content.";
    }

    public static int Run(EvalArgs a, bool resuming)
    {
        string run = PyPath.Str(a.Run!);
        EvalStore.SetRunStore(run);
        var (plan, why) = EvalStore.LoadPlan(run);
        if (plan == null) throw new Refusal(why!);
        if (EvalStore.RequestRefusal(plan) is { } outdated) throw new Refusal(outdated);
        // the programs this plan can launch, and those its retained work came from, are the ones it recorded: checked
        // by content before anything below writes
        if (IdentityChecks.ContractRefusal(plan) is { } noContract) throw new Refusal(noContract);
        var (changed, measured) = ToolPreflight.Check(run, plan);
        if (changed != null) throw new Refusal(changed);
        // a plan made for another layout's probes (its --harness) runs them as recorded, never silently
        if (LayoutNote(plan) is { } note) Console.Error.WriteLine(note);
        // the plan's premises are read before anything is written: a ledger rebuild writes the ledger
        if (EvalStore.PremiseRefusal(plan) is { } premise) throw new Refusal(premise);
        var ledger = new Ledger(run, plan);
        if (a.RebuildLedger) ledger.Rebuild();
        if (ledger.Refusal() is { } bad) throw new Refusal(bad);

        using (var parent = RunLog.Begin("evalrun-checks", new RunOptions { StageSource = StageSource.Counted }))
        {
            try
            {
                object? limit = a.BudgetMinutes is { } minutes ? minutes : null;
                ledger.OpenInvocation(resuming ? "resume" : "run", parent.RunId, null, budgetMinutes: limit);
                var ex = new Executor(run, plan, ledger, resuming, limit) { ParentRunId = parent.RunId, Preflight = measured };
                int healed = ledger.HealInterrupted();
                if (healed > 0) Console.Out.WriteLine($"   {healed} interrupted entr{(healed == 1 ? "y" : "ies")} re-queued");

                // `run` and `resume` give the same guarantee: a run over a ledger that already holds completed
                // entries is a reuse decision, so it re-checks them too
                if (resuming || ledger.Entries.Any(kv => EvalStore.IsTerminalOk(Ev.G(kv.Value, "status"))))
                    Revalidate.Run(run, plan, ledger);

                var planned = PyValue.Iter(plan["checks"]).Select(PyValue.Dict).ToList();
                parent.Feature("checks", planned.Count, "checks");
                parent.Feature("arms", PyValue.Len(plan["arms"]), "arms");
                parent.Feature("probes", PyValue.Len(plan["ordersweepProbes"]), "probes");
                parent.Stage("checks", total: planned.Sum(c => PyValue.Len(c["arms"])), unit: "check-arms");

                try { Execute(ex, parent); }
                catch (ToolChanged tc)
                {
                    // launches stop here: what this invocation accepted earlier stands
                    string msg = $"{tc.Message} Results accepted before this point stand; nothing further was launched.";
                    Console.Error.WriteLine($"REFUSED: {msg}");
                    parent.Refuse(msg, Exit.Refused);
                    return Exit.Refused;
                }
                if (ex.DeadlineStops > 0)
                {
                    parent.Note($"deadline: {ex.DeadlineStops} running child(ren) stopped at the budget's deadline");
                    Console.Out.WriteLine($"   deadline: {ex.DeadlineStops} running child(ren) stopped when this run's budget of "
                        + $"{Ev.Str(ex.BudgetMinutes)} minute(s) ran out; their checks are stopped(deadline) and "
                        + "`resume` re-runs them under a fresh deadline");
                }
            }
            catch (Exception error) when (error is not ToolChanged)
            {
                if (error is Refusal) parent.Refuse(error.Message, Exit.Refused);
                else parent.Fail($"{error.GetType().Name}: {error.Message}");   // as Python's Run records an exception
                throw;
            }
        }

        EvalStatus.Print(plan, ledger);
        return Exit.Ok;
    }

    /// <summary>The order checks run in: a topological pass over the prerequisites in which, among the checks whose
    /// prerequisites are already scheduled, required ones go first and registry order breaks ties. A cycle, or a
    /// prerequisite no check has, cannot stall it: what remains is appended in registry order.</summary>
    public static List<PyDict> Schedule(PyDict plan)
    {
        var checks = PyValue.Iter(plan["checks"]).Select(PyValue.Dict).ToList();
        var byId = new Dictionary<string, PyDict>(StringComparer.Ordinal);
        var order = new Dictionary<string, int>(StringComparer.Ordinal);
        for (int i = 0; i < checks.Count; i++)
        {
            string id = Ev.Str(checks[i]["checkId"]);
            byId[id] = checks[i];   // a repeated id keeps its last check and its last place, as a Python dict does
            order[id] = i;
        }
        var pending = new HashSet<string>(byId.Keys, StringComparer.Ordinal);
        var done = new HashSet<string>(StringComparer.Ordinal);
        var scheduled = new List<PyDict>();
        while (pending.Count > 0)
        {
            var ready = pending.Where(cid => Ev.Items(byId[cid].Get("prerequisites")).All(pre => pre is not string p || done.Contains(p) || !byId.ContainsKey(p))).ToList();
            if (ready.Count == 0)
            {
                scheduled.AddRange(pending.OrderBy(cid => order[cid]).Select(cid => byId[cid]));
                break;
            }
            string next = ready.OrderBy(cid => PyValue.Truthy(byId[cid].Get("required")) ? 0 : 1).ThenBy(cid => order[cid]).First();
            scheduled.Add(byId[next]);
            pending.Remove(next);
            done.Add(next);
        }
        return scheduled;
    }

    /// <summary>Every row of the plan settled: reused, accounted by its plan status, or measured. The swept checks are
    /// settled once, at the position of the first; the derived checks after the loop.</summary>
    public static void Execute(Executor ex, Run parent) => Execute(ex, () => { parent.Advance(); });

    /// <summary>The same, counting each settled row through `advance`.</summary>
    public static void Execute(Executor ex, Action advance)
    {
        var plan = ex.Plan;
        var ledger = ex.Ledger;

        bool AlreadyDone(object? cid, object? aid) => EvaluateRun.AlreadyDone(ledger, cid, aid, null);
        void MarkReused(object? cid, object? aid) => EvaluateRun.MarkReused(ledger, cid, aid, null);

        // one child for every swept check, at the position of the first: a prerequisite ahead of it has run before
        // it, and a check behind it can read its receipts
        var sweepChecks = SweepRows.Planned(plan);
        bool sweepDone = false;

        foreach (var check in Schedule(plan))
        {
            var adapter = PyValue.Dict(check["adapter"]);
            object? kind = adapter.Get("kind");
            string cid = Ev.Str(check["checkId"]);
            if (Ev.Eq(kind, "ordersweep-probe") && sweepChecks.Any(c => ReferenceEquals(c, check)))
            {
                if (!sweepDone) SweepRows.Settle(ex, sweepChecks, advance);
                sweepDone = true;
                continue;
            }
            foreach (var armRow in PyValue.Iter(check["arms"]).Select(PyValue.Dict))
            {
                var arm = ex.Arm(armRow["armId"]);
                object? aid = arm["armId"];
                object? st = armRow["status"];
                if (AlreadyDone(cid, aid))
                {
                    string? pre = Ev.Eq(st, "planned") ? Prerequisites.ReuseBlockedBy(ex, check, aid) : null;
                    if (Ev.Eq(kind, "derived")) { }   // reused or blocked after the loop, where derived checks are decided
                    else if (pre != null)
                        ex.Account(check, arm, $"blocked(prerequisite:{pre})", "blocked-prerequisite", Prerequisites.BlockDetail(ex, pre, aid));
                    else MarkReused(cid, aid);
                    advance();
                    continue;
                }
                if (AccountPlanStatus(ex, check, arm, st)) { }
                else if (Ev.Eq(st, "unsupported"))
                    ex.Account(check, arm, (string)st, null,
                        PyValue.Truthy(check.Get("unsupportedReason")) ? (string)check["unsupportedReason"]! : "the registry records no CLI adapter for this check");
                else if (Ev.Eq(st, "manual"))
                    ex.Account(check, arm, "manual", null,
                        PyValue.Truthy(check.Get("unsupportedReason")) ? (string)check["unsupportedReason"]!
                            : "this check is completed by an attributed manual decision, read at finalize");
                else if (!Ev.Eq(st, "planned"))
                    // a status only another program's plan gives (an older SkyWright planned this run): never launched here
                    ex.Account(check, arm, "unsupported", null,
                        $"the plan gives this row the status {PyText.Repr(st)}, which this SkyWright does not evaluate; plan the run again to evaluate it");
                else if (Ev.Eq(kind, "derived")) { }   // after the loop: derived checks consume other receipts
                else
                {
                    // a prerequisite measured and failed on this arm, or blocked on it by such a failure, blocks it here
                    // even when the baseline's was accepted; one not attempted falls back to the baseline's
                    var missing = PyValue.Iter(check["prerequisites"])
                        .Where(p => !Prerequisites.AnswerArms(ex, p, aid).Any(a => EvalStore.IsTerminalOk(ledger.Entry(p, a, null)?.Get("status")))).ToList();
                    // the budget first: a prerequisite missing because the deadline stopped it is the budget's consequence
                    if (!ex.BudgetLeft())
                        ex.Account(check, arm, "skipped-budget", "budget-exhausted", ex.BudgetSentence());
                    else if (missing.Count > 0)
                        ex.Account(check, arm, $"blocked(prerequisite:{Ev.Str(missing[0])})", "blocked-prerequisite",
                            "a prerequisite check has no accepted receipt: " + string.Join(", ", missing.Select(p =>
                                Prerequisites.AnswerArms(ex, p, aid).Count == 1
                                    ? $"{Ev.Str(p)} ({Ev.Str(ledger.Entry(p, aid, null)?.Get("status"))} on this arm)" : Ev.Str(p))));
                    else GenericRows.Run(ex, check, arm);
                }
                advance();
            }
        }

        foreach (var check in Schedule(plan))
        {
            if (!Ev.Eq(PyValue.Dict(check["adapter"]).Get("kind"), "derived") || !Ev.Eq(check["status"], "planned")) continue;
            foreach (var armRow in PyValue.Iter(check["arms"]).Select(PyValue.Dict))
            {
                if (!Ev.Eq(armRow["status"], "planned")) continue;   // its plan status was accounted in the loop above
                var arm = ex.Arm(armRow["armId"]);
                if (AlreadyDone(check["checkId"], arm["armId"]))
                {
                    if (Prerequisites.ReuseBlockedBy(ex, check, arm["armId"]) is { } pre)
                        ex.Account(check, arm, $"blocked(prerequisite:{pre})", "blocked-prerequisite", Prerequisites.BlockDetail(ex, pre, arm["armId"]));
                    else MarkReused(check["checkId"], arm["armId"]);
                    continue;
                }
                DerivedRows.Run(ex, check, arm);
            }
        }
    }

    /// <summary>A row the plan already answered (not applicable, excluded, not selected, an arm the adapter does not
    /// take, a held-back arm, a missing payload, an opt-in check not opted in), accounted by its plan status; false for
    /// a status this rule does not answer. runcontrol's evaluation and another program evaluating a plan of its own
    /// use the same rule.</summary>
    public static bool AccountPlanStatus(Executor ex, PyDict check, PyDict arm, object? st)
    {
        var plan = ex.Plan;
        string cid = Ev.Str(check["checkId"]);
        if (Ev.Eq(st, "not-applicable"))
            ex.Account(check, arm, "not-applicable", null, PyValue.Truthy(check.Get("applicabilityEvidence")) ? (string)check["applicabilityEvidence"]! : "");
        else if (Ev.Eq(st, "excluded"))
            ex.Account(check, arm, "skipped-user", null,
                Ev.Str(Ev.G(PyValue.Dict(PyValue.Dict(plan["explicitDecisions"])["exclude"]), cid, "excluded by the caller")));
        else if (Ev.Eq(st, "skipped-user"))
            ex.Account(check, arm, "skipped-user", null, "not in --select; --select narrows what runs, never what is planned");
        else if (Ev.Eq(st, "unsupported-for-arm"))
            ex.Account(check, arm, "unsupported", null, $"this adapter does not accept the {Ev.Str(arm["kind"])} arm as an input the plan can hand it");
        else if (Ev.Eq(st, "skipped-budget"))
            ex.Account(check, arm, "skipped-budget", "budget-exhausted",
                $"arm {Ev.Str(arm["armId"])} was held back by --max-candidates {Ev.Str(PyValue.Dict(plan["budgets"])["maxCandidates"])}: it was planned and not evaluated");
        else if (Ev.Eq(st, "blocked-payload-missing"))
            ex.Account(check, arm, "blocked(payload-missing)", "blocked-prerequisite",
                "the supplied arm declares files that are not in the configuration: "
                + string.Join(", ", PyValue.Iter(PyValue.Dict(arm["supplied"])["payloadMissing"]).Select(x => (string)x!)));
        else if (Ev.Eq(st, EvalStore.NotOptedIn))
            ex.Account(check, arm, "skipped-user", null,
                "opt-in check, not requested (`plan --opt-in " + cid + "`): it was not run and this evaluation says nothing about it");
        else return false;
        return true;
    }

    /// <summary>Whether an earlier invocation settled this (check, arm, part): completed or reused, or an accepted
    /// import that found nothing to judge.</summary>
    public static bool AlreadyDone(Ledger ledger, object? cid, object? aid, string? part)
    {
        var e = ledger.Entry(cid, aid, part);
        return PyValue.Truthy(e) && (EvalStore.IsTerminalOk(e!.Get("status"))
                                    || (Ev.Eq(e.Get("status"), "not-applicable") && PyValue.Truthy(e.Get("imported"))));
    }

    /// <summary>A completed entry kept by this invocation reads reused, naming the invocation that measured it.</summary>
    public static void MarkReused(Ledger ledger, object? cid, object? aid, string? part)
    {
        var e = ledger.Entry(cid, aid, part);
        if (PyValue.Truthy(e) && Ev.Eq(e!.Get("status"), "completed"))
            ledger.Transition(Ev.Str(cid), Ev.Str(aid), part, "reused", receiptId: e.Get("receiptId") as string,
                detector: e.Get("detector") as string, extra: new PyDict { ["reusedFromRunId"] = e.Get("runId") });
    }
}
