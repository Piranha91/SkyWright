using System.Numerics;

using HousecarlCore;

namespace SkyWright.Engine;

// NODE SWEEP (reach rule `emitter`, Part 3b): where a named node goes over the model's motion, for the moving-emitter bound
// (ParticleReach.BoundsFromModifiers with a NodeSweeper).

public static partial class NifMotion
{
    /// <summary>A named node's model transforms at every sample time of every source that drives a node on its chain (key times,
    /// span ends, 64 uniform samples), and a distance bounding how far a point within <paramref name="radius"/> of the node origin
    /// moves between consecutive samples (per link: translation bound plus the chord of the angle bound plus the scale change, each
    /// times the largest distance below that link, scaled by the links above). Null when no source moves the chain, when the graph
    /// blends several such sources (single-source samples would miss the blends), or the node is absent.</summary>
    /// <summary>Whether <paramref name="src"/> holds every link of <paramref name="links"/> it drives at the link's stored local (1e-4 u,
    /// 1e-5 rad, 1e-6 scale) at every key time, both span ends and 63 uniform samples, with zero certified motion between consecutive
    /// samples.</summary>
    static bool ChainHoldsStored(MotionRig rig, MotionSource src, IReadOnlyList<NiflySharp.Blocks.NiAVObject> links)
    {
        float a = src.Animation.Start, b = src.Animation.End;
        var ts = src.KeyTimes().Where(x => x >= a && x <= b).Append(a).Append(b).Concat(b > a ? Enumerable.Range(1, 63).Select(k => a + (b - a) * k / 64f) : Array.Empty<float>()).Distinct().OrderBy(x => x).ToList();
        foreach (var link in links)
        {
            var nm = link.Name?.String;
            if (nm is null || !src.Nodes.Contains(nm)) continue;
            var st = rig.Stored(link, false);
            var q0 = Quaternion.Normalize(Quaternion.CreateFromRotationMatrix(st.R));
            for (int k = 0; k < ts.Count; k++)
            {
                var l = src.Local(nm, st, ts[k]);
                if (Vector3.Distance(l.T, st.T) > 1e-4f || Math.Abs(l.S - st.S) > 1e-6f
                    || HavokClip.AngleBetween(Quaternion.Normalize(Quaternion.CreateFromRotationMatrix(l.R)), q0) > 1e-5f) return false;
                if (k + 1 < ts.Count)
                {
                    var bd = src.Bound(nm, st, ts[k], ts[k + 1]);
                    if (bd.T > 1e-4f || bd.A > 1e-5f || Math.Abs(bd.S) > 1e-6f) return false;
                }
            }
        }
        return true;
    }

