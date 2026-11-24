using System.Numerics;
using NiflySharp.Blocks;

using HousecarlCore;

namespace SkyWright.Engine;

// BLEND ENVELOPE OF A MODEL (reach rule `blend`, Part 3b). A behaviour graph blends generator outputs per bone (transitions between
// states, hkbBlenderGenerator children); the swept region holds each animation's own poses, not the blends between them. This adds
// the blends: per driven node, the poses every animation reaches (key/knot times, span ends, and uniform samples, plus the stored
// local, which a source that does not drive the node leaves) give either a 1-DOF family (BlendEnvelope.Family), whose interval is
// widened by the certified per-subinterval motion bound so motion between samples stays inside, or a chord link (angle and distance
// from the stored local, each widened the same way). Every visible shape's vertex box corners (rigid: in the shape's frame; skinned:
// per bone, the skin-transformed vertices it weights, in the bone's frame) are bounded over the chain: the product box when every
// driven node on the chain has a family, else the chord box. The image of a box's corners bounds the image of everything inside it
// (each pose is an affine map), and a skinned vertex is a convex combination of its bones' images, inside the union of their boxes.
// Constant node scales are folded into the translations and the point (product or chord box); a node whose scale changes between
// poses takes the chord bound extended by its scale range (|s - s_ref| |x_ref| added, lower links' deviation scaled by the largest
// s), composed down the chain by the triangle inequality.

public static partial class NifMotion
{
    public const string BlendProductLabel = "uncertified: blend envelope (product of per-node 1-DOF intervals over the graph's blended poses)";
    public const string BlendChordLabel = "uncertified: blend envelope (chord bound: each blended node within its poses' angle and distance of its stored local; an outer bound)";

    sealed record NodeEnvelope(BlendFamily? Family, ChordLink Chord, float Scale, string? Why, float ScaleMin = float.NaN, float ScaleMax = float.NaN);

    /// <summary>The model-space box of every blend the graph can produce over the model's visible shapes (null box = nothing to add),
    /// the label naming the bound used, or a refusal naming the cause.</summary>
    public static (Aabb Box, string? Label, string? Refusal) BlendBox(byte[] nif, MotionModel model, Func<string, byte[]?>? read, float delta)
    {
        if (model.Status != "moving") return (Aabb.Empty, null, null);
        var rig = RigOf(nif, model, read);
        if (!rig.BlendActive) return (Aabb.Empty, null, null);
        var env = new Dictionary<string, NodeEnvelope>(StringComparer.Ordinal);
        foreach (var node in rig.Sources.Values.SelectMany(s => s.Nodes).Distinct(StringComparer.Ordinal))
        {
            if (!rig.NodeByName.TryGetValue(node, out var n)) continue;
            var e = Envelope(rig, n, node);
            if (e.Why is not null) return (Aabb.Empty, null, $"blend: node '{node}': {e.Why}");
            env[node] = e;
        }
        var box = Aabb.Empty; bool chordUsed = false;
        foreach (var rs in rig.Shapes)
        {
            if (rs.Hidden || rs.Particle || rs.Vertices.Count == 0) continue;
            if (rs.Refusal is not null) return (Aabb.Empty, null, $"blend: shape '{rs.Name}': {rs.Refusal}");
            if (rs.Weights is null)
            {
                var (b, chord, why) = ChainBox(rig, env, (NiAVObject)rs.Shape, Corners(rs.Vertices), delta);
                if (why is not null) return (Aabb.Empty, null, $"blend: shape '{rs.Name}': {why}");
                box = box.Union(b); chordUsed |= chord;
            }
            else
            {
                var sources = rs.WeightsAlt is null ? new[] { rs.Weights } : new[] { rs.Weights, rs.WeightsAlt };
                foreach (var w in sources)
                    for (int bi = 0; bi < rs.Bones!.Count; bi++)
                    {
                        var pts = new List<Vector3>();
                        for (int v = 0; v < rs.Vertices.Count; v++)
                            if (w[v].Any(x => x.Bone == bi && x.W > 0)) pts.Add(rs.Skin![bi].Apply(rs.Vertices[v]));
                        if (pts.Count == 0) continue;
                        var (b, chord, why) = ChainBox(rig, env, rs.Bones[bi], Corners(pts), delta);
                        if (why is not null) return (Aabb.Empty, null, $"blend: shape '{rs.Name}' bone '{rs.Bones[bi].Name?.String}': {why}");
                        box = box.Union(b); chordUsed |= chord;
                    }
            }
        }
        return (box, box.IsEmpty ? null : chordUsed ? BlendChordLabel : BlendProductLabel, null);
    }

