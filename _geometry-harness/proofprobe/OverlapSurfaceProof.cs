using System.Numerics;
using HousecarlCore;
using NiflySharp;
using NiflySharp.Blocks;
using NiflySharp.Enums;
using NiflySharp.Structs;

/// <summary>Known answers for the measured surface contact (patch-miss packet, session 6, plan `g1/PLAN-6.md` §4 with the
/// plan review's H1, M1 and L1 folded in). Expected values are analytic (stated beside each case) or, for SC3, from an
/// independent oracle written before the code (the chord of a rotated rectangle, printed in the red-gate record). Each
/// case names the wrong answer it rejects. Cases marked "fixture control" check the fixture itself against code that
/// already exists and hold on the base; every other case is red until <see cref="MeshSurface"/> is implemented.</summary>
static partial class OverlapSurfaceProof
{
    const double Rel = 1e-6;
    static bool Near(double? v, double want, double rel = Rel) => v is { } x && Math.Abs(x - want) <= Math.Max(Math.Abs(want) * rel, 1e-6);
    static bool Has(SurfaceInterval? i, double want, double rel = Rel) => i is { } x && Near(x.Lower, want, rel) && Near(x.Upper, want, rel);
    static double Tag(IReadOnlyDictionary<string, double>? d, string a, string b) => d is not null && d.TryGetValue(SurfaceMaterial.Pair(a, b), out var v) ? v : 0;
    static string Say(SurfaceContactResult r) =>
        $"{r.Status} {r.Reason} L [{r.CrossingLength?.Lower}, {r.CrossingLength?.Upper}] undecided {r.UndecidedEdgeLength} same {r.CoplanarSameFacingArea} opp {r.CoplanarOppositeFacingArea} " +
        $"segments {r.InteriorSegments}+{r.EdgeSegments} tags [{string.Join("; ", r.CrossingLengthByTags?.Select(kv => kv.Key + "=" + kv.Value) ?? [])}] " +
        $"AinB {r.AInB?.Status} [{r.AInB?.Inside?.Lower}, {r.AInB?.Inside?.Upper}] f [{r.AInB?.Fraction?.Lower}, {r.AInB?.Fraction?.Upper}] full {r.AInB?.FullyBuried} {r.AInB?.Reason} " +
        $"BinA {r.BInA?.Status} {r.BInA?.Reason}";

    // A runner that turns an exception (the base's NotImplementedException) into one FAIL line for the named case.
    static void Case(Action<string, bool, string> check, string name, Func<(bool Ok, string Detail)> body)
    {
        (bool ok, string detail) r;
        try { r = body(); }
        catch (Exception ex) { r = (false, ex.GetType().Name + ": " + ex.Message); }
        check(name, r.ok, r.detail);
    }

    // ---- soups ----
    // Closed outward-wound axis box (the winding OverlapVolumeProof's Box uses).
    static TriangleSoup Box(float x0, float y0, float z0, float x1, float y1, float z1)
    {
        var v = new[] { new Vector3(x0, y0, z0), new Vector3(x1, y0, z0), new Vector3(x1, y1, z0), new Vector3(x0, y1, z0),
                        new Vector3(x0, y0, z1), new Vector3(x1, y0, z1), new Vector3(x1, y1, z1), new Vector3(x0, y1, z1) };
        var i = new[] { 0,2,1, 0,3,2, 4,5,6, 4,6,7, 0,1,5, 0,5,4, 1,2,6, 1,6,5, 2,3,7, 2,7,6, 3,0,4, 3,4,7 };
        return new TriangleSoup(v, i);
    }
    static TriangleSoup Cube(float cx, float cy, float cz, float half) => Box(cx - half, cy - half, cz - half, cx + half, cy + half, cz + half);
    // Square in the plane z = z0, two triangles split along the (x0,y0)-(x1,y1) diagonal, normal +Z.
    static TriangleSoup PlateZ(float x0, float y0, float x1, float y1, float z0) =>
        new(new[] { new Vector3(x0, y0, z0), new Vector3(x1, y0, z0), new Vector3(x1, y1, z0), new Vector3(x0, y1, z0) }, new[] { 0, 1, 2, 0, 2, 3 });
    // Square in the plane x = x0 (y, z extents), normal +X.
    static TriangleSoup PlateX(float x0, float y0, float z0, float y1, float z1) =>
        new(new[] { new Vector3(x0, y0, z0), new Vector3(x0, y1, z0), new Vector3(x0, y1, z1), new Vector3(x0, y0, z1) }, new[] { 0, 1, 2, 0, 2, 3 });
    static TriangleSoup Soup(params Vector3[][] tris)
    {
        var v = new List<Vector3>(); var i = new List<int>();
        foreach (var t in tris) { foreach (var p in t) { i.Add(v.Count); v.Add(p); } }
        return new TriangleSoup(v.ToArray(), i.ToArray());
    }
    static Vector3 V(float x, float y, float z) => new(x, y, z);
    static float Rad(float deg) => deg * MathF.PI / 180f;
    static readonly Vec3 Zero = new(0, 0, 0);

