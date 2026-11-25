using System.Diagnostics;
using System.IO.Compression;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using HarnessShared;
using HousecarlCore.Progress;

namespace RunControl;

// The known answers for `plan`: the rules it is made of, against evalrun.py's functions, and the command itself as a
// child process, against `evalrun.py plan` over the same small instance (known-answers/make_known_answers_plan.py).
public static partial class SelfTest
{
    static string Unmask(Temp t, string s) => s.Replace("<ROOT\\>", t.Root).Replace("<ROOT/>", PyOrder.Fwd(t.Root));
    static object? Unmask(Temp t, object? v) => v switch
    {
        string s => Unmask(t, s),
        PyDict d => new PyDict(d.Select(kv => new KeyValuePair<string, object?>(kv.Key, Unmask(t, kv.Value)))),
        List<object?> l => l.Select(x => Unmask(t, x)).ToList(),
        _ => v,
    };

    // A file's text that names the case's folder by the placeholder as JSON spells it, written out for this folder.
    static byte[] Materialise(Temp t, string text) =>
        PyJson.Utf8(text.Replace("<ROOT\\\\>", t.Root.Replace("\\", "\\\\")).Replace("<ROOT/>", PyOrder.Fwd(t.Root)));

    static void WriteTree(Temp t, PyDict tree)
    {
        foreach (var (rel, data) in D(tree["files"]))
        {
            string path = t.At(rel);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllBytes(path, B64(data));
        }
        foreach (string d in Strings(tree["dirs"])) Directory.CreateDirectory(t.At(d));
    }

    static string Gunzip(object? b64)
    {
        using var gz = new GZipStream(new MemoryStream(B64(b64)), CompressionMode.Decompress);
        using var r = new StreamReader(gz, Encoding.UTF8);
        return r.ReadToEnd();
    }

    // Where two long texts first differ, with what is round it.
    static string? SameText(string what, string? got, string? want)
    {
        if (got == want) return null;
        if (got == null || want == null) return $"{what}: got {Show(got)}, Python has {Show(want)}";
        int at = 0;
        while (at < got.Length && at < want.Length && got[at] == want[at]) at++;
        string Around(string s) => Show(s[Math.Max(0, Math.Min(at, s.Length) - 60)..Math.Min(s.Length, at + 100)]);
        return $"{what}: differs at {at}: got {Around(got)}, Python has {Around(want)}";
    }

    static void PlanCases()
    {
        PyDict ka;
        using (var s = Assembly.GetExecutingAssembly().GetManifestResourceStream("plan-command.json")!)
        using (var r = new StreamReader(s, Encoding.UTF8))
            ka = D(PyJson.Loads(r.ReadToEnd()));
        PlanUnitCases(D(ka["units"]));
        PlanCommandCases(D(ka["command"]));
        PlanStageCases();
    }

    // ---- the rules, against evalrun.py's functions

