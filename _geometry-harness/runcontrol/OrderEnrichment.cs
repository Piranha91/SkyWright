using HarnessShared;

namespace RunControl;

/// <summary>The bundle's order step (evalrun.order_step, with bundleenrich's recorded_plugin_headers and
/// pair_headers): the manifest's `order` says whether the evaluated pair's order is a choice. The reversed setup's
/// record rows are derived from record imports, which only an AI-side evaluation has: the houseCARL integration module
/// derives them on its overlay bundle (recordenrich.py), and a free pair's sentence here says so.</summary>
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

    /// <summary>The evaluated pair's header facts as the setup with both loads them: what the bundle's emits recorded
    /// for those bytes, else the file's header read at the recorded bytes (PluginHeader, the probes' reader), else absent.</summary>
    public static Dictionary<string, OrderChoice.Head> PairHeaders(string d, PyDict manifest, PyDict plan, List<string> pair,
        List<((string name, string sha) key, OrderChoice.Head head)> recorded)
    {
        var pairLow = pair.Select(PyText.Lower).ToHashSet(StringComparer.Ordinal);
        // the plan's setups
        var sources = Ev.Items(plan.Get("arms")).Select(a => Ev.Items(Ev.OrEmpty(Ev.G(a, "requested")).Get("plugins")));
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

    /// <summary>The sentence a free pair is refused with: the reversed setup's record rows come from record imports,
    /// which only an AI-side evaluation has.</summary>
    public const string NotDerived = "this controller does not derive record rows for the reversed setup; the houseCARL "
        + "integration module derives them from an AI-side evaluation's record imports on its overlay bundle";

    /// <summary>The refusal of a free pair's reversed setup: the bundle's own refusal when it is already enriched with
    /// one, else NotDerived.</summary>
    static void Variant(string d)
    {
        EnrichCommit.Guard(d, "order-variant");
        throw new EnrichRefused(NotDerived);
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
            var heads = PairHeaders(d, m, plan, pair, recorded);
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
                    Variant(d);
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
