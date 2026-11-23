using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using HarnessShared;

namespace Workbench;

/// <summary>One plugin of a scene's load order as the panel lists it: its file name, its index as MO2 shows it (hex, a
/// light plugin as FE:xxx), and its masters as its header states them (null: the header could not be read).</summary>
internal sealed record OrderEntry(string Name, string Index, IReadOnlyList<string>? Masters);

/// <summary>The load-order panel's rules that need no window: the order a scene is built on, what switching plugins off
/// takes with it, and the order file that leaves them out.</summary>
internal static class LoadOrderRules
{
    /// <summary>The plugins of the order, in load order, read by the owner viewprobe builds the scene with
    /// (<see cref="LoadOrderIndexes.PathsOf"/>), with their MO2 index and their masters.</summary>
    internal static IReadOnlyList<OrderEntry> Entries(LoadOrderSpec order)
    {
        var list = new List<OrderEntry>();
        int full = 0, light = 0;
        var paths = LoadOrderIndexes.PathsOf(order);
        // an order file's names are listed as it spells them, so the list reads exactly as the setup does
        var spelled = order.OrderFile is { } f ? CandidateFile.Read(f).Plugins : null;
        if (spelled is not null && (spelled.Count != paths.Count || spelled.Where((n, i) => !n.Equals(Path.GetFileName(paths[i]), StringComparison.OrdinalIgnoreCase)).Any()))
            throw new InvalidOperationException($"the plugins read for {order.Label} are not the ones its order file lists, so the list would not be the scene's load order; open the row again.");
        for (int i = 0; i < paths.Count; i++)
        {
            var h = PluginHeader.TryRead(paths[i]);
            bool isLight = h?.LightFlag ?? false;
            list.Add(new OrderEntry(spelled?[i] ?? Path.GetFileName(paths[i]), isLight ? $"FE:{light++:X3}" : $"{full++:X2}", h?.Masters));
        }
        return list;
    }

    /// <summary>What switching <paramref name="off"/> leaves loaded, and the plugins that go with them: every plugin that
    /// lists a switched-off plugin as a master, found by the missing-master rule (<see cref="OrderMasters.Gaps"/>) until
    /// none is left. A header that could not be read makes no claim, so that plugin stays.</summary>
    internal static (IReadOnlyList<string> Kept, IReadOnlyList<string> AlsoOff) Without(IReadOnlyList<OrderEntry> order, IReadOnlySet<string> off)
    {
        var kept = order.Where(e => !off.Contains(e.Name)).ToList();
        var also = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        while (true)
        {
            var gaps = OrderMasters.Gaps(kept.Select(e => (e.Name, e.Masters)).ToList());
            if (gaps.Count == 0) break;
            foreach (var g in gaps) also.Add(g.Plugin);
            kept.RemoveAll(e => also.Contains(e.Name));
        }
        return (kept.Select(e => e.Name).ToList(), order.Where(e => also.Contains(e.Name)).Select(e => e.Name).ToList());
    }

    /// <summary>The plugins that look like patches for a switched-off plugin but do not list it as a master, so they stay
    /// loaded (Piranha, 2026-11-22: "only plugins that list it as a master" go with it; the others are shown in bold red).
    /// Nothing in a plugin says what it was made for, so the rule is its name: a plugin that loads after the switched-off
    /// one and whose file name holds that plugin's name (four letters or more, without the extension), and that does not
    /// master it. Each comes with the sentence its tooltip says. Nothing is removed by this.</summary>
    internal static IReadOnlyDictionary<string, string> LooksLikePatches(IReadOnlyList<OrderEntry> order, IReadOnlySet<string> off, IReadOnlyCollection<string> alsoOff)
    {
        var found = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var gone = new HashSet<string>(off.Concat(alsoOff), StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i < order.Count; i++)
        {
            if (!off.Contains(order[i].Name)) continue;
            var stem = Path.GetFileNameWithoutExtension(order[i].Name);
            if (stem.Length < 4) continue;
            for (int j = i + 1; j < order.Count; j++)
            {
                var p = order[j];
                if (gone.Contains(p.Name) || found.ContainsKey(p.Name)) continue;
                if (!Path.GetFileNameWithoutExtension(p.Name).Contains(stem, StringComparison.OrdinalIgnoreCase)) continue;
                if (p.Masters?.Contains(order[i].Name, StringComparer.OrdinalIgnoreCase) == true) continue;
                found[p.Name] = $"Its name holds '{stem}', so it may be a patch for {order[i].Name}, but it does not list {order[i].Name} as a master, so it stays loaded. Untick it too to see the cell without it.";
            }
        }
        return found;
    }

