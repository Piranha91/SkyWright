using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using Mutagen.Bethesda.Plugins;

namespace Workbench;

/// <summary>The Results tab's rules that need no window: the groups' names, shapes and marks, a row's line, and which rows the choices list.</summary>
internal static class ResultRules
{
    internal const string AddressedMeans = "Addressed means the patch edits that record or object. It does not show the clash is gone.";
    internal const string NotScoredMeans = "Predicted object overlaps are not counted as addressed or not addressed.";

    /// <summary>A group as the tab names it: the tag's shape and words, and the scene mark.</summary>
    internal static readonly IReadOnlyDictionary<ResultGroup, (string Shape, string Words, MarkStyle Mark)> Groups =
        new Dictionary<ResultGroup, (string, string, MarkStyle)>
        {
            [ResultGroup.Addressed] = ("■", "addressed", MarkStyle.Solid),
            [ResultGroup.NotAddressed] = ("□", "not addressed", MarkStyle.Dashed),
            [ResultGroup.PatchChange] = ("◆", "patch change, nothing predicted", MarkStyle.Corners),
            [ResultGroup.NotScored] = ("○", "overlap, not scored", MarkStyle.Crossed),
        };

    internal static readonly string[] Setups = { "each row's own setup", "before the patch", "after the patch" };

    /// <summary>One row's line in the list: the group's tag in a shape and words, then the reader's own summary, then why there is no view.</summary>
    internal static string Line(ResultRow r, bool withPair = true)
    {
        var g = Groups[r.Group];
        string tag = r.Group == ResultGroup.PatchChange ? $"{g.Shape} patch change ({CategoryWords(r.Category)})" : $"{g.Shape} {g.Words}";
        string summary = r.Lane == "overlap" ? OverlapWords.Sentence(r.Summary) : r.Summary;
        string text = summary.Length > 260 ? summary[..260] + "…" : summary;
        return $"{tag}  ·  {(withPair ? r.Pair + "  ·  " : "")}{text}" + (r.Place.Cell is not null ? "" : PlacedOnOpen(r) ? "   (its cell is found in the load order when the row is opened)" : $"   (no view: {r.Place.WhyNot})");
    }

    /// <summary>A row the results give no cell for, but whose cell the setup's own load order gives when it is opened: a
    /// reference filed in a worldspace's persistent cell (the grid cell it stands in), a navmesh or a landscape record (its cell).</summary>
    internal static bool PlacedOnOpen(ResultRow r) =>
        r.Place.Cell is null && (r.Place.InPersistentCell && r.Place.RefKey is not null || r.RecordType is "NAVM" or "LAND");

    internal static string CategoryWords(int? category) => category switch
    {
        3 => "3: a new value",
        4 => "4: forwards a value",
        5 => "5: not measured",
        _ => "category " + category,
    };

    /// <summary>The rows the choices list now, in the reader's order: pair, then group, then position.</summary>
    internal static List<ResultRow> Select(IEnumerable<ResultRow> rows, string? pair, IReadOnlySet<ResultGroup> groups, IReadOnlySet<int> categories, string text) =>
        rows.Where(r => (pair is null || string.Equals(r.Pair, pair, StringComparison.OrdinalIgnoreCase))
                        && groups.Contains(r.Group)
                        && (r.Group != ResultGroup.PatchChange || (r.Category is { } c && categories.Contains(c)))
                        && (text.Length == 0 || Line(r).Contains(text, StringComparison.OrdinalIgnoreCase) || r.FormKey.Contains(text, StringComparison.OrdinalIgnoreCase)))
            .ToList();
}

/// <summary>The Results tab: a finished evaluation (<see cref="EvaluationResults"/>) by pair, filtered into what the
/// probes predicted and the patch addressed, what they predicted and it did not, what the patch changed that nothing
/// predicted, and the predicted overlaps the scorecard does not score. A row shows its values; a row with a place opens
/// its cell in the setup before or after the patch, lands on its object, and marks every listed row the scene draws.
/// The counts and categories are the scorecard's own, read and never recomputed.</summary>
internal static class ResultsView
{
    internal const string AddressedMeans = ResultRules.AddressedMeans, NotScoredMeans = ResultRules.NotScoredMeans;
    internal static IReadOnlyDictionary<ResultGroup, (string Shape, string Words, MarkStyle Mark)> Groups => ResultRules.Groups;
    internal static string[] Setups => ResultRules.Setups;
    internal static string CategoryWords(int? category) => ResultRules.CategoryWords(category);
    internal static string Line(ResultRow r) => ResultRules.Line(r, withPair: Pair is null);
    internal static List<ResultRow> Select(IEnumerable<ResultRow> rows, string? pair, IReadOnlySet<ResultGroup> groups, IReadOnlySet<int> categories, string text) =>
        ResultRules.Select(rows, pair, groups, categories, text);

