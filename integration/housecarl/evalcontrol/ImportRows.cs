using RunControl;

namespace EvalControl;

/// <summary>The import-row rule of the module's evaluate loop (moved from runcontrol's loop with the import rows): an
/// accepted import credits its row only while the row's gate still holds on the arm; one whose gate fails later is
/// blocked and recorded, and one no longer credited (invalidated, refused reuse) keeps its cause. The gate is the one an
/// import is refused by: the row's app prerequisites in the bound app bundle, then its own prerequisites in this
/// overlay, each by runcontrol's prerequisite rule.</summary>
public static class ImportRows
{
    /// <summary>The entry keys an import's transitions carry forward (Ledger.MoreCarried).</summary>
    public static readonly IReadOnlyList<string> Carried = ["imported", "importOrigin"];

    /// <summary>An import receipt's facts on a rebuilt entry (Ledger.MoreReceiptFacts).</summary>
    public static PyDict? ReceiptFacts(PyDict receipt) => receipt.Get("imported") is PyDict imp
        ? new PyDict { ["imported"] = true, ["importOrigin"] = imp.Get("origin") } : null;

    /// <summary>An overlay's ledger: runcontrol's, with the import facts.</summary>
    public static Ledger OverlayLedger(string run, PyDict plan) => new(run, plan) { MoreReceiptFacts = ReceiptFacts, MoreCarried = Carried };

    /// <summary>Whether an earlier invocation settled this row: runcontrol's rule, or an accepted import that found
    /// nothing to judge.</summary>
    public static bool AlreadyDone(Ledger ledger, object? cid, object? aid)
    {
        var e = ledger.Entry(cid, aid, null);
        return EvaluateRun.AlreadyDone(ledger, cid, aid, null)
               || (PyValue.Truthy(e) && Ev.Eq(e!.Get("status"), "not-applicable") && PyValue.Truthy(e.Get("imported")));
    }

    /// <summary>Whether a row's plan status is one an import settles.</summary>
    public static bool IsImport(object? st) => st is "mcp-only" or "live-import";

    /// <summary>The first prerequisite not settled on this arm, with the status and the sentence a row held by it is
    /// accounted with; null when the gate holds. A launched row is held by the same gate.</summary>
    public static (string pre, string status, string detail)? Gate(Executor ex, string run, PyDict plan, PyDict check, object? armId)
    {
        if (RecordImport.AppPrerequisite(run, plan, check, armId) is { } app)
            return (app.status["blocked(prerequisite:".Length..^1], app.status, app.detail);
        if (Prerequisites.Satisfied(ex, check, armId) is { } pre)
            return (pre, $"blocked(prerequisite:{pre})", Prerequisites.BlockDetail(ex, pre, armId));
        return null;
    }

    /// <summary>An already-settled import row whose gate fails now: blocked and recorded (true); false when it holds.</summary>
    public static bool BlockReused(Executor ex, string run, PyDict plan, PyDict check, PyDict arm)
    {
        if (Gate(ex, run, plan, check, arm["armId"]) is not { } g) return false;
        ex.Account(check, arm, g.status, "blocked-prerequisite", g.detail + Note, extra: SupersededImport(ex, check, arm));
        return true;
    }

    /// <summary>An import row whose import is no longer credited, accounted with its cause: blocked by name while its
    /// gate still fails on this arm, else the cause an earlier gate recorded (invalidated or refused reuse). False when
    /// the row's import was never un-credited.</summary>
    public static bool AccountUncredited(Executor ex, string run, PyDict plan, PyDict check, PyDict arm)
    {
        object? aid = arm["armId"];
        string own = ex.Ledger.Entry(check["checkId"], aid, null)?.Get("status") is string s ? s : "";
        var gate = Gate(ex, run, plan, check, aid);
        if (own.StartsWith("blocked(prerequisite:", StringComparison.Ordinal) && gate is { } g)
        {
            // an import this gate un-credited earlier stays blocked, by name, while its prerequisite still fails
            ex.Account(check, arm, g.status, "blocked-prerequisite", g.detail + Note, extra: SupersededImport(ex, check, arm));
            return true;
        }
        if (!own.StartsWith("invalidated(", StringComparison.Ordinal) && !own.StartsWith("refused-reuse(", StringComparison.Ordinal)) return false;
        // an import whose entry was invalidated or refused reuse keeps that cause
        ex.Account(check, arm, own, "stale-inputs",
            "an accepted import of this check is no longer credited: " + own
            + ". Its copied evidence stays under imports/ but backs no receipt; import it again "
            + "(the re-import is copied beside the old folder) once its inputs are what it states"
            + (gate is not { } also ? "" : $". A re-import is refused until {also.pre} is settled on this arm as well: " + also.detail),
            extra: SupersededImport(ex, check, arm));
        return true;
    }

    const string Note = ". The imported evidence stays under imports/ but is no longer credited; import it again once the prerequisite is settled on this arm";

    /// <summary>{supersededImport: ...} when the receipt an accounting receipt is about to replace is an accepted
    /// import, so its origin does not vanish with it; a receipt that already carries the record hands it on.</summary>
    static PyDict? SupersededImport(Executor ex, PyDict check, PyDict arm)
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
}
