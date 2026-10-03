using System.Numerics;
using HousecarlCore;
using NiflySharp;
using NiflySharp.Blocks;
using NiflySharp.Enums;
using NiflySharp.Structs;

/// <summary>Known answers for the reach-region Part 3 rules besides the skin rule: (P) a particle emitter controller is not a
/// transform (the `particles` rule; nif.xml NiPSysEmitterCtlr / NiPSysModifierActiveCtlr), (C) a pose confirmation carrying
/// explicit node locals (a confirmed graph start-state pose, given as data), (T) the measured primitive default for a model-less
/// activator with no stated collision layer. Expected values are analytic, stated beside each case. Fixture controls hold on the
/// base; the rule cases are red until implemented (the APIs are stubs that throw). The canary is wrong by construction.</summary>
static partial class MotionProof
{
    /// <summary>The Stage A particle fixture ("Sparks", sphere emitter radius 5 on the root) with one controller sequence whose
    /// controlled block names the particle system node: controller type <paramref name="ctrlType"/>.</summary>
    static byte[] ParticleNifWithBlock(string ctrlType)
    {
        var f = Load(ParticleNif());
        var root = f.GetRootNodes().First();
        var seq = new NiControllerSequence { Name = new NiStringRef("Idle"), CycleType = CycleType.CYCLE_CLAMP, StartTime = 0, StopTime = 1, Frequency = 1, Weight = 1 };
        seq.ControlledBlocks = new List<ControlledBlock> { new() { NodeName = new NiStringRef("Sparks"), ControllerType = new NiStringRef(ctrlType), Interpolator = new(-1), Controller = new(-1) } };
        seq.NumControlledBlocks = 1;
        var mgr = new NiControllerManager(); int mgri = f.AddBlock(mgr);
        mgr.ControllerSequences ??= new(); mgr.ControllerSequences.AddBlockRef(f.AddBlock(seq)); mgr.NumControllerSequences = 1;
        root.Controller = new(mgri);
        return Save(f, "particles with a controlled block");
    }

