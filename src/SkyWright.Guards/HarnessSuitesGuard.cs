using System.Diagnostics;
using HarnessShared;

namespace SkyWright.Guards;

/// <summary>
/// Runs SkyWright's own known-answer suites that the build and the unit tests do not reach, and fails on any nonzero
/// exit. These are the Python test runners under the geometry harness and planeassess, and the probes' synthetic
/// self-tests. Moved from houseCARL's generator in the split's M2, with its rule unchanged.
///
/// Runners are FOUND, not listed: every <c>test_*.py</c> in the named folders, every probe whose Program.cs carries a
/// self-test flag, and the whole-run synthetic suites. A runner that cannot run here is excluded BY NAME, with the
/// reason; one that can run here with stated arguments (its code cases, say) gets an argument row instead, with the
/// reason. An exclusion or argument row that names nothing fails the guard, and so does a folder or flag that finds no runner: either
/// would let a moved file pass by running less. The slow runners run only with <c>--full</c>.
///
/// Runners run side by side, up to <c>--jobs N</c> at once (default 4; <c>--jobs 1</c> runs them one after another in
/// plan order, as before). A runner that cannot share the machine is marked to run alone, BY NAME, with the reason, in
/// the same data as the exclusions; it runs after the others, with nothing beside it. <c>--area app|module|all</c>
/// picks the app's runners (outside integration/housecarl) or the module's (inside it); the header says what the other
/// area holds, so a part is never read as the whole.
///
/// It needs the solution built first (probe exes, conflictdiff), and runs the probes of the build it sits in. It counts
/// nothing and keeps no baseline: a suite's own exit code is its verdict.
/// </summary>
internal static class HarnessSuitesGuard
{
    internal sealed record Config(string[] PythonFolders, string[] PythonSingles, string[] SelfTestFlags,
                                  string[] SuiteProbes, (string Path, string Why)[] Excluded, (string Path, string Why)[] FullOnly,
                                  (string Path, string[] Args, string Why)[]? WithArgs = null, string[]? ProbeRoots = null,
                                  (string Path, string Why)[]? Alone = null, string ModuleRoot = "integration/housecarl");

    /// <summary>One runner: <paramref name="Source"/> is the file or probe folder it comes from, relative to the checkout
    /// (its area is read from it); <paramref name="Alone"/> runs it with nothing beside it.</summary>
    internal sealed record Runner(string Name, string Exe, string[] Args, bool FullOnly, string Source = "", bool Alone = false);

    /// <summary>A runner's result: its exit code, its seconds, and the last lines it printed.</summary>
    internal sealed record Outcome(int Code, double Secs, List<string> Tail);

    internal enum Area { All, App, Module }

    const int DefaultJobs = 4;

