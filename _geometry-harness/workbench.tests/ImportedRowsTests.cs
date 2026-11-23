using System.Text.Json;
using Workbench;
using Xunit;
using Xunit.Abstractions;

namespace WorkbenchTests;

/// <summary>A full evaluation's bundle opens with its imported checks' rows (houseCARL's checks, imported by evalrecords:
/// dangling references, missing assets, the record checks). The rows are each check's own rows file, the one its
/// result.json names, and their count is the check's own. WB_REAL_BUNDLE names a bundle folder (the one holding plan.json);
/// the test only reads it, and checks no file in it changed. Without it the test fails saying so.</summary>
[Trait("tier", "real-run")]
public sealed class ImportedRowsRealRunTests
{
    readonly ITestOutputHelper _out;
    public ImportedRowsRealRunTests(ITestOutputHelper o) => _out = o;

    static Dictionary<string, (long, DateTime)> Snapshot(string dir) =>
        Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories).ToDictionary(f => f, f => { var i = new FileInfo(f); return (i.Length, i.LastWriteTimeUtc); });

    [Fact]
    public void A_full_evaluation_bundle_opens_and_each_imported_check_shows_its_own_rows()
    {
        var dir = Environment.GetEnvironmentVariable("WB_REAL_BUNDLE") is { Length: > 0 } d ? d
            : throw new Xunit.Sdk.XunitException("set WB_REAL_BUNDLE to a full evaluation's bundle folder (the one holding plan.json) to open it");
        var work = Path.Combine(Path.GetTempPath(), "hc-wb-imported-" + Guid.NewGuid().ToString("N"));
        var before = Snapshot(dir);
        var b = BundleConflicts.Open(dir, "baseline", Path.Combine(work, "verify.json"), RunEngine.OfBuiltIn(R3Bundle.Runcontrol));
        Assert.Equal(before, Snapshot(dir));                // opening reads; nothing in the bundle changed
        foreach (var n in b.Notes) _out.WriteLine("note: " + n);

        int checkedImports = 0;
        foreach (var rf in Directory.EnumerateFiles(Path.Combine(dir, "evidence", "imports"), "result.json", SearchOption.AllDirectories)
                     .Where(f => Path.GetFileName(Path.GetDirectoryName(f)!).EndsWith("__baseline", StringComparison.Ordinal)))
        {
            using var r = JsonDocument.Parse(File.ReadAllText(rf));
            var check = r.RootElement.GetProperty("checkId").GetString()!;
            var rows = Path.Combine(Path.GetDirectoryName(rf)!, "03-rows.json");
            // the check's own rows, as the Findings tab keys them, and its own count of findings
            // every entry of the file, one row each (a record dangling to two targets is two rows), counted independently of the reader
            using var rd = JsonDocument.Parse(File.ReadAllText(rows));
            var own = rd.RootElement.GetProperty("Conflicts").EnumerateArray()
                .SelectMany(c =>
                {
                    string Key(string fk) => (c.GetProperty("Kind").GetString() + "|" + fk + "|" + (c.TryGetProperty("A", out var a) && a.ValueKind == JsonValueKind.String ? a.GetString() : "")
                                              + "|" + (c.TryGetProperty("Summary", out var sm) ? sm.GetString() : "")).ToLowerInvariant();
                    var nec = c.GetProperty("Necessary").EnumerateArray().Select(n => Key(n.GetProperty("FormKey").GetString()!)).ToList();
                    return nec.Count > 0 ? nec : new List<string> { Key("") };      // an entry that names no record is one row
                })
                .Distinct().Count();
            int said = r.RootElement.GetProperty("result").TryGetProperty("findings", out var f) && f.ValueKind == JsonValueKind.Number ? f.GetInt32() : -1;
            int shown = b.Findings.Count(x => x.CheckId == check);
            _out.WriteLine($"{check}: shown {shown}, its rows file {own} distinct row(s), its result.json says {said} finding(s)");
            Assert.Equal(own, shown);
            if (check is "loadability.dangling-references" or "loadability.missing-assets") Assert.Equal(said, shown);
            checkedImports++;
        }
        Assert.True(checkedImports > 0, "the bundle has no imported check on its baseline arm");
        Assert.DoesNotContain(b.Notes, n => n.Contains("no Findings reader"));
        try { Directory.Delete(work, true); } catch (IOException) { }
    }
}
