using System.Numerics;
using HousecarlCore;
using Mutagen.Bethesda.Plugins;

namespace HarnessShared;

// THE EMPTY-SPACE SEARCH — LEFT IN EMPTY SPACE (2026-10-14), the sibling of STRANDED over the same arms.
//
// A fixture's ASSEMBLY is the fixture plus every reference it touches within ε (any surface: collision, else render, labelled),
// transitively, plus the items its weapon racks mount (core RackMount, the rest rule's reading). The walk ends at an ANCHOR: a
// floor or wall — a Static (or a Fixed MoveableStatic) with solid collision, at least MinAnchorDim across, not a marker, and not
// a reference the fixture's own plugin defines (a Bethesda master's references always count). GROUNDED: the assembly touches an
// anchor. The rule (DetectorModel.EmptySpace) reads the two arms ContactIndex already builds — the winner, and the order without
// a mover's side (R1 enable state per arm, the --state flip carried by both) — so a weapon rack that holds a rack that holds a
// sword in a void a later plugin made is read as in the void, where Stranded calls it held by its neighbour.
//
// Shared by contactprobe --empty-space (the load-time state or one --state), stateprobe --empty-space (each state's arriving
// references) and proofprobe (a synthetic order with OBND box soups). Everything here is the walk and the facts.

/// <summary>One arm's reading of a fixture's assembly. <paramref name="Anchors"/>: the anchors it touches (key, base, largest
/// world dimension, surface source c/r/o); <paramref name="ChangedAnchors"/>: those absent or moved under the winner (authoring arm).</summary>
public sealed record EmptySpaceArmReading(bool Exists, bool Grounded, bool OverCap, int Members, IReadOnlyList<FormKey> FirstMembers,
    IReadOnlyList<(FormKey Key, FormKey Base, float Dim, char Src)> Anchors, IReadOnlyList<FormKey> ChangedAnchors, bool AnchorUndecided = false,
    int UnsoundAnchors = 0)
{
    public static readonly EmptySpaceArmReading Missing = new(false, false, false, 0, Array.Empty<FormKey>(),
        Array.Empty<(FormKey, FormKey, float, char)>(), Array.Empty<FormKey>());
}

/// <summary>One fixture against one mover, judged. <paramref name="HeldBy"/> says why it stays put (fixed, mounted on a trigger, or
/// resting on a fixed reference); <paramref name="OwnSupport"/> the Fixed references of the fixture's own plugin it rests on under the
/// winner (Gate A review condition 2); <paramref name="NearestAnchor"/> the least box distance from the winner assembly to any anchor
/// within <see cref="EmptySpaceSearch.AnchorReport"/> (+inf when none; diagnostic, the verdict never reads it);
/// <paramref name="GroundedAt128"/> / <paramref name="GroundedAt256"/>: the winner arm read again with the anchor size at 128 u and
/// 256 u (sensitivity, printed beside the row; null when not read).</summary>
public sealed record EmptySpaceRow(FormKey Fixture, FormKey Base, int Mover, int Placer, FormKey Cell, bool Interior, string? State,
    DependentMotion Motion, string HeldBy, IReadOnlyList<FormKey> OwnSupport,
    EmptySpaceArmReading Winner, EmptySpaceArmReading Authoring, float NearestAnchor, bool? GroundedAt128, bool? GroundedAt256,
    DetectorModel.EmptySpaceFacts Facts, EmptySpaceVerdict Verdict, string Why, bool? GroundedAt32 = null);

public sealed class EmptySpaceSearch
{
    public ContactIndex C { get; }
    readonly IReadOnlyList<HashSet<int>> _closure;
    readonly Func<int, string> _pluginName;
    readonly Func<FormKey, string, bool?> _triggerHasNode;
    readonly Func<string, bool> _isBethesdaFile;
    readonly Func<int, bool> _isFloorPlugin;
    /// <summary>The largest assembly read before the arm is refused by name.</summary>
    public int MaxMembers { get; init; } = 256;
    /// <summary>An anchor's least largest world dimension (ContactIndex's host scale).</summary>
    public float MinAnchorDim { get; init; } = 64f;
    /// <summary>How far the diagnostic nearest-anchor distance looks.</summary>
    public float AnchorReport { get; init; } = 512f;
    /// <summary>How many suspect floor or wall pieces (each touched a piece the mover changed) an anchor's grounding may cross before the
    /// reading is refused by name (Q-B2).</summary>
    public int MaxAnchorWalk { get; init; } = 64;
    /// <summary>Candidates: dependent-scale references (as STRANDED's dependents) within this of a moved host's previous box.</summary>
    public float Reach => C.Reach;
    public long TouchTests, SurfacesBuilt, ExteriorHostsSkipped, WalksRun, WalksReused;

    Dictionary<FormKey, (FormKey Act, FormKey Trig)>? _rackItem;
    Dictionary<FormKey, List<FormKey>>? _trigItems;
    readonly System.Collections.Concurrent.ConcurrentDictionary<(FormKey, int, FormKey), Lazy<(TriangleSoup Soup, TriangleGrid Grid, char Src)?>> _surf = new();
    /// <summary>One state's own enable arms on a per-state view (<see cref="JudgeArrivalsAlone"/>); null: the index's arms.</summary>
    readonly EnableArms? _stateArms;
    const int SurfCacheMax = 20000;

    /// <param name="triggerHasNode">whether a weapon-rack trigger base's model has a named node (true / false; null when its model
    /// could not be read): the rack reading needs the pivot to exist, never its pose here.</param>
    /// <param name="isBethesdaFile">a plugin file name is a Bethesda master (its references always count as the room).</param>
    /// <param name="isFloorPlugin">a plugin index is Bethesda or USSEP (rule 12's floor population).</param>
    public EmptySpaceSearch(ContactIndex c, IReadOnlyList<HashSet<int>> closure, Func<int, string> pluginName, Func<FormKey, string, bool?> triggerHasNode,
                            Func<string, bool> isBethesdaFile, Func<int, bool> isFloorPlugin)
    {
        C = c; _closure = closure; _pluginName = pluginName; _triggerHasNode = triggerHasNode; _isBethesdaFile = isBethesdaFile; _isFloorPlugin = isFloorPlugin;
    }