    internal static readonly Config SkyWright = new(
        PythonFolders: new[] { "_geometry-harness/tests", "_geometry-harness/viewprobe/tools", "_geometry-harness/planeassess", "integration/housecarl/tests" },
        PythonSingles: Array.Empty<string>(),
        SelfTestFlags: new[] { "--self-test", "--moved-ref-selftest" },
        SuiteProbes: new[] { "crossprobe", "proofprobe" },
        Excluded: new[]
        {
            ("_geometry-harness/tests/test_replay_command.py", "replays a recorded command against a real MO2 instance: needs --exe, --instance, --profile, --game and --cell"),
            ("_geometry-harness/tests/test_run_records.py", "reads a run store given by --store, or renders a fixture given by --render: not a self-contained runner"),
            ("_geometry-harness/tests/test_effect_shapes.py", "renders the capture fixture over a Skyrim SE game folder's own archives (HOUSECARL_FIXTURE_GAME or fixtureGame in the harness config); without them it reports NOT RUN and exits 3 by design, never 0"),
            ("_geometry-harness/tests/test_effects_path_rule.py", "renders the capture fixture over a Skyrim SE game folder's own archives (HOUSECARL_FIXTURE_GAME or fixtureGame in the harness config); without them it reports NOT RUN and exits 3 by design, never 0"),
            ("integration/housecarl/tests/test_eye_inside_solid.py", "renders the capture fixture over a Skyrim SE game folder's own archives (HOUSECARL_FIXTURE_GAME or fixtureGame in the harness config); without them it reports NOT RUN and exits 3 by design, never 0"),
            ("_geometry-harness/tests/test_point_queries_answer_or_refuse.py", "renders the capture fixture over a Skyrim SE game folder's own archives (HOUSECARL_FIXTURE_GAME or fixtureGame in the harness config); without them it reports NOT RUN and exits 3 by design, never 0"),
            ("_geometry-harness/tests/test_ledge_link_counts.py", "renders the capture fixture over a Skyrim SE game folder's own archives (HOUSECARL_FIXTURE_GAME or fixtureGame in the harness config); without them it reports NOT RUN and exits 3 by design, never 0"),
            ("_geometry-harness/tests/test_route_solve_ledge_drop.py", "renders the capture fixture over a Skyrim SE game folder's own archives (HOUSECARL_FIXTURE_GAME or fixtureGame in the harness config); without them it reports NOT RUN and exits 3 by design, never 0"),
            ("_geometry-harness/tests/test_navgrid_rebuild.py", "its case 2512 reproduces a vanilla navmesh from the game's Data folder (HC_NAVGRID_DATA) and fails by name without it"),
            ("_geometry-harness/tests/test_route_geometry.py","renders the capture fixture over a Skyrim SE game folder's own archives (HOUSECARL_FIXTURE_GAME or fixtureGame in the harness config); without them it reports NOT RUN and exits 3 by design, never 0"),
        },
        FullOnly: new[]
        {
            ("_geometry-harness/tests/test_evalrun.py", "about eight minutes"),
            ("proofprobe", "about four minutes"),
        },
        WithArgs: new[]
        {
            ("integration/housecarl/tests/test_capture_fallbacks.py", new[] { "--code-only" }, "its render cases need a Skyrim SE game folder's own archives; its code cases (CF13, CF20-CF24 and on) run here"),
        },
        // the probes: SkyWright's, and the houseCARL integration module's (lookprobe), each run from its own build
        ProbeRoots: new[] { "_geometry-harness", "integration/housecarl" },
        Alone: new[]
        {
            ("_geometry-harness/tests/test_contact_memory_route.py", "its case CR-16 needs the unlimited contactprobe run to take the fast memory route, which the probe takes only while the machine's free physical memory covers the fast route's need; a runner beside it that holds memory turns that run lean and the case red"),
        });

    public static int RunGuard(string[] args)
    {
        bool full = args.Contains("--full");
        if (!TryOptions(args, out int jobs, out var area, out var bad)) { Console.WriteLine($"  FAIL  {bad}"); return 2; }
        Console.WriteLine("================================================================");
        Console.WriteLine($" harness-suites guard — SkyWright's known-answer suites ({(full ? "full" : "default level; --full adds the slow ones")}; {(jobs == 1 ? "one at a time" : $"up to {jobs} at once")})");
        Console.WriteLine("================================================================");
        int fail = 0;
        void Check(bool ok, string label) { Console.WriteLine((ok ? "  PASS  " : "  FAIL  ") + label); if (!ok) fail++; }

        // The guard's own rule first, on fixture trees: an empty folder and a stale exclusion must each fail.
        foreach (var (ok, label) in SelfCheck()) Check(ok, "self-check: " + label);
        if (args.Contains("--self-check")) return fail == 0 ? 0 : 1;   // the rule alone, without running the suites

        var repo = CheckoutLayout.FindRoot();
        if (repo is null)
        {
            Console.WriteLine($"  FAIL  no SkyWright checkout ({CheckoutLayout.Marker}) above {AppContext.BaseDirectory}; run the guard from a checkout's build.");
            return 1;
        }
        var build = CheckoutLayout.Locate(null, out var why);
        if (build is null)
        {
            Console.WriteLine($"  FAIL  {why}");
            return 1;
        }
        Console.WriteLine($"  build     {build.Root}");
        var moduleBuild = IntegrationLayout.Root(repo);
        Console.WriteLine($"  module    {moduleBuild}");
        var (runners, problems) = Plan(repo, SkyWright, build.Root, moduleBuild);
        foreach (var p in problems) Check(false, p);
        foreach (var (path, reason) in SkyWright.Excluded) Console.WriteLine($"  excluded  {path} — {reason}");
        foreach (var (path, a, reason) in SkyWright.WithArgs ?? Array.Empty<(string, string[], string)>()) Console.WriteLine($"  with args {path} {string.Join(" ", a)} — {reason}");
        foreach (var (path, reason) in SkyWright.Alone ?? Array.Empty<(string, string)>()) Console.WriteLine($"  alone     {path} — {reason}");

        var chosen = InArea(runners, SkyWright, area);
        Console.WriteLine("  area      " + AreaSentence(area, runners, chosen, SkyWright));
        int toRun = chosen.Count(r => full || !r.FullOnly), finished = 0;
        var wall = Stopwatch.StartNew();
        var results = Execute(chosen, jobs, full, r => Run(repo, r),
            (i, o) => Console.WriteLine($"  ..    [{++finished}/{toRun}] {chosen[i].Name}: exit {o.Code} in {o.Secs:0.0}s"));
        wall.Stop();

        Console.WriteLine("\n  results, in plan order:");
        fail += Report(chosen, results, Console.Out);
        Console.WriteLine($"\n  wall time {wall.Elapsed.TotalSeconds:0.0}s for {toRun} runners {(jobs == 1 ? "one at a time" : $"up to {jobs} at once")}; the runners' own times add up to {results.Sum(o => o?.Secs ?? 0):0.0}s");
        Console.WriteLine(fail == 0 ? "  harness suites: all passed" : $"  harness suites: {fail} failed");
        return fail == 0 ? 0 : 1;
    }

