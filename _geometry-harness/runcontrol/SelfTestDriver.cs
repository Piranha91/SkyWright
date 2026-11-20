using System.Diagnostics;
using System.Text;
using HousecarlCore.Progress;

namespace RunControl;

// The driver loop and the child launch: the cases of tests/test_evalpipeline.py that run the driver over stand-in
// stage bodies (2321, 2323, 2326, 2327) or a stand-in runner (60, 62, 63), and the three built stage bodies run over
// an instance with a stand-in scopeprobe.
public static partial class SelfTest
{
    // A run-log record that writes nothing: a test's run is not a run of this machine.
    static Run QuietRun(string mode)
    {
        bool was = RunLog.Enabled;
        RunLog.Enabled = false;
        try { return RunLog.Begin("evalpipeline", new RunOptions { Mode = mode }); }
        finally { RunLog.Enabled = was; }
    }

    // One driver run with its lines captured: (exit code, standard output, standard error).
    static (int exit, string stdout, string stderr) Drive(Driver driver, PipelineArgs args)
    {
        TextWriter o = Report.Out, e = Report.Err;
        var stdout = new StringWriter();
        var stderr = new StringWriter();
        Report.Out = stdout;
        Report.Err = stderr;
        try
        {
            int exit;
            try { exit = driver.Run(args); }
            catch (Refusal r)
            {
                stderr.WriteLine($"REFUSED: {r.Message}");
                exit = Exit.Refused;
            }
            return (exit, stdout.ToString(), stderr.ToString());
        }
        finally
        {
            Report.Out = o;
            Report.Err = e;
        }
    }

    // The driver over a stand-in for every stage through bundle: each body records its call, writes one file and
    // accepts, unless `hook` gives it a sentence to refuse with.
    sealed class StandInPipeline(string outDir, PyDict? res = null, PyDict? machine = null, Func<string, RunContext, string, string?>? hook = null)
    {
        public string Out { get; } = outDir;
        public List<string> Calls { get; } = [];

        public (int exit, List<string> ran) Run(PyDict entry)
        {
            Calls.Clear();
            var bodies = new Dictionary<string, StageBody>();
            foreach (string name in Stages.Ported)
                bodies[name] = (ctx, _, sdir) =>
                {
                    Calls.Add(name);
                    if (hook?.Invoke(name, ctx, sdir) is { } why) throw new Refusal(why);
                    string f = PyPath.Join(sdir, name + ".json");
                    File.WriteAllText(f, "{}");
                    return (new PyDict(), [f]);
                };
            var driver = new Driver
            {
                Bodies = bodies, OpenRun = QuietRun, SetsRunLogFolder = false,
                MakeContext = a => new RunContext(entry, machine ?? new PyDict(), a.Out!, "m")
                {
                    ResolveWith = () => D(Py.Copy(res ?? Json("{\"arms\": [], \"subject\": [\"S.esp\"], \"keys\": []}"))),
                },
            };
            int exit = Drive(driver, new PipelineArgs(Entry: "e.json", Out: Out, Through: "bundle")).exit;
            return (exit, Calls.ToList());
        }
    }

    static string Ran(List<string> stages) => string.Join(" ", stages);

    // Three plugins whose headers state 100, 10 and 7 records, and two arms over them.
    static PyDict CountedRes(Temp t)
    {
        var headers = new PyDict();
        foreach (var (name, n) in new[] { ("Base.esm", 100u), ("A.esp", 10u), ("B.esp", 7u) })
        {
            WritePlugin(t.At("plugins/" + name), records: n);
            headers[name] = new PyDict { ["path"] = t.At("plugins/" + name) };
        }
        var res = Json("{\"baseline\": [\"Base.esm\"], \"subject\": [\"A.esp\"], \"keys\": [], \"arms\": [{\"id\": \"subject\", \"plugins\": [\"Base.esm\", \"A.esp\"]}, "
                       + "{\"id\": \"combined\", \"plugins\": [\"Base.esm\", \"A.esp\", \"B.esp\"]}]}");
        res["headers"] = headers;
        return res;
    }

