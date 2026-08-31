using System.Numerics;

namespace HarnessShared;

// THE DETECTOR CORES, extracted so they can be PROVEN — the same move FomodModel made for the
// installer rules, applied to the decisions that were still living inline in probe Program.cs
// files with zero by-construction cases (charter item 2's actual gap, closed 2026-08-27).
//
// Why this matters was demonstrated twice in one review:
//   - the clobber rule silently checked DIRECT masters only, and 3 real pairs slipped through the
//     hole (winner mastering an intermediate that masters the loser);
//   - the FOMOD trust threshold drifted from "all" to "half" and no case could tell.
// A rule with no case that catches its inversion is not guarded. Each function here is small,
// pure, and owned by proofprobe cases whose answers are known by construction.
//
// The probes keep the I/O: reading plugins, building provider lists, resolving names. These
// functions own the DECISIONS.

public static class DetectorModel
{
    /// <summary>Transitive closure over declared masters. <paramref name="direct"/>[p] holds the
    /// indices of p's DECLARED masters, each strictly less than p (masters load first — the caller
    /// guarantees it, and MO2's sort enforces it). One ascending pass closes the relation.</summary>
    public static HashSet<int>[] MasterClosure(IReadOnlyList<IReadOnlySet<int>> direct)
    {
        var closure = new HashSet<int>[direct.Count];
        for (int p = 0; p < direct.Count; p++)
        {
            var c = new HashSet<int>(direct[p]);
            foreach (var m in direct[p]) if (m < p && closure[m] is { } mc) c.UnionWith(mc);
            closure[p] = c;
        }
        return closure;
    }

    /// <summary>One navmesh's clobber decision. <paramref name="providers"/> is every plugin that
    /// supplies the record, in load order — index 0 the definer, last the winner. Returns the
    /// LATEST provider the winner declares no dependency on (the loser), or null when there is no
    /// clobber: fewer than three providers, or every intermediate editor is covered by the
    /// winner's dependency closure.
    /// <para>Material = the geometry actually differs, decided by the full (verts, tris, content
    /// hash) triple — an identical triple is a byte-identical forward and reverts nothing. The
    /// claim encoded here is "no declared dependency", never "cannot have seen": declaring is not
    /// seeing, and this function does not pretend otherwise.</para></summary>
    public static (int Loser, int Winner, bool Material)? FindClobber(
        IReadOnlyList<int> providers,
        Func<int, int, bool> declaresDependency,          // (winner, editor) -> editor is in winner's closure
        Func<int, (int V, int T, int H)> sizeBy,          // geometry as supplied BY that provider
        Func<int, int, bool>? wanted = null)              // optional (loser, winner) report filter
    {
        if (providers.Count < 3) return null;             // needs a definer plus two competing editors
        int winner = providers[^1];
        for (int i = providers.Count - 2; i >= 1; i--)    // index 0 is the original definer
        {
            int loser = providers[i];
            if (declaresDependency(winner, loser)) continue;
            if (wanted is not null && !wanted(loser, winner)) continue;
            return (loser, winner, sizeBy(loser) != sizeBy(winner));
        }
        return null;
    }

    /// <summary>Rule 12 applied to CLOBBERED itself: which population does a clobber belong to?
    /// A is Bethesda re-cutting Bethesda — permanent, shipped that way, measured floor, never a
    /// finding. B and C are the findings; USSEP-as-loser is kept apart from mod-on-mod because it
    /// is a different conversation (nearly nothing masters USSEP on purpose).</summary>
    public static ClobberPopulation ClassifyClobber(bool loserIsVanilla, bool winnerIsVanilla, bool loserIsUssep)
        => loserIsVanilla && winnerIsVanilla ? ClobberPopulation.BethesdaFloor
         : loserIsUssep ? ClobberPopulation.UssepDiscarded
         : ClobberPopulation.ModOnMod;

    /// <summary>The dangling-link population split — the two-END classification that separated
    /// Bethesda's permanent floor (28 of 112) from cross-plugin breakage. A is floor; B, C, D are
    /// findings.</summary>
    public static DanglePopulation ClassifyDangle(bool indexEndModTouched, bool targetEndModTouched)
        => (indexEndModTouched, targetEndModTouched) switch
        {
            (false, false) => DanglePopulation.BethesdaFloor,
            (false, true) => DanglePopulation.ModBrokeTarget,
            (true, false) => DanglePopulation.ModWroteIndex,
            (true, true) => DanglePopulation.ModsBothEnds,
        };

