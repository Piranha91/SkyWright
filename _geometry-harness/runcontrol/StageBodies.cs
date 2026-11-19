namespace RunControl;

/// <summary>A stage's work: what it bound, and the outputs its receipt fingerprints.</summary>
public delegate (PyDict? bound, List<string> outputs) StageBody(RunContext ctx, PyDict res, string stageDir);

/// <summary>The stage bodies this controller has: census, prepare, scope and plan here, evaluate, records and scenes in
/// StageEvaluate, bundle in StageBundle.</summary>
public static class StageBodies
{
    public static Dictionary<string, StageBody> Built() => new(StringComparer.Ordinal)
    {
        ["census"] = Census, ["prepare"] = Prepare, ["scope"] = Scope, ["plan"] = Plan,
        ["evaluate"] = StageEvaluate.Evaluate, ["records"] = StageRecords.Records, ["scenes"] = StageEvaluate.Scenes,
        ["bundle"] = StageBundle.Bundle,
    };

    static bool Incomplete(PyDict scope) =>
        scope.Get("complete") is not true || Py.Truthy(scope.Get("walkFailures")) || Py.Truthy(scope.Get("unreadableRecords"));

    /// <summary>The subject's census on the source profile: the cells and record types it touches, for the scope reconciliation.</summary>
    public static (PyDict?, List<string>) Census(RunContext ctx, PyDict res, string sdir)
    {
        string output = PyPath.Join(sdir, "source-scope.json"), censusFile = PyPath.Join(sdir, "census.json");
        var subject = Py.Strs(res["subject"]);
        if (ctx.InPlace != null && subject.Count == 0)
        {
            PyJson.WritePipelineFile(censusFile, new PyDict
            {
                ["source"] = null, ["cells"] = new PyDict(), ["recordTypes"] = new PyDict(),
                ["note"] = "the selection provides no plugin, so there is no subject to census",
            });
            Report.Line("census: the selection provides no plugin; skipped, and the plan runs without a scope");
            return (new PyDict { ["subject"] = new List<object?>() }, [censusFile]);
        }
        var argv = new List<string>
        {
            Checkout.ScopeprobeExe, "--instance", PyOrder.Fwd(ctx.SourceInstance), "--profile", ctx.SourceProfile,
            "--game", PyOrder.Fwd(ctx.SourceGame), "--assets", "1000000", "--json", PyOrder.Fwd(output),
        };
        if (ctx.InPlace != null && Py.L(res["arms"]).Any(a => Py.Truthy(Py.D(a).Get("switchedOn"))))
        {
            // a selected mod MO2 has disabled is not in the profile as it stands: the census reads the order with it enabled
            var combined = Candidates.Files(ctx, res).First(c => c.arm["id"] as string == "combined");
            string order = PyPath.Join(sdir, "census-order.json");
            Candidates.Write(order, combined.doc);
            argv.AddRange(["--order", PyOrder.Fwd(order)]);
        }
        foreach (string p in subject.Take(1)) argv.AddRange(["--target", p]);
        var r = ChildLaunch.Run(ctx, PyPath.Join(sdir, "commands"), "census-scope", argv);
        if (r.ExitCode != 0 || !File.Exists(output))
            throw new Refusal($"the census scope did not complete (exit {ChildLaunch.ExitText(r)}); read {PyOrder.Fwd(PyPath.Join(sdir, "commands"))}.");
        var doc = Py.D(PyJson.ReadPipelineFile(output));
        if (Incomplete(doc)) throw new Refusal("the census scope is incomplete; it cannot give the subject's cells.");
        var cells = new PyDict();
        var recordTypes = new PyDict();
        foreach (var t in Py.L(doc["targets"]).Select(Py.D))
        {
            var footprint = Py.D(t["footprint"]);
            cells[Py.S(t["name"])] = Py.Objs(ScopeRules.PlacedCells(Py.D(footprint["placed"]), repeats: true));
            recordTypes[Py.S(t["name"])] = footprint["recordTypes"];
        }
        PyJson.WritePipelineFile(censusFile, new PyDict
        {
            ["source"] = PyOrder.Fwd(output), ["sourceSha256"] = Hashing.Sha256File(output), ["cells"] = cells, ["recordTypes"] = recordTypes,
        });
        return (new PyDict { ["subject"] = res["subject"] }, [censusFile, output]);
    }

