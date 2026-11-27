using System.Globalization;
using System.Numerics;
using System.Text.Json;
using HarnessShared;
using HousecarlCore;
using SkyWright.Engine;
using Mutagen.Bethesda.Plugins;
using ViewProbe;

namespace Workbench;

/// <summary>One candidate position: its number, the horizontal move from the reference's load-order place, and the rule
/// that made it, in words.</summary>
internal sealed record Candidate(int N, Vector3 Move, string Rule);

/// <summary>Item 8's candidates, generated and never judged: from the pair's two boxes, the smallest move along +x, -x, +y
/// and -y that separates them, at 1x and 1.5x that distance, nearest first.</summary>
internal static class Candidates
{
    internal const int Default = 3, Max = 6;

    internal static IReadOnlyList<Candidate> Generate(Aabb subject, Aabb other, int max = Default)
    {
        var dirs = new (string Name, float D, Vector2 U)[]
        {
            ("+x (east)", other.Max.X - subject.Min.X, new Vector2(1, 0)), ("-x (west)", subject.Max.X - other.Min.X, new Vector2(-1, 0)),
            ("+y (north)", other.Max.Y - subject.Min.Y, new Vector2(0, 1)), ("-y (south)", subject.Max.Y - other.Min.Y, new Vector2(0, -1)),
        };
        var list = new List<(float Dist, int Dir, float Factor, Candidate C)>();
        for (int i = 0; i < dirs.Length; i++)
        {
            var (name, d, u) = dirs[i];
            if (!(d > 0)) continue;
            foreach (var f in new[] { 1f, 1.5f })
            {
                float dist = d * f;
                list.Add((dist, i, f, new Candidate(0, new Vector3(u.X * dist, u.Y * dist, 0),
                    FormattableString.Invariant($"{(f == 1f ? "1x" : "1.5x")} the smallest move along {name} that separates the two boxes ({d:0.#} units)"))));
            }
        }
        return list.OrderBy(x => x.Dist).ThenBy(x => x.Dir).ThenBy(x => x.Factor).Take(Math.Clamp(max, 1, Max))
                   .Select((x, i) => x.C with { N = i + 1 }).ToList();
    }
}

/// <summary>A candidate's word and sentence, and the rows it names.</summary>
internal sealed record TrialVerdict(string Word, string Sentence, IReadOnlyList<string> Rows);

/// <summary>Item 8's words over the frozen re-check rule (F11): "clear" only when the open row resolved, every other row
/// naming the moved reference resolved, no carried predicate of it still holds and nothing was created. A created row
/// outranks an unresolved one, and both are named.</summary>
internal static class ClearRule
{
    internal const string ClearLimit = "clear of every conflict the re-check measures between mods; the moved object is not measured against scenery no mod is responsible for, and a crossing already there before the move is not counted";

    /// <summary>When a clear candidate's contact read or drop names crossings: one sentence saying the re-check counts none of them, and why.</summary>
    internal static string CrossingNote(IReadOnlyCollection<string> crossings) =>
        $" Its contact read or drop names a crossing with {string.Join(", ", crossings)}; the re-check counts none of these as a conflict: each is either scenery no mod is responsible for, or a crossing that was already there before the move.";

    /// <summary>What each word means, said once per answer.</summary>
    internal static readonly IReadOnlyDictionary<string, string> Meanings = new Dictionary<string, string>
    {
        ["clear"] = ClearLimit,
        ["resolved here, but"] = "the open row resolved, but another row naming the moved object did not, or a measured crossing of it still holds",
        ["new conflict"] = "the re-check found a row with the moved object that was not there before",
        ["still there"] = "the re-run finds the open row as before",
        ["reclassified"] = "the open row changed class without its physical predicate going away, so it is not shown resolved",
        ["unmeasured"] = "a predicate the open row rests on could not be measured",
        ["not in either run"] = "the re-check's population does not reach the open row",
        ["not checked"] = "the re-check could not run; its sentence says why",
    };

