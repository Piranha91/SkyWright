using System.Globalization;
using System.Numerics;
using System.Text.Json;
using SkyWright.Engine;
using Mutagen.Bethesda.Plugins;
using ViewProbe;

namespace Workbench;

/// <summary>The kinds a nudge refuses, read from the owner's data file (data/nudge-refusals.json, Data\ in the build): base
/// record types, the object's own facts, and how a record naming it counts. The rule reads it each time a nudge is asked
/// for, so the owner revises the file, not the code.</summary>
internal sealed record NudgeKinds(IReadOnlyDictionary<string, string> BaseTypes, IReadOnlyDictionary<string, string> Facts,
                                  IReadOnlyDictionary<string, string> NamedBy, IReadOnlySet<string> Carryable, string Source)
{
    internal const string FileName = "nudge-refusals.json";

    /// <summary>The file beside the build's other app data (the registry's folder, Data\).</summary>
    internal static string DefaultPath() =>
        Path.Combine(Path.GetDirectoryName(Program.Piece(HarnessShared.CheckoutLayout.Piece.Registry, "check registry check-registry.json"))!, FileName);

    internal static NudgeKinds Load(string path)
    {
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            var r = doc.RootElement;
            Dictionary<string, string> Map(string name) => r.TryGetProperty(name, out var m) && m.ValueKind == JsonValueKind.Object
                ? m.EnumerateObject().ToDictionary(p => p.Name, p => p.Value.GetString() ?? "", StringComparer.Ordinal) : new Dictionary<string, string>();
            var carry = r.TryGetProperty("carryableBaseTypes", out var c) && c.ValueKind == JsonValueKind.Array
                ? c.EnumerateArray().Select(x => x.GetString() ?? "").ToHashSet(StringComparer.Ordinal) : new HashSet<string>();
            return new NudgeKinds(Map("baseTypes"), Map("facts"), Map("namedBy"), carry, path);
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException or InvalidOperationException)
        {
            throw new InvalidOperationException($"the nudge's list of what it refuses ({path}) could not be read ({ex.Message}); restore the file from the build or the repository's data folder.");
        }
    }
}

/// <summary>What a nudge may move (refusals by what depends on where the object stands, each kind from <see cref="NudgeKinds"/>),
/// its cap and its words. The facts come from the one reader Gate B's disable uses (<see cref="DisableRule.ReadAsync"/>);
/// whether it is large from <see cref="LargeReferenceLists"/> through that reader.</summary>
internal static class NudgeRule
{
    /// <summary>The owner's cap: the object ends at most 107 units (about 5 feet) from where the load order places it, the drop included.</summary>
    internal const float Cap = 107f;
    internal const string Checked = "clear of the other object and still supported";
    internal const string RoutesNotChecked = "walking routes not checked";
    /// <summary>A nudge whose three arms (the row's probe, placement contact, route cut) all held (Gate E).</summary>
    internal const string CheckedAll = "clear of the other object, still supported and no walking route cut";
    const string Try = "try moving the other object of the conflict, or move this one by hand";

    static string Kind(string field) => DisableRule.Tokens(field) is { Length: > 0 } t ? string.Join(", ", t) : field.Split(' ', 2)[0];

