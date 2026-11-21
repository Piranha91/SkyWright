using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using HousecarlCore;

namespace Workbench;

/// <summary>One row of the World tab's tree, as the state tools list it.</summary>
internal sealed record WorldNode(string Kind, string? Key, string Label);

/// <summary>The World tab: a load order's worldspaces and interior cells in xEdit's shape (worldspace or "Interior
/// cells", block, sub-block, cell, then Persistent and Temporary with one row per record), read from
/// <see cref="WorldIndex"/>. Each level is filled when first opened; a cell's records are read on a background task.
/// Picking a cell asks for its scene, picking an object asks for its cell and lands on it. Above the tree a map of one
/// worldspace's terrain (<see cref="WorldMap"/>) picks a cell by where it is; the tree and the map follow each other.
/// Nothing here edits.</summary>
internal static class WorldView
{
    static readonly TextBlock Header = new() { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(6, 4, 6, 2) };
    static readonly TextBlock Notes = new() { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(6, 0, 6, 2), Foreground = Theme.Brush(Theme.Dim), FontSize = 11 };
    static readonly TextBox SearchBox = new() { MinWidth = 240, Margin = new Thickness(0, 0, 8, 0), ToolTip = "part of a cell's EditorID or name, a FormKey of any record (such as 0165A8:Skyrim.esm), or a grid such as 5,-3" };
    static readonly TextBlock SearchCount = new() { VerticalAlignment = VerticalAlignment.Center, Foreground = Theme.Brush(Theme.Dim) };
    static readonly ListBox Hits = new() { MaxHeight = 200, Margin = new Thickness(6, 2, 6, 2), Visibility = Visibility.Collapsed };
    static readonly TreeView Tree = new();
    static readonly Button BrowseProfile = new() { Content = "Show the loaded profile's world", Margin = new Thickness(6, 2, 6, 2), Padding = new Thickness(8, 2, 8, 2), HorizontalAlignment = HorizontalAlignment.Left };