    /// <summary>One state's view of <paramref name="root"/>: its own enable arms and walk memos, sharing the surfaces and the space hashes.</summary>
    EmptySpaceSearch(EmptySpaceSearch root, StateArm arm)
    {
        C = root.C; _closure = root._closure; _pluginName = root._pluginName; _triggerHasNode = root._triggerHasNode; _isBethesdaFile = root._isBethesdaFile; _isFloorPlugin = root._isFloorPlugin;
        MaxMembers = root.MaxMembers; MinAnchorDim = root.MinAnchorDim; AnchorReport = root.AnchorReport; MaxAnchorWalk = root.MaxAnchorWalk;
        _surf = root._surf; _spaceHash = root._spaceHash; _bySpace = root._bySpace; _rackItem = root._rackItem; _trigItems = root._trigItems;
        _stateArms = new EnableArms(C.Index) { State = arm };
    }

    float Eps => C.Epsilon;
    static float MaxDim(Aabb b) => MathF.Max(b.Max.X - b.Min.X, MathF.Max(b.Max.Y - b.Min.Y, b.Max.Z - b.Min.Z));
    bool IsBethesdaOrUssep(int plugin) => plugin >= 0 && _isFloorPlugin(plugin);

    // ---- placement in an arm (mover < 0: the winner) ----------------------------------------------------------------------

    public readonly record struct Placed(Snap Snap, int Index, FormKey Base);

    public Placed? Place(FormKey key, int mover)
    {
        var snaps = C.Index.History.Of(key);
        if (snaps is null || snaps.Count == 0) return null;
        if (mover < 0)
        {
            var w = snaps[^1];
            if (w.Deleted || C.Index.IsDeletedNow(key)) return null;
            var en = _stateArms?.Winner(key) ?? C.WinnerEnable(key);
            if (!en.Resolved || !en.Enabled) return null;
            var wb = C.WinnerBase(key);
            return wb.IsNull ? null : new Placed(w, snaps.Count - 1, wb);
        }
        int i = ContactIndex.AuthoringSnapIndex(snaps, mover, _closure);
        if (i < 0) return null;
        var s = snaps[i];
        if (s.Deleted) return null;
        int del = C.Index.Deletions.WinningDeleter(key);
        if (del >= 0 && del > s.Plugin && del != mover && !_closure[del].Contains(mover)) return null;
        var aen = _stateArms?.Without(key, mover, _closure) ?? C.ArmEnable(key, mover, _closure);
        if (!aen.Resolved || !aen.Enabled) return null;
        var b = C.ArmBase(key, mover, _closure);
        if (b.IsNull) b = C.BaseAt(key, s.Plugin);
        return b.IsNull ? null : new Placed(s, i, b);
    }

    bool Physical(FormKey b) => !C.IsMarker(b) && C.IsPhysicalType(b) && C.Summary(b) is { HasGeometry: true };

    (TriangleSoup Soup, TriangleGrid Grid, char Src)? Surface(FormKey key, Placed p)
    {
        var k = (key, p.Index, p.Base);
        if (_surf.TryGetValue(k, out var hit)) return hit.Value;
        if (_surf.Count >= SurfCacheMax) _surf.Clear();
        return _surf.GetOrAdd(k, _ => new Lazy<(TriangleSoup Soup, TriangleGrid Grid, char Src)?>(() => BuildSurface(p))).Value;
    }

    (TriangleSoup Soup, TriangleGrid Grid, char Src)? BuildSurface(Placed p)
    {
        (TriangleSoup, TriangleGrid, char)? r = null;
        if (C.GeomOf(p.Base) is { HasGeometry: true } g)
        {
            var (soup, src) = g.HostSoup.TriangleCount > 0 ? (g.HostSoup, g.HostSrc) : (g.DepSoup, g.DepSrc);
            if (soup.TriangleCount > 0)
            {
                var w = SpatialOverlap.TransformSoup(soup, p.Snap.Pos, p.Snap.Rot, p.Snap.Scale);
                r = (w, TriangleGrid.Build(w, 32f), src);
                SurfacesBuilt++;
            }
        }
        return r;
    }

    /// <summary>Whether a and b touch within ε, and whether a rests on b (b's face under a points up within the support band).</summary>
    (bool Touch, bool AOnB) Touch(FormKey a, Placed pa, FormKey b, Placed pb)
    {
        var sa = Surface(a, pa); var sb = Surface(b, pb);
        if (sa is null || sb is null) return (false, false);
        TouchTests++;
        var c = SpatialOverlap.Contact(sa.Value.Soup, sb.Value.Grid, Eps + 1f, Eps);
        bool touch = c.Distance <= Eps;
        bool on = touch && c.Normal.LengthSquared() > 1e-12f && Vector3.Normalize(c.Normal).Z >= C.SupportCos;
        return (touch, on);
    }

    /// <summary>Every physical reference at every undeleted snap, hashed by that snap's world box, built per space (an interior cell or a
    /// worldspace) only when the search first looks there, so a run reads the geometry of the spaces it judges and no other.</summary>
    BoxHash<(FormKey Key, int Snap)> In(FormKey space)
    {
        BuildBySpace();
        return _spaceHash.GetOrAdd(space, s => new Lazy<BoxHash<(FormKey, int)>>(() => BuildSpaceHash(s))).Value;
    }

    void BuildBySpace()
    {
        if (_bySpace is not null) return;
        _bySpace = new();
        foreach (var (key, snaps) in C.Index.History.Refs)
            for (int i = 0; i < snaps.Count; i++)
            {
                if (snaps[i].Deleted) continue;
                if (!_bySpace.TryGetValue(snaps[i].Space, out var l)) _bySpace[snaps[i].Space] = l = new();
                l.Add((key, i));
            }
    }

    BoxHash<(FormKey, int)> BuildSpaceHash(FormKey space)
    {
        var h = new BoxHash<(FormKey, int)>();
        if (_bySpace.TryGetValue(space, out var refs))
            foreach (var (key, i) in refs)
            {
                var snaps = C.Index.History.Of(key)!;
                var s = snaps[i];
                foreach (var b in new[] { C.BaseAt(key, s.Plugin), i == snaps.Count - 1 ? C.WinnerBase(key) : FormKey.Null }.Distinct())
                {
                    if (b.IsNull || C.IsMarker(b) || !C.IsPhysicalType(b)) continue;
                    var box = C.WorldBox(b, s);
                    if (box.IsEmpty || MaxDim(box) > C.MaxHostDim) continue;
                    h.Add(s.Space, box, (key, i));
                }
            }
        return h;
    }
    Dictionary<FormKey, List<(FormKey, int)>>? _bySpace;
    readonly System.Collections.Concurrent.ConcurrentDictionary<FormKey, Lazy<BoxHash<(FormKey, int)>>> _spaceHash = new();

