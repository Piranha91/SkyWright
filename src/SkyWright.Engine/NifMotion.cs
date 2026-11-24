using System.Numerics;
using System.Runtime.CompilerServices;
using NiflySharp;
using NiflySharp.Blocks;
using NiflySharp.Enums;
using NiflySharp.Structs;

using HousecarlCore;

namespace SkyWright.Engine;

// WHAT MOVES A MESH, AND WHERE IT GOES (animated-object swept volume). Reads a NIF's motion sources — tier 1: its own
// NiControllerSequences and active direct transform controllers; tier 2: the behaviour graph it names (HavokBehaviour) —
// and poses its visible render shapes or its solid collision at a named animation and time: rigid shapes through the
// node chain (root left out, rows act on a row vector, as NifGeometry), skinned shapes by linear-blend skinning
// (decision record D4). What cannot be derived from the files refuses with its reason (D2, D9); nothing is estimated
// from bounds or a stored pose.
//
// STATUS: API frozen at the Gate B red gate; implemented.

/// <summary>One animation the files can play. Id: <c>seq:&lt;name&gt;</c> (NIF sequence), <c>ctl:&lt;node&gt;</c> (direct
/// controller), <c>clip:&lt;generator&gt;</c> (behaviour clip), <c>gseq:&lt;generator&gt;</c> (behaviour-selected NIF
/// sequence). Source: <c>nif-sequence</c>, <c>nif-controller</c>, <c>behaviour-clip</c>, <c>behaviour-sequence</c>. Start
/// and End are the span the animation covers in its own time; Trigger states what the files settle about playing it.</summary>
public sealed record MotionAnimation(string Id, string Source, string Name, string Cycle, float Start, float End,
                                     string? ClipPath, string Trigger);

/// <summary>A named pose: the animation and time it is taken at, whether it is the rest pose, and its label.</summary>
public sealed record MotionPose(string Name, string AnimationId, float Time, bool Rest, string Label);

/// <summary>Per visible render shape (NifGeometry/GetShapes order): <c>static</c>, <c>rigid</c> (a driven node on its
/// chain), <c>skinned</c>; Refusal names why the shape cannot be posed.</summary>
public sealed record MotionShape(int ShapeIndex, string Name, string Mover, IReadOnlyList<string> DrivenBy, string? Refusal);

/// <summary>Skinning checks per skinned shape: the spread of the per-bone (bone ∘ skin) composites at the stored node
/// transforms (one rigid bind when small), the bind-vs-stored distance, the two weight sources' disagreement, and which
/// source is authoritative (<c>partition</c>, <c>skin-data</c>, or <c>refused</c>).</summary>
public sealed record MotionSkin(int ShapeIndex, double CompositeSpread, double BindVsStored, double? WeightSourceDifference,
                                string Authority, string? Reason);

/// <summary>Context from the placing record that the NIF cannot give: the base record type (DOOR, CONT, ACTI, MSTT…)
/// and the reference's open-by-default flag.</summary>
public sealed record MotionPlacement(string BaseRecordType, bool IsOpenByDefault);

/// <summary>Status <c>static</c> (nothing visible moves), <c>moving</c>, or <c>refused</c> (Reason names the cause).</summary>
public sealed record MotionModel(string Status, string? Reason, string Tier,
                                 IReadOnlyList<MotionAnimation> Animations, IReadOnlyList<MotionPose> Poses,
                                 IReadOnlyList<MotionShape> Shapes, IReadOnlyList<MotionSkin> Skins, HavokGraph? Graph,
                                 [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)] IReadOnlyList<string>? Labels = null);

/// <summary>How the reach rules read a mesh's motion (null = the default reading, unchanged). <c>IgnorePhysics</c>: a
/// constraint in the file does not refuse (the physics rule bounds the bodies itself). <c>IgnoreAnimation</c>: no NIF
/// sequence, controller or behaviour graph is read (a placed book is read closed, at its stored pose). <c>SwitchUnion</c>:
/// a switch node's children are all read (their union holds whichever is active). <c>WindAsStatic</c>: a Tree_Anim shape is
/// read as file geometry (the caller labels it). <c>Graph</c>: the behaviour-graph options. <c>SkinUnion</c>: a skin whose two
/// weight sources (NiSkinData, the skin partition) disagree only on vertices no triangle draws is read; one whose sources
/// disagree on drawn vertices is posed under both sources (the caller labels it); a drawn vertex with no weight under one
/// source still refuses.</summary>
public sealed record MotionReadOptions(bool IgnorePhysics = false, bool IgnoreAnimation = false, bool SwitchUnion = false,
                                       bool WindAsStatic = false, GraphOptions? Graph = null, bool SkinUnion = false, ClipChoice? OnlyClip = null,
                                       bool PathHull = false);

/// <summary>A confirmed clip (Data-relative path) to read alone from the model's behaviour graph, swept over its whole span or the
/// given window [t0, t1]: the graph's other clips and its transitions are not read (they were not what the in-game look confirmed).</summary>
public sealed record ClipChoice(string ClipPath, float[]? Window = null);

/// <summary>A posed model-space soup (visible render, per-triangle material tags; or solid collision), or a refusal.</summary>
public sealed record MotionPoseSoup(string Status, string? Reason, TriangleSoup? Soup, string[]? Tags, string AnimationId, float Time);

/// <summary>A node's local transform: rows act on a row vector (NifGeometry's sense).</summary>
internal readonly record struct NodeXf(Matrix4x4 R, float S, Vector3 T)
{
    public static readonly NodeXf Identity = new(Matrix4x4.Identity, 1, Vector3.Zero);
    public Vector3 Apply(Vector3 p) => Vector3.Transform(p * S, R) + T;
    /// <summary>this ∘ child: apply child first, then this.</summary>
    public NodeXf Compose(NodeXf child) => new(child.R * R, S * child.S, Vector3.Transform(child.T * S, R) + T);
}

/// <summary>One animation's evaluator: the local transform of each node it drives at a time, and a certified bound on each
/// driven node's motion over an interval.</summary>
/// <summary>A source read only inside [t0, t1] (a confirmed clip's window): the same transforms, key times clipped to the window.</summary>
internal sealed class WindowSource(MotionSource inner, float t0, float t1) : MotionSource
{
    public override IReadOnlyCollection<string> Nodes => inner.Nodes;
    public override NodeXf Local(string node, NodeXf stored, float t) => inner.Local(node, stored, t);
    public override (float T, float A, float S) Bound(string node, NodeXf stored, float a, float b) => inner.Bound(node, stored, a, b);
    public override IEnumerable<float> KeyTimes() => inner.KeyTimes().Where(t => t >= t0 && t <= t1);
}

internal abstract class MotionSource
{
    public required MotionAnimation Animation { get; init; }
    public abstract IReadOnlyCollection<string> Nodes { get; }
    public abstract NodeXf Local(string node, NodeXf stored, float t);
    /// <summary>(translation change, rotation angle in radians, scale change) bound over [t0, t1] relative to t0.</summary>
    public abstract (float T, float A, float S) Bound(string node, NodeXf stored, float t0, float t1);
    /// <summary>Times a sampler must include (keys / knots) inside the animation's span.</summary>
    public abstract IEnumerable<float> KeyTimes();
}

/// <summary>NIF keyframe data driving nodes (a sequence's controlled blocks or one direct controller).</summary>
internal sealed class NifKeySource : MotionSource
{
    public readonly Dictionary<string, NiKeyframeData> Data = new(StringComparer.Ordinal);
    public override IReadOnlyCollection<string> Nodes => Data.Keys;

    static bool Has<T>(KeyGroup<T> g) => g.Keys is { Count: > 0 };

    public static NodeXf Eval(NiKeyframeData kd, NodeXf stored, float t)
    {
        var r = stored.R;
        if (kd.RotationType == KeyType.XYZ_ROTATION_KEY && kd.XYZRotations is { } xyz)
        {
            float Ax(int i) => xyz.Length > i && xyz[i] is KeyGroup<float> g && Has(g) ? NifMotionKeys.EvalFloat(g, t) : 0f;
            r = NifMotionKeys.XyzRows(Ax(0), Ax(1), Ax(2));
        }
        else if (kd.QuaternionKeys is { Count: > 0 } qk) r = NifMotionKeys.QuaternionRows(NifMotionKeys.EvalQuaternion(qk, kd.RotationType, t));
        var tr = Has(kd.Translations) ? NifMotionKeys.EvalVector(kd.Translations, t) : stored.T;
        var s = Has(kd.Scales) ? NifMotionKeys.EvalFloat(kd.Scales, t) : stored.S;
        return new NodeXf(r, s, tr);
    }

