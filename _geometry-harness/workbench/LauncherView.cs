using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;

namespace Workbench;

/// <summary>The Start tab: the launcher's controls. Each one calls the same <see cref="Launcher"/>
/// method the matching <c>workbench_act</c> verb calls; a refusal shows its one sentence in the status line.
/// Probes are tiles in groups by family, each with a pill switch, its own Run and Abort, and while it runs a progress bar
/// and an ETA read from the run's own run log (never a made-up bar).</summary>
internal static class LauncherView
{
    static readonly ComboBox Mode = new() { ItemsSource = new[] { "free", "housecarl" }, SelectedIndex = 0, Width = 110 };
    static readonly TextBox Instance = new() { Width = 300 }, Output = new() { Width = 300 }, Work = new() { Width = 300 };
    static readonly ComboBox Profile = new() { MinWidth = 160, IsEditable = true };
    static readonly StackPanel ModList = new(), ProbeGroups = new();
    static readonly TextBox ModSearch = new() { MinWidth = 220, Margin = new Thickness(0, 0, 8, 0), ToolTip = "filter the mods by name" };
    static readonly TextBlock ModCount = new() { VerticalAlignment = VerticalAlignment.Center, Foreground = Theme.Brush(Theme.Dim) };
    static readonly ScrollViewer ModScroll = new() { Content = ModList, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, MaxHeight = 320, Margin = new Thickness(4, 2, 4, 2) };
    static readonly Dictionary<string, CheckBox> ModBoxes = new(StringComparer.OrdinalIgnoreCase);
    static string? _modsShown;
    static readonly TextBlock Status = new() { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(4) };
    static readonly TextBlock Settings = new() { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(4), Foreground = Brushes.Salmon };
    static readonly TextBlock RunLine = new() { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(4, 0, 4, 4), Foreground = Theme.Brush(Theme.Dim) };
    static readonly TextBlock NotProbes = new() { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(4, 8, 4, 4), Foreground = Theme.Brush(Theme.Dim) };
    static readonly Button RunAll = new() { Content = "Run selected", Margin = new Thickness(4), FontWeight = FontWeights.SemiBold };
    static readonly Button AbortAll = new() { Content = "Abort run", Margin = new Thickness(4) };
    static readonly Button FullCheck = new()
    {
        Content = "Check Compatibility with Full Load Order", FontWeight = FontWeights.SemiBold, FontSize = 14, Padding = new Thickness(12, 6, 12, 6),
        Margin = new Thickness(4, 8, 4, 2), Background = Theme.Brush(Theme.Accent), Foreground = Brushes.White, Visibility = Visibility.Collapsed,
    };
    static readonly TextBlock FullCheckWhat = new() { TextWrapping = TextWrapping.Wrap, FontSize = 11, Margin = new Thickness(4, 0, 4, 6), Visibility = Visibility.Collapsed };
    static readonly Button RunEvery = new() { Content = "Run all", Margin = new Thickness(4), ToolTip = "run every probe that can run here, switched on or not" };
    static readonly Dictionary<string, Tile> Tiles = new(StringComparer.OrdinalIgnoreCase);
    static bool _filling;

    sealed record Tile(Ellipse Dot, TextBlock Line, ProgressBar Bar, TextBlock Eta, Button Run, Button Abort, ToggleButton Switch);

    static Launcher L => Shell.Launcher;

    /// <summary>Put a sentence in the status line (the restore at start says what it restored).</summary>
    internal static void Say(string s) => Status.Text = s;

    static void Try(Func<string> act) { try { Status.Text = act(); } catch (InvalidOperationException ex) { Status.Text = ex.Message; } Fill(); }

    // The tab is built once: its controls are static, and a second build would give them a second parent.
    static ScrollViewer? _built;

