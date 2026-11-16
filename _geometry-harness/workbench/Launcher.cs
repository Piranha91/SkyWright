using System.Text.Json;
using HousecarlCore;
using ModelContextProtocol.Protocol;

namespace Workbench;

/// <summary>One mod folder of the open profile, in MO2 priority order (strongest first).</summary>
internal sealed record LauncherMod(string Name, int Priority, bool Enabled, bool Separator)
{
    internal bool Selected { get; set; }
    internal bool Unmanaged { get; init; }
}

/// <summary>The launcher's state: the mode, the chosen instance, profile and game, the profile's
/// mod folders and the ticked ones, the ticked probes, the Output Folder and the Work Folder. Every verb is a method here
/// that the window's controls and <c>workbench_act</c> both call; a refusal is an <see cref="InvalidOperationException"/>
/// carrying one sentence. It also owns the houseCARL server bound to the open profile, which the endpoint forwards to.</summary>
internal sealed class Launcher : IAsyncDisposable
{
    readonly object _gate = new();
    PatchWriter? _bound;
    Task<PatchWriter>? _starting;

    internal string Mode { get; private set; } = "free";
    internal string? Instance { get; private set; }
    internal string? Profile { get; private set; }
    internal string? Game { get; private set; }
    internal List<LauncherMod> Mods { get; } = new();
    internal string? Output { get; private set; }
    internal string? WorkFolder { get; private set; }
    internal bool WorkFolderChosen { get; private set; }
    internal List<string> Probes { get; } = new();
    /// <summary>Checks the launcher added for another, with the one that needs it (per-check table, row 10).</summary>
    internal Dictionary<string, string> AddedFor { get; } = new();
    /// <summary>standard, or deep when the conditional-geometry check is ticked (per-check table, row 11).</summary>
    internal string Depth => Probes.Contains("state.conditional-geometry") ? "deep" : "standard";
    internal string? RunBundle { get; private set; }
    internal string? RunLine { get; private set; }
    internal string? RunFolder { get; private set; }
    internal Task? RunTask { get; private set; }
    /// <summary>Raised on the run's thread with the verified bundle's folder when a run is done (the window opens it).</summary>
    internal event Action<string>? BundleReady;
    /// <summary>Why Run cannot start now (a scene is open in the window), or null.</summary>
    internal Func<string?>? RunBlocker { get; set; }
    internal string RunState { get; private set; } = "idle";
    internal string? RunRefusal { get; private set; }

    // ---- the settings file (free mode), the probes of the current run, and what was aborted
    /// <summary>Where free mode remembers the instance, profile and Output Folder; beside the exe unless a test says otherwise.</summary>
    internal string SettingsPath { get; set; } = LauncherSettings.DefaultPath;
    /// <summary>Why the settings were not written or read, in one sentence; null when they were.</summary>
    internal string? SettingsNote { get; private set; }
    /// <summary>The checks the current (or last) run was started with.</summary>
    internal List<string> RunChecks { get; } = new();
    /// <summary>The checks aborted in the current run; their probe is stopped whenever a pass of it starts.</summary>
    internal HashSet<string> AbortedChecks { get; } = new(StringComparer.OrdinalIgnoreCase);
    /// <summary>The current run's own run-log store, where its probes record their progress: the pipeline keeps it under
    /// its output folder (<c>run/runlog</c>).</summary>
    internal string? RunStore => RunFolder is null ? null : Path.Combine(RunFolder, "run", "runlog");
    readonly Dictionary<string, HousecarlCore.Progress.RunEstimate> _first = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Run every probe that can run on this profile and selection, switched on or not; the rest are named.</summary>
    internal string RunAll()
    {
        var reg = Registry().Where(c => ProbeKinds.Contains(c.Kind)).ToList();
        var can = reg.Where(c => WhyNotHere(c) is null).Select(c => c.Id).ToList();
        var cannot = reg.Where(c => WhyNotHere(c) is not null).Select(c => ProbeNames.Of(c.Id)).ToList();
        if (can.Count == 0) throw new InvalidOperationException("no probe can run on this profile and selection.");
        return Run(can) + (cannot.Count > 0 ? $"; left out because they cannot run here: {string.Join(", ", cannot)}." : "");
    }
    System.Diagnostics.Process? _pipeline;
    System.Threading.Timer? _enforce;
    bool _groupAborted;

    internal Mo2OutputLayout Layout() => Mo2OutputLayout.Of(Instance!, Profile!, Game!);

