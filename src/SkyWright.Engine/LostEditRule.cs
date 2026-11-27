namespace SkyWright.Engine;

// STUB (record checks unit 2, the red point): every version is judged, the plugin beneath the loser is what it was made
// with, and a field is lost when its text differs. The real rule replaces it.

/// <summary>One field a version lost: the top-level field, the paths under it, the loser's value, the winner's, and the
/// value the loser was made with (null where the loser defines the record).</summary>
public sealed record LostField(string Field, IReadOnlyList<string> Paths, string? Lost, string? Winner, string? MadeWith);

/// <summary>What one version gives: its lost fields, how many authored fields were compared, and what was not judged.</summary>
public sealed record VersionResult(IReadOnlyList<LostField> Lost, long FieldsJudged, IReadOnlyDictionary<string, long> NotJudged);

/// <summary>Whether a version is judged, and if not, the reason it is set aside or not judged.</summary>
public sealed record Admission(bool Judge, string? SetAside = null, string? NotJudged = null);

public static class LostEditRule
{
    public const string Registration = "registration and dialogue records: that check's (record checks unit 4)";
    public const string Navmesh = "navmesh: the navmesh check's";
    public const string RuntimeMerged = "a record type the field scope leaves out: the game merges it at run time";
    public const string Incomplete = "a read houseCARL marks incomplete";
    public const string Unreadable = "a delta line SkyWright could not read";
    public const string OrderRead = "a list whose order the game reads, reordered";
    public const string MastersUnread = "a plugin whose masters' headers could not all be read";

    public static bool IsRegistration(string rtype) => false;
    public static bool IsNavmesh(string rtype) => false;

    public static Admission Admit(string rtype, LinkEnd loser, LinkEnd winner, bool winnerMadeWithLoser, bool mastersUnread, bool instrument, RecordFieldScope scope) =>
        new(true);

    public static string? MadeWithPole(IReadOnlyList<string> touchers, string loser, IReadOnlySet<string> loserMasters)
    {
        int i = touchers.ToList().FindIndex(t => string.Equals(t, loser, StringComparison.OrdinalIgnoreCase));
        return i > 0 ? touchers[i - 1] : null;
    }

    public static VersionResult Judge(string rtype, IReadOnlyList<string> authoredLines, IReadOnlyList<string> vsWinnerLines, IReadOnlyList<string> plugins,
        RecordFieldScope scope, bool complete = true, bool defines = false, IReadOnlySet<string>? masters = null,
        IReadOnlyDictionary<string, (IReadOnlyList<string> Lines, bool Complete)>? providers = null, bool mastersUnread = false, bool angles = true)
    {
        var against = vsWinnerLines.Select(l => RecordDelta.Parse(l, plugins)).Where(x => x is not null).ToDictionary(x => x!.Field, x => x!);
        var authored = (defines ? vsWinnerLines : authoredLines).Select(l => RecordDelta.Parse(l, plugins)).Where(x => x is not null).ToList();
        var lost = new List<LostField>();
        foreach (var a in authored)
            if (against.TryGetValue(a!.Field, out var w) && w.Own != w.OtherValue)
                lost.Add(new LostField(RecordDelta.Top(a.Field), new[] { a.Field }, a.Own, w.OtherValue, defines ? null : a.OtherValue));
        return new VersionResult(lost, authored.Count, new Dictionary<string, long>());
    }

    public static bool? WinnerChanged(string top, IReadOnlyList<string> winnerLines, bool complete, bool defines, string rtype, IReadOnlyList<string> plugins, RecordFieldScope scope) =>
        defines || winnerLines.Any(l => RecordDelta.Parse(l, plugins) is { } d && RecordDelta.Top(d.Field) == top);
}
