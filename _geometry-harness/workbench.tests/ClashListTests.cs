using System.Text.Json;
using Workbench;
using Xunit;

namespace WorkbenchTests;

/// <summary>The clash list format (docs/clash-list-format.md): what it reads, and that each wrong file is refused whole
/// in one sentence naming the clash and the field.</summary>
[Trait("tier", "unit")]
public class ClashListTests
{
    static ClashList Parse(string json) => ClashList.Parse(JsonDocument.Parse(json).RootElement, "t.clashes.json");

    static string Refusal(string json) => Assert.Throws<InvalidOperationException>(() => Parse(json)).Message;

    const string Head = "\"format\": \"skywright-clash-list\", \"version\": 1";

    [Fact]
    public void The_documented_example_reads_with_its_title_load_order_and_clashes()
    {
        var docs = Path.Combine(HarnessShared.CheckoutLayout.FindRoot()!, "docs", "clash-list-format.md");
        var text = File.ReadAllText(docs);
        int at = text.IndexOf("```json", StringComparison.Ordinal) + "```json".Length;
        var json = text[at..text.IndexOf("```", at, StringComparison.Ordinal)];
        var list = Parse(json);
        Assert.Equal("Riften mods", list.Title);
        Assert.Equal("X:/Skyrim Conflict Lab/profiles/WB-largest", list.LoadOrder);
        Assert.Equal(2, list.Clashes.Count);
        var first = list.Clashes[0];
        Assert.Equal(new[] { "Riften Expansion.esp", "Unofficial Skyrim Special Edition Patch.esp" }, first.Mods);
        Assert.Equal("000891:Skyrim.esm", first.OpensAt);
        Assert.Equal("high", first.Severity);
        Assert.Equal("00BCF0:Skyrim.esm", list.Clashes[1].OpensAt);       // no record: its cell opens
        Assert.Null(list.Clashes[1].Record);
    }

    [Fact]
    public void A_clash_naming_no_record_or_cell_is_listed_but_opens_nothing()
    {
        var list = Parse($$"""{ {{Head}}, "clashes": [ { "mods": ["A", "B"], "what": "they clash" } ] }""");
        Assert.Null(list.Clashes[0].OpensAt);
        Assert.Contains("does not open", ClashList.Tip(list.Clashes[0]));
        Assert.Equal(0, Parse($$"""{ {{Head}}, "clashes": [] }""").Clashes.Count);
    }

    [Theory]
    [InlineData("""{ "version": 1, "clashes": [] }""", "is not a clash list")]
    [InlineData("""{ "format": "skywright-clash-list", "version": 2, "clashes": [] }""", "version 1")]
    [InlineData("""{ "format": "skywright-clash-list", "version": 1 }""", "no \"clashes\" list")]
    [InlineData("""[1, 2]""", "is not a JSON object")]
    public void A_file_that_is_not_a_version_1_clash_list_is_refused(string json, string says) =>
        Assert.Contains(says, Refusal(json));

    [Theory]
    [InlineData("""{ "mods": ["A"], "what": "x" }""", "clash 1 does not name at least two mods")]
    [InlineData("""{ "mods": ["A", " "], "what": "x" }""", "clash 1 does not name at least two mods")]
    [InlineData("""{ "mods": ["A", "B"] }""", "clash 1 has no \"what\"")]
    [InlineData("""{ "mods": ["A", "B"], "what": "x", "record": "Skyrim.esm" }""", "clash 1 gives \"record\": \"Skyrim.esm\"")]
    [InlineData("""{ "mods": ["A", "B"], "what": "x", "cell": "ZZZZZZ:Skyrim.esm" }""", "clash 1 gives \"cell\"")]
    public void A_wrong_clash_is_refused_naming_the_clash_and_the_field(string clash, string says) =>
        Assert.Contains(says, Refusal($$"""{ {{Head}}, "clashes": [ {{clash}} ] }"""));

    [Fact]
    public void Only_a_file_ending_in_clashes_json_is_recognised()
    {
        var dir = Path.Combine(Path.GetTempPath(), "hc-wb-clash-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var yes = Path.Combine(dir, "mine.clashes.json"); File.WriteAllText(yes, "{}");
            var no = Path.Combine(dir, "mine.json"); File.WriteAllText(no, "{}");
            var r = new ClashListReader();
            Assert.NotNull(r.Recognise(yes));
            Assert.Null(r.Recognise(no));
            Assert.Null(r.Recognise(dir));
        }
        finally { Directory.Delete(dir, true); }
    }
}
