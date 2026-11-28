using System.Security.Cryptography;
using Workbench;
using Xunit;

namespace WorkbenchTests;

/// <summary>The launcher's disk line: what the Work Folder and the Output Folder hold, in plain words with their paths,
/// and that reading it changes nothing on the disk.</summary>
public sealed class DiskUseTests
{
    [Fact]
    public void The_line_names_each_folder_its_size_and_what_the_work_folder_holds_and_reading_it_changes_nothing()
    {
        var root = Path.Combine(Path.GetTempPath(), "hc-wb-diskuse-" + Guid.NewGuid().ToString("N"));
        try
        {
            string work = Path.Combine(root, "Work"), output = Path.Combine(root, "Output");
            var files = new Dictionary<string, int>
            {
                [Path.Combine(work, "20261101T100000Z", "run", "bundle.json")] = 2 << 20,
                [Path.Combine(work, "20261102T100000Z", "run", "bundle.json")] = 1 << 20,
                [Path.Combine(work, "20261103T100000Z", "entry.json")] = 1000,
                [Path.Combine(work, "trials", "20261127T200000Z-1", "1", "WbTrial.esp")] = 2000,
                [Path.Combine(work, "trials", "20261127T200100Z-2", "1", "WbTrial.esp")] = 2000,
                [Path.Combine(work, "nudge-20261127T200000Z", "answer.json")] = 300,
                [Path.Combine(work, "not-a-scan", "x.txt")] = 10,
                [Path.Combine(output, "houseCARL - WbRe", "WbRe.esp")] = 3 << 20,
            };
            foreach (var (f, n) in files) { Directory.CreateDirectory(Path.GetDirectoryName(f)!); File.WriteAllBytes(f, new byte[n]); }
            string Print(string f) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(f))) + "|" + File.GetLastWriteTimeUtc(f).Ticks;
            var before = files.Keys.ToDictionary(f => f, Print);

            var w = DiskUse.Measure(work)!; var o = DiskUse.Measure(output)!;
            Assert.Equal((3, 2, 1), (w.Scans, w.TrialAsks, w.Nudges));
            Assert.Equal(files.Where(kv => kv.Key.StartsWith(work + Path.DirectorySeparatorChar)).Sum(kv => (long)kv.Value), w.Bytes);
            Assert.Equal(3L << 20, o.Bytes);
            var said = DiskUse.Sentence(work, w, output, o);
            Assert.Equal($"Disk: the Work Folder ({work}) holds 3 MB: 3 scan(s), 2 trial ask(s), 1 nudge(s); the Output Folder ({output}) holds 3 MB. "
                         + "SkyWright removes neither; delete old scan folders in the Work Folder yourself when you no longer need their results.", said);

            foreach (var f in files.Keys) Assert.True(File.Exists(f) && Print(f) == before[f], $"{f} was changed or removed by reading the sizes");
            Assert.Null(DiskUse.Measure(Path.Combine(root, "absent")));
            Assert.Equal("", DiskUse.Sentence(null, null, null, null));
        }
        finally { Directory.Delete(root, true); }
    }
}
