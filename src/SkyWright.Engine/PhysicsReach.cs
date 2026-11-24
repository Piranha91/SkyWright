using System.Numerics;
using NiflySharp;
using NiflySharp.Blocks;
using NiflySharp.Enums;
using NiflySharp.Structs;

using HousecarlCore;

namespace SkyWright.Engine;

// PHYSICS REACH (unbounded neighbour classes, Part 1). What a NIF's Havok bodies say about whether its parts can move,
// and the two regions built from that:
//  * a body is IMMOVABLE when its mass is 0 (nif.xml bhkRigidBodyCInfo: "A mass of zero represents an immovable object")
//    or its motion system is MO_SYS_FIXED / MO_SYS_KEYFRAMED; MOVABLE when its mass is above 0 and its motion system is a
//    simulated one (DYNAMIC, SPHERE/BOX INERTIA or STABILIZED, THIN_BOX: nif.xml hkMotionType); any other motion system
//    with a mass refuses by name;
//  * constraints join bodies into islands; an island holding an immovable body is PINNED and its movable members swing
//    within the constraint's limits (bhkLimitedHingeConstraint: rotation about Axis A through Pivot A, limited by Min/Max
//    Angle; bhkHingeConstraint: the same with no limit) — the region is that envelope, labelled as read from the file;
//  * movable bodies in islands without an immovable body are FREE and get the modelling rule: they drop straight down,
//    rigidly at their placed pose, to first contact with the nearest supporting collision below — labelled as the rule;
//  * a support is a solid body of another reference, enabled in the state, not movable, on a layer the dropping body's
//    layer collides with according to its COLL record, whose own layer is neither a trigger volume nor a sensor.
// Neither region is ever labelled certified. Units and frames as HavokCollision: Havok units × the era's unit scale; a
// bhkRigidBodyT's own frame applies, a plain body's does not; then the node chain (root left out), then the placement.

/// <summary>One rigid body of a NIF. <c>Layer</c> is the Havok filter's layer number (the COLL record's Index);
/// <c>Movable</c> = mass &gt; 0 and a simulated motion system.</summary>
public sealed record PhysicsBody(int Block, string Node, uint Layer, string LayerName, string MotionSystem, float Mass, bool Movable);

/// <summary>One constraint: its class, the two entities (body block indices) and, for a limited hinge, its limits in
/// radians. The pivots and axes are each entity's own statement placed in the model frame (Skyrim units) at the stored pose
/// — the pivots are equal when the constraint is satisfied at rest.</summary>
public sealed record PhysicsConstraint(int Block, string Class, int BodyA, int BodyB, float? MinAngle, float? MaxAngle,
                                       Vector3 PivotAModel, Vector3 PivotBModel, Vector3 AxisAModel, Vector3 AxisBModel);

/// <summary>Kind: <c>none</c> (no rigid body), <c>fixed</c> (no movable body), <c>free</c> (movable, no constraint),
/// <c>pinned</c> (every island with a movable body holds an immovable body), <c>free-chain</c> (an island of movable bodies
/// only), <c>mixed</c>, or <c>refused</c> (Refusal names the constraint or body that cannot be read).</summary>
public sealed record PhysicsModel(string Kind, IReadOnlyList<PhysicsBody> Bodies, IReadOnlyList<PhysicsConstraint> Constraints, string? Refusal);

/// <summary>A COLL record as the drop rule reads it: its Index, editor id, the TriggerVolume and Sensor flags, and the
/// indices of the layers it collides with.</summary>
public sealed record CollisionLayerRule(uint Index, string? Name, bool TriggerVolume, bool Sensor, IReadOnlySet<uint> CollidesWith);

/// <summary>A candidate support: another reference's world collision soup for one body, its layer, whether the body is
/// solid (not a phantom) and whether it is movable. <c>AssumedStatic</c>: the file says the body may move but its placing
/// base is a Static, so it is held only by the untested Static assumption (a drop landing on it carries that label).
/// <c>CoreWorld</c>: the same body read without its Havok radius, when the caller has it (the drop's shell-crossing check).</summary>
public sealed record DropSupport(string Id, uint Layer, bool Solid, bool Movable, TriangleSoup World, bool AssumedStatic = false,
                                 string? Group = null, TriangleSoup? CoreWorld = null, LooseRole Loose = LooseRole.None);

/// <summary>A loose (movable) support's role under the rest rule's loose-support pass: <c>None</c> (not read: a movable body is no
/// support), <c>Holds</c> (its own reading holds it at load: touching, lifted, mounted or exempt; it supports what is above it),
/// <c>Moves</c> (it drops, is lost, refused or pushed off: a body whose first contact is on it is refused by name), <c>Cycle</c>
/// (loose bodies that rest on each other: refused by name).</summary>
public enum LooseRole { None = 0, Holds = 1, Moves = 2, Cycle = 3 }

/// <summary>The drop: <c>dropped</c>, <c>touching</c> (d = 0, already in contact at placement) or <c>refused</c> (nothing
/// below, or an unreadable collision). <c>Placed</c> is the placed box (render ∪ collision), <c>Landed</c> the same box moved
/// down by <c>Distance</c>, <c>Prism</c> their union (the region). <c>SupportId</c> names the support met first.</summary>
public sealed record DropResult(string Status, string? Reason, double Distance, Aabb Placed, Aabb Landed, Aabb Prism, string? SupportId,
                                bool SupportAssumedStatic = false, double DistanceMax = double.NaN, bool PushedOff = false, bool DownThroughSlab = false,
                                PushCause Causes = PushCause.None, double PushDepth = double.NaN, double PushUpDepth = double.NaN,
                                bool LowerBound = false);

/// <summary>Why a pair the body overlapped at placement was left out (pushed sideways or down), by the support piece and the push:
/// a wall-like slab's face, a hull's side, a down-facing slab (ceiling), a slab's border rim, a hull pushing down, an up-facing slab
/// pushed through some other way, an up-facing slab pushed down through its bottom, and an impalement (a deep downward push whose
/// upward alternative is a near tie, <see cref="PhysicsReach.ImpaleMinDepth"/>, <see cref="PhysicsReach.ImpaleTieRatio"/>).</summary>
[Flags]
public enum PushCause { None = 0, SlabWall = 1, HullSide = 2, SlabCeiling = 4, SlabRimBorder = 8, HullDown = 16, SlabFloorOther = 32, UpFacingDown = 64, Impaled = 128 }

public static class PhysicsReach
{
    public const string DropLabel = "rest: dropped (modelling rule)";
    /// <summary>The drop read with every Havok radius is LONGER than the same drop read without it, which grown shapes cannot do
    /// unless the body starts inside a support's radius shell and the crossing test missed it (convex-radius-default 2026-10-03,
    /// RESULT §6: about 24 of 9,326 G1 rows). The label names it and carries the radius-off drop; the row is not corrected.</summary>
    // not emitted since drop-crossing lane 2 (2026-10-13): the row is refused or its gap marked instead; kept for older manifests
    public const string ShellCrossingLabel = "drop starts inside a support's radius shell; drop length may overstate (crossing test limited)";
    public const string DropTouchingLabel = "rest: dropped (modelling rule) — touching at placement";
    /// <summary>The body overlapped a support piece at placement and the push that resolves it is sideways or down, so it falls past that
    /// piece (drop-crossing redesign 2026-10-03; in the lab, items starting in a side clip left it). Where it lands after the push is not
    /// modelled (the lab items also travelled sideways).</summary>
    // not emitted since drop-crossing lane 2 (2026-10-13): the row is refused or its gap marked instead; kept for older manifests
    public const string PushedOffLabel = "drop: pushed off a support it overlapped at placement; travel after the push is not modelled";
    /// <summary>A pushed-off piece was a mesh slab and the push went down through it (the body sat past the slab's middle). Kept as
    /// read; untested in game.</summary>
    // not emitted since drop-crossing lane 2 (2026-10-13): the row is refused or its gap marked instead; kept for older manifests
    public const string DownThroughSlabLabel = "drop: downward push through a thin mesh slab: unverified in game (one lab station agreed only on a confounded frame feature; the pose read at load was faulty for that shape)";
    public const string RigidChainLabel = "free chain dropped rigidly at its placed pose; flex on landing is not modelled";
    public const string HingeLabel = "constrained: limits read from the file (bhkLimitedHingeConstraint)";
    public const string StaticAssumptionLabel = "not simulated: a Static base did not move in the lab (one model, crossbow: its loose twin fell and was pushed; beam model: no push acted)";
    public const string TerrainNotReadLabel = "drop: terrain is not read as a support (the fall can only be longer)";
    public const string TerrainDiagonalLabel = "drop: the terrain's triangulation is not in the file; the landing lies between the two readings";