    /// <summary>Null when the object may be nudged, else the one sentence before any work; and a note to say when it is moved.</summary>
    internal static (string? Refusal, string? Note) Refusal(DisableFacts f, NudgeKinds kinds)
    {
        if (f.RecordType is { } t && t != "PlacedObject")
            return ($"{f.Key} is {(t == "PlacedNpc" ? "an actor" : "a " + t)}, not a placed object, and a nudge moves placed objects only; {Try}.", null);
        if (f.Deleted || (f.RawFlags & DisableRule.DeletedBit) != 0)
            return ($"{f.Key} is deleted in the load order, so there is nothing to nudge; try reopening the scene, as it may be out of date.", null);
        if ((f.RawFlags & DisableRule.InitiallyDisabled) != 0)
            return ($"{f.Key} is initially disabled in the load order, so a nudge changes nothing in game; try reopening the scene, as it may be out of date.", null);
        string What(string phrase) => $"{f.Key} is {phrase}{(f.BaseEditorId is { } e ? $" ({e})" : "")}, which the game, its actors or its scripts use where it stands, so a nudge does not move it; {Try}.";
        bool marker = f.BaseMarkerFlag || HarnessShared.DetectorModel.ClassifyHost(f.BaseType, f.BaseEditorId) == HarnessShared.HostClass.Marker;
        foreach (var (fact, holds) in new[] { ("leadsSomewhere", f.LeadsTo is not null), ("largeReference", f.LargeIn is not null), ("mapMarker", f.MapMarker),
                                              ("primitive", f.Primitive), ("marker", marker) })
            if (holds && kinds.Facts.TryGetValue(fact, out var phrase))
                return (fact switch
                {
                    "leadsSomewhere" => $"{f.Key} is {phrase} (to {f.LeadsTo}), whose far side brings the player to where it stands, so a nudge does not move it; {Try}.",
                    "largeReference" => $"{f.Key} is {phrase} (on worldspace {f.LargeIn}'s large-reference list), and overriding one from a patch plugin makes it flicker in game, so a nudge does not move it; {Try}.",
                    _ => What(phrase),
                }, null);
        if (f.BaseType is { } bt && kinds.BaseTypes.TryGetValue(bt, out var bp)) return (What(bp), null);

        // every record that names it, by how it names it (the cell answer is kept only when no reference names it: a cell is
        // credited through a reference filed in it)
        var named = f.NamedBy.Where(n => !n.Field.StartsWith("Cell", StringComparison.Ordinal) || !f.NamedBy.Any(o => DisableRule.Tokens(o.Field).Length > 0)).ToList();
        string Action(string kind) => kind.Split(", ").Select(k => kinds.NamedBy.TryGetValue(k, out var a) ? a : kinds.NamedBy.TryGetValue("other", out var o) ? o : "allow")
                                          .OrderBy(a => a == "refuse" ? 0 : a == "refuse-unless-carryable" ? 1 : 2).First();
        var weighed = named.Select(n => (n.Key, n.Field, Act: Action(Kind(n.Field)))).Where(n => n.Act != "allow").ToList();
        bool carryable = f.BaseType is { } cb && kinds.Carryable.Contains(cb);
        if (weighed.Count > 0 && carryable && weighed.All(n => n.Act == "refuse-unless-carryable") && f.NamedNotListed == 0)
            return (null, $"{f.Key} is named only by {DisableRule.List(weighed.Select(n => $"{n.Key} ({DisableRule.How(n.Field)})").ToList(), 0)}, and it is an item the player can pick up, so it is moved like any item; the quest finds it wherever it lies.");
        if (weighed.Count > 0 || f.NamedNotListed > 0)
            return ($"{f.Key} is named by {DisableRule.List(weighed.Select(n => $"{n.Key} ({DisableRule.How(n.Field)})").ToList(), f.NamedNotListed)}, which depends on where it stands, so a nudge does not move it; {Try}.", null);
        return (null, null);
    }

    /// <summary>Gate B's one-fix rule, said for a nudge; null when the object has no staged or written fix.</summary>
    internal static string? OneFix(EditSession s, FormKey key)
    {
        if (s.IsDisabled(key))
            return $"{key} is {(s.Pending.Any(e => e.Ref == key && e.Disable) ? "staged to be disabled" : $"written disabled to {s.Written[key].Patch}")}, and an object takes one fix, a move or a disable; undo the disable first to nudge it.";
        if (s.Pending.Any(e => e.Ref == key) || s.Written.ContainsKey(key))
            return $"{key} has a move {(s.Pending.Any(e => e.Ref == key) ? "staged" : $"written to {s.Written[key].Patch}")}, and an object takes one fix; undo or revert that move first to nudge it.";
        return null;
    }

    static string N(float v) => v.ToString("0.#", CultureInfo.InvariantCulture);

