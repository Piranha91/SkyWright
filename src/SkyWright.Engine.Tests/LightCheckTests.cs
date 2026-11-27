using System.Numerics;
using HousecarlCore;
using Xunit;

namespace SkyWright.Engine.Tests;

/// <summary>Known answers for the lighting check (<see cref="LightCheck"/>, <see cref="LightCheckSceneIndex"/>): each case builds the four
/// arrangements of one scene by hand (D the dependencies, S the selection over them, R the rest over them, C the combination) and names
/// the wrong answer it rejects. Case ids follow the Gate B plan (run folder C:/hc-runs/2026-11-26-lighting-check, GATE-A.md section 8 and
/// GATE-A-REVIEW.md's findings).</summary>
[Trait("tier", "unit")]
public sealed class LightCheckTests
{
    const string Base = "Skyrim.esm", Sel = "SelMod.esp", RestB = "RestB.esp", RestC = "RestC.esp";
    const string Scene = "TestCell";

    static LightCheckSides Sides() => new(
        new Dictionary<string, LightSide> { [Base] = LightSide.Base, [Sel] = LightSide.Selection, [RestB] = LightSide.Rest, [RestC] = LightSide.Rest },
        new Dictionary<string, string> { [Sel] = "Selected Mod", [RestB] = "Rest Mod B", [RestC] = "Rest Mod C" });

    static LightCheckReading D() => new(new[] { Base });
    static LightCheckReading S() => new(new[] { Base, Sel });
    static LightCheckReading R() => new(new[] { Base, RestC, RestB });
    static LightCheckReading C() => new(new[] { Base, Sel, RestC, RestB });

    static string K(int id, string plugin) => $"{id:X6}:{plugin}";

    /// <summary>A candle: a Static whose one emissive part (2 u box) sits 10 u above where it stands.</summary>
    static LightCheckRef Candle(string key, Vector3 at, bool on = true, bool deleted = false, params string[] touchers) =>
        new(key, "Static", "CandleHornTable01", true, at, 1, on && !deleted)
        {
            Deleted = deleted, Obnd = new Vector3(10, 10, 20), Touchers = touchers.Length > 0 ? touchers : new[] { LightCheck.PluginOf(key) },
            Emitters = new[] { new LightCheckEmitter("Flame", "emissive", 0.5, at + new Vector3(-1, -1, 9), new Vector3(2, 0, 0), new Vector3(0, 2, 0), new Vector3(0, 0, 2)) },
        };

    static LightCheckRef Solid(string key, Vector3 at, bool on = true, params string[] touchers) =>
        new(key, "Static", "WallStone01", true, at, 1, on) { Obnd = new Vector3(200, 20, 200), Touchers = touchers.Length > 0 ? touchers : new[] { LightCheck.PluginOf(key) } };

    /// <summary>Add a light to a reading: its reference (model-less) and its light reading (by default in an open room: 60 front hits).</summary>
    static LightCheckReading Light(LightCheckReading a, string key, Vector3 at, bool enabled = true, FacingResult? facing = null, double lit = 100,
                                   float radius = 256, string[]? touchers = null, IReadOnlyDictionary<string, int>? back = null,
                                   IReadOnlyDictionary<string, double>? litBy = null)
    {
        a.Add(new LightCheckRef(key, "Light", "TestLight01", false, at, 1, enabled) { Touchers = touchers ?? new[] { LightCheck.PluginOf(key) } });
        a.Add(new LightCheckLight(key, "TestLight01", enabled, at, radius, false)
        {
            Facing = facing ?? new FacingResult(64, 60, 0, 4), Lit = lit, BackByRef = back ?? new Dictionary<string, int>(),
            LitByRef = litBy ?? new Dictionary<string, double>(),
        });
        return a;
    }

    static LightCheckSceneResult Eval(LightCheckReading d, LightCheckReading s, LightCheckReading r, LightCheckReading c, bool exterior = false) =>
        LightCheck.Evaluate(Scene, exterior, d, s, r, c, Sides());

