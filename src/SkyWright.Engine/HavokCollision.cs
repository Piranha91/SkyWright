using System.Numerics;
using System.Reflection;
using NiflySharp;
using NiflySharp.Blocks;
using NiflySharp.Structs;

using HousecarlCore;

namespace SkyWright.Engine;

// ======================================================================
//  HavokCollision — the COLLISION half of the geometry primitive.
//
//  NifGeometry measures what a mesh LOOKS like: the render triangle soup.
//  This decodes what a mesh IS to the physics engine: every bhk* shape hung
//  off the mesh's collision objects, as a second triangle soup. The two are
//  different questions — a pine's render soup is its canopy, its collision
//  is a trunk capsule; a harvestable flower has a render soup and no solid
//  collision at all — and a test of whether an actor can pass an object has
//  to run on the second, never the first.
//
//  Kept in its own file so the boundary stays legible: NifGeometry answers
//  "what SHAPE does this mesh occupy", this answers "what SOLID does the
//  engine collide with". NifGeometry composes the two into one model.
//
//  Units. Havok stores its geometry in its own unit (metres); Skyrim world
//  units are ~70 to the metre. Everything this file produces is in HAVOK
//  units, in the frame of the NIF node the collision object hangs off.
//  NifGeometry applies the unit scale and the node chain. The scale factor
//  is NOT trusted from reputation (the widely cited 69.99): collprobe
//  measures it against the render geometry of the same meshes, and the
//  measured value with its spread is what SkyrimUnitsPerHavokUnit carries.
//
//  Format grounding (2026-09-02), read before a line was written:
//    * niftools/nifxml `nif.xml`: bhkCMSChunk ("Vertex positions in havok
//      coordinates*1000"; Strips = "Length of strips longer than one
//      triangle"), bhkQsTransform, bhkCMSBigTri, bhkCompressedMeshShapeData
//      (Error = "Quantization error", default 0.001; AABB = hkAabb), the
//      convex shapes (bhkBoxShape Dimensions = half extents; the convex
//      hull's Normals = exterior half spaces with w = −n·v), bhkRigidBody
//      ("a normal bhkRigidBody ignores" its translation/rotation; the info
//      variant is bhkRigidBodyCInfo2010 for Skyrim, 550_660 before FO3,
//      2014 for FO4).
//    * niftools/nifskope `src/gl/gltools.cpp` drawCMS: chunk vertex =
//      (chunk Translation + transform Translation + quantized/1000), the
//      transform Rotation applied to the sum; strips first (a strip of n
//      indices is n−2 triangles), the remaining indices as a plain list;
//      Big Tris index Big Verts directly. NifSkope also scales every Havok
//      shape by 7 × 10 = 70 for Skyrim-era files (glnode.cpp).
//    * the Nifly 1.1.0 API surface, by reflection probe (niflyprobe
//      collision) — property and struct names below are the probe's.
//
//  One deliberate divergence from NifSkope: a chunk's transform is applied
//  as R·v + T (Havok's hkQsTransform semantics) rather than R·(v + T). The
//  two agree wherever the rotation is identity, and ChunksWithRotation
//  counts how often it is not, so the sweep can say whether the choice was
//  ever exercised.
// ======================================================================

/// <summary>What the decoder met on the way through a file — the counters that let a corpus sweep say
/// which branches were exercised and which never were, so an untested branch is named rather than
/// assumed to work.</summary>
public sealed class HavokDecodeStats
{
    public int CollisionObjects, Phantoms, Unattached, BodiesWithTransform, BodiesPlain;
    public int Chunks, ChunkTriangles, BigTriangles, ChunksWithRotation, ChunksReferencing, ChunksReferenceBroken;
    public int CompressedShapes, CompressedNonUnitScale, CompressedZeroScale, MoppNonUnitScale, PackedNonUnitScale;
    public int TransformShapes, TransformTranslationInColumn, TransformTranslationInRow, TransformTranslationAmbiguous, TransformRotated;
    public int ConvexShapes, ConvexPlanesDropped, ConvexWithoutNormals;
    /// <summary>Convex-vertices shapes whose faces were rebuilt from their vertices because the stored planes dropped a face
    /// (ConvexHull.FromVertices).</summary>
    public int ConvexRebuiltFromVertices;
    /// <summary>Convex-vertices shapes whose every stored plane lies between the hull's support and that support pushed out by
    /// the convex radius (ConvexPlanesMatchHull), and those with a plane outside that window (ConvexPlanesMismatch, the first
    /// 64 named by shape in ConvexPlanesMismatchShapes). Faces come from the vertices either way.</summary>
    public int ConvexPlanesMatchHull, ConvexPlanesMismatch;
    public List<string> ConvexPlanesMismatchShapes = new();
    /// <summary>Convex-vertices shapes read as their hull grown by a non-zero convex radius (ConvexHull.Offset; only when the
    /// caller asked for the radius).</summary>
    public int ConvexRadiusApplied;
    /// <summary>Box shapes read grown by their radius, and compressed meshes read as a slab of their mesh radius (only with the
    /// radius on).</summary>
    public int BoxRadiusApplied, MeshRadiusApplied;
    /// <summary>Which sign convention the stored half-space w follows, counted per plane against the hull's own support
    /// value: Minus means w = −n·v (nif.xml's wording), Plus means w = +n·v, Neither means the stored w matched no vertex —
    /// the faces are recovered from the support rule regardless, so this is knowledge, not a dependency.</summary>
    public int ConvexPlaneSignMinus, ConvexPlaneSignPlus, ConvexPlaneSignNeither;
    /// <summary>...and the same test with the shape's convex radius folded in (w = −n·v − r): Havok's planes may
    /// describe the radius-expanded shell rather than the core vertices.</summary>
    public int ConvexPlaneSignMinusRadius;
    public int ConvexVertices, ConvexShapesWithDroppedPlanes;
    public int Boxes, Spheres, Capsules, Cylinders;
    /// <summary>The compressed-mesh self-check: the file states an AABB for every compressed shape, and the decoded
    /// vertices must lie inside it. The largest amount by which any decoded vertex overshot the stated box, in Havok
    /// units — a decode that puts chunks in the wrong place shows up here before it shows up anywhere else.</summary>
    public float StatedAabbMaxExcess;
    /// <summary>...and the smallest per-axis fraction of the stated box the decoded box covered — a decode that
    /// silently drops chunks lands inside the box and is caught only by this.</summary>
    public float StatedAabbMinCoverage = float.NaN;
    public int StatedAabbViolations;
}

