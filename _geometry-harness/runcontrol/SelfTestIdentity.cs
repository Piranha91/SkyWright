using System.Diagnostics;
using System.Reflection;
using System.Text;
using HousecarlCore.Progress;

namespace RunControl;

/// <summary>The scan plan's configuration identity, held strictly: what the plan is made from is what is measured, and a
/// choice about models, reports, video, narration or an agents folder beside the program never moves it or decides whether
/// a plan is made. A measuring choice still reaches the plan. Each case compares the plan two runs write, in two folders,
/// with what names the folder, the moment and the controller masked (NormPlan).</summary>
public static partial class SelfTest
{
    static PyDict? _planCommand;
    static PyDict PlanCommand()
    {
        if (_planCommand != null) return _planCommand;
        using var s = Assembly.GetExecutingAssembly().GetManifestResourceStream("plan-command.json")!;
        using var r = new StreamReader(s, Encoding.UTF8);
        return _planCommand = D(D(PyJson.Loads(r.ReadToEnd()))["command"]);
    }

    // The plan command's world (the known answers' tree), planned without a --depth, with these extra arguments, this
    // environment and these files (relative path -> bytes).
    static string[] IdentityArgv(params string[] extra) =>
    [
        "--registry", "<ROOT\\>\\reg\\check-registry.json", "--scope", "<ROOT\\>\\scope.json", "--candidates", "<ROOT\\>\\cands\\two",
        "--out", "<ROOT\\>\\run", "--harness", "<ROOT\\>\\harness", .. extra,
    ];

    static PyDict IdentityRow(string[] argv, Dictionary<string, string>? env = null, Dictionary<string, string>? files = null)
    {
        var f = new PyDict();
        foreach (var (rel, text) in files ?? []) f[rel] = new PyDict { ["b64"] = Convert.ToBase64String(Encoding.UTF8.GetBytes(text)) };
        var e = new PyDict();
        foreach (var (k, v) in env ?? []) e[k] = v;
        return new PyDict { ["argv"] = Py.Objs(argv), ["env"] = e, ["files"] = f };
    }

    // One plan in its own folder: (exit, stderr, the plan as NormPlan writes it, or null, and every file the run wrote as text).
    static (int exit, string stderr, string? plan, string files) PlanOnce(PyDict row, string? exe = null)
    {
        using var t = new Temp();
        PlanRun got;
        if (exe == null) got = RunPlan(t, PlanCommand(), row);
        else
        {
            var command = PlanCommand();
            WriteTree(t, D(command["tree"]));
            foreach (var (rel, text) in D(command["templates"])) File.WriteAllBytes(t.At(rel), Materialise(t, S(text)));
            foreach (var (rel, file) in D(row["files"]))
            {
                string path = t.At(rel);
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                File.WriteAllBytes(path, B64(D(file)["b64"]));
            }
            var psi = new ProcessStartInfo(exe)
            {
                RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, WorkingDirectory = t.Root,
                StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8,
            };
            foreach (string k in psi.Environment.Keys.Where(k => k.StartsWith("HOUSECARL_", StringComparison.OrdinalIgnoreCase)).ToList()) psi.Environment.Remove(k);
            psi.Environment["HOUSECARL_RUNLOG_DIR"] = t.At("runlog");
            psi.Environment["HOUSECARL_EVAL_SETTINGS"] = t.At("settings/standard.json");
            foreach (var (k, v) in D(row["env"])) psi.Environment[k] = Unmask(t, S(v));
            psi.ArgumentList.Add("plan");
            foreach (string a in Strings(row["argv"])) psi.ArgumentList.Add(Unmask(t, a));
            using var p = Process.Start(psi)!;
            var err = p.StandardError.ReadToEndAsync();
            string stdout = p.StandardOutput.ReadToEnd();
            p.WaitForExit();
            got = new PlanRun(p.ExitCode, stdout, err.Result, t.At("run"));
        }
        string planFile = PyPath.Join(got.Run, "plan.json");
        string? plan = File.Exists(planFile) ? NormPlan(t, D(PyJson.Loads(PyJson.Decode(File.ReadAllBytes(planFile), bom: false)))) : null;
        var written = new StringBuilder();
        if (Directory.Exists(got.Run))
            foreach (string f in PyOrder.SortedPaths(PyPath.RGlob(got.Run).Where(File.Exists)))
                if (!f.Replace('\\', '/').Contains("/runlog/")) written.Append(f).Append('\n').Append(Encoding.UTF8.GetString(File.ReadAllBytes(f))).Append('\n');
        return (got.Exit, t.Mask(got.Stderr), plan, written.ToString());
    }

