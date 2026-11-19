using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Workbench;
using Xunit;

namespace WorkbenchTests;

/// <summary>The Start tab's "?" help: an entry for every probe tile, in plain words, and the one popup that shows it.</summary>
[Collection("Shell"), Trait("tier", "unit")]
public sealed class ProbeHelpTests
{
    static IEnumerable<Launcher.RegistryCheck> Probes() => Launcher.Registry().Where(c => Launcher.ProbeKinds.Contains(c.Kind));

    static int Sentences(string s) => Regex.Matches(s.Trim(), @"[.!?](\s|$)").Count;

    [Fact]
    public void Every_probe_tile_has_help_with_both_sections()
    {
        var missing = new List<string>();
        foreach (var c in Probes())
        {
            var h = ProbeHelp.Of(c.Id);
            if (h is null || string.IsNullOrWhiteSpace(h.What) || h.Paragraphs.Count == 0) missing.Add(c.Id);
        }
        Assert.True(missing.Count == 0, "no help written for: " + string.Join(", ", missing));
    }

    [Fact]
    public void Each_summary_is_at_most_two_sentences_and_each_method_is_a_short_read()
    {
        foreach (var (id, h) in ProbeHelp.All)
        {
            int n = Sentences(h.What);
            Assert.True(n is >= 1 and <= 2, $"{id}'s summary has {n} sentences: {h.What}");
            int words = Regex.Matches(h.How, @"\S+").Count;
            Assert.True(words is >= 80 and <= 260, $"{id}'s method is {words} words; it should be roughly 80 to 200");
        }
    }

    [Fact]
    public void Help_text_avoids_the_worst_jargon()
    {
        // tool words a player never meets, record signatures, and file names
        var banned = new[] { "host", "dependent", "emit", "predicate", "provenance", "arm", "REFR", "ACHR", "XESP", "XLKR", "XAPR", "NAVM", "Mutagen", "houseCARL" };
        var files = new[] { ".esp", ".esm", ".esl", ".ini", ".nif" };
        foreach (var (id, h) in ProbeHelp.All)
        {
            var text = h.What + "\n" + h.How;
            foreach (var b in banned) Assert.False(Regex.IsMatch(text, $@"\b{Regex.Escape(b)}\b", RegexOptions.IgnoreCase), $"{id}'s help says '{b}'");
            foreach (var f in files) Assert.DoesNotContain(f, text, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public void Every_probe_tile_carries_a_help_button()
    {
        Exception? failed = null; var without = new List<string>();
        ShellCollection.Ui.Value.Invoke(() =>
        {
            try
            {
                foreach (var c in Probes())
                {
                    var tile = LauncherView.MakeTile(c);
                    var help = Descendants(tile).OfType<Button>().FirstOrDefault(b => b.Content as string == "?");
                    if (help is null || help.Tag as string != c.Id || help.ToolTip is null) without.Add(c.Id);
                }
            }
            catch (Exception ex) { failed = ex; }
        });
        Assert.True(failed is null, "a tile did not build: " + failed);
        Assert.True(without.Count == 0, "tiles without a help button: " + string.Join(", ", without));
    }

    [Fact]
    public void A_probe_with_no_help_says_so_in_the_popup()
    {
        string? said = null;
        ShellCollection.Ui.Value.Invoke(() => said = string.Join(" | ", Descendants(ProbeHelpPopup.Content("geometry.no-such-probe")).OfType<TextBlock>().Select(t => t.Text)));
        Assert.Contains(ProbeHelp.Missing, said);
        Assert.DoesNotContain("How it works", said);
    }

    [Fact]
    public void One_popup_is_open_at_a_time_and_Esc_or_close_shuts_it()
    {
        Exception? failed = null;
        ShellCollection.Ui.Value.Invoke(() =>
        {
            try
            {
                var a = ProbeHelpPopup.Button("geometry.object-overlap");
                var b = ProbeHelpPopup.Button("environment.land-seams");
                var host = new StackPanel(); host.Children.Add(a); host.Children.Add(b);
                var w = new Window { Content = host, Width = 300, Height = 200, ShowInTaskbar = false, WindowStyle = WindowStyle.None, Left = -2000, Top = -2000, ShowActivated = false };
                w.Show();
                try
                {
                    ProbeHelpPopup.Show("geometry.object-overlap", a);
                    Assert.Equal("geometry.object-overlap", ProbeHelpPopup.OpenId);
                    ProbeHelpPopup.Show("environment.land-seams", b);
                    Assert.Equal("environment.land-seams", ProbeHelpPopup.OpenId);
                    ProbeHelpPopup.Close();
                    Assert.Null(ProbeHelpPopup.OpenId);
                    // Esc on the popup's face closes it the same way
                    var closed = false;
                    var face = ProbeHelpPopup.Content("environment.land-seams", () => closed = true);
                    var esc = new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(w)!, 0, Key.Escape) { RoutedEvent = UIElement.PreviewKeyDownEvent };
                    face.RaiseEvent(esc);
                    Assert.True(closed, "Esc did not close the help");
                }
                finally { ProbeHelpPopup.Close(); w.Close(); }
            }
            catch (Exception ex) { failed = ex; }
        });
        Assert.True(failed is null, "the popup did not behave: " + failed);
    }

    /// <summary>Each probe's popup face drawn offscreen; with WB_HELP_RENDER_DIR set, each is written there as a PNG.</summary>
    [Fact, Trait("tier", "render")]
    public void Every_popup_draws_its_text_inside_its_frame()
    {
        Exception? failed = null; var clipped = new List<string>();
        var dir = Environment.GetEnvironmentVariable("WB_HELP_RENDER_DIR");
        ShellCollection.Ui.Value.Invoke(() =>
        {
            try
            {
                foreach (var c in Probes())
                {
                    var face = ProbeHelpPopup.Content(c.Id);
                    var host = new Border { Background = Theme.Brush(Theme.Back), Padding = new Thickness(10), Child = face };
                    host.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
                    var size = host.DesiredSize;
                    host.Arrange(new Rect(size)); host.UpdateLayout();
                    // the scrolling body holds the text: wrapped to the frame's width, never wider
                    var scroll = Descendants(face).OfType<ScrollViewer>().Single();
                    if (scroll.ExtentWidth > scroll.ViewportWidth + 0.5) clipped.Add(c.Id);
                    if (dir is { Length: > 0 })
                    {
                        var bmp = new RenderTargetBitmap((int)Math.Ceiling(size.Width), (int)Math.Ceiling(size.Height), 96, 96, PixelFormats.Pbgra32);
                        bmp.Render(host);
                        var enc = new PngBitmapEncoder(); enc.Frames.Add(BitmapFrame.Create(bmp));
                        Directory.CreateDirectory(dir);
                        using var fs = File.Create(Path.Combine(dir, "help-" + c.Id + ".png")); enc.Save(fs);
                    }
                }
            }
            catch (Exception ex) { failed = ex; }
        });
        Assert.True(failed is null, "a popup did not draw: " + failed);
        Assert.True(clipped.Count == 0, "help text wider than its frame: " + string.Join(", ", clipped));
    }

    static IEnumerable<DependencyObject> Descendants(DependencyObject root)
    {
        foreach (var child in LogicalTreeHelper.GetChildren(root).OfType<DependencyObject>())
        {
            yield return child;
            foreach (var d in Descendants(child)) yield return d;
        }
    }
}
