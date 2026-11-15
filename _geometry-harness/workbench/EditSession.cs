using System.Numerics;
using HousecarlCore;
using Mutagen.Bethesda.Plugins;
using ViewProbe;

namespace Workbench;

/// <summary>One staged placement edit: the reference and its pose before and after. Nothing is on disk until a commit.</summary>
internal sealed record PendingEdit(FormKey Ref, RefPlacementEdit.Pose Before, RefPlacementEdit.Pose After);

/// <summary>The workbench's session state: the selected reference and the ordered list of staged edits. The window and
/// (stage 5) the MCP state tools both call these methods; nothing keeps a second copy of the state.
///
/// Every edit re-places the reference on screen through <see cref="RefPlacementEdit"/>; undo pops the last edit and
/// re-places the reference at that edit's <see cref="PendingEdit.Before"/>, so the screen and the list agree.</summary>
internal sealed class EditSession
{
    readonly Dictionary<FormKey, RefOcclusion> _refs = new();
    readonly List<PendingEdit> _pending = new();

    internal EditSession(IEnumerable<RefOcclusion> refs)
    {
        foreach (var ro in refs)
            if (!ro.Ref.RefKey.IsNull && ro.Meshes.Count > 0) _refs[ro.Ref.RefKey] = ro;   // drawn refs only; first wins
    }

    internal FormKey? Selected { get; private set; }
    internal IReadOnlyList<PendingEdit> Pending => _pending;
    internal event Action<string>? Changed;

    internal bool IsDrawn(FormKey key) => _refs.ContainsKey(key);
    internal RefOcclusion RefOf(FormKey key) => _refs.TryGetValue(key, out var ro) ? ro
        : throw new InvalidOperationException($"{key} is not a drawn reference in this scene; select one the window shows.");

    /// <summary>Edits written to a patch that is not enabled: what the view shows for them and which patch holds them.</summary>
    internal IReadOnlyDictionary<FormKey, (string Patch, RefPlacementEdit.Pose Pose)> Written => _written;
    readonly Dictionary<FormKey, (string Patch, RefPlacementEdit.Pose Pose)> _written = new();

    /// <summary>After a write: the staged edits become written ones (stage-4 review M-1).</summary>
    internal void MarkWritten(string patch)
    {
        var net = Net();
        foreach (var e in net) _written[e.Ref] = (patch, e.After);
        _pending.Clear();
        Changed?.Invoke($"{net.Count} edit(s) written to {patch}; the view keeps them where the patch puts them (the patch is not enabled)");
    }

    /// <summary>Draw a reference at a placement a patch holds (read back from it), labelled as written.</summary>
    internal void ShowWritten(FormKey key, RefPlacementEdit.Pose pose, string patch)
    {
        RefPlacementEdit.Apply(RefOf(key), pose);
        _written[key] = (patch, pose);
        Changed?.Invoke($"{key} drawn as {patch} places it ({Describe(pose)})");
    }

    /// <summary>What the status bar says about a written reference the view shows.</summary>
    internal static string WrittenLabel(FormKey key, string patch) =>
        $"the view shows {key} as written to {patch}, not enabled (the load order still places it where it was)";

    /// <summary>The pose the reference is drawn at now: its last staged edit, else the build's.</summary>
    internal RefPlacementEdit.Pose Current(FormKey key)
    {
        for (int i = _pending.Count - 1; i >= 0; i--) if (_pending[i].Ref == key) return _pending[i].After;
        if (_written.TryGetValue(key, out var w)) return w.Pose;
        return RefPlacementEdit.BakedPose(RefOf(key).Ref);
    }

    internal void Select(FormKey? key)
    {
        if (key is { } k) RefOf(k);
        Selected = key;
        Changed?.Invoke(key is { } s ? $"selected {s}" : "selection cleared");
    }

    /// <summary>Show <paramref name="pose"/> on screen without staging it (a handle drag in progress).</summary>
    internal RefPlacementEdit.Edit Preview(FormKey key, RefPlacementEdit.Pose pose) => RefPlacementEdit.Apply(RefOf(key), pose);

    /// <summary>Stage one edit: re-place the reference and append it to the pending list.</summary>
    internal PendingEdit Stage(FormKey key, RefPlacementEdit.Pose after)
    {
        var before = Current(key);
        var applied = RefPlacementEdit.Apply(RefOf(key), after);
        var e = new PendingEdit(key, before, after);
        _pending.Add(e);
        Changed?.Invoke($"staged {key}: {Describe(before)} -> {Describe(after)}"
                        + (applied.Stale.Count > 0 ? "; " + string.Join("; ", applied.Stale) : ""));
        return e;
    }

    /// <summary>Move by <paramref name="delta"/> (Skyrim units) and turn by <paramref name="radians"/> about world
    /// <paramref name="axis"/>, composed with the current rotation.</summary>
    internal PendingEdit StageDelta(FormKey key, Vector3 delta, Vector3 axis, float radians)
    {
        var c = Current(key);
        var rot = radians == 0f ? c.Rotation : RefPlacementEdit.Turned(c.Rotation, axis, radians);
        return Stage(key, c with { Position = new Vec3(c.Position.X + delta.X, c.Position.Y + delta.Y, c.Position.Z + delta.Z), Rotation = rot });
    }

    /// <summary>Undo the last staged edit, on screen and in the list. Returns null when there is nothing to undo.</summary>
    internal PendingEdit? Undo()
    {
        if (_pending.Count == 0) { Changed?.Invoke(_written.Count > 0 ? "nothing staged to undo; a written edit is undone by a new write" : "nothing to undo"); return null; }
        var last = _pending[^1];
        _pending.RemoveAt(_pending.Count - 1);
        RefPlacementEdit.Apply(RefOf(last.Ref), last.Before);
        Changed?.Invoke($"undid {last.Ref}: back at {Describe(last.Before)}");
        return last;
    }

    /// <summary>The edits a commit writes: one per reference, its latest pose, in first-edited order.</summary>
    internal IReadOnlyList<PendingEdit> Net()
    {
        var order = new List<FormKey>();
        foreach (var e in _pending) if (!order.Contains(e.Ref)) order.Add(e.Ref);
        return order.Select(k => new PendingEdit(k, RefPlacementEdit.BakedPose(RefOf(k).Ref), Current(k))).ToList();
    }

    /// <summary>Raised before any verb re-places a reference (stage, undo, revert, write), so the view can stop showing originals first.</summary>
    internal event Action? Replacing;

    /// <summary>Drop every staged edit of one reference and draw it where it was before them. Null when it has none.</summary>
    internal PendingEdit? Revert(FormKey key) => null;

    /// <summary>After a written edit is removed from its patch: forget it and draw the reference at its load-order place.</summary>
    internal void ForgetWritten(FormKey key) { }

    /// <summary>One edit in words, e.g. "Moved barrel 150 units east".</summary>
    internal static string Sentence(PendingEdit e, string? baseName) => Describe(e.After);

    internal static string Describe(RefPlacementEdit.Pose p) =>
        FormattableString.Invariant($"pos ({p.Position.X:0.###}, {p.Position.Y:0.###}, {p.Position.Z:0.###}) rot ({p.Rotation.X:0.#####}, {p.Rotation.Y:0.#####}, {p.Rotation.Z:0.#####})");
}
