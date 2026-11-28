using System.Globalization;
using System.Text.RegularExpressions;

namespace Workbench;

/// <summary>How much the Work Folder and the Output Folder hold, said in the launcher where a modder sees it. SkyWright
/// removes neither: scans, trial patches, nudges and patches are the modder's to keep or delete. Read only: nothing here
/// writes, moves or deletes a file (PreloadRunTests, DiskUseTests).</summary>
internal static class DiskUse
{
    internal sealed record Reading(long Bytes, int Scans, int TrialAsks, int Nudges);

    static readonly Regex ScanFolder = new(@"^\d{8}T\d{6}Z$", RegexOptions.CultureInvariant);

    /// <summary>The bytes under <paramref name="folder"/>, and in a Work Folder its scans (one folder per scan, named by its
    /// UTC start), trial asks (under <c>trials</c>) and nudges; null when the folder is not there.</summary>
    internal static Reading? Measure(string? folder)
    {
        if (folder is null || !Directory.Exists(folder)) return null;
        long bytes = 0;
        var opts = new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true, AttributesToSkip = FileAttributes.ReparsePoint };
        foreach (var f in new DirectoryInfo(folder).EnumerateFiles("*", opts)) bytes += f.Length;
        var top = new DirectoryInfo(folder).EnumerateDirectories("*", new EnumerationOptions { IgnoreInaccessible = true }).Select(d => d.Name).ToList();
        var trials = Path.Combine(folder, "trials");
        int asks = Directory.Exists(trials) ? Directory.GetDirectories(trials).Length : 0;
        return new Reading(bytes, top.Count(ScanFolder.IsMatch), asks, top.Count(n => n.StartsWith("nudge-", StringComparison.Ordinal)));
    }

    internal static string Size(long bytes) => bytes switch
    {
        >= 1L << 30 => (bytes / (double)(1L << 30)).ToString("0.0", CultureInfo.InvariantCulture) + " GB",
        >= 1L << 20 => (bytes / (double)(1L << 20)).ToString("0", CultureInfo.InvariantCulture) + " MB",
        _ => (bytes / 1024.0).ToString("0", CultureInfo.InvariantCulture) + " KB",
    };

    /// <summary>The launcher's line: what each folder holds, and that SkyWright removes neither.</summary>
    internal static string Sentence(string? work, Reading? w, string? output, Reading? o)
    {
        var parts = new List<string>();
        if (work is not null && w is not null)
        {
            var what = new List<string> { $"{w.Scans} scan(s)" };
            if (w.TrialAsks > 0) what.Add($"{w.TrialAsks} trial ask(s)");
            if (w.Nudges > 0) what.Add($"{w.Nudges} nudge(s)");
            parts.Add($"the Work Folder ({work}) holds {Size(w.Bytes)}: {string.Join(", ", what)}");
        }
        if (output is not null && o is not null) parts.Add($"the Output Folder ({output}) holds {Size(o.Bytes)}");
        if (parts.Count == 0) return "";
        return "Disk: " + string.Join("; ", parts) + ". SkyWright removes neither; delete old scan folders in the Work Folder yourself when you no longer need their results.";
    }
}
