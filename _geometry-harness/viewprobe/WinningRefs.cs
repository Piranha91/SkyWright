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
// Moved out of Program.cs's top-level statements unchanged, so that --winning-refs-selftest can drive the same code
// the render runs.
internal sealed class WinningRefWalk
{
    readonly Dictionary<FormKey, PlacedRef> _winRef;
    readonly Dictionary<FormKey, (FormKey Ws, PlacedRef Ref)> _persistentWin;
    readonly Dictionary<FormKey, FormKey> _refCellOf;

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

    /// <summary>One cell of one plugin, in load order.</summary>
    internal void Observe(CellPlacements cp, bool isTarget, bool inRing)
    {
        // Worldspace PERSISTENT refs (doors above all). Not detected by the holding cell —
        // the persistent cell masquerades as grid (0,0) — but by each ref's own Persistent
        // flag: the game loads these with the worldspace, so membership in a rendered cell
        // is a question of POSITION, resolved after the target is known.
        if (cp.Worldspace != FormKey.Null)
            foreach (var r in cp.Refs)
                if (r.Persistent) { _persistentWin[r.FormKey] = (cp.Worldspace, r); _refCellOf[r.FormKey] = cp.Cell; }   // later plugin wins
        if (!isTarget && !inRing) return;
        if (isTarget)
            foreach (var r in cp.Refs) { _winRef[r.FormKey] = r; _refCellOf[r.FormKey] = cp.Cell; }
        else
            foreach (var r in cp.Refs)
                if (!r.Persistent) { _winRef[r.FormKey] = r; _refCellOf[r.FormKey] = cp.Cell; }
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
