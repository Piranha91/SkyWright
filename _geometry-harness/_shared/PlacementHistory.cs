using HousecarlCore;
using SkyWright.Engine;
using Mutagen.Bethesda.Plugins;

namespace HarnessShared;

// PLACEMENT HISTORY — who put a reference where, in load order, and what each plugin could have seen.
//
// Moved out of driftprobe 2026-09-01 so the placement-dependency pass (depprobe) reads positions the
// same way scenario consistency does, rather than re-deriving a second history with its own quirks.
// The two rules this carries are driftprobe's, unchanged:
//
//   - only a plugin that actually CHANGED a placement is a step in the story. An override that
//     forwards the position untouched is not recorded, or it would swamp the history;
//   - the AUTHORING VIEW: a plugin can only have seen the positions its own masters provide, so
//     "where was this when plugin P authored against it" is the last snap contributed by P or by a
//     plugin in P's transitive master closure. A winning position from anywhere else is one P never
//     saw. The closure is DetectorModel.MasterClosure, owned by proofprobe.
//
// Cell travels with the position: two interiors have independent origins, so a distance between
// them is meaningless — the first driftprobe run reported 125,000-unit "moves" that were a
// relocation into another cell.

/// <summary>One plugin's contribution to a reference's placement. <paramref name="Worldspace"/> is
/// FormKey.Null for an interior; for an exterior it is the containing worldspace, and <see cref="Space"/>
/// is the partition two references must share before a distance between them means anything (the cell
/// for an interior, the worldspace for an exterior — a persistent exterior ref lives in the worldspace's
/// persistent cell whatever its coordinates). Not part of <see cref="SameAs"/>: a ref's space follows its
/// cell, so the changed-only rule is unaffected. <paramref name="Rot"/> and <paramref name="Scale"/> ride along
/// for consumers that need the whole transform (the overlap pass rebuilds a world box at a previous snap); they
/// are NOT part of <see cref="SameAs"/>, so a rotation- or scale-only override is not a step in the story — the
/// same rule driftprobe has always run on, unchanged. The overlap pass counts those silent re-placements and
/// reports them as a limit.</summary>
public readonly record struct Snap(int Plugin, Vec3 Pos, FormKey Cell, bool Disabled, bool Deleted, FormKey Worldspace,
                                   Vec3 Rot = default, float Scale = 1f)
{
    public bool Interior => Worldspace.IsNull;
    public FormKey Space => Worldspace.IsNull ? Cell : Worldspace;
    public bool SameAs(Snap o) => Cell == o.Cell && Disabled == o.Disabled && Deleted == o.Deleted
                               && MathF.Abs(Pos.X - o.Pos.X) < 0.01f
                               && MathF.Abs(Pos.Y - o.Pos.Y) < 0.01f
                               && MathF.Abs(Pos.Z - o.Pos.Z) < 0.01f;
}

/// <summary>How far a reference travelled between two snaps, on driftprobe's generic-census rule.</summary>
public enum Movement { Minor, MovedFar, Relocated }

public sealed class PlacementHistory
{
    /// <summary>driftprobe's "moved" threshold: a within-cell displacement at or under this is jitter, not a move.</summary>
    public const float MoveThreshold = 16f;

    readonly Dictionary<FormKey, List<Snap>> _history = new();

    public int Count => _history.Count;

    /// <summary>Every reference with its snaps, in load order — for census passes.</summary>
    public IEnumerable<KeyValuePair<FormKey, List<Snap>>> Refs => _history;

    /// <summary>Append <paramref name="snap"/> unless it repeats the latest one. Call in load order.</summary>
    public void Record(FormKey key, Snap snap)
    {
        if (!_history.TryGetValue(key, out var h)) _history[key] = h = new List<Snap>(1);
        if (h.Count == 0 || !h[^1].SameAs(snap)) h.Add(snap);
    }

    public IReadOnlyList<Snap>? Of(FormKey key) => _history.TryGetValue(key, out var h) ? h : null;

    /// <summary>The winning placement — the last plugin to change it.</summary>
    public Snap? Winning(FormKey key) => _history.TryGetValue(key, out var h) && h.Count > 0 ? h[^1] : null;

    /// <summary>The authoring view: the last snap contributed by <paramref name="byPlugin"/> or a plugin in its
    /// transitive master closure. Null when neither contributed one — the plugin never saw this reference placed.</summary>
    public Snap? AsSeenBy(FormKey key, int byPlugin, IReadOnlyList<HashSet<int>> closure)
    {
        if (!_history.TryGetValue(key, out var h)) return null;
        for (int i = h.Count - 1; i >= 0; i--)
            if (h[i].Plugin == byPlugin || closure[byPlugin].Contains(h[i].Plugin)) return h[i];
        return null;
    }

    /// <summary>driftprobe's generic-census classification of a first-to-last displacement: a cell change is a
    /// relocation whatever the distance; a within-cell move over the threshold is far; anything else is minor.</summary>
    public static Movement Classify(Snap first, Snap last, float threshold = MoveThreshold)
    {
        if (first.Cell != last.Cell) return Movement.Relocated;
        return Dist(first.Pos, last.Pos) <= threshold ? Movement.Minor : Movement.MovedFar;
    }

    public static float Dist(Vec3 a, Vec3 b)
    {
        float dx = a.X - b.X, dy = a.Y - b.Y, dz = a.Z - b.Z;
        return MathF.Sqrt(dx * dx + dy * dy + dz * dz);
    }

    /// <summary>The index of a reference's snap in the order without <paramref name="mover"/>'s side: the last one whose
    /// plugin is neither the mover nor downstream of it. −1 when every snap is the mover's side (it introduced the ref).
    /// Moved here from ContactIndex in Q27 so the enable arms (_shared/EnableArms.cs) can ask it without dragging the
    /// whole contact pass in; <c>ContactIndex.AuthoringSnapIndex</c> forwards to it and is unchanged for its callers.</summary>
    public static int AuthoringSnapIndex(IReadOnlyList<Snap> snaps, int mover, IReadOnlyList<HashSet<int>> closure)
    {
        for (int i = snaps.Count - 1; i >= 0; i--)
        {
            int p = snaps[i].Plugin;
            if (p == mover || closure[p].Contains(mover)) continue;
            return i;
        }
        return -1;
    }
}