    static void DriverCases()
    {
        Case("case 2321 a patch search change does not rerun the bundle; a mod name change does", () =>
        {
            // the stages from report-plan on are the Python pipeline's, so here a patch search reruns nothing
            using var t = new Temp();
            var p = new StandInPipeline(t.At("run"));
            var basis = Json("{\"evaluation\": {\"budgetMinutes\": 5}, \"modNames\": {\"S.esp\": \"S\"}}");
            var searched = new PyDict(basis) { ["patchSearch"] = "X:/ps.json" };
            var renamed = new PyDict(searched) { ["modNames"] = Json("{\"S.esp\": \"Ess\"}") };
            var (rc1, first) = p.Run(basis);
            var (_, afterSearch) = p.Run(searched);
            var (_, afterNames) = p.Run(renamed);
            if (rc1 != Exit.Ok || Ran(first) != Ran(Stages.Ported.ToList())) return $"the first run ran {Ran(first)} (exit {rc1})";
            if (afterSearch.Count > 0) return $"a patch-search change re-ran {Ran(afterSearch)}";
            return Ran(afterNames) == "bundle" ? null : $"a mod-name change re-ran {Ran(afterNames)}";
        });
        Case("case 2323 a refused stage keeps its accepted receipt so restoring the input restores it", () =>
        {
            using var t = new Temp();
            var p = new StandInPipeline(t.At("run"), hook: (name, _, sdir) =>
            {
                if (name != "bundle") return null;
                if (File.Exists(PyPath.Join(sdir, "made"))) return "bundle/evidence exists from an earlier bundle; move it aside rather than overwrite it.";
                File.WriteAllText(PyPath.Join(sdir, "made"), "1");
                return null;
            });
            var e = Json("{\"evaluation\": {\"budgetMinutes\": 5}, \"modNames\": {\"S.esp\": \"S\"}}");
            var (rc1, first) = p.Run(e);
            var (rc2, changed) = p.Run(new PyDict(e) { ["modNames"] = Json("{\"S.esp\": \"Ess\"}") });
            var (rc3, restored) = p.Run(e);
            if (rc1 != Exit.Ok || Ran(first) != Ran(Stages.Ported.ToList())) return $"the first run ran {Ran(first)} (exit {rc1})";
            if (rc2 != Exit.Refused || Ran(changed) != "bundle") return $"the changed entry ran {Ran(changed)} (exit {rc2})";
            if (rc3 != Exit.Ok || restored.Count > 0) return $"restoring the entry re-ran {Ran(restored)} (exit {rc3})";
            var bundle = D(new StageLedger(p.Out).Doc["stages"]).Get("bundle") as PyDict;
            return bundle?.Get("accepted") is true ? null : "the bundle's accepted receipt is not back in stages.json";
        });
        Case("case 2326 a stage receipt records the bytes it added with each file once", () =>
        {
            using var t = new Temp();
            Directory.CreateDirectory(t.At("u"));
            File.WriteAllBytes(t.At("u/a.bin"), new byte[1000]);
            NativeFile.HardLink(t.At("u/b.bin"), t.At("u/a.bin"));
            File.WriteAllBytes(t.At("u/c.bin"), new byte[500]);
            if (Hashing.UniqueBytes(t.At("u")) != 1500) return $"unique bytes {Hashing.UniqueBytes(t.At("u"))}, expected 1500 (a link is one file)";
            var res = CountedRes(t);
            if (DiskForecast.EvaluatedRecords(res) != 27) return $"evaluated records {DiskForecast.EvaluatedRecords(res)}, expected 10 + 17";
            var p = new StandInPipeline(t.At("run"), res, hook: (name, ctx, sdir) =>
            {
                if (name == "records") File.WriteAllBytes(PyPath.Join(sdir, "transcript.bin"), new byte[4000]);
                if (name == "bundle") NativeFile.HardLink(PyPath.Join(sdir, "transcript.bin"), PyPath.Join(ctx.Out, "records", "transcript.bin"));
                return null;
            });
            var (rc, _) = p.Run(Json("{\"evaluation\": {}}"));
            if (rc != Exit.Ok) return $"the run did not finish (exit {rc})";
            var stages = D(new StageLedger(p.Out).Doc["stages"]);
            string records = PyJson.Dumps(D(stages["records"]).Get("disk")), bundle = PyJson.Dumps(D(D(stages["bundle"])["disk"]).Get("addedBytes"));
            return Same("records stage disk", records, "{\"addedBytes\": 4002, \"evaluatedRecords\": 27}") ?? Same("the bundle's added bytes", bundle, "2");
        });
        Case("case 2327 the disk forecast uses measured figures, names them and keeps the floor", () =>
        {
            using var t = new Temp();
            void FigureRun(string name, long recordsBytes)
            {
                Directory.CreateDirectory(t.At("runs/" + name));
                File.WriteAllText(t.At($"runs/{name}/stages.json"),
                    "{\"schema\": 1, \"history\": [], \"stages\": {\"records\": {\"accepted\": true, \"disk\": {\"addedBytes\": " + recordsBytes
                    + ", \"evaluatedRecords\": 100}}, \"bundle\": {\"accepted\": true, \"disk\": {\"addedBytes\": 500, \"evaluatedRecords\": 100}}}}");
            }
            string outDir = t.At("runs/this");
            Directory.CreateDirectory(outDir);
            FigureRun("older", 2000);
            FigureRun("newer", 3000);
            string drive = Path.GetPathRoot(outDir)!.TrimEnd('\\');
            var (line, shortBy) = DiskForecast.Forecast(new RunContext(new PyDict(), new PyDict(), outDir, "m"), 1000, ["records", "bundle"]);
            if (shortBy != null) return $"a 35 KB forecast refused: {shortBy}";
            if (!line.Contains("about 34.2 KiB") || !line.Contains("newer") || !line.Contains("records") || line.Contains("older"))
                return $"the forecast does not use and name the larger figure (30 B per record, newer): {line}";
            const double gib = 1L << 30;
            double need = 1e9 * 35, free = new DriveInfo(Path.GetPathRoot(outDir)!).AvailableFreeSpace / gib;
            double floor = Math.Max(free - need / gib / 2, 0);
            var slot = new PyDict { ["heavyPrefix"] = Py.Objs(["python", "idle_lock.py", "--min-disk", $"{drive}={PyText.FloatRepr(floor)}"]) };
            (line, shortBy) = DiskForecast.Forecast(new RunContext(new PyDict(), slot, outDir, "m"), 1_000_000_000, ["records", "bundle"]);
            if (shortBy == null || !shortBy.Contains("newer") || !shortBy.Contains($"{PyText.General(floor)} GiB floor") || !shortBy.Contains("32.6 GiB"))
                return $"the floor was not kept, or the refusal does not name the figure's run: {shortBy}";
            string empty = t.At("alone/this");
            Directory.CreateDirectory(empty);
            (line, shortBy) = DiskForecast.Forecast(new RunContext(new PyDict(), new PyDict(), empty, "m"), 1000, ["records"]);
            if (shortBy != null || !line.Contains("no measured per-record figure")) return $"no figure must be said plainly: {line}";
            var figure = Json("{\"diskPerRecord\": {\"bytes\": 17920, \"source\": \"Hroggar 2026-11-15 unique bytes\"}}");
            (line, shortBy) = DiskForecast.Forecast(new RunContext(new PyDict(), figure, empty, "m"), 1000, ["records"]);
            if (shortBy != null || !line.Contains("Hroggar 2026-11-15") || !line.Contains("17.5 KiB")) return $"diskPerRecord not used: {line}";
            var machine = Json("{\"diskPerRecord\": {\"bytes\": 1000000, \"source\": \"a test figure\"}}");
            machine["heavyPrefix"] = Py.Objs(["--min-disk", $"{drive}=1000000000"]);
            var p = new StandInPipeline(t.At("alone/run"), CountedRes(t), machine);
            var (rc, ran) = p.Run(Json("{\"evaluation\": {}}"));
            if (rc != Exit.Refused || ran.Count > 0) return $"a short disk must stop the run before any stage, got exit {rc}, ran {Ran(ran)}";
            var status = D(PyJson.ReadPipelineFile(PyPath.Join(p.Out, "status.json")));
            return (status.Get("sentence") as string ?? "").Contains("a test figure") ? null : $"the refusal does not name its figure: {PyJson.Dumps(status)}";
        });
        Case("driver: a stage with no body stops the run as an unbuilt stage, and report-plan or later is refused", () =>
        {
            using var t = new Temp();
            var entry = Json("{\"evaluation\": {}}");
            var driver = new Driver
            {
                Bodies = new() { ["census"] = (_, _, sdir) => { File.WriteAllText(PyPath.Join(sdir, "c.json"), "{}"); return (null, [PyPath.Join(sdir, "c.json")]); } },
                OpenRun = QuietRun, SetsRunLogFolder = false,
                MakeContext = a => new RunContext(entry, new PyDict(), a.Out!, "m") { ResolveWith = () => Json("{\"arms\": [], \"subject\": [], \"keys\": []}") },
            };
            var (exit, stdout, _) = Drive(driver, new PipelineArgs(Entry: "e.json", Out: t.At("run"), Through: "scope"));
            var status = D(PyJson.ReadPipelineFile(t.At("run/status.json")));
            if (exit != Exit.Refused || !stdout.Contains("prepare: not built in this version; the run stops here.") || status.Get("stoppedAt") as string != "prepare")
                return $"exit {exit}, status {PyJson.Dumps(status)}, said {stdout}";
            var (exit2, _, stderr) = Drive(driver, new PipelineArgs(Entry: "e.json", Out: t.At("run2"), Through: null));
            status = D(PyJson.ReadPipelineFile(t.At("run2/status.json")));
            return exit2 == Exit.Refused && stderr.Contains("evalpipeline.py") && stderr.Contains("deliver") && status.Get("stoppedAt") as string == "census"
                ? null : $"exit {exit2}: {stderr}";
        });
        Case("driver: a non-blocking refusal lets the run go on and keeps it incomplete; an awaited judgement stops it", () =>
        {
            using var t = new Temp();
            var entry = Json("{\"evaluation\": {}}");
            var ran = new List<string>();
            StageBody Writes(string name) => (_, _, sdir) =>
            {
                ran.Add(name);
                File.WriteAllText(PyPath.Join(sdir, "o.json"), "{}");
                return (null, [PyPath.Join(sdir, "o.json")]);
            };
            Driver With(StageBody prepare) => new()
            {
                Bodies = new() { ["census"] = Writes("census"), ["prepare"] = prepare, ["scope"] = Writes("scope") },
                OpenRun = QuietRun, SetsRunLogFolder = false,
                MakeContext = a => new RunContext(entry, new PyDict(), a.Out!, "m") { ResolveWith = () => Json("{\"arms\": [], \"subject\": [], \"keys\": []}") },
            };
            var args = new PipelineArgs(Entry: "e.json", Out: t.At("run"), Through: "scope");
            var (exit, stdout, _) = Drive(With((_, _, _) => throw new NonBlockingRefusal("nothing to prepare here.")), args);
            var status = D(PyJson.ReadPipelineFile(t.At("run/status.json")));
            if (exit != Exit.Ok || Ran(ran) != "census scope" || !stdout.Contains("prepare: refused, not blocking: nothing to prepare here."))
                return $"exit {exit}, ran {Ran(ran)}: {stdout}";
            string? why = Same("status", PyJson.Dumps(WithoutAt(status, [])),
                               "{\"status\": \"incomplete\", \"through\": \"scope\", \"sentence\": \"nothing to prepare here.\", "
                               + "\"refusals\": [{\"stage\": \"prepare\", \"sentence\": \"nothing to prepare here.\"}], "
                               + "\"diskForecast\": \"disk not forecast for census..scope: no measured per-record figure exists (no earlier receipt of these "
                               + "stages carries one, and the machine file names no diskPerRecord).\"}");
            if (why != null) return why;
            ran.Clear();
            (exit, stdout, _) = Drive(With((_, _, _) => throw new Awaiting("waiting for judgement.json.")), args);
            status = D(PyJson.ReadPipelineFile(t.At("run/status.json")));
            var last = D(L(new StageLedger(t.At("run")).Doc["history"])[^1]);
            return exit == Exit.Judgement && ran.Count == 0 && stdout.Contains("prepare: AWAITING: waiting for judgement.json.")
                   && status["status"] as string == "awaiting-judgement" && last["reason"] as string == "awaiting: waiting for judgement.json."
                ? null : $"exit {exit}, ran {Ran(ran)}: {stdout} {PyJson.Dumps(status)}";
        });
        Case("driver: a run folder path too long for the probes is refused before anything is written", () =>
        {
            using var t = new Temp();
            string deep = t.At(string.Join("/", Enumerable.Repeat("a-long-folder-name", 9)));
            var (exit, _, stderr) = Drive(new Driver { OpenRun = QuietRun, SetsRunLogFolder = false }, new PipelineArgs(Entry: "e.json", Out: deep, Through: "scope"));
            return exit == Exit.Refused && stderr.Contains("choose a shorter --out") && !Directory.Exists(deep) ? null : $"exit {exit}: {stderr}";
        });
    }

