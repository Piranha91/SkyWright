using HousecarlCore;
using SkyWright.Engine;
using Mutagen.Bethesda.Plugins;

namespace ViewProbe;

/// <summary>Explicit annotation participants within the renderer's existing exterior ring.</summary>
internal sealed class NavmeshSelection
{
    internal sealed record OwnedGraph(NavmeshSelection Selection,
        Dictionary<FormKey, NavmeshGeometry> Winners,
        Dictionary<string, Dictionary<FormKey, NavmeshGeometry>> Versions);

    public Dictionary<FormKey, string?> Cells { get; } = new();
    public Dictionary<FormKey, string> Overrides { get; } = new();
    public Dictionary<NavmeshGeometry, (FormKey Cell, string Plugin)> Provenance { get; } = new(ReferenceEqualityComparer.Instance);
    private readonly Dictionary<FormKey, CellPlacements> observed = new();
    // The winners under the last-sighting rule (_shared/LastSighting.cs): a navmesh is a winner iff its last sighting stands
    // in a selected cell with its geometry; moved out of them or deleted, with or without its geometry, it is not.
    private readonly HarnessShared.LastSightingNavSet win = new();
    public FormKey Target { get; }

    internal OwnedGraph CopyGraph(IReadOnlyDictionary<FormKey, NavmeshGeometry> winners,
        IReadOnlyDictionary<string, Dictionary<FormKey, NavmeshGeometry>> versions)
    {
        var map = new Dictionary<NavmeshGeometry, NavmeshGeometry>(ReferenceEqualityComparer.Instance);
        NavmeshGeometry Mesh(NavmeshGeometry old)
        {
            if (map.TryGetValue(old, out var found)) return found;
            var copy = old with { Vertices = old.Vertices.ToArray(), Triangles = old.Triangles.ToArray(),
                EdgeLinks = old.EdgeLinks?.ToArray(), DoorTriangles = old.DoorTriangles?.ToArray() };
            map[old] = copy; return copy;
        }
        CellPlacements Cell(CellPlacements old) => old with
        {
            Refs = old.Refs.Select(r => r with { LinkedReferences = r.LinkedReferences?.ToArray(),
                ActivateParents = r.ActivateParents?.ToArray() }).ToArray(),
            Navmeshes = old.Navmeshes.Select(Mesh).ToArray(),
            Land = old.Land is null ? null : (float[,])old.Land.Clone(),
        };
        var selection = new NavmeshSelection(Target, Array.Empty<string>(), Array.Empty<string>());
        selection.Cells.Clear(); foreach (var x in Cells) selection.Cells[x.Key] = x.Value;
        foreach (var x in Overrides) selection.Overrides[x.Key] = x.Value;
        foreach (var x in observed) selection.observed[x.Key] = Cell(x.Value);
        var win = winners.ToDictionary(x => x.Key, x => Mesh(x.Value));
        var ver = versions.ToDictionary(x => x.Key,
            x => x.Value.ToDictionary(y => y.Key, y => Mesh(y.Value)), StringComparer.OrdinalIgnoreCase);
        foreach (var x in Provenance) selection.Provenance[Mesh(x.Key)] = x.Value;
        foreach (var k in this.win.TakenOut) selection.win.TakenOut.Add(k);   // Substitute reads it
        return new(selection, win, ver);
    }

    public NavmeshSelection(FormKey target, IEnumerable<string> cells, IEnumerable<string> versions)
    {
        Target = target;
        Cells.Add(target, null);
        foreach (var spec in cells)
        {
            var pair = spec.Split('=', 2);
            var key = ParseKey(pair[0], "--nav-cell");
            string? colour = pair.Length == 2 ? pair[1].ToLowerInvariant() : null;
            if (colour is not (null or "green" or "cyan" or "magenta" or "yellow" or "blue"))
                throw new ArgumentException("--nav-cell colour must be green, cyan, magenta, yellow or blue.");
            if (!Cells.TryAdd(key, colour)) throw new ArgumentException($"--nav-cell repeats target/participant {key}.");
        }
        if (Cells.Count > 9) throw new ArgumentException("--nav-cell permits at most eight additional cells.");
        foreach (var spec in versions)
        {
            var pair = spec.Split('=', 2);
            if (pair.Length != 2 || string.IsNullOrWhiteSpace(pair[1]))
                throw new ArgumentException("--nav-version requires NAVM_FORMKEY=Plugin.esp.");
            if (!Overrides.TryAdd(ParseKey(pair[0], "--nav-version"), pair[1]))
                throw new ArgumentException("--nav-version repeats a NAVM FormKey.");
        }
        if (Overrides.Count > 32) throw new ArgumentException("--nav-version permits at most 32 record substitutions.");
    }

