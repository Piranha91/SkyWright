using HousecarlCore.Progress;
using RunControl;

namespace EvalControl;

/// <summary>`evalcontrol plan`: the overlay's plan, frozen once over a finished app scan. Its arms are the app plan's
/// (the same frozen requests, so a receipt here binds to the configuration the app measured); its checks are
/// ai-checks.json's, frozen by runcontrol's own rule (Plan.FreezeChecks) with their applicability from the app's scope;
/// its overlay block binds the app plan (its planId and plan.sha256), the app bundle's manifest (its sha256) and the
/// list. Everything else a plan carries is the app plan's.</summary>
public static class OverlayPlan
{
    /// <summary>The block that binds an overlay to its app scan: the same in the plan and in the overlay bundle's manifest.</summary>
    public const string Key = "overlay";

    public static int Run(string appRun, string appBundle, string outDir, List<string> optIn)
    {
        string app = PyPath.Resolve(appRun), bundle = PyPath.Resolve(appBundle), run = PyPath.Resolve(outDir);
        var (appPlan, why) = EvalStore.LoadPlan(app);
        if (appPlan == null) throw new Refusal($"--app {app}: {why}");
        string manifestPath = PyPath.Join(bundle, "manifest.json");
        if (PyJson.LoadEvalFile(manifestPath) is not PyDict manifest)
            throw new Refusal($"--app-bundle {bundle} holds no readable manifest.json: bundle the app scan first (runcontrol run --through bundle).");
        if (!Ev.Eq(manifest.Get("planId"), appPlan["planId"]))
            throw new Refusal($"--app-bundle {bundle} is the bundle of plan {Ev.Str(manifest.Get("planId"))}, not of the app plan {Ev.Str(appPlan["planId"])}: name the bundle of this run.");
        if (Hashing.Sha256FileOrNull(PyPath.Join(bundle, "plan.json")) != Hashing.Sha256FileOrNull(PyPath.Join(app, "plan.json")))
            throw new Refusal($"--app-bundle {bundle}: its plan.json is not this run's plan.json; bundle the run again.");
        if (Ev.Exists(PyPath.Join(run, "plan.json")))
            throw new Refusal($"--out {run} already holds an overlay plan; plan into a new folder (a plan is frozen once).");
        // every input the app plan pins at its top level is taken from the app bundle's own copy below (the registry is
        // replaced by the list), never from its original, which may have changed or gone since (Hroggar, Ice Blade);
        // a pinned input this program does not know is refused rather than carried on unchecked
        foreach (var (label, pinned) in appPlan)
            if (pinned is PyDict pin && PyValue.Truthy(pin.Get("path")) && PyValue.Truthy(pin.Get("sha256"))
                && label is not ("registry" or "scope" or "search"))
                throw new Refusal($"the app plan pins {label} ({Ev.Str(pin["path"])}), which the overlay does not know how to take from the app bundle; add it to OverlayPlan before planning the AI-side rows.");

        string listSrc = AiList.Shipped;
        var list = Registry.Load(listSrc);
        var problems = AiList.Validate(list, appPlan);
        if (problems.Count > 0) throw new Refusal($"{listSrc} is invalid: " + string.Join("; ", problems));

        // the scope as the app bundle holds it (the copy its verify checks against the app plan's frozen hash)
        var appScope = Ev.OrEmpty(appPlan.Get("scope"));
        PyDict? scope = null;
        string? scopePath = null;
        if (PyValue.Truthy(appScope.Get("sha256")))
        {
            scopePath = PyPath.Join(bundle, "scope", "scope.json");
            if (Hashing.Sha256FileOrNull(scopePath) != Ev.Str(appScope["sha256"]))
                throw new Refusal($"the app bundle's scope copy {scopePath} does not hash to the app plan's frozen scope; bundle the app scan again.");
            scope = PyJson.LoadEvalFile(scopePath) as PyDict;
        }
        object? reachFile = Ev.OrEmpty(appPlan.Get("meshReach")).Get("file");
        var meshReach = PyValue.Truthy(reachFile) ? PyJson.LoadEvalFile(Ev.Str(reachFile)) as PyDict : null;

        var opted = new SortedSet<string>(PyOrder.CodePoint);
        foreach (string id in optIn)
        {
            var c = Registry.CheckById(list, id) ?? throw new Refusal($"--opt-in {PyText.Repr(id)}: {AiList.FileName} defines no such check");
            if (!PyValue.Truthy(c.Get("optIn"))) throw new Refusal($"--opt-in {PyText.Repr(id)}: that check is not opt-in (it is planned by default)");
            opted.Add(id);
        }

        var arms = Ev.Items(appPlan.Get("arms")).Select(PyValue.Dict).ToList();
        string singleArm = appPlan.Get("reviewArm") as string ?? "baseline";
        string harness = Ev.Str(Ev.OrEmpty(appPlan.Get("options")).Get("harness"));
        var applic = Registry.Applicability(list, scope, meshReach);
        var regChecks = Ev.Items(list.Get("checks")).Select(PyValue.Dict).ToList();
        var (checks, unknown, _, touched, tools) = RunControl.Plan.FreezeChecks(
            regChecks, applic, arms, singleArm, opted, new PyDict(), null, harness, AiList.NeverRunKinds);
        foreach (var c in checks)
        {
            var row = regChecks.First(r => Ev.Eq(r["id"], c["checkId"]));
            if (row.ContainsKey("appPrerequisites")) c["appPrerequisites"] = Py.Copy(row["appPrerequisites"]);
        }

        Directory.CreateDirectory(PyPath.Join(run, "registry"));
        string listCopy = PyPath.Join(run, "registry", AiList.FileName);
        File.Copy(listSrc, listCopy, overwrite: false);
        string listSha = Registry.Sha256(listCopy);

        var programs = new PyDict { [PlanIdentity.Controller] = ToolIdentity.Controller() };
        var statesNotExplored = checks.Where(c => Ev.Eq(Ev.OrEmpty(c["inputs"]).Get("states"), "all"))
            .Select(c => (object?)new PyDict
            {
                ["checkId"] = c["checkId"], ["requires"] = "all", ["selectedStates"] = Ev.G(appPlan["states"], "selected"),
                ["note"] = "states beyond the selected ones were not enumerated: this plan carries no state census, so what was not explored is unknown in extent, not empty",
            }).ToList();

        var plan = (PyDict)Py.Copy(appPlan)!;
        plan["planId"] = null;
        plan["createdUtc"] = EvalStore.UtcNow();
        plan["registry"] = new PyDict
        {
            ["path"] = listCopy, ["registryVersion"] = PyValue.Whole(PyValue.Int(list.ContainsKey("registryVersion") ? list["registryVersion"] : 0L)), ["sha256"] = listSha,
        };
        plan["tools"] = tools;
        plan["toolIdentity"] = new PyDict { ["contract"] = (long)PlanIdentity.Contract, ["rule"] = PlanIdentity.Rule, ["programs"] = programs };
        plan["runner"] = new PyDict { ["evalcontrol"] = Ev.G(programs[PlanIdentity.Controller], "digest") };
        if (scopePath != null) PyValue.Dict(plan["scope"])["path"] = scopePath;
        // the candidate search, likewise from the app bundle's copy when the app plan froze one
        if (plan.Get("search") is PyDict search && PyValue.Truthy(search.Get("sha256")))
        {
            string copy = PyPath.Join(bundle, "search", "candidates.json");
            if (Hashing.Sha256FileOrNull(copy) != Ev.Str(search["sha256"]))
                throw new Refusal($"the app bundle's search copy {copy} does not hash to the app plan's frozen candidate search; bundle the app scan again.");
            search["path"] = copy;
        }
        plan["checks"] = checks.Cast<object?>().ToList();
        plan["ordersweepProbes"] = new List<object?>();
        plan["states"] = new PyDict
        {
            ["selected"] = Ev.G(appPlan["states"], "selected"), ["depth"] = Ev.G(appPlan["states"], "depth"), ["notExplored"] = statesNotExplored,
        };
        var budgets = (PyDict)Py.Copy(appPlan["budgets"])!;
        budgets["minutes"] = null;
        budgets["statesNotExplored"] = statesNotExplored;
        plan["budgets"] = budgets;
        var decisions = new PyDict
        {
            ["select"] = null, ["selectRaw"] = null, ["selectImplied"] = new PyDict(), ["exclude"] = new PyDict(),
        };
        if (opted.Count > 0)
        {
            decisions["optIn"] = opted.Cast<object?>().ToList();
            decisions["optInSource"] = new PyDict(opted.Select(c => new KeyValuePair<string, object?>(c, "flag")));
        }
        decisions["touched"] = touched;
        decisions["note"] = "the overlay plans every row of the module's list over the app plan's arms; nothing is selected or excluded here";
        plan["explicitDecisions"] = decisions;
        plan[Key] = new PyDict
        {
            ["appPlanId"] = appPlan["planId"],
            ["appPlanSha256"] = PyValue.Strip(PyJson.Decode(File.ReadAllBytes(PyPath.Join(app, "plan.sha256")), bom: false)),
            ["appBundle"] = PyPath.AsPosix(Path.GetRelativePath(run, bundle)),
            ["appManifestSha256"] = Hashing.Sha256File(manifestPath),
            ["listSha256"] = listSha,
            ["note"] = "an AI-side overlay: these rows are not the app's; its verify checks the app bundle it names, by this hash, and the binding",
        };
        plan["planId"] = Hashing.Sha256Text(PyJson.Canonical(new PyDict(plan.Where(kv => kv.Key != "planId"))))[..12];

        EvalStore.SetRunStore(run);
        string planFile = PyPath.Join(run, "plan.json");
        using (var record = RunLog.Begin("evalcontrol-plan", new RunOptions { StageSource = StageSource.Counted }))
        {
            record.Stage("plan", total: 1, unit: "plans");
            PyJson.WriteEvalFile(planFile, plan);
            File.WriteAllText(PyPath.Join(run, "plan.sha256"), Hashing.Sha256File(planFile) + Environment.NewLine, new System.Text.UTF8Encoding(false));
            record.Advance();
        }
        PyJson.WriteEvalFile(PyPath.Join(run, "ledger.json"), new PyDict
        {
            ["schemaVersion"] = (long)RunControl.Plan.SchemaVersion, ["planId"] = plan["planId"], ["registrySha256"] = listSha,
            ["runIds"] = new List<object?>(), ["entries"] = new PyDict(),
        });
        File.WriteAllBytes(PyPath.Join(run, "journal.jsonl"), []);
        Report.Line($"overlay plan {plan["planId"]}: {checks.Count} AI-side check(s) over the {arms.Count} arm(s) of app plan {Ev.Str(appPlan["planId"])}");
        foreach (var c in checks.Where(c => c["status"] as string != "planned"))
            Report.Line($"   {Ev.Str(c["checkId"]),-38} {c["status"]}");
        if (unknown.Count > 0) Report.Line($"   {unknown.Count} check(s) of unknown applicability — they keep the AI-side evaluation incomplete until measured");
        Report.Line($"written: {planFile}");
        return Exit.Ok;
    }
}
