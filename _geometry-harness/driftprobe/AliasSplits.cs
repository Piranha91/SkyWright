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