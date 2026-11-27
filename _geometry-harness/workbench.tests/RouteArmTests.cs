using HarnessShared;
using Mutagen.Bethesda.Plugins;
using SkyWright.Engine;
using ViewProbe;
using Workbench;
using Xunit;

namespace WorkbenchTests;

/// <summary>Gate E, the street rule a probe can re-check: a nudge's proof runs a third arm, blockprobe's route cut, beside the
/// row's probe and contactprobe. blockprobe (<c>--near</c>) judges the moved object and every object whose route window its
/// move can reach, on the unpatched and the patched order, and lists every reference it judged with its verdict, so a
/// reference it did not test is never read as fine. These tests hold the app's half: the comparison of the two runs
/// (<see cref="RouteCutProof"/>), the proof's word, the lane trying the next candidate, and the words: "resolved" for a nudge
/// only when all three arms ran and held. blockprobe's half (the same verdict as a whole-plugin run, the near set) is its
/// own self-test (BlockNearSelfTest.cs).</summary>
public sealed class RouteArmTests : IDisposable
{
    readonly string _root = Path.Combine(Path.GetTempPath(), "wb-routearm-" + Guid.NewGuid().ToString("N"));
    public RouteArmTests() => Directory.CreateDirectory(_root);
    public void Dispose() { try { Directory.Delete(_root, true); } catch (IOException) { } }

    const string Moved = "001FBB:JKs Skyrim.esp", Near = "002036:JKs Skyrim.esp";

    /// <summary>A rows file as blockprobe --near writes it: the listed rows (bad verdicts only) and every judged reference.</summary>
    string Rows(string label, params (string Key, string Verdict)[] judged)
    {
        var path = Path.Combine(_root, label + ".route-cut-rows.json");
        var listed = judged.Where(j => j.Verdict is "Blocked" or "LikelyBlocked" or "NoLine" or "Unjudgeable")
                           .Select(j => new { Verdict = j.Verdict, FormKey = j.Key, Cell = "MarkarthOrigin", Navcut = false, Why = "the rule's sentence" });
        File.WriteAllText(path, System.Text.Json.JsonSerializer.Serialize(new
        {
            Probe = "blockprobe", Schema = "route-cut-rows/1", Targets = new[] { "JKs Skyrim.esp" }, Population = "baseline",
            Tally = new Dictionary<string, int>(), Rows = listed.ToList(),
            Near = new { Reference = Moved, Cells = new[] { "MarkarthOrigin" } },
            Judged = judged.Select(j => new { FormKey = j.Key, Cell = "MarkarthOrigin", Verdict = j.Verdict, Navcut = false,
                                              Why = j.Verdict == "NotMeasured" ? "its solid enters no actor column" : (string?)null }).ToList(),
        }));
        return path;
    }

    RouteArmResult Judge((string, string)[] before, (string, string)[] after)
        => RouteCutProof.Judge(RouteCutProof.Read(Rows("unpatched-" + Guid.NewGuid().ToString("N")[..6], before)),
                               RouteCutProof.Read(Rows("patched-" + Guid.NewGuid().ToString("N")[..6], after)), Moved);

    static TrialRecheck Proof(RouteArmResult? route) =>
        new(new RecheckResult(new RowVerdict("open", "resolved", "repaired", "resolved: as the re-run measured it."), Array.Empty<RowVerdict>(), "verdict : DIFFERENTIAL",
                              Array.Empty<string>(), "overlapprobe: one build for both arms", 70),
            Array.Empty<RowVerdict>(), Array.Empty<string>(), "population: 1 plugin(s), 1 cell", Array.Empty<RowVerdict>(), Route: route);

    static readonly RouteArmResult Held = new("held", "no walking route cut", Array.Empty<string>());
    static readonly RouteArmResult Cut = new("cut", "it would block a walking route", Array.Empty<string>());

    static RefPlacementEdit.Pose P(float x, float y, float z) => new(new Vec3(x, y, z), new Vec3(0, 0, 0), 1f);
    static Aabb Bx(float x0, float y0, float z0, float x1, float y1, float z1) => new(new Vec3(x0, y0, z0), new Vec3(x1, y1, z1), false);

    NudgeLane Lane(Func<int, TrialRecheck> proofOf, List<int> proved, List<string> staged) => new(
        p => (p, "keeps its height"),
        (req, ct) => Task.FromResult(new NudgeJudgeAnswer(req.Candidates.Select(c => new NudgeJudged(c.N, true, false, Array.Empty<string>(), Array.Empty<string>())).ToList(), Array.Empty<string>(), 3, 0, 0.1)),
        (edit, n, ct) => { proved.Add(n); return Task.FromResult(proofOf(n)); },
        (pose, note) => { staged.Add(note); return "staged: nudge"; });