    /// <summary>CHECK 2's split decision: were these two aliases together when the quest was
    /// authored, and are they apart now? Together = same cell AND within <paramref name="near"/>.
    /// Apart = different cells now, or drawn apart past max(near, 3× the authored distance) — a
    /// pair that moved TOGETHER (a whole scene relocated) is not split.</summary>
    public static bool CameApart(
        bool sameCellThen, float wasApart,
        bool sameCellNow, float nowApart, float near)
    {
        if (!sameCellThen || wasApart > near) return false;    // never were together: not judgeable as a pair
        if (!sameCellNow) return true;
        return nowApart > MathF.Max(near, wasApart * 3f);
    }

    /// <summary>Milestone 5's broad-phase decision: do two world-space AABBs overlap, and how
    /// hard? Null when they are disjoint or merely touching (exact contact is how adjacent
    /// architecture is authored — the same reason the SAT treats touch as separation).
    /// MinDepth is the smallest per-axis overlap extent — the separation one box would need to
    /// move to stop overlapping. VolumeFraction is overlap volume over the SMALLER box's volume:
    /// 1 means the smaller box is entirely inside the bigger one.</summary>
    public static (float MinDepth, float VolumeFraction)? BoxOverlap(
        Vector3 aMin, Vector3 aMax, Vector3 bMin, Vector3 bMax)
    {
        float ox = MathF.Min(aMax.X, bMax.X) - MathF.Max(aMin.X, bMin.X);
        float oy = MathF.Min(aMax.Y, bMax.Y) - MathF.Max(aMin.Y, bMin.Y);
        float oz = MathF.Min(aMax.Z, bMax.Z) - MathF.Max(aMin.Z, bMin.Z);
        if (ox <= 0 || oy <= 0 || oz <= 0) return null;
        float volA = MathF.Max(0, (aMax.X - aMin.X) * (aMax.Y - aMin.Y) * (aMax.Z - aMin.Z));
        float volB = MathF.Max(0, (bMax.X - bMin.X) * (bMax.Y - bMin.Y) * (bMax.Z - bMin.Z));
        float smaller = MathF.Max(1e-6f, MathF.Min(volA, volB));
        return (MathF.Min(ox, MathF.Min(oy, oz)), MathF.Min(1f, ox * oy * oz / smaller));
    }

    /// <summary>One package's positional facts, as CHECK 2's reach filter needs them — every
    /// location slot the package carries, aggregated, each already resolved against the winning
    /// load order by the caller (the anchor's position is a load-order question, not a rule).</summary>
    /// <param name="Positional">the package carries a location slot at all.</param>
    /// <param name="AllSlotsAtPlacement">every location slot anchors to the actor's own placement
    /// — its editor location, itself, or where the package started.</param>
    /// <param name="AnyUnresolved">a slot anchors to something this order cannot place: the
    /// player, another actor, a quest alias, a run-time "nearest object of type X" search, or a
    /// reference in a different cell.</param>
    /// <param name="WorstAnchorDistance">the distance to the furthest slot anchor that sits
    /// OUTSIDE its own radius — the package is then telling the actor to stand somewhere its
    /// placement is not. Null when no slot does.</param>
    /// <param name="Reach">how far from the placement this package can put the actor: the slot
    /// radius when the anchor is the placement, anchor distance plus radius when it is not.</param>
    public readonly record struct PackageAnchor(
        bool Positional, bool AllSlotsAtPlacement, bool AnyUnresolved, float? WorstAnchorDistance,
        uint Reach, bool Scheduled, int ScheduleMinutes)
    {
        /// <summary>Minutes per day this package is SCHEDULED to run. An unscheduled package
        /// scores zero — not because it never runs, but because it has no window of its own: it is
        /// the fallback that fills whatever the scheduled ones leave, which is a quantity this
        /// struct cannot know without evaluating the whole list's conditions.</summary>
        public int Coverage => Scheduled ? Math.Clamp(ScheduleMinutes, 0, 1440) : 0;
    }

