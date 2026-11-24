using System.Numerics;

using HousecarlCore;

namespace SkyWright.Engine;

// ANCHOR AGAINST A MOVING PARTNER (contactprobe pair-measure gaps, lane 3b, gap b). contactprobe --anchor tested the actor's body only
// against a partner's stored pose and refused a partner whose live pose is not its stored pose. With --motion swept the body is tested
// against every sample of the partner's swept region (SweptGeometry.Build, placed): witness when a sampled pose's opaque surface meets
// the body (MeshAnchor's body reading, at that animation and time); never when every sample is clear by more than the sample's
// certified displacement bound (a point that moves at most delta cannot close a clearance larger than delta); cannot_tell otherwise.
//
// STATUS: implemented (red at 7bbbe518).

/// <summary>Decision <c>witness</c>, <c>never</c>, <c>cannot_tell</c> or <c>refused</c>. A witness names the animation and time;
/// Margin is the smallest (clearance - delta) over the samples when never, and the remaining width when cannot_tell.</summary>
public sealed record AnchorMotionResult(string Decision, string? Reason, string? AnimationId, float? Time, double? Margin, bool Converged,
                                        int Samples);

public static class AnchorMotion
{
    public const string PolicyVersion = "anchor_body_swept_v1";

    /// <summary>Test an anchor's body against a placed swept region.</summary>
    public static AnchorMotionResult TestSwept(SweptRegion placed, Vector3 anchor, BodyExtent body, HalfSpace? exclude = null)
    {
        if (placed.Status != "built") return new("refused", placed.Reason ?? "swept region not built", null, null, null, false, 0);
        if (placed.Samples.Count == 0) return new("refused", "swept region has no sample", null, null, null, false, 0);
        // rounding allowance on the clearance comparison, scaled to the coordinates involved
        double tol = 1e-6 * (1 + Math.Abs(anchor.X) + Math.Abs(anchor.Y) + Math.Abs(anchor.Z));
        double minMargin = double.MaxValue;
        string? openWhy = null;
        foreach (var s in placed.Samples)
        {
            var r = MeshAnchor.Test(s.Soup, s.Tags, anchor, body, exclude);
            if (r.Decision == "meets")
                return new("witness", r.Reason, s.AnimationId, s.Time, null, placed.Converged, placed.Samples.Count);
            if (r.Decision == "undecided") { openWhy ??= $"{s.AnimationId}@{s.Time}: {r.Reason}"; continue; }
            // clear: the sample is clear of every opaque surface by r.BodyClearance (null = no opaque surface at all)
            double margin = (r.BodyClearance ?? double.MaxValue) - s.Delta - tol;
            if (margin <= 0) openWhy ??= $"{s.AnimationId}@{s.Time}: clearance {r.BodyClearance} not above the sample's displacement bound {s.Delta}";
            minMargin = Math.Min(minMargin, margin);
        }
        if (openWhy is null)
            return new("never", null, null, null, minMargin == double.MaxValue ? null : minMargin, placed.Converged, placed.Samples.Count);
        return new("cannot_tell", openWhy, null, null, minMargin == double.MaxValue ? null : minMargin, placed.Converged, placed.Samples.Count);
    }
}