    // ---- NIF fixtures ----
    static readonly NiVersion Ver = new() { FileVersion = NiVersion.ToFile("20.2.0.7"), UserVersion = 12, StreamVersion = 100 };
    static BSTriShape Shape(NifFile f, string name, TriangleSoup s)
    {
        var shape = new BSTriShape { Name = new NiStringRef(name), Flags_ui = 0xE, Scale = 1f };
        var tris = new List<Triangle>();
        for (int k = 0; k < s.Indices.Length; k += 3) tris.Add(new Triangle((ushort)s.Indices[k], (ushort)s.Indices[k + 1], (ushort)s.Indices[k + 2]));
        shape.Create(Ver, s.Vertices.ToList(), tris, new(), new());
        shape.IsSkinned = false;
        return shape;
    }
    static byte[] Save(NifFile f, string what)
    {
        using var ms = new MemoryStream();
        if (f.Save(ms) != 0) throw new InvalidOperationException("surface fixture failed to save: " + what);
        return ms.ToArray();
    }
    static Matrix33 QuarterTurnZ() => new() { M11 = 0, M12 = -1, M13 = 0, M21 = 1, M22 = 0, M23 = 0, M31 = 0, M32 = 0, M33 = 1 };

    // The node-chain fixture (OverlapVolumeProof's): root turned, lifted 50 u and scaled 3 (left out by the reader), child
    // translated (100,0,0) and turned; cube [0,20]^3 -> model space [100,120] x [-20,0] x [0,20].
    static byte[] ChainCube()
    {
        var f = new NifFile(); f.Create(Ver, withRootNode: true);
        var root = f.GetRootNodes().First();
        root.Flags_ui = 0xE; root.Rotation = QuarterTurnZ(); root.Translation = new Vector3(0, 0, 50); root.Scale = 3f;
        var child = new NiNode { Name = new NiStringRef("SurfChild"), Flags_ui = 0xE, Scale = 1f, Translation = new Vector3(100, 0, 0), Rotation = QuarterTurnZ() };
        root.Children.AddBlockRef(f.AddBlock(child));
        child.Children.AddBlockRef(f.AddBlock(Shape(f, "SurfCube", Box(0, 0, 0, 20, 20, 20))));
        return Save(f, "chain cube");
    }

    // Two quads under the root: an opaque one at z = 7 and an alpha-TESTED one (blend off) at z = 17, both [-100,100]^2.
    static byte[] AlphaPair()
    {
        var f = new NifFile(); f.Create(Ver, withRootNode: true);
        var root = f.GetRootNodes().First(); root.Flags_ui = 0xE;
        root.Children.AddBlockRef(f.AddBlock(Shape(f, "Opaque", PlateZ(-100, -100, 100, 100, 7))));
        var alpha = Shape(f, "AlphaTested", PlateZ(-100, -100, 100, 100, 17));
        var ap = new NiAlphaProperty { Threshold = 128 };
        ap.Flags.Value = 0x12EC;                                   // test on, blend off
        alpha.AlphaPropertyRef = new NiBlockRef<NiAlphaProperty>(f.AddBlock(ap));
        root.Children.AddBlockRef(f.AddBlock(alpha));
        return Save(f, "alpha pair");
    }

    // A root with two child nodes: "Leaf" holding cube P [0,20]^3 and "Frame" holding cube Q [40,60] x [0,20] x [0,20],
    // plus an optional third, shapeless node "Hinge". animate = the node a CLAMP sequence's transform block targets
    // (null: no manager at all); behaviour = a BSBehaviorGraphExtraData file on the root; skinned / switched variants.
    static byte[] Rig(string? animate, string? behaviour = null, bool skinned = false, bool switched = false, CycleType cycle = CycleType.CYCLE_CLAMP)
    {
        var f = new NifFile(); f.Create(Ver, withRootNode: true);
        var root = f.GetRootNodes().First(); root.Flags_ui = 0xE;
        NiNode leaf = switched ? new NiSwitchNode { Name = new NiStringRef("Leaf"), Flags_ui = 0xE, Scale = 1f }
                               : new NiNode { Name = new NiStringRef("Leaf"), Flags_ui = 0xE, Scale = 1f };
        var frame = new NiNode { Name = new NiStringRef("Frame"), Flags_ui = 0xE, Scale = 1f };
        var hinge = new NiNode { Name = new NiStringRef("Hinge"), Flags_ui = 0xE, Scale = 1f };
        root.Children.AddBlockRef(f.AddBlock(leaf));
        root.Children.AddBlockRef(f.AddBlock(frame));
        root.Children.AddBlockRef(f.AddBlock(hinge));
        var p = Shape(f, "P", Box(0, 0, 0, 20, 20, 20));
        if (skinned) { p.IsSkinned = true; p.SkinInstanceRef = new(f.AddBlock(new NiSkinInstance())); }
        leaf.Children.AddBlockRef(f.AddBlock(p));
        frame.Children.AddBlockRef(f.AddBlock(Shape(f, "Q", Box(40, 0, 0, 60, 20, 20))));
        if (animate is not null)
        {
            var mtc = new NiMultiTargetTransformController();
            int mtcIndex = f.AddBlock(mtc);
            var interp = new NiTransformInterpolator();
            int interpIndex = f.AddBlock(interp);
            var seq = new NiControllerSequence { Name = new NiStringRef("Open"), CycleType = cycle, StartTime = 0, StopTime = 1, Frequency = 1, Weight = 1 };
            seq.ControlledBlocks = new List<ControlledBlock>
            {
                new() { NodeName = new NiStringRef(animate), ControllerType = new NiStringRef("NiTransformController"),
                        Interpolator = new(interpIndex), Controller = new(mtcIndex) }
            };
            seq.NumControlledBlocks = 1;
            var mgr = new NiControllerManager();
            int mgrIndex = f.AddBlock(mgr);
            mgr.ControllerSequences ??= new();
            mgr.ControllerSequences.AddBlockRef(f.AddBlock(seq));
            mgr.NumControllerSequences = 1;
            mgr.NextController = new(mtcIndex);
            root.Controller = new(mgrIndex);
        }
        if (behaviour is not null)
        {
            root.ExtraDataList ??= new();
            root.ExtraDataList.AddBlockRef(f.AddBlock(new BSBehaviorGraphExtraData { BehaviourGraphFile = new NiStringRef(behaviour) }));
            root.NumExtraDataList = 1;
        }
        return Save(f, "rig");
    }