    public static (IReadOnlyList<(Matrix4x4 R, float S, Vector3 T)> Xf, double Widen, double SpeedEstimate)? NodeSweep(byte[] nif, MotionModel model, Func<string, byte[]?>? read, string nodeName, double radius)
    {
        if (model.Status == "refused") return null;
        var rig = RigOf(nif, model, read);
        if (!rig.NodeByName.TryGetValue(nodeName, out var node)) return null;
        var chain = rig.Chain(node);                       // node first, root last (the root's own transform is left out)
        var links = chain.Take(chain.Count - 1).ToList();  // node .. root's child
        var names = links.Select(l => l.Name?.String).Where(n => n is not null).Select(n => n!).ToHashSet(StringComparer.Ordinal);
        var srcs = rig.Sources.Values.Where(s => s.Nodes.Any(n => names.Contains(n))).ToList();
        if (srcs.Count == 0)
        {
            // no source with keys drives the chain (a pose-only interpolator names it): the reader holds it at its stored pose
            var m0 = rig.Model(node, null, 0, false);
            return (new List<(Matrix4x4, float, Vector3)> { (m0.R, m0.S, m0.T) }, 0, 0);
        }
        if (srcs.All(src => ChainHoldsStored(rig, src, links)))
        {
            // Part 3b round 2: every source driving the chain holds each link at its stored local at every key time, span end and
            // uniform sample, with no certified motion between them: the chain does not move under any source or any blend of them
            // (a blend of equal poses is that pose)
            var m0 = rig.Model(node, null, 0, false);
            return (new List<(Matrix4x4, float, Vector3)> { (m0.R, m0.S, m0.T) }, 0, 0);
        }
        if (rig.BlendActive && srcs.Count > 1)
        {
            // the graph blends the sources: the node's origin over every blend is the blend envelope of the points within radius of
            // it (corners of the radius cube, BlendPointBox); returned as identity-rotation transforms at that box's corners plus one
            // turned transform (so the caller treats the frame as turning), with widen 2 radius: any rotated offset of length <= radius
            // lies within it. No speed is known under blends (+infinity: an inherit-velocity modifier then refuses).
            var cube = Enumerable.Range(0, 8).Select(k => new Vector3((k & 1) == 0 ? -(float)radius : (float)radius, (k & 2) == 0 ? -(float)radius : (float)radius, (k & 4) == 0 ? -(float)radius : (float)radius)).Distinct().ToList();
            var (ob, why) = BlendPointBox(rig, node, cube, 0.5f);
            if (why is not null || ob.IsEmpty) return null;
            var list = Enumerable.Range(0, 8).Select(k => (Matrix4x4.Identity, 1f, new Vector3((k & 1) == 0 ? ob.Min.X : ob.Max.X, (k & 2) == 0 ? ob.Min.Y : ob.Max.Y, (k & 4) == 0 ? ob.Min.Z : ob.Max.Z))).ToList();
            list.Add((Matrix4x4.CreateRotationZ(MathF.PI / 2), 1f, list[0].Item3));
            return (list, 2 * radius, double.PositiveInfinity);
        }
        var reach = links.Select(l => LinkReach(rig, l)).ToList();
        var rho = new double[links.Count];
        double acc = radius;
        for (int i = 0; i < links.Count; i++) { rho[i] = acc; acc = reach[i].T + reach[i].S * acc; }   // links[0] = the node itself
        var xfs = new List<(Matrix4x4, float, Vector3)>(); double widen = 0, speed = 0;
        foreach (var s in srcs)
        {
            float a = s.Animation.Start, b = s.Animation.End;
            var ts = s.KeyTimes().Where(x => x >= a && x <= b).Append(a).Append(b).Concat(b > a ? Enumerable.Range(1, 63).Select(k => a + (b - a) * k / 64f) : Array.Empty<float>()).Distinct().OrderBy(x => x).ToList();
            for (int k = 0; k < ts.Count; k++)
            {
                var m = rig.Model(node, s, ts[k], false); xfs.Add((m.R, m.S, m.T));
                if (k + 1 >= ts.Count) continue;
                double move = 0, scaleAbove = 1;
                for (int i = links.Count - 1; i >= 0; i--)
                {
                    var nm = links[i].Name?.String;
                    if (nm is not null && s.Nodes.Contains(nm))
                    {
                        var st = rig.Stored(links[i], false);
                        var bd = s.Bound(nm, st, ts[k], ts[k + 1]);
                        move += scaleAbove * (bd.T + (2 * Math.Sin(Math.Min(bd.A, Math.PI) / 2) + Math.Abs(bd.S)) * rho[i]);
                    }
                    scaleAbove *= reach[i].S;
                }
                widen = Math.Max(widen, move);
            }
            // speed estimate (inherit-velocity modifier): the node origin's largest finite-difference speed over 1024 samples x 1.25
            if (b > a)
            {
                var prev = rig.Model(node, s, a, false).T;
                for (int k = 1; k <= 1024; k++)
                {
                    float t = a + (b - a) * k / 1024f;
                    var cur = rig.Model(node, s, t, false).T;
                    speed = Math.Max(speed, 1.25 * Vector3.Distance(cur, prev) / ((b - a) / 1024.0));
                    prev = cur;
                }
            }
        }
        return (xfs, widen, speed);
    }
}