    /// <summary>A move in the modder's words: "8.1 units (8 west, 1 down)".</summary>
    internal static string Move(RefPlacementEdit.Pose from, RefPlacementEdit.Pose to)
    {
        var d = new Vector3(to.Position.X - from.Position.X, to.Position.Y - from.Position.Y, to.Position.Z - from.Position.Z);
        var parts = new List<string>();
        void Part(float v, string pos, string neg) { if (MathF.Abs(v) >= 0.05f) parts.Add($"{N(MathF.Abs(v))} {(v > 0 ? pos : neg)}"); }
        Part(d.X, "east", "west"); Part(d.Y, "north", "south"); Part(d.Z, "up", "down");
        return $"{N(d.Length())} units ({(parts.Count == 0 ? "no change" : string.Join(", ", parts))})";
    }
}

/// <summary>One candidate move: its number, the horizontal move from the load-order place, the rule that made it.</summary>
internal sealed record NudgeCandidate(int N, Vector2 Move, string Rule);

/// <summary>A candidate after its drop, within the cap.</summary>
internal sealed record NudgeSettled(NudgeCandidate Candidate, RefPlacementEdit.Pose Pose, string Drop);

/// <summary>Where a nudge looks (Gate C): outward from the load-order place, nearest first, along the line from the other
/// object's centre through this one's, then 16 other headings, in 4-unit steps to the cap. Rotation is not searched. Each
/// candidate is settled by the trial lane's drop (<see cref="TrialLane.SettleBy"/>, the caller's) and kept only when its whole
/// move, the drop included, is within the cap.</summary>
internal static class NudgeSearch
{
    internal const float Step = 4f;
    internal const int Headings = 16;

    internal static IReadOnlyList<NudgeCandidate> Candidates(Aabb subject, Aabb other, float cap = NudgeRule.Cap)
    {
        var line = new Vector2(subject.Center.X - other.Center.X, subject.Center.Y - other.Center.Y);
        bool hasLine = line.Length() > 1e-3f;
        float lineAngle = hasLine ? MathF.Atan2(line.Y, line.X) : 0f;
        var gen = new List<(float D, float Rank, Vector2 Move, string Rule)>();
        for (float d = Step; d <= cap + 1e-3f; d += Step)
        {
            if (hasLine) { var u = Vector2.Normalize(line); gen.Add((d, -1f, u * d, "along the line from the other object's centre through this one's")); }
            for (int h = 0; h < Headings; h++)
            {
                float a = h * 2 * MathF.PI / Headings;
                float off = MathF.Abs(MathF.IEEERemainder(a - lineAngle, 2 * MathF.PI));
                if (hasLine && off < 0.01f) continue;                          // the line itself, already tried
                gen.Add((d, off, new Vector2(MathF.Cos(a), MathF.Sin(a)) * d, FormattableString.Invariant($"heading {h * 360.0 / Headings:0.#}° (0° east, 90° north)")));
            }
        }
        return gen.OrderBy(x => x.D).ThenBy(x => x.Rank).Select((x, i) => new NudgeCandidate(i + 1, x.Move, x.Rule)).ToList();
    }

    static float Dist(RefPlacementEdit.Pose a, RefPlacementEdit.Pose b)
        => new Vector3(a.Position.X - b.Position.X, a.Position.Y - b.Position.Y, a.Position.Z - b.Position.Z).Length();

    /// <summary>Each candidate settled by <paramref name="settle"/>; kept when its whole move, the drop included, is within the cap;
    /// nearest first by that whole move.</summary>
    internal static IReadOnlyList<NudgeSettled> Settled(RefPlacementEdit.Pose home, IReadOnlyList<NudgeCandidate> candidates,
        Func<RefPlacementEdit.Pose, (RefPlacementEdit.Pose Pose, string Drop)> settle, float cap = NudgeRule.Cap, CancellationToken ct = default)
    {
        var kept = new List<NudgeSettled>();
        foreach (var c in candidates)
        {
            ct.ThrowIfCancellationRequested();
            var (p, d) = settle(home with { Position = new Vec3(home.Position.X + c.Move.X, home.Position.Y + c.Move.Y, home.Position.Z) });
            if (Dist(home, p) <= cap + 1e-3f) kept.Add(new NudgeSettled(c, p, d));
        }
        return kept.OrderBy(k => Dist(home, k.Pose)).ThenBy(k => k.Candidate.N).ToList();
    }
}