    IEnumerable<(FormKey Key, Placed P)> Near(FormKey self, Placed p, int mover, float reach)
    {
        var box = C.WorldBox(p.Base, p.Snap);
        if (box.IsEmpty) yield break;
        var seen = new HashSet<FormKey>();
        foreach (var ((n, ni), _, _) in In(p.Snap.Space).Near(p.Snap.Space, box, reach))
        {
            if (n == self || seen.Contains(n)) continue;
            if (Place(n, mover) is not { } pn || pn.Index != ni) continue;           // another snap of n: this arm places it elsewhere
            seen.Add(n);
            if (!Physical(pn.Base)) continue;
            yield return (n, pn);
        }
    }

    // ---- anchors ---------------------------------------------------------------------------------------------------------

    public bool IsAnchor(FormKey n, Placed pn, FormKey fixture, float minDim)
    {
        string type = C.Index.BaseInfo.TryGetValue(pn.Base, out var bi) ? bi.Type : "?";
        if (type != "Static" && !(type == "MoveableStatic" && C.MotionOf(pn.Base) == DependentMotion.Fixed)) return false;
        if (C.IsMarker(pn.Base) || C.Summary(pn.Base) is not { CanHost: true }) return false;
        if (MaxDim(C.WorldBox(pn.Base, pn.Snap)) < minDim) return false;
        // the fixture's own plugin's references are part of what it brought, never the room it stands in
        return n.ModKey != fixture.ModKey || _isBethesdaFile(n.ModKey.FileName);
    }

    /// <summary>The mover changed this anchor: it is absent or placed elsewhere under the winner than in the order without the mover's side,
    /// AND the mover is the plugin whose step decides its state under the winner (its last placement step, its winning deletion, or the
    /// latest step on its enable chain; MovedHosts' rule). The arm without a master also drops every plugin that masters it, so the
    /// difference alone would blame a master (USSEP) for what a downstream mod did.</summary>
    // ---- is a floor or wall itself still the room? (Q-B2) ---------------------------------------------------------------

    public enum AnchorSoundness { Sound, Unsound, Undecided }
    readonly Dictionary<(int Mover, float MinDim, ModKey Side, FormKey Anchor), AnchorSoundness> _soundMemo = new();

    /// <summary>SUSPECT for mover D: in the order without D's side the anchor touches an anchor D changed. An anchor D's side brought in
    /// (absent without it) is never suspect.</summary>
    bool Suspect(FormKey a, int mover, FormKey fixture, float minDim)
    {
        if (Place(a, mover) is not { } pa) return false;
        foreach (var (n, pn) in Near(a, pa, mover, Eps + 0.5f))
            if (IsAnchor(n, pn, fixture, minDim) && Changed(n, mover) && Touch(a, pa, n, pn).Touch) return true;
        return false;
    }

    /// <summary>The same grounding rule applied to a floor or wall: an anchor grounds when it is not suspect, or when its anchor
    /// component under the winner (anchor-to-anchor touches) reaches an anchor that is not; Unsound when the component holds suspect
    /// pieces only; Undecided past <see cref="MaxAnchorWalk"/> suspect pieces. One answer for the whole component it walked.</summary>
    public AnchorSoundness Soundness(FormKey a, FormKey fixture, int mover, float minDim)
    {
        if (mover < 0) return AnchorSoundness.Sound;                         // no mover named: the anchor is the room
        var key = (mover, minDim, fixture.ModKey, a);
        if (_soundMemo.TryGetValue(key, out var hit)) return hit;
        if (!Suspect(a, mover, fixture, minDim)) return _soundMemo[key] = AnchorSoundness.Sound;
        var seen = new HashSet<FormKey> { a };
        var queue = new Queue<FormKey>(); queue.Enqueue(a);
        AnchorSoundness result = AnchorSoundness.Unsound;
        if (seen.Count > MaxAnchorWalk) result = AnchorSoundness.Undecided;
        else
            while (queue.Count > 0 && result == AnchorSoundness.Unsound)
            {
                var x = queue.Dequeue();
                if (Place(x, -1) is not { } px) continue;
                foreach (var (n, pn) in Near(x, px, -1, Eps + 0.5f))
                {
                    if (seen.Contains(n) || !IsAnchor(n, pn, fixture, minDim) || !Touch(x, px, n, pn).Touch) continue;
                    if (!Suspect(n, mover, fixture, minDim)) { _soundMemo[(mover, minDim, fixture.ModKey, n)] = AnchorSoundness.Sound; result = AnchorSoundness.Sound; break; }
                    seen.Add(n);
                    if (seen.Count > MaxAnchorWalk) { result = AnchorSoundness.Undecided; break; }
                    queue.Enqueue(n);
                }
            }
        foreach (var s in seen) _soundMemo[(mover, minDim, fixture.ModKey, s)] = result;
        return result;
    }

    bool Changed(FormKey a, int mover)
    {
        if (Place(a, mover) is not { } pa) return false;
        bool differs = Place(a, -1) is not { } pw || pw.Snap.Space != pa.Snap.Space || PlacementHistory.Dist(pa.Snap.Pos, pw.Snap.Pos) > C.MoveThreshold;
        return differs && Responsible(a) == mover;
    }

    /// <summary>The fixture's side: the plugin whose step last set its winning placement, its base, or its enable state (a mod that only
    /// re-enables a base-game reference another mod disabled makes it that mod's).</summary>
    int SideOf(FormKey f, int placement)
    {
        int r = placement;
        if (C.Index.BaseChanges.TryGetValue(f, out var bc) && bc.Count > 0 && bc[^1].Plugin > r) r = bc[^1].Plugin;
        if (C.Index.EnableChanges.TryGetValue(f, out var ec) && ec.Count > 0 && ec[^1].Plugin > r) r = ec[^1].Plugin;
        return r;
    }