    internal static TrialVerdict Judge(RowVerdict open, IReadOnlyList<RowVerdict> othersOfRef, IReadOnlyList<RowVerdict> created,
                                       IReadOnlyList<string> stillHolding)
    {
        var unresolved = othersOfRef.Where(o => o.Word != "resolved").ToList();
        string Others() => string.Join("; ", unresolved.Select(o => $"{o.Id} reads {o.Word}").Concat(stillHolding.Select(h => $"{h} still holds")));
        if (created.Count > 0)
        {
            var rows = created.Select(c => c.Id).Concat(unresolved.Select(o => o.Id)).Concat(stillHolding).ToList();
            return new("new conflict", $"new conflict: the re-check found {created.Count} new row(s) with the moved object ({string.Join(", ", created.Select(c => c.Id))}); the open row reads {open.Word}"
                                       + (rows.Count > created.Count ? "; " + Others() : "") + ".", rows);
        }
        if (open.Word == "resolved")
        {
            if (unresolved.Count == 0 && stillHolding.Count == 0) return new("clear", $"clear: {ClearLimit}.", Array.Empty<string>());
            return new("resolved here, but", $"resolved here, but: the open row resolved; {Others()}.", unresolved.Select(o => o.Id).Concat(stillHolding).ToList());
        }
        return new(open.Word, open.Sentence, Array.Empty<string>());
    }

    /// <summary>Every unpatched row naming the moved reference (either side, any case) other than the open row, with its
    /// frozen word from the two emits.</summary>
    internal static IReadOnlyList<RowVerdict> OthersOfRef(string unpatchedEmit, string patchedEmit, string openRowId, string refKey)
    {
        var credited = DifferentialRows.Credit(ConflictIo.Read(unpatchedEmit), ConflictIo.Read(patchedEmit));
        bool Is(string? k) => string.Equals(k, refKey, StringComparison.OrdinalIgnoreCase);
        return credited.Before.Where(kv => kv.Key != openRowId && (Is(kv.Value.FormKey) || Is(kv.Value.Other)))
                              .OrderBy(kv => kv.Key, StringComparer.Ordinal).Select(kv => RecheckRule.Of(credited, kv.Key)).ToList();
    }

    /// <summary>The carried predicates of the patched emit that still hold for the moved reference, in words.</summary>
    internal static IReadOnlyList<string> StillHolding(string patchedEmitPath, string refKey)
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(patchedEmitPath));
        var list = new List<string>();
        if (!doc.RootElement.TryGetProperty("Predicates", out var ps) || ps.ValueKind != JsonValueKind.Array) return list;
        static string? S(JsonElement e, string k) => e.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
        foreach (var p in ps.EnumerateArray())
        {
            string? a = S(p, "FormKey"), b = S(p, "Other");
            if (!string.Equals(S(p, "Physical"), "Holds", StringComparison.OrdinalIgnoreCase)) continue;
            if (!string.Equals(a, refKey, StringComparison.OrdinalIgnoreCase) && !string.Equals(b, refKey, StringComparison.OrdinalIgnoreCase)) continue;
            list.Add($"{a} / {b}: holds ({S(p, "Detail") ?? "no detail"})");
        }
        return list;
    }
}

/// <summary>A candidate as the placements part shows it.</summary>
internal sealed record TrialPlacement(int N, RefPlacementEdit.Pose Pose, string Move, string Drop, IReadOnlyList<string> Contacts,
                                      TrialVerdict? Verdict, double? Seconds, IReadOnlyList<string> Checked, IReadOnlyList<string> NotChecked,
                                      string? PatchFolder);

/// <summary>What one trial's re-check gives the lane: the result, the other rows naming the moved reference and their words,
/// the carried predicates still holding for it, and the population line.</summary>
internal sealed record TrialRecheck(RecheckResult Result, IReadOnlyList<RowVerdict> OthersOfRef, IReadOnlyList<string> StillHolding, string Population,
                                   IReadOnlyList<RowVerdict>? ContactNew = null, IReadOnlySet<string>? HeldBefore = null,
                                   RouteArmResult? Route = null);

