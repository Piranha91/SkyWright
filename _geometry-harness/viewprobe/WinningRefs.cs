using HousecarlCore;
using Mutagen.Bethesda.Plugins;

namespace ViewProbe;

// THE WINNING-REFERENCE WALK: which placed references the render's winning state admits, and where.
//
// The load-order walk in Program.cs hands every cell of every plugin (a CellPlacements, in load order) to Observe, and
// the persistent worldspace references are merged by position once the target is known (MergePersistent). The three
// dictionaries are Program.cs's own; this class only decides what goes into them, so the retained cell walk
// (CellWalkFacts) and everything downstream read them exactly as before.
//
// THE INVARIANT (every accumulator here obeys it, through the one loop in Observe):
//   After the walk, a reference is in the winning state if and only if its LAST sighting in load order is admitted --
//   in the target cell, or temporary in a ring cell -- and it is at that sighting's position, in that sighting's cell.
//   It is a persistent-merge candidate if and only if its last sighting is a persistent reference of a worldspace; the
//   by-position merge then adds the candidates inside the loaded area that the winning state does not already hold.
// A later plugin that files a reference anywhere else (another cell, another worldspace, the persistent cell) therefore
// takes the earlier plugin's version OUT. Before this rule, admission was decided per sighting and nothing removed an
// earlier admitted version, so a reference a later plugin moved out of the loaded cells was drawn at the earlier
// plugin's position, still labelled as changed by the later one (found 2026-09-26).
//
// A DELETION is a sighting too: an override that deletes a reference and carries no placement data arrives as a
// CellPlacements.DeletedRefs entry (the walk asks FromMod for such cells), and it takes the earlier version out --
// not admitted, not a merge candidate, and not counted as "moved elsewhere" (--winning-refs-selftest K6).
internal sealed class WinningRefWalk
{
    readonly Dictionary<FormKey, PlacedRef> _winRef;
    readonly Dictionary<FormKey, (FormKey Ws, PlacedRef Ref)> _persistentWin;
    readonly Dictionary<FormKey, FormKey> _refCellOf;
    // References a later sighting took out of the winning state; re-admission removes them again.
    readonly HashSet<FormKey> _takenOut = new();

    internal WinningRefWalk(Dictionary<FormKey, PlacedRef> winRef,
                            Dictionary<FormKey, (FormKey Ws, PlacedRef Ref)> persistentWin,
                            Dictionary<FormKey, FormKey> refCellOf)
    {
        _winRef = winRef; _persistentWin = persistentWin; _refCellOf = refCellOf;
    }

    /// <summary>Is this cell the target, or one of the target's ±1 ring neighbours whose refs load with it?</summary>
    internal static (bool IsTarget, bool InRing) Classify(CellPlacements cp, FormKey? cellKey, bool interior, bool ringRefs,
                                                          FormKey targetWorldspace, (int X, int Y)? targetGrid)
    {
        bool isTarget = cp.Cell == cellKey;
        // ±1-ring neighbours (exteriors): their refs are in frame from any eye-level
        // camera, so they load like the terrain always has. The worldspace persistent
        // cell masquerades as grid (0,0) and must not be admitted as a neighbour —
        // its refs are all Persistent, and only non-persistent refs are taken from
        // ring cells (persistent refs merge by position, below, ring-wide).
        bool inRing = !isTarget && ringRefs && !interior && !cp.Interior
            && cp.Worldspace == targetWorldspace && cp.Worldspace != FormKey.Null
            && targetGrid is { } tg && cp.Grid is { } cg
            && Math.Abs(cg.X - tg.X) <= 1 && Math.Abs(cg.Y - tg.Y) <= 1;
        return (isTarget, inRing);
    }

    /// <summary>One cell of one plugin, in load order. Every sighting of every reference decides that reference's state
    /// afresh (the invariant above): this sighting is the last one so far.</summary>
    internal void Observe(CellPlacements cp, bool isTarget, bool inRing)
    {
        foreach (var r in cp.Refs)
        {
            // Admitted: every ref of the target cell; a ring cell's non-persistent refs (persistent ones merge by
            // position, ring-wide, in MergePersistent).
            bool admit = isTarget || (inRing && !r.Persistent);
            // Worldspace PERSISTENT refs (doors above all). Not detected by the holding cell —
            // the persistent cell masquerades as grid (0,0) — but by each ref's own Persistent
            // flag: the game loads these with the worldspace, so membership in a rendered cell
            // is a question of POSITION, resolved after the target is known.
            bool candidate = cp.Worldspace != FormKey.Null && r.Persistent;

            if (admit) { _winRef[r.FormKey] = r; _takenOut.Remove(r.FormKey); }
            else if (_winRef.Remove(r.FormKey)) _takenOut.Add(r.FormKey);

            if (candidate) _persistentWin[r.FormKey] = (cp.Worldspace, r);
            else _persistentWin.Remove(r.FormKey);

            if (admit || candidate) _refCellOf[r.FormKey] = cp.Cell;
            else _refCellOf.Remove(r.FormKey);
        }
        // A deleted sighting with no placement data: the game does not load the reference, so no earlier version stays.
        // It is a deletion, not a move, so it leaves the taken-out set as well.
        if (cp.DeletedRefs is { Count: > 0 } deleted)
            foreach (var d in deleted)
            {
                _winRef.Remove(d.FormKey);
                _takenOut.Remove(d.FormKey);
                _persistentWin.Remove(d.FormKey);
                _refCellOf.Remove(d.FormKey);
            }
    }

    /// <summary>After the walk and the merge: the references an earlier plugin placed in the loaded cells (the target, or
    /// temporary in a ring cell) that a later plugin filed somewhere the render does not load. They are not drawn. A
    /// reference that only ever reached the render through the persistent merge is not counted here.</summary>
    internal HashSet<FormKey> TakenOutKeys() => _takenOut.Where(k => !_winRef.ContainsKey(k)).ToHashSet();

    internal int TakenOutCount => TakenOutKeys().Count;

    /// <summary>The taken-out set, for the retained cell walk (CellWalkFacts) to carry to the next shot.</summary>
    internal HashSet<FormKey> TakenOutSnapshot() => _takenOut.ToHashSet();

    /// <summary>A new shot: forget the previous walk's taken-out set (the dictionaries are cleared by their owner), and,
    /// for a retained walk, take the set it carried.</summary>
    internal void Reset(IEnumerable<FormKey>? retained = null)
    {
        _takenOut.Clear();
        if (retained is not null) _takenOut.UnionWith(retained);
    }

    /// <summary>Persistent refs merge by position over the whole REF-LOADED area: the 3x3 ring when
    /// ring refs are on, the target cell alone otherwise. A narrower test would silently
    /// drop exterior doors standing in a neighbour cell that visibly renders. Returns how many were added.</summary>
    internal int MergePersistent(FormKey targetWorldspace, (int X, int Y) tgp, bool ringRefs)
    {
        int ringR = ringRefs ? 1 : 0;
        float px0 = (tgp.X - ringR) * 4096f, py0 = (tgp.Y - ringR) * 4096f;
        float pspan = 4096f * (1 + 2 * ringR);
        int addedPersist = 0;
        foreach (var (pws, pr) in _persistentWin.Values)
            if (pws == targetWorldspace
                && pr.Position.X >= px0 && pr.Position.X < px0 + pspan
                && pr.Position.Y >= py0 && pr.Position.Y < py0 + pspan
                && !_winRef.ContainsKey(pr.FormKey))
            { _winRef[pr.FormKey] = pr; addedPersist++; }
        return addedPersist;
    }
}
