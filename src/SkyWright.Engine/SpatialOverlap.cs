using System.Numerics;

using HousecarlCore;

namespace SkyWright.Engine;

// ======================================================================
//  SpatialOverlap — the SPATIAL QUERY PRIMITIVE (geometry-deconfliction wave 1).
//
//  Pure geometry: place a model-space box into the world with a reference's
//  transform, and ask which navmesh triangles a world box touches. It knows
//  nothing about "blockage", "conflict", or how much overlap is too much —
//  those are judgements, and judgement belongs in a skill (CONTRIBUTING).
//
//  Every threshold is a PARAMETER, never a constant baked in here.
// ======================================================================

/// <summary>One triangle a spatial query matched, with the navmesh it belongs to and its own raw flag bits so a caller
/// can decide what "walkable" means without this layer deciding for it.</summary>
public readonly record struct TriHit(int NavIndex, int TriangleIndex, int Flags, Aabb Bounds);

/// <summary>Named outcome of applying one reference transform to finite model bounds.</summary>
public sealed record BoundsPlacement(string Status, string? Reason, Aabb? Bounds)
{
    public bool UsableForOverlap => Status == "measured";
}

/// <summary>Bounds-only quantities for two world AABBs. These measure enclosing boxes, not occupied mesh volume,
/// visible defect area, contact, or severity.</summary>
public sealed record AabbOverlapMeasures(
    Vec3 DimensionsA,
    Vec3 DimensionsB,
    double VolumeA,
    double VolumeB,
    Vec3 IntersectionExtents,
    double IntersectionVolume,
    double IntersectionAreaXy,
    double? FractionOfSmaller,
    string? FractionReason,
    double SignedZIntersection,
    string ZRelation,
    double ZGap);

public static partial class SpatialOverlap
{
    public static bool IsFinite(Aabb box) => box.IsEmpty
        || (float.IsFinite(box.Min.X) && float.IsFinite(box.Min.Y) && float.IsFinite(box.Min.Z)
         && float.IsFinite(box.Max.X) && float.IsFinite(box.Max.Y) && float.IsFinite(box.Max.Z));

    public static bool IsDegenerate(Aabb box) => !box.IsEmpty
        && (box.Max.X <= box.Min.X || box.Max.Y <= box.Min.Y || box.Max.Z <= box.Min.Z);

    /// <summary>Apply the effective winning reference pose exactly once. Finite negative scale mirrors the model and
    /// is supported. Zero scale retains its finite point bounds as <c>degenerate_geometry</c>. Empty/non-finite/inverted
    /// model bounds and non-finite reference fields are named refusals rather than NaN boxes.</summary>
    public static BoundsPlacement PlaceBounds(Aabb model, Vec3 position, Vec3 rotation, float scale)
    {
        if (model.IsEmpty) return new("missing_vertices", "model bounds are empty", null);
        if (!IsFinite(model) || model.Max.X < model.Min.X || model.Max.Y < model.Min.Y || model.Max.Z < model.Min.Z)
            return new("invalid_transform", "model bounds are non-finite or inverted", null);
        if (!float.IsFinite(position.X) || !float.IsFinite(position.Y) || !float.IsFinite(position.Z)
            || !float.IsFinite(rotation.X) || !float.IsFinite(rotation.Y) || !float.IsFinite(rotation.Z)
            || !float.IsFinite(scale))
            return new("invalid_transform", "reference translation, rotation or scale is non-finite", null);
        var world = ToWorld(model, position, rotation, scale);
        if (!IsFinite(world)) return new("invalid_transform", "the composed world bounds are non-finite", null);
        if (IsDegenerate(world)) return new("degenerate_geometry", "world bounds have zero extent on at least one axis", world);
        return new("measured", null, world);
    }

    /// <summary>Measure two finite, ordered world AABBs. Intersection extents are clamped to zero independently;
    /// therefore XY projected area may be positive while 3D volume is zero because Z is separate. The fraction is null
    /// when the smaller box has zero volume.</summary>
    public static AabbOverlapMeasures MeasureAabbOverlap(Aabb a, Aabb b)
    {
        if (a.IsEmpty || b.IsEmpty || !IsFinite(a) || !IsFinite(b)
            || a.Max.X < a.Min.X || a.Max.Y < a.Min.Y || a.Max.Z < a.Min.Z
            || b.Max.X < b.Min.X || b.Max.Y < b.Min.Y || b.Max.Z < b.Min.Z)
            throw new ArgumentException("AABB overlap measurement requires finite, ordered, non-empty bounds.");

        // Widen the frozen float coordinates before subtracting. The Vec3 outputs below are explicitly presentation
        // values; volumes, areas, fractions and signed Z quantities retain the double-precision differences.
        double ax = (double)a.Max.X - a.Min.X, ay = (double)a.Max.Y - a.Min.Y, az = (double)a.Max.Z - a.Min.Z;
        double bx = (double)b.Max.X - b.Min.X, by = (double)b.Max.Y - b.Min.Y, bz = (double)b.Max.Z - b.Min.Z;
        double sx = Math.Min((double)a.Max.X, b.Max.X) - Math.Max((double)a.Min.X, b.Min.X);
        double sy = Math.Min((double)a.Max.Y, b.Max.Y) - Math.Max((double)a.Min.Y, b.Min.Y);
        double sz = Math.Min((double)a.Max.Z, b.Max.Z) - Math.Max((double)a.Min.Z, b.Min.Z);
        double dx = Math.Max(0d, sx), dy = Math.Max(0d, sy), dz = Math.Max(0d, sz);
        double va = ax * ay * az, vb = bx * by * bz;
        double iv = dx * dy * dz;
        double denominator = Math.Min(va, vb);
        double? fraction = denominator > 0d ? iv / denominator : null;
        string? fractionReason = denominator > 0d ? null : "zero_volume";
        string zRelation = sz > 0d ? "overlapping" : sz < 0d ? "separated" : "touching";
        return new(new((float)ax, (float)ay, (float)az), new((float)bx, (float)by, (float)bz),
                   va, vb, new((float)dx, (float)dy, (float)dz), iv, dx * dy,
                   fraction, fractionReason, sz, zRelation, Math.Max(0d, -sz));
    }

    /// <summary>A model-space box placed into the world by a reference's transform. The eight corners are rotated and
    /// re-bounded, so the result is the AABB *of the oriented box* — conservative (it can only over-cover, never
    /// under-cover), which is the correct direction of error for a broad phase.</summary>
    /// <param name="rotation">Euler XYZ in RADIANS, as stored in the record.</param>
    /// <remarks>The rotation is <see cref="RefrRotation"/> — the engine's own composition, Rx(−x)·Ry(−y)·Rz(−z),
    /// derived from CommonLibVR's <c>NiMatrix3::SetEulerAnglesXYZ</c> and confirmed in game on tilted architecture
    /// (2026-09-01; viewprobe draws with the same matrix). Until the re-baseline of 2026-09-01 this composed
    /// Rz·Ry·Rx un-negated (<see cref="EulerZyx"/>, kept for the Euler sweep and the proof), which is the TRANSPOSE
    /// of the engine's matrix: the opposite sign on every angle AND the opposite order. The two agree only on a
    /// box centred on the ref origin under a single axis — mirroring a centred box gives the same AABB — so a
    /// yaw-only calibration could never see the difference. An off-centre box under a plain yaw lands on the
    /// opposite side of the ref, and every multi-axis ref moves. The four pinned cases are in crossprobe; the
    /// detector floors that were calibrated against the old boxes were re-measured the same day.</remarks>
    public static Aabb ToWorld(Aabb model, Vec3 position, Vec3 rotation, float scale)
    {
        if (model.IsEmpty) return Aabb.Empty;

        var r = RefrRotation(rotation);
        var t = new Vector3(position.X, position.Y, position.Z);
        var box = Aabb.Empty;

        for (int i = 0; i < 8; i++)
        {
            var c = new Vector3(
                (i & 1) == 0 ? model.Min.X : model.Max.X,
                (i & 2) == 0 ? model.Min.Y : model.Max.Y,
                (i & 4) == 0 ? model.Min.Z : model.Max.Z);
            box = box.Add(Vector3.Transform(c * scale, r) + t);
        }
        return box;
    }

    /// <summary>Rz · Ry · Rx — X applied first, angles un-negated. The composition the detectors placed boxes with
    /// until 2026-09-01; SUPERSEDED for placing anything by <see cref="RefrRotation"/>, of which it is the transpose.
    /// Kept because the Euler sweep self-checks against it and crossprobe pins the one case where the two agree.
    /// Written out rather than composed from three matrices so the order is legible and cannot drift.</summary>
    public static Matrix4x4 EulerZyx(Vec3 rot)
    {
        float cx = MathF.Cos(rot.X), sx = MathF.Sin(rot.X);
        float cy = MathF.Cos(rot.Y), sy = MathF.Sin(rot.Y);
        float cz = MathF.Cos(rot.Z), sz = MathF.Sin(rot.Z);

        // Row-major rows of Rz*Ry*Rx, laid out for Vector3.Transform (which computes v * M with M's rows as basis).
        return new Matrix4x4(
            cz * cy, sz * cy, -sy, 0,
            cz * sy * sx - sz * cx, sz * sy * sx + cz * cx, cy * sx, 0,
            cz * sy * cx + sz * sx, sz * sy * cx - cz * sx, cy * cx, 0,
            0, 0, 0, 1);
    }

