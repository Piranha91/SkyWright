using System.Numerics;
using System.Runtime.CompilerServices;

using HousecarlCore;

namespace SkyWright.Engine;

// MEMBERS UNDER MOTION (one shared reader for every motion consumer). A member that moves under the engine's own animation
// or physics occupies a region, read here once under one rule set (MemberRules, derived from the reach rules) and used two
// ways: samples (posed soups, each with a certified displacement bound and an origin) and a decision loop that refines those
// samples against a predicate the consumer supplies (crosses / clear(margin) / open) until witness, never or cannot_tell.
// Mechanisms: NIF and behaviour-graph clips; transition blends between graph states on a multi-node chain (one weight for
// every node, sign-aligned quaternions); hkbBlenderGenerator with 2 or 3 children; hkbReferencePoseGenerator (stored pose,
// reference pose and every state's poses); pinned limited-hinge chains (hinge angles within the file's limits, either sense,
// from the stored angle); a book on a BOOK base at its closed, stored pose. What the files cannot bound refuses by name.
//
// The decision loop works on cells: a clip's time interval, a blend's box over (each clip's time, the blend weights), a
// hinge island's box over its angles, and single poses (the stored pose, the reference pose, a clip's end, a hinge limit
// corner). Each cell carries a certified bound on how far any point moves from its representative pose over the cell
// (SweptGeometry.DeltaBound's chain composition fed by a per-node bound; for a hinge the Lipschitz bound PhysicsReach's
// envelope uses). A cell the partner keeps open is split, largest bound first, until the budget is spent. A witness is
// taken from the most direct origin: a pose that is not a blend or a swing outranks one that is (P1, P2).
//
// Blend bound per node (MemberMotion.Blend.cs): the outputs of the children (their clips' bounds over the cell's times) are
// combined with the weights over the cell. nlerp: the normalised weighted sum moves by at most 1/m times the change of the sum,
// m the shortest the sum can be over the cell (radial projection onto the ball of radius m is 1-Lipschitz; two children with
// product at least c: m^2 = 1 - 2 w (1 - w) (1 - c) at the cell's w nearest 0.5; three: 1/3). slerp (two children): each
// child's angle times the geodesic interpolant's endpoint sensitivity, sin(x W) / sin W (x: 1 - w or w; W the widest arc
// over the cell, at most 90 degrees), plus the arc times the weight change. A cell in which a product can change sign is split
// into the engine's two sign branches; translation and scale are linear in the weights.
//
// STATUS: implemented at Gate C (lane M).

/// <summary>The one rule set every consumer reads motion under, derived from the reach rules (there are no per-consumer
/// options). <c>Blend</c>: transition blends and hkbBlenderGenerator children are sampled. <c>ReferencePose</c>: the
/// reference-pose generator is read (with the graph's binding policy and a missing sequence's stored pose, as the reach
/// rule `graph`). <c>ClipNames</c>: a clip resolves through the character's animation list. <c>Hinge</c>: a pinned
/// limited-hinge island is sampled over its hinge angles. <c>Book</c>: a BOOK base is read closed, at its stored pose.
/// <c>Skin</c>: the reach rule `skin` (two weight sources posed when they disagree on drawn vertices).</summary>
public sealed record MemberRules(bool Blend = true, bool ReferencePose = true, bool ClipNames = true, bool Hinge = true,
                                 bool Book = true, bool Skin = true)
{
    /// <summary>The shared rule set every consumer uses under <c>--motion</c>.</summary>
    public static readonly MemberRules Shared = new();

    /// <summary>The rule set the reach rules name (physics = Hinge, graph = ReferencePose, the Part 3b blend and clip-name rules,
    /// book, skin).</summary>
    public static MemberRules Of(ReachRules rules) =>
        new(rules.Blend, rules.Graph, rules.ClipNames, rules.Physics, rules.BookArt, rules.Skin);

    /// <summary>The reach rules this rule set corresponds to for a base of the given record type (BOOK takes the book rule).</summary>
    public ReachRules Reach(string? baseRecordType) =>
        new(Physics: Hinge, Graph: ReferencePose, BookArt: Book && MemberMotion.IsBook(baseRecordType), BaseRecordType: baseRecordType,
            Skin: Skin, ClipNames: ClipNames, Blend: Blend);
}

/// <summary>A mechanism that moves the member. Kind: <c>clip</c>, <c>blend</c>, <c>blender</c>, <c>hinge</c>,
/// <c>reference-pose</c>, <c>book-closed</c>. Certification: <c>certified</c> (an exact union with a stated per-cell bound,
/// relative to the model the label names) or <c>modelling rule</c> / <c>read by class name</c>.</summary>
public sealed record MemberMechanism(string Kind, string Label, string Certification);

/// <summary>Status <c>static</c>, <c>moving</c> or <c>refused</c> (Reason names the cause). TransitionSeconds: the longest
/// transition effect duration the graph's files state (null when none applies or a duration is bound to a variable;
/// TransitionNote then says which).</summary>
public sealed record MemberModel(string Status, string? Reason, IReadOnlyList<MemberMechanism> Mechanisms, IReadOnlyList<string> Labels,
                                 MotionModel Motion, MemberRules Rules, MotionPlacement? Placement,
                                 double? TransitionSeconds = null, string? TransitionNote = null);

