using HarnessShared;
using HousecarlCore;
using Mutagen.Bethesda.Plugins;

// CHECK 2's DECISIONS (Program.cs): where a package puts an actor, which alias pairs the quest states, which pairs came
// apart and survive the reach filter, and which of them are emitted. Held in their own classes so --self-test runs exactly
// the code a real run runs; Program.cs keeps the console text, the sweep and the emit writing.

/// <summary>Where an alias's reference can stand: a non-actor stays at its placement; an actor by its packages, resolved
/// against the winning order (the DECISION on what the numbers mean is DetectorModel.ClassifyReach).</summary>
sealed class ActorReach
{
    readonly Dictionary<FormKey, PackFacts> packFacts; readonly Func<FormKey, Snap?> Winning;
    readonly Dictionary<FormKey, FormKey> refBase; readonly Dictionary<FormKey, FormKey[]> npcPackages; readonly HashSet<FormKey> npcTemplated;

    public ActorReach(Dictionary<FormKey, PackFacts> packFacts, Func<FormKey, Snap?> winning, Dictionary<FormKey, FormKey> refBase,
                      Dictionary<FormKey, FormKey[]> npcPackages, HashSet<FormKey> npcTemplated)
    {
        this.packFacts = packFacts; Winning = winning; this.refBase = refBase; this.npcPackages = npcPackages; this.npcTemplated = npcTemplated;
    }

    public DetectorModel.PackageAnchor? AnchorOf(FormKey packageKey, Snap at)
    {
        if (!packFacts.TryGetValue(packageKey, out var f)) return null;
        if (f.Locations.Count == 0)
            return new DetectorModel.PackageAnchor(false, false, false, null, 0, f.Scheduled, f.ScheduleMinutes);

        bool allAtPlacement = true, anyUnresolved = false;
        float? worst = null;
        uint reach = 0;
        foreach (var (arm, radius, link) in f.Locations)
        {
            if (arm == PackFacts.EditorArm || arm == PackFacts.SelfArm) { reach = Math.Max(reach, radius); continue; }
            allAtPlacement = false;
            // Only a placed reference has a position this order can read. A cell, a keyword, an
            // object type, an alias and the player all resolve at run time or not at all.
            if (arm != PackFacts.RefArm || link.IsNull || Winning(link) is not { } w || w.Cell != at.Cell)
            { anyUnresolved = true; continue; }
            // HORIZONTAL distance. A package radius bounds where an actor wanders on the floor; the
            // anchor for a temple's sandbox sits 328u above the priests' heads, and counting that gap
            // put Rorlund 8u outside a radius he is 404u inside of.
            float d = DistXY(w.Pos, at.Pos);
            if (d > radius) worst = MathF.Max(worst ?? 0f, d);
            else reach = Math.Max(reach, (uint)MathF.Ceiling(d + radius));
        }
        return new DetectorModel.PackageAnchor(true, allAtPlacement, anyUnresolved, worst, reach,
                                               f.Scheduled, f.ScheduleMinutes);
    }

    public (ReachVerdict Verdict, uint Radius, string Why) ReachOf(FormKey refKey, Snap at, bool byCoverage = false)
    {
        var baseKey = refBase.GetValueOrDefault(refKey);
        if (!npcPackages.TryGetValue(baseKey, out var pkgs))
            return (ReachVerdict.PlacementBound, 0, "not an actor: it stays where it is placed");
        return DetectorModel.ClassifyReach(pkgs.Select(k => AnchorOf(k, at)).ToArray(),
                                           npcTemplated.Contains(baseKey), byCoverage);
    }

    static float DistXY(Vec3 a, Vec3 b)
    {
        float dx = a.X - b.X, dy = a.Y - b.Y;
        return MathF.Sqrt(dx * dx + dy * dy);
    }
}


/// <summary>The alias pairs a quest STATES belong together: two actors of one of its scenes, or an alias whose package
/// targets another alias. Every other pair is proximity only.</summary>
static class AliasStated
{
    /// <summary>An unordered alias pair.</summary>
    public static (int, int) Pair(int a, int b) => a <= b ? (a, b) : (b, a);

    static bool IsPlayerRef(FormKey k) => k.ID == 0x14;

    public static FormKey? AliasRef(AliasInfo a, Dictionary<FormKey, List<FormKey>> npcPlaced)
    {
        if (a.Forced is { } f) return IsPlayerRef(f) ? null : f;
        if (a.UniqueNpc is { } n && npcPlaced.TryGetValue(n, out var l) && l.Count == 1) return l[0];
        return null;
    }

