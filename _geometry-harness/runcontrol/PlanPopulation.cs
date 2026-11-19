using HarnessShared;

namespace RunControl;

/// <summary>The population the scoped checks of a plan run on: the plan's targets plus every plugin and mod folder
/// the arms differ by, frozen as a file whose bytes are the population's identity.</summary>
public static class PlanPopulation
{
    /// <summary>The names a set of ordered lists differ by: in some list and not all, or in another relative order
    /// than in the first list. Case is ignored; each name is spelled as the first list that has it spells it.</summary>
    public static List<string> ArmDelta(List<List<string>> lists)
    {
        if (lists.Count == 0) return [];
        var spelled = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var lst in lists)
            foreach (string x in lst) spelled.TryAdd(PyText.Lower(x), x);
        var sets = lists.Select(l => l.Select(PyText.Lower).ToHashSet(StringComparer.Ordinal)).ToList();
        var common = new HashSet<string>(sets[0], StringComparer.Ordinal);
        foreach (var s in sets.Skip(1)) common.IntersectWith(s);
        var differ = sets.SelectMany(s => s).Where(x => !common.Contains(x)).ToHashSet(StringComparer.Ordinal);
        var first = lists[0].Select(PyText.Lower).Where(common.Contains).ToList();
        var pos0 = new Dictionary<string, int>(StringComparer.Ordinal);
        for (int i = 0; i < first.Count; i++) pos0[first[i]] = i;   // a repeated name keeps its last place
        foreach (var lst in lists.Skip(1))
        {
            var pos = new Dictionary<string, int>(StringComparer.Ordinal);
            int n = 0;
            foreach (string y in lst.Select(PyText.Lower).Where(common.Contains)) pos[y] = n++;
            for (int i = 0; i < first.Count; i++)
                for (int j = i + 1; j < first.Count; j++)
                    if (pos0[first[i]] < pos0[first[j]] != pos[first[i]] < pos[first[j]]) { differ.Add(first[i]); differ.Add(first[j]); }
        }
        return PyOrder.SortedByLowerKey(differ.Select(x => spelled[x]));
    }

    static List<string> Entries(string folder)
    {
        try { return Directory.EnumerateFileSystemEntries(folder).ToList(); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return []; }
    }

    // A folder and every folder below it, each before the folders inside it.
    static IEnumerable<string> FoldersTopDown(string folder)
    {
        yield return folder;
        foreach (string sub in Entries(folder).Where(Directory.Exists))
            foreach (string below in FoldersTopDown(sub)) yield return below;
    }

    // Every file under a folder in the order Python's rglob gives them: the folder's own files, then, for each folder
    // top down, the files of each folder directly inside it.
    static IEnumerable<string> FilesInRglobOrder(string folder)
    {
        foreach (string f in Entries(folder).Where(File.Exists)) yield return f;
        foreach (string at in FoldersTopDown(folder))
            foreach (string sub in Entries(at).Where(Directory.Exists))
                foreach (string f in Entries(sub).Where(File.Exists)) yield return f;
    }

    /// <summary>The population file for a delta: (text, sha256, plugins, mods, race carriers), or one sentence saying
    /// why the delta cannot be scoped. What unfits a delta is PopulationRules, the probes' own statement of it.</summary>
    public static (string? refusal, string text, string sha256, List<PyDict> plugins, List<PyDict> mods, List<string> raceCarriers)
        PopulationFile(List<PyDict> plugins, List<string> modNames, List<string> targetNames, string? instance)
    {
        var outPlugins = new List<PyDict>();
        var raceCarriers = new List<string>();
        foreach (var row in plugins)
        {
            string name = Py.S(row["name"]), path = Py.S(row["path"]);
            Dictionary<string, long>? groups;
            try { groups = PopulationRules.TopGroupRecords(File.ReadAllBytes(path)); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { groups = null; }
            if (PopulationRules.CarriesCollisionLayers(groups))
                return ($"{name} carries collision-layer records (COLL), which change how every reference collides; "
                        + "the scoped probes do not model that, so these checks run on the whole order", "", "", [], [], []);
            if (PopulationRules.CarriesRaces(groups)) raceCarriers.Add(name);
            outPlugins.Add(new PyDict { ["name"] = name, ["path"] = path, ["sha256"] = row["sha256"] });
        }
        var mods = new List<PyDict>();
        foreach (string name in modNames)
        {
            if (string.IsNullOrEmpty(instance))
                return ($"the population needs mod folder {name} by path, and no --instance was given", "", "", [], [], []);
            string folder = PyPath.Join(instance, "mods", name);
            if (!Directory.Exists(folder))
                return ($"the population needs mod folder {name}, and {folder} is not a folder", "", "", [], [], []);
            string? swap = FilesInRglobOrder(folder).FirstOrDefault(f => PopulationRules.IsSwapIni(PyPath.Name(f)));
            if (swap != null)
                return ($"mod folder {name} ships a Base Object Swapper ini ({PyPath.Name(swap)}), whose swaps the population does not model", "", "", [], [], []);
            mods.Add(new PyDict { ["name"] = name, ["path"] = folder });
        }
        string text = PyJson.PopulationText(new PyDict
        {
            ["schema"] = 1L, ["plugins"] = outPlugins.Cast<object?>().ToList(), ["mods"] = mods.Cast<object?>().ToList(), ["targets"] = Py.Objs(targetNames),
        });
        return (null, text, Hashing.Sha256Text(text), outPlugins, mods, raceCarriers);
    }

    static bool Planned(PyDict check) => Py.L(check["arms"]).Any(r => Py.D(r).Get("status") as string == "planned");
    static PyDict Adapter(PyDict check) => Py.DictOrEmpty(check.Get("adapter"));

    /// <summary>(the plan's population block, the population file's text or null). A population is planned only when
    /// every file it names can be stated; anything less leaves every check on the whole order, and the block says why.</summary>
    public static (PyDict block, string? text) Plan(List<PyDict> checks, List<PyDict> arms, object? targets, string basePopulation, string? instance)
    {
        var planned = checks.Where(c => Adapter(c).Get("kind") as string == "ordersweep-probe" && Planned(c)).ToList();
        var scopedProbes = PyOrder.SortedByCodePoint(planned.Where(c => Py.Truthy(Adapter(c).Get("populationFlag"))).Select(c => Py.S(Adapter(c)["exe"])).Distinct());
        var whole = new PyDict();
        foreach (var c in planned.Where(c => !Py.Truthy(Adapter(c).Get("populationFlag"))))
            whole[Py.S(c["checkId"])] = "this probe takes no population (it runs on the whole order)";
        // a probe-emit check that takes a population is scoped by check id: the controller hands it the file itself
        var emitScoped = checks.Where(c => Adapter(c).Get("kind") as string == "probe-emit" && Py.Truthy(Adapter(c).Get("populationFlag")) && Planned(c)).ToList();
        var targetNames = Py.ListOrEmpty(targets).OfType<PyDict>().Where(t => Py.Truthy(t.Get("name"))).Select(t => PyText.Str(t["name"])).ToList();

        (PyDict, string?) Unscoped(string reason)
        {
            var wholeOrder = new PyDict(whole);
            foreach (var c in planned.Concat(emitScoped).Where(c => Py.Truthy(Adapter(c).Get("populationFlag")))) wholeOrder[Py.S(c["checkId"])] = reason;
            return (new PyDict { ["scoped"] = false, ["reason"] = reason, ["wholeOrderChecks"] = wholeOrder }, null);
        }

        if (scopedProbes.Count == 0 && emitScoped.Count == 0) return Unscoped("no planned check takes a population");
        var pluginNames = ArmDelta(arms.Select(a => Py.ListOrEmpty(a.Get("plugins")).Select(PyText.Str).ToList()).ToList());
        foreach (string t in targetNames)
            if (!pluginNames.Any(n => PyText.Lower(n) == PyText.Lower(t))) pluginNames.Add(t);
        var modNames = ArmDelta(arms.Select(a => Py.ListOrEmpty(Py.DictOrEmpty(a.Get("requested")).Get("providers")).Select(Py.D)
            .Where(v => PyText.Lower(Py.Truthy(v.Get("kind")) ? PyText.Str(v["kind"]) : "") == "mod" && Py.Truthy(v.Get("name")))
            .Select(v => PyText.Str(v["name"])).ToList()).ToList());
        if (pluginNames.Count == 0 && modNames.Count == 0)
            return Unscoped("the arms do not differ and the plan names no target, so there is no population to scope to");
        var statedPlugins = new List<PyDict>();
        foreach (string name in PyOrder.SortedByLowerKey(pluginNames))
        {
            var stated = arms.SelectMany(a => Py.ListOrEmpty(Py.DictOrEmpty(a.Get("requested")).Get("plugins")).Select(Py.D))
                .Where(r => PyText.Lower(PyPath.Name(Py.Truthy(r.Get("name")) ? PyText.Str(r["name"]) : "")) == PyText.Lower(name))
                .Where(r => Py.Truthy(r.Get("path")) && PlanArms.IsSha256(r.Get("sha256")))
                .Select(r => (path: PyText.Str(r["path"]), sha: PyText.Lower(Py.S(r["sha256"])))).Distinct().ToList();
            if (stated.Count != 1)
                return Unscoped($"the population needs {name} by path and hash, and the arms' frozen requests state "
                                + (stated.Count == 0 ? "no copy of it" : $"{stated.Count} different copies"));
            statedPlugins.Add(new PyDict { ["name"] = name, ["path"] = stated[0].path, ["sha256"] = stated[0].sha });
        }
        var made = PopulationFile(statedPlugins, modNames, targetNames, instance);
        if (made.refusal != null) return Unscoped(made.refusal);
        var scopedChecks = planned.Where(c => Py.Truthy(Adapter(c).Get("populationFlag"))).Select(c => Py.S(c["checkId"])).ToList();
        foreach (var c in emitScoped)
        {
            if (Adapter(c).Get("exe") as string == "stateprobe" && made.raceCarriers.Count > 0)
                // stateprobe refuses this population too (every actor band reads RACE); deciding it here keeps the check running
                whole[Py.S(c["checkId"])] = $"{string.Join(", ", made.raceCarriers)} carries RACE records, which change every actor band the "
                                            + "state route check reads, so it runs on the whole order";
            else scopedChecks.Add(Py.S(c["checkId"]));
        }
        return (new PyDict
        {
            ["scoped"] = true, ["file"] = "population.json", ["sha256"] = made.sha256,
            ["identity"] = PopulationRules.Identity(basePopulation, made.sha256),
            ["plugins"] = made.plugins.Select(p => p["name"]).ToList(), ["mods"] = made.mods.Select(m => m["name"]).ToList(),
            ["targets"] = Py.Objs(targetNames), ["scopedProbes"] = Py.Objs(scopedProbes), ["scopedChecks"] = Py.Objs(PyOrder.SortedByCodePoint(scopedChecks)),
            ["wholeOrderChecks"] = whole,
            ["note"] = "the scoped probes measure only rows a reference the population reaches can take part in; their "
                       + "whole-order index is kept, so every such row is computed as in a whole-order run",
        }, made.text);
    }

    /// <summary>`--population-from FILE`: (block, text) when FILE covers this plan's own population, every plugin
    /// (same path and sha256) and every mod folder it needs; else the refusal. The file is used byte for byte.</summary>
    public static (PyDict? block, string? text, string? refusal) Adopt(string path, PyDict own, string? ownText, string basePopulation)
    {
        string p = PyPath.Str(path);
        if (!File.Exists(p)) return (null, null, $"--population-from {p}: no such file");
        if (!Py.Truthy(own.Get("scoped")))
            return (null, null, $"--population-from was given, but this plan cannot state its own population: {PyText.Str(own.Get("reason"))}");
        string text = PyJson.Decode(File.ReadAllBytes(p), bom: false);
        object? parsed;
        try { parsed = PyJson.Loads(text); }
        catch (FormatException ex) { return (null, null, $"--population-from {p} is not JSON ({ex.Message})"); }
        var doc = PyValue.Dict(parsed);
        if (!PyValue.Eq(doc.Get("schema"), 1L)) return (null, null, $"--population-from {p}: schema is not 1");
        var mine = Py.D(PyJson.Loads(ownText!));
        var have = new Dictionary<string, PyDict>(StringComparer.Ordinal);
        foreach (var x in Py.ListOrEmpty(doc.Get("plugins")).Select(Py.D)) have[PyText.Lower(PyText.Str(x.Get("name")))] = x;
        foreach (var x in Py.L(mine["plugins"]).Select(Py.D))
        {
            if (!have.TryGetValue(PyText.Lower(Py.S(x["name"])), out var y))
                return (null, null, $"--population-from {p} does not name {x["name"]}, which this plan's arms differ by or target");
            if (PyText.Lower(PyText.Str(y.Get("sha256"))) != Py.S(x["sha256"]) || PyText.Str(y.Get("path")) != Py.S(x["path"]))
                return (null, null, $"--population-from {p} names {x["name"]} with another copy (path or sha256) than this plan's arms");
        }
        var mods = Py.ListOrEmpty(doc.Get("mods")).Select(x => PyText.Lower(PyText.Str(Py.D(x).Get("name")))).ToHashSet(StringComparer.Ordinal);
        foreach (var x in Py.L(mine["mods"]).Select(Py.D))
            if (!mods.Contains(PyText.Lower(Py.S(x["name"]))))
                return (null, null, $"--population-from {p} does not name mod folder {x["name"]}, which this plan's arms differ by");
        string sha = Hashing.Sha256Text(text);
        var block = new PyDict(own)
        {
            ["sha256"] = sha, ["identity"] = PopulationRules.Identity(basePopulation, sha),
            ["plugins"] = Py.ListOrEmpty(doc.Get("plugins")).Select(x => (object?)PyText.Str(Py.D(x).Get("name"))).ToList(),
            ["mods"] = Py.ListOrEmpty(doc.Get("mods")).Select(x => (object?)PyText.Str(Py.D(x).Get("name"))).ToList(),
            ["targets"] = new List<object?>(Py.ListOrEmpty(doc.Get("targets"))), ["adoptedFrom"] = PyPath.Resolve(p),
        };
        return (block, text, null);
    }
}
