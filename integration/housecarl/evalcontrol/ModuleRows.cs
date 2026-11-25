using System.Reflection;
using RunControl;

namespace EvalControl;

/// <summary>The rules this module's probe rows add to runcontrol's generic row (GenericRows.Run): which adapter kinds
/// launch a probe of this module's build, where that build is, and the placeholders, command-line rules and output
/// rule of patch.author-advice (moved here from runcontrol's GenericRows and Discovery with the row; the sentences are
/// the ones the known answers recorded).</summary>
public static class ModuleRows
{
    /// <summary>The adapter kinds of the list whose check launches its probe as a child, added to runcontrol's own
    /// wherever runcontrol binds and checks a program's identity.</summary>
    public static readonly IReadOnlySet<string> LaunchKinds = new HashSet<string>(StringComparer.Ordinal) { "adviceprobe" };

    /// <summary>The module's build folder (integration\housecarl\bin\&lt;Configuration&gt;\), where its probes are, from the
    /// metadata its build wrote (Directory.Build.props owns the layout).</summary>
    public static string ProbeBuild()
    {
        string rel = typeof(ModuleRows).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>().First(a => a.Key == "HousecarlIntegrationBuild").Value!;
        return Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, rel));
    }

    public static readonly IReadOnlyDictionary<string, GenericRows.Placeholder> Placeholders =
        new Dictionary<string, GenericRows.Placeholder>(StringComparer.Ordinal) { ["{nexusId}"] = NexusIdPlaceholder };

    public static readonly IReadOnlyList<GenericRows.Placeholder> ArgvRules = [AdviceArgv];

    public static readonly IReadOnlyList<GenericRows.OutputsRule> OutputRules = [NexusReport];

    /// <summary>One planned row of a module probe, by runcontrol's generic row with this module's rules.</summary>
    public static void Run(Executor ex, PyDict check, PyDict arm) => GenericRows.Run(ex, check, arm, Placeholders, ArgvRules, OutputRules);

    /// <summary>The first target's Nexus id the scope records, or null.</summary>
    public static object? PlanNexusId(PyDict plan)
    {
        foreach (var t in Ev.Items(plan.Get("targets")).OfType<PyDict>())
        {
            object? nid = Ev.OrEmpty(t.Get("footprint")).Get("nexusId");
            if (!PyValue.Truthy(nid)) nid = t.Get("nexusId");
            if (PyValue.Truthy(nid)) return nid;
        }
        return null;
    }

    /// <summary>adviceprobe's --with list: the Nexus id of every instance mod folder the plan's configuration loads a
    /// plugin from, other than a target's own folder and the target's own id, in number order.</summary>
    public static List<long> AdviceNeighbourIds(PyDict plan)
    {
        object? inst = Ev.OrEmpty(plan.Get("options")).Get("instance");
        if (!PyValue.Truthy(inst)) return [];
        string instance = Ev.Str(inst);
        string mods = PyPath.NormCase(Path.GetFullPath(Path.Combine(instance, "mods")));
        var own = Ev.Items(plan.Get("targets")).OfType<PyDict>()
            .Select(t => PyText.Lower(PyValue.Truthy(t.Get("provider")) ? Ev.Str(t["provider"]) : "")).ToHashSet(StringComparer.Ordinal);
        object? nid = PlanNexusId(plan);
        string nidText = PyValue.Truthy(nid) ? Ev.Str(nid) : "";
        long? target = PyValue.IsDigit(nidText) && PyValue.IntText(nidText) is { } n ? (long)n : null;
        var ids = new SortedSet<long>();
        foreach (var p in Ev.Items(Ev.OrEmpty(plan.Get("configuration")).Get("plugins")))
        {
            object? path = (p as PyDict)?.Get("path");
            if (!PyValue.Truthy(path)) continue;
            string abs = Path.GetFullPath(Ev.Str(path));
            string full = PyPath.NormCase(abs);
            // only a plugin at the root of an instance mod folder
            if (!full.StartsWith(mods + "\\", StringComparison.Ordinal) || full[(mods.Length + 1)..].Split('\\').Length != 2) continue;
            string folder = PyPath.Name(Path.GetDirectoryName(abs)!);
            if (own.Contains(PyText.Lower(folder))) continue;
            int? mid = ModNexus.ModNexusId(PyPath.Join(instance, "mods", folder));
            if (mid is { } id && id != target) ids.Add(id);
        }
        return ids.ToList();
    }

    static (string status, string why)? NexusIdPlaceholder(Executor ex, PyDict plan, PyDict check, PyDict arm, List<string> args)
    {
        object? nid = PlanNexusId(plan);
        if (!PyValue.Truthy(nid))
            return ("blocked(no-nexus-id)",
                "the scope records no Nexus id for any target (the mod folder's meta.ini carries none), so there is no page to read offline");
        args.Add(Ev.Str(nid));
        return null;
    }

    /// <summary>adviceprobe's command line: offline only, and the evaluated mod's neighbours by Nexus id.</summary>
    static (string status, string why)? AdviceArgv(Executor ex, PyDict plan, PyDict check, PyDict arm, List<string> argv)
    {
        if (!Ev.Eq(Ev.OrEmpty(check.Get("adapter")).Get("kind"), "adviceprobe")) return null;
        int at = argv.IndexOf("--refresh");
        if (at >= 0)
        {
            string word = at + 1 < argv.Count ? argv[at + 1] : "";
            if (word != "offline")
                return ("blocked(network-refused)",
                    $"the registry adapter asks for --refresh {PyText.Repr(word)}; evalrun never fetches. "
                    + "Use --refresh offline, or run adviceprobe yourself and supply its output.");
        }
        else if (!argv.Contains("--offline")) argv.Add("--offline");
        if (!argv.Contains("--with"))
            // the evaluated mod's neighbours: a clash stated on their page is author advice for the pair
            foreach (long id in AdviceNeighbourIds(plan)) argv.AddRange(["--with", id.ToString(System.Globalization.CultureInfo.InvariantCulture)]);
        return null;
    }

    /// <summary>A json row run with --out names its report by the target's Nexus id (adviceprobe's report-ID.json).</summary>
    static List<string>? NexusReport(PyDict plan, PyDict check, List<string> argv, string outDir)
    {
        if (!argv.Contains("--out") || !Ev.Eq(Ev.OrEmpty(check.Get("adapter")).Get("output"), "json")) return null;
        object? nid = PlanNexusId(plan);
        return PyValue.Truthy(nid) ? [PyPath.Join(outDir, $"report-{Ev.Str(nid)}.json")] : null;
    }

    /// <summary>The rules and the stated clashes one adviceprobe receipt's report carries, for the disposition.</summary>
    public static PyDict AdviceStatements(string run, PyDict receipt)
    {
        var outs = PyValue.Truthy(receipt.Get("outputs")) ? (List<object?>)receipt["outputs"]! : [];
        object? doc = outs.Count > 0 && PyValue.Truthy(Ev.G(outs[0], "path")) ? PyJson.LoadEvalFile(PyPath.Join(run, Ev.Str(Ev.G(outs[0], "path")))) : null;
        if (doc is not PyDict d)
            return new PyDict
            {
                ["rules"] = null, ["clashCount"] = null, ["clashes"] = new List<object?>(),
                ["statementsNote"] = "the receipt's report could not be read, so its rules and clashes are unknown here",
            };
        string[] keys = ["namedModId", "namedModName", "pageModId", "page", "readDate", "line", "symptom"];
        var clashes = new List<object?>();
        foreach (var c in Ev.Items(d.Get("clashes")))
            if (c is PyDict cd) clashes.Add(new PyDict(keys.Select(k => new KeyValuePair<string, object?>(k, cd.Get(k)))));
        return new PyDict { ["rules"] = (long)PyValue.Len(Ev.OrList(d.Get("rules"))), ["clashCount"] = (long)clashes.Count, ["clashes"] = clashes };
    }

    /// <summary>The advice rows of every adviceprobe receipt of the overlay, as runcontrol's disposition wrote
    /// them before the row moved: each receipt's check, arm, configuration source, staleness and statements.</summary>
    public static List<object?> Advice(string run, PyDict receipts)
    {
        var advice = new List<object?>();
        foreach (var r in receipts.Select(kv => PyValue.Dict(kv.Value)).Where(r => Ev.Eq(Ev.OrEmpty(r.Get("adapter")).Get("kind"), "adviceprobe")))
            advice.Add(new PyDict
            {
                ["checkId"] = r["checkId"], ["armId"] = r["armId"],
                ["provenance"] = Ev.G(Ev.G(Ev.OrEmpty(r.Get("consumed")), "configuration", new PyDict()), "source"),
                ["staleness"] = "read offline from the stored catalogue snapshot; the cached source's age is not the date this report was assembled",
            }.Update(AdviceStatements(run, r)));
        return advice;
    }
}
