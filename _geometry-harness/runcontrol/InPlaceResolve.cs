using HarnessShared;
using HousecarlCore;

namespace RunControl;

/// <summary>An in-place entry's two arms, from the chosen profile as MO2 resolves it: `combined` is the profile with
/// the selected mod folders enabled at their MO2 priority, `without-selection` the profile with them disabled. A
/// plugin only selected folders provide drops from the baseline; one an unselected folder or the game's Data also
/// provides stays, from there. The profile's files are never changed; both arms are candidates over it.
/// The profile is read by Mo2LoadOrder.ReadComposition and each plugin's copy by HarnessOrderCli.PluginLayerMap,
/// the readers the probes themselves use.</summary>
public static class InPlaceResolve
{
    public static PyDict Resolve(RunContext ctx)
    {
        string prof = ctx.Profile;
        string pdir = PyPath.Join(ctx.Instance, "profiles", prof);
        if (!File.Exists(PyPath.Join(pdir, "modlist.txt")))
            throw new Refusal($"the profile {prof} has no readable modlist.txt at {PyOrder.Fwd(pdir)}; name a profile of this instance.");
        var comp = Mo2LoadOrder.ReadComposition(pdir);
        var sel = Py.Strs(Py.DictOrEmpty(ctx.Entry.Get("selection")).Get("mods"));
        if (sel.Count == 0) throw new Refusal("the selection names no mod folder; tick at least one of the profile's mods.");
        var known = comp.EnabledMods.Concat(comp.DisabledMods).Select(PyText.Lower).ToHashSet(StringComparer.Ordinal);
        foreach (string m in sel)
            if (!known.Contains(PyText.Lower(m)))
                throw new Refusal($"{m} is not a mod folder of profile {prof}; pick one of the mods its modlist.txt lists.");
        var selLow = sel.Select(PyText.Lower).ToHashSet(StringComparer.Ordinal);
        var enabledLow = comp.EnabledMods.Select(PyText.Lower).ToHashSet(StringComparer.Ordinal);
        var switchedOn = comp.DisabledMods.Where(m => selLow.Contains(PyText.Lower(m))).ToList();   // selected mods MO2 has disabled
        foreach (string need in new[] { "loadorder.txt", "plugins.txt" })
            if (!File.Exists(PyPath.Join(pdir, need)))
                throw new Refusal($"the profile {prof} has no {need} at {PyOrder.Fwd(pdir)}, so its load order cannot be read; open the "
                                  + "profile in MO2 once so it writes the file, or pick another profile.");
        var order = Py.Strs(ctx.Names.Many(comp.OrderedPluginNames));
        // a name plugins.txt lists without `*` is unchecked and does not load; every other name in loadorder.txt loads
        var unchecked_ = new HashSet<string>(comp.InactivePluginNames, StringComparer.OrdinalIgnoreCase);
        var active = order.Where(p => !unchecked_.Contains(p)).ToList();
        var withoutMods = comp.EnabledMods.Where(m => !selLow.Contains(PyText.Lower(m))).ToList();
        var candidateMods = comp.ModsByPriority.Where(m => enabledLow.Contains(PyText.Lower(m)) || selLow.Contains(PyText.Lower(m))).ToList();

        string modsDir = PyPath.Join(ctx.Instance, "mods"), dataDir = PyPath.Join(ctx.Game, "Data");
        string overwriteDir = PyPath.Join(ctx.Instance, "overwrite");

        // The plugins a view loads, each from the copy the probes would read; a plugin nothing in the view provides is left out.
        (List<string> plugins, PyDict providers, PyDict headers) View(List<string> mods, IEnumerable<string> names)
        {
            var copies = HarnessOrderCli.PluginLayerMap(pdir, mods, modsDir, dataDir, overwriteDir);
            var plugins = new List<string>();
            var providers = new PyDict();
            var headers = new PyDict();
            foreach (string p in names)
            {
                if (!copies.TryGetValue(PyPath.Name(p), out var path)) continue;
                string? layer = LayerInView(path, mods, modsDir, dataDir, overwriteDir);
                if (layer == null) continue;   // only a folder outside this view provides it: MO2 would not load it
                plugins.Add(p);
                providers[p] = layer is "Data" or "overwrite" ? null : layer;
                var h = PluginFiles.Header(path);
                h["masters"] = ctx.Names.Many(Py.L(h["masters"]));
                headers[p] = h;
            }
            return (plugins, providers, headers);
        }

        var strongestFirst = candidateMods.Where(m => switchedOn.Any(s => PyText.Lower(s) == PyText.Lower(m)));
        var added = NewPluginPlacement.Added(modsDir, strongestFirst, active, ctx.Names);
        var (cPlugins, cProviders, cHeaders) = View(candidateMods, active.Concat(added));
        if (added.Count > 0) cPlugins = NewPluginPlacement.Place(cPlugins, added, p => PluginFiles.IsMaster(p, cHeaders));
        var (wPlugins, wProviders, wHeaders) = View(withoutMods, active);
        var subject = cPlugins.Where(p => selLow.Contains(PyText.Lower(cProviders.Get(p) as string ?? "")) && !wPlugins.Contains(p)).ToList();
        var arms = new List<object?>
        {
            new PyDict
            {
                ["id"] = "combined", ["role"] = switchedOn.Count > 0 ? "the profile with the selected mods enabled" : "the profile as it is",
                ["keys"] = new List<object?>(), ["evaluated"] = Py.Objs(subject), ["plugins"] = Py.Objs(cPlugins),
                ["mods"] = Py.Objs(candidateMods), ["switchedOn"] = Py.Objs(switchedOn),
                ["appendedPlugins"] = Py.Objs(added.Where(cPlugins.Contains)),
            },
            new PyDict
            {
                ["id"] = "without-selection", ["role"] = "the profile without the selected mods", ["keys"] = new List<object?>(),
                ["evaluated"] = new List<object?>(), ["plugins"] = Py.Objs(wPlugins), ["mods"] = Py.Objs(withoutMods),
            },
        };
        return new PyDict
        {
            ["headers"] = new PyDict(wHeaders).Update(cHeaders), ["providers"] = new PyDict(wProviders).Update(cProviders),
            ["baseline"] = Py.Objs(wPlugins), ["arms"] = arms, ["subject"] = Py.Objs(subject), ["partners"] = new List<object?>(),
            ["keys"] = new List<object?>(), ["selection"] = Py.Objs(sel), ["withoutProviders"] = wProviders,
        };
    }