    static readonly TextBlock Header = new() { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(6, 4, 6, 2) };
    static readonly TextBlock Means = new() { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(6, 0, 6, 2), Text = AddressedMeans, Foreground = Theme.Brush(Theme.Dim) };
    static readonly TextBlock NotesLine = new() { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(6, 0, 6, 2), Foreground = Theme.Brush(Theme.Dim), FontSize = 11 };
    static readonly ComboBox PairBox = new() { Margin = new Thickness(6, 2, 6, 2) };
    static readonly Dictionary<ResultGroup, CheckBox> GroupBoxes = new();
    static readonly Dictionary<int, CheckBox> CategoryBoxes = new();
    static readonly ComboBox SetupBox = new() { ItemsSource = ResultRules.Setups, SelectedIndex = 0, Width = 190, Margin = new Thickness(0, 0, 8, 0) };
    static readonly TextBox FilterBox = new() { MinWidth = 180, ToolTip = "list only rows whose text holds this (any case)" };
    static readonly TextBlock CountLine = new() { Margin = new Thickness(6, 2, 6, 2), Foreground = Theme.Brush(Theme.Dim) };
    static readonly TextBlock Legend = new() { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(6, 0, 6, 2), Foreground = Theme.Brush(Theme.Dim), FontSize = 11 };
    // the rows as a table: a column per thing a modder sorts or reads by (ResultTableRow)
    static readonly ListView Rows = new() { View = ResultTableRow.Grid() };
    // the open row laid out for reading (ResultDetail); its plain-text account is DetailText
    static readonly ScrollViewer Detail = new() { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Margin = new Thickness(6, 2, 6, 4) };

    static EvaluationResults? _results;
    static readonly Dictionary<string, IReadOnlyList<ResultRow>> Loaded = new(StringComparer.OrdinalIgnoreCase);
    static List<ResultRow> _listed = new();
    static bool _filling;
    static DispatcherTimer? _filterTimer;
    static int _generation, _openGeneration;

    internal static EvaluationResults? Results => _results;
    /// <summary>The pair chosen, or null for all pairs.</summary>
    internal static string? Pair { get; private set; }
    internal static HashSet<ResultGroup> Shown { get; } = new() { ResultGroup.Addressed, ResultGroup.NotAddressed, ResultGroup.PatchChange, ResultGroup.NotScored };
    internal static HashSet<int> Categories { get; } = new() { 3, 4, 5 };
    internal static string Setup { get; private set; } = ResultRules.Setups[0];
    internal static string TextFilter { get; private set; } = "";
    internal static IReadOnlyList<ResultRow> Listed => _listed;
    internal static ResultRow? Open { get; private set; }
    /// <summary>The setup the open row's scene was asked on ("before" or "after"), and what was said about it.</summary>
    internal static string? OpenSetup { get; private set; }
    internal static string? OpenSaid { get; private set; }
    internal static string? OpenCell { get; private set; }
    internal static int Marked { get; private set; }


