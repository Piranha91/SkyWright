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
// rotation and scale the world box needs), base-record types and mesh paths, the candidate
// build, the spatial hash, and the facts for one hit. The DECISION is DetectorModel.OverlapProvenance,
// owned by proofprobe; the broad-phase test is DetectorModel.BoxOverlap, likewise. Shared between
// overlapprobe (the real order, meshes through the VFS) and proofprobe (a synthetic order written with
// Mutagen, injected synthetic boxes), so the proof exercises the same extraction and the same arm construction
// the live run uses.
//
// Standing rules applied: complete decoded NIF render geometry only; no geometry = not
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
                                Snap? LaterPrevious, Snap? OtherAsSeenByLater, float WithoutDepth, float WithoutFrac,
                                string CounterfactualStatus = "measured", string? CounterfactualReason = null)
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

/// <summary>One actual placed render surface eligible for symmetric mesh intersection. The bounds enclose
/// <paramref name="WorldSoup"/> and are used only for conservative indexing/rejection.</summary>
public readonly record struct SurfaceOverlapCandidate(FormKey Key, Snap Win, FormKey Base, Aabb WorldBounds,
                                                       TriangleSoup WorldSoup, char Src);

/// <summary>The stable subset of the core mesh result consumed by probes and serialized evidence.</summary>
public sealed record SurfaceQueryOutcome(string Status, string? Reason, string SurfaceRelation, string Containment,
                                         bool? Intersects, long TrianglePairsTested)
{
    public bool Unknown => Intersects is null || Status == "unknown" || SurfaceRelation == "unknown";

    public static SurfaceQueryOutcome Query(TriangleSoup a, TriangleSoup b, double tolerance, long maxTrianglePairs,
                                            TimeSpan pairTimeout, CancellationToken cancellationToken = default)
    {
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(pairTimeout);
            var result = MeshIntersection.Query(a, b, tolerance, maxTrianglePairs, timeout.Token);
            return new(Normalize(result.Status), result.Reason, Normalize(result.SurfaceRelation),
                       Normalize(result.Containment), result.Intersects, result.TrianglePairsTested);
        }
        catch (OperationCanceledException)
        {
            return new("unknown", "pair time budget exhausted", "unknown", "unknown", null, 0);
        }
        catch (Exception ex)
        {
            return new("unknown", $"mesh query failed: {ex.GetType().Name}: {ex.Message}", "unknown", "unknown", null, 0);
        }
    }

    static string Normalize(object? value)
    {
        string text = Convert.ToString(value, System.Globalization.CultureInfo.InvariantCulture) ?? "unknown";
        if (text.Length == 0) return "unknown";
        var b = new System.Text.StringBuilder(text.Length + 4);
        for (int i = 0; i < text.Length; i++)
        {
            char c = text[i];
            if (char.IsUpper(c) && i > 0 && text[i - 1] != '_') b.Append('_');
            b.Append(char.ToLowerInvariant(c));
        }
        return b.ToString();
    }
}

public readonly record struct SurfaceOverlapHit(SurfaceOverlapCandidate A, SurfaceOverlapCandidate B,
                                                SurfaceQueryOutcome Query, bool Declared);

public sealed record SurfaceOverlapRow(SurfaceOverlapHit Hit, SurfaceOverlapCandidate LaterRef,
    SurfaceOverlapCandidate OtherRef, DetectorModel.OverlapFacts Facts, OverlapOrigin? Origin, OverlapSight Sight,
    string Why, Snap? LaterPrevious, Snap? OtherAsSeenByLater, SurfaceQueryOutcome? Counterfactual,
    string CounterfactualStatus, string? CounterfactualReason)
{
    public int Later => LaterRef.Win.Plugin;
    public int Earlier => OtherRef.Win.Plugin;
}

public sealed class SurfacePairSweep
{
    public long PairsChecked, SameChecked, SameIntersects, SameUnknown,
                CrossDeclaredChecked, CrossDeclaredIntersects, CrossUndeclaredChecked,
                CrossUndeclaredIntersects, CrossUnknown;
    public readonly Dictionary<string, (long Intersects, long Unknown, long Checked)> SameByDomain = new();
    /// <summary>Same-responsible queries already measured by the sweep, retained only when requested by a caller.</summary>
    public readonly List<SurfaceOverlapHit> SameResults = new();
    /// <summary>Every cross-responsible query reached by the tolerance-aware broad phase, including separation and unknown.</summary>
    public readonly List<SurfaceOverlapHit> CrossResults = new();
    /// <summary>Every cross-responsible actual-shape intersection; unknown is never promoted into this list.</summary>
    public readonly List<SurfaceOverlapHit> CrossHits = new();
}

