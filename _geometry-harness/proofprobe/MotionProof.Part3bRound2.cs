using System.Numerics;
using HousecarlCore;
using NiflySharp;
using NiflySharp.Blocks;
using NiflySharp.Enums;
using NiflySharp.Structs;

/// <summary>Known answers for Part 3b round 2: particle_model_v3 (the Gamebryo SDK laws: gravity x 1.6, turbulence 500 x Turbulence x
/// Scale per axis, drag per 1/30 s, a 1/30 s explicit-Euler drift, controller keys), the settled-gravity fold (SF), the support-plane
/// term with the Gate B review's F1 condition (SP), and the wiring the round-1 sweep found unseen (W4 path subtree radius, W6 moving
/// emitter swept over every pose, W7 the system node's own displacement). Fixture (ParticleNif): sphere emitter radius 5 on the root,
/// Speed 20 +/- 5 (v 25), Life Span 2 +/- 0.5 (T 2.5), Initial Radius 1 (r sqrt 2), planar gravity 10 along the root's -z.
/// v T + r = 63.91421; T^2/2 = 3.125; the drift term T dt / 2 = 2.5 / 60 = 0.0416667. Each case names the wrong answer it rejects.</summary>
static partial class MotionProof
{
    const double R2Iso = 62.5 + Sqrt2, R2Lat = 5 + R2Iso, R2Q = 3.125 + 2.5 / 60.0;

    static byte[] ParticleWithStrengthCtlr(float[] ownKeys, float[]? seqKeys)
    {
        var f = Load(ParticleNif()); var ps = f.Blocks.OfType<NiParticleSystem>().Single(); var root = f.GetRootNodes().First();
        int FloatInterp(float[] keys)
        {
            var fd = new NiFloatData { Data = new KeyGroup<float> { Interpolation = KeyType.LINEAR_KEY, Keys = keys.Select((v, i) => new Key<float> { Time = i * 0.5f, Value = v }).ToList(), NumKeys = (uint)keys.Length } };
            return f.AddBlock(new NiFloatInterpolator { Value = float.MinValue, Data = new(f.AddBlock(fd)) });
        }
        var ctl = new NiPSysGravityStrengthCtlr { ModifierName = new NiStringRef("Gravity"), Interpolator = new(FloatInterp(ownKeys)), StartTime = 0, StopTime = 1, Frequency = 1, NextController = new(-1) };
        ctl.Target = new(f.Blocks.IndexOf(ps)); ps.Controller = new(f.AddBlock(ctl));
        if (seqKeys is not null)
        {
            var seq = new NiControllerSequence { Name = new NiStringRef("Puff"), CycleType = CycleType.CYCLE_CLAMP, StartTime = 0, StopTime = 1, Frequency = 1, Weight = 1 };
            seq.ControlledBlocks = new List<ControlledBlock> { new() { NodeName = new NiStringRef("Sparks"), ControllerType = new NiStringRef("NiPSysGravityStrengthCtlr"), ControllerID = new NiStringRef("Gravity"), Interpolator = new(FloatInterp(seqKeys)), Controller = new(-1) } };
            seq.NumControlledBlocks = 1;
            var mgr = new NiControllerManager(); int mgri = f.AddBlock(mgr);
            mgr.ControllerSequences ??= new(); mgr.ControllerSequences.AddBlockRef(f.AddBlock(seq)); mgr.NumControllerSequences = 1;
            root.Controller = new(mgri);
        }
        return Save(f, "strength controller");
    }

    /// <summary>A closed box soup (12 triangles).</summary>
    static TriangleSoup ClosedBox(float x0, float y0, float z0, float x1, float y1, float z1)
    {
        var v = new[] { new Vector3(x0, y0, z0), new Vector3(x1, y0, z0), new Vector3(x1, y1, z0), new Vector3(x0, y1, z0),
                        new Vector3(x0, y0, z1), new Vector3(x1, y0, z1), new Vector3(x1, y1, z1), new Vector3(x0, y1, z1) };
        var i = new[] { 0, 2, 1, 0, 3, 2, 4, 5, 6, 4, 6, 7, 0, 1, 5, 0, 5, 4, 1, 2, 6, 1, 6, 5, 2, 3, 7, 2, 7, 6, 3, 0, 4, 3, 4, 7 };
        return new TriangleSoup(v, i);
    }

