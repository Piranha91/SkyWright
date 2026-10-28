using CharacterViewer.Rendering;

namespace ViewProbe;

/// <summary>F-G5-3 (Gate B8 review Q4): the retained renderer's created settings, as an explicit record — every settable
/// property of the dependency's GlRenderer, its five light slots and its overlay list — captured when the cell
/// creates and initializes the renderer and applied at every later BeginShot, before the shot sets its own six. A shot
/// on a retained renderer therefore starts from exactly what a fresh renderer has. The diagnostic sink is restored only
/// when it differs (its setter re-arms one-shot dumps). A property the dependency adds later is not covered until it
/// is added here.</summary>
internal sealed class RendererSettings
{
    OpenTK.Mathematics.Vector3 WireframeColor;
    OpenTK.Mathematics.Vector3 MissingTextureWireframeColor;
    bool EnableToneMapping;
    bool EnableShadows;
    bool ExcludeHairShadowCaster;
    bool SoftenShadowEdges;
    float ShadowPcfRadius;
    bool TightShadowFrustum;
    float ShadowFrustumRadius;
    bool EnableAmbientOcclusion;
    float SsaoRadius;
    float SsaoBias;
    float SsaoIntensity;
    float SsaoThickness;
    float SsaoHairGap;
    bool EnableEyeCatchlight;
    bool SpecularAchromatic;
    bool SkinFaithfulSoftLight;
    bool TonemapHairRelief;
    float HairAlbedoCompensate;
    bool DaylightBoost;
    float DaylightBoostIntensity;
    bool EnableBloom;
    float BloomIntensity;
    float SubsurfaceStrength;
    float SkinSaturationBoost;
    float VignetteRadius;
    float VignetteIntensity;
    float Exposure;
    bool SkinTintApplyToFace;
    int SkinTintOperator;
    float SkinTintLerpStrength;
    int VertexColorMultiplyMode;
    int FaceTintMode;
    bool FaceTintMultiplyOnEmptyDetail;
    bool UseEngineStyleDetailMap;
    bool UseBlankDetailFallback;
    bool ShowKeyLightVisualization;
    int SelectedLightIndex;
    OpenTK.Mathematics.Vector3 BacklightColor;
    OpenTK.Mathematics.Vector3 ClearColor;
    float ModelScale;
    OpenTK.Mathematics.Vector4 ClipPlane;
    bool ClipEnabled;
    bool ShowClipPlaneVisual;
    OpenTK.Mathematics.Vector3 ClipPlaneVisualColor;
    GlRenderer.LightData[] Lights = [];
    List<IRenderOverlay> Overlays = new();
    Action<string>? DiagnosticLog;

    internal static RendererSettings Capture(GlRenderer r)
    {
        var s = new RendererSettings
        {
            WireframeColor = r.WireframeColor,
            MissingTextureWireframeColor = r.MissingTextureWireframeColor,
            EnableToneMapping = r.EnableToneMapping,
            EnableShadows = r.EnableShadows,
            ExcludeHairShadowCaster = r.ExcludeHairShadowCaster,
            SoftenShadowEdges = r.SoftenShadowEdges,
            ShadowPcfRadius = r.ShadowPcfRadius,
            TightShadowFrustum = r.TightShadowFrustum,
            ShadowFrustumRadius = r.ShadowFrustumRadius,
            EnableAmbientOcclusion = r.EnableAmbientOcclusion,
            SsaoRadius = r.SsaoRadius,
            SsaoBias = r.SsaoBias,
            SsaoIntensity = r.SsaoIntensity,
            SsaoThickness = r.SsaoThickness,
            SsaoHairGap = r.SsaoHairGap,
            EnableEyeCatchlight = r.EnableEyeCatchlight,
            SpecularAchromatic = r.SpecularAchromatic,
            SkinFaithfulSoftLight = r.SkinFaithfulSoftLight,
            TonemapHairRelief = r.TonemapHairRelief,
            HairAlbedoCompensate = r.HairAlbedoCompensate,
            DaylightBoost = r.DaylightBoost,
            DaylightBoostIntensity = r.DaylightBoostIntensity,
            EnableBloom = r.EnableBloom,
            BloomIntensity = r.BloomIntensity,
            SubsurfaceStrength = r.SubsurfaceStrength,
            SkinSaturationBoost = r.SkinSaturationBoost,
            VignetteRadius = r.VignetteRadius,
            VignetteIntensity = r.VignetteIntensity,
            Exposure = r.Exposure,
            SkinTintApplyToFace = r.SkinTintApplyToFace,
            SkinTintOperator = r.SkinTintOperator,
            SkinTintLerpStrength = r.SkinTintLerpStrength,
            VertexColorMultiplyMode = r.VertexColorMultiplyMode,
            FaceTintMode = r.FaceTintMode,
            FaceTintMultiplyOnEmptyDetail = r.FaceTintMultiplyOnEmptyDetail,
            UseEngineStyleDetailMap = r.UseEngineStyleDetailMap,
            UseBlankDetailFallback = r.UseBlankDetailFallback,
            ShowKeyLightVisualization = r.ShowKeyLightVisualization,
            SelectedLightIndex = r.SelectedLightIndex,
            BacklightColor = r.BacklightColor,
            ClearColor = r.ClearColor,
            ModelScale = r.ModelScale,
            ClipPlane = r.ClipPlane,
            ClipEnabled = r.ClipEnabled,
            ShowClipPlaneVisual = r.ShowClipPlaneVisual,
            ClipPlaneVisualColor = r.ClipPlaneVisualColor,
            Lights = (GlRenderer.LightData[])r.Lights.Clone(),
            DiagnosticLog = r.DiagnosticLog,
        };
        s.Overlays.AddRange(r.Overlays);
        return s;
    }

