using System.Numerics;

using HousecarlCore;

namespace SkyWright.Engine;

// REACH REGION (G1 build list, Stage A, rows 1-3): a world-space box guaranteed to contain every point a placed
// reference's geometry can occupy, built from the file without estimating from bounds or OBND.
//  * static shapes: the box of the decoded vertices under the placement transform (exact);
//  * moving shapes: the swept outer bound (SweptGeometry: sampled poses grown by the certified delta);
//  * a switch node: the union of every child, whichever is active;
//  * particle systems: ParticleReach's modelled bound — `uncertified: particle dynamics model`;
//  * a shape swayed by wind (Tree_Anim): its file geometry — `uncertified: wind amplitude`;
//  * anything else refuses with its reason.
// Status: certified | uncertified (Labels name every reason) | refused | no_geometry.

/// <summary>One uncertified part: its label and its world box at the file pose (wind) or under the particle model.</summary>
public sealed record ReachPart(string Label, Aabb Box);

/// <summary>Part 3b: an in-game measurement that a model absent from the order draws nothing: the model path, what the provider
/// search found (e.g. "found only in an inactive mod"), and the evidence (frames).</summary>
public sealed record AbsenceMeasurement(string ModelPath, string ProviderNote, string Evidence);

/// <summary>A reach region. <c>Box</c> is world space; empty for refused and no_geometry. <c>Labels</c> lists each reason the
/// region is not certified (empty when certified); <c>UncertifiedParts</c> gives each such part's own box.</summary>
public sealed record ReachRegion(string Status, string? Reason, Aabb Box, string Policy, IReadOnlyList<string> Labels,
                                 IReadOnlyList<ReachPart> UncertifiedParts, [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)] ReachDrop? Drop = null);

/// <summary>A drop (physics rule): <c>Distance</c> fallen, the placed and the landed box (the region is their union), and the
/// support met first. Carried beside the region so a consumer can evaluate the object at contact.</summary>
public sealed record ReachDrop(string Status, double Distance, Aabb Placed, Aabb Landed, string? SupportId, double DistanceMax = double.NaN,
                               double TiltMargin = double.NaN, double RenderBelowCollision = double.NaN,
                               bool LowerBound = false, PushCause Push = PushCause.None, double PushDepth = double.NaN,
                               double PushUpDepth = double.NaN, bool MovesOnLoad = false, double ShellCore = double.NaN, RackMountReading? Mount = null);

/// <summary>The reach rules (overlapprobe <c>--reach-rules</c>). Null = the rules off: the region is exactly what it was
/// without them. <c>Physics</c>: a pinned constrained body gets its hinge envelope, a free body the drop. <c>Graph</c>:
/// binding member paths, the reference-pose generator, a missing sequence with no transform source. <c>SwitchMotion</c>: a
/// switch whose children animate gets the union of their swept regions, wind set applied. <c>BookArt</c>: the base is a
/// BOOK, or a Static some BOOK names as InventoryArt: the closed (stored) pose, a modelling rule. <c>BaseRecordType</c>: the
/// placing base's signature (a STAT is treated as not simulated, an untested assumption that is labelled).
/// <c>Supports</c>: the candidate supports under a world box (the caller's state population); <c>Layers</c>: the COLL
/// records by Index. <c>Skin</c>: skin weight sources that disagree only on vertices no triangle draws do not refuse; a
/// skin whose sources disagree on drawn vertices is posed under both and labelled (MotionReadOptions.SkinUnion).
/// Part 3b: <c>ClipNames</c>, <c>Blend</c>, <c>MissingSequence</c> (GraphOptions); <c>PathHull</c>: a NiPathInterpolator node is
/// bounded by its path's control-polygon hull grown by the subtree's radius; <c>Emitter</c>: a moving particle emitter is bounded
/// by its swept origins grown by the particle offset box; <c>Transfers</c>: confirmed models a refused graph model may take its
/// confirmation from (PoseTransfer); <c>Gravity</c>: settled-gravity observations for particle systems.</summary>
public sealed record ReachRules(bool Physics = false, bool Graph = false, bool SwitchMotion = false, bool BookArt = false,
                                string? BaseRecordType = null, Func<Aabb, IEnumerable<DropSupport>>? Supports = null,
                                IReadOnlyDictionary<uint, CollisionLayerRule>? Layers = null,
                                IReadOnlyList<PoseConfirmation>? Confirmations = null, string? ModelPath = null, bool Skin = false,
                                bool Particles = false, SettleDecision? Settle = null,
                                SettleFacts? SettleFacts = null, IReadOnlyList<SettleExemption>? SettleExemptions = null,
                                bool Rest = false,
                                bool ClipNames = false, bool Blend = false, bool MissingSequence = false, bool PathHull = false, bool Emitter = false,
                                IReadOnlyList<TransferSource>? Transfers = null, IReadOnlyList<SettledGravity>? Gravity = null,
                                IReadOnlyList<MeasuredApproximation>? Approximations = null, RackMountReading? Rack = null);

/// <summary>Part 3b round 2 (reach rule `approx`): an in-game measured approximation of a model's extent, as data - the model path
/// and the sha256 of the measured bytes, the measured box in the model's frame (grown by its stated margin), and its label (what was
/// measured, burst or frame counts, the margin, what it could understate). It is reported as a labelled part beside the file bound,
/// and the region holds it too (a measurement outside the file bound would widen the region rather than be dropped).</summary>
public sealed record MeasuredApproximation(string ModelPath, string Sha256, Aabb ModelBox, string Label);

/// <summary>A confirmation, as data, that a model whose animation cannot be read rests at its stored pose: the model path
/// (Data-relative), the sha256 of the exact bytes compared, and where the evidence (an in-game screenshot beside a render of
/// the stored pose from the same view) is kept. A refused animated model whose path AND bytes match one is read at its
/// stored pose, labelled; no confirmation, no change.</summary>
public sealed record PoseConfirmation(string ModelPath, string Sha256, string Evidence,
                                      IReadOnlyDictionary<string, PoseLocal>? Locals = null, string? Label = null,
                                      string? Clip = null, float[]? Sweep = null);

/// <summary>A confirmed node's local transform (rows act on a row vector, as the motion reader's), given as data by the
/// confirmation: the pose the in-game comparison matched, written out node by node so no clip is parsed here.</summary>
public sealed record PoseLocal(System.Numerics.Vector3 T, System.Numerics.Matrix4x4 R, float S);

/// <summary><c>ParticlesAside</c>: the region of the render shapes alone. Particle systems and their measured approximations are
/// left out and named by <see cref="MeshReach.ParticlesAsideLabel"/>: they are runtime state, not a body anything rests on or meets.</summary>
public sealed record ReachOptions(double MotionDelta = 0.5, int MotionBudget = 4096, ReachRules? Rules = null, bool ParticlesAside = false);

