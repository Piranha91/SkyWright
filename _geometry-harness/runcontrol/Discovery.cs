namespace RunControl;

/// <summary>The plugin pairs a plan is about and which discovery hits name them (evalrun.py): the part finalize
/// reads. A hit is relevant when its own masters, FOMOD condition plugins or pair name a plan target or a plugin
/// of a plan pair.</summary>
public static class Discovery
{
    /// <summary>The discovery probes whose hit list (schema 1) qualification reads.</summary>
    public static readonly string[] DiscoveryExes = ["patchprobe", "poolprobe", "fomodprobe"];

    public const string NotRelevantWhy = "its masters, FOMOD conditions and pair name no plan target and no plugin of a plan pair (its own "
        + "masters only: a hit that masters just a dependent of a target is counted here)";

    // tuple(sorted([x, y], key=str.lower)): stable, so equal keys keep their order.
    static (string, string) Ordered(string x, string y) =>
        PyOrder.CompareCodePoints(PyText.Lower(y), PyText.Lower(x)) < 0 ? (y, x) : (x, y);

    static List<string> TargetNames(PyDict plan) =>
        Ev.Items(plan.Get("targets")).Where(t => t is PyDict d && PyValue.Truthy(d.Get("name")))
            .Select(t => Ev.Str(((PyDict)t!)["name"])).ToList();

    /// <summary>The pairs whose relative order flips between the baseline and this arm, plus, for a supplied arm,
    /// each plan target with each supplied plugin.</summary>
    public static List<(string, string)> TargetPairs(PyDict plan, PyDict arm)
    {
        PyDict? bas = null;
        foreach (var a in PyValue.Iter(plan["arms"]))
            if (Ev.Eq(PyValue.Dict(a)["kind"], "baseline")) { bas = PyValue.Dict(a); break; }
        var pairs = new List<(string, string)>();
        var seen = new HashSet<(string, string)>();
        void Add(string x, string y)
        {
            var key = Ordered(x, y);
            if (!seen.Contains(key) && PyText.Lower(key.Item1) != PyText.Lower(key.Item2))
            {
                seen.Add(key);
                pairs.Add(key);
            }
        }
        if (bas != null && bas.Count > 0 && !Ev.Eq(arm["armId"], bas["armId"]))
        {
            var b = Ev.Items(bas.Get("plugins")).Select(Ev.Str).ToList();
            var c = Ev.Items(arm.Get("plugins")).Select(Ev.Str).ToList();
            var bi = new Dictionary<string, int>(StringComparer.Ordinal);
            var ci = new Dictionary<string, int>(StringComparer.Ordinal);
            for (int i = 0; i < b.Count; i++) bi[PyText.Lower(b[i])] = i;
            for (int i = 0; i < c.Count; i++) ci[PyText.Lower(c[i])] = i;
            var common = c.Where(n => bi.ContainsKey(PyText.Lower(n))).ToList();
            var bIdx = common.Select(n => bi[PyText.Lower(n)]).ToArray();
            var cIdx = common.Select(n => ci[PyText.Lower(n)]).ToArray();
            for (int i = 0; i < common.Count; i++)
                for (int j = i + 1; j < common.Count; j++)
                    if ((bIdx[i] < bIdx[j]) != (cIdx[i] < cIdx[j])) Add(common[i], common[j]);
        }
        var targets = TargetNames(plan);
        object? supplied = arm.ContainsKey("supplied") ? arm["supplied"] : new PyDict();
        foreach (var file in Ev.Items(Ev.OrEmpty(supplied).Get("declaredFiles")))
            foreach (string t in targets) Add(t, PyPath.Name(Ev.Str(file)));
        return pairs;
    }

