using System.Diagnostics;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using Mutagen.Bethesda.Plugins;
using Window = System.Windows.Window;

namespace Workbench;

/// <summary>The app shell, alive for the whole process while scenes come and go: the window, the Conflicts tab (the
/// bundle's ranked rows, subject rows by default, a toggle to all rows), the Records tree, and the slot the current
/// scene's 3D view sits in. viewprobe rebuilds the scene for another cell through its worklist cell switch (the host
/// returns with <c>CellSwitch</c> set); the shell keeps the window and the bundle, and lands on the asked-for reference
/// when the next scene arrives.</summary>
internal static class Shell
{
    internal static BundleConflicts? Bundle;
    internal static SeverityTable? Severity;
    internal static IReadOnlyList<RankedRow> Ranked = Array.Empty<RankedRow>();
    internal static IReadOnlyList<RankedFinding> Findings = Array.Empty<RankedFinding>();
    /// <summary>The launcher (manual mode): the Start tab and the state tools drive this one instance.</summary>
    internal static readonly Launcher Launcher = new();
    internal static TabControl? Tabs;

    /// <summary>Bring the tab with this header to the front.</summary>
    internal static string ShowTab(string? header)
    {
        if (Tabs is null) throw new InvalidOperationException("no window is shown, so there is no tab to show.");
        var items = Tabs.Items.OfType<TabItem>().ToList();
        var item = items.FirstOrDefault(t => string.Equals(t.Header as string, header?.Trim(), StringComparison.OrdinalIgnoreCase))
                   ?? throw new InvalidOperationException($"'{header}' is not a tab; show_tab takes one of {string.Join(", ", items.Select(t => t.Header))}.");
        Tabs.SelectedItem = item;
        return $"the {item.Header} tab is in front";
    }
    internal static IReadOnlyList<string> WorklistCells = Array.Empty<string>();
    internal static bool ShowAll;
    /// <summary>The "show decided" toggle: rows decided intentional that still apply are hidden unless it is on.</summary>
    internal static bool ShowDecided;
    static readonly CheckBox DecidedRows = new() { Content = "show rows decided intentional", Margin = new Thickness(6, 2, 6, 2) };
    internal static bool ShowFindingsOnly;
    static readonly CheckBox FindingsOnlyRows = new() { Content = "show cells with only Findings rows", Margin = new Thickness(6, 2, 6, 2) };
    static readonly HashSet<int> ExpandedScenes = new();
    internal static string? PendingLand;            // a reference to land on when the next scene arrives
    internal static Stopwatch? SwitchClock;         // runs from a cell switch's request to the next scene's arrival
    internal static string? SwitchCell;
    internal static int ExpectFailures;
    internal static bool Closed;
    internal static string? LastLanding;
    internal static RankedRow? CurrentRow;          // the open conflict row: its other reference is marked beside the selected one            // the last landing's one-line account, for the dump

    static Application? _app;
    internal static Window? Window;
    internal static readonly ContentControl ViewSlot = new();
    static readonly ContentControl BarSlot = new();
    static readonly TextBlock Banner = new() { Margin = new Thickness(6, 4, 6, 4), TextWrapping = TextWrapping.Wrap, FontWeight = FontWeights.SemiBold };
    static readonly ListBox ConflictList = new();
    static readonly ListBox FindingList = new();
    static readonly TreeView Records = new();
    static readonly CheckBox AllRows = new() { Content = "all rows on this arm", Margin = new Thickness(6, 2, 6, 2) };
    internal static IReadOnlyList<TreeNode> Tree = Array.Empty<TreeNode>();
    internal static WorkbenchHost? Current;

    /// <summary>The rows the Conflicts tab lists now: subject rows (A or B a plan target) unless the toggle shows all.</summary>
    internal static IReadOnlyList<RankedRow> Visible => ShowAll ? Ranked : Ranked.Where(r => r.InvolvesTarget).ToList();

    internal static string RowText(RankedRow r) =>
        $"{r.Rank}. {r.Row.Kind}  {r.Row.A} / {r.Row.B}  ref {r.Row.RefKey}{(r.Row.RefBaseEditorId is { } e ? " " + e : "")}"
        + $"  [physical {r.PhysicalLabel}; check's evidence grade {r.EvidenceLabel}; {r.NewLabel}; severity {r.Severity} ({r.SeverityLabel})]";

