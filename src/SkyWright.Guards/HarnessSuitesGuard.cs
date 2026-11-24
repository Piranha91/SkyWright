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
/// It needs the solution built first (probe exes, conflictdiff), and runs the probes of the build it sits in. It counts
/// nothing and keeps no baseline: a suite's own exit code is its verdict.
/// </summary>
internal static class HarnessSuitesGuard
{
    internal sealed record Config(string[] PythonFolders, string[] PythonSingles, string[] SelfTestFlags,
                                  string[] SuiteProbes, (string Path, string Why)[] Excluded, (string Path, string Why)[] FullOnly,
                                  (string Path, string[] Args, string Why)[]? WithArgs = null);

    internal sealed record Runner(string Name, string Exe, string[] Args, bool FullOnly);

    internal static readonly Config SkyWright = new(
        PythonFolders: new[] { "_geometry-harness/tests", "_geometry-harness/viewprobe/tools", "_geometry-harness/planeassess" },
        PythonSingles: Array.Empty<string>(),
        SelfTestFlags: new[] { "--self-test", "--moved-ref-selftest" },
        SuiteProbes: new[] { "crossprobe", "proofprobe" },
        Excluded: new[]
        {
            ("_geometry-harness/tests/test_replay_command.py", "replays a recorded command against a real MO2 instance: needs --exe, --instance, --profile, --game and --cell"),
            ("_geometry-harness/tests/test_run_records.py", "reads a run store given by --store, or renders a fixture given by --render: not a self-contained runner"),
            ("_geometry-harness/tests/test_effect_shapes.py", "renders the capture fixture over a Skyrim SE game folder's own archives (HOUSECARL_FIXTURE_GAME or fixtureGame in the harness config); without them it reports NOT RUN and exits 3 by design, never 0"),
            ("_geometry-harness/tests/test_effects_path_rule.py", "renders the capture fixture over a Skyrim SE game folder's own archives (HOUSECARL_FIXTURE_GAME or fixtureGame in the harness config); without them it reports NOT RUN and exits 3 by design, never 0"),
            ("_geometry-harness/tests/test_eye_inside_solid.py", "renders the capture fixture over a Skyrim SE game folder's own archives (HOUSECARL_FIXTURE_GAME or fixtureGame in the harness config); without them it reports NOT RUN and exits 3 by design, never 0"),
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
            ("_geometry-harness/tests/test_capture_fallbacks.py", new[] { "--code-only" }, "its render cases need a Skyrim SE game folder's own archives; its code cases (CF13, CF20-CF24 and on) run here"),
        });

    public static int RunGuard(string[] args)
    {
        bool full = args.Contains("--full");
        Console.WriteLine("================================================================");
        Console.WriteLine($" harness-suites guard — SkyWright's known-answer suites ({(full ? "full" : "default level; --full adds the slow ones")})");
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
        var (runners, problems) = Plan(repo, SkyWright, build.Root);
        foreach (var p in problems) Check(false, p);
        foreach (var (path, reason) in SkyWright.Excluded) Console.WriteLine($"  excluded  {path} — {reason}");
        foreach (var (path, a, reason) in SkyWright.WithArgs ?? Array.Empty<(string, string[], string)>()) Console.WriteLine($"  with args {path} {string.Join(" ", a)} — {reason}");

        foreach (var r in runners)
        {
            if (r.FullOnly && !full) { Console.WriteLine($"  --full    {r.Name} (not run at the default level)"); continue; }
            var (code, secs, tail) = Run(repo, r);
            Check(code == 0, $"{r.Name} exited {code} in {secs:0.0}s");
            if (code != 0) foreach (var line in tail) Console.WriteLine("        | " + line);
        }
        Console.WriteLine(fail == 0 ? "\n  harness suites: all passed" : $"\n  harness suites: {fail} failed");
        return fail == 0 ? 0 : 1;
    }

    /// <summary>The runners a tree holds under a configuration, and every reason the plan itself is unsound.</summary>
    internal static (List<Runner> Runners, List<string> Problems) Plan(string root, Config cfg, string? build = null)
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
            if (!File.Exists(Path.Combine(root, path)) && !Directory.Exists(Path.Combine(root, "_geometry-harness", path)))
                problems.Add($"the --full runner {path} names nothing in this tree");

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
                runners.Add(new Runner(extra.Length > 0 ? rel + " " + string.Join(" ", extra) : rel, "python", new[] { rel }.Concat(extra).ToArray(), fullOnly.Contains(rel)));
            }
        }
        foreach (var single in cfg.PythonSingles)
        {
            if (!File.Exists(Path.Combine(root, single))) { problems.Add($"the runner {single} does not exist"); continue; }
            runners.Add(new Runner(single, "python", new[] { single }, fullOnly.Contains(Norm(single))));
        }

        var harness = Path.Combine(root, "_geometry-harness");
        foreach (var flag in cfg.SelfTestFlags)
        {
            var hits = Directory.Exists(harness)
                ? Directory.GetDirectories(harness).OrderBy(d => d, StringComparer.Ordinal)
                    .Where(d => File.Exists(Path.Combine(d, "Program.cs")) && File.ReadAllText(Path.Combine(d, "Program.cs")).Contains($"\"{flag}\""))
                    .ToList()
                : new List<string>();
            if (hits.Count == 0) { problems.Add($"no probe carries the self-test flag {flag}"); continue; }
            foreach (var d in hits)
            {
                var probe = Path.GetFileName(d);
                runners.Add(new Runner($"{probe} {flag}", ProbeExe(build, probe), new[] { flag }, fullOnly.Contains(probe)));
            }
        }
        foreach (var probe in cfg.SuiteProbes)
        {
            var d = Path.Combine(harness, probe);
            if (!Directory.Exists(d)) { problems.Add($"the suite probe {probe} does not exist"); continue; }
            runners.Add(new Runner(probe, ProbeExe(build, probe), Array.Empty<string>(), fullOnly.Contains(probe)));
        }
        return (runners, problems);
    }

    /// <summary>The probe's exe in the build, where the build's layout puts it (the run then fails, named, when it is not built).</summary>
    static string ProbeExe(string? build, string probe) =>
        build is null ? Path.Combine("(no build)", probe + ".exe") : CheckoutLayout.ProgramExe(build, probe);

    static (int Code, double Secs, List<string> Tail) Run(string repo, Runner r)
    {
        var sw = Stopwatch.StartNew();
        var tail = new Queue<string>();
        void Keep(string? s) { if (s is null) return; lock (tail) { tail.Enqueue(s); while (tail.Count > 25) tail.Dequeue(); } }
        if (r.Exe != "python" && !File.Exists(r.Exe)) return (-1, 0, new List<string> { $"not built: {r.Exe}" });
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
            return (-2, sw.Elapsed.TotalSeconds, tail.ToList());
        }
        p.WaitForExit();
        return (p.ExitCode, sw.Elapsed.TotalSeconds, tail.ToList());
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

            var (_, flagProblems) = Plan(root, cfg with { SelfTestFlags = new[] { "--no-such-selftest" } });
            yield return (flagProblems.Any(p => p.Contains("no probe carries the self-test flag --no-such-selftest")),
                          "a self-test flag that no probe carries fails the plan: " + string.Join("; ", flagProblems));
        }
        finally { try { Directory.Delete(root, true); } catch { /* temp cleanup best-effort */ } }
    }

    static string Norm(string p) => p.Replace('\\', '/');
}
