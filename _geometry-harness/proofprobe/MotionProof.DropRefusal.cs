using System.Numerics;
using HousecarlCore;

/// <summary>Drop-crossing redesign, lane 2 (2026-10-13): a body pushed off a support it overlapped at placement no longer carries a label.
/// A settling body's reach row is refused by name: it moves on load and its rest place is not read; the reader's drop is kept as data,
/// marked a lower bound only for causes a lab station covered. A downward push through an up-facing surface and a deep near-tie push
/// (an impalement) are refused with no bound: the direction is not read, and the row carries the reader's two push depths. An exempt
/// body keeps its placed region; its gap carries the same marks. A drop that starts inside a support's shell and reads longer than
/// the reduced reading is refused too. Expected values are analytic (stated per case).</summary>
static partial class MotionProof
{
    public static void RunDropRefusal(Action<string, bool, string> check)
    {
        static string Show(ReachRegion r) => $"{r.Status} [{r.Reason}] labels [{string.Join("; ", r.Labels)}] drop " +
            (r.Drop is { } d ? $"{d.Status} d {d.Distance:0.###} support {d.SupportId} lowerBound {d.LowerBound} push {d.Push} {d.PushDepth:0.###}/{d.PushUpDepth:0.###} moves {d.MovesOnLoad} core {d.ShellCore:0.###}" : "-");
        static string ShowD(DropResult r) => $"{r.Status} d {r.Distance:0.###} support {r.SupportId} pushedOff {r.PushedOff} causes {r.Causes} {r.PushDepth:0.###}/{r.PushUpDepth:0.###}";
        static bool Nan(double x) => double.IsNaN(x);
        ReachOptions Settles(IEnumerable<DropSupport> s) => new(0.5, 65536, new ReachRules(Physics: true, BaseRecordType: "MISC",
            Supports: _ => s, Layers: RRLayers, Settle: new SettleDecision("settles", null), Rest: true));
        ReachOptions Rests(IEnumerable<DropSupport> s) => new(0.5, 65536, new ReachRules(Physics: true, BaseRecordType: "MISC",
            Supports: _ => s, Layers: RRLayers, Settle: new SettleDecision("rests", new SettleExemption("refRecordFlag", "0x20000000", "lab")), Rest: true));
        var floor = new DropSupport("floor", 1, true, false, Plate(500, -100));
        // a closed convex wall whose shell the item [-5,5]^3 clips sideways by 0.5 (LI-K16b's): a hull side push
        var wall = new[] { new DropSupport("wall", 1, true, false, ClosedBox(4.5f, -52, -62, 28.5f, 52, 42)), floor };
        IReadOnlyList<DropSupport> PlateAt(float z) => PhysicsReach.Supports("plate", SupportMeshPlate(50f, 0.35f), new Vec3(0, 0, z), new Vec3(0, 0, 0), 1f, movableBase: false)
                                                                    .Append(floor).ToArray();
        bool Pushed(ReachRegion r) => r.Labels.Contains(PhysicsReach.PushedOffLabel) || r.Labels.Contains(PhysicsReach.DownThroughSlabLabel);

        // L2-1 a settling item pushed off a hull side (lab-covered cause: lab 4's knife): refused by name, moves on load, the reader's drop
        // 95 kept as a lower bound, cause HullSide, no label. Rejects today's labelled uncertified row, a refusal that loses the drop or the
        // "moves" fact, and a certified drop.
        Case(check, "drop-refusal L2-1: a settling item pushed off a hull side - refused (moves on load), lower bound 95, cause HullSide, no label", () =>
        {
            var r = MeshReach.OfModel(FreeItem(), Origin, Origin, 1f, null, Settles(wall));
            return (r.Status == "refused" && (r.Reason ?? "").StartsWith(PhysicsReach.PushedOffReason) && !Pushed(r)
                    && r.Drop is { Status: "dropped", LowerBound: true, MovesOnLoad: true, Push: PushCause.HullSide, SupportId: "floor" } d && NearA7(d.Distance, 95, 1e-3), Show(r));
        });
        // L2-1b the same under plain physics (no rest rule, no settle decision): the same refusal. Rejects a change made only under rest.
        Case(check, "drop-refusal L2-1b: the same under plain physics - refused, lower bound 95, cause HullSide, no label", () =>
        {
            var r = MeshReach.OfModel(FreeItem(), Origin, Origin, 1f, null, RulesOn(physics: true, baseType: "MISC", supports: wall));
            return (r.Status == "refused" && (r.Reason ?? "").StartsWith(PhysicsReach.PushedOffReason) && !Pushed(r)
                    && r.Drop is { Status: "dropped", LowerBound: true, MovesOnLoad: true, Push: PushCause.HullSide } d && NearA7(d.Distance, 95, 1e-3), Show(r));
        });
        // L2-2 an exempt item (rests where placed until disturbed) in the same clip: its region stays the placed box (not refused), its
        // gap 95 is recorded as a lower bound with the cause, it does not move on load, no label. Rejects refusing a body that rests in
        // place, and an unmarked gap.
        Case(check, "drop-refusal L2-2: an exempt item in the same clip - placed box kept, rest-gap 95 marked lower bound, not moving, no label", () =>
        {
            var r = MeshReach.OfModel(FreeItem(), Origin, Origin, 1f, null, Rests(wall));
            return (r.Status != "refused" && BoxIs(r.Box, -5, -5, -5, 5, 5, 5) && !Pushed(r)
                    && r.Drop is { Status: "rest-gap", LowerBound: true, MovesOnLoad: false, Push: PushCause.HullSide } d && NearA7(d.Distance, 95, 1e-3), Show(r));
        });
        // L2-3 pushed down out of a down-facing slab (ceiling; lab-covered: lab 4's venison): a mesh plate at z 5.2 wound to face down
        // (radius 0.35: slab 4.85..5.55) over the item's top (5): out down 0.15. Refused, lower bound 95, cause SlabCeiling.
        Case(check, "drop-refusal L2-3: pushed down out of a ceiling slab - refused, lower bound 95, cause SlabCeiling", () =>
        {
            var cv = new[] { new Vector3(-50, -50, 5.2f), new Vector3(50, -50, 5.2f), new Vector3(50, 50, 5.2f), new Vector3(-50, 50, 5.2f) };
            var sup = PhysicsReach.Supports("ceil", SupportMesh(cv, new[] { 0, 2, 1, 0, 3, 2 }, 0.35f), new Vec3(0, 0, 0), new Vec3(0, 0, 0), 1f, movableBase: false)
                                  .Append(floor).ToArray();
            var r = MeshReach.OfModel(FreeItem(), Origin, Origin, 1f, null, Settles(sup));
            return (r.Status == "refused" && (r.Reason ?? "").StartsWith(PhysicsReach.PushedOffReason) && !Pushed(r)
                    && r.Drop is { LowerBound: true, MovesOnLoad: true, Push: PushCause.SlabCeiling } d && NearA7(d.Distance, 95, 1e-3), Show(r));
        });
        // L2-4 a cause no lab station covered (a hull pushing the body down: a closed box z 4.8..30 over the item's top, out down 0.2):
        // refused, moves on load, the reader's drop 95 kept but NOT marked a lower bound, cause HullDown. Rejects the bound claimed for an
        // unmeasured cause.
        Case(check, "drop-refusal L2-4: pushed down off a hull (no station covers it) - refused, the reader's drop 95 not a lower bound, cause HullDown", () =>
        {
            var sup = new[] { new DropSupport("lid", 1, true, false, ClosedBox(-20, -20, 4.8f, 20, 20, 30)), floor };
            var r = MeshReach.OfModel(FreeItem(), Origin, Origin, 1f, null, Settles(sup));
            return (r.Status == "refused" && (r.Reason ?? "").StartsWith(PhysicsReach.PushedOffUnverifiedReason) && !Pushed(r)
                    && r.Drop is { LowerBound: false, MovesOnLoad: true, Push: PushCause.HullDown } d && NearA7(d.Distance, 95, 1e-3), Show(r));
        });
        // L2-5 blocker 1: an up-facing mesh plate at z 0.5 (slab 0.15..0.85) past the item's middle: out down 4.85, out up 5.85. Refused
        // with no drop claimed (distance NaN, no bound, not "moves"): the engine may hold it; the two push depths are given. Rejects a
        // lower bound on a direction-unread push (if the engine pushes it up, the true fall is 0), and today's label.
        Case(check, "drop-refusal L2-5: an up-facing surface past the item's middle - refused, no drop or bound, push 4.85 down / 5.85 up", () =>
        {
            var r = MeshReach.OfModel(FreeItem(), Origin, Origin, 1f, null, Settles(PlateAt(0.5f)));
            // corrected 2026-10-13 (rest rule part 5, lab 5 "down verified"): a single-component body over an open plate, landing on
            // another reference, is pushed down through it: the bound applies (was: refused, direction not read).
            return (r.Status == "refused" && (r.Reason ?? "").StartsWith(PhysicsReach.PushedOffReason) && !Pushed(r)
                    && r.Drop is { LowerBound: true, MovesOnLoad: true, Push: PushCause.UpFacingDown, SupportId: "floor" } d && NearA7(d.Distance, 95, 1e-3), Show(r));
        });
        // L2-6 blocker 2: the plate at z 0.1 (slab -0.25..0.45): out down 5.25 (a downward push >= ImpaleMinDepth 5), out up 5.45
        // (ratio 1.038 <= ImpaleTieRatio 1.25): an impalement. Refused with no drop or bound, the two depths given. Rejects a tie resolved silently.
        Case(check, "drop-refusal L2-6: an impalement (5.25 down / 5.45 up) - refused, no drop or bound, cause Impaled", () =>
        {
            var r = MeshReach.OfModel(FreeItem(), Origin, Origin, 1f, null, Settles(PlateAt(0.1f)));
            return (r.Status == "refused" && (r.Reason ?? "").StartsWith(PhysicsReach.ImpaledReason) && !Pushed(r)
                    && r.Drop is { LowerBound: false, MovesOnLoad: false } d && d.Push.HasFlag(PushCause.Impaled) && Nan(d.Distance)
                    && NearA7(d.PushDepth, 5.25, 1e-3) && NearA7(d.PushUpDepth, 5.45, 1e-3), Show(r));
        });
        // L2-7 ImpaleMinDepth either side (Drop): the [-5,5]^3 box under the plate at z 0.1 (down 5.25, up 5.45: impaled) and at z 0.5
        // (down 4.85 < 5, up 5.85: up-facing down, not impaled). Rejects a depth threshold read the wrong way or not at all.
        Case(check, "drop-refusal L2-7: ImpaleMinDepth either side - plate z 0.1 impaled (5.25), z 0.5 up-facing down only (4.85)", () =>
        {
            var body = ClosedBox(-5, -5, -5, 5, 5, 5);
            var a = PhysicsReach.Drop(body, SpatialOverlap.SoupBounds(body), 4, PlateAt(0.1f), RRLayers);
            var b = PhysicsReach.Drop(body, SpatialOverlap.SoupBounds(body), 4, PlateAt(0.5f), RRLayers);
            return (a.Causes.HasFlag(PushCause.Impaled) && NearA7(a.PushDepth, 5.25, 1e-3) && NearA7(a.PushUpDepth, 5.45, 1e-3)
                    && b.Causes == PushCause.UpFacingDown && NearA7(b.PushDepth, 4.85, 1e-3) && NearA7(b.PushUpDepth, 5.85, 1e-3), "z 0.1 " + ShowD(a) + " | z 0.5 " + ShowD(b));
        });
        // L2-8 ImpaleTieRatio either side (Drop): a box [-5,5]x[-5,5]x[-10,10] under the plate at z 0.85 (down 9.5, up 11.2: ratio 1.179,
        // impaled) and at z 1.35 (down 9.0, up 11.7: ratio 1.3, up-facing down only). Rejects a ratio threshold read the wrong way.
        Case(check, "drop-refusal L2-8: ImpaleTieRatio either side - plate z 0.85 impaled (9.5/11.2), z 1.35 up-facing down only (9.0/11.7)", () =>
        {
            var body = ClosedBox(-5, -5, -10, 5, 5, 10);
            var a = PhysicsReach.Drop(body, SpatialOverlap.SoupBounds(body), 4, PlateAt(0.85f), RRLayers);
            var b = PhysicsReach.Drop(body, SpatialOverlap.SoupBounds(body), 4, PlateAt(1.35f), RRLayers);
            return (a.Causes.HasFlag(PushCause.Impaled) && NearA7(a.PushDepth, 9.5, 1e-3) && NearA7(a.PushUpDepth, 11.2, 1e-3)
                    && b.Causes == PushCause.UpFacingDown && NearA7(b.PushDepth, 9.0, 1e-3) && NearA7(b.PushUpDepth, 11.7, 1e-3), "z 0.85 " + ShowD(a) + " | z 1.35 " + ShowD(b));
        });
        // L2-13 the ceiling bound covers grazes only (the one ceiling station, lab 4 L4-1, was a 0.074 u graze): a down-facing mesh plate
        // (radius 0.35) at z 4.7 (slab 4.35..5.05) takes the item's top (5) down 0.65 (< CeilingGrazeDepth 0.7): bound; at z 4.6 (slab
        // 4.25..4.95) down 0.75 (deeper than a graze): refused unverified, the reader's drop 95 not a bound. Rejects the bound read for
        // every ceiling push, and the graze line read the wrong way.
        Case(check, "drop-refusal L2-13: ceiling push either side of CeilingGrazeDepth - 0.65 bound, 0.75 unverified (drop 95 both)", () =>
        {
            ReachRegion At(float z)
            {
                var cv = new[] { new Vector3(-50, -50, z), new Vector3(50, -50, z), new Vector3(50, 50, z), new Vector3(-50, 50, z) };
                var sup = PhysicsReach.Supports("ceil", SupportMesh(cv, new[] { 0, 2, 1, 0, 3, 2 }, 0.35f), new Vec3(0, 0, 0), new Vec3(0, 0, 0), 1f, movableBase: false)
                                      .Append(floor).ToArray();
                return MeshReach.OfModel(FreeItem(), Origin, Origin, 1f, null, Settles(sup));
            }
            var a = At(4.7f); var b = At(4.6f);
            bool okA = a.Status == "refused" && (a.Reason ?? "").StartsWith(PhysicsReach.PushedOffReason) && a.Drop is { LowerBound: true, Push: PushCause.SlabCeiling } da && NearA7(da.Distance, 95, 1e-3);
            bool okB = b.Status == "refused" && (b.Reason ?? "").StartsWith(PhysicsReach.PushedOffUnverifiedReason) && b.Drop is { LowerBound: false, Push: PushCause.SlabCeiling } db && NearA7(db.Distance, 95, 1e-3);
            return (okA && okB && PhysicsReach.CeilingGrazeDepth == 0.7, "z 4.7 " + Show(a) + " | z 4.6 " + Show(b));
        });
        // L2-12 a deep side push near an upward tie is not an impalement (lab 4: the knife L4-3, 6.8 u sideways against 7.5 u up, and the
        // flour L4-4b, 6.4 against 6.7, left sideways as read): the item [-5,5]^3 clips a closed wall x -0.5..30, z -62..1 by 5.5 in x;
        // out -x 5.5, out up 6.0 (ratio 1.09). Cause HullSide only, depths not recorded. Rejects an impalement test that ignores the
        // push direction.
        Case(check, "drop-refusal L2-12: a deep side push near an upward tie (5.5 side / 6.0 up) - HullSide, not Impaled", () =>
        {
            var body = ClosedBox(-5, -5, -5, 5, 5, 5);
            var sup = new[] { new DropSupport("wall", 1, true, false, ClosedBox(-0.5f, -52, -62, 30, 52, 1)), floor };
            var r = PhysicsReach.Drop(body, SpatialOverlap.SoupBounds(body), 4, sup, RRLayers);
            return (r.PushedOff && r.Causes == PushCause.HullSide, ShowD(r));
        });
        // L2-9 a held item is not refused: the item's bottom 0.15 into an up-facing plate's slab top (plate z -5.2, slab -5.55..-4.85):
        // pushed up, held, touching on the plate, region not refused, cause None. Rejects over-refusal of held bodies.
        Case(check, "drop-refusal L2-9: a held item (pushed up) - touching on the plate, not refused, no cause", () =>
        {
            var r = MeshReach.OfModel(FreeItem(), Origin, Origin, 1f, null, Settles(PlateAt(-5.2f)));
            return (r.Status != "refused" && r.Drop is { Status: "lifted", SupportId: "plate", Push: PushCause.None, LowerBound: false }, Show(r));
        });
        // L2-10 a plain drop with no overlap is unchanged: the item over a floor 100 u down, settling: dropped 95, not refused, no cause,
        // no bound mark. Rejects over-refusal of plain drops.
        Case(check, "drop-refusal L2-10: a plain drop (no overlap) - dropped 95, not refused, no cause", () =>
        {
            var r = MeshReach.OfModel(FreeItem(), Origin, Origin, 1f, null, Settles(new[] { floor }));
            return (r.Status != "refused" && r.Drop is { Status: "dropped", Push: PushCause.None, LowerBound: false } d && NearA7(d.Distance, 95, 1e-3), Show(r));
        });
        // L2-11 the shell-crossing path (LI-K14's supports): a support whose radius soup sits at -100 while its core sits at -10 reads 95
        // with the radius and 5 with less of it. Refused by name with the reduced drop 5 given, no label; the honest support (core =
        // radius soup) is not refused. Rejects the label, and a refusal on every drop.
        Case(check, "drop-refusal L2-11: a drop longer with the radius than without - refused (shell crossing), reduced drop 5, no label; honest support not refused", () =>
        {
            var lying = new[] { new DropSupport("s", 1, true, false, Plate(500, -100), CoreWorld: Plate(500, -10)) };
            var honest = new[] { new DropSupport("s", 1, true, false, Plate(500, -100), CoreWorld: Plate(500, -100)) };
            var a = MeshReach.OfModel(FreeItem(), Origin, Origin, 1f, null, RulesOn(physics: true, baseType: "MISC", supports: lying));
            var b = MeshReach.OfModel(FreeItem(), Origin, Origin, 1f, null, RulesOn(physics: true, baseType: "MISC", supports: honest));
            bool label = a.Labels.Concat(b.Labels).Any(x => x.StartsWith(PhysicsReach.ShellCrossingLabel));
            return (!label && a.Status == "refused" && (a.Reason ?? "").StartsWith(PhysicsReach.ShellCrossingReason) && a.Drop is { LowerBound: false } d && NearA7(d.ShellCore, 5, 1e-3)
                    && b.Status != "refused" && b.Drop is { Status: "dropped" } e && NearA7(e.Distance, 95, 1e-3), "lying " + Show(a) + " | honest " + Show(b));
        });
    }
}
