using System.Numerics;

namespace HarnessShared;

// ======================================================================
//  PortalBoxOrientation - which primitive orientations the occlusion
//  research rule predicts, as pure functions so they can be proven without
//  a renderer. viewprobe's ShapeCullPolicy.Shadow asks them; proofprobe's
//  stationrule H and 6016-6020 cases hold them down.
//
//  The rule predicts UPRIGHT primitives: a rectangle's normal, or a box's
//  two horizontal axes, may leave level by at most the rule's upright
//  tolerance. A rule file states it as uprightMaximumAbsNormalZ; one that
//  does not state it uses maximumAbsNormalZ, so every older rule behaves
//  word for word as before. (Lab block 3, 2026-10-15: tilts up to |n.z|
//  0.021 cull in game as their true shape predicts.)
//
//  One further class can be opted into by a rule file, and ONLY that class:
//  a HORIZONTAL PortalBox whose authored normal (local +Y) points DOWN (-Z),
//  seen from ABOVE its plane - the in-situ form of 20 of the College's 21
//  "tilted" panels (X about 90/270 with a Y yaw, lying just under walkable
//  floors). Horizontal means |n.x| and |n.y| within maximumAbsNormalZ of 0
//  (so |n.z| is within it of 1): the upright tolerance does not reach it.
//  The +Z form, an eye below the plane and every other tilt stay refused
//  until separately tested.
// ======================================================================

public static class PortalBoxOrientation
{
    /// <summary>The sealed refusal, unchanged: the text every report has carried.</summary>
    public const string OutsideScope = "plane orientation outside the supplied rule's evidence scope";
    public const string HorizontalUp =
        "horizontal PortalBox with its normal up (+Z): outside the supplied rule's evidence scope; only normal-down (-Z) was opted in";
    public const string HorizontalEyeBelow =
        "horizontal PortalBox seen from below its plane: outside the supplied rule's evidence scope; only eye-above was opted in";
    /// <summary>The Box refusal, unchanged: the text every report has carried.</summary>
    public const string BoxOutsideScope = "box orientation outside the supplied rule's evidence scope; only upright boxes were measured";

    /// <summary>The orientation verdict for one PortalBox from one eye. <paramref name="authoredNormal"/> is the
    /// ref's local +Y in the world (unit); <paramref name="quadNormal"/> is the rectangle's own normal as the
    /// shadow code computes it (cross of its edges) - the upright test reads that one. The upright test uses
    /// <paramref name="maxUprightAbsNormalZ"/>, the horizontal class <paramref name="maxHorizontalDeviation"/>.
    /// Returns null when the rectangle may be predicted, else the refusal text.</summary>
    public static string? Check(Vector3 quadNormal, Vector3 authoredNormal, Vector3 centre, Vector3 eye,
                                float maxUprightAbsNormalZ, float maxHorizontalDeviation, bool horizontalNormalDownEyeAbove)
    {
        if (MathF.Abs(quadNormal.Z) <= maxUprightAbsNormalZ) return null;     // upright
        if (!horizontalNormalDownEyeAbove) return OutsideScope;                // the default, byte for byte
        bool horizontal = MathF.Abs(authoredNormal.X) <= maxHorizontalDeviation
                          && MathF.Abs(authoredNormal.Y) <= maxHorizontalDeviation;
        if (!horizontal) return OutsideScope;                                  // any other tilt
        if (authoredNormal.Z > 0f) return HorizontalUp;
        if (!(eye.Z - centre.Z > 0f)) return HorizontalEyeBelow;              // on the plane counts as below
        return null;
    }

    /// <summary>Whether a Box is upright: both of its horizontal axes (<paramref name="axisX"/>, <paramref name="axisY"/>,
    /// unit, in the world) leave level by at most <paramref name="maxUprightAbsNormalZ"/>. Null when it may be
    /// predicted, else the refusal text.</summary>
    public static string? BoxUpright(Vector3 axisX, Vector3 axisY, float maxUprightAbsNormalZ)
    {
        float tilt = MathF.Max(MathF.Abs(axisX.Z), MathF.Abs(axisY.Z));
        return float.IsFinite(tilt) && tilt <= maxUprightAbsNormalZ ? null : BoxOutsideScope;
    }
}