/// <summary>What the candidate build set aside, counted per rule 6.</summary>
public sealed class CandidateSkips
{
    public long Deleted, Disabled, NonObstructingType, NoGeometry, InvalidTransform, DegenerateGeometry,
                ExcludedResponsible, ScopeExcluded, Volume, MarkerMesh, EnableStateUnresolved, BosUnresolved;
}

/// <summary>The pair sweep's tallies: the same-author floor by domain, and the two cross populations.</summary>
public sealed class PairSweep
{
    public long PairsChecked, SameChecked, SameDeep, CrossDeclaredChecked, CrossDeclaredDeep, CrossUndeclaredChecked;
    public readonly Dictionary<string, (long Deep, long Checked)> SameByDomain = new();
    /// <summary>Every cross-responsible deep hit, declared or not — the population the provenance rule runs over.</summary>
    public readonly List<OverlapHit> CrossDeep = new();
    /// <summary>Every cross-responsible pair whose finite boxes intersect, including intersections below the detector
    /// thresholds. This keeps "measurable but below threshold" distinct from an unmeasurable pair in review exports.</summary>
    public readonly List<(OverlapCandidate A, OverlapCandidate B, float Depth, float Frac, bool Deep, bool Declared)> CrossIntersections = new();
}

public sealed class OverlapIndex
{
    public PlacementHistory History { get; } = new();
    /// <summary>base record -> its Mutagen type name and editor id, winner wins. Modeled records only.</summary>
    public Dictionary<FormKey, (string Type, string? Edid)> BaseInfo { get; } = new();
    /// <summary>Light bases carrying the Can Be Carried flag (torches, lanterns): loose items under overlapprobe's rest rule.</summary>
    public HashSet<FormKey> LightCarryable { get; } = new();
    public Dictionary<FormKey, string> BaseMesh { get; } = new();
    /// <summary>An armour's FEMALE ground model where the record gives one (BaseMesh holds the male) — G1 build list row 2.</summary>
    public Dictionary<FormKey, string> BaseMeshFemale { get; } = new();
    /// <summary>base record -> its model's alternate textures (MODL alternate texture entries: 3D shape name, 3D index, texture set),
    /// winner wins; absent when the winning record has none. Read by the visible-crossing measure (texture alpha).</summary>
    public Dictionary<FormKey, List<(string Name, int Index, FormKey TextureSet)>> BaseAlternateTextures { get; } = new();
    /// <summary>texture set (TXST) -> its diffuse path as given, winner wins (null when the winning record names none).</summary>
    public Dictionary<FormKey, string?> TextureSetDiffuse { get; } = new();
    /// <summary>base record -> OBND extents (|second − first| per axis).</summary>
    public Dictionary<FormKey, Vec3> BaseObnd { get; } = new();
    /// <summary>The latest record/runtime pose for each reference, including rotation-only and scale-only overrides.
    /// This is deliberately separate from <see cref="History"/>, whose last snap identifies the last plugin that made
    /// an attributable placement step. Geometry uses this pose while provenance keeps the changed-only attribution.</summary>
    readonly Dictionary<FormKey, Snap> _effectivePose = new();
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
    /// <summary>COLL records as the reach rules' drop reads them, by INDEX under the winner: the TriggerVolume and Sensor
    /// flags and the records listed in CollidesWith (resolved to indices by <see cref="CollisionLayerRules"/>).</summary>
    public Dictionary<uint, (FormKey Key, string? Edid, bool TriggerVolume, bool Sensor, List<FormKey> CollidesWith)> CollisionLayerRecords { get; } = new();
    /// <summary>Set before ingesting to keep each exterior grid's terrain under the winner (the reach rules' drop): the
    /// decoded heights, or why a LAND that says it has heights gives none. A LAND that declares no heights removes the grid.
    /// Off by default: nothing else reads it.</summary>
    public bool TrackLand { get; set; }
    public Dictionary<(FormKey Ws, int X, int Y), float[,]> Land { get; } = new();
    public Dictionary<(FormKey Ws, int X, int Y), string> LandUnreadable { get; } = new();

