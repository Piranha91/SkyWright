using System.Numerics;
using HousecarlCore;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Skyrim;

namespace HarnessShared;

// THE OVERLAP INDEX — the I/O half of the cross-plugin overlap pass (milestone 5, roadmap 4.1).
//
// overlapprobe's first cut (2026-08-27) carried its own "last plugin to SET a placement" bookkeeping.
// This moves that onto PlacementHistory — the same changed-only history and authoring view that
// driftprobe's scenario check and depprobe's dependency pass run on — so "responsible" means one
// thing across the detectors, and so the pass can ask the question the first cut could not: what
// did this reference look like BEFORE the later plugin touched it? That is the differential arm.
//
// Everything here is bookkeeping and geometry plumbing: placements into the history (with the
// rotation and scale the world box needs), base-record types, mesh paths and OBND, the candidate
// build, the spatial hash, and the facts for one hit. The DECISION is DetectorModel.OverlapProvenance,
// owned by proofprobe; the broad-phase test is DetectorModel.BoxOverlap, likewise. Shared between
// overlapprobe (the real order, meshes through the VFS) and proofprobe (a synthetic order written with
// Mutagen, OBND boxes only), so the proof exercises the same extraction and the same arm construction
// the live run uses.
//
// Standing rules applied: measured geometry first, OBND as a LABELLED fallback, no geometry = not
// judged, counted (rule 6); the same-author floor measured before any cross count is read; a broad
// phase is a candidate, never a verdict; nothing verified in game.

/// <summary>A winning, enabled, obstructing-typed reference with a world box. <paramref name="Win"/> is the winning
/// snap — its Plugin is the RESPONSIBLE plugin, the last one to change the placement.</summary>
public readonly record struct OverlapCandidate(FormKey Key, Snap Win, FormKey Base, Aabb World, char Src);

/// <summary>Two candidates whose world boxes overlap at the run's threshold. <paramref name="Declared"/> is the
/// closure relation between the two responsible plugins in either direction — the first cut's exclusion, kept
/// as a tally so the run can check that Seen reproduces it.</summary>
public readonly record struct OverlapHit(OverlapCandidate A, OverlapCandidate B, float Depth, float Frac, bool Declared);

/// <summary>One cross-responsible hit judged: which side is the later plugin, the arm without it, and the verdict.
/// <paramref name="LaterPrevious"/> is the later plugin's reference at its previous snap (null when it introduced
/// it); <paramref name="OtherAsSeenByLater"/> is the other reference as the later plugin's masters last placed it
/// (null when none of them ever did); <paramref name="WithoutDepth"/>/<paramref name="WithoutFrac"/> are the overlap
/// in the arm without the later plugin (NaN when no box could be rebuilt).</summary>
public sealed record OverlapRow(OverlapHit Hit, OverlapCandidate LaterRef, OverlapCandidate OtherRef,
                                DetectorModel.OverlapFacts Facts, OverlapOrigin Origin, OverlapSight Sight, string Why,
                                Snap? LaterPrevious, Snap? OtherAsSeenByLater, float WithoutDepth, float WithoutFrac)
{
    public int Later => LaterRef.Win.Plugin;
    public int Earlier => OtherRef.Win.Plugin;
    /// <summary>The ranker's score: how deep times how much of the smaller box. Reported, not thresholded.</summary>
    public float Rank => Hit.Depth * Hit.Frac;
    /// <summary>Largest dimension of the smaller box — the thing that is buried.</summary>
    public float SmallerMaxDim
    {
        get
        {
            static float Vol(Aabb b) => (b.Max.X - b.Min.X) * (b.Max.Y - b.Min.Y) * (b.Max.Z - b.Min.Z);
            static float Dim(Aabb b) => MathF.Max(b.Max.X - b.Min.X, MathF.Max(b.Max.Y - b.Min.Y, b.Max.Z - b.Min.Z));
            return Vol(Hit.A.World) <= Vol(Hit.B.World) ? Dim(Hit.A.World) : Dim(Hit.B.World);
        }
    }
    public OverlapScale Scale => DetectorModel.ClassifyOverlapScale(SmallerMaxDim);
}