/// <summary>One rigid body's decoded geometry, in HAVOK units, in the frame of the NIF node its collision object
/// hangs off (<see cref="Node"/>; null when the collision object was attached to no node at all).</summary>
/// <param name="Layer">The body's collision layer, by the game's own name (SKYL_STATIC, SKYL_TREES, SKYL_NONCOLLIDABLE
/// …). Which layers an actor can walk through is a JUDGEMENT and belongs to the caller; this only reports.</param>
/// <param name="Solid">False for a phantom (a trigger volume, never something to bump into).</param>
/// <param name="HasTransform">True for a bhkRigidBodyT, whose own translation/rotation was applied.</param>
/// <param name="FrameTranslation">The body frame's translation in Havok units (zero for a plain body), and
/// <paramref name="FrameRotated"/> whether its rotation was anything but identity — surfaced so a run can say how
/// often the T-body frame was exercised and check it against the render placement.</param>
/// <param name="MotionSystem">The rigid body's <c>hkMotionType</c> by name (MO_SYS_FIXED, MO_SYS_KEYFRAMED, MO_SYS_DYNAMIC,
/// MO_SYS_BOX_INERTIA, …) — what the NIF says about whether physics may move this body. Reported, not judged: whether the
/// ENGINE lets it move also depends on the placing record's type, which the caller knows and this decoder does not. Empty
/// for a phantom or a body without one.</param>
/// <param name="QualityType">The rigid body's <c>hkQualityType</c> by name (MO_QUAL_FIXED, MO_QUAL_MOVING, MO_QUAL_DEBRIS…),
/// beside the motion system because the two disagree on some vanilla meshes and a reader should see both.</param>
/// <param name="TriangleMaterials">One nif.xml material name per triangle. Null entries mean unread material;
/// geometry and material completeness are separate. Compound shapes preserve each child/chunk's material.</param>
/// <param name="TriangleRadii">Per triangle, the Havok radius of its shape in Havok units (box, convex-vertices, compressed mesh),
/// 0 where the shape has none or the decode already grew it into the geometry (<c>convexRadius</c>).</param>
public sealed record HavokBody(
    string BodyType, string Layer, bool Solid, bool HasTransform, Vector3 FrameTranslation, bool FrameRotated, NiAVObject? Node,
    Vector3[] Vertices, int[] Indices,
    IReadOnlyList<string> ShapeTypes, IReadOnlyList<string> Unread,
    string MotionSystem = "", string QualityType = "", string?[]? TriangleMaterials = null, float[]? TriangleRadii = null);

public sealed record HavokCollisionResult(IReadOnlyList<HavokBody> Bodies, HavokDecodeStats Stats, uint StreamVersion);

public static class HavokCollision
{
    /// <summary>Skyrim world units per Havok unit, for Skyrim-era files (BS stream version 83 and up).
    /// MEASURED 2026-09-02 rather than taken on reputation: collprobe compares each vanilla mesh's render extent to
    /// its decoded collision extent, axis by axis, and the compressed-mesh statics put the ratio at 70.0 with a
    /// quartile spread of under one percent — the largest statics within a few tenths. That cannot tell 70 from the
    /// engine's own 1/0.0142875 = 69.991 (the bhkWorld world scale CommonLibSSE exposes at runtime), so the reputed
    /// figure is kept: it is inside the measured band and the difference is half a unit across a cell. Files older
    /// than Skyrim use 7 (NifSkope's constant for Oblivion/FO3) — unmeasured, because an SSE load order should hold
    /// none. Re-run collprobe to see the current spread; the numbers belong with the run, not here.</summary>
    public const float SkyrimUnitsPerHavokUnit = 69.99f;
    public const float LegacyUnitsPerHavokUnit = 7f;

    public static float UnitScaleFor(uint streamVersion) => streamVersion >= 83 ? SkyrimUnitsPerHavokUnit : LegacyUnitsPerHavokUnit;

    /// <summary>EXPERIMENT KNOBS — the two conventions this decoder had to choose without a spec to cite, exposed so a
    /// corpus run can try the other readings against the files' own stated AABBs and record which one they obey.
    /// <see cref="ChunkTransformMode"/>: 0 = Havok's hkQsTransform semantics, R·v + T (the default); 1 = NifSkope's
    /// drawCMS, which rotates AFTER adding both translations, R·(v + T); 2 and 3 = the same two with the quaternion
    /// conjugated (the opposite rotation sense). <see cref="TransformRotationTransposed"/>: read a bhkTransformShape's
    /// 3×3 as row-vector (transposed) rather than column-vector. Not configuration; a measurement's knobs.</summary>
    public static int ChunkTransformMode;
    public static bool TransformRotationTransposed;

    /// <summary>Decode every collision body in a parsed file. Never throws for a shape it does not understand —
    /// the shape is NAMED in the body's <see cref="HavokBody.Unread"/> list instead, because a soup that silently
    /// understates its volume would be trusted by a caller (Q3). <paramref name="convexRadius"/>: read every shape's Havok radius —
    /// a convex-vertices hull grown by its convex radius, a box grown by its radius, a compressed mesh as a slab of its mesh radius
    /// (Havok's definitions; measured in game: convex items, and drops onto convex and compressed-mesh supports, rest on the grown
    /// surface; the box radius by its definition and the vertical fit of box collision to its render, with a lab station in
    /// runs/2026-10-03-convex-radius-default). Off by default here: the NifGeometry reader (route,
    /// floors, contact, overlap's collision column, state) keeps the core, where its calibrations were made and where contact's
    /// in-game acceptance did not improve with the radius; the physics drop (PhysicsReach) reads it.</summary>
    public static HavokCollisionResult Decode(NifFile nif, bool includeTriangles, bool convexRadius = false)
    {
        var stats = new HavokDecodeStats();
        uint bsver = 0;
        try { bsver = nif.Header.Version.StreamVersion; } catch { }

        var bodies = new List<HavokBody>();
        var seen = new HashSet<object>(ReferenceEqualityComparer.Instance);

        // Attached pass: every AV object's own collision object, so the node it hangs off is known.
        foreach (var block in nif.Blocks)
        {
            if (block is not NiAVObject av) continue;
            var col = CollisionObjectOf(nif, av);
            if (col is null || !seen.Add(col)) continue;
            DecodeCollisionObject(nif, col, av, bsver, includeTriangles, stats, bodies, convexRadius);
        }
        // Stray pass: collision objects no node points at. Decoded in the file's root frame and counted, never dropped.
        foreach (var block in nif.Blocks)
        {
            if (block is not bhkNiCollisionObject col || !seen.Add(col)) continue;
            stats.Unattached++;
            DecodeCollisionObject(nif, col, null, bsver, includeTriangles, stats, bodies, convexRadius);
        }
        return new HavokCollisionResult(bodies, stats, bsver);
    }

    // NiAVObject.CollisionObject, found by reflection so a rename in the library's model is a missing property
    // (reported as "no collision") rather than a compile-time dependency on one accessor name.
    static PropertyInfo? _colProp;
    static bool _colProbed;
    static bhkNiCollisionObject? CollisionObjectOf(NifFile nif, NiAVObject av)
    {
        if (!_colProbed)
        {
            _colProp = typeof(NiAVObject).GetProperty("CollisionObject", BindingFlags.Public | BindingFlags.Instance);
            _colProbed = true;
        }
        if (_colProp is null) return null;
        object? r;
        try { r = _colProp.GetValue(av); } catch { return null; }
        return r switch
        {
            NiBlockRef<NiCollisionObject> a => nif.GetBlock(a) as bhkNiCollisionObject,
            NiBlockRef<bhkNiCollisionObject> b => nif.GetBlock(b),
            _ => null,
        };
    }

