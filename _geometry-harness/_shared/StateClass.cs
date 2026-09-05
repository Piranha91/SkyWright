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
//   OVERLAP  an arriving reference's world box against every enabled box in its space at the overlap
//            rule's threshold (16 u deep, a quarter of the smaller) is a StateConditional row; a
//            departing reference's deep overlaps at baseline are StateResolved. Two members of the
//            same set meeting each other is the set's own floor (SameSet), counted apart; so is a
//            member meeting a reference its own placer placed (SameAuthor: the author designed the
//            set against that geometry — the overlap pass's same-responsible floor, in state form).
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
/// <paramref name="Arrives"/>); <paramref name="Other"/> the reference on the other side (null for a route row).</summary>
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
    PhysicalState Physical = PhysicalState.Holds, string PhysicalDetail = "");

/// <summary>What one evaluation walked and set aside, counted per rule 6.</summary>
public sealed class StateTally
{
    public int States, Changes, Arrivals, Departures, SwitchesThemselves;
    public int NoGeometry, NoBox, NotObstructing, Volume, PairsTested, SameSetPairs, Unchanged;
    public int HostsLeaving, HostsArriving, DepsArriving, DepsLeaving, DepsRemainOnHost, ReSupported, FloatingOnArrival, TouchingOnArrival, ContactUnmeasured;
    public int RouteAsked, RouteUnjudged, RouteNoLine, RouteClipped, RouteNone;
    public double ElapsedMs;
}