/// <summary>What the candidate build set aside, counted per rule 6.</summary>
public sealed class CandidateSkips
{
    public long Deleted, Disabled, NonObstructingType, NoGeometry, ExcludedResponsible, Volume, MarkerMesh;
}

/// <summary>The pair sweep's tallies: the same-author floor by domain, and the two cross populations.</summary>
public sealed class PairSweep
{
    public long PairsChecked, SameChecked, SameDeep, CrossDeclaredChecked, CrossDeclaredDeep, CrossUndeclaredChecked;
    public readonly Dictionary<string, (long Deep, long Checked)> SameByDomain = new();
    /// <summary>Every cross-responsible deep hit, declared or not — the population the provenance rule runs over.</summary>
    public readonly List<OverlapHit> CrossDeep = new();
}

public sealed class OverlapIndex
{
    public PlacementHistory History { get; } = new();
    /// <summary>base record -> its Mutagen type name and editor id, winner wins. Modeled records only.</summary>
    public Dictionary<FormKey, (string Type, string? Edid)> BaseInfo { get; } = new();
    public Dictionary<FormKey, string> BaseMesh { get; } = new();
    /// <summary>base record -> OBND extents (|second − first| per axis).</summary>
    public Dictionary<FormKey, Vec3> BaseObnd { get; } = new();
    /// <summary>ref -> its winning base record.</summary>
    public Dictionary<FormKey, FormKey> RefBase { get; } = new();
    public Dictionary<FormKey, (string? Edid, bool Interior)> CellName { get; } = new();
    public long Placements { get; private set; }
    /// <summary>Overrides that changed ONLY rotation or scale — same position, cell and flags as the latest snap. The
    /// history does not record them (driftprobe's rule), so they are not a step and do not make the plugin responsible.
    /// Counted so the limit is a number in the output, not a sentence.</summary>
    public long SilentRePlacements { get; private set; }
    /// <summary>ref -> every plugin carrying a record of it, in load order, whether or not the placement changed. What
    /// "the mover's side overrode it without moving it" is read from (the contact pass; DependencyIndex keeps the same).</summary>
    public Dictionary<FormKey, List<int>> Touchers { get; } = new();
    /// <summary>ref -> the plugins that CHANGED its base record, with the base each set, in load order — the first entry
    /// is the defining plugin's. A previous snap's box is rebuilt with the base the reference had THEN, so an override
    /// that swapped the mesh as it moved is not rebuilt with the wrong mesh (4.1's stated limit, closed for the
    /// contact pass). Not part of the history's changed-only rule: a base swap without a move is still not a step.</summary>
    public Dictionary<FormKey, List<(int Plugin, FormKey Base)>> BaseChanges { get; } = new();
    /// <summary>Deletion, per view (Q34, _shared/DeletionHistory.cs). A deleted reference usually loses its placement
    /// subrecord and is invisible to PlacementExtract, so deletions are read off the major-record walk — else a deleted
    /// dependent would read as still where it was. The view selects the record and only then is its Deleted flag read:
    /// the last deletion and the last placement STEP are different plugins whenever a deletion stripped the placement or
    /// a later record restored the reference where it already stood.</summary>
    public DeletionHistory Deletions { get; }

