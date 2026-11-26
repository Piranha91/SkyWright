using HousecarlCore;
using HousecarlCore.Progress;
using SkyWright.Engine;

namespace RunControl;

/// <summary>overlapprobe's own scene preflight run on every arm of a plan, the check its scene captures will run.
/// The rules live in the probe; this acts on the verdict file it writes.</summary>
public static class ScenePreflight
{
    public const string Probe = "overlapprobe";
    /// <summary>The one preflight refusal that only a scene capture needs.</summary>
    public const string MeshGapCode = "view-lacks-installed-meshes";

    /// <summary>Test seam: runs the jobs for (run-log op, log folder).</summary>
    public static Func<string, string, IReadOnlyList<ChildJob>, IReadOnlyList<ChildResult>> RunJobs { get; set; } =
        (op, logs, jobs) => new ChildRunner(op, logs).Run(jobs, echo: false);

    /// <summary>The instance, profile and game as the variables a probe with no --instance flag reads them from.</summary>
    public static Dictionary<string, string> ChildEnv(PyDict opts)
    {
        var env = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (name, key) in new[] { ("HOUSECARL_MO2_INSTANCE", "instance"), ("HOUSECARL_MO2_PROFILE", "profile"), ("HOUSECARL_GAME_PATH", "game") })
            if (Py.Truthy(opts.Get(key))) env[name] = PyText.Str(opts[key]);
        return env;
    }

    public static List<string> CommonFlags(PyDict opts)
    {
        var flags = new List<string>();
        foreach (string flag in new[] { "instance", "profile", "game" })
            if (Py.Truthy(opts.Get(flag))) flags.AddRange(["--" + flag, Py.S(opts[flag])]);
        return flags;
    }

    static string? Instance(PyDict opts) =>
        Py.Truthy(opts.Get("instance")) ? PyText.Str(opts["instance"]) : Environment.GetEnvironmentVariable("HOUSECARL_MO2_INSTANCE") is { Length: > 0 } e ? e : null;

    /// <summary>Records each arm's verdict on it as `scenePreflight` and returns (refusals, notes): one line per arm
    /// the probe refused, and lines for the plan's output. A mesh-gap refusal gates only an arm in sceneArms (a planned
    /// check runs overlapprobe there); every other refusal gates every arm.</summary>
    public static (List<string> refusals, List<string> notes) Run(List<PyDict> arms, PyDict opts, string harness, HashSet<string> sceneArms)
    {
        var refusals = new List<string>();
        var notes = new List<string>();
        if (Instance(opts) == null)
        {
            string reason = "no --instance was given, so the plan could not check the arms' asset views against the base "
                            + "profile the way overlapprobe's scene captures will";
            foreach (var arm in arms) arm["scenePreflight"] = new PyDict { ["status"] = "not-run", ["reason"] = reason };
            notes.Add("scene preflight not run: the arms' asset views were not checked against the base profile, because no --instance was given");
            return (refusals, notes);
        }
        var argv0 = PlanIdentity.ProbeArgv(opts, harness, Probe);
        if (argv0 == null)
        {
            refusals.Add($"no overlapprobe build at {PlanIdentity.ProbeExe(harness, Probe)}, so the plan cannot "
                         + "check the arms' asset views the way the scene captures will; build overlapprobe, then plan again");
            return (refusals, notes);
        }
        object? over = Py.DictOrEmpty(opts.Get("probeCmd")).Get(Probe);
        var tool = Py.Truthy(over) ? new PyDict { ["probeCmd"] = over } : PlanIdentity.ToolRecord(harness, Probe);
        string td = Path.Combine(Path.GetTempPath(), "evalrun-preflight-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(td);
        try
        {
            var jobs = arms.Select((arm, i) => new ChildJob(
                $"scene-preflight-{i:00}",
                [.. argv0, "--scene-preflight", "--order", PyText.Str(arm["candidatePath"]), "--json", PyPath.Join(td, $"{i:00}.json"), .. CommonFlags(opts)],
                Env: ChildEnv(opts))).ToList();
            var results = RunJobs("evalrun-plan-preflight", PyPath.Join(td, "logs"), jobs);
            for (int i = 0; i < arms.Count; i++)
            {
                var arm = arms[i];
                var verdict = PyJson.LoadEvalFile(PyPath.Join(td, $"{i:00}.json")) as PyDict;
                string where = $"arm {arm["armId"]} ({arm["candidateFile"]})";
                string? word = verdict?.Get("verdict") as string;
                if (word is not ("passed" or "refused"))
                {
                    string log = PyPath.Join(td, "logs", $"scene-preflight-{i:00}.txt");
                    var tail = File.Exists(log) ? PyText.SplitLines(PyJson.Decode(File.ReadAllBytes(log), bom: false)).Where(ln => PyValue.Strip(ln).Length > 0).ToList() : [];
                    refusals.Add($"{where}: overlapprobe's scene preflight wrote no verdict (exit {ChildLaunch.ExitText(results[i])}): "
                                 + $"{(tail.Count > 0 ? tail[^1] : "no output")}; run it by hand on that candidate to see why");
                    continue;
                }
                string? code = verdict!.Get("code") as string;
                if (word == "passed")
                {
                    var reordered = new List<object?>(Py.ListOrEmpty(verdict.Get("reorderedProviders")));
                    arm["scenePreflight"] = new PyDict
                    {
                        ["status"] = "passed", ["providers"] = (long)PyValue.Len(Py.Truthy(verdict.Get("providers")) ? verdict["providers"] : new List<object?>()),
                        ["omittedProviders"] = verdict.Get("omittedProviderCount"), ["reorderedProviders"] = reordered, ["tool"] = tool,
                    };
                    if (reordered.Count > 0)
                        // the probes read the candidate's own Mods order; say so, since the base profile differs
                        notes.Add($"   {arm["armId"]}: states its own asset priority for {string.Join(", ", reordered.Select(PyText.Str))}; the "
                                  + "probes read the candidate's Mods order, not the base profile's modlist.txt");
                }
                else if (PyValue.Eq(verdict.Get("code"), "no-explicit-mods"))
                {
                    // a candidate that keeps the profile's assets is fine for the swept probes; only a scene capture needs a view
                    arm["scenePreflight"] = new PyDict { ["status"] = "no-asset-view", ["code"] = verdict["code"], ["reason"] = verdict.Get("reason"), ["tool"] = tool };
                    notes.Add($"   {arm["armId"]}: no scene can be captured from this arm: {PyText.Str(verdict.Get("reason"))}");
                }
                else if (code == MeshGapCode && !sceneArms.Contains(Py.S(arm["armId"])))
                {
                    arm["scenePreflight"] = new PyDict
                    {
                        ["status"] = "refused", ["gating"] = false, ["code"] = verdict["code"], ["reason"] = verdict.Get("reason"), ["tool"] = tool,
                    };
                    notes.Add($"   {arm["armId"]}: no planned check on this arm captures scenes, so the scene preflight "
                              + $"does not gate it; a scene capture would refuse it: {PyText.Str(verdict.Get("reason"))}");
                }
                else
                {
                    refusals.Add($"{where}: overlapprobe's scene preflight refused it: {PyText.Str(verdict.Get("reason"))}. "
                                 + $"To fix: {PyText.Str(verdict.Get("remedy"))}.");
                }
            }
        }
        finally
        {
            try { Directory.Delete(td, recursive: true); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
        return (refusals, notes);
    }
}
