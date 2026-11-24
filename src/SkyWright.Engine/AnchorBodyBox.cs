using System.Numerics;

using HousecarlCore;

namespace SkyWright.Engine;

/// <summary>A placed actor's humanoid body (<see cref="MeshAnchor"/>'s capsule: radius Radius, axis from Bottom + Radius to
/// Top - Radius above the anchor) against a world box, for box contact when the actor's base has no model path. Usage-week M4
/// (2026-10-14, g1-0040).</summary>
public static class AnchorBodyBox
{
    /// <summary>The label a box-contact row carries when the actor's body stands in for its missing model.</summary>
    public const string Label = "actor body (route constants)";

    /// <summary>Does the body standing at <paramref name="anchor"/> meet <paramref name="box"/>? Exact capsule-box distance.</summary>
    public static bool Meets(Vector3 anchor, BodyExtent body, Aabb box)
    {
        if (box.IsEmpty) return false;
        double r = body.Radius, lo = body.Bottom + r, hi = body.Top - r;
        if (hi < lo) lo = hi = (body.Bottom + body.Top) / 2.0;   // as MeshAnchor: a body shorter than its diameter is a sphere
        double zLo = anchor.Z + lo, zHi = anchor.Z + hi;
        // the axis is vertical and the box axis-aligned, so the distance splits exactly into a horizontal and a vertical gap
        double dx = Math.Max(0, Math.Max(box.Min.X - anchor.X, anchor.X - box.Max.X));
        double dy = Math.Max(0, Math.Max(box.Min.Y - anchor.Y, anchor.Y - box.Max.Y));
        double dz = Math.Max(0, Math.Max(box.Min.Z - zHi, zLo - box.Max.Z));
        return dx * dx + dy * dy + dz * dz <= r * r;
    }

    /// <summary>Is the body the member's geometry? Only for a placed actor (its placed arm is <c>PlacedNpc</c>) whose base has no
    /// model path; any other model-less member keeps its refusal, and a member with a model keeps its render surface.</summary>
    public static bool Applies(string? placedArm, bool hasModelPath) => !hasModelPath && placedArm == "PlacedNpc";
}
