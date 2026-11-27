using System.Text.Json;

namespace Workbench;

/// <summary>One reference's route-cut verdict as a blockprobe run judged it (its rows file's <c>Judged</c> list).</summary>
internal sealed record RouteJudged(string FormKey, string Verdict, string Cell, bool Navcut, string? Why);

/// <summary>What one blockprobe run of the route arm judged: every reference it tested, by form key (lower case), and the
/// reference it was asked about (<c>--near</c>); or why the file cannot be read as a list of what was judged.</summary>
internal sealed record RouteRun(IReadOnlyDictionary<string, RouteJudged> Judged, string? Near, string Path, string? Refused = null);

/// <summary>The route arm's word on a nudge (Gate E): "held" (no walking route cut), "cut" (the move makes one), "not confirmed"
/// (the two runs cannot show it), or "not run" (the scan did not include the route-cut check); its sentence, the cuts that were
/// already there before the move and remain, and its seconds.</summary>
internal sealed record RouteArmResult(string Word, string Sentence, IReadOnlyList<string> Remains, double Seconds = 0);

/// <summary>Gate E's route arm: blockprobe (the owner of the route-cut rule) judges, with <c>--near</c>, the moved object and every
/// object whose verdict its move can change, on the unpatched order and on the patched one, and lists every reference it
/// judged with its verdict. This compares the two lists, nothing more (the brief's choice):
/// <list type="bullet">
/// <item>a reference in one list and not the other was not tested there, so nothing is shown ("not confirmed"); absent is
/// never read as fine, and a file with no list at all confirms nothing;</item>
/// <item>a cut (Blocked or LikelyBlocked) under the patch that was not one before rejects the candidate ("cut");</item>
/// <item>a reference that can no longer be judged (NoLine, Unjudgeable, NotJudged) where it could be before: "not confirmed";</item>
/// <item>a cut there before the move and still there is not held against the candidate; it is said ("remains");</item>
/// <item>the moved object itself not judgeable at the nudged place: "not confirmed", whatever it read before (review E1); a
/// neighbour judgeable neither before nor after is not held against the move, and the sentence names it.</item>
/// </list></summary>
internal static class RouteCutProof
{
    static readonly HashSet<string> Cut = new(StringComparer.Ordinal) { "Blocked", "LikelyBlocked" };
    static readonly HashSet<string> Unjudged = new(StringComparer.Ordinal) { "NoLine", "Unjudgeable", "NotJudged" };
    internal const string NotInScan = "this scan did not include the route-cut check";

    internal static bool IsCut(string verdict) => Cut.Contains(verdict);

