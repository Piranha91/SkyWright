using HarnessShared;

namespace RunControl;

/// <summary>The lighting check's four arrangements (environment.lighting; run folder C:/hc-runs/2026-11-26-lighting-check, Gate C).
/// runcontrol owns the arrangement lists: lightprobe --check reads which plugin is on which side from the four order files it is
/// handed and builds none itself. A scan the app starts has two arms, the profile with the selected mods (the combination, C) and
/// without them (the rest, R); this writes the other two beside the row's output:
///   the dependencies (D): the selection's masters, followed through their own masters, that the profile without the selection loads;
///   the selection over them (S): D and the selection's plugins.
/// Plugins keep the combination's order. D and R are read through the mod folders of the profile without the selection, S and C
/// through the combination's (the asset view; GATE-C.md, decisions). The three files are the row's outputs after the probe's own
/// json, so the verified bundle carries what the check read.</summary>
public static class LightingArrangements
{
    public const string CheckId = "environment.lighting";
    public static readonly string[] Roles = ["dependencies", "selection", "rest"];

    /// <summary>The three lists from the combination's order, the rest's order and the selection's plugins; null with one sentence
    /// when they cannot be built. <paramref name="masters"/> gives a plugin's masters (null when its header cannot be read).</summary>
    public static (List<string> Dependencies, List<string> Selection, List<string> RestSide)? Lists(IReadOnlyList<string> combined,
        IReadOnlyList<string> rest, IReadOnlyCollection<string> selection, Func<string, IReadOnlyList<string>?> masters, out string? refusal)
    {
        refusal = null;
        var cmp = StringComparer.OrdinalIgnoreCase;
        var inC = new HashSet<string>(combined, cmp);
        var inR = new HashSet<string>(rest, cmp);
        var sel = combined.Where(p => selection.Contains(p, cmp)).ToList();
        if (sel.Count == 0)
        { refusal = "The selected mods add no plugin to the load order, so the lighting check has nothing to compare; select a mod with a plugin."; return null; }
        if (sel.FirstOrDefault(inR.Contains) is { } both)
        { refusal = $"{both} loads both with and without the selected mods, so it cannot be on one side; leave it out of the selection or its other copy out of the profile."; return null; }
        if (rest.FirstOrDefault(p => !inC.Contains(p)) is { } stray)
        { refusal = $"The profile without the selected mods loads {stray}, which the profile with them does not; scan again so both come from one profile."; return null; }
        var deps = new HashSet<string>(cmp);
        var todo = new Queue<string>(sel);
        while (todo.Count > 0)
        {
            var p = todo.Dequeue();
            if (masters(p) is not { } ms)
            { refusal = $"The header of {p} could not be read, so its masters are unknown; check that the file is a plugin and readable."; return null; }
            foreach (var m in ms)
                if (inR.Contains(m) && deps.Add(m)) todo.Enqueue(m);
        }
        var d = combined.Where(deps.Contains).ToList();
        var s = combined.Where(p => deps.Contains(p) || sel.Contains(p, cmp)).ToList();
        var r = combined.Where(inR.Contains).ToList();
        if (r.Count == d.Count)
        { refusal = "Without the selected mods the profile loads only what the selection itself needs, so nothing can clash with it; scan a profile with other mods."; return null; }
        return (d, s, r);
    }

    static string Folder(string outDir) => PyPath.Join(outDir, "arrangements");

    /// <summary>The order file of one role for the row's arm.</summary>
    public static string PathOf(string outDir, string role) => PyPath.Join(Folder(outDir), role + ".json");