    public static void RunPart3Rules(Action<string, bool, string> check)
    {
        // ================= Static support label (measured in the lab, one model) =================
        Case(check, "part3 K-S1: the Static-support label names the lab measurement, not an untested assumption", () =>
        {
            const string want = "not simulated: a Static base did not move in the lab (one model, crossbow: its loose twin fell and was pushed; beam model: no push acted)";
            return (PhysicsReach.StaticAssumptionLabel == want && !PhysicsReach.StaticAssumptionLabel.Contains("untested"), PhysicsReach.StaticAssumptionLabel);
        });

        // ================= load settle: one rule, data-driven exemptions =================
        // The list as shipped beside overlapprobe (kinds and values; evidence text is not asserted).
        var settleList = LoadSettle.Read("""
            [{"kind":"refRecordFlag","value":"0x20000000","evidence":"lab"},
             {"kind":"enabledAfterLoad","value":"true","evidence":"lab"}]
            """);
        // K-L1: a reference flagged Don't Havok Settle rests (exemption refRecordFlag). Rejects ignoring the flag mask.
        Case(check, "part3 K-L1: REFR flag 0x20000000 -> rests, by the refRecordFlag exemption", () =>
        {
            var d = LoadSettle.Decide(new SettleFacts(0x20000000u | 0x400u, "Crossbow", true), settleList);
            return (d.Status == "rests" && d.Exemption?.Kind == "refRecordFlag", $"{d}");
        });
        // K-L2: the weapon animation type is not a reader kind (B5c refuted it as the key: a graph model that is not a weapon rested,
        // and the bows are covered by the graph condition). A list naming it is refused by name. Rejects the kind still interpreted.
        Case(check, "part3 K-L2: a list naming weaponAnimationType is refused when read, naming the kind", () =>
        {
            try { LoadSettle.Read("""[{"kind":"weaponAnimationType","value":"Bow","evidence":"x"}]"""); return (false, "read"); }
            catch (ArgumentException e) { return (e.Message.Contains("unknown kind") && e.Message.Contains("weaponAnimationType"), e.Message); }
        });
        // K-L3: a Crossbow / a sword / a non-weapon, unflagged, enabled at load -> settles. Rejects exempting everything.
        Case(check, "part3 K-L3: Crossbow, OneHandSword and a non-weapon, unflagged and enabled at load -> settles", () =>
        {
            var a = LoadSettle.Decide(new SettleFacts(0x400u, "Crossbow", true), settleList);
            var b = LoadSettle.Decide(new SettleFacts(0u, "OneHandSword", true), settleList);
            var c = LoadSettle.Decide(new SettleFacts(0u, null, true), settleList);
            return (a.Status == "settles" && b.Status == "settles" && c.Status == "settles" && a.Exemption is null, $"{a} {b} {c}");
        });
        // K-L4: disabled at load, enabled by the judged state -> timing-unknown (exemption enabledAfterLoad). Rejects a hold
        // (the enable may have come before the cell loaded) and a plain drop.
        Case(check, "part3 K-L4: enabled by the judged state, disabled at load -> timing-unknown, by the enabledAfterLoad exemption", () =>
        {
            var d = LoadSettle.Decide(new SettleFacts(0u, "Crossbow", false), settleList);
            return (d.Status == "timing-unknown" && d.Exemption?.Kind == "enabledAfterLoad", $"{d}");
        });
        // K-L5: a property exemption outranks the timing one: a flagged reference enabled by the state still rests.
        Case(check, "part3 K-L5: a flagged reference enabled by the state rests (the property exemption first)", () =>
        {
            var d = LoadSettle.Decide(new SettleFacts(0x20000000u, null, false), settleList);
            return (d.Status == "rests" && d.Exemption?.Kind == "refRecordFlag", $"{d}");
        });
        // K-L6: an empty list exempts nothing (the list is the only source of exemptions).
        Case(check, "part3 K-L6: with no exemptions a Bow, a flagged and a state-enabled reference all settle", () =>
        {
            var none = Array.Empty<SettleExemption>();
            var a = LoadSettle.Decide(new SettleFacts(0x20000000u, "Bow", false), none);
            return (a.Status == "settles", $"{a}");
        });
        // K-L7: an unknown exemption kind fails loud.
        Case(check, "part3 K-L7: an unknown exemption kind is refused when the list is read", () =>
        {
            try { LoadSettle.Read("""[{"kind":"weight","value":"5","evidence":"x"}]"""); return (false, "read"); }
            catch (ArgumentException e) { return (e.Message.Contains("unknown kind"), e.Message); }
        });
        ReachOptions SettleOn(SettleDecision? d) => new(0.5, 65536, new ReachRules(Physics: true, BaseRecordType: "WEAP",
            Supports: _ => new[] { new DropSupport("floor", 1, true, false, Plate(500, -100)) }, Layers: RRLayers, Settle: d));
        var exFlag = settleList.First(e => e.Kind == "refRecordFlag");
        var exTime = settleList.First(e => e.Kind == "enabledAfterLoad");
        // K-L8: rests -> the placed box [-5,5]^3, the rests label, no drop. Rejects dropping an exempt item, an unlabelled hold.
        Case(check, "part3 K-L8: a resting free item keeps its placed box, labelled rests where placed, no drop", () =>
        {
            var r = MeshReach.OfModel(FreeItem(), Origin, Origin, 1f, null, SettleOn(new SettleDecision("rests", exFlag)));
            return (r.Status == "uncertified" && r.Labels.Contains(LoadSettle.RestsLabel) && !r.Labels.Contains(PhysicsReach.DropLabel)
                    && r.Drop is null && BoxIs(r.Box, -5, -5, -5, 5, 5, 5), ShowRR(r));
        });
        // K-L9: timing-unknown -> the prism from placed to landed (z -100..5), the drop recorded, the timing label.
        Case(check, "part3 K-L9: timing-unknown holds the placed and dropped poses (prism z -100..5), labelled, drop recorded", () =>
        {
            var r = MeshReach.OfModel(FreeItem(), Origin, Origin, 1f, null, SettleOn(new SettleDecision("timing-unknown", exTime)));
            return (r.Labels.Contains(LoadSettle.TimingUnknownLabel) && r.Drop is { } d && NearA7(d.Distance, 95, 1e-3)
                    && BoxIs(r.Box, -5, -5, -100, 5, 5, 5, 0.01f), ShowRR(r));
        });
        // K-L10: settles -> the drop (d 95) labelled with what it was measured on. Rejects an unlabelled settled drop.
        Case(check, "part3 K-L10: a settling free item drops (d 95) and carries the settles label", () =>
        {
            var r = MeshReach.OfModel(FreeItem(), Origin, Origin, 1f, null, SettleOn(new SettleDecision("settles", null)));
            return (r.Drop is { } d && NearA7(d.Distance, 95, 1e-3) && r.Labels.Contains(LoadSettle.SettlesLabel)
                    && !r.Labels.Contains(LoadSettle.RestsLabel), ShowRR(r));
        });
        // ---- the BSX exemption (B5: the one NIF field separating the four bows, 202, from the fallers, 203 and 194) ----
        IReadOnlyList<SettleExemption> BsxList() => LoadSettle.Read("""[{"kind":"bsxFlags","value":"set=0x8;clear=0x1","evidence":"lab"}]""");
        // K-L11: BSX 202 (Complex, not Animated) rests; 203 (Animated too) and 194 (no Complex) settle; no BSX settles.
        Case(check, "part3 K-L11: bsxFlags set=0x8;clear=0x1 -> 202 rests; 203, 194 and no BSX settle", () =>
        {
            var a = LoadSettle.Decide(new SettleFacts(0u, null, true, 202u), BsxList());
            var b = LoadSettle.Decide(new SettleFacts(0u, null, true, 203u), BsxList());
            var c = LoadSettle.Decide(new SettleFacts(0u, null, true, 194u), BsxList());
            var d = LoadSettle.Decide(new SettleFacts(0u, null, true, null), BsxList());
            return (a.Status == "rests" && a.Exemption?.Kind == "bsxFlags" && b.Status == "settles" && c.Status == "settles" && d.Status == "settles", $"{a} {b} {c} {d}");
        });
        byte[] FreeItemBsx(uint v)
        {
            var f = Load(FreeItem()); var root = f.GetRootNodes().First();
            root.ExtraDataList ??= new(); root.ExtraDataList.AddBlockRef(f.AddBlock(new BSXFlags { Name = new NiStringRef("BSX"), IntegerData = v }));
            root.NumExtraDataList = (uint)root.ExtraDataList.Count;
            return Save(f, $"free item with BSX {v}");
        }
        ReachOptions FactsOn(IReadOnlyList<SettleExemption> l) => new(0.5, 65536, new ReachRules(Physics: true, BaseRecordType: "WEAP",
            Supports: _ => new[] { new DropSupport("floor", 1, true, false, Plate(500, -100)) }, Layers: RRLayers,
            SettleFacts: new SettleFacts(0u, null, true), SettleExemptions: l));
        // K-L12: the reader takes the BSX from the model: BSX 202 rests at its placed box, labelled, no drop.
        Case(check, "part3 K-L12: the model's own BSX 202 is read: rests where placed, labelled, no drop", () =>
        {
            var r = MeshReach.OfModel(FreeItemBsx(202), Origin, Origin, 1f, null, FactsOn(BsxList()));
            return (r.Labels.Contains(LoadSettle.RestsLabel) && r.Drop is null && BoxIs(r.Box, -5, -5, -5, 5, 5, 5), ShowRR(r));
        });
        // K-L13: the same with BSX 203 settles: dropped 95 u, labelled settles. Rejects reading "Complex" alone.
        Case(check, "part3 K-L13: the model's own BSX 203 is read: settles, dropped (d 95)", () =>
        {
            var r = MeshReach.OfModel(FreeItemBsx(203), Origin, Origin, 1f, null, FactsOn(BsxList()));
            return (r.Drop is { } d && NearA7(d.Distance, 95, 1e-3) && r.Labels.Contains(LoadSettle.SettlesLabel), ShowRR(r));
        });
        // fixture control: the fixture's BSX block round-trips (holds on the base)
        Case(check, "part3 fixture control: the BSX fixture carries one BSXFlags block with the value given", () =>
        {
            var n = Load(FreeItemBsx(202)); var x = n.Blocks.OfType<BSXFlags>().ToList();
            return (x.Count == 1 && x[0].IntegerData == 202, $"{x.Count} {x.FirstOrDefault()?.IntegerData}");
        });

        // ---- the graph condition (B5b: Animated causal on one bow; a graph separates the bows from the 202 sword) ----
        IReadOnlyList<SettleExemption> GraphList() => LoadSettle.Read("""[{"kind":"bsxFlags","value":"graph=present;clear=0x1","evidence":"lab"}]""");
        // K-L14: graph + 202 rests; graph + 203 settles; no graph + 202 settles; unknown graph settles.
        Case(check, "part3 K-L14: bsxFlags graph=present;clear=0x1 -> graph+202 rests; graph+203, no graph+202 and unknown settle", () =>
        {
            var a = LoadSettle.Decide(new SettleFacts(0u, null, true, 202u, true), GraphList());
            var b = LoadSettle.Decide(new SettleFacts(0u, null, true, 203u, true), GraphList());
            var c = LoadSettle.Decide(new SettleFacts(0u, null, true, 202u, false), GraphList());
            var d = LoadSettle.Decide(new SettleFacts(0u, null, true, 202u, null), GraphList());
            return (a.Status == "rests" && b.Status == "settles" && c.Status == "settles" && d.Status == "settles", $"{a} {b} {c} {d}");
        });
        byte[] FreeItemGraph(uint bsx, bool graph)
        {
            var f = Load(FreeItemBsx(bsx)); var root = f.GetRootNodes().First();
            if (graph)
            {
                root.ExtraDataList.AddBlockRef(f.AddBlock(new BSBehaviorGraphExtraData { Name = new NiStringRef("BGED"), BehaviourGraphFile = new NiStringRef(@"Weapons\Lab\Lab.hkx") }));
                root.NumExtraDataList = (uint)root.ExtraDataList.Count;
            }
            return Save(f, $"free item BSX {bsx} graph {graph}");
        }
        // K-L15: the reader reads the graph from the model: BSX 202 with a graph rests; BSX 202 without one settles (dropped d 95).
        Case(check, "part3 K-L15: the model's graph is read: BSX 202 + graph rests (no drop); BSX 202, no graph, drops d 95", () =>
        {
            // the fixture's graph file is not in the fixture, so its motion refuses; a stored-pose confirmation for these exact bytes
            // stands in for the confirmed in-game look the lab's graph models have, and lets the physics rule decide
            byte[] ga = FreeItemGraph(202, true), gb = FreeItemGraph(202, false);
            ReachOptions Confirmed(byte[] nif) => new(0.5, 65536, FactsOn(GraphList()).Rules! with
            {
                Confirmations = new[] { new PoseConfirmation(@"meshes\lab\Graph.nif", Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(nif)).ToLowerInvariant(), "lab") },
                ModelPath = @"meshes\lab\Graph.nif",
            });
            var a = MeshReach.OfModel(ga, Origin, Origin, 1f, null, Confirmed(ga));
            var b = MeshReach.OfModel(gb, Origin, Origin, 1f, null, Confirmed(gb));
            return (a.Labels.Contains(LoadSettle.RestsLabel) && a.Drop is null && b.Drop is { } d && NearA7(d.Distance, 95, 1e-3), ShowRR(a) + " | " + ShowRR(b));
        });

