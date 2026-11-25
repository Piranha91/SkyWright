using System.Globalization;
using HousecarlCore.Progress;

namespace RunControl;

/// <summary>One child process of a stage, through the shared runner; a heavy child goes through the machine's slot
/// command, which holds the machine slot while it runs.</summary>
public static class ChildLaunch
{
    /// <summary>How far past the slot command's own waits and hold a heavy child's outer deadline sits, in minutes.</summary>
    public static double HoldMarginMin { get; set; } = 5;

    /// <summary>The program the run log names a child's job after (its op is "&lt;label&gt;-&lt;op&gt;") and that a released
    /// lock's log line says released it. runcontrol's own commands keep "runcontrol"; another program launching
    /// children through this library sets its own name once at start, so its runs are never read as runcontrol's.</summary>
    public static string LogLabel { get; set; } = "runcontrol";

    /// <summary>Test seam: runs one job for (run-log op, log folder, deadline on the runner's clock).</summary>
    public static Func<string, string, double?, ChildJob, ChildResult> RunJob { get; set; } = RunReal;

    static ChildResult RunReal(string op, string outDir, double? deadline, ChildJob job) => new ChildRunner(op, outDir, deadline).Run(job, echo: false);

    // The minutes a slot command flag names, or the default when the flag is absent or not a number.
    static double PrefixMinutes(List<string> prefix, string flag, double fallback)
    {
        int at = prefix.IndexOf(flag);
        if (at < 0 || at + 1 >= prefix.Count) return fallback;
        return double.TryParse(prefix[at + 1].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out double v) ? v : fallback;
    }

    /// <summary>The command line of one of this program's own commands (plan, run, resume, finalize, bundle, ...), where
    /// the Python pipeline starts `python evalrun.py <sub>`.</summary>
    public static List<string> Controller(string sub, IEnumerable<string> args) => [Environment.ProcessPath!, sub, .. args];

    /// <summary>Runs `argv` as the job `op`, its log under `ledgerDir`. A heavy child is held for `holdMin` minutes
    /// when given (the slot command's --max-min), else for the stage's `deadlineMin`, else for the machine's own hold.
    /// The slot command stops its own job at that hold and releases; the runner's deadline sits past the slot
    /// command's own waits and hold, so it never stops the slot command first. Minutes are whole (long) or not (double).</summary>
    public static ChildResult Run(RunContext ctx, string ledgerDir, string op, IEnumerable<string> argv, bool heavy = false,
                                  string? purpose = null, object? deadlineMin = null, IReadOnlyDictionary<string, string>? env = null,
                                  object? holdMin = null)
    {
        string? lockFile = null;
        var args = argv.ToList();
        double? deadlineMinutes = Py.Truthy(deadlineMin) ? Py.Float(deadlineMin) : null;
        if (heavy && Py.Truthy(ctx.Machine.Get("heavyPrefix")))
        {
            // one lock file per mod and step: two runs of different mods never name the same lock
            lockFile = PyPath.Join(Py.Truthy(ctx.Machine.Get("lockDir")) ? Py.S(ctx.Machine["lockDir"]) : ctx.Out, $"HEAVY-LOCK-{ctx.Id}-{op}");
            var prefix = Py.L(ctx.Machine["heavyPrefix"]).Select(x => Py.S(x).Replace("{purpose}", purpose ?? op).Replace("{lock}", PyOrder.Fwd(lockFile))).ToList();
            object? hold = holdMin ?? deadlineMin;
            if (hold != null)
            {
                int at = prefix.IndexOf("--max-min");
                if (at >= 0) prefix[at + 1] = PyText.Str(hold);
                else prefix.AddRange(["--max-min", PyText.Str(hold)]);
            }
            if (deadlineMinutes is { } own)
                // the deadline counts from launch, the hold from when the lock is taken: cover the turn and idle waits too
                deadlineMinutes = PrefixMinutes(prefix, "--slot-wait-min", 240) + PrefixMinutes(prefix, "--wait-timeout-min", 0)
                                  + PrefixMinutes(prefix, "--max-min", own) + HoldMarginMin;
            args = prefix.Append("--").Concat(args).ToList();
        }
        var job = new ChildJob(op, args, Checkout.Root, env);
        bool lockBefore = lockFile != null && File.Exists(lockFile);
        double? deadline = deadlineMinutes is { } minutes ? ChildRunner.Now() + 60 * minutes : null;
        var res = RunJob(LogLabel + "-" + op, PyPath.Str(ledgerDir), deadline, job);
        if (lockFile != null) AddWaited(PyPath.Join(ledgerDir, op + ".txt"));
        if (lockFile != null && !lockBefore && !string.IsNullOrEmpty(res.Stopped) && File.Exists(lockFile)) ReleaseStoppedLock(lockFile, res);
        return res;
    }

