using System.Numerics;
using NiflySharp;
using NiflySharp.Blocks;

using HousecarlCore;

namespace SkyWright.Engine;

// PARTICLE REACH (G1 build list, Stage A, row 3): how far a NIF's particle systems can carry a particle from its emitter,
// under a DECLARED dynamics model. The files hold the parameters; the engine's integration law is not public (nif.xml
// describes the fields, not the law), so every bound is `modelled`, never certified, and lists its assumptions (red-gate
// review: accepted refutation of a certified bound).
// Model (particle_model_v1), per particle system:
//  * emitter extent in the emitter object's frame: sphere ±Radius; box ±Width, ±Height, ±Depth and cylinder ±max(Radius,
//    Height) on every axis (the axes and half/full extent conventions are not in the files: the largest reading is
//    taken); mesh emitters: the emitter meshes' vertices. Carried to model space by the emitter object's node chain
//    (root's own transform left out, as for render geometry);
//  * speed v = max over emitters (|Speed| + |SpeedVariation|) + G x max |SpawnSpeedVariation|;
//  * lifetime T = max over emitters (LifeSpan + |LifeSpanVariation|) + G x max (spawn LifeSpan + |spawn LifeSpanVariation|),
//    G = the largest NumSpawnGenerations of the spawn modifiers in the system;
//  * acceleration a = sum of |Strength| over gravity modifiers + sum of |DeltaV| over bomb modifiers;
//  * displacement D = v·T + a·T²/2; particle radius r = max (InitialRadius + |RadiusVariation|) x max(1, the largest
//    BSPSysScaleModifier key); model box = emitter extent grown by D + r, times max(1, the emitter chain's scale).
// Refused by name: turbulence != 0; a negative decay; drag outside [0, 1]; a collider with bounce > 1; an emitter object or
// particle system whose node chain is animated; a field, wind or Havok modifier, or any modifier the model does not know.

public sealed record ParticleBound(string Status, string? Reason, string SystemName, Aabb ModelBox, double Displacement,
                                   double ParticleRadius, IReadOnlyList<string> Assumptions,
                                   Aabb? WorldReadingBox = null, Vec3 WorldGrow = default, bool MovingEmitter = false,
                                   double InheritedTravel = 0, SettledFold? Fold = null);

/// <summary>Part 3b round 2: what a settled system's region was built from, for the caller's support-plane term after placement:
/// the model-frame direction of world -z, the speed bound, lifetime, half-size, the largest isotropic acceleration on an axis, gravity
/// moving with it (x 1.6) and for a climb (x 1.0), the emitter volume (model frame), the lateral half-extent without the emitter, and
/// the settled model axis (all lengths times the system's growth scale).</summary>
public sealed record SettledFold(Vector3 DownModel, double V, double T, double R, double AIso, double GWith, double GClimb, Aabb Emitter,
                                 double Lateral, int Axis);

/// <summary>Part 3b (reach rule `emitter`): a named node's model transforms over the model's motion (every source's sample times)
/// and a distance bounding how far a point within <c>radius</c> of the node origin moves between samples; null = the node does not
/// move or cannot be sampled.</summary>
public delegate (IReadOnlyList<(System.Numerics.Matrix4x4 R, float S, System.Numerics.Vector3 T)> Xf, double Widen, double SpeedEstimate)? NodeSweeper(string node, double radius);

/// <summary>One particle system's emitter extent (lane A, 2026-10-13): where particles are born, in the model frame at the stored pose.
/// Refusal is set (and Box empty) only when the emitter cannot be read; Moving says a transform block drives its chain.</summary>
public sealed record ParticleEmitter(string SystemName, int BlockIndex, Aabb Box, bool Moving, string? Refusal);

public static class ParticleReach
{
    /// <summary>Where each particle system's particles are born (lane A, 2026-10-13, the light-fixture rule): the union of its emitters'
    /// extents in the model frame at the stored pose, with the same extent reading <see cref="Bounds(byte[])"/> uses (root transform left
    /// out). No dynamics model: a moving emitter is read at its stored pose and flagged <c>Moving</c>, never refused; only an emitter that
    /// cannot be read refuses. Additive: <see cref="Bounds(byte[])"/> and every other method are unchanged.</summary>
    public static IReadOnlyList<ParticleEmitter> EmitterExtents(byte[] nif)
    {
        var f = new NifFile();
        using (var ms = new MemoryStream(nif, writable: false))
            if (f.Load(ms) != 0) return new[] { new ParticleEmitter("", -1, Aabb.Empty, false, "the NIF could not be read") };
        var rig = new MotionRig { Nif = f, Bytes = nif, Shapes = new(), NodeByName = new(StringComparer.Ordinal), Sources = new(StringComparer.Ordinal), Poses = new() };
        var animated = AnimatedNodes(f, transformOnly: true);
        var none = new HashSet<object>(ReferenceEqualityComparer.Instance);
        var list = new List<ParticleEmitter>();
        for (int bi = 0; bi < f.Blocks.Count; bi++)
        {
            if (f.Blocks[bi] is not NiParticleSystem ps) continue;
            string name = ps.Name?.String ?? $"#{bi}";
            var box = Aabb.Empty; string? why = null; bool any = false;
            bool moving = rig.Chain(ps).Any(animated.Contains);
            foreach (int i in ps.Modifiers?.Indices ?? Enumerable.Empty<int>())
            {
                if (i < 0 || i >= f.Blocks.Count || f.Blocks[i] is not NiPSysEmitter em) continue;
                any = true;
                var (eb, _, w) = Extent(f, rig, em, none);
                if (w is not null) { why = w; break; }
                box = box.Union(eb);
                IEnumerable<int> objs = em is NiPSysVolumeEmitter ve ? new[] { ve.EmitterObject?.Index ?? -1 }
                                      : em is NiPSysMeshEmitter me ? (me.EmitterMeshes?.Indices ?? Enumerable.Empty<int>()) : Enumerable.Empty<int>();
                foreach (int oi in objs)
                    if (oi >= 0 && oi < f.Blocks.Count && f.Blocks[oi] is NiAVObject o && rig.Chain(o).Any(animated.Contains)) moving = true;
            }
            if (!any) why = "no emitter";
            list.Add(why is null ? new ParticleEmitter(name, bi, box, moving, null) : new ParticleEmitter(name, bi, Aabb.Empty, moving, why));
        }
        return list;
    }

    public const string PolicyVersion = "particle_model_v1";
    public const string PolicyVersion2 = "particle_model_v2";

    static readonly string[] Assumed2 =
    {
        "speed bounded by |Speed| + |SpeedVariation| (and + |SpawnSpeedVariation| per spawn generation)",
        "lifetime bounded by LifeSpan + |LifeSpanVariation| (spawned: spawn LifeSpan + |variation| per generation)",
        "gravity Strength and bomb DeltaV are accelerations in u/s^2 that decay never increases; FORCE_PLANAR acts along the Gravity Axis only, and a PLANAR_SYMMETRY bomb along its Bomb Axis only (v4)",
        "drag (nif.xml: a linear drag force) read per second: the velocity component along the Drag Axis decays at k = Percentage /s; a per-update reading at >= 1 Hz decays faster (-f ln(1-p) >= p), so this is the weaker reading; a slower time base is not excluded by the files. Used only along a model axis a drag axis lies on, Range unlimited, no collider, no spawn generation",
        "colliders with bounce <= 1 never speed a particle up; with a collider present every acceleration is applied on every axis",
        "World Aligned (no description in nif.xml): both readings held, the axis in the gravity object's frame and in the world frame",
        "box and cylinder emitter dimensions read as half-extents on every axis (the larger reading)",
        "particle half-size = (InitialRadius + |RadiusVariation|) x the largest scale key, LOD End Size and grow-fade Base Scale (each at least 1), x sqrt 2 for a turned quad's corner",
        "growth times max(1, the emitter chain's scale, the particle system node's chain scale)",
    };

