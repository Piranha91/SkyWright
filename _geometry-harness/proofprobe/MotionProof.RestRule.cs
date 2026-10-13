using System.Numerics;
using HousecarlCore;

/// <summary>Rest rule implementation (2026-10-13; plan runs/2026-10-13-rest-rule-implementation/GATE-A.md): held embedded items read
/// `lifted` (part 1), weapon racks mount their default linked reference (part 2), loose items that hold are drop supports (part 3),
/// lab 5's downward direction through an up-facing surface for the shape it measured (part 5), and the tiny-drop text. Expected
/// values are analytic, stated per case.</summary>
static partial class MotionProof
{
    public static void RunRestRule(Action<string, bool, string> check)
    {
        static string Show(ReachRegion r) => $"{r.Status} [{r.Reason}] box {r.Box} labels [{string.Join("; ", r.Labels)}] drop " +
            (r.Drop is { } d ? $"{d.Status} d {d.Distance:0.###} support {d.SupportId} lowerBound {d.LowerBound} push {d.Push} {d.PushDepth:0.###}/{d.PushUpDepth:0.###} moves {d.MovesOnLoad}" : "-");
        static string ShowD(DropResult r) => $"{r.Status} [{r.Reason}] d {r.Distance:0.###}..{r.DistanceMax:0.###} support {r.SupportId} pushedOff {r.PushedOff} causes {r.Causes} {r.PushDepth:0.###}/{r.PushUpDepth:0.###} lowerBound {r.LowerBound}";
        ReachOptions Settles(IEnumerable<DropSupport> s, RackMountReading? rack = null) => new(0.5, 65536, new ReachRules(Physics: true, BaseRecordType: "MISC",
            Supports: _ => s, Layers: RRLayers, Settle: new SettleDecision("settles", null), Rest: true, Rack: rack));
        static TriangleSoup Merge(TriangleSoup a, TriangleSoup b) =>
            new(a.Vertices.Concat(b.Vertices).ToArray(), a.Indices.Concat(b.Indices.Select(i => i + a.Vertices.Length)).ToArray());
        var floor = new DropSupport("floor", 1, true, false, Plate(500, -100));
        var body = ClosedBox(-5, -5, -5, 5, 5, 5);
        var placed = SpatialOverlap.SoupBounds(body);
        IReadOnlyList<DropSupport> Hull(float top) => new[] { new DropSupport("slab", 1, true, false, ClosedBox(-50, -50, -40, 50, 50, top)), floor };
        IReadOnlyList<DropSupport> PlateAt(float z) => PhysicsReach.Supports("plate", SupportMeshPlate(50f, 0.35f), new Vec3(0, 0, z), new Vec3(0, 0, 0), 1f, movableBase: false)
                                                                    .Append(floor).ToArray();
        double m = Math.Sqrt(75) - 5;   // the [-5,5]^3 body's tilt margin: half-diagonal minus the smallest half-extent

        // ---- part 1: held embedded items read lifted ----
        // RR-H1 a box sunk 3 u into a hull (top -2; item bottom -5): pushed up 3 (sideways 45+), held: lifted, upward push 3. Rejects
        // today's touching (d 0) for a body the engine moves up on load.
        Case(check, "rest-rule RR-H1: a body sunk 3 u into its support reads lifted, upward push 3", () =>
        {
            var r = PhysicsReach.Drop(body, placed, 4, Hull(-2), RRLayers);
            return (r.Status == "lifted" && r.SupportId == "slab" && NearA7(r.PushUpDepth, 3, 1e-3), ShowD(r));
        });
        // RR-H3 a body resting exactly on its support (top -5, no overlap): touching. Rejects lifted over-applied to plain contact.
        Case(check, "rest-rule RR-H3: a body resting exactly on its support (no overlap) stays touching", () =>
        {
            var r = PhysicsReach.Drop(body, placed, 4, Hull(-5), RRLayers);
            return (r.Status == "touching" && r.SupportId == "slab", ShowD(r));
        });
        // RR-H4 a body 0.02 u into its support (top -4.98; below LiftMinDepth 0.05): touching. Rejects lifted on float noise.
        Case(check, "rest-rule RR-H4: a body 0.02 u into its support (under LiftMinDepth) stays touching", () =>
        {
            var r = PhysicsReach.Drop(body, placed, 4, Hull(-4.98f), RRLayers);
            return (r.Status == "touching" && r.SupportId == "slab" && PhysicsReach.LiftMinDepth == 0.05, ShowD(r));
        });
        // RR-H1m the reach row of RR-H1 (settling item): drop lifted, the lifted label and not the touching label, upward push 3,
        // region not refused. Rejects today's touching row.
        Case(check, "rest-rule RR-H1m: a settling item sunk 3 u reads lifted in its reach row (label, push 3, not refused)", () =>
        {
            var r = MeshReach.OfModel(FreeItem(), Origin, Origin, 1f, null, Settles(Hull(-2)));
            return (r.Status != "refused" && r.Drop is { Status: "lifted", SupportId: "slab" } d && NearA7(d.PushUpDepth, 3, 1e-3)
                    && r.Labels.Contains(PhysicsReach.LiftedLabel) && !r.Labels.Contains(PhysicsReach.DropTouchingLabel), Show(r));
        });
        // RR-H5 (KH-T) a lifted body's region reaches m below the placed bottom (its centre does not descend; a turn lowers the bottom
        // by at most m): box [-5-m, 5+m] laterally, [-5-m, 5+2m] vertically, m = 3.660. Rejects a lifted region whose floor stays at
        // the placed bottom (the tipped crate boots 00084D left it by 1.408 u).
        Case(check, "rest-rule RR-H5: a lifted body's region reaches the tilt margin below its placed bottom (KH-T)", () =>
        {
            var r = MeshReach.OfModel(FreeItem(), Origin, Origin, 1f, null, Settles(Hull(-2)));
            float a = (float)(5 + m);
            return (r.Drop is { Status: "lifted" } && BoxIs(r.Box, -a, -a, -a, a, a, (float)(5 + 2 * m)), Show(r));
        });
        // RR-H6 the same margin is NOT taken below a touching (not embedded) body: its bottom stays on the support. Rejects the
        // downward margin applied to every rest row.
        Case(check, "rest-rule RR-H6: a touching body's region keeps its bottom on the support (no downward margin)", () =>
        {
            var r = MeshReach.OfModel(FreeItem(), Origin, Origin, 1f, null, Settles(Hull(-5)));
            float a = (float)(5 + m);
            return (r.Drop is { Status: "touching" } && BoxIs(r.Box, -a, -a, -5, a, a, (float)(5 + 2 * m)), Show(r));
        });

        // ---- part 2: weapon racks ----
        var pivot = new RackPose(new Vec3(10, 20, 100), new Vec3(0, 0, 1.5708f));
        Func<string, RackPose?> Pivots(params string[] nodes) => n => nodes.Contains(n) ? pivot : null;
        RackFacts Facts(string kind, string keyword, int rackType = 1, bool itemEnabled = true, bool trigger = true, bool activatorEnabled = true) =>
            new(RackMount.ActivatorScript, rackType, false, activatorEnabled, trigger, true, itemEnabled, true, kind, new HashSet<string> { keyword });
        // RR-R1 a sword on a standard rack: mounted at SwordPivot01 of the trigger, at that node's pose. Rejects no mount.
        Case(check, "rest-rule RR-R1: a sword that is a standard rack's starting item is mounted at SwordPivot01", () =>
        {
            var r = RackMount.Decide(Facts("weapon", "WeaponTypeSword"), Pivots("SwordPivot01", "BowPivot01"));
            return (r.Status == "mounted" && r.Node == "SwordPivot01" && r.Pose == pivot, $"{r}");
        });
        // RR-R1b a bow -> BowPivot01, a battleaxe -> BattleAxePivot01 (labr2's five bows and axe). Rejects one pivot for every type.
        Case(check, "rest-rule RR-R1b: a bow mounts at BowPivot01 and a battleaxe at BattleAxePivot01", () =>
        {
            var b = RackMount.Decide(Facts("weapon", "WeaponTypeBow"), Pivots("SwordPivot01", "BowPivot01", "BattleAxePivot01"));
            var a = RackMount.Decide(Facts("weapon", "WeaponTypeBattleAxe"), Pivots("SwordPivot01", "BowPivot01", "BattleAxePivot01"));
            return (b is { Status: "mounted", Node: "BowPivot01" } && a is { Status: "mounted", Node: "BattleAxePivot01" }, $"{b} | {a}");
        });
        // RR-R2 the starting item disabled: the script skips it, not mounted. Rejects mounting a disabled item.
        Case(check, "rest-rule RR-R2: a disabled starting item is not mounted", () =>
        {
            var r = RackMount.Decide(Facts("weapon", "WeaponTypeSword", itemEnabled: false), Pivots("SwordPivot01"));
            return (r.Status == "not-mounted", $"{r}");
        });
        // RR-R2b no WRackTrigger link: the script goes inactive, not mounted. Rejects a mount without a trigger.
        Case(check, "rest-rule RR-R2b: no trigger link - not mounted", () =>
        {
            var r = RackMount.Decide(Facts("weapon", "WeaponTypeSword", trigger: false), Pivots("SwordPivot01"));
            return (r.Status == "not-mounted", $"{r}");
        });
        // RR-R3 the trigger's model has no pivot for the item's type: refused by name (the files cannot say where it goes). Rejects a
        // guessed pivot.
        Case(check, "rest-rule RR-R3: the trigger model lacks the type's pivot - refused by name", () =>
        {
            var r = RackMount.Decide(Facts("weapon", "WeaponTypeSword"), Pivots("BowPivot01"));
            return (r.Status == "refused" && (r.Reason ?? "").StartsWith(RackMount.RefusedReason), $"{r}");
        });
        // RR-R3b the activator itself disabled: whether its script runs at attach is not read: refused by name.
        Case(check, "rest-rule RR-R3b: a disabled rack activator - refused by name", () =>
        {
            var r = RackMount.Decide(Facts("weapon", "WeaponTypeSword", activatorEnabled: false), Pivots("SwordPivot01"));
            return (r.Status == "refused" && (r.Reason ?? "").StartsWith(RackMount.RefusedReason), $"{r}");
        });
        // RR-R4 a dagger on a CoA weapon rack (type 3) and a sword on a shield rack (type 2): rejected types, not mounted (the script
        // shows a message only). Rejects the rack-type table ignored.
        Case(check, "rest-rule RR-R4: a dagger on a type-3 rack and a sword on a type-2 rack are not mounted", () =>
        {
            var a = RackMount.Decide(Facts("weapon", "WeaponTypeDagger", rackType: 3), Pivots("DaggerPivot01", "SwordPivot01"));
            var b = RackMount.Decide(Facts("weapon", "WeaponTypeSword", rackType: 2), Pivots("DaggerPivot01", "SwordPivot01"));
            return (a.Status == "not-mounted" && b.Status == "not-mounted", $"{a} | {b}");
        });
        // RR-R5 a mounted reach row: the region is the item's box at the pivot pose (here translated to (10,20,100), turned 90° about
        // z: the cube is unchanged), no drop applied, drop status mounted, the mounted label. Rejects the placed pose or a drop.
        Case(check, "rest-rule RR-R5: a mounted item's region is its box at the pivot pose, no drop", () =>
        {
            var reading = new RackMountReading("mounted", null, "SwordPivot01", pivot, "trigger");
            var r = MeshReach.OfModel(FreeItem(), Origin, Origin, 1f, null, Settles(new[] { floor }, reading));
            return (r.Status != "refused" && r.Drop is { Status: "mounted" } && r.Labels.Any(l => l.StartsWith(RackMount.MountedLabel))
                    && BoxIs(r.Box, 5, 15, 95, 15, 25, 105, 0.05f), Show(r));
        });
        // RR-R6 a refused rack reading refuses the row by name. Rejects a silent fall-back to the drop.
        Case(check, "rest-rule RR-R6: a refused rack reading refuses the reach row by name", () =>
        {
            var reading = new RackMountReading("refused", RackMount.RefusedReason + " (no SwordPivot01 in the trigger model)", null, null);
            var r = MeshReach.OfModel(FreeItem(), Origin, Origin, 1f, null, Settles(new[] { floor }, reading));
            return (r.Status == "refused" && (r.Reason ?? "").StartsWith(RackMount.RefusedReason), Show(r));
        });

        // ---- part 3: loose supports ----
        DropSupport Loose(LooseRole role, float top, string id = "loose", string? group = null) =>
            new(id, 4, true, true, ClosedBox(-20, -20, top - 20, 20, 20, top), Group: group, Loose: role);
        // RR-L0 a loose box that holds (top -10) under the body: it lands on it, 5 u. Rejects the drop through it to the floor (95).
        Case(check, "rest-rule RR-L0: a loose item that holds is a support - lands 5 on it, not 95 to the floor", () =>
        {
            var r = PhysicsReach.Drop(body, placed, 4, new[] { Loose(LooseRole.Holds, -10), floor }, RRLayers);
            return (r.Status == "dropped" && r.SupportId == "loose" && NearA7(r.Distance, 5, 1e-3), ShowD(r));
        });
        // RR-L1 the same loose box when it itself moves at load: the body's first contact is on it: refused by name. Rejects landing on
        // the moving item's placed pose.
        Case(check, "rest-rule RR-L1: first contact on a loose item that moves - refused by name", () =>
        {
            var r = PhysicsReach.Drop(body, placed, 4, new[] { Loose(LooseRole.Moves, -10), floor }, RRLayers);
            return (r.Status == "refused" && r.Reason == PhysicsReach.LandsOnMovingLooseReason, ShowD(r));
        });
        // RR-L1b a static shelf (top -8) above the moving loose box: first contact on the shelf, dropped 3; the moving box is not met.
        // Rejects a refusal for any moving loose item in the column.
        Case(check, "rest-rule RR-L1b: a moving loose item below the first contact does not refuse - dropped 3 on the shelf", () =>
        {
            var shelf = new DropSupport("shelf", 1, true, false, ClosedBox(-20, -20, -9, 20, 20, -8));
            var r = PhysicsReach.Drop(body, placed, 4, new[] { Loose(LooseRole.Moves, -10), shelf, floor }, RRLayers);
            return (r.Status == "dropped" && r.SupportId == "shelf" && NearA7(r.Distance, 3, 1e-3), ShowD(r));
        });
        // RR-L2 first contact on a loose item in a cycle: refused by the cycle reason. Rejects an arbitrary order.
        Case(check, "rest-rule RR-L2: first contact on a loose item in a cycle - refused by the cycle reason", () =>
        {
            var r = PhysicsReach.Drop(body, placed, 4, new[] { Loose(LooseRole.Cycle, -10), floor }, RRLayers);
            return (r.Status == "refused" && r.Reason == PhysicsReach.LooseCycleReason, ShowD(r));
        });
        // RR-L2b the pass: a and b each ask for the other's role; the inner ask sees Cycle; each key is read once. Rejects recursion
        // without a visiting set, and a re-read per asker.
        Case(check, "rest-rule RR-L2b: the loose pass reports Cycle to the inner asker and reads each key once", () =>
        {
            var reads = new Dictionary<string, int>(); LooseRole inner = LooseRole.None;
            LooseReadings<string>? pass = null;
            pass = new LooseReadings<string>((k, role) =>
            {
                reads[k] = reads.GetValueOrDefault(k) + 1;
                var other = role(k == "a" ? "b" : "a");
                if (k == "b") inner = other;
                return other is LooseRole.Cycle or LooseRole.Moves ? LooseRole.Moves : LooseRole.Holds;
            });
            var a = pass.Role("a"); var b = pass.Role("b"); var a2 = pass.Role("a");
            return (inner == LooseRole.Cycle && a == LooseRole.Moves && b == LooseRole.Moves && a2 == a && reads["a"] == 1 && reads["b"] == 1,
                    $"inner {inner} a {a} b {b} reads {string.Join(",", reads)}");
        });
        // RR-L3 a lifted loose support given as two readings in one group (placed top -10, raised by its push 3 to -7): the body lands
        // between 2 and 5 (dropped-range). Rejects only the placed reading.
        Case(check, "rest-rule RR-L3: a lifted loose support's two readings give dropped-range 2..5", () =>
        {
            var r = PhysicsReach.Drop(body, placed, 4, new[] { Loose(LooseRole.Holds, -10, "loose", "g"), Loose(LooseRole.Holds, -7, "loose", "g"), floor }, RRLayers);
            return (r.Status == "dropped-range" && NearA7(r.Distance, 2, 1e-3) && NearA7(r.DistanceMax, 5, 1e-3), ShowD(r));
        });
        // RR-L4 guard: a movable body with no loose reading is no support (the rule off): dropped 95. Rejects a change to the rule-off path.
        Case(check, "rest-rule RR-L4: a movable body with no loose reading is no support - dropped 95 (rule off unchanged)", () =>
        {
            var r = PhysicsReach.Drop(body, placed, 4, new[] { Loose(LooseRole.None, -10), floor }, RRLayers);
            return (r.Status == "dropped" && r.SupportId == "floor" && NearA7(r.Distance, 95, 1e-3), ShowD(r));
        });

        // ---- part 5: lab 5's direction ----
        // RR-U1 one convex body through an open plate past its middle (plate z 0.5: down 4.85, up 5.85), landing on the floor (another
        // reference): the bound applies (lab 5 down verified). Rejects today's direction-not-read (no bound).
        Case(check, "rest-rule RR-U1: one convex body pushed down through an open plate, landing elsewhere - lower bound", () =>
        {
            var r = PhysicsReach.Drop(body, placed, 4, PlateAt(0.5f), RRLayers);
            return (r.PushedOff && r.Causes == PushCause.UpFacingDown && r.LowerBound && r.SupportId == "floor" && NearA7(r.Distance, 95, 1e-3), ShowD(r));
        });
        // RR-U2 two convex components (x -5..-1 and 1..5) through the same plate: not the measured shape: no bound. Rejects the bound
        // applied to a multi-component body (the G1 book).
        Case(check, "rest-rule RR-U2: a two-component body through the same plate - no bound", () =>
        {
            var two = Merge(ClosedBox(-5, -5, -5, -1, 5, 5), ClosedBox(1, -5, -5, 5, 5, 5));
            var r = PhysicsReach.Drop(two, SpatialOverlap.SoupBounds(two), 4, PlateAt(0.5f), RRLayers);
            return (r.PushedOff && (r.Causes & PushCause.UpFacingDown) != 0 && !r.LowerBound, ShowD(r));
        });
        // RR-U3 one convex body through a table top whose lower shelf (z -20, same reference) catches it: landing on the reference it
        // was pushed through: no bound (the closed table top lands it on its own other face). Rejects the bound on a same-reference landing.
        Case(check, "rest-rule RR-U3: pushed down through a top and landing on the same reference's shelf - no bound", () =>
        {
            var v = new[] { new Vector3(-50, -50, 0.5f), new Vector3(50, -50, 0.5f), new Vector3(50, 50, 0.5f), new Vector3(-50, 50, 0.5f),
                            new Vector3(-50, -50, -20), new Vector3(50, -50, -20), new Vector3(50, 50, -20), new Vector3(-50, 50, -20) };
            var sup = PhysicsReach.Supports("table", SupportMesh(v, new[] { 0, 1, 2, 0, 2, 3, 4, 5, 6, 4, 6, 7 }, 0.35f), new Vec3(0, 0, 0), new Vec3(0, 0, 0), 1f, movableBase: false)
                                  .Append(floor).ToArray();
            var r = PhysicsReach.Drop(body, placed, 4, sup, RRLayers);
            return (r.PushedOff && (r.Causes & PushCause.UpFacingDown) != 0 && r.SupportId == "table" && !r.LowerBound, ShowD(r));
        });
        // RR-T1 the reason text: a positive depth below 0.005 prints "under 0.01", others as before. Rejects "the reader's drop is 0 u"
        // for a drop of 0.002 u.
        Case(check, "rest-rule RR-T1: a 0.002 u depth prints 'under 0.01'; 0.28 and 0 print as before", () =>
        {
            string a = PhysicsReach.FormatDepth(0.002), b = PhysicsReach.FormatDepth(0.28), c = PhysicsReach.FormatDepth(0);
            return (a == "under 0.01" && b == "0.28" && c == "0", $"{a} | {b} | {c}");
        });
    }
}