    private static FormKey ParseKey(string value, string option)
    {
        try
        {
            var key = FormKey.Factory(value);
            if (key.IsNull || !value.Contains(':')) throw new FormatException();
            return key;
        }
        catch { throw new ArgumentException($"{option}: '{value}' is not a full FormKey (XXXXXX:Plugin.esp)."); }
    }

    /// <summary>One cell of one plugin, in load order: EVERY cell, not only the selected ones, since a later sighting
    /// elsewhere takes a navmesh out of the winners. <paramref name="winners"/> mirrors the rule's set.</summary>
    public void Observe(CellPlacements cp, string plugin, ISet<string> watched,
        Dictionary<FormKey, NavmeshGeometry> winners,
        Dictionary<string, Dictionary<FormKey, NavmeshGeometry>> versions)
    {
        bool selected = Cells.ContainsKey(cp.Cell);
        if (selected) observed[cp.Cell] = cp;
        foreach (var k in win.Observe(cp, (c, _) => Cells.ContainsKey(c.Cell)))
            if (win.Navs.TryGetValue(k, out var nav)) { winners[k] = nav; Provenance[nav] = (cp.Cell, plugin); }
            else winners.Remove(k);
        if (!selected || !watched.Contains(plugin)) return;
        foreach (var nav in cp.Navmeshes)
        {
            if (nav.Deleted) continue;   // a deletion is no version to substitute
            if (!versions.TryGetValue(plugin, out var own)) versions[plugin] = own = new();
            own[nav.FormKey] = nav;
        }
    }

    public void Validate(bool ringRefs, IReadOnlyDictionary<FormKey, NavmeshGeometry> winners)
    {
        if (Cells.Count == 1) return;
        if (!ringRefs) throw new ArgumentException("--nav-cell requires the surrounding geometry; remove --no-ring-refs.");
        if (!observed.TryGetValue(Target, out var target) || target.Interior || target.Grid is null || target.Worldspace.IsNull)
            throw new ArgumentException("--nav-cell requires an exterior target with a resolved worldspace/grid.");
        foreach (var cell in Cells.Keys.Where(k => k != Target))
        {
            if (!observed.TryGetValue(cell, out var cp)) throw new ArgumentException($"--nav-cell {cell} was not resolved in the evaluated order.");
            if (cp.Interior || cp.Worldspace != target.Worldspace || cp.Grid is not { } grid
                || Math.Abs(grid.X - target.Grid.Value.X) > 1 || Math.Abs(grid.Y - target.Grid.Value.Y) > 1
                || grid == target.Grid.Value)
                throw new ArgumentException($"--nav-cell {cell} is not a neighbouring exterior cell in the loaded ring.");
            if (!winners.Values.Any(n => Provenance[n].Cell == cell))
                throw new ArgumentException($"--nav-cell {cell} provides no winning navmesh records.");
        }
    }

    public Dictionary<FormKey, NavmeshGeometry> Substitute(IReadOnlyDictionary<FormKey, NavmeshGeometry> winners,
        IReadOnlyDictionary<string, Dictionary<FormKey, NavmeshGeometry>> versions)
    {
        var result = new Dictionary<FormKey, NavmeshGeometry>(winners);
        foreach (var (key, plugin) in Overrides)
        {
            // A navmesh that stood in a selected cell and that a later plugin moved out or deleted is still a record the
            // selection knows: an earlier plugin's version of it may be substituted ("as if that plugin won").
            if (!winners.ContainsKey(key) && !win.TakenOut.Contains(key))
                throw new ArgumentException($"--nav-version {key} is outside the selected navmesh cells or unresolved.");
            if (!versions.TryGetValue(plugin, out var own) || !own.TryGetValue(key, out var nav))
                throw new ArgumentException($"--nav-version: {plugin} provides no version of {key} in the selected cells.");
            result[key] = nav;
        }
        return result;
    }

    public static bool IsDrawable(NavmeshGeometry nav, int index) => index >= 0 && index < nav.Triangles.Count
        && (nav.Triangles[index].Flags & 0x08) == 0
        && nav.Triangles[index].A >= 0 && nav.Triangles[index].A < nav.Vertices.Count
        && nav.Triangles[index].B >= 0 && nav.Triangles[index].B < nav.Vertices.Count
        && nav.Triangles[index].C >= 0 && nav.Triangles[index].C < nav.Vertices.Count;