    /// <summary>Write the three order files for <paramref name="arm"/> (the combination) under <paramref name="outDir"/>; null when
    /// written, else one sentence. The rest is the plan's baseline arm; the selection is what the combination loads and the rest does not.</summary>
    public static string? Write(string outDir, PyDict plan, PyDict arm, Func<string, IReadOnlyList<string>?> masters)
    {
        string armId = Ev.Str(arm["armId"]);
        var restArm = Ev.Items(plan.Get("arms")).OfType<PyDict>().FirstOrDefault(a => Ev.Eq(a.Get("kind"), "baseline") && Ev.Str(a["armId"]) != armId);
        if (restArm is null)
            return "The lighting check needs the load order with and without the selected mods, and this run has no second setup; start it as a scan from the app.";
        var cDoc = Py.D(PyJson.ReadPipelineFile(Ev.Str(arm["candidatePath"])));
        var rDoc = Py.D(PyJson.ReadPipelineFile(Ev.Str(restArm["candidatePath"])));
        // the selection's plugins are the ones the combination loads and the profile without the selection does not (the in-place
        // resolution's own subject rule), never a name match against the plan's targets
        var restSet = new HashSet<string>(Py.Strs(rDoc["Plugins"]), StringComparer.OrdinalIgnoreCase);
        var selection = Py.Strs(cDoc["Plugins"]).Where(p => !restSet.Contains(p)).ToList();
        var lists = Lists(Py.Strs(cDoc["Plugins"]), Py.Strs(rDoc["Plugins"]), selection, masters, out var refusal);
        if (lists is not { } l) return refusal;
        Directory.CreateDirectory(Folder(outDir));
        foreach (var (role, plugins, view) in new[] { ("dependencies", l.Dependencies, rDoc), ("selection", l.Selection, cDoc), ("rest", l.RestSide, rDoc) })
            Candidates.Write(PathOf(outDir, role), new PyDict
            {
                ["SchemaVersion"] = 1L, ["Name"] = $"{Ev.Str(cDoc.Get("Name"))}: lighting {role}", ["BaseProfile"] = cDoc.Get("BaseProfile"),
                ["Plugins"] = Py.Objs(plugins), ["Mods"] = view.Get("Mods"),
                ["Changes"] = new List<object?> { $"runcontrol lighting arrangement {role} of arm {armId}" },
                ["Provenance"] = new PyDict { ["arm"] = armId, ["role"] = "lighting " + role, ["rest"] = restArm["armId"], ["selection"] = Py.Objs(selection) },
            });
        return null;
    }

    /// <summary>A plugin's masters from the arm's frozen request (the copy the probes read), null when its header will not read.</summary>
    public static Func<string, IReadOnlyList<string>?> MastersOf(PyDict arm)
    {
        var paths = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var p in Ev.Items(Ev.OrEmpty(arm.Get("requested")).Get("plugins")).OfType<PyDict>())
            if (p.Get("name") is string n && p.Get("path") is string path) paths[PyPath.Name(n)] = path;
        return name => paths.TryGetValue(PyPath.Name(name), out var path) && PluginHeader.TryRead(path) is { } h ? h.Masters : null;
    }

    /// <summary>The registry's {dependencies}, {selection} and {rest}: the first fills write the three files, each appends its path.</summary>
    public static GenericRows.Placeholder Fill(string role) => (ex, plan, check, arm, args) =>
    {
        string outDir = PyPath.Join(ex.Run, "outputs", Ev.Str(check["checkId"]));
        string mine = PathOf(outDir, role);
        if (role == Roles[0] || !File.Exists(mine))
            if (Write(outDir, plan, arm, MastersOf(arm)) is { } why) return ("blocked(lighting-arrangements)", why);
        args.Add(mine);
        return null;
    };

    /// <summary>Where the row's result lands: the probe's json first (read by the validator and the app), then the three order files.</summary>
    public static List<string>? Outputs(PyDict plan, PyDict check, List<string> argv, string outDir)
    {
        if (Ev.Str(check["checkId"]) != CheckId) return null;
        int j = argv.IndexOf("--json");
        if (j < 0 || j + 1 >= argv.Count) return null;
        return [argv[j + 1], .. Roles.Select(r => PathOf(outDir, r))];
    }
}
