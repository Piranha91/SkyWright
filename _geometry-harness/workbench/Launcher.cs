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
/// carrying one sentence. Opening a profile retires the houseCARL writers of the one before (<see cref="HousecarlWriters"/>).</summary>
internal sealed class Launcher : IAsyncDisposable
{
    readonly object _gate = new();

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
    /// <summary>The pipeline's own sentence on the preparation: reused from an earlier run, or why not.</summary>
    internal string? PrepNote { get; private set; }
    /// <summary>The pipeline's scope line: the cells the ticked mods edit, who else touches them, disabled mods to tick.</summary>
    internal string? ScopeNote { get; private set; }
    /// <summary>What the ticked checks did, from the bundle's disposition: found something, found nothing, did not complete.</summary>
    internal string? RunTally { get; private set; }
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
    /// <summary>The engine every command of a run and of a review goes through: the built-in one, the only one.</summary>
    internal RunEngine Engine => RunEngine.OfBuiltIn();

    /// <summary>One sentence when a remembered setting or WB_CONTROLLER still names the Python run engine, which is gone;
    /// null when nothing does. A remembered csharp means what it always meant and is dropped without a word.</summary>
    static string? GoneEngineNote(LauncherSettings? saved)
    {
        var env = Environment.GetEnvironmentVariable("WB_CONTROLLER");
        if (env is { Length: > 0 }) return $"WB_CONTROLLER is set to '{env}', but there is no choice of run engine any more: runs use the built-in one.";
        var name = saved?.Controller?.Trim().ToLowerInvariant();
        if (name is null or "" or "csharp" or "built-in" or "builtin") return null;
        return $"the saved run engine '{saved!.Controller}' is gone: runs use the built-in engine, and the setting is dropped the next time the settings are saved.";
    }

    readonly Dictionary<string, HousecarlCore.Progress.RunEstimate> _first = new(StringComparer.OrdinalIgnoreCase);
    (double Seconds, int Runs)? _prepGuess;

    /// <summary>The whole run's line while it goes: the pipeline's step from its own run record, and while it prepares,
    /// how long earlier runs in this Work Folder took to prepare; null when no pipeline record is live.</summary>
    internal string? PipelineLine()
    {
        if (RunStore is not { } st || RunTask is not { IsCompleted: false } || ProbeRuns.Pipeline(st) is not { } p) return null;
        var line = p.Line(DateTimeOffset.UtcNow);
        if (p.ChecksStarted is null)
            line += _prepGuess is { } g ? $"; preparing took about {TimeSpan.FromSeconds(Math.Round(g.Seconds)).ToString(@"m\:ss")} in {(g.Runs == 1 ? "1 earlier run" : $"{g.Runs} earlier runs")}" : "; no earlier run here to estimate the preparation from";
        return line;
    }

    /// <summary>The pipeline's preparation line in the tab's words: "reused from X (…)" or "not reused: why".</summary>
    internal static string PrepWords(string said) =>
        said.StartsWith("reused from ", StringComparison.Ordinal) ? "reused the preparation from run " + said["reused from ".Length..]
        : said.StartsWith("not reused: ", StringComparison.Ordinal) ? "prepared afresh: " + said["not reused: ".Length..]
        : said;

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


    /// <summary>Free mode: any MO2 instance in place. The game folder is the instance's gamePath unless given.</summary>
    internal async Task<string> OpenProfileAsync(string? mode, string? instance, string? profile, string? game)
    {
        Loading.Set("profile", "Opening the profile" + (string.IsNullOrWhiteSpace(profile) ? "" : $" '{profile.Trim()}'"));
        try { return await OpenProfileCoreAsync(mode, instance, profile, game); }
        finally { Loading.Clear("profile"); }
    }

    async Task<string> OpenProfileCoreAsync(string? mode, string? instance, string? profile, string? game)
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
        // a new profile: every houseCARL writer of the old one is closed (on the pool), and the next use opens the new one
        await HousecarlWriters.RetireAll();
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
    /// <summary>What a restore says when no profile was saved: a first start, which the window does not mention.</summary>
    internal const string NothingToRestore = "no saved instance to restore.";

