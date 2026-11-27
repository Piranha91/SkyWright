namespace RunControl;

// Known answers for the lighting check's stage (environment.lighting, Gate C; run folder C:/hc-runs/2026-11-26-lighting-check): the
// four arrangements runcontrol writes for a known selection, the stage's refusals, the row's outputs (what the verified bundle carries)
// and the registry row's classes. Worked by hand; each case names the wrong answer it rejects.
public static class SelfTestLighting
{
    const string Sky = "Skyrim.esm", Upd = "Update.esm", Cotn = "Cities of the North - Morthal.esp", Lux = "Lux.esp", Other = "Other.esp",
                 CotnMaster = "CotN Base.esm";

    static readonly Dictionary<string, string[]> Masters = new(StringComparer.OrdinalIgnoreCase)
    {
        [Sky] = [], [Upd] = [Sky], [CotnMaster] = [Sky], [Cotn] = [Sky, CotnMaster], [Lux] = [Sky, Upd], [Other] = [Sky],
    };
    static IReadOnlyList<string>? M(string p) => Masters.TryGetValue(p, out var m) ? m : null;

    static string? Same(string what, IEnumerable<string> got, params string[] want) =>
        got.SequenceEqual(want) ? null : $"{what}: got [{string.Join(", ", got)}], want [{string.Join(", ", want)}]";