    static void DecodeCollisionObject(NifFile nif, bhkNiCollisionObject col, NiAVObject? node, uint bsver,
                                      bool includeTriangles, HavokDecodeStats stats, List<HavokBody> bodies, bool convexRadius = false)
    {
        stats.CollisionObjects++;
        var body = nif.GetBlock(col.Body);
        var shapeTypes = new List<string>();
        var unread = new List<string>();
        if (body is null)
        {
            bodies.Add(new HavokBody(col.GetType().Name, "", false, false, Vector3.Zero, false, node, Array.Empty<Vector3>(), Array.Empty<int>(),
                                     shapeTypes, new[] { "collision object with no body" }));
            return;
        }

        bool solid = body is not bhkPhantom;
        if (!solid) stats.Phantoms++;
        string layer = LayerOf(body, bsver);

        // The body's own frame. Only a bhkRigidBodyT carries one the engine honours (nif.xml: "a normal
        // bhkRigidBody ignores those properties"); the info variant depends on the file's era. MEASURED on the whole
        // vanilla order 2026-09-05 (Q28, runs/2026-09-05-root-transform-in-game/plainbody-check.txt): applying a plain
        // body's frame as a T's moved the collision of 820 of 1,283 meshes with a plain translation away from their own
        // render (a bucket's by 4,094 u, a milk jug's by 461 u) and turned 47 of 219 with a plain rotation, none improved —
        // the fields on a plain body are informational (a copy of the node's world rotation, a centre of mass), not a
        // frame. The pine trunk that seemed to need the plain body's rotation (Q26-b) is the node CHAIN's rotation sense,
        // NifGeometry.ToMatrix, not this.
        var frame = NifTransform.Identity;
        bool hasTransform = false;
        if (body is bhkRigidBodyT t)
        {
            hasTransform = true;
            stats.BodiesWithTransform++;
            frame = BodyFrame(t, bsver);
        }
        else stats.BodiesPlain++;

        var verts = new List<Vector3>();
        var idx = new List<int>();
        var sink = new Sink(verts, idx, shapeTypes, unread, stats, includeTriangles) { ConvexRadius = convexRadius };

        if (body is bhkWorldObject wo)
            DecodeShape(nif, nif.GetBlock(wo.Shape), frame, sink, 0);
        else
            unread.Add(body.GetType().Name + " (not a world object)");

        // The body's own account of whether it may move. Read by name so an enum value nifly adds later is reported
        // rather than mapped to "unknown"; a body type without the fields (a phantom) reports empty.
        string motion = "", quality = "";
        if (body is bhkRigidBody rb)
        {
            try
            {
                if (bsver >= 130) { motion = rb.RigidBodyInfo_bRBCI2014.MotionSystem.ToString(); quality = rb.RigidBodyInfo_bRBCI2014.QualityType.ToString(); }
                else if (bsver >= 83) { motion = rb.RigidBodyInfo_bRBCI2010.MotionSystem.ToString(); quality = rb.RigidBodyInfo_bRBCI2010.QualityType.ToString(); }
                else { motion = rb.RigidBodyInfo_bRBCI550660.MotionSystem.ToString(); quality = rb.RigidBodyInfo_bRBCI550660.QualityType.ToString(); }
            }
            catch { }
        }

        bodies.Add(new HavokBody(body.GetType().Name, layer, solid, hasTransform, frame.Translation, !IsIdentityRotation(frame.Rotation), node,
                                 verts.ToArray(), idx.ToArray(), shapeTypes.Distinct().ToList(), unread.Distinct().ToList(), motion, quality, sink.Materials.ToArray(), sink.Radii.ToArray()));
    }

    static string LayerOf(object body, uint bsver)
    {
        try
        {
            if (body is bhkWorldObject wo)
            {
                var f = wo.HavokFilter;
                if (bsver >= 83) return f.Layer_SL.ToString();
                if (bsver == 34) return f.Layer_F3L.ToString();
                return f.Layer_OL.ToString();
            }
        }
        catch { }
        return "";
    }

    static NifTransform BodyFrame(bhkRigidBody t, uint bsver)
    {
        Vector4 tr; hkQuaternion q;
        if (bsver >= 130) { tr = t.RigidBodyInfo_bRBCI2014.Translation; q = t.RigidBodyInfo_bRBCI2014.Rotation; }
        else if (bsver >= 83) { tr = t.RigidBodyInfo_bRBCI2010.Translation; q = t.RigidBodyInfo_bRBCI2010.Rotation; }
        else { tr = t.RigidBodyInfo_bRBCI550660.Translation; q = t.RigidBodyInfo_bRBCI550660.Rotation; }
        return new NifTransform(FromQuaternion(q), 1f, new Vector3(tr.X, tr.Y, tr.Z));
    }

    /// <summary>A Havok quaternion as the row-vector matrix <see cref="Vector3.Transform(Vector3, Matrix4x4)"/> expects.
    /// A zero quaternion (an unread info block) is treated as identity rather than collapsing the shape to a point.</summary>
    public static Matrix4x4 FromQuaternion(hkQuaternion q)
    {
        var n = new Quaternion(q.X, q.Y, q.Z, q.W);
        if (n.LengthSquared() < 1e-8f) return Matrix4x4.Identity;
        return Matrix4x4.CreateFromQuaternion(Quaternion.Normalize(n));
    }

    /// <summary>A NIF Matrix44 as a rigid frame. The rotation is the upper 3×3 in the column-vector sense (the
    /// engine's convention for every NIF matrix, cf. NifGeometry.ToMatrix); the translation lives in either the last
    /// COLUMN (m14, m24, m34 — hkTransform's layout) or the last ROW, and rather than assume, the reader takes
    /// whichever triple is populated and counts what it saw, so the corpus sweep can say which one the files use.</summary>
    public static NifTransform FromMatrix44(Matrix44 m, HavokDecodeStats? stats)
    {
        var col = new Vector3(m.M14, m.M24, m.M34);
        var row = new Vector3(m.M41, m.M42, m.M43);
        bool colSet = col.LengthSquared() > 1e-10f, rowSet = row.LengthSquared() > 1e-10f;
        Vector3 tr;
        if (colSet && !rowSet) { tr = col; if (stats is not null) stats.TransformTranslationInColumn++; }
        else if (rowSet && !colSet) { tr = row; if (stats is not null) stats.TransformTranslationInRow++; }
        else { tr = col; if (stats is not null && (colSet || rowSet)) stats.TransformTranslationAmbiguous++; }

        // Row-major NIF (i,j) → the transposed layout Vector3.Transform needs for R·v.
        var rot = TransformRotationTransposed
            ? new Matrix4x4(
                m.M11, m.M12, m.M13, 0,
                m.M21, m.M22, m.M23, 0,
                m.M31, m.M32, m.M33, 0,
                0, 0, 0, 1)
            : new Matrix4x4(
                m.M11, m.M21, m.M31, 0,
                m.M12, m.M22, m.M32, 0,
                m.M13, m.M23, m.M33, 0,
                0, 0, 0, 1);
        if (stats is not null && !IsIdentityRotation(rot)) stats.TransformRotated++;
        return new NifTransform(rot, 1f, tr);
    }

