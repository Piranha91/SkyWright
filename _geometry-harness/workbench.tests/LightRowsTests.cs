using System.Numerics;
using System.Text.Json;
using System.Text.Json.Nodes;
using Workbench;
using Xunit;

namespace WorkbenchTests;

/// <summary>The lighting check's review rows (environment.lighting, Gate C; run folder C:/hc-runs/2026-11-26-lighting-check): which rows
/// are listed is the registry's per-class admission, never code, and a row opens the view at its own scene and point.</summary>
[Trait("tier", "unit")]
public sealed class LightRowsTests
{
    const string Cell = "0A1B2C:Skyrim.esm", Light = "000802:Lux.esp", Candle = "000801:Skyrim.esm";

    static string Temp(string name, string text)
    {
        var dir = Path.Combine(Path.GetTempPath(), "wb-lightrows-" + Guid.NewGuid().ToString("N")[..10]);
        Directory.CreateDirectory(dir);
        var p = Path.Combine(dir, name);
        File.WriteAllText(p, text);
        return p;
    }

    /// <summary>The registry's lighting row (the real one, from data/check-registry.json), optionally with one class's admission flipped.</summary>
    static string Registry(string? flip = null, string? whenItLightsNothing = null)
    {
        var reg = JsonNode.Parse(File.ReadAllText(Path.Combine(ReviewSpeedPart2Tests.RepoRoot(), "data", "check-registry.json")))!;
        if (whenItLightsNothing is not null)
            reg["checks"]!.AsArray().First(c => (string?)c!["id"] == LightRows.CheckId)!["classes"]!.AsArray()
               .First(c => (string?)c!["id"] == "light-lights-nothing")!["whenItLightsNothing"] = whenItLightsNothing;
        if (flip is not null)
        {
            var row = reg["checks"]!.AsArray().First(c => (string?)c!["id"] == LightRows.CheckId)!;
            var cls = row["classes"]!.AsArray().First(c => (string?)c!["id"] == flip)!;
            bool now = !(bool)cls["admitted"]!;
            cls["admitted"] = now;
            cls["held"] = now ? null : "flipped by the test";
        }
        return Temp("check-registry.json", reg.ToJsonString());
    }

    static object Row(string cls, string verdict, string subject, string summary, string kind = "interior", int? x = null, int? y = null,
                      float[]? fixtureAt = null, string? cell = Cell, object? readings = null) => new
    {
        @class = cls, verdict, scene = "MorthalThonnirsHouse", sceneId = kind == "interior" ? Cell : "00003C:Skyrim.esm@-11,23", kind,
        space = kind == "interior" ? Cell : "00003C:Skyrim.esm", x, y, cell, subject, subjectEdid = "LuxLight01", fixture = Candle, fixtureEdid = "CandleHornTable01",
        subjectMod = new { mod = "Lux", plugin = "Lux.esp" }, otherMods = new[] { new { mod = "Cities of the North - Morthal", plugin = "COTN - Morthal.esp" } },
        summary, reason = verdict == "not judged" ? summary : null, at = new[] { 100f, 200f, 30f }, fixtureAt, readings = readings ?? new { },
    };

    /// <summary>One row of each kind the reader sorts: listed in each class, not judged, a whole scene not judged, the over-limit count.</summary>
    static string Output() => Temp("candidate-01.json", JsonSerializer.Serialize(new
    {
        identity = new { schema = "skywright.light-check/1" },
        rows = new object[]
        {
            Row("light-no-fixture", "listed", Light, "Lux's light LuxLight01 sits at CandleHornTable01, which Cities of the North - Morthal removes; the light now glows where nothing burns.", fixtureAt: new[] { 100f, 200f, 10f }),
            Row("fixture-no-light", "listed", Candle, "Skyrim.esm's CandleHornTable01 is lit by Lux's light in its own setup, and Cities of the North - Morthal disables, removes or moves that light; it now burns without lighting anything."),
            Row("light-behind-wall", "listed", "000803:Lux.esp", "Lux's light L2 now sits behind Cities of the North - Morthal's wall."),
            Row("light-lights-nothing", "listed", "000804:Lux.esp", "Lux's light L3 lights nothing with Cities of the North - Morthal installed."),
            Row("fixture-lit-twice", "listed", "000805:Skyrim.esm", "Skyrim.esm's candle is lit by a light from both sides."),
            Row("light-behind-wall", "not judged", "000806:Lux.esp", "Not judged: too few surfaces around Lux's light L4 to tell which side of a wall it is on (3 of 64 checks hit something); look at it in game."),
            Row("over-light-limit", "cannot tell", "000807:Skyrim.esm", "Cannot tell: 9 lights reach the floor."),
            Row(LightRows.SceneClass, "not judged", "", "Not judged: a Base Object Swapper file changes 000900:Skyrim.esm in this scene (combined arrangement), and this check does not apply Base Object Swapper; look at the scene in game.", cell: null),
        },
    }));