    internal const double MinWidth = 560 + 360, MinHeight = 480;   // the tabs' width plus a 360-wide view; device-independent units

    internal static Window EnsureWindow(string title)
    {
        if (Window is not null) return Window;
        _app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        Theme.Apply(_app);                                   // dark by default
        ScrollViewer.SetHorizontalScrollBarVisibility(ConflictList, ScrollBarVisibility.Auto);
        VirtualizingPanel.SetIsVirtualizing(Records, true);
        ConflictList.SelectionChanged += (_, _) =>
        {
            switch (ConflictList.SelectedItem)
            {
                case ListBoxItem { Tag: RankedRow r }: OpenRow(r); break;
                case ListBoxItem { Tag: Scene sc } when sc.BestRank is { } best && Visible.FirstOrDefault(x => x.Rank == best) is { } row: OpenRow(row); break;
                case ListBoxItem { Tag: RankedFinding f }: SetBanner(new LiveSurface().OpenFinding(f)); break;
                case ListBoxItem { Tag: FoldTag fold }: ExpandedScenes.Add(fold.Scene); FillConflicts(); break;
            }
        };
        Records.SelectedItemChanged += (_, e) => { if (e.NewValue is TreeViewItem { Tag: TreeNode { Kind: "ref" } n }) { CurrentRow = null; OpenRef(n.Key, CellOfRef(n.Key)); } };
        ScrollViewer.SetHorizontalScrollBarVisibility(FindingList, ScrollBarVisibility.Auto);
        FindingList.SelectionChanged += (_, _) =>
        {
            if (FindingList.SelectedItem is not ListBoxItem { Tag: RankedFinding f }) return;
            if (f.Row.Clickable) { WorkbenchHost.Say($"open finding {f.Rank}: {f.Row.Identity}"); CurrentRow = null; OpenRef(f.Row.RefKey!, f.Row.Cell); }
            else SetBanner($"finding {f.Rank} ({f.Row.CheckId}) does not open the viewer: {f.Row.WhyNot}.");
        };
        DecidedRows.Checked += (_, _) => { ShowDecided = true; FillConflicts(); };
        DecidedRows.Unchecked += (_, _) => { ShowDecided = false; FillConflicts(); };
        FindingsOnlyRows.Checked += (_, _) => { ShowFindingsOnly = true; FillConflicts(); };
        FindingsOnlyRows.Unchecked += (_, _) => { ShowFindingsOnly = false; FillConflicts(); };
        ReviewList.Changed += () => Window?.Dispatcher.BeginInvoke(FillConflicts);
        AllRows.Checked += (_, _) => { ShowAll = true; FillConflicts(); };
        AllRows.Unchecked += (_, _) => { ShowAll = false; FillConflicts(); };
        var conflictsTab = new DockPanel();
        DockPanel.SetDock(AllRows, Dock.Top); DockPanel.SetDock(DecidedRows, Dock.Top); DockPanel.SetDock(FindingsOnlyRows, Dock.Top);
        conflictsTab.Children.Add(AllRows); conflictsTab.Children.Add(DecidedRows); conflictsTab.Children.Add(FindingsOnlyRows); conflictsTab.Children.Add(ConflictList);
        var tabs = Tabs = new TabControl { Width = 560 };
        var start = new TabItem { Header = "Start", Content = LauncherView.Build() };
        tabs.Items.Add(start);
        tabs.Items.Add(new TabItem { Header = "Conflicts", Content = conflictsTab });
        tabs.SelectedIndex = Bundle is null ? 0 : 1;           // the Start tab until a bundle is open
        tabs.Items.Add(new TabItem { Header = "Findings", Content = FindingList });
        tabs.Items.Add(new TabItem { Header = "Records", Content = Records });
        var root = new DockPanel();
        DockPanel.SetDock(Banner, Dock.Top); DockPanel.SetDock(BarSlot, Dock.Bottom); DockPanel.SetDock(tabs, Dock.Left);
        root.Children.Add(Banner); root.Children.Add(BarSlot); root.Children.Add(tabs); root.Children.Add(ViewSlot);
        // The view keeps room of its own: below this the tabs would take the whole width (a 360x240 window drew the scene
        // into a 1x66 view, stage 3 receipts). A smaller size asked for opens at this minimum and says so.
        Window = new Window { Title = title, Width = 1440, Height = 960, MinWidth = MinWidth, MinHeight = MinHeight, Content = root,
                              Background = Theme.Brush(Theme.Back), Foreground = Theme.Brush(Theme.Text) };
        Window.Closed += (_, _) => { Closed = true; Current?.StopFrame(); };
        FillConflicts();
        FillFindings();
        StartServer(Window.Dispatcher);
        return Window;
    }

