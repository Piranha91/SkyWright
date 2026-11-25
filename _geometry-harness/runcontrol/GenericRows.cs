namespace RunControl;

/// <summary>One row of a check whose probe is launched as a child (evalrun.py's run_generic): scopeprobe,
/// probe-emit, probe-json, probe-target, adviceprobe, patchprobe. The probe's flags come from the registry as data,
/// with placeholders filled from named sources; a placeholder whose source is absent blocks the row by name.</summary>
public static class GenericRows
{
    /// <summary>Kinds that take --instance, --profile and --game on the command line when the registry does not say.</summary>
    public static readonly HashSet<string> CommonFlagKinds = new(StringComparer.Ordinal)
        { "scopeprobe", "ordersweep-probe", "probe-emit", "probe-json" };

    public const string PairSourceNone = "no geometry check ran on the review arm, so there is no clash to take a plugin pair from; select "
                                         + "overlap, contact or navmesh ownership with patch discovery.";

    public static bool WantsCommonFlags(PyDict adapter)
    {
        object? declared = adapter.Get("commonFlags");
        if (declared != null) return PyValue.Truthy(declared);
        return adapter.Get("kind") is string kind && CommonFlagKinds.Contains(kind);
    }

    /// <summary>The world states a check is asked to evaluate: every selected state other than the baseline.</summary>
    public static List<object?> StatesFor(PyDict check, PyDict plan)
    {
        if (!Ev.Eq(Ev.OrEmpty(check.Get("inputs")).Get("states"), "selected")) return [];
        return Ev.Items(PyValue.Dict(plan["states"])["selected"]).Where(s => PyText.Lower(Ev.Str(s)) != "baseline").ToList();
    }

    /// <summary>The first target's Nexus id the scope records, or null.</summary>
    public static object? PlanNexusId(PyDict plan)
    {
        foreach (var t in Ev.Items(plan.Get("targets")).OfType<PyDict>())
        {
            object? nid = Ev.OrEmpty(t.Get("footprint")).Get("nexusId");
            if (!PyValue.Truthy(nid)) nid = t.Get("nexusId");
            if (PyValue.Truthy(nid)) return nid;
        }
        return null;
    }

    /// <summary>adviceprobe's --with list: the Nexus id of every instance mod folder the plan's configuration loads a
    /// plugin from, other than a target's own folder and the target's own id, in number order.</summary>
    public static List<long> AdviceNeighbourIds(PyDict plan)
    {
        object? inst = Ev.OrEmpty(plan.Get("options")).Get("instance");
        if (!PyValue.Truthy(inst)) return [];
        string instance = Ev.Str(inst);
        string mods = PyPath.NormCase(Path.GetFullPath(Path.Combine(instance, "mods")));
        var own = Ev.Items(plan.Get("targets")).OfType<PyDict>()
            .Select(t => PyText.Lower(PyValue.Truthy(t.Get("provider")) ? Ev.Str(t["provider"]) : "")).ToHashSet(StringComparer.Ordinal);
        object? nid = PlanNexusId(plan);
        string nidText = PyValue.Truthy(nid) ? Ev.Str(nid) : "";
        long? target = PyValue.IsDigit(nidText) && PyValue.IntText(nidText) is { } n ? (long)n : null;
        var ids = new SortedSet<long>();
        foreach (var p in Ev.Items(Ev.OrEmpty(plan.Get("configuration")).Get("plugins")))
        {
            object? path = (p as PyDict)?.Get("path");
            if (!PyValue.Truthy(path)) continue;
            string abs = Path.GetFullPath(Ev.Str(path));
            string full = PyPath.NormCase(abs);
            // only a plugin at the root of an instance mod folder
            if (!full.StartsWith(mods + "\\", StringComparison.Ordinal) || full[(mods.Length + 1)..].Split('\\').Length != 2) continue;
            string folder = PyPath.Name(Path.GetDirectoryName(abs)!);
            if (own.Contains(PyText.Lower(folder))) continue;
            int? mid = ModNexus.ModNexusId(PyPath.Join(instance, "mods", folder));
            if (mid is { } id && id != target) ids.Add(id);
        }
        return ids.ToList();
    }