    /// <summary>BOOK -> the Static its InventoryArt names (a placed Static of that base is book art: the reach rules read it
    /// closed). Under the winner.</summary>
    public Dictionary<FormKey, FormKey> BookInventoryArt { get; } = new();

    /// <summary>The COLL records as drop-rule layers: CollidesWith resolved from record keys to layer indices.</summary>
    public Dictionary<uint, CollisionLayerRule> CollisionLayerRules()
    {
        var byKey = CollisionLayerRecords.ToDictionary(kv => kv.Value.Key, kv => kv.Key);
        return CollisionLayerRecords.ToDictionary(kv => kv.Key, kv => new CollisionLayerRule(kv.Key, kv.Value.Edid, kv.Value.TriggerVolume, kv.Value.Sensor,
            kv.Value.CollidesWith.Where(byKey.ContainsKey).Select(k => byKey[k]).ToHashSet()));
    }
    /// <summary>ref -> its XTRI collision-layer index under the winner; absent when none.</summary>
    public Dictionary<FormKey, uint> RefCollisionLayer { get; } = new();
    /// <summary>ref -> its winning record's flags (the load-settle rule reads them).</summary>
    public Dictionary<FormKey, uint> RefRecordFlags { get; } = new();
    /// <summary>WEAP base -> its animation type by the record enum name (the load-settle rule reads it).</summary>
    /// <summary>The worldspace persistent cells (the top cell of each worldspace, no grid) — a marker placed persistent
    /// sits here whatever its coordinates, so a per-cell census labels it rather than reading it as a crowded cell.</summary>
    public HashSet<FormKey> PersistentCells { get; } = new();
    /// <summary>ref -> its XPRM primitive under the winner; absent when none. A navcut's obstacle is this box.</summary>
    public Dictionary<FormKey, PlacedPrimitiveInfo> RefPrimitive { get; } = new();
    /// <summary>Refs whose winning record carries ONAM (IsOpenByDefault), the motion reader's rest pose (follow-ups E3).</summary>
    public HashSet<FormKey> RefOpenByDefault { get; } = new();
    /// <summary>ref -> its winning record's XLKR links and XAPR parents (absent when none): the two-mover coupling (E4).</summary>
    public Dictionary<FormKey, IReadOnlyList<PlacedLink>> RefLinkedRefs { get; } = new();
    public Dictionary<FormKey, IReadOnlyList<PlacedActivateParent>> RefActivateParents { get; } = new();
    /// <summary>ref -> its editor id, the last non-empty one in load order (an override that omits it keeps the earlier
    /// name). What the world-state census names a switch by.</summary>
    public Dictionary<FormKey, string> RefEditorId { get; } = new();

    /// <summary>Is the WINNING record of the reference a deletion? The last record in the evaluated order decides —
    /// not the last plugin that ever deleted it, and not the last plugin to change its placement (Q34).</summary>
    public bool IsDeletedNow(FormKey key) => Deletions.AtWinner(key);

    /// <summary>The effective winning geometry pose with the responsible placement plugin retained in
    /// <see cref="Snap.Plugin"/>. Returns null when the reference has no placement history. A rotation-only or scale-only
    /// override therefore changes geometry without taking placement responsibility.</summary>
    public Snap? WinningGeometryPose(FormKey key)
    {
        if (History.Winning(key) is not { } responsible || !_effectivePose.TryGetValue(key, out var effective)) return null;
        return effective with { Plugin = responsible.Plugin };
    }

    /// <summary>Update the runtime pose after a non-record layer such as BOS applies properties.</summary>
    public void NoteEffectivePose(FormKey key, Snap pose) => _effectivePose[key] = pose;

    /// <summary>…and the same question in the arm WITHOUT <paramref name="mover"/>'s side: the last record by a plugin
    /// that is neither the mover nor downstream of it. What the counterfactual arms ask, so an arm that excludes a
    /// RESTORING plugin sees the deletion that stands without it.</summary>
    public bool IsDeletedWithout(FormKey key, int mover, IReadOnlyList<HashSet<int>> closure)
        => Deletions.Without(key, mover, closure);

    void NoteAlternates(FormKey key, IReadOnlyList<IAlternateTextureGetter>? alternates)
    {
        if (alternates is not { Count: > 0 }) { BaseAlternateTextures.Remove(key); return; }
        BaseAlternateTextures[key] = alternates.Select(a => (a.Name ?? "", a.Index, a.NewTexture.FormKey)).ToList();
    }