    int Responsible(FormKey a)
    {
        var snaps = C.Index.History.Of(a);
        int r = snaps is { Count: >= 2 } ? snaps[^1].Plugin : -1;
        int del = C.Index.Deletions.WinningDeleter(a); if (del > r) r = del;
        int cm = C.ChainMover(a); if (cm > r) r = cm;
        return r;
    }

    // ---- weapon racks ----------------------------------------------------------------------------------------------------

    void BuildRacks()
    {
        if (_rackItem is not null) return;
        _rackItem = new(); _trigItems = new();
        var idx = C.Index;
        foreach (var (k, links) in idx.RefLinkedRefs)
        {
            if (!idx.RefBase.TryGetValue(k, out var ab) || !idx.RackActivators.TryGetValue(ab, out var info)) continue;
            var trig = info.Objects.TryGetValue("WRackTrigger", out var wk) ? links.FirstOrDefault(l => l.Keyword == wk).Reference : FormKey.Null;
            foreach (var l in links)
                if (l.Keyword is null)
                {
                    _rackItem[l.Reference] = (k, trig);
                    if (!trig.IsNull) { if (!_trigItems.TryGetValue(trig, out var li)) _trigItems[trig] = li = new(); li.Add(l.Reference); }
                }
        }
    }

    /// <summary>The other references of the weapon rack(s) a reference belongs to (activator, trigger, starting items): the walk
    /// joins them through <see cref="MountPartners"/>, wherever each is filed. Empty for a reference on no rack.</summary>
    public IEnumerable<FormKey> RackPartners(FormKey k)
    {
        BuildRacks();
        if (_rackGroups is null)
        {
            _rackGroups = new();
            void Link(FormKey a, FormKey b)
            {
                if (a.IsNull || b.IsNull || a == b) return;
                if (!_rackGroups.TryGetValue(a, out var la)) _rackGroups[a] = la = new();
                if (!_rackGroups.TryGetValue(b, out var lb)) _rackGroups[b] = lb = new();
                la.Add(b); lb.Add(a);
            }
            foreach (var (item, (act, trig)) in _rackItem!) { Link(item, act); Link(item, trig); Link(act, trig); }
        }
        return _rackGroups.TryGetValue(k, out var l) ? l : Enumerable.Empty<FormKey>();
    }
    Dictionary<FormKey, HashSet<FormKey>>? _rackGroups;

    /// <summary>The rest rule's rack reading for an item in an arm (null when it is no rack's starting item).</summary>
    public RackMountReading? MountOf(FormKey item, int mover)
    {
        BuildRacks();
        if (!_rackItem!.TryGetValue(item, out var chain)) return null;
        var idx = C.Index;
        RackMountReading Refused(string why) => new("refused", $"{RackMount.RefusedReason} ({why})", null, null, chain.Trig.IsNull ? null : chain.Trig.ToString());
        if (!idx.RefBase.TryGetValue(chain.Act, out var ab) || !idx.RackActivators.TryGetValue(ab, out var info)) return null;
        if (idx.RefScripts.TryGetValue(chain.Act, out var own) && own.Any(x => string.Equals(x, RackMount.ActivatorScript, StringComparison.OrdinalIgnoreCase)))
            return Refused($"the activator reference {chain.Act} carries its own script properties, which are not read");
        bool actOn = Place(chain.Act, mover) is not null;
        var tp = chain.Trig.IsNull ? null : Place(chain.Trig, mover);
        bool trigPresent = !chain.Trig.IsNull && idx.History.Of(chain.Trig) is { Count: > 0 } && tp is not null;
        var ip = Place(item, mover);
        var names = info.Objects.GroupBy(kv => kv.Value).ToDictionary(g => g.Key, g => g.Select(kv => kv.Key).ToList());
        var kws = new HashSet<string>(StringComparer.Ordinal);
        FormKey ib = ip?.Base ?? (idx.RefBase.TryGetValue(item, out var rb) ? rb : FormKey.Null);
        if (idx.BaseKeywords.TryGetValue(ib, out var fks))
            foreach (var fk in fks) if (names.TryGetValue(fk, out var ns)) foreach (var n in ns) kws.Add(n);
        string? itype = idx.BaseInfo.TryGetValue(ib, out var bi2) ? bi2.Type : null;
        string kind = itype == "Weapon" ? "weapon" : itype == "Armor" && kws.Contains("ArmorShield") ? "shield" : "other";
        bool sameCell = ip is { } ipp && Place(chain.Act, mover) is { } app && ipp.Snap.Cell == app.Snap.Cell;
        var facts = new RackFacts(info.Script, info.RackType ?? 1, info.CoaPatch ?? false, actOn, trigPresent, tp is not null, ip is not null, sameCell, kind, kws);
        try
        {
            var rd = RackMount.Decide(facts, node => tp is not { } t ? null
                : _triggerHasNode(t.Base, node) switch
                {
                    true => new RackPose(new Vec3(0, 0, 0), new Vec3(0, 0, 0)),
                    false => null,
                    null => throw new InvalidOperationException("the trigger's model could not be read"),
                });
            return rd with { Trigger = chain.Trig.ToString() };
        }
        catch (InvalidOperationException ex) { return Refused(ex.Message); }
    }

    IEnumerable<FormKey> MountPartners(FormKey m, int mover)
    {
        BuildRacks();
        if (_rackItem!.TryGetValue(m, out var ch) && !ch.Trig.IsNull && MountOf(m, mover) is { Status: "mounted" }) yield return ch.Trig;
        if (_trigItems!.TryGetValue(m, out var items))
            foreach (var it in items) if (MountOf(it, mover) is { Status: "mounted" }) yield return it;
    }

    // ---- the walk --------------------------------------------------------------------------------------------------------