    static readonly WorldMap Map = new();
    static readonly ComboBox MapWorld = new() { MinWidth = 220, Margin = new Thickness(0, 0, 8, 0), ToolTip = "the worldspace the map shows" };
    static readonly TextBlock MapSays = new() { VerticalAlignment = VerticalAlignment.Center, Foreground = Theme.Brush(Theme.Dim), TextTrimming = TextTrimming.CharacterEllipsis };
    static readonly CheckBox MapTextured = new() { Content = "Textured", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 0),
        ToolTip = "zoomed in, draw the cells as the game draws them from straight above (the renderer's pictures, kept on disk per load order); off shows the shaded shapes" };
    static readonly TextBlock TileSays = new() { VerticalAlignment = VerticalAlignment.Center, Foreground = Theme.Brush(Theme.Dim), TextTrimming = TextTrimming.CharacterEllipsis, Margin = new Thickness(0, 1, 0, 0) };
    static string _mapStatus = "";
    static string? _mapWorld;                             // the worldspace the map shows or is reading
    static bool _mapSetting;                              // the worldspace list is being set from code

    static WorldIndex? _index;
    static LoadOrderSpec? _order;
    static int _generation;                               // a newer open or pick makes an older answer stale
    static DispatcherTimer? _pickTimer, _searchTimer;
    static object? _picked;
    static bool _revealing;

    internal static WorldIndex? Index => _index;
    internal static LoadOrderSpec? Order => _order;
    /// <summary>What the header says now: reading, the load order's counts, or why it could not be read.</summary>
    internal static string Status { get; private set; } = "no load order is open: use Open above, or the button below.";
    internal static double? IndexSeconds { get; private set; }
    internal static IReadOnlyList<CellRow> LastHits { get; private set; } = Array.Empty<CellRow>();
    internal static string? LastSearch { get; private set; }

    internal static UIElement Build()
    {
        VirtualizingPanel.SetIsVirtualizing(Tree, true);
        VirtualizingPanel.SetVirtualizationMode(Tree, VirtualizationMode.Recycling);
        ScrollViewer.SetHorizontalScrollBarVisibility(Hits, ScrollBarVisibility.Auto);
        Header.Text = Status;
        BrowseProfile.ToolTip = "open the profile chosen on the Start tab as a world to browse";
        BrowseProfile.Click += (_, _) => Say(() => OpenLauncherProfile());
        var find = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(6, 2, 6, 2) };
        find.Children.Add(new TextBlock { Text = "Search", Width = 60, VerticalAlignment = VerticalAlignment.Center });
        find.Children.Add(SearchBox); find.Children.Add(SearchCount);
        SearchBox.TextChanged += (_, _) =>
        {
            _searchTimer ??= NewTimer(180, () => Search(SearchBox.Text));
            _searchTimer.Stop(); _searchTimer.Start();
        };
        Hits.SelectionChanged += (_, _) => { if (Hits.SelectedItem is ListBoxItem { Tag: string key }) Say(() => PickKey(key)); };
        Tree.SelectedItemChanged += (_, e) =>
        {
            if (_revealing || e.NewValue is not TreeViewItem { Tag: { } tag }) return;
            // a pick is acted on after a short pause, so stepping through the tree with the arrow keys does not start a load per row
            _picked = tag;
            _pickTimer ??= NewTimer(350, () => { if (_picked is { } t) Say(() => Pick(t)); });
            _pickTimer.Stop(); _pickTimer.Start();
        };
        var mapBar = new DockPanel { Margin = new Thickness(6, 2, 6, 2) };
        var mapLabel = new TextBlock { Text = "Map", Width = 60, VerticalAlignment = VerticalAlignment.Center };
        DockPanel.SetDock(mapLabel, Dock.Left); DockPanel.SetDock(MapWorld, Dock.Left); DockPanel.SetDock(MapTextured, Dock.Left);
        mapBar.Children.Add(mapLabel); mapBar.Children.Add(MapWorld); mapBar.Children.Add(MapTextured); mapBar.Children.Add(MapSays);
        // the tiles' own line under the bar, so the pointer's cell name does not hide it
        TileSays.Margin = new Thickness(66, 0, 6, 2);
        MapTextured.IsChecked = MapSettings.Textured;
        Map.Tiles.SetEnabled(MapTextured.IsChecked == true);
        MapTextured.Click += (_, _) => { MapSettings.Textured = MapTextured.IsChecked == true; SetTextured(MapTextured.IsChecked == true); };
        Map.Tiles.Changed += () => { TileSays.Text = Map.Tiles.Status; TileSays.Visibility = Map.Tiles.Status.Length > 0 ? Visibility.Visible : Visibility.Collapsed; };
        TileSays.Visibility = Visibility.Collapsed;
        MapWorld.SelectionChanged += (_, _) => { if (!_mapSetting && MapWorld.SelectedItem is ComboBoxItem { Tag: string key }) ShowMap(key, null); };
        Map.ShapesShown += () =>
        {
            if (Map.ShapesPainted is { } p && p.AsBoxes > 0) Shell.SetBanner($"map: {p.Buildings - p.AsBoxes:N0} building(s) in view drawn in their real shape; {p.AsBoxes:N0} whose mesh could not be read are drawn as boxes.");
        };
        Map.Hovered += at => MapSays.Text = at is { } sq ? MapSquareLabel(sq.X, sq.Y) : _mapStatus;
        Map.Clicked += (gx, gy) => Say(() => PickSquare(gx, gy));
        Tree.SelectedItemChanged += (_, e) =>
        {
            // the map follows the tree at once; the scene waits for the pause above
            if (e.NewValue is TreeViewItem { Tag: CellRow { Worldspace: { } world, GridX: { } gx, GridY: { } gy } }) ShowMap(world, (gx, gy));
        };
        // the map over the tree, with a bar between them to drag
        var body = new Grid();
        body.RowDefinitions.Add(new RowDefinition { Height = new GridLength(2, GridUnitType.Star), MinHeight = 80 });
        body.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        body.RowDefinitions.Add(new RowDefinition { Height = new GridLength(3, GridUnitType.Star), MinHeight = 80 });
        var bar = new GridSplitter { Height = 5, HorizontalAlignment = HorizontalAlignment.Stretch, Background = Theme.Brush("#2C3036") };
        var mapPanel = new DockPanel();
        DockPanel.SetDock(mapBar, Dock.Top); DockPanel.SetDock(TileSays, Dock.Top);
        mapPanel.Children.Add(mapBar); mapPanel.Children.Add(TileSays); mapPanel.Children.Add(Map.View);
        Grid.SetRow(mapPanel, 0); Grid.SetRow(bar, 1); Grid.SetRow(Tree, 2);
        body.Children.Add(mapPanel); body.Children.Add(bar); body.Children.Add(Tree);
        var panel = new DockPanel();
        foreach (var top in new UIElement[] { Header, Notes, BrowseProfile, find, Hits }) { DockPanel.SetDock(top, Dock.Top); panel.Children.Add(top); }
        panel.Children.Add(body);
        return panel;
    }

    // ---- the map ------------------------------------------------------------------------------------------------------
    static void SetMapStatus(string s) { _mapStatus = s; MapSays.Text = s; }

    /// <summary>Switch the map's textured tiles on or off, as the bar's checkbox does.</summary>
    static string SetTextured(bool on)
    {
        if (MapTextured.IsChecked != on) MapTextured.IsChecked = on;
        Map.Tiles.SetEnabled(on);
        Map.UpdateTiles();
        return on ? "the map draws textured tiles when zoomed in." : "the map draws the shaded shapes; textured tiles are off.";
    }

    /// <summary>Set the map as a state tool asks: its worldspace (EditorID or FormKey), its centre (Skyrim units), its
    /// zoom (screen pixels across one cell) and whether tiles are drawn. The view is set once the worldspace's terrain is read.</summary>
    internal static string SetMapView(string? world, double[]? centre, double? pixelsPerCell, bool? textured)
    {
        var (x, _) = Need();
        if (textured is { } on) SetTextured(on);
        string? key = _mapWorld;
        if (world is { Length: > 0 })
            key = x.Worldspaces.FirstOrDefault(w => string.Equals(w.Key, world, StringComparison.OrdinalIgnoreCase) || string.Equals(w.EditorId, world, StringComparison.OrdinalIgnoreCase))?.Key
                  ?? throw new InvalidOperationException($"'{world}' is not a worldspace of this load order; name one by its EditorID or FormKey.");
        if (key is null) throw new InvalidOperationException("the map shows no worldspace yet; name one with the worldspace.");
        if (centre is { Length: not 2 }) throw new InvalidOperationException("the map's centre is two numbers, [x, y] in Skyrim units.");
        _pendingView = (centre?[0], centre?[1], pixelsPerCell);
        if (!string.Equals(_mapWorld, key, StringComparison.OrdinalIgnoreCase)) { ShowMap(key, null); return $"reading the terrain of {key}; the view is set when it is shown."; }
        ApplyPendingView();
        return FormattableString.Invariant($"the map shows {key}") + (centre is { } c ? FormattableString.Invariant($" around ({c[0]:0}, {c[1]:0})") : "") + (pixelsPerCell is { } p ? FormattableString.Invariant($" at {p:0} pixels a cell") : "") + ".";
    }
    static (double? X, double? Y, double? PixelsPerCell)? _pendingView;

    static void ApplyPendingView()
    {
        if (_pendingView is not { } v || Map.Terrain is not { } t) return;
        _pendingView = null;
        double cx = v.X ?? (t.MinX + t.Width / 2.0) * 4096, cy = v.Y ?? (t.MinY + t.Height / 2.0) * 4096;
        Map.SetView(cx, cy, v.PixelsPerCell ?? Math.Max(Map.PixelsPerCell, 1));
    }

    static string MapSquareLabel(int gx, int gy) =>
        _index is { } x && _mapWorld is { } w && x.CellAt(w, gx, gy) is { } c ? CellLabel(c) : $"({gx}, {gy})  no cell here";

    /// <summary>Fill the map's worldspace list and show the main worldspace: Tamriel when the load order has it, or the one with the most cells.</summary>
    static void FillMapWorlds()
    {
        _mapSetting = true;
        try
        {
            MapWorld.Items.Clear();
            if (_index is not { } x) return;
            foreach (var w in x.Worldspaces.Where(w => w.CellCount > 0))
                MapWorld.Items.Add(new ComboBoxItem { Content = $"{w.EditorId ?? w.Key}" + (w.Name is { Length: > 0 } n ? $"  \"{n}\"" : "") + $"  ({w.CellCount:N0})", Tag = w.Key });
        }
        finally { _mapSetting = false; }
        var first = _index.Worldspaces.FirstOrDefault(w => string.Equals(w.EditorId, "Tamriel", StringComparison.OrdinalIgnoreCase) && w.CellCount > 0)
                    ?? _index.Worldspaces.FirstOrDefault(w => w.CellCount > 0);
        if (first is null) { Map.Show(null, null); SetMapStatus("this load order has no worldspace with cells."); return; }
        ShowMap(first.Key, null);
    }

    /// <summary>Show a worldspace on the map, reading its terrain when it is not the one shown, and mark a grid square on it.</summary>
    static void ShowMap(string worldKey, (int X, int Y)? mark)
    {
        if (_index is not { } x) return;
        if (string.Equals(_mapWorld, worldKey, StringComparison.OrdinalIgnoreCase))
        {
            if (mark is { } m) { _pendingMark = Map.Terrain is null ? m : null; Map.Choose(m.X, m.Y); }
            return;
        }
        _mapWorld = worldKey; _pendingMark = mark;
        _mapSetting = true;
        try { MapWorld.SelectedItem = MapWorld.Items.OfType<ComboBoxItem>().FirstOrDefault(i => string.Equals(i.Tag as string, worldKey, StringComparison.OrdinalIgnoreCase)); }
        finally { _mapSetting = false; }
        Map.Show(null, null);
        Map.Tiles.SetWorld(null, null, null);
        SetMapStatus("reading the terrain");
        int gen = _generation;
        var ui = Dispatcher.CurrentDispatcher;
        x.TerrainOfAsync(worldKey, CancellationToken.None).ContinueWith(r => (Terrain: r, Picture: r.IsCompletedSuccessfully ? WorldMap.Paint(r.Result) : null), TaskScheduler.Default)
         .ContinueWith(done => ui.BeginInvoke(() =>
        {
            if (gen != _generation || !string.Equals(_mapWorld, worldKey, StringComparison.OrdinalIgnoreCase)) return;
            var r = done.Result.Terrain;
            if (r.IsFaulted) { SetMapStatus("the terrain could not be read: " + r.Exception!.GetBaseException().Message); return; }
            var t = r.Result;
            Map.Show(t, done.Result.Picture);
            Map.Tiles.SetWorld(worldKey, t, (gx, gy) => x.CellAt(worldKey, gx, gy)?.Key);
            ApplyPendingView();
            MapSeconds = t.Seconds;
            string lent = t.BorrowedCells == 0 ? "" : $", and {t.BorrowedCells:N0} squares of its parent {x.Worldspaces.FirstOrDefault(w => w.Key == t.Parent)?.EditorId ?? t.Parent}'s ground (dim)";
            SetMapStatus(FormattableString.Invariant($"{t.LandCells:N0} cells of terrain{lent}, read in {t.Seconds:0.0} s")
                         + (t.Unreadable > 0 ? $"; {t.Unreadable:N0} could not be read and are not drawn" : "") + ". Wheel zooms, drag pans, click opens a cell.");
            if (_pendingMark is { } m) { _pendingMark = null; Map.Choose(m.X, m.Y); }
            // the buildings and captions follow the terrain: they take longer to read
            string terrainSaid = _mapStatus;
            SetMapStatus("reading buildings and places; " + terrainSaid);
            x.FeaturesOfAsync(worldKey, CancellationToken.None).ContinueWith(fr => ui.BeginInvoke(() =>
            {
                if (gen != _generation || !string.Equals(_mapWorld, worldKey, StringComparison.OrdinalIgnoreCase)) return;
                if (fr.IsFaulted) { SetMapStatus("buildings and places could not be read: " + fr.Exception!.GetBaseException().Message + " " + terrainSaid); return; }
                Map.ShowFeatures(fr.Result);
                SetMapStatus(FormattableString.Invariant($"{fr.Result.Outlines.Count:N0} buildings and {fr.Result.Places.Count:N0} places, read in {fr.Result.Seconds:0.0} s. ") + terrainSaid);
            }), TaskScheduler.Default);
        }), TaskScheduler.Default);
    }
    static (int X, int Y)? _pendingMark;
    internal static double? MapSeconds { get; private set; }

    /// <summary>Open the cell on a square of the map's worldspace, as picking it in the tree does.</summary>
    internal static string PickSquare(int gx, int gy)
    {
        var (x, _) = Need();
        if (_mapWorld is not { } w) throw new InvalidOperationException("the map shows no worldspace yet.");
        var cell = x.CellAt(w, gx, gy) ?? throw new InvalidOperationException($"no cell stands on ({gx}, {gy}) of this worldspace in this load order.");
        Map.Choose(gx, gy);
        return PickKey(cell.Key);
    }

    static DispatcherTimer NewTimer(int ms, Action act)
    {
        var t = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(ms) };
        t.Tick += (_, _) => { t.Stop(); act(); };
        return t;
    }

    static void Say(Func<string> act)
    {
        try { Shell.SetBanner(act()); }
        catch (InvalidOperationException ex) { Shell.SetBanner(ex.Message); }
    }

    static void SetStatus(string s) { Status = s; Header.Text = s; }

    /// <summary>The profile the Start tab has open, as a world to browse.</summary>
    internal static string OpenLauncherProfile()
    {
        var l = Shell.Launcher;
        if (l.Instance is null || l.Profile is null || l.Game is null)
            throw new InvalidOperationException("no profile is open on the Start tab; open one there, or use Open above with an MO2 instance, a profile folder or a game folder.");
        return Open(new LoadOrderSpec(l.Instance, l.Profile, l.Game, null, $"profile '{l.Profile}'") { Navmesh = false }, Array.Empty<string>());
    }

    /// <summary>Open a load order's world: the tab comes to the front and its index is read on a background task.</summary>
    internal static string Open(LoadOrderSpec order, IReadOnlyList<string> notes)
    {
        int gen = ++_generation;
        _order = order; _index = null; IndexSeconds = null;
        _mapWorld = null; _pendingMark = null; MapSeconds = null; MapWorld.Items.Clear(); Map.Show(null, null); SetMapStatus("");
        Map.Meshes = null;
        // the load order's meshes, for the buildings' real shapes; without them the map draws boxes
        Loading.Set("buildings", "Reading the buildings' shapes for the map");
        Task.Run(() => BuildingMeshes.For(order)).ContinueWith(t => { if (gen == _generation) { Loading.Clear("buildings"); if (t.IsCompletedSuccessfully) Map.Meshes = t.Result; } }, TaskScheduler.FromCurrentSynchronizationContext());
        // the load order's tile folder: its name is a digest of every plugin's size and time, read off the window's thread
        Map.Tiles.SetOrder(null, null, null);
        Task.Run(() => MapTileCache.FolderFor(order)).ContinueWith(t =>
        {
            if (gen != _generation) return;
            Map.Tiles.SetOrder(order, t.IsCompletedSuccessfully ? t.Result : null,
                t.IsCompletedSuccessfully ? null : $"the tile folder could not be made ({t.Exception!.GetBaseException().Message.TrimEnd('.')}).");
            if (Map.Terrain is { } terrain && _mapWorld is { } w && _index is { } ix) Map.Tiles.SetWorld(w, terrain, (gx, gy) => ix.CellAt(w, gx, gy)?.Key);
            Map.UpdateTiles();
        }, TaskScheduler.FromCurrentSynchronizationContext());
        Tree.Items.Clear(); Hits.Items.Clear(); Hits.Visibility = Visibility.Collapsed; SearchCount.Text = ""; LastHits = Array.Empty<CellRow>();
        Notes.Text = string.Join(" ", notes.Select(n => n.TrimEnd('.') + "."));
        SetStatus($"reading the load order of {order.Label}");
        Loading.Set("world", $"Reading the load order of {order.Label}");
        var ui = Dispatcher.CurrentDispatcher;
        var sw = Stopwatch.StartNew();
        var say = new Progress<string>(s => { if (gen == _generation) { SetStatus($"reading the load order of {order.Label}: {s}"); Loading.Set("world", $"Reading the load order of {order.Label}: {s}"); } });
        LoadOrderIndexes.For(order, say).ContinueWith(t => ui.BeginInvoke(() =>
        {
            if (gen != _generation) return;
            Loading.Clear("world");
            if (t.IsFaulted)
            {
                var why = t.Exception!.GetBaseException().Message;
                SetStatus($"the load order of {order.Label} could not be read: {why}");
                Shell.SetBanner($"the world of {order.Label} could not be opened: {why}");
                return;
            }
            _index = t.Result; IndexSeconds = sw.Elapsed.TotalSeconds;
            Fill();
            FillMapWorlds();
            var x = _index;
            int exterior = x.Cells.Count(c => !c.IsPersistentCell && c.Worldspace is not null);
            SetStatus(FormattableString.Invariant(
                $"{order.Label}: {x.Plugins.Count} plugins, {x.Worldspaces.Count} worldspaces, {x.Interiors.Count:N0} interior cells, {exterior:N0} exterior cells; read in {IndexSeconds:0.0} s. Pick a cell or an object. Read-only."));
            if (x.Notes.Count > 0) Notes.Text = (Notes.Text + " " + string.Join(" ", x.Notes)).Trim();
            Shell.SetBanner($"the world of {order.Label} is open: {x.Worldspaces.Count} worldspaces and {x.Interiors.Count:N0} interior cells.");
        }), TaskScheduler.Default);
        return $"opening the world of {order.Label}; its tree fills when the load order has been read.";
    }

    // ---- the tree -----------------------------------------------------------------------------------------------------
    internal static string CellLabel(CellRow c) =>
        (c.EditorId ?? "(no EditorID)") + (c.Name is { Length: > 0 } n ? $"  \"{n}\"" : "")
        + (c.GridX is { } gx && c.GridY is { } gy && !c.Interior ? $"  ({gx}, {gy})" : "") + $"  [{c.Key}]  winner: {c.Winner}";

    internal static string ObjectLabel(ObjectRow o) =>
        $"{o.Type}  {o.EditorId ?? "(no EditorID)"}"
        + (o.BaseKey is null ? "" : $"  base {o.BaseEditorId ?? o.BaseKey}" + (o.BaseType is { } bt ? $" ({bt})" : ""))
        + $"  [{o.Key}]  winner: {o.Winner}" + (o.Deleted ? "  [deleted]" : "") + (o.InitiallyDisabled ? "  [initially disabled]" : "");

    internal static string WorldspaceLabel(WorldspaceRow w) =>
        $"Worldspace  {w.EditorId ?? "(no EditorID)"}" + (w.Name is { Length: > 0 } n ? $"  \"{n}\"" : "") + $"  [{w.Key}]  {w.CellCount:N0} cells  winner: {w.Winner}";

    static TreeViewItem Node(string header, object? tag, Action<TreeViewItem>? fill = null)
    {
        var t = new TreeViewItem { Header = header, Tag = tag };
        if (fill is not null)
        {
            t.Items.Add(new TreeViewItem { Header = "reading", IsEnabled = false });
            bool filled = false;
            t.Expanded += (_, e) =>
            {
                if (filled || !ReferenceEquals(e.OriginalSource, t)) return;
                filled = true; t.Items.Clear(); fill(t);
            };
        }
        return t;
    }

    static void Fill()
    {
        Tree.Items.Clear();
        if (_index is not { } x) return;
        foreach (var w in x.Worldspaces) Tree.Items.Add(Node(WorldspaceLabel(w), w, t => FillWorldspace(t, w)));
        Tree.Items.Add(Node($"Interior cells  ({x.Interiors.Count:N0})", "interiors", t => FillBlocks(t, x.Interiors, interior: true)));
    }

    static void FillWorldspace(TreeViewItem t, WorldspaceRow w)
    {
        if (_index is not { } x) return;
        if (w.PersistentCell is { } pc)
        {
            var row = x.Cells.FirstOrDefault(c => string.Equals(c.Key, pc, StringComparison.OrdinalIgnoreCase));
            t.Items.Add(Node($"Persistent cell  [{pc}]" + (row is null ? "" : $"  winner: {row.Winner}"), new PersistentCell(pc), n => FillObjects(n, pc)));
        }
        FillBlocks(t, x.CellsOf(w.Key), interior: false);
    }
    internal sealed record PersistentCell(string Key);

    static void FillBlocks(TreeViewItem t, IReadOnlyList<CellRow> cells, bool interior)
    {
        foreach (var block in cells.GroupBy(c => (c.BlockA, c.BlockB)).OrderBy(g => g.Key.BlockA).ThenBy(g => g.Key.BlockB))
        {
            var list = block.ToList();
            string name = interior ? $"Block {block.Key.BlockA}" : $"Block {block.Key.BlockA}, {block.Key.BlockB}";
            t.Items.Add(Node($"{name}  ({list.Count:N0})", null, b =>
            {
                foreach (var sub in list.GroupBy(c => (c.SubBlockA, c.SubBlockB)).OrderBy(g => g.Key.SubBlockA).ThenBy(g => g.Key.SubBlockB))
                {
                    var subList = sub.OrderBy(c => c.GridX ?? 0).ThenBy(c => c.GridY ?? 0).ThenBy(c => c.EditorId ?? c.Key, StringComparer.OrdinalIgnoreCase).ToList();
                    string subName = interior ? $"Sub-block {sub.Key.SubBlockA}" : $"Sub-block {sub.Key.SubBlockA}, {sub.Key.SubBlockB}";
                    b.Items.Add(Node($"{subName}  ({subList.Count:N0})", null, s =>
                    {
                        foreach (var c in subList) s.Items.Add(Node(CellLabel(c), c, n => FillObjects(n, c.Key)));
                    }));
                }
            }));
        }
    }

    /// <summary>A cell's records, read on a background task; the node says "reading…" meanwhile and rows are added in
    /// batches so a cell of thousands does not hold the window.</summary>
    static void FillObjects(TreeViewItem t, string cellKey)
    {
        if (_index is not { } x) return;
        int gen = _generation;
        t.Items.Add(new TreeViewItem { Header = "reading", IsEnabled = false });
        var ui = Dispatcher.CurrentDispatcher;
        x.ObjectsOfAsync(cellKey, CancellationToken.None).ContinueWith(r => ui.BeginInvoke(() =>
        {
            if (gen != _generation) return;
            t.Items.Clear();
            if (r.IsFaulted) { t.Items.Add(new TreeViewItem { Header = "could not be read: " + r.Exception!.GetBaseException().Message, IsEnabled = false }); return; }
            var o = r.Result;
            void Group(string name, IReadOnlyList<ObjectRow> rows)
            {
                var g = Node($"{name}  ({rows.Count:N0})", null, n => AddInBatches(n, rows, gen));
                t.Items.Add(g);
                // the object a search or a FormKey asked for: its group is opened, and its row selected when it is added
                if (_revealObject is { } want && rows.Any(r => string.Equals(r.Key, want, StringComparison.OrdinalIgnoreCase))) g.IsExpanded = true;
            }
            Group("Persistent", o.Persistent);
            Group("Temporary", o.Temporary);
            if (o.Unfiled.Count > 0) Group("Not listed by the winning plugin's copy of the cell", o.Unfiled);
            foreach (var note in o.Notes) t.Items.Add(new TreeViewItem { Header = note, IsEnabled = false });
        }), TaskScheduler.Default);
    }

    static void AddInBatches(TreeViewItem t, IReadOnlyList<ObjectRow> rows, int gen, int from = 0)
    {
        const int batch = 400;
        if (gen != _generation) return;
        for (int i = from; i < Math.Min(rows.Count, from + batch); i++)
        {
            var item = Node(ObjectLabel(rows[i]), rows[i]);
            t.Items.Add(item);
            if (_revealObject is { } want && string.Equals(rows[i].Key, want, StringComparison.OrdinalIgnoreCase))
            {
                _revealObject = null;
                _revealing = true;
                try { item.IsSelected = true; item.BringIntoView(); ScrollLeft(); } finally { _revealing = false; }
            }
        }
        if (from + batch < rows.Count)
            Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.Background, () => AddInBatches(t, rows, gen, from + batch));
    }

    // ---- picking ------------------------------------------------------------------------------------------------------
    static string Pick(object tag) => tag switch
    {
        CellRow c => OpenCell(c.Key, null),
        ObjectRow o => OpenObject(o),
        PersistentCell => "a worldspace's persistent cell is not shown as one scene; pick one of its objects to open the cell it stands in.",
        WorldspaceRow w => $"{w.EditorId ?? w.Key}: {w.CellCount:N0} cells; open a block to choose one.",
        _ => Shell.BannerText,
    };

    /// <summary>Ask for a cell's scene on the browsed load order.</summary>
    internal static string OpenCell(string cellKey, string? landOn)
    {
        var (x, order) = Need();
        // the map marks the cell however it was asked for (the tree, a search, the map, a state tool)
        if (x.Cells.FirstOrDefault(c => string.Equals(c.Key, cellKey, StringComparison.OrdinalIgnoreCase)) is { Worldspace: { } world, GridX: { } gx, GridY: { } gy })
            ShowMap(world, (gx, gy));
        int gen = ++_pickGeneration;
        var ui = Dispatcher.CurrentDispatcher;
        // that the cell reads on this load order is checked from its records (a moment), off the window's thread
        x.ObjectsOfAsync(cellKey, CancellationToken.None).ContinueWith(r => ui.BeginInvoke(() =>
        {
            if (gen != _pickGeneration) return;
            Say(() =>
            {
                if (r.IsFaulted) throw new InvalidOperationException($"cell {cellKey} could not be read: {r.Exception!.GetBaseException().Message}");
                Shell.PartnerFor = null; Shell.SceneCaption = null;
                return SceneDirector.Request(new SceneRequest(order, cellKey, landOn, landOn is null ? "the World tab's cell" : $"to show {landOn}"));
            });
        }), TaskScheduler.Default);
        return $"opening cell {cellKey}";
    }
    static int _pickGeneration;
    static string? _revealObject;                        // an object to select in the tree once its cell's rows arrive

    static string OpenObject(ObjectRow o)
    {
        var (x, _) = Need();
        if (o.Type is "NAVM" or "LAND")
        {
            // a navmesh or landscape record is not an object the camera lands on: its cell is opened
            var where = x.Locate(o.Key)?.Cell ?? throw new InvalidOperationException($"{o.Key} is not filed under a cell in this load order.");
            return OpenCell(where, null);
        }
        if (o.Deleted) throw new InvalidOperationException($"{o.Key} is deleted by {o.Winner}, so no scene draws it.");
        int gen = ++_pickGeneration;
        var ui = Dispatcher.CurrentDispatcher;
        Task.Run(() => x.OpenableCellOf(o.Key)).ContinueWith(r => ui.BeginInvoke(() =>
        {
            if (gen != _pickGeneration) return;
            Say(() =>
            {
                if (r.IsFaulted) throw new InvalidOperationException($"{o.Key} could not be read: {r.Exception!.GetBaseException().Message}");
                var cell = r.Result ?? throw new InvalidOperationException($"{o.Key} is filed in a persistent cell and its position falls in no cell of its worldspace, so there is no cell to open for it.");
                return OpenCell(cell, o.Key);
            });
        }), TaskScheduler.Default);
        return $"finding the cell {o.Key} stands in";
    }

    static (WorldIndex Index, LoadOrderSpec Order) Need() =>
        _index is { } x && _order is { } o ? (x, o)
        : throw new InvalidOperationException(_order is null ? "no load order is open in the World tab; open one with Open above." : "the load order is still being read; try again in a moment.");

    /// <summary>Open whatever a FormKey names in the browsed load order: a cell, or the cell of a record with the camera on it.</summary>
    internal static string PickKey(string key)
    {
        var (x, _) = Need();
        var at = x.Locate(key) ?? throw new InvalidOperationException($"{key} is not a record of this load order.");
        if (at.IsCell) { Reveal(at.Key); return OpenCell(at.Key, null); }
        if (at.Cell is null) throw new InvalidOperationException($"{key} is a {at.RecordType} record, which is not in any cell, so there is nothing to show.");
        _revealObject = at.Key;
        Reveal(at.Cell);
        if (at.RecordType is "NAVM" or "LAND") return OpenCell(at.Cell, null);
        var open = x.OpenableCellOf(at.Key) ?? throw new InvalidOperationException($"{key} is filed in a persistent cell and its position falls in no cell of its worldspace, so there is no cell to open for it.");
        return OpenCell(open, at.Key);
    }

    // ---- search -------------------------------------------------------------------------------------------------------
    /// <summary>Filter the cell index by the text; a FormKey of a record in a cell lists that cell.</summary>
    internal static string Search(string? text)
    {
        if (SearchBox.Text != (text ?? "")) SearchBox.Text = text ?? "";
        LastSearch = text;
        Hits.Items.Clear();
        if (string.IsNullOrWhiteSpace(text)) { Hits.Visibility = Visibility.Collapsed; SearchCount.Text = ""; LastHits = Array.Empty<CellRow>(); return "the search is cleared."; }
        if (_index is not { } x) { SearchCount.Text = "no load order is open"; return "no load order is open in the World tab."; }
        const int max = 200;
        var hits = x.Search(text, max);
        LastHits = hits;
        foreach (var c in hits) Hits.Items.Add(new ListBoxItem { Content = (c.Interior || c.Worldspace is null ? "interior  " : "exterior  ") + CellLabel(c), Tag = c.Key });
        Hits.Visibility = hits.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        SearchCount.Text = hits.Count == 0 ? "no cell matches" : hits.Count >= max ? $"the first {max} of more" : $"{hits.Count} of {x.Cells.Count:N0} cells";
        return hits.Count == 0 ? $"no cell matches '{text}'" : $"{hits.Count} cell(s) match '{text}'";
    }

    /// <summary>Open the tree down to a cell and select it, without starting a second load for the selection.</summary>
    static void Reveal(string cellKey)
    {
        if (_index is not { } x) return;
        var cell = x.Cells.FirstOrDefault(c => string.Equals(c.Key, cellKey, StringComparison.OrdinalIgnoreCase));
        if (cell is null) return;
        TreeViewItem? Child(ItemsControl parent, Func<object?, bool> tag) => parent.Items.OfType<TreeViewItem>().FirstOrDefault(i => tag(i.Tag));
        TreeViewItem? ByHeader(ItemsControl parent, string start) => parent.Items.OfType<TreeViewItem>().FirstOrDefault(i => (i.Header as string)?.StartsWith(start, StringComparison.Ordinal) == true);
        _revealing = true;
        try
        {
            bool interior = cell.Worldspace is null;
            var top = interior ? Child(Tree, t => t is "interiors") : Child(Tree, t => t is WorldspaceRow w && string.Equals(w.Key, cell.Worldspace, StringComparison.OrdinalIgnoreCase));
            if (top is null) return;
            top.IsExpanded = true;
            TreeViewItem? target;
            if (cell.IsPersistentCell) target = Child(top, t => t is PersistentCell);
            else
            {
                var block = ByHeader(top, (interior ? $"Block {cell.BlockA}" : $"Block {cell.BlockA}, {cell.BlockB}") + "  (");
                if (block is null) return;
                block.IsExpanded = true;
                var sub = ByHeader(block, (interior ? $"Sub-block {cell.SubBlockA}" : $"Sub-block {cell.SubBlockA}, {cell.SubBlockB}") + "  (");
                if (sub is null) return;
                sub.IsExpanded = true;
                target = Child(sub, t => t is CellRow c && string.Equals(c.Key, cell.Key, StringComparison.OrdinalIgnoreCase));
            }
            if (target is null) return;
            target.IsSelected = true;
            target.IsExpanded = true;                        // its Persistent and Temporary groups are read and shown
            target.BringIntoView();
            ScrollLeft();
        }
        finally { _revealing = false; }
    }

    /// <summary>After a row is brought into view, put the tree back at its left edge: a long row otherwise scrolls the
    /// tree sideways and hides the levels above it.</summary>
    static void ScrollLeft() =>
        Tree.Dispatcher.BeginInvoke(DispatcherPriority.Background, () =>
        {
            if (FindScroll(Tree) is { } sv) sv.ScrollToHorizontalOffset(0);
        });

    static ScrollViewer? FindScroll(DependencyObject d)
    {
        for (int i = 0; i < System.Windows.Media.VisualTreeHelper.GetChildrenCount(d); i++)
        {
            var c = System.Windows.Media.VisualTreeHelper.GetChild(d, i);
            if (c is ScrollViewer sv) return sv;
            if (FindScroll(c) is { } deeper) return deeper;
        }
        return null;
    }

    // ---- what the state tools read ------------------------------------------------------------------------------------
    internal static object State(string? cell, int limit, int offset)
    {
        var x = _index;
        object? objects = null;
        if (x is not null && cell is not null)
        {
            var o = x.ObjectsOfAsync(cell, CancellationToken.None).GetAwaiter().GetResult();
            object Rows(IReadOnlyList<ObjectRow> rows) => new
            {
                total = rows.Count,
                rows = rows.Skip(offset).Take(limit).Select(r => new
                {
                    key = r.Key, type = r.Type, editorId = r.EditorId, baseKey = r.BaseKey, baseEditorId = r.BaseEditorId, baseType = r.BaseType,
                    winner = r.Winner, persistent = r.Persistent, deleted = r.Deleted, initiallyDisabled = r.InitiallyDisabled, position = r.Position, label = ObjectLabel(r),
                }).ToList(),
            };
            objects = new { cell, hasPlacedOrNavmesh = o.HasPlacedOrNavmesh, persistent = Rows(o.Persistent), temporary = Rows(o.Temporary), unfiled = o.Unfiled.Count, notes = o.Notes };
        }
        return new
        {
            status = Status, loadOrder = _order?.Label, instance = _order?.Instance, profile = _order?.Profile, game = _order?.Game, orderFile = _order?.OrderFile,
            ready = x is not null, indexSeconds = IndexSeconds,
            plugins = x?.Plugins.Count, pluginOrder = x?.Plugins,
            worldspaces = x?.Worldspaces.Select(w => new { key = w.Key, editorId = w.EditorId, name = w.Name, winner = w.Winner, persistentCell = w.PersistentCell, cells = w.CellCount, label = WorldspaceLabel(w) }).ToList(),
            interiorCells = x?.Interiors.Count, cells = x?.Cells.Count, notes = x?.Notes,
            search = LastSearch, hits = LastHits.Skip(offset).Take(limit).Select(c => new
            {
                key = c.Key, editorId = c.EditorId, name = c.Name, interior = c.Interior, worldspace = c.Worldspace, gridX = c.GridX, gridY = c.GridY, winner = c.Winner,
                block = new[] { c.BlockA, c.BlockB }, subBlock = new[] { c.SubBlockA, c.SubBlockB }, label = CellLabel(c),
            }).ToList(),
            objects,
            map = new { worldspace = _mapWorld, ready = Map.Terrain is not null, status = _mapStatus, seconds = MapSeconds, landCells = Map.Terrain?.LandCells, samples = Map.Terrain?.Samples, parent = Map.Terrain?.Parent, borrowedCells = Map.Terrain?.BorrowedCells,
                        unreadable = Map.Terrain?.Unreadable, grid = Map.Terrain is { } t ? new[] { t.MinX, t.MinY, t.Width, t.Height } : null,
                        chosen = Map.Chosen is { } c ? new[] { c.X, c.Y } : null,
                        featuresReady = Map.Features is not null, featureSeconds = Map.Features?.Seconds, buildings = Map.Features?.Outlines.Count, places = Map.Features?.Places.Count,
                        placedRead = Map.Features?.PlacedRead, pixelsPerCell = Map.PixelsPerCell, buildingsShown = Map.BuildingsShown, captionsShown = Map.CaptionsShown,
                        shapes = Map.ShapesPainted is { } sp ? new { buildings = sp.Buildings, asBoxes = sp.AsBoxes, seconds = sp.Seconds, unitsPerPixel = sp.UnitsPerPixel } : null,
                        tiles = Map.Tiles.State() },
        };
    }
}

