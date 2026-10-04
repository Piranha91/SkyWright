using System.Numerics;
using HousecarlCore;
using NiflySharp;
using NiflySharp.Blocks;
using NiflySharp.Enums;
using NiflySharp.Structs;

/// <summary>Known answers for lane 3b (2026-10-04): the five contactprobe pair-measure gaps that held G1 clusters at cannot tell.
/// (a) a uniform factor stored in a render node's rotation matrix; (b) a moving partner in the anchor test, and the 1-DOF blend rule on
/// a node that moves only particles; (c) slivers collapsed by single-precision world placement; (d) a NiPathInterpolator partner, as a
/// tube around its curve; (e) particle systems set aside from the render surface, and the box-against-surface contact that measures
/// them. Each case names the wrong answer it rejects. Red at the Gate A commit except the cases marked guard.</summary>
static partial class MotionProof
{
    // ---------------- (a) fixtures ----------------
    /// <summary>root -> one shape "Plate" with the given rotation rows, scale field 1 and translation (10,0,0); one triangle
    /// (2,0,0), (0,2,0), (0,0,2).</summary>
    static byte[] MatrixNif(Matrix33 rows)
    {
        var f = new NifFile(); f.Create(Ver, withRootNode: true);
        var root = f.GetRootNodes().First(); root.Flags_ui = 0xE;
        var s = Shape("Plate", new TriangleSoup(new[] { new Vector3(2, 0, 0), new Vector3(0, 2, 0), new Vector3(0, 0, 2) }, new[] { 0, 1, 2 }));
        s.Rotation = rows; s.Scale = 1f; s.Translation = new Vector3(10, 0, 0);
        root.Children.AddBlockRef(f.AddBlock(s));
        return Save(f, "matrix");
    }
    static Matrix33 M33(float a, float b, float c, float d, float e, float g, float h, float i, float j) =>
        new() { M11 = a, M12 = b, M13 = c, M21 = d, M22 = e, M23 = g, M31 = h, M32 = i, M33 = j };
    static bool HasVertex(TriangleSoup s, Vector3 v, float tol = 1e-4f) => s.Vertices.Any(x => Vector3.Distance(x, v) <= tol);

    // ---------------- (c) fixtures ----------------
    // A: a sliver (0,0,0), (100,0,0), (100,0.001,0) - area 0.05 u^2 in model space - plus nothing else.
    // B: a vertical quad in the plane y = 0.0005, x 50..150, z -10..10: it crosses the sliver along x 50..100 (length 50).
    static readonly TriangleSoup SliverA = new(new[] { new Vector3(0, 0, 0), new Vector3(100, 0, 0), new Vector3(100, 0.001f, 0) }, new[] { 0, 1, 2 });
    static readonly TriangleSoup QuadB = new(new[] { new Vector3(50, 0.0005f, -10), new Vector3(150, 0.0005f, -10), new Vector3(150, 0.0005f, 10), new Vector3(50, 0.0005f, 10) },
                                             new[] { 0, 1, 2, 0, 2, 3 });
    static readonly Vec3 FarOrigin = new(-68447.203125f, 104265.203125f, -8496f), Zero3 = new(0, 0, 0);

