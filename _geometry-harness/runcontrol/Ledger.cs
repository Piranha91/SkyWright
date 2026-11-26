using System.Text;

namespace RunControl;

/// <summary>The run's ledger and journal (evalrun.py's Ledger): one entry per (check, arm, part), every transition
/// appended to journal.jsonl and the whole ledger rewritten atomically.</summary>
public sealed class Ledger
{
    public string Run { get; }
    public PyDict Plan { get; }
    public string PathFile { get; }
    public string Journal { get; }
    public string? RunId { get; set; }

    // What load_json gave: a dict, null when missing or corrupt, or another JSON value (which has no get, as in Python).
    object? _doc;

    /// <summary>A caller's facts read from its own receipts into an entry on rebuild (another program's receipt
    /// blocks); null adds none. runcontrol's own ledger has none.</summary>
    public Func<PyDict, PyDict?>? MoreReceiptFacts { get; init; }

    /// <summary>A caller's entry keys a transition carries forward, besides runcontrol's own.</summary>
    public IReadOnlyList<string> MoreCarried { get; init; } = [];

    public Ledger(string run, PyDict plan)
    {
        Run = PyPath.Str(run);
        Plan = plan;
        PathFile = PyPath.Join(Run, "ledger.json");
        Journal = PyPath.Join(Run, "journal.jsonl");
        _doc = PyJson.LoadEvalFile(PathFile);
    }

    /// <summary>The ledger document.</summary>
    public PyDict Doc
    {
        get => PyValue.Dict(_doc);
        set => _doc = value;
    }

    /// <summary>Whether ledger.json was there and readable as JSON.</summary>
    public bool Readable => _doc != null;

    /// <summary>doc["entries"].</summary>
    public PyDict Entries => PyValue.Dict(Doc["entries"]);

    /// <summary>The ledger key: checkId__armId, with __part when the entry has one.</summary>
    public static string Key(object? checkId, object? armId, object? part) =>
        $"{Ev.Str(checkId)}__{Ev.Str(armId)}" + (PyValue.Truthy(part) ? $"__{Ev.Str(part)}" : "");

    /// <summary>Why this ledger cannot be used: missing or corrupt, or another plan's.</summary>
    public string? Refusal()
    {
        if (_doc == null)
            return $"{PathFile} is missing or corrupt. It is the run's record of what happened; "
                + "rebuild it from the journal with `resume --rebuild-ledger` rather than guessing.";
        if (!Ev.Eq(Doc.Get("planId"), Plan["planId"]))
            return $"foreign-run: {PathFile} belongs to plan {Ev.Str(Doc.Get("planId"))}, not {Ev.Str(Plan["planId"])}";
        return null;
    }

    /// <summary>The ledger rebuilt from the journal, with the facts a transition also writes restored from each
    /// entry's own receipt.</summary>
    public void Rebuild()
    {
        var entries = new PyDict();
        _doc = new PyDict
        {
            ["schemaVersion"] = (long)EvalStore.SchemaVersion, ["planId"] = Plan["planId"],
            ["registrySha256"] = PyValue.Dict(Plan["registry"])["sha256"], ["runIds"] = new List<object?>(), ["entries"] = entries,
        };
        if (Ev.Exists(Journal))
        {
            foreach (string raw in PyText.SplitLines(Ev.ReadLoose(Journal)))
            {
                string line = PyValue.Strip(raw);
                if (line.Length == 0) continue;
                object? ev;
                try { ev = PyJson.Loads(line); }
                catch (FormatException) { continue; }
                string k = Key(Ev.G(ev, "checkId", "?"), Ev.G(ev, "armId", "?"), Ev.G(ev, "part"));
                entries[k] = new PyDict
                {
                    ["checkId"] = Ev.G(ev, "checkId"), ["armId"] = Ev.G(ev, "armId"), ["part"] = Ev.G(ev, "part"),
                    ["status"] = Ev.G(ev, "to"), ["cause"] = Ev.G(ev, "cause"),
                    ["detail"] = Ev.G(ev, "detail"), ["nextAction"] = Ev.G(ev, "nextAction"),
                    ["receiptId"] = Ev.G(ev, "receiptId"), ["runId"] = Ev.G(ev, "runId"),
                    ["receiptPath"] = $"receipts/{k}.json", ["updatedUtc"] = Ev.G(ev, "t"),
                };
            }
        }
        foreach (var (k, e) in entries) PyValue.Dict(e).Update(ReceiptFacts(k, PyValue.Dict(e)));
        Save();
    }