    static bool IsIdentityRotation(Matrix4x4 r) =>
        MathF.Abs(r.M11 - 1) < 1e-4f && MathF.Abs(r.M22 - 1) < 1e-4f && MathF.Abs(r.M33 - 1) < 1e-4f
        && MathF.Abs(r.M12) < 1e-4f && MathF.Abs(r.M13) < 1e-4f && MathF.Abs(r.M21) < 1e-4f
        && MathF.Abs(r.M23) < 1e-4f && MathF.Abs(r.M31) < 1e-4f && MathF.Abs(r.M32) < 1e-4f;

    // ---- the shape walk ---------------------------------------------------

    sealed class Sink
    {
        public readonly List<Vector3> V; public readonly List<int> I;
        public readonly List<string?> Materials = new();
        /// <summary>Per triangle, the Havok radius of its shape that is NOT grown into the geometry (0 once grown, so no reader
        /// counts it twice); <see cref="Radius"/> is the current shape's, set around its <see cref="Add"/>.</summary>
        public readonly List<float> Radii = new();
        public float Radius;
        public readonly List<string> Types; public readonly List<string> Unread;
        public readonly HavokDecodeStats Stats; public readonly bool Triangles;
        public bool ConvexRadius;
        public Sink(List<Vector3> v, List<int> i, List<string> t, List<string> u, HavokDecodeStats s, bool tri)
        { V = v; I = i; Types = t; Unread = u; Stats = s; Triangles = tri; }

        /// <summary>Append a local-frame soup under <paramref name="frame"/>. Vertices always (the bounds need them);
        /// indices only when triangles were asked for. For a CONVEX shape (<paramref name="convex"/>: a box, sphere,
        /// capsule, cylinder or hull) every triangle is turned to face away from the shape's own vertex centroid after
        /// the frame is applied — exact for a convex solid, and what makes the soup usable as a closed outward surface
        /// whatever the frame's handedness or a hull plane's stored sign (the contact audit found 664 of 1,367 vanilla
        /// hulls carrying an inward face before this). Non-convex soups keep the file's winding.</summary>
        public void Add(NifTransform frame, IReadOnlyList<Vector3> verts, IReadOnlyList<int> idx, bool convex = false, string? material = null, IReadOnlyList<string?>? materials = null)
        {
            int b = V.Count;
            foreach (var p in verts) V.Add(frame.Apply(p));
            if (!Triangles) return;
            var cen = Vector3.Zero;
            if (convex && verts.Count > 0)
            {
                for (int k = b; k < V.Count; k++) cen += V[k];
                cen /= V.Count - b;
            }
            for (int k = 0; k + 2 < idx.Count; k += 3)
            {
                if (idx[k] < 0 || idx[k + 1] < 0 || idx[k + 2] < 0 || idx[k] >= verts.Count || idx[k + 1] >= verts.Count || idx[k + 2] >= verts.Count) continue;
                int i0 = b + idx[k], i1 = b + idx[k + 1], i2 = b + idx[k + 2];
                if (convex)
                {
                    var p0 = V[i0]; var p1 = V[i1]; var p2 = V[i2];
                    if (Vector3.Dot(Vector3.Cross(p1 - p0, p2 - p0), (p0 + p1 + p2) / 3f - cen) < 0f) (i1, i2) = (i2, i1);
                }
                I.Add(i0); I.Add(i1); I.Add(i2);
                Materials.Add(materials is null ? material : materials[k / 3]);
                Radii.Add(Radius * frame.Scale);
            }
        }
    }

