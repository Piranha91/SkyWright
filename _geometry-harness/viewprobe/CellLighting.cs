using System;
using System.Collections.Generic;
using System.Linq;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Skyrim;
using V3 = System.Numerics.Vector3;

namespace ViewProbe;

/// <summary>
/// --cell-lighting: light an interior from its own records instead of the fixed studio lights. The
/// target cell's XCLL (ambient, directional colour and rotation, fog), with each value the cell marks
/// as inherited taken from its lighting template (LTMP -> LGTM); the cell's image space (XCIM) as
/// SkyRender's image-space finish; and every drawn LIGH reference as a SkyRender point light (the
/// base's colour times its fade plus the reference's XLIG fade offset, its radius plus the reference's
/// XRDS; negative lights subtract; shadow-casting lights cast shadows). Off by default, so the
/// default output and the interior verify render are unchanged. Values are copied out of the
/// records during the walk, never held.
///
/// Not modelled: the six-direction ambient (the XCLL ambient colour lights every surface), light
/// fade distances, flicker, spot lights' cones, shadows from point lights, the per-object light
/// limit (every light reaches every surface in its radius), and eye adaptation (the adapted
/// luminance is --adapt-lum's, default 0.367, the exterior value).
/// </summary>
internal static class CellLighting
{
    /// <summary>One XCLL or LGTM's values.</summary>
    internal sealed record Values(V3 Ambient, V3 Directional, int RotXY, int RotZ, float DirectionalFade,
        V3 FogNearColor, V3 FogFarColor, float FogNear, float FogFar, float FogPower, float FogMax, V3[] Ambient6)
    {
        public override string ToString()
            => $"ambient {Ambient * 255f:0}, directional {Directional * 255f:0} x {DirectionalFade:0.##} at rotation {RotXY}/{RotZ}, "
             + $"fog {FogNearColor * 255f:0} -> {FogFarColor * 255f:0} over {FogNear:0}..{FogFar:0}, power {FogPower:0.##}, max {FogMax:0.##}";
    }

    internal sealed record CellRecord(Values Lighting, CellLighting.InheritSet Inherits, FormKey? Template, FormKey? ImageSpace, string Name);

    /// <summary>Which XCLL values the cell takes from its template (CellLighting.Inherit, by name).</summary>
    internal sealed record InheritSet(bool Ambient, bool Directional, bool FogColor, bool FogNear, bool FogFar,
        bool Rotation, bool Fade, bool FogPower, bool FogMax);

    /// <summary>--cell-lighting was given.</summary>
    internal static bool Requested;
    internal static readonly Dictionary<FormKey, (V3 Color, float Fade, float Radius, bool Negative, bool Shadowed, string Name)> Bases = new();
    /// <summary>Per placed light: its XLIG fade offset (added to the base's fade) and its XRDS radius
    /// offset (added to the base's radius), winner-takes-all.</summary>
    internal static readonly Dictionary<FormKey, (float FadeOffset, float Radius)> RefOverrides = new();
    internal static readonly Dictionary<FormKey, CellRecord> Cells = new();
    internal static readonly Dictionary<FormKey, Values> Templates = new();
    internal static readonly Dictionary<FormKey, SceneWeather.ImageSpaceValues> ImageSpaces = new();
    /// <summary>The interior cell being drawn (null for an exterior), set before the scene list is built.</summary>
    internal static FormKey? TargetCell;
    /// <summary>The drawn LIGH references (Skyrim coordinates), filled while the scene list is built.</summary>
    internal static readonly List<(HousecarlCore.Vec3 Position, FormKey Base, FormKey Ref)> Placed = new();