    /// <summary>The particle fixture with a mover: node "Mover" translated x 0 -> 100 over 1 s (linear keys, sequence "Open"); the
    /// emitter object is the mover (<paramref name="emitterOnMover"/>) or the particle system node hangs under it.</summary>
    static byte[] MovingParticleNif(bool emitterOnMover)
    {
        var data = new NiTransformData { RotationType = KeyType.LINEAR_KEY, NumRotationKeys = 0, Translations = VG(KeyType.LINEAR_KEY, (0f, new Vector3(0, 0, 0)), (1f, new Vector3(100, 0, 0))), Scales = NoKeys() };
        return Rig(Box(-1, -1, -1, 1, 1, 1), Vector3.Zero, data, extra: (f, root) =>
        {
            var mover = f.Blocks.OfType<NiNode>().Single(n => n.Name?.String == "Mover");
            int moverIndex = f.Blocks.IndexOf(mover), rootIndex = f.Blocks.IndexOf(root);
            var psys = new NiParticleSystem { Name = new NiStringRef("Sparks"), Flags_ui = 0xE, Scale = 1f, Rotation = Identity33(), WorldSpace = true };
            int psysIndex = f.AddBlock(psys);
            if (emitterOnMover) root.Children.AddBlockRef(psysIndex); else mover.Children.AddBlockRef(psysIndex);
            psys.Data_NiBlockRef_NPSD = new(f.AddBlock(new NiPSysData { NumParticles = 0 }));
            psys.Modifiers ??= new();
            void Add(NiPSysModifier m) { m.Target = new(psysIndex); m.Active = true; psys.Modifiers.AddBlockRef(f.AddBlock(m)); }
            Add(new NiPSysAgeDeathModifier { Name = new NiStringRef("Age") });
            Add(new NiPSysSphereEmitter { Name = new NiStringRef("Emitter"), Radius = 5f, Speed = 20f, SpeedVariation = 5f, LifeSpan = 2f, LifeSpanVariation = 0.5f,
                                          InitialRadius = 1f, EmitterObject = new(emitterOnMover ? moverIndex : rootIndex) });
            Add(new NiPSysPositionModifier { Name = new NiStringRef("Position") });
            psys.NumModifiers = (uint)psys.Modifiers.Count;
        });
    }

    /// <summary>A node "Bird" on the root driven by a direct transform controller with a NiPathInterpolator (linear keys (0,0,0) ->
    /// (100,0,0)), its shape a 4 u cube centred 50 u above the node: the path hull is flat (z 0); only the subtree radius reaches z 52.</summary>
    static byte[] PathNif()
    {
        var f = new NifFile(); f.Create(Ver, withRootNode: true);
        var root = f.GetRootNodes().First(); root.Flags_ui = 0xE;
        var bird = new NiNode { Name = new NiStringRef("Bird"), Flags_ui = 0xE, Scale = 1f, Rotation = Identity33() };
        int bi = f.AddBlock(bird); root.Children.AddBlockRef(bi);
        bird.Children.AddBlockRef(f.AddBlock(Shape("Body", Box(-2, -2, 48, 2, 2, 52))));
        var pos = new NiPosData { Data = VG(KeyType.LINEAR_KEY, (0f, new Vector3(0, 0, 0)), (1f, new Vector3(100, 0, 0))) };
        var pi = new NiPathInterpolator { PathData = new(f.AddBlock(pos)), PercentData = new(-1) };
        int pii = f.AddBlock(pi);
        var ctl = new NiTransformController { Interpolator = new(pii), StartTime = 0, StopTime = 1, Frequency = 1 };
        ctl.Flags.Active = true; ctl.Target = new(bi); bird.Controller = new(f.AddBlock(ctl));
        return Save(f, "path");
    }