    internal async Task<string> RestoreAsync()
    {
        SettingsNote = LauncherSettings.CannotWrite(SettingsPath);
        var (s, why) = LauncherSettings.Load(SettingsPath);
        if (why is not null) { SettingsNote = why; return why; }
        var gone = GoneEngineNote(s);                                 // read before anything is written back
        if (s?.Instance is null) { SettingsNote ??= gone; return gone ?? NothingToRestore; }
        var said = new List<string>();
        try { said.Add(await OpenProfileAsync("free", s.Instance, s.Profile, s.Game)); }
        catch (InvalidOperationException ex) { SettingsNote ??= gone; return "the saved instance could not be opened: " + ex.Message + (gone is null ? "" : " " + gone); }
        if (s.Output is not null) { try { said.Add(SetOutput(s.Output)); } catch (InvalidOperationException ex) { said.Add("the saved Output Folder was not set: " + ex.Message.TrimEnd('.')); } }
        if (s.WorkFolder is not null) { try { said.Add(SetWorkFolder(s.WorkFolder)); } catch (InvalidOperationException ex) { said.Add("the saved Work Folder was not set: " + ex.Message.TrimEnd('.')); } }
        if (gone is not null) { SettingsNote ??= gone; said.Add(gone.TrimEnd('.')); }
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
    internal static readonly string[] ProbeKinds = { "scopeprobe", "ordersweep-probe", "probe-emit", "probe-json", "probe-target" };

    /// <param name="Held">The registry marks the check opt-in (`optIn`): built, not yet confirmed in game, so a scan does not run it.</param>
    internal sealed record RegistryCheck(string Id, string Kind, string Title, string? Exe, bool Held = false);

    /// <summary>The greyed line of a check the registry holds back (its `optIn` field, never its id).</summary>
    internal const string HeldWhy = "it is held back until a look in the game confirms what it finds";

    static IReadOnlyList<RegistryCheck>? _registry;
    internal static IReadOnlyList<RegistryCheck> Registry()
    {
        if (_registry is not null) return _registry;
        var path = Program.Piece(HarnessShared.CheckoutLayout.Piece.Registry, "check registry check-registry.json");
        if (!File.Exists(path)) throw new InvalidOperationException($"the check registry is not at '{path}'; set WB_REGISTRY to check-registry.json.");
        using var d = JsonDocument.Parse(File.ReadAllText(path));
        _registry = d.RootElement.GetProperty("checks").EnumerateArray().Select(c =>
        {
            var ad = c.TryGetProperty("adapter", out var a) && a.ValueKind == JsonValueKind.Object ? a : default;
            string? S(JsonElement e, string k) => e.ValueKind == JsonValueKind.Object && e.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
            bool held = c.TryGetProperty("optIn", out var oi) && oi.ValueKind == JsonValueKind.True;
            return new RegistryCheck(c.GetProperty("id").GetString()!, S(ad, "kind") ?? "unsupported", S(c, "title") ?? "", S(ad, "exe"), held);
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
        if (c.Held) return HeldWhy;
        if (Instance is null) return null;
        var mods = Path.Combine(Mo2Instance.LayoutRoot(Instance), "mods");
        if (c.Id == "geometry.bos-overlay")
        {
            var roots = Mods.Where(m => (m.Enabled || m.Selected) && !m.Separator).Select(m => Path.Combine(mods, m.Name)).Append(Path.Combine(Game!, "Data"));
            if (!roots.Any(r => Directory.Exists(r) && Directory.EnumerateFiles(r, "*_SWAP.ini").Any()))
                return "no enabled mod and not the game's Data folder has a _SWAP.ini file, so Base Object Swapper changes nothing in this profile";
        }
        return null;
    }

    /// <summary>Start a run of the selection through the chosen run engine (<see cref="RunEngine"/>): an in-place entry
    /// under the Work Folder, its <c>run --through bundle</c>, and the bundle it verifies. Returns at once.</summary>
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
        if (RunTask is { IsCompleted: true } && !AbortedChecks.Contains(check) && ProbeRuns.Verdict(Path.Combine(RunFolder, "run", "evaluation"), check) is { } v)
        {
            // the bundle's own count, when this run's bundle is open: rows new with the selection, and rows that were there before
            if (RunBundle is { } rb && Shell.Bundle is { } ob && string.Equals(Path.GetFullPath(ob.Dir), Path.GetFullPath(rb), StringComparison.OrdinalIgnoreCase)
                && ob.Checks.FirstOrDefault(c => c.CheckId == check) is { State: "ran" } cs)
                return v with { Line = v.Line + " " + char.ToUpperInvariant(CheckStatusWords.Rows(cs)[0]) + CheckStatusWords.Rows(cs)[1..] + "." };
            return v;
        }
        return ProbeRuns.Of(RunStore, exe, AbortedChecks.Contains(check), RunTask is { IsCompleted: true }, DateTimeOffset.UtcNow, _first.GetValueOrDefault(exe), pipelined: true,
            nameOf: e => Registry().FirstOrDefault(c => string.Equals(c.Exe, e, StringComparison.OrdinalIgnoreCase)) is { } c ? ProbeNames.Of(c.Id) : e);
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
        var engine = Engine;
        engine.NeedProgram();                                           // before any file of the run is written
        var stamp = DateTime.UtcNow.ToString("yyyyMMdd'T'HHmmss'Z'");
        var dir = Path.Combine(WorkFolder, stamp);
        Directory.CreateDirectory(dir);
        var entry = Path.Combine(dir, "entry.json");
        File.WriteAllText(entry, JsonSerializer.Serialize(new
        {
            schema = 1, id = "wb-" + stamp, inPlace = new { instance = Instance, profile = Profile, game = Game },
            selection = new { mods = sel }, evaluation = new { select = checks.ToArray(), depth },
        }, new JsonSerializerOptions { WriteIndented = true }));
        // the machine file: WB_MACHINE's (heavy-stage prefix and the rest); with no readerCmd in it, the run reads the names
        // with houseCARL's code compiled into runcontrol, starting no reader program
        var machine = Path.Combine(dir, "machine.json");
        var m = Environment.GetEnvironmentVariable("WB_MACHINE") is { Length: > 0 } mf && File.Exists(mf)
            ? System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(mf))!.AsObject() : new System.Text.Json.Nodes.JsonObject();
        File.WriteAllText(machine, m.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
        var run = Path.Combine(dir, "run");
        var psi = engine.Pipeline(entry, machine, run, WorkFolder, Path.Combine(dir, "runlog"));
        lock (_gate)
        {
            RunState = "running"; RunRefusal = null; RunBundle = null; RunLine = null; PrepNote = null; ScopeNote = null; RunTally = null; RunFolder = dir;
            RunChecks.Clear(); RunChecks.AddRange(checks); AbortedChecks.Clear(); _groupAborted = false;
            // the first guess per probe: the run-log estimator over earlier runs' histories (this Work Folder's runs and the default store)
            _first.Clear();
            // earlier workbench runs only: the default store also holds self-tests and probes of other sizes, which are not this work
            var histories = Directory.EnumerateDirectories(WorkFolder).Select(d => Path.Combine(d, "run", "runlog", "history.jsonl")).ToList();
            foreach (var exe in checks.Select(ExeOf).OfType<string>().Distinct(StringComparer.OrdinalIgnoreCase))
                _first[exe] = ProbeRuns.First(histories, exe, DateTimeOffset.UtcNow);
            _prepGuess = ProbeRuns.PreparationGuess(histories);
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
            var log = Path.Combine(dir, "pipeline.log");
            try
            {
                using var p = System.Diagnostics.Process.Start(psi)!;
                _pipeline = p;
                using var w = new StreamWriter(log) { AutoFlush = true };
                p.OutputDataReceived += (_, e) =>
                {
                    if (e.Data is null) return;
                    lock (w) w.WriteLine(e.Data);
                    RunLine = e.Data;
                    if (e.Data.StartsWith("preparation: ", StringComparison.Ordinal)) PrepNote = PrepWords(e.Data["preparation: ".Length..]);
                    if (e.Data.StartsWith("scope: ", StringComparison.Ordinal)) ScopeNote = e.Data["scope: ".Length..];
                };
                p.ErrorDataReceived += (_, e) => { if (e.Data is null) return; lock (w) w.WriteLine(e.Data); if (e.Data.Trim().Length > 0) RunLine = e.Data; };
                p.BeginOutputReadLine(); p.BeginErrorReadLine();
                p.WaitForExit();
                _enforce?.Dispose(); _pipeline = null;
                var bundle = Path.Combine(run, "bundle", "evidence");
                if (_groupAborted) { }                                                     // AbortRun said so already
                else if (p.ExitCode == 0 && File.Exists(Path.Combine(bundle, "manifest.json")))
                {
                    var tally = Tally(Path.Combine(bundle, "disposition.json"), checks);
                    lock (_gate) { RunState = "done"; RunBundle = bundle; RunTally = tally; }
                    BundleReady?.Invoke(bundle);
                }
                else lock (_gate) { RunState = "refused"; RunRefusal = $"the run stopped (exit code {p.ExitCode}) without making results: {RunLine?.Trim()} (its log: {log})"; }
            }
            catch (Exception ex) { lock (_gate) { RunState = "refused"; RunRefusal = $"the run could not be started: {ex.Message}"; } }
        });
        return $"running {(checks.Count == 1 ? ProbeNames.Of(checks[0]) : $"{checks.Count} probes")} on {sel.Count} mod(s){(depth == "deep" ? ", thorough" : "")} in profile '{Profile}'; its files go under '{dir}'";
    }

    /// <summary>One sentence on the ticked checks from a disposition (FOLD-REVIEW finding 2). A check with any finding found
    /// something, else one with a completed null result found nothing, else it did not complete; either of the first two is
    /// marked not complete when a part or arm of it is listed missing. What it could not judge is the disposition's own
    /// unjudgeablePopulations on the review arm (the rows repeat per part, so the largest per population); a check whose only
    /// missing rows are an unknown applicability says so, since that is a measurement the controller could not make, not a fault.</summary>
    internal static string Tally(string disposition, IReadOnlyCollection<string> checks)
    {
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllBytes(disposition));
            var root = doc.RootElement;
            var ev = root.GetProperty("evaluation");
            static string Str(JsonElement x, string k) => x.ValueKind == JsonValueKind.Object && x.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";
            IEnumerable<JsonElement> Rows(JsonElement at, string key) => at.TryGetProperty(key, out var a) && a.ValueKind == JsonValueKind.Array ? a.EnumerateArray() : [];
            HashSet<string> Ids(string key) => Rows(ev, key).Select(x => Str(x, "checkId")).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var found = Ids("findings"); var nothing = Ids("completedNullChecks");
            var missing = Rows(ev, "missing").GroupBy(x => Str(x, "checkId"), StringComparer.OrdinalIgnoreCase)
                .ToDictionary(g => g.Key, g => g.Select(x => Str(x, "status")).ToList(), StringComparer.OrdinalIgnoreCase);
            string review = Str(root, "reviewArm");
            var unj = root.TryGetProperty("unassessed", out var ua) ? Rows(ua, "unjudgeablePopulations").ToList() : new List<JsonElement>();
            if (unj.Any(x => Str(x, "armId") == review)) unj = unj.Where(x => Str(x, "armId") == review).ToList();
            static long Total(JsonElement x) => x.TryGetProperty("unjudgeable", out var u) && u.ValueKind == JsonValueKind.Object
                ? u.EnumerateObject().Sum(p => p.Value.ValueKind == JsonValueKind.Number && p.Value.TryGetInt64(out var v) ? v : 0) : 0;
            int f = 0, n = 0, m = 0, fInc = 0, nInc = 0, judgedShort = 0, onlyUnknown = 0;
            var unjudged = new SortedDictionary<string, long>(StringComparer.Ordinal);
            foreach (var c in checks)
            {
                var miss = missing.GetValueOrDefault(c);
                bool incomplete = miss is { Count: > 0 };
                if (found.Contains(c)) { f++; if (incomplete) fInc++; }
                else if (nothing.Contains(c)) { n++; if (incomplete) nInc++; }
                else { m++; continue; }
                if (incomplete && miss!.All(s => s == "unknown-applicability")) onlyUnknown++;
                var mine = unj.Where(x => string.Equals(Str(x, "checkId"), c, StringComparison.OrdinalIgnoreCase))
                              .GroupBy(x => Str(x, "population")).Select(g => (pop: g.Key, n: g.Max(Total))).Where(p => p.n > 0).ToList();
                if (mine.Count > 0) judgedShort++;
                foreach (var (pop, k) in mine) unjudged[pop] = unjudged.GetValueOrDefault(pop) + k;
            }
            string Of(int k) => k == 1 ? "1 check" : $"{k} checks";
            string Inc(int k) => k > 0 ? $" ({k} not complete)" : "";
            var line = $"{Of(f)} found something{Inc(fInc)}, {Of(n)} found nothing{Inc(nInc)}, {Of(m)} did not complete";
            if (m > 0 || fInc + nInc > 0) line += f + n == 0 ? ", so nothing was measured" : ", so this is not a full result";
            if (judgedShort > 0)
                line += $"; {judgedShort} could not judge every object ({string.Join(", ", unjudged.Select(kv => $"{kv.Key} {kv.Value}"))})";
            if (onlyUnknown > 0)
                line += $"; {onlyUnknown} {(onlyUnknown == 1 ? "is" : "are")} not complete only because whether {(onlyUnknown == 1 ? "it applies" : "they apply")} "
                        + "to this mod could not be measured, which is not a fault in the mod";
            return line;
        }
        catch (Exception ex) when (ex is IOException or JsonException or KeyNotFoundException or InvalidOperationException)
        {
            return $"what the checks did could not be read from {disposition} ({ex.Message})";
        }
    }

    /// <summary>Closes every houseCARL writer (the app closing).</summary>
    public async ValueTask DisposeAsync() => await HousecarlWriters.RetireAll();

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
                run = new { state = RunState, refusal = RunRefusal, line = RunLine, folder = RunFolder, bundle = RunBundle, preparation = PrepNote, scope = ScopeNote, tally = RunTally },
            };
        }
    }

    internal static string[] ProfilesOf(string inst)
    {
        var dir = Path.Combine(Mo2Instance.LayoutRoot(inst), "profiles");
        return Directory.Exists(dir) ? Directory.GetDirectories(dir).Select(Path.GetFileName).OfType<string>().OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToArray() : Array.Empty<string>();
    }
}