/// <summary>The probe's route measurement for one reference — the route-cut verdict of its obstacle (its actor-solid
/// collision, or its navcut box) against its cell's winning navmesh — or null when it cannot be measured, with why.</summary>
public delegate RouteCutVerdict? RouteAsk(FormKey key, bool navcut, out string why);

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

    [Flags] enum Role { None = 0, Obstructing = 1, Dependent = 2, Physical = 4, Host = 8, Navcut = 16 }
    readonly BoxHash<FormKey> _boxes = new();
    readonly Dictionary<FormKey, (Aabb Box, Role Role, FormKey Base)> _facts = new();
    readonly Dictionary<FormKey, (RouteCutVerdict? V, string Why)> _routeMemo = new();
    public int Boxed => _facts.Count;
    public int BoxedNoGeometry, BoxedMarkers, BoxedFxVolumes;

    public StateEvaluator(ContactIndex cidx, SwitchIndex sx)
    {
        Contact = cidx; Switches = sx;
        // Every winning, undeleted reference with geometry, WHATEVER its enable state — the arm decides who is there.
        foreach (var (key, snaps) in Index.History.Refs)
        {
            var s = snaps[^1];
            if (s.Deleted || Index.IsDeletedNow(key)) continue;
            var b = cidx.WinnerBase(key);
            if (b.IsNull) continue;
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
            if (cidx.IsMarker(b)) { BoxedMarkers++; continue; }
            var g = cidx.GeomOf(b);
            if (g is null || !g.HasGeometry) { BoxedNoGeometry++; continue; }
            var box = cidx.WorldBox(b, s);
            if (box.IsEmpty) { BoxedNoGeometry++; continue; }
            var role = Role.None;
            string type = Index.BaseInfo.TryGetValue(b, out var bi) ? bi.Type : "?";
            float dim = MathF.Max(box.Max.X - box.Min.X, MathF.Max(box.Max.Y - box.Min.Y, box.Max.Z - box.Min.Z));
            // An effect volume (a mesh under effects\ — a magic barrier, a mist, a glow) fills a room by design and
            // obstructs nothing; the renderer drops the same files. Set aside from the overlap role, counted.
            bool fx = Index.BaseMesh.TryGetValue(b, out var mesh) && mesh.Replace('/', '\\').Contains(@"effects\", StringComparison.OrdinalIgnoreCase);
            if (fx) BoxedFxVolumes++;
            if (Obstructing.Contains(type) && dim <= MaxDim && !fx) role |= Role.Obstructing;
            if (cidx.IsDependentType(b) && g.CanDepend && dim <= cidx.MaxDependentDim) role |= Role.Dependent;
            if (cidx.IsPhysicalType(b) && g.CanHost) role |= Role.Physical;
            if (cidx.IsHostType(b) && g.CanHost && dim >= cidx.MinHostDim && dim <= cidx.MaxHostDim) role |= Role.Host;
            if (role == Role.None) continue;
            _facts[key] = (box, role, b);
            _boxes.Add(s.Space, box, key);
        }
    }

    public bool HasBox(FormKey k) => _facts.ContainsKey(k);

    /// <summary>The class for one state. <paramref name="isVanilla"/> / <paramref name="isUssep"/> classify a plugin index.</summary>
    public List<StateRow> Evaluate(StateArm arm, Func<int, bool> isVanilla, Func<int, bool> isUssep, StateTally tally)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
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

        foreach (var (key, before, after, root, member) in changes)
        {
            if (after) tally.Arrivals++; else tally.Departures++;
            if (member is null) tally.SwitchesThemselves++;
            if (!_facts.TryGetValue(key, out var f)) { tally.NoBox++; continue; }
            var win = Index.History.Winning(key)!.Value;
            int placer = PlacerOf(key), attacher = member?.Attacher ?? sx.AttacherOf(key), resp = ResponsibleOf(key);
            bool interior = win.Interior;

            // ---- OVERLAP ----
            if ((f.Role & Role.Obstructing) != 0)
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
                        Physical: PhysicalState.Holds, PhysicalDetail: $"the boxes overlap {o.MinDepth:0}u deep, {o.VolumeFraction:0.00} of the smaller, in the {(v == StateVerdict.StateConditional ? "state" : "baseline")}"));
                }
            }

            // ---- ROUTE ----
            if (Route is not null && ((f.Role & Role.Navcut) != 0 || (f.Role & Role.Physical) != 0))
            {
                bool navcut = (f.Role & Role.Navcut) != 0;
                if (!_routeMemo.TryGetValue(key, out var rm)) { var v0 = Route(key, navcut, out var w0); _routeMemo[key] = rm = (v0, w0); }
                tally.RouteAsked++;
                if (rm.V is null || rm.V == RouteCutVerdict.Unjudgeable) tally.RouteUnjudged++;
                else if (rm.V == RouteCutVerdict.NoLine) tally.RouteNoLine++;
                else if (rm.V == RouteCutVerdict.Clipped) tally.RouteClipped++;
                else if (rm.V == RouteCutVerdict.None) tally.RouteNone++;
                else
                {
                    var (v, why) = DetectorModel.StateDifferential(before, after);
                    rows.Add(new StateRow(arm.Name, root.Key, root.Owner, StateRowKind.Route, v, $"{rm.V}: {rm.Why}",
                        key, after, placer, attacher, f.Base, null, -1, FormKey.Null, win.Cell, win.Space, interior,
                        Pop(resp, -1), false, Route: rm.V, Navcut: navcut, MemberResponsible: resp,
                        Physical: PhysicalState.Holds, PhysicalDetail: $"{rm.V} on the cell's winning navmesh"));
                }
            }

            // ---- CONTACT: a host leaves or arrives ----
            if ((f.Role & Role.Host) != 0)
            {
                var snaps = Index.History.Of(key)!;
                int hostSnap = snaps.Count - 1;
                if (before && !after)
                {
                    tally.HostsLeaving++;
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
                }
                else if (!before && after)
                {
                    tally.HostsArriving++;
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
                }
            }

            // ---- CONTACT: a dependent arrives ----
            if ((f.Role & Role.Dependent) != 0 && !before && after)
            {
                tally.DepsArriving++;
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
                        Physical: PhysicalState.Holds, PhysicalDetail: $"{best.Contact.EffectivePenetration:0.#}u inside {best.Against}"));
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
            }
        }
        tally.ElapsedMs += sw.Elapsed.TotalMilliseconds;
        return rows;
    }

}