    static List<LightCheckRow> Rows(LightCheckSceneResult res, string cls) => res.Rows.Where(x => x.Class == cls).ToList();

    // ---------------------------------------------------------------- a light left with no fixture

    static readonly Vector3 At0 = Vector3.Zero, Above = new(0, 0, 20);

    [Fact]
    public void KP1_other_side_removes_the_candle_lists_the_light_and_names_that_mod()
    {
        // rejects: a host looked for in the combination (where the candle is gone, so the light reads free-standing and no row)
        string candle = K(0x801, Base), light = K(0x802, Sel);
        var d = D().Add(Candle(candle, At0));
        var s = Light(S().Add(Candle(candle, At0)), light, Above);
        var r = R().Add(Candle(candle, At0, deleted: true, touchers: new[] { Base, RestB }));
        var c = Light(C().Add(Candle(candle, At0, deleted: true, touchers: new[] { Base, RestB })), light, Above);
        var rows = Rows(Eval(d, s, r, c), LightCheck.LightNoFixture);
        var row = Assert.Single(rows);
        Assert.Equal("listed", row.Verdict);
        Assert.Equal(light, row.Subject);
        Assert.Equal(candle, row.Fixture);
        Assert.Equal(new LightCheckMod("Selected Mod", Sel), row.SubjectMod);
        Assert.Equal(new LightCheckMod("Rest Mod B", RestB), Assert.Single(row.OtherMods));
        Assert.Contains("Rest Mod B removes", row.Summary);
    }

    [Fact]
    public void KP1_rest_holds_two_mods_and_names_only_the_one_whose_record_removed_the_candle()
    {
        // finding 6; rejects: naming the first other-side mod with a record of the candle (Rest Mod C's identical copy, which changes nothing)
        string candle = K(0x801, Base), light = K(0x802, Sel);
        var gone = new[] { Base, RestC, RestB };
        var d = D().Add(Candle(candle, At0));
        var s = Light(S().Add(Candle(candle, At0)), light, Above);
        var r = R().Add(Candle(candle, At0, deleted: true, touchers: gone));
        var c = Light(C().Add(Candle(candle, At0, deleted: true, touchers: gone)), light, Above);
        var row = Assert.Single(Rows(Eval(d, s, r, c), LightCheck.LightNoFixture));
        Assert.Equal(RestB, Assert.Single(row.OtherMods).Plugin);
    }

    [Fact]
    public void KP1_a_replacement_candle_at_the_spot_keeps_the_light_hosted()
    {
        // finding 1; rejects: "stranded" from the original host alone, when the other mod put its own burning candle in the same place
        string candle = K(0x801, Base), light = K(0x802, Sel), theirs = K(0x900, RestB);
        var d = D().Add(Candle(candle, At0));
        var s = Light(S().Add(Candle(candle, At0)), light, Above);
        var r = R().Add(Candle(candle, At0, deleted: true, touchers: new[] { Base, RestB })).Add(Candle(theirs, At0));
        var c = Light(C().Add(Candle(candle, At0, deleted: true, touchers: new[] { Base, RestB })).Add(Candle(theirs, At0)), light, Above);
        var res = Eval(d, s, r, c);
        Assert.Empty(Rows(res, LightCheck.LightNoFixture));
        Assert.Equal(1, res.Counts[LightCheck.LightNoFixture].Subjects);
        Assert.Equal(1, res.Counts[LightCheck.LightNoFixture].NoClash);
    }

