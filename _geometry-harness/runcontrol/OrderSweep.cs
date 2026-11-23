using System.Numerics;
using System.Text.RegularExpressions;
using HousecarlCore.Progress;

namespace RunControl;

/// <summary>`runcontrol ordersweep`: ordersweep.py in C#. For every candidate order and every probe named it runs the
/// probe against the baseline emit, the reverse measurement and conflictdiff, then classifies the candidate from what
/// those steps established. Same flags, files, job ids, cache and words as the Python sweep.</summary>
public static class OrderSweep
{
    public static readonly Regex VerdictRe = PyRegex.Compile(@"verdict\s*:\s*(DIFFERENTIAL-[A-Z]+)");
    public static readonly Regex CountRe = PyRegex.Compile(@"^\s*(repaired|reclassified|created|unmeasured|unchanged)\s*:\s*([\d,]+)", multiline: true);
    // A refusal, in any case, but not a count: "refused 50" and "3 refused" are tallies.
    public static readonly Regex RefusedRe = PyRegex.Compile(@"(?i)MISSING MASTER|CANNOT ENABLE|PROFILE MISMATCH|(?<![\d,] )\brefused\b(?!\s*[\d,]*\d)");
    public const string NotProof = "No passing order found within the budget is not proof that none exists.";

    // Probes whose reverse run can take the measured baseline emit's rows: probe -> the flag that names that emit.
    static readonly Dictionary<string, string> ReconcileReverse = new() { ["overlapprobe"] = "--reverse-of", ["contactprobe"] = "--reverse-of" };
    // Probes that keep placed-pair results across the jobs of one build.
    static readonly HashSet<string> PairResults = ["overlapprobe"];
    const string PairResultsSuffix = ".pair-results.jsonl.gz";

    // ---- what a probe left beside its emit

    /// <summary>What a probe's memory-route file (emit + ".memory.json") says; "not reported" when there is none.</summary>
    public static PyDict MemoryRoute(string? emit)
    {
        string? side = string.IsNullOrEmpty(emit) ? null : PyPath.Str(emit + ".memory.json");
        if (side == null || !File.Exists(side)) return new PyDict { ["route"] = "not reported" };
        PyDict d;
        try { d = PyValue.Dict(PyJson.Loads(PyJson.Decode(File.ReadAllBytes(side), bom: false))); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or FormatException)
        {
            return new PyDict { ["route"] = "not reported", ["sidecarUnreadable"] = $"{side}: {ex.Message}" };
        }
        return new PyDict
        {
            ["route"] = PyValue.Truthy(d.Get("route")) ? d.Get("route") : "not reported", ["case"] = d.Get("case"),
            ["unattendedDefault"] = d.Get("unattendedDefault"), ["warning"] = d.Get("warning"), ["outcome"] = d.Get("outcome"),
            ["sidecar"] = side,
        };
    }

    // ---- which program runs

    static string ProbeExe(string harness, string probe)
    {
        string p = PyPath.Str(HarnessShared.CheckoutLayout.ProgramExe(harness, probe));
        if (!SweepFiles.Exists(p)) throw new SweepExit(1, $"{probe}: no Release binary at {p} — build it first (dotnet build -c Release)");
        return p;
    }

    // The argv prefix that runs one probe (or conflictdiff): the Release binary, or a --probe-cmd stand-in.
    static List<string> ProbeProgram(string harness, string probe, Dictionary<string, string> overrides) =>
        overrides.TryGetValue(probe, out string? over) && over.Length > 0 ? PyText.Split(over) : [ProbeExe(harness, probe)];

    /// <summary>What the cache keys a program by: its identity digest under the one tool-identity rule.</summary>
    public static string ProgramKey(string harness, string probe, Dictionary<string, string> overrides)
    {
        if (overrides.TryGetValue(probe, out string? over) && over.Length > 0)
            return ToolIdentity.ProbeCmd(over)["digest"] as string is { Length: > 0 } digest ? digest : "probe-cmd:unverifiable:" + Hashing.Sha256Text(over);
        return (string)ToolIdentity.Package(SweepFiles.Parent(ProbeExe(harness, probe)))["digest"]!;
    }

    /// <summary>The request key: sha256 over the "|"-joined probe, program digest, order hash, base emit hash (or "-"),
    /// the shared arguments, then "population:" and "reverse-of:" terms when they apply.</summary>
    public static string RequestKey(string probe, string program, string orderSha, string? baseSha, IEnumerable<string> arguments,
                                    string? populationSha = null, string? reverseOfSha = null, string? meshFolders = null)
    {
        var parts = new List<string> { probe, program, orderSha, baseSha ?? "-", string.Join(" ", arguments) };
        if (populationSha != null) parts.Add("population:" + populationSha);
        if (meshFolders != null) parts.Add("mesh:" + meshFolders);
        if (reverseOfSha != null) parts.Add("reverse-of:" + reverseOfSha);
        return Hashing.Sha256Text(string.Join("|", parts));
    }

    // ---- the candidates

    static PyDict LoadManifest(string cdir)
    {
        string m = PyPath.Join(cdir, "candidates.json");
        if (!SweepFiles.Exists(m)) return new PyDict { ["_note"] = "no candidates.json manifest: search outcome unknown" };
        return PyJson.LoadEvalFile(m) as PyDict ?? new PyDict { ["_note"] = "candidates.json unreadable" };
    }