    static void DecodeShape(NifFile nif, object? shape, NifTransform frame, Sink s, int depth)
    {
        if (shape is null) { s.Unread.Add("null shape reference"); return; }
        if (depth > 16) { s.Unread.Add("shape nesting deeper than 16"); return; }
        s.Types.Add(shape.GetType().Name);

        string? material = shape is bhkSphereRepShape sr && nif.Header.Version.StreamVersion >= 83
            ? sr.Material.Material_SHM.ToString() : null;
        switch (shape)
        {
            case bhkMoppBvTreeShape mopp:
                if (MathF.Abs(mopp.Scale - 1f) > 1e-4f) s.Stats.MoppNonUnitScale++;
                DecodeShape(nif, nif.GetBlock(mopp.Shape), frame, s, depth + 1);
                return;

            case bhkListShape list:
                foreach (var sub in list.SubShapes.GetBlocks(nif)) DecodeShape(nif, sub, frame, s, depth + 1);
                return;

            case bhkConvexListShape clist:
                foreach (var sub in clist.SubShapes.GetBlocks(nif)) DecodeShape(nif, sub, frame, s, depth + 1);
                return;

            case bhkTransformShape ts:
                s.Stats.TransformShapes++;
                DecodeShape(nif, nif.GetBlock(ts.Shape), frame.Compose(FromMatrix44(ts.Transform, s.Stats)), s, depth + 1);
                return;

            case bhkConvexTransformShape cts:
                s.Stats.TransformShapes++;
                DecodeShape(nif, nif.GetBlock(cts.Shape), frame.Compose(FromMatrix44(cts.Transform, s.Stats)), s, depth + 1);
                return;

            case bhkBoxShape box:
                s.Stats.Boxes++;
                {
                    // Havok's box surface is the box grown by its radius (a shell; the corners are drawn square, a little outside it)
                    var half = box.Dimensions;
                    if (s.ConvexRadius && box.Radius > 0) { half += new Vector3(box.Radius); s.Stats.BoxRadiusApplied++; }
                    var (v, i) = BoxSoup(half); s.Radius = s.ConvexRadius ? 0f : box.Radius; s.Add(frame, v, i, convex: true, material: material); s.Radius = 0f;
                }
                return;

            case bhkSphereShape sph:
                s.Stats.Spheres++;
                { var (v, i) = SphereSoup(Vector3.Zero, sph.Radius); s.Add(frame, v, i, convex: true, material: material); }
                return;

            case bhkMultiSphereShape ms:
                s.Stats.Spheres += ms.Spheres?.Count ?? 0;
                if (ms.Spheres is not null)
                    foreach (var b in ms.Spheres) { var (v, i) = SphereSoup(b.Center, b.Radius); s.Add(frame, v, i, convex: true, material: material); }
                return;

            case bhkCapsuleShape cap:
                s.Stats.Capsules++;
                {
                    float r = cap.Radius1 > 0 ? cap.Radius1 : cap.Radius;
                    var (v, i) = CapsuleSoup(cap.FirstPoint, cap.SecondPoint, r);
                    s.Add(frame, v, i, convex: true, material: material);
                }
                return;

            case bhkCylinderShape cyl:
                s.Stats.Cylinders++;
                {
                    var (v, i) = CylinderSoup(new Vector3(cyl.VertexA.X, cyl.VertexA.Y, cyl.VertexA.Z),
                                              new Vector3(cyl.VertexB.X, cyl.VertexB.Y, cyl.VertexB.Z), cyl.CylinderRadius);
                    s.Add(frame, v, i, convex: true, material: material);
                }
                return;

            case bhkConvexVerticesShape cv:
                s.Stats.ConvexShapes++;
                {
                    var pts = new List<Vector3>(cv.Vertices?.Count ?? 0);
                    if (cv.Vertices is not null) foreach (var p in cv.Vertices) pts.Add(new Vector3(p.X, p.Y, p.Z));
                    var planes = new List<Vector4>();
                    if (cv.Normals is not null) planes.AddRange(cv.Normals);
                    if (planes.Count == 0)
                    {
                        s.Stats.ConvexWithoutNormals++;
                        // Vertices still bound the shape even when its faces cannot be recovered.
                        s.Add(frame, pts, Array.Empty<int>());
                        s.Unread.Add("bhkConvexVerticesShape (no half-space normals to build faces from)");
                        return;
                    }
                    var idx = ConvexHullFromPlanes(pts, planes, out int dropped, s.Stats, radius: cv.Radius);
                    s.Stats.ConvexPlanesDropped += dropped;
                    s.Stats.ConvexVertices += pts.Count;
                    bool plate = false;
                    if (dropped > 0)
                    {
                        // the shape IS the hull of its vertices: a dropped face is a hole, so the faces are rebuilt from them
                        s.Stats.ConvexShapesWithDroppedPlanes++;
                        float ext = pts.Count == 0 ? 0 : pts.Max(p => MathF.Max(MathF.Abs(p.X), MathF.Max(MathF.Abs(p.Y), MathF.Abs(p.Z))));
                        var (hull, degenerate) = ConvexHull.FromVertices(pts, 1e-5f * MathF.Max(ext, 1e-3f));
                        if (degenerate)
                        {
                            s.Add(frame, pts, Array.Empty<int>());
                            s.Unread.Add("bhkConvexVerticesShape (vertices collinear or fewer than three: no faces)");
                            return;
                        }
                        idx = hull;
                        plate = IsPlate(pts, hull);
                        s.Stats.ConvexRebuiltFromVertices++;
                    }
                    CheckStoredPlanes(pts, idx, planes, cv.Radius, s.Stats);
                    if (s.ConvexRadius && cv.Radius > 0 && idx.Count > 0)
                    {
                        // the collision surface is the hull grown by the convex radius (Havok; measured in the lab: the
                        // radius closes the drop disagreement of a convex item to within the item's own tilt)
                        var (ov, oi) = ConvexHull.Offset(pts, idx, cv.Radius);
                        if (oi.Count > 0)
                        {
                            s.Stats.ConvexRadiusApplied++;
                            s.Add(frame, ov, oi, convex: true, material: material);
                            return;
                        }
                    }
                    s.Radius = cv.Radius; s.Add(frame, pts, idx, convex: !plate, material: material); s.Radius = 0f;
                }
                return;

            case bhkCompressedMeshShape cms:
                s.Stats.CompressedShapes++;
                DecodeCompressed(nif, cms, frame, s, s.ConvexRadius ? cms.Radius : 0f);
                return;

            case bhkPackedNiTriStripsShape packed:
                {
                    var pd = nif.GetBlock(packed.Data);
                    if (pd is null) { s.Unread.Add("bhkPackedNiTriStripsShape (no data block)"); return; }
                    var pts = new List<Vector3>();
                    if (pd.Vertices is { Count: > 0 }) pts.AddRange(pd.Vertices);
                    else if (pd.CompressedVertices is { Count: > 0 })
                        foreach (var h in pd.CompressedVertices) pts.Add(new Vector3((float)h.X, (float)h.Y, (float)h.Z));
                    var sc = packed.Scale;
                    if (MathF.Abs(sc.X - 1) > 1e-4f || MathF.Abs(sc.Y - 1) > 1e-4f || MathF.Abs(sc.Z - 1) > 1e-4f)
                    {
                        s.Stats.PackedNonUnitScale++;
                        for (int k = 0; k < pts.Count; k++) pts[k] = new Vector3(pts[k].X * sc.X, pts[k].Y * sc.Y, pts[k].Z * sc.Z);
                    }
                    var idx = new List<int>();
                    if (pd.Triangles is not null)
                        foreach (var td in pd.Triangles) { idx.Add(td.Triangle.V1); idx.Add(td.Triangle.V2); idx.Add(td.Triangle.V3); }
                    s.Add(frame, pts, idx);
                }
                return;

            case bhkNiTriStripsShape:
                // Legacy (Oblivion-era) collision whose NiTriStripsData vertices are in GAME units, not Havok's —
                // NifSkope draws it at 1/7 against the ×7 body. Not decoded here: an SSE order should hold none,
                // and mixing unit systems inside one soup is exactly the kind of silent wrong answer to refuse.
                s.Unread.Add("bhkNiTriStripsShape (legacy game-unit strips; not decoded)");
                return;

            case bhkPlaneShape:
                s.Unread.Add("bhkPlaneShape (infinite plane)");
                return;

            default:
                s.Unread.Add(shape.GetType().Name);
                return;
        }
    }

    // ---- bhkCompressedMeshShape ----------------------------------------------