    /// <summary>One candidate file per arm and the preparation record; a lab entry also gets its base profile.</summary>
    public static (PyDict?, List<string>) Prepare(RunContext ctx, PyDict res, string sdir)
    {
        string cdir = PyPath.Join(sdir, "candidates");
        if (Directory.Exists(cdir)) Directory.Delete(cdir, recursive: true);
        Directory.CreateDirectory(cdir);
        var rows = new List<object?>();
        foreach (var (name, arm, cand) in Candidates.Files(ctx, res))
        {
            string file = PyPath.Join(cdir, name);
            Candidates.Write(file, cand);
            rows.Add(new PyDict
            {
                ["file"] = name, ["arm"] = arm["id"], ["role"] = arm["role"], ["variant"] = arm.Get("variant"), ["keys"] = arm["keys"],
                ["plugins"] = arm["plugins"], ["mods"] = arm["mods"], ["sha256"] = Hashing.Sha256File(file),
            });
        }
        var files = new PyDict();
        string pdir = PyPath.Join(ctx.Instance, "profiles", ctx.Profile);   // an in-place profile is read where it is, never written
        if (ctx.InPlace == null)
        {
            files = Candidates.ProfilePayload(ctx, res);
            pdir = Candidates.WriteProfile(ctx, files);
        }
        var profileFiles = new PyDict();
        foreach (var (n, b) in files) profileFiles[n] = Hashing.Sha256Bytes((byte[])b!);
        string preparation = PyPath.Join(sdir, "preparation.json");
        PyJson.WritePipelineFile(preparation, new PyDict
        {
            ["profile"] = ctx.Profile, ["profileDir"] = PyOrder.Fwd(pdir), ["profileFiles"] = profileFiles, ["baseline"] = res["baseline"],
            ["arms"] = rows, ["headers"] = res["headers"], ["providers"] = res["providers"],
        });
        var hashes = new PyDict();
        foreach (var (p, h) in Py.D(res["headers"])) hashes[p] = Py.D(h)["sha256"];
        return (new PyDict { ["headers"] = hashes }, [cdir, preparation]);
    }

