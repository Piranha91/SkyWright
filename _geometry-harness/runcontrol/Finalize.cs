using HousecarlCore.Progress;

namespace RunControl;

/// <summary>`runcontrol finalize --run RUN` (evalrun.py's cmd_finalize): derives disposition.json and STATUS.md from
/// the ledger and the receipts. Exit 0, or 3 with a refusal disposition or an evidence-integrity refusal written.</summary>
public static class Finalizer
{
    // One refusal: the sentence on standard error, the refusal disposition written, exit 3.
    static int Refused(string run, string cause, string sentence)
    {
        Console.Error.WriteLine($"REFUSED: {sentence}");
        Disposition.WriteRefusal(run, cause, sentence);
        return Exit.Judgement;
    }

    public static int Run(string runFolder)
    {
        string run = PyPath.Str(runFolder);
        EvalStore.SetRunStore(run);
        var (plan, why) = EvalStore.LoadPlan(run);
        if (plan == null) return Refused(run, EvalStore.RefusalCause(why), why!);
        var ledger = new Ledger(run, plan);
        string? bad = ledger.Refusal();
        if (bad != null) return Refused(run, bad.Contains("foreign-run", StringComparison.Ordinal) ? "foreign-run" : "ledger-unreadable", bad);

        // the registry and the scope are premises of the frozen plan: a changed or missing one is a refused result
        bad = EvalStore.PremiseRefusal(plan);
        if (bad != null) return Refused(run, EvalStore.RefusalCause(bad), bad);

        var receipts = Receipts.ReadReceipts(run);
        // walked over the ledger: every settled entry wrote a receipt, so one whose receipt file is gone is a gap
        foreach (var (k, eRaw) in ledger.Entries)
        {
            object? status = Ev.G(eRaw, "status");
            if (status is null or "running" or "interrupted" or "pending") continue;
            if (!Ev.Exists(PyPath.Join(run, "receipts", $"{k}.json")))
                return Refused(run, "receipt-missing", $"receipt-missing: the ledger records {k} as {Ev.Str(status)} but receipts/{k}.json is gone");
        }
        foreach (var (k, r) in receipts)
            if (!Ev.Eq(Ev.G(r, "planId"), plan["planId"]))
                return Refused(run, "foreign-run",
                    $"foreign-run: receipts/{k}.json carries planId {Ev.Str(Ev.G(r, "planId"))}, this run is {Ev.Str(plan["planId"])}");

        PyDict integrity, disp;
        using (var record = RunLog.Begin("evalrun-finalize", new RunOptions { StageSource = StageSource.Counted }))
        {
            record.Stage("finalize", total: 1, unit: "dispositions");
            (integrity, var badKeys) = EvidenceIntegrity.Check(run, plan, ledger, receipts);
            disp = Disposition.Build(run, plan, ledger, receipts, integrity, badKeys);
            PyJson.WriteEvalFile(PyPath.Join(run, "disposition.json"), disp);
            Ev.WriteText(PyPath.Join(run, "STATUS.md"), StatusMd.Build(plan, disp));
            record.Advance();
        }
        var evaluation = PyValue.Dict(disp["evaluation"]);
        Console.Out.WriteLine($"evaluation.complete: {Ev.Str(evaluation["complete"])}   claim: {Ev.Str(evaluation["claim"])}");
        var defects = (List<object?>)integrity["defects"]!;
        foreach (var d in defects.Take(20).Select(PyValue.Dict))
            Console.Error.WriteLine($"   evidence {Ev.Str(d["kind"])}: {Ev.Str(d["key"])} {(PyValue.Truthy(d["path"]) ? Ev.Str(d["path"]) : "")}");
        foreach (var m in ((List<object?>)evaluation["missing"]!).Take(20).Select(PyValue.Dict))
            Console.Out.WriteLine($"   missing {Ev.Str(m["checkId"])} [{Ev.Str(m["armId"])}] {Ev.Str(m["status"])}");
        if (!PyValue.Truthy(integrity["ok"]))
        {
            Console.Error.WriteLine($"REFUSED: evidence-integrity: {defects.Count} completed entr{(defects.Count == 1 ? "y" : "ies")} no longer "
                + "match the evidence they were derived from. The disposition and STATUS.md are written with the defects listed first; no "
                + "completed claim rests on them. Next action: `resume --run RUN` re-runs the affected entries (finalize never rewrites the ledger).");
            return Exit.Judgement;
        }
        return Exit.Ok;
    }
}
