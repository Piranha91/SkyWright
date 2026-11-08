using System.Numerics;
using HousecarlCore;
using Mutagen.Bethesda.Plugins;

namespace HarnessShared;

// THE STATE CLASS — StateConditional / StateResolved rows across a switch flip (roadmap 4.11, handoff
// 2026-09-20 Part C.2), computed from the switch's OWN sets rather than by re-running the world.
//
// A row not involving a reference whose existence the flip changes is identical in both arms, so a
// state's class is the differential over the references that ARRIVE (disabled at baseline, enabled in
// the state) and DEPART (the reverse):
//
//   OVERLAP  is withheld until this evaluator has the strict placed render surfaces used by the current
//            overlap predicate. Its former world-box rule remains reachable only by the synthetic historical
//            proof fixture; production must never publish those box decisions as STATE-OVERLAP findings.
//   CONTACT  a HOST that departs (physical, furniture-scale or larger, solid collision) strands what
//            rested on or hung from it, unless something else in the state holds it — the contact rule
//            with the switch as the mover (the R1 shape, a plugin's disable replaced by a flip). A host
//            that arrives sinks what it arrives inside of. A DEPENDENT that arrives inside something is
//            Sunk; one that arrives touching nothing is "floating on arrival" — a row only when the
//            nearest thing under it was moved or removed by a later plugin (the Stranded shape),
//            otherwise the absolute floor's business, tallied.
//   ROUTE    an arriving obstacle or navcut box that the route-cut rule reads BLOCKED or LIKELY on the
//            cell's winning navmesh is a StateConditional row; a departing one is StateResolved. The
//            measurement is the probe's (it holds the navmesh and the meshes) through RouteAsk; the
//            proof passes a stand-in.
//
// Decisions are DetectorModel.StateDifferential and DetectorModel.PlacementContact; the enable
// state is EnableState under StateArm.Wrap; everything here is the walk, the hashes and the facts.
// Shared between stateprobe (the real order) and proofprobe (a synthetic order with OBND boxes).

public enum StateRowKind { Overlap, Contact, Route }

/// <summary>One row of the class. <paramref name="Member"/> is the reference the flip changed (arriving when
/// <paramref name="Arrives"/>); <paramref name="Other"/> the reference on the other side (null for a route row).
/// <paramref name="MemberDepends"/>: the member is the dependent of the measurement (the dependent pass's Sunk row).</summary>
public sealed record StateRow(
    string State, FormKey Switch, int SwitchOwner,
    StateRowKind Kind, StateVerdict Verdict, string Why,
    FormKey Member, bool Arrives, int MemberPlacer, int MemberAttacher, FormKey MemberBase,
    FormKey? Other, int OtherPlacer, FormKey OtherBase,
    FormKey Cell, FormKey Space, bool Interior,
    StatePopulation Population, bool SameSet,
    float Depth = float.NaN, float Frac = float.NaN,
    bool SameAuthor = false, int MemberResponsible = -1, int OtherResponsible = -1,
    ContactVerdict? ContactVerdict = null, ContactRelation Relation = ContactRelation.None,
    float Distance = float.NaN, float Penetration = float.NaN, float RawDistance = float.NaN, DependentMotion Motion = DependentMotion.Unknown,
    RouteCutVerdict? Route = null, bool Navcut = false,
    PhysicalState Physical = PhysicalState.Holds, string PhysicalDetail = "", bool MemberDepends = false);