    /// <summary>particle_model_v2 (the reach rule `particles`; a node moves only when a transform block drives it): the extent from
    /// the file's modifiers, per axis. Per system, with v, T as v1 (speed and lifetime with variations and spawn generations):
    /// iso = v·T + r (r the quad-corner half-size); each FORCE_PLANAR gravity adds |Strength|·T²/2·|axis component| along its
    /// Gravity Axis carried by the Gravity Object's model rotation (World Aligned: also the world-frame reading, returned as
    /// <see cref="ParticleBound.WorldReadingBox"/> plus <see cref="ParticleBound.WorldGrow"/> for the caller to apply after
    /// placement); FORCE_SPHERICAL, a gravity without an object or axis or on a moving frame, every bomb, and every acceleration
    /// when a collider is present add |a|·T²/2 on every axis; FORCE_UNKNOWN refuses. The growth is scaled by max(1, emitter chain
    /// scale, system node chain scale). Drag is accepted and not used.</summary>
    public static IReadOnlyList<ParticleBound> BoundsFromModifiers(byte[] nif) => BoundsFromModifiers(nif, null);

    /// <summary>particle_model_v2 with the Part 3b moving-emitter rule: where the emitter object, emitter mesh or the system's chain
    /// moves, <paramref name="sweep"/> supplies its sampled transforms; the region is the swept emitter volume (and the system node's
    /// displacement) grown by the particle offset box (the sphere of its farthest corner when a chain turns:
    /// ParticleMotionReach.MovingEmitterBox). Null sweep: v2 unchanged (moving emitters refuse).</summary>
    public static IReadOnlyList<ParticleBound> BoundsFromModifiers(byte[] nif, NodeSweeper? sweep) => BoundsFromModifiers(nif, sweep, null);

    /// <summary>As above, with the settled-gravity fold (Part 3b round 2): <paramref name="settledDown"/> gives, for a system name, the
    /// model-frame unit vector of world -z when an in-game observation settled that the system's particles fall toward world -z (null =
    /// not settled). A settled system's planar gravity acts along that direction only: where it lies on a model axis the axis bound is
    /// one-sided (ParticleMotionReach.SettledAxis) and both World Aligned readings are dropped; otherwise the fold is not applied.</summary>
    public static IReadOnlyList<ParticleBound> BoundsFromModifiers(byte[] nif, NodeSweeper? sweep, Func<string, Vector3?>? settledDown)
    {
        var f = new NifFile();
        using (var ms = new MemoryStream(nif, writable: false))
            if (f.Load(ms) != 0) return new[] { new ParticleBound("refused", "the NIF could not be read", "", Aabb.Empty, 0, 0, Assumed2) };
        var rig = new MotionRig { Nif = f, Bytes = nif, Shapes = new(), NodeByName = new(StringComparer.Ordinal), Sources = new(StringComparer.Ordinal), Poses = new() };
        var animated = AnimatedNodes(f, transformOnly: true);
        // Part 3b round 2: with the motion reader's sweep, a node a transform block names but whose chain never leaves its stored pose
        // (every source holds it: NifMotion.NodeSweep returns the stored transform alone) is not moving
        if (sweep is not null)
            animated.RemoveWhere(n => n is NiAVObject o && o.Name?.String is { Length: > 0 } nm && sweep(nm, 0) is { } q && q.Xf.Count == 1 && q.Widen == 0);
        var list = new List<ParticleBound>();
        foreach (var ps in f.Blocks.OfType<NiParticleSystem>())
            list.Add(One2(f, rig, ps, animated, sweep, settledDown));
        return list;
    }

    // particle_model_v3 constants (Part 3b round 2), each from the Gamebryo 2.3 SDK source (CoreLibs/NiParticle; the 2.6
    // NiPSForceHelpers.inl carries the same): NiPSysGravityModifier::Update `fStrength = m_fStrength * 1.6f` and
    // `fTurbulenceScale = m_fTurbulence * m_fScale * 500.0f` (a per-axis NiSymmetricRandom acceleration times the update
    // interval); NiPSysDragModifier::Update `fDeltaT = (fTime - m_fLastUpdate) / DRAG_MIN_SAMPLE_TIME`, `DRAG_MIN_SAMPLE_TIME
    // 0.0333333f` (Percentage is removed per 1/30 s: a per-step factor (1 - 30 p dt) <= e^(-30 p dt), so k = 30 p per second is a
    // lower bound on the decay at any frame interval). Skyrim's binary is not read: the factor is applied on the side that does not
    // understate (1.6 x Strength moving with the force, 1.0 x Strength for a climb against it).
    public const double GravityFactor = 1.6, TurbulenceConstant = 500.0, DragSampleTime = 1.0 / 30.0;
    /// <summary>The update interval the explicit-Euler drift term assumes: position takes the already-updated velocity
    /// (NiPSysPositionModifier), so a constant acceleration a over T overshoots a T^2/2 by a T dt / 2.</summary>
    public const double EulerStep = 1.0 / 30.0;

    // particle-system controllers that change nothing the model bounds (birth rate, active flags, update, direction of an isotropic
    // speed, rotation of a quad about its own centre)
    static readonly HashSet<string> HarmlessCtlrs = new(StringComparer.Ordinal)
    {
        "NiPSysEmitterCtlr", "NiPSysModifierActiveCtlr", "NiPSysUpdateCtlr", "NiPSysResetOnLoopCtlr", "BSPSysMultiTargetEmitterCtlr",
        "NiPSysEmitterDeclinationCtlr", "NiPSysEmitterDeclinationVarCtlr", "NiPSysEmitterPlanarAngleCtlr", "NiPSysEmitterPlanarAngleVarCtlr",
        "NiPSysInitialRotSpeedCtlr", "NiPSysInitialRotSpeedVarCtlr", "NiPSysInitialRotAngleCtlr", "NiPSysInitialRotAngleVarCtlr",
    };
    // controllers whose float values replace a field the model reads: the bound takes the largest |value| over every key
    static readonly HashSet<string> ParamCtlrs = new(StringComparer.Ordinal)
    {
        "NiPSysGravityStrengthCtlr", "NiPSysEmitterSpeedCtlr", "NiPSysEmitterLifeSpanCtlr", "NiPSysEmitterInitialRadiusCtlr",
    };

    // a transform or visibility controller on the system node is the motion reader's (a moving emitter), not a particle parameter
    static bool MotionCtlr(string type) => type.Contains("Transform", StringComparison.Ordinal) || type.Contains("Keyframe", StringComparison.Ordinal) || type == "NiVisController"
        || RenderCtlr(type);
    // render-only controllers (shader colour and float values, alpha, material colour, texture transform and flip): they change how a
    // particle is drawn, never where (Part 3b round 2; found on the barrier models)
    static bool RenderCtlr(string type) => type.StartsWith("BSEffectShaderProperty", StringComparison.Ordinal) || type.StartsWith("BSLightingShaderProperty", StringComparison.Ordinal)
        || type is "NiAlphaController" or "NiMaterialColorController" or "NiFlipController" or "BSNiAlphaPropertyTestRefController";

    static IEnumerable<double> FloatValues(NifFile f, int interp)
    {
        if (interp < 0 || interp >= f.Blocks.Count || f.Blocks[interp] is not NiFloatInterpolator fi) yield break;
        if (float.IsFinite(fi.Value) && Math.Abs(fi.Value) < 1e30f) yield return fi.Value;
        int di = fi.Data?.Index ?? -1;
        if (di >= 0 && di < f.Blocks.Count && f.Blocks[di] is NiFloatData fd && fd.Data.Keys is { } keys)
            foreach (var k in keys) yield return k.Value;
    }