    [Fact]
    public void KP1_the_light_is_stranded_by_one_side_alone_so_it_is_not_the_pair()
    {
        // rejects: attribution that ignores the single arrangements (the selection alone already strands the base game's light)
        string candle = K(0x801, Base), light = K(0x803, Base);
        var d = Light(D().Add(Candle(candle, At0)), light, Above);
        var s = Light(S().Add(Candle(candle, At0, deleted: true, touchers: new[] { Base, Sel })), light, Above);
        var r = Light(R().Add(Candle(candle, At0)), light, Above);
        var c = Light(C().Add(Candle(candle, At0, deleted: true, touchers: new[] { Base, Sel })), light, Above);
        var res = Eval(d, s, r, c);
        Assert.Empty(Rows(res, LightCheck.LightNoFixture));
        Assert.Equal(1, res.Counts[LightCheck.LightNoFixture].NotThePair);
    }

    [Fact]
    public void KP1_an_unreadable_model_by_the_light_is_not_judged()
    {
        // rejects: an undecided host folded into "no clash"
        string obj = K(0x804, Base), light = K(0x805, Base);
        LightCheckRef Unread() => new(obj, "Static", "MysteryLamp01", true, At0, 1, true) { Obnd = new Vector3(20, 20, 20), EmitterStatus = "refused: missing_asset: meshes\\x.nif", Touchers = new[] { Base } };
        var d = Light(D().Add(Unread()), light, Above);
        var s = Light(S().Add(Unread()), light, Above);
        var r = Light(R().Add(Unread()), light, Above);
        var c = Light(C().Add(Unread()), light, Above);
        var res = Eval(d, s, r, c);
        var row = Assert.Single(Rows(res, LightCheck.LightNoFixture));
        Assert.Equal("not judged", row.Verdict);
        Assert.Contains("could not be read", row.Reason);
        Assert.Equal(1, res.Counts[LightCheck.LightNoFixture].NotJudged);
    }

    [Fact]
    public void KAI_a_mod_that_swaps_the_base_light_for_its_own_lists_nothing()
    {
        // author intent: a light its own mod switches off is not lit in its own arrangement; rejects a row for a deliberate replacement
        string candle = K(0x871, Base), old = K(0x870, Base), mine = K(0x872, Sel), rug = K(0x873, RestB);
        var d = Light(D().Add(Candle(candle, At0)), old, Above);
        var s = Light(Light(S().Add(Candle(candle, At0)), old, Above, enabled: false, touchers: new[] { Base, Sel }), mine, new Vector3(0, 5, 20));
        var r = Light(R().Add(Candle(candle, At0)).Add(Solid(rug, new Vector3(500, 0, 0))), old, Above);
        var c = Light(Light(C().Add(Candle(candle, At0)).Add(Solid(rug, new Vector3(500, 0, 0))), old, Above, enabled: false, touchers: new[] { Base, Sel }),
                      mine, new Vector3(0, 5, 20));
        var res = Eval(d, s, r, c);
        Assert.DoesNotContain(res.Rows, x => x.Verdict == "listed");
    }

    // ---------------------------------------------------------------- a fixture left with no light

    [Fact]
    public void KP1b_the_other_side_disables_the_light_that_lit_the_fixture()
    {
        // rejects: the fixture read in its own arrangement only, or the light's mod named instead of the one that switched it off
        string fx = K(0x810, Sel), light = K(0x811, Base);
        var at = new Vector3(100, 0, 0); var above = at + new Vector3(0, 0, 20);
        var d = Light(D(), light, above);
        var s = Light(S().Add(Candle(fx, at)), light, above);
        var r = Light(R(), light, above, enabled: false, touchers: new[] { Base, RestB });
        var c = Light(C().Add(Candle(fx, at)), light, above, enabled: false, touchers: new[] { Base, RestB });
        var row = Assert.Single(Rows(Eval(d, s, r, c), LightCheck.FixtureNoLight));
        Assert.Equal("listed", row.Verdict);
        Assert.Equal(fx, row.Subject);
        Assert.Equal(RestB, Assert.Single(row.OtherMods).Plugin);
    }

