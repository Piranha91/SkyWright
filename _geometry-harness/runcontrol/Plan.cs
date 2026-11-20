using System.Text.RegularExpressions;
using HousecarlCore.Progress;

namespace RunControl;

/// <summary>`plan`: derives the check plan from the registry and a measured scope and freezes it in a new run
/// folder: plan.json and its hash, the population file, the primitive table's copy, an empty ledger and journal.</summary>
public static class Plan
{
    public const int SchemaVersion = 2;
    /// <summary>What a plan's arms[].requested.providers means: the providers that arm's own view reads.</summary>
    public const string RequestProviders = "arm-view";
    public const string LiveCheckId = "runtime.state-switch-flip";
    public const string NotOptedIn = "not-opted-in";

    /// <summary>The placeholders the controller fills in a check's adapter.args.</summary>
    public static readonly HashSet<string> Placeholders = new(StringComparer.Ordinal)
    { "order", "emit", "json", "base", "out", "run", "arm", "target", "nexusId", "switchMap", "pair" };

    static readonly Regex PlaceholderForm = new(@"\{([A-Za-z0-9_]+)\}", RegexOptions.CultureInvariant);
    static readonly string[] NeverRunKinds = ["unsupported", "mcp-only", "manual", "live-import"];

    /// <summary>An input path as the plan stores it: absolute against the current folder, links and mapped drives kept.</summary>
    public static string GivenPath(string p) => PyPath.NormPath(Path.GetFullPath(p));

    /// <summary>The registry's checks in prerequisite order, so a check runs after the checks whose receipts it needs.</summary>
    public static List<PyDict> TopoOrder(List<PyDict> checks)
    {
        var byId = new Dictionary<string, PyDict>(StringComparer.Ordinal);
        foreach (var c in checks) byId[Py.S(c["id"])] = c;
        var ordered = new List<PyDict>();
        var state = new Dictionary<string, int>(StringComparer.Ordinal);
        void Visit(string cid, List<string> chain)
        {
            if (state.GetValueOrDefault(cid) == 2 || !byId.ContainsKey(cid)) return;
            if (state.GetValueOrDefault(cid) == 1) throw new Refusal("prerequisite cycle: " + string.Join(" -> ", chain.Append(cid)));
            state[cid] = 1;
            foreach (string pre in Py.ListOrEmpty(byId[cid].Get("prerequisites")).Select(PyText.Str)) Visit(pre, [.. chain, cid]);
            state[cid] = 2;
            ordered.Add(byId[cid]);
        }
        foreach (var c in checks) Visit(Py.S(c["id"]), []);
        return ordered;
    }

    // The registry at a path, validated: (document, sha256 of its bytes, checks in prerequisite order).
    static (PyDict doc, string sha256, List<PyDict> checks) ReadRegistry(string path)
    {
        if (!File.Exists(path)) throw new Refusal($"[Errno 2] No such file or directory: {PyText.Repr(path)}");
        string sha = Registry.Sha256(path);
        var doc = Registry.Load(path);
        var problems = Registry.Validate(doc);
        if (problems.Count > 0) throw new Refusal($"registry {path} is invalid: " + string.Join("; ", problems));
        return (doc, sha, TopoOrder(Py.ListOrEmpty(doc.Get("checks")).Select(Py.D).ToList()));
    }

    // The comma-separated tokens of a repeatable flag, stripped, empty ones dropped.
    static List<string> Tokens(List<string>? given) =>
        string.Join(",", given ?? []).Split(',').Select(PyValue.Strip).Where(t => t.Length > 0).ToList();

    // The record of the candidate search the plan read its budget from: its path and hash, or a sentence saying it is absent.
    static PyDict SearchRecord(string cdir)
    {
        string f = PyPath.Join(cdir, "candidates.json");
        if (File.Exists(f)) return new PyDict { ["path"] = f, ["sha256"] = Hashing.Sha256FileOrNull(f) };
        return new PyDict
        {
            ["path"] = null, ["sha256"] = null,
            ["absent"] = $"the candidates folder {cdir} holds no candidates.json, so this plan has no candidate-search record",
        };
    }

    /// <summary>A run's timing history stays with the run unless the caller said otherwise.</summary>
    public static void SetRunStore(string run)
    {
        if (PyValue.Strip(Environment.GetEnvironmentVariable("HOUSECARL_RUNLOG_DIR") ?? "").Length == 0)
            Environment.SetEnvironmentVariable("HOUSECARL_RUNLOG_DIR", PyPath.Resolve(PyPath.Join(run, "runlog")));
    }

    static List<string> ReachFolders(PyDict reach) =>
        Py.ListOrEmpty(reach.Get("folders")).Select(f => PyText.Str(Py.DictOrEmpty(f).Get("name"))).ToList();

    /// <summary>The mesh reach must be about exactly the folders the probes will credit: the population's mods when it is scoped,
    /// its meshFolders when it is not. Otherwise the applicability it gives is about other folders, and the plan refuses.</summary>
    public static void CheckReachFolders(PyDict reach, PyDict populationBlock, IEnumerable<PyDict> checks)
    {
        // only a planned mesh probe credits a folder; with none planned there is nothing the reach must agree with
        bool meshPlanned = checks.Any(c => Py.DictOrEmpty(c.Get("adapter")).Get("exe") is string exe && HarnessShared.PopulationRules.MeshProbes.Contains(exe)
                                           && Py.ListOrEmpty(c.Get("arms")).Any(r => Py.DictOrEmpty(r).Get("status") as string == "planned"));
        if (!meshPlanned) return;
        var credited = Py.ListOrEmpty(Py.Truthy(populationBlock.Get("scoped")) ? populationBlock.Get("mods") : populationBlock.Get("meshFolders"))
            .Select(PyText.Str).ToList();
        var measured = ReachFolders(reach);
        var a = PyOrder.SortedByLowerKey(credited.Distinct(StringComparer.OrdinalIgnoreCase));
        var b = PyOrder.SortedByLowerKey(measured.Distinct(StringComparer.OrdinalIgnoreCase));
        if (!a.Select(PyText.Lower).SequenceEqual(b.Select(PyText.Lower)))
            throw new Refusal($"the mesh reach measured the folders [{string.Join(", ", b)}], but this plan credits [{string.Join(", ", a)}]; "
                              + "re-run the scope stage so the reach is about the folders the probes credit.");
    }