    public override NodeXf Local(string node, NodeXf stored, float t) => Data.TryGetValue(node, out var kd) ? Eval(kd, stored, t) : stored;

    public override (float T, float A, float S) Bound(string node, NodeXf stored, float t0, float t1)
    {
        if (!Data.TryGetValue(node, out var kd)) return (0, 0, 0);
        float dA = 0, dT = 0, dS = 0;
        if (kd.RotationType == KeyType.XYZ_ROTATION_KEY && kd.XYZRotations is { } xyz)
        {
            for (int i = 0; i < Math.Min(3, xyz.Length); i++)
                if (xyz[i] is KeyGroup<float> g && Has(g))
                {
                    var rg = NifMotionKeys.FloatRange(g, t0, t1);
                    float v0 = NifMotionKeys.EvalFloat(g, t0);
                    dA += MathF.Max(MathF.Abs(rg.Max - v0), MathF.Abs(v0 - rg.Min));
                }
        }
        else if (kd.QuaternionKeys is { Count: > 0 } qk)
        {
            var q0 = NifMotionKeys.EvalQuaternion(qk, kd.RotationType, t0);
            foreach (float t in new[] { t1 }.Concat(qk.Select(k => k.Time).Where(x => x > t0 && x < t1)))
                dA = MathF.Max(dA, 2 * MathF.Acos(MathF.Min(1, MathF.Abs(Quaternion.Dot(q0, NifMotionKeys.EvalQuaternion(qk, kd.RotationType, t))))));
        }
        if (Has(kd.Translations))
        {
            var (lo, hi) = NifMotionKeys.VectorRange(kd.Translations, t0, t1);
            var p0 = NifMotionKeys.EvalVector(kd.Translations, t0);
            dT = Vector3.Max(Vector3.Abs(hi - p0), Vector3.Abs(p0 - lo)).Length();
        }
        if (Has(kd.Scales))
        {
            var rg = NifMotionKeys.FloatRange(kd.Scales, t0, t1);
            float s0 = NifMotionKeys.EvalFloat(kd.Scales, t0);
            dS = MathF.Max(MathF.Abs(rg.Max - s0), MathF.Abs(s0 - rg.Min));
        }
        return (dT, dA + 1e-6f, dS);
    }

    public override IEnumerable<float> KeyTimes()
    {
        foreach (var kd in Data.Values)
        {
            if (kd.XYZRotations is { } xyz) foreach (var o in xyz) if (o is KeyGroup<float> g && g.Keys is { } k) foreach (var x in k) yield return x.Time;
            if (kd.QuaternionKeys is { } q) foreach (var x in q) yield return x.Time;
            if (kd.Translations.Keys is { } tk) foreach (var x in tk) yield return x.Time;
            if (kd.Scales.Keys is { } sk) foreach (var x in sk) yield return x.Time;
        }
    }
}

/// <summary>A decoded Havok clip driving NIF nodes by the skeleton's bone names.</summary>
internal sealed class ClipSource : MotionSource
{
    public required HavokClip Clip { get; init; }
    public readonly Dictionary<string, int> Track = new(StringComparer.Ordinal);   // NIF node name -> track
    public override IReadOnlyCollection<string> Nodes => Track.Keys;

    public override NodeXf Local(string node, NodeXf stored, float t)
    {
        if (!Track.TryGetValue(node, out int k)) return stored;
        var s = Clip.Sample(k, t);
        return new NodeXf(NifMotionKeys.QuaternionRows(s.Rotation), s.Scale.X, s.Translation);
    }

    public override (float T, float A, float S) Bound(string node, NodeXf stored, float t0, float t1)
    {
        if (!Track.TryGetValue(node, out int k)) return (0, 0, 0);
        var b = Clip.Bound(k, t0, t1);
        return (b.Translation, b.Angle, b.Scale);
    }

    public override IEnumerable<float> KeyTimes() => Clip.KnotTimes();
}

/// <summary>A reference-pose generator: the skeleton's reference pose on every bone the NIF carries (one pose, no motion).</summary>
internal sealed class RefPoseSource : MotionSource
{
    public readonly Dictionary<string, HavokTrackSample> Pose = new(StringComparer.Ordinal);
    public override IReadOnlyCollection<string> Nodes => Pose.Keys;
    public override NodeXf Local(string node, NodeXf stored, float t) =>
        Pose.TryGetValue(node, out var s) ? new NodeXf(NifMotionKeys.QuaternionRows(s.Rotation), s.Scale.X, s.Translation) : stored;
    public override (float T, float A, float S) Bound(string node, NodeXf stored, float t0, float t1) => (0, 0, 0);
    public override IEnumerable<float> KeyTimes() => new[] { 0f };
}

/// <summary>Everything parsed once per mesh: shapes, vertices, triangles, materials, skins, and the animation sources.</summary>
internal sealed class MotionRig
{
    public required NifFile Nif { get; init; }
    public required byte[] Bytes { get; init; }
    public required List<RigShape> Shapes { get; init; }
    public required Dictionary<string, NiAVObject> NodeByName { get; init; }
    public required Dictionary<string, MotionSource> Sources { get; init; }
    public required List<MotionPose> Poses { get; init; }
    public HavokCollisionResult? Collision { get; set; }
    /// <summary>Part 3b blend rule: the graph blends generator outputs (transitions between states, or an hkbBlenderGenerator);
    /// the caller bounds the blends with <see cref="NifMotion.BlendBox"/>.</summary>
    public bool BlendActive { get; set; }
    /// <summary>Part 3b path rule: nodes a NiPathInterpolator drives (left at their stored pose by the sources; bounded by
    /// <see cref="NifMotion.PathBox"/>).</summary>
    public List<(NiAVObject Node, NiPathInterpolator Path)> PathNodes { get; } = new();

    public NodeXf Stored(NiAVObject n, bool collision) =>
        new(Rows(n.Rotation), collision && n.Scale == 0 ? 1 : n.Scale, n.Translation);

    public static Matrix4x4 Rows(Matrix33 m) => new(m.M11, m.M12, m.M13, 0, m.M21, m.M22, m.M23, 0, m.M31, m.M32, m.M33, 0, 0, 0, 0, 1);

    public List<NiAVObject> Chain(NiAVObject n)
    {
        var c = new List<NiAVObject>();
        var seen = new HashSet<NiAVObject>(ReferenceEqualityComparer.Instance);
        for (var cur = n; cur is not null; cur = Nif.GetParentNode(cur))
        {
            if (c.Count >= 256 || !seen.Add(cur)) throw new InvalidOperationException("a node chain is cyclic or exceeds 256 nodes");
            c.Add(cur);
        }
        return c;
    }

    /// <summary>Model transform of a node (root's own transform left out) with the source's locals substituted.</summary>
    public NodeXf Model(NiAVObject n, MotionSource? src, float t, bool collision)
    {
        var chain = Chain(n);
        var xf = NodeXf.Identity;
        for (int i = chain.Count - 2; i >= 0; i--)
        {
            var node = chain[i];
            var stored = Stored(node, collision);
            var local = src is not null && node.Name?.String is { } nm && src.Nodes.Contains(nm) ? src.Local(nm, stored, t) : stored;
            xf = xf.Compose(local);
        }
        return xf;
    }
}

internal sealed class RigShape
{
    public int Index;
    public string Name = "";
    public INiShape Shape = null!;
    public bool Hidden, Particle;
    /// <summary>Drawn in game (ShapeDrawing, the one predicate); an undrawn shape's posed triangles are tagged not_drawn.</summary>
    public bool Drawn = true;
    public string MaterialClass = SurfaceMaterial.Opaque;
    /// <summary>The tag a posed triangle of this shape carries.</summary>
    public string Tag => Drawn ? MaterialClass : SurfaceMaterial.NotDrawn;
    public List<Vector3> Vertices = new();
    public List<Triangle> Triangles = new();
    public List<NiAVObject> Chain = new();
    public string? Refusal;
    // skinning
    public List<NiAVObject>? Bones;
    public List<NodeXf>? Skin;
    public List<(int Bone, float W)>[]? Weights;
    // skin rule (MotionReadOptions.SkinUnion): the second weight source, posed beside the first (null = one source), and
    // whether only the vertices a triangle references are posed
    public List<(int Bone, float W)>[]? WeightsAlt;
    public bool DrawnOnly;
}

