using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;

namespace Workbench;

/// <summary>The Start tab (DESIGN section 1): the launcher's controls. Each one calls the same <see cref="Launcher"/>
/// method the matching <c>workbench_act</c> verb calls; a refusal shows its one sentence in the status line.</summary>
internal static class LauncherView
{
    static readonly ComboBox Mode = new() { ItemsSource = new[] { "free", "housecarl" }, SelectedIndex = 0, Width = 110 };
    static readonly TextBox Instance = new() { MinWidth = 300 }, Profile = new() { MinWidth = 120 }, Output = new() { MinWidth = 300 }, Work = new() { MinWidth = 300 };
    static readonly StackPanel ModList = new(), ProbeList = new();
    static readonly TextBlock Status = new() { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(4) };
    static bool _filling;

    static Launcher L => Shell.Launcher;

    static void Try(Func<string> act) { try { Status.Text = act(); } catch (InvalidOperationException ex) { Status.Text = ex.Message; } Fill(); }

    internal static UIElement Build()
    {
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
        var all = new Button { Content = "All probes", Margin = new Thickness(4) };
        all.Click += (_, _) => Try(() => L.SelectProbes(new[] { "all" }));
        var run = new Button { Content = "Run", Margin = new Thickness(4), FontWeight = FontWeights.SemiBold };
        run.Click += (_, _) => Try(() => L.Run());
        StackPanel Row(string label, params UIElement[] els)
        {
            var r = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(4, 2, 4, 2) };
            r.Children.Add(new TextBlock { Text = label, Width = 90, VerticalAlignment = VerticalAlignment.Center });
            foreach (var e in els) r.Children.Add(e);
            return r;
        }
        var panel = new StackPanel();
        panel.Children.Add(Row("Mode", Mode));
        panel.Children.Add(Row("Instance", Instance));
        panel.Children.Add(Row("Profile", Profile, open));
        panel.Children.Add(Row("Output", Output, setOut));
        panel.Children.Add(Row("Work folder", Work, setWork));
        panel.Children.Add(new TextBlock { Text = "Mods (MO2 priority order, strongest first)", FontWeight = FontWeights.SemiBold, Margin = new Thickness(4, 8, 4, 2) });
        panel.Children.Add(ModList);
        panel.Children.Add(new TextBlock { Text = "Probes", FontWeight = FontWeights.SemiBold, Margin = new Thickness(4, 8, 4, 2) });
        panel.Children.Add(all);
        panel.Children.Add(ProbeList);
        panel.Children.Add(run);
        panel.Children.Add(Status);
        // the run's state, read again while it goes
        var tick = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
        tick.Tick += (_, _) => { if (L.RunState == "running") Status.Text = "running: " + (L.RunLine ?? "started"); else if (L.RunState == "refused") Status.Text = L.RunRefusal; };
        tick.Start();
        return new ScrollViewer { Content = panel, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
    }

    internal static void Fill()
    {
        _filling = true;
        try
        {
            if (L.Instance is not null) { Instance.Text = L.Instance; Profile.Text = L.Profile ?? ""; }
            Output.Text = L.Output ?? Output.Text; Work.Text = L.WorkFolder ?? Work.Text;
            ModList.Children.Clear();
            foreach (var m in L.Mods)
            {
                if (m.Separator) { ModList.Children.Add(new TextBlock { Text = "— " + m.Name[..^"_separator".Length] + " —", Margin = new Thickness(4, 4, 4, 0) }); continue; }
                var cb = new CheckBox { Content = m.Name + (m.Enabled ? "" : "  (disabled)"), IsChecked = m.Selected, IsEnabled = m.Enabled, Margin = new Thickness(8, 1, 4, 1) };
                var name = m.Name;
                cb.Click += (_, _) => { if (_filling) return; Try(() => L.SelectMods(new[] { name }, cb.IsChecked == true ? "add" : "remove")); };
                ModList.Children.Add(cb);
            }
            ProbeList.Children.Clear();
            foreach (var c in Launcher.Registry())
            {
                var why = L.WhyNotHere(c);
                var cb = new CheckBox
                {
                    Content = c.Id + (L.AddedFor.TryGetValue(c.Id, out var f) ? $"  (added: {f})" : "") + (why is null ? "" : $"  ({why})"),
                    IsChecked = L.Probes.Contains(c.Id), IsEnabled = why is null, Margin = new Thickness(8, 1, 4, 1), ToolTip = c.Title,
                };
                var id = c.Id;
                cb.Click += (_, _) =>
                {
                    if (_filling) return;
                    var want = L.Probes.Where(p => !L.AddedFor.ContainsKey(p)).ToList();
                    if (cb.IsChecked == true) want.Add(id); else want.Remove(id);
                    if (want.Count == 0) { L.Probes.Clear(); L.AddedFor.Clear(); Fill(); return; }
                    Try(() => L.SelectProbes(want));
                };
                ProbeList.Children.Add(cb);
            }
        }
        finally { _filling = false; }
    }
}