    /// <summary>Part 3b round 2: every float value a controller of the particle system holds for (controller type, modifier name),
    /// from the system's own controller chain and from every NiControllerSequence block naming the system; and the first controller
    /// type the model neither bounds nor knows to be harmless (null if none).</summary>
    /// <summary>Whether a NiPSysModifierActiveCtlr's interpolator can switch its modifier off: a false value or key, or an interpolator
    /// the reader cannot read (taken as able to).</summary>
    static bool MaySwitchOff(NifFile f, int interp)
    {
        if (interp < 0) return false;                                       // no interpolator: the modifier's own Active flag stands
        // a blend interpolator takes its values from the sequences' blocks, each of which is examined on its own
        if (interp < f.Blocks.Count && f.Blocks[interp] is NiBlendBoolInterpolator) return false;
        if (interp >= f.Blocks.Count || f.Blocks[interp] is not NiBoolInterpolator bi) return true;
        if (bi.Value == false) return true;
        int di = bi.Data?.Index ?? -1;
        if (di < 0) return bi.Value != true;                                // no keys: an unset value is taken as able to switch off
        if (di >= f.Blocks.Count || f.Blocks[di] is not NiBoolData bd) return true;
        return bd.Data.Keys.Any(k => k.Value == 0);
    }

    static (Dictionary<(string Type, string Modifier), List<double>> Values, string? Unknown) ParamControllers(NifFile f, NiParticleSystem ps)
        => ParamControllers(f, ps, out _);

    /// <summary>As above; <paramref name="mayBeOff"/> receives the names of modifiers a modifier-active controller (own chain or a
    /// sequence block) can switch off at some time (Part 3b round 2: a drag that can be off cannot be relied on to slow a particle, and
    /// a gravity that can be off cannot be relied on to stop a climb).</summary>
    static (Dictionary<(string Type, string Modifier), List<double>> Values, string? Unknown) ParamControllers(NifFile f, NiParticleSystem ps, out HashSet<string> mayBeOff)
    {
        var vals = new Dictionary<(string, string), List<double>>();
        var off = new HashSet<string>(StringComparer.Ordinal);
        string? unknown = null;
        void Add(string type, string mod, IEnumerable<double> xs)
        {
            if (!vals.TryGetValue((type, mod), out var l)) vals[(type, mod)] = l = new();
            l.AddRange(xs);
        }
        int ci = ps.Controller?.Index ?? -1, guard = 0;
        while (ci >= 0 && ci < f.Blocks.Count && f.Blocks[ci] is NiTimeController tc && guard++ < 64)
        {
            string type = tc.GetType().Name;
            if (type == "NiPSysModifierActiveCtlr" && MaySwitchOff(f, (tc as NiSingleInterpController)?.Interpolator?.Index ?? -1))
                off.Add(tc.GetType().GetProperty("ModifierName")?.GetValue(tc)?.ToString() ?? "");
            if (ParamCtlrs.Contains(type))
            {
                string mod = tc.GetType().GetProperty("ModifierName")?.GetValue(tc)?.ToString() ?? "";
                int ii = (tc as NiSingleInterpController)?.Interpolator?.Index ?? -1;
                Add(type, mod, FloatValues(f, ii));
            }
            else if (!HarmlessCtlrs.Contains(type) && !MotionCtlr(type)) unknown ??= type;
            ci = tc.NextController?.Index ?? -1;
        }
        string psName = ps.Name?.String ?? "";
        foreach (var seq in f.Blocks.OfType<NiControllerSequence>())
            foreach (var cb in seq.ControlledBlocks ?? new())
            {
                if (cb.NodeName?.ToString() != psName) continue;
                string type = cb.ControllerType?.ToString() ?? "";
                if (type == "NiPSysModifierActiveCtlr" && MaySwitchOff(f, cb.Interpolator?.Index ?? -1)) off.Add(cb.ControllerID?.ToString() ?? "");
                if (ParamCtlrs.Contains(type)) Add(type, cb.ControllerID?.ToString() ?? "", FloatValues(f, cb.Interpolator?.Index ?? -1));
                else if (!HarmlessCtlrs.Contains(type) && !MotionCtlr(type)) unknown ??= type;
            }
        mayBeOff = off;
        return (vals, unknown);
    }

    static bool Turns(IReadOnlyList<(System.Numerics.Matrix4x4 R, float S, Vector3 T)> xf)
    {
        if (xf.Count == 0) return false;
        var q0 = Quaternion.Normalize(Quaternion.CreateFromRotationMatrix(xf[0].R));
        return xf.Any(x => HavokClip.AngleBetween(Quaternion.Normalize(Quaternion.CreateFromRotationMatrix(x.R)), q0) > 1e-4f);
    }