    // The baseline file and the other candidate files, in path order.
    internal static (string baseline, List<string> others) CandidateFiles(string cdir, string? baseline, BigInteger? limit)
    {
        var found = new List<string>();
        if (Directory.Exists(cdir))
            foreach (string entry in Directory.EnumerateFileSystemEntries(cdir))
            {
                string name = PyText.Lower(Path.GetFileName(entry));
                if (name.Length >= "candidate-.json".Length && name.StartsWith("candidate-", StringComparison.Ordinal) && name.EndsWith(".json", StringComparison.Ordinal))
                    found.Add(PyPath.Join(cdir, Path.GetFileName(entry)));
            }
        var files = PyOrder.SortedPaths(found);
        if (files.Count == 0) throw new SweepExit(1, $"no candidate-*.json in {cdir}");
        if (baseline == null)
        {
            var named = files.Where(f => SweepFiles.Stem(f).EndsWith("baseline", StringComparison.Ordinal)).ToList();
            if (named.Count != 1)
                throw new SweepExit(1, $"{cdir}: expected exactly one candidate-*-baseline.json, found {named.Count}; pass --baseline");
            baseline = named[0];
        }
        var others = files.Where(f => !SweepFiles.SamePath(f, baseline)).ToList();
        if (limit is { } n)
        {
            // others[:n]: a negative n counts from the end.
            int take = n >= others.Count ? others.Count : n >= 0 ? (int)n : (int)BigInteger.Max(0, others.Count + n);
            others = others.Take(take).ToList();
        }
        return (baseline, others);
    }

    // ---- reading emits

    /// <summary>The records a predicate is about, never the plugin pair, lower-cased; with a part the row is one link.</summary>
    public static string RowIdentity(string kind, string formKey, string? other, string a, object? part = null)
    {
        kind = PyText.Lower(kind);
        formKey = PyText.Lower(formKey);
        a = PyText.Lower(a);
        if (other == null) return $"{kind}|{formKey}|{a}";
        other = PyText.Lower(other);
        if (part != null) return $"{kind}|{formKey}|{other}|{PyText.Str(part)}";
        return PyOrder.CompareCodePoints(formKey, other) <= 0 ? $"{kind}|{formKey}|{other}" : $"{kind}|{other}|{formKey}";
    }

    // (load_json(p) or {}): the document, or an empty one when it is unreadable or empty.
    static PyDict Doc(string path)
    {
        object? doc = PyJson.LoadEvalFile(path);
        return PyValue.Truthy(doc) ? PyValue.Dict(doc) : new PyDict();
    }

    // d.get(key, []) walked as rows.
    static IEnumerable<PyDict> Rows(PyDict d, string key) =>
        d.ContainsKey(key) ? PyValue.Iter(d[key]).Select(PyValue.Dict) : [];

    /// <summary>identity -> Physical for every necessary record of an emit; the first row of an identity is kept.</summary>
    public static PyDict EmitRows(string emit)
    {
        var rows = new PyDict();
        foreach (var c in Rows(Doc(emit), "Conflicts"))
        {
            var alt = PyValue.Truthy(c.Get("Alternative")) ? PyValue.Iter(c.Get("Alternative")).ToList() : [];
            string? other = alt.Count > 0 ? (string?)PyValue.Dict(alt[0])["FormKey"] : null;
            object? part = alt.Count > 0 ? PyValue.Dict(alt[0]).Get("Triangle") : null;
            foreach (var r in Rows(c, "Necessary"))
            {
                string id = RowIdentity((string)c["Kind"]!, (string)r["FormKey"]!, other, (string)c["A"]!, part);
                if (!rows.ContainsKey(id)) rows[id] = r.Get("Physical");
            }
        }
        return rows;
    }

    /// <summary>identity -> Physical for the base emit's rows measured again under this emit's arm.</summary>
    public static PyDict EmitPredicates(string emit)
    {
        var found = new PyDict();
        var doc = Doc(emit);
        foreach (var p in PyValue.Truthy(doc.Get("Predicates")) ? PyValue.Iter(doc.Get("Predicates")).Select(PyValue.Dict) : [])
        {
            string id = RowIdentity((string)p["Kind"]!, (string)p["FormKey"]!, (string?)p.Get("Other"), (string)p["A"]!, p.Get("OtherTriangle"));
            if (!found.ContainsKey(id)) found[id] = p.ContainsKey("Physical") ? p["Physical"] : "Unmeasured";
        }
        return found;
    }

    /// <summary>The configuration identity an emit carries, or null when it carries none.</summary>
    public static PyDict? ReadIdentity(string emit)
    {
        var doc = Doc(emit);
        object? raw = PyValue.Truthy(doc.Get("Configuration")) ? doc.Get("Configuration") : doc.Get("configuration");
        if (!PyValue.Truthy(raw)) return null;
        var cfg = PyValue.Dict(raw);
        List<object?> Hashed(string key) =>
            Rows(cfg, key).Select(p => (object?)new PyDict { ["path"] = p["Path"], ["sha256"] = p["Sha256"] }).ToList();
        return new PyDict
        {
            ["fingerprint"] = cfg.ContainsKey("Fingerprint") ? cfg["Fingerprint"] : "",
            ["plugins"] = Hashed("Plugins"), ["overlays"] = Hashed("Overlays"),
            ["population"] = cfg.Get("Population"), ["state"] = cfg.Get("State"),
            ["limits"] = cfg.ContainsKey("Limits") ? cfg["Limits"] : new List<object?>(),
        };
    }

