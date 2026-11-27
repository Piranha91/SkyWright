using System.Text.Json;
using Mutagen.Bethesda.Plugins;
using ViewProbe;

namespace Workbench;

/// <summary>A staged edit as saved: the reference and its pose before and after.</summary>
internal sealed record SavedEdit(string Ref, RefPlacementEdit.Pose Before, RefPlacementEdit.Pose After, bool Disable = false);

/// <summary>A written edit as saved: the reference and the patch (and its folder) that holds it.</summary>
internal sealed record SavedWritten(string Ref, string Patch, string PatchFolder, bool Disable = false);

/// <summary>One review's session: selection, open row, camera, staged and written edits.</summary>
internal sealed record SessionData(string PlanId, string Arm, string? Selected, int? OpenConflict, float[]? Camera,
                                   IReadOnlyList<SavedEdit> Staged, IReadOnlyList<SavedWritten> Written, string? CameraCell = null);

/// <summary>The session file under the Work Folder (<c>sessions/&lt;planId&gt;-&lt;arm&gt;.json</c>), written on every change
/// and read when the same bundle is opened again. View-only state (hidden, see-through) is not kept: it is temporary.</summary>
internal static class SessionFile
{
    static readonly JsonSerializerOptions Json = new() { WriteIndented = true, IncludeFields = true };