    static ParticleBound One2(NifFile f, MotionRig rig, NiParticleSystem ps, HashSet<object> animated, NodeSweeper? sweep = null,
                              Func<string, Vector3?>? settledDown = null)
    {
        string name = ps.Name?.String ?? $"#{f.Blocks.IndexOf(ps)}";
        ParticleBound Refuse(string why) => new("refused", why, name, Aabb.Empty, 0, 0, Assumed2);
        bool chainMoves = rig.Chain(ps).Any(animated.Contains), moving = false, turns = false;
        double inheritSpeed = 0;
        var sysDisp = Aabb.Empty.Add(Vector3.Zero);
        if (chainMoves && sweep is null) return Refuse("the particle system's node chain is animated (a moving emitter is not bounded)");
        if (chainMoves)
        {
            // the system node moves: local-space particles move with it (its displacement from the stored pose is added to the
            // origins) and turn with it
            if (sweep(ps.Name?.String ?? "", 0) is not { } sw) return Refuse("moving emitter: the particle system node's motion cannot be sampled");
            var st = rig.Model(ps, null, 0, false).T;
            foreach (var x in sw.Xf) sysDisp = sysDisp.Add(x.T - st);
            sysDisp = new Aabb(new Vec3(sysDisp.Min.X - (float)sw.Widen, sysDisp.Min.Y - (float)sw.Widen, sysDisp.Min.Z - (float)sw.Widen),
                               new Vec3(sysDisp.Max.X + (float)sw.Widen, sysDisp.Max.Y + (float)sw.Widen, sysDisp.Max.Z + (float)sw.Widen), false);
            moving = true; turns |= Turns(sw.Xf);
        }
        var mods = new List<INiObject>();
        foreach (int i in ps.Modifiers?.Indices ?? Enumerable.Empty<int>())
            if (i >= 0 && i < f.Blocks.Count) mods.Add(f.Blocks[i]);
        // v3: a controller that animates a field the model reads is bounded by its largest key; any other unknown controller refuses
        var (ctl, unknownCtl) = ParamControllers(f, ps, out var mayBeOff);
        if (unknownCtl is not null) return Refuse($"particle-system controller {unknownCtl}: not in the particle model");
        double KeyMax(string type, string? mod, double field) => ctl.TryGetValue((type, mod ?? ""), out var kl) && kl.Count > 0 ? Math.Max(Math.Abs(field), kl.Max(Math.Abs)) : Math.Abs(field);
        double KeyMin(string type, string? mod, double field) => ctl.TryGetValue((type, mod ?? ""), out var kl) && kl.Count > 0 ? Math.Min(Math.Abs(field), kl.Min(Math.Abs)) : Math.Abs(field);
        double turbA = 0, climbG = double.PositiveInfinity;
        var spawns = new List<NiPSysSpawnModifier>(mods.OfType<NiPSysSpawnModifier>());
        void AddSpawn(int idx) { if (idx >= 0 && idx < f.Blocks.Count && f.Blocks[idx] is NiPSysSpawnModifier s && !spawns.Contains(s)) spawns.Add(s); }
        double v0 = 0, life0 = 0, radius = 0, scaleMax = 1, lodSize = 1, growFade = 1; bool anyEmitter = false, collider = false;
        double aIso = 0;                                   // accelerations with no fixed axis
        var bombAxis = Vector3.Zero; double bombSum = 0;   // v4: planar bombs, per model axis (|component| x DeltaV), and their total
        var planar = new List<(double A, Vector3 ModelAxis, Vector3? WorldAxis)>();
        var drags = new List<(double P, Vector3 ModelAxis)>();
        var extent = Aabb.Empty; double chainScale = 1;
        foreach (var m in mods)
        {
            string type = m.GetType().Name;
            switch (m)
            {
                case NiPSysEmitter em:
                {
                    anyEmitter = true;
                    string emName = em.Name?.String ?? "";
                    v0 = Math.Max(v0, KeyMax("NiPSysEmitterSpeedCtlr", emName, em.Speed) + Math.Abs(em.SpeedVariation));
                    life0 = Math.Max(life0, KeyMax("NiPSysEmitterLifeSpanCtlr", emName, em.LifeSpan) + Math.Abs(em.LifeSpanVariation));
                    radius = Math.Max(radius, KeyMax("NiPSysEmitterInitialRadiusCtlr", emName, em.InitialRadius) + Math.Abs(em.RadiusVariation));
                    var (ebox, es, why) = Extent(f, rig, em, animated);
                    if (why is not null && sweep is not null && why.Contains("moves"))
                    {
                        var (mbox, ms, mwhy, mturn) = MovingExtent(f, rig, em, sweep);
                        if (mwhy is not null) return Refuse(mwhy);
                        (ebox, es, why) = (mbox, ms, null); moving = true; turns |= mturn;
                    }
                    if (why is not null) return Refuse(why);
                    extent = extent.Union(ebox); chainScale = Math.Max(chainScale, es);
                    break;
                }
                case NiPSysGravityModifier g:
                {
                    string gname = g.Name?.String ?? "";
                    if (g.Decay < 0) return Refuse($"gravity modifier '{gname}': negative decay");
                    // v3: turbulence is a uniform random acceleration in [-A, A] on each axis of the system's frame, A = 500 x
                    // Turbulence x Turbulence Scale (not scaled by decay or the 1.6 factor); bounded by its supremum A T^2/2
                    if (!float.IsFinite(g.Turbulence) || !float.IsFinite(g.TurbulenceScale)) return Refuse($"gravity modifier '{gname}': non-finite turbulence");
                    turbA += TurbulenceConstant * Math.Abs(g.Turbulence) * Math.Abs(g.TurbulenceScale);
                    // a gravity a modifier-active controller can switch off stops no climb (its weakest pull is zero)
                    climbG = Math.Min(climbG, mayBeOff.Contains(gname) ? 0 : KeyMin("NiPSysGravityStrengthCtlr", gname, g.Strength));
                    double a = GravityFactor * KeyMax("NiPSysGravityStrengthCtlr", gname, g.Strength);
                    if (g.ForceType == NiflySharp.Enums.ForceType.FORCE_SPHERICAL) { aIso += a; break; }
                    if (g.ForceType != NiflySharp.Enums.ForceType.FORCE_PLANAR) return Refuse($"gravity modifier '{gname}': force type {g.ForceType} is not in the particle model");
                    int oi = g.GravityObject?.Index ?? -1;
                    var axis = g.GravityAxis;
                    if (oi < 0 || oi >= f.Blocks.Count || f.Blocks[oi] is not NiAVObject gobj || !(axis.Length() > 0) || !float.IsFinite(axis.Length())) { aIso += a; break; }
                    if (rig.Chain(gobj).Any(animated.Contains)) { aIso += a; break; }   // a turning frame: no fixed axis
                    var unit = Vector3.Normalize(axis);
                    var model = Vector3.Transform(unit, rig.Model(gobj, null, 0, false).R);
                    if (!(model.Length() > 0)) { aIso += a; break; }
                    planar.Add((a, Vector3.Normalize(model), g.WorldAligned == true ? unit : null));
                    break;
                }
                case NiPSysBombModifier b:
                {
                    if (b.Decay < 0) return Refuse($"bomb modifier '{b.Name?.String}': negative decay");
                    // v4 (lane B item 4, 2026-10-13): Gamebryo 2.3 NiPSysBombModifier::Update adds fDecay x DeltaV x dt x kForce, and
                    // under PLANAR_SYMMETRY kForce is the bomb axis (its sign by the particle's side of the plane): a planar bomb on a
                    // still frame pushes along its axis only, like a planar gravity. Spherical and cylindrical bombs, and a bomb with no
                    // readable axis or frame or on a moving frame, keep the push on every axis. Decay is not read (never increases it).
                    double bv = Math.Abs(b.DeltaV);
                    int bo = b.BombObject?.Index ?? -1;
                    var baxis = b.BombAxis;
                    if (b.SymmetryType == NiflySharp.Enums.SymmetryType.PLANAR_SYMMETRY && bo >= 0 && bo < f.Blocks.Count
                        && f.Blocks[bo] is NiAVObject bobj && baxis.Length() > 0 && float.IsFinite(baxis.Length()) && !rig.Chain(bobj).Any(animated.Contains))
                    {
                        var bm = Vector3.Transform(Vector3.Normalize(baxis), rig.Model(bobj, null, 0, false).R);
                        // symmetric along the axis (either side of the plane), so it rides with the per-axis turbulence bound below
                        if (bm.Length() > 0) { bombAxis += (float)bv * Vector3.Abs(Vector3.Normalize(bm)); bombSum += bv; break; }
                    }
                    aIso += bv; break;
                }
                case NiPSysDragModifier d:
                {
                    if (d.Percentage < 0 || d.Percentage > 1) return Refuse($"drag modifier '{d.Name?.String}': percentage {d.Percentage} outside [0, 1]");
                    if (mayBeOff.Contains(d.Name?.String ?? "")) break;   // a drag that can be switched off cannot be relied on (round 2)
                    // used only at full strength everywhere (Range unlimited), on a still frame with a stated axis
                    int oi = d.DragObject?.Index ?? -1;
                    var axis = d.DragAxis;
                    if (!(d.Range >= float.MaxValue) || oi < 0 || oi >= f.Blocks.Count || f.Blocks[oi] is not NiAVObject dobj
                        || !(axis.Length() > 0) || !float.IsFinite(axis.Length()) || rig.Chain(dobj).Any(animated.Contains)) break;
                    var model = Vector3.Transform(Vector3.Normalize(axis), rig.Model(dobj, null, 0, false).R);
                    if (model.Length() > 0) drags.Add((d.Percentage, Vector3.Normalize(model)));
                    break;
                }
                case NiPSysSpawnModifier:
                    break;
                case NiPSysAgeDeathModifier ad:
                    AddSpawn(ad.SpawnModifier?.Index ?? -1); break;
                case NiPSysColliderManager cm:
                {
                    int ci = cm.Collider?.Index ?? -1, guard = 0;
                    while (ci >= 0 && ci < f.Blocks.Count && f.Blocks[ci] is NiPSysCollider col && guard++ < 64)
                    {
                        if (col.Bounce > 1) return Refuse($"collider: bounce {col.Bounce} > 1");
                        collider = true;
                        AddSpawn(col.SpawnModifier?.Index ?? -1);
                        ci = col.NextCollider?.Index ?? -1;
                    }
                    break;
                }
                case BSPSysScaleModifier sc:
                    if (sc.Scales is { Count: > 0 }) scaleMax = Math.Max(scaleMax, sc.Scales.Max(x => Math.Abs((double)x)));
                    break;
                case BSPSysLODModifier lod:
                    lodSize = Math.Max(lodSize, Math.Abs(lod.EndSize)); break;
                case NiPSysGrowFadeModifier gf:
                    growFade = Math.Max(growFade, Math.Abs(gf.BaseScale)); break;
                case BSPSysInheritVelocityModifier iv:
                {
                    int oi = iv.InheritObject?.Index ?? -1;
                    if (oi >= 0 && oi < f.Blocks.Count && f.Blocks[oi] is NiAVObject o && rig.Chain(o).Any(animated.Contains))
                    {
                        // Part 3b: the particle inherits (VelocityMultiplier + VelocityVariation) x the object's speed at birth and
                        // keeps it over its lifetime (added isotropically; the speed is a sampled estimate, labelled by the caller)
                        if (sweep is null || sweep(o.Name?.String ?? "", 0) is not { } isw) return Refuse("inherit-velocity modifier: the inherit object moves");
                        if (!double.IsFinite(isw.SpeedEstimate)) return Refuse("inherit-velocity modifier: the inherit object moves under graph blends (no speed bound)");
                        inheritSpeed = Math.Max(inheritSpeed, (Math.Abs(iv.VelocityMultiplier) + Math.Abs(iv.VelocityVariation)) * isw.SpeedEstimate);
                        moving = true; turns |= Turns(isw.Xf);
                    }
                    break;
                }
                default:
                    if (!Harmless.Contains(type)) return Refuse($"modifier {type} '{(m as NiPSysModifier)?.Name?.String}': not in the particle model");
                    break;
            }
        }
        if (!anyEmitter) return Refuse("no emitter");
        int gens = spawns.Count == 0 ? 0 : spawns.Max(s => (int)s.NumSpawnGenerations);
        double spawnLife = spawns.Count == 0 ? 0 : spawns.Max(s => Math.Abs(s.LifeSpan) + Math.Abs(s.LifeSpanVariation));
        double spawnVar = spawns.Count == 0 ? 0 : spawns.Max(s => Math.Abs(s.SpawnSpeedVariation));
        if (spawns.Any(s => s.WorldShiftSpawnSpeedAddition != 0)) return Refuse("spawn modifier: world-shift speed addition");
        double T = life0 + gens * spawnLife, v = v0 + gens * spawnVar, half = T * T / 2;
        if (collider) { foreach (var p in planar) aIso += p.A; planar.Clear(); aIso += bombSum; bombAxis = Vector3.Zero; bombSum = 0; }
        double r = radius * scaleMax * lodSize * growFade * Math.Sqrt(2);
        double sysScale = Math.Abs(rig.Model(ps, null, 0, false).S);
        double s = Math.Max(1, Math.Max(chainScale, double.IsFinite(sysScale) ? sysScale : double.PositiveInfinity));
        // drag per model axis, read per second (k = Percentage): only along a model axis a drag axis lies on, and not with a collider
        // (a bounce turns undamped speed onto the axis) or spawn generations (a spawned particle's speed restarts)
        double K(Vector3 e) => collider || gens > 0 ? 0 : drags.Where(d => Math.Abs(Vector3.Dot(d.ModelAxis, e)) >= 1 - 1e-6).Select(d => d.P / DragSampleTime).DefaultIfEmpty(0).Max();
        double kx = K(Vector3.UnitX), ky = K(Vector3.UnitY), kz = K(Vector3.UnitZ);
        double Phi1(double k) => k > 0 ? (1 - Math.Exp(-k * T)) / k : T;
        double Phi2(double k) => k > 0 ? T / k - (1 - Math.Exp(-k * T)) / (k * k) : half;
        // v3: turbulence per model axis: the cube [-A, A]^3 of the system's frame carried by the system node's model rotation
        var psR = rig.Model(ps, null, 0, false).R;
        Vector3 tx = Vector3.Abs(Vector3.Transform(Vector3.UnitX, psR)), ty = Vector3.Abs(Vector3.Transform(Vector3.UnitY, psR)), tz = Vector3.Abs(Vector3.Transform(Vector3.UnitZ, psR));
        // v4: a planar bomb's per-axis push is added here, beside the turbulence bound: both act on each axis symmetrically
        var turbAxis = (tx + ty + tz) * (float)turbA + bombAxis;
        if (!(turbA >= 0) || !float.IsFinite(turbAxis.X + turbAxis.Y + turbAxis.Z)) return Refuse("non-finite turbulence bound");
        // along an axis: |x| <= v phi1(k) + r + a (phi2(k) + T dt / 2) (the explicit-Euler drift), a = the isotropic accelerations +
        // each planar one's component + the turbulence bound on that axis
        double Axis(double k, double aPlanar, double aTurb = 0) => v * Phi1(k) + r + (aIso + aPlanar + aTurb) * (Phi2(k) + T * EulerStep / 2);
        double Ax(Func<Vector3, float> c, bool worldToo) => planar.Where(p => worldToo || p.WorldAxis is null).Sum(p => p.A * Math.Abs(c(p.ModelAxis)));
        double aSum = aIso + planar.Sum(p => p.A) + Math.Sqrt(3) * turbA + bombSum;
        double D = v * T + aSum * (half + T * EulerStep / 2);   // the drag-free total, for the caller's scale compensation
        double ex = Axis(kx, Ax(a => a.X, true), turbAxis.X), ey = Axis(ky, Ax(a => a.Y, true), turbAxis.Y), ez = Axis(kz, Ax(a => a.Z, true), turbAxis.Z);
        if (!double.IsFinite(D) || !double.IsFinite(r) || !double.IsFinite(s) || !double.IsFinite(ex) || !double.IsFinite(ey) || !double.IsFinite(ez))
            return Refuse("non-finite particle bound");
        Aabb Grown(double gx, double gy, double gz) => new(
            new Vec3(Down(extent.Min.X - gx * s), Down(extent.Min.Y - gy * s), Down(extent.Min.Z - gz * s)),
            new Vec3(Up(extent.Max.X + gx * s), Up(extent.Max.Y + gy * s), Up(extent.Max.Z + gz * s)), false);
        if (!moving && !collider && planar.Count > 0 && settledDown?.Invoke(name) is { } dn && double.IsFinite(climbG))
        {
            int ax = Math.Abs(dn.X) >= 1 - 1e-6 ? 0 : Math.Abs(dn.Y) >= 1 - 1e-6 ? 1 : Math.Abs(dn.Z) >= 1 - 1e-6 ? 2 : -1;
            if (ax >= 0)
            {
                // Part 3b round 2 (Gate A review F1): the observation settles the frame and the sign; the planar gravity acts along
                // world -z only (the World Aligned readings are dropped); moving with it 1.6 x the largest strength, a climb against it
                // 1.0 x the smallest (the side that does not understate either way)
                double gWith = planar.Sum(p => p.A), drift = T * EulerStep / 2;
                double[] ks = { kx, ky, kz }; float[] tb = { turbAxis.X, turbAxis.Y, turbAxis.Z };
                var lo = new double[3]; var hi = new double[3];
                for (int i = 0; i < 3; i++)
                {
                    if (i != ax) { double e = Axis(ks[i], 0, tb[i]); lo[i] = -e; hi[i] = e; continue; }
                    var (mn, mx) = ParticleMotionReach.SettledAxis(v, T, r, aIso + tb[i], ks[i], gWith, climbG, (i == 0 ? dn.X : i == 1 ? dn.Y : dn.Z) < 0);
                    lo[i] = mn - (aIso + tb[i] + ((i == 0 ? dn.X : i == 1 ? dn.Y : dn.Z) < 0 ? gWith : 0)) * drift;
                    hi[i] = mx + (aIso + tb[i] + ((i == 0 ? dn.X : i == 1 ? dn.Y : dn.Z) > 0 ? gWith : 0)) * drift;
                }
                var sbox = new Aabb(new Vec3(Down(extent.Min.X + lo[0] * s), Down(extent.Min.Y + lo[1] * s), Down(extent.Min.Z + lo[2] * s)),
                                    new Vec3(Up(extent.Max.X + hi[0] * s), Up(extent.Max.Y + hi[1] * s), Up(extent.Max.Z + hi[2] * s)), false);
                double lat = Math.Max(Axis(ks[(ax + 1) % 3], 0, tb[(ax + 1) % 3]), Axis(ks[(ax + 2) % 3], 0, tb[(ax + 2) % 3]));
                var fold = new SettledFold(dn, v * s, T, r * s, (aIso + Math.Max(tb[0], Math.Max(tb[1], tb[2]))) * s, gWith * s, climbG * s, extent, lat * s, ax);
                return new ParticleBound("modelled", null, name, sbox, D, r, Assumed2, Fold: fold);
            }
            // world -z off the model axes (a turned placement): the model box WITHOUT the planar gravity (Axis = -1); the caller
            // composes the settled gravity in the world frame after placement (MeshReach)
            double gx = Axis(kx, 0, turbAxis.X), gy = Axis(ky, 0, turbAxis.Y), gz = Axis(kz, 0, turbAxis.Z);
            double gW = planar.Sum(p => p.A);
            // Lateral here: the largest gravity-free growth on any axis (drag-reduced; the caller's small-scale compensation)
            var gfold = new SettledFold(dn, v * s, T, r * s, (aIso + Math.Max(turbAxis.X, Math.Max(turbAxis.Y, turbAxis.Z))) * s, gW * s, climbG * s, extent, Math.Max(gx, Math.Max(gy, gz)) * s, -1);
            return new ParticleBound("modelled", null, name, Grown(gx, gy, gz), D, r, Assumed2, Fold: gfold);
        }
        if (moving)
        {
            // Part 3b moving emitter: swept origins (emitter volume over the motion, plus the system node's displacement) grown by the
            // offset box, rotation-invariant when a chain turns
            var origins = new Aabb(new Vec3(extent.Min.X + sysDisp.Min.X, extent.Min.Y + sysDisp.Min.Y, extent.Min.Z + sysDisp.Min.Z),
                                   new Vec3(extent.Max.X + sysDisp.Max.X, extent.Max.Y + sysDisp.Max.Y, extent.Max.Z + sysDisp.Max.Z), false);
            Aabb Off(double gx, double gy, double gz) => new(new Vec3(-Up(gx * s), -Up(gy * s), -Up(gz * s)), new Vec3(Up(gx * s), Up(gy * s), Up(gz * s)), false);
            float inh = Up(inheritSpeed * T * s);
            var mboxA = ParticleMotionReach.MovingEmitterBox(origins, Off(ex, ey, ez), turns, Quaternion.Identity, inh);
            if (!planar.Any(p => p.WorldAxis is not null)) return new ParticleBound("modelled", null, name, mboxA, D, r, Assumed2, MovingEmitter: true, InheritedTravel: inh);
            double wx2 = 0, wy2 = 0, wz2 = 0;
            foreach (var p in planar)
                if (p.WorldAxis is { } w) { double hw = half + T * EulerStep / 2; wx2 += p.A * hw * Math.Abs(w.X) * s; wy2 += p.A * hw * Math.Abs(w.Y) * s; wz2 += p.A * hw * Math.Abs(w.Z) * s; }
            var mboxB = ParticleMotionReach.MovingEmitterBox(origins, Off(Axis(kx, Ax(a => a.X, false), turbAxis.X), Axis(ky, Ax(a => a.Y, false), turbAxis.Y), Axis(kz, Ax(a => a.Z, false), turbAxis.Z)), turns, Quaternion.Identity, inh);
            return new ParticleBound("modelled", null, name, mboxA, D, r, Assumed2, mboxB, new Vec3(Up(wx2), Up(wy2), Up(wz2)), MovingEmitter: true, InheritedTravel: inh);
        }
        // reading A: every planar axis in its gravity object's frame (model space)
        var box = Grown(ex, ey, ez);
        if (!planar.Any(p => p.WorldAxis is not null)) return new ParticleBound("modelled", null, name, box, D, r, Assumed2);
        // reading B: the world-aligned axes in the world frame, applied by the caller after placement, with no drag
        double wx = 0, wy = 0, wz = 0;
        foreach (var p in planar)
            if (p.WorldAxis is { } w) { double hw = half + T * EulerStep / 2; wx += p.A * hw * Math.Abs(w.X) * s; wy += p.A * hw * Math.Abs(w.Y) * s; wz += p.A * hw * Math.Abs(w.Z) * s; }
        var boxB = Grown(Axis(kx, Ax(a => a.X, false), turbAxis.X), Axis(ky, Ax(a => a.Y, false), turbAxis.Y), Axis(kz, Ax(a => a.Z, false), turbAxis.Z));
        return new ParticleBound("modelled", null, name, box, D, r, Assumed2, boxB, new Vec3(Up(wx), Up(wy), Up(wz)));
    }

static float Down(double x) { float g = (float)x; return g > x ? MathF.BitDecrement(g) : g; }
    static float Up(double x) { float g = (float)x; return g < x ? MathF.BitIncrement(g) : g; }