    internal static UIElement Build()
    {
        VirtualizingPanel.SetIsVirtualizing(Rows, true);
        VirtualizingPanel.SetVirtualizationMode(Rows, VirtualizationMode.Recycling);
        ScrollViewer.SetHorizontalScrollBarVisibility(Rows, ScrollBarVisibility.Auto);
        Header.Text = "No earlier results are loaded. Use Load › Previous Results to open an evaluation's results folder.";
        var filters = new WrapPanel { Margin = new Thickness(6, 2, 6, 2) };
        foreach (var (group, g) in Groups)
        {
            var cb = new CheckBox { IsChecked = true, Margin = new Thickness(0, 0, 14, 2), Tag = group, Content = GroupLabel(group, g.Words) };
            cb.Click += (_, _) => { if (_filling) return; if (cb.IsChecked == true) Shown.Add(group); else Shown.Remove(group); Refill(); };
            GroupBoxes[group] = cb; filters.Children.Add(cb);
        }
        var cats = new WrapPanel { Margin = new Thickness(0, 0, 0, 2) };
        cats.Children.Add(new TextBlock { Text = "Patch changes by kind:  ", VerticalAlignment = VerticalAlignment.Center, Foreground = Theme.Brush(Theme.Dim),
                                          ToolTip = "the scorecard's categories 3, 4 and 5 of a patch change nothing predicted" });
        foreach (int c in new[] { 3, 4, 5 })
        {
            var cb = new CheckBox { IsChecked = true, Margin = new Thickness(0, 0, 12, 2), Content = CategoryWords(c) };
            int cat = c;
            cb.Click += (_, _) => { if (_filling) return; if (cb.IsChecked == true) Categories.Add(cat); else Categories.Remove(cat); Refill(); };
            CategoryBoxes[c] = cb; cats.Children.Add(cb);
        }
        var controls = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(6, 2, 6, 2) };
        controls.Children.Add(new TextBlock { Text = "Show ", VerticalAlignment = VerticalAlignment.Center });
        controls.Children.Add(SetupBox);
        controls.Children.Add(new TextBlock { Text = "Find ", VerticalAlignment = VerticalAlignment.Center });
        controls.Children.Add(FilterBox);
        SetupBox.ToolTip = "which setup a row's scene is built on: before the patch (the two mods together) or after it (with the patch added)";
        SetupBox.SelectionChanged += (_, _) => { if (_filling || SetupBox.SelectedItem is not string s) return; Setup = s; if (Open is { } r) Say(() => OpenRow(r)); };
        PairBox.SelectionChanged += (_, _) => { if (_filling || PairBox.SelectedItem is not ComboBoxItem { Tag: var tag }) return; Pair = tag as string; Refill(); };
        FilterBox.TextChanged += (_, _) =>
        {
            _filterTimer ??= new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(200) };
            _filterTimer.Tick -= FilterTick; _filterTimer.Tick += FilterTick;
            _filterTimer.Stop(); _filterTimer.Start();
        };
        // a click on a column's header sorts by it; a second click turns the order round
        Rows.AddHandler(GridViewColumnHeader.ClickEvent, new RoutedEventHandler((_, e) =>
        {
            if (e.OriginalSource is not GridViewColumnHeader { Column: { } col }) return;
            // the result square's column sorts by the result's words
            var path = col.DisplayMemberBinding is System.Windows.Data.Binding { Path.Path: { } p } ? p : nameof(ResultTableRow.Result);
            var view = System.Windows.Data.CollectionViewSource.GetDefaultView(Rows.ItemsSource);
            if (view is null) return;
            var dir = view.SortDescriptions.FirstOrDefault() is { } d && d.PropertyName == path && d.Direction == System.ComponentModel.ListSortDirection.Ascending
                ? System.ComponentModel.ListSortDirection.Descending : System.ComponentModel.ListSortDirection.Ascending;
            view.SortDescriptions.Clear();
            view.SortDescriptions.Add(new System.ComponentModel.SortDescription(path, dir));
        }));
        Rows.SelectionChanged += (_, _) => { if (_filling || Rows.SelectedItem is not ResultTableRow item) return; Say(() => OpenRow(item.Row)); };
        Legend.Text = "In the scene: " + ResultMarks.Legend(Groups.Select(g => (g.Value.Mark, g.Value.Words)))
                    + "; cyan: the row's object, magenta: its other object. " + NotScoredMeans;
        // the finer filters, the notes and the marks' legend fold away, so the rows keep the height
        var about = new StackPanel { Margin = new Thickness(6, 2, 6, 2) };
        about.Children.Add(cats); about.Children.Add(NotesLine); about.Children.Add(Legend);
        var aboutBox = new Expander { Header = "More filters, notes and the scene's marks", IsExpanded = false, Content = about, Margin = new Thickness(6, 0, 6, 2) };
        var panel = new DockPanel();
        // the pair list with its label, so it reads as a choice and not as a summary line
        var pairRow = new DockPanel { Margin = new Thickness(6, 2, 0, 2) };
        var pairLabel = new TextBlock { Text = "Pair", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 6, 0), ToolTip = "the mods an evaluation checked against each other; pick one, or all pairs" };
        DockPanel.SetDock(pairLabel, Dock.Left);
        PairBox.Margin = new Thickness(0, 0, 6, 0);
        pairRow.Children.Add(pairLabel); pairRow.Children.Add(PairBox);
        // the explanations are tooltips, not lines of grey text: what "addressed" means on the filters, the count on the header
        filters.ToolTip = AddressedMeans + " " + NotScoredMeans;
        foreach (var top in new UIElement[] { Header, pairRow, filters, controls, aboutBox }) { DockPanel.SetDock(top, Dock.Top); panel.Children.Add(top); }
        // the open row's values fill the tab; the rows themselves are the table the window shows across its full width
        // under the view (Table), while this tab is in front
        // under them, the open row's values and the load order its scene is built on share the pane, the load order the larger
        // the values fold away above it, so the load order has the pane's height; opened, they take what they need, up to a third
        Detail.MaxHeight = 320;
        var values = new Expander { Header = "The open row's values", IsExpanded = false, Content = Detail, Margin = new Thickness(6, 2, 6, 2),
                                    ToolTip = "what the open row's object is, what the result means, and the values side by side" };
        DockPanel.SetDock(values, Dock.Top);
        panel.Children.Add(values);
        panel.Children.Add(LoadOrderPanel.Build());
        return panel;
    }

    /// <summary>The rows as a table, for the window to place under the view.</summary>
    internal static ListView Table => Rows;

    /// <summary>A result filter's label: the table's colored square, then the words.</summary>
    static StackPanel GroupLabel(ResultGroup g, string words)
    {
        var sp = new StackPanel { Orientation = Orientation.Horizontal };
        var sq = ResultTableRow.Square(g); sq.Margin = new Thickness(0, 0, 6, 0);
        sp.Children.Add(sq); sp.Children.Add(new TextBlock { Text = words });
        return sp;
    }

    static void FilterTick(object? s, EventArgs e) { _filterTimer!.Stop(); TextFilter = FilterBox.Text.Trim(); Refill(); }

    static void Say(Func<string> act)
    {
        try { Shell.SetBanner(act()); }
        catch (InvalidOperationException ex) { Shell.SetBanner(ex.Message); }
    }

    /// <summary>Show an opened evaluation: its pairs and counts at once, its rows as each pair's files are read.</summary>
    internal static string Show(EvaluationResults results)
    {
        int gen = ++_generation;
        _results = results; Loaded.Clear(); _listed = new(); Open = null; OpenSetup = null; OpenSaid = null; OpenCell = null; Marked = 0;
        Pair = null; Shell.PartnerFor = null; Shell.SceneCaption = null;
        Rows.ItemsSource = null; Detail.Content = null;
        FillHeader(); FillPairs();
        if (Shell.Tabs is not null) Shell.ShowTab("Results");
        var ui = Dispatcher.CurrentDispatcher;
        // each pair's rows are read off the window's thread (the overlap lists are tens of megabytes); the list fills as they arrive
        int read = 0, pairs = results.Pairs.Count;
        if (pairs > 0) Loading.Set("results", $"Reading the evaluation's rows (0 of {pairs} pairs)", 0); else Loading.Clear("results");
        Task.Run(() =>
        {
            foreach (var p in results.Pairs)
            {
                IReadOnlyList<ResultRow> rows; string? why = null;
                try { rows = p.LoadRows(); }
                catch (Exception ex) when (ex is InvalidOperationException or IOException or System.Text.Json.JsonException) { rows = Array.Empty<ResultRow>(); why = ex.Message; }
                ui.BeginInvoke(() =>
                {
                    if (gen != _generation) return;
                    Loaded[p.Id] = rows;
                    if (++read >= pairs) Loading.Clear("results"); else Loading.Set("results", $"Reading the evaluation's rows ({read} of {pairs} pairs)", (double)read / pairs);
                    if (why is not null) Shell.SetBanner($"the rows of {p.Id} could not be read: {why}");
                    FillHeader(); FillPairs(); Refill();
                });
            }
        });
        var t = results.Totals;
        return $"opened the evaluation at '{results.Folder}': {results.Pairs.Count} pairs, {t.Addressed:N0} predicted clashes the patches addressed, {t.NotAddressed:N0} they did not, "
             + $"{t.PatchChange:N0} patch changes nothing predicted; {results.Checked.Count} file(s) match the receipt.";
    }

    static string CountsText(ResultCounts c) =>
        $"{c.Addressed:N0} addressed · {c.NotAddressed:N0} not addressed · {c.PatchChange:N0} patch changes nothing predicted ({c.Category3:N0} / {c.Category4:N0} / {c.Category5:N0}) · "
        + (c.NotScored is { } n ? $"{n:N0} overlap not scored" : "overlap not scored: reading");

    static void FillHeader()
    {
        if (_results is not { } r) return;
        // the folder's name and pair count shown; where it is and what was checked, in the tooltip
        Header.Text = $"{Path.GetFileName(r.Folder.TrimEnd('\\', '/'))}: {r.Pairs.Count} pairs";
        Header.FontWeight = FontWeights.SemiBold;
        Header.ToolTip = $"{r.Folder}{Environment.NewLine}{r.Checked.Count} file(s) match the evaluation's receipt.{Environment.NewLine}All pairs: {CountsText(r.Totals)}"
                       + (CountLine.Text is { Length: > 0 } listed ? Environment.NewLine + listed + "." : "");
        var notes = r.Notes.Concat(Pair is null ? Enumerable.Empty<string>() : r.Pairs.Where(p => p.Id == Pair).SelectMany(p => p.Notes)).Distinct().ToList();
        NotesLine.Text = string.Join(" ", notes.Select(n => n.TrimEnd('.') + "."));
        NotesLine.Visibility = notes.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    static void FillPairs()
    {
        if (_results is not { } r) return;
        bool was = _filling; _filling = true;
        try
        {
            PairBox.Items.Clear();
            // short enough to fit the box; the counts are in each item's tooltip and beside the filters below
            PairBox.Items.Add(new ComboBoxItem { Content = $"All {r.Pairs.Count} pairs", Tag = null, ToolTip = CountsText(r.Totals) });
            foreach (var p in r.Pairs)
                // the pair by its mods, as a modder knows it; its id and patch in the tooltip
                PairBox.Items.Add(new ComboBoxItem { Content = new TextBlock { Text = $"{string.Join(", ", p.Subject)} against {string.Join(", ", p.Partner)}", TextWrapping = TextWrapping.Wrap }, Tag = p.Id,
                                                     ToolTip = $"{p.Id}; patch {p.PatchPlugin}; {CountsText(p.Counts)}" });
            PairBox.SelectedIndex = Pair is null ? 0 : 1 + r.Pairs.ToList().FindIndex(p => p.Id == Pair);
        }
        finally { _filling = was; }
    }

    static void Refill()
    {
        if (_results is not { } r) return;
        bool was = _filling; _filling = true;
        try
        {
            var all = r.Pairs.Where(p => Loaded.ContainsKey(p.Id)).SelectMany(p => Loaded[p.Id]);
            _listed = Select(all, Pair, Shown, Categories, TextFilter);
            var pairs = _results?.Pairs.ToDictionary(p => p.Id, StringComparer.OrdinalIgnoreCase);
            Rows.ItemsSource = _listed.Select(x => new ResultTableRow(x, Line(x), pairs?.GetValueOrDefault(x.Pair))).ToList();
            var counts = Pair is null ? r.Totals : r.Pairs.First(p => p.Id == Pair).Counts;
            foreach (var (g, cb) in GroupBoxes)
                {
                int? n = g switch { ResultGroup.Addressed => counts.Addressed, ResultGroup.NotAddressed => counts.NotAddressed, ResultGroup.PatchChange => counts.PatchChange, _ => counts.NotScored };
                cb.Content = GroupLabel(g, $"{Groups[g].Words}  ({n?.ToString("N0") ?? "reading"})");
            }
            foreach (var (c, cb) in CategoryBoxes)
                cb.Content = $"{CategoryWords(c)}  ({(c == 3 ? counts.Category3 : c == 4 ? counts.Category4 : counts.Category5):N0})";
            int reading = r.Pairs.Count(p => !Loaded.ContainsKey(p.Id));
            int noView = _listed.Count(x => x.Place.Cell is null && !ResultRules.PlacedOnOpen(x));
            int onOpen = _listed.Count(ResultRules.PlacedOnOpen);
            CountLine.Text = $"{_listed.Count:N0} row(s) listed" + (noView > 0 ? $"; {noView:N0} have no view and say why" : "")
                           + (onOpen > 0 ? $"; {onOpen:N0} are placed through the load order when opened" : "")
                           + (reading > 0 ? $"; {reading} pair(s) still being read" : "");
            FillHeader();
            ApplyMarks();
        }
        finally { _filling = was; }
    }

    // ---- choices, as the controls and the state tools make them ---------------------------------------------------------
    internal static string Choose(string? pair, string[]? groups, int[]? categories, string? setup, string? text)
    {
        if (_results is not { } r) throw new InvalidOperationException("no evaluation is open; open a finished evaluation's results folder first.");
        if (pair is not null)
        {
            if (pair.Equals("all", StringComparison.OrdinalIgnoreCase)) Pair = null;
            else Pair = r.Pairs.FirstOrDefault(p => p.Id.Equals(pair, StringComparison.OrdinalIgnoreCase))?.Id
                        ?? throw new InvalidOperationException($"'{pair}' is not a pair of this evaluation; name one of {string.Join(", ", r.Pairs.Select(p => p.Id))}, or all.");
        }
        if (groups is not null)
        {
            var want = new HashSet<ResultGroup>();
            foreach (var g in groups)
                want.Add(g.Trim().ToLowerInvariant().Replace("-", "_").Replace(" ", "_") switch
                {
                    "addressed" => ResultGroup.Addressed,
                    "not_addressed" => ResultGroup.NotAddressed,
                    "patch_change" => ResultGroup.PatchChange,
                    "not_scored" => ResultGroup.NotScored,
                    _ => throw new InvalidOperationException($"'{g}' is not a group; use addressed, not_addressed, patch_change or not_scored."),
                });
            Shown.Clear(); foreach (var g in want) Shown.Add(g);
        }
        if (categories is not null)
        {
            if (categories.FirstOrDefault(c => c is not (3 or 4 or 5)) is var bad && bad != 0) throw new InvalidOperationException($"{bad} is not a category of a patch change nothing predicted; use 3, 4 or 5.");
            Categories.Clear(); foreach (var c in categories) Categories.Add(c);
        }
        if (setup is not null)
            Setup = Setups.FirstOrDefault(s => s.StartsWith(setup.Trim(), StringComparison.OrdinalIgnoreCase) || s.Contains(setup.Trim(), StringComparison.OrdinalIgnoreCase))
                    ?? throw new InvalidOperationException($"'{setup}' is not a setup choice; use own, before or after.");
        if (text is not null) TextFilter = text.Trim();
        bool was = _filling; _filling = true;
        try
        {
            foreach (var (g, cb) in GroupBoxes) cb.IsChecked = Shown.Contains(g);
            foreach (var (c, cb) in CategoryBoxes) cb.IsChecked = Categories.Contains(c);
            SetupBox.SelectedItem = Setup; FilterBox.Text = TextFilter;
        }
        finally { _filling = was; }
        FillPairs(); Refill();
        return $"{_listed.Count:N0} row(s) listed for {(Pair ?? "all pairs")}: {string.Join(", ", Shown.OrderBy(g => g).Select(g => Groups[g].Words))}; scenes show {Setup}.";
    }

    /// <summary>A row by its key (<c>pair|f3</c>, <c>pair|e12</c>, <c>pair|o7</c>), among the rows read so far.</summary>
    internal static ResultRow RowOf(string key) =>
        Loaded.Values.SelectMany(v => v).FirstOrDefault(r => string.Equals(r.Key, key, StringComparison.OrdinalIgnoreCase))
        ?? throw new InvalidOperationException($"'{key}' is not a row of the open evaluation; read workbench_state project results for the keys listed.");

    // ---- one row ------------------------------------------------------------------------------------------------------
    static string DetailText(ResultRow r)
    {
        var g = Groups[r.Group];
        var lines = new List<string>
        {
            r.Lane == "overlap" ? OverlapWords.Sentence(r.Summary) : r.Summary,
            "",
            $"{g.Shape} {(r.Group == ResultGroup.PatchChange ? $"patch change nothing predicted, the scorecard's category {CategoryWords(r.Category)} ({r.Rule})" : g.Words)}"
                + (r.Group is ResultGroup.Addressed or ResultGroup.NotAddressed ? $"  —  {AddressedMeans}" : r.Group == ResultGroup.NotScored ? $"  —  {NotScoredMeans}" : ""),
            $"pair {r.Pair}; {RecordWords.Named(r.RecordType, r.EditorId, r.BaseEditorId, r.FormKey)}"
                + (r.EditorId is not null && r.BaseEditorId is { } b ? $"; base object {b}" : "") + (r.BaseType is { } bt ? $" ({bt.ToLowerInvariant()})" : "")
                + (r.Field is { } f ? $"; {RecordWords.Field(f)}" : "") + (r.Attribution is { } a ? "; " + OverlapWords.Attribution(a, null).TrimEnd('.') : ""),
        };
        if (r.Values.Count > 0) { lines.Add(""); foreach (var v in OverlapWords.Values(r)) lines.Add($"{v.Label}{(v.Plugin is { } p ? $" ({p})" : "")}: {v.Text}"); }
        if (r.JoinedEdits.Count > 0) { lines.Add(""); lines.Add("the patch's edits here:"); foreach (var j in r.JoinedEdits) lines.Add("  " + j); }
        if (r.Note is { } n && r.Lane != "overlap") { lines.Add(""); lines.Add("note: " + n); }
        lines.Add("");
        lines.Add(r.Place.Cell is { } c ? $"place: cell {c}" + (r.Place.RefKey is { } k ? $", object {k}" : "") + (r.Place.OtherKey is { } o ? $", other object {o}" : "")
                  : ResultRules.PlacedOnOpen(r) ? $"place: {r.Place.WhyNot}; its cell is found in the setup's own load order when the row is opened"
                  : $"no view: {r.Place.WhyNot}");
        return string.Join(Environment.NewLine, lines);
    }

    /// <summary>The setup a row's scene is built on now: the choice above the list, or the row's own.</summary>
    internal static string SetupOf(ResultRow r) => Setup == Setups[1] ? "before" : Setup == Setups[2] ? "after" : r.DefaultSetup;

    static LoadOrderSpec OrderOf(ResultPair p, string setup)
    {
        var s = (setup == "after" ? p.After : p.Before)
                ?? throw new InvalidOperationException($"the setup {(setup == "after" ? "after" : "before")} the patch is not available for {p.Id}: {string.Join(" ", p.Notes)}");
        return new LoadOrderSpec(s.Instance, s.Profile, s.Game, s.OrderFile, $"{p.Id} {(setup == "after" ? "AFTER the patch (with " + p.PatchPlugin + ")" : "BEFORE the patch (the two mods together)")}");
    }

    /// <summary>A click on a row: show its values, and when it has a place, ask for its cell's scene on its setup and land
    /// on its object. Where the object stands in that setup is read from that setup's own load order (its index), which is
    /// read once per setup; the results' own cell is the fallback.</summary>
    internal static string OpenRow(ResultRow r)
    {
        if (_results is not { } res) throw new InvalidOperationException("no evaluation is open.");
        Open = r; Detail.Content = ResultDetail.Build(r, () => Clipboard.SetText(DetailText(r))); OpenCell = null; OpenSaid = null;
        var item = (Rows.ItemsSource as List<ResultTableRow>)?.FirstOrDefault(i => ReferenceEquals(i.Row, r));
        if (item is not null && !ReferenceEquals(Rows.SelectedItem, item)) { bool was = _filling; _filling = true; try { Rows.SelectedItem = item; Rows.ScrollIntoView(item); } finally { _filling = was; } }
        var pair = res.Pairs.First(p => p.Id == r.Pair);
        string setup = SetupOf(r); OpenSetup = setup;
        bool placedRef = r.Place.RefKey is not null && r.RecordType is not ("NAVM" or "LAND" or "CELL");
        if (r.Place.Cell is null && !placedRef && r.RecordType is not ("NAVM" or "LAND"))
            return OpenSaid = $"{RecordWords.Named(r.RecordType, r.EditorId, r.BaseEditorId, r.FormKey)} has no view: {r.Place.WhyNot}.";
        // the navmesh is drawn for a navmesh row and left out otherwise, where its wire only hides the objects
        var order = OrderOf(pair, setup) with { Navmesh = r.Lane == "navmesh" || r.RecordType == "NAVM" };
        int gen = ++_openGeneration;
        var ui = Dispatcher.CurrentDispatcher;
        var placing = $"Reading the load order of {order.Label} to place {RecordWords.Named(r.RecordType, r.EditorId, r.BaseEditorId, r.FormKey)}";
        var index = LoadOrderIndexes.For(order, new Progress<string>(s => { if (gen == _openGeneration) Loading.Set("placing", $"{placing}: {s}"); }));
        bool waited = !index.IsCompleted;
        if (waited) Loading.Set("placing", placing); else Loading.Clear("placing");
        index.ContinueWith(t => ui.BeginInvoke(() =>
        {
            if (gen != _openGeneration) return;
            Loading.Clear("placing");
            Say(() =>
            {
                if (t.IsFaulted) throw new InvalidOperationException($"the load order of {order.Label} could not be read, so the row's place is not known: {t.Exception!.GetBaseException().Message}");
                var x = t.Result;
                string? cell = null, land = null, extra = null;
                if (placedRef)
                {
                    land = r.Place.RefKey;
                    cell = x.OpenableCellOf(land!) ?? (r.Place.OtherKey is { } ok ? x.OpenableCellOf(ok) : null);
                    if (cell is null && x.Locate(land!) is null)
                    {
                        extra = setup == "before" && r.Lane == "edit" && r.Kind == "ADD"
                            ? $"the patch adds {land}, so the scene before the patch does not have it"
                            : $"{land} is not a record of this setup's load order";
                        cell = r.Place.Cell;
                    }
                    cell ??= r.Place.Cell;
                }
                else if (r.RecordType is "NAVM" or "LAND") cell = r.Place.Cell ?? x.Locate(r.FormKey)?.Cell;
                else cell = r.Place.Cell;
                if (cell is null)
                    throw new InvalidOperationException(OpenSaid = $"{RecordWords.Named(r.RecordType, r.EditorId, r.BaseEditorId, r.FormKey)} has no view: {(placedRef ? "its position falls in no cell of its worldspace in this setup" : "this setup's load order files it under no cell")}.");
                OpenCell = cell;
                Shell.PartnerFor = sel => string.Equals(sel, r.Place.RefKey, StringComparison.OrdinalIgnoreCase) ? r.Place.OtherKey
                                        : string.Equals(sel, r.Place.OtherKey, StringComparison.OrdinalIgnoreCase) ? r.Place.RefKey : null;
                Shell.SceneCaption = setup == "after" ? $"AFTER the patch ({pair.PatchPlugin})" : "BEFORE the patch";
                var said = SceneDirector.Request(new SceneRequest(order, cell, land, $"{Groups[r.Group].Words}: {RecordWords.Named(r.RecordType, r.EditorId, r.BaseEditorId, r.FormKey)}") { Shown = ApplyMarks });
                return OpenSaid = said + (extra is null ? "" : $" Note: {extra}.");
            });
        }), TaskScheduler.Default);
        return waited ? $"reading the load order of {order.Label} to place {RecordWords.Named(r.RecordType, r.EditorId, r.BaseEditorId, r.FormKey)} (once per setup, a few seconds)" : $"opening {RecordWords.Named(r.RecordType, r.EditorId, r.BaseEditorId, r.FormKey)} on {order.Label}";
    }

    /// <summary>Mark, in the scene shown, every listed row of the open row's pair that the scene draws, each in its group's shape.</summary>
    static void ApplyMarks()
    {
        if (Shell.Current is not { } h) { Marked = 0; return; }
        if (Open is null || !Loaded.TryGetValue(Open.Pair, out var rows) || SceneDirector.Showing is null) { Marked = h.MarkResults(Array.Empty<(FormKey, MarkStyle)>()); return; }
        var marks = new Dictionary<FormKey, MarkStyle>();
        // a later group does not replace an earlier one's mark: addressed, then not addressed, then patch change, then not scored
        foreach (var r in Select(rows, Open.Pair, Shown, Categories, "").OrderBy(r => r.Group))
            foreach (var k in new[] { r.Place.RefKey, r.Group == ResultGroup.NotScored || r.Lane == "contact" ? r.Place.OtherKey : null })
                if (k is not null && FormKey.TryFactory(k, out var fk)) marks.TryAdd(fk, Groups[r.Group].Mark);
        Marked = h.MarkResults(marks.Select(m => (m.Key, m.Value)));
    }

    // ---- what the state tools read ------------------------------------------------------------------------------------
    internal static object State(int limit, int offset)
    {
        if (_results is not { } r) return new { open = false };
        object Counts(ResultCounts c) => new
        {
            addressed = c.Addressed, notAddressed = c.NotAddressed, patchChange = c.PatchChange, category3 = c.Category3, category4 = c.Category4, category5 = c.Category5, notScored = c.NotScored,
        };
        return new
        {
            open = true, folder = r.Folder, receiptFilesMatched = r.Checked.Count, notes = r.Notes, means = AddressedMeans, notScoredMeans = NotScoredMeans,
            totals = Counts(r.Totals),
            pairs = r.Pairs.Select(p => new
            {
                id = p.Id, patch = p.PatchPlugin, subject = p.Subject, partner = p.Partner, counts = Counts(p.Counts), rowsRead = Loaded.ContainsKey(p.Id),
                sealedRun = p.SealedRun, patchedRun = p.PatchedRun, before = p.Before?.OrderFile, after = p.After?.OrderFile, profile = p.Before?.Profile, notes = p.Notes,
            }).ToList(),
            pair = Pair ?? "all", groups = Shown.OrderBy(g => g).Select(g => g.ToString()).ToList(), categories = Categories.OrderBy(c => c).ToList(), setup = Setup, text = TextFilter,
            legend = Legend.Text,
            listed = _listed.Count, listedWithNoView = _listed.Count(x => x.Place.Cell is null && !ResultRules.PlacedOnOpen(x)), listedPlacedOnOpen = _listed.Count(ResultRules.PlacedOnOpen),
            listedByGroup = _listed.GroupBy(x => x.Group).ToDictionary(g => g.Key.ToString(), g => g.Count()),
            rows = _listed.Skip(offset).Take(limit).Select(RowJson).ToList(),
            openRow = Open is null ? null : new
            {
                row = RowJson(Open), setup = OpenSetup, cell = OpenCell, said = OpenSaid, detail = DetailText(Open), marked = Marked,
                sceneCaption = Shell.SceneCaption,
            },
        };
    }

    static object RowJson(ResultRow x) => new
    {
        key = x.Key, group = x.Group.ToString(), pair = x.Pair, lane = x.Lane, kind = x.Kind, recordType = x.RecordType, formKey = x.FormKey, field = x.Field,
        editorId = x.EditorId, baseEditorId = x.BaseEditorId, category = x.Category, rule = x.Rule, attribution = x.Attribution, line = Line(x),
        values = x.Values.Select(v => new { label = v.Label, plugin = v.Plugin, text = v.Text.Length > 400 ? v.Text[..400] + "…" : v.Text }).ToList(),
        place = new { cell = x.Place.Cell, @ref = x.Place.RefKey, other = x.Place.OtherKey, inPersistentCell = x.Place.InPersistentCell, whyNot = x.Place.WhyNot },
        ownSetup = x.DefaultSetup, note = x.Note,
    };
}

/// <summary>A finished evaluation's results folder (it holds <c>SCORING-RECEIPT.txt</c> and <c>scoring/result/</c>), or
/// that receipt: opens into the Results tab once every file the receipt pins has been checked.</summary>
internal sealed class ResultsInputReader : IInputReader
{
    public string Kind => "evaluation results";
    public string Accepts => "an evaluation results folder";

    public InputMatch? Recognise(string path) =>
        EvaluationResults.LooksLike(path) ? new InputMatch(this, path, "a finished evaluation's results") : null;

    public string Open(InputMatch match)
    {
        var work = Path.GetFullPath(Program.RequestWork());
        var results = EvaluationResults.Open(match.Path);
        // the window writes its own files (worklists, captures) under the Work Folder: never inside the evidence it reads
        foreach (var root in new[] { results.Folder }.Concat(results.Pairs.SelectMany(p => new[] { p.SealedRun, p.PatchedRun })).OfType<string>())
            if (work.StartsWith(Path.GetFullPath(root).TrimEnd('\\', '/') + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
                || string.Equals(work.TrimEnd('\\', '/'), Path.GetFullPath(root).TrimEnd('\\', '/'), StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException($"the Work Folder '{work}' is inside the evaluation's own folder '{root}', and opening results must change nothing there; choose a Work Folder elsewhere (or unset WB_OUT).");
        return ResultsView.Show(results);
    }
}