        // fixture control: no decision (the list not given) is today's drop, with no settle label (holds on the base)
        Case(check, "part3 fixture control: with no settle decision the free item drops as before (d 95), no settle labels", () =>
        {
            var r = MeshReach.OfModel(FreeItem(), Origin, Origin, 1f, null, SettleOn(null));
            return (r.Drop is { } d && NearA7(d.Distance, 95, 1e-3) && !r.Labels.Contains(LoadSettle.SettlesLabel) && !r.Labels.Contains(LoadSettle.RestsLabel), ShowRR(r));
        });

        // ================= canary =================
        Case(check, "part3 CANARY (must FAIL): a CollisionMarker box stating L_STATIC is not solid", () => (false, "the lab held an item on it"));

        byte[] emitterCtl = ParticleNifWithBlock("NiPSysEmitterCtlr");
        byte[] transformCtl = ParticleNifWithBlock("NiTransformController");
        // ================= fixture controls (existing code; hold on the base) =================
        Case(check, "part3 fixture control: rules off, an emitter-controller block on the particle node refuses as 'node chain is animated' (today's reading)", () =>
        {
            var b = ParticleReach.Bounds(emitterCtl).Single();
            return (b.Status == "refused" && (b.Reason ?? "").Contains("node chain is animated", StringComparison.Ordinal), $"{b.Status} {b.Reason}");
        });
        Case(check, "part3 fixture control: the fixture's blocks round-trip (one sequence, node 'Sparks', the controller type given)", () =>
        {
            var f = Load(emitterCtl); var cb = f.Blocks.OfType<NiControllerSequence>().Single().ControlledBlocks.Single();
            return (cb.NodeName?.ToString() == "Sparks" && cb.ControllerType?.ToString() == "NiPSysEmitterCtlr", $"{cb.NodeName} {cb.ControllerType}");
        });