    /// <summary>A LAND heightmap patch as two alternative drop supports (Group = <paramref name="id"/>): the quads of the
    /// heightmap under <paramref name="column"/> cut along each diagonal. Which diagonal the engine's terrain collision cuts
    /// is not in the file (NavmeshFloor.LandHeightAt), so the drop takes the first contact over both and reports the range.</summary>
    public static IReadOnlyList<DropSupport> TerrainSupports(string id, float[,] heights, int cellX, int cellY, Aabb column, uint terrainLayer)
    {
        float sp = LandHeight.VertexSpacing; int n = LandHeight.Side;
        float ox = cellX * 4096f, oy = cellY * 4096f;
        int i0 = Math.Max(0, (int)MathF.Floor((column.Min.X - ox) / sp)), i1 = Math.Min(n - 2, (int)MathF.Floor((column.Max.X - ox) / sp));
        int j0 = Math.Max(0, (int)MathF.Floor((column.Min.Y - oy) / sp)), j1 = Math.Min(n - 2, (int)MathF.Floor((column.Max.Y - oy) / sp));
        if (i0 > i1 || j0 > j1) return Array.Empty<DropSupport>();
        var v = new List<Vector3>(); var a = new List<int>(); var b = new List<int>();
        int Vtx(int i, int j) { v.Add(new Vector3(ox + i * sp, oy + j * sp, heights[i, j])); return v.Count - 1; }
        for (int i = i0; i <= i1; i++)
            for (int j = j0; j <= j1; j++)
            {
                int v00 = Vtx(i, j), v10 = Vtx(i + 1, j), v11 = Vtx(i + 1, j + 1), v01 = Vtx(i, j + 1);
                a.AddRange(new[] { v00, v10, v11, v00, v11, v01 });   // cut along 00-11
                b.AddRange(new[] { v00, v10, v01, v10, v11, v01 });   // cut along 10-01
            }
        var verts = v.ToArray();
        return new[]
        {
            new DropSupport(id, terrainLayer, true, false, new TriangleSoup(verts, a.ToArray()), Group: id),
            new DropSupport(id, terrainLayer, true, false, new TriangleSoup(verts, b.ToArray()), Group: id),
        };
    }
    public const string NothingBelow = "dropped: no collision surface below in the searched cells";
    /// <summary>The rest rule (loose-item clashes): a settling body with no support below is an outcome, not a refusal. The
    /// wording carries what the lab measured (one item over empty space in one interior).</summary>
    public const string LostLabel = "rest: lost - settles at load with no support below; measured in the lab: the engine keeps it falling and returns it to its placed height every few seconds (one item, one interior)";
    /// <summary>The rest rule: an exempt body with no support below hangs at its placed pose.</summary>
    public const string FloatsLabel = "rest: floats over nothing - rests where placed with no support below";
    /// <summary>A moving body whose collision has vertices and no faces cannot meet anything; refused by name, never read as
    /// "nothing below".</summary>
    /// <summary>A pushed-off settling body: it moves on load, its rest place is not read. The bound wording is used only for
    /// causes a lab station covered.</summary>
    public const string PushedOffReason = "drop: pushed off a support it overlapped at placement: moves on load; falls at least the reader's drop, within the lab tolerance; where it comes to rest is not read";
    public const string PushedOffUnverifiedReason = "drop: pushed off a support it overlapped at placement: moves on load; where it comes to rest is not read; the reader's drop is not verified as a bound in game";
    public const string UpFacingDownReason = "drop: overlaps an up-facing surface past its middle at placement: whether the engine holds it or pushes it down through is not read";
    public const string ImpaledReason = "drop: impaled through a support at placement: the way out is a near tie and is not read";
    public const string ShellCrossingReason = "drop: starts inside a support's radius shell and the crossing test is limited: the drop may overstate";
    /// <summary>An impalement: a left-out downward push at least this deep (Skyrim units) whose cheapest allowed upward push is within
    /// <see cref="ImpaleTieRatio"/> of it.</summary>
    /// <summary>Rest rule part 1: an upward push at placement deeper than this reads <c>lifted</c>; shallower stays <c>touching</c>.</summary>
    public const double LiftMinDepth = 0.05;
    public const string LiftedLabel = "rest: lifted - held embedded in its support at placement; moves up on load; resting pose not read; the region holds it within the tilt margin";
    public const string LandsOnMovingLooseReason = "drop: lands on a loose item that itself moves at load; resting pose not read";
    public const string LooseCycleReason = "drop: rests on loose items that rest on each other; resting pose not read";
    /// <summary>A depth or drop as printed in a reason (u).</summary>
    public static string FormatDepth(double x) => x > 0 && x < 0.005 ? "under 0.01" : x.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture);
    public const double ImpaleMinDepth = 5.0;
    public const double ImpaleTieRatio = 1.25;
    /// <summary>A push out of a down-facing slab is a lab-covered cause (a lower bound) only below this depth (Skyrim units): the one
    /// ceiling station was a graze, inside the mesh shell's scale.</summary>
    public const double CeilingGrazeDepth = 0.7;
    /// <summary>The push causes a lab station measured (the game fell at least the reader's drop, within the lab tolerance): a side push
    /// out of a wall-like slab's face or off a hull's side, and a ceiling push shallower than <see cref="CeilingGrazeDepth"/>.</summary>
    public const PushCause BoundCauses = PushCause.SlabWall | PushCause.HullSide | PushCause.SlabCeiling;
    public const string NoFacesReason = "physics: the moving body's collision has no faces";

    static readonly HashSet<hkMotionType> Simulated = new()
    {
        hkMotionType.MO_SYS_DYNAMIC, hkMotionType.MO_SYS_SPHERE_INERTIA, hkMotionType.MO_SYS_SPHERE_STABILIZED,
        hkMotionType.MO_SYS_BOX_INERTIA, hkMotionType.MO_SYS_BOX_STABILIZED, hkMotionType.MO_SYS_THIN_BOX,
    };

    static NifFile Load(byte[] nif)
    {
        var f = new NifFile();
        using var ms = new MemoryStream(nif, writable: false);
        if (f.Load(ms) != 0) throw new InvalidDataException("NiflySharp could not parse this mesh");
        return f;
    }

    static uint StreamVersion(NifFile f) { try { return f.Header.Version.StreamVersion; } catch { return 0; } }

    /// <summary>(mass, motion system, layer number, layer name) of a rigid body in its file's era.</summary>
    static (float Mass, hkMotionType Motion, uint Layer, string LayerName) BodyFacts(bhkRigidBody rb, uint bsver)
    {
        if (bsver >= 130) { var i = rb.RigidBodyInfo_bRBCI2014; return (i.Mass, i.MotionSystem, (uint)rb.HavokFilter.Layer_SL, rb.HavokFilter.Layer_SL.ToString()); }
        if (bsver >= 83) { var i = rb.RigidBodyInfo_bRBCI2010; return (i.Mass, i.MotionSystem, (uint)rb.HavokFilter.Layer_SL, rb.HavokFilter.Layer_SL.ToString()); }
        var o = rb.RigidBodyInfo_bRBCI550660; return (o.Mass, o.MotionSystem, (uint)rb.HavokFilter.Layer_OL, rb.HavokFilter.Layer_OL.ToString());
    }

    /// <summary>The body's own frame: a bhkRigidBodyT's translation and rotation (Havok units); identity for a plain body
    /// (nif.xml: "a normal bhkRigidBody ignores" them; measured, HavokCollision.DecodeCollisionObject).</summary>
    static NifTransform BodyFrame(bhkRigidBody rb, uint bsver)
    {
        if (rb is not bhkRigidBodyT) return NifTransform.Identity;
        Vector4 tr; hkQuaternion q;
        if (bsver >= 130) { tr = rb.RigidBodyInfo_bRBCI2014.Translation; q = rb.RigidBodyInfo_bRBCI2014.Rotation; }
        else if (bsver >= 83) { tr = rb.RigidBodyInfo_bRBCI2010.Translation; q = rb.RigidBodyInfo_bRBCI2010.Rotation; }
        else { tr = rb.RigidBodyInfo_bRBCI550660.Translation; q = rb.RigidBodyInfo_bRBCI550660.Rotation; }
        return new NifTransform(HavokCollision.FromQuaternion(q), 1f, new Vector3(tr.X, tr.Y, tr.Z));
    }

    /// <summary>The node each rigid body's collision object hangs off, by body block index.</summary>
    static Dictionary<int, NiAVObject> BodyNodes(NifFile f)
    {
        var r = new Dictionary<int, NiAVObject>();
        foreach (var n in f.Blocks.OfType<NiAVObject>())
        {
            if (n.CollisionObject?.Index is not int ci || ci < 0 || ci >= f.Blocks.Count || f.Blocks[ci] is not bhkNiCollisionObject co) continue;
            if (co.Body?.Index is int bi && bi >= 0 && !r.ContainsKey(bi)) r[bi] = n;
        }
        return r;
    }

    sealed class Model
    {
        public required NifFile File;
        public required uint Bsver;
        public required float Unit;
        public required MotionRig Rig;
        public required Dictionary<int, NiAVObject> Nodes;
        public required List<PhysicsBody> Bodies;
        public required List<PhysicsConstraint> Constraints;
        public required Dictionary<int, NifTransform> Frames;
        public NodeXf NodeModel(NiAVObject n, bool collision) => Rig.Model(n, null, 0, collision);
        public Vector3 BodyPoint(int body, Vector4 p) =>
            NodeModel(Nodes[body], true).Apply(Frames[body].Apply(new Vector3(p.X, p.Y, p.Z)) * Unit);
        public Vector3 BodyDirection(int body, Vector4 d)
        {
            var dir = Vector3.Transform(new Vector3(d.X, d.Y, d.Z), Frames[body].Rotation);
            var nm = NodeModel(Nodes[body], true);
            var w = Vector3.Transform(dir, nm.R);
            return w.LengthSquared() > 0 ? Vector3.Normalize(w) : w;
        }
    }

    static Model Read(byte[] nif)
    {
        var f = Load(nif);
        uint bsver = StreamVersion(f);
        var nodes = BodyNodes(f);
        var nodeByName = new Dictionary<string, NiAVObject>(StringComparer.Ordinal);
        foreach (var n in f.Blocks.OfType<NiAVObject>()) if (n.Name?.String is { } nm && !nodeByName.ContainsKey(nm)) nodeByName[nm] = n;
        var rig = new MotionRig { Nif = f, Bytes = nif, Shapes = new(), NodeByName = nodeByName, Sources = new(), Poses = new() };
        var bodies = new List<PhysicsBody>(); var frames = new Dictionary<int, NifTransform>();
        for (int i = 0; i < f.Blocks.Count; i++)
        {
            if (f.Blocks[i] is not bhkRigidBody rb) continue;
            var (mass, motion, layer, layerName) = BodyFacts(rb, bsver);
            bool immovable = mass <= 0 || motion is hkMotionType.MO_SYS_FIXED or hkMotionType.MO_SYS_KEYFRAMED;
            bool movable = !immovable && Simulated.Contains(motion);
            string node = nodes.TryGetValue(i, out var nn) ? nn.Name?.String ?? $"#{i}" : $"(no node) #{i}";
            bodies.Add(new PhysicsBody(i, node, layer, layerName, motion.ToString(), mass, movable));
            frames[i] = BodyFrame(rb, bsver);
        }
        var m = new Model { File = f, Bsver = bsver, Unit = HavokCollision.UnitScaleFor(bsver), Rig = rig, Nodes = nodes, Bodies = bodies, Constraints = new(), Frames = frames };
        for (int i = 0; i < f.Blocks.Count; i++)
        {
            var b = f.Blocks[i];
            if (!b.GetType().Name.StartsWith("bhk", StringComparison.Ordinal) || !b.GetType().Name.Contains("Constraint", StringComparison.Ordinal)) continue;
            if (b.GetType().GetProperty("ConstraintInfo")?.GetValue(b) is not bhkConstraintCInfo ci) continue;
            int ea = ci.EntityA?.Index ?? -1, eb = ci.EntityB?.Index ?? -1;
            Vector3 pa = Vector3.Zero, pb = Vector3.Zero, aa = Vector3.Zero, ab = Vector3.Zero; float? lo = null, hi = null;
            bool known = nodes.ContainsKey(ea) && nodes.ContainsKey(eb) && frames.ContainsKey(ea) && frames.ContainsKey(eb);
            if (known && b is bhkLimitedHingeConstraint lh)
            {
                var c = lh.Constraint;
                pa = m.BodyPoint(ea, c.PivotA); pb = m.BodyPoint(eb, c.PivotB); aa = m.BodyDirection(ea, c.AxisA); ab = m.BodyDirection(eb, c.AxisB);
                lo = c.MinAngle; hi = c.MaxAngle;
            }
            else if (known && b is bhkHingeConstraint hg)
            {
                var c = hg.Constraint;
                pa = m.BodyPoint(ea, c.PivotA); pb = m.BodyPoint(eb, c.PivotB); aa = m.BodyDirection(ea, c.AxisA); ab = m.BodyDirection(eb, c.AxisB);
            }
            m.Constraints.Add(new PhysicsConstraint(i, b.GetType().Name, ea, eb, lo, hi, pa, pb, aa, ab));
        }
        return m;
    }

    /// <summary>Islands: each set of bodies joined by constraints (a body on its own is its own island).</summary>
    static List<List<int>> Islands(IReadOnlyList<PhysicsBody> bodies, IReadOnlyList<PhysicsConstraint> cons)
    {
        var parent = bodies.ToDictionary(b => b.Block, b => b.Block);
        int Find(int x) { while (parent[x] != x) x = parent[x] = parent[parent[x]]; return x; }
        foreach (var c in cons) if (parent.ContainsKey(c.BodyA) && parent.ContainsKey(c.BodyB)) parent[Find(c.BodyA)] = Find(c.BodyB);
        return bodies.GroupBy(b => Find(b.Block)).Select(g => g.Select(b => b.Block).ToList()).ToList();
    }

    static string? ReadRefusal(Model m)
    {
        foreach (var b in m.Bodies)
        {
            if (b.Movable || b.Mass <= 0 || b.MotionSystem is "MO_SYS_FIXED" or "MO_SYS_KEYFRAMED") continue;
            return $"physics: body on '{b.Node}' has mass {b.Mass} and motion system {b.MotionSystem}, which is not classified";
        }
        var ids = m.Bodies.Select(b => b.Block).ToHashSet();
        foreach (var c in m.Constraints)
            if (!ids.Contains(c.BodyA) || !ids.Contains(c.BodyB)) return $"physics: {c.Class} joins an entity that is not a rigid body of this file";
        foreach (var b in m.Bodies) if (!m.Nodes.ContainsKey(b.Block)) return $"physics: a rigid body (block {b.Block}) hangs off no node";
        return null;
    }

    static string Kind(IReadOnlyList<PhysicsBody> bodies, IReadOnlyList<PhysicsConstraint> cons)
    {
        if (bodies.Count == 0) return "none";
        if (!bodies.Any(b => b.Movable)) return "fixed";
        var byId = bodies.ToDictionary(b => b.Block);
        var kinds = new HashSet<string>();
        foreach (var isl in Islands(bodies, cons))
        {
            if (!isl.Any(i => byId[i].Movable)) continue;
            if (isl.Any(i => !byId[i].Movable)) kinds.Add("pinned");
            else kinds.Add(isl.Count == 1 ? "free" : "free-chain");
        }
        if (kinds.Contains("pinned")) return kinds.Count == 1 ? "pinned" : "mixed";
        return kinds.Contains("free-chain") ? "free-chain" : "free";
    }

    /// <summary>Read the bodies and constraints of a NIF and classify them.</summary>
    public static PhysicsModel Classify(byte[] nif)
    {
        Model m;
        try { m = Read(nif); }
        catch (Exception ex) { return new PhysicsModel("refused", Array.Empty<PhysicsBody>(), Array.Empty<PhysicsConstraint>(), "physics: unreadable mesh: " + ex.Message); }
        var why = ReadRefusal(m);
        return new PhysicsModel(why is null ? Kind(m.Bodies, m.Constraints) : "refused", m.Bodies, m.Constraints, why);
    }

    // ------------------------------------------------------------------------------------------------ first contact
    /// <summary>The smallest d ≥ 0 such that <paramref name="moving"/> translated by (0,0,−d) touches
    /// <paramref name="support"/>; null when no translation downward ever meets it. Exact for triangle soups: the minimum
    /// over moving vertices cast down onto support triangles, support vertices cast up onto moving triangles, and
    /// edge–edge vertical contacts.
    /// <para>A body that already overlaps the support at d = 0 (drop-crossing redesign, 2026-10-03): both soups are split into
    /// convex components (<see cref="ConvexPush"/>) and every overlapping (body, support) component pair is resolved by its minimum
    /// translation. A push with n_z ≥ 0.5 lifts the body: it is held, and the contact is 0. Any other push moves it off that piece
    /// sideways or down (a modelling rule: the lab confirmed that items starting in a side clip leave it): that pair's triangles
    /// are left out of the casts, so the body falls past the piece it clips. Pairs that do not overlap keep the casts.</para></summary>
    public static double? FirstContact(TriangleSoup moving, TriangleSoup support) => Contact(moving, support).D;

    /// <summary>As <see cref="FirstContact"/>, with whether a pair was pushed off (left out) and whether one such push went down
    /// out of an up-facing (or wall) mesh slab. A mesh slab never pushes out through an internal edge (<see cref="ConvexPush.PushAllowed"/>;
    /// reinstated after lab 4, 2026-10-04). A downward push out of a down-facing slab (n_z &lt;= -0.5, a surface above the body) is
    /// lab-verified (lab 4 L4-1) and is not flagged.</summary>
    public static ContactReading Contact(TriangleSoup moving, TriangleSoup support)
    {
        var mb = SpatialOverlap.SoupBounds(moving);
        if (mb.IsEmpty) return new(null);
        var ms = ConvexPush.Of(moving); var ss = ConvexPush.Of(support);
        if (ms.Tris.Length == 0 || ss.Tris.Length == 0) return new(null);
        // overlaps at the placed pose, per component pair
        HashSet<(int M, int S)>? leftOut = null; bool down = false;
        var causes = PushCause.None; bool bounded = true, boundedUp = true; double unreadDepth = double.NaN, unreadUp = double.NaN; bool unreadImpaled = false;
        double held = double.NaN;   // rest rule part 1: the deepest upward (held) push; any held pair makes this support read d = 0
        for (int mi = 0; mi < ms.Comps.Count; mi++)
            for (int si = 0; si < ss.Comps.Count; si++)
            {
                if (!ConvexPush.BoxesMeet(ms.Boxes[mi], ss.Boxes[si])) continue;
                int sc = si;
                if (ConvexPush.Push(ms.Comps[mi].Select(k => ms.Tris[k]).ToList(), ss.Comps[si].Select(k => ss.Tris[k]).ToList(),
                                    d => ConvexPush.PushAllowed(ss, sc, d)) is not { } push) continue;
                if (push.Dir.Z >= 0.5) { if (!(push.Depth <= held)) held = push.Depth; continue; }   // held: pushed up, it rests on this support
                if (!double.IsNaN(held)) continue;                                  // already held: only deeper held pairs matter
                (leftOut ??= new()).Add((mi, si));                                  // pushed off sideways or down: falls past it
                if (push.Dir.Z <= -0.5 && ss.Slab[si] is { } sl && sl.Normal.Z > -0.5) down = true;
                // why it was left out (lane 2): the piece and the push; a deep downward push whose allowed upward way out is a near tie is an
                // impalement; the direction of an impalement or of a push down through an up-facing surface is not read in game
                var cause = CauseOf(ss.Slab[si], push.Dir);
                double up = double.NaN; bool impaled = false;
                if (push.Dir.Z <= -0.5 && push.Depth >= ImpaleMinDepth
                    && ConvexPush.Push(ms.Comps[mi].Select(k => ms.Tris[k]).ToList(), ss.Comps[si].Select(k => ss.Tris[k]).ToList(),
                                       d => ConvexPush.PushAllowed(ss, sc, d) && d.Z >= 0.5) is { } u && u.Depth <= ImpaleTieRatio * push.Depth)
                { impaled = true; up = u.Depth; }
                causes |= cause | (impaled ? PushCause.Impaled : PushCause.None);
                bounded &= !impaled && (BoundCauses & cause) == cause && (cause != PushCause.SlabCeiling || push.Depth < CeilingGrazeDepth);
                // lab 5 (2026-10-13): a push down through an up-facing surface is covered where the shape matches (Drop decides)
                boundedUp &= !impaled && ((BoundCauses | PushCause.UpFacingDown) & cause) == cause && (cause != PushCause.SlabCeiling || push.Depth < CeilingGrazeDepth);
                if (impaled || cause == PushCause.UpFacingDown)
                {
                    if (cause == PushCause.UpFacingDown && !impaled)
                        up = ConvexPush.Push(ms.Comps[mi].Select(k => ms.Tris[k]).ToList(), ss.Comps[si].Select(k => ss.Tris[k]).ToList(),
                                             d => ConvexPush.PushAllowed(ss, sc, d) && d.Z >= 0.5) is { } u2 ? u2.Depth : double.NaN;
                    // the deepest direction-unread pair names the depths, an impalement before an up-facing push
                    if ((impaled && !unreadImpaled) || (impaled == unreadImpaled && !(push.Depth <= unreadDepth)))
                    { unreadDepth = push.Depth; unreadUp = up; unreadImpaled = impaled; }
                }
            }
        if (!double.IsNaN(held)) return new(0, Held: true, HeldDepth: held);
        var st = TrisK(support, mb);
        if (leftOut is null)
        {
            var mt = Tris(moving, null);
            if (mt.Count == 0 || st.Count == 0) return new(null);
            double b = Casts(Verts(moving), mt, st.Select(x => x.T).ToList());
            return new(double.IsPositiveInfinity(b) ? null : b);
        }
        double best = double.PositiveInfinity;
        for (int mi = 0; mi < ms.Comps.Count; mi++)
        {
            var mt = ms.Comps[mi].Select(k => ToTri(ms.Tris[k])).ToList();
            var keep = st.Where(x => !leftOut.Contains((mi, ss.CompOf[x.K]))).Select(x => x.T).ToList();
            if (mt.Count == 0 || keep.Count == 0) continue;
            best = Math.Min(best, Casts(mt.SelectMany(t => new[] { t.A, t.B, t.C }).Distinct(), mt, keep));
        }
        return new(double.IsPositiveInfinity(best) ? null : best, true, down, causes, bounded, unreadDepth, unreadUp, BoundedIfUpCovered: boundedUp);
    }

    /// <summary>One support's contact reading: the first contact, whether a pair was pushed off, whether a push went down out of an
    /// up-facing or wall slab (the old flag), the causes of the left-out pairs, whether every left-out pair is a lab-covered cause
    /// (<see cref="BoundCauses"/>), and the deepest direction-unread pair's push and allowed upward push (NaN when none).</summary>
    public readonly record struct ContactReading(double? D, bool PushedOff = false, bool DownThroughSlab = false, PushCause Causes = PushCause.None,
                                                 bool Bounded = true, double PushDepth = double.NaN, double PushUpDepth = double.NaN,
                                                 bool Held = false, double HeldDepth = double.NaN, bool BoundedIfUpCovered = true);

    /// <summary>The cause of one left-out pair, by the support piece and the push (unit) direction: a non-slab piece pushes down
    /// (n_z &lt;= -0.5) or sideways; a slab push along its normal (|dir . n| &gt;= 0.5) is up-facing down / other, ceiling or wall by the
    /// slab's own n_z; any other slab push leaves through a rim (never an internal one, <see cref="ConvexPush.PushAllowed"/>).</summary>
    static PushCause CauseOf(ConvexPush.SlabInfo? slab, ConvexPush.P3 dir)
    {
        if (slab is null) return dir.Z <= -0.5 ? PushCause.HullDown : PushCause.HullSide;
        var n = slab.Normal; double pdn = dir.X * n.X + dir.Y * n.Y + dir.Z * n.Z;
        if (Math.Abs(pdn) < 0.5) return PushCause.SlabRimBorder;
        if (n.Z >= 0.5) return pdn <= -0.5 ? PushCause.UpFacingDown : PushCause.SlabFloorOther;
        return n.Z <= -0.5 ? PushCause.SlabCeiling : PushCause.SlabWall;
    }

    static Tri ToTri(ConvexPush.T3 t) => new(new D3(t.A.X, t.A.Y, t.A.Z), new D3(t.B.X, t.B.Y, t.B.Z), new D3(t.C.X, t.C.Y, t.C.Z));

    /// <summary>The least non-negative vertical gap of the three cast families (a negative gap is a surface above); +∞ when none.</summary>
    static double Casts(IEnumerable<D3> movingVerts, List<Tri> mt, List<Tri> st)
    {
        double best = double.PositiveInfinity;
        void Take(double t) { if (t >= -1e-9) best = Math.Min(best, Math.Max(0, t)); }
        // moving vertices down onto support triangles
        foreach (var p in movingVerts) foreach (var s in st) if (Above(s, p, out double z)) Take(p.Z - z);
        // support vertices up onto moving triangles
        foreach (var q in st.SelectMany(s => new[] { s.A, s.B, s.C }).Distinct()) foreach (var m in mt) if (Above(m, q, out double z)) Take(z - q.Z);
        // edge-edge
        var me = Edges(mt); var se = Edges(st);
        foreach (var (a, b) in me)
            foreach (var (c, d) in se)
                if (EdgeEdge(a, b, c, d, out double za, out double zc)) Take(za - zc);
        return best;
    }

    readonly record struct D3(double X, double Y, double Z);
    readonly record struct Tri(D3 A, D3 B, D3 C);

    static IEnumerable<D3> Verts(TriangleSoup s) => s.Vertices.Select(v => new D3(v.X, v.Y, v.Z)).Distinct();

    static List<Tri> Tris(TriangleSoup s, Aabb? footprint) => TrisK(s, footprint).Select(x => x.T).ToList();

    /// <summary>The soup's triangles (with their ordinal k / 3), those wholly outside the footprint or above it left out.</summary>
    static List<(Tri T, int K)> TrisK(TriangleSoup s, Aabb? footprint)
    {
        var r = new List<(Tri, int)>();
        for (int k = 0; k + 2 < s.Indices.Length; k += 3)
        {
            var a = s.Vertices[s.Indices[k]]; var b = s.Vertices[s.Indices[k + 1]]; var c = s.Vertices[s.Indices[k + 2]];
            if (footprint is { } f)
            {
                float x0 = MathF.Min(a.X, MathF.Min(b.X, c.X)), x1 = MathF.Max(a.X, MathF.Max(b.X, c.X));
                float y0 = MathF.Min(a.Y, MathF.Min(b.Y, c.Y)), y1 = MathF.Max(a.Y, MathF.Max(b.Y, c.Y));
                float z0 = MathF.Min(a.Z, MathF.Min(b.Z, c.Z));
                if (x1 < f.Min.X || x0 > f.Max.X || y1 < f.Min.Y || y0 > f.Max.Y || z0 > f.Max.Z) continue;
            }
            r.Add((new Tri(new D3(a.X, a.Y, a.Z), new D3(b.X, b.Y, b.Z), new D3(c.X, c.Y, c.Z)), k / 3));
        }
        return r;
    }

    static List<(D3, D3)> Edges(List<Tri> tris)
    {
        var set = new HashSet<(D3, D3)>();
        foreach (var t in tris)
            foreach (var (u, v) in new[] { (t.A, t.B), (t.B, t.C), (t.C, t.A) })
                set.Add(Compare(u, v) <= 0 ? (u, v) : (v, u));
        return set.ToList();
    }
    static int Compare(D3 u, D3 v) => u.X != v.X ? u.X.CompareTo(v.X) : u.Y != v.Y ? u.Y.CompareTo(v.Y) : u.Z.CompareTo(v.Z);

    /// <summary>Whether p's vertical line meets triangle t (closed, in the xy projection); z = the triangle's height there.
    /// A triangle seen edge-on from above (zero projected area) is met only through its edges (edge–edge family).</summary>
    static bool Above(Tri t, D3 p, out double z)
    {
        z = 0;
        double d = (t.B.X - t.A.X) * (t.C.Y - t.A.Y) - (t.C.X - t.A.X) * (t.B.Y - t.A.Y);
        if (Math.Abs(d) < 1e-12) return false;
        double u = ((p.X - t.A.X) * (t.C.Y - t.A.Y) - (t.C.X - t.A.X) * (p.Y - t.A.Y)) / d;
        double v = ((t.B.X - t.A.X) * (p.Y - t.A.Y) - (p.X - t.A.X) * (t.B.Y - t.A.Y)) / d;
        const double e = 1e-9;
        if (u < -e || v < -e || u + v > 1 + e) return false;
        z = t.A.Z + u * (t.B.Z - t.A.Z) + v * (t.C.Z - t.A.Z);
        return true;
    }

    /// <summary>Where the xy projections of segments ab and cd cross (non-parallel), the heights of each there.</summary>
    static bool EdgeEdge(D3 a, D3 b, D3 c, D3 d, out double za, out double zc)
    {
        za = zc = 0;
        double rx = b.X - a.X, ry = b.Y - a.Y, sx = d.X - c.X, sy = d.Y - c.Y;
        double den = rx * sy - ry * sx;
        if (Math.Abs(den) < 1e-12) return false;
        double qx = c.X - a.X, qy = c.Y - a.Y;
        double t = (qx * sy - qy * sx) / den, u = (qx * ry - qy * rx) / den;
        const double e = 1e-9;
        if (t < -e || t > 1 + e || u < -e || u > 1 + e) return false;
        za = a.Z + t * (b.Z - a.Z); zc = c.Z + u * (d.Z - c.Z);
        return true;
    }

    // ------------------------------------------------------------------------------------------------ supports and drop
    /// <summary>Whether a candidate holds up a body on <paramref name="movingLayer"/>: solid, not movable, its layer listed
    /// in the moving layer's CollidesWith, and its own layer neither a trigger volume nor a sensor. An unknown layer is not
    /// a support.</summary>
    public static bool IsSupport(uint movingLayer, DropSupport candidate, IReadOnlyDictionary<uint, CollisionLayerRule> layers)
    {
        if (!candidate.Solid || (candidate.Movable && candidate.Loose == LooseRole.None)) return false;
        if (!layers.TryGetValue(movingLayer, out var ml) || !layers.TryGetValue(candidate.Layer, out var sl)) return false;
        if (sl.TriggerVolume || sl.Sensor) return false;
        return ml.CollidesWith.Contains(candidate.Layer);
    }

    /// <summary>Drop a world collision soup onto the first support below. <paramref name="placedBox"/> is the placed world
    /// box (render ∪ collision) that the prism extends downward.</summary>
    public static DropResult Drop(TriangleSoup movingWorld, Aabb placedBox, uint movingLayer, IEnumerable<DropSupport> supports,
                                  IReadOnlyDictionary<uint, CollisionLayerRule> layers)
    {
        var placed = placedBox.Union(SpatialOverlap.SoupBounds(movingWorld));
        // a body with vertices and no faces can meet nothing: refused by name, never read as "nothing below"
        if (movingWorld.Vertices.Length > 0 && movingWorld.Indices.Length == 0)
            return new DropResult("refused", NoFacesReason, double.NaN, placed, Aabb.Empty, Aabb.Empty, null);
        // supports in one Group are alternative readings of one surface (only one is real): the group meets the object first
        // at its earliest reading, and the real contact may be as late as its latest
        double best = double.PositiveInfinity, bestMax = double.PositiveInfinity; string? id = null; bool assumed = false, pushedOff = false, downSlab = false;
        var causes = PushCause.None; bool bounded = true, boundedUp = true; double pDepth = double.NaN, pUp = double.NaN; bool pImpaled = false;
        double heldBest = double.NaN; var loose = LooseRole.None; var upIds = new HashSet<string>();
        foreach (var grp in supports.Where(s => IsSupport(movingLayer, s, layers))
                                    .GroupBy(s => s.Group is null ? "\u0001" + s.Id + "\u0001" + Guid.NewGuid() : "\u0002" + s.Group)
                                    .OrderBy(g => g.First().Id, StringComparer.Ordinal))
        {
            // a loose support the body is pushed off at placement is not read as one (two movable bodies separate; how is not read)
            var cs = grp.Select(s => (S: s, C: Contact(movingWorld, s.World))).Where(x => !(x.S.Loose != LooseRole.None && x.C.PushedOff)).ToList();
            foreach (var c in cs)
            {
                pushedOff |= c.C.PushedOff; downSlab |= c.C.DownThroughSlab;
                if (!c.C.PushedOff) continue;
                causes |= c.C.Causes; bounded &= c.C.Bounded; boundedUp &= c.C.BoundedIfUpCovered;
                if ((c.C.Causes & PushCause.UpFacingDown) != 0) upIds.Add(c.S.Id);
                bool imp = (c.C.Causes & PushCause.Impaled) != 0;
                if (!double.IsNaN(c.C.PushDepth) && ((imp && !pImpaled) || (imp == pImpaled && !(c.C.PushDepth <= pDepth))))
                { pDepth = c.C.PushDepth; pUp = c.C.PushUpDepth; pImpaled = imp; }
            }
            var ds = cs.Select(x => (x.S, D: x.C.D)).Where(x => x.D is not null).ToList();
            if (ds.Count == 0) continue;
            double lo = ds.Min(x => x.D!.Value), hi = grp.First().Group is null ? lo : ds.Max(x => x.D!.Value);
            // the deepest held push and the most severe loose role among the readings met first
            double HeldAt(double d) => cs.Where(x => x.C.D == d && x.C.Held).Select(x => x.C.HeldDepth).DefaultIfEmpty(double.NaN).Max();
            LooseRole LooseAt(double d) => ds.Where(x => x.D == d).Select(x => x.S.Loose).DefaultIfEmpty(LooseRole.None).Max();
            if (lo < best) { best = lo; bestMax = hi; id = grp.First().Id; assumed = ds.Any(x => x.S.AssumedStatic && x.D == lo); heldBest = HeldAt(lo); loose = LooseAt(lo); }
            else if (lo == best && grp.First().Id == id)
            {
                assumed |= ds.Any(x => x.S.AssumedStatic && x.D == lo); bestMax = Math.Max(bestMax, hi);
                var h = HeldAt(lo); if (!double.IsNaN(h) && !(h <= heldBest)) heldBest = h;
                if (LooseAt(lo) > loose) loose = LooseAt(lo);
            }
        }
        if (id is null) return new DropResult("refused", NothingBelow, double.NaN, placed, Aabb.Empty, Aabb.Empty, null);
        var landed = Down(placed, best);
        bool ranged = bestMax - best > 1e-3;
        // rest rule part 3: a first contact on a loose item that itself moves, or on loose items resting on each other, is not read
        if (loose is LooseRole.Moves or LooseRole.Cycle)
            return new DropResult("refused", loose == LooseRole.Moves ? LandsOnMovingLooseReason : LooseCycleReason, best, placed, landed, placed.Union(landed), id);
        // rest rule part 1: held embedded deeper than LiftMinDepth -> lifted (moves up on load)
        bool lifted = !ranged && best <= 0 && heldBest > LiftMinDepth;
        // rest rule part 5 (lab 5): one convex body pushed down through an up-facing surface, landing on another reference: the bound applies
        bool upCovered = (causes & PushCause.UpFacingDown) != 0 && (causes & PushCause.Impaled) == 0 && !upIds.Contains(id)
                         && ConvexPush.Of(movingWorld).Comps.Count == 1;
        var prism = placed.Union(landed).Union(Down(placed, bestMax));
        return new DropResult(ranged ? "dropped-range" : best <= 0 ? (lifted ? "lifted" : "touching") : "dropped", null, best, placed, landed, prism, id, assumed,
                              ranged ? bestMax : double.NaN, PushedOff: pushedOff && best > 0, DownThroughSlab: downSlab && best > 0,
                              Causes: pushedOff && best > 0 ? causes : PushCause.None, PushDepth: pushedOff && best > 0 ? pDepth : double.NaN,
                              PushUpDepth: pushedOff && best > 0 ? pUp : lifted ? heldBest : double.NaN,
                              LowerBound: pushedOff && best > 0 && (bounded || (upCovered && boundedUp)));
    }

    /// <summary>A box moved down by d, rounded outward (never shallower than the exact fall).</summary>
    static Aabb Down(Aabb b, double d)
    {
        float Lo(double v) { float f = (float)v; return f > v ? MathF.BitDecrement(f) : f; }
        float Hi(double v) { float f = (float)v; return f < v ? MathF.BitIncrement(f) : f; }
        return new Aabb(new Vec3(b.Min.X, b.Min.Y, Lo(b.Min.Z - d)), new Vec3(b.Max.X, b.Max.Y, Hi(b.Max.Z - d)), false);
    }

    /// <summary>The model-space solid collision of every body (Skyrim units, frames and node chain applied): its rigid body's
    /// layer, whether it is movable, and its soup. Phantoms are listed with Solid false. <paramref name="convexRadius"/> (on by
    /// default): every shape read with its Havok radius, the surface the engine's drop rests on (measured in game for convex items
    /// and convex and compressed-mesh supports; runs/2026-10-03-convex-radius-default).</summary>
    public static IReadOnlyList<(uint Layer, bool Solid, bool Movable, bool Complete, TriangleSoup Model)> ModelBodies(byte[] nif, bool convexRadius = true)
    {
        var m = Read(nif);
        var col = HavokCollision.Decode(m.File, includeTriangles: true, convexRadius: convexRadius);
        var movableByNode = new Dictionary<NiAVObject, (uint Layer, bool Movable)>(ReferenceEqualityComparer.Instance);
        foreach (var b in m.Bodies) if (m.Nodes.TryGetValue(b.Block, out var n)) movableByNode[n] = (b.Layer, b.Movable);
        var r = new List<(uint, bool, bool, bool, TriangleSoup)>();
        foreach (var body in col.Bodies)
        {
            var xf = body.Node is null ? NodeXf.Identity : m.NodeModel(body.Node, true);
            var v = body.Vertices.Select(p => xf.Apply(p * m.Unit)).ToArray();
            uint layer; bool movable;
            if (body.Node is not null && movableByNode.TryGetValue(body.Node, out var fact)) (layer, movable) = fact;
            else { layer = Enum.TryParse<SkyrimLayer>(body.Layer, out var sl) ? (uint)sl : uint.MaxValue; movable = false; }
            r.Add((layer, body.Solid, movable, body.Unread.Count == 0, new TriangleSoup(v, body.Indices)));
        }
        return r;
    }

    /// <summary>The world collision soup of a NIF's movable bodies at the placed pose, and the layer of the first movable
    /// body; null when the file has no movable body or a movable body's shape is unread.</summary>
    public static (TriangleSoup World, uint Layer)? MovingCollision(byte[] nif, Vec3 position, Vec3 rotation, float scale, bool convexRadius = true)
    {
        var bodies = ModelBodies(nif, convexRadius).Where(b => b.Movable && b.Solid).ToList();
        if (bodies.Count == 0 || bodies.Any(b => !b.Complete)) return null;
        var v = new List<Vector3>(); var idx = new List<int>();
        foreach (var b in bodies)
        {
            int baseIdx = v.Count;
            v.AddRange(b.Model.Vertices); idx.AddRange(b.Model.Indices.Select(i => i + baseIdx));
        }
        return (SpatialOverlap.TransformSoup(new TriangleSoup(v.ToArray(), idx.ToArray()), position, rotation, scale), bodies[0].Layer);
    }

    /// <summary>The world collision soups of every body of a placed NIF, as drop supports (Id = <paramref name="id"/>). A
    /// body is movable when its file says so AND the placing base is simulated (<paramref name="movableBase"/>).</summary>
    public static IReadOnlyList<DropSupport> Supports(string id, byte[] nif, Vec3 position, Vec3 rotation, float scale, bool movableBase) =>
        PlaceSupports(id, ModelBodies(nif), position, rotation, scale, movableBase, ModelBodies(nif, convexRadius: false));

    /// <summary>As <see cref="Supports"/> from already-read model bodies (a caller caches them per model).
    /// <c>coreBodies</c> (optional): the same bodies read without the radius, in the same order; carried as each support's
    /// <c>CoreWorld</c> for the shell-crossing check.</summary>
    public static IReadOnlyList<DropSupport> PlaceSupports(string id, IReadOnlyList<(uint Layer, bool Solid, bool Movable, bool Complete, TriangleSoup Model)> bodies,
                                                         Vec3 position, Vec3 rotation, float scale, bool movableBase,
                                                         IReadOnlyList<(uint Layer, bool Solid, bool Movable, bool Complete, TriangleSoup Model)>? coreBodies = null)
    {
        bool aligned = coreBodies is not null && coreBodies.Count == bodies.Count;
        return bodies.Select((b, i) => (b, i)).Where(x => x.b.Model.Indices.Length > 0)
              .Select(x => new DropSupport(id, x.b.Layer, x.b.Solid, x.b.Movable && movableBase, SpatialOverlap.TransformSoup(x.b.Model, position, rotation, scale),
                                           AssumedStatic: x.b.Movable && !movableBase,
                                           CoreWorld: aligned && coreBodies![x.i].Model.Indices.Length > 0 ? SpatialOverlap.TransformSoup(coreBodies[x.i].Model, position, rotation, scale) : null))
              .ToList();
    }

    /// <summary>The shell-crossing check: the shortest of the same drop read with less of the radius — every radius off (the
    /// moving body's core over each support's <c>CoreWorld</c>), the moving body grown over core supports, and the core body over
    /// grown supports. A reading with more of the radius cannot be longer than one with less, so a longer drop is a crossing the
    /// test missed. Null when no support carries a core soup or every reduced drop is refused.</summary>
    public static double? CoreDrop(byte[] nif, Vec3 position, Vec3 rotation, float scale, Aabb placedBox, IEnumerable<DropSupport> supports,
                                   IReadOnlyDictionary<uint, CollisionLayerRule> layers)
    {
        var list = supports.ToList();
        if (!list.Any(s => s.CoreWorld is not null)) return null;
        var coreSupports = list.Select(s => s.CoreWorld is { } c ? s with { World = c } : s).ToList();
        var core = MovingCollision(nif, position, rotation, scale, convexRadius: false);
        var grown = MovingCollision(nif, position, rotation, scale, convexRadius: true);
        double? best = null;
        void Take((TriangleSoup World, uint Layer)? body, IEnumerable<DropSupport> sup)
        {
            if (body is not { } b) return;
            var d = Drop(b.World, placedBox, b.Layer, sup, layers);
            if (d.Status != "refused" && (best is null || d.Distance < best)) best = d.Distance;
        }
        Take(core, coreSupports); Take(grown, coreSupports); Take(core, list);
        return best;
    }

    // ------------------------------------------------------------------------------------------------ hinge envelope
    /// <summary>A pinned island's hinge tree: the one reading the hinge envelope and the member reader (MemberMotion) share. Each
    /// movable body's hinge (pivot and axis as the child entity states them, in the stored model frame; the offset interval from
    /// the stored angle in EITHER sense, an unlimited hinge a full turn), its hinge path top-down, and the body each rig node
    /// moves with.</summary>
    internal sealed class HingeTree
    {
        public required MotionRig Rig { get; init; }
        public required List<(int Child, Vector3 Pivot, Vector3 Axis, double Lo, double Hi)> Hinges { get; init; }
        public required Dictionary<int, int> ParentOf { get; init; }
        public required Dictionary<int, List<int>> Paths { get; init; }
        public required Dictionary<NiAVObject, int> BodyOfNode { get; init; }

        /// <summary>The movable body a node moves with (the nearest body node on its chain), or -1.</summary>
        public int BodyOf(NiAVObject n) { foreach (var c in Rig.Chain(n)) if (BodyOfNode.TryGetValue(c, out int b)) return b; return -1; }

        /// <summary>A point turned about hinge h by angle a (stored model frame).</summary>
        public Vector3 Turn(int h, Vector3 p, double a)
        {
            var (_, piv, ax, _, _) = Hinges[h];
            var q = Quaternion.CreateFromAxisAngle(ax, (float)a);
            return Vector3.Transform(p - piv, q) + piv;
        }

        /// <summary>A stored model point moving with <paramref name="body"/> at the hinge angles (offsets from the stored pose).</summary>
        public Vector3 Pose(Vector3 p, int body, double[] ang)
        {
            if (body >= 0) { var path = Paths[body]; for (int k = path.Count - 1; k >= 0; k--) p = Turn(path[k], p, ang[path[k]]); }
            return p;
        }

        /// <summary>Lipschitz per hinge: max over points moving with a body under it of (distance to the last pivot on its path +
        /// the rigid distances between consecutive pivots from this hinge down).</summary>
        public double[] Lipschitz(IEnumerable<(Vector3 P, int Body)> points)
        {
            var L = new double[Hinges.Count];
            foreach (var (p, body) in points)
            {
                if (body < 0) continue;
                var path = Paths[body];
                double acc = Vector3.Distance(p, Hinges[path[^1]].Pivot);
                for (int k = path.Count - 1; k >= 0; k--)
                {
                    L[path[k]] = Math.Max(L[path[k]], acc);
                    if (k > 0) acc += Vector3.Distance(Hinges[path[k]].Pivot, Hinges[path[k - 1]].Pivot);
                }
            }
            return L;
        }
    }

    /// <summary>Read a pinned file's hinge tree, or the refusal (a free island, a loop, another constraint class, an axis-less
    /// hinge, no hinge).</summary>
    internal static (HingeTree? Tree, string? Refusal) HingeTreeOf(byte[] nif, MotionModel motion, Func<string, byte[]?>? read)
    {
        Model m;
        try { m = Read(nif); } catch (Exception ex) { return (null, "physics: unreadable mesh: " + ex.Message); }
        if (ReadRefusal(m) is { } why) return (null, why);
        var byId = m.Bodies.ToDictionary(b => b.Block);
        // tree: from each island's immovable bodies outward
        var parentHinge = new Dictionary<int, PhysicsConstraint>();
        var parentOf = new Dictionary<int, int>();
        foreach (var isl in Islands(m.Bodies, m.Constraints))
        {
            if (!isl.Any(i => byId[i].Movable)) continue;
            var roots = isl.Where(i => !byId[i].Movable).ToList();
            if (roots.Count == 0) return (null, "physics: a free island has no envelope");
            var seen = new HashSet<int>(roots); var q = new Queue<int>(roots);
            while (q.Count > 0)
            {
                int cur = q.Dequeue();
                foreach (var c in m.Constraints.Where(c => c.BodyA == cur || c.BodyB == cur))
                {
                    int other = c.BodyA == cur ? c.BodyB : c.BodyA;
                    if (seen.Contains(other))
                    {
                        if (parentOf.TryGetValue(cur, out var pc) && pc == other) continue;
                        if (parentOf.TryGetValue(other, out var po) && po == cur) continue;
                        return (null, "physics: a constraint loop (or two immovable bodies in one island) is not supported");
                    }
                    if (!byId[other].Movable) return (null, "physics: a constraint loop (or two immovable bodies in one island) is not supported");
                    if (c.Class is not ("bhkLimitedHingeConstraint" or "bhkHingeConstraint"))
                        return (null, $"physics: {c.Class} on a pinned body is not supported");
                    seen.Add(other); parentOf[other] = cur; parentHinge[other] = c; q.Enqueue(other);
                }
            }
        }
        var order = parentHinge.Keys.ToList();   // BFS order (children after parents)
        // per hinge (keyed by child body): pivot, axis, offset interval
        var hinges = new List<(int Child, Vector3 Pivot, Vector3 Axis, double Lo, double Hi)>();
        foreach (int child in order)
        {
            var c = parentHinge[child];
            bool childIsA = c.BodyA == child;
            var pivot = childIsA ? c.PivotAModel : c.PivotBModel;
            var axis = childIsA ? c.AxisAModel : c.AxisBModel;
            if (axis.LengthSquared() < 1e-12f) return (null, $"physics: {c.Class} has no axis");
            double lo, hi;
            if (c.MinAngle is { } mn && c.MaxAngle is { } mx)
            {
                double t0 = StoredAngle(m, c);
                // either sign convention: offsets [min − θ0, max − θ0] or [θ0 − max, θ0 − min]; and the stored pose itself
                lo = Math.Min(0, Math.Min(mn - t0, t0 - mx)); hi = Math.Max(0, Math.Max(mx - t0, t0 - mn));
            }
            else { lo = -Math.PI; hi = Math.PI; }
            hinges.Add((child, pivot, Vector3.Normalize(axis), lo, hi));
        }
        if (hinges.Count == 0) return (null, "physics: no hinge to sweep");
        var hingeOfChild = hinges.Select((h, i) => (h.Child, i)).ToDictionary(x => x.Child, x => x.i);
        // the hinge path (top-down) of each body
        List<int> PathOf(int body)
        {
            var p = new List<int>();
            for (int cur = body; parentOf.ContainsKey(cur); cur = parentOf[cur]) p.Add(hingeOfChild[cur]);
            p.Reverse(); return p;
        }
        var rig = NifMotion.RigOf(nif, motion, read);
        var bodyOfNode = new Dictionary<NiAVObject, int>(ReferenceEqualityComparer.Instance);
        foreach (var b in m.Bodies) if (m.Nodes.TryGetValue(b.Block, out var n0) && parentOf.ContainsKey(b.Block)) bodyOfNode[NodeIn(rig, n0)] = b.Block;
        var paths = new Dictionary<int, List<int>>();
        foreach (var b in parentOf.Keys) paths[b] = PathOf(b);
        return (new HingeTree { Rig = rig, Hinges = hinges, ParentOf = parentOf, Paths = paths, BodyOfNode = bodyOfNode }, null);
    }

    /// <summary>The world box holding every pose a pinned island's movable bodies can take within the file's constraint
    /// limits: render geometry (skinned by bone ∘ skin, rigid by the node chain) of every visible shape, each body's
    /// subtree turned about its hinge (axis and pivot as the child entity states them) through any angle in [min, max]
    /// measured from the stored angle in EITHER sense (the angle convention is not assumed), an unlimited hinge through a
    /// full turn. Certified to within <paramref name="delta"/> by branch and bound on each box face with a per-hinge
    /// Lipschitz bound (chord ≤ arc; distances to downstream pivots are rigid). Null reason = built.</summary>
    internal static (Aabb Box, string? Refusal, bool Converged) HingeEnvelope(byte[] nif, MotionModel motion, Vec3 position, Vec3 rotation, float scale,
                                                                              double delta, int budget, Func<string, byte[]?>? read)
    {
        var (tree, refusal) = HingeTreeOf(nif, motion, read);
        if (tree is null) return (Aabb.Empty, refusal, false);
        int H = tree.Hinges.Count;
        var rig = tree.Rig;
        // points: (stored model point, the body its bone moves with or -1), weighted per vertex
        var verts = new List<(Vector3 P, int Body, float W)[]>();
        foreach (var rs in rig.Shapes)
        {
            if (rs.Hidden || rs.Particle) continue;
            if (rs.Refusal is not null) return (Aabb.Empty, $"shape '{rs.Name}': {rs.Refusal}", false);
            if (rs.Weights is not null)
            {
                var models = rs.Bones!.Select(bn => rig.Model(bn, null, 0, false)).ToList();
                var bodyOfBone = rs.Bones!.Select(tree.BodyOf).ToList();
                // the skin rule keeps a second weight source (both are posed) and leaves undrawn vertices out
                foreach (var wset in rs.WeightsAlt is null ? new[] { rs.Weights } : new[] { rs.Weights, rs.WeightsAlt })
                    for (int k = 0; k < rs.Vertices.Count; k++)
                    {
                        float ws = wset[k].Sum(x => x.W);
                        if (rs.DrawnOnly && ws <= 0) continue;
                        verts.Add(wset[k].Select(x => (models[x.Bone].Apply(rs.Skin![x.Bone].Apply(rs.Vertices[k])), bodyOfBone[x.Bone], x.W / ws)).ToArray());
                    }
            }
            else
            {
                var xf = rig.Model((NiAVObject)rs.Shape, null, 0, false); int body = tree.BodyOf((NiAVObject)rs.Shape);
                foreach (var p in rs.Vertices) verts.Add(new[] { (xf.Apply(p), body, 1f) });
            }
        }
        if (verts.Count == 0) return (Aabb.Empty, "render: no visible triangles", false);
        var L = tree.Lipschitz(verts.SelectMany(v => v.Select(x => (x.P, x.Body))));
        var place = SpatialOverlap.RefrRotation(rotation); var pos = new Vector3(position.X, position.Y, position.Z);
        double sAbs = Math.Abs(scale);
        Vector3 PoseOf((Vector3 P, int Body, float W)[] v, double[] ang)
        {
            var acc = Vector3.Zero;
            foreach (var (p0, body, w) in v) acc += w * tree.Pose(p0, body, ang);
            return Vector3.Transform(acc * scale, place) + pos;
        }
        var hinges = tree.Hinges;
        var result = new double[6]; bool converged = true;
        for (int face = 0; face < 6; face++)
        {
            int axisIdx = face / 2; double sign = face % 2 == 0 ? 1 : -1;
            double F(double[] ang)
            {
                double best = double.NegativeInfinity;
                foreach (var v in verts) { var w = PoseOf(v, ang); best = Math.Max(best, sign * (axisIdx == 0 ? w.X : axisIdx == 1 ? w.Y : w.Z)); }
                return best;
            }
            var pq = new PriorityQueue<(double[] Lo, double[] Hi, double Ub), double>();
            double lower = double.NegativeInfinity;
            void Push(double[] lo, double[] hi)
            {
                var c = new double[H]; double slack = 0;
                for (int i = 0; i < H; i++) { c[i] = (lo[i] + hi[i]) / 2; slack += (hi[i] - lo[i]) / 2 * L[i]; }
                double f = F(c); lower = Math.Max(lower, f);
                double ub = f + slack * sAbs;
                pq.Enqueue((lo, hi, ub), -ub);
            }
            Push(hinges.Select(h => h.Lo).ToArray(), hinges.Select(h => h.Hi).ToArray());
            int evals = 1; double top = double.PositiveInfinity;
            while (pq.Count > 0)
            {
                var cell = pq.Peek(); top = cell.Ub;
                if (top - lower <= delta) break;
                if (evals + 2 > budget) { converged = false; break; }
                pq.Dequeue();
                int split = 0; double wmax = -1;
                for (int i = 0; i < H; i++) { double w = (cell.Hi[i] - cell.Lo[i]) * L[i]; if (w > wmax) { wmax = w; split = i; } }
                double mid = (cell.Lo[split] + cell.Hi[split]) / 2;
                var hiA = (double[])cell.Hi.Clone(); hiA[split] = mid;
                var loB = (double[])cell.Lo.Clone(); loB[split] = mid;
                Push(cell.Lo, hiA); Push(loB, cell.Hi); evals += 2;
            }
            result[face] = pq.Count > 0 ? pq.Peek().Ub : top;
        }
        float Up(double v) { float f = (float)v; return f < v ? MathF.BitIncrement(f) : f; }
        var box = new Aabb(new Vec3(-Up(result[1]), -Up(result[3]), -Up(result[5])), new Vec3(Up(result[0]), Up(result[2]), Up(result[4])), false);
        return (box, null, converged);
    }

    /// <summary>The same node in the motion rig's own parse (the rig was read from the same bytes): by name, else by block
    /// position.</summary>
    static NiAVObject NodeIn(MotionRig rig, NiAVObject n) =>
        n.Name?.String is { } nm && rig.NodeByName.TryGetValue(nm, out var r) ? r : n;

    /// <summary>The hinge's angle at the stored pose: B's zero direction measured from A's about A's axis (nif.xml: Perp Axis
    /// In A1 / B1 define the zero angle). Its sign convention does not matter to the envelope, which takes both.</summary>
    static double StoredAngle(Model m, PhysicsConstraint c)
    {
        var b = m.File.Blocks[c.Block];
        Vector4 pa, pb;
        if (b is bhkLimitedHingeConstraint lh) { pa = lh.Constraint.PerpAxisInA1; pb = lh.Constraint.PerpAxisInB1; }
        else if (b is bhkHingeConstraint hg) { pa = hg.Constraint.PerpAxisInA1; pb = hg.Constraint.PerpAxisInB1; }
        else return 0;
        var a1 = m.BodyDirection(c.BodyA, pa); var b1 = m.BodyDirection(c.BodyB, pb);
        var ax = c.AxisAModel;
        double y = Vector3.Dot(Vector3.Cross(a1, b1), ax), x = Vector3.Dot(a1, b1);
        return Math.Atan2(y, x);
    }
}
