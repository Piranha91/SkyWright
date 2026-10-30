using System;
using System.Linq;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Skyrim;
using V3 = System.Numerics.Vector3;

namespace ViewProbe;

/// <summary>
/// The exterior's weather (on by default; --no-weather turns it off): one WTHR's colours and fog for
/// one time of day (--weather-slot day|sunrise|sunset|night), read from the winning record. It sets
/// the frame's sky (also what water reflects), the sun's colour, the ambient colour and SkyRender's
/// distance fog. Which WTHR: --weather NAME; else the most likely weather of the target cell's
/// region (CELL XCLR -> REGN weather data: the highest-priority region that has weather, its
/// highest-chance entry), which is where vanilla exteriors get their weather (the Tamriel climate
/// lists SkyrimCloudy alone); else the most likely weather of the worldspace's climate (WRLD CNAM
/// -> CLMT); else SkyrimClear. Interiors take no weather (their own lighting is --cell-lighting). Values are
/// copied out of the record, never held (the walk disposes its overlays).
///
/// What is used, and how far it goes: the sky is a gradient by elevation (SkyKeys; the flat sky-upper colour with VP_SKY_FLAT=1: what a water
/// surface seen from above reflects); the ambient is the directional ambient's "Z-" colour, the
/// light travelling from the sky down (what an up-facing surface receives), not the six-direction
/// ambient; fog is the slot's fog near/far
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
    /// <summary>The slot's sky-lower and horizon colours (the sky dome's other two gradient colours).</summary>
    internal V3 SkyLower { get; init; }
    internal V3 Horizon { get; init; }
    /// <summary>The slot's water-multiplier colour (printed).</summary>
    internal V3 WaterMultiplier { get; init; }
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
    /// <summary>Without --weather: pick the exterior's weather from its climate (default on;
    /// --no-weather turns it off).</summary>
    internal static bool Auto = true;
    /// <summary>The weather used when the climate gives none (EditorID).</summary>
    internal const string FallbackWeather = "SkyrimClear";
    /// <summary>Where <see cref="Current"/> came from, for the console line.</summary>
    internal static string? CurrentWhy;
    /// <summary>Every WTHR's values for the requested slot, winner-takes-all, keyed by FormKey; and
    /// EditorID -> FormKey. Kept so the weather can be chosen after the walk (it depends on the
    /// worldspace's climate, which the walk reaches at any point).</summary>
    internal static readonly System.Collections.Generic.Dictionary<FormKey, SceneWeather> Weathers = new();
    internal static readonly System.Collections.Generic.Dictionary<string, FormKey> WeatherIds = new(StringComparer.OrdinalIgnoreCase);
    /// <summary>Every climate's weather list (weather, chance), winner-takes-all.</summary>
    internal static readonly System.Collections.Generic.Dictionary<FormKey, (FormKey Weather, int Chance)[]> ClimateWeathers = new();
    /// <summary>Every region's weather data (priority, list), winner-takes-all; and every cell's
    /// region list (XCLR), winner-takes-all.</summary>
    internal static readonly System.Collections.Generic.Dictionary<FormKey, (int Priority, string Name, (FormKey Weather, int Chance)[] List)> RegionWeathers = new();
    internal static readonly System.Collections.Generic.Dictionary<FormKey, FormKey[]> CellRegions = new();

    /// <summary>The walk calls this for every REGN.</summary>
    internal static void NoteRegion(IRegionGetter r)
    {
        if (r.Weather is { } rw && rw.Weathers is { Count: > 0 } ws)
            RegionWeathers[r.FormKey] = (rw.Priority, r.EditorID ?? r.FormKey.ToString(), ws.Select(t => (t.Weather.FormKey, t.Chance)).ToArray());
        else RegionWeathers.Remove(r.FormKey);
    }

    /// <summary>The walk calls this for every CELL.</summary>
    internal static void NoteCell(ICellGetter c)
    {
        if (c.Regions is { Count: > 0 } rs) CellRegions[c.FormKey] = rs.Select(l => l.FormKey).ToArray();
        else CellRegions.Remove(c.FormKey);
    }

    /// <summary>Every IMGS's values and text, winner-takes-all.</summary>
    internal static readonly System.Collections.Generic.Dictionary<FormKey, (ImageSpaceValues Values, string Text)> ImageSpaces = new();

    /// <summary>The walk calls this for every WTHR.</summary>
    internal static void NoteWeather(IWeatherGetter w)
    {
        Weathers[w.FormKey] = From(w, RequestedSlot);
        if (!string.IsNullOrWhiteSpace(w.EditorID)) WeatherIds[w.EditorID!] = w.FormKey;
    }

    /// <summary>After the walk: choose <see cref="Current"/> (and its image space) for the target.
    /// --weather wins; otherwise, for an exterior with <see cref="Auto"/>, the climate's most likely
    /// weather, else <see cref="FallbackWeather"/>. An interior gets none unless --weather names one.</summary>
    internal static void Resolve(bool interior, FormKey? cell)
    {
        Current = null; CurrentWhy = null; ImageSpaceData = null; ImageSpaceText = null;
        FormKey? pick = null;
        if (Requested is { } rq)
        {
            if (WeatherIds.TryGetValue(rq, out var byId)) pick = byId;
            else if (FormKey.TryFactory(rq, out var fk) && Weathers.ContainsKey(fk)) pick = fk;
            CurrentWhy = "--weather";
        }
        else if (Auto && !interior)
        {
            if (cell is { } cfk && CellRegions.TryGetValue(cfk, out var regions))
            {
                var best = regions.Where(RegionWeathers.ContainsKey).Select(k => RegionWeathers[k])
                                  .OrderByDescending(r => r.Priority).FirstOrDefault();
                if (best.List is { Length: > 0 })
                    foreach (var e in best.List.OrderByDescending(e => e.Chance))
                        if (Weathers.ContainsKey(e.Weather))
                        {
                            pick = e.Weather;
                            CurrentWhy = $"the cell's region {best.Name}'s most likely weather (chance {e.Chance} of {best.List.Sum(x => x.Chance)})";
                            break;
                        }
            }
            if (pick is null && WorldClimate is { } ck && ClimateWeathers.TryGetValue(ck, out var list))
            {
                if (Environment.GetEnvironmentVariable("VP_WEATHER_LIST") == "1")
                    foreach (var e in list)
                        Console.WriteLine($"   climate weather: {e.Weather} {(Weathers.TryGetValue(e.Weather, out var lw) ? lw.Name : "?")} chance {e.Chance}");
                foreach (var e in list.OrderByDescending(e => e.Chance))
                    if (Weathers.ContainsKey(e.Weather))
                    {
                        pick = e.Weather;
                        CurrentWhy = $"the worldspace climate {(Climates.TryGetValue(ck, out var cn) ? cn.Name : ck.ToString())}'s most likely weather (chance {e.Chance} of {list.Sum(x => x.Chance)})";
                        break;
                    }
            }
            if (pick is null && WeatherIds.TryGetValue(FallbackWeather, out var fb))
            {
                pick = fb;
                CurrentWhy = "fallback (no climate weather)";
            }
        }
        if (pick is { } k && Weathers.TryGetValue(k, out var sw))
        {
            Current = sw;
            if (sw.ImageSpace is { } ik && ImageSpaces.TryGetValue(ik, out var isv))
            {
                ImageSpaceData = isv.Values;
                ImageSpaceText = isv.Text;
            }
        }
    }
    /// <summary>The --weather-slot argument.</summary>
    internal static string RequestedSlot = "day";
    /// <summary>--sky was given: it wins over the weather's sky colour.</summary>
    internal static bool SkyGiven;
    /// <summary>The winning record's values once the walk has seen it.</summary>
    internal static SceneWeather? Current;
    /// <summary>--shadows / --no-shadows: the sun's shadow map. Unset, it is on whenever a weather lights
    /// the frame (the default for exteriors) and off without one (interiors).</summary>
    internal static bool? ShadowsRequested;
    /// <summary>--shadow-distance D: how far from the eye the sun's shadow map reaches (default 10000,
    /// the lab profile's fShadowDistance).</summary>
    internal static float ShadowDistance = 10000f;
    /// <summary>How far beyond the frame's ground the shadow map reaches for casters (world units).</summary>
    internal static float CasterMargin = 1500f;
    internal static bool ShadowsOn => ShadowsRequested ?? Current is not null;

    /// <summary>The sphere (Skyrim coordinates) the sun's shadow map covers for one frame: the eye and
    /// the frame's view rays out to where they meet the ground (the terrain under the eye, or the
    /// look point's height) or <paramref name="reach"/>, whichever is nearer. One 2048-texel map
    /// over that sphere stands in for the engine's cascades.</summary>
    internal static (V3 Center, float Radius) ShadowSphere(V3 eye, V3 look, float vfovDeg, float aspect, float groundZ, float reach)
    {
        var fwd = V3.Normalize(look - eye);
        var right = V3.Normalize(V3.Cross(fwd, V3.UnitZ));
        if (float.IsNaN(right.X)) right = V3.UnitX;
        var up = V3.Cross(right, fwd);
        float ty = MathF.Tan(vfovDeg * MathF.PI / 360f), tx = ty * aspect;
        var pts = new System.Collections.Generic.List<V3> { eye };
        for (int iy = -2; iy <= 2; iy++)
            for (int ix = -2; ix <= 2; ix++)
            {
                var d = V3.Normalize(fwd + right * (tx * ix / 2f) + up * (ty * iy / 2f));
                float t = reach;
                if (d.Z < -1e-4f) t = MathF.Min(t, (groundZ - eye.Z) / d.Z);
                pts.Add(eye + d * MathF.Max(t, 0f));
            }
        var min = pts[0]; var max = pts[0];
        foreach (var p in pts) { min = V3.Min(min, p); max = V3.Max(max, p); }
        var c = (min + max) * 0.5f;
        float r = 0f;
        foreach (var p in pts) r = MathF.Max(r, V3.Distance(p, c));
        // Casters stand outside the frame too (a tree beside it shades the ground in it): widen by
        // CasterMargin, a tall tree's height.
        return (c, r + CasterMargin);
    }

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
        ClimateWeathers[c.FormKey] = c.WeatherTypes.Select(t => (t.Weather.FormKey, t.Chance)).ToArray();
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
            // The ambient on an up-facing surface: the DALC "Z-" colour, the light travelling from the
            // sky to the ground (the Step weather reference's definition of DALC Z-; the in-game
            // Riverwood aerial matches it, where "Z+" left the ground underlit and cyan).
            Pick(w.SkyUpperColor), Pick(w.SunlightColor), C(ambSlot.DirectionalZMinus),
            Pick(w.FogNearColor), Pick(w.FogFarColor),
            night ? w.FogDistanceNightNear : w.FogDistanceDayNear,
            night ? w.FogDistanceNightFar : w.FogDistanceDayFar,
            night ? w.FogDistanceNightPower : w.FogDistanceDayPower,
            night ? w.FogDistanceNightMax : w.FogDistanceDayMax)
        {
            ImageSpace = ispKey,
            SkyLower = Pick(w.SkyLowerColor), Horizon = Pick(w.HorizonColor), WaterMultiplier = Pick(w.WaterMultiplierColor),
            Ambient6 = new[] { C(ambSlot.DirectionalXPlus), C(ambSlot.DirectionalXMinus), C(ambSlot.DirectionalYPlus),
                               C(ambSlot.DirectionalYMinus), C(ambSlot.DirectionalZPlus), C(ambSlot.DirectionalZMinus) },
        };
    }

    /// <summary>The vanilla sky dome's blend masks by elevation (degrees above the dome's rim): the
    /// vertex colours of <c>meshes/sky/atmosphere.nif</c> (Skyrim - Meshes0.bsa), averaged per ring.
    /// Red weights the horizon colour, green the sky-lower colour, blue the sky-upper colour; the
    /// engine's sky shader sums BlendColor[i] x mask[i] per vertex (Community Shaders Sky.hlsl).</summary>
    internal static readonly (float El, float H, float L, float U)[] DomeMasks =
    {
        (0f, 1f, 0f, 0f), (2.1f, 1f, 0f, 0f), (3.6f, 0.933f, 0.071f, 0f), (5.6f, 0.804f, 0.220f, 0f),
        (9.7f, 0.490f, 0.573f, 0f), (14.5f, 0.157f, 0.910f, 0f), (18.0f, 0.020f, 1f, 0.016f),
        (26.8f, 0f, 0.769f, 0.306f), (34.9f, 0f, 0.471f, 0.584f), (47.0f, 0f, 0.184f, 0.831f),
        (59.1f, 0f, 0.063f, 0.941f), (74.0f, 0f, 0.012f, 0.984f), (90f, 0f, 0f, 1f),
    };

    /// <summary>The sky by elevation for SkyRender's SkyGradient (what sky-reflecting water reflects),
    /// one key per dome ring. Each key is the dome colour (horizon, sky-lower and sky-upper mixed by
    /// the ring's masks) plus the image space's sky scale (the sky shader adds it after the gradient),
    /// seen through the weather's far fog at its maximum: (1 - max) x dome + max x fog far, then the
    /// sky scale. The fog over the sky is NOT sourced: it is what makes the sky in the in-game
    /// Riverwood captures match (the bare dome is far darker and bluer than the game's sky and
    /// river); the engine's Atmosphere object does carry a BSFogProperty (CommonLibSSE). With
    /// <paramref name="fog"/> false the keys are the bare dome plus the sky scale.</summary>
    internal (float El, V3 Color)[] SkyKeys(float skyScale, bool fog)
    {
        var keys = new (float, V3)[DomeMasks.Length];
        for (int i = 0; i < DomeMasks.Length; i++)
        {
            var m = DomeMasks[i];
            var dome = Horizon * m.H + SkyLower * m.L + SkyUpper * m.U;
            var c = fog ? dome * (1f - FogMax) + FogFarColor * FogMax : dome;
            keys[i] = (m.El, c + new V3(skyScale));
        }
        return keys;
    }

    /// <summary>Notes an IMGS's values (the walk calls this for every IMGS; the last, winning, one is
    /// kept); <see cref="Resolve"/> picks the weather's.</summary>
    internal static void NoteImageSpace(IImageSpaceGetter i)
    {
        var h = i.Hdr; var c = i.Cinematic; var t = i.Tint;
        var values = new ImageSpaceValues(i.EditorID ?? i.FormKey.ToString(),
            h?.White ?? 1f, h?.SunlightScale ?? 1f, h?.SkyScale ?? 1f,
            c?.Saturation ?? 1f, c?.Brightness ?? 1f, c?.Contrast ?? 1f,
            t is null ? V3.One : new V3(t.Color.R / 255f, t.Color.G / 255f, t.Color.B / 255f), t?.Amount ?? 0f);
        var text = $"{i.EditorID ?? i.FormKey.ToString()}: HDR eye adapt speed {h?.EyeAdaptSpeed:0.###} strength {h?.EyeAdaptStrength:0.###}, "
                       + $"bloom blur {h?.BloomBlurRadius:0.###} scale {h?.BloomScale:0.###} threshold {h?.BloomThreshold:0.###} receive {h?.ReceiveBloomThreshold:0.###}, "
                       + $"white {h?.White:0.###}, sunlight scale {h?.SunlightScale:0.###}, sky scale {h?.SkyScale:0.###}; "
                       + $"cinematic saturation {c?.Saturation:0.###} brightness {c?.Brightness:0.###} contrast {c?.Contrast:0.###}; "
                       + $"tint {t?.Color} x {t?.Amount:0.###}";
        ImageSpaces[i.FormKey] = (values, text);
    }

    /// <summary>One IMGS's values, copied out of the record.</summary>
    internal sealed record ImageSpaceValues(string Name, float White, float SunlightScale, float SkyScale,
        float Saturation, float Brightness, float Contrast, V3 TintColor, float TintAmount);

    public override string ToString()
        => $"{Name} ({Slot}): sky {SkyUpper:0.000}, sun {Sunlight:0.000}, ambient (up-facing, DALC Z-) {AmbientUp:0.000}, "
         + $"sky lower {SkyLower:0.000}, horizon {Horizon:0.000}, water multiplier {WaterMultiplier:0.000}, "
         + $"fog {FogNearColor:0.000} -> {FogFarColor:0.000} over {FogNear:0}..{FogFar:0} u, power {FogPower:0.##}, max {FogMax:0.##}"
         + (Ambient6.Length == 6 ? $"; directional ambient +X {Ambient6[0]:0.000} -X {Ambient6[1]:0.000} +Y {Ambient6[2]:0.000} "
                                 + $"-Y {Ambient6[3]:0.000} +Z {Ambient6[4]:0.000} -Z {Ambient6[5]:0.000}" : "");
}
