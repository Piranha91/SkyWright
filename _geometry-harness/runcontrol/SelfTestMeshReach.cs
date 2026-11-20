namespace RunControl;

// The mesh reach's readers (fold-review-fix/GATE-A.md sections 2 and 3): the registry's placedMeshes trigger, the scope line's
// incomplete and plugin-selection sentences, and the plan's check that the reach is about the folders the probes credit.
public static partial class SelfTest
{
    static PyDict ReachDoc(bool complete, long credited, params PyDict[] folders) => new()
    {
        ["schema"] = 1L, ["complete"] = complete, ["why"] = complete ? new List<object?>() : new List<object?> { "1 plugin(s) could not be read (X.esp)" },
        ["folders"] = folders.Cast<object?>().ToList(), ["references"] = folders.Sum(f => (long)PyValue.Int(f.Get("references") ?? 0L)), ["credited"] = credited,
    };

    static PyDict ReachFolder(string name, long refs = 0, long credited = 0, long equal = 0, long fresh = 0, long notLoaded = 0, PyDict? over = null) => new()
    {
        ["name"] = name, ["meshFiles"] = 1L, ["textureFiles"] = 0L, ["archives"] = new PyDict { ["held"] = new List<object?>(), ["loaded"] = new List<object?>() },
        ["references"] = refs, ["credited"] = credited, ["equalBytes"] = equal, ["overriddenBy"] = over ?? new PyDict(), ["noFallback"] = fresh,
        ["unreadable"] = 0L, ["notLoaded"] = notLoaded, ["examples"] = new List<object?>(),
    };