    // The two plans are made and equal, or the first difference in one sentence.
    static string? SamePlans(string what, (int exit, string stderr, string? plan, string files) a, (int exit, string stderr, string? plan, string files) b)
    {
        if (a.exit != 0 || a.plan == null) return $"{what}: the first plan was not made (exit {a.exit}): {a.stderr.Trim()}";
        if (b.exit != 0 || b.plan == null) return $"{what}: the second plan was not made (exit {b.exit}): {b.stderr.Trim()}";
        return SameText($"{what}: the plan", b.plan, a.plan);
    }

    const string SettingsVariable = "HOUSECARL_EVAL_SETTINGS";
    static Dictionary<string, string> SettingsFile(string json) => new() { ["settings/user.json"] = json };
    static Dictionary<string, string> SettingsEnv() => new() { [SettingsVariable] = "<ROOT\\>\\settings\\user.json" };

    static void IdentityCases()
    {
        Case("identity I1: a model setting does not change the plan", () =>
        {
            const string head = "{\"schemaVersion\": 1, \"evaluation\": {\"depth\": \"standard\"}, \"models\": {\"rewriteExplanations\": ";
            var off = PlanOnce(IdentityRow(IdentityArgv(), SettingsEnv(), SettingsFile(head + "\"off\"}}")));
            var session = PlanOnce(IdentityRow(IdentityArgv(), SettingsEnv(), SettingsFile(head + "\"session\"}}")));
            return SamePlans("models.rewriteExplanations off, then session", off, session);
        });
        Case("identity I2: report, video and narration settings do not change the plan", () =>
        {
            var plain = PlanOnce(IdentityRow(IdentityArgv(), SettingsEnv(), SettingsFile("{\"schemaVersion\": 1, \"evaluation\": {\"depth\": \"standard\"}}")));
            var shown = PlanOnce(IdentityRow(IdentityArgv(), SettingsEnv(), SettingsFile(
                "{\"schemaVersion\": 1, \"evaluation\": {\"depth\": \"standard\"}, \"report\": {\"mode\": \"exhaustive\", \"maxPages\": 7}, "
                + "\"video\": {\"make\": \"always\", \"narration\": {\"voice\": \"af_heart\"}}}")));
            return SamePlans("report, video and narration settings changed", plain, shown);
        });
        Case("identity I3: an agents folder beside the program neither changes the plan nor decides whether it is made", () =>
        {
            // two copies of this program's folder, each in a layout of its own; the second has plugin/agents above it
            using var t = new Temp();
            string me = AppContext.BaseDirectory.TrimEnd('\\', '/');
            string marker = PyPath.Join(Checkout.Root, HarnessShared.CheckoutLayout.LayoutMarker);
            string Copy(string layout)
            {
                string dir = t.At(layout + "/Tools/runcontrol");
                Directory.CreateDirectory(dir);
                foreach (string f in Directory.GetFiles(me)) File.Copy(f, Path.Combine(dir, Path.GetFileName(f)));
                foreach (string f in PyPath.RGlob(PyPath.Join(me, "runtimes")).Where(File.Exists))
                {
                    string to = Path.Combine(dir, Path.GetRelativePath(me, f));
                    Directory.CreateDirectory(Path.GetDirectoryName(to)!);
                    File.Copy(f, to);
                }
                File.Copy(marker, t.At(layout + "/" + HarnessShared.CheckoutLayout.LayoutMarker));
                return Path.Combine(dir, "runcontrol.exe");
            }
            string bare = Copy("bare"), withAgents = Copy("agents");
            Directory.CreateDirectory(t.At("agents/plugin/agents"));
            File.WriteAllText(t.At("agents/plugin/agents/model-claude-opus-5-5.md"),
                "---\nname: model-claude-opus-5-5\ndescription: a test agent\nmodel: claude-opus-5-5\n---\n\nA test agent.\n");
            string settings = "{\"schemaVersion\": 1, \"evaluation\": {\"depth\": \"standard\"}, \"models\": {\"recordChecks\": \"claude-opus-5-5\"}}";
            var a = PlanOnce(IdentityRow(IdentityArgv(), SettingsEnv(), SettingsFile(settings)), bare);
            var b = PlanOnce(IdentityRow(IdentityArgv(), SettingsEnv(), SettingsFile(settings)), withAgents);
            return SamePlans("without, then with an agents folder above the program", a, b);
        });
        Case("identity I5: a settings file that is not valid does not stop a plan", () =>
        {
            // the same flags on both sides; the only difference is a broken file where the settings variable points
            var none = PlanOnce(IdentityRow(IdentityArgv("--depth", "standard"), new() { [SettingsVariable] = "<ROOT\\>\\settings\\absent.json" }));
            var bad = PlanOnce(IdentityRow(IdentityArgv("--depth", "standard"), SettingsEnv(), SettingsFile("{ this is not json")));
            return SamePlans("no settings file, then a broken one, both with --depth standard", none, bad) is { } why
                ? why.Replace("the second plan", "the plan beside a broken settings file") : null;
        });
        Case("identity I7: plan --settings is refused in one sentence naming the flags", () =>
        {
            var got = PlanOnce(IdentityRow(IdentityArgv("--settings", "<ROOT\\>\\settings\\user.json"), files: SettingsFile("{\"schemaVersion\": 1}")));
            string said = got.stderr.Trim();
            if (got.exit != Exit.Refused) return $"plan --settings exited {got.exit} (a plan was {(got.plan == null ? "not " : "")}made): {said}";
            return said.StartsWith("REFUSED: ", StringComparison.Ordinal) && !said.Contains('\n') && said.Contains("--depth") && said.Contains("--no-reverse")
                   && said.Contains("--budget-minutes") && said.Contains("--opt-in") ? null : $"the refusal is not one sentence naming the four flags: {said}";
        });
        Case("identity I8: nothing the plan writes names a model or an agent", () =>
        {
            var got = PlanOnce(IdentityRow(IdentityArgv(), SettingsEnv(), SettingsFile(
                "{\"schemaVersion\": 1, \"evaluation\": {\"depth\": \"standard\"}, \"models\": {\"recordChecks\": \"session\"}}")));
            if (got.exit != 0) return $"the plan was not made (exit {got.exit}): {got.stderr.Trim()}";
            var hits = new[] { "claude-", "models.", "\"agent", "plugin/agents", "plugin\\\\agents", "evalsettings", "houseCARL.evaluation" }
                .Where(w => got.files.Contains(w, StringComparison.OrdinalIgnoreCase)).ToList();
            return hits.Count == 0 ? null : $"the run folder's files name {string.Join(", ", hits)}";
        });
        InPlaceIdentityCases();
    }