    [Fact]
    public void KP1b_the_rest_alone_already_leaves_the_base_candle_unlit_so_it_is_not_the_pair()
    {
        string fx = K(0x820, Base), light = K(0x821, Base);
        var at = new Vector3(200, 0, 0); var above = at + new Vector3(0, 0, 20);
        var d = Light(D().Add(Candle(fx, at)), light, above);
        var s = Light(S().Add(Candle(fx, at)), light, above);
        var r = Light(R().Add(Candle(fx, at)), light, above, enabled: false, touchers: new[] { Base, RestB });
        var c = Light(C().Add(Candle(fx, at)), light, above, enabled: false, touchers: new[] { Base, RestB });
        var res = Eval(d, s, r, c);
        Assert.Empty(Rows(res, LightCheck.FixtureNoLight));
        Assert.Equal(1, res.Counts[LightCheck.FixtureNoLight].NotThePair);
    }

    // ---------------------------------------------------------------- a light behind a wall; a light that lights nothing

    [Fact]
    public void KP2_the_other_sides_wall_puts_the_light_behind_it()
    {
        // rejects: no row (the class missing), or the wall's mod not named
        string light = K(0x830, Sel), wall = K(0x831, RestB);
        var s = Light(S(), light, At0);
        var r = R().Add(Solid(wall, new Vector3(50, 0, 0)));
        var c = Light(C().Add(Solid(wall, new Vector3(50, 0, 0))), light, At0, facing: new FacingResult(64, 10, 40, 14), lit: 50,
                      back: new Dictionary<string, int> { [wall] = 40 });
        var row = Assert.Single(Rows(Eval(D(), s, r, c), LightCheck.LightBehindWall));
        Assert.Equal("listed", row.Verdict);
        Assert.Equal(RestB, Assert.Single(row.OtherMods).Plugin);
    }

    [Fact]
    public void KP2_already_behind_a_wall_in_its_own_arrangement_is_not_the_pair()
    {
        string light = K(0x830, Sel);
        var s = Light(S(), light, At0, facing: new FacingResult(64, 10, 40, 14));
        var c = Light(C(), light, At0, facing: new FacingResult(64, 10, 40, 14));
        var res = Eval(D(), s, R(), c);
        Assert.Empty(Rows(res, LightCheck.LightBehindWall));
        Assert.Equal(1, res.Counts[LightCheck.LightBehindWall].NotThePair);
    }

    [Fact]
    public void KP2_three_hits_are_too_few_to_judge()
    {
        // rejects: a back-face majority read from 3 of 64 hits (the 8-hit floor dropped)
        string light = K(0x832, Sel);
        var s = Light(S(), light, At0);
        var c = Light(C(), light, At0, facing: new FacingResult(64, 1, 2, 61), lit: 10);
        var row = Assert.Single(Rows(Eval(D(), s, R(), c), LightCheck.LightBehindWall));
        Assert.Equal("not judged", row.Verdict);
        Assert.Contains("3 of 64", row.Reason);
    }

    [Fact]
    public void KP2v_the_other_side_removes_the_room_the_light_lit()
    {
        // rejects: the void reading switched off, or the room's remover not named
        string light = K(0x840, Sel), room = K(0x841, Base);
        var d = D().Add(Solid(room, At0));
        var s = Light(S().Add(Solid(room, At0)), light, At0, litBy: new Dictionary<string, double> { [room] = 100 });
        var r = R().Add(Solid(room, At0, on: false, touchers: new[] { Base, RestB }));
        var c = Light(C().Add(Solid(room, At0, on: false, touchers: new[] { Base, RestB })), light, At0, facing: new FacingResult(64, 0, 0, 64), lit: 0);
        var res = Eval(d, s, r, c);
        var row = Assert.Single(Rows(res, LightCheck.LightLightsNothing));
        Assert.Equal("listed", row.Verdict);
        Assert.Equal(RestB, Assert.Single(row.OtherMods).Plugin);
        Assert.Contains("safe to disable", row.Summary);
        Assert.Empty(Rows(res, LightCheck.LightBehindWall));
    }