    static List<string> TargetNames(PyDict plan) =>
        Ev.Items(plan.Get("targets")).OfType<PyDict>().Where(t => PyValue.Truthy(t.Get("name"))).Select(t => Ev.Str(t["name"])).ToList();

    /// <summary>The plugin names (lower case) an arm's frozen request loads.</summary>
    public static HashSet<string> ArmRequestedPlugins(PyDict arm)
    {
        if (Ev.OrEmpty(arm.Get("requested")).Get("plugins") is List<object?> rows)
            return rows.OfType<PyDict>().Select(p => PyText.Lower(PyValue.Truthy(p.Get("name")) ? Ev.Str(p["name"]) : "")).ToHashSet(StringComparer.Ordinal);
        return Ev.Items(arm.Get("plugins")).Select(p => PyText.Lower(Ev.Str(p))).ToHashSet(StringComparer.Ordinal);
    }

    /// <summary>The plugin names an arm's frozen request loads, in its order.</summary>
    static List<string> ArmPluginOrder(PyDict arm) =>
        Ev.OrEmpty(arm.Get("requested")).Get("plugins") is List<object?> rows
            ? rows.OfType<PyDict>().Where(p => PyValue.Truthy(p.Get("name"))).Select(p => Ev.Str(p["name"])).ToList()
            : Ev.Items(arm.Get("plugins")).Select(Ev.Str).ToList();

    /// <summary>The command line and outputs of one row, or the row accounted (blocked, not applicable, skipped)
    /// when it cannot be built. `launch` receives what RunChild takes.</summary>
    public static void Run(Executor ex, PyDict check, PyDict arm) => Run(ex, check, arm, null);

    /// <summary>As above, with the caller's own argument placeholders beside the registry's (runcontrol's own
    /// evaluation passes none).</summary>
    public static void Run(Executor ex, PyDict check, PyDict arm, IReadOnlyDictionary<string, Placeholder>? placeholders) =>
        Build(ex, check, arm, (argv, outputs, states) => ex.RunChild(check, arm, null, argv, outputs, states: states), placeholders);

    /// <summary>A caller's argument placeholder: it appends its tokens to <c>args</c> (the arguments so far, whose last
    /// may be the flag it repeats) and returns null, or returns the status and the sentence the row is accounted
    /// with when it cannot be filled.</summary>
    public delegate (string status, string why)? Placeholder(Executor ex, PyDict plan, PyDict check, PyDict arm, List<string> args);

    /// <summary>runcontrol's own placeholders beyond the plain substitutions; a caller's entry of the same token wins.</summary>
    public static readonly IReadOnlyDictionary<string, Placeholder> OwnPlaceholders = new Dictionary<string, Placeholder>(StringComparer.Ordinal)
    {
        ["{nexusId}"] = NexusIdPlaceholder,
        ["{pair}"] = PairPlaceholder,
    };

    /// <summary>runcontrol's own rules over a built command line (each may add to it, or refuse the row with a status
    /// and a sentence), run after the common flags and before the outputs are named.</summary>
    public static readonly IReadOnlyList<Placeholder> OwnArgvRules = [AdviceArgv];

    static (string status, string why)? NexusIdPlaceholder(Executor ex, PyDict plan, PyDict check, PyDict arm, List<string> args)
    {
        object? nid = PlanNexusId(plan);
        if (!PyValue.Truthy(nid))
            return ("blocked(no-nexus-id)",
                "the scope records no Nexus id for any target (the mod folder's meta.ini carries none), so there is no page to read offline");
        args.Add(Ev.Str(nid));
        return null;
    }

