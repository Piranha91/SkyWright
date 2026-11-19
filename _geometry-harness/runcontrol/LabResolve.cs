using HousecarlCore;

namespace RunControl;

/// <summary>A lab entry resolved: where each plugin's file is in the lab, the baseline, the arms and their views.</summary>
public static class LabResolve
{
    // Every role of the entry that may name a plugin's mod folder.
    static IEnumerable<PyDict> Roles(Entry e)
    {
        yield return Py.D(e.Get("subject"));
        foreach (string k in new[] { "partners", "keys", "dependencies", "companionsOf" })
            foreach (var r in Py.ListOrEmpty(e.Get(k))) yield return Py.D(r);
    }

    /// <summary>(mod folder, or null for the game's Data; file) for one plugin in the lab; refuses an absent or
    /// ambiguous one.</summary>
    public static (string? mod, string file) ProviderOf(RunContext ctx, string plugin)
    {
        var named = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var role in Roles(ctx.Entry))
        {
            var plugins = Py.Truthy(role.Get("plugins")) ? Py.L(role["plugins"]) : [role.Get("plugin")];
            foreach (var p in plugins)
                if (Py.Truthy(p) && Py.Truthy(role.Get("mod"))) named.TryAdd(PyText.Lower(Py.S(p)), Py.S(role["mod"]));
        }
        string mods = PyPath.Join(ctx.Instance, "mods");
        if (named.TryGetValue(PyText.Lower(plugin), out var mod))
        {
            string f = PyPath.Join(mods, mod, plugin);
            if (!File.Exists(f)) throw new Refusal($"{plugin} is not in the lab mod folder {mod}; copy it in (copy-in) or fix the entry.");
            return (mod, f);
        }
        string data = PyPath.Join(ctx.Game, "Data", plugin);
        if (File.Exists(data)) return (null, data);
        var hits = Directory.EnumerateFileSystemEntries(mods).Where(d => File.Exists(Path.Combine(d, plugin))).Select(PyPath.Name).ToList();
        if (hits.Count == 1) return (hits[0], PyPath.Join(mods, hits[0], plugin));
        if (hits.Count == 0) throw new Refusal($"{plugin} is in no lab mod folder and not in the game's Data; copy it into the lab first.");
        throw new Refusal($"{plugin} is in {hits.Count} lab mod folders ({string.Join(", ", hits.Take(4))}); name its folder in the "
                          + "entry's dependencies.");
    }

    /// <summary>The source profile's load order, read by the profile reader the probes use.</summary>
    public static List<string> SourceOrder(RunContext ctx)
    {
        string profile = PyPath.Join(ctx.SourceInstance, "profiles", ctx.SourceProfile);
        string lo = PyPath.Join(profile, "loadorder.txt");
        if (!File.Exists(lo)) throw new Refusal($"the source profile has no loadorder.txt at {PyOrder.Fwd(lo)}.");
        return Py.Strs(ctx.Names.Many(Mo2LoadOrder.ReadComposition(profile).OrderedPluginNames));
    }

    public static PyDict Resolve(RunContext ctx)
    {
        var e = ctx.Entry;
        var subject = Py.D(e.Get("subject"));
        var subj = Py.Truthy(subject.Get("plugins")) ? Py.Strs(subject["plugins"]) : [Py.S(subject["plugin"])];
        var keyRoles = Py.ListOrEmpty(e.Get("keys")).Select(Py.D).ToList();
        var keys = keyRoles.Select(k => Py.S(k["plugin"])).ToList();
        var measured = e.Measured();
        var partnerPlugins = Arms.Units(measured).SelectMany(u => u).ToList();
        var companions = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        foreach (var kv in Py.DictOrEmpty(e.Get("companions"))) companions[PyText.Lower(kv.Key)] = Py.Strs(kv.Value);
        var source = SourceOrder(ctx);   // read before the headers: its spellings outrank theirs
        var places = Py.DictOrEmpty(e.Get("places"));
        foreach (var (p, where) in places)
        {
            if (source.Contains(p)) throw new Refusal($"{p} is in the source profile's load order already; drop it from the entry's places.");
            object? after = Py.DictOrEmpty(where).Get("after");
            if (after is not string a || !source.Contains(a))
                throw new Refusal($"places: {p} is to load after {PyText.Str(after)}, which the source profile does not load; "
                                  + "name a plugin it loads.");
            source.Insert(source.IndexOf(a) + 1, p);
        }
        var headers = new PyDict();
        var providers = new PyDict();
        var baselinePins = Py.Strs(e.Get("baseline"));
        var todo = new Queue<string>(subj.Concat(partnerPlugins).Concat(keys).Concat(baselinePins).Concat(companions.Values.SelectMany(c => c)));
        while (todo.Count > 0)
        {
            string p = ctx.Names.Of(todo.Dequeue())!;
            if (headers.ContainsKey(p)) continue;
            var (mod, file) = ProviderOf(ctx, p);
            var h = PluginFiles.Header(file);
            h["masters"] = ctx.Names.Many(Py.L(h["masters"]));
            headers[p] = h;
            providers[p] = mod;
            foreach (string m in Py.Strs(h["masters"])) todo.Enqueue(m);
        }
        List<string> Masters(string p) => Py.Strs(Py.D(headers[p])["masters"]);
        foreach (var (p, where) in places)
        {
            // a place is checked against the plugin's own masters: one that loads later would be a broken order
            var late = Py.Strs(Py.DictOrEmpty(headers.Get(p)).Get("masters"))
                .Where(m => source.Contains(m) && source.IndexOf(m) > source.IndexOf(p)).OrderBy(m => source.IndexOf(m)).ToList();
            if (late.Count > 0)
                throw new Refusal($"places: {p} after {PyText.Str(Py.D(where).Get("after"))} loads before its master {late[0]}; "
                                  + $"place it after {late[^1]}.");
        }
        var evaluated = subj.Concat(partnerPlugins).Concat(keys).ToHashSet(StringComparer.Ordinal);
        // baseline: the entry's pins plus every master closure of the subject and keys outside the evaluated set
        var baseline = new List<string>(baselinePins);
        List<string> Closure(string p, List<string> acc)
        {
            foreach (string m in Masters(p))
                if (!acc.Contains(m))
                {
                    acc.Add(m);
                    Closure(m, acc);
                }
            return acc;
        }
        foreach (string p in subj.Concat(keys))
            foreach (string m in Closure(p, []))
                if (!evaluated.Contains(m) && !baseline.Contains(m)) baseline.Add(m);
        // a partner unit carries its own masters that are not baseline, and its companions
        var entryUnits = new List<object?>();
        foreach (var p in Py.ListOrEmpty(e.Get("partners")).Select(Py.D))
        {
            var plugins = Py.Truthy(p.Get("plugins")) ? Py.Strs(p["plugins"]) : [Py.S(p["plugin"])];
            foreach (string q in plugins.ToList())
            {
                foreach (string m in Closure(q, []))
                    if (!baseline.Contains(m) && !evaluated.Contains(m) && !plugins.Contains(m)) plugins.Add(m);
                foreach (string c in companions.GetValueOrDefault(PyText.Lower(q), []))
                    if (!plugins.Contains(c)) plugins.Add(c);
            }
            entryUnits.Add(new PyDict(p) { ["plugins"] = Py.Objs(plugins) });
        }
        var subjFull = new List<string>(subj);
        foreach (string q in subj)
            subjFull.AddRange(companions.GetValueOrDefault(PyText.Lower(q), []).Where(c => !subjFull.Contains(c)).ToList());
        measured["partners"] = entryUnits;
        measured["subject"] = new PyDict(subject) { ["plugins"] = Py.Objs(subjFull) };
        var arms = Arms.Derive(measured, headers);
        var keyMods = new Dictionary<string, object?>(StringComparer.Ordinal);
        foreach (var k in keyRoles) keyMods[Py.S(k["plugin"])] = k.Get("mod");
        foreach (var a in arms)
        {
            var plugins = Arms.Order(a, baseline, source, headers);
            a["plugins"] = Py.Objs(plugins);
            var mods = new List<string>();
            foreach (string k in plugins.Where(keys.Contains).Reverse())
                if (Py.Truthy(keyMods.GetValueOrDefault(k))) mods.Add(Py.S(keyMods[k]));
            foreach (string p in Enumerable.Reverse(plugins))
                if (providers.Get(p) is string m && m.Length > 0 && !mods.Contains(m) && !keys.Contains(p)) mods.Add(m);
            a["mods"] = Py.Objs(mods);
        }
        return new PyDict
        {
            ["headers"] = headers, ["providers"] = providers, ["baseline"] = Py.Objs(baseline), ["arms"] = arms.Cast<object?>().ToList(),
            ["subject"] = Py.Objs(subj), ["partners"] = entryUnits.Select(u => Py.D(u)["plugins"]).ToList(), ["keys"] = Py.Objs(keys),
        };
    }
}
