using System.Numerics;
using SkyRender;
using SkyRender.Character;
using HousecarlCore;
using SkyWright.Engine;

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

/// <summary>The rule this process renders with: the Engine's <see cref="OcclusionRule"/>, loaded once from
/// <c>--occlusion-rule</c>. The rule itself (its fields, scope, refusals and Shadow) is the Engine's; this holds which
/// rule file the process was given and asks it about viewprobe's planes.</summary>
static class ShapeCullPolicy
{
    public static OcclusionRule? Configuration { get; private set; }
    public static string? Sha256 { get; private set; }
    public static string Identity => Configuration?.Identity ?? "unconfigured";
    /// <summary>What the configured rule claims to cover (<see cref="OcclusionRule.ScopeOf"/>).</summary>
    public static string Scope => OcclusionRule.ScopeOf(Configuration);

    public static void Load(string path)
    {
        var (rule, sha) = OcclusionRule.Load(System.IO.File.ReadAllBytes(path));
        Configuration = rule;
        Sha256 = sha;
    }

    public static (ConvexShadow? Shadow, string? Issue) Shadow(PlaneRender p, Vector3 eye)
        => Configuration is not { } rule
            ? (null, "engine rule unconfigured; supply --occlusion-rule with its evidence scope")
            : rule.Shadow(p.Primitive, eye);
}
