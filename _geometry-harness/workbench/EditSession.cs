using System.Globalization;
using System.Numerics;
using HousecarlCore;
using SkyWright.Engine;
using Mutagen.Bethesda.Plugins;
using ViewProbe;

namespace Workbench;

/// <summary>One staged edit: the reference and its pose before and after, or (<paramref name="Disable"/>) the reference to be
/// written "initially disabled" at the pose it has. Nothing is on disk until a commit.</summary>
internal sealed record PendingEdit(FormKey Ref, RefPlacementEdit.Pose Before, RefPlacementEdit.Pose After, bool Disable = false);

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

    /// <summary>Raised before any verb re-places a reference (stage, undo, revert, write, forget), so the view can stop
    /// showing originals first and the screen and the list stay in agreement.</summary>
    internal event Action? Replacing;

    /// <summary>A cell added to the live scene: its drawn references become selectable (an existing one stays).</summary>
    internal void Add(IEnumerable<RefOcclusion> refs)
    {
        foreach (var ro in refs)
            if (!ro.Ref.RefKey.IsNull && ro.Meshes.Count > 0) _refs.TryAdd(ro.Ref.RefKey, ro);
    }

    /// <summary>A cell dropped from the live scene: its references are no longer drawn. The caller keeps a cell that holds
    /// the selection or an edit (<see cref="Holds"/>), so neither is lost.</summary>
    internal void Remove(IEnumerable<RefOcclusion> refs)
    {
        foreach (var ro in refs)
            if (_refs.TryGetValue(ro.Ref.RefKey, out var held) && ReferenceEquals(held, ro)) _refs.Remove(ro.Ref.RefKey);
    }

    /// <summary>The reference is selected, or has a staged or written edit.</summary>
    internal bool Holds(FormKey key) => Selected == key || _pending.Any(e => e.Ref == key) || _written.ContainsKey(key);

    internal bool IsDrawn(FormKey key) => _refs.ContainsKey(key);
    internal RefOcclusion RefOf(FormKey key) => _refs.TryGetValue(key, out var ro) ? ro
        : throw new InvalidOperationException($"{key} is not a drawn reference in this scene; select one the window shows.");

    /// <summary>Every reference with a staged or written edit, staged ones first in first-edited order.</summary>
    internal IReadOnlyList<FormKey> Edited()
    {
        var order = new List<FormKey>();
        foreach (var e in _pending) if (!order.Contains(e.Ref)) order.Add(e.Ref);
        foreach (var k in _written.Keys) if (!order.Contains(k)) order.Add(k);
        return order;
    }

    /// <summary>Edits written to a patch that is not enabled: what the view shows for them and which patch holds them.</summary>
    internal IReadOnlyDictionary<FormKey, (string Patch, RefPlacementEdit.Pose Pose)> Written => _written;
    readonly Dictionary<FormKey, (string Patch, RefPlacementEdit.Pose Pose)> _written = new();

    /// <summary>After a write: the staged edits become written ones (stage-4 review M-1).</summary>
    internal void MarkWritten(string patch)
    {
        Replacing?.Invoke();
        var net = Net();
        foreach (var e in net) _written[e.Ref] = (patch, e.After);
        _pending.Clear();
        Changed?.Invoke($"{net.Count} edit(s) written to {patch}; the view keeps them where the patch puts them (the patch is not enabled)");
    }

    /// <summary>Draw a reference at a placement a patch holds (read back from it), labelled as written.</summary>
    internal void ShowWritten(FormKey key, RefPlacementEdit.Pose pose, string patch)
    {
        Replacing?.Invoke();
        RefPlacementEdit.Apply(RefOf(key), pose);
        _written[key] = (patch, pose);
        Changed?.Invoke($"{key} drawn as {patch} places it ({Describe(pose)})");
    }

    /// <summary>After a written edit's override is removed from its patch: forget it and draw the reference where its
    /// staged edits put it, else where the load order puts it.</summary>
    internal void ForgetWritten(FormKey key)
    {
        if (!_written.ContainsKey(key)) return;
        Replacing?.Invoke();
        _written.Remove(key);
        RefPlacementEdit.Apply(RefOf(key), Current(key));
        Changed?.Invoke($"{key}'s written edit is gone; the view shows it where the load order places it");
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
        RefOf(key);
        Replacing?.Invoke();
        var before = Current(key);
        var applied = RefPlacementEdit.Apply(RefOf(key), after);
        var e = new PendingEdit(key, before, after);
        _pending.Add(e);
        Changed?.Invoke($"staged {key}: {Describe(before)} -> {Describe(after)}"
                        + (applied.Stale.Count > 0 ? "; " + string.Join("; ", applied.Stale) : ""));
        return e;
    }

    /// <summary>Stage "disable this object" (Gate B; not built yet).</summary>
    internal PendingEdit? StageDisable(FormKey key) => null;

    /// <summary>The reference has a staged or written disable (Gate B; not built yet).</summary>
    internal bool IsDisabled(FormKey key) => false;

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
        if (_pending.Count == 0) { Changed?.Invoke(_written.Count > 0 ? "nothing staged to undo; a written edit is undone by removing it from its patch" : "nothing to undo"); return null; }
        Replacing?.Invoke();
        var last = _pending[^1];
        _pending.RemoveAt(_pending.Count - 1);
        RefPlacementEdit.Apply(RefOf(last.Ref), last.Before);
        Changed?.Invoke($"undid {last.Ref}: back at {Describe(last.Before)}");
        return last;
    }

    /// <summary>Drop every staged edit of one reference and draw it where it was before them. Returns the dropped edits
    /// as one (first before, last after), or null when it has none.</summary>
    internal PendingEdit? Revert(FormKey key)
    {
        var mine = _pending.Where(e => e.Ref == key).ToList();
        if (mine.Count == 0) return null;
        Replacing?.Invoke();
        _pending.RemoveAll(e => e.Ref == key);
        RefPlacementEdit.Apply(RefOf(key), mine[0].Before);
        Changed?.Invoke($"reverted {key}: back at {Describe(mine[0].Before)}");
        return new PendingEdit(key, mine[0].Before, mine[^1].After);
    }

    /// <summary>The edits a commit writes: one per reference, its latest pose, in first-edited order.</summary>
    internal IReadOnlyList<PendingEdit> Net()
    {
        var order = new List<FormKey>();
        foreach (var e in _pending) if (!order.Contains(e.Ref)) order.Add(e.Ref);
        return order.Select(k => new PendingEdit(k, RefPlacementEdit.BakedPose(RefOf(k).Ref), Current(k))).ToList();
    }

    static string N(float v) => v.ToString("0.##", CultureInfo.InvariantCulture);

    /// <summary>One edit in words, in world directions (+X east, +Y north, +Z up): "Moved barrel 150 units east",
    /// "Turned shrub 15° left" (left is counter-clockwise seen from above), "Tilted shrub 10° about the east-west axis",
    /// "Rotated shrub 20° about a slanted axis", or a move and a turn joined: "Moved shrub 150 units east; turned it 15° left".</summary>
    internal static string Sentence(PendingEdit e, string? baseName)
    {
        string name = baseName ?? e.Ref.ToString();
        var d = new Vector3(e.After.Position.X - e.Before.Position.X, e.After.Position.Y - e.Before.Position.Y, e.After.Position.Z - e.Before.Position.Z);
        var parts = new List<string>();
        void Part(float v, string pos, string neg) { if (MathF.Abs(v) >= 0.005f) parts.Add($"{N(MathF.Abs(v))} units {(v > 0 ? pos : neg)}"); }
        Part(d.X, "east", "west"); Part(d.Y, "north", "south"); Part(d.Z, "up", "down");
        string? move = parts.Count == 0 ? null
            : parts.Count == 1 ? parts[0] : string.Join(", ", parts.Take(parts.Count - 1)) + " and " + parts[^1];
        string? turn = TurnWords(e.Before.Rotation, e.After.Rotation, out var verb);
        if (move is null && turn is null) return $"Left {name} where it was";
        if (turn is null) return $"Moved {name} {move}";
        if (move is null) return $"{char.ToUpperInvariant(verb[0])}{verb[1..]} {name} {turn}";
        return $"Moved {name} {move}; {verb} it {turn}";
    }

    /// <summary>The turn from one rotation to another as words, or null when there is none worth saying.</summary>
    static string? TurnWords(Vec3 before, Vec3 after, out string verb)
    {
        verb = "turned";
        if (!Matrix4x4.Invert(SpatialOverlap.RefrRotation(before), out var inv)) return null;
        var delta = inv * SpatialOverlap.RefrRotation(after);          // after = before · delta, a turn about a world axis
        var q = Quaternion.Normalize(Quaternion.CreateFromRotationMatrix(delta));
        if (q.W < 0) q = new Quaternion(-q.X, -q.Y, -q.Z, -q.W);
        float angle = 2f * MathF.Acos(Math.Clamp(q.W, -1f, 1f));
        float deg = float.RadiansToDegrees(angle);
        if (deg < 0.05f) return null;
        float s = MathF.Sqrt(MathF.Max(1f - q.W * q.W, 1e-12f));
        var axis = new Vector3(q.X / s, q.Y / s, q.Z / s);
        float cos1 = MathF.Cos(float.DegreesToRadians(1f));
        string a = deg.ToString("0.#", CultureInfo.InvariantCulture);
        if (MathF.Abs(axis.Z) >= cos1) return $"{a}° {(axis.Z > 0 ? "left" : "right")}";
        if (MathF.Abs(axis.X) >= cos1) { verb = "tilted"; return $"{a}° about the east-west axis"; }
        if (MathF.Abs(axis.Y) >= cos1) { verb = "tilted"; return $"{a}° about the north-south axis"; }
        verb = "rotated";
        return $"{a}° about a slanted axis";
    }

    internal static string Describe(RefPlacementEdit.Pose p) =>
        FormattableString.Invariant($"pos ({p.Position.X:0.###}, {p.Position.Y:0.###}, {p.Position.Z:0.###}) rot ({p.Rotation.X:0.#####}, {p.Rotation.Y:0.#####}, {p.Rotation.Z:0.#####})");
}