    // Which of a view's own layers holds a copy: "overwrite", the view's mod folder as the view spells it, "Data";
    // null when the copy sits in a folder the view does not contain.
    static string? LayerInView(string path, IReadOnlyList<string> viewMods, string modsDir, string dataDir, string overwriteDir)
    {
        string folder = Path.GetDirectoryName(path) ?? "";
        if (PyOrder.SamePath(folder, overwriteDir)) return "overwrite";
        foreach (string m in viewMods)
            if (PyOrder.SamePath(folder, PyPath.Join(modsDir, m))) return m;
        return PyOrder.SamePath(folder, dataDir) ? "Data" : null;
    }

    /// <summary>The entry's ticked checks closed over the registry: every prerequisite of a ticked check, every derived
    /// check whose prerequisites are all selected and which the controller computes, and depth deep when a ticked check
    /// judges a state other than the baseline. Returns (check ids, depth).</summary>
    public static (List<string> select, string depth) Select(RunContext ctx, string? registryPath = null)
    {
        var reg = new PyDict();
        foreach (var c in Py.L(Py.D(PyJson.ReadPipelineFile(registryPath ?? Checkout.Registry))["checks"]).Select(Py.D)) reg[Py.S(c["id"])] = c;
        var evaluation = Py.DictOrEmpty(ctx.Entry.Get("evaluation"));
        var sel = Py.Strs(evaluation.Get("select")).ToHashSet(StringComparer.Ordinal);
        var unknown = PyOrder.SortedByCodePoint(sel.Where(c => !reg.ContainsKey(c)));
        if (unknown.Count > 0) throw new Refusal($"the selection names {string.Join(", ", unknown)}, which the check registry does not have.");
        var todo = new Stack<string>(sel);
        while (todo.Count > 0)
            foreach (string pre in Py.Strs(Py.D(reg[todo.Pop()]).Get("prerequisites")))
                if (sel.Add(pre)) todo.Push(pre);
        string[] computedDerived = ["patch.order-evaluation", "patch.candidate-qualification"];   // the controller's own derived set
        for (bool changed = true; changed;)
        {
            changed = false;
            foreach (var (id, c) in reg)
                if (Py.DictOrEmpty(Py.D(c).Get("adapter")).Get("kind") as string == "derived" && computedDerived.Contains(id) && !sel.Contains(id)
                    && Py.Strs(Py.D(c).Get("prerequisites")).All(sel.Contains))
                {
                    sel.Add(id);
                    changed = true;
                }
        }
        bool deep = sel.Any(c => Py.DictOrEmpty(Py.D(reg[c]).Get("inputs")).Get("states") as string == "selected");
        string depth = deep ? "deep" : Py.Truthy(evaluation.Get("depth")) ? Py.S(evaluation["depth"]) : "standard";
        return (PyOrder.SortedByCodePoint(sel), depth);
    }
}