    public OverlapIndex() => Deletions = new DeletionHistory(Touchers);
    /// <summary>Bases carrying the Is-Marker header flag (0x00800000) under the winner — the engine renders no mesh for
    /// them, whatever the record type or the mesh name says (JK's sit-marker chairs: Furniture, CommonChair01.nif, the
    /// flag set). Winner-takes-all like the mesh path; an override that clears the flag removes the entry. The marker
    /// rule for the contact pass since the 2026-09-03 fold (R2).</summary>
    public HashSet<FormKey> BaseIsMarker { get; } = new();
    /// <summary>ref -> the plugins that CHANGED its XESP enable parent (or the opposite-of-parent flag), with what each
    /// set, in load order; a ref born without a parent and never given one has no entry, and an override that drops
    /// the parent records a null. The runtime enable state of an arm is resolved from the last entry not on the
    /// mover's side (R1, 2026-09-03), the way <see cref="BaseChanges"/> gives a previous snap the base it had then.
    /// Not part of the history's changed-only rule: an XESP change without a move is not a placement step.</summary>
    public Dictionary<FormKey, List<(int Plugin, FormKey? Parent, bool Opposite)>> EnableChanges { get; } = new();
    /// <summary>ref -> the cell and worldspace of its winning placement (Null worldspace for an interior). What the
    /// Base Object Swapper reader's filters read (BosHook).</summary>
    public Dictionary<FormKey, (FormKey Cell, FormKey Worldspace)> RefCell { get; } = new();
    /// <summary>ref -> its placed arm ("PlacedObject", "PlacedNpc", …), winner wins. BOS swaps three arms and no actor.</summary>
    public Dictionary<FormKey, string> RefArm { get; } = new();
    /// <summary>ref -> its own XEZN under the winner; absent when none.</summary>
    public Dictionary<FormKey, FormKey> RefEncounterZone { get; } = new();
    /// <summary>The Base Object Swapper form index, filled during <see cref="Ingest"/> when a caller sets it before
    /// the walk (one pass over the order serves both). Null: no BOS.</summary>
    public BosFormIndex? Bos { get; set; }
    /// <summary>ref -> its RECORD base, kept when BosHook replaced <see cref="RefBase"/> with the effective one. Empty
    /// when BOS was not applied.</summary>
    public Dictionary<FormKey, FormKey> RecordBase { get; } = new();
    /// <summary>ref -> what BOS does to it, for every reference it swaps, moves or may swap (BosHook).</summary>
    public Dictionary<FormKey, BaseObjectSwaps.BosResolution> BosSwaps { get; } = new();
    /// <summary>The pseudo-plugin index BosHook recorded its steps under; -1 when BOS was not applied.</summary>
    public int BosPlugin { get; set; } = -1;
    /// <summary>Collision layers (COLL) by their INDEX — the number a placed reference's XTRI carries — under the winner:
    /// the record, its editor id, and whether it is flagged NavmeshObstacle. L_NAVCUT is index 49 in the base game
    /// (measured 2026-09-04). What the world-state census reads a navcut box by (WorldState.cs).</summary>
    public Dictionary<uint, (FormKey Key, string? Edid, bool NavmeshObstacle)> CollisionLayersByIndex { get; } = new();
    /// <summary>ref -> its XTRI collision-layer index under the winner; absent when none.</summary>
    public Dictionary<FormKey, uint> RefCollisionLayer { get; } = new();
    /// <summary>The worldspace persistent cells (the top cell of each worldspace, no grid) — a marker placed persistent
    /// sits here whatever its coordinates, so a per-cell census labels it rather than reading it as a crowded cell.</summary>
    public HashSet<FormKey> PersistentCells { get; } = new();
    /// <summary>ref -> its XPRM primitive under the winner; absent when none. A navcut's obstacle is this box.</summary>
    public Dictionary<FormKey, PlacedPrimitiveInfo> RefPrimitive { get; } = new();
    /// <summary>ref -> its editor id, the last non-empty one in load order (an override that omits it keeps the earlier
    /// name). What the world-state census names a switch by.</summary>
    public Dictionary<FormKey, string> RefEditorId { get; } = new();

    /// <summary>Is the WINNING record of the reference a deletion? The last record in the evaluated order decides —
    /// not the last plugin that ever deleted it, and not the last plugin to change its placement (Q34).</summary>
    public bool IsDeletedNow(FormKey key) => Deletions.AtWinner(key);

    /// <summary>…and the same question in the arm WITHOUT <paramref name="mover"/>'s side: the last record by a plugin
    /// that is neither the mover nor downstream of it. What the counterfactual arms ask, so an arm that excludes a
    /// RESTORING plugin sees the deletion that stands without it.</summary>
    public bool IsDeletedWithout(FormKey key, int mover, IReadOnlyList<HashSet<int>> closure)
        => Deletions.Without(key, mover, closure);

