using HarnessShared;
using HousecarlCore;
using SkyWright.Engine;
using Mutagen.Bethesda.Plugins;

// CLOBBERED, the decisions between the walk and the emit: which navmesh records are clobbered (DetectorModel.FindClobber
// fed the providers, the master closure and each provider's geometry triple), which population each falls in, which are
// findings, and the class's physical predicate. Moved out of Program.cs's top-level statements unchanged so the self-test
// can hold them to known answers (task 2026-10-14-probe-toplevel-decisions-testable).
static class ClobberFindings
{
    public const float ReachClearance = 120f;   // the oracle's measured resolution: closer reads reachable
    public const float ReachSpacing = 64f;      // sampling bound — a loss beyond clearance+spacing cannot be missed

    /// <summary>Every navmesh record whose winner declares no dependency on a re-cutting editor, in provider order.</summary>
    public static List<(FormKey Nav, int Loser, int Winner, int LoserT, int WinnerT, bool Material)> Find(
        IReadOnlyDictionary<FormKey, List<int>> providers, IReadOnlyList<HashSet<int>> masterClosure,
        IReadOnlyDictionary<(FormKey, int), (int V, int T, int H)> size, Func<int, bool> wanted)
    {
        var clobber = new List<(FormKey Nav, int Loser, int Winner, int LoserT, int WinnerT, bool Material)>();
        foreach (var (nav, provs) in providers)
        {
            var hit = DetectorModel.FindClobber(
                provs,
                (w, l) => masterClosure[w].Contains(l),
                pi => size.GetValueOrDefault((nav, pi)),
                (l, w) => wanted(l) || wanted(w));
            if (hit is not { } h) continue;
            clobber.Add((nav, h.Loser, h.Winner,
                         size.GetValueOrDefault((nav, h.Loser)).T,
                         size.GetValueOrDefault((nav, h.Winner)).T, h.Material));
        }
        return clobber;
    }

    /// <summary>Rule 12 on CLOBBERED: the population of a clobber by its two plugins' file names.</summary>
    /// <remarks>Bethesda's own shipped content is <see cref="BaseGamePlugins.Names"/>. Creation Club plugins are deliberately NOT
    /// in it: their changes, including navmesh deletions, are reported like any mod's (decided 2026-09-28).</remarks>
    public static ClobberPopulation Population(string loser, string winner, IReadOnlySet<string> vanilla)
        => DetectorModel.ClassifyClobber(
            vanilla.Contains(loser),
            vanilla.Contains(winner),
            BaseGamePlugins.IsUssep(loser));

    public static string ClassLabel(ClobberPopulation p) => p switch
    {
        ClobberPopulation.BethesdaFloor => "A. Bethesda-on-Bethesda (permanent FLOOR)",
        ClobberPopulation.UssepDiscarded => "B. USSEP's work discarded by a mod",
        _ => "C. mod-on-mod",
    };

    /// <summary>A finding: the geometry differs and the population is not the Bethesda floor (class A).</summary>
    public static bool IsFinding(bool material, ClobberPopulation p) => material && p != ClobberPopulation.BethesdaFloor;

    static (List<System.Numerics.Vector3> V, List<(int A, int B, int C, int Flags)> T) Mesh(NavmeshGeometry g)
        => (g.Vertices.Select(v => new System.Numerics.Vector3(v.X, v.Y, v.Z)).ToList(),
            g.Triangles.Select(t => ((int)t.A, (int)t.B, (int)t.C, t.Flags)).ToList());

    // THE PHYSICAL PREDICATE of a CLOBBERED row (2026-09-03; handoff 2026-09-12): victim-only ground remains — some point of
    // the loser's mesh lies farther than the reach clearance from the winner's, whoever the winner is under the arm being
    // read. This is the class's physical half on its own: "the winner declares no dependency on the loser" is the provenance
    // half, and a sham patch that masters both and forwards the winner's navmesh unchanged flips only that. Not used: the
    // content-hash Material test (symmetric — a true merge also differs from the loser), the neighbour-navmesh rescue and the
    // witness join (the reach pass's refinements). A row whose loser is within clearance everywhere reads Gone in its own
    // base arm: a declared-dependency clobber with no loss this instrument can see. The credit rule counts that apart.
    /// <param name="winnerLabel">The winner's file name as the detail prints it.</param>
    public static (PhysicalState State, string Detail) Predicate(FormKey nav, int loser, int winner,
        IReadOnlyDictionary<(FormKey Nav, int Plugin), NavmeshGeometry> geom, string winnerLabel)
    {
        if (loser == winner) return (PhysicalState.Gone, "the loser's own version is the winning one");
        if (!geom.TryGetValue((nav, loser), out var lg) || !geom.TryGetValue((nav, winner), out var wg))
            return (PhysicalState.Unmeasured, "a version's geometry could not be read");
        var L = Mesh(lg); var W = Mesh(wg);
        var victim = DetectorModel.CoverageLoss(L.V, L.T, W.V, W.T, ReachClearance, ReachSpacing);
        return victim.Count > 0
            ? (PhysicalState.Holds, $"victim-only ground: {victim.Count} sample(s) of the loser's mesh farther than {ReachClearance:0}u from the winner's ({winnerLabel})")
            : (PhysicalState.Gone, $"no point of the loser's mesh is farther than {ReachClearance:0}u from the winner's ({winnerLabel})");
    }

    // FNV-1a over every vertex component, triangle index and flag, in record order. Not cryptographic
    // and does not need to be: it only has to make "byte-identical forward" and "re-cut" distinguishable.
    public static int GeomHash(NavmeshGeometry nav)
    {
        unchecked
        {
            int h = (int)2166136261;
            void Mix(int v) => h = (h ^ v) * 16777619;
            foreach (var v in nav.Vertices)
            {
                Mix(BitConverter.SingleToInt32Bits(v.X));
                Mix(BitConverter.SingleToInt32Bits(v.Y));
                Mix(BitConverter.SingleToInt32Bits(v.Z));
            }
            foreach (var t in nav.Triangles) { Mix(t.A); Mix(t.B); Mix(t.C); Mix(t.Flags); }
            return h;
        }
    }
}
