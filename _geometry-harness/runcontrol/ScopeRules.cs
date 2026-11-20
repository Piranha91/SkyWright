using System.Text;
using HarnessShared;
using HousecarlCore;

namespace RunControl;

/// <summary>The scope stage's rules: which plugins an arm's scope targets, whether a scope is accepted against what
/// the arm prepared, the order reader's refusal read from a probe log, and the tab's scope line.</summary>
public static class ScopeRules
{
    // A row's value under a camelCase key, or under the same key with a capital first letter.
    static object? Named(PyDict row, string key) => row.ContainsKey(key) ? row.Get(key) : row.Get(char.ToUpperInvariant(key[0]) + key[1..]);

    // A cell of a placed footprint that is neither the persistent cell nor a grid key.
    static bool OwnCell(PyDict c) => c.Get("kind") as string != "persistent" && !PyText.Str(c["cell"]).StartsWith("grid:", StringComparison.Ordinal);

    /// <summary>The cells a target's footprint places into, sorted; each once unless `repeats` keeps the rows as they are.</summary>
    public static List<string> PlacedCells(PyDict placed, bool repeats = false)
    {
        var cells = Py.ListOrEmpty(placed.Get("cells")).Select(Py.D).Where(OwnCell).Select(c => Py.S(c["cell"]));
        return PyOrder.SortedByCodePoint(repeats ? cells : cells.Distinct());
    }

    /// <summary>Accepts one arm's scopeprobe output against what the arm prepared, or refuses with the reason.</summary>
    public static PyDict Accept(PyDict scope, PyDict expect)
    {
        if (scope.Get("complete") is not true || Py.Truthy(scope.Get("walkFailures")) || Py.Truthy(scope.Get("unreadableRecords")))
            throw new Refusal("the scope is incomplete (complete flag, walk failures or unreadable records); "
                              + "read its log and rerun the scope after fixing the cause.");
        var cfg = Py.DictOrEmpty(scope.Get("configuration"));
        var rows = Py.ListOrEmpty(cfg.Get("plugins")).Select(Py.D).ToList();
        var want = Py.L(expect["plugins"]).Select(Py.D).ToList();
        if (!Py.Eq(rows.Select(r => Named(r, "name")).ToList(), want.Select(w => w["name"]).ToList()))
            throw new Refusal("the scope's plugin order differs from the arm's prepared order; the scope read another view.");
        foreach (var (r, w) in rows.Zip(want))
            if (!PyOrder.SamePath(Named(r, "path") as string ?? "", Py.S(w["path"])) || !Py.Eq(Named(r, "sha256"), w["sha256"]))
                throw new Refusal($"{PyText.Str(w["name"])}: the scope read other bytes or another provider than the prepared copy.");
        var mods = Py.ListOrEmpty(cfg.Get("providers")).Select(Py.D)
            .Where(r => PyText.Lower(PyText.Str(Named(r, "kind"))) == "mod").Select(r => Named(r, "name")).ToList();
        if (!Py.Eq(mods, Py.L(expect["mods"])) || !Py.Truthy(cfg.Get("fingerprint")))
            throw new Refusal("the scope's asset provider view differs from the arm's prepared view.");
        var targets = Py.ListOrEmpty(scope.Get("targets")).Select(Py.D).ToList();
        var got = PyOrder.SortedByCodePoint(targets.Select(t => Py.S(t.Get("name"))));
        var asked = PyOrder.SortedByCodePoint(Py.Strs(expect["targets"]));
        if (!got.SequenceEqual(asked))
            throw new Refusal($"the scope's target population {PyText.Repr(Py.Objs(got))} differs from the "
                              + $"requested target {PyText.Repr(Py.Objs(asked))}.");
        var shas = new PyDict();
        foreach (var w in want) shas[Py.S(w["name"])] = w["sha256"];
        var accepted = new PyDict();
        foreach (var t in targets)
        {
            string name = Py.S(t["name"]);
            if (shas.ContainsKey(name) && !Py.Eq(t.Get("sha256"), shas[name]))
                throw new Refusal($"{name}: the scope's target bytes differ from the prepared copy.");
            var cells = PlacedCells(Py.DictOrEmpty(Py.DictOrEmpty(t.Get("footprint")).Get("placed")));
            var census = PyOrder.SortedByCodePoint(Py.Strs(Py.DictOrEmpty(expect.Get("censusCells")).Get(name)).Distinct());
            var missing = census.Where(c => !cells.Contains(c)).ToList();
            if (missing.Count > 0)
                throw new Refusal($"{name}: the scope misses census cells {string.Join(", ", missing)}; reconcile the census and "
                                  + "the arm's view before evaluating.");
            accepted[name] = new PyDict
            {
                ["measuredCells"] = Py.Objs(cells), ["censusCells"] = Py.Objs(census), ["missingCensusCells"] = Py.Objs(missing),
                ["additionalCells"] = Py.Objs(census.Count > 0 ? cells.Where(c => !census.Contains(c)) : []),
            };
        }
        return new PyDict { ["targets"] = accepted };
    }