    static string Safe(string s) => string.Concat(s.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c));

    /// <summary>The session file for a bundle and arm, or one sentence when there is no Work Folder to keep it in.</summary>
    internal static (string? Path, string? Refusal) PathFor(string? workFolder, string planId, string arm)
    {
        if (string.IsNullOrWhiteSpace(workFolder))
            return (null, "no Work Folder is set, so this review's session is not saved; set one in the launcher, or WB_OUT for a bundle.");
        return (Path.Combine(workFolder, "sessions", $"{Safe(planId)}-{Safe(arm)}.json"), null);
    }

    internal static void Write(string path, SessionData data)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var tmp = path + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(data, Json));
        File.Move(tmp, path, overwrite: true);
    }

    internal static SessionData? Read(string path)
    {
        if (!File.Exists(path)) return null;
        try { return JsonSerializer.Deserialize<SessionData>(File.ReadAllText(path), Json); }
        catch (JsonException ex) { throw new InvalidOperationException($"the session file '{path}' could not be read ({ex.Message}); move it aside to start a fresh session."); }
    }

    /// <summary>The session as it stands, from the surface's own objects.</summary>
    internal static SessionData? Capture(IWorkbenchSurface s)
    {
        if (s.PlanId is not { } plan || s.Arm is not { } arm) return null;
        var sc = s.Scene;
        var staged = sc?.Session.Pending.Select(e => new SavedEdit(e.Ref.ToString(), e.Before, e.After, e.Disable)).ToList() ?? new List<SavedEdit>();
        var written = sc is null ? new List<SavedWritten>()
            : sc.Session.Written.Select(kv => new SavedWritten(kv.Key.ToString(), kv.Value.Patch, sc.PatchFolderOf(kv.Value.Patch) ?? "", sc.Session.WrittenDisabled.Contains(kv.Key))).ToList();
        return new SessionData(plan, arm, sc?.Session.Selected?.ToString(), s.OpenRow?.Rank, sc?.Camera, staged, written, sc?.Cell);
    }

    /// <summary>The session to write: <paramref name="now"/> for the references this scene draws, and the previous
    /// file's entries for every reference it does not draw (they belong to another cell).</summary>
    internal static SessionData Merge(SessionData? previous, SessionData now, Func<string, bool> drawnHere)
    {
        if (previous is null || previous.PlanId != now.PlanId || previous.Arm != now.Arm) return now;
        var staged = now.Staged.Concat(previous.Staged.Where(e => !drawnHere(e.Ref))).ToList();
        var written = now.Written.Concat(previous.Written.Where(w => !drawnHere(w.Ref) && !now.Written.Any(x => string.Equals(x.Ref, w.Ref, StringComparison.OrdinalIgnoreCase)))).ToList();
        return now.Camera is null ? now with { Staged = staged, Written = written, Camera = previous.Camera, CameraCell = previous.CameraCell }
                                  : now with { Staged = staged, Written = written };
    }

    /// <summary>The conflict a reopened review starts on: the saved open row, when it is still listed.</summary>
    internal static int? StartRank(SessionData? saved, IReadOnlyList<RankedRow> rows) =>
        saved?.OpenConflict is { } n && rows.Any(r => r.Rank == n) ? n : null;

    /// <summary>The saved camera, only when it was saved in <paramref name="cell"/>.</summary>
    internal static float[]? CameraFor(SessionData saved, string? cell) =>
        cell is not null && string.Equals(saved.CameraCell, cell, StringComparison.OrdinalIgnoreCase) ? saved.Camera : null;

    /// <summary>Write the session where the surface keeps it; nothing when it has no bundle or no Work Folder.</summary>
    internal static void Save(IWorkbenchSurface s)
    {
        if (Capture(s) is not { } d) return;
        var (path, _) = PathFor(s.WorkFolder, d.PlanId, d.Arm);
        if (path is null) return;
        // the scene is one cell: the saved entries of references it does not draw are another cell's, and are kept
        var sc = s.Scene;
        bool Drawn(string k) => sc is not null && FormKey.TryFactory(k, out var fk) && sc.Session.IsDrawn(fk);
        Write(path, Merge(Read(path), d, Drawn));
    }

    /// <summary>Re-stage the saved edits whose reference still sits where it did, redraw written ones from their patch,
    /// restore the selection, and say, one sentence each, what was not restored.</summary>
    internal static IReadOnlyList<string> Restore(EditSession session, SessionData data,
                                                  Func<FormKey, SavedWritten, RefPlacementEdit.Pose?> readWritten)
    {
        var notes = new List<string>();
        foreach (var w in data.Written)
        {
            if (!FormKey.TryFactory(w.Ref, out var k) || !session.IsDrawn(k)) { notes.Add($"{w.Ref}'s written edit in {w.Patch} is kept for its cell; it is drawn again when that cell is open."); continue; }
            if (readWritten(k, w) is { } pose) { if (w.Disable) session.ShowWrittenDisabled(k, pose, w.Patch); else session.ShowWritten(k, pose, w.Patch); }
            else notes.Add($"{w.Ref}'s written edit was not restored: {w.Patch} no longer holds it.");
        }
        var stale = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var e in data.Staged)
        {
            if (stale.Contains(e.Ref)) continue;
            if (!FormKey.TryFactory(e.Ref, out var k) || !session.IsDrawn(k)) { if (stale.Add(e.Ref)) notes.Add($"{e.Ref}'s staged edit is kept for its cell; it is staged again when that cell is open."); continue; }
            var now = session.Current(k);
            if (!Same(now, e.Before))
            {
                notes.Add($"{e.Ref}'s staged edit was not restored: its placement changed since the session was saved.");
                stale.Add(e.Ref);
                continue;
            }
            if (e.Disable) session.StageDisable(k); else session.Stage(k, e.After);
        }
        if (data.Selected is { } sel && FormKey.TryFactory(sel, out var sk) && session.IsDrawn(sk)) session.Select(sk);
        return notes;
    }

    static bool Same(RefPlacementEdit.Pose a, RefPlacementEdit.Pose b) =>
        MathF.Abs(a.Position.X - b.Position.X) <= 0.01f && MathF.Abs(a.Position.Y - b.Position.Y) <= 0.01f && MathF.Abs(a.Position.Z - b.Position.Z) <= 0.01f
        && MathF.Abs(a.Rotation.X - b.Rotation.X) <= 1e-4f && MathF.Abs(a.Rotation.Y - b.Rotation.Y) <= 1e-4f && MathF.Abs(a.Rotation.Z - b.Rotation.Z) <= 1e-4f;
}