    /// <summary>The six ways three Euler angles can be composed, named by the order the axes are APPLIED
    /// (so <see cref="Xyz"/> rotates about X first). Skyrim stores a reference's rotation as three angles and
    /// nothing in the record says which order the engine composes them in — a wrong choice still produces a
    /// plausible-looking box, so it cannot be eyeballed and has to be measured.</summary>
    public enum EulerOrder { Xyz, Xzy, Yxz, Yzx, Zxy, Zyx }

    /// <summary>Compose three axis rotations in a given application order, for <see cref="Vector3.Transform(Vector3,
    /// Matrix4x4)"/>'s row-vector convention (v · M), where "apply A then B" is <c>A * B</c>.</summary>
    public static Matrix4x4 Euler(Vec3 rot, EulerOrder order)
    {
        var x = Matrix4x4.CreateRotationX(rot.X);
        var y = Matrix4x4.CreateRotationY(rot.Y);
        var z = Matrix4x4.CreateRotationZ(rot.Z);
        return order switch
        {
            EulerOrder.Xyz => x * y * z,
            EulerOrder.Xzy => x * z * y,
            EulerOrder.Yxz => y * x * z,
            EulerOrder.Yzx => y * z * x,
            EulerOrder.Zxy => z * x * y,
            EulerOrder.Zyx => z * y * x,
            _ => Matrix4x4.Identity,
        };
    }

    /// <summary>A placed reference's rotation, DERIVED from the engine rather than calibrated against a picture.
    /// Skyrim builds a REFR's local-to-parent matrix with <c>NiMatrix3::SetEulerAnglesXYZ(x, y, z)</c>
    /// (CommonLibVR <c>src/RE/N/NiMatrix3.cpp</c>), whose written-out entries are exactly
    /// <c>(Rz(z)·Ry(y)·Rx(x))ᵀ  =  Rx(−x)·Ry(−y)·Rz(−z)</c> in the ordinary right-handed column-vector sense.
    /// Two independent things fall out of that transpose, and BOTH are needed:
    ///
    ///   1. the angles are NEGATED — Skyrim's REFR angles are clockwise-positive compass headings
    ///      (getangle z: 0 = north, 90 = east) against a counter-clockwise-positive right-handed basis;
    ///   2. the axes are composed Z FIRST, then Y, then X.
    ///
    /// Point 2 is the one that hides. For a ref rotated about a SINGLE axis — which is nearly every static in
    /// the game — order is irrelevant and any composition agrees, so a convention calibrated on yaw-only
    /// geometry passes and stays wrong for everything tilted. Measured 2026-08-30 on the twelve
    /// <c>XJKCoWArchSlimShort</c> arches of one College of Winterhold dome (all three angles non-zero): under
    /// this composition all twelve lean 45° from vertical at a CONSTANT +29.6° off inward-radial — one 12-gon
    /// segment, the arches spanning corner to corner. Under Z-last they scatter over −177°..+145°, which is
    /// what a dome coming apart into free-floating pieces looks like.
    ///
    /// <see cref="Vector3.Transform(Vector3, Matrix4x4)"/>'s row-vector convention reverses the written order,
    /// so "apply Z, then Y, then X" is <see cref="EulerOrder.Zyx"/>.</summary>
    public static Matrix4x4 RefrRotation(Vec3 rotation) =>
        Euler(new Vec3(-rotation.X, -rotation.Y, -rotation.Z), EulerOrder.Zyx);

    /// <summary>The REFR angles (radians, each in [0, 2π)) whose <see cref="RefrRotation"/> is <paramref name="r"/>'s
    /// rotation part: the inverse of that convention, for writing a composed rotation back as a record's three angles.
    /// At the pole (Y = ±90°) X is set to 0 and Z carries the whole turn about the shared axis.</summary>
    public static Vec3 RefrAngles(Matrix4x4 r)
    {
        // RefrRotation = Rz(a)·Ry(b)·Rx(c) in System.Numerics' row-vector form, with a = -z, b = -y, c = -x; its
        // entries M31 = sin b, M32 = -sin c cos b, M33 = cos b cos c, M11 = cos a cos b, M21 = -sin a cos b.
        // b from atan2 of sin b and |cos b| rather than asin: asin loses about half the digits near the poles.
        float sb = Math.Clamp(r.M31, -1f, 1f), cb = MathF.Sqrt(r.M11 * r.M11 + r.M21 * r.M21);
        float b = MathF.Atan2(sb, cb), a, c;
        if (cb > 1e-6f)
        {
            c = MathF.Atan2(-r.M32, r.M33);
            a = MathF.Atan2(-r.M21, r.M11);
        }
        else
        {
            c = 0f;                                   // gimbal: only a ± c is defined; M12 = sin a, M22 = cos a when c = 0
            a = MathF.Atan2(r.M12, r.M22);
        }
        return new Vec3(Wrap(-c), Wrap(-b), Wrap(-a));
        static float Wrap(float v) { var t = v % (2f * MathF.PI); return t < 0f ? t + 2f * MathF.PI : t; }
    }

    /// <summary>Does <paramref name="box"/> intersect the triangle (a,b,c)? Separating-axis test over all 13 axes:
    /// the box's 3 face normals, the triangle's normal, and the 9 edge cross-products. Exact — no tolerance, no
    /// sampling.
    ///
    /// <para>FIXED 2026-09-02. Until then the nine edge-cross tests paired each edge component with the WRONG box
    /// half-extent (for the axis X̂ × e the projection radius is |e.z|·h.y + |e.y|·h.z; the code had |e.z|·h.z +
    /// |e.y|·h.y), which is exact only for a cube. On the severance raster's 32 × 32 × 96 columns it rejected wall
    /// faces whose top edge sat inside the column, so the severance floors measured before this date UNDER-count;
    /// crossprobe's "SAT box" cases were red before the fix and pin it. Found when the route raster's thin column
    /// query returned no ground at all.</para></summary>
    public static bool TriangleIntersectsBox(Vector3 a, Vector3 b, Vector3 c, Aabb box)
    {
        if (box.IsEmpty) return false;

        var center = new Vector3((box.Min.X + box.Max.X) * 0.5f, (box.Min.Y + box.Max.Y) * 0.5f, (box.Min.Z + box.Max.Z) * 0.5f);
        var h = new Vector3((box.Max.X - box.Min.X) * 0.5f, (box.Max.Y - box.Min.Y) * 0.5f, (box.Max.Z - box.Min.Z) * 0.5f);

        var v0 = a - center; var v1 = b - center; var v2 = c - center;
        var e0 = v1 - v0; var e1 = v2 - v1; var e2 = v0 - v2;

        // 9 edge cross-product axes (box axes x triangle edges), written out — the standard Akenine-Möller form.
        if (!AxisTest(v0.Z, v0.Y, v1.Z, v1.Y, v2.Z, v2.Y, e0.Z, e0.Y, h.Z, h.Y)) return false;
        if (!AxisTest(v0.Z, v0.Y, v1.Z, v1.Y, v2.Z, v2.Y, e1.Z, e1.Y, h.Z, h.Y)) return false;
        if (!AxisTest(v0.Z, v0.Y, v1.Z, v1.Y, v2.Z, v2.Y, e2.Z, e2.Y, h.Z, h.Y)) return false;

        if (!AxisTest(v0.X, v0.Z, v1.X, v1.Z, v2.X, v2.Z, e0.X, e0.Z, h.X, h.Z)) return false;
        if (!AxisTest(v0.X, v0.Z, v1.X, v1.Z, v2.X, v2.Z, e1.X, e1.Z, h.X, h.Z)) return false;
        if (!AxisTest(v0.X, v0.Z, v1.X, v1.Z, v2.X, v2.Z, e2.X, e2.Z, h.X, h.Z)) return false;

        if (!AxisTest(v0.Y, v0.X, v1.Y, v1.X, v2.Y, v2.X, e0.Y, e0.X, h.Y, h.X)) return false;
        if (!AxisTest(v0.Y, v0.X, v1.Y, v1.X, v2.Y, v2.X, e1.Y, e1.X, h.Y, h.X)) return false;
        if (!AxisTest(v0.Y, v0.X, v1.Y, v1.X, v2.Y, v2.X, e2.Y, e2.X, h.Y, h.X)) return false;

        // 3 box face normals — the triangle's AABB must overlap the box on every axis.
        if (MathF.Min(v0.X, MathF.Min(v1.X, v2.X)) > h.X || MathF.Max(v0.X, MathF.Max(v1.X, v2.X)) < -h.X) return false;
        if (MathF.Min(v0.Y, MathF.Min(v1.Y, v2.Y)) > h.Y || MathF.Max(v0.Y, MathF.Max(v1.Y, v2.Y)) < -h.Y) return false;
        if (MathF.Min(v0.Z, MathF.Min(v1.Z, v2.Z)) > h.Z || MathF.Max(v0.Z, MathF.Max(v1.Z, v2.Z)) < -h.Z) return false;

        // 1 triangle normal — plane vs box.
        var n = Vector3.Cross(e0, e1);
        float d = Vector3.Dot(n, v0);
        float rr = h.X * MathF.Abs(n.X) + h.Y * MathF.Abs(n.Y) + h.Z * MathF.Abs(n.Z);
        return MathF.Abs(d) <= rr;

        static bool AxisTest(float a0, float a1, float b0, float b1, float c0, float c1,
                             float ea, float eb, float ha, float hb)
        {
            float p0 = ea * a1 - eb * a0;
            float p1 = ea * b1 - eb * b0;
            float p2 = ea * c1 - eb * c0;
            float min = MathF.Min(p0, MathF.Min(p1, p2));
            float max = MathF.Max(p0, MathF.Max(p1, p2));
            float rad = MathF.Abs(ea) * hb + MathF.Abs(eb) * ha;
            return !(min > rad || max < -rad);
        }
    }