/// <summary>An MO2 instance folder or one of its profile folders: opens that profile's world in the World tab.</summary>
internal sealed class ProfileReader : IInputReader
{
    public string Kind => "MO2 profile";
    public string Accepts => "an MO2 instance or profile folder";

    /// <summary>The instance and profile a path names; the profile is null for an instance folder (its selected one is used).</summary>
    internal static (string Instance, string? Profile)? Parse(string path)
    {
        var dir = File.Exists(path) ? Path.GetDirectoryName(Path.GetFullPath(path))! : Path.GetFullPath(path);
        dir = dir.TrimEnd('\\', '/');
        if (File.Exists(Path.Combine(dir, "modlist.txt")) && Path.GetDirectoryName(dir) is { } profiles
            && string.Equals(Path.GetFileName(profiles), "profiles", StringComparison.OrdinalIgnoreCase) && Path.GetDirectoryName(profiles) is { } inst)
            return (inst, Path.GetFileName(dir));
        if (File.Exists(Path.Combine(dir, Mo2Instance.IniFileName)) || Directory.Exists(Path.Combine(dir, "profiles"))) return (dir, null);
        return null;
    }

    public InputMatch? Recognise(string path) =>
        Parse(path) is { } p ? new InputMatch(this, path, p.Profile is null ? "an MO2 instance" : $"the MO2 profile '{p.Profile}'") : null;

