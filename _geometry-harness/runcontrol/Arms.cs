namespace RunControl;

/// <summary>The arms of a lab entry and each arm's load order (derive_arms, order_arm).</summary>
public static class Arms
{
    /// <summary>The partner units: one plugin list per partner, or one per `group`.</summary>
    public static List<List<string>> Units(PyDict entry)
    {
        var units = new List<List<string>>();
        var byGroup = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        foreach (var p in Py.ListOrEmpty(entry.Get("partners")).Select(Py.D))
        {
            var plugins = Py.Truthy(p.Get("plugins")) ? Py.Strs(p["plugins"]) : [Py.S(p["plugin"])];
            object? g = p.Get("group");
            if (g == null) units.Add(plugins);
            else if (byGroup.TryGetValue(PyText.Repr(g), out var unit))
            {
                foreach (string x in plugins)
                    if (!unit.Contains(x)) unit.Add(x);
            }
            else
            {
                byGroup[PyText.Repr(g)] = plugins;
                units.Add(plugins);
            }
        }
        return units;
    }

    static List<string> Subject(PyDict entry)
    {
        var subject = Py.D(entry["subject"]);
        return Py.Truthy(subject.Get("plugins")) ? Py.Strs(subject["plugins"]) : [Py.S(subject["plugin"])];
    }

    /// <summary>The arms of one evaluation from its selection entry: dependencies; subject; each partner unit;
    /// partners together; combined; subject with each unit; per key, combined + key and the owning pair; combined +
    /// every key; then the entry's extra arms and order variants. Same plugins and order rule collapse to the first.</summary>
    public static List<PyDict> Derive(PyDict entry, PyDict headers)
    {
        var subj = Subject(entry);
        var units = Units(entry);
        var keys = Py.ListOrEmpty(entry.Get("keys")).Select(k => Py.S(Py.D(k)["plugin"])).ToList();
        var all = subj.Concat(units.SelectMany(u => u)).ToList();
        var arms = new List<PyDict>();
        var seen = new HashSet<string>(StringComparer.Ordinal);

        void Add(string id, IEnumerable<string> evaluated, IEnumerable<string>? ks = null, object? variant = null, PyDict? rule = null)
        {
            var ev = Py.Unique(evaluated);
            var keyList = (ks ?? []).ToList();
            string sig = string.Join("\0", PyOrder.SortedByCodePoint(ev)) + "\u0001" + PyJson.Dumps(rule ?? new PyDict(), sortKeys: true);
            if (!seen.Add(sig)) return;
            bool Same(IEnumerable<string> other) => ev.ToHashSet(StringComparer.Ordinal).SetEquals(other);
            string role = ev.Count == 0 ? "vanilla" : keyList.Count > 0 ? "patched" : Same(all) ? "combined"
                : Same(subj) || units.Any(Same) ? "source" : "combined";
            var arm = new PyDict
            {
                ["id"] = id, ["evaluated"] = Py.Objs(ev), ["keys"] = Py.Objs(keyList), ["role"] = role, ["variant"] = variant,
            };
            if (rule != null) arm.Update(rule);
            arms.Add(arm);
        }

        Add("dependencies", []);
        Add("subject", subj);
        for (int i = 0; i < units.Count; i++) Add($"unit:{i}", units[i]);
        if (units.Count > 1) Add("partners", units.SelectMany(u => u));
        Add("combined", all);
        if (units.Count > 1)
            for (int i = 0; i < units.Count; i++) Add($"subject+unit:{i}", subj.Concat(units[i]));
        foreach (string k in keys)
        {
            Add($"combined+key:{k}", all.Append(k), [k]);
            var masters = Py.Strs(Py.DictOrEmpty(headers.Get(k)).Get("masters")).ToHashSet(StringComparer.Ordinal);
            var owners = Enumerable.Range(0, units.Count).Where(i => units[i].Any(masters.Contains)).ToList();
            if (owners.Count == 1) Add($"pair:{owners[0]}+key:{k}", subj.Concat(units[owners[0]]).Append(k), [k]);
        }
        if (keys.Count > 1) Add("combined+keys", all.Concat(keys), keys);
        foreach (var x in Py.ListOrEmpty(entry.Get("extraArms")).Select(Py.D))
        {
            var plugins = Py.Strs(x["plugins"]);
            Add(PyText.Str(x["id"]), plugins, plugins.Where(keys.Contains));
        }
        var byId = new PyDict();
        foreach (var a in arms) byId[Py.S(a["id"])] = a;
        foreach (var v in Py.ListOrEmpty(entry.Get("orderVariants")).Select(Py.D))
        {
            if (byId.Get(PyText.Str(v["of"])) is not PyDict basis)
                throw new Refusal($"order variant {PyText.Str(v["id"])} names arm {PyText.Str(v["of"])}, which this entry does not derive; "
                                  + $"name one of: {string.Join(", ", byId.Keys)}.");
            var rule = new PyDict();
            foreach (string k in new[] { "reverse", "last" })
                if (v.ContainsKey(k)) rule[k] = v[k];
            Add(PyText.Str(v["id"]), Py.Strs(basis["evaluated"]), Py.Strs(basis["keys"]), variant: v["id"], rule: rule);
        }
        return arms;
    }