    static void DecodeCompressed(NifFile nif, bhkCompressedMeshShape cms, NifTransform frame, Sink s, float radius = 0f)
    {
        var d = nif.GetBlock(cms.Data);
        if (d is null) { s.Unread.Add("bhkCompressedMeshShape (no data block)"); return; }

        var local = new List<Vector3>();
        var idx = new List<int>();
        var materials = new List<string?>();
        // nif.xml models single-value-per-chunk. Unknown encodings must not reinterpret a per-triangle table as it.
        string? MaterialAt(uint index) => (int)d.MaterialType == 1 && d.ChunkMaterials is not null && index < d.ChunkMaterials.Count
            ? d.ChunkMaterials[(int)index].Material.ToString() : null;

        // Big triangles: plain Vector4 vertices, indexed directly.
        if (d.BigVerts is not null)
        {
            int b = local.Count;
            foreach (var v in d.BigVerts) local.Add(new Vector3(v.X, v.Y, v.Z));
            if (d.BigTris is not null)
                foreach (var bt in d.BigTris)
                {
                    s.Stats.BigTriangles++;
                    idx.Add(b + bt.Triangle.V1); idx.Add(b + bt.Triangle.V2); idx.Add(b + bt.Triangle.V3);
                    materials.Add(MaterialAt(bt.Material));
                }
        }

        // Chunks: quantized vertices, an offset, an optional transform, strips then a list.
        float err = d.Error > 0 ? d.Error : 0.001f;
        var chunks = d.Chunks;
        if (chunks is not null)
        {
            for (int ci = 0; ci < chunks.Count; ci++)
            {
                var c = chunks[ci];
                s.Stats.Chunks++;

                var src = c;
                if ((c.Vertices is null || c.Vertices.Count == 0) && c.Reference != ushort.MaxValue)
                {
                    // An instanced chunk: its geometry lives in another chunk, only the placement is its own.
                    s.Stats.ChunksReferencing++;
                    if (c.Reference < chunks.Count) src = chunks[c.Reference];
                    else { s.Stats.ChunksReferenceBroken++; continue; }
                }

                var xf = NifTransform.Identity;
                if (d.ChunkTransforms is not null && c.TransformIndex < d.ChunkTransforms.Count)
                {
                    var qs = d.ChunkTransforms[c.TransformIndex];
                    var qq = qs.Rotation;
                    if ((ChunkTransformMode & 2) != 0) qq = new hkQuaternion { X = -qq.X, Y = -qq.Y, Z = -qq.Z, W = qq.W };
                    var rot = FromQuaternion(qq);
                    if (!IsIdentityRotation(rot)) s.Stats.ChunksWithRotation++;
                    xf = new NifTransform(rot, 1f, new Vector3(qs.Translation.X, qs.Translation.Y, qs.Translation.Z));
                }
                var offset = new Vector3(c.Translation.X, c.Translation.Y, c.Translation.Z);

                int b = local.Count;
                if (src.Vertices is not null)
                    foreach (var q in src.Vertices)
                    {
                        var p = new Vector3(q.X * err, q.Y * err, q.Z * err) + offset;
                        local.Add((ChunkTransformMode & 1) != 0
                            ? Vector3.Transform(p + xf.Translation, xf.Rotation)
                            : xf.Apply(p));
                    }

                var tri = ChunkTriangles(src.Indices, src.Strips);
                s.Stats.ChunkTriangles += tri.Count / 3;
                foreach (var k in tri) idx.Add(b + k);
                string? chunkMaterial = MaterialAt(c.MaterialIndex);
                for (int k = 0; k < tri.Count / 3; k++) materials.Add(chunkMaterial);
            }
        }

        // The shape's own scale (rarely anything but 1,1,1). A ZERO scale vector is an unset field, not an instruction
        // to collapse the shape to a point — vanilla Barrel01's animated body carries one — so it is counted and ignored,
        // as NifSkope ignores the field altogether.
        var sc = cms.Scale;
        if (MathF.Abs(sc.X) < 1e-6f && MathF.Abs(sc.Y) < 1e-6f && MathF.Abs(sc.Z) < 1e-6f)
            s.Stats.CompressedZeroScale++;
        else if (MathF.Abs(sc.X - 1) > 1e-4f || MathF.Abs(sc.Y - 1) > 1e-4f || MathF.Abs(sc.Z - 1) > 1e-4f)
        {
            s.Stats.CompressedNonUnitScale++;
            for (int k = 0; k < local.Count; k++) local[k] = new Vector3(local[k].X * sc.X, local[k].Y * sc.Y, local[k].Z * sc.Z);
        }

        // Self-check against the AABB the file itself states for this shape.
        if (local.Count > 0)
        {
            var box = Aabb.Empty;
            foreach (var p in local) box = box.Add(p);
            var mn = d.AABB.Min; var mx = d.AABB.Max;
            float excess = MathF.Max(0, MathF.Max(MathF.Max(mn.X - box.Min.X, mn.Y - box.Min.Y), mn.Z - box.Min.Z));
            excess = MathF.Max(excess, MathF.Max(MathF.Max(box.Max.X - mx.X, box.Max.Y - mx.Y), box.Max.Z - mx.Z));
            if (excess > s.Stats.StatedAabbMaxExcess) s.Stats.StatedAabbMaxExcess = excess;
            if (excess > 0.02f) s.Stats.StatedAabbViolations++;
            float cov = float.PositiveInfinity;
            var ext = box.Size;
            if (mx.X - mn.X > 1e-3f) cov = MathF.Min(cov, ext.X / (mx.X - mn.X));
            if (mx.Y - mn.Y > 1e-3f) cov = MathF.Min(cov, ext.Y / (mx.Y - mn.Y));
            if (mx.Z - mn.Z > 1e-3f) cov = MathF.Min(cov, ext.Z / (mx.Z - mn.Z));
            if (!float.IsInfinity(cov) && (float.IsNaN(s.Stats.StatedAabbMinCoverage) || cov < s.Stats.StatedAabbMinCoverage))
                s.Stats.StatedAabbMinCoverage = cov;
        }

        if (radius > 0 && idx.Count >= 3)
        {
            var (sv, si, sm) = MeshSlab(local, idx, materials, radius);
            s.Stats.MeshRadiusApplied++;
            s.Add(frame, sv, si, materials: sm);
            return;
        }
        s.Radius = cms.Radius; s.Add(frame, local, idx, materials: materials); s.Radius = 0f;
    }

    /// <summary>A triangle mesh read with its Havok mesh radius: each triangle as a closed prism of half-thickness <paramref name="r"/>
    /// — its two faces offset along the triangle's normal and the three rim quads joining them, all wound outward, on six vertices of
    /// its own (edges are not rounded). Exact on a face, which is where a resting body meets a floor or a shelf; the rims close the
    /// gap the two offset faces leave at a crease, so the grown mesh contains the core (drop-crossing redesign 2026-10-03: a point
    /// over a ridge otherwise fell through that gap; lab-confirmed that items starting in a rim overlap are pushed off). The soup is
    /// eight triangles per triangle. A degenerate triangle is dropped. Materials follow their triangle.</summary>
    public static (List<Vector3> V, List<int> I, List<string?> M) MeshSlab(IReadOnlyList<Vector3> v, IReadOnlyList<int> idx, IReadOnlyList<string?> materials, float r)
    {
        var nv = new List<Vector3>(idx.Count * 2); var ni = new List<int>(idx.Count * 8); var nm = new List<string?>(idx.Count * 8 / 3);
        for (int k = 0; k + 2 < idx.Count; k += 3)
        {
            if (idx[k] < 0 || idx[k + 1] < 0 || idx[k + 2] < 0 || idx[k] >= v.Count || idx[k + 1] >= v.Count || idx[k + 2] >= v.Count) continue;
            var p0 = v[idx[k]]; var p1 = v[idx[k + 1]]; var p2 = v[idx[k + 2]];
            var n = Vector3.Cross(p1 - p0, p2 - p0); float len = n.Length();
            if (len < 1e-12f) continue;
            n /= len;
            string? m = k / 3 < materials.Count ? materials[k / 3] : null;
            int b0 = nv.Count;
            nv.Add(p0 + n * r); nv.Add(p1 + n * r); nv.Add(p2 + n * r);
            nv.Add(p0 - n * r); nv.Add(p2 - n * r); nv.Add(p1 - n * r);
            ni.Add(b0); ni.Add(b0 + 1); ni.Add(b0 + 2); ni.Add(b0 + 3); ni.Add(b0 + 4); ni.Add(b0 + 5);
            nm.Add(m); nm.Add(m);
            // rims: for each top edge Tk -> Tk2 (counter-clockwise about n) the quad down to the bottom face, outward (e x n)
            int[] top = { b0, b0 + 1, b0 + 2 }, bot = { b0 + 3, b0 + 5, b0 + 4 };
            for (int e = 0; e < 3; e++)
            {
                int e2 = (e + 1) % 3;
                ni.Add(top[e]); ni.Add(bot[e]); ni.Add(bot[e2]);
                ni.Add(top[e]); ni.Add(bot[e2]); ni.Add(top[e2]);
                nm.Add(m); nm.Add(m);
            }
        }
        return (nv, ni, nm);
    }