/// <summary>A predicate's answer for one posed sample: <c>crosses</c> (Note may say how, e.g. alpha-only), <c>clear</c>
/// with a certified margin (the posed soup lies at least Margin from the partner), or <c>open</c>.</summary>
public sealed record MemberTest(string Kind, double Margin = 0, string? Note = null)
{
    public static MemberTest Crosses(string? note = null) => new("crosses", 0, note);
    public static MemberTest Clear(double margin) => new("clear", margin);
    public static readonly MemberTest Open = new("open");
}

/// <summary>The consumer's predicate: a posed, placed (world) soup with its per-triangle material tags and the certified
/// bound on how far any of its points moves within the sample's cell.</summary>
public delegate MemberTest MemberPredicate(TriangleSoup posedWorldSoup, string[]? tags, double delta);

/// <summary>DeltaTarget: the initial sampling's target bound; the decision loop refines below it wherever the partner keeps a
/// cell open, until Budget posed evaluations are spent.</summary>
public sealed record MemberSweep(double DeltaTarget = 0.5, int Budget = 4096);

/// <summary>A discovery proxy (<see cref="MemberMotion.DiscoveryProxy"/>): status <c>built</c> with the closed per-sample boxes as one
/// soup and their union, or the region's refusal.</summary>
public sealed record DiscoveryProxyBoxes(string Status, string? Reason, TriangleSoup? Soup, Aabb Box, int SampleCount, double MaxDelta,
                                         bool Converged, IReadOnlyList<MotionAnimation> Animations);

/// <summary>Decision <c>witness</c>, <c>never</c>, <c>cannot_tell</c> or <c>refused</c>. A witness names its sample's Origin
/// (<c>clip</c>, <c>rest</c>, <c>blend</c>, <c>swing</c>, <c>reference</c>), the animation(s) and time(s), the blend weights or the
/// hinge angles (radians, from the stored pose), the label (what it means for a player) and the predicate's note. Width is the
/// bound a cannot_tell leaves; Evaluations counts posed samples tested.</summary>
public sealed record MemberDecision(string Decision, string? Reason, string? Origin, string? AnimationId, float? Time,
                                    string? AnimationB, float? TimeB, IReadOnlyList<double>? Weights, IReadOnlyList<double>? Angles,
                                    string? Label, string? Note, double Width, int Evaluations, bool Converged);

public static partial class MemberMotion
{
    public const string PolicyVersion = "member_motion_v1";

    // ---- origins and mechanism kinds ----
    public const string OriginClip = "clip", OriginRest = "rest", OriginBlend = "blend", OriginSwing = "swing", OriginReference = "reference";
    public const string KindClip = "clip", KindBlend = "blend", KindBlender = "blender", KindHinge = "hinge",
                        KindReferencePose = "reference-pose", KindBookClosed = "book-closed";

    // ---- labels (decisions P1, P2 of 2026-10-07; GATE-A §2) ----
    /// <summary>P2: a swing witness against a partner whose collision the body's layer collides with: the body stops on it.</summary>
    public const string SwingRestsAgainst = "can rest against when disturbed";
    /// <summary>P2: a swing witness against a partner with render geometry only: a visible clip while it swings.</summary>
    public const string SwingVisibleClip = "visible clip while swinging when disturbed";
    /// <summary>The stored pose of a pinned hinge island (origin rest).</summary>
    public const string HingeRestLabel = "rest: stored pose; the gravity equilibrium is not established";
    /// <summary>A sample of a crossbow-like graph's reference-pose generator (origin reference).</summary>
    public const string ReferenceLabel = "reference pose read by class name; visible only if the placed item runs its graph";
    /// <summary>Kept refusal: a free chain of movable bodies on a base other than BOOK.</summary>
    public const string FreeChainRefusal = "physics: free-chain hinges (no immovable body) on a non-BOOK base: no envelope; the drop and rest rules are a different mechanism";
    /// <summary>Decision 1 of the Gate B review: a pinned body whose node an animation also drives is refused by name (whether the
    /// animation moves a simulated body is not established; the in-game reading G-1 would settle it).</summary>
    public const string AnimatedPinnedRefusal = "physics: a pinned body's node is also animated";
    /// <summary>Lane K (decision 6, after the in-game reading of 2026-10-09): a NiTransformController on movable bodies' own nodes is
    /// bounded by the hinge swing alone; the label states the reading's limit.</summary>
    public const string AnimatedPinnedLabel = "swing alone: a NiTransformController also drives a pinned body's node; in one in-game reading it moved nothing when the 3D was created in view; whether it ever runs is not established";
    /// <summary>The stored pose of a model whose graph holds a reference-pose generator (origin rest).</summary>
    public const string GraphRestLabel = "rest: stored pose (visible as placed; whether the placed item runs its graph is not established)";
    /// <summary>A swing witness before the partner's collision is read (the consumer adds the P2 meaning).</summary>
    public const string SwingLabel = "swing: within the file's hinge limits, in either sense, from the stored pose; reached when disturbed";
    /// <summary>The blend mechanism's model (decision 2 of the Gate B review: the union of nlerp and slerp).</summary>
    public const string BlendModelLabel = "blend: one weight for every node, sign-aligned quaternions, the engine's pose blend taken as nlerp or slerp (both sampled), clocks independent; three or more poses as the normalised weighted mean";

