using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;

namespace Workbench;

/// <summary>The Load choices, the one way into the window: the Start tab lists them as buttons, and the Load button on
/// the top bar opens the same list from any tab. Each choice ends in <see cref="OpenBar.Open"/> (the readers decide what
/// a path is); an MO2 profile also opens in the Start tab's mod list, so the mods can be checked.</summary>
internal static class LoadMenu
{
    internal sealed record Choice(string Label, string Tip, Action Act);

    /// <summary>The choices, in the order shown.</summary>
    internal static IReadOnlyList<Choice> Choices { get; } = new Choice[]
    {
        new("Current Data Folder", "The game's Data folder as it is now: as Mod Organizer 2 shows it when MO2 started SkyWright, else the installed game's.", CurrentDataFolder),
        new("MO2 Profile", "A Mod Organizer 2 profile: its mods can then be checked against its load order, and its world explored.", Mo2Profile),
        new("Previous Results", "The folder of an earlier check: a run's results or an evaluation's results.", PreviousResults),
        new("Clash List", "A list of clashes written by an AI, a script or by hand (a .clashes.json file; docs/clash-list-format.md describes it).", ClashListFile),
        new("Other folder or file", "Any folder or file SkyWright reads; it works out what it is.", OtherFolder),
    };

    static void Say(string s) { Shell.SetBanner(s); LauncherView.Say(s); }

    static void Try(Func<string> act)
    {
        try { Say(act()); }
        catch (InvalidOperationException ex) { Say(ex.Message); }
    }

    static void CurrentDataFolder()
    {
        if (DataFolderOrder.CurrentGame() is { } game) { Try(() => OpenBar.Open(game)); return; }
        var d = new Microsoft.Win32.OpenFolderDialog { Title = "No installed Skyrim was found: choose the game folder or its Data folder" };
        if (d.ShowDialog() != true) return;
        Try(() => OpenBar.Open(d.FolderName));
    }

    static void Mo2Profile()
    {
        var d = new Microsoft.Win32.OpenFolderDialog { Title = "Choose an MO2 profile folder (profiles\\<name>), or an MO2 instance folder for its selected profile",
                                                       InitialDirectory = Shell.Launcher.Instance is { } i && Directory.Exists(Path.Combine(i, "profiles")) ? Path.Combine(i, "profiles") : null };
        if (d.ShowDialog() == true) _ = OpenAsync(d.FolderName);
    }

    static void PreviousResults()
    {
        var d = new Microsoft.Win32.OpenFolderDialog { Title = "Choose the folder of an earlier check's results" };
        if (d.ShowDialog() == true) _ = OpenAsync(d.FolderName);
    }

    static void ClashListFile()
    {
        var d = new Microsoft.Win32.OpenFileDialog { Title = "Choose a clash list", Filter = $"Clash lists (*{ClashList.Suffix})|*{ClashList.Suffix}|All files (*.*)|*.*", CheckFileExists = true };
        if (d.ShowDialog() == true) _ = OpenAsync(d.FileName);
    }

    static void OtherFolder()
    {
        var d = new Microsoft.Win32.OpenFolderDialog { Title = "Choose a folder SkyWright reads" };
        if (d.ShowDialog() == true) _ = OpenAsync(d.FolderName);
    }

    /// <summary>Open a path as the Load choices do: a profile goes to the Start tab's mod list as well as to its world.</summary>
    internal static async Task OpenAsync(string path)
    {
        if (ProfileReader.Parse(path) is { } p)
        {
            try { Say(await Shell.Launcher.OpenProfileAsync("free", p.Instance, p.Profile, null)); LauncherView.Fill(); }
            catch (InvalidOperationException ex) { Say(ex.Message); return; }
        }
        Try(() => OpenBar.Open(path));
    }

    /// <summary>A choice as a button of the given width.</summary>
    internal static Button Button(Choice c, double width, Action? after = null)
    {
        var b = new Button { Content = c.Label, ToolTip = c.Tip, Width = width, HorizontalAlignment = HorizontalAlignment.Left, HorizontalContentAlignment = HorizontalAlignment.Left,
                             Padding = new Thickness(12, 6, 12, 6), Margin = new Thickness(0, 0, 0, 6) };
        AutomationPropertiesName(b, c.Label);
        b.Click += (_, _) => { after?.Invoke(); c.Act(); };
        return b;
    }

    static void AutomationPropertiesName(UIElement e, string name) => System.Windows.Automation.AutomationProperties.SetName(e, name);

    /// <summary>The top bar's Load button: the same choices and the recent list, in a drop-down.</summary>
    internal static UIElement TopButton()
    {
        var open = new ToggleButton { Content = "Load ▾", Padding = new Thickness(10, 2, 10, 2), Margin = new Thickness(4, 2, 4, 2), FontWeight = FontWeights.SemiBold,
                                      ToolTip = "load a Data folder, an MO2 profile or earlier results" };
        var list = new StackPanel { Margin = new Thickness(8) };
        var pop = new Popup { PlacementTarget = open, Placement = PlacementMode.Bottom, StaysOpen = false, AllowsTransparency = true,
                              Child = new Border { Background = Theme.Brush(Theme.Panel), BorderBrush = Theme.Brush(Theme.Line), BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(4), Child = list } };
        void Fill()
        {
            list.Children.Clear();
            foreach (var c in Choices) list.Children.Add(Button(c, 240, () => pop.IsOpen = false));
            var recent = OpenBar.ReadRecent(OpenBar.RecentPath).Take(6).ToList();
            if (recent.Count == 0) return;
            list.Children.Add(new TextBlock { Text = "Recent", Foreground = Theme.Brush(Theme.Dim), Margin = new Thickness(2, 6, 0, 4) });
            foreach (var r in recent) list.Children.Add(RecentButton(r, 240, () => pop.IsOpen = false));
        }
        open.Checked += (_, _) => { Fill(); pop.IsOpen = true; };
        pop.Closed += (_, _) => open.IsChecked = false;
        pop.KeyDown += (_, e) => { if (e.Key == Key.Escape) pop.IsOpen = false; };
        var host = new Grid();
        host.Children.Add(open); host.Children.Add(pop);
        return host;
    }

    /// <summary>A recent path as a button: its last folder name, the full path in the tooltip.</summary>
    internal static Button RecentButton(string path, double width, Action? after = null)
    {
        var name = Path.GetFileName(path.TrimEnd('\\', '/'));
        var b = new Button { Content = new TextBlock { Text = name.Length > 0 ? name : path, TextTrimming = TextTrimming.CharacterEllipsis }, ToolTip = path, Width = width,
                             HorizontalAlignment = HorizontalAlignment.Left, HorizontalContentAlignment = HorizontalAlignment.Left, Padding = new Thickness(12, 4, 12, 4), Margin = new Thickness(0, 0, 0, 4) };
        b.Click += (_, _) => { after?.Invoke(); _ = OpenAsync(path); };
        return b;
    }
}