    // ---------------- (d) fixtures ----------------
    /// <summary>A node "Bird" driven by a direct NiTransformController with a NiPathInterpolator over the given keys; its shape a 4 u
    /// cube centred 50 u above the node (subtree radius sqrt(2^2 + 2^2 + 52^2) = 52.0769).</summary>
    static byte[] PathNifKeys(KeyType type, params (float T, Vector3 V, Vector3 Fwd, Vector3 Bwd)[] keys)
    {
        var f = new NifFile(); f.Create(Ver, withRootNode: true);
        var root = f.GetRootNodes().First(); root.Flags_ui = 0xE;
        var bird = new NiNode { Name = new NiStringRef("Bird"), Flags_ui = 0xE, Scale = 1f, Rotation = Identity33() };
        int bi = f.AddBlock(bird); root.Children.AddBlockRef(bi);
        bird.Children.AddBlockRef(f.AddBlock(Shape("Body", Box(-2, -2, 48, 2, 2, 52))));
        var g = new KeyGroup<Vector3> { Interpolation = type, Keys = keys.Select(k => new Key<Vector3> { Time = k.T, Value = k.V, Forward = k.Fwd, Backward = k.Bwd }).ToList(), NumKeys = (uint)keys.Length };
        var pi = new NiPathInterpolator { PathData = new(f.AddBlock(new NiPosData { Data = g })), PercentData = new(-1) };
        var ctl = new NiTransformController { Interpolator = new(f.AddBlock(pi)), StartTime = 0, StopTime = 1, Frequency = 1 };
        ctl.Flags.Active = true; ctl.Target = new(bi); bird.Controller = new(f.AddBlock(ctl));
        return Save(f, "path keys");
    }
    const double BirdR = 52.076866;
    static double SegDist(Vector3 p, Vector3 a, Vector3 b)
    {
        var ab = b - a; float t = ab.LengthSquared() > 0 ? Math.Clamp(Vector3.Dot(p - a, ab) / ab.LengthSquared(), 0, 1) : 0;
        return Vector3.Distance(p, a + t * ab);
    }
    static bool InTube(PathTubeResult t, Vector3 p) => t.Pieces.Any(c => SegDist(p, c.A, c.B) <= c.Radius);
    static TriangleSoup FlatQuad(float x0, float y0, float x1, float y1, float z) =>
        new(new[] { new Vector3(x0, y0, z), new Vector3(x1, y0, z), new Vector3(x1, y1, z), new Vector3(x0, y1, z) }, new[] { 0, 1, 2, 0, 2, 3 });
    static PathTubeResult Tube(byte[] nif) => NifMotion.PathTube(nif, NifMotion.Read(nif, null, null, new MotionReadOptions(PathHull: true)), null);