    /// <summary>P1: what a crossing seen only inside a blend means: a named minor transient, never a fix by itself.</summary>
    public static string TransientLabel(double? seconds, string? note) =>
        "transient: crossing seen only inside a blend; minor, never a fix by itself ("
        + (seconds is { } s ? $"a transition of at most {s.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture)} s" : note ?? "blend duration not stated by the files")
        + ")";

    /// <summary>P2: the meaning of a swing witness from the partner's solid status.</summary>
    public static string SwingMeaning(bool partnerCollides) => partnerCollides ? SwingRestsAgainst : SwingVisibleClip;

    internal static bool IsBook(string? baseRecordType) => baseRecordType is "BOOK" or "Book";

    static readonly ConditionalWeakTable<MemberModel, MemberReading> Readings = new();

    static MemberModel Refused(string why, MotionModel? motion, MemberRules rules, MotionPlacement? placement) =>
        new("refused", why, Array.Empty<MemberMechanism>(), Array.Empty<string>(),
            motion ?? new MotionModel("refused", why, "none", [], [], [], [], null), rules, placement);

    /// <summary>Read what moves a member, under the shared rules.</summary>
    public static MemberModel Read(byte[] nif, Func<string, byte[]?>? read, MotionPlacement? placement, MemberRules? rules = null)
    {
        rules ??= MemberRules.Shared;
        try { return ReadCore(nif, read, placement, rules); }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return Refused("member reading failed: " + ex.Message, null, rules, placement);
        }
    }

    static MemberModel ReadCore(byte[] nif, Func<string, byte[]?>? read, MotionPlacement? placement, MemberRules rules)
    {
        var phys = rules.Hinge || rules.Book ? PhysicsReach.Classify(nif) : null;
        bool constrained = phys is { Constraints.Count: > 0 };
        bool book = rules.Book && IsBook(placement?.BaseRecordType);
        GraphOptions? go = rules.ReferencePose
            ? new GraphOptions(true, true, true, ClipNames: rules.ClipNames, Blend: rules.Blend, Members: true)
            : new GraphOptions(ClipNames: rules.ClipNames, Blend: rules.Blend, Members: true);
        var mech = new List<MemberMechanism>();
        var labels = new List<string>();
        var reading = new MemberReading();

        // ---- the book rule: a BOOK base is read closed, at its stored pose (no animation, no hinge sampling) ----
        if (book)
        {
            var plain = NifMotion.Read(nif, read, placement);
            bool moves = constrained || plain.Animations.Count > 0 || plain.Status == "refused";
            var closed = NifMotion.Read(nif, read, placement, new MotionReadOptions(IgnorePhysics: true, IgnoreAnimation: true, SkinUnion: rules.Skin));
            if (closed.Status == "refused") return Refused(closed.Reason ?? "refused", closed, rules, placement);
            if (!moves) return Done(closed, "static");
            mech.Add(new MemberMechanism(KindBookClosed, MeshReach.BookClosedLabel, "modelling rule"));
            reading.Rest = true; reading.RestLabel = MeshReach.BookClosedLabel;
            labels.Add(MeshReach.BookClosedLabel);
            return Done(closed, "moving");
        }

        // ---- physics: which rule a constrained file takes ----
        bool hinge = false;
        if (constrained && rules.Hinge)
        {
            switch (phys!.Kind)
            {
                case "pinned": hinge = true; break;
                case "fixed": break;   // nothing simulated: read as if unconstrained
                case "free-chain":
                    return Refused(FreeChainRefusal, null, rules, placement);
                case "mixed":
                    return Refused("physics: pinned and free bodies in one file are not supported", null, rules, placement);
                default: break;   // free, none, refused: the constraint refusal stands (the motion reader names it)
            }
        }
        bool ignorePhysics = constrained && rules.Hinge && phys!.Kind is "pinned" or "fixed";
        var motion = NifMotion.Read(nif, read, placement, new MotionReadOptions(IgnorePhysics: ignorePhysics, Graph: go, SkinUnion: rules.Skin));
        if (motion.Status == "refused") return Refused(motion.Reason ?? "refused", motion, rules, placement);
        if (motion.Labels is { } ml) labels.AddRange(ml);
        var rig = NifMotion.RigOf(nif, motion, read);
        reading.Rig = rig;
        var g = motion.Graph;

        // ---- graph: kept refusals, transition durations, blend families ----
        double? seconds = null; string? note = null;
        if (g is not null)
        {
            bool blends = g.States.Count(s => s.GeneratorKind != "state_machine") > 1 || (g.Blenders?.Count ?? 0) > 0;
            if (blends && rules.Blend)
            {
                if (g.Classes.Contains("hkbBoneWeightArray"))
                    return Refused("behaviour graph holds an hkbBoneWeightArray: per-bone blend weights break the single blend weight", motion, rules, placement);
                if (g.Classes.FirstOrDefault(c => c.EndsWith("TransitionEffect", StringComparison.Ordinal) && c != "hkbBlendingTransitionEffect") is { } other)
                    return Refused($"behaviour graph transition effect class {other} is not read", motion, rules, placement);
                var fx = g.Transitions ?? Array.Empty<HavokTransitionEffect>();
                bool bound = g.Bindings?.Any(b => b.OwnerClass == "hkbBlendingTransitionEffect" && b.MemberPath == "duration") == true;
                if (bound) note = "a transition's duration is bound to a graph variable";
                else if (fx.Count == 0) note = "no transition effect in the files";
                else seconds = fx.Max(x => (double)(x.Duration ?? 0));
                var why = BuildFamilies(reading, rig, g, seconds, note);
                if (why is not null) return Refused(why, motion, rules, placement);
            }
        }
        foreach (var s in rig.Sources.Values)
            (s.Animation.Source == "behaviour-reference-pose" ? reading.ReferenceIds : reading.ClipIds).Add(s.Animation.Id);
        if (reading.ReferenceIds.Count > 0) { reading.Rest = true; reading.RestLabel = GraphRestLabel; }

        // ---- the hinge part ----
        if (hinge)
        {
            var (tree, why) = PhysicsReach.HingeTreeOf(nif, motion, read);
            if (tree is null) return Refused(why ?? "physics: hinge tree refused", motion, rules, placement);
            // lane K (decision 6): a NIF transform controller driving only movable bodies' own nodes is left out; the swing bounds it
            var swingOnly = rig.Sources.Values.Where(s => s.Animation.Source == "nif-controller" && s.Nodes.Count > 0
                && s.Nodes.All(n => rig.NodeByName.TryGetValue(n, out var nd) && tree.BodyOfNode.ContainsKey(nd))).ToList();
            // decision 1 of the Gate B review: any other animation driving a pinned body's node (or a node above or below it) refuses
            foreach (var src in rig.Sources.Values.Except(swingOnly))
                foreach (var dn in src.Nodes)
                {
                    if (!rig.NodeByName.TryGetValue(dn, out var dNode)) continue;
                    var dChain = rig.Chain(dNode);
                    foreach (var bNode in tree.BodyOfNode.Keys)
                        if (ReferenceEquals(bNode, dNode) || dChain.Any(c => ReferenceEquals(c, bNode)) || rig.Chain(bNode).Any(c => ReferenceEquals(c, dNode)))
                            return Refused($"{AnimatedPinnedRefusal} (body node '{bNode.Name?.String}'{(dn == bNode.Name?.String ? "" : $", node '{dn}'")} driven by {src.Animation.Id}): whether the animation moves a simulated body is not established",
                                           motion, rules, placement);
                }
            foreach (var s in swingOnly) reading.ClipIds.Remove(s.Animation.Id);
            reading.Tree = tree;
            reading.Rest = true; reading.RestLabel ??= HingeRestLabel;
            mech.Add(new MemberMechanism(KindHinge, PhysicsReach.HingeLabel + "; every angle within the limits in either sense, from the stored angle"
                                                    + (swingOnly.Count > 0 ? "; " + AnimatedPinnedLabel : ""), "certified relative to the file's limits"));
            if (!labels.Contains(PhysicsReach.HingeLabel)) labels.Add(PhysicsReach.HingeLabel);
            if (swingOnly.Count > 0) labels.Add(AnimatedPinnedLabel);
        }

        if (reading.ClipIds.Count > 0) mech.Insert(0, new MemberMechanism(KindClip, "clip samples", "certified"));
        if (reading.ReferenceIds.Count > 0)
        {
            mech.Add(new MemberMechanism(KindReferencePose, ReferenceLabel, "read by class name"));
            if (!labels.Contains(MeshReach.ReferencePoseLabel)) labels.Add(MeshReach.ReferencePoseLabel);
        }
        if (reading.Blends.Any(f => f.Kind == KindBlend))
        {
            mech.Add(new MemberMechanism(KindBlend, BlendModelLabel + "; " + TransientLabel(seconds, note), "certified relative to the stated model"));
            labels.Add(BlendModelLabel);
        }
        foreach (var bl in reading.Blends.Where(f => f.Kind == KindBlender).Select(f => f.Blender).Distinct())
            mech.Add(new MemberMechanism(KindBlender, $"hkbBlenderGenerator '{bl}': its children's poses over the weight simplex (weights not read)", "certified relative to the stated model"));
        foreach (var fixedLabel in reading.FixedBlenders)
            mech.Add(new MemberMechanism(KindBlender, fixedLabel, "read from the file"));
        bool moving = mech.Count > 0 && (motion.Status == "moving" || reading.Tree is not null || reading.ReferenceIds.Count > 0);
        return Done(motion, moving ? "moving" : motion.Status, seconds, note);

        MemberModel Done(MotionModel m, string status, double? sec = null, string? nt = null)
        {
            reading.Rig ??= NifMotion.RigOf(nif, m, read);
            var mm = new MemberModel(status, null, mech, labels, m, rules, placement, sec, nt);
            Readings.AddOrUpdate(mm, reading);
            return mm;
        }
    }

    internal static MemberReading ReadingOf(byte[] nif, MemberModel model, Func<string, byte[]?>? read)
    {
        if (Readings.TryGetValue(model, out var r)) return r;
        var again = Read(nif, read, model.Placement, model.Rules);
        return Readings.TryGetValue(again, out r) ? r : throw new InvalidOperationException("member reading unavailable: " + again.Reason);
    }

    /// <summary>True when the reading has nothing but clip animations: the region is exactly SweptGeometry.Build's.</summary>
    internal static bool ClipOnly(MemberReading r) => !r.Rest && r.ReferenceIds.Count == 0 && r.Blends.Count == 0 && r.Tree is null;

    /// <summary>True when the member's region is exactly today's clip-only swept region (a consumer keeps its existing output).</summary>
    public static bool IsClipOnly(byte[] nif, MemberModel model, Func<string, byte[]?>? read = null) =>
        model.Status != "refused" && ClipOnly(ReadingOf(nif, model, read));

    /// <summary>The member's samples in model space (origin set on every sample that is not a clip sample).</summary>
    public static SweptRegion Region(byte[] nif, MemberModel model, VolumeGeometry geometry, MemberSweep? sweep = null,
                                     Func<string, byte[]?>? read = null, CancellationToken cancellationToken = default)
    {
        sweep ??= new MemberSweep();
        SweptRegion Refuse(string? why) => new("refused", why, geometry, model.Motion.Animations, Array.Empty<SweptSample>(), 0, false, false);
        if (model.Status == "refused") return Refuse(model.Reason);
        var samples = new List<SweptSample>();
        var topology = new SweptGeometry.SampleTopology();
        var why = SampleRegion(nif, model, geometry, sweep, read, cancellationToken, (id, t, soup, tags, d, origin) =>
        {
            var shared = topology.Share(soup, tags);
            samples.Add(new SweptSample(id, t, shared.Soup, shared.Tags, d, origin));
        }, out var anims, out bool converged);
        if (why is not null) return Refuse(why);
        bool solid = MeshIntersection.PrepareVolumeSolid(samples[0].Soup, 50_000_000, cancellationToken).Solid is not null;
        if (geometry == VolumeGeometry.Render && solid && samples[0].Tags is { } tags && tags.Any(x => x != SurfaceMaterial.Opaque)) solid = false;
        return new SweptRegion("built", null, geometry, anims, samples, samples.Max(s => s.Delta), converged, solid);
    }

    /// <summary>The sampling loop of <see cref="Region"/> and <see cref="DiscoveryProxy"/>: a clip-only member through SweptGeometry's
    /// loop (Build's samples), any other member's cells refined largest bound first and posed in precedence order. Each posed sample
    /// goes to <paramref name="sink"/> (id, time, soup, tags, delta, origin) and is not kept here. Returns the refusal, or null.</summary>
    static string? SampleRegion(byte[] nif, MemberModel model, VolumeGeometry geometry, MemberSweep sweep, Func<string, byte[]?>? read,
                                CancellationToken cancellationToken, Action<string, float, TriangleSoup, string[]?, double, string?> sink,
                                out IReadOnlyList<MotionAnimation> anims, out bool converged)
    {
        anims = model.Motion.Animations;
        converged = false;
        int count = 0;
        var r = ReadingOf(nif, model, read);
        if (ClipOnly(r))
        {
            var clipWhy = SweptGeometry.Sample(nif, model.Motion, null, geometry, new MotionOptions(sweep.DeltaTarget, sweep.Budget), read,
                                               cancellationToken, (id, t, soup, tg, d) => { count++; sink(id, t, soup, tg, d, null); },
                                               out var clipAnims, out converged);
            if (clipWhy is not null) return clipWhy;
            anims = clipAnims;
            return count == 0 ? "no animation to sweep" : null;
        }
        var ctx = MemberCtx.Of(model, r, geometry);
        if (ctx.Refusal is not null) return ctx.Refusal;
        var cells = InitialCells(ctx);
        // refine the largest bound first until every bound meets the target or the sample budget is spent
        var pq = new PriorityQueue<MemberCell, double>();
        var done = new List<MemberCell>();
        foreach (var c in cells) { if (c.Splittable && c.Delta > sweep.DeltaTarget) pq.Enqueue(c, -c.Delta); else done.Add(c); }
        converged = true;
        while (pq.Count > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (done.Count + pq.Count + 1 > sweep.Budget) { converged = false; break; }
            var c = pq.Dequeue();
            foreach (var k in c.Split(ctx))
            {
                if (k.Splittable && k.Delta > sweep.DeltaTarget) pq.Enqueue(k, -k.Delta); else done.Add(k);
            }
        }
        while (pq.Count > 0) done.Add(pq.Dequeue());
        if (done.Any(c => c.Delta > sweep.DeltaTarget)) converged = false;
        foreach (var c in done.OrderBy(c => c.Precedence))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var p = c.Pose(ctx);
            if (p.Status != "posed") return $"{c.Describe()}: {p.Reason}";
            count++;
            sink(c.SampleId, c.SampleTime, p.Soup!, p.Tags, c.Delta, c.Origin == OriginClip ? null : c.Origin);
        }
        return count == 0 ? "no pose to sample" : null;
    }

    /// <summary>The samples a discovery consumer (overlapprobe's swept proxy) may use: a clip-only member's region as today; any other
    /// member's only when it converged in its budget, otherwise refused, naming the width left (a box wider than the budget could
    /// narrow carries no information).</summary>
    public static SweptRegion DiscoveryRegion(byte[] nif, MemberModel model, VolumeGeometry geometry, MemberSweep? sweep = null,
                                              Func<string, byte[]?>? read = null, CancellationToken cancellationToken = default)
    {
        var r = Region(nif, model, geometry, sweep, read, cancellationToken);
        // a clip-only member keeps today's proxy, converged or not (the base manifests carry such proxies; unchanged by construction)
        if (r.Status != "built" || r.Converged || ClipOnly(ReadingOf(nif, model, read))) return r;
        return r with { Status = "refused", Reason = NotConverged(r.MaxDelta), Samples = Array.Empty<SweptSample>() };
    }

    static string NotConverged(double width) =>
        $"motion region did not converge in budget (width {width.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture)} u)";

    /// <summary>overlapprobe's <c>--motion swept</c> proxy: <see cref="DiscoveryRegion"/>'s samples, each reduced as it is posed to one
    /// closed box (its vertex bounds grown by its delta), in sample order, and their union. No posed sample is kept, so memory does
    /// not grow with the sample count. Refused with DiscoveryRegion's reason.</summary>
    public static DiscoveryProxyBoxes DiscoveryProxy(byte[] nif, MemberModel model, VolumeGeometry geometry, MemberSweep? sweep = null,
                                                     Func<string, byte[]?>? read = null, CancellationToken cancellationToken = default)
    {
        sweep ??= new MemberSweep();
        DiscoveryProxyBoxes Refuse(string? why) => new("refused", why, null, Aabb.Empty, 0, 0, false, model.Motion.Animations);
        if (model.Status == "refused") return Refuse(model.Reason);
        var v = new List<Vector3>(); var idx = new List<int>(); var deltas = new List<double>();
        var total = Aabb.Empty;
        var why = SampleRegion(nif, model, geometry, sweep, read, cancellationToken, (_, _, soup, _, d, _) =>
        {
            var lo = soup.Vertices.Aggregate(new Vector3(float.MaxValue), Vector3.Min) - new Vector3((float)d);
            var hi = soup.Vertices.Aggregate(new Vector3(float.MinValue), Vector3.Max) + new Vector3((float)d);
            int b0 = v.Count;
            for (int k = 0; k < 8; k++) v.Add(new((k & 1) == 0 ? lo.X : hi.X, (k & 2) == 0 ? lo.Y : hi.Y, (k & 4) == 0 ? lo.Z : hi.Z));
            foreach (var t in BoxTriangles) idx.Add(b0 + t);
            total = total.Add(lo).Add(hi);
            deltas.Add(d);
        }, out var anims, out bool converged);
        if (why is not null) return Refuse(why);
        double maxDelta = deltas.Max();
        // DiscoveryRegion's rule: a clip-only member keeps its proxy, converged or not; any other member only when converged
        if (!converged && !ClipOnly(ReadingOf(nif, model, read)))
            return new("refused", NotConverged(maxDelta), null, Aabb.Empty, 0, maxDelta, converged, anims);
        return new("built", null, new TriangleSoup(v.ToArray(), idx.ToArray()), total, deltas.Count, maxDelta, converged, anims);
    }

    static readonly int[] BoxTriangles = { 0,2,1, 1,2,3, 4,5,6, 5,7,6, 0,1,4, 1,5,4, 2,6,3, 3,6,7, 0,4,2, 2,4,6, 1,3,5, 3,7,5 };

    /// <summary>Witness / never / cannot_tell of the member, placed, against the consumer's predicate.</summary>
    public static MemberDecision Decide(byte[] nif, MemberModel model, VolumeGeometry geometry, Vec3 position, Vec3 rotation, float scale,
                                        MemberSweep? sweep, MemberPredicate predicate, Func<string, byte[]?>? read = null,
                                        CancellationToken cancellationToken = default)
    {
        sweep ??= new MemberSweep();
        static MemberDecision Refuse(string? why, int evals = 0) => new("refused", why, null, null, null, null, null, null, null, null, null, 0, evals, false);
        if (model.Status == "refused") return Refuse(model.Reason);
        var r = ReadingOf(nif, model, read);
        var ctx = MemberCtx.Of(model, r, geometry);
        if (ctx.Refusal is not null) return Refuse(ctx.Refusal);
        double s = Math.Abs(scale);
        int evals = 0, budget = Math.Max(1, sweep.Budget);
        MemberCell? best = null; MemberTest? bestT = null;
        var open = new PriorityQueue<MemberCell, double>();
        var unresolved = new List<MemberCell>();
        bool budgetOut = false;
        string? poseRefusal = null;

        void Test(MemberCell c)
        {
            if (poseRefusal is not null || (best is not null && c.Precedence >= best.Precedence)) return;
            if (evals >= budget) { budgetOut = true; unresolved.Add(c); return; }
            cancellationToken.ThrowIfCancellationRequested();
            var p = c.Pose(ctx);
            if (p.Status != "posed") { poseRefusal = $"{c.Describe()}: {p.Reason}"; return; }
            evals++;
            double d = c.Delta * s;
            var t = predicate(SpatialOverlap.TransformSoup(p.Soup!, position, rotation, scale), p.Tags, d);
            if (t.Kind == "crosses" && c.RepRealized) { best = c; bestT = t; return; }
            if (t.Kind == "clear" && t.Margin > d) return;
            if (c.Splittable) open.Enqueue(c, -c.Delta); else unresolved.Add(c);
        }

        foreach (var c in InitialCells(ctx).OrderBy(c => c.Precedence)) Test(c);
        while (open.Count > 0 && poseRefusal is null)
        {
            var c = open.Dequeue();
            if (best is not null && c.Precedence >= best.Precedence) continue;
            if (evals >= budget) { budgetOut = true; unresolved.Add(c); continue; }
            foreach (var k in c.Split(ctx)) Test(k);
        }
        if (poseRefusal is not null) return Refuse(poseRefusal, evals);
        var left = unresolved.Where(c => best is null || c.Precedence < best.Precedence).ToList();
        if (best is not null)
        {
            string? n = bestT!.Note;
            if (left.Count > 0)
                n = (n is null ? "" : n + "; ") + $"not resolved in budget: a {string.Join("/", left.Select(c => c.Origin).Distinct())} pose may also cross";
            return best.Witness(ctx, n, evals);
        }
        if (left.Count == 0) return new("never", null, null, null, null, null, null, null, null, null, null, 0, evals, true);
        double width = left.Max(c => c.Delta) * s;
        string reason = budgetOut
            ? $"budget exhausted: {budget} posed evaluations spent with {left.Count} cells open (largest bound {width.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture)} u)"
            : "a pose lies within the rounding bound of the partner and no pose crosses it";
        return new("cannot_tell", reason, null, null, null, null, null, null, null, null, null, width, evals, false);
    }

    /// <summary>The reach box from the same reading (the reach reader's analytic boxes: the blend envelope, the hinge envelope),
    /// unchanged; every sampled pose lies inside it.</summary>
    public static (Aabb Box, string? Label, string? Refusal) Box(byte[] nif, MemberModel model, Vec3 position, Vec3 rotation, float scale,
                                                                 MemberSweep? sweep = null, Func<string, byte[]?>? read = null)
    {
        sweep ??= new MemberSweep();
        var r = MeshReach.OfModel(nif, position, rotation, scale, read,
                                  new ReachOptions(sweep.DeltaTarget, sweep.Budget, model.Rules.Reach(model.Placement?.BaseRecordType)));
        if (r.Status is "refused" or "no_geometry") return (Aabb.Empty, null, r.Reason ?? r.Status);
        return (r.Box, r.Labels.Count == 0 ? null : string.Join("; ", r.Labels), null);
    }

    // ---------------- cells ----------------
    static List<MemberCell> InitialCells(MemberCtx ctx)
    {
        var r = ctx.Reading;
        var cells = new List<MemberCell>();
        if (r.Rest) cells.Add(new RestCell(r.RestLabel ?? HingeRestLabel));
        foreach (var id in r.ReferenceIds) cells.Add(new ClipCell(ctx, id, 0, 0, OriginReference));
        foreach (var id in r.ClipIds)
        {
            var a = r.Rig.Sources[id].Animation;
            if (a.End > a.Start) cells.Add(new ClipCell(ctx, id, a.Start, a.End, OriginClip));
            cells.Add(new ClipCell(ctx, id, a.End, a.End, OriginClip));
        }
        foreach (var f in r.Blends) cells.AddRange(MixCell.Initial(ctx, f));
        if (r.Tree is not null && ctx.Hinge is { } hl) cells.AddRange(HingeCell.Initial(ctx, hl));
        return cells;
    }
}