    // The app's scan, through the plan stage with a stand-in scopeprobe and this program's own plan command.
    static (int exit, string stderr, PyDict? plan, List<string> planArgv) InPlaceThroughPlan(Temp t, string run, PyDict entry, Dictionary<string, string?> env)
    {
        var (inst, game) = World(t, ShadowMods, ShadowEnabled, ShadowPlugins);
        foreach (var (k, v) in new Dictionary<string, object?> { ["instance"] = inst, ["game"] = game }) D(entry["inPlace"])[k] = v;
        string entryFile = PyPath.Join(t.Root, PyPath.Name(run) + "-entry.json");
        PyJson.WritePipelineFile(entryFile, entry);
        var res = new RunContext(entryFile, null, run).Resolve();
        var saved = ChildLaunch.RunJob;
        var before = env.ToDictionary(kv => kv.Key, kv => Environment.GetEnvironmentVariable(kv.Key));
        var planArgv = new List<string>();
        foreach (var (k, v) in env) Environment.SetEnvironmentVariable(k, v);
        ChildLaunch.RunJob = (op, dir, deadline, job) =>
        {
            if (job.Argv.Count > 1 && job.Argv[0] == Environment.ProcessPath && job.Argv[1] == "plan")
            {
                planArgv.AddRange(job.Argv.Skip(1));
                return saved(op, dir, deadline, job);
            }
            return StandInScopeprobe(res, job, []);
        };
        try
        {
            var (exit, _, stderr) = Drive(new Driver { OpenRun = QuietRun, SetsRunLogFolder = false },
                                          new PipelineArgs(Entry: entryFile, Out: run, Through: "plan"));
            string planFile = PyPath.Join(run, "evaluation", "plan.json");
            return (exit, t.Mask(stderr), File.Exists(planFile) ? D(PyJson.ReadPipelineFile(planFile)) : null, planArgv);
        }
        finally
        {
            ChildLaunch.RunJob = saved;
            foreach (var (k, v) in before) Environment.SetEnvironmentVariable(k, v);
        }
    }