    /// <summary>The options --jobs N (1 or more) and --area app|module|all; false with one plain sentence when either is wrong.</summary>
    internal static bool TryOptions(string[] args, out int jobs, out Area area, out string bad)
    {
        jobs = DefaultJobs; area = Area.All; bad = "";
        for (int k = 0; k < args.Length; k++)
        {
            if (args[k] == "--jobs")
            {
                if (k + 1 >= args.Length || !int.TryParse(args[k + 1], out jobs) || jobs < 1)
                { bad = "--jobs needs a whole number of runners at once, 1 or more; try --jobs 4, or --jobs 1 to run them one after another."; return false; }
                k++;
            }
            else if (args[k] == "--area")
            {
                string? v = k + 1 < args.Length ? args[k + 1] : null;
                if (v is "all") area = Area.All;
                else if (v is "app") area = Area.App;
                else if (v is "module") area = Area.Module;
                else { bad = $"--area takes app, module or all, not {(v is null ? "nothing" : $"'{v}'")}; try --area app for the runners outside integration/housecarl."; return false; }
                k++;
            }
        }
        return true;
    }

    /// <summary>Whether a runner is the module's: its source sits under the module's root.</summary>
    static bool InModule(Runner r, Config cfg) => Norm(r.Source).StartsWith(cfg.ModuleRoot.TrimEnd('/') + "/", StringComparison.OrdinalIgnoreCase);

    /// <summary>The runners of one area, in plan order: app is every runner outside the module's root, module every one inside it.</summary>
    internal static List<Runner> InArea(IReadOnlyList<Runner> runners, Config cfg, Area area) => area switch
    {
        Area.App => runners.Where(r => !InModule(r, cfg)).ToList(),
        Area.Module => runners.Where(r => InModule(r, cfg)).ToList(),
        _ => runners.ToList(),
    };

    /// <summary>One plain sentence for the header: which area runs, and what the other area holds.</summary>
    internal static string AreaSentence(Area area, IReadOnlyList<Runner> all, IReadOnlyList<Runner> chosen, Config cfg)
    {
        int module = all.Count(r => InModule(r, cfg)), app = all.Count - module;
        return area switch
        {
            Area.App => $"app only: the {chosen.Count} runners outside {cfg.ModuleRoot}; the module's {module} runners are not run here, so this is not the whole check (--area all runs both).",
            Area.Module => $"module only: the {chosen.Count} runners under {cfg.ModuleRoot}; the app's {app} runners are not run here, so this is not the whole check (--area all runs both).",
            _ => $"all: {chosen.Count} runners, the app's {app} and the module's {module} (under {cfg.ModuleRoot}).",
        };
    }

