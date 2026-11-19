namespace RunControl;

/// <summary>Which switches the runtime state-switch check is about, on one arm (switchflip_scope.py). A switch is in
/// the population when a script drives it and it touches a target, by plugin or by cell. The answer is
/// not-applicable only when nothing leaves it unknown and the population is empty; each cause that keeps it open is named.</summary>
public static class SwitchFlipScope
{
    static readonly object?[] ScriptDriven = ["Triggered", "BoundUnread"];

    static PyDict DictOrEmpty(object? v) => PyValue.Truthy(v) ? PyValue.Dict(v) : [];
    static IEnumerable<object?> Items(object? v) => PyValue.Truthy(v) ? PyValue.Iter(v) : [];

    // A switch's on-set and off-set members.
    static IEnumerable<PyDict> Members(PyDict s) =>
        new[] { "onSet", "offSet" }.SelectMany(side => Items(s.Get(side))).OfType<PyDict>();

    // Target plugin names in lower case, the cells targets place in, and the targets whose placed cells are not listed.
    static (HashSet<string> names, HashSet<string> cells, List<string> unlisted) Targets(PyDict scope)
    {
        var names = new HashSet<string>(StringComparer.Ordinal);
        var cells = new HashSet<string>(StringComparer.Ordinal);
        var unlisted = new List<string>();
        foreach (object? row in Items(scope.Get("targets")))
        {
            if (row is not PyDict t || !PyValue.Truthy(t.Get("name"))) continue;
            names.Add(PyText.Lower(PyText.Str(t["name"])));
            var placed = DictOrEmpty(DictOrEmpty(t.Get("footprint")).Get("placed"));
            if (placed.Get("cells") is not List<object?> rows) { unlisted.Add(PyText.Str(t["name"])); continue; }
            foreach (var c in rows.OfType<PyDict>())
                if (PyValue.Truthy(c.Get("cell"))) cells.Add(PyText.Str(c["cell"]));
        }
        return (names, cells, unlisted);
    }

    /// <summary>"plugin", "cell" or null: how the switch touches a target.</summary>
    public static string? Touches(PyDict s, IReadOnlySet<string> names, IReadOnlySet<string> cells)
    {
        var plugins = Members(s).SelectMany(m => new[] { m.Get("placer"), m.Get("attacher") }).Prepend(s.Get("owner"));
        if (plugins.Any(p => PyValue.Truthy(p) && names.Contains(PyText.Lower(PyText.Str(p))))) return "plugin";
        var keys = Members(s).Select(m => m.Get("cellKey")).Prepend(s.Get("cellKey"));
        return keys.Any(k => PyValue.Truthy(k) && cells.Contains(PyText.Str(k))) ? "cell" : null;
    }

    /// <summary>{status: not-applicable | open, causes: [{code, detail}], population: [{key, edid, trigger, by}], counts}.</summary>
    public static PyDict Population(PyDict census, PyDict scope)
    {
        var causes = new List<object?>();
        void Cause(string code, string detail) => causes.Add(new PyDict { ["code"] = code, ["detail"] = detail });

        if (DictOrEmpty(census.Get("summary")).Get("triggers") is not PyDict)
            Cause("no-trigger-map", "the census ran without the trigger map, so which switches a script drives is unknown");
        var unreadable = Items(census.Get("unreadable")).ToList();
        if (unreadable.Count > 0)
            Cause("unreadable-plugins",
                $"the census could not read {unreadable.Count} plugin(s) ({string.Join(", ", unreadable.Take(5).Select(PyText.Str))}), "
                + "so their switches are absent, not zero");
        if (scope.Get("complete") is not true)
            Cause("scope-incomplete", "the scope is not complete, so the targets are not known");
        var (names, cells, unlisted) = Targets(scope);
        if (names.Count == 0) Cause("no-targets", "the scope names no target plugin");

        static object? Status(PyDict s) => DictOrEmpty(s.Get("trigger")).Get("status");
        var switches = Items(census.Get("switches")).OfType<PyDict>().ToList();
        var driven = switches.Where(s => PyValue.In(Status(s), ScriptDriven)).ToList();
        var population = new List<object?>();
        int undecided = 0;
        foreach (var s in driven)
        {
            string? by = Touches(s, names, cells);
            if (by != null)
                population.Add(new PyDict { ["key"] = s.Get("key"), ["edid"] = s.Get("edid"), ["trigger"] = Status(s), ["by"] = by });
            else if (unlisted.Count > 0) undecided++;
        }
        if (undecided > 0)
            Cause("cells-unknown",
                $"{undecided} script-driven switch(es) touch no target plugin, and the scope lists no "
                + $"placed cells for {string.Join(", ", unlisted)}, so whether they lie in a target's cells is unknown");

        var counts = new PyDict
        {
            ["switches"] = (long)switches.Count,
            ["scriptDriven"] = (long)driven.Count,
            ["triggered"] = (long)driven.Count(s => PyValue.Eq(Status(s), "Triggered")),
            ["boundUnread"] = (long)driven.Count(s => PyValue.Eq(Status(s), "BoundUnread")),
            ["population"] = (long)population.Count,
            ["undecided"] = (long)undecided,
        };
        return new PyDict
        {
            ["status"] = causes.Count == 0 && population.Count == 0 ? "not-applicable" : "open",
            ["causes"] = causes, ["population"] = population, ["counts"] = counts,
        };
    }

    /// <summary>One sentence for the ledger: why the arm is not applicable, or what keeps it open.</summary>
    public static string Sentence(PyDict result)
    {
        var c = (PyDict)result["counts"]!;
        string N(string key) => PyText.Str(c[key]);
        if (PyValue.Eq(result["status"], "not-applicable"))
            return $"the census on this arm lists {N("switches")} switch(es); none that a quest or script drives touches a "
                   + $"target by plugin or by cell ({N("triggered")} triggered and {N("boundUnread")} bound-but-unread "
                   + "elsewhere), so there is nothing to flip";
        if (PyValue.Truthy(result["population"]))
            return $"{N("population")} script-driven switch(es) touch a target on this arm "
                   + $"({N("triggered")} triggered, {N("boundUnread")} bound-but-unread in the whole census)";
        return string.Join("; ", PyValue.Iter(result["causes"]).Select(x => (string)PyValue.Dict(x)["detail"]!));
    }
}
