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

        // The split test at 600 u (AliasSplits.Find), reach filter on. PatchExists is a stub (no patch): the fixture has none,
        // and Patched decides nothing these cases read.
        var closure = DetectorModel.MasterClosure(x.MasterIdx);
        AliasSplits Splitter() => new(x.Quests, x.NpcPlaced, stated, true, reach, a => AliasStated.AliasRef(a, x.NpcPlaced),
                                      (k, p) => x.History.AsSeenBy(k, p, closure), x.History.Winning, (_, _) => false,
                                      p => x.PluginKey[p].FileName.String, k => k.ToString(), c => c.ToString());
        var (found, sp) = Splitter().Find(600f);
        string Pairs(IEnumerable<Split> s) => string.Join(" ", s.Select(v => $"{v.Alias1}+{v.Alias2}{(v.Stated ? "(stated)" : "")}[{string.Join(",", v.MovedRefs.Select(r => r.ID.ToString("X")))}]"));
        Split? Of(string a1, string a2) => found.FirstOrDefault(v => v.Alias1 == a1 && v.Alias2 == a2);
        t.Check("PD3-D-A pair A (1+2): R1 left for another cell; split, stated, the moved record R1 only",
                Of("Alias1", "Alias2") is { Stated: true, DifferentCell: true } a && a.MovedRefs.SequenceEqual(new[] { F.R1 }), Pairs(found));
        t.Check("PD3-D-B pair B (3+4): R3 has no packages; dropped by the reach filter (NoPackages 1)",
                Of("Alias3", "Alias4") is null && sp.ReachDropped.GetValueOrDefault("NoPackages") == 1 && sp.ReachDropped.Count == 1,
                $"{Pairs(found)}; dropped {string.Join(", ", sp.ReachDropped.Select(kv => $"{kv.Key} {kv.Value}"))}");
        t.Check("PD3-D-C pair C (5+6): 900 u apart, reach 600 + 600 still overlaps; not split, overlap 1",
                Of("Alias5", "Alias6") is null && sp.ReachOverlap == 1, $"{Pairs(found)}; overlap {sp.ReachOverlap}");
        t.Check("PD3-D-D pair D (7+8): 900 u apart, reach 100 + 100 is disjoint; split, stated, the moved record R7 only",
                Of("Alias7", "Alias8") is { Stated: true, DifferentCell: false } d && d.MovedRefs.SequenceEqual(new[] { F.R7 }), Pairs(found));
        t.Check("PD3-D-E pair E (1+10): split but not stated", Of("Alias1", "Alias10") is { Stated: false }, Pairs(found));
        t.Check("PD3-D-N exactly three pairs split (A, D, E)", found.Count == 3, Pairs(found));
        t.Check("PD3-D-V skips: NoAuthoringView 1 (alias 9), NonPositional 1 (alias 11), MultiPlacement 1 (alias 12), NoWinning 0; 9 aliases resolved, 36 pairs judged",
                sp.SkipNoAuthoringView == 1 && sp.SkipNonPositional == 1 && sp.SkipMultiPlacement == 1 && sp.SkipNoWinning == 0
                && sp.AliasesResolved == 9 && sp.PairsJudged == 36,
                $"noView {sp.SkipNoAuthoringView}, nonPos {sp.SkipNonPositional}, multi {sp.SkipMultiPlacement}, noWin {sp.SkipNoWinning}, resolved {sp.AliasesResolved}, judged {sp.PairsJudged}");

        // DC: a run asks the split test at 600 u, then the sensitivity sweep asks it again at 300, 600 and 1200 u, and the emit
        // is written after the sweep. The coverage it writes must be the 600 u call's alone, not a sum over the four calls.
        var run = Splitter();
        var (mainSplit, main) = run.Find(600f);
        var sweep = new[] { 300f, 600f, 1200f }.Select(v => run.Find(v).Coverage).ToArray();
        t.Check("DC-221 after the sweep, the coverage the emit reads is the 600 u call's: 9 aliases resolved, 36 pairs judged, skips 1/1/1/0, NoPackages 1, overlap 1",
                main.AliasesResolved == 9 && main.PairsJudged == 36 && main.SkipNoAuthoringView == 1 && main.SkipNonPositional == 1
                && main.SkipMultiPlacement == 1 && main.SkipNoWinning == 0 && main.ReachDropped.Values.Sum() == 1 && main.ReachOverlap == 1,
                $"resolved {main.AliasesResolved}, judged {main.PairsJudged}, noView {main.SkipNoAuthoringView}, nonPos {main.SkipNonPositional}, "
              + $"multi {main.SkipMultiPlacement}, noWin {main.SkipNoWinning}, dropped {main.ReachDropped.Values.Sum()}, overlap {main.ReachOverlap}");

        // The two Coverage lines the emit writes for that call (SplitCoverage.Lines, which Program.cs's emit calls).
        var lines = main.Lines(true, mainSplit);
        string Line(CoverageLine c) => $"{c.Population}: {c.Judged}"
            + (c.Unjudgeable is { } u ? " unjudgeable " + string.Join(",", u.Select(kv => $"{kv.Key} {kv.Value}")) : "")
            + (c.SetAside is { } a ? " set aside " + string.Join(",", a.Select(kv => $"{kv.Key} {kv.Value}")) : "") + $" ({c.Note})";
        t.Check("DC-222 the emit's lines: quest aliases judged 9 (NonPositional 1, MultiPlacement 1, NoAuthoringView 1, NoWinning 0); "
              + "alias pairs judged 36 (ReachFilterDropped 1, overlap 1, readings disagree 0; 3 split, 2 stated)",
                lines.Length == 2 && lines[0].Population == "quest aliases (positional resolution)" && lines[0].Judged == 9
                && lines[0].Unjudgeable!["NonPositionalArm"] == 1 && lines[0].Unjudgeable!["MultiPlacementUniqueActor"] == 1
                && lines[0].Unjudgeable!["NoAuthoringView"] == 1 && lines[0].Unjudgeable!["NoWinningPlacement"] == 0
                && lines[1].Population == "alias pairs (split test)" && lines[1].Judged == 36 && lines[1].Unjudgeable is null
                && lines[1].SetAside!["ReachFilterDropped"] == 1 && lines[1].SetAside!["ReachAreasStillOverlap"] == 1
                && lines[1].SetAside!["GoverningReadingsDisagree"] == 0 && lines[1].Note == "reach filter on; 3 pair(s) split, 2 of them stated",
                string.Join(" | ", lines.Select(Line)));

        // Each sweep call counts its own population: the aliases and pairs do not depend on the threshold, and the four calls'
        // own counts add up to what the emit printed before this fix (36 aliases, 144 pairs, 4 per skip, 5 dropped, 3 overlap).
        var all = sweep.Prepend(main).ToArray();
        t.Check("DC-223 every sweep call judges 9 aliases and 36 pairs; the four calls sum to the old emit's 36 / 144 / 4 / 5 / 3",
                sweep.All(c => c.AliasesResolved == 9 && c.PairsJudged == 36 && c.SkipNonPositional == 1)
                && all.Sum(c => c.AliasesResolved) == 36 && all.Sum(c => c.PairsJudged) == 144 && all.Sum(c => c.SkipNoAuthoringView) == 4
                && all.Sum(c => c.ReachDropped.Values.Sum()) == 5 && all.Sum(c => c.ReachOverlap) == 3,
                string.Join(" ; ", new[] { 600f, 300f, 600f, 1200f }.Zip(all, (v, c) =>
                    $"{v:0}u: {c.AliasesResolved}/{c.PairsJudged}, dropped {c.ReachDropped.Values.Sum()}, overlap {c.ReachOverlap}")));

        // What --emit writes (AliasSplits.Emitted): the stated splits A and D, each with its moved record; E is proximity only.
        var emitted = AliasSplits.Emitted(found).ToList();
        string Emits() => string.Join(" ", emitted.Select(e => $"{e.s.Alias1}+{e.s.Alias2}[{string.Join(",", e.necessary.Select(n => n.FormKey))}]"));
        t.Check("PD3-D-M --emit writes exactly A (necessary R1) and D (necessary R7); E, not stated, is not written",
                emitted.Count == 2 && emitted[0].s.Alias1 == "Alias1" && emitted[0].s.Alias2 == "Alias2" && emitted[0].necessary.Single().FormKey == F.R1.ToString()
                && emitted[1].s.Alias1 == "Alias7" && emitted[1].necessary.Single().FormKey == F.R7.ToString(), Emits());

        // The fixed order (DfFix puts R1 and R7 back): nothing splits.
        var xf = new Walked(w.Order.Append(w.Fix).ToList());
        var closureF = DetectorModel.MasterClosure(xf.MasterIdx);
        var reachF = new ActorReach(xf.PackFacts, xf.History.Winning, xf.RefBase, xf.NpcPackages, xf.NpcTemplated);
        var fixedSplits = new AliasSplits(xf.Quests, xf.NpcPlaced, AliasStated.Of(xf.Scenes, xf.Quests, xf.PackageTargets), true, reachF,
                                          a => AliasStated.AliasRef(a, xf.NpcPlaced), (k, p) => xf.History.AsSeenBy(k, p, closureF), xf.History.Winning,
                                          (_, _) => false, p => xf.PluginKey[p].FileName.String, k => k.ToString(), c => c.ToString()).Find(600f).Split;
        t.Check("PD3-D-F with DfFix loaded, no pair is split", fixedSplits.Count == 0, Pairs(fixedSplits));
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