    public static void Run(Action<string, bool, string> check)
    {
        var none = Array.Empty<string>();

        // ================= fixture controls (existing code; hold on the base) =================
        Case(check, "surface fixture control: the CLAMP rig reads with the blanket pose issue on both shapes (existing rule)", () =>
        {
            var rig = NifGeometry.MeasureVisibleRender(Rig("Leaf"), includeTriangles: true);
            return (rig.Model is { } m && m.Shapes.Count == 2 && m.Shapes.All(s => s.PoseIssue is not null),
                    $"{rig.Error} {string.Join(" | ", rig.Model?.Shapes.Select(s => s.Name + ":" + s.PoseIssue) ?? [])}");
        });
        Case(check, "surface fixture control: the rig's sequence round-trips its controlled block (node 'Leaf', NiTransformController, CLAMP)", () =>
        {
            var nif = new NifFile();
            using (var ms = new MemoryStream(Rig("Leaf"))) nif.Load(ms);
            var seq = nif.Blocks.OfType<NiControllerSequence>().FirstOrDefault();
            if (seq is null) return (false, "no sequence");
            var cb = seq.ControlledBlocks.FirstOrDefault();
            return (seq.ControlledBlocks.Count == 1 && cb.NodeName?.ToString() == "Leaf" && cb.ControllerType?.ToString() == "NiTransformController"
                    && seq.CycleType == CycleType.CYCLE_CLAMP, $"{seq.ControlledBlocks.Count} {cb.NodeName} {cb.ControllerType} {seq.CycleType}");
        });
        Case(check, "surface fixture control: the behaviour-graph rig carries BSBehaviorGraphExtraData and NO controller", () =>
        {
            var beh = new NifFile();
            using (var ms = new MemoryStream(Rig(null, behaviour: @"Fixture\Rig.hkx"))) beh.Load(ms);
            var bg = beh.Blocks.OfType<BSBehaviorGraphExtraData>().FirstOrDefault();
            return (bg?.BehaviourGraphFile?.ToString() == @"Fixture\Rig.hkx" && !beh.Blocks.Any(b => b is NiTimeController),
                    $"graph {bg?.BehaviourGraphFile}; controllers {beh.Blocks.Count(b => b is NiTimeController)}");
        });
        // SCB (review Q-R2): the DEFAULT rule refuses a file that references a behaviour graph: every visible shape carries
        // the pose issue naming the graph, and the overlap reader does not call it measured. Rejects the stored pose read
        // as the live pose when an animation source is visible in the file (before the fix: no pose issue, measured).
        Case(check, "surface SCB: the default rule refuses a behaviour-graph file — every shape names 'behaviour graph 'Fixture\\Rig.hkx''", () =>
        {
            var bytes = Rig(null, behaviour: @"Fixture\Rig.hkx");
            var geo = NifGeometry.MeasureVisibleRender(bytes, includeTriangles: true);
            var ov = NifOverlapGeometry.Measure(bytes, includeTriangles: true);
            return (geo.Model is { } bm && bm.Shapes.Count == 2 && bm.Shapes.All(s => (s.PoseIssue ?? "").Contains(@"behaviour graph 'Fixture\Rig.hkx'"))
                    && ov.Status != NifRenderGeometryStatus.Measured && !ov.UsableForSurfaceIntersection,
                    $"{string.Join(" | ", geo.Model?.Shapes.Select(s => s.Name + ":" + s.PoseIssue) ?? [])}; overlap {ov.Status}: {ov.Reason}");
        });
        Case(check, "surface fixture control: the skinned rig reads P as skinned", () =>
        {
            var sk = NifGeometry.MeasureVisibleRender(Rig(null, skinned: true), includeTriangles: true);
            return (sk.Model is { } km && km.Shapes.Any(s => s.Name == "P" && s.PoseIssue is not null && s.PoseIssue.Contains("skinned")),
                    $"{sk.Error} {string.Join(" | ", sk.Model?.Shapes.Select(s => s.Name + ":" + s.PoseIssue) ?? [])}");
        });
        Case(check, "surface fixture control: the switch rig reads P under a switch node", () =>
        {
            var sw = NifGeometry.MeasureVisibleRender(Rig(null, switched: true), includeTriangles: true);
            return (sw.Model is { } wm && wm.Shapes.Any(s => s.Name == "P" && s.PoseIssue is not null && s.PoseIssue.Contains("switch")),
                    $"{sw.Error} {string.Join(" | ", sw.Model?.Shapes.Select(s => s.Name + ":" + s.PoseIssue) ?? [])}");
        });
        Case(check, "surface fixture control: the existing predicate calls two surfaces crossing only along a shared edge line 'touching' (what SC-L1 adds)", () =>
        {
            var a = Soup(new[] { V(-100, 0, 0), V(100, 0, 0), V(0, 100, 0) }, new[] { V(100, 0, 0), V(-100, 0, 0), V(0, -100, 0) });
            var b = Soup(new[] { V(-100, 0, 0), V(100, 0, 0), V(0, 0, 100) }, new[] { V(100, 0, 0), V(-100, 0, 0), V(0, 0, -100) });
            var q = MeshIntersection.Query(a, b);
            return (q.SurfaceRelation == "touching", $"{q.Status} {q.SurfaceRelation} {q.Reason}");
        });
        Case(check, "surface fixture control: the alpha pair reads two visible shapes, the second alpha-tested (volume material gate names it)", () =>
        {
            var v = MeshVolume.ModelSolid(AlphaPair(), VolumeGeometry.Render);
            return (v.Status == "refused" && (v.Reason ?? "").Contains("alpha test on shape 'AlphaTested'"), $"{v.Status} {v.Reason}");
        });

        // ================= SC1: interior crossing =================
        // Plate [-100,100]^2 at z = 7 through a closed cube of side 100 centred (7,3,0): the crossing curve is the cube's
        // section, a 100 x 100 square: L = 400. Rejects: no crossing counted; half a perimeter.
        Case(check, "surface SC1 (canary): plate through a cube — L = 400 exactly, no coplanar area", () =>
        {
            var r = MeshSurface.Measure(PlateZ(-100, -100, 100, 100, 7), null, Cube(7, 3, 0, 50), null, VolumeGeometry.Render);
            return (r.Status == "measured" && Has(r.CrossingLength, 400) && Near(r.CoplanarSameFacingArea, 0) && Near(r.CoplanarOppositeFacingArea, 0)
                    && Near(Tag(r.CrossingLengthByTags, "opaque", "opaque"), 400), Say(r));
        });

        // ================= SC2: a crossing that lies along an edge of A, on a face of B =================
        // Diamond plate at z = 7: T1 = (50,-100),(50,100),(-50,0) inside x <= 50, T2 = (50,-100),(150,0),(50,100) outside.
        // Cube [-50,50]^3. Curve: T1 crosses faces y = +-50 (x in [0,50]: 50 + 50) and the shared edge x = 50 crosses the
        // face x = 50 (y in [-50,50]: 100; T1 and T2 on opposite sides). L = 200, edge part 100.
        // Rejects: the edge-lying crossing lost (L = 100, today's per-triangle predicate calls it touching) and the edge
        // counted per incident triangle (L = 300).
        Case(check, "surface SC2: crossing along A's shared edge on B's face — L = 200 (interior 100 + edge 100), counted once", () =>
        {
            var diamond = Soup(new[] { V(50, -100, 7), V(50, 100, 7), V(-50, 0, 7) }, new[] { V(50, -100, 7), V(150, 0, 7), V(50, 100, 7) });
            var r = MeshSurface.Measure(diamond, null, Cube(0, 0, 0, 50), null, VolumeGeometry.Render);
            return (r.Status == "measured" && Has(r.CrossingLength, 200) && r.EdgeSegments > 0, Say(r));
        });
        // SC2b: the same edge on the same face, both incident triangles OUTSIDE (a crease resting against the wall): no
        // crossing. Rejects: an edge contact counted as a crossing (L = 100).
        Case(check, "surface SC2b: crease touching B's face from one side — L = 0", () =>
        {
            var crease = Soup(new[] { V(50, -100, 7), V(150, 0, 7), V(50, 100, 7) }, new[] { V(50, 100, 7), V(150, 0, 47), V(50, -100, 7) });
            var r = MeshSurface.Measure(crease, null, Cube(0, 0, 0, 50), null, VolumeGeometry.Render);
            return (r.Status == "measured" && Has(r.CrossingLength, 0), Say(r));
        });

        // ================= SC-L1: a crossing along an edge of BOTH meshes (review L1) =================
        // A: two triangles in z = 0 sharing the x-axis edge [-100,100]; B: two triangles in y = 0 sharing the same edge.
        // Half-planes around the line interleave (A +y, B +z, A -y, B -z): the surfaces cross along it, L = 200, counted
        // once for the edge pair. Rejects: 0 (today's predicate: touching), 400 or 800 (one count per incident triangle pair).
        Case(check, "surface SC-L1: crossing along an edge shared by both meshes (interleaved) — L = 200, once", () =>
        {
            var a = Soup(new[] { V(-100, 0, 0), V(100, 0, 0), V(0, 100, 0) }, new[] { V(100, 0, 0), V(-100, 0, 0), V(0, -100, 0) });
            var b = Soup(new[] { V(-100, 0, 0), V(100, 0, 0), V(0, 0, 100) }, new[] { V(100, 0, 0), V(-100, 0, 0), V(0, 0, -100) });
            var r = MeshSurface.Measure(a, null, b, null, VolumeGeometry.Render);
            return (r.Status == "measured" && Has(r.CrossingLength, 200), Say(r));
        });
        // SC-L1b: B's two triangles both on one side of A's line arrangement (half-planes at about 20.6 and 63.4 degrees,
        // A at 90 and 270): not interleaved, the surfaces only touch along the line. L = 0. Rejects: 200.
        Case(check, "surface SC-L1b: edge shared by both meshes, not interleaved — L = 0", () =>
        {
            var a = Soup(new[] { V(-100, 0, 0), V(100, 0, 0), V(0, 100, 0) }, new[] { V(100, 0, 0), V(-100, 0, 0), V(0, -100, 0) });
            var b = Soup(new[] { V(-100, 0, 0), V(100, 0, 0), V(0, 50, 100) }, new[] { V(100, 0, 0), V(-100, 0, 0), V(0, 80, 30) });
            var r = MeshSurface.Measure(a, null, b, null, VolumeGeometry.Render);
            return (r.Status == "measured" && Has(r.CrossingLength, 0), Say(r));
        });
        // SC-L1c: a single triangle whose BOUNDARY edge lies on B's face (one incident face): the side test cannot decide.
        // L = [0, 100] with 100 undecided. Rejects: the undecided length dropped (upper 0) or counted as decided (lower 100).
        Case(check, "surface SC-L1c: boundary edge on B's face — undecided, L = [0, 100]", () =>
        {
            var tri = Soup(new[] { V(50, -100, 7), V(50, 100, 7), V(150, 0, 7) });
            var r = MeshSurface.Measure(tri, null, Cube(0, 0, 0, 50), null, VolumeGeometry.Render);
            return (r.Status == "measured" && r.CrossingLength is { } l && Near(l.Lower, 0) && Near(l.Upper, 100) && Near(r.UndecidedEdgeLength, 100), Say(r));
        });

        // ================= SC3-SC6: the placement and reader stages =================
        // SC3: box [0,100] x [0,40] x [-50,50] placed with Z rotation +30 degrees (VT1's convention), cut by a plate in
        // x = 95. Oracle (red-gate record): chord 26.7949192431123, L = 2*100 + 2*chord = 253.5898384862246.
        // Rejects: dropped rotation (chord 40: L = 280); transposed rotation (-30 degrees: the plane misses, L = 0).
        Case(check, "surface SC3: Z-rotated asymmetric box — L = 253.5898 (dropped rotation 280, transposed 0)", () =>
        {
            var r = MeshSurface.MeasurePlaced(PlateX(95, -200, -200, 200, 200), null, Zero, Zero, 1f,
                                              Box(0, 0, -50, 100, 40, 50), null, Zero, new Vec3(0, 0, Rad(30)), 1f, VolumeGeometry.Render);
            return (r.Status == "measured" && Has(r.CrossingLength, 253.5898384862246, 1e-5), Say(r));
        });
        // SC4: SC1 with the cube placed at scale 1.5 about its reference origin: side 150, L = 600. Rejects missing scale (400).
        Case(check, "surface SC4: reference scale 1.5 — L = 600 (missing scale 400)", () =>
        {
            var r = MeshSurface.MeasurePlaced(PlateZ(-100, -100, 100, 100, 0), null, new Vec3(0, 0, 7), Zero, 1f,
                                              Cube(0, 0, 0, 50), null, new Vec3(7, 3, 0), Zero, 1.5f, VolumeGeometry.Render);
            return (r.Status == "measured" && Has(r.CrossingLength, 600), Say(r));
        });
        // SC5: each member placed by its own transform. Plate at (0,0,200) scale 1; cube at (7,3,180) scale 1.5 ->
        // z in [105,255], section 150 x 150: L = 600. Rejects the wrong member's transform (B on A's pose: L = 400).
        Case(check, "surface SC5: each member by its own transform — L = 600 (B on A's pose 400)", () =>
        {
            var r = MeshSurface.MeasurePlaced(PlateZ(-100, -100, 100, 100, 0), null, new Vec3(0, 0, 200), Zero, 1f,
                                              Cube(0, 0, 0, 50), null, new Vec3(7, 3, 180), Zero, 1.5f, VolumeGeometry.Render);
            return (r.Status == "measured" && Has(r.CrossingLength, 600), Say(r));
        });
        // SC6: the reader's node chain: the chain cube is at [100,120] x [-20,0] x [0,20]; a plate at z = 10 over
        // [85,130] x [-30,10] crosses it along a 20 x 20 square: L = 80. Rejects a dropped chain or an applied root (L = 0).
        Case(check, "surface SC6: NIF node chain under a non-identity root — L = 80", () =>
        {
            var model = MeshSurface.ModelSurface(ChainCube(), VolumeGeometry.Render);
            if (model.Soup is null) return (false, $"{model.Status} {model.Reason}");
            var r = MeshSurface.Measure(PlateZ(85, -30, 130, 10, 10), null, model.Soup, model.Tags, VolumeGeometry.Render);
            return (r.Status == "measured" && Has(r.CrossingLength, 80), Say(r));
        });

        // ================= SC7-SC8: coplanar areas =================
        // SC7: [0,100]^2 and [50,150] x [0,100], both at z = 0, normals +Z: same-facing 5,000, opposite 0, L = 0.
        // Rejects: coplanar overlap counted as crossing; zero area.
        Case(check, "surface SC7: coplanar same-facing squares — same-facing 5,000, L = 0", () =>
        {
            var r = MeshSurface.Measure(PlateZ(0, 0, 100, 100, 0), null, PlateZ(50, 0, 150, 100, 0), null, VolumeGeometry.Render);
            return (r.Status == "measured" && Near(r.CoplanarSameFacingArea, 5000) && Near(r.CoplanarOppositeFacingArea, 0) && Has(r.CrossingLength, 0), Say(r));
        });
        // SC8: a cube [-50,50]^2 x [0,100] resting on a +Z plate at z = 0: opposite-facing 10,000, same-facing 0, L = 0
        // (the side faces' bottom edges lie on the plate next to a coplanar face: no crossing). Rejects resting contact
        // read as z-fighting, and the side faces' edges read as crossings.
        Case(check, "surface SC8: cube resting on a plate — opposite-facing 10,000, same-facing 0, L = 0", () =>
        {
            var r = MeshSurface.Measure(PlateZ(-100, -100, 100, 100, 0), null, Box(-50, -50, 0, 50, 50, 100), null, VolumeGeometry.Render);
            return (r.Status == "measured" && Near(r.CoplanarOppositeFacingArea, 10000) && Near(r.CoplanarSameFacingArea, 0) && Has(r.CrossingLength, 0), Say(r));
        });

        // ================= SC9: buried area (review L3) =================
        // Plate [-100,100]^2 at z = 7 (area 40,000) wholly inside a closed cube of side 300: H(A in B) contains 40,000,
        // fraction contains 1, fully buried; H(B in A) refused (A open). Rejects inverted parity (0) and an open container.
        Case(check, "surface SC9: open plate wholly inside a closed cube — buried 40,000, fraction 1, fully buried; B in A refused", () =>
        {
            var r = MeshSurface.Measure(PlateZ(-100, -100, 100, 100, 7), null, Cube(0, 0, 0, 150), null, VolumeGeometry.Render);
            var h = r.AInB;
            return (r.Status == "measured" && h is { Status: "measured", Inside: { } i, Fraction: { } fr } && i.Contains(40000, 1e-6) && fr.Contains(1, 1e-9)
                    && h.FullyBuried && h.Converged && Near(h.Area, 40000) && r.BInA is { Status: "refused" } && Has(r.CrossingLength, 0), Say(r));
        });
        // SC9b: the same plate half inside (cube x in [0,300]): buried 20,000 (fraction 0.5), not fully buried, converged
        // to the 1 % target; L = 200 along the face x = 0. Rejects "fully buried" on a partial burial and a midpoint.
        Case(check, "surface SC9b: plate half inside — buried interval holds 20,000 (fraction 0.5), not fully buried, L = 200", () =>
        {
            var r = MeshSurface.Measure(PlateZ(-100, -100, 100, 100, 7), null, Box(0, -150, -150, 300, 150, 150), null, VolumeGeometry.Render);
            var h = r.AInB;
            return (r.Status == "measured" && h is { Status: "measured", Inside: { } i, Fraction: { } fr } && i.Contains(20000, 1e-6) && i.Width <= 400 + 1e-6
                    && fr.Contains(0.5, 1e-9) && !h.FullyBuried && h.Converged && Has(r.CrossingLength, 200), Say(r));
        });

        // SC20 (commit review H1): a large plate (2,000 x 2,000, area 4,000,000) crossing a small closed cube (side 20,
        // surface 2,400) centred (1,2,0): the plate's area inside is the 20 x 20 section, 400. The interval must hold 400
        // and may read converged only within 1 % of the SMALLER of the two areas (24 u²). Rejects convergence judged
        // against the measured surface's own area (40,000 u², wider than the whole cube), which reads converged with a
        // lower bound of 0.
        Case(check, "surface SC20: large plate in a small cube — buried 400, converged only within 1 % of the smaller area (24)", () =>
        {
            var r = MeshSurface.Measure(PlateZ(-1000, -1000, 1000, 1000, 3), null, Cube(1, 2, 0, 10), null, VolumeGeometry.Render);
            var h = r.AInB;
            bool ok = r.Status == "measured" && h is { Status: "measured", Inside: { } i } && i.Contains(400, 1e-6)
                      && (h.Converged ? i.Width <= 24 + 1e-6 : (h.Reason ?? "").Contains("not converged")) && i.Lower > 0;
            return (ok, Say(r));
        });

        // ================= SC10-SC11: alpha and open meshes =================
        // SC10: the alpha-pair NIF (opaque quad at z = 7, alpha-tested quad at z = 17) against a cube of side 100 centred
        // (7,3,0): both quads cross it along a 100 x 100 square. L split opaque x opaque 400 and alpha_test x opaque 400,
        // total 800; the overlap VOLUME of the same NIF still refuses on its material. Rejects: alpha shapes dropped
        // (total 400) or the pair refused.
        Case(check, "surface SC10: alpha-tested shape tagged, not dropped — opaque 400 + alpha_test 400; volume still refuses", () =>
        {
            var bytes = AlphaPair();
            var model = MeshSurface.ModelSurface(bytes, VolumeGeometry.Render);
            if (model.Soup is null) return (false, $"{model.Status} {model.Reason}");
            var r = MeshSurface.Measure(model.Soup, model.Tags, Cube(7, 3, 0, 50), null, VolumeGeometry.Render);
            var vol = MeshVolume.ModelSolid(bytes, VolumeGeometry.Render);
            return (r.Status == "measured" && Has(r.CrossingLength, 800) && Near(Tag(r.CrossingLengthByTags, "opaque", "opaque"), 400)
                    && Near(Tag(r.CrossingLengthByTags, "alpha_test", "opaque"), 400) && vol.Status == "refused",
                    Say(r) + $" | volume {vol.Status} {vol.Reason}");
        });
        // SC11: an open quad crossing a closed cube: L measured, H(quad in cube) measured (100 x 100 = 10,000 of 40,000:
        // fraction 0.25), converged to the 1 % target, H(cube in quad) refused naming the open member. Rejects refusing the
        // pair because one side is open, and an inverted parity (the interval then holds 10,000 only by staying wide; the
        // commit-gate sweep found the width unchecked).
        Case(check, "surface SC11: open quad vs closed cube — L 400, buried 10,000 (0.25), cube-in-quad refused as open", () =>
        {
            var r = MeshSurface.Measure(PlateZ(-100, -100, 100, 100, 7), null, Cube(7, 3, 0, 50), null, VolumeGeometry.Render);
            return (r.Status == "measured" && Has(r.CrossingLength, 400) && r.AInB is { Status: "measured", Inside: { } i, Fraction: { } fr, Converged: true }
                    && i.Contains(10000, 1e-6) && i.Width <= 400 + 1e-6 && fr.Contains(0.25, 1e-9)
                    && r.BInA is { Status: "refused", Reason: { } why } && why.Contains("open"), Say(r));
        });

        // ================= SC12: agreement with the existing predicate =================
        // Interior crossings: L.Lower > 0 exactly when Query says crossing, on SC1, SC3 (placed), SC7, SC8 and the
        // half-buried plate; an edge-only crossing (SC-L1) is the documented case Query calls touching and L counts.
        Case(check, "surface SC12: interior L > 0 iff Query = crossing (5 fixtures); SC-L1 is Query touching with L = 200", () =>
        {
            var pairs = new (TriangleSoup A, TriangleSoup B)[]
            {
                (PlateZ(-100, -100, 100, 100, 7), Cube(7, 3, 0, 50)),
                (PlateX(95, -200, -200, 200, 200), SpatialOverlap.TransformSoup(Box(0, 0, -50, 100, 40, 50), Zero, new Vec3(0, 0, Rad(30)), 1f)),
                (PlateZ(0, 0, 100, 100, 0), PlateZ(50, 0, 150, 100, 0)),
                (PlateZ(-100, -100, 100, 100, 0), Box(-50, -50, 0, 50, 50, 100)),
                (PlateZ(-100, -100, 100, 100, 7), Box(0, -150, -150, 300, 150, 150)),
            };
            var bad = new List<string>();
            foreach (var (a, b) in pairs)
            {
                var q = MeshIntersection.Query(a, b);
                var r = MeshSurface.Measure(a, null, b, null, VolumeGeometry.Render);
                bool crossing = q.SurfaceRelation == "crossing";
                if (r.CrossingLength is null || (r.InteriorSegments > 0) != crossing) bad.Add($"{q.SurfaceRelation} vs interior {r.InteriorSegments} L {r.CrossingLength?.Lower}");
            }
            var la = Soup(new[] { V(-100, 0, 0), V(100, 0, 0), V(0, 100, 0) }, new[] { V(100, 0, 0), V(-100, 0, 0), V(0, -100, 0) });
            var lb = Soup(new[] { V(-100, 0, 0), V(100, 0, 0), V(0, 0, 100) }, new[] { V(100, 0, 0), V(-100, 0, 0), V(0, 0, -100) });
            var lq = MeshIntersection.Query(la, lb); var lr = MeshSurface.Measure(la, null, lb, null, VolumeGeometry.Render);
            if (lq.SurfaceRelation != "touching" || !Has(lr.CrossingLength, 200) || lr.InteriorSegments != 0) bad.Add($"L1 {lq.SurfaceRelation} {Say(lr)}");
            return (bad.Count == 0, bad.Count == 0 ? "all agree" : string.Join(" | ", bad));
        });

        // ================= SC13: budget =================
        Case(check, "surface SC13: a one-pair budget — incomplete, no final value", () =>
        {
            var r = MeshSurface.Measure(PlateZ(-100, -100, 100, 100, 7), null, Cube(7, 3, 0, 50), null, VolumeGeometry.Render,
                                        new SurfaceOptions(MaxTrianglePairs: 1));
            return (r.Status == "incomplete" && r.CrossingLength is null && r.CoplanarSameFacingArea is null, Say(r));
        });

        // ================= SC14-SC15: live pose (R1, review H1) =================
        // SC14a: the sequence animates "Hinge" (no shapes): under Targeted both P and Q are static -> measured, 24
        // triangles; under Blanket the same file refuses (today's rule). Rejects a blanket refusal with the flag on.
        Case(check, "surface SC14a: manager animates a shapeless node — Targeted measures 24 triangles, Blanket refuses", () =>
        {
            var bytes = Rig("Hinge");
            var t = MeshSurface.ModelSurface(bytes, VolumeGeometry.Render, LivePosePolicy.Targeted);
            var b = MeshSurface.ModelSurface(bytes, VolumeGeometry.Render, LivePosePolicy.Blanket);
            return (t.Status == "measured" && t.Soup?.TriangleCount == 24 && t.Tags?.Length == 24 && b.Status == "refused" && (b.Reason ?? "").Contains("live pose"),
                    $"targeted {t.Status} {t.Reason} {t.Soup?.TriangleCount}; blanket {b.Status} {b.Reason}");
        });
        // SC14b: the sequence animates "Leaf" (P's node): Targeted refuses naming the node and the sequence. Rejects
        // measuring an animated shape at its stored pose.
        Case(check, "surface SC14b: manager animates P's node — Targeted refuses: animated node 'Leaf' (sequence 'Open')", () =>
        {
            var t = MeshSurface.ModelSurface(Rig("Leaf"), VolumeGeometry.Render, LivePosePolicy.Targeted);
            return (t.Status == "refused" && (t.Reason ?? "").Contains("animated node 'Leaf'") && (t.Reason ?? "").Contains("'Open'"), $"{t.Status} {t.Reason}");
        });
        // SC14c: a looping sequence names its cycle. Rejects a refusal that hides the cause.
        Case(check, "surface SC14c: looping sequence — refusal names CYCLE_LOOP", () =>
        {
            var t = MeshSurface.ModelSurface(Rig("Leaf", cycle: CycleType.CYCLE_LOOP), VolumeGeometry.Render, LivePosePolicy.Targeted);
            return (t.Status == "refused" && (t.Reason ?? "").Contains("CYCLE_LOOP"), $"{t.Status} {t.Reason}");
        });
        // SC14d: a skinned shape and a switch node refuse under Targeted with their causes.
        Case(check, "surface SC14d: skinned shape and switch node refuse under Targeted with their causes", () =>
        {
            var sk = MeshSurface.ModelSurface(Rig(null, skinned: true), VolumeGeometry.Render, LivePosePolicy.Targeted);
            var sw = MeshSurface.ModelSurface(Rig(null, switched: true), VolumeGeometry.Render, LivePosePolicy.Targeted);
            return (sk.Status == "refused" && (sk.Reason ?? "").Contains("skinned") && sw.Status == "refused" && (sw.Reason ?? "").Contains("switch"),
                    $"skinned {sk.Status} {sk.Reason}; switch {sw.Status} {sw.Reason}");
        });
        // SC15 (review H1): a behaviour graph and no embedded controller — R1 as first specified would call both shapes
        // static; Targeted must refuse naming the graph. Same with a manager that animates only "Hinge" plus a graph.
        Case(check, "surface SC15: behaviour graph refuses under Targeted (with and without a manager) — 'behaviour graph 'Fixture\\Rig.hkx''", () =>
        {
            var g = MeshSurface.ModelSurface(Rig(null, behaviour: @"Fixture\Rig.hkx"), VolumeGeometry.Render, LivePosePolicy.Targeted);
            var gm = MeshSurface.ModelSurface(Rig("Hinge", behaviour: @"Fixture\Rig.hkx"), VolumeGeometry.Render, LivePosePolicy.Targeted);
            return (g.Status == "refused" && (g.Reason ?? "").Contains(@"behaviour graph 'Fixture\Rig.hkx'") && gm.Status == "refused" && (gm.Reason ?? "").Contains("behaviour graph"),
                    $"graph only {g.Status} {g.Reason}; graph + manager {gm.Status} {gm.Reason}");
        });

        // ================= SC16-SC18: four-setup attribution (review M1) =================
        // SC16: the crossing exists in the +B arm too (B's own placement already crosses A's object): not attributed to
        // the pair. Rejects a crossing already present in a single-mod arm counted for the pair.
        Case(check, "surface SC16: crossing already present in a single-mod arm — present_without_both", () =>
        {
            var combined = MeshSurface.MeasurePlaced(PlateZ(-100, -100, 100, 100, 0), null, new Vec3(0, 0, 7), Zero, 1f, Cube(0, 0, 0, 50), null, new Vec3(7, 3, 0), Zero, 1f, VolumeGeometry.Render);
            var plusB = MeshSurface.MeasurePlaced(PlateZ(-100, -100, 100, 100, 0), null, new Vec3(0, 0, 7), Zero, 1f, Cube(0, 0, 0, 50), null, new Vec3(-9, 1, 4), Zero, 1f, VolumeGeometry.Render);
            var att = MeshSurface.Attribution(combined, null, null, plusB);
            return (att == "present_without_both", $"{att}: combined {Say(combined)} | +B {Say(plusB)}");
        });
        // SC17: the crossing exists only with both mods (single arms: member absent, or placed apart): needs_both.
        Case(check, "surface SC17: crossing only with both mods — needs_both", () =>
        {
            var combined = MeshSurface.MeasurePlaced(PlateZ(-100, -100, 100, 100, 0), null, new Vec3(0, 0, 7), Zero, 1f, Cube(0, 0, 0, 50), null, new Vec3(7, 3, 0), Zero, 1f, VolumeGeometry.Render);
            var plusB = MeshSurface.MeasurePlaced(PlateZ(-100, -100, 100, 100, 0), null, new Vec3(0, 0, 7), Zero, 1f, Cube(0, 0, 0, 50), null, new Vec3(7, 3, 400), Zero, 1f, VolumeGeometry.Render);
            var att = MeshSurface.Attribution(combined, null, null, plusB);
            return (att == "needs_both", $"{att}: combined {Say(combined)} | +B {Say(plusB)}");
        });
        // SC18: an alpha-only crossing never decides: opaque x opaque L = 0 -> no_overlap. Rejects alpha deciding.
        Case(check, "surface SC18: alpha-only crossing — no_overlap (alpha never decides)", () =>
        {
            var alphaOnly = MeshSurface.Measure(PlateZ(-100, -100, 100, 100, 7), new[] { SurfaceMaterial.AlphaTest, SurfaceMaterial.AlphaTest },
                                                Cube(7, 3, 0, 50), null, VolumeGeometry.Render);
            var att = MeshSurface.Attribution(alphaOnly, null, null, null);
            return (att == "no_overlap" && Has(alphaOnly.CrossingLength, 400), $"{att}: {Say(alphaOnly)}");
        });

        // ================= SC19: collision geometry is reported as collision =================
        Case(check, "surface SC19: collision geometry tags 'collision x collision'", () =>
        {
            var r = MeshSurface.Measure(PlateZ(-100, -100, 100, 100, 7), null, Cube(7, 3, 0, 50), null, VolumeGeometry.Collision);
            return (r.Status == "measured" && r.Geometry == "collision" && Near(Tag(r.CrossingLengthByTags, "collision", "collision"), 400), Say(r));
        });
    }
}
