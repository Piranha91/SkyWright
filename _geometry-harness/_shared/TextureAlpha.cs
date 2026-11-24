using HousecarlCore;
using SkyWright.Engine;

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
// STATUS: API frozen at the lane D Gate B red gate; implemented at Gate C.

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
    public static string? NormalizePath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return null;
        var p = path.Trim().Replace('/', '\\').ToLowerInvariant().TrimStart('\\');
        if (p.StartsWith("data\\", StringComparison.Ordinal)) p = p[5..];
        if (!p.StartsWith("textures\\", StringComparison.Ordinal)) p = "textures\\" + p;
        return p;
    }

    /// <summary>Decode a DDS's alpha at mip 0. Format: the Pfim format name. Refusal names an unreadable or unsupported file.</summary>
    public static (AlphaPlane? Plane, string? Format, string? Refusal) DecodeDds(byte[] dds, string? source = null)
    {
        if (dds is null || dds.Length < 4 || dds[0] != (byte)'D' || dds[1] != (byte)'D' || dds[2] != (byte)'S' || dds[3] != (byte)' ')
            return (null, null, "texture: not a DDS file" + (source is null ? "" : $" ({source})"));
        try
        {
            using var ms = new MemoryStream(dds, writable: false);
            using var image = Pfim.Pfimage.FromStream(ms);
            if (image.Compressed) image.Decompress();
            int w = image.Width, h = image.Height, stride = image.Stride;
            string fmt = image.Format.ToString();
            if (w <= 0 || h <= 0) return (null, fmt, "texture: empty image");
            var alpha = new byte[w * h];
            switch (image.Format)
            {
                case Pfim.ImageFormat.Rgba32:
                    if (image.DataLen < stride * (h - 1) + 4 * w) return (null, fmt, "texture: decoded data shorter than mip 0");
                    for (int y = 0; y < h; y++) for (int x = 0; x < w; x++) alpha[y * w + x] = image.Data[y * stride + 4 * x + 3];
                    break;
                case Pfim.ImageFormat.Rgb24:
                case Pfim.ImageFormat.Rgb8:
                case Pfim.ImageFormat.R5g6b5:
                    Array.Fill(alpha, (byte)255);   // no alpha channel: drawn everywhere (format reported)
                    break;
                default:
                    return (null, fmt, $"texture: decoded format {fmt} is not read for alpha");
            }
            return (new AlphaPlane(w, h, alpha, source), fmt, null);
        }
        catch (Exception ex) { return (null, null, $"texture: DDS unreadable ({ex.GetType().Name}: {ex.Message})"); }
    }

    /// <summary>The texture source per shape of one member, with provenance. <paramref name="read"/> resolves a "textures\..." path
    /// to the VFS winner's bytes and provenance (the run's AssetResolver; a test may pass a fixture reader).</summary>
    public static (ShapeTexture Texture, IReadOnlyList<TextureProvenance> Provenance) ForMember(
        SurfaceTexcoordSet texcoords, IReadOnlyList<AlternateTexture>? alternates,
        Func<string, (byte[]? Bytes, TextureProvenance Where)> read)
    {
        var results = new Dictionary<int, (AlphaPlane?, string?)>();
        var provenance = new List<TextureProvenance>();
        var decoded = new Dictionary<string, (AlphaPlane? Plane, TextureProvenance Where)>(StringComparer.Ordinal);
        var shapes = texcoords.Shapes ?? Array.Empty<ShapeAlphaRule>();
        foreach (var rule in shapes)
        {
            if (rule.Class is not (SurfaceMaterial.AlphaTest or SurfaceMaterial.AlphaBlend)) continue;
            var alts = alternates?.Where(a => string.Equals(a.Name, rule.Name, StringComparison.OrdinalIgnoreCase)).ToList() ?? new();
            bool isAlt = alts.Count > 0;
            string? refusal = null, raw = rule.TexturePath;
            if (alts.Select(a => a.DiffusePath).Distinct(StringComparer.OrdinalIgnoreCase).Count() > 1)
                refusal = $"shape '{rule.Name}': alternate textures disagree ({string.Join(", ", alts.Select(a => a.DiffusePath ?? "unresolved"))})";
            else if (isAlt && alts[0].DiffusePath is null) refusal = $"shape '{rule.Name}': its alternate texture set could not be resolved";
            else if (isAlt) raw = alts[0].DiffusePath;
            string? path = NormalizePath(raw);
            if (refusal is null && path is null) refusal = $"shape '{rule.Name}': no texture path";
            if (refusal is not null)
            {
                results[rule.Shape] = (null, refusal);
                provenance.Add(new TextureProvenance(rule.Shape, path, null, null, null, null, false, null, 0, 0, isAlt, refusal));
                continue;
            }
            if (!decoded.TryGetValue(path!, out var hit))
            {
                var (bytes, where) = read(path!);
                if (bytes is null) hit = (null, where with { Path = path, Refusal = where.Refusal ?? $"missing texture: {path}" });
                else
                {
                    var (plane, fmt, why) = DecodeDds(bytes, path);
                    hit = (plane, where with { Path = path, Format = fmt, Width = plane?.Width ?? 0, Height = plane?.Height ?? 0, Refusal = why ?? where.Refusal });
                }
                decoded[path!] = hit;
            }
            var prov = hit.Where with { Shape = rule.Shape, AlternateTexture = isAlt };
            provenance.Add(prov);
            results[rule.Shape] = (hit.Plane, hit.Plane is null ? prov.Refusal ?? "texture unreadable" : null);
        }
        ShapeTexture texture = shape => results.TryGetValue(shape, out var r) ? r : (null, $"shape {shape}: not an alpha shape, no texture read");
        return (texture, provenance);
    }

    /// <summary>The run's resolver as a reader for <see cref="ForMember"/>: ResolveForPlacement, Sources[0], read incomplete refuses.</summary>
    public static Func<string, (byte[]? Bytes, TextureProvenance Where)> Reader(AssetResolver assets) => path =>
    {
        TextureProvenance Fail(string why) => new(-1, path, null, null, null, null, false, null, 0, 0, false, why);
        PlacementResolution res;
        try { res = assets.ResolveForPlacement(path); }
        catch (ArgumentException ex) { return (null, Fail($"texture path rejected: {ex.Message}")); }
        if (res.ReadIncomplete) return (null, Fail($"texture: the VFS read is incomplete (an archive could not be read); {path} not trusted"));
        if (res.Sources.Count == 0) return (null, Fail($"missing texture: {path}"));
        var src = res.Sources[0];
        var (bytes, error) = AssetResolver.ReadPlacementSource(src);
        string kind = src.Kind == AssetKind.Loose ? "loose" : "bsa";
        if (bytes is null) return (null, new(-1, path, src.ProviderName, kind, src.ArchivePath, null, res.Ambiguous, null, 0, 0, false, $"texture read failed: {error}"));
        string sha = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bytes)).ToLowerInvariant();
        return (bytes, new(-1, path, src.ProviderName, kind, src.ArchivePath, sha, res.Ambiguous, null, 0, 0, false, null));
    };
}