    /// <summary>Each arm's scope by scopeprobe over its candidate, accepted against what the arm prepared.</summary>
    public static (PyDict?, List<string>) Scope(RunContext ctx, PyDict res, string sdir)
    {
        string preparation = PyPath.Join(ctx.Out, "prepare", "preparation.json");
        var prep = Py.D(PyJson.ReadPipelineFile(preparation));
        var census = Py.D(PyJson.ReadPipelineFile(PyPath.Join(ctx.Out, "census", "census.json")));
        var accepted = new PyDict();
        var outs = new List<string>();
        var subject = Py.Strs(res["subject"]);
        foreach (var row in Py.L(prep["arms"]).Select(Py.D))
        {
            var arm = Py.L(res["arms"]).Select(Py.D).First(a => Py.Eq(a["id"], row["arm"]));
            string file = Py.S(row["file"]), stem = file.Replace(".json", "");
            string adir = PyPath.Join(sdir, stem);
            Directory.CreateDirectory(adir);
            string output = PyPath.Join(adir, "scope.json");
            var targets = ScopeRules.Targets(ctx, res, arm);
            var argv = new List<string>
            {
                Checkout.ScopeprobeExe, "--order", PyOrder.Fwd(PyPath.Join(ctx.Out, "prepare", "candidates", file)), "--json", PyOrder.Fwd(output),
                "--assets", "1000000", "--instance", PyOrder.Fwd(ctx.Instance), "--profile", ctx.Profile, "--game", PyOrder.Fwd(ctx.Game),
            };
            foreach (string t in targets) argv.AddRange(["--target", t]);
            bool combined = ctx.InPlace != null && arm["id"] as string == "combined";
            if (combined)
            {
                // the scope line: who else touches the footprint or the loaded grid around it, and which disabled mods would
                argv.AddRange(["--ring", "2"]);
                foreach (string path in ScopeRules.DisabledPluginFiles(ctx, res)) argv.AddRange(["--also-scan", PyOrder.Fwd(path)]);
            }
            var r = ChildLaunch.Run(ctx, PyPath.Join(adir, "commands"), "scope-" + stem, argv);
            if (r.ExitCode != 0 || !File.Exists(output))
            {
                string? why = ScopeRules.OrderRefusal(PyPath.Join(adir, "commands"));
                if (!string.IsNullOrEmpty(why) && ctx.InPlace != null && arm["id"] as string == "without-selection")
                    throw new Refusal($"the profile without the selected mods would not load: {why} Tick the mods that need the "
                                      + "selection too, or pick another mod.");
                throw new Refusal($"scope of arm {PyText.Str(arm["id"])} did not complete (exit {ChildLaunch.ExitText(r)}); read {PyOrder.Fwd(adir)}.");
            }
            var headers = Py.D(prep["headers"]);
            var censusCells = new PyDict();
            // an asset-only selection has no subject, so there is no census cell to reconcile
            if (subject.Count > 0 && targets.Contains(subject[0]))
                censusCells[subject[0]] = Py.D(census["cells"]).Get(subject[0]) ?? new List<object?>();
            var expect = new PyDict
            {
                ["plugins"] = Py.Strs(arm["plugins"]).Select(p => (object?)new PyDict
                {
                    ["name"] = p, ["path"] = Py.D(headers[p])["path"], ["sha256"] = Py.D(headers[p])["sha256"],
                }).ToList(),
                ["mods"] = arm["mods"], ["targets"] = Py.Objs(targets), ["censusCells"] = censusCells,
            };
            var rec = ScopeRules.Accept(Py.D(PyJson.ReadPipelineFile(output)), expect);
            accepted[PyText.Str(row["arm"])] = new PyDict
            {
                ["file"] = row["file"], ["scope"] = PyOrder.Fwd(output), ["sha256"] = Hashing.Sha256File(output), ["targets"] = Py.Objs(targets),
            }.Update(rec);
            outs.Add(output);
        }
        string scopes = PyPath.Join(sdir, "scopes.json");
        PyJson.WritePipelineFile(scopes, accepted);
        if (ctx.InPlace != null && accepted.ContainsKey("combined"))
        {
            string lineFile = PyPath.Join(sdir, "scope-line.json");
            var scope = Py.D(PyJson.ReadPipelineFile(Py.S(Py.D(accepted["combined"])["scope"])));
            // the line speaks of the selection's own plugins: an asset-only selection has none, and the plugin its
            // arms scope only to prove the order loads is not the selection's
            scope["targets"] = Py.ListOrEmpty(scope.Get("targets")).Where(t => subject.Contains(PyText.Str(Py.D(t).Get("name")))).ToList();
            PyJson.WritePipelineFile(lineFile, new PyDict { ["line"] = ScopeRules.Line(scope, res, ScopeRules.DisabledModOf(ctx, res)) });
            outs.Add(lineFile);
        }
        outs.Add(scopes);
        return (new PyDict { ["preparation"] = Hashing.Sha256File(preparation) }, outs);
    }
    /// <summary>The frozen check plan, made by this program's own `plan` command as a child, then held to every prepared arm
    /// and the whole registry.</summary>
    public static (PyDict?, List<string>) Plan(RunContext ctx, PyDict res, string sdir)
    {
        string scopesFile = PyPath.Join(ctx.Out, "scope", "scopes.json");
        var scopes = Py.D(PyJson.ReadPipelineFile(scopesFile));
        string run = PyPath.Join(ctx.Out, "evaluation");
        if (Directory.Exists(run) || File.Exists(run))
            throw new Refusal($"{PyOrder.Fwd(run)} exists from an earlier plan; move it aside to replan, never plan over it.");
        var ev = Py.DictOrEmpty(ctx.Entry.Get("evaluation"));
        string budget = PyText.Str(ev.ContainsKey("budgetMinutes") ? ev["budgetMinutes"] : 90L);
        string candidates = PyOrder.Fwd(PyPath.Join(ctx.Out, "prepare", "candidates"));
        string[] where = ["--instance", PyOrder.Fwd(ctx.Instance), "--profile", ctx.Profile, "--game", PyOrder.Fwd(ctx.Game)];
        List<string> argv;
        Dictionary<string, string>? env = null;
        if (ctx.InPlace != null)
        {
            var (select, depth) = InPlaceResolve.Select(ctx);
            // an asset-only selection plans without a scope: a scope needs a target, and any target would be a plugin outside the selection
            string[] scope = Py.Truthy(res["subject"]) ? ["--scope", Py.S(Py.D(scopes["combined"])["scope"])] : [];
            argv = ChildLaunch.Controller("plan", ["--registry", PyOrder.Fwd(Checkout.Registry), .. scope, "--candidates", candidates,
                "--depth", depth, "--budget-minutes", budget, "--max-candidates", PyText.Str((long)scopes.Count), "--out", PyOrder.Fwd(run),
                "--select", string.Join(",", select), "--review-arm", "candidate-01", .. where]);
            if (Py.Truthy(ev.Get("primitiveSolidity"))) argv.AddRange(["--primitive-solidity", PyText.Str(ev["primitiveSolidity"])]);
            // never this machine's harness config: a manual run on another machine would get another table or none
            else env = new() { ["HOUSECARL_HARNESS_CONFIG"] = "", ["HOUSECARL_PRIMITIVE_SOLIDITY"] = "" };
        }
        else
            argv = ChildLaunch.Controller("plan", ["--registry", PyOrder.Fwd(Checkout.Registry), "--scope", Py.S(Py.D(scopes["combined"])["scope"]),
                "--candidates", candidates, "--depth", PyText.Str(ev.ContainsKey("depth") ? ev["depth"] : "deep"), "--budget-minutes", budget,
                "--max-candidates", PyText.Str((long)scopes.Count), "--out", PyOrder.Fwd(run), .. where]);
        foreach (object? o in Py.ListOrEmpty(ev.Get("optIn"))) argv.AddRange(["--opt-in", PyText.Str(o)]);
        var r = ChildLaunch.Run(ctx, PyPath.Join(sdir, "commands"), "plan", argv, env: env);
        string planFile = PyPath.Join(run, "plan.json");
        if (r.ExitCode != 0 || !File.Exists(planFile))
            throw new Refusal($"evalrun plan refused or failed (exit {ChildLaunch.ExitText(r)}); its sentence is in {PyOrder.Fwd(PyPath.Join(sdir, "commands"))}.");
        var plan = Py.D(PyJson.ReadPipelineFile(planFile));
        var prep = Py.D(PyJson.ReadPipelineFile(PyPath.Join(ctx.Out, "prepare", "preparation.json")));
        var prepared = Py.L(prep["arms"]).Select(row => Py.S(Py.D(row)["file"])).ToHashSet(StringComparer.Ordinal);
        var planned = Py.ListOrEmpty(plan.Get("arms")).Select(a => PyPath.Name(PyText.Str(Py.D(a).Get("candidateFile")))).ToHashSet(StringComparer.Ordinal);
        var held = Py.DictOrEmpty(Py.DictOrEmpty(plan.Get("budgets")).Get("candidatesHeldBack"));
        if (!planned.SetEquals(prepared) || Py.Truthy(held.Get("count")))
            throw new Refusal("the frozen plan does not hold every prepared arm (some were held back or missing); "
                              + "raise --max-candidates or fix the candidates.");
        var registry = Py.D(PyJson.ReadPipelineFile(Checkout.Registry));
        if (Py.ListOrEmpty(plan.Get("checks")).Count != Py.L(registry["checks"]).Count)
            throw new Refusal("the frozen plan does not carry the full check registry.");
        return (new PyDict { ["scopes"] = Hashing.Sha256File(scopesFile) }, [planFile]);
    }
}
