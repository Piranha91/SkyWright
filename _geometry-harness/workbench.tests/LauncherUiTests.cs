using System.Diagnostics;
using System.Text.Json;
using HousecarlCore.Progress;
using Workbench;
using Xunit;

namespace WorkbenchTests;

/// <summary>The Start tab's real behaviour changes: aborting one probe stops only that probe's process, and free mode's
/// settings file is written, read back, and its absence or an unwritable folder is said.</summary>
[Trait("tier", "unit")]
public sealed class LauncherUiTests : IDisposable
{
    readonly string _root = Path.Combine(Path.GetTempPath(), "hc-wb-ui-" + Guid.NewGuid().ToString("N"));
    readonly List<Process> _procs = new();
    public LauncherUiTests() => Directory.CreateDirectory(_root);
    public void Dispose()
    {
        foreach (var p in _procs) { try { if (!p.HasExited) p.Kill(true); } catch { } p.Dispose(); }
        try { Directory.Delete(_root, true); } catch { }
    }

    Process Sleeper()
    {
        var p = Process.Start(new ProcessStartInfo("powershell.exe", "-NoProfile -Command Start-Sleep -Seconds 120") { UseShellExecute = false, CreateNoWindow = true })!;
        _procs.Add(p);
        return p;
    }

    void Snapshot(string store, string op, Process p, DateTimeOffset? started = null)
    {
        var dir = Path.Combine(store, "active"); Directory.CreateDirectory(dir);
        var r = new RunRecord
        {
            RunId = $"{op}-{Guid.NewGuid():N}", Op = op, Status = RunStatus.Running, StartedUtc = DateTimeOffset.UtcNow,
            Pid = p.Id, ProcessStartUtc = started ?? new DateTimeOffset(p.StartTime.ToUniversalTime()),
        };
        File.WriteAllText(Path.Combine(dir, r.RunId + ".json"), JsonSerializer.Serialize(r, RunContract.Json));
    }

    [Fact]
    public void Aborting_one_probe_stops_its_process_and_leaves_the_other_running()
    {
        var store = Path.Combine(_root, "runlog");
        var a = Sleeper(); var b = Sleeper();
        Snapshot(store, "aprobe", a); Snapshot(store, "bprobe", b);
        var stopped = ProbeRuns.Abort(store, "aprobe");
        Assert.Equal(new[] { a.Id }, stopped);
        Assert.True(a.WaitForExit(10_000), "the aborted probe is still running");
        Assert.False(b.HasExited, "the other probe was stopped too");
    }

    [Fact]
    public void A_reused_pid_is_never_killed()
    {
        var store = Path.Combine(_root, "runlog");
        var a = Sleeper();
        Snapshot(store, "aprobe", a, DateTimeOffset.UtcNow.AddHours(-3));          // the record's process started long ago: not this one
        Assert.Empty(ProbeRuns.Abort(store, "aprobe"));
        Assert.False(a.HasExited, "a process the record does not name was killed");
    }

    [Fact]
    public void A_probe_reporting_no_counters_says_so_and_shows_no_bar()
    {
        var store = Path.Combine(_root, "runlog");
        var a = Sleeper();
        Snapshot(store, "aprobe", a);
        var p = ProbeRuns.Of(store, "aprobe", aborted: false, runEnded: false, DateTimeOffset.UtcNow, first: null);
        Assert.Equal("running", p.State);
        Assert.Null(p.Fraction);
        Assert.Contains("does not report how far it is", p.Line);
    }

