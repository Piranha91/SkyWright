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

    /// <summary>Navmesh triangle flag bits the coverage rule reads. Deleted triangles are not
    /// ground on either side. Water is ground the oracle can REACH (an actor swims) but never a
    /// place to stand a probe target on — so it counts as coverage and is never sampled.</summary>
    public const int NavTriDeleted = 0x08;
    public const int NavTriWater = 0x200;

    /// <summary>One candidate probe point from <see cref="CoverageLoss"/>: a point on the FROM
    /// mesh's standable surface whose nearest point of AGAINST-side coverage is
    /// <see cref="Clearance"/> away. Triangle and Flags name the FROM triangle it was sampled
    /// from. Clearance is PositiveInfinity when the AGAINST mesh has no coverage at all.</summary>
    public readonly record struct CoverageSample(Vector3 Point, int Triangle, int Flags, float Clearance);

    /// <summary>The 3.5.5 candidate selector — ground where the two arms of a navmesh clobber
    /// PREDICT different reachability answers (roadmap 3.5.5; the rule the E5 null showed was
    /// missing: 26 probe targets on ground where the arms were geometrically identical proved
    /// nothing, so candidates must be chosen where the winning geometry actually changes).
    ///
    /// <para>The rule: sample the FROM mesh's standable surface — every triangle that is neither
    /// Deleted nor Water, midpoint-subdivided until no edge exceeds <paramref name="spacing"/>,
    /// one sample per leaf centroid — and keep each sample whose minimum 3-D distance to the
    /// AGAINST mesh's coverage exceeds <paramref name="clearance"/>. Run twice with the meshes
    /// swapped for the two directions; FROM = the loser's version and AGAINST = the winner's is
    /// the direction that can strand something.</para>
    ///
    /// <para>Miss bound, stated rather than implied: every point of the FROM surface lies within
    /// <paramref name="spacing"/> of some sample, so a lost region whose true clearance exceeds
    /// clearance + spacing cannot be missed; one inside that band may be. Set clearance to the
    /// oracle's measured resolution (~120 u — a target closer than that to walkable ground reads
    /// as reachable regardless, the NEG-void result).</para>
    ///
    /// <para>What this deliberately does NOT judge: connectivity (a point covered in both arms
    /// but cut off in one is invisible here — coverage presence only), and coverage supplied by
    /// OTHER records — the caller must re-check survivors against every other winning mesh in
    /// the cell with <see cref="DistanceToCoverage"/>, because the arms differ in one record and
    /// the rest of the cell can rescue a point the winner dropped.</para></summary>
    public static List<CoverageSample> CoverageLoss(
        IReadOnlyList<Vector3> verts, IReadOnlyList<(int A, int B, int C, int Flags)> tris,
        IReadOnlyList<Vector3> againstVerts, IReadOnlyList<(int A, int B, int C, int Flags)> againstTris,
        float clearance, float spacing)
    {
        var found = new List<CoverageSample>();
        var samples = new List<Vector3>();
        for (int i = 0; i < tris.Count; i++)
        {
            var t = tris[i];
            if ((t.Flags & NavTriWater) != 0) continue;               // reachable, but not standable
            if (!CoverageTriangle(t, verts, out var a, out var b, out var c)) continue;
            samples.Clear();
            Subdivide(a, b, c, spacing, 0, samples);
            foreach (var s in samples)
            {
                float d = DistanceToCoverage(s, againstVerts, againstTris);
                if (d > clearance) found.Add(new CoverageSample(s, i, t.Flags, d));
            }
        }
        return found;
    }

    /// <summary>Minimum 3-D distance from a point to a mesh's coverage — its non-deleted,
    /// non-degenerate triangles (water included: it is reachable ground to the oracle).
    /// PositiveInfinity when the mesh has no coverage at all.</summary>
    public static float DistanceToCoverage(Vector3 p, IReadOnlyList<Vector3> verts,
        IReadOnlyList<(int A, int B, int C, int Flags)> tris)
    {
        float best = float.PositiveInfinity;
        foreach (var t in tris)
        {
            if (!CoverageTriangle(t, verts, out var a, out var b, out var c)) continue;
            var q = ClosestOnTriangle(p, a, b, c);
            float d = (p - q).Length();
            if (d < best) best = d;
        }
        return best;
    }

    // A triangle counts as coverage only when it is not Deleted, its indices resolve, and it has
    // actual area — a zero-area triangle is a line, not ground, and would divide by zero below.
    static bool CoverageTriangle((int A, int B, int C, int Flags) t, IReadOnlyList<Vector3> verts,
        out Vector3 a, out Vector3 b, out Vector3 c)
    {
        a = b = c = default;
        if ((t.Flags & NavTriDeleted) != 0) return false;
        if (t.A < 0 || t.B < 0 || t.C < 0 || t.A >= verts.Count || t.B >= verts.Count || t.C >= verts.Count)
            return false;
        a = verts[t.A]; b = verts[t.B]; c = verts[t.C];
        return Vector3.Cross(b - a, c - a).LengthSquared() > 1e-6f;
    }

    // Closest point on a triangle to p — the standard Voronoi-region walk (vertex, edge, face).
    static Vector3 ClosestOnTriangle(Vector3 p, Vector3 a, Vector3 b, Vector3 c)
    {
        Vector3 ab = b - a, ac = c - a, ap = p - a;
        float d1 = Vector3.Dot(ab, ap), d2 = Vector3.Dot(ac, ap);
        if (d1 <= 0 && d2 <= 0) return a;
        Vector3 bp = p - b;
        float d3 = Vector3.Dot(ab, bp), d4 = Vector3.Dot(ac, bp);
        if (d3 >= 0 && d4 <= d3) return b;
        float vc = d1 * d4 - d3 * d2;
        if (vc <= 0 && d1 >= 0 && d3 <= 0) return a + ab * (d1 / (d1 - d3));
        Vector3 cp = p - c;
        float d5 = Vector3.Dot(ab, cp), d6 = Vector3.Dot(ac, cp);
        if (d6 >= 0 && d5 <= d6) return c;
        float vb = d5 * d2 - d1 * d6;
        if (vb <= 0 && d2 >= 0 && d6 <= 0) return a + ac * (d2 / (d2 - d6));
        float va = d3 * d6 - d5 * d4;
        if (va <= 0 && d4 - d3 >= 0 && d5 - d6 >= 0)
            return b + (c - b) * ((d4 - d3) / ((d4 - d3) + (d5 - d6)));
        float denom = 1f / (va + vb + vc);
        return a + ab * (vb * denom) + ac * (vc * denom);
    }

    // Leaf centroids of a midpoint subdivision. A leaf's longest edge is at most `spacing`, and
    // the farthest point of a triangle from its centroid is a vertex, at most 2/3 of a median
    // (< 0.58 × the longest edge) away — which is what buys CoverageLoss its stated miss bound.
    // The depth cap only guards against absurd input; 2^8 × 64 u out-spans any cell.
    static void Subdivide(Vector3 a, Vector3 b, Vector3 c, float spacing, int depth, List<Vector3> outPts)
    {
        float longest = MathF.Sqrt(MathF.Max((a - b).LengthSquared(),
                            MathF.Max((b - c).LengthSquared(), (c - a).LengthSquared())));
        if (longest > spacing && depth < 8)
        {
            Vector3 ab = (a + b) * 0.5f, bc = (b + c) * 0.5f, ca = (c + a) * 0.5f;
            Subdivide(a, ab, ca, spacing, depth + 1, outPts);
            Subdivide(ab, b, bc, spacing, depth + 1, outPts);
            Subdivide(ca, bc, c, spacing, depth + 1, outPts);
            Subdivide(ab, bc, ca, spacing, depth + 1, outPts);
            return;
        }
        outPts.Add((a + b + c) / 3f);
    }

    /// <summary>What else the moving plugin touches — the records-only facts intent classification
    /// runs on (roadmap 3.5.3). The caller resolves them against the load order; the RULE that
    /// reads them is <see cref="ClassifyIntent"/>. Not SPLIT-specific: any finding with a moving
    /// plugin, an affected quest, and affected cells can fill this in (3.5.5 will want the same
    /// join).</summary>
    /// <param name="TouchesPairNarrative">the moving plugin defines or overrides the AFFECTED
    /// quest's own narrative records — the QUST itself, a SCEN of that quest, or a DIAL owned by
    /// it. This is the NARROW reading, chosen deliberately: "any narrative record at all" would
    /// classify a large mod as aware of this scene on the strength of an unrelated quest edit.</param>
    /// <param name="NarrativeRecords">DIAL/INFO/SCEN/QUST records anywhere in the plugin —
    /// context for the report (zero is the strongest "purely mechanical" evidence), never the
    /// deciding fact.</param>
    /// <param name="PlacementEdits">placed references (REFR/ACHR) the plugin contributes,
    /// anywhere — its mechanical work.</param>
    /// <param name="EditsInAffectedCells">of those, how many are in the cells the pair occupied
    /// or occupies now.</param>
    /// <param name="MovedRefs">the finding's own moved references.</param>
    /// <param name="AffectedInterior">the cell the pair was authored in is an interior.</param>
    /// <param name="OtherWorkExteriorOnly">the plugin HAS placement edits outside the affected
    /// cells, and every one of them is in an exterior cell. False when there is no other work —
    /// a plugin that only edits the affected interior has no exterior scope to mismatch.</param>
    public readonly record struct IntentFacts(
        bool TouchesPairNarrative, int NarrativeRecords, int PlacementEdits,
        int EditsInAffectedCells, int MovedRefs, bool AffectedInterior, bool OtherWorkExteriorOnly);

    /// <summary>WHY a finding happened, not just that it did — the rule table from
    /// RECOMMENDATIONS-2026-08-30 ("Explaining WHY, not just THAT"), frozen. Classify a finding by
    /// what else the moving plugin touches: the pair's own quest/scene/dialogue reads as deliberate
    /// (informational, low priority); placements only reads as probably an oversight (a finding);
    /// placements only where the plugin moved INTERIOR actors yet does all its other placement
    /// work in EXTERIORS is an oversight plus a scope mismatch (highest confidence).
    ///
    /// <para>WildEditCandidate is a separate axis, never a verdict (Piranha, 2026-09-01): when the
    /// plugin touches nothing in the pair's cells beyond the reference(s) it displaced — a lone
    /// displaced ref with no accompanying re-layout is the classic accidental-drag signature — the
    /// flag raises the QUESTION of a wild (unintended) edit. Records alone are never strong enough
    /// to assert one: the caller must corroborate (position sanity, the author's stated scope)
    /// before saying "wild edit" out loud, and reports it as a candidate until then. A plugin that
    /// also rewrites the pair's narrative did not drag the actor by accident, so Deliberate never
    /// carries the flag.</para>
    ///
    /// <para>The description (meta.ini nexusDescription) deliberately does NOT enter this
    /// function: the records decide, the description corroborates — an author's page text is a
    /// marketing claim, not a specification, and must never outvote the records. Its availability
    /// is a separate axis: <see cref="CorroborationOf"/>.</para></summary>
    public static (IntentVerdict Verdict, bool WildEditCandidate, string Why) ClassifyIntent(IntentFacts f)
    {
        if (f.TouchesPairNarrative)
            return (IntentVerdict.Deliberate, false,
                    "the moving plugin also rewrites this pair's own quest/scene/dialogue — the author saw the scene");
        // Candidate only where the plugin's whole presence in the pair's cells IS the displacement.
        bool lone = f.EditsInAffectedCells > 0 && f.EditsInAffectedCells <= f.MovedRefs;
        if (f.AffectedInterior && f.OtherWorkExteriorOnly)
            return (IntentVerdict.OversightScopeMismatch, lone,
                    "moved interior actors while every other placement edit is exterior — outside the work's own scope");
        return (IntentVerdict.Oversight, lone, f.NarrativeRecords > 0
            ? "narrative work elsewhere does not cover this pair's quest (narrow reading — an unrelated quest edit is not awareness)"
            : "placements only — nothing in the plugin accounts for the scene the move breaks");
    }

    /// <summary>Whether the author's stated purpose is even readable for a mover — the source-2
    /// axis, kept apart from the verdict so a missing description can be COUNTED (rule 6) and
    /// never read as evidence of anything: unjudgeable is not "no intent". Roughly one installed
    /// mod in four has no cached description, so this is a population, not an edge case.</summary>
    public static IntentCorroboration CorroborationOf(bool metaIniFound, string? description)
        => !metaIniFound ? IntentCorroboration.NoMetaIni
         : description is null ? IntentCorroboration.NoDescription
         : IntentCorroboration.DescriptionOnFile;

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

/// <summary>WHY the moving plugin separated the pair. Deliberate is informational (low priority);
/// both Oversight rows are findings, the scope-mismatch row at highest confidence. A possible wild
/// (accidental) edit rides on <see cref="DetectorModel.ClassifyIntent"/>'s candidate flag, never
/// on a verdict of its own — records alone cannot assert one.</summary>
public enum IntentVerdict { Deliberate, Oversight, OversightScopeMismatch }

/// <summary>Whether the author's stated purpose (meta.ini nexusDescription) is available to
/// corroborate a verdict. The two unavailable arms are counted, never treated as evidence.</summary>
public enum IntentCorroboration { DescriptionOnFile, NoDescription, NoMetaIni }