    /// <summary>A plan with a reviewArm: each target with each plugin it clashes with on that arm, from the A and B
    /// fields of the review arm's accepted conflict-file receipts.</summary>
    public static List<(string, string)> ReviewPairs(string run, PyDict plan)
    {
        var found = new List<(string, string)>();
        object? review = plan.Get("reviewArm");
        if (!PyValue.Truthy(review)) return found;
        var targets = new HashSet<string>(TargetNames(plan).Select(PyText.Lower), StringComparer.Ordinal);
        var seen = new HashSet<(string, string)>();
        string dir = PyPath.Join(run, "receipts");
        var names = Directory.Exists(dir) ? Directory.EnumerateFileSystemEntries(dir).ToList() : [];
        List<string> Glob(string tail) =>
            PyOrder.SortedPaths(names.Where(p => Path.GetFileName(p).EndsWith(tail, StringComparison.OrdinalIgnoreCase)));
        foreach (string f in Glob($"__{Ev.Str(review)}__arm.json").Concat(Glob($"__{Ev.Str(review)}.json")))
        {
            var r = Ev.OrEmpty(PyJson.LoadEvalFile(f));
            if (!Ev.Eq(r.Get("armId"), review) || !PyValue.Truthy(Ev.OrEmpty(r.Get("validation")).Get("accepted"))) continue;
            foreach (var o in Ev.Items(r.Get("outputs")))
            {
                object? path = Ev.G(o, "path");
                if (!PyValue.Truthy(path) || PyJson.LoadEvalFile(PyPath.Join(run, Ev.Str(path))) is not PyDict doc) continue;
                foreach (var c in Ev.Items(doc.Get("Conflicts")))
                {
                    string a = PyValue.Truthy(Ev.G(c, "A")) ? Ev.Str(Ev.G(c, "A")) : "", b = PyValue.Truthy(Ev.G(c, "B")) ? Ev.Str(Ev.G(c, "B")) : "";
                    foreach (var (t, other) in new[] { (a, b), (b, a) })
                        if (targets.Contains(PyText.Lower(t)) && other.Length > 0 && !targets.Contains(PyText.Lower(other)))
                        {
                            var key = Ordered(t, other);
                            if (seen.Add(key)) found.Add(key);
                        }
                }
            }
        }
        return found;
    }

    /// <summary>Every pair the whole plan is about: the review pairs, then each arm's.</summary>
    public static List<(string, string)> AllPlanPairs(string run, PyDict plan)
    {
        var found = new List<(string, string)>();
        var seen = new HashSet<(string, string)>();
        foreach (var pair in ReviewPairs(run, plan))
            if (seen.Add(Ordered(pair.Item1, pair.Item2))) found.Add(Ordered(pair.Item1, pair.Item2));
        foreach (var arm in PyValue.Iter(plan["arms"]))
            foreach (var pair in TargetPairs(plan, PyValue.Dict(arm)))
                if (seen.Add(Ordered(pair.Item1, pair.Item2))) found.Add(Ordered(pair.Item1, pair.Item2));
        return found;
    }

    /// <summary>What makes a discovered patch about this evaluation: a plan target, or a plugin of a pair the plan moves.</summary>
    public static HashSet<string> RelevantPlugins(string run, PyDict plan)
    {
        var names = new HashSet<string>(TargetNames(plan).Select(PyText.Lower), StringComparer.Ordinal);
        foreach (var (x, y) in AllPlanPairs(run, plan))
        {
            names.Add(PyText.Lower(x));
            names.Add(PyText.Lower(y));
        }
        return names;
    }