    /// <summary>Grow <paramref name="box"/> DOWNWARD by <paramref name="height"/>.
    ///
    /// <para>This is the exact reduction of "does the object intersect the volume an actor occupies while standing on
    /// this triangle?" — that volume is the triangle swept up by the actor's height, and for a swept volume
    /// <c>Box ∩ (Tri ⊕ S) ≠ ∅ ⟺ (Box ⊕ −S) ∩ Tri ≠ ∅</c>. Since S is a vertical segment, <c>Box ⊕ −S</c> is just the
    /// box with its floor lowered. So the expensive prism test collapses into the plain triangle-vs-box test above,
    /// with no approximation whatsoever.</para></summary>
    public static Aabb GrowDown(Aabb box, float height) => box.IsEmpty
        ? box
        : new Aabb(new Vec3(box.Min.X, box.Min.Y, box.Min.Z - height), box.Max, false);

    /// <summary>Shrink a box toward its centre on X and Y only — the horizontal inset that turns "touches at all" into
    /// "meaningfully covers". An object grazing a triangle edge by a hair is not blocking it, and the inset is the
    /// caller's knob for saying how much grazing to forgive. Returns empty if the inset consumes the box.</summary>
    public static Aabb InsetHorizontal(Aabb box, float inset)
    {
        if (box.IsEmpty || inset <= 0) return box;
        float minX = box.Min.X + inset, maxX = box.Max.X - inset;
        float minY = box.Min.Y + inset, maxY = box.Max.Y - inset;
        if (minX >= maxX || minY >= maxY) return Aabb.Empty;
        return new Aabb(new Vec3(minX, minY, box.Min.Z), new Vec3(maxX, maxY, box.Max.Z), false);
    }

    /// <summary>Shrink a triangle inward by <paramref name="inset"/> units, measured perpendicular to every edge.
    ///
    /// <para>For a triangle this offset is EXACT and closed-form: offsetting all three edges inward by <c>d</c> yields
    /// a similar triangle about the incentre with inradius <c>r − d</c>, so the result is the original scaled about its
    /// incentre by <c>(r − d) / r</c>. Returns false when the inset consumes the triangle — a sliver narrower than the
    /// tolerance cannot be judged, and saying so is better than judging it wrongly.</para>
    ///
    /// <para>WHY A SURFACE TEST NEEDS THIS. Navmesh is authored flush against walls: a triangle's edge lies ON the wall
    /// face. A surface-crossing test with no inset therefore fires on every well-built navmesh in the game, because
    /// grazing the boundary and cutting through the middle are the same event to it. The inset is the caller's knob for
    /// "how far past the edge must the surface reach before it is blocking rather than bounding".</para></summary>
    public static bool InsetTriangle(Vector3 a, Vector3 b, Vector3 c, float inset,
                                     out Vector3 ia, out Vector3 ib, out Vector3 ic)
    {
        ia = a; ib = b; ic = c;
        if (inset <= 0) return true;

        float la = (b - c).Length(), lb = (c - a).Length(), lc = (a - b).Length();
        float per = la + lb + lc;
        if (per <= 1e-6f) return false;

        // Incentre is the side-length-weighted vertex average; inradius = 2*area/perimeter.
        var incentre = (a * la + b * lb + c * lc) / per;
        float area2 = Vector3.Cross(b - a, c - a).Length();
        float r = area2 / per;                       // (area2/2) / (per/2)
        if (r <= inset) return false;

        float k = (r - inset) / r;
        ia = incentre + (a - incentre) * k;
        ib = incentre + (b - incentre) * k;
        ic = incentre + (c - incentre) * k;
        return true;
    }

    /// <summary>Does the object triangle (q0,q1,q2) intersect the vertical prism raised from the triangle (p0,p1,p2)?
    /// Separating-axis test over all 17 axes — the prism's 4 distinct face normals, the triangle's normal, and the 12
    /// edge cross-products. Exact, no sampling.
    ///
    /// <para>THIS IS THE PRIMITIVE VERTEX-CONTAINMENT SHOULD HAVE BEEN. Asking "are the navmesh triangle's vertices
    /// inside the object" cannot see a thin blocker: a wall crossing a triangle leaves all three vertices in open air
    /// on either side, and only the middle of the triangle is cut. Asking instead whether the object's SURFACE crosses
    /// the volume an actor standing on that triangle would occupy sees the wall and does not see the floor.</para>
    ///
    /// <para>The prism is <c>base ⊕ [0,height]·ẑ</c>, so its projection on any axis is the base's projection extended by
    /// <c>height · axis.Z</c> — six vertices are never materialised.</para>
    ///
    /// <param name="tolerance">Overlap along an axis smaller than this reads as separated. Surfaces that merely touch —
    /// a navmesh edge flush with a wall face, a floor tile the navmesh sits exactly on — are boundary contact, not
    /// blockage, and without a tolerance floating-point noise decides which way they land.</param></summary>
    public static bool TriangleIntersectsPrism(Vector3 q0, Vector3 q1, Vector3 q2,
                                               Vector3 p0, Vector3 p1, Vector3 p2,
                                               float height, float tolerance = 0.5f)
    {
        var pe0 = p1 - p0; var pe1 = p2 - p1; var pe2 = p0 - p2;
        var qe0 = q1 - q0; var qe1 = q2 - q1; var qe2 = q0 - q2;
        var z = new Vector3(0, 0, 1);

        // Prism face normals: the base's own normal, and one horizontal normal per vertical side.
        if (Separated(Vector3.Cross(pe0, -pe2))) return false;
        if (Separated(Vector3.Cross(pe0, z))) return false;
        if (Separated(Vector3.Cross(pe1, z))) return false;
        if (Separated(Vector3.Cross(pe2, z))) return false;

        // The object triangle's own normal.
        if (Separated(Vector3.Cross(qe0, -qe2))) return false;

        // Edge x edge. The prism's distinct edge directions are the three base edges plus vertical.
        if (Separated(Vector3.Cross(pe0, qe0))) return false;
        if (Separated(Vector3.Cross(pe0, qe1))) return false;
        if (Separated(Vector3.Cross(pe0, qe2))) return false;
        if (Separated(Vector3.Cross(pe1, qe0))) return false;
        if (Separated(Vector3.Cross(pe1, qe1))) return false;
        if (Separated(Vector3.Cross(pe1, qe2))) return false;
        if (Separated(Vector3.Cross(pe2, qe0))) return false;
        if (Separated(Vector3.Cross(pe2, qe1))) return false;
        if (Separated(Vector3.Cross(pe2, qe2))) return false;
        if (Separated(Vector3.Cross(z, qe0))) return false;
        if (Separated(Vector3.Cross(z, qe1))) return false;
        if (Separated(Vector3.Cross(z, qe2))) return false;

        return true;

        bool Separated(Vector3 axis)
        {
            float lsq = axis.LengthSquared();
            if (lsq < 1e-12f) return false;             // parallel edges give a null axis — it separates nothing

            float a0 = Vector3.Dot(axis, p0), a1 = Vector3.Dot(axis, p1), a2 = Vector3.Dot(axis, p2);
            float pmin = MathF.Min(a0, MathF.Min(a1, a2)), pmax = MathF.Max(a0, MathF.Max(a1, a2));
            float dz = axis.Z * height;
            if (dz < 0) pmin += dz; else pmax += dz;

            float b0 = Vector3.Dot(axis, q0), b1 = Vector3.Dot(axis, q1), b2 = Vector3.Dot(axis, q2);
            float qmin = MathF.Min(b0, MathF.Min(b1, b2)), qmax = MathF.Max(b0, MathF.Max(b1, b2));

            float slack = tolerance * MathF.Sqrt(lsq);  // tolerance is in world units, the axis is unnormalised
            return pmin >= qmax - slack || qmin >= pmax - slack;
        }
    }
}

