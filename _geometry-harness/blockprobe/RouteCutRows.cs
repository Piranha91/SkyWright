using System.Text.Json;
using HarnessShared;
using HousecarlCore;
using SkyWright.Engine;
using Mutagen.Bethesda.Plugins;

// blockprobe --rows FILE: the route-cut listing as rows the report can read (2026-10-24). Every Blocked, LikelyBlocked,
// NoLine and Unjudgeable reference the route-cut rule measured, objects and navcut boxes, UNCAPPED (the printed listing stops
// at --list x 3). Each row states what the listing states: the verdict, the old rule's verdict, the object, its cell, its
// mesh, the target(s) that place it there, the passage beside it (rounded as the listing prints it; null where it prints
// "-"), the passage share the rule states (DetectorModel.LikelyBlockedPassageShare, on a LikelyBlocked row only) and the
// rule's sentence. The file adds nothing the listing does not say; the console is unchanged.
static class RouteCutRows
{
    public sealed record Row(string Verdict, string? OldRule, string FormKey, string Cell, string? Mesh, IReadOnlyList<string> By,
                             int Touched, int? Passage, int? PassageLeft, int? PassageRight, int? PassageCap, string? PassageShare,
                             bool Navcut, string Why);

    static int? Round(float v) => float.IsFinite(v) ? (int)Math.Round(v, MidpointRounding.AwayFromZero) : null;

    public static bool Listed(RouteCutVerdict v) =>
        v is RouteCutVerdict.Blocked or RouteCutVerdict.LikelyBlocked or RouteCutVerdict.NoLine or RouteCutVerdict.Unjudgeable;

    public static Row Of(RouteCutVerdict v, string? old, string cell, string? mesh, FormKey fk, IReadOnlyList<string> by,
                         int touched, RouteLine? line, string why, bool navcut)
    {
        var l = line;
        bool hasPassage = l is not null && float.IsFinite(l.Passage);
        return new Row(v.ToString(), old, fk.ToString(), cell, mesh, by, touched,
                       hasPassage ? Round(l!.Passage) : null,
                       l is null ? null : Round(l.PassageLeft), l is null ? null : Round(l.PassageRight),
                       hasPassage ? Round(l!.PassageCap) : null,
                       v == RouteCutVerdict.LikelyBlocked ? DetectorModel.LikelyBlockedPassageShare : null,
                       navcut, why);
    }

    public static void Write(string path, IReadOnlyList<string> targets, string population,
                             IReadOnlyDictionary<RouteCutVerdict, int> tally, IReadOnlyList<Row> rows)
    {
        var dir = Path.GetDirectoryName(Path.GetFullPath(path));
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        var doc = new
        {
            Probe = "blockprobe", Schema = "route-cut-rows/1", Targets = targets, Population = population,
            Tally = Enum.GetValues<RouteCutVerdict>().ToDictionary(v => v.ToString(), v => tally.TryGetValue(v, out var n) ? n : 0),
            Rows = rows,
        };
        File.WriteAllText(path, JsonSerializer.Serialize(doc, new JsonSerializerOptions { WriteIndented = true }));
    }
}
