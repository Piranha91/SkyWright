using System.Numerics;
using HarnessShared;
using HousecarlCore;
using Mutagen.Bethesda;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Plugins.Records;
using Mutagen.Bethesda.Skyrim;
using NiflySharp;
using NiflySharp.Blocks;
using NiflySharp.Enums;
using NiflySharp.Structs;

/// <summary>Known answers for the G1 build list, Stage A (plan `g1/PLAN-7.md`, Gate A review F1, F3, Q5, Q6): the anchor
/// test (row 2, A*), furniture entry positions (row 2, F*), the armour ground model in the overlap index (row 2, Q6,
/// A10*) and the navmesh distance (row 4, N*). Expected values are analytic, stated beside each case; placements use
/// crossprobe's pinned hand-computed boxes C and D (yaw 90 sends model +X to world -Y). Each case names the wrong answer it
/// rejects. Fixture controls hold on the base; every other case is red until the stubs are implemented.</summary>
static class AnchorReachProof
{
    const float Deg = MathF.PI / 180f;
    static bool Near(double? a, double b, double tol = 1e-6) => a is { } x && Math.Abs(x - b) <= tol;
    static void Case(Action<string, bool, string> check, string name, Func<(bool Ok, string Detail)> body)
    {
        (bool ok, string detail) r;
        try { r = body(); }
        catch (Exception ex) { r = (false, ex.GetType().Name + ": " + ex.Message); }
        check(name, r.ok, r.detail);
    }
    static string Say(AnchorResult r) =>
        $"point {r.Point} ({r.PointReason}) line {r.Line} [{string.Join(", ", r.LineCrossingHeights ?? Array.Empty<double>())}] inside {r.LineInsideLength} body {r.Body} clearance {r.BodyClearance} -> {r.Decision} {r.Reason}";

    static TriangleSoup Box(float x0, float y0, float z0, float x1, float y1, float z1)
    {
        var v = new[] { new Vector3(x0, y0, z0), new Vector3(x1, y0, z0), new Vector3(x1, y1, z0), new Vector3(x0, y1, z0),
                        new Vector3(x0, y0, z1), new Vector3(x1, y0, z1), new Vector3(x1, y1, z1), new Vector3(x0, y1, z1) };
        var i = new[] { 0,2,1, 0,3,2, 4,5,6, 4,6,7, 0,1,5, 0,5,4, 1,2,6, 1,6,5, 2,3,7, 2,7,6, 3,0,4, 3,4,7 };
        return new TriangleSoup(v, i);
    }
    /// <summary>The same box with its two top (+Z) triangles removed: an open mesh.</summary>
    static TriangleSoup OpenTopBox(float x0, float y0, float z0, float x1, float y1, float z1)
    {
        var b = Box(x0, y0, z0, x1, y1, z1);
        var idx = b.Indices.ToList(); idx.RemoveRange(6, 6);
        return new TriangleSoup(b.Vertices, idx.ToArray());
    }
    /// <summary>An outer box with an inner box wound inward (a cavity).</summary>
    static TriangleSoup Cavity()
    {
        var o = Box(0, 0, 0, 100, 100, 100); var n = Box(40, 40, 40, 60, 60, 60);
        var v = o.Vertices.Concat(n.Vertices).ToArray();
        var i = o.Indices.ToList();
        for (int k = 0; k < n.Indices.Length; k += 3) { i.Add(n.Indices[k] + 8); i.Add(n.Indices[k + 2] + 8); i.Add(n.Indices[k + 1] + 8); }
        return new TriangleSoup(v, i.ToArray());
    }
    static TriangleSoup PlateZ(float half, float z) =>
        new(new[] { new Vector3(-half, -half, z), new Vector3(half, -half, z), new Vector3(half, half, z), new Vector3(-half, half, z) }, new[] { 0, 1, 2, 0, 2, 3 });
    static readonly Vec3 O = new(0, 0, 0);
    static BodyExtent Humanoid => new(ActorBand.PhysicalStep, ActorBand.PhysicalControllerTop(ActorBand.ScaleOneHeight), RouteConnectivity.DefaultCapsuleRadius);