/// <summary>What the judge program is asked: the object, the conflict's other object and each settled candidate pose.</summary>
internal sealed record NudgeRequest(FormKey Subject, FormKey Other, RefPlacementEdit.Pose Home, IReadOnlyList<(int N, RefPlacementEdit.Pose Pose)> Candidates);

/// <summary>The judge's word on one candidate (layer 1: it only chooses).</summary>
internal sealed record NudgeJudged(int N, bool Clear, bool OtherHit, IReadOnlyList<string> NewCrossings, IReadOnlyList<string> NewUnknown);

/// <summary>The judge program's answer, or its one-sentence refusal.</summary>
internal sealed record NudgeJudgeAnswer(IReadOnlyList<NudgeJudged> Candidates, IReadOnlyList<string> HomeCrossings, int Neighbours, int NoGeometry,
                                        double Seconds, string? Refused = null)
{
    internal static NudgeJudgeAnswer Read(string path)
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(path));
        var r = doc.RootElement;
        if (r.TryGetProperty("refused", out var rf) && rf.ValueKind == JsonValueKind.String)
            return new(Array.Empty<NudgeJudged>(), Array.Empty<string>(), 0, 0, 0, rf.GetString());
        static List<string> L(JsonElement e, string k) => e.TryGetProperty(k, out var a) && a.ValueKind == JsonValueKind.Array ? a.EnumerateArray().Select(x => x.GetString() ?? "").ToList() : new();
        var cands = r.GetProperty("candidates").EnumerateArray().Select(c => new NudgeJudged(c.GetProperty("n").GetInt32(), c.GetProperty("clear").GetBoolean(),
            c.GetProperty("otherHit").GetBoolean(), L(c, "newCrossings"), L(c, "newUndecided"))).ToList();
        return new(cands, L(r.GetProperty("home"), "crossings"), r.GetProperty("neighbours").GetInt32(), r.GetProperty("noGeometry").GetInt32(),
                   r.GetProperty("seconds").GetDouble());
    }
}

/// <summary>The proof's word on a candidate (layer 2): the trial re-check's frozen words (<see cref="ClearRule.Judge"/>) over
/// the open row's probe and contactprobe, where a created placement-contact row (something left in the air) is a new conflict;
/// and a differential whose conflictdiff refused a gate (the probe could not measure part of what the move changed) confirms
/// nothing (GATE-C-RED.md, proof p03).</summary>
internal static class NudgeProofRule
{
    static readonly System.Text.RegularExpressions.Regex Key = new(@"[0-9A-Fa-f]{6}:[^|/()]+?\.es[mpl]", System.Text.RegularExpressions.RegexOptions.Compiled);

    /// <summary>The pair of objects a row's identity or a still-holding line names, as one key ("a|b", lower case, sorted).</summary>
    internal static string? PairOf(string text)
    {
        var keys = Key.Matches(text).Select(m => m.Value.ToLowerInvariant()).Distinct().OrderBy(k => k, StringComparer.Ordinal).ToList();
        return keys.Count == 2 ? keys[0] + "|" + keys[1] : null;
    }

