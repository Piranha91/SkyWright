using System.Reflection;
using ViewProbe;

namespace Workbench;

/// <summary>Entry point: viewprobe's own command line builds the scene (cell, instance, profile, camera — anything
/// <c>viewprobe --interactive</c> takes), then hands it to the workbench window instead of the fly-through.</summary>
internal static class Program
{
    /// <summary>The SkyWright checkout this exe was built from (the folder holding SkyWright.sln), or null.</summary>
    internal static string? CheckoutRoot() => HarnessShared.CheckoutLayout.FindRoot();

    /// <summary>The houseCARL server built in this checkout's pinned houseCARL: external/houseCARL/src/housecarl-mcp/bin/Release.</summary>
    internal static string DefaultServerExe()
    {
        var root = CheckoutRoot()
            ?? throw new InvalidOperationException("this workbench was not built inside a SkyWright checkout; set WB_SERVER to the housecarl-mcp.exe it should use.");
        return HarnessShared.CheckoutLayout.ServerExe(root);
    }

    internal static string FromCheckoutPublic(string env, string what, params string[] rel) => FromCheckout(env, what, rel);

    static string FromCheckout(string env, string what, params string[] rel)
    {
        if (Environment.GetEnvironmentVariable(env) is { Length: > 0 } v) return v;
        var root = CheckoutRoot() ?? throw new InvalidOperationException($"this workbench was not built inside a SkyWright checkout; set {env} to the {what}.");
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
        Shell.BundleOrder = LoadOrderSpec.FromArgs(rest, "the bundle's profile");
        arm ??= BundleConflicts.DefaultArm(dir);               // the plan's review arm in manual mode, else baseline
        var work = Environment.GetEnvironmentVariable("WB_OUT") is { Length: > 0 } o ? o : outDir ?? Path.Combine(Path.GetTempPath(), "workbench");
        Directory.CreateDirectory(work);
        var sevPath = FromCheckout("WB_SEVERITY", "severity table", "data", "workbench-severity.json");
        Shell.Severity = SeverityTable.Load(sevPath);
        var sw = System.Diagnostics.Stopwatch.StartNew();
        Shell.Bundle = BundleConflicts.Open(dir, arm, Path.Combine(work, "bundle-verify.json"), Shell.Launcher.Engine);
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

    /// <summary>Open a bundle folder in the window that is already shown (the Open control): its rows fill the tabs and,
    /// when a row names a placed reference, its first cell is asked for as a scene. Needs the launcher's profile, or
    /// WB_INSTANCE, WB_PROFILE and WB_GAME_DATA, to know the load order the bundle was measured on.</summary>
    internal static string OpenBundleInWindow(string dir)
    {
        var l = Shell.Launcher;
        string? inst = l.Instance ?? Env("WB_INSTANCE"), prof = l.Profile ?? Env("WB_PROFILE");
        string? game = l.Game ?? (Env("WB_GAME_DATA") is { } gd ? Path.GetDirectoryName(Path.GetFullPath(gd.TrimEnd('\\', '/'))) : null);
        if (inst is null || prof is null || game is null)
            throw new InvalidOperationException("a bundle's rows are shown on the load order they were measured on: open that profile on the Start tab first, then open the bundle.");
        if (Shell.Current is { } h && h.Session.Pending.Count > 0)
            throw new InvalidOperationException($"opening another bundle would drop {h.Session.Pending.Count} staged edit(s); write or undo them first.");
        var opened = OpenBundle(new[] { "--bundle", dir, "--instance", inst, "--profile", prof, "--game", game });
        Shell.ShowTab("Conflicts");
        if (opened is null) return Shell.BannerText;
        var order = LoadOrderSpec.FromArgs(opened, $"profile '{prof}'")!;
        var first = Shell.WorklistCells.FirstOrDefault() ?? throw new InvalidOperationException("the bundle names no cell to open.");
        return SceneDirector.Request(new SceneRequest(order, first, Shell.PendingLand, "the bundle's first row", ReadOnly: false));
    }
    static string? Env(string n) => Environment.GetEnvironmentVariable(n) is { Length: > 0 } v ? v : null;

    /// <summary>Show a sentence that would otherwise go only to the log: a message box when no parent is capturing the
    /// output (a person started the app), nothing more when one is (the tests read standard error).</summary>
    static void Tell(string sentence)
    {
        if (Console.IsErrorRedirected) return;
        System.Windows.MessageBox.Show(sentence, "SkyWright", System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Error);
    }

    [STAThread]
    static int Main(string[] args)
    {
        SessionLog.Start();                                    // no console window: the log file holds what it would have shown
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
        {
            var said = $"the workbench stopped on an error it did not expect: {(e.ExceptionObject as Exception)?.GetBaseException().Message}";
            Console.Error.WriteLine("workbench: " + said + Environment.NewLine + e.ExceptionObject);
            Tell(said + (SessionLog.FilePath is { } p ? $" The details are in {p}." : ""));
        };
        args = FrameSwitches(args);
        InteractiveViewer.Host = WorkbenchHost.Run;
        RenderStages.Tick = SceneDirector.Tick;                // the window is answered while a scene builds
        // no bundle and no scene asked for: the window first (the launcher, or the world of the profile MO2 started the workbench in)
        bool manual = !args.Contains("--bundle") && !args.Contains("--instance");
        int code = 0;
        string[]? first = null;
        if (!manual)
        {
            try { first = OpenBundle(args); }
            catch (InvalidOperationException ex) { Console.Error.WriteLine("workbench: " + ex.Message); Tell(ex.Message); return 2; }
            if (first is null)
            {
                // a bundle with nothing to open: shown in the window with no scene; the Open control and the tabs still work
                var w0 = Shell.EnsureWindow("workbench — results");
                w0.Show();
                Shell.FillConflicts(); Shell.FillFindings();
            }
        }
        else
        {
            var window = Shell.EnsureWindow("workbench — start");
            window.Show();
            // free mode remembers the instance, profile and Output Folder between sessions (workbench-settings.json beside the exe)
            window.Dispatcher.BeginInvoke(async () =>
            {
                try { LauncherView.Say(await Shell.Launcher.RestoreAsync()); }
                catch (Exception ex) { LauncherView.Say("the last session's settings could not be restored: " + ex.GetBaseException().Message); }
                LauncherView.Fill();
                OpenBar.OpenAtStart();                           // the Data folder MO2 started the workbench in, when it did
            });
        }
        Shell.Launcher.RunBlocker = () => Shell.Current is not null || SceneDirector.Running
            ? "a scene is open in this window; restart the workbench to run another selection." : null;
        string? ready = null;
        Shell.Launcher.BundleReady += b => Shell.Window?.Dispatcher.BeginInvoke(() => { ready = b; SceneDirector.Wake(); });
        // One loop for the life of the window: wait for something to show (a finished launcher run, a row or a cell asked
        // for), run viewprobe on it, and come back when its scene closes. A scene viewprobe could not build is said on the
        // banner and the window stays.
        while (true)
        {
            string[] run; LoadOrderSpec? order; SceneRequest? request = null;
            if (first is not null) { run = first; first = null; order = LoadOrderSpec.FromArgs(run, "the bundle's profile"); }
            else
            {
                if (ready is null && !SceneDirector.HasPending) SceneDirector.WaitIdle();
                if (Shell.Closed) break;
                if (SceneDirector.Take() is { } req)
                {
                    request = req;
                    run = SceneDirector.ArgsFor(req, RequestWork());
                    order = req.Order;
                }
                else if (ready is { } bundle)
                {
                    ready = null;
                    var l = Shell.Launcher;
                    // the session's writes go under the launcher's Output Folder; its re-checks and files under the Work Folder
                    WorkbenchHost.ManualTarget = new WriteTarget(l.Instance!, l.Profile!, Path.Combine(l.Game!, "Data"), l.Output);
                    Environment.SetEnvironmentVariable("WB_OUT", Path.Combine(l.RunFolder!, "workbench"));
                    string[]? opened;
                    try { opened = OpenBundle(new[] { "--bundle", bundle, "--instance", l.Instance!, "--profile", l.Profile!, "--game", l.Game! }); }
                    catch (InvalidOperationException ex) { Shell.SetBanner("the run's bundle could not be opened: " + ex.Message); continue; }
                    if (opened is null) continue;                 // listed with no scene; another run can open the next
                    run = opened; order = LoadOrderSpec.FromArgs(run, $"profile '{l.Profile}'");
                }
                else continue;
            }
            var a = run.Contains("--interactive") ? run : run.Append("--interactive").ToArray();
            var entry = typeof(InteractiveViewer).Assembly.EntryPoint
                ?? throw new InvalidOperationException("viewprobe has no entry point");
            SceneDirector.Began(order, request);
            object? r;
            try { r = entry.Invoke(null, new object[] { a }); }
            finally { SceneDirector.Ended(); }
            code = r is int c ? c : 0;
            if (Shell.Closed) break;
            if (SceneDirector.HasPending) continue;              // the scene closed for a request on another load order
            if (!manual && Shell.Window is null) break;          // the viewer stopped before any window: its exit code is the answer
            Shell.SwitchClock = null;
            Shell.SetBanner($"the viewer stopped (exit {code}) without a scene to show{(request is null ? "" : $" for cell {request.Cell}")}; its own sentence is in the workbench log.");
            if (Shell.Window is not null) { Shell.FillConflicts(); Shell.FillFindings(); }
            if (!manual && Environment.GetEnvironmentVariable("WB_SCRIPT") is { Length: > 0 }) break;   // a scripted run ends with its scene
        }
        Shell.StopServer();
        if (Shell.ExpectFailures > 0) { Console.Error.WriteLine($"workbench: {Shell.ExpectFailures} expectation(s) failed"); return 4; }
        return code;
    }

    /// <summary>The frame-rate switches, taken off the command line before viewprobe reads it: <c>--fps</c> shows the
    /// readout (as <c>WB_FPS=on</c>), <c>--fps-log PATH</c> writes the frame log from the first scene on (as <c>WB_FPS_LOG</c>).</summary>
    internal static string[] FrameSwitches(string[] args)
    {
        var rest = new List<string>();
        for (int i = 0; i < args.Length; i++)
        {
            if (args[i] == "--fps") Environment.SetEnvironmentVariable("WB_FPS", "on");
            else if (args[i] == "--fps-log" && i + 1 < args.Length) Environment.SetEnvironmentVariable("WB_FPS_LOG", args[++i]);
            else rest.Add(args[i]);
        }
        return rest.ToArray();
    }

    /// <summary>Where a requested scene's worklist and captures go: WB_OUT, else the launcher's Work Folder, else the temp folder.</summary>
    internal static string RequestWork() =>
        Env("WB_OUT") ?? (Shell.Launcher.WorkFolder is { } w ? Path.Combine(w, "workbench") : Path.Combine(Path.GetTempPath(), "workbench"));
}
