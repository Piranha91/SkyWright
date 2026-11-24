using System.Numerics;
using HousecarlCore;
using SkyWright.Engine;

/// <summary>The reach reader keeps a refused loose item's drop (2026-10-14): a body whose first contact is a loose item that itself moves
/// (or loose items resting on each other) is refused by name, and its row keeps the placed box, the support met first and the
/// first-contact distance that the drop already read; a pushed-off body that is not impaled carries the drop's two push depths.
/// Expected values are analytic (stated per case).</summary>
static partial class MotionProof
{
    public static void RunReachDrop(Action<string, bool, string> check)
    {
        static string Show(ReachRegion r) => $"{r.Status} [{r.Reason}] drop " +
            (r.Drop is { } d ? $"{d.Status} d {d.Distance:0.###} placed {d.Placed} landed {d.Landed} support {d.SupportId} push {d.Push} {d.PushDepth:0.###}/{d.PushUpDepth:0.###}" : "-");
        ReachOptions Settles(IEnumerable<DropSupport> s) => new(0.5, 65536, new ReachRules(Physics: true, BaseRecordType: "MISC",
            Supports: _ => s, Layers: RRLayers, Settle: new SettleDecision("settles", null), Rest: true));
        var floor = new DropSupport("floor", 1, true, false, Plate(500, -100));
        // a loose 40 u box with its top at -10 under the item [-5,5]^3: first contact after 5 u
        DropSupport Loose(LooseRole role) => new("loose", 4, true, true, ClosedBox(-20, -20, -30, 20, 20, -10), Loose: role);
        IReadOnlyList<DropSupport> PlateAt(float z) => PhysicsReach.Supports("plate", SupportMeshPlate(50f, 0.35f), new Vec3(0, 0, z), new Vec3(0, 0, 0), 1f, movableBase: false)
                                                                    .Append(floor).ToArray();

        // RD-1 the item over a loose box that moves at load: refused by name, and the row keeps the placed box [-5,5]^3, the support
        // "loose" and the first-contact distance 5 (landed box z -10..0). Rejects a refusal that drops the drop.
        Case(check, "reach-drop RD-1: lands on a moving loose item - refused, keeps placed box, support 'loose', distance 5", () =>
        {
            var r = MeshReach.OfModel(FreeItem(), Origin, Origin, 1f, null, Settles(new[] { Loose(LooseRole.Moves), floor }));
            return (r.Status == "refused" && r.Reason == PhysicsReach.LandsOnMovingLooseReason
                    && r.Drop is { SupportId: "loose" } d && BoxIs(d.Placed, -5, -5, -5, 5, 5, 5) && NearA7(d.Distance, 5)
                    && BoxIs(d.Landed, -5, -5, -10, 5, 5, 0), Show(r));
        });
        // RD-2 the same over a loose box in a cycle: refused by the cycle reason, same kept drop. Rejects keeping it for one reason only.
        Case(check, "reach-drop RD-2: lands on a loose item in a cycle - refused, keeps placed box, support 'loose', distance 5", () =>
        {
            var r = MeshReach.OfModel(FreeItem(), Origin, Origin, 1f, null, Settles(new[] { Loose(LooseRole.Cycle), floor }));
            return (r.Status == "refused" && r.Reason == PhysicsReach.LooseCycleReason
                    && r.Drop is { SupportId: "loose" } d && BoxIs(d.Placed, -5, -5, -5, 5, 5, 5) && NearA7(d.Distance, 5), Show(r));
        });
        // RD-3 a non-impaled push: the item past the middle of an up-facing plate at z 0.5 (slab 0.15..0.85): pushed down through it,
        // out down 4.85, out up 5.85 (drop-refusal L2-7's reading). The row carries both depths. Rejects NaN depths on a non-impaled push.
        Case(check, "reach-drop RD-3: a non-impaled pushed-off item carries its push depths (4.85 down / 5.85 up)", () =>
        {
            var r = MeshReach.OfModel(FreeItem(), Origin, Origin, 1f, null, Settles(PlateAt(0.5f)));
            return (r.Status == "refused" && r.Drop is { Push: PushCause.UpFacingDown } d
                    && NearA7(d.PushDepth, 4.85) && NearA7(d.PushUpDepth, 5.85), Show(r));
        });
        // RD-4 the row carries what the drop read, for every pushed-off cause in drop-refusal (hull side, ceiling, hull down, up-facing):
        // the row's depths equal the DropResult's (NaN where the drop has none). Rejects a depth invented or lost between drop and row.
        Case(check, "reach-drop RD-4: a pushed-off row's depths equal its drop's, across the four non-impaled causes", () =>
        {
            var cv = new[] { new Vector3(-50, -50, 5.2f), new Vector3(50, -50, 5.2f), new Vector3(50, 50, 5.2f), new Vector3(-50, 50, 5.2f) };
            var sets = new (string Name, IReadOnlyList<DropSupport> S)[]
            {
                ("hull side", new[] { new DropSupport("wall", 1, true, false, ClosedBox(4.5f, -52, -62, 28.5f, 52, 42)), floor }),
                ("ceiling", PhysicsReach.Supports("ceil", SupportMesh(cv, new[] { 0, 2, 1, 0, 3, 2 }, 0.35f), new Vec3(0, 0, 0), new Vec3(0, 0, 0), 1f, movableBase: false).Append(floor).ToArray()),
                ("hull down", new[] { new DropSupport("lid", 1, true, false, ClosedBox(-20, -20, 4.8f, 20, 20, 30)), floor }),
                ("up-facing", PlateAt(0.5f)),
            };
            var body = ClosedBox(-5, -5, -5, 5, 5, 5);
            bool ok = true; var detail = new List<string>();
            static bool Same(double a, double b) => (double.IsNaN(a) && double.IsNaN(b)) || NearA7(a, b, 1e-6);
            foreach (var (name, s) in sets)
            {
                var dr = PhysicsReach.Drop(body, SpatialOverlap.SoupBounds(body), 4, s, RRLayers);
                var r = MeshReach.OfModel(FreeItem(), Origin, Origin, 1f, null, Settles(s));
                ok &= dr.PushedOff && (dr.Causes & PushCause.Impaled) == 0 && r.Drop is { } d && Same(d.PushDepth, dr.PushDepth) && Same(d.PushUpDepth, dr.PushUpDepth);
                detail.Add($"{name}: drop {dr.PushDepth:0.###}/{dr.PushUpDepth:0.###} row {r.Drop?.PushDepth:0.###}/{r.Drop?.PushUpDepth:0.###}");
            }
            return (ok, string.Join(" | ", detail));
        });
        // RD-5 nothing below under plain physics (no rest rule): refused with no drop, as before. Rejects widening the kept drop to a
        // refusal that read no support.
        Case(check, "reach-drop RD-5: nothing below under plain physics - refused, no drop kept (unchanged)", () =>
        {
            var r = MeshReach.OfModel(FreeItem(), Origin, Origin, 1f, null, RulesOn(physics: true, baseType: "MISC", supports: Array.Empty<DropSupport>()));
            return (r.Status == "refused" && r.Reason == PhysicsReach.NothingBelow && r.Drop is null, Show(r));
        });
    }
}
