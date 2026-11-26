using RunControl;

namespace EvalControl;

/// <summary>The module's list of AI-side checks (ai-checks.json, beside this program): the registry's schema, validated
/// by runcontrol's validator against this module's adapter vocabulary, never runcontrol's.</summary>
public static class AiList
{
    public const string FileName = "ai-checks.json";

    /// <summary>The list this build carries.</summary>
    public static string Shipped => Path.Combine(AppContext.BaseDirectory, FileName);

    /// <summary>The adapter kinds an AI-side row may have. media: supplied pictures and clips, assessed at bundle;
    /// mcp-only: a check only houseCARL's record tools read, left open by the plan for `evalcontrol import`;
    /// live-import: a check a live game driver measures (the live switch check), planned only when opted in, open
    /// until `evalcontrol import` binds the driver's export; adviceprobe: a probe of this module's build that reads a
    /// mod page's stated order offline, run as a child (ModuleRows), its result a discovery listing; patchfind: a patch-finding
    /// probe of this module's build (patchfind, fomodprobe, poolprobe) writing a schema-1 hit list, run as a
    /// child.</summary>
    public static readonly Registry.Vocabulary Vocabulary = new(
        AdapterKinds: new HashSet<string>(StringComparer.Ordinal) { "media", "mcp-only", "live-import", "adviceprobe", "patchfind" },
        KindsNeedingReason: new HashSet<string>(StringComparer.Ordinal) { "mcp-only" },
        ResultKinds: new HashSet<string>(StringComparer.Ordinal) { "discovery" },
        KindsWithoutOutput: new HashSet<string>(StringComparer.Ordinal) { "media", "mcp-only", "live-import" },
        KindsRequiringOptIn: new HashSet<string>(StringComparer.Ordinal) { "live-import" },
        KindsWithChild: new HashSet<string>(StringComparer.Ordinal) { "adviceprobe", "patchfind" },
        EvidenceStrengths: new HashSet<string>(StringComparer.Ordinal) { "supplied-media", "record-fact", "engine-observation" });

    /// <summary>The kinds the overlay plan records by their own name and never runs: an mcp-only or live-import row
    /// waits for an import. (A media row is planned and accounted at evaluate, then assessed by bundle --media.)</summary>
    public static readonly string[] NeverRunKinds = ["mcp-only", "live-import"];

    /// <summary>The kinds on which a manual decision never satisfies completion: a record reader's judgement is
    /// imported, never decided by hand.</summary>
    public static readonly string[] NeverCompletingKinds = ["mcp-only"];

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
