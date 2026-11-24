using System.Numerics;
using NiflySharp.Blocks;

using HousecarlCore;

namespace SkyWright.Engine;

// PATH REGION OF A MODEL (reach rule `path`, Part 3b). A NiPathInterpolator moves its node's origin along the spline of its NiPosData
// keys (positions in the node's parent frame) and may turn it to follow the path. The region of everything under the node is the
// path's control-polygon hull (PathReach.PathHull, both tangent readings) carried by the parent's model transform, grown by the
// subtree's radius about the node origin, any rotation allowed: R <= |T_1| + s_1 (|T_2| + s_2 (... + s_k rho)), each |T_i| and s_i
// the largest the link's poses reach (every source's key times, span ends and 64 uniform samples, widened by the certified motion
// between samples) and rho the largest vertex distance in the shape's frame (rotations never lengthen). Percent keys must keep the
// node on the path: values within 1e-3 of [0, 1] are read as clamped to it (labelled); farther outside refuses by name. A parent
// chain that moves refuses by name.

public static partial class NifMotion
{
    public const string PathHullLabel = "uncertified: path - the node's spline control-polygon hull (both tangent readings) grown by its subtree's radius, any rotation";
    public const string PathPercentClampLabel = "uncertified: path percent keys exceed [0, 1] by at most 1e-3, read as clamped to the path";

    /// <summary>The model-space box of every point under a path-driven node (empty when the model has none), its labels, or a refusal.</summary>
    public static (Aabb Box, IReadOnlyList<string> Labels, string? Refusal) PathBox(byte[] nif, MotionModel model, Func<string, byte[]?>? read)
    {
        var labels = new List<string>();
        if (model.Status == "refused") return (Aabb.Empty, labels, null);
        var rig = RigOf(nif, model, read);
        if (rig.PathNodes.Count == 0) return (Aabb.Empty, labels, null);
        var blocks = rig.Nif.Blocks; var box = Aabb.Empty;
        var driven = new HashSet<string>(rig.Sources.Values.SelectMany(s => s.Nodes), StringComparer.Ordinal);
        foreach (var (node, pi) in rig.PathNodes)
        {
            string nm = node.Name?.String ?? "?";
            if (pi.PathData?.Index is not int pd || pd < 0 || pd >= blocks.Count || blocks[pd] is not NiPosData pos || pos.Data.Keys is not { Count: > 0 } keys)
                return (Aabb.Empty, labels, $"path on '{nm}': no position keys");
            if (pi.PercentData?.Index is int fd && fd >= 0 && fd < blocks.Count && blocks[fd] is NiFloatData pct && pct.Data.Keys is { Count: > 0 } pk)
            {
                var vals = pk.Select(k => k.Value).ToList();
                if (vals.Any(v => !(v >= -1e-3f && v <= 1 + 1e-3f))) return (Aabb.Empty, labels, $"path on '{nm}': " + PathReach.PercentRefusal(vals));
                if (PathReach.PercentRefusal(vals) is not null && !labels.Contains(PathPercentClampLabel)) labels.Add(PathPercentClampLabel);
            }
            // both tangent readings (NifMotionKeys reads out = Backward, in = Forward; PathReach's synthetic cases the other way):
            // the hull of the union holds either curve
            var ka = keys.Select(k => new PathKey(k.Time, k.Value, k.Forward, k.Backward, k.TBC is { } tb ? tb.T : 0, k.TBC is { } tc ? tc.C : 0, k.TBC is { } tq ? tq.B : 0)).ToList();
            var kb = keys.Select(k => new PathKey(k.Time, k.Value, k.Backward, k.Forward, k.TBC is { } tb ? tb.T : 0, k.TBC is { } tc ? tc.C : 0, k.TBC is { } tq ? tq.B : 0)).ToList();
            string kt = pos.Data.Interpolation.ToString();
            var hull = PathReach.PathHull(ka, kt, out var why).Union(PathReach.PathHull(kb, kt, out var why2));
            if (why is not null || why2 is not null) return (Aabb.Empty, labels, $"path on '{nm}': {why ?? why2}");
            // the parent chain must be still: the hull is carried by its model transform
            var parent = rig.Nif.GetParentNode(node) as NiAVObject;
            if (parent is not null && rig.Chain(parent).Any(p => p.Name?.String is { } pn && driven.Contains(pn)))
                return (Aabb.Empty, labels, $"path on '{nm}': its parent chain moves");
            var px = parent is null ? NodeXf.Identity : rig.Model(parent, null, 0, false);
            double R = PathSubtreeRadius(rig, node, out var rwhy);
            if (rwhy is not null) return (Aabb.Empty, labels, $"path on '{nm}': {rwhy}");
            var c = Enumerable.Range(0, 8).Select(k => px.Apply(new Vector3((k & 1) == 0 ? hull.Min.X : hull.Max.X, (k & 2) == 0 ? hull.Min.Y : hull.Max.Y, (k & 4) == 0 ? hull.Min.Z : hull.Max.Z)));
            var hb = Aabb.Empty; foreach (var p in c) hb = hb.Add(p);
            float g = (float)R; g = g < R ? MathF.BitIncrement(g) : g;
            box = box.Union(new Aabb(new Vec3(MathF.BitDecrement(hb.Min.X - g), MathF.BitDecrement(hb.Min.Y - g), MathF.BitDecrement(hb.Min.Z - g)),
                                     new Vec3(MathF.BitIncrement(hb.Max.X + g), MathF.BitIncrement(hb.Max.Y + g), MathF.BitIncrement(hb.Max.Z + g)), false));
            if (!labels.Contains(PathHullLabel)) labels.Add(PathHullLabel);
        }
        return (box, labels, null);
    }

