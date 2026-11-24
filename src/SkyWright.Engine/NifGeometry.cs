using System.Numerics;
using System.Reflection;
using NiflySharp;
using NiflySharp.Blocks;
using NiflySharp.Structs;

using HousecarlCore;

namespace SkyWright.Engine;

// ======================================================================
//  NifGeometry — the GEOMETRY PRIMITIVE (geometry-deconfliction wave 1).
//
//  Deliberately steps PAST NifService's stated "data values only; it does not
//  read geometry" boundary — that line was a scope choice, not a limitation, and
//  this is the scope widening. Kept in its OWN file so the boundary stays legible:
//  NifService still answers "what does this mesh SAY", this answers "what SHAPE
//  does this mesh occupy".
//
//  Output is generic mesh measurement — vertex counts, triangle counts, a
//  model-space AABB with the node chain applied, and the collision volume when
//  one exists. No notion of "conflict" or "overlap"; that judgement is a skill.
//
//  WHY THIS EXISTS AT ALL — Piranha's standing rule, 2026-08-26:
//    "Do not consider OBND to be definitive; it is often stale compared to the
//     nif geometry, especially if the nif has been modded by a standalone mesh
//     replacer mod. Always validate against the real geometry when available."
//  A mesh replacer swaps the .nif through the MO2 VFS and never touches the
//  plugin, so the record's OBND keeps describing a mesh the game will not load.
//  Measured geometry is the ground truth; OBND is a labelled fallback.
//
//  API grounded by reflection probe against the pinned Nifly 1.1.0 before a line
//  was written. Notably: NifFile offers NO world-transform helper, so the parent
//  chain is walked here (GetParentNode) rather than assumed away.
// ======================================================================

/// <summary>An axis-aligned box. Empty (<see cref="IsEmpty"/>) until a point is added, so an accumulation over zero
/// vertices is DISTINGUISHABLE from a box at the origin — the difference between "this mesh has no geometry" and
/// "this mesh is a point at 0,0,0", which a naive zero-init conflates.</summary>
public readonly record struct Aabb(Vec3 Min, Vec3 Max, bool IsEmpty)
{
    public static readonly Aabb Empty = new(default, default, true);

    public Aabb Add(Vector3 p) => IsEmpty
        ? new Aabb(new Vec3(p.X, p.Y, p.Z), new Vec3(p.X, p.Y, p.Z), false)
        : new Aabb(new Vec3(MathF.Min(Min.X, p.X), MathF.Min(Min.Y, p.Y), MathF.Min(Min.Z, p.Z)),
                   new Vec3(MathF.Max(Max.X, p.X), MathF.Max(Max.Y, p.Y), MathF.Max(Max.Z, p.Z)), false);

    public Aabb Union(Aabb o)
    {
        if (o.IsEmpty) return this;
        if (IsEmpty) return o;
        return new Aabb(new Vec3(MathF.Min(Min.X, o.Min.X), MathF.Min(Min.Y, o.Min.Y), MathF.Min(Min.Z, o.Min.Z)),
                        new Vec3(MathF.Max(Max.X, o.Max.X), MathF.Max(Max.Y, o.Max.Y), MathF.Max(Max.Z, o.Max.Z)), false);
    }

    public Vec3 Size => IsEmpty ? default : new Vec3(Max.X - Min.X, Max.Y - Min.Y, Max.Z - Min.Z);
    public Vec3 Center => IsEmpty ? default : new Vec3((Min.X + Max.X) / 2, (Min.Y + Max.Y) / 2, (Min.Z + Max.Z) / 2);
    public override string ToString() => IsEmpty ? "<empty>" : $"{Min}..{Max}";
}

/// <summary>A rigid transform with uniform scale — a NIF node's local transform, and the composition of a chain of them.
/// Matches NIF semantics exactly: a point maps as <c>R * (s * p) + T</c>.</summary>
public readonly record struct NifTransform(Matrix4x4 Rotation, float Scale, Vector3 Translation)
{
    public static readonly NifTransform Identity = new(Matrix4x4.Identity, 1f, Vector3.Zero);

    public Vector3 Apply(Vector3 p) => Vector3.Transform(p * Scale, Rotation) + Translation;

    /// <summary>this ∘ child — apply <paramref name="child"/> first, then this. The order a parent chain composes in.</summary>
    public NifTransform Compose(NifTransform child) => new(
        child.Rotation * Rotation,
        Scale * child.Scale,
        Vector3.Transform(child.Translation * Scale, Rotation) + Translation);
}

/// <param name="BlockType">The concrete nifly block type ("BSTriShape", "NiTriShape", …) — reported, never branched on.</param>
/// <param name="Bounds">Model-space AABB of this shape with its full node chain applied.</param>
/// <param name="SphereRadius">The shape's OWN stated bounding sphere radius (free, no vertex walk) — for cross-checking
/// the measured box, and as the only signal when a shape's vertices could not be read.</param>
/// <param name="DoubleSided">The shape's shader draws both sides (SLSF2 Double_Sided): its back faces are not culled.</param>
/// <param name="Mirrored">The shape's node chain has a negative determinant, which flips its triangles' winding.</param>
/// <param name="Effect">The shape's shader is a BSEffectShaderProperty: an effect (cloud, glow, flame, editor marker), not a lit surface.</param>
public sealed record NifShapeGeometry(
    string Name,
    string BlockType,
    int VertexCount,
    int TriangleCount,
    Aabb Bounds,
    float? SphereRadius,
    bool VerticesRead,
    int BlockIndex = -1,
    NifShapeBound? StoredBound = null,
    string? BoundIssue = null,
    string? PoseIssue = null,
    bool Hidden = false,
    bool Lod = false,
    bool TrianglesRead = false,
    int InvalidTriangleCount = 0,
    bool DoubleSided = false,
    bool Mirrored = false,
    bool Effect = false);

