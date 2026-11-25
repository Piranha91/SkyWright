using RunControl;

namespace EvalControl;

/// <summary>The module's list of AI-side checks (ai-checks.json, beside this program): the registry's schema, validated
/// by runcontrol's validator against this module's adapter vocabulary, never runcontrol's.</summary>
public static class AiList
{
    public const string FileName = "ai-checks.json";

    /// <summary>The list this build carries.</summary>
    public static string Shipped => Path.Combine(AppContext.BaseDirectory, FileName);

    /// <summary>The adapter kinds an AI-side row may have. media: supplied pictures and clips, assessed at bundle.</summary>
    public static readonly Registry.Vocabulary Vocabulary = new(
        AdapterKinds: new HashSet<string>(StringComparer.Ordinal) { "media" },
        KindsNeedingReason: new HashSet<string>(StringComparer.Ordinal),
        ResultKinds: new HashSet<string>(StringComparer.Ordinal),
        KindsWithoutOutput: new HashSet<string>(StringComparer.Ordinal) { "media" },
        KindsRequiringOptIn: new HashSet<string>(StringComparer.Ordinal),
        KindsWithChild: new HashSet<string>(StringComparer.Ordinal),
        EvidenceStrengths: new HashSet<string>(StringComparer.Ordinal) { "supplied-media" });

    /// <summary>The kinds the overlay plan records by their own name and never runs (none yet: a media row is planned
    /// and accounted at evaluate, then assessed by bundle --media).</summary>
    public static readonly string[] NeverRunKinds = [];

    /// <summary>Every problem with the list: runcontrol's validator with this vocabulary, and each appPrerequisites
    /// entry naming a check of the app plan.</summary>
    public static List<string> Validate(PyDict list, PyDict appPlan)
    {
        var problems = Registry.Validate(list, Vocabulary);
        var appChecks = Ev.Items(appPlan.Get("checks")).Select(c => Ev.Str(Ev.G(c, "checkId"))).ToHashSet(StringComparer.Ordinal);
        foreach (var c in Ev.Items(list.Get("checks")).OfType<PyDict>())
        {
            if (!c.ContainsKey("appPrerequisites")) continue;
            if (c["appPrerequisites"] is not List<object?> pres) { problems.Add($"{Ev.Str(c.Get("id"))}: appPrerequisites must be a list of the app plan's check ids"); continue; }
            foreach (var p in pres)
                if (p is not string id || !appChecks.Contains(id))
                    problems.Add($"{Ev.Str(c.Get("id"))}: appPrerequisites names {PyText.Repr(p)}, which the app plan has no check for");
        }
        return problems;
    }
}
