using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Workbench;
using Xunit;

namespace WorkbenchTests;

/// <summary>The Start tab after a done run, drawn from a disposition the engine wrote (the fold's mesh-only run, whose object
/// overlap found row O8 and is listed missing for its unknown applicability): the run line must say the check found
/// something and is not complete, never that nothing was measured (FOLD-REVIEW finding 2). Drawn to a PNG when WB_RENDER_OUT
/// names a file. The tab's controls are built once per process (LauncherView.Build), so the test hands them back after.</summary>
[Collection("Shell"), Trait("tier", "render")]
public sealed class LauncherDoneLineTests
{
    static void Set(object o, string name, object? v) =>
        o.GetType().GetProperty(name, BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public)!.GetSetMethod(true)!.Invoke(o, new[] { v });

    [Fact]
    public void A_done_mesh_only_run_says_what_it_found_on_the_start_tab()
    {
        var inst = Path.Combine(Path.GetTempPath(), "hc-wb-done-" + Guid.NewGuid().ToString("N"));
        foreach (var d in new[] { "profiles/P", "mods/PpMeshMod", "overwrite", "Data" }) Directory.CreateDirectory(Path.Combine(inst, d));
        File.WriteAllText(Path.Combine(inst, "ModOrganizer.ini"), "[General]" + Environment.NewLine + "selected_profile=@ByteArray(P)" + Environment.NewLine);
        File.WriteAllLines(Path.Combine(inst, "profiles", "P", "modlist.txt"), new[] { "+PpMeshMod" });
        string disp = RunTallyTests.Real("fold-meshonly");
        Exception? failed = null; string text = "";
        ShellCollection.Ui.Value.Invoke(() =>
        {
            Border? host = null;
            try
            {
                var l = Shell.Launcher;
                l.OpenProfileAsync("free", inst, "P", inst).GetAwaiter().GetResult();
                l.SelectMods(new[] { "PpMeshMod" }, "set");
                l.SelectProbes(new[] { "geometry.object-overlap" });
                Set(l, "RunState", "done");
                Set(l, "RunTally", Launcher.Tally(disp, new[] { "geometry.object-overlap" }));
                // the scope line this change writes for the fixture's PpMeshMod (tests/test_asset_only_selection.py)
                Set(l, "ScopeNote", "PpMeshMod has no plugin, so it places nothing; 9 placed objects use a mesh it provides that wins and differs "
                                    + "from the one it replaces (for example mshb.nif in SkRoom). Object overlap and placement contact compare those "
                                    + "with and without it. Worn and actor meshes, its textures (0 files) and other files are not measured.");
                var ui = (FrameworkElement)LauncherView.Build();
                LauncherView.Fill();
                host = new Border { Width = 640, Height = 1100, Background = Brushes.White, Child = ui };
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
            finally
            {
                if (host is not null) host.Child = null;
                Set(Shell.Launcher, "RunState", "idle"); Set(Shell.Launcher, "RunTally", null); Set(Shell.Launcher, "ScopeNote", null);
            }
        });
        Assert.True(failed is null, "the Start tab did not draw: " + failed);
        Assert.StartsWith("done: 1 check found something (1 not complete), 0 checks found nothing, 0 checks did not complete, so this is not a full result;", text);
    }
}
