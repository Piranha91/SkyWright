using HousecarlCore;
using Mutagen.Bethesda.Plugins;

namespace HarnessShared;

// THE ENABLE CHAIN, PER ARM — the runtime enable state of a reference in the winning order, in a
// world state, or in the counterfactual order without one plugin's side.
//
// Lifted out of ContactIndex (Q27, 2026-09-06) unchanged in behaviour, because the contact pass was
// not the only detector that needs it: the overlap pass rebuilds "the arm without the later plugin"
// to decide whether that plugin INTRODUCED an intersection, and it was reading the previous snap's
// own initially-disabled flag while the winning arm went through the chain. That asymmetry is the
// exact shape the handoff calls a leak — a winning-state answer standing in for a historical one —
// and it decides Introduced vs Inherited, which is the class.
//
// Three arms, three memos, and they must not share:
//   WINNER      the winning flags and the winning XESP of every chain element.
//   STATE       the winner with some switches flipped (StateArm.Wrap). A flipped switch's baseline is
//               resolved from the RAW winner facts, memoised apart, so the flip is against the
//               load-time state and not against a half-applied one.
//   WITHOUT p   each chain element's flag at its authoring snap (the last one not on p's side) and
//               its XESP as last set outside p's side. Memoised per mover.
//
// The rule itself is HousecarlCore.EnableState and is not touched here; this file only decides WHICH
// facts each arm hands it.

/// <summary>Per-arm runtime enable state over an <see cref="OverlapIndex"/>'s history and XESP change log.</summary>
public sealed class EnableArms
{
    readonly OverlapIndex _index;
    readonly Dictionary<FormKey, EnableResolution> _winner = new();
    readonly Dictionary<FormKey, EnableResolution> _winnerBaseline = new();
    readonly Dictionary<int, Dictionary<FormKey, EnableResolution>> _arm = new();
    StateArm? _state;

    public EnableArms(OverlapIndex index) => _index = index;

    /// <summary>The world-state arm (roadmap 4.11). Set BEFORE any enable state is read — every memo is cleared when it
    /// changes, so a winning-state answer cannot survive into another arm. Null is the load-time state.</summary>
    public StateArm? State
    {
        get => _state;
        set { _state = value; Clear(); }
    }

    public void Clear() { _winner.Clear(); _winnerBaseline.Clear(); _arm.Clear(); }

    /// <summary>Winner-arm references whose chain could not be read to the end — a parent nothing places, a parent a
    /// plugin deleted, or a cycle. The answer for those is EnableState's stated fallback rather than a measurement, so
    /// a caller that emits findings carries this into its coverage block and no clean verdict rests on it (Q27).
    /// Derived from the memo, so counting costs nothing and cannot change an answer.</summary>
    public int WinnerUnresolved
    {
        get { int n = 0; foreach (var r in _winner.Values) if (!r.Resolved) n++; return n; }
    }
    /// <summary>Winner-arm references the chain resolved as absent when the cell loads.</summary>
    public int WinnerAbsent
    {
        get { int n = 0; foreach (var r in _winner.Values) if (!r.Enabled) n++; return n; }
    }
    /// <summary>How many references the winner arm was asked about — the denominator the two counts above belong to.
    /// It is the memo's size, so it counts the chain's own intermediate nodes as well as the caller's questions.</summary>
    public int WinnerQueried => _winner.Count;

    /// <summary>The XESP a reference carries at plugin <paramref name="plugin"/>'s load position: the last change at or
    /// before it (none when it never had one).</summary>
    public (FormKey? Parent, bool Opposite) EnableParentAt(FormKey key, int plugin)
    {
        if (_index.EnableChanges.TryGetValue(key, out var changes))
            for (int i = changes.Count - 1; i >= 0; i--)
                if (changes[i].Plugin <= plugin) return (changes[i].Parent, changes[i].Opposite);
        return (null, false);
    }

    /// <summary>The XESP in the arm WITHOUT <paramref name="mover"/>'s side: the last change by a plugin that is neither
    /// the mover nor downstream of it.</summary>
    public (FormKey? Parent, bool Opposite) ArmEnableParent(FormKey key, int mover, IReadOnlyList<HashSet<int>> closure)
    {
        if (_index.EnableChanges.TryGetValue(key, out var changes))
            for (int i = changes.Count - 1; i >= 0; i--)
            {
                int p = changes[i].Plugin;
                if (p == mover || closure[p].Contains(mover)) continue;
                return (changes[i].Parent, changes[i].Opposite);
            }
        return (null, false);
    }

    /// <summary>A reference's runtime enable state under the winner (with the state arm applied, when one is set): its
    /// winning flag and winning XESP, the chain followed through everything else's winning state. A reference the order
    /// does not place, or has deleted, is unknown to the chain and is reported unresolved.</summary>
    public EnableResolution Winner(FormKey key) => EnableState.Resolve(key, WinnerStateFacts, _winner);

    /// <summary>The winner's facts with no state arm applied — the load-time reading, and what a flipped switch's
    /// baseline is read from.</summary>
    public EnableFacts? WinnerRawFacts(FormKey k)
    {
        var s = _index.History.Winning(k);
        if (s is null || s.Value.Deleted || _index.IsDeletedNow(k)) return null;
        var (p, o) = EnableParentAt(k, int.MaxValue);
        return new EnableFacts(s.Value.Disabled, p, o);
    }

    /// <summary>The winner's facts under the state arm: a flipped switch reads as the opposite of its load-time state
    /// (resolved from the RAW facts, memoised apart), everything else as the records say.</summary>
    public EnableFacts? WinnerStateFacts(FormKey k)
        => _state is null ? WinnerRawFacts(k) : _state.Wrap(WinnerRawFacts, b => EnableState.Resolve(b, WinnerRawFacts, _winnerBaseline))(k);

    /// <summary>A reference's runtime enable state in the arm WITHOUT <paramref name="mover"/>'s side: each chain
    /// reference's flag at its authoring snap and its XESP as last set outside the mover's side. Memoised per mover, in
    /// its own dictionary — the winner's answers never leak into it.</summary>
    public EnableResolution Without(FormKey key, int mover, IReadOnlyList<HashSet<int>> closure)
    {
        if (!_arm.TryGetValue(mover, out var memo)) _arm[mover] = memo = new Dictionary<FormKey, EnableResolution>();
        EnableFacts? Raw(FormKey k)
        {
            var snaps = _index.History.Of(k);
            if (snaps is null) return null;
            int i = PlacementHistory.AuthoringSnapIndex(snaps, mover, closure);
            if (i < 0 || snaps[i].Deleted) return null;
            var (p, o) = ArmEnableParent(k, mover, closure);
            return new EnableFacts(snaps[i].Disabled, p, o);
        }
        // Under a state arm the flip applies in this arm too — a switch's baseline here is its state in the arm without
        // the mover, resolved from the raw facts.
        var facts = _state is null ? Raw : _state.Wrap(Raw, b => EnableState.Resolve(b, Raw, new Dictionary<FormKey, EnableResolution>()));
        return EnableState.Resolve(key, facts, memo);
    }
}
