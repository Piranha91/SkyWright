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

    static readonly string[] Parts = { "bundle", "conflicts", "selection", "pending", "recheck" };
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
                     "pending (the edits staged and not written, and the edits written), recheck (the last re-check's result). " +
                     "Default: selection, pending, recheck.")]
            string[]? project = null,
        [Description("conflicts: how many rows to list (default 20).")] int limit = 20,
        [Description("conflicts: how many rows to skip (default 0).")] int offset = 0,
        [Description("conflicts: list only rows whose kind, plugins, references or base EditorIDs contain this text (any case).")] string? filter = null,
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
            var r0 = await hub.OnUi(() => { running = hub.Surface.Scene is { Rechecking: true } s ? s.RecheckRunning : null; return Task.FromResult(Answer(new { })); });
            if (r0.IsError == true) return r0;
            if (running is not null) await Task.WhenAny(running, Task.Delay(TimeSpan.FromSeconds(Math.Min(wait_seconds, 600))));
        }
        return await hub.OnUi(() => Task.FromResult(Answer(Project(hub.Surface, parts, limit, offset, filter))));
    }

    [McpServerTool(Name = ActName, Destructive = false, OpenWorld = false, Title = "Act on the workbench"),
     Description("Do one thing in the workbench window, as its own buttons and clicks do, and answer with one sentence (`said`) and the state " +
                 "(selection, pending, recheck). Verbs: select (open a conflict by its rank with `conflict`, which lands the camera on its " +
                 "reference; or select a drawn reference with `ref`, or ref \"none\"); stage (move and/or turn a reference: relative with `delta` " +
                 "and `turn_degrees`, or absolute with `position` and `rotation_degrees`; the selected reference unless `ref` names one); " +
                 "undo (the last staged edit); commit (write the staged edits to the new patch plugin `patch`: `dry_run` true, the default, " +
                 "previews the values and masters and writes nothing; false writes, and then re-checks the open conflict in the background, " +
                 "about 70-80 s, read with workbench_state wait_seconds); recheck (re-check the open conflict against the written patch `patch`). " +
                 "The app never enables a patch: the load order is unchanged until the user enables it in Mod Organizer 2.")]
    public static Task<CallToolResult> Act(
        StateHub hub,
        [Description("select, stage, undo, commit or recheck.")] string verb,
        [Description("select: the rank of the conflict to open, as workbench_state's conflicts list it.")] int? conflict = null,
        [Description("select or stage: a reference as XXXXXX:Plugin.esp (6 hex digits, a colon, the defining plugin), or \"none\" for select.")] string? @ref = null,
        [Description("stage: the move, [dx, dy, dz] in Skyrim units (about 70 to a metre; +X east, +Y north, +Z up).")] float[]? delta = null,
        [Description("stage: the turn about the vertical (Z) axis, in degrees, composed with the current rotation.")] float? turn_degrees = null,
        [Description("stage: an absolute position, [x, y, z] in Skyrim units.")] float[]? position = null,
        [Description("stage: an absolute rotation, [x, y, z] in degrees, as the record's rotation fields.")] float[]? rotation_degrees = null,
        [Description("commit: true (default) previews and writes nothing; false writes the patch.")] bool dry_run = true,
        [Description("commit or recheck: the patch plugin's name, for example WbFixBarrel (.esp is added). A commit needs a name no mod folder holds yet.")]
            string? patch = null)
        => hub.OnUi(() => ActOnUi(hub.Surface, verb, conflict, @ref, delta, turn_degrees, position, rotation_degrees, dry_run, patch));

    static async Task<CallToolResult> ActOnUi(IWorkbenchSurface s, string verb, int? conflict, string? refKey, float[]? delta, float? turnDeg,
                                             float[]? position, float[]? rotationDeg, bool dryRun, string? patch)
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
                    bool relative = delta is not null || turnDeg is not null, absolute = position is not null || rotationDeg is not null;
                    if (relative && absolute) return Refuse("give either delta and turn_degrees (relative) or position and rotation_degrees (absolute), not both.");
                    if (!relative && !absolute) return Refuse("stage needs a move or a turn: delta [dx, dy, dz] and/or turn_degrees, or position and/or rotation_degrees.");
                    foreach (var (name, v) in new[] { ("delta", delta), ("position", position), ("rotation_degrees", rotationDeg) })
                        if (v is not null && (v.Length != 3 || v.Any(x => !float.IsFinite(x)))) return Refuse($"{name} must be three finite numbers, [x, y, z].");
                    var before = sc.Session.Current(k);
                    if (relative)
                        sc.Session.StageDelta(k, delta is { } d ? new Vector3(d[0], d[1], d[2]) : Vector3.Zero, Vector3.UnitZ, float.DegreesToRadians(turnDeg ?? 0f));
                    else
                        sc.Session.Stage(k, before with
                        {
                            Position = position is { } p ? new Vec3(p[0], p[1], p[2]) : before.Position,
                            Rotation = rotationDeg is { } r ? new Vec3(float.DegreesToRadians(r[0]), float.DegreesToRadians(r[1]), float.DegreesToRadians(r[2])) : before.Rotation,
                        });
                    sc.AfterStage();
                    said = $"staged {k}: {EditSession.Describe(before)} -> {EditSession.Describe(sc.Session.Current(k))}; nothing is written until commit";
                    break;
                }
                case "undo":
                {
                    var sc = SceneOr(s, out var none); if (sc is null) return none!;
                    var u = sc.Session.Undo();
                    sc.AfterStage();
                    if (u is null) return Refuse(sc.Session.Written.Count > 0 ? "there is no staged edit to undo; a written edit is undone by writing a new patch." : "there is no staged edit to undo.");
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
                    return Refuse($"'{verb}' is not a verb; use select, stage, undo, commit or recheck.");
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
    internal static Dictionary<string, object?> Project(IWorkbenchSurface s, IReadOnlyCollection<string> parts, int limit, int offset, string? filter)
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
            var rows = s.Listed.Where(r => filter is null || Has(r.Row.Kind) || Has(r.Row.A) || Has(r.Row.B) || Has(r.Row.RefKey) || Has(r.Row.OtherKey)
                                           || Has(r.Row.RefBaseEditorId) || Has(r.Row.OtherBaseEditorId)).ToList();
            d["conflicts"] = new { total = s.Total, listed = s.Listed.Count, matched = rows.Count, offset, limit, rows = rows.Skip(offset).Take(limit).Select(r => RowJson(r, s.OpenRow)) };
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
                    @ref = k.ToString(), @base = ro.Ref.BaseEditorId, cell = sc.CellOf(k),
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
                @ref = e.Ref.ToString(), before = PoseJson(e.Before), after = PoseJson(e.After),
                moved = new[] { e.After.Position.X - e.Before.Position.X, e.After.Position.Y - e.Before.Position.Y, e.After.Position.Z - e.Before.Position.Z },
            }).ToList();
            d["written"] = sc?.Session.Written.Select(kv => new { @ref = kv.Key.ToString(), patch = kv.Value.Patch, pose = PoseJson(kv.Value.Pose) }).ToList();
        }
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
