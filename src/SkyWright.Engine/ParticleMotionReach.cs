using System.Numerics;

using HousecarlCore;

namespace SkyWright.Engine;

// PARTICLE MOTION REACH (reach rule `emitter` and the settled-gravity fold of `particles`, Part 3b).
//  * Moving emitter: a particle's offset from the point and moment it was emitted is bounded by particle_model_v2 in the emitter's
//    frame at that moment; over a moving emitter the region is the Minkowski sum of the emitter origin's swept world positions and
//    that offset box — rotation-invariant (the sphere of the box's farthest corner) when the emitter's frame turns. Inherited
//    velocity adds its travel isotropically.
//  * Settled gravity (in-game observation): nif.xml states neither World Aligned's frame nor the sign convention, so v2 holds both
//    frames and both signs. Where a recording shows which way particles fall, the observation settles both: along the settled
//    direction the axis bound becomes one-sided, and against it the climb is the ballistic apex (v^2 / 2g when v/g <= T, else
//    v T - g T^2 / 2), not v T.
//  * Support plane (proposed, Gate B question): a particle that reaches the plane of the surface below the emitter is under that
//    surface; the drawn region above the surface is bounded laterally by the projectile range for a launch height h.
//
// STATUS: implemented (red at fb6de1ba).

/// <summary>An in-game observation settling a particle model's gravity: the model (Data-relative path and the sha256 of the observed
/// bytes), the systems it covers (empty = every system), whether the particles fall toward world -z, the evidence, and optionally a
/// measured footprint (the farthest particle from the emitter axis over every recorded burst, grown by the recorded spread) with its
/// label - reported beside the file bound, never in place of it.</summary>
public sealed record SettledGravity(string ModelPath, string Sha256, IReadOnlyList<string> Systems, bool TowardWorldNegativeZ, string Evidence,
                                    double? MeasuredRadius = null, string? MeasuredLabel = null);

public static class ParticleMotionReach
{
    /// <summary>The Minkowski region: <paramref name="emitterOrigins"/> (world box of the emitter origin over its motion) grown by
    /// <paramref name="offsets"/> (box relative to the origin, in the emitter frame <paramref name="frame"/> when it does not turn;
    /// when <paramref name="turns"/>, the sphere of offsets' farthest corner from the origin), then by
    /// <paramref name="inheritedTravel"/> on every axis.</summary>
    public static Aabb MovingEmitterBox(Aabb emitterOrigins, Aabb offsets, bool turns, Quaternion frame, float inheritedTravel)
    {
        if (emitterOrigins.IsEmpty || offsets.IsEmpty) return Aabb.Empty;
        Vector3 lo, hi;
        var corners = Enumerable.Range(0, 8).Select(k => new Vector3((k & 1) == 0 ? offsets.Min.X : offsets.Max.X, (k & 2) == 0 ? offsets.Min.Y : offsets.Max.Y, (k & 4) == 0 ? offsets.Min.Z : offsets.Max.Z)).ToList();
        if (turns)
        {
            float rr = corners.Max(c => c.Length());
            lo = new Vector3(-rr); hi = new Vector3(rr);
        }
        else
        {
            var q = Quaternion.Normalize(frame);
            var w = corners.Select(c => Vector3.Transform(c, q)).ToList();
            lo = w.Aggregate(Vector3.Min); hi = w.Aggregate(Vector3.Max);
        }
        var e = new Vector3(inheritedTravel);
        lo -= e; hi += e;
        return new Aabb(new Vec3(MathF.BitDecrement(emitterOrigins.Min.X + lo.X), MathF.BitDecrement(emitterOrigins.Min.Y + lo.Y), MathF.BitDecrement(emitterOrigins.Min.Z + lo.Z)),
                        new Vec3(MathF.BitIncrement(emitterOrigins.Max.X + hi.X), MathF.BitIncrement(emitterOrigins.Max.Y + hi.Y), MathF.BitIncrement(emitterOrigins.Max.Z + hi.Z)), false);
    }

    /// <summary>The extent along one axis (min ≤ 0 ≤ max) for speed bound <paramref name="v"/>, lifetime <paramref name="T"/>,
    /// half-size <paramref name="r"/>, isotropic accelerations <paramref name="aIso"/>, drag decay <paramref name="k"/> (per
    /// second) and a planar gravity of magnitude <paramref name="g"/> settled to pull toward the axis's negative direction
    /// (<paramref name="towardNegative"/> true) or positive; unsettled gravity is v2's symmetric bound.</summary>
    public static (double Min, double Max) SettledAxis(double v, double T, double r, double aIso, double k, double g, bool? towardNegative)
        => SettledAxis(v, T, r, aIso, k, g, g, towardNegative);

    /// <summary>As above with the gravity moving with the particle (<paramref name="gWith"/>) and the gravity a climb works against
    /// (<paramref name="gClimb"/>) given apart: the larger pull lengthens the fall, the smaller one lengthens the climb (v3: 1.6 x the
    /// largest strength key and 1.0 x the smallest).</summary>
    public static (double Min, double Max) SettledAxis(double v, double T, double r, double aIso, double k, double gWith, double gClimb, bool? towardNegative)
    {
        double g = gWith;
        double phi1 = k > 0 ? (1 - Math.Exp(-k * T)) / k : T;
        double phi2 = k > 0 ? T / k - (1 - Math.Exp(-k * T)) / (k * k) : T * T / 2;
        double with = v * phi1 + r + (aIso + Math.Abs(g)) * phi2;          // moving with the gravity (or either way, unsettled)
        if (towardNegative is null) return (-with, with);
        // against the gravity: the drag-free climb under the net downward pull (isotropic accelerations taken as pushing up)
        double net = Math.Abs(gClimb) - aIso, climb;
        if (net <= 0) climb = v * phi1 + r + (aIso - Math.Abs(gClimb)) * phi2;
        else climb = (v / net <= T ? v * v / (2 * net) : v * T - net * T * T / 2) + r;
        return towardNegative == true ? (-with, climb) : (-climb, with);
    }

    /// <summary>The largest horizontal distance a particle launched at speed ≤ <paramref name="v"/> from height
    /// <paramref name="h"/> ≥ 0 above a plane reaches before it is back on the plane, under downward gravity
    /// <paramref name="g"/> &gt; 0, no drag, within lifetime <paramref name="T"/> (the drag-free bound; drag only shortens it).</summary>
    public static double ProjectileRange(double v, double g, double h, double T)
        => g > 0 ? Math.Min(v / g * Math.Sqrt(v * v + 2 * g * Math.Max(0, h)), v * T) : v * T;
}
