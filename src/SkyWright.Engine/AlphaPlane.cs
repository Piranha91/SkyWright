using HousecarlCore;

namespace SkyWright.Engine;

// TEXTURE ALPHA PLANE (lane D, Stage D alpha sampling). The decoded alpha channel of one texture at the mip level a caller chose,
// row-major, row 0 = v 0 (the first stored row of the DDS; D3D texel convention, texel centres at half-integers). Core takes this
// plane, never an image package: decoding lives with the caller (the harness decodes DDS through Pfim).

/// <summary>How a texture coordinate outside [0, 1) is mapped on one axis.</summary>
public enum TextureWrap { Repeat, Clamp }

/// <summary>An alpha plane: Width x Height bytes, row-major, row 0 = v 0. Source names where it came from (for the output only).</summary>
public sealed record AlphaPlane(int Width, int Height, byte[] Alpha, string? Source = null)
{
    /// <summary>The byte at texel (x, y) with the wrap mode applied on each axis.</summary>
    public byte At(int x, int y, TextureWrap wrapU, TextureWrap wrapV)
    {
        x = wrapU == TextureWrap.Clamp ? Math.Clamp(x, 0, Width - 1) : ((x % Width) + Width) % Width;
        y = wrapV == TextureWrap.Clamp ? Math.Clamp(y, 0, Height - 1) : ((y % Height) + Height) % Height;
        return Alpha[y * Width + x];
    }
}
