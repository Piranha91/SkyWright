using HousecarlCore.Progress;
using RunControl;

namespace EvalControl;

/// <summary>`evalcontrol evaluate --run AI`: every row of the overlay plan settled, in runcontrol's schedule order: a
/// row an earlier invocation completed is re-checked and reused; a row the plan already answered is accounted by
/// runcontrol's own rule (EvaluateRun.AccountPlanStatus); the rest by this module's rule for its kind.</summary>
public static class OverlayEvaluate
{
    /// <summary>The sentence a media row is accounted with: it is assessed at bundle, never here.</summary>
    public const string NoMedia = "no supplied media identified this check's configuration on this arm: media is "
        + "assessed by `evalcontrol bundle --media`, and an item whose fingerprint does not match an "
        + "accepted receipt of the app scan is unassessed, not an illustration of a verified result";

    /// <summary>rebuildLedger: the ledger is rebuilt from the journal and the receipts first (runcontrol's own
    /// Ledger.Rebuild, as `runcontrol resume --rebuild-ledger` does).</summary>
    public static int Run(string aiRun, bool rebuildLedger = false)
    {
        string run = PyPath.Resolve(aiRun);
        EvalStore.SetRunStore(run);
        var (plan, why) = EvalStore.LoadPlan(run);
        if (plan == null) throw new Refusal(why!);
        if (!plan.ContainsKey(OverlayPlan.Key)) throw new Refusal($"{run} is not an AI-side overlay (its plan has no {OverlayPlan.Key} block); runcontrol evaluates an app scan.");
        if (EvalStore.RequestRefusal(plan) is { } outdated) throw new Refusal(outdated);
        if (IdentityChecks.ContractRefusal(plan) is { } noContract) throw new Refusal(noContract);
        var (changed, measured) = ToolPreflight.Check(run, plan);
        if (changed != null) throw new Refusal(changed);
        if (EvalStore.PremiseRefusal(plan) is { } premise) throw new Refusal(premise);
        var ledger = new Ledger(run, plan);
        if (rebuildLedger) ledger.Rebuild();
        if (ledger.Refusal() is { } bad) throw new Refusal(bad);

        using (var parent = RunLog.Begin("evalcontrol-checks", new RunOptions { StageSource = StageSource.Counted }))
        {
            ledger.OpenInvocation("run", parent.RunId, null);
            var ex = new Executor(run, plan, ledger, false) { ParentRunId = parent.RunId, Preflight = measured };
            ledger.HealInterrupted();
            if (ledger.Entries.Any(kv => EvalStore.IsTerminalOk(Ev.G(kv.Value, "status")))) Revalidate.Run(run, plan, ledger);
            var planned = Ev.Items(plan["checks"]).Select(PyValue.Dict).ToList();
            parent.Stage("checks", total: planned.Sum(c => PyValue.Len(c["arms"])), unit: "check-arms");
            foreach (var check in EvaluateRun.Schedule(plan))
            {
                string kind = Ev.Str(Ev.OrEmpty(check["adapter"]).Get("kind"));
                foreach (var armRow in Ev.Items(check["arms"]).Select(PyValue.Dict))
                {
                    var arm = ex.Arm(armRow["armId"]);
                    object? st = armRow["status"];
                    if (EvaluateRun.AlreadyDone(ledger, check["checkId"], arm["armId"], null)) EvaluateRun.MarkReused(ledger, check["checkId"], arm["armId"], null);
                    else if (EvaluateRun.AccountPlanStatus(ex, check, arm, st)) { }
                    // an import no longer credited (its evidence changed, its reuse refused) keeps its cause, by runcontrol's rule
                    else if (st is "mcp-only" or "live-import" && EvaluateRun.AccountUncreditedImport(ex, check, arm)) { }
                    else if (kind == "media") ex.Account(check, arm, "blocked(no-identified-media)", null, NoMedia);
                    else if (Ev.Eq(st, "mcp-only"))
                        // open until `evalcontrol import` binds a record reader's evidence to it
                        ex.Account(check, arm, "mcp-only", null,
                            PyValue.Truthy(check.Get("unsupportedReason")) ? (string)check["unsupportedReason"]! : "the list records no command-line adapter for this check");
                    else if (Ev.Eq(st, "live-import")) AccountLive(ex, run, plan, ledger, check, arm);
                    else throw new Refusal($"{Ev.Str(check["checkId"])}: adapter kind {PyText.Repr(kind)} is not one evalcontrol evaluates.");
                    parent.Advance();
                }
            }
        }
        EvalStatus.Print(plan, ledger);
        return Exit.Ok;
    }

    /// <summary>An opted-in live row (moved from runcontrol's evaluate with the check): not applicable when the arm's
    /// accepted census in the app scan answers it (nothing a script drives touches a target), else open until the live
    /// driver's export is imported.</summary>
    static void AccountLive(Executor ex, string run, PyDict plan, Ledger ledger, PyDict check, PyDict arm)
    {
        var scopeRead = Ev.Eq(check["checkId"], LiveScope.CheckId) ? LiveScope.Read(run, plan, ledger, arm["armId"]) : null;
        if (PyValue.Truthy(scopeRead) && Ev.Eq(scopeRead!["status"], "not-applicable"))
            ex.Account(check, arm, "not-applicable", null, SwitchFlipScope.Sentence(scopeRead),
                extra: new PyDict { ["switchScope"] = LiveScope.Record(scopeRead) });
        else
            ex.Account(check, arm, "live-import", null,
                "opted in and waiting for a live receipt: run the live switch check for this arm, then "
                + "`evalcontrol import --file` its export; the controller itself never launches the game"
                + (PyValue.Truthy(scopeRead) ? $" ({SwitchFlipScope.Sentence(scopeRead!)})" : ""));
    }
}

/// <summary>`evalcontrol finalize --run AI`: runcontrol's finalizer over the overlay, with the blocks only AI-side rows
/// have added before the disposition is written.</summary>
public static class OverlayFinalize
{
    public const string MediaSentence = "media is identified and assessed by `evalcontrol bundle --media`; see the overlay bundle's manifest";

    public static void Amend(PyDict disposition, string run, PyDict plan, PyDict receipts)
    {
        var un = PyValue.Dict(disposition["unassessed"]);
        var families = Ev.Items(un.Get("familiesUnsupportedOrManual")).Select(Ev.Str).ToHashSet(StringComparer.Ordinal);
        foreach (var c in Ev.Items(plan["checks"]).Select(PyValue.Dict))
            if (Ev.OrEmpty(c["adapter"]).Get("kind") is "media" or "mcp-only") families.Add(Ev.Str(c["family"]));
        un["familiesUnsupportedOrManual"] = PyOrder.SortedByCodePoint(families).Cast<object?>().ToList();
        un["mediaUnassessed"] = MediaSentence;
        un["runtimeObservations"] = LiveScope.RuntimeObservations(receipts);
        disposition[OverlayPlan.Key] = plan[OverlayPlan.Key];
    }
}