    /// <summary>The arm's plugins in load order: the source profile's order, master-flagged plugins first, keys the
    /// source does not load after everything. Refuses an illegal order by naming the plugin.</summary>
    public static List<string> Order(PyDict arm, IReadOnlyList<string> baseline, IReadOnlyList<string> sourceOrder, PyDict headers)
    {
        var keyList = Py.Strs(arm.Get("keys"));
        var keys = keyList.ToHashSet(StringComparer.Ordinal);
        var evaluated = Py.Strs(arm["evaluated"]);
        var plugins = Py.Unique(baseline.Concat(evaluated));
        var index = new Dictionary<string, int>(StringComparer.Ordinal);
        for (int i = 0; i < sourceOrder.Count; i++) index[PyText.Lower(sourceOrder[i])] = i;
        foreach (string p in plugins)
            if (!index.ContainsKey(PyText.Lower(p)) && !keys.Contains(p))
                throw new Refusal($"{p} is not in the source profile's load order; add it there or name its place in the entry's places ({{plugin: {{after: plugin}}}}).");
        int late = sourceOrder.Count;
        int Pos(string p) => index.TryGetValue(PyText.Lower(p), out int at) ? at : keys.Contains(p) ? late + keyList.IndexOf(p) : late;

        var order = plugins.OrderBy(p => PluginFiles.IsMaster(p, headers) ? 0 : 1).ThenBy(Pos).ToList();
        if (Py.Truthy(arm.Get("reverse")))
        {
            var slots = Enumerable.Range(0, order.Count).Where(i => evaluated.Contains(order[i]) && !keys.Contains(order[i])).ToList();
            var moved = slots.Select(i => order[i]).Reverse().ToList();
            for (int n = 0; n < slots.Count; n++) order[slots[n]] = moved[n];
        }
        if (Py.Truthy(arm.Get("last")))
        {
            var tail = Py.Strs(arm["last"]).Where(order.Contains).ToList();
            order = order.Where(p => !tail.Contains(p)).Concat(tail).ToList();
        }
        var where = new Dictionary<string, int>(StringComparer.Ordinal);
        for (int i = 0; i < order.Count; i++) where[PyText.Lower(order[i])] = i;
        string armId = arm.ContainsKey("id") ? PyText.Str(arm["id"]) : "?";
        string? seenPlain = null;
        for (int i = 0; i < order.Count; i++)
        {
            string p = order[i];
            if (headers.Get(p) is not PyDict h) throw new Refusal($"{p} has no header read; the preparation could not find its file.");
            foreach (string m in Py.Strs(h.Get("masters")))
            {
                if (!where.TryGetValue(PyText.Lower(m), out int j)) throw new Refusal($"{p} needs master {m}, which arm {armId} does not load.");
                if (j > i) throw new Refusal($"{p} loads before its master {m} in arm {armId}; fix the source order or the variant.");
            }
            if (PluginFiles.IsMaster(p, headers))
            {
                if (seenPlain != null)
                    throw new Refusal($"{p} is ESM-flagged but loads after {seenPlain}, which the game would reorder; "
                                      + "drop the variant that moves it.");
            }
            else seenPlain ??= p;
        }
        return order;
    }
}