    static Task<string> Run(NudgeLane lane) => lane.RunAsync(FormKey.Factory(Moved), "MiscSack02SmallFlat", P(0, 0, 0), Bx(-10, -10, 0, 10, 10, 20), Bx(5, -10, 0, 25, 10, 20), CancellationToken.None);

    // ---- the comparison: from fine to a cut is rejected ----------------------------------------------------------------

    [Fact]
    public async Task A_candidate_whose_route_cut_verdict_goes_from_fine_to_a_cut_is_rejected_and_the_next_is_tried()
    {
        var r = Judge(new[] { (Moved, "None"), (Near, "Clipped") }, new[] { (Moved, "Blocked"), (Near, "Clipped") });
        Assert.True(r.Word == "cut", $"a move that turns the object's verdict from None to Blocked reads '{r.Word}' ({r.Sentence})");
        var v = NudgeProofRule.Verdict(Proof(r));
        Assert.False(v.Word is "clear" or "resolved here, but", $"a proof whose route arm reads a new cut confirms the candidate: {v.Word}: {v.Sentence}");
        var proved = new List<int>(); var staged = new List<string>();
        int? first = null;
        var said = await Run(Lane(n => { first ??= n; return Proof(n == first ? Cut : Held); }, proved, staged));
        Assert.True(proved.Count == 2 && staged.Count == 1, $"the candidate that cuts a route was staged, or the next was not tried: {proved.Count} proof(s), {staged.Count} staged ({said})");
    }

    [Fact]
    public void A_neighbour_the_move_turns_into_a_cut_rejects_the_candidate_too()
    {
        var r = Judge(new[] { (Moved, "NotMeasured"), (Near, "None") }, new[] { (Moved, "NotMeasured"), (Near, "LikelyBlocked") });
        Assert.True(r.Word == "cut", $"a neighbour whose route the move cuts (None to LikelyBlocked) reads '{r.Word}' ({r.Sentence})");
    }

    [Fact]
    public void A_reference_that_can_no_longer_be_judged_after_the_move_does_not_confirm()
    {
        var r = Judge(new[] { (Moved, "Clipped") }, new[] { (Moved, "NoLine") });
        Assert.True(r.Word == "not confirmed", $"an object judged Clipped before the move and NoLine after it reads '{r.Word}' ({r.Sentence})");
    }

    // ---- review E1: the moved object must be judged at the nudged place --------------------------------------------------

    [Theory]
    [InlineData("NoLine")]
    [InlineData("Unjudgeable")]
    [InlineData("NotJudged")]
    public void The_moved_object_that_cannot_be_judged_after_the_move_is_not_confirmed_whatever_it_read_before(string verdict)
    {
        var r = Judge(new[] { (Moved, verdict), (Near, "None") }, new[] { (Moved, verdict), (Near, "None") });
        Assert.True(r.Word == "not confirmed", $"the moved object read {verdict} before and after the move, so nothing judged its route at the nudged place, yet the arm reads '{r.Word}' ({r.Sentence})");
    }

    [Fact]
    public void A_neighbour_judged_neither_before_nor_after_is_not_held_against_the_move_and_is_named()
    {
        var r = Judge(new[] { (Moved, "None"), (Near, "NoLine") }, new[] { (Moved, "None"), (Near, "NoLine") });
        Assert.True(r.Word == "held", $"a neighbour that could be judged neither before nor after is held against the move: '{r.Word}' ({r.Sentence})");
        Assert.True(r.Sentence.Contains(Near) && r.Sentence.Contains("could not be judged before or after"), $"the sentence does not name the neighbour that could not be judged: {r.Sentence}");
    }

    // ---- absent is not fine ------------------------------------------------------------------------------------------

    [Fact]
    public void A_reference_absent_from_the_rows_file_because_it_was_not_tested_is_not_read_as_fine()
    {
        // the neighbour is judged before the move, and absent from the patched run's list: not tested, so nothing is shown
        var r = Judge(new[] { (Moved, "None"), (Near, "None") }, new[] { (Moved, "None") });
        Assert.True(r.Word == "not confirmed", $"a reference the patched run did not test reads '{r.Word}' ({r.Sentence}); absent is not fine");
        Assert.Contains(Near, r.Sentence);
        // a rows file with no list of what was judged (a blockprobe before Gate E): nothing it leaves out is fine
        var old = Path.Combine(_root, "old.route-cut-rows.json");
        File.WriteAllText(old, """{ "Probe": "blockprobe", "Schema": "route-cut-rows/1", "Rows": [] }""");
        var o = RouteCutProof.Judge(RouteCutProof.Read(old), RouteCutProof.Read(Rows("p-old", (Moved, "None"))), Moved);
        Assert.True(o.Word == "not confirmed", $"a run that lists no judged references reads '{o.Word}' ({o.Sentence})");
    }