    /// <summary>A chunk's index buffer as a flat triangle list: the leading strips (each <c>Strips[s]</c> indices long,
    /// a strip of n indices being n−2 triangles, winding alternated so every triangle faces the same way), then whatever
    /// indices remain as plain triangles. Exposed so a guard can pin the decode on a hand-built buffer.</summary>
    public static List<int> ChunkTriangles(IReadOnlyList<ushort>? indices, IReadOnlyList<ushort>? strips)
    {
        var tri = new List<int>();
        if (indices is null) return tri;
        int offset = 0;
        if (strips is not null)
            foreach (var len in strips)
            {
                for (int k = 0; k + 2 < len; k++)
                {
                    if (offset + k + 2 >= indices.Count) break;
                    int a = indices[offset + k], b = indices[offset + k + 1], c = indices[offset + k + 2];
                    if ((k & 1) == 0) { tri.Add(a); tri.Add(b); tri.Add(c); }
                    else { tri.Add(a); tri.Add(c); tri.Add(b); }
                }
                offset += len;
            }
        for (int k = offset; k + 2 < indices.Count; k += 3)
        { tri.Add(indices[k]); tri.Add(indices[k + 1]); tri.Add(indices[k + 2]); }
        return tri;
    }

    // ---- the analytic shapes, tessellated -----------------------------------------
    // Coarse on purpose: these feed a surface-crossing test, not a renderer, and a 12-segment capsule is a
    // trunk to within a few units. Every one is closed and wound outward, because MeshColumnIndex reads facing.

    /// <summary>A box of the given HALF extents about the origin, 12 outward triangles.</summary>
    public static (List<Vector3> V, List<int> I) BoxSoup(Vector3 half)
    {
        var lo = -half; var hi = half;
        var v = new List<Vector3>
        {
            new(lo.X, lo.Y, lo.Z), new(hi.X, lo.Y, lo.Z), new(hi.X, hi.Y, lo.Z), new(lo.X, hi.Y, lo.Z),
            new(lo.X, lo.Y, hi.Z), new(hi.X, lo.Y, hi.Z), new(hi.X, hi.Y, hi.Z), new(lo.X, hi.Y, hi.Z),
        };
        var i = new List<int>
        {
            0,2,1, 0,3,2,   4,5,6, 4,6,7,   0,1,5, 0,5,4,   2,3,7, 2,7,6,   1,2,6, 1,6,5,   3,0,4, 3,4,7,
        };
        return (v, i);
    }

    public static (List<Vector3> V, List<int> I) SphereSoup(Vector3 centre, float r, int segs = 12, int rings = 6)
        => CapsuleSoup(centre, centre, r, segs, rings / 2);

    /// <summary>A capsule from <paramref name="a"/> to <paramref name="b"/> of radius <paramref name="r"/>: a ring
    /// ladder along the axis — hemispherical cap, cylinder, hemispherical cap — closed with two pole fans. With a == b
    /// it degenerates cleanly to a sphere.</summary>
    public static (List<Vector3> V, List<int> I) CapsuleSoup(Vector3 a, Vector3 b, float r, int segs = 12, int capRings = 3)
    {
        var v = new List<Vector3>();
        var i = new List<int>();
        var axis = b - a;
        Vector3 w = axis.LengthSquared() > 1e-10f ? Vector3.Normalize(axis) : Vector3.UnitZ;
        var u = Vector3.Normalize(Vector3.Cross(MathF.Abs(w.Z) < 0.9f ? Vector3.UnitZ : Vector3.UnitX, w));
        var vv = Vector3.Cross(w, u);   // u, vv, w right-handed → ccw about +w is outward-consistent

        var rings = new List<(Vector3 C, float R)>();
        for (int k = 1; k <= capRings; k++)
        {
            float phi = -MathF.PI / 2 + k * (MathF.PI / 2) / capRings;   // −90°..0°, excluding the pole
            if (k == capRings) phi = 0f;
            rings.Add((a + w * (r * MathF.Sin(phi)), r * MathF.Cos(phi)));
        }
        if (axis.LengthSquared() > 1e-10f) rings.Add((b, r));
        for (int k = 1; k < capRings; k++)
        {
            float phi = k * (MathF.PI / 2) / capRings;
            rings.Add((b + w * (r * MathF.Sin(phi)), r * MathF.Cos(phi)));
        }

        int poleA = v.Count; v.Add(a - w * r);
        int ringBase = v.Count;
        foreach (var (c, rr) in rings)
            for (int sgm = 0; sgm < segs; sgm++)
            {
                float th = 2 * MathF.PI * sgm / segs;
                v.Add(c + u * (rr * MathF.Cos(th)) + vv * (rr * MathF.Sin(th)));
            }
        int poleB = v.Count; v.Add(b + w * r);

        int R(int ring, int sgm) => ringBase + ring * segs + (sgm % segs);
        for (int sgm = 0; sgm < segs; sgm++) { i.Add(poleA); i.Add(R(0, sgm + 1)); i.Add(R(0, sgm)); }
        for (int ring = 0; ring + 1 < rings.Count; ring++)
            for (int sgm = 0; sgm < segs; sgm++)
            {
                int l0 = R(ring, sgm), l1 = R(ring, sgm + 1), u0 = R(ring + 1, sgm), u1 = R(ring + 1, sgm + 1);
                i.Add(l0); i.Add(l1); i.Add(u1);
                i.Add(l0); i.Add(u1); i.Add(u0);
            }
        int last = rings.Count - 1;
        for (int sgm = 0; sgm < segs; sgm++) { i.Add(R(last, sgm)); i.Add(R(last, sgm + 1)); i.Add(poleB); }
        return (v, i);
    }

