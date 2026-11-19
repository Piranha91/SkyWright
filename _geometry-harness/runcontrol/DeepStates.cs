namespace RunControl;

/// <summary>A Deep plan's states for one check on one arm (evalrun.py's deep_states): generated from the arm's own
/// accepted switch census and written beside the check's output.</summary>
public static class DeepStates
{
    public const string CensusCheck = "state.switch-census";

    /// <summary>The output file of the arm's accepted state.switch-census receipt, or null when there is none.</summary>
    public static string? AcceptedCensus(Executor ex, PyDict arm)
    {
        var e = ex.Ledger.Entry(CensusCheck, arm["armId"], null);
        object? r = PyJson.LoadEvalFile(PyPath.Join(ex.Run, "receipts", $"{CensusCheck}__{Ev.Str(arm["armId"])}.json"));
        if (!PyValue.Truthy(e) || !EvalStore.IsTerminalOk(e!.Get("status")) || !PyValue.Truthy(r)) return null;
        if (!PyValue.Truthy(PyValue.Dict(PyValue.Dict(r)["validation"])["accepted"])) return null;
        var outs = Ev.Items(Ev.G(r, "outputs")).ToList();
        return outs.Count > 0 && PyValue.Truthy(Ev.G(outs[0], "path")) ? PyPath.Join(ex.Run, Ev.Str(Ev.G(outs[0], "path"))) : null;
    }

    /// <summary>The states file handed to the probe (ARM.states.txt, one spec per line) and what the receipt records
    /// of it, with the document ARM.states.json written beside it. Null when the row was accounted here instead: no
    /// accepted census, no states-file flag in the registry, an unreadable census, or a census with no state to judge.</summary>
    public static (string path, PyDict record)? For(Executor ex, PyDict check, PyDict arm)
    {
        string? census = AcceptedCensus(ex, arm);
        if (census == null || !Ev.Exists(census))
        {
            ex.Account(check, arm, "blocked(no-switch-map)", null,
                "this plan is Deep, and there is no accepted state.switch-census receipt on this arm, so there "
                + "is no census to generate its states from");
            return null;
        }
        if (!PyValue.Truthy(Ev.OrEmpty(check.Get("adapter")).Get("statesFileFlag")))
        {
            ex.Account(check, arm, "blocked(no-states-file-flag)", null,
                "this plan is Deep, and the registry gives this check no statesFileFlag, so its probe cannot be "
                + "handed the census's states");
            return null;
        }
        PyDict doc;
        // the generator is this controller, named by its package digest, as the plan names it
        try { doc = StateGen.GenerateFile(census, (string)ToolIdentity.Controller()["digest"]!); }
        catch (Exception why) when (why is IOException or UnauthorizedAccessException or FormatException)
        {
            ex.Account(check, arm, "blocked(census-unreadable)", null,
                $"this plan is Deep, and this arm's census could not be read to generate its states ({why.Message})");
            return null;
        }
        string armId = Ev.Str(arm["armId"]);
        string outDir = PyPath.Join(ex.Run, "outputs", Ev.Str(check["checkId"]));
        Directory.CreateDirectory(outDir);
        string jpath = PyPath.Join(outDir, $"{armId}.states.json"), tpath = PyPath.Join(outDir, $"{armId}.states.txt");
        Ev.WriteText(jpath, StateGen.Dumps(doc));
        Ev.WriteText(tpath, StateGen.SpecsText(doc));
        var states = PyValue.Iter(doc["states"]).ToList();
        var record = new PyDict
        {
            ["depth"] = "deep", ["count"] = (long)states.Count,
            ["file"] = PyPath.RelativeTo(tpath, ex.Run), ["sha256"] = Hashing.Sha256FileOrNull(tpath),
            ["document"] = PyPath.RelativeTo(jpath, ex.Run), ["documentSha256"] = Hashing.Sha256FileOrNull(jpath),
            ["censusSha256"] = Ev.OrEmpty(doc.Get("census")).Get("sha256"),
            ["generatorSha256"] = Ev.OrEmpty(doc.Get("generator")).Get("sha256"),
            ["notExplored"] = (long)PyValue.Len(doc["notExplored"]),
        };
        if (states.Count == 0)
        {
            ex.Account(check, arm, "not-applicable", null,
                "the census found no switch on this arm that can be flipped, so there is no switched state to "
                + $"judge (the states document {Ev.Str(record["document"])} lists what was not explored)");
            return null;
        }
        return (tpath, record);
    }
}
