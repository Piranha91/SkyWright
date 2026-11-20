namespace RunControl;

/// <summary>`runcontrol population` (evalrun.py's cmd_population): the population file for an ad-hoc delta, a patch
/// plugin and its mod folder, by the rule `plan` uses. Writes only --out.</summary>
public static class Population
{
    public static int Run(EvalArgs a)
    {
        if (a.Plugins.Count == 0) throw new Refusal("population needs --plugin, a delta plugin by its path (repeatable).");
        if (a.Out == null) throw new Refusal("population needs --out, the population file to write.");
        var plugins = new List<PyDict>();
        foreach (string raw in a.Plugins)
        {
            string path = PyPath.Str(raw);
            string? sha = File.Exists(path) ? Hashing.Sha256FileOrNull(path) : null;
            if (sha == null)
            {
                Console.Error.WriteLine($"population: plugin {raw} is not a readable file; name the patch plugin by its path.");
                return Exit.Refused;
            }
            plugins.Add(new PyDict { ["name"] = PyPath.Name(path), ["path"] = path, ["sha256"] = sha });
        }
        var targets = a.Targets.Count > 0 ? a.Targets : plugins.Select(r => Py.S(r["name"])).ToList();
        var made = PlanPopulation.PopulationFile(plugins, a.Mods, targets, a.Instance);
        if (made.refusal != null)
        {
            Console.Error.WriteLine($"population: {made.refusal}.");
            return Exit.Refused;
        }
        string outFile = PyPath.Str(a.Out);
        string? parent = Path.GetDirectoryName(Path.GetFullPath(outFile));
        if (!string.IsNullOrEmpty(parent)) Directory.CreateDirectory(parent);
        PyJson.WritePopulationFile(outFile, made.text);
        Console.Out.WriteLine($"population {outFile}: {plugins.Count} plugin(s), {made.mods.Count} mod folder(s); sha256 {made.sha256}; "
            + $"identity {a.EnablePopulation}+population:{made.sha256[..12]}");
        return Exit.Ok;
    }
}
