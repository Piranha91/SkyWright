using HousecarlCore.Progress;

namespace RunControl;

/// <summary>One invocation of `run` or `resume` (evalrun.py's Executor): its deadline, the program identities it
/// checks at each launch and acceptance, the accounting receipts, and one child from launch to receipt.</summary>
public sealed class Executor
{
    public string Run { get; }
    public PyDict Plan { get; }
    public Ledger Ledger { get; }
    public bool Resume { get; }
    public PyDict Opts { get; }
    public string Harness { get; }
    public Validator Validator { get; }
    public string Logs { get; }
    public string? ParentRunId { get; set; }

    /// <summary>When this invocation's budget started, on the runner's clock.</summary>
    public double T0 { get; }
    public object? BudgetMinutes { get; }
    public double? Deadline { get; }
    public int DeadlineStops { get; set; }
    public int Children { get; set; }
    /// <summary>armId -> the plan's arm.</summary>
    public PyDict Arms { get; } = new();
    public string OptionsSha { get; }

    /// <summary>The plan's record of each program, what the preflight measured, the latest launch measurement, the
    /// programs found changed at an acceptance check, and how the identity bound into the next receipt was checked.</summary>
    public PyDict Programs { get; }
    public PyDict Preflight { get; set; } = new();
    public PyDict Measured { get; } = new();
    public SortedDictionary<string, string> Changed { get; } = new(PyOrder.CodePoint);
    public List<object?> Checked { get; set; } = ["preflight"];

    /// <summary>Test seam: runs one child job in place of the runner.</summary>
    public Func<ChildJob, ChildResult>? RunJob { get; set; }

    /// <summary>Test seam: a program's identity now, in place of measuring its files.</summary>
    public Func<string, PyDict>? MeasureProgram { get; set; }

    PyDict MeasureNow(string pid) => MeasureProgram != null ? MeasureProgram(pid) : PlanIdentity.Measure(Opts, Harness, pid);

    /// <summary>One child through the runner, under this invocation's deadline, its log in logs/ID.txt.</summary>
    public ChildResult Launch(ChildJob job) => RunJob != null ? RunJob(job) : new ChildRunner("evalrun-check", Logs, Deadline).Run(job, echo: true);

    public Executor(string run, PyDict plan, Ledger ledger, bool resume, object? budgetMinutes = null)
    {
        Run = PyPath.Str(run);
        Plan = plan;
        Ledger = ledger;
        Resume = resume;
        Opts = PyValue.Dict(plan["options"]);
        Harness = PyPath.Str(Ev.Str(Opts["harness"]));
        Validator = new Validator(Run, plan);
        Logs = PyPath.Join(Run, "logs");
        // the budget is a deadline for this invocation, measured from here on the clock the runner stops children by
        T0 = ChildRunner.Now();
        BudgetMinutes = budgetMinutes ?? PyValue.Dict(plan["budgets"]).Get("minutes");
        double seconds = PyValue.Truthy(BudgetMinutes) ? Py.Float(BudgetMinutes) * 60 : 0;
        Deadline = seconds != 0 ? T0 + seconds : null;
        foreach (var a in PyValue.Iter(plan["arms"]).Select(PyValue.Dict)) Arms[Ev.Str(a["armId"])] = a;
        OptionsSha = Hashing.Sha256Text(PyJson.Canonical(Opts));
        Programs = Ev.OrEmpty(Ev.OrEmpty(plan.Get("toolIdentity")).Get("programs"));
    }

    /// <summary>The plan's arm of an id.</summary>
    public PyDict Arm(object? armId) => PyValue.Dict(Arms[Ev.Str(armId)]);

    // ---- program identity at the launch boundary and at acceptance

    /// <summary>Before a child starts: every program it uses is still the plan's, or nothing is launched and the run stops.</summary>
    public void CheckLaunch(IEnumerable<string> pids, string what)
    {
        foreach (string pid in pids)
        {
            PyDict now;
            try { now = MeasureNow(pid); }
            catch (Unreadable ex)
            {
                throw new ToolChanged($"{ToolPreflight.UnreadableSentence(pid, ex)}, before {what} was launched. {ToolPreflight.IdentityAction}");
            }
            if (ToolPreflight.Drift(pid, Programs.Get(pid), now) is { } why)
                throw new ToolChanged($"{why}, before {what} was launched. {ToolPreflight.IdentityAction}");
            Measured[pid] = now;
        }
    }