/// <summary>The stored sphere, separately from the bounds of the vertices. Model coordinates
/// apply every below-root node transform, using the same placed-mesh root convention as the
/// vertex reader. Block indices identify the source chain even when shape names repeat.
/// This is a measurement of file data, not a claim that the sphere encloses its triangles.</summary>
public sealed record NifShapeBound(
    Vec3 StoredCenter,
    float StoredRadius,
    Vec3 ModelCenter,
    float ModelRadius,
    IReadOnlyList<int> AppliedNodeBlocks,
    int IgnoredRootBlock);

/// <param name="Bounds">Model-space AABB over every shape — the RENDER volume.</param>
/// <param name="HasCollision">The mesh carries at least one collision object — solid or not.</param>
/// <param name="CollisionBounds">Model-space AABB of the SOLID Havok collision volume (phantoms excluded), decoded by
/// <see cref="HavokCollision"/>, scaled to Skyrim units and carried through the node chain. Empty when the mesh has no
/// solid collision, which is itself meaningful: such an object blocks nothing.</param>
/// <param name="CollisionShapeTypes">Which bhk shape types were traversed — surfaced so an unhandled exotic shape is
/// visible rather than silently contributing nothing (Q3).</param>
/// <param name="CollisionUnread">Collision shapes reached but NOT decodable into triangles, by name. Non-empty means
/// <see cref="CollisionBounds"/> and <see cref="CollisionSoup"/> UNDERSTATE the real volume — never treat them as
/// authoritative then; <see cref="CollisionComplete"/> is the one-word form of that check.</param>
/// <param name="Soup">The whole mesh as one model-space triangle list, node transforms already applied — present only
/// when <c>includeTriangles</c> was asked for. This is what a NARROW phase needs: an AABB cannot tell a navmesh lying
/// ON a dirt mound from one buried inside a wall, because both sit inside the box.</param>
/// <param name="CollisionSoup">The decoded collision of EVERY body (phantoms included — <see cref="CollisionBodies"/>
/// says which index range is whose, so a caller can keep the layers it wants) as a model-space triangle list, present
/// only when <c>includeTriangles</c> was asked for. A test of whether an actor can pass the object runs on THIS, never
/// on <see cref="Soup"/>: a pine's render soup is its canopy, its collision is a trunk.</param>
/// <param name="CollisionBodies">One entry per collision body, in <see cref="CollisionSoup"/> order.</param>
/// <param name="CollisionStats">The decoder's counters — which branches a file exercised.</param>
/// <param name="SoupShapeOfTriangle">For each <see cref="Soup"/> triangle, the index into <see cref="Shapes"/> of the
/// shape it came from (present with <see cref="Soup"/>), so a caller can carry a per-shape property such as its
/// material onto the triangles.</param>
/// <param name="HavokUnitScale">Skyrim units per Havok unit as applied to this mesh.</param>
/// <param name="StreamVersion">The file's Bethesda stream version (100 = SSE, 83 = Skyrim LE, 34 = FO3).</param>
public sealed record NifGeometryModel(
    IReadOnlyList<NifShapeGeometry> Shapes,
    Aabb Bounds,
    int TotalVertices,
    int TotalTriangles,
    bool HasCollision,
    Aabb CollisionBounds,
    IReadOnlyList<string> CollisionShapeTypes,
    IReadOnlyList<string> CollisionUnread,
    TriangleSoup? Soup = null,
    TriangleSoup? CollisionSoup = null,
    IReadOnlyList<CollisionBodyInfo>? CollisionBodies = null,
    HavokDecodeStats? CollisionStats = null,
    float HavokUnitScale = 0f,
    uint StreamVersion = 0,
    IReadOnlyList<string>? RenderUnread = null,
    IReadOnlyList<int>? SoupShapeOfTriangle = null)
{
    /// <summary>Every parsed NiShape block was returned by Nifly's render-shape enumerator.</summary>
    public bool RenderComplete => RenderUnread is null || RenderUnread.Count == 0;
    /// <summary>At least one solid (non-phantom) body decoded to geometry.</summary>
    public bool HasSolidCollision => CollisionBodies is not null && CollisionBodies.Any(b => b.Solid && b.VertexCount > 0);
    /// <summary>Every solid body decoded without an unread shape — the collision soup can be trusted as the whole solid.</summary>
    public bool CollisionComplete => CollisionBodies is not null && CollisionBodies.All(b => !b.Solid || b.Unread.Count == 0);

    /// <summary>The collision soup restricted to the bodies <paramref name="keep"/> selects — the generic cut a caller
    /// makes to say "only the layers an actor meets" without this layer knowing what a layer means. Null when
    /// triangles were not asked for; an EMPTY soup (not null) when no body was kept, so "nothing solid" and "no
    /// soup" stay distinguishable.</summary>
    public TriangleSoup? CollisionSoupFor(Func<CollisionBodyInfo, bool> keep)
    {
        if (CollisionSoup is null || CollisionBodies is null) return null;
        var v = new List<Vector3>();
        var i = new List<int>();
        var materials = new List<string?>();
        var radii = new List<float>();
        foreach (var b in CollisionBodies)
        {
            if (!keep(b) || b.IndexCount == 0) continue;
            // Re-index this body's triangles onto a fresh vertex array (the vertices it touches, in first-use order).
            var map = new Dictionary<int, int>();
            for (int k = b.IndexStart; k < b.IndexStart + b.IndexCount; k++)
            {
                int old = CollisionSoup.Indices[k];
                if (!map.TryGetValue(old, out int n)) { n = v.Count; map[old] = n; v.Add(CollisionSoup.Vertices[old]); }
                i.Add(n);
                if (k % 3 == 0) materials.Add(CollisionSoup.TriangleMaterials?[k / 3]);
                if (k % 3 == 0) radii.Add(CollisionSoup.TriangleRadii is { } tr && k / 3 < tr.Length ? tr[k / 3] : 0f);
            }
        }
        return new TriangleSoup(v.ToArray(), i.ToArray(), materials.ToArray(), radii.ToArray());
    }
}