    static (string status, string why)? PairPlaceholder(Executor ex, PyDict plan, PyDict check, PyDict arm, List<string> args)
    {
        // the reference arm runs once with the plan-wide set; another arm runs with the moves that arm makes
        object? review = plan.Get("reviewArm");
        bool reference = Ev.Eq(arm["armId"], review) || (!PyValue.Truthy(review) && Ev.Eq(arm["kind"], "baseline"));
        var pairs = reference ? Discovery.AllPlanPairs(ex.Run, plan) : Discovery.TargetPairs(plan, arm);
        if (pairs.Count == 0 && !reference) pairs = Discovery.AllPlanPairs(ex.Run, plan);
        if (pairs.Count == 0)
            return ("blocked(no-pair-source)",
                PyValue.Truthy(review) ? PairSourceNone
                    : "the candidate manifest records no move involving a target plugin, so there is no plugin pair for this check to verify");
        string flag = "--pair";
        if (args.Count > 0 && args[^1].StartsWith("--", StringComparison.Ordinal))
        {
            flag = args[^1];
            args.RemoveAt(args.Count - 1);
        }
        foreach (var (a, b) in pairs) args.AddRange([flag, a, b]);
        return null;
    }

    /// <summary>adviceprobe's command line: offline only, and the evaluated mod's neighbours by Nexus id.</summary>
    static (string status, string why)? AdviceArgv(Executor ex, PyDict plan, PyDict check, PyDict arm, List<string> argv)
    {
        if (!Ev.Eq(Ev.OrEmpty(check.Get("adapter")).Get("kind"), "adviceprobe")) return null;
        int at = argv.IndexOf("--refresh");
        if (at >= 0)
        {
            string word = at + 1 < argv.Count ? argv[at + 1] : "";
            if (word != "offline")
                return ("blocked(network-refused)",
                    $"the registry adapter asks for --refresh {PyText.Repr(word)}; evalrun never fetches. "
                    + "Use --refresh offline, or run adviceprobe yourself and supply its output.");
        }
        else if (!argv.Contains("--offline")) argv.Add("--offline");
        if (!argv.Contains("--with"))
            // the evaluated mod's neighbours: a clash stated on their page is author advice for the pair
            foreach (long id in AdviceNeighbourIds(plan)) argv.AddRange(["--with", id.ToString(System.Globalization.CultureInfo.InvariantCulture)]);
        return null;
    }