    internal void ApplyTo(GlRenderer r)
    {
        r.WireframeColor = WireframeColor;
        r.MissingTextureWireframeColor = MissingTextureWireframeColor;
        r.EnableToneMapping = EnableToneMapping;
        r.EnableShadows = EnableShadows;
        r.ExcludeHairShadowCaster = ExcludeHairShadowCaster;
        r.SoftenShadowEdges = SoftenShadowEdges;
        r.ShadowPcfRadius = ShadowPcfRadius;
        r.TightShadowFrustum = TightShadowFrustum;
        r.ShadowFrustumRadius = ShadowFrustumRadius;
        r.EnableAmbientOcclusion = EnableAmbientOcclusion;
        r.SsaoRadius = SsaoRadius;
        r.SsaoBias = SsaoBias;
        r.SsaoIntensity = SsaoIntensity;
        r.SsaoThickness = SsaoThickness;
        r.SsaoHairGap = SsaoHairGap;
        r.EnableEyeCatchlight = EnableEyeCatchlight;
        r.SpecularAchromatic = SpecularAchromatic;
        r.SkinFaithfulSoftLight = SkinFaithfulSoftLight;
        r.TonemapHairRelief = TonemapHairRelief;
        r.HairAlbedoCompensate = HairAlbedoCompensate;
        r.DaylightBoost = DaylightBoost;
        r.DaylightBoostIntensity = DaylightBoostIntensity;
        r.EnableBloom = EnableBloom;
        r.BloomIntensity = BloomIntensity;
        r.SubsurfaceStrength = SubsurfaceStrength;
        r.SkinSaturationBoost = SkinSaturationBoost;
        r.VignetteRadius = VignetteRadius;
        r.VignetteIntensity = VignetteIntensity;
        r.Exposure = Exposure;
        r.SkinTintApplyToFace = SkinTintApplyToFace;
        r.SkinTintOperator = SkinTintOperator;
        r.SkinTintLerpStrength = SkinTintLerpStrength;
        r.VertexColorMultiplyMode = VertexColorMultiplyMode;
        r.FaceTintMode = FaceTintMode;
        r.FaceTintMultiplyOnEmptyDetail = FaceTintMultiplyOnEmptyDetail;
        r.UseEngineStyleDetailMap = UseEngineStyleDetailMap;
        r.UseBlankDetailFallback = UseBlankDetailFallback;
        r.ShowKeyLightVisualization = ShowKeyLightVisualization;
        r.SelectedLightIndex = SelectedLightIndex;
        r.BacklightColor = BacklightColor;
        r.ClearColor = ClearColor;
        r.ModelScale = ModelScale;
        r.ClipPlane = ClipPlane;
        r.ClipEnabled = ClipEnabled;
        r.ShowClipPlaneVisual = ShowClipPlaneVisual;
        r.ClipPlaneVisualColor = ClipPlaneVisualColor;
        if (r.Lights.Length != Lights.Length)
            throw new InvalidOperationException($"renderer light slots changed: {r.Lights.Length} against {Lights.Length}");
        Array.Copy(Lights, r.Lights, Lights.Length);
        r.Overlays.Clear(); r.Overlays.AddRange(Overlays);
        if (!ReferenceEquals(r.DiagnosticLog, DiagnosticLog)) r.DiagnosticLog = DiagnosticLog;
    }

    internal const int PropertyCount = 46;
    internal const int CollectionCount = 1;
}
