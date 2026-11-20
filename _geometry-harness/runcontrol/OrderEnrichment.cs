using HarnessShared;

namespace RunControl;

/// <summary>The bundle's order step (evalrun.order_step, with bundleenrich's recorded_plugin_headers, _record_imports,
/// pair_headers and the refusals of enrich_order_variant): the manifest's `order` says whether the evaluated pair's
/// order is a choice. The reversed setup's record rows (recordrows.derive) are not derived by this controller; a
/// free pair whose setups hold a record import says so in its sentence.</summary>
public static class OrderEnrichment
{
    /// <summary>The header facts the bundle's own probe emits recorded (Configuration.Plugins[]), keyed by (plugin
    /// lower, sha256 lower), in the order first met. Per setup the smallest emit that records master lists is read;
    /// a reverse part's emit records another setup and is not read.</summary>
    public static List<((string name, string sha) key, OrderChoice.Head head)> RecordedHeaders(string d, PyDict manifest)
    {
        var emits = Ev.Items(manifest.Get("evidence")).Select(PyValue.Dict)
            .Where(e => Ev.Eq(e.Get("kind"), "emit") && Ev.Str(Ev.G(e, "path", "")).EndsWith(".json", StringComparison.Ordinal)
                && !Ev.Str(e.Get("path")).Contains(".reverse.", StringComparison.Ordinal))
            .ToList();
        double Size(PyDict e)
        {
            var info = new FileInfo(PyPath.Join(d, (string)e["path"]!));
            return info.Exists ? info.Length : double.PositiveInfinity;
        }
        var found = new List<((string, string), OrderChoice.Head)>();
        var keys = new HashSet<(string, string)>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var e in emits.OrderBy(Size))   // a stable sort, as Python's
        {
            if (seen.Contains(PyValue.Hash(e.Get("armId")))) continue;
            object? cfg = EnrichCommit.Load(PyPath.Join(d, (string)e["path"]!)) is PyDict em ? em.Get("Configuration") : null;
            var rows = Ev.Items(Ev.OrEmpty(cfg).Get("Plugins")).OfType<PyDict>()
                .Where(x => x.Get("Masters") is List<object?> && PyValue.Truthy(x.Get("Name")) && PyValue.Truthy(x.Get("Sha256"))).ToList();
            if (rows.Count == 0) continue;
            seen.Add(PyValue.Hash(e.Get("armId")));
            foreach (var x in rows)
            {
                var key = (PyText.Lower(Ev.Str(x["Name"])), PyText.Lower(Ev.Str(x["Sha256"])));
                if (keys.Add(key))
                    found.Add((key, new OrderChoice.Head(((List<object?>)x["Masters"]!).Select(Ev.Str).ToList(), x.Get("MasterFlag"))));
            }
        }
        return found;
    }

    /// <summary>The configuration of every credited record import that holds both its transcripts, in receipt order.</summary>
    public static List<(PyDict receipt, PyDict configuration)> RecordImports(string d, PyDict manifest)
    {
        var registry = Ev.OrEmpty(EnrichCommit.Load(PyPath.Join(d, PyValue.Truthy(manifest.Get("registry")) ? Ev.Str(manifest["registry"]) : "registry/check-registry.json")));
        var family = new Dictionary<string, object?>(StringComparer.Ordinal);
        foreach (var c in Ev.Items(registry.Get("checks")).OfType<PyDict>()) family[PyValue.Hash(c.Get("id"))] = c.Get("family");
        var found = new List<(PyDict, PyDict)>();
        foreach (string file in Ev.SortedJson(PyPath.Join(d, "receipts")))
        {
            var rc = Ev.OrEmpty(EnrichCommit.Load(file));
            if (!Ev.Eq(Ev.OrEmpty(rc.Get("adapter")).Get("kind"), "mcp-import") || !PyValue.Truthy(Ev.OrEmpty(rc.Get("validation")).Get("accepted"))) continue;
            if (!Ev.Eq(family.GetValueOrDefault(PyValue.Hash(rc.Get("checkId"))), "records")) continue;
            var outs = Ev.Items(rc.Get("outputs")).Select(o => Ev.G(o, "path")).ToList();
            object? resultOut = outs.FirstOrDefault(o => Ev.Str(o).EndsWith("result.json", StringComparison.Ordinal));
            if ((PyValue.Truthy(resultOut) ? EnrichCommit.Load(PyPath.Join(d, "evidence", (string)resultOut!)) : null) is not PyDict doc) continue;
            var roles = Ev.Items(doc.Get("outputs")).Select(x => Ev.Str(Ev.G(x, "role"))).ToList();
            var copied = outs.Where(o => !Ev.Eq(o, resultOut)).ToList();
            var byRole = new Dictionary<string, object?>(StringComparer.Ordinal);
            for (int i = 0; i < Math.Min(roles.Count, copied.Count); i++) byRole[roles[i]] = copied[i];
            if (!PyValue.Truthy(byRole.GetValueOrDefault("request")) || !PyValue.Truthy(byRole.GetValueOrDefault("response"))) continue;
            object? cfg = Ev.OrEmpty(rc.Get("consumed")).Get("configuration");
            if (!PyValue.Truthy(cfg)) cfg = doc.Get("configuration");
            found.Add((rc, Ev.OrEmpty(cfg)));
        }
        return found;
    }

    /// <summary>The evaluated pair's header facts as the setup with both loads them: what the bundle's emits recorded
    /// for those bytes, else the file's header read at the recorded bytes (PluginHeader, the probes' reader), else absent.</summary>
    public static Dictionary<string, OrderChoice.Head> PairHeaders(string d, PyDict manifest, PyDict plan, List<string> pair,
        List<((string name, string sha) key, OrderChoice.Head head)> recorded, List<(PyDict receipt, PyDict configuration)> imports)
    {
        var pairLow = pair.Select(PyText.Lower).ToHashSet(StringComparer.Ordinal);
        // the plan's setups first, then the record imports' own configurations (the bytes they read)
        var sources = Ev.Items(plan.Get("arms")).Select(a => Ev.Items(Ev.OrEmpty(Ev.G(a, "requested")).Get("plugins")))
            .Concat(imports.Select(i => Ev.Items(i.configuration.Get("plugins"))));
        var want = new List<(string low, PyDict q)>();
        foreach (var plugins in sources)
            foreach (var q in plugins.OfType<PyDict>())
            {
                string low = PyText.Lower(PyValue.Truthy(q.Get("name")) ? Ev.Str(q["name"]) : "");
                if (pairLow.Contains(low) && !want.Any(w => w.low == low)) want.Add((low, q));
            }
        var heads = new Dictionary<string, OrderChoice.Head>(StringComparer.Ordinal);
        foreach (var (low, q) in want)
        {
            string sha = PyText.Lower(PyValue.Truthy(q.Get("sha256")) ? Ev.Str(q["sha256"]) : "");
            var rec = recorded.FirstOrDefault(r => r.key == (low, sha)).head;
            if (rec != null) { heads[low] = rec; continue; }
            if (!PyValue.Truthy(q.Get("path"))) continue;
            string path = PyPath.Str(Ev.Str(q["path"]));
            if (!File.Exists(path) || Hashing.Sha256FileOrNull(path) != sha) continue;
            if (PluginHeader.TryRead(path) is { } h) heads[low] = new OrderChoice.Head(h.Masters, h.MasterFlag);
        }
        return heads;
    }

    // The plan's evaluated plugins by name.
    static List<string> Pair(PyDict plan) =>
        Ev.Items(plan.Get("targets")).OfType<PyDict>().Where(t => PyValue.Truthy(t.Get("name"))).Select(t => Ev.Str(t["name"])).ToList();

    /// <summary>The refusals of enrich_order_variant for a free pair, in its order. It always refuses here: where
    /// Python would derive the reversed setup's rows, this controller says it does not.</summary>
    static void Variant(string d, List<string> pair, Dictionary<string, OrderChoice.Head> heads,
        List<((string name, string sha) key, OrderChoice.Head head)> recorded)
    {
        var manifest = EnrichCommit.Guard(d, "order-variant");
        var allHeads = new Dictionary<string, OrderChoice.Head>(StringComparer.Ordinal);
        foreach (var (key, head) in recorded) allHeads[key.name] = head;
        foreach (var (low, head) in heads) allHeads[low] = head;
        int todo = 0;
        foreach (var (_, cfg) in RecordImports(d, manifest))
        {
            var ps = Ev.Items(cfg.Get("plugins")).ToList();
            var low = ps.OfType<PyDict>().Select(p => PyText.Lower(Ev.Str(p.Get("name")))).ToList();
            if (!pair.All(x => low.Contains(PyText.Lower(x)))) continue;
            ps = ps.Where(p => p is PyDict).ToList();
            int i = low.IndexOf(PyText.Lower(pair[0])), j = low.IndexOf(PyText.Lower(pair[1]));
            if (i > j) (i, j) = (j, i);
            // the earlier of the two moves to just after the later; every other plugin keeps its place
            var moved = ps[i];
            ps.RemoveAt(i);
            ps.Insert(j, moved);
            var order = ps.Select(p => PyText.Lower(Ev.Str(Ev.G(p, "name")))).ToList();
            // every plugin's masters still load before it
            for (int k = 0; k < order.Count; k++)
            {
                if (!allHeads.TryGetValue(order[k], out var h)) continue;
                string? late = h.Masters.FirstOrDefault(m => order.Contains(PyText.Lower(m)) && order.IndexOf(PyText.Lower(m)) > k);
                if (late != null)
                    throw new EnrichRefused($"reversing {pair[0]} and {pair[1]} would load {Ev.Str(PyValue.Dict(ps[k])["name"])} before its master "
                        + $"{late}, so no reversed setup was derived");
            }
            todo++;
        }
        if (todo == 0) throw new EnrichRefused("the bundle holds no credited record import whose setup loads both evaluated plugins");
        throw new EnrichRefused("this controller does not derive record rows from a record import (recordrows.py does), so the reversed "
            + $"setup of {todo} import(s) was left underived; `evalrun.py enrich --kind order-variant` adds it");
    }

    /// <summary>Writes the manifest's `order` statement and returns it. Never throws for a refusal.</summary>
    public static PyDict Step(string d)
    {
        var m = Ev.OrEmpty(EnrichCommit.Load(PyPath.Join(d, "manifest.json")));
        var plan = Ev.OrEmpty(EnrichCommit.Load(PyPath.Join(d, PyValue.Truthy(m.Get("plan")) ? Ev.Str(m["plan"]) : "plan.json")));
        var pair = Pair(plan);
        var names = pair.Cast<object?>().ToList();
        PyDict st;
        if (pair.Count != 2)
            st = new PyDict
            {
                ["status"] = "unknown", ["pair"] = names, ["rule"] = null,
                ["sentence"] = $"The plan names {pair.Count} evaluated plugin(s), not a pair, so no order is stated.",
            };
        else
        {
            var recorded = RecordedHeaders(d, m);
            var heads = PairHeaders(d, m, plan, pair, recorded, RecordImports(d, m));
            var ch = OrderChoice.Decide(pair[0], pair[1], heads);
            if (ch.Forced == null)
                st = new PyDict
                {
                    ["status"] = "unknown", ["pair"] = names, ["rule"] = null,
                    ["sentence"] = "Whether the order is a choice is not said: " + ch.Missing + ".",
                };
            else if (ch.Forced == true)
                st = new PyDict
                {
                    ["status"] = "forced", ["pair"] = names, ["rule"] = ch.Rule, ["loadsFirst"] = ch.LoadsFirst,
                    ["sentence"] = $"The order of {pair[0]} and {pair[1]} is not a choice ({ch.Rule}): {ch.LoadsFirst} loads first.",
                };
            else
            {
                string why;
                try
                {
                    Variant(d, pair, heads, recorded);
                    why = "";
                }
                catch (EnrichRefused ex) { why = ex.Message; }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { why = ex.Message; }   // Python's OSError, said the same way
                st = new PyDict
                {
                    ["status"] = "refused", ["pair"] = names, ["rule"] = ch.Rule,
                    ["sentence"] = $"The order of {pair[0]} and {pair[1]} is free, but the reversed setup was not derived: {why.TrimEnd('.')}.",
                };
            }
        }
        m = Ev.OrEmpty(EnrichCommit.Load(PyPath.Join(d, "manifest.json")));
        m["order"] = st;
        PyJson.WriteEvalFile(PyPath.Join(d, "manifest.json"), m);
        return st;
    }
}