    /// <summary>One placed light's colour (times its fade, base fade plus the reference's offset),
    /// radius (the base's plus the reference's XRDS) and flags.</summary>
    internal static (V3 Color, float Radius, bool Negative, bool Shadowed, string Name) Effective(FormKey baseKey, FormKey refKey)
    {
        var b = Bases[baseKey];
        float fade = b.Fade, radius = b.Radius;
        if (RefOverrides.TryGetValue(refKey, out var o))
        {
            fade += o.FadeOffset;
            // A placed light's XRDS is an offset to the base's radius, not a replacement: the
            // Bannered Mare's values (candles +80, -3.4, +8.5; the hearths -189 and -304) only make
            // sense as offsets, and read so the in-game captures match (hearth-lit surfaces and the
            // unlit pillar beside the hearth; no source states it). Diagnostic: VP_XRDS=override
            // (a positive XRDS replaces the radius, a negative one is ignored: the earlier reading),
            // abs or ignore.
            switch (Environment.GetEnvironmentVariable("VP_XRDS"))
            {
                case "ignore": break;
                case "abs": if (o.Radius != 0f) radius = MathF.Abs(o.Radius); break;
                case "override": if (o.Radius > 0f) radius = o.Radius; break;
                default: radius = MathF.Max(radius + o.Radius, 0f); break;
            }
        }
        return (b.Color * MathF.Max(fade, 0f), radius, b.Negative, b.Shadowed, b.Name);
    }

    static V3 C(System.Drawing.Color c) => new(c.R / 255f, c.G / 255f, c.B / 255f);

    static V3[] Six(IAmbientColorsGetter? a) => a is null ? Array.Empty<V3>() : new[]
    {
        C(a.DirectionalXPlus), C(a.DirectionalXMinus), C(a.DirectionalYPlus),
        C(a.DirectionalYMinus), C(a.DirectionalZPlus), C(a.DirectionalZMinus),
    };

    /// <summary>The walk calls this for every record (winner-takes-all: later overrides replace).</summary>
    internal static void Note(object rec)
    {
        if (!Requested) return;
        switch (rec)
        {
            case ILightGetter l:
                Bases[l.FormKey] = (C(l.Color), l.FadeValue, l.Radius, l.Flags.HasFlag(Light.Flag.Negative),
                    (l.Flags & (Light.Flag.ShadowSpotlight | Light.Flag.ShadowHemisphere | Light.Flag.ShadowOmnidirectional)) != 0, l.EditorID ?? l.FormKey.ToString());
                break;
            case IPlacedObjectGetter po:
                float fo = po.LightData?.FadeOffset ?? 0f, rr = po.Radius ?? 0f;
                if (fo != 0f || rr != 0f) RefOverrides[po.FormKey] = (fo, rr);
                else RefOverrides.Remove(po.FormKey);
                break;
            case ILightingTemplateGetter t:
                Templates[t.FormKey] = new Values(C(t.AmbientColor), C(t.DirectionalColor), t.DirectionalRotationXY, t.DirectionalRotationZ,
                    t.DirectionalFade, C(t.FogNearColor), C(t.FogFarColor), t.FogNear, t.FogFar, t.FogPower, t.FogMax, Six(t.AmbientColors));
                break;
            case IImageSpaceGetter i:
                var h = i.Hdr; var ci = i.Cinematic; var ti = i.Tint;
                ImageSpaces[i.FormKey] = new SceneWeather.ImageSpaceValues(i.EditorID ?? i.FormKey.ToString(),
                    h?.White ?? 1f, h?.SunlightScale ?? 1f, h?.SkyScale ?? 1f,
                    ci?.Saturation ?? 1f, ci?.Brightness ?? 1f, ci?.Contrast ?? 1f,
                    ti is null ? V3.One : new V3(ti.Color.R / 255f, ti.Color.G / 255f, ti.Color.B / 255f), ti?.Amount ?? 0f);
                break;
            case ICellGetter c when c.Flags.HasFlag(Cell.Flag.IsInteriorCell) && c.Lighting is { } x:
                var inh = x.Inherits;
                Cells[c.FormKey] = new CellRecord(
                    new Values(C(x.AmbientColor), C(x.DirectionalColor), x.DirectionalRotationXY, x.DirectionalRotationZ, x.DirectionalFade,
                        C(x.FogNearColor), C(x.FogFarColor), x.FogNear, x.FogFar, x.FogPower, x.FogMax, Six(x.AmbientColors)),
                    new InheritSet(inh.HasFlag(Mutagen.Bethesda.Skyrim.CellLighting.Inherit.AmbientColor),
                                   inh.HasFlag(Mutagen.Bethesda.Skyrim.CellLighting.Inherit.DirectionalColor),
                                   inh.HasFlag(Mutagen.Bethesda.Skyrim.CellLighting.Inherit.FogColor),
                                   inh.HasFlag(Mutagen.Bethesda.Skyrim.CellLighting.Inherit.FogNear),
                                   inh.HasFlag(Mutagen.Bethesda.Skyrim.CellLighting.Inherit.FogFar),
                                   inh.HasFlag(Mutagen.Bethesda.Skyrim.CellLighting.Inherit.DirectionalRotation),
                                   inh.HasFlag(Mutagen.Bethesda.Skyrim.CellLighting.Inherit.DirectionalFade),
                                   inh.HasFlag(Mutagen.Bethesda.Skyrim.CellLighting.Inherit.FogPower),
                                   inh.HasFlag(Mutagen.Bethesda.Skyrim.CellLighting.Inherit.FogMax)),
                    c.LightingTemplate.FormKeyNullable is { IsNull: false } lt ? lt : null,
                    c.ImageSpace?.FormKeyNullable is { IsNull: false } im ? im : null,
                    c.EditorID ?? c.FormKey.ToString());
                break;
        }
    }

