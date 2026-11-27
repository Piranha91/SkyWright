using SkyWright.Engine;
using Workbench;
using Xunit;

namespace WorkbenchTests;

/// <summary>The stranded check's rows in a modder's words (StrandedWords), every not-judged reason the rule gives, and the
/// held tile: a check the registry marks opt-in is greyed with one sentence, read from the field.</summary>
[Trait("tier", "unit")]
public sealed class StrandedWordsTests
{
    // words a modder should never meet in the rows or the tile
    static readonly string[] Banned = { "own view", "form", "optIn", "opt-in", "bound", "emit", "predicate", "STRANDED", "Unmeasured", "Holds", "winning" };

    static void Plain(string text)
    {
        foreach (var b in Banned)
            Assert.False(System.Text.RegularExpressions.Regex.IsMatch(text, $@"\b{System.Text.RegularExpressions.Regex.Escape(b)}\b"), $"'{b}' in: {text}");
    }

    // strandprobe's summary shapes (StrandEmit.Summary)
    static string Reported(string cls) =>
        $"Light Lux_NorthFakeFlicker from Lux.esp in MorthalAlvasHouse: 612.8 u from walkable ground with COTN - Morthal.esp's navmesh, 99.9 u with the navmesh its mod was made with; {cls}";
    static string NotJudged(string why) => $"Static SfStatic from SfA.esp in SfRoom01: not judged, {why}";

    [Fact]
    public void A_reported_row_names_the_object_the_two_mods_the_room_and_the_distances_in_units()
    {
        var s = StrandedWords.Sentence(StrandedWords.CheckId, Reported("decoration (light)"))!;
        Assert.Equal("A light from Lux.esp is out of reach in MorthalAlvasHouse: with COTN - Morthal.esp's navmesh it is 613 units from anywhere a character "
                     + "can walk, and 100 units with the navmesh Lux.esp was made for. Nothing of another mod is near it. It is a light.", s);
        Plain(s);
    }

    [Theory]
    [InlineData("scripted or quest object (demands remediation)", "the player may need to reach it")]
    [InlineData("AI marker", "Characters use it")]
    [InlineData("decoration", "It is decoration.")]
    [InlineData("decoration (light)", "It is a light.")]
    public void Each_class_has_its_sentence(string cls, string says)
    {
        var s = StrandedWords.Sentence(StrandedWords.CheckId, Reported(cls))!;
        Assert.Contains(says, s);
        Plain(s);
    }

    static NavDistance M(double d) => new("measured", null, d, 0, d, "000800:SfMaster.esm", 0);
    static readonly NavDistance Refused = new("refused", "no navmesh triangle in the cells searched", null, null, null, null, null);
    static readonly StrandedClearance Far = new(true, 400, "SfWall (000801:SfC.esp)", null, null);

    /// <summary>Every not-judged path of StrandedRule.Decide, with its sentence in the app.</summary>
    public static IEnumerable<object[]> Reasons() => new[]
    {
        new object[] { new StrandedFacts(false, M(300), M(100), Far, "its model file could not be found", "Static", "X", false), "Its shape could not be read" },
        new object[] { new StrandedFacts(false, Refused, M(100), Far, null, "Static", "X", false), "Its distance from walkable ground could not be measured" },
        new object[] { new StrandedFacts(false, M(300), M(100), new(false, null, null, null, null), null, "Static", "X", false), "No other mod's solid object" },
        new object[] { new StrandedFacts(false, M(300), M(100), new(true, 0, "SfWall (000801:SfC.esp)", null, null), null, "Static", "X", false), "It touches SfWall" },
        new object[] { new StrandedFacts(false, M(300), M(100), new(true, 400, "SfWall", 50, "SfBox (000802:SfC.esp)"), null, "Static", "X", false), "could not be settled" },
        new object[] { new StrandedFacts(false, M(160), M(100), Far, null, "Static", "X", false), "within 12 u of the 150 u reach" },
    };

    [Theory]
    [MemberData(nameof(Reasons))]
    public void Each_not_judged_reason_reads_as_a_sentence(StrandedFacts f, string says)
    {
        var v = StrandedRule.Decide(f);
        Assert.Equal(StrandedState.NotJudged, v.State);
        var s = StrandedWords.Sentence(StrandedWords.CheckId, NotJudged(v.Reason))!;
        Assert.StartsWith("A static from SfA.esp in SfRoom01: not judged. ", s);
        Assert.Contains(says, s, StringComparison.OrdinalIgnoreCase);
        Plain(s);
    }

    [Fact]
    public void Another_checks_row_or_an_unknown_shape_gets_no_sentence()
    {
        Assert.Null(StrandedWords.Sentence("geometry.object-overlap", Reported("decoration")));
        Assert.Null(StrandedWords.Sentence(StrandedWords.CheckId, "something else"));
    }

    [Fact]
    public void A_check_the_registry_holds_is_greyed_with_one_plain_sentence_read_from_the_field()
    {
        var held = Launcher.Registry().Where(c => c.Held).ToList();
        Assert.Contains(held, c => c.Id == StrandedWords.CheckId);
        var l = new Launcher();
        foreach (var c in held) Assert.Equal(Launcher.HeldWhy, l.WhyNotHere(c));
        // the same check not held is not greyed: the field decides, not the id
        Assert.Null(l.WhyNotHere(held[0] with { Held = false }));
        Plain(Launcher.HeldWhy);
        Assert.Single(System.Text.RegularExpressions.Regex.Matches(Launcher.HeldWhy + ".", @"[.!?](\s|$)"));
    }
}
