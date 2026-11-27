using System.Text.Json.Nodes;
using HarnessShared;
using Mutagen.Bethesda.Plugins;
using SkyWright.Engine;
using ViewProbe;
using Workbench;
using Xunit;

namespace WorkbenchTests;

/// <summary>Gate C, "nudge one object": the smallest move that clears the clash, found by the cheap judge, proved by the
/// real probes, and staged, never written. These tests hold the app's half: the search and its cap (the drop included),
/// the proof's word (a candidate the probes do not confirm, or that strands something, is dropped and the next is tried,
/// at most three), the refusals read from data/nudge-refusals.json, the words ("walking routes not checked" beside every
/// word from a nudge), and staged-not-written with an exact undo. The judge's own rule (no new crossing with any object,
/// a crossing already there at home not held against it) is held by overlapprobe --self-test (NudgeSelfTest.cs).</summary>
[Trait("tier", "integration")]
public sealed class NudgeTests : IClassFixture<DisableWorld>
{
    readonly DisableWorld _w;
    public NudgeTests(DisableWorld w) => _w = w;

    static string KindsPath => Path.Combine(Path.GetDirectoryName(TestBuild.Layout.PathOf(CheckoutLayout.Piece.Registry))!, NudgeKinds.FileName);

    static Aabb Bx(float x0, float y0, float z0, float x1, float y1, float z1) => new(new Vec3(x0, y0, z0), new Vec3(x1, y1, z1), false);
    static RefPlacementEdit.Pose P(float x, float y, float z) => new(new Vec3(x, y, z), new Vec3(0, 0, 0), 1f);
    static float Dist(RefPlacementEdit.Pose a, RefPlacementEdit.Pose b)
        => MathF.Sqrt(MathF.Pow(a.Position.X - b.Position.X, 2) + MathF.Pow(a.Position.Y - b.Position.Y, 2) + MathF.Pow(a.Position.Z - b.Position.Z, 2));
    static float Horizontal(NudgeCandidate c) => c.Move.Length();

    static TrialRecheck Proof(string word, RowVerdict[]? created = null, RowVerdict[]? contactNew = null) =>
        new(new RecheckResult(new RowVerdict("open", word, word, word + ": as the re-run measured it."), created ?? Array.Empty<RowVerdict>(), "verdict : DIFFERENTIAL",
                              Array.Empty<string>(), "overlapprobe: one build for both arms", 70),
            Array.Empty<RowVerdict>(), Array.Empty<string>(), "population: 1 plugin(s), 1 cell", contactNew ?? Array.Empty<RowVerdict>());

    /// <summary>A lane over fake judge and proof: what was asked, proved and staged.</summary>
    sealed class Fakes
    {
        internal readonly List<NudgeRequest> Asked = new();
        internal readonly List<int> Proved = new();
        internal readonly List<(RefPlacementEdit.Pose Pose, string Note)> Staged = new();
        internal Func<int, bool> ClearAt = _ => true;
        internal Func<int, TrialRecheck> ProofOf = _ => Proof("resolved");
        internal NudgeLane Lane(Func<RefPlacementEdit.Pose, (RefPlacementEdit.Pose, string)>? settle = null) => new(
            settle ?? (p => (p, "the drop found nothing below, so it keeps its height")),
            (req, ct) =>
            {
                Asked.Add(req);
                return Task.FromResult(new NudgeJudgeAnswer(req.Candidates.Select(c => new NudgeJudged(c.N, ClearAt(c.N), !ClearAt(c.N),
                    ClearAt(c.N) ? Array.Empty<string>() : new[] { "000900:Some.esp" }, Array.Empty<string>())).ToList(), Array.Empty<string>(), 12, 0, 0.5));
            },
            (edit, n, ct) => { Proved.Add(n); return Task.FromResult(ProofOf(n)); },
            (pose, note) => { Staged.Add((pose, note)); return "staged: nudge"; });
        internal RefPlacementEdit.Pose PoseOf(int n) => Asked.Single().Candidates.Single(c => c.N == n).Pose;
    }