    static readonly string[] Assumed =
    {
        "speed bounded by |Speed| + |SpeedVariation| (and + |SpawnSpeedVariation| per spawn generation)",
        "lifetime bounded by LifeSpan + |LifeSpanVariation| (spawned: spawn LifeSpan + |variation| per generation)",
        "gravity Strength and bomb DeltaV are accelerations in u/s^2 that decay never increases",
        "drag only slows a particle; colliders with bounce <= 1 never speed it up",
        "box and cylinder emitter dimensions read as half-extents on every axis (the larger reading)",
        "particle size = InitialRadius + |RadiusVariation|, times the largest scale key",
    };

    static readonly HashSet<string> Harmless = new(StringComparer.Ordinal)
    {
        nameof(NiPSysAgeDeathModifier), nameof(NiPSysPositionModifier), nameof(NiPSysBoundUpdateModifier), nameof(NiPSysRotationModifier),
        nameof(BSPSysSimpleColorModifier), nameof(BSPSysLODModifier), nameof(BSPSysSubTexModifier), "NiPSysColorModifier", "NiPSysGrowFadeModifier",
    };

    /// <summary>One bound per particle system in the NIF (model space, root transform left out as for render geometry).</summary>
    public static IReadOnlyList<ParticleBound> Bounds(byte[] nif) => Bounds(nif, transformOnly: false);

