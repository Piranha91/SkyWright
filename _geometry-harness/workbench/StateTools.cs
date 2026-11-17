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

    static readonly string[] Parts = { "bundle", "conflicts", "findings", "selection", "pending", "recheck", "repair", "launcher", "view", "decisions", "scenes", "context", "placements" };
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
                     "repair (the open navmesh row's previewed repair: its plan per navmesh, invariants and refusals, the links unproven in game, and the written patch with its read-back and re-check), " +
                     "findings (the Findings tab: every other selected check's rows, ranked; each says whether it opens the viewer and why not), " +
                     "launcher (manual mode: the mode, instance, profile and game, the profile's mod folders, the probes, the Output and Work Folders, the run), " +
                     "view (view-only state: originals shown, the ghost, hidden and see-through references, the Restore visibility label), " +
                     "decisions (each Conflicts row's decision: real problem, intentional or unsure, its reason, and whether it still applies or is flagged to reconsider; and decisions whose row is not in this run). " +
                     "scenes (the Conflicts rows by cell: inside a cell, rows naming a common reference are one cluster, and rows with equal values of the reporting skill's group key are one group; with each cell's clickable Findings rows), " +
                     "context (the selected reference: doors within 256 units, each 'touches it' or 'near only', with the far side; what names it; its enable parent; what rests on it and what physics does with it at load; the navmesh under its bounds; what stays behind a move; 'reading…' while the read runs), " +
                     "placements (find_clear's candidate positions: each one's move, drop, what it touches there, its word from the open row's re-check, what was and was not checked; wait_seconds waits for them). " +
                     "Default: selection, pending, recheck.")]
            string[]? project = null,
        [Description("conflicts: how many rows to list (default 20).")] int limit = 20,
        [Description("conflicts: how many rows to skip (default 0).")] int offset = 0,
        [Description("conflicts or findings: list only rows whose kind, plugins, references or base EditorIDs (findings: summary) contain this text (any case).")] string? filter = null,
        [Description("findings: list only the rows of this check id.")] string? check = null,
        [Description("conflicts: also list rows decided intentional (hidden by default, as in the window).")] bool include_decided = false,
        [Description("scenes: also list cells that hold only Findings rows (left out and counted by default).")] bool include_findings_only = false,
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
            var r0 = await hub.OnUi(() =>
            {
                running = hub.Surface.Scene is { Rechecking: true } s ? s.RecheckRunning
                        : hub.Surface.Scene?.Trials?.Running is { IsCompleted: false } tr ? tr
                        : hub.Launcher.RunTask is { IsCompleted: false } lr ? lr : null;
                return Task.FromResult(Answer(new { }));
            });
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
            if (parts.Contains("scenes")) d["scenes"] = ScenesJson(hub.Surface, hub.Standings, include_decided, include_findings_only);
            if (parts.Contains("context")) d["context"] = ContextJson(hub.Surface);
            if (parts.Contains("placements")) d["placements"] = PlacementsJson(hub.Surface);
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
                 "repair_preview (plan the open navmesh row's repair: relink a broken border, or move vertices to follow ground a later plugin moved; nothing is written) and " +
                 "repair_write (write that previewed plan to the new patch `patch` under the Output Folder, read every planned navmesh back against its winner, and re-check the order with the patch added: each link repaired, cut or refused). " +
                 "stage also takes `offset` (typed, such as \"+120 x\" or \"15°\"), `axis` (lock the move to x, y, z or xy), `snap` (round the moved axes to a grid, units) and `angle_snap` (degrees). " +
                 "revert (drop one reference's staged edits; a written one is removed from its patch); drop (lower a reference straight down onto the drawn surface or terrain below it, measured from the reference's lowest 16 units, so a wider part higher up that would touch first is not seen; it lowers only, and is refused when another surface already crosses the reference; render triangles, not collision); " +
                 "view (`op`: show_original, show_edited, ghost_on, ghost_off, hide, hide_part with `part`, hide_in_front, see_through with `ref`, `mod` or `plugin`, isolate, restore; view only, never written); " +
                 "decide (a Conflicts row by `conflict`: `verdict` real problem, intentional or unsure, and a short `reason`; it opens the next row to decide, a flagged one first; Findings rows are not decided yet). " +
                 "select also takes `scene` (a scene's number: opens its best row). move_with (`refs`: stage the selected reference's move, as one rigid move, for each named reference). " +
                 "find_clear (candidate positions for the selected reference of the open conflict, `max` default 3, at most 6, `snap` a grid: each is written as its own trial patch under the Work Folder and judged only by the open row's re-check, about 67-76 s each, in the background; read with workbench_state placements). " +
                 "choose (`n`: stage that candidate like any edit). " +
                 "Manual mode (the launcher): open_profile (`mode` free with `instance`, `profile`, optional `game`), select_mods (`mods`, `how` " +
                 "set/add/remove), select_probes (`checks`: check ids, probe names or [\"all\"]), set_output (`path`, the Output Folder every " +
                 "write goes under), set_work_folder (`path`, where a run keeps its files), run (the ticked probes, only `checks`, or [\"all\"]: every probe that can run here), " +
                 "abort (one probe of the running run by `checks`, leaving the rest running; with no checks, the whole run), capture_window (`path`, a .png of the window), show_tab (`tab`, by its header), search_mods (`search`, filters the Start tab's mod list by name). " +
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
        [Description("show_tab: the tab to bring to the front, by its header (Start, Conflicts, Findings or Records).")] string? tab = null,
        [Description("search_mods: the text the Start tab's mod list is filtered by (any case; empty shows every mod).")] string? search = null,
        [Description("stage: a typed offset, such as \"+120 x\", \"-30 z\" or \"15°\" (a turn about the vertical).")] string? offset = null,
        [Description("stage: lock the move to x, y, z or xy.")] string? axis = null,
        [Description("stage: round the axes the edit moved to this grid, in units (0 or absent: off).")] float? snap = null,
        [Description("stage: round a turn about the vertical to this step, in degrees.")] float? angle_snap = null,
        [Description("view: show_original, show_edited, ghost_on, ghost_off, hide, hide_part, hide_in_front, see_through, isolate or restore.")] string? op = null,
        [Description("view hide_part: which part of the reference, counting its drawn shapes from 0.")] int? part = null,
        [Description("view see_through: a mod folder, as the launcher lists it; the references its plugins win are drawn see-through.")] string? mod = null,
        [Description("view see_through: a plugin file name; the references it wins are drawn see-through.")] string? plugin = null,
        [Description("decide: real problem, intentional or unsure.")] string? verdict = null,
        [Description("decide: a few words saying why.")] string? reason = null,
        [Description("select: a scene by its number, as workbench_state's scenes list them; it opens the scene's best row.")] int? scene = null,
        [Description("move_with: the references to move with the selected one, by the same rigid move.")] string[]? refs = null,
        [Description("find_clear: how many candidate positions to check (default 3, at most 6).")] int? max = null,
        [Description("choose: the candidate's number, as workbench_state's placements list them.")] int? n = null)
        => hub.OnUi(async () =>
        {
            var v = (verb ?? "").Trim().ToLowerInvariant();
            if (LauncherVerb(verb)) return await LauncherOnUi(hub, verb, mode, instance, profile, game, mods, how, checks, path, tab, search);
            if (v == "decide") return await Decide(hub, conflict, finding, verdict, reason);
            if (v == "select" && scene is { } sn) return SelectScene(hub, sn);
            if (v == "move_with") { var mw = MoveWith(hub.Surface, refs); if (mw.IsError != true) SaveSession(hub.Surface); return mw; }
            if (v == "find_clear") return FindClear(hub.Surface, max, snap);
            if (v == "choose") { var ch = await Choose(hub.Surface, n); if (ch.IsError != true) SaveSession(hub.Surface); return ch; }
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
            var m = await DecisionCompare.RowAsync(s, d, r, hub.Standings);
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
        var checks = (s.Open?.Checks ?? Array.Empty<CheckStatus>()).Select(c => new { checkId = c.CheckId, state = c.State, rows = c.Rows, before = c.Before, why = c.Why }).ToList();
        return new { checks, total = all.Count, matched = rows.Count, offset, limit, rows = rows.Skip(offset).Take(limit).Select(FindingRowJson).ToList() };
    }

    /// <summary>Item 5: the selected reference's context, read once per scene off the UI thread; "reading…" per section until it lands.</summary>
    static object? ContextJson(IWorkbenchSurface s)
    {
        if (s.Scene is not { } sc) return new { refused = "no scene is shown." };
        if (sc.Session.Selected is not { } k) return new { refused = "nothing is selected; select a reference first." };
        var key = k.ToString();
        if (ContextCache.Read(sc, key) is not { } read) return new { @ref = key, refused = "this scene has no houseCARL server to read the reference's context from." };
        if (!read.IsCompleted) return new { @ref = key, sections = ContextRead.SectionNames.ToDictionary(n => n, _ => "reading…") };
        if (read.IsFaulted) return new { @ref = key, refused = read.Exception!.GetBaseException().Message };
        var a = read.Result;
        var moving = ContextCache.MovedWith(sc, key);
        return new
        {
            @ref = key,
            doors = a.Doors.Select(d => new { key = d.Key, label = d.Label, farDoor = d.FarDoor, farCell = d.FarCell,
                                              arrival = d.Arrival is { } p ? new[] { p.X, p.Y, p.Z } : null }).ToList(),
            names = a.Names.Select(n => new { key = n.Key, field = n.Field }).ToList(),
            enableParent = a.EnableParent, touches = a.Touches,
            resting = a.Resting.Select(r => new { key = r.Key, motion = r.Motion.ToString().ToLowerInvariant(), sentence = r.Sentence }).ToList(),
            navmesh = a.Navmesh.Select(n => new { navmesh = n.Navmesh, triangles = n.Triangles }).ToList(),
            sections = a.Sections, statement = a.Statement, movingWith = moving, staysBehind = ContextRead.StaysBehind(a, moving),
        };
    }

    /// <summary>Item 5: stage the selected reference's rigid move, from its load-order place to its staged pose, for each named reference.</summary>
    static CallToolResult MoveWith(IWorkbenchSurface s, string[]? refs)
    {
        var sc = SceneOr(s, out var none); if (sc is null) return none!;
        if (sc.Session.Selected is not { } host) return Refuse("nothing is selected; select the reference the others move with first.");
        if (refs is not { Length: > 0 }) return Refuse("move_with needs refs, the references to move with the selected one.");
        var keys = new List<FormKey>();
        foreach (var r in refs) { var key = Key(r, out var bad); if (key is null) return bad!; keys.Add(key.Value); }
        string said;
        try { said = MoveWithRule.Apply(sc, host, keys); } catch (InvalidOperationException ex) { return Refuse(ex.Message); }
        return Answer(new { said, state = Project(s, DefaultParts, 20, 0, null) });
    }

    /// <summary>Item 5: before a commit, what follows each moved host and what stays behind; one sentence per host read.</summary>
    static async Task<string> StaysBehindAsync(IWorkbenchScene sc)
    {
        var followers = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var hosts = sc.Session.Edited().Select(k => k.ToString()).ToList();
        foreach (var h in hosts) foreach (var f in ContextCache.MovedWith(sc, h)) followers.Add(f);
        var said = new List<string>();
        foreach (var h in hosts.Where(h => !followers.Contains(h)))
        {
            if (ContextCache.Read(sc, h) is not { } read) continue;
            try { said.Add($"{h}: " + ContextRead.StaysBehind(await read, ContextCache.MovedWith(sc, h))); }
            catch (Exception ex) { said.Add($"{h}: what stays behind could not be read: {ex.GetBaseException().Message}"); }
        }
        return said.Count == 0 ? "" : " " + string.Join(" ", said);
    }

    /// <summary>Item 8: candidate positions for the selected reference of the open row, judged by its re-check in the background.</summary>
    static CallToolResult FindClear(IWorkbenchSurface s, int? max, float? grid)
    {
        var sc = SceneOr(s, out var none); if (sc is null) return none!;
        if (sc.Session.Selected is not { } key) return Refuse("nothing is selected; select the reference to find a clear position for.");
        if (sc.Trials is not { } lane) return Refuse("this scene cannot run trials: it has no houseCARL server or no bundle to re-check with.");
        if (s.OpenRow is not { } row) return Refuse("find_clear needs an open conflict, whose re-check judges each candidate; open one first.");
        if (!Recheck.ProbeOfCheck.TryGetValue(row.Row.CheckId, out var probe)) return Refuse($"a {row.Row.CheckId} row has no probe re-run here, so its candidates cannot be judged.");
        if (sc.PairBoxes(key) is not { } boxes) return Refuse($"{key} or the open row's other reference is not drawn in this scene, so no candidate can be made.");
        if (max is < 1 or > Candidates.Max) return Refuse($"max must be 1 to {Candidates.Max}.");
        var cands = Candidates.Generate(boxes.Subject, boxes.Other, max ?? Candidates.Default);
        var loadOrder = RefPlacementEdit.BakedPose(sc.Session.RefOf(key).Ref);
        int others = sc.Session.Pending.Where(e => e.Ref != key).Select(e => e.Ref).Distinct().Count();
        string said;
        try { said = lane.Start(key, sc.Cell, loadOrder, cands, probe, others, sc.Session.Written.ContainsKey(key), grid ?? 0f); }
        catch (InvalidOperationException ex) { return Refuse(ex.Message); }
        sc.View?.ShowCandidates(key, lane.Placements.Select(p => p.Pose).ToList());
        return Answer(new { said = said + " The candidates are drawn as numbered tints (1 magenta, 2 orange, 3 cyan); none is staged until choose.",
                            rule = "candidates: from the two boxes, the smallest move along +x, -x, +y and -y that separates them, at 1x and 1.5x, nearest first, snapped to the grid on the axes moved, then dropped onto the surface below; judged only by the open row's re-check" });
    }

    static async Task<CallToolResult> Choose(IWorkbenchSurface s, int? n)
    {
        var sc = SceneOr(s, out var none); if (sc is null) return none!;
        if (sc.Trials is not { } lane || lane.Placements.Count == 0) return Refuse("there are no candidates; ask for them with find_clear first.");
        if (n is not { } num) return Refuse("choose needs n, the candidate's number.");
        if (sc.Session.Selected is not { } key) return Refuse("nothing is selected; select the reference the candidates were made for.");
        var (pose, said) = lane.Choose(num, sc.Cell, sc.Session.Current(key));
        if (pose is null) return Refuse(said);
        sc.Session.Stage(key, pose.Value);
        sc.View?.ClearCandidates();
        sc.AfterStage();
        // what the candidate leaves behind, said as the commit says it, from the same function
        if (ContextCache.Read(sc, key.ToString()) is { } read)
        {
            try { said += " " + ContextRead.StaysBehind(await read, ContextCache.MovedWith(sc, key.ToString())) + " To take any of them along, tick them in the context panel or use move_with with refs."; }
            catch (Exception ex) { said += " What stays behind could not be read: " + ex.GetBaseException().Message; }
        }
        return Answer(new { said, state = Project(s, DefaultParts, 20, 0, null) });
    }

    static object? PlacementsJson(IWorkbenchSurface s)
    {
        if (s.Scene?.Trials is not { } lane) return new { refused = "this scene cannot run trials." };
        static float[] V(Vec3 v) => new[] { v.X, v.Y, v.Z };
        return new
        {
            running = lane.Running is { IsCompleted: false },
            shown = s.Scene?.View?.CandidateTriangles().Select(t => t.N).ToList() ?? new List<int>(),
            workFolder = lane.WorkFolder,
            words = ClearRule.Meanings,
            candidates = lane.Placements.Select(p => new
            {
                n = p.N, move = p.Move, position = V(p.Pose.Position), rotationDeg = V(new Vec3(p.Pose.Rotation.X * 180 / MathF.PI, p.Pose.Rotation.Y * 180 / MathF.PI, p.Pose.Rotation.Z * 180 / MathF.PI)),
                drop = p.Drop, contacts = p.Contacts, word = p.Verdict?.Word ?? "checking", sentence = p.Verdict?.Sentence, rows = p.Verdict?.Rows,
                seconds = p.Seconds, @checked = p.Checked, notChecked = p.NotChecked, patchFolder = p.PatchFolder,
            }).ToList(),
        };
    }

    /// <summary>Item 7: open a scene's best row (a findings-only scene: its first Findings row).</summary>
    static CallToolResult SelectScene(StateHub hub, int number)
    {
        var s = hub.Surface;
        if (ReviewList.ScenesFor(s, hub.Standings, true) is not { } views) return Refuse("the reporting skill's scene policy was not found, so there are no scenes to open.");
        if (views.FirstOrDefault(v => v.Scene.Number == number) is not { } view)
            return Refuse($"no scene is numbered {number}; read workbench_state with project [\"scenes\"] to see them.");
        string said;
        try
        {
            if (view.Scene.BestRank is { } best && s.Listed.FirstOrDefault(r => r.Rank == best) is { } row) said = s.OpenConflict(row);
            else if (view.Scene.FindingRanks.Count > 0 && s.Findings.FirstOrDefault(f => f.Rank == view.Scene.FindingRanks[0]) is { } fr) said = s.OpenFinding(fr);
            else return Refuse($"scene {number} has no row that opens.");
        }
        catch (InvalidOperationException ex) { return Refuse(ex.Message); }
        SaveSession(s);
        return Answer(new { said = $"{view.Header}; {said}", state = Project(s, DefaultParts, 20, 0, null) });
    }

    static readonly string[] LauncherVerbs = { "open_profile", "select_mods", "select_probes", "set_output", "set_work_folder", "run", "abort", "capture_window", "show_tab", "search_mods" };
    static bool LauncherVerb(string? verb) => LauncherVerbs.Contains((verb ?? "").Trim().ToLowerInvariant());

    /// <summary>Save the whole window as a PNG at <paramref name="path"/> (an absolute .png path), as the script's capturewindow does.</summary>
    static string CaptureWindow(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path) || !path.EndsWith(".png", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("capture_window needs path, an absolute .png path.");
        if (Shell.Window is not { } w) throw new InvalidOperationException("no window is shown, so there is nothing to capture.");
        var (cw, ch) = WindowCapture.SaveWindow(w, path);
        return $"window captured ({cw}x{ch}) to {path}";
    }

    static async Task<CallToolResult> LauncherOnUi(StateHub hub, string verb, string? mode, string? instance, string? profile, string? game,
                                                   string[]? mods, string? how, string[]? checks, string? path, string? tab, string? search)
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
                "abort" => checks is { Length: > 0 } ? l.AbortProbe(checks[0]) : l.AbortRun(),
                "capture_window" => CaptureWindow(path),
                "show_tab" => Shell.ShowTab(tab),
                "search_mods" => LauncherView.Search(search),
                _ => checks is [var only] && string.Equals(only, "all", StringComparison.OrdinalIgnoreCase) ? l.RunAll()
                     : checks is { Length: > 0 } ? l.Run(checks) : l.Run(),
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
                    // an absolute rotation is the record's own angles; snapping them would not snap a tilted reference's turn, so it is said instead
                    sc.Session.Stage(k, MoveRules.Snap(before, after, snap ?? 0f, 0f));
                    sc.AfterStage();
                    said = $"staged {k}: {EditSession.Describe(before)} -> {EditSession.Describe(sc.Session.Current(k))}; nothing is written until commit";
                    if (absolute && rotationDeg is not null && angleSnap is > 0) said += "; angle_snap is not applied to an absolute rotation_degrees (use turn_degrees to snap a turn)";
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
                    var behind = await StaysBehindAsync(sc);                    // said before the write, from the scene as staged
                    var text = await sc.CommitAsync(dryRun, patch.Trim());
                    if (text is null) return Refuse(sc.CommitRefusal ?? "the commit was refused without a reason; read the window's log.");
                    text += behind;
                    said = dryRun ? "dry run, nothing written: " + text
                        : "written: " + text + (sc.Rechecking
                            ? $" The re-check of conflict {s.OpenRow?.Rank} is running (about 70-80 s); read workbench_state with wait_seconds 150 for its result."
                            : sc.RecheckRefusal is { } rr ? " The re-check was refused: " + rr
                            : s.OpenRow is null ? " No conflict is open, so nothing was re-checked." : "");
                    break;
                }
                case "repair_preview":
                {
                    var sc = SceneOr(s, out var none); if (sc is null) return none!;
                    var rp = await sc.RepairPreviewAsync();
                    said = "repair preview, nothing written: " + rp.Sentence;
                    break;
                }
                case "repair_write":
                {
                    var sc = SceneOr(s, out var none); if (sc is null) return none!;
                    if (string.IsNullOrWhiteSpace(patch)) return Refuse("repair_write needs patch, the new plugin's name (for example WbNavRepair).");
                    var rw = await sc.RepairWriteAsync(patch.Trim());
                    said = "repair written: " + rw.Sentence;
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
                    return Refuse($"'{verb}' is not a verb; use select, stage, undo, revert, drop, view, decide, commit, recheck, repair_preview, repair_write, open_profile, select_mods, select_probes, set_output, set_work_folder or run.");
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

    /// <summary>Each Conflicts row's scene number; empty when the skill's policy is not found.</summary>
    static IReadOnlyDictionary<int, int> SceneNumbers(IWorkbenchSurface s)
    {
        var map = new Dictionary<int, int>();
        if (s.ScenePolicy is not { } p) return map;
        foreach (var sc in Scenes.Build(s.Listed, s.Findings, p).Scenes)
            foreach (var r in sc.Clusters.SelectMany(c => c.Ranks)) map[r] = sc.Number;
        return map;
    }

    /// <summary>Item 7's scenes as the tab draws them, from the one list model.</summary>
    static object ScenesJson(IWorkbenchSurface s, Dictionary<string, DecisionMatch> standings, bool includeDecided, bool includeFindingsOnly = false)
    {
        if (ReviewList.ScenesViewFor(s, standings, includeDecided, includeFindingsOnly) is not { } view)
            return new { refused = "the reporting skill's scene policy was not found, so rows are not grouped into scenes." };
        var views = view.Scenes;
        return new
        {
            rule = "a scene is one cell; inside it, rows naming a common reference are one cluster, and rows with equal group-key values (" +
                   string.Join(", ", s.ScenePolicy!.Keys) + ") are one group",
            unplacedFindings = view.Unplaced,
            findingsOnlyCells = view.FindingsOnlyCells, findingsOnlyRows = view.FindingsOnlyRows,
            findingsOnly = includeFindingsOnly ? "listed" : view.FindingsOnlyCells > 0
                ? $"{view.FindingsOnlyCells} more cell(s) hold only Findings rows ({view.FindingsOnlyRows} rows); pass include_findings_only to list them" : "none",
            scenes = views.Select(v => new
            {
                number = v.Scene.Number, cell = v.Scene.Cell, bestRank = v.Scene.BestRank, header = v.Header, hidden = v.Hidden,
                rows = v.Rows.Select(e => new { rank = e.Row.Rank, standing = e.Standing.Standing.ToString().ToLowerInvariant(), verdict = e.Decision?.Verdict }),
                clusters = v.Scene.Clusters.Select(c => new { ranks = c.Ranks, groups = c.Groups.Select(g => new { key = g.Key, ranks = g.Ranks }) }),
                findings = v.Scene.FindingRanks,
            }).ToList(),
        };
    }

    /// <summary>The state as <c>workbench_state</c> answers it, built from the live objects each time.</summary>
    static object ReviewJson(ReviewEntry e, RankedRow? open, IReadOnlyDictionary<int, int>? sceneOf = null)
    {
        var r = e.Row;
        return new
        {
            scene = sceneOf is not null && sceneOf.TryGetValue(r.Rank, out var sn) ? sn : (int?)null,
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
            var sceneOf = SceneNumbers(s);
            var rows = review.Where(e => filter is null || Has(e.Row.Row.Kind) || Has(e.Row.Row.A) || Has(e.Row.Row.B) || Has(e.Row.Row.RefKey) || Has(e.Row.Row.OtherKey)
                                           || Has(e.Row.Row.RefBaseEditorId) || Has(e.Row.Row.OtherBaseEditorId)).ToList();
            d["conflicts"] = new
            {
                total = s.Total, listed = review.Count, hiddenDecided = s.Listed.Count - review.Count, matched = rows.Count, offset, limit,
                rows = rows.Skip(offset).Take(limit).Select(e => ReviewJson(e, s.OpenRow, sceneOf)),
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
        if (parts.Contains("repair"))
        {
            var rp = sc?.LastRepairPreview; var rw = sc?.LastRepairWritten;
            d["repair"] = rp is null ? null : new
            {
                row = rp.RowId, navmesh = rp.Navmesh, sentence = rp.Sentence, plan = rp.Plan, edits = rp.Ops.Count,
                plans = rp.Plans.Select(e => new { navmesh = e.Navmesh, @class = e.Class, status = e.Status, why = e.Why, adjacencyChange = e.AdjacencyChange, edits = e.Edits, invariants = e.Invariants }).ToList(),
                unproven = rp.Unproven,
                written = rw is null ? null : new
                {
                    patch = rw.Patch, readBackHolds = rw.ReadBackHolds, readBack = rw.ReadBack, sentence = rw.Sentence,
                    verdicts = rw.Verdicts.Select(v => new { row = v.Row, kind = v.Kind, verdict = v.Verdict }).ToList(), created = rw.Created,
                },
            };
        }
        return d;
    }
}
