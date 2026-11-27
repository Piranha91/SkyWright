using System.Text.RegularExpressions;

namespace Workbench;

/// <summary>A geometry.stranded-object row as a modder reads it: which object, from which mod, is out of reach where, with
/// whose navmesh, and what kind of object it is; or why the check could not judge it. Read from the row strandprobe wrote
/// (its summary: "{type} {editor id} from {plugin} in {cell}: {d} u from walkable ground with {plugin}'s navmesh, {d} u with
/// the navmesh its mod was made with; {class}", or "...: not judged, {reason}").</summary>
internal static partial class StrandedWords
{
    internal const string CheckId = "geometry.stranded-object";

    [GeneratedRegex(@"^(?<type>\S+) (?<edid>.*?) from (?<a>.+?) in (?<cell>[^:]+): (?<w>[\d.]+) u from walkable ground with (?<b>.+?)'s navmesh, (?<o>[\d.]+) u with the navmesh its mod was made with; (?<cls>.+)$")]
    private static partial Regex Reported();

    [GeneratedRegex(@"^(?<type>\S+) (?<edid>.*?) from (?<a>.+?) in (?<cell>[^:]+): not judged, (?<why>.+)$")]
    private static partial Regex NotJudged();

    static string An(string type)
    {
        var t = RecordWords.BaseType(type);
        return ("aeiou".Contains(char.ToLowerInvariant(t[0])) ? "an " : "a ") + t;
    }

    /// <summary>What kind of object it is, in a sentence, by the check's class.</summary>
    internal static string ClassSentence(string cls) => cls switch
    {
        "scripted or quest object (demands remediation)" => "It is a quest or scripted object: the player may need to reach it.",
        "AI marker" => "Characters use it: it is a spot they walk to.",
        "decoration (light)" => "It is a light.",
        "decoration" => "It is decoration.",
        _ => "",
    };

    /// <summary>The row in a sentence, or null for a row of another check or a summary of another shape.</summary>
    internal static string? Sentence(string checkId, string summary)
    {
        if (checkId != CheckId) return null;
        string s = Regex.Replace(summary, @"\s+", " ").Trim();
        if (Reported().Match(s) is { Success: true } m)
        {
            string text = $"{An(m.Groups["type"].Value)} from {m.Groups["a"].Value} is out of reach in {m.Groups["cell"].Value}: with {m.Groups["b"].Value}'s navmesh it is "
                          + $"{Units(m.Groups["w"].Value)} from anywhere a character can walk, and {Units(m.Groups["o"].Value)} with the navmesh {m.Groups["a"].Value} was made for. "
                          + "It touches nothing of another mod.";
            string cs = ClassSentence(m.Groups["cls"].Value);
            return char.ToUpperInvariant(text[0]) + text[1..] + (cs.Length > 0 ? " " + cs : "");
        }
        if (NotJudged().Match(s) is { Success: true } n)
        {
            string text = $"{An(n.Groups["type"].Value)} from {n.Groups["a"].Value} in {n.Groups["cell"].Value}: not judged. ";
            string why = n.Groups["why"].Value;
            return char.ToUpperInvariant(text[0]) + text[1..] + char.ToUpperInvariant(why[0]) + why[1..] + (why.EndsWith('.') ? "" : ".");
        }
        return null;
    }

    static string Units(string d) => double.TryParse(d, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var v)
        ? $"{v:0} units" : d + " units";
}