/// <summary>What the member reader keeps beside a model: the rig, the clip and reference-pose sources, the blend families, the
/// hinge tree, and whether the stored pose is a rest sample.</summary>
internal sealed class MemberReading
{
    public MotionRig Rig = null!;
    public readonly List<string> ClipIds = new();
    public readonly List<string> ReferenceIds = new();
    public bool Rest;
    public string? RestLabel;
    public readonly List<MixFamily> Blends = new();
    /// <summary>Lane N: blenders read with fixed one-hot weights, each as the one child it outputs (its mechanism label).</summary>
    public readonly List<string> FixedBlenders = new();
    public PhysicsReach.HingeTree? Tree;
}

/// <summary>One decision's or region's working state: the reading, the geometry, the chain pieces DeltaBound walks and the hinge
/// layout.</summary>
internal sealed class MemberCtx
{
    public required MemberModel Model { get; init; }
    public required MemberReading Reading { get; init; }
    public required VolumeGeometry Geometry { get; init; }
    public required List<SweptGeometry.ChainPiece> Pieces { get; init; }
    public bool Collision => Geometry == VolumeGeometry.Collision;
    public HingeLayout? Hinge { get; init; }
    public string? Refusal { get; init; }

    public static MemberCtx Of(MemberModel model, MemberReading r, VolumeGeometry g)
    {
        var pieces = SweptGeometry.Pieces(r.Rig, g);
        HingeLayout? hl = null; string? why = null;
        if (r.Tree is not null) (hl, why) = HingeLayout.Of(r.Tree, r.Rig, g);
        return new MemberCtx { Model = model, Reading = r, Geometry = g, Pieces = pieces, Hinge = hl, Refusal = why };
    }
}

