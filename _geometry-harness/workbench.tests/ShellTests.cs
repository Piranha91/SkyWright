using HarnessShared;
using Mutagen.Bethesda.Plugins;
using Workbench;
using Xunit;

namespace WorkbenchTests;

/// <summary>The Conflicts tab's order, the Records tree's contents and what a click-through does, on hand-built data.</summary>
[Trait("tier", "unit")]
public class ShellTests
{
    static ConflictRow Row(string id, string a, string b, PhysicalState? ph, string strength, bool? isNew, string? type) =>
        new(id, "geometry.object-overlap", "OVERLAP", a, b, "", "000801:Hc.esp", null, ph, "000900:Hc.esp", type, null, null, null, strength, isNew);

    static SeverityTable Table()
    {
        var p = Path.Combine(Path.GetTempPath(), "hc-wb-sev-" + Guid.NewGuid().ToString("N") + ".json");
        File.WriteAllText(p, """{ "unread": 25, "default": 25, "weights": { "*": { "Door": 90, "Static": 50 } } }""");
        return SeverityTable.Load(p);
    }

    [Fact]
    public void Rows_rank_by_target_then_physical_then_evidence_grade_then_new_then_severity_then_identity()
    {
        const string T = "Subject.esp", O = "Other.esp";
        // Each adjacent pair below is decided by exactly one key; the names say which. The identities are chosen so that
        // identity order alone (key 6) is a different order.
        var rows = new[]
        {
            Row("d-not-target", O, "Third.esp", PhysicalState.Holds, "record-fact", true, "Door"),
            Row("c-gone", T, O, PhysicalState.Gone, "record-fact", true, "Door"),
            Row("y-unmeasured-calibrated", T, O, PhysicalState.Unmeasured, "calibrated-inference", true, "Door"),
            Row("b-unmeasured-record-fact", O, T, null, "record-fact", false, "Static"),
            Row("q-holds-static", T, O, PhysicalState.Holds, "calibrated-inference", false, "Static"),
            Row("a-holds-door", T, O, PhysicalState.Holds, "calibrated-inference", false, "Door"),
            Row("m2-tie", T, O, PhysicalState.Holds, "calibrated-inference", true, "Static"),
            Row("m1-tie", T, O, PhysicalState.Holds, "calibrated-inference", true, "Static"),
        };
        var ranked = ConflictRanking.Rank(rows, new[] { T }, Table());
        var got = ranked.Select(r => r.Row.Identity).ToArray();
        var want = new[] { "m1-tie", "m2-tie", "a-holds-door", "q-holds-static", "b-unmeasured-record-fact",
                           "y-unmeasured-calibrated", "c-gone", "d-not-target" };
        Assert.True(want.SequenceEqual(got), "ranked order: " + string.Join(", ", got));
        Assert.Equal(Enumerable.Range(1, 8), ranked.Select(r => r.Rank));
        Assert.Equal("calibrated-inference", ranked[0].EvidenceLabel);
        Assert.Equal(90, ranked[2].Severity);
    }