    [Fact]
    public void A_probe_between_passes_is_not_called_done_until_the_run_ends()
    {
        var store = Path.Combine(_root, "runlog"); Directory.CreateDirectory(store);
        var r = new RunRecord { RunId = "aprobe-1", Op = "aprobe", Status = RunStatus.Completed, Outcome = RunOutcome.Ok, StartedUtc = DateTimeOffset.UtcNow.AddSeconds(-40), WallSeconds = 38 };
        File.WriteAllText(Path.Combine(store, "history.jsonl"), JsonSerializer.Serialize(r, RunContract.Json) + "\n");
        var between = ProbeRuns.Of(store, "aprobe", aborted: false, runEnded: false, DateTimeOffset.UtcNow, first: null);
        Assert.Equal("waiting", between.State);
        Assert.Contains("1 run finished", between.Line);
        Assert.Equal("done", ProbeRuns.Of(store, "aprobe", aborted: false, runEnded: true, DateTimeOffset.UtcNow, first: null).State);
        // a failed run followed by a good one: the tile names the failed run's reason, not the last run's outcome
        var bad = new RunRecord { RunId = "aprobe-2", Op = "aprobe", Status = RunStatus.Completed, Outcome = RunOutcome.Refused, Error = "exit code 1", StartedUtc = DateTimeOffset.UtcNow.AddSeconds(-30), WallSeconds = 5 };
        var good = new RunRecord { RunId = "aprobe-3", Op = "aprobe", Status = RunStatus.Completed, Outcome = RunOutcome.Ok, StartedUtc = DateTimeOffset.UtcNow.AddSeconds(-20), WallSeconds = 5 };
        File.AppendAllLines(Path.Combine(store, "history.jsonl"), new[] { JsonSerializer.Serialize(bad, RunContract.Json), JsonSerializer.Serialize(good, RunContract.Json) });
        var mixed = ProbeRuns.Of(store, "aprobe", aborted: false, runEnded: true, DateTimeOffset.UtcNow, first: null);
        Assert.Equal("failed", mixed.State);
        Assert.Contains("1 of 3 runs did not finish (exit code 1)", mixed.Line);
    }

    [Fact]
    public void After_the_run_a_tile_says_the_ledgers_verdict_not_the_probes_raw_exit()
    {
        // the setup without the selected mods does not load their plugins: scopeprobe exits 1 there and the ledger accepts
        // the order's answer with the plugins stated as not measured; the tile must neither say failed nor plain clean
        var ev = Path.Combine(_root, "evaluation"); Directory.CreateDirectory(Path.Combine(ev, "receipts"));
        File.WriteAllText(Path.Combine(ev, "ledger.json"), """
            {"entries": {
              "c.x__baseline": {"checkId": "c.x", "armId": "baseline", "status": "completed", "detector": "no-finding", "receiptPath": "receipts/b.json"},
              "c.x__candidate-01": {"checkId": "c.x", "armId": "candidate-01", "status": "completed", "detector": "no-finding", "receiptPath": "receipts/c.json"},
              "c.y__baseline": {"checkId": "c.y", "armId": "baseline", "status": "failed", "cause": "exit-nonzero", "detail": "exit code 1"}}}
            """);
        File.WriteAllText(Path.Combine(ev, "receipts", "b.json"), """{"result": {"coverage": [{"Judged": 0, "Unjudgeable": {"not-loaded-by-this-arm": 1}}]}}""");
        File.WriteAllText(Path.Combine(ev, "receipts", "c.json"), """{"result": {"coverage": [{"Judged": 1, "Unjudgeable": {}}]}}""");
        var x = ProbeRuns.Verdict(ev, "c.x")!;
        Assert.Equal("done", x.State);
        Assert.Contains("without the selected mods: loads; the selected plugins are not in it, so they were not checked there", x.Line);
        Assert.Contains("with the selected mods: nothing found", x.Line);
        var y = ProbeRuns.Verdict(ev, "c.y")!;
        Assert.Equal("failed", y.State);
        Assert.Contains("exit code 1", y.Line);
        Assert.Null(ProbeRuns.Verdict(ev, "c.none"));
    }