/// <summary>A cell of the decision loop: a representative pose and a certified bound (model space) on how far any point moves from
/// it over the cell. Precedence orders origins (rest 0; clip and reference 1; blend 2; swing 3): a witness of a lower number
/// outranks one of a higher.</summary>
internal abstract class MemberCell
{
    public double Delta;
    public abstract string Origin { get; }
    public int Precedence => Origin switch { MemberMotion.OriginRest => 0, MemberMotion.OriginClip or MemberMotion.OriginReference => 1, MemberMotion.OriginBlend => 2, _ => 3 };
    public abstract bool Splittable { get; }
    /// <summary>False when the representative pose is not one the engine takes (a sign branch whose choice differs there): it bounds
    /// the cell but is never a witness.</summary>
    public virtual bool RepRealized => true;
    public abstract MotionPoseSoup Pose(MemberCtx ctx);
    public abstract IEnumerable<MemberCell> Split(MemberCtx ctx);
    public abstract MemberDecision Witness(MemberCtx ctx, string? note, int evals);
    public abstract string Describe();
    public virtual string SampleId => Describe();
    public virtual float SampleTime => 0;
}

/// <summary>The stored pose (origin rest): a hinge island's, a book's (closed), or a reference-pose graph's.</summary>
internal sealed class RestCell(string label) : MemberCell
{
    public override string Origin => MemberMotion.OriginRest;
    public override bool Splittable => false;
    public override MotionPoseSoup Pose(MemberCtx ctx) => NifMotion.PoseWith(ctx.Reading.Rig, null, "stored", 0, ctx.Geometry);
    public override IEnumerable<MemberCell> Split(MemberCtx ctx) => Array.Empty<MemberCell>();
    public override MemberDecision Witness(MemberCtx ctx, string? note, int evals) =>
        new("witness", null, MemberMotion.OriginRest, "stored", 0, null, null, null, null, label, note, 0, evals, true);
    public override string Describe() => "stored";
}