    static readonly FormKey Subject = DisableWorld.MoveTarget;
    static readonly RefPlacementEdit.Pose Home = P(0, 0, 0);
    static readonly Aabb SubjectBox = Bx(-10, -10, 0, 10, 10, 20), OtherBox = Bx(5, -10, 0, 25, 10, 20);
    static Task<string> Run(NudgeLane lane) => lane.RunAsync(Subject, "HcWbDisMoveTarget", Home, SubjectBox, OtherBox, CancellationToken.None);

    // ---- the search and the cap ---------------------------------------------------------------------------------------

    [Fact]
    public void Never_past_the_cap_counting_the_drop()
    {
        var cands = NudgeSearch.Candidates(SubjectBox, OtherBox);
        // every candidate drops 60 units: one that moved 104 units sideways ends 120 units away, past the 107-unit cap
        var kept = NudgeSearch.Settled(Home, cands, p => (p with { Position = new Vec3(p.Position.X, p.Position.Y, p.Position.Z - 60) }, "lowered 60 units"));
        var past = kept.Where(k => Dist(Home, k.Pose) > NudgeRule.Cap + 1e-3f).ToList();
        Assert.True(past.Count == 0, $"{past.Count} position(s) past the cap counting the drop were offered, the first {(past.Count > 0 ? Dist(Home, past[0].Pose) : 0):0.#} units away");
        Assert.True(kept.Count > 0, "no position was kept at all");
        Assert.Contains(kept, k => Horizontal(k.Candidate) > 80);                         // sqrt(107^2 - 60^2) = 88.6: those still fit
        Assert.True(cands.All(c => Horizontal(c) <= NudgeRule.Cap + 1e-3f), "a candidate moves further sideways than the cap");
    }

    [Fact]
    public void The_search_goes_along_the_line_between_the_centres_first_then_other_headings_nearest_first()
    {
        var cands = NudgeSearch.Candidates(SubjectBox, OtherBox);
        Assert.True(cands.Count > 16, $"only {cands.Count} candidates: the search should try many headings, not only four");
        var first = cands[0];
        Assert.True(first.Move.X < 0 && MathF.Abs(first.Move.Y) < 1e-3f, $"the first candidate is not along the line away from the other object: {first.Move}");
        for (int i = 1; i < cands.Count; i++)
            Assert.True(Horizontal(cands[i]) >= Horizontal(cands[i - 1]) - 1e-3f, $"candidate {cands[i].N} is nearer than candidate {cands[i - 1].N}");
        var headings = cands.Select(c => MathF.Round(MathF.Atan2(c.Move.Y, c.Move.X), 2)).Distinct().Count();
        Assert.True(headings >= 8, $"only {headings} headings are searched");
    }

    // ---- the proof (layer 2) ------------------------------------------------------------------------------------------

    [Fact]
    public async Task A_candidate_the_proof_rejects_is_not_staged_and_the_next_is_tried()
    {
        var f = new Fakes();
        int? firstProved = null;
        f.ProofOf = n => { firstProved ??= n; return n == firstProved ? Proof("still there") : Proof("resolved"); };
        var said = await Run(f.Lane());
        Assert.True(f.Proved.Count == 2, $"the probes ran {f.Proved.Count} time(s); the first rejected candidate should be followed by the next ({said})");
        Assert.True(f.Staged.Count == 1, $"{f.Staged.Count} position(s) staged ({said})");
        Assert.True(f.Staged[0].Pose == f.PoseOf(f.Proved[1]), $"the staged position is not the second candidate's, the one the probes confirmed ({said})");
        Assert.True(f.Proved[0] < f.Proved[1], "the next candidate tried is not the next in order");
    }

    /// <summary>The app run on the lab (RESULT-C.md): every proof of row 8 read "resolved here, but", because a crossing of the
    /// object with a third object, there before the move, still held. Not held against the candidate, as in the judge's rule;
    /// the staged note says what still holds.</summary>
    [Fact]
    public async Task A_crossing_already_there_before_the_move_does_not_block_the_proof_and_the_note_says_it()
    {
        var f = new Fakes
        {
            ProofOf = _ => Proof("resolved") with { StillHolding = new[] { "001FBB:JKs Skyrim.esp / 000B5C:Ultimate Markarth.esp: holds (actual render surfaces: crossing)" } },
        };
        var said = await Run(f.Lane());
        Assert.True(f.Proved.Count == 1 && f.Staged.Count == 1, $"a proof whose only remaining row was there before the move was not taken ({said})");
        Assert.Contains("000B5C:Ultimate Markarth.esp", f.Staged[0].Note);
        Assert.Contains("walking routes not checked", f.Staged[0].Note);
    }

