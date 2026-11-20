using Workbench;
using Xunit;

namespace WorkbenchTests;

/// <summary>The end sentence of a done run counts what the ticked checks did from the bundle's disposition, so a run in
/// which nothing could be measured does not read as a clean result, and a check that found something is not read as one
/// that did not complete (FOLD-REVIEW finding 2). The real cases read dispositions the engine wrote (fixtures/dispositions:
/// the fold's mesh-only and texture-only runs, and the runs of fold-review-fix), the machine path in them replaced.</summary>
public sealed class RunTallyTests
{
    static string Write(string json)
    {
        var p = Path.Combine(Path.GetTempPath(), "hc-wb-tally-" + Guid.NewGuid().ToString("N") + ".json");
        File.WriteAllText(p, json);
        return p;
    }

    internal static string Real(string name) => Path.Combine(AppContext.BaseDirectory, "fixtures", "dispositions", name + ".json");

    const string Overlap = "geometry.object-overlap";
    const string NotMeasurable = "; 1 is not complete only because whether it applies to this mod could not be measured, which is not a fault in the mod";

    [Fact]
    public void The_fold_mesh_only_run_found_its_crossing()
    {
        // overlap has row O8 in findings and is listed missing for its unknown applicability: found something, not complete
        Assert.Equal("1 check found something (1 not complete), 0 checks found nothing, 0 checks did not complete, so this is not a full result"
                     + NotMeasurable, Launcher.Tally(Real("fold-meshonly"), new[] { Overlap }));
    }

    [Fact]
    public void The_fold_texture_only_run_found_nothing_and_says_what_it_could_not_judge()
    {
        // review arm candidate-01: base records (strict render geometry) missing_asset 1 (twice, once per part), candidate refs NoGeometry 2
        Assert.Equal("0 checks found something, 1 check found nothing (1 not complete), 0 checks did not complete, so this is not a full result"
                     + "; 1 could not judge every object (base records (strict render geometry) 1, candidate refs 2)" + NotMeasurable,
                     Launcher.Tally(Real("fold-assetonly-tex"), new[] { Overlap }));
    }

    [Theory]
    [InlineData("fix-meshonly")]       // PpMeshMod, no plugin: overlap applies through its mesh reach
    [InlineData("fix-meshonly-esp")]   // PpMeshModEsp, a plugin that places nothing: overlap applies through its mesh reach
    public void A_mesh_only_run_after_the_fix_found_its_crossings_and_is_complete(string name)
    {
        // object overlap has findings on every part and no missing row; the fixture's three bases whose copy is not a NIF are the
        // unjudged members on the review arm (parse_error 3 base records, NoGeometry 3 candidate refs)
        Assert.Equal("1 check found something, 0 checks found nothing, 0 checks did not complete; 1 could not judge every object "
                     + "(base records (strict render geometry) 3, candidate refs 3)", Launcher.Tally(Real(name), new[] { Overlap }));
    }

    const string AssetOnly = """
        {"evaluation": {"missing": [{"checkId": "geometry.object-overlap", "armId": "candidate-01", "part": "diff"}],
                        "findings": [],
                        "completedNullChecks": [{"checkId": "loadability.masters-and-order", "armId": "baseline"}]}}
        """;

    [Fact]
    public void A_run_where_no_ticked_check_has_a_result_says_nothing_was_measured()
    {
        var t = Launcher.Tally(Write(AssetOnly), new[] { Overlap });
        Assert.Equal("0 checks found something, 0 checks found nothing, 1 check did not complete, so nothing was measured", t);
    }

    [Fact]
    public void A_partly_measured_run_says_it_is_not_a_full_result()
    {
        var t = Launcher.Tally(Write(AssetOnly), new[] { Overlap, "loadability.masters-and-order" });
        Assert.Equal("0 checks found something, 1 check found nothing, 1 check did not complete, so this is not a full result", t);
    }

    [Fact]
    public void A_check_with_findings_and_a_missing_part_found_something_but_is_not_complete()
    {
        // corrected (FOLD-REVIEW finding 2): this case used to read "1 check did not complete" for placement contact, whose
        // baseline arm found something
        var p = Write("""
            {"evaluation": {"missing": [{"checkId": "geometry.placement-contact", "armId": "candidate-01", "part": "diff"}],
                            "findings": [{"checkId": "geometry.placement-contact", "armId": "baseline"},
                                         {"checkId": "geometry.object-overlap", "armId": "candidate-01"}],
                            "completedNullChecks": []}}
            """);
        Assert.Equal("2 checks found something (1 not complete), 0 checks found nothing, 0 checks did not complete, so this is not a full result",
            Launcher.Tally(p, new[] { "geometry.placement-contact", Overlap }));
    }

    [Fact]
    public void A_complete_run_has_no_qualifier_and_an_unreadable_disposition_is_said()
    {
        var p = Write("""{"evaluation": {"missing": [], "findings": [{"checkId": "a"}], "completedNullChecks": [{"checkId": "b"}]}}""");
        Assert.Equal("1 check found something, 1 check found nothing, 0 checks did not complete", Launcher.Tally(p, new[] { "a", "b" }));
        Assert.StartsWith("what the checks did could not be read from ", Launcher.Tally(Write("not json"), new[] { "a" }));
    }
}
