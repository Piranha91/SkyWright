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

    /// <summary>One needs-witness, as the reachability join hands it to the rule (3.5.5's named
    /// tail). A witness is a records-side fact that an actor is meant to be on a specific point:
    /// a package location slot (W1, with its radius), a placed actor (W2), or a marker/furniture
    /// a package targets (W3). The caller has already found the witness, resolved its anchor, and
    /// measured it — everything here is a fact, and the DECISION is <see cref="NeedsWitness"/>.</summary>
    /// <param name="Radius">the slot's radius (0 for W2/W3): the package sends the actor somewhere
    /// within it, so the disc, not the point, is what must have ground.</param>
    /// <param name="DV">3-D distance from the witness point to the VICTIM arm's effective coverage
    /// (the loser's version of the record plus every other winning mesh nearby). NaN when the
    /// geometry could not be extracted.</param>
    /// <param name="DW">the same against the WINNER arm's effective coverage.</param>
    /// <param name="Blocker">an I/O reason the witness cannot be judged at all — an anchor arm this
    /// order cannot place, a templated or leveled actor whose packages are unreadable, a package
    /// record missing from the order. None when the numbers are meaningful.</param>
    /// <param name="WinnerSideTouched">the witness's own winning record (the ACHR, or the anchor
    /// REFR) is authored or overridden by the winner plugin or by a plugin that masters the winner
    /// — the winner's side demonstrably knew about it.</param>
    public readonly record struct WitnessFacts(
        WitnessKind Kind, float Radius, float DV, float DW, WitnessBlocker Blocker, bool WinnerSideTouched);

    /// <summary>What turns "coverage lost" into "something is broken", records-only. The 3.5.5
    /// in-game result showed coverage loss alone is not a finding — Bethesda ships the same shape,
    /// and a winner deleting ground under its new architecture is doing its job. A finding needs
    /// a witness that still NEEDS the lost ground. What "broken" is, measured in game: a placed
    /// actor ABANDONS victim-only ground — its own AI puts it on the winner's mesh, far from where
    /// the author set it down, by the first read — and an actor spawned into an island with no
    /// route out freezes there. Not "cannot function": a package-free actor set down on the lost
    /// ground stays put, and an on-mesh actor can still be walked onto it.
    ///
    /// <para>The rule, per witness point with radius r: <b>Stranded</b> iff the point had ground
    /// in the victim arm (<c>DV ≤ clearance</c> — the point itself, not the disc) and the disc has
    /// none in the winner arm (<c>DW − r &gt; clearance</c>). The <c>− r</c> is deliberate: a wide
    /// sandbox radius that still overlaps winner coverage can put the actor on usable ground. The
    /// <b>Mirror</b> — winner-only ground with a witness — is the symmetric case, reported as the
    /// mirror and never as a finding. Ground both arms cover is <b>Covered</b>; ground neither
    /// covers (the vanilla cave shape) is <b>Uncovered</b> — nothing either way.</para>
    ///
    /// <para>The resolution band. A margin inside (clearance, 2×clearance] is within the oracle's
    /// own resolution and is reported as <b>Unjudgeable</b> (counted, never cleared), not called —
    /// for a package anchor or a targeted ref (W1/W3). That resolution was measured on a WALK
    /// TARGET (an actor sent to a point that far off-mesh still gets there), and those witnesses
    /// are walk targets. A placed actor (W2) is not: in game (2026-09-01) a placed actor inside
    /// the band was displaced exactly like one well beyond it — on the winner's mesh from the
    /// first read, never at its placement — so for a placement the band collapses and Stranded
    /// holds from <c>clearance</c> up, with no upper test. The floor at <c>clearance</c> is NOT
    /// measured for placements either: it is the only figure below the in-game reading the
    /// project has, and a placed actor between 0 and <c>clearance</c> off the winner's mesh reads
    /// Covered. The driver counts those rows as a stated blind band.</para>
    ///
    /// <para>Authorship rides on a separate axis, from the intent work: a witness the winner's side
    /// authored or overrode was moved or kept on purpose (<b>Deliberate</b> — HearthFires placing
    /// its own homestead actors on its own mesh); one untouched by that side (vanilla, USSEP, the
    /// victim's own placement) is an <b>Oversight</b> — a city overhaul reverting USSEP's navmesh
    /// under a vanilla guard's patrol marker. "The winner's side" is the winner and anything
    /// downstream of it (a plugin that masters the winner saw its mesh); a plugin UPSTREAM of the
    /// winner did not, which is why Skyrim.esm placing the guard reads as untouched even though
    /// every mod masters Skyrim.esm.</para>
    ///
    /// <para>Deliberately NOT judged: connectivity (the point may be covered and still an island),
    /// which package is running at a given hour, and whether the actor is ever actually loaded.</para></summary>
    public static (WitnessVerdict Verdict, WitnessBlocker Reason, WitnessAuthorship Authorship, string Why)
        NeedsWitness(WitnessFacts f, float clearance)
    {
        var authorship = f.WinnerSideTouched ? WitnessAuthorship.Deliberate : WitnessAuthorship.Oversight;
        if (f.Blocker != WitnessBlocker.None)
            return (WitnessVerdict.Unjudgeable, f.Blocker, authorship, f.Blocker switch
            {
                WitnessBlocker.UnreadableAnchor => "the anchor arm has no position this order can read",
                WitnessBlocker.Templated => "the actor inherits its packages from a template",
                WitnessBlocker.LeveledBase => "the actor is placed from a leveled base — its packages resolve at run time",
                WitnessBlocker.PackageMissing => "a package record is not in the load order",
                _ => "unjudgeable",
            });
        if (float.IsNaN(f.DV) || float.IsNaN(f.DW))
            return (WitnessVerdict.Unjudgeable, WitnessBlocker.GeometryMissing, authorship, "the cell's geometry failed to extract");

        float marginW = f.DW - f.Radius;     // how far the disc still is from winner-arm coverage
        float marginV = f.DV - f.Radius;
        string nums = $"victim {Fmt(f.DV)}, winner {Fmt(f.DW)}" + (f.Radius > 0 ? $" − r {f.Radius:0} = {Fmt(marginW)}" : "");
        if (f.DV <= clearance && marginW > clearance)
        {
            // The band is the walk-target resolution; a placed actor is not a walk target (see the
            // summary) — for W2 it collapses, and a W2 inside it says so in its Why.
            if (f.Kind != WitnessKind.PlacedActor && marginW <= 2f * clearance)
                return (WitnessVerdict.Unjudgeable, WitnessBlocker.WithinResolutionBand, authorship,
                        $"{nums}: lost, but inside 2× the {clearance:0}u walk-target resolution — reported, not called");
            bool inFormerBand = f.Kind == WitnessKind.PlacedActor && marginW <= 2f * clearance;
            return (WitnessVerdict.Stranded, WitnessBlocker.None, authorship,
                    $"{nums}: ground under the witness before the winner, none after ({authorship})"
                    + (inFormerBand ? $" — inside 2× the {clearance:0}u walk-target band, and a placed actor at this distance was displaced in game (2026-09-01 confirmer)" : ""));
        }
        if (f.DW <= clearance && marginV > clearance)
            return (WitnessVerdict.Mirror, WitnessBlocker.None, authorship, $"{nums}: winner-only ground under a witness — the mirror, not a finding");
        if (marginV <= clearance && marginW <= clearance)
            return (WitnessVerdict.Covered, WitnessBlocker.None, authorship, $"{nums}: both arms offer ground");
        return (WitnessVerdict.Uncovered, WitnessBlocker.None, authorship, $"{nums}: neither arm offers ground — not this record's doing");

        static string Fmt(float d) => float.IsPositiveInfinity(d) ? "inf" : $"{d:0}u";
    }

    /// <summary>The HORIZONTAL distance from a point to a mesh's coverage — the mesh's triangles
    /// projected onto the ground plane. Not the frozen measure (<see cref="NeedsWitness"/> takes
    /// the 3-D <see cref="DistanceToCoverage"/>), but the reading the join reports beside it: a
    /// package anchor can sit hundreds of units ABOVE the floor its actor sandboxes on (the temple
    /// marker 328 u over the priests' heads), and a 3-D measure reads such an anchor as ground
    /// neither arm covers. A witness the 3-D rule calls Uncovered and the horizontal reading
    /// would strand is counted as an elevated anchor, never silently cleared.</summary>
    public static float DistanceToCoverageXY(Vector3 p, IReadOnlyList<Vector3> verts,
        IReadOnlyList<(int A, int B, int C, int Flags)> tris)
    {
        var q = new Vector3(p.X, p.Y, 0);
        float best = float.PositiveInfinity;
        foreach (var t in tris)
        {
            if (!CoverageTriangle(t, verts, out var a, out var b, out var c)) continue;
            Vector3 fa = new(a.X, a.Y, 0), fb = new(b.X, b.Y, 0), fc = new(c.X, c.Y, 0);
            if (Vector3.Cross(fb - fa, fc - fa).LengthSquared() <= 1e-6f) continue;   // vertical in XY: a line, not floor
            float d = (q - ClosestOnTriangle(q, fa, fb, fc)).Length();
            if (d < best) best = d;
        }
        return best;
    }

    /// <summary>One declared host edge as the placement-dependency join hands it to the rule (roadmap 4.5/4.6,
    /// the records layer). A dependent reference D names a host R through an enable parent (XESP), a linked
    /// reference (XLKR) or an activate parent (XAPR). The edge has an AUTHOR — the last plugin to set it — and
    /// positions have two views: the author's (the last position the author or one of its masters contributed,
    /// per PlacementHistory) and the winner's. The caller (DependencyIndex.Judge) has resolved both; everything
    /// here is a fact, and the DECISION is <see cref="PlacementDependency"/>.</summary>
    /// <param name="HostIsPlayer">the host is the player reference (ID 0x14) — an enable parent of the player
    /// with the opposite flag is the standard "permanently disabled" idiom, and the player has no placement.</param>
    /// <param name="HostResolved">the host has a placement, or a deletion, somewhere in this order.</param>
    /// <param name="HostDeleted">the host's record is a DELETION in one of the two compared views — the winning order,
    /// or the edge author's own (Q34, 2026-09-06). The view selects the record first and its Deleted flag is read
    /// second; the flag is NOT taken from the last plugin that ever deleted the reference, which is a different plugin
    /// whenever a deletion stripped the placement subrecord or a later record restored the reference where it already
    /// stood. Deleted at the winner leaves no position to compare against; deleted in the author's view leaves that
    /// author's "where did it stand" a reading it could never have made. <c>DependencyRow</c> keeps the two apart.</param>
    /// <param name="HostAbsent">the host's EFFECTIVE enable state — its XESP chain resolved, not its own checkbox —
    /// leaves it absent when the cell loads, in the edge author's view or in the winning order (Q32, 2026-09-06). A
    /// reference with an enable parent takes the parent's state XOR the opposite-of-parent flag and the engine ignores
    /// its own initially-disabled flag, so the checkbox this used to read both withheld hosts a parent switches ON and
    /// admitted hosts a parent switches OFF.</param>
    /// <param name="HostStateUnresolved">…and the chain could NOT be read to the end in one of those two views: a
    /// parent the view does not place, a parent deleted in it, a cycle, or an interaction with no measurement behind
    /// it. The resolver answers with a stated fallback there, so the row is withheld rather than judged on one.</param>
    /// <param name="SameSpaceAtAuthoring">host and dependent shared a SPACE in the author's view — the cell for
    /// an interior, the worldspace for an exterior. The cell alone is the wrong test outdoors: a persistent
    /// exterior reference lives in the worldspace's persistent cell whatever its coordinates, so a persistent
    /// host and a temporary dependent standing side by side would read as different cells.</param>
    /// <param name="HostChangedSpace">the host's winning space differs from the author's view (it changed
    /// interior cell, or worldspace). A move between exterior grid cells is a distance, not a space change.</param>
    /// <param name="HostMoved">distance from the host's authoring-view position to its winning one; NaN when
    /// either view is missing.</param>
    /// <param name="DependentDeleted">the WINNING record of the dependent is a deletion. The winner alone, deliberately:
    /// "nothing left behind" is a claim about the order that ships, so a dependent some earlier plugin deleted and a
    /// later one restored is judged by the ordinary placement and author rules (Q34). Its author-view reading rides on
    /// <c>DependencyRow.DependentDeletedThen</c> for the report.</param>
    /// <param name="DependentDeletedInAuthorView">the dependent's record is a DELETION in the edge author's own view
    /// (Q35, 2026-09-07). An edge is extracted only from a record that CARRIED a placement, so the author is always in
    /// the toucher list and the RECORD view always selects its own record — which makes this true exactly when the
    /// author deleted the reference while keeping its placement data and the edge (a shape real tools write and
    /// Mutagen's own writer normalises away). The author's view then holds no placement to compare against, in either
    /// of two ways. Usually the snap it selects is that same deleted record's leftover position, which the engine never
    /// reads. But <see cref="PlacementHistory"/> drops a snap that repeats the previous one and
    /// <see cref="Snap.SameAs"/> does not consider the edge set, so an author that repeats an EARLIER plugin's deleted
    /// placement while adding the edge leaves no snap at all, and the view falls back to an older LIVE position its own
    /// record contradicts. Either way the same-space test, the authored offset and the re-placement comparison rest on
    /// a placement that view does not have — and in the second shape the re-placement comparison can come back EQUAL,
    /// so the old rule returned LeftBehind. The row is withheld — UNLESS the winning order deletes the dependent too,
    /// where "nothing left behind" is a claim about the shipped order that needs no baseline at all.</param>
    /// <param name="DependentRePlaced">the dependent's winning placement differs from the author's view.</param>
    /// <param name="DependentRePlacedByMoverSide">…and it was set by the host's mover or a plugin downstream of it.</param>
    /// <param name="Host">what the host is: something to rest on or hang from, a marker, or something else.</param>
    public readonly record struct DependencyFacts(
        HostEdgeKind Kind, bool HostIsPlayer, bool HostResolved, bool HostDeleted, bool HostAbsent, bool HostStateUnresolved,
        bool SameSpaceAtAuthoring, bool HostChangedSpace, float HostMoved,
        bool DependentDeleted, bool DependentDeletedInAuthorView, bool DependentRePlaced, bool DependentRePlacedByMoverSide, HostClass Host);

    /// <summary>The placement-dependency rule, records-only (frozen 2026-09-01 from the handoff of 2026-09-03).
    /// Piranha's two classes — apples left in the air when the table moves (stranded support), a sconce left
    /// on the old wall line when the wall moves (orphaned attachment) — are contact facts no record declares, so
    /// most of both is geometry. This rule covers the minority of refs that DECLARE a host, and it is
    /// differential: nothing here says anything floats, only that a declared arrangement changed under one
    /// side without the other side following.
    ///
    /// <para>Per edge, in order: a host that is the player, or that this order cannot place, or that the winning order
    /// or the edge author's own view has deleted, or a DEPENDENT the author's own view has deleted while the winner
    /// still has it (Q35 — that view holds no placement to compare against), or whose authoring view is missing, is
    /// <b>Unjudgeable</b> by that reason. Then the enable chain: a host whose
    /// EFFECTIVE state cannot be resolved in one of the two compared views is <b>Unjudgeable</b> too, and that gate
    /// comes BEFORE the movement test on purpose — an <b>Intact</b> verdict is a clean answer, and a clean answer may
    /// not rest on the resolver's fallback (Q32). A host that stayed within <paramref name="threshold"/>
    /// of its authoring-view position in the same space leaves the edge <b>Intact</b>. A moved host that the chain
    /// leaves absent when the cell loads, or that was in a different space from its dependent when the edge was
    /// authored, is a toggle relationship, not a physical one — <b>Unjudgeable</b>, counted. A deleted dependent leaves
    /// nothing behind (<b>DependentRemoved</b>). A dependent re-placed by the mover or a plugin downstream of
    /// it is <b>MovedTogether</b> WHATEVER the new offset — a deliberate re-arrangement is not a defect; one
    /// re-placed by an unrelated third plugin is <b>Unjudgeable</b>, nobody's fault this pass can name.
    /// Otherwise the dependent stayed where the author put it while its host moved: <b>LeftBehind</b>. The
    /// host class rides on the facts for the caller to split by — a marker or toggle host is an enable
    /// relationship and is never counted with 4.5/4.6.</para>
    ///
    /// <para>Deliberately NOT judged: whether anything rests on anything (no mesh is read), rotation-only
    /// moves (PlacementHistory records position and cell), and which of two moves of the same host mattered.</para></summary>
    public static (DependencyVerdict Verdict, DependencyBlocker Reason, string Why) PlacementDependency(DependencyFacts f, float threshold)
    {
        if (f.HostIsPlayer)
            return (DependencyVerdict.Unjudgeable, DependencyBlocker.HostIsPlayer, "the host is the player reference — the permanently-disabled idiom, not a placement");
        if (!f.HostResolved)
            return (DependencyVerdict.Unjudgeable, DependencyBlocker.HostUnresolved, "the host reference has no placement anywhere in this order");
        if (f.HostDeleted)
            return (DependencyVerdict.Unjudgeable, DependencyBlocker.HostDeleted, "the host's record is a deletion in the winning order or in the edge author's own view — no pair of positions to compare");
        // Q35: the dependent's side of the same question the line above asks about the host — and it sits here, ahead
        // of every gate that would read a position, for the same reason Q32's state gate sits ahead of Intact: no
        // verdict, clean or withheld, may rest on a value that is not a reading. An Unjudgeable/CrossCell decided by a
        // space read off a deleted record is not a clean withholding either. It does NOT fire when the winner deletes
        // the dependent too: "nothing left behind" needs no baseline, so DependentRemoved below is untouched.
        if (f.DependentDeletedInAuthorView && !f.DependentDeleted)
            return (DependencyVerdict.Unjudgeable, DependencyBlocker.DependentDeletedInAuthorView,
                    "the dependent's record is a deletion in the edge author's own view while the winning order still has it — that view has no placement to compare against");
        if (float.IsNaN(f.HostMoved))
            return (DependencyVerdict.Unjudgeable, DependencyBlocker.NoAuthoringView, "the edge's author and its masters contributed no position for the host or the dependent");
        if (f.HostStateUnresolved)
            return (DependencyVerdict.Unjudgeable, DependencyBlocker.HostStateUnresolved,
                    "the host's enable-parent chain does not resolve in the edge author's view or in the winning order — a parent the view does not place, a parent deleted in it, or a cycle; the answer there is a fallback, not a reading");
        bool moved = f.HostChangedSpace || f.HostMoved > threshold;
        string move = f.HostChangedSpace ? "the host left its cell or worldspace" : $"the host moved {f.HostMoved:0}u";
        if (!moved)
            return (DependencyVerdict.Intact, DependencyBlocker.None, $"the host is where the edge's author left it ({f.HostMoved:0}u ≤ {threshold:0}u)");
        if (f.HostAbsent)
            return (DependencyVerdict.Unjudgeable, DependencyBlocker.HostDisabled, $"{move}, but its enable chain leaves it absent when the cell loads — in the edge author's view or in the winning order — a toggle arrangement, not judged");
        if (!f.SameSpaceAtAuthoring)
            return (DependencyVerdict.Unjudgeable, DependencyBlocker.CrossCell, $"{move}, but host and dependent were in different cells or worldspaces when the edge was authored — a toggle, not a support");
        if (f.DependentDeleted)
            return (DependencyVerdict.DependentRemoved, DependencyBlocker.None, $"{move}; the dependent is deleted — nothing left behind");
        if (f.DependentRePlaced && f.DependentRePlacedByMoverSide)
            return (DependencyVerdict.MovedTogether, DependencyBlocker.None, $"{move} and the mover's side re-placed the dependent — a deliberate re-arrangement, whatever the new offset");
        if (f.DependentRePlaced)
            return (DependencyVerdict.Unjudgeable, DependencyBlocker.DependentMovedByThirdParty, $"{move} and a plugin unrelated to the mover re-placed the dependent — nobody's fault this pass can name");
        return (DependencyVerdict.LeftBehind, DependencyBlocker.None, $"{move}; the dependent stayed where the edge's author put it ({f.Kind} edge, {f.Host} host)");
    }

    /// <summary>What a host IS, for the split the rule's caller makes. Something to rest on or mount on — a
    /// static, a movable static, furniture, a container, a door — is Physical. A Marker is a base the engine renders
    /// nothing for, whatever its record type: an enable relationship to a marker is a toggle, never a support.
    /// <b>The marker rule is the record's Is-Marker header flag (0x00800000)</b> — the engine's own rule for drawing
    /// no base — when the caller has read it (<paramref name="markerFlag"/> non-null: the contact index since the
    /// 2026-09-03 fold, after the in-game arm found JK's sit-marker chairs — Furniture, an ordinary mesh name, the flag
    /// set — read as Sunk clutter). A caller that reads no flags (the records pass) passes null and gets the older
    /// editor-id heuristic — "marker" anywhere in the name (XMarker, XMarkerHeading, enable markers, map markers) —
    /// which the contact pass now reports against the flag as a disagreement count, never uses. Everything else —
    /// activators used as switches, lights, trees, actors, an unresolved base — is Other, reported apart.</summary>
    public static HostClass ClassifyHost(string? baseType, string? baseEditorId, bool? markerFlag = null)
    {
        bool marker = markerFlag ?? (baseEditorId is not null && baseEditorId.Contains("marker", StringComparison.OrdinalIgnoreCase));
        if (marker) return HostClass.Marker;
        return baseType switch
        {
            "Static" or "MoveableStatic" or "Furniture" or "Container" or "Door" => HostClass.Physical,
            _ => HostClass.Other,
        };
    }

    /// <summary>What a DEPENDENT is — the split the first live run showed the host class alone cannot make. Every
    /// physical-host LeftBehind row on the first order had an actor or a trigger as the dependent: an NPC linked
    /// to the throne it sits on, prisoners linked to a jail door, a quest trigger linked to a fire pillar. An actor
    /// walks to its linked furniture wherever it now stands, and a trigger volume marks a spot; neither rests on
    /// or hangs from anything. The two classes are about a PHYSICAL dependent on a physical host — clutter on a
    /// table, a fixture on a wall — so that is the row the caller counts. Actors are the placed-NPC arm or an NPC
    /// / leveled-NPC base; Control is anything whose base is a marker (the Is-Marker header flag when the caller read
    /// it — <paramref name="markerFlag"/> — else "marker" in the editor id, the same split <see cref="ClassifyHost"/>
    /// makes), a trigger, an idle marker or an FX object by editor id; Physical is a static, furniture, container,
    /// door, light, activator, tree, flora or any item type; Other is the rest, an unresolved base included.</summary>
    public static DependentClass ClassifyDependent(string? recordType, string? baseType, string? baseEditorId, bool? markerFlag = null)
    {
        if (recordType == "PlacedNpc" || baseType is "Npc" or "LeveledNpc") return DependentClass.Actor;
        if (baseType == "IdleMarker") return DependentClass.Control;
        bool marker = markerFlag ?? (baseEditorId is not null && baseEditorId.Contains("marker", StringComparison.OrdinalIgnoreCase));
        if (marker) return DependentClass.Control;
        if (baseEditorId is not null && (baseEditorId.Contains("trigger", StringComparison.OrdinalIgnoreCase)
                                      || baseEditorId.StartsWith("FX", StringComparison.Ordinal)))
            return DependentClass.Control;
        return baseType switch
        {
            "Static" or "MoveableStatic" or "Furniture" or "Container" or "Door" or "Light" or "Activator"
                or "Tree" or "Flora" or "Ingestible" or "Ingredient" or "MiscItem" or "Book" or "Weapon" or "Armor"
                or "Ammunition" or "SoulGem" or "Key" or "Scroll" or "LeveledItem" => DependentClass.Physical,
            _ => DependentClass.Other,
        };
    }

    /// <summary>Rule 12 applied to a dependency edge: which population does a (mover, edge author) pair belong
    /// to? Bethesda moving a Bethesda host under a Bethesda edge is the floor. USSEP as the mover is its own
    /// row. A vanilla plugin moving a host under a MOD's edge is the case the authoring-view assumption is
    /// weakest on — the mod's author almost certainly ran Update.esm and the DLCs whether or not the plugin
    /// declares them — so it is kept apart rather than folded into either the floor or the class. Everything
    /// else is a mod moving a host: the class, split by the author's side in the report.</summary>
    public static DependencyPopulation ClassifyDependencyPopulation(bool moverIsVanilla, bool authorIsVanilla, bool moverIsUssep)
        => moverIsVanilla && authorIsVanilla ? DependencyPopulation.BethesdaFloor
         : moverIsUssep ? DependencyPopulation.UssepMover
         : moverIsVanilla ? DependencyPopulation.VanillaMoverUnderModEdge
         : DependencyPopulation.ModMover;

    /// <summary>One cross-plugin deep overlap as the overlap pass hands it to the rule (milestone 5, roadmap 4.1 — the
    /// differential framing). Two placed references from different responsible plugins have world boxes that overlap
    /// deeply NOW. L is the LATER of the two responsible plugins — the one that could have seen the other's work. The
    /// caller (OverlapIndex.Judge) has rebuilt the arm WITHOUT L's contribution: L's reference at its previous snap in
    /// the placement history, or absent if L introduced it; everything here is a fact, and the DECISION is
    /// <see cref="OverlapProvenance"/>.</summary>
    /// <param name="LaterHadPreviousPlacement">L's reference has a snap before L's — L moved or re-placed an existing
    /// reference rather than introducing one.</param>
    /// <param name="PreviousPlacementPresent">…and that previous snap is a placed, enabled, undeleted reference in the
    /// same space as the other reference, so a box could be rebuilt for it. False when L enabled it, relocated it
    /// from another cell or worldspace, or introduced it.</param>
    /// <param name="WithoutLaterOverlaps">the rebuilt previous box overlaps the other reference's WINNING box at the
    /// same depth-and-fraction threshold as the hit itself.</param>
    /// <param name="OtherWinnerInLaterClosure">the plugin that set the other reference's winning placement is L or is
    /// in L's transitive master closure — the position was in L's editor.</param>
    public readonly record struct OverlapFacts(bool LaterHadPreviousPlacement, bool PreviousPlacementPresent,
                                               bool WithoutLaterOverlaps, bool OtherWinnerInLaterClosure);

    /// <summary>The overlap-provenance rule, records-only (frozen 2026-09-01 from the handoff of 2026-09-04). A deep
    /// overlap is an absolute fact; this rule asks the with-vs-without question and the who-saw-what question that
    /// the clobber, needs-witness and placement-dependency detectors earned their findings by asking.
    ///
    /// <para><b>Origin.</b> If the overlap is already there in the arm without L — L's reference at its previous
    /// placement already intersects the other's winning box — L <b>Inherited</b> it: L forwarded or nudged into an
    /// intersection an earlier plugin (or the same author) created, and this pass attributes it to nobody. Otherwise L
    /// <b>Introduced</b> it: by placing a new reference, by moving one in, or by enabling or relocating one into
    /// the other's box. The differential rewards a fix — a plugin that moves a reference OUT of an inherited overlap
    /// produces no hit at all, so it never reaches this rule.</para>
    ///
    /// <para><b>Sight.</b> If the other reference's winning placement was set by L or by a plugin in L's master
    /// closure, L placed into known context: <b>Seen</b> — an author's own choice, reported as "knowingly overlapping",
    /// never a finding of L's. If it was set by a plugin outside L's closure, it could not have been in L's editor:
    /// <b>Blind</b>. Seen is exactly the declared-dependency relation read from the later side, so the population this
    /// runs over is every cross-responsible deep hit, declared or not, and a driver can check its plumbing by
    /// counting: Seen must equal the declared-dependency deep count.</para>
    ///
    /// <para>The class is <b>Introduced + Blind</b>. Introduced + Seen is the coordination row; Inherited rows are the
    /// authored floor's shape. Geometry ranks within the class (<see cref="ClassifyOverlapScale"/>, depth × fraction);
    /// it never thresholds it into a verdict. Deliberately NOT judged: rotation- or scale-only re-placements (the
    /// history does not record them), an intersection created two movers back, and anything about collision or
    /// contact — this is a conservative rotated-AABB broad phase, and nothing here is verified in game.</para></summary>
    public static (OverlapOrigin Origin, OverlapSight Sight, string Why) OverlapProvenance(OverlapFacts f)
    {
        var sight = f.OtherWinnerInLaterClosure ? OverlapSight.Seen : OverlapSight.Blind;
        string saw = f.OtherWinnerInLaterClosure
            ? "the other reference's winning placement was set by the later plugin's own masters — it was in its editor"
            : "the other reference's winning placement was set outside the later plugin's master closure — it could not have been in its editor";
        if (f.WithoutLaterOverlaps)
            return (OverlapOrigin.Inherited, sight, $"the later plugin's reference already overlapped at its previous placement — the intersection pre-dates it; {saw}");
        string how = !f.LaterHadPreviousPlacement ? "introduced its reference into the other's box"
                   : !f.PreviousPlacementPresent ? "enabled or relocated its reference into the other's box"
                   : "moved its reference into the other's box (it stood clear at its previous placement)";
        return (OverlapOrigin.Introduced, sight, $"the later plugin {how}; {saw}");
    }

    /// <summary>Rule 12 applied to an overlap: which population does a (responsible A, responsible B) pair belong to?
    /// Both Bethesda (Dawnguard × HearthFires, a DLC into a base-game cell) is the floor's shape — the same shape as the
    /// clobber floor. USSEP on either side is its own row: it moves vanilla objects deliberately and everything loads
    /// after it. Everything else is mod-on-mod, the class.</summary>
    public static OverlapPopulation ClassifyOverlapPopulation(bool aIsVanilla, bool bIsVanilla, bool aIsUssep, bool bIsUssep)
        => aIsVanilla && bIsVanilla ? OverlapPopulation.BethesdaFloor
         : aIsUssep || bIsUssep ? OverlapPopulation.UssepInvolved
         : OverlapPopulation.ModOnMod;

    /// <summary>The ranker's scale class, from the largest dimension of the SMALLER box in the hit — the thing that is
    /// buried. A wall through a house is a different finding from two bottles sharing a shelf, and the reader should
    /// see which is which without a threshold deciding for them: 256 u and up is architecture (a wall piece is
    /// 128–512 u), 64 u to 256 u is furniture-scale, under 64 u is clutter. Reported, never used as a verdict.</summary>
    public static OverlapScale ClassifyOverlapScale(float smallerBoxMaxDimension)
        => smallerBoxMaxDimension >= 256f ? OverlapScale.Architecture
         : smallerBoxMaxDimension >= 64f ? OverlapScale.Furniture
         : OverlapScale.Clutter;

    /// <summary>One (dependent, moved host) pair as the contact pass hands it to the rule (roadmap 4.5 / 4.6, the
    /// geometry half; charter milestone 8 over milestone 6's primitive). The caller (ContactIndex) has measured the
    /// pair in two arms with <c>SpatialOverlap.Contact</c>: the AUTHORING arm — the order without the host's mover, the
    /// host at its previous placement, the dependent where the last plugin outside the mover's side left it — and the
    /// WINNER arm, where the dependent is tested against the host AND every other physical reference near it. Every
    /// field is a fact; the decision is <see cref="PlacementContact"/>.</summary>
    /// <param name="AuthoringDistance">least surface distance between the two in the authoring arm (0 on touch or crossing).</param>
    /// <param name="AuthoringPenetration">deepest a dependent vertex sits inside the host in the authoring arm (0 when none does).</param>
    /// <param name="AuthoringNormalUp">cosine between the host's contact normal and +Z in the authoring arm: 1 under, 0 beside,
    /// −1 above; NaN when no normal could be read.</param>
    /// <param name="DependentDisabled">the dependent is disabled AT RUNTIME under the winner — its own initially-disabled
    /// flag when it has no enable parent, else the parent chain's state (<c>EnableState.Resolve</c>; the R1 fold,
    /// 2026-09-03). <paramref name="DependentDisabledViaParent"/> says which: a flag is the −30000 idiom (the fix); a
    /// parent means the thing is simply absent. <paramref name="DependentEnableUnresolved"/>: the chain could not be
    /// followed in this order (a cycle, or a parent nobody places).</param>
    /// <param name="DependentRePlacedByMoverSide">the dependent's winning placement was set by the host's mover or a plugin
    /// downstream of it (a deliberate re-arrangement, whatever the geometry says).</param>
    /// <param name="WinnerDistance">the best contact the dependent has under the winner against ANY physical reference within
    /// reach — least distance; <paramref name="WinnerPenetration"/> the deepest penetration into any of them.</param>
    /// <param name="WinnerContactIsHost">…and that best contact is with the moved host itself (it moved a little and the
    /// dependent is still on it).</param>
    /// <param name="WinnerAgainstExtent">how wide the thing the winner penetration was measured against is, along the contact
    /// normal (the R4 fold, 2026-09-03); NaN when nothing was measured or the normal could not be read.</param>
    /// <param name="Motion">what physics would do with the dependent at cell load — rides on the facts for the caller's split;
    /// the verdict never reads it.</param>
    /// <param name="WinnerPreexistingPenetration">the differential Sunk rule (closed-slabs lane, 2026-10-04): how deep the dependent
    /// already sat in the same against-reference as authored, both at their authoring placements; only the depth beyond it can make
    /// the row Sunk. 0 when not measured (callers that do not opt in read exactly as before).</param>
    /// <param name="WinnerPreexistingOnly">the winner contact is such a pre-existing overlap and nothing else is within ε: the row
    /// is ReSupported with <see cref="PreexistingOnlyLabel"/>.</param>
    /// <summary>The decision model's coverage quantities, independent of a geometry implementation.
    /// Raw geometry evidence and arithmetic uncertainty remain with the measuring caller.</summary>
    public sealed record CoverageFacts(double ProjectedArea, double CoveredArea, double UncoveredArea,
        double? MinGap, double? MaxGap, bool Complete, string? RefusalReason, string FullCoverReading, string? UncertaintyReason);

    public readonly record struct ContactFacts(
        bool DependentGeometry, bool HostGeometry, bool AuthoringMeasured,
        float AuthoringDistance, float AuthoringPenetration, float AuthoringNormalUp,
        bool DependentDeleted, bool DependentDisabled, bool DependentRePlacedByMoverSide,
        bool WinnerMeasured, float WinnerDistance, float WinnerPenetration, bool WinnerContactIsHost,
        DependentMotion Motion,
        bool DependentDisabledViaParent = false, bool DependentEnableUnresolved = false, float WinnerAgainstExtent = float.NaN,
        float WinnerPreexistingPenetration = 0f, bool WinnerPreexistingOnly = false,
        CoverageFacts? WinnerCoverage = null,
        CoverageFacts? WinnerAuthoredCoverage = null,
        bool WinnerCoverageInputsExactlyUnchanged = false);

    /// <summary>The label a ReSupported row carries when its only contact under the winner is an overlap that already existed as
    /// authored (Gate A review F1, 2026-10-04): counted apart from ReSupported in every report, never merged into it.</summary>
    public const string PreexistingOnlyLabel = "ReSupported (pre-existing overlap only)";

    /// <summary>The placement-contact rule (frozen 2026-09-02 from the handoff of 2026-09-07). Piranha's two classes —
    /// apples left in the air when the table moves (stranded support), a sconce left on the old wall line when the
    /// wall moves (orphaned attachment) — are contact facts no record declares (the records pass measured that: zero
    /// rows), so this rule reads them from the surfaces, differentially.
    ///
    /// <para><b>The relationship</b>, from the authoring arm. Touching — surfaces within <paramref name="epsilon"/> and
    /// no vertex deeper than <paramref name="epsilon"/> inside — with the host's normal within ~30° of +Z
    /// (<paramref name="supportCos"/>) is <b>Supported</b>; within ~30° of horizontal (|up| ≤ <paramref name="attachCos"/>)
    /// is <b>Attached</b>; within ~30° of −Z is <b>Hung</b>. A gap wider than ε is no relationship. A penetration deeper
    /// than ε as AUTHORED is no relationship either — the thing was already sunk when it shipped, which is the absolute
    /// floor's business, not a differential finding. Contact on a slope between the bands is reported as none.</para>
    ///
    /// <para><b>The outcome</b>, from the winner arm, in order: a dependent that is deleted, or disabled under the winner
    /// by its own flag, is <b>Removed</b> — the fix, never a finding. One whose enable chain cannot be followed is
    /// <b>Unjudgeable / EnableChainUnresolved</b>; one disabled at runtime THROUGH its parent is <b>Unjudgeable /
    /// RuntimeDisabled</b> — absent, not a defect (the R1 fold: the game showed the rule five rows whose dependent, host or
    /// against-ref never existed at runtime). One the mover's side re-placed is <b>MovedTogether</b>, whatever its new
    /// contact. One that now sits deeper than ε inside anything — counting only depth beyond what it already had in that same
    /// reference as authored (the differential Sunk rule, 2026-10-04; a pre-existing overlap counts as contact, and a row whose
    /// only contact is one reads <see cref="PreexistingOnlyLabel"/>) — is <b>Sunk</b> — unless the depth exceeds the width of the
    /// thing it is measured against along the contact normal, which no interior point of a closed object can do: that is
    /// <b>Unjudgeable / DepthExceedsObject</b>, a refused reading (the R4 fold: a bookcase "105 u inside" a 30 u jar). One
    /// that touches anything within ε — the host at its new place, or something else the room was rebuilt with, a whole
    /// tower shell included (the R3 fold: the volume cap gates moved hosts, not re-supporters) — is <b>ReSupported</b>
    /// (counted, not a finding). Otherwise it touches nothing: <b>Stranded</b> for a supported dependent, <b>Orphaned</b>
    /// for an attached or hung one.</para>
    ///
    /// <para><b>What the class claims</b> (in game, 2026-09-03, the lab's JK's–OCW pair): a Fixed Stranded row is an
    /// object hanging in the air where its host was — it <b>floats in air</b>, three of three runtime-real rows (a strongbox
    /// and a lit candlestick on bare walls, a magnifier 60 u over the floor), and the patch put all three back. What a
    /// Fixed Orphaned row looks like in game is still unseen: both lab instances were the R3 false positive.</para>
    ///
    /// <para>Deliberately NOT judged: whether physics will drop a movable dependent onto the floor (4.7 Part C says it
    /// does; the motion class rides on the facts and the caller splits by it); rotation- and scale-only moves of the
    /// host (the history does not record them); terrain (LAND is not a reference, so an exterior dependent may be on
    /// the ground and read Stranded — the caller splits interior from exterior); whether Sunk is visible (the in-game arm
    /// said the four readings it saw were not, each for a reason the folds above now gate; Sunk stays beside the class).</para></summary>
    public static (ContactRelation Relation, ContactVerdict Verdict, ContactBlocker Reason, string Why)
        PlacementContact(ContactFacts f, float epsilon = 4f, float supportCos = 0.866f, float attachCos = 0.5f)
    {
        if (!f.DependentGeometry)
            return (ContactRelation.None, ContactVerdict.Unjudgeable, ContactBlocker.NoDependentGeometry, "the dependent has no geometry to test (no mesh, or nothing solid in it)");
        if (!f.HostGeometry)
            return (ContactRelation.None, ContactVerdict.Unjudgeable, ContactBlocker.NoHostGeometry, "the host has no geometry to test (no mesh, or nothing solid in it)");
        if (!f.AuthoringMeasured)
            return (ContactRelation.None, ContactVerdict.Unjudgeable, ContactBlocker.AuthoringArmMissing, "the arm without the mover could not be built");

        if (f.AuthoringDistance > epsilon)
            return (ContactRelation.None, ContactVerdict.NoRelation, ContactBlocker.None, $"not in contact as authored — {f.AuthoringDistance:0.#}u apart (ε {epsilon:0.#}u)");
        if (f.AuthoringPenetration > epsilon)
            return (ContactRelation.None, ContactVerdict.NoRelation, ContactBlocker.None, $"already sunk {f.AuthoringPenetration:0.#}u into the host as authored — the absolute floor's business, not a differential finding");
        if (float.IsNaN(f.AuthoringNormalUp))
            return (ContactRelation.None, ContactVerdict.NoRelation, ContactBlocker.None, "in contact as authored, but the host presents no readable normal there");

        var relation = f.AuthoringNormalUp >= supportCos ? ContactRelation.Supported
                     : f.AuthoringNormalUp <= -supportCos ? ContactRelation.Hung
                     : MathF.Abs(f.AuthoringNormalUp) <= attachCos ? ContactRelation.Attached
                     : ContactRelation.None;
        if (relation == ContactRelation.None)
            return (relation, ContactVerdict.NoRelation, ContactBlocker.None, $"in contact as authored on a slope (up-cosine {f.AuthoringNormalUp:0.00}) — neither resting on nor mounted against");

        string rel = relation switch
        {
            ContactRelation.Supported => $"rested on the host as authored ({f.AuthoringDistance:0.#}u gap, {f.AuthoringPenetration:0.#}u sunk)",
            ContactRelation.Attached => $"was mounted against the host as authored ({f.AuthoringDistance:0.#}u gap)",
            _ => $"hung from the host as authored ({f.AuthoringDistance:0.#}u gap)",
        };
        string motion = f.Motion == DependentMotion.Movable ? "; MOVABLE — physics drops or shoves it at cell load" : "";

        if (f.DependentDeleted)
            return (relation, ContactVerdict.Removed, ContactBlocker.None, $"{rel}; the dependent is deleted under the winner — the fix, not a finding");
        if (f.DependentEnableUnresolved)
            return (relation, ContactVerdict.Unjudgeable, ContactBlocker.EnableChainUnresolved, $"{rel}; the dependent's enable-parent chain cannot be followed in this order (a cycle, or a parent nobody places) — its runtime state is unknown");
        if (f.DependentDisabled && f.DependentDisabledViaParent)
            return (relation, ContactVerdict.Unjudgeable, ContactBlocker.RuntimeDisabled, $"{rel}; the dependent is disabled at runtime through its enable parent under the winner — absent, not a defect");
        if (f.DependentDisabled)
            return (relation, ContactVerdict.Removed, ContactBlocker.None, $"{rel}; the dependent is initially disabled under the winner — the fix, not a finding");
        if (f.DependentRePlacedByMoverSide)
            return (relation, ContactVerdict.MovedTogether, ContactBlocker.None, $"{rel}; the mover's side re-placed the dependent — a deliberate re-arrangement, whatever the new contact");
        if (!f.WinnerMeasured)
            return (relation, ContactVerdict.Unjudgeable, ContactBlocker.WinnerArmMissing, $"{rel}; the winner arm could not be measured");

        // The differential Sunk rule: only depth that was not already there as authored (the same reference, both at their
        // authoring placements) can sink a row. R4 still reads the total depth, the physical reading.
        float pre = f.WinnerPreexistingPenetration > 0f ? f.WinnerPreexistingPenetration : 0f;
        string against = f.WinnerContactIsHost ? "the host at its new place" : "something else";
        if (f.WinnerPenetration - pre > epsilon)
        {
            if (!float.IsNaN(f.WinnerAgainstExtent) && f.WinnerPenetration > f.WinnerAgainstExtent)
                return (relation, ContactVerdict.Unjudgeable, ContactBlocker.DepthExceedsObject, $"{rel}; under the winner it reads {f.WinnerPenetration:0.#}u inside {against} that is only {f.WinnerAgainstExtent:0.#}u across along the contact normal — no interior point of a closed object is that far from its surface; the reading is refused, not Sunk{motion}");
            string authored = pre > 0f ? $", {pre:0.#}u of it already as authored ({f.WinnerPenetration - pre:0.#}u new)" : "";
            return (relation, ContactVerdict.Sunk, ContactBlocker.None, $"{rel}; under the winner it sits {f.WinnerPenetration:0.#}u inside {against}{authored}{motion}");
        }
        if (f.WinnerCoverage is { } cover && !(f.WinnerCoverageInputsExactlyUnchanged
            && cover.Complete && f.WinnerAuthoredCoverage is { Complete: true }))
        {
            var authored = f.WinnerAuthoredCoverage;
            if (!cover.Complete || cover.FullCoverReading == "numerically_uncertain" || authored is null
                || !authored.Complete || authored.FullCoverReading == "numerically_uncertain")
                return (relation, ContactVerdict.Unjudgeable, ContactBlocker.ShallowCoverageIncomplete,
                    $"{rel}; plausible shallow coverage against {against} is incomplete or numerically uncertain: winner {cover.RefusalReason ?? cover.UncertaintyReason ?? cover.FullCoverReading}, authored {authored?.RefusalReason ?? authored?.UncertaintyReason ?? authored?.FullCoverReading ?? "missing"}; no clear-scene or burial claim{motion}");
            if (cover.ProjectedArea > 0 && cover.FullCoverReading == "nominal_whole_cover" && cover.UncoveredArea == 0
                && authored.FullCoverReading != "nominal_whole_cover")
                return (relation, ContactVerdict.Sunk, ContactBlocker.None,
                    $"{rel}; the thin dependent has newly whole projected surface coverage by upward core faces of {against}, {cover.MinGap:0.###}–{cover.MaxGap:0.###}u above it (authored covered fraction {(authored.ProjectedArea > 0 ? authored.CoveredArea / authored.ProjectedArea : 0):0.####}); approximate polygon geometry, not certified visibility or volume; core penetration remains {f.WinnerPenetration:0.###}u{motion}");
        }
        if (f.WinnerPreexistingOnly)
            return (relation, ContactVerdict.ReSupported, ContactBlocker.None, $"{rel}; {PreexistingOnlyLabel}: under the winner the only thing within {epsilon:0.#}u is {against} it already overlapped as authored — {f.WinnerPenetration:0.#}u inside, {pre:0.#}u of it already as authored; the rest of it may float{motion}");
        if (f.WinnerDistance <= epsilon)
            return (relation, ContactVerdict.ReSupported, ContactBlocker.None, $"{rel}; under the winner it still touches {(f.WinnerContactIsHost ? "the host at its new place" : "another physical reference")} ({f.WinnerDistance:0.#}u) — the room was rebuilt around it");
        var verdict = relation == ContactRelation.Supported ? ContactVerdict.Stranded : ContactVerdict.Orphaned;
        return (relation, verdict, ContactBlocker.None, $"{rel}; under the winner it touches nothing within {epsilon:0.#}u (nearest {f.WinnerDistance:0.#}u){motion}");
    }

    /// <summary>One fixture against one mover, as the empty-space search (<c>_shared/ContactAssembly.cs</c>) hands it to the rule.
    /// An ASSEMBLY is the fixture plus everything it touches within ε, transitively, and the items its weapon racks mount; an ANCHOR is a
    /// floor or wall (<c>EmptySpaceSearch.IsAnchor</c>), which ends the walk. GROUNDED means the assembly touches an anchor. The
    /// AUTHORING arm is the order without the mover's side, the WINNER arm the order as loaded; the R1 enable state per arm.</summary>
    /// <param name="Held">the fixture stays where the winner places it at load: Fixed by its record type or NIF, mounted by a weapon
    /// rack, or a loose body resting on a Fixed reference. A loose body that is not held falls (the rest rule's class).</param>
    /// <param name="AuthoringAnchorChangedByMover">an anchor the authoring assembly touches is absent from the winner arm, or placed
    /// elsewhere there: the mover's side removed or moved it.</param>
    /// <param name="RePlacedByMoverSide">the fixture's winning placement was set by the mover or a plugin downstream of it.</param>
    /// <param name="BethesdaFloor">the mover and the fixture's placer are both Bethesda or USSEP files.</param>
    /// <param name="AuthoringRead">the order without the mover was walked. The search skips that walk when the winner arm is grounded
    /// and the mover's side did not re-place the fixture: no answer of the authoring arm can make such a fixture the class, and that
    /// walk is the costly one.</param>
    public readonly record struct EmptySpaceFacts(
        bool Interior, bool ExistsWinner, bool ExistsAuthoring, DependentMotion Motion, bool Held,
        bool WinnerOverCap, bool AuthoringOverCap, bool WinnerGrounded, bool AuthoringGrounded, bool AuthoringAnchorChangedByMover,
        bool RePlacedByMoverSide, bool BethesdaFloor, int MaxMembers = 256, bool AuthoringRead = true);

    /// <summary>LEFT IN EMPTY SPACE (2026-10-14): a fixture that stood in a room in the order without the mover, and whose assembly
    /// reaches no floor or wall under the winner because the mover removed or moved what it stood on or against. The sibling of
    /// STRANDED: Stranded reads one dependent against one host and calls a fixture held when anything touches it; this rule follows
    /// what touches it, so a rack holding a rack holding a sword in the void is still in the void. Order: exterior refused (the land is
    /// not read); absent in an arm; unknown motion refused; a loose body that is not held falls; an assembly over the cap refused in the
    /// arm that needs it; ungrounded as authored → floats as shipped (ungrounded now too) or no relation; no anchor the mover changed →
    /// no relation; re-placed by the mover's side → grounded or still in empty space; grounded now → re-grounded; Bethesda floor →
    /// counted apart; else LEFT IN EMPTY SPACE.</summary>
    public static (EmptySpaceVerdict Verdict, string Why) EmptySpace(EmptySpaceFacts f)
    {
        string cap = $"its assembly passes {f.MaxMembers} references without reaching a floor or wall";
        if (!f.Interior) return (EmptySpaceVerdict.Refused, "exterior: the land under it is not read");
        if (!f.ExistsWinner) return (EmptySpaceVerdict.Absent, "not present under the winner (deleted, or disabled at runtime)");
        if (!f.ExistsAuthoring) return (EmptySpaceVerdict.Absent, "not present in the order without the mover's side");
        if (f.Motion == DependentMotion.Unknown && !f.Held) return (EmptySpaceVerdict.Refused, "what physics does with it at load is unknown (no record type or bodies read)");
        if (!f.Held) return (EmptySpaceVerdict.Falls, "a loose body that is neither mounted nor resting on a fixed reference: it falls at load (the rest rule's class)");
        if (!f.AuthoringRead)
            return f.WinnerGrounded && !f.WinnerOverCap && !f.RePlacedByMoverSide
                ? (EmptySpaceVerdict.Grounded, "a floor or wall holds it under the winner (the order without the mover was not walked: nothing there can make it the class)")
                : (EmptySpaceVerdict.Refused, "the order without the mover was not walked, and the winner arm alone cannot decide it");
        if (f.AuthoringOverCap) return (EmptySpaceVerdict.Refused, $"in the order without the mover {cap}: not read");
        if (!f.AuthoringGrounded)
        {
            if (f.WinnerOverCap) return (EmptySpaceVerdict.Refused, $"under the winner {cap}: not read");
            return f.WinnerGrounded ? (EmptySpaceVerdict.NoRelation, "no floor or wall held it in the order without the mover either")
                                    : (EmptySpaceVerdict.FloatsAsShipped, "no floor or wall holds it in either order: it floats as shipped (the absolute floor, not a clash)");
        }
        if (!f.AuthoringAnchorChangedByMover) return (EmptySpaceVerdict.NoRelation, "the floors and walls that held it are not ones the mover removed or moved");
        if (f.WinnerOverCap) return (EmptySpaceVerdict.Refused, $"under the winner {cap}: not read");
        if (f.RePlacedByMoverSide)
            return f.WinnerGrounded ? (EmptySpaceVerdict.RePlacedGrounded, "the mover's side re-placed it, and it stands on or against a floor or wall there")
                                    : (EmptySpaceVerdict.RePlacedStillEmpty, "the mover's side re-placed it, and nothing it touches reaches a floor or wall there");
        if (f.WinnerGrounded) return (EmptySpaceVerdict.ReGrounded, "the mover removed or moved what held it, and another floor or wall holds it now");
        if (f.BethesdaFloor) return (EmptySpaceVerdict.FloorPopulation, "left in empty space, with Bethesda or USSEP files on both sides (counted apart)");
        return (EmptySpaceVerdict.LeftInEmptySpace, "the mover removed or moved the floor or wall it stood on or against, and nothing it touches reaches a floor or wall now");
    }

    /// <summary>What physics does with a dependent at cell load — the split 4.7 Part C made necessary: a movable
    /// CLUTTER urn was shoved aside, so a movable dependent left in the air FALLS rather than floats, and its
    /// player-visible defect is "apples on the floor", self-healing in the common case. Item records (MISC, INGR, ALCH,
    /// BOOK, WEAP, ARMO, AMMO, SLGM, KEYM, SCRL, and a leveled item, which resolves to one) are movable by record type
    /// — the engine gives them a dynamic body whatever the NIF says. A Static, Furniture, Container, Door, Activator,
    /// Tree, Flora or Light is fixed by record type — the engine never lets physics move them. A MoveableStatic is the
    /// one type that defers to its NIF: movable when any solid body's motion system is a dynamic one
    /// (MO_SYS_DYNAMIC and the inertia/stabilized/thin-box variants), and — when the motion system could not be read —
    /// when a solid body sits on a layer the game reserves for things that move (CLUTTER, PROPS, DEBRIS, WEAPON);
    /// fixed when its bodies say FIXED, KEYFRAMED or INVALID on a static layer. Unknown when nothing is known about the
    /// base or its bodies. The record-type half is an ASSUMPTION about the engine, stated here once.</summary>
    public static DependentMotion ClassifyMotion(string? baseType, IEnumerable<(string Layer, string MotionSystem)>? solidBodies)
    {
        switch (baseType)
        {
            case "MiscItem" or "Ingredient" or "Ingestible" or "Book" or "Weapon" or "Armor" or "Ammunition"
                or "SoulGem" or "Key" or "Scroll" or "LeveledItem":
                return DependentMotion.Movable;
            case "Static" or "Furniture" or "Container" or "Door" or "Activator" or "Tree" or "Flora" or "Light":
                return DependentMotion.Fixed;
            case "MoveableStatic":
                if (solidBodies is null) return DependentMotion.Unknown;
                bool any = false, movable = false;
                foreach (var (layer, motion) in solidBodies)
                {
                    any = true;
                    if (motion is "MO_SYS_DYNAMIC" or "MO_SYS_SPHERE_INERTIA" or "MO_SYS_SPHERE_STABILIZED"
                               or "MO_SYS_BOX_INERTIA" or "MO_SYS_BOX_STABILIZED" or "MO_SYS_THIN_BOX") movable = true;
                    else if (string.IsNullOrEmpty(motion) && layer is "SKYL_CLUTTER" or "SKYL_PROPS" or "SKYL_DEBRIS_LARGE" or "SKYL_DEBRIS_SMALL" or "SKYL_WEAPON") movable = true;
                }
                return !any ? DependentMotion.Unknown : movable ? DependentMotion.Movable : DependentMotion.Fixed;
            default:
                return DependentMotion.Unknown;
        }
    }

    /// <summary>The route-CUT rule (roadmap 4.7's closing step, frozen 2026-09-02 from the handoff of 2026-09-06) — the
    /// rule Part C returned, over the facts <c>RouteConnectivity.Measure</c> now adds to the raster (<c>RouteFacts.Line</c>).
    /// Supersedes <see cref="RouteBlockage"/>, which asked whether ANY detour survives inside the window; Part C (56
    /// arms, two corridors) showed the engine does not look for that detour: a commanded walker stalls against a
    /// full-width obstacle 5 of 5, walks straight through whenever its own path line is clear by its ~22 u capsule
    /// 10 of 10, and with its line cut and a passage open beside the obstacle gets through 12 of 22 — 3 of 11 when the
    /// widest passage is 44 u or under, 7 of 11 at 64 u, 5 of 11 at 76–160 u. The pass rate does not rise with the
    /// passage: the avoidance shifts to one side by a bounded amount, sometimes not at all, never to the far side.
    /// <list type="bullet">
    /// <item><b>Blocked</b> — the object cuts the corridor's natural path line at capsule width inside the actor band,
    /// and the widest passage beside it is under <paramref name="blockedBelow"/> (48 u: measured 0 of 5 at full width
    /// and 3 of 11 at 44 u and under). The very-high class.</item>
    /// <item><b>LikelyBlocked</b> — the line is cut and a passage of <paramref name="blockedBelow"/> or more is open
    /// beside it: the coin (12 of 22 from 64 u up). 48–64 u was not measured; that band is assigned HERE, to the
    /// lower-severity class, on purpose — a claim of certainty where nothing was measured would cost precision.</item>
    /// <item><b>Clipped</b> — the object stands on walkable ground in the band (<paramref name="touched"/> &gt; 0) but
    /// does not cut the line: the walker passes it 10 of 10. Piranha's clipping class, kept apart from Blocked.</item>
    /// <item><b>NoLine</b> — on walkable ground, but the window has no path line to test (open ground, a dead end, a
    /// junction, an island, or the record's links disagree with the raster). Counted by reason; never read as clear —
    /// Part C measured corridors only, and this rule says nothing about anything else.</item>
    /// <item><b>None</b> — the object never enters an actor column over walkable ground in the window.</item>
    /// <item><b>Unjudgeable</b> — the window could not be measured; never read as clear.</item>
    /// </list>
    /// No NARROWED: Part C found no clear-gap threshold to set one at. Doors and MoveableStatics are set aside by the
    /// caller before measuring (passable by design; pushed aside — Part C's urn).
    /// <para>Two clauses qualify a cut without moving its class (the class is by passage; moving it on either is a design
    /// decision the measurements hand to Piranha): the <b>floor-bottom term</b> (Q26, from Q25's measurement) — a cut
    /// object whose lowest material along the cut stands <see cref="FloatWall"/> or more above the floor
    /// (<paramref name="cutFloat"/>) is a wall to the controller at any rise, and the why says so instead of naming the
    /// step band; else the <b>step band</b> (Q23, the top and the rates from Q25) — a rise (<paramref name="cutRise"/>)
    /// inside (<see cref="PhysicalStep"/>, <see cref="PhysicalStepBandTop"/>) is one a walking humanoid gets over by size
    /// and pace, and the why names it with the measured rates.</para></summary>
    public static (RouteCutVerdict Verdict, string Why) RouteCut(bool judged, int touched, bool lineFound, string lineWhy,
                                                                  bool cut, float passage, float blockedBelow = 48f, float cutRise = float.NaN,
                                                                  float cutFloat = float.NaN, bool cutOnStairs = false)
    {
        if (!judged) return (RouteCutVerdict.Unjudgeable, "the window could not be measured");
        if (touched <= 0) return (RouteCutVerdict.None, "the object never enters an actor column over walkable ground");
        if (!lineFound) return (RouteCutVerdict.NoLine, $"on walkable ground ({touched} cells), but the window has no path line to test: {lineWhy}");
        if (!cut) return (RouteCutVerdict.Clipped, $"stands on walkable ground ({touched} cells) beside the corridor's path line, not across it");
        if (cutOnStairs && cutRise < PhysicalStepBandTop && float.IsFinite(cutRise) && float.IsFinite(cutFloat) && cutFloat < FloatWall)
            return (RouteCutVerdict.None, $"stairs-material contact rises {cutRise:0.###} u above the feet, below {PhysicalStepBandTop:0} u: a step");
        string band = cutFloat >= FloatWall ? FloatWallClause(cutFloat)
            + (cutOnStairs ? "; the float rule is unchanged on stairs materials (unproven in game)" : "")
            : cutOnStairs ? "" : StepBandClause(cutRise);
        if (passage < blockedBelow)
            return (RouteCutVerdict.Blocked, $"cuts the path line at capsule width with {passage:0} u open beside it (under {blockedBelow:0} u){band}");
        return (RouteCutVerdict.LikelyBlocked, $"cuts the path line at capsule width with {passage:0} u open beside it — the engine takes such a passage about half the time{band}");
    }

    /// <summary>The actor band's bottom — the step height, above the navmesh. MEASURED 2026-09-05 (Q23 Part A, five
    /// fresh games, ~230 arms; Research/ACTOR-BAND-BOTTOM-2026-09-05.md): 48 u is the highest beam every walking
    /// humanoid from SetScale 0.7 to 1.3 and the child race got up in every repeat, so an object whose top is at or under
    /// it is never an obstacle to a humanoid. Was 32 u (assumed) from milestone 4c until 2026-09-05. This is the constant's
    /// one home (DetectorModel is compiled into every probe; ActorBand forwards to it).</summary>
    public const float StepHeight = 48f;

    /// <summary>nif.xml material names, supplied to the geometry sweep as data. The measured convex stairs effect is
    /// provisionally applied to compressed chunks and the other named stairs materials; those transfers are unproven.</summary>
    public static bool IsStairsMaterial(string? material) =>
        material?.StartsWith("SKY_HAV_MAT_STAIRS_", StringComparison.Ordinal) == true;

    /// <summary>The top of the step band ABOVE THE NAVMESH at the Ratway station — the record of Q23's measurement. Above
    /// <see cref="StepHeight"/> and up to here, whether a walking humanoid gets over the object depends on its size and
    /// pace (52 u: an adult every time, the child race 2 of 3, a 0.7 body 1 of 3; 55 u: an adult 2 of 8, scale 1.3 3 of 4,
    /// running 4 of 4; 60 u: only running, 1 of 4). The route-cut verdict names the band when the cut object's rise falls
    /// inside it; the class does not change on it (a class change is a design decision the measurement hands to Piranha).
    /// Q25 (2026-09-05, two more floors, one adult walking) found the coin runs to 74 u above the FEET — 63.6 above this
    /// station's navmesh, past the 60 the Ratway's five arms at 70.4 above the feet (0 of 5) had put it at — so the physical
    /// band's top is stated on its own (<see cref="PhysicalStepBandTop"/>), not as this plus the offset; this stays the
    /// navmesh-relative record for a caller measuring at the station.</summary>
    public const float StepBandTop = 60f;

    /// <summary>The measuring station's navmesh-to-floor offset. Every rung of the band was measured on the Ratway corridor,
    /// where the navmesh reads 47 u and the walker's feet 36.6 (Q23), so every navmesh-relative constant above is stated at
    /// this offset. MEASURED 2026-09-05 (Q24, Research/NAVMESH-FLOOR-OFFSET-2026-09-05.md): the records put the collision
    /// floor 10.0 u under the navmesh at the station and within 2 u of the feet along 718 recorded walker positions; over
    /// the vanilla cells the offset is centred near ZERO (interior median +1.1 u, cell medians within ±4 u of 0 for three
    /// quarters of interior cells and nine tenths of exterior ones) and varies WITHIN a cell by −9..+12 u around its own
    /// median — the station's 10 u is the high side of the distribution, and no per-cell number carries the texture. So
    /// the route rule reads the physical floor under each raster cell (FloorField) and measures the band above the
    /// FEET with the constants below.</summary>
    public const float StationOffset = 10.4f;

    /// <summary>The step above the walker's FEET: the highest rung every walking humanoid cleared every time (48 u above
    /// the navmesh) at the station's offset — 58.4 u. What the route rule's band bottom is measured from once it stands
    /// on the physical floor. The conservative sure line: Q25 (2026-09-05) measured the sure step at 64 u above the feet
    /// at two more stations (the Temple of the Divines at δ 0 and the Abandoned Prison at δ 26.4: 6 of 6 at 62–64; the
    /// Ratway's adult cleared 62.4 every time too), and 58.4 stays because the Ratway's 48 u was the rung EVERY walking
    /// humanoid cleared — the child race and the 0.7 body included — where Q25 walked one adult.</summary>
    public const float PhysicalStep = StepHeight + StationOffset;

    /// <summary>The step band's top above the feet: 76 u, EXCLUSIVE — the sure refusal. MEASURED 2026-09-05 (Q25,
    /// Research/FLOOR-OFFSET-IN-GAME-2026-09-05.md §3; two stations, beams on the floor, the top above the feet): sure to 64
    /// (temple 9 of 9, prison 6 of 6), stochastic from 65 to 74 (temple 7 of 18, prison 16 of 21 — the prison's coin runs
    /// hotter by about a rung, for a reason not found), refused at 76 (0 of 6, no climb, held 14–19 u short of the face).
    /// Nothing was measured between 74 and 76; the band's top is the refusal line, so a rise of 76 is outside it. Was 70.4
    /// (the Ratway's 60 u + the station's 10.4 u offset) from Q24 until Q26 folded this; the Ratway's 70.4 (0 of 5) was a
    /// low draw inside the coin.</summary>
    public const float PhysicalStepBandTop = 76f;

    /// <summary>What a floor field assumes under a raster cell with no floor within reach: the navmesh minus the
    /// station's offset, so an unread floor reproduces the band exactly as it was measured. Every such read is counted
    /// and the probes print the count.</summary>
    public const float FallbackOffset = StationOffset;

    /// <summary>The step-band clause of a route-cut why (Q23, 2026-09-05; above the feet since Q24 the same day; the top
    /// and the rates from Q25 since Q26): named when the cut object's rise above the floor at the cut lies inside
    /// (<paramref name="bottom"/>, <paramref name="top"/>) — open at both ends: the bottom is a rung every walker cleared,
    /// the top the sure refusal — the physical band (<see cref="PhysicalStep"/>, <see cref="PhysicalStepBandTop"/>) by
    /// default, because the route rule's rise is measured from the physical floor under the cut; a caller with a
    /// navmesh-relative rise at the station passes <see cref="StepHeight"/> and <see cref="StepBandTop"/>. The rates in
    /// the clause are Q25's, above the feet: up to 64 u every time (temple 9 of 9, prison 6 of 6), 65–74 u about a third
    /// to two thirds of the time (temple 7 of 18, prison 16 of 21), 76 u refused (0 of 6); Q23's size arms at 62 u
    /// (52 above the Ratway navmesh): the child race 2 of 3, a 0.7 body 1 of 3. The class is unchanged: naming the band
    /// is the "which actor" clause the handoff asked for; moving the class on it is a design decision the measurement
    /// hands to Piranha. Empty when no rise is known or the rise is outside the band; not consulted when the floor-bottom
    /// term applies (<see cref="FloatWallClause"/>).</summary>
    public static string StepBandClause(float cutRise, float bottom = PhysicalStep, float top = PhysicalStepBandTop) =>
        float.IsNaN(cutRise) || cutRise <= bottom || cutRise >= top ? "" :
        $"; the object rises {cutRise:0} u above the floor at the cut — inside the step band ({bottom:0}, {top:0}) u a walking humanoid gets over by size and pace (Q25, above the feet: up to 64 u every time, 65–74 u about a third to two thirds of the time — temple 7 of 18, prison 16 of 21 — 76 u refused; Q23 at 62 u: a child 2 of 3, a 0.7 body 1 of 3)";

    /// <summary>The floor-bottom term: a cut object whose lowest material within the capsule's footprint stands this far or
    /// more above the floor along the cut is a wall to the character controller whatever its rise. MEASURED 2026-09-05
    /// (Q25, Research/FLOOR-OFFSET-IN-GAME-2026-09-05.md §4; 47 arms at the Abandoned Prison, one adult walking): with the
    /// beam's top fixed at 62 u above the feet — a rung the adult clears every time on the floor — its underside lifted 0–14 u
    /// was stepped 22 of 22, 16 u a coin (5 of 8, two beam sizes), 22, 26 and 32 u refused with no climb (0 of 14); a
    /// fixed-size 36 u beam lifted whole was stepped at 0 / 8 / 16 and refused at 26.4 — the float alone does it (the
    /// controller's lower hemisphere ends 27 u up; a contact that high is a wall to it, not a step). The transition lies in
    /// (16, 22] u — nothing was measured between — and 20 is its middle, inclusive. Until Q26 the route rule measured an
    /// object's rise above the floor and never asked where its bottom was: at the prison a ladder of beams floating 28 u
    /// stopped the walker at every rung it climbs on the floor.</summary>
    public const float FloatWall = 20f;

    /// <summary>The floor-bottom clause of a route-cut why: named when the cut object's lowest material along the cut stands
    /// <see cref="FloatWall"/> or more above the floor. It REPLACES the step-band clause — the step band and its coin were
    /// measured for objects standing on the floor and do not apply to one the controller meets above its lower hemisphere.
    /// The class is unchanged (by passage, as for every cut), as the band leaves it. Empty under the term or when no float
    /// is known.</summary>
    public static string FloatWallClause(float cutFloat) =>
        float.IsNaN(cutFloat) || cutFloat < FloatWall ? "" :
        $"; the object's lowest surface floats {cutFloat:0} u above the floor at the cut — a wall to the controller whatever its rise (Q25: a beam lifted 0–14 u above the feet is stepped every time, 16 u a coin, 22 u and more refused with no climb; the step-band clause does not apply)";

    /// <summary>The route-blockage rule (roadmap 4.7 Part B, frozen 2026-09-02 from the handoff of 2026-09-05).
    /// <b>SUPERSEDED the same day by <see cref="RouteCut"/></b> — kept callable so the re-pricing beside Part B's numbers
    /// is like for like in one pass, and so its cases keep pinning the ladder the raster still reports; the drivers no
    /// longer rank on it, and it can go once the comparison is no longer wanted.
    /// Replaces milestone 4's "any severed navmesh triangle" with a route-level decision over the facts
    /// <c>RouteConnectivity.Measure</c> returns for one object in a window of walkable ground:
    /// <list type="bullet">
    /// <item><b>Blocked</b> — the object disconnects ground that was connected without it, even at zero clearance
    /// (<paramref name="maxIntactRung"/> &lt; 0). The canonical failure: a wall across the route, the NPC pressing into it.</item>
    /// <item><b>Narrowed</b> — a route survives, but the widest ladder rung it survives at is below
    /// <paramref name="threshold"/> AND below what the corridor allowed without the object. The second condition is the
    /// with-vs-without arm: a corridor that was already that narrow is not the object's doing.</item>
    /// <item><b>Clipped</b> — the object stands in an actor column over walkable ground (<paramref name="touched"/> &gt; 0)
    /// but every route survives at the threshold. Piranha's clipping class: reported, ranked, kept apart from Blocked.</item>
    /// <item><b>None</b> — the object never enters an actor column over walkable ground in the window.</item>
    /// <item><b>Unjudgeable</b> — the window could not be measured; never read as clear.</item>
    /// </list>
    /// The threshold is Part C's number — the minimum clear gap an NPC needs, measured in game — and is a PARAMETER
    /// here; the rungs are the ladder the measurement ran (rung 0 is clearance 0).</summary>
    public static (RouteVerdict Verdict, string Why) RouteBlockage(bool judged, int touched, int maxIntactRung,
                                                                    int maxIntactRungWithout, float[] ladder, float threshold)
    {
        if (!judged) return (RouteVerdict.Unjudgeable, "the window could not be measured");
        if (touched <= 0) return (RouteVerdict.None, "the object never enters an actor column over walkable ground");
        if (maxIntactRung < 0) return (RouteVerdict.Blocked, "disconnects ground that was connected without it, at zero clearance");
        float with = ladder[Math.Min(maxIntactRung, ladder.Length - 1)];
        float without = maxIntactRungWithout < 0 ? 0f : ladder[Math.Min(maxIntactRungWithout, ladder.Length - 1)];
        if (with < threshold && with < without)
            return (RouteVerdict.Narrowed, $"a route survives but only {with:0} u clear past the object, {without:0} u without it, threshold {threshold:0} u");
        return (RouteVerdict.Clipped, $"overlaps walkable ground ({touched} cells) but every route survives at {MathF.Min(with, without):0} u clearance");
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

    // ---- the differential credit rule (2026-09-03; handoff 2026-09-12, board Q9) ----------------------------

    /// <summary>What a row's presence or absence across the two arms of a differential is WORTH. Every class the chain
    /// diffs has a physical half and a provenance half; a sham patch that masters both plugins and re-states the
    /// winner's record flips only the provenance half, and on the lab (2026-09-03) three detectors let four such
    /// cells read DIFFERENTIAL-VERIFIED because the diff credited absence. Credit is therefore given only when the
    /// PHYSICAL predicate — the class's own physical half, read under each arm (<see cref="PhysicalState"/>) — went
    /// Holds -> Gone. A row that left the class with its predicate still holding, or that never carried a physical
    /// defect (base predicate Gone), or that ENTERED the class with a predicate reading Gone, changed class
    /// membership without a physical flip: RECLASSIFIED. A row that entered with the predicate holding is CREATED
    /// (stated limit: the base arm measures no predicate for rows outside its class, so a defect that pre-dated the
    /// change and merely became attributed to it reads Created too). Any predicate the outcome depends on that is
    /// Unmeasured — or null, an emit older than the field — is UNMEASURED: counted apart, credited neither way.
    /// A row in both arms is UNCHANGED whatever its predicates did (a flip is reported beside, never credited).</summary>
    public static CreditOutcome Credit(bool inBase, PhysicalState? basePhysical, bool inChanged, PhysicalState? changedPhysical)
    {
        if (inBase && inChanged) return CreditOutcome.Unchanged;
        if (inBase)
        {
            if (basePhysical is null or PhysicalState.Unmeasured) return CreditOutcome.Unmeasured;
            if (basePhysical == PhysicalState.Gone) return CreditOutcome.Reclassified;        // nothing physical to repair
            if (changedPhysical is null or PhysicalState.Unmeasured) return CreditOutcome.Unmeasured;
            return changedPhysical == PhysicalState.Gone ? CreditOutcome.Repaired : CreditOutcome.Reclassified;
        }
        if (inChanged)
        {
            if (changedPhysical is null or PhysicalState.Unmeasured) return CreditOutcome.Unmeasured;
            return changedPhysical == PhysicalState.Holds ? CreditOutcome.Created : CreditOutcome.Reclassified;
        }
        return CreditOutcome.Absent;
    }

    /// <summary>The verdict line, from the credited counts. Coverage refusals (<see cref="CoverageRefusals"/>) outrank
    /// everything: a change that made rows unreadable is not judged. Then repaired against created decides VERIFIED /
    /// NEUTRAL / HARMFUL as before — but only physical repairs and physical creations count. With neither, a run that
    /// only moved rows between classes is RECLASSIFIED (every sham must read INERT or this), one that only moved
    /// unmeasured rows is UNMEASURED, and one that touched nothing is INERT.</summary>
    public static string DifferentialVerdict(int repaired, int created, int reclassified, int unmeasured, int coverageRefusals)
    {
        if (coverageRefusals > 0) return "DIFFERENTIAL-UNJUDGED";
        if (repaired > 0 || created > 0)
            return repaired > created ? "DIFFERENTIAL-VERIFIED" : repaired == created ? "DIFFERENTIAL-NEUTRAL" : "DIFFERENTIAL-HARMFUL";
        if (reclassified > 0) return "DIFFERENTIAL-RECLASSIFIED";
        if (unmeasured > 0) return "DIFFERENTIAL-UNMEASURED";
        return "DIFFERENTIAL-INERT";
    }

    /// <summary>Which populations the changed arm judged WORSE than the base arm: for every population name present in
    /// both, the unjudgeable fraction (unjudgeable / (judged + unjudgeable)) rose by more than <paramref name="tolerance"/>
    /// (a fraction, 0.01 = one percentage point). A population with nothing in it on either side has fraction 0. Returns
    /// one line per refusal, naming the population and both fractions; empty when nothing rose.</summary>
    public static List<string> CoverageRefusals(
        IEnumerable<(string Population, long Judged, long Unjudgeable)> baseCoverage,
        IEnumerable<(string Population, long Judged, long Unjudgeable)> changedCoverage,
        double tolerance)
    {
        static double Frac(long judged, long unjudgeable) => judged + unjudgeable == 0 ? 0 : (double)unjudgeable / (judged + unjudgeable);
        var basis = new Dictionary<string, (long J, long U)>(StringComparer.OrdinalIgnoreCase);
        foreach (var (pop, j, u) in baseCoverage) basis[pop] = (j, u);
        var refusals = new List<string>();
        foreach (var (pop, j, u) in changedCoverage)
        {
            if (!basis.TryGetValue(pop, out var b)) continue;
            double before = Frac(b.J, b.U), after = Frac(j, u);
            if (after - before > tolerance)
                refusals.Add($"{pop}: unjudgeable {before:P2} -> {after:P2} ({b.U:N0} of {b.J + b.U:N0} -> {u:N0} of {j + u:N0}) — rose by more than {tolerance:P2}");
        }
        return refusals;
    }

    // -----------------------------------------------------------------------------------
    // CELL WATER (roadmap 4.9, Part A — 2026-09-04). Special Edition's Update.esm gave exterior cells a
    // water tuple Oldrim never had (the WATR link, the flow velocity, the noise and env-map paths);
    // a later override that wins a CELL for some unrelated reason carries whatever water it was saved
    // with. Per cell: the TRUTH (the last Bethesda-or-USSEP contributor's tuple), the FIRST tuple (the
    // earliest contributor's — the pre-Update state on a vanilla cell), an optional second truth from a
    // WATER MOD (a plugin the caller found retyping WATR records or setting its own WATR on many cells),
    // and the WINNER. The verdict names how the winner differs from the truth. The seam rule and the
    // missing-water rule read the resolved surface (cell height, else the worldspace default), which the
    // caller computes with the engine's precedence (viewprobe's RealHeight rule, restated in HeightKind).
    // -----------------------------------------------------------------------------------

    /// <summary>The engine's "not set" marker is more than one value (viewprobe, 2026-08-27): the documented
    /// 3.4028235E+38, an unsigned reinterpretation near 4.29e9, a negative one near −2.15e9. Anything outside
    /// Skyrim's ±60,000 u world is a sentinel, never a height; the name only tells the reader which.</summary>
    public static WaterHeightKind HeightKind(float? h)
    {
        if (h is not { } v) return WaterHeightKind.Unset;
        if (float.IsNaN(v) || float.IsInfinity(v)) return WaterHeightKind.OtherSentinel;
        if (MathF.Abs(v) < 100_000f) return WaterHeightKind.Real;
        if (v == float.MaxValue) return WaterHeightKind.Unset;
        if (v > 4.0e9f && v < 4.3e9f) return WaterHeightKind.UnsignedWrapped;
        if (v < -2.0e9f && v > -2.2e9f) return WaterHeightKind.NegativeWrapped;
        return WaterHeightKind.OtherSentinel;
    }

    /// <summary>A cell's water fields as one comparable value. <paramref name="Water"/> is the WATR FormKey as a string
    /// (null = no link); velocity as two vectors (null = the XWCU subrecord absent); paths null when absent.</summary>
    public readonly record struct WaterTuple(bool HasWater, bool NoLodWater, string? Water, float? Height,
                                             Vector3? VelocityAngle, Vector3? VelocityOffset, string? Noise, string? EnvMap)
    {
        public static readonly WaterTuple None = new(false, false, null, null, null, null, null, null);
    }

    /// <summary>The fields on which two tuples differ, by name, in a fixed order. Empty = equal. Heights compare by
    /// kind (two sentinels are equal; two reals within 0.01 u); vectors within 0.01; paths ordinal-ignore-case with
    /// null and empty equal.</summary>
    public static List<string> WaterDiff(WaterTuple a, WaterTuple b)
    {
        var d = new List<string>();
        if (a.HasWater != b.HasWater) d.Add("HasWater");
        if (a.NoLodWater != b.NoLodWater) d.Add("NoLodWater");
        if (!string.Equals(a.Water ?? "", b.Water ?? "", StringComparison.OrdinalIgnoreCase)) d.Add("Water");
        if (!HeightsEqual(a.Height, b.Height)) d.Add("Height");
        if (!VecEqual(a.VelocityAngle, b.VelocityAngle) || !VecEqual(a.VelocityOffset, b.VelocityOffset)) d.Add("Velocity");
        if (!string.Equals(a.Noise ?? "", b.Noise ?? "", StringComparison.OrdinalIgnoreCase)) d.Add("Noise");
        if (!string.Equals(a.EnvMap ?? "", b.EnvMap ?? "", StringComparison.OrdinalIgnoreCase)) d.Add("EnvMap");
        return d;
    }

    /// <summary>The height as the ENGINE reads it, measured in game 2026-09-04 (Part C, the sentinel stations): the value
    /// behaves like a SATURATING float→int32 conversion — +3.4028235E+38 and 4.2949532E+09 both land on int-max, which is
    /// "use the worldspace default" (a swimmer in Winterhold's 4.29e9 cell sits at the same depth as in the Unset cell
    /// beside it); −2.1474836E+09 (int-min exactly) and −3.4028235E+38 land on int-min, which is NO surface (the player
    /// stood on the floor of Lux's warehouse cell in vanilla, and `Cell.GetActualWaterLevel` read −2,147,483,648 there).
    /// Null = no surface.</summary>
    public static float? SurfaceHeight(float? h, float? worldDefault) => HeightKind(h) switch
    {
        WaterHeightKind.Real => h,
        WaterHeightKind.Unset => HeightKind(worldDefault) == WaterHeightKind.Real ? worldDefault : null,
        WaterHeightKind.UnsignedWrapped => HeightKind(worldDefault) == WaterHeightKind.Real ? worldDefault : null,
        _ => null,
    };

    /// <summary>Does the height put a surface somewhere (the engine's reading)? Unset counts — it defers to the default.</summary>
    public static bool HasSurface(float? h) => HeightKind(h) is WaterHeightKind.Real or WaterHeightKind.Unset or WaterHeightKind.UnsignedWrapped;

    public static bool HeightsEqual(float? a, float? b)
    {
        var ka = HeightKind(a); var kb = HeightKind(b);
        // Two statements are equal when the engine would place the same surface: two reals within 0.01 u, two "use
        // the default" (Unset or the wrapped 4.29e9 — both saturate to int-max), or two "no surface" (the negatives).
        bool da = ka is WaterHeightKind.Unset or WaterHeightKind.UnsignedWrapped, db = kb is WaterHeightKind.Unset or WaterHeightKind.UnsignedWrapped;
        if (da || db) return da && db;
        if (!HasSurface(a) || !HasSurface(b)) return !HasSurface(a) && !HasSurface(b);
        return MathF.Abs(a!.Value - b!.Value) <= 0.01f;
    }

    static bool VecEqual(Vector3? a, Vector3? b)
    {
        if (a is null && b is null) return true;
        if (a is null || b is null) return false;
        return (a.Value - b.Value).Length() <= 0.01f;
    }

    /// <summary>What the caller knows about one cell before the verdict. <paramref name="Truth"/> is the last
    /// Bethesda-or-USSEP tuple (or the definer's, when no such plugin ever stated the cell — <paramref name="AuthorTruth"/>);
    /// <paramref name="First"/> the earliest contributor's; <paramref name="WaterModTruth"/> the last water-mod
    /// contributor's, when one exists; <paramref name="Winner"/> the load-order winner's.</summary>
    // ---- world state (roadmap 4.11): a row's standing across a switch flip ------------------------------------

    /// <summary>The state-differential decision (frozen 2026-09-04, handoff 2026-09-20 Part C). A detector row is
    /// judged in the BASELINE (the load-time state, EnableState over the winner) and in a STATE (the same order with
    /// one switch, or one quest stage's set of switches, flipped — WorldState.StateArm). A row present in the state
    /// and absent in the baseline is <b>StateConditional</b>: the conflict exists only after the flip, attributed to
    /// the switch. A row present in the baseline and gone in the state is <b>StateResolved</b>: reported, never a
    /// finding — the flip removes it. Present in both is Unchanged (the ordinary detector's row, not this class's);
    /// absent in both is Absent (nothing to say). The credit rule reads the same two arms: a patch that re-parents a
    /// reference onto the right marker reads as repaired only when the row is gone in THAT state.</summary>
    public static (StateVerdict Verdict, string Why) StateDifferential(bool inBaseline, bool inState) => (inBaseline, inState) switch
    {
        (false, true) => (StateVerdict.StateConditional, "present once the switch is flipped, absent at load time"),
        (true, false) => (StateVerdict.StateResolved, "present at load time, gone once the switch is flipped"),
        (true, true) => (StateVerdict.Unchanged, "present in both states — the ordinary detector's row"),
        _ => (StateVerdict.Absent, "present in neither state"),
    };

    /// <summary>The rule-12 split for a state row, by the SET MEMBER's placer (the reference the flip brought in or took
    /// out) and the OTHER side's placer: Bethesda-on-Bethesda is the floor (a vanilla set against vanilla geometry — the
    /// sieges are full of it); a mod's reference against a vanilla switch's set; a mod's own set member (whether the mod
    /// hung it on its own switch or on a vanilla one — the switch owner rides beside the row) against vanilla geometry;
    /// mod against mod. USSEP on either side is its own row, as the overlap split keeps it.</summary>
    public static StatePopulation ClassifyStatePopulation(bool memberIsVanilla, bool otherIsVanilla, bool memberIsUssep, bool otherIsUssep)
        => memberIsVanilla && otherIsVanilla ? StatePopulation.BethesdaFloor
         : memberIsUssep || otherIsUssep ? StatePopulation.UssepInvolved
         : memberIsVanilla ? StatePopulation.ModRefUnderVanillaSet
         : otherIsVanilla ? StatePopulation.ModSetOverVanilla
         : StatePopulation.ModOnMod;

    public readonly record struct WaterFacts(WaterTuple Truth, WaterTuple First, WaterTuple? WaterModTruth, WaterTuple Winner,
                                             PluginKind WinnerKind, bool AuthorTruth, bool DuplicateGridPoint);

    /// <summary>The verdict, winner against truth, first clause that applies.</summary>
    public static (WaterVerdict Verdict, WaterBlocker Reason, string Why) ClassifyWater(WaterFacts f)
    {
        if (f.DuplicateGridPoint)
            return (WaterVerdict.Unjudgeable, WaterBlocker.DuplicateCell, "more than one CELL record at this grid point — which the engine reads is not known from the records");
        if (f.WinnerKind == PluginKind.Generated)
            return (WaterVerdict.Unjudgeable, WaterBlocker.GeneratedWinner, "the winner is a generated plugin; its tuple is whatever its tool was run against");
        var dt = WaterDiff(f.Winner, f.Truth);
        if (dt.Count == 0) return (WaterVerdict.Forwarded, WaterBlocker.None, f.AuthorTruth ? "equals the definer's tuple" : "equals the truth tuple");
        // No water in either arm: the other fields are dormant (the CK re-saves an interior's height as 0 where the
        // sentinel stood, a link nobody reads), and calling that Dropped would name a surface that never existed.
        if (!f.Truth.HasWater && !f.Winner.HasWater)
            return (WaterVerdict.Inert, WaterBlocker.None, $"no water in either arm; the dormant fields differ in {string.Join(", ", dt)}");
        if (f.WaterModTruth is { } wm && WaterDiff(f.Winner, wm).Count == 0)
            return (WaterVerdict.Forwarded, WaterBlocker.None, "equals the water mod's tuple (the second truth)");
        if (WaterDiff(f.Winner, f.First).Count == 0)
            return (WaterVerdict.Reverted, WaterBlocker.None, $"equals the earliest contributor's tuple; differs from the truth in {string.Join(", ", dt)}");
        bool hasCleared = f.Truth.HasWater && !f.Winner.HasWater;
        bool linkNulled = f.Truth.Water is { Length: > 0 } && f.Winner.Water is null or { Length: 0 };
        // The surface removed: a height the engine places a surface at (real, the default, or a wrapped literal)
        // replaced by one it does not (int-min). Measured 2026-09-04; the earlier "kind flip" clause called a stated
        // height for the default a drop, and the reverse — neither removes a surface.
        bool surfaceGone = HasSurface(f.Truth.Height) && !HasSurface(f.Winner.Height);
        if (hasCleared || linkNulled || surfaceGone)
        {
            var parts = new List<string>();
            if (hasCleared) parts.Add("HasWater cleared");
            if (linkNulled) parts.Add("the WATR link nulled");
            if (surfaceGone) parts.Add($"the surface removed ({HeightKind(f.Truth.Height)} {f.Truth.Height:0.#} replaced by {HeightKind(f.Winner.Height)}, which the engine reads as no surface)");
            return (WaterVerdict.Dropped, WaterBlocker.None, string.Join("; ", parts));
        }
        if (dt.Contains("Water") && f.Truth.Water is { Length: > 0 } && f.Winner.Water is { Length: > 0 })
            return (WaterVerdict.Retyped, WaterBlocker.None, $"a different WATR ({f.Winner.Water} for {f.Truth.Water})"
                                                             + (dt.Count > 1 ? $"; also {string.Join(", ", dt.Where(x => x != "Water"))}" : ""));
        if (!HasSurface(f.Truth.Height) && HasSurface(f.Winner.Height))
            return (WaterVerdict.Altered, WaterBlocker.None, $"a surface where the truth had none ({HeightKind(f.Truth.Height)} replaced by {HeightKind(f.Winner.Height)} {f.Winner.Height:0.#})"
                                                             + (dt.Count > 1 ? $"; also {string.Join(", ", dt.Where(x => x != "Height"))}" : ""));
        return (WaterVerdict.Altered, WaterBlocker.None, $"differs from the truth in {string.Join(", ", dt)}");
    }

    /// <summary>Rule 12's split by who wins the record.</summary>
    public static WaterPopulation ClassifyWaterPopulation(PluginKind winner) => winner switch
    {
        PluginKind.Bethesda => WaterPopulation.BethesdaFloor,
        PluginKind.Ussep => WaterPopulation.Ussep,
        PluginKind.WaterMod => WaterPopulation.WaterMod,
        _ => WaterPopulation.Other,
    };

    /// <summary>One side of a cell pair, RESOLVED through its worldspace: <paramref name="Has"/> the HasWater flag;
    /// <paramref name="Water"/> the cell's WATR else the worldspace's (null = neither); <paramref name="Z"/> the cell's real
    /// height else the worldspace default when real (null = no surface height anywhere).</summary>
    public readonly record struct ResolvedWater(bool Has, string? Water, float? Z);

    /// <summary>A pair of edge-adjacent cells under one arm is JOINED when both have water, the same resolved WATR and
    /// heights within <paramref name="eps"/>. A pair with no surface height on a watered side cannot be judged joined.</summary>
    public static bool WaterJoined(ResolvedWater a, ResolvedWater b, float eps)
        => a.Has && b.Has && a.Z is { } za && b.Z is { } zb && MathF.Abs(za - zb) <= eps
           && string.Equals(a.Water ?? "", b.Water ?? "", StringComparison.OrdinalIgnoreCase);

    /// <summary>The seam rule over a pair, truth arm against winner arm.</summary>
    public static (WaterSeamVerdict Verdict, string Why) WaterSeam(ResolvedWater aTruth, ResolvedWater bTruth,
                                                                   ResolvedWater aWin, ResolvedWater bWin,
                                                                   WaterVerdict aVerdict, WaterVerdict bVerdict, float eps)
    {
        if (!aWin.Has && !bWin.Has && !aTruth.Has && !bTruth.Has) return (WaterSeamVerdict.None, "neither side has water in either arm");
        bool tj = WaterJoined(aTruth, bTruth, eps), wj = WaterJoined(aWin, bWin, eps);
        if (tj && wj) return (WaterSeamVerdict.None, "joined in both arms");
        if (!tj && !wj)
        {
            // Bethesda ships this pair apart. Whether the winner's disagreement is the same one is stated.
            return (WaterSeamVerdict.Inherited, $"apart in both arms (truth {Describe(aTruth)} | {Describe(bTruth)}; winner {Describe(aWin)} | {Describe(bWin)})");
        }
        if (!tj && wj) return (WaterSeamVerdict.Joined, $"apart under the truth ({Describe(aTruth)} | {Describe(bTruth)}), joined under the winner ({Describe(aWin)})");
        // truth joined, winner not
        string attr = aVerdict != WaterVerdict.Forwarded && bVerdict != WaterVerdict.Forwarded ? "both sides"
                    : aVerdict != WaterVerdict.Forwarded ? "side A"
                    : bVerdict != WaterVerdict.Forwarded ? "side B" : "the worldspace record (both cells forward)";
        return (WaterSeamVerdict.Seam, $"joined under the truth ({Describe(aTruth)}), apart under the winner ({Describe(aWin)} | {Describe(bWin)}) — attributed to {attr}");

        static string Describe(ResolvedWater r) => !r.Has ? "no water" : $"{r.Water ?? "(no WATR)"} z {(r.Z is { } z ? z.ToString("0.#") : "?")}";
    }

    /// <summary>The missing-water candidate rule. <paramref name="truthVisible"/> / <paramref name="winnerVisible"/> are the
    /// share of the cell's LAND vertices that lie BELOW the resolved surface under each arm (null = no LAND to measure
    /// against). A surface under the ground everywhere is invisible, which is what a player calls "no water".</summary>
    public static (bool Candidate, PhysicalState Physical, string Why) MissingWater(WaterVerdict verdict, bool truthHas, float? truthVisible,
                                                                                    bool winnerHas, float? winnerVisible, float minShare)
    {
        if (verdict == WaterVerdict.Forwarded) return (false, PhysicalState.Gone, "the winner forwards the truth");
        if (verdict == WaterVerdict.Unjudgeable) return (false, PhysicalState.Unmeasured, "unjudgeable cell");
        if (!truthHas) return (false, PhysicalState.Gone, "the truth has no water here — nothing to lose");
        if (truthVisible is { } tv && tv < minShare)
            return (false, PhysicalState.Gone, $"the truth's own surface is under the ground ({tv:P0} of the cell below it) — the same-defect control, never a candidate");
        if (!winnerHas) return (true, PhysicalState.Holds, "the winner has no water at all where the truth's surface showed");
        if (truthVisible is null) return (false, PhysicalState.Unmeasured, "no LAND to measure the surface against");
        if (winnerVisible is null) return (false, PhysicalState.Unmeasured, "no LAND under the winner to measure the surface against");
        if (winnerVisible.Value < minShare)
            return (true, PhysicalState.Holds, $"the winner's surface stands above the LAND over {winnerVisible:P0} of the cell (truth {truthVisible:P0}; the floor is {minShare:P0})");
        return (false, PhysicalState.Gone, $"the winner's surface still shows over {winnerVisible:P0} of the cell (truth {truthVisible:P0})");
    }

    // -----------------------------------------------------------------------------------
    // LANDSCAPE SEAMS (roadmap 4.9, Part B — 2026-09-04). The engine draws each cell's LAND on its own;
    // two adjacent cells whose shared-edge vertices disagree show a step or a gap. Heights come from
    // HousecarlCore.LandHeight; this is the rule over the three deltas the index measures.
    // -----------------------------------------------------------------------------------

    /// <summary>Max |Δ| and the count over <paramref name="eps"/> between two 33-vertex edges.</summary>
    public static (float MaxDelta, int OverEps) EdgeMismatch(IReadOnlyList<float> a, IReadOnlyList<float> b, float eps)
    {
        if (a.Count != b.Count) throw new ArgumentException("edges of different length");
        float max = 0f; int over = 0;
        for (int i = 0; i < a.Count; i++)
        {
            float d = MathF.Abs(a[i] - b[i]);
            if (d > max) max = d;
            if (d > eps) over++;
        }
        return (max, over);
    }

    /// <summary>The three deltas of a pair under the LATER winner's view. <paramref name="WinDelta"/>: the two winners.
    /// <paramref name="ViewDelta"/>: later's own cell against the other side as later saw it (the last snap by later or a
    /// plugin in its master closure). <paramref name="PrevDelta"/>: the two sides as later's masters left them, before
    /// later's own edit — null when later defined the cell or its masters never stated the other. <paramref name="OtherSeen"/>:
    /// the other side's winner is later or in later's closure (then ViewDelta == WinDelta by construction).</summary>
    public readonly record struct LandSeamFacts(float WinDelta, float ViewDelta, float? PrevDelta, bool OtherSeen, bool Judged = true);

    public static (LandSeamVerdict Verdict, SeamOrigin? Origin, SeamSight? Sight, string Why) LandSeam(LandSeamFacts f, float eps)
    {
        if (!f.Judged) return (LandSeamVerdict.Unjudgeable, null, null, "a side's heightmap could not be decoded");
        if (f.WinDelta <= eps) return (LandSeamVerdict.None, null, null, $"the winners agree within {eps:0.#} u (Δ {f.WinDelta:0.#})");
        if (f.ViewDelta <= eps)
            return (LandSeamVerdict.Seam, SeamOrigin.Introduced, SeamSight.Blind,
                    $"each side matches what it saw (Δ {f.ViewDelta:0.#} under the later plugin's view); together they differ by {f.WinDelta:0.#} u"
                    + (f.OtherSeen ? " — yet the other side's winner is in its closure (a view/winner disagreement the caller should count)" : ""));
        if (f.PrevDelta is { } pd && pd > eps)
            return (LandSeamVerdict.Seam, SeamOrigin.Inherited, SeamSight.Seen,
                    $"the seam pre-dates the later plugin's edit (Δ {pd:0.#} before it, {f.ViewDelta:0.#} under its view, {f.WinDelta:0.#} now)");
        return (LandSeamVerdict.Seam, SeamOrigin.Introduced, SeamSight.Seen,
                $"the later plugin broke a matching edge it could see (Δ {(f.PrevDelta is { } p ? p.ToString("0.#") + " before it" : "n/a: its masters state no LAND on its side, so the LAND is new here")}, {f.ViewDelta:0.#} under its view, {f.WinDelta:0.#} now)");
    }

    /// <summary>Rule 12's split for a pair: who the later winner is.</summary>
    public static WaterPopulation ClassifyLandSeamPopulation(PluginKind later) => ClassifyWaterPopulation(later);
}

/// <summary>Who a plugin is, for the truth rule. Bethesda and USSEP are filenames; Generated is the fixed filenames
/// the retired <c>tool-output-awareness</c> skill listed; WaterMod is a rule over the plugin's records (see CellBoundaryIndex); Other is
/// everything else.</summary>
public enum PluginKind { Bethesda, Ussep, Generated, WaterMod, Other }

/// <summary>How a CELL.WaterHeight value reads (viewprobe's RealHeight rule with the sentinels named).</summary>
public enum WaterHeightKind { Real, Unset, UnsignedWrapped, NegativeWrapped, OtherSentinel }

/// <summary>Winner against truth, per cell. Inert: neither arm has water, so the differing fields are dormant. Altered: a
/// difference outside the named clauses (a real height moved, a velocity or path changed, water added where the truth
/// had none).</summary>
public enum WaterVerdict { Forwarded, Inert, Reverted, Dropped, Retyped, Altered, Unjudgeable }
public enum WaterBlocker { None, DuplicateCell, GeneratedWinner, UnreadablePlugin }
public enum WaterPopulation { BethesdaFloor, Ussep, WaterMod, Other }

/// <summary>Per edge-adjacent pair. Seam: joined under the truth, apart under the winner. Inherited: apart in both
/// (Bethesda ships it). Joined: apart under the truth, joined under the winner (a fixer's shape).</summary>
public enum WaterSeamVerdict { None, Seam, Inherited, Joined }

public enum LandSeamVerdict { None, Seam, Unjudgeable }
public enum SeamOrigin { Introduced, Inherited }
/// <summary>Blind: under the later plugin's own view the edge matched — the seam exists only in the combination.</summary>
public enum SeamSight { Seen, Blind }

/// <summary>The class's PHYSICAL half under one arm: it holds, it is gone, or the arm could not measure it. What the
/// predicate IS per detector: a clobber — victim-only ground remains (CoverageLoss samples beyond the reach clearance);
/// a dangling link — the index still exceeds the winning count (exact); an overlap — the two winning boxes still meet at
/// the run's threshold; a contact row — the dependent still touches nothing within ε. Never a stricter or looser test
/// than the class itself.</summary>
public enum PhysicalState { Holds, Gone, Unmeasured }

/// <summary>A row's standing across a switch flip (<see cref="DetectorModel.StateDifferential"/>). StateConditional is
/// the class; StateResolved is reported, not a finding.</summary>
public enum StateVerdict { StateConditional, StateResolved, Unchanged, Absent }

/// <summary>The rule-12 split for a state row (<see cref="DetectorModel.ClassifyStatePopulation"/>).</summary>
public enum StatePopulation { BethesdaFloor, UssepInvolved, ModRefUnderVanillaSet, ModSetOverVanilla, ModOnMod }

/// <summary>What a row's movement between two emits is credited as (<see cref="DetectorModel.Credit"/>).</summary>
public enum CreditOutcome { Unchanged, Repaired, Reclassified, Created, Unmeasured, Absent }


public enum ClobberPopulation { BethesdaFloor, UssepDiscarded, ModOnMod }

/// <summary>The route-cut classes (roadmap 4.7, the rule Part C returned). Blocked is the very-high class;
/// LikelyBlocked is the coin (cut line, passage open — the engine gets past about half the time); Clipped is the
/// clipping class, never merged with either; NoLine is counted by reason and never cleared.</summary>
public enum RouteCutVerdict { None, Clipped, LikelyBlocked, Blocked, NoLine, Unjudgeable }

/// <summary>The route-blockage classes (roadmap 4.7 Part B — superseded by <see cref="RouteCutVerdict"/>; kept for the
/// like-for-like comparison). Two severities by design: Blocked is the very-high class (no viable pathing left);
/// Narrowed and Clipped are the clipping classes, never merged with it.</summary>
public enum RouteVerdict { None, Clipped, Narrowed, Blocked, Unjudgeable }

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

/// <summary>The three witness sources, strongest first. Counted separately — W3 is partial and
/// mostly W1 by another route, so it is never merged into W1.</summary>
public enum WitnessKind { PackageAnchor, PlacedActor, TargetedRef }

/// <summary>What <see cref="DetectorModel.NeedsWitness"/> makes of one witness. Stranded is the
/// finding-shaped verdict; Mirror is its symmetric non-finding; Covered and Uncovered are nothing;
/// Unjudgeable is counted by its <see cref="WitnessBlocker"/> and never cleared.</summary>
public enum WitnessVerdict { Stranded, Mirror, Covered, Uncovered, Unjudgeable }

/// <summary>Why a witness could not be called. The first four are I/O facts the driver supplies;
/// the last two the rule finds in the numbers.</summary>
public enum WitnessBlocker { None, UnreadableAnchor, Templated, LeveledBase, PackageMissing, GeometryMissing, WithinResolutionBand }

/// <summary>Did the winner's side know about the witness? Deliberate is informational; Oversight
/// on a Stranded witness is the finding.</summary>
public enum WitnessAuthorship { Deliberate, Oversight }

/// <summary>The three ways a placed reference declares a host: XESP, XLKR, XAPR.</summary>
public enum HostEdgeKind { EnableParent, LinkedRef, ActivateParent }

/// <summary>What a host is, per <see cref="DetectorModel.ClassifyHost"/>. Only Physical hosts count for the
/// stranded-support / orphaned-attachment classes; Marker and Other are reported apart.</summary>
public enum HostClass { Physical, Marker, Other }

/// <summary>What <see cref="DetectorModel.PlacementDependency"/> makes of one edge. LeftBehind is the
/// candidate-shaped verdict (no geometry has been checked); MovedTogether, Intact and DependentRemoved are
/// nothing; Unjudgeable is counted by its <see cref="DependencyBlocker"/>, never cleared.</summary>
public enum DependencyVerdict { Intact, MovedTogether, LeftBehind, DependentRemoved, Unjudgeable }

/// <summary>Why an edge could not be called — why <see cref="DetectorModel.PlacementDependency"/> withheld a
/// verdict. <c>HostUnresolved</c> is about the
/// host's PLACEMENT (no plugin places it at all); <c>HostDeleted</c> is about the RECORD the compared views select
/// (Q34) — a missing record, a deleted record and an unresolved chain are three different answers;
/// <c>HostStateUnresolved</c> is about its ENABLE CHAIN in one of the two
/// compared views — the two stay separate on purpose. <c>DependentDeletedInAuthorView</c> (Q35) is the same record
/// question on the DEPENDENT's side: its record is a deletion in the author's own view while the winning order still
/// has it, so that view holds no placement to compare against — kept apart from <c>NoAuthoringView</c> (no record
/// contributed a position at all) and from <c>DependentMovedByThirdParty</c> (a real re-placement) precisely because
/// a missing baseline, an absent one and a moved one are three different answers. <c>HostDisabled</c> keeps its name for the tallies and JSON keys
/// that have always carried it, and since Q32 (2026-09-06) means the effective state leaves the host absent when the cell
/// loads, not that its own initially-disabled checkbox is ticked.</summary>
public enum DependencyBlocker { None, HostIsPlayer, HostUnresolved, HostDeleted, DependentDeletedInAuthorView, NoAuthoringView, HostDisabled, CrossCell, DependentMovedByThirdParty, HostStateUnresolved }

/// <summary>What a dependent is, per <see cref="DetectorModel.ClassifyDependent"/>. A LeftBehind row counts for
/// 4.5/4.6 only when the dependent is Physical AND the host is <see cref="HostClass.Physical"/>.</summary>
public enum DependentClass { Physical, Actor, Control, Other }

/// <summary>The rule-12 split for a dependency edge, by (mover, edge author).</summary>
public enum DependencyPopulation { BethesdaFloor, UssepMover, VanillaMoverUnderModEdge, ModMover }

/// <summary>Who created a deep overlap, per <see cref="DetectorModel.OverlapProvenance"/>: the later plugin
/// (Introduced) or somebody before it (Inherited — attributed to nobody by this pass).</summary>
public enum OverlapOrigin { Introduced, Inherited }

/// <summary>Whether the later plugin could have seen the other reference where it stands: Seen when its winning
/// placement came from the later plugin's own master closure, Blind otherwise. The class is Introduced + Blind.</summary>
public enum OverlapSight { Blind, Seen }

/// <summary>The rule-12 split for an overlap, by (responsible A, responsible B).</summary>
public enum OverlapPopulation { BethesdaFloor, UssepInvolved, ModOnMod }

/// <summary>The ranker's scale class, from the buried box's largest dimension. Reported beside the rank, never a verdict.</summary>
public enum OverlapScale { Architecture, Furniture, Clutter }

/// <summary>How a dependent met its host as authored: resting on it (the host's face under it points up), mounted
/// against it (the face points sideways), or hanging from it (the face points down). None: not in contact, or on a slope.</summary>
public enum ContactRelation { None, Supported, Attached, Hung }

/// <summary>What became of the arrangement under the winner. NoRelation: there was nothing to lose. Stranded / Orphaned:
/// the two classes — nothing under or against the dependent now. Sunk: something is, and the dependent is inside it.
/// ReSupported: something else holds it (counted, not a finding). MovedTogether: the mover's side re-placed it.
/// Removed: deleted or disabled under the winner — the fix. Unjudgeable: counted by reason, never cleared.</summary>
public enum ContactVerdict { NoRelation, Stranded, Orphaned, Sunk, ReSupported, MovedTogether, Removed, Unjudgeable }

/// <summary>Why a contact pair could not be called. The last three are the 2026-09-03 folds: a dependent absent at
/// runtime through its enable parent (R1), a chain the order cannot follow (R1), and a penetration deeper than the
/// against-object is wide (R4) — each counted, never cleared, never a finding.</summary>
public enum ContactBlocker { None, NoDependentGeometry, NoHostGeometry, AuthoringArmMissing, WinnerArmMissing, RuntimeDisabled, EnableChainUnresolved, DepthExceedsObject, ShallowCoverageIncomplete }

/// <summary>What physics does with a dependent at cell load. Fixed floats where it was left; Movable falls or is shoved
/// (4.7 Part C). Unknown: nothing known about the base or its bodies.</summary>
public enum DependentMotion { Fixed, Movable, Unknown }

/// <summary>What <see cref="DetectorModel.EmptySpace"/> makes of one fixture against one mover. LeftInEmptySpace is the class;
/// the rest are counted beside it, never findings: Absent (not there in one arm), Falls (a loose body: the rest rule's class),
/// Refused (by name), NoRelation (nothing the mover changed held it), FloatsAsShipped (the absolute floor), FloorPopulation
/// (Bethesda or USSEP on both sides, rule 12), Grounded (a floor or wall holds it under the winner and the mover's side did not re-place
/// it, so it cannot be the class; the order without the mover is not walked for it), RePlacedGrounded / RePlacedStillEmpty (the
/// mover's side moved it), ReGrounded (another floor or wall holds it now; read only when the order without the mover was walked).</summary>
public enum EmptySpaceVerdict { Absent, Falls, Refused, NoRelation, FloatsAsShipped, FloorPopulation, Grounded, RePlacedGrounded, RePlacedStillEmpty, ReGrounded, LeftInEmptySpace }
