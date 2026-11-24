using System.Numerics;

using HousecarlCore;

namespace SkyWright.Engine;

// BLEND ENVELOPE (reach rule `blend`, Part 3b). Behaviour-graph transitions blend the poses of two (or more) generators. For a
// node chain that is a nonlinear set; two sound outer bounds:
//  * PRODUCT: when every driven node's poses lie on one 1-DOF family (rotation about one fixed axis with a fixed translation, or
//    translation along one line with a fixed rotation), a blend of any of them stays inside that node's family between the
//    family's extreme parameters, so the reachable set lies in the product of the per-node intervals. The box is swept over a
//    grid of that product, each grid pose's box grown by a certified delta (the largest move any point can make between grid
//    neighbours, from the chord of the half step times the distance to the node's pivot, summed down the chain).
//  * CHORD: otherwise each node's local transform stays within angle theta and distance tau of a reference pose (a geodesic ball
//    of rotation angle < pi is convex, so normalized blends stay inside it; a translation ball is convex); a point's deviation
//    from its reference world position is bounded by the triangle inequality down the chain. theta >= pi uses the full sphere.
// Conventions (both functions): local transform x -> R x + T (System.Numerics Vector3.Transform with the quaternion); the
// chain is listed root first; the point is in the last link's frame; the result is in the first link's parent frame.
//
// STATUS: implemented (red at fb6de1ba).

/// <summary>A node's 1-DOF family. <c>rotation</c>: R(a) = CreateFromAxisAngle(Axis, a) * Q0, T = T0, a in [Min, Max] radians;
/// <c>translation</c>: R = Q0, T = T0 + s·Axis, s in [Min, Max]; <c>fixed</c>: R = Q0, T = T0.</summary>
public sealed record BlendFamily(string Kind, Quaternion Q0, Vector3 T0, Vector3 Axis, float Min, float Max);

/// <summary>A node's reference local transform and the largest rotation angle (radians) and translation distance of any pose
/// from it.</summary>
public sealed record ChordLink(Quaternion QRef, Vector3 TRef, float MaxAngle, float MaxTranslation);

public static class BlendEnvelope
{
    /// <summary>The 1-DOF family of a node's poses, or null (with <paramref name="why"/>) when they do not lie on one; the
    /// parameter interval spans every pose (tolerances as SweptGeometry.OneDofRefusal).</summary>
    public static BlendFamily? Family(IReadOnlyList<(Quaternion Rotation, Vector3 Translation)> poses, out string? why)
    {
        why = null;
        if (poses.Count == 0) { why = "no poses"; return null; }
        var q0 = Quaternion.Normalize(poses[0].Rotation); var t0 = poses[0].Translation;
        bool sameRot = poses.All(p => HavokClip.AngleBetween(Quaternion.Normalize(p.Rotation), q0) < 1e-4f);
        bool sameT = poses.All(p => Vector3.Distance(p.Translation, t0) < 1e-3f);
        if (sameRot && sameT) return new BlendFamily("fixed", q0, t0, Vector3.UnitZ, 0, 0);
        if (sameRot)
        {
            var far = poses.OrderByDescending(p => Vector3.Distance(p.Translation, t0)).First().Translation;
            var dir = Vector3.Normalize(far - t0);
            float lo = 0, hi = 0;
            foreach (var p in poses)
            {
                var d = p.Translation - t0; float sv = Vector3.Dot(d, dir);
                if ((d - sv * dir).Length() > 1e-3f) { why = "translations do not lie on one line"; return null; }
                lo = MathF.Min(lo, sv); hi = MathF.Max(hi, sv);
            }
            return new BlendFamily("translation", q0, t0, dir, lo, hi);
        }
        if (sameT)
        {
            Vector3? axis = null; var rels = new List<Quaternion>();
            foreach (var p in poses)
            {
                var rel = Quaternion.Normalize(Quaternion.Normalize(p.Rotation) * Quaternion.Conjugate(q0));
                if (rel.W < 0) rel = Quaternion.Negate(rel);
                rels.Add(rel);
                var v = new Vector3(rel.X, rel.Y, rel.Z);
                if (v.Length() < 1e-5f) continue;
                v = Vector3.Normalize(v);
                if (axis is null) { axis = v; continue; }
                if (Vector3.Cross(axis.Value, v).Length() > 1e-3f) { why = "rotations about more than one axis"; return null; }
            }
            var ax = axis!.Value; float lo = 0, hi = 0;
            foreach (var rel in rels)
            {
                float a = 2 * MathF.Atan2(Vector3.Dot(new Vector3(rel.X, rel.Y, rel.Z), ax), rel.W);
                lo = MathF.Min(lo, a); hi = MathF.Max(hi, a);
            }
            return new BlendFamily("rotation", q0, t0, ax, lo, hi);
        }
        why = "both rotation and translation change between poses";
        return null;
    }