    // ---------------- (b) fixtures ----------------
    /// <summary>A door: panel x 0..100, y -1..1, z 0..200 on node "Mover" at the origin; sequence "Open" (CLAMP, 1 s) turns it about
    /// z from 0 to pi/2 (LINEAR): +x swings toward +y (rows = Rz^T, the convention RotZ/AngleZ pin).</summary>
    static byte[] DoorNif() => Rig(Box(0, -1, 0, 100, 1, 200), Vector3.Zero,
                                   Xyz(NoKeys(), NoKeys(), FG(KeyType.LINEAR_KEY, FK(0, 0), FK(1, Pi / 2))));
    static readonly BodyExtent DoorBody = new(10, 150, 10);
    static SweptRegion DoorRegion(MotionOptions? o = null)
    {
        var nif = DoorNif();
        return SweptGeometry.Place(SweptGeometry.Build(nif, NifMotion.Read(nif, null), null, VolumeGeometry.Render, o ?? new MotionOptions()), Zero3, Zero3, 1f);
    }
    /// <summary>root -> "Lid" (a 10 u cube) and "SteamEmitter" (a particle system; with <paramref name="meshToo"/> also a cube); one
    /// sequence "Idle" (CLAMP) translates both nodes, Lid along x only and SteamEmitter along x then y (two directions).</summary>
    static byte[] LidAndEmitterNif(bool meshToo)
    {
        var f = new NifFile(); f.Create(Ver, withRootNode: true);
        var root = f.GetRootNodes().First(); root.Flags_ui = 0xE;
        var lid = new NiNode { Name = new NiStringRef("Lid"), Flags_ui = 0xE, Scale = 1f, Rotation = Identity33() };
        var em = new NiNode { Name = new NiStringRef("SteamEmitter"), Flags_ui = 0xE, Scale = 1f, Rotation = Identity33() };
        root.Children.AddBlockRef(f.AddBlock(lid)); int emi = f.AddBlock(em); root.Children.AddBlockRef(emi);
        lid.Children.AddBlockRef(f.AddBlock(Shape("LidCube", Cube(0, 0, 0, 5))));
        var ps = new NiParticleSystem { Name = new NiStringRef("Steam"), Flags_ui = 0xE, Scale = 1f, Rotation = Identity33(), WorldSpace = true };
        int psi = f.AddBlock(ps); em.Children.AddBlockRef(psi);
        ps.Data_NiBlockRef_NPSD = new(f.AddBlock(new NiPSysData { NumParticles = 0 }));
        ps.Modifiers ??= new();
        void AddMod(NiPSysModifier m) { m.Target = new(psi); m.Active = true; ps.Modifiers.AddBlockRef(f.AddBlock(m)); }
        AddMod(new NiPSysAgeDeathModifier { Name = new NiStringRef("Age") });
        AddMod(new NiPSysSphereEmitter { Name = new NiStringRef("Emitter"), Radius = 5f, Speed = 20f, LifeSpan = 2f, InitialRadius = 1f, EmitterObject = new(emi) });
        AddMod(new NiPSysPositionModifier { Name = new NiStringRef("Position") });
        ps.NumModifiers = (uint)ps.Modifiers.Count;
        if (meshToo) em.Children.AddBlockRef(f.AddBlock(Shape("EmitterCube", Cube(0, 0, 0, 2))));
        NiTransformData Tr(params (float T, Vector3 V)[] k) => new() { RotationType = KeyType.LINEAR_KEY, Translations = VG(KeyType.LINEAR_KEY, k), Scales = NoKeys() };
        int dLid = f.AddBlock(Tr((0, Vector3.Zero), (1, new Vector3(10, 0, 0))));
        int dEm = f.AddBlock(Tr((0, Vector3.Zero), (0.5f, new Vector3(10, 0, 0)), (1, new Vector3(10, 10, 0))));
        var mtc = new NiMultiTargetTransformController(); int mtci = f.AddBlock(mtc);
        var seq = new NiControllerSequence { Name = new NiStringRef("Idle"), CycleType = CycleType.CYCLE_CLAMP, StartTime = 0, StopTime = 1, Frequency = 1, Weight = 1 };
        seq.ControlledBlocks = new List<ControlledBlock>
        {
            new() { NodeName = new NiStringRef("Lid"), ControllerType = new NiStringRef("NiTransformController"), Interpolator = new(f.AddBlock(new NiTransformInterpolator { Data = new(dLid) })), Controller = new(mtci) },
            new() { NodeName = new NiStringRef("SteamEmitter"), ControllerType = new NiStringRef("NiTransformController"), Interpolator = new(f.AddBlock(new NiTransformInterpolator { Data = new(dEm) })), Controller = new(mtci) },
        };
        seq.NumControlledBlocks = 2;
        var mgr = new NiControllerManager(); int mgri = f.AddBlock(mgr);
        mgr.ControllerSequences ??= new(); mgr.ControllerSequences.AddBlockRef(f.AddBlock(seq)); mgr.NumControllerSequences = 1;
        mgr.NextController = new(mtci); root.Controller = new(mgri);
        return Save(f, "lid and emitter");
    }