    /// <summary>Read one plugin, in load order: placements into the history, base-record names, meshes and OBND.</summary>
    public void Ingest(int plugin, ISkyrimModGetter mod)
    {
        foreach (var rec in mod.EnumerateMajorRecords())
        {
            Bos?.Note(rec);
            // Every placed record states whether it is deleted, whether or not it kept its placement — that is what
            // makes a later restoration expressible at all (Q34).
            if (rec is IPlacedGetter pg) { Deletions.Note(plugin, pg.FormKey, pg.IsDeleted, pg.Placement is not null); continue; }
            if (rec is ICollisionLayerGetter coll)
            {
                CollisionLayersByIndex[coll.Index] = (coll.FormKey, coll.EditorID, coll.Flags.HasFlag(CollisionLayer.Flag.NavmeshObstacle));
                continue;
            }
            if (rec is IModeledGetter mo && rec is ISkyrimMajorRecordGetter sm)
            {
                BaseInfo[sm.FormKey] = (rec.GetType().Name.Replace("BinaryOverlay", ""), sm.EditorID);
                if (mo.Model?.File is { } mf && !string.IsNullOrWhiteSpace(mf.GivenPath)) BaseMesh[sm.FormKey] = mf.GivenPath;
                if ((sm.MajorRecordFlagsRaw & EnableState.IsMarkerFlag) != 0) BaseIsMarker.Add(sm.FormKey); else BaseIsMarker.Remove(sm.FormKey);
                if (rec is IObjectBoundedGetter ob && ob.ObjectBounds is { } bnd)
                    BaseObnd[sm.FormKey] = new Vec3(MathF.Abs(bnd.Second.X - bnd.First.X),
                                                    MathF.Abs(bnd.Second.Y - bnd.First.Y),
                                                    MathF.Abs(bnd.Second.Z - bnd.First.Z));
            }
        }
        foreach (var cp in PlacementExtract.FromMod(mod))
        {
            CellName[cp.Cell] = (cp.CellEditorId, cp.Interior);
            if (!cp.Interior && cp.Grid is null && !cp.Worldspace.IsNull) PersistentCells.Add(cp.Cell);
            Bos?.NoteCell(cp.Cell, cp.Worldspace);
            foreach (var r in cp.Refs) Record(plugin, cp.Cell, cp.Worldspace, r);
        }
    }

    /// <summary>One placed record from one plugin. Exposed so a proof can feed extracted refs directly.</summary>
    public void Record(int plugin, FormKey cell, FormKey worldspace, PlacedRef r)
    {
        Placements++;
        RefCell[r.FormKey] = (cell, worldspace);
        RefArm[r.FormKey] = r.RecordType;
        Bos?.NotePlaced(r.FormKey, r.Persistent);
        if (r.EncounterZone is { } ez) RefEncounterZone[r.FormKey] = ez; else RefEncounterZone.Remove(r.FormKey);
        if (r.CollisionLayerIndex is { } cl) RefCollisionLayer[r.FormKey] = cl; else RefCollisionLayer.Remove(r.FormKey);
        if (!string.IsNullOrWhiteSpace(r.EditorId)) RefEditorId[r.FormKey] = r.EditorId!;
        if (r.Primitive is { } pr) RefPrimitive[r.FormKey] = pr; else RefPrimitive.Remove(r.FormKey);
        Deletions.Note(plugin, r.FormKey, r.Deleted, hasPlacement: true);
        var snap = new Snap(plugin, r.Position, cell, r.InitiallyDisabled, r.Deleted, worldspace, r.Rotation, r.Scale);
        if (History.Winning(r.FormKey) is { } prev && prev.SameAs(snap)
            && (MathF.Abs(prev.Rot.X - snap.Rot.X) > 1e-4f || MathF.Abs(prev.Rot.Y - snap.Rot.Y) > 1e-4f
             || MathF.Abs(prev.Rot.Z - snap.Rot.Z) > 1e-4f || MathF.Abs(prev.Scale - snap.Scale) > 1e-3f))
            SilentRePlacements++;
        History.Record(r.FormKey, snap);
        if (!Touchers.TryGetValue(r.FormKey, out var touchers)) Touchers[r.FormKey] = touchers = new List<int>(1);
        if (touchers.Count == 0 || touchers[^1] != plugin) touchers.Add(plugin);
        {
            // The XESP as this plugin states it; an entry only when it differs from the last one recorded (none = no parent).
            var had = EnableChanges.TryGetValue(r.FormKey, out var ec) && ec.Count > 0 ? ec[^1] : (Plugin: -1, Parent: (FormKey?)null, Opposite: false);
            bool same = had.Parent == r.EnableParent && had.Opposite == r.EnableParentOpposite;
            if (!same)
            {
                if (ec is null) EnableChanges[r.FormKey] = ec = new List<(int, FormKey?, bool)>(1);
                ec.Add((plugin, r.EnableParent, r.EnableParentOpposite));
            }
        }
        if (r.Base is { } b)
        {
            if (!RefBase.TryGetValue(r.FormKey, out var prevBase) || prevBase != b)
            {
                if (!BaseChanges.TryGetValue(r.FormKey, out var changes)) BaseChanges[r.FormKey] = changes = new List<(int, FormKey)>(1);
                changes.Add((plugin, b));
            }
            RefBase[r.FormKey] = b;
        }
    }