    /// <summary>Free mode: any MO2 instance in place. The game folder is the instance's gamePath unless given.</summary>
    internal async Task<string> OpenProfileAsync(string? mode, string? instance, string? profile, string? game)
    {
        var m = (mode ?? "free").Trim().ToLowerInvariant();
        if (m != "free" && m != "housecarl") throw new InvalidOperationException($"'{mode}' is not a mode; use housecarl or free.");
        if (m == "housecarl")
        {
            // the instance the client's own houseCARL server manages, read from that server started as the client starts it
            var launch = HousecarlMode.Locate(Environment.GetEnvironmentVariable("WB_HOUSECARL_LAUNCH"),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude.json"), Environment.CurrentDirectory);
            instance = await HousecarlMode.ReportedInstanceAsync(launch);
        }
        if (string.IsNullOrWhiteSpace(instance) || !Path.IsPathFullyQualified(instance.Trim()))
            throw new InvalidOperationException("open_profile in free mode needs instance, the MO2 instance folder by its full path.");
        var inst = OutputFolderPolicy.Full(instance);
        if (!File.Exists(Mo2Instance.IniPath(inst)) && !Directory.Exists(Path.Combine(inst, "profiles")))
            throw new InvalidOperationException($"'{inst}' is not an MO2 instance: it has no ModOrganizer.ini and no profiles folder.");
        var root = Mo2Instance.LayoutRoot(inst);
        if (!string.Equals(OutputFolderPolicy.Full(root), inst, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException($"'{inst}' keeps its mods and profiles in '{root}' (ModOrganizer.ini base_directory), and the probes read <instance>/mods and <instance>/profiles, so a manual run cannot use this instance yet.");
        var prof = string.IsNullOrWhiteSpace(profile) ? Mo2Instance.ReadSelectedProfile(inst) : profile.Trim();
        if (prof is null) throw new InvalidOperationException($"name a profile of '{inst}'; its ModOrganizer.ini selects none.");
        var profDir = Path.Combine(root, "profiles", prof);
        var modlist = Path.Combine(profDir, "modlist.txt");
        if (!File.Exists(modlist)) throw new InvalidOperationException($"the profile '{prof}' has no modlist.txt at '{profDir}'; name a profile of that instance.");
        string g;
        if (!string.IsNullOrWhiteSpace(game)) g = OutputFolderPolicy.Full(game);
        else if (Mo2Instance.TryResolve(inst, out var paths) && paths is not null) g = OutputFolderPolicy.Full(paths.GamePath);
        else g = GamePathFromIni(inst) ?? throw new InvalidOperationException($"'{inst}' names no game folder in its ModOrganizer.ini (gamePath); give game, the game's folder.");
        var lines = File.ReadAllLines(modlist).Where(l => l.Length > 1 && (l[0] == '+' || l[0] == '-' || l[0] == '*')).ToList();
        await DropBoundAsync();
        lock (_gate)
        {
            Mode = m; Instance = inst; Profile = prof; Game = g;
            Mods.Clear();
            // modlist.txt lists the strongest folder first; that is the order kept (priority counts up from the weakest)
            for (int i = 0; i < lines.Count; i++)
            {
                var name = lines[i][1..].Trim();
                Mods.Add(new LauncherMod(name, lines.Count - 1 - i, lines[i][0] != '-', name.EndsWith("_separator", StringComparison.OrdinalIgnoreCase)) { Unmanaged = lines[i][0] == '*' });
            }
            Output = null; WorkFolder = null; WorkFolderChosen = false; RunState = "idle"; RunRefusal = null;
        }
        Remember();
        return $"opened profile '{prof}' of '{inst}' (game '{g}'): {Mods.Count(x => !x.Separator)} mod folder(s), {Mods.Count(x => x.Enabled && !x.Separator)} enabled";
    }

    /// <summary>Free mode writes what it opened to the settings file; a folder it cannot write is said, not ignored.</summary>
    void Remember()
    {
        if (Mode != "free" || Instance is null) return;
        SettingsNote = LauncherSettings.Save(SettingsPath, new LauncherSettings(Instance, Profile, Game, Output, WorkFolderChosen ? WorkFolder : null));
    }

    /// <summary>Open what the settings file remembers: the instance and profile, then the Output Folder and a chosen Work
    /// Folder. Each refusal is said; nothing is restored silently in part.</summary>
    internal async Task<string> RestoreAsync()
    {
        SettingsNote = LauncherSettings.CannotWrite(SettingsPath);
        var (s, why) = LauncherSettings.Load(SettingsPath);
        if (why is not null) { SettingsNote = why; return why; }
        if (s?.Instance is null) return "no saved instance to restore.";
        var said = new List<string>();
        try { said.Add(await OpenProfileAsync("free", s.Instance, s.Profile, s.Game)); }
        catch (InvalidOperationException ex) { return "the saved instance could not be opened: " + ex.Message; }
        if (s.Output is not null) { try { said.Add(SetOutput(s.Output)); } catch (InvalidOperationException ex) { said.Add("the saved Output Folder was not set: " + ex.Message.TrimEnd('.')); } }
        if (s.WorkFolder is not null) { try { said.Add(SetWorkFolder(s.WorkFolder)); } catch (InvalidOperationException ex) { said.Add("the saved Work Folder was not set: " + ex.Message.TrimEnd('.')); } }
        return "restored from the last session: " + string.Join("; ", said);
    }

    static string? GamePathFromIni(string inst)
    {
        var ini = Mo2Instance.IniPath(inst);
        if (!File.Exists(ini)) return null;
        var line = File.ReadAllLines(ini).FirstOrDefault(l => l.StartsWith("gamePath=", StringComparison.OrdinalIgnoreCase));
        if (line is null) return null;
        var v = line["gamePath=".Length..].Trim();
        if (v.StartsWith("@ByteArray(") && v.EndsWith(")")) v = v["@ByteArray(".Length..^1];
        v = v.Replace("\\\\", "\\");
        return v.Length > 0 ? OutputFolderPolicy.Full(v) : null;
    }

    void NeedProfile()
    {
        if (Instance is null) throw new InvalidOperationException("open a profile first.");
    }

    internal string SelectMods(IReadOnlyList<string>? names, string? how)
    {
        NeedProfile();
        var h = (how ?? "set").Trim().ToLowerInvariant();
        if (h is not ("set" or "add" or "remove")) throw new InvalidOperationException($"'{how}' is not a way to select; use set, add or remove.");
        var want = (names ?? Array.Empty<string>()).Select(n => n.Trim()).ToList();
        lock (_gate)
        {
            foreach (var n in want)
            {
                var m = Mods.FirstOrDefault(x => string.Equals(x.Name, n, StringComparison.OrdinalIgnoreCase))
                        ?? throw new InvalidOperationException($"'{n}' is not a mod folder of profile '{Profile}'.");
                if (m.Unmanaged) throw new InvalidOperationException($"'{n}' is an unmanaged entry (a DLC or a file MO2 does not manage), not a mod folder, so it cannot be selected.");
                if (m.Separator) throw new InvalidOperationException($"'{n}' is a separator, not a mod folder; tick the mods under it.");
            }
            if (h == "set") foreach (var m in Mods) m.Selected = false;
            foreach (var m in Mods.Where(x => want.Contains(x.Name, StringComparer.OrdinalIgnoreCase))) m.Selected = h != "remove";
        }
        int off = Mods.Count(x => x.Selected && !x.Enabled);
        return $"{Mods.Count(x => x.Selected)} mod(s) selected" + (off > 0 ? $"; {off} of them disabled in MO2, tested as if enabled" : "");
    }

    /// <summary>The registry's checks whose adapter runs a probe; every other kind is not a probe.</summary>
    internal static readonly string[] ProbeKinds = { "scopeprobe", "ordersweep-probe", "probe-emit", "probe-json", "probe-target", "patchprobe", "adviceprobe" };

    internal sealed record RegistryCheck(string Id, string Kind, string Title, string? Exe);

    static IReadOnlyList<RegistryCheck>? _registry;
    internal static IReadOnlyList<RegistryCheck> Registry()
    {
        if (_registry is not null) return _registry;
        var path = Program.FromCheckoutPublic("WB_REGISTRY", "check registry", ".claude", "skills", "world-edit-compatibility", "references", "check-registry.json");
        if (!File.Exists(path)) throw new InvalidOperationException($"the check registry is not at '{path}'; set WB_REGISTRY to check-registry.json.");
        using var d = JsonDocument.Parse(File.ReadAllText(path));
        _registry = d.RootElement.GetProperty("checks").EnumerateArray().Select(c =>
        {
            var ad = c.TryGetProperty("adapter", out var a) && a.ValueKind == JsonValueKind.Object ? a : default;
            string? S(JsonElement e, string k) => e.ValueKind == JsonValueKind.Object && e.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
            return new RegistryCheck(c.GetProperty("id").GetString()!, S(ad, "kind") ?? "unsupported", S(c, "title") ?? "", S(ad, "exe"));
        }).ToList();
        return _registry;
    }

    internal string SelectProbes(IReadOnlyList<string>? checks)
    {
        var want = (checks ?? Array.Empty<string>()).Select(c => c.Trim()).Where(c => c.Length > 0).ToList();
        if (want.Count == 0) throw new InvalidOperationException("select_probes needs checks: check ids, probe names, or [\"all\"].");
        var reg = Registry();
        var picked = new List<string>();
        foreach (var w in want)
        {
            if (string.Equals(w, "all", StringComparison.OrdinalIgnoreCase)) { picked.AddRange(reg.Where(c => WhyNotHere(c) is null).Select(c => c.Id)); continue; }
            var hits = reg.Where(c => string.Equals(c.Id, w, StringComparison.OrdinalIgnoreCase) || string.Equals(c.Exe, w, StringComparison.OrdinalIgnoreCase)).ToList();
            if (hits.Count == 0) throw new InvalidOperationException($"'{w}' is not a check of the registry; read workbench_state project launcher for the probes.");
            foreach (var c in hits)
            {
                if (!ProbeKinds.Contains(c.Kind))
                    throw new InvalidOperationException($"{ProbeNames.Of(c.Id)} is not a probe (it {ProbeNames.NotAProbe(c.Kind)}), so it is not run from here.");
                if (WhyNotHere(c) is { } why) throw new InvalidOperationException($"'{c.Id}' cannot run here: {why}.");
                picked.Add(c.Id);
            }
        }
        string extra = "";
        lock (_gate)
        {
            Probes.Clear(); Probes.AddRange(picked.Distinct()); AddedFor.Clear();
            // conditional geometry reads the switch census's receipt ({switchMap}), and runs only at depth deep
            if (Probes.Contains("state.conditional-geometry") && !Probes.Contains("state.switch-census"))
            { Probes.Add("state.switch-census"); AddedFor["state.switch-census"] = $"{ProbeNames.Of("state.conditional-geometry")} needs its list of enable markers"; }
            if (Probes.Contains("state.conditional-geometry"))
                extra = $"; {ProbeNames.Of("state.conditional-geometry")} runs only in a thorough run, so the whole run is thorough" + (AddedFor.Count > 0 ? $", and {ProbeNames.Of("state.switch-census")} was added for it" : "");
        }
        return $"{Probes.Count} probe(s) selected" + extra;
    }

    /// <summary>The Output Folder, with the rule's verdict; the Work Folder defaults from it unless one was chosen.</summary>
    internal string SetOutput(string? path)
    {
        NeedProfile();
        if (OutputFolderRule.Check(path ?? "", Instance!, Profile!, Game!) is { } why) throw new InvalidOperationException(why);
        var o = OutputFolderPolicy.Full(path!);
        string said;
        lock (_gate)
        {
            Output = o;
            if (!WorkFolderChosen)
            {
                try { WorkFolder = WorkFolderRule.Default(o, Instance!); said = $"the Output Folder is '{o}'; the Work Folder is '{WorkFolder}'"; }
                catch (InvalidOperationException ex) { WorkFolder = null; said = $"the Output Folder is '{o}'; " + ex.Message.TrimEnd('.'); }
            }
            else said = $"the Output Folder is '{o}'; the Work Folder stays '{WorkFolder}'";
        }
        Remember();
        return said;
    }

    internal string SetWorkFolder(string? path)
    {
        NeedProfile();
        if (WorkFolderRule.Check(path ?? "", Instance!, Profile!, Game!) is { } why) throw new InvalidOperationException(why);
        lock (_gate) { WorkFolder = OutputFolderPolicy.Full(path!); WorkFolderChosen = true; }
        Remember();
        return $"the Work Folder is '{WorkFolder}'";
    }

    /// <summary>Start a run of the selection; refused in one sentence until the launcher has
    /// what a run needs.</summary>
    /// <summary>Why a check cannot run on this profile and selection, or null (the per-check table's greyed lines).</summary>
    internal string? WhyNotHere(RegistryCheck c)
    {
        if (!ProbeKinds.Contains(c.Kind)) return $"it is not a probe; it {ProbeNames.NotAProbe(c.Kind)}";
        if (Instance is null) return null;
        var mods = Path.Combine(Mo2Instance.LayoutRoot(Instance), "mods");
        if (c.Id == "geometry.bos-overlay")
        {
            var roots = Mods.Where(m => (m.Enabled || m.Selected) && !m.Separator).Select(m => Path.Combine(mods, m.Name)).Append(Path.Combine(Game!, "Data"));
            if (!roots.Any(r => Directory.Exists(r) && Directory.EnumerateFiles(r, "*_SWAP.ini").Any()))
                return "no enabled mod and not the game's Data folder has a _SWAP.ini file, so Base Object Swapper changes nothing in this profile";
        }
        if (c.Id is "patch.discovery-installed" && HarnessTarget.SearchRoots.Length == 0)
            return "this computer has no search roots set for finding MO2 instances (HOUSECARL_SEARCH_ROOTS or the harness config's searchRoots), so installed patches cannot be looked for";
        if (c.Id is "patch.discovery-installed" or "patch.discovery-pool" or "patch.discovery-fomod" && HarnessTarget.DownloadPool.Length == 0)
            return "this computer has no downloads folder set (HOUSECARL_DOWNLOAD_POOL or the harness config's downloadPool), so patches cannot be looked for";
        if (c.Id == "patch.author-advice")
        {
            var sel = Mods.Where(m => m.Selected).ToList();
            if (sel.Count > 0 && !sel.Any(m => NexusId(Path.Combine(mods, m.Name)) is > 0))
                return "none of the selected mods has a Nexus mod id in MO2 (its meta.ini), so there is no author page to read";
        }
        return null;
    }

    static int? NexusId(string folder)
    {
        var ini = Path.Combine(folder, "meta.ini");
        if (!File.Exists(ini)) return null;
        var line = File.ReadLines(ini).FirstOrDefault(l => l.StartsWith("modid=", StringComparison.OrdinalIgnoreCase));
        return line is not null && int.TryParse(line[6..].Trim(), out var id) ? id : null;
    }

    /// <summary>Start a run of the selection through the existing pipeline: an in-place entry under
    /// the Work Folder, <c>evalpipeline.py run --through bundle</c>, and the bundle it verifies. Returns at once.</summary>
    internal string Run() => Run(null);

    /// <summary>The checks a run of <paramref name="only"/> needs: each must be a probe that can run here; the switch census
    /// is added for conditional geometry, which also makes the run deep.</summary>
    (List<string> Checks, string Depth) Expand(IReadOnlyList<string> only)
    {
        var reg = Registry();
        var list = new List<string>();
        foreach (var id in only)
        {
            var c = reg.FirstOrDefault(x => string.Equals(x.Id, id, StringComparison.OrdinalIgnoreCase))
                    ?? throw new InvalidOperationException($"'{id}' is not a check of the registry; read workbench_state project launcher for the probes.");
            if (WhyNotHere(c) is { } why) throw new InvalidOperationException($"{ProbeNames.Of(c.Id)} cannot run here: {why}.");
            list.Add(c.Id);
        }
        if (list.Contains("state.conditional-geometry") && !list.Contains("state.switch-census")) list.Add("state.switch-census");
        return (list.Distinct().ToList(), list.Contains("state.conditional-geometry") ? "deep" : "standard");
    }

    /// <summary>The probe a check runs (the registry's exe).</summary>
    internal static string? ExeOf(string check) => Registry().FirstOrDefault(c => string.Equals(c.Id, check, StringComparison.OrdinalIgnoreCase))?.Exe;

    /// <summary>Stop the whole run: the pipeline and every probe it started.</summary>
    internal string AbortRun()
    {
        if (RunTask is not { IsCompleted: false } || _pipeline is null) throw new InvalidOperationException("no run is going, so there is nothing to abort.");
        _groupAborted = true;
        try { _pipeline.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
        lock (_gate) { RunState = "aborted"; RunRefusal = "the run was aborted; no results were made."; }
        return "aborted the whole run: every probe it started was stopped.";
    }

    /// <summary>Stop one probe of the running run and nothing else: its live process now, and any later pass of it in this run.
    /// A probe shared by two checks stops for both, and says so.</summary>
    internal string AbortProbe(string? check)
    {
        if (RunTask is not { IsCompleted: false } || RunStore is not { } store) throw new InvalidOperationException("no run is going, so there is no probe to abort.");
        var id = RunChecks.FirstOrDefault(c => string.Equals(c, check?.Trim(), StringComparison.OrdinalIgnoreCase))
                 ?? throw new InvalidOperationException($"'{check}' is not a probe of the running run; abort takes one of {string.Join(", ", RunChecks)}.");
        var exe = ExeOf(id) ?? throw new InvalidOperationException($"{ProbeNames.Of(id)} names no probe exe in the registry.");
        var sharing = RunChecks.Where(c => c != id && string.Equals(ExeOf(c), exe, StringComparison.OrdinalIgnoreCase)).ToList();
        lock (_gate) { AbortedChecks.Add(id); foreach (var c in sharing) AbortedChecks.Add(c); }
        var stopped = ProbeRuns.Abort(store, exe);
        return (stopped.Count > 0 ? $"aborted {ProbeNames.Of(id)}" : $"{ProbeNames.Of(id)} will be stopped as soon as it starts")
               + (sharing.Count > 0 ? $"; {string.Join(", ", sharing.Select(ProbeNames.Of))} is part of the same probe and stops with it" : "")
               + "; the rest of the run goes on.";
    }

    /// <summary>What a probe's tile shows now.</summary>
    internal ProbeProgress ProgressOf(string check)
    {
        var exe = ExeOf(check);
        if (exe is null || !RunChecks.Contains(check, StringComparer.OrdinalIgnoreCase) || RunFolder is null) return new("idle", null, "", null);
        // once the run has ended, the tile says what the run's ledger concluded, not the probe's raw exits
        if (RunTask is { IsCompleted: true } && !AbortedChecks.Contains(check) && ProbeRuns.Verdict(Path.Combine(RunFolder, "run", "evaluation"), check) is { } v) return v;
        return ProbeRuns.Of(RunStore, exe, AbortedChecks.Contains(check), RunTask is { IsCompleted: true }, DateTimeOffset.UtcNow, _first.GetValueOrDefault(exe));
    }

    /// <summary>Start a run of <paramref name="only"/>, or of the ticked probes when it is null.</summary>
    internal string Run(IReadOnlyList<string>? only)
    {
        if (Instance is null) throw new InvalidOperationException("there is nothing to run: open a profile first.");
        if (RunBlocker?.Invoke() is { } blocked) throw new InvalidOperationException(blocked);
        if (RunTask is { IsCompleted: false }) throw new InvalidOperationException("a run is already going; wait for it to end or abort it.");
        var sel = Mods.Where(m => m.Selected).Select(m => m.Name).ToList();
        if (sel.Count == 0) throw new InvalidOperationException("no mod is ticked; tick the mods to check.");
        if (only is null && Probes.Count == 0) throw new InvalidOperationException("no probe is switched on; switch on the probes to run.");
        var (checks, depth) = only is null ? (Probes.ToList(), Depth) : Expand(only);
        if (checks.Count == 0) throw new InvalidOperationException("run needs at least one probe.");
        if (Output is null) throw new InvalidOperationException("choose an Output Folder first.");
        if (WorkFolder is null) throw new InvalidOperationException("with the Output Folder at the instance's mods folder there is no default Work Folder; choose one outside the instance and the game folder.");
        if (WorkFolderRule.Check(WorkFolder, Instance, Profile!, Game!) is { } bad) throw new InvalidOperationException(bad);
        var stamp = DateTime.UtcNow.ToString("yyyyMMdd'T'HHmmss'Z'");
        var dir = Path.Combine(WorkFolder, stamp);
        Directory.CreateDirectory(dir);
        var entry = Path.Combine(dir, "entry.json");
        File.WriteAllText(entry, JsonSerializer.Serialize(new
        {
            schema = 1, id = "wb-" + stamp, inPlace = new { instance = Instance, profile = Profile, game = Game },
            selection = new { mods = sel }, evaluation = new { select = checks.ToArray(), depth },
        }, new JsonSerializerOptions { WriteIndented = true }));
        // the machine file: WB_MACHINE's (heavy-stage prefix and the rest), with the names read through this checkout's server
        var machine = Path.Combine(dir, "machine.json");
        var m = Environment.GetEnvironmentVariable("WB_MACHINE") is { Length: > 0 } mf && File.Exists(mf)
            ? System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(mf))!.AsObject() : new System.Text.Json.Nodes.JsonObject();
        if (!m.ContainsKey("readerCmd"))
        {
            var exe = Environment.GetEnvironmentVariable("WB_SERVER") is { Length: > 0 } sx ? sx : StateServerExe();
            m["readerCmd"] = "\"" + exe + "\"";
        }
        File.WriteAllText(machine, m.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
        var pipeline = Environment.GetEnvironmentVariable("WB_PIPELINE_CMD") is { Length: > 0 } pc ? pc
            : Program.FromCheckoutPublic("WB_PIPELINE", "evaluation pipeline evalpipeline.py", "_geometry-harness", "evalpipeline.py");
        var run = Path.Combine(dir, "run");
        lock (_gate)
        {
            RunState = "running"; RunRefusal = null; RunBundle = null; RunLine = null; RunFolder = dir;
            RunChecks.Clear(); RunChecks.AddRange(checks); AbortedChecks.Clear(); _groupAborted = false;
            // the first guess per probe: the run-log estimator over earlier runs' histories (this Work Folder's runs and the default store)
            _first.Clear();
            // earlier workbench runs only: the default store also holds self-tests and probes of other sizes, which are not this work
            var histories = Directory.EnumerateDirectories(WorkFolder).Select(d => Path.Combine(d, "run", "runlog", "history.jsonl")).ToList();
            foreach (var exe in checks.Select(ExeOf).OfType<string>().Distinct(StringComparer.OrdinalIgnoreCase))
                _first[exe] = ProbeRuns.First(histories, exe, DateTimeOffset.UtcNow);
        }
        // an aborted probe stays stopped: any later pass of it in this run is stopped as it starts
        _enforce?.Dispose();
        _enforce = new System.Threading.Timer(_ =>
        {
            string[] aborted; lock (_gate) aborted = AbortedChecks.ToArray();
            if (RunStore is { } st) foreach (var exe in aborted.Select(ExeOf).OfType<string>().Distinct()) ProbeRuns.Abort(st, exe);
        }, null, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1));
        RunTask = Task.Run(() =>
        {
            var psi = new System.Diagnostics.ProcessStartInfo("python") { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
            foreach (var a in new[] { "-I", pipeline, "run", "--entry", entry, "--machine", machine, "--out", run, "--through", "bundle" }) psi.ArgumentList.Add(a);
            psi.Environment["PYTHONIOENCODING"] = "utf-8";
            psi.Environment["HOUSECARL_RUNLOG_DIR"] = Path.Combine(dir, "runlog");      // set, not left to a default
            var log = Path.Combine(dir, "pipeline.log");
            try
            {
                using var p = System.Diagnostics.Process.Start(psi)!;
                _pipeline = p;
                using var w = new StreamWriter(log) { AutoFlush = true };
                p.OutputDataReceived += (_, e) => { if (e.Data is null) return; lock (w) w.WriteLine(e.Data); RunLine = e.Data; };
                p.ErrorDataReceived += (_, e) => { if (e.Data is null) return; lock (w) w.WriteLine(e.Data); if (e.Data.Trim().Length > 0) RunLine = e.Data; };
                p.BeginOutputReadLine(); p.BeginErrorReadLine();
                p.WaitForExit();
                _enforce?.Dispose(); _pipeline = null;
                var bundle = Path.Combine(run, "bundle", "evidence");
                if (_groupAborted) { }                                                     // AbortRun said so already
                else if (p.ExitCode == 0 && File.Exists(Path.Combine(bundle, "manifest.json")))
                {
                    lock (_gate) { RunState = "done"; RunBundle = bundle; }
                    BundleReady?.Invoke(bundle);
                }
                else lock (_gate) { RunState = "refused"; RunRefusal = $"the run stopped (exit code {p.ExitCode}) without making results: {RunLine?.Trim()} (its log: {log})"; }
            }
            catch (Exception ex) { lock (_gate) { RunState = "refused"; RunRefusal = $"the run could not be started: {ex.Message}"; } }
        });
        return $"running {(checks.Count == 1 ? ProbeNames.Of(checks[0]) : $"{checks.Count} probes")} on {sel.Count} mod(s){(depth == "deep" ? ", thorough" : "")} in profile '{Profile}'; its files go under '{dir}'";
    }

    static string StateServerExe() { try { return Program.DefaultServerExe(); } catch (InvalidOperationException) { return "housecarl-mcp.exe"; } }

    /// <summary>The houseCARL server bound to the open profile by explicit paths, its data folder under the Work Folder
    ///, started on the first forwarded call.</summary>
    Task<PatchWriter> BoundAsync()
    {
        lock (_gate)
        {
            if (_bound is not null) return Task.FromResult(_bound);
            if (_starting is not null) return _starting;
            NeedProfile();
            if (Output is null) throw new InvalidOperationException("choose an Output Folder first (workbench_act verb set_output): in manual mode every houseCARL write goes under it.");
            if (WorkFolder is null) throw new InvalidOperationException("choose a Work Folder outside the instance and the game folder first (workbench_act verb set_work_folder): the houseCARL server keeps its data there.");
            var exe = Environment.GetEnvironmentVariable("WB_SERVER") is { Length: > 0 } s ? s : Program.DefaultServerExe();
            var corpus = Environment.GetEnvironmentVariable("WB_CORPUS") is { Length: > 0 } cp ? cp : Program.DefaultCorpus();
            var target = new WriteTarget(Instance!, Profile!, Path.Combine(Game!, "Data"), Output);
            var data = Path.Combine(WorkFolder, "hcdata");
            _starting = Task.Run(async () =>
            {
                var w = await PatchWriter.StartAsync(exe, target, data, corpus, WorkbenchHost.Say);
                lock (_gate) { _bound = w; _starting = null; }
                return w;
            });
            return _starting;
        }
    }

    internal async Task<CallToolResult> ForwardAsync(string tool, IDictionary<string, JsonElement>? args, JsonElement schema, CancellationToken ct)
    {
        // judge the names the server will bind, not only the spelling given (toFile binds as to_file)
        var (canon, bad) = ForwardRule.Canonical(tool, args is null ? null : new Dictionary<string, JsonElement>(args), schema);
        if (bad is not null) return StateTools.Refuse(bad);
        args = canon;
        string output; Mo2OutputLayout layout; string? work;
        lock (_gate)
        {
            NeedProfile();
            if (Output is null) throw new InvalidOperationException("choose an Output Folder first (workbench_act verb set_output): in manual mode every houseCARL write goes under it.");
            output = Output; layout = Layout(); work = WorkFolder;
        }
        var (send, refusal) = ForwardRule.Prepare(tool, args is null ? null : new Dictionary<string, JsonElement>(args), output, layout, work);
        if (refusal is not null) return StateTools.Refuse(refusal);
        // the folder an allowed to_file goes in may not exist yet; it is under the Output or the Work Folder
        if (send!.TryGetValue("to_file", out var tfe) && tfe.ValueKind == JsonValueKind.String && Path.GetDirectoryName(tfe.GetString()!) is { Length: > 0 } tfd)
            Directory.CreateDirectory(tfd);
        var server = await BoundAsync();
        return await server.ForwardAsync(tool, send!, ct);
    }

    async Task DropBoundAsync()
    {
        PatchWriter? old; Task<PatchWriter>? starting;
        lock (_gate) { old = _bound; starting = _starting; _bound = null; _starting = null; }
        if (starting is not null) { try { old = await starting; } catch { } }
        if (old is not null) await old.DisposeAsync();
    }

    public async ValueTask DisposeAsync() => await DropBoundAsync();

    /// <summary>The <c>launcher</c> part of <c>workbench_state</c>.</summary>
    internal object Project()
    {
        lock (_gate)
        {
            string? verdict = Instance is null || Output is null ? null : OutputFolderRule.Check(Output, Instance, Profile!, Game!) ?? "allowed";
            return new
            {
                mode = Mode, instance = Instance, game = Game, profile = Profile,
                profiles = Instance is null ? Array.Empty<string>() : ProfilesOf(Instance),
                mods = Mods.Select(m => new { name = m.Name, priority = m.Priority, enabled = m.Enabled, separator = m.Separator, unmanaged = m.Unmanaged, selected = m.Selected }).ToList(),
                probes = Registry().Where(c => ProbeKinds.Contains(c.Kind)).Select(c => new
                {
                    checkId = c.Id, name = ProbeNames.Of(c.Id), group = ProbeNames.Family(c.Id), title = c.Title, selectable = WhyNotHere(c) is null, selected = Probes.Contains(c.Id),
                    whyNot = WhyNotHere(c), addedFor = AddedFor.GetValueOrDefault(c.Id),
                    progress = RunChecks.Contains(c.Id) && ProgressOf(c.Id) is { } pr ? new { state = pr.State, fraction = pr.Fraction, line = pr.Line, eta = pr.Eta } : null,
                }).ToList(),
                notProbes = Registry().Where(c => !ProbeKinds.Contains(c.Kind)).Select(c => new { checkId = c.Id, name = ProbeNames.Of(c.Id), why = ProbeNames.NotAProbe(c.Kind) }).ToList(),
                settings = new { path = SettingsPath, note = SettingsNote },
                output = Output, outputVerdict = verdict, workFolder = WorkFolder,
                depth = Depth,
                run = new { state = RunState, refusal = RunRefusal, line = RunLine, folder = RunFolder, bundle = RunBundle },
            };
        }
    }

    internal static string[] ProfilesOf(string inst)
    {
        var dir = Path.Combine(Mo2Instance.LayoutRoot(inst), "profiles");
        return Directory.Exists(dir) ? Directory.GetDirectories(dir).Select(Path.GetFileName).OfType<string>().OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToArray() : Array.Empty<string>();
    }
}
