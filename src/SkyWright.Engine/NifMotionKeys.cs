using System.Numerics;
using NiflySharp.Enums;
using NiflySharp.Structs;

using HousecarlCore;

namespace SkyWright.Engine;

// NIF KEY EVALUATION (animated-object swept volume, tier 1). Conventions decided by measurement and frozen in the task's
// decision record (D2):
//  * QUADRATIC float keys: cubic Hermite on [k_i, k_i+1] with tangents per interval (not scaled by the interval's
//    duration); the OUT tangent of key i is NiflySharp's `Backward` field and the IN tangent of key i+1 is its `Forward`
//    field. Decided against a Havok-compiled copy of the same motion, which the obvious reading (Forward out,
//    Backward in) does not reproduce and this one does.
//  * XYZ rotation: rows = (Rz(z) Ry(y) Rx(x))^T, the column-form product transposed into the row sense NifGeometry uses
//    for a node's Matrix33.
//  * Quaternion keys: NiflySharp loads the file's (w, x, y, z) into Quaternion.X, .Y, .Z, .W in that order (checked
//    against the raw key bytes); FromNifly converts to a true (x, y, z, w) quaternion before any use. Rows are the
//    transpose of its column-form matrix, the same sense as the Euler rows.
//  * LINEAR: lerp (slerp for quaternions, shortest arc); CONST: the earlier key's value; outside the key range: clamp.
// Every other key type is refused by the caller with its name; this class throws for one it does not evaluate.
//
// STATUS: API frozen at the Gate B red gate (proofprobe MotionProof); implemented.

/// <summary>A certified range of a key channel over a time interval (min and max of every value it takes there).</summary>
public readonly record struct KeyRange(float Min, float Max)
{
    public float Width => Max - Min;
}

public static class NifMotionKeys
{
    public const string Convention = "hermite-per-interval:out=Backward,in=Forward|xyz=(RzRyRx)^T|quat=nifly(X,Y,Z,W)=file(w,x,y,z)|linear=lerp/slerp|const=hold|clamp";

    static float Hermite(float a, float b, float outT, float inT, float u)
    {
        float u2 = u * u, u3 = u2 * u;
        return (2 * u3 - 3 * u2 + 1) * a + (u3 - 2 * u2 + u) * outT + (-2 * u3 + 3 * u2) * b + (u3 - u2) * inT;
    }

    /// <summary>The piece of a key list containing t: index i with keys[i].Time &lt;= t &lt;= keys[i+1].Time and u in [0, 1];
    /// -1 before the first key and Count-1 at or after the last (clamped).</summary>
    internal static int Piece<TKey>(List<TKey> keys, Func<TKey, float> time, float t, out float u)
    {
        u = 0;
        if (t <= time(keys[0])) return -1;
        if (t >= time(keys[^1])) return keys.Count - 1;
        int lo = 0, hi = keys.Count - 1;
        while (hi - lo > 1) { int mid = (lo + hi) / 2; if (time(keys[mid]) <= t) lo = mid; else hi = mid; }
        float dt = time(keys[lo + 1]) - time(keys[lo]);
        u = dt > 0 ? (t - time(keys[lo])) / dt : 0;
        return lo;
    }

    /// <summary>Value of a float key group at time t.</summary>
    public static float EvalFloat(KeyGroup<float> group, float t)
    {
        var k = group.Keys;
        if (k is null || k.Count == 0) throw new InvalidOperationException("float key group has no keys");
        int i = Piece(k, x => x.Time, t, out float u);
        if (i < 0) return k[0].Value;
        if (i >= k.Count - 1) return k[^1].Value;
        var a = k[i]; var b = k[i + 1];
        return group.Interpolation switch
        {
            KeyType.LINEAR_KEY => a.Value + (b.Value - a.Value) * u,
            KeyType.QUADRATIC_KEY => Hermite(a.Value, b.Value, a.Backward, b.Forward, u),
            KeyType.CONST_KEY => a.Value,
            _ => throw new NotSupportedException($"float key type {group.Interpolation}"),
        };
    }