    public static void RunPart3bRound2(Action<string, bool, string> check)
    {
        // ================= v3: the SDK laws =================
        // G1: gravity x 1.6 with the drift: z half = 68.91421 + 16 (3.125 + 0.0416667) = 119.58088; x, y keep 68.91421 (no acceleration
        // on them, so no drift). Rejects the 1.0 factor (z 100.58) and the drift dropped (z 118.91).
        Case(check, "r2 K-V3G1: planar gravity x 1.6 with the Euler drift - x, y half 68.91421, z half 119.58088", () =>
        {
            var p = ParticleReach.BoundsFromModifiers(ParticleNif()).Single();
            return (p.Status == "modelled" && HalfIs(p.ModelBox, R2Lat, R2Lat, R2Lat + 16 * R2Q), ShowPB(p));
        });
        // K1: a strength controller on the system's own chain with keys [10, 25]: the largest key, x 1.6: z half 68.91421 + 40 x 3.1666667
        // = 195.58088. Rejects the field alone (119.58).
        Case(check, "r2 K-V3K1: own-chain strength keys [10, 25] - z half 195.58088", () =>
        {
            var p = ParticleReach.BoundsFromModifiers(ParticleWithStrengthCtlr(new[] { 10f, 25f }, null)).Single();
            return (p.Status == "modelled" && HalfIs(p.ModelBox, R2Lat, R2Lat, R2Lat + 40 * R2Q), ShowPB(p));
        });
        // K1b: a sequence block naming the system and the modifier with keys up to 30: z half 68.91421 + 48 x 3.1666667 = 220.91421.
        // Rejects the own chain read alone (195.58).
        Case(check, "r2 K-V3K1b: a sequence's strength keys up to 30 - z half 220.91421", () =>
        {
            var p = ParticleReach.BoundsFromModifiers(ParticleWithStrengthCtlr(new[] { 10f, 25f }, new[] { 5f, 30f, 10f })).Single();
            return (p.Status == "modelled" && HalfIs(p.ModelBox, R2Lat, R2Lat, R2Lat + 48 * R2Q), ShowPB(p));
        });
        // K2: an unknown particle-system controller in a sequence refuses naming its type; a birth-rate controller does not.
        Case(check, "r2 K-V3K2: an unknown particle controller refuses by name; an emitter (birth-rate) controller does not", () =>
        {
            var u = ParticleReach.BoundsFromModifiers(ParticleNifWithBlock("NiPSysFieldMagnitudeCtlr")).Single();
            var e = ParticleReach.BoundsFromModifiers(ParticleNifWithBlock("NiPSysEmitterCtlr")).Single();
            return (u.Status == "refused" && (u.Reason ?? "").Contains("NiPSysFieldMagnitudeCtlr") && e.Status == "modelled", $"{ShowPB(u)}; {ShowPB(e)}");
        });
        // T1: turbulence 0.1, scale 1: A = 50 per axis: x half 68.91421 + 50 x 3.1666667 = 227.24755; z 119.58088 + 158.33333 = 277.91421.
        // Rejects the refusal (round 1), A = Turbulence x Scale (x 69.2) and turbulence on one axis only.
        Case(check, "r2 K-V3T1: turbulence 0.1 x 1 -> A 50 per axis: x half 227.24755, z half 277.91421", () =>
        {
            var p = ParticleReach.BoundsFromModifiers(ParticleNif(turbulence: 0.1f)).Single();
            return (p.Status == "modelled" && HalfIs(p.ModelBox, R2Lat + 50 * R2Q, R2Lat + 50 * R2Q, R2Lat + 16 * R2Q + 50 * R2Q), ShowPB(p));
        });
        // T2: the system node turned 45 deg about z: the turbulence cube's extent on model x and y is A (|cos| + |sin|) = 70.710678; z A.
        // x half 68.91421 + 70.710678 x 3.1666667 = 292.83136. Rejects the system frame ignored (227.25).
        Case(check, "r2 K-V3T2: turbulence in a system frame turned 45 deg about z - x, y half 292.83136, z 277.91421", () =>
        {
            var b = ParticleVariant((f, ps, root) =>
            {
                float c = MathF.Sqrt(0.5f);
                ps.Rotation = new Matrix33 { M11 = c, M12 = -c, M13 = 0, M21 = c, M22 = c, M23 = 0, M31 = 0, M32 = 0, M33 = 1 };
                foreach (var g in f.Blocks.OfType<NiPSysGravityModifier>()) { g.Turbulence = 0.1f; g.TurbulenceScale = 1f; }
            }, "turned system with turbulence");
            var p = ParticleReach.BoundsFromModifiers(b).Single();
            double a = 50 * Math.Sqrt(2);
            return (p.Status == "modelled" && HalfIs(p.ModelBox, R2Lat + a * R2Q, R2Lat + a * R2Q, R2Lat + 16 * R2Q + 50 * R2Q, 2e-2), ShowPB(p));
        });
        // D1: drag 0.5 on x and y per 1/30 s: k = 15 /s: x half = 5 + 25 (1 - e^-37.5) / 15 + 1.41421 = 8.08088; z 119.58088. Rejects the
        // per-second reading (42.09).
        Case(check, "r2 K-V3D1: drag 0.5 per 1/30 s on x and y - x, y half 8.08088", () =>
        {
            var b = ParticleVariant((f, ps, root) =>
            {
                foreach (var ax in new[] { Vector3.UnitX, Vector3.UnitY })
                    AddModifier(f, ps, new NiPSysDragModifier { Name = new NiStringRef("Drag"), DragObject = new(f.Blocks.IndexOf(root)), DragAxis = ax, Percentage = 0.5f, Range = float.MaxValue, RangeFalloff = float.MaxValue });
            }, "drag v3");
            var p = ParticleReach.BoundsFromModifiers(b).Single();
            double lat = 5 + 25 * (1 - Math.Exp(-37.5)) / 15 + Sqrt2;
            return (p.Status == "modelled" && HalfIs(p.ModelBox, lat, lat, R2Lat + 16 * R2Q), ShowPB(p));
        });

        // ================= SF: the settled-gravity fold =================
        // S1: settled toward model -z: fall = 5 + 62.5 + 1.41421 + 16 (3.125 + 0.0416667) = 119.58088; climb = 5 + v^2 / (2 x 10) + r
        // = 5 + 31.25 + 1.41421 = 37.66421 (the weaker pull, x 1.0). Rejects the symmetric box (z max 119.58) and the 1.6 factor in the
        // climb (25.95).
        Case(check, "r2 K-SF1: settled toward -z - z [-119.58088, 37.66421], x, y half 68.91421", () =>
        {
            var p = ParticleReach.BoundsFromModifiers(ParticleNif(), null, _ => new Vector3(0, 0, -1)).Single();
            var b = p.ModelBox;
            return (p.Status == "modelled" && p.Fold is not null && NearA7(b.Min.Z, -(R2Lat + 16 * R2Q)) && NearA7(b.Max.Z, 5 + 31.25 + Sqrt2)
                    && NearA7(b.Max.X, R2Lat) && NearA7(-b.Min.Y, R2Lat), ShowPB(p));
        });
        // S2: a settled direction off the model axes does not fold (both readings kept): z half 119.58088, no fold.
        Case(check, "r2 K-SF2: a settled direction off the model axes - no fold, z half 119.58088", () =>
        {
            var p = ParticleReach.BoundsFromModifiers(ParticleNif(), null, _ => Vector3.Normalize(new Vector3(0.6f, 0, -0.8f))).Single();
            return (p.Status == "modelled" && p.Fold is null && HalfIs(p.ModelBox, R2Lat, R2Lat, R2Lat + 16 * R2Q), ShowPB(p));
        });
        // S3: through MeshReach: an entry for these exact bytes settles it (label names the evidence); a different sha does not.
        byte[] pn = ParticleNif();
        string sha = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(pn)).ToLowerInvariant();
        ReachRegion SR(string sh, Func<Aabb, IEnumerable<DropSupport>>? sup = null) => MeshReach.OfModel(pn, Origin, Origin, 1f, null,
            new ReachOptions(0.5, 4096, new ReachRules(Particles: true, ModelPath: "meshes\\t\\p.nif", Supports: sup,
                Gravity: new[] { new SettledGravity("Meshes/t/P.nif", sh, Array.Empty<string>(), true, "frames F1, F2") })));
        Case(check, "r2 K-SF3: MeshReach with a settled entry for these bytes - z max 37.66421, labelled with the evidence; another sha - z max 119.58088", () =>
        {
            var r = SR(sha); var o = SR(new string('0', 64));
            return (r.Status == "uncertified" && NearA7(r.Box.Max.Z, 5 + 31.25 + Sqrt2, 1e-2) && r.Labels.Any(l => l.StartsWith(MeshReach.SettledGravityLabelPrefix) && l.Contains("frames F1, F2"))
                    && NearA7(o.Box.Max.Z, R2Lat + 16 * R2Q, 1e-2), $"{ShowRR(r)} || {ShowRR(o)}");
        });

