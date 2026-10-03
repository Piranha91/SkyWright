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
            return (r.Status == "uncertified" && r.Labels.Contains(MeshReach.ParticleLabel), ShowRR(r));
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