    /// <summary>One arm's assembly walk from <paramref name="fixture"/>. <paramref name="stopAtAnchor"/>: stop at the first anchor (the
    /// winner arm's question); otherwise walk on until an anchor <paramref name="mover"/>'s side changed is met, or the walk ends.</summary>
    /// <param name="soundFor">the winner arm's walk (mover &lt; 0) reads each anchor's soundness against this mover: an unsound anchor is
    /// walked through as a member, an undecided one is noted (Q-B2). Ignored in the order without a mover.</param>
    public EmptySpaceArmReading Walk(FormKey fixture, int mover, bool stopAtAnchor, float minAnchorDim, int soundFor = -1)
    {
        if (Place(fixture, mover) is null) return EmptySpaceArmReading.Missing;
        if (mover >= 0) soundFor = -1;
        // Grounded, over-cap and "touches an anchor the mover changed" are properties of the connected assembly, not of where the walk
        // starts (touch and the mount link are symmetric; the anchor test reads only the fixture's own plugin), so one walk answers
        // for every member it visited under the same side, arm, anchor size and stopping rule.
        var ck = (mover, minAnchorDim, fixture.ModKey, stopAtAnchor, soundFor, fixture);
        if (_walkMemo.TryGetValue(ck, out var hit)) { WalksReused++; return hit; }
        WalksRun++;
        var members = new List<FormKey> { fixture };
        var inSet = new HashSet<FormKey> { fixture };
        var anchors = new List<(FormKey, FormKey, float, char)>();
        var anchorSeen = new HashSet<FormKey>();
        var changed = new List<FormKey>();
        bool undecided = false; int unsound = 0;
        var queue = new Queue<FormKey>(); queue.Enqueue(fixture);
        while (queue.Count > 0)
        {
            var m = queue.Dequeue();
            if (Place(m, mover) is not { } pm) continue;
            foreach (var (n, pn) in Near(m, pm, mover, Eps + 0.5f))
            {
                if (inSet.Contains(n) || anchorSeen.Contains(n)) continue;
                if (!Touch(m, pm, n, pn).Touch) continue;
                if (IsAnchor(n, pn, fixture, minAnchorDim) && soundFor >= 0 && Soundness(n, fixture, soundFor, minAnchorDim) is var sd && sd != AnchorSoundness.Sound)
                {
                    // a floor or wall that is itself left by the mover: part of what floats, walked through; undecided: noted
                    if (sd == AnchorSoundness.Undecided) { undecided = true; anchorSeen.Add(n); continue; }
                    unsound++;
                    if (!Add(n)) return Reading(anchors.Count > 0, true);
                    continue;
                }
                if (IsAnchor(n, pn, fixture, minAnchorDim))
                {
                    anchorSeen.Add(n);
                    anchors.Add((n, pn.Base, MaxDim(C.WorldBox(pn.Base, pn.Snap)), Surface(n, pn)?.Src ?? '-'));
                    if (stopAtAnchor) return Reading(true, false);
                    if (mover >= 0 && Changed(n, mover)) { changed.Add(n); return Reading(true, false); }
                    continue;
                }
                if (!Add(n)) return Reading(anchors.Count > 0, true);
            }
            foreach (var p in MountPartners(m, mover))
                if (!inSet.Contains(p) && Place(p, mover) is not null && !Add(p)) return Reading(anchors.Count > 0, true);
        }
        return Reading(anchors.Count > 0, false);

        bool Add(FormKey n)
        {
            inSet.Add(n); members.Add(n);
            if (members.Count > MaxMembers) return false;
            queue.Enqueue(n); return true;
        }
        EmptySpaceArmReading Reading(bool grounded, bool over)
        {
            var r = new EmptySpaceArmReading(true, grounded, over, members.Count, members.Take(8).ToList(), anchors, changed, undecided && !grounded, unsound);
            foreach (var m in inSet) _walkMemo[(mover, minAnchorDim, fixture.ModKey, stopAtAnchor, soundFor, m)] = r;
            return r;
        }
    }

    /// <summary>Why the fixture stays where the winner places it, or null when it is a loose body that falls. Also returns the Fixed
    /// references of its own plugin it rests on (condition 2).</summary>
    public (string? HeldBy, List<FormKey> OwnSupport) Held(FormKey fixture, Placed pf)
    {
        var own = new List<FormKey>();
        string? held = null;
        var motion = C.MotionOf(pf.Base);
        if (motion == DependentMotion.Fixed) held = "fixed";
        else if (MountOf(fixture, -1) is { Status: "mounted" } rd) held = $"mounted on {rd.Trigger}";
        foreach (var (n, pn) in Near(fixture, pf, -1, Eps + 0.5f))
        {
            var (touch, on) = Touch(fixture, pf, n, pn);
            if (!touch || !on || C.MotionOf(pn.Base) != DependentMotion.Fixed) continue;
            held ??= $"rests on {n}";
            string type = C.Index.BaseInfo.TryGetValue(pn.Base, out var bi) ? bi.Type : "?";
            if (n.ModKey == fixture.ModKey && !_isBethesdaFile(n.ModKey.FileName) && type is "Static" or "MoveableStatic") own.Add(n);
        }
        return (held, own);
    }

    float NearestAnchor(EmptySpaceArmReading w, FormKey fixture)
    {
        float best = float.PositiveInfinity;
        foreach (var m in w.FirstMembers)                                       // the first eight members: a diagnostic, stated
        {
            if (Place(m, -1) is not { } pm) continue;
            var mbox = C.WorldBox(pm.Base, pm.Snap);
            foreach (var (n, pn) in Near(m, pm, -1, AnchorReport))
            {
                if (!IsAnchor(n, pn, fixture, MinAnchorDim)) continue;
                float d = SpatialOverlap.BoxDistance(mbox, C.WorldBox(pn.Base, pn.Snap));
                if (d < best) best = d;
            }
        }
        return best;
    }

    // ---- candidates and movers -------------------------------------------------------------------------------------------

    /// <summary>A dependent-scale physical reference a mover could have left: its base, at some snap, is dependent-typed and no more
    /// than STRANDED's dependent size.</summary>
    public bool IsCandidate(FormKey key, Placed p)
        => C.IsDependentType(p.Base) && !C.IsMarker(p.Base) && MaxDim(C.WorldBox(p.Base, p.Snap)) is var d && d > 0 && d <= C.MaxDependentDim;

    /// <summary>Every (fixture, mover) pair the moved hosts suggest: a candidate with any undeleted snap within reach of a host's
    /// previous box, paired with that host's mover.</summary>
    public IEnumerable<(FormKey Fixture, int Mover)> CandidatesNear(IEnumerable<MovedHost> hosts)
    {
        var seen = new HashSet<(FormKey, int)>();
        foreach (var h in hosts)
        {
            if (!h.Previous.Interior) { ExteriorHostsSkipped++; continue; }
            var hbox = C.WorldBox(h.PrevBase, h.Previous);
            if (hbox.IsEmpty) continue;
            foreach (var ((k, si), _, _) in In(h.Previous.Space).Near(h.Previous.Space, hbox, Reach))
            {
                if (k == h.Key || seen.Contains((k, h.Mover))) continue;
                var snaps = C.Index.History.Of(k)!;
                var s = snaps[si];
                var b = C.BaseAt(k, s.Plugin);
                if (!IsCandidate(k, new Placed(s, si, b))) continue;
                seen.Add((k, h.Mover));
                yield return (k, h.Mover);
            }
        }
    }

