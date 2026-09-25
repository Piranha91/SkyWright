using System.Numerics;
using CharacterViewer.Rendering;
using HousecarlCore;

namespace ViewProbe;

/// <summary>One NIF block and exactly the renderer meshes made from that block. A material
/// covering follows its parent shape; it is not an independent engine culling bound.</summary>
sealed class ShapeOcclusion
{
    public string Name = "";
    public int Block = -1;
    public string AssetHash = "";
    public NifShapeBound? Stored;
    public SphereBound? World;
    public string? Issue;
    public readonly List<GlMesh> Meshes = new();
    public readonly List<PickShape> Picks = new();
    public readonly List<PlaneRender> CulledBy = new();
    // PlaneRender's lazy geometry caches participate in its record hash. Match the
    // same scene plane by identity so materializing those caches cannot lose a refusal.
    public readonly Dictionary<PlaneRender, string> Relations = new(ReferenceEqualityComparer.Instance);
    public ShapeVisibility? Visibility;
    public string? VisibilityMask;
    public string? IsolatedDepthHash;
    public bool Culled => CulledBy.Count > 0;
    public bool Visible => Culled && Visibility is { Assessed: true, VisiblePixels: > 0 };
}

/// <summary>Bounded research policy, separately identified from the geometry primitive.
/// The near-eye interval is unsupported, not rounded into an invented threshold. A Box is a
/// catalogue entry unless the supplied rule opts in (<c>boxSilhouette</c>), and outside what
/// its evidence covers the refusal names what is still unassessed.</summary>
static class ShapeCullPolicy
{
    /// <param name="BoxSilhouette">Opt in to predicting Box primitives by their view shadow. A rule
    /// file that omits it keeps the catalogue behaviour, refusal text included.</param>
    /// <param name="HorizontalPortalBoxNormalDownEyeAbove">Opt in to predicting ONE further PortalBox class:
    /// horizontal (the authored normal's X and Y within <paramref name="MaximumAbsNormalZ"/> of 0), normal
    /// pointing DOWN (-Z), seen from ABOVE its plane (HarnessShared.PortalBoxOrientation). The +Z form, an eye
    /// below and every other tilt stay refused. A rule file that omits it predicts exactly what it always did,
    /// and the field is not written into a report's ruleConfiguration unless it is on, so such a report is
    /// byte-identical to one written before the field existed.</param>
    public sealed record Rule(string Identity, float MinimumEyeDistance, float MaximumAbsNormalZ,
                              bool Frozen, string[] Evidence, bool BoxSilhouette = false,
                              [property: System.Text.Json.Serialization.JsonIgnore(
                                  Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingDefault)]
                              bool HorizontalPortalBoxNormalDownEyeAbove = false);
    public static Rule? Configuration { get; private set; }
    public static string? Sha256 { get; private set; }
    public static string Identity => Configuration?.Identity ?? "unconfigured";
    public const string RectangleScope = "Research predictions for rigid opaque shapes and finite PortalBox rectangles; no cell-wide clearance or compatibility attribution.";
    public const string BoxScope = " Upright Box primitives beyond the rule's minimum eye distance are predicted by the silhouette of the stored box from the eye together with the box's own volume; a union of per-face shadows is NOT separated from that silhouette by the supporting evidence and remains untested.";
    /// <summary>What the configured rule claims to cover. The Box clause appears only when a rule
    /// opts in, so a rule file that says nothing about boxes reports exactly what it always did.</summary>
    public const string HorizontalScope = " Horizontal PortalBox rectangles whose authored normal points down (-Z), seen from above their plane, are predicted by the same rectangle shadow; the normal-up form, an eye below the plane and every other tilt remain refused.";
    public static string Scope => (Configuration is { BoxSilhouette: true } ? RectangleScope + BoxScope : RectangleScope)
                                  + (Configuration is { HorizontalPortalBoxNormalDownEyeAbove: true } ? HorizontalScope : "");

    public static void Load(string path)
    {
        var bytes = System.IO.File.ReadAllBytes(path);
        var rule = System.Text.Json.JsonSerializer.Deserialize<Rule>(bytes,
            new System.Text.Json.JsonSerializerOptions { PropertyNameCaseInsensitive = true,
                RespectRequiredConstructorParameters = true });
        if (rule is null || string.IsNullOrWhiteSpace(rule.Identity) ||
            !float.IsFinite(rule.MinimumEyeDistance) || rule.MinimumEyeDistance < 0 ||
            !float.IsFinite(rule.MaximumAbsNormalZ) || rule.MaximumAbsNormalZ < 0 || rule.MaximumAbsNormalZ > 1 ||
            rule.Evidence is null || rule.Evidence.Length == 0 || rule.Evidence.Any(string.IsNullOrWhiteSpace))
            throw new ArgumentException("Occlusion rule requires an identity, finite camera/orientation limits and evidence references.");
        Configuration = rule;
        Sha256 = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bytes));
    }

    public static (ConvexShadow? Shadow, string? Issue) Shadow(PlaneRender p, Vector3 eye)
    {
        if (Configuration is not { } rule) return (null, "engine rule unconfigured; supply --occlusion-rule with its evidence scope");
        if (p.Kind == PlaneKind.Box)
        {
            if (!rule.BoxSilhouette) return (null, "Box face behavior unassessed");
            if (!SphereBound.Finite(p.Bounds) || p.Bounds.X <= 0 || p.Bounds.Y <= 0 || p.Bounds.Z <= 0 ||
                !float.IsFinite(p.Scale) || p.Scale <= 0) return (null, "invalid plane bounds or scale");
            // Upright, on the same tolerance the rule already states for a rectangle's normal: the
            // measured box had no X or Y rotation, so both of its horizontal axes must stay level.
            float tilt = MathF.Max(MathF.Abs(PlaneGeometry.Axis(p, Vector3.UnitX).Z),
                                   MathF.Abs(PlaneGeometry.Axis(p, Vector3.UnitY).Z));
            if (!float.IsFinite(tilt) || tilt > rule.MaximumAbsNormalZ)
                return (null, "box orientation outside the supplied rule's evidence scope; only upright boxes were measured");
            var volume = ConvexShadow.FromBox(p.Corners, eye);
            if (volume is null) return (null, "no box view shadow: a degenerate box, or an eye inside or on it, is unassessed");
            if (volume.EyeDistance <= rule.MinimumEyeDistance)
                return (null, "near-eye behavior unassessed; observations bracket an exemption and do not establish a sharp threshold");
            return (volume, null);
        }
        if (!SphereBound.Finite(p.Bounds) || p.Bounds.X <= 0 || p.Bounds.Z <= 0 ||
            !float.IsFinite(p.Scale) || p.Scale <= 0) return (null, "invalid plane bounds or scale");
        var shadow = ConvexShadow.FromRectangle(p.Quad, eye);
        if (shadow is null) return (null, "degenerate plane or camera");
        var normal = Vector3.Normalize(Vector3.Cross(p.Quad[1]-p.Quad[0], p.Quad[3]-p.Quad[0]));
        var orientation = HarnessShared.PortalBoxOrientation.Check(normal, p.Normal, p.Centre, eye,
            rule.MaximumAbsNormalZ, rule.HorizontalPortalBoxNormalDownEyeAbove);
        if (orientation is not null) return (null, orientation);
        if (shadow.EyeDistance <= rule.MinimumEyeDistance)
            return (null, "near-eye behavior unassessed; observations bracket an exemption and do not establish a sharp threshold");
        return (shadow, null);
    }
}
