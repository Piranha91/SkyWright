using System.Numerics;
using System.Text.Json.Serialization;

namespace SkyWright.Engine;

// RED STUB (unit 1, first commit): the API with wrong bodies, so OcclusionRuleTests fail on their assertions.
// THE OCCLUSION RULE: which occlusion primitives the research rule predicts from an eye, and the primitive's geometry.
// One owner. viewprobe (its renders and planes report, through ShapeCullPolicy), the station rule (StationRule's
// corners and mid-quad) and the occlusion probe call it; nothing else carries its constants. The orientation verdicts
// are PortalBoxOrientation (PortalBoxOrientation.cs). Moved here from viewprobe's ShapeOcclusion.cs unchanged in
// behaviour: the same refusal texts, the same rule-file fields, the same report serialization.

/// <summary>The two primitive kinds the rule reads (XPRM type 3 and 1).</summary>
public enum OcclusionPrimitiveKind { PortalBox, Box }

/// <summary>One occlusion primitive as the winning state places it. <paramref name="Bounds"/> are its FULL extents
/// (twice the raw XPRM floats); <paramref name="Rotation"/> is the REFR's angles in radians (SpatialOverlap.RefrRotation).</summary>
public readonly record struct OcclusionPrimitive(OcclusionPrimitiveKind Kind, Vector3 Bounds, Vector3 Position, Vec3 Rotation, float Scale);

/// <summary>A primitive's world geometry. Every caller's corners, mid-quad and axes come from here.</summary>
public static class OcclusionPrimitiveGeometry
{
    internal static Vector3 Unit(Vector3 d) => d.LengthSquared() > 1e-12f ? Vector3.Normalize(d) : Vector3.Zero;

    /// <summary>The eight corners of the primitive's box in Skyrim space, indexed by axis bits (1 = +x, 2 = +y, 4 = +z).</summary>
    public static Vector3[] BoxCorners(in OcclusionPrimitive p) => new Vector3[8];   // RED STUB

    internal static Vector3[] BoxCornersReal(in OcclusionPrimitive p)
    {
        var r = SpatialOverlap.RefrRotation(p.Rotation);
        var h = p.Bounds * 0.5f * p.Scale;
        var c = new Vector3[8];
        for (int i = 0; i < 8; i++)
        {
            var l = new Vector3((i & 1) == 0 ? -h.X : h.X, (i & 2) == 0 ? -h.Y : h.Y, (i & 4) == 0 ? -h.Z : h.Z);
            c[i] = Vector3.Transform(l, r) + p.Position;
        }
        return c;
    }

    /// <summary>The slab's mid-plane quad (local y = 0), ring-ordered, Skyrim space.</summary>
    public static Vector3[] MidQuad(in OcclusionPrimitive p) => new Vector3[4];   // RED STUB

    internal static Vector3[] MidQuadReal(in OcclusionPrimitive p)
    {
        var r = SpatialOverlap.RefrRotation(p.Rotation);
        var h = p.Bounds * 0.5f * p.Scale;
        return new[]
        {
            Vector3.Transform(new Vector3(-h.X, 0f, -h.Z), r) + p.Position,
            Vector3.Transform(new Vector3(h.X, 0f, -h.Z), r) + p.Position,
            Vector3.Transform(new Vector3(h.X, 0f, h.Z), r) + p.Position,
            Vector3.Transform(new Vector3(-h.X, 0f, h.Z), r) + p.Position,
        };
    }

    /// <summary>A local axis of the primitive in the world, unit length (zero for a degenerate rotation).</summary>
    public static Vector3 Axis(in OcclusionPrimitive p, Vector3 local) => Vector3.Zero;   // RED STUB
}