    /// <summary>As <see cref="Bounds(byte[])"/>; <paramref name="transformOnly"/>: a node counts as moving only when a TRANSFORM
    /// block drives it (a particle emitter controller animates birth rate and emitter-active, a modifier-active controller a
    /// modifier's active flag: nif.xml NiPSysEmitterCtlr, NiPSysModifierActiveCtlr), the reach rule `particles`.</summary>
    public static IReadOnlyList<ParticleBound> Bounds(byte[] nif, bool transformOnly)
    {
        var f = new NifFile();
        using (var ms = new MemoryStream(nif, writable: false))
            if (f.Load(ms) != 0) return new[] { new ParticleBound("refused", "the NIF could not be read", "", Aabb.Empty, 0, 0, Assumed) };
        var rig = new MotionRig { Nif = f, Bytes = nif, Shapes = new(), NodeByName = new(StringComparer.Ordinal), Sources = new(StringComparer.Ordinal), Poses = new() };
        var animated = AnimatedNodes(f, transformOnly);
        var list = new List<ParticleBound>();
        foreach (var ps in f.Blocks.OfType<NiParticleSystem>())
            list.Add(One(f, rig, ps, animated));
        return list;
    }

    static ParticleBound One(NifFile f, MotionRig rig, NiParticleSystem ps, HashSet<object> animated)
    {
        string name = ps.Name?.String ?? $"#{f.Blocks.IndexOf(ps)}";
        ParticleBound Refuse(string why) => new("refused", why, name, Aabb.Empty, 0, 0, Assumed);
        if (rig.Chain(ps).Any(animated.Contains)) return Refuse("the particle system's node chain is animated (a moving emitter is not bounded)");
        var mods = new List<INiObject>();
        foreach (int i in ps.Modifiers?.Indices ?? Enumerable.Empty<int>())
            if (i >= 0 && i < f.Blocks.Count) mods.Add(f.Blocks[i]);
        // spawn modifiers can also hang off the age/death modifier or a collider
        var spawns = new List<NiPSysSpawnModifier>(mods.OfType<NiPSysSpawnModifier>());
        void AddSpawn(int idx) { if (idx >= 0 && idx < f.Blocks.Count && f.Blocks[idx] is NiPSysSpawnModifier s && !spawns.Contains(s)) spawns.Add(s); }
        double v0 = 0, life0 = 0, a = 0, radius = 0, scaleMax = 1; bool anyEmitter = false;
        var extent = Aabb.Empty; double chainScale = 1;
        foreach (var m in mods)
        {
            string type = m.GetType().Name;
            switch (m)
            {
                case NiPSysEmitter em:
                {
                    anyEmitter = true;
                    v0 = Math.Max(v0, Math.Abs(em.Speed) + Math.Abs(em.SpeedVariation));
                    life0 = Math.Max(life0, Math.Abs(em.LifeSpan) + Math.Abs(em.LifeSpanVariation));
                    radius = Math.Max(radius, Math.Abs(em.InitialRadius) + Math.Abs(em.RadiusVariation));
                    var (ebox, es, why) = Extent(f, rig, em, animated);
                    if (why is not null) return Refuse(why);
                    extent = extent.Union(ebox); chainScale = Math.Max(chainScale, es);
                    break;
                }
                case NiPSysGravityModifier g:
                    if (g.Turbulence != 0) return Refuse($"gravity modifier '{g.Name?.String}': turbulence {g.Turbulence} (its magnitude is not in the files)");
                    if (g.Decay < 0) return Refuse($"gravity modifier '{g.Name?.String}': negative decay");
                    a += Math.Abs(g.Strength); break;
                case NiPSysBombModifier b:
                    if (b.Decay < 0) return Refuse($"bomb modifier '{b.Name?.String}': negative decay");
                    a += Math.Abs(b.DeltaV); break;
                case NiPSysDragModifier d:
                    if (d.Percentage < 0 || d.Percentage > 1) return Refuse($"drag modifier '{d.Name?.String}': percentage {d.Percentage} outside [0, 1]");
                    break;
                case NiPSysSpawnModifier:
                    break;
                case NiPSysAgeDeathModifier ad:
                    AddSpawn(ad.SpawnModifier?.Index ?? -1); break;
                case NiPSysColliderManager cm:
                {
                    int ci = cm.Collider?.Index ?? -1, guard = 0;
                    while (ci >= 0 && ci < f.Blocks.Count && f.Blocks[ci] is NiPSysCollider col && guard++ < 64)
                    {
                        if (col.Bounce > 1) return Refuse($"collider: bounce {col.Bounce} > 1");
                        AddSpawn(col.SpawnModifier?.Index ?? -1);
                        ci = col.NextCollider?.Index ?? -1;
                    }
                    break;
                }
                case BSPSysScaleModifier sc:
                    if (sc.Scales is { Count: > 0 }) scaleMax = Math.Max(scaleMax, sc.Scales.Max(x => Math.Abs((double)x)));
                    break;
                case BSPSysInheritVelocityModifier iv:
                {
                    int oi = iv.InheritObject?.Index ?? -1;
                    if (oi >= 0 && oi < f.Blocks.Count && f.Blocks[oi] is NiAVObject o && rig.Chain(o).Any(animated.Contains))
                        return Refuse("inherit-velocity modifier: the inherit object moves");
                    break;
                }
                default:
                    if (!Harmless.Contains(type)) return Refuse($"modifier {type} '{(m as NiPSysModifier)?.Name?.String}': not in the particle model");
                    break;
            }
        }
        if (!anyEmitter) return Refuse("no emitter");
        int gens = spawns.Count == 0 ? 0 : spawns.Max(s => (int)s.NumSpawnGenerations);
        double spawnLife = spawns.Count == 0 ? 0 : spawns.Max(s => Math.Abs(s.LifeSpan) + Math.Abs(s.LifeSpanVariation));
        double spawnVar = spawns.Count == 0 ? 0 : spawns.Max(s => Math.Abs(s.SpawnSpeedVariation));
        if (spawns.Any(s => s.WorldShiftSpawnSpeedAddition != 0)) return Refuse("spawn modifier: world-shift speed addition");
        double T = life0 + gens * spawnLife, v = v0 + gens * spawnVar;
        double D = v * T + a * T * T / 2;
        double r = radius * scaleMax;
        if (!double.IsFinite(D) || !double.IsFinite(r) || !double.IsFinite(chainScale)) return Refuse("non-finite particle bound");
        float grow = (float)((D + r) * Math.Max(1, chainScale));
        var box = new Aabb(new Vec3(extent.Min.X - grow, extent.Min.Y - grow, extent.Min.Z - grow),
                           new Vec3(extent.Max.X + grow, extent.Max.Y + grow, extent.Max.Z + grow), false);
        return new ParticleBound("modelled", null, name, box, D, r, Assumed);
    }

