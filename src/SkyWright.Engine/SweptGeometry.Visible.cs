using HousecarlCore;

namespace SkyWright.Engine;

// SWEPT CONTACT, VISIBLE PART (lane D, Stage D; Gate A D5 accepted as F4). SweptGeometry.Contact stops at the first sampled pose
// with any crossing, alpha included. This opt-in variant measures the visible crossing (SurfaceVisibility) at every unclear sampled
// pose: a visible crossing at any sample is a visible witness; none at any sample reads "no visible crossing at N sampled poses
// (spacing delta)", a labelled reading that is NOT never (the drawn texel region is not geometry, so the sweep's clearance test
// cannot certify between samples). The moving member's texture coordinates are its static soup's: a posed soup keeps the static
// triangle order (checked by the caller's alignment proof).
//
// STATUS: API frozen at the lane D Gate B red gate; implemented at Gate C.

/// <summary>Decision <c>visible_witness</c>, <c>no_visible_at_samples</c>, <c>never</c> (the unchanged swept never),
/// <c>refused</c>. SampledPoses: how many unclear poses were measured; Spacing: the largest sample delta among them.</summary>
public sealed record SweptVisibleDecision(string Decision, string? Reason, string? AnimationId, float? Time, double? VisibleLower,
                                          int SampledPoses, double Spacing, IReadOnlyList<string> Labels);

public static partial class SweptGeometry
{
    public static SweptVisibleDecision ContactVisible(SweptRegion moving, MemberAlpha? movingAlpha, TriangleSoup partner, string[]? partnerTags,
                                                      MemberAlpha? partnerAlpha, bool partnerIsSolid, VisibilityOptions? options = null,
                                                      CancellationToken cancellationToken = default)
    {
        var none = Array.Empty<string>();
        if (moving.Status != "built") return new("refused", moving.Reason, null, null, null, 0, 0, none);
        if (partner is null || partner.TriangleCount == 0) return new("refused", "partner: empty_mesh", null, null, null, 0, 0, none);
        // the never-test reads the drawn view of both members (ShapeDrawing), as SweptGeometry.Contact does
        var (pd, partnerIsSolidDrawn) = DrawnPartner(partner, partnerTags, partnerIsSolid);
        if (pd.TriangleCount == 0) return new("never", null, null, null, null, 0, 0, none);
        partnerIsSolid = partnerIsSolidDrawn;
        var pBox = Box(pd);
        var pMesh = MeshIntersection.PrepareSurfaceMesh(pd, out var why);
        if (pMesh is null) return new("refused", "partner: " + why, null, null, null, 0, 0, none);
        // 1. the never-test per sample (SweptGeometry.Contact's): samples that are not clear are measured
        var unclear = new List<SweptSample>();
        foreach (var s in moving.Samples)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var (sd, _) = MeshSurface.Drawn(s.Soup, s.Tags);
            if (sd.TriangleCount == 0) continue;
            var sBox = Box(sd);
            if (!Overlap(sBox, pBox, s.Delta)) continue;
            var sMesh = MeshIntersection.PrepareSurfaceMesh(sd, out _);
            if (sMesh is null || MeshIntersection.MeshesWithin(sMesh, pMesh, s.Delta)) { unclear.Add(s); continue; }
            bool contained = false;
            if (partnerIsSolid && sd.Vertices.Length > 0)
            {
                var v = FirstVertex(sd, s.Soup);
                contained |= MeshIntersection.PointInSolid(pMesh, v.X, v.Y, v.Z) != 0;
            }
            if (moving.Solid && pd.Vertices.Length > 0 && sMesh is not null)
            {
                var v = FirstVertex(pd, partner);
                contained |= MeshIntersection.PointInSolid(sMesh, v.X, v.Y, v.Z) != 0;
            }
            if (contained) unclear.Add(s);
        }
        if (unclear.Count == 0) return new("never", null, null, null, null, 0, 0, none);
        // 2. the visible crossing at every unclear pose whose box meets the partner's (the others cannot cross at the sample)
        var labels = new SortedSet<string>(StringComparer.Ordinal);
        int measured = 0;
        foreach (var s in unclear)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!Overlap(Box(MeshSurface.Drawn(s.Soup, s.Tags).Soup), pBox, 0)) continue;
            // the moving member's texture coordinates are its static soup's: a sample with another triangle count cannot use them
            if (movingAlpha?.Texcoords.Triangles is { } tc && tc.Count != s.Soup.TriangleCount)
                return new("refused", $"pose {s.AnimationId}@{s.Time}: the posed soup has {s.Soup.TriangleCount} triangles, the texture coordinates {tc.Count}",
                           s.AnimationId, s.Time, null, measured, unclear.Max(x => x.Delta), labels.ToList());
            var r =SurfaceVisibility.Measure(s.Soup, s.Tags, movingAlpha, partner, partnerTags, partnerAlpha, options, cancellationToken: cancellationToken);
            if (r.Status != "measured" || r.VisibleCrossing is not { } vc)
                return new("refused", $"pose {s.AnimationId}@{s.Time}: {r.Reason ?? r.Status}", s.AnimationId, s.Time, null, measured, unclear.Max(x => x.Delta), labels.ToList());
            measured++;
            foreach (var l in r.Labels) labels.Add(l);
            if (r.ThresholdSensitive) labels.Add($"pose {s.AnimationId}@{s.Time}: threshold-sensitive");
            if (vc.Lower > (r.Contact?.RoundingBound ?? 0))
                return new("visible_witness", null, s.AnimationId, s.Time, vc.Lower, measured, unclear.Max(x => x.Delta), labels.ToList());
        }
        double spacing = unclear.Max(x => x.Delta);
        labels.Add($"no visible crossing at {measured} sampled pose(s) (spacing {spacing:0.###} u); not a never: the drawn texel region is not geometry");
        return new("no_visible_at_samples", null, null, null, null, measured, spacing, labels.ToList());
    }
}