    // ---- cases 60, 62, 63: a heavy child and the machine's slot command, over a stand-in runner

    static void ChildCases()
    {
        var saved = ChildLaunch.RunJob;
        RunContext Slot(Temp t, params string[] prefix) =>
            new(new PyDict(), new PyDict { ["heavyPrefix"] = Py.Objs(prefix), ["lockDir"] = t.Root }, t.Root, "m");
        string After(IReadOnlyList<string> argv, string flag) => argv[argv.ToList().IndexOf(flag) + 1];

        Case("case 60 a heavy child holds the slot for its own hold minutes", () =>
        {
            using var t = new Temp();
            var seen = new List<ChildJob>();
            ChildLaunch.RunJob = (_, _, _, job) => { seen.Add(job); return new ChildResult(job.Id, 0, 0.0, null, 1); };
            try
            {
                var ctx = Slot(t, "python", "idle_lock.py", "hold", "--lock", "{lock}", "--purpose", "{purpose}", "--max-min", "180", "--min-free-gb", "16");
                ChildLaunch.Run(ctx, t.Root, "records-capture", ["x"], heavy: true, holdMin: 460L);
                ChildLaunch.Run(ctx, t.Root, "evaluate-01", ["x"], heavy: true);
            }
            finally { ChildLaunch.RunJob = saved; }
            var a1 = seen[0].Argv;
            var a2 = seen[1].Argv;
            if (After(a1, "--max-min") != "460" || a1.Count(x => x == "--max-min") != 1) return $"the hold is 460 min, once, got {string.Join(" ", a1.Take(12))}";
            if (After(a2, "--max-min") != "180") return "a child with no hold of its own keeps the machine's hold";
            if (After(a1, "--lock") != PyOrder.Fwd(PyPath.Join(t.Root, "HEAVY-LOCK-m-records-capture")) || After(a1, "--purpose") != "records-capture")
                return $"the lock and purpose are not this step's: {string.Join(" ", a1)}";
            return string.Join(" ", a1.Skip(a1.ToList().IndexOf("--") + 1)) == "x" ? null : "the job's own argv follows the prefix";
        });
        Case("case 62 a heavy child stopped from outside leaves no lock when its tree is confirmed gone, and its lock when it is not", () =>
        {
            // the runner is a stand-in here; a real tree stopped at its deadline is the child runner's own case
            using var t = new Temp();
            string lockFile = PyPath.Join(t.Root, "HEAVY-LOCK-m-evaluate-01");
            ChildResult Stopped(ChildJob job, bool treeGone)
            {
                File.WriteAllText(After(job.Argv, "--lock"), "{\"pid\": 999999}");
                return new ChildResult(job.Id, null, 1.0, null, 1, Stopped: "deadline", TreeStopped: treeGone);
            }
            var ctx = Slot(t, "slot", "--lock", "{lock}", "--purpose", "{purpose}", "--max-min", "180");
            TextWriter was = Report.Out;
            Report.Out = new StringWriter();
            try
            {
                ChildLaunch.RunJob = (_, _, _, job) => Stopped(job, treeGone: true);
                var r = ChildLaunch.Run(ctx, PyPath.Join(t.Root, "ledger"), "evaluate-01", ["x"], heavy: true, deadlineMin: 1L);
                if (r.Stopped != "deadline" || File.Exists(lockFile)) return "a lock is left after a confirmed tree stop";
                var log = PyText.SplitLines(File.ReadAllText(PyPath.Join(t.Root, "idle_lock.log"))).Select(Json).ToList();
                if (log.Count != 1 || log[0]["step"] as string != "released" || !S(log[0]["reason"]).Contains("outer-stop") || !Py.Eq(log[0]["heldPid"], 999999L)
                    || S(log[0]["lock"]) != lockFile || log[0]["by"] as string != "evalpipeline")
                    return $"idle_lock.log does not hold one release line naming the outer stop: {PyJson.Dumps(log.Cast<object?>().ToList())}";
                if (!Report.Out.ToString()!.Contains("released the stopped slot command's lock")) return "the release was not said";
                ChildLaunch.RunJob = (_, _, _, job) => Stopped(job, treeGone: false);
                string? why = Refuses(() => ChildLaunch.Run(ctx, PyPath.Join(t.Root, "ledger"), "evaluate-01", ["x"], heavy: true, deadlineMin: 1L), "not confirmed");
                return why ?? (File.Exists(lockFile) ? null : "an unconfirmed stop removed the lock");
            }
            finally
            {
                ChildLaunch.RunJob = saved;
                Report.Out = was;
            }
        });
        Case("case 63 the outer deadline sits past the slot command's own bounds", () =>
        {
            using var t = new Temp();
            var deadlines = new List<double?>();
            var argvs = new List<IReadOnlyList<string>>();
            ChildLaunch.RunJob = (_, _, deadline, job) =>
            {
                deadlines.Add(deadline);
                argvs.Add(job.Argv);
                return new ChildResult(job.Id, 0, 0.0, null, 1);
            };
            double now = ChildRunner.Now();
            try
            {
                var ctx = Slot(t, "python", "idle_lock.py", "hold", "--lock", "{lock}", "--purpose", "{purpose}", "--max-min", "180", "--wait-timeout-min", "240",
                               "--min-free-gb", "16");
                ChildLaunch.Run(ctx, t.Root, "records-capture", ["x"], heavy: true, deadlineMin: 460L, holdMin: 460L);
                ChildLaunch.Run(ctx, t.Root, "acquire", ["x"], heavy: true, deadlineMin: 60L);
                ChildLaunch.Run(ctx, t.Root, "plan", ["x"], deadlineMin: 90L);
            }
            finally { ChildLaunch.RunJob = saved; }
            double m1 = (deadlines[0]!.Value - now) / 60, m2 = (deadlines[1]!.Value - now) / 60, m3 = (deadlines[2]!.Value - now) / 60;
            if (After(argvs[0], "--max-min") != "460") return $"the hold is the stage's 460 min, got {string.Join(" ", argvs[0].Take(12))}";
            if (m1 <= 240 + 240 + 460) return $"the outer deadline ({m1:0} min) must sit past slot wait 240 + idle wait 240 + hold 460";
            if (After(argvs[1], "--max-min") != "60") return $"a child with only a deadline holds the slot that long, got {string.Join(" ", argvs[1].Take(12))}";
            if (m2 <= 240 + 240 + 60) return $"capture's outer deadline ({m2:0} min) must sit past its waits and its 60-min hold";
            return m3 is > 89 and <= 90.1 ? null : $"a light child keeps its own deadline, got {m3:0.0} min";
        });
        Case("child: a hold that is not a whole number is passed as Python writes it", () =>
        {
            using var t = new Temp();
            IReadOnlyList<string> argv = [];
            ChildLaunch.RunJob = (_, _, _, job) => { argv = job.Argv; return new ChildResult(job.Id, 0, 0.0, null, 1); };
            try { ChildLaunch.Run(Slot(t, "slot", "{lock}"), t.Root, "evaluate-01", ["x"], heavy: true, deadlineMin: 100.0, holdMin: 100.0); }
            finally { ChildLaunch.RunJob = saved; }
            return Same("argv", string.Join(" ", argv.Skip(2)), "--max-min 100.0 -- x");
        });
    }

