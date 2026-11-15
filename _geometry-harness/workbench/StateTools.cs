using System.ComponentModel;
using System.Numerics;
using System.Text.Json;
using HousecarlCore;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using Mutagen.Bethesda.Plugins;
using ViewProbe;

namespace Workbench;

/// <summary>The workbench's two MCP tools (DESIGN.md section 7): one read with a projection, one act with a verb. Each
/// call runs on the UI thread through <see cref="StateHub"/> and calls what the window calls (the edit session, the
/// Conflicts tab's row click, the bar's dry run and write, the re-check), so the app and a client share one state.</summary>
[McpServerToolType]
internal sealed class StateTools
{
    internal const string StateName = "workbench_state", ActName = "workbench_act";

    internal const string Instructions =
        "The conflict review workbench: a desktop window showing one cell of a Skyrim load order in 3D, with the conflicts an " +
        "evaluation bundle found, ranked. These tools read and drive the window's own state: the open conflict, the selected " +
        "reference, the edits staged but not written, the edits written to a patch, and the last re-check. A move is staged, " +
        "previewed by a dry run, written to a NEW patch plugin (never enabled by the app), and then re-checked: the detector " +
        "runs again on the patch's cell and says whether the open conflict is resolved and whether a new one appeared. " +
        "Positions are Skyrim units (about 70 units to a metre); +X is east, +Y is north, +Z is up.";

    static readonly string[] Parts = { "bundle", "conflicts", "findings", "selection", "pending", "recheck", "launcher", "view", "decisions" };
    static readonly string[] DefaultParts = { "selection", "pending", "recheck" };
    static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    internal static CallToolResult Refuse(string sentence) => new() { IsError = true, Content = [new TextContentBlock { Text = sentence }] };
    static CallToolResult Answer(object o) => new() { Content = [new TextContentBlock { Text = JsonSerializer.Serialize(o, Json) }] };

    [McpServerTool(Name = StateName, ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false, Title = "Read the workbench's state"),
     Description("Read the workbench window's state. Always answers with `window` (the cell shown, a cell being loaded, the banner, " +
                 "the status bar and the open conflict's rank), plus the parts named in `project`.")]
    public static async Task<CallToolResult> State(
        StateHub hub,
        [Description("Which parts to add, any of: bundle (the evaluation bundle open), conflicts (the ranked conflicts the Conflicts tab lists), " +
                     "selection (the open conflict and the selected reference: its placement as drawn, as the load order has it, and as written to a patch), " +
                     "pending (the edits staged and not written, and the edits written), recheck (the last re-check's result), " +
                     "findings (the Findings tab: every other selected check's rows, ranked; each says whether it opens the viewer and why not), " +
                     "launcher (manual mode: the mode, instance, profile and game, the profile's mod folders, the probes, the Output and Work Folders, the run), " +
                     "view (view-only state: originals shown, the ghost, hidden and see-through references, the Restore visibility label), " +
                     "decisions (each Conflicts row's decision: real problem, intentional or unsure, its reason, and whether it still applies or is flagged to reconsider; and decisions whose row is not in this run). " +
                     "Default: selection, pending, recheck.")]
            string[]? project = null,
        [Description("conflicts: how many rows to list (default 20).")] int limit = 20,
        [Description("conflicts: how many rows to skip (default 0).")] int offset = 0,
        [Description("conflicts or findings: list only rows whose kind, plugins, references or base EditorIDs (findings: summary) contain this text (any case).")] string? filter = null,
        [Description("findings: list only the rows of this check id.")] string? check = null,
        [Description("conflicts: also list rows decided intentional (hidden by default, as in the window).")] bool include_decided = false,
        [Description("If a re-check is running, wait up to this many seconds (at most 600) for it to finish before answering. Default 0.")]
            int wait_seconds = 0)
    {
        var parts = project is { Length: > 0 } ? project.Select(p => p.Trim().ToLowerInvariant()).ToArray() : DefaultParts;
        if (parts.FirstOrDefault(p => !Parts.Contains(p)) is { } bad)
            return Refuse($"'{bad}' is not a part of the state; project any of {string.Join(", ", Parts)}.");
        if (limit < 1 || offset < 0) return Refuse("limit must be 1 or more and offset 0 or more.");
        if (wait_seconds > 0)
        {
            Task? running = null;
            var r0 = await hub.OnUi(() => { running = hub.Surface.Scene is { Rechecking: true } s ? s.RecheckRunning : hub.Launcher.RunTask is { IsCompleted: false } lr ? lr : null; return Task.FromResult(Answer(new { })); });
            if (r0.IsError == true) return r0;
            if (running is not null) await Task.WhenAny(running, Task.Delay(TimeSpan.FromSeconds(Math.Min(wait_seconds, 600))));
        }
        return await hub.OnUi(async () =>
        {
            object? decisions = null;
            if (parts.Contains("decisions"))
            {
                try { decisions = await DecisionsPart(hub); }
                catch (InvalidOperationException ex) { return Refuse(ex.Message); }
            }
            var d = Project(hub.Surface, parts, limit, offset, filter, hub.Standings, include_decided);
            if (decisions is not null) d["decisions"] = decisions;
            if (parts.Contains("findings")) d["findings"] = FindingsPart(hub.Surface, limit, offset, filter, check);
            if (parts.Contains("launcher")) d["launcher"] = hub.Launcher.Project();
            return Answer(d);
        });
    }

