using System.Numerics;

namespace HarnessShared;

// ======================================================================
//  PortalBoxOrientation - which PortalBox orientations the occlusion
//  research rule predicts, as a pure function so it can be proven without
//  a renderer. viewprobe's ShapeCullPolicy.Shadow asks it; proofprobe's
//  stationrule H cases hold it down.
//
//  The sealed rule predicts only UPRIGHT rectangles: the rectangle's normal
//  may leave level by at most maximumAbsNormalZ. That stays the default,
//  word for word.
//
//  One further class can be opted into by a rule file, and ONLY that class:
//  a HORIZONTAL PortalBox whose authored normal (local +Y) points DOWN (-Z),
//  seen from ABOVE its plane - the in-situ form of 20 of the College's 21
//  "tilted" panels (X about 90/270 with a Y yaw, lying just under walkable
//  floors). Horizontal means |n.x| and |n.y| within the same tolerance of 0
//  (so |n.z| is within it of 1). The +Z form, an eye below the plane and
//  every other tilt stay refused until separately tested.
// ======================================================================

public static class PortalBoxOrientation
{
    /// <summary>The sealed refusal, unchanged: the text every report has carried.</summary>
    public const string OutsideScope = "plane orientation outside the supplied rule's evidence scope";
    public const string HorizontalUp =
        "horizontal PortalBox with its normal up (+Z): outside the supplied rule's evidence scope; only normal-down (-Z) was opted in";
    public const string HorizontalEyeBelow =
        "horizontal PortalBox seen from below its plane: outside the supplied rule's evidence scope; only eye-above was opted in";

    /// <summary>The orientation verdict for one PortalBox from one eye. <paramref name="authoredNormal"/> is the
    /// ref's local +Y in the world (unit); <paramref name="quadNormal"/> is the rectangle's own normal as the
    /// shadow code computes it (cross of its edges) - the sealed test reads that one, and it still does.
    /// Returns null when the rectangle may be predicted, else the refusal text.</summary>
    public static string? Check(Vector3 quadNormal, Vector3 authoredNormal, Vector3 centre, Vector3 eye,
                                float maxAbsNormalZ, bool horizontalNormalDownEyeAbove)
    {
        if (MathF.Abs(quadNormal.Z) <= maxAbsNormalZ) return null;            // upright: the sealed class
        if (!horizontalNormalDownEyeAbove) return OutsideScope;                // the default, byte for byte
        bool horizontal = MathF.Abs(authoredNormal.X) <= maxAbsNormalZ
                          && MathF.Abs(authoredNormal.Y) <= maxAbsNormalZ;
        if (!horizontal) return OutsideScope;                                  // any other tilt
        if (authoredNormal.Z > 0f) return HorizontalUp;
        if (!(eye.Z - centre.Z > 0f)) return HorizontalEyeBelow;              // on the plane counts as below
        return null;
    }
}
