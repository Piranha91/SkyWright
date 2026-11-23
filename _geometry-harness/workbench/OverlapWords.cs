using System.Text.RegularExpressions;

namespace Workbench;

/// <summary>A predicted overlap as a modder reads it (walkthrough problems 11 and 12). The reader keeps the check's own
/// summary and words (the state tools read them as they are); this turns them into the values pane's and the table's words:
/// which two objects, from which plugins, pass into each other where, and what the attribution means.</summary>
internal static partial class OverlapWords
{
    /// <summary>The parts of the overlap check's summary: "Static from A.esp / Tree from B.esp: crossing, containment
    /// unassessed, 41 triangle pairs in RiftenCitySouth (ext) [mm] 079241:Skyrim.esm / 079242:Skyrim.esm".</summary>
    internal sealed record Parts(string TypeA, string PluginA, string TypeB, string PluginB, string Relation, string? Containment, string? Place);

    [GeneratedRegex(@"^(?<ta>\w[\w ]*?) from (?<pa>.+?) / (?<tb>\w[\w ]*?) from (?<pb>.+?): (?<rel>[a-z_ ]+?)(?:, containment (?<cont>[a-z_ ]+?))?(?:, [\d,]+ triangle pairs)?(?: in (?<place>.+?) \((?:ext|int)\))?(?: \[\w+\])?(?: [0-9A-Fa-f]{6}:.+? / [0-9A-Fa-f]{6}:.+?)?\s*$")]
    private static partial Regex Summary();

    internal static Parts? Parse(string summary)
    {
        var m = Summary().Match(Regex.Replace(summary, @"\s+", " ").Trim());
        if (!m.Success) return null;
        string? G(string n) => m.Groups[n].Success && m.Groups[n].Value.Length > 0 ? m.Groups[n].Value : null;
        return new Parts(G("ta")!, G("pa")!, G("tb")!, G("pb")!, G("rel")!, G("cont"), G("place"));
    }

    /// <summary>How the two surfaces meet, as a verb: "passes into" for a crossing.</summary>
    static string Verb(string relation, string? containment) => containment switch
    {
        "a_inside_b" => "sits inside",
        "b_inside_a" => "holds inside it",
        _ => relation switch
        {
            "crossing" => "passes into",
            "coplanar_overlap" => "lies flat against",
            "touching" => "touches",
            _ => "overlaps",
        },
    };

    static string A(string type) { var t = RecordWords.BaseType(type); return ("aeiou".Contains(t[0]) ? "an " : "a ") + t; }

    /// <summary>The overlap in a sentence: "A static from Riften Expansion.esp passes into a tree from Unofficial Skyrim
    /// Special Edition Patch.esp in RiftenCitySouth." The summary as it is when it does not have the expected shape.</summary>
    internal static string Sentence(string summary)
    {
        if (Parse(summary) is not { } p) return summary;
        var s = $"{A(p.TypeA)} from {p.PluginA} {Verb(p.Relation, p.Containment)} {A(p.TypeB)} from {p.PluginB}" + (p.Place is { } at ? $" in {at}" : "") + ".";
        return char.ToUpperInvariant(s[0]) + s[1..];
    }

    /// <summary>What the attribution says, in words: which mod the overlap comes from.</summary>
    internal static string Attribution(string? attribution, string? mod) => attribution switch
    {
        "needs-both" => "It appears only with both mods together.",
        "single-mod" => mod is null ? "One mod causes it on its own." : $"{mod} causes it on its own.",
        "with-target" => (mod is null ? "It comes with the mod checked" : $"It comes with {mod}")
                         + ". No setup without the other mod was checked, so whether it needs both is not known.",
        "needs-some" => (mod is null ? "It needs the mod checked" : $"It needs {mod}") + " together with other plugins of the load order, not with the other mod.",
        "not-separable" => "The checks could not tell which mod causes it.",
        null or "" or "(none)" => "The checks did not say which mod causes it.",
        _ => $"The checks' attribution: {attribution}.",
    };

    /// <summary>An overlap row's values as they are shown: the physical state and the attribution in words, the mod named in
    /// the sentence rather than beside the label. Other rows' values are returned as they are.</summary>
    internal static List<ResultValue> Values(ResultRow r) => r.Lane != "overlap" ? r.Values.ToList() : r.Values.Select(v => v.Label switch
    {
        "what the check says" => v with { Text = Sentence(v.Text) },
        "physical state" => v with { Label = "in the setup checked", Text = Physical(v.Text) },
        "attribution" => v with { Label = "which mod it comes from", Plugin = null, Text = Attribution(v.Text, v.Plugin) },
        _ => v,
    }).ToList();

    /// <summary>The overlap check's physical state in words.</summary>
    internal static string Physical(string state) => state switch
    {
        "Holds" => "Still there: the two objects cross in the setup checked.",
        "Gone" => "Gone: the two objects no longer cross in the setup checked.",
        "Unmeasured" => "Not measured in the setup checked.",
        _ => state,
    };
}
