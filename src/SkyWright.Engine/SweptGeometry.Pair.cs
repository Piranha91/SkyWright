using HousecarlCore;

namespace SkyWright.Engine;

// TWO MOVING MEMBERS (animated motion follow-ups, item 4, decision E4, Gate A review F1). Two references animate on their
// own clocks unless the files link them, and a script or trigger can couple them in ways the files do not show. So:
//   never       - no sample of A, grown by its own delta plus B's largest, comes near B's whole swept region (and no
//                 sampled pose of either lies inside the other's): every point of each member stays inside its own outer
//                 region whatever the clocks do. Reported without a link check.
//   witness     - only with no coupling in the files, and only for a crossing that holds whatever the clocks' offset or
//                 trigger timing: one member at a HELD pose (its rest, the end of a CLAMP animation, a held graph state)
//                 against any sampled time of the other. A held member stays put while the other plays a whole
//                 animation, so every time of that animation meets the held pose. A loop never supplies the held side.
//   cannot_tell - everything else; a crossing with both members mid-animation names both times and says the game
//                 reaches that pair only for some starts of the two clocks; under a coupling every crossing is this.
// No volume interval is given for two moving members. The exact pair tests are capped by a count (pairBudget).
//
// STATUS: API frozen at the Gate B red gate; implemented.

/// <summary>A held pose of a member, posed and placed in world space.</summary>
public sealed record HeldSoup(string Pose, string AnimationId, float Time, TriangleSoup Soup, string[]? Tags);

/// <summary>Decision <c>never</c>, <c>witness</c>, <c>cannot_tell</c> or <c>refused</c> for two moving members. For a
/// witness or a named crossing: each side's animation and time, or its held pose (PoseA / PoseB). Width: the largest
/// dA + dB among the samples not cleared. Coupling: the link the files show, or null.</summary>
public sealed record SweptPairDecision(string Decision, string? Reason, string? AnimationA, float? TimeA, string? PoseA,
                                       string? AnimationB, float? TimeB, string? PoseB, double Width, bool Converged,
                                       string? Coupling, long PairsExamined, long ContainmentTests = 0);

public static partial class SweptGeometry
{
    /// <summary>Default cap on exact sample-pair tests (the witness and crossing-detail searches). The never test is not
    /// capped: it tests each sample of one member against the other member's whole region. A count, not a clock.</summary>
    public const long DefaultPairBudget = 65_536;

    /// <summary>Cap on point-in-solid tests in the never test's containment step (solid regions only). A count.</summary>
    public const long ContainmentBudget = 16_777_216;

    public const string IndependentClocks = "independent clocks: the files do not link the two references; a script or trigger can still couple them";

    /// <summary>The member's held poses (<see cref="NifMotion.HeldPoses"/>), posed in the given geometry and placed by its
    /// reference's transform; a pose that cannot be posed is left out (its refusal is the region's own).</summary>
    public static IReadOnlyList<HeldSoup> HeldSoups(byte[] nif, MotionModel model, VolumeGeometry geometry, Vec3 position, Vec3 rotation,
                                                    float scale, Func<string, byte[]?>? read = null)
    {
        var list = new List<HeldSoup>();
        foreach (var p in NifMotion.HeldPoses(model))
        {
            var posed = NifMotion.Pose(nif, model, p.AnimationId, p.Time, geometry, read);
            if (posed.Status != "posed" || posed.Soup is null) continue;
            list.Add(new HeldSoup(p.Name, p.AnimationId, p.Time, SpatialOverlap.TransformSoup(posed.Soup, position, rotation, scale), posed.Tags));
        }
        return list;
    }