    /// <summary>The plugin an arm is scoped to when it loads none of the selection's: its last one, only to prove the order loads.</summary>
    public static string? OrderProof(IReadOnlyList<string> plugins) => plugins.Count > 0 ? plugins[^1] : null;

    // The reach's numbers, summed over its folders (the delta's), with the stronger folders named.
    sealed record Reach(long Refs, long Credited, long Equal, long Fresh, long NotLoaded, long Over, SortedDictionary<string, long> Stronger, long Meshes,
                        long Textures, long Held, long Loaded, bool Complete, string Why, PyDict? Example);

    static long N(PyDict d, string k) => Py.Truthy(d.Get(k)) ? (long)PyValue.Int(d[k]) : 0;

    static Reach Read(PyDict reach)
    {
        var folders = Py.ListOrEmpty(reach.Get("folders")).Select(Py.D).ToList();
        long Sum(string k) => folders.Sum(f => N(f, k));
        var stronger = new SortedDictionary<string, long>(StringComparer.OrdinalIgnoreCase);
        foreach (var f in folders)
            foreach (var (k, v) in Py.DictOrEmpty(f.Get("overriddenBy"))) stronger[k] = stronger.GetValueOrDefault(k) + (long)PyValue.Int(v);
        long held = folders.Sum(f => (long)Py.ListOrEmpty(Py.DictOrEmpty(f.Get("archives")).Get("held")).Count);
        long loaded = folders.Sum(f => (long)Py.ListOrEmpty(Py.DictOrEmpty(f.Get("archives")).Get("loaded")).Count);
        var example = folders.SelectMany(f => Py.ListOrEmpty(f.Get("examples"))).Select(Py.D).FirstOrDefault();
        string why = string.Join("; ", Py.ListOrEmpty(reach.Get("why")).Select(PyText.Str));
        return new Reach(Sum("references"), Sum("credited"), Sum("equalBytes"), Sum("noFallback"), Sum("notLoaded"),
                         stronger.Values.Sum(), stronger, Sum("meshFiles"), Sum("textureFiles"), held, loaded,
                         reach.Get("complete") is true, why.Length > 0 ? why : "no reason given", example);
    }

    static string Count(long n, string one) => $"{n} {one}{(n != 1 ? "s" : "")}";
    static string Use(long n) => n == 1 ? "uses" : "use";

    // Of the placed objects that use one of the folders' meshes, why none gets a changed one.
    static string Split(Reach r)
    {
        var parts = new List<string>();
        if (r.Equal > 0) parts.Add($"{r.Equal} {(r.Equal == 1 ? "gets" : "get")} the same bytes {(r.Equal == 1 ? "it" : "they")} had without it");
        if (r.Over > 0)
            parts.Add($"{r.Over} {(r.Over == 1 ? "gets" : "get")} the copy of a stronger folder ("
                      + (r.Stronger.Count == 1 ? r.Stronger.Keys.First() : string.Join(", ", r.Stronger.Select(kv => $"{kv.Key} {kv.Value}"))) + ")");
        if (r.Fresh > 0) parts.Add($"{r.Fresh} {Use(r.Fresh)} a mesh only it has");
        if (r.NotLoaded > 0) parts.Add($"{r.NotLoaded} would read it from an archive the game does not load");
        return string.Join(", ", parts);
    }

    // The archive sentence (3466b0a3): the game loads a folder's archive only beside a plugin of the same name or from the INI's list.
    static string Unloaded(Reach r) => r.Held > r.Loaded
        ? $" The game does not load {(r.Loaded == 0 ? (r.Held == 1 ? "its archive" : $"its {r.Held} archives") : $"{r.Held - r.Loaded} of its {r.Held} archives")}: "
          + "an archive loads only beside a loaded plugin of the same name or from the INI's archive list, so what it holds is not measured."
        : "";

    const string WhyUnknown = " With no plugin to read, the two checks cannot be ruled out, so they show as not complete; that is not a fault in the mod.";

