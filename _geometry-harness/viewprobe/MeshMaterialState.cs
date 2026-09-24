using CharacterViewer.Rendering;

namespace ViewProbe;

/// <summary>F-G5-2 (Gate B8 review): the full mutable material and draw state of one retained mesh — every settable
/// property of the dependency's GlMesh except IsRendering (restored by BuiltScene's visibility baseline, as before), CpuPositions (geometry, set with the upload), CpuIndices (geometry, set with the upload), CpuBoneIndices (geometry, set with the upload), CpuBoneWeights (geometry, set with the upload).
/// Captured at the end of each binding pass, before any per-shot paint (occlusion, BOS and state tints, visibility), and
/// restored with visibility by BuiltScene.RestoreBaseline, so nothing one shot writes on a retained mesh reaches the next.
/// The list is explicit; a property the dependency adds later is not covered until it is added here.</summary>
internal sealed class MeshMaterialState
{
    int DiffuseTexture;
    int NormalTexture;
    int SkinTexture;
    int SpecularTexture;
    int FaceTintTexture;
    bool HasNormalMap;
    bool HasSkinMap;
    bool HasSpecular;
    bool HasSpecularMap;
    bool HasFaceTintMap;
    bool HasGreyscaleToPalette;
    bool HasTintColor;
    bool HasEmissive;
    bool IsModelSpace;
    bool HasHairSoftLighting;
    bool HasSoftLighting;
    bool HasRimLighting;
    bool HasVertexColors;
    bool UseAlphaTest;
    bool HasAlphaBlend;
    bool IsDoubleSided;
    bool DepthWrite;
    bool IsDecal;
    float MaterialAlpha;
    int SrcBlendIndex;
    int DstBlendIndex;
    bool IsHairTintShader;
    bool IsEye;
    int BipedSlots;
    int HidesSlots;
    int SlotDrawPriority;
    string? OverrideKey;
    bool HiddenBySlotOccupancy;
    bool ShowWireframe;
    bool RenderAsWireframeFallback;
    System.Numerics.Vector3? WireframeColorOverride;
    float AlphaThreshold;
    float GreyscaleToPaletteScale;
    System.Numerics.Vector3 TintColor;
    float MaterialGlossiness;
    float MaterialSpecularStrength;
    System.Numerics.Vector3 SpecularColor;
    float RimlightPower;
    float SubsurfaceRolloff;
    System.Numerics.Vector3 EmissiveColor;
    float EmissiveMultiple;
    System.Numerics.Vector2 UvScale;
    System.Numerics.Vector2 UvOffset;
    float EnvMapScale;
    float EyeCubemapScale;
    bool UseEyeCubemapScale;
    int EnvMapTexture;
    int EnvMaskTexture;
    bool HasEnvironmentMap;
    bool HasEnvMask;
    bool IsEnvMap2D;
    int DetailTexture;
    bool HasDetailMap;
    int GlowTexture;
    bool HasGlowMap;
    bool DiffuseEnabled;
    bool NormalEnabled;
    bool SkinEnabled;
    bool SpecularEnabled;
    bool FaceTintEnabled;
    bool DetailEnabled;
    bool EnvMapEnabled;
    bool EmissiveEnabled;
    bool TintColorEnabled;
    string ShapeName;
    string BodyPart;
    bool IsPrimaryHeadShape;
    bool IsFaceShape;
    bool IsSkinShape;
    float SkinTintAlpha;
    bool IsFaceWithEmptyDetailSlot;
    AssetSource? MeshSource;

