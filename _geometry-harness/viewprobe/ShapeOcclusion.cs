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
/// The near-eye interval is unsupported, not rounded into an invented threshold. Boxes
/// remain a catalogue until their distinct face behavior has a measured control.</summary>
static class ShapeCullPolicy
{
    public sealed record Rule(string Identity, float MinimumEyeDistance, float MaximumAbsNormalZ,
                              bool Frozen, string[] Evidence);
    public static Rule? Configuration { get; private set; }
    public static string? Sha256 { get; private set; }
    public static string Identity => Configuration?.Identity ?? "unconfigured";
    public const string Scope = "Research predictions for rigid opaque shapes and finite PortalBox rectangles; no cell-wide clearance or compatibility attribution.";

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
        if (p.Kind != PlaneKind.PortalBox) return (null, "Box face behavior unassessed");
        if (!SphereBound.Finite(p.Bounds) || p.Bounds.X <= 0 || p.Bounds.Z <= 0 ||
            !float.IsFinite(p.Scale) || p.Scale <= 0) return (null, "invalid plane bounds or scale");
        var shadow = ConvexShadow.FromRectangle(p.Quad, eye);
        if (shadow is null) return (null, "degenerate plane or camera");
        var normal = Vector3.Normalize(Vector3.Cross(p.Quad[1]-p.Quad[0], p.Quad[3]-p.Quad[0]));
        if (MathF.Abs(normal.Z) > rule.MaximumAbsNormalZ)
            return (null, "plane orientation outside the supplied rule's evidence scope");
        if (shadow.EyeDistance <= rule.MinimumEyeDistance)
            return (null, "near-eye behavior unassessed; observations bracket an exemption and do not establish a sharp threshold");
        return (shadow, null);
    }
}