    // ---- the three built stages over an instance, scopeprobe stood in for

    // What scopeprobe would write for one launch: the order it was given, its targets with one interior cell each.
    static ChildResult StandInScopeprobe(PyDict res, ChildJob job, List<string> launched)
    {
        var argv = job.Argv.ToList();
        launched.Add(job.Id);
        string Value(string flag) => argv[argv.IndexOf(flag) + 1];
        var headers = D(res["headers"]);
        PyDict order = argv.Contains("--order")
            ? D(PyJson.ReadPipelineFile(Value("--order")))
            : new PyDict(L(res["arms"]).Select(D).First(a => a["id"] as string == "combined").Select(kv =>
                new KeyValuePair<string, object?>(kv.Key == "plugins" ? "Plugins" : kv.Key == "mods" ? "Mods" : kv.Key, kv.Value)));
        var targets = Enumerable.Range(0, argv.Count - 1).Where(i => argv[i] == "--target").Select(i => argv[i + 1]).ToList();
        var doc = new PyDict
        {
            ["complete"] = true, ["walkFailures"] = new List<object?>(), ["unreadableRecords"] = new List<object?>(),
            ["configuration"] = new PyDict
            {
                ["fingerprint"] = "f",
                ["plugins"] = Strings(order["Plugins"]).Select(p => (object?)new PyDict
                {
                    ["Name"] = p, ["Path"] = D(headers[p])["path"], ["Sha256"] = D(headers[p])["sha256"],
                }).ToList(),
                ["providers"] = Strings(order["Mods"]).Select(m => (object?)new PyDict { ["Name"] = m, ["Kind"] = "mod" })
                    .Append(new PyDict { ["Name"] = "Data", ["Kind"] = "data" }).ToList(),
            },
            ["targets"] = targets.Select(name => (object?)new PyDict
            {
                ["name"] = name, ["sha256"] = D(headers[name])["sha256"],
                ["footprint"] = new PyDict
                {
                    ["placed"] = new PyDict { ["cells"] = new List<object?> { new PyDict { ["cell"] = "000802:Base.esm", ["kind"] = "interior", ["name"] = "Room" } } },
                    ["recordTypes"] = new PyDict { ["CELL"] = 1L },
                },
            }).ToList(),
            ["locationContributors"] = new PyDict { ["complete"] = true, ["locations"] = new List<object?>() },
            ["nearby"] = new PyDict { ["ring"] = 2L, ["plugins"] = new List<object?>(), ["alsoScanned"] = new List<object?>() },
        };
        PyJson.WritePipelineFile(Value("--json"), doc);
        return new ChildResult(job.Id, 0, 0.1, null, 1);
    }