    /// <summary>After a child returns, before its result can be accepted: a program that changed while it ran is
    /// recorded, and every receipt resting on it is rejected(tool-changed) by Finish.</summary>
    public void CheckAccept(IEnumerable<string> pids, string what)
    {
        foreach (string pid in pids)
        {
            PyDict now;
            try { now = MeasureNow(pid); }
            catch (Unreadable ex)
            {
                Changed[pid] = $"{ToolPreflight.UnreadableSentence(pid, ex)}, after {what} ran";
                continue;
            }
            if (ToolPreflight.Drift(pid, Programs.Get(pid), now) is { } why) Changed[pid] = $"{why}, while {what} ran";
        }
    }

    /// <summary>Ends the invocation when a program changed while a child ran.</summary>
    public void StopIfChanged()
    {
        if (Changed.Count > 0) throw new ToolChanged(string.Join("; ", Changed.Values) + $". {ToolPreflight.IdentityAction}");
    }

    /// <summary>The identity a receipt was accepted under, per role, as measured (never copied from the plan).</summary>
    public PyDict Bind(PyDict roles)
    {
        var bound = new PyDict();
        foreach (var (role, pidRaw) in roles)
        {
            string pid = Ev.Str(pidRaw);
            var src = PyValue.Truthy(Measured.Get(pid)) ? PyValue.Dict(Measured[pid]) : Ev.OrEmpty(Preflight.Get(pid));
            bound[role] = new PyDict { ["program"] = pid, ["kind"] = src.Get("kind"), ["digest"] = src.Get("digest"), ["checked"] = new List<object?>(Checked) };
        }
        return bound;
    }

    // ---- budgeting

    /// <summary>Checked before a child launches, so an expired budget starts nothing.</summary>
    public bool BudgetLeft() => Deadline == null || ChildRunner.Now() < Deadline;

    public string BudgetSentence() =>
        $"this run's budget of {Ev.Str(BudgetMinutes)} minute(s) was reached "
        + $"{PyText.Fixed((ChildRunner.Now() - T0) / 60, 1)} minutes into this run, before this check "
        + "launched: it was planned and not evaluated";

    public string StopSentence(ChildResult res, string what) =>
        $"this run's budget of {Ev.Str(BudgetMinutes)} minute(s) was reached while {what} "
        + $"was running: its process tree was stopped at the deadline after {PyText.Fixed(res.Seconds, 1)}s "
        + $"({(res.TreeStopped == true ? "confirmed gone" : "NOT confirmed gone")}); nothing it "
        + "wrote is accepted, and the check was planned and not evaluated";

    /// <summary>A deadline outcome for one (check, arm, part): stopped(deadline) when a child was stopped mid-run,
    /// skipped-budget when the runner refused to launch it, blocked(uncontained) when it could not be contained.
    /// Never accepted, never read.</summary>
    public void AccountStop(PyDict check, PyDict arm, string? part, ChildResult res, string what, string? log = null)
    {
        if (res.Stopped == "before-launch")
        {
            Account(check, arm, "skipped-budget", "budget-exhausted", BudgetSentence(), part: part);
            return;
        }
        if (res.Stopped == "uncontained")
        {
            // not the budget: the runner could not contain the child's process tree, and ended it before it ran
            Account(check, arm, "blocked(uncontained)", null,
                $"{what} never ran: its process could not be contained, so the deadline could not be "
                + "enforced on its tree, and it was terminated before it started "
                + $"({(string.IsNullOrEmpty(res.Refused) ? "no reason given" : res.Refused)}); nothing was measured",
                part: part, runId: res.RunId, log: log);
            return;
        }
        // the receipt's exit code stays null: a stopped child returned nothing; the launched process's own code goes in the detail
        string detail = StopSentence(res, what);
        if (res.LaunchedExitCodeUnsigned is { } own) detail += $" (the launched process itself had exited with code {own})";
        Account(check, arm, "stopped(deadline)", "budget-exhausted", detail, part: part, runId: res.RunId, log: log);
    }

    // ---- the common consumed block

    /// <summary>Whether the plan's scope was measured on this arm's candidate: the scope names its source as
    /// candidate:FILE; one that names no candidate is the baseline's.</summary>
    public static bool ScopeMeasuredOn(PyDict plan, PyDict arm)
    {
        object? source = Ev.OrEmpty(plan.Get("configuration")).Get("source");
        string src = PyValue.Truthy(source) ? Ev.Str(source) : "";
        if (!src.StartsWith("candidate:", StringComparison.Ordinal)) return true;
        return src.Split(':', 2)[1] == (PyValue.Truthy(arm.Get("candidateFile")) ? Ev.Str(arm["candidateFile"]) : "");
    }