public static partial class NifMotion
{
    public const string PolicyVersion = "nif_motion_v1";
    const string TriggerNotSettled = "trigger: not settled by the files";

    /// <summary>The extra-data name hdtSMP64 matches (hdtDefaultBBP.cpp, scanBBP) to attach SMP physics to a mesh.</summary>
    public const string SmpMarker = "HDT Skinned Mesh Physics Object";

    /// <summary>Skyrim shader flags 2 Tree_Anim (SLSF2): the vertex shader sways the shape with the wind. One test for
    /// the motion reader and the static wind label (<see cref="NifWind.IsTreeAnim"/>).</summary>
    static bool ShaderTreeAnim(INiShader shader) => NifWind.IsTreeAnim(shader);

    static readonly ConditionalWeakTable<MotionModel, MotionRig> Rigs = new();

    static NifFile Load(byte[] nif)
    {
        var f = new NifFile();
        using var ms = new MemoryStream(nif, writable: false);
        if (f.Load(ms) != 0) throw new InvalidDataException("NiflySharp could not parse this mesh");
        return f;
    }

    static string KeyTypeProblem(NiKeyframeData kd)
    {
        if (kd.RotationType == KeyType.XYZ_ROTATION_KEY && kd.XYZRotations is { } xyz)
        {
            foreach (var o in xyz)
                if (o is KeyGroup<float> g && g.Keys is { Count: > 0 } && g.Interpolation is not (KeyType.LINEAR_KEY or KeyType.QUADRATIC_KEY or KeyType.CONST_KEY))
                    return $"rotation key type {g.Interpolation}";
        }
        else if (kd.QuaternionKeys is { Count: > 0 } && kd.RotationType != KeyType.LINEAR_KEY)
            return $"quaternion key type {kd.RotationType}";
        if (kd.Translations.Keys is { Count: > 0 } && kd.Translations.Interpolation is not (KeyType.LINEAR_KEY or KeyType.CONST_KEY))
            return $"translation key type {kd.Translations.Interpolation}";
        if (kd.Scales.Keys is { Count: > 0 } && kd.Scales.Interpolation is not (KeyType.LINEAR_KEY or KeyType.QUADRATIC_KEY or KeyType.CONST_KEY))
            return $"scale key type {kd.Scales.Interpolation}";
        return "";
    }

    /// <summary>Read what moves a mesh. <paramref name="read"/> resolves behaviour-graph files by their path relative to
    /// Data (null when the mesh needs none or the caller has no asset access — a graph then refuses).</summary>
    public static MotionModel Read(byte[] nif, Func<string, byte[]?>? read, MotionPlacement? placement = null, MotionReadOptions? options = null)
    {
        NifFile f;
        var labels = new List<string>();
        try { f = Load(nif); }
        catch (Exception ex) { return new MotionModel("refused", "unreadable mesh: " + ex.Message, "none", [], [], [], [], null); }
        var blocks = f.Blocks;
        var nodeByName = new Dictionary<string, NiAVObject>(StringComparer.Ordinal);
        foreach (var n in blocks.OfType<NiAVObject>()) if (n.Name?.String is { } nm && !nodeByName.ContainsKey(nm)) nodeByName[nm] = n;
        var materials = MeshVolume.ShapeMaterials(nif);
        // SMP physics: the NiStringExtraData name hdtSMP64 looks for on a node (scanBBP); its value names the physics XML.
        // The skinned shapes' bones are then simulated at runtime, so those shapes refuse.
        var smp = blocks.OfType<NiStringExtraData>().FirstOrDefault(x => string.Equals(x.Name?.String, SmpMarker, StringComparison.OrdinalIgnoreCase));
        var shapes = new List<RigShape>();
        int idx = 0;
        foreach (var sh in f.GetShapes())
        {
            var av = (NiAVObject)sh;
            var rs = new RigShape { Index = idx, Name = sh.Name?.String ?? $"#{idx + 1}", Shape = sh };
            rs.Chain = new MotionRig { Nif = f, Bytes = nif, Shapes = new(), NodeByName = nodeByName, Sources = new(), Poses = new() }.Chain(av);
            rs.Hidden = rs.Chain.Any(n => ((n.Flags_ui | n.Flags_us) & 1) != 0);
            rs.Particle = sh is NiParticles || sh.GetType().Name.Contains("Particle", StringComparison.Ordinal);
            rs.Drawn = ShapeDrawing.Drawn(rs.Name, rs.Hidden, rs.Particle, unread: false);
            if (materials is not null && idx < materials.Count) rs.MaterialClass = materials[idx].Class;
            rs.Vertices = (sh as BSTriShape)?.VertexPositions
                          ?? (sh.GeometryData?.GetType().GetProperty("Vertices")?.GetValue(sh.GeometryData) as List<Vector3>) ?? new();
            try { rs.Triangles = sh.Triangles ?? new(); } catch { rs.Triangles = new(); }
            if (rs.Particle) rs.Refusal = "particles: a particle system's geometry is runtime state";
            else if (options?.SwitchUnion != true && rs.Chain.FirstOrDefault(n => n.GetType().Name.Contains("SwitchNode", StringComparison.OrdinalIgnoreCase)) is { } sw)
                rs.Refusal = $"switch node '{sw.Name?.String}': the active child is runtime state";
            else if (options?.WindAsStatic != true && f.GetShader(sh) is { } shader && ShaderTreeAnim(shader))
                rs.Refusal = $"wind: the {shader.GetType().Name}'s Tree_Anim flag sways its vertices in the vertex shader at runtime";
            else if (smp is not null && (sh.IsSkinned || sh.HasSkinInstance))
                rs.Refusal = $"SMP physics: NiStringExtraData '{SmpMarker}' ('{smp.StringData?.String}') simulates its bones at runtime";
            shapes.Add(rs);
            idx++;
        }
        var rig = new MotionRig { Nif = f, Bytes = nif, Shapes = shapes, NodeByName = nodeByName, Sources = new(StringComparer.Ordinal), Poses = new() };
        string? modelRefusal = null;
        string tier = "none";
        string trigger = placement is null ? TriggerNotSettled : $"base:{placement.BaseRecordType} {TriggerNotSettled}";
        HavokGraph? graph = null;

        // ---- model-level refusals: physics, cloth ----
        if (options?.IgnorePhysics != true && blocks.FirstOrDefault(b => b.GetType().Name.StartsWith("bhk", StringComparison.Ordinal) && b.GetType().Name.Contains("Constraint", StringComparison.Ordinal)) is { } cons)
            modelRefusal = $"physics: {cons.GetType().Name} in the file (constrained rigid bodies move these parts)";
        else if (blocks.Any(b => b.GetType().Name.Contains("Cloth", StringComparison.Ordinal)))
            modelRefusal = "cloth: BSClothExtraData in the file (cloth simulation is runtime state)";

        var graphFile = blocks.OfType<BSBehaviorGraphExtraData>().Select(b => b.BehaviourGraphFile?.ToString()).FirstOrDefault(x => !string.IsNullOrEmpty(x));
        if (options?.IgnoreAnimation == true) { }
        else if (modelRefusal is null && graphFile is not null)
        {
            tier = "tier2";
            if (read is null) modelRefusal = $"behaviour graph '{graphFile}': no asset access to read it";
            else
            {
                graph = options?.Graph is { } go ? HavokBehaviour.Resolve(graphFile, read, go) : HavokBehaviour.Resolve(graphFile, read);
                if (graph.Status != "read") modelRefusal = $"behaviour graph '{graphFile}': {graph.Reason}";
                else modelRefusal = ReadGraph(rig, graph, read, trigger, options?.Graph, labels);
            }
        }
        // a confirmed clip: only that clip's source is kept, swept over its span or the window; with one clip there are no blends
        bool singleClip = false;
        if (options?.OnlyClip is { } oc && modelRefusal is null && graph is not null)
        {
            static string N(string p) => p.Replace('/', '\\').TrimStart('\\').ToLowerInvariant();
            var keep = rig.Sources.Where(kv => kv.Value.Animation.ClipPath is { } cp && N(cp) == N(oc.ClipPath)).ToList();
            if (keep.Count == 0) modelRefusal = $"confirmed clip '{oc.ClipPath}' is not among the behaviour graph's clips";
            else
            {
                rig.Sources.Clear();
                foreach (var (k, s) in keep)
                    rig.Sources[k] = oc.Window is { Length: 2 } w
                        ? new WindowSource(s, w[0], w[1]) { Animation = s.Animation with { Start = Math.Max(w[0], s.Animation.Start), End = Math.Min(w[1], s.Animation.End) } }
                        : s;
                singleClip = true;
            }
        }
        else if (options?.OnlyClip is { } oc2 && modelRefusal is null && graph is null)
            modelRefusal = $"confirmed clip '{oc2.ClipPath}': the model has no behaviour graph to read it from";
        if (graphFile is null && options?.IgnoreAnimation != true && options?.OnlyClip is null)
        {
            // read even under a physics/cloth refusal, so the key evaluator stays inspectable (NodeLocal); the refusal stands
            var animRefusal = ReadNifAnimations(rig, blocks, nodeByName, placement, trigger, options?.PathHull == true);
            modelRefusal ??= animRefusal;
            if (rig.Sources.Count > 0) tier = "tier1";
        }

        // ---- skins ----
        var skins = new List<MotionSkin>();
        if (modelRefusal is null)
            // a shape already refused (switch node, wind, SMP) keeps its named refusal; its skin is not read
            foreach (var rs in shapes.Where(s => !s.Hidden && !s.Particle && s.Refusal is null && (s.Shape.IsSkinned || s.Shape.HasSkinInstance)))
            {
                var sk = ReadSkin(rig, rs, options?.SkinUnion == true);
                if (sk is not null) skins.Add(sk);
                if (rs.WeightsAlt is not null && !labels.Contains(MeshReach.SkinUnionLabel)) labels.Add(MeshReach.SkinUnionLabel);
            }

        // ---- shape movers ----
        var driven = new HashSet<string>(rig.Sources.Values.SelectMany(s => s.Nodes), StringComparer.Ordinal);
        var motionShapes = new List<MotionShape>();
        foreach (var rs in shapes)
        {
            var on = rs.Chain.Take(rs.Chain.Count).Select(n => n.Name?.String).Where(n => n is not null && driven.Contains(n)).Select(n => n!).ToList();
            if (rs.Bones is not null) on.AddRange(rs.Bones.SelectMany(b => rig.Chain(b)).Select(n => n.Name?.String).Where(n => n is not null && driven.Contains(n)).Select(n => n!));
            on = on.Distinct().ToList();
            string mover = rs.Bones is not null ? "skinned" : on.Count > 0 ? "rigid" : "static";
            if (rs.Refusal is null && (rs.Shape.IsSkinned || rs.Shape.HasSkinInstance) && rs.Bones is null && !rs.Particle)
                rs.Refusal = "skinned shape: skin could not be read";
            if (rs.Refusal is null && graph is not null && !singleClip && !rig.BlendActive && graph.States.Count > 1 && rs.Chain.Count(n => n.Name?.String is { } nm && driven.Contains(nm)) >= 2)
                rs.Refusal = "two or more animated nodes on the chain under behaviour-graph transitions: blends not bounded";
            motionShapes.Add(new MotionShape(rs.Index, rs.Name, mover, on, rs.Refusal));
        }
        // the model's status still counts an undrawn shape's motion and refusal (it is posed, tagged not_drawn, so the soup stays aligned)
        bool anyMoving = motionShapes.Any(s => !shapes[s.ShapeIndex].Hidden && s.Mover != "static");
        var shapeRefusal = motionShapes.FirstOrDefault(s => !shapes[s.ShapeIndex].Hidden && !shapes[s.ShapeIndex].Particle && s.Refusal is not null);
        string status = modelRefusal is not null ? "refused" : shapeRefusal is not null ? "refused" : anyMoving ? "moving" : "static";
        string? reason = modelRefusal ?? (shapeRefusal is not null ? $"shape '{shapeRefusal.Name}': {shapeRefusal.Refusal}" : null);
        var model = new MotionModel(status, reason, tier, rig.Sources.Values.Select(s => s.Animation).ToList(), rig.Poses, motionShapes, skins, graph,
                                    options is null ? null : labels);
        Rigs.AddOrUpdate(model, rig);
        return model;
    }

