using System.Numerics;
using HousecarlCore;
using NiflySharp;
using NiflySharp.Blocks;
using NiflySharp.Structs;

/// <summary>Known answers for the measured overlap volume (patch-miss packet, GATE-A §5/§7, GATE-B §2). Every case goes
/// through the production placement (<see cref="SpatialOverlap.TransformSoup"/>) and, for V6 and the material and
/// reader cases, the production NIF reader, before <see cref="MeshVolume"/>. Expected values are analytic, stated
/// beside each case. The transform-stage cases (VT1-VT3) check the placement and reader alone: they are the named
/// targets of the wrong-transform sabotage arms (dropped rotation, transposed rotation, missing scale, root applied,
/// below-root chain dropped) and hold on the base.</summary>
static class OverlapVolumeProof
{
    const double Tol = 1e-6;

    // A closed, outward-wound axis box (the winding the census canary validated closed).
    static TriangleSoup Box(float x0, float y0, float z0, float x1, float y1, float z1)
    {
        var v = new[] { new Vector3(x0, y0, z0), new Vector3(x1, y0, z0), new Vector3(x1, y1, z0), new Vector3(x0, y1, z0),
                        new Vector3(x0, y0, z1), new Vector3(x1, y0, z1), new Vector3(x1, y1, z1), new Vector3(x0, y1, z1) };
        var i = new[] { 0,2,1, 0,3,2, 4,5,6, 4,6,7, 0,1,5, 0,5,4, 1,2,6, 1,6,5, 2,3,7, 2,7,6, 3,0,4, 3,4,7 };
        return new TriangleSoup(v, i);
    }
    static TriangleSoup Open(TriangleSoup s) => new(s.Vertices, s.Indices[..30]);          // one face removed
    static TriangleSoup Place(TriangleSoup model, float x, float y, float z, float rz = 0, float scale = 1) =>
        SpatialOverlap.TransformSoup(model, new Vec3(x, y, z), new Vec3(0, 0, rz), scale);
    static float Rad(float deg) => deg * MathF.PI / 180f;

    static bool Is(OverlapVolumeResult r, double inter, double aInB, double bInA, double tolRel = 0.01)
    {
        if (r.Status != "measured" || r.Intersection is null || r.AInB is null || r.BInA is null) return false;
        double t = Math.Max(inter * tolRel, 1.0);
        return r.Intersection.Contains(inter, 1e-6) && r.Intersection.Width <= t + 1e-6
            && r.AInB.Contains(aInB, 1e-9) && r.BInA.Contains(bInA, 1e-9) && r.Converged;
    }
    static string Say(OverlapVolumeResult r) =>
        $"{r.Status} {r.Reason} vol A {r.VolumeA} B {r.VolumeB} inter [{r.Intersection?.Lower}, {r.Intersection?.Upper}] aInB [{r.AInB?.Lower}, {r.AInB?.Upper}] bInA [{r.BInA?.Lower}, {r.BInA?.Upper}] converged {r.Converged} cells {r.Cells}";