    // an exact crossing of two placed soups by the existing predicates (Contact's witness test). Pruned first: two
    // surfaces that come nowhere near each other cross only if one lies inside the other, which one vertex decides.
    static bool Crosses(TriangleSoup a, string[]? tagsA, bool solidA, TriangleSoup b, string[]? tagsB, bool solidB, VolumeGeometry g, CancellationToken ct,
                        Func<TriangleSoup, object?> mesh, Func<TriangleSoup, object?> solid)
    {
        // the pruning reads the drawn view of both soups (ShapeDrawing); the crossing measure keeps the whole soups
        var (ad, _) = MeshSurface.Drawn(a, tagsA); var (bd, _) = MeshSurface.Drawn(b, tagsB);
        if (ad.TriangleCount == 0 || bd.TriangleCount == 0) return false;
        if (!Overlap(Box(ad), Box(bd), 0)) return false;
        var ma = mesh(ad); var mb = mesh(bd);
        if (ma is not null && mb is not null && !MeshIntersection.MeshesWithin(ma, mb, 0))
        {
            bool inside = false;
            var va = FirstVertex(ad, a); var vb = FirstVertex(bd, b);
            if (solidB && ad.Vertices.Length > 0 && solid(bd) is { } sb) inside |= MeshIntersection.PointInSolid(sb, va.X, va.Y, va.Z) != 0;
            if (solidA && bd.Vertices.Length > 0 && solid(ad) is { } sa) inside |= MeshIntersection.PointInSolid(sa, vb.X, vb.Y, vb.Z) != 0;
            if (!inside) return false;
        }
        if (solidA && solidB)
        {
            var v = MeshVolume.Measure(ad, bd, g, cancellationToken: ct);
            if (v.Status == "measured" && v.Intersection is { Lower: > 0 }) return true;
        }
        var c = MeshSurface.Measure(a, tagsA, b, tagsB, g, cancellationToken: ct);
        if (c.Status != "measured") return false;
        double cross = c.CrossingLength?.Lower ?? 0;
        double buried = Math.Max(c.AInB?.Inside?.Lower ?? 0, c.BInA?.Inside?.Lower ?? 0);
        return cross > c.RoundingBound || buried > 0;
    }