    /// <summary>The blend envelope (model space) of points given in <paramref name="leaf"/>'s frame, over every blend the graph can
    /// produce; a refusal names a node whose envelope cannot be formed.</summary>
    internal static (Aabb Box, string? Why) BlendPointBox(MotionRig rig, NiAVObject leaf, List<Vector3> points, float delta)
    {
        var env = new Dictionary<string, NodeEnvelope>(StringComparer.Ordinal);
        foreach (var node in rig.Sources.Values.SelectMany(s => s.Nodes).Distinct(StringComparer.Ordinal))
        {
            if (!rig.NodeByName.TryGetValue(node, out var n)) continue;
            var e = Envelope(rig, n, node);
            if (e.Why is not null) return (Aabb.Empty, $"blend: node '{node}': {e.Why}");
            env[node] = e;
        }
        var (b, _, why) = ChainBox(rig, env, leaf, points, delta);
        return (b, why);
    }

    static List<Vector3> Corners(IReadOnlyList<Vector3> pts)
    {
        var lo = pts.Aggregate(new Vector3(float.MaxValue), Vector3.Min); var hi = pts.Aggregate(new Vector3(float.MinValue), Vector3.Max);
        return Enumerable.Range(0, 8).Select(k => new Vector3((k & 1) == 0 ? lo.X : hi.X, (k & 2) == 0 ? lo.Y : hi.Y, (k & 4) == 0 ? lo.Z : hi.Z)).Distinct().ToList();
    }

    /// <summary>One driven node's envelope over every source that drives it (and the stored local).</summary>
    static NodeEnvelope Envelope(MotionRig rig, NiAVObject n, string node)
    {
        var st = rig.Stored(n, false);
        var poses = new List<(Quaternion R, Vector3 T)>();
        var qs = Quaternion.Normalize(Quaternion.CreateFromRotationMatrix(st.R));
        poses.Add((qs, st.T));
        float widenA = 0, widenT = 0, widenS = 0, maxAng = 0, maxTr = 0, sMin = st.S, sMax = st.S;
        foreach (var s in rig.Sources.Values.Where(s => s.Nodes.Contains(node)))
        {
            float a = s.Animation.Start, b = s.Animation.End;
            var times = s.KeyTimes().Where(t => t >= a && t <= b).Append(a).Append(b);
            if (b > a) times = times.Concat(Enumerable.Range(1, 63).Select(k => a + (b - a) * k / 64f));
            var ts = times.Distinct().OrderBy(t => t).ToList();
            for (int k = 0; k < ts.Count; k++)
            {
                var l = s.Local(node, st, ts[k]);
                if (!float.IsFinite(l.S)) return new(null, new ChordLink(qs, st.T, 0, 0), 1, "a non-finite scale");
                sMin = MathF.Min(sMin, l.S); sMax = MathF.Max(sMax, l.S);
                var q = Quaternion.Normalize(Quaternion.CreateFromRotationMatrix(l.R));
                poses.Add((q, l.T));
                maxAng = MathF.Max(maxAng, HavokClip.AngleBetween(q, qs)); maxTr = MathF.Max(maxTr, Vector3.Distance(l.T, st.T));
                if (k + 1 < ts.Count)
                {
                    var bd = s.Bound(node, st, ts[k], ts[k + 1]);
                    widenA = MathF.Max(widenA, bd.A); widenT = MathF.Max(widenT, bd.T); widenS = MathF.Max(widenS, MathF.Abs(bd.S));
                }
            }
        }
        var chord = new ChordLink(qs, st.T, maxAng + widenA, maxTr + widenT);
        if (sMax - sMin > 1e-4f * MathF.Max(1, MathF.Abs(st.S)) || widenS > 1e-6f)
            return new(null, chord, st.S, null, sMin - widenS, sMax + widenS);   // the scale changes: chord bound with the scale range
        var fam = BlendEnvelope.Family(poses, out _);
        if (fam is { Kind: "rotation" }) fam = fam with { Min = fam.Min - widenA, Max = fam.Max + widenA };
        else if (fam is { Kind: "translation" }) fam = fam with { Min = fam.Min - widenT, Max = fam.Max + widenT };
        else if (fam is { Kind: "fixed" } && (widenA > 1e-6f || widenT > 1e-6f)) fam = null;   // motion between samples: not provably fixed
        return new(fam, chord, st.S, null);
    }

