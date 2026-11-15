using HarnessShared;
using Workbench;
using Xunit;

namespace WorkbenchTests;

/// <summary>Item 2: when a stored decision still applies, when it is flagged for reconsideration and why, how rows are
/// keyed, the store, and the list order with decisions.</summary>
[Trait("tier", "unit")]
public class ReviewSpeedDecisionTests : IDisposable
{
    readonly string _root = Path.Combine(Path.GetTempPath(), "hc-wb-decisions-" + Guid.NewGuid().ToString("N"));
    public void Dispose() { try { Directory.Delete(_root, true); } catch { } }

    static readonly RowState Row0 = new("Holds", "box A inside box B", "folder:aaaa");

    static ObjectState Obj(string key, string posX = "100", string version = "44", string? sha = "m1") => new(key, "Subject.esp",
        new[] { new FieldValue("Placement.Position", $"{posX},0,0"), new FieldValue("Base", "000801:HcWbTest.esp"), new FieldValue("FormVersion", version), new FieldValue("VersionControl", "123") },
        "000801:HcWbTest.esp", new[] { new FieldValue("Model.File", "hcwb\\box.nif") },
        "meshes\\hcwb\\box.nif", "HcWbTest - Main.bsa", sha);

    static Decision Stored(params ObjectState[] objs) => new("C:/Instance", "Default", "geometry.object-overlap", "overlap|000a01:hcwbtest.esp|000a02:hcwbtest.esp",
        "intentional", "placed on purpose", DateTimeOffset.Parse("2026-11-15T12:00:00Z"), Row0, objs, "server-1");

    // D2a
    [Fact]
    public void An_unchanged_row_and_unchanged_objects_keep_the_decision()
    {
        var m = Decisions.Compare(Stored(Obj("000A01:HcWbTest.esp"), Obj("000A02:HcWbTest.esp")), Row0,
                                  new[] { Obj("000A01:HcWbTest.esp"), Obj("000A02:HcWbTest.esp") }, "server-1");
        Assert.Equal(Standing.Applies, m.Standing);
    }

    // D2b
    [Fact]
    public void A_changed_record_field_flags_the_decision_naming_the_object_and_the_field()
    {
        var m = Decisions.Compare(Stored(Obj("000A01:HcWbTest.esp"), Obj("000A02:HcWbTest.esp")), Row0,
                                  new[] { Obj("000A01:HcWbTest.esp"), Obj("000A02:HcWbTest.esp", posX: "130") }, "server-1");
        Assert.Equal(Standing.Reconsider, m.Standing);
        Assert.Contains("000A02:HcWbTest.esp", m.Sentence);
        Assert.Contains("Placement.Position", m.Sentence);
    }

    // D2c: bookkeeping alone is a re-save
    [Fact]
    public void A_change_in_bookkeeping_fields_only_keeps_the_decision_and_says_it_was_re_saved()
    {
        Assert.Contains("FormVersion", Decisions.Bookkeeping);
        Assert.Contains("VersionControl", Decisions.Bookkeeping);
        var m = Decisions.Compare(Stored(Obj("000A01:HcWbTest.esp")), Row0, new[] { Obj("000A01:HcWbTest.esp", version: "45") }, "server-1");
        Assert.Equal(Standing.Applies, m.Standing);
        Assert.Contains("re-saved", m.Sentence);
    }

    // D2d: the detector's own row is part of the key, so a mesh, swap or enable change it sees is caught
    [Fact]
    public void A_changed_detector_row_flags_the_decision_even_when_the_records_are_unchanged()
    {
        var now = Row0 with { Physical = "Gone", Summary = "box A clear of box B" };
        var m = Decisions.Compare(Stored(Obj("000A01:HcWbTest.esp")), now, new[] { Obj("000A01:HcWbTest.esp") }, "server-1");
        Assert.Equal(Standing.Reconsider, m.Standing);
        Assert.Contains("detector", m.Sentence);
        var newBuild = Decisions.Compare(Stored(Obj("000A01:HcWbTest.esp")), now with { ProbeBuild = "folder:bbbb" }, new[] { Obj("000A01:HcWbTest.esp") }, "server-1");
        Assert.Equal(Standing.Reconsider, newBuild.Standing);
        Assert.Contains("probe build", newBuild.Sentence);
    }