    static readonly object WaitGate = new();
    static double _waited;

    /// <summary>Seconds the heavy children launched since the last call spent waiting for the machine (the slot
    /// command's slot and idle waits, from its "taken" line), and resets the count.</summary>
    public static double TakeWaited() { lock (WaitGate) { double w = _waited; _waited = 0; return w; } }

    // Adds the waitedSeconds of every "taken" line in a heavy child's log; a log without one adds nothing.
    internal static void AddWaited(string log)
    {
        double sum = 0;
        try
        {
            foreach (var line in File.ReadLines(log))
            {
                var t = line.Trim();
                if (!t.StartsWith('{') || !t.Contains("\"step\": \"taken\"", StringComparison.Ordinal)) continue;
                if ((PyJson.Loads(t) as PyDict)?.Get("waitedSeconds") is { } w && Py.Truthy(w)) sum += Py.Float(w);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or FormatException or ArgumentException or PyError) { }
        lock (WaitGate) _waited += sum;
    }

    /// <summary>The lock a slot command took before a stop from outside killed it, so its own release never ran.
    /// Removed, with a release line in its log, only when the runner confirmed the whole tree gone; otherwise left,
    /// and the stage refuses.</summary>
    public static void ReleaseStoppedLock(string lockFile, ChildResult res)
    {
        object? held = null;
        try { held = (PyJson.Loads(PyJson.Decode(File.ReadAllBytes(lockFile), bom: false)) as PyDict)?.Get("pid"); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or FormatException or ArgumentException) { }
        if (res.TreeStopped != true)
            throw new Refusal($"the stage's slot command was stopped from outside ({res.Stopped}) and its process tree was "
                              + $"not confirmed gone, so its lock {PyOrder.Fwd(lockFile)} is left in place; check nothing it started still runs, "
                              + "then delete it.");
        File.Delete(lockFile);
        var now = DateTime.Now;
        var zone = TimeZoneInfo.Local;
        var rec = new PyDict
        {
            ["step"] = "released",
            ["reason"] = $"outer-stop: the stage's {res.Stopped} stopped the slot command; its process tree was confirmed gone",
            ["by"] = LogLabel, ["heldPid"] = held,
            ["t"] = now.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture) + " " + (zone.IsDaylightSavingTime(now) ? zone.DaylightName : zone.StandardName),
            ["lock"] = lockFile,
        };
        File.AppendAllText(PyPath.Join(Path.GetDirectoryName(lockFile)!, "idle_lock.log"), PyJson.Dumps(rec) + Environment.NewLine, new System.Text.UTF8Encoding(false));
        Report.Line($"released the stopped slot command's lock {PyOrder.Fwd(lockFile)} (its tree was confirmed gone)");
    }

    /// <summary>A child's exit code as the sentences state it: the number Python reports, or None for a child that
    /// never ended on its own.</summary>
    public static string ExitText(ChildResult r) => r.ExitCodeUnsigned is { } c ? c.ToString(CultureInfo.InvariantCulture) : "None";
}