    /// <summary>Each decided row's standing as last compared, shared by the window's decision buttons and the state tools.</summary>
    internal static readonly Dictionary<string, DecisionMatch> Standings = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The MCP state tools' server, when <c>WB_MCP_PORT</c> asks for one (DESIGN.md section 7).</summary>
    internal static StateServer? Server;

    static void StartServer(Dispatcher ui)
    {
        if (Environment.GetEnvironmentVariable("WB_MCP_PORT") is not { Length: > 0 } ps) return;
        if (!int.TryParse(ps, out var port) || port is < 1 or > 65535)
        { WorkbenchHost.Say($"MCP state tools not started: WB_MCP_PORT '{ps}' is not a port number."); return; }
        try
        {
            // Started off the UI thread so no continuation waits for the dispatcher this thread is about to run.
            Server = Task.Run(() => StateServer.StartAsync(port, new StateHub(ui, new LiveSurface()) { Launcher = Launcher, Standings = Standings })).GetAwaiter().GetResult();
            WorkbenchHost.Say($"MCP state tools ({StateTools.StateName}, {StateTools.ActName}) served at {Server.Url}/ (Streamable HTTP)");
        }
        catch (Exception ex) { WorkbenchHost.Say($"MCP state tools not started on port {port}: {ex.GetBaseException().Message}"); }
    }

    internal static void StopServer()
    {
        if (Server is null) return;
        Task.Run(async () => await Server.DisposeAsync()).GetAwaiter().GetResult();
        Server = null;
    }

    internal static void SetBanner(string text) { Banner.Text = text; WorkbenchHost.Say("banner: " + text); }
    internal static string BannerText => Banner.Text;

    /// <summary>Put a scene's view and bar into the window (replacing the last scene's).</summary>
    internal static void Mount(UIElement view, UIElement bar) { ViewSlot.Content = view; BarSlot.Content = bar; }
    internal static void Unmount() { ViewSlot.Content = null; BarSlot.Content = null; }