    /// <summary>Whether an actor's PLACEMENT is a usable predictor of where it will be found, and
    /// within what radius — the filter the 2026-08-30 field test sent back.
    ///
    /// <para>That test proved CHECK 2's class real and its metric unreliable in one run: authored
    /// placement distance tracked runtime distance almost exactly for two priests (137u authored →
    /// 116-134u measured) and meant nothing for a third, who stood 1,632u from her placement in
    /// BOTH arms of the A/B. The records say why, and say it in units. Skyrim runs an actor's
    /// packages in list order, first satisfied wins, and a package positions the actor at its
    /// location slot's anchor plus a radius. Freir's highest-priority package anchors her to a
    /// greeting marker <b>1,617u from her placement</b> — 15u from what the game was measured
    /// doing. Rorlund's and Silana's anchor to their editor location, which IS the placement.</para>
    ///
    /// <para>The rule: take the actor's <b>highest-priority package that positions it</b>, and
    /// judge every location slot it carries. If they all anchor to the placement, the placement
    /// predicts position within their radius. If one anchors to a reference no further away than
    /// its own radius, same answer with a wider reach. If one stands the actor OUTSIDE its own
    /// radius, or anchors to something this order cannot place, the placement predicts nothing and
    /// every pair involving this actor is unassessable — counted with its measured distance, never
    /// quietly cleared.</para>
    ///
    /// <para>What this deliberately does NOT do: evaluate package conditions, model which package
    /// is running at a given hour, or read target slots (what the package acts on) as opposed to
    /// location slots (where it sends the actor). A conditional top-priority package is taken at
    /// face value. Hence the verdict is "the placement is a usable predictor", never "the actor is
    /// at its placement" — and <paramref name="byCoverage"/> re-asks the question with "governing"
    /// read as the package covering the most of the day instead, because which reading you take is
    /// a judgement the records do not settle and the count should not hide.</para></summary>
    public static (ReachVerdict Verdict, uint Radius, string Why) ClassifyReach(
        IReadOnlyList<PackageAnchor?> packages, bool templated, bool byCoverage = false)
    {
        if (templated) return (ReachVerdict.Templated, 0, "packages inherited from a template");
        if (packages.Count == 0) return (ReachVerdict.NoPackages, 0, "no packages");
        if (packages.Any(x => x is null))
            return (ReachVerdict.PackageMissing, 0, "a package record is not in the load order");

        int governing = -1;
        for (int i = 0; i < packages.Count; i++)
        {
            if (!packages[i]!.Value.Positional) continue;
            if (governing < 0) { governing = i; if (!byCoverage) break; continue; }
            // ties keep the earlier package: equal claims are settled by priority, as the engine does
            if (packages[i]!.Value.Coverage > packages[governing]!.Value.Coverage) governing = i;
        }
        if (governing < 0) return (ReachVerdict.NoAnchor, 0, "no package positions this actor");

        var g = packages[governing]!.Value;
        if (g.WorstAnchorDistance is { } away)
            return (ReachVerdict.AnchorAwayFromPlacement, 0,
                    $"package {governing} stands this actor {away:N0}u from its placement");
        if (g.AllSlotsAtPlacement)
            return (ReachVerdict.PlacementBound, g.Reach, $"package {governing} anchors to the placement, reach {g.Reach}u");
        if (g.AnyUnresolved)
            return (ReachVerdict.UnresolvedAnchor, 0, $"package {governing} anchors to something this order cannot place");
        return (ReachVerdict.PlacementBound, g.Reach, $"package {governing} anchors within reach {g.Reach}u of the placement");
    }

    /// <summary>The pair decision the reach filter replaces raw distance with. Two actors who each
    /// stay within a radius of their placement can only meet if those bubbles overlap, so the
    /// predictive quantity is the gap between the bubbles, not between the placements. Reproduces
    /// the field measurement both ways: 137u apart with 512+512 of reach — they meet, and the
    /// scene fires; 1,800u apart with the same reach — they cannot, and it does not.</summary>
    public static bool BubblesDisjoint(float distance, uint reachA, uint reachB)
        => distance > (float)reachA + reachB;

    /// <summary>The record-level verify verdict. CONFIRMED means every necessary record is
    /// overridden — which is COVERAGE, not repair: content and load-order position are not judged
    /// here, and the differential run exists precisely because a CONFIRMED patch can net to zero.</summary>
    public static string Verdict(bool readable, int necessaryHit, int necessaryCount, int alternativeHit)
        => !readable ? "UNREADABLE"
         : necessaryHit == necessaryCount ? "CONFIRMED"
         : necessaryHit > 0 ? "PARTIAL"
         : alternativeHit > 0 ? "ALTERNATIVE"
         : "UNRELATED";
}

public enum ClobberPopulation { BethesdaFloor, UssepDiscarded, ModOnMod }

/// <summary>Whether an actor's PLACEMENT predicts where it will be found. Everything that is not
/// PlacementBound is unassessable and counted by reason, never quietly cleared (rule 6).</summary>
public enum ReachVerdict { PlacementBound, AnchorAwayFromPlacement, UnresolvedAnchor, NoAnchor, Templated, NoPackages, PackageMissing }
public enum DanglePopulation { BethesdaFloor, ModBrokeTarget, ModWroteIndex, ModsBothEnds }
