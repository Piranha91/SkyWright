namespace RunControl;

/// <summary>The records stage of a lab entry (evalpipeline.py's stage_records): the record capture for every arm the
/// plan left open for a record reader, then this program's own import-results for each import document the capture
/// wrote. The capture itself is the Python tool evalrecords.py, started as an external tool the way a probe is.</summary>
public static class StageRecords
{
    /// <summary>One arm's record read is stopped at this many minutes (a hung reader).</summary>
    public const long ArmMinutes = 150;

    /// <summary>Test seam: where a program name is looked for, in place of the process's PATH.</summary>
    public static Func<string?> SearchPath { get; set; } = () => Environment.GetEnvironmentVariable("PATH");

    /// <summary>The Python that runs the record capture: the machine file's `python`, else python on PATH.</summary>
    public static string Python(RunContext ctx)
    {
        const string need = "the lab records capture needs Python (it starts evalrecords.py capture)";
        if (Py.Truthy(ctx.Machine.Get("python")))
        {
            string named = PyText.Str(ctx.Machine["python"]);
            if (File.Exists(named)) return named;
            throw new Refusal($"{need}, and the machine file's python names {named}, which is not a file; set the key python to a python.exe, or remove it to use the one on PATH.");
        }
        foreach (string dir in (SearchPath() ?? "").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            string candidate;
            try { candidate = Path.Combine(dir.Trim('"'), "python.exe"); }
            catch (ArgumentException) { continue; }
            if (File.Exists(candidate)) return candidate;
        }
        throw new Refusal($"{need}, and no python.exe is on PATH; install Python 3, or name its python.exe under the key python in the machine file.");
    }

    /// <summary>`auto` or a whole number from 1 up: how many arms the capture reads at once.</summary>
    public static object ParseWorkers(object? value, string name)
    {
        string text = PyValue.Strip(PyText.Str(value));
        if (text == "auto") return "auto";
        if (PyValue.IsDigit(text) && PyValue.IntText(text) is { } n && n >= 1) return PyValue.Whole(n);
        throw new Refusal($"{name} is {PyText.Repr(value)}; it takes auto or a whole number from 1 up, and 1 reads one arm at a time.");
    }

    /// <summary>The one sentence for a capture that never started because the machine slot's wait for idle ran out;
    /// null for any other end.</summary>
    public static string? SlotWaitSentence(string log, RunContext ctx)
    {
        if (!File.Exists(log)) return null;
        string? line = PyText.SplitLines(new System.Text.UTF8Encoding(false, false).GetString(File.ReadAllBytes(log)))
            .FirstOrDefault(x => x.Contains("gave up waiting for idle", StringComparison.Ordinal));
        if (line == null) return null;
        int at = line.IndexOf(": ", StringComparison.Ordinal);
        string why = PyValue.Strip(at < 0 ? line : line[(at + 2)..]);
        var prefix = Py.Truthy(ctx.Machine.Get("heavyPrefix")) ? Py.L(ctx.Machine["heavyPrefix"]) : [];
        int flag = prefix.FindIndex(x => x is "--min-free-gb");
        string floor = flag >= 0 ? PyText.Str(prefix[flag + 1]) : "None";
        if (why.Contains("memory", StringComparison.Ordinal))
            return $"the record capture never started: the machine slot's wait for memory ran out ({why}); it needs {floor} GB free, so close other programs "
                   + "or lower --min-free-gb in the machine file's heavyPrefix, then run again.";
        return $"the record capture never started: the machine slot's wait for an idle machine ran out ({why}); end what it names, then run again.";
    }

    // Path(p).parent.name: the name of the folder a file is in.
    static string FolderName(string path)
    {
        string? dir = Path.GetDirectoryName(PyPath.Str(path));
        return string.IsNullOrEmpty(dir) ? "" : PyPath.Name(dir);
    }