    public static void Build(Executor ex, PyDict check, PyDict arm, Action<List<string>, List<string>, object?> launch,
        IReadOnlyDictionary<string, Placeholder>? placeholders = null)
    {
        var adapter = PyValue.Dict(check["adapter"]);
        object? kind = adapter.Get("kind");
        var plan = ex.Plan;
        if (Prerequisites.CandidateStale(arm) is { } stale)
        {
            // the frozen candidate hash gates the launch, not just the receipt
            ex.Account(check, arm, "blocked(candidate-changed)", "stale-inputs", stale);
            return;
        }
        var prog = ex.ExeArgv(adapter);
        if (prog == null)
        {
            ex.Account(check, arm, "blocked(tool-missing)", null,
                $"no Release binary for '{Ev.Str(adapter.Get("exe"))}' under {ex.Harness} - build it, or stand one in with --probe-cmd");
            return;
        }

        var states = StatesFor(check, plan);
        (string path, PyDict record)? statesFile = null;
        bool selected = Ev.Eq(Ev.OrEmpty(check.Get("inputs")).Get("states"), "selected");
        if (selected && Ev.Eq(PyValue.Dict(plan["states"]).Get("depth"), "deep"))
        {
            statesFile = DeepStates.For(ex, check, arm);
            if (statesFile == null) return;
        }
        else if (selected && states.Count == 0)
        {
            ex.Account(check, arm, "skipped-user", null,
                "this check evaluates a world state other than the baseline, and the plan's "
                + "--states selected none: " + string.Join(", ", PyValue.Iter(PyValue.Dict(plan["states"])["selected"]).Select(s => (string)s!))
                + ". An unexplored state is recorded, never assumed equal to the baseline.");
            return;
        }

        string armId = Ev.Str(arm["armId"]);
        string outDir = PyPath.Join(ex.Run, "outputs", Ev.Str(check["checkId"]));
        Directory.CreateDirectory(outDir);
        string emit = PyPath.Join(outDir, $"{armId}.json");

        var raw = Ev.Items(adapter.Get("args")).Select(a => (string)a!).ToList();
        var args = new List<string>();
        var fills = new Dictionary<string, Placeholder>(OwnPlaceholders, StringComparer.Ordinal);
        foreach (var (k, v) in placeholders ?? new Dictionary<string, Placeholder>()) fills[k] = v;
        foreach (string token in raw)
        {
            if (fills.TryGetValue(token, out var fill))
            {
                if (fill(ex, plan, check, arm, args) is { } unfilled)
                {
                    ex.Account(check, arm, unfilled.status, null, unfilled.why);
                    return;
                }
                continue;
            }
            if (token == "{target}")
            {
                var names = TargetNames(plan);
                if (names.Count == 0)
                {
                    ex.Account(check, arm, "blocked(no-target)", null, "the plan names no target plugin, so this probe cannot be told what to read");
                    return;
                }
                if (Ev.Eq(kind, "probe-target"))
                {
                    // the probe reads this arm's candidate, so it is told only the targets that candidate loads
                    var loads = ArmRequestedPlugins(arm);
                    names = names.Where(t => loads.Contains(PyText.Lower(t))).ToList();
                    if (names.Count == 0)
                    {
                        ex.Account(check, arm, "not-applicable", null, "this arm loads none of the plan's target plugins, so it has no target objects to judge");
                        return;
                    }
                }
                // the shape the registry wrote decides: `--target {target}` repeats with its flag, a bare `{target}` on its own
                string? flag = args.Count > 0 && args[^1].StartsWith("--", StringComparison.Ordinal) ? args[^1] : null;
                if (flag != null)
                {
                    args.RemoveAt(args.Count - 1);
                    foreach (string n in names) args.AddRange([flag, n]);
                }
                else args.AddRange(names);
                continue;
            }
            if (token == "{switchMap}")
            {
                string? path = DeepStates.AcceptedCensus(ex, arm);
                if (path == null)
                {
                    ex.Account(check, arm, "blocked(no-switch-map)", null,
                        "no accepted state.switch-census receipt on this arm, so there is no switch map for this check to read");
                    return;
                }
                args.Add(path);
                continue;
            }
            args.Add(token.Replace("{order}", Ev.Str(arm["candidatePath"])).Replace("{emit}", emit).Replace("{json}", emit)
                .Replace("{out}", outDir).Replace("{run}", ex.Run).Replace("{arm}", armId));
        }

        object? statesFlag = adapter.Get("statesFlag");
        if (statesFile is { } deep) args.AddRange([(string)adapter["statesFileFlag"]!, deep.path]);
        else if (states.Count > 0 && PyValue.Truthy(statesFlag))
            foreach (var st in states) args.AddRange([(string)statesFlag!, Ev.Str(st)]);

        var argv = new List<string>(prog);
        if (kind is "scopeprobe" or "probe-emit" or "probe-json" or "probe-target" && !args.Contains("--order") && PyValue.Truthy(arm["candidatePath"]))
            argv.AddRange(["--order", (string)arm["candidatePath"]!]);
        if (Ev.Eq(kind, "scopeprobe") && !args.Contains("--target"))
        {
            // scopeprobe reads what a target contains; the target list is the plan's, named, never guessed from the order
            var names = TargetNames(plan);
            // a selection with no plugin has no footprint to open: the row then proves this arm's order loads, on the
            // plugin the scope stage proves it on
            if (names.Count == 0 && ScopeRules.OrderProof(ArmPluginOrder(arm)) is { } last) names = [last];
            if (names.Count == 0)
            {
                ex.Account(check, arm, "blocked(no-target)", null, "the plan names no target plugin, so the footprint probe has nothing to open");
                return;
            }
            foreach (string n in names) argv.AddRange(["--target", n]);
        }
        argv.AddRange(args);
        object? outKind = PyValue.Truthy(adapter.Get("output")) ? adapter["output"] : "conflict-file";
        if (Ev.Eq(outKind, "conflict-file") && !argv.Contains("--emit")) argv.AddRange(["--emit", emit]);
        else if (Ev.Eq(outKind, "json") && !argv.Contains("--json") && !argv.Contains("--out")) argv.AddRange(["--json", emit]);
        if (WantsCommonFlags(adapter)) argv.AddRange(ex.CommonFlags());
        if (PyValue.Truthy(ex.Opts.Get("probeArgs"))) argv.AddRange(PyText.Split((string)ex.Opts["probeArgs"]!));
        var population = Ev.OrEmpty(plan.Get("population"));
        if (Ev.Eq(kind, "probe-emit") && PyValue.Truthy(population.Get("scoped"))
            && PyValue.In(check["checkId"], Ev.Items(population.Get("scopedChecks"))))
        {
            // the plan's population, by the file the plan froze: a changed or missing file blocks the check
            string pfile = PyPath.Join(ex.Run, (string)population["file"]!);
            if (!File.Exists(pfile) || !Ev.Eq(Hashing.Sha256FileOrNull(pfile), population["sha256"]))
            {
                ex.Account(check, arm, "blocked(population-changed)", "stale-inputs",
                    !File.Exists(pfile) ? $"the plan's population file {PyPath.Name(pfile)} is gone"
                        : $"the plan's population file {PyPath.Name(pfile)} no longer hashes to the plan's value");
                return;
            }
            argv.AddRange([PyValue.Truthy(adapter.Get("populationFlag")) ? (string)adapter["populationFlag"]! : "--population", pfile]);
        }
        var table = Ev.OrEmpty(Ev.OrEmpty(plan.Get("inputs")).Get("primitiveSolidity"));
        if (Ev.Eq(kind, "probe-emit") && PyValue.Truthy(adapter.Get("primitiveSolidityFlag")) && PyValue.Truthy(table.Get("given")))
        {
            // the plan's frozen copy of the measured primitive table, gated by its hash
            string tfile = PyPath.Join(ex.Run, (string)table["file"]!);
            if (!File.Exists(tfile) || !Ev.Eq(Hashing.Sha256FileOrNull(tfile), table["sha256"]))
            {
                ex.Account(check, arm, "blocked(primitive-table-changed)", "stale-inputs",
                    !File.Exists(tfile) ? $"the plan's primitive table {PyPath.Name(tfile)} is gone"
                        : $"the plan's primitive table {PyPath.Name(tfile)} no longer hashes to the plan's value; plan again");
                return;
            }
            argv.AddRange([(string)adapter["primitiveSolidityFlag"]!, tfile]);
        }

        foreach (var rule in OwnArgvRules)
            if (rule(ex, plan, check, arm, argv) is { } refused)
            {
                ex.Account(check, arm, refused.status, null, refused.why);
                return;
            }

        // where the result lands: the file the probe writes, not a file named for it
        var outputs = new List<string> { emit };
        if (Ev.Eq(outKind, "text"))
            // a text probe prints its answer: the log is the output, recorded first by RunChild; adapter.files are
            // what it writes besides, each required and fresh
            outputs = Ev.Items(adapter.Get("files")).Select(f => PyPath.Str(((string)f!).Replace("{out}", outDir).Replace("{run}", ex.Run).Replace("{arm}", armId))).ToList();
        else if (argv.Contains("--out") && Ev.Eq(outKind, "json"))
        {
            object? nid = PlanNexusId(plan);
            if (PyValue.Truthy(nid)) outputs = [PyPath.Join(outDir, $"report-{Ev.Str(nid)}.json")];
        }

        launch(argv, outputs, statesFile is { } made ? made.record : states);
    }
}