    /// <summary>Runs the runners (the --full ones only when <paramref name="full"/>): with one job, one after another in
    /// plan order; with more, up to that many at once in plan order, then the ones marked alone, each with nothing beside
    /// it. <paramref name="done"/> is told of each as it finishes, one at a time. The outcomes come back by plan index,
    /// null for a runner not run.</summary>
    internal static Outcome?[] Execute(IReadOnlyList<Runner> runners, int jobs, bool full, Func<Runner, Outcome> run, Action<int, Outcome> done)
    {
        var results = new Outcome?[runners.Count];
        var gate = new object();
        void One(int i)
        {
            Outcome o;
            try { o = run(runners[i]); }
            catch (Exception ex) { o = new Outcome(-3, 0, new List<string> { $"could not be run: {ex.GetType().Name}: {ex.Message}" }); }
            lock (gate) { results[i] = o; done(i, o); }
        }
        var todo = Enumerable.Range(0, runners.Count).Where(i => full || !runners[i].FullOnly).ToList();
        if (jobs <= 1) { foreach (var i in todo) One(i); return results; }

        var beside = todo.Where(i => !runners[i].Alone).ToList();
        var alone = todo.Except(beside).ToList();
        var queue = new System.Collections.Concurrent.ConcurrentQueue<int>(beside);
        var workers = Enumerable.Range(0, Math.Min(jobs, beside.Count))
            .Select(_ => new Thread(() => { while (queue.TryDequeue(out var i)) One(i); }) { IsBackground = true }).ToList();
        foreach (var w in workers) w.Start();
        foreach (var w in workers) w.Join();
        foreach (var i in alone) One(i);
        return results;
    }

    /// <summary>The verdict lines in plan order, a failing runner's last lines under it; returns how many failed.</summary>
    internal static int Report(IReadOnlyList<Runner> runners, Outcome?[] results, TextWriter w)
    {
        int fail = 0;
        for (int i = 0; i < runners.Count; i++)
        {
            var (r, o) = (runners[i], results[i]);
            if (o is null) { w.WriteLine($"  --full    {r.Name} (not run at the default level)"); continue; }
            w.WriteLine((o.Code == 0 ? "  PASS  " : "  FAIL  ") + $"{r.Name} exited {o.Code} in {o.Secs:0.0}s");
            if (o.Code == 0) continue;
            fail++;
            foreach (var line in o.Tail) w.WriteLine("        | " + line);
        }
        return fail;
    }