    public static Dictionary<FormKey, HashSet<(int, int)>> Of(Dictionary<FormKey, (FormKey Quest, int[] Actors)> scenes,
                                                             Dictionary<FormKey, QuestInfo> quests, Dictionary<FormKey, int[]> packageTargets)
    {
        var statedPairs = new Dictionary<FormKey, HashSet<(int, int)>>();   // quest -> unordered alias pairs

        void AddStated(FormKey quest, int a, int b)
        {
            if (a == b) return;
            if (!statedPairs.TryGetValue(quest, out var set)) statedPairs[quest] = set = new HashSet<(int, int)>();
            set.Add(Pair(a, b));
        }

        foreach (var sc in scenes.Values)
        {
            if (sc.Quest.IsNull || sc.Actors.Length < 2) continue;
            for (int i = 0; i < sc.Actors.Length; i++)
                for (int j = i + 1; j < sc.Actors.Length; j++)
                    AddStated(sc.Quest, sc.Actors[i], sc.Actors[j]);
        }

        foreach (var q in quests.Values)
            foreach (var a in q.Aliases)
                foreach (var pk in a.Packages)
                    if (packageTargets.TryGetValue(pk, out var targets))
                        foreach (var t in targets)
                            AddStated(q.Key, a.Id, t);
        return statedPairs;
    }
}
/// <summary>CHECK 2's split test over every winning quest: each alias resolved to its authoring view and its winning
/// placement (counted when it cannot be), each pair judged by DetectorModel.CameApart, then the reach filter, then stated
/// and patched. Each call returns its own coverage counts; nothing carries from one threshold to the next.</summary>
sealed class AliasSplits
{
    readonly Dictionary<FormKey, QuestInfo> quests; readonly Dictionary<FormKey, List<FormKey>> npcPlaced;
    readonly Dictionary<FormKey, HashSet<(int, int)>> statedPairs; readonly bool reachFilter; readonly ActorReach reach;
    readonly Func<AliasInfo, FormKey?> aliasRef; readonly Func<FormKey, int, Snap?> asSeenBy; readonly Func<FormKey, Snap?> winning;
    readonly Func<int, int, bool> patchExists; readonly Func<int, string> pluginName; readonly Func<FormKey, string> nameOf, cellOf;

    public AliasSplits(Dictionary<FormKey, QuestInfo> quests, Dictionary<FormKey, List<FormKey>> npcPlaced,
                       Dictionary<FormKey, HashSet<(int, int)>> statedPairs, bool reachFilter, ActorReach reach,
                       Func<AliasInfo, FormKey?> aliasRef, Func<FormKey, int, Snap?> asSeenBy, Func<FormKey, Snap?> winning,
                       Func<int, int, bool> patchExists, Func<int, string> pluginName, Func<FormKey, string> nameOf, Func<FormKey, string> cellOf)
    {
        this.quests = quests; this.npcPlaced = npcPlaced; this.statedPairs = statedPairs; this.reachFilter = reachFilter; this.reach = reach;
        this.aliasRef = aliasRef; this.asSeenBy = asSeenBy; this.winning = winning;
        this.patchExists = patchExists; this.pluginName = pluginName; this.nameOf = nameOf; this.cellOf = cellOf;
    }

    FormKey? AliasRef(AliasInfo a) => aliasRef(a);
    Snap? AsSeenBy(FormKey refKey, int byPlugin) => asSeenBy(refKey, byPlugin);
    Snap? Winning(FormKey refKey) => winning(refKey);
    (ReachVerdict Verdict, uint Radius, string Why) ReachOf(FormKey refKey, Snap at, bool byCoverage = false) => reach.ReachOf(refKey, at, byCoverage);
    bool PatchExists(int questPlugin, int moverPlugin) => patchExists(questPlugin, moverPlugin);
    string PluginName(int p) => pluginName(p);
    string NameOf(FormKey refKey) => nameOf(refKey);
    string CellOf(FormKey c) => cellOf(c);
    static (int, int) Pair(int a, int b) => AliasStated.Pair(a, b);
    static void Bump(Dictionary<string, int> d, string k) => d[k] = d.GetValueOrDefault(k) + 1;