/// <summary>What one evaluation walked and set aside, counted per rule 6.</summary>
public sealed class StateTally
{
    public int States, Changes, Arrivals, Departures, SwitchesThemselves;
    public int NoGeometry, NoBox, NotObstructing, Volume, PairsTested, SameSetPairs, Unchanged, StateOverlapUnmeasured;
    public int HostsLeaving, HostsArriving, DepsArriving, DepsLeaving, DepsRemainOnHost, ReSupported, FloatingOnArrival, TouchingOnArrival, ContactUnmeasured;
    public int RouteAsked, RouteUnjudged, RouteNoLine, RouteClipped, RouteNone;
    public double ElapsedMs;
    /// <summary>Changed references a population scope did not process (window 2, task 1).</summary>
    public int OutsidePopulation;
    /// <summary>Sunk contact rows folded into another row of the same state and pair (<see cref="StateEvaluator.Evaluate"/>).</summary>
    public int MergedContactRows;
    /// <summary>Changed references that are a light with no model (ContactIndex.IsBodiless): no body, so not judged as an object,
    /// and set aside by name — never counted with the references the check could not judge (NoBox).</summary>
    public int NoBody;
    /// <summary>The same for an activator, and for a door, whose base record has no model (station AD, 2026-10-25).</summary>
    public int NoBodyActivator, NoBodyDoor;
    /// <summary>Changed references whose base carries the Is-Marker flag and whose reference carries no collision primitive: drawn
    /// as nothing and colliding with nothing, set aside by name.</summary>
    public int EditorMarker;
    /// <summary>Changed references whose base carries the Is-Marker flag and whose reference carries a collision primitive (a
    /// CollisionMarker's box, which may block: station Q): the check does not measure primitives, so unjudged, by name.</summary>
    public int MarkerPrimitive;
    /// <summary>With station Q's table (stateprobe --primitive-solidity): marker primitives the table measures solid (a body; the
    /// check does not measure primitive boxes, so unjudged, by name) and not solid (no body: set aside).</summary>
    public int MarkerPrimitiveSolid, MarkerPrimitiveNotSolid;
    /// <summary>The rest of the changed references without a box, by cause. Set aside: a decal (a placed TextureSet: a projected
    /// texture, no model and no collision). Unjudged, by name: a placed actor (the check judges objects), a placed hazard or trap
    /// (the placement reader reads no base link on those arms), an effects mesh in no role, a type in none of the check's role
    /// sets, and a box over the volume cap in no role.</summary>
    public int Decal, PlacedTrap, Actor, EffectVolume, TypeOutsideRoles, OverSizeCap;
}

/// <summary>Opt-in phase timers for <see cref="StateEvaluator.Evaluate"/> (stateprobe --phase-timers): ticks of
/// <see cref="System.Diagnostics.Stopwatch.GetTimestamp"/> per phase, summed over every state. Null on the evaluator: no clock reads.</summary>
public sealed class StatePhaseTimer
{
    public long Setup, Route, HostLeaving, HostArriving, DependentArriving, Other;
    public long RouteCalls, RouteMemoHits;
    public static double Seconds(long ticks) => ticks / (double)System.Diagnostics.Stopwatch.Frequency;
}

/// <summary>The probe's route measurement for one reference IN ONE STATE — the route-cut verdict of its obstacle (its
/// actor-solid collision, or its navcut box) against its cell's winning navmesh — or null when it cannot be measured,
/// with why. The arm is a parameter (Q27, 2026-09-06) because the floor the band stands on and the band's own top
/// belong to the state being judged: a flip can bring in a floor piece or a taller actor, and measuring an arriving
/// obstacle against the baseline's floor mixes two worlds.</summary>
public delegate RouteCutVerdict? RouteAsk(StateArm arm, FormKey key, bool navcut, out string why);