    /// <summary>The tab's plain sentence for a selection with no plugin, from the mesh reach (scope/mesh-reach.json): how many
    /// placed objects use a mesh it provides that wins and differs, why none does, and what is not measured.</summary>
    public static string AssetOnlyLine(PyDict reach, PyDict res)
    {
        var ticked = Py.Strs(res.Get("selection"));
        string who = ticked.Count > 0 ? NamesOf(ticked, 3) : "the selection";
        var r = Read(reach);
        string notMeasured = $"Worn and actor meshes, its textures ({Count(r.Textures, "file")}) and other files are not measured.";
        if (r.Credited > 0)
        {
            string eg = r.Example is { } e ? $" (for example {PyPath.Name(PyText.Str(e.Get("model")))} in {PyText.Str(e.Get("cell"))})" : "";
            return $"{who} has no plugin, so it places nothing; {Count(r.Credited, "placed object")} {Use(r.Credited)} a mesh it provides that wins and "
                   + $"differs from the one it replaces{eg}. Object overlap and placement contact compare those with and without it. "
                   + notMeasured + Unloaded(r);
        }
        if (!r.Complete)
            return $"{who} has no plugin; whether a placed object uses a mesh it changes is not known: {r.Why}." + Unloaded(r);
        if (r.Meshes == 0 && r.Loaded == 0)
            return $"{who} has no plugin and no mesh{(r.Held > 0 ? " the game loads" : "")} ({Count(r.Textures, "texture file")}{(r.Held > 0 ? $", {Count(r.Held, "archive")}" : "")}), "
                   + "so no check here can measure it: textures, sounds and scripts change nothing these checks read." + WhyUnknown + Unloaded(r);
        if (r.Refs == 0)
            return $"{who} has no plugin, and no placed object uses any of its {Count(r.Meshes, "mesh file")}, so object overlap and placement "
                   + $"contact have nothing of it to compare. Worn and actor meshes (body and armour replacers) are not measured, nor are its "
                   + $"textures ({Count(r.Textures, "file")})." + WhyUnknown + Unloaded(r);
        return $"{who} has no plugin, and no placed object uses a mesh it changes: of the {Count(r.Refs, "placed object")} that {Use(r.Refs)} one of "
               + $"its meshes, {Split(r)}. " + notMeasured + WhyUnknown + Unloaded(r);
    }

    /// <summary>The mesh sentence after a plugin selection's scope line, when the arms differ by a mod folder that holds a mesh;
    /// null otherwise.</summary>
    public static string? MeshClause(PyDict reach)
    {
        if (Py.ListOrEmpty(reach.Get("folders")).Count == 0) return null;
        var r = Read(reach);
        if (r.Meshes == 0 && r.Loaded == 0 && r.Refs == 0) return null;   // a plugin mod that ships no mesh: nothing to say about meshes
        if (r.Credited > 0) return $" Its meshes: {Count(r.Credited, "placed object")} {Use(r.Credited)} one that wins and differs from the one it replaces.";
        if (!r.Complete) return $" Whether a placed object uses a mesh it changes is not known: {r.Why}.";
        if (r.Refs == 0) return " No placed object uses any of its meshes.";
        return $" No placed object uses a mesh it changes: of the {Count(r.Refs, "placed object")} that {Use(r.Refs)} one of its meshes, {Split(r)}.";
    }

    /// <summary>The plugins one arm's scope targets.</summary>
    public static List<string> Targets(RunContext ctx, PyDict res, PyDict arm)
    {
        var plugins = Py.Strs(arm["plugins"]);
        var subject = Py.Strs(res["subject"]);
        if (ctx.InPlace != null)
        {
            // the selection's plugins this arm loads; an arm that loads none scopes its last plugin only, to prove the order loads
            var mine = subject.Where(plugins.Contains).ToList();
            return mine.Count > 0 ? mine : OrderProof(plugins) is { } last ? [last] : [];
        }
        List<string> targets;
        if (plugins.Contains(subject[0])) targets = [subject[0]];
        else
        {
            targets = Py.ListOrEmpty(ctx.Entry.Get("partners")).Select(p => Py.D(p).Get("plugin") as string)
                .Where(p => p != null && plugins.Contains(p)).Select(p => p!).ToList();
            if (targets.Count == 0)
                targets = [Py.Truthy(ctx.Entry.Get("baselineScopeTarget")) ? Py.S(ctx.Entry.Get("baselineScopeTarget")) : Py.Strs(res["baseline"])[^1]];
        }
        return targets.Concat(Py.Strs(res["keys"]).Where(plugins.Contains)).ToList();
    }

