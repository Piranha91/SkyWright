using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace Workbench;

/// <summary>The Open control above the tabs: one box and two browse buttons for anything the window opens. What a path
/// is, is decided by <see cref="InputReaders"/>; this control only hands it over and shows the answer on the banner.
/// The <c>open</c> verb of <c>workbench_act</c> calls the same <see cref="Open"/>.</summary>
internal static class OpenBar
{
    internal const string RecentFile = "workbench-recent.json";
    const int MaxRecent = 12;

    static readonly TextBox PathBox = new() { MinWidth = 260, Margin = new Thickness(4, 2, 4, 2), VerticalContentAlignment = VerticalAlignment.Center,
        ToolTip = "an evaluation results folder, an evaluation bundle, an MO2 instance or profile folder, or a game or Data folder" };
    static readonly ComboBox Recent = new() { Width = 26, Margin = new Thickness(0, 2, 4, 2), ToolTip = "opened before" };

    /// <summary>Where the recent list is kept: beside the settings file.</summary>
    internal static string RecentPath => Path.Combine(Path.GetDirectoryName(Path.GetFullPath(LauncherSettings.DefaultPath))!, RecentFile);

    internal static UIElement Build()
    {
        var folder = new Button { Content = "Open folder…", Margin = new Thickness(0, 2, 4, 2), Padding = new Thickness(8, 2, 8, 2) };
        var file = new Button { Content = "Open file…", Margin = new Thickness(0, 2, 4, 2), Padding = new Thickness(8, 2, 8, 2) };
        var go = new Button { Content = "Open", Margin = new Thickness(0, 2, 4, 2), Padding = new Thickness(8, 2, 8, 2), FontWeight = FontWeights.SemiBold };
        folder.Click += (_, _) =>
        {
            var d = new Microsoft.Win32.OpenFolderDialog { InitialDirectory = Directory.Exists(PathBox.Text) ? PathBox.Text : null, Title = "Open a folder" };
            if (d.ShowDialog() == true) { PathBox.Text = d.FolderName; Do(d.FolderName); }
        };
        file.Click += (_, _) =>
        {
            var d = new Microsoft.Win32.OpenFileDialog { Title = "Open a file", CheckFileExists = true };
            if (d.ShowDialog() == true) { PathBox.Text = d.FileName; Do(d.FileName); }
        };
        go.Click += (_, _) => Do(PathBox.Text);
        PathBox.KeyDown += (_, e) => { if (e.Key == Key.Enter) { e.Handled = true; Do(PathBox.Text); } };
        Recent.DropDownOpened += (_, _) => Recent.ItemsSource = ReadRecent(RecentPath);
        Recent.SelectionChanged += (_, _) => { if (Recent.SelectedItem is string p) { Recent.SelectedItem = null; PathBox.Text = p; Do(p); } };
        var bar = new DockPanel { LastChildFill = true, Background = Theme.Brush(Theme.Panel) };
        var label = new TextBlock { Text = "Open", Margin = new Thickness(8, 2, 4, 2), VerticalAlignment = VerticalAlignment.Center, FontWeight = FontWeights.SemiBold };
        DockPanel.SetDock(label, Dock.Left);
        foreach (var b in new UIElement[] { go, file, folder, Recent }) DockPanel.SetDock(b, Dock.Right);
        bar.Children.Add(label);
        bar.Children.Add(go); bar.Children.Add(file); bar.Children.Add(folder); bar.Children.Add(Recent);
        bar.Children.Add(PathBox);
        return bar;
    }

    static void Do(string? path)
    {
        try { Shell.SetBanner(Open(path)); }
        catch (InvalidOperationException ex) { Shell.SetBanner(ex.Message); }
    }

    /// <summary>Open a path in the window and remember it; returns what was opened, in one sentence.</summary>
    internal static string Open(string? path)
    {
        var said = InputReaders.Open(path);
        var full = Path.GetFullPath(path!.Trim().Trim('"'));
        PathBox.Text = full;
        Remember(RecentPath, full);
        return said;
    }

    /// <summary>What is opened when the window starts with no arguments: the path WB_OPEN names, else the game Data
    /// folder when Mod Organizer 2 started the workbench (<see cref="StartInput"/>). Nothing otherwise: the launcher.</summary>
    internal static void OpenAtStart()
    {
        string? path = Environment.GetEnvironmentVariable("WB_OPEN") is { Length: > 0 } p ? p : StartInput?.Invoke();
        if (path is null) return;
        try { Shell.SetBanner(Open(path)); }
        catch (InvalidOperationException ex) { Shell.SetBanner(ex.Message); }
    }

    /// <summary>The input a start with no arguments opens by itself, or null (set by the reader that knows: the Data folder reader).</summary>
    internal static Func<string?>? StartInput;

    internal static IReadOnlyList<string> ReadRecent(string file)
    {
        try { return File.Exists(file) ? JsonSerializer.Deserialize<List<string>>(File.ReadAllText(file)) ?? new() : new List<string>(); }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException) { return new List<string>(); }
    }

    /// <summary>Put a path at the head of the recent list; a folder that cannot be written is said in the log, and the open goes on.</summary>
    internal static void Remember(string file, string path)
    {
        var list = ReadRecent(file).Where(x => !string.Equals(x, path, StringComparison.OrdinalIgnoreCase)).Prepend(path).Take(MaxRecent).ToList();
        try { File.WriteAllText(file, JsonSerializer.Serialize(list, new JsonSerializerOptions { WriteIndented = true })); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { WorkbenchHost.Say($"the recent list at '{file}' could not be written ({ex.Message.TrimEnd('.')})."); }
    }
}