    /// <summary>The radius about a path node's origin (in the node's parent frame, the node's own scale applied) that holds every point
    /// of the visible, non-particle shapes under it, any rotation: R = max over shapes of |T_1| + s_1 (|T_2| + s_2 (... + s_k rho)). A
    /// skinned shape under the node refuses (<paramref name="refusal"/>).</summary>
    internal static double PathSubtreeRadius(MotionRig rig, NiAVObject node, out string? refusal, bool drawnOnly = false)
    {
        refusal = null;
        double R = 0;
        // drawnOnly (the path tube, a measure): undrawn shapes take no part (ShapeDrawing); the reach box (PathBox) keeps them
        foreach (var rs in rig.Shapes.Where(s => !s.Hidden && !s.Particle && (!drawnOnly || s.Drawn) && s.Chain.Contains(node)))
        {
            if (rs.Bones is not null) { refusal = "a skinned shape under the path node is not in the path model"; return 0; }
            double rho = rs.Vertices.Count == 0 ? 0 : rs.Vertices.Max(v => (double)v.Length());
            // links from the shape up to (not including) the path node
            foreach (var link in rs.Chain.TakeWhile(x => !ReferenceEquals(x, node)))
            {
                var (tMax, sMax) = LinkReach(rig, link);
                rho = tMax + sMax * rho;
            }
            rho *= Math.Abs(node.Scale == 0 ? 1 : node.Scale);
            R = Math.Max(R, rho);
        }
        return R;
    }

    /// <summary>A link's largest translation length and scale over every source that drives it (sampled, widened by the certified
    /// motion between samples); a link no source drives keeps its stored values.</summary>
    static (double T, double S) LinkReach(MotionRig rig, NiAVObject link)
    {
        var st = rig.Stored(link, false);
        double t = st.T.Length(), s = Math.Abs(st.S);
        string? nm = link.Name?.String;
        if (nm is null) return (t, s);
        foreach (var src in rig.Sources.Values.Where(x => x.Nodes.Contains(nm)))
        {
            float a = src.Animation.Start, b = src.Animation.End;
            var ts = src.KeyTimes().Where(x => x >= a && x <= b).Append(a).Append(b).Concat(b > a ? Enumerable.Range(1, 63).Select(k => a + (b - a) * k / 64f) : Array.Empty<float>()).Distinct().OrderBy(x => x).ToList();
            for (int k = 0; k < ts.Count; k++)
            {
                var l = src.Local(nm, st, ts[k]);
                double wT = 0, wS = 0;
                if (k + 1 < ts.Count) { var bd = src.Bound(nm, st, ts[k], ts[k + 1]); wT = bd.T; wS = Math.Abs(bd.S); }
                t = Math.Max(t, l.T.Length() + wT); s = Math.Max(s, Math.Abs(l.S) + wS);
            }
        }
        return (t, s);
    }
}