    /// <summary>The words by which the probes' order reader refuses an order (HarnessOrderCli and OrderMasters write them).</summary>
    public static readonly string[] OrderRefusalMarkers = ["MISSING MASTER", "CANNOT ENABLE", "PROFILE MISMATCH"];

    /// <summary>The order reader's own refusal from a probe's job logs, as one line, or null.</summary>
    public static string? OrderRefusal(string logs)
    {
        if (!Directory.Exists(logs)) return null;
        foreach (string log in PyOrder.SortedPaths(Directory.GetFiles(logs, "*.txt")))
        {
            List<string> lines;
            try { lines = PyText.SplitLines(Encoding.UTF8.GetString(File.ReadAllBytes(log))); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { continue; }
            for (int i = 0; i < lines.Count; i++)
            {
                if (!OrderRefusalMarkers.Any(lines[i].Contains)) continue;
                var more = lines.Skip(i + 1).Take(3).Where(x => x.Trim().StartsWith('-') || x.Trim().StartsWith('\'') || PyText.Lower(x).Contains("master"))
                    .Select(x => x.Trim());
                return string.Join(" ", new[] { string.Join(" ", PyText.Split(lines[i])) }.Concat(more)).TrimEnd('.') + ".";
            }
        }
        return null;
    }

    /// <summary>{plugin file name, lower case: mod folder} for the plugins of the profile's disabled mod folders nobody ticked.</summary>
    public static PyDict DisabledModOf(RunContext ctx, PyDict res)
    {
        var disabled = Mo2LoadOrder.ReadComposition(PyPath.Join(ctx.Instance, "profiles", ctx.Profile)).DisabledMods;
        var sel = Py.Strs(res.Get("selection")).Select(PyText.Lower).ToHashSet(StringComparer.Ordinal);
        var found = new PyDict();
        foreach (string m in disabled)
        {
            if (sel.Contains(PyText.Lower(m))) continue;
            string folder = PyPath.Join(ctx.Instance, "mods", m);
            if (!Directory.Exists(folder)) continue;
            foreach (string name in PyOrder.SortedByLowerKey(Directory.GetFiles(folder).Where(PluginFiles.IsPlugin).Select(PyPath.Name)))
                if (!found.ContainsKey(PyText.Lower(name))) found[PyText.Lower(name)] = m;
        }
        return found;
    }

    /// <summary>The plugin files of the profile's disabled, unticked mod folders (scopeprobe reads them for CELL records only).</summary>
    public static List<string> DisabledPluginFiles(RunContext ctx, PyDict res)
    {
        var files = new List<string>();
        foreach (var (low, mod) in DisabledModOf(ctx, res))
            files.AddRange(Directory.GetFiles(PyPath.Join(ctx.Instance, "mods", Py.S(mod))).Where(f => PyText.Lower(PyPath.Name(f)) == low));
        return files;
    }

    // The base game's own plugins and Creation Club content, by the probes' two tests.
    static bool BaseGame(string name) => BaseGamePlugins.IsBaseGame(name) || BaseGamePlugins.IsCreationClub(name);

    static string NamesOf(IEnumerable<string> items, int most = 6)
    {
        var all = items.ToList();
        return string.Join(", ", all.Take(most)) + (all.Count > most ? $" and {all.Count - most} more" : "");
    }

    /// <summary>The tab's plain sentence on what the ticked mods touch, from the combined arm's scope: the cells their
    /// plugins edit, the other enabled plugins with records in those cells or within the ring around them, and the
    /// disabled, unticked mods whose plugins also edit them.</summary>
    public static string Line(PyDict scope, PyDict res, PyDict disabledMods)
    {
        var ticked = Py.Strs(res.Get("selection"));
        var targets = Py.ListOrEmpty(scope.Get("targets")).Select(Py.D).ToList();
        var mine = targets.Select(t => PyText.Lower(PyText.Str(t.Get("name")))).ToHashSet(StringComparer.Ordinal);
        var cells = targets.SelectMany(t => Py.ListOrEmpty(Py.DictOrEmpty(Py.DictOrEmpty(t.Get("footprint")).Get("placed")).Get("cells"))).Select(Py.D);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var ext = new List<(string worldspace, List<string?> names)>();
        var interiors = new List<string>();
        foreach (var c in cells)
        {
            string cell = PyText.Repr(c.Get("cell"));
            if (c.Get("kind") as string == "persistent" || !seen.Add(cell)) continue;
            if (c.Get("kind") as string == "interior")
                interiors.Add(Py.Truthy(c.Get("name")) ? Py.S(c["name"]) : Py.S(c.Get("cell")));
            else
            {
                string ws = Py.Truthy(c.Get("worldspace")) ? Py.S(c["worldspace"]) : Py.Truthy(c.Get("worldspaceKey")) ? Py.S(c["worldspaceKey"]) : "a worldspace";
                int at = ext.FindIndex(x => x.worldspace == ws);
                if (at < 0) { ext.Add((ws, [])); at = ext.Count - 1; }
                ext[at].names.Add(c.Get("name") as string);
            }
        }
        string who = ticked.Count > 0 ? NamesOf(ticked, 3) : "the selection";
        var parts = new List<string>();
        foreach (var (ws, names) in ext)
        {
            var named = names.Where(n => !string.IsNullOrEmpty(n)).Select(n => n!).ToList();
            parts.Add($"{names.Count} cell{(names.Count != 1 ? "s" : "")} in {ws}" + (named.Count > 0 ? $" ({NamesOf(named, 3)})" : ""));
        }
        if (interiors.Count > 0) parts.Add($"{interiors.Count} interior{(interiors.Count != 1 ? "s" : "")} ({NamesOf(interiors, 3)})");
        if (parts.Count == 0) return $"{who} places nothing in any cell, so no other plugin shares its cells.";
        var contributors = Py.DictOrEmpty(scope.Get("locationContributors"));
        var nearby = Py.DictOrEmpty(scope.Get("nearby"));
        var inside = Py.ListOrEmpty(contributors.Get("locations")).SelectMany(loc => Py.ListOrEmpty(Py.D(loc).Get("plugins"))).Select(p => Py.S(Py.D(p)["name"]));
        var near = Py.ListOrEmpty(nearby.Get("plugins")).Select(p => Py.S(Py.D(p)["name"]));
        var others = PyOrder.SortedByLowerKey(PyOrder.SortedByCodePoint(inside.Concat(near).Distinct().Where(n => !mine.Contains(PyText.Lower(n)) && !BaseGame(n))));
        object ring = Py.Truthy(nearby.Get("ring")) ? nearby["ring"]! : 0L;
        string joined = string.Join(" and ", parts);
        if (contributors.Get("complete") is false)
        {
            // never "no other plugin" from a walk that did not finish: say what could not be read
            var missing = Py.ListOrEmpty(contributors.Get("missing"));
            string first = missing.Count > 0 ? PyText.Str(missing[0]) : "";
            return $"{who} edits " + joined + "; which other plugins touch those cells is not known: "
                   + (missing.Count > 0 ? $"{missing.Count} plugin(s) could not be read for it ({(first.Length > 160 ? first[..160] : first)})"
                       : "the probe's footprint walk was incomplete") + ".";
        }
        string line = $"{who} edits " + joined + "; ";
        line += others.Count > 0
            ? $"{others.Count} other enabled plugin{(others.Count != 1 ? "s touch" : " touches")} those cells"
              + (Py.Truthy(ring) ? $" or the {PyText.Str(ring)} cells around them" : "") + $": {NamesOf(others, 6)}."
            : "no other enabled plugin besides the base game touches those cells"
              + (Py.Truthy(ring) ? $" or the {PyText.Str(ring)} cells around them (persistent references placed into those cells are not read)" : "") + ".";
        var alsoScanned = Py.ListOrEmpty(nearby.Get("alsoScanned")).Select(Py.D).ToList();
        var hintMods = new List<string>();
        foreach (var x in alsoScanned)
            if (Py.Truthy(x.Get("footprintCells")) || Py.Truthy(x.Get("ringCells")))
                if (disabledMods.Get(PyText.Lower(PyText.Str(x.Get("name")))) is string m && m.Length > 0 && !hintMods.Contains(m)) hintMods.Add(m);
        foreach (string m in hintMods.Take(3)) line += $" {m} (disabled in MO2) also edits these cells; tick it to test them together.";
        if (hintMods.Count > 3) line += $" {hintMods.Count - 3} more disabled mods also edit them.";
        var unread = alsoScanned.Where(x => Py.Truthy(x.Get("unreadable"))).ToList();
        if (unread.Count > 0)
            line += $" {unread.Count} disabled plugin{(unread.Count != 1 ? "s" : "")} could not be read for this "
                    + $"({NamesOf(unread.Select(x => PyText.Str(x.Get("name"))), 3)}), so whether they edit these cells is not known.";
        return line;
    }
}