    static PyDict ReadIfFile(string path) => File.Exists(path) ? Py.D(PyJson.ReadPipelineFile(path)) : new PyDict();

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
        static bool Open(object? entry) => PyText.Str(Py.D(entry).Get("status")) == "mcp-only";
        var openArms = PyOrder.SortedByCodePoint(led.Where(kv => Open(kv.Value) && kv.Key.Contains("__", StringComparison.Ordinal))
            .Select(kv => kv.Key.Split("__")[1]).Distinct(StringComparer.Ordinal));
        if (openArms.Count == 0)
        {
            PyJson.WritePipelineFile(record, new PyDict { ["note"] = "no mcp-only row is open; nothing to capture" });
            return (new PyDict { ["openArms"] = 0L }, [record]);
        }
        object workersAsked = ParseWorkers(ctx.Machine.ContainsKey("recordWorkers") ? ctx.Machine["recordWorkers"] : "auto", "the machine file's recordWorkers");
        int n = Directory.Exists(sdir) ? Directory.EnumerateFileSystemEntries(sdir, "capture-*").Count() : 0;
        string cap = PyPath.Join(sdir, $"capture-{n + 1:00}"), checkpoints = PyPath.Join(sdir, "checkpoints"), commands = PyPath.Join(sdir, "commands");
        var argv = new List<string> { Python(ctx), PyOrder.Fwd(Checkout.Script("evalrecords.py", "the record capture")), "capture", "--run", PyOrder.Fwd(run) };
        foreach (string arm in openArms) argv.AddRange(["--arm", arm]);
        argv.AddRange(["--baseline-from", PyOrder.Fwd(PyPath.Join(ctx.Out, "prepare", "preparation.json")), "--reader-cmd", StageBundle.ReaderCmd(ctx),
            "--data", PyOrder.Fwd(PyPath.Join(ctx.Game, "Data")), "--mods", PyOrder.Fwd(PyPath.Join(ctx.Instance, "mods")),
            "--profile-dir", PyOrder.Fwd(PyPath.Join(ctx.Instance, "profiles", ctx.Profile)),
            "--reader-data", PyOrder.Fwd(PyPath.Join(ctx.Out, "reader-data")), "--out", PyOrder.Fwd(cap)]);
        // finished arms are kept in the checkpoints folder, so a stop or a rerun reads only the arms not yet finished
        Directory.CreateDirectory(checkpoints);
        argv.AddRange(["--checkpoints", PyOrder.Fwd(checkpoints), "--arm-deadline-min", PyText.Str(ArmMinutes)]);
        // how many arms read at once; the workers run inside the one slot below
        argv.AddRange(["--workers", PyText.Str(workersAsked)]);
        // the read takes the machine slot, held as long as the stage's own backstop so the slot never stops it first
        long backstop = ArmMinutes * openArms.Count + 10;
        var r = ChildLaunch.Run(ctx, commands, "records-capture", argv, heavy: true, purpose: $"evalpipeline {ctx.Id} records",
            deadlineMin: backstop, holdMin: backstop);
        var summary = ReadIfFile(PyPath.Join(cap, "capture-summary.json"));
        var workers = ReadIfFile(PyPath.Join(cap, "capture-workers.json"));
        if (Py.Truthy(workers.Get("warning"))) Report.Line($"records: {PyText.Str(workers["warning"])}");
        foreach (var (arm, why) in Ev.OrEmpty(workers.Get("curedAlone")))
            Report.Line($"records: {arm} was refused beside other arms and read fine alone: {PyText.Str(why)}");
        var imports = new List<string>();
        var refusals = new PyDict();
        foreach (var (arm, raw) in summary)
        {
            var v = Py.D(raw);
            if (v.ContainsKey("refused")) refusals[arm] = v["refused"];
            if (v.ContainsKey("imports")) imports.AddRange(PyValue.Iter(v["imports"]).Select(Py.S));
        }
        // only a document for a row that was open before the capture is imported
        imports = imports.Where(d => PyText.Str(Ev.OrEmpty(led.Get(FolderName(d))).Get("status")) == "mcp-only").ToList();
        var results = new List<object?>();
        int i = 0;
        foreach (string doc in PyOrder.SortedByCodePoint(imports))
        {
            string op = $"import-{PyPath.Name(cap)}-{i++:00}";
            var rr = ChildLaunch.Run(ctx, commands, op, ChildLaunch.Controller("import-results", ["--run", PyOrder.Fwd(run), "--file", doc]));
            string log = PyPath.Join(commands, op + ".txt");
            results.Add(new PyDict { ["file"] = doc, ["exitCode"] = rr.ExitCodeUnsigned, ["log"] = File.Exists(log) ? PyOrder.Fwd(log) : null });
        }
        var kept = new PyDict();
        foreach (string k in new[] { "requested", "workers", "case", "warning", "memory", "curedAlone" }) kept[k] = workers.Get(k);
        PyJson.WritePipelineFile(record, new PyDict
        {
            ["capture"] = summary, ["captureExit"] = r.ExitCodeUnsigned, ["imports"] = results, ["refusedArms"] = refusals, ["workers"] = kept,
        });
        int bad = results.Count(x => !PyValue.Eq(Py.D(x)["exitCode"], 0L));
        if (r.ExitCodeUnsigned is not (0 or 2) || summary.Count == 0)
            throw new Refusal(SlotWaitSentence(PyPath.Join(commands, "records-capture.txt"), ctx)
                              ?? $"the record capture did not finish (exit {ChildLaunch.ExitText(r)}); read {PyOrder.Fwd(commands)}.");
        var stopped = PyOrder.SortedByCodePoint(refusals.Where(kv => PyText.Str(kv.Value).Contains("deadline", StringComparison.Ordinal)).Select(kv => kv.Key));
        if (stopped.Count > 0)
            throw new Refusal($"the record read of arm(s) {string.Join(", ", stopped)} reached its {ArmMinutes}-minute deadline; "
                              + $"run again: finished arms are kept in {PyOrder.Fwd(checkpoints)} and only these are read again.");
        if (bad > 0 || refusals.Count > 0)
            // recorded, not hidden: the evaluation stays incomplete for those rows, and the bundle says so
            Report.Line($"records: {bad} import(s) refused and {refusals.Count} arm(s) refused; see {PyOrder.Fwd(record)}");
        return (new PyDict { ["imports"] = (long)results.Count, ["refused"] = (long)(bad + refusals.Count) }, [record, cap, checkpoints]);
    }
}
