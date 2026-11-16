using System.Reflection;
using ViewProbe;

namespace Workbench;

/// <summary>Entry point: viewprobe's own command line builds the scene (cell, instance, profile, camera — anything
/// <c>viewprobe --interactive</c> takes), then hands it to the workbench window instead of the fly-through.</summary>
internal static class Program
{
    /// <summary>The houseCARL checkout this exe was built from (the folder holding housecarl.sln), or null.</summary>
    internal static string? CheckoutRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "housecarl.sln"))) dir = dir.Parent;
        return dir?.FullName;
    }

    /// <summary>The houseCARL server built in this checkout, beside this exe's checkout: src/housecarl-mcp/bin/Release.</summary>
    internal static string DefaultServerExe()
    {
        var root = CheckoutRoot()
            ?? throw new InvalidOperationException("this workbench was not built inside a houseCARL checkout; set WB_SERVER to the housecarl-mcp.exe it should use.");
        return Path.Combine(root, "src", "housecarl-mcp", "bin", "Release", "net9.0", "housecarl-mcp.exe");
    }

    internal static string FromCheckoutPublic(string env, string what, params string[] rel) => FromCheckout(env, what, rel);

    static string FromCheckout(string env, string what, params string[] rel)
    {
        if (Environment.GetEnvironmentVariable(env) is { Length: > 0 } v) return v;
        var root = CheckoutRoot() ?? throw new InvalidOperationException($"this workbench was not built inside a houseCARL checkout; set {env} to the {what}.");
        return Path.Combine(new[] { root }.Concat(rel).ToArray());
    }

    /// <summary>--bundle DIR [--arm A]: verify and read the bundle, rank its rows, and turn the cells its rows sit in into
    /// a worklist (one placeholder shot per cell: the workbench frames the camera on the reference after each build), so
    /// viewprobe's worklist cell switch is what loads another conflict's cell. Returns viewprobe's arguments.</summary>
    static string[]? OpenBundle(string[] args)
    {
        var rest = new List<string>(); string? dir = null; string? arm = null; string? outDir = null;
        for (int i = 0; i < args.Length; i++)
        {
            if (args[i] == "--bundle" && i + 1 < args.Length) dir = args[++i];
            else if (args[i] == "--arm" && i + 1 < args.Length) arm = args[++i];
            else { if (args[i] == "--out" && i + 1 < args.Length) outDir = args[i + 1]; rest.Add(args[i]); }
        }
        if (dir is null) return args;
        arm ??= BundleConflicts.DefaultArm(dir);               // the plan's review arm in manual mode, else baseline
        var work = Environment.GetEnvironmentVariable("WB_OUT") is { Length: > 0 } o ? o : outDir ?? Path.Combine(Path.GetTempPath(), "workbench");
        Directory.CreateDirectory(work);
        var evalrun = FromCheckout("WB_EVALRUN", "evaluation controller evalrun.py", "_geometry-harness", "evalrun.py");
        var sevPath = FromCheckout("WB_SEVERITY", "severity table", ".claude", "skills", "world-edit-compatibility", "references", "workbench-severity.json");
        Shell.Severity = SeverityTable.Load(sevPath);
        var sw = System.Diagnostics.Stopwatch.StartNew();
        Shell.Bundle = BundleConflicts.Open(dir, arm, Path.Combine(work, "bundle-verify.json"), evalrun);
        WorkbenchHost.Say($"bundle {Shell.Bundle.Dir}: plan {Shell.Bundle.PlanId}, arm {arm}; {Shell.Bundle.VerifySummary}; "
                          + FormattableString.Invariant($"verified and read in {sw.Elapsed.TotalSeconds:0.0} s"));
        foreach (var n in Shell.Bundle.Notes) WorkbenchHost.Say("bundle note: " + n);
        Shell.Ranked = ConflictRanking.Rank(Shell.Bundle.Rows, Shell.Bundle.Targets, Shell.Severity);
        Shell.Findings = FindingRanking.Rank(Shell.Bundle.Findings, Shell.Bundle.Targets, Shell.Severity);
        var first = Shell.Visible.FirstOrDefault(r => r.Row.Cell is not null);
        // a review saved earlier reopens on the row it was on, so its cell (and that cell's edits) come back first
        try
        {
            IWorkbenchSurface live = new LiveSurface();
            if (SessionFile.PathFor(live.WorkFolder, Shell.Bundle.PlanId, arm).Path is { } sp
                && SessionFile.StartRank(SessionFile.Read(sp), Shell.Visible) is { } start
                && Shell.Visible.FirstOrDefault(r => r.Rank == start && r.Row.Cell is not null) is { } saved)
            {
                first = saved;
                WorkbenchHost.Say($"reopening the saved review on conflict {start}");
            }
        }
        catch (InvalidOperationException ex) { WorkbenchHost.Say(ex.Message); }
        var firstFinding = first is null ? Shell.Findings.FirstOrDefault(f => f.Row.Clickable) : null;
        if (first is null && firstFinding is null)
        {
            // nothing to build a scene for: the results are listed in the window, and the Start tab stays usable
            Shell.WorklistCells = Array.Empty<string>(); Shell.CurrentRow = null; Shell.PendingLand = null;
            if (Shell.Window is not null) { Shell.FillConflicts(); Shell.FillFindings(); }
            Shell.SetBanner($"no row on arm '{arm}' names a placed reference to open; the results are listed ({Shell.Ranked.Count} Conflicts, {Shell.Findings.Count} Findings rows).");
            return null;
        }
        var cells = SessionCells.From(new[] { first?.Row.Cell }.Concat(Shell.Ranked.Select(r => r.Row.Cell)),
                                      new[] { firstFinding?.Row.Cell }.Concat(Shell.Findings.Where(f => f.Row.Clickable).Select(f => f.Row.Cell))).ToList();
        Shell.WorklistCells = cells;
        Shell.PendingLand = first?.Row.RefKey ?? firstFinding!.Row.RefKey;
        Shell.CurrentRow = first;
        var wl = new HarnessShared.WorklistFile("conflict review workbench: the cells the bundle's placed-object rows sit in",
            cells.Select(c => new HarnessShared.WorklistCell(c, new[] { new HarnessShared.WorklistShot(
                "placeholder pose: the workbench frames the camera on the asked-for reference once the cell is built", new[] { 0f, 0f, 0f }) })).ToList());
        var wlPath = Path.Combine(work, "workbench-worklist.json");
        HarnessShared.WorklistIo.Write(wlPath, wl);
        WorkbenchHost.Say($"{Shell.Ranked.Count} rows ranked ({Shell.Ranked.Count(r => r.InvolvesTarget)} name a target), {Shell.Findings.Count} Findings rows"
                          + (Shell.Bundle.Manual ? $" ({Shell.Bundle.RowsBefore + Shell.Bundle.FindingsBefore} more were there before the selected mods and are not listed)" : "")
                          + $"; {cells.Count} cell(s) in {wlPath}; opening on "
                          + (first is not null ? $"conflict {first.Rank}'s cell {first.Row.Cell}" : $"finding {firstFinding!.Rank}'s cell {firstFinding.Row.Cell}"));
        if (Shell.Window is not null) { Shell.FillConflicts(); Shell.FillFindings(); }   // a window the launcher opened first
        return new[] { "--worklist", wlPath }.Concat(rest).ToArray();
    }

    /// <summary>The rulebook the server reads: this checkout's generated/corpus.json (the generator writes it).</summary>
    internal static string DefaultCorpus()
    {
        var exe = DefaultServerExe();
        // the server's own default is the corpus.json beside it, where the generator writes it; else the checkout's generated/
        var beside = Path.Combine(Path.GetDirectoryName(exe)!, "corpus.json");
        return File.Exists(beside) ? beside : Path.Combine(Path.GetDirectoryName(exe)!, "..", "..", "..", "..", "..", "generated", "corpus.json");
    }

    [STAThread]
    static int Main(string[] args)
    {
        InteractiveViewer.Host = WorkbenchHost.Run;
        // no bundle and no scene asked for: the launcher (manual mode) first, in the same window and state server
        bool manual = !args.Contains("--bundle") && !args.Contains("--instance");
        int code = 0; bool restored = false;
        // manual mode: the launcher, a run, its scene; a scene the viewer could not build (it returns with the window still
        // open) is said on the banner and the window goes back to the launcher, never closing the workbench
        while (true)
        {
        if (manual)
        {
            var window = Shell.EnsureWindow("workbench — start");
            Shell.Launcher.RunBlocker = () => Shell.Current is not null ? "a scene is open in this window; restart the workbench to run another selection." : null;
            window.Show();
            // free mode remembers the instance, profile and Output Folder between sessions (workbench-settings.json beside the exe)
            if (!restored)
                window.Dispatcher.BeginInvoke(async () =>
                {
                    try { LauncherView.Say(await Shell.Launcher.RestoreAsync()); }
                    catch (Exception ex) { LauncherView.Say("the last session's settings could not be restored: " + ex.GetBaseException().Message); }
                    LauncherView.Fill();
                });
            restored = true;
            while (true)
            {
                string? ready = null;
                var frame = new System.Windows.Threading.DispatcherFrame();
                Action<string> onReady = b => window.Dispatcher.BeginInvoke(() => { ready = b; frame.Continue = false; });
                EventHandler onClosed = (_, _) => frame.Continue = false;
                Shell.Launcher.BundleReady += onReady; window.Closed += onClosed;
                if (!Shell.Closed) System.Windows.Threading.Dispatcher.PushFrame(frame);
                Shell.Launcher.BundleReady -= onReady; window.Closed -= onClosed;
                if (ready is null) { Shell.StopServer(); return 0; }
                var l = Shell.Launcher;
                // the session's writes go under the launcher's Output Folder; its re-checks and files under the Work Folder
                WorkbenchHost.ManualTarget = new WriteTarget(l.Instance!, l.Profile!, Path.Combine(l.Game!, "Data"), l.Output);
                Environment.SetEnvironmentVariable("WB_OUT", Path.Combine(l.RunFolder!, "workbench"));
                string[]? opened;
                try { opened = OpenBundle(new[] { "--bundle", ready, "--instance", l.Instance!, "--profile", l.Profile!, "--game", l.Game! }); }
                catch (InvalidOperationException ex) { Shell.SetBanner("the run's bundle could not be opened: " + ex.Message); continue; }
                if (opened is null) continue;                 // listed with no scene; another run can open the next
                args = opened;
                break;
            }
        }
        else
        {
            string[]? opened;
            try { opened = OpenBundle(args); }
            catch (InvalidOperationException ex) { Console.Error.WriteLine("workbench: " + ex.Message); return 2; }
            if (opened is null)
            {
                // a bundle with nothing to open: shown in the window with no scene until it is closed
                var window = Shell.EnsureWindow("workbench — results");
                var frame = new System.Windows.Threading.DispatcherFrame();
                window.Closed += (_, _) => frame.Continue = false;
                window.Show();
                Shell.FillConflicts(); Shell.FillFindings();
                System.Windows.Threading.Dispatcher.PushFrame(frame);
                Shell.StopServer();
                return 0;
            }
            args = opened;
        }
        var a = args.Contains("--interactive") ? args : args.Append("--interactive").ToArray();
        var entry = typeof(InteractiveViewer).Assembly.EntryPoint
            ?? throw new InvalidOperationException("viewprobe has no entry point");
        var r = entry.Invoke(null, new object[] { a });
        code = r is int c ? c : 0;
        if (manual && !Shell.Closed)
        {
            Shell.SetBanner($"the viewer stopped (exit {code}) without a scene to show, so the window is back on the Start tab; its own sentence is in the workbench log.");
            if (Shell.Window is not null) { Shell.FillConflicts(); Shell.FillFindings(); }
            continue;
        }
        break;
        }
        Shell.StopServer();
        if (Shell.ExpectFailures > 0) { Console.Error.WriteLine($"workbench: {Shell.ExpectFailures} expectation(s) failed"); return 4; }
        return code;
    }
}