    /// <summary>The splits --emit writes: stated ones only, each with the records a repair has to touch (the moved references);
    /// a stated split with nothing identifiable moved is not judgeable and not written.</summary>
    public static IEnumerable<(Split s, List<ConflictRecord> necessary)> Emitted(List<Split> split)
    {
        foreach (var s in split.Where(s => s.Stated))
        {
            // A repair has to reconcile the PLACEMENT that changed. Re-pointing the quest's alias is the
            // other legitimate fix and is reported as an alternative rather than scored, because a patch
            // that overrides the quest may well be doing something unrelated to this pair.
            var necessary = s.MovedRefs.Select(k => new ConflictRecord(k.ToString(), "moved-ref", s.CellThen)).ToList();
            if (necessary.Count == 0) continue;                       // nothing identifiable moved; not judgeable
            yield return (s, necessary);
        }
    }

    /// <summary>The pairs that came apart at the "together" threshold <paramref name="nearU"/>, and what this call judged
    /// and could not.</summary>
    public (List<Split> Split, SplitCoverage Coverage) Find(float nearU)
    {
        var split = new List<Split>();
        long skipNonPositional = 0, skipMultiPlacement = 0, skipNoAuthoringView = 0, skipNoWinning = 0;
        long aliasesResolved = 0, pairsJudged = 0, reachOverlap = 0, reachReadingDisagrees = 0;
        var reachDropped = new Dictionary<string, int>();
        foreach (var q in quests.Values)
        {
            var resolved = new List<(AliasInfo A, FormKey Ref, Snap Then, Snap Now)>();
            foreach (var a in q.Aliases)
            {
                if (AliasRef(a) is not { } rk)
                {
                    if (a.UniqueNpc is { } n0 && npcPlaced.TryGetValue(n0, out var l0) && l0.Count > 1) skipMultiPlacement++;
                    else skipNonPositional++;
                    continue;
                }
                if (AsSeenBy(rk, q.Plugin) is not { } then) { skipNoAuthoringView++; continue; }
                if (Winning(rk) is not { } now) { skipNoWinning++; continue; }
                resolved.Add((a, rk, then, now));
            }
            aliasesResolved += resolved.Count;

            for (int i = 0; i < resolved.Count; i++)
                for (int j = i + 1; j < resolved.Count; j++)
                {
                    var (a1, r1, t1, n1) = resolved[i];
                    var (a2, r2, t2, n2) = resolved[j];
                    if (r1 == r2) continue;
                    // Counted here, where the pair is actually compared: the emit's Coverage must say how many
                    // pairs the split test JUDGED, not how many came out split. Zero findings over a judged
                    // population and zero findings because nothing was judged are different answers.
                    pairsJudged++;
                    bool togetherThen = t1.Cell == t2.Cell;
                    float was = togetherThen ? Dist(t1.Pos, t2.Pos) : float.PositiveInfinity;
                    bool apartNow = n1.Cell != n2.Cell;
                    float isNow = apartNow ? float.PositiveInfinity : Dist(n1.Pos, n2.Pos);
                    // The split decision is DetectorModel.CameApart — proven by construction in proofprobe:
                    // together = same cell within nearU; split = different cells now, or drawn apart past
                    // max(nearU, 3× the authored distance). A pair that moved TOGETHER is fine.
                    if (!DetectorModel.CameApart(togetherThen, was, !apartNow, isNow, nearU)) continue;

                    // THE REACH FILTER (2026-08-31). CameApart is a statement about two AUTHORED
                    // placements; the field test showed that only predicts runtime distance when each
                    // actor's own packages keep it at its placement. Ask that first, and where the answer
                    // is yes, ask the question the placements can then answer: are the two reachable
                    // areas disjoint? Anything else is not a weaker finding, it is not a finding.
                    if (reachFilter)
                    {
                        var reach1 = ReachOf(r1, n1);
                        var reach2 = ReachOf(r2, n2);
                        bool boundByPriority = reach1.Verdict == ReachVerdict.PlacementBound
                                            && reach2.Verdict == ReachVerdict.PlacementBound;
                        var cov1 = ReachOf(r1, n1, byCoverage: true);
                        var cov2 = ReachOf(r2, n2, byCoverage: true);
                        bool boundByCoverage = cov1.Verdict == ReachVerdict.PlacementBound
                                            && cov2.Verdict == ReachVerdict.PlacementBound;
                        if (boundByPriority != boundByCoverage) reachReadingDisagrees++;
                        if (reach1.Verdict != ReachVerdict.PlacementBound) { Bump(reachDropped, reach1.Verdict.ToString()); continue; }
                        if (reach2.Verdict != ReachVerdict.PlacementBound) { Bump(reachDropped, reach2.Verdict.ToString()); continue; }
                        if (!DetectorModel.BubblesDisjoint(isNow, reach1.Radius, reach2.Radius)) { reachOverlap++; continue; }
                    }

                    bool objTarget = q.Objectives.Any(o => o.TargetAliasIds.Contains(a1.Id) || o.TargetAliasIds.Contains(a2.Id));
                    bool stated = statedPairs.TryGetValue(q.Key, out var sp) && sp.Contains(Pair(a1.Id, a2.Id));
                    int moverPlugin = n1.Plugin != t1.Plugin ? n1.Plugin : n2.Plugin;
                    bool patched = PatchExists(q.Plugin, moverPlugin);
                    string movers = string.Join("+", new[] { n1, n2 }
                        .Where(s => s.Plugin != t1.Plugin && s.Plugin != t2.Plugin)
                        .Select(s => PluginName(s.Plugin)).Distinct());
                    var moverList = new[] { n1, n2 }
                        .Where(s => s.Plugin != t1.Plugin && s.Plugin != t2.Plugin)
                        .Select(s => PluginName(s.Plugin)).Distinct().ToArray();
                    var moverIdxList = new[] { n1, n2 }
                        .Where(s => s.Plugin != t1.Plugin && s.Plugin != t2.Plugin)
                        .Select(s => s.Plugin).Distinct().ToArray();
                    // The cells intent classification judges the mover's work against: where the pair was
                    // authored (FIRST — its interior flag is the scope-mismatch input) plus wherever the
                    // pair stands now.
                    var affectedCells = new[] { t1.Cell, n1.Cell, n2.Cell }.Distinct().ToArray();
                    split.Add(new Split(q.Label, PluginName(q.Plugin), a1.Name, a2.Name,
                                        NameOf(r1), NameOf(r2), CellOf(t1.Cell), CellOf(n1.Cell), CellOf(n2.Cell),
                                        was, isNow, apartNow, movers, objTarget, stated, patched,
                                        q.Objectives.Where(o => o.TargetAliasIds.Contains(a1.Id) || o.TargetAliasIds.Contains(a2.Id))
                                                    .Select(o => o.Text).FirstOrDefault(t => !string.IsNullOrWhiteSpace(t)),
                                        new[] { (Key: r1, Moved: n1.Plugin != t1.Plugin), (Key: r2, Moved: n2.Plugin != t2.Plugin) }
                                            .Where(x => x.Moved).Select(x => x.Key).ToArray(),
                                        new[] { r1, r2 }, q.Key, moverList, moverIdxList, affectedCells));
                }
        }
        return (split, new SplitCoverage(aliasesResolved, pairsJudged, skipNonPositional, skipMultiPlacement, skipNoAuthoringView,
                                         skipNoWinning, reachDropped, reachOverlap, reachReadingDisagrees));
    }