    public static int Run(PlanArgs a)
    {
        string run = PyPath.Str(a.Out!);
        string registryPath = GivenPath(a.Registry!);
        string? scopePath = string.IsNullOrEmpty(a.Scope) ? null : GivenPath(a.Scope);
        string cdir = GivenPath(a.Candidates!);
        string? settingsPath = string.IsNullOrEmpty(a.Settings) ? a.Settings : GivenPath(a.Settings);
        string? instance = string.IsNullOrEmpty(a.Instance) ? a.Instance : GivenPath(a.Instance);
        string? game = string.IsNullOrEmpty(a.Game) ? a.Game : GivenPath(a.Game);

        PyDict regDoc;
        string regSha;
        List<PyDict> regChecks;
        try { (regDoc, regSha, regChecks) = ReadRegistry(registryPath); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or FormatException or PyError) { throw new Refusal(ex.Message); }

        // a flag beats the settings file, the file beats the built-in default; the effective values are frozen into the plan
        var flags = new PyDict();
        if (!string.IsNullOrEmpty(a.Depth)) flags["evaluation.depth"] = a.Depth;
        if (a.NoReverse) flags["evaluation.reverseComparison"] = false;
        if (a.BudgetMinutes is { } asked) flags["evaluation.budgetMinutes"] = asked;
        var settings = EvalSettings.Load(settingsPath, flags);
        bool noReverse = settings.Value("evaluation.reverseComparison") is false;
        object? budgetMinutes = settings.Value("evaluation.budgetMinutes");
        object? liveChoice = settings.Value("evaluation.liveSwitchCheck");

        object? scopeRead = scopePath != null ? PyJson.LoadEvalFile(scopePath) : null;
        if (scopePath != null && scopeRead == null)
            throw new Refusal($"--scope {scopePath}: not readable as JSON. Applicability would be unknown "
                              + "for every check; run scopeprobe first or say so explicitly.");
        var scope = scopeRead == null ? null : PyValue.Dict(scopeRead);
        var scopeOr = scope ?? new PyDict();
        string? scopeSha = scopePath != null ? Hashing.Sha256FileOrNull(scopePath) : null;
        // the mesh reach (scope/mesh-reach.json): the one input the placedMeshes trigger reads, with or without a scope
        string? reachPath = string.IsNullOrEmpty(a.MeshReach) ? null : GivenPath(a.MeshReach);
        PyDict? meshReach = null;
        if (reachPath != null)
        {
            object? read = File.Exists(reachPath) ? PyJson.LoadEvalFile(reachPath) : null;
            if (read is not PyDict rd) throw new Refusal($"--mesh-reach {reachPath}: not readable as a JSON object; re-run the scope stage.");
            meshReach = rd;
        }

        if (!Directory.Exists(cdir)) throw new Refusal($"--candidates {cdir}: not a directory");
        var files = PyOrder.SortedPaths(Directory.EnumerateFileSystemEntries(cdir).Where(f =>
        {
            string n = PyText.Lower(PyPath.Name(f));
            return n.Length >= "candidate-.json".Length && n.StartsWith("candidate-", StringComparison.Ordinal) && n.EndsWith(".json", StringComparison.Ordinal);
        }));
        if (files.Count == 0) throw new Refusal($"--candidates {cdir}: no candidate-*.json");
        var bases = files.Where(f => Stem(f).EndsWith("baseline", StringComparison.Ordinal)).ToList();
        if (bases.Count != 1) throw new Refusal($"--candidates {cdir}: expected exactly one candidate-*-baseline.json, found {bases.Count}");
        string baseline = bases[0];

        var alts = files.Where(f => f != baseline).ToList();
        if (a.Alt is { Count: > 0 })
        {
            var wanted = a.Alt.Select(PyPath.Name).ToHashSet(StringComparer.Ordinal);
            var missing = wanted.Except(alts.Select(PyPath.Name)).ToList();
            if (missing.Count > 0)
                throw new Refusal("--alt names candidate files that are not in --candidates: " + string.Join(", ", PyOrder.SortedByCodePoint(missing)));
            alts = alts.Where(f => wanted.Contains(PyPath.Name(f))).ToList();
        }
        // --max-candidates holds arms back rather than dropping them: a held-back arm stays visible, its checks skipped-budget
        var heldBack = new HashSet<string>(StringComparer.Ordinal);
        if (a.MaxCandidates is { } most)
        {
            long from = most < 0 ? Math.Max(0, alts.Count + most) : Math.Min(most, alts.Count);
            foreach (string f in alts.Skip((int)from)) heldBack.Add(PyPath.Name(f));
        }

        var supplied = new List<(List<string> declared, string cand)>();
        foreach (string spec in a.Supplied ?? [])
        {
            // `=` separates the plugins from the file; `:` still works when the file part carries none of its own
            int eq = spec.IndexOf('=');
            string head = eq < 0 ? "" : spec[..eq], tail = eq < 0 ? "" : spec[(eq + 1)..];
            if (eq < 0)
            {
                int colon = spec.LastIndexOf(':');
                (head, tail) = colon < 0 ? ("", spec) : (spec[..colon], spec[(colon + 1)..]);
                if (tail.Contains(':')) (head, tail) = ("", "");
            }
            if (head.Length == 0 || !PyText.Lower(tail).EndsWith(".json", StringComparison.Ordinal))
                throw new Refusal($"--supplied {PyText.Repr(spec)}: expected PLUGIN[,PLUGIN...]=candidate-NN.json");
            string cand = Path.IsPathFullyQualified(tail) ? PyPath.Str(tail) : PyPath.Join(cdir, PyPath.Name(tail));
            if (!File.Exists(cand) && !Directory.Exists(cand))
                throw new Refusal($"--supplied {PyText.Repr(spec)}: candidate file {cand} does not exist");
            supplied.Add((head.Split(',').Select(PyValue.Strip).Where(p => p.Length > 0).ToList(), cand));
        }

        var cfg = Py.Truthy(scopeOr.Get("configuration")) ? PyValue.Dict(scopeOr["configuration"])
            : Py.Truthy(scopeOr.Get("Configuration")) ? PyValue.Dict(scopeOr["Configuration"]) : new PyDict();
        var cfgPlugins = cfg.ContainsKey("plugins") ? PyValue.Iter(cfg["plugins"]).ToList() : Py.ListOrEmpty(cfg.Get("Plugins"));
        var scopePlugins = new PyDict();
        // which layer gave the scope its copy of each plugin, kept apart from the rows the plan freezes
        var scopePluginProvider = new PyDict();
        foreach (var p in cfgPlugins.Select(PyValue.Dict))
        {
            string key = PyText.Lower(PyText.Str(PlanArms.Either(p, "name", "Name", "")));
            scopePlugins[key] = new PyDict
            {
                ["name"] = PlanArms.Either(p, "name", "Name"), ["path"] = PlanArms.Either(p, "path", "Path"), ["sha256"] = PlanArms.Either(p, "sha256", "Sha256"),
            };
            scopePluginProvider[key] = PlanArms.Either(p, "provider", "Provider");
        }
        object? statedPopulation = PlanArms.Either(cfg, "population", "Population");
        string population = Py.Truthy(statedPopulation) ? PyText.Str(statedPopulation) : "load-time";
        // the scope's providers and overlays are part of the request: what was never recorded cannot be compared later
        var scopeProviders = Py.ListOrEmpty(PlanArms.Either(cfg, "providers", "Providers", new List<object?>())).Select(PyValue.Dict)
            .Select(v => new PyDict { ["name"] = PlanArms.Either(v, "name", "Name"), ["kind"] = PlanArms.Either(v, "kind", "Kind") }).ToList();
        var scopeOverlays = Py.ListOrEmpty(PlanArms.Either(cfg, "overlays", "Overlays", new List<object?>())).Select(PyValue.Dict)
            .Select(o => new PyDict { ["kind"] = PlanArms.Either(o, "kind", "Kind"), ["path"] = PlanArms.Either(o, "path", "Path"), ["sha256"] = PlanArms.Either(o, "sha256", "Sha256") }).ToList();

        // standard judges the load-time state; deep judges every switched state each arm's census yields; --states names them by hand
        List<string> states;
        string depth;
        if (a.States != null)
        {
            states = a.States.Split(',').Select(PyValue.Strip).Where(s => s.Length > 0).ToList();
            if (states.Count == 0) states = ["baseline"];
            depth = "explicit";
        }
        else
        {
            states = ["baseline"];
            depth = PyText.Str(settings.Value("evaluation.depth"));
        }

        var layers = string.IsNullOrEmpty(instance) ? null : new PlanLayers(instance, a.Profile, game);
        var scopeView = scopeProviders.Where(v => PyText.Lower(Py.Truthy(v.Get("kind")) ? PyText.Str(v["kind"]) : "") == "mod" && Py.Truthy(v.Get("name")))
            .Select(v => PyText.Str(v["name"])).ToList();
        var arms = new List<PyDict>();

        void AddArm(string kind, string cand, List<string>? declared = null)
        {
            var doc = PyJson.LoadEvalFile(cand) as PyDict ?? new PyDict();
            string armId = kind == "baseline" ? "baseline" : Stem(cand);
            var plugins = new List<object?>(Py.ListOrEmpty(Py.Truthy(doc.Get("Plugins")) ? doc["Plugins"] : doc.Get("plugins")));
            // the view this arm's plugins resolve through: the candidate's own mod list when it has one (an empty list is
            // an empty view), else the scope's mod providers in the order the probe recorded them
            List<string>? ownView = PlanArms.Either(doc, "Mods", "mods") is List<object?> declaredView
                ? declaredView.Select(PyText.Str).Where(m => PyValue.Strip(m).Length > 0).ToList() : null;
            var viewMods = ownView ?? scopeView;
            LayerWalk? Resolve(string plain) => layers?.Walk(plain, viewMods);
            // an arm that declares a mod list other than the scope's is resolved through it, even for names the scope measured
            OwnView? armView = ownView != null && !ownView.Select(PyText.Lower).SequenceEqual(scopeView.Select(PyText.Lower))
                ? new OwnView(layers != null && !string.IsNullOrEmpty(game) && !string.IsNullOrEmpty(a.Profile), ownView, scopeView, scopePluginProvider,
                              (plain, hashed) => layers!.Walk(plain, viewMods, hashed))
                : null;

            var payloadMissing = new List<object?>();
            var outOfView = new List<object?>();
            foreach (string name in declared ?? [])
            {
                string where = PlanArms.Provides(PyPath.Name(name), scopePlugins, Resolve);
                if (where == "out-of-view") outOfView.Add(PyPath.Name(name));
                else if (where != "in-view") payloadMissing.Add(PyPath.Name(name));
            }
            var rec = new PyDict
            {
                ["armId"] = armId, ["kind"] = kind, ["candidateFile"] = PyPath.Name(cand), ["candidatePath"] = cand,
                ["candidateSha256"] = Hashing.Sha256FileOrNull(cand), ["plugins"] = plugins,
                ["mods"] = Py.Truthy(doc.Get("Mods")) ? doc["Mods"] : doc.Get("mods"),
                ["changes"] = new List<object?>(Py.ListOrEmpty(Py.Truthy(doc.Get("Changes")) ? doc["Changes"] : doc.Get("changes"))),
                ["provenance"] = Py.Truthy(doc.Get("Provenance")) ? doc["Provenance"] : Py.Truthy(doc.Get("provenance")) ? doc["provenance"] : new PyDict(),
                ["loadedInGame"] = null,
                ["loadedInGameNote"] = "offline hypothetical configuration; not verified loaded in game",
                ["expected"] = new PyDict { ["population"] = population, ["states"] = Py.Objs(states) },
                ["heldBack"] = heldBack.Contains(PyPath.Name(cand)),
                // the frozen request for this arm; every accepted receipt on the arm is bound to it
                ["requested"] = new PyDict
                {
                    ["plugins"] = plugins.Select(n => (object?)PlanArms.RequestedPlugin(n, scopePlugins, Resolve, armView)).ToList(),
                    ["overlays"] = scopeOverlays.Select(o => (object?)new PyDict(o)).ToList(),
                    ["providers"] = PlanArms.RequestedProviders(scopeProviders, ownView),
                    ["population"] = population,
                    ["states"] = Py.Objs(states),
                },
            };
            if (kind == "supplied-combination")
            {
                rec["supplied"] = new PyDict { ["declaredFiles"] = Py.Objs(declared ?? []), ["payloadMissing"] = payloadMissing, ["outOfView"] = outOfView };
                string note = "an ESP-only arm: the supplied plugin was placed in the order, but its meshes, "
                              + "scripts and installer choices were NOT staged and are not part of this evaluation";
                if (outOfView.Count > 0)
                    note += "; " + string.Join(", ", outOfView.Select(PyText.Str)) + " is provided only by a mod folder that is "
                            + "not in the profile's view, so its assets are certainly not staged";
                rec["payload"] = new PyDict { ["espOnly"] = true, ["staged"] = new List<object?>(), ["note"] = note };
            }
            else
                rec["payload"] = new PyDict
                {
                    ["espOnly"] = false, ["staged"] = new List<object?>(), ["note"] = "the candidate reorders plugins already present in the configuration",
                };
            arms.Add(rec);
        }

        AddArm("baseline", baseline);
        foreach (string f in alts) AddArm("candidate", f);
        foreach (var (declared, cand) in supplied) AddArm("supplied-combination", cand, declared);

        string? reviewArm = string.IsNullOrEmpty(a.ReviewArm) ? null : a.ReviewArm;
        if (reviewArm != null && !arms.Any(x => Py.S(x["armId"]) == reviewArm))
            throw new Refusal($"--review-arm {reviewArm} names no arm of this plan (its arms are "
                              + $"{string.Join(", ", arms.Select(x => Py.S(x["armId"])))}); name one of them or leave it out");
        string singleArm = reviewArm ?? "baseline";

        object? budgetBlock = (PyJson.LoadEvalFile(PyPath.Join(cdir, "candidates.json")) as PyDict)?.Get("Budget");

        var known = regChecks.Select(c => Py.S(c["id"])).ToHashSet(StringComparer.Ordinal);
        var byExe = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        foreach (var c in regChecks)
            if (Py.DictOrEmpty(c.Get("adapter")).Get("exe") is string e && e.Length > 0)
            {
                if (!byExe.TryGetValue(e, out var ids)) byExe[e] = ids = [];
                ids.Add(Py.S(c["id"]));
            }

        // --select takes check ids; an exe name expands to every check that runs it. The plan records the resolved ids.
        HashSet<string>? selected = null;
        List<string>? selectRaw = null;
        var implied = new PyDict();
        if (a.Select is { Count: > 0 })
        {
            selectRaw = Tokens(a.Select);
            selected = new HashSet<string>(StringComparer.Ordinal);
            var unresolved = new List<string>();
            foreach (string tok in selectRaw)
            {
                if (known.Contains(tok)) selected.Add(tok);
                else if (byExe.TryGetValue(tok, out var ids)) selected.UnionWith(ids);
                else unresolved.Add(tok);
            }
            if (unresolved.Count > 0)
                throw new Refusal("--select names neither a registry check id nor an adapter exe: " + string.Join(", ", PyOrder.SortedByCodePoint(unresolved)));
            // a selection implies every prerequisite it needs, and the plan says which check implied each
            var byId = regChecks.ToDictionary(c => Py.S(c["id"]), StringComparer.Ordinal);
            var frontier = PyOrder.SortedByCodePoint(selected);
            while (frontier.Count > 0)
            {
                string cid = frontier[^1];
                frontier.RemoveAt(frontier.Count - 1);
                foreach (string pre in Py.ListOrEmpty(byId[cid].Get("prerequisites")).Select(PyText.Str))
                    if (selected.Add(pre))
                    {
                        implied[pre] = $"implied-by:{cid}";
                        frontier.Add(pre);
                    }
            }
        }
        // an opt-in check is planned only when the caller names it; opting in makes it required for this run
        var opted = new SortedSet<string>(PyOrder.CodePoint);
        foreach (string tok in Tokens(a.OptIn))
        {
            var chk = regChecks.FirstOrDefault(c => Py.S(c["id"]) == tok)
                      ?? throw new Refusal($"--opt-in {PyText.Repr(tok)}: the registry defines no such check");
            if (!Py.Truthy(chk.Get("optIn"))) throw new Refusal($"--opt-in {PyText.Repr(tok)}: that check is not opt-in (it is planned by default)");
            opted.Add(tok);
        }
        var optInSource = new PyDict();
        foreach (string c in opted) optInSource[c] = "flag";
        if (PyValue.Eq(liveChoice, "always") && !opted.Contains(LiveCheckId)
            && regChecks.Any(c => Py.S(c["id"]) == LiveCheckId && Py.Truthy(c.Get("optIn"))))
        {
            opted.Add(LiveCheckId);
            optInSource[LiveCheckId] = "settings: evaluation.liveSwitchCheck always";
        }
        var excluded = new PyDict();
        foreach (string spec in a.Exclude ?? [])
        {
            int eq = spec.IndexOf('=');
            string cid = PyValue.Strip(eq < 0 ? spec : spec[..eq]), reason = PyValue.Strip(eq < 0 ? "" : spec[(eq + 1)..]);
            if (cid.Length == 0 || reason.Length == 0)
                throw new Refusal($"--exclude {PyText.Repr(spec)}: expected CHECK=reason (a reason is not optional)");
            excluded[cid] = reason;
        }
        var unknownExcluded = excluded.Keys.Where(k => !known.Contains(k)).ToList();
        if (unknownExcluded.Count > 0)
            throw new Refusal("--exclude names check ids the registry does not define: " + string.Join(", ", PyOrder.SortedByCodePoint(unknownExcluded)));

        var probeCmds = new PyDict();
        foreach (string spec in a.ProbeCmd ?? [])
        {
            int eq = spec.IndexOf('=');
            string name = PyValue.Strip(eq < 0 ? spec : spec[..eq]), prefix = PyValue.Strip(eq < 0 ? "" : spec[(eq + 1)..]);
            if (name.Length == 0 || prefix.Length == 0) throw new Refusal($"--probe-cmd {PyText.Repr(spec)}: expected NAME=<argv prefix>");
            probeCmds[name] = prefix;
        }

        string harness = string.IsNullOrEmpty(a.Harness) ? Checkout.Harness : PyPath.Resolve(a.Harness);
        var options = new PyDict
        {
            ["instance"] = instance, ["profile"] = a.Profile, ["game"] = game, ["harness"] = harness, ["probeArgs"] = a.ProbeArgs,
            ["probeCmd"] = probeCmds, ["diffTolerance"] = a.DiffTolerance, ["noReverse"] = noReverse, ["population"] = population,
        };

        foreach (var check in regChecks)
            foreach (object? tok in Py.ListOrEmpty(Py.DictOrEmpty(check.Get("adapter")).Get("args")))
                foreach (Match m in PlaceholderForm.Matches(PyText.Str(tok)))
                    if (!Placeholders.Contains(m.Groups[1].Value))
                        throw new Refusal($"registry check {check["id"]}: adapter.args uses the placeholder "
                                          + $"{{{m.Groups[1].Value}}}, which this controller cannot fill. Known placeholders: "
                                          + string.Join(", ", PyOrder.SortedByCodePoint(Placeholders))
                                          + ". A placeholder is never passed through to a probe literally.");

        PyDict appMap;
        try { appMap = Registry.Applicability(regDoc, scope, meshReach); }
        catch (Exception ex) when (ex is PyError or Refusal or InvalidCastException or KeyNotFoundException)
        {
            throw new Refusal($"the registry's applicability could not be derived from this scope: {ex.Message}");
        }

        var checks = new List<PyDict>();
        var unknownApplicability = new List<PyDict>();
        var ordersweepProbes = new List<string>();
        var touched = new PyDict();
        var tools = new PyDict();
        foreach (var check in regChecks)
        {
            string cid = Py.S(check["id"]);
            var adapter = new PyDict(Py.DictOrEmpty(check.Get("adapter")));
            object? kind = adapter.ContainsKey("kind") ? adapter["kind"] : "unsupported";
            var row = Py.DictOrEmpty(appMap.Get(cid));
            object? applicability = row.ContainsKey("applicability") ? row["applicability"] : "unknown";
            object? evidence = row.ContainsKey("evidence") ? row["evidence"] : "the registry returned no applicability for this check";
            if (applicability is not ("yes" or "no" or "unknown"))
                throw new Refusal($"registry check {cid}: applicability {PyText.Repr(applicability)} is not yes/no/unknown");

            var inputs = Py.DictOrEmpty(check.Get("inputs"));
            object? want = inputs.ContainsKey("arms") ? inputs["arms"] : "all";
            var armIds = PyValue.Eq(want, "all") ? arms.Select(x => Py.S(x["armId"])).ToList() : [singleArm];

            string? exe = adapter.Get("exe") as string;
            if (!string.IsNullOrEmpty(exe) && !tools.ContainsKey(exe)) tools[exe] = PlanIdentity.ToolRecord(harness, exe);

            // first match wins: a check that could not have run is recorded as that, selected or not, and a caller's
            // narrowing is recorded for optional checks too
            string? baseStatus = null;
            if (PyValue.Eq(applicability, "no")) baseStatus = "not-applicable";
            else if (Py.Truthy(check.Get("optIn")) && !opted.Contains(cid)) baseStatus = NotOptedIn;
            else if (kind is string k && NeverRunKinds.Contains(k)) baseStatus = k;

            var perArm = new List<object?>();
            foreach (string aid in armIds)
            {
                var arm = arms.First(x => Py.S(x["armId"]) == aid);
                string? st = baseStatus;
                if (st == null)
                {
                    if (PyValue.Eq(want, "baseline") && aid != singleArm) st = "unsupported-for-arm";
                    else if (Py.Truthy(Py.DictOrEmpty(arm.Get("supplied")).Get("payloadMissing"))) st = "blocked-payload-missing";
                    else if (excluded.ContainsKey(cid)) { st = "excluded"; touched[cid] = "excluded"; }
                    else if (selected != null && !selected.Contains(cid)) { st = "skipped-user"; touched[cid] = "not in --select"; }
                    else if (Py.Truthy(arm.Get("heldBack"))) st = "skipped-budget";
                    else st = "planned";
                }
                else if (excluded.ContainsKey(cid)) touched[cid] = $"excluded, but the status is {st} (it could not have run anyway)";
                else if (selected != null && !selected.Contains(cid)) touched[cid] = $"not in --select, but the status is {st} (it could not have run anyway)";
                perArm.Add(new PyDict { ["armId"] = aid, ["status"] = st });
            }
            bool anyPlanned = perArm.Any(x => Py.D(x)["status"] as string == "planned");
            baseStatus ??= anyPlanned ? "planned" : perArm.Count > 0 ? Py.S(Py.D(perArm[0])["status"]) : "planned";
            if (anyPlanned && PyValue.Eq(kind, "ordersweep-probe") && !string.IsNullOrEmpty(exe)) ordersweepProbes.Add(exe);
            if (PyValue.Eq(applicability, "unknown")) unknownApplicability.Add(new PyDict { ["checkId"] = cid, ["evidence"] = evidence });

            var planned = new PyDict
            {
                ["checkId"] = cid, ["family"] = check.Get("family"), ["title"] = check.Get("title"),
                ["required"] = Py.Truthy(check.Get("required")) || opted.Contains(cid),
            };
            if (Py.Truthy(check.Get("optIn"))) { planned["optIn"] = true; planned["optedIn"] = opted.Contains(cid); }
            planned.Update(new PyDict
            {
                ["applicability"] = applicability, ["applicabilityEvidence"] = evidence,
                ["prerequisites"] = new List<object?>(Py.ListOrEmpty(check.Get("prerequisites"))),
                ["inputs"] = Py.Truthy(check.Get("inputs")) ? check["inputs"] : new PyDict(),
                ["evidence"] = Py.Truthy(check.Get("evidence")) ? check["evidence"] : new PyDict(),
                ["adapter"] = adapter,
                ["manualDecisionAllowed"] = Py.Truthy(check.Get("manualDecisionAllowed")),
                ["unsupportedReason"] = check.Get("unsupportedReason"),
                ["arms"] = perArm,
                ["status"] = baseStatus,
            });
            checks.Add(planned);
        }

        // an arm the scene captures will refuse is refused here, by the same check, before anything runs
        var sceneArms = checks.Where(c => Py.D(c["adapter"]).Get("exe") as string == ScenePreflight.Probe)
            .SelectMany(c => Py.L(c["arms"]).Select(Py.D)).Where(x => x["status"] as string == "planned").Select(x => Py.S(x["armId"]))
            .ToHashSet(StringComparer.Ordinal);
        var (preflightRefusals, preflightNotes) = ScenePreflight.Run(arms, options, harness, sceneArms, opted.Contains(LiveCheckId));
        if (preflightRefusals.Count > 0)
            throw new Refusal("overlapprobe's scene preflight, the check every scene capture runs, refused this plan; "
                              + "nothing was planned:\n   - " + string.Join("\n   - ", preflightRefusals));

        // the identity of every program this plan can launch, frozen with it
        var programs = new PyDict();
        try
        {
            var armKinds = arms.ToDictionary(x => Py.S(x["armId"]), x => Py.S(x["kind"]), StringComparer.Ordinal);
            foreach (string pid in PyOrder.SortedByCodePoint(PlanIdentity.Launchable(checks, armKinds)))
                programs[pid] = PlanIdentity.Measure(options, harness, pid);
        }
        catch (Unreadable ex)
        {
            throw new Refusal($"tool-unreadable: {ex.FilePath} could not be read ({ex.Error.Message}), so this plan cannot record which program "
                              + "would run. Make the file readable and plan again.");
        }
        string controllerDigest = Py.S(Py.D(programs[PlanIdentity.Controller])["digest"]);

        var (tableRecord, tableBytes, tableRefusal) = PrimitiveTable.Read(a.PrimitiveSolidity);
        if (tableRefusal != null) throw new Refusal(tableRefusal);
        object? targets = scopeOr.ContainsKey("targets") ? scopeOr["targets"] : new List<object?>();
        var (populationBlock, populationText) = PlanPopulation.Plan(checks, arms, targets, population, instance);
        if (!string.IsNullOrEmpty(a.PopulationFrom))
        {
            var (block, text, why) = PlanPopulation.Adopt(a.PopulationFrom, populationBlock, populationText, population);
            if (block == null) throw new Refusal(why!);
            (populationBlock, populationText) = (block, text);
        }
        if (Py.Truthy(populationBlock.Get("scoped")))
            foreach (var c in checks)
            {
                var ad = Py.D(c["adapter"]);
                if ((PyValue.Eq(ad.Get("kind"), "ordersweep-probe") && PyValue.In(ad.Get("exe"), Py.L(populationBlock["scopedProbes"])))
                    || (PyValue.Eq(ad.Get("kind"), "probe-emit") && PyValue.In(c["checkId"], Py.ListOrEmpty(populationBlock.Get("scopedChecks")))))
                    ad["population"] = populationBlock["identity"];   // what the scoped emit must carry, for the receipt validator
            }
        else if (Py.ListOrEmpty(populationBlock.Get("meshFolders")) is { Count: > 0 } meshFolders)
            foreach (var c in checks)
            {
                var ad = Py.D(c["adapter"]);
                // a whole-order emit that credits the delta's folders carries them in its population (HarnessShared.PopulationRules.MeshIdentity)
                if (PyValue.Eq(ad.Get("kind"), "ordersweep-probe") && ad.Get("exe") is string exe && HarnessShared.PopulationRules.MeshProbes.Contains(exe))
                    ad["population"] = HarnessShared.PopulationRules.MeshIdentity(population, meshFolders.Select(PyText.Str));
            }
        if (meshReach != null) CheckReachFolders(meshReach, populationBlock, checks);
        var censusChecks = PyOrder.SortedByCodePoint(checks.Where(c => PyValue.Eq(Py.DictOrEmpty(c["inputs"]).Get("states"), "selected")).Select(c => Py.S(c["checkId"])));
        var statesNotExplored = new List<object?>();
        foreach (var c in checks.Where(c => PyValue.Eq(Py.DictOrEmpty(c["inputs"]).Get("states"), "all")))
            statesNotExplored.Add(new PyDict
            {
                ["checkId"] = c["checkId"], ["requires"] = "all", ["selectedStates"] = Py.Objs(states),
                ["note"] = "states beyond the selected ones were not enumerated: this plan carries no "
                           + "state census, so what was not explored is unknown in extent, not empty",
            });

        var searchRecord = SearchRecord(cdir);
        if (Py.Truthy(searchRecord.Get("absent"))) Report.Line($"   search: {searchRecord["absent"]}");

        var statesBlock = new PyDict { ["selected"] = Py.Objs(states), ["depth"] = depth, ["notExplored"] = statesNotExplored };
        if (depth == "deep")
            statesBlock["fromCensus"] = new PyDict
            {
                ["generator"] = "runcontrol", ["sha256"] = controllerDigest, ["checks"] = Py.Objs(censusChecks),
                ["note"] = "each arm's states are generated at run time from that arm's own "
                           + "accepted state.switch-census output; the list, its hash and what it "
                           + "could not express are recorded beside the check's output",
            };
        var decisions = new PyDict
        {
            ["select"] = selected != null ? Py.Objs(PyOrder.SortedByCodePoint(selected)) : null,
            ["selectRaw"] = selectRaw != null ? Py.Objs(selectRaw) : null,
            ["selectImplied"] = implied,
            ["exclude"] = excluded,
        };
        if (opted.Count > 0) { decisions["optIn"] = Py.Objs(opted); decisions["optInSource"] = optInSource; }
        decisions["settings"] = settings.Record().Update(new PyDict { ["path"] = GivenPath(settings.FilePath) });
        decisions["touched"] = touched;
        decisions["note"] = "--select narrows what RUNS, never what is PLANNED: a check that was not selected "
                            + "is skipped-user and stays visible, and a required one keeps the evaluation "
                            + "incomplete. An exe name in --select expands to every check that runs it.";

        var plan = new PyDict
        {
            ["schemaVersion"] = (long)SchemaVersion,
            ["planId"] = null,
            ["requestProviders"] = RequestProviders,
            ["createdUtc"] = PyText.UtcNowZ(),
            ["registry"] = new PyDict { ["path"] = registryPath, ["registryVersion"] = PyValue.Whole(PyValue.Int(regDoc.ContainsKey("registryVersion") ? regDoc["registryVersion"] : 0L)), ["sha256"] = regSha },
            ["tools"] = tools,
            ["toolIdentity"] = new PyDict { ["contract"] = (long)PlanIdentity.Contract, ["rule"] = PlanIdentity.Rule, ["programs"] = programs },
            // the controller that made the plan, by its package digest; recorded, not compared
            ["runner"] = new PyDict { ["runcontrol"] = controllerDigest },
            ["targets"] = targets,
            ["scope"] = new PyDict
            {
                ["path"] = scopePath, ["sha256"] = scopeSha, ["complete"] = scopeOr.Get("complete"),
                ["unmeasured"] = scopeOr.ContainsKey("unmeasured") ? scopeOr["unmeasured"] : new List<object?>(),
                ["recordTypes"] = Py.Objs(Py.Truthy(scopeOr.Get("recordTypes")) ? PyValue.Dict(scopeOr["recordTypes"]).Keys : []),
                ["walkFailures"] = scopeOr.ContainsKey("walkFailures") ? scopeOr["walkFailures"] : new List<object?>(),
            },
            ["configuration"] = new PyDict
            {
                ["fingerprint"] = PlanArms.Either(cfg, "fingerprint", "Fingerprint"), ["source"] = PlanArms.Either(cfg, "source", "Source"),
                ["population"] = population, ["plugins"] = scopePlugins.Select(kv => kv.Value).ToList(),
                ["providers"] = scopeProviders.Cast<object?>().ToList(), ["overlays"] = scopeOverlays.Cast<object?>().ToList(),
            },
            ["candidatesDir"] = cdir,
            // the candidate search the plan read its budget from: frozen by hash, or recorded absent with the reason
            ["search"] = searchRecord,
            ["arms"] = arms.Cast<object?>().ToList(),
        };
        if (reviewArm != null) plan["reviewArm"] = reviewArm;
        if (meshReach != null)
            plan["meshReach"] = new PyDict
            {
                ["file"] = reachPath, ["sha256"] = Hashing.Sha256FileOrNull(reachPath!), ["complete"] = meshReach.Get("complete"),
                ["credited"] = meshReach.Get("credited"), ["folders"] = ReachFolders(meshReach).Cast<object?>().ToList(),
            };
        plan.Update(new PyDict
        {
            ["states"] = statesBlock,
            ["checks"] = checks.Cast<object?>().ToList(),
            ["options"] = options,
            ["ordersweepProbes"] = Py.Objs(PyOrder.SortedByCodePoint(ordersweepProbes.Distinct())),
            ["population"] = populationBlock,
            ["inputs"] = new PyDict { ["primitiveSolidity"] = tableRecord },
            ["budgets"] = new PyDict
            {
                ["minutes"] = budgetMinutes, ["maxCandidates"] = a.MaxCandidates,
                ["candidatesHeldBack"] = new PyDict
                {
                    ["byMaxCandidates"] = Py.Objs(PyOrder.SortedByCodePoint(heldBack)), ["count"] = (long)heldBack.Count, ["fromManifestBudget"] = budgetBlock,
                },
                ["statesNotExplored"] = statesNotExplored,
            },
            ["explicitDecisions"] = decisions,
        });
        plan["planId"] = Hashing.Sha256Text(PyJson.Canonical(new PyDict(plan.Where(kv => kv.Key != "planId"))))[..12];

        Directory.CreateDirectory(run);
        if (populationText != null) PyJson.WritePopulationFile(PyPath.Join(run, Py.S(populationBlock["file"])), populationText);
        if (tableBytes != null)
        {
            string copy = PyPath.Join(run, PrimitiveTable.FileName);
            Directory.CreateDirectory(Path.GetDirectoryName(copy)!);
            File.WriteAllBytes(copy, tableBytes);
        }
        SetRunStore(run);
        string planFile = PyPath.Join(run, "plan.json");
        using (var record = RunLog.Begin("evalrun-plan", new RunOptions { StageSource = StageSource.Counted }))
        {
            record.Feature("checks", checks.Count, "checks");
            record.Feature("arms", arms.Count, "arms");
            record.Feature("probes", Py.L(plan["ordersweepProbes"]).Count, "probes");
            record.Stage("plan", total: 1, unit: "plans");
            PyJson.WriteEvalFile(planFile, plan);
            File.WriteAllText(PyPath.Join(run, "plan.sha256"), Hashing.Sha256File(planFile) + Environment.NewLine, new System.Text.UTF8Encoding(false));
            record.Advance();
        }

        PyJson.WriteEvalFile(PyPath.Join(run, "ledger.json"), new PyDict
        {
            ["schemaVersion"] = (long)SchemaVersion, ["planId"] = plan["planId"], ["registrySha256"] = regSha,
            ["runIds"] = new List<object?>(), ["entries"] = new PyDict(),
        });
        string journal = PyPath.Join(run, "journal.jsonl");
        if (File.Exists(journal)) File.SetLastWriteTimeUtc(journal, DateTime.UtcNow);
        else File.WriteAllBytes(journal, []);

        Report.Line($"plan {plan["planId"]}: {checks.Count} checks, {arms.Count} arms, states {string.Join(",", states)}, depth {depth}");
        foreach (string s in settings.Sentences()) Report.Line(s);
        if (Py.Truthy(tableRecord!.Get("given")))
            Report.Line($"primitive table: {tableRecord["path"]} ({tableRecord["source"]}, sha256 {Py.S(tableRecord["sha256"])[..12]}), "
                        + $"frozen as {PrimitiveTable.FileName}; stateprobe decides collision-marker primitives by it");
        else
            Report.Line($"no primitive table given: {PrimitiveTable.NoneGiven}, so stateprobe leaves every collision-marker primitive "
                        + "unjudged and its emit says so");
        foreach (string line in preflightNotes) Report.Line(line);
        foreach (var (pid, rec) in programs)
        {
            object? state = Py.D(rec).Get("state");
            if (PyValue.Eq(state, "absent"))
                Report.Line($"   {PlanIdentity.ProgramLabel(pid)}: no build at {PyText.Str(Py.D(rec).Get("locator"))}; its checks will be blocked(tool-missing)");
            else if (PyValue.Eq(state, "unverifiable"))
                Report.Line($"   {PlanIdentity.ProgramLabel(pid)}: its --probe-cmd names no file whose content can be checked; run and "
                            + "resume will refuse it (tool-unverifiable)");
        }
        if (PyValue.Eq(liveChoice, "ask") && !opted.Contains(LiveCheckId))
            Report.Line("The live switch check is set to ask each evaluation and was not opted in (`--opt-in " + LiveCheckId + "` opts in).");
        foreach (var c in checks.Where(c => c["status"] as string != "planned"))
            Report.Line($"   {Py.S(c["checkId"]),-38} {c["status"]}");
        if (unknownApplicability.Count > 0)
            Report.Line($"   {unknownApplicability.Count} check(s) of unknown applicability — they keep the "
                        + "evaluation incomplete until measured");
        Report.Line($"written: {planFile}");
        return Exit.Ok;
    }

    // Path(p).stem: the name without its last suffix.
    static string Stem(string path)
    {
        string name = PyPath.Name(path);
        return name[..(name.Length - PyPath.Suffix(path).Length)];
    }
}