    // Exercises the same selection, record-version and triangle admission functions used by the renderer.
    public static int Smoke()
    {
        FormKey Key(uint id) => new(ModKey.FromNameAndExtension("Fixture.esm"), id);
        var a = Key(1); var b = Key(2); var c = Key(3); var ws = Key(4);
        var na = new NavmeshGeometry(Key(5), new[] { new Vec3(0,0,0), new Vec3(1,0,0), new Vec3(0,1,0) }, new[] { new NavTri(0,1,2,0) }, default, default);
        var nb = na with { FormKey = Key(6) }; var prior = nb with { Triangles = new[] { new NavTri(0,1,2,0), new NavTri(0,1,2,0) } };
        CellPlacements Cell(FormKey key, int x, NavmeshGeometry nav) => new(key, null, false, ws, (x,0), Array.Empty<PlacedRef>(), new[] { nav });
        int tests = 0;
        void Check(bool result, string label) { if (!result) throw new Exception(label); tests++; Console.WriteLine("PASS " + label); }
        void Refuses(Action call, string label) { try { call(); } catch (ArgumentException) { Check(true,label); return; } throw new Exception("Failed refusal: " + label); }
        var scope = new NavmeshSelection(a, new[] { b + "=magenta" }, new[] { nb.FormKey + "=Prior.esp" });
        var winners = new Dictionary<FormKey, NavmeshGeometry>();
        var versions = new Dictionary<string, Dictionary<FormKey, NavmeshGeometry>>(StringComparer.OrdinalIgnoreCase);
        var watched = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "Prior.esp" };
        scope.Observe(Cell(a,0,na), "Current.esp", watched, winners, versions);
        scope.Observe(Cell(b,1,prior), "Prior.esp", watched, winners, versions);
        scope.Observe(Cell(b,1,nb), "Current.esp", watched, winners, versions);
        scope.Observe(Cell(c,-1,nb with { FormKey = Key(7) }), "Current.esp", watched, winners, versions);
        scope.Validate(true,winners);
        Check(winners.Count == 2 && ReferenceEquals(winners[nb.FormKey],nb), "only target and explicit neighbour; last provider wins");
        var historic = scope.Substitute(winners,versions);
        Check(ReferenceEquals(historic[na.FormKey],na) && ReferenceEquals(historic[nb.FormKey],prior), "one record substituted; current source preserved");
        Check(scope.Provenance[prior].Cell == b && scope.Provenance[prior].Plugin == "Prior.esp", "actual source provenance retained");
        var owned = scope.CopyGraph(winners, versions);
        var copiedPrior = owned.Versions["Prior.esp"][nb.FormKey];
        Check(ReferenceEquals(copiedPrior, owned.Selection.Provenance.Keys.Single(n => n.FormKey == nb.FormKey
                  && owned.Selection.Provenance[n].Plugin == "Prior.esp")),
              "copied versions and provenance preserve reference identity");
        var sourceVerts = (Vec3[])prior.Vertices;
        var copiedFirst = copiedPrior.Vertices[0];
        sourceVerts[0] = new Vec3(99, 99, 99);
        Check(copiedPrior.Vertices[0] == copiedFirst,
              "copied navigation graph ignores source-array mutation");
        Check(!IsDrawable(nb,1) && IsDrawable(prior,1), "absent current triangle never fabricated from historical version");
        Check(!IsDrawable(nb with { Triangles = new[] { new NavTri(0,1,9,0) } },0), "invalid vertex refused");
        Check(!IsDrawable(nb with { Triangles = new[] { new NavTri(0,1,2,8) } },0), "deleted triangle refused");
        Refuses(() => scope.Validate(false,winners), "missing neighbour geometry refused");
        scope.Observe(Cell(b,3,nb), "Current.esp", watched,winners,versions);
        Refuses(() => scope.Validate(true,winners), "distant cell refused");
        Refuses(() => new NavmeshSelection(a,new[] { b + "=red" },Array.Empty<string>()), "reserved red refused");
        Refuses(() => new NavmeshSelection(a,new[] { a.ToString() },Array.Empty<string>()), "duplicate target refused");
        Refuses(() => new NavmeshSelection(a,Array.Empty<string>(),new[] { nb.FormKey + "=Missing.esp" }).Substitute(winners,versions), "unresolved version refused");
        var missing = new NavmeshSelection(a,new[] { c.ToString() },Array.Empty<string>());
        missing.Observe(Cell(a,0,na),"Current.esp",watched,winners,versions);
        Refuses(() => missing.Validate(true,winners), "unresolved cell refused");
        Console.WriteLine($"{tests} navmesh selection checks passed.");
        return 0;
    }
}
