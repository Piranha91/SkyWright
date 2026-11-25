using Xunit;
using Xunit.Abstractions;

namespace SkyWright.Engine.Tests;

/// <summary>proofprobe's FOMOD decision-model section (FomodProof.cs, moved verbatim with fomodprobe and FomodModel.cs in
/// step 3 of the boundary), run whole: every case is printed to the test's output as proofprobe printed it
/// ("PASS  NAME  DETAIL" or "FAIL ..."), so the accounting finds each case by name in the trx, and the test fails naming
/// every case that failed, by proofprobe's canary rule (a case named "(must FAIL)" must fail).</summary>
public class FomodProofTests(ITestOutputHelper output)
{
    [Fact]
    public void FomodProof_section()
    {
        var failures = new List<string>();
        int n = 0;
        FomodProof.Run((name, ok, detail) =>
        {
            n++;
            bool canary = name.Contains("(must FAIL)", StringComparison.Ordinal);
            bool good = ok != canary;
            output.WriteLine($"   {(good ? "PASS" : "FAIL")}  {name,-56} {detail}{(canary && !ok ? "   [canary: red as designed]" : "")}");
            if (!good) failures.Add(name);
        });
        Assert.True(n > 0, "the section checked nothing");
        Assert.True(failures.Count == 0, "failed: " + string.Join("; ", failures));
    }
}