    /// <summary>The OBND fallback box for a base record: the record's extents centred on the origin as the model box.
    /// LABELLED ('o'), never blended with a measured mesh. Empty when the record carries no bounds.</summary>
    public Aabb ObndBox(FormKey baseKey)
    {
        if (!BaseObnd.TryGetValue(baseKey, out var ob) || (ob.X <= 0 && ob.Y <= 0 && ob.Z <= 0)) return Aabb.Empty;
        var half = new Vec3(ob.X / 2, ob.Y / 2, ob.Z / 2);
        return new Aabb(new Vec3(-half.X, -half.Y, -half.Z), half, false);
    }

    /// <summary>Build the candidates: every winning, enabled, undeleted reference whose base is an obstructing type
    /// and has geometry, whose responsible plugin <paramref name="excludeResponsible"/> does not set aside, whose base
    /// is not an editor marker (the engine renders no marker mesh, so a marker cannot obstruct anything), and whose
    /// world box is no larger than <paramref name="maxDim"/> on any axis (volumes — fog, LOD, skydomes — overlap
    /// everything by design).</summary>
    /// <param name="enabledOf">the run's population — <c>EnablePopulation.Predicate</c>: the load-time enable chain
    /// by default since Q27 (2026-09-06), a world-state arm's under <c>--state</c> (EnableState under StateArm.Wrap,
    /// roadmap 4.11). NULL reads the winning snap's own flag, the population every run before Q27 judged, byte for
    /// byte — what <c>--flags</c> asks for.</param>
    public List<OverlapCandidate> Candidates(Func<FormKey, (Aabb Box, char Src)> boxOf, ISet<string> obstructing,
                                             Func<int, bool> excludeResponsible, float maxDim, out CandidateSkips skips,
                                             Func<FormKey, bool>? enabledOf = null)
    {
        skips = new CandidateSkips();
        var list = new List<OverlapCandidate>();
        foreach (var (key, snaps) in History.Refs)
        {
            var s = snaps[^1];
            // The winning RECORD decides, not the last placement step's own flag: a deletion that stripped the
            // placement leaves no step at all, so a reference removed that way used to stay a candidate (Q34).
            if (s.Deleted || IsDeletedNow(key)) { skips.Deleted++; continue; }
            if (enabledOf is null ? s.Disabled : !enabledOf(key)) { skips.Disabled++; continue; }
            if (excludeResponsible(s.Plugin)) { skips.ExcludedResponsible++; continue; }
            if (!RefBase.TryGetValue(key, out var bk) || !BaseInfo.TryGetValue(bk, out var bi) || !obstructing.Contains(bi.Type))
            { skips.NonObstructingType++; continue; }
            if (DetectorModel.ClassifyHost(bi.Type, bi.Edid) == HostClass.Marker
                || (BaseMesh.TryGetValue(bk, out var mesh) && Path.GetFileName(mesh).StartsWith("MarkerX", StringComparison.OrdinalIgnoreCase)))
            { skips.MarkerMesh++; continue; }
            var (box, src) = boxOf(bk);
            if (src == '-' || box.IsEmpty) { skips.NoGeometry++; continue; }
            var world = SpatialOverlap.ToWorld(box, s.Pos, s.Rot, s.Scale);
            if (world.IsEmpty) { skips.NoGeometry++; continue; }
            if (MathF.Max(world.Max.X - world.Min.X, MathF.Max(world.Max.Y - world.Min.Y, world.Max.Z - world.Min.Z)) > maxDim)
            { skips.Volume++; continue; }
            list.Add(new OverlapCandidate(key, s, bk, world, src));
        }
        return list;
    }