    static void PlanUnitCases(PyDict units)
    {
        Case("plan: arm_delta, the names a set of orders differ by", () => Rows(L(units["armDelta"]).Select(D), row =>
            Same("delta", PyJson.Dumps(Py.Objs(PlanPopulation.ArmDelta(L(row["lists"]).Select(Strings).ToList()))), PyJson.Dumps(row["delta"]))));
        Case("plan: topo_order, checks after their prerequisites", () => Rows(L(units["topoOrder"]).Select(D), row =>
            Same("order", string.Join(" ", Plan.TopoOrder(L(row["checks"]).Select(D).ToList()).Select(c => S(c["id"]))), string.Join(" ", Strings(row["order"])))));
        using var t = new Temp();
        WriteTree(t, D(units["tree"]));
        Case("plan: plugin_top_groups_from_bytes, records per top-level group", () => Rows(L(units["topGroups"]).Select(D), row =>
        {
            var got = PopulationRules.TopGroupRecords(File.ReadAllBytes(t.At("p/" + S(row["name"]))));
            string text = got == null ? "null" : PyJson.Dumps(new PyDict(got.Select(kv => new KeyValuePair<string, object?>(kv.Key, kv.Value))), sortKeys: true);
            return Same(S(row["name"]), text, PyJson.Dumps(row["groups"], sortKeys: true));
        }));
        Case("plan: population_file, the file's bytes and each reason a delta cannot be scoped", () => Rows(L(units["populationFiles"]).Select(D), row =>
        {
            var made = PlanPopulation.PopulationFile(L(Unmask(t, row["plugins"])).Select(D).ToList(), Strings(Unmask(t, row["mods"])), Strings(row["targets"]),
                                                    row["instance"] == null ? null : Unmask(t, S(row["instance"])));
            string name = S(row["name"]);
            if (row["refusal"] != null || made.refusal != null) return Same(name + ": refusal", made.refusal == null ? null : t.Mask(made.refusal), row["refusal"] as string);
            return Same(name + ": document", PyJson.PopulationText(t.Mask(PyJson.Loads(made.text))), PyJson.PopulationText(row["document"]))
                   ?? Same(name + ": race carriers", string.Join(",", made.raceCarriers), string.Join(",", Strings(row["raceCarriers"])))
                   ?? (row["rawSha256"] is string sha ? Same(name + ": sha256 of the text", made.sha256, sha) : null)
                   ?? Same(name + ": the hash is the text's", made.sha256, Hashing.Sha256Text(made.text));
        }));
        Case("case 430 a race-carrying delta leaves the state check on the whole order, the others scoped", () =>
        {
            PyDict Check(string id, string kind, string exe) => new()
            {
                ["checkId"] = id, ["adapter"] = new PyDict { ["kind"] = kind, ["exe"] = exe, ["populationFlag"] = "--population" },
                ["arms"] = new List<object?> { new PyDict { ["armId"] = "baseline", ["status"] = "planned" } },
            };
            var checks = new List<PyDict>
            {
                Check("geometry.object-overlap", "ordersweep-probe", "overlapprobe"), Check("state.conditional-geometry", "probe-emit", "stateprobe"),
                Check("geometry.fixture-empty-space", "probe-emit", "contactprobe"),
            };
            List<PyDict> Arms(string file) =>
            [
                new PyDict { ["plugins"] = Py.Objs(["A.esp"]), ["requested"] = new PyDict { ["plugins"] = new List<object?>(), ["providers"] = new List<object?>() } },
                new PyDict
                {
                    ["plugins"] = Py.Objs(["A.esp", file]),
                    ["requested"] = new PyDict
                    {
                        ["plugins"] = new List<object?> { new PyDict { ["name"] = file, ["path"] = t.At("p/" + file), ["sha256"] = Hashing.Sha256File(t.At("p/" + file)) } },
                        ["providers"] = new List<object?>(),
                    },
                },
            ];
            var (race, text) = PlanPopulation.Plan(checks, Arms("race.esp"), new List<object?>(), "load-time", null);
            var (plain, _) = PlanPopulation.Plan(checks, Arms("plain.esp"), new List<object?>(), "load-time", null);
            return Same("scoped with a race carrier", PyJson.Dumps(race.Get("scopedChecks")), "[\"geometry.fixture-empty-space\", \"geometry.object-overlap\"]")
                   ?? Same("the state check's reason", PyText.Str(Py.DictOrEmpty(race.Get("wholeOrderChecks")).Get("state.conditional-geometry")),
                           "race.esp carries RACE records, which change every actor band the state route check reads, so it runs on the whole order")
                   ?? (text != null && race["scoped"] is true ? null : "a race carrier must still leave a population for the other checks")
                   ?? Same("scoped without one", PyJson.Dumps(plain.Get("scopedChecks")),
                           "[\"geometry.fixture-empty-space\", \"geometry.object-overlap\", \"state.conditional-geometry\"]");
        });
        Case("case 431 a delta the population cannot hold still names its mod folders for the mesh rule (mesh-responsibility)", () =>
        {
            var checks = new List<PyDict>
            {
                new()
                {
                    ["checkId"] = "geometry.object-overlap", ["adapter"] = new PyDict { ["kind"] = "ordersweep-probe", ["exe"] = "overlapprobe", ["populationFlag"] = "--population" },
                    ["arms"] = new List<object?> { new PyDict { ["armId"] = "baseline", ["status"] = "planned" } },
                },
            };
            PyDict Provider(string name) => new() { ["kind"] = "mod", ["name"] = name };
            var arms = new List<PyDict>
            {
                new() { ["plugins"] = Py.Objs(["A.esp"]), ["requested"] = new PyDict { ["plugins"] = new List<object?>(), ["providers"] = new List<object?> { Provider("A") } } },
                // the delta plugin's copy is not stated by path and hash, so no population can be planned
                new() { ["plugins"] = Py.Objs(["A.esp", "B.esp"]), ["requested"] = new PyDict
                    { ["plugins"] = new List<object?> { new PyDict { ["name"] = "B.esp" } }, ["providers"] = new List<object?> { Provider("A"), Provider("B Meshes") } } },
            };
            var (block, text) = PlanPopulation.Plan(checks, arms, new List<object?>(), "load-time", null);
            return (block["scoped"] is false && text == null ? null : "the population must be refused here")
                   ?? Same("the folders a mesh row may name", PyJson.Dumps(block.Get("meshFolders")), "[\"B Meshes\"]")
                   ?? Same("their identity", HarnessShared.PopulationRules.MeshIdentity("load-time", ["B Meshes"]),
                           "load-time+mesh:" + Hashing.Sha256Text("b meshes")[..12]);
        });
        Case("owner: the configuration fingerprint is ConfigurationIdentity's (Python hashes a null population or state as the text None)", () =>
            Rows(L(units["fingerprints"]).Select(D), row =>
            {
                // the owner takes text; Python's copy writes str(None) for a null, which no plan or probe passes
                string Text(object? v) => v as string ?? "None";
                var id = ConfigurationIdentity.Compute("s",
                    L(row["plugins"]).Select(D).Select(p => new ConfigPlugin(S(p["name"]), "", S(p["sha256"]), 0, "", false, false, [])).ToList(),
                    L(row["providers"]).Select(D).Select(v => new ConfigProvider(S(v["name"]), S(v["kind"]))).ToList(),
                    L(row["overlays"]).Select(D).Select(o => new ConfigOverlay(S(o["kind"]), "", S(o["sha256"]))).ToList(), Text(row["population"]), Text(row["state"]));
                return Same("fingerprint", id.Fingerprint, S(row["python"]));
            }));
    }

