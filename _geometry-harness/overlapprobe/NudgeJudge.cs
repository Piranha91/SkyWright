using System.Diagnostics;
using System.Text.Json;
using HarnessShared;
using Mutagen.Bethesda.Plugins;
using SkyWright.Engine;

/// <summary>A nudge candidate's word from the cheap judge (Gate C layer 1): clear, or why not. <paramref name="Inherited"/>:
/// what it crosses that it already crossed at its load-order place, which is not held against it.</summary>
public sealed record NudgeVerdict(int N, bool Clear, bool OtherHit, IReadOnlyList<(FormKey Key, string Who, string Relation)> NewCrossings,
                                  IReadOnlyList<FormKey> NewUnknown, IReadOnlyList<FormKey> Inherited);

/// <summary>overlapprobe --nudge: the cheap judge of a nudge's candidate positions (Gate C). It only chooses; the proof is the
/// probes' re-check of a trial patch, and nothing here says "resolved".
///
/// The rule, its one owner: a candidate is rejected when the subject there still crosses the row's other object, or crosses any
/// object it did not cross at its load-order place (another mod's, the same mod's or the base game's: a nudge must not push an
/// object into a wall the probes never pair), or meets a pair the mesh query cannot decide that was decided at home. Crossings
/// already there at home are not held against it. The predicate is the one this probe pairs with
/// (<see cref="SurfaceQueryOutcome.QueryPlaced"/>), over the same candidates (its geometry, state and marker rules), the base
/// game included; the neighbourhood is chosen by bounds (Gate A side discovery 5): every reference of the 3x3 grid squares
/// around the subject (its cell, for an interior), kept when its bounds come within <c>reach</c> of the subject's.</summary>
public static class NudgeJudge
{
    /// <summary>The subject at its load-order place and at each pose (its rotation and scale kept) against every neighbour whose
    /// bounds reach it.</summary>
    public static (NudgeVerdict Home, IReadOnlyList<NudgeVerdict> Candidates) JudgeAll(SurfaceOverlapCandidate subject, FormKey other,
        IReadOnlyList<SurfaceOverlapCandidate> near, IReadOnlyList<(int N, Vec3 Pos)> poses, Func<int, bool> isBaseGame, Func<int, string> pluginName)
    {
        string Who(SurfaceOverlapCandidate c) => isBaseGame(c.Win.Plugin) ? "base game" : c.Win.Plugin == subject.Win.Plugin ? "same mod" : "other mod";
        (bool OtherHit, List<(SurfaceOverlapCandidate C, string Rel)> Hits, List<FormKey> Unknown) At(Vec3 pos)
        {
            var world = SpatialOverlap.TransformSoup(subject.Model!, pos, subject.Win.Rot, subject.Win.Scale);
            var bounds = SpatialOverlap.SoupBounds(world);
            var placed = new SurfaceQueryOutcome.Placed(subject.Model!, pos, subject.Win.Rot, subject.Win.Scale);
            var hits = new List<(SurfaceOverlapCandidate, string)>(); var unknown = new List<FormKey>(); bool otherHit = false;
            foreach (var c in near)
            {
                if (c.Key == subject.Key || c.Win.Space != subject.Win.Space || c.Placed is null) continue;
                if (SpatialOverlap.BoxDistance(bounds, c.WorldBounds) > 1e-3f) continue;
                var q = SurfaceQueryOutcome.QueryPlaced(world, placed, c.WorldSoup, c.Placed, 1e-5, 2_000_000, TimeSpan.FromSeconds(120));
                if (q.Intersects == true) { if (c.Key == other) otherHit = true; else hits.Add((c, q.SurfaceRelation)); }
                else if (q.Intersects is null) unknown.Add(c.Key);
            }
            return (otherHit, hits, unknown);
        }
        var home = At(subject.Win.Pos);
        var homeKeys = home.Hits.Select(h => h.C.Key).ToHashSet();
        var homeUnknown = home.Unknown.ToHashSet();
        var homeVerdict = new NudgeVerdict(0, false, home.OtherHit, Array.Empty<(FormKey, string, string)>(), home.Unknown, home.Hits.Select(h => h.C.Key).ToList());
        var list = new List<NudgeVerdict>();
        foreach (var (n, pos) in poses)
        {
            var j = At(pos);
            var fresh = j.Hits.Where(h => !homeKeys.Contains(h.C.Key)).Select(h => (h.C.Key, Who(h.C), h.Rel)).ToList();
            var freshUnknown = j.Unknown.Where(u => !homeUnknown.Contains(u)).ToList();
            var inherited = j.Hits.Where(h => homeKeys.Contains(h.C.Key)).Select(h => h.C.Key).ToList();
            list.Add(new NudgeVerdict(n, !j.OtherHit && fresh.Count == 0 && freshUnknown.Count == 0, j.OtherHit, fresh, freshUnknown, inherited));
        }
        return (homeVerdict, list);
    }

    static (int X, int Y) Grid(Vec3 p) => ((int)MathF.Floor(p.X / 4096f), (int)MathF.Floor(p.Y / 4096f));

    /// <summary>Which references the judge reads geometry for: the subject's 3x3 grid squares in its worldspace, or its cell.</summary>
    public static bool InNeighbourhood(Snap subject, Snap s)
    {
        if (subject.Interior) return s.Interior && s.Cell == subject.Cell;
        if (s.Worldspace != subject.Worldspace) return false;
        var a = Grid(subject.Pos); var b = Grid(s.Pos);
        return Math.Abs(a.X - b.X) <= 1 && Math.Abs(a.Y - b.Y) <= 1;
    }