    /// <summary>The pair sweep: a spatial hash of 256 u tiles inside a space partition (interior cell | worldspace),
    /// every pair in a tile tested once with <see cref="DetectorModel.BoxOverlap"/>. Same-responsible pairs are the
    /// authored floor, tallied by domain; cross-responsible deep hits are collected for judgement, with the declared
    /// relation (closure in either direction) recorded on each.</summary>
    public static PairSweep Pairs(IReadOnlyList<OverlapCandidate> candidates, IReadOnlyList<HashSet<int>> closure,
                                  float minDepth, float minFrac, float tile = 256f)
    {
        var grid = new Dictionary<(FormKey Space, int X, int Y), List<int>>();
        for (int i = 0; i < candidates.Count; i++)
        {
            var c = candidates[i];
            var space = c.Win.Space;
            int x0 = (int)MathF.Floor(c.World.Min.X / tile), x1 = (int)MathF.Floor(c.World.Max.X / tile);
            int y0 = (int)MathF.Floor(c.World.Min.Y / tile), y1 = (int)MathF.Floor(c.World.Max.Y / tile);
            for (int x = x0; x <= x1; x++)
                for (int y = y0; y <= y1; y++)
                {
                    if (!grid.TryGetValue((space, x, y), out var l)) grid[(space, x, y)] = l = new List<int>();
                    l.Add(i);
                }
        }

        var sweep = new PairSweep();
        var seenPair = new HashSet<(FormKey, FormKey)>();
        foreach (var bucket in grid.Values)
        {
            if (bucket.Count < 2) continue;
            for (int a = 0; a < bucket.Count; a++)
                for (int b = a + 1; b < bucket.Count; b++)
                {
                    var ca = candidates[bucket[a]];
                    var cb = candidates[bucket[b]];
                    // A total order over FormKeys (ID, then ModKey name) so the same pair seen from two tiles
                    // dedupes regardless of which side came first.
                    bool aFirst = ca.Key.ID != cb.Key.ID
                        ? ca.Key.ID < cb.Key.ID
                        : string.CompareOrdinal(ca.Key.ModKey.FileName.String, cb.Key.ModKey.FileName.String) <= 0;
                    var key = aFirst ? (ca.Key, cb.Key) : (cb.Key, ca.Key);
                    if (!seenPair.Add(key)) continue;
                    sweep.PairsChecked++;

                    var ov = Overlap(ca.World, cb.World);
                    bool deep = ov is { } o && o.MinDepth >= minDepth && o.VolumeFraction >= minFrac;

                    if (ca.Win.Plugin == cb.Win.Plugin)
                    {
                        sweep.SameChecked++;
                        var dom = ca.Win.Interior ? "interior" : "exterior";
                        var cur = sweep.SameByDomain.GetValueOrDefault(dom);
                        sweep.SameByDomain[dom] = (cur.Deep + (deep ? 1 : 0), cur.Checked + 1);
                        if (deep) sweep.SameDeep++;
                        continue;
                    }
                    bool declared = closure[ca.Win.Plugin].Contains(cb.Win.Plugin) || closure[cb.Win.Plugin].Contains(ca.Win.Plugin);
                    if (declared) { sweep.CrossDeclaredChecked++; if (deep) sweep.CrossDeclaredDeep++; }
                    else sweep.CrossUndeclaredChecked++;
                    if (deep) sweep.CrossDeep.Add(new OverlapHit(ca, cb, ov!.Value.MinDepth, ov.Value.VolumeFraction, declared));
                }
        }
        return sweep;
    }

