using System.Numerics;
using HousecarlCore;
using NiflySharp;
using NiflySharp.Blocks;
using NiflySharp.Structs;

/// <summary>Known answers for the skin rule (reach region Part 3, `--reach-rules skin`, MotionReadOptions.SkinUnion): a skin
/// whose two weight sources (NiSkinData and the SSE skin partition) disagree. (i) A disagreement on a vertex no triangle draws
/// is not counted, and such a vertex enters no region; (ii) a disagreement on drawn vertices is posed under BOTH sources (the
/// region holds both placements, labelled); (iii) a drawn vertex with no weight under one source still refuses. Fixtures are
/// the two-bone SSE strip (SkinRig, permuted palette) edited in one way each; expected positions are analytic (BoneB turns
/// about (20,0,0) by 90° at t = 1). Fixture controls hold on the base; the rule cases are red until the rule is implemented
/// (the option is a stub that throws). The canary is wrong by construction and must stay red.</summary>
static partial class MotionProof
{
    /// <summary>The SSE skin strip with one edit applied to its shape, skin data and partition.</summary>
    static byte[] SkinVariant(Action<NifFile, BSTriShape, NiSkinData, NiSkinPartition> edit)
    {
        var f = Load(SkinRig(Pi / 2, ssePermuted: true));
        var sh = f.GetShapes().OfType<BSTriShape>().Single();
        var si = (NiSkinInstance)f.Blocks[sh.SkinInstanceRef.Index];
        var sd = (NiSkinData)f.Blocks[si.Data.Index];
        var sp = (NiSkinPartition)f.Blocks[si.SkinPartition.Index];
        edit(f, sh, sd, sp);
        return Save(f, "skin variant");
    }

    /// <summary>NiSkinData weight of <paramref name="v"/> on skin bone <paramref name="bone"/> set to <paramref name="w"/>.</summary>
    static void SetDataWeight(NiSkinData sd, int bone, int v, float w)
    {
        var bd = sd.BoneList[bone];
        var list = bd.VertexWeights.ToList();
        int i = list.FindIndex(x => x.Index == v);
        if (i >= 0) { var e = list[i]; e.Weight = w; list[i] = e; } else list.Add(new BoneVertData { Index = (ushort)v, Weight = w });
        bd.VertexWeights = list; bd.NumVertices = (ushort)list.Count;
        sd.BoneList[bone] = bd;
    }

    /// <summary>Every SSE vertex record of <paramref name="v"/> (shape and partition copies) given all-zero weights.</summary>
    static void ZeroPartitionWeights(BSTriShape sh, NiSkinPartition sp, int v)
    {
        void Z(List<BSVertexDataSSE>? list)
        {
            if (list is null || v >= list.Count) return;
            var vd = list[v];
            for (int k = 0; k < 4; k++) vd.BoneWeights[k] = (Half)0f;
            list[v] = vd;
        }
        Z(sh.VertexDataSSE); Z(sp.VertexData);
    }

    /// <summary>The strip's last quad (vertices 6, 7 at x = 40) removed from every triangle list, so 6 and 7 are drawn by none.</summary>
    static void DropLastQuad(BSTriShape sh, NiSkinPartition sp)
    {
        static bool Keep(Triangle t) => t.V1 < 6 && t.V2 < 6 && t.V3 < 6;
        for (int p = 0; p < sp.Partitions.Count; p++)
        {
            var part = sp.Partitions[p];
            part.Triangles = part.Triangles?.Where(Keep).ToList();
            part.TrianglesCopy = part.TrianglesCopy?.Where(Keep).ToList();
            part.NumTriangles = (ushort)(part.Triangles?.Count ?? 0);
            sp.Partitions[p] = part;
        }
        if (sh.Triangles is { } own) sh.SetTriangles(Ver, own.Where(Keep).ToList());
    }

    static (Vector3 A, Vector3 B) TwoSourcePlacement(Vector3 v, float wAa, float wAb, float wBa, float wBb)
    {
        var turned = RotZ(v, new Vector3(20, 0, 0), Pi / 2);
        return (wAa * v + wAb * turned, wBa * v + wBb * turned);
    }

    static ReachOptions SkinRulesOn() => new(0.5, 65536, new ReachRules(Skin: true));