    /// <summary>Read one plugin, in load order: placements into the history and winning base-record names/meshes.</summary>
    public void Ingest(int plugin, ISkyrimModGetter mod)
    {
        foreach (var rec in mod.EnumerateMajorRecords())
        {
            Bos?.Note(rec);
            if (rec is ITextureSetGetter txst) { TextureSetDiffuse[txst.FormKey] = txst.Diffuse?.GivenPath is { Length: > 0 } dp ? dp : null; continue; }
            // Every placed record states whether it is deleted, whether or not it kept its placement — that is what
            // makes a later restoration expressible at all (Q34).
            if (rec is IPlacedGetter pg) { Deletions.Note(plugin, pg.FormKey, pg.IsDeleted, pg.Placement is not null); continue; }
            if (rec is ICollisionLayerGetter coll)
            {
                CollisionLayersByIndex[coll.Index] = (coll.FormKey, coll.EditorID, coll.Flags.HasFlag(CollisionLayer.Flag.NavmeshObstacle));
                CollisionLayerRecords[coll.Index] = (coll.FormKey, coll.EditorID, coll.Flags.HasFlag(CollisionLayer.Flag.TriggerVolume),
                                                     coll.Flags.HasFlag(CollisionLayer.Flag.Sensor), (coll.CollidesWith ?? []).Select(l => l.FormKey).ToList());
                continue;
            }
            // Armour carries its ground model per gender in WorldModel, not Model (G1 build list row 2, Gate A review Q6): the
            // male path goes to BaseMesh (the female when only it is given), the female to BaseMeshFemale.
            if (rec is IArmorGetter ar)
            {
                BaseInfo[ar.FormKey] = (rec.GetType().Name.Replace("BinaryOverlay", ""), ar.EditorID);
                string? male = ar.WorldModel?.Male?.Model?.File is { } mf0 && !string.IsNullOrWhiteSpace(mf0.GivenPath) ? mf0.GivenPath : null;
                string? female = ar.WorldModel?.Female?.Model?.File is { } ff0 && !string.IsNullOrWhiteSpace(ff0.GivenPath) ? ff0.GivenPath : null;
                if ((male ?? female) is { } any) BaseMesh[ar.FormKey] = any; else BaseMesh.Remove(ar.FormKey);
                NoteAlternates(ar.FormKey, male is not null ? ar.WorldModel?.Male?.Model?.AlternateTextures : ar.WorldModel?.Female?.Model?.AlternateTextures);
                if ((female ?? male) is { } anyF) BaseMeshFemale[ar.FormKey] = anyF; else BaseMeshFemale.Remove(ar.FormKey);
                if ((ar.MajorRecordFlagsRaw & EnableState.IsMarkerFlag) != 0) BaseIsMarker.Add(ar.FormKey); else BaseIsMarker.Remove(ar.FormKey);
                if (ar.ObjectBounds is { } ab)
                    BaseObnd[ar.FormKey] = new Vec3(MathF.Abs(ab.Second.X - ab.First.X), MathF.Abs(ab.Second.Y - ab.First.Y), MathF.Abs(ab.Second.Z - ab.First.Z));
                continue;
            }
            if (rec is IBookGetter bk)
            {
                if (!bk.InventoryArt.IsNull) BookInventoryArt[bk.FormKey] = bk.InventoryArt.FormKey; else BookInventoryArt.Remove(bk.FormKey);
            }
            if (rec is ILightGetter lg)
            {
                if (lg.Flags.HasFlag(Light.Flag.CanBeCarried)) LightCarryable.Add(lg.FormKey); else LightCarryable.Remove(lg.FormKey);
            }
            if (rec is IWeaponGetter wp)
            {
            }
            if (rec is IModeledGetter mo && rec is ISkyrimMajorRecordGetter sm)
            {
                BaseInfo[sm.FormKey] = (rec.GetType().Name.Replace("BinaryOverlay", ""), sm.EditorID);
                if (mo.Model?.File is { } mf && !string.IsNullOrWhiteSpace(mf.GivenPath)) BaseMesh[sm.FormKey] = mf.GivenPath;
                else BaseMesh.Remove(sm.FormKey);
                NoteAlternates(sm.FormKey, mo.Model?.AlternateTextures);
                if ((sm.MajorRecordFlagsRaw & EnableState.IsMarkerFlag) != 0) BaseIsMarker.Add(sm.FormKey); else BaseIsMarker.Remove(sm.FormKey);
                if (rec is IObjectBoundedGetter ob && ob.ObjectBounds is { } bnd)
                    BaseObnd[sm.FormKey] = new Vec3(MathF.Abs(bnd.Second.X - bnd.First.X),
                                                    MathF.Abs(bnd.Second.Y - bnd.First.Y),
                                                    MathF.Abs(bnd.Second.Z - bnd.First.Z));
            }
        }
        if (TrackLand)
            foreach (var lc in PlacementExtract.FromMod(mod, includeEmpty: true))
            {
                if (lc.Interior || lc.Worldspace.IsNull || lc.Grid is not { } g) continue;
                var gk = (lc.Worldspace, g.X, g.Y);
                if (lc.LandUnreadableReason is { } why) { Land.Remove(gk); LandUnreadable[gk] = why; }
                else if (lc.LandDeclaresNoHeights) { Land.Remove(gk); LandUnreadable.Remove(gk); }
                else if (lc.Land is { } h) { Land[gk] = h; LandUnreadable.Remove(gk); }
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
        if (r.OpenByDefault) RefOpenByDefault.Add(r.FormKey); else RefOpenByDefault.Remove(r.FormKey);
        RefRecordFlags[r.FormKey] = r.RecordFlags;
        if (r.LinkedReferences is { Count: > 0 } links) RefLinkedRefs[r.FormKey] = links; else RefLinkedRefs.Remove(r.FormKey);
        if (r.ActivateParents is { Count: > 0 } parents) RefActivateParents[r.FormKey] = parents; else RefActivateParents.Remove(r.FormKey);
        Deletions.Note(plugin, r.FormKey, r.Deleted, hasPlacement: true);
        var snap = new Snap(plugin, r.Position, cell, r.InitiallyDisabled, r.Deleted, worldspace, r.Rotation, r.Scale);
        if (History.Winning(r.FormKey) is { } prev && prev.SameAs(snap)
            && (MathF.Abs(prev.Rot.X - snap.Rot.X) > 1e-4f || MathF.Abs(prev.Rot.Y - snap.Rot.Y) > 1e-4f
             || MathF.Abs(prev.Rot.Z - snap.Rot.Z) > 1e-4f || MathF.Abs(prev.Scale - snap.Scale) > 1e-3f))
            SilentRePlacements++;
        History.Record(r.FormKey, snap);
        _effectivePose[r.FormKey] = snap;
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

    /// <summary>A synthetic/test helper retained for historical proof fixtures. Production overlap discovery and
    /// remeasurement never call it; record ObjectBounds are not a geometry source or fallback.</summary>
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
            var s = WinningGeometryPose(key) ?? snaps[^1];
            // The winning RECORD decides, not the last placement step's own flag: a deletion that stripped the
            // placement leaves no step at all, so a reference removed that way used to stay a candidate (Q34).
            if (s.Deleted || IsDeletedNow(key)) { skips.Deleted++; continue; }
            if (enabledOf is null ? s.Disabled : !enabledOf(key)) { skips.Disabled++; continue; }
            if (excludeResponsible(s.Plugin)) { skips.ExcludedResponsible++; continue; }
            if (!RefBase.TryGetValue(key, out var bk) || !BaseInfo.TryGetValue(bk, out var bi) || !obstructing.Contains(bi.Type))
            { skips.NonObstructingType++; continue; }
            if (BaseIsMarker.Contains(bk) || DetectorModel.ClassifyHost(bi.Type, bi.Edid) == HostClass.Marker
                || (BaseMesh.TryGetValue(bk, out var mesh) && Path.GetFileName(mesh).StartsWith("MarkerX", StringComparison.OrdinalIgnoreCase)))
            { skips.MarkerMesh++; continue; }
            var (box, src) = boxOf(bk);
            if (src == '-' || box.IsEmpty) { skips.NoGeometry++; continue; }
            var placed = SpatialOverlap.PlaceBounds(box, s.Pos, s.Rot, s.Scale);
            if (placed.Status == "invalid_transform") { skips.InvalidTransform++; continue; }
            if (placed.Status == "degenerate_geometry") { skips.DegenerateGeometry++; continue; }
            if (!placed.UsableForOverlap || placed.Bounds is not { } world) { skips.NoGeometry++; continue; }
            if (MathF.Max(world.Max.X - world.Min.X, MathF.Max(world.Max.Y - world.Min.Y, world.Max.Z - world.Min.Z)) > maxDim)
            { skips.Volume++; continue; }
            list.Add(new OverlapCandidate(key, s, bk, world, src));
        }
        return list;
    }