/// <summary>One collision body as it sits in <see cref="NifGeometryModel.CollisionSoup"/>.</summary>
/// <param name="Layer">The body's collision layer by the game's own name (SKYL_STATIC, SKYL_TREES, SKYL_NONCOLLIDABLE…).
/// Which layers an actor walks through is the caller's judgement; this only reports.</param>
/// <param name="Solid">False for a phantom — a trigger volume, never something to bump into.</param>
/// <param name="ChainIdentity">The node chain above the body was identity, so its geometry is the body's own frame
/// scaled — the meshes a unit-scale measurement can use without a NIF-unit translation mixed in.</param>
/// <param name="IndexStart">Offset into the soup's index array of this body's first triangle; <paramref name="IndexCount"/>
/// indices follow. Zero counts when triangles were not asked for.</param>
/// <param name="Bounds">This body's model-space AABB (scaled, chained), from its vertices.</param>
/// <param name="FrameTranslation">The body's own frame translation in HAVOK units (zero for a plain body) and whether
/// its rotation was anything but identity — reported so a run can say how often the T-body frame was exercised.</param>
/// <param name="MotionSystem">The rigid body's motion type by name (see <see cref="HavokBody.MotionSystem"/>) — whether the
/// NIF lets physics move this body. Whether the engine does also depends on the placing record's type; the caller judges.</param>
/// <param name="QualityType">The rigid body's quality type by name, beside the motion system.</param>
public sealed record CollisionBodyInfo(
    string BodyType, string Layer, bool Solid, bool HasTransform, Vector3 FrameTranslation, bool FrameRotated, bool ChainIdentity,
    int IndexStart, int IndexCount, int VertexCount,
    IReadOnlyList<string> ShapeTypes, IReadOnlyList<string> Unread, Aabb Bounds,
    string MotionSystem = "", string QualityType = "");

/// <summary>A flat triangle list: <paramref name="Vertices"/> in model space, <paramref name="Indices"/> in triples.
/// Deliberately arrays of primitives rather than objects — a load order's worth of meshes is millions of triangles,
/// and per-triangle allocation would not survive it.</summary>
/// <param name="TriangleMaterials">Optional material names aligned to triangle ordinals (not vertices). Null entries
/// are unknown; render soups have no collision-material stream. Preserve this array when transforming vertices.</param>
/// <param name="TriangleRadii">Optional Havok radius per triangle (aligned to triangle ordinals), in the soup's own units: the
/// shell distance the engine keeps around that triangle's shape (box, convex-vertices and compressed-mesh radius; 0 where the
/// shape has none or where the radius was already grown into the geometry). Null: none recorded (render soups, hand-built soups).
/// Carried through <see cref="NifGeometryModel.CollisionSoupFor"/> and <see cref="SpatialOverlap.TransformSoup"/> (scaled with the geometry).</param>
public sealed record TriangleSoup(Vector3[] Vertices, int[] Indices, string?[]? TriangleMaterials = null, float[]? TriangleRadii = null)
{
    public int TriangleCount => Indices.Length / 3;
    public static readonly TriangleSoup Empty = new(Array.Empty<Vector3>(), Array.Empty<int>());
}

public enum NifGeometryFailureKind { None, EmptyAsset, ParseError, InvalidTransform, MeasurementError }

public sealed record NifGeometryOutcome(
    NifGeometryModel? Model,
    string? Error,
    NifGeometryFailureKind FailureKind = NifGeometryFailureKind.None);

sealed class NifTransformException(string message) : Exception(message);

public static class NifGeometry
{
    /// <summary>Measure a mesh from its raw bytes. Mirrors <see cref="NifService.Inspect"/>'s contract: the model on
    /// success, or a NAMED error — never a partial model, never a silent empty result.</summary>
    public static NifGeometryOutcome Measure(byte[] bytes) => Measure(bytes, includeTriangles: false);