    static readonly string[] BuiltStages = ["census", "prepare", "scope"];

    static void StageCases()
    {
        var saved = ChildLaunch.RunJob;
        Case("stages: census, prepare and scope run over an entry carrying every presentation key, and read none", () =>
        {
            using var t = new Temp();
            var (inst, game) = World(t, ShadowMods.Append(Mod("Idle", "Idle.esp")).ToArray(), ShadowEnabled, ShadowPlugins);
            var entry = InPlaceEntry(inst, game, ["Sel"], ["geometry.object-overlap"]);
            entry["modNames"] = Json("{\"Sel.esp\": \"The Selection\"}");
            entry["nexusNames"] = Json("{\"Sel.esp\": \"Sel on Nexus\"}");
            entry["patchSearch"] = "X:/patch-search.json";
            entry["report"] = Json("{\"mode\": \"full\", \"maxPages\": 30}");
            entry["captureSize"] = L(PyJson.Loads("[1920, 1080]"));
            var res = new RunContext(entry, new PyDict(), t.At("run")).Resolve();
            var launched = new List<string>();
            ChildLaunch.RunJob = (_, _, _, job) => StandInScopeprobe(res, job, launched);
            try
            {
                Driver Driver(PyDict e) => new()
                {
                    OpenRun = QuietRun, SetsRunLogFolder = false, MakeContext = a => new RunContext(e, new PyDict(), a.Out!),
                };
                var args = new PipelineArgs(Entry: "e.json", Out: t.At("run"), Through: "scope");
                var (exit, stdout, stderr) = Drive(Driver(entry), args);
                if (exit != Exit.Ok) return $"exit {exit}: {stderr}{stdout}";
                foreach (string s in BuiltStages)
                    if (!stdout.Contains($"{s}: accepted in ")) return $"{s} was not accepted: {stdout}";
                string? why = Same("children", string.Join(" ", launched), "census-scope scope-candidate-00-baseline scope-candidate-01")
                              ?? Same("scope line", S(D(PyJson.ReadPipelineFile(t.At("run/scope/scope-line.json")))["line"]),
                                      "Sel edits 1 interior (Room); no other enabled plugin besides the base game touches those cells or the 2 cells around them "
                                      + "(persistent references placed into those cells are not read).")
                              ?? Same("census cells", PyJson.Dumps(D(PyJson.ReadPipelineFile(t.At("run/census/census.json")))["cells"]), "{\"Sel.esp\": [\"000802:Base.esm\"]}")
                              ?? Same("status", S(D(PyJson.ReadPipelineFile(t.At("run/status.json")))["status"]), "through scope");
                if (why != null) return why;
                string scan = File.ReadAllText(t.At("run/preparation-key.json"));
                if (!scan.Contains("idle.esp")) return "the preparation key does not hold the disabled, unticked mod's plugin the scope also reads";
                // other presentation values: no measuring stage is bound to them, so nothing runs again
                launched.Clear();
                var changed = new PyDict(entry) { ["modNames"] = Json("{\"Sel.esp\": \"Renamed\"}"), ["patchSearch"] = "X:/other.json" };
                changed.Pop("captureSize");
                (exit, stdout, stderr) = Drive(Driver(changed), args);
                if (exit != Exit.Ok || launched.Count > 0) return $"a presentation change ran {string.Join(" ", launched)} (exit {exit}): {stderr}";
                foreach (string s in BuiltStages)
                    if (!stdout.Contains($"{s}: reused (inputs and outputs unchanged)")) return $"{s} was not reused: {stdout}";
                // a measuring key: every stage runs again
                var other = new PyDict(entry) { ["evaluation"] = Json("{\"select\": [\"geometry.object-overlap\"], \"depth\": \"deep\"}") };
                (exit, stdout, _) = Drive(Driver(other), args);
                if (exit != Exit.Ok || launched.Count != 3) return $"a changed measuring key ran {launched.Count} children (exit {exit})";
                // a stage reading a presentation key the table does not declare for it is refused by the accessor
                var ctx = new RunContext(entry, new PyDict(), t.At("run"));
                return Rows(BuiltStages, s => Refuses(() => ctx.Entry.Presentation(s, "modNames"), s))
                       ?? Refuses(() => ctx.Entry.Get("patchSearch"), "patchSearch");
            }
            finally { ChildLaunch.RunJob = saved; }
        });
        Case("stages: a second run in the Work Folder takes the first run's preparation over, and a changed file does not", () =>
        {
            using var t = new Temp();
            var (inst, game) = World(t, ShadowMods, ShadowEnabled, ShadowPlugins);
            var entry = InPlaceEntry(inst, game, ["Sel"], ["geometry.object-overlap"]);
            var res = new RunContext(entry, new PyDict(), t.At("work/r1/run")).Resolve();
            var launched = new List<string>();
            ChildLaunch.RunJob = (_, _, _, job) => StandInScopeprobe(res, job, launched);
            try
            {
                (int exit, string stdout, string stderr) Run(string name) =>
                    Drive(new Driver { OpenRun = QuietRun, SetsRunLogFolder = false, MakeContext = a => new RunContext(entry, new PyDict(), a.Out!) },
                          new PipelineArgs(Entry: "e.json", Out: t.At($"work/{name}/run"), Through: "scope", ReusePreparationFrom: t.At("work")));
                var first = Run("r1");
                if (first.exit != Exit.Ok || !first.stdout.Contains("preparation: not reused: no earlier run in the Work Folder recorded its preparation"))
                    return $"the first run: exit {first.exit}: {first.stdout}{first.stderr}";
                launched.Clear();
                var second = Run("r2");
                if (second.exit != Exit.Ok || launched.Count > 0) return $"the second run launched {string.Join(" ", launched)} (exit {second.exit}): {second.stderr}";
                if (!second.stdout.Contains("preparation: reused from r1 (same profile, selection, files and probe build)")) return second.stdout;
                foreach (string s in BuiltStages)
                    if (!second.stdout.Contains($"{s}: reused from an earlier run")) return $"{s} was not taken over: {second.stdout}";
                var scope = D(D(new StageLedger(t.At("work/r2/run")).Doc["stages"])["scope"]);
                string? why = Same("reusedFrom", scope["reusedFrom"] as string, PyOrder.Fwd(t.At("work/r1/run")))
                              ?? (D(scope["outputs"]).Keys.All(k => k.StartsWith(PyOrder.Fwd(t.At("work/r2/run")))) ? null : "a seeded receipt names the earlier run's files")
                              ?? (File.ReadAllText(t.At("work/r2/run/scope/scopes.json")).Contains("/r1/") ? "a seeded file still names the earlier run" : null);
                if (why != null) return why;
                File.AppendAllText(PyPath.Join(inst, "profiles", "P", "modlist.txt"), "# touched\r\n");
                var third = Run("r3");
                return third.exit == Exit.Ok && launched.Count == 3 && third.stdout.Contains("preparation: not reused: r2 used this profile and selection, but a profile file changed")
                    ? null : $"after a profile file changed: exit {third.exit}, {launched.Count} children: {third.stdout}";
            }
            finally { ChildLaunch.RunJob = saved; }
        });
        // a selection with no plugin: the census is skipped, and the scope line says what its folders hold and what is measured
        string? AssetOnlyLine(string[] files, string expected)
        {
            using var t = new Temp();
            var (inst, game) = World(t, [new ModFolder("Tex"), Mod("A", "A.esp")], ["Tex", "A"], ["A.esp"]);
            foreach (string f in files)
            {
                string at = PyPath.Join(inst, "mods", "Tex", f);
                Directory.CreateDirectory(Path.GetDirectoryName(at)!);
                File.WriteAllBytes(at, [0]);
            }
            var entry = InPlaceEntry(inst, game, ["Tex"], ["geometry.object-overlap"]);
            var res = new RunContext(entry, new PyDict(), t.At("run")).Resolve();
            var launched = new List<string>();
            ChildLaunch.RunJob = (_, _, _, job) => StandInScopeprobe(res, job, launched);
            try
            {
                var (exit, stdout, stderr) = Drive(new Driver { OpenRun = QuietRun, SetsRunLogFolder = false, MakeContext = a => new RunContext(entry, new PyDict(), a.Out!) },
                                                   new PipelineArgs(Entry: "e.json", Out: t.At("run"), Through: "scope"));
                if (exit != Exit.Ok) return $"exit {exit}: {stderr}";
                return (stdout.Contains("census: the selection provides no plugin; skipped, and the plan runs without a scope") ? null : stdout)
                       ?? Same("children", string.Join(" ", launched), "scope-candidate-00-baseline scope-candidate-01")
                       ?? Same("scope line", S(D(PyJson.ReadPipelineFile(t.At("run/scope/scope-line.json")))["line"]), expected);
            }
            finally { ChildLaunch.RunJob = saved; }
        }
        Case("stages: a selection with only textures says no check can measure it", () => AssetOnlyLine(["textures/a/b.dds"],
            "Tex has no plugin and no mesh (1 texture file), so no check here can measure it: textures, sounds and scripts change nothing these checks read."));
        Case("stages: an empty selected folder says no check can measure it", () => AssetOnlyLine([],
            "Tex has no plugin and no mesh (0 texture files), so no check here can measure it: textures, sounds and scripts change nothing these checks read."));
        Case("stages: a selection with meshes says the mesh checks compare the objects that use them", () => AssetOnlyLine(
            ["meshes/x/a.nif", "meshes/x/y/b.NIF", "meshes/x/notes.txt", "textures/t.dds", "textures/u/v.dds"],
            "Tex has no plugin, so it places nothing; it holds 2 mesh files. The checks that read meshes compare every placed object that uses one of its meshes, "
            + "with and without it. Its textures (2 files) and other files are not measured."));
        Case("stages: a selection with a mesh and an archive names both", () => AssetOnlyLine(["meshes/a.nif", "Tex.bsa"],
            "Tex has no plugin, so it places nothing; it holds 1 mesh file and 1 archive. The checks that read meshes compare every placed object that uses one of its meshes, "
            + "with and without it. Its textures (0 files) and other files are not measured."));
        Case("stages: a selection with only an archive names it", () => AssetOnlyLine(["Tex.BSA", "textures/t.dds"],
            "Tex has no plugin, so it places nothing; it holds 1 archive. The checks that read meshes compare every placed object that uses one of its meshes, "
            + "with and without it. Its textures (1 file) and other files are not measured."));
        Case("stages: a scope that stops on the order reader's refusal names it for the profile without the selection", () =>
        {
            using var t = new Temp();
            var (inst, game) = World(t, [new ModFolder("Lib", ("Lib.esm", ["Base.esm"])), new ModFolder("Uses", ("Uses.esp", ["Base.esm", "Lib.esm"]))],
                                     ["Uses", "Lib"], ["Lib.esm", "Uses.esp"]);
            var entry = InPlaceEntry(inst, game, ["Lib"], ["geometry.object-overlap"]);
            var res = new RunContext(entry, new PyDict(), t.At("run")).Resolve();
            var launched = new List<string>();
            ChildLaunch.RunJob = (_, outDir, _, job) =>
            {
                if (job.Id != "scope-candidate-00-baseline") return StandInScopeprobe(res, job, launched);
                Directory.CreateDirectory(outDir);
                File.WriteAllText(PyPath.Join(outDir, job.Id + ".txt"),
                    "$ scopeprobe\n(exit 2, 0.1s)\n\n   ! REFUSED:   MISSING MASTER: 1 plugin names a master the order does not load.\n     - 'Uses.esp' needs 'Lib.esm'\n     next line\n");
                return new ChildResult(job.Id, 2, 0.1, null, 1);
            };
            try
            {
                var (exit, _, stderr) = Drive(new Driver { OpenRun = QuietRun, SetsRunLogFolder = false, MakeContext = a => new RunContext(entry, new PyDict(), a.Out!) },
                                              new PipelineArgs(Entry: "e.json", Out: t.At("run"), Through: "scope"));
                const string want = "scope: REFUSED: the profile without the selected mods would not load: ! REFUSED: MISSING MASTER: 1 plugin names a master the order "
                                    + "does not load. - 'Uses.esp' needs 'Lib.esm'. Tick the mods that need the selection too, or pick another mod.";
                return exit == Exit.Refused ? Same("refusal", stderr.Trim(), want) : $"exit {exit}: {stderr}";
            }
            finally { ChildLaunch.RunJob = saved; }
        });
    }