    internal static RouteRun Read(string rowsPath)
    {
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(rowsPath));
            var r = doc.RootElement;
            string? near = r.TryGetProperty("Near", out var n) && n.ValueKind == JsonValueKind.Object && n.TryGetProperty("Reference", out var nr) ? nr.GetString() : null;
            if (!r.TryGetProperty("Judged", out var j) || j.ValueKind != JsonValueKind.Array)
                return new(new Dictionary<string, RouteJudged>(), near, rowsPath,
                           "its rows file lists no judged references (a blockprobe from before this check could prove a nudge), so what it leaves out cannot be read as fine");
            var map = new Dictionary<string, RouteJudged>(StringComparer.Ordinal);
            foreach (var e in j.EnumerateArray())
            {
                static string? S(JsonElement x, string k) => x.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
                var fk = S(e, "FormKey");
                if (fk is null) continue;
                map[fk.ToLowerInvariant()] = new RouteJudged(fk, S(e, "Verdict") ?? "NotJudged", S(e, "Cell") ?? "?",
                                                             e.TryGetProperty("Navcut", out var nc) && nc.ValueKind == JsonValueKind.True, S(e, "Why"));
            }
            return new(map, near, rowsPath);
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            return new(new Dictionary<string, RouteJudged>(), null, rowsPath, $"its rows file could not be read ({ex.Message})");
        }
    }

    static string Plain(string verdict) => verdict switch
    {
        "Blocked" => "blocks a walking route",
        "LikelyBlocked" => "likely blocks a walking route",
        "NoLine" => "has no walking line the rule can follow",
        "Unjudgeable" or "NotJudged" => "could not be judged",
        _ => "blocks no walking route",
    };

    static string List(IEnumerable<string> names)
    {
        var l = names.ToList();
        return l.Count <= 1 ? string.Join("", l) : string.Join(", ", l.Take(l.Count - 1)) + " and " + l[^1];
    }

    internal static RouteArmResult Judge(RouteRun unpatched, RouteRun patched, string moved, Func<string, string>? nameOf = null)
    {
        nameOf ??= k => k;
        if ((unpatched.Refused ?? patched.Refused) is { } why)
            return new("not confirmed", $"walking routes could not be shown uncut: {why}", Array.Empty<string>());
        var m = moved.ToLowerInvariant();
        foreach (var (run, where) in new[] { (unpatched, "where the load order places it"), (patched, "at the nudged place") })
            if (!run.Judged.ContainsKey(m))
                return new("not confirmed", $"walking routes could not be shown uncut: {nameOf(moved)} was not judged {where}", Array.Empty<string>());
        var keys = unpatched.Judged.Keys.Union(patched.Judged.Keys).OrderBy(k => k, StringComparer.Ordinal).ToList();
        string Name(string k) => nameOf((unpatched.Judged.TryGetValue(k, out var a) ? a : patched.Judged[k]).FormKey);
        var untested = keys.Where(k => !unpatched.Judged.ContainsKey(k) || !patched.Judged.ContainsKey(k)).ToList();
        if (untested.Count > 0)
            return new("not confirmed", $"walking routes could not be shown uncut: {List(untested.Select(k => $"{Name(k)} (tested {(unpatched.Judged.ContainsKey(k) ? "only before the move" : "only after it")})"))} "
                                        + "was judged in one run and not tested in the other, and an object not tested is not read as fine", Array.Empty<string>());
        var created = new List<string>(); var lost = new List<string>(); var remains = new List<string>(); var neither = new List<string>();
        foreach (var k in keys)
        {
            string u = unpatched.Judged[k].Verdict, p = patched.Judged[k].Verdict;
            if (Cut.Contains(p) && !Cut.Contains(u)) created.Add(k);
            else if (Unjudged.Contains(p) && !Unjudged.Contains(u)) lost.Add(k);
            else if (Cut.Contains(p) && Cut.Contains(u)) remains.Add(k);
            else if (Unjudged.Contains(p) && Unjudged.Contains(u) && k != m) neither.Add(k);
        }
        if (created.Count > 0)
            return new("cut", $"after the move {List(created.Select(k => $"{Name(k)} {Plain(patched.Judged[k].Verdict)}"))}, which it did not before", Array.Empty<string>());
        // review E1: the moved object itself must be judged at the nudged place, whatever it read before
        if (Unjudged.Contains(patched.Judged[m].Verdict))
            return new("not confirmed", $"walking routes could not be shown uncut: at the nudged place {Name(m)} {Plain(patched.Judged[m].Verdict)}"
                                        + (Unjudged.Contains(unpatched.Judged[m].Verdict) ? ", as where the load order places it" : ""), Array.Empty<string>());
        if (lost.Count > 0)
            return new("not confirmed", $"walking routes could not be shown uncut: after the move {List(lost.Select(k => $"{Name(k)} {Plain(patched.Judged[k].Verdict)}"))}, where before the move it could be judged", Array.Empty<string>());
        var rem = remains.Select(k => unpatched.Judged[k].FormKey).ToList();
        // a neighbour that could be judged neither before nor after is not held against the move, but it is named
        var unknown = neither.Count == 0 ? "" : $"; {List(neither.Select(Name))} could not be judged before or after the move";
        return new("held", (rem.Count == 0 ? $"no walking route cut ({keys.Count} object(s) near it judged before and after the move)"
                                           : $"no walking route cut by the move; {List(rem.Select(nameOf))} already {(rem.Count == 1 ? "blocked" : "block")} a walking route before the move and still {(rem.Count == 1 ? "does" : "do")}") + unknown, rem);
    }
}