    /// <summary>Part 3b: a moving emitter's volume over its sampled motion (volume emitter: the object's cube at every sampled
    /// transform; mesh emitter: the mesh's vertices at every sampled transform of the mesh node), grown by the between-sample widen.</summary>
    static (Aabb Box, double Scale, string? Why, bool Turns) MovingExtent(NifFile f, MotionRig rig, NiPSysEmitter em, NodeSweeper sweep)
    {
        var box = Aabb.Empty; double sc = 1; bool turns = false;
        string? Add(NiAVObject obj, IReadOnlyList<Vector3> local, string what)
        {
            double rad = local.Count == 0 ? 0 : local.Max(p => (double)p.Length());
            if (sweep(obj.Name?.String ?? "", rad) is not { } sw || sw.Xf.Count == 0) return $"moving emitter: the {what} motion cannot be sampled";
            turns |= Turns(sw.Xf);
            var b = Aabb.Empty;
            foreach (var x in sw.Xf)
            {
                var xf = new NodeXf(x.R, x.S, x.T); sc = Math.Max(sc, Math.Abs(x.S));
                foreach (var p in local) b = b.Add(xf.Apply(p));
            }
            float w = (float)sw.Widen;
            if (w > 0) b = new Aabb(new Vec3(b.Min.X - w, b.Min.Y - w, b.Min.Z - w), new Vec3(b.Max.X + w, b.Max.Y + w, b.Max.Z + w), false);
            box = box.Union(b);
            return null;
        }
        if (em is NiPSysMeshEmitter me)
        {
            foreach (int i in me.EmitterMeshes?.Indices ?? Enumerable.Empty<int>())
            {
                if (i < 0 || i >= f.Blocks.Count || f.Blocks[i] is not NiAVObject shape) return (Aabb.Empty, 1, "mesh emitter: an emitter mesh is missing", false);
                if (shape is INiShape ish && (ish.IsSkinned || ish.HasSkinInstance)) return (Aabb.Empty, 1, "moving emitter: a skinned emitter mesh is not in the model", false);
                var verts = (shape as BSTriShape)?.VertexPositions
                            ?? ((shape as NiGeometry)?.GeometryData?.GetType().GetProperty("Vertices")?.GetValue((shape as NiGeometry)!.GeometryData) as List<Vector3>);
                if (verts is null || verts.Count == 0) return (Aabb.Empty, 1, "mesh emitter: an emitter mesh has no readable vertices", false);
                if (Add(shape, verts, "emitter mesh") is { } why) return (Aabb.Empty, 1, why, false);
            }
            return box.IsEmpty ? (Aabb.Empty, 1, "mesh emitter: no emitter mesh", false) : (box, sc, null, turns);
        }
        if (em is not NiPSysVolumeEmitter ve) return (Aabb.Empty, 1, $"emitter {em.GetType().Name}: not in the particle model", false);
        int oi = ve.EmitterObject?.Index ?? -1;
        if (oi < 0 || oi >= f.Blocks.Count || f.Blocks[oi] is not NiAVObject obj) return (Aabb.Empty, 1, "volume emitter: no emitter object", false);
        float h = ve switch
        {
            NiPSysSphereEmitter sp => Math.Abs(sp.Radius),
            NiPSysCylinderEmitter c => Math.Max(Math.Abs(c.Radius), Math.Abs(c.Height)),
            NiPSysBoxEmitter bx => Math.Max(Math.Abs(bx.Width), Math.Max(Math.Abs(bx.Height), Math.Abs(bx.Depth))),
            _ => float.NaN,
        };
        if (float.IsNaN(h)) return (Aabb.Empty, 1, $"emitter {ve.GetType().Name}: not in the particle model", false);
        var cube = Enumerable.Range(0, 8).Select(k => new Vector3((k & 1) == 0 ? -h : h, (k & 2) == 0 ? -h : h, (k & 4) == 0 ? -h : h)).ToList();
        if (Add(obj, cube, "emitter object") is { } why2) return (Aabb.Empty, 1, why2, false);
        return (box, sc, null, turns);
    }

