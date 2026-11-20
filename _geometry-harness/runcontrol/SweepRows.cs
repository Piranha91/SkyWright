using System.Globalization;
using HousecarlCore.Progress;

namespace RunControl;

/// <summary>The rows of the swept checks (adapter kind ordersweep-probe), as evalrun.py settles them: one order sweep
/// child for all of them, in a launch folder of its own, its job records split into arm, reverse and diff receipts.
/// The sweep's classification and credit rules are read from what it wrote, never worked out again here.</summary>
public static class SweepRows
{
    /// <summary>The parts one swept (check, arm) settles, in the order they rest on each other.</summary>
    public static readonly string[] Parts = ["arm", "reverse", "diff"];

    /// <summary>How much of conflictdiff's own refusal a rejection quotes.</summary>
    public const int RefusalQuoteChars = 600;

    /// <summary>The plan's swept checks that are planned, in plan order: the checks one sweep child measures.</summary>
    public static List<PyDict> Planned(PyDict plan) =>
        PyValue.Iter(plan["checks"]).Select(PyValue.Dict)
            .Where(c => Ev.Eq(PyValue.Dict(c["adapter"]).Get("kind"), "ordersweep-probe") && Ev.Eq(c["status"], "planned")).ToList();

    static IEnumerable<PyDict> ArmRows(PyDict check) => PyValue.Iter(check["arms"]).Select(PyValue.Dict);

    // The parts an arm has at all: the baseline only its arm.
    static string[] AllParts(PyDict arm) => Ev.Eq(arm["kind"], "baseline") ? ["arm"] : Parts;

    static bool NoReverse(Executor ex) => PyValue.Truthy(ex.Opts.Get("noReverse"));

    static string StateOf(Dictionary<string, string> allowed, object? armId) => allowed.GetValueOrDefault(Ev.Str(armId), "ok");

    /// <summary>The parts the sweep is planned to settle for an arm: the baseline its arm; a candidate its arm, its
    /// diff and, unless --no-reverse, its reverse.</summary>
    public static string[] SweepParts(Executor ex, PyDict arm) =>
        Ev.Eq(arm["kind"], "baseline") ? ["arm"] : NoReverse(ex) ? ["arm", "diff"] : Parts;