    static readonly NiVersion Ver = new() { FileVersion = NiVersion.ToFile("20.2.0.7"), UserVersion = 12, StreamVersion = 100 };
    /// <summary>A furniture marker NIF: root with a BSFurnitureMarkerNode holding one position (offset (10,0,0), heading
    /// 90 degrees, Lean, entry Front).</summary>
    static byte[] FurnitureNif()
    {
        var f = new NifFile(); f.Create(Ver, withRootNode: true);
        var root = f.GetRootNodes().First(); root.Flags_ui = 0xE;
        var pos = new FurniturePosition { Offset = new Vector3(10, 0, 0), Heading = 90 * Deg, AnimationType = AnimationType.Lean, EntryProperties = FurnitureEntryPoints.Front };
        var fm = new BSFurnitureMarkerNode { Name = new NiStringRef("FRN"), Positions = new List<FurniturePosition> { pos }, NumPositions = 1 };
        root.ExtraDataList ??= new();
        root.ExtraDataList.AddBlockRef(f.AddBlock(fm));
        root.NumExtraDataList = 1;
        using var ms = new MemoryStream();
        if (f.Save(ms) != 0) throw new InvalidOperationException("furniture fixture failed to save");
        return ms.ToArray();
    }

    public static void Run(Action<string, bool, string> check)
    {
        var body = Humanoid;

        // ================= fixture controls (hold on the base) =================
        Case(check, "stageA7 fixture control: the humanoid body is PhysicalStep 58.4 .. PhysicalControllerTop(128) 119.2, radius 22", () =>
            (Near(body.Bottom, 58.4, 1e-4) && Near(body.Top, 119.2, 1e-4) && body.Radius == 22f, $"{body}"));
        Case(check, "stageA7 fixture control (F3): Mutagen NavmeshTriangle.Flag.Deleted is 0x08 (the value viewprobe and RouteConnectivity type)", () =>
            ((int)NavmeshTriangle.Flag.Deleted == 0x08, $"Deleted = 0x{(int)NavmeshTriangle.Flag.Deleted:X}"));
        Case(check, "stageA7 fixture control: the furniture NIF round-trips one position (offset (10,0,0), Lean)", () =>
        {
            var n = new NifFile(); using (var ms = new MemoryStream(FurnitureNif())) n.Load(ms);
            var p = n.Blocks.OfType<BSFurnitureMarker>().SelectMany(b => b.Positions).ToList();
            return (p.Count == 1 && p[0].Offset == new Vector3(10, 0, 0) && p[0].AnimationType == AnimationType.Lean, $"positions {p.Count} {(p.Count > 0 ? p[0].Offset + " " + p[0].AnimationType : "")}");
        });

        // ================= A: the anchor test (row 2) =================
        // A1: box [0,200]x[-20,20]x[0,100] at the origin, yaw 90 -> world [-20,20]x[-200,0]x[0,100] (crossprobe C). Anchor
        // (0,-100,50) is inside. Rejects a dropped rotation (box along +X) and the wrong sign (box along +Y): outside.
        Case(check, "stageA7 A1: yaw 90 off-centre box — anchor (0,-100,50) inside", () =>
        {
            var r = MeshAnchor.TestPlaced(Box(0, -20, 0, 200, 20, 100), null, O, new Vec3(0, 0, 90 * Deg), 1f, new Vector3(0, -100, 50), body);
            return (r.Point == "inside", Say(r));
        });
        // A2: the same at scale 2 -> [-40,40]x[-400,0]x[0,200]; anchor (0,-300,150) inside. Rejects a missing scale.
        Case(check, "stageA7 A2: scale 2 — anchor (0,-300,150) inside (outside the unscaled box)", () =>
        {
            var r = MeshAnchor.TestPlaced(Box(0, -20, 0, 200, 20, 100), null, O, new Vec3(0, 0, 90 * Deg), 2f, new Vector3(0, -300, 150), body);
            return (r.Point == "inside", Say(r));
        });
        // A3: crossprobe case D (three axes, scale 2): the box's centre is the centre of its hand-computed world AABB,
        // (501.32435, -435.16865, 218.12265). Rejects a transposed composition / wrong axis order (the centre falls outside).
        Case(check, "stageA7 A3: three axes + scale 2 (crossprobe D) — the box centre is inside", () =>
        {
            var r = MeshAnchor.TestPlaced(Box(0, -20, 0, 200, 20, 100), null, new Vec3(500, -300, 40), new Vec3(20 * Deg, -35 * Deg, 110 * Deg), 2f,
                                          new Vector3(501.32435f, -435.16865f, 218.12265f), body);
            return (r.Point == "inside", Say(r));
        });
        // A4: an anchor exactly on a face of the cube [0,20]^3 reads on_surface. Rejects on-surface read as inside or outside.
        Case(check, "stageA7 A4: anchor (20,10,10) on the face of cube [0,20]^3 — on_surface", () =>
        {
            var r = MeshAnchor.TestPlaced(Box(0, 0, 0, 20, 20, 20), null, O, O, 1f, new Vector3(20, 10, 10), body);
            return (r.Point == "on_surface", Say(r));
        });
        // A5: inside a box whose top is open: point undecided, naming the open mesh. Rejects parity on an open mesh.
        Case(check, "stageA7 A5: anchor inside an open-topped box — point undecided (open), not inside", () =>
        {
            var r = MeshAnchor.TestPlaced(OpenTopBox(0, 0, 0, 20, 20, 20), null, O, O, 1f, new Vector3(10, 10, 10), body);
            return (r.Point == "undecided" && (r.PointReason ?? "").Contains("open"), Say(r));
        });
        // A7 (corrected at the fit, PLAN-7 §13): a solid with an inward-wound cavity component [40,60]^3. An anchor IN the
        // cavity (50,50,50) is undecided with the validator's reason — rejects the cavity read as solid ("inside"). An
        // anchor in the shell's material (10,10,10), outside the cavity's box, is inside. (The red-gate version asserted
        // "undecided" at (10,10,10), which is in solid material: that expectation was wrong.)
        Case(check, "stageA7 A7: cavity component — anchor in the cavity undecided (validator's reason), anchor in the shell inside", () =>
        {
            var c = MeshAnchor.TestPlaced(Cavity(), null, O, O, 1f, new Vector3(50, 50, 50), body);
            var m = MeshAnchor.TestPlaced(Cavity(), null, O, O, 1f, new Vector3(10, 10, 10), body);
            return (c.Point == "undecided" && (c.PointReason ?? "").Contains("negatively_oriented") && m.Point == "inside", $"cavity: {Say(c)} | shell: {Say(m)}");
        });
        // A8: an open horizontal plate at z 80 over the anchor (origin): the line [58.4, 119.2] crosses at 80, exactly; the
        // body meets. Rejects a crossing missed because the mesh is open.
        Case(check, "stageA7 A8: open plate at z 80 — line crosses at 80, body meets", () =>
        {
            var r = MeshAnchor.TestPlaced(PlateZ(50, 80), null, O, O, 1f, Vector3.Zero, body);
            return (r.Line == "crosses" && r.LineCrossingHeights.Count == 1 && Near(r.LineCrossingHeights[0], 80) && r.Body == "meets" && r.Decision == "meets", Say(r));
        });
        // A9: a closed slab [-50,50]^2 x [-10,1] with the anchor 1 u under its top: point inside, line and body clear
        // (the body starts 58.4 above the feet), decision clear. Rejects a floor graze reported as the body in a solid.
        Case(check, "stageA7 A9: anchor 1 u under a slab top — point inside, line clear, body clear, decided clear", () =>
        {
            var r = MeshAnchor.TestPlaced(Box(-50, -50, -10, 50, 50, 1), null, O, O, 1f, Vector3.Zero, body);
            return (r.Point == "inside" && r.Line == "clear" && r.Body == "clear" && r.Decision == "clear", Say(r));
        });
        // A11 (review F1): a closed wall x [10,40] beside the anchor: the line is clear, the body (radius 22) is inside the
        // wall, decided "meets" by the body. Rejects "line clear, body inside" decided as clear.
        Case(check, "stageA7 A11: wall 10 u beside the anchor — line clear, body meets, decided meets (by the body)", () =>
        {
            var r = MeshAnchor.TestPlaced(Box(10, -100, 0, 40, 100, 200), null, O, O, 1f, Vector3.Zero, body);
            return (r.Line == "clear" && r.Body == "meets" && r.Decision == "meets", Say(r));
        });
        // A12: the wall at x [30,60]: clear, clearance 30 - 22 = 8 exactly. Rejects a clearance measured from the line (30).
        Case(check, "stageA7 A12: wall 30 u away — body clear, clearance 8", () =>
        {
            var r = MeshAnchor.TestPlaced(Box(30, -100, 0, 60, 100, 200), null, O, O, 1f, Vector3.Zero, body);
            return (r.Body == "clear" && Near(r.BodyClearance, 8, 1e-4) && r.Decision == "clear", Say(r));
        });
        // A13 (pending the reviewer's ruling on the support half-space): a lean furniture's wall x [-30,-10] behind the
        // entry; excluding the declared support side (x < -6) the body is clear; without the exclusion it meets.
        Case(check, "stageA7 A13: lean support wall behind the entry — clear with the support half-space excluded, meets without", () =>
        {
            var wall = Box(-30, -100, 0, -10, 100, 200);
            var w = MeshAnchor.TestPlaced(wall, null, O, O, 1f, Vector3.Zero, body, new HalfSpace(new Vector3(1, 0, 0), -6f));
            var n = MeshAnchor.TestPlaced(wall, null, O, O, 1f, Vector3.Zero, body);
            return (w.Decision == "clear" && n.Decision == "meets", $"with exclusion: {Say(w)} | without: {Say(n)}");
        });
        // A13b (red-gate review): with the support exclusion active (x < -6), a solid entering the body IN FRONT of the face
        // (a post at x [5,15]) still meets, and the detail names the exclusion. Rejects an exclusion that hides everything.
        Case(check, "stageA7 A13b: exclusion active, a post in front of the support face — still meets, exclusion named", () =>
        {
            var post = Box(5, -5, 0, 15, 5, 200);
            var r = MeshAnchor.TestPlaced(post, null, O, O, 1f, Vector3.Zero, body, new HalfSpace(new Vector3(1, 0, 0), -6f));
            return (r.Decision == "meets" && r.Body == "meets" && (r.Reason ?? "").Contains("support"), Say(r));
        });
        // A14: the A11 wall tagged alpha_test: its contact does not decide (session 6's material rule): undecided, naming
        // alpha_test. Rejects an alpha surface deciding a clash.
        Case(check, "stageA7 A14: alpha-tested wall in the body — undecided, naming alpha_test", () =>
        {
            var wall = Box(10, -100, 0, 40, 100, 200);
            var tags = Enumerable.Repeat(SurfaceMaterial.AlphaTest, wall.TriangleCount).ToArray();
            var r = MeshAnchor.TestPlaced(wall, tags, O, O, 1f, Vector3.Zero, body);
            return (r.Decision == "undecided" && (r.Reason ?? "").Contains(SurfaceMaterial.AlphaTest), Say(r));
        });

        // ================= F: furniture entries (review F1) =================
        // F1: the NIF's one position; F2: at a reference (100,0,0) yaw 90 the entry (10,0,0) lands at (100,-10,0). Rejects
        // the furniture reference's origin used as the actor's position, and a dropped rotation ((110,0,0)).
        Case(check, "stageA7 F1: furniture entries — one position, offset (10,0,0), heading 90 deg, Lean", () =>
        {
            var e = MeshAnchor.FurnitureEntries(FurnitureNif());
            return (e.Count == 1 && e[0].Offset == new Vector3(10, 0, 0) && Near(e[0].Heading, 90 * Deg, 1e-5) && e[0].AnimationType == "Lean",
                    string.Join("; ", e.Select(x => $"{x.Offset} {x.Heading} {x.AnimationType} {x.EntryPoints}")));
        });
        Case(check, "stageA7 F2: entry world position at ref (100,0,0) yaw 90 = (100,-10,0)", () =>
        {
            var e = MeshAnchor.FurnitureEntries(FurnitureNif())[0];
            var w = MeshAnchor.EntryWorld(e, new Vec3(100, 0, 0), new Vec3(0, 0, 90 * Deg), 1f);
            return (Near(w.X, 100, 1e-3) && Near(w.Y, -10, 1e-3) && Near(w.Z, 0, 1e-3), $"{w}");
        });

        // ================= A10: armour ground models in the overlap index (review Q6) =================
        // An Armor whose WorldModel gives male m.nif and female f.nif: BaseMesh = male, BaseMeshFemale = female, BaseInfo
        // type "Armor"; an Armor with one model for both: both maps equal. Rejects "no model path" for armour and a
        // silently chosen gender.
        Case(check, "stageA7 A10: armour WorldModel read — male in BaseMesh, female in BaseMeshFemale, type Armor", () =>
        {
            var release = SkyrimRelease.SkyrimSE;
            var key = new ModKey("A7Armour", ModType.Master);
            var two = new FormKey(key, 0x801); var one = new FormKey(key, 0x802);
            var mod = new SkyrimMod(key, release);
            mod.Armors.Add(new Armor(two, release)
            {
                EditorID = "TwoGender",
                WorldModel = new GenderedItem<ArmorModel?>(new ArmorModel { Model = new Model { File = "meshes\\a7\\m.nif" } },
                                                           new ArmorModel { Model = new Model { File = "meshes\\a7\\f.nif" } }),
            });
            mod.Armors.Add(new Armor(one, release)
            {
                EditorID = "OneModel",
                WorldModel = new GenderedItem<ArmorModel?>(new ArmorModel { Model = new Model { File = "meshes\\a7\\b.nif" } },
                                                           new ArmorModel { Model = new Model { File = "meshes\\a7\\b.nif" } }),
            });
            var idx = new OverlapIndex();
            idx.Ingest(0, mod);
            idx.BaseMesh.TryGetValue(two, out var m2); idx.BaseMeshFemale.TryGetValue(two, out var f2);
            idx.BaseMesh.TryGetValue(one, out var m1); idx.BaseMeshFemale.TryGetValue(one, out var f1);
            bool typed = idx.BaseInfo.TryGetValue(two, out var bi) && bi.Item1 == "Armor";
            return (m2 == "meshes\\a7\\m.nif" && f2 == "meshes\\a7\\f.nif" && m1 == "meshes\\a7\\b.nif" && f1 == "meshes\\a7\\b.nif" && typed,
                    $"two: {m2} / {f2}; one: {m1} / {f1}; type {(typed ? "Armor" : "missing")}");
        });

        // ================= N: navmesh distance (row 4, review F3/F4) =================
        var flat = new NavTriangle(new Vector3(0, 0, 0), new Vector3(100, 0, 0), new Vector3(0, 100, 0), 0, "Nav", 0);
        // N1: a point 50 above a triangle: 50, horizontal 0, vertical 50. Rejects a 2D distance (0).
        Case(check, "stageA7 N1: point 50 u above a triangle — distance 50 (horizontal 0, vertical 50)", () =>
        {
            var d = NavmeshReach.FromPoint(new Vector3(10, 10, 50), new[] { flat });
            return (d.Status == "measured" && Near(d.Distance, 50) && Near(d.Horizontal, 0) && Near(d.Vertical, 50), $"{d}");
        });
        // N2: a long thin triangle (0,0,0),(1000,0,0),(0,10,0) and the point (900,20,0): the nearest point is on the long edge,
        // distance 1.9 / sqrt(1e-6 + 1e-2). Rejects the centroid distance (about 567).
        Case(check, "stageA7 N2: long thin triangle — exact edge distance 1.9/sqrt(0.010001), not the centroid's", () =>
        {
            var t = new NavTriangle(new Vector3(0, 0, 0), new Vector3(1000, 0, 0), new Vector3(0, 10, 0), 0, "Nav", 1);
            var d = NavmeshReach.FromPoint(new Vector3(900, 20, 0), new[] { t });
            return (d.Status == "measured" && Near(d.Distance, 1.9 / Math.Sqrt(1e-6 + 1e-2), 1e-4), $"{d}");
        });
        // N3: an object box x [0,500] and a triangle starting at x 600: 100 from the geometry. Rejects the reference
        // position (the origin, 600 away) used for a meshed object.
        Case(check, "stageA7 N3: object geometry x [0,500] vs a triangle at x 600 — 100 (not 600 from the origin)", () =>
        {
            var t = new NavTriangle(new Vector3(600, 0, 0), new Vector3(700, 0, 0), new Vector3(600, 10, 0), 0, "Nav", 2);
            var d = NavmeshReach.FromSoup(Box(0, 0, 0, 500, 10, 10), new[] { t });
            return (d.Status == "measured" && Near(d.Distance, 100, 1e-4), $"{d}");
        });
        // N4 (F3): a triangle flagged Deleted (by the Mutagen member) right under the point is skipped; the live one 200 away
        // is reported. Rejects a deleted triangle counted.
        Case(check, "stageA7 N4: a Deleted-flagged triangle (NavmeshTriangle.Flag.Deleted) is skipped — 200 to the live one", () =>
        {
            var dead = new NavTriangle(new Vector3(-10, -10, 0), new Vector3(10, -10, 0), new Vector3(0, 10, 0), (ushort)NavmeshTriangle.Flag.Deleted, "Nav", 3);
            var live = new NavTriangle(new Vector3(200, -10, 0), new Vector3(220, -10, 0), new Vector3(200, 10, 0), 0, "Nav", 4);
            var d = NavmeshReach.FromPoint(Vector3.Zero, new[] { dead, live });
            return (d.Status == "measured" && Near(d.Distance, 200, 1e-4) && d.Triangle == 4 && NavmeshReach.IsDeleted((ushort)NavmeshTriangle.Flag.Deleted), $"{d}");
        });
        // N5: no triangle at all refuses. Rejects 0 or a number.
        Case(check, "stageA7 N5: no navmesh — refused, no distance", () =>
        {
            var d = NavmeshReach.FromPoint(Vector3.Zero, Array.Empty<NavTriangle>());
            return (d.Status == "refused" && d.Distance is null, $"{d}");
        });
    }
}