public sealed class StateEvaluator
{
    public ContactIndex Contact { get; }
    public SwitchIndex Switches { get; }
    public OverlapIndex Index => Contact.Index;
    public float MinDepth { get; init; } = 16f;
    public float MinFrac { get; init; } = 0.25f;
    /// <summary>The overlap pass's volume cap: a box larger than this on any axis (fog, LOD, skydomes) overlaps
    /// everything by design and is never an overlap row.</summary>
    public float MaxDim { get; init; } = 4096f;
    /// <summary>The overlap pass's obstructing base types (overlapprobe's set).</summary>
    public ISet<string> Obstructing { get; init; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    { "Static", "MoveableStatic", "Tree", "Furniture", "Door", "Container", "Activator", "Flora" };
    public RouteAsk? Route { get; set; }
    /// <summary>Station Q's measured primitive table (stateprobe --primitive-solidity): decides a marker's primitive. Null: none given.</summary>
    public IReadOnlyList<PrimitiveMeasurement>? PrimitiveTable { get; set; }
    /// <summary>A plan's population (window 2, task 1): a changed reference this says false for is not processed. Every row is
    /// computed from its own changed reference and the whole-order index, so the rows of a processed one are the whole-order
    /// run's. Null: every changed reference.</summary>
    public Func<FormKey, bool>? KeyInScope { get; set; }
    /// <summary>Opt-in phase timers (null: none, and no clock reads beyond the existing per-state stopwatch).</summary>
    public StatePhaseTimer? Timer { get; set; }

    [Flags] enum Role { None = 0, Obstructing = 1, Dependent = 2, Physical = 4, Host = 8, Navcut = 16 }
    readonly BoxHash<FormKey> _boxes = new();
    readonly Dictionary<FormKey, (Aabb Box, Role Role, FormKey Base)> _facts = new();
    readonly HashSet<FormKey> _stateOverlapScope = new();
    readonly System.Collections.Concurrent.ConcurrentDictionary<(string Arm, FormKey Key), (RouteCutVerdict? V, string Why)> _routeMemo = new();
    readonly bool _allowLegacyBoundsOverlapForProof;
    public int Boxed => _facts.Count;
    public int BoxedNoGeometry, BoxedMarkers, BoxedFxVolumes;

    public StateEvaluator(ContactIndex cidx, SwitchIndex sx) : this(cidx, sx, false) { }

    internal StateEvaluator(ContactIndex cidx, SwitchIndex sx, bool allowLegacyBoundsOverlapForProof)
    {
        Contact = cidx; Switches = sx;
        _allowLegacyBoundsOverlapForProof = allowLegacyBoundsOverlapForProof;
        // Every winning, undeleted reference with geometry, WHATEVER its enable state — the arm decides who is there.
        foreach (var (key, snaps) in Index.History.Refs)
        {
            var s = snaps[^1];
            if (s.Deleted || Index.IsDeletedNow(key)) continue;
            var b = cidx.WinnerBase(key);
            if (b.IsNull) { if (Index.RefArm.TryGetValue(key, out var ta) && ta is not ("PlacedObject" or "PlacedNpc")) _why[key] = Why.PlacedTrap; continue; }
            if (Index.RefArm.TryGetValue(key, out var arm) && arm == "PlacedNpc") { _why[key] = Why.Actor; BoxedNoGeometry++; continue; }
            if (Index.TextureSetDiffuse.ContainsKey(b)) { _why[key] = Why.Decal; BoxedNoGeometry++; continue; }
            // A navcut box: its obstacle is the XPRM primitive, not a mesh (its base is a flagged marker with no drawn shape).
            if (sx.IsNavcut(key))
            {
                if (!Index.RefPrimitive.TryGetValue(key, out var prim)) continue;
                var nbox = SpatialOverlap.ToWorld(NavcutGeometry.ModelBox(prim), s.Pos, s.Rot, s.Scale);
                if (nbox.IsEmpty) continue;
                _facts[key] = (nbox, Role.Navcut, b);
                _boxes.Add(s.Space, nbox, key);
                continue;
            }
            // A marker draws nothing; one whose reference carries a primitive may still block by it (station Q), so the two are
            // counted apart: the first set aside, the second unjudged.
            if (cidx.IsMarker(b))
            {
                BoxedMarkers++;
                if (Index.RefPrimitive.ContainsKey(key)) _markerPrimitive[key] = (b, s); else _marker.Add(key);
                continue;
            }
            // A light, activator or door with no model has no body: nothing to box, and nothing the check failed to judge.
            if (cidx.IsBodiless(b)) { _bodiless[key] = cidx.BodilessType(b); continue; }
            string type = Index.BaseInfo.TryGetValue(b, out var bi) ? bi.Type : "?";
            // Preserve the state class's declared effects exclusion while counting every other obstructing
            // reference as unmeasured, including missing geometry and formerly size-filtered surfaces.
            bool fx = Index.BaseMesh.TryGetValue(b, out var mesh) && mesh.Replace('/', '\\').Contains(@"effects\", StringComparison.OrdinalIgnoreCase);
            if (Obstructing.Contains(type) && !fx) _stateOverlapScope.Add(key);
            var g = cidx.GeomOf(b);
            if (g is null || !g.HasGeometry) { BoxedNoGeometry++; continue; }
            var box = cidx.WorldBox(b, s);
            if (box.IsEmpty) { BoxedNoGeometry++; continue; }
            var role = Role.None;
            float dim = MathF.Max(box.Max.X - box.Min.X, MathF.Max(box.Max.Y - box.Min.Y, box.Max.Z - box.Min.Z));
            // An effect volume (a mesh under effects\ — a magic barrier, a mist, a glow) fills a room by design and
            // obstructs nothing; the renderer drops the same files. Set aside from the overlap role, counted.
            if (fx) BoxedFxVolumes++;
            if (Obstructing.Contains(type) && dim <= MaxDim && !fx) role |= Role.Obstructing;
            if (cidx.IsDependentType(b) && g.CanDepend && dim <= cidx.MaxDependentDim) role |= Role.Dependent;
            if (cidx.IsPhysicalType(b) && g.CanHost) role |= Role.Physical;
            if (cidx.IsHostType(b) && g.CanHost && dim >= cidx.MinHostDim && dim <= cidx.MaxHostDim) role |= Role.Host;
            if (role == Role.None)
            {
                _why[key] = fx ? Why.EffectVolume : dim > MaxDim ? Why.OverSizeCap
                    : !Obstructing.Contains(type) && !cidx.IsDependentType(b) && !cidx.IsPhysicalType(b) && !cidx.IsHostType(b) ? Why.TypeOutsideRoles : Why.None;
                continue;
            }
            _facts[key] = (box, role, b);
            _boxes.Add(s.Space, box, key);
        }
    }

    public bool HasBox(FormKey k) => _facts.ContainsKey(k);
    readonly Dictionary<FormKey, string> _bodiless = new();
    readonly HashSet<FormKey> _marker = new();
    enum Why { None, PlacedTrap, Actor, Decal, EffectVolume, OverSizeCap, TypeOutsideRoles }
    readonly Dictionary<FormKey, Why> _why = new();
    readonly Dictionary<FormKey, (FormKey Base, Snap Pose)> _markerPrimitive = new();
    readonly System.Collections.Concurrent.ConcurrentDictionary<FormKey, string> _markerVerdict = new();

    /// <summary>A marker primitive under station Q's table: "solid", "not_solid", or "" (no table, a scaled reference, an unnamed
    /// layer, a shape or a (base, layer) the table does not measure). The same reader and rule as overlapprobe's.</summary>
    string MarkerVerdict(FormKey key) => _markerVerdict.GetOrAdd(key, k =>
    {
        if (PrimitiveTable is not { } table || !_markerPrimitive.TryGetValue(k, out var m) || !Index.RefPrimitive.TryGetValue(k, out var prim)) return "";
        if (MathF.Abs(m.Pose.Scale - 1f) > 1e-4f) return "";   // measured at scale 1
        string? lname = null;
        if (Index.RefCollisionLayer.TryGetValue(k, out var li))
        {
            if (!Index.CollisionLayersByIndex.TryGetValue(li, out var cl) || cl.Edid is null) return "";
            lname = cl.Edid;
        }
        string? btype = Index.BaseInfo.TryGetValue(m.Base, out var bi) ? bi.Type : null;
        var facts = new PrimitiveFacts(m.Base.ToString(), btype, lname, prim.Type, new Vector3(prim.Bounds.X / 2, prim.Bounds.Y / 2, prim.Bounds.Z / 2), m.Pose.Pos, m.Pose.Rot);
        return PrimitiveSolidity.Decide(facts, table, "").Status switch { "no_geometry" => "not_solid", "certified" => "solid", _ => "" };
    });

    /// <summary>Whether any boxed reference within <paramref name="reach"/> of <paramref name="k"/>'s winning box (its winning space)
    /// satisfies <paramref name="pred"/>: the neighbourhood the contact half reads.</summary>
    public bool AnyNear(FormKey k, float reach, Func<FormKey, bool> pred)
    {
        if (!_facts.TryGetValue(k, out var f) || Index.History.Winning(k) is not { } w) return false;
        foreach (var (n, _, _) in _boxes.Near(w.Space, f.Box, reach))
            if (n != k && pred(n)) return true;
        return false;
    }

    /// <summary>The class for one state. <paramref name="isVanilla"/> / <paramref name="isUssep"/> classify a plugin index.</summary>
    public List<StateRow> Evaluate(StateArm arm, Func<int, bool> isVanilla, Func<int, bool> isUssep, StateTally tally)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var tm = Timer;
        long t0 = tm is null ? 0 : System.Diagnostics.Stopwatch.GetTimestamp();
        long Lap(ref long bucket) { long now = System.Diagnostics.Stopwatch.GetTimestamp(); Interlocked.Add(ref bucket, now - t0); t0 = now; return now; }
        var rows = new List<StateRow>();
        var sx = Switches;
        var facts = arm.Wrap(sx.WinnerFacts, sx.Baseline);
        var memo = new Dictionary<FormKey, EnableResolution>();
        bool InState(FormKey k) => EnableState.Resolve(k, facts, memo).Enabled;
        bool InBase(FormKey k) => sx.Baseline(k).Enabled;
        bool Placed(FormKey k) => Index.History.Winning(k) is { Deleted: false } && !Index.IsDeletedNow(k);
        var changes = arm.Changes(sx).ToList();
        var changed = new Dictionary<FormKey, (SwitchInfo Root, SetMember? Member, bool After)>();
        foreach (var c in changes) changed[c.Key] = (c.Root, c.Member, c.After);
        tally.States++; tally.Changes += changes.Count;
        StatePopulation Pop(int memberResp, int otherResp) => DetectorModel.ClassifyStatePopulation(isVanilla(memberResp), isVanilla(otherResp), isUssep(memberResp), isUssep(otherResp));
        bool SameSet(FormKey a, FormKey b) => changed.TryGetValue(a, out var ca) && changed.TryGetValue(b, out var cb) && ca.Root.Key == cb.Root.Key && ca.After == cb.After;
        int PlacerOf(FormKey k) => sx.PlacerOf(k);
        // The plugin whose work the reference's geometry IS: the later of its last placement step and its last base change
        // (a re-based vanilla shell piece is the re-baser's). The overlap pass's "responsible" plus the base half.
        int ResponsibleOf(FormKey k)
        {
            int r = Index.History.Winning(k)?.Plugin ?? -1;
            if (Index.BaseChanges.TryGetValue(k, out var bc) && bc.Count > 0 && bc[^1].Plugin > r) r = bc[^1].Plugin;
            return r;
        }

        if (tm is not null) Lap(ref tm.Setup);
        foreach (var (key, before, after, root, member) in changes)
        {
            if (KeyInScope is not null && !KeyInScope(key)) { tally.OutsidePopulation++; continue; }
            if (after) tally.Arrivals++; else tally.Departures++;
            if (member is null) tally.SwitchesThemselves++;
            if (!_allowLegacyBoundsOverlapForProof && _stateOverlapScope.Contains(key)) tally.StateOverlapUnmeasured++;
            if (!_facts.TryGetValue(key, out var f))
            {
                if (_bodiless.TryGetValue(key, out var bt)) { if (bt == "Light") tally.NoBody++; else if (bt == "Activator") tally.NoBodyActivator++; else tally.NoBodyDoor++; }
                else if (_marker.Contains(key)) tally.EditorMarker++;
                else if (_markerPrimitive.ContainsKey(key))
                {
                    var v = MarkerVerdict(key);
                    if (v == "not_solid") tally.MarkerPrimitiveNotSolid++; else if (v == "solid") tally.MarkerPrimitiveSolid++; else tally.MarkerPrimitive++;
                }
                else switch (_why.GetValueOrDefault(key))
                {
                    case Why.PlacedTrap: tally.PlacedTrap++; break;
                    case Why.Actor: tally.Actor++; break;
                    case Why.Decal: tally.Decal++; break;
                    case Why.EffectVolume: tally.EffectVolume++; break;
                    case Why.OverSizeCap: tally.OverSizeCap++; break;
                    case Why.TypeOutsideRoles: tally.TypeOutsideRoles++; break;
                    default: tally.NoBox++; break;
                }
                continue;
            }
            var win = Index.History.Winning(key)!.Value;
            int placer = PlacerOf(key), attacher = member?.Attacher ?? sx.AttacherOf(key), resp = ResponsibleOf(key);
            bool interior = win.Interior;

            // ---- OVERLAP ----
            // Production never enumerates box-near pairs: that would turn a diagnostic broad phase into either
            // a positive or a credible pair denominator. Only the historical proof opts into this block.
            if ((f.Role & Role.Obstructing) != 0 && _allowLegacyBoundsOverlapForProof)
            {
                foreach (var (n, nbox, _) in _boxes.Near(win.Space, f.Box, 0f))
                {
                    if (n == key) continue;
                    var nf = _facts[n];
                    if ((nf.Role & Role.Obstructing) == 0) continue;
                    bool nChanged = changed.ContainsKey(n);
                    if (nChanged && string.CompareOrdinal(key.ToString(), n.ToString()) > 0) continue;   // the pair once
                    var ov = OverlapIndex.Overlap(f.Box, nbox);
                    if (ov is not { } o || o.MinDepth < MinDepth || o.VolumeFraction < MinFrac) continue;
                    tally.PairsTested++;
                    bool inBase = before && InBase(n), inState = after && InState(n);
                    var (v, why) = DetectorModel.StateDifferential(inBase, inState);
                    if (v is StateVerdict.Unchanged or StateVerdict.Absent) { tally.Unchanged++; continue; }
                    bool same = SameSet(key, n);
                    if (same) tally.SameSetPairs++;
                    int nr = ResponsibleOf(n);
                    rows.Add(new StateRow(arm.Name, root.Key, root.Owner, StateRowKind.Overlap, v, why,
                        key, after, placer, attacher, f.Base, n, PlacerOf(n), nf.Base, win.Cell, win.Space, interior,
                        Pop(resp, nr), same, o.MinDepth, o.VolumeFraction, SameAuthor: resp == nr, MemberResponsible: resp, OtherResponsible: nr,
                        Physical: PhysicalState.Holds, PhysicalDetail: $"historical proof only: the boxes overlap {o.MinDepth:0}u deep, {o.VolumeFraction:0.00} of the smaller, in the {(v == StateVerdict.StateConditional ? "state" : "baseline")}"));
                }
            }

            if (tm is not null) Lap(ref tm.Other);
            // ---- ROUTE ----
            if (Route is not null && ((f.Role & Role.Navcut) != 0 || (f.Role & Role.Physical) != 0))
            {
                bool navcut = (f.Role & Role.Navcut) != 0;
                // Memoised per (state, reference): the same reference measured in two different states can differ, because
                // each state has its own floor and band (Q27). Before then the memo was keyed on the reference alone.
                if (!_routeMemo.TryGetValue((arm.Name, key), out var rm)) { var v0 = Route(arm, key, navcut, out var w0); _routeMemo[(arm.Name, key)] = rm = (v0, w0); if (tm is not null) Interlocked.Increment(ref tm.RouteCalls); }
                else if (tm is not null) Interlocked.Increment(ref tm.RouteMemoHits);
                tally.RouteAsked++;
                if (rm.V is null || rm.V == RouteCutVerdict.Unjudgeable) tally.RouteUnjudged++;
                else if (rm.V == RouteCutVerdict.NoLine) tally.RouteNoLine++;
                else if (rm.V == RouteCutVerdict.Clipped) tally.RouteClipped++;
                else if (rm.V == RouteCutVerdict.None) tally.RouteNone++;
                else
                {
                    var (v, why) = DetectorModel.StateDifferential(before, after);
                    // A route row has no other reference: its other side is the switch that brings the obstacle in or takes it out.
                    rows.Add(new StateRow(arm.Name, root.Key, root.Owner, StateRowKind.Route, v, $"{rm.V}: {rm.Why}",
                        key, after, placer, attacher, f.Base, null, -1, FormKey.Null, win.Cell, win.Space, interior,
                        Pop(resp, root.Owner), false, Route: rm.V, Navcut: navcut, MemberResponsible: resp,
                        Physical: PhysicalState.Holds, PhysicalDetail: $"{rm.V} on the cell's winning navmesh"));
                }
            }

            if (tm is not null) Lap(ref tm.Route);
            // ---- CONTACT: a host leaves or arrives ----
            if ((f.Role & Role.Host) != 0)
            {
                var snaps = Index.History.Of(key)!;
                int hostSnap = snaps.Count - 1;
                if (before && !after)
                {
                    tally.HostsLeaving++;
                    try {
                    foreach (var (d, dbox, _) in _boxes.Near(win.Space, f.Box, Contact.Reach))
                    {
                        if (d == key) continue;
                        var df = _facts[d];
                        if ((df.Role & Role.Dependent) == 0) continue;
                        if (!InBase(d) || !InState(d)) { if (InBase(d)) tally.DepsLeaving++; continue; }   // it leaves too, or was never there
                        if (SpatialOverlap.BoxDistance(dbox, f.Box) > Contact.BoxBand) continue;
                        var dsnaps = Index.History.Of(d)!; var dwin = dsnaps[^1];
                        var auth = Contact.PairContact(d, dsnaps.Count - 1, df.Base, dwin, key, hostSnap, f.Base, win);
                        if (auth is null) { tally.ContactUnmeasured++; continue; }
                        var motion = Contact.MotionOf(df.Base);
                        var f0 = new DetectorModel.ContactFacts(true, true, true, auth.Contact.FacingDistance, auth.Contact.EffectivePenetration, auth.Contact.NormalUp,
                            false, false, false, false, float.NaN, float.NaN, false, motion);
                        var r0 = DetectorModel.PlacementContact(f0, Contact.Epsilon, Contact.SupportCos, Contact.AttachCos);
                        if (r0.Relation == ContactRelation.None) continue;                    // it was not on this host
                        // The state arm: everything physical and enabled in the state, the departed host excluded.
                        var best = Contact.BestContact(d, dsnaps.Count - 1, df.Base, dwin, dbox, _boxes,
                            k => k != key && (_facts[k].Role & Role.Physical) != 0 && Placed(k) && InState(k) ? (Index.History.Winning(k)!.Value, Index.History.Of(k)!.Count - 1) : null,
                            (k, _) => Contact.WinnerBase(k), out _);
                        var f1 = f0 with { WinnerMeasured = true, WinnerDistance = best?.Contact.FacingDistance ?? Contact.Reach, WinnerPenetration = best?.Contact.EffectivePenetration ?? 0f,
                                           WinnerContactIsHost = false, WinnerAgainstExtent = best?.AgainstExtent ?? float.NaN };
                        var r = DetectorModel.PlacementContact(f1, Contact.Epsilon, Contact.SupportCos, Contact.AttachCos);
                        if (r.Verdict is ContactVerdict.ReSupported) { tally.ReSupported++; continue; }
                        if (r.Verdict is not (ContactVerdict.Stranded or ContactVerdict.Orphaned or ContactVerdict.Sunk)) { tally.ContactUnmeasured++; continue; }
                        var (v, why) = DetectorModel.StateDifferential(false, true);
                        int dr = ResponsibleOf(d);
                        rows.Add(new StateRow(arm.Name, root.Key, root.Owner, StateRowKind.Contact, v, $"{r.Verdict}: the host leaves with the flip — {r.Why}",
                            key, false, placer, attacher, f.Base, d, PlacerOf(d), df.Base, dwin.Cell, win.Space, interior,
                            Pop(resp, dr), SameSet(key, d), SameAuthor: resp == dr, MemberResponsible: resp, OtherResponsible: dr,
                            ContactVerdict: r.Verdict, Relation: r.Relation, Distance: f1.WinnerDistance, Penetration: f1.WinnerPenetration, RawDistance: best?.RawDistance ?? float.NaN, Motion: motion,
                            Physical: PhysicalState.Holds, PhysicalDetail: r.Verdict == ContactVerdict.Sunk ? $"{f1.WinnerPenetration:0.#}u inside something in the state" : $"touches nothing within {Contact.Epsilon:0.#}u in the state (nearest {f1.WinnerDistance:0.#}u)"));
                    }
                    } finally { if (tm is not null) Lap(ref tm.HostLeaving); }
                }
                else if (!before && after)
                {
                    tally.HostsArriving++;
                    try {
                    foreach (var (d, dbox, _) in _boxes.Near(win.Space, f.Box, Contact.BoxBand))
                    {
                        if (d == key) continue;
                        var df = _facts[d];
                        if ((df.Role & Role.Dependent) == 0 || !InState(d)) continue;
                        if (SpatialOverlap.BoxDistance(dbox, f.Box) > Contact.BoxBand) continue;
                        var dsnaps = Index.History.Of(d)!; var dwin = dsnaps[^1];
                        var c = Contact.PairContact(d, dsnaps.Count - 1, df.Base, dwin, key, hostSnap, f.Base, win);
                        if (c is null) { tally.ContactUnmeasured++; continue; }
                        bool same = SameSet(key, d);
                        if (c.Contact.EffectivePenetration > Contact.Epsilon)
                        {
                            bool refused = !float.IsNaN(c.AgainstExtent) && c.Contact.EffectivePenetration > c.AgainstExtent;   // R4
                            if (refused) { tally.ContactUnmeasured++; continue; }
                            if (same) tally.SameSetPairs++;
                            int dr2 = ResponsibleOf(d);
                            rows.Add(new StateRow(arm.Name, root.Key, root.Owner, StateRowKind.Contact, StateVerdict.StateConditional, $"Sunk: the arriving host encloses the dependent {c.Contact.EffectivePenetration:0.#}u deep",
                                key, true, placer, attacher, f.Base, d, PlacerOf(d), df.Base, dwin.Cell, win.Space, interior, Pop(resp, dr2), same, SameAuthor: resp == dr2, MemberResponsible: resp, OtherResponsible: dr2,
                                ContactVerdict: ContactVerdict.Sunk, Relation: ContactRelation.None, Distance: c.Contact.FacingDistance, Penetration: c.Contact.EffectivePenetration, Motion: Contact.MotionOf(df.Base),
                                Physical: PhysicalState.Holds, PhysicalDetail: $"{c.Contact.EffectivePenetration:0.#}u inside the arrived host"));
                        }
                        else if (c.Contact.FacingDistance <= Contact.Epsilon) tally.ReSupported++;
                    }
                    } finally { if (tm is not null) Lap(ref tm.HostArriving); }
                }
            }

            // ---- CONTACT: a dependent arrives ----
            if (tm is not null) Lap(ref tm.Other);
            if ((f.Role & Role.Dependent) != 0 && !before && after)
            {
                tally.DepsArriving++;
                try {
                var dsnaps = Index.History.Of(key)!;
                var best = Contact.BestContact(key, dsnaps.Count - 1, f.Base, win, f.Box, _boxes,
                    k => k != key && (_facts[k].Role & Role.Physical) != 0 && Placed(k) && InState(k) ? (Index.History.Winning(k)!.Value, Index.History.Of(k)!.Count - 1) : null,
                    (k, _) => Contact.WinnerBase(k), out _);
                var motion = Contact.MotionOf(f.Base);
                if (best is not null && best.Contact.EffectivePenetration > Contact.Epsilon && (float.IsNaN(best.AgainstExtent) || best.Contact.EffectivePenetration <= best.AgainstExtent))
                {
                    bool same = SameSet(key, best.Against);
                    if (same) tally.SameSetPairs++;
                    int ar = ResponsibleOf(best.Against);
                    rows.Add(new StateRow(arm.Name, root.Key, root.Owner, StateRowKind.Contact, StateVerdict.StateConditional, $"Sunk: the arriving dependent sits {best.Contact.EffectivePenetration:0.#}u inside something enabled in the state",
                        key, true, placer, attacher, f.Base, best.Against, PlacerOf(best.Against), Contact.WinnerBase(best.Against), win.Cell, win.Space, interior, Pop(resp, ar), same, SameAuthor: resp == ar, MemberResponsible: resp, OtherResponsible: ar,
                        ContactVerdict: ContactVerdict.Sunk, Distance: best.Contact.FacingDistance, Penetration: best.Contact.EffectivePenetration, RawDistance: best.RawDistance, Motion: motion,
                        Physical: PhysicalState.Holds, PhysicalDetail: $"{best.Contact.EffectivePenetration:0.#}u inside {best.Against}", MemberDepends: true));
                }
                else if (best is not null && best.Contact.FacingDistance <= Contact.Epsilon) tally.TouchingOnArrival++;
                else
                {
                    // Floating on arrival. BestContact looks inside the box band only; the nearest physical thing within the
                    // contact reach is found here. A row only when that thing was moved or relocated by a plugin after the
                    // member's placer — the Stranded shape with the flip as the moment it shows; else the absolute floor's
                    // business (the set shipped floating), tallied.
                    tally.FloatingOnArrival++;
                    FormKey near = FormKey.Null; float nearD = float.PositiveInfinity;
                    foreach (var (k, kbox, d) in _boxes.Near(win.Space, f.Box, Contact.Reach))
                        if (k != key && (_facts[k].Role & Role.Physical) != 0 && Placed(k) && InState(k) && d < nearD) { nearD = d; near = k; }
                    if (!near.IsNull)
                    {
                        var ns = Index.History.Of(near);
                        bool moved = ns is { Count: >= 2 } && ns[^1].Plugin > placer && (PlacementHistory.Dist(ns[0].Pos, ns[^1].Pos) > PlacementHistory.MoveThreshold || ns[0].Cell != ns[^1].Cell);
                        if (moved)
                            rows.Add(new StateRow(arm.Name, root.Key, root.Owner, StateRowKind.Contact, StateVerdict.StateConditional,
                                $"Stranded on arrival: nothing within {Contact.Epsilon:0.#}u; the nearest thing ({nearD:0.#}u away) was moved by a later plugin",
                                key, true, placer, attacher, f.Base, near, PlacerOf(near), Contact.WinnerBase(near), win.Cell, win.Space, interior, Pop(resp, ResponsibleOf(near)), SameSet(key, near), SameAuthor: resp == ResponsibleOf(near), MemberResponsible: resp, OtherResponsible: ResponsibleOf(near),
                                ContactVerdict: ContactVerdict.Stranded, Distance: nearD, Penetration: 0f, RawDistance: best?.RawDistance ?? float.NaN, Motion: motion,
                                Physical: PhysicalState.Holds, PhysicalDetail: $"touches nothing within {Contact.Epsilon:0.#}u in the state (nearest {nearD:0.#}u)"));
                    }
                }
                } finally { if (tm is not null) Lap(ref tm.DependentArriving); }
            }
            if (tm is not null) Lap(ref tm.Other);
        }
        tally.ElapsedMs += sw.Elapsed.TotalMilliseconds;
        return OnePerPair(rows, tally);
    }

    /// <summary>One row per pair: the host pass and the dependent pass can each write a Sunk row for the same two references in one
    /// state, sometimes read in the two directions (each one's deepest vertex inside the other's solid). They are one clip in the
    /// game, so they are one row. Each reading is a lower bound on the move that clears the clip, so the deeper is kept; on equal
    /// depth (the same direction read twice) the dependent pass's row is kept. The kept row takes the pair's first row's place.</summary>
    static List<StateRow> OnePerPair(List<StateRow> rows, StateTally tally)
    {
        var at = new Dictionary<(FormKey, FormKey), int>();
        var kept = new List<StateRow>(rows.Count);
        foreach (var r in rows)
        {
            if (r.Kind != StateRowKind.Contact || r.ContactVerdict != ContactVerdict.Sunk || r.Other is not { } o) { kept.Add(r); continue; }
            var pair = PairKey(r.Member, o);
            if (!at.TryGetValue(pair, out var i)) { at[pair] = kept.Count; kept.Add(r); continue; }
            tally.MergedContactRows++;
            if (Deeper(r, kept[i])) kept[i] = r;
        }
        return kept;
    }

    static (FormKey, FormKey) PairKey(FormKey a, FormKey b) => string.CompareOrdinal(a.ToString(), b.ToString()) <= 0 ? (a, b) : (b, a);

    static bool Deeper(StateRow a, StateRow b) => a.Penetration != b.Penetration ? a.Penetration > b.Penetration : a.MemberDepends && !b.MemberDepends;

}