        // ================= SP: the support-plane term with F1's condition =================
        // P1: a thick body (closed box z -1000..-10, +-2000): its surface -10 under the whole footprint, solid below the region's bottom:
        // the region stops at the plane minus r: z min = -10 - 1.41421 = -11.41421; range from h = 5 (emitter bottom -5 to -10):
        // min((25/10) sqrt(625 + 100), 62.5) = 62.5, + r = 63.91421 -> lateral 68.91421 (not tighter here). Rejects the unfolded fall.
        Case(check, "r2 K-SP1: thick body below - z min -11.41421, labelled support-plane term", () =>
        {
            var r = SR(sha, _ => new[] { new DropSupport("body", 1, true, false, ClosedBox(-2000, -2000, -1000, 2000, 2000, -10)) });
            return (r.Status == "uncertified" && NearA7(r.Box.Min.Z, -10 - Sqrt2, 1e-2) && r.Labels.Any(l => l.Contains("support-plane term -")), ShowRR(r));
        });
        // P2 (F1's known answer): a thin plate (2 u, closed) with open space below keeps the downward extent (z min -119.58088) and says
        // why. Rejects the term applied to a plate.
        Case(check, "r2 K-SP2: thin plate with open space below - downward extent kept, F1 named", () =>
        {
            var r = SR(sha, _ => new[] { new DropSupport("plate", 1, true, false, ClosedBox(-2000, -2000, -12, 2000, 2000, -10)) });
            return (r.Status == "uncertified" && NearA7(r.Box.Min.Z, -(R2Lat + 16 * R2Q), 1e-2) && r.Labels.Any(l => l.Contains("support-plane term not applied") && l.Contains("F1")), ShowRR(r));
        });
        // P3: landscape (an open terrain sheet at -10) needs no depth: applied, z min -11.41421.
        Case(check, "r2 K-SP3: landscape below - applied, z min -11.41421", () =>
        {
            var r = SR(sha, _ => new[] { new DropSupport("LAND w 0,0", 1, true, false, Plate(2000, -10)) });
            return (r.Status == "uncertified" && NearA7(r.Box.Min.Z, -10 - Sqrt2, 1e-2), ShowRR(r));
        });
        // P4: a surface missing under part of the footprint (the body covers x >= 0 only): not applied, named.
        Case(check, "r2 K-SP4: footprint not covered - not applied, z min -119.58088", () =>
        {
            var r = SR(sha, _ => new[] { new DropSupport("body", 1, true, false, ClosedBox(0, -2000, -1000, 2000, 2000, -10)) });
            return (NearA7(r.Box.Min.Z, -(R2Lat + 16 * R2Q), 1e-2) && r.Labels.Any(l => l.Contains("no surface under")), ShowRR(r));
        });

