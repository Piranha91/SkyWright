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
    /// <summary>The navmesh repair of a Findings row: the open bundle's arm order, the write target, this checkout's navownprobe.</summary>
    internal static readonly RepairDesk Repair = new(() =>
    {
        var b = Bundle ?? throw new InvalidOperationException("no bundle is open, so the repair has no order to plan on.");
        var t = WorkbenchHost.TargetFromEnv() ?? throw new InvalidOperationException("no write target is set (the launcher's profile, or WB_INSTANCE, WB_PROFILE and WB_GAME_DATA), so the order cannot be read.");
        return (BundleConflicts.CandidateFile(b.Dir, b.Arm), t, NavRepairLane.ProbeExe(Launcher.Engine.HarnessDir()), RepairWork());
    }, t => PatchWriter.StartAsync(Environment.GetEnvironmentVariable("WB_SERVER") is { Length: > 0 } s ? s : Program.DefaultServerExe(), t,
                                   Path.Combine(RepairWork(), "housecarl-data-repair"),
                                   Environment.GetEnvironmentVariable("WB_CORPUS") is { Length: > 0 } cp ? cp : Program.DefaultCorpus(), WorkbenchHost.Say));
    static string RepairWork() => Environment.GetEnvironmentVariable("WB_OUT") is { Length: > 0 } o ? o : Path.Combine(Path.GetTempPath(), "workbench");

    /// <summary>Each view by name: the group tab it sits in, and its tab inside the group (null for Start, a group of its
    /// own). The names are the views' own (Start, Conflicts, Findings, Results, Records, World) and their headers.</summary>
    static readonly Dictionary<string, (TabItem Group, TabItem? Inner, string Says)> Views = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Bring a view to the front: its group, and its tab inside the group.</summary>
    internal static string ShowTab(string? header)
    {
        if (Tabs is null) throw new InvalidOperationException("no window is shown, so there is no tab to show.");
        var key = header?.Trim() ?? "";
        if (!Views.TryGetValue(key, out var v))
            throw new InvalidOperationException($"'{header}' is not a tab; show_tab takes one of {string.Join(", ", Views.Keys.Distinct(StringComparer.OrdinalIgnoreCase))}.");
        Tabs.SelectedItem = v.Group;
        if (v.Inner is { Parent: TabControl inner }) inner.SelectedItem = v.Inner;
        return $"the {v.Says} tab is in front";
    }

    /// <summary>A group of views: a tab of the window holding its own row of tabs.</summary>
    static TabItem Group(string header, string tip, params (string Key, string Header, string Tip, object Content)[] views)
    {
        var inner = new TabControl { Margin = new Thickness(0, 4, 0, 0) };
        var group = new TabItem { Header = header, Content = inner, ToolTip = tip };
        Views[header] = (group, null, header);
        foreach (var (key, h, t, content) in views)
        {
            var item = new TabItem { Header = h, Content = content, ToolTip = t };
            inner.Items.Add(item);
            Views[key] = Views[h] = (group, item, $"{header} › {h}");
        }
        // the group's own name brings its first view
        Views[header] = (group, (TabItem)inner.Items[0], $"{header} › {((TabItem)inner.Items[0]).Header}");
        return group;
    }
    internal static IReadOnlyList<string> WorklistCells = Array.Empty<string>();
    /// <summary>The load order the open bundle's rows are shown on, when the session named it in full; null otherwise.</summary>
    internal static LoadOrderSpec? BundleOrder;
    /// <summary>The scene being built or shown takes no edits (opened from the Results or World tab).</summary>
    internal static bool ReadOnlyScene;
    /// <summary>For a scene opened from a list with no conflict row open: the other reference to mark beside the selected one.</summary>
    internal static Func<string, string?>? PartnerFor;
    /// <summary>A few words the status line leads with while a read-only scene is shown (which setup it is), or null.</summary>
    internal static string? SceneCaption;
    internal static bool ShowAll;
    /// <summary>The "show decided" toggle: rows decided intentional that still apply are hidden unless it is on.</summary>
    internal static bool ShowDecided;
    static readonly CheckBox DecidedRows = new() { Content = "show clashes marked as intended", Margin = new Thickness(6, 2, 6, 2),
        ToolTip = "clashes you marked as intended stay hidden while the mark still applies" };
    internal static bool ShowFindingsOnly;
    static readonly CheckBox FindingsOnlyRows = new() { Content = "show places with only other issues", Margin = new Thickness(6, 2, 6, 2),
        ToolTip = "places where the checks found other issues (Other issues tab) but no clash between mods" };
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
    static readonly CheckBox AllRows = new() { Content = "show every clash, not only the checked mod's", Margin = new Thickness(6, 2, 6, 2),
        ToolTip = "off: only the clashes that involve the mod you checked; on: every clash this check found in the load order" };
    internal static IReadOnlyList<TreeNode> Tree = Array.Empty<TreeNode>();
    internal static WorkbenchHost? Current;

    /// <summary>The rows the Conflicts tab lists now: subject rows (A or B a plan target) unless the toggle shows all.</summary>
    internal static IReadOnlyList<RankedRow> Visible => ShowAll ? Ranked : Ranked.Where(r => r.InvolvesTarget).ToList();

    internal static string RowText(RankedRow r) =>
        $"{r.Rank}. {r.Row.Kind}  {r.Row.A} / {r.Row.B}{(r.Row.Mesh is not null ? " (its mesh)" : "")}  ref {r.Row.RefKey}{(r.Row.RefBaseEditorId is { } e ? " " + e : "")}"
        + $"  [physical {r.PhysicalLabel}; check's evidence grade {r.EvidenceLabel}; {r.NewLabel}; severity {r.Severity} ({r.SeverityLabel})]"
        + (r.Row.MeshSentence is { } ms ? "\n      " + ms : "");

    /// <summary>A row as the list shows it: rank, what clashes, the two mods and the object; the grades are in its tooltip.</summary>
    internal static string RowLabel(RankedRow r) =>
        $"{r.Rank}. {r.Row.Kind}  {r.Row.A} / {r.Row.B}  ·  {r.Row.RefBaseEditorId ?? r.Row.RefKey}";

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
                case ListBoxItem { Tag: ClashEntry c }:
                    try { SetBanner(ClashList.OpenEntry(c)); } catch (InvalidOperationException ex) { SetBanner(ex.Message); }
                    break;
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
        // a navmesh Findings row's repair (RepairDesk): previewed, then written to a new patch, read back and re-checked
        var repairPreview = new Button { Content = "Preview repair", Margin = new Thickness(4), ToolTip = "plan the selected navmesh row's repair: relink a border, or follow moved ground; nothing is written" };
        var repairWrite = new Button { Content = "Write repair", Margin = new Thickness(4), ToolTip = "write the previewed repair to a new patch under the Output Folder, read it back and re-check it" };
        RankedFinding? Selected() => FindingList.SelectedItem is ListBoxItem { Tag: RankedFinding f } ? f : null;
        repairPreview.Click += async (_, _) =>
        {
            if (Selected() is not { } f) { SetBanner("select a navmesh row under Other issues first."); return; }
            try { var p = await Repair.PreviewAsync(f); SetBanner("repair preview: " + p.Sentence); foreach (var e in p.Plans) foreach (var i in e.Invariants) WorkbenchHost.Say($"repair preview: {e.Navmesh}: {i}"); }
            catch (Exception ex) { SetBanner("repair preview refused: " + ex.Message); }
        };
        repairWrite.Click += async (_, _) =>
        {
            if (Selected() is not { } f) { SetBanner("select a navmesh row under Other issues first."); return; }
            try { var r = await Repair.WriteAsync(f, "WbNavRepair"); SetBanner("repair: " + r.Sentence); foreach (var (row, kind, verdict) in r.Verdicts) WorkbenchHost.Say($"repair re-check: {kind} {row}: {verdict}"); }
            catch (Exception ex) { SetBanner("repair write refused: " + ex.Message); }
        };
        repairPreview.IsEnabled = repairWrite.IsEnabled = false;
        ToolTipService.SetShowOnDisabled(repairPreview, true); ToolTipService.SetShowOnDisabled(repairWrite, true);
        FindingList.SelectionChanged += (_, _) => repairPreview.IsEnabled = repairWrite.IsEnabled = Selected() is not null;
        var repairBar = new StackPanel { Orientation = Orientation.Horizontal };
        repairBar.Children.Add(repairPreview); repairBar.Children.Add(repairWrite);
        var findingsTab = new DockPanel();
        DockPanel.SetDock(repairBar, Dock.Top);
        findingsTab.Children.Add(repairBar); findingsTab.Children.Add(FindingList);
        // Three tabs: Start (load, check), Conflicts (what was found, three ways), Explore (the load order as it is)
        var tabs = Tabs = new TabControl { MinWidth = 380 };
        var start = new TabItem { Header = "Start", Content = LauncherView.Build(), ToolTip = "load a Data folder, a profile or earlier results, and check a mod" };
        Views["Start"] = (start, null, "Start");
        tabs.Items.Add(start);
        tabs.Items.Add(Group("Conflicts", "what the checks found",
            ("Conflicts", "In the world", "clashes between mods at a place in the world; pick one to see it in the view", conflictsTab),
            ("Findings", "Other issues", "what the checks found that is not a clash at one place: missing masters, navmesh, patch advice", findingsTab),
            ("Results", "Earlier results", "an earlier evaluation's results, pair by pair", ResultsView.Build())));
        tabs.Items.Add(Group("Explore", "the load order as it is, without a check",
            ("Records", "Records", "the cells and references of the scene shown, by worldspace", Records),
            ("World", "World", "the map and cells of the loaded Data folder or profile", WorldView.Build())));
        tabs.SelectedIndex = Bundle is null ? 0 : 1;           // the Start tab until a bundle is open
        ViewSlot.Content ??= EmptyView;
        if (Records.Items.Count == 0) Records.Items.Add(new TreeViewItem { Header = "Open a clash or a place to list its cells and references here.", IsEnabled = false, FontStyle = FontStyles.Italic });
        var root = new DockPanel();
        // one Open control for anything the window opens; each kind is a reader (InputReaders)
        InputReaders.Register(new ClashListReader());
        InputReaders.Register(new ResultsInputReader());
        InputReaders.Register(new BundleReader());
        InputReaders.Register(new ProfileReader());
        InputReaders.Register(new DataFolderReader());
        OpenBar.StartInput = DataFolderOrder.StartGame;
        var openBar = OpenBar.Build();
        DockPanel.SetDock(openBar, Dock.Top);
        DockPanel.SetDock(Banner, Dock.Top);
        // the tabs and the 3D view side by side, with a splitter to drag: a long list can be given room, the view keeps its minimum
        var top = new Grid();
        top.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(560), MinWidth = 380 });
        top.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        top.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star), MinWidth = 360 });
        var splitter = new GridSplitter { Width = 5, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Stretch,
                                          Background = Theme.Brush(Theme.Line), ToolTip = "drag to give the lists or the view more room" };
        // the view with its own bar right under it, so a table below never sits between them
        var viewColumn = new DockPanel();
        DockPanel.SetDock(BarSlot, Dock.Bottom);
        viewColumn.Children.Add(BarSlot); viewColumn.Children.Add(ViewSlot);
        Grid.SetColumn(tabs, 0); Grid.SetColumn(splitter, 1); Grid.SetColumn(viewColumn, 2);
        top.Children.Add(tabs); top.Children.Add(splitter); top.Children.Add(viewColumn);
        // Earlier results' table runs across the whole window under the tabs and the view, while that tab is in front
        var body = new Grid();
        body.RowDefinitions.Add(new RowDefinition { Height = new GridLength(3, GridUnitType.Star), MinHeight = 300 });
        body.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        body.RowDefinitions.Add(new RowDefinition { Height = new GridLength(2, GridUnitType.Star), MinHeight = 120 });
        var tableSplit = new GridSplitter { Height = 5, HorizontalAlignment = HorizontalAlignment.Stretch, VerticalAlignment = VerticalAlignment.Center,
                                            Background = Theme.Brush(Theme.Line), ToolTip = "drag to give the table or the view more room" };
        var table = ResultsView.Table;
        Grid.SetRow(top, 0); Grid.SetRow(tableSplit, 1); Grid.SetRow(table, 2);
        body.Children.Add(top); body.Children.Add(tableSplit); body.Children.Add(table);
        GridLength tableHeight = body.RowDefinitions[2].Height;
        void PlaceTable()
        {
            bool show = Views.TryGetValue("Results", out var r) && Tabs.SelectedItem == r.Group && r.Inner is { Parent: TabControl inner } && inner.SelectedItem == r.Inner;
            if (show == (table.Visibility == Visibility.Visible)) return;
            if (!show) tableHeight = body.RowDefinitions[2].Height;           // kept for the next time the tab comes to the front
            table.Visibility = tableSplit.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
            body.RowDefinitions[2].Height = show ? tableHeight : new GridLength(0);
            body.RowDefinitions[2].MinHeight = show ? 120 : 0;
        }
        // the group tabs and the tabs inside them both raise SelectionChanged here
        tabs.AddHandler(System.Windows.Controls.Primitives.Selector.SelectionChangedEvent, new SelectionChangedEventHandler((_, e) => { if (e.OriginalSource is TabControl) PlaceTable(); }));
        table.Visibility = tableSplit.Visibility = Visibility.Collapsed;
        body.RowDefinitions[2].Height = new GridLength(0); body.RowDefinitions[2].MinHeight = 0;
        PlaceTable();
        root.Children.Add(openBar);
        var strip = Loading.Strip();                           // one bar for whatever is loading now
        DockPanel.SetDock(strip, Dock.Top);
        root.Children.Add(strip);
        root.Children.Add(Banner); root.Children.Add(body);
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

    internal static void SetBanner(string text)
    {
        _bannerSaid = text;
        Banner.Text = text.Length > 0 && char.IsLower(text[0]) ? char.ToUpperInvariant(text[0]) + text[1..] : text;
        WorkbenchHost.Say("banner: " + text);
    }
    static string _bannerSaid = "";

    /// <summary>Select a Findings row in its tab, as a click does (the script's repair steps).</summary>
    internal static void SelectFinding(RankedFinding f)
    {
        foreach (var it in FindingList.Items)
            if (it is ListBoxItem { Tag: RankedFinding g } li && g.Rank == f.Rank) { FindingList.SelectedItem = li; li.BringIntoView(); return; }
    }
    internal static string BannerText => _bannerSaid;

    /// <summary>Put a scene's view and bar into the window (replacing the last scene's).</summary>
    internal static void Mount(UIElement view, UIElement bar) { ViewSlot.Content = view; BarSlot.Content = bar; }
    internal static void Unmount() { ViewSlot.Content = EmptyView; BarSlot.Content = null; }

    /// <summary>What the 3D view shows before any scene: what will appear there and how to get it.</summary>
    static readonly UIElement EmptyView = new TextBlock
    {
        Text = "The 3D view shows a clash here." + Environment.NewLine + "Pick one under Conflicts › In the world, or a place under Explore › World.",
        TextAlignment = TextAlignment.Center, TextWrapping = TextWrapping.Wrap, MaxWidth = 620, FontSize = 14,
        HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, Foreground = Theme.Brush(Theme.Dim),
    };

    /// <summary>A list's empty state: one line, not selectable.</summary>
    static ListBoxItem EmptyItem(string text) => new() { Content = text, IsEnabled = false, FontStyle = FontStyles.Italic, Foreground = Theme.Brush(Theme.Dim), Padding = new Thickness(6, 8, 6, 8) };

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
            // beside the app, or in a checkout's pinned houseCARL (external/houseCARL), whose reporting skill owns the policy
            for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null && path is null; dir = dir.Parent)
                foreach (var cand in new[] { Path.Combine(dir.FullName, SceneKeyPolicy.RelativePath),
                                             Path.Combine(HarnessShared.CheckoutLayout.Housecarl(dir.FullName), SceneKeyPolicy.RelativePath) })
                    if (File.Exists(cand)) { path = cand; break; }
        if (string.IsNullOrWhiteSpace(path)) return (null, "the reporting skill's scene policy was not found beside the workbench, so rows are listed without scenes.");
        try { return (SceneKeyPolicy.Load(path), null); }
        catch (Exception ex) when (ex is InvalidOperationException or IOException or System.Text.Json.JsonException) { return (null, ex.Message); }
    });

    internal static void FillConflicts()
    {
        ConflictList.Items.Clear();
        // the switches sort a check's rows: with no check open they have nothing to sort
        AllRows.Visibility = DecidedRows.Visibility = FindingsOnlyRows.Visibility = Bundle is null ? Visibility.Collapsed : Visibility.Visible;
        // a clash list loaded with no check open: its clashes, as written
        if (Bundle is null && ClashList.Current is { } cl)
        {
            ConflictList.Items.Add(new ListBoxItem { Content = $"{cl.Title ?? Path.GetFileName(cl.File)}: {cl.Clashes.Count} clash(es)", FontWeight = FontWeights.Bold,
                                                     Foreground = Brushes.LightSkyBlue, ToolTip = cl.File, IsEnabled = false });
            foreach (var c in cl.Clashes) ConflictList.Items.Add(new ListBoxItem { Content = ClashList.Label(c), Tag = c, ToolTip = ClashList.Tip(c) });
            ConflictsEmpty();
            return;
        }
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
            ConflictsEmpty();
            return;
        }
        foreach (var e in ReviewList.For(live, Visible, Standings, ShowDecided)) ConflictList.Items.Add(RowItem(e));
        ConflictsEmpty();
    }

    static void ConflictsEmpty()
    {
        if (ConflictList.Items.Count > 0) return;
        ConflictList.Items.Add(EmptyItem(Bundle is null ? "Nothing checked yet. Check a mod on the Start tab, or load earlier results."
                                                         : "No clash to list with these switches. Turn one on above to see more."));
    }

    static ListBoxItem RowItem(ReviewEntry e)
    {
        var r = e.Row;
        string decided = e.Decision is { } d ? $"\n   {d.Verdict}: {d.Reason} — {e.Standing.Sentence}" : "";
        var item = new ListBoxItem { Content = RowLabel(r) + decided, Tag = r, ToolTip = RowText(r) };
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
        foreach (var c in Bundle?.Checks ?? Array.Empty<CheckStatus>()) FindingList.Items.Add(CheckItem(c));
        if (Bundle is null) FindingList.Items.Add(EmptyItem("Nothing checked yet. Check a mod on the Start tab, or load earlier results."));
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

    /// <summary>A ticked check's line on the Findings tab, with the probe's "?" help after it when the check is a probe.</summary>
    internal static ListBoxItem CheckItem(CheckStatus c)
    {
        var rest = c.State == "ran" ? $": ran, {CheckStatusWords.Rows(c)}" : $": {c.State}: {c.Why}";
        object content = c.CheckId + rest;
        if (ProbeHelp.Of(c.CheckId) is not null)
        {
            // the "?" sits right after the id, so a long line never scrolls it out of view
            var line = new TextBlock();
            line.Inlines.Add(new System.Windows.Documents.Run(c.CheckId));
            line.Inlines.Add(new System.Windows.Documents.InlineUIContainer(ProbeHelpPopup.Button(c.CheckId)) { BaselineAlignment = BaselineAlignment.Center });
            line.Inlines.Add(new System.Windows.Documents.Run(rest));
            content = line;
        }
        return new ListBoxItem { Content = content, Foreground = c.State == "ran" ? Brushes.DarkGray : Brushes.Salmon };
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
        // the bundle's load order is known: any of its cells can be asked for, whatever the window shows now
        if (BundleOrder is { } bo)
        {
            bool here = h is not null && !h.ReadOnly && SceneDirector.Order?.Key == bo.Key;
            var p = ClickThrough.Plan(refKey, cell, here ? h!.SceneCell : null, cell is null ? Array.Empty<string>() : new[] { cell },
                                      here ? h!.Session.Pending.Count : 0, k => here && h!.Session.IsDrawn(k));
            switch (p)
            {
                case ClickPlan.Land l: h!.Land(l.Ref); break;
                case ClickPlan.Refuse f: SetBanner(f.Sentence); LastLanding = "refused: " + f.Sentence; break;
                case ClickPlan.Switch s:
                    try { SceneDirector.Request(new SceneRequest(bo, s.Cell, s.Ref.ToString(), $"to show {s.Ref}", ReadOnly: false)); }
                    catch (InvalidOperationException ex) { SetBanner(ex.Message); LastLanding = "refused: " + ex.Message; }
                    break;
            }
            return;
        }
        if (h is null) return;
        if (SceneDirector.Order is not null)
        { SetBanner("this window has moved to another load order, and the bundle's own was not named in full; restart the workbench with the bundle to open its rows."); return; }
        var plan = ClickThrough.Plan(refKey, cell, h.SceneCell, WorklistCells, h.Session.Pending.Count, k => h.Session.IsDrawn(k));
        switch (plan)
        {
            case ClickPlan.Land l: h.Land(l.Ref); break;
            case ClickPlan.Refuse f: SetBanner(f.Sentence); LastLanding = "refused: " + f.Sentence; break;
            case ClickPlan.Switch s:
                PendingLand = s.Ref.ToString(); SwitchCell = s.Cell; SwitchClock = Stopwatch.StartNew();
                SetBanner($"loading cell {s.Cell} to show {s.Ref}: the scene is being rebuilt.");
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
        director = SceneDirector.State(),
        scene = h?.SceneState(),
        tree = Tree,
        lastLanding = LastLanding,
        loading = Loading.State(),
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
