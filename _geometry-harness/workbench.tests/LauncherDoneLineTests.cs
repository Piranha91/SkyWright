using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Workbench;
using Xunit;

namespace WorkbenchTests;

/// <summary>The Start tab after a done run in which no ticked check completed: the run line must say nothing was
/// measured, not only that the results are open. Drawn to a PNG when WB_RENDER_OUT names a file.</summary>
[Collection("Shell"), Trait("tier", "render")]
public sealed class LauncherDoneLineTests
{
    static void Set(object o, string name, object? v) =>
        o.GetType().GetProperty(name, BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public)!.GetSetMethod(true)!.Invoke(o, new[] { v });

    [Fact]
    public void A_done_run_that_measured_nothing_says_so_on_the_start_tab()
    {
        var inst = Path.Combine(Path.GetTempPath(), "hc-wb-done-" + Guid.NewGuid().ToString("N"));
        foreach (var d in new[] { "profiles/P", "mods/TexMod", "overwrite", "Data" }) Directory.CreateDirectory(Path.Combine(inst, d));
        File.WriteAllText(Path.Combine(inst, "ModOrganizer.ini"), "[General]" + Environment.NewLine + "selected_profile=@ByteArray(P)" + Environment.NewLine);
        File.WriteAllLines(Path.Combine(inst, "profiles", "P", "modlist.txt"), new[] { "+TexMod" });
        var disp = Path.Combine(inst + "-out", "disposition.json");
        Directory.CreateDirectory(Path.GetDirectoryName(disp)!);
        File.WriteAllText(disp, """{"evaluation": {"missing": [{"checkId": "geometry.object-overlap", "armId": "candidate-01"}], "findings": [], "completedNullChecks": []}}""");
        Exception? failed = null; string text = "";
        ShellCollection.Ui.Value.Invoke(() =>
        {
            try
            {
                var l = Shell.Launcher;
                l.OpenProfileAsync("free", inst, "P", inst).GetAwaiter().GetResult();
                l.SelectMods(new[] { "TexMod" }, "set");
                l.SelectProbes(new[] { "geometry.object-overlap" });
                Set(l, "RunState", "done");
                Set(l, "RunTally", Launcher.Tally(disp, new[] { "geometry.object-overlap" }));
                Set(l, "ScopeNote", "TexMod has no plugin and no mesh (1 texture file), so no check here can measure it: textures, sounds and scripts change nothing these checks read.");
                var ui = (FrameworkElement)LauncherView.Build();
                LauncherView.Fill();
                var host = new Border { Width = 640, Height = 1100, Background = Brushes.White, Child = ui };
                host.Measure(new Size(640, 1100)); host.Arrange(new Rect(0, 0, 640, 1100)); host.UpdateLayout();
                text = ((TextBlock)typeof(LauncherView).GetField("RunLine", BindingFlags.Static | BindingFlags.NonPublic)!.GetValue(null)!).Text;
                if (Environment.GetEnvironmentVariable("WB_RENDER_OUT") is { Length: > 0 } outPath)
                {
                    var bmp = new RenderTargetBitmap(640, 1100, 96, 96, PixelFormats.Pbgra32);
                    bmp.Render(host);
                    var enc = new PngBitmapEncoder(); enc.Frames.Add(BitmapFrame.Create(bmp));
                    Directory.CreateDirectory(Path.GetDirectoryName(outPath)!);
                    using var fs = File.Create(outPath); enc.Save(fs);
                }
            }
            catch (Exception ex) { failed = ex; }
            finally { Set(Shell.Launcher, "RunState", "idle"); Set(Shell.Launcher, "RunTally", null); Set(Shell.Launcher, "ScopeNote", null); }
        });
        Assert.True(failed is null, "the Start tab did not draw: " + failed);
        Assert.StartsWith("done: 0 checks found something, 0 checks found nothing, 1 check did not complete, so nothing was measured; the results are open", text);
    }
}
