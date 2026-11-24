using HousecarlCore;

namespace SkyWright.Engine;

// BOX AGAINST A MOVING MEMBER (contactprobe pair-measure gaps, lane 3b, gap e follow-on). --box-contact tested a supplied world box only
// against the other member's stored render pose and refused a member whose live pose is not its stored pose (a chest's latch, a skinned
// banner). Against the member's placed swept region: witness when a sampled pose's opaque triangles meet the box (that animation and
// time); never when no sample's opaque triangles meet the box grown by the sample's certified displacement bound on every side (the grown
// box holds every point within that bound of the box, so a point moving at most that far cannot reach the box); cannot_tell otherwise.
//
// STATUS: implemented (red at c5b38437).

/// <summary>Decision <c>witness</c>, <c>never</c>, <c>cannot_tell</c> or <c>refused</c>; a witness names the animation and time.</summary>
public sealed record BoxContactMotionResult(string Decision, string? Reason, string? AnimationId, float? Time, bool Converged, int Samples);

public static partial class MeshSurfaceBox
{
    public const string MotionPolicyVersion = "box_contact_swept_v1";

    /// <summary>A world box against a placed swept region (opaque triangles decide).</summary>
    public static BoxContactMotionResult BoxContactSwept(SweptRegion placed, Aabb box)
    {
        if (placed.Status != "built") return new("refused", placed.Reason ?? "swept region not built", null, null, false, 0);
        if (placed.Samples.Count == 0) return new("refused", "swept region has no sample", null, null, false, 0);
        if (box.IsEmpty) return new("refused", "box contact: empty box", null, null, false, 0);
        string? openWhy = null;
        foreach (var s in placed.Samples)
        {
            var hit = BoxContact(s.Soup, s.Tags, box);
            if (hit.Reason is not null) return new("refused", $"{s.AnimationId}@{s.Time}: {hit.Reason}", null, null, placed.Converged, placed.Samples.Count);
            if (hit.OpaqueMeets) return new("witness", null, s.AnimationId, s.Time, placed.Converged, placed.Samples.Count);
            if (openWhy is not null) continue;
            // grown on every side by the sample's displacement bound, rounded outward, plus a rounding allowance scaled to the coordinates
            double d = s.Delta + 1e-6 * (1 + Math.Abs(box.Min.X) + Math.Abs(box.Min.Y) + Math.Abs(box.Min.Z) + Math.Abs(box.Max.X) + Math.Abs(box.Max.Y) + Math.Abs(box.Max.Z));
            static float Down(double v) { float f = (float)v; return f > v ? MathF.BitDecrement(f) : f; }
            static float Up(double v) { float f = (float)v; return f < v ? MathF.BitIncrement(f) : f; }
            var grown = new Aabb(new Vec3(Down(box.Min.X - d), Down(box.Min.Y - d), Down(box.Min.Z - d)),
                                 new Vec3(Up(box.Max.X + d), Up(box.Max.Y + d), Up(box.Max.Z + d)), false);
            if (BoxContact(s.Soup, s.Tags, grown).OpaqueMeets)
                openWhy = $"{s.AnimationId}@{s.Time}: an opaque triangle lies within the sample's displacement bound {s.Delta:0.###} u of the box";
        }
        return openWhy is null ? new("never", null, null, null, placed.Converged, placed.Samples.Count)
                               : new("cannot_tell", openWhy, null, null, placed.Converged, placed.Samples.Count);
    }
}