    // ---- the command, as a child process

    sealed record PlanRun(int Exit, string Stdout, string Stderr, string Run);

    // `plan` in a fresh copy of the world, with the environment the Python answers were made under.
    static PlanRun RunPlan(Temp t, PyDict command, PyDict row)
    {
        WriteTree(t, D(command["tree"]));
        foreach (var (rel, text) in D(command["templates"])) File.WriteAllBytes(t.At(rel), Materialise(t, S(text)));
        foreach (var (rel, file) in D(row["files"]))
        {
            string path = t.At(rel);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllBytes(path, D(file).ContainsKey("template") ? Materialise(t, S(D(file)["template"])) : B64(D(file)["b64"]));
        }
        var psi = new ProcessStartInfo(Environment.ProcessPath!)
        {
            RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, WorkingDirectory = t.Root,
            StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8,
        };
        foreach (string k in psi.Environment.Keys.Where(k => k.StartsWith("HOUSECARL_", StringComparison.OrdinalIgnoreCase)).ToList()) psi.Environment.Remove(k);
        psi.Environment["HOUSECARL_RUNLOG_DIR"] = t.At("runlog");
        foreach (var (k, v) in D(row["env"])) psi.Environment[k] = Unmask(t, S(v));
        psi.ArgumentList.Add("plan");
        foreach (string a in Strings(row["argv"])) psi.ArgumentList.Add(Unmask(t, a));
        using var p = Process.Start(psi)!;
        var err = p.StandardError.ReadToEndAsync();
        string stdout = p.StandardOutput.ReadToEnd();
        p.WaitForExit();
        return new PlanRun(p.ExitCode, stdout.Replace("\r\n", "\n"), err.Result.Replace("\r\n", "\n"), t.At("run"));
    }

    static readonly Regex PlanIdLine = new("^plan [0-9a-f]{12}:", RegexOptions.Multiline | RegexOptions.CultureInvariant);

    // The plan as two controllers must agree on it: what names the controller, a moment or this folder is replaced.
    static string NormPlan(Temp t, PyDict plan)
    {
        plan["planId"] = "<id>";
        plan["createdUtc"] = "<utc>";
        plan["runner"] = "<runner>";
        if (D(plan["scope"])["sha256"] != null) D(plan["scope"])["sha256"] = "<sha>";   // the scope file names this folder
        var programs = D(D(plan["toolIdentity"])["programs"]);
        foreach (string pid in programs.Keys.ToList())
        {
            if (pid == "controller") programs[pid] = "<controller>";
            else if (Py.Truthy(D(programs[pid]).Get("digest"))) D(programs[pid])["digest"] = "<digest>";
        }
        if (D(plan["states"]).Get("fromCensus") is PyDict census) { census["generator"] = "<generator>"; census["sha256"] = "<sha>"; }
        var pop = D(plan["population"]);
        if (Py.Truthy(pop.Get("scoped"))) { pop["sha256"] = "<sha>"; pop["identity"] = S(pop["identity"])[..^12] + "<sha12>"; }
        foreach (var ad in L(plan["checks"]).Select(c => D(D(c)["adapter"])))
            if (ad.ContainsKey("population")) ad["population"] = S(ad["population"])[..^12] + "<sha12>";
        return PyJson.Dumps(t.Mask(plan), indent: 1, ensureAscii: false);
    }

