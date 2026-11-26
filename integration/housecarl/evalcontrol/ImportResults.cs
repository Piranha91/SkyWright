using RunControl;

namespace EvalControl;

/// <summary>What every `evalcontrol import` shares (moved from runcontrol's import-results with the live check, the
/// last import SkyWright's registry had; evalrun.py's cmd_import_results): Open reads the document and binds it to its
/// run's plan, check and arm, under the controller's own identity; StoreFile puts an evidence file beside the run. The
/// command itself is RecordImport's, which hands a live row to LiveImport.</summary>
public static class ImportResults
{
    /// <summary>Test seam: the controller's measured identity, in place of reading this program's own files.</summary>
    public static Func<string, PyDict, (string? refusal, PyDict measured)>? PreflightWith { get; set; }

    // One refusal: the sentence on standard error, exit 2.
    static int Refuse(string message)
    {
        Console.Error.WriteLine($"REFUSED: {message}");
        return Exit.Refused;
    }

    /// <summary>Puts src at dst: a hard link when asked and both are on one volume, else a copy. A file already at
    /// dst is removed first, so a copy is never written through an earlier link into the file it shares.</summary>
    public static string StoreFile(string src, string dst, bool link)
    {
        File.Delete(dst);
        if (link)
        {
            try
            {
                NativeFile.HardLink(dst, src);
                return "link";
            }
            catch (System.ComponentModel.Win32Exception) { }
        }
        File.Copy(src, dst);
        return "copy";
    }

    /// <summary>An import document read and bound to its run: the plan, the ledger, the document and its bytes, the
    /// check and arm it names (the arm null when the plan has none of that id), and the controller's identity.</summary>
    public sealed record Opened(string Run, string Src, PyDict Plan, Ledger Ledger, PyDict Doc, byte[] ManifestBytes,
        PyDict Programs, PyDict Check, PyDict? Arm);

    /// <summary>The import's shared start: the plan loaded and current, the controller's own code the plan's, the
    /// ledger the plan's, the document read as schema 1 of this plan and naming one of its checks. Null and the
    /// refusal printed (exit 2) when any of that fails.</summary>
    public static Opened? Open(string runFolder, string file)
    {
        string run = PyPath.Str(runFolder), src = PyPath.Str(file);
        EvalStore.SetRunStore(run);
        var (plan, why) = EvalStore.LoadPlan(run);
        if (plan == null) { Refuse(why!); return null; }
        if (EvalStore.RequestRefusal(plan) is { } outdated) { Refuse(outdated); return null; }
        // an import is accepted by the controller's own code: that code is the plan's, checked before anything is written
        if (IdentityChecks.ContractRefusal(plan) is { } noContract) { Refuse(noContract); return null; }
        var (changed, measured) = PreflightWith != null ? PreflightWith(run, plan) : ToolPreflight.Check(run, plan, only: [Receipts.Controller]);
        if (changed != null) { Refuse(changed); return null; }
        var own = PyValue.Dict(measured[Receipts.Controller]);
        var programs = new PyDict
        {
            ["controller"] = new PyDict
            {
                ["program"] = Receipts.Controller, ["kind"] = own.Get("kind"), ["digest"] = own.Get("digest"), ["checked"] = new List<object?> { "preflight" },
            },
        };
        var ledger = ImportRows.OverlayLedger(run, plan);
        if (ledger.Refusal() is { } badLedger) { Refuse(badLedger); return null; }

        byte[] manifestBytes;
        object? parsed;
        try
        {
            manifestBytes = File.ReadAllBytes(src);
            parsed = PyJson.Loads(PyJson.Decode(manifestBytes, bom: true));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or FormatException or ArgumentException)
        {
            // the words in brackets are each runtime's own
            Refuse($"--file {src}: not readable as JSON ({ex.GetType().Name}: {ex.Message})");
            return null;
        }
        if (parsed is not PyDict doc) { Refuse($"--file {src}: not readable as a JSON object"); return null; }
        if (!Ev.Eq(doc.Get("schemaVersion"), 1L))
        {
            Refuse($"schema-unsupported: imported result schemaVersion must be 1, found {PyText.Repr(doc.Get("schemaVersion"))}");
            return null;
        }
        if (!Ev.Eq(doc.Get("planId"), plan["planId"]))
        {
            Refuse($"foreign-run: imported result belongs to plan {PyText.Repr(doc.Get("planId"))}, this run is {Ev.Str(plan["planId"])}");
            return null;
        }
        object? cid = doc.Get("checkId"), aid = doc.Get("armId");
        var check = Ev.Items(plan.Get("checks")).Select(PyValue.Dict).FirstOrDefault(c => Ev.Eq(c.Get("checkId"), cid));
        var arm = Ev.Items(plan.Get("arms")).Select(PyValue.Dict).FirstOrDefault(x => Ev.Eq(x.Get("armId"), aid));
        if (check == null) { Refuse($"wrong-check: plan {Ev.Str(plan["planId"])} has no check {PyText.Repr(cid)}"); return null; }
        return new Opened(run, src, plan, ledger, doc, manifestBytes, programs, check, arm);
    }
}