    /// <summary>Two placed moving members (regions from <see cref="Place"/>, held soups from <see cref="HeldSoups"/>).
    /// <paramref name="coupling"/> is the link the files show between the two references, or null.</summary>
    public static SweptPairDecision ContactBoth(SweptRegion a, IReadOnlyList<HeldSoup> heldA, SweptRegion b, IReadOnlyList<HeldSoup> heldB,
                                                string? coupling, MotionOptions? options = null, long pairBudget = DefaultPairBudget,
                                                CancellationToken cancellationToken = default, long containmentBudget = ContainmentBudget)
    {
        SweptPairDecision Refuse(string why) => new("refused", why, null, null, null, null, null, null, 0, false, coupling, 0);
        if (a.Status != "built") return Refuse("A: " + (a.Reason ?? "refused"));
        if (b.Status != "built") return Refuse("B: " + (b.Reason ?? "refused"));
        bool converged = a.Converged && b.Converged;
        var g = a.Geometry;

        // 1. never: each sample of A against B's whole region, grown by its own delta plus B's largest delta (a larger grow
        //    can only clear less, so a clearance holds for every pair), then containment both ways.
        // the never test reads the drawn view of every sample (ShapeDrawing); a member that draws nothing meets nothing
        if (a.Samples.All(s => MeshSurface.Drawn(s.Soup, s.Tags).Soup.TriangleCount == 0)
            || b.Samples.All(s => MeshSurface.Drawn(s.Soup, s.Tags).Soup.TriangleCount == 0))
            return new("never", null, null, null, null, null, null, null, 0, converged, coupling, 0);
        var meshB = CombinedMesh(b);
        if (meshB is null) return Refuse("B: a sampled pose is not a valid mesh");
        double maxDb = b.Samples.Max(s => s.Delta), maxDa = a.Samples.Max(s => s.Delta);
        var boxB = MeshIntersection.MeshBounds(meshB);
        var unclearA = new List<int>();
        // containment: a sample with no surface near the other region is wholly inside or outside each of its solids;
        // one vertex decides (as Contact does). Prepared solids are cached; the tests are counted against a cap.
        var solidsA = new object?[a.Samples.Count]; var solidsB = new object?[b.Samples.Count];
        long containTests = 0; bool containCapped = false;
        bool Contained(System.Numerics.Vector3 p, SweptRegion r, object?[] cache)
        {
            for (int k = 0; k < r.Samples.Count; k++)
            {
                var bx = Box(r.Samples[k].Soup);
                if (p.X < bx.MinX || p.X > bx.MaxX || p.Y < bx.MinY || p.Y > bx.MaxY || p.Z < bx.MinZ || p.Z > bx.MaxZ) continue;
                if (containTests >= containmentBudget) { containCapped = true; return true; }
                containTests++;
                cache[k] ??= (object?)MeshIntersection.PrepareVolumeSolid(r.Samples[k].Soup, 50_000_000, CancellationToken.None).Solid ?? NotSolid;
                if (ReferenceEquals(cache[k], NotSolid)) return true;   // cannot tell: counted as inside, so never stays certified
                if (MeshIntersection.PointInSolid(cache[k]!, p.X, p.Y, p.Z) != 0) return true;
            }
            return false;
        }
        for (int i = 0; i < a.Samples.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var s = a.Samples[i];
            var (sd, _) = MeshSurface.Drawn(s.Soup, s.Tags);
            if (sd.TriangleCount == 0) continue;
            if (!Overlap(Box(sd), boxB, s.Delta + maxDb)) continue;
            var ms = MeshIntersection.PrepareSurfaceMesh(sd, out _);
            if (ms is null || MeshIntersection.MeshesWithin(ms, meshB, s.Delta + maxDb)) { unclearA.Add(i); continue; }
            if (b.Solid && sd.Vertices.Length > 0 && Contained(FirstVertex(sd, s.Soup), b, solidsB)) unclearA.Add(i);
        }
        bool bInsideA = false;
        if (unclearA.Count == 0 && a.Solid)
            foreach (var sb in b.Samples)
            {
                var (sbd, _) = MeshSurface.Drawn(sb.Soup, sb.Tags);
                if (sbd.TriangleCount > 0 && Contained(FirstVertex(sbd, sb.Soup), a, solidsA)) { bInsideA = true; break; }
            }
        if (containCapped)
            return new("cannot_tell", $"containment budget reached: {containmentBudget} point-in-solid tests made before the never test finished",
                       null, null, null, null, null, null, maxDa + maxDb, converged, coupling, 0, containTests);
        if (unclearA.Count == 0 && !bInsideA)
            return new("never", null, null, null, null, null, null, null, 0, converged, coupling, 0, containTests);

        long examined = 0; bool budgetHit = false;
        var meshCache = new Dictionary<TriangleSoup, object?>(ReferenceEqualityComparer.Instance);
        var solidCache = new Dictionary<TriangleSoup, object?>(ReferenceEqualityComparer.Instance);
        object? MeshOf(TriangleSoup t) => meshCache.TryGetValue(t, out var m) ? m : meshCache[t] = MeshIntersection.PrepareSurfaceMesh(t, out _);
        object? SolidOf(TriangleSoup t) => solidCache.TryGetValue(t, out var m) ? m : solidCache[t] = MeshIntersection.PrepareVolumeSolid(t, 50_000_000, CancellationToken.None).Solid;
        bool X(TriangleSoup p, string[]? tp, bool sp, TriangleSoup q, string[]? tq, bool sq) => Crosses(p, tp, sp, q, tq, sq, g, cancellationToken, MeshOf, SolidOf);
        bool Take() { if (examined >= pairBudget) { budgetHit = true; return false; } examined++; return true; }

        // 2. witness: only with no coupling, and only where one side rests at a held pose against any time of the other
        if (coupling is null)
        {
            foreach (var h in heldB)
                foreach (var s in a.Samples)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (!Overlap(Box(s.Soup), Box(h.Soup), 0)) continue;
                    if (!Take()) goto afterWitness;
                    if (X(s.Soup, s.Tags, a.Solid, h.Soup, h.Tags, b.Solid))
                        return new("witness", $"{IndependentClocks}; B rests at its held pose '{h.Pose}' while A plays {s.AnimationId}",
                                   s.AnimationId, s.Time, null, h.AnimationId, h.Time, h.Pose, 0, converged, null, examined);
                }
            foreach (var h in heldA)
                foreach (var s in b.Samples)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (!Overlap(Box(s.Soup), Box(h.Soup), 0)) continue;
                    if (!Take()) goto afterWitness;
                    if (X(h.Soup, h.Tags, a.Solid, s.Soup, s.Tags, b.Solid))
                        return new("witness", $"{IndependentClocks}; A rests at its held pose '{h.Pose}' while B plays {s.AnimationId}",
                                   h.AnimationId, h.Time, h.Pose, s.AnimationId, s.Time, null, 0, converged, null, examined);
                }
        }
        afterWitness:

        // 3. cannot_tell, naming a crossing when one is found: both mid-animation, or (under a coupling) any crossing
        double width = (unclearA.Count == 0 ? maxDa : unclearA.Max(i => a.Samples[i].Delta)) + maxDb;
        if (!budgetHit)
        {
            foreach (var (sa, pa, sb, pb) in DetailPairs(a, heldA, b, heldB, unclearA, coupling is not null))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!Overlap(Box(sa.Soup), Box(sb.Soup), 0)) continue;
                if (!Take()) break;
                if (X(sa.Soup, sa.Tags, a.Solid, sb.Soup, sb.Tags, b.Solid))
                {
                    string at(SweptSample s, string? held) => $"{s.AnimationId}@{s.Time.ToString(System.Globalization.CultureInfo.InvariantCulture)}{(held is null ? "" : $" (held '{held}')")}";
                    string why = coupling is not null
                        ? $"the files link the two references ({coupling}): A {at(sa, pa)} and B {at(sb, pb)} cross, and whether the game shows that pair depends on how they are triggered together"
                        : $"A {at(sa, pa)} and B {at(sb, pb)} cross with both mid-animation; whether the game reaches this pair depends on how the two clocks start ({IndependentClocks})";
                    return new("cannot_tell", why, sa.AnimationId, sa.Time, pa, sb.AnimationId, sb.Time, pb, width, converged, coupling, examined);
                }
            }
        }
        string reason = budgetHit ? $"pair budget reached: {pairBudget} exact sample-pair tests made before a decision"
                      : coupling is not null ? $"the two swept regions come within dA + dB and no sampled pair crosses; the files link the two references ({coupling})"
                      : $"the two swept regions come within dA + dB and no sampled pair crosses ({IndependentClocks})";
        return new("cannot_tell", reason, null, null, null, null, null, null, width, converged, coupling, examined);
    }

    static IEnumerable<(SweptSample A, string? PoseA, SweptSample B, string? PoseB)> DetailPairs(SweptRegion a, IReadOnlyList<HeldSoup> heldA,
        SweptRegion b, IReadOnlyList<HeldSoup> heldB, List<int> unclearA, bool coupled)
    {
        foreach (int i in unclearA) foreach (var sb in b.Samples) yield return (a.Samples[i], null, sb, null);
        if (!coupled) yield break;
        foreach (var h in heldB) foreach (var sa in a.Samples) yield return (sa, null, new SweptSample(h.AnimationId, h.Time, h.Soup, h.Tags, 0), h.Pose);
        foreach (var h in heldA) foreach (var sb in b.Samples) yield return (new SweptSample(h.AnimationId, h.Time, h.Soup, h.Tags, 0), h.Pose, sb, null);
    }

    static object? CombinedMesh(SweptRegion r)
    {
        var v = new List<System.Numerics.Vector3>(); var idx = new List<int>();
        foreach (var s in r.Samples)
        {
            var (d, _) = MeshSurface.Drawn(s.Soup, s.Tags);
            int b0 = v.Count; v.AddRange(d.Vertices); idx.AddRange(d.Indices.Select(x => x + b0));
        }
        return MeshIntersection.PrepareSurfaceMesh(new TriangleSoup(v.ToArray(), idx.ToArray()), out _);
    }

    static readonly object NotSolid = new();
}