    [Fact]
    public void A_checks_measurement_reverse_and_comparison_rows_on_one_setup_read_as_one_phrase()
    {
        // an ordersweep check records three rows on the setup with the selected mods (its measurement, the reverse
        // measurement and the comparison); the tile says that setup once, from the measurement row
        var ev = Path.Combine(_root, "evaluation-parts"); Directory.CreateDirectory(ev);
        File.WriteAllText(Path.Combine(ev, "ledger.json"), """
            {"entries": {
              "c.n__baseline__arm": {"checkId": "c.n", "armId": "baseline", "part": "arm", "status": "completed", "detector": "finding"},
              "c.n__candidate-01__diff": {"checkId": "c.n", "armId": "candidate-01", "part": "diff", "status": "completed", "detector": "inconclusive"},
              "c.n__candidate-01__arm": {"checkId": "c.n", "armId": "candidate-01", "part": "arm", "status": "completed", "detector": "no-finding"},
              "c.n__candidate-01__reverse": {"checkId": "c.n", "armId": "candidate-01", "part": "reverse", "status": "completed", "detector": "finding"}}}
            """);
        var v = ProbeRuns.Verdict(ev, "c.n")!;
        Assert.Equal("done. without the selected mods: found something; with the selected mods: nothing found.", v.Line);
        // a check with nothing to measure on one setup is not a failure (the lab's route-cut tile read "failed")
        File.WriteAllText(Path.Combine(ev, "ledger.json"), """
            {"entries": {
              "c.r__baseline": {"checkId": "c.r", "armId": "baseline", "status": "not-applicable"},
              "c.r__candidate-01": {"checkId": "c.r", "armId": "candidate-01", "status": "completed", "detector": "finding"}}}
            """);
        var r = ProbeRuns.Verdict(ev, "c.r")!;
        Assert.Equal("done", r.State);
        Assert.Equal("done. without the selected mods: not applicable; with the selected mods: found something.", r.Line);
    }

    [Fact]
    public void While_the_run_prepares_a_tile_says_so_and_never_waiting_its_turn()
    {
        // a single-probe run spends its first minute preparing; that is not a queue, and the plan's preflight run of the
        // same probe is neither this check's run nor a basis for its estimate
        var store = Path.Combine(_root, "runlog"); var active = Path.Combine(store, "active"); Directory.CreateDirectory(active);
        var t0 = DateTimeOffset.UtcNow.AddSeconds(-50);
        void Put(RunRecord r) => File.WriteAllText(Path.Combine(active, r.RunId + ".json"), JsonSerializer.Serialize(r, RunContract.Json));
        var pipe = new RunRecord { RunId = "evalpipeline-1", Op = "evalpipeline", Status = RunStatus.Running, StartedUtc = t0, Pid = Environment.ProcessId,
                                   Stage = "stages", Notes = { "stage census started " + t0.ToString("o"), "stage scope started " + t0.AddSeconds(30).ToString("o") } };
        pipe.Stages.Add(new RunStage { Id = "stages", Title = "pipeline stages", Total = 8, Processed = 2, Unit = "stages", StartedUtc = t0 });
        Put(pipe);
        var preflight = new RunRecord { RunId = "aprobe-pre", Op = "aprobe", Status = RunStatus.Completed, Outcome = RunOutcome.Ok, StartedUtc = t0.AddSeconds(5), WallSeconds = 1 };
        File.WriteAllText(Path.Combine(store, "history.jsonl"), JsonSerializer.Serialize(preflight, RunContract.Json) + Environment.NewLine);
        var prep = ProbeRuns.Of(store, "aprobe", aborted: false, runEnded: false, DateTimeOffset.UtcNow, first: null, pipelined: true);
        Assert.Equal("waiting", prep.State);
        Assert.Contains("preparing the run: checking that each load order loads (step 3 of 8)", prep.Line);
        Assert.DoesNotContain("waiting its turn", prep.Line);
        // the checks begin and another probe runs: now it really is waiting its turn, and the preflight run is not counted
        File.AppendAllLines(Path.Combine(store, "history.jsonl"), new[] { JsonSerializer.Serialize(new RunRecord { RunId = "eval-1", Op = "evalpipeline-evaluate-01", Status = RunStatus.Completed, Outcome = RunOutcome.Ok, StartedUtc = t0.AddSeconds(45) }, RunContract.Json) });
        Put(new RunRecord { RunId = "other-1", Op = "otherprobe", Status = RunStatus.Running, StartedUtc = t0.AddSeconds(46), Pid = Environment.ProcessId });
        var turn = ProbeRuns.Of(store, "aprobe", aborted: false, runEnded: false, DateTimeOffset.UtcNow, first: null, pipelined: true);
        Assert.Contains("waiting its turn: otherprobe is running", turn.Line);
        Assert.DoesNotContain("1 run finished", turn.Line);
        Assert.Equal("learning", ProbeRuns.First(new[] { Path.Combine(store, "history.jsonl") }, "aprobe", DateTimeOffset.UtcNow).Basis);
    }

