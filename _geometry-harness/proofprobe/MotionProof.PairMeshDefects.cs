using System.Numerics;
using HousecarlCore;
using NiflySharp;
using NiflySharp.Blocks;
using NiflySharp.Structs;

/// <summary>Known answers for the pair-measure mesh defects (2026-10-05, Group 6 transfer D1/D2): a member mesh holding zero-area
/// triangles refused the whole surface pair, and a render shape that declares no vertices and no triangles refused its member as
/// unreadable. Each case names the wrong answer it rejects. Red at the test commit except the cases marked guard.</summary>
static partial class MotionProof
{
    // A horizontal plate z = 0, x 0..100, y -10..10, crossed by QuadB (the vertical quad in y = 0.0005, x 50..150, z -10..10) along
    // x 50..100: crossing length 50. The zero-area triangles below lie inside QuadB's face, away from the crossing line.
    static TriangleSoup PlateWith(params Vector3[][] zeroArea)
    {
        var v = new List<Vector3> { new(0, -10, 0), new(100, -10, 0), new(100, 10, 0), new(0, 10, 0) };
        var i = new List<int> { 0, 1, 2, 0, 2, 3 };
        foreach (var t in zeroArea) { int b = v.Count; v.AddRange(t); i.AddRange(t.Length == 3 ? new[] { b, b + 1, b + 2 } : new[] { b, b, b + 1 }); }
        return new TriangleSoup(v.ToArray(), i.ToArray());
    }
    // three distinct collinear vertices in QuadB's plane (x 60..80, z 5): a needle of length 20 lying in B's face
    static readonly Vector3[] Collinear = { new(60, 0.0005f, 5), new(80, 0.0005f, 5), new(70, 0.0005f, 5) };
    // two vertices used with a repeated index (b, b, b+1): a repeated-index triangle in B's face
    static readonly Vector3[] Repeated = { new(60, 0.0005f, -5), new(80, 0.0005f, -5) };

    /// <summary>QuadB plus one zero-area triangle of its own: three collinear vertices in its plane at z = 5, x 120..140 (off the
    /// plate).</summary>
    static TriangleSoup QuadBWithCollinear()
    {
        var v = QuadB.Vertices.Concat(new[] { new Vector3(120, 0.0005f, 5), new Vector3(140, 0.0005f, 5), new Vector3(130, 0.0005f, 5) }).ToArray();
        return new TriangleSoup(v, QuadB.Indices.Concat(new[] { 4, 5, 6 }).ToArray());
    }

    /// <summary>root -> "Plate" (one triangle) and an empty BSTriShape "Walls.001" declaring no vertices and no triangles.</summary>
    static byte[] EmptyShapeNif(bool withEmpty)
    {
        var f = new NifFile(); f.Create(Ver, withRootNode: true);
        var root = f.GetRootNodes().First(); root.Flags_ui = 0xE;
        root.Children.AddBlockRef(f.AddBlock(Shape("Plate", new TriangleSoup(new[] { new Vector3(0, 0, 0), new Vector3(10, 0, 0), new Vector3(0, 10, 0) }, new[] { 0, 1, 2 }))));
        if (withEmpty)
        {
            var e = new BSTriShape { Name = new NiStringRef("Walls.001"), Flags_ui = 0xE, Scale = 1f };
            e.Create(Ver, new List<Vector3>(), new List<Triangle>(), new(), new());
            e.IsSkinned = false;
            root.Children.AddBlockRef(f.AddBlock(e));
        }
        return Save(f, "empty shape");
    }

