using System.Diagnostics;
using HarnessShared;
using Workbench;
using Xunit;

namespace WorkbenchTests;

/// <summary>Stage 4's frozen comparison rule, on synthetic emits. A base emit with three rows (R1, R2, R3) and a changed
/// emit, made with --base, in which R1 vanished with its predicate re-measured Gone, R2 vanished with its predicate still
/// Holds, R3 stayed, and R4 entered holding. The re-check must read R1 resolved, R2 reclassified, R3 still there and R4
/// new, and conflictdiff itself, run on the same two files, must count the same.</summary>
[Trait("tier", "unit")]
public class RecheckRuleTests
{
    const string Probe = "overlapprobe", Policy = "synthetic-policy", Pop = "load-time+population:000000000000", State = "baseline";

    static Conflict Overlap(int a, int b, PhysicalState ph) =>
        new("OVERLAP", "Base.esm", "Subject.esp", "synthetic",
            new[] { new ConflictRecord($"{a:X6}:Base.esm", "placed-ref", null, ph) },
            new[] { new ConflictRecord($"{b:X6}:Base.esm", "placed-ref", null) });

    static string Id(int a, int b) => ConflictIo.RowIdentity("OVERLAP", $"{a:X6}:Base.esm", $"{b:X6}:Base.esm", "Base.esm", null);

    static ConfigurationIdentity Config(params string[] plugins) =>
        ConfigurationIdentity.Compute("synthetic:test",
            plugins.Select((p, i) => new ConfigPlugin(p, "C:/synthetic/" + p, new string((char)('a' + i), 64), 100, "data", i == 0, false, Array.Empty<string>())).ToList(),
            new[] { new ConfigProvider("data", "data") }, Array.Empty<ConfigOverlay>(), Pop, State);

    /// <summary>The two emits; written to <paramref name="dir"/> as base.json and changed.json.</summary>
    internal static (ConflictFile Base, ConflictFile Changed) Emits(string dir)
    {
        var cov = new[] { new CoverageLine("placed references", 10) };
        var b = new ConflictFile(Probe, "synthetic", "2026-11-14T00:00:00Z",
            new[] { Overlap(0x801, 0x901, PhysicalState.Holds), Overlap(0x802, 0x902, PhysicalState.Holds), Overlap(0x803, 0x903, PhysicalState.Holds) },
            cov, Population: Pop, State: State, Configuration: Config("Base.esm", "Subject.esp"), MeasurementPolicy: Policy);
        var c = new ConflictFile(Probe, "synthetic", "2026-11-14T00:00:01Z",
            new[] { Overlap(0x803, 0x903, PhysicalState.Holds), Overlap(0x804, 0x904, PhysicalState.Holds) },
            cov,
            Predicates: new[]
            {
                new Predicate("OVERLAP", "Base.esm", "Subject.esp", "000801:Base.esm", "000901:Base.esm", PhysicalState.Gone, null),
                new Predicate("OVERLAP", "Base.esm", "Subject.esp", "000802:Base.esm", "000902:Base.esm", PhysicalState.Holds, null),
            },
            BaseEmit: Path.Combine(dir, "base.json"), Population: Pop, State: State,
            Configuration: Config("Base.esm", "Subject.esp", "Patch.esp"), MeasurementPolicy: Policy);
        ConflictIo.Write(Path.Combine(dir, "base.json"), b);
        ConflictIo.Write(Path.Combine(dir, "changed.json"), c);
        return (b, c);
    }

    static string Dir()
    {
        var d = Environment.GetEnvironmentVariable("WB_FROZEN_OUT");
        if (string.IsNullOrEmpty(d)) d = Path.Combine(AppContext.BaseDirectory, "frozen-rule-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(d);
        return d;
    }

    [Fact]
    public void A_row_whose_predicate_went_from_holds_to_gone_reads_resolved()
    {
        var (b, c) = Emits(Dir());
        var v = RecheckRule.Of(DifferentialRows.Credit(b, c), Id(0x801, 0x901));
        Assert.Equal("resolved", v.Word);
        Assert.Equal("repaired", v.ConflictdiffWord);
    }

    [Fact]
    public void A_row_only_in_the_re_run_with_its_predicate_holding_reads_new()
    {
        var (b, c) = Emits(Dir());
        var cr = DifferentialRows.Credit(b, c);
        Assert.Equal("new", RecheckRule.Of(cr, Id(0x804, 0x904)).Word);
        Assert.Equal(new[] { Id(0x804, 0x904) }, RecheckRule.NewRows(cr).Select(r => r.Id));
    }

    [Fact]
    public void A_row_that_vanished_with_its_predicate_still_holding_reads_reclassified_not_resolved()
    {
        var (b, c) = Emits(Dir());
        var cr = DifferentialRows.Credit(b, c);
        Assert.Equal("reclassified", RecheckRule.Of(cr, Id(0x802, 0x902)).Word);
        Assert.Equal("still there", RecheckRule.Of(cr, Id(0x803, 0x903)).Word);
    }

    /// <summary>conflictdiff, run on the same two files, credits what the rule reads: repaired 1, created 1, reclassified 1.
    /// The build is <c>WB_CONFLICTDIFF</c>, else this checkout's Release build.</summary>
    [Fact]
    public void Conflictdiff_on_the_same_emits_counts_what_the_rule_reads()
    {
        var dir = Dir();
        var (b, c) = Emits(dir);
        var exe = Environment.GetEnvironmentVariable("WB_CONFLICTDIFF");
        if (string.IsNullOrEmpty(exe)) exe = TestBuild.Program("conflictdiff");
        Assert.True(File.Exists(exe), $"conflictdiff is not built at {exe}; build the solution first.");
        var psi = new ProcessStartInfo(exe) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        psi.ArgumentList.Add(Path.Combine(dir, "base.json")); psi.ArgumentList.Add(Path.Combine(dir, "changed.json"));
        using var p = Process.Start(psi)!;
        var o = p.StandardOutput.ReadToEndAsync(); var e = p.StandardError.ReadToEndAsync();
        p.WaitForExit();
        File.WriteAllText(Path.Combine(dir, "conflictdiff.txt"), o.Result + e.Result);
        Assert.True(p.ExitCode == 0, $"conflictdiff exited {p.ExitCode}: {o.Result}{e.Result}");
        var cr = DifferentialRows.Credit(b, c);
        int n(CreditOutcome x) => cr.Outcome.Values.Count(v => v == x);
        Assert.Contains($"(repaired {n(CreditOutcome.Repaired)} - created {n(CreditOutcome.Created)} = net", o.Result);
        Assert.Contains($"reclassified {n(CreditOutcome.Reclassified)}, unmeasured {n(CreditOutcome.Unmeasured)})", o.Result);
        Assert.Contains("verdict : DIFFERENTIAL-NEUTRAL", o.Result);
        Assert.Equal((1, 1, 1), (n(CreditOutcome.Repaired), n(CreditOutcome.Created), n(CreditOutcome.Reclassified)));
    }
}