    // ---- a cut that was there before -----------------------------------------------------------------------------------

    [Fact]
    public async Task A_cut_already_there_before_the_move_is_not_held_against_the_candidate_and_is_said()
    {
        var r = Judge(new[] { (Moved, "None"), (Near, "Blocked") }, new[] { (Moved, "None"), (Near, "Blocked") });
        Assert.True(r.Word == "held", $"a cut there before the move and still there reads '{r.Word}' ({r.Sentence}); it is not the move's doing");
        Assert.True(r.Remains.Contains(Near) && r.Sentence.Contains("before the move"), $"the cut that remains is not said: {r.Sentence}");
        var proved = new List<int>(); var staged = new List<string>();
        var said = await Run(Lane(_ => Proof(r), proved, staged));
        Assert.True(staged.Count == 1 && staged[0].Contains(Near), $"the staged note does not say the route cut that remains ({said}; {string.Join(" | ", staged)})");
    }

    // ---- the words: resolved only with all three arms ------------------------------------------------------------------

    static RecheckResult Committed(RouteArmResult? route, string? contactNote = null) =>
        new(new RowVerdict("overlap|000848:ultimate markarth.esp|001fbb:jks skyrim.esp", "resolved", "repaired", "resolved."), Array.Empty<RowVerdict>(),
            "verdict : DIFFERENTIAL-VERIFIED (repaired 1 - created 0 = net +1)", Array.Empty<string>(), "build", 200,
            ContactNote: contactNote, Nudged: Moved, ContactNew: Array.Empty<RowVerdict>(), Route: route);

    static bool SaysResolved(string banner) => System.Text.RegularExpressions.Regex.IsMatch(banner, @"(?<!not called )(?<!not )\bresolved\b");

    [Fact]
    public void Resolved_after_a_nudge_needs_all_three_arms()
    {
        var all = Recheck.Banner(8, Committed(Held));
        Assert.True(SaysResolved(all) && all.Contains("no walking route is cut"), $"with all three arms holding the banner does not say resolved and no walking route cut: {all}");
        foreach (var (what, r) in new[] { ("the route arm not run", Committed(new RouteArmResult("not run", "this scan did not include the route-cut check", Array.Empty<string>()))),
                                          ("no route arm at all", Committed(null)),
                                          ("the route arm not confirmed", Committed(new RouteArmResult("not confirmed", "002036 was not tested after the move", Array.Empty<string>()))),
                                          ("the contact arm missing", Committed(Held, "what rested on it was not judged")) })
        {
            var b = Recheck.Banner(8, r);
            Assert.False(SaysResolved(b), $"{what}: the banner says resolved: {b}");
        }
        Assert.Contains("Walking routes were not checked", Recheck.Banner(8, Committed(null)));
    }

    [Fact]
    public void The_banner_after_a_nudge_s_commit_is_plain_sentences_with_the_detail_left_to_the_log()
    {
        var b = Recheck.Banner(8, Committed(Held));
        foreach (var jargon in new[] { "DIFFERENTIAL", "repaired 1", "net +1", "|", "verdict :" })
            Assert.False(b.Contains(jargon, StringComparison.Ordinal), $"the banner says '{jargon}': {b}");
        Assert.True(System.Text.RegularExpressions.Regex.Matches(b, @"\.\s").Count <= 2, $"more than three sentences: {b}");
    }

    [Fact]
    public async Task A_nudge_whose_three_arms_hold_is_staged_with_no_walking_route_cut()
    {
        var proved = new List<int>(); var staged = new List<string>();
        var said = await Run(Lane(_ => Proof(Held), proved, staged));
        Assert.True(staged.Count == 1 && staged[0].Contains(NudgeRule.CheckedAll) && !staged[0].Contains(NudgeRule.RoutesNotChecked),
            $"the staged note does not say all three were checked: {string.Join(" | ", staged)}");
        Assert.True(said.Contains("no walking route is cut") && !said.Contains("Walking routes were not checked"), $"the banner: {said}");
        var e = new PendingEdit(FormKey.Factory(Moved), P(0, 0, 0), P(-8, 0, 0), Nudge: staged[0]);
        var line = EditSession.Sentence(e, "MiscSack02SmallFlat");
        Assert.True(line.Contains(NudgeRule.CheckedAll) && !line.Contains(NudgeRule.RoutesNotChecked), $"the Edits line: {line}");
    }