    /// <summary>As <see cref="Measure(byte[])"/>, but optionally also returns the full model-space triangle soups (render
    /// and collision) for a narrow-phase test. Costs memory proportional to the mesh, so it is opt-in.</summary>
    /// <param name="havokUnitScale">Skyrim units per Havok unit to decode the collision at. Null takes the file's era
    /// from its stream version (<see cref="HavokCollision.UnitScaleFor"/>); a measurement run passes 1 to read the
    /// collision in Havok's own units.</param>
    /// <param name="collisionRadius">Decode the collision with every Havok radius grown into the geometry (box, convex-vertices,
    /// compressed mesh; <see cref="HavokCollision.Decode"/>'s <c>convexRadius</c>). Off by default: route, floors, overlap and every
    /// other consumer keep the core surface.</param>
    public static NifGeometryOutcome Measure(byte[] bytes, bool includeTriangles, float? havokUnitScale = null, bool collisionRadius = false)
        => MeasureCore(bytes, includeTriangles, havokUnitScale, excludeStaticallyHidden: false,
                       validateRenderTransforms: false, collisionRadius: collisionRadius);

    /// <summary>Measure render geometry while excluding shapes hidden by bit zero on the shape or an ancestor.
    /// This explicit policy entry point leaves the longstanding raw <see cref="Measure(byte[], bool, float?)"/>
    /// behavior unchanged for route, contact, inspection and other consumers.</summary>
    public static NifGeometryOutcome MeasureVisibleRender(byte[] bytes, bool includeTriangles = false)
        => MeasureCore(bytes, includeTriangles, havokUnitScale: null, excludeStaticallyHidden: true,
                       validateRenderTransforms: true);

    static NifGeometryOutcome MeasureCore(byte[] bytes, bool includeTriangles, float? havokUnitScale,
                                          bool excludeStaticallyHidden, bool validateRenderTransforms, bool collisionRadius = false)
    {
        if (bytes is null || bytes.Length == 0)
            return new NifGeometryOutcome(null, "the mesh is empty (0 bytes) — nothing to measure.",
                                          NifGeometryFailureKind.EmptyAsset);

        var nif = new NifFile();
        try
        {
            using var ms = new MemoryStream(bytes, writable: false);
            int rc = nif.Load(ms);
            if (rc != 0)
                return new NifGeometryOutcome(null,
                    $"NiflySharp could not parse this mesh (Load returned {rc}) — it may be truncated, not a NIF, or a " +
                    "format the library rejects.", NifGeometryFailureKind.ParseError);
        }
        catch (Exception ex)
        {
            return new NifGeometryOutcome(null, $"NiflySharp threw while parsing this mesh — {ex.GetType().Name}: {ex.Message}",
                                          NifGeometryFailureKind.ParseError);
        }

        try { return new NifGeometryOutcome(Build(nif, includeTriangles, havokUnitScale, excludeStaticallyHidden,
                                                  validateRenderTransforms, collisionRadius), null); }
        catch (NifTransformException ex)
        {
            return new NifGeometryOutcome(null, $"the mesh has an invalid render transform — {ex.Message}",
                                          NifGeometryFailureKind.InvalidTransform);
        }
        catch (Exception ex)
        {
            return new NifGeometryOutcome(null, $"the mesh parsed but measuring its geometry failed — {ex.GetType().Name}: {ex.Message}",
                                          NifGeometryFailureKind.MeasurementError);
        }
    }