/// <summary>Where a point sits relative to a mesh, measured by casting a vertical ray through it.</summary>
public enum ColumnPosition
{
    /// <summary>No surface above or below this (x,y) — the point is outside the mesh's footprint entirely.</summary>
    Outside,
    /// <summary>Above every surface in this column — standing ON the object. A navmesh over a dirt mound or a floor
    /// tile lands here, and it is NOT blocked.</summary>
    Above,
    /// <summary>Below every surface — under the object (a navmesh beneath an overhanging rock). Not blocked.</summary>
    Below,
    /// <summary>Between surfaces — the point is INSIDE the object's solid extent at this column. This is the one that
    /// means blockage: an actor standing here is inside a wall.</summary>
    Inside,
}

/// <summary>A 2D-bucketed triangle set that answers "what surface heights does this mesh have at (x,y)?".
///
/// <para>THE REASON THIS EXISTS. An AABB test flags a dirt mound whose navmesh is authored across its top surface just
/// as loudly as a wall a navmesh runs through — both lie inside the box. Calibrated against vanilla Skyrim, where
/// objects and navmesh were authored together and every hit is by definition a false positive, the AABB test flagged
/// 43% of references. Useless. Casting a vertical ray and asking whether the point is ON the surface or INSIDE it is
/// the distinction that separates them.</para></summary>
public sealed class MeshColumnIndex
{
    readonly Vector3[] _v;
    readonly int[] _i;
    readonly string?[]? _materials;
    readonly Dictionary<(int, int), List<int>> _grid = new();   // bucket -> triangle ordinals
    readonly float _cell;

    public int TriangleCount => _i.Length / 3;

    MeshColumnIndex(Vector3[] v, int[] i, float cell, string?[]? materials) { _v = v; _i = i; _cell = cell; _materials = materials; }

    /// <summary>Index a triangle soup already transformed into the space the queries will use.</summary>
    public static MeshColumnIndex Build(Vector3[] verts, int[] indices, float cellSize = 64f, string?[]? triangleMaterials = null)
    {
        if (triangleMaterials is not null && triangleMaterials.Length != indices.Length / 3)
            throw new ArgumentException("Material count must equal triangle count", nameof(triangleMaterials));
        var idx = new MeshColumnIndex(verts, indices, cellSize, triangleMaterials);
        for (int t = 0; t < indices.Length / 3; t++)
        {
            var a = verts[indices[t * 3]]; var b = verts[indices[t * 3 + 1]]; var c = verts[indices[t * 3 + 2]];
            int x0 = (int)MathF.Floor(MathF.Min(a.X, MathF.Min(b.X, c.X)) / cellSize);
            int x1 = (int)MathF.Floor(MathF.Max(a.X, MathF.Max(b.X, c.X)) / cellSize);
            int y0 = (int)MathF.Floor(MathF.Min(a.Y, MathF.Min(b.Y, c.Y)) / cellSize);
            int y1 = (int)MathF.Floor(MathF.Max(a.Y, MathF.Max(b.Y, c.Y)) / cellSize);
            if ((long)(x1 - x0 + 1) * (y1 - y0 + 1) > 1024) continue;    // degenerate sliver — skip rather than blow up the index
            for (int gx = x0; gx <= x1; gx++)
                for (int gy = y0; gy <= y1; gy++)
                {
                    if (!idx._grid.TryGetValue((gx, gy), out var list)) idx._grid[(gx, gy)] = list = new List<int>();
                    list.Add(t);
                }
        }
        return idx;
    }

    /// <summary>Classify a point by casting a vertical ray through the mesh at its (x,y) and reading the FACING of the
    /// nearest surface below it.
    ///
    /// <para>Why facing and not a min/max span: a Skyrim room piece is a hollow SHELL — floor, walls, ceiling in one
    /// mesh. A span test sees floor as the low surface and ceiling as the high one and calls the entire room interior
    /// "inside the mesh", which is true of the bounding volume and false of the material. Reading the nearest surface
    /// below instead answers the question that actually matters: is the thing directly under this point something you
    /// STAND ON (an up-facing floor, so the point is in open air) or the UNDERSIDE of something (a down-facing face, so
    /// the point is embedded in material)?</para>
    ///
    /// <para><paramref name="tolerance"/> forgives a point sitting fractionally inside a surface — navmesh is authored
    /// to hug geometry, so an exact test would call every well-placed navmesh triangle "inside".</para></summary>
    public ColumnPosition Classify(Vector3 p, float tolerance = 8f)
    {
        int gx = (int)MathF.Floor(p.X / _cell), gy = (int)MathF.Floor(p.Y / _cell);
        if (!_grid.TryGetValue((gx, gy), out var list)) return ColumnPosition.Outside;

        float bestBelow = float.MinValue; bool belowUp = false, anyBelow = false;
        bool anyAbove = false, anyHit = false;

        foreach (var t in list)
        {
            var a = _v[_i[t * 3]]; var b = _v[_i[t * 3 + 1]]; var c = _v[_i[t * 3 + 2]];
            if (BarycentricZ(a, b, c, p.X, p.Y) is not { } z) continue;
            anyHit = true;

            float nz = Vector3.Cross(b - a, c - a).Z;          // >0 = up-facing, <0 = down-facing
            if (z <= p.Z + tolerance)
            {
                if (z > bestBelow) { bestBelow = z; belowUp = nz >= 0; anyBelow = true; }
            }
            else anyAbove = true;
        }

        if (!anyHit) return ColumnPosition.Outside;
        if (!anyBelow) return ColumnPosition.Below;            // everything is above — the point is under the mesh
        if (belowUp) return ColumnPosition.Above;              // standing on an up-facing surface: open air
        return anyAbove ? ColumnPosition.Inside : ColumnPosition.Above;
    }

    /// <summary>Every surface the mesh presents at (x,y) — the height of each triangle whose plan projection covers the
    /// point, and whether it faces UP (n.Z ≥ 0 by the soup's winding: the face an actor stands on, as
    /// <see cref="Classify"/> reads it). The raw column, appended to <paramref name="into"/>, for a caller that decides
    /// which surface is the floor (<see cref="NavmeshFloor"/>). A triangle with no plan area (a wall face seen edge-on)
    /// presents no surface.</summary>
    public void SurfacesAt(float x, float y, List<(float Z, bool Up)> into)
    {
        int gx = (int)MathF.Floor(x / _cell), gy = (int)MathF.Floor(y / _cell);
        if (!_grid.TryGetValue((gx, gy), out var list)) return;
        foreach (var t in list)
        {
            var a = _v[_i[t * 3]]; var b = _v[_i[t * 3 + 1]]; var c = _v[_i[t * 3 + 2]];
            if (BarycentricZ(a, b, c, x, y) is not { } z) continue;
            into.Add((z, Vector3.Cross(b - a, c - a).Z >= 0));
        }
    }

    /// <summary>The lowest and highest surface height the mesh presents at (x,y), from every triangle whose 2D
    /// projection contains the point. <c>any=false</c> when the column misses the mesh entirely.</summary>
    public (float Lo, float Hi, bool Any) SurfaceSpan(float x, float y)
    {
        int gx = (int)MathF.Floor(x / _cell), gy = (int)MathF.Floor(y / _cell);
        if (!_grid.TryGetValue((gx, gy), out var list)) return (0, 0, false);

        float lo = float.MaxValue, hi = float.MinValue;
        bool any = false;
        foreach (var t in list)
        {
            var a = _v[_i[t * 3]]; var b = _v[_i[t * 3 + 1]]; var c = _v[_i[t * 3 + 2]];
            if (BarycentricZ(a, b, c, x, y) is not { } z) continue;
            if (z < lo) lo = z;
            if (z > hi) hi = z;
            any = true;
        }
        return any ? (lo, hi, true) : (0, 0, false);
    }

    /// <summary>Does any of the mesh's triangles intersect <paramref name="column"/>? The actor-column test the
    /// severance raster runs per cell, exposed on its own so a caller rasterising a REGION (a route window around an
    /// object, not one navmesh triangle) asks the same question with the same SAT. Exact; the grid only narrows.</summary>
    public bool BlocksColumn(Aabb column)
    {
        if (column.IsEmpty) return false;
        int gx0 = (int)MathF.Floor(column.Min.X / _cell), gx1 = (int)MathF.Floor(column.Max.X / _cell);
        int gy0 = (int)MathF.Floor(column.Min.Y / _cell), gy1 = (int)MathF.Floor(column.Max.Y / _cell);
        _mark ??= new int[TriangleCount];
        if (++_epoch == int.MaxValue) { Array.Clear(_mark); _epoch = 1; }
        for (int gx = gx0; gx <= gx1; gx++)
            for (int gy = gy0; gy <= gy1; gy++)
            {
                if (!_grid.TryGetValue((gx, gy), out var list)) continue;
                foreach (var t in list)
                {
                    if (_mark[t] == _epoch) continue;
                    _mark[t] = _epoch;
                    var qa = _v[_i[t * 3]]; var qb = _v[_i[t * 3 + 1]]; var qc = _v[_i[t * 3 + 2]];
                    if (MathF.Min(qa.Z, MathF.Min(qb.Z, qc.Z)) > column.Max.Z || MathF.Max(qa.Z, MathF.Max(qb.Z, qc.Z)) < column.Min.Z) continue;
                    if (SpatialOverlap.TriangleIntersectsBox(qa, qb, qc, column)) return true;
                }
            }
        return false;
    }