    /// <summary>The per-entry facts the journal does not carry, read from the receipt this entry names and no other.</summary>
    public PyDict ReceiptFacts(string key, PyDict entry)
    {
        var none = new PyDict();
        object? rid = entry.Get("receiptId");
        if (!PyValue.Truthy(rid)) return none;
        if (PyJson.LoadEvalFile(PyPath.Join(Run, "receipts", $"{key}.json")) is not PyDict r || !Ev.Eq(r.Get("receiptId"), rid)) return none;
        if (!Ev.Eq(r.Get("planId"), Plan["planId"]) || !Ev.Eq(r.Get("checkId"), entry.Get("checkId"))
            || !Ev.Eq(r.Get("armId"), entry.Get("armId")) || !Ev.Eq(r.Get("part"), entry.Get("part"))) return none;
        var o = new PyDict();
        object? detector = Ev.OrEmpty(r.Get("result")).Get("detector");
        if (detector != null)
        {
            o["detector"] = detector;
            o["result"] = new PyDict { ["detector"] = detector, ["execution"] = entry.Get("status") };
        }
        if (r.Get("validation") is PyDict v)
            o["validation"] = new PyDict { ["accepted"] = PyValue.Truthy(v.Get("accepted")), ["rejections"] = Ev.OrList(v.Get("rejections")) };
        if (MoreReceiptFacts?.Invoke(r) is { } more) o.Update(more);
        return o;
    }

    /// <summary>Records this invocation in runIds and saves.</summary>
    public void OpenInvocation(string command, string? runId, string? parent, object? budgetMinutes = null)
    {
        RunId = runId;
        var row = new PyDict { ["runId"] = runId, ["parentRunId"] = parent, ["command"] = command, ["startedUtc"] = EvalStore.UtcNow() };
        if (budgetMinutes != null) row["budgetMinutes"] = budgetMinutes;
        if (!Doc.ContainsKey("runIds")) Doc["runIds"] = new List<object?>();
        ((List<object?>)Doc["runIds"]!).Add(row);
        Save();
    }

    /// <summary>The entry of a (check, arm, part), or null.</summary>
    public PyDict? Entry(object? checkId, object? armId, object? part) => Entries.Get(Key(checkId, armId, part)) as PyDict;

    /// <summary>Moves an entry to a status: the ledger entry rewritten, one journal line appended, the ledger saved.</summary>
    public void Transition(string checkId, string armId, string? part, string to, string? cause = null, string? receiptId = null,
        string? detector = null, PyDict? extra = null, List<object?>? rejections = null, string? detail = null, string? nextAction = null)
    {
        string k = Key(checkId, armId, part);
        object? prevRaw = Entries.Get(k);
        PyDict? prev = PyValue.Truthy(prevRaw) ? PyValue.Dict(prevRaw) : null;
        object? frm = prev?.Get("status");
        bool none = rejections == null || rejections.Count == 0;
        var rec = new PyDict
        {
            ["checkId"] = checkId, ["armId"] = armId, ["part"] = part, ["status"] = to,
            ["cause"] = cause, ["receiptId"] = receiptId, ["detector"] = detector,
            ["detail"] = detail, ["nextAction"] = nextAction,
            ["validation"] = new PyDict { ["accepted"] = none, ["rejections"] = none ? new List<object?>() : rejections },
            ["result"] = new PyDict { ["detector"] = detector, ["execution"] = to },
            ["receiptPath"] = $"receipts/{k}.json", ["runId"] = RunId, ["updatedUtc"] = EvalStore.UtcNow(),
        };
        if (prev != null)
            foreach (string keep in new[] { "reusedFromRunId", "derivedFrom" }.Concat(MoreCarried))
                if (prev.ContainsKey(keep) && extra?.Get(keep) == null) rec[keep] = prev[keep];
        if (extra != null) rec.Update(extra);
        Entries[k] = rec;
        string line = PyJson.Canonical(new PyDict
        {
            ["t"] = EvalStore.UtcNow(), ["checkId"] = checkId, ["armId"] = armId, ["part"] = part,
            ["from"] = frm, ["to"] = to, ["cause"] = cause, ["receiptId"] = receiptId,
            ["detail"] = detail, ["nextAction"] = nextAction, ["runId"] = RunId,
        }) + "\n";
        // Python appends in text mode: "\n" becomes the platform's line end.
        if (Environment.NewLine != "\n") line = line.Replace("\n", Environment.NewLine);
        using (var f = new FileStream(Journal, FileMode.Append, FileAccess.Write, FileShare.Read))
        {
            byte[] bytes = PyJson.Utf8(line);
            f.Write(bytes, 0, bytes.Length);
        }
        Save();
    }

    /// <summary>Writes ledger.json atomically.</summary>
    public void Save() => PyJson.WriteEvalFile(PathFile, Doc);

    /// <summary>A running entry with no terminal line was interrupted, never completed. Returns how many were healed.</summary>
    public int HealInterrupted()
    {
        int n = 0;
        foreach (var (_, raw) in Entries.ToList())
        {
            var e = PyValue.Dict(raw);
            if (!Ev.Eq(e.Get("status"), "running")) continue;
            Transition((string)e["checkId"]!, (string)e["armId"]!, e.Get("part") as string, "interrupted",
                cause: "the process did not write a terminal transition");
            n++;
        }
        return n;
    }
}
