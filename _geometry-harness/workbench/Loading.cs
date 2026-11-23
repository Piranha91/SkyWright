using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;

namespace Workbench;

/// <summary>The loading strip under the Open bar: one bar for whatever the window is loading now (a scene, a profile's
/// world, an evaluation's rows, map tiles, a run). Each load sets its own line by key and clears it when done; the strip
/// shows the newest, says how many others are under way, and hides when none is. A fraction is shown only when the
/// load knows its total; otherwise the bar moves without a number.</summary>
internal static class Loading
{
    sealed record Job(string Key, string What, double? Fraction, long Order);

    static readonly Dictionary<string, Job> Jobs = new(StringComparer.Ordinal);
    static long _order;
    // the strip's controls are made with the strip, on the window's thread; a load may report from any thread
    static TextBlock What = null!, Percent = null!;
    static ProgressBar Bar = null!;
    static Border? _strip;

    /// <summary>The strip, built once.</summary>
    internal static UIElement Strip()
    {
        if (_strip is not null) return _strip;
        What = new TextBlock { VerticalAlignment = VerticalAlignment.Center, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(8, 0, 8, 0) };
        Percent = new TextBlock { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 0), MinWidth = 36, TextAlignment = TextAlignment.Right };
        Bar = new ProgressBar { Height = 6, Minimum = 0, Maximum = 1, Width = 220, VerticalAlignment = VerticalAlignment.Center };
        Percent.Foreground = What.Foreground = Theme.Brush(Theme.Dim);
        var row = new DockPanel { LastChildFill = true };
        DockPanel.SetDock(Bar, Dock.Left); DockPanel.SetDock(Percent, Dock.Left);
        row.Children.Add(Bar); row.Children.Add(Percent); row.Children.Add(What);
        _strip = new Border { Child = row, Padding = new Thickness(8, 4, 8, 4), Background = Theme.Brush(Theme.Panel), Visibility = Visibility.Collapsed };
        Show();
        return _strip;
    }

    /// <summary>Show or update a load. <paramref name="fraction"/> is 0..1 when the total is known, null when it is not.</summary>
    internal static void Set(string key, string what, double? fraction = null)
    {
        lock (Jobs)
        {
            long at = Jobs.TryGetValue(key, out var had) ? had.Order : ++_order;
            Jobs[key] = new Job(key, what, fraction is { } f ? Math.Clamp(f, 0, 1) : null, at);
        }
        OnUi(Show);
    }

    /// <summary>The load is finished (or failed: its sentence is said elsewhere).</summary>
    internal static void Clear(string key) { bool had; lock (Jobs) had = Jobs.Remove(key); if (had) OnUi(Show); }

    /// <summary>The loads under way, newest first, for the state tools.</summary>
    internal static IReadOnlyList<object> State()
    {
        lock (Jobs) return Jobs.Values.OrderByDescending(j => j.Order).Select(j => (object)new { key = j.Key, what = j.What, fraction = j.Fraction }).ToList();
    }

    static void Show()
    {
        if (_strip is null) return;
        List<Job> jobs; lock (Jobs) jobs = Jobs.Values.OrderByDescending(j => j.Order).ToList();
        var top = jobs.FirstOrDefault();
        if (top is null) { _strip.Visibility = Visibility.Collapsed; Bar.IsIndeterminate = false; return; }
        _strip.Visibility = Visibility.Visible;
        int others = jobs.Count - 1;
        What.Text = top.What + (others > 0 ? $"   (and {others} more)" : "");
        _strip.ToolTip = string.Join(Environment.NewLine, jobs.Select(j => j.What + (j.Fraction is { } f ? $" — {f:P0}" : "")));
        Bar.IsIndeterminate = top.Fraction is null;
        if (top.Fraction is { } v) Bar.Value = v;
        Percent.Text = top.Fraction is { } p ? $"{p:P0}" : "";
        Percent.Visibility = top.Fraction is null ? Visibility.Collapsed : Visibility.Visible;
    }

    static void OnUi(Action act)
    {
        if (_strip?.Dispatcher is not { } d) return;      // no strip yet: it shows what is under way when it is built
        if (d.CheckAccess()) act();
        else d.BeginInvoke(act, DispatcherPriority.Background);
    }
}
