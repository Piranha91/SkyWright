using HarnessShared;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Skyrim;
using F = HarnessShared.DriftFixture;

// driftprobe --self-test: _shared/DriftFixture.cs written (PD_FIXTURE_KEEP keeps it for a probe run), then the probe's
// own CHECK 2 decisions over it, each against an answer worked out by hand from the fixture's table. Exit 0 when every
// case passes. (--moved-ref-selftest is CHECK 1's, DriftSelfTest.cs.)
static class DriftQuestSelfTest
{
    public static int Run() => ProbeSelfTest.Run("driftprobe", "DriftFixture through the alias-split decisions", t =>
    {
        var w = F.Write(t.Root);
        t.Check("PD3-D-F0 the fixture writes DfMaster.esm, DfQuest.esp, DfMover.esp, DfInject.esp and the fix DfFix.esp",
                w.Order.Count == 4 && w.Order.Append(w.Fix).All(File.Exists), string.Join(", ", w.Order.Append(w.Fix).Select(Path.GetFileName)));

        // Smoke (Gate A risk 1): a package's location slot reads back as the reach filter reads it (PackFacts).
        using (var m = SkyrimMod.CreateFromBinaryOverlay(w.Order[0], F.Rel))
        {
            string Slots(Mutagen.Bethesda.Plugins.FormKey k) =>
                m.Packages.FirstOrDefault(p => p.FormKey == k) is { } pk
                    ? string.Join(", ", PackFacts.From(pk).Locations.Select(l => $"{l.Arm} r={l.Radius}")) : "(no package)";
            t.Check("PD3-D-F1 DfStay600 reads back as one slot at its editor location, radius 600", Slots(F.Pk600) == "editor-location r=600", Slots(F.Pk600));
            t.Check("PD3-D-F2 DfStay100 reads back as one slot at its editor location, radius 100", Slots(F.Pk100) == "editor-location r=100", Slots(F.Pk100));
        }

        // Pass 1 over the order, as Program.cs runs it (DriftIndex), with the swap layer off.
        var x = new Walked(w.Order);
        t.Check("PD3-D-W0 pass 1: one quest, four scenes, two packages, three NPCs; 14 placements; masters as written",
                x.Quests.Count == 1 && x.Scenes.Count == 4 && x.PackFacts.Count == 2 && x.NpcPackages.Count == 3 && x.Placements == 14
                && x.MasterIdx[1].SetEquals(new[] { 0 }) && x.MasterIdx[3].SetEquals(new[] { 0, 1 }),
                $"quests {x.Quests.Count}, scenes {x.Scenes.Count}, packages {x.PackFacts.Count}, npcs {x.NpcPackages.Count}, placements {x.Placements}, "
              + $"masters of DfInject {string.Join("+", x.MasterIdx[3])}");
        t.Check("PD3-D-W1 the quest's aliases read as written: 10 forced, one UniqueActor, one with no fill",
                x.Quests[F.Q].Aliases.Count(a => a.Forced is not null) == 10 && x.Quests[F.Q].Aliases.Count(a => a.UniqueNpc is not null) == 1
                && x.Quests[F.Q].Aliases.Count(a => a.Arm == "(none / conditions only)") == 1,
                string.Join(", ", x.Quests[F.Q].Aliases.Select(a => $"{a.Id}:{a.Arm}")));

        // Where each reference can stand (ActorReach), at its winning placement.
        var reach = new ActorReach(x.PackFacts, x.History.Winning, x.RefBase, x.NpcPackages, x.NpcTemplated);
        string Reach(FormKey r) => x.History.Winning(r) is { } at ? $"{reach.ReachOf(r, at).Verdict} {reach.ReachOf(r, at).Radius}" : "(no placement)";
        t.Check("PD3-D-R1 a static stays at its placement: PlacementBound, radius 0", Reach(F.R1) == "PlacementBound 0", Reach(F.R1));
        t.Check("PD3-D-R2 an actor with no packages: NoPackages", Reach(F.R3) == "NoPackages 0", Reach(F.R3));
        t.Check("PD3-D-R3 actors kept near their editor location: PlacementBound at the package radius (600, 100)",
                Reach(F.R5) == "PlacementBound 600" && Reach(F.R7) == "PlacementBound 100", $"{Reach(F.R5)} / {Reach(F.R7)}");

        // The pairs the quest states (AliasStated): its four scenes' actor pairs, and nothing else.
        var stated = AliasStated.Of(x.Scenes, x.Quests, x.PackageTargets);
        string Stated() => string.Join(" ", stated.SelectMany(kv => kv.Value.OrderBy(v => v).Select(v => $"{v.Item1}+{v.Item2}")));
        t.Check("PD3-D-S0 the stated pairs are exactly 1+2, 3+4, 5+6 and 7+8 (the scenes); 1+10 is not stated",
                stated.Count == 1 && stated[F.Q].SetEquals(new[] { (1, 2), (3, 4), (5, 6), (7, 8) }), Stated());
    });

    /// <summary>Pass 1 of a real run over <paramref name="order"/>: the tables Program.cs declares, filled by DriftIndex.</summary>
    sealed class Walked
    {
        public readonly ModKey[] PluginKey; public readonly HashSet<int>[] MasterIdx;
        public readonly PlacementHistory History = new(); public readonly AliasTargets AliasTargets;
        public readonly Dictionary<FormKey, FormKey> RefBase = new(); public readonly Dictionary<FormKey, List<FormKey>> NpcPlaced = new();
        public readonly Dictionary<FormKey, (string Type, string? Edid)> BaseName = new(); public readonly Dictionary<FormKey, (string? Edid, bool Interior)> CellName = new();
        public readonly Dictionary<FormKey, QuestInfo> Quests = new(); public readonly Dictionary<FormKey, (FormKey Quest, int[] Actors)> Scenes = new();
        public readonly Dictionary<FormKey, int[]> PackageTargets = new(); public readonly Dictionary<FormKey, PackFacts> PackFacts = new();
        public readonly Dictionary<FormKey, FormKey[]> NpcPackages = new(); public readonly HashSet<FormKey> NpcTemplated = new();
        public long Placements, QuestRecords;
        public Walked(IReadOnlyList<string> order)
        {
            PluginKey = new ModKey[order.Count]; MasterIdx = new HashSet<int>[order.Count]; AliasTargets = new AliasTargets(History);
            var index = new DriftIndex(PluginKey, MasterIdx, new BosFeed(false), AliasTargets, RefBase, NpcPlaced, BaseName, CellName,
                                       new(), new(), Quests, Scenes, PackageTargets, PackFacts, NpcPackages, NpcTemplated,
                                       new(), new int[order.Count], new(), new());
            for (int p = 0; p < order.Count; p++)
                using (var mod = SkyrimMod.CreateFromBinaryOverlay(order[p], F.Rel))
                {
                    var (dp, dq) = index.Plugin(p, mod);
                    Placements += dp; QuestRecords += dq;
                }
        }
    }
}
