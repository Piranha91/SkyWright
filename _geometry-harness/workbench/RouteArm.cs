using System.Text.Json;

namespace Workbench;

/// <summary>One reference's route-cut verdict as a blockprobe run judged it (its rows file's <c>Judged</c> list).</summary>
internal sealed record RouteJudged(string FormKey, string Verdict, string Cell, bool Navcut, string? Why);

/// <summary>What one blockprobe run of the route arm judged: every reference it tested, by form key (lower case), and the
/// reference it was asked about (<c>--near</c>).</summary>
internal sealed record RouteRun(IReadOnlyDictionary<string, RouteJudged> Judged, string? Near, string Path, string? Refused = null);

/// <summary>The route arm's word on a nudge (Gate E): "held" (no walking route cut), "cut" (the move makes one), "not confirmed"
/// (the two runs cannot show it), or "not run" (the scan did not include the route-cut check); its sentence, the cuts that were
/// already there before the move and remain, and its seconds.</summary>
internal sealed record RouteArmResult(string Word, string Sentence, IReadOnlyList<string> Remains, double Seconds = 0);

/// <summary>Gate E's route arm (STUB: the red tests run against it before the feature).</summary>
internal static class RouteCutProof
{
    internal static RouteRun Read(string rowsPath) => new(new Dictionary<string, RouteJudged>(), null, rowsPath);

    internal static RouteArmResult Judge(RouteRun unpatched, RouteRun patched, string moved, Func<string, string>? nameOf = null)
        => new("held", "no walking route cut", Array.Empty<string>());
}
