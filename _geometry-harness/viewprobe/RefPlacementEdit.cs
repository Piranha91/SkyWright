using System.Numerics;
using HousecarlCore;
using SkyRender;
using GlMatrix4 = OpenTK.Mathematics.Matrix4;

namespace ViewProbe;

/// <summary>Re-places ONE drawn reference live, without rebuilding the scene: the conflict review workbench's preview
/// of a placement edit.
///
/// The scene build bakes each reference's placement into its vertices (Skyrim space: <c>v·s·R + P</c> with
/// <c>R = SpatialOverlap.RefrRotation(rotation)</c>, then swapped to the renderer's Y-up), so a new placement is the
/// matrix that carries the baked pose to the new one, <c>(sRP)⁻¹ · (sR'P')</c>, conjugated into Y-up and set as every
/// one of the reference's meshes' <see cref="GlMesh.ModelTransform"/> (SkyRender applies it in every pass). Restoring
/// the baked placement sets it back to null. The rotation is whatever three REFR angles the caller gives; composing a
/// handle turn with the existing rotation is <see cref="Turned"/>, which goes through the same convention.
///
/// What else the build derived from the placement, and what this does with each:
///   - the meshes and the projected-covering shells (<see cref="RefOcclusion.Meshes"/>): re-placed;
///   - the pick shapes (<see cref="RefOcclusion.Shapes"/>): their triangles are re-placed from the baked copy, bounds
///     recomputed, so a pick names the reference where it is now drawn and not where it was;
///   - the occlusion preview's box (<see cref="RefOcclusion.Min"/>/<see cref="RefOcclusion.Max"/>) and each part's world
///     sphere (<see cref="ShapeOcclusion.World"/>): recomputed; plane culling is re-evaluated by its owner on the next
///     camera change, so the cull verdict for this reference is stale until then, and <see cref="Edit.Stale"/> says so;
///   - a placed LIGHT: its point light was placed at build and is not moved — stated in <see cref="Edit.Stale"/>;
///   - a Base Object Swapper reference: refused, because its drawn placement is the swapped one and a record edit would
///     not be what the game draws;
///   - the BOS and state paint (colour, not placement), the sun's shadow fit (camera-based) and the navmesh, route and
///     cell-outline overlays (not per reference): unaffected.</summary>
static class RefPlacementEdit
{
    /// <summary>A placement: Skyrim-space position, REFR rotation angles in radians, scale.</summary>
    internal readonly record struct Pose(Vec3 Position, Vec3 Rotation, float Scale);

    internal sealed record Edit(Mutagen.Bethesda.Plugins.FormKey Ref, Pose Baked, Pose Now, int Meshes, int PickShapes, IReadOnlyList<string> Stale);

    // The baked pick triangles of each edited shape, so every re-placement starts from the build's own vertices.
    static readonly System.Runtime.CompilerServices.ConditionalWeakTable<PickShape, Vector3[]> Baked = new();

    /// <summary>The Skyrim-space placement matrix the build bakes (row vectors): scale, then REFR rotation, then position.</summary>
    internal static Matrix4x4 World(Pose p) =>
        Matrix4x4.CreateScale(p.Scale) * SpatialOverlap.RefrRotation(p.Rotation)
        * Matrix4x4.CreateTranslation(p.Position.X, p.Position.Y, p.Position.Z);

    // Skyrim Z-up -> viewer Y-up, as TexturedScene.YUp: (x, y, z) -> (x, z, -y), row-vector form, and its inverse.
    static readonly Matrix4x4 ToYUp = new(1, 0, 0, 0, 0, 0, -1, 0, 0, 1, 0, 0, 0, 0, 0, 1);
    static readonly Matrix4x4 FromYUp = Matrix4x4.Transpose(ToYUp);

    /// <summary>The Y-up matrix that carries the baked vertices to <paramref name="now"/>.</summary>
    internal static Matrix4x4 DeltaYUp(Pose baked, Pose now)
    {
        if (!Matrix4x4.Invert(World(baked), out var inv))
            throw new InvalidOperationException("the reference's baked placement cannot be inverted (zero scale)");
        return FromYUp * inv * World(now) * ToYUp;
    }

    /// <summary>The REFR angles after turning <paramref name="angles"/> by <paramref name="radians"/> about world
    /// <paramref name="axis"/> (Skyrim space, counter-clockwise seen from the axis tip), composed — never added per angle.</summary>
    internal static Vec3 Turned(Vec3 angles, Vector3 axis, float radians) =>
        SpatialOverlap.RefrAngles(SpatialOverlap.RefrRotation(angles) * Matrix4x4.CreateFromAxisAngle(Vector3.Normalize(axis), radians));

    internal static Pose BakedPose(SceneRef r) => new(r.Position, r.Rotation, r.Scale);

    /// <summary>A shape's triangle vertices where the build placed them (Y-up), whatever re-placement has happened since.</summary>
    internal static Vector3[] BakedVerts(PickShape s) => Baked.TryGetValue(s, out var v) ? v : s.Verts;

    /// <summary>Re-place <paramref name="ro"/>'s reference at <paramref name="now"/>. Throws a one-sentence refusal for a
    /// reference whose drawn placement is not its record's.</summary>
    internal static Edit Apply(RefOcclusion ro, Pose now)
    {
        var r = ro.Ref;
        if (r.BosSwapped)
            throw new InvalidOperationException($"{r.RefKey} is drawn with the Base Object Swapper's base and transform, so a "
                + "record placement edit would not be what the game draws; edit the swap ini instead.");
        var baked = BakedPose(r);
        bool identity = baked == now;
        var d = DeltaYUp(baked, now);
        GlMatrix4? m = identity ? null : ToGl(d);
        foreach (var mesh in ro.Meshes) mesh.ModelTransform = m;

        var mn = new Vector3(float.MaxValue); var mx = new Vector3(float.MinValue);
        foreach (var s in ro.Shapes)
        {
            var src = Baked.GetValue(s, x => x.Verts);
            var v = new Vector3[src.Length];
            for (int i = 0; i < src.Length; i++) v[i] = identity ? src[i] : Vector3.Transform(src[i], d);
            s.Verts = v;
            s.ComputeBounds();
            foreach (var i in s.Tris) { var z = new Vector3(v[i].X, -v[i].Z, v[i].Y); mn = Vector3.Min(mn, z); mx = Vector3.Max(mx, z); }
        }
        if (ro.Shapes.Count > 0) { ro.Min = mn; ro.Max = mx; }
        foreach (var part in ro.Parts)
            if (part.Stored is { } sb) part.World = SphereBound.Place(sb, now.Rotation, now.Scale, now.Position);

        var stale = new List<string>();
        if (ro.Culled || ro.Parts.Any(p => p.CulledBy.Count > 0))
            stale.Add("the occlusion preview's verdict for this reference is from before the move until the camera moves");
        if (CellLighting.Placed.Any(p => p.Ref == r.RefKey))
            stale.Add("this reference is a light, and its light is still drawn at the old place");
        return new Edit(r.RefKey, baked, now, ro.Meshes.Count, ro.Shapes.Count, stale);
    }

    static GlMatrix4 ToGl(Matrix4x4 m) => new(
        m.M11, m.M12, m.M13, m.M14,
        m.M21, m.M22, m.M23, m.M24,
        m.M31, m.M32, m.M33, m.M34,
        m.M41, m.M42, m.M43, m.M44);
}