    internal static MeshMaterialState Capture(GlMesh m) => new()
    {
        DiffuseTexture = m.DiffuseTexture,
        NormalTexture = m.NormalTexture,
        SkinTexture = m.SkinTexture,
        SpecularTexture = m.SpecularTexture,
        FaceTintTexture = m.FaceTintTexture,
        HasNormalMap = m.HasNormalMap,
        HasSkinMap = m.HasSkinMap,
        HasSpecular = m.HasSpecular,
        HasSpecularMap = m.HasSpecularMap,
        HasFaceTintMap = m.HasFaceTintMap,
        HasGreyscaleToPalette = m.HasGreyscaleToPalette,
        HasTintColor = m.HasTintColor,
        HasEmissive = m.HasEmissive,
        IsModelSpace = m.IsModelSpace,
        HasHairSoftLighting = m.HasHairSoftLighting,
        HasSoftLighting = m.HasSoftLighting,
        HasRimLighting = m.HasRimLighting,
        HasVertexColors = m.HasVertexColors,
        UseAlphaTest = m.UseAlphaTest,
        HasAlphaBlend = m.HasAlphaBlend,
        IsDoubleSided = m.IsDoubleSided,
        DepthWrite = m.DepthWrite,
        IsDecal = m.IsDecal,
        MaterialAlpha = m.MaterialAlpha,
        SrcBlendIndex = m.SrcBlendIndex,
        DstBlendIndex = m.DstBlendIndex,
        IsHairTintShader = m.IsHairTintShader,
        IsEye = m.IsEye,
        BipedSlots = m.BipedSlots,
        HidesSlots = m.HidesSlots,
        SlotDrawPriority = m.SlotDrawPriority,
        OverrideKey = m.OverrideKey,
        HiddenBySlotOccupancy = m.HiddenBySlotOccupancy,
        ShowWireframe = m.ShowWireframe,
        RenderAsWireframeFallback = m.RenderAsWireframeFallback,
        WireframeColorOverride = m.WireframeColorOverride,
        AlphaThreshold = m.AlphaThreshold,
        GreyscaleToPaletteScale = m.GreyscaleToPaletteScale,
        TintColor = m.TintColor,
        MaterialGlossiness = m.MaterialGlossiness,
        MaterialSpecularStrength = m.MaterialSpecularStrength,
        SpecularColor = m.SpecularColor,
        RimlightPower = m.RimlightPower,
        SubsurfaceRolloff = m.SubsurfaceRolloff,
        EmissiveColor = m.EmissiveColor,
        EmissiveMultiple = m.EmissiveMultiple,
        UvScale = m.UvScale,
        UvOffset = m.UvOffset,
        EnvMapScale = m.EnvMapScale,
        EyeCubemapScale = m.EyeCubemapScale,
        UseEyeCubemapScale = m.UseEyeCubemapScale,
        EnvMapTexture = m.EnvMapTexture,
        EnvMaskTexture = m.EnvMaskTexture,
        HasEnvironmentMap = m.HasEnvironmentMap,
        HasEnvMask = m.HasEnvMask,
        IsEnvMap2D = m.IsEnvMap2D,
        DetailTexture = m.DetailTexture,
        HasDetailMap = m.HasDetailMap,
        GlowTexture = m.GlowTexture,
        HasGlowMap = m.HasGlowMap,
        DiffuseEnabled = m.DiffuseEnabled,
        NormalEnabled = m.NormalEnabled,
        SkinEnabled = m.SkinEnabled,
        SpecularEnabled = m.SpecularEnabled,
        FaceTintEnabled = m.FaceTintEnabled,
        DetailEnabled = m.DetailEnabled,
        EnvMapEnabled = m.EnvMapEnabled,
        EmissiveEnabled = m.EmissiveEnabled,
        TintColorEnabled = m.TintColorEnabled,
        ShapeName = m.ShapeName,
        BodyPart = m.BodyPart,
        IsPrimaryHeadShape = m.IsPrimaryHeadShape,
        IsFaceShape = m.IsFaceShape,
        IsSkinShape = m.IsSkinShape,
        SkinTintAlpha = m.SkinTintAlpha,
        IsFaceWithEmptyDetailSlot = m.IsFaceWithEmptyDetailSlot,
        MeshSource = m.MeshSource,
    };

