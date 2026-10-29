using HarnessShared;
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
    internal const int EdgeRadius = RingReach.EdgeRadius;   // the shared ring rule (_shared/RingReach.cs)
    FormKey _edgeWs = FormKey.Null;
    (int X, int Y)? _edgeGrid;
    // Last sighting of each reference anchored 1 < d <= EdgeRadius from the target (temporary: its cell's grid;
    // persistent: its position), with the cell it is filed in.
    readonly Dictionary<FormKey, (PlacedRef Ref, FormKey Cell)> _edge = new();
    readonly List<FormKey> _edgeAdmitted = new();

    // LARGE REFERENCES (lane H1, 2026-10-11). The game 3D-loads the references a worldspace's large-reference lists (WRLD
    // RNAM) name out to (uLargeRefLODGridSize - 1) / 2 cells: 11 in the lab INI, so 5. MEASURED in game: Tamriel at one
    // station (lane G: listed references 3D-loaded at rings 3-5, none at 6-7, although the winning Tamriel WRLD lists
    // nothing) and both city worldspaces (lane L: the city's own Skyrim.esm-listed references loaded beyond ring 2, the
    // parent Tamriel list not in memory). A reference anchored 2 < d <= LargeRadius out joins the scene, by the ring
    // edge's own box test, when the SELECTED list names it:
    //   a root worldspace: the union of every plugin's list (RootUsesUnion; the winning WRLD override of a city or eFPS
    //     mod carries no list, and the game still loads the base list's references);
    //   a child worldspace (one with a Parent): its own lists' union, or none, by --large-refs-child (default union).
    // Every reference anchored 2 < d <= LargeRadius is tracked by the last-sighting rule (_far), because a LATER plugin's
    // list can name an earlier plugin's reference (Dawnguard's lists name Skyrim.esm references); the list filter runs
    // in MergeLarge, after the walk. Occlusion primitives on a list are admitted BY RECORD: their in-game activity at
    // rings 3-5 has not been read (LargePrimitivesByRecord). Content beyond the 3x3 is not modelled for any ring: a large
    // reference whose box misses the 3x3 is not drawn even when it would be in the view (a scene-scope limit, README).
    internal const int LargeRadius = RingReach.LargeRadius;
    internal const bool RootUsesUnion = RingReach.RootUsesUnion;
    internal const bool LargePrimitivesByRecord = true;
    internal const bool LargeModelsByRecord = true;
    readonly Dictionary<FormKey, (PlacedRef Ref, FormKey Cell)> _far = new();
    LargeReferenceLists _lists = new();
    readonly List<FormKey> _largeAdmitted = new();

    internal WinningRefWalk(Dictionary<FormKey, PlacedRef> winRef,
                            Dictionary<FormKey, (FormKey Ws, PlacedRef Ref)> persistentWin,
                            Dictionary<FormKey, FormKey> refCellOf)
    {
        _winRef = winRef; _persistentWin = persistentWin; _refCellOf = refCellOf;
    }

    /// <summary>Is this cell the target, or one of the target's ±1 ring neighbours whose refs load with it?</summary>
    /// <remarks><paramref name="visible"/> (--ring visible|N, VisibleCells.cs) replaces the ±1 square with the cells the
    /// camera's frame can see; null keeps the ring.</remarks>
    internal static (bool IsTarget, bool InRing) Classify(CellPlacements cp, FormKey? cellKey, bool interior, bool ringRefs,
                                                          FormKey targetWorldspace, (int X, int Y)? targetGrid,
                                                          IReadOnlySet<(int X, int Y)>? visible = null)
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
            && (visible is not null ? visible.Contains((cg.X, cg.Y))
                                    : Math.Abs(cg.X - tg.X) <= 1 && Math.Abs(cg.Y - tg.Y) <= 1);
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

            if (!admit && IsFar(cp, r)) _far[r.FormKey] = (r, cp.Cell);
            else _far.Remove(r.FormKey);
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
                _far.Remove(d.FormKey);
            }
    }

    /// <summary>Before the walk: the target the edge is measured from. Ring refs off, an interior or no grid: no edge.</summary>
    internal void TrackEdge(FormKey targetWorldspace, (int X, int Y) targetGrid, bool ringRefs)
    {
        _edge.Clear(); _edgeAdmitted.Clear();
        _far.Clear(); _largeAdmitted.Clear(); _lists = new();
        bool on = ringRefs && !targetWorldspace.IsNull;
        _edgeWs = on ? targetWorldspace : FormKey.Null;
        _edgeGrid = on ? targetGrid : null;
        _visible = null;
    }

    /// <summary>After TrackEdge, before the walk: --ring visible|N's loaded cells (null keeps the 3x3). A no-op when
    /// TrackEdge left the edge off (ring refs off, no worldspace). Separate from TrackEdge so that signature, which the
    /// selftest calls by reflection, stays as it was.</summary>
    internal void SetVisible(IReadOnlySet<(int X, int Y)>? visible) =>
        _visible = _edgeGrid is not null && visible is not null ? new HashSet<(int X, int Y)>(visible) : null;

    // --ring visible|N (VisibleCells.cs): the loaded area is this set instead of the 3x3 around the target. The ring edge
    // becomes the one-cell border around the set (EdgeRadius - 1 cells out, as the 3x3's edge is), the large references
    // stay those anchored within LargeRadius of the target and outside that border, and both admit by the placed box
    // overlapping any cell of the set. Null: the 3x3 rules above, unchanged.
    HashSet<(int X, int Y)>? _visible;

    bool NearVisible((int X, int Y) a, int reach)
    {
        for (int dx = -reach; dx <= reach; dx++)
            for (int dy = -reach; dy <= reach; dy++)
                if (_visible!.Contains((a.X + dx, a.Y + dy))) return true;
        return false;
    }

    bool IsEdge(CellPlacements cp, PlacedRef r)
    {
        if (_edgeGrid is not { } g || cp.Interior || cp.Worldspace != _edgeWs) return false;
        var a = RingReach.AnchorOf(cp, r);
        if (_visible is { } v) return !v.Contains(a) && NearVisible(a, EdgeRadius - 1);
        int d = RingReach.Ring(a, g);
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
        foreach (var (k, (r, cell)) in _edge.OrderBy(x => x.Key.ToString(), StringComparer.Ordinal))
        {
            if (_winRef.ContainsKey(k) || localBounds(r) is not { } b) continue;
            if (EntersSquare(r, b, g))
            {
                _winRef[k] = r; _refCellOf[k] = cell; _takenOut.Remove(k); _edgeAdmitted.Add(k);
            }
        }
        return _edgeAdmitted.Count;
    }

    internal IReadOnlyList<FormKey> EdgeAdmittedKeys => _edgeAdmitted;

    // Anchored beyond the ring edge and within the large-reference grid (the same anchor rule as IsEdge).
    bool IsFar(CellPlacements cp, PlacedRef r)
    {
        if (_edgeGrid is not { } g || cp.Interior || cp.Worldspace != _edgeWs) return false;
        var a = RingReach.AnchorOf(cp, r);
        int d = RingReach.Ring(a, g);
        if (_visible is { } v) return d <= LargeRadius && !v.Contains(a) && !NearVisible(a, EdgeRadius - 1);
        return d > EdgeRadius && d <= LargeRadius;
    }

    /// <summary>One plugin, in load order, before its cells: its record of the tracked worldspace, if it carries one, and
    /// that record's large-reference list.</summary>
    internal void ObserveLarge(Mutagen.Bethesda.Skyrim.ISkyrimModGetter mod)
    {
        if (_edgeWs.IsNull) return;
        foreach (var ws in mod.Worldspaces.Records)
        {
            if (ws.FormKey != _edgeWs) continue;
            _lists.Observe(mod.ModKey.FileName.String, ws);
        }
    }

    /// <summary>After MergeEdge: admit every tracked reference anchored 2 &lt; d &lt;= LargeRadius that the selected
    /// large-reference list names and whose placed box (localBounds, as MergeEdge) overlaps the 3x3 square in XY. Returns
    /// how many were admitted (LargeAdmittedKeys names them).</summary>
    internal int MergeLarge(Func<PlacedRef, (Vec3 Min, Vec3 Max)?> localBounds, bool childUnion)
    {
        _largeAdmitted.Clear();
        if (_edgeGrid is not { } g) return 0;
        var selected = _lists.Selected(childUnion, RootUsesUnion);
        foreach (var (k, (r, cell)) in _far.OrderBy(x => x.Key.ToString(), StringComparer.Ordinal))
        {
            if (_winRef.ContainsKey(k)) continue;
            bool listed = selected.Contains(k);
            if (!listed) continue;
            bool primitive = r.Primitive is not null;
            if (primitive ? !LargePrimitivesByRecord : !LargeModelsByRecord) continue;
            if (localBounds(r) is not { } b || !EntersSquare(r, b, g)) continue;
            _winRef[k] = r; _refCellOf[k] = cell; _takenOut.Remove(k); _largeAdmitted.Add(k);
        }
        return _largeAdmitted.Count;
    }

    // The placed box overlaps the 3x3 square around g in XY: the ONE box test of both merges and of FloorFeed's reach
    // (_shared/RingReach.cs), so a convention fixed there holds for all of them (lane V's yaw-only arm edits it).
    bool EntersSquare(PlacedRef r, (Vec3 Min, Vec3 Max) b, (int X, int Y) g) =>
        _visible is { } v ? RingReach.EntersCells(r, b, v) : RingReach.EntersSquare(r, b, g);

    internal IReadOnlyList<FormKey> LargeAdmittedKeys => _largeAdmitted;
    /// <summary>The tracked worldspace is a child (its winning record has a Parent).</summary>
    internal bool LargeIsChild => _lists.IsChild;
    /// <summary>How many references the selected list names.</summary>
    internal int LargeListed(bool childUnion) => _lists.Selected(childUnion, RootUsesUnion).Count;

    /// <summary>The far set and the lists, for the retained cell walk (CellWalkFacts) to carry to the next shot.</summary>
    internal object LargeSnapshot() => new LargeState(
        _far.ToDictionary(x => x.Key, x => (CellWalkFacts.Copy(x.Value.Ref), x.Value.Cell)), _lists.Clone());

    /// <summary>A retained walk: put back what LargeSnapshot carried (after TrackEdge has set the target).</summary>
    internal void RestoreLarge(object snapshot)
    {
        var s = (LargeState)snapshot;
        _far.Clear();
        foreach (var (k, v) in s.Far) _far[k] = (CellWalkFacts.Copy(v.Ref), v.Cell);
        _lists = s.Lists.Clone();
    }

    sealed record LargeState(Dictionary<FormKey, (PlacedRef Ref, FormKey Cell)> Far, LargeReferenceLists Lists);

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
        _edge.Clear(); _edgeAdmitted.Clear(); _edgeWs = FormKey.Null; _edgeGrid = null; _visible = null;
        _far.Clear(); _largeAdmitted.Clear(); _lists = new();
        if (retained is not null) _takenOut.UnionWith(retained);
    }

    /// <summary>Persistent refs merge by position over the whole REF-LOADED area: the 3x3 ring when
    /// ring refs are on, the target cell alone otherwise. A narrower test would silently
    /// drop exterior doors standing in a neighbour cell that visibly renders. Returns how many were added.</summary>
    internal int MergePersistent(FormKey targetWorldspace, (int X, int Y) tgp, bool ringRefs)
    {
        if (_visible is { } vis)
        {
            int addedVis = 0;
            foreach (var (pws, pr) in _persistentWin.Values)
                if (pws == targetWorldspace
                    && vis.Contains(((int)MathF.Floor(pr.Position.X / 4096f), (int)MathF.Floor(pr.Position.Y / 4096f)))
                    && !_winRef.ContainsKey(pr.FormKey))
                { _winRef[pr.FormKey] = pr; addedVis++; }
            return addedVis;
        }
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