    [Fact]
    public async Task At_most_three_proofs_and_when_none_is_confirmed_nothing_is_staged()
    {
        var f = new Fakes { ProofOf = _ => Proof("still there") };
        var said = await Run(f.Lane());
        Assert.True(f.Proved.Count == NudgeLane.MaxProofs, $"the probes ran {f.Proved.Count} time(s), not at most {NudgeLane.MaxProofs}");
        Assert.True(f.Staged.Count == 0, "a position the probes did not confirm was staged");
        Assert.Contains("try disabling", said, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task When_nothing_is_clear_within_the_cap_one_sentence_says_so_and_suggests_disabling()
    {
        var f = new Fakes { ClearAt = _ => false };
        var said = await Run(f.Lane());
        Assert.True(f.Proved.Count == 0 && f.Staged.Count == 0, $"with nothing clear, the probes ran {f.Proved.Count} time(s) and {f.Staged.Count} position(s) were staged");
        Assert.True(said.Contains("no clear position", StringComparison.OrdinalIgnoreCase) && said.Contains("107") && said.Contains("try disabling", StringComparison.OrdinalIgnoreCase),
            $"the sentence does not say nothing is clear within the cap and to try disabling: '{said}'");
        Assert.True(said.TrimEnd().EndsWith('.') && said.Count(c => c == '.') <= 2, $"not one sentence: '{said}'");
    }

    [Fact]
    public async Task A_candidate_that_strands_something_is_rejected()
    {
        var stranded = new RowVerdict("contact|Stranded|001FC7", "new", "created", "new: it appears under the patch with its physical predicate holding.");
        Assert.Equal("new conflict", NudgeProofRule.Verdict(Proof("resolved", contactNew: new[] { stranded })).Word);
        var f = new Fakes();
        int? firstProved = null;
        f.ProofOf = n => { firstProved ??= n; return n == firstProved ? Proof("resolved", contactNew: new[] { stranded }) : Proof("resolved"); };
        var said = await Run(f.Lane());
        Assert.True(f.Staged.Count == 1 && f.Staged[0].Pose == f.PoseOf(f.Proved[^1]) && f.Proved.Count == 2,
            $"a candidate whose proof created a placement-contact row was staged, or the next was not tried ({said})");
    }

    /// <summary>Step 1, proof p03 (GATE-C-RED.md): the open row was repaired, but conflictdiff refused a gate ("unjudgeable
    /// 0.00% -> 2.94%"): the probe met pairs it could not decide at the candidate. That proof does not confirm the candidate.</summary>
    [Fact]
    public void A_proof_whose_differential_refused_a_gate_does_not_confirm_the_candidate()
    {
        var p = Proof("resolved");
        var refused = p with
        {
            Result = p.Result with
            {
                Verdict = "verdict : DIFFERENTIAL-UNJUDGED  (repaired 1 - created 0 = net +1; reclassified 0, unmeasured 0)",
                Lines = new[] { "   ! REFUSED: cross-responsible actual-shape queries: unjudgeable 0.00% -> 2.94% (0 of 62 -> 2 of 68) - rose by more than 1.00%",
                                "   verdict : DIFFERENTIAL-UNJUDGED  (repaired 1 - created 0 = net +1; reclassified 0, unmeasured 0)" },
            },
        };
        var v = NudgeProofRule.Verdict(refused);
        Assert.True(v.Word != "clear" && v.Sentence.Contains("2.94%", StringComparison.Ordinal),
            $"a proof whose conflictdiff refused a gate was taken as confirmed, or the sentence does not say which gate: {v.Word}: {v.Sentence}");
        Assert.Equal("clear", NudgeProofRule.Verdict(Proof("resolved")).Word);
    }

    // ---- the refusals, from the data file -------------------------------------------------------------------------------

    async Task<(string? Refusal, string? Note)> Judge(FormKey key, string? kindsPath = null)
    {
        var output = Path.Combine(_w.Root, "out"); Directory.CreateDirectory(output);
        await using var w = PatchWriter.OpenInProcess(new WriteTarget(_w.Instance, "Default", _w.DataDir, output),
            Path.Combine(_w.Root, "hcdata-" + Guid.NewGuid().ToString("N")), TestCorpus.Path, _ => { });
        var facts = await DisableRule.ReadAsync(new ServerContextSource(Task.FromResult(w), _ => null), key);
        return NudgeRule.Refusal(facts, NudgeKinds.Load(kindsPath ?? KindsPath));
    }

    public static IEnumerable<object[]> Kinds() => new[]
    {
        new object[] { "a marker", DisableWorld.MarkerRef.ToString(), "marker", "" },
        new object[] { "a trigger or other primitive", DisableWorld.PrimitiveRef.ToString(), "primitive", "" },
        new object[] { "an activator", DisableWorld.ActivatorRef.ToString(), "activator", "" },
        new object[] { "furniture", DisableWorld.FurnitureRef.ToString(), "furniture", "" },
        new object[] { "an idle marker", DisableWorld.IdleRef.ToString(), "idle marker", "" },
        new object[] { "a door", DisableWorld.PlainDoor.ToString(), "door", "" },
        new object[] { "a door that leads somewhere", DisableWorld.Door.ToString(), "leads", "" },
        new object[] { "a large reference", DisableWorld.LargeRef.ToString(), "large reference", "" },
        new object[] { "a map marker", DisableWorld.MapMarkerRef.ToString(), "map marker", "" },
        new object[] { "named by a linked reference", DisableWorld.LinkTarget.ToString(), "linked reference", DisableWorld.Linker.ToString() },
        new object[] { "the enable parent of another", DisableWorld.Parent.ToString(), "enable parent", DisableWorld.Child.ToString() },
        new object[] { "named by an activate parent", DisableWorld.ActTarget.ToString(), "activate parent", DisableWorld.ActChild.ToString() },
        new object[] { "a package's location", DisableWorld.PackLocTarget.ToString(), "package", DisableWorld.PackLocation.ToString() },
        new object[] { "a package's target", DisableWorld.PackTargetRef.ToString(), "package", DisableWorld.PackTarget.ToString() },
        new object[] { "named by a quest alias", DisableWorld.AliasTarget.ToString(), "quest", DisableWorld.Quest.ToString() },
        new object[] { "named by a script property of an object", DisableWorld.ScriptTarget.ToString(), "script", DisableWorld.ScriptHolder.ToString() },
    };

    [Theory, MemberData(nameof(Kinds))]
    public async Task Each_refused_kind_is_refused_before_any_work_from_the_data_file(string kind, string key, string words, string names)
    {
        var (refusal, _) = await Judge(FormKey.Factory(key));
        Assert.True(refusal is not null, $"{kind}: {key} was not refused");
        Assert.True(refusal!.Contains(words, StringComparison.OrdinalIgnoreCase), $"{kind}: the refusal does not say '{words}': {refusal}");
        if (names.Length > 0) Assert.True(refusal.Contains(names, StringComparison.OrdinalIgnoreCase), $"{kind}: the refusal does not name {names}: {refusal}");
        Assert.True(refusal.TrimEnd().EndsWith('.') && refusal.Count(c => c == '.') <= 2 + refusal.Split(':').Length, $"{kind}: not one sentence: {refusal}");
        Assert.Contains("try", refusal, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task A_plain_object_is_allowed_and_a_carryable_item_a_quest_alias_alone_names_is_moved_with_a_note()
    {
        var (plain, plainNote) = await Judge(DisableWorld.Plain);
        Assert.True(plain is null && plainNote is null, $"a plain object was refused or noted: {plain ?? plainNote}");
        var (item, note) = await Judge(DisableWorld.CarryAlias);
        Assert.True(item is null, $"a loose item only a quest alias names was refused: {item}");
        Assert.True(note is not null && note.Contains("quest", StringComparison.OrdinalIgnoreCase) && note.Contains("pick", StringComparison.OrdinalIgnoreCase),
            $"no note says the quest's item is moved like any item the player can pick up: {note ?? "none"}");
    }

    [Fact]
    public async Task The_data_file_changed_changes_the_answer()
    {
        var doc = JsonNode.Parse(File.ReadAllText(KindsPath))!.AsObject();
        var types = doc["baseTypes"]!.AsObject();
        types.Remove("Activator");
        types["Static"] = "a static";
        var changed = Path.Combine(_w.Root, "nudge-refusals-" + Guid.NewGuid().ToString("N") + ".json");
        File.WriteAllText(changed, doc.ToJsonString());
        var (plain, _) = await Judge(DisableWorld.Plain, changed);
        Assert.True(plain is not null && plain.Contains("a static", StringComparison.Ordinal), $"with Static added to the file, a static was not refused: {plain ?? "allowed"}");
        var (lever, _) = await Judge(DisableWorld.ActivatorRef, changed);
        Assert.True(lever is null, $"with Activator taken out of the file, an activator was still refused: {lever}");
    }

    [Fact]
    public void An_object_with_a_staged_or_written_fix_is_refused_by_the_one_fix_rule()
    {
        var ro = DisableTests.Drawn(DisableWorld.Plain, new Vec3(0, 0, 0), new Vec3(0, 0, 0), 1f);
        var s = new EditSession(new[] { ro });
        Assert.Null(NudgeRule.OneFix(s, DisableWorld.Plain));
        s.Stage(DisableWorld.Plain, s.Current(DisableWorld.Plain) with { Position = new Vec3(5, 0, 0) });
        Assert.True(NudgeRule.OneFix(s, DisableWorld.Plain) is { } moved && moved.Contains("one fix", StringComparison.Ordinal), "an object with a staged move was not refused a nudge");
        s.Undo();
        s.StageDisable(DisableWorld.Plain);
        Assert.True(NudgeRule.OneFix(s, DisableWorld.Plain) is { } off && off.Contains("one fix", StringComparison.Ordinal), "an object with a staged disable was not refused a nudge");
        s.Undo();
        s.Stage(DisableWorld.Plain, s.Current(DisableWorld.Plain) with { Position = new Vec3(5, 0, 0) });
        s.MarkWritten("WbWritten.esp");
        Assert.True(NudgeRule.OneFix(s, DisableWorld.Plain) is { } written && written.Contains("one fix", StringComparison.Ordinal), "an object with a written move was not refused a nudge");
    }

    // ---- the words ------------------------------------------------------------------------------------------------------

    [Fact]
    public void Nothing_from_a_nudge_reads_resolved_without_the_routes_sentence()
    {
        var resolved = new RecheckResult(new RowVerdict("open", "resolved", "repaired", "resolved: its physical predicate went Holds -> Gone under the patch."),
                                         Array.Empty<RowVerdict>(), "verdict : DIFFERENTIAL-VERIFIED", Array.Empty<string>(), "one build", 140.0, Nudged: "001FBB:JKs Skyrim.esp");
        var banner = Recheck.Banner(8, resolved);
        Assert.True(banner.Contains("walking routes not checked", StringComparison.Ordinal) && banner.Contains("clear of the other object and still supported", StringComparison.Ordinal),
            $"the banner after a nudge does not say what was and was not checked: {banner}");
        var still = Recheck.Banner(8, resolved with { Row = new RowVerdict("open", "still there", "unchanged", "still there: the re-run finds it as before.") });
        Assert.True(still.Contains("walking routes not checked", StringComparison.Ordinal) && !still.Contains("clear of the other object", StringComparison.Ordinal),
            $"a nudge whose row is still there is said wrongly: {still}");
        var edit = new PendingEdit(Subject, Home, P(-8, 0, -1), Nudge: "moved 8.1 units");
        var line = EditSession.Sentence(edit, "MiscSack02SmallFlat");
        Assert.True(line.StartsWith("Nudged", StringComparison.Ordinal) && line.Contains("walking routes not checked", StringComparison.Ordinal),
            $"the staged line does not say it is a nudge and that walking routes were not checked: {line}");
    }

    [Fact]
    public void A_nudge_is_marked_as_one_in_the_session_until_it_is_undone()
    {
        var ro = DisableTests.Drawn(DisableWorld.Plain, new Vec3(0, 0, 0), new Vec3(0, 0, 0), 1f);
        var s = new EditSession(new[] { ro });
        s.Stage(DisableWorld.Plain, P(-8, 0, 0), "moved 8 units");
        Assert.True(s.Pending.Single().Nudge is not null, "the staged edit does not know it came from a nudge");
        s.MarkWritten("WbNudged.esp");
        Assert.True(s.WrittenNudged.Contains(DisableWorld.Plain), "the written edit does not know it came from a nudge");
        s.ForgetWritten(DisableWorld.Plain);
        Assert.False(s.WrittenNudged.Contains(DisableWorld.Plain), "an undone written nudge is still marked as one");
        var data = new SessionData("plan", "baseline", null, null, null,
            new[] { new SavedEdit(DisableWorld.Plain.ToString(), P(0, 0, 0), P(-8, 0, 0), false, "moved 8 units") }, Array.Empty<SavedWritten>());
        var fresh = new EditSession(new[] { DisableTests.Drawn(DisableWorld.Plain, new Vec3(0, 0, 0), new Vec3(0, 0, 0), 1f) });
        SessionFile.Restore(fresh, data, (_, _) => null);
        Assert.True(fresh.Pending.SingleOrDefault()?.Nudge is not null, "a restored session lost that the staged edit came from a nudge");
    }

    // ---- staged, not written; undo exact --------------------------------------------------------------------------------

    [Fact]
    public async Task A_nudge_is_staged_not_written_and_undo_restores_exactly_the_state_before()
    {
        var id = Guid.NewGuid().ToString("N");
        var output = Path.Combine(_w.Root, "nudge-out-" + id); var work = Path.Combine(_w.Root, "nudge-work-" + id);
        Directory.CreateDirectory(output); Directory.CreateDirectory(work);
        await using var w = PatchWriter.OpenInProcess(new WriteTarget(_w.Instance, "Default", _w.DataDir, output),
            Path.Combine(_w.Root, "hcdata-" + id), TestCorpus.Path, _ => { });
        var home = new RefPlacementEdit.Pose(new Vec3(1200, 0, 0), new Vec3(0, 0, 0), 1f);
        var ro = DisableTests.Drawn(Subject, home.Position, home.Rotation, 1f);
        var s = new EditSession(new[] { ro });
        s.Select(Subject);
        var before = (Pose: s.Current(Subject), Pending: s.Pending.Count, Written: s.Written.Count, Selected: s.Selected);
        var trials = new TrialLane(work, async (edit, patch, outPath) => await w.CommitAsync(new[] { edit }, patch, dryRun: false, outPath: outPath),
            (patch, folder, dir, contact, ct) => Task.FromResult(Proof("resolved")), p => (p, "keeps its height"), _ => Array.Empty<string>());
        var lane = new NudgeLane(p => (p, "the drop found nothing below, so it keeps its height"),
            (req, ct) => Task.FromResult(new NudgeJudgeAnswer(req.Candidates.Select(c => new NudgeJudged(c.N, true, false, Array.Empty<string>(), Array.Empty<string>())).ToList(),
                                                              Array.Empty<string>(), 1, 0, 0.1)),
            (edit, n, ct) => trials.ProveAsync(edit, n, true, ct),
            (pose, note) => { s.Stage(Subject, pose, note); return "staged"; });
        var said = await lane.RunAsync(Subject, "HcWbDisMoveTarget", home, Bx(1190, -10, -10, 1210, 10, 10), Bx(1205, -10, -10, 1225, 10, 10), CancellationToken.None);

        Assert.True(s.Pending.Count == 1 && s.Pending[0].Nudge is not null, $"the nudge did not stage its candidate as a nudge: {said}");
        Assert.True(s.Written.Count == 0, "the nudge wrote its edit; it should only stage it");
        var left = Directory.GetFiles(output, "*.esp", SearchOption.AllDirectories);
        Assert.True(left.Length == 0, $"a plugin was left in the Output Folder: {string.Join(", ", left)}");
        Assert.NotEmpty(Directory.GetFiles(Path.Combine(work, "trials"), "*.esp", SearchOption.AllDirectories));     // the proof's trial patch is under the Work Folder

        s.Undo();
        var after = (Pose: s.Current(Subject), Pending: s.Pending.Count, Written: s.Written.Count, Selected: s.Selected);
        Assert.True(before == after, $"undo did not restore the state before the nudge: before {before}, after {after}");
        Assert.False(ro.Meshes[0].IsHiddenByHost);
    }
}