    // ---------------- tier 1 ----------------
    static string? ReadNifAnimations(MotionRig rig, List<INiObject> blocks, Dictionary<string, NiAVObject> nodeByName, MotionPlacement? placement, string trigger, bool pathHull = false)
    {
        var seqNodes = new HashSet<NiTimeController>(ReferenceEqualityComparer.Instance);
        foreach (var seq in blocks.OfType<NiControllerSequence>())
        {
            var src = new NifKeySource
            {
                Animation = new MotionAnimation($"seq:{seq.Name?.String}", "nif-sequence", seq.Name?.String ?? "", seq.CycleType.ToString(),
                                                seq.StartTime, seq.StopTime, null, trigger),
            };
            foreach (var cb in seq.ControlledBlocks ?? new())
            {
                if (cb.Controller?.Index is int ci && ci >= 0 && ci < blocks.Count && blocks[ci] is NiTimeController tc) seqNodes.Add(tc);
                int ii = cb.Interpolator?.Index ?? -1;
                if (ii < 0 || ii >= blocks.Count) continue;
                var interp = blocks[ii];
                string ctype = cb.ControllerType?.ToString() ?? "";
                bool transform = ctype.Contains("Transform", StringComparison.Ordinal) || ctype.Contains("Keyframe", StringComparison.Ordinal) || interp is NiTransformInterpolator;
                if (!transform) continue;
                string node = cb.NodeName?.ToString() ?? "";
                if (pathHull && interp is NiPathInterpolator spi && nodeByName.TryGetValue(node, out var spn)) { if (!rig.PathNodes.Any(x => x.Node == spn)) rig.PathNodes.Add((spn, spi)); continue; }
                if (interp is not NiTransformInterpolator ti) return $"transform interpolator {interp.GetType().Name} on '{node}' is not supported";
                if (!nodeByName.ContainsKey(node)) return $"sequence '{seq.Name?.String}' drives a node '{node}' that is not in the file";
                int di = ti.Data?.Index ?? -1;
                if (di < 0 || blocks[di] is not NiKeyframeData kd) continue;          // pose-only interpolator: no motion
                var prob = KeyTypeProblem(kd);
                if (prob.Length > 0) return $"{prob} on '{node}' (sequence '{seq.Name?.String}') is not supported";
                src.Data[node] = kd;
            }
            if (src.Data.Count > 0) rig.Sources[src.Animation.Id] = src;
        }
        foreach (var n in blocks.OfType<NiAVObject>())
        {
            int ci = n.Controller?.Index ?? -1, guard = 0;
            while (ci >= 0 && ci < blocks.Count && guard++ < 64)
            {
                var c = blocks[ci];
                if (c is NiTransformController tc && !seqNodes.Contains(tc) && tc.Flags?.ManagerControlled != true)
                {
                    string node = n.Name?.String ?? "";
                    int ii = tc.Interpolator?.Index ?? -1;
                    var interp = ii >= 0 && ii < blocks.Count ? blocks[ii] : null;
                    if (pathHull && interp is NiPathInterpolator dpi) { if (!rig.PathNodes.Any(x => x.Node == n)) rig.PathNodes.Add((n, dpi)); ci = tc.NextController?.Index ?? -1; continue; }
                    if (interp is not null && interp is not NiTransformInterpolator) return $"{interp.GetType().Name} on '{node}' is not supported";
                    if (interp is NiTransformInterpolator ti && ti.Data?.Index is int di && di >= 0 && blocks[di] is NiKeyframeData kd && kd != null)
                    {
                        if (tc.Flags?.Active != true) return $"controller inactive: the transform controller on '{node}' is started at runtime";
                        var prob = KeyTypeProblem(kd);
                        if (prob.Length > 0) return $"{prob} on '{node}' (direct controller) is not supported";
                        var src = new NifKeySource
                        {
                            Animation = new MotionAnimation($"ctl:{node}", "nif-controller", node, tc.Flags.CycleType.ToString(), tc.StartTime, tc.StopTime, null, trigger),
                        };
                        src.Data[node] = kd;
                        rig.Sources[src.Animation.Id] = src;
                    }
                }
                ci = c is NiTimeController t2 ? (t2.NextController?.Index ?? -1) : -1;
            }
        }
        // poses
        foreach (var s in rig.Sources.Values)
        {
            rig.Poses.Add(new MotionPose($"{s.Animation.Id}@start", s.Animation.Id, s.Animation.Start, false, "files"));
            rig.Poses.Add(new MotionPose($"{s.Animation.Id}@end", s.Animation.Id, s.Animation.End, false, "files"));
        }
        var open = rig.Sources.Values.FirstOrDefault(s => s.Animation.Source == "nif-sequence" && s.Animation.Name.Equals("Open", StringComparison.OrdinalIgnoreCase) && s.Animation.Cycle == "CYCLE_CLAMP");
        var close = rig.Sources.Values.FirstOrDefault(s => s.Animation.Source == "nif-sequence" && s.Animation.Name.Equals("Close", StringComparison.OrdinalIgnoreCase) && s.Animation.Cycle == "CYCLE_CLAMP");
        if (open is not null)
        {
            bool openDefault = placement?.IsOpenByDefault == true;
            rig.Poses.Add(new MotionPose("closed", open.Animation.Id, open.Animation.Start, !openDefault,
                                         openDefault ? "closed (Open at start)" : "rest: closed (Open at start) — files; a script can change it at load"));
            rig.Poses.Add(new MotionPose("open", open.Animation.Id, open.Animation.End, openDefault,
                                         openDefault ? "rest: open — the reference sets IsOpenByDefault (files); a script can change it at load" : "open (Open at end)"));
            if (close is not null && DiffersAtEnd(rig, open, close))
                rig.Poses.Add(new MotionPose("after-close", close.Animation.Id, close.Animation.End, false, "after Close (Close at end differs from Open at start)"));
        }
        return null;
    }