    // An intended difference from evalrun.py (mesh-responsibility): a plan with no population names the delta's mod folders
    // (population.meshFolders), and overlap's and contact's adapters expect a population carrying them. Checked here against
    // the owner (PopulationRules.MeshIdentity), then taken out, so the rest of the plan is compared with Python's as recorded.
    static string? MeshAddition(PyDict plan)
    {
        var pop = D(plan["population"]);
        if (pop.Get("meshFolders") is not { } mf) return null;
        var folders = L(mf).Select(S).ToList();
        string baseline = S(Py.DictOrEmpty(plan.Get("configuration")).Get("population") ?? "load-time");
        if (Py.Truthy(pop.Get("scoped"))) return "a scoped plan must not name mesh folders: its population names them";
        foreach (var c in L(plan["checks"]).Select(D))
        {
            var ad = D(c["adapter"]);
            bool mesh = Py.S(ad.Get("kind")) == "ordersweep-probe" && ad.Get("exe") is string exe && HarnessShared.PopulationRules.MeshProbes.Contains(exe);
            if (!mesh) continue;
            if (Same($"{S(c["checkId"])}'s expected population", S(ad.Get("population")), HarnessShared.PopulationRules.MeshIdentity(baseline, folders)) is { } bad) return bad;
            ad.Pop("population");
        }
        pop.Pop("meshFolders");
        return null;
    }

    static string Listing(string run) => !Directory.Exists(run) ? "" : string.Join(" ",
        PyOrder.SortedPaths(PyPath.RGlob(run).Where(File.Exists)).Select(f => PyPath.RelativeTo(f, run)!).Where(r => !r.Split('/').Contains("runlog")));

