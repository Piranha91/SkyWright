namespace RunControl;

/// <summary>The manual decisions a person left in RUN/decisions (evalrun.py's load_decisions): one row per file,
/// accepted only when the registry allows a decision on that check and the file is attributed and evidenced.</summary>
public static class Decisions
{
    static readonly object?[] Permitted = ["completed-with-finding", "completed-no-finding", "not-applicable"];

    /// <summary>Every decisions/*.json as a row, in Python's sorted path order.</summary>
    public static List<object?> Load(string run, PyDict plan)
    {
        var rows = new List<object?>();
        string d = PyPath.Join(run, "decisions");
        if (!Ev.Exists(d)) return rows;
        var checks = new Dictionary<string, PyDict>(StringComparer.Ordinal);
        foreach (var c in PyValue.Iter(plan["checks"])) checks[PyValue.Hash(PyValue.Dict(c)["checkId"])] = PyValue.Dict(c);
        foreach (string p in Ev.SortedJson(d))
        {
            if (PyJson.LoadEvalFile(p) is not PyDict doc)
            {
                rows.Add(new PyDict { ["file"] = PyPath.Name(p), ["accepted"] = false, ["why"] = "not readable as JSON" });
                continue;
            }
            object? cid = doc.Get("checkId"), aid = doc.Get("armId");
            var check = checks.GetValueOrDefault(PyValue.Hash(cid));
            var evidence = new List<object?>();
            var row = new PyDict
            {
                ["checkId"] = cid, ["armId"] = aid, ["decidedBy"] = doc.Get("decidedBy"),
                ["decidedUtc"] = doc.Get("decidedUtc"), ["decision"] = doc.Get("decision"),
                ["note"] = doc.Get("note"), ["evidence"] = evidence, ["strength"] = "attributed-decision",
                ["accepted"] = false, ["why"] = null,
            };
            if (check == null) row["why"] = $"no registry check with id {PyText.Repr(cid)}";
            else if (!PyValue.Truthy(check.Get("manualDecisionAllowed"))) row["why"] = "the registry check does not allow a manual decision";
            else if (!PyValue.In(doc.Get("decision"), Permitted))
                row["why"] = $"decision {PyText.Repr(doc.Get("decision"))} is not one of the three permitted values";
            else if (!PyValue.Truthy(doc.Get("decidedBy"))) row["why"] = "no decidedBy: an attributed decision needs an attribution";
            else
            {
                var missing = new List<string>();
                foreach (var relRaw in Ev.Items(doc.Get("evidence")))
                {
                    string rel = relRaw as string ?? throw new PyError($"unsupported operand type(s) for /: 'WindowsPath' and '{PyValue.TypeName(relRaw)}'");
                    string f = PyPath.Join(run, rel);
                    if (Ev.Exists(f)) evidence.Add(new PyDict { ["path"] = rel, ["sha256"] = Hashing.Sha256FileOrNull(f) });
                    else missing.Add(rel);
                }
                if (missing.Count > 0) row["why"] = "evidence path(s) do not exist: " + string.Join(", ", missing);
                else row["accepted"] = true;
            }
            if (check != null && Ev.G(check["adapter"], "kind") is "mcp-only" or "unsupported")
            {
                row["satisfiesCompletion"] = false;
                row["why"] = (row["why"] as string ?? "")
                    + "; recorded, but a decision on an mcp-only/unsupported check never satisfies completion";
            }
            else row["satisfiesCompletion"] = row["accepted"];
            rows.Add(row);
        }
        return rows;
    }
}