    static bool DiffersAtEnd(MotionRig rig, MotionSource open, MotionSource close)
    {
        foreach (var node in open.Nodes.Union(close.Nodes))
        {
            if (!rig.NodeByName.TryGetValue(node, out var n)) continue;
            var st = rig.Stored(n, false);
            var a = open.Local(node, st, open.Animation.Start); var b = close.Local(node, st, close.Animation.End);
            if (Vector3.Distance(a.T, b.T) > 1e-4f || MathF.Abs(a.S - b.S) > 1e-5f) return true;
            for (int i = 0; i < 3; i++) for (int j = 0; j < 3; j++) if (MathF.Abs(a.R[i, j] - b.R[i, j]) > 1e-5f) return true;
        }
        return false;
    }

    // ---------------- tier 2 ----------------
    static string Mode(HavokClipGenerator c) => c.Mode;

    static string? ReadGraph(MotionRig rig, HavokGraph g, Func<string, byte[]?> read, string trigger, GraphOptions? go = null, List<string>? labels = null)
    {
        // binding policy (reach rules): each binding's member path decides; an unknown member refuses by name
        var widen = new Dictionary<string, string>(StringComparer.Ordinal);   // clip generator name -> "span" | "clip"
        if (go?.BindingPolicy == true && g.Bindings is { } binds)
        {
            foreach (var b in binds)
            {
                var effect = HavokBehaviour.BindingEffect(b.OwnerClass, b.MemberPath);
                if (effect is null) return $"{b.OwnerClass} '{b.Owner}' binds '{b.MemberPath}' to variable {b.VariableIndex}: not settled by the files";
                if (effect != "none" && b.OwnerClass == "hkbClipGenerator")
                    widen[b.Owner] = effect == "clip" || widen.GetValueOrDefault(b.Owner) == "clip" ? "clip" : "span";
            }
        }
        else
        {
            var bound = g.Clips.FirstOrDefault(c => c.HasVariableBinding);
            if (bound is not null) return $"clip generator '{bound.Name}' has a variable binding: its time or weight is runtime state";
        }
        HavokSkeleton? sk = null;
        if (g.Clips.Count > 0 || g.ReferencePoses is { Count: > 0 })
        {
            if (g.SkeletonPath is null || read(g.SkeletonPath) is not { } skb) return $"behaviour graph skeleton missing: {g.SkeletonPath}";
            sk = HavokAnimation.ReadSkeleton(skb);
        }
        var clipCache = new Dictionary<string, HavokClip>(StringComparer.OrdinalIgnoreCase);
        if (labels is not null && g.ClipLabels is { } cl)
            foreach (var l in cl) { string lab = "uncertified: " + l; if (!labels.Contains(lab)) labels.Add(lab); }
        foreach (var c in g.Clips)
        {
            var path = HavokBehaviour.ClipPath(g, c);
            if (!clipCache.TryGetValue(path, out var clip))
            {
                var bytes = read(path);
                if (bytes is null) return $"behaviour graph file missing: {path}";
                clip = HavokAnimation.Decode(bytes);
                if (clip.Status != "decoded") return $"clip '{path}': {clip.Reason}";
                clipCache[path] = clip;
            }
            float end = Math.Max(0, clip.Duration - c.CropEnd), start0 = Math.Clamp(c.CropStart, 0, end);
            float start, stop;
            switch (Mode(c))
            {
                case "SINGLE_PLAY": start = Math.Clamp(Math.Max(start0, c.StartTime), start0, end); stop = end; break;
                case "LOOPING": case "PING_PONG": start = start0; stop = end; break;
                case "USER_CONTROLLED": start = stop = start0 + Math.Clamp(c.UserControlledTimeFraction, 0, 1) * (end - start0); break;
                default: return $"clip generator '{c.Name}' mode {c.Mode} is not supported";
            }
            if (widen.TryGetValue(c.Name, out var w)) { if (w == "clip") { start = 0; stop = clip.Duration; } else { start = start0; stop = end; } }
            var src = new ClipSource
            {
                Clip = clip,
                Animation = new MotionAnimation($"clip:{c.Name}", "behaviour-clip", c.Name, c.Mode, start, stop, path, trigger),
            };
            for (int k = 0; k < clip.TransformTracks; k++)
            {
                int bi = clip.TrackToBone[k];
                if (sk is null || bi < 0 || bi >= sk.Bones.Count) return $"clip '{path}' track {k} maps to no skeleton bone";
                string bone = sk.Bones[bi];
                if (!rig.NodeByName.ContainsKey(bone)) return $"clip track bone '{bone}' has no NIF node";
                src.Track[bone] = k;
            }
            rig.Sources[src.Animation.Id] = src;
        }
        foreach (var rp in g.ReferencePoses ?? Array.Empty<string>())
        {
            var src = new RefPoseSource
            {
                Animation = new MotionAnimation($"ref:{rp}", "behaviour-reference-pose", rp, "REFERENCE_POSE", 0, 0, g.SkeletonPath, trigger),
            };
            for (int k = 0; k < sk!.Bones.Count && k < sk.ReferencePose.Count; k++)
                if (rig.NodeByName.ContainsKey(sk.Bones[k])) src.Pose[sk.Bones[k]] = sk.ReferencePose[k];
            rig.Sources[src.Animation.Id] = src;
            if (labels is not null && !labels.Contains(MeshReach.ReferencePoseLabel)) labels.Add(MeshReach.ReferencePoseLabel);
        }
        bool noTransformSource = !rig.Nif.Blocks.OfType<NiControllerSequence>().Any() && !rig.Nif.Blocks.OfType<NiTransformController>().Any();
        foreach (var sq in g.Sequences)
        {
            var seq = rig.Nif.Blocks.OfType<NiControllerSequence>().FirstOrDefault(s => s.Name?.String == sq.Sequence);
            if (seq is null && go?.MissingSequenceStoredPose == true && noTransformSource)
            {
                // the file holds no NiControllerSequence and no transform controller at all: no node can move whatever the
                // graph asks for, so the sequence plays nothing (the stored pose)
                if (labels is not null && !labels.Contains(MeshReach.MissingSequenceLabel)) labels.Add(MeshReach.MissingSequenceLabel);
                continue;
            }
            if (seq is null && go?.MissingSequenceUnion == true)
            {
                // Part 3b missingseq rule: whatever the engine does with a missing name (plays nothing, or falls back to another
                // sequence), the node poses are the stored pose or one of the file's own sequences: read them all; the caller holds
                // the stored pose too (MissingSequenceUnionLabel)
                string lab = MeshReach.MissingSequenceUnionLabel + $" ('{sq.Sequence}')";
                if (labels is not null && !labels.Contains(lab)) labels.Add(lab);
                foreach (var any in rig.Nif.Blocks.OfType<NiControllerSequence>())
                {
                    string id = $"gseq-any:{any.Name?.String}";
                    if (rig.Sources.ContainsKey(id)) continue;
                    var why = AddSequence(id, any.Name?.String ?? "", any);
                    if (why is not null) return why;
                }
                continue;
            }
            if (seq is null) return $"sequence generator '{sq.Name}' names a sequence '{sq.Sequence}' that is not in the NIF";
            var why2 = AddSequence($"gseq:{sq.Name}", sq.Sequence, seq);
            if (why2 is not null) return why2;
        }
        string? AddSequence(string id, string seqName, NiControllerSequence seq)
        {
            var src = new NifKeySource
            {
                Animation = new MotionAnimation(id, "behaviour-sequence", seqName, seq.CycleType.ToString(), seq.StartTime, seq.StopTime, null, trigger),
            };
            foreach (var cb in seq.ControlledBlocks ?? new())
            {
                int ii = cb.Interpolator?.Index ?? -1;
                if (ii < 0 || rig.Nif.Blocks[ii] is not NiTransformInterpolator ti) continue;
                if (ti.Data?.Index is int di && di >= 0 && rig.Nif.Blocks[di] is NiKeyframeData kd)
                {
                    var prob = KeyTypeProblem(kd);
                    if (prob.Length > 0) return $"{prob} on '{cb.NodeName}' (sequence '{seqName}') is not supported";
                    src.Data[cb.NodeName?.ToString() ?? ""] = kd;
                }
            }
            if (src.Data.Count > 0) rig.Sources[src.Animation.Id] = src;
            return null;
        }
        // Part 3b blend rule: transitions and blender children are bounded by the blend envelope (NifMotion.BlendBox), not refused
        bool blends = g.States.Count > 1 || g.Classes.Contains("hkbBlenderGenerator");
        if (go?.Blend == true && blends) rig.BlendActive = true;
        // 1-DOF blend rule, per driven node, over the poses every animation reaches (key/knot times and span ends)
        else if (g.States.Count > 1)
            foreach (var node in BlendCheckNodes(rig, rig.Sources.Values.SelectMany(s => s.Nodes)))
            {
                if (!rig.NodeByName.TryGetValue(node, out var n)) continue;
                var st = rig.Stored(n, false);
                var poses = new List<(Quaternion, Vector3)>();
                foreach (var s in rig.Sources.Values.Where(s => s.Nodes.Contains(node)))
                    foreach (var t in s.KeyTimes().Where(x => x >= s.Animation.Start && x <= s.Animation.End).Append(s.Animation.Start).Append(s.Animation.End))
                    {
                        var l = s.Local(node, st, t);
                        poses.Add((Quaternion.CreateFromRotationMatrix(l.R), l.T));
                    }
                if (SweptGeometry.OneDofRefusal(poses) is { } why) return $"transition blends between clips not bounded on node '{node}': {why}";
            }
        // named poses: each state's pose; the graph start state is the rest pose
        foreach (var s in g.States)
        {
            string? id = s.GeneratorKind switch { "clip" => $"clip:{s.GeneratorName}", "sequence" => $"gseq:{s.GeneratorName}", _ => null };
            if (id is null || !rig.Sources.TryGetValue(id, out var src)) continue;
            float t = src.Animation.Cycle is "SINGLE_PLAY" or "CYCLE_CLAMP" ? src.Animation.End : src.Animation.Start;
            bool rest = s.Name == g.StartStateName;
            rig.Poses.Add(new MotionPose($"state:{s.Name}", id, t, rest,
                                         rest ? "rest: graph start state — a script can send events at load" : $"state {s.Name}"));
        }
        foreach (var s in rig.Sources.Values)
        {
            rig.Poses.Add(new MotionPose($"{s.Animation.Id}@start", s.Animation.Id, s.Animation.Start, false, "files"));
            rig.Poses.Add(new MotionPose($"{s.Animation.Id}@end", s.Animation.Id, s.Animation.End, false, "files"));
        }
        return null;
    }