    // What `plan` printed and wrote, against Python's; then the files' own forms, which Python's answers cannot hold
    // (they name this folder): each file is the one writer's bytes, and each hash and identity is of those bytes.
    static string? ComparePlan(Temp t, PyDict row, PlanRun got)
    {
        string stderr = t.Mask(got.Stderr), head = row.Get("stderrHead") as string ?? S(row["stderr"]);
        string? why = Same("exit", got.Exit.ToString(), PyText.Str(row["exit"]))
                      ?? SameText("stdout", PlanIdLine.Replace(t.Mask(got.Stdout), "plan <id>:"), S(row["stdout"]))
                      ?? (row.ContainsKey("stderrHead") ? SameText("stderr, up to the runtime's own words", stderr[..Math.Min(stderr.Length, head.Length)], head) : SameText("stderr", stderr, head))
                      ?? Same("files written", Listing(got.Run), string.Join(" ", Strings(row["written"])));
        if (why != null || !row.ContainsKey("planGz")) return why;
        string planFile = PyPath.Join(got.Run, "plan.json");
        byte[] raw = File.ReadAllBytes(planFile);
        var plan = D(PyJson.Loads(PyJson.Decode(raw, bom: false)));
        string planId = S(plan["planId"]);
        var asWritten = (PyDict)Py.Copy(plan)!;
        why = SameBytes("plan.json is the one writer's form", raw, PyJson.Utf8(PyJson.Dumps(plan, indent: 1, ensureAscii: false).Replace("\n", "\r\n")))
              ?? Same("planId is the canonical plan's hash", planId, Hashing.Sha256Text(PyJson.Canonical(new PyDict(plan.Where(kv => kv.Key != "planId"))))[..12])
              ?? Same("plan.sha256", File.ReadAllText(PyPath.Join(got.Run, "plan.sha256")), Hashing.Sha256Bytes(raw) + "\r\n")
              ?? Same("the scope's hash is the file's", D(plan["scope"])["sha256"] as string, D(plan["scope"])["path"] is string scoped ? Hashing.Sha256File(scoped) : null)
              ?? Same("the journal is empty", new FileInfo(PyPath.Join(got.Run, "journal.jsonl")).Length.ToString(), "0")
              ?? MeshAddition(plan)
              ?? SameText("plan", NormPlan(t, plan), Gunzip(row["planGz"]));
        if (why != null) return why;
        byte[] ledgerRaw = File.ReadAllBytes(PyPath.Join(got.Run, "ledger.json"));
        var ledger = D(PyJson.Loads(PyJson.Decode(ledgerRaw, bom: false)));
        why = SameBytes("ledger.json is the one writer's form", ledgerRaw, PyJson.Utf8(PyJson.Dumps(ledger, indent: 1, ensureAscii: false).Replace("\n", "\r\n")))
              ?? Same("the ledger's plan", S(ledger["planId"]), planId);
        ledger["planId"] = "<id>";
        why ??= SameText("ledger", PyJson.Dumps(ledger, indent: 1, ensureAscii: false), S(row["ledger"]));
        string popFile = PyPath.Join(got.Run, "population.json");
        var block = D(asWritten["population"]);
        if (row["population"] == null) why ??= File.Exists(popFile) ? "a population file was written and Python wrote none" : null;
        else
        {
            byte[] popRaw = File.ReadAllBytes(popFile);
            object? doc = PyJson.Loads(PyJson.Decode(popRaw, bom: false));
            string sha = Hashing.Sha256Bytes(popRaw);
            why ??= SameText("population", PyJson.PopulationText(t.Mask(doc)), S(row["population"]))
                    ?? Same("the block's sha256 is the file's", S(block["sha256"]), sha)
                    ?? Same("the identity names the file", S(block["identity"]), PopulationRules.Identity(S(D(asWritten["options"])["population"]), sha));
            if (!block.ContainsKey("adoptedFrom"))
                why ??= SameBytes("population.json is the population form", popRaw, PyJson.Utf8(PyJson.PopulationText(doc)));
        }
        string table = PyPath.Join(got.Run, PrimitiveTable.FileName);
        return why ?? Same("the primitive table's copy", File.Exists(table) ? Hashing.Sha256File(table) : null, row["table"] as string);
    }

    // The last arm's supplied block and the request row of its last plugin, as text.
    static string SuppliedArm(PyDict plan)
    {
        var arm = D(L(plan["arms"])[^1]);
        return PyJson.Dumps(new PyDict { ["supplied"] = arm["supplied"], ["row"] = L(D(arm["requested"])["plugins"])[^1] }, ensureAscii: false);
    }