    /// <summary>The plugins a discovery hit names directly: its masters, its FOMOD condition plugins and its pair.</summary>
    public static HashSet<string> HitNames(PyDict h)
    {
        var detail = Ev.OrEmpty(h.Get("detail"));
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var x in Ev.Items(h.Get("masters"))) names.Add(PyText.Lower(Ev.Str(x)));
        foreach (var x in Ev.Items(detail.Get("conditions"))) names.Add(PyText.Lower(Ev.Str(x)));
        foreach (var x in Ev.Items(detail.Get("pair"))) names.Add(PyText.Lower(Ev.Str(x)));
        return names;
    }

    /// <summary>{checkId: {armId: {...}}} for every accepted discovery receipt: the relevant hits' files and the
    /// not-relevant count, or computed false with the reason.</summary>
    public static PyDict Relevance(string run, PyDict plan, PyDict receipts)
    {
        var relevant = RelevantPlugins(run, plan);
        var found = new PyDict();
        foreach (var r in PyOrder.SortedBy(receipts.Select(kv => PyValue.Dict(kv.Value)), x => Ev.Str(x.Get("receiptId"))))
        {
            object? exe = Ev.OrEmpty(r.Get("adapter")).Get("exe");
            if (exe is not string e || !DiscoveryExes.Contains(e) || !PyValue.Truthy(Ev.OrEmpty(r.Get("validation")).Get("accepted"))) continue;
            string cid = Ev.Str(r.Get("checkId"));
            if (!found.ContainsKey(cid)) found[cid] = new PyDict();
            var slot = (PyDict)found[cid]!;
            string arm = Ev.Str(r.Get("armId"));
            if (relevant.Count == 0)
            {
                slot[arm] = new PyDict
                {
                    ["computed"] = false,
                    ["why"] = "the plan names no target and no pair, so no hit can be judged relevant to it (the rule reads the plan's targets and pairs)",
                };
                continue;
            }
            var outs = PyValue.Truthy(r.Get("outputs")) ? (List<object?>)r["outputs"]! : [];
            var o = outs.Count > 0 ? PyValue.Dict(outs[0]) : new PyDict();
            string? f = PyValue.Truthy(o.Get("path")) ? PyPath.Join(run, Ev.Str(o["path"])) : null;
            object? doc = f != null && Ev.Exists(f) && Ev.Eq(Hashing.Sha256FileOrNull(f), o.Get("sha256")) ? PyJson.LoadEvalFile(f) : null;
            if (doc is not PyDict d || d.Get("hits") is not List<object?> all)
            {
                slot[arm] = new PyDict { ["computed"] = false, ["why"] = $"receipt {Ev.Str(r.Get("receiptId"))}'s hit list cannot be read as it recorded it" };
                continue;
            }
            var hits = all.Where(h => h is PyDict hd && PyValue.Truthy(hd.Get("file"))).Select(h => (PyDict)h!).ToList();
            var rel = hits.Where(h => HitNames(h).Overlaps(relevant)).Select(h => (object?)Ev.Str(h["file"])).ToList();
            slot[arm] = new PyDict
            {
                ["computed"] = true, ["relevant"] = rel,
                ["notRelevant"] = new PyDict { ["count"] = (long)(hits.Count - rel.Count), ["why"] = NotRelevantWhy },
            };
        }
        return found;
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

    /// <summary>Every arm's generated not-explored rows (a deep plan), with the arm and check named.</summary>
    public static List<object?> GeneratedNotExplored(string run, PyDict plan)
    {
        var rows = new List<object?>();
        if (!Ev.Eq(Ev.OrEmpty(plan.Get("states")).Get("depth"), "deep")) return rows;
        foreach (var cid in Ev.Items(Ev.OrEmpty(PyValue.Dict(plan["states"]).Get("fromCensus")).Get("checks")))
            foreach (var armRaw in PyValue.Iter(plan["arms"]))
            {
                object? aid = PyValue.Dict(armRaw)["armId"];
                var doc = Ev.OrEmpty(PyJson.LoadEvalFile(PyPath.Join(run, "outputs", Ev.Str(cid), $"{Ev.Str(aid)}.states.json")));
                foreach (var r in Ev.Items(doc.Get("notExplored")))
                    rows.Add(new PyDict { ["checkId"] = cid, ["armId"] = aid }.Update(PyValue.Dict(r)));
            }
        return rows;
    }
}