    /// <summary>The order file for <paramref name="baseOrder"/> without some plugins: the kept plugins in their order, the
    /// base order file's mod list as it is (the meshes and textures stay the same), written under <paramref name="folder"/>
    /// with a name taken from its plugins, so the same choice is the same file.</summary>
    internal static string WriteWithout(LoadOrderSpec baseOrder, IReadOnlyList<string> kept, IReadOnlyList<string> switchedOff, IReadOnlyList<string> alsoOff, string folder)
    {
        IReadOnlyList<string>? mods = baseOrder.OrderFile is { } f ? CandidateFile.Read(f).Mods : null;
        var changes = switchedOff.Select(p => $"switched off {p}").Concat(alsoOff.Select(p => $"switched off {p}, which needs a plugin switched off")).ToList();
        var name = "without-" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(string.Join("\n", kept).ToLowerInvariant() + "|" + (baseOrder.OrderFile ?? baseOrder.Profile))))[..12].ToLowerInvariant();
        var cand = new CandidateFile(ConfigurationIdentity.CurrentSchema, name, baseOrder.Profile, kept, mods, changes,
            new Dictionary<string, JsonElement> { ["workbench"] = JsonSerializer.SerializeToElement("a load order to look at without some plugins; read-only, nothing is written from it") });
        var path = Path.Combine(folder, name + ".json");
        cand.Write(path);
        return path;
    }
}

