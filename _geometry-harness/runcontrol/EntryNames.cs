namespace RunControl;

/// <summary>One spelling per plugin. The game matches plugin names without regard to case; the first spelling seen
/// is the plugin's name everywhere after: the entry's first, then the source profile's, then the headers'.</summary>
public sealed class PluginNames
{
    readonly Dictionary<string, string> _by = new(StringComparer.Ordinal);

    public string? Of(object? p)
    {
        if (p == null) return null;
        string text = PyText.Str(p), key = PyText.Lower(text);
        if (!_by.TryGetValue(key, out var first)) _by[key] = first = text;
        return first;
    }

    public List<object?> Many(IEnumerable<object?>? ps) => (ps ?? []).Select(p => (object?)Of(p)).ToList();
    public List<object?> Many(IEnumerable<string> ps) => ps.Select(p => (object?)Of(p)).ToList();

    /// <summary>normalise_entry: the entry with every plugin name it holds in its one spelling.</summary>
    public PyDict Normalise(PyDict entry)
    {
        var e = Py.D(Py.Copy(entry));
        var roles = new List<object?> { e.Get("subject") is { } s && Py.Truthy(s) ? s : new PyDict() };
        foreach (string k in new[] { "partners", "keys", "dependencies", "companionsOf" }) roles.AddRange(Py.ListOrEmpty(e.Get(k)));
        foreach (var r in roles.Select(Py.D))
        {
            if (r.Get("plugin") != null) r["plugin"] = Of(r["plugin"]);
            if (r.Get("plugins") != null) r["plugins"] = Many(Py.L(r["plugins"]));
        }
        if (e.ContainsKey("baseline")) e["baseline"] = Many(e["baseline"] as List<object?>);
        if (Py.Truthy(e.Get("baselineScopeTarget"))) e["baselineScopeTarget"] = Of(e["baselineScopeTarget"]);
        if (Py.Truthy(e.Get("companions")))
        {
            var named = new PyDict();
            foreach (var kv in Py.D(e["companions"])) named[Of(kv.Key)!] = Many(kv.Value as List<object?>);
            e["companions"] = named;
        }
        foreach (var x in Py.ListOrEmpty(e.Get("extraArms")).Select(Py.D)) x["plugins"] = Many(x.Get("plugins") as List<object?>);
        foreach (var v in Py.ListOrEmpty(e.Get("orderVariants")).Select(Py.D))
            if (v.ContainsKey("last")) v["last"] = Many(v["last"] as List<object?>);
        if (Py.Truthy(e.Get("places")))
        {
            var named = new PyDict();
            foreach (var kv in Py.D(e["places"]))
            {
                var where = new PyDict(Py.D(kv.Value));
                where["after"] = Of(where.Get("after"));
                named[Of(kv.Key)!] = where;
            }
            e["places"] = named;
        }
        if (Py.Truthy(e.Get("modNames")))
        {
            var named = new PyDict();
            foreach (var kv in Py.D(e["modNames"])) named[Of(kv.Key)!] = kv.Value;
            e["modNames"] = named;
        }
        return e;
    }
}