    // ---- black box: this program as a child process, with the real scopeprobe when it is built

    static (int exit, string output) Self(string runlog, params string[] args)
    {
        var psi = new ProcessStartInfo(Environment.ProcessPath!)
        {
            RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false,
            StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8,
        };
        psi.Environment["HOUSECARL_RUNLOG_DIR"] = runlog;
        foreach (string a in args) psi.ArgumentList.Add(a);
        using var p = Process.Start(psi)!;
        var err = p.StandardError.ReadToEndAsync();
        string stdout = p.StandardOutput.ReadToEnd();
        p.WaitForExit();
        return (p.ExitCode, stdout + err.Result);
    }

    static void BlackBoxCases()
    {
        const string b6 = "case b6 a selected master with an active dependent is refused naming it";
        Case("black box: arms prints an in-place entry's two arms", () =>
        {
            using var t = new Temp();
            var (inst, game) = World(t, ShadowMods, ShadowEnabled, ShadowPlugins);
            PyJson.WritePipelineFile(t.At("entry.json"), InPlaceEntry(inst, game, ["Sel"], ["geometry.object-overlap"]));
            var (exit, output) = Self(t.At("runlog"), "arms", "--entry", t.At("entry.json"));
            string want = "baseline (3): Base.esm, A.esp, Dup.esp\n"
                          + "combined".PadRight(40) + " the profile as it is Sel.esp\n"
                          + "".PadRight(40) + " mods: Sel; Other; A\n"
                          + "without-selection".PadRight(40) + " the profile without the selected mods (baseline only)\n"
                          + "".PadRight(40) + " mods: Other; A\n";
            return exit == Exit.Ok ? Same("lines", output.Replace("\r\n", "\n"), want) : $"exit {exit}: {output}";
        });
        Case("black box: a flag the command does not take, and a stage that is not one, are refused in one sentence", () =>
        {
            using var t = new Temp();
            var a = Self(t.At("runlog"), "arms", "--entry", t.At("entry.json"), "--through", "scope");
            var b = Self(t.At("runlog"), "run", "--entry", t.At("entry.json"), "--out", t.At("run"), "--through", "everything");
            var c = Self(t.At("runlog"), "status");
            return a.exit == Exit.Refused && a.output.Contains("does not take --through") && b.exit == Exit.Refused && b.output.Contains("is not a stage")
                   && c.exit == Exit.Refused && c.output.Contains("needs --out") ? null : $"{a.exit} {a.output} | {b.exit} {b.output} | {c.exit} {c.output}";
        });
        string? probe = null;
        try { probe = File.Exists(Checkout.ScopeprobeExe) ? null : "scopeprobe is not built in this checkout"; }
        catch (Refusal r) { probe = r.Message; }
        if (probe != null)
        {
            Skip(b6, probe);
            return;
        }
        Case(b6, () =>
        {
            using var t = new Temp();
            var (inst, game) = World(t, [new ModFolder("Lib", ("Lib.esm", ["Base.esm"])), new ModFolder("Uses", ("Uses.esp", ["Base.esm", "Lib.esm"]))],
                                     ["Uses", "Lib"], ["Lib.esm", "Uses.esp"]);
            File.WriteAllText(PyPath.Join(inst, "ModOrganizer.ini"), "[General]\ngameName=Skyrim Special Edition\nselected_profile=@ByteArray(P)\n");
            PyJson.WritePipelineFile(t.At("entry.json"), InPlaceEntry(inst, game, ["Lib"], ["geometry.object-overlap"]));
            var (exit, output) = Self(t.At("runlog"), "run", "--entry", t.At("entry.json"), "--out", t.At("out"), "--through", "scope");
            if (exit != Exit.Refused) return $"exit {exit}: {(output.Length > 600 ? output[^600..] : output)}";
            if (!output.Contains("MISSING MASTER") || !output.Contains("Uses.esp"))
                return $"the refusal must come from the order check and name the dependent Uses.esp: {(output.Length > 600 ? output[^600..] : output)}";
            var status = Self(t.At("runlog"), "status", "--out", t.At("out"));
            return status.exit == Exit.Ok && status.output.Contains("census               accepted ") && status.output.Contains("scope                not accepted (")
                ? null : $"status: {status.output}";
        });
    }