public static class MeshReach
{
    // reach_region_v2 (2026-11-08): a model-less activator's primitive is no_geometry whatever its stated layer (station AD),
    // and a measured primitive table decides Box and PortalBox shapes only (station Q measured no other).
    public const string PolicyVersion = "reach_region_v2";
    public const string WindLabel = "uncertified: wind amplitude";
    public const string ParticleLabel = "uncertified: particle dynamics model";
    public const string ParticleModelLabel = "uncertified: particle extent from the file's modifiers (particle_model_v4: speed, lifetime, gravity x 1.6, a planar bomb along its axis, turbulence 500 x Turbulence x Scale per axis, drag per 1/30 s, a 1/30 s explicit-Euler drift, controller keys, size; laws from the Gamebryo 2.3/2.6 SDK source, not read from Skyrim's binary)";
    public const string SettledGravityLabelPrefix = "uncertified: gravity frame and sign settled by in-game observation";
    public const string BookClosedLabel = "rest: closed (modelling rule)";
    /// <summary>The rest rule's tilt margin on a landed body (the drop translates rigidly; a body that tilts or turns on landing
    /// stays inside the landed box grown by the half-diagonal minus the smallest half-extent of its placed box, bottom on the
    /// support). The margin is the per-case tolerance for comparing a landing with the game.</summary>
    public const string TiltLabel = "rest: tilt on landing not modelled; the landed box is grown by the tilt margin (half-diagonal minus smallest half-extent of the placed box), bottom on the support";
    public const string InheritedSpeedLabel = "uncertified: inherited particle velocity from the moving object's speed, estimated from 1024 samples x 1.25 (not a certified derivative bound), kept over the lifetime";
    public const string MovingEmitterLabel = "uncertified: moving emitter - the emitter's swept volume over its animation grown by the particle offset box (rotation-invariant where a chain turns)";
    public const string ReferencePoseLabel = "uncertified: reference-pose generator read by class name (no engine source)";
    public const string MissingSequenceLabel = "uncertified: graph names a missing sequence; no transform source in the file";
    public const string MissingSequenceUnionLabel = "uncertified: graph names a missing sequence; region holds the stored pose and every sequence in the file";
    public const string ConfirmedPoseLabel = "rest: stored pose (confirmed by in-game comparison)";
    public const string StaticGraphLabel = "uncertified: a Static base's graph may not run; the region holds the stored pose and every graph pose";
    public const string ClipSweptLabel = "rest: a confirmed clip swept (confirmed by in-game look)";
    public const string StartStatePoseLabel = "rest: graph start-state pose (confirmed by in-game comparison)";
    public const string PrimitiveActivatorReason = "primitive on a model-less activator: not solid whatever its stated layer (measured in the lab, station T and station AD: an item and the player passed through every layer that occurs on them)";
    public const string ParticlesAsideLabel = "particles set aside: the region holds the render shapes only";
    public const string SkinUnionLabel = "uncertified: skin weight sources disagree; region holds both sources' placement (renderer's source untested)";

    static ReachRegion Refuse(string why) => new("refused", why, Aabb.Empty, PolicyVersion, Array.Empty<string>(), Array.Empty<ReachPart>());

    /// <summary>The reach region of a NIF placed by a reference's transform.</summary>
    public static ReachRegion OfModel(byte[] nif, Vec3 position, Vec3 rotation, float scale,
                                      Func<string, byte[]?>? read = null, ReachOptions? options = null)
    {
        options ??= new ReachOptions();
        // rest rule part 2: a weapon rack's starting item is mounted at its type's pivot (keyframed; no drop) or refused by name
        if (options.Rules?.Rack is { } rk && rk.Status != "not-mounted")
        {
            if (rk.Status != "mounted" || rk.Pose is null) return Refuse(rk.Reason ?? RackMount.RefusedReason);
            var inner = OfModel(nif, rk.Pose.Position, rk.Pose.Rotation, scale, read,
                                options with { Rules = options.Rules with { Rack = null, Physics = false, Rest = false } });
            if (inner.Status is "refused" or "no_geometry") return inner;
            var mbox = inner.Box;
            if (PhysicsReach.MovingCollision(nif, rk.Pose.Position, rk.Pose.Rotation, scale) is { } mc) mbox = mbox.Union(SpatialOverlap.SoupBounds(mc.World));
            var mlabels = new List<string>(inner.Labels) { $"{RackMount.MountedLabel} (trigger {rk.Trigger}, node {rk.Node})" };
            return new("uncertified", null, mbox, PolicyVersion, mlabels, inner.UncertifiedParts,
                       new ReachDrop("mounted", 0, mbox, mbox, rk.Trigger, Mount: rk));
        }
        if (options.Rules is not null) return OfModelRules(nif, position, rotation, scale, read, options, options.Rules);
        MotionModel motion;
        try { motion = NifMotion.Read(nif, read); }
        catch (Exception ex) { return Refuse("unreadable mesh: " + ex.Message); }
        var labels = new List<string>(); var parts = new List<ReachPart>();
        var box = Aabb.Empty;

        // particle systems: the declared model, labelled
        foreach (var p in options.ParticlesAside ? Array.Empty<ParticleBound>() : ParticleReach.Bounds(nif))
        {
            if (p.Status != "modelled") return Refuse($"particles '{p.SystemName}': {p.Reason}");
            var w = BoxWorld(p.ModelBox, position, rotation, scale);
            if (Math.Abs(scale) < 1) w = Grow(w, (p.Displacement + p.ParticleRadius) * (1 - Math.Abs(scale)));   // world-space particles do not shrink with the reference
            box = box.Union(w); parts.Add(new ReachPart(ParticleLabel, w));
            if (!labels.Contains(ParticleLabel)) labels.Add(ParticleLabel);
        }
        if (options.ParticlesAside && ParticleReach.Bounds(nif).Count > 0) labels.Add(ParticlesAsideLabel);

        if (motion.Status == "moving")
        {
            // each sample's placed bounds as it is posed; no posed sample is kept, so memory does not grow with the sample count
            var region = SweptGeometry.PlacedSampleBounds(nif, motion, null, VolumeGeometry.Render, position, rotation, scale,
                                                          new MotionOptions(options.MotionDelta, options.MotionBudget), read);
            if (region.Status != "built") return Refuse("motion: " + region.Reason);
            foreach (var (bounds, delta) in region.Samples) box = box.Union(Grow(bounds, delta));
        }
        else if (motion.Status == "static")
        {
            var posed = NifMotion.Pose(nif, motion, "stored", 0f, VolumeGeometry.Render, read);
            if (posed.Status == "posed") box = box.Union(SpatialOverlap.SoupBounds(SpatialOverlap.TransformSoup(posed.Soup!, position, rotation, scale)));
            else if (posed.Reason != "render: no visible triangles") return Refuse(posed.Reason ?? "the stored pose could not be read");
        }
        else
        {
            // A switch node (every child is a candidate: their union holds whichever is active) or a wind-swayed shape
            // (its file geometry, labelled) in an otherwise unanimated, unskinned mesh. Anything else keeps the refusal.
            var refusing = motion.Shapes.Where(s => s.Refusal is not null && !s.Refusal.StartsWith("particles", StringComparison.Ordinal)).ToList();
            bool onlySwitchWind = refusing.Count > 0
                && refusing.All(s => s.Refusal!.StartsWith("switch node", StringComparison.Ordinal) || s.Refusal!.StartsWith("wind:", StringComparison.Ordinal))
                && motion.Animations.Count == 0 && motion.Shapes.All(s => s.Mover != "skinned");
            if (!onlySwitchWind) return Refuse(motion.Reason ?? "motion refused");
            var g = NifGeometry.MeasureVisibleRender(nif, includeTriangles: true);
            if (g.Model is not { Soup: { } soup, SoupShapeOfTriangle: { } map } m || !m.RenderComplete) return Refuse("render geometry unreadable or partial");
            var world = SpatialOverlap.TransformSoup(soup, position, rotation, scale);
            box = box.Union(SpatialOverlap.SoupBounds(world));
            // a Tree_Anim shape is wind-swayed whatever the motion reader named first (a switch node is reported before
            // wind), so the wind set is read from the shaders as well (NifWind)
            var wind = new HashSet<int>(refusing.Where(s => s.Refusal!.StartsWith("wind:", StringComparison.Ordinal)).Select(s => s.ShapeIndex));
            foreach (var ws in NifWind.Shapes(nif)) wind.Add(ws.ShapeIndex);
            foreach (int si in wind)
            {
                var pb = Aabb.Empty;
                for (int t = 0; t < map.Count; t++)
                    if (map[t] == si) for (int k = 0; k < 3; k++) pb = pb.Add(world.Vertices[world.Indices[3 * t + k]]);
                if (!pb.IsEmpty) parts.Add(new ReachPart(WindLabel, pb));
            }
            if (wind.Count > 0 && !labels.Contains(WindLabel)) labels.Add(WindLabel);
        }
        if (box.IsEmpty) return new("no_geometry", "no visible geometry and no particle system", Aabb.Empty, PolicyVersion, labels, parts);
        if (!Finite(box) || parts.Any(x => !Finite(x.Box))) return Refuse("non-finite reach box");
        return new(labels.Count == 0 ? "certified" : "uncertified", null, box, PolicyVersion, labels, parts);
    }