    public static void RunSkinRule(Action<string, bool, string> check)
    {
        // ================= canary (wrong by construction; must stay RED) =================
        Case(check, "skin-rule CANARY (must FAIL): the strip's tip (40,1,0) at t = 1 placed unturned", () =>
        {
            var b = SkinRig(Pi / 2, ssePermuted: true);
            var p = NifMotion.Pose(b, NifMotion.Read(b, null), "seq:Open", 1f, VolumeGeometry.Render);
            return (p.Soup is { } s && s.Vertices.Any(x => NearV(x, new Vector3(40, 1, 0), 1e-3f)), $"{p.Status} {p.Reason}");
        });

        // drawn disagreement: v4, v5 (x = 30) are A 0.5 / B 0.5 in the partition, A 0.8 / B 0.2 in NiSkinData
        byte[] drawn = SkinVariant((f, sh, sd, sp) => { foreach (int v in new[] { 4, 5 }) { SetDataWeight(sd, 0, v, 0.8f); SetDataWeight(sd, 1, v, 0.2f); } });
        // undrawn disagreement: v6, v7 (x = 40) drawn by no triangle and given no partition weight; NiSkinData keeps B = 1
        byte[] undrawn = SkinVariant((f, sh, sd, sp) => { DropLastQuad(sh, sp); ZeroPartitionWeights(sh, sp, 6); ZeroPartitionWeights(sh, sp, 7); });
        // drawn zero weight: v4, v5 keep their triangles but have no partition weight
        byte[] drawnZero = SkinVariant((f, sh, sd, sp) => { ZeroPartitionWeights(sh, sp, 4); ZeroPartitionWeights(sh, sp, 5); });

        // ================= fixture controls (existing code; hold on the base) =================
        Case(check, "skin-rule fixture control: drawn variant — rules off, the reader refuses 'skin weight sources disagree on 2 vertices'", () =>
        {
            var m = NifMotion.Read(drawn, null);
            return (m.Status == "refused" && (m.Reason ?? "").Contains("skin weight sources disagree on 2 vertices", StringComparison.Ordinal), $"{m.Status} {m.Reason}");
        });
        Case(check, "skin-rule fixture control: undrawn variant — 4 triangles remain, none uses vertex 6 or 7, and rules off it refuses 'disagree on 2 vertices'", () =>
        {
            var f = Load(undrawn);
            var sh = f.GetShapes().OfType<BSTriShape>().Single();
            var sp = f.Blocks.OfType<NiSkinPartition>().Single();
            var lists = new List<List<Triangle>> { sh.Triangles ?? new() };
            foreach (var part in sp.Partitions) { lists.Add(part.Triangles ?? new()); lists.Add(part.TrianglesCopy ?? new()); }
            bool none = lists.All(l => l.All(t => t.V1 < 6 && t.V2 < 6 && t.V3 < 6));
            var m = NifMotion.Read(undrawn, null);
            return (none && sp.Partitions[0].Triangles?.Count == 4 && m.Status == "refused" && (m.Reason ?? "").Contains("disagree on 2 vertices", StringComparison.Ordinal),
                    $"own {sh.Triangles?.Count} partition {sp.Partitions[0].Triangles?.Count} copy {sp.Partitions[0].TrianglesCopy?.Count} none-use-6-7 {none} | {m.Status} {m.Reason}");
        });
        Case(check, "skin-rule fixture control: drawn-zero variant — rules off, the reader refuses on the skin", () =>
        {
            var m = NifMotion.Read(drawnZero, null);
            return (m.Status == "refused" && (m.Reason ?? "").Contains("skin", StringComparison.Ordinal), $"{m.Status} {m.Reason}");
        });
        Case(check, "skin-rule fixture control: rules off, MeshReach refuses the drawn variant as today (default output unchanged)", () =>
        {
            var r = MeshReach.OfModel(drawn, Origin, Origin, 1f);
            return (r.Status == "refused" && (r.Reason ?? "").Contains("skin weight sources disagree", StringComparison.Ordinal), SayA7(r));
        });

        // ================= K-S1: a disagreement no triangle draws is not counted, and the vertex enters no region =================
        // The undrawn variant reads; at the stored pose its drawn vertices span x 0..30 (v6, v7 at x 40 are drawn by nothing):
        // box [0,30]x[-1,1]x[0,0]. Rejects counting undrawn vertices (refusal kept) and boxing every vertex (x to 40).
        Case(check, "skin-rule K-S1: undrawn disagreement — reads; stored-pose box [0,30]x[-1,1]x[0,0] (not x 40); no skin-union label", () =>
        {
            var m = NifMotion.Read(undrawn, null, null, new MotionReadOptions(SkinUnion: true));
            if (m.Status == "refused") return (false, $"{m.Status} {m.Reason}");
            var p = NifMotion.Pose(undrawn, m, "stored", 0f, VolumeGeometry.Render);
            if (p.Soup is null) return (false, $"{p.Status} {p.Reason}");
            var box = SpatialOverlap.SoupBounds(p.Soup);
            var r = MeshReach.OfModel(undrawn, Origin, Origin, 1f, null, SkinRulesOn());
            return (BoxIs(box, 0, -1, 0, 30, 1, 0, 0.01f) && !r.Labels.Contains(MeshReach.SkinUnionLabel) && r.Status != "refused",
                    $"stored box {ShowA7(box)} | reach {ShowRR(r)}");
        });

        // ================= K-S2: drawn disagreement posed under both sources =================
        // At t = 1 (BoneB turned 90° about (20,0,0)) vertex (30,1,0) lies at 0.8 v + 0.2 R(v) under NiSkinData and 0.5 v + 0.5 R(v)
        // under the partition; the posed soup holds BOTH (and the same for (30,-1,0)). Rejects one source only.
        Case(check, "skin-rule K-S2: drawn disagreement — the pose at t = 1 holds vertex (30,±1,0) under both weight sources", () =>
        {
            var m = NifMotion.Read(drawn, null, null, new MotionReadOptions(SkinUnion: true));
            if (m.Status == "refused") return (false, $"{m.Status} {m.Reason}");
            var p = NifMotion.Pose(drawn, m, "seq:Open", 1f, VolumeGeometry.Render);
            if (p.Soup is null) return (false, $"{p.Status} {p.Reason}");
            var want = new List<Vector3>();
            foreach (var y in new[] { 1f, -1f }) { var (a, b) = TwoSourcePlacement(new Vector3(30, y, 0), 0.8f, 0.2f, 0.5f, 0.5f); want.Add(a); want.Add(b); }
            var v = p.Soup.Vertices;
            return (want.All(w => v.Any(x => NearV(x, w, 1e-3f))), $"want {string.Join(" ", want.Select(S))} found {string.Join(",", want.Select(w => v.Any(x => NearV(x, w, 1e-3f))))}");
        });
        // Fixture control for K-S2's own check: a soup holding only one source's placements (NiSkinData only, or the partition
        // only) must fail it, so K-S2 can tell the union from either source alone. Analytic soups; holds on the base.
        Case(check, "skin-rule fixture control: K-S2's check rejects a one-source pose (NiSkinData only; partition only)", () =>
        {
            var both = new List<Vector3>(); var onlyA = new List<Vector3>(); var onlyB = new List<Vector3>();
            foreach (var y in new[] { 1f, -1f })
            {
                var (a, b) = TwoSourcePlacement(new Vector3(30, y, 0), 0.8f, 0.2f, 0.5f, 0.5f);
                both.Add(a); both.Add(b); onlyA.Add(a); onlyB.Add(b);
            }
            bool Holds(List<Vector3> soup) => both.All(w => soup.Any(x => NearV(x, w, 1e-3f)));
            double sep = Vector3.Distance(both[0], both[1]);
            return (Holds(both) && !Holds(onlyA) && !Holds(onlyB) && sep > 1, $"both {Holds(both)} A-only {Holds(onlyA)} B-only {Holds(onlyB)} A-B separation {sep:0.###} u");
        });
        // The reach region with the skin rule: uncertified, labelled, and its box holds both placements of (30,±1,0) at t = 1.
        Case(check, "skin-rule K-S2b: drawn disagreement — reach region labelled '" + MeshReach.SkinUnionLabel + "' and holding both placements", () =>
        {
            var r = MeshReach.OfModel(drawn, Origin, Origin, 1f, null, SkinRulesOn());
            var pts = new List<Vector3>();
            foreach (var y in new[] { 1f, -1f }) { var (a, b) = TwoSourcePlacement(new Vector3(30, y, 0), 0.8f, 0.2f, 0.5f, 0.5f); pts.Add(a); pts.Add(b); }
            bool holds = pts.All(q => Contains(r.Box, AB(q.X, q.Y, q.Z, q.X, q.Y, q.Z)));
            return (r.Status == "uncertified" && r.Labels.Contains(MeshReach.SkinUnionLabel) && holds, ShowRR(r));
        });

        // ================= K-S3: a drawn vertex with no weight under one source still refuses =================
        // Rejects the union applied to a vertex one source cannot place at all (the engine's placement of a zero-weight drawn
        // vertex is the question no file settles).
        Case(check, "skin-rule K-S3: drawn zero-weight vertex — still refused under the rule, reason names the vertices without weight", () =>
        {
            var r = MeshReach.OfModel(drawnZero, Origin, Origin, 1f, null, SkinRulesOn());
            return (r.Status == "refused" && (r.Reason ?? "").Contains("no weight", StringComparison.Ordinal) && !(r.Reason ?? "").Contains("NotImplemented", StringComparison.Ordinal)
                    && !(r.Reason ?? "").Contains("not implemented", StringComparison.Ordinal), ShowRR(r));
        });
    }
}
