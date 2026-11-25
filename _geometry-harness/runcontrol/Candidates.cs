using HarnessShared;

namespace RunControl;

/// <summary>The candidate configurations a run prepares (one per arm) and, for a lab entry, the base profile.</summary>
public static class Candidates
{
    /// <summary>(file name, arm, document) per arm: the baseline arm is candidate-00-baseline.json, the rest in order.
    /// The baseline is `combined` for a lab entry and `without-selection` for an in-place one.</summary>
    public static List<(string name, PyDict arm, PyDict doc)> Files(RunContext ctx, PyDict res)
    {
        string first = ctx.InPlace != null ? "without-selection" : "combined";
        var arms = Py.L(res["arms"]).Select(Py.D).OrderBy(a => a["id"] as string == first ? 0 : 1).ToList();
        var files = new List<(string, PyDict, PyDict)>();
        for (int i = 0; i < arms.Count; i++)
        {
            var a = arms[i];
            string id = PyText.Str(a["id"]);
            files.Add((i == 0 ? "candidate-00-baseline.json" : $"candidate-{i:00}.json", a, new PyDict
            {
                ["SchemaVersion"] = 1L, ["Name"] = $"{ctx.Id}: {id}", ["BaseProfile"] = ctx.Profile,
                ["Plugins"] = a["plugins"], ["Mods"] = a["mods"],
                ["Changes"] = new List<object?> { $"runcontrol arm {id}" },
                ["Provenance"] = new PyDict
                {
                    ["arm"] = a["id"], ["role"] = a["role"], ["keys"] = a["keys"], ["variant"] = a.Get("variant"), ["entry"] = ctx.Id,
                },
            }));
        }
        return files;
    }

    /// <summary>Writes one candidate in the pipeline's own bytes, then reads it back through the probes' reader so a
    /// file a probe would refuse is refused here.</summary>
    public static void Write(string path, PyDict doc)
    {
        PyJson.WritePipelineFile(path, doc);
        try { CandidateFile.Read(path); }
        catch (Exception ex) when (ex is InvalidDataException or System.Text.Json.JsonException)
        {
            throw new Refusal($"a candidate is not one a probe can read ({ex.Message}); fix the entry or the selection so the arm loads at least one plugin.");
        }
    }

    /// <summary>The lab base profile's files: the combined arm's order and mods, and the template's INIs.</summary>
    public static PyDict ProfilePayload(RunContext ctx, PyDict res)
    {
        var arms = Py.L(res["arms"]).Select(Py.D).ToList();
        var combined = arms.First(a => a["id"] as string == "combined");
        var combinedMods = Py.Strs(combined["mods"]);
        var combinedPlugins = Py.Strs(combined["plugins"]);
        var universe = Py.Unique(arms.SelectMany(a => Py.Strs(a["mods"])));
        string modlist = "# runcontrol base profile; strongest first\n"
                         + string.Concat(universe.Where(m => !combinedMods.Contains(m)).Select(m => "-" + m + "\n"))
                         + string.Concat(combinedMods.Select(m => "+" + m + "\n"));
        var files = new PyDict
        {
            ["modlist.txt"] = PyJson.Utf8(modlist),
            ["plugins.txt"] = PyJson.Utf8(string.Concat(combinedPlugins.Select(p => "*" + p + "\n"))),
            ["loadorder.txt"] = PyJson.Utf8(string.Join("\n", combinedPlugins) + "\n"),
            // the profile's own INIs are read only with LocalSettings on
            ["settings.ini"] = PyJson.Utf8("[General]\r\nLocalSettings=true\r\n"),
        };
        if (Py.Truthy(ctx.Template))
        {
            string tdir = PyPath.Join(ctx.Instance, "profiles", ctx.Template!);
            foreach (string f in PyOrder.SortedPaths(Directory.GetFiles(tdir, "*.ini")))
                if (PyText.Lower(PyPath.Name(f)) != "settings.ini") files[PyPath.Name(f)] = File.ReadAllBytes(f);
        }
        return files;
    }

    /// <summary>Creates the base profile, or accepts an existing one with exactly these files; never overwrites another.</summary>
    public static string WriteProfile(RunContext ctx, PyDict files)
    {
        string d = PyPath.Join(ctx.Instance, "profiles", ctx.Profile);
        if (Directory.Exists(d) || File.Exists(d))
        {
            var have = Directory.Exists(d) ? Directory.GetFiles(d).ToDictionary(PyPath.Name, File.ReadAllBytes, StringComparer.Ordinal) : [];
            bool same = have.Count == files.Count
                        && files.All(kv => have.TryGetValue(kv.Key, out var b) && b.AsSpan().SequenceEqual((byte[])kv.Value!));
            if (!same)
                throw new Refusal($"lab profile {ctx.Profile} exists with other contents; name another profile in the entry "
                                  + "rather than overwrite it.");
            return d;
        }
        Directory.CreateDirectory(d);
        foreach (var (name, data) in files)
        {
            using var f = new FileStream(PyPath.Join(d, name), FileMode.CreateNew, FileAccess.Write);
            f.Write((byte[])data!);
        }
        return d;
    }
}