    /// <summary>The movers worth asking about for one fixture (stateprobe's per-state path, where no host census is run): every
    /// plugin that stepped on an anchor-typed reference within reach of any of the fixture's snaps after it was first placed — a later
    /// placement, a deletion, or a step on its enable chain.</summary>
    public IEnumerable<int> MoversNear(FormKey fixture)
    {
        var snaps = C.Index.History.Of(fixture);
        if (snaps is null) yield break;
        var movers = new SortedSet<int>();
        var seen = new HashSet<FormKey>();
        for (int i = 0; i < snaps.Count; i++)
        {
            var s = snaps[i];
            if (s.Deleted) continue;
            var box = C.WorldBox(C.BaseAt(fixture, s.Plugin), s);
            if (box.IsEmpty) continue;
            foreach (var ((a, _), _, _) in In(s.Space).Near(s.Space, box, Reach))
            {
                if (a == fixture || !seen.Add(a)) continue;
                var hs = C.Index.History.Of(a);
                if (hs is null || hs.Count == 0) continue;
                for (int j = 1; j < hs.Count; j++) movers.Add(hs[j].Plugin);
                int del = C.Index.Deletions.WinningDeleter(a); if (del >= 0) movers.Add(del);
                int cm = C.ChainMover(a); if (cm >= 0) movers.Add(cm);
            }
        }
        foreach (var m in movers) yield return m;
    }

    // ---- one judgement ---------------------------------------------------------------------------------------------------

    readonly Dictionary<(FormKey, int), (EmptySpaceArmReading W, string? Held, List<FormKey> Own, DependentMotion Motion)> _winMemo = new();

    /// <summary>Forget the winner readings (after a state change).</summary>
    public void ClearMemo() { _winMemo.Clear(); _walkMemo.Clear(); _soundMemo.Clear(); }
    readonly Dictionary<(int Mover, float MinDim, ModKey Side, bool Stop, int SoundFor, FormKey Member), EmptySpaceArmReading> _walkMemo = new();

    public EmptySpaceRow Judge(FormKey fixture, int mover, string? state = null, bool sensitivity = true)
    {
        var pw = Place(fixture, -1);
        var pa = Place(fixture, mover);
        var any = pw ?? pa;
        bool interior = any?.Snap.Interior ?? true;
        FormKey baseKey = any?.Base ?? FormKey.Null;
        var motion = any is { } a0 ? C.MotionOf(a0.Base) : DependentMotion.Unknown;
        int placer = pw is null ? -1 : SideOf(fixture, pw.Value.Snap.Plugin);
        FormKey cell = any?.Snap.Cell ?? FormKey.Null;

        EmptySpaceArmReading win = EmptySpaceArmReading.Missing, auth = EmptySpaceArmReading.Missing;
        string? held = null; var own = new List<FormKey>();
        if (interior && pw is { } pwv && pa is not null)
        {
            if (!_winMemo.TryGetValue((fixture, mover), out var wm))
            {
                var (h, o) = Held(fixture, pwv);
                var w = h is null ? new EmptySpaceArmReading(true, false, false, 1, new[] { fixture }, Array.Empty<(FormKey, FormKey, float, char)>(), Array.Empty<FormKey>())
                                  : Walk(fixture, -1, stopAtAnchor: true, MinAnchorDim, soundFor: mover);
                _winMemo[(fixture, mover)] = wm = (w, h, o, motion);
            }
            win = wm.W; held = wm.Held; own = wm.Own;
        }
        bool replaced = pw is { } pr && (pr.Snap.Plugin == mover || (pr.Snap.Plugin < _closure.Count && _closure[pr.Snap.Plugin].Contains(mover)));
        // the authoring walk is the costly one; a fixture grounded under the winner and not re-placed by the mover's side cannot be the
        // class whatever it finds, so it is not walked (DetectorModel.EmptySpace reads that as Grounded)
        bool readAuth = held is not null && pa is not null && !(win.Grounded && !win.OverCap && !replaced);
        if (readAuth) auth = Walk(fixture, mover, stopAtAnchor: false, MinAnchorDim);
        var facts = new DetectorModel.EmptySpaceFacts(interior, pw is not null, pa is not null, motion, held is not null,
            win.OverCap, auth.OverCap, win.Grounded, auth.Grounded, auth.ChangedAnchors.Count > 0,
            replaced, IsBethesdaOrUssep(mover) && IsBethesdaOrUssep(placer), MaxMembers, AuthoringRead: readAuth || held is null,
            FixtureSideIsBase: !IsBethesdaOrUssep(mover) && IsBethesdaOrUssep(placer), WinnerAnchorUndecided: win.AnchorUndecided, MaxAnchorWalk: MaxAnchorWalk);
        var (v, why) = DetectorModel.EmptySpace(facts);
        float nearest = float.PositiveInfinity; bool? g128 = null, g256 = null, g32 = null;
        if (v is EmptySpaceVerdict.LeftInEmptySpace or EmptySpaceVerdict.RePlacedStillEmpty or EmptySpaceVerdict.FloorPopulation)
            nearest = NearestAnchor(win, fixture);
        if (sensitivity && v is EmptySpaceVerdict.LeftInEmptySpace or EmptySpaceVerdict.ReGrounded or EmptySpaceVerdict.RePlacedGrounded or EmptySpaceVerdict.RePlacedStillEmpty)
        {
            g128 = Walk(fixture, -1, true, 128f, mover).Grounded;
            g256 = Walk(fixture, -1, true, 256f, mover).Grounded;
            g32 = Walk(fixture, -1, true, 32f, mover).Grounded;     // the informative direction for a class row (Q-B3)
        }
        return new EmptySpaceRow(fixture, baseKey, mover, placer, cell, interior, state, motion, held ?? "falls", own,
            win, auth, nearest, g128, g256, facts, v, why, g32);
    }

