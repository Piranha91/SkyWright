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
    // References some sighting admitted during this walk: a later non-admitted sighting of one is a move out of the loaded
    // cells, even when a deletion came in between (the deletion alone is not a move, and is not counted).
    readonly HashSet<FormKey> _everAdmitted = new();

    // THE RING EDGE. The game loads more than the 3x3 this render draws: uGridsToLoad, unset in every lab INI, defaults
    // to 5 (the player's cell +-2). That value is INFERRED, not read: the live GetINIInt returned 0 (2026-10-10). A
    // reference anchored up to EdgeRadius cells out is loaded by the game, and when its placed bounds reach into the 3x3
    // it can hide content there (the Windhelm bridge, two cells west of the docks, hides the market). Such references are
    // tracked here by the same last-sighting rule and admitted by MergeEdge after the walk, once every base is final.
    // Why bounds-in-the-3x3 is enough: every eye and every scored content shape lie inside the 3x3, which is a square and
    // therefore convex, so the segment between them lies inside it too; anything that can come between them has geometry
    // inside the 3x3. References beyond EdgeRadius are not admitted, the large-reference grid (uLargeRefLODGridSize)
    // included: the winning WRLD records of the lab orders carry no large-reference list and its effect is unmeasured.
    internal const int EdgeRadius = 2;
    FormKey _edgeWs = FormKey.Null;
    (int X, int Y)? _edgeGrid;
    // Last sighting of each reference anchored 1 < d <= EdgeRadius from the target (temporary: its cell's grid;
    // persistent: its position), with the cell it is filed in.
    readonly Dictionary<FormKey, (PlacedRef Ref, FormKey Cell)> _edge = new();
    readonly List<FormKey> _edgeAdmitted = new();

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

            if (admit) { _winRef[r.FormKey] = r; _takenOut.Remove(r.FormKey); _everAdmitted.Add(r.FormKey); }
            else { _winRef.Remove(r.FormKey); if (_everAdmitted.Contains(r.FormKey)) _takenOut.Add(r.FormKey); }

            if (candidate) _persistentWin[r.FormKey] = (cp.Worldspace, r);
            else _persistentWin.Remove(r.FormKey);

            if (admit || candidate) _refCellOf[r.FormKey] = cp.Cell;
            else _refCellOf.Remove(r.FormKey);

            if (!admit && IsEdge(cp, r)) _edge[r.FormKey] = (r, cp.Cell);
            else _edge.Remove(r.FormKey);
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
                _edge.Remove(d.FormKey);
            }
    }

    /// <summary>Before the walk: the target the edge is measured from. Ring refs off, an interior or no grid: no edge.</summary>
    internal void TrackEdge(FormKey targetWorldspace, (int X, int Y) targetGrid, bool ringRefs)
    {
        _edge.Clear(); _edgeAdmitted.Clear();
        bool on = ringRefs && !targetWorldspace.IsNull;
        _edgeWs = on ? targetWorldspace : FormKey.Null;
        _edgeGrid = on ? targetGrid : null;
    }

    bool IsEdge(CellPlacements cp, PlacedRef r)
    {
        if (_edgeGrid is not { } g || cp.Interior || cp.Worldspace != _edgeWs) return false;
        (int X, int Y) a = r.Persistent || cp.Grid is not { } cg
            ? ((int)MathF.Floor(r.Position.X / 4096f), (int)MathF.Floor(r.Position.Y / 4096f))
            : (cg.X, cg.Y);
        int d = Math.Max(Math.Abs(a.X - g.X), Math.Abs(a.Y - g.Y));
        return d > 1 && d <= EdgeRadius;
    }

    /// <summary>After the walk and MergePersistent: admit every tracked edge reference whose box, given in its own frame by
    /// <paramref name="localBounds"/> (null: not drawable, never admitted) and placed by scale, the engine's rotation and
    /// position, overlaps the 3x3 square in XY. A reference already in the winning state is left as it is. Returns how
    /// many were admitted (EdgeAdmittedKeys names them).</summary>
    internal int MergeEdge(Func<PlacedRef, (Vec3 Min, Vec3 Max)?> localBounds)
    {
        _edgeAdmitted.Clear();
        if (_edgeGrid is not { } g) return 0;
        float x0 = (g.X - 1) * 4096f, y0 = (g.Y - 1) * 4096f, x1 = (g.X + 2) * 4096f, y1 = (g.Y + 2) * 4096f;
        foreach (var (k, (r, cell)) in _edge.OrderBy(x => x.Key.ToString(), StringComparer.Ordinal))
        {
            if (_winRef.ContainsKey(k) || localBounds(r) is not { } b) continue;
            var rot = SpatialOverlap.RefrRotation(r.Rotation);
            var pos = new System.Numerics.Vector3(r.Position.X, r.Position.Y, r.Position.Z);
            float mx0 = float.MaxValue, my0 = float.MaxValue, mx1 = float.MinValue, my1 = float.MinValue;
            for (int i = 0; i < 8; i++)
            {
                var c = new System.Numerics.Vector3((i & 1) == 0 ? b.Min.X : b.Max.X, (i & 2) == 0 ? b.Min.Y : b.Max.Y,
                                                    (i & 4) == 0 ? b.Min.Z : b.Max.Z);
                var w = System.Numerics.Vector3.Transform(c * r.Scale, rot) + pos;
                mx0 = MathF.Min(mx0, w.X); my0 = MathF.Min(my0, w.Y); mx1 = MathF.Max(mx1, w.X); my1 = MathF.Max(my1, w.Y);
            }
            if (mx0 < x1 && mx1 > x0 && my0 < y1 && my1 > y0)
            {
                _winRef[k] = r; _refCellOf[k] = cell; _takenOut.Remove(k); _edgeAdmitted.Add(k);
            }
        }
        return _edgeAdmitted.Count;
    }

    internal IReadOnlyList<FormKey> EdgeAdmittedKeys => _edgeAdmitted;

    /// <summary>The tracked edge set, for the retained cell walk (CellWalkFacts) to carry to the next shot.</summary>
    internal Dictionary<FormKey, (PlacedRef Ref, FormKey Cell)> EdgeSnapshot() => new(_edge);

    /// <summary>A retained walk: put back the edge set it carried (after TrackEdge has set the target).</summary>
    internal void RestoreEdge(IReadOnlyDictionary<FormKey, (PlacedRef Ref, FormKey Cell)> edge)
    {
        _edge.Clear();
        foreach (var (k, v) in edge) _edge[k] = v;
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
        _everAdmitted.Clear();
        _edge.Clear(); _edgeAdmitted.Clear(); _edgeWs = FormKey.Null; _edgeGrid = null;
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