    static void PlanCommandCases(PyDict command)
    {
        foreach (var row in L(command["cases"]).Select(D))
            Case($"plan command: {S(row["name"])}", () =>
            {
                using var t = new Temp();
                return ComparePlan(t, row, RunPlan(t, command, row));
            });
        // where the probes' readers answer otherwise than evalrun.py's own copies: this controller's answer, and that
        // Python's is still the other one
        var owner = new (string name, string python, string mine)[]
        {
            ("owner: with no profile named, a mod folder outside the view is not a layer",
             "no --profile was given, so the profile's mod folders outside this configuration's view cannot be listed",
             "{\"supplied\": {\"declaredFiles\": [\"Stray.esp\"], \"payloadMissing\": [\"Stray.esp\"], \"outOfView\": []}, \"row\": {\"name\": \"Stray.esp\", \"sha256\": null, "
             + "\"path\": null, \"hashSource\": \"unstated\", \"unstatedReason\": \"no layer provides Stray.esp: not the overwrite folder, not a mod folder of this "
             + "configuration's view, not the game's Data folder, and not one of the profile's other mod folders\"}}"),
            ("owner: a profile with no modlist.txt has no mod folders outside the view",
             "modlist.txt would not read, so the mod folders outside this configuration's view have no priority order",
             "{\"supplied\": {\"declaredFiles\": [\"Stray.esp\"], \"payloadMissing\": [\"Stray.esp\"], \"outOfView\": []}, \"row\": {\"name\": \"Stray.esp\", \"sha256\": null, "
             + "\"path\": null, \"hashSource\": \"unstated\", \"unstatedReason\": \"no layer provides Stray.esp: not the overwrite folder, not a mod folder of this "
             + "configuration's view, not the game's Data folder, and not one of the profile's other mod folders\"}}"),
            ("owner: a modlist.txt with a byte-order mark keeps its first mod",
             "no layer provides Extra.esp",
             "{\"supplied\": {\"declaredFiles\": [\"Extra.esp\"], \"payloadMissing\": [], \"outOfView\": [\"Extra.esp\"]}, \"row\": {\"name\": \"Extra.esp\", "
             + "\"sha256\": \"3f4f4ff5ed5a1c52bf5a0a6bf2b1c8a4e3a6d2d8a0d1f0a1d7c7c2b5d5c0f0aa\", \"path\": \"<ROOT\\\\>\\\\instance\\\\mods\\\\OffMod\\\\Extra.esp\", "
             + "\"hashSource\": \"instance:OffMod\", \"providedBy\": [{\"layer\": \"OffMod\", \"path\": \"<ROOT\\\\>\\\\instance\\\\mods\\\\OffMod\\\\Extra.esp\", "
             + "\"sha256\": \"3f4f4ff5ed5a1c52bf5a0a6bf2b1c8a4e3a6d2d8a0d1f0a1d7c7c2b5d5c0f0aa\"}]}}"),
        };
        foreach (var (name, python, mine) in owner)
            Case(name, () =>
            {
                var row = L(command["owner"]).Select(D).First(r => S(r["name"]) == name);
                using var t = new Temp();
                var got = RunPlan(t, command, row);
                if (got.Exit != Exit.Ok) return $"exit {got.Exit}: {got.Stderr}";
                string theirs = SuppliedArm(D(PyJson.Loads(Gunzip(row["planGz"]))));
                string ours = SuppliedArm((PyDict)t.Mask(PyJson.Loads(PyJson.Decode(File.ReadAllBytes(PyPath.Join(got.Run, "plan.json")), bom: false)))!);
                string wanted = mine.Replace("3f4f4ff5ed5a1c52bf5a0a6bf2b1c8a4e3a6d2d8a0d1f0a1d7c7c2b5d5c0f0aa", Hashing.Sha256File(t.At("instance/mods/OffMod/Extra.esp")));
                if (!theirs.Contains(python)) return $"Python's answer is no longer the one recorded as differing: {theirs}";
                return ours == theirs ? "the two controllers agree now; this is no longer a difference" : SameText("this controller's answer", ours, wanted);
            });
    }

    // ---- the pipeline's plan stage, the child stood in for

    // What `plan` would leave for the stage to read: a plan over every prepared arm and the whole registry.
    static ChildResult StandInPlan(ChildJob job, int exit = 0, int dropChecks = 0, bool dropArm = false, long held = 0)
    {
        var argv = job.Argv.ToList();
        string Value(string flag) => argv[argv.IndexOf(flag) + 1];
        if (exit != 0) return new ChildResult(job.Id, exit, 0.1, null, 1);
        var files = Directory.GetFiles(Value("--candidates"), "candidate-*.json").Select(PyPath.Name).ToList();
        int checks = L(D(PyJson.ReadPipelineFile(Value("--registry")))["checks"]).Count - dropChecks;
        PyJson.WriteEvalFile(PyPath.Join(Value("--out"), "plan.json"), new PyDict
        {
            ["arms"] = files.Skip(dropArm ? 1 : 0).Select(f => (object?)new PyDict { ["candidateFile"] = f }).ToList(),
            ["checks"] = Enumerable.Range(0, checks).Select(i => (object?)new PyDict { ["checkId"] = $"c{i}" }).ToList(),
            ["budgets"] = new PyDict { ["minutes"] = 90.0, ["candidatesHeldBack"] = new PyDict { ["count"] = held } },
        });
        return new ChildResult(job.Id, 0, 0.1, null, 1);
    }

    // The plan stage over an in-place world: (the plan child's job, the stage's outcome).
    static (ChildJob? job, string outcome) PlanStage(Temp t, PyDict entry, Func<ChildJob, ChildResult>? plan = null)
    {
        var saved = ChildLaunch.RunJob;
        ChildJob? planJob = null;
        try
        {
            var res = new RunContext(entry, new PyDict(), t.At("run")).Resolve();
            var launched = new List<string>();
            ChildLaunch.RunJob = (_, _, _, job) =>
            {
                if (job.Id != "plan") return StandInScopeprobe(res, job, launched);
                planJob = job;
                return (plan ?? (j => StandInPlan(j)))(job);
            };
            var driver = new Driver { OpenRun = QuietRun, SetsRunLogFolder = false, MakeContext = a => new RunContext(entry, new PyDict(), a.Out!) };
            var (exit, stdout, stderr) = Drive(driver, new PipelineArgs(Entry: "e.json", Out: t.At("run"), Through: "plan"));
            return (planJob, $"exit {exit}: {stderr}{stdout}");
        }
        finally { ChildLaunch.RunJob = saved; }
    }