    // D2e: the mesh is compared by content hash; none known is said, never "unchanged"
    [Fact]
    public void A_changed_mesh_flags_the_decision_and_an_unknown_mesh_hash_is_said()
    {
        var changed = Decisions.Compare(Stored(Obj("000A01:HcWbTest.esp")), Row0, new[] { Obj("000A01:HcWbTest.esp", sha: "m2") }, "server-1");
        Assert.Equal(Standing.Reconsider, changed.Standing);
        Assert.Contains("mesh", changed.Sentence);
        var unknown = Decisions.Compare(Stored(Obj("000A01:HcWbTest.esp", sha: null)), Row0, new[] { Obj("000A01:HcWbTest.esp", sha: null) }, "server-1");
        Assert.Equal(Standing.Applies, unknown.Standing);
        Assert.Contains("mesh not compared", unknown.Sentence);
    }

    // D2f: a reader change is visible
    [Fact]
    public void A_changed_reader_with_changed_fields_says_the_reader_changed_too()
    {
        var m = Decisions.Compare(Stored(Obj("000A01:HcWbTest.esp")), Row0, new[] { Obj("000A01:HcWbTest.esp", posX: "101") }, "server-2");
        Assert.Equal(Standing.Reconsider, m.Standing);
        Assert.Contains("reader", m.Sentence);
    }

    // D2g: FormKeys compare without regard to case
    [Fact]
    public void FormKeys_compare_without_regard_to_case()
    {
        var m = Decisions.Compare(Stored(Obj("000A01:HcWbTest.esp")), Row0, new[] { Obj("000a01:hcwbtest.esp") }, "server-1");
        Assert.Equal(Standing.Applies, m.Standing);
    }

    // D2h: a Findings row is keyed by its check and its own identity
    [Fact]
    public void A_findings_row_is_keyed_by_its_check_and_identity()
    {
        var f = new FindingRow("state.switch-census", "switch", "", "", "summary", null, null, "this check writes a report", "finding", "state.switch-census|Part3");
        Assert.Equal("finding|state.switch-census|part3", Decisions.FindingKey(f));
    }

    // D2i: the store keeps decisions per instance and profile, across reopening
    [Fact]
    public void The_store_keeps_decisions_per_instance_and_profile_across_reopening()
    {
        var path = Path.Combine(_root, "review-decisions.json");
        var d = Stored(Obj("000A01:HcWbTest.esp"));
        new DecisionStore(path).Put(d);
        new DecisionStore(path).Put(d with { Profile = "Other", Verdict = "real problem" });
        var back = new DecisionStore(path);
        Assert.Equal("intentional", back.Find("C:/Instance", "Default", d.RowKey)?.Verdict);
        Assert.Equal("real problem", back.Find("C:/Instance", "Other", d.RowKey)?.Verdict);
        Assert.Null(back.Find("C:/Instance", "Third", d.RowKey));
        Assert.Equal(2, back.All.Count);
        Assert.Equal("intentional", back.Find("c:/instance", "default", d.RowKey.ToUpperInvariant())?.Verdict);
    }

    // D2j: where each kind of row sits
    [Fact]
    public void The_order_is_reconsider_undecided_unsure_real_problem_then_intentional()
    {
        var rows = new[] { ("i", Standing.Applies, "intentional"), ("r", Standing.Applies, "real problem"), ("u", Standing.Undecided, (string?)null),
                           ("s", Standing.Applies, "unsure"), ("c", Standing.Reconsider, "intentional") };
        var ordered = Decisions.Order(rows, x => (x.Item2, x.Item3)).Select(x => x.Item1);
        Assert.Equal(new[] { "c", "u", "s", "r", "i" }, ordered);
    }

    // D2l: a new probe build with the same row keeps the decision
    [Fact]
    public void A_new_probe_build_with_an_unchanged_row_keeps_the_decision_and_says_only_the_build_changed()
    {
        var m = Decisions.Compare(Stored(Obj("000A01:HcWbTest.esp")), Row0 with { ProbeBuild = "folder:cccc" }, new[] { Obj("000A01:HcWbTest.esp") }, "server-1");
        Assert.Equal(Standing.Applies, m.Standing);
        Assert.Contains("probe build", m.Sentence);
        Assert.Contains("nothing else", m.Sentence);
    }

    // D2m: a new reader with the same fields keeps the decision
    [Fact]
    public void A_new_reader_with_unchanged_fields_keeps_the_decision_and_says_only_the_reader_changed()
    {
        var m = Decisions.Compare(Stored(Obj("000A01:HcWbTest.esp")), Row0, new[] { Obj("000A01:HcWbTest.esp") }, "server-2");
        Assert.Equal(Standing.Applies, m.Standing);
        Assert.Contains("reader", m.Sentence);
        Assert.Contains("nothing else", m.Sentence);
    }
}
