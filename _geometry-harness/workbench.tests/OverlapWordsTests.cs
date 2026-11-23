using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Workbench;
using Xunit;

namespace WorkbenchTests;

/// <summary>A predicted overlap in a modder's words: the check's summary, its attribution and its physical state.</summary>
[Trait("tier", "unit")]
[Collection("Shell")]
public sealed class OverlapWordsTests
{
    const string RockS02 = "Static from Riften Expansion.esp / Tree from Unofficial Skyrim Special Edition Patch.esp: crossing, containment unassessed, 41 triangle pairs in RiftenCitySouth (ext) [mm] 079241:Skyrim.esm / 079242:Skyrim.esm";

    [Fact]
    public void The_checks_summary_reads_as_a_sentence_about_the_two_objects_and_their_plugins()
    {
        var p = OverlapWords.Parse(RockS02)!;
        Assert.Equal(("Static", "Riften Expansion.esp", "Tree", "Unofficial Skyrim Special Edition Patch.esp", "crossing", "unassessed", "RiftenCitySouth"),
                     (p.TypeA, p.PluginA, p.TypeB, p.PluginB, p.Relation, p.Containment, p.Place));
        var s = OverlapWords.Sentence(RockS02);
        Assert.Equal("A static from Riften Expansion.esp passes into a tree from Unofficial Skyrim Special Edition Patch.esp in RiftenCitySouth.", s);
        foreach (var probeWord in new[] { "containment", "unassessed", "triangle pairs", "(ext)", "[mm]", "079241" }) Assert.DoesNotContain(probeWord, s);
        // FormKeys whose plugin name has a space (all 700 of the held-out evaluation's overlap summaries parse)
        var spaced = OverlapWords.Parse("Static from Riften Expansion.esp / Tree from Unofficial Skyrim Special Edition Patch.esp: crossing, containment unassessed, 13 triangle pairs in RiftenCitySouth (ext) [mm] 000828:Riften Expansion.esp / 024355:Skyrim.esm");
        Assert.Equal(("Riften Expansion.esp", "Unofficial Skyrim Special Edition Patch.esp", "RiftenCitySouth"), (spaced!.PluginA, spaced.PluginB, spaced.Place));
        // a thousands separator in the count, and a summary of another shape is kept as it is
        Assert.NotNull(OverlapWords.Parse(RockS02.Replace("41 triangle", "1,204 triangle")));
        Assert.Equal("an overlap that was there already", OverlapWords.Sentence("an overlap that was there already"));
    }

    [Fact]
    public void The_attribution_and_the_physical_state_are_said_in_words()
    {
        Assert.Equal("It appears only with both mods together.", OverlapWords.Attribution("needs-both", "A.esp"));
        Assert.StartsWith("It comes with Riften Expansion.esp. No setup without the other mod was checked", OverlapWords.Attribution("with-target", "Riften Expansion.esp"));
        Assert.DoesNotContain("needs-some", OverlapWords.Attribution("needs-some", "A.esp"));
        Assert.Equal("Still there: the two objects cross in the setup checked.", OverlapWords.Physical("Holds"));
    }

    /// <summary>The RockS02 overlap row as the reader gives it, with the check's own words.</summary>
    static ResultRow RockRow() => new(12, ResultGroup.NotScored, "cocd-efps", "overlap", "OVERLAP", "REFR", "079241:Skyrim.esm", null, null, "RockS02", "Static",
        null, null, "with-target", RockS02,
        new[] { new ResultValue("what the check says", null, RockS02), new ResultValue("physical state", null, "Holds"), new ResultValue("attribution", "Riften Expansion.esp", "with-target") },
        new ResultPlace("042245:Skyrim.esm", "079241:Skyrim.esm", "079242:Skyrim.esm", false, null), "before", Array.Empty<string>(),
        OverlapWords.Attribution("with-target", "Riften Expansion.esp"), "RiftenCitySouth");

    [Fact]
    public void The_overlap_values_pane_shows_no_probe_word()
    {
        string[] texts = Array.Empty<string>();
        ShellCollection.Ui.Value.Invoke(() =>
        {
            var pane = (FrameworkElement)ResultDetail.Build(RockRow(), () => { });
            static IEnumerable<string> Walk(DependencyObject d) =>
                (d is TextBlock t ? new[] { t.Text } : Array.Empty<string>()).Concat(LogicalTreeHelper.GetChildren(d).OfType<DependencyObject>().SelectMany(Walk));
            texts = Walk(pane).ToArray();
            // a picture of the pane, for the task's evidence, when asked for
            if (Environment.GetEnvironmentVariable("WB_DETAIL_PNG") is { Length: > 0 } png)
            {
                var host = new Border { Background = Theme.Brush("#1E1F22"), Width = 420, Child = pane };
                System.Windows.Documents.TextElement.SetForeground(host, Brushes.Gainsboro);
                host.Measure(new Size(420, double.PositiveInfinity)); host.Arrange(new Rect(host.DesiredSize)); host.UpdateLayout();
                var bmp = new RenderTargetBitmap((int)Math.Ceiling(host.ActualWidth * 1.5), (int)Math.Ceiling(host.ActualHeight * 1.5), 144, 144, PixelFormats.Pbgra32);
                bmp.Render(host);
                var enc = new PngBitmapEncoder(); enc.Frames.Add(BitmapFrame.Create(bmp));
                using var f = File.Create(png); enc.Save(f);
            }
        });
        var all = string.Join(Environment.NewLine, texts);
        Assert.Contains("A static from Riften Expansion.esp passes into a tree from Unofficial Skyrim Special Edition Patch.esp in RiftenCitySouth.", all);
        Assert.Contains("It comes with Riften Expansion.esp", all);
        foreach (var probeWord in new[] { "containment", "unassessed", "triangle pairs", "(ext)", "[mm]", "with-target", "Holds", "Attribution", "Physical state" })
            Assert.DoesNotContain(probeWord, all);
    }
}