    static (List<ConflictRow> Rows, List<string> Lines) Read(string registry) => LightRows.Read(Output(), LightRows.Classes(registry), "calibrated-inference");

    [Fact]
    public void The_registry_decides_which_classes_are_listed_and_held_classes_are_counted_with_their_reason()
    {
        // rejects: admission decided in code (a held class's rows listed as clashes), a held class dropped silently, the over-limit count
        // listed as a clash, or a not-judged row (a scene's included) hidden
        var (rows, lines) = Read(Registry());
        // the lights-nothing row is counted, not listed, at the registry's default (the owner's preliminary ruling, 2026-11-26)
        Assert.Equal(new[] { "A light left with no fixture", "A light behind a wall", "Not judged: a light behind a wall", "A scene not judged" }, rows.Select(r => r.Kind));
        Assert.Contains(lines, l => l.StartsWith("1 light(s) that now reach nothing were left out"));
        Assert.All(rows, r => Assert.Equal(LightRows.CheckId, r.CheckId));
        Assert.Contains(lines, l => l.StartsWith("held: a fixture left with no light (1), not shown as clashes; it waits on: no case of it has been checked in the game yet"));
        Assert.Contains(lines, l => l.StartsWith("held: a fixture lit twice (1)"));
        Assert.Contains(lines, l => l.StartsWith("cannot tell: 1 surface(s)"));
        Assert.DoesNotContain(rows, r => r.Summary.StartsWith("Cannot tell"));
        var scene = Assert.Single(rows, r => r.Light!.Class == LightRows.SceneClass);
        Assert.StartsWith("Not judged: a Base Object Swapper file", scene.Summary);
    }

    [Theory]
    [InlineData("fixture-no-light")]
    [InlineData("light-no-fixture")]
    public void Flipping_one_class_admission_moves_only_its_rows_between_listed_and_held(string flip)
    {
        // rejects: a flip that changes another class's rows, or rows that stay put when the data says otherwise
        var before = Read(Registry());
        var after = Read(Registry(flip));
        var moved = before.Rows.Concat(after.Rows).Where(r => r.Light!.Class == flip && r.Light.Verdict == "listed").Select(r => r.Identity).Distinct().ToList();
        Assert.Single(moved);
        string Key(ConflictRow r) => r.Identity + "|" + r.Summary + "|" + r.Cell + "|" + r.RefKey + "|" + r.OtherKey;
        var keep = before.Rows.Where(r => !moved.Contains(r.Identity)).Select(Key).ToList();
        Assert.Equal(keep, after.Rows.Where(r => !moved.Contains(r.Identity)).Select(Key).ToList());
        bool wasListed = before.Rows.Any(r => moved.Contains(r.Identity));
        Assert.Equal(!wasListed, after.Rows.Any(r => moved.Contains(r.Identity)));
        var heldLine = (wasListed ? after.Lines : before.Lines).Single(l => l.StartsWith("held: ") && l.Contains("(1)") && l.Contains(flip == "light-no-fixture" ? "a light left with no fixture" : "a fixture left with no light"));
        Assert.DoesNotContain(heldLine, wasListed ? before.Lines : after.Lines);
        Assert.Equal(before.Lines.Where(l => l != heldLine), after.Lines.Where(l => l != heldLine));
    }

    [Fact]
    public void A_row_opens_at_its_light_in_its_cell_with_the_gone_fixture_marked()
    {
        // rejects: a lighting row opened on the fixture key, in no cell, or without the marker for the removed candle
        var row = Read(Registry()).Rows.First(r => r.Light!.Class == "light-no-fixture");
        var t = LightRows.Target(row);
        Assert.Null(t.WhyNot);
        Assert.Equal(Light, t.RefKey);
        Assert.Equal(Cell, t.Cell);
        Assert.Equal(new Vector3(100, 200, 30), t.At);
        Assert.Equal(new Vector3(100, 200, 10), t.FixtureAt);
        var wall = LightRows.Target(Read(Registry()).Rows.First(r => r.Light!.Class == "light-behind-wall" && r.Light.Verdict == "listed"));
        Assert.Null(wall.FixtureAt);   // nothing gone, nothing marked
    }

