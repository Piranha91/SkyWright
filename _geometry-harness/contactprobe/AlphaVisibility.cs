using HarnessShared;
using HousecarlCore;
using Mutagen.Bethesda.Plugins;

/// <summary>contactprobe --alpha-visibility (with --surface): a render pair whose crossing lies on an alpha-tested or alpha-blended
/// triangle also gets a <c>visibility</c> block (<see cref="SurfaceVisibility"/>): the part of the crossing curve that passes through
/// drawn texels of both members, mip 0, bilinear, with the texture each alpha shape was read from. A swept witness whose crossing is
/// not opaque x opaque gets a <c>visibleMotion</c> block (<see cref="SweptGeometry.ContactVisible"/>). Without the flag nothing here
/// runs and the surface file is unchanged. Textures are the VFS winners of the run's own resolver (the one the meshes were read with),
/// replaced by the winning base record's alternate texture set where one names the shape.</summary>
sealed class AlphaVisibility(AssetResolver assets, OverlapIndex index)
{
    public VisibilityOptions Options { get; } = new();
    readonly Func<string, (byte[]? Bytes, TextureProvenance Where)> reader = TextureAlpha.Reader(assets);
    readonly Dictionary<string, (MemberAlpha Alpha, IReadOnlyList<TextureProvenance> Provenance)> members = new();

    /// <summary>The member's alpha source, read when the measure first asks for it. <paramref name="modelSoup"/> is the stored-pose
    /// model-space render soup of <paramref name="bytes"/> (the texture coordinates are index-aligned with it, proven by the reader).</summary>
    public Func<MemberAlpha?> Source(string formKey, byte[] bytes, TriangleSoup modelSoup) => () =>
    {
        if (members.TryGetValue(formKey, out var hit)) return hit.Alpha;
        var tc = SurfaceTexcoords.Read(bytes, modelSoup);
        var (texture, provenance) = tc.Status == "measured"
            ? TextureAlpha.ForMember(tc, Alternates(formKey), reader)
            : ((ShapeTexture)(_ => (null, tc.Reason)), (IReadOnlyList<TextureProvenance>)Array.Empty<TextureProvenance>());
        var alpha = new MemberAlpha(tc, texture);
        members[formKey] = (alpha, provenance);
        return alpha;
    };

    /// <summary>A moving member's source: its static render soup (NifGeometry.MeasureVisibleRender), which the posed soups keep the
    /// triangle order of (alpha K14). Null when the stored render soup cannot be read.</summary>
    public Func<MemberAlpha?> MovingSource(string formKey, byte[] bytes)
    {
        var g = NifGeometry.MeasureVisibleRender(bytes, includeTriangles: true);
        return g.Model?.Soup is { } soup ? Source(formKey, bytes, soup) : () => null;
    }

    // The winning base record's alternate textures: shape name and 3D index, with the texture set's diffuse (null: unresolved).
    IReadOnlyList<AlternateTexture>? Alternates(string formKey)
    {
        if (!FormKey.TryFactory(formKey, out var key) || !index.RefBase.TryGetValue(key, out var basis)
            || !index.BaseAlternateTextures.TryGetValue(basis, out var alts)) return null;
        return alts.Select(a => new AlternateTexture(a.Name, a.Index, index.TextureSetDiffuse.TryGetValue(a.TextureSet, out var d) ? d : null)).ToList();
    }

    /// <summary>The textures read for a member (none when the measure never asked for its alpha).</summary>
    IReadOnlyList<TextureProvenance>? Textures(string formKey) => members.TryGetValue(formKey, out var m) ? m.Provenance : null;

    public object Block(SurfaceVisibilityResult v, string formA, string formB) => new
    {
        status = v.Status, reason = v.Reason, policy = v.PolicyVersion,
        rule = $"mip 0, bilinear; alpha test: > .. >= the shape's threshold; blend only: >= {Options.BlendThreshold} (readings at {Options.LadderLow} and {Options.LadderHigh})",
        alphaVisible = v.AlphaVisible, visibleCrossing = v.VisibleCrossing, thresholdSensitive = v.ThresholdSensitive,
        byTags = v.ByTags, labels = v.Labels, texturesA = Textures(formA), texturesB = Textures(formB),
    };

    public object MotionBlock(SweptVisibleDecision d, string formMoving, string formOther) => new
    {
        decision = d.Decision, reason = d.Reason, animation = d.AnimationId, time = d.Time, visibleLower = d.VisibleLower,
        sampledPoses = d.SampledPoses, spacing = d.Spacing, labels = d.Labels,
        texturesMoving = Textures(formMoving), texturesOther = Textures(formOther),
    };

    /// <summary>True when a measured crossing has length on a key with an alpha-tested or alpha-blended side.</summary>
    public static bool HasAlphaCrossing(IReadOnlyDictionary<string, double>? byTags) =>
        byTags is not null && byTags.Any(kv => kv.Value > 0 && (kv.Key.Contains(SurfaceMaterial.AlphaTest, StringComparison.Ordinal)
                                                                || kv.Key.Contains(SurfaceMaterial.AlphaBlend, StringComparison.Ordinal)));
}
