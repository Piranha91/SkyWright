using HarnessShared;
using Workbench;
using Xunit;

namespace WorkbenchTests;

/// <summary>The two lost-edit checks in the app (record checks unit 2): each Findings row keeps its own sentence when one
/// record carries several (one row per field and losing plugin), what the row names, and the held tiles' lines and help.
/// The sentence itself is recordprobe's (its LE22).</summary>
[Trait("tier", "unit")]
public sealed class LostEditWordsTests
{
    const string Rec = "000801:Skyrim.esm";

    static Conflict Row(string loser, string field, string sentence) =>
        new("LOST-EDIT", loser, "Other.esp", sentence,
            new[] { new ConflictRecord(Rec, "record", null, PhysicalState.Holds, $"its {field} field") },
            new[] { new ConflictRecord($"{Rec}#{field}#{loser}", "field", field) });

    [Fact]
    public void Each_field_and_losing_plugin_of_one_record_is_its_own_row_with_its_own_sentence_naming_that_record()
    {
        var file = new ConflictFile("recordprobe", @"X:\inst", "2026-11-27 00:00:00Z", new[]
        {
            Row("Base.esp", "Placement", "Base.esp (mod Base Mod) changed the Placement of the placed object 000801 in Skyrim.esm; Other.esp (mod Other Mod) wins and was not made with Base.esp, so the change is lost in game."),
            Row("Base.esp", "Scale", "Base.esp (mod Base Mod) changed the Scale of the placed object 000801 in Skyrim.esm to 2; Other.esp (mod Other Mod) wins with 1 and was not made with Base.esp, so the change is lost in game."),
            Row("Mid.esp", "Placement", "Mid.esp (mod Mid Mod) changed the Placement of the placed object 000801 in Skyrim.esm; Other.esp (mod Other Mod) wins and was not made with Mid.esp, so the change is lost in game."),
        }, null, null, null, "load-time", "baseline");
        var rows = DifferentialRows.Of(file);
        Assert.Equal(3, rows.Select(r => r.Id).Distinct(StringComparer.OrdinalIgnoreCase).Count());
        Assert.All(rows, r => Assert.Equal(Rec, r.FormKey));
        var said = BundleConflicts.SummariesById(file);
        Assert.Equal(file.Conflicts.Select(c => c.Summary), rows.Select(r => said[r.Id]));
    }

    [Fact]
    public void Both_checks_are_held_with_their_own_reason_a_plain_line_and_help()
    {
        foreach (var id in new[] { "records.contested-fields", "records.cell-attributes" })
        {
            var c = Assert.Single(Launcher.Registry(), x => x.Id == id);
            Assert.True(c.Held);
            Assert.Equal("probe-emit", c.Kind);
            Assert.Equal("it is held back until a person has read the rows it found on the lab's load order", c.HeldReason);
            Assert.Equal(c.HeldReason, new Launcher().WhyNotHere(c));
            Assert.Contains("not made with it", ProbeNames.Says(id));
            var help = ProbeHelp.Of(id);
            Assert.NotNull(help);
            Assert.Contains("made with", help!.What + help.How);
        }
    }
}
