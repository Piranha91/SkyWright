using Workbench;
using Xunit;

namespace WorkbenchTests;

/// <summary>Step 3 of the SkyWright / houseCARL boundary: a bundle made before the AI-side rows left the app's registry
/// (tests/fixtures/old-bundles/lab-record-rows: a record import credited on an mcp-only row, a record-rows enrichment)
/// still opens, nothing crashes, and what the app no longer shows is said in one sentence.</summary>
public sealed class OldBundleTests : IDisposable
{
    readonly string _dir = Path.Combine(Path.GetTempPath(), "sw-old-bundle-" + Guid.NewGuid().ToString("N")[..8]);

    public OldBundleTests()
    {
        var src = Path.Combine(R3Bundle.Checkout(), "_geometry-harness", "tests", "fixtures", "old-bundles", "lab-record-rows");
        foreach (var f in Directory.EnumerateFiles(src, "*", SearchOption.AllDirectories))
        {
            var t = Path.Combine(_dir, "bundle", Path.GetRelativePath(src, f));
            Directory.CreateDirectory(Path.GetDirectoryName(t)!);
            File.Copy(f, t);
        }
    }

    public void Dispose() { try { Directory.Delete(_dir, true); } catch { } }

    [Fact]
    public void An_old_lab_bundle_opens_and_says_in_one_sentence_which_receipts_are_of_checks_this_SkyWright_does_not_list()
    {
        var b = BundleConflicts.Open(Path.Combine(_dir, "bundle"), "baseline", Path.Combine(_dir, "verify.json"), RunEngine.OfBuiltIn(R3Bundle.Runcontrol));
        // the imported record rows are an AI-side evaluation's: not shown as the app's Findings
        Assert.DoesNotContain(b.Findings, f => f.CheckId.StartsWith("records.", StringComparison.Ordinal) || f.CheckId.StartsWith("loadability.dangling", StringComparison.Ordinal));
        Assert.DoesNotContain(b.Checks, c => c.CheckId.StartsWith("records.", StringComparison.Ordinal));
        // ...and said once: how many receipts, of which checks, that the integration module reads them
        var said = b.Notes.Where(n => n.Contains("checks this SkyWright does not list", StringComparison.Ordinal)).ToList();
        Assert.Single(said);
        Assert.Contains("records.cell-attributes", said[0]);
        Assert.DoesNotContain("\n", said[0]);
        Assert.EndsWith(".", said[0]);
    }
}