    [Fact]
    public void Every_probe_adapter_shape_gets_a_bar_from_its_stage_counts()
    {
        // one probe of each adapter shape the launcher runs: a live run record with a counted stage gives the tile a real bar
        foreach (var kind in Launcher.ProbeKinds)
        {
            var c = Launcher.Registry().First(x => x.Kind == kind && x.Exe is not null);
            var store = Path.Combine(_root, "shape-" + kind); var active = Path.Combine(store, "active"); Directory.CreateDirectory(active);
            var r = new RunRecord { RunId = c.Exe + "-1", Op = c.Exe!, Status = RunStatus.Running, StartedUtc = DateTimeOffset.UtcNow.AddSeconds(-10),
                                    Pid = Environment.ProcessId, Stage = "work" };
            r.Stages.Add(new RunStage { Id = "work", Title = "reading plugins", Total = 4, Processed = 1, Unit = "plugins", StartedUtc = r.StartedUtc });
            File.WriteAllText(Path.Combine(active, r.RunId + ".json"), JsonSerializer.Serialize(r, RunContract.Json));
            var p = ProbeRuns.Of(store, c.Exe!, aborted: false, runEnded: false, DateTimeOffset.UtcNow, first: null);
            Assert.True(p.State == "running" && p.Fraction == 0.25, $"{kind} ({c.Exe}): {p.State} {p.Fraction} {p.Line}");
            Assert.Contains("1/4 plugins", p.Line);
        }
    }

    // ---- the run engine: the built-in one, and what an older settings file's choice does now ----

    [Fact]
    public void An_old_settings_file_loads_unchanged_and_runs_the_built_in_engine()
    {
        var path = Path.Combine(_root, LauncherSettings.FileName);
        var old = "{\n  \"instance\": \"X:/Lab\",\n  \"profile\": \"Default\",\n  \"game\": \"C:/Game\",\n  \"output\": \"X:/Out\",\n  \"workFolder\": null\n}";
        File.WriteAllText(path, old);
        var s = LauncherSettings.Load(path).Settings!;
        Assert.Equal(new LauncherSettings("X:/Lab", "Default", "C:/Game", "X:/Out", null), s);
        Assert.Null(s.Controller);
        // written back, the file is the old file: no new member appears
        Assert.Null(LauncherSettings.Save(path, s));
        Assert.Equal(old.ReplaceLineEndings(), File.ReadAllText(path).ReplaceLineEndings());
        Assert.Equal("runcontrol", new Launcher { SettingsPath = path }.Engine.Controller);
    }

    [Theory]
    [InlineData("python", true)]
    [InlineData("Python", true)]
    [InlineData("ruby", true)]
    [InlineData("csharp", false)]
    public async Task A_settings_file_that_names_a_run_engine_is_read_says_once_that_the_choice_is_gone_and_drops_it(string saved, bool said)
    {
        Assert.Null(Environment.GetEnvironmentVariable("WB_CONTROLLER"));
        var path = Path.Combine(_root, LauncherSettings.FileName);
        File.WriteAllText(path, "{\n  \"instance\": null,\n  \"controller\": \"" + saved + "\"\n}");
        Assert.Equal(saved, LauncherSettings.Load(path).Settings!.Controller);       // read, not refused
        var l = new Launcher { SettingsPath = path };
        var restored = await l.RestoreAsync();
        var note = $"the saved run engine '{saved}' is gone: runs use the built-in engine, and the setting is dropped the next time the settings are saved.";
        if (said) { Assert.Equal(note, restored); Assert.Equal(note, l.SettingsNote); }
        else { Assert.Equal("no saved instance to restore.", restored); Assert.Null(l.SettingsNote); }
        Assert.Equal("runcontrol", l.Engine.Controller);
        // the next save never writes a run engine
        Assert.Null(LauncherSettings.Save(path, LauncherSettings.Load(path).Settings! with { Output = "X:/Out" }));
        Assert.DoesNotContain("controller", File.ReadAllText(path));
    }