    [Fact]
    public void KRadius_an_implausible_radius_is_not_judged()
    {
        // finding 5 (the two Dragonborn test lights in QASmoke read 844,822.9 u); rejects: such a light judged like any other
        string light = K(0x880, Sel);
        var s = Light(S(), light, At0, radius: 844822.9f);
        var c = Light(C(), light, At0, radius: 844822.9f, facing: new FacingResult(64, 10, 40, 14));
        var row = Assert.Single(Rows(Eval(D(), s, R(), c), LightCheck.LightBehindWall));
        Assert.Equal("not judged", row.Verdict);
        Assert.Contains("radius", row.Reason);
    }

    [Fact]
    public void KExtReach_an_exterior_light_reaching_past_the_neighbour_cells_is_not_judged()
    {
        string light = K(0x881, Sel);
        var s = Light(S(), light, At0, radius: 5000);
        var c = Light(C(), light, At0, radius: 5000, facing: new FacingResult(64, 10, 40, 14));
        var row = Assert.Single(Rows(Eval(D(), s, R(), c, exterior: true), LightCheck.LightBehindWall));
        Assert.Equal("not judged", row.Verdict);
        Assert.Contains("neighbouring cells", row.Reason);
    }

    [Fact]
    public void KGap_unreadable_ground_leaves_the_light_readings_not_judged()
    {
        // finding 4; rejects: an exterior light read without the ground under it and reported as judged
        string light = K(0x882, Sel);
        var s = Light(S(), light, At0);
        var c = Light(C(), light, At0, facing: new FacingResult(64, 10, 40, 14));
        c.SurfaceGaps.Add("the terrain of cell 0, 0 says it has heights and gives none");
        var res = Eval(D(), s, R(), c, exterior: true);
        Assert.Equal("not judged", Assert.Single(Rows(res, LightCheck.LightBehindWall)).Verdict);
        Assert.Equal(1, res.Counts[LightCheck.LightLightsNothing].NotJudged);
    }

    // ---------------------------------------------------------------- a fixture lit twice

    [Fact]
    public void KP3_one_light_from_each_side_on_one_candle()
    {
        string fx = K(0x850, Base), a = K(0x851, Sel), b = K(0x852, RestB);
        var at = new Vector3(300, 0, 0);
        var d = D().Add(Candle(fx, at));
        var s = Light(S().Add(Candle(fx, at)), a, at + new Vector3(0, 0, 20));
        var r = Light(R().Add(Candle(fx, at)), b, at + new Vector3(0, 0, 25));
        var c = Light(Light(C().Add(Candle(fx, at)), a, at + new Vector3(0, 0, 20)), b, at + new Vector3(0, 0, 25));
        var row = Assert.Single(Rows(Eval(d, s, r, c), LightCheck.FixtureLitTwice));
        Assert.Equal("listed", row.Verdict);
        Assert.Equal(new[] { Sel, RestB }, row.OtherMods.Select(m => m.Plugin).ToArray());
    }

    [Fact]
    public void KP3_two_lights_from_one_side_are_not_lit_twice()
    {
        // rejects: counting lights rather than sides
        string fx = K(0x850, Base), a = K(0x851, Sel), b = K(0x853, Sel);
        var at = new Vector3(300, 0, 0);
        var d = D().Add(Candle(fx, at));
        var s = Light(Light(S().Add(Candle(fx, at)), a, at + new Vector3(0, 0, 20)), b, at + new Vector3(0, 0, 25));
        var r = R().Add(Candle(fx, at)).Add(Solid(K(0x854, RestB), new Vector3(900, 0, 0)));
        var c = Light(Light(C().Add(Candle(fx, at)).Add(Solid(K(0x854, RestB), new Vector3(900, 0, 0))), a, at + new Vector3(0, 0, 20)), b, at + new Vector3(0, 0, 25));
        var res = Eval(d, s, r, c);
        Assert.Empty(Rows(res, LightCheck.FixtureLitTwice));
        Assert.Equal(1, res.Counts[LightCheck.FixtureLitTwice].NoClash);
    }

