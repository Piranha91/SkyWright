using System.Security.Cryptography;
using HarnessShared;
using Workbench;
using Xunit;

namespace WorkbenchTests;

/// <summary>The background preload leaves nothing behind but its area's own log: its run records last as long as its
/// process, so the preload folder does not grow with every preload, and a preload never reads an earlier one's records
/// (a run log is read whole at start-up). Nothing else on the disk is touched. contactprobe stands in for viewprobe: it
/// opens its run record at start-up and refuses a missing --base at once.</summary>
public sealed class PreloadRunTests
{
    static string Contactprobe => CheckoutLayout.ProgramExe(TestBuild.Layout.Root, "contactprobe");

    static string NewRoot()
    {
        var d = Path.Combine(Path.GetTempPath(), "hc-wb-preload-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(d);
        return d;
    }

    static (int Exit, string Text) Preload(string dir, string file)
    {
        var psi = PreloadRun.Start(Contactprobe, dir, file, new[] { "--base", Path.Combine(dir, "no-such-base.json"), "--emit", Path.Combine(dir, "e.json") });
        var (exit, o, e) = PreloadRun.Run(psi, _ => { });
        return (exit, o + e);
    }

    static int Lines(string f) => File.Exists(f) ? File.ReadLines(f).Count(l => l.Length > 0) : 0;

    [Fact]
    public void A_preload_leaves_no_run_records_in_its_folder()
    {
        var root = NewRoot();
        try
        {
            var dir = Path.Combine(root, "preload"); Directory.CreateDirectory(dir);
            var (exit, text) = Preload(dir, Path.Combine(dir, "area_0_0.bin"));
            Assert.True(exit == 2, $"the stand-in exit {exit}: {text}");
            var left = Directory.GetFileSystemEntries(dir, "*", SearchOption.AllDirectories).Select(p => Path.GetRelativePath(dir, p)).ToList();
            Assert.True(left.Count == 0, $"the preload left {string.Join(", ", left)} in its folder");
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public void A_preload_reads_no_earlier_preloads_records()
    {
        var root = NewRoot();
        try
        {
            var dir = Path.Combine(root, "preload"); Directory.CreateDirectory(dir);
            var file = Path.Combine(dir, "area_0_0.bin");
            // an earlier preload's records where this one keeps its own (one that ended without removing them)
            var stale = Path.Combine(PreloadRun.RunLogOf(dir, file), "history.jsonl");
            Directory.CreateDirectory(Path.GetDirectoryName(stale)!);
            File.WriteAllLines(stale, new[] { "{}", "{}", "{}" });
            // a stand-in that says how many records its run log holds as it starts, which is when a probe reads them all
            var psi = PreloadRun.Start("python", dir, file, new[] { "-c",
                "import os; p = os.path.join(os.environ['HOUSECARL_RUNLOG_DIR'], 'history.jsonl'); print('AT-START', sum(1 for _ in open(p)) if os.path.exists(p) else 0)" });
            var (exit, o, e) = PreloadRun.Run(psi, _ => { });
            Assert.True(exit == 0 && o.Contains("AT-START"), $"the stand-in exit {exit}: {o}{e}");
            Assert.True(o.Contains("AT-START 0"), $"the preload started with an earlier preload's records in its run log: {o.Trim()}");
            Assert.True(!File.Exists(stale), $"the earlier records are still there ({Lines(stale)} lines)");
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public void Preloads_touch_no_patch_in_the_output_folder_and_no_trial_patch()
    {
        var root = NewRoot();
        try
        {
            // a patch in the Output Folder, a trial patch under the Work Folder, and an earlier area's own log beside the preloads
            var keep = new[]
            {
                Path.Combine(root, "Output", "houseCARL - WbRe", "WbRe.esp"),
                Path.Combine(root, "Work", "trials", "20261127T200000Z-1", "1", "WbTrial.esp"),
                Path.Combine(root, "out", "preload", "area_5_5.log"),
            };
            foreach (var k in keep) { Directory.CreateDirectory(Path.GetDirectoryName(k)!); File.WriteAllText(k, "kept " + k); }
            string Print(string f) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(f))) + "|" + File.GetLastWriteTimeUtc(f).Ticks;
            var before = keep.ToDictionary(k => k, Print);
            var dir = Path.Combine(root, "out", "preload");
            for (int i = 0; i < 2; i++) Preload(dir, Path.Combine(dir, "area_0_0.bin"));
            foreach (var k in keep)
            {
                Assert.True(File.Exists(k), $"{k} is gone");
                Assert.True(Print(k) == before[k], $"{k} was changed");
            }
        }
        finally { Directory.Delete(root, true); }
    }
}
