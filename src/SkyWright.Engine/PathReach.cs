using System.Numerics;

using HousecarlCore;

namespace SkyWright.Engine;

// PATH REACH (reach rule `path`, Part 3b). A NiPathInterpolator moves its node along the spline of its NiPosData keys; its
// NiFloatData (percent) picks WHERE on the path at each time. Whatever the percent keys do, a percent in [0, 1] keeps the node on
// the path, so the path's hull bounds the node's position. A Hermite segment lies in the convex hull of its Bezier control points
// P0, P0 + m0/3, P1 - m1/3, P1. nif.xml does not say whether a quadratic key's tangents are per segment or per second, so both
// readings are hulled (tangent as stored, and times the segment's duration). TBC tangents follow Kochanek-Bartels from the
// neighbouring keys (both readings likewise). LINEAR keys: the key points. Any other key type refuses by name.
//
// STATUS: implemented (red at fb6de1ba).

/// <summary>A position key: time, value, the quadratic key's forward/backward tangents (zero otherwise) and TBC parameters.</summary>
public sealed record PathKey(float Time, Vector3 Value, Vector3 Forward, Vector3 Backward, float Tension, float Continuity, float Bias);

public static class PathReach
{
    /// <summary>The box of every point the path can reach. <paramref name="keyType"/>: LINEAR_KEY, QUADRATIC_KEY or TBC_KEY;
    /// another type, or no key, returns Aabb.Empty with <paramref name="refusal"/> naming it.</summary>
    public static Aabb PathHull(IReadOnlyList<PathKey> keys, string keyType, out string? refusal)
    {
        refusal = null;
        if (keys.Count == 0) { refusal = "path: no position keys"; return Aabb.Empty; }
        var box = Aabb.Empty;
        foreach (var k in keys) box = box.Add(k.Value);
        if (keyType == "LINEAR_KEY") return box;
        if (keyType is not ("QUADRATIC_KEY" or "TBC_KEY")) { refusal = $"path: key type {keyType} is not in the path model"; return Aabb.Empty; }
        for (int i = 0; i + 1 < keys.Count; i++)
        {
            var (m0, m1, kmax) = SegmentTangents(keys, keyType, i);
            // every curve with tangents scaled by k in [0, kmax] lies in the hull of these four control points
            box = box.Add(keys[i].Value + kmax * m0 / 3).Add(keys[i + 1].Value - kmax * m1 / 3);
        }
        return box;
    }

    /// <summary>Segment i's (key i to key i + 1) Hermite tangents as stored, and the largest scaling kmax the readings allow: every
    /// reading is the curve with tangents k m0, k m1 for some k in [0, kmax]. QUADRATIC_KEY or TBC_KEY only (lane 3b: shared with the
    /// path tube, NifMotion.PathTube).</summary>
    internal static (Vector3 M0, Vector3 M1, float KMax) SegmentTangents(IReadOnlyList<PathKey> keys, string keyType, int i)
    {
        var a = keys[i]; var b = keys[i + 1];
        float dt = MathF.Abs(b.Time - a.Time);
        if (keyType == "QUADRATIC_KEY") return (a.Forward, b.Backward, MathF.Max(1, dt));
        // Kochanek-Bartels, the end keys taking their one neighbouring difference; the duration-ratio adjustment some
        // implementations apply is below 2, and the per-second reading multiplies by dt: kmax covers both
        Vector3 Prev(int j) => j > 0 ? keys[j].Value - keys[j - 1].Value : keys[j + 1].Value - keys[j].Value;
        Vector3 Next(int j) => j + 1 < keys.Count ? keys[j + 1].Value - keys[j].Value : keys[j].Value - keys[j - 1].Value;
        Vector3 Out(int j) { var k = keys[j]; return (1 - k.Tension) * (1 + k.Continuity) * (1 + k.Bias) / 2 * Prev(j) + (1 - k.Tension) * (1 - k.Continuity) * (1 - k.Bias) / 2 * Next(j); }
        Vector3 In(int j) { var k = keys[j]; return (1 - k.Tension) * (1 - k.Continuity) * (1 + k.Bias) / 2 * Prev(j) + (1 - k.Tension) * (1 + k.Continuity) * (1 - k.Bias) / 2 * Next(j); }
        return (Out(i), In(i + 1), 2 * MathF.Max(1, dt));
    }

    /// <summary>Percent keys keep the node on the path only inside [0, 1] (a loop flag wraps, which stays on the path too): null
    /// when every percent value lies in [0, 1], else the refusal naming the first value outside.</summary>
    public static string? PercentRefusal(IReadOnlyList<float> percentValues)
    {
        foreach (var v in percentValues)
            if (!(v >= 0 && v <= 1)) return $"path: percent value {v.ToString(System.Globalization.CultureInfo.InvariantCulture)} lies outside [0, 1]";
        return null;
    }
}