    /// <summary>Value of a vector key group (LINEAR, CONST) at time t.</summary>
    public static Vector3 EvalVector(KeyGroup<Vector3> group, float t)
    {
        var k = group.Keys;
        if (k is null || k.Count == 0) throw new InvalidOperationException("vector key group has no keys");
        int i = Piece(k, x => x.Time, t, out float u);
        if (i < 0) return k[0].Value;
        if (i >= k.Count - 1) return k[^1].Value;
        return group.Interpolation switch
        {
            KeyType.LINEAR_KEY => Vector3.Lerp(k[i].Value, k[i + 1].Value, u),
            KeyType.CONST_KEY => k[i].Value,
            _ => throw new NotSupportedException($"vector key type {group.Interpolation}"),
        };
    }

    /// <summary>A NiflySharp quaternion as loaded from a NIF (file w, x, y, z held in X, Y, Z, W) as a true quaternion.</summary>
    public static Quaternion FromNifly(Quaternion raw) => new(raw.Y, raw.Z, raw.W, raw.X);

    /// <summary>Shortest-arc spherical interpolation of two unit quaternions.</summary>
    public static Quaternion Slerp(Quaternion a, Quaternion b, float u)
    {
        float d = Quaternion.Dot(a, b);
        if (d < 0) { b = Quaternion.Negate(b); d = -d; }
        if (d > 0.9995f) return Quaternion.Normalize(Quaternion.Lerp(a, b, u));
        double th = Math.Acos(Math.Min(1.0, d)), s = Math.Sin(th);
        float wa = (float)(Math.Sin((1 - u) * th) / s), wb = (float)(Math.Sin(u * th) / s);
        return Quaternion.Normalize(new Quaternion(wa * a.X + wb * b.X, wa * a.Y + wb * b.Y, wa * a.Z + wb * b.Z, wa * a.W + wb * b.W));
    }

    /// <summary>Value of quaternion keys (LINEAR: shortest-arc slerp) at time t. Keys are as NiflySharp loads them; the
    /// result is a true quaternion (FromNifly applied).</summary>
    public static Quaternion EvalQuaternion(IReadOnlyList<QuatKey<Quaternion>> keys, KeyType type, float t)
    {
        if (keys is null || keys.Count == 0) throw new InvalidOperationException("quaternion key list is empty");
        if (type != KeyType.LINEAR_KEY) throw new NotSupportedException($"quaternion key type {type}");
        var list = keys as List<QuatKey<Quaternion>> ?? keys.ToList();
        int i = Piece(list, x => x.Time, t, out float u);
        if (i < 0) return Quaternion.Normalize(FromNifly(list[0].Value));
        if (i >= list.Count - 1) return Quaternion.Normalize(FromNifly(list[^1].Value));
        return Slerp(Quaternion.Normalize(FromNifly(list[i].Value)), Quaternion.Normalize(FromNifly(list[i + 1].Value)), u);
    }

    /// <summary>The node rotation, in NifGeometry's row sense, of XYZ Euler angles in radians: (Rz Ry Rx)^T.</summary>
    public static Matrix4x4 XyzRows(float x, float y, float z)
    {
        double cx = Math.Cos(x), sx = Math.Sin(x), cy = Math.Cos(y), sy = Math.Sin(y), cz = Math.Cos(z), sz = Math.Sin(z);
        // column form C = Rz Ry Rx
        double c11 = cz * cy, c12 = cz * sy * sx - sz * cx, c13 = cz * sy * cx + sz * sx;
        double c21 = sz * cy, c22 = sz * sy * sx + cz * cx, c23 = sz * sy * cx - cz * sx;
        double c31 = -sy, c32 = cy * sx, c33 = cy * cx;
        return Rows(c11, c12, c13, c21, c22, c23, c31, c32, c33);
    }

    /// <summary>The node rotation, in NifGeometry's row sense, of a unit quaternion (x, y, z, w): the transpose of its
    /// column-form matrix. Shared by NIF quaternion keys and decoded Havok clips (one function, so one convention).</summary>
    public static Matrix4x4 QuaternionRows(Quaternion q)
    {
        q = Quaternion.Normalize(q);
        double x = q.X, y = q.Y, z = q.Z, w = q.W;
        double c11 = 1 - 2 * (y * y + z * z), c12 = 2 * (x * y - z * w), c13 = 2 * (x * z + y * w);
        double c21 = 2 * (x * y + z * w), c22 = 1 - 2 * (x * x + z * z), c23 = 2 * (y * z - x * w);
        double c31 = 2 * (x * z - y * w), c32 = 2 * (y * z + x * w), c33 = 1 - 2 * (x * x + y * y);
        return Rows(c11, c12, c13, c21, c22, c23, c31, c32, c33);
    }