        // ================= K-PA: an emitter controller is not a transform =================
        // With transformOnly, the emitter-controller fixture reads `modelled`, the same bound as the fixture without the block
        // (D = v T + a T^2/2 with v 25, T 2.5, gravity 10 -> 93.75); the transform-controller fixture still refuses. Rejects the
        // fix applied to transform blocks too, and the fix not applied.
        Case(check, "part3 K-PA1: transformOnly — an NiPSysEmitterCtlr block on the particle node reads modelled with the unblocked fixture's displacement", () =>
        {
            var b = ParticleReach.Bounds(emitterCtl, transformOnly: true).Single();
            var plain = ParticleReach.Bounds(ParticleNif()).Single();
            return (b.Status == "modelled" && NearA7(b.Displacement, plain.Displacement, 1e-6), $"{b.Status} {b.Reason} D {b.Displacement} plain D {plain.Displacement}");
        });
        Case(check, "part3 K-PA2: transformOnly — an NiTransformController block on the particle node still refuses (a moving emitter)", () =>
        {
            var b = ParticleReach.Bounds(transformCtl, transformOnly: true).Single();
            return (b.Status == "refused" && (b.Reason ?? "").Contains("animated", StringComparison.Ordinal), $"{b.Status} {b.Reason}");
        });
        Case(check, "part3 K-PA3: MeshReach with the particles rule — the emitter-controller fixture is uncertified (particle model), not refused", () =>
        {
            var r = MeshReach.OfModel(emitterCtl, Origin, Origin, 1f, null, new ReachOptions(0.5, 4096, new ReachRules(Particles: true)));
            return (r.Status == "uncertified" && r.Labels.Contains(MeshReach.ParticleModelLabel), ShowRR(r));   // the rule reads particle_model_v2 (K-PM9)
        });