    /// <summary>One world state's pass (stateprobe --empty-space): the state is set on the ContactIndex arms, every interior candidate
    /// that ARRIVES in it is judged against every mover near it, and the state is cleared again. Every judged pair is returned, each
    /// with the switch whose set brought the fixture in; the caller counts and filters.</summary>
    public List<(EmptySpaceRow Row, FormKey Switch)> JudgeArrivals(StateArm arm, SwitchIndex sx) => JudgeArrivals(arm, sx, null, out _, out _);

    /// <summary><see cref="JudgeArrivals(StateArm, SwitchIndex)"/> within a population (<paramref name="scope"/>; null: all): an
    /// arrival is judged when <see cref="EmptySpaceScope.MustJudge"/> and returned when <see cref="EmptySpaceScope.InScope"/>.</summary>
    public List<(EmptySpaceRow Row, FormKey Switch)> JudgeArrivals(StateArm arm, SwitchIndex sx, EmptySpaceScope? scope, out int outside, out int judgedDropped)
    {
        outside = 0; judgedDropped = 0;
        var res = new List<(EmptySpaceRow, FormKey)>();
        var arrivals = arm.Changes(sx).Where(c => c.After).GroupBy(c => c.Key).Select(g => (g.Key, g.First().Root.Key)).ToList();
        if (arrivals.Count == 0) return res;
        var was = C.State;
        C.State = arm; ClearMemo();
        try { JudgeEach(arrivals, arm, scope, res, ref outside, ref judgedDropped); }
        finally { C.State = was; ClearMemo(); }
        return res;
    }

    void JudgeEach(List<(FormKey Key, FormKey Switch)> arrivals, StateArm arm, EmptySpaceScope? scope, List<(EmptySpaceRow, FormKey)> res, ref int outside, ref int judgedDropped)
    {
        foreach (var (f, sw) in arrivals)
        {
            if (Place(f, -1) is not { } pf || !IsCandidate(f, pf) || !pf.Snap.Interior) continue;
            if (scope is not null && !scope.MustJudge(f)) { outside++; continue; }
            bool keep = scope is null || scope.InScope(f);
            foreach (var m in MoversNear(f))
            {
                var r = Judge(f, m, arm.Name);
                if (keep) res.Add((r, sw)); else judgedDropped++;
            }
        }
    }

    /// <summary>Build what the per-state views share, before any of them runs. A forced reference or a state set on the index's arms has
    /// no equal on a view's own arms, so either refuses.</summary>
    public void PrepareStateViews()
    {
        if (C.State is not null || C.Arms.Forced.Count > 0) throw new InvalidOperationException("the empty-space pass cannot run per-state views over an index with a state set or references forced; use --threads 1");
        BuildRacks(); BuildBySpace();
    }

    /// <summary><see cref="JudgeArrivals(StateArm, SwitchIndex, EmptySpaceScope?, out int, out int)"/> on a view of its own, leaving the index's
    /// arms alone, so states can run side by side. Same rows: the shared pass clears its memos at each state anyway.</summary>
    public List<(EmptySpaceRow Row, FormKey Switch)> JudgeArrivalsAlone(StateArm arm, SwitchIndex sx, EmptySpaceScope? scope, out int outside, out int judgedDropped)
    {
        outside = 0; judgedDropped = 0;
        var res = new List<(EmptySpaceRow, FormKey)>();
        var arrivals = arm.Changes(sx).Where(c => c.After).GroupBy(c => c.Key).Select(g => (g.Key, g.First().Root.Key)).ToList();
        if (arrivals.Count == 0) return res;
        var view = new EmptySpaceSearch(this, arm);
        view.JudgeEach(arrivals, arm, scope, res, ref outside, ref judgedDropped);
        Interlocked.Add(ref TouchTests, view.TouchTests); Interlocked.Add(ref SurfacesBuilt, view.SurfacesBuilt);
        Interlocked.Add(ref WalksRun, view.WalksRun); Interlocked.Add(ref WalksReused, view.WalksReused);
        return res;
    }

    /// <summary>The physical half for an emit row under THIS order (contactprobe --base): Holds when the fixture is present, held and
    /// its assembly reaches no floor or wall; Gone when it is absent, falls or is grounded; Unmeasured when exterior or over the cap.</summary>
    public (PhysicalState State, string Detail) Predicate(FormKey fixture, int mover = -1)
    {
        if (Place(fixture, -1) is not { } pw) return (PhysicalState.Gone, "not present under this order");
        if (!pw.Snap.Interior) return (PhysicalState.Unmeasured, "exterior: the land under it is not read");
        var (h, _) = Held(fixture, pw);
        if (h is null) return (PhysicalState.Gone, "a loose body that falls at load under this order");
        var w = Walk(fixture, -1, true, MinAnchorDim, mover);
        if (w.OverCap) return (PhysicalState.Unmeasured, $"its assembly passes {MaxMembers} references: not read");
        if (w.AnchorUndecided) return (PhysicalState.Unmeasured, $"the floor or wall it reaches is itself among the mover's removals past {MaxAnchorWalk} pieces: not read");
        return w.Grounded ? (PhysicalState.Gone, $"its assembly reaches a floor or wall ({w.Anchors[0].Key})")
                          : (PhysicalState.Holds, $"its assembly of {w.Members} reaches no floor or wall");
    }
}

/// <summary>The empty-space pass inside a plan's population (window 2, task 1): <see cref="Cells"/> is the population's interior
/// cells closed over relocations and rack links (<see cref="PopulationScope.InteriorClosure"/>). A fixture with an interior placement
/// there is IN SCOPE: judged and reported. A fixture ever placed in an exterior is judged as well (its rows dropped unless in
/// scope), so the search's memos of exterior assemblies fill exactly as in a whole-order run. Every other fixture is not judged.
/// The search's memos are keyed by the references a walk visits, and a walk from a fixture in these cells visits only references
/// of these cells (or exterior ones), so every in-scope row is the one the whole-order run writes.</summary>
public sealed class EmptySpaceScope
{
    readonly OverlapIndex _idx;
    public IReadOnlySet<FormKey> Cells { get; }
    public EmptySpaceScope(OverlapIndex idx, IReadOnlySet<FormKey> cells) { _idx = idx; Cells = cells; }
    public bool InScope(FormKey f) => _idx.History.Of(f) is { } hs && hs.Any(s => s.Interior && Cells.Contains(s.Cell));
    public bool MustJudge(FormKey f) => _idx.History.Of(f) is { } hs && hs.Any(s => !s.Interior || Cells.Contains(s.Cell));
}