    /// <summary>The reach region under the reach rules (<see cref="ReachRules"/>). The order: the motion reading the rules
    /// ask for (books at their stored, closed pose; switch children all read; Tree_Anim shapes as labelled file geometry;
    /// graph options), the region that reading gives, then the physics rule on top (a pinned island's hinge envelope, or the
    /// free bodies' drop). Every rule-based part carries its label; a rule-based region is never certified.</summary>
    static ReachRegion OfModelRules(byte[] nif, Vec3 position, Vec3 rotation, float scale, Func<string, byte[]?>? read,
                                    ReachOptions options, ReachRules rules)
    {
        bool isStatic = rules.BaseRecordType is "STAT" or "Static";
        PhysicsModel? phys = rules.Physics ? PhysicsReach.Classify(nif) : null;
        if (phys is { Kind: "refused" }) return Refuse(phys.Refusal ?? "physics refused");
        var mopts = new MotionReadOptions(IgnorePhysics: rules.Physics, IgnoreAnimation: rules.BookArt, SwitchUnion: rules.SwitchMotion,
                                          WindAsStatic: rules.SwitchMotion, Graph: rules.Graph ? new GraphOptions(true, true, true, ClipNames: rules.ClipNames, Blend: rules.Blend, MissingSequenceUnion: rules.MissingSequence) : null,
                                          SkinUnion: rules.Skin, PathHull: rules.PathHull);
        MotionModel motion;
        try { motion = NifMotion.Read(nif, read, null, mopts); }
        catch (Exception ex) { return Refuse("unreadable mesh: " + ex.Message); }
        bool confirmed = false;
        PoseConfirmation? conf = null;
        LocalsSource? confirmedPose = null;
        string? transferLabel = null;
        // in-game evidence (a confirmation, or a transfer from one) outranks the analytic blend envelope: it is decided on the
        // reading without the blend rule, as before that rule existed
        bool refusedBare = motion.Status == "refused";
        if (!refusedBare && rules.Blend && mopts.Graph is { } gopt && ((rules.Confirmations?.Count ?? 0) > 0 || (rules.Transfers?.Count ?? 0) > 0))
        {
            try { refusedBare = NifMotion.Read(nif, read, null, mopts with { Graph = gopt with { Blend = false } }).Status == "refused"; }
            catch (Exception ex) { return Refuse("unreadable mesh: " + ex.Message); }
        }
        if (refusedBare && !Confirmed(nif, rules) && rules.Transfers is { Count: > 0 } tsrc && read is not null
            && NifMotion.GraphFacts(nif, read, mopts.Graph) is { } facts)
        {
            // Part 3b transfer: the graph files are byte-identical to a confirmed model's and the stored locals on every driven node
            // equal that model's: the confirmation (stored pose) carries over
            var tr = PoseTransfer.Decide(facts.Files, facts.DrivenLocals, tsrc);
            if (tr.From is { } from)
            {
                try { motion = NifMotion.Read(nif, read, null, mopts with { IgnoreAnimation = true }); }
                catch (Exception ex) { return Refuse("unreadable mesh: " + ex.Message); }
                transferLabel = $"rest: stored pose, transferred from {from.ModelPath} ({tr.Reason}; that model: {from.Label})";
            }
        }
        if (refusedBare && Confirmed(nif, rules))
        {
            conf = ConfirmationOf(nif, rules);
            // the animation cannot be read, but an in-game comparison confirmed these exact bytes rest at their stored pose - or, when
            // the confirmation names a clip, that they play that clip: it alone is read from the graph and swept (its span or window)
            try { motion = NifMotion.Read(nif, read, null, conf?.Clip is { } clip ? mopts with { OnlyClip = new ClipChoice(clip, conf.Sweep) } : mopts with { IgnoreAnimation = true }); }
            catch (Exception ex) { return Refuse("unreadable mesh: " + ex.Message); }
            if (conf?.Clip is not null && motion.Status == "refused") return Refuse(motion.Reason ?? "confirmed clip refused");
            confirmed = true;
            // a confirmed non-stored pose arrives as explicit node locals (data): the rig is posed with them, no clip is read
            if (conf?.Locals is { Count: > 0 } loc)
                confirmedPose = new LocalsSource(loc) { Animation = new MotionAnimation("confirmed", "pose-confirmation", "confirmed", "CONFIRMED", 0, 0, null, conf.Evidence) };
        }
        var labels = new List<string>(motion.Labels ?? Array.Empty<string>()); var parts = new List<ReachPart>();
        if (transferLabel is not null) labels.Add(transferLabel);
        if (confirmed) labels.Add(conf?.Label ?? (conf?.Clip is not null ? ClipSweptLabel : confirmedPose is null ? ConfirmedPoseLabel : StartStatePoseLabel));
        var box = Aabb.Empty;
        ReachDrop? drop = null;
        NodeSweeper? sweeper = rules.Particles && rules.Emitter && motion.Status != "refused" ? (name, rad) => NifMotion.NodeSweep(nif, motion, read, name, rad) : null;
        // Part 3b round 2: a settled-gravity observation for these exact bytes - the model-frame direction of world -z at this placement
        Func<string, System.Numerics.Vector3?>? settled = null;
        SettledGravity? sg = rules.Particles ? SettledOf(nif, rules) : null;
        if (sg is not null)
        {
            var rot = SpatialOverlap.RefrRotation(rotation);
            var dn = System.Numerics.Vector3.Normalize(System.Numerics.Vector3.Transform(-System.Numerics.Vector3.UnitZ, System.Numerics.Matrix4x4.Transpose(rot)));
            settled = name => sg.Systems.Count == 0 || sg.Systems.Contains(name, StringComparer.Ordinal) ? dn : null;
        }
        foreach (var p0 in options.ParticlesAside ? Array.Empty<ParticleBound>()
                           : rules.Particles ? ParticleReach.BoundsFromModifiers(nif, sweeper, settled) : ParticleReach.Bounds(nif, false))
        {
            var p = p0;
            if (p.Status != "modelled") return Refuse($"particles '{p.SystemName}': {p.Reason}");
            string plabel = rules.Particles ? ParticleModelLabel : ParticleLabel;
            if (p.MovingEmitter && !labels.Contains(MovingEmitterLabel)) labels.Add(MovingEmitterLabel);
            if (p.InheritedTravel > 0 && !labels.Contains(InheritedSpeedLabel)) labels.Add(InheritedSpeedLabel);
            var w = BoxWorld(p.ModelBox, position, rotation, scale);
            if (p.WorldReadingBox is { } wr)
            {
                // World Aligned's second reading: the world-frame gravity growth after placement (world-space particles: not shrunk)
                var b = BoxWorld(wr, position, rotation, scale); double k = Math.Max(1, Math.Abs(scale));
                w = w.Union(new Aabb(new Vec3(b.Min.X - Up(p.WorldGrow.X * k), b.Min.Y - Up(p.WorldGrow.Y * k), b.Min.Z - Up(p.WorldGrow.Z * k)),
                                     new Vec3(b.Max.X + Up(p.WorldGrow.X * k), b.Max.Y + Up(p.WorldGrow.Y * k), b.Max.Z + Up(p.WorldGrow.Z * k)), false));
            }
            // world-space particles do not shrink with a reference scale below 1; a world-composed fold grows by its gravity-free
            // displacement only (its gravity is composed unscaled below)
            double comp = p.Fold is { Axis: < 0 } && sg is not null ? p.Fold.Lateral : p.Displacement + p.ParticleRadius;
            if (Math.Abs(scale) < 1) w = Grow(w, comp * (1 - Math.Abs(scale)));
            if (p.Fold is { Axis: < 0 } wf && sg is not null)
            {
                // Part 3b round 2: world -z off the model axes: the gravity-free box is placed, then the settled gravity is composed in
                // the world frame - the top capped at the emitter's top plus the ballistic apex (the weaker pull; drag only lowers it),
                // the bottom lowered by the gravity term with no drag (x 1.6, the stronger pull); world-space growth is not shrunk by a
                // reference scale below 1
                double ks = Math.Max(1, Math.Abs(scale)), kg = Math.Min(1, Math.Abs(scale));
                var ew = BoxWorld(wf.Emitter, position, rotation, scale);
                double climb = ParticleMotionReach.SettledAxis(wf.V * ks, wf.T, wf.R * ks, wf.AIso * ks, 0, wf.GWith * ks, wf.GClimb * kg, true).Max
                               + wf.AIso * ks * wf.T * ParticleReach.EulerStep / 2;
                double fall = wf.GWith * ks * (wf.T * wf.T / 2 + wf.T * ParticleReach.EulerStep / 2);
                float top = MathF.Min(w.Max.Z, Up(ew.Max.Z + climb)), bottom = Down(w.Min.Z - fall);
                w = new Aabb(new Vec3(w.Min.X, w.Min.Y, bottom), new Vec3(w.Max.X, w.Max.Y, MathF.Max(top, MathF.Max(ew.Max.Z, bottom))), false);
                p = p with { Fold = wf with { Axis = 2 } };   // world-composed: the support-plane term below works in the world frame
            }
            if (p.Fold is { } fd && sg is not null)
            {
                string slab = $"{SettledGravityLabelPrefix} ({sg.Evidence}): the planar gravity acts toward world -z only; the climb against it is the ballistic apex";
                if (!labels.Contains(slab)) labels.Add(slab);
                if (rules.Supports is not null)
                {
                    var ew = BoxWorld(fd.Emitter, position, rotation, scale);
                    double ks = Math.Max(1, Math.Abs(scale)), kg = Math.Min(1, Math.Abs(scale));
                    var fw = fd with { V = fd.V * ks, R = fd.R * ks, GClimb = fd.GClimb * kg };
                    var col = new Aabb(new Vec3(w.Min.X, w.Min.Y, float.NegativeInfinity), w.Max, false);
                    var sp = SupportPlane.Apply(w, ew, fw, rules.Supports(col).ToList());
                    if (sp.Region is { } tight) { w = tight; if (sp.Label is { } spl && !labels.Contains(spl)) labels.Add(spl); }
                    else labels.Add($"uncertified: support-plane term not applied for '{p.SystemName}' ({sp.NotApplied})");
                }
            }
            box = box.Union(w); parts.Add(new ReachPart(plabel, w));
            if (!labels.Contains(plabel)) labels.Add(plabel);
        }
        if (options.ParticlesAside && ParticleReach.Bounds(nif, false).Count > 0) labels.Add(ParticlesAsideLabel);
        if (motion.Status == "refused") return Refuse(motion.Reason ?? "motion refused");
        Aabb Stored()
        {
            var posed = confirmedPose is null ? NifMotion.Pose(nif, motion, "stored", 0f, VolumeGeometry.Render, read)
                                              : NifMotion.PoseWith(NifMotion.RigOf(nif, motion, read), confirmedPose, "confirmed", 0f, VolumeGeometry.Render);
            if (posed.Status == "posed") return SpatialOverlap.SoupBounds(SpatialOverlap.TransformSoup(posed.Soup!, position, rotation, scale));
            if (posed.Reason == "render: no visible triangles") return Aabb.Empty;
            throw new ReachRefusal(posed.Reason ?? "the stored pose could not be read");
        }
        try
        {
            if (motion.Status == "moving" && motion.Animations.Count > 0)
            {
                // each sample's placed bounds as it is posed; no posed sample is kept, so memory does not grow with the sample count
                var region = SweptGeometry.PlacedSampleBounds(nif, motion, null, VolumeGeometry.Render, position, rotation, scale,
                                                              new MotionOptions(options.MotionDelta, options.MotionBudget), read);
                if (region.Status != "built") return Refuse("motion: " + region.Reason);
                foreach (var (bounds, delta) in region.Samples) box = box.Union(Grow(bounds, delta));
                if (rules.Blend && !confirmed)   // a confirmed clip or pose is what the look showed: no blend is added to it
                {
                    // Part 3b: the blends between the graph's generator outputs (the sweep holds each animation's own poses only)
                    var (bb, blab, bwhy) = BlendCached(nif, motion, read, (float)options.MotionDelta);
                    if (bwhy is not null) return Refuse(bwhy);
                    if (!bb.IsEmpty) { box = box.Union(BoxWorld(bb, position, rotation, scale)); if (blab is not null && !labels.Contains(blab)) labels.Add(blab); }
                }
                if (rules.MissingSequence && labels.Any(l => l.StartsWith(MissingSequenceUnionLabel, StringComparison.Ordinal)))
                    box = box.Union(Stored());   // the missing name may play nothing: the stored pose is held as well
                if (rules.Graph && isStatic && motion.Graph is not null)
                {
                    // a Static base may never run its graph: the stored pose is held as well as every pose the graph reaches
                    box = box.Union(Stored());
                    if (!labels.Contains(StaticGraphLabel)) labels.Add(StaticGraphLabel);
                }
            }
            else box = box.Union(Stored());
        }
        catch (ReachRefusal r) { return Refuse(r.Message); }
        if (rules.PathHull)
        {
            // Part 3b: a NiPathInterpolator node's subtree anywhere on its path, any rotation
            var (pb, plabs, pwhy) = NifMotion.PathBox(nif, motion, read);
            if (pwhy is not null) return Refuse(pwhy);
            if (!pb.IsEmpty) { box = box.Union(BoxWorld(pb, position, rotation, scale)); foreach (var l in plabs) if (!labels.Contains(l)) labels.Add(l); }
        }
        if (rules.BookArt && !labels.Contains(BookClosedLabel)) labels.Add(BookClosedLabel);
        if (rules.SwitchMotion)
        {
            // Tree_Anim shapes (read from the shaders) are file geometry with the wind label, as in the default static path;
            // one that moves on an animated chain is not bounded
            var windSet = NifWind.Shapes(nif).Select(w => w.ShapeIndex).ToHashSet();
            if (windSet.Count > 0)
            {
                // driven = on an animated node's chain (a skinned shape at rest is posed at its stored skin, not moved)
                if (motion.Shapes.Any(s => windSet.Contains(s.ShapeIndex) && s.DrivenBy.Count > 0))
                    return Refuse("wind: a Tree_Anim shape moves on an animated chain; its sway is not bounded");
                var g = NifGeometry.MeasureVisibleRender(nif, includeTriangles: true);
                if (g.Model is not { Soup: { } soup, SoupShapeOfTriangle: { } map } m || !m.RenderComplete) return Refuse("render geometry unreadable or partial");
                var world = SpatialOverlap.TransformSoup(soup, position, rotation, scale);
                foreach (int si in windSet)
                {
                    var pb = Aabb.Empty;
                    for (int t = 0; t < map.Count; t++)
                        if (map[t] == si) for (int k = 0; k < 3; k++) pb = pb.Add(world.Vertices[world.Indices[3 * t + k]]);
                    if (!pb.IsEmpty) { parts.Add(new ReachPart(WindLabel, pb)); box = box.Union(pb); }
                }
                if (!labels.Contains(WindLabel)) labels.Add(WindLabel);
            }
        }
        if (phys is { Kind: not ("none" or "fixed") } ph)
        {
            // the crossing marks (drop-crossing lane 2, 2026-10-13; no labels): a drop that pushed the body off a piece it overlapped at
            // placement is refused for a settling body (it moves on load; where it comes to rest is not read), its drop kept as a lower bound
            // only for lab-covered causes; an impalement keeps no drop (the direction is not read); every pushed-off body gives the
            // drop's two push depths. A drop read with the radius that is longer than the same drop read without it (the body
            // started inside a support's shell and the crossing test missed it) is refused too; pushed-off drops are longer than the
            // reduced readings by design, so the shell guard reads only drops with nothing pushed off.
            static string U(double x) => PhysicsReach.FormatDepth(x);
            (string? Reason, bool LowerBound, PushCause Push, double PushDepth, double PushUpDepth, bool Unread, double ShellCore) Mark(
                double distance, Aabb placedBox, IEnumerable<DropSupport> sup, DropResult dr)
            {
                if (double.IsNaN(distance)) return (null, false, PushCause.None, double.NaN, double.NaN, false, double.NaN);
                if (dr.PushedOff)
                {
                    string depths = $"push {U(dr.PushDepth)} u down against {U(dr.PushUpDepth)} u up";
                    if ((dr.Causes & PushCause.Impaled) != 0)
                        return ($"{PhysicsReach.ImpaledReason}: {depths}", false, dr.Causes, dr.PushDepth, dr.PushUpDepth, true, double.NaN);
                    return ($"{(dr.LowerBound ? PhysicsReach.PushedOffReason : PhysicsReach.PushedOffUnverifiedReason)}: the reader's drop is {U(distance)} u",
                            dr.LowerBound, dr.Causes, dr.PushDepth, dr.PushUpDepth, false, double.NaN);
                }
                if (PhysicsReach.CoreDrop(nif, position, rotation, scale, placedBox, sup, rules.Layers ?? new Dictionary<uint, CollisionLayerRule>()) is { } core
                    && distance > core + 0.01)
                    return ($"{PhysicsReach.ShellCrossingReason}: the reader's drop is {U(distance)} u; with less of the radius it is {U(core)} u",
                            false, PushCause.None, double.NaN, double.NaN, false, core);
                return (null, false, PushCause.None, double.NaN, double.NaN, false, double.NaN);
            }
            if (isStatic)
            {
                if (!labels.Contains(PhysicsReach.StaticAssumptionLabel)) labels.Add(PhysicsReach.StaticAssumptionLabel);
            }
            else if (ph.Kind == "pinned")
            {
                var env = PhysicsReach.HingeEnvelope(nif, motion, position, rotation, scale, options.MotionDelta, options.MotionBudget, read);
                if (env.Refusal is not null) return Refuse(env.Refusal);
                box = box.Union(env.Box);
                labels.Add(PhysicsReach.HingeLabel);
            }
            else if (ph.Kind == "mixed") return Refuse("physics: pinned and free bodies in one file are not supported");
            else if (SettleOf(nif, rules) is { Status: "rests" } rest)
            {
                // the load-settle rule: an exempt object is not settled at load; it rests at its placed pose (box and its
                // collision) until disturbed, labelled with the exemption it rests under; no drop
                if (PhysicsReach.MovingCollision(nif, position, rotation, scale) is not { } hc) return Refuse("physics: a movable body's collision is empty or unread");
                double renderBelowR = box.IsEmpty ? double.NaN : Math.Max(0, SpatialOverlap.SoupBounds(hc.World).Min.Z - box.Min.Z);
                box = box.Union(SpatialOverlap.SoupBounds(hc.World));
                labels.Add(LoadSettle.RestsLabel);
                if (rest.Exemption is { } ex) labels.Add($"load-settle exemption: {ex.Kind} {ex.Value} ({ex.Evidence})");
                if (rules.Rest)
                {
                    // the rest rule: the drop it would take is recorded beside the placed box (the gap to what is under it), not applied
                    var placedR = box;
                    var colR = new Aabb(new Vec3(placedR.Min.X, placedR.Min.Y, float.NegativeInfinity), placedR.Max, false);
                    var g = PhysicsReach.Drop(hc.World, placedR, hc.Layer, rules.Supports?.Invoke(colR) ?? Array.Empty<DropSupport>(),
                                              rules.Layers ?? new Dictionary<uint, CollisionLayerRule>());
                    if (g.Status != "refused")
                    {
                        // an exempt body rests where placed: its gap carries the crossing marks, the region is not refused
                        var mk = Mark(g.Distance, placedR, rules.Supports?.Invoke(colR) ?? Array.Empty<DropSupport>(), g);
                        drop = new ReachDrop("rest-gap", mk.Unread ? double.NaN : g.Distance, g.Placed, mk.Unread ? Aabb.Empty : g.Landed,
                                             mk.Unread ? null : g.SupportId, g.DistanceMax, RenderBelowCollision: renderBelowR, LowerBound: mk.LowerBound,
                                             Push: mk.Push, PushDepth: mk.PushDepth, PushUpDepth: mk.PushUpDepth, MovesOnLoad: false, ShellCore: mk.ShellCore);
                    }
                    else if (g.Reason == PhysicsReach.NothingBelow)
                    {
                        drop = new ReachDrop("floats", double.NaN, g.Placed, Aabb.Empty, null);
                        labels.Add(PhysicsReach.FloatsLabel);
                    }
                    else labels.Add("rest gap not read: " + g.Reason);
                }
            }
            else
            {
                if (PhysicsReach.MovingCollision(nif, position, rotation, scale) is not { } mc) return Refuse("physics: a movable body's collision is empty or unread");
                // how far the render mesh reaches below the moving collision at the placed pose (the rest rule: at rest that
                // much render overlap with the support is the author's mesh, not a clash)
                double renderBelow = box.IsEmpty ? double.NaN : Math.Max(0, SpatialOverlap.SoupBounds(mc.World).Min.Z - box.Min.Z);
                var placed = box.Union(SpatialOverlap.SoupBounds(mc.World));
                var column = new Aabb(new Vec3(placed.Min.X, placed.Min.Y, float.NegativeInfinity), placed.Max, false);
                var supports = rules.Supports?.Invoke(column) ?? Array.Empty<DropSupport>();
                var d = PhysicsReach.Drop(mc.World, placed, mc.Layer, supports, rules.Layers ?? new Dictionary<uint, CollisionLayerRule>());
                if (d.Status == "refused" && rules.Rest && d.Reason == PhysicsReach.NothingBelow)
                {
                    // the rest rule: a settling body with nothing below is an outcome (lost: the engine keeps it falling and
                    // puts it back, lab L1), not a refusal; enabled after load it rests at its placed pose (both readings)
                    var lost = new ReachDrop("lost", double.NaN, d.Placed, Aabb.Empty, null);
                    labels.Add(PhysicsReach.LostLabel);
                    if (SettleOf(nif, rules) is { Status: "timing-unknown" })
                    {
                        labels.Add(LoadSettle.TimingUnknownLabel);
                        return new("uncertified", null, placed, PolicyVersion, labels, parts, lost);
                    }
                    return new("no_geometry", PhysicsReach.LostLabel, Aabb.Empty, PolicyVersion, labels, parts, lost);
                }
                // a refused first contact on a loose item keeps what the drop read: placed box, landed box, distance, support met first
                if (d.Status == "refused")
                    return d.SupportId is null ? Refuse(d.Reason ?? PhysicsReach.NothingBelow)
                        : Refuse(d.Reason ?? PhysicsReach.NothingBelow) with { Drop = new ReachDrop(d.Status, d.Distance, d.Placed, d.Landed, d.SupportId) };
                var mark = Mark(d.Distance, placed, supports, d);
                if (mark.Reason is not null)
                    return new("refused", mark.Reason, Aabb.Empty, PolicyVersion, labels, parts,
                               new ReachDrop(d.Status, mark.Unread ? double.NaN : d.Distance, d.Placed, mark.Unread ? Aabb.Empty : d.Landed,
                                             mark.Unread ? null : d.SupportId, d.DistanceMax, LowerBound: mark.LowerBound, Push: mark.Push,
                                             PushDepth: mark.PushDepth, PushUpDepth: mark.PushUpDepth, MovesOnLoad: d.PushedOff && !mark.Unread,
                                             ShellCore: mark.ShellCore));
                box = d.Prism;
                double tilt = double.NaN;
                if (rules.Rest)
                {
                    // tilt on landing (the rule drops rigidly): the landed box grown by the most a rotation about the placed
                    // box's centre can move a face (half-diagonal minus the smallest half-extent), bottom kept on the support
                    var h = d.Placed.Size;
                    double hx = h.X / 2.0, hy = h.Y / 2.0, hz = h.Z / 2.0;
                    tilt = Math.Sqrt(hx * hx + hy * hy + hz * hz) - Math.Min(hx, Math.Min(hy, hz));
                    if (tilt > 0)
                    {
                        float m = Up(tilt);
                        var L = d.Landed;
                        // a lifted body's centre does not descend, so a turn lowers its bottom by at most m (KH-T, rest rule part 1)
                        float below = d.Status == "lifted" ? m : 0f;
                        box = box.Union(new Aabb(new Vec3(L.Min.X - m, L.Min.Y - m, L.Min.Z - below), new Vec3(L.Max.X + m, L.Max.Y + m, L.Max.Z + 2 * m), false));
                    }
                    labels.Add(TiltLabel);
                }
                labels.Add(d.Status == "touching" ? PhysicsReach.DropTouchingLabel : d.Status == "lifted" ? PhysicsReach.LiftedLabel : PhysicsReach.DropLabel);
                if (d.Status == "dropped-range") labels.Add(PhysicsReach.TerrainDiagonalLabel);
                // the landing is decided by a Static whose own file body may move: held only by the Static assumption
                if (d.SupportAssumedStatic && !labels.Contains(PhysicsReach.StaticAssumptionLabel)) labels.Add(PhysicsReach.StaticAssumptionLabel);
                if (ph.Kind == "free-chain") labels.Add(PhysicsReach.RigidChainLabel);
                var sd = SettleOf(nif, rules);
                if (sd is { Status: "timing-unknown" }) labels.Add(LoadSettle.TimingUnknownLabel);   // the prism holds both poses
                else if (sd is { Status: "settles" }) labels.Add(LoadSettle.SettlesLabel);
                drop = new ReachDrop(d.Status, d.Distance, d.Placed, d.Landed, d.SupportId, d.DistanceMax, tilt, rules.Rest ? renderBelow : double.NaN,
                                     PushUpDepth: d.Status == "lifted" ? d.PushUpDepth : double.NaN);
            }
        }
        // runs after the rest rule's early return for a lost body; safe while the classes are disjoint: a lost body is a free physics body, and every approximation names a particle model with no free body (a model in both would skip its approximation)
        if (!options.ParticlesAside && ApproximationOf(nif, rules) is { } ap)
        {
            // Part 3b round 2: the measured approximation beside the file bound (labelled part); the region holds it as well
            var aw = BoxWorld(ap.ModelBox, position, rotation, scale);
            string alab = "in-game approximation (beside the file bound): " + ap.Label;
            parts.Add(new ReachPart(alab, aw)); box = box.Union(aw);
            if (!labels.Contains(alab)) labels.Add(alab);
        }
        if (box.IsEmpty) return new("no_geometry", "no visible geometry and no particle system", Aabb.Empty, PolicyVersion, labels, parts);
        if (!Finite(box) || parts.Any(x => !Finite(x.Box))) return Refuse("non-finite reach box");
        return new(labels.Count == 0 ? "certified" : "uncertified", null, box, PolicyVersion, labels, parts, drop);
    }

