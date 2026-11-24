using System.Numerics;
using NiflySharp.Blocks;

using HousecarlCore;

namespace SkyWright.Engine;

// PINNED HINGE ISLANDS (GATE-A §2c). The hinge tree is PhysicsReach's (one reading with the reach envelope). Every posed
// vertex is the weighted sum of its stored model points, each turned along its body's hinge path; the soup's layout is the
// motion reader's (NifMotion.PoseWith: per visible shape, the skin rule's drawn vertices under each kept weight source). A
// cell is a box of hinge angles (offsets from the stored pose, in [lo, hi] of each hinge: either sense), posed at its centre;
// its bound is the sum over hinges of L_i x half-width_i (the envelope's Lipschitz bound, over this layout's points). The
// limit corners are poses of their own (a centre never reaches a limit).

/// <summary>The posed layout of a hinge island: per soup vertex its weighted (stored model point, body) entries; the indices and
/// tags of the stored soup; the per-hinge Lipschitz constants over these points.</summary>
internal sealed class HingeLayout
{
    public required List<(Vector3 P, int Body, float W)[]> Verts { get; init; }
    public required int[] Indices { get; init; }
    public required string[] Tags { get; init; }
    public required double[] L { get; init; }
    public double Mag { get; init; }

    public TriangleSoup Pose(PhysicsReach.HingeTree tree, double[] ang)
    {
        var v = new Vector3[Verts.Count];
        for (int i = 0; i < v.Length; i++)
        {
            var acc = Vector3.Zero;
            foreach (var (p, body, w) in Verts[i]) acc += w * tree.Pose(p, body, ang);
            v[i] = acc;
        }
        return new TriangleSoup(v, Indices);
    }

    public static (HingeLayout? Layout, string? Refusal) Of(PhysicsReach.HingeTree tree, MotionRig rig, VolumeGeometry g)
    {
        var verts = new List<(Vector3 P, int Body, float W)[]>(); var idx = new List<int>(); var tags = new List<string>();
        if (g == VolumeGeometry.Collision)
        {
            rig.Collision ??= HavokCollision.Decode(rig.Nif, includeTriangles: true);
            float unit = HavokCollision.UnitScaleFor(rig.Collision.StreamVersion);
            foreach (var body in rig.Collision.Bodies.Where(b => b.Solid))
            {
                if (body.Unread.Count > 0) return (null, "collision_incomplete: a solid body has an unread shape");
                var xf = body.Node is null ? NodeXf.Identity : rig.Model(body.Node, null, 0, true);
                int bd = body.Node is null ? -1 : tree.BodyOf(body.Node);
                int b0 = verts.Count;
                foreach (var p in body.Vertices) verts.Add(new[] { (xf.Apply(p * unit), bd, 1f) });
                foreach (var k in body.Indices) idx.Add(b0 + k);
            }
            if (idx.Count == 0) return (null, "no_solid_collision");
            tags.AddRange(Enumerable.Repeat(SurfaceMaterial.Collision, idx.Count / 3));
        }
        else
        {
            foreach (var rs in rig.Shapes)
            {
                if (rs.Hidden || rs.Particle) continue;
                if (rs.Refusal is not null) return (null, $"shape '{rs.Name}': {rs.Refusal}");
                var rigid = rig.Model((NiAVObject)rs.Shape, null, 0, false); int rigidBody = tree.BodyOf((NiAVObject)rs.Shape);
                List<NodeXf>? models = rs.Bones?.Select(bn => rig.Model(bn, null, 0, false)).ToList();
                List<int>? bodyOfBone = rs.Bones?.Select(tree.BodyOf).ToList();
                (Vector3, int, float)[] Entry(List<(int Bone, float W)>[]? w, int k)
                {
                    if (w is null) return new[] { (rigid.Apply(rs.Vertices[k]), rigidBody, 1f) };
                    float ws = w[k].Sum(x => x.W);
                    return w[k].Select(x => (models![x.Bone].Apply(rs.Skin![x.Bone].Apply(rs.Vertices[k])), bodyOfBone![x.Bone], x.W / ws)).ToArray();
                }
                foreach (var t in rs.Triangles)
                    if (t.V1 >= rs.Vertices.Count || t.V2 >= rs.Vertices.Count || t.V3 >= rs.Vertices.Count)
                        return (null, $"shape '{rs.Name}': a triangle indexes outside its vertices");
                if (rs.DrawnOnly)
                {
                    foreach (var w in rs.WeightsAlt is null ? new[] { rs.Weights } : new[] { rs.Weights, rs.WeightsAlt })
                    {
                        var map = new Dictionary<int, int>();
                        foreach (var t in rs.Triangles)
                        {
                            foreach (int vi in new[] { (int)t.V1, (int)t.V2, (int)t.V3 })
                                if (!map.ContainsKey(vi)) { map[vi] = verts.Count; verts.Add(Entry(w, vi)); }
                            idx.Add(map[t.V1]); idx.Add(map[t.V2]); idx.Add(map[t.V3]);
                            tags.Add(rs.Tag);
                        }
                    }
                    continue;
                }
                int b0 = verts.Count;
                for (int k = 0; k < rs.Vertices.Count; k++) verts.Add(Entry(rs.Weights, k));
                foreach (var t in rs.Triangles) { idx.Add(b0 + t.V1); idx.Add(b0 + t.V2); idx.Add(b0 + t.V3); tags.Add(rs.Tag); }
            }
            if (idx.Count == 0) return (null, "render: no visible triangles");
        }
        var L = tree.Lipschitz(verts.SelectMany(v => v.Select(x => (x.P, x.Body))));
        double mag = verts.Count == 0 ? 0 : verts.Max(v => v.Max(x => (double)x.P.Length()));
        return (new HingeLayout { Verts = verts, Indices = idx.ToArray(), Tags = tags.ToArray(), L = L, Mag = mag }, null);
    }
}