    /// <summary>A closed cylinder from <paramref name="a"/> to <paramref name="b"/>: two rings and two cap fans.</summary>
    public static (List<Vector3> V, List<int> I) CylinderSoup(Vector3 a, Vector3 b, float r, int segs = 12)
    {
        var v = new List<Vector3>();
        var i = new List<int>();
        var axis = b - a;
        Vector3 w = axis.LengthSquared() > 1e-10f ? Vector3.Normalize(axis) : Vector3.UnitZ;
        var u = Vector3.Normalize(Vector3.Cross(MathF.Abs(w.Z) < 0.9f ? Vector3.UnitZ : Vector3.UnitX, w));
        var vv = Vector3.Cross(w, u);
        int ca = v.Count; v.Add(a);
        int cb = v.Count; v.Add(b);
        int ra = v.Count;
        for (int sgm = 0; sgm < segs; sgm++) { float th = 2 * MathF.PI * sgm / segs; v.Add(a + u * (r * MathF.Cos(th)) + vv * (r * MathF.Sin(th))); }
        int rb = v.Count;
        for (int sgm = 0; sgm < segs; sgm++) { float th = 2 * MathF.PI * sgm / segs; v.Add(b + u * (r * MathF.Cos(th)) + vv * (r * MathF.Sin(th))); }
        for (int sgm = 0; sgm < segs; sgm++)
        {
            int n = (sgm + 1) % segs;
            i.Add(ca); i.Add(ra + n); i.Add(ra + sgm);                    // bottom cap, facing −w
            i.Add(ra + sgm); i.Add(ra + n); i.Add(rb + n);                // side
            i.Add(ra + sgm); i.Add(rb + n); i.Add(rb + sgm);
            i.Add(rb + sgm); i.Add(rb + n); i.Add(cb);                    // top cap, facing +w
        }
        return (v, i);
    }

    /// <summary>Whether a hull's triangles all lie in one plane (a flat plate, triangulated in both windings).</summary>
    static bool IsPlate(IReadOnlyList<Vector3> pts, IReadOnlyList<int> idx)
    {
        if (idx.Count < 3) return false;
        var n0 = Vector3.Cross(pts[idx[1]] - pts[idx[0]], pts[idx[2]] - pts[idx[0]]);
        if (n0.LengthSquared() <= 0) return false;
        n0 = Vector3.Normalize(n0);
        for (int k = 0; k + 2 < idx.Count; k += 3)
        {
            var n = Vector3.Cross(pts[idx[k + 1]] - pts[idx[k]], pts[idx[k + 2]] - pts[idx[k]]);
            if (n.LengthSquared() > 0 && MathF.Abs(Vector3.Dot(Vector3.Normalize(n), n0)) < 0.9999f) return false;
        }
        return true;
    }

    /// <summary>Checks each stored plane against the hull: the plane must lie between the hull's support in its normal's
    /// direction and that support pushed out by the convex radius (either sign convention for w), within
    /// <paramref name="tolerance"/>. A shape with any plane outside that window is COUNTED and NAMED in
    /// <see cref="HavokDecodeStats.ConvexPlanesMismatchShapes"/>; its faces still come from its vertices (Havok's definition;
    /// in the lab a shield whose stored planes cut 0.015-0.062 Havok units inside its vertices came to rest where its
    /// vertices plus the radius put it, not where the planes would).</summary>
    static void CheckStoredPlanes(IReadOnlyList<Vector3> pts, IReadOnlyList<int> idx, IReadOnlyList<Vector4> planes, float radius,
                                  HavokDecodeStats stats, float tolerance = 0.005f)
    {
        var hullVerts = idx.Count > 0 ? idx.Distinct().ToList() : Enumerable.Range(0, pts.Count).ToList();
        float worst = 0; int bad = 0;
        foreach (var pl in planes)
        {
            var n = new Vector3(pl.X, pl.Y, pl.Z); float nl = n.Length();
            if (nl < 1e-6f) { bad++; worst = float.PositiveInfinity; continue; }
            n /= nl; float w = pl.W / nl;
            float s = float.NegativeInfinity;
            foreach (int k in hullVerts) s = MathF.Max(s, Vector3.Dot(n, pts[k]));
            static float Outside(float e, float r, float tol) => e > tol ? e - tol : e < -r - tol ? -r - tol - e : 0;
            float miss = MathF.Min(Outside(s + w, radius, tolerance), Outside(s - w, radius, tolerance));
            if (miss > 0) { bad++; worst = MathF.Max(worst, miss); }
        }
        if (bad == 0) { stats.ConvexPlanesMatchHull++; return; }
        stats.ConvexPlanesMismatch++;
        if (stats.ConvexPlanesMismatchShapes.Count < 64)
            stats.ConvexPlanesMismatchShapes.Add($"{pts.Count} vertices, {planes.Count} planes, radius {radius:0.######}: {bad} planes outside the hull's radius window, worst by {worst:0.####} Havok units");
    }

    /// <summary>Faces of a convex hull from its vertices and the outward half-space normals the file stores beside
    /// them. Each plane's face is the hull's SUPPORT in that normal's direction — the vertices at the maximum of n·v —
    /// ordered counter-clockwise about the normal and fanned. Using the support rule rather than the stored w means
    /// no sign convention is assumed for w; the stored value is checked against both readings and COUNTED in
    /// <paramref name="stats"/> instead. Planes with fewer than three vertices at their support are counted in
    /// <paramref name="dropped"/> — a face the file describes that the vertices do not carry.</summary>
    public static List<int> ConvexHullFromPlanes(IReadOnlyList<Vector3> pts, IReadOnlyList<Vector4> planes, out int dropped,
                                                 HavokDecodeStats? stats = null, float tolerance = 0.005f, float radius = 0f)
    {
        var idx = new List<int>();
        dropped = 0;
        var onPlane = new List<int>();
        foreach (var pl in planes)
        {
            var n = new Vector3(pl.X, pl.Y, pl.Z);
            float nl = n.Length();
            if (nl < 1e-6f) { dropped++; continue; }
            n /= nl;
            float max = float.NegativeInfinity;
            for (int k = 0; k < pts.Count; k++) max = MathF.Max(max, Vector3.Dot(n, pts[k]));
            if (stats is not null && pts.Count > 0)
            {
                float w = pl.W / nl;
                if (MathF.Abs(max + w) <= tolerance) stats.ConvexPlaneSignMinus++;
                else if (MathF.Abs(max - w) <= tolerance) stats.ConvexPlaneSignPlus++;
                else if (MathF.Abs(max + w + radius) <= tolerance) stats.ConvexPlaneSignMinusRadius++;
                else stats.ConvexPlaneSignNeither++;
            }
            onPlane.Clear();
            for (int k = 0; k < pts.Count; k++)
                if (Vector3.Dot(n, pts[k]) >= max - tolerance) onPlane.Add(k);
            if (onPlane.Count < 3) { dropped++; continue; }

            var c = Vector3.Zero;
            foreach (var k in onPlane) c += pts[k];
            c /= onPlane.Count;
            var u = Vector3.Normalize(Vector3.Cross(MathF.Abs(n.Z) < 0.9f ? Vector3.UnitZ : Vector3.UnitX, n));
            var v = Vector3.Cross(n, u);
            onPlane.Sort((p, q) =>
            {
                var dp = pts[p] - c; var dq = pts[q] - c;
                return MathF.Atan2(Vector3.Dot(dp, v), Vector3.Dot(dp, u)).CompareTo(MathF.Atan2(Vector3.Dot(dq, v), Vector3.Dot(dq, u)));
            });
            for (int k = 1; k + 1 < onPlane.Count; k++) { idx.Add(onPlane[0]); idx.Add(onPlane[k]); idx.Add(onPlane[k + 1]); }
        }
        return idx;
    }
}