    public static void Run(Action<string, Func<string?>> @case)
    {
        // C: the profile with the selected mods; R: without them. CotN Base.esm is a master only the selection needs, and it is
        // in the profile without the selection too (another mod folder provides it): it is a dependency, not the rest's.
        string[] c = [Sky, Upd, CotnMaster, Cotn, Lux, Other];
        string[] r = [Sky, Upd, CotnMaster, Lux, Other];

        @case("lighting: the four arrangements for a known selection (D its masters, followed through theirs; S over D; R the profile without it)", () =>
        {
            // rejects: D taken as everything the rest loads (then the rest side is empty), masters not followed past the first level,
            // or the combination's order lost
            var l = LightingArrangements.Lists(c, r, [Cotn], M, out var why);
            if (l is not { } x) return "refused: " + why;
            return Same("dependencies", x.Dependencies, Sky, CotnMaster) ?? Same("selection", x.Selection, Sky, CotnMaster, Cotn)
                   ?? Same("rest", x.RestSide, Sky, Upd, CotnMaster, Lux, Other);
        });
        @case("lighting: a master of a master is a dependency (Update.esm through Mid.esm, the selected plugin's master)", () =>
        {
            // rejects: one level of masters only
            var deep = new Dictionary<string, string[]>(Masters, StringComparer.OrdinalIgnoreCase) { ["Sel.esp"] = ["Mid.esm"], ["Mid.esm"] = [Upd] };
            var l = LightingArrangements.Lists([Sky, Upd, "Mid.esm", "Sel.esp", Lux], [Sky, Upd, "Mid.esm", Lux], ["Sel.esp"],
                                               p => deep.TryGetValue(p, out var m) ? m : null, out var why);
            return l is { } x ? Same("dependencies", x.Dependencies, Sky, Upd, "Mid.esm") : "refused: " + why;
        });
        foreach (var (name, comb, rest, sel, masters, want) in new (string, string[], string[], string[], Func<string, IReadOnlyList<string>?>, string)[]
        {
            ("the selection adds no plugin", c, r, ["Textures Only.esp"], M, "add no plugin"),
            ("a selected plugin also loads without the selection", c, c, [Cotn], M, "loads both with and without"),
            ("the profile without the selection loads a plugin the combination does not", c, [.. r, "Stray.esp"], [Cotn], M, "Stray.esp"),
            ("a header will not read", c, r, [Cotn], p => p == CotnMaster ? null : M(p), "could not be read"),
            ("the rest is only the selection's own masters", [Sky, CotnMaster, Cotn], [Sky, CotnMaster], [Cotn], M, "nothing can clash"),
        })
            @case($"lighting: refused in one sentence when {name}", () =>
            {
                // rejects: an arrangement written anyway (the probe then refuses later with less to say, or judges a wrong side)
                var l = LightingArrangements.Lists(comb, rest, sel, masters, out var why);
                if (l is not null) return "built arrangements: " + string.Join(" / ", l.Value.Dependencies);
                if (why is null || !why.Contains(want)) return $"the refusal is '{why}', which does not say '{want}'";
                return why.EndsWith('.') && !why.TrimEnd('.').Contains(". ") ? null : $"not one sentence: '{why}'";
            });

        @case("lighting: the stage writes the three order files beside the row, the views the right ones, and names them as the row's outputs", () =>
        {
            // rejects: the files written under candidates/ (the plan would read them as arms), a wrong mod view, or outputs the bundle
            // would not carry
            string root = Path.Combine(Path.GetTempPath(), "hc-runcontrol-lighting-" + Guid.NewGuid().ToString("N")[..12]);
            try
            {
                Directory.CreateDirectory(root);
                string cf = PyPath.Join(root, "candidate-01.json"), rf = PyPath.Join(root, "candidate-00-baseline.json");
                PyJson.WritePipelineFile(cf, new PyDict { ["SchemaVersion"] = 1L, ["Name"] = "scan: combined", ["BaseProfile"] = "Manual", ["Plugins"] = Py.Objs(c), ["Mods"] = Py.Objs(["CotN Morthal", "Lux", "Base"]) });
                PyJson.WritePipelineFile(rf, new PyDict { ["SchemaVersion"] = 1L, ["Name"] = "scan: without", ["BaseProfile"] = "Manual", ["Plugins"] = Py.Objs(r), ["Mods"] = Py.Objs(["Lux", "Base"]) });
                var arm = new PyDict { ["armId"] = "candidate-01", ["kind"] = "candidate", ["candidatePath"] = cf };
                var plan = new PyDict
                {
                    ["arms"] = new List<object?> { new PyDict { ["armId"] = "baseline", ["kind"] = "baseline", ["candidatePath"] = rf }, arm },
                    // a target named as Mod Organizer shows the mod: the selection must not come from a name match against it
                    ["targets"] = new List<object?> { new PyDict { ["name"] = "Cities of the North - Morthal" } },
                };
                string outDir = PyPath.Join(root, "outputs", LightingArrangements.CheckId);
                if (LightingArrangements.Write(outDir, plan, arm, M) is { } why) return "refused: " + why;
                List<string> P(string role) => Py.Strs(Py.D(PyJson.ReadPipelineFile(LightingArrangements.PathOf(outDir, role)))["Plugins"]);
                List<string> V(string role) => Py.Strs(Py.D(PyJson.ReadPipelineFile(LightingArrangements.PathOf(outDir, role)))["Mods"]);
                var bad = Same("D", P("dependencies"), Sky, CotnMaster) ?? Same("S", P("selection"), Sky, CotnMaster, Cotn) ?? Same("R", P("rest"), r)
                          ?? Same("D view", V("dependencies"), "Lux", "Base") ?? Same("S view", V("selection"), "CotN Morthal", "Lux", "Base") ?? Same("R view", V("rest"), "Lux", "Base");
                if (bad is not null) return bad;
                string json = PyPath.Join(outDir, "candidate-01.json");
                var outs = LightingArrangements.Outputs(plan, new PyDict { ["checkId"] = LightingArrangements.CheckId }, ["lightprobe.exe", "--check", "--json", json], outDir);
                if (outs is null) return "the output rule named nothing for the lighting check";
                var bad2 = Same("outputs", outs, json, LightingArrangements.PathOf(outDir, "dependencies"), LightingArrangements.PathOf(outDir, "selection"), LightingArrangements.PathOf(outDir, "rest"));
                if (bad2 is not null) return bad2;
                if (LightingArrangements.Outputs(plan, new PyDict { ["checkId"] = "geometry.object-overlap" }, ["x", "--json", json], outDir) is not null)
                    return "the lighting output rule answered for another check";
                // a lab entry's plan: the row's arm is the only baseline, so there is no setup without the selection
                var lab = new PyDict { ["arms"] = new List<object?> { new PyDict { ["armId"] = "baseline", ["kind"] = "baseline", ["candidatePath"] = cf } }, ["targets"] = plan["targets"] };
                var labWhy = LightingArrangements.Write(PyPath.Join(root, "lab"), lab, Py.D(Py.L(lab["arms"])[0]), M);
                return labWhy is not null && labWhy.Contains("start it as a scan from the app") ? null : $"a lab plan gave '{labWhy}'";
            }
            finally { try { Directory.Delete(root, true); } catch (IOException) { } }
        });

        @case("lighting: the registry row names the three arrangements runcontrol fills, and its classes validate", () =>
        {
            // rejects: a token runcontrol does not fill (the row would launch with a literal {rest}), or classes the app cannot read
            var reg = Registry.Load(Checkout.Registry);
            if (Registry.CheckById(reg, LightingArrangements.CheckId) is not { } row) return "the registry has no environment.lighting row";
            var args = Py.Strs(Py.D(row["adapter"])["args"]);
            foreach (var role in LightingArrangements.Roles)
                if (!args.Contains("{" + role + "}") || !GenericRows.OwnPlaceholders.ContainsKey("{" + role + "}")) return $"{{{role}}} is not both in the row's args and filled by runcontrol";
            var problems = new List<string>();
            Registry.ValidateClasses(LightingArrangements.CheckId, row["classes"], problems);
            if (problems.Count > 0) return string.Join("; ", problems);
            var ids = Py.L(row["classes"]).Select(x => Py.S(Py.D(x)["id"])).ToList();
            return Same("class ids", ids, "light-no-fixture", "fixture-no-light", "light-behind-wall", "light-lights-nothing", "fixture-lit-twice", "over-light-limit");
        });
        @case("lighting: a class held without its reason, admitted while held, or a count admitted is refused by the registry guard", () =>
        {
            // rejects: a held class the app would show with no reason, or an over-limit count listed as a clash
            foreach (var (bad, want) in new (PyDict, string)[]
            {
                (new PyDict { ["id"] = "x", ["title"] = "X", ["admitted"] = false, ["evidence"] = "e" }, "waits on"),
                (new PyDict { ["id"] = "x", ["title"] = "X", ["admitted"] = true, ["held"] = "why", ["evidence"] = "e" }, "cannot also be held"),
                (new PyDict { ["id"] = "x", ["title"] = "X", ["admitted"] = true, ["count"] = true, ["evidence"] = "e" }, "a count, never a clash"),
                (new PyDict { ["id"] = "x", ["title"] = "X", ["evidence"] = "e" }, "admitted must be true or false"),
                (new PyDict { ["id"] = "x", ["title"] = "X", ["admitted"] = true, ["evidence"] = "e", ["whenItLightsNothing"] = "sometimes" }, "whenItLightsNothing must be"),
            })
            {
                var problems = new List<string>();
                Registry.ValidateClasses("t", new List<object?> { bad }, problems);
                if (!problems.Any(p => p.Contains(want))) return $"no problem saying '{want}' (got: {string.Join("; ", problems)})";
            }
            return null;
        });
    }
}