    /// <summary>Does any triangle come within <paramref name="radius"/> IN PLAN of (<paramref name="cx"/>, <paramref name="cy"/>)
    /// while inside the height slab [<paramref name="zLo"/>, <paramref name="zHi"/>]? The capsule test the route-cut rule
    /// sweeps along a path line (roadmap 4.7, the rule Part C returned): a disc of the walker's capsule radius, extruded
    /// through the actor band. Exact — each candidate triangle is clipped to the slab and the clipped polygon's plan
    /// distance to the point is compared to the radius — so a face 1 u outside the disc is not a cut and a face 1 u
    /// inside it is (Part C's "line clears the block by 1 u; straight through"); the grid only narrows.</summary>
    public bool IntersectsDiscInSlab(float cx, float cy, float radius, float zLo, float zHi)
        => IntersectsDiscInSlab(cx, cy, radius, zLo, zHi, null, out _);

    /// <summary>Also reports whether every contacting triangle satisfies the caller's material predicate. Unknown
    /// material is passed as null. Only triangles inside this disc and slab participate; another part of a mixed
    /// mesh cannot grant an exception to the contact being tested.</summary>
    public bool IntersectsDiscInSlab(float cx, float cy, float radius, float zLo, float zHi,
                                     Func<string?, bool>? materialMatches, out bool allMaterialsMatch)
    {
        allMaterialsMatch = false;
        bool hit = false, allMatch = true;
        if (zHi <= zLo || radius < 0f) return false;
        int gx0 = (int)MathF.Floor((cx - radius) / _cell), gx1 = (int)MathF.Floor((cx + radius) / _cell);
        int gy0 = (int)MathF.Floor((cy - radius) / _cell), gy1 = (int)MathF.Floor((cy + radius) / _cell);
        _mark ??= new int[TriangleCount];
        if (++_epoch == int.MaxValue) { Array.Clear(_mark); _epoch = 1; }
        float r2 = radius * radius;
        Span<Vector3> poly = stackalloc Vector3[8];   // a triangle clipped by two planes has at most 5 corners
        Span<Vector3> tmp = stackalloc Vector3[8];
        for (int gx = gx0; gx <= gx1; gx++)
            for (int gy = gy0; gy <= gy1; gy++)
            {
                if (!_grid.TryGetValue((gx, gy), out var list)) continue;
                foreach (var t in list)
                {
                    if (_mark[t] == _epoch) continue;
                    _mark[t] = _epoch;
                    var qa = _v[_i[t * 3]]; var qb = _v[_i[t * 3 + 1]]; var qc = _v[_i[t * 3 + 2]];
                    if (MathF.Min(qa.Z, MathF.Min(qb.Z, qc.Z)) > zHi || MathF.Max(qa.Z, MathF.Max(qb.Z, qc.Z)) < zLo) continue;
                    if (MathF.Min(qa.X, MathF.Min(qb.X, qc.X)) > cx + radius || MathF.Max(qa.X, MathF.Max(qb.X, qc.X)) < cx - radius) continue;
                    if (MathF.Min(qa.Y, MathF.Min(qb.Y, qc.Y)) > cy + radius || MathF.Max(qa.Y, MathF.Max(qb.Y, qc.Y)) < cy - radius) continue;
                    poly[0] = qa; poly[1] = qb; poly[2] = qc;
                    int n = ClipHalfSpace(poly, 3, tmp, zLo, keepAbove: true);
                    if (n == 0) continue;
                    n = ClipHalfSpace(poly, n, tmp, zHi, keepAbove: false);
                    if (n == 0) continue;
                    if (PlanDistanceSquared(poly, n, cx, cy) <= r2)
                    {
                        if (materialMatches is null) return true;
                        hit = true;
                        allMatch &= materialMatches(_materials?[t]);
                    }
                }
            }
        allMaterialsMatch = hit && allMatch;
        return hit;
    }

    /// <summary>The lowest height at which the mesh presents material within <paramref name="radius"/> IN PLAN of
    /// (<paramref name="cx"/>, <paramref name="cy"/>) at or above <paramref name="zFloor"/> — the underside the walker's
    /// capsule meets: the floor-bottom term of the route-cut rule (Q25 measured, Q26 folded, 2026-09-05: a beam whose
    /// underside floats about 20 u or more above the feet is a wall to the character controller whatever its top). Each
    /// candidate triangle is clipped to the half-space above the floor (material buried in the floor is the floor's), and
    /// the least height over the part of the clipped polygon inside the disc is taken exactly: a linear height over a
    /// convex region is least at a corner inside the disc, where an edge meets the circle, or on the arc at the point
    /// facing down the slope — so a beam standing on the floor reads the floor whatever its top, a beam lifted whole reads
    /// its underside, and a sloped face reads its lowest point under the capsule, not its lowest point anywhere. NaN when
    /// no material lies within the disc at or above the floor.</summary>
    public float LowestSurfaceInDisc(float cx, float cy, float radius, float zFloor)
    {
        if (radius < 0f) return float.NaN;
        int gx0 = (int)MathF.Floor((cx - radius) / _cell), gx1 = (int)MathF.Floor((cx + radius) / _cell);
        int gy0 = (int)MathF.Floor((cy - radius) / _cell), gy1 = (int)MathF.Floor((cy + radius) / _cell);
        _mark ??= new int[TriangleCount];
        if (++_epoch == int.MaxValue) { Array.Clear(_mark); _epoch = 1; }
        float r2 = radius * radius, best = float.NaN;
        Span<Vector3> poly = stackalloc Vector3[8];
        Span<Vector3> tmp = stackalloc Vector3[8];
        for (int gx = gx0; gx <= gx1; gx++)
            for (int gy = gy0; gy <= gy1; gy++)
            {
                if (!_grid.TryGetValue((gx, gy), out var list)) continue;
                foreach (var t in list)
                {
                    if (_mark[t] == _epoch) continue;
                    _mark[t] = _epoch;
                    var qa = _v[_i[t * 3]]; var qb = _v[_i[t * 3 + 1]]; var qc = _v[_i[t * 3 + 2]];
                    if (MathF.Max(qa.Z, MathF.Max(qb.Z, qc.Z)) < zFloor) continue;                      // wholly in the floor
                    if (!float.IsNaN(best) && MathF.Max(MathF.Min(qa.Z, MathF.Min(qb.Z, qc.Z)), zFloor) >= best) continue;   // cannot go lower
                    if (MathF.Min(qa.X, MathF.Min(qb.X, qc.X)) > cx + radius || MathF.Max(qa.X, MathF.Max(qb.X, qc.X)) < cx - radius) continue;
                    if (MathF.Min(qa.Y, MathF.Min(qb.Y, qc.Y)) > cy + radius || MathF.Max(qa.Y, MathF.Max(qb.Y, qc.Y)) < cy - radius) continue;
                    poly[0] = qa; poly[1] = qb; poly[2] = qc;
                    int n = ClipHalfSpace(poly, 3, tmp, zFloor, keepAbove: true);
                    if (n == 0) continue;
                    float z = LowestInDisc(poly, n, Vector3.Cross(qb - qa, qc - qa), cx, cy, radius, r2);
                    if (!float.IsNaN(z) && (float.IsNaN(best) || z < best)) best = z;
                }
            }
        return best;
    }