    static (Quaternion R, Vector3 T) At(BlendFamily f, float p) => f.Kind switch
    {
        "rotation" => (Quaternion.CreateFromAxisAngle(f.Axis, p) * f.Q0, f.T0),
        "translation" => (f.Q0, f.T0 + p * f.Axis),
        _ => (f.Q0, f.T0),
    };

    static float MaxTranslation(BlendFamily f) => f.Kind == "translation"
        ? MathF.Max((f.T0 + f.Min * f.Axis).Length(), (f.T0 + f.Max * f.Axis).Length()) : f.T0.Length();

    /// <summary>A box containing every position of <paramref name="point"/> over the product of the chain's family intervals,
    /// sound to within <paramref name="delta"/> (the grid is refined until the certified growth is at most delta).</summary>
    public static Aabb ProductBox(IReadOnlyList<BlendFamily> chain, Vector3 point, float delta)
    {
        int n = chain.Count;
        if (n == 0) return Aabb.Empty.Add(point);
        // rho[i]: bound on the point's distance from link i's pivot (its position in link i's own frame)
        var rho = new double[n];
        rho[n - 1] = point.Length();
        for (int i = n - 2; i >= 0; i--) rho[i] = MaxTranslation(chain[i + 1]) + rho[i + 1];
        var steps = chain.Select(f => f.Max > f.Min ? 1 : 0).ToArray();   // a zero-range link is evaluated once
        double Move(int i)   // the largest move of the point when link i's parameter is off a grid node by half a step
        {
            var f = chain[i]; if (steps[i] == 0) return 0; double h = (f.Max - f.Min) / steps[i];
            return f.Kind switch { "rotation" => 2 * Math.Sin(Math.Min(h / 4, Math.PI / 2)) * rho[i], "translation" => h / 2 * f.Axis.Length(), _ => 0 };
        }
        double total = Enumerable.Range(0, n).Sum(Move);
        long cells = 1;
        while (total > delta)
        {
            int worst = Enumerable.Range(0, n).OrderByDescending(Move).First();
            if (cells * 2 > 4_000_000) break;   // the grid cap: the box is still sound, grown by the larger certified move
            steps[worst] *= 2; cells = steps.Aggregate(1L, (a, b) => a * (b + 1));
            total = Enumerable.Range(0, n).Sum(Move);
        }
        var box = Aabb.Empty; var idx = new int[n];
        while (true)
        {
            var x = point;
            for (int i = n - 1; i >= 0; i--)
            {
                var f = chain[i]; float p = steps[i] == 0 ? f.Min : f.Min + (f.Max - f.Min) * idx[i] / steps[i];
                var (r, t) = At(f, p); x = Vector3.Transform(x, r) + t;
            }
            box = box.Add(x);
            int k = 0;
            while (k < n && ++idx[k] > steps[k]) { idx[k] = 0; k++; }
            if (k == n) break;
        }
        float g = (float)total;
        return new Aabb(new Vec3(Dn(box.Min.X - g), Dn(box.Min.Y - g), Dn(box.Min.Z - g)), new Vec3(Upf(box.Max.X + g), Upf(box.Max.Y + g), Upf(box.Max.Z + g)), false);
    }

    static float Dn(float x) => MathF.BitDecrement(x);
    static float Upf(float x) => MathF.BitIncrement(x);

    /// <summary>A box containing every position of <paramref name="point"/> when each link deviates from its reference by at
    /// most its MaxAngle and MaxTranslation (the reference world position grown by the chord bound on every axis).</summary>
    public static Aabb ChordBox(IReadOnlyList<ChordLink> chain, Vector3 point)
    {
        int n = chain.Count;
        if (n == 0) return Aabb.Empty.Add(point);
        var x = point;
        for (int i = n - 1; i >= 0; i--) x = Vector3.Transform(x, Quaternion.Normalize(chain[i].QRef)) + chain[i].TRef;
        double rho = point.Length(), dev = 0;
        for (int i = n - 1; i >= 0; i--)
        {
            if (i < n - 1) rho += chain[i + 1].TRef.Length() + chain[i + 1].MaxTranslation;
            double c = 2 * Math.Sin(Math.Min(Math.Abs(chain[i].MaxAngle), Math.PI) / 2);
            dev += chain[i].MaxTranslation + c * rho;
        }
        float g = (float)dev;
        return new Aabb(new Vec3(Dn(x.X - g), Dn(x.Y - g), Dn(x.Z - g)), new Vec3(Upf(x.X + g), Upf(x.Y + g), Upf(x.Z + g)), false);
    }
}