    sealed class ReachRefusal(string message) : Exception(message);

    static readonly Dictionary<string, (Aabb, string?, string?)> BlendMemo = new();
    /// <summary>The blend box of a model, memoized per exact bytes, sampling delta and animation set (G1 places one model many times).</summary>
    static (Aabb Box, string? Label, string? Refusal) BlendCached(byte[] nif, MotionModel motion, Func<string, byte[]?>? read, float delta)
    {
        string key = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(nif)) + "|" + delta.ToString("R") + "|" + string.Join(",", motion.Animations.Select(a => a.Id + "@" + a.ClipPath));
        lock (BlendMemo)
            if (BlendMemo.TryGetValue(key, out var hit)) return hit;
        var r = NifMotion.BlendBox(nif, motion, read, delta);
        lock (BlendMemo) BlendMemo[key] = r;
        return r;
    }

    /// <summary>The load-settle decision: the caller's, or decided here from its facts, the exemption list and the model's own
    /// BSX; null when neither is given (today's drop, unlabelled).</summary>
    static SettleDecision DecideWithModel(byte[] nif, SettleFacts f, IReadOnlyList<SettleExemption> l)
    {
        var (bsx, graph) = LoadSettle.NifFacts(nif);
        return LoadSettle.Decide(f with { BsxFlags = bsx, HasBehaviorGraph = graph }, l);
    }

    static SettleDecision? SettleOf(byte[] nif, ReachRules rules)
        => rules.Settle ?? (rules.SettleFacts is { } f && rules.SettleExemptions is { } l ? DecideWithModel(nif, f, l) : null);

    /// <summary>Whether a confirmation names this model: the same Data-relative path (case-insensitive, either slash) AND the
    /// sha256 of these exact bytes.</summary>
    static bool Confirmed(byte[] nif, ReachRules rules)
    {
        if (rules.Confirmations is not { Count: > 0 } list || rules.ModelPath is null) return false;
        static string Norm(string p) => p.Replace('/', '\\').TrimStart('\\').ToLowerInvariant();
        string path = Norm(rules.ModelPath);
        string sha = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(nif)).ToLowerInvariant();
        return list.Any(c => Norm(c.ModelPath) == path && string.Equals(c.Sha256, sha, StringComparison.OrdinalIgnoreCase));
    }

    static MeasuredApproximation? ApproximationOf(byte[] nif, ReachRules rules)
    {
        if (rules.Approximations is not { Count: > 0 } list || rules.ModelPath is null) return null;
        static string Norm(string p) => p.Replace('/', '\\').TrimStart('\\').ToLowerInvariant();
        string path = Norm(rules.ModelPath);
        string sha = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(nif)).ToLowerInvariant();
        return list.FirstOrDefault(c => Norm(c.ModelPath) == path && string.Equals(c.Sha256, sha, StringComparison.OrdinalIgnoreCase));
    }

    static SettledGravity? SettledOf(byte[] nif, ReachRules rules)
    {
        if (rules.Gravity is not { Count: > 0 } list || rules.ModelPath is null) return null;
        static string Norm(string p) => p.Replace('/', '\\').TrimStart('\\').ToLowerInvariant();
        string path = Norm(rules.ModelPath);
        string sha = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(nif)).ToLowerInvariant();
        return list.FirstOrDefault(c => Norm(c.ModelPath) == path && string.Equals(c.Sha256, sha, StringComparison.OrdinalIgnoreCase));
    }

    static PoseConfirmation? ConfirmationOf(byte[] nif, ReachRules rules)
    {
        if (rules.Confirmations is not { Count: > 0 } list || rules.ModelPath is null) return null;
        static string Norm(string p) => p.Replace('/', '\\').TrimStart('\\').ToLowerInvariant();
        string path = Norm(rules.ModelPath);
        string sha = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(nif)).ToLowerInvariant();
        return list.FirstOrDefault(c => Norm(c.ModelPath) == path && string.Equals(c.Sha256, sha, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>The reach rule `primitive`: a reference's primitive under the measured default. A model-less ACTIVATOR's
    /// primitive is not solid whatever its stated layer (no_geometry; stations T and AD); every other case returns the caller's
    /// refusal unchanged. <paramref name="layerIndex"/> is not consulted: the rule is by base type.</summary>
    public static ReachRegion OfPrimitiveRule(string? baseType, bool baseHasModel, uint? layerIndex, string refusal)
        => baseType is "Activator" or "ACTI" && !baseHasModel
            ? new("no_geometry", PrimitiveActivatorReason, Aabb.Empty, PolicyVersion, Array.Empty<string>(), Array.Empty<ReachPart>())
            : Refuse(refusal);

    /// <summary>Part 3b: a model absent from every active provider. With a measurement naming the model path (case-insensitive,
    /// either slash) → no_geometry, reason "model absent from every active provider ({provider note}); measured: {evidence}";
    /// otherwise the caller's refusal unchanged.</summary>
    public static ReachRegion OfAbsentModel(string modelPath, IReadOnlyList<AbsenceMeasurement> measured, string refusal)
    {
        static string Norm(string p) => p.Replace('/', '\\').TrimStart('\\').ToLowerInvariant();
        var m = measured.FirstOrDefault(x => Norm(x.ModelPath) == Norm(modelPath));
        return m is null ? Refuse(refusal)
            : new("no_geometry", $"model absent from every active provider ({m.ProviderNote}); measured: {m.Evidence}", Aabb.Empty, PolicyVersion,
                  Array.Empty<string>(), Array.Empty<ReachPart>());
    }

    /// <summary>A confirmed pose given as node locals (PoseConfirmation.Locals): those nodes take the given local transform,
    /// every other node keeps its stored one.</summary>
    sealed class LocalsSource(IReadOnlyDictionary<string, PoseLocal> locals) : MotionSource
    {
        public override IReadOnlyCollection<string> Nodes => locals.Keys.ToList();
        public override NodeXf Local(string node, NodeXf stored, float t) => locals.TryGetValue(node, out var l) ? new NodeXf(l.R, l.S, l.T) : stored;
        public override (float T, float A, float S) Bound(string node, NodeXf stored, float t0, float t1) => (0, 0, 0);
        public override IEnumerable<float> KeyTimes() => Array.Empty<float>();
    }

    /// <summary>A model-less reference's primitive box (half extents in the reference frame). <paramref name="solidLayer"/>:
    /// true = its collision layer blocks actors (the box is geometry), false = non-solid (no geometry), null = the record
    /// states no layer (refused by name until the default layer is settled).</summary>
    public static ReachRegion OfPrimitive(Vector3 halfExtents, Vec3 position, Vec3 rotation, bool? solidLayer)
    {
        if (solidLayer is null) return Refuse("primitive with no collision layer stated: the default layer is not settled");
        if (solidLayer == false) return new("no_geometry", "primitive on a non-solid collision layer", Aabb.Empty, PolicyVersion, Array.Empty<string>(), Array.Empty<ReachPart>());
        var h = halfExtents;
        var model = new Aabb(new Vec3(-h.X, -h.Y, -h.Z), new Vec3(h.X, h.Y, h.Z), false);
        return new("certified", null, BoxWorld(model, position, rotation, 1f), PolicyVersion, Array.Empty<string>(), Array.Empty<ReachPart>());
    }

    /// <summary>The exact distance between two world boxes (0 when they meet). The margin a wind- or particle-labelled
    /// `no relation` prints: measured from the uncertified PART's box, never from the reference position (review note).</summary>
    public static double Gap(Aabb a, Aabb b)
    {
        static double G(double amin, double amax, double bmin, double bmax) => Math.Max(0, Math.Max(bmin - amax, amin - bmax));
        double x = G(a.Min.X, a.Max.X, b.Min.X, b.Max.X), y = G(a.Min.Y, a.Max.Y, b.Min.Y, b.Max.Y), z = G(a.Min.Z, a.Max.Z, b.Min.Z, b.Max.Z);
        return Math.Sqrt(x * x + y * y + z * z);
    }

    /// <summary>Whether a region's box meets a world box (closed intervals). A refused region meets everything.</summary>
    public static bool Meets(ReachRegion region, Aabb box)
    {
        if (region.Status == "refused") return true;
        if (region.Box.IsEmpty || box.IsEmpty) return false;
        double g = Gap(region.Box, box);
        return !double.IsFinite(g) || g == 0;   // a non-finite gap cannot exclude a neighbour
    }

    static bool Finite(Aabb b) => float.IsFinite(b.Min.X) && float.IsFinite(b.Min.Y) && float.IsFinite(b.Min.Z)
                                 && float.IsFinite(b.Max.X) && float.IsFinite(b.Max.Y) && float.IsFinite(b.Max.Z);

    internal static Aabb BoxWorld(Aabb model, Vec3 position, Vec3 rotation, float scale)
    {
        var corners = new Vector3[8];
        for (int k = 0; k < 8; k++)
            corners[k] = new Vector3((k & 1) == 0 ? model.Min.X : model.Max.X, (k & 2) == 0 ? model.Min.Y : model.Max.Y, (k & 4) == 0 ? model.Min.Z : model.Max.Z);
        return SpatialOverlap.SoupBounds(SpatialOverlap.TransformSoup(new TriangleSoup(corners, Array.Empty<int>()), position, rotation, scale));
    }

    static float Up(double x) { float g = (float)x; return g < x ? MathF.BitIncrement(g) : g; }
    static float Down(double x) { float g = (float)x; return g > x ? MathF.BitDecrement(g) : g; }

    static Aabb Grow(Aabb b, double d)
    {
        if (b.IsEmpty || d <= 0) return b;
        float g = (float)d;
        if (g < d) g = MathF.BitIncrement(g);   // never round a bound down
        return new Aabb(new Vec3(b.Min.X - g, b.Min.Y - g, b.Min.Z - g), new Vec3(b.Max.X + g, b.Max.Y + g, b.Max.Z + g), false);
    }
}
