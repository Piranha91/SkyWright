using System.Text.Json;
using HousecarlCore;
using ModelContextProtocol.Protocol;

namespace Workbench;

/// <summary>One mod folder of the open profile, in MO2 priority order (strongest first).</summary>
internal sealed record LauncherMod(string Name, int Priority, bool Enabled, bool Separator)
{
    internal bool Selected { get; set; }
}

/// <summary>The launcher's state (DESIGN section 1 and 6): the mode, the chosen instance, profile and game, the profile's
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
    internal string RunState { get; private set; } = "idle";
    internal string? RunRefusal { get; private set; }

    internal Mo2OutputLayout Layout() => Mo2OutputLayout.Of(Instance!, Profile!, Game!);

    /// <summary>Free mode: any MO2 instance in place. The game folder is the instance's gamePath unless given.</summary>
    internal async Task<string> OpenProfileAsync(string? mode, string? instance, string? profile, string? game)
    {
        var m = (mode ?? "free").Trim().ToLowerInvariant();
        if (m != "free" && m != "housecarl") throw new InvalidOperationException($"'{mode}' is not a mode; use housecarl or free.");
        if (m == "housecarl") throw new InvalidOperationException("houseCARL mode is not available in this build yet; use free mode with the instance's folder.");
        if (string.IsNullOrWhiteSpace(instance) || !Path.IsPathFullyQualified(instance.Trim()))
            throw new InvalidOperationException("open_profile in free mode needs instance, the MO2 instance folder by its full path.");
        var inst = OutputFolderPolicy.Full(instance);
        if (!File.Exists(Mo2Instance.IniPath(inst)) && !Directory.Exists(Path.Combine(inst, "profiles")))
            throw new InvalidOperationException($"'{inst}' is not an MO2 instance: it has no ModOrganizer.ini and no profiles folder.");
        var root = Mo2Instance.LayoutRoot(inst);
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
                Mods.Add(new LauncherMod(name, lines.Count - 1 - i, lines[i][0] != '-', name.EndsWith("_separator", StringComparison.OrdinalIgnoreCase)));
            }
            Output = null; WorkFolder = null; WorkFolderChosen = false; RunState = "idle"; RunRefusal = null;
        }
        return $"opened profile '{prof}' of '{inst}' (game '{g}'): {Mods.Count(x => !x.Separator)} mod folder(s), {Mods.Count(x => x.Enabled && !x.Separator)} enabled";
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

    void NeedProfile(string verb)
    {
        if (Instance is null) throw new InvalidOperationException($"{verb} needs a profile: open a profile first (workbench_act verb open_profile).");
    }

    internal string SelectMods(IReadOnlyList<string>? names, string? how)
    {
        NeedProfile("select_mods");
        var h = (how ?? "set").Trim().ToLowerInvariant();
        if (h is not ("set" or "add" or "remove")) throw new InvalidOperationException($"'{how}' is not a way to select; use set, add or remove.");
        var want = (names ?? Array.Empty<string>()).Select(n => n.Trim()).ToList();
        lock (_gate)
        {
            foreach (var n in want)
            {
                var m = Mods.FirstOrDefault(x => string.Equals(x.Name, n, StringComparison.OrdinalIgnoreCase))
                        ?? throw new InvalidOperationException($"'{n}' is not a mod folder of profile '{Profile}'.");
                if (m.Separator) throw new InvalidOperationException($"'{n}' is a separator, not a mod folder; tick the mods under it.");
                if (!m.Enabled && h != "remove")
                    throw new InvalidOperationException($"'{n}' is disabled: a disabled mod is not part of this profile; enable it in MO2 or pick a profile that loads it.");
            }
            if (h == "set") foreach (var m in Mods) m.Selected = false;
            foreach (var m in Mods.Where(x => want.Contains(x.Name, StringComparer.OrdinalIgnoreCase))) m.Selected = h != "remove";
        }
        return $"{Mods.Count(x => x.Selected)} mod folder(s) selected";
    }

    /// <summary>The registry's checks whose adapter runs a probe; every other kind is not a probe (DESIGN section 3).</summary>
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
            if (string.Equals(w, "all", StringComparison.OrdinalIgnoreCase)) { picked.AddRange(reg.Where(c => ProbeKinds.Contains(c.Kind)).Select(c => c.Id)); continue; }
            var hits = reg.Where(c => string.Equals(c.Id, w, StringComparison.OrdinalIgnoreCase) || string.Equals(c.Exe, w, StringComparison.OrdinalIgnoreCase)).ToList();
            if (hits.Count == 0) throw new InvalidOperationException($"'{w}' is not a check of the registry; read workbench_state project launcher for the probes.");
            foreach (var c in hits)
            {
                if (!ProbeKinds.Contains(c.Kind))
                    throw new InvalidOperationException($"'{c.Id}' is a check of kind {c.Kind}, which runs no probe, so the launcher does not run it.");
                picked.Add(c.Id);
            }
        }
        lock (_gate) { Probes.Clear(); Probes.AddRange(picked.Distinct()); }
        return $"{Probes.Count} probe check(s) selected";
    }

    /// <summary>The Output Folder, with the rule's verdict; the Work Folder defaults from it unless one was chosen.</summary>
    internal string SetOutput(string? path)
    {
        NeedProfile("set_output");
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
        return said;
    }

    internal string SetWorkFolder(string? path)
    {
        NeedProfile("set_work_folder");
        if (WorkFolderRule.Check(path ?? "", Instance!, Profile!, Game!) is { } why) throw new InvalidOperationException(why);
        lock (_gate) { WorkFolder = OutputFolderPolicy.Full(path!); WorkFolderChosen = true; }
        return $"the Work Folder is '{WorkFolder}'";
    }

    /// <summary>Start a run of the selection (M7 builds the run itself); refused in one sentence until the launcher has
    /// what a run needs.</summary>
    internal string Run()
    {
        if (Instance is null) throw new InvalidOperationException("there is nothing to run: open a profile first (workbench_act verb open_profile).");
        throw new InvalidOperationException("the launcher's run is not built in this build yet; run evalpipeline.py with an in-place entry.");
    }

    /// <summary>The houseCARL server bound to the open profile by explicit paths, its data folder under the Work Folder
    /// (GATE-B.md 3.1), started on the first forwarded call.</summary>
    Task<PatchWriter> BoundAsync()
    {
        lock (_gate)
        {
            if (_bound is not null) return Task.FromResult(_bound);
            if (_starting is not null) return _starting;
            NeedProfile("a houseCARL tool");
            if (Output is null) throw new InvalidOperationException("choose an Output Folder first (workbench_act verb set_output): in manual mode every houseCARL write goes under it.");
            if (WorkFolder is null) throw new InvalidOperationException("choose a Work Folder outside the instance and the game folder first (workbench_act verb set_work_folder): the houseCARL server keeps its data there.");
            var exe = Environment.GetEnvironmentVariable("WB_SERVER") is { Length: > 0 } s ? s : Program.DefaultServerExe();
            var corpus = Environment.GetEnvironmentVariable("WB_CORPUS") is { Length: > 0 } cp ? cp : Program.DefaultCorpus();
            var target = new WriteTarget(Instance!, Profile!, Path.Combine(Game!, "Data"));
            var data = Path.Combine(WorkFolder, "hcdata");
            var inst = Instance!;
            _starting = Task.Run(async () =>
            {
                var w = await PatchWriter.StartAsync(exe, target, new[] { inst }, data, corpus, WorkbenchHost.Say);
                lock (_gate) { _bound = w; _starting = null; }
                return w;
            });
            return _starting;
        }
    }

    internal async Task<CallToolResult> ForwardAsync(string tool, IDictionary<string, JsonElement>? args, CancellationToken ct)
    {
        string output; Mo2OutputLayout layout;
        lock (_gate)
        {
            NeedProfile(tool);
            if (Output is null) throw new InvalidOperationException("choose an Output Folder first (workbench_act verb set_output): in manual mode every houseCARL write goes under it.");
            output = Output; layout = Layout();
        }
        var (send, refusal) = ForwardRule.Prepare(tool, args is null ? null : new Dictionary<string, JsonElement>(args), output, layout);
        if (refusal is not null) return StateTools.Refuse(refusal);
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
                mods = Mods.Select(m => new { name = m.Name, priority = m.Priority, enabled = m.Enabled, separator = m.Separator, selected = m.Selected }).ToList(),
                probes = Registry().Select(c => new
                {
                    checkId = c.Id, kind = c.Kind, title = c.Title, selectable = ProbeKinds.Contains(c.Kind), selected = Probes.Contains(c.Id),
                    whyNot = ProbeKinds.Contains(c.Kind) ? null : $"a check of kind {c.Kind} runs no probe",
                }).ToList(),
                output = Output, outputVerdict = verdict, workFolder = WorkFolder,
                run = new { state = RunState, refusal = RunRefusal },
            };
        }
    }

    static string[] ProfilesOf(string inst)
    {
        var dir = Path.Combine(Mo2Instance.LayoutRoot(inst), "profiles");
        return Directory.Exists(dir) ? Directory.GetDirectories(dir).Select(Path.GetFileName).OfType<string>().OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToArray() : Array.Empty<string>();
    }
}