    public static void RunContactGaps(Action<string, bool, string> check)
    {
        // ================= (a) a uniform factor stored in the rotation matrix =================
        // K-A1: rows 0.5 x Rz(90) (rows (0,0.5,0), (-0.5,0,0), (0,0,0.5)), scale field 1, translation (10,0,0). Read as given (row
        // vector times rows, NifGeometry's sense): (2,0,0) -> (10,1,0), (0,2,0) -> (9,0,0), (0,0,2) -> (10,0,1). Rejects the refusal
        // (render_unreadable) and the factor divided out ((2,0,0) -> (10,2,0)).
        Case(check, "lane3b K-A1: rotation rows 0.5 x Rz(90), scale 1 - read as given: (10,1,0), (9,0,0), (10,0,1)", () =>
        {
            var o = NifGeometry.MeasureVisibleRender(MatrixNif(M33(0, 0.5f, 0, -0.5f, 0, 0, 0, 0, 0.5f)), includeTriangles: true);
            if (o.Model?.Soup is not { } s) return (false, "refused: " + o.Error);
            return (HasVertex(s, new(10, 1, 0)) && HasVertex(s, new(9, 0, 0)) && HasVertex(s, new(10, 0, 1)) && !HasVertex(s, new(10, 2, 0)),
                    string.Join(" ", s.Vertices.Select(S)));
        });
        // K-A2: a mirrored factor, rows 0.5 x diag(1, 1, -1) (determinant < 0): (0,0,2) -> (10,0,-1). Rejects the refusal and a reading
        // that drops the mirror ((10,0,1)).
        Case(check, "lane3b K-A2: rotation rows 0.5 x diag(1,1,-1) - read as given: (0,0,2) -> (10,0,-1)", () =>
        {
            var o = NifGeometry.MeasureVisibleRender(MatrixNif(M33(0.5f, 0, 0, 0, 0.5f, 0, 0, 0, -0.5f)), includeTriangles: true);
            if (o.Model?.Soup is not { } s) return (false, "refused: " + o.Error);
            return (HasVertex(s, new(10, 0, -1)) && HasVertex(s, new(11, 0, 0)) && !HasVertex(s, new(10, 0, 1)), string.Join(" ", s.Vertices.Select(S)));
        });
        // K-A3 (guard): rows diag(1, 2, 1) are not one factor times a rotation: still refused by name. Rejects accepting any matrix.
        Case(check, "lane3b K-A3 (guard): rotation rows diag(1,2,1) (non-uniform) - still refused as an invalid render transform", () =>
        {
            var o = NifGeometry.MeasureVisibleRender(MatrixNif(M33(1, 0, 0, 0, 2, 0, 0, 0, 1)), includeTriangles: true);
            return (o.Model is null && (o.Error ?? "").Contains("invalid render transform"), o.Error ?? "measured");
        });
        // K-A4 (guard): rows with a sheared pair (rows (1,0,0), (0.5,1,0), (0,0,1)): still refused.
        Case(check, "lane3b K-A4 (guard): sheared rotation rows - still refused", () =>
        {
            var o = NifGeometry.MeasureVisibleRender(MatrixNif(M33(1, 0, 0, 0.5f, 1, 0, 0, 0, 1)), includeTriangles: true);
            return (o.Model is null && (o.Error ?? "").Contains("invalid render transform"), o.Error ?? "measured");
        });
        // K-A5 (guard): a non-finite entry: still refused.
        Case(check, "lane3b K-A5 (guard): a NaN rotation entry - still refused", () =>
        {
            var o = NifGeometry.MeasureVisibleRender(MatrixNif(M33(float.NaN, 0, 0, 0, 1, 0, 0, 0, 1)), includeTriangles: true);
            return (o.Model is null, o.Error ?? "measured");
        });
        // K-A6: the render surface of K-A1's mesh is measured (contactprobe's reader). Rejects render_unreadable.
        Case(check, "lane3b K-A6: ModelSurface of the 0.5 x Rz(90) mesh - measured", () =>
        {
            var t = MeshSurface.ModelSurface(MatrixNif(M33(0, 0.5f, 0, -0.5f, 0, 0, 0, 0, 0.5f)), VolumeGeometry.Render);
            return (t.Status == "measured", $"{t.Status} {t.Reason}");
        });

        // ================= (c) slivers collapsed by single-precision placement =================
        // K-C1: the sliver and the quad placed at city-scale coordinates (rotation 0, scale 1): measured in the pair's frame, crossing
        // length 50 (+- rounding). Rejects the refusal (A: a_degenerate_triangle) and dropping the collapsed sliver (crossing 0).
        Case(check, "lane3b K-C1: a sliver placed at (-68447.2, 104265.2, -8496) - measured, crossing length 50", () =>
        {
            var r = MeshSurface.MeasurePlaced(SliverA, null, FarOrigin, Zero3, 1f, QuadB, null, FarOrigin, Zero3, 1f, VolumeGeometry.Render);
            return (r.Status == "measured" && r.CrossingLength is { } L && Math.Abs(L.Lower - 50) < 0.01 && L.Upper < 50.01,
                    $"{r.Status} {r.Reason} L {r.CrossingLength}");
        });
        // K-C2 (guard): the same pair at the origin is measured in the world frame, crossing 50, as before.
        Case(check, "lane3b K-C2 (guard): the same pair at the origin - measured, crossing length 50", () =>
        {
            var r = MeshSurface.MeasurePlaced(SliverA, null, Zero3, Zero3, 1f, QuadB, null, Zero3, Zero3, 1f, VolumeGeometry.Render);
            return (r.Status == "measured" && r.CrossingLength is { } L && Math.Abs(L.Lower - 50) < 0.01, $"{r.Status} {r.Reason} L {r.CrossingLength}");
        });
        // K-C3: PairFrame picks member B's position for the FarOrigin pair and keeps the world frame (0,0,0) at the origin and for a pair
        // placed at FarOrigin whose triangles survive placement (two 10 u cubes). Rejects always-world and always-recentred.
        Case(check, "lane3b K-C3: PairFrame - B's position when placement collapses a sliver, (0,0,0) otherwise", () =>
        {
            var o1 = PairFrame.Origin(SliverA, FarOrigin, Zero3, 1f, QuadB, FarOrigin, Zero3, 1f);
            var o2 = PairFrame.Origin(SliverA, Zero3, Zero3, 1f, QuadB, Zero3, Zero3, 1f);
            var o3 = PairFrame.Origin(Cube(0, 0, 0, 5), FarOrigin, Zero3, 1f, Cube(20, 0, 0, 5), FarOrigin, Zero3, 1f);
            return (o1 == FarOrigin && o2 == Zero3 && o3 == Zero3, $"{o1} {o2} {o3}");
        });
        // K-C5: the world arm is byte for byte the old reading: two 10 u cubes 5 u apart at city-scale coordinates (nothing collapses)
        // serialise identically through MeasurePlaced and through Measure on TransformSoup (the pre-lane-3b code). Rejects recentring
        // every pair (the always-recentred arm changes the envelope's rounding).
        Case(check, "lane3b K-C5: a far pair that collapses nothing - MeasurePlaced equals the world-frame measure byte for byte", () =>
        {
            var o = new System.Text.Json.JsonSerializerOptions { NumberHandling = System.Text.Json.Serialization.JsonNumberHandling.AllowNamedFloatingPointLiterals };
            var far = new Vec3(-68447.3f, 104265.1f, -8496.4f);
            var ca = Cube(0, 0, 0, 5); var cb = Cube(7.3f, 1.1f, 0.4f, 5);
            var placed = MeshSurface.MeasurePlaced(ca, null, far, new Vec3(0, 0, 0.3f), 1f, cb, null, far, new Vec3(0, 0, 0.3f), 1f, VolumeGeometry.Render);
            var world = MeshSurface.Measure(SpatialOverlap.TransformSoup(ca, far, new Vec3(0, 0, 0.3f), 1f), null, SpatialOverlap.TransformSoup(cb, far, new Vec3(0, 0, 0.3f), 1f), null, VolumeGeometry.Render);
            string a = System.Text.Json.JsonSerializer.Serialize(placed, o), b = System.Text.Json.JsonSerializer.Serialize(world, o);
            return (a == b && placed.Status == "measured", a == b ? $"{placed.Status} L {placed.CrossingLength}" : "differs: " + a[..Math.Min(200, a.Length)] + " / " + b[..Math.Min(200, b.Length)]);
        });
        // K-C4 (guard): a triangle with a repeated index in model space is still refused (it is not a placement artefact).
        Case(check, "lane3b K-C4 (guard): a repeated-index triangle in model space - still refused", () =>
        {
            var bad = new TriangleSoup(new[] { new Vector3(0, 0, 0), new Vector3(1, 0, 0), new Vector3(0, 1, 0) }, new[] { 0, 1, 1 });
            var r = MeshSurface.MeasurePlaced(bad, null, FarOrigin, Zero3, 1f, QuadB, null, FarOrigin, Zero3, 1f, VolumeGeometry.Render);
            return (r.Status == "refused" && (r.Reason ?? "").Contains("degenerate"), $"{r.Status} {r.Reason}");
        });

        // ================= (d) a path partner as a tube around its curve =================
        // Path: LINEAR keys (0,0,0) -> (1000,0,0) -> (1000,1000,0); the bird's subtree radius 52.0769.
        var lPath = PathNifKeys(KeyType.LINEAR_KEY, (0, new(0, 0, 0), default, default), (0.5f, new(1000, 0, 0), default, default), (1, new(1000, 1000, 0), default, default));
        // K-D1: the tube holds the curve's points grown by the radius, and not the box corner (900,900,0), 100 u from the nearer leg.
        // Rejects the refusal and the hull box (which holds (900,900,0)).
        Case(check, "lane3b K-D1: L-shaped path tube - holds (500,0,40) and (1000,500,-40), not (900,900,0)", () =>
        {
            var t = Tube(lPath);
            if (t.Status != "built") return (false, $"{t.Status} {t.Reason}");
            bool inside = InTube(t, new(500, 0, 40)) && InTube(t, new(1000, 500, -40)) && InTube(t, new(0, 0, 52));
            return (inside && !InTube(t, new(900, 900, 0)) && t.Pieces.All(c => c.Radius >= BirdR - 1e-3), $"{t.Pieces.Count} pieces, radii {t.Pieces.Min(c => c.Radius):0.###}..{t.Pieces.Max(c => c.Radius):0.###}");
        });
        // K-D2: a plate at the box corner (x, y 880..920, z 0), 80 u from both legs: never. Rejects the refusal and a cannot_tell read off
        // the hull box.
        Case(check, "lane3b K-D2: a plate inside the hull box but 80 u from the path - never", () =>
        {
            var d = SweptGeometry.ContactTube(Tube(lPath), FlatQuad(880, 880, 920, 920, 0), null);
            return (d.Decision == "never", $"{d.Decision} {d.Reason}");
        });
        // K-D3: a plate across the first leg (x 490..510, y -20..20, z 0): cannot_tell, never a witness. Rejects never and witness.
        Case(check, "lane3b K-D3: a plate across the path - cannot_tell", () =>
        {
            var d = SweptGeometry.ContactTube(Tube(lPath), FlatQuad(490, -20, 510, 20, 0), null);
            return (d.Decision == "cannot_tell", $"{d.Decision} {d.Reason}");
        });
        // Quadratic keys (0,0,0) -> (1000,0,0) over 1 s, tangents (0,600,0) out of the first and (0,-600,0) into the second. Tangent as
        // stored: y(u) = 600 u (1-u), apex 150 at x 500; the other reading (times the 1 s duration) is the same curve, and the hull rule's
        // family of scalings k in [0, 1] sweeps every y in [0, 150 k] between the chord and that curve.
        var qPath = PathNifKeys(KeyType.QUADRATIC_KEY, (0, new(0, 0, 0), new(0, 600, 0), new(0, 600, 0)), (1, new(1000, 0, 0), new(0, -600, 0), new(0, -600, 0)));
        // K-D4: a plate at (500, 75), 75 u from both the chord and the apex curve (outside either curve's own tube): cannot_tell, because
        // the family of tangent scalings passes through it. Rejects a tube on one reading only (never).
        Case(check, "lane3b K-D4: quadratic path - a plate between the chord and the curve - cannot_tell", () =>
        {
            var d = SweptGeometry.ContactTube(Tube(qPath), FlatQuad(495, 70, 505, 80, 0), null);
            return (d.Decision == "cannot_tell", $"{d.Decision} {d.Reason}");
        });
        // K-D5: a plate at (500, 225..235): beyond the family's reach (apex 150 + radius 52.08 + the 8 u cover tolerance = 210.08) but
        // inside the control-polygon hull box grown by the radius (control points at y 200, + 52.08 = 252.08): never. Rejects the hull box.
        Case(check, "lane3b K-D5: quadratic path - a plate beyond the curve's reach, inside the hull box - never", () =>
        {
            var d = SweptGeometry.ContactTube(Tube(qPath), FlatQuad(495, 225, 505, 235, 0), null);
            return (d.Decision == "never", $"{d.Decision} {d.Reason}");
        });
        // K-D6: the tube placed at (1000, 2000, 0), turned pi/2 about z, scale 2: the radius doubles (104.15) and the placed image of the
        // first leg's (500,0,0) (by SpatialOverlap.TransformSoup, the production placement) lies in it. Rejects an unplaced tube.
        Case(check, "lane3b K-D6: placed tube - turned pi/2, scale 2: holds the placed image of (500,0,0), radius 104.15", () =>
        {
            var p = NifMotion.PlaceTube(Tube(lPath), new Vec3(1000, 2000, 0), new Vec3(0, 0, Pi / 2), 2f);
            if (p.Status != "built") return (false, $"{p.Status} {p.Reason}");
            var img = SpatialOverlap.TransformSoup(new TriangleSoup(new[] { new Vector3(500, 0, 0) }, Array.Empty<int>()), new Vec3(1000, 2000, 0), new Vec3(0, 0, Pi / 2), 2f).Vertices[0];
            return (InTube(p, img) && p.Pieces.All(c => c.Radius >= 2 * BirdR - 1e-2) && !InTube(p, new Vector3(500, 0, 0)), $"image {S(img)}, radius {p.Pieces.Min(c => c.Radius):0.##}");
        });

        // K-D7: a second path-driven node "Empty" with no shape under it, on a path through (500,0,0): no tube for it, so a plate on
        // that path but 300 u from the bird's L path reads never; and the bird's own tube is unchanged. Rejects a tube for a node that
        // moves nothing (the arm K-D3 holds the other branch: a node with a shape gets its tube).
        Case(check, "lane3b K-D7: a path node with no shape under it gets no tube - a plate on its path reads never", () =>
        {
            var f = Load(lPath); var root = f.GetRootNodes().First();
            var empty = new NiNode { Name = new NiStringRef("Empty"), Flags_ui = 0xE, Scale = 1f, Rotation = Identity33() };
            int ei = f.AddBlock(empty); root.Children.AddBlockRef(ei);
            var g = new KeyGroup<Vector3> { Interpolation = KeyType.LINEAR_KEY, Keys = new() { new() { Time = 0, Value = new(500, 300, -300) }, new() { Time = 1, Value = new(500, 300, 300) } }, NumKeys = 2 };
            var pi2 = new NiPathInterpolator { PathData = new(f.AddBlock(new NiPosData { Data = g })), PercentData = new(-1) };
            var c2 = new NiTransformController { Interpolator = new(f.AddBlock(pi2)), StartTime = 0, StopTime = 1, Frequency = 1 };
            c2.Flags.Active = true; c2.Target = new(ei); empty.Controller = new(f.AddBlock(c2));
            var nif = Save(f, "path with an empty path node");
            var t = Tube(nif);
            var d = SweptGeometry.ContactTube(t, FlatQuad(490, 290, 510, 310, 0), null);
            return (t.Status == "built" && d.Decision == "never" && t.PathNodes.SequenceEqual(new[] { "Bird" }), $"{t.Status} nodes {string.Join(",", t.PathNodes)} {d.Decision} {d.Reason}");
        });

        // ================= (b) the anchor against a moving partner; blends of a particle-only node =================
        // K-B1: an anchor in the door's swing (45 degrees, 60 u out): witness. The stored (closed) pose leaves it clear by 31.4 u.
        // Rejects the refusal and the stored-pose reading (clear).
        Case(check, "lane3b K-B1: anchor in a door's swing - witness (the closed pose reads clear)", () =>
        {
            var r = AnchorMotion.TestSwept(DoorRegion(), new Vector3(42.43f, 42.43f, 0), DoorBody);
            return (r.Decision == "witness" && r.Time is > 0 and < 1, $"{r.Decision} {r.Reason} t {r.Time}");
        });
        // K-B2: an anchor behind the hinge (-60, -60): never, the margin positive. Rejects cannot_tell by default.
        Case(check, "lane3b K-B2: anchor behind the hinge - never", () =>
        {
            var r = AnchorMotion.TestSwept(DoorRegion(), new Vector3(-60, -60, 0), DoorBody);
            return (r.Decision == "never" && r.Margin > 0, $"{r.Decision} {r.Reason} margin {r.Margin}");
        });
        // K-B3: an anchor 113 u out at 45 degrees (3 u clear of the tip's arc) with two samples only (budget 2): the samples are clear
        // but their displacement bound (about 157 u) is larger than the clearance - cannot_tell. Rejects never from the samples alone.
        Case(check, "lane3b K-B3: anchor near the arc, two samples - cannot_tell", () =>
        {
            var r = AnchorMotion.TestSwept(DoorRegion(new MotionOptions(DeltaTarget: 0.5, SampleBudget: 2)), new Vector3(79.9f, 79.9f, 0), DoorBody);
            return (r.Decision == "cannot_tell", $"{r.Decision} {r.Reason}");
        });
        // K-B4: the 1-DOF blend rule checks Lid (it moves a cube) and not SteamEmitter (it carries only a particle system). Rejects
        // checking every driven node.
        Case(check, "lane3b K-B4: blend check nodes - Lid, not the particle-only SteamEmitter", () =>
        {
            var nif = LidAndEmitterNif(meshToo: false);
            var m = NifMotion.Read(nif, null);
            var nodes = NifMotion.BlendCheckNodes(NifMotion.RigOf(nif, m, null), new[] { "Lid", "SteamEmitter" });
            return (nodes.SequenceEqual(new[] { "Lid" }), string.Join(",", nodes) + " / model " + m.Status + " " + m.Reason);
        });
        // K-B5 (guard): SteamEmitter that also carries a cube is checked.
        Case(check, "lane3b K-B5 (guard): blend check nodes - an emitter node that also carries a mesh is kept", () =>
        {
            var nif = LidAndEmitterNif(meshToo: true);
            var nodes = NifMotion.BlendCheckNodes(NifMotion.RigOf(nif, NifMotion.Read(nif, null), null), new[] { "Lid", "SteamEmitter" });
            return (nodes.OrderBy(x => x).SequenceEqual(new[] { "Lid", "SteamEmitter" }), string.Join(",", nodes));
        });

        // ================= (e) particle systems set aside; box against surface =================
        // K-E1: a cube plus a particle system "Sparks": the render surface is the cube's 12 triangles, labelled with the system set
        // aside. Rejects the refusal (render_partial) and an unlabelled exclusion.
        Case(check, "lane3b K-E1: cube + particle system - measured, 12 triangles, label names 'Sparks'", () =>
        {
            var t = MeshSurface.ModelSurface(ParticleNif(withCube: true), VolumeGeometry.Render);
            return (t.Status == "measured" && t.Soup?.TriangleCount == 12 && t.Labels is { } l && l.Any(x => x.Contains("'Sparks'")),
                    $"{t.Status} {t.Reason} tris {t.Soup?.TriangleCount} labels {string.Join("; ", t.Labels ?? Array.Empty<string>())}");
        });
        // K-E2: a mesh with nothing but a particle system: refused as having no geometry, not as render_partial. Rejects render_partial.
        Case(check, "lane3b K-E2: particle system only - refused, not render_partial", () =>
        {
            var t = MeshSurface.ModelSurface(ParticleNif(), VolumeGeometry.Render);
            return (t.Status == "refused" && !(t.Reason ?? "").StartsWith("render_partial"), $"{t.Status} {t.Reason}");
        });
        // K-E3: a box overlapping a 10 u opaque cube: OpaqueMeets and AnyMeets. Rejects false.
        Case(check, "lane3b K-E3: box meets an opaque cube - opaque and any", () =>
        {
            var r = MeshSurfaceBox.BoxContact(Box(0, 0, 0, 10, 10, 10), null, new Aabb(new Vec3(5, 5, 5), new Vec3(20, 20, 20), false));
            return (r.OpaqueMeets && r.AnyMeets && r.Reason is null, $"{r}");
        });
        // K-E4: a box inside the cube's bounds but touching no face (2..8): neither (the surface, not the volume, is tested). Rejects
        // a bounds test.
        Case(check, "lane3b K-E4: box inside the cube, touching no face - neither", () =>
        {
            var r = MeshSurfaceBox.BoxContact(Box(0, 0, 0, 10, 10, 10), null, new Aabb(new Vec3(2, 2, 2), new Vec3(8, 8, 8), false));
            return (!r.OpaqueMeets && !r.AnyMeets && r.Reason is null, $"{r}");
        });
        // K-E5: the same overlap when every triangle is alpha_blend: AnyMeets only. Rejects counting non-opaque triangles as opaque.
        Case(check, "lane3b K-E5: box meets an alpha-blend cube - any, not opaque", () =>
        {
            var tags = Enumerable.Repeat(SurfaceMaterial.AlphaBlend, 12).ToArray();
            var r = MeshSurfaceBox.BoxContact(Box(0, 0, 0, 10, 10, 10), tags, new Aabb(new Vec3(5, 5, 5), new Vec3(20, 20, 20), false));
            return (!r.OpaqueMeets && r.AnyMeets && r.Reason is null, $"{r}");
        });
    }
}