    /// <summary>What a child read, as strongly as the child stated it: the emit's own identity; on the baseline arm
    /// the scope's configuration when the scope was measured on it; else this arm's frozen request, with no fingerprint.</summary>
    public PyDict ConsumedFor(PyDict arm, PyDict adapter, string? baseEmit = null, PyDict? identity = null, object? inputs = null,
        PyDict? programs = null, object? tool = null)
    {
        object? exe = adapter.Get("exe");
        tool ??= PyValue.Truthy(exe) ? PyValue.Dict(Plan["tools"]).Get(Ev.Str(exe)) : null;
        PyDict cfg;
        string source;
        if (identity != null)
        {
            cfg = new PyDict(identity);
            source = "emit";
        }
        else if (Ev.Eq(arm.Get("kind"), "baseline") && ScopeMeasuredOn(Plan, arm))
        {
            var pc = PyValue.Dict(Plan["configuration"]);
            cfg = new PyDict
            {
                ["fingerprint"] = pc.Get("fingerprint"), ["source"] = pc.Get("source"),
                ["plugins"] = Ev.G(pc, "plugins", new List<object?>()), ["providers"] = Ev.G(pc, "providers", new List<object?>()),
                ["overlays"] = Ev.G(pc, "overlays", new List<object?>()), ["population"] = pc.Get("population"),
                ["state"] = null, ["limits"] = new List<object?>(),
            };
            source = "scope";
        }
        else
        {
            var req = Ev.OrEmpty(arm.Get("requested"));
            cfg = new PyDict
            {
                ["fingerprint"] = null,
                ["fingerprintNote"] = "the child stated no identity; reuse re-checks the plan's stated plugin hashes on disk",
                ["source"] = $"candidate:{Ev.Str(arm.Get("candidateFile"))}",
                // the request's rows with the hashSource that says where each hash came from
                ["plugins"] = Ev.Items(req.Get("plugins")).Select(PyValue.Dict).Select(p => (object?)new PyDict
                {
                    ["name"] = p.Get("name"), ["path"] = p.Get("path"), ["sha256"] = p.Get("sha256"), ["hashSource"] = p.Get("hashSource"),
                }).ToList(),
                ["providers"] = Ev.Items(req.Get("providers")).Select(v => (object?)new PyDict(PyValue.Dict(v))).ToList(),
                ["overlays"] = Ev.Items(req.Get("overlays")).Select(o => (object?)new PyDict(PyValue.Dict(o))).ToList(),
                ["population"] = PyValue.Truthy(req.Get("population")) ? req["population"] : PyValue.Dict(Plan["configuration"]).Get("population"),
                ["state"] = null, ["limits"] = new List<object?>(),
            };
            source = "plan";
        }
        if (source != "emit")
        {
            // only an unstated row is a reuse gap: a row the plan resolved states a path and a hash
            var unstated = Ev.Items(Ev.OrEmpty(arm.Get("requested")).Get("plugins")).Select(PyValue.Dict)
                .Where(p => Ev.Eq(p.Get("hashSource"), "unstated") || !PlanArms.IsSha256(p.Get("sha256"))).Select(p => p.Get("name")).ToList();
            if (unstated.Count > 0)
                cfg["identityLimits"] = unstated.Select(n => (object?)new PyDict
                {
                    ["plugin"] = n, ["recordedSha256"] = null,
                    ["detail"] = $"the plan states no hash for {Ev.Str(n)}, so reuse cannot re-check its bytes: "
                                 + "this plugin is a named reuse gap, not a verified input",
                }).ToList();
            if (!PyValue.Truthy(cfg.Get("plugins")))
                cfg["reuseGap"] = "scope.json states no plugin paths and hashes for this configuration, "
                                  + "so what this child consumed cannot be re-checked on disk";
        }
        var block = new PyDict
        {
            ["candidateSha256"] = arm.Get("candidateSha256"),
            ["candidatePath"] = arm.Get("candidatePath"),
            ["configuration"] = cfg,
            ["configurationSource"] = source,
            ["baseEmitSha256"] = baseEmit != null && Ev.Exists(baseEmit) ? Hashing.Sha256FileOrNull(baseEmit) : null,
            ["baseEmitPath"] = baseEmit != null ? PyPath.RelTo(Run, baseEmit) : null,
            ["registrySha256"] = PyValue.Dict(Plan["registry"])["sha256"],
            ["tool"] = tool,
            ["optionsSha256"] = OptionsSha,
            ["scopeSha256"] = PyValue.Dict(Plan["scope"])["sha256"],
        };
        // the comparison a receipt is: a diff rests on two emits, and reuse must follow both
        if (inputs != null) block["inputs"] = inputs;
        if (programs != null) block["programs"] = programs;
        return block;
    }