    /// <summary>Which arms may enter the sweep: "ok", or "blocked:PREREQUISITE". A baseline whose prerequisite is not
    /// settled keeps every arm out, because every other arm is measured against it.</summary>
    public static Dictionary<string, string> ArmsAllowed(Executor ex, List<PyDict> sweepChecks)
    {
        var blocked = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var c in sweepChecks)
            foreach (var r in ArmRows(c))
                if (Prerequisites.Satisfied(ex, c, r["armId"]) is { } miss) blocked.TryAdd(Ev.Str(r["armId"]), miss);
        blocked.TryGetValue("baseline", out string? baseBlock);
        var allowed = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var c in sweepChecks)
            foreach (var r in ArmRows(c))
            {
                string aid = Ev.Str(r["armId"]);
                allowed[aid] = baseBlock != null ? $"blocked:{baseBlock}" : blocked.TryGetValue(aid, out string? own) ? $"blocked:{own}" : "ok";
            }
        return allowed;
    }

    /// <summary>True when launching the sweep could settle something: an arm the plan measures and its prerequisites
    /// allow has a planned part that is not completed or reused. A blocked baseline launches nothing.</summary>
    public static bool Needed(Executor ex, List<PyDict> sweepChecks, Dictionary<string, string> allowed)
    {
        if (StateOf(allowed, "baseline") != "ok") return false;
        var memo = new HashMemo();
        foreach (var c in sweepChecks)
            foreach (var r in ArmRows(c))
            {
                if (!Ev.Eq(r["status"], "planned") || StateOf(allowed, r["armId"]) != "ok") continue;
                var arm = ex.Arm(r["armId"]);
                if (Prerequisites.CandidateStale(arm, memo) != null) continue;   // the plan no longer describes its order: launching cannot settle it
                foreach (string part in SweepParts(ex, arm))
                    if (!EvalStore.IsTerminalOk(ex.Ledger.Entry(c["checkId"], r["armId"], part)?.Get("status"))) return true;
            }
        return false;
    }

    /// <summary>Every arm its prerequisite keeps out reads blocked(prerequisite:ID), on the arm and on each part an
    /// earlier invocation settled for it. An entry already carrying that status is left as it is.</summary>
    public static void AccountKeptOut(Executor ex, List<PyDict> sweepChecks, Dictionary<string, string> allowed)
    {
        foreach (var c in sweepChecks)
            foreach (var r in ArmRows(c))
            {
                string state = StateOf(allowed, r["armId"]);
                if (state == "ok") continue;
                string pre = state.Split(':', 2)[1];
                string status = $"blocked(prerequisite:{pre})";
                string detail = Prerequisites.BlockDetail(ex, pre, r["armId"]);
                foreach (string? part in new string?[] { null, "arm", "reverse", "diff" })
                {
                    var e = ex.Ledger.Entry(c["checkId"], r["armId"], part);
                    if ((part != null && e == null) || (e != null && Ev.Eq(e.Get("status"), status))) continue;
                    ex.Account(c, ex.Arm(r["armId"]), status, "blocked-prerequisite", detail, part: part);
                }
            }
    }

    /// <summary>Accounts one part of an arm the plan never meant this sweep to measure, with its plan status as the
    /// cause. False (nothing accounted) for a planned arm. An entry already carrying that status is left as it is.</summary>
    public static bool AccountUnplannedPart(Executor ex, PyDict check, PyDict arm, object? st, string part)
    {
        if (st == null || Ev.Eq(st, "planned")) return false;
        string aid = Ev.Str(arm["armId"]);
        string status, detail;
        string? cause;
        if (Ev.Eq(st, "skipped-budget"))
        {
            // held back by --max-candidates at plan time: that, not the deadline or the sweep, is its cause
            (status, cause) = ("skipped-budget", "budget-exhausted");
            detail = $"arm {aid} was held back by --max-candidates {Ev.Str(PyValue.Dict(ex.Plan["budgets"])["maxCandidates"])}: it was planned and not evaluated";
        }
        else if (Ev.Eq(st, "blocked-payload-missing"))
        {
            (status, cause) = ("blocked(payload-missing)", "blocked-prerequisite");
            detail = "the supplied arm declares files that are not in the configuration: "
                     + string.Join(", ", PyValue.Iter(PyValue.Dict(arm["supplied"])["payloadMissing"]).Select(x => (string)x!));
        }
        else
        {
            status = Ev.Eq(st, "not-applicable") ? "not-applicable"
                : st is "excluded" or "skipped-user" or EvalStore.NotOptedIn ? "skipped-user" : "unsupported";
            cause = null;
            detail = $"plan status {Ev.Str(st)}: this arm was not going to be measured";
        }
        if (!Ev.Eq(ex.Ledger.Entry(check["checkId"], aid, part)?.Get("status"), status))
            ex.Account(check, arm, status, cause, detail, part: part);
        return true;
    }

    // Every row of every swept check reads skipped-budget: the budget was gone before the sweep could launch.
    static void AccountBudgetGone(Executor ex, List<PyDict> sweepChecks)
    {
        foreach (var c in sweepChecks)
            foreach (var r in ArmRows(c))
                ex.Account(c, ex.Arm(r["armId"]), "skipped-budget", "budget-exhausted", ex.BudgetSentence());
    }

    /// <summary>Settles every row of the swept checks, once per invocation, at the place of the first of them in the
    /// schedule (evalrun.py's sweep block in execute): budget gone with work pending, nothing to settle, or the
    /// sweep child launched and its jobs split into receipts.</summary>
    public static void Settle(Executor ex, List<PyDict> sweepChecks, Action advance)
    {
        var allowed = ArmsAllowed(ex, sweepChecks);
        bool pending = Needed(ex, sweepChecks, []);
        if (pending && !ex.BudgetLeft())
            // the budget first: a prerequisite unsettled because the deadline passed is the budget's consequence
            AccountBudgetGone(ex, sweepChecks);
        else if (!pending || !Needed(ex, sweepChecks, allowed))
        {
            // nothing the sweep could settle: no child runs. An arm its prerequisite keeps out, or the plan never
            // measures, is accounted here without a launch.
            AccountKeptOut(ex, sweepChecks, allowed);
            var memo = new HashMemo();
            foreach (var c in sweepChecks)
                foreach (var r in ArmRows(c))
                {
                    var arm = ex.Arm(r["armId"]);
                    if (StateOf(allowed, r["armId"]) != "ok") continue;
                    string? why = Ev.Eq(r["status"], "planned") ? Prerequisites.CandidateStale(arm, memo) : null;
                    if (why != null)
                    {
                        bool gone = !Ev.Exists(PyPath.Str(Ev.Str(arm["candidatePath"])));
                        string st = gone ? "blocked(candidate-missing)" : "blocked(candidate-changed)";
                        if (!Ev.Eq(ex.Ledger.Entry(c["checkId"], r["armId"], null)?.Get("status"), st))
                            ex.Account(c, arm, st, gone ? "output-missing" : "stale-inputs",
                                gone ? $"the plan's candidate file {Ev.Str(arm["candidatePath"])} is gone" : why);
                        continue;
                    }
                    foreach (string part in AllParts(arm))
                        if (!AccountUnplannedPart(ex, c, arm, r["status"], part))
                            EvaluateRun.MarkReused(ex.Ledger, c["checkId"], r["armId"], part);
                }
            var kept = PyOrder.SortedByCodePoint(allowed.Where(kv => kv.Value != "ok").Select(kv => kv.Key));
            Console.Out.WriteLine(kept.Count == 0 ? "   ordersweep: every arm reused, no child run"
                : $"   ordersweep: nothing to measure, no child run ({kept.Count} arm(s) kept out by a prerequisite: {string.Join(", ", kept)})");
        }
        else if (ex.BudgetLeft()) RunSweep(ex, sweepChecks, advance, allowed);
        else AccountBudgetGone(ex, sweepChecks);
    }

    // ---- the launch folders

    /// <summary>RUN/sweep/inv-NNNN folders, oldest first: one per sweep launch.</summary>
    public static List<string> LaunchFolders(string run)
    {
        string root = PyPath.Join(run, "sweep");
        if (!Directory.Exists(root)) return [];
        var found = new List<(System.Numerics.BigInteger n, string path)>();
        foreach (string p in Directory.GetDirectories(root))
        {
            var m = SweepReading.SweepFolderRe.Match(Path.GetFileName(p));
            if (m.Success) found.Add((PyValue.IntText(m.Groups[1].Value)!.Value, PyPath.Join(root, Path.GetFileName(p))));
        }
        return found.OrderBy(x => x.n).ThenBy(x => x.path, PyOrder.CodePoint).Select(x => x.path).ToList();
    }

    /// <summary>A folder no earlier launch wrote: the next number.</summary>
    public static string NewFolder(string run)
    {
        var folders = LaunchFolders(run);
        var n = folders.Count > 0 ? PyValue.IntText(SweepReading.SweepFolderRe.Match(PyPath.Name(folders[^1])).Groups[1].Value)!.Value + 1 : 1;
        string root = PyPath.Join(run, "sweep");
        Directory.CreateDirectory(root);
        while (true)
        {
            string p = PyPath.Join(root, "inv-" + n.ToString("D4", CultureInfo.InvariantCulture));
            if (!Ev.Exists(p))
            {
                Directory.CreateDirectory(p);
                return p;
            }
            n += 1;
        }
    }

    static void Unlink(string path)
    {
        if (File.Exists(path)) File.Delete(path);
    }

    // Path(text) or None.
    static string? PathOf(object? text) => PyValue.Truthy(text) ? PyPath.Str(Ev.Str(text)) : null;

    static string Parent(string path) => Path.GetDirectoryName(path) ?? path;

    // A job's record as the runner's result: the stop it states.
    static ChildResult StopOf(PyDict rec, string? runId)
    {
        var stop = Ev.OrEmpty(rec.Get("stop"));
        return new ChildResult("ordersweep", null, stop.ContainsKey("seconds") ? Py.Float(stop["seconds"]) : 0.0, runId, 1,
            rec.Get("stopped") as string, PyValue.Truthy(stop.Get("treeStopped")),
            stop.Get("launchedExitCode") == null ? null : unchecked((int)(uint)(long)PyValue.Int(stop["launchedExitCode"])),
            PyValue.Truthy(stop.Get("refused")) ? Ev.Str(stop["refused"]) : null);
    }

    // ---- the launch

    /// <summary>Starts the order sweep once, into a folder of its own, then splits what that launch recorded into
    /// per-(check, arm, part) receipts (evalrun.py's run_ordersweep).</summary>
    public static void RunSweep(Executor ex, List<PyDict> sweepChecks, Action advance, Dictionary<string, string> allowed)
    {
        var plan = ex.Plan;
        string run = ex.Run;
        string cdir = PyPath.Join(run, "candidates");
        Directory.CreateDirectory(cdir);

        // the launch boundary: every program this child can run is the plan's, before anything of this sweep is written
        var exes = sweepChecks.Select(c => PyValue.Dict(c["adapter"]).Get("exe")).Where(PyValue.Truthy).Select(Ev.Str).ToHashSet(StringComparer.Ordinal);
        var sweepPids = PyOrder.SortedByCodePoint(exes.Select(e => Receipts.ProgramId(e)).Distinct(StringComparer.Ordinal));
        if (ex.Programs.ContainsKey(Receipts.Comparator)) sweepPids.Add(Receipts.Comparator);
        sweepPids.Add(Receipts.Controller);
        ex.CheckLaunch(sweepPids, "the ordersweep child");
        AccountKeptOut(ex, sweepChecks, allowed);
        HashSet<string>? runnable = allowed.Count > 0 ? allowed.Where(kv => kv.Value == "ok").Select(kv => kv.Key).ToHashSet(StringComparer.Ordinal) : null;
        if (runnable != null && !runnable.Contains("baseline"))
        {
            Console.Out.WriteLine("   ordersweep: not run - the baseline's prerequisite is not settled");
            return;
        }

        // RUN/candidates is assembled from the live files, each hash-checked against the plan: a copy that still
        // matches says nothing about the file the plan points at
        var arms = PyValue.Iter(plan["arms"]).Select(PyValue.Dict).ToList();
        string baselineId = Ev.Str(arms.First(a => Ev.Eq(a["kind"], "baseline"))["armId"]);
        var memo = new HashMemo();
        var staleArms = new HashSet<string>(StringComparer.Ordinal);
        // an arm no swept check plans is never handed to the sweep: it would be measured while the plan says it was not
        var plannedArms = sweepChecks.SelectMany(ArmRows).Where(r => Ev.Eq(r.Get("status"), "planned")).Select(r => Ev.Str(r["armId"])).ToHashSet(StringComparer.Ordinal);
        foreach (var arm in arms)
        {
            string aid = Ev.Str(arm["armId"]);
            if ((runnable != null && !runnable.Contains(aid)) || (!plannedArms.Contains(aid) && !Ev.Eq(arm["kind"], "baseline")))
            {
                // a copy an earlier invocation assembled would hand the sweep an arm this one keeps out
                if (PyValue.Truthy(arm.Get("candidateFile"))) Unlink(PyPath.Join(cdir, Ev.Str(arm["candidateFile"])));
                continue;
            }
            string src = PyPath.Str(Ev.Str(arm["candidatePath"]));
            string dst = PyPath.Join(cdir, Ev.Str(arm["candidateFile"]));
            void Stale(string status, string cause, string why)
            {
                foreach (var check in sweepChecks) ex.Account(check, arm, status, cause, why);
                staleArms.Add(aid);
                Unlink(dst);   // never hand the sweep an order the plan did not approve
            }
            if (!Ev.Exists(src))
            {
                Stale("blocked(candidate-missing)", "output-missing", $"the plan's candidate file {src} is gone");
                continue;
            }
            if (Prerequisites.CandidateStale(arm, memo) is { } why)
            {
                Stale("blocked(candidate-changed)", "stale-inputs", why);
                continue;
            }
            if (!Ev.Exists(dst) || !Ev.Eq(Hashing.Sha256FileOrNull(dst), arm["candidateSha256"])) File.Copy(src, dst, overwrite: true);
            if (!Ev.Eq(Hashing.Sha256FileOrNull(dst), arm["candidateSha256"]))
                Stale("blocked(candidate-changed)", "stale-inputs", $"{Ev.Str(arm["candidateFile"])} no longer hashes to the plan's value");
        }
        if (staleArms.Contains(baselineId))
        {
            Console.Out.WriteLine("   ordersweep: not run - the baseline candidate file no longer hashes to the plan's value");
            return;
        }
        if (staleArms.Count > 0)
        {
            runnable ??= arms.Select(a => Ev.Str(a["armId"])).ToHashSet(StringComparer.Ordinal);
            runnable.ExceptWith(staleArms);
        }
        string srcManifest = PyPath.Join(PyPath.Str(Ev.Str(plan["candidatesDir"])), "candidates.json");
        if (Ev.Exists(srcManifest)) File.Copy(srcManifest, PyPath.Join(cdir, "candidates.json"), overwrite: true);

        // a folder of this launch's own: no earlier launch wrote at any path this child writes. Earlier launches'
        // caches are passed read-only, newest first.
        var earlier = LaunchFolders(run);
        string inv = NewFolder(run);
        string folder = PyPath.RelTo(run, inv);
        var rows = PyValue.Truthy(ex.Ledger.Doc.Get("runIds")) ? (List<object?>)ex.Ledger.Doc["runIds"]! : [];
        PyDict? last = rows.Count > 0 ? PyValue.Dict(rows[^1]) : null;
        if (last != null)
        {
            if (!last.ContainsKey("sweepFolders")) last["sweepFolders"] = new List<object?>();
            ((List<object?>)last["sweepFolders"]!).Add(folder);
            ex.Ledger.Save();
        }
        var probes = PyOrder.SortedByCodePoint(exes);
        var population = Ev.OrEmpty(plan.Get("population"));
        var populationArgs = new List<string>();
        if (PyValue.Truthy(population.Get("scoped")))
        {
            string pfile = PyPath.Join(run, Ev.Str(population["file"]));
            var scopedHere = PyOrder.SortedByCodePoint(Ev.Items(population.Get("scopedProbes")).Select(Ev.Str).Distinct(StringComparer.Ordinal).Where(exes.Contains));
            if (scopedHere.Count > 0 && (!File.Exists(pfile) || !Ev.Eq(Hashing.Sha256FileOrNull(pfile), population["sha256"])))
            {
                string why = !File.Exists(pfile) ? $"the plan's population file {PyPath.Name(pfile)} is gone"
                    : $"the plan's population file {PyPath.Name(pfile)} no longer hashes to the plan's value";
                foreach (var check in sweepChecks)
                    foreach (var armRow in ArmRows(check))
                        if (Ev.Eq(armRow.Get("status"), "planned"))
                            ex.Account(check, arms.First(x => Ev.Eq(x["armId"], armRow["armId"])), "blocked(population-changed)", "stale-inputs", why);
                Console.Out.WriteLine("   ordersweep: not run - " + why);
                return;
            }
            if (scopedHere.Count > 0) populationArgs = ["--population", pfile, "--population-probes", string.Join(",", scopedHere)];
        }
        else if (Ev.Items(population.Get("meshFolders")).Select(Ev.Str).ToList() is { Count: > 0 } meshFolders)
        {
            // no population: the delta's folders are still the ones a mesh row may name (PlanPopulation, Plan.cs)
            var meshHere = PyOrder.SortedByCodePoint(HarnessShared.PopulationRules.MeshProbes.Where(exes.Contains));
            if (meshHere.Count > 0)
                populationArgs = ["--mesh-folders", PyJson.Dumps(meshFolders.Cast<object?>().ToList()), "--mesh-folder-probes", string.Join(",", meshHere)];
        }
        string baselineFile = Ev.Str(arms.First(a => Ev.Eq(a["kind"], "baseline"))["candidateFile"]);
        var args = new List<string>
        {
            "--candidates", cdir, "--out", inv, "--probes", string.Join(",", probes), "--harness", ex.Harness,
            "--baseline", PyPath.Join(cdir, baselineFile),
        };
        args.AddRange(ex.CommonFlags());
        if (PyValue.Truthy(ex.Opts.Get("probeArgs"))) args.AddRange(["--probe-args", Ev.Str(ex.Opts["probeArgs"])]);
        if (PyValue.Truthy(ex.Opts.Get("diffTolerance"))) args.AddRange(["--diff-tolerance", Ev.Str(ex.Opts["diffTolerance"])]);
        if (NoReverse(ex)) args.Add("--no-reverse");
        args.AddRange(populationArgs);
        foreach (var (name, prefix) in Ev.OrEmpty(ex.Opts.Get("probeCmd"))) args.AddRange(["--probe-cmd", $"{name}={Ev.Str(prefix)}"]);
        foreach (string f in Enumerable.Reverse(earlier))
            if (Ev.Exists(PyPath.Join(f, "cache.json"))) args.AddRange(["--reuse-cache", PyPath.Join(f, "cache.json")]);

        foreach (var check in sweepChecks)
            foreach (var armRow in ArmRows(check))
            {
                string aid = Ev.Str(armRow["armId"]);
                // an arm kept out of the sweep is not being measured, so it is never marked running
                if (staleArms.Contains(aid) || (runnable != null && !runnable.Contains(aid)) || !Ev.Eq(armRow.Get("status"), "planned")) continue;
                // an arm the sweep will serve from its own cache is not being measured again
                if (EvalStore.IsTerminalOk(ex.Ledger.Entry(check["checkId"], aid, "arm")?.Get("status"))) continue;
                ex.Ledger.Transition(Ev.Str(check["checkId"]), aid, "arm", "running");
            }
        if (ex.Deadline is { } deadline)
            // the sweep's probes get the tree rule too
            args.AddRange(["--deadline-seconds", PyText.Fixed(Math.Max(0.0, deadline - ChildRunner.Now()), 3)]);
        string startedIso = EvalStore.UtcNow();
        double startedMono = (DateTime.UtcNow - DateTime.UnixEpoch).TotalSeconds;
        var res = ex.Launch(new ChildJob("ordersweep", ChildLaunch.Controller("ordersweep", args), Env: ex.ChildEnv()));
        string log = PyPath.Join(ex.Logs, "ordersweep.txt");
        if (res.Stopped is "before-launch" or "uncontained" && !Directory.EnumerateFileSystemEntries(inv).Any())
        {
            // the child never ran, so there is no launch to keep: no folder, no recorded launch
            Directory.Delete(inv);
            if (last != null && last.Get("sweepFolders") is List<object?> recorded && recorded.Any(x => Ev.Eq(x, folder)))
            {
                recorded.RemoveAt(recorded.FindIndex(x => Ev.Eq(x, folder)));
                if (recorded.Count == 0) last.Pop("sweepFolders");
                ex.Ledger.Save();
            }
        }
        IEnumerable<PyDict> BaselineFirst(PyDict check) => ArmRows(check).OrderBy(r => Ev.Eq(ex.Arm(r["armId"])["kind"], "baseline") ? 0 : 1);
        bool KeptOut(string aid) => staleArms.Contains(aid) || (runnable != null && !runnable.Contains(aid));
        if (!string.IsNullOrEmpty(res.Stopped))
        {
            // The sweep was stopped at the deadline, or never launched. A (check, arm) is credited only when every job
            // its receipts read finished in this launch; those go through the ordinary split. Every other planned part
            // not already accepted is accounted with the named cause; its finished probes stay in the folder's cache,
            // which the next launch reads.
            PyDict? jobsDone = null, summaryDone = null;
            string? endedDone = null;
            if (res.Stopped == "deadline")
            {
                ex.Children++;
                ex.DeadlineStops++;
                ex.CheckAccept(sweepPids, "the ordersweep child");
                jobsDone = SweepReading.ReadSweepJobs(inv);
                summaryDone = Ev.OrEmpty(PyJson.LoadEvalFile(PyPath.Join(inv, "summary.json")));
                endedDone = EvalStore.UtcNow();
            }
            foreach (var check in sweepChecks)
                foreach (var armRow in BaselineFirst(check))
                {
                    string aid = Ev.Str(armRow["armId"]);
                    if (KeptOut(aid))
                    {
                        advance();   // already accounted blocked(...) above
                        continue;
                    }
                    var arm = ex.Arm(aid);
                    if (jobsDone != null && Ev.Eq(armRow.Get("status"), "planned") && ArmFinished(ex, check, arm, jobsDone, summaryDone!))
                    {
                        ex.Checked = ["launch", "acceptance"];
                        try { Split(ex, check, arm, Ev.Str(PyValue.Dict(check["adapter"])["exe"]), inv, jobsDone, summaryDone!, res, startedIso, endedDone!, startedMono, log); }
                        finally { ex.Checked = ["preflight"]; }
                        advance();
                        continue;
                    }
                    foreach (string part in AllParts(arm))
                    {
                        var e = ex.Ledger.Entry(check["checkId"], aid, part);
                        if (EvalStore.IsTerminalOk(e?.Get("status")))
                        {
                            ex.Ledger.Transition(Ev.Str(check["checkId"]), aid, part, "reused", receiptId: e!.Get("receiptId") as string,
                                detector: e.Get("detector") as string, extra: new PyDict { ["reusedFromRunId"] = e.Get("runId") });
                            continue;
                        }
                        // an arm the plan never meant to measure keeps its own cause: the deadline stopped nothing
                        if (AccountUnplannedPart(ex, check, arm, armRow.Get("status"), part)) continue;
                        if (part == "reverse" && NoReverse(ex))
                        {
                            // no reverse was going to be measured, stop or no stop
                            ex.Account(check, arm, "blocked(no-reverse-measurement)", null,
                                "no reverse emit: --no-reverse was given, so every created row reads unmeasured under the baseline order", part: part);
                            continue;
                        }
                        ex.AccountStop(check, arm, part, res, "the ordersweep child", log: Ev.Exists(log) ? log : null);
                    }
                    advance();
                }
            return;
        }
        ex.Children++;
        string endedIso = EvalStore.UtcNow();
        ex.CheckAccept(sweepPids, "the ordersweep child");
        var summary = Ev.OrEmpty(PyJson.LoadEvalFile(PyPath.Join(inv, "summary.json")));
        var jobs = SweepReading.ReadSweepJobs(inv);
        ex.Checked = ["launch", "acceptance"];
        try
        {
            foreach (var check in sweepChecks)
            {
                string probe = Ev.Str(PyValue.Dict(check["adapter"])["exe"]);
                // the baseline arm first: every candidate part is credited only on the baseline arm emit this split accepted
                foreach (var armRow in BaselineFirst(check))
                {
                    string aid = Ev.Str(armRow["armId"]);
                    if (KeptOut(aid))
                    {
                        advance();   // already accounted blocked(...) above
                        continue;
                    }
                    var arm = ex.Arm(aid);
                    if (!Ev.Eq(armRow.Get("status"), "planned"))
                        // kept out of the candidate directory, so nothing of it was measured: its plan status is its cause
                        foreach (string part in AllParts(arm)) AccountUnplannedPart(ex, check, arm, armRow.Get("status"), part);
                    else Split(ex, check, arm, probe, inv, jobs, summary, res, startedIso, endedIso, startedMono, log);
                    advance();
                }
            }
        }
        finally { ex.Checked = ["preflight"]; }
        ex.StopIfChanged();
    }

    // A job record that produced its emit: it ran to exit 0 and wrote it, or was served from a cache.
    static bool Produced(PyDict? rec) =>
        PyValue.Truthy(rec) && Ev.Eq(rec!.Get("exitCode"), 0L)
        && (Ev.Eq(rec.Get("outcome"), "reused") || (Ev.Eq(rec.Get("outcome"), "ran") && PyValue.Truthy(rec.Get("emitWritten"))));

    /// <summary>After a stop: whether every job this (check, arm)'s receipts read finished in this launch: the
    /// baseline's emit for all; for a candidate its forward, its reverse unless none was asked for, its diff and the
    /// diff's row in the summary.</summary>
    public static bool ArmFinished(Executor ex, PyDict check, PyDict arm, PyDict jobs, PyDict summary)
    {
        string probe = Ev.Str(PyValue.Dict(check["adapter"])["exe"]);
        bool Done(string job) => Produced(Ev.OrEmpty(jobs.Get(job)));
        if (!Done($"{probe}-baseline")) return false;
        if (Ev.Eq(arm["kind"], "baseline")) return true;
        string stem = Ev.Stem(Ev.Str(arm["candidateFile"]));
        var needed = new List<string> { $"{probe}-{stem}", $"diff-{probe}-{stem}" };
        if (!NoReverse(ex)) needed.Add($"{probe}-{stem}-reverse");
        var rows = Ev.Items(Ev.OrEmpty(Ev.OrEmpty(summary.Get("candidates")).Get(stem)).Get("rows"));
        return needed.All(Done) && rows.Any(r => Ev.Eq(Ev.G(r, "probe"), probe));
    }

    /// <summary>conflictdiff's reason for not comparing two emits, as one paragraph, from its job log, or null: the
    /// configuration gate's indented lines ending REFUSED.; the population or state gate's `! ...` line and its
    /// indented continuation; a usage or read error after the log's stderr marker.</summary>
    public static string? ConflictdiffRefusal(string? logText)
    {
        string text = logText ?? "";
        const string marker = "\n---- stderr ----\n";
        int at = text.IndexOf(marker, StringComparison.Ordinal);
        string stdout = at < 0 ? text : text[..at], err = at < 0 ? "" : text[(at + marker.Length)..];
        var lines = PyText.SplitLines(stdout);
        static bool Indented(string ln) => ln.Length > 0 && PyValue.Strip(ln[..1]).Length == 0;
        static string LStrip(string ln) => ln[(ln.Length - (PyValue.Strip(ln + "x").Length - 1))..];
        static string RStrip(string ln) => PyValue.Strip("x" + ln)[1..];
        int end = -1;
        for (int i = 0; i < lines.Count; i++)
            if (RStrip(lines[i]).EndsWith("REFUSED.", StringComparison.Ordinal) && Indented(lines[i])) end = i;
        if (end >= 0)
        {
            int start = end;
            while (start > 0 && Indented(lines[start - 1]) && PyValue.Strip(lines[start - 1]).Length > 0) start--;
            return string.Join(" ", lines.Skip(start).Take(end - start + 1).Select(PyValue.Strip));
        }
        for (int i = 0; i < lines.Count; i++)
        {
            string ln = lines[i];
            if (!PyValue.Strip(ln).StartsWith('!')) continue;
            int indent = ln.Length - LStrip(ln).Length;
            var block = new List<string> { PyValue.Strip(ln) };
            foreach (string nxt in lines.Skip(i + 1))
            {
                string body = PyValue.Strip(nxt);
                if (body.Length == 0 || body.StartsWith('!') || nxt.Length - LStrip(nxt).Length <= indent) break;
                block.Add(body);
            }
            return string.Join(" ", block);
        }
        return PyText.SplitLines(err).Select(PyValue.Strip).FirstOrDefault(ln => ln.Length > 0);
    }

    // x > 0 for a count read from JSON; absent counts as 0.
    static bool Positive(PyDict d, string key) => d.ContainsKey(key) && Py.Float(d[key]) > 0;

    /// <summary>The detector word of a diff whose verdict line was read: inconclusive when unjudged or anything is
    /// unmeasured, finding when a row was created, else no-finding.</summary>
    public static string DiffDetector(object? verdict, PyDict diff, PyDict created)
    {
        var counts = diff.ContainsKey("counts") ? PyValue.Dict(diff["counts"]) : new PyDict();
        if (Ev.Eq(verdict, "DIFFERENTIAL-UNJUDGED") || Positive(counts, "unmeasured") || Positive(created, "unmeasured")) return "inconclusive";
        return Positive(created, "newDefect") || Positive(counts, "created") ? "finding" : "no-finding";
    }

    /// <summary>One (check, arm)'s parts, each decided on its own from this launch's job records (evalrun.py's
    /// split_sweep_receipts). The plan decides which receipts exist. A part accepted by an earlier invocation stays
    /// reused unless this launch produced different bytes for it. Credit rests only on accepted emits: a candidate's
    /// parts on the same check's accepted baseline arm, a reverse and a diff on the accepted arm, a diff on the
    /// accepted reverse its created rows were split by. The caller settles the baseline arm first.</summary>
    public static void Split(Executor ex, PyDict check, PyDict arm, string probe, string inv, PyDict jobs, PyDict summary,
        ChildResult res, string startedIso, string endedIso, double startedMono, string log)
    {
        string run = ex.Run;
        string cid = Ev.Str(check["checkId"]), aid = Ev.Str(arm["armId"]);
        string stem = Ev.Stem(Ev.Str(arm["candidateFile"]));
        bool isBase = Ev.Eq(arm["kind"], "baseline");
        string baselineId = Ev.Str(PyValue.Iter(ex.Plan["arms"]).Select(PyValue.Dict).First(a => Ev.Eq(a["kind"], "baseline"))["armId"]);
        string folder = PyPath.RelTo(run, inv);
        var adapter = PyValue.Dict(check["adapter"]);
        var cand = Ev.OrEmpty(PyValue.Dict(Ev.G(summary, "candidates", new PyDict())).Get(stem));
        PyDict? row = null;
        foreach (var r in PyValue.Iter(Ev.G(cand, "rows", new List<object?>())).Select(PyValue.Dict))
            if (Ev.Eq(r.Get("probe"), probe)) row = r;
        object? classification = cand.Get("classification");
        string exitText = ChildLaunch.ExitText(res);

        string TextOf(string? p) => p != null && Ev.Exists(p) ? Ev.ReadText(p) : "";
        string StatusOf(string part, string? armId = null) => Ev.Str(ex.Ledger.Entry(cid, armId ?? aid, part)?.Get("status"));
        PyDict? AcceptedReceipt(string part, string? armId = null)
        {
            var e = ex.Ledger.Entry(cid, armId ?? aid, part);
            if (!EvalStore.IsTerminalOk(e?.Get("status"))) return null;
            object? doc = PyJson.LoadEvalFile(PyPath.Join(run, "receipts", $"{Ledger.Key(cid, armId ?? aid, part)}.json"));
            return PyValue.Truthy(doc) ? PyValue.Dict(doc) : null;
        }
        static object? OutputSha(PyDict? receipt) => Ev.Items(receipt?.Get("outputs")).Select(o => Ev.G(o, "sha256")).FirstOrDefault();
        // the first output of an accepted receipt: where it is and what it hashed to
        (string path, object? sha) Credit(PyDict receipt)
        {
            var out0 = PyValue.Truthy(receipt.Get("outputs")) ? PyValue.Dict(((List<object?>)receipt["outputs"]!)[0]) : new PyDict();
            return (PyPath.Join(run, Ev.Str(out0["path"])), out0.Get("sha256"));
        }
        void Keep(string part) => EvaluateRun.MarkReused(ex.Ledger, cid, aid, part);
        // why this launch ran nothing downstream of a job: its record says it ran and produced no emit
        string? FailedUpstream(string? job)
        {
            if (job == null || jobs.Get(job) is not PyDict rec || Produced(rec)) return null;
            string how = Ev.Eq(rec.Get("outcome"), "stopped") ? $"was stopped ({Ev.Str(rec.Get("stopped"))})" : $"ran with exit {Ev.Str(rec.Get("exitCode"))}";
            return $"this launch's job {job} {how} and produced no emit";
        }
        // the receipt's exit code is the job's own, never the sweep's
        ChildResult JobRes(PyDict? rec) => new("ordersweep", rec?.Get("exitCode") == null ? null : unchecked((int)(uint)(long)PyValue.Int(rec["exitCode"])), 0.0, res.RunId, 1);
        PyDict SweepBlock(string job, PyDict? rec)
        {
            string? src = PathOf(rec?.Get("reusedFrom"));
            return new PyDict
            {
                ["sweep"] = new PyDict
                {
                    ["folder"] = folder, ["job"] = job, ["outcome"] = rec?.Get("outcome"),
                    ["reusedFrom"] = src != null ? PyPath.RelTo(run, Parent(src)) : null,
                },
            };
        }
        // the mapping this controller's own children get: uncontained, refused by the deadline, or stopped at it
        void AccountStopped(string part, string job, PyDict rec) =>
            ex.AccountStop(check, arm, part, StopOf(rec, res.RunId), $"job {job} of the ordersweep child", log: PathOf(rec.Get("log")));

        // Settles the arm or reverse part from its job record; the credited emit (path, sha256) or null. baseExpected
        // is the credited emit this job is measured against; upstream the job whose failure in this launch would
        // explain a missing record; gate (status, why) says nothing this job measured may be credited: the part is
        // still validated, so a failure keeps its own cause.
        (string path, object? sha)? EmitPart(string part, string job, string defaultEmit, (string path, object? sha)? baseExpected,
            string? upstream, (string status, string why)? gate = null)
        {
            var rec = jobs.Get(job) as PyDict;
            var prior = AcceptedReceipt(part);
            if (prior != null && gate == null)
            {
                bool baseOk = baseExpected == null || Ev.Eq(Ev.OrEmpty(prior.Get("consumed")).Get("baseEmitSha256"), baseExpected.Value.sha);
                if (baseOk && (!Produced(rec) || Ev.Eq(rec!.Get("emitSha256"), OutputSha(prior))))
                {
                    // accepted earlier and nothing this launch did says otherwise: its receipt and files stand as written
                    Keep(part);
                    return Credit(prior);
                }
            }
            if (rec == null)
            {
                if (FailedUpstream(upstream) is { } why)
                {
                    // nothing ran for this part because the job it rests on failed in this launch: that failure is the cause
                    ex.Account(check, arm, part == "reverse" ? "blocked(no-reverse-measurement)" : "blocked(no-baseline-measurement)",
                        "blocked-prerequisite", $"no {part} job ran: {why}", part: part);
                    return null;
                }
                var missing = new PyDict
                {
                    ["accepted"] = false, ["detector"] = null, ["findings"] = null, ["coverage"] = new List<object?>(), ["verdict"] = null, ["identity"] = null,
                    ["rejections"] = new List<object?>
                    {
                        new PyDict
                        {
                            ["cause"] = "completion-missing",
                            ["detail"] = $"the ordersweep child (exit {exitText}) recorded no result for job {job} in {folder}/jobs.jsonl: nothing of that job is credited",
                        },
                    },
                };
                ex.Finish(check, arm, part, adapter, null, JobRes(rec), startedIso, endedIso, [defaultEmit], log, missing, extra: SweepBlock(job, rec));
                return null;
            }
            if (Ev.Eq(rec.Get("outcome"), "stopped"))
            {
                AccountStopped(part, job, rec);
                return null;
            }
            string emit = PathOf(rec.Get("emit")) ?? defaultEmit;
            string? basePath = PathOf(Ev.OrEmpty(rec.Get("inputs")).Get("base"));
            bool reused = Ev.Eq(rec.Get("outcome"), "reused");
            string? jobLog = PathOf(rec.Get("log"));
            string? srcLog = reused && PyValue.Truthy(rec.Get("reusedFrom")) ? PyPath.Join(Parent(PathOf(rec["reusedFrom"])!), "logs", $"{job}.txt") : null;
            var v = ex.Validator.Validate(check, arm, part, adapter, rec.Get("exitCode") == null ? null : (long)PyValue.Int(rec["exitCode"]),
                TextOf(reused ? srcLog : jobLog), [emit], new Dictionary<string, string?>(), reused ? null : startedMono);
            if (part == "reverse" && PyValue.Truthy(v["accepted"]) && v["detector"] == null && v["findings"] != null)
                v["detector"] = Py.Float(v["findings"]) > 0 ? "finding" : "no-finding";
            if (gate != null && PyValue.Truthy(v["accepted"]))
            {
                ex.Account(check, arm, gate.Value.status, "blocked-prerequisite",
                    $"job {job} measured on {gate.Value.why}: nothing measured on an emit that was not accepted is credited", part: part);
                return null;
            }
            // the reverse is measured on the baseline order with the candidate as --base, so it is validated against
            // the baseline arm's frozen request (the validator's reference arm)
            string? receiptLog = reused ? PyPath.Join(inv, "cache-log.txt") : jobLog;
            var receipt = ex.Finish(check, arm, part, adapter, null, JobRes(rec), startedIso, endedIso, [emit],
                receiptLog != null && Ev.Exists(receiptLog) ? receiptLog : log, v, baseEmit: basePath, extra: SweepBlock(job, rec));
            return PyValue.Truthy(v["accepted"]) ? (emit, OutputSha(receipt)) : null;
        }

        // ---- the arm part
        string baseJob = $"{probe}-baseline";
        string armJob = isBase ? baseJob : $"{probe}-{stem}";
        (string path, object? sha)? baseCredit = null;
        (string status, string why)? baseGate = null;
        if (!isBase)
        {
            if (AcceptedReceipt("arm", baselineId) is { } br) baseCredit = Credit(br);
            else
                // every part of a candidate arm is measured against the baseline arm's emit, so none is credited while
                // that emit is not accepted
                baseGate = ("blocked(baseline-not-accepted)", $"this check's baseline arm emit, and the baseline arm part is {StatusOf("arm", baselineId)}");
        }
        var armCredit = EmitPart("arm", armJob, PyPath.Join(inv, probe, isBase ? "baseline.json" : $"{stem}.json"), baseCredit, isBase ? null : baseJob, baseGate);
        if (isBase) return;

        // ---- the reverse part
        string revJob = $"{probe}-{stem}-reverse";
        string revEmit = PyPath.Join(inv, probe, $"{stem}.reverse.json");
        (string path, object? sha)? revCredit = null;
        if (NoReverse(ex))
            ex.Account(check, arm, "blocked(no-reverse-measurement)", null,
                "no reverse emit: --no-reverse was given, so every created row reads unmeasured under the baseline order", part: "reverse");
        else if (armCredit == null && jobs.Get(revJob) == null)
            ex.Account(check, arm, "blocked(no-reverse-measurement)", null,
                $"no reverse was measured: the arm part is {StatusOf("arm")}, so no reverse ran on it and every created row reads unmeasured under the baseline order",
                part: "reverse");
        else if (armCredit == null)
            EmitPart("reverse", revJob, revEmit, null, armJob, baseGate ?? ("blocked(arm-not-accepted)", $"the arm emit, and the arm part is {StatusOf("arm")}"));
        else revCredit = EmitPart("reverse", revJob, revEmit, armCredit, armJob);

        // ---- the diff part
        string diffJob = $"diff-{probe}-{stem}";
        var drec = jobs.Get(diffJob) as PyDict;
        var dinputs = Ev.OrEmpty(drec?.Get("inputs"));
        if (AcceptedReceipt("diff") is { } priorDiff && armCredit != null && baseCredit != null)
        {
            object? pinArm = null, pinReverse = null;
            foreach (var r in Ev.Items(Ev.OrEmpty(priorDiff.Get("consumed")).Get("inputs")))
            {
                if (Ev.Eq(Ev.G(r, "part"), "arm")) pinArm = Ev.G(r, "sha256");
                if (Ev.Eq(Ev.G(r, "part"), "reverse")) pinReverse = Ev.G(r, "sha256");
            }
            if (Ev.Eq(pinArm, armCredit.Value.sha) && Ev.Eq(pinReverse, revCredit?.sha))
            {
                // the accepted receipt keeps pointing at its own log, which no later launch writes
                Keep("diff");
                return;
            }
        }
        string diffLog = PathOf(drec?.Get("emit")) ?? PyPath.Join(inv, "logs", $"{diffJob}.txt");
        string? armPath = PathOf(dinputs.Get("arm")), reversePath = PathOf(dinputs.Get("reverse"));
        var diff = Ev.OrEmpty(row?.Get("diff"));
        var created = Ev.OrEmpty(row?.Get("created"));
        object? verdict = diff.Get("verdict");
        string? detector = null;
        var rejections = new List<object?>();
        void Reject(string cause, string detail) => rejections.Add(new PyDict { ["cause"] = cause, ["detail"] = detail });
        if (drec == null)
        {
            string? why = FailedUpstream(armJob) ?? FailedUpstream(baseJob);
            if (why != null || armCredit == null)
                Reject("output-missing", $"{PyPath.RelTo(run, diffLog)}: no diff ran, because " + (why ?? $"the arm part is {StatusOf("arm")}"));
            else
                Reject("completion-missing", $"the ordersweep child (exit {exitText}) recorded no result for job {diffJob} in {folder}/jobs.jsonl");
        }
        else if (Ev.Eq(drec.Get("outcome"), "stopped"))
        {
            AccountStopped("diff", diffJob, drec);
            return;
        }
        else if (drec.Get("exitCode") != null && !Ev.Eq(drec["exitCode"], 0L))
        {
            // conflictdiff exits 3 when it refuses a comparison and 2 on a usage or read error, before any verdict:
            // its own refusal line is the cause, not a missing verdict
            string? said = ConflictdiffRefusal(TextOf(diffLog));
            Reject("exit-nonzero", $"exit code {Ev.Str(drec["exitCode"])}: conflictdiff did not compare these emits"
                                   + (string.IsNullOrEmpty(said) ? "" : $" - it said {PyText.Repr(Ev.Head(said, RefusalQuoteChars))}"));
        }
        else if (!Ev.Exists(diffLog) || !PyValue.Truthy(drec.Get("emitWritten"))) Reject("output-missing", PyPath.RelTo(run, diffLog));
        else if (row == null)
            Reject("completion-missing", $"the ordersweep child (exit {exitText}) wrote no summary row for {probe}/{stem} in {folder}/summary.json, "
                                         + "which is where this diff's verdict and created rows are read");
        else if (verdict is null or "(not run)" or "(no verdict line)")
            Reject("schema-unsupported", $"conflictdiff produced no verdict line for {probe}/{stem}");
        else detector = DiffDetector(verdict, diff, created);
        if (rejections.Count == 0)
        {
            // its own checks pass; it is credited only on accepted emits
            (string status, string why)? gate = baseGate
                ?? (armCredit == null ? ("blocked(arm-not-accepted)", $"the arm emit, and the arm part is {StatusOf("arm")}")
                    : PyValue.Truthy(dinputs.Get("reverse")) && revCredit == null
                        ? ("blocked(reverse-not-accepted)", $"a reverse emit that split its created rows, and the reverse part is {StatusOf("reverse")}")
                        : null);
            if (gate != null)
            {
                ex.Account(check, arm, gate.Value.status, "blocked-prerequisite",
                    $"job {diffJob} compared {gate.Value.why}: nothing measured on an emit that was not accepted is credited", part: "diff");
                return;
            }
        }
        var vd = new PyDict
        {
            ["accepted"] = rejections.Count == 0, ["rejections"] = rejections, ["detector"] = detector,
            ["findings"] = (diff.ContainsKey("counts") ? PyValue.Dict(diff["counts"]) : new PyDict()).Get("created"),
            ["coverage"] = new List<object?>(), ["verdict"] = verdict, ["identity"] = null,
        };
        // the emits the diff's result was read from, as the job recorded them
        var diffInputs = new List<object?>();
        foreach (var (part, p) in new[] { ("arm", armPath), ("reverse", reversePath) })
            if (p != null) diffInputs.Add(new PyDict { ["part"] = part, ["path"] = PyPath.RelTo(run, p), ["sha256"] = Hashing.Sha256FileOrNull(p) });
        var extra = SweepBlock(diffJob, drec);
        extra["orderEvaluation"] = new PyDict
        {
            ["classification"] = classification, ["probes"] = new List<object?> { probe }, ["diff"] = diff, ["created"] = created,
            ["note"] = "ordersweep's own classification, verbatim and scoped to the probes named; never a whole-evaluation pass",
        };
        ex.Finish(check, arm, "diff", adapter, null, JobRes(drec), startedIso, endedIso, [diffLog], Ev.Exists(diffLog) ? diffLog : log, vd,
            verdict: verdict, extra: extra, inputs: diffInputs);
    }
}