    /// <summary>The target cell's effective lighting (inherited values from its template), its image
    /// space, and a one-line description; null when the cell has no XCLL.</summary>
    internal static (Values Lighting, SceneWeather.ImageSpaceValues? ImageSpace, string Text)? Resolve(FormKey cell)
    {
        if (!Cells.TryGetValue(cell, out var cr)) return null;
        var v = cr.Lighting; var i = cr.Inherits;
        Values? t = cr.Template is { } tk && Templates.TryGetValue(tk, out var tv) ? tv : null;
        if (t is not null)
            v = v with
            {
                Ambient = i.Ambient ? t.Ambient : v.Ambient,
                Ambient6 = i.Ambient ? t.Ambient6 : v.Ambient6,
                Directional = i.Directional ? t.Directional : v.Directional,
                RotXY = i.Rotation ? t.RotXY : v.RotXY, RotZ = i.Rotation ? t.RotZ : v.RotZ,
                DirectionalFade = i.Fade ? t.DirectionalFade : v.DirectionalFade,
                FogNearColor = i.FogColor ? t.FogNearColor : v.FogNearColor,
                FogFarColor = i.FogColor ? t.FogFarColor : v.FogFarColor,
                FogNear = i.FogNear ? t.FogNear : v.FogNear, FogFar = i.FogFar ? t.FogFar : v.FogFar,
                FogPower = i.FogPower ? t.FogPower : v.FogPower, FogMax = i.FogMax ? t.FogMax : v.FogMax,
            };
        var isv = cr.ImageSpace is { } ik && ImageSpaces.TryGetValue(ik, out var iv) ? iv : null;
        var inheritsNamed = new[] { ("ambient", i.Ambient), ("directional", i.Directional), ("fog colour", i.FogColor), ("fog near", i.FogNear),
                                    ("fog far", i.FogFar), ("rotation", i.Rotation), ("fade", i.Fade), ("fog power", i.FogPower), ("fog max", i.FogMax) }
                             .Where(x => x.Item2).Select(x => x.Item1).ToArray();
        string text = $"{cr.Name}: {v}; template {(cr.Template?.ToString() ?? "none")}{(t is null && cr.Template is not null ? " (not found)" : "")}, "
                    + $"inherited: {(inheritsNamed.Length == 0 ? "nothing" : string.Join(", ", inheritsNamed))}; image space {isv?.Name ?? cr.ImageSpace?.ToString() ?? "none"}"
                    + (isv is null ? "" : $" (saturation {isv.Saturation:0.##}, brightness {isv.Brightness:0.##}, contrast {isv.Contrast:0.##}, white {isv.White:0.##}, tint x {isv.TintAmount:0.##})");
        return (v, isv, text);
    }
}
