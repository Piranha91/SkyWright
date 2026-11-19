using Workbench;
using Xunit;

namespace WorkbenchTests;

/// <summary>The Results tab's list rules: which rows the choices list, and what a row's line says without colour.</summary>
[Trait("tier", "unit")]
public sealed class ResultsViewTests
{
    static ResultRow Row(int i, ResultGroup g, string pair = "a-b", int? category = null, string? cell = "000001:M.esp", string summary = "something", string lane = "record") =>
        new(i, g, pair, lane, "Placement", "REFR", $"00000{i}:M.esp", "Placement", null, null, null, category, category is null ? null : "a rule", null, summary,
            Array.Empty<ResultValue>(), new ResultPlace(cell, $"00000{i}:M.esp", null, false, cell is null ? "a NAVI record belongs to no cell, so there is nothing to show" : null),
            "before", Array.Empty<string>(), null);

    static readonly ResultRow[] Rows =
    {
        Row(1, ResultGroup.Addressed), Row(2, ResultGroup.NotAddressed), Row(3, ResultGroup.PatchChange, category: 3, lane: "edit"),
        Row(4, ResultGroup.PatchChange, category: 5, lane: "edit"), Row(5, ResultGroup.NotScored, lane: "overlap"), Row(6, ResultGroup.Addressed, pair: "c-d", summary: "a barrel"),
    };

    static readonly HashSet<ResultGroup> All = new() { ResultGroup.Addressed, ResultGroup.NotAddressed, ResultGroup.PatchChange, ResultGroup.NotScored };

    [Fact]
    public void The_pair_the_groups_and_the_categories_each_narrow_the_list()
    {
        Assert.Equal(6, ResultRules.Select(Rows, null, All, new HashSet<int> { 3, 4, 5 }, "").Count);
        Assert.Equal(5, ResultRules.Select(Rows, "a-b", All, new HashSet<int> { 3, 4, 5 }, "").Count);
        Assert.Equal(new[] { 1, 6 }, ResultRules.Select(Rows, null, new HashSet<ResultGroup> { ResultGroup.Addressed }, new HashSet<int> { 3, 4, 5 }, "").Select(r => r.Index));
        // a category choice narrows patch changes only
        Assert.Equal(new[] { 1, 2, 4, 5, 6 }, ResultRules.Select(Rows, null, All, new HashSet<int> { 5 }, "").Select(r => r.Index));
        Assert.Equal(new[] { 6 }, ResultRules.Select(Rows, null, All, new HashSet<int> { 3, 4, 5 }, "BARREL").Select(r => r.Index));
    }

    [Fact]
    public void A_rows_line_names_its_group_in_a_shape_and_words_and_says_why_it_has_no_view()
    {
        Assert.StartsWith("■ addressed", ResultRules.Line(Rows[0]));
        Assert.StartsWith("□ not addressed", ResultRules.Line(Rows[1]));
        Assert.StartsWith("◆ patch change (3: a new value)", ResultRules.Line(Rows[2]));
        Assert.StartsWith("○ overlap, not scored", ResultRules.Line(Rows[4]));
        Assert.EndsWith("(no view from the results alone: a NAVI record belongs to no cell, so there is nothing to show)", ResultRules.Line(Row(7, ResultGroup.PatchChange, category: 3, cell: null)));
        // the four groups have four different shapes and four different scene marks, and no line says fixed or resolved
        Assert.Equal(4, ResultRules.Groups.Values.Select(g => g.Shape).Distinct().Count());
        Assert.Equal(4, ResultRules.Groups.Values.Select(g => g.Mark).Distinct().Count());
        foreach (var text in ResultRules.Groups.Values.Select(g => g.Words).Append(ResultRules.AddressedMeans).Append(ResultRules.NotScoredMeans))
        {
            Assert.DoesNotContain("fixed", text, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("resolved", text, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Theory]
    [InlineData("Solid", 12)]
    [InlineData("Corners", 24)]
    [InlineData("Crossed", 14)]
    public void Each_mark_style_draws_its_own_shape_on_a_box(string style, int segments)
    {
        var lines = ResultMarks.BoxLines(new(0, 0, 0), new(100, 100, 100), Enum.Parse<MarkStyle>(style)).ToList();
        Assert.Equal(segments, lines.Count);
    }

    [Fact]
    public void A_dashed_box_has_gaps_and_a_flat_object_still_gets_a_box()
    {
        var solid = ResultMarks.BoxLines(new(0, 0, 0), new(240, 240, 240), MarkStyle.Solid).Sum(l => System.Numerics.Vector3.Distance(l.A, l.B));
        var dashed = ResultMarks.BoxLines(new(0, 0, 0), new(240, 240, 240), MarkStyle.Dashed).ToList();
        Assert.True(dashed.Count > 12);
        Assert.InRange(dashed.Sum(l => System.Numerics.Vector3.Distance(l.A, l.B)), solid * 0.4f, solid * 0.7f);
        var flat = ResultMarks.BoxLines(new(0, 0, 5), new(100, 100, 5), MarkStyle.Solid).ToList();
        Assert.Contains(flat, l => Math.Abs(l.A.Z - l.B.Z) >= 3.9f);          // widened to 4 units on its thin axis
    }
}