    public string Open(InputMatch match)
    {
        var (inst, profile) = Parse(match.Path)!.Value;
        var root = Mo2Instance.LayoutRoot(inst);
        if (!string.Equals(Path.GetFullPath(root).TrimEnd('\\', '/'), Path.GetFullPath(inst).TrimEnd('\\', '/'), StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException($"'{inst}' keeps its mods and profiles in '{root}' (ModOrganizer.ini base_directory), which the viewer does not read yet; open the game's Data folder instead.");
        profile ??= Mo2Instance.ReadSelectedProfile(inst)
                    ?? throw new InvalidOperationException($"'{inst}' selects no profile in its ModOrganizer.ini; open one of its profile folders instead.");
        if (!File.Exists(Path.Combine(inst, "profiles", profile, "modlist.txt")))
            throw new InvalidOperationException($"the profile '{profile}' has no modlist.txt under '{Path.Combine(inst, "profiles")}'; open one of the instance's profile folders.");
        if (!Mo2Instance.TryResolve(inst, out var paths) || paths is null || DataFolderOrder.GameOf(paths.GamePath) is not { } game)
            throw new InvalidOperationException($"'{inst}' names no Skyrim game folder in its ModOrganizer.ini (gamePath), so the base game's plugins cannot be found; open the profile from the Start tab with the game folder given.");
        return WorldView.Open(new LoadOrderSpec(inst, profile, game, null, $"profile '{profile}' of '{inst}'") { Navmesh = false }, Array.Empty<string>());
    }
}

/// <summary>A game folder or its Data folder: opens that Data folder's world in the World tab (<see cref="DataFolderOrder"/>).</summary>
internal sealed class DataFolderReader : IInputReader
{
    public string Kind => "game Data folder";
    public string Accepts => "a game or Data folder";

    public InputMatch? Recognise(string path) =>
        DataFolderOrder.GameOf(path) is { } game ? new InputMatch(this, game, "a game's Data folder" + (DataFolderOrder.UnderMo2 ? ", as Mod Organizer 2 shows it" : "")) : null;

    public string Open(InputMatch match)
    {
        var staged = DataFolderOrder.Stage(match.Path, Program.RequestWork());
        foreach (var n in staged.Notes) WorkbenchHost.Say("data folder: " + n);
        var said = WorldView.Open(staged.Order with { Navmesh = false }, staged.Notes);
        if (Shell.Tabs is not null) Shell.ShowTab("World");     // a Data folder has no mod list on Start: its world is what there is to see
        return said;
    }
}