    // ---- the diff and the classification

    /// <summary>What conflictdiff's text says: the verdict, the counts, the refusal lines and the configuration delta.</summary>
    public static PyDict ParseDiff(string text)
    {
        var v = VerdictRe.Match(text);
        var counts = new PyDict();
        foreach (Match m in CountRe.Matches(text))
            counts[m.Groups[1].Value] = PyValue.Whole(PyValue.Int(m.Groups[2].Value.Replace(",", "")));
        var lines = PyText.SplitLines(text);
        var refusals = lines.Where(ln => ln.Contains("! REFUSED", StringComparison.Ordinal)).Select(ln => (object?)PyValue.Strip(ln)).ToList();
        var delta = new List<object?>();
        bool grab = false;
        foreach (string ln in lines)
        {
            if (PyText.Lower(ln).Contains("configuration delta", StringComparison.Ordinal)) { grab = true; continue; }
            if (!grab) continue;
            if (PyValue.Strip(ln).Length == 0 || !ln.StartsWith(' ')) { grab = false; continue; }
            delta.Add(PyValue.Strip(ln));
        }
        return new PyDict
        {
            ["verdict"] = v.Success ? v.Groups[1].Value : "(no verdict line)", ["counts"] = counts, ["refusals"] = refusals, ["delta"] = delta,
        };
    }

    /// <summary>The candidate's created rows, split by what the reverse measurement says about them under the baseline.</summary>
    public static PyDict CreatedBreakdown(string baseEmit, string candEmit, string? reverseEmit)
    {
        var baseRows = EmitRows(baseEmit);
        var created = EmitRows(candEmit).Where(kv => !baseRows.ContainsKey(kv.Key) && kv.Value as string == "Holds").Select(kv => kv.Key).ToList();
        if (reverseEmit == null || !SweepFiles.Exists(reverseEmit))
            return new PyDict
            {
                ["created"] = (long)created.Count, ["newDefect"] = 0L, ["attributed"] = 0L, ["unmeasured"] = (long)created.Count,
                ["note"] = "no reverse measurement: every created row is unmeasured under the baseline order",
            };
        var rev = EmitPredicates(reverseEmit);
        long fresh = 0, attributed = 0, unmeasured = 0;
        var examples = new List<object?>();
        foreach (string id in created)
        {
            string? p = rev.Get(id) as string;
            if (p == "Holds") attributed++;
            else if (p == "Gone")
            {
                fresh++;
                if (examples.Count < 5) examples.Add(id);
            }
            else unmeasured++;
        }
        return new PyDict
        {
            ["created"] = (long)created.Count, ["newDefect"] = fresh, ["attributed"] = attributed, ["unmeasured"] = unmeasured, ["newExamples"] = examples,
        };
    }

    static bool IsZero(object? v) => PyValue.Eq(v, 0L);

    /// <summary>One candidate's word, from its probes' rows.</summary>
    public static string Classify(IReadOnlyList<PyDict> rows)
    {
        if (rows.Any(r => PyValue.Truthy(r["illegal"]))) return "ILLEGAL";
        if (rows.Any(r => PyValue.AboveZero(PyValue.Dict(r["created"])["newDefect"]))) return "REGRESSION";
        if (rows.Any(r =>
            {
                var diff = PyValue.Dict(r["diff"]);
                var counts = PyValue.Dict(diff["counts"]);
                return !IsZero(r["probeExit"]) || PyValue.Truthy(r["probeRefused"]) || PyValue.Truthy(r.Get("emitNotWritten"))
                    || diff["verdict"] is "DIFFERENTIAL-UNJUDGED" or "(no verdict line)" or "(not run)"
                    || PyValue.Truthy(diff["refusals"]) || PyValue.AboveZero(counts.ContainsKey("unmeasured") ? counts["unmeasured"] : 0L)
                    || PyValue.AboveZero(PyValue.Dict(r["created"])["unmeasured"]);
            })) return "UNRESOLVED";
        return "PASSES-COMPLETED-CHECKS";
    }

    // ---- the command

    /// <summary>Run the sweep; the exit code is Python's: 0 done, 1 a stop with a sentence, 2 arguments not understood.</summary>
    public static int Run(string[] args)
    {
        // UTF-8 on the two streams without changing the console's code page, which the probes inherit.
        Console.SetOut(new StreamWriter(Console.OpenStandardOutput(), new System.Text.UTF8Encoding(false)) { AutoFlush = true });
        Console.SetError(new StreamWriter(Console.OpenStandardError(), new System.Text.UTF8Encoding(false)) { AutoFlush = true });
        try { return Sweep(SweepArgs.Parse(args)); }
        catch (SweepExit e)
        {
            if (e.Message.Length > 0) (e.Code == 0 ? Console.Out : Console.Error).WriteLine(e.Message);
            return e.Code;
        }
        catch (Exception e)
        {
            Console.Error.WriteLine($"{e.GetType().Name}: {e.Message}");
            return 1;
        }
    }

    // Text items of a JSON list joined, as "; ".join(list) does.
    static string Joined(string sep, object? list) => string.Join(sep, PyValue.Iter(list).Select(x => (string)x!));

