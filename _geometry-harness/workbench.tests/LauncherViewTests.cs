using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Workbench;
using Xunit;

namespace WorkbenchTests;

/// <summary>The Start tab drawn offscreen with an open profile, to a PNG when WB_RENDER_OUT names a file; the controls
/// must show the profile picker, the mods (an unmanaged entry not tickable) and the probes.</summary>
[Collection("Shell"), Trait("tier", "render")]
public sealed class LauncherViewTests
{
    [Fact]
    public void The_start_tab_draws_its_controls_for_an_open_profile()
    {
        var inst = Path.Combine(Path.GetTempPath(), "hc-wb-view-" + Guid.NewGuid().ToString("N"));
        foreach (var d in new[] { "profiles/P", "profiles/Q", "mods/TopMod", "mods/OnMod", "mods/OffMod", "overwrite", "Data" }) Directory.CreateDirectory(Path.Combine(inst, d));
        File.WriteAllText(Path.Combine(inst, "ModOrganizer.ini"), "[General]" + Environment.NewLine + "selected_profile=@ByteArray(P)" + Environment.NewLine);
        File.WriteAllLines(Path.Combine(inst, "profiles", "P", "modlist.txt"), new[] { "+TopMod", "+Stuff_separator", "+OnMod", "-OffMod", "*DLC: Dawnguard" });
        Exception? failed = null; int drawn = 0;
        ShellCollection.Ui.Value.Invoke(() =>
        {
            try
            {
                var l = Shell.Launcher;
                l.OpenProfileAsync("free", inst, "P", inst).GetAwaiter().GetResult();
                l.SelectMods(new[] { "OnMod" }, "set");
                l.SelectProbes(new[] { "geometry.object-overlap", "state.conditional-geometry" });
                l.SetOutput(inst + "-out");
                var ui = (FrameworkElement)LauncherView.Build();
                LauncherView.Fill();
                var host = new Border { Width = 640, Height = 1100, Background = Brushes.White, Child = ui };
                host.Measure(new Size(640, 1100)); host.Arrange(new Rect(0, 0, 640, 1100)); host.UpdateLayout();
                var bmp = new RenderTargetBitmap(640, 1100, 96, 96, PixelFormats.Pbgra32);
                bmp.Render(host);
                var px = new byte[640 * 1100 * 4]; bmp.CopyPixels(px, 640 * 4, 0);
                for (int i = 0; i < px.Length; i += 4) if (px[i] < 128 && px[i + 1] < 128 && px[i + 2] < 128) drawn++;   // dark pixels: text and borders
                if (Environment.GetEnvironmentVariable("WB_RENDER_OUT") is { Length: > 0 } outPath)
                {
                    var enc = new PngBitmapEncoder(); enc.Frames.Add(BitmapFrame.Create(bmp));
                    Directory.CreateDirectory(Path.GetDirectoryName(outPath)!);
                    using var fs = File.Create(outPath); enc.Save(fs);
                }
                host.Child = null;   // the tab's controls are built once per process; give them back for the next test that draws it
            }
            catch (Exception ex) { failed = ex; }
        });
        Assert.True(failed is null, "the Start tab did not draw: " + failed);
        Assert.True(drawn > 1000, $"the Start tab drew {drawn} dark pixels; its controls are not on it");
    }
}