    /// <summary>Every pair of <paramref name="refKey"/> with another object that held in an emit: its rows, and its carried
    /// predicates whose physical state is Holds.</summary>
    internal static IReadOnlySet<string> HeldPairs(string emitPath, string refKey)
    {
        var set = new HashSet<string>(StringComparer.Ordinal);
        using var doc = JsonDocument.Parse(File.ReadAllText(emitPath));
        static string? S(JsonElement e, string k) => e.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
        void Add(string? a, string? b)
        {
            if (a is null || b is null) return;
            if (!string.Equals(a, refKey, StringComparison.OrdinalIgnoreCase) && !string.Equals(b, refKey, StringComparison.OrdinalIgnoreCase)) return;
            var k = new[] { a.ToLowerInvariant(), b.ToLowerInvariant() }.OrderBy(x => x, StringComparer.Ordinal).ToArray();
            set.Add(k[0] + "|" + k[1]);
        }
        if (doc.RootElement.TryGetProperty("Conflicts", out var cs) && cs.ValueKind == JsonValueKind.Array)
            foreach (var c in cs.EnumerateArray())
            {
                string? other = c.TryGetProperty("Alternative", out var alt) && alt.ValueKind == JsonValueKind.Array && alt.GetArrayLength() > 0 ? S(alt[0], "FormKey") : null;
                if (c.TryGetProperty("Necessary", out var ns) && ns.ValueKind == JsonValueKind.Array)
                    foreach (var n in ns.EnumerateArray()) Add(S(n, "FormKey"), other);
            }
        if (doc.RootElement.TryGetProperty("Predicates", out var ps) && ps.ValueKind == JsonValueKind.Array)
            foreach (var p in ps.EnumerateArray())
                if (string.Equals(S(p, "Physical"), "Holds", StringComparison.OrdinalIgnoreCase)) Add(S(p, "FormKey"), S(p, "Other"));
        return set;
    }

    /// <summary>Why a "resolved here, but" proof does not confirm the nudge, or null when every remaining item was there before
    /// the move: each other row of the object is still there (or reclassified with its same pair still holding) and held in the
    /// unpatched run, and each pair still holding held in the unpatched run too. A row unmeasured or in neither run confirms
    /// nothing.</summary>
    internal static string? NotThereBefore(TrialRecheck rc)
    {
        if (rc.HeldBefore is not { } before) return "what held before the move was not read, so what remains cannot be shown to be old";
        var still = rc.StillHolding.Select(PairOf).Where(p => p is not null).Select(p => p!).ToHashSet(StringComparer.Ordinal);
        foreach (var o in rc.OthersOfRef.Where(o => o.Word != "resolved"))
        {
            var pair = PairOf(o.Id);
            if (o.Word is not ("still there" or "reclassified")) return $"row {o.Id} reads {o.Word}";
            if (pair is null || !before.Contains(pair)) return $"row {o.Id} did not hold before the move";
            if (o.Word == "reclassified" && !still.Contains(pair)) return $"row {o.Id} was reclassified and its crossing is not shown still holding";
        }
        foreach (var h in rc.StillHolding)
            if (PairOf(h) is not { } p || !before.Contains(p)) return $"{h} did not hold before the move";
        return null;
    }

    internal static TrialVerdict Verdict(TrialRecheck rc)
    {
        var refused = rc.Result.Lines.FirstOrDefault(l => l.Contains("! REFUSED", StringComparison.Ordinal))
                      ?? (rc.Result.Verdict.Contains("UNJUDGED", StringComparison.Ordinal) || rc.Result.Verdict.Contains("REFUSED", StringComparison.Ordinal) ? rc.Result.Verdict : null);
        if (refused is not null)
            return new TrialVerdict("not confirmed", $"not confirmed: conflictdiff refused a gate ({refused.Trim().Replace("! REFUSED: ", "")}), so the probe could not measure part of what the move changed", Array.Empty<string>());
        var v = ClearRule.Judge(rc.Result.Row, rc.OthersOfRef, rc.Result.New.Concat(rc.ContactNew ?? Array.Empty<RowVerdict>()).ToList(), rc.StillHolding);
        if (v.Word == "resolved here, but" && NotThereBefore(rc) is { } why)
            return new TrialVerdict("not confirmed", $"not confirmed: {v.Sentence.TrimEnd('.')}; and {why}", v.Rows);
        return v;
    }
}

/// <summary>One proof run: the candidate, its word, its sentence and its seconds.</summary>
internal sealed record NudgeProofRun(int N, string Word, string Sentence, double Seconds);