    // ---------------------------------------------------------------- over the light limit (a count)

    static LightCheckShape Shape(int count, params string[] lights) => new(K(0x860, Base), 0, "Frame", count, 0, 0, lights);

    [Fact]
    public void KP4_four_lights_from_each_side_is_a_count_that_cannot_tell()
    {
        // rejects: the count reported as a clash
        var c = C().Add(Shape(8, K(1, Sel), K(2, Sel), K(3, Sel), K(4, Sel), K(5, RestB), K(6, RestB), K(7, RestB), K(8, RestB)));
        var s = S().Add(Shape(4, K(1, Sel), K(2, Sel), K(3, Sel), K(4, Sel)));
        var r = R().Add(Shape(4, K(5, RestB), K(6, RestB), K(7, RestB), K(8, RestB)));
        var row = Assert.Single(Rows(Eval(D(), s, r, c), LightCheck.OverLightLimit));
        Assert.Equal("cannot tell", row.Verdict);
        Assert.StartsWith("Cannot tell", row.Summary);
    }

    [Fact]
    public void KP4_eight_from_one_side_is_not_the_pair()
    {
        var eight = Enumerable.Range(1, 8).Select(i => K(i, Sel)).ToArray();
        var res = Eval(D(), S().Add(Shape(8, eight)), R(), C().Add(Shape(8, eight)));
        Assert.Empty(Rows(res, LightCheck.OverLightLimit));
        Assert.Equal(1, res.Counts[LightCheck.OverLightLimit].NotThePair);
    }

    [Fact]
    public void Every_class_is_counted_even_with_no_subject()
    {
        var res = Eval(D(), S(), R(), C());
        Assert.Equal(LightCheck.Classes.OrderBy(x => x), res.Counts.Keys.OrderBy(x => x));
        Assert.Equal(6, res.Counts.Count);
    }

    // ---------------------------------------------------------------- the population: scenes, members, terrain, sides

    static LightCheckPlacement P(string key, string? cell, string? ws, Vector3 at, float reach = 50, bool light = false, params string[] touchers) =>
        new(key, cell, ws, at, reach, touchers.Length > 0 ? touchers : new[] { LightCheck.PluginOf(key) }, light);

    [Fact]
    public void KCell_an_interior_scene_reads_its_own_cell_only()
    {
        // rejects: every cell's references in one key space (lightclash.load_arm's fold: a candle in another house hosted the light)
        var idx = new LightCheckSceneIndex(new[] { P(K(1, Base), "cellA", null, At0), P(K(2, Sel), "cellB", null, At0, light: true) });
        var m = idx.Members(new LightCheckSceneKey(false, "cellB"));
        Assert.Equal(new[] { K(2, Sel) }, m.Select(x => x.P.Key).ToArray());
        Assert.All(m, x => Assert.True(x.Subject));
    }

    [Fact]
    public void KExt_an_exterior_scene_reads_its_ring_of_neighbours_and_the_large_references_that_reach_it()
    {
        // rejects: the centre cell alone (the neighbour's wall the light shines on dropped), or every large reference ignored
        const string ws = "00003C:Skyrim.esm";
        string light = K(1, Sel), wall = K(2, Base), edge = K(3, Base), far = K(4, Base), mountain = K(5, Base);
        var idx = new LightCheckSceneIndex(new[]
        {
            P(light, null, ws, new Vector3(2000, 2000, 0), light: true),
            P(wall, null, ws, new Vector3(5000, 2000, 0), 100),                // grid (1,0)
            P(edge, null, ws, new Vector3(2 * 4096 + 10, 2000, 0), 100),       // grid (2,0), reaching back into the square
            P(far, null, ws, new Vector3(3 * 4096 + 100, 2000, 0), 50),        // grid (3,0): out of reach
            P(mountain, null, ws, new Vector3(5 * 4096, 2000, 0), 30000),      // far away but large enough to reach
        });
        var m = idx.Members(new LightCheckSceneKey(true, ws, 0, 0)).Select(x => $"{x.P.Key} subject={x.Subject}").OrderBy(x => x, StringComparer.Ordinal);
        Assert.Equal(new[] { $"{light} subject=True", $"{wall} subject=False", $"{edge} subject=False", $"{mountain} subject=False" }
                         .OrderBy(x => x, StringComparer.Ordinal), m);
    }