    [Fact]
    public void The_shipped_severity_table_loads_and_puts_a_door_above_a_static_above_flora()
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "housecarl.sln"))) root = root.Parent;
        Assert.NotNull(root);
        var t = SeverityTable.Load(Path.Combine(root!.FullName, ".claude", "skills", "world-edit-compatibility", "references", "workbench-severity.json"));
        Assert.True(t.Of("OVERLAP", "Door").Weight > t.Of("OVERLAP", "Static").Weight);
        Assert.True(t.Of("OVERLAP", "Static").Weight > t.Of("OVERLAP", "Flora").Weight);
        Assert.Contains("PROPOSED", t.Status);
    }

    static FormKey K(uint id) => new(new ModKey("Hc", ModType.Plugin), id);

    [Fact]
    public void The_records_tree_holds_every_drawn_ref_under_its_cell_and_marks_the_conflict_refs()
    {
        var cellA = K(0x900); var cellB = K(0x901);
        var drawn = new[]
        {
            new SceneRefEntry(K(0x801), cellA, "Wall"), new SceneRefEntry(K(0x802), cellA, "Crate"),
            new SceneRefEntry(K(0x803), cellB, "Tree"), new SceneRefEntry(K(0x804), null, null),
        };
        var conflicts = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { K(0x802).ToString(), "000999:Far.esp" };
        var unloaded = new Dictionary<string, IReadOnlyList<string>> { ["000777:Far.esp"] = new[] { "000999:Far.esp" } };
        var tree = RecordsTree.Build("worldspace W", drawn, c => c.ToString(), conflicts, unloaded);

        var loadedRefs = RecordsTree.Walk(tree.Take(1)).Where(n => n.Kind == "ref").Select(n => n.Key).OrderBy(x => x).ToArray();
        Assert.Equal(drawn.Select(d => d.Ref.ToString()).OrderBy(x => x).ToArray(), loadedRefs);
        var cellOf = tree[0].Children.SelectMany(c => c.Children.Select(r => (c.Key, r.Key))).ToDictionary(x => x.Item2, x => x.Item1);
        Assert.Equal(cellA.ToString(), cellOf[K(0x801).ToString()]);
        Assert.Equal(cellB.ToString(), cellOf[K(0x803).ToString()]);
        Assert.Equal("", cellOf[K(0x804).ToString()]);
        var marked = RecordsTree.Walk(tree).Where(n => n.Kind == "ref" && n.Conflict).Select(n => n.Key).OrderBy(x => x).ToArray();
        Assert.Equal(new[] { "000999:Far.esp", K(0x802).ToString() }.OrderBy(x => x).ToArray(), marked);
        Assert.False(RecordsTree.Walk(tree).Single(n => n.Key == "000999:Far.esp").Loaded);
    }

    [Fact]
    public void A_reference_is_found_in_the_names_enrichment_whatever_case_its_plugin_is_spelled_in()
    {
        var p = Path.Combine(Path.GetTempPath(), "hc-wb-names-" + Guid.NewGuid().ToString("N") + ".json");
        File.WriteAllText(p, """
            { "refs": { "048C19:HcSubject.esp": { "base": "0ABC01:Skyrim.esm", "carriedBy": ["Skyrim.esm", "HcSubject.esp"],
                                                  "cells": { "Skyrim.esm": "000AAA:Skyrim.esm", "HcSubject.esp": "00927C:Skyrim.esm" } },
                        "00083c:hc patch.esp": { "base": "000003:skyrim.esm" } },
              "bases": { "0ABC01:Skyrim.esm": { "type": "Door", "editorId": "SomeDoor" }, "000003:Skyrim.esm": { "type": "Static" } } }
            """);
        var n = BundleConflicts.Names.Load(p)!;
        var a = n.Ref("048c19:HCSUBJECT.ESP");
        Assert.NotNull(a);
        Assert.Equal("00927C:Skyrim.esm", a!.Value.Cell);           // the winning provider's (last carrier's) cell
        Assert.Equal("Door", a.Value.BaseType);
        Assert.Equal("Static", n.Ref("00083C:Hc Patch.esp")!.Value.BaseType);
    }

    [Fact]
    public void The_dry_run_names_the_parent_cell_and_worldspace_the_patch_carries_in_one_sentence()
    {
        var s = PatchWriter.ParentRecordsSentence(new[] { (K(0x801), (FormKey?)K(0x900)), (K(0x802), (FormKey?)K(0x900)) }, K(0x3C));
        Assert.Contains("cell " + K(0x900), s);
        Assert.Contains("worldspace " + K(0x3C), s);
        Assert.Contains("masks any later edit", s);
        Assert.Single(s.Split(". ", StringSplitOptions.RemoveEmptyEntries));
        var interior = PatchWriter.ParentRecordsSentence(new[] { (K(0x801), (FormKey?)K(0x900)) }, null);
        Assert.DoesNotContain("worldspace", interior);
    }

    [Fact]
    public void A_click_lands_here_switches_cell_or_refuses_in_one_sentence()
    {
        var here = K(0x801);
        var cells = new[] { "000900:Hc.esp", "000901:Hc.esp" };
        bool Drawn(FormKey k) => k == here;
        Assert.Equal(new ClickPlan.Land(here), ClickThrough.Plan(here.ToString(), "000900:Hc.esp", "000900:Hc.esp", cells, 0, Drawn));
        Assert.Equal(new ClickPlan.Switch(K(0x805), 1, "000901:Hc.esp"), ClickThrough.Plan(K(0x805).ToString(), "000901:Hc.esp", "000900:Hc.esp", cells, 0, Drawn));
        var staged = Assert.IsType<ClickPlan.Refuse>(ClickThrough.Plan(K(0x805).ToString(), "000901:Hc.esp", "000900:Hc.esp", cells, 2, Drawn));
        Assert.Contains("2 staged edit(s)", staged.Sentence);
        Assert.IsType<ClickPlan.Refuse>(ClickThrough.Plan(K(0x805).ToString(), null, "000900:Hc.esp", cells, 0, Drawn));
        Assert.IsType<ClickPlan.Refuse>(ClickThrough.Plan(K(0x805).ToString(), "000900:Hc.esp", "000900:Hc.esp", cells, 0, Drawn));
        Assert.IsType<ClickPlan.Refuse>(ClickThrough.Plan(K(0x805).ToString(), "000902:Hc.esp", "000900:Hc.esp", cells, 0, Drawn));
    }
}