    [McpServerTool(Name = ActName, Destructive = false, OpenWorld = false, Title = "Act on the workbench"),
     Description("Do one thing in the workbench window, as its own buttons and clicks do, and answer with one sentence (`said`) and the state " +
                 "(selection, pending, recheck). Verbs: select (open a conflict by its rank with `conflict`, which lands the camera on its " +
                 "reference; or select a drawn reference with `ref`, or ref \"none\"); stage (move and/or turn a reference: relative with `delta` " +
                 "and `turn_degrees`, or absolute with `position` and `rotation_degrees`; the selected reference unless `ref` names one); " +
                 "undo (the last staged edit); commit (write the staged edits to the new patch plugin `patch`: `dry_run` true, the default, " +
                 "previews the values and masters and writes nothing; false writes, and then re-checks the open conflict in the background, " +
                 "about 70-80 s, read with workbench_state wait_seconds); recheck (re-check the open conflict against the written patch `patch`). " +
                 "stage also takes `offset` (typed, such as \"+120 x\" or \"15°\"), `axis` (lock the move to x, y, z or xy), `snap` (round the moved axes to a grid, units) and `angle_snap` (degrees). " +
                 "revert (drop one reference's staged edits; a written one is removed from its patch); drop (lower a reference straight down onto the drawn surface or terrain below it, measured from the reference's lowest 16 units, so a wider part higher up that would touch first is not seen; it lowers only, and is refused when another surface already crosses the reference; render triangles, not collision); " +
                 "view (`op`: show_original, show_edited, ghost_on, ghost_off, hide, hide_part with `part`, hide_in_front, see_through with `ref`, `mod` or `plugin`, isolate, restore; view only, never written); " +
                 "decide (a Conflicts row by `conflict`: `verdict` real problem, intentional or unsure, and a short `reason`; it opens the next row to decide, a flagged one first; Findings rows are not decided yet). " +
                 "Manual mode (the launcher): open_profile (`mode` free with `instance`, `profile`, optional `game`), select_mods (`mods`, `how` " +
                 "set/add/remove), select_probes (`checks`: check ids, probe names or [\"all\"]), set_output (`path`, the Output Folder every " +
                 "write goes under), set_work_folder (`path`, where a run keeps its files), run. " +
                 "The app never enables a patch: the load order is unchanged until the user enables it in Mod Organizer 2.")]
    public static Task<CallToolResult> Act(
        StateHub hub,
        [Description("select, stage, undo, commit or recheck.")] string verb,
        [Description("select: the rank of the conflict to open, as workbench_state's conflicts list it.")] int? conflict = null,
        [Description("select: the rank of a Findings row to open, as workbench_state's findings list it.")] int? finding = null,
        [Description("select or stage: a reference as XXXXXX:Plugin.esp (6 hex digits, a colon, the defining plugin), or \"none\" for select.")] string? @ref = null,
        [Description("stage: the move, [dx, dy, dz] in Skyrim units (about 70 to a metre; +X east, +Y north, +Z up).")] float[]? delta = null,
        [Description("stage: the turn about the vertical (Z) axis, in degrees, composed with the current rotation.")] float? turn_degrees = null,
        [Description("stage: an absolute position, [x, y, z] in Skyrim units.")] float[]? position = null,
        [Description("stage: an absolute rotation, [x, y, z] in degrees, as the record's rotation fields.")] float[]? rotation_degrees = null,
        [Description("commit: true (default) previews and writes nothing; false writes the patch.")] bool dry_run = true,
        [Description("commit or recheck: the patch plugin's name, for example WbFixBarrel (.esp is added). A commit needs a name no mod folder holds yet.")]
            string? patch = null,
        [Description("open_profile: free (any MO2 instance, in place) or housecarl.")] string? mode = null,
        [Description("open_profile: the MO2 instance folder, by its full path (free mode).")] string? instance = null,
        [Description("open_profile: the profile's name (default: the instance's selected profile).")] string? profile = null,
        [Description("open_profile: the game's folder (the one holding Data), when the instance's ModOrganizer.ini names none.")] string? game = null,
        [Description("select_mods: mod folder names, as the launcher lists them.")] string[]? mods = null,
        [Description("select_mods: set (default), add or remove.")] string? how = null,
        [Description("select_probes: check ids or probe names, or [\"all\"] for every probe check.")] string[]? checks = null,
        [Description("set_output or set_work_folder: the folder, by its full path.")] string? path = null,
        [Description("stage: a typed offset, such as \"+120 x\", \"-30 z\" or \"15°\" (a turn about the vertical).")] string? offset = null,
        [Description("stage: lock the move to x, y, z or xy.")] string? axis = null,
        [Description("stage: round the axes the edit moved to this grid, in units (0 or absent: off).")] float? snap = null,
        [Description("stage: round a turn about the vertical to this step, in degrees.")] float? angle_snap = null,
        [Description("view: show_original, show_edited, ghost_on, ghost_off, hide, hide_part, hide_in_front, see_through, isolate or restore.")] string? op = null,
        [Description("view hide_part: which part of the reference, counting its drawn shapes from 0.")] int? part = null,
        [Description("view see_through: a mod folder, as the launcher lists it; the references its plugins win are drawn see-through.")] string? mod = null,
        [Description("view see_through: a plugin file name; the references it wins are drawn see-through.")] string? plugin = null,
        [Description("decide: real problem, intentional or unsure.")] string? verdict = null,
        [Description("decide: a few words saying why.")] string? reason = null)
        => hub.OnUi(async () =>
        {
            var v = (verb ?? "").Trim().ToLowerInvariant();
            if (LauncherVerb(verb)) return await LauncherOnUi(hub, verb, mode, instance, profile, game, mods, how, checks, path);
            if (v == "decide") return await Decide(hub, conflict, finding, verdict, reason);
            if (finding is { } fr) return await SelectFinding(hub.Surface, verb, fr);
            var r = v switch
            {
                "view" => await ViewOnUi(hub.Surface, op, @ref, part, mod, plugin),
                "revert" => await RevertOnUi(hub.Surface, @ref),
                "drop" => DropOnUi(hub.Surface, @ref),
                _ => await ActOnUi(hub.Surface, verb, conflict, @ref, delta, turn_degrees, position, rotation_degrees, dry_run, patch, offset, axis, snap, angle_snap),
            };
            if (r.IsError != true) SaveSession(hub.Surface);
            return r;
        });