    // The least height of a planar convex polygon (a clipped triangle) over the part of it inside a plan disc: a corner
    // inside the disc, an edge's crossing of the circle, or the arc's point facing down the plane's slope (the centre when
    // the plane is level). A face seen edge-on (no plan area) is covered by its corners and crossings alone. NaN when the
    // polygon and the disc share no plan point.
    static float LowestInDisc(ReadOnlySpan<Vector3> poly, int n, Vector3 normal, float cx, float cy, float r, float r2)
    {
        float best = float.NaN, zLo = float.MaxValue, zHi = float.MinValue;
        for (int i = 0; i < n; i++)
        {
            var p = poly[i];
            zLo = MathF.Min(zLo, p.Z); zHi = MathF.Max(zHi, p.Z);
            float dx = p.X - cx, dy = p.Y - cy;
            if (dx * dx + dy * dy <= r2 && (float.IsNaN(best) || p.Z < best)) best = p.Z;
            if (n < 2) break;
            var q = poly[(i + 1) % n];
            float ex = q.X - p.X, ey = q.Y - p.Y, ez = q.Z - p.Z;
            float a = ex * ex + ey * ey;
            if (a < 1e-12f) continue;
            float b = 2f * (dx * ex + dy * ey), c = dx * dx + dy * dy - r2;
            float disc = b * b - 4f * a * c;
            if (disc < 0f) continue;
            float s = MathF.Sqrt(disc);
            float t0 = (-b - s) / (2f * a), t1 = (-b + s) / (2f * a);
            if (t0 >= 0f && t0 <= 1f) { float z = p.Z + ez * t0; if (float.IsNaN(best) || z < best) best = z; }
            if (t1 >= 0f && t1 <= 1f) { float z = p.Z + ez * t1; if (float.IsNaN(best) || z < best) best = z; }
        }
        if (n >= 3 && MathF.Abs(normal.Z) > 1e-9f)
        {
            float gx = -normal.X / normal.Z, gy = -normal.Y / normal.Z, gl = MathF.Sqrt(gx * gx + gy * gy);
            float ax = gl > 1e-9f ? cx - r * gx / gl : cx, ay = gl > 1e-9f ? cy - r * gy / gl : cy;
            if (InsidePlan(poly, n, ax, ay))
            {
                float z = poly[0].Z + gx * (ax - poly[0].X) + gy * (ay - poly[0].Y);
                // a point inside the polygon lies between its corners' heights; a sliver's arithmetic that does not is refused
                if (z >= zLo - 1e-3f && z <= zHi + 1e-3f && (float.IsNaN(best) || z < best)) best = z;
            }
        }
        return best;
    }

    // Plan point-in-convex-polygon, either winding: every edge's cross product has one sign (or is zero).
    static bool InsidePlan(ReadOnlySpan<Vector3> poly, int n, float x, float y)
    {
        bool pos = false, neg = false;
        for (int i = 0; i < n; i++)
        {
            var p = poly[i]; var q = poly[(i + 1) % n];
            float c = (q.X - p.X) * (y - p.Y) - (q.Y - p.Y) * (x - p.X);
            if (c > 1e-6f) pos = true; else if (c < -1e-6f) neg = true;
        }
        return !(pos && neg);
    }

    // Sutherland–Hodgman against one horizontal plane. The polygon is rewritten in place; tmp is scratch.
    static int ClipHalfSpace(Span<Vector3> poly, int n, Span<Vector3> tmp, float z, bool keepAbove)
    {
        int m = 0;
        for (int i = 0; i < n; i++)
        {
            var p = poly[i]; var q = poly[(i + 1) % n];
            bool pin = keepAbove ? p.Z >= z : p.Z <= z;
            bool qin = keepAbove ? q.Z >= z : q.Z <= z;
            if (pin) tmp[m++] = p;
            if (pin != qin) tmp[m++] = p + (q - p) * ((z - p.Z) / (q.Z - p.Z));
        }
        for (int i = 0; i < m; i++) poly[i] = tmp[i];
        return m;
    }

    // Squared plan distance from a point to a convex polygon (the clipped triangle): zero inside, else the nearest
    // edge. The inside test is skipped for a polygon with no plan area (a wall face seen edge-on projects to a
    // segment, and every cross product is zero for every point), which is exactly the case the edge distance covers.
    static float PlanDistanceSquared(ReadOnlySpan<Vector3> poly, int n, float px, float py)
    {
        if (n == 1) { float dx = poly[0].X - px, dy = poly[0].Y - py; return dx * dx + dy * dy; }
        if (n >= 3)
        {
            float area2 = 0f;
            for (int i = 1; i + 1 < n; i++)
                area2 += (poly[i].X - poly[0].X) * (poly[i + 1].Y - poly[0].Y) - (poly[i].Y - poly[0].Y) * (poly[i + 1].X - poly[0].X);
            if (MathF.Abs(area2) > 1e-6f)
            {
                bool pos = false, neg = false;
                for (int i = 0; i < n; i++)
                {
                    var a = poly[i]; var b = poly[(i + 1) % n];
                    float c = (b.X - a.X) * (py - a.Y) - (b.Y - a.Y) * (px - a.X);
                    if (c > 1e-6f) pos = true; else if (c < -1e-6f) neg = true;
                }
                if (!(pos && neg)) return 0f;
            }
        }
        float best = float.MaxValue;
        for (int i = 0; i < n; i++)
        {
            var a = poly[i]; var b = poly[(i + 1) % n];
            float ex = b.X - a.X, ey = b.Y - a.Y, len2 = ex * ex + ey * ey;
            float t = len2 < 1e-9f ? 0f : Math.Clamp(((px - a.X) * ex + (py - a.Y) * ey) / len2, 0f, 1f);
            float dx = a.X + ex * t - px, dy = a.Y + ey * t - py;
            best = MathF.Min(best, dx * dx + dy * dy);
        }
        return best;
    }

    /// <summary>The triangle's height at (x,y), or null if the point is outside its 2D projection. Degenerate
    /// triangles (zero projected area — a wall seen edge-on) return null: they have no height to report, and dividing
    /// by their area would produce an infinity that poisons the span.</summary>
    static float? BarycentricZ(Vector3 a, Vector3 b, Vector3 c, float x, float y)
    {
        float d = (b.Y - c.Y) * (a.X - c.X) + (c.X - b.X) * (a.Y - c.Y);
        if (MathF.Abs(d) < 1e-6f) return null;
        float u = ((b.Y - c.Y) * (x - c.X) + (c.X - b.X) * (y - c.Y)) / d;
        float v = ((c.Y - a.Y) * (x - c.X) + (a.X - c.X) * (y - c.Y)) / d;
        float w = 1f - u - v;
        if (u < -1e-4f || v < -1e-4f || w < -1e-4f) return null;
        return u * a.Z + v * b.Z + w * c.Z;
    }

    /// <summary>How many of the mesh's triangles CUT ACROSS the volume an actor occupies while standing on the navmesh
    /// triangle (a,b,c)? The surface-crossing narrow phase.
    ///
    /// <para>WHY THIS REPLACES <see cref="Classify"/> FOR BLOCKAGE. Containment asks whether the navmesh triangle's
    /// vertices are inside the object. A wall — the single most likely thing to block a path — cannot put them there:
    /// the triangle straddles it, both vertices in open air, only the middle crossing. Containment is structurally
    /// blind to thin blockers. Crossing asks the question that actually decides whether an actor can walk the triangle:
    /// is there object material standing in the column above it?</para>
    ///
    /// <para>Three parameters, and every one of them earns its place against a specific false positive:</para>
    /// <list type="bullet">
    /// <item><paramref name="clearance"/> — the prism starts this far ABOVE the navmesh surface. Navmesh is authored to
    /// hug the ground, and a faceted rock or dirt mound pokes through the coarse triangulation laid over it. Without a
    /// clearance every terrain static in the game is "crossing" its own navmesh. Physically this is step height.</item>
    /// <item><paramref name="inset"/> — the triangle is shrunk perpendicular to its edges first, because navmesh is
    /// authored flush against walls and a boundary graze is not a blockage.</item>
    /// <item><paramref name="maxUpFacing"/> — count only surfaces steeper than this (|n·ẑ| / |n|). 1 counts every
    /// surface; smaller values keep walls and drop floors and ledges. A knob, deliberately not a decision.</item>
    /// </list>
    ///
    /// <para>Returns −1 when the inset consumed the triangle: it is UNJUDGEABLE, which is not the same as clear, and a
    /// caller that conflates the two will report a sliver-heavy navmesh as clean.</para></summary>
    public int CountSurfaceCrossings(Vector3 a, Vector3 b, Vector3 c,
                                     float clearance = 24f, float height = 128f, float inset = 8f,
                                     float maxUpFacing = 1f, float tolerance = 0.5f, int stopAfter = int.MaxValue)
    {
        if (!SpatialOverlap.InsetTriangle(a, b, c, inset, out var pa, out var pb, out var pc)) return -1;

        float ph = height - clearance;
        if (ph <= 0) return 0;
        pa.Z += clearance; pb.Z += clearance; pc.Z += clearance;

        float minX = MathF.Min(pa.X, MathF.Min(pb.X, pc.X)), maxX = MathF.Max(pa.X, MathF.Max(pb.X, pc.X));
        float minY = MathF.Min(pa.Y, MathF.Min(pb.Y, pc.Y)), maxY = MathF.Max(pa.Y, MathF.Max(pb.Y, pc.Y));
        float minZ = MathF.Min(pa.Z, MathF.Min(pb.Z, pc.Z)), maxZ = MathF.Max(pa.Z, MathF.Max(pb.Z, pc.Z)) + ph;

        _mark ??= new int[TriangleCount];
        if (++_epoch == int.MaxValue) { Array.Clear(_mark); _epoch = 1; }

        int found = 0;
        int gx0 = (int)MathF.Floor(minX / _cell), gx1 = (int)MathF.Floor(maxX / _cell);
        int gy0 = (int)MathF.Floor(minY / _cell), gy1 = (int)MathF.Floor(maxY / _cell);

        for (int gx = gx0; gx <= gx1; gx++)
            for (int gy = gy0; gy <= gy1; gy++)
            {
                if (!_grid.TryGetValue((gx, gy), out var list)) continue;
                foreach (var t in list)
                {
                    if (_mark[t] == _epoch) continue;
                    _mark[t] = _epoch;

                    var qa = _v[_i[t * 3]]; var qb = _v[_i[t * 3 + 1]]; var qc = _v[_i[t * 3 + 2]];

                    // Cheap AABB reject first — most candidates in a bucket miss the prism outright.
                    if (MathF.Min(qa.X, MathF.Min(qb.X, qc.X)) > maxX || MathF.Max(qa.X, MathF.Max(qb.X, qc.X)) < minX) continue;
                    if (MathF.Min(qa.Y, MathF.Min(qb.Y, qc.Y)) > maxY || MathF.Max(qa.Y, MathF.Max(qb.Y, qc.Y)) < minY) continue;
                    if (MathF.Min(qa.Z, MathF.Min(qb.Z, qc.Z)) > maxZ || MathF.Max(qa.Z, MathF.Max(qb.Z, qc.Z)) < minZ) continue;

                    if (maxUpFacing < 1f)
                    {
                        var n = Vector3.Cross(qb - qa, qc - qa);
                        float nsq = n.LengthSquared();
                        if (nsq < 1e-12f) continue;                            // degenerate face has no facing to read
                        if (n.Z * n.Z > maxUpFacing * maxUpFacing * nsq) continue;
                    }

                    if (!SpatialOverlap.TriangleIntersectsPrism(qa, qb, qc, pa, pb, pc, ph, tolerance)) continue;

                    if (++found >= stopAfter) return found;
                }
            }
        return found;
    }