/// <summary>A clip's time interval [A, B] (A = B: one pose), posed at A; the bound is SweptGeometry's per-interval bound.</summary>
internal sealed class ClipCell : MemberCell
{
    readonly string id; readonly float a, b; readonly string origin;
    public ClipCell(MemberCtx ctx, string id, float a, float b, string origin)
    {
        this.id = id; this.a = a; this.b = b; this.origin = origin;
        Delta = b > a ? SweptGeometry.DeltaBound(ctx.Reading.Rig, ctx.Reading.Rig.Sources[id], ctx.Pieces, a, b, ctx.Collision) : 0;
    }
    public override string Origin => origin;
    public override bool Splittable => b - a > 1e-6f && Delta > 0;
    public override MotionPoseSoup Pose(MemberCtx ctx) => NifMotion.PoseWith(ctx.Reading.Rig, ctx.Reading.Rig.Sources[id], id, a, ctx.Geometry);
    public override IEnumerable<MemberCell> Split(MemberCtx ctx)
    {
        float m = (a + b) / 2;
        return new[] { new ClipCell(ctx, id, a, m, origin), new ClipCell(ctx, id, m, b, origin) };
    }
    public override MemberDecision Witness(MemberCtx ctx, string? note, int evals)
    {
        var an = ctx.Reading.Rig.Sources[id].Animation;
        string label = origin == MemberMotion.OriginReference ? MemberMotion.ReferenceLabel : $"{an.Id} {an.Cycle} {an.Trigger}";
        return new("witness", null, origin, id, a, null, null, null, null, label, note, 0, evals, true);
    }
    public override string Describe() => $"{id}@{a.ToString(System.Globalization.CultureInfo.InvariantCulture)}";
    public override string SampleId => id;
    public override float SampleTime => a;
}