    /// <summary>Let WPF draw what was just set (the loading banner) before the thread goes to rebuild the scene.</summary>
    internal static void Flush()
    {
        var f = new DispatcherFrame();
        Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.Background, () => f.Continue = false);
        Dispatcher.PushFrame(f);
    }

    /// <summary>The reporting skill's grouping data the scenes are built from, or why it is not there.</summary>
    internal static SceneKeyPolicy? ScenePolicy => _scenePolicy.Value.Policy;
    internal static string? ScenePolicyRefusal => _scenePolicy.Value.Refusal;
    static readonly Lazy<(SceneKeyPolicy? Policy, string? Refusal)> _scenePolicy = new(() =>
    {
        var path = Environment.GetEnvironmentVariable("WB_SCENE_POLICY");
        if (string.IsNullOrWhiteSpace(path))
            for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
                if (File.Exists(Path.Combine(dir.FullName, SceneKeyPolicy.RelativePath))) { path = Path.Combine(dir.FullName, SceneKeyPolicy.RelativePath); break; }
        if (string.IsNullOrWhiteSpace(path)) return (null, "the reporting skill's scene policy was not found beside the workbench, so rows are listed without scenes.");
        try { return (SceneKeyPolicy.Load(path), null); }
        catch (Exception ex) when (ex is InvalidOperationException or IOException or System.Text.Json.JsonException) { return (null, ex.Message); }
    });

    internal static void FillConflicts()
    {
        ConflictList.Items.Clear();
        var live = new LiveSurface();
        // the one list model the conflicts and scenes state parts render: scenes by cell, decision order inside each,
        // intentional rows behind the toggle
        if (ReviewList.ScenesViewFor(live, Standings, ShowDecided, ShowFindingsOnly) is { } view)
        {
            // Findings indexed by rank once; long runs folded; findings-only cells counted unless the toggle lists them
            var byRank = Findings.GroupBy(f => f.Rank).ToDictionary(g => g.Key, g => g.First());
            foreach (var it in ReviewList.Items(view, byRank, ReviewList.FindingsFoldAt, ExpandedScenes))
                ConflictList.Items.Add(it.Kind switch
                {
                    "header" => new ListBoxItem { Content = it.Text, Tag = it.Tag, FontWeight = FontWeights.Bold, Foreground = Brushes.LightSkyBlue },
                    "row" => RowItem((ReviewEntry)it.Tag!),
                    "finding" => new ListBoxItem { Content = it.Text, Tag = it.Tag, Foreground = Brushes.DarkGray },
                    "fold" => new ListBoxItem { Content = it.Text, Tag = it.Tag, FontStyle = FontStyles.Italic, Foreground = Brushes.LightSkyBlue },
                    _ => new ListBoxItem { Content = it.Text, FontStyle = FontStyles.Italic, Foreground = Brushes.DarkGray },
                });
            return;
        }
        foreach (var e in ReviewList.For(live, Visible, Standings, ShowDecided)) ConflictList.Items.Add(RowItem(e));
    }

    static ListBoxItem RowItem(ReviewEntry e)
    {
        var r = e.Row;
        string decided = e.Decision is { } d ? $"\n   {d.Verdict}: {d.Reason} — {e.Standing.Sentence}" : "";
        var item = new ListBoxItem { Content = RowText(r) + decided, Tag = r };
        if (r.InvolvesTarget) item.FontWeight = FontWeights.SemiBold;
        if (e.Standing.Standing == Standing.Reconsider) item.Foreground = Brushes.Salmon;
        else if (e.Decision is not null) item.Foreground = Brushes.DarkGray;
        return item;
    }

    /// <summary>The Findings tab: read-only, ranked; a clickable row opens the viewer, any other says why in the banner.</summary>
    internal static void FillFindings()
    {
        FindingList.Items.Clear();
        // each ticked check first: ran with its rows, or why not, so a check that failed never reads as one that found nothing
        foreach (var c in Bundle?.Checks ?? Array.Empty<CheckStatus>())
            FindingList.Items.Add(new ListBoxItem { Content = c.State == "ran" ? $"{c.CheckId}: ran, {c.Rows} row(s)" : $"{c.CheckId}: {c.State}: {c.Why}",
                                                    Foreground = c.State == "ran" ? Brushes.DarkGray : Brushes.Salmon });
        foreach (var f in Findings)
        {
            var item = new ListBoxItem
            {
                Content = $"{f.Rank}. [{f.Row.CheckId}] {f.Row.Role}, {f.NewLabel}: {f.Row.Summary}" + (f.Row.Clickable ? "" : "   (does not open the viewer)"),
                Tag = f,
            };
            if (f.InvolvesTarget) item.FontWeight = FontWeights.SemiBold;
            if (f.Row.Role == "incomplete") item.Foreground = Brushes.DarkOrange;
            FindingList.Items.Add(item);
        }
    }

    internal static void FillTree(IReadOnlyList<TreeNode> tree)
    {
        Tree = tree;
        Records.Items.Clear();
        foreach (var n in tree) Records.Items.Add(Item(n));
        static TreeViewItem Item(TreeNode n)
        {
            var t = new TreeViewItem { Header = n.Label + (n.Conflict && n.Kind == "ref" ? "   (conflict)" : ""), Tag = n, IsExpanded = n.Kind == "worldspace" };
            if (n.Conflict) { t.Foreground = Brushes.Salmon; if (n.Kind == "ref") t.FontWeight = FontWeights.Bold; }
            if (!n.Loaded) t.FontStyle = FontStyles.Italic;
            // A cell's references are made when the cell is first expanded: a city scene draws thousands.
            if (n.Kind == "cell" && n.Children.Count > 0)
            {
                t.Items.Add(new TreeViewItem { Header = $"{n.Children.Count} reference(s)" });
                bool filled = false;
                t.Expanded += (_, _) => { if (filled) return; filled = true; t.Items.Clear(); foreach (var c in n.Children) t.Items.Add(Item(c)); };
            }
            else foreach (var c in n.Children) t.Items.Add(Item(c));
            return t;
        }
    }

    internal static string? CellOfRef(string refKey) =>
        Ranked.Select(r => r.Row).FirstOrDefault(r => string.Equals(r.RefKey, refKey, StringComparison.OrdinalIgnoreCase))?.Cell;

    /// <summary>A click on a Conflicts row: land on the row's reference (its Necessary record, the one a fix touches).</summary>
    internal static void OpenRow(RankedRow r) { WorkbenchHost.Say($"open conflict {r.Rank}: {r.Row.Identity}"); CurrentRow = r; OpenRef(r.Row.RefKey, r.Row.Cell); }

    /// <summary>Land on a reference: here if this scene draws it, else rebuild the scene for its cell.</summary>
    internal static void OpenRef(string refKey, string? cell)
    {
        var h = Current;
        if (h is null) return;
        var plan = ClickThrough.Plan(refKey, cell, h.SceneCell, WorklistCells, h.Session.Pending.Count, k => h.Session.IsDrawn(k));
        switch (plan)
        {
            case ClickPlan.Land l: h.Land(l.Ref); break;
            case ClickPlan.Refuse f: SetBanner(f.Sentence); LastLanding = "refused: " + f.Sentence; break;
            case ClickPlan.Switch s:
                PendingLand = s.Ref.ToString(); SwitchCell = s.Cell; SwitchClock = Stopwatch.StartNew();
                SetBanner($"loading cell {s.Cell} to show {s.Ref}: viewprobe is rebuilding the scene; the window does not respond until it is built.");
                h.RequestCell(s.CellIndex);
                break;
        }
    }

    /// <summary>The structured state a test asserts (WB_DUMP, the script's <c>state</c> step).</summary>
    internal static object State(WorkbenchHost? h) => new
    {
        bundle = Bundle is null ? null : new
        {
            dir = Bundle.Dir, planId = Bundle.PlanId, arm = Bundle.Arm, targets = Bundle.Targets, verify = Bundle.VerifyPath,
            verifySummary = Bundle.VerifySummary, notes = Bundle.Notes,
            emitsRead = Bundle.EmitsRead.Select(e => new { receipt = e.Receipt, path = e.Path }),
            severityTable = Severity?.Source, severityStatus = Severity?.Status,
        },
        conflicts = new
        {
            showAll = ShowAll, total = Ranked.Count, subject = Ranked.Count(r => r.InvolvesTarget), listed = Visible.Count,
            rows = Visible.Select(r => new
            {
                rank = r.Rank, identity = r.Row.Identity, check = r.Row.CheckId, kind = r.Row.Kind, a = r.Row.A, b = r.Row.B,
                refKey = r.Row.RefKey, other = r.Row.OtherKey, cell = r.Row.Cell, refBase = r.Row.RefBaseEditorId, refType = r.Row.RefBaseType,
                otherType = r.Row.OtherBaseType,
                keys = new { involvesTarget = r.InvolvesTarget, physical = r.PhysicalGrade, physicalLabel = r.PhysicalLabel,
                             evidenceGrade = r.EvidenceGrade, evidenceLabel = r.EvidenceLabel, newGrade = r.NewGrade, newLabel = r.NewLabel,
                             severity = r.Severity, severityLabel = r.SeverityLabel },
            }),
        },
        worklistCells = WorklistCells,
        scene = h?.SceneState(),
        tree = Tree,
        lastLanding = LastLanding,
        recheck = h is null ? null : new
        {
            running = h.Rechecking,
            refused = h.RecheckRefusal,
            row = h.LastRecheck?.Row,
            created = h.LastRecheck?.New,
            verdict = h.LastRecheck?.Verdict,
            build = h.LastRecheck?.BuildSentence,
            wallSeconds = h.LastRecheck?.WallSeconds,
        },
    };

    internal static string Dump(WorkbenchHost? h, string path)
    {
        File.WriteAllText(path, JsonSerializer.Serialize(State(h), new JsonSerializerOptions { WriteIndented = true }));
        WorkbenchHost.Say($"state dumped to {path}");
        return path;
    }
}