    [Fact]
    public void KScenes_only_scenes_both_sides_touch_and_that_hold_a_light_are_read()
    {
        // rejects: reading a scene only one side touches (its subjects read the same in the combination as in that side alone)
        const string ws = "00003C:Skyrim.esm";
        var idx = new LightCheckSceneIndex(new[]
        {
            P(K(1, Base), "cellA", null, At0, touchers: new[] { Base, Sel }), P(K(2, Base), "cellA", null, At0, touchers: new[] { Base, RestB }),
            P(K(3, Base), "cellA", null, At0, light: true, touchers: new[] { Base }),
            P(K(4, Sel), "cellB", null, At0), P(K(5, Base), "cellB", null, At0, light: true),
            P(K(6, Sel), "cellC", null, At0), P(K(7, RestB), "cellC", null, At0),
            P(K(8, Sel), null, ws, new Vector3(100, 100, 0)), P(K(9, Base), null, ws, new Vector3(200, 100, 0), light: true),
            P(K(10, RestB), null, ws, new Vector3(4096 + 100, 100, 0)),
        });
        var got = idx.TwoSide(Sides(), out int oneSide, out int noLight).Select(s => s.Id).ToList();
        Assert.Equal(new[] { "cellA", ws + "@0,0", ws + "@1,0" }, got);
        Assert.Equal(1, oneSide);
        Assert.Equal(1, noLight);
    }

    [Fact]
    public void KTerrain_an_exterior_scene_reads_the_ground_of_all_nine_cells()
    {
        // finding 4; rejects: exterior lights read without terrain (a lamp over open ground then lights nothing)
        var scene = new LightCheckSceneKey(true, "00003C:Skyrim.esm", 0, 0);
        Assert.Equal(9, LightCheckSceneIndex.TerrainGrids(scene).Count());
        Assert.Empty(LightCheckSceneIndex.TerrainGrids(new LightCheckSceneKey(false, "cellA")));
        var flat = new float[LandHeight.Side, LandHeight.Side];
        var soup = LightCheckSceneIndex.TerrainSoup(0, 0, flat);
        Assert.Equal(32 * 32 * 2, soup.TriangleCount);
        var f = new LightScene(new[] { (soup, (string[]?)null, 0) }).Facing(new Vector3(2048, 2048, 100), 500);
        Assert.True(f.Front > 0);
        Assert.Equal(0, f.Back);
    }

    [Fact]
    public void KSides_the_sides_are_read_from_the_arms()
    {
        // finding 3; rejects: a plugin on both sides accepted, or the dependencies counted as the rest
        var mods = new Dictionary<string, string> { [Sel] = "Selected Mod", [RestB] = "Rest Mod B" };
        var ok = LightCheckSceneIndex.Sides(new[] { Base }, new[] { Base, Sel }, new[] { Base, RestB }, new[] { Base, Sel, RestB }, mods, out var none);
        Assert.Null(none);
        Assert.NotNull(ok);
        Assert.Equal(new[] { Sel }, ok!.PluginsOn(LightSide.Selection).ToArray());
        Assert.Equal(new[] { RestB }, ok.PluginsOn(LightSide.Rest).ToArray());
        Assert.Equal(LightSide.Base, ok.Of(Base));
        var bad = LightCheckSceneIndex.Sides(new[] { Base }, new[] { Base, Sel, RestB }, new[] { Base, RestB }, new[] { Base, Sel, RestB }, mods, out var why);
        Assert.Null(bad);
        Assert.Contains(RestB, why);
    }
}