/// <summary>A box of hinge angles, posed at its centre (a limit corner: lo = hi).</summary>
internal sealed class HingeCell : MemberCell
{
    readonly double[] lo, hi;
    public HingeCell(MemberCtx ctx, double[] lo, double[] hi)
    {
        this.lo = lo; this.hi = hi;
        var L = ctx.Hinge!.L;
        double d = 0; bool point = true;
        for (int i = 0; i < lo.Length; i++) { d += (hi[i] - lo[i]) / 2 * L[i]; if (hi[i] > lo[i]) point = false; }
        Delta = point ? 0 : d + 1e-4 + 1e-6 * ctx.Hinge.Mag;
    }
    double[] Centre => lo.Select((l, i) => (l + hi[i]) / 2).ToArray();
    public override string Origin => MemberMotion.OriginSwing;
    public override bool Splittable => Delta > 0 && Enumerable.Range(0, lo.Length).Any(i => hi[i] - lo[i] > 1e-9);
    public override MotionPoseSoup Pose(MemberCtx ctx)
    {
        var hl = ctx.Hinge!;
        return new("posed", null, hl.Pose(ctx.Reading.Tree!, Centre), hl.Tags, "hinge", 0);
    }

    public static IEnumerable<HingeCell> Initial(MemberCtx ctx, HingeLayout hl)
    {
        var h = ctx.Reading.Tree!.Hinges;
        var lo = h.Select(x => x.Lo).ToArray(); var hi = h.Select(x => x.Hi).ToArray();
        yield return new HingeCell(ctx, lo, hi);
        if (h.Count > 6) yield break;
        for (int mask = 0; mask < (1 << h.Count); mask++)
        {
            var c = Enumerable.Range(0, h.Count).Select(i => (mask & (1 << i)) != 0 ? hi[i] : lo[i]).ToArray();
            yield return new HingeCell(ctx, c, (double[])c.Clone());
        }
    }

    public override IEnumerable<MemberCell> Split(MemberCtx ctx)
    {
        var L = ctx.Hinge!.L;
        int best = 0; double wmax = -1;
        for (int i = 0; i < lo.Length; i++) { double w = (hi[i] - lo[i]) * L[i]; if (w > wmax) { wmax = w; best = i; } }
        double m = (lo[best] + hi[best]) / 2;
        var hiA = (double[])hi.Clone(); hiA[best] = m;
        var loB = (double[])lo.Clone(); loB[best] = m;
        return new[] { new HingeCell(ctx, lo, hiA), new HingeCell(ctx, loB, hi) };
    }

    public override MemberDecision Witness(MemberCtx ctx, string? note, int evals) =>
        new("witness", null, MemberMotion.OriginSwing, "hinge", null, null, null, null, Centre, MemberMotion.SwingLabel, note, 0, evals, true);

    public override string Describe() => $"hinge at [{string.Join(",", Centre.Select(x => (x * 180 / Math.PI).ToString("0.##", System.Globalization.CultureInfo.InvariantCulture)))}] deg";
    public override string SampleId => "hinge";
}

public static partial class MemberMotion
{
    /// <summary>P2 for a swing witness: whether a partner model has any solid collision body (the COLL layer filter between the two
    /// bodies is not read; the meaning is labelled as read from solid status).</summary>
    public static bool HasSolidCollision(byte[] nif)
    {
        try
        {
            var f = new NiflySharp.NifFile();
            using var ms = new MemoryStream(nif, writable: false);
            if (f.Load(ms) != 0) return false;
            return HavokCollision.Decode(f, includeTriangles: false).Bodies.Any(b => b.Solid);
        }
        catch (Exception) { return false; }
    }
}