/// <summary>The output shapes both probes write for the empty-space pass: one JSON row per judged pair, and the emit's conflict for a
/// LEFT IN EMPTY SPACE row (kind EMPTY-SPACE; STATE-EMPTY from stateprobe). The fixture is the necessary record (a patch re-places,
/// re-grounds or disables it); the anchors the mover changed are the alternative.</summary>
public static class EmptySpaceOutput
{
    public const string Kind = "EMPTY-SPACE";
    public const string StateKind = "STATE-EMPTY";

    public static object Row(EmptySpaceRow r, Func<int, string> plugin, Func<FormKey, string> baseLabel, Func<FormKey, string> cellOf)
    {
        object Arm(EmptySpaceArmReading a) => new
        {
            a.Exists, a.Grounded, a.OverCap, a.AnchorUndecided, a.UnsoundAnchors, a.Members, FirstMembers = a.FirstMembers.Select(k => k.ToString()).ToList(),
            Anchors = a.Anchors.Take(4).Select(x => new { Key = x.Key.ToString(), Base = baseLabel(x.Base), x.Dim, Src = x.Src.ToString() }).ToList(),
            ChangedAnchors = a.ChangedAnchors.Select(k => k.ToString()).ToList(),
        };
        return new
        {
            Fixture = r.Fixture.ToString(), Base = baseLabel(r.Base), Side = r.Fixture.ModKey.FileName.String, Placer = plugin(r.Placer),
            Mover = plugin(r.Mover), Cell = cellOf(r.Cell), CellKey = r.Cell.ToString(), r.Interior, r.State,
            Motion = r.Motion.ToString(), r.HeldBy, OwnSupport = r.OwnSupport.Select(k => k.ToString()).ToList(),
            Verdict = r.Verdict.ToString(), r.Why,
            Winner = Arm(r.Winner), Authoring = Arm(r.Authoring),
            NearestAnchor = float.IsPositiveInfinity(r.NearestAnchor) ? (float?)null : r.NearestAnchor,
            r.GroundedAt128, r.GroundedAt256, r.GroundedAt32,
        };
    }

    public static Conflict ToConflict(EmptySpaceRow r, Func<int, string> plugin, Func<FormKey, string> baseLabel, Func<FormKey, string> cellOf, bool state)
    {
        string near = float.IsPositiveInfinity(r.NearestAnchor) ? "no floor or wall within 512u" : $"nearest floor or wall {r.NearestAnchor:0}u";
        string summary = $"{r.Verdict}: {baseLabel(r.Base)} ({r.HeldBy}) in {cellOf(r.Cell)}; {plugin(r.Mover)} removed or moved "
                       + $"{string.Join(", ", r.Authoring.ChangedAnchors.Take(3))}; its assembly of {r.Winner.Members} touches no floor or wall now ({near})"
                       + (r.State is null ? "" : $" @ {r.State}") + $"  {r.Fixture}";
        return new Conflict(state ? StateKind : Kind, plugin(r.Placer), plugin(r.Mover), summary,
            new List<ConflictRecord> { new(r.Fixture.ToString(), "placed-ref", cellOf(r.Cell), PhysicalState.Holds, $"its assembly of {r.Winner.Members} reaches no floor or wall") },
            r.Authoring.ChangedAnchors.Select(k => new ConflictRecord(k.ToString(), "placed-ref", null)).ToList());
    }

    /// <summary>Whether a weapon-rack trigger base's model has a named node, read through the probe's asset view (memoised per base).</summary>
    public static Func<FormKey, string, bool?> NodeCheck(OverlapIndex idx, AssetResolver assets)
    {
        var memo = new Dictionary<FormKey, HashSet<string>?>();
        return (b, node) =>
        {
            if (!memo.TryGetValue(b, out var names))
            {
                names = null;
                if (idx.BaseMesh.TryGetValue(b, out var rel))
                {
                    var full = rel.StartsWith("meshes", StringComparison.OrdinalIgnoreCase) ? rel : Path.Combine("meshes", rel);
                    var res = assets.ResolveForPlacement(full);
                    if (res.Sources.Count > 0 && AssetResolver.ReadPlacementSource(res.Sources[0]).Bytes is { } bytes)
                    {
                        var f = new NiflySharp.NifFile();
                        using var ms = new MemoryStream(bytes, writable: false);
                        if (f.Load(ms) == 0)
                            names = f.Blocks.OfType<NiflySharp.Blocks.NiAVObject>().Select(x => x.Name?.String ?? "").ToHashSet(StringComparer.OrdinalIgnoreCase);
                    }
                }
                memo[b] = names;
            }
            return names?.Contains(node);
        };
    }
}

/// <summary>--population-audit EMIT OUT (window 2, task 1): for every row of another emit (a whole-order run's), whether this run's
/// population rule keeps it and which part of the rule does (null: dropped). The necessary record decides (the fixture, or a state
/// row's member); <c>rule</c> gets the row's kind and that record.</summary>
public static class EmptySpaceAudit
{
    public static void Write(string emit, string outPath, string probe, string population, Func<string, FormKey, string?> rule)
    {
        var rows = new List<object>();
        foreach (var c in ConflictIo.Read(emit).Conflicts)
        {
            string? other = c.Alternative is { Count: > 0 } alt ? alt[0].FormKey : null;
            foreach (var rec in c.Necessary)
            {
                var why = rule(c.Kind, FormKey.Factory(rec.FormKey));
                rows.Add(new { identity = ConflictIo.RowIdentity(c.Kind, rec.FormKey, other, c.A), kind = c.Kind, key = rec.FormKey, inScope = why is not null, rule = why });
            }
        }
        var dir = Path.GetDirectoryName(Path.GetFullPath(outPath));
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        File.WriteAllText(outPath, System.Text.Json.JsonSerializer.Serialize(new { probe, population, emit = Path.GetFullPath(emit), rows },
            new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
        Console.WriteLine($"population audit: {rows.Count:N0} row(s) of {Path.GetFileName(emit)} -> {outPath}");
    }
}