public static partial class NifMotion
{
    /// <summary>The poses a member can rest in while something else moves (E4): its rest pose; the CLAMP door aliases
    /// closed / open / after-close; the end of every NIF animation with CYCLE_CLAMP; a graph state's pose when its clip is
    /// SINGLE_PLAY or CYCLE_CLAMP (taken at the end) or USER_CONTROLLED (a fixed pose). Never a time inside an animation;
    /// never a looping, ping-pong or reverse animation's time. One entry per (animation, time), keeping the named one.</summary>
    public static IReadOnlyList<MotionPose> HeldPoses(MotionModel model)
    {
        var anim = model.Animations.ToDictionary(a => a.Id, a => a);
        bool Clamp(string id) => anim.TryGetValue(id, out var a) && a.Cycle == "CYCLE_CLAMP";
        bool HeldState(string id) => anim.TryGetValue(id, out var a) && a.Cycle is "SINGLE_PLAY" or "CYCLE_CLAMP" or "USER_CONTROLLED";
        var held = new List<MotionPose>();
        var seen = new HashSet<(string, float)>();
        // named poses first (rest, the door aliases, graph states), so a duplicate (animation, time) keeps its name
        int Rank(MotionPose p) => p.Rest ? 0 : p.Name is "closed" or "open" or "after-close" ? 1 : p.Name.StartsWith("state:", StringComparison.Ordinal) ? 2 : 3;
        foreach (var p in model.Poses.OrderBy(Rank))
        {
            bool keep = (p.Rest && !Loops(p.AnimationId))
                        || (p.Name is "closed" or "open" or "after-close" && Clamp(p.AnimationId))
                        || (p.Name == $"{p.AnimationId}@end" && Clamp(p.AnimationId))
                        || (p.Name.StartsWith("state:", StringComparison.Ordinal) && HeldState(p.AnimationId));
            if (keep && seen.Add((p.AnimationId, p.Time))) held.Add(p);
        }
        return held;

        bool Loops(string id) => anim.TryGetValue(id, out var a) && a.Cycle is "CYCLE_LOOP" or "LOOPING" or "PING_PONG" or "CYCLE_REVERSE";
    }
}