    static NifGeometryModel Build(NifFile nif, bool includeTriangles, float? havokUnitScale,
                                  bool excludeStaticallyHidden, bool validateRenderTransforms, bool collisionRadius = false)
    {
        var shapes = new List<NifShapeGeometry>();
        var total = Aabb.Empty;
        int verts = 0, tris = 0;
        List<Vector3>? soupV = includeTriangles ? new() : null;
        List<int>? soupI = includeTriangles ? new() : null;
        List<int>? soupShape = includeTriangles ? new() : null;
        var blockIndices = new Dictionary<object, int>(ReferenceEqualityComparer.Instance);
        for (int i = 0; i < nif.Blocks.Count; i++) blockIndices[nif.Blocks[i]] = i;

        var enumeratedShapes = nif.GetShapes().ToList();
        var enumeratedSet = new HashSet<object>(enumeratedShapes.Cast<object>(), ReferenceEqualityComparer.Instance);
        var renderUnread = nif.Blocks.Where(IsParsedRenderShape).Where(s => !enumeratedSet.Contains(s))
            .Select(s => $"{s.GetType().Name} block {blockIndices.GetValueOrDefault(s, -1)} was not returned by NifFile.GetShapes")
            .ToList();
        // A behaviour graph (BSBehaviorGraphExtraData naming an .hkx) can drive any node of the file at runtime with no
        // embedded controller to show it, so no shape's stored pose establishes its live pose.
        var behaviourGraph = nif.Blocks.OfType<BSBehaviorGraphExtraData>().Select(b => b.BehaviourGraphFile?.ToString() ?? "")
                                .FirstOrDefault();
        foreach (var shape in enumeratedShapes)
        {
            var av = shape as NiAVObject;
            var xf = WorldTransform(nif, av, validateRenderTransforms);
            var chain = NodeChain(nif, av);
            bool hidden = chain.Any(n => ((n.Flags_ui | n.Flags_us) & 1) != 0);
            bool lod = chain.Any(n => n.GetType().Name.Contains("LOD", StringComparison.OrdinalIgnoreCase));
            var box = Aabb.Empty;

            var positions = TryGetVertexPositions(nif, shape);
            if (positions is not null)
            {
                int baseIdx = soupV?.Count ?? 0;
                foreach (var v in positions)
                {
                    var w = xf.Apply(v);
                    box = box.Add(w);
                    if (!excludeStaticallyHidden || !hidden) soupV?.Add(w);
                }
                if (soupI is not null && (!excludeStaticallyHidden || !hidden))
                {
                    // Indices are per-shape, so they must be rebased onto the shared vertex array; a triangle that
                    // points outside its own shape's vertex range is DROPPED rather than trusted, since a bad index
                    // here would silently place a triangle at another shape's geometry.
                    List<NiflySharp.Structs.Triangle>? t = null;
                    try { t = shape.Triangles; } catch { }
                    if (t is not null)
                        foreach (var tri in t)
                        {
                            if (tri.V1 >= positions.Count || tri.V2 >= positions.Count || tri.V3 >= positions.Count) continue;
                            soupShape!.Add(shapes.Count);
                            soupI.Add(baseIdx + tri.V1);
                            soupI.Add(baseIdx + tri.V2);
                            soupI.Add(baseIdx + tri.V3);
                        }
                }
            }

            float? radius = null;
            try { radius = shape.Bounds.Radius; } catch { /* a shape need not carry bounds */ }
            NifShapeBound? bound = null;
            string? boundIssue;
            try
            {
                var stored = shape.Bounds;
                bound = ReadStoredBound(nif, shape as NiAVObject, stored.Center, stored.Radius,
                                        blockIndices, out boundIssue);
            }
            catch (Exception ex) { boundIssue = $"stored sphere unreadable: {ex.GetType().Name}: {ex.Message}"; }

            int vc = 0, tc = 0;
            try { vc = (int)shape.VertexCount; } catch { }
            try { tc = (int)shape.TriangleCount; } catch { }

            string? poseIssue = shape.IsSkinned || shape.HasSkinInstance ? "skinned shape: live pose and bounds unsupported" : null;
            if (chain.Any(n => n.Controller?.Index >= 0))
                poseIssue = "controller on shape or node chain: live pose and bounds unsupported";
            if (chain.Any(n => n.GetType().Name.Contains("SwitchNode", StringComparison.OrdinalIgnoreCase)))
                poseIssue = "switch node on shape chain: active render child is runtime state and unsupported";
            if (poseIssue is null && behaviourGraph is not null)
                poseIssue = $"behaviour graph '{behaviourGraph}' in the file: live pose and bounds unsupported";
            bool trianglesRead = false;
            int invalidTriangles = 0;
            if (includeTriangles)
            {
                try
                {
                    var shapeTriangles = shape.Triangles;
                    trianglesRead = shapeTriangles is not null;
                    if (shapeTriangles is not null && positions is not null)
                        invalidTriangles = shapeTriangles.Count(t => t.V1 >= positions.Count || t.V2 >= positions.Count || t.V3 >= positions.Count);
                }
                catch { }
            }
            shapes.Add(new NifShapeGeometry(shape.Name?.String ?? "", shape.GetType().Name,
                                            vc, tc, box, radius, positions is not null,
                                            blockIndices.GetValueOrDefault(shape, -1), bound, boundIssue, poseIssue,
                                            hidden, lod, trianglesRead, invalidTriangles,
                                            IsDoubleSided(nif.GetShader(shape)),
                                            xf.Rotation.GetDeterminant() * xf.Scale * xf.Scale * xf.Scale < 0f,
                                            nif.GetShader(shape) is BSEffectShaderProperty));
            if (!excludeStaticallyHidden || !hidden)
            {
                total = total.Union(box);
                verts += vc; tris += tc;
            }
        }

        var soup = soupV is null ? null : new TriangleSoup(soupV.ToArray(), soupI!.ToArray());

        // ---- collision: decoded in Havok units by HavokCollision, brought into model space here ----
        // Unit scale first, then the node chain — the chain's translation is in NIF units, so the order is not
        // optional. Every body is kept (phantoms too) and labelled; only SOLID bodies feed CollisionBounds.
        var havok = HavokCollision.Decode(nif, includeTriangles, convexRadius: collisionRadius);
        float unit = havokUnitScale ?? HavokCollision.UnitScaleFor(havok.StreamVersion);
        List<Vector3>? cv = includeTriangles ? new() : null;
        List<int>? ci = includeTriangles ? new() : null;
        List<string?>? cm = includeTriangles ? new() : null;
        List<float>? cr = includeTriangles ? new() : null;   // per-triangle Havok radius, Skyrim units
        var bodies = new List<CollisionBodyInfo>(havok.Bodies.Count);
        var cbox = Aabb.Empty;
        var types = new List<string>();
        var unread = new List<string>();
        foreach (var b in havok.Bodies)
        {
            var chain = b.Node is null ? NifTransform.Identity : WorldTransform(nif, b.Node);
            var bb = Aabb.Empty;
            int start = ci?.Count ?? 0, baseIdx = cv?.Count ?? 0;
            foreach (var p in b.Vertices)
            {
                var w = chain.Apply(p * unit);
                bb = bb.Add(w);
                cv?.Add(w);
            }
            if (ci is not null) foreach (var k in b.Indices) ci.Add(baseIdx + k);
            if (cm is not null) cm.AddRange(b.TriangleMaterials ?? new string?[b.Indices.Length / 3]);
            if (cr is not null) foreach (var r in b.TriangleRadii ?? new float[b.Indices.Length / 3]) cr.Add(r * unit * chain.Scale);
            types.Add(b.BodyType);
            types.AddRange(b.ShapeTypes);
            unread.AddRange(b.Unread);
            if (b.Solid) cbox = cbox.Union(bb);
            bodies.Add(new CollisionBodyInfo(b.BodyType, b.Layer, b.Solid, b.HasTransform, b.FrameTranslation, b.FrameRotated, IsIdentity(chain),
                                             start, (ci?.Count ?? 0) - start, b.Vertices.Length, b.ShapeTypes, b.Unread, bb, b.MotionSystem, b.QualityType));
        }
        var csoup = cv is null ? null : new TriangleSoup(cv.ToArray(), ci!.ToArray(), cm!.ToArray(), cr!.ToArray());

        return new NifGeometryModel(shapes, total, verts, tris,
                                    havok.Stats.CollisionObjects > 0, cbox, types.Distinct().ToList(), unread.Distinct().ToList(),
                                    soup, csoup, bodies, havok.Stats, unit, havok.StreamVersion, renderUnread, soupShape);
    }

