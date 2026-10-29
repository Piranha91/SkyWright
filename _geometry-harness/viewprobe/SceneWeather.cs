using System;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Skyrim;
using V3 = System.Numerics.Vector3;

namespace ViewProbe;

/// <summary>
/// --weather NAME [--weather-slot day|sunrise|sunset|night]: one WTHR's colours and fog for one
/// time of day, read from the winning record. It sets the frame's sky (also what water reflects),
/// the sun's colour, the ambient colour and SkyRender's distance fog. Without --weather nothing
/// changes. Values are copied out of the record, never held (the walk disposes its overlays).
///
/// What is used, and how far it goes: the sky colour is WTHR's sky-upper colour (what a water
/// surface seen from above reflects); the ambient is the directional ambient's +Z colour (what an
/// up-facing surface receives), not the six-direction ambient; fog is the slot's fog near/far
/// colours with the day or night fog distances (WTHR has only those two; sunrise and sunset take
/// the day distances). The slot's image space (IMGS) scales the sun by its HDR sunlight scale and
/// grades the frame (SkyRender ImageSpaceFinish; --no-imagespace turns it off). Not modelled: bloom,
/// eye adaptation over time, clouds, and blending between slots. The sun's direction comes from the
/// target worldspace's climate at --game-hour (default 12; see SunPath).
/// </summary>
internal sealed record SceneWeather(string Name, string Slot, V3 SkyUpper, V3 Sunlight, V3 AmbientUp,
    V3 FogNearColor, V3 FogFarColor, float FogNear, float FogFar, float FogPower, float FogMax)
{
    /// <summary>The slot's image space (IMGS), or null.</summary>
    internal FormKey? ImageSpace { get; init; }
    /// <summary>The slot's six directional ambient colours (+X, -X, +Y, -Y, +Z, -Z), printed.</summary>
    internal V3[] Ambient6 { get; init; } = Array.Empty<V3>();
    /// <summary>That image space's values once the walk has seen it (printed).</summary>
    internal static string? ImageSpaceText;
    /// <summary>That image space's HDR / cinematic / tint values, applied as SkyRender's image-space
    /// finish (and its sunlight scale on the sun) unless --no-imagespace.</summary>
    internal static ImageSpaceValues? ImageSpaceData;
    /// <summary>--no-imagespace: light from the weather but leave the frame ungraded.</summary>
    internal static bool ImageSpaceOff;
    /// <summary>--adapt-lum X: the adapted scene luminance (the engine's contrast pivot). The default
    /// is the value read from the retail adaptation texture at SkyrimClear noon (0.36-0.375 over
    /// the frame; see the viewprobe README), since one frame has no adaptation history.</summary>
    internal static float AdaptedLuminance = 0.367f;
    /// <summary>The --weather argument (an EditorID or a FormKey such as 00081A:Skyrim.esm), or null.</summary>
    internal static string? Requested;
    /// <summary>The --weather-slot argument.</summary>
    internal static string RequestedSlot = "day";
    /// <summary>--sky was given: it wins over the weather's sky colour.</summary>
    internal static bool SkyGiven;
    /// <summary>The winning record's values once the walk has seen it.</summary>
    internal static SceneWeather? Current;
    /// <summary>--game-hour H: the hour the sun is placed for (default 12, the in-game captures').</summary>
    internal static float GameHour = 12f;
    /// <summary>The target worldspace's climate (WRLD.Climate), once the walk has seen the worldspace.</summary>
    internal static FormKey? WorldClimate;
    /// <summary>Every climate's sunrise and sunset (begin, end), in hours, winner-takes-all.</summary>
    internal static readonly System.Collections.Generic.Dictionary<FormKey, (float RiseBegin, float RiseEnd, float SetBegin, float SetEnd, string Name)> Climates = new();

    internal static void NoteClimate(IClimateGetter c)
    {
        static float H(TimeOnly t) => t.Hour + t.Minute / 60f;
        Climates[c.FormKey] = (H(c.SunriseBegin), H(c.SunriseEnd), H(c.SunsetBegin), H(c.SunsetEnd), c.EditorID ?? c.FormKey.ToString());
    }

    /// <summary>The sun's direction at <see cref="GameHour"/> under the worldspace's climate, as
    /// azimuth / elevation for SkyRender's key light (azimuth 0 = Skyrim -Y, 90 = +X), or null without
    /// a climate. Community Shaders' sun path (SkySync, its "vanilla" trajectory): sunrise = the mean
    /// of the climate's sunrise begin and end less a quarter hour, sunset = the mean of the sunset
    /// begin and end plus a quarter hour, phi = pi (hour - sunrise) / (sunset - sunrise), and the
    /// direction (cos phi, -sin phi cos 95deg, sin phi sin 95deg) in Skyrim axes: east at sunrise,
    /// 5 degrees off the zenith toward +Y at the top of the arc.</summary>
    internal static (float Azimuth, float Elevation, string Text)? SunPath()
    {
        if (WorldClimate is not { } k || !Climates.TryGetValue(k, out var c)) return null;
        float rise = (c.RiseBegin + c.RiseEnd) * 0.5f - 0.25f, set = (c.SetBegin + c.SetEnd) * 0.5f + 0.25f;
        if (set <= rise) return null;
        double phi = Math.PI * (GameHour - rise) / (set - rise);
        double tilt = 95.0 * Math.PI / 180.0;
        double x = Math.Cos(phi), y = -Math.Sin(phi) * Math.Cos(tilt), z = Math.Sin(phi) * Math.Sin(tilt);
        double len = Math.Sqrt(x * x + y * y + z * z);
        x /= len; y /= len; z /= len;
        float el = (float)(Math.Asin(z) * 180.0 / Math.PI);
        float az = (float)(Math.Atan2(x, -y) * 180.0 / Math.PI);
        return (az, el, $"{c.Name}: sunrise {rise:0.##} h, sunset {set:0.##} h; at {GameHour:0.##} h the sun is at azimuth {az:0.#} elevation {el:0.#}");
    }

    internal static bool Matches(IWeatherGetter w)
        => Requested is { } rq
           && (string.Equals(w.EditorID, rq, StringComparison.OrdinalIgnoreCase)
               || (FormKey.TryFactory(rq, out var fk) && fk == w.FormKey));

    internal static SceneWeather From(IWeatherGetter w, string slot)
    {
        static V3 C(System.Drawing.Color c) => new(c.R / 255f, c.G / 255f, c.B / 255f);
        V3 Pick(IWeatherColorGetter wc) => slot switch
        {
            "sunrise" => C(wc.Sunrise), "sunset" => C(wc.Sunset), "night" => C(wc.Night), _ => C(wc.Day),
        };
        var amb = w.DirectionalAmbientLightingColors;
        var ambSlot = slot switch { "sunrise" => amb.Sunrise, "sunset" => amb.Sunset, "night" => amb.Night, _ => amb.Day };
        bool night = slot == "night";
        var isp = w.ImageSpaces;
        var ispKey = isp is null ? null : (slot switch { "sunrise" => isp.Sunrise, "sunset" => isp.Sunset, "night" => isp.Night, _ => isp.Day }).FormKeyNullable;
        return new SceneWeather(w.EditorID ?? w.FormKey.ToString(), slot,
            Pick(w.SkyUpperColor), Pick(w.SunlightColor), C(ambSlot.DirectionalZPlus),
            Pick(w.FogNearColor), Pick(w.FogFarColor),
            night ? w.FogDistanceNightNear : w.FogDistanceDayNear,
            night ? w.FogDistanceNightFar : w.FogDistanceDayFar,
            night ? w.FogDistanceNightPower : w.FogDistanceDayPower,
            night ? w.FogDistanceNightMax : w.FogDistanceDayMax)
        {
            ImageSpace = ispKey,
            Ambient6 = new[] { C(ambSlot.DirectionalXPlus), C(ambSlot.DirectionalXMinus), C(ambSlot.DirectionalYPlus),
                               C(ambSlot.DirectionalYMinus), C(ambSlot.DirectionalZPlus), C(ambSlot.DirectionalZMinus) },
        };
    }

    /// <summary>Notes an IMGS's values when it is the weather's image space (the walk calls this for
    /// every IMGS; the last, winning, one is kept).</summary>
    internal static void NoteImageSpace(IImageSpaceGetter i, FormKey? wanted)
    {
        if (wanted is not { } k || i.FormKey != k) return;
        var h = i.Hdr; var c = i.Cinematic; var t = i.Tint;
        ImageSpaceData = new ImageSpaceValues(i.EditorID ?? i.FormKey.ToString(),
            h?.White ?? 1f, h?.SunlightScale ?? 1f, h?.SkyScale ?? 1f,
            c?.Saturation ?? 1f, c?.Brightness ?? 1f, c?.Contrast ?? 1f,
            t is null ? V3.One : new V3(t.Color.R / 255f, t.Color.G / 255f, t.Color.B / 255f), t?.Amount ?? 0f);
        ImageSpaceText = $"{i.EditorID ?? i.FormKey.ToString()}: HDR eye adapt speed {h?.EyeAdaptSpeed:0.###} strength {h?.EyeAdaptStrength:0.###}, "
                       + $"bloom blur {h?.BloomBlurRadius:0.###} scale {h?.BloomScale:0.###} threshold {h?.BloomThreshold:0.###} receive {h?.ReceiveBloomThreshold:0.###}, "
                       + $"white {h?.White:0.###}, sunlight scale {h?.SunlightScale:0.###}, sky scale {h?.SkyScale:0.###}; "
                       + $"cinematic saturation {c?.Saturation:0.###} brightness {c?.Brightness:0.###} contrast {c?.Contrast:0.###}; "
                       + $"tint {t?.Color} x {t?.Amount:0.###}";
    }

    /// <summary>One IMGS's values, copied out of the record.</summary>
    internal sealed record ImageSpaceValues(string Name, float White, float SunlightScale, float SkyScale,
        float Saturation, float Brightness, float Contrast, V3 TintColor, float TintAmount);

    public override string ToString()
        => $"{Name} ({Slot}): sky {SkyUpper:0.000}, sun {Sunlight:0.000}, ambient +Z {AmbientUp:0.000}, "
         + $"fog {FogNearColor:0.000} -> {FogFarColor:0.000} over {FogNear:0}..{FogFar:0} u, power {FogPower:0.##}, max {FogMax:0.##}"
         + (Ambient6.Length == 6 ? $"; directional ambient +X {Ambient6[0]:0.000} -X {Ambient6[1]:0.000} +Y {Ambient6[2]:0.000} "
                                 + $"-Y {Ambient6[3]:0.000} +Z {Ambient6[4]:0.000} -Z {Ambient6[5]:0.000}" : "");
}