    // ---------------- skinning ----------------
    static MotionSkin? ReadSkin(MotionRig rig, RigShape rs, bool union = false)
    {
        var b = rig.Nif.Blocks;
        int skRef = (rs.Shape.GetType().GetProperty("SkinInstanceRef")?.GetValue(rs.Shape) as INiRef)?.Index ?? -1;
        if (skRef < 0 || skRef >= b.Count || b[skRef] is not NiSkinInstance si) { rs.Refusal = "skinned shape without a readable NiSkinInstance"; return null; }
        if (si.Data?.Index is not int di || di < 0 || b[di] is not NiSkinData sd) { rs.Refusal = "skinned shape without NiSkinData"; return null; }
        var bones = si.Bones.References.Select(r => r.Index >= 0 && r.Index < b.Count ? b[r.Index] as NiAVObject : null).ToList();
        if (bones.Any(x => x is null) || bones.Count != sd.BoneList.Count) { rs.Refusal = "skin bone list does not match its data"; return null; }
        int n = rs.Vertices.Count;
        var skin = sd.BoneList.Select(x => new NodeXf(MotionRig.Rows(x.SkinTransform.Rotation), x.SkinTransform.Scale == 0 ? 1 : x.SkinTransform.Scale, x.SkinTransform.Translation)).ToList();
        var wA = Enumerable.Range(0, n).Select(_ => new List<(int, float)>()).ToArray();
        for (int bi = 0; bi < sd.BoneList.Count; bi++)
            foreach (var vw in sd.BoneList[bi].VertexWeights ?? new()) if (vw.Index < n && vw.Weight > 0) wA[vw.Index].Add((bi, vw.Weight));
        // a pre-SSE NiSkinData may carry no vertex weights at all (HasVertexWeights off): the partition is then the only source
        bool skinDataWeights = wA.Any(x => x.Count > 0);
        List<(int, float)>[]? wB = null;
        var part = si.SkinPartition?.Index is int pi && pi >= 0 && pi < b.Count ? b[pi] as NiSkinPartition : null;
        var vds = (rs.Shape as BSTriShape)?.VertexDataSSE;
        if (part is not null)
        {
            wB = new List<(int, float)>[n];
            for (int v = 0; v < n; v++) wB[v] = new();
            if (part.Partitions.Count == 1 && vds is { } && vds.Count == n)
            {
            var pb = part.Partitions[0].Bones;
            for (int v = 0; v < n; v++)
            {
                wB[v] = new();
                for (int j = 0; j < 4; j++)
                {
                    float w = (float)vds[v].BoneWeights[j];
                    if (w <= 0) continue;
                    int local = vds[v].BoneIndices[j];
                    if (local >= pb.Count) { rs.Refusal = "skin partition bone index out of range"; return null; }
                    wB[v].Add((pb[local], w));
                }
            }
            }
            else
            {
                // pre-SSE partitions: per partition, vertex map (local -> shape vertex), weights and partition-local bone
                // indices per vertex (NumWeightsPerVertex each), partition bones (local -> skin bone)
                foreach (var sp in part.Partitions)
                {
                    int nv = sp.NumVertices, nw = sp.NumWeightsPerVertex;
                    if (sp.VertexMap is null || sp.VertexWeights is null || sp.BoneIndices is null || sp.Bones is null
                        || sp.VertexMap.Count < nv || sp.VertexWeights.Count < nv * nw || sp.BoneIndices.Count < nv * nw)
                    { rs.Refusal = "skin partition without vertex map, weights or bone indices"; return null; }
                    for (int j = 0; j < nv; j++)
                    {
                        int v = sp.VertexMap[j];
                        if (v >= n) { rs.Refusal = "skin partition maps a vertex outside the shape"; return null; }
                        for (int k = 0; k < nw; k++)
                        {
                            float w = sp.VertexWeights[j * nw + k];
                            if (w <= 0) continue;
                            int local = sp.BoneIndices[j * nw + k];
                            if (local >= sp.Bones.Count) { rs.Refusal = "skin partition bone index out of range"; return null; }
                            wB[v].Add((sp.Bones[local], w));
                        }
                    }
                }
            }
        }
        string authority = wB is null ? "skin-data" : "partition";
        string? reason = null;
        int differing = 0;
        bool criterion = true;
        // skin rule: only vertices a triangle references are drawn (indexed triangles), so only they can disagree
        HashSet<int>? drawn = null;
        if (union)
        {
            drawn = new HashSet<int>();
            foreach (var t in rs.Triangles) { drawn.Add(t.V1); drawn.Add(t.V2); drawn.Add(t.V3); }
            rs.DrawnOnly = true;
        }
        if (wB is not null && !skinDataWeights) reason = "skin data carries no vertex weights: the partition is the only source";
        if (wB is not null && skinDataWeights)
        {
            int noWeight = 0;
            for (int v = 0; v < n; v++)
            {
                if (drawn is not null && !drawn.Contains(v)) continue;
                var a = wA[v].GroupBy(x => x.Item1).ToDictionary(x => x.Key, x => x.Sum(y => y.Item2));
                var p = wB[v].GroupBy(x => x.Item1).ToDictionary(x => x.Key, x => x.Sum(y => y.Item2));
                bool same = a.Count == p.Count && p.All(kv => a.TryGetValue(kv.Key, out var w) && MathF.Abs(w - kv.Value) <= 2e-3f);
                if (same) continue;
                differing++;
                if (!(a.Count > 4 && p.Keys.All(a.ContainsKey))) criterion = false;
                if (a.Values.Sum() <= 0 || p.Values.Sum() <= 0) noWeight++;
            }
            if (differing > 0 && !criterion)
            {
                if (union && noWeight == 0) { authority = "partition"; rs.WeightsAlt = wA; reason = $"skin weight sources disagree on {differing} drawn vertices: posed under both"; }
                else if (union) { authority = "refused"; reason = $"skin weight sources disagree on {differing} drawn vertices, {noWeight} with no weight under one source"; rs.Refusal = reason; }
                else { authority = "refused"; reason = $"skin weight sources disagree on {differing} vertices"; rs.Refusal = reason; }
            }
        }
        rs.Bones = bones!; rs.Skin = skin; rs.Weights = authority == "partition" ? wB : wA;
        int unweighted = Enumerable.Range(0, rs.Weights!.Length).Count(v => (drawn is null || drawn.Contains(v)) && (rs.Weights[v].Count == 0 || rs.Weights[v].Sum(y => y.Item2) <= 0));
        if (unweighted > 0 && authority != "refused") { authority = "refused"; reason = $"skin: {unweighted} vertices carry no bone weight"; rs.Refusal = reason; }
        // checks at the stored transforms
        var composites = bones.Select((bn, i) => rig.Model(bn!, null, 0, false).Compose(skin[i])).ToList();
        var lo = rs.Vertices.Count > 0 ? rs.Vertices.Aggregate(new Vector3(float.MaxValue), Vector3.Min) : Vector3.Zero;
        var hi = rs.Vertices.Count > 0 ? rs.Vertices.Aggregate(new Vector3(float.MinValue), Vector3.Max) : Vector3.Zero;
        var corners = Enumerable.Range(0, 8).Select(k => new Vector3((k & 1) == 0 ? lo.X : hi.X, (k & 2) == 0 ? lo.Y : hi.Y, (k & 4) == 0 ? lo.Z : hi.Z)).ToList();
        double spread = composites.Count == 0 ? 0 : composites.Max(c => corners.Max(p => (double)Vector3.Distance(c.Apply(p), composites[0].Apply(p))));
        var shapeModel = rig.Model((NiAVObject)rs.Shape, null, 0, false);
        double bind = 0;
        if (rs.Weights is not null)
            for (int v = 0; v < n; v++)
                if ((drawn is null || drawn.Contains(v)) && rs.Weights[v].Count > 0 && rs.Weights[v].Sum(y => y.Item2) > 0)
                    bind = Math.Max(bind, Vector3.Distance(Skinned(rig, rs, v, null, 0), shapeModel.Apply(rs.Vertices[v])));
        double? diff = wB is null || !skinDataWeights ? null : differing;
        return new MotionSkin(rs.Index, spread, bind, diff, authority, reason);
    }