/// <summary>The nudge for one object (Gate C), off the UI thread: the search settled by the scene's drop, the judge program on
/// every candidate within the cap, then the real probes on the nearest clear ones, at most three; the first the probes confirm
/// is handed to <c>stage</c>, never written. Abort is the token.</summary>
internal sealed class NudgeLane
{
    internal delegate Task<NudgeJudgeAnswer> Judge(NudgeRequest request, CancellationToken ct);
    internal delegate Task<TrialRecheck> Prove(PendingEdit edit, int n, CancellationToken ct);

    internal const int MaxProofs = 3;

    readonly Func<RefPlacementEdit.Pose, (RefPlacementEdit.Pose Pose, string Drop)> _settle;
    readonly Judge _judge;
    readonly Prove _prove;
    readonly Func<RefPlacementEdit.Pose, string, string> _stage;
    readonly Action<string>? _say;
    readonly Func<string, string> _nameOf;
    readonly Action<string>? _log;
    readonly object _lock = new();
    readonly List<NudgeProofRun> _proofs = new();

    internal NudgeLane(Func<RefPlacementEdit.Pose, (RefPlacementEdit.Pose Pose, string Drop)> settle, Judge judge, Prove prove,
                       Func<RefPlacementEdit.Pose, string, string> stage, Action<string>? say = null, Func<string, string>? nameOf = null,
                       Action<string>? log = null)
    { _settle = settle; _judge = judge; _prove = prove; _stage = stage; _say = say; _nameOf = nameOf ?? (k => k); _log = log; }

    internal IReadOnlyList<NudgeProofRun> Proofs { get { lock (_lock) return _proofs.ToList(); } }
    internal string Phase { get; private set; } = "not started";
    internal int Judged { get; private set; }
    internal int ClearCount { get; private set; }

    void Say(string phase, string s) { Phase = phase; _say?.Invoke(s); }