        // ================= K-C4: a confirmed start-state pose given as node locals =================
        // The inactive-controller rig (Part [0,10]^3 under node Mover at the origin; Frame [-200,-180]x[-200,-180]x[-40,-20]) with a
        // confirmation whose locals put Mover at (100,0,0): the region is Part at [100,110]x[0,10]x[0,10] plus Frame ->
        // [-200,110]x[-200,10]x[-40,10], labelled the start-state pose. Rejects the locals ignored (stored pose, max x 10).
        byte[] inactive = Rig(Box(0, 0, 0, 10, 10, 10), Vector3.Zero, Slide(new(100, 0, 0)), direct: true, directActive: false);
        string sha = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(inactive)).ToLowerInvariant();
        var locals = new Dictionary<string, PoseLocal> { ["Mover"] = new PoseLocal(new Vector3(100, 0, 0), Matrix4x4.Identity, 1f) };
        Case(check, "part3 K-C4: a confirmation with node locals poses the confirmed bytes there — box [-200,110]x[-200,10]x[-40,10], '" + MeshReach.StartStatePoseLabel + "'", () =>
        {
            var conf = new[] { new PoseConfirmation(@"meshes\fx\Rig.nif", sha, "evidence/rig", locals, MeshReach.StartStatePoseLabel) };
            var r = MeshReach.OfModel(inactive, Origin, Origin, 1f, null, new ReachOptions(0.5, 4096, new ReachRules(Confirmations: conf, ModelPath: @"meshes\fx\Rig.nif")));
            return (r.Status == "uncertified" && r.Labels.Contains(MeshReach.StartStatePoseLabel) && !r.Labels.Contains(MeshReach.ConfirmedPoseLabel)
                    && BoxIs(r.Box, -200, -200, -40, 110, 10, 10), ShowRR(r));
        });
        Case(check, "part3 K-C5: node locals on a confirmation whose sha256 does not match change nothing — still refused", () =>
        {
            var conf = new[] { new PoseConfirmation(@"meshes\fx\Rig.nif", new string('0', 64), "evidence/rig", locals, MeshReach.StartStatePoseLabel) };
            var r = MeshReach.OfModel(inactive, Origin, Origin, 1f, null, new ReachOptions(0.5, 4096, new ReachRules(Confirmations: conf, ModelPath: @"meshes\fx\Rig.nif")));
            return (r.Status == "refused", ShowRR(r));
        });

        // ================= K-T: the primitive rule (lab: an ACTI box, XTRI absent or 1, passed an item and the player) =================
        Case(check, "part3 K-T1: a model-less Activator with no stated layer -> no_geometry, the measured reason", () =>
        {
            var r = MeshReach.OfPrimitiveRule("Activator", false, null, "REFUSAL");
            return (r.Status == "no_geometry" && r.Reason == MeshReach.PrimitiveActivatorReason, SayA7(r));
        });
        Case(check, "part3 K-T2: an Activator WITH a model, a stated layer, or another base type keeps the caller's refusal", () =>
        {
            var a = MeshReach.OfPrimitiveRule("Activator", true, null, "REFUSAL");
            var b = MeshReach.OfPrimitiveRule("Activator", false, 1, "REFUSAL");
            var c = MeshReach.OfPrimitiveRule("Static", false, null, "REFUSAL");
            return (new[] { a, b, c }.All(x => x.Status == "refused" && x.Reason == "REFUSAL"), $"{SayA7(a)} | {SayA7(b)} | {SayA7(c)}");
        });
    }
}