    static bool IsIdentity(NifTransform t) =>
        MathF.Abs(t.Scale - 1f) < 1e-5f && t.Translation.LengthSquared() < 1e-8f
        && MathF.Abs(t.Rotation.M11 - 1) < 1e-5f && MathF.Abs(t.Rotation.M22 - 1) < 1e-5f && MathF.Abs(t.Rotation.M33 - 1) < 1e-5f
        && MathF.Abs(t.Rotation.M12) < 1e-5f && MathF.Abs(t.Rotation.M13) < 1e-5f && MathF.Abs(t.Rotation.M21) < 1e-5f
        && MathF.Abs(t.Rotation.M23) < 1e-5f && MathF.Abs(t.Rotation.M31) < 1e-5f && MathF.Abs(t.Rotation.M32) < 1e-5f;

    // The managed wrapper does not publicly project its generated NiShape base type into NiflySharp.Blocks, although
    // concrete shapes are public there. Walk the parsed runtime inheritance chain so every present subtype is checked
    // against GetShapes without maintaining a hand-written list of render block names.
    static bool IsParsedRenderShape(object block)
    {
        for (Type? t = block.GetType(); t is not null; t = t.BaseType)
            if (t.Name == "NiShape") return true;
        return false;
    }

    // ---- node chain -------------------------------------------------------

    static NifShapeBound? ReadStoredBound(NifFile nif, NiAVObject? av, Vector3 center, float radius,
                                         IReadOnlyDictionary<object, int> indices, out string? issue)
    {
        issue = null;
        if (av is null) { issue = "shape has no node transform"; return null; }
        if (!Finite(center) || !float.IsFinite(radius) || radius < 0)
        { issue = "stored sphere is non-finite or has negative radius"; return null; }
        var chain = new List<NiAVObject>();
        var seen = new HashSet<NiAVObject>(ReferenceEqualityComparer.Instance);
        for (var cur = av; cur is not null; cur = nif.GetParentNode(cur))
        {
            if (chain.Count >= 256 || !seen.Add(cur))
            { issue = "node chain is cyclic or exceeds 256 nodes"; return null; }
            chain.Add(cur);
        }
        var xf = NifTransform.Identity;
        var applied = new List<int>();
        for (int i = chain.Count - 2; i >= 0; i--)
        {
            var node = chain[i];
            var rotation = ToMatrix(node.Rotation);
            var x = Vector3.TransformNormal(Vector3.UnitX, rotation);
            var y = Vector3.TransformNormal(Vector3.UnitY, rotation);
            var z = Vector3.TransformNormal(Vector3.UnitZ, rotation);
            if (!float.IsFinite(node.Scale) || node.Scale == 0 || !Finite(node.Translation)
                || !Finite(x) || !Finite(y) || !Finite(z)
                || MathF.Abs(x.LengthSquared() - 1) > 1e-3f
                || MathF.Abs(y.LengthSquared() - 1) > 1e-3f
                || MathF.Abs(z.LengthSquared() - 1) > 1e-3f
                || MathF.Abs(Vector3.Dot(x, y)) > 1e-3f
                || MathF.Abs(Vector3.Dot(x, z)) > 1e-3f
                || MathF.Abs(Vector3.Dot(y, z)) > 1e-3f)
            { issue = "node transform is non-finite, singular, or not uniformly scaled rigid geometry"; return null; }
            xf = xf.Compose(new NifTransform(rotation, node.Scale, node.Translation));
            applied.Add(indices.GetValueOrDefault(node, -1));
        }
        var modelCenter = xf.Apply(center);
        float modelRadius = radius * MathF.Abs(xf.Scale);
        if (!Finite(modelCenter) || !float.IsFinite(modelRadius))
        { issue = "composed stored sphere is non-finite"; return null; }
        return new(new(center.X, center.Y, center.Z), radius,
                   new(modelCenter.X, modelCenter.Y, modelCenter.Z), modelRadius,
                   applied, indices.GetValueOrDefault(chain[^1], -1));
    }

    static bool Finite(Vector3 v) => float.IsFinite(v.X) && float.IsFinite(v.Y) && float.IsFinite(v.Z);

