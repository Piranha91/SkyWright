using Workbench;
using Xunit;

namespace WorkbenchTests;

/// <summary>The end sentence of a done run counts what the ticked checks did from the bundle's disposition, so a run in
/// which nothing could be measured does not read as a clean result.</summary>
public sealed class RunTallyTests
{
    static string Write(string json)
    {
        var p = Path.Combine(Path.GetTempPath(), "hc-wb-tally-" + Guid.NewGuid().ToString("N") + ".json");
        File.WriteAllText(p, json);
        return p;
    }

    const string AssetOnly = """
        {"evaluation": {"missing": [{"checkId": "geometry.object-overlap", "armId": "candidate-01", "part": "diff"}],
                        "findings": [],
                        "completedNullChecks": [{"checkId": "loadability.masters-and-order", "armId": "baseline"}]}}
        """;

    [Fact]
    public void A_run_where_no_ticked_check_completed_says_nothing_was_measured()
    {
        var t = Launcher.Tally(Write(AssetOnly), new[] { "geometry.object-overlap" });
        Assert.Equal("0 checks found something, 0 checks found nothing, 1 check did not complete, so nothing was measured", t);
    }

    [Fact]
    public void A_partly_measured_run_says_it_is_not_a_full_result()
    {
        var t = Launcher.Tally(Write(AssetOnly), new[] { "geometry.object-overlap", "loadability.masters-and-order" });
        Assert.Equal("0 checks found something, 1 check found nothing, 1 check did not complete, so this is not a full result", t);
    }

    [Fact]
    public void A_check_missing_on_one_arm_is_not_complete_even_with_findings_on_another()
    {
        var p = Write("""
            {"evaluation": {"missing": [{"checkId": "geometry.placement-contact", "armId": "candidate-01", "part": "diff"}],
                            "findings": [{"checkId": "geometry.placement-contact", "armId": "baseline"},
                                         {"checkId": "geometry.object-overlap", "armId": "candidate-01"}],
                            "completedNullChecks": []}}
            """);
        Assert.Equal("1 check found something, 0 checks found nothing, 1 check did not complete, so this is not a full result",
            Launcher.Tally(p, new[] { "geometry.placement-contact", "geometry.object-overlap" }));
    }

    [Fact]
    public void A_complete_run_has_no_qualifier_and_an_unreadable_disposition_is_said()
    {
        var p = Write("""{"evaluation": {"missing": [], "findings": [{"checkId": "a"}], "completedNullChecks": [{"checkId": "b"}]}}""");
        Assert.Equal("1 check found something, 1 check found nothing, 0 checks did not complete", Launcher.Tally(p, new[] { "a", "b" }));
        Assert.StartsWith("what the checks did could not be read from ", Launcher.Tally(Write("not json"), new[] { "a" }));
    }
}