    [Fact]
    public async Task A_nudge_whose_scan_has_no_route_cut_check_is_staged_with_walking_routes_not_checked()
    {
        var proved = new List<int>(); var staged = new List<string>();
        var said = await Run(Lane(_ => Proof(new RouteArmResult("not run", "this scan did not include the route-cut check", Array.Empty<string>())), proved, staged));
        Assert.True(staged.Count == 1 && staged[0].Contains(NudgeRule.RoutesNotChecked), $"the staged note: {string.Join(" | ", staged)}");
        Assert.Contains("Walking routes were not checked", said);
        var e = new PendingEdit(FormKey.Factory(Moved), P(0, 0, 0), P(-8, 0, 0), Nudge: staged[0]);
        Assert.Contains(NudgeRule.RoutesNotChecked, EditSession.Sentence(e, "MiscSack02SmallFlat"));
    }

    // ---- review E2: a written nudge's Edits line says only what is known ---------------------------------------------------

    [Fact]
    public void A_written_nudge_s_Edits_line_says_only_what_is_known_before_and_after_its_re_check()
    {
        var ro = DisableTests.Drawn(DisableWorld.Plain, new Vec3(0, 0, 0), new Vec3(0, 0, 0), 1f);
        var s = new EditSession(new[] { ro });
        s.Stage(DisableWorld.Plain, P(-8, 3, 0), "moved 8.5 units");
        s.MarkWritten("WbNudged.esp");
        string Line() => NudgeRule.WrittenLine("HcWbPlain", "WbNudged.esp", s.NudgeRechecked(DisableWorld.Plain));
        var before = Line();
        Assert.True(before.Contains("not yet re-checked") && !before.Contains("walking route") && !SaysResolved(before),
            $"before any re-check the written nudge's line claims more than that it is written: {before}");
        s.NoteNudgeRechecked(DisableWorld.Plain, Recheck.NudgeOutcome(Committed(new RouteArmResult("not run", RouteCutProof.NotInScan, Array.Empty<string>()))));
        var notRun = Line();
        Assert.True(notRun.Contains("walking routes were not checked") && !SaysResolved(notRun) && !notRun.Contains("not yet re-checked"),
            $"after a re-check whose scan had no route-cut check the line does not say walking routes were not checked: {notRun}");
        s.NoteNudgeRechecked(DisableWorld.Plain, Recheck.NudgeOutcome(Committed(Held)));
        var held = Line();
        Assert.True(SaysResolved(held), $"after a re-check where all three held the line does not say resolved: {held}");
    }

    // ---- the re-check of a written nudge, after the session is saved and restored ----------------------------------------

    [Fact]
    public void A_written_nudge_restored_from_the_session_file_gives_its_route_arm_both_places()
    {
        var ro = DisableTests.Drawn(DisableWorld.Plain, new Vec3(0, 0, 0), new Vec3(0, 0, 0), 1f);
        var s = new EditSession(new[] { ro });
        s.Stage(DisableWorld.Plain, P(-8, 3, 0), "moved 8.5 units");
        s.MarkWritten("WbNudged.esp");
        var path = Path.Combine(_root, "session.json");
        SessionFile.Write(path, new SessionData("plan", "baseline", null, null, null, Array.Empty<SavedEdit>(), SessionFile.WrittenOf(s, _ => "folder")));
        var fresh = new EditSession(new[] { DisableTests.Drawn(DisableWorld.Plain, new Vec3(0, 0, 0), new Vec3(0, 0, 0), 1f) });
        SessionFile.Restore(fresh, SessionFile.Read(path)!, (_, _) => P(-8, 3, 0));
        var t = Recheck.RouteTargets(fresh, fresh.NudgedIn("WbNudged.esp"));
        Assert.True(t.Count == 1 && t[0].Key == DisableWorld.Plain.ToString() && t[0].Home.X == 0 && t[0].At.X == -8 && t[0].At.Y == 3,
            $"the restored written nudge's route arm is not asked about its load-order place and its written place: {string.Join("; ", t.Select(x => $"{x.Key} {x.Home} -> {x.At}"))}");
    }
}