    static void InPlaceIdentityCases()
    {
        Case("identity I4: the app's scan reads no evaluation settings file", () =>
        {
            using var t = new Temp();
            Directory.CreateDirectory(t.At("user/houseCARL"));
            string file = t.At("user/houseCARL/houseCARL.evaluation.json");
            File.WriteAllText(file, "{\"schemaVersion\": 1, \"evaluation\": {\"reverseComparison\": false}, \"models\": {\"recordChecks\": \"session\"}}");
            var none = InPlaceThroughPlan(t, t.At("none"), InPlaceEntry("", "", ["Sel"], ["geometry.object-overlap"]),
                new() { [SettingsVariable] = t.At("user/absent.json") });
            var named = InPlaceThroughPlan(t, t.At("named"), InPlaceEntry("", "", ["Sel"], ["geometry.object-overlap"]),
                new() { [SettingsVariable] = file });
            var local = InPlaceThroughPlan(t, t.At("local"), InPlaceEntry("", "", ["Sel"], ["geometry.object-overlap"]),
                new() { [SettingsVariable] = null, ["HOUSECARL_DATA_DIR"] = null, ["LOCALAPPDATA"] = t.At("user") });
            foreach (var (label, got) in new[] { ("without a settings file", none), ("with HOUSECARL_EVAL_SETTINGS", named), ("with %LOCALAPPDATA%\\houseCARL", local) })
            {
                if (got.exit != Exit.Ok || got.plan == null) return $"the scan {label} made no plan (exit {got.exit}): {got.stderr.Trim()}";
                if (D(got.plan["options"]).Get("noReverse") is not false) return $"the scan {label} turned the reverse comparison off: options.noReverse is {PyText.Repr(D(got.plan["options"]).Get("noReverse"))}";
                if (D(got.plan["explicitDecisions"]).ContainsKey("settings")) return $"the scan {label} froze a settings record: {PyJson.Dumps(D(got.plan["explicitDecisions"])["settings"]).Substring(0, 160)}";
            }
            return null;
        });
        Case("identity I6: a measuring choice in the entry still reaches the plan stage and the plan", () =>
        {
            using var t = new Temp();
            var plain = InPlaceEntry("", "", ["Sel"], ["geometry.object-overlap"]);
            var off = InPlaceEntry("", "", ["Sel"], ["geometry.object-overlap"]);
            D(off["evaluation"])["reverseComparison"] = false;
            var a = InPlaceThroughPlan(t, t.At("a"), plain, new() { [SettingsVariable] = t.At("absent.json") });
            var b = InPlaceThroughPlan(t, t.At("b"), off, new() { [SettingsVariable] = t.At("absent.json") });
            if (a.plan == null || b.plan == null) return $"a plan was not made (exits {a.exit}, {b.exit}): {a.stderr.Trim()} {b.stderr.Trim()}";
            string ia = PyText.Str(D(D(new StageLedger(t.At("a")).Doc["stages"])["plan"])["inputsSha256"]);
            string ib = PyText.Str(D(D(new StageLedger(t.At("b")).Doc["stages"])["plan"])["inputsSha256"]);
            if (ia == ib) return "the plan stage's inputs are the same with and without evaluation.reverseComparison false";
            if (D(a.plan["options"]).Get("noReverse") is not false) return "without the key the plan has no reverse comparison";
            return D(b.plan["options"]).Get("noReverse") is true ? null
                : $"evaluation.reverseComparison false did not reach the plan: options.noReverse is {PyText.Repr(D(b.plan["options"]).Get("noReverse"))}, the plan command was given {string.Join(" ", b.planArgv.Where(x => x.StartsWith("--")))}";
        });
    }
}
