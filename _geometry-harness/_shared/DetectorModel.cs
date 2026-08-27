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
public enum DanglePopulation { BethesdaFloor, ModBrokeTarget, ModWroteIndex, ModsBothEnds }