    // ---- the accounting receipts (no child runs)

    /// <summary>One (check, arm, part) accounted without a result: the receipt, kept when it states what the one on
    /// disk states, and the ledger transition.</summary>
    public void Account(PyDict check, PyDict arm, string status, string? cause, string detail, string? part = null,
        string? runId = null, string? log = null, object? exitCode = null, PyDict? extra = null)
    {
        var adapter = PyValue.Dict(check["adapter"]);
        var rejections = cause != null ? new List<object?> { new PyDict { ["cause"] = cause, ["detail"] = detail } } : [];
        var validation = new PyDict { ["accepted"] = false, ["rejections"] = rejections };
        var result = new PyDict
        {
            ["execution"] = status, ["detector"] = null, ["findings"] = null, ["coverage"] = new List<object?>(),
            ["strength"] = Ev.OrEmpty(check.Get("evidence")).Get("strength"), ["verdict"] = null, ["note"] = detail,
        };
        Receipts.WriteReceipt(Run, Plan, check, arm, part, adapter, null, runId, ParentRunId, null, EvalStore.UtcNow(), exitCode,
            [], log, ConsumedFor(arm, adapter), validation, result, extra, keepSame: true);
        bool stopped = status == "stopped(deadline)", uncontained = status == "blocked(uncontained)";
        Ledger.Transition(Ev.Str(check["checkId"]), Ev.Str(arm["armId"]), part, status, cause: cause, rejections: rejections,
            detail: stopped || uncontained ? detail : null,
            nextAction: stopped ? "`resume --run RUN` re-runs it under a fresh deadline"
                : uncontained ? "`resume --run RUN` re-runs it; it never ran" : null);
    }

    // ---- one child