    /// <summary>The shader draws both sides of the shape (SLSF2 Double_Sided), so its back faces are not culled.</summary>
    static bool IsDoubleSided(INiShader? shader) => shader switch
    {
        BSLightingShaderProperty l => l.ShaderFlags_SSPF2.HasFlag(NiflySharp.Enums.SkyrimShaderPropertyFlags2.Double_Sided),
        BSEffectShaderProperty e => e.ShaderFlags_SSPF2.HasFlag(NiflySharp.Enums.SkyrimShaderPropertyFlags2.Double_Sided),
        _ => false,
    };

    /// <summary>The accumulated transform from the NIF root down to <paramref name="av"/>. NifFile exposes no helper
    /// for this (verified by reflection probe against Nifly 1.1.0), so the parent chain is walked explicitly. A shape's
    /// own Translation/Rotation/Scale is LOCAL TO ITS PARENT — using it raw would put every shape in a multi-node mesh
    /// at the wrong place, and silently so, since the numbers stay plausible.</summary>
    internal static NifTransform WorldTransform(NifFile nif, NiAVObject? av, bool validate = false)
    {
        if (av is null) return NifTransform.Identity;

        // Root-first so Compose() applies parents outermost — and depth-capped, because a malformed NIF can present a
        // parent cycle and an uncapped walk would hang rather than fail (Q3: refuse loudly, never spin).
        var chain = NodeChain(nif, av);

        // The ROOT's own local transform is LEFT OUT (chain[^1] is the node with no parent — every top-level root's
        // transform is identity for this purpose); everything below it composes as before. The engine places a mesh by the
        // reference and discards the root node's transform, and the exporter leaves a quarter turn on many kit roots — 299
        // vanilla placed meshes (Solitude architecture 175, Whiterun 43). Measured 2026-09-05 (Q26-b / Q28): the Creation
        // Kit's baked ObjectBounds carry no root turn (Solitude statics: root-turned pieces 55 of 64 extents SWAPPED and 78
        // of 110 centres off against this routine WITH the root applied; the unturned controls 139 of 172 centres matching;
        // below-root turns 12 of 15 matching — the sense below the root is right), and in the game a commanded walker
        // crossed the line this routine's root-turned copy of a Solitude wall cut across a navmesh corridor 9 times of 9
        // in three cells and two mesh families (SMdEWalSol02/05, SMdAWallSol01), no stall, no climb, while the CK's copy
        // sat along the room's edge (runs/2026-09-05-root-transform-in-game). Render and collision share this routine, so
        // viewprobe and every detector see the fix alike; the proof is proofprobe's nif-chain case.
        var xf = NifTransform.Identity;
        for (int i = chain.Count - 2; i >= 0; i--)
            xf = xf.Compose(validate ? LocalChecked(chain[i]) : Local(chain[i]));
        return xf;
    }

    // Legacy/raw consumers historically treated the NIF default zero scale as identity. Preserve that behavior here;
    // the explicit strict overlap path uses LocalChecked and retains zero as measurable degenerate geometry.
    static NifTransform Local(NiAVObject av) => new(ToMatrix(av.Rotation), av.Scale == 0f ? 1f : av.Scale, av.Translation);

    static IReadOnlyList<NiAVObject> NodeChain(NifFile nif, NiAVObject? av)
    {
        if (av is null) return Array.Empty<NiAVObject>();
        var chain = new List<NiAVObject>();
        var seen = new HashSet<NiAVObject>(ReferenceEqualityComparer.Instance);
        var cur = av;
        while (cur is not null)
        {
            if (chain.Count >= 256 || !seen.Add(cur))
                throw new NifTransformException("a render node chain is cyclic or exceeds 256 nodes");
            chain.Add(cur);
            cur = nif.GetParentNode(cur);
        }
        return chain;
    }

    static NifTransform LocalChecked(NiAVObject av)
    {
        var rotation = ToMatrix(av.Rotation);
        var x = Vector3.TransformNormal(Vector3.UnitX, rotation);
        var y = Vector3.TransformNormal(Vector3.UnitY, rotation);
        var z = Vector3.TransformNormal(Vector3.UnitZ, rotation);
        if (!float.IsFinite(av.Scale) || !Finite(av.Translation) || !Finite(x) || !Finite(y) || !Finite(z))
            throw new NifTransformException("a render node transform is non-finite or not uniformly scaled rigid geometry");
        // A uniform factor s stored IN the rotation matrix (rows mutually orthogonal and of one length s; lane 3b, gap a) is
        // applied as given: the factor moves into the scale and the rotation is left orthonormal, which is the same product.
        // A mirrored factor (negative determinant) leaves an improper R, as the orthonormal path already accepts one: the product
        // is the same either way, so the sign is not split out. Rows of unequal length, or not orthogonal, are not one factor
        // times a rotation and still refuse.
        if (MathF.Abs(x.LengthSquared() - 1) <= 1e-3f && MathF.Abs(y.LengthSquared() - 1) <= 1e-3f && MathF.Abs(z.LengthSquared() - 1) <= 1e-3f
            && MathF.Abs(Vector3.Dot(x, y)) <= 1e-3f && MathF.Abs(Vector3.Dot(x, z)) <= 1e-3f && MathF.Abs(Vector3.Dot(y, z)) <= 1e-3f)
            // The orthonormal case, exactly as before. Zero and negative uniform scales are finite stored transforms. The strict
            // overlap policy reports the former as degenerate after vertex measurement and supports the latter as a mirror.
            return new NifTransform(rotation, av.Scale, av.Translation);
        double sx = Math.Sqrt(x.LengthSquared()), sy = Math.Sqrt(y.LengthSquared()), sz = Math.Sqrt(z.LengthSquared());
        double s = (sx + sy + sz) / 3;
        // the same tolerances as the orthonormal test, relative to the factor
        if (!(s > 1e-6)
            || Math.Abs(sx * sx / (s * s) - 1) > 1e-3 || Math.Abs(sy * sy / (s * s) - 1) > 1e-3 || Math.Abs(sz * sz / (s * s) - 1) > 1e-3
            || Math.Abs(Vector3.Dot(x, y)) / (s * s) > 1e-3
            || Math.Abs(Vector3.Dot(x, z)) / (s * s) > 1e-3
            || Math.Abs(Vector3.Dot(y, z)) / (s * s) > 1e-3)
            throw new NifTransformException("a render node transform is non-finite or not uniformly scaled rigid geometry");
        float f = (float)s;
        var r = rotation;
        r.M11 /= f; r.M12 /= f; r.M13 /= f; r.M21 /= f; r.M22 /= f; r.M23 /= f; r.M31 /= f; r.M32 /= f; r.M33 /= f;
        return new NifTransform(r, av.Scale * f, av.Translation);
    }