    internal static Vector3 Skinned(MotionRig rig, RigShape rs, int v, MotionSource? src, float t, Dictionary<NiAVObject, NodeXf>? cache = null)
    {
        var acc = Vector3.Zero; float ws = 0;
        foreach (var (bi, w) in rs.Weights![v])
        {
            var bone = rs.Bones![bi];
            NodeXf bm;
            if (cache is null || !cache.TryGetValue(bone, out bm)) { bm = rig.Model(bone, src, t, false); cache?.Add(bone, bm); }
            acc += w * bm.Apply(rs.Skin![bi].Apply(rs.Vertices[v]));
            ws += w;
        }
        return ws > 0 ? acc / ws : new Vector3(float.NaN);
    }

    internal static MotionRig RigOf(byte[] nif, MotionModel model, Func<string, byte[]?>? read)
    {
        if (Rigs.TryGetValue(model, out var r)) return r;
        var again = Read(nif, read);
        return Rigs.TryGetValue(again, out r) ? r : throw new InvalidOperationException("motion rig unavailable");
    }

    /// <summary>Pose the mesh at an animation's time (animation-local seconds).</summary>
    public static MotionPoseSoup Pose(byte[] nif, MotionModel model, string animationId, float time, VolumeGeometry geometry,
                                      Func<string, byte[]?>? read = null)
    {
        MotionPoseSoup Refuse(string why) => new("refused", why, null, null, animationId, time);
        if (model.Status == "refused") return Refuse(model.Reason ?? "refused");
        var rig = RigOf(nif, model, read);
        MotionSource? src = null;
        if (animationId != "stored" && !rig.Sources.TryGetValue(animationId, out src)) return Refuse($"no animation '{animationId}'");
        return PoseWith(rig, src, animationId, time, geometry);
    }

    internal static MotionPoseSoup PoseWith(MotionRig rig, MotionSource? src, string animationId, float time, VolumeGeometry geometry)
    {
        if (geometry == VolumeGeometry.Collision) return PoseCollision(rig, src, animationId, time);
        var v = new List<Vector3>(); var idx = new List<int>(); var tags = new List<string>();
        var boneCache = new Dictionary<NiAVObject, NodeXf>(ReferenceEqualityComparer.Instance);
        foreach (var rs in rig.Shapes)
        {
            if (rs.Hidden || rs.Particle) continue;
            if (rs.Refusal is not null) return new("refused", $"shape '{rs.Name}': {rs.Refusal}", null, null, animationId, time);
            if (rs.DrawnOnly)
            {
                // skin rule: only the vertices a triangle references, posed under every weight source the rule keeps
                foreach (var t in rs.Triangles)
                    if (t.V1 >= rs.Vertices.Count || t.V2 >= rs.Vertices.Count || t.V3 >= rs.Vertices.Count)
                        return new("refused", $"shape '{rs.Name}': a triangle indexes outside its vertices", null, null, animationId, time);
                var keepW = rs.Weights;
                var sources = rs.WeightsAlt is null ? new[] { rs.Weights } : new[] { rs.Weights, rs.WeightsAlt };
                foreach (var w in sources)
                {
                    rs.Weights = w;
                    var map = new Dictionary<int, int>();
                    foreach (var t in rs.Triangles)
                    {
                        foreach (int vi in new[] { (int)t.V1, (int)t.V2, (int)t.V3 })
                            if (!map.ContainsKey(vi))
                            {
                                map[vi] = v.Count;
                                v.Add(w is not null ? Skinned(rig, rs, vi, src, time, boneCache) : rig.Model((NiAVObject)rs.Shape, src, time, false).Apply(rs.Vertices[vi]));
                            }
                        idx.Add(map[t.V1]); idx.Add(map[t.V2]); idx.Add(map[t.V3]);
                        tags.Add(rs.Tag);
                    }
                }
                rs.Weights = keepW;
                continue;
            }
            int baseIdx = v.Count;
            if (rs.Weights is not null)
                for (int k = 0; k < rs.Vertices.Count; k++) v.Add(Skinned(rig, rs, k, src, time, boneCache));
            else
            {
                var xf = rig.Model((NiAVObject)rs.Shape, src, time, false);
                foreach (var p in rs.Vertices) v.Add(xf.Apply(p));
            }
            foreach (var t in rs.Triangles)
            {
                if (t.V1 >= rs.Vertices.Count || t.V2 >= rs.Vertices.Count || t.V3 >= rs.Vertices.Count)
                    return new("refused", $"shape '{rs.Name}': a triangle indexes outside its vertices", null, null, animationId, time);
                idx.Add(baseIdx + t.V1); idx.Add(baseIdx + t.V2); idx.Add(baseIdx + t.V3);
                tags.Add(rs.Tag);
            }
        }
        if (idx.Count == 0) return new("refused", "render: no visible triangles", null, null, animationId, time);
        return new("posed", null, new TriangleSoup(v.ToArray(), idx.ToArray()), tags.ToArray(), animationId, time);
    }