    internal void ApplyTo(GlMesh m)
    {
        m.DiffuseTexture = DiffuseTexture;
        m.NormalTexture = NormalTexture;
        m.SkinTexture = SkinTexture;
        m.SpecularTexture = SpecularTexture;
        m.FaceTintTexture = FaceTintTexture;
        m.HasNormalMap = HasNormalMap;
        m.HasSkinMap = HasSkinMap;
        m.HasSpecular = HasSpecular;
        m.HasSpecularMap = HasSpecularMap;
        m.HasFaceTintMap = HasFaceTintMap;
        m.HasGreyscaleToPalette = HasGreyscaleToPalette;
        m.HasTintColor = HasTintColor;
        m.HasEmissive = HasEmissive;
        m.IsModelSpace = IsModelSpace;
        m.HasHairSoftLighting = HasHairSoftLighting;
        m.HasSoftLighting = HasSoftLighting;
        m.HasRimLighting = HasRimLighting;
        m.HasVertexColors = HasVertexColors;
        m.UseAlphaTest = UseAlphaTest;
        m.HasAlphaBlend = HasAlphaBlend;
        m.IsDoubleSided = IsDoubleSided;
        m.DepthWrite = DepthWrite;
        m.IsDecal = IsDecal;
        m.MaterialAlpha = MaterialAlpha;
        m.SrcBlendIndex = SrcBlendIndex;
        m.DstBlendIndex = DstBlendIndex;
        m.IsHairTintShader = IsHairTintShader;
        m.IsEye = IsEye;
        m.BipedSlots = BipedSlots;
        m.HidesSlots = HidesSlots;
        m.SlotDrawPriority = SlotDrawPriority;
        m.OverrideKey = OverrideKey;
        m.HiddenBySlotOccupancy = HiddenBySlotOccupancy;
        m.ShowWireframe = ShowWireframe;
        m.RenderAsWireframeFallback = RenderAsWireframeFallback;
        m.WireframeColorOverride = WireframeColorOverride;
        m.AlphaThreshold = AlphaThreshold;
        m.GreyscaleToPaletteScale = GreyscaleToPaletteScale;
        m.TintColor = TintColor;
        m.MaterialGlossiness = MaterialGlossiness;
        m.MaterialSpecularStrength = MaterialSpecularStrength;
        m.SpecularColor = SpecularColor;
        m.RimlightPower = RimlightPower;
        m.SubsurfaceRolloff = SubsurfaceRolloff;
        m.EmissiveColor = EmissiveColor;
        m.EmissiveMultiple = EmissiveMultiple;
        m.UvScale = UvScale;
        m.UvOffset = UvOffset;
        m.EnvMapScale = EnvMapScale;
        m.EyeCubemapScale = EyeCubemapScale;
        m.UseEyeCubemapScale = UseEyeCubemapScale;
        m.EnvMapTexture = EnvMapTexture;
        m.EnvMaskTexture = EnvMaskTexture;
        m.HasEnvironmentMap = HasEnvironmentMap;
        m.HasEnvMask = HasEnvMask;
        m.IsEnvMap2D = IsEnvMap2D;
        m.DetailTexture = DetailTexture;
        m.HasDetailMap = HasDetailMap;
        m.GlowTexture = GlowTexture;
        m.HasGlowMap = HasGlowMap;
        m.DiffuseEnabled = DiffuseEnabled;
        m.NormalEnabled = NormalEnabled;
        m.SkinEnabled = SkinEnabled;
        m.SpecularEnabled = SpecularEnabled;
        m.FaceTintEnabled = FaceTintEnabled;
        m.DetailEnabled = DetailEnabled;
        m.EnvMapEnabled = EnvMapEnabled;
        m.EmissiveEnabled = EmissiveEnabled;
        m.TintColorEnabled = TintColorEnabled;
        m.ShapeName = ShapeName;
        m.BodyPart = BodyPart;
        m.IsPrimaryHeadShape = IsPrimaryHeadShape;
        m.IsFaceShape = IsFaceShape;
        m.IsSkinShape = IsSkinShape;
        m.SkinTintAlpha = SkinTintAlpha;
        m.IsFaceWithEmptyDetailSlot = IsFaceWithEmptyDetailSlot;
        m.MeshSource = MeshSource;
    }

    internal const int PropertyCount = 77;
}