    /// <summary>What a severance measurement found. <paramref name="Judged"/> false means the triangle could not be
    /// measured at all (an inset that consumed it, or too few samples to resolve a corridor) — which is NOT the same as
    /// "clear", and a caller that treats it as clear will report a sliver-heavy navmesh as clean.</summary>
    public readonly record struct Severance(bool Judged, bool Severed, int Free, int Blocked, int Components, float Step,
                                           int Supported, int InTriangle)
    {
        /// <summary>How much of the triangle the object is HOLDING UP. Near 1 means the navmesh was draped over this
        /// object — it is the ground here, not an obstacle on it — which is how Bethesda authors navmesh across rock
        /// piles, mountain trim and cave floors. Near 0 means the object stands on a surface it did not create, which
        /// is what a wall dropped into a cell looks like.</summary>
        public float SupportedFraction => InTriangle == 0 ? 0f : (float)Supported / InTriangle;
    }

    /// <summary>Does the object SEVER this navmesh triangle — does it break the triangle's promise that an actor can
    /// walk from any edge of it to any other?
    ///
    /// <para>WHY A CROSSING TEST IS NOT ENOUGH. Measured against vanilla Skyrim, "the object's surface enters the actor
    /// column above this triangle" fires on 13% of all references. It is not wrong — it is answering a question whose
    /// honest answer really is "yes" for most of Skyrim. Bethesda does not cut navmesh around every shelf, eggsac and
    /// rubble pile; it lays a few large triangles over a room floor and lets physics steer actors around the furniture.
    /// A crossing test therefore flags a bookshelf standing in the corner of a 400-unit triangle exactly as loudly as a
    /// wall cutting that triangle in half. The difference between clutter and a blocker is not whether it touches the
    /// walkable surface, it is whether it CUTS it: a wall, a fence, a gate separates the triangle into parts an actor
    /// cannot walk between, and a bookshelf does not.</para>
    ///
    /// <para>How: rasterise the (inset) triangle at <paramref name="step"/> units, mark a cell blocked when object
    /// material stands in the actor column over it, flood-fill what is left with 4-connectivity, and ask whether one
    /// free piece survives. Severed means more than one did, or none of a usable size did. Step is actor-scaled —
    /// a corridor narrower than an actor is not a corridor — so it is a physical parameter, not a resolution knob.</para>
    ///
    /// <para>Rasterisation, not point sampling, for the obstruction: a cell is marked from the object's triangles
    /// projected onto it, so a wall thinner than the step cannot fall between sample points. A large object triangle is
    /// tested at the cell centre instead, because using its whole span would let one big sloped floor triangle black out
    /// the entire grid.</para></summary>
    public Severance MeasureSeverance(Vector3 a, Vector3 b, Vector3 c,
                                      float clearance = 24f, float height = 128f, float inset = 8f,
                                      float step = 32f, int minComponent = 2, int maxCells = 4096)
    {
        if (!SpatialOverlap.InsetTriangle(a, b, c, inset, out var pa, out var pb, out var pc))
            return default;

        float minX = MathF.Min(pa.X, MathF.Min(pb.X, pc.X)), maxX = MathF.Max(pa.X, MathF.Max(pb.X, pc.X));
        float minY = MathF.Min(pa.Y, MathF.Min(pb.Y, pc.Y)), maxY = MathF.Max(pa.Y, MathF.Max(pb.Y, pc.Y));

        // A navmesh triangle can be thousands of units across. Coarsen rather than refuse — a wall crossing a huge
        // triangle is still a wall at 128 units — but report the step that was actually used.
        int nx, ny;
        while (true)
        {
            nx = Math.Max(1, (int)MathF.Ceiling((maxX - minX) / step));
            ny = Math.Max(1, (int)MathF.Ceiling((maxY - minY) / step));
            if ((long)nx * ny <= maxCells) break;
            step *= 2f;
        }

        // Plane of the ORIGINAL triangle — the inset moved the corners, not the surface.
        var n = Vector3.Cross(b - a, c - a);
        if (MathF.Abs(n.Z) < 1e-6f) return default;                                     // vertical navmesh has no column
        float nd = Vector3.Dot(n, a);
        float NavZ(float x, float y) => (nd - n.X * x - n.Y * y) / n.Z;

        const byte Outside = 0, Free = 1, Blocked = 2;
        var cellState = new byte[nx * ny];
        var supportedCell = new bool[nx * ny];

        // ---- 1. which cells are inside the inset triangle? ----
        float d = (pb.Y - pc.Y) * (pa.X - pc.X) + (pc.X - pb.X) * (pa.Y - pc.Y);
        if (MathF.Abs(d) < 1e-6f) return default;
        int inTri = 0;
        for (int j = 0; j < ny; j++)
            for (int i = 0; i < nx; i++)
            {
                float cx = minX + (i + 0.5f) * step, cy = minY + (j + 0.5f) * step;
                float u = ((pb.Y - pc.Y) * (cx - pc.X) + (pc.X - pb.X) * (cy - pc.Y)) / d;
                float v = ((pc.Y - pa.Y) * (cx - pc.X) + (pa.X - pc.X) * (cy - pc.Y)) / d;
                if (u < 0 || v < 0 || u + v > 1) continue;
                cellState[j * nx + i] = Free;
                inTri++;
            }
        // Fewer than four cells cannot express a corridor; saying so beats guessing.
        if (inTri < 4) return new Severance(false, false, inTri, 0, 0, step, 0, inTri);

        // ---- 2. mark cells where object material stands in the actor column ----
        _mark ??= new int[TriangleCount];
        if (++_epoch == int.MaxValue) { Array.Clear(_mark); _epoch = 1; }

        int gx0 = (int)MathF.Floor(minX / _cell), gx1 = (int)MathF.Floor(maxX / _cell);
        int gy0 = (int)MathF.Floor(minY / _cell), gy1 = (int)MathF.Floor(maxY / _cell);
        int blocked = 0;

        for (int gx = gx0; gx <= gx1; gx++)
            for (int gy = gy0; gy <= gy1; gy++)
            {
                if (!_grid.TryGetValue((gx, gy), out var list)) continue;
                foreach (var t in list)
                {
                    if (_mark[t] == _epoch) continue;
                    _mark[t] = _epoch;

                    var qa = _v[_i[t * 3]]; var qb = _v[_i[t * 3 + 1]]; var qc = _v[_i[t * 3 + 2]];
                    float tx0 = MathF.Min(qa.X, MathF.Min(qb.X, qc.X)), tx1 = MathF.Max(qa.X, MathF.Max(qb.X, qc.X));
                    float ty0 = MathF.Min(qa.Y, MathF.Min(qb.Y, qc.Y)), ty1 = MathF.Max(qa.Y, MathF.Max(qb.Y, qc.Y));
                    if (tx1 < minX || tx0 > maxX || ty1 < minY || ty0 > maxY) continue;
                    bool upFacing = Vector3.Cross(qb - qa, qc - qa).Z >= 0;

                    int i0 = Math.Max(0, (int)MathF.Floor((tx0 - minX) / step)), i1 = Math.Min(nx - 1, (int)MathF.Floor((tx1 - minX) / step));
                    int j0 = Math.Max(0, (int)MathF.Floor((ty0 - minY) / step)), j1 = Math.Min(ny - 1, (int)MathF.Floor((ty1 - minY) / step));

                    for (int j = j0; j <= j1; j++)
                        for (int i = i0; i <= i1; i++)
                        {
                            int k = j * nx + i;
                            if (cellState[k] == Outside) continue;
                            float x0 = minX + i * step, y0 = minY + j * step;
                            float cx = x0 + step * 0.5f, cy = y0 + step * 0.5f;
                            float nz = NavZ(cx, cy);

                            // Is the object HOLDING UP the navmesh here? An up-facing surface within a step of the
                            // walkable plane means the navmesh was laid over this object, not obstructed by it.
                            if (upFacing && !supportedCell[k] && BarycentricZ(qa, qb, qc, cx, cy) is { } sz
                                && sz >= nz - clearance && sz <= nz + clearance)
                                supportedCell[k] = true;

                            if (cellState[k] != Free) continue;

                            // The cell IS the actor's standing column here: a square of walkable surface swept from
                            // step height to head height. Exact triangle-vs-box SAT rather than a height sample —
                            // a vertical wall face projects to a LINE in plan, so any sampling scheme that reads a
                            // height at the cell centre is blind to precisely the geometry this exists to catch.
                            var colBox = new Aabb(new Vec3(x0, y0, nz + clearance),
                                                  new Vec3(x0 + step, y0 + step, nz + height), false);
                            if (!SpatialOverlap.TriangleIntersectsBox(qa, qb, qc, colBox)) continue;

                            cellState[k] = Blocked; blocked++;
                        }
                }
            }

        int supported = 0;
        for (int k = 0; k < supportedCell.Length; k++) if (supportedCell[k]) supported++;

        // ---- 3. flood fill what is left and ask whether it is still ONE walkable piece ----
        // A navmesh triangle's promise is that an actor can move freely between any two points in it. An object keeps
        // that promise if you can walk around it and breaks it if you cannot — so the test is connectivity of the free
        // space, not how much of it there is. That is what tells a wall from a bookshelf.
        //
        // NOT "does one free region still touch all three edges": a wall across a corner leaves the larger piece
        // touching every edge, and the rule would call a genuine cut clean. Measured, not assumed — the synthetic wall
        // case failed exactly that way before this was rewritten.
        int free = inTri - blocked;
        if (free == 0) return new Severance(true, true, 0, blocked, 0, step, supported, inTri);

        var comp = new int[nx * ny];
        var stack = new Stack<int>();
        int components = 0, significant = 0;

        for (int start = 0; start < cellState.Length; start++)
        {
            if (cellState[start] != Free || comp[start] != 0) continue;
            components++;
            int size = 0;
            stack.Push(start); comp[start] = components;
            while (stack.Count > 0)
            {
                int k = stack.Pop();
                size++;
                int i = k % nx, j = k / nx;
                // 4-connectivity: a diagonal pinch between two blocked cells is not a corridor an actor can walk.
                if (i > 0) Push(k - 1);
                if (i < nx - 1) Push(k + 1);
                if (j > 0) Push(k - nx);
                if (j < ny - 1) Push(k + nx);
            }
            if (size >= minComponent) significant++;

            void Push(int k2) { if (cellState[k2] == Free && comp[k2] == 0) { comp[k2] = components; stack.Push(k2); } }
        }

        // Two or more real pieces means the object cut the triangle. Zero real pieces means what survived is dust the
        // width of a cell, which is a blockage too — and neither reads as "clean".
        bool severed = significant != 1;
        return new Severance(true, severed, free, blocked, significant, step, supported, inTri);

    }

