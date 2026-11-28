using SkyRender;

namespace ViewProbe;

/// <summary>
/// The key under which SkyRender's <see cref="ResidentTextureCache"/> holds a texture that
/// <see cref="GlTextureManager.LoadTexture"/> uploaded, so the retained scene (BuiltScene.cs) can find and touch the
/// entry it was given. SkyRender 4.0.0 exposes no public way to learn that key: the upload diagnostics name the disk
/// path, and the cache lists no keys. On the decoded policy the key is the resolved disk path; on a compressed policy the
/// library prefixes it with its authored-mip version and the policy's limit (GlTextureManager.LoadTexture,
/// <c>dds-authored-v2:{limit}:</c>), whichever route the texture then took.
///
/// <para>This restates that one library convention, in this one place. <c>--resident-key-selftest</c>
/// (ResidentKeySelfTest.cs) uploads a real texture through <see cref="GlTextureManager"/> under each policy and looks the
/// entry up through this helper, so a changed convention fails that test by name; in a render a wrong key never passes
/// silently either, because the retained manifest then refuses before the draw. Retire it when SkyRender exposes the
/// key (a lead filed for SkyRender).</para>
/// </summary>
internal static class ResidentKey
{
    internal static string? For(AssetCache cache, string? diskPath) =>
        diskPath is not null && cache.DdsMaximumFinalMipDimension is int limit
            ? FormattableString.Invariant($"dds-authored-v2:{limit}:{diskPath}")
            : diskPath;
}