    public static (float MinDepth, float VolumeFraction)? Overlap(Aabb a, Aabb b) =>
        DetectorModel.BoxOverlap(new Vector3(a.Min.X, a.Min.Y, a.Min.Z), new Vector3(a.Max.X, a.Max.Y, a.Max.Z),
                                 new Vector3(b.Min.X, b.Min.Y, b.Min.Z), new Vector3(b.Max.X, b.Max.Y, b.Max.Z));

    /// <summary>Assemble the rule's facts for one cross-responsible hit and decide it. The LATER of the two responsible
    /// plugins is L (plugin indices are load-order positions). The arm without L is L's reference at its previous
    /// snap: absent if L introduced it; not rebuildable if that snap was deleted, not there in the arm without L, or in
    /// another space; else its box under the same base record's geometry, tested against the other reference's WINNING
    /// box at the run's threshold. The base record is the winner's — an override that also swapped the base is a stated
    /// limit.
    ///
    /// <para><paramref name="arms"/> (Q27, 2026-09-06): the arm's own enable chain. Without it the counterfactual reads
    /// the previous snap's initially-disabled FLAG while the winning population goes through the chain — a winning-state
    /// answer standing in for a historical one, and it decides Introduced against Inherited, which is the class. With it,
    /// "present in the arm without L" is <see cref="EnableArms.Without"/>: each chain element's flag at its authoring
    /// snap and its XESP as last set outside L's side. The POSITION in that arm is still <c>snaps[^2]</c>, 4.1's stated
    /// simplification — where L placed the reference more than once the two halves come from different snaps, and that
    /// limit is 4.1's, not something this pairing introduces.</para></summary>
    public OverlapRow Judge(OverlapHit hit, IReadOnlyList<HashSet<int>> closure, Func<FormKey, (Aabb Box, char Src)> boxOf,
                            float minDepth, float minFrac, EnableArms? arms = null)
    {
        var (later, other) = hit.A.Win.Plugin > hit.B.Win.Plugin ? (hit.A, hit.B) : (hit.B, hit.A);
        int L = later.Win.Plugin;
        var snaps = History.Of(later.Key)!;
        Snap? prev = snaps.Count >= 2 ? snaps[^2] : null;
        bool hadPrev = prev is not null;
        bool prevPresent = prev is { Deleted: false } p && p.Space == other.Win.Space
                        && !IsDeletedWithout(later.Key, L, closure)
                        && (arms is null ? !p.Disabled : arms.Without(later.Key, L, closure).Enabled);
        float wDepth = float.NaN, wFrac = float.NaN;
        bool withoutDeep = false;
        if (prevPresent)
        {
            var (box, src) = boxOf(later.Base);
            if (src != '-' && !box.IsEmpty)
            {
                var world = SpatialOverlap.ToWorld(box, prev!.Value.Pos, prev.Value.Rot, prev.Value.Scale);
                if (Overlap(world, other.World) is { } o)
                {
                    wDepth = o.MinDepth; wFrac = o.VolumeFraction;
                    withoutDeep = o.MinDepth >= minDepth && o.VolumeFraction >= minFrac;
                }
                else { wDepth = 0; wFrac = 0; }
            }
        }
        bool seen = closure[L].Contains(other.Win.Plugin);
        var facts = new DetectorModel.OverlapFacts(hadPrev, prevPresent, withoutDeep, seen);
        var (origin, sight, why) = DetectorModel.OverlapProvenance(facts);
        return new OverlapRow(hit, later, other, facts, origin, sight, why, prev,
                              History.AsSeenBy(other.Key, L, closure), wDepth, wFrac);
    }
}