    [Fact]
    public void An_exterior_row_names_its_worldspace_and_grid_and_a_scene_row_says_why_it_does_not_open()
    {
        // rejects: an exterior row with no grid, or a whole-scene row that opens on nothing without saying so
        var ext = Temp("candidate-01.json", JsonSerializer.Serialize(new
        {
            identity = new { schema = "skywright.light-check/1" },
            rows = new object[] { Row("light-no-fixture", "listed", Light, "s", kind: "exterior", x: -11, y: 23, cell: "00ABCD:Skyrim.esm") },
        }));
        var t = LightRows.Target(LightRows.Read(ext, LightRows.Classes(Registry()), "x").Rows.Single());
        Assert.Equal(("00003C:Skyrim.esm", (-11, 23), "00ABCD:Skyrim.esm"), (t.Worldspace, t.Grid!.Value, t.Cell));
        var scene = LightRows.Target(Read(Registry()).Rows.Single(r => r.Light!.Class == LightRows.SceneClass));
        Assert.Contains("whole scene that was not judged", scene.WhyNot);
    }

    /// <summary>Finding 4's shape: two stranded lights that reach surfaces (10 and 40 of 64 hits) and one that also lights nothing.</summary>
    static string StrandedOutput() => Temp("candidate-01.json", JsonSerializer.Serialize(new
    {
        identity = new { schema = "skywright.light-check/1" },
        rows = new object[]
        {
            Row("light-no-fixture", "listed", "000810:Lux.esp", "L10 sits at a candle, which COTN removes; 10 of 64 checks from the light hit a surface.", readings: new { hits = 10 }),
            Row("light-no-fixture", "listed", Light, "L sits at CandleHornTable01, which COTN removes; 0 of 64 checks from the light hit a surface; it reaches very little.",
                fixtureAt: new[] { 1f, 2f, 3f }, readings: new { hits = 0, alsoLightsNothing = true }),
            Row("light-no-fixture", "listed", "000840:Lux.esp", "L40 sits at a candle, which COTN removes; 40 of 64 checks from the light hit a surface.", readings: new { hits = 40 }),
            Row("light-lights-nothing", "listed", Light, "Lux's light L lights nothing with COTN installed: no surface is in its reach. Harmless to look at; safe to disable.",
                fixtureAt: new[] { 1f, 2f, 3f }, readings: new
                {
                    strandedSummary = "Lux's light L sat at CandleHornTable01, which Cities of the North - Morthal removes; with Cities of the North - Morthal installed it lights nothing: no surface is in its reach. Harmless to look at; safe to disable.",
                    strandedOtherMods = new[] { new { mod = "Cities of the North - Morthal", plugin = "COTN - Morthal.esp" } },
                }),
        },
    }));

    [Fact]
    public void A_light_that_reaches_nothing_is_one_entry_counted_or_listed_as_the_registry_says()
    {
        // finding 4 and the owner's preliminary ruling; rejects: the same light twice, the fixture fact lost from its entry, the setting
        // decided in code, a counted light silently dropped, or a flip that changes anything but those rows; stranded lights that reach
        // more surfaces first
        var count = LightRows.ReadAll(StrandedOutput(), LightRows.Classes(Registry()), "x");
        Assert.Equal(new[] { "000840:Lux.esp", "000810:Lux.esp" }, count.Rows.Select(r => r.RefKey));
        var entry = Assert.Single(count.Counted);
        Assert.Contains("which Cities of the North - Morthal removes", entry.Summary);
        Assert.Contains("safe to disable", entry.Summary);
        Assert.Equal(new Vector3(1, 2, 3), entry.Light!.FixtureAt);
        Assert.Contains(count.Lines, l => l.StartsWith("1 light(s) that now reach nothing were left out") && l.Contains("1 of them also left with no fixture"));

        var list = LightRows.ReadAll(StrandedOutput(), LightRows.Classes(Registry(whenItLightsNothing: "list")), "x");
        Assert.Empty(list.Counted);
        var listed = Assert.Single(list.Rows, r => r.Light!.Class == "light-lights-nothing");
        Assert.Equal(entry.Summary, listed.Summary);   // the same entry, now listed
        Assert.Equal(count.Rows.Select(r => r.Identity + r.Summary), list.Rows.Where(r => r.Light!.Class != "light-lights-nothing").Select(r => r.Identity + r.Summary));
        Assert.Contains(list.Lines, l => l.Contains("listed once, under a light that lights nothing"));
        Assert.DoesNotContain(list.Lines, l => l.Contains("were left out"));
    }
}