    /// <summary>The session file follows every change the tools make; a failed save is said in the banner, never silent.</summary>
    static void SaveSession(IWorkbenchSurface s)
    {
        try { SessionFile.Save(s); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { s.SaveRefusal = $"the session file could not be written ({ex.Message})."; }
    }

    static object ViewJson(ViewState? v) => v is null ? new { available = false } : new
    {
        available = true, showingOriginal = v.ShowingOriginal, ghost = v.Ghost,
        hidden = v.Hidden.Select(k => k.ToString()).ToList(), hiddenParts = v.HiddenParts,
        seeThrough = v.SeeThroughRefs.Select(k => k.ToString()).ToList(), active = v.Active, label = v.Label,
    };

    static async Task<CallToolResult> ViewOnUi(IWorkbenchSurface s, string? op, string? refKey, int? part, string? mod, string? plugin)
    {
        var sc = SceneOr(s, out var none); if (sc is null) return none!;
        if (sc.View is not { } v) return Refuse("this scene has no view controls.");
        FormKey? K()
        {
            if (refKey is not null) return Key(refKey, out _);
            return sc.Session.Selected;
        }
        string said;
        try
        {
            switch ((op ?? "").Trim().ToLowerInvariant())
            {
                case "show_original": said = v.ShowOriginal(true); break;
                case "show_edited": said = v.ShowOriginal(false); break;
                case "ghost_on": v.Ghost = true; said = "the ghost of each edited reference's load-order place is shown"; break;
                case "ghost_off": v.Ghost = false; said = "the ghost is hidden"; break;
                case "hide":
                    if (K() is not { } hk) return Refuse("hide needs ref, or a selected reference.");
                    said = v.Hide(hk); break;
                case "hide_part":
                {
                    if (K() is not { } pk) return Refuse("hide_part needs ref, or a selected reference.");
                    var shapes = sc.Session.RefOf(pk).Shapes;
                    if (part is not { } pi || pi < 0 || pi >= shapes.Count) return Refuse($"hide_part needs part, from 0 to {shapes.Count - 1} for {pk}.");
                    said = v.HidePart(shapes[pi]); break;
                }
                case "hide_in_front":
                    if (K() is not { } fk) return Refuse("hide_in_front needs ref, or a selected reference.");
                    said = v.HideInFront(fk, sc.PickCentre); break;
                case "see_through":
                    if (mod is not null || plugin is not null)
                    {
                        var (refs, why, note) = await sc.RefsOfModAsync(mod!, plugin);
                        if (why is not null) return Refuse(why);
                        if (refs.Count == 0) return Refuse($"no reference drawn in this scene is won by {mod ?? plugin}" + (note.Length > 0 ? "; " + note : "."));
                        said = v.SeeThrough(refs, mod ?? plugin) + (note.Length > 0 ? "; " + note : "");
                    }
                    else if (K() is { } sk) said = v.SeeThrough(sk);
                    else return Refuse("see_through needs ref, mod or plugin, or a selected reference.");
                    break;
                case "isolate":
                {
                    if (K() is not { } a) return Refuse("isolate needs ref, or a selected reference.");
                    FormKey? b = s.OpenRow is { } o && o.Row.OtherKey is { } other && FormKey.TryFactory(other, out var ok) && ok != a ? ok : null;
                    said = v.Isolate(a, b); break;
                }
                case "restore": said = v.Restore(); break;
                default: return Refuse($"'{op}' is not a view operation; use show_original, show_edited, ghost_on, ghost_off, hide, hide_part, hide_in_front, see_through, isolate or restore.");
            }
        }
        catch (InvalidOperationException ex) { return Refuse(ex.Message); }
        sc.AfterStage();
        return Answer(new { said, view = ViewJson(v) });
    }

    static async Task<CallToolResult> RevertOnUi(IWorkbenchSurface s, string? refKey)
    {
        var sc = SceneOr(s, out var none); if (sc is null) return none!;
        FormKey k;
        if (refKey is not null) { var key = Key(refKey, out var bad); if (key is null) return bad!; k = key.Value; }
        else if (sc.Session.Selected is { } sel) k = sel;
        else return Refuse("revert needs ref, or a selected reference.");
        string said;
        try
        {
            if (sc.Session.Revert(k) is { } r) said = $"reverted {k}: back at {EditSession.Describe(r.Before)}";
            else if (sc.Session.Written.ContainsKey(k)) said = await sc.RevertWrittenAsync(k);
            else return Refuse($"there is nothing staged or written for {k} to revert.");
        }
        catch (InvalidOperationException ex) { return Refuse(ex.Message); }
        sc.AfterStage();
        return Answer(new { said, state = Project(s, DefaultParts, 20, 0, null) });
    }

    static CallToolResult DropOnUi(IWorkbenchSurface s, string? refKey)
    {
        var sc = SceneOr(s, out var none); if (sc is null) return none!;
        FormKey k;
        if (refKey is not null) { var key = Key(refKey, out var bad); if (key is null) return bad!; k = key.Value; }
        else if (sc.Session.Selected is { } sel) k = sel;
        else return Refuse("drop needs ref, or a selected reference.");
        try
        {
            var own = sc.Session.RefOf(k).Shapes;
            var shapes = sc.Shapes.Concat(own.Where(o => !sc.Shapes.Contains(o)));
            var ans = CaptureViews.Drop(CaptureViews.FromPickShapes(shapes), k.ToString());
            if (ans.Refusal is { } why) return Refuse(why);
            var dist = ans.Distance!.Value;
            sc.Session.StageDelta(k, new Vector3(0, 0, -dist), Vector3.UnitZ, 0f);
            sc.AfterStage();
            var onto = ans.Onto == CaptureViews.TerrainKey ? "the terrain" : $"{ans.Onto} ({ans.OntoName})";
            var said = FormattableString.Invariant($"lowered {k} {dist:0.##} units onto {onto}; drop lowers onto drawn surfaces (render triangles), not collision");
            return Answer(new { said, state = Project(s, DefaultParts, 20, 0, null) });
        }
        catch (InvalidOperationException ex) { return Refuse(ex.Message); }
    }

    static Decision? StoredFor(IWorkbenchSurface s, DecisionStore? store, string key) =>
        store is not null && s.Binding is { } b ? store.Find(b.Instance, b.Profile, key) : null;

    /// <summary>Each listed row's decision and standing, and the stored decisions whose row is not in this run.</summary>
    static async Task<object> DecisionsPart(StateHub hub)
    {
        var s = hub.Surface;
        var store = DecisionStore.For(s.WorkFolder);
        var rows = new List<(RankedRow Row, Decision? D, DecisionMatch M)>();
        foreach (var r in s.Listed)
        {
            var key = Decisions.RowKey(r.Row);
            var d = StoredFor(s, store, key);
            if (d is null) { rows.Add((r, null, new DecisionMatch(Standing.Undecided, "undecided"))); continue; }
            // the row half needs no scene and no server; the object half needs the row's cell open, and is that row's own answer
            var m = Decisions.CompareRow(d, s.RowStateOf(r));
            if (m.Standing != Standing.Reconsider)
            {
                if (hub.Standings.TryGetValue(key, out var cached)) m = cached;
                else if (s.Scene is { } sc)
                {
                    try
                    {
                        var (row, objects, build) = await sc.DecisionInputsAsync(r);
                        m = Decisions.Compare(d, row, objects, build);
                        hub.Standings[key] = m;
                    }
                    catch (InvalidOperationException ex) { m = new DecisionMatch(m.Standing, m.Sentence.TrimEnd('.') + " (" + ex.Message.TrimEnd('.') + ")."); }
                }
            }
            rows.Add((r, d, m));
        }
        var keys = s.Listed.Select(r => Decisions.RowKey(r.Row)).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var gone = store is not null && s.Binding is { } bb
            ? store.Of(bb.Instance, bb.Profile).Where(d => !keys.Contains(d.RowKey)).ToList()
            : new List<Decision>();
        var ordered = Decisions.Order(rows, x => (x.M.Standing, x.D?.Verdict));
        return new
        {
            store = store is null ? null : Path.Combine(s.WorkFolder!, DecisionStore.FileName),
            note = store is null ? "no Work Folder is set, so decisions are not kept; set one in the launcher, or WB_OUT for a bundle." : null,
            rows = ordered.Select(x => new
            {
                rank = x.Row.Rank, key = Decisions.RowKey(x.Row.Row), kind = x.Row.Row.Kind, verdict = x.D?.Verdict, reason = x.D?.Reason,
                standing = x.M.Standing.ToString().ToLowerInvariant(), sentence = x.M.Sentence, decided = x.D?.When,
            }).ToList(),
            notInThisRun = gone.Select(d => new { key = d.RowKey, verdict = d.Verdict, reason = d.Reason, decided = d.When, note = "this row is not in this run; the decision is kept" }).ToList(),
        };
    }

    static Task<CallToolResult> Decide(StateHub hub, int? conflict, int? finding, string? verdict, string? reason)
        => DecideOn(hub.Surface, hub.Standings, conflict, finding, verdict, reason);

    /// <summary>The one decide: the state tool and the window's decision buttons both call it.</summary>
    internal static async Task<CallToolResult> DecideOn(IWorkbenchSurface s, Dictionary<string, DecisionMatch> standings, int? conflict, int? finding, string? verdict, string? reason)
    {
        var v = (verdict ?? "").Trim().ToLowerInvariant();
        if (!Decisions.Verdicts.Contains(v)) return Refuse($"'{verdict}' is not a verdict; use real problem, intentional or unsure.");
        if (string.IsNullOrWhiteSpace(reason)) return Refuse("decide needs a reason, a few words saying why (for example: placed on purpose).");
        if (finding is not null) return Refuse("Findings rows are not decided yet; decide takes conflict, a Conflicts row's rank.");
        if (conflict is null) return Refuse("decide needs conflict, a Conflicts row's rank.");
        if (DecisionStore.For(s.WorkFolder) is not { } store) return Refuse("no Work Folder is set, so a decision cannot be kept; set one in the launcher, or WB_OUT for a bundle.");
        if (s.Binding is not { } b) return Refuse("no instance and profile are bound, so a decision cannot say which load order it is about.");
        string said = "";
        try
        {
            if (conflict is { } n)
            {
                var row = s.Listed.FirstOrDefault(r => r.Rank == n);
                if (row is null) return Refuse($"no conflict ranked {n} is listed; read workbench_state with project conflicts to see the ranks listed.");
                if (s.Scene is not { } sc) return Refuse("no scene is open to read the row's objects from; open a conflict first.");
                var (state, objects, build) = await sc.DecisionInputsAsync(row);
                var key = Decisions.RowKey(row.Row);
                store.Put(new Decision(b.Instance, b.Profile, row.Row.CheckId, key, v, reason.Trim(), DateTimeOffset.UtcNow, state, objects, build));
                standings[key] = new DecisionMatch(Standing.Applies, "applies, decided just now.");
                ReviewList.RaiseChanged();
                var next = ReviewList.NextUndecided(ReviewList.For(s, s.Listed, standings, includeDecided: true), n);
                said = $"recorded {v} for conflict {n} ({reason.Trim()})";
                if (next is not null)
                {
                    try { said += $"; opened conflict {next.Rank}, the next undecided: " + s.OpenConflict(next); }
                    catch (InvalidOperationException ex) { said += $"; conflict {next.Rank} is the next undecided, but it could not be opened: {ex.Message}"; }
                }
                else said += "; every listed conflict is decided";
            }
        }
        catch (InvalidOperationException ex) { return Refuse(ex.Message); }
        catch (IOException ex) { return Refuse($"the decisions file could not be written ({ex.Message})."); }
        return Answer(new { said, state = Project(s, DefaultParts, 20, 0, null) });
    }

    static Task<CallToolResult> SelectFinding(IWorkbenchSurface s, string verb, int rank)
    {
        if (!string.Equals(verb?.Trim(), "select", StringComparison.OrdinalIgnoreCase)) return Task.FromResult(Refuse("finding is an input of select only."));
        var row = s.Findings.FirstOrDefault(r => r.Rank == rank);
        if (row is null) return Task.FromResult(Refuse($"no Findings row ranked {rank} is listed; read workbench_state with project [\"findings\"] to see the ranks."));
        if (!row.Row.Clickable) return Task.FromResult(Refuse($"finding {rank} does not open the viewer: {row.Row.WhyNot}."));
        string said;
        try { said = s.OpenFinding(row); } catch (InvalidOperationException ex) { return Task.FromResult(Refuse(ex.Message)); }
        return Task.FromResult(Answer(new { said, state = Project(s, DefaultParts, 20, 0, null) }));
    }

    static object FindingRowJson(RankedFinding f) => new
    {
        rank = f.Rank, checkId = f.Row.CheckId, arm = f.Row.Arm, kind = f.Row.Kind, a = f.Row.A, b = f.Row.B, summary = f.Row.Summary,
        @ref = f.Row.RefKey, cell = f.Row.Cell, clickable = f.Row.Clickable, whyNot = f.Row.WhyNot, role = f.Row.Role,
        isNew = f.NewLabel, evidence = f.Row.EvidenceStrength, severity = f.Severity, identity = f.Row.Identity, source = f.Row.Source,
    };

    static object FindingsPart(IWorkbenchSurface s, int limit, int offset, string? filter, string? check)
    {
        var all = s.Findings;
        bool Has(string? v) => v is not null && filter is not null && v.Contains(filter, StringComparison.OrdinalIgnoreCase);
        var rows = all.Where(r => (check is null || string.Equals(r.Row.CheckId, check, StringComparison.OrdinalIgnoreCase))
                                  && (filter is null || Has(r.Row.Kind) || Has(r.Row.A) || Has(r.Row.B) || Has(r.Row.RefKey) || Has(r.Row.Summary))).ToList();
        var checks = (s.Open?.Checks ?? Array.Empty<CheckStatus>()).Select(c => new { checkId = c.CheckId, state = c.State, rows = c.Rows, why = c.Why }).ToList();
        return new { checks, total = all.Count, matched = rows.Count, offset, limit, rows = rows.Skip(offset).Take(limit).Select(FindingRowJson).ToList() };
    }

    static readonly string[] LauncherVerbs = { "open_profile", "select_mods", "select_probes", "set_output", "set_work_folder", "run" };
    static bool LauncherVerb(string? verb) => LauncherVerbs.Contains((verb ?? "").Trim().ToLowerInvariant());

    static async Task<CallToolResult> LauncherOnUi(StateHub hub, string verb, string? mode, string? instance, string? profile, string? game,
                                                   string[]? mods, string? how, string[]? checks, string? path)
    {
        var l = hub.Launcher;
        string said;
        try
        {
            said = verb.Trim().ToLowerInvariant() switch
            {
                "open_profile" => await l.OpenProfileAsync(mode, instance, profile, game),
                "select_mods" => l.SelectMods(mods, how),
                "select_probes" => l.SelectProbes(checks),
                "set_output" => l.SetOutput(path),
                "set_work_folder" => l.SetWorkFolder(path),
                _ => l.Run(),
            };
        }
        catch (InvalidOperationException ex) { return Refuse(ex.Message); }
        return Answer(new { said, launcher = l.Project() });
    }

    static async Task<CallToolResult> ActOnUi(IWorkbenchSurface s, string verb, int? conflict, string? refKey, float[]? delta, float? turnDeg,
                                             float[]? position, float[]? rotationDeg, bool dryRun, string? patch,
                                             string? offset = null, string? axis = null, float? snap = null, float? angleSnap = null)
    {
        string said;
        try
        {
            switch ((verb ?? "").Trim().ToLowerInvariant())
            {
                case "select":
                {
                    if ((conflict is null) == (refKey is null)) return Refuse("select takes exactly one of conflict (a rank) or ref (a reference).");
                    if (conflict is { } n)
                    {
                        var row = s.Listed.FirstOrDefault(r => r.Rank == n);
                        if (row is null) return Refuse($"no conflict ranked {n} is listed; read workbench_state with project [\"conflicts\"] to see the ranks listed.");
                        said = s.OpenConflict(row);
                        break;
                    }
                    var sc = SceneOr(s, out var none); if (sc is null) return none!;
                    if (string.Equals(refKey, "none", StringComparison.OrdinalIgnoreCase)) { sc.Select(null); said = "selection cleared"; break; }
                    var key = Key(refKey!, out var badKey); if (key is null) return badKey!;
                    sc.Select(key);
                    sc.AfterStage();
                    said = $"selected {key}";
                    break;
                }
                case "stage":
                {
                    var sc = SceneOr(s, out var none); if (sc is null) return none!;
                    FormKey k;
                    if (refKey is not null) { var key = Key(refKey, out var badKey); if (key is null) return badKey!; k = key.Value; }
                    else if (sc.Session.Selected is { } sel) k = sel;
                    else return Refuse("nothing is selected; select a reference first, or name it with ref.");
                    if (offset is not null)
                    {
                        if (delta is not null || turnDeg is not null) return Refuse("give either offset or delta and turn_degrees, not both.");
                        var (od, ot, oerr) = MoveRules.ParseOffset(offset);
                        if (oerr is not null) return Refuse(oerr);
                        delta = new[] { od.X, od.Y, od.Z };
                        if (ot != 0f) turnDeg = ot;
                    }
                    var lockAxes = MoveRules.ParseLock(axis);
                    bool relative = delta is not null || turnDeg is not null, absolute = position is not null || rotationDeg is not null;
                    if (relative && absolute) return Refuse("give either delta and turn_degrees (relative) or position and rotation_degrees (absolute), not both.");
                    if (!relative && !absolute) return Refuse("stage needs a move or a turn: delta [dx, dy, dz] and/or turn_degrees, or position and/or rotation_degrees.");
                    foreach (var (name, v) in new[] { ("delta", delta), ("position", position), ("rotation_degrees", rotationDeg) })
                        if (v is not null && (v.Length != 3 || v.Any(x => !float.IsFinite(x)))) return Refuse($"{name} must be three finite numbers, [x, y, z].");
                    var before = sc.Session.Current(k);
                    RefPlacementEdit.Pose after;
                    if (relative)
                    {
                        var dv = MoveRules.Lock(delta is { } d ? new Vector3(d[0], d[1], d[2]) : Vector3.Zero, lockAxes);
                        // the turn is snapped as an amount about the axis, so it applies to a tilted reference too
                        float rad = float.DegreesToRadians(MoveRules.SnapTurn(turnDeg ?? 0f, angleSnap ?? 0f));
                        after = before with
                        {
                            Position = new Vec3(before.Position.X + dv.X, before.Position.Y + dv.Y, before.Position.Z + dv.Z),
                            Rotation = rad == 0f ? before.Rotation : RefPlacementEdit.Turned(before.Rotation, Vector3.UnitZ, rad),
                        };
                    }
                    else
                        after = before with
                        {
                            Position = position is { } p ? new Vec3(p[0], p[1], p[2]) : before.Position,
                            Rotation = rotationDeg is { } r ? new Vec3(float.DegreesToRadians(r[0]), float.DegreesToRadians(r[1]), float.DegreesToRadians(r[2])) : before.Rotation,
                        };
                    if (snap is < 0 || angleSnap is < 0) return Refuse("snap and angle_snap must be 0 or more.");
                    sc.Session.Stage(k, MoveRules.Snap(before, after, snap ?? 0f, relative ? 0f : angleSnap ?? 0f));
                    sc.AfterStage();
                    said = $"staged {k}: {EditSession.Describe(before)} -> {EditSession.Describe(sc.Session.Current(k))}; nothing is written until commit";
                    break;
                }
                case "undo":
                {
                    var sc = SceneOr(s, out var none); if (sc is null) return none!;
                    var u = sc.Session.Undo();
                    sc.AfterStage();
                    if (u is null) return Refuse(sc.Session.Written.Count > 0 ? "there is no staged edit to undo; a written edit is undone with revert, which removes it from its patch." : "there is no staged edit to undo.");
                    said = $"undid {u.Ref}: back at {EditSession.Describe(u.Before)}";
                    break;
                }
                case "commit":
                {
                    var sc = SceneOr(s, out var none); if (sc is null) return none!;
                    if (string.IsNullOrWhiteSpace(patch)) return Refuse("commit needs patch, the new plugin's name (for example WbFixBarrel).");
                    var text = await sc.CommitAsync(dryRun, patch.Trim());
                    if (text is null) return Refuse(sc.CommitRefusal ?? "the commit was refused without a reason; read the window's log.");
                    said = dryRun ? "dry run, nothing written: " + text
                        : "written: " + text + (sc.Rechecking
                            ? $" The re-check of conflict {s.OpenRow?.Rank} is running (about 70-80 s); read workbench_state with wait_seconds 150 for its result."
                            : sc.RecheckRefusal is { } rr ? " The re-check was refused: " + rr
                            : s.OpenRow is null ? " No conflict is open, so nothing was re-checked." : "");
                    break;
                }
                case "recheck":
                {
                    var sc = SceneOr(s, out var none); if (sc is null) return none!;
                    if (string.IsNullOrWhiteSpace(patch)) return Refuse("recheck needs patch, the written patch plugin's name.");
                    if (sc.Rechecking) return Refuse("a re-check is already running; read workbench_state with wait_seconds for its result.");
                    var t = sc.StartRecheck(patch.Trim());
                    if (t.IsCompleted && sc.RecheckRefusal is { } rr) return Refuse("the re-check was refused: " + rr);
                    said = $"re-checking conflict {s.OpenRow?.Rank} against {patch.Trim()} (about 70-80 s); read workbench_state with wait_seconds 150 for its result.";
                    break;
                }
                default:
                    return Refuse($"'{verb}' is not a verb; use select, stage, undo, revert, drop, view, decide, commit, recheck, open_profile, select_mods, select_probes, set_output, set_work_folder or run.");
            }
        }
        catch (InvalidOperationException ex) { return Refuse(ex.Message); }
        return Answer(new { said, state = Project(s, DefaultParts, 20, 0, null) });
    }

    static IWorkbenchScene? SceneOr(IWorkbenchSurface s, out CallToolResult? none)
    {
        none = s.Scene is null ? Refuse(s.Loading is { } c ? $"the window is loading cell {c}; read the state again in a minute." : "no scene is shown; open a conflict first.") : null;
        return s.Scene;
    }

    static FormKey? Key(string text, out CallToolResult? bad)
    {
        bad = null;
        if (FormKey.TryFactory(text.Trim(), out var k)) return k;
        bad = Refuse($"'{text}' is not a reference key; write it as XXXXXX:Plugin.esp, for example 09A3BB:Skyrim.esm.");
        return null;
    }

    static object PoseJson(RefPlacementEdit.Pose p) => new
    {
        position = new[] { p.Position.X, p.Position.Y, p.Position.Z },
        rotationDegrees = new[] { Deg(p.Rotation.X), Deg(p.Rotation.Y), Deg(p.Rotation.Z) },
    };
    static float Deg(float rad) => MathF.Round(float.RadiansToDegrees(rad), 4);

    static object RowJson(RankedRow r, RankedRow? open) => new
    {
        rank = r.Rank, open = ReferenceEquals(r, open) || (open is not null && open.Row.Identity == r.Row.Identity),
        kind = r.Row.Kind, a = r.Row.A, b = r.Row.B, @ref = r.Row.RefKey, refBase = r.Row.RefBaseEditorId, refType = r.Row.RefBaseType,
        other = r.Row.OtherKey, otherBase = r.Row.OtherBaseEditorId, cell = r.Row.Cell, summary = r.Row.Summary,
        physical = r.PhysicalLabel, evidence = r.EvidenceLabel, isNew = r.NewLabel, severity = r.Severity,
    };

    /// <summary>The state as <c>workbench_state</c> answers it, built from the live objects each time.</summary>
    static object ReviewJson(ReviewEntry e, RankedRow? open)
    {
        var r = e.Row;
        return new
        {
            rank = r.Rank, open = ReferenceEquals(r, open) || (open is not null && open.Row.Identity == r.Row.Identity),
            kind = r.Row.Kind, a = r.Row.A, b = r.Row.B, @ref = r.Row.RefKey, refBase = r.Row.RefBaseEditorId, refType = r.Row.RefBaseType,
            other = r.Row.OtherKey, otherBase = r.Row.OtherBaseEditorId, cell = r.Row.Cell, summary = r.Row.Summary,
            physical = r.PhysicalLabel, evidence = r.EvidenceLabel, isNew = r.NewLabel, severity = r.Severity,
            verdict = e.Decision?.Verdict, reason = e.Decision?.Reason, standing = e.Standing.Standing.ToString().ToLowerInvariant(), decisionSentence = e.Standing.Sentence,
        };
    }

    internal static Dictionary<string, object?> Project(IWorkbenchSurface s, IReadOnlyCollection<string> parts, int limit, int offset, string? filter,
                                                        Dictionary<string, DecisionMatch>? standings = null, bool includeDecided = false)
    {
        var sc = s.Scene;
        var d = new Dictionary<string, object?>
        {
            ["window"] = new { cell = sc?.Cell, loading = s.Loading, banner = s.Banner, status = sc?.Status, openConflict = s.OpenRow?.Rank },
        };
        if (parts.Contains("bundle")) d["bundle"] = s.Bundle;
        if (parts.Contains("conflicts"))
        {
            bool Has(string? v) => v is not null && filter is not null && v.Contains(filter, StringComparison.OrdinalIgnoreCase);
            var review = ReviewList.For(s, s.Listed, standings ?? new(StringComparer.OrdinalIgnoreCase), includeDecided);
            var rows = review.Where(e => filter is null || Has(e.Row.Row.Kind) || Has(e.Row.Row.A) || Has(e.Row.Row.B) || Has(e.Row.Row.RefKey) || Has(e.Row.Row.OtherKey)
                                           || Has(e.Row.Row.RefBaseEditorId) || Has(e.Row.Row.OtherBaseEditorId)).ToList();
            d["conflicts"] = new
            {
                total = s.Total, listed = review.Count, hiddenDecided = s.Listed.Count - review.Count, matched = rows.Count, offset, limit,
                rows = rows.Skip(offset).Take(limit).Select(e => ReviewJson(e, s.OpenRow)),
            };
        }
        if (parts.Contains("selection"))
        {
            object? sel = null;
            if (sc?.Session.Selected is { } k && sc.Session.IsDrawn(k))
            {
                var ro = sc.Session.RefOf(k);
                bool written = sc.Session.Written.TryGetValue(k, out var w);
                int staged = sc.Session.Pending.Count(e => e.Ref == k);
                sel = new
                {
                    @ref = k.ToString(), @base = ro.Ref.BaseEditorId, cell = sc.CellOf(k), parts = ro.Shapes.Count,
                    drawn = PoseJson(sc.Session.Current(k)),
                    loadOrder = PoseJson(RefPlacementEdit.BakedPose(ro.Ref)),
                    written = written ? new { patch = w.Patch, pose = PoseJson(w.Pose) } : null,
                    stagedEdits = staged,
                    view = staged > 0 ? $"the view shows {k} at its staged pose, which is not written yet"
                         : written ? EditSession.WrittenLabel(k, w.Patch)
                         : $"the view shows {k} where the load order places it",
                };
            }
            d["openConflict"] = s.OpenRow is { } o ? RowJson(o, o) : null;
            d["selection"] = sel;
        }
        if (parts.Contains("pending"))
        {
            d["pending"] = sc?.Session.Pending.Select(e => new
            {
                @ref = e.Ref.ToString(), sentence = EditSession.Sentence(e, sc.Session.IsDrawn(e.Ref) ? sc.Session.RefOf(e.Ref).Ref.BaseEditorId : null),
                before = PoseJson(e.Before), after = PoseJson(e.After),
                moved = new[] { e.After.Position.X - e.Before.Position.X, e.After.Position.Y - e.Before.Position.Y, e.After.Position.Z - e.Before.Position.Z },
            }).ToList();
            d["written"] = sc?.Session.Written.Select(kv => new { @ref = kv.Key.ToString(), patch = kv.Value.Patch, pose = PoseJson(kv.Value.Pose) }).ToList();
        }
        if (parts.Contains("view")) d["view"] = ViewJson(sc?.View);
        if (parts.Contains("recheck"))
        {
            static object V(RowVerdict v) => new { id = v.Id, word = v.Word, sentence = v.Sentence };
            d["recheck"] = sc is null ? null : new
            {
                running = sc.Rechecking, refused = sc.RecheckRefusal,
                row = sc.LastRecheck is { } lr ? V(lr.Row) : null,
                created = sc.LastRecheck?.New.Select(V).ToList(),
                verdict = sc.LastRecheck?.Verdict, build = sc.LastRecheck?.BuildSentence, wallSeconds = sc.LastRecheck?.WallSeconds,
            };
        }
        return d;
    }
}