    static float Dist(Vec3 a, Vec3 b)
    {
        float dx = a.X - b.X, dy = a.Y - b.Y, dz = a.Z - b.Z;
        return MathF.Sqrt(dx * dx + dy * dy + dz * dz);
    }
}

/// <summary>What one split-test call judged and could not: the counts the console prints and the emit's two Coverage lines.</summary>
sealed record SplitCoverage(long AliasesResolved, long PairsJudged, long SkipNonPositional, long SkipMultiPlacement,
                            long SkipNoAuthoringView, long SkipNoWinning, IReadOnlyDictionary<string, int> ReachDropped,
                            long ReachOverlap, long ReachReadingDisagrees)
{
    /// <summary>The emit's "quest aliases" and "alias pairs" lines for this call, whose splits are <paramref name="split"/>.</summary>
    public CoverageLine[] Lines(bool reachFilter, List<Split> split) => new CoverageLine[]
    {
        new("quest aliases (positional resolution)", AliasesResolved,
            new Dictionary<string, long>
            {
                ["NonPositionalArm"] = SkipNonPositional,
                ["MultiPlacementUniqueActor"] = SkipMultiPlacement,
                ["NoAuthoringView"] = SkipNoAuthoringView,
                ["NoWinningPlacement"] = SkipNoWinning,
            }),
        new("alias pairs (split test)", PairsJudged,
            null,
            reachFilter
                ? new Dictionary<string, long>
                {
                    ["ReachFilterDropped"] = ReachDropped.Values.Sum(),
                    ["ReachAreasStillOverlap"] = ReachOverlap,
                    ["GoverningReadingsDisagree"] = ReachReadingDisagrees,
                }
                : null,
            reachFilter ? $"reach filter on; {split.Count:N0} pair(s) split, {split.Count(s => s.Stated):N0} of them stated"
                        : $"--no-reach: raw placement distance; {split.Count:N0} pair(s) split, {split.Count(s => s.Stated):N0} of them stated"),
    };
}
