using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
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
    static readonly TextBlock Title = new() { FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(6, 6, 6, 0) };
    static readonly TextBlock Said = new() { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(6, 2, 6, 2), Foreground = Theme.Brush(Theme.Dim) };
    static readonly TextBox Find = new() { MinWidth = 120, ToolTip = "list only plugins whose name holds this (any case)" };
    static readonly CheckBox OnlyEditing = new() { Content = "only plugins that edit the selection", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0, 0, 0) };
    static readonly Button AllBack = new() { Content = "Switch all back on", Margin = new Thickness(6, 2, 6, 2), HorizontalAlignment = HorizontalAlignment.Left, IsEnabled = false,
                                             ToolTip = "show the scene on the whole load order again" };
    static readonly ListBox List = new() { BorderThickness = new Thickness(0), Margin = new Thickness(2, 2, 2, 2) };
    static UIElement? _built;

    static SceneRequest? _base;                     // the request the scene was asked with, on its whole load order
    static IReadOnlyList<OrderEntry> _entries = Array.Empty<OrderEntry>();
    static readonly HashSet<string> Off = new(StringComparer.OrdinalIgnoreCase);
    static IReadOnlyList<string> _alsoOff = Array.Empty<string>();
    static string? _variantKey;                     // the key of the order the panel asked for, while one is asked for
    static bool _asking, _filling;
    static string? _selected, _partner;
    static IReadOnlyDictionary<string, string> _edits = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    static int _generation, _editGeneration;

    /// <summary>The plugins listed, in order; the state tools read them.</summary>
    internal static IReadOnlyList<OrderEntry> Entries => _entries;
    internal static IReadOnlyCollection<string> SwitchedOff => Off;
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
        Title.Text = "Load order";
        Said.Text = "Open a row or a cell to list the load order its scene is built on.";
        var panel = new DockPanel();
        foreach (var top in new UIElement[] { Title, Said, bar, AllBack }) { DockPanel.SetDock(top, Dock.Top); panel.Children.Add(top); }
        panel.Children.Add(List);
        SceneDirector.Requested += Requested;
        return _built = panel;
    }

    /// <summary>Every scene request passes here first: one the panel did not make is a new scene, whose whole load order
    /// the panel lists and whose switched-off plugins are forgotten.</summary>
    static void Requested(SceneRequest req)
    {
        if (_asking) return;
        bool sameOrder = _base is not null && _base.Order.Key == req.Order.Key;
        _base = req; _variantKey = null;
        if (Off.Count > 0 || !sameOrder) { Off.Clear(); _alsoOff = Array.Empty<string>(); }
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
            if (t.IsFaulted) { Said.Text = $"the load order could not be listed: {t.Exception!.GetBaseException().Message}"; return; }
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
              + ". Patches made for it that do not list it as a master stay on. Each switch rebuilds the scene, about 20 s.")
            + " Highest priority at the bottom.";
        AllBack.IsEnabled = Off.Count > 0;
    }

    static string Names(IReadOnlyList<string> n) => n.Count <= 3 ? string.Join(", ", n) : string.Join(", ", n.Take(3)) + $" and {n.Count - 3} more";

    /// <summary>The objects whose plugins are marked: the scene's selection and the open clash's other object.</summary>
    internal static void Selection(string? selected, string? partner)
    {
        if (string.Equals(selected, _selected, StringComparison.OrdinalIgnoreCase) && string.Equals(partner, _partner, StringComparison.OrdinalIgnoreCase)) return;
        _selected = selected; _partner = partner;
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
                if (edit is not null || Off.Contains(e.Name) || also.Contains(e.Name)) last = row;
            }
            // the last plugin that edits the selection or is switched off stays in view (the highest priority one)
            if (last is not null) List.ScrollIntoView(last);
        }
        finally { _filling = was; }
    }

    static UIElement Row(OrderEntry e, string? edit, bool goesWith)
    {
        var box = new CheckBox { IsChecked = !Off.Contains(e.Name) && !goesWith, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 6, 0),
                                 IsEnabled = !goesWith,
                                 ToolTip = goesWith ? "off because it needs a plugin you switched off as a master" : "untick to see the scene without this plugin and the plugins that need it" };
        string name = e.Name;
        box.Click += (_, _) =>
        {
            if (_filling) return;
            if (box.IsChecked == true) Off.Remove(name); else Off.Add(name);
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
                Child = new TextBlock { Text = edit == "other" ? "edits the other object" : edit == "both" ? "edits both objects" : "edits the selected object", FontSize = 11, Foreground = Brushes.White },
                ToolTip = edit == "other" ? "this plugin has a version of the clash's other object (magenta in the view)" : edit == "both" ? "this plugin has a version of both objects" : "this plugin has a version of the selected object (cyan in the view)",
            };
            DockPanel.SetDock(tag, Dock.Right);
            sp.Children.Add(tag);
        }
        var label = new TextBlock { Text = e.Name, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis, ToolTip = e.Name };
        if (edit is not null) label.FontWeight = FontWeights.SemiBold;
        if (Off.Contains(e.Name) || goesWith) { label.Foreground = Theme.Brush(Theme.Dim); label.TextDecorations = TextDecorations.Strikethrough; }
        sp.Children.Add(label);
        return sp;
    }

    /// <summary>Ask for the scene on the whole load order without the switched-off plugins; the whole load order again when none is.</summary>
    static void Ask()
    {
        if (_base is not { } b) return;
        try
        {
            SceneRequest req;
            if (Off.Count == 0) { _alsoOff = Array.Empty<string>(); _variantKey = null; req = b with { Reload = false }; }
            else
            {
                var (kept, also) = LoadOrderRules.Without(_entries, Off);
                _alsoOff = also;
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
        switchedOff = Off.ToList(), alsoOff = _alsoOff, shownOn = _variantKey is null ? "the whole load order" : "the load order without the plugins switched off",
        edits = _edits, said = Said.Text,
    };
}