    static MotionPoseSoup PoseCollision(MotionRig rig, MotionSource? src, string animationId, float time)
    {
        rig.Collision ??= HavokCollision.Decode(rig.Nif, includeTriangles: true);
        var c = rig.Collision;
        float unit = HavokCollision.UnitScaleFor(c.StreamVersion);
        var v = new List<Vector3>(); var idx = new List<int>();
        foreach (var body in c.Bodies.Where(b => b.Solid))
        {
            if (body.Unread.Count > 0) return new("refused", "collision_incomplete: a solid body has an unread shape", null, null, animationId, time);
            var xf = body.Node is null ? NodeXf.Identity : rig.Model(body.Node, src, time, true);
            int baseIdx = v.Count;
            foreach (var p in body.Vertices) v.Add(xf.Apply(p * unit));
            foreach (var k in body.Indices) idx.Add(baseIdx + k);
        }
        if (idx.Count == 0) return new("refused", "no_solid_collision", null, null, animationId, time);
        return new("posed", null, new TriangleSoup(v.ToArray(), idx.ToArray()), Enumerable.Repeat(SurfaceMaterial.Collision, idx.Count / 3).ToArray(), animationId, time);
    }

    /// <summary>Part 3b (reach rule `transfer`): a graph model's transfer facts - every graph file it reads (project, character,
    /// behaviour, skeleton, each clip as the reader resolves it) with its sha256, and the stored local transform of every NIF node a
    /// clip track drives. Null when the model names no behaviour graph or the graph cannot be resolved.</summary>
    public static (IReadOnlyDictionary<string, string> Files, IReadOnlyDictionary<string, NodeLocal> DrivenLocals)? GraphFacts(byte[] nif, Func<string, byte[]?> read, GraphOptions? options = null)
    {
        NifFile f;
        try { f = Load(nif); } catch { return null; }
        var graphFile = f.Blocks.OfType<BSBehaviorGraphExtraData>().Select(b => b.BehaviourGraphFile?.ToString()).FirstOrDefault(x => !string.IsNullOrEmpty(x));
        if (graphFile is null) return null;
        var g = HavokBehaviour.Resolve(graphFile, read, options ?? new GraphOptions(true, true, true));
        if (g.Status != "read") return null;
        string Sha(string p) => read(p) is { } b ? Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(b)).ToLowerInvariant() : "missing";
        var files = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var p in new[] { g.ProjectPath, g.CharacterPath, g.BehaviourPath, g.SkeletonPath }.Where(p => p is not null)) files[p!] = Sha(p!);
        foreach (var c in g.Clips) { var p = HavokBehaviour.ClipPath(g, c); files[p] = Sha(p); }
        var locals = new Dictionary<string, NodeLocal>(StringComparer.Ordinal);
        if (g.SkeletonPath is null || read(g.SkeletonPath) is not { } skb) return (files, locals);
        var sk = HavokAnimation.ReadSkeleton(skb);
        var byName = new Dictionary<string, NiAVObject>(StringComparer.Ordinal);
        foreach (var n in f.Blocks.OfType<NiAVObject>()) if (n.Name?.String is { } nm && !byName.ContainsKey(nm)) byName[nm] = n;
        foreach (var c in g.Clips)
        {
            if (read(HavokBehaviour.ClipPath(g, c)) is not { } cb) continue;
            var clip = HavokAnimation.Decode(cb);
            if (clip.Status != "decoded") continue;
            for (int k = 0; k < clip.TransformTracks; k++)
            {
                int bi = clip.TrackToBone[k];
                if (bi < 0 || bi >= sk.Bones.Count || !byName.TryGetValue(sk.Bones[bi], out var node)) continue;
                var r = MotionRig.Rows(node.Rotation);
                locals[sk.Bones[bi]] = new NodeLocal(node.Translation, Quaternion.Normalize(Quaternion.CreateFromRotationMatrix(r)), node.Scale);
            }
        }
        return (files, locals);
    }

    /// <summary>Pose the mesh at a named pose.</summary>
    public static MotionPoseSoup Pose(byte[] nif, MotionModel model, string poseName, VolumeGeometry geometry,
                                      Func<string, byte[]?>? read = null)
    {
        var p = poseName == "rest" ? model.Poses.FirstOrDefault(x => x.Rest) : model.Poses.FirstOrDefault(x => x.Name == poseName);
        if (p is null) return new("refused", $"no pose '{poseName}'", null, null, "", 0);
        return Pose(nif, model, p.AnimationId, p.Time, geometry, read);
    }

    /// <summary>A node's local transform at an animation's time, in NifGeometry's row sense (diagnostic and test
    /// access to the evaluator). <paramref name="animationId"/> may also be <c>nifkeys:&lt;node&gt;</c>: the node's own
    /// embedded controller keys, evaluated even on a behaviour-graph mesh (never a swept animation).</summary>
    public static (Matrix4x4 Rotation, float Scale, Vector3 Translation) NodeLocal(byte[] nif, MotionModel model, string animationId,
                                                                                  string nodeName, float time, Func<string, byte[]?>? read = null)
    {
        if (animationId.StartsWith("nifkeys:", StringComparison.Ordinal))
        {
            var f = Load(nif);
            var node = f.Blocks.OfType<NiAVObject>().FirstOrDefault(n => n.Name?.String == animationId[8..])
                       ?? throw new ArgumentException($"no node '{animationId[8..]}'");
            int ci = node.Controller?.Index ?? -1, guard = 0;
            while (ci >= 0 && ci < f.Blocks.Count && guard++ < 64)
            {
                if (f.Blocks[ci] is NiTransformController tc && tc.Interpolator?.Index is int ii && ii >= 0 && f.Blocks[ii] is NiTransformInterpolator ti
                    && ti.Data?.Index is int di && di >= 0 && f.Blocks[di] is NiKeyframeData kd)
                {
                    var st = new NodeXf(MotionRig.Rows(node.Rotation), node.Scale, node.Translation);
                    var l = NifKeySource.Eval(kd, st, time);
                    return (l.R, l.S, l.T);
                }
                ci = f.Blocks[ci] is NiTimeController t2 ? (t2.NextController?.Index ?? -1) : -1;
            }
            throw new ArgumentException($"node '{animationId[8..]}' has no transform controller with keys");
        }
        var rig = RigOf(nif, model, read);
        if (!rig.Sources.TryGetValue(animationId, out var src)) throw new ArgumentException($"no animation '{animationId}'");
        if (!rig.NodeByName.TryGetValue(nodeName, out var n)) throw new ArgumentException($"no node '{nodeName}'");
        var x = src.Local(nodeName, rig.Stored(n, false), time);
        return (x.R, x.S, x.T);
    }
}