    public static void RunPairMeshDefects(Action<string, bool, string> check)
    {
        var json = new System.Text.Json.JsonSerializerOptions { NumberHandling = System.Text.Json.Serialization.JsonNumberHandling.AllowNamedFloatingPointLiterals };

        // ================= D1: zero-area triangles in a member's own mesh =================
        // PM-1: A = the plate plus a collinear zero-area triangle lying in B's face. Measured; crossing exactly 50 (upper bound too: the
        // needle's edges add nothing); A's skip count 1, B's 0. Rejects the refusal (A: a_degenerate_triangle) and a crossing or
        // undecided length counted on the zero-area triangle (upper bound > 50).
        Case(check, "pair-mesh PM-1: a collinear zero-area triangle in A - measured, crossing [50, 50], skipped A 1", () =>
        {
            var r = MeshSurface.MeasurePlaced(PlateWith(Collinear), null, Zero3, Zero3, 1f, QuadB, null, Zero3, Zero3, 1f, VolumeGeometry.Render);
            return (r.Status == "measured" && r.CrossingLength is { } L && Math.Abs(L.Lower - 50) < 0.01 && L.Upper < 50.01
                    && r.ZeroAreaSkipped is { A: 1, B: 0 }, $"{r.Status} {r.Reason} L {r.CrossingLength} skipped {r.ZeroAreaSkipped}");
        });
        // PM-2: the same with a repeated-index triangle in A, and one collinear zero-area triangle in B, the pair placed at city-scale
        // coordinates (both at FarOrigin; the pair frame keeps the sliver-free fixture in the world frame). Measured, crossing 50, skips
        // A 1 and B 1. Rejects the refusal and a skip count that misses either member.
        Case(check, "pair-mesh PM-2: repeated index in A, collinear in B, at FarOrigin - measured, crossing 50, skipped 1/1", () =>
        {
            var r = MeshSurface.MeasurePlaced(PlateWith(Repeated), null, FarOrigin, Zero3, 1f, QuadBWithCollinear(), null, FarOrigin, Zero3, 1f, VolumeGeometry.Render);
            return (r.Status == "measured" && r.CrossingLength is { } L && Math.Abs(L.Lower - 50) < 0.05 && L.Upper < 50.05
                    && r.ZeroAreaSkipped is { A: 1, B: 1 }, $"{r.Status} {r.Reason} L {r.CrossingLength} skipped {r.ZeroAreaSkipped}");
        });
        // PM-3: tags travel with their triangles. A = plate (tag alpha_test on both plate triangles) with the zero-area triangle FIRST
        // (tag opaque): after the skip the crossing is keyed alpha_test x opaque, never opaque x opaque. Rejects tags left unshifted.
        Case(check, "pair-mesh PM-3: tags follow their triangles after a skip - crossing keyed alpha_test x opaque", () =>
        {
            var v = new[] { new Vector3(60, 0.0005f, 5), new Vector3(80, 0.0005f, 5), new Vector3(70, 0.0005f, 5),
                            new Vector3(0, -10, 0), new Vector3(100, -10, 0), new Vector3(100, 10, 0), new Vector3(0, 10, 0) };
            var a = new TriangleSoup(v, new[] { 0, 1, 2, 3, 4, 5, 3, 5, 6 });
            var tags = new[] { SurfaceMaterial.Opaque, SurfaceMaterial.AlphaTest, SurfaceMaterial.AlphaTest };
            var r = MeshSurface.MeasurePlaced(a, tags, Zero3, Zero3, 1f, QuadB, null, Zero3, Zero3, 1f, VolumeGeometry.Render);
            var by = r.CrossingLengthByTags;
            return (r.Status == "measured" && by is not null && by.Count == 1 && by.TryGetValue(SurfaceMaterial.Pair(SurfaceMaterial.AlphaTest, SurfaceMaterial.Opaque), out var l) && Math.Abs(l - 50) < 0.01,
                    $"{r.Status} {r.Reason} {(by is null ? "-" : string.Join(", ", by.Select(kv => $"{kv.Key}={kv.Value:0.###}")))}");
        });
        // PM-4: a triangle with model-space area that placement collapses in every frame the rule tries (A at FarOrigin, B at
        // -FarOrigin, ~250,000 u apart, and each member holds a sliver: centred on B, A's collapses; centred on A, B's does; lane D
        // 2026-10-10 moved the old fixture, which the frame centred on A undoes, to PC-5). Still refused, naming the collapse and that
        // centring on either member does not undo it. A also holds an ordinary triangle 50 u above, so skipping the collapsed sliver
        // would leave a measurable member. Rejects skipping a placement-collapsed sliver as if it were a zero-area mesh triangle (it
        // has real area), measuring in a frame that collapses the other member (B: b_degenerate_triangle), and the reason without the
        // clause (red at the lane D test commit for the clause only).
        Case(check, "pair-mesh PM-4: a placement collapse no centred frame undoes - refused, naming it", () =>
        {
            var (a, b, neg) = Pm4Pair();
            var r = MeshSurface.MeasurePlaced(a, null, FarOrigin, Zero3, 1f, b, null, neg, Zero3, 1f, VolumeGeometry.Render);
            return (r.Status == "refused" && (r.Reason ?? "").StartsWith("A: a_degenerate_triangle", StringComparison.Ordinal)
                    && (r.Reason ?? "").Contains("model-space area") && (r.Reason ?? "").Contains("centring the pair on either member does not undo it")
                    && r.ZeroAreaSkipped is null,
                    $"{r.Status} {r.Reason}");
        });
        // PM-5: a member whose every triangle has zero area is refused by name, not measured as empty. Rejects the bare refusal that
        // does not say why (A: a_degenerate_triangle alone).
        Case(check, "pair-mesh PM-5: every triangle of A has zero area - refused by name", () =>
        {
            var a = new TriangleSoup(Collinear, new[] { 0, 1, 2 });
            var r = MeshSurface.MeasurePlaced(a, null, Zero3, Zero3, 1f, QuadB, null, Zero3, Zero3, 1f, VolumeGeometry.Render);
            return (r.Status == "refused" && (r.Reason ?? "").Contains("every triangle has zero area"), $"{r.Status} {r.Reason}");
        });
        // PM-6 (guard): a pair without zero-area triangles serialises with no zeroAreaSkipped member (rows unchanged byte for byte).
        Case(check, "pair-mesh PM-6 (guard): a clean pair writes no ZeroAreaSkipped", () =>
        {
            var r = MeshSurface.MeasurePlaced(PlateWith(), null, Zero3, Zero3, 1f, QuadB, null, Zero3, Zero3, 1f, VolumeGeometry.Render);
            string s = System.Text.Json.JsonSerializer.Serialize(r, json);
            return (r.Status == "measured" && r.ZeroAreaSkipped is null && !s.Contains("ZeroAreaSkipped"), $"{r.Status} L {r.CrossingLength}");
        });

        // ================= D2: a render shape that declares no vertices and no triangles =================
        // PM-7: a NIF with a one-triangle plate and an empty BSTriShape 'Walls.001'. The render surface is measured with the plate's one
        // triangle and names the empty shape. Rejects the refusal (render_partial: shape 'Walls.001' has unreadable vertices or triangles).
        Case(check, "pair-mesh PM-7: an empty BSTriShape beside a plate - measured, 1 triangle, empty shape named", () =>
        {
            var t = MeshSurface.ModelSurface(EmptyShapeNif(withEmpty: true), VolumeGeometry.Render);
            return (t.Status == "measured" && t.Soup?.TriangleCount == 1 && t.EmptyShapes is { Count: 1 } e && e[0] == "Walls.001",
                    $"{t.Status} {t.Reason} tris {t.Soup?.TriangleCount} empty [{string.Join(", ", t.EmptyShapes ?? Array.Empty<string>())}]");
        });
        // PM-8 (guard): the same NIF without the empty shape reads with no EmptyShapes (null), as before.
        Case(check, "pair-mesh PM-8 (guard): no empty shape - EmptyShapes null", () =>
        {
            var t = MeshSurface.ModelSurface(EmptyShapeNif(withEmpty: false), VolumeGeometry.Render);
            return (t.Status == "measured" && t.EmptyShapes is null && t.Soup?.TriangleCount == 1, $"{t.Status} {t.Reason}");
        });
        // PM-9 (guard): a NiTriShape with no geometry data block is still unreadable (it declares nothing because its data is missing,
        // not because it is empty). Rejects reading a missing data block as an empty shape.
        Case(check, "pair-mesh PM-9 (guard): a NiTriShape with no data block - still refused as unreadable", () =>
        {
            var f = new NifFile(); f.Create(Ver, withRootNode: true);
            var root = f.GetRootNodes().First(); root.Flags_ui = 0xE;
            root.Children.AddBlockRef(f.AddBlock(Shape("Plate", new TriangleSoup(new[] { new Vector3(0, 0, 0), new Vector3(10, 0, 0), new Vector3(0, 10, 0) }, new[] { 0, 1, 2 }))));
            root.Children.AddBlockRef(f.AddBlock(new NiTriShape { Name = new NiStringRef("NoData"), Flags_ui = 0xE, Scale = 1f }));
            var t = MeshSurface.ModelSurface(Save(f, "no data"), VolumeGeometry.Render);
            return (t.Status == "refused" && (t.Reason ?? "").Contains("'NoData'") && t.EmptyShapes is null, $"{t.Status} {t.Reason}");
        });
    }
}