    /// <summary>Build actual-surface candidates under the same declared source/state/type/marker rules as the historical
    /// overlap pass. No box size, depth or volume gate applies. Flat triangle soups remain eligible.</summary>
    public List<SurfaceOverlapCandidate> SurfaceCandidates(
        Func<FormKey, (TriangleSoup? Soup, char Src, string? Reason)> geometryOf,
        ISet<string> obstructing, Func<int, bool> excludeResponsible, out CandidateSkips skips,
        Func<FormKey, bool>? enabledOf = null, Func<FormKey, Snap, bool>? includeReference = null)
    {
        skips = new CandidateSkips();
        var list = new List<SurfaceOverlapCandidate>();
        foreach (var (key, snaps) in History.Refs)
        {
            var s = WinningGeometryPose(key) ?? snaps[^1];
            if (includeReference is not null && !includeReference(key, s)) { skips.ScopeExcluded++; continue; }
            if (s.Deleted || IsDeletedNow(key)) { skips.Deleted++; continue; }
            if (enabledOf is null ? s.Disabled : !enabledOf(key)) { skips.Disabled++; continue; }
            if (excludeResponsible(s.Plugin)) { skips.ExcludedResponsible++; continue; }
            if (!RefBase.TryGetValue(key, out var bk) || !BaseInfo.TryGetValue(bk, out var bi) || !obstructing.Contains(bi.Type))
            { skips.NonObstructingType++; continue; }
            if (BaseIsMarker.Contains(bk) || DetectorModel.ClassifyHost(bi.Type, bi.Edid) == HostClass.Marker
                || (BaseMesh.TryGetValue(bk, out var mesh) && Path.GetFileName(mesh).StartsWith("MarkerX", StringComparison.OrdinalIgnoreCase)))
            { skips.MarkerMesh++; continue; }
            var (model, src, _) = geometryOf(bk);
            if (src == '-' || model is not { TriangleCount: > 0 }) { skips.NoGeometry++; continue; }
            if (s.Scale == 0) { skips.DegenerateGeometry++; continue; }
            var world = SpatialOverlap.TransformSoup(model, s.Pos, s.Rot, s.Scale);
            var bounds = SpatialOverlap.SoupBounds(world);
            if (bounds.IsEmpty || !SpatialOverlap.IsFinite(bounds)) { skips.InvalidTransform++; continue; }
            list.Add(new(key, s, bk, bounds, world, src));
        }
        return list;
    }