        // ================= AP: a measured approximation beside the file bound =================
        // AP1: an entry for these bytes with a model-frame box (-10..10, -10..10, 0..30) placed at (100, 0, 0) turned 90 deg about z: the
        // part is the box carried by the placement, (90..110, -10..10, 0..30) (z turned: x and y spans swap, equal here), labelled, inside
        // the region. Rejects the part dropped and the box left in the model frame (x -10..10).
        // AP2: a different sha adds nothing. AP3: a box outside the file bound widens the region (z 0..500: the region reaches 500).
        ReachRegion AP(string sh, float zTop) => MeshReach.OfModel(pn, new Vec3(100, 0, 0), new Vec3(0, 0, MathF.PI / 2), 1f, null,
            new ReachOptions(0.5, 4096, new ReachRules(Particles: true, ModelPath: "meshes\\t\\p.nif",
                Approximations: new[] { new MeasuredApproximation("meshes/t/p.nif", sh, new Aabb(new Vec3(-10, -10, 0), new Vec3(10, 10, zTop), false), "test box (2 views, 5 bursts, margin 30 %)") })));
        Case(check, "r2 K-AP1: a measured approximation for these bytes is a labelled part carried by the placement, inside the region", () =>
        {
            var r = AP(sha, 30);
            var part = r.UncertifiedParts.FirstOrDefault(x => x.Label.StartsWith("in-game approximation (beside the file bound): test box"));
            return (r.Status == "uncertified" && part is not null && NearA7(part.Box.Min.X, 90, 1e-3) && NearA7(part.Box.Max.X, 110, 1e-3) && NearA7(part.Box.Max.Z, 30, 1e-3)
                    && r.Labels.Any(l => l.Contains("test box")) && BoxContains3b(r.Box, new Vector3(110, 10, 30)), ShowRR(r));
        });
        Case(check, "r2 K-AP2: another sha adds no part", () =>
        {
            var r = AP(new string('1', 64), 30);
            return (r.Status == "uncertified" && !r.UncertifiedParts.Any(x => x.Label.StartsWith("in-game approximation")), ShowRR(r));
        });
        Case(check, "r2 K-AP3: an approximation reaching outside the file bound widens the region (z max 500)", () =>
        {
            var r = AP(sha, 500);
            return (NearA7(r.Box.Max.Z, 500, 1e-2), ShowRR(r));
        });

