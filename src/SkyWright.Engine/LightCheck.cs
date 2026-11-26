using System.Numerics;

namespace SkyWright.Engine;

// THE LIGHTING CHECK (2026-11-26, Gate B): the API its known answers are written against. Not yet implemented: every entry point returns
// an empty answer, so each known answer fails on its own assertion.

public static class LightCheck
{
    public const string PolicyVersion = "light_check_v1 (not implemented)";
    public const double HostRadius = 64;
    public const int MinFacingHits = 8;
    public const float ImplausibleRadius = 16384f;
    public const float ExteriorReach = 4096f;

    public const string LightNoFixture = "light-no-fixture", FixtureNoLight = "fixture-no-light", LightBehindWall = "light-behind-wall",
                        LightLightsNothing = "light-lights-nothing", FixtureLitTwice = "fixture-lit-twice", OverLightLimit = "over-light-limit";
    public static readonly IReadOnlyList<string> Classes = new[] { LightNoFixture, FixtureNoLight, LightBehindWall, LightLightsNothing, FixtureLitTwice, OverLightLimit };

    public static string PluginOf(string key) => key.IndexOf(':') is int i and >= 0 ? key[(i + 1)..] : "";

    public static LightCheckSceneResult Evaluate(string scene, bool exterior, LightCheckReading dependencies, LightCheckReading selection,
                                                 LightCheckReading rest, LightCheckReading combined, LightCheckSides sides, LightCheckOptions? options = null)
        => new(scene, Array.Empty<LightCheckRow>(), Classes.ToDictionary(k => k, _ => new LightCheckCount()));
}