    int[]? _mark;
    int _epoch;
}

/// <summary>A uniform-grid index over a cell's navmesh triangles, so a box query costs O(cells touched) instead of
/// O(triangles). Built per cell — the cell is already the natural partition, and a Skyrim exterior cell is 4096 units
/// across, so a 128-unit grid is 32x32 buckets.</summary>
public sealed class NavmeshIndex
{
    readonly List<(NavmeshGeometry Nav, int Index)> _navs = new();
    readonly Dictionary<(int, int), List<(int NavIdx, int TriIdx)>> _grid = new();
    readonly float _cell;

    public int TriangleCount { get; private set; }
    public int NavmeshCount => _navs.Count;

    NavmeshIndex(float cell) => _cell = cell;

    /// <summary>Index every triangle of every navmesh given. Triangles with an out-of-range vertex index are SKIPPED
    /// and counted in <paramref name="malformed"/> rather than throwing or silently vanishing — a hand-edited navmesh
    /// with a bad index is a real thing and the caller should be able to say so (Q3).</summary>
    public static NavmeshIndex Build(IReadOnlyList<NavmeshGeometry> navs, out int malformed, float cellSize = 128f)
    {
        var idx = new NavmeshIndex(cellSize);
        malformed = 0;

        for (int n = 0; n < navs.Count; n++)
        {
            var nav = navs[n];
            idx._navs.Add((nav, n));
            for (int t = 0; t < nav.Triangles.Count; t++)
            {
                var tri = nav.Triangles[t];
                if (!InRange(tri.A, nav) || !InRange(tri.B, nav) || !InRange(tri.C, nav)) { malformed++; continue; }
                var a = nav.Vertices[tri.A]; var b = nav.Vertices[tri.B]; var c = nav.Vertices[tri.C];

                int x0 = (int)MathF.Floor(MathF.Min(a.X, MathF.Min(b.X, c.X)) / cellSize);
                int x1 = (int)MathF.Floor(MathF.Max(a.X, MathF.Max(b.X, c.X)) / cellSize);
                int y0 = (int)MathF.Floor(MathF.Min(a.Y, MathF.Min(b.Y, c.Y)) / cellSize);
                int y1 = (int)MathF.Floor(MathF.Max(a.Y, MathF.Max(b.Y, c.Y)) / cellSize);

                // A pathological triangle spanning thousands of buckets would blow the index up; cap the span and let
                // such a triangle be found by the coarse sweep instead of registering it everywhere.
                if ((long)(x1 - x0 + 1) * (y1 - y0 + 1) > 4096) { idx._oversized.Add((n, t)); continue; }

                for (int gx = x0; gx <= x1; gx++)
                    for (int gy = y0; gy <= y1; gy++)
                    {
                        if (!idx._grid.TryGetValue((gx, gy), out var list))
                            idx._grid[(gx, gy)] = list = new List<(int, int)>();
                        list.Add((n, t));
                    }
                idx.TriangleCount++;
            }
        }
        return idx;

        static bool InRange(short i, NavmeshGeometry nav) => i >= 0 && i < nav.Vertices.Count;
    }

    readonly List<(int NavIdx, int TriIdx)> _oversized = new();

    /// <summary>Every triangle whose surface intersects <paramref name="box"/>, exactly (SAT — the grid only narrows the
    /// candidate set, it never decides the answer).</summary>
    public IEnumerable<TriHit> Query(Aabb box)
    {
        if (box.IsEmpty) yield break;

        int x0 = (int)MathF.Floor(box.Min.X / _cell), x1 = (int)MathF.Floor(box.Max.X / _cell);
        int y0 = (int)MathF.Floor(box.Min.Y / _cell), y1 = (int)MathF.Floor(box.Max.Y / _cell);

        var seen = new HashSet<(int, int)>();
        for (int gx = x0; gx <= x1; gx++)
            for (int gy = y0; gy <= y1; gy++)
            {
                if (!_grid.TryGetValue((gx, gy), out var list)) continue;
                foreach (var key in list)
                {
                    if (!seen.Add(key)) continue;
                    if (Test(key, box) is { } hit) yield return hit;
                }
            }

        foreach (var key in _oversized)
            if (seen.Add(key) && Test(key, box) is { } hit) yield return hit;
    }

    TriHit? Test((int NavIdx, int TriIdx) key, Aabb box)
    {
        var nav = _navs[key.NavIdx].Nav;
        var tri = nav.Triangles[key.TriIdx];
        var a = nav.Vertices[tri.A]; var b = nav.Vertices[tri.B]; var c = nav.Vertices[tri.C];
        var va = new Vector3(a.X, a.Y, a.Z);
        var vb = new Vector3(b.X, b.Y, b.Z);
        var vc = new Vector3(c.X, c.Y, c.Z);
        if (!SpatialOverlap.TriangleIntersectsBox(va, vb, vc, box)) return null;

        var tb = Aabb.Empty.Add(va).Add(vb).Add(vc);
        return new TriHit(key.NavIdx, key.TriIdx, tri.Flags, tb);
    }

    public NavmeshGeometry NavAt(int i) => _navs[i].Nav;
}