    // cf.get("Changes") or cf.get("changes") or [].
    static object? Changes(PyDict cf) =>
        PyValue.Truthy(cf.Get("Changes")) ? cf.Get("Changes") : PyValue.Truthy(cf.Get("changes")) ? cf.Get("changes") : new List<object?>();

    static long Count(PyDict counts, string key) => counts.ContainsKey(key) ? (long)PyValue.Int(counts[key]) : 0;

    static int Sweep(SweepArgs a)
    {
        string cdir = PyPath.Str(a.Candidates), outDir = PyPath.Str(a.Out), harness = PyPath.Str(a.Harness);
        Directory.CreateDirectory(outDir);
        var probes = a.Probes.Split(',').Select(PyValue.Strip).Where(p => p.Length > 0).ToList();
        var common = new List<string>();
        foreach (var (flag, val) in new[] { ("--instance", a.Instance), ("--profile", a.Profile), ("--game", a.Game) })
            if (!string.IsNullOrEmpty(val)) { common.Add(flag); common.Add(val); }
        var extra = a.ProbeArgs.Length > 0 ? PyText.Split(a.ProbeArgs) : [];
        var shared = common.Concat(extra).ToList();
        var (baseline, cands) = CandidateFiles(cdir, string.IsNullOrEmpty(a.Baseline) ? null : PyPath.Str(a.Baseline), a.MaxCandidates);
        var manifest = LoadManifest(cdir);
        // Which advice rules each candidate violates or satisfies, from the manifest.
        var rulesOf = new Dictionary<string, PyDict>();
        foreach (var mc in Rows(manifest, "Candidates"))
        {
            string stem = SweepFiles.Stem((string)(mc.ContainsKey("File") ? mc["File"] : "")!);
            List<object?> Listed(string key) => PyValue.Truthy(mc.Get(key)) ? PyValue.Iter(mc.Get(key)).ToList() : [];
            rulesOf[stem] = new PyDict { ["violates"] = Listed("ViolatesAdvice"), ["satisfies"] = Listed("SatisfiesAdvice") };
        }
        PyDict RulesFor(string stem) =>
            rulesOf.GetValueOrDefault(stem) ?? new PyDict { ["violates"] = new List<object?>(), ["satisfies"] = new List<object?>() };
        var cache = new SweepCache(PyPath.Join(outDir, "cache.json"), PyPath.Join(outDir, "cache-log.txt"), a.ReuseCache);
        // One record per job, appended the moment the job has a result. Emptied first: a record is this launch's or nothing.
        string jobsLog = PyPath.Join(outDir, "jobs.jsonl");
        SweepFiles.WriteText(jobsLog, "");

        void RecordJob(string job, string kind, string outcome, long? exitCode, string? emit, bool written, string? reusedFrom = null,
                       string? stopped = null, string? log = null, PyDict? inputs = null, ChildResult? runner = null, string? program = null)
        {
            var rec = new PyDict
            {
                ["job"] = job, ["kind"] = kind, ["outcome"] = outcome, ["exitCode"] = exitCode, ["stopped"] = stopped,
                // the program's key at this launch's start: which build this job's result came from
                ["program"] = program,
                // what the runner said about a stop, so a reader names the real cause
                ["stop"] = !string.IsNullOrEmpty(stopped) && runner != null
                    ? new PyDict
                    {
                        ["seconds"] = runner.Seconds, ["treeStopped"] = runner.TreeStopped, ["refused"] = runner.Refused,
                        ["launchedExitCode"] = runner.LaunchedExitCodeUnsigned,
                    }
                    : null,
                ["emit"] = emit != null ? PyPath.Resolve(emit) : null, ["emitWritten"] = written,
                ["memoryRoute"] = kind == "probe" ? MemoryRoute(emit) : null,
                ["emitSha256"] = emit != null && File.Exists(emit) ? Hashing.Sha256File(emit) : null,
                ["reusedFrom"] = reusedFrom != null ? PyPath.Resolve(reusedFrom) : null,
                ["log"] = log != null && SweepFiles.Exists(log) ? PyPath.Resolve(log) : null,
                ["inputs"] = new PyDict((inputs ?? new PyDict()).Select(kv =>
                    new KeyValuePair<string, object?>(kv.Key, kv.Value is string { Length: > 0 } v ? PyPath.Resolve(v) : null))),
            };
            SweepFiles.AppendText(jobsLog, PyJson.Dumps(rec) + "\n");
        }

        var overrides = new Dictionary<string, string>();
        foreach (string spec in a.ProbeCmd)
        {
            int eq = spec.IndexOf('=');
            string name = PyValue.Strip(eq < 0 ? spec : spec[..eq]), prefix = PyValue.Strip(eq < 0 ? "" : spec[(eq + 1)..]);
            if (name.Length == 0 || prefix.Length == 0) throw new SweepExit(1, $"--probe-cmd {PyText.Repr(spec)}: expected NAME=<argv prefix>");
            overrides[name] = prefix;
        }
        var popProbes = a.PopulationProbes.Split(',').Select(PyValue.Strip).Where(p => p.Length > 0).ToHashSet();
        var popSorted = PyOrder.SortedByCodePoint(popProbes);
        if (popProbes.Count > 0 && string.IsNullOrEmpty(a.Population)) throw new SweepExit(1, "--population-probes needs --population FILE");
        if (!string.IsNullOrEmpty(a.Population) && popProbes.Count == 0)
            throw new SweepExit(1, "--population needs --population-probes naming the probes that take it");
        if (popSorted.Any(p => !probes.Contains(p)))
            throw new SweepExit(1, "--population-probes names probes this sweep does not run: " + string.Join(", ", popSorted.Where(p => !probes.Contains(p))));
        string? popFile = string.IsNullOrEmpty(a.Population) ? null : PyPath.Resolve(a.Population);
        if (popFile != null && !File.Exists(popFile)) throw new SweepExit(1, $"--population {popFile}: no such file");
        string? popSha = popFile != null ? Hashing.Sha256File(popFile) : null;
        var meshProbes = a.MeshFolderProbes.Split(',').Select(PyValue.Strip).Where(p => p.Length > 0).ToHashSet();
        var meshFolders = string.IsNullOrEmpty(a.MeshFolders) ? new List<string>() : PyValue.Iter(PyJson.Loads(a.MeshFolders)).Select(PyText.Str).ToList();
        if (meshProbes.Count > 0 && meshFolders.Count == 0) throw new SweepExit(1, "--mesh-folder-probes needs --mesh-folders naming at least one folder");
        var exes = new Dictionary<string, List<string>>();
        foreach (string p in probes) exes[p] = ProbeProgram(harness, p, overrides);
        var exeSha = new Dictionary<string, string>();
        foreach (string p in probes) exeSha[p] = ProgramKey(harness, p, overrides);
        var diffExe = ProbeProgram(harness, "conflictdiff", overrides);
        string diffKey = ProgramKey(harness, "conflictdiff", overrides);
        string logs = PyPath.Join(outDir, "logs");

        string KeyFor(string probe, string orderFile, string? baseEmit, string? reverseOf) =>
            RequestKey(probe, exeSha[probe], Hashing.Sha256File(orderFile), baseEmit != null ? Hashing.Sha256File(baseEmit) : null, shared,
                       popProbes.Contains(probe) ? popSha : null, reverseOf != null ? Hashing.Sha256File(reverseOf) : null,
                       meshProbes.Contains(probe) ? string.Join("\n", meshFolders) : null);

        // Under a caller's deadline a probe is finished only when its whole process tree is empty, and is cached only then.
        double? deadline = a.DeadlineSeconds is { } secs ? ChildRunner.Now() + (secs > 0 ? secs : 0.0) : null;
        var runner = new ChildRunner("ordersweep", logs, deadline);

        // Run (or reuse) one probe arm: (exit, refused, illegal, emit path, measured). Measured is true only for an
        // emit this launch wrote or a cache entry it verified.
        (long exit, bool refused, bool illegal, string emit, bool measured) RunProbe(string jobId, string probe, string orderFile,
                                                                                    string? baseEmit, string emit, string? reverseOf = null)
        {
            string k = KeyFor(probe, orderFile, baseEmit, reverseOf);
            var hit = cache.Lookup(k, jobId);
            var inputs = new PyDict { ["order"] = orderFile, ["base"] = baseEmit };
            if (reverseOf != null) inputs["baselineEmit"] = reverseOf;
            if (hit is { } served)
            {
                RecordJob(jobId, "probe", "reused", 0, served.emit, written: false, reusedFrom: served.cacheFile, inputs: inputs, program: exeSha[probe]);
                return (0, false, false, served.emit, true);
            }
            var argv = new List<string>(exes[probe]) { "--order", orderFile };
            if (baseEmit != null) argv.AddRange(["--base", baseEmit]);
            argv.AddRange(["--emit", emit]);
            argv.AddRange(shared);
            if (reverseOf != null) argv.AddRange([ReconcileReverse[probe], reverseOf]);
            if (PairResults.Contains(probe) && !a.NoPairResults)
            {
                string mine = PyPath.Join(outDir, probe, jobId + PairResultsSuffix);
                string mineResolved = PyPath.Resolve(mine);
                foreach (string d in new[] { outDir }.Concat(a.ReuseCache.Select(c => SweepFiles.Parent(PyPath.Resolve(c)))))
                {
                    string folder = PyPath.Join(d, probe);
                    if (!Directory.Exists(folder)) continue;
                    var written = Directory.EnumerateFileSystemEntries(folder)
                        .Where(f => PyText.Lower(Path.GetFileName(f)).EndsWith(PairResultsSuffix, StringComparison.Ordinal))
                        .Select(f => PyPath.Join(folder, Path.GetFileName(f)));
                    foreach (string f in PyOrder.SortedPaths(written))
                        if (!SweepFiles.SamePath(PyPath.Resolve(f), mineResolved)) argv.AddRange(["--pair-cache-read", f]);
                }
                argv.AddRange(["--pair-cache-write", mine]);
            }
            if (popProbes.Contains(probe)) argv.AddRange(["--population", popFile!]);
            if (meshProbes.Contains(probe)) foreach (string f in meshFolders) argv.AddRange(["--mesh-folder", f]);
            // Only an emit this launch wrote is recorded or measured: an earlier run's file may already be at this path.
            var before = SweepFiles.Signature(emit);
            var res = runner.Run(new ChildJob(jobId, argv), echo: true);
            string log = PyPath.Join(logs, jobId + ".txt");
            string text = SweepFiles.Exists(log) ? SweepFiles.ReadTextReplace(log) : "";
            bool refused = RefusedRe.IsMatch(text);
            bool illegal = res.ExitCodeUnsigned == 2 && text.Contains("REFUSED", StringComparison.Ordinal) && !SweepFiles.Exists(emit);
            var after = SweepFiles.Signature(emit);
            bool wrote = after != null && after != before;
            if (!string.IsNullOrEmpty(res.Stopped))
            {
                // stopped at (or refused by) the caller's deadline: not finished, never cached, never a clean exit
                RecordJob(jobId, "probe", "stopped", res.ExitCodeUnsigned, emit, wrote, stopped: res.Stopped, log: log, inputs: inputs,
                          runner: res, program: exeSha[probe]);
                return ((res.ExitCodeUnsigned ?? 0) != 0 ? res.ExitCodeUnsigned!.Value : -1, refused, false, emit, false);
            }
            if (res.ExitCodeUnsigned == 0 && SweepFiles.Exists(emit))
            {
                if (wrote) cache.Record(k, jobId, emit);
                else
                    cache.Note($"NOT-RECORDED {jobId}: exited 0 but did not write {PyPath.Name(emit)}; the file at that path "
                               + "is an earlier run's and is neither cached nor measured as this request's result");
            }
            RecordJob(jobId, "probe", "ran", res.ExitCodeUnsigned, emit, wrote, log: log, inputs: inputs, program: exeSha[probe]);
            return (res.ExitCodeUnsigned!.Value, refused, illegal, emit, res.ExitCodeUnsigned == 0 && wrote);
        }

        var results = new PyDict();
        var baseEmits = new Dictionary<string, string>();
        var baselineRoutes = new PyDict();

        PyDict CandidateEntry(string cand, List<object?> rows, string? classification)
        {
            var cf = Doc(cand);
            string changes = Joined("; ", Changes(cf));
            return new PyDict
            {
                ["classification"] = classification, ["changes"] = changes.Length > 0 ? changes : "(none listed)", ["rows"] = rows,
                ["rules"] = RulesFor(SweepFiles.Stem(cand)),
            };
        }

        PyDict SummaryHead()
        {
            var head = new PyDict
            {
                ["baseline"] = baseline, ["probes"] = probes.Cast<object?>().ToList(), ["candidates"] = new PyDict(), ["manifest"] = manifest,
                ["baselineMemoryRoute"] = baselineRoutes,
            };
            if (popProbes.Count > 0)
                head["population"] = new PyDict { ["file"] = popFile, ["sha256"] = popSha, ["probes"] = popSorted.Cast<object?>().ToList() };
            return head;
        }

        static List<PyDict> AsRows(object? rows) => ((List<object?>)rows!).Cast<PyDict>().ToList();

        // The rows finished so far, so a stop keeps them. A candidate is classified only once all its probes have rows.
        void SaveProgress()
        {
            var doc = SummaryHead();
            foreach (var (c, rows) in results)
            {
                string path = cands.First(x => SweepFiles.Stem(x) == c);
                var list = (List<object?>)rows!;
                ((PyDict)doc["candidates"]!)[c] = CandidateEntry(path, list, list.Count == probes.Count ? Classify(AsRows(list)) : null);
            }
            string tmp = PyPath.Join(outDir, "summary.json.tmp");
            SweepFiles.WriteText(tmp, PyJson.Dumps(doc, indent: 1));
            File.Move(tmp, PyPath.Join(outDir, "summary.json"), overwrite: true);
        }

        var plan = RunLog.Begin("ordersweep-plan", new RunOptions { StageSource = StageSource.Counted, PredictAtStart = false, Detached = true });
        try
        {
            plan.Feature("candidates", cands.Count, "candidates");
            plan.Feature("probes", probes.Count, "probes");
            plan.Stage("baseline", total: probes.Count, unit: "probes");
            foreach (string p in probes)
            {
                string emit = PyPath.Join(outDir, p, "baseline.json");
                Directory.CreateDirectory(SweepFiles.Parent(emit));
                var (code, _, _, got, measured) = RunProbe($"{p}-baseline", p, baseline, null, emit);
                baselineRoutes[p] = MemoryRoute(got);
                if (measured) baseEmits[p] = got;
                else
                    SweepFiles.Print($"   !! {p} baseline exited {code}" + (code != 0 ? "" : " without writing its emit") + " — its candidates read UNRESOLVED");
                plan.Advance();
            }

            int arms = a.NoReverse ? 2 : 3;
            plan.Stage("candidates", total: (long)cands.Count * probes.Count * arms, unit: "probe-arms");
            foreach (string cand in cands)
            {
                string stem = SweepFiles.Stem(cand);
                var rows = new List<object?>();
                results[stem] = rows;
                foreach (string p in probes)
                {
                    var row = new PyDict
                    {
                        ["probe"] = p, ["candidate"] = stem, ["probeExit"] = null, ["probeRefused"] = false, ["illegal"] = false,
                        ["diff"] = new PyDict
                        {
                            ["verdict"] = "(not run)", ["counts"] = new PyDict(), ["refusals"] = new List<object?>(), ["delta"] = new List<object?>(),
                        },
                        ["created"] = new PyDict { ["created"] = 0L, ["newDefect"] = 0L, ["attributed"] = 0L, ["unmeasured"] = 0L },
                    };
                    if (!baseEmits.TryGetValue(p, out string? baseEmit))
                    {
                        row["probeExit"] = -1L;
                        rows.Add(row);
                        SaveProgress();
                        plan.Advance(arms);
                        continue;
                    }
                    string emit = PyPath.Join(outDir, p, stem + ".json");
                    var (code, refused, illegal, got, measured) = RunProbe($"{p}-{stem}", p, cand, baseEmit, emit);
                    row["probeExit"] = code;
                    row["probeRefused"] = refused;
                    row["illegal"] = illegal;
                    row["emit"] = got;
                    row["memoryRoute"] = MemoryRoute(got);
                    if (code == 0 && !measured) row["emitNotWritten"] = true;
                    plan.Advance();
                    if (measured)
                    {
                        string? reverse = null;
                        if (!a.NoReverse)
                        {
                            string revEmit = PyPath.Join(outDir, p, stem + ".reverse.json");
                            string? rof = ReconcileReverse.ContainsKey(p) && !a.FullReverse ? baseEmit : null;
                            var (rcode, _, _, rgot, rmeasured) = RunProbe($"{p}-{stem}-reverse", p, baseline, got, revEmit, rof);
                            reverse = rmeasured ? rgot : null;
                            row["reverseExit"] = rcode;
                            row["reverseEmit"] = rgot;
                            row["reverseMemoryRoute"] = MemoryRoute(rgot);
                            if (rcode == 0 && !rmeasured) row["reverseEmitNotWritten"] = true;
                            plan.Advance();
                        }
                        row["created"] = CreatedBreakdown(baseEmit, got, reverse);
                        var dargs = new List<string>(diffExe) { baseEmit, got };
                        if (!string.IsNullOrEmpty(a.DiffTolerance)) dargs.AddRange(["--tolerance", a.DiffTolerance]);
                        string djob = $"diff-{p}-{stem}";
                        string dlog = PyPath.Join(logs, djob + ".txt");
                        var dbefore = SweepFiles.Signature(dlog);
                        var dres = runner.Run(new ChildJob(djob, dargs), echo: true);
                        var dafter = SweepFiles.Signature(dlog);
                        row["diff"] = ParseDiff(SweepFiles.Exists(dlog) ? SweepFiles.ReadTextReplace(dlog) : "");
                        row["diffExit"] = dres.ExitCodeUnsigned;
                        // The diff's own record names every emit its result was read from.
                        bool dstopped = !string.IsNullOrEmpty(dres.Stopped);
                        RecordJob(djob, "diff", dstopped ? "stopped" : "ran", dres.ExitCodeUnsigned, dlog, dafter != null && dafter != dbefore,
                                  stopped: dres.Stopped, log: dlog, inputs: new PyDict { ["base"] = baseEmit, ["arm"] = got, ["reverse"] = reverse },
                                  runner: dres, program: diffKey);
                        plan.Advance();
                    }
                    else plan.Advance(arms - 1);
                    rows.Add(row);
                    SaveProgress();
                }
            }
            plan.Complete();
        }
        catch (Exception ex)
        {
            plan.Fail($"{ex.GetType().Name}: {ex.Message}");
            throw;
        }
        finally { plan.Dispose(); }

        // ---- the report
        var summary = SummaryHead();
        var summaryCandidates = (PyDict)summary["candidates"]!;
        var lines = new List<string>
        {
            "# ordersweep report", "",
            $"baseline: `{PyPath.Name(baseline)}`; probes: {string.Join(", ", probes)}; candidates evaluated: {cands.Count}; "
            + $"reverse measurement: {(a.NoReverse ? "off" : "on")}"
            + (popProbes.Count > 0 ? $"; population {SweepFiles.First(popSha!, 12)} for {string.Join(", ", popSorted)}, the other probes on the whole order" : ""),
            "",
            "| candidate | changes | " + string.Join(" | ", probes) + " | rules | classification |",
            "|---|---|" + string.Concat(Enumerable.Repeat("---|", probes.Count)) + "---|---|",
        };
        var ranking = new List<(int cls, long newDefects, int violated, long minusRepaired, int displaced, string stem)>();
        foreach (string cand in cands)
        {
            string stem = SweepFiles.Stem(cand);
            var rowList = (List<object?>)results[stem]!;
            var rows = AsRows(rowList);
            string cls = Classify(rows);
            var cf = Doc(cand);
            string changes = Joined("; ", Changes(cf));
            if (changes.Length == 0) changes = "(none listed)";
            var cells = new List<string>();
            foreach (var r in rows)
            {
                var diff = (PyDict)r["diff"]!;
                var c = (PyDict)diff["counts"]!;
                var cr = (PyDict)r["created"]!;
                cells.Add($"{((string)diff["verdict"]!).Replace("DIFFERENTIAL-", "")} r{Count(c, "repaired")} c{Count(c, "created")}"
                          + $"(new {cr["newDefect"]}/attr {cr["attributed"]}/unm {cr["unmeasured"]}) q{Count(c, "reclassified")} u{Count(c, "unmeasured")}"
                          + (PyValue.Truthy(r["probeRefused"]) ? " REFUSED" : "")
                          + (r["probeExit"] is long exit && exit != 0 ? $" exit {exit}" : ""));
            }
            var ru = RulesFor(stem);
            var violates = (List<object?>)ru["violates"]!;
            var satisfies = (List<object?>)ru["satisfies"]!;
            string rulesCell = violates.Count > 0 ? "violates " + string.Join("; ", violates.Select(v => ((string)v!).Split(" — ")[0]))
                : satisfies.Count > 0 ? "satisfies " + satisfies.Count : "—";
            lines.Add($"| {stem} | {changes} | " + string.Join(" | ", cells) + $" | {rulesCell} | **{cls}** |");
            summaryCandidates[stem] = CandidateEntry(cand, rowList, cls);
            long newDefects = rows.Sum(r => (long)PyValue.Int(((PyDict)r["created"]!)["newDefect"]));
            long repaired = rows.Sum(r => Count((PyDict)((PyDict)r["diff"]!)["counts"]!, "repaired"));
            int displaced = PyValue.Len(Changes(cf));
            // Rank: class, then new defects, then rules violated, then repairs, then displacement.
            ranking.Add((cls == "PASSES-COMPLETED-CHECKS" ? 0 : cls == "UNRESOLVED" ? 1 : 2, newDefects, violates.Count, -repaired, displaced, stem));
        }
        lines.AddRange(["", "Cells read: conflictdiff verdict; r = repaired (physical predicate Holds -> Gone); c = created (entered with the "
            + "predicate holding), split by the reverse measurement into NEW physical defects (a regression), ATTRIBUTED "
            + "(the same physical state under the baseline order — the class's history-dependent attribution, not an engine "
            + "difference) and UNMEASURED; q = reclassified (changed hands, defect intact); u = unmeasured.", ""]);
        foreach (string cand in cands)
        {
            string stem = SweepFiles.Stem(cand);
            var rows = AsRows(results[stem]);
            var deltas = PyOrder.SortedByCodePoint(rows.SelectMany(r => ((List<object?>)((PyDict)r["diff"]!)["delta"]!).Select(d => (string)d!)).Distinct());
            if (deltas.Count > 0) lines.Add($"- `{stem}` configuration delta vs baseline: " + string.Join("; ", deltas));
            var ex = rows.SelectMany(r => ((PyDict)r["created"]!).Get("newExamples") is List<object?> l ? l.Select(e => (string)e!) : []).ToList();
            if (ex.Count > 0) lines.Add("  - new physical defects, first rows: " + string.Join("; ", ex.Take(5).Select(e => $"`{e}`")));
        }
        lines.AddRange(["", "## What was established", ""]);
        var passing = cands.Select(SweepFiles.Stem)
            .Where(s => ((PyDict)summaryCandidates[s]!)["classification"] as string == "PASSES-COMPLETED-CHECKS").ToList();
        if (passing.Count > 0)
        {
            lines.Add($"- {passing.Count} candidate(s) pass the completed checks within this scope: " + string.Join(", ", passing.Select(s => $"`{s}`")) + ".");
            lines.Add("  Passing the completed checks is a statement about the probes named above, in the population and state they ran in; it is not a compatibility claim beyond them.");
        }
        else lines.Add($"- No candidate passes the completed checks within this budget. **{NotProof}**");
        ranking.Sort((x, y) =>
        {
            int c = x.cls.CompareTo(y.cls);
            if (c == 0) c = x.newDefects.CompareTo(y.newDefects);
            if (c == 0) c = x.violated.CompareTo(y.violated);
            if (c == 0) c = x.minusRepaired.CompareTo(y.minusRepaired);
            if (c == 0) c = x.displaced.CompareTo(y.displaced);
            return c != 0 ? c : PyOrder.CompareCodePoints(x.stem, y.stem);
        });
        // A candidate is worth changing to only when it repairs something the baseline's completed checks flag.
        var improving = ranking.Where(r => r.cls == 0 && -r.minusRepaired > 0).ToList();
        if (improving.Count > 0)
        {
            var best = improving[0];
            var entry = (PyDict)summaryCandidates[best.stem]!;
            var viol = (List<object?>)((PyDict)entry["rules"]!)["violates"]!;
            lines.Add($"- What the user would change first: `{best.stem}` — {entry["changes"]}: repairs {-best.minusRepaired} row(s) of the baseline's completed checks and creates no new physical defect"
                      + (viol.Count > 0 ? $"; **but it violates {viol.Count} stated rule(s)**: " + string.Join(" | ", viol.Select(v => (string)v!)) + " — the user's call, not the tool's." : "."));
        }
        else if (passing.Count > 0)
            lines.Add("- No passing candidate repairs anything the completed checks flag in the baseline: the passing candidates are alternatives that do not regress, and the user's own order stands.");
        lines.AddRange(["", "## Search outcome (from the manifest)", ""]);
        if (manifest.ContainsKey("_note")) lines.Add($"- {PyText.Str(manifest["_note"])}");
        else
            foreach (var (k, v) in manifest)
                if (PyText.Lower(k) is "budget" or "contradictions" or "limits" or "unsatisfiableadvice" or "search" or "outcome" or "result")
                    lines.Add($"- {k}: {SweepFiles.First(PyJson.Dumps(v), 600)}");
        lines.AddRange(["", $"Cache decisions: `{PyPath.Name(cache.Log)}`; per-run logs: `logs/`; the RunLog history carries every child's timing."]);
        string report = string.Join("\n", lines);
        SweepFiles.WriteText(PyPath.Join(outDir, "REPORT.md"), report + "\n");
        SweepFiles.WriteText(PyPath.Join(outDir, "summary.json"), PyJson.Dumps(summary, indent: 1));
        SweepFiles.Print(report);
        return 0;
    }
}