/// <summary>Bounded research policy, separately identified from the geometry primitive.
/// The near-eye interval is unsupported, not rounded into an invented threshold. A Box is a
/// catalogue entry unless the supplied rule opts in (<c>boxSilhouette</c>), and outside what
/// its evidence covers the refusal names what is still unassessed.</summary>
/// <param name="BoxSilhouette">Opt in to predicting Box primitives by their view shadow. A rule
/// file that omits it keeps the catalogue behaviour, refusal text included.</param>
/// <param name="HorizontalPortalBoxNormalDownEyeAbove">Opt in to predicting ONE further PortalBox class:
/// horizontal (the authored normal's X and Y within <paramref name="MaximumAbsNormalZ"/> of 0), normal
/// pointing DOWN (-Z), seen from ABOVE its plane (PortalBoxOrientation). The +Z form, an eye
/// below and every other tilt stay refused. A rule file that omits it predicts exactly what it always did,
/// and the field is not written into a report's ruleConfiguration unless it is on, so such a report is
/// byte-identical to one written before the field existed.</param>
/// <param name="UprightMaximumAbsNormalZ">The upright class's own tolerance: how far a PortalBox's normal, or a
/// Box's horizontal axes, may leave level. A rule file that omits it uses <paramref name="MaximumAbsNormalZ"/>, which
/// then governs only the horizontal class; the field is not written into a report unless the rule states it.</param>
public sealed record OcclusionRule(string Identity, float MinimumEyeDistance, float MaximumAbsNormalZ,
                                   bool Frozen, string[] Evidence, bool BoxSilhouette = false,
                                   [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
                                   bool HorizontalPortalBoxNormalDownEyeAbove = false,
                                   [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
                                   float? UprightMaximumAbsNormalZ = null)
{
    /// <summary>The upright tolerance in force: the rule's own, else <see cref="MaximumAbsNormalZ"/>.</summary>
    [JsonIgnore]
    public float UprightTolerance => UprightMaximumAbsNormalZ ?? MaximumAbsNormalZ;

    public const string RectangleScope = "Research predictions for rigid opaque shapes and finite PortalBox rectangles; no cell-wide clearance or compatibility attribution.";
    public const string BoxScope = " Upright Box primitives beyond the rule's minimum eye distance are predicted by the silhouette of the stored box from the eye together with the box's own volume; a union of per-face shadows is NOT separated from that silhouette by the supporting evidence and remains untested.";
    public const string HorizontalScope = " Horizontal PortalBox rectangles whose authored normal points down (-Z), seen from above their plane, are predicted by the same rectangle shadow; the normal-up form, an eye below the plane and every other tilt remain refused.";

    /// <summary>The refusal sentence of a rule file that cannot be used.</summary>
    public const string LoadRefusal = "Occlusion rule requires an identity, finite camera/orientation limits and evidence references.";
    public const string BoxUnassessed = "Box face behavior unassessed";
    public const string InvalidBounds = "invalid plane bounds or scale";
    public const string NoBoxShadow = "no box view shadow: a degenerate box, or an eye inside or on it, is unassessed";
    public const string DegeneratePlane = "degenerate plane or camera";
    public const string NearEye = "near-eye behavior unassessed; observations bracket an exemption and do not establish a sharp threshold";

    /// <summary>The upright tolerance's clause, only for a rule that states its own.</summary>
    internal static string UprightScope(float t) =>
        $" Upright Box and PortalBox primitives are admitted within |n.z| <= {t.ToString("0.####", System.Globalization.CultureInfo.InvariantCulture)} of upright (a rectangle's normal, a box's horizontal axes); larger tilts outside the horizontal class remain refused.";

    /// <summary>What a rule claims to cover; with no rule, the rectangle scope alone. The Box clause appears only when
    /// the rule opts in, so a rule file that says nothing about boxes reports exactly what it always did.</summary>
    public static string ScopeOf(OcclusionRule? rule) => "";   // RED STUB

    /// <summary>A rule file's bytes as a rule and the bytes' SHA-256 (upper-case hex); refused with
    /// <see cref="LoadRefusal"/> when a field is missing or out of range.</summary>
    public static (OcclusionRule Rule, string Sha256) Load(byte[] bytes) =>   // RED STUB: no checks, no hash
        (System.Text.Json.JsonSerializer.Deserialize<OcclusionRule>(bytes,
            new System.Text.Json.JsonSerializerOptions { PropertyNameCaseInsensitive = true })!, "");

    /// <summary>The primitive's view shadow from <paramref name="eye"/> when this rule predicts it, else the refusal.</summary>
    public (ConvexShadow? Shadow, string? Issue) Shadow(in OcclusionPrimitive p, Vector3 eye) => (null, "red stub");
}
