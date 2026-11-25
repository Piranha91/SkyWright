namespace RunControl;

/// <summary>What gates a row before it runs or is reused (evalrun.py): the candidate file still being the plan's,
/// the prerequisite rule, an import that is no longer credited, and the live switch check's scope.</summary>
public static class Prerequisites
{
    /// <summary>Null when the candidate file on disk still hashes to the plan's frozen value, else why not: the
    /// order a child is launched on is the file as it is now.</summary>
    public static string? CandidateStale(PyDict arm, HashMemo? memo = null)
    {
        object? path = arm.Get("candidatePath"), want = arm.Get("candidateSha256");
        if (!PyValue.Truthy(path) || !PlanArms.IsSha256(want)) return null;
        string p = PyPath.Str(Ev.Str(path));
        const string nxt = "Next action: plan again.";
        if (!Ev.Exists(p))
            return $"the plan's candidate file {p} is gone, so the order this arm names cannot be read. {nxt}";
        string? got = (memo ?? new HashMemo()).Sha256(p);
        if (got != (string)want!)
            return $"{Ev.Str(arm.Get("candidateFile"))} at {p} now hashes {Ev.Head(got, 12)}; the plan froze "
                   + $"{Ev.Head(want, 12)}. The order this arm would be measured on is not the one the plan approved. {nxt}";
        return null;
    }

    // str((ledger entry or {}).get("status") or "").
    static string StatusOf(Executor ex, object? checkId, object? armId) => StatusOf(ex.Ledger, checkId, armId);

    static string StatusOf(Ledger ledger, object? checkId, object? armId)
    {
        object? status = ledger.Entry(checkId, armId, null)?.Get("status");
        return PyValue.Truthy(status) ? Ev.Str(status) : "";
    }

    /// <summary>The arm's own status for a prerequisite when that status decides for this arm alone: measured and
    /// failed here (rejected, failed), or blocked here by such a failure. Anything else leaves the baseline's answer counting.</summary>
    public static string? DecidedOnArm(Executor ex, object? pre, object? armId) => DecidedOnArm(ex.Ledger, pre, armId);

    /// <summary>The same, read from a ledger alone (an import has no executor).</summary>
    public static string? DecidedOnArm(Ledger ledger, object? pre, object? armId)
    {
        string own = StatusOf(ledger, pre, armId);
        return own.StartsWith("rejected", StringComparison.Ordinal) || own == "failed"
               || own.StartsWith("blocked(prerequisite:", StringComparison.Ordinal) ? own : null;
    }

    /// <summary>The arms whose ledger entry answers a prerequisite for an arm.</summary>
    public static List<object?> AnswerArms(Executor ex, object? pre, object? armId) => AnswerArms(ex.Ledger, pre, armId);

    public static List<object?> AnswerArms(Ledger ledger, object? pre, object? armId) =>
        DecidedOnArm(ledger, pre, armId) != null ? [armId] : [armId, "baseline"];

    public static string BlockDetail(Executor ex, object? pre, object? armId) => BlockDetail(ex.Ledger, pre, armId);

    public static string BlockDetail(Ledger ledger, object? pre, object? armId)
    {
        string? own = DecidedOnArm(ledger, pre, armId);
        if (own != null)
            return $"the prerequisite {Ev.Str(pre)} is {own} on this arm, which decides for this arm: the baseline's answer does not settle it";
        return $"the prerequisite {Ev.Str(pre)} has no accepted receipt on this arm"
               + (Ev.Eq(armId, "baseline") ? "" : " (or on the baseline every arm is measured against)");
    }

    /// <summary>Null when every prerequisite of the check is settled on the arm, else the first that is not. A
    /// not-applicable prerequisite is an answer, not an absence.</summary>
    public static string? Satisfied(Executor ex, PyDict check, object? armId) => Satisfied(ex.Ledger, check, armId);