        // ================= W: wiring the round-1 sweep could not see =================
        // W6: an emitter object translating x 0 -> 100: the region holds the emitter at x 100: x max = 105 + 63.91421 = 168.91421. Rejects
        // the emitter read at its first sampled pose only (x max 68.91).
        Case(check, "r2 K-W6: moving emitter swept over every pose - x max 168.91421", () =>
        {
            var r = MeshReach.OfModel(MovingParticleNif(true), Origin, Origin, 1f, null, new ReachOptions(0.5, 4096, new ReachRules(Particles: true, Emitter: true)));
            return (r.Status == "uncertified" && r.Box.Max.X >= 105 + R2Iso - 1e-2 && r.Box.Min.X <= -5 - R2Iso + 1e-2, ShowRR(r));
        });
        // W7: the particle system node itself under the mover (emitter object on the still root): the system's displacement carries the
        // particles: x max = 5 + 100 + 63.91421 = 168.91421. Rejects the displacement dropped (68.91).
        Case(check, "r2 K-W7: system node displaced x 0 -> 100 - x max 168.91421", () =>
        {
            var r = MeshReach.OfModel(MovingParticleNif(false), Origin, Origin, 1f, null, new ReachOptions(0.5, 4096, new ReachRules(Particles: true, Emitter: true)));
            return (r.Status == "uncertified" && r.Box.Max.X >= 105 + R2Iso - 1e-2, ShowRR(r));
        });
        // W8: a node a sequence drives with keys that hold its stored pose (translation (0,0,0) -> (0,0,0)) does not move: the particle
        // system under it, its emitter object and its gravity object keep the still-frame model (planar gravity on z only): x half
        // 68.91421, z half 119.58088. Rejects the block's presence alone making the frame move (gravity then on every axis: x 119.58).
        Case(check, "r2 K-W8: a sequence that holds the stored pose does not make the frame move - x half 68.91421, z half 119.58088", () =>
        {
            var data = new NiTransformData { RotationType = KeyType.LINEAR_KEY, NumRotationKeys = 0, Translations = VG(KeyType.LINEAR_KEY, (0f, new Vector3(0, 0, 0)), (1f, new Vector3(0, 0, 0))), Scales = NoKeys() };
            var nif = Rig(Box(-1, -1, -1, 1, 1, 1), Vector3.Zero, data, extra: (f, root) =>
            {
                var mover = f.Blocks.OfType<NiNode>().Single(n => n.Name?.String == "Mover");
                int mi = f.Blocks.IndexOf(mover);
                var psys = new NiParticleSystem { Name = new NiStringRef("Sparks"), Flags_ui = 0xE, Scale = 1f, Rotation = Identity33(), WorldSpace = true };
                int pi = f.AddBlock(psys); mover.Children.AddBlockRef(pi);
                psys.Data_NiBlockRef_NPSD = new(f.AddBlock(new NiPSysData { NumParticles = 0 }));
                psys.Modifiers ??= new();
                void Add(NiPSysModifier m) { m.Target = new(pi); m.Active = true; psys.Modifiers.AddBlockRef(f.AddBlock(m)); }
                Add(new NiPSysAgeDeathModifier { Name = new NiStringRef("Age") });
                Add(new NiPSysSphereEmitter { Name = new NiStringRef("Emitter"), Radius = 5f, Speed = 20f, SpeedVariation = 5f, LifeSpan = 2f, LifeSpanVariation = 0.5f, InitialRadius = 1f, EmitterObject = new(mi) });
                Add(new NiPSysGravityModifier { Name = new NiStringRef("Gravity"), GravityAxis = new Vector3(0, 0, -1), Strength = 10f, TurbulenceScale = 1f, GravityObject = new(mi) });
                Add(new NiPSysPositionModifier { Name = new NiStringRef("Position") });
                psys.NumModifiers = (uint)psys.Modifiers.Count;
            });
            var r = MeshReach.OfModel(nif, Origin, Origin, 1f, null, new ReachOptions(0.5, 4096, new ReachRules(Particles: true, Emitter: true)));
            var part = r.UncertifiedParts.FirstOrDefault(x => x.Label == MeshReach.ParticleModelLabel);
            return (r.Status == "uncertified" && part is not null && HalfIs(part.Box, R2Lat, R2Lat, R2Lat + 16 * R2Q, 1e-2), ShowRR(r));
        });
        // W4: a path node whose shape sits 48..52 u above it: the flat path hull (z 0) grown by the subtree radius |(2, 2, 52)| = 52.0769
        // reaches z 52.0769. Rejects the radius dropped (z max 0).
        Case(check, "r2 K-W4: path hull grown by the subtree radius - z max >= 52.0769", () =>
        {
            var nif = PathNif();
            var m = NifMotion.Read(nif, null, null, new MotionReadOptions(PathHull: true));
            var (pb, labs, why) = NifMotion.PathBox(nif, m, null);
            return (why is null && pb.Max.Z >= 52.0769 - 1e-3 && pb.Max.X >= 100 + 52.0769 - 1e-3, $"{m.Status} {m.Reason} box {pb} labels [{string.Join("; ", labs)}] why {why}");
        });
    }
}