    /// <summary>The runners a tree holds under a configuration, and every reason the plan itself is unsound.</summary>
    internal static (List<Runner> Runners, List<string> Problems) Plan(string root, Config cfg, string? build = null, string? moduleBuild = null)
    {
        var runners = new List<Runner>();
        var problems = new List<string>();
        var excluded = new HashSet<string>(cfg.Excluded.Select(e => Norm(e.Path)), StringComparer.OrdinalIgnoreCase);
        var fullOnly = new HashSet<string>(cfg.FullOnly.Select(e => Norm(e.Path)), StringComparer.OrdinalIgnoreCase);
        var withArgs = (cfg.WithArgs ?? Array.Empty<(string, string[], string)>()).ToDictionary(e => Norm(e.Item1), e => e.Item2, StringComparer.OrdinalIgnoreCase);

        foreach (var (path, _) in cfg.Excluded)
            if (!File.Exists(Path.Combine(root, path)))
                problems.Add($"the exclusion {path} names no file: a moved or renamed runner would hide behind it");
        foreach (var (path, _, _) in cfg.WithArgs ?? Array.Empty<(string, string[], string)>())
            if (!File.Exists(Path.Combine(root, path)))
                problems.Add($"the argument row {path} names no file: a moved or renamed runner would hide behind it");
        foreach (var (path, _) in cfg.FullOnly)
            if (!File.Exists(Path.Combine(root, path)) && !(cfg.ProbeRoots ?? new[] { "_geometry-harness" }).Any(pr => Directory.Exists(Path.Combine(root, pr, path))))
                problems.Add($"the --full runner {path} names nothing in this tree");
        var alone = new HashSet<string>((cfg.Alone ?? Array.Empty<(string, string)>()).Select(e => Norm(e.Item1)), StringComparer.OrdinalIgnoreCase);
        foreach (var (path, _) in cfg.Alone ?? Array.Empty<(string, string)>())
            if (!File.Exists(Path.Combine(root, path)) && !(cfg.ProbeRoots ?? new[] { "_geometry-harness" }).Any(pr => Directory.Exists(Path.Combine(root, pr, path))))
                problems.Add($"the run-alone mark {path} names no file: a moved or renamed runner would run beside others");

        foreach (var folder in cfg.PythonFolders)
        {
            var dir = Path.Combine(root, folder);
            var files = Directory.Exists(dir) ? Directory.GetFiles(dir, "test_*.py").OrderBy(f => f, StringComparer.Ordinal).ToArray() : Array.Empty<string>();
            if (files.Length == 0) { problems.Add($"no test_*.py runner found under {folder}"); continue; }
            foreach (var f in files)
            {
                var rel = Norm(Path.GetRelativePath(root, f));
                if (excluded.Contains(rel)) continue;
                var extra = withArgs.TryGetValue(rel, out var a) ? a : Array.Empty<string>();
                runners.Add(new Runner(extra.Length > 0 ? rel + " " + string.Join(" ", extra) : rel, "python", new[] { rel }.Concat(extra).ToArray(), fullOnly.Contains(rel), rel, alone.Contains(rel)));
            }
        }
        foreach (var single in cfg.PythonSingles)
        {
            if (!File.Exists(Path.Combine(root, single))) { problems.Add($"the runner {single} does not exist"); continue; }
            runners.Add(new Runner(single, "python", new[] { single }, fullOnly.Contains(Norm(single)), Norm(single), alone.Contains(Norm(single))));
        }

        // probe folders, in root order: the first root is SkyWright's (its build), any other the module's (its own build)
        var probeRoots = cfg.ProbeRoots ?? new[] { "_geometry-harness" };
        var probeDirs = new List<(string Dir, bool Module)>();
        for (int k = 0; k < probeRoots.Length; k++)
        {
            var pr = Path.Combine(root, probeRoots[k]);
            var dirs = Directory.Exists(pr) ? Directory.GetDirectories(pr).OrderBy(d => d, StringComparer.Ordinal).Where(d => File.Exists(Path.Combine(d, "Program.cs"))).ToList() : new List<string>();
            if (dirs.Count == 0) { problems.Add($"no probe (a folder with a Program.cs) found under {probeRoots[k]}"); continue; }
            probeDirs.AddRange(dirs.Select(d => (d, k > 0)));
        }
        foreach (var flag in cfg.SelfTestFlags)
        {
            var hits = probeDirs.Where(p => File.ReadAllText(Path.Combine(p.Dir, "Program.cs")).Contains($"\"{flag}\"")).ToList();
            if (hits.Count == 0) { problems.Add($"no probe carries the self-test flag {flag}"); continue; }
            foreach (var (d, module) in hits)
            {
                var probe = Path.GetFileName(d);
                runners.Add(new Runner($"{probe} {flag}", module ? ModuleProbeExe(moduleBuild, probe) : ProbeExe(build, probe), new[] { flag }, fullOnly.Contains(probe),
                                       Norm(Path.GetRelativePath(root, d)), alone.Contains(probe)));
            }
        }
        var harness = Path.Combine(root, "_geometry-harness");
        foreach (var probe in cfg.SuiteProbes)
        {
            var d = Path.Combine(harness, probe);
            if (!Directory.Exists(d)) { problems.Add($"the suite probe {probe} does not exist"); continue; }
            runners.Add(new Runner(probe, ProbeExe(build, probe), Array.Empty<string>(), fullOnly.Contains(probe), Norm(Path.GetRelativePath(root, d)), alone.Contains(probe)));
        }
        return (runners, problems);
    }

    /// <summary>The probe's exe in the build, where the build's layout puts it (the run then fails, named, when it is not built).</summary>
    static string ProbeExe(string? build, string probe) =>
        build is null ? Path.Combine("(no build)", probe + ".exe") : CheckoutLayout.ProgramExe(build, probe);

    /// <summary>A module probe's exe in the module's build, where its marker puts it.</summary>
    static string ModuleProbeExe(string? moduleBuild, string probe) =>
        moduleBuild is null ? Path.Combine("(no module build)", probe + ".exe") : IntegrationLayout.ProgramExe(moduleBuild, probe);