    static string Flag(ChildJob job, string flag)
    {
        int at = job.Argv.ToList().IndexOf(flag);
        return at < 0 ? "(absent)" : job.Argv[at + 1];
    }

    static void PlanStageCases()
    {
        Case("case b7 select is closed over the registry prerequisites and derived checks", () =>
        {
            using var t = new Temp();
            var (inst, game) = World(t, ShadowMods, ShadowEnabled, ShadowPlugins);
            var (job, outcome) = PlanStage(t, InPlaceEntry(inst, game, ["Sel"], ["geometry.object-overlap", "state.conditional-geometry"]));
            if (job == null || !outcome.Contains("plan: accepted in ")) return outcome;
            var select = Flag(job, "--select").Split(',');
            string[] need = ["geometry.object-overlap", "loadability.masters-and-order", "patch.order-evaluation", "state.conditional-geometry", "state.switch-census"];
            return need.All(select.Contains) && select.SequenceEqual(PyOrder.SortedByCodePoint(select))
                ? Same("depth", Flag(job, "--depth"), "deep") ?? Same("program", string.Join(" ", job.Argv.Take(2)), Environment.ProcessPath + " plan")
                  ?? Same("budget and arms", Flag(job, "--budget-minutes") + " " + Flag(job, "--max-candidates"), "90 2")
                  ?? Same("registry", Flag(job, "--registry"), PyOrder.Fwd(Checkout.Registry))
                  ?? Same("scope", Flag(job, "--scope"), PyOrder.Fwd(t.At("run/scope/candidate-01/scope.json")))
                : $"--select {Flag(job, "--select")}";
        });
        Case("case b5b the plan names the review arm", () =>
        {
            using var t = new Temp();
            var (inst, game) = World(t, ShadowMods, ShadowEnabled, ShadowPlugins);
            var (job, outcome) = PlanStage(t, InPlaceEntry(inst, game, ["Sel"], ["geometry.object-overlap"]));
            return job == null ? outcome : Same("review arm", Flag(job, "--review-arm"), "candidate-01") ?? Same("depth", Flag(job, "--depth"), "standard");
        });
        Case("case b17 the primitive table is explicit, never this machine's harness config", () =>
        {
            using var t = new Temp();
            var (inst, game) = World(t, ShadowMods, ShadowEnabled, ShadowPlugins);
            var (job, outcome) = PlanStage(t, InPlaceEntry(inst, game, ["Sel"], ["geometry.object-overlap"]));
            if (job?.Env == null) return "the plan child was given no environment: " + outcome;
            string blanked = string.Join(" ", job.Env.OrderBy(kv => kv.Key, StringComparer.Ordinal).Select(kv => $"{kv.Key}='{kv.Value}'"));
            using var t2 = new Temp();
            (inst, game) = World(t2, ShadowMods, ShadowEnabled, ShadowPlugins);
            var entry = InPlaceEntry(inst, game, ["Sel"], ["geometry.object-overlap"]);
            D(entry["evaluation"]).Update(Json("{\"primitiveSolidity\": \"Q:/table.json\", \"budgetMinutes\": 12.5, \"optIn\": [\"runtime.state-switch-flip\"]}"));
            var (named, outcome2) = PlanStage(t2, entry);
            return named == null ? outcome2
                : Same("blanked", blanked, "HOUSECARL_HARNESS_CONFIG='' HOUSECARL_PRIMITIVE_SOLIDITY=''")
                  ?? Same("named table", Flag(named, "--primitive-solidity") + (named.Env == null ? " and no environment" : " and an environment"), "Q:/table.json and no environment")
                  ?? Same("budget", Flag(named, "--budget-minutes"), "12.5") ?? Same("opt in", Flag(named, "--opt-in"), "runtime.state-switch-flip");
        });
        Case("case b6b an asset-only selection plans without a scope", () =>
        {
            using var t = new Temp();
            var (inst, game) = World(t, [new ModFolder("Tex"), Mod("A", "A.esp")], ["Tex", "A"], ["A.esp"]);
            var (job, outcome) = PlanStage(t, InPlaceEntry(inst, game, ["Tex"], ["geometry.object-overlap"]));
            return job == null ? outcome : Same("scope", Flag(job, "--scope"), "(absent)") ?? (outcome.Contains("plan: accepted in ") ? null : outcome);
        });
        Case("stages: plan runs over an entry carrying every presentation key, reads none, and is reused until a measuring key changes", () =>
        {
            using var t = new Temp();
            var (inst, game) = World(t, ShadowMods, ShadowEnabled, ShadowPlugins);
            var entry = InPlaceEntry(inst, game, ["Sel"], ["geometry.object-overlap"]);
            entry["modNames"] = Json("{\"Sel.esp\": \"The Selection\"}");
            entry["nexusNames"] = Json("{\"Sel.esp\": \"Sel on Nexus\"}");
            entry["patchSearch"] = "X:/patch-search.json";
            entry["report"] = Json("{\"mode\": \"full\", \"maxPages\": 30}");
            entry["captureSize"] = L(PyJson.Loads("[1920, 1080]"));
            var (job, outcome) = PlanStage(t, entry);
            if (job == null || !outcome.StartsWith("exit 0") || !outcome.Contains("plan: accepted in ")) return outcome;
            string? why = Same("status", S(D(PyJson.ReadPipelineFile(t.At("run/status.json")))["status"]), "through plan")
                          ?? Same("the stage's outputs", string.Join(" ", D(D(D(new StageLedger(t.At("run")).Doc["stages"])["plan"])["outputs"]).Keys), PyOrder.Fwd(t.At("run/evaluation/plan.json")))
                          ?? Refuses(() => new RunContext(entry, new PyDict(), t.At("run")).Entry.Presentation("plan", "modNames"), "plan");
            if (why != null) return why;
            var renamed = new PyDict(entry) { ["modNames"] = Json("{\"Sel.esp\": \"Renamed\"}"), ["report"] = Json("{\"mode\": \"short\"}") };
            var (again, second) = PlanStage(t, renamed);
            if (again != null || !second.Contains("plan: reused (inputs and outputs unchanged)")) return "a presentation change ran plan again: " + second;
            var deeper = new PyDict(entry) { ["evaluation"] = Json("{\"select\": [\"geometry.object-overlap\"], \"depth\": \"deep\"}") };
            var (third, last) = PlanStage(t, deeper);
            return third == null && last.Contains("exists from an earlier plan; move it aside to replan, never plan over it.") ? null
                : "a changed measuring key must run plan again, which refuses to plan over the earlier one: " + last;
        });
        Case("stages: a plan that refuses, holds an arm back, lacks an arm or lacks a check is refused by the stage", () =>
        {
            string Try(Func<ChildJob, ChildResult> plan)
            {
                using var t = new Temp();
                var (inst, game) = World(t, ShadowMods, ShadowEnabled, ShadowPlugins);
                return PlanStage(t, InPlaceEntry(inst, game, ["Sel"], ["geometry.object-overlap"]), plan).outcome;
            }
            string refused = Try(j => StandInPlan(j, exit: 2)), held = Try(j => StandInPlan(j, held: 1)), lacksArm = Try(j => StandInPlan(j, dropArm: true)),
                   lacksCheck = Try(j => StandInPlan(j, dropChecks: 1)), unknown;
            using (var t = new Temp())
            {
                var (inst, game) = World(t, ShadowMods, ShadowEnabled, ShadowPlugins);
                unknown = PlanStage(t, InPlaceEntry(inst, game, ["Sel"], ["geometry.object-overlap", "geometry.no-such-check"])).outcome;
            }
            const string arms = "plan: REFUSED: the frozen plan does not hold every prepared arm (some were held back or missing); raise --max-candidates or fix the candidates.";
            return (refused.Contains("plan: REFUSED: evalrun plan refused or failed (exit 2); its sentence is in ") && refused.Contains("/run/plan/commands.") ? null : refused)
                   ?? (held.Contains(arms) ? null : held) ?? (lacksArm.Contains(arms) ? null : lacksArm)
                   ?? (lacksCheck.Contains("plan: REFUSED: the frozen plan does not carry the full check registry.") ? null : lacksCheck)
                   ?? (unknown.Contains("plan: REFUSED: the selection names geometry.no-such-check, which the check registry does not have.") ? null : unknown);
        });
    }
}