    /// <summary>Runs the nudge; returns two or three plain sentences for the banner (what moved and how far, what it is clear of
    /// and still touches, what was not checked; or why nothing is staged). The detail goes to <c>say</c> (the Log).</summary>
    internal async Task<string> RunAsync(FormKey key, string? name, RefPlacementEdit.Pose home, Aabb subject, Aabb other, CancellationToken ct, FormKey? otherKey = null)
    {
        string what = name is { Length: > 0 } ? $"{name} ({key})" : key.ToString();
        string me = _nameOf(key.ToString()), them = otherKey is { } ok ? _nameOf(ok.ToString()) : "the other object";
        string cap = FormattableString.Invariant($"{NudgeRule.Cap:0} units");
        const string Then = " Nothing is staged; try disabling it, or move the other object.";
        Say("searching", $"nudge {what}: settling the candidate positions within {cap} by the drop");
        var cands = NudgeSearch.Candidates(subject, other);
        var settled = await Task.Run(() => NudgeSearch.Settled(home, cands, _settle, NudgeRule.Cap, ct), ct).ConfigureAwait(false);
        Judged = settled.Count;
        if (settled.Count == 0)
            return Done($"No clear position within {cap} was found for {me}: wherever it was set down, the drop took it past {cap}." + Then,
                        $"nudge {what}: every candidate's drop took it past the cap");
        Say("judging", $"nudge {what}: the judge program is checking {settled.Count} positions against everything near it (about 30 s)");
        var ans = await _judge(new NudgeRequest(key, otherKey ?? FormKey.Null, home, settled.Select(s => (s.Candidate.N, s.Pose)).ToList()), ct).ConfigureAwait(false);
        if (ans.Refused is { } no) return Done(no, "nudge judge refused: " + no);
        var verdicts = ans.Candidates.ToDictionary(c => c.N);
        var clear = settled.Where(s => verdicts.TryGetValue(s.Candidate.N, out var v) && v.Clear).ToList();
        ClearCount = clear.Count;
        if (clear.Count == 0)
            return Done($"No clear position within {cap} was found for {me}: each of the {settled.Count} places checked still touches {them} or touches something new." + Then,
                        $"nudge {what}: each of the {settled.Count} positions judged still crosses the other object or crosses something new ({ans.Neighbours} objects near it, {ans.NoGeometry} with no readable shape)");
        int tried = 0;
        var reasons = new List<string>();
        foreach (var c in clear.Take(MaxProofs))
        {
            ct.ThrowIfCancellationRequested();
            tried++;
            Say($"proving {tried}", FormattableString.Invariant($"nudge {what}: the probes are checking position {tried} of at most {MaxProofs} ({NudgeRule.Move(home, c.Pose)}), about 150-220 s"));
            var clock = System.Diagnostics.Stopwatch.StartNew();
            TrialVerdict v;
            TrialRecheck? rc = null;
            try { rc = await _prove(new PendingEdit(key, home, c.Pose), c.Candidate.N, ct).ConfigureAwait(false); v = NudgeProofRule.Verdict(rc); }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex) { v = new TrialVerdict("not checked", "not checked: " + (ex is AggregateException ae ? ae.GetBaseException() : ex).Message, Array.Empty<string>()); }
            lock (_lock) _proofs.Add(new NudgeProofRun(c.Candidate.N, v.Word, v.Sentence, clock.Elapsed.TotalSeconds));
            _log?.Invoke($"nudge {what}: position {c.Candidate.N} ({NudgeRule.Move(home, c.Pose)}): {v.Sentence}");
            // confirmed: the open row resolved and nothing was created ("clear"), or the same with only crossings shown to have
            // been there before the move still there ("resolved here, but", checked by NudgeProofRule.NotThereBefore)
            if (v.Word is not ("clear" or "resolved here, but"))
            {
                reasons.Add(v.Word switch
                {
                    "new conflict" => "it would make a new clash or leave something in the air",
                    "not confirmed" or "unmeasured" or "not checked" => "the probes could not measure all of it",
                    _ => $"it did not clear {them}",
                });
                continue;
            }
            var stillKeys = (rc!.OthersOfRef.Where(o => o.Word != "resolved").Select(o => o.Id).Concat(rc.StillHolding))
                .Select(NudgeProofRule.PairOf).Where(p => p is not null).SelectMany(p => p!.Split('|'))
                .Where(k => !string.Equals(k, key.ToString(), StringComparison.OrdinalIgnoreCase)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            var still = v.Word == "clear" ? "" : $"; still there from before the move, as the probes say it: {v.Sentence.Replace("resolved here, but: ", "").TrimEnd('.')}";
            var note = FormattableString.Invariant($"moved {NudgeRule.Move(home, c.Pose)} from where the load order places it, {c.Candidate.Rule}; {c.Drop}; the nearest of {clear.Count} clear position(s) the judge found among {settled.Count} within {cap}, confirmed by the probes on try {tried}{still}; {NudgeRule.Checked}; {NudgeRule.RoutesNotChecked}");
            var staged = _stage(c.Pose, note);
            if (staged.StartsWith("nothing is staged", StringComparison.Ordinal)) return Done(staged, staged);
            var touches = stillKeys.Count == 0 ? "" : $", and it still touches {string.Join(" and ", stillKeys.Select(_nameOf))}, as it did before the move";
            return Done($"Staged a nudge of {me}: {NudgeRule.Move(home, c.Pose)} from where the load order places it. It is clear of {them} and nothing is left in the air{touches}. Walking routes were not checked, and nothing is written until you commit.",
                        $"nudge {what}: {staged}");
        }
        return Done($"The probes did not confirm any of the {tried} nearest places that looked clear for {me}: {string.Join("; ", reasons.Distinct())}." + Then,
                    $"nudge {what}: the probes confirmed none of the {tried} nearest clear position(s) within {cap} ({string.Join(", ", Proofs.Select(p => $"position {p.N} {p.Word}"))})");
    }

    string Done(string said, string detail) { _log?.Invoke(detail); Say("done", said); return said; }
}