    // The node-chain fixture: a ROOT turned a quarter turn about Z, lifted 50 u and scaled 3 (the engine discards the
    // root's own transform — Q28), a CHILD translated (100, 0, 0) and turned by rows (0,-1,0)/(1,0,0) (+X onto -Y in the
    // engine's sense, NifChainProof case 2), and a cube shape [0,20]^3 with an identity transform.
    // Expected model-space box: x' = y in [0,20] + 100, y' = -x in [-20,0], z' in [0,20] -> [100,120] x [-20,0] x [0,20].
    static Matrix33 QuarterTurnZ() => new() { M11 = 0, M12 = -1, M13 = 0, M21 = 1, M22 = 0, M23 = 0, M31 = 0, M32 = 0, M33 = 1 };
    static byte[] ChainCube(bool alpha = false)
    {
        var ver = new NiVersion { FileVersion = NiVersion.ToFile("20.2.0.7"), UserVersion = 12, StreamVersion = 100 };
        var f = new NifFile();
        f.Create(ver, withRootNode: true);
        var root = f.GetRootNodes().First();
        root.Flags_ui = 0xE; root.Rotation = QuarterTurnZ(); root.Translation = new Vector3(0, 0, 50); root.Scale = 3f;
        var child = new NiNode { Name = new NiStringRef("VolChild"), Flags_ui = 0xE, Scale = 1f, Translation = new Vector3(100, 0, 0), Rotation = QuarterTurnZ() };
        root.Children.AddBlockRef(f.AddBlock(child));
        var cube = Box(0, 0, 0, 20, 20, 20);
        var shape = new BSTriShape { Name = new NiStringRef("VolCube"), Flags_ui = 0xE, Scale = 1f };
        var tris = new List<Triangle>();
        for (int k = 0; k < cube.Indices.Length; k += 3) tris.Add(new Triangle((ushort)cube.Indices[k], (ushort)cube.Indices[k + 1], (ushort)cube.Indices[k + 2]));
        shape.Create(ver, cube.Vertices.ToList(), tris, new(), new());
        shape.IsSkinned = false;
        child.Children.AddBlockRef(f.AddBlock(shape));
        if (alpha)
        {
            var ap = new NiAlphaProperty { Threshold = 128 };
            ap.Flags.Value = 0x12ED;                                  // blend on, test on (NifServiceGuardProbe's fixture value)
            shape.AlphaPropertyRef = new NiBlockRef<NiAlphaProperty>(f.AddBlock(ap));
        }
        using var ms = new MemoryStream();
        if (f.Save(ms) != 0) throw new InvalidOperationException("overlap-volume chain fixture failed to save");
        return ms.ToArray();
    }

