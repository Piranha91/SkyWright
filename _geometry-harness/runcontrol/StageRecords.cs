namespace RunControl;

/// <summary>The records stage: what the run holds for its mcp-only checks, the rows a record reader completes. This
/// controller starts no record reader. A lab run's records are captured and imported (through this program's own
/// `import-results`) by the AI-driven evaluation between its evaluate and records stages; this stage then lists every
/// mcp-only row still open, so the bundle and the reader see which were not assessed. An in-place run (the app's)
/// offers no record check at all.</summary>
public static class StageRecords
{
    /// <summary>What a lab run's stage writes beside the capture's own records.json.</summary>
    public const string OpenRowsFile = "open-rows.json";

    public static (PyDict?, List<string>) Records(RunContext ctx, PyDict res, string sdir)
    {
        string run = PyPath.Join(ctx.Out, "evaluation"), record = PyPath.Join(sdir, "records.json");
        if (ctx.InPlace != null)
        {
            // an in-place run offers no model-driven record check: the note is the stage's whole output
            PyJson.WritePipelineFile(record, new PyDict
            {
                ["note"] = "a manual run offers no model-driven record check; mcp-only rows stay open and the bundle says they were not assessed",
            });
            return (new PyDict { ["openArms"] = 0L }, [record]);
        }
        var led = Ev.OrEmpty(Py.D(PyJson.ReadPipelineFile(PyPath.Join(run, "ledger.json"))).Get("entries"));
        var byArm = new Dictionary<string, List<object?>>(StringComparer.Ordinal);
        foreach (string key in PyOrder.SortedByCodePoint(led.Keys))
        {
            if (PyText.Str(Py.D(led[key]).Get("status")) != "mcp-only" || !key.Contains("__", StringComparison.Ordinal)) continue;
            string arm = key.Split("__")[1];
            if (!byArm.TryGetValue(arm, out var rows)) byArm[arm] = rows = [];
            rows.Add(Py.D(led[key]).Get("checkId") ?? key.Split("__")[0]);
        }
        var open = new PyDict();   // the arms in code-point order, each arm's checks in its rows' key order
        foreach (string arm in PyOrder.SortedByCodePoint(byArm.Keys)) open[arm] = byArm[arm];
        string file = PyPath.Join(sdir, OpenRowsFile);
        long count = open.Sum(kv => (long)Py.L(kv.Value).Count);
        PyJson.WritePipelineFile(file, new PyDict
        {
            ["open"] = open,
            ["note"] = count == 0
                ? "no mcp-only row is open"
                : "these mcp-only rows have no imported record check: they stay open and the bundle says they were not assessed",
        });
        if (count > 0)
            Report.Line($"records: {count} mcp-only row(s) on arm(s) {string.Join(", ", open.Keys)} have no imported record check; "
                        + "they stay open and the bundle says they were not assessed.");
        return (new PyDict { ["openRows"] = count }, [file]);
    }
}