    static void MeshReachCases()
    {
        // overlap's trigger as the registry carries it, alone in a registry
        var registry = new PyDict
        {
            ["checks"] = new List<object?>
            {
                new PyDict
                {
                    ["id"] = "geometry.object-overlap",
                    ["applicability"] = new PyDict
                    {
                        ["trigger"] = new PyDict { ["anyOf"] = new List<object?> { new PyDict { ["placed"] = true }, new PyDict { ["placedMeshes"] = true } } },
                        ["note"] = "n",
                    },
                },
            },
        };
        var placesNothing = new PyDict
        {
            ["complete"] = true,
            ["targets"] = new List<object?> { new PyDict { ["name"] = "M.esp", ["footprint"] = new PyDict { ["placed"] = new PyDict { ["total"] = 0L } } } },
        };
        string Answer(PyDict? scope, PyDict? reach)
        {
            var row = D(Registry.Applicability(registry, scope, reach)["geometry.object-overlap"]);
            return $"{S(row["applicability"])}: {S(row["evidence"])}";
        }
        var rows = new (string name, PyDict? scope, PyDict? reach, string want)[]
        {
            ("no reach and no scope", null, null,
             "unknown: no scope.json was measured, so this trigger could not be read."),
            ("a plugin that places nothing, no reach", placesNothing, null,
             "unknown: no mesh reach was measured, so whether a selected folder's mesh is used by a placed object is not known."),
            ("no scope, a credited mesh", null, ReachDoc(true, 3, ReachFolder("M", refs: 4, credited: 3, equal: 1)),
             "yes: 3 placed reference(s) use a mesh M provide(s) that wins and differs from the one it replaces."),
            ("a plugin that places nothing, a credited mesh in an incomplete reach", placesNothing, ReachDoc(false, 1, ReachFolder("M", refs: 1, credited: 1)),
             "yes: 1 placed reference(s) use a mesh M provide(s) that wins and differs from the one it replaces."),
            ("a plugin that places nothing, nothing credited, a complete reach", placesNothing,
             ReachDoc(true, 0, ReachFolder("M", refs: 5, equal: 1, fresh: 1, notLoaded: 1, over: new PyDict { ["S"] = 2L })),
             "no: placed: 0 records filed under any cell.; no placed reference uses a mesh M provide(s) that wins and differs: 5 use one of its meshes "
             + "(1 equal to the copy they replace, 2 overridden by S, 1 new with nothing below, 1 in an archive the game does not load)."),
            ("a plugin that places nothing, nothing credited, an incomplete reach", placesNothing, ReachDoc(false, 0, ReachFolder("M")),
             "unknown: the mesh reach is incomplete (1 plugin(s) could not be read (X.esp)), so whether a selected folder's mesh is used by a placed object is not known."),
            ("no scope, nothing credited, a complete reach", null, ReachDoc(true, 0, ReachFolder("M")),
             "unknown: no scope.json was measured, so this trigger could not be read."),
            ("a plugin that places nothing, the arms differ by no folder", placesNothing, ReachDoc(true, 0),
             "no: placed: 0 records filed under any cell.; the arms differ by no mod folder, so no mesh is credited to one."),
        };
        foreach (var (name, scope, reach, want) in rows)
            Case($"mesh reach: placedMeshes, {name}", () => Same("applicability", Answer(scope, reach), want));

        Case("mesh reach: the registry accepts placedMeshes and refuses it with any value but true", () =>
        {
            var bad = new PyDict(registry);
            var said = Registry.Validate(registry);
            if (said.Any(p => p.Contains("placedMeshes"))) return "refused a valid placedMeshes: " + string.Join("; ", said);
            var c = new PyDict(D(L(registry["checks"])[0])) { ["applicability"] = new PyDict { ["trigger"] = new PyDict { ["placedMeshes"] = false }, ["note"] = "n" } };
            bad["checks"] = new List<object?> { c };
            return Registry.Validate(bad).Any(p => p.Contains("placedMeshes must be true")) ? null : "placedMeshes: false was not refused";
        });

        // the scope line's two sentences the stage cases do not reach
        var res = new PyDict { ["selection"] = Py.Objs(["Tex"]) };
        Case("mesh reach: an incomplete reach with nothing credited says what is not known", () => Same("line",
            ScopeRules.AssetOnlyLine(ReachDoc(false, 0, ReachFolder("Tex", refs: 1)), res),
            "Tex has no plugin; whether a placed object uses a mesh it changes is not known: 1 plugin(s) could not be read (X.esp)."));
        Case("mesh reach: a plugin selection's mesh clause, credited, changing nothing, none used, unknown, and no folder", () =>
            Same("credited", ScopeRules.MeshClause(ReachDoc(true, 2, ReachFolder("M", refs: 2, credited: 2))),
                 " Its meshes: 2 placed objects use one that wins and differs from the one it replaces.")
            ?? Same("nothing changed", ScopeRules.MeshClause(ReachDoc(true, 0, ReachFolder("M", refs: 1, equal: 1))),
                 " No placed object uses a mesh it changes: of the 1 placed object that uses one of its meshes, 1 gets the same bytes it had without it.")
            ?? Same("none used", ScopeRules.MeshClause(ReachDoc(true, 0, ReachFolder("M"))), " No placed object uses any of its meshes.")
            ?? Same("unknown", ScopeRules.MeshClause(ReachDoc(false, 0, ReachFolder("M"))),
                 " Whether a placed object uses a mesh it changes is not known: 1 plugin(s) could not be read (X.esp).")
            ?? (ScopeRules.MeshClause(ReachDoc(true, 0)) is null ? null : "a clause for arms that differ by no folder")
            ?? (ScopeRules.MeshClause(ReachDoc(true, 0, new PyDict(ReachFolder("M")) { ["meshFiles"] = 0L })) is null ? null : "a clause for a folder with no mesh"));

        // the plan refuses a reach about other folders than it credits, when a mesh probe is planned
        var planned = new List<PyDict>
        {
            new() { ["adapter"] = new PyDict { ["exe"] = "overlapprobe" }, ["arms"] = new List<object?> { new PyDict { ["status"] = "planned" } } },
        };
        string? Refuses(Action act) { try { act(); return null; } catch (Refusal r) { return r.Message; } }
        Case("mesh reach: the plan refuses a reach about other folders than its population credits, naming both", () =>
        {
            var scoped = new PyDict { ["scoped"] = true, ["mods"] = Py.Objs(["A", "b"]) };
            var unscoped = new PyDict { ["scoped"] = false, ["meshFolders"] = Py.Objs(["A"]) };
            string? same = Refuses(() => Plan.CheckReachFolders(ReachDoc(true, 0, ReachFolder("B"), ReachFolder("a")), scoped, planned));
            if (same != null) return "refused the same folders in another case and order: " + same;
            if (Refuses(() => Plan.CheckReachFolders(ReachDoc(true, 0, ReachFolder("A")), unscoped, planned)) is { } u) return "refused the unscoped plan's own folders: " + u;
            string? other = Refuses(() => Plan.CheckReachFolders(ReachDoc(true, 0, ReachFolder("A")), scoped, planned));
            if (other == null || !other.Contains("[A]") || !other.Contains("[A, b]")) return $"a reach about [A] against a population of [A, b]: {other ?? "not refused"}";
            var notPlanned = new List<PyDict> { new() { ["adapter"] = new PyDict { ["exe"] = "overlapprobe" }, ["arms"] = new List<object?> { new PyDict { ["status"] = "not-applicable" } } } };
            return Refuses(() => Plan.CheckReachFolders(ReachDoc(true, 0, ReachFolder("Z")), scoped, notPlanned)) is { } n
                ? "refused with no mesh probe planned: " + n : null;
        });
    }
}