    static Outcome Run(string repo, Runner r)
    {
        var sw = Stopwatch.StartNew();
        var tail = new Queue<string>();
        void Keep(string? s) { if (s is null) return; lock (tail) { tail.Enqueue(s); while (tail.Count > 25) tail.Dequeue(); } }
        List<string> Tail() { lock (tail) return tail.ToList(); }
        if (r.Exe != "python" && !File.Exists(r.Exe)) return new Outcome(-1, 0, new List<string> { $"not built: {r.Exe}" });
        var psi = new ProcessStartInfo(r.Exe) { WorkingDirectory = repo, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        foreach (var a in r.Args) psi.ArgumentList.Add(a);
        psi.Environment["PYTHONIOENCODING"] = "utf-8";
        using var p = Process.Start(psi)!;
        p.OutputDataReceived += (_, e) => Keep(e.Data);
        p.ErrorDataReceived += (_, e) => Keep(e.Data);
        p.BeginOutputReadLine();
        p.BeginErrorReadLine();
        if (!p.WaitForExit(TimeSpan.FromMinutes(20)))
        {
            try { p.Kill(entireProcessTree: true); } catch { /* already gone */ }
            Keep("stopped after 20 minutes");
            return new Outcome(-2, sw.Elapsed.TotalSeconds, Tail());
        }
        p.WaitForExit();
        return new Outcome(p.ExitCode, sw.Elapsed.TotalSeconds, Tail());
    }

    /// <summary>The guard's own known answers, on fixture trees in a temp folder.</summary>
    internal static IEnumerable<(bool Ok, string Label)> SelfCheck()
    {
        var root = Path.Combine(Path.GetTempPath(), "hc-harness-suites-selfcheck-" + Guid.NewGuid().ToString("N"));
        try
        {
            void Touch(string rel, string text = "") { var p = Path.Combine(root, rel); Directory.CreateDirectory(Path.GetDirectoryName(p)!); File.WriteAllText(p, text); }
            Touch("t/test_a.py"); Touch("t/test_b.py"); Touch("s/test_one.py");
            Touch("_geometry-harness/probe1/Program.cs", "if (args[0] == \"--self-test\") {}");
            var cfg = new Config(new[] { "t" }, new[] { "s/test_one.py" }, new[] { "--self-test" }, Array.Empty<string>(),
                                 new[] { ("t/test_b.py", "fixture") }, Array.Empty<(string, string)>());

            var (ok, okProblems) = Plan(root, cfg);
            yield return (okProblems.Count == 0 && ok.Select(r => r.Name).SequenceEqual(new[] { "t/test_a.py", "s/test_one.py", "probe1 --self-test" }),
                          "a complete tree plans every runner but the excluded one: " + string.Join(", ", ok.Select(r => r.Name)) + (okProblems.Count > 0 ? " | " + string.Join("; ", okProblems) : ""));

            Directory.CreateDirectory(Path.Combine(root, "empty"));
            var (_, emptyProblems) = Plan(root, cfg with { PythonFolders = new[] { "t", "empty" } });
            yield return (emptyProblems.Any(p => p.Contains("no test_*.py runner found under empty")),
                          "a folder that holds no runner fails the plan: " + string.Join("; ", emptyProblems));

            var (_, staleProblems) = Plan(root, cfg with { Excluded = new[] { ("t/test_gone.py", "fixture") } });
            yield return (staleProblems.Any(p => p.Contains("t/test_gone.py names no file")),
                          "an exclusion that names no file fails the plan: " + string.Join("; ", staleProblems));

            var (argRun, argProblems) = Plan(root, cfg with { WithArgs = new[] { ("t/test_a.py", new[] { "--code-only" }, "fixture") } });
            var argRunner = argRun.FirstOrDefault(r => r.Args.Length > 0 && r.Args[0] == "t/test_a.py");
            yield return (argProblems.Count == 0 && argRunner is not null && argRunner.Args.SequenceEqual(new[] { "t/test_a.py", "--code-only" }),
                          "an argument row runs its runner with those arguments: " + (argRunner is null ? "not planned" : string.Join(" ", argRunner.Args)));

            var (_, staleArgProblems) = Plan(root, cfg with { WithArgs = new[] { ("t/test_gone.py", new[] { "--code-only" }, "fixture") } });
            yield return (staleArgProblems.Any(p => p.Contains("t/test_gone.py names no file")),
                          "an argument row that names no file fails the plan: " + string.Join("; ", staleArgProblems));

            Touch("mod/probe2/Program.cs", "if (args[0] == \"--self-test\") {}");
            var (two, twoProblems) = Plan(root, cfg with { ProbeRoots = new[] { "_geometry-harness", "mod" } }, "B", "M");
            var p2 = two.FirstOrDefault(r => r.Name == "probe2 --self-test");
            yield return (twoProblems.Count == 0 && p2 is not null && p2.Exe.StartsWith("M", StringComparison.Ordinal) && two.Any(r => r.Name == "probe1 --self-test" && r.Exe.StartsWith("B", StringComparison.Ordinal)),
                          "a probe under a second root runs from the module's build, the first root's from SkyWright's: " + string.Join(", ", two.Select(r => r.Name + "=" + r.Exe)) + (twoProblems.Count > 0 ? " | " + string.Join("; ", twoProblems) : ""));
            var (_, noRootProblems) = Plan(root, cfg with { ProbeRoots = new[] { "_geometry-harness", "empty" } });
            yield return (noRootProblems.Any(p => p.Contains("no probe (a folder with a Program.cs) found under empty")),
                          "a probe root that holds no probe fails the plan: " + string.Join("; ", noRootProblems));

            var (_, flagProblems) = Plan(root, cfg with { SelfTestFlags = new[] { "--no-such-selftest" } });
            yield return (flagProblems.Any(p => p.Contains("no probe carries the self-test flag --no-such-selftest")),
                          "a self-test flag that no probe carries fails the plan: " + string.Join("; ", flagProblems));

            // run-alone marks: by file or probe name, like the --full rows; one that names nothing fails the plan
            var (marked, markProblems) = Plan(root, cfg with { Alone = new[] { ("t/test_a.py", "fixture"), ("probe1", "fixture") } });
            var markedNames = marked.Where(r => r.Alone).Select(r => r.Name).ToList();
            yield return (markProblems.Count == 0 && markedNames.SequenceEqual(new[] { "t/test_a.py", "probe1 --self-test" }),
                          "a run-alone mark marks its runner, by file or by probe: " + string.Join(", ", markedNames) + (markProblems.Count > 0 ? " | " + string.Join("; ", markProblems) : ""));
            var (_, staleMarkProblems) = Plan(root, cfg with { Alone = new[] { ("t/test_gone.py", "fixture") } });
            yield return (staleMarkProblems.Any(p => p.Contains("run-alone mark t/test_gone.py names no file")),
                          "a run-alone mark that names no file fails the plan: " + string.Join("; ", staleMarkProblems));

            // the areas: by root, app outside the module's root and module inside it, together exactly the whole plan
            Touch("mod/tests/test_m.py");
            var areaCfg = cfg with { PythonFolders = new[] { "t", "mod/tests" }, ProbeRoots = new[] { "_geometry-harness", "mod" }, ModuleRoot = "mod" };
            var (whole, areaProblems) = Plan(root, areaCfg, "B", "M");
            var app = InArea(whole, areaCfg, Area.App);
            var module = InArea(whole, areaCfg, Area.Module);
            var both = InArea(whole, areaCfg, Area.All);
            bool sameAsWhole = app.Concat(module).Select(r => r.Name).OrderBy(n => n, StringComparer.Ordinal)
                                  .SequenceEqual(whole.Select(r => r.Name).OrderBy(n => n, StringComparer.Ordinal));
            yield return (areaProblems.Count == 0 && app.Count > 0 && module.Count > 0 && sameAsWhole && both.SequenceEqual(whole)
                          && app.All(r => !r.Source.StartsWith("mod/", StringComparison.Ordinal)) && module.All(r => r.Source.StartsWith("mod/", StringComparison.Ordinal))
                          && AreaSentence(Area.App, whole, app, areaCfg).Contains($"the module's {module.Count} runners are not run here"),
                          $"each area selects by root and the two make the whole: app {string.Join(", ", app.Select(r => r.Name))} | module {string.Join(", ", module.Select(r => r.Name))} | whole {whole.Count}"
                          + (areaProblems.Count > 0 ? " | " + string.Join("; ", areaProblems) : ""));

            // the run itself, on stand-in runners that each take a moment and note when they ran
            (Outcome?[] Results, List<(string Name, double Start, double End)> Spans, int Most, List<string> Order) Fake(IReadOnlyList<Runner> rs, int jobs, string? failing = null)
            {
                var clock = Stopwatch.StartNew();
                var spans = new List<(string, double, double)>();
                var order = new List<string>();
                int now = 0, most = 0;
                var g = new object();
                var res = Execute(rs, jobs, true, r =>
                {
                    double s;
                    lock (g) { s = clock.Elapsed.TotalMilliseconds; most = Math.Max(most, ++now); }
                    Thread.Sleep(150);
                    lock (g) { now--; spans.Add((r.Name, s, clock.Elapsed.TotalMilliseconds)); }
                    return new Outcome(r.Name == failing ? 1 : 0, 1.5, new List<string> { r.Name == failing ? "the stand-in failed here" : "ok" });
                }, (i, _) => order.Add(rs[i].Name));
                return (res, spans, most, order);
            }
            var six = Enumerable.Range(1, 6).Select(n => new Runner($"r{n}", "python", Array.Empty<string>(), false, $"t/r{n}.py", Alone: n == 3)).ToList();

            var four = Fake(six, 4);
            var a3 = four.Spans.Single(s => s.Name == "r3");
            var beside3 = four.Spans.Where(s => s.Name != "r3" && s.Start < a3.End && a3.Start < s.End).Select(s => s.Name).ToList();
            yield return (beside3.Count == 0 && four.Most >= 2,
                          $"a runner marked alone runs with nothing beside it, the others side by side: beside r3 {(beside3.Count == 0 ? "nothing" : string.Join(", ", beside3))}; most at once {four.Most}");

            var plain = six.Select(r => r with { Alone = false }).ToList();
            var failed = Fake(plain, 4, failing: "r4");
            var text = new StringWriter();
            int failCount = Report(plain, failed.Results, text);
            var lines = text.ToString().Split('\n').Select(l => l.TrimEnd('\r')).ToList();
            var verdicts = lines.Where(l => l.StartsWith("  PASS  ", StringComparison.Ordinal) || l.StartsWith("  FAIL  ", StringComparison.Ordinal)).Select(l => l[8..].Split(' ')[0]).ToList();
            yield return (failCount == 1 && failed.Most >= 2 && lines.Contains("  FAIL  r4 exited 1 in 1.5s") && lines.Contains("        | the stand-in failed here")
                          && verdicts.SequenceEqual(plain.Select(r => r.Name)),
                          $"a failing runner fails a parallel run, its lines shown, the verdicts in plan order: {failCount} failed, most at once {failed.Most}, order {string.Join(" ", verdicts)}");

            var one = Fake(six, 1);
            var again = Fake(six, 4);
            yield return (one.Order.SequenceEqual(six.Select(r => r.Name)) && one.Most == 1 && again.Most >= 2
                          && again.Order.OrderBy(n => n, StringComparer.Ordinal).SequenceEqual(one.Order.OrderBy(n => n, StringComparer.Ordinal)),
                          $"--jobs 1 and --jobs 4 run the same runners, --jobs 1 in plan order one at a time: {string.Join(" ", one.Order)} (most {one.Most}) | {string.Join(" ", again.Order)} (most {again.Most})");

            bool jobsRefused = !TryOptions(new[] { "--jobs", "0" }, out _, out _, out var jobsWhy) && jobsWhy.StartsWith("--jobs needs", StringComparison.Ordinal);
            bool areaRefused = !TryOptions(new[] { "--area", "apps" }, out _, out _, out var areaWhy) && areaWhy.Contains("not 'apps'");
            bool read = TryOptions(new[] { "--full", "--jobs", "2", "--area", "module" }, out int j2, out var a2, out _) && j2 == 2 && a2 == Area.Module;
            bool dflt = TryOptions(Array.Empty<string>(), out int jd, out var ad, out _) && jd == DefaultJobs && ad == Area.All;
            yield return (jobsRefused && areaRefused && read && dflt,
                          $"--jobs and --area are read, a wrong value refused in one sentence: {jobsWhy} | {areaWhy}");
        }
        finally { try { Directory.Delete(root, true); } catch { /* temp cleanup best-effort */ } }
    }

    static string Norm(string p) => p.Replace('\\', '/');
}