    [Fact]
    public async Task WB_CONTROLLER_is_not_obeyed_and_the_window_says_so()
    {
        var path = Path.Combine(_root, LauncherSettings.FileName);
        File.WriteAllText(path, "{\n  \"instance\": null\n}");
        Environment.SetEnvironmentVariable("WB_CONTROLLER", "python");
        try
        {
            var l = new Launcher { SettingsPath = path };
            var restored = await l.RestoreAsync();
            Assert.Equal("WB_CONTROLLER is set to 'python', but there is no choice of run engine any more: runs use the built-in one.", restored);
            Assert.Equal("runcontrol", l.Engine.Controller);
        }
        finally { Environment.SetEnvironmentVariable("WB_CONTROLLER", null); }
    }

    /// <summary>An empty file where runcontrol.exe would be built, for commands that are composed and not started.</summary>
    string PlacedExe()
    {
        var exe = Path.Combine(_root, "harness", "Tools", "runcontrol", "runcontrol.exe");
        Directory.CreateDirectory(Path.GetDirectoryName(exe)!); File.WriteAllText(exe, "");
        return exe;
    }

    [Fact]
    public void The_bundle_check_and_the_population_are_the_built_in_engines_commands()
    {
        var exe = PlacedExe();
        var cs = RunEngine.OfBuiltIn(exe);
        var v = cs.Verify(@"B:\bundle", @"W:\wb\bundle-verify.json");
        Assert.Equal(exe, v.FileName);
        Assert.Equal(new[] { "verify-bundle", @"B:\bundle", "--out", @"W:\wb\bundle-verify.json" }, v.ArgumentList);
        Assert.Equal(@"W:\wb\runlog", v.Environment["HOUSECARL_RUNLOG_DIR"]);

        var p = cs.Population(@"M:\p\Fix.esp", "Fix", @"I:\inst", @"W:\re\population.json");
        Assert.Equal(exe, p.FileName);
        Assert.Equal(new[] { "population", "--plugin", @"M:\p\Fix.esp", "--mod", "Fix", "--instance", @"I:\inst", "--out", @"W:\re\population.json" }, p.ArgumentList);
        Assert.Equal(@"W:\re\runlog", p.Environment["HOUSECARL_RUNLOG_DIR"]);

        // the harness folder is above the built-in engine's build; no command names Python or a .py path
        Assert.Equal(Path.Combine(_root, "harness"), cs.HarnessDir());
        foreach (var c in new[] { v, p }) Assert.DoesNotContain(c.ArgumentList.Prepend(c.FileName), a => a.EndsWith(".py", StringComparison.OrdinalIgnoreCase) || a.Contains("python", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void A_missing_built_in_engine_is_refused_in_one_sentence_naming_where_it_was_looked_for()
    {
        var gone = Path.Combine(_root, "nowhere", "runcontrol.exe");
        foreach (var act in new Action[] { () => RunEngine.OfBuiltIn(gone).Verify("b", Path.Combine(_root, "v.json")), () => RunEngine.OfBuiltIn(gone).Population("p", "m", "i", Path.Combine(_root, "o.json")),
                                           () => RunEngine.OfBuiltIn(gone).PackageDigest(_root, _root), () => RunEngine.OfBuiltIn(gone).ControllerExe() })
        {
            var ex = Assert.Throws<InvalidOperationException>(act);
            Assert.Equal($"the built-in run engine is not at '{gone}'; build the solution (scripts\\build.ps1), or set WB_RUNCONTROL to runcontrol.exe.", ex.Message);
        }
    }

    [Fact]
    public void The_built_in_engine_reads_a_build_folders_digest_from_identity_and_gives_none_when_it_fails()
    {
        // a stand-in for `runcontrol identity FOLDER`: prints the folder it was asked about inside the JSON's digest
        var ok = Path.Combine(_root, "identity-ok.cmd");
        File.WriteAllText(ok, "@echo off\r\necho {\r\necho  \"digest\": \"folder:abc123\",\r\necho  \"asked\": \"%1 %~nx2\"\r\necho }\r\n");
        var work = Path.Combine(_root, "recheck");
        Assert.Equal("folder:abc123", RunEngine.OfBuiltIn(ok).PackageDigest(Path.Combine(_root, "probe"), work));
        var bad = Path.Combine(_root, "identity-bad.cmd");
        File.WriteAllText(bad, "@echo off\r\necho REFUSED: no such folder 1>&2\r\nexit /b 2\r\n");
        Assert.Null(RunEngine.OfBuiltIn(bad).PackageDigest(Path.Combine(_root, "probe"), work));
        var odd = Path.Combine(_root, "identity-odd.cmd");
        File.WriteAllText(odd, "@echo off\r\necho not json\r\n");
        Assert.Null(RunEngine.OfBuiltIn(odd).PackageDigest(Path.Combine(_root, "probe"), work));
    }

    [Fact]
    public void The_settings_round_trip_and_an_unwritable_folder_is_said()
    {
        var path = Path.Combine(_root, LauncherSettings.FileName);
        Assert.Equal((null, null), LauncherSettings.Load(path));
        var s = new LauncherSettings("X:/Lab", "Default", "C:/Game", "X:/Out", null);
        Assert.Null(LauncherSettings.Save(path, s));
        Assert.Equal(s, LauncherSettings.Load(path).Settings);
        var blocked = Path.Combine(_root, "no-such-folder", LauncherSettings.FileName);
        var why = LauncherSettings.Save(blocked, s);
        Assert.NotNull(why);
        Assert.Contains("not writable", why);
        File.WriteAllText(path, "{ not json");
        Assert.Contains("could not be read", LauncherSettings.Load(path).Refusal);
    }

    [Fact]
    public void Probe_names_read_as_words()
    {
        Assert.Equal("Loadability: Masters and Order", ProbeNames.Of("loadability.masters-and-order"));
        Assert.Equal("Geometry: BOS Overlay", ProbeNames.Of("geometry.bos-overlay"));
        Assert.Equal("State: Switch Census", ProbeNames.Of("state.switch-census"));
    }

    [Fact]
    public void Every_probe_is_described_in_game_terms()
    {
        // the tile's line is for a modder: no subrecord signatures or tool-internal words
        var banned = new[] { "dependent", "host", "census", "XESP", "XLKR", "XAPR", "pass", " arm", "emit", "predicate", "provenance", "houseCARL", "Mutagen" };
        foreach (var c in Launcher.Registry().Where(c => Launcher.ProbeKinds.Contains(c.Kind)))
        {
            var says = ProbeNames.Says(c.Id);
            Assert.False(says.Length == 0, $"{c.Id} has no plain description");
            foreach (var b in banned) Assert.DoesNotContain(b, says, StringComparison.OrdinalIgnoreCase);
        }
        foreach (var k in new[] { "mcp-only", "derived", "manual", "media", "live-import", "unsupported" })
            foreach (var b in banned) Assert.DoesNotContain(b, ProbeNames.NotAProbe(k), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void The_mods_are_listed_as_MO2_lists_them_with_the_winner_last()
    {
        // modlist.txt order, strongest first; priority counts up from the weakest
        var mods = new[] { new LauncherMod("Winner", 3, true, false), new LauncherMod("Mid", 2, true, false), new LauncherMod("Group_separator", 1, true, true), new LauncherMod("Weakest", 0, true, false) };
        Assert.Equal(new[] { "Weakest", "Group_separator", "Mid", "Winner" }, LauncherView.InMo2Order(mods).Select(m => m.Name));
    }
}
