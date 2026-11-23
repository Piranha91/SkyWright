#if SKYRENDER_SKY
using SkyRender;
using GlVec2 = OpenTK.Mathematics.Vector2;
using GlVec3 = OpenTK.Mathematics.Vector3;

namespace ViewProbe;

/// <summary>
/// The sky the engine draws, from the scene's weather and its worldspace's climate, for SkyRender's sky pass
/// (<c>GlRenderer.Sky</c>, SkyRender 4.1): the dome by the weather's sky gradient (SceneWeather.SkyKeys), the weather's
/// enabled cloud layers at the slot's time of day, the climate's sun disc and glare at the key light's direction, and
/// at night the climate's moons and the stars.
/// <para>Not modelled: the moons' real positions and phases (they stand at fixed places in the night sky), the sun
/// and glare sizes (SkyRender's stand-ins), and blending between weathers or times of day.</para>
/// </summary>
internal static class SceneSky
{
    // the moons' stand-in places in the night sky (azimuth as SetKeyLight takes it, elevation), and their sizes in degrees
    static readonly (string Texture, float Azimuth, float Elevation, float Size) MasserAt = (@"textures\sky\masser_full.dds", 200f, 32f, 14f);
    static readonly (string Texture, float Azimuth, float Elevation, float Size) SecundaAt = (@"textures\sky\secunda_full.dds", 235f, 24f, 7f);

    static GlVec3 G(System.Numerics.Vector3 v) => new(v.X, v.Y, v.Z);

    internal static SkySettings Settings(SceneWeather w, SkyGradient? gradient, Func<string, int?> loadTexture)
    {
        bool night = w.Slot == "night";
        var sky = SceneWeather.WorldClimate is { } k && SceneWeather.ClimateSkies.TryGetValue(k, out var c) ? c : default;
        var moons = new List<SkyMoon>();
        if (night && sky.Masser) moons.Add(new SkyMoon(MasserAt.Texture, MasserAt.Azimuth, MasserAt.Elevation, MasserAt.Size));
        if (night && sky.Secunda) moons.Add(new SkyMoon(SecundaAt.Texture, SecundaAt.Azimuth, SecundaAt.Elevation, SecundaAt.Size));
        return new SkySettings
        {
            LoadTexture = loadTexture,
            Gradient = gradient,
            Clouds = w.Clouds.Select(l => new SkyCloudLayer(Tex(l.Texture), G(l.Color), l.Alpha, new GlVec2(l.XSpeed, l.YSpeed))).ToList(),
            Sun = night ? null : new SkySun(sky.Sun is { } s ? Tex(s) : null, sky.Glare is { } gl ? Tex(gl) : null, G(w.SunColor)),
            Moons = moons,
            Stars = night ? new SkyStars(@"textures\sky\skystars.dds", 1f) : null,
        };
    }

    // a texture path as the loader takes it: game-relative, under textures\
    static string Tex(string p)
    {
        p = p.Replace('/', '\\');
        return p.StartsWith(@"textures\", StringComparison.OrdinalIgnoreCase) ? p : @"textures\" + p;
    }

    internal static string Describe(SceneWeather w)
    {
        var sky = SceneWeather.WorldClimate is { } k && SceneWeather.ClimateSkies.TryGetValue(k, out var c) ? c : default;
        return $"drawn: {w.Clouds.Count} cloud layer(s), " + (w.Slot == "night"
            ? $"stars, moons {(sky.Masser ? "Masser " : "")}{(sky.Secunda ? "Secunda" : "")}(at stand-in places)"
            : $"sun {(sky.Sun ?? "(no climate texture)")}");
    }
}
#endif