    // rows = transpose of the column-form matrix c
    static Matrix4x4 Rows(double c11, double c12, double c13, double c21, double c22, double c23, double c31, double c32, double c33) =>
        new((float)c11, (float)c21, (float)c31, 0, (float)c12, (float)c22, (float)c32, 0, (float)c13, (float)c23, (float)c33, 0, 0, 0, 0, 1);

    /// <summary>Certified range of a float key group over [t0, t1]: the exact minimum and maximum of the piecewise
    /// polynomial (ends, interior key values and the cubic pieces' critical points), widened by a float rounding margin.</summary>
    public static KeyRange FloatRange(KeyGroup<float> group, float t0, float t1)
    {
        if (t1 < t0) (t0, t1) = (t1, t0);
        var k = group.Keys;
        if (k is null || k.Count == 0) throw new InvalidOperationException("float key group has no keys");
        float lo = float.MaxValue, hi = float.MinValue;
        void Take(float v) { if (v < lo) lo = v; if (v > hi) hi = v; }
        Take(EvalFloat(group, t0)); Take(EvalFloat(group, t1));
        for (int i = 0; i < k.Count; i++) if (k[i].Time > t0 && k[i].Time < t1) Take(k[i].Value);
        if (group.Interpolation == KeyType.QUADRATIC_KEY)
            for (int i = 0; i + 1 < k.Count; i++)
            {
                var a = k[i]; var b = k[i + 1];
                float ta = a.Time, tb = b.Time;
                if (tb <= t0 || ta >= t1 || tb <= ta) continue;
                // p(u) = h00 va + h10 m0 + h01 vb + h11 m1 = A u^3 + B u^2 + m0 u + va
                double m0 = a.Backward, m1 = b.Forward, va = a.Value, vb = b.Value;
                double A = 2 * va + m0 - 2 * vb + m1, B = -3 * va - 2 * m0 + 3 * vb - m1;
                double qa = 3 * A, qb = 2 * B, qc = m0;
                var roots = new List<double>();
                if (Math.Abs(qa) < 1e-12) { if (Math.Abs(qb) > 1e-12) roots.Add(-qc / qb); }
                else
                {
                    double disc = qb * qb - 4 * qa * qc;
                    if (disc >= 0) { double sq = Math.Sqrt(disc); roots.Add((-qb + sq) / (2 * qa)); roots.Add((-qb - sq) / (2 * qa)); }
                }
                foreach (var u in roots)
                {
                    if (u <= 0 || u >= 1) continue;
                    double t = ta + u * (tb - ta);
                    if (t > t0 && t < t1) Take(Hermite(a.Value, b.Value, a.Backward, b.Forward, (float)u));
                }
            }
        float margin = 1e-6f + 1e-6f * Math.Max(Math.Abs(lo), Math.Abs(hi));
        return new KeyRange(lo - margin, hi + margin);
    }

    /// <summary>Certified per-axis range of a vector key group (LINEAR / CONST: hull of the values met) over [t0, t1].</summary>
    public static (Vector3 Min, Vector3 Max) VectorRange(KeyGroup<Vector3> group, float t0, float t1)
    {
        if (t1 < t0) (t0, t1) = (t1, t0);
        var k = group.Keys;
        var a = EvalVector(group, t0); var b = EvalVector(group, t1);
        var lo = Vector3.Min(a, b); var hi = Vector3.Max(a, b);
        foreach (var key in k) if (key.Time > t0 && key.Time < t1) { lo = Vector3.Min(lo, key.Value); hi = Vector3.Max(hi, key.Value); }
        var margin = new Vector3(1e-5f) + 1e-6f * Vector3.Max(Vector3.Abs(lo), Vector3.Abs(hi));
        return (lo - margin, hi + margin);
    }
}