    internal static UIElement Build()
    {
        if (_built is not null) return _built;
        Button Browse(TextBox box, Action? after = null)
        {
            var b = new Button { Content = "Browse…", Margin = new Thickness(4, 0, 0, 0) };
            b.Click += (_, _) =>
            {
                var d = new Microsoft.Win32.OpenFolderDialog { InitialDirectory = Directory.Exists(box.Text) ? box.Text : null };
                if (d.ShowDialog() == true) { box.Text = d.FolderName; after?.Invoke(); }
            };
            return b;
        }
        Instance.LostFocus += (_, _) => FillProfiles();
        var open = new Button { Content = "Open profile", Margin = new Thickness(4, 0, 0, 0) };
        open.Click += async (_, _) =>
        {
            try { Status.Text = await L.OpenProfileAsync((string)Mode.SelectedItem, Instance.Text, Profile.Text.Length > 0 ? Profile.Text : null, null); }
            catch (InvalidOperationException ex) { Status.Text = ex.Message; }
            Fill();
        };
        var setOut = new Button { Content = "Set", Margin = new Thickness(4, 0, 0, 0) };
        setOut.Click += (_, _) => Try(() => L.SetOutput(Output.Text));
        var setWork = new Button { Content = "Set", Margin = new Thickness(4, 0, 0, 0) };
        setWork.Click += (_, _) => Try(() => L.SetWorkFolder(Work.Text));
        RunAll.Click += (_, _) => Try(() => L.Run());
        RunEvery.Click += (_, _) => Try(() => L.RunAll());
        FullCheck.Click += (_, _) => Try(() => L.RunAll());
        AbortAll.Click += (_, _) => Try(() => L.AbortRun());
        StackPanel Row(string label, params UIElement[] els)
        {
            var r = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(4, 2, 4, 2) };
            r.Children.Add(new TextBlock { Text = label, Width = 90, VerticalAlignment = VerticalAlignment.Center });
            foreach (var e in els) r.Children.Add(e);
            return r;
        }
        var panel = new StackPanel { Background = Theme.Brush(Theme.Panel) };
        panel.SetValue(TextElement.ForegroundProperty, Theme.Brush(Theme.Text));
        panel.Children.Add(Settings);
        panel.Children.Add(Row("Mode", Mode));
        panel.Children.Add(Row("Instance", Instance, Browse(Instance, FillProfiles)));
        panel.Children.Add(Row("Profile", Profile, open));
        panel.Children.Add(Row("Output", Output, Browse(Output), setOut));
        panel.Children.Add(Row("Work folder", Work, Browse(Work), setWork));
        panel.Children.Add(new TextBlock { Text = "Mods (priority order)", FontWeight = FontWeights.SemiBold, Margin = new Thickness(4, 8, 4, 0) });
        panel.Children.Add(new TextBlock { Text = "As in MO2: the mod at the bottom wins. Disabled mods you select are tested as if enabled; their plugins load last, as MO2 places a newly enabled plugin.",
                                           Foreground = Theme.Brush(Theme.Dim), FontSize = 11, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(4, 0, 4, 4) });
        var find = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(4, 0, 4, 2) };
        find.Children.Add(new TextBlock { Text = "Search", Width = 90, VerticalAlignment = VerticalAlignment.Center });
        find.Children.Add(ModSearch); find.Children.Add(ModCount);
        ModSearch.TextChanged += (_, _) => FilterMods();
        panel.Children.Add(find);
        panel.Children.Add(new Border { BorderBrush = Theme.Brush(Theme.Line), BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(4), Margin = new Thickness(4, 0, 4, 0), Child = ModScroll });
        var head = new DockPanel { Margin = new Thickness(4, 10, 4, 0) };
        var buttons = new StackPanel { Orientation = Orientation.Horizontal };
        buttons.Children.Add(RunEvery); buttons.Children.Add(RunAll); buttons.Children.Add(AbortAll);
        DockPanel.SetDock(buttons, Dock.Right);
        head.Children.Add(buttons);
        head.Children.Add(new TextBlock { Text = "Probes", FontWeight = FontWeights.SemiBold, FontSize = 14, VerticalAlignment = VerticalAlignment.Center });
        // one ticked mod: the run that answers "is this mod compatible with my load order", every probe that can run here
        panel.Children.Add(FullCheck);
        FullCheckWhat.Foreground = Theme.Brush(Theme.Dim);
        panel.Children.Add(FullCheckWhat);
        panel.Children.Add(head);
        panel.Children.Add(RunLine);
        panel.Children.Add(ProbeGroups);
        panel.Children.Add(NotProbes);
        panel.Children.Add(Status);
        BuildTiles();
        // the run read again every second: each probe's progress from its own run record, and the whole run's line
        var tick = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        tick.Tick += (_, _) => { if (Signature() != _seen) Fill(); else Refresh(); };
        tick.Start();
        var outer = new ScrollViewer { Content = panel, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Background = Theme.Brush(Theme.Panel) };
        // the mod list scrolls on its own, at most about 40% of the tab, so the probes stay in view below it
        outer.SizeChanged += (_, _) => ModScroll.MaxHeight = Math.Max(160, outer.ActualHeight * 0.4);
        return _built = outer;
    }

    /// <summary>One tile per probe, in a group box per family; checks that are not probes are said below in plain words.</summary>
    static void BuildTiles()
    {
        ProbeGroups.Children.Clear(); Tiles.Clear();
        IReadOnlyList<Launcher.RegistryCheck> reg;
        try { reg = Launcher.Registry(); }
        catch (InvalidOperationException ex) { ProbeGroups.Children.Add(new TextBlock { Text = ex.Message, TextWrapping = TextWrapping.Wrap, Foreground = Brushes.Salmon }); return; }
        foreach (var family in reg.Where(c => Launcher.ProbeKinds.Contains(c.Kind)).GroupBy(c => ProbeNames.Family(c.Id)))
        {
            var stack = new StackPanel();
            foreach (var c in family) stack.Children.Add(MakeTile(c));
            ProbeGroups.Children.Add(new GroupBox { Header = new TextBlock { Text = family.Key, FontWeight = FontWeights.SemiBold }, Content = stack });
        }
        var others = reg.Where(c => !Launcher.ProbeKinds.Contains(c.Kind)).ToList();
        NotProbes.Text = others.Count == 0 ? "" : "Not run from this tab: " +
            string.Join("; ", others.GroupBy(c => ProbeNames.NotAProbe(c.Kind)).Select(g => $"{string.Join(", ", g.Select(c => ProbeNames.Of(c.Id)))} ({(g.Key.StartsWith("is ") ? g.Key[3..] : g.Key)})")) + ".";
    }

    internal static Border MakeTile(Launcher.RegistryCheck c)
    {
        var id = c.Id;
        var dot = new Ellipse { Width = 10, Height = 10, Fill = Theme.Brush("#666666"), Margin = new Thickness(2, 6, 10, 0), VerticalAlignment = VerticalAlignment.Top };
        var name = new TextBlock { FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap };
        // the name, then its "?" help button on the same line (it wraps with the name on a narrow window)
        name.Inlines.Add(new Run(ProbeNames.Of(id)) { ToolTip = id });
        name.Inlines.Add(new InlineUIContainer(ProbeHelpPopup.Button(id)) { BaselineAlignment = BaselineAlignment.Center });
        var line = new TextBlock { Foreground = Theme.Brush(Theme.Dim), FontSize = 11, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 2, 0, 0) };
        var bar = new ProgressBar { Height = 6, Minimum = 0, Maximum = 100, Visibility = Visibility.Collapsed, Margin = new Thickness(0, 4, 8, 0) };
        var eta = new TextBlock { Foreground = Theme.Brush(Theme.Dim), FontSize = 11, Margin = new Thickness(0, 2, 0, 0) };
        var run = new Button { Content = "Run", Margin = new Thickness(4, 0, 0, 0), VerticalAlignment = VerticalAlignment.Top };
        var abort = new Button { Content = "Abort", Margin = new Thickness(4, 0, 0, 0), VerticalAlignment = VerticalAlignment.Top, Visibility = Visibility.Collapsed };
        var sw = new ToggleButton { Style = (Style)Theme.Dark["Pill"], Margin = new Thickness(8, 0, 0, 0), VerticalAlignment = VerticalAlignment.Top, ToolTip = "include in Run selected" };
        run.Click += (_, _) => Try(() => L.Run(new[] { id }));
        abort.Click += (_, _) => Try(() => L.AbortProbe(id));
        sw.Click += (_, _) =>
        {
            if (_filling) return;
            var want = L.Probes.Where(p => !L.AddedFor.ContainsKey(p)).ToList();
            if (sw.IsChecked == true) want.Add(id); else want.Remove(id);
            if (want.Count == 0) { L.Probes.Clear(); L.AddedFor.Clear(); Fill(); return; }
            Try(() => L.SelectProbes(want));
        };
        var text = new StackPanel();
        text.Children.Add(name); text.Children.Add(line); text.Children.Add(bar); text.Children.Add(eta);
        var grid = new Grid();
        foreach (var w in new[] { GridLength.Auto, new GridLength(1, GridUnitType.Star), GridLength.Auto, GridLength.Auto, GridLength.Auto })
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = w });
        void Put(UIElement e, int col) { Grid.SetColumn(e, col); grid.Children.Add(e); }
        Put(dot, 0); Put(text, 1); Put(run, 2); Put(abort, 3); Put(sw, 4);
        Tiles[id] = new Tile(dot, line, bar, eta, run, abort, sw);
        return new Border { Background = Theme.Brush(Theme.Tile), CornerRadius = new CornerRadius(6), Padding = new Thickness(10, 8, 10, 8), Margin = new Thickness(2, 3, 2, 3), Child = grid };
    }

    /// <summary>The profile picker: the profiles folder of the instance typed or browsed to.</summary>
    static void FillProfiles()
    {
        try { Profile.ItemsSource = Directory.Exists(Instance.Text) ? Launcher.ProfilesOf(Instance.Text) : Array.Empty<string>(); }
        catch (IOException) { Profile.ItemsSource = Array.Empty<string>(); }
    }

    static string? _seen;

    /// <summary>What the text fields and the mods list show; a change made through the state tools refills them.</summary>
    static string Signature() => string.Join("|", L.Instance, L.Profile, L.Output, L.WorkFolder, string.Join(",", L.Mods.Where(m => m.Selected).Select(m => m.Name)), L.Mods.Count);

    /// <summary>The mods as MO2 lists them: lowest priority at the top, the winning mod at the bottom, each separator above its mods.</summary>
    internal static IEnumerable<LauncherMod> InMo2Order(IEnumerable<LauncherMod> mods) => mods.OrderBy(m => m.Priority);

    internal static void Fill()
    {
        _seen = Signature();
        _filling = true;
        try
        {
            if (L.Instance is not null) { Instance.Text = L.Instance; FillProfiles(); Profile.Text = L.Profile ?? ""; }
            Output.Text = L.Output ?? Output.Text; Work.Text = L.WorkFolder ?? Work.Text;
            // the boxes are built once per opened profile (a large list is slow to rebuild); a tick only updates them
            var shown = $"{L.Instance}|{L.Profile}|{string.Join(",", L.Mods.Select(m => m.Name))}";
            if (shown != _modsShown)
            {
                _modsShown = shown;
                ModList.Children.Clear(); ModBoxes.Clear();
                // shown as MO2 shows it, lowest priority first and the winning mod last; each box selects by name, not position
                foreach (var m in InMo2Order(L.Mods))
                {
                    if (m.Separator) { ModList.Children.Add(new TextBlock { Text = "— " + m.Name[..^"_separator".Length] + " —", Tag = "separator", Margin = new Thickness(4, 4, 4, 0), Foreground = Theme.Brush(Theme.Dim) }); continue; }
                    var label = new TextBlock { Text = m.Name + (m.Unmanaged ? "  (unmanaged)" : m.Enabled ? "" : "  (disabled in MO2)") };
                    if (!m.Enabled && !m.Unmanaged) label.Foreground = Theme.Brush(Theme.Dim);
                    var cb = new CheckBox { Content = label, Tag = m.Name, IsEnabled = !m.Unmanaged, Margin = new Thickness(8, 1, 4, 1) };
                    var name = m.Name;
                    cb.Click += (_, _) => { if (_filling) return; Try(() => L.SelectMods(new[] { name }, cb.IsChecked == true ? "add" : "remove")); };
                    ModList.Children.Add(cb);
                    ModBoxes[m.Name] = cb;
                }
                FilterMods();
            }
            foreach (var m in L.Mods) if (ModBoxes.TryGetValue(m.Name, out var box)) box.IsChecked = m.Selected;
            int sel = L.Mods.Count(m => m.Selected), off = L.Mods.Count(m => m.Selected && !m.Enabled);
            ModCount.Text = $"{sel} selected" + (off > 0 ? $", {off} disabled in MO2" : "");
            Refresh();
        }
        finally { _filling = false; }
    }

    /// <summary>Show only the mods whose name holds the search text (any case); a tick is kept on a hidden mod, and the
    /// selected count beside the box still counts it. Separators show only while no filter is typed.</summary>
    internal static string Search(string? text)
    {
        ModSearch.Text = text ?? "";
        FilterMods();
        int shown = ModBoxes.Values.Count(b => b.Visibility == Visibility.Visible);
        return ModSearch.Text.Length == 0 ? "the mod list shows every mod" : $"{shown} mod(s) match '{ModSearch.Text}'";
    }

    static void FilterMods()
    {
        var q = ModSearch.Text.Trim();
        foreach (var e in ModList.Children.OfType<FrameworkElement>())
        {
            bool show = q.Length == 0 || (e.Tag is string n && n != "separator" && n.Contains(q, StringComparison.OrdinalIgnoreCase));
            e.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
        }
    }

    /// <summary>Each tile's switch, reason, dot, progress, ETA and buttons, from the launcher's state now.</summary>
    static void Refresh()
    {
        bool was = _filling; _filling = true;
        try
        {
            Settings.Text = L.SettingsNote ?? "";
            Settings.Visibility = L.SettingsNote is null ? Visibility.Collapsed : Visibility.Visible;
            bool running = L.RunTask is { IsCompleted: false };
            // the loading strip: the run's own line while it runs (each probe's bar is on its tile)
            if (running) Loading.Set("run", "Running the checks: " + (L.PipelineLine() ?? L.RunLine ?? "starting"));
            else Loading.Clear("run");
            RunAll.Visibility = running ? Visibility.Collapsed : Visibility.Visible;
            RunEvery.Visibility = RunAll.Visibility;
            var ticked = L.Mods.Where(m => m.Selected).ToList();
            bool one = ticked.Count == 1 && !running && L.Instance is not null;
            FullCheck.Visibility = FullCheckWhat.Visibility = one ? Visibility.Visible : Visibility.Collapsed;
            if (one) FullCheckWhat.Text = $"Tests {ticked[0].Name}{(ticked[0].Enabled ? "" : " (disabled in MO2, tested as if enabled)")} against everything enabled in this profile, with every probe that can run here.";
            AbortAll.Visibility = running ? Visibility.Visible : Visibility.Collapsed;
            RunLine.Text = L.RunState switch
            {
                "running" => (L.PipelineLine() ?? "running: " + (L.RunLine ?? "started")) + (L.PrepNote is { } pn ? Environment.NewLine + pn : "") + (L.ScopeNote is { } sn ? Environment.NewLine + sn : ""),
                "refused" => L.RunRefusal ?? "",
                "aborted" => L.RunRefusal ?? "aborted",
                "done" => "done: " + (L.RunTally is { } tl ? tl + "; " : "") + "the results are open in the Conflicts tab" + (L.RunBundle is { } b ? $" ({b})" : "") + (L.PrepNote is { } pd ? Environment.NewLine + pd : "") + (L.ScopeNote is { } sd ? Environment.NewLine + sd : ""),
                _ => L.Instance is null ? "open a profile to choose mods and probes" : $"{L.Probes.Count} probe(s) selected",
            };
            foreach (var c in Launcher.Registry().Where(c => Launcher.ProbeKinds.Contains(c.Kind)))
            {
                if (!Tiles.TryGetValue(c.Id, out var t)) continue;
                var why = L.WhyNotHere(c);
                t.Switch.IsChecked = L.Probes.Contains(c.Id);
                t.Switch.IsEnabled = why is null && !running;
                var p = L.RunChecks.Contains(c.Id) ? L.ProgressOf(c.Id) : new ProbeProgress("idle", null, "", null);
                bool active = running && p.State is "running" or "waiting";
                // a button that cannot be used now is not shown: Run while nothing runs on a probe that can run here, Abort while it runs
                t.Run.Visibility = !running && why is null && L.Instance is not null ? Visibility.Visible : Visibility.Collapsed;
                t.Abort.Visibility = active ? Visibility.Visible : Visibility.Collapsed;
                t.Line.Text = p.State == "idle"
                    ? (why ?? (L.AddedFor.TryGetValue(c.Id, out var f) ? $"added: {f}" : ProbeNames.Says(c.Id) is { Length: > 0 } says ? says : c.Title))
                    : p.Line;
                t.Bar.Visibility = p.Fraction is not null && p.State is "running" or "done" ? Visibility.Visible : Visibility.Collapsed;
                t.Bar.Value = (p.Fraction ?? 0) * 100;
                t.Eta.Text = p.Eta ?? "";
                t.Eta.Visibility = p.Eta is null ? Visibility.Collapsed : Visibility.Visible;
                t.Dot.Fill = Theme.Brush(p.State switch
                {
                    "running" => "#FFB300", "waiting" => "#B0B0B0", "done" => "#4CAF50", "failed" or "aborted" => "#E57373",
                    _ => why is not null ? "#4A4A4A" : L.Probes.Contains(c.Id) ? "#4CAF50" : "#777777",
                });
            }
        }
        finally { _filling = was; }
    }
}
