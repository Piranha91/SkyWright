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
/// the day distances). Not modelled: the weather's image space (HDR, tint, exposure), clouds, the
/// sun's direction from the climate, and blending between slots.
/// </summary>
internal sealed record SceneWeather(string Name, string Slot, V3 SkyUpper, V3 Sunlight, V3 AmbientUp,
    V3 FogNearColor, V3 FogFarColor, float FogNear, float FogFar, float FogPower, float FogMax)
{
    /// <summary>The --weather argument (an EditorID or a FormKey such as 00081A:Skyrim.esm), or null.</summary>
    internal static string? Requested;
    /// <summary>The --weather-slot argument.</summary>
    internal static string RequestedSlot = "day";
    /// <summary>--sky was given: it wins over the weather's sky colour.</summary>
    internal static bool SkyGiven;
    /// <summary>The winning record's values once the walk has seen it.</summary>
    internal static SceneWeather? Current;

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
        return new SceneWeather(w.EditorID ?? w.FormKey.ToString(), slot,
            Pick(w.SkyUpperColor), Pick(w.SunlightColor), C(ambSlot.DirectionalZPlus),
            Pick(w.FogNearColor), Pick(w.FogFarColor),
            night ? w.FogDistanceNightNear : w.FogDistanceDayNear,
            night ? w.FogDistanceNightFar : w.FogDistanceDayFar,
            night ? w.FogDistanceNightPower : w.FogDistanceDayPower,
            night ? w.FogDistanceNightMax : w.FogDistanceDayMax);
    }

    public override string ToString()
        => $"{Name} ({Slot}): sky {SkyUpper:0.000}, sun {Sunlight:0.000}, ambient +Z {AmbientUp:0.000}, "
         + $"fog {FogNearColor:0.000} -> {FogFarColor:0.000} over {FogNear:0}..{FogFar:0} u, power {FogPower:0.##}, max {FogMax:0.##}";
}