    public static string? Satisfied(Ledger ledger, PyDict check, object? armId)
    {
        foreach (var pre in Ev.Items(check.Get("prerequisites")))
        {
            bool ok = false;
            foreach (var aid in AnswerArms(ledger, pre, armId))
            {
                var e = ledger.Entry(pre, aid, null);
                if (PyValue.Truthy(e) && (EvalStore.IsTerminalOk(e!.Get("status"))
                                          || (PyValue.Truthy(e.Get("status")) ? Ev.Str(e["status"]) : "").StartsWith("not-applicable", StringComparison.Ordinal)))
                {
                    ok = true;
                    break;
                }
            }
            if (!ok) return Ev.Str(pre);
        }
        return null;
    }

    /// <summary>True when this import row is blocked(prerequisite:...) and a prerequisite still fails for the arm.</summary>
    public static bool UncreditedImportStillBlocked(Executor ex, PyDict check, object? armId) =>
        StatusOf(ex, check["checkId"], armId).StartsWith("blocked(prerequisite:", StringComparison.Ordinal) && Satisfied(ex, check, armId) != null;

    /// <summary>The status word of an import row that resume stopped crediting: invalidated(...) or refused-reuse(...).</summary>
    public static string? UncreditedImportCause(Executor ex, PyDict check, object? armId)
    {
        string own = StatusOf(ex, check["checkId"], armId);
        return own.StartsWith("invalidated(", StringComparison.Ordinal) || own.StartsWith("refused-reuse(", StringComparison.Ordinal) ? own : null;
    }

    /// <summary>{supersededImport: ...} when the receipt an accounting receipt is about to replace is an accepted
    /// import, so its origin does not vanish with it; a receipt that already carries the record hands it on.</summary>
    public static PyDict? SupersededImport(Executor ex, PyDict check, PyDict arm)
    {
        string key = Ledger.Key(check["checkId"], arm["armId"], null);
        if (PyJson.LoadEvalFile(PyPath.Join(ex.Run, "receipts", $"{key}.json")) is not PyDict rc) return null;
        if (rc.Get("imported") is not PyDict imported || !PyValue.Truthy(Ev.OrEmpty(rc.Get("validation")).Get("accepted")))
            return rc.Get("supersededImport") is PyDict carried ? new PyDict { ["supersededImport"] = carried } : null;
        // the folders of this key only: KEY and its re-imports KEY~n
        string imports = PyPath.Join(ex.Run, "imports");
        var folders = !Directory.Exists(imports) ? []
            : PyOrder.SortedPaths(Directory.EnumerateDirectories(imports)
                    .Where(p => PyPath.Name(p).StartsWith(key, StringComparison.OrdinalIgnoreCase))
                    .Where(p => PyPath.Name(p) == key || PyPath.Name(p).StartsWith(key + "~", StringComparison.Ordinal)))
                .Select(p => (object?)PyPath.RelTo(ex.Run, p)).ToList();
        return new PyDict
        {
            ["supersededImport"] = new PyDict
            {
                ["receiptId"] = rc.Get("receiptId"), ["origin"] = PyValue.Truthy(imported.Get("origin")) ? imported["origin"] : "unknown",
                ["reusedFrom"] = imported.Get("reusedFrom"), ["sourceSha256"] = imported.Get("sourceSha256"),
                ["capturedUtc"] = imported.Get("capturedUtc"), ["folders"] = folders,
                ["note"] = "this check was credited by an import that is no longer credited; the status and cause on this receipt say why",
            },
        };
    }

    /// <summary>The first prerequisite whose own status on this arm now decides against an entry accepted earlier:
    /// such an entry is blocked, not reused.</summary>
    public static string? ReuseBlockedBy(Executor ex, PyDict check, object? armId)
    {
        foreach (var p in Ev.Items(check.Get("prerequisites")))
            if (DecidedOnArm(ex, p, armId) != null) return Ev.Str(p);
        return null;
    }

}