/// <summary>Item 8's trial lane: each candidate written as its own patch under the Work Folder's trials folder from a made-up
/// net edit (load-order pose to candidate pose), never through the edit session, and judged by the re-check, one at a time.</summary>
internal sealed class TrialLane
{
    internal delegate Task<string> WriteTrial(PendingEdit edit, string patch, string outPath);
    internal delegate Task<TrialRecheck> RecheckTrial(string patch, string patchFolder, string workDir, bool contact, CancellationToken ct);

    /// <summary>The measured cost of one candidate (both runs), in seconds: the low and high of the Gate A probe.</summary>
    internal const double SecondsLow = 67, SecondsHigh = 76;

    readonly string? _work;
    readonly WriteTrial _write;
    readonly RecheckTrial _recheck;
    readonly Func<RefPlacementEdit.Pose, (RefPlacementEdit.Pose Pose, string Drop)> _settle;
    readonly Func<RefPlacementEdit.Pose, IReadOnlyList<string>> _contactsAt;
    readonly object _lock = new();
    List<TrialPlacement> _placements = new();
    Task? _running;
    string? _cell;
    RefPlacementEdit.Pose _home;
    int _ask;

    internal TrialLane(string? workFolder, WriteTrial write, RecheckTrial recheck,
                       Func<RefPlacementEdit.Pose, (RefPlacementEdit.Pose Pose, string Drop)> settle,
                       Func<RefPlacementEdit.Pose, IReadOnlyList<string>> contactsAt)
    { _work = workFolder; _write = write; _recheck = recheck; _settle = settle; _contactsAt = contactsAt; }

    internal Task? Running => _running;
    internal IReadOnlyList<TrialPlacement> Placements { get { lock (_lock) return _placements.ToList(); } }
    internal string? Cell => _cell;
    /// <summary>The reference the last ask was for.</summary>
    internal FormKey? Key { get; private set; }
    internal string? WorkFolder => _work;

    /// <summary>Start the trials of <paramref name="key"/> from its load-order pose; returns the cost sentence at once. Each
    /// candidate is snapped to the grid on the axes it moved and settled by the drop on the calling thread; the writes and
    /// re-checks then run in the background, one at a time.</summary>
    internal string Start(FormKey key, string? cell, RefPlacementEdit.Pose loadOrder, IReadOnlyList<Candidate> candidates, string probe,
                          int otherStaged, bool writtenSame, float grid = 0f)
    {
        if (_work is null)
            throw new InvalidOperationException("with the Output Folder at the instance's mods folder there is no default Work Folder, so trial patches have nowhere to go; choose one outside the instance and the game folder (workbench_act verb set_work_folder).");
        if (_running is { IsCompleted: false })
            throw new InvalidOperationException("a find_clear is already running; read workbench_state with project [\"placements\"] and wait_seconds for it.");
        if (candidates.Count == 0) throw new InvalidOperationException("no candidate position: along no horizontal axis does a move separate the two boxes.");
        Key = key;
        var trials = Path.Combine(_work, "trials");
        var ask = Path.Combine(trials, DateTime.UtcNow.ToString("yyyyMMdd'T'HHmmss'Z'", CultureInfo.InvariantCulture) + "-" + (++_ask));
        var made = new List<TrialPlacement>();
        foreach (var c in candidates)
        {
            var target = loadOrder with { Position = new Vec3(loadOrder.Position.X + c.Move.X, loadOrder.Position.Y + c.Move.Y, loadOrder.Position.Z) };
            if (grid > 0) target = MoveRules.Snap(loadOrder, target, grid, 0f);
            var (pose, drop) = _settle(target);
            made.Add(new TrialPlacement(c.N, pose, FormattableString.Invariant($"{c.Rule}: moved {pose.Position.X - loadOrder.Position.X:0.#}, {pose.Position.Y - loadOrder.Position.Y:0.#}, {pose.Position.Z - loadOrder.Position.Z:0.#} units (x, y, z) from its load-order place"),
                drop, _contactsAt(pose), null, null, new[] { $"{probe}: the open row's own check, re-run on the trial patch's cell with the bundle's build" },
                NotCheckedFor(probe, "read when the re-check runs"), Path.Combine(ask, c.N.ToString(CultureInfo.InvariantCulture))));
        }
        lock (_lock) _placements = made;
        _cell = cell; _home = loadOrder;
        int kept = Directory.Exists(trials) ? Directory.GetDirectories(trials).Length : 0;
        var said = CostSentence(made.Count) + $" Trial patches are kept in {trials} ({kept + 1} ask(s) there, {made.Count} patch(es) in this one); the tool never deletes them.";
        if (otherStaged > 0) said += $" {otherStaged} other staged edits are not in the trials: each trial holds only {key}'s move from its load-order place.";
        if (writtenSame) said += $" {key}'s written edit is not in the trials either: each trial starts from its load-order place.";
        _running = Task.Run(() => RunAsync(key, loadOrder, ask, probe));
        return said;
    }