    // ---- against a live Python: the seam between the two controllers on one run folder

    static void PipelineLiveCases()
    {
        const string name = "live: the Python pipeline reuses the census, prepare and scope this program accepted";
        string? root = AppContext.BaseDirectory;
        while (root != null && !File.Exists(Path.Combine(root, "housecarl.sln"))) root = Path.GetDirectoryName(root);
        string harness = root == null ? "" : Path.Combine(root, "_geometry-harness");
        string? reason = !File.Exists(Path.Combine(harness, "evalpipeline.py")) ? "this build is not inside a houseCARL checkout with the Python controller"
            : Python(harness, "--version") is not { exit: 0 } ? "python is not on PATH" : null;
        if (reason != null)
        {
            Skip(name, reason);
            return;
        }
        var saved = ChildLaunch.RunJob;
        Case(name, () =>
        {
            using var t = new Temp();
            var (inst, game) = World(t, ShadowMods, ShadowEnabled, ShadowPlugins);
            var entry = InPlaceEntry(inst, game, ["Sel"], ["geometry.object-overlap"]);
            entry["modNames"] = Json("{\"Sel.esp\": \"The Selection\"}");
            PyJson.WritePipelineFile(t.At("entry.json"), entry);
            var res = new RunContext(t.At("entry.json"), null, t.At("run")).Resolve();
            ChildLaunch.RunJob = (_, _, _, job) => StandInScopeprobe(res, job, []);
            try
            {
                var (exit, _, stderr) = Drive(new Driver { OpenRun = QuietRun, SetsRunLogFolder = false },
                                              new PipelineArgs(Entry: t.At("entry.json"), Out: t.At("run"), Through: "scope"));
                if (exit != Exit.Ok) return $"this program's run: exit {exit}: {stderr}";
            }
            finally { ChildLaunch.RunJob = saved; }
            var r = Python(harness, "-B", Path.Combine(harness, "evalpipeline.py"), "run", "--entry", t.At("entry.json"), "--out", t.At("run"), "--through", "scope");
            if (r is not { exit: 0 }) return $"python exited {r?.exit}: {r?.stderr}";
            return Rows(BuiltStages, s => r.Value.stdout.Contains($"{s}: reused (inputs and outputs unchanged)") ? null : $"Python ran {s} again: {r.Value.stdout}");
        });
    }
}