/// <summary>The load order of the scene shown, plugin by plugin, in place of most of the left pane (walkthrough problem 7):
/// which plugins the scene is built on, which of them edit the selected objects, and a tick box per plugin to see the cell
/// without it and the plugins that need it, then with it again. Nothing is written to the game's files: a choice is an
/// order file under the Work Folder, and the scene is rebuilt on it.</summary>
internal static class LoadOrderPanel
{
    static TextBlock? _title;
    static TextBlock Title => _title ??= new() { FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(6, 6, 6, 0) };
    static TextBlock? _said;
    static TextBlock Said => _said ??= new() { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(6, 2, 6, 2), Foreground = Theme.Brush(Theme.Dim) };
    static TextBox? _find;
    static TextBox Find => _find ??= new() { MinWidth = 120, ToolTip = "list only plugins whose name holds this (any case)" };
    static CheckBox? _onlyediting;
    static CheckBox OnlyEditing => _onlyediting ??= new() { Content = "only plugins that edit the selection", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0, 0, 0) };
    static Button? _allback;
    static Button AllBack => _allback ??= new() { Content = "Switch all back on", Margin = new Thickness(6, 2, 6, 2), HorizontalAlignment = HorizontalAlignment.Left, IsEnabled = false,
                                             ToolTip = "show the scene on the whole load order again" };
    static ListBox? _list;
    static ListBox List => _list ??= new() { BorderThickness = new Thickness(0), Margin = new Thickness(2, 2, 2, 2) };
    // the controls are made when first used, by Build on the window's thread: a static field made at the class's first
    // touch would belong to whatever thread touched it first (a selection's mark update)
    static UIElement? _built;

    static SceneRequest? _base;                     // the request the scene was asked with, on its whole load order
    static IReadOnlyList<OrderEntry> _entries = Array.Empty<OrderEntry>();
    static readonly HashSet<string> Off = new(StringComparer.OrdinalIgnoreCase);
    // plugins switched off for the selected object only: the object is drawn as the load order without them has it,
    // the cell is not built again (Piranha 2026-11-23: quick A/B on one object)
    static readonly HashSet<string> ObjectOff = new(StringComparer.OrdinalIgnoreCase);
    static IReadOnlyList<string> _objectAlsoOff = Array.Empty<string>();
    static string? _objectKey;                      // the object ObjectOff is for
    static int _versionGeneration;

    /// <summary>Forget the one-object switches and draw that object as the scene has it again (a new scene, another
    /// object, an edit of it, or a rebuild of the cell).</summary>
    internal static void ForgetObjectSwitches()
    {
        if (ObjectOff.Count == 0 && _objectKey is null) return;
        ObjectOff.Clear(); _objectAlsoOff = Array.Empty<string>(); _objectKey = null; ++_versionGeneration;
        Shell.Current?.ClearVersion();
        if (_built is not null) { SayOrder(); Fill(); }
    }
    static IReadOnlyList<string> _alsoOff = Array.Empty<string>();
    static IReadOnlyDictionary<string, string> _patchLike = new Dictionary<string, string>();
    static string? _variantKey;                     // the key of the order the panel asked for, while one is asked for
    static bool _asking, _filling;
    static string? _selected, _partner;
    static IReadOnlyDictionary<string, string> _edits = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    static int _generation, _editGeneration;

    /// <summary>The plugins listed, in order; the state tools read them.</summary>
    internal static IReadOnlyList<OrderEntry> Entries => _entries;
    internal static IReadOnlyCollection<string> SwitchedOff => Off;
    internal static IReadOnlyCollection<string> SwitchedOffForObject => ObjectOff;
    internal static IReadOnlyList<string> AlsoOff => _alsoOff;
    internal static LoadOrderSpec? BaseOrder => _base?.Order;

    internal static UIElement Build()
    {
        if (_built is not null) return _built;
        var bar = new DockPanel { Margin = new Thickness(6, 2, 6, 2) };
        bar.Children.Add(new TextBlock { Text = "Find ", VerticalAlignment = VerticalAlignment.Center });
        DockPanel.SetDock(OnlyEditing, Dock.Right);
        bar.Children.Add(OnlyEditing);
        bar.Children.Add(Find);
        Find.TextChanged += (_, _) => Fill();
        OnlyEditing.Click += (_, _) => Fill();
        AllBack.Click += (_, _) => { Off.Clear(); Ask(); };
        VirtualizingPanel.SetIsVirtualizing(List, true);
        ScrollViewer.SetHorizontalScrollBarVisibility(List, ScrollBarVisibility.Disabled);   // names wrap, never clip
        Title.Text = "Load order";
        Said.Text = "Open a row or a cell to list the load order its scene is built on. A tag marks the plugins that edit the selected object (cyan), the clash's other object (magenta) or both.";
        var panel = new DockPanel();
        // what the panel says (how to use it, what the scene is built on) is the title's tooltip, not a line of grey text
        Title.SetBinding(FrameworkElement.ToolTipProperty, new System.Windows.Data.Binding(nameof(TextBlock.Text)) { Source = Said });
        foreach (var top in new UIElement[] { Title, bar, AllBack }) { DockPanel.SetDock(top, Dock.Top); panel.Children.Add(top); }
        panel.Children.Add(List);
        SceneDirector.Requested += Requested;
        return _built = panel;
    }

    /// <summary>Every scene request passes here first: one the panel did not make is a new scene, whose whole load order
    /// the panel lists and whose switched-off plugins are forgotten.</summary>
    static void Requested(SceneRequest req)
    {
        if (_asking) return;
        ForgetObjectSwitches();
        bool sameOrder = _base is not null && _base.Order.Key == req.Order.Key;
        _base = req; _variantKey = null;
        if (Off.Count > 0 || !sameOrder) { Off.Clear(); _alsoOff = Array.Empty<string>(); _patchLike = new Dictionary<string, string>(); }
        if (sameOrder && _entries.Count > 0) { SayOrder(); Fill(); ReadEdits(); return; }
        _entries = Array.Empty<OrderEntry>();
        Title.Text = "Load order: " + req.Order.Label;
        Said.Text = "reading the load order";
        Fill();
        int gen = ++_generation;
        var ui = Dispatcher.CurrentDispatcher;
        var order = req.Order;
        Task.Run(() => LoadOrderRules.Entries(order)).ContinueWith(t => ui.BeginInvoke(() =>
        {
            if (gen != _generation) return;
            if (t.IsFaulted) { Said.Text = $"the load order could not be listed: {t.Exception!.GetBaseException().Message}"; Shell.SetBanner(Said.Text); return; }
            _entries = t.Result;
            SayOrder(); Fill(); ReadEdits();
        }), TaskScheduler.Default);
    }

    static void SayOrder()
    {
        if (_base is not { } b) return;
        Title.Text = $"Load order: {b.Order.Label}";
        int lights = _entries.Count(e => e.Index.StartsWith("FE:", StringComparison.Ordinal));
        string what = $"{_entries.Count:N0} plugins" + (lights > 0 ? $" ({lights:N0} light)" : "");
        Said.Text = (Off.Count == 0
            ? $"The scene is built on all {what}. Untick a plugin to see the cell without it and the plugins that need it as a master."
            : $"The scene is built on {_entries.Count - Off.Count - _alsoOff.Count:N0} of the {what}: without {Names(Off.ToList())}"
              + (_alsoOff.Count > 0 ? $" and the {_alsoOff.Count} plugin(s) that need it as a master ({Names(_alsoOff)})" : "")
              + (_patchLike.Count > 0 ? $". {_patchLike.Count} plugin(s) that look like patches for it but do not list it as a master stay loaded, in bold red" : "")
              + ". Each switch rebuilds the scene, about 20 s.")
            + (ObjectOff.Count > 0 ? $" The selected object is drawn as it is without {Names(ObjectOff.ToList())}; the rest of the cell is unchanged." : "")
            + " Highest priority at the bottom.";
        AllBack.IsEnabled = Off.Count > 0;
    }

    static string Names(IReadOnlyList<string> n) => n.Count <= 3 ? string.Join(", ", n) : string.Join(", ", n.Take(3)) + $" and {n.Count - 3} more";

    /// <summary>The objects whose plugins are marked: the scene's selection and the open clash's other object.</summary>
    internal static void Selection(string? selected, string? partner)
    {
        if (string.Equals(selected, _selected, StringComparison.OrdinalIgnoreCase) && string.Equals(partner, _partner, StringComparison.OrdinalIgnoreCase)) return;
        bool moved = !string.Equals(selected, _selected, StringComparison.OrdinalIgnoreCase);
        _selected = selected; _partner = partner;
        // another object selected: the last one is drawn as the scene has it again, and its switches are forgotten
        if (moved) ForgetObjectSwitches();
        ReadEdits();
    }

    /// <summary>Which plugins edit the selected objects, read from the whole load order's record index.</summary>
    static void ReadEdits()
    {
        if (_base is not { } b) return;
        string? sel = _selected ?? b.LandOn, other = _partner;
        int gen = ++_editGeneration;
        var ui = Dispatcher.CurrentDispatcher;
        LoadOrderIndexes.For(b.Order).ContinueWith(t => ui.BeginInvoke(() =>
        {
            if (gen != _editGeneration || t.IsFaulted) return;
            var edits = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var p in (other is null ? null : t.Result.TouchingPlugins(other)) ?? Array.Empty<string>()) edits[p] = "other";
            foreach (var p in (sel is null ? null : t.Result.TouchingPlugins(sel)) ?? Array.Empty<string>()) edits[p] = edits.ContainsKey(p) ? "both" : "selected";
            _edits = edits;
            Fill();
        }), TaskScheduler.Default);
    }

    /// <summary>The plugins that edit the selected object ("selected"), the clash's other object ("other") or both.</summary>
    internal static IReadOnlyDictionary<string, string> Edits => _edits;

    static void Fill()
    {
        bool was = _filling; _filling = true;
        try
        {
            List.Items.Clear();
            var text = Find.Text.Trim();
            var also = new HashSet<string>(_alsoOff, StringComparer.OrdinalIgnoreCase);
            UIElement? last = null;
            foreach (var e in _entries)
            {
                _edits.TryGetValue(e.Name, out var edit);
                if (OnlyEditing.IsChecked == true && edit is null) continue;
                if (text.Length > 0 && !e.Name.Contains(text, StringComparison.OrdinalIgnoreCase)) continue;
                var row = Row(e, edit, also.Contains(e.Name));
                List.Items.Add(row);
                if (edit is not null || Off.Contains(e.Name) || also.Contains(e.Name) || _patchLike.ContainsKey(e.Name)) last = row;
            }
            // the last plugin that edits the selection or is switched off stays in view (the highest priority one)
            if (last is not null) List.ScrollIntoView(last);
        }
        finally { _filling = was; }
    }

    static UIElement Row(OrderEntry e, string? edit, bool goesWith)
    {
        bool objectOff = ObjectOff.Contains(e.Name) || _objectAlsoOff.Contains(e.Name, StringComparer.OrdinalIgnoreCase);
        var box = new CheckBox { IsChecked = !Off.Contains(e.Name) && !goesWith && !objectOff, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 6, 0),
                                 IsEnabled = !goesWith && !_objectAlsoOff.Contains(e.Name, StringComparer.OrdinalIgnoreCase),
                                 ToolTip = goesWith ? "off because it needs a plugin you switched off as a master"
                                         : "untick: with an object selected, see that object without this plugin (the cell stays as it is); with nothing selected, "
                                           + "build the cell again without it and the plugins that need it (asks first). Ctrl+click builds the cell again without asking." };
        string name = e.Name;
        box.Click += (_, _) =>
        {
            if (_filling) return;
            bool on = box.IsChecked == true;
            bool ctrl = (Keyboard.Modifiers & ModifierKeys.Control) != 0;
            // a plugin the cell was built without goes back the same way; else, with an object selected, the object alone
            if (!ctrl && !Off.Contains(name) && _selected is { } sel && Shell.Current is not null)
            {
                try { SwitchForObject(sel, name, on); }
                catch (InvalidOperationException ex) { Shell.SetBanner(ex.Message); Fill(); }   // Fill puts the box back as it was
                return;
            }
            if (!ctrl && !ConfirmRebuild(name, on)) { _filling = true; try { box.IsChecked = !on; } finally { _filling = false; } return; }
            if (on) Off.Remove(name); else Off.Add(name);
            Ask();
        };
        var sp = new DockPanel { LastChildFill = true };
        sp.Children.Add(box);
        var index = new TextBlock { Text = e.Index, Width = 48, Foreground = Theme.Brush(Theme.Dim), VerticalAlignment = VerticalAlignment.Center, FontFamily = new FontFamily("Consolas") };
        sp.Children.Add(index);
        if (edit is not null)
        {
            var tag = new Border
            {
                Background = new SolidColorBrush(edit == "other" ? Color.FromRgb(0x8A, 0x1F, 0x8A) : edit == "both" ? Color.FromRgb(0x55, 0x55, 0xAA) : Color.FromRgb(0x10, 0x70, 0x78)),
                CornerRadius = new CornerRadius(3), Padding = new Thickness(4, 0, 4, 0), Margin = new Thickness(6, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center,
                Child = new TextBlock { Text = edit == "other" ? "other object" : edit == "both" ? "both" : "selected", FontSize = 11, Foreground = Brushes.White },
                ToolTip = edit == "other" ? "this plugin has a version of the clash's other object (magenta in the view)" : edit == "both" ? "this plugin has a version of both objects" : "this plugin has a version of the selected object (cyan in the view)",
            };
            DockPanel.SetDock(tag, Dock.Right);
            sp.Children.Add(tag);
        }
        var label = new TextBlock { Text = e.Name, VerticalAlignment = VerticalAlignment.Center, TextWrapping = TextWrapping.Wrap, ToolTip = e.Name };
        if (edit is not null) label.FontWeight = FontWeights.SemiBold;
        if (Off.Contains(e.Name) || goesWith) { label.Foreground = Theme.Brush(Theme.Dim); label.TextDecorations = TextDecorations.Strikethrough; }
        else if (objectOff)
        {
            label.Foreground = Theme.Brush(Theme.Dim); label.TextDecorations = TextDecorations.Strikethrough; label.FontStyle = FontStyles.Italic;
            label.ToolTip = "switched off for the selected object only; the rest of the cell keeps it";
        }
        else if (_patchLike.TryGetValue(e.Name, out var why))
        {
            label.FontWeight = FontWeights.Bold; label.Foreground = new SolidColorBrush(Color.FromRgb(0xFF, 0x55, 0x55)); label.ToolTip = why;
            box.ToolTip = why;
        }
        sp.Children.Add(label);
        return sp;
    }

    /// <summary>With nothing selected, a switch builds the whole cell again (about 20 s): asked first, so a stray click does
    /// not start it. Ctrl+click skips the question.</summary>
    static bool ConfirmRebuild(string plugin, bool on)
    {
        if (_base is null) return true;
        string text = on
            ? $"Build the cell again with {plugin} switched back on?"
            : $"Build the cell again without {plugin}" + (LoadOrderRules.Without(_entries, new HashSet<string>(Off.Append(plugin), StringComparer.OrdinalIgnoreCase)).AlsoOff is { Count: > 0 } also
                ? $" and the {also.Count} plugin(s) that need it as a master" : "") + "?";
        text += Environment.NewLine + Environment.NewLine + (_selected is null
                  ? "Nothing is selected, so the whole cell is built again on that load order (about 20 seconds). To compare one object instead, select it first: the plugin is then switched for that object only."
                  : "The cell was built without this plugin, so it is switched back for the whole cell, which builds it again (about 20 seconds).") + Environment.NewLine
              + "Ctrl+click a plugin to build the cell again without this question.";
        return MessageBox.Show(text, "Build the cell again?", MessageBoxButton.OKCancel, MessageBoxImage.Question, MessageBoxResult.OK) == MessageBoxResult.OK;
    }

    /// <summary>Switch a plugin off or on for the selected object only: the object is drawn as the load order without the
    /// object's switched-off plugins (and the plugins that need them as a master) has it; the cell is not built again.</summary>
    internal static string SwitchForObject(string selected, string plugin, bool on)
    {
        if (_base is not { } b) throw new InvalidOperationException("no scene's load order is listed yet; open a row or a cell first.");
        if (Shell.Current is not { } h) throw new InvalidOperationException("no scene is shown yet; wait for the cell to finish loading and try again.");
        if (!Mutagen.Bethesda.Plugins.FormKey.TryFactory(selected, out var key)) throw new InvalidOperationException($"'{selected}' is not a FormKey; name a drawn object as XXXXXX:Plugin.esp.");
        if (_entries.All(e => !e.Name.Equals(plugin, StringComparison.OrdinalIgnoreCase)))
            throw new InvalidOperationException($"'{plugin}' is not a plugin of {b.Order.Label}; name one the panel lists.");
        h.CheckVersionable(key);                     // refused in one sentence before anything changes
        if (!string.Equals(_objectKey, selected, StringComparison.OrdinalIgnoreCase)) ForgetObjectSwitches();
        _objectKey = selected;
        var before = ObjectOff.ToList();
        if (on) ObjectOff.Remove(plugin); else ObjectOff.Add(plugin);
        int gen = ++_versionGeneration;
        if (ObjectOff.Count == 0)
        {
            ForgetObjectSwitches();
            var back = $"{selected} is drawn with every plugin again.";
            Shell.SetBanner(back);
            return back;
        }
        var (_, also) = LoadOrderRules.Without(_entries, ObjectOff);
        _objectAlsoOff = also;
        var without = new HashSet<string>(ObjectOff.Concat(also).Concat(Off).Concat(_alsoOff), StringComparer.OrdinalIgnoreCase);
        string words = Names(ObjectOff.ToList()) + (also.Count > 0 ? $" (and the {also.Count} plugin(s) that need it)" : "");
        SayOrder(); Fill();
        var ui = Dispatcher.CurrentDispatcher;
        LoadOrderIndexes.For(b.Order).ContinueWith(t => t.IsCompletedSuccessfully ? t.Result.VersionWithout(selected, without) : throw t.Exception!.GetBaseException(), TaskScheduler.Default)
            .ContinueWith(t => ui.BeginInvoke(() =>
            {
                if (gen != _versionGeneration || !ReferenceEquals(Shell.Current, h)) return;
                try
                {
                    if (t.IsFaulted) throw new InvalidOperationException($"the version of {selected} without {words} could not be read: {t.Exception!.GetBaseException().Message}");
                    Shell.SetBanner(h.ShowVersion(key, t.Result, words));
                }
                catch (InvalidOperationException ex)
                {
                    // refused: the switch did not happen, so the panel goes back to what the view shows
                    ObjectOff.Clear(); foreach (var p in before) ObjectOff.Add(p);
                    _objectAlsoOff = ObjectOff.Count > 0 ? LoadOrderRules.Without(_entries, ObjectOff).AlsoOff : Array.Empty<string>();
                    if (ObjectOff.Count == 0) ForgetObjectSwitches(); else { SayOrder(); Fill(); }
                    Shell.SetBanner(ex.Message);
                }
            }), TaskScheduler.Default);
        var said = $"reading {selected} as the load order without {words} has it";
        Shell.SetBanner(said);
        return said;
    }

    /// <summary>Ask for the scene on the whole load order without the switched-off plugins; the whole load order again when none is.</summary>
    static void Ask()
    {
        if (_base is not { } b) return;
        ForgetObjectSwitches();                     // the cell is built again: no object stays switched on its own
        try
        {
            SceneRequest req;
            _patchLike = new Dictionary<string, string>();
            if (Off.Count == 0) { _alsoOff = Array.Empty<string>(); _variantKey = null; req = b with { Reload = false }; }
            else
            {
                var (kept, also) = LoadOrderRules.Without(_entries, Off);
                _alsoOff = also;
                _patchLike = LoadOrderRules.LooksLikePatches(_entries, Off, also);
                var folder = Path.Combine(Program.RequestWork(), "load-order-without");
                var file = LoadOrderRules.WriteWithout(b.Order, kept, _entries.Where(e => Off.Contains(e.Name)).Select(e => e.Name).ToList(), also, folder);
                var order = b.Order with { OrderFile = file, Label = $"{b.Order.Label} without {Names(Off.ToList())}" + (also.Count > 0 ? $" and {also.Count} that need it" : "") };
                _variantKey = order.Key;
                req = b with { Order = order, Why = $"without {Names(Off.ToList())}" };
            }
            _asking = true;
            try { Shell.SetBanner(SceneDirector.Request(req)); }
            finally { _asking = false; }
        }
        catch (Exception ex) when (ex is InvalidOperationException or IOException or InvalidDataException)
        {
            Shell.SetBanner($"the scene without those plugins could not be asked for: {ex.Message}");
        }
        SayOrder(); Fill();
    }

    /// <summary>The panel's tick box, as the state tools work it: switch a plugin off or on (all: every plugin on).</summary>
    internal static string Switch(string? plugin, bool? on)
    {
        if (_base is null || _entries.Count == 0) throw new InvalidOperationException("no scene's load order is listed yet; open a row or a cell first.");
        if (plugin is null || on is null) throw new InvalidOperationException("name the plugin and say show true (on) or false (off).");
        if (plugin.Equals("all", StringComparison.OrdinalIgnoreCase)) { if (on != true) throw new InvalidOperationException("all can only be switched back on."); Off.Clear(); }
        else
        {
            var e = _entries.FirstOrDefault(x => x.Name.Equals(plugin, StringComparison.OrdinalIgnoreCase))
                    ?? throw new InvalidOperationException($"'{plugin}' is not a plugin of {_base.Order.Label}; name one the panel lists.");
            if (on == true) Off.Remove(e.Name); else Off.Add(e.Name);
        }
        Ask();
        return Said.Text;
    }

    /// <summary>The <c>loadOrder</c> part of the state.</summary>
    internal static object State() => new
    {
        order = _base?.Order.Label, orderFile = _base?.Order.OrderFile, plugins = _entries.Select(e => e.Name).ToList(),
        switchedOff = Off.ToList(), alsoOff = _alsoOff, switchedOffForObject = ObjectOff.ToList(), objectAlsoOff = _objectAlsoOff, looksLikePatches = _patchLike, shownOn = _variantKey is null ? "the whole load order" : "the load order without the plugins switched off",
        edits = _edits, said = Said.Text,
    };
}
