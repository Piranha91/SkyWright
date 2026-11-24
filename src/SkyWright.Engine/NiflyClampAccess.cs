using System.Reflection;
using NiflySharp.Blocks;
using NiflySharp.Enums;

using HousecarlCore;

namespace SkyWright.Engine;

// THE ONE PLACE that reads BSLightingShaderProperty's texture clamp mode (lane D, Gate B review F1). Nifly 1.1.0 exposes no public
// accessor for it, only the generated private field `_textureClampMode` (listed in the package's XML doc). A missing or renamed field,
// or a value that is not a TexClampMode, refuses with a named reason; there is never a default. proofprobe's alpha K10 fails if the
// field moves: re-check this accessor on any Nifly bump.
internal static class NiflyClampAccess
{
    static readonly FieldInfo? Field = typeof(BSLightingShaderProperty).GetField("_textureClampMode", BindingFlags.Instance | BindingFlags.NonPublic);

    /// <summary>The lighting shader's clamp mode, or the reason it cannot be read.</summary>
    public static (TexClampMode? Mode, string? Refusal) LightingClampMode(BSLightingShaderProperty shader)
    {
        if (Field is null) return (null, "texture clamp mode: Nifly's BSLightingShaderProperty._textureClampMode field is missing (re-check NiflyClampAccess after a Nifly bump)");
        object? raw;
        try { raw = Field.GetValue(shader); }
        catch (Exception ex) { return (null, $"texture clamp mode: the field could not be read ({ex.GetType().Name})"); }
        if (raw is null) return (null, "texture clamp mode: the field is null");
        long value;
        try { value = Convert.ToInt64(raw is Enum e ? Convert.ChangeType(e, Enum.GetUnderlyingType(e.GetType())) : raw); }
        catch { return (null, $"texture clamp mode: the field's type {raw.GetType().Name} is not numeric"); }
        return FromNumber(value);
    }

    /// <summary>A stored clamp-mode number as a TexClampMode, or the reason it is not one.</summary>
    public static (TexClampMode? Mode, string? Refusal) FromNumber(long value)
    {
        var mode = (TexClampMode)Enum.ToObject(typeof(TexClampMode), value);
        if (!Enum.IsDefined(typeof(TexClampMode), mode)) return (null, $"texture clamp mode: value {value} is not a TexClampMode");
        return (mode, null);
    }

    /// <summary>The wrap per axis of a clamp mode (S = U, T = V).</summary>
    public static (TextureWrap U, TextureWrap V) Wraps(TexClampMode mode) => mode switch
    {
        TexClampMode.CLAMP_S_CLAMP_T => (TextureWrap.Clamp, TextureWrap.Clamp),
        TexClampMode.CLAMP_S_WRAP_T => (TextureWrap.Clamp, TextureWrap.Repeat),
        TexClampMode.WRAP_S_CLAMP_T => (TextureWrap.Repeat, TextureWrap.Clamp),
        TexClampMode.WRAP_S_WRAP_T => (TextureWrap.Repeat, TextureWrap.Repeat),
        _ => throw new ArgumentOutOfRangeException(nameof(mode), mode, "not a TexClampMode"),
    };
}
