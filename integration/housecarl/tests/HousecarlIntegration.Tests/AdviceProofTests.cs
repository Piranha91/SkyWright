using Xunit;
using Xunit.Abstractions;

namespace SkyWright.Engine.Tests;

/// <summary>proofprobe's author-stated-order section (AdviceProof.cs, moved here unchanged with adviceprobe and its
/// extractor in step 3 of the boundary), run whole: every case it checks is printed to the test's output as proofprobe
/// printed it ("PASS  NAME  DETAIL" or "FAIL ..."), so the accounting finds each case by name in the trx, and the test
/// fails naming every case that failed, by proofprobe's rule for a canary (a case named "(must FAIL)" is planted wrong,
/// so it must fail).</summary>
public class AdviceProofTests(ITestOutputHelper output)
{
    [Fact]
    public void AdviceProof_section()
    {
        var failures = new List<string>();
        int n = 0;
        AdviceProof.Run((name, ok, detail) =>
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