    /// <summary>nifly's Matrix33 -> System.Numerics.Matrix4x4 in the sense the ENGINE reads a node's rotation: the
    /// stored rows act on a row vector (<see cref="Vector3.Transform"/>'s convention), so M11 M12 M13 is the first row of
    /// the matrix as laid out here. Until 2026-09-05 this was the transpose, on the belief that the stored matrix acts on
    /// a column vector — and the two readings produce mirrored geometry that still LOOKS like a plausible box, which is
    /// why it went unnoticed through every floor up to Q26. Decided by measurement (Q28, runs/2026-09-05-root-transform-
    /// in-game/ckbounds-sense0.txt vs ckbounds-sense1.txt, sense-check.txt): against the Creation Kit's baked ObjectBounds
    /// of 10,834 spatial meshes, this sense puts the centres off for 5 % of unturned meshes, 1 % of root-turned and 12 %
    /// of below-root ones (the transpose: 8 %, 12 %, 28 %); within a mesh, the collision of 38 pine and aspen trunks moves
    /// from a tube along the ground onto the render's upright trunk (0.6 x 0.6 x 18.4 Havok u standing on the base) and
    /// 72 T-body compound buildings' collision moves onto their render, none worsened; and in the game the trunk IS
    /// upright (a walker crossed the tube's line 9 of 9 and was pushed out of the trunk at its base). The Q26-b reading
    /// that the plain rigid body's quaternion stood the trunk up was this sense error seen from the other side: that
    /// quaternion is the node's world rotation copied by the exporter, informational, ignored by the engine.
    /// bhkTransformShape's Matrix44 (HavokCollision.FromMatrix44) keeps its own layout and flag — not re-measured here.</summary>
    static Matrix4x4 ToMatrix(Matrix33 m) => new(
        m.M11, m.M12, m.M13, 0,
        m.M21, m.M22, m.M23, 0,
        m.M31, m.M32, m.M33, 0,
        0, 0, 0, 1);

    // ---- vertices ---------------------------------------------------------

    // concrete type -> the property that holds its Vector3 vertex list, resolved once per type.
    static readonly System.Collections.Concurrent.ConcurrentDictionary<Type, PropertyInfo?> VertProp = new();

    /// <summary>A shape's vertex positions, found by REFLECTION over the library's model rather than a switch on the
    /// concrete block type. BSTriShape and friends expose <c>VertexPositions</c> directly; the older NiTriShape family
    /// keeps them on its GeometryData as <c>Vertices</c>. Looking for the PROPERTY means a vertex-bearing type nifly
    /// gains later needs no code here. Null when neither shape nor data exposes a readable list — reported by the
    /// caller as <c>VerticesRead: false</c>, never silently treated as an empty mesh.</summary>
    static List<Vector3>? TryGetVertexPositions(NifFile nif, INiShape shape)
    {
        if (Read(shape) is { } direct) return direct;
        try { if (shape.GeometryData is { } data && Read(data) is { } viaData) return viaData; } catch { }
        return null;

        static List<Vector3>? Read(object o)
        {
            var p = VertProp.GetOrAdd(o.GetType(), static t =>
            {
                var found = t.GetProperty("VertexPositions", BindingFlags.Public | BindingFlags.Instance)
                    ?? t.GetProperty("Vertices", BindingFlags.Public | BindingFlags.Instance);
                return found is not null && found.PropertyType != typeof(List<Vector3>) ? null : found;
            });
            if (p is null) return null;
            try { return p.GetValue(o) as List<Vector3>; } catch { return null; }
        }
    }

    // ---- collision: see HavokCollision.cs ---------------------------------------
    // The decoder lives in its own file. Until 2026-09-02 this section walked the bhk tree for an AABB only and
    // reported every chunked bhkCompressedMeshShape as unread; the roadmap-4.7 handoff made the full decode a
    // prerequisite, and the unit-scale caveat that kept the body transform unapplied is answered by measurement there.
}