    /// <summary>The --nudge mode: read the request, judge, write the answer; exit 0 with an answer, 2 with a one-sentence
    /// refusal in the answer and on stderr.</summary>
    public static int Run(string requestPath, string outPath, OverlapIndex idx, Func<Func<FormKey, Snap, bool>, List<SurfaceOverlapCandidate>> candidatesNear,
                          Func<int, bool> isBaseGame, Func<int, string> pluginName, Func<FormKey, bool> drawnWithoutGeometry)
    {
        var clock = Stopwatch.StartNew();
        int Refuse(string why)
        {
            File.WriteAllText(outPath, JsonSerializer.Serialize(new { schema = "nudge-answer/1", refused = why }, new JsonSerializerOptions { WriteIndented = true }));
            Console.Error.WriteLine(why);
            return 2;
        }
        JsonDocument req;
        try { req = JsonDocument.Parse(File.ReadAllText(requestPath)); }
        catch (Exception ex) when (ex is IOException or JsonException) { return Refuse($"the nudge request {requestPath} could not be read ({ex.Message}); ask for the nudge again."); }
        using var _ = req;
        var root = req.RootElement;
        if (!FormKey.TryFactory(root.GetProperty("subject").GetString() ?? "", out var sk) || !FormKey.TryFactory(root.GetProperty("other").GetString() ?? "", out var ok))
            return Refuse("the nudge request does not name its object and the conflict's other object as FormKeys; ask for the nudge again.");
        float reach = root.TryGetProperty("reach", out var r) ? r.GetSingle() : 137f;
        if (idx.WinningGeometryPose(sk) is not { } seed) return Refuse($"{sk} is not placed in this load order, so it has nothing to judge; reopen the scene.");
        var cands = candidatesNear((k, s) => InNeighbourhood(seed, s));
        var byKey = cands.ToDictionary(c => c.Key);
        if (!byKey.TryGetValue(sk, out var sub))
            return Refuse($"{sk} has no complete drawn shape this probe can read (or it is a marker or not present at load), so where it may move cannot be judged; move it by hand.");
        if (!byKey.ContainsKey(ok))
            return Refuse($"the conflict's other object {ok} has no complete drawn shape this probe can read, so a clear position cannot be judged; move it by hand.");
        float[] P(JsonElement e) => e.EnumerateArray().Select(x => x.GetSingle()).ToArray();
        if (root.TryGetProperty("home", out var h) && P(h) is { Length: 3 } hp
            && (MathF.Abs(hp[0] - sub.Win.Pos.X) > 0.5f || MathF.Abs(hp[1] - sub.Win.Pos.Y) > 0.5f || MathF.Abs(hp[2] - sub.Win.Pos.Z) > 0.5f))
            return Refuse($"the load order this probe read places {sk} at {sub.Win.Pos}, not where the scene has it ({hp[0]:0.##}, {hp[1]:0.##}, {hp[2]:0.##}); reopen the scene so both read the same load order.");
        var home = sub.WorldBounds;
        var box = new Aabb(new Vec3(home.Min.X - reach, home.Min.Y - reach, home.Min.Z - reach), new Vec3(home.Max.X + reach, home.Max.Y + reach, home.Max.Z + reach), false);
        var near = cands.Where(c => c.Key != sk && c.Win.Space == sub.Win.Space && SpatialOverlap.BoxDistance(box, c.WorldBounds) <= 0).ToList();
        int noGeometry = idx.History.Refs.Select(kv => kv.Key).Count(k => k != sk && idx.WinningGeometryPose(k) is { } s && s.Space == sub.Win.Space
                                                         && MathF.Abs(s.Pos.X - sub.Win.Pos.X) <= reach + 512 && MathF.Abs(s.Pos.Y - sub.Win.Pos.Y) <= reach + 512
                                                         && !byKey.ContainsKey(k) && drawnWithoutGeometry(k));
        var poses = root.GetProperty("candidates").EnumerateArray().Select(c => (c.GetProperty("n").GetInt32(), P(c.GetProperty("position")) is var p ? new Vec3(p[0], p[1], p[2]) : default)).ToList();
        var (homeV, verdicts) = JudgeAll(sub, ok, near, poses, isBaseGame, pluginName);
        string Name(FormKey k) => byKey.TryGetValue(k, out var c) ? $"{k} [{pluginName(c.Win.Plugin)}]" : k.ToString();
        var answer = new
        {
            schema = "nudge-answer/1", subject = sk.ToString(), other = ok.ToString(), refused = (string?)null,
            rule = "rejected when it still crosses the other object, or crosses any object (another mod's, the same mod's or the base game's) it did not cross at its load-order place, or meets a pair the mesh query cannot decide; crossings already there at home are not held against it",
            home = new { otherHit = homeV.OtherHit, crossings = homeV.Inherited.Select(Name).ToList(), undecided = homeV.NewUnknown.Select(Name).ToList() },
            neighbours = near.Count, noGeometry,
            candidates = verdicts.Select(v => new
            {
                n = v.N, clear = v.Clear, otherHit = v.OtherHit,
                newCrossings = v.NewCrossings.Select(c => $"{c.Key} ({c.Who}, {Name(c.Key).Split('[', 2)[1].TrimEnd(']')})").ToList(),
                newUndecided = v.NewUnknown.Select(Name).ToList(), inherited = v.Inherited.Select(Name).ToList(),
            }).ToList(),
            seconds = clock.Elapsed.TotalSeconds,
        };
        File.WriteAllText(outPath, JsonSerializer.Serialize(answer, new JsonSerializerOptions { WriteIndented = true }));
        Console.WriteLine($"nudge: {sk} against {near.Count:N0} neighbour(s) by bounds ({noGeometry} near it with no readable shape); {verdicts.Count(v => v.Clear)} of {verdicts.Count} candidate(s) clear; {clock.Elapsed.TotalSeconds:0.0} s -> {outPath}");
        return 0;
    }
}