    /// <summary>Chord bound with scale: link i (root first) deviates from its reference (QRef, TRef, Scale) by angle <= MaxAngle,
    /// translation <= MaxTranslation, and its scale lies in [ScaleMin, ScaleMax] (a constant-scale link: both = Scale). From the
    /// leaf up: d_i <= tau_i + S_i d_(i+1) + |s - s_i|max |x_ref| + s_i 2 sin(theta_i / 2) |x_ref|, x_ref the reference position of
    /// the point in link i's frame before link i acts. The box is the reference world position grown by d_0.</summary>
    static Aabb ScaledChordBox(IReadOnlyList<NodeEnvelope> chain, Vector3 point)
    {
        int n = chain.Count; var x = point; double d = 0;
        for (int i = n - 1; i >= 0; i--)
        {
            var e = chain[i]; float s0 = e.Scale;
            float lo = float.IsFinite(e.ScaleMin) ? e.ScaleMin : s0, hi = float.IsFinite(e.ScaleMax) ? e.ScaleMax : s0;
            double rho = x.Length();
            double sMaxAbs = Math.Max(Math.Abs(lo), Math.Abs(hi)), sDev = Math.Max(Math.Abs(hi - s0), Math.Abs(lo - s0));
            double c = 2 * Math.Sin(Math.Min(Math.Abs(e.Chord.MaxAngle), Math.PI) / 2);
            d = e.Chord.MaxTranslation + sMaxAbs * d + sDev * rho + Math.Abs(s0) * c * rho;
            x = Vector3.Transform(x * s0, Quaternion.Normalize(e.Chord.QRef)) + e.Chord.TRef;
        }
        float g = (float)d; g = g < d ? MathF.BitIncrement(g) : g;
        return new Aabb(new Vec3(MathF.BitDecrement(x.X - g), MathF.BitDecrement(x.Y - g), MathF.BitDecrement(x.Z - g)),
                        new Vec3(MathF.BitIncrement(x.X + g), MathF.BitIncrement(x.Y + g), MathF.BitIncrement(x.Z + g)), false);
    }

    /// <summary>The box of the points (in <paramref name="leaf"/>'s frame) over every blend, through the chain from the root's child
    /// down to <paramref name="leaf"/> (the root's own transform left out, as the motion reader).</summary>
    static (Aabb Box, bool Chord, string? Why) ChainBox(MotionRig rig, Dictionary<string, NodeEnvelope> env, NiAVObject leaf, List<Vector3> points, float delta)
    {
        var chain = rig.Chain(leaf);
        var links = new List<(NodeEnvelope E, NodeXf Stored)>();
        for (int i = chain.Count - 2; i >= 0; i--)
        {
            var node = chain[i]; var st = rig.Stored(node, false);
            if (node.Name?.String is { } nm && env.TryGetValue(nm, out var e)) links.Add((e, st));
            else
            {
                var q = Quaternion.Normalize(Quaternion.CreateFromRotationMatrix(st.R));
                links.Add((new NodeEnvelope(new BlendFamily("fixed", q, st.T, Vector3.UnitZ, 0, 0), new ChordLink(q, st.T, 0, 0), st.S, null), st));
            }
        }
        if (links.Any(l => float.IsFinite(l.E.ScaleMin)))
        {
            var sbox = Aabb.Empty;
            foreach (var p in points) sbox = sbox.Union(ScaledChordBox(links.Select(l => l.E).ToList(), p));
            return (sbox, true, null);
        }
        // fold constant scales: T_i' = (product of the scales above i) T_i, point' = (product of every scale) point
        float c = 1; var fams = new List<BlendFamily?>(); var chords = new List<ChordLink>();
        foreach (var (e, _) in links)
        {
            fams.Add(e.Family is { } f ? f with { T0 = f.T0 * c, Axis = f.Kind == "translation" ? f.Axis * c : f.Axis } : null);
            chords.Add(e.Chord with { TRef = e.Chord.TRef * c, MaxTranslation = e.Chord.MaxTranslation * c });
            c *= e.Scale;
        }
        var box = Aabb.Empty;
        bool product = fams.All(f => f is not null);
        foreach (var p in points)
        {
            var pp = p * c;
            box = box.Union(product ? BlendEnvelope.ProductBox(fams.Select(f => f!).ToList(), pp, delta) : BlendEnvelope.ChordBox(chords, pp));
        }
        return (box, !product, null);
    }
}