    /// <summary>Symmetric actual-surface discovery. Bounds only decide a provably separated broad-phase rejection;
    /// every surviving pair reaches the generic mesh predicate with a per-pair work and time budget.</summary>
    public static SurfacePairSweep SurfacePairs(IReadOnlyList<SurfaceOverlapCandidate> candidates,
        IReadOnlyList<HashSet<int>> closure, double tolerance = 1e-5, long maxTrianglePairs = 2_000_000,
        TimeSpan? pairTimeout = null, Func<int, int, bool>? includeResponsiblePair = null,
        Action<long>? onProgress = null, float tile = 256f, CancellationToken cancellationToken = default,
        bool retainSameResults = false)
    {
        var timeout = pairTimeout ?? TimeSpan.FromSeconds(120);
        var sweep = new SurfacePairSweep();
        // A sweep per coordinate space has no allocation proportional to an object's dimensions. This matters now that
        // large real surfaces are eligible. Bounds reject only axis separation greater than the predicate tolerance.
        foreach (var space in candidates.GroupBy(c => c.Win.Space))
        {
            var ordered = space.OrderBy(c => (double)c.WorldBounds.Min.X).ThenBy(c => c.Key.ToString(), StringComparer.Ordinal).ToList();
            for (int ai = 0; ai < ordered.Count; ai++)
                for (int bi = ai + 1; bi < ordered.Count; bi++)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var a = ordered[ai]; var b = ordered[bi];
                    if ((double)b.WorldBounds.Min.X - a.WorldBounds.Max.X > tolerance) break;
                    if ((double)b.WorldBounds.Min.Y - a.WorldBounds.Max.Y > tolerance
                        || (double)a.WorldBounds.Min.Y - b.WorldBounds.Max.Y > tolerance
                        || (double)b.WorldBounds.Min.Z - a.WorldBounds.Max.Z > tolerance
                        || (double)a.WorldBounds.Min.Z - b.WorldBounds.Max.Z > tolerance) continue;
                    if (includeResponsiblePair is not null && !includeResponsiblePair(a.Win.Plugin, b.Win.Plugin)) continue;
                    sweep.PairsChecked++;
                    var query = SurfaceQueryOutcome.Query(a.WorldSoup, b.WorldSoup, tolerance, maxTrianglePairs, timeout, cancellationToken);
                    bool intersects = query.Intersects == true;
                    bool unknown = query.Unknown;

                    if (a.Win.Plugin == b.Win.Plugin)
                    {
                        if (retainSameResults) sweep.SameResults.Add(new(a, b, query, true));
                        sweep.SameChecked++;
                        if (intersects) sweep.SameIntersects++;
                        if (unknown) sweep.SameUnknown++;
                        string domain = a.Win.Interior ? "interior" : "exterior";
                        var old = sweep.SameByDomain.GetValueOrDefault(domain);
                        sweep.SameByDomain[domain] = (old.Intersects + (intersects ? 1 : 0), old.Unknown + (unknown ? 1 : 0), old.Checked + 1);
                    }
                    else
                    {
                        bool declared = closure[a.Win.Plugin].Contains(b.Win.Plugin) || closure[b.Win.Plugin].Contains(a.Win.Plugin);
                        var hit = new SurfaceOverlapHit(a, b, query, declared);
                        sweep.CrossResults.Add(hit);
                        if (intersects) sweep.CrossHits.Add(hit);
                        if (unknown) sweep.CrossUnknown++;
                        if (declared)
                        {
                            sweep.CrossDeclaredChecked++;
                            if (intersects) sweep.CrossDeclaredIntersects++;
                        }
                        else
                        {
                            sweep.CrossUndeclaredChecked++;
                            if (intersects) sweep.CrossUndeclaredIntersects++;
                        }
                    }
                    onProgress?.Invoke(sweep.PairsChecked);
                }
        }
        return sweep;
    }

    /// <summary>Apply the unchanged provenance rule to an actual-shape hit, rebuilding the arm without the later
    /// responsible plugin from strict model triangles. An unknown counterfactual is retained and withheld from credit.</summary>
    public SurfaceOverlapRow JudgeSurface(SurfaceOverlapHit hit, IReadOnlyList<HashSet<int>> closure,
        Func<FormKey, (TriangleSoup? Soup, char Src, string? Reason)> geometryOf, double tolerance = 1e-5,
        long maxTrianglePairs = 2_000_000, TimeSpan? pairTimeout = null, EnableArms? arms = null,
        CancellationToken cancellationToken = default)
    {
        var (later, other) = hit.A.Win.Plugin > hit.B.Win.Plugin ? (hit.A, hit.B) : (hit.B, hit.A);
        int mover = later.Win.Plugin;
        var snaps = History.Of(later.Key)!;
        int previousIndex = PlacementHistory.AuthoringSnapIndex(snaps, mover, closure);
        Snap? previous = previousIndex >= 0 ? snaps[previousIndex] : null;
        bool hadPrevious = previous is not null;
        var previousEnable = previous is not null && arms is not null ? arms.Without(later.Key, mover, closure) : default;
        bool enableKnown = previous is null || arms is null || previousEnable.Resolved;
        bool previousPresent = enableKnown && previous is { Deleted: false } p && p.Space == other.Win.Space
            && !IsDeletedWithout(later.Key, mover, closure)
            && (arms is null ? !p.Disabled : previousEnable.Enabled);
        SurfaceQueryOutcome? counterfactual = null;
        string counterfactualStatus = !enableKnown ? "unknown" : previousPresent ? "unknown" : "absent";
        string? counterfactualReason = !enableKnown ? "the previous arm's enable chain is unresolved"
            : previousPresent ? "the previous-placement render surface was not measured" : null;

        if (previousPresent)
        {
            FormKey previousBase = FormKey.Null;
            if (BaseChanges.TryGetValue(later.Key, out var changes))
                for (int i = changes.Count - 1; i >= 0; i--)
                {
                    int basePlugin = changes[i].Plugin;
                    if (basePlugin == mover || closure[basePlugin].Contains(mover)) continue;
                    previousBase = changes[i].Base;
                    break;
                }
            var (model, _, reason) = previousBase.IsNull ? (null, '-', "the previous arm has no base record") : geometryOf(previousBase);
            if (model is { TriangleCount: > 0 })
            {
                var world = SpatialOverlap.TransformSoup(model, previous!.Value.Pos, previous.Value.Rot, previous.Value.Scale);
                counterfactual = SurfaceQueryOutcome.Query(world, other.WorldSoup, tolerance, maxTrianglePairs,
                    pairTimeout ?? TimeSpan.FromSeconds(120), cancellationToken);
                counterfactualStatus = counterfactual.Unknown ? "unknown" : "measured";
                counterfactualReason = counterfactual.Reason;
            }
            else counterfactualReason = reason ?? "the previous arm has no complete strict render triangles";
        }

        bool withoutIntersection = counterfactual?.Intersects == true;
        bool seen = closure[mover].Contains(other.Win.Plugin);
        var facts = new DetectorModel.OverlapFacts(hadPrevious, previousPresent, withoutIntersection, seen);
        var (origin, sight, why) = DetectorModel.OverlapProvenance(facts);
        OverlapOrigin? assessedOrigin = counterfactualStatus == "unknown" ? null : origin;
        if (assessedOrigin is null) why = counterfactualReason ?? "counterfactual actual-shape result is unknown";
        return new(hit, later, other, facts, assessedOrigin, sight, why, previous,
            History.AsSeenBy(other.Key, mover, closure), counterfactual, counterfactualStatus, counterfactualReason);
    }

    /// <summary>The pair sweep: a spatial hash of 256 u tiles inside a space partition (interior cell | worldspace),
    /// every pair in a tile tested once with <see cref="DetectorModel.BoxOverlap"/>. Same-responsible pairs are the
    /// authored floor, tallied by domain; cross-responsible deep hits are collected for judgement, with the declared
    /// relation (closure in either direction) recorded on each.</summary>
    public static PairSweep Pairs(IReadOnlyList<OverlapCandidate> candidates, IReadOnlyList<HashSet<int>> closure,
                                  float minDepth, float minFrac, float tile = 256f,
                                  Func<int, int, bool>? includeResponsiblePair = null)
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
                    if (includeResponsiblePair is not null && !includeResponsiblePair(ca.Win.Plugin, cb.Win.Plugin)) continue;
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
                    if (ov is { } intersection)
                        sweep.CrossIntersections.Add((ca, cb, intersection.MinDepth, intersection.VolumeFraction, deep, declared));
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
        string counterfactualStatus = prevPresent ? "unmeasurable" : "absent";
        string? counterfactualReason = prevPresent ? "the previous-placement geometry was not measured" : null;
        if (prevPresent)
        {
            var (box, src) = boxOf(later.Base);
            if (src != '-' && !box.IsEmpty)
            {
                var placed = SpatialOverlap.PlaceBounds(box, prev!.Value.Pos, prev.Value.Rot, prev.Value.Scale);
                if (placed.UsableForOverlap && placed.Bounds is { } world)
                {
                    counterfactualStatus = "measured";
                    counterfactualReason = null;
                    if (Overlap(world, other.World) is { } o)
                    {
                        wDepth = o.MinDepth; wFrac = o.VolumeFraction;
                        withoutDeep = o.MinDepth >= minDepth && o.VolumeFraction >= minFrac;
                    }
                    else { wDepth = 0; wFrac = 0; }
                }
                else counterfactualReason = placed.Reason;
            }
        }
        bool seen = closure[L].Contains(other.Win.Plugin);
        var facts = new DetectorModel.OverlapFacts(hadPrev, prevPresent, withoutDeep, seen);
        var (origin, sight, why) = DetectorModel.OverlapProvenance(facts);
        return new OverlapRow(hit, later, other, facts, origin, sight, why, prev,
                              History.AsSeenBy(other.Key, L, closure), wDepth, wFrac,
                              counterfactualStatus, counterfactualReason);
    }
}
