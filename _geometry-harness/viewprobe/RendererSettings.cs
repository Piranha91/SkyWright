using CharacterViewer.Rendering;

namespace ViewProbe;

/// <summary>F-G5-3 (Gate B8 review Q4): the retained renderer's created settings, as an explicit record — every settable
/// property of the dependency's GlRenderer, its five light slots and its overlay collections — captured when the cell
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
    OpenTK.Mathematics.Vector3 KeyVertexMarkerColor;
    OpenTK.Mathematics.Vector3 KeyVertexMarkerSelectedColor;
    OpenTK.Mathematics.Vector3 BoxResolvedMarkerColor;
    OpenTK.Mathematics.Vector3 PreviewKeyVertexMarkerColor;
    OpenTK.Mathematics.Vector3 PreviewPickMarkerColor;
    OpenTK.Mathematics.Vector3 RegionCapMarkerColor;
    OpenTK.Mathematics.Vector3 RegionSolidColor;
    float KeyVertexMarkerRadius;
    float RegionWireWidth;
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
    List<OpenTK.Mathematics.Vector3> KeyVertexMarkers = new();
    HashSet<int> SelectedKeyVertexMarkerIndices = new();
    List<OpenTK.Mathematics.Vector3> BoxResolvedMarkers = new();
    List<OpenTK.Mathematics.Vector3> PreviewKeyVertexMarkers = new();
    List<OpenTK.Mathematics.Vector3> PreviewPickMarkers = new();
    List<OpenTK.Mathematics.Vector3> RegionCapMarkers = new();
    List<float> RegionSolidTriangles = new();
    List<float> SliderHeatmapTriangles = new();
    List<GlRenderer.MeasurementLineSegment> MeasurementLines = new();
    List<GlRenderer.MeasurementLineSegment> RegionOverlayLines = new();
    List<GlRenderer.MeasurementLineSegment> RegionWireLines = new();
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
            KeyVertexMarkerColor = r.KeyVertexMarkerColor,
            KeyVertexMarkerSelectedColor = r.KeyVertexMarkerSelectedColor,
            BoxResolvedMarkerColor = r.BoxResolvedMarkerColor,
            PreviewKeyVertexMarkerColor = r.PreviewKeyVertexMarkerColor,
            PreviewPickMarkerColor = r.PreviewPickMarkerColor,
            RegionCapMarkerColor = r.RegionCapMarkerColor,
            RegionSolidColor = r.RegionSolidColor,
            KeyVertexMarkerRadius = r.KeyVertexMarkerRadius,
            RegionWireWidth = r.RegionWireWidth,
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
        s.KeyVertexMarkers.AddRange(r.KeyVertexMarkers);
        s.SelectedKeyVertexMarkerIndices.UnionWith(r.SelectedKeyVertexMarkerIndices);
        s.BoxResolvedMarkers.AddRange(r.BoxResolvedMarkers);
        s.PreviewKeyVertexMarkers.AddRange(r.PreviewKeyVertexMarkers);
        s.PreviewPickMarkers.AddRange(r.PreviewPickMarkers);
        s.RegionCapMarkers.AddRange(r.RegionCapMarkers);
        s.RegionSolidTriangles.AddRange(r.RegionSolidTriangles);
        s.SliderHeatmapTriangles.AddRange(r.SliderHeatmapTriangles);
        s.MeasurementLines.AddRange(r.MeasurementLines);
        s.RegionOverlayLines.AddRange(r.RegionOverlayLines);
        s.RegionWireLines.AddRange(r.RegionWireLines);
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
        r.KeyVertexMarkerColor = KeyVertexMarkerColor;
        r.KeyVertexMarkerSelectedColor = KeyVertexMarkerSelectedColor;
        r.BoxResolvedMarkerColor = BoxResolvedMarkerColor;
        r.PreviewKeyVertexMarkerColor = PreviewKeyVertexMarkerColor;
        r.PreviewPickMarkerColor = PreviewPickMarkerColor;
        r.RegionCapMarkerColor = RegionCapMarkerColor;
        r.RegionSolidColor = RegionSolidColor;
        r.KeyVertexMarkerRadius = KeyVertexMarkerRadius;
        r.RegionWireWidth = RegionWireWidth;
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
        r.KeyVertexMarkers.Clear(); r.KeyVertexMarkers.AddRange(KeyVertexMarkers);
        r.SelectedKeyVertexMarkerIndices.Clear(); r.SelectedKeyVertexMarkerIndices.UnionWith(SelectedKeyVertexMarkerIndices);
        r.BoxResolvedMarkers.Clear(); r.BoxResolvedMarkers.AddRange(BoxResolvedMarkers);
        r.PreviewKeyVertexMarkers.Clear(); r.PreviewKeyVertexMarkers.AddRange(PreviewKeyVertexMarkers);
        r.PreviewPickMarkers.Clear(); r.PreviewPickMarkers.AddRange(PreviewPickMarkers);
        r.RegionCapMarkers.Clear(); r.RegionCapMarkers.AddRange(RegionCapMarkers);
        r.RegionSolidTriangles.Clear(); r.RegionSolidTriangles.AddRange(RegionSolidTriangles);
        r.SliderHeatmapTriangles.Clear(); r.SliderHeatmapTriangles.AddRange(SliderHeatmapTriangles);
        r.MeasurementLines.Clear(); r.MeasurementLines.AddRange(MeasurementLines);
        r.RegionOverlayLines.Clear(); r.RegionOverlayLines.AddRange(RegionOverlayLines);
        r.RegionWireLines.Clear(); r.RegionWireLines.AddRange(RegionWireLines);
        if (!ReferenceEquals(r.DiagnosticLog, DiagnosticLog)) r.DiagnosticLog = DiagnosticLog;
    }

    internal const int PropertyCount = 55;
    internal const int CollectionCount = 11;
}