    public static void Run(Action<string, bool, string> check)
    {
        // ---- transform stage (production placement and reader only; the sabotage arms' named targets) ----
        {
            // VT1: Skyrim's Z rotation turns local +X toward world -Y for a positive angle (heading clockwise from above).
            var p = Place(new TriangleSoup(new[] { new Vector3(100, 0, 0), new Vector3(0, 100, 0), Vector3.Zero }, new[] { 0, 1, 2 }), 0, 0, 0, Rad(30));
            var e0 = new Vector3(86.60254f, -50f, 0); var e1 = new Vector3(50f, 86.60254f, 0);
            check("overlap-volume VT1: placement rotation — Z +30° maps local +X to (86.60, -50, 0) and +Y to (50, 86.60, 0)",
                  (p.Vertices[0] - e0).Length() < 1e-3f && (p.Vertices[1] - e1).Length() < 1e-3f, $"+X -> {p.Vertices[0]}, +Y -> {p.Vertices[1]}");
            // VT2: uniform reference scale multiplies model coordinates before translation.
            var s = Place(new TriangleSoup(new[] { new Vector3(100, 0, 0), new Vector3(0, 0, 40), Vector3.Zero }, new[] { 0, 1, 2 }), 10, 0, 0, 0, 1.5f);
            check("overlap-volume VT2: placement scale — scale 1.5 maps (100,0,0) to (160,0,0) and (0,0,40) to (10,0,60) with translation (10,0,0)",
                  (s.Vertices[0] - new Vector3(160, 0, 0)).Length() < 1e-3f && (s.Vertices[1] - new Vector3(10, 0, 60)).Length() < 1e-3f,
                  $"{s.Vertices[0]}, {s.Vertices[1]}");
            // VT3: the render reader applies the chain below the root and leaves the root's own transform out.
            var m = NifOverlapGeometry.Measure(ChainCube(), includeTriangles: true);
            var b = m.Soup is { TriangleCount: > 0 } soup ? SpatialOverlap.SoupBounds(soup) : Aabb.Empty;
            check("overlap-volume VT3: reader node chain — root (turn, lift 50, scale 3) left out, child turn and (100,0,0) applied: cube at [100,120] x [-20,0] x [0,20]",
                  !b.IsEmpty && (new Vector3(b.Min.X, b.Min.Y, b.Min.Z) - new Vector3(100, -20, 0)).Length() < 1e-3f
                             && (new Vector3(b.Max.X, b.Max.Y, b.Max.Z) - new Vector3(120, 0, 20)).Length() < 1e-3f,
                  $"status {m.Status} {m.Reason}; bounds {b.Min} .. {b.Max}");
        }

        // ---- solids ----
        var cube100 = Box(0, 0, 0, 100, 100, 100);
        {
            var r = MeshVolume.Solid(cube100);
            check("overlap-volume S1: a closed 100 u cube is a solid of volume 1,000,000", r.Status == "solid" && r.Volume is { } v && Math.Abs(v - 1e6) < 1e-3 && r.Components == 1,
                  $"{r.Status} {r.Reason} {r.Volume} components {r.Components}");
        }
        // R1 open, R3 cavity (inward inner shell), R4 degenerate triangle
        {
            var r1 = MeshVolume.Solid(Open(cube100));
            check("overlap-volume R1: an open box (one face removed) is refused as open, with no volume", r1.Status == "refused" && r1.Volume is null && (r1.Reason ?? "").Contains("open"), $"{r1.Status} {r1.Reason}");
            var outer = Box(0, 0, 0, 100, 100, 100); var inner = Box(25, 25, 25, 75, 75, 75);
            var innerFlipped = new TriangleSoup(inner.Vertices, inner.Indices.Chunk(3).SelectMany(t => new[] { t[0], t[2], t[1] }).ToArray());
            var both = new TriangleSoup(outer.Vertices.Concat(innerFlipped.Vertices).ToArray(), outer.Indices.Concat(innerFlipped.Indices.Select(k => k + 8)).ToArray());
            var r3 = MeshVolume.Solid(both);
            check("overlap-volume R3: a nested inward shell (a cavity) is refused, never filled or subtracted silently", r3.Status == "refused" && r3.Volume is null, $"{r3.Status} {r3.Reason}");
            var deg = new TriangleSoup(cube100.Vertices.Append(new Vector3(0, 0, 0)).ToArray(), cube100.Indices.Concat(new[] { 0, 8, 1 }).ToArray());
            var r4 = MeshVolume.Solid(deg);
            check("overlap-volume R4: a degenerate triangle refuses the object", r4.Status == "refused" && r4.Volume is null, $"{r4.Status} {r4.Reason}");
        }

        // ---- V1-V4: axis boxes through the production placement (identity pose) ----
        var A = Place(cube100, 0, 0, 0);
        {
            var r = MeshVolume.Measure(A, Place(cube100, 200, 200, 200), VolumeGeometry.Render);
            check("overlap-volume V1: disjoint boxes -> intersection exactly [0,0], both fractions 0",
                  r.Status == "measured" && r.Intersection is { Lower: 0, Upper: 0 } && r.AInB is { Lower: 0, Upper: 0 } && r.BInA is { Lower: 0, Upper: 0 }, Say(r));
        }
        var big = Box(0, 0, 0, 200, 200, 200);
        {
            var r = MeshVolume.Measure(A, Place(big, 50, -50, -50), VolumeGeometry.Render);
            check("overlap-volume V2: half of A inside B -> 500,000 u³, A in B 0.5, B in A 0.0625 (asymmetric)", Is(r, 500_000, 0.5, 0.0625), Say(r));
        }
        {
            var r = MeshVolume.Measure(A, Place(big, 50, 50, -50), VolumeGeometry.Render);
            check("overlap-volume V3: a quarter of A inside B -> 250,000 u³, A in B 0.25, B in A 0.03125", Is(r, 250_000, 0.25, 0.03125), Say(r));
        }
        {
            var r = MeshVolume.Measure(A, Place(big, -50, -50, -50), VolumeGeometry.Render);
            check("overlap-volume V4: A wholly inside B -> 1,000,000 u³, A in B 1.0, B in A 0.125", Is(r, 1_000_000, 1.0, 0.125), Say(r));
            var swapped = MeshVolume.Measure(Place(big, -50, -50, -50), A, VolumeGeometry.Render);
            check("overlap-volume V4b: the same pair swapped swaps the fractions", Is(swapped, 1_000_000, 0.125, 1.0), Say(swapped));
        }

        // ---- V5: rotation that a transposed or dropped rotation cannot fake ----
        // A: 200 x 20 x 20 box along local X, rot Z +30°, centred at c = 50 * (cos30, -sin30) = (43.30, -25, 0), so it spans
        // [-50, 150] along the direction (cos30, -sin30). B: 30 x 300 x 30 along local Y, rot Z +120° (local +Y -> (sin120,
        // cos120) = (0.866, -0.5), the same line), centred at the origin, spanning [-150, 150]. A lies wholly inside B:
        // intersection 80,000, A in B 1.0, B in A 80,000 / 270,000. Transposed (both turn the other way, positions kept):
        // A's line is 43.3 u off B's (B's half width 15) -> nowhere near 1.0. Dropped rotation: A along X, B along Y -> small.
        {
            var aModel = Box(-100, -10, -10, 100, 10, 10);
            var bModel = Box(-15, -150, -15, 15, 150, 15);
            var a = Place(aModel, 50f * MathF.Cos(Rad(30)), -50f * MathF.Sin(Rad(30)), 0, Rad(30));
            var b = Place(bModel, 0, 0, 0, Rad(120));
            var r = MeshVolume.Measure(a, b, VolumeGeometry.Render);
            check("overlap-volume V5: rotated pair — A (rot 30°, offset along its axis) wholly inside B (rot 120°): 80,000 u³, A in B 1.0, B in A 0.296296",
                  Is(r, 80_000, 1.0, 80_000.0 / 270_000.0), Say(r));
        }

        // ---- V6: the reader's node chain with a non-identity root ----
        // A = the chain cube ([100,120] x [-20,0] x [0,20], 8,000 u³) at an identity reference; B = axis box [100,110] x
        // [-20,0] x [0,20] (4,000 u³) -> intersection 4,000, A in B 0.5, B in A 1.0. Root applied or chain dropped moves
        // A off B entirely; the pre-2026-09-05 transposed child turn puts A at y in [0,20] -> 0.
        {
            var m = NifOverlapGeometry.Measure(ChainCube(), includeTriangles: true);
            var a = m.Soup is { TriangleCount: > 0 } soup ? Place(soup, 0, 0, 0) : TriangleSoup.Empty;
            var r = MeshVolume.Measure(a, Box(100, -20, 0, 110, 0, 20), VolumeGeometry.Render);
            check("overlap-volume V6: node chain under a non-identity root — 4,000 u³, A in B 0.5, B in A 1.0", Is(r, 4_000, 0.5, 1.0), Say(r));
            var model = MeshVolume.ModelSolid(ChainCube(), VolumeGeometry.Render);
            check("overlap-volume V6b: ModelSolid reads the same chain cube as a solid of 8,000 u³", model.Status == "solid" && model.Volume is { } mv && Math.Abs(mv - 8000) < 1e-3,
                  $"{model.Status} {model.Reason} {model.Volume}");
        }

        // ---- V7: uniform reference scale ----
        // A = cube [-50,50]^3 at scale 1.5 -> [-75,75]^3 (3,375,000 u³). B = [25,225] x [-200,200] x [-200,200] (32,000,000).
        // Intersection 50 x 150 x 150 = 1,125,000; A in B 1/3; B in A 0.03515625. Unscaled A would give 1/4.
        {
            var a = Place(Box(-50, -50, -50, 50, 50, 50), 0, 0, 0, 0, 1.5f);
            var r = MeshVolume.Measure(a, Box(25, -200, -200, 225, 200, 200), VolumeGeometry.Render);
            check("overlap-volume V7: scaled reference (1.5) — 1,125,000 u³, A in B 1/3, B in A 0.03515625", Is(r, 1_125_000, 1.0 / 3.0, 0.03515625), Say(r));
        }

        // ---- refusal inside Measure, and the material gate ----
        {
            var r = MeshVolume.Measure(Place(Open(cube100), 0, 0, 0), Place(big, -50, -50, -50), VolumeGeometry.Render);
            check("overlap-volume R1b: a pair with an open member is refused with the member named, no interval",
                  r.Status == "refused" && r.Intersection is null && r.AInB is null && (r.Reason ?? "").Contains("A"), Say(r));
            var alpha = MeshVolume.ModelSolid(ChainCube(alpha: true), VolumeGeometry.Render);
            check("overlap-volume R2: an alpha-blended render shape refuses the object (material), no volume", alpha.Status == "refused" && alpha.Volume is null && (alpha.Reason ?? "").Contains("material"),
                  $"{alpha.Status} {alpha.Reason}");
        }

        // ---- P1: interval soundness on a rotated pair with a known answer, and a canary that must fail ----
        {
            // A cube side 100 centred at the origin, rot Z 45°; B = the half-space-like box x >= 0 (large). By symmetry
            // exactly half of A lies at x >= 0: 500,000 u³.
            var a = Place(Box(-50, -50, -50, 50, 50, 50), 0, 0, 0, Rad(45));
            var r = MeshVolume.Measure(a, Box(0, -500, -500, 500, 500, 500), VolumeGeometry.Render);
            bool sound = r.Status == "measured" && r.Intersection is { } iv && iv.Contains(500_000, 1e-6);
            check("overlap-volume P1: a 45°-turned cube cut by a plane through its centre — the interval contains exactly 500,000", sound, Say(r));
            bool canary = r.Status == "measured" && r.Intersection is { } iv2 && iv2.Contains(500_000 * 1.05, 1e-6);
            check("overlap-volume P1 canary: the same interval does NOT contain a 5 % wrong answer (a sweep that passes this is broken)", r.Status == "measured" && !canary, Say(r));
        }

        // ---- V8: a solid against itself (found by the CLI known-answer case on a real barrel, 2026-09-28) ----
        // A 45°-turned cube with itself: every boundary cell carries the SAME plane for both solids. The interval must
        // contain vol(A) and both fractions must contain 1; clipping by the second, identical plane may not add a face.
        {
            var a = Place(Box(-50, -50, -50, 50, 50, 50), 0, 0, 0, Rad(45));
            var r = MeshVolume.Measure(a, a, VolumeGeometry.Render);
            check("overlap-volume V8: a turned cube measured against itself — the interval contains vol(A); A in B and B in A contain 1",
                  r.Status == "measured" && r.VolumeA is { } va && r.Intersection!.Contains(va, 1e-6) && r.AInB!.Contains(1.0, 1e-12) && r.BInA!.Contains(1.0, 1e-12),
                  Say(r));
        }

        // ---- A1: four-setup attribution ----
        {
            var c = new VolumeInterval(80_000, 80_000); var z = new VolumeInterval(0, 0);
            check("overlap-volume A1: overlapping only in the combined arm needs both mods",
                  MeshVolume.Attribution(c, z, z, z) == "needs_both", MeshVolume.Attribution(c, z, z, z));
            check("overlap-volume A1b: overlapping already with one mod alone does not need both",
                  MeshVolume.Attribution(c, z, new VolumeInterval(80_000, 80_000), null) == "present_without_both", MeshVolume.Attribution(c, z, c, null));
            check("overlap-volume A1c: combined upper bound zero is no overlap",
                  MeshVolume.Attribution(z, z, z, z) == "no_overlap", MeshVolume.Attribution(z, z, z, z));
            check("overlap-volume A1d: a single-mod arm whose interval straddles the combined lower bound is unresolved, not counted",
                  MeshVolume.Attribution(new VolumeInterval(1000, 2000), z, new VolumeInterval(0, 1500), z) == "unresolved",
                  MeshVolume.Attribution(new VolumeInterval(1000, 2000), z, new VolumeInterval(0, 1500), z));
        }
    }
}