    /// <summary>One child from launch to receipt: the program identities checked, the ledger entry set running, the
    /// child run under the deadline, its result validated and receipted. Null when the child was stopped and accounted.</summary>
    public PyDict? RunChild(PyDict check, PyDict arm, string? part, List<string> argv, List<string> outputs,
        string? baseEmit = null, string jobSuffix = "", object? states = null)
    {
        var adapter = PyValue.Dict(check["adapter"]);
        string key = Ledger.Key(check["checkId"], arm["armId"], part);
        string jobId = key + jobSuffix;
        if (jobId.Length > 100) jobId = jobId[..100];
        string what = $"check {Ev.Str(check["checkId"])} [{Ev.Str(arm["armId"])}]";
        var pids = Receipts.ReceiptRoles(new PyDict { ["adapter"] = adapter, ["part"] = part }).Select(kv => Ev.Str(kv.Value)).ToList();
        CheckLaunch(pids, what);   // before anything of this job is written
        foreach (string p in outputs) Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(p))!);
        var pre = new Dictionary<string, string?>(StringComparer.Ordinal);
        foreach (string p in outputs) pre[p] = Hashing.Sha256FileOrNull(p);
        Ledger.Transition(Ev.Str(check["checkId"]), Ev.Str(arm["armId"]), part, "running");
        string startedIso = EvalStore.UtcNow();
        double started = (DateTime.UtcNow - DateTime.UnixEpoch).TotalSeconds;
        var job = new ChildJob(jobId, argv, Env: ScenePreflight.ChildEnv(Opts));
        var res = Launch(job);
        string log = PyPath.Join(Logs, $"{jobId}.txt");
        if (!string.IsNullOrEmpty(res.Stopped))
        {
            // a child stopped at (or refused by) the deadline is never validated: what it wrote is partial
            if (res.Stopped == "deadline")
            {
                Children++;
                DeadlineStops++;
            }
            AccountStop(check, arm, part, res, what, log: Ev.Exists(log) ? log : null);
            return null;
        }
        Children++;
        string endedIso = EvalStore.UtcNow();
        CheckAccept(pids, what);
        string text = Ev.Exists(log) ? Ev.ReadText(log) : "";
        if (Ev.Eq(PyValue.Truthy(adapter.Get("output")) ? adapter["output"] : "conflict-file", "text") && Ev.Exists(log))
            outputs = [log, .. outputs];   // the log, then any adapter.files
        var v = Validator.Validate(check, arm, part, adapter, res.ExitCodeUnsigned, text, outputs, pre, started);
        Checked = ["launch", "acceptance"];
        PyDict receipt;
        try
        {
            receipt = Finish(check, arm, part, adapter, argv, res, startedIso, endedIso, outputs, log, v, baseEmit: baseEmit,
                extra: PyValue.Truthy(states) ? new PyDict { ["states"] = states } : null);
        }
        finally { Checked = ["preflight"]; }
        StopIfChanged();
        return receipt;
    }

    /// <summary>A validated result written: the receipt with what was consumed and the identities it was accepted
    /// under, and the ledger transition. A result resting on a program that changed while it ran is rejected.</summary>
    public PyDict Finish(PyDict check, PyDict arm, string? part, PyDict adapter, IReadOnlyList<string>? argv, ChildResult? res,
        string? startedIso, string? endedIso, IEnumerable<string> outputs, string? log, PyDict v, string? baseEmit = null,
        PyDict? extra = null, object? verdict = null, string? execution = null, object? inputs = null)
    {
        var roles = Receipts.ReceiptRoles(new PyDict { ["adapter"] = adapter, ["part"] = part });
        var drift = roles.Select(kv => Ev.Str(kv.Value)).Where(Changed.ContainsKey).Select(pid => Changed[pid]).ToList();
        if (drift.Count > 0 && PyValue.Truthy(v["accepted"]))
        {
            // a program this result rests on changed while it was produced: it is not accepted as the plan's build
            var rejections = new List<object?> { new PyDict { ["cause"] = "tool-changed", ["detail"] = string.Join("; ", drift) } };
            rejections.AddRange(Ev.Items(v.Get("rejections")));
            v = new PyDict(v) { ["accepted"] = false, ["rejections"] = rejections };
        }
        var rej = Ev.Items(v["rejections"]).ToList();
        string status = PyValue.Truthy(v["accepted"]) ? execution ?? "completed"
            : rej.Count > 0 ? $"rejected({Ev.Str(PyValue.Dict(rej[0])["cause"])})"
            : execution ?? "failed";
        var result = new PyDict
        {
            ["execution"] = status, ["detector"] = v["detector"], ["findings"] = v["findings"], ["coverage"] = v["coverage"],
            ["strength"] = Ev.OrEmpty(check.Get("evidence")).Get("strength"),
            ["verdict"] = verdict ?? v.Get("verdict"),
        };
        if (PyValue.Truthy(v["accepted"]) && v["detector"] == null)
            result["detectorUndetermined"] = "this adapter's output contract does not say where a finding count lives "
                + "(no adapter.findings path), so the detector result is undetermined rather than null-as-clean";
        bool launches = PyValue.Truthy(adapter.Get("exe")) && adapter.Get("kind") is string kind && Receipts.LaunchKinds.Contains(kind);
        var receipt = Receipts.WriteReceipt(Run, Plan, check, arm, part, adapter, argv, res?.RunId, ParentRunId, startedIso, endedIso,
            res?.ExitCodeUnsigned, outputs, log,
            ConsumedFor(arm, adapter, baseEmit: baseEmit, identity: v.Get("identity") as PyDict, inputs: inputs, programs: Bind(roles),
                tool: launches ? PlanIdentity.ToolRecord(Harness, Ev.Str(adapter["exe"])) : null),
            v, result, extra);
        var first = rej.Count > 0 ? PyValue.Dict(rej[0]) : null;
        Ledger.Transition(Ev.Str(check["checkId"]), Ev.Str(arm["armId"]), part, status,
            cause: first == null ? null : Ev.Str(first["cause"]), receiptId: Ev.Str(receipt["receiptId"]), detector: v["detector"] as string,
            rejections: rej, detail: first == null ? null : Ev.Str(first["detail"]),
            nextAction: first == null ? null : "re-run this check once the cause above is fixed",
            extra: new PyDict { ["derivedFrom"] = extra?.Get("derivedFrom") });
        return receipt;
    }

    // ---- argv assembly

    /// <summary>The variables every child receives: the instance, profile and game a flag-less probe reads.</summary>
    public Dictionary<string, string> ChildEnv() => ScenePreflight.ChildEnv(Opts);

    public List<string> CommonFlags() => ScenePreflight.CommonFlags(Opts);

    /// <summary>The child's program: a --probe-cmd prefix, else the probe's build; null when there is neither.</summary>
    public List<string>? ExeArgv(PyDict adapter) => PlanIdentity.ProbeArgv(Opts, Harness, adapter.Get("exe") as string);

    /// <summary>Each {name} of the substitutions replaced in each argument.</summary>
    public static List<string> Expand(IEnumerable<string> args, IEnumerable<KeyValuePair<string, string>> subs) =>
        args.Select(tok => subs.Aggregate(tok, (t, kv) => t.Replace("{" + kv.Key + "}", kv.Value))).ToList();
}
