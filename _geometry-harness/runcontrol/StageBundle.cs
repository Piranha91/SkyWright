namespace RunControl;

/// <summary>The bundle stage (evalpipeline.py's stage_bundle): this program's own finalize, bundle and verify-bundle
/// as children, one after the other, each held to its exit code.</summary>
public static class StageBundle
{
    /// <summary>Where a run's bundle is written.</summary>
    public static string BundleDir(RunContext ctx) => PyPath.Join(ctx.Out, "bundle", "evidence");

    /// <summary>The record reader's command: the machine file's readerCmd, else HOUSECARL_RECORD_READER_CMD.</summary>
    public static string ReaderCmd(RunContext ctx)
    {
        object? cmd = Py.Truthy(ctx.Machine.Get("readerCmd")) ? ctx.Machine["readerCmd"] : Environment.GetEnvironmentVariable("HOUSECARL_RECORD_READER_CMD");
        if (!Py.Truthy(cmd))
            throw new Refusal("no record reader is named: set readerCmd in the machine file (a houseCARL server whose folder "
                              + "carries its generated corpus.json).");
        return PyText.Str(cmd);
    }

    /// <summary>The reader's environment for the names read: a private, empty instance under the run folder, so every
    /// record is read by absolute path and never from an active copy of the same plugin.</summary>
    public static List<KeyValuePair<string, string>> NamesEnv(RunContext ctx) =>
        PrivateInstance.Create(PyPath.Join(ctx.Out, "names-instance")).Select(kv => new KeyValuePair<string, string>(kv.Key, PyOrder.Fwd(Py.S(kv.Value)))).ToList();

    public static (PyDict?, List<string>) Bundle(RunContext ctx, PyDict res, string sdir)
    {
        string run = PyPath.Join(ctx.Out, "evaluation");
        string bundle = BundleDir(ctx);
        if (Ev.Exists(bundle))
            throw new Refusal($"{PyOrder.Fwd(bundle)} exists from an earlier bundle; move it aside rather than overwrite it.");
        // the machine file's readerInProcess reads the names with houseCARL's code compiled in; else the named reader program
        var bundleArgs = new List<string> { "--run", PyOrder.Fwd(run), "--out", PyOrder.Fwd(bundle) };
        if (Py.Truthy(ctx.Machine.Get("readerInProcess")) && !Py.Truthy(ctx.Machine.Get("readerCmd"))) bundleArgs.Add(InProcessReader.Flag);
        else bundleArgs.AddRange(["--reader-cmd", ReaderCmd(ctx)]);
        foreach (var (k, v) in NamesEnv(ctx)) bundleArgs.AddRange(["--reader-env", $"{k}={v}"]);
        foreach (var (p, n) in Py.DictOrEmpty(Py.Truthy(ctx.Entry.Presentation("bundle", "modNames")) ? ctx.Entry.Presentation("bundle", "modNames") : null))
            bundleArgs.AddRange(["--mod-name", $"{p}={PyText.Str(n)}"]);
        (string name, List<string> argv, long[] ok)[] steps =
        [
            ("finalize", ChildLaunch.Controller("finalize", ["--run", PyOrder.Fwd(run)]), [0, 3]),
            ("bundle", ChildLaunch.Controller("bundle", bundleArgs), [0]),
            ("verify-bundle", ChildLaunch.Controller("verify-bundle", [PyOrder.Fwd(bundle)]), [0]),
        ];
        string commands = PyPath.Join(sdir, "commands"), record = PyPath.Join(sdir, "bundle.json");
        var done = new List<object?>();
        foreach (var (name, argv, ok) in steps)
        {
            var r = ChildLaunch.Run(ctx, commands, name, argv);
            done.Add(new PyDict { ["step"] = name, ["exitCode"] = r.ExitCodeUnsigned });
            if (r.ExitCodeUnsigned is not { } code || !ok.Contains(code))
            {
                PyJson.WritePipelineFile(record, new PyDict { ["steps"] = done });
                throw new Refusal($"evalrun {name} ended with exit {ChildLaunch.ExitText(r)}; read {PyOrder.Fwd(PyPath.Join(commands, name + ".txt"))}.");
            }
        }
        string logFile = PyPath.Join(commands, "bundle.txt");
        string log = File.Exists(logFile) ? Ev.ReadText(logFile) : "";
        var names = PyText.SplitLines(log).Where(l => l.StartsWith("names:", StringComparison.Ordinal)).ToList();
        if (names.Count > 0 && names[^1].Contains("refused", StringComparison.Ordinal))
            throw new Refusal("the bundle was written but its names enrichment " + Ev.Head(PyValue.Strip(names[^1]["names:".Length..]), 300));
        string dispFile = PyPath.Join(run, "disposition.json");
        var disp = Py.D(PyJson.ReadPipelineFile(dispFile));
        var evaluation = disp.ContainsKey("evaluation") ? Py.D(disp["evaluation"]) : new PyDict();
        PyJson.WritePipelineFile(record, new PyDict
        {
            ["steps"] = done, ["bundle"] = PyOrder.Fwd(bundle), ["evaluation"] = evaluation.Get("claim"),
            ["complete"] = evaluation.Get("complete"), ["ledgerCounts"] = disp.Get("ledgerCounts"),
        });
        return (new PyDict { ["disposition"] = Hashing.Sha256File(dispFile) }, [record, PyPath.Join(bundle, "manifest.json")]);
    }
}