    async Task RunAsync(FormKey key, RefPlacementEdit.Pose loadOrder, string ask, string probe)
    {
        foreach (var p in Placements)
        {
            var clock = System.Diagnostics.Stopwatch.StartNew();
            TrialPlacement done;
            try
            {
                var patch = $"WbTrial{_ask}c{p.N}";
                var outPath = p.PatchFolder!;
                Directory.CreateDirectory(outPath);
                await _write(new PendingEdit(key, loadOrder, p.Pose), patch, outPath).ConfigureAwait(false);
                var rc = await _recheck(patch, outPath, outPath + "-recheck", false, CancellationToken.None).ConfigureAwait(false);
                var verdict = ClearRule.Judge(rc.Result.Row, rc.OthersOfRef, rc.Result.New, rc.StillHolding);
                if (verdict.Word == "clear" && Crossings(p) is { Count: > 0 } crossed) verdict = verdict with { Sentence = verdict.Sentence + ClearRule.CrossingNote(crossed) };
                done = p with
                {
                    Verdict = verdict, Seconds = clock.Elapsed.TotalSeconds,
                    Checked = new[] { $"{probe}: the open row's own check, re-run on the trial patch's cell ({rc.Result.BuildSentence})" },
                    NotChecked = NotCheckedFor(probe, rc.Population),
                };
            }
            catch (Exception ex)
            {
                var msg = (ex is AggregateException ae ? ae.GetBaseException() : ex).Message;
                done = p with { Verdict = new TrialVerdict("not checked", "not checked: " + msg, Array.Empty<string>()), Seconds = clock.Elapsed.TotalSeconds };
            }
            lock (_lock) _placements = _placements.Select(x => x.N == p.N ? done : x).ToList();
        }
    }

    /// <summary>A nudge's proof (Gate C): one candidate written as its own trial patch under the Work Folder's trials folder (never
    /// the Output Folder, never enabled) from a made-up net edit, then re-checked by the open row's probe, with contactprobe when
    /// <paramref name="contact"/>. The one path find_clear's trials take too.</summary>
    internal async Task<TrialRecheck> ProveAsync(PendingEdit edit, int n, bool contact, CancellationToken ct)
    {
        if (_work is null)
            throw new InvalidOperationException("with the Output Folder at the instance's mods folder there is no default Work Folder, so trial patches have nowhere to go; choose one outside the instance and the game folder (workbench_act verb set_work_folder).");
        Key = edit.Ref;
        int ask = Interlocked.Increment(ref _ask);
        var folder = Path.Combine(_work, "trials", DateTime.UtcNow.ToString("yyyyMMdd'T'HHmmss'Z'", CultureInfo.InvariantCulture) + "-nudge-" + ask, n.ToString(CultureInfo.InvariantCulture));
        Directory.CreateDirectory(folder);
        var patch = $"WbNudge{ask}c{n}";
        ct.ThrowIfCancellationRequested();
        await _write(edit with { Nudge = null }, patch, folder).ConfigureAwait(false);
        return await _recheck(patch, folder, folder + "-recheck", contact, ct).ConfigureAwait(false);
    }

