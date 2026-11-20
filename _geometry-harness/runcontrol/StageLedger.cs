namespace RunControl;

/// <summary>The stages this controller runs, and the entry keys that only change what is presented.</summary>
public static class Stages
{
    public static readonly string[] Ported = ["census", "prepare", "scope", "plan", "evaluate", "records", "scenes", "bundle"];
    public static readonly HashSet<string> NonBlocking = ["precedent"];
    public static readonly HashSet<string> PresentationKeys = ["modNames", "nexusNames", "patchSearch", "report", "captureSize"];

    /// <summary>stage -> the presentation keys its code reads. A stage's entry hash binds exactly these.</summary>
    public static readonly Dictionary<string, string[]> PresentationReads = new()
    {
        ["census"] = [], ["prepare"] = [], ["scope"] = [], ["plan"] = [], ["evaluate"] = [], ["records"] = [],
        ["scenes"] = [], ["bundle"] = ["modNames"],
    };

    /// <summary>stage_outcome: accepted or refused, and whether a refusal stops the later stages.</summary>
    public static PyDict Outcome(string stage, int exitCode, bool outputsOk, string sentence = "")
    {
        bool accepted = exitCode == 0 && outputsOk;
        return new PyDict
        {
            ["stage"] = stage, ["status"] = accepted ? "accepted" : "refused",
            ["blocksLater"] = !accepted && !NonBlocking.Contains(stage), ["sentence"] = sentence,
        };
    }

    /// <summary>run_status: awaiting-judgement, complete or incomplete, from the stage outcomes.</summary>
    public static string RunStatus(IReadOnlyList<PyDict> outcomes)
    {
        if (outcomes.Any(o => o["status"] as string == "awaiting")) return "awaiting-judgement";
        return outcomes.Count > 0 && outcomes.All(o => o["status"] as string == "accepted") ? "complete" : "incomplete";
    }

    /// <summary>stage_entry_hash: the entry without its presentation keys, plus the presentation keys named.</summary>
    public static string EntryHash(PyDict entry, PyDict measured, IEnumerable<string> reads)
    {
        var bound = new PyDict(measured);
        foreach (string k in reads)
            if (entry.ContainsKey(k)) bound[k] = entry[k];
        return PyJson.Sha256Json(bound);
    }
}

/// <summary>The run's entry. A presentation key is read only by naming the reading stage, and only when the
/// table declares that stage reads it, so a stage cannot read a key its hash does not bind.</summary>
public sealed class Entry(PyDict doc)
{
    /// <summary>A measuring key (anything that is not a presentation key); null when absent.</summary>
    public object? Get(string key)
    {
        if (Stages.PresentationKeys.Contains(key))
            throw new Refusal($"entry key {key} is a presentation key and was read without naming the reading stage; "
                              + "read it through Presentation(stage, key).");
        return doc.Get(key);
    }

    /// <summary>A presentation key, for a stage the table declares as reading it; null when absent.</summary>
    public object? Presentation(string stage, string key)
    {
        if (!Stages.PresentationKeys.Contains(key))
            throw new Refusal($"entry key {key} is not a presentation key; read it through Get(key).");
        if (!Stages.PresentationReads.TryGetValue(stage, out var reads) || !reads.Contains(key))
            throw new Refusal($"stage {stage} read entry key {key}, which the stage table does not declare for it; "
                              + "add the key to that stage's row in Stages.PresentationReads so the stage's hash binds what it reads.");
        return doc.Get(key);
    }

    /// <summary>The entry without its presentation keys.</summary>
    public PyDict Measured() => new(doc.Where(kv => !Stages.PresentationKeys.Contains(kv.Key)));

    /// <summary>A stage's entry binding, over `measured` (the measuring keys, as the driver adjusted them).</summary>
    public string StageEntryHash(string stage, PyDict? measured = null)
    {
        if (!Stages.PresentationReads.TryGetValue(stage, out var reads))
            throw new Refusal($"stage {stage} has no row in Stages.PresentationReads; add one naming the presentation keys it reads.");
        return Stages.EntryHash(doc, measured ?? Measured(), reads);
    }
}

