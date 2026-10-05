using HousecarlCore;

namespace HarnessShared;

// TEXTURE ALPHA FOR THE VISIBLE-CROSSING MEASURE (lane D, Stage D). The harness half of SurfaceVisibility: which texture a shape
// draws with, and its decoded alpha plane at mip 0.
//  * Path: the shape's diffuse (lighting) or source (effect) texture as SurfaceTexcoords read it, REPLACED by the winning base
//    record's alternate texture where one names that 3D shape (MODL alternate textures -> TXST diffuse), normalised to
//    "textures\...".
//  * Source: AssetResolver.ResolveForPlacement on the run's own resolver (the one the mesh was read with): Sources[0] is the VFS
//    winner, loose or BSA by MO2 precedence. A read-incomplete resolver, a missing file or an unread archive entry refuses.
//  * Decode: Pfim 0.11.4 (MIT, https://github.com/nickbabcock/Pfim, tag v0.11.4). API used, verified from that tag's sources:
//    Pfimage.FromStream(Stream) (src/Pfim/Pfimage.cs) -> IImage {Data, Width, Height, Stride, Format, MipMaps} (src/Pfim/IImage.cs);
//    ImageFormat {Rgb8, R5g5b5, R5g6b5, R5g5b5a1, Rgba16, Rgb24, Rgba32, R16f, R32f} (src/Pfim/ImageFormat.cs); DXT1 decodes to
//    Rgba32 with the punch-through index written as alpha 0 (src/Pfim/dds/Dxt1Dds.cs). Mip 0 is the plane at Data offset 0 with
//    Stride; Rgba32 carries alpha in byte 3 of each pixel. A format without an alpha channel (Rgb8, Rgb24, R5g6b5) reads alpha 255
//    everywhere, and the format is reported; any other format refuses by name.
//
// STATUS: API frozen at the lane D Gate B red gate; not implemented.

/// <summary>A base record's alternate texture for one 3D shape (Mutagen's Model.AlternateTextures entry): Name is the shape name,
/// Index the 3D index; DiffusePath the TXST's diffuse, or null when the TXST could not be resolved (refuses that shape).</summary>
public sealed record AlternateTexture(string Name, int Index, string? DiffusePath);

/// <summary>Where one shape's texture came from. Kind <c>loose</c> or <c>bsa</c>; Format the decoded Pfim format name.</summary>
public sealed record TextureProvenance(int Shape, string? Path, string? Provider, string? Kind, string? ArchivePath, string? Sha256,
                                       bool Ambiguous, string? Format, int Width, int Height, bool AlternateTexture, string? Refusal);

public static class TextureAlpha
{
    /// <summary>"textures\..." form of a NIF texture path (lower case, backslashes, a missing "textures\" prefix added; a leading
    /// "data\" removed). Null for an empty path.</summary>
    public static string? NormalizePath(string? path) =>
        throw new NotImplementedException("TextureAlpha.NormalizePath: lane D Gate B stub");

    /// <summary>Decode a DDS's alpha at mip 0. Format: the Pfim format name. Refusal names an unreadable or unsupported file.</summary>
    public static (AlphaPlane? Plane, string? Format, string? Refusal) DecodeDds(byte[] dds, string? source = null) =>
        throw new NotImplementedException("TextureAlpha.DecodeDds: lane D Gate B stub");

    /// <summary>The texture source per shape of one member, with provenance. <paramref name="read"/> resolves a "textures\..." path
    /// to the VFS winner's bytes and provenance (the run's AssetResolver; a test may pass a fixture reader).</summary>
    public static (ShapeTexture Texture, IReadOnlyList<TextureProvenance> Provenance) ForMember(
        SurfaceTexcoordSet texcoords, IReadOnlyList<AlternateTexture>? alternates,
        Func<string, (byte[]? Bytes, TextureProvenance Where)> read) =>
        throw new NotImplementedException("TextureAlpha.ForMember: lane D Gate B stub");

    /// <summary>The run's resolver as a reader for <see cref="ForMember"/>: ResolveForPlacement, Sources[0], read incomplete refuses.</summary>
    public static Func<string, (byte[]? Bytes, TextureProvenance Where)> Reader(AssetResolver assets) =>
        throw new NotImplementedException("TextureAlpha.Reader: lane D Gate B stub");
}