    /// <summary>The references a candidate crosses, by its contact read ("X crosses it") and its refused drop ("X crosses …").</summary>
    static IReadOnlyCollection<string> Crossings(TrialPlacement p)
    {
        var keys = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var line in p.Contacts.Append(p.Drop))
        {
            var i = line.IndexOf(" crosses ", StringComparison.Ordinal);
            if (i <= 0) continue;
            var who = line[..i].Split(' ')[0];
            if (FormKey.TryFactory(who, out _)) keys.Add(who);
        }
        return keys;
    }

    /// <summary>The pose to stage for candidate <paramref name="n"/>, or why not.</summary>
    internal (RefPlacementEdit.Pose? Pose, string Said) Choose(int n, string? cell, RefPlacementEdit.Pose current)
    {
        var p = Placements.FirstOrDefault(x => x.N == n);
        if (p is null) return (null, $"there is no candidate {n}; read workbench_state with project [\"placements\"] to see them.");
        if (!string.Equals(cell, _cell, StringComparison.OrdinalIgnoreCase))
            return (null, $"candidate {n} was made in cell {_cell}, and the scene now shows cell {cell}; ask again in this cell.");
        if (current != _home)
            return (null, $"the reference was edited since the candidates were made (it is no longer at its load-order place), so candidate {n} no longer fits; revert it or ask again.");
        return (p.Pose, $"staged candidate {n} ({p.Verdict?.Word ?? "not judged yet"}): {p.Move}; nothing is written until commit");
    }

    /// <summary>The candidate's settle rule: raised 20 units and lowered by the drop onto what is below; a refused drop keeps
    /// the candidate at the reference's own height, and the refusal is its drop sentence.</summary>
    internal static (RefPlacementEdit.Pose Pose, string Drop) SettleBy(RefPlacementEdit.Pose target, Func<RefPlacementEdit.Pose, CaptureViews.DropAnswer> drop)
    {
        var raised = target with { Position = new Vec3(target.Position.X, target.Position.Y, target.Position.Z + 20f) };
        var d = drop(raised);
        if (d.Refusal is not null || d.Distance is not { } dist) return (target, d.Refusal ?? "the drop gave no distance");
        var onto = d.Onto == CaptureViews.TerrainKey ? "the terrain" : $"{d.Onto} ({d.OntoName})";
        return (raised with { Position = new Vec3(raised.Position.X, raised.Position.Y, raised.Position.Z - dist) },
                FormattableString.Invariant($"raised 20 units and lowered {dist:0.##} units onto {onto}; drop lowers onto drawn surfaces (render triangles), not collision"));
    }

    internal static string CostSentence(int candidates) => FormattableString.Invariant(
        $"checking {candidates} positions, about {candidates * SecondsLow / 60:0.0} to {candidates * SecondsHigh / 60:0.0} minutes in all ({SecondsLow:0}-{SecondsHigh:0} s each: the open row's own probe runs twice per candidate); the view stays usable, and each candidate is reported as it lands.");

    /// <summary>What a re-check of this probe does not check, said with every candidate.</summary>
    internal static IReadOnlyList<string> NotCheckedFor(string probe, string population) => new[]
    {
        probe == "overlapprobe" ? "the other placed-object checks: geometry.placement-contact and geometry.fixture-empty-space" : "the other placed-object check: geometry.object-overlap",
        "navmesh (geometry.navmesh-ownership): the context panel shows the navmesh under it, not judged",
        "doors and what names it: the context panel shows them, not judged",
        "what rests on it stays where it is when it moves (choose and the commit say which; move_with carries them)",
        "collision: drop and the overlap check read render triangles",
        "scenery no mod is responsible for: the re-check pairs the moved object only with references a mod is responsible for",
        $"anything outside the trial patch's population ({population})",
    };
}