/// <summary>RUN/stages.json: one receipt per accepted stage, bound to its inputs and outputs.</summary>
public sealed class StageLedger
{
    public string FilePath { get; }
    public PyDict Doc { get; }

    public StageLedger(string run)
    {
        FilePath = PyPath.Join(run, "stages.json");
        Doc = File.Exists(FilePath)
            ? (PyDict)PyJson.ReadPipelineFile(FilePath)!
            : new PyDict { ["schema"] = 1L, ["stages"] = new PyDict(), ["history"] = new List<object?>() };
    }

    public void Save() => PyJson.WritePipelineFile(FilePath, Doc);

    PyDict StagesDoc => (PyDict)Doc["stages"]!;
    List<object?> History => (List<object?>)Doc["history"]!;

    // Every recorded output still has the fingerprint the receipt holds.
    static bool Intact(PyDict rec) =>
        (rec.Get("outputs") as PyDict ?? new PyDict()).All(kv => Equals(Hashing.Fingerprint(kv.Key), kv.Value));

    /// <summary>"reuse" when the accepted receipt matches these inputs and its outputs are unchanged, else "run".
    /// A receipt a later refusal set aside is put back, with a history line, when the inputs are again the ones it
    /// was accepted under.</summary>
    public string Decide(string stage, PyDict inputs)
    {
        string want = PyJson.Sha256Json(inputs);
        if (StagesDoc.Get(stage) is PyDict rec && Equals(rec.Get("inputsSha256"), want))
            return Intact(rec) ? "reuse" : "run";
        if (Doc.Get("kept") is PyDict keptAll && keptAll.Get(stage) is PyDict kept
            && Equals(kept.Get("inputsSha256"), want) && Intact(kept))
        {
            StagesDoc[stage] = keptAll.Pop(stage);
            History.Add(new PyDict
            {
                ["stage"] = stage, ["at"] = PyText.UtcNowPlus(), ["restored"] = true, ["inputsSha256"] = want,
                ["reason"] = "the inputs are again the ones this receipt was accepted under and its "
                             + "outputs are unchanged, so the receipt a refusal set aside is restored",
            });
            Save();
            return "reuse";
        }
        return "run";
    }

    /// <summary>Records a stage's end. Exit 0 with every output present is an accepted receipt; anything else is a
    /// history line, and the accepted receipt is set aside. Exit 0 with a missing output is refused.</summary>
    public PyDict Record(string stage, PyDict inputs, IEnumerable<string> outputs, int exitCode, PyDict? extra = null)
    {
        var outs = outputs.Select(PyPath.Str).ToList();
        var entry = new PyDict
        {
            ["stage"] = stage, ["at"] = PyText.UtcNowPlus(), ["exitCode"] = (long)exitCode,
            ["inputsSha256"] = PyJson.Sha256Json(inputs),
        };
        var missing = outs.Where(o => !File.Exists(o) && !Directory.Exists(o)).Select(PyOrder.Fwd).ToList();
        if (exitCode != 0 || missing.Count > 0)
        {
            entry["accepted"] = false;
            entry["reason"] = exitCode != 0 ? $"exit {exitCode}" : "missing outputs: " + string.Join(", ", missing);
            var prev = StagesDoc.Pop(stage);
            if (prev != null)
            {
                if (Doc.Get("kept") is not PyDict) Doc["kept"] = new PyDict();
                ((PyDict)Doc["kept"]!)[stage] = prev;
            }
            History.Add(entry);
            Save();
            if (missing.Count > 0 && exitCode == 0)
                throw new Refusal($"stage {stage} exited 0 but did not write {string.Join(", ", missing)}; it is not accepted.");
            return entry;
        }
        entry["accepted"] = true;
        entry["inputs"] = inputs;
        var fingerprints = new PyDict();
        foreach (string o in outs) fingerprints[PyOrder.Fwd(o)] = Hashing.Fingerprint(o);
        entry["outputs"] = fingerprints;
        if (extra != null) entry.Update(extra);
        StagesDoc[stage] = entry;
        (Doc.Get("kept") as PyDict)?.Pop(stage);
        var line = new PyDict();
        foreach (string k in new[] { "stage", "at", "exitCode", "inputsSha256", "accepted" }) line[k] = entry[k];
        History.Add(line);
        Save();
        return entry;
    }
}