    static (Aabb Box, double Scale, string? Why) Extent(NifFile f, MotionRig rig, NiPSysEmitter em, HashSet<object> animated)
    {
        if (em is NiPSysMeshEmitter me)
        {
            var box = Aabb.Empty; double sc = 1;
            foreach (int i in me.EmitterMeshes?.Indices ?? Enumerable.Empty<int>())
            {
                if (i < 0 || i >= f.Blocks.Count || f.Blocks[i] is not NiAVObject shape) return (Aabb.Empty, 1, "mesh emitter: an emitter mesh is missing");
                if (rig.Chain(shape).Any(animated.Contains)) return (Aabb.Empty, 1, "mesh emitter: an emitter mesh moves");
                var xf = rig.Model(shape, null, 0, false); sc = Math.Max(sc, Math.Abs(xf.S));
                var verts = (shape as BSTriShape)?.VertexPositions
                            ?? ((shape as NiGeometry)?.GeometryData?.GetType().GetProperty("Vertices")?.GetValue((shape as NiGeometry)!.GeometryData) as List<Vector3>);
                if (verts is null || verts.Count == 0) return (Aabb.Empty, 1, "mesh emitter: an emitter mesh has no readable vertices");
                foreach (var p in verts) box = box.Add(xf.Apply(p));
            }
            return box.IsEmpty ? (Aabb.Empty, 1, "mesh emitter: no emitter mesh") : (box, sc, null);
        }
        if (em is not NiPSysVolumeEmitter ve) return (Aabb.Empty, 1, $"emitter {em.GetType().Name}: not in the particle model");
        int oi = ve.EmitterObject?.Index ?? -1;
        if (oi < 0 || oi >= f.Blocks.Count || f.Blocks[oi] is not NiAVObject obj) return (Aabb.Empty, 1, "volume emitter: no emitter object");
        if (rig.Chain(obj).Any(animated.Contains)) return (Aabb.Empty, 1, "volume emitter: the emitter object moves");
        float h = ve switch
        {
            NiPSysSphereEmitter s => Math.Abs(s.Radius),
            NiPSysCylinderEmitter c => Math.Max(Math.Abs(c.Radius), Math.Abs(c.Height)),
            NiPSysBoxEmitter b => Math.Max(Math.Abs(b.Width), Math.Max(Math.Abs(b.Height), Math.Abs(b.Depth))),
            _ => float.NaN,
        };
        if (float.IsNaN(h)) return (Aabb.Empty, 1, $"emitter {ve.GetType().Name}: not in the particle model");
        // the object's own frame: a cube of half-size h about its origin, carried by the object's model transform
        var model = rig.Model(obj, null, 0, false);   // identity for the root: its own transform is left out
        var bx = Aabb.Empty;
        for (int k = 0; k < 8; k++) bx = bx.Add(model.Apply(new Vector3((k & 1) == 0 ? -h : h, (k & 2) == 0 ? -h : h, (k & 4) == 0 ? -h : h)));
        return (bx, Math.Abs(model.S), null);
    }

    /// <summary>Whether a controlled block drives a node's transform: its controller type names a transform or keyframe
    /// controller, or its interpolator is an NiTransformInterpolator (the test NifMotion uses for sequences). An emitter or
    /// modifier-active controller block is not one (nif.xml: they animate birth rate, emitter-active, a modifier's active flag).</summary>
    static bool IsTransformBlock(NifFile f, NiflySharp.Structs.ControlledBlock cb)
    {
        string ctype = cb.ControllerType?.ToString() ?? "";
        int ii = cb.Interpolator?.Index ?? -1;
        var interp = ii >= 0 && ii < f.Blocks.Count ? f.Blocks[ii] : null;
        return ctype.Contains("Transform", StringComparison.Ordinal) || ctype.Contains("Keyframe", StringComparison.Ordinal) || interp is NiTransformInterpolator;
    }

    /// <summary>Nodes a sequence's transform block or a direct transform controller drives.</summary>
    static HashSet<object> AnimatedNodes(NifFile f, bool transformOnly = false)
    {
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var seq in f.Blocks.OfType<NiControllerSequence>())
            foreach (var cb in seq.ControlledBlocks ?? new())
                if (cb.NodeName?.ToString() is { Length: > 0 } nn && (!transformOnly || IsTransformBlock(f, cb))) names.Add(nn);
        var set = new HashSet<object>(ReferenceEqualityComparer.Instance);
        foreach (var n in f.Blocks.OfType<NiAVObject>())
        {
            if (n.Name?.String is { } nm && names.Contains(nm)) { set.Add(n); continue; }
            int ci = n.Controller?.Index ?? -1, guard = 0;
            while (ci >= 0 && ci < f.Blocks.Count && guard++ < 64)
            {
                var b = f.Blocks[ci];
                if (b is NiTransformController || b.GetType().Name.Contains("KeyframeController", StringComparison.Ordinal)) { set.Add(n); break; }
                ci = b is NiTimeController tc ? (tc.NextController?.Index ?? -1) : -1;
            }
        }
        return set;
    }
}
