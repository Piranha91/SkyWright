using System.Numerics;
using HousecarlCore;
using NiflySharp;
using NiflySharp.Blocks;
using NiflySharp.Enums;
using NiflySharp.Structs;

/// <summary>Known answers for animated-object motion and swept regions (task 2026-10-01-animated-object-swept-volume,
/// decision record D1-D10, Gate A review F1-F5). Synthetic NIF fixtures are built here with NiflySharp; expected values
/// are analytic (stated beside each case). Each case names the wrong answer it rejects. Cases marked "fixture control"
/// check the fixture against code that already exists and hold on the base; every other case is red until the motion
/// code is implemented. The synthetic tier-1 key cases (MK3*) encode the conventions the decision record froze; the
/// real-data cases that test those conventions independently run through motionprobe (run folder, motion_kat.py).</summary>
static partial class MotionProof
{
    static void Case(Action<string, bool, string> check, string name, Func<(bool Ok, string Detail)> body)
    {
        (bool ok, string detail) r;
        try { r = body(); }
        catch (Exception ex) { r = (false, ex.GetType().Name + ": " + ex.Message); }
        check(name, r.ok, r.detail);
    }
    static readonly NiVersion Ver = new() { FileVersion = NiVersion.ToFile("20.2.0.7"), UserVersion = 12, StreamVersion = 100 };
    static readonly NiVersion VerLE = new() { FileVersion = NiVersion.ToFile("20.2.0.7"), UserVersion = 12, StreamVersion = 83 };
    const float Pi = MathF.PI;

    // ---------------- geometry helpers ----------------
    static TriangleSoup Box(float x0, float y0, float z0, float x1, float y1, float z1)
    {
        var v = new[] { new Vector3(x0, y0, z0), new Vector3(x1, y0, z0), new Vector3(x1, y1, z0), new Vector3(x0, y1, z0),
                        new Vector3(x0, y0, z1), new Vector3(x1, y0, z1), new Vector3(x1, y1, z1), new Vector3(x0, y1, z1) };
        var i = new[] { 0,2,1, 0,3,2, 4,5,6, 4,6,7, 0,1,5, 0,5,4, 1,2,6, 1,6,5, 2,3,7, 2,7,6, 3,0,4, 3,4,7 };
        return new TriangleSoup(v, i);
    }
    static TriangleSoup Cube(float cx, float cy, float cz, float half) => Box(cx - half, cy - half, cz - half, cx + half, cy + half, cz + half);
    static BSTriShape Shape(string name, TriangleSoup s)
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
        if (f.Save(ms) != 0) throw new InvalidOperationException("motion fixture failed to save: " + what);
        return ms.ToArray();
    }
    static NifFile Load(byte[] b) { var f = new NifFile(); using var ms = new MemoryStream(b); f.Load(ms); return f; }
    static Matrix33 Identity33() => new() { M11 = 1, M22 = 1, M33 = 1 };
    /// <summary>A true quaternion in the field order NiflySharp holds a NIF key in (file w, x, y, z in X, Y, Z, W).</summary>
    static Quaternion Raw(Quaternion q) => new(q.W, q.X, q.Y, q.Z);

    // ---------------- key helpers ----------------
    static Key<float> FK(float t, float v, float fw = 0, float bw = 0) => new() { Time = t, Value = v, Forward = fw, Backward = bw };
    static KeyGroup<float> FG(KeyType type, params Key<float>[] keys) => new() { Interpolation = type, Keys = keys.ToList(), NumKeys = (uint)keys.Length };
    static KeyGroup<float> NoKeys() => new() { Interpolation = KeyType.LINEAR_KEY, Keys = new(), NumKeys = 0 };
    static KeyGroup<Vector3> VG(KeyType type, params (float T, Vector3 V)[] keys) =>
        new() { Interpolation = type, Keys = keys.Select(k => new Key<Vector3> { Time = k.T, Value = k.V }).ToList(), NumKeys = (uint)keys.Length };
    static KeyGroup<Vector3> NoVKeys() => new() { Interpolation = KeyType.LINEAR_KEY, Keys = new(), NumKeys = 0 };

    /// <summary>XYZ-rotation keyframe data (x, y, z groups), optional translations.</summary>
    static NiTransformData Xyz(KeyGroup<float> x, KeyGroup<float> y, KeyGroup<float> z, KeyGroup<Vector3>? tr = null) => new()
    {
        RotationType = KeyType.XYZ_ROTATION_KEY, NumRotationKeys = 1, XYZRotations = new[] { x, y, z },
        Translations = tr ?? NoVKeys(), Scales = NoKeys(),
    };

    /// <summary>A rig: root -> node "Mover" (local translation <paramref name="pivot"/>) -> shape "Part" (the given soup, in
    /// the mover's frame); plus a static shape "Frame". One CLAMP/LOOP sequence "Open" (0..stop) drives "Mover" with
    /// <paramref name="data"/>, or a direct NiTransformController when <paramref name="direct"/> is set.</summary>
    static byte[] Rig(TriangleSoup part, Vector3 pivot, NiTransformData? data, float stop = 1f, CycleType cycle = CycleType.CYCLE_CLAMP,
                      bool direct = false, bool directActive = true, string sequence = "Open", Action<NifFile, NiNode>? extra = null,
                      NiTransformData? second = null, string secondSequence = "Close")
    {
        var f = new NifFile(); f.Create(Ver, withRootNode: true);
        var root = f.GetRootNodes().First(); root.Flags_ui = 0xE;
        var mover = new NiNode { Name = new NiStringRef("Mover"), Flags_ui = 0xE, Scale = 1f, Translation = pivot, Rotation = Identity33() };
        root.Children.AddBlockRef(f.AddBlock(mover));
        mover.Children.AddBlockRef(f.AddBlock(Shape("Part", part)));
        root.Children.AddBlockRef(f.AddBlock(Shape("Frame", Box(-200, -200, -40, -180, -180, -20))));
        if (data is not null)
        {
            int dataIndex = f.AddBlock(data);
            var interp = new NiTransformInterpolator { Data = new(dataIndex) };
            int interpIndex = f.AddBlock(interp);
            if (direct)
            {
                var ctl = new NiTransformController { Interpolator = new(interpIndex), StartTime = 0, StopTime = stop, Frequency = 1 };
                ctl.Flags.Active = directActive;
                ctl.Flags.CycleType = cycle;
                int ci = f.AddBlock(ctl);
                ctl.Target = new(f.Blocks.IndexOf(mover));
                mover.Controller = new(ci);
            }
            else
            {
                var mtc = new NiMultiTargetTransformController();
                int mtcIndex = f.AddBlock(mtc);
                var mgr = new NiControllerManager();
                int mgrIndex = f.AddBlock(mgr);
                mgr.ControllerSequences ??= new();
                void AddSeq(string name, int interpIdx)
                {
                    var seq = new NiControllerSequence { Name = new NiStringRef(name), CycleType = cycle, StartTime = 0, StopTime = stop, Frequency = 1, Weight = 1 };
                    seq.ControlledBlocks = new List<ControlledBlock>
                    {
                        new() { NodeName = new NiStringRef("Mover"), ControllerType = new NiStringRef("NiTransformController"),
                                Interpolator = new(interpIdx), Controller = new(mtcIndex) }
                    };
                    seq.NumControlledBlocks = 1;
                    mgr.ControllerSequences.AddBlockRef(f.AddBlock(seq));
                }
                AddSeq(sequence, interpIndex);
                if (second is not null)
                {
                    int d2 = f.AddBlock(second);
                    AddSeq(secondSequence, f.AddBlock(new NiTransformInterpolator { Data = new(d2) }));
                }
                mgr.NumControllerSequences = (uint)mgr.ControllerSequences.Count;
                mgr.NextController = new(mtcIndex);
                root.Controller = new(mgrIndex);
            }
        }
        extra?.Invoke(f, root);
        return Save(f, "rig");
    }

    /// <summary>A two-bone skinned strip: BoneA at the origin, BoneB child of BoneA at (20,0,0); vertices along x at 0..40
    /// (a thin quad strip), weights: x &lt;= 10 -> A; x = 30 -> A 0.5 / B 0.5; x = 40 -> B. Sequence "Open" turns BoneB
    /// by <paramref name="angleB"/> about Z (LINEAR) over 1 s.</summary>
    static byte[] SkinRig(float angleB, Action<NifFile, NiNode>? extra = null, bool partitioned = false, bool ssePermuted = false)
    {
        var f = new NifFile(); f.Create(partitioned ? VerLE : Ver, withRootNode: true);
        var root = f.GetRootNodes().First(); root.Flags_ui = 0xE;
        var a = new NiNode { Name = new NiStringRef("BoneA"), Flags_ui = 0xE, Scale = 1f, Rotation = Identity33() };
        var b = new NiNode { Name = new NiStringRef("BoneB"), Flags_ui = 0xE, Scale = 1f, Rotation = Identity33(), Translation = new(20, 0, 0) };
        int ai = f.AddBlock(a); root.Children.AddBlockRef(ai);
        int bi = f.AddBlock(b); a.Children.AddBlockRef(bi);
        var xs = new[] { 0f, 10f, 30f, 40f };
        var verts = new List<Vector3>();
        foreach (var x in xs) { verts.Add(new(x, -1, 0)); verts.Add(new(x, 1, 0)); }
        var tris = new List<Triangle>();
        for (int k = 0; k < xs.Length - 1; k++) { int p = 2 * k; tris.Add(new((ushort)p, (ushort)(p + 2), (ushort)(p + 1))); tris.Add(new((ushort)(p + 1), (ushort)(p + 2), (ushort)(p + 3))); }
        // a pre-SSE NiTriShape with NiTriShapeData: its triangles live in the data block, so a skin instance without a
        // partition keeps them (an SSE BSTriShape marked skinned stores its geometry in a skin partition this fixture omits)
        INiShape shape;
        BSTriShape? sseShape = null;
        if (ssePermuted)
        {
            // an SSE BSTriShape: its skin partition (built by NiflySharp's UpdateSkinPartitions below) carries the geometry
            sseShape = new BSTriShape { Name = new NiStringRef("Strip"), Flags_ui = 0xE, Scale = 1f };
            sseShape.Create(Ver, verts, tris, new(), new());
            shape = sseShape;
        }
        else
        {
            var geom = new NiTriShapeData { Vertices = verts, HasVertices = true, Triangles = tris };
            var tri = new NiTriShape { Name = new NiStringRef("Strip"), Flags_ui = 0xE, Scale = 1f };
            tri.DataRef = new(f.AddBlock(geom));
            shape = tri;
        }
        int si = f.AddBlock(shape); root.Children.AddBlockRef(si);
        var data = new NiSkinData
        {
            SkinTransform = new NiTransform { Rotation = Identity33(), Scale = 1, Translation = Vector3.Zero },
            HasVertexWeights = !partitioned, NumBones = 2,
            BoneList = new List<BoneData>
            {
                new() { SkinTransform = new NiTransform { Rotation = Identity33(), Scale = 1, Translation = Vector3.Zero },
                        VertexWeights = new List<BoneVertData> { new() { Index = 0, Weight = 1 }, new() { Index = 1, Weight = 1 }, new() { Index = 2, Weight = 1 }, new() { Index = 3, Weight = 1 },
                                                                 new() { Index = 4, Weight = 0.5f }, new() { Index = 5, Weight = 0.5f } }, NumVertices = 6 },
                new() { SkinTransform = new NiTransform { Rotation = Identity33(), Scale = 1, Translation = new(-20, 0, 0) },
                        VertexWeights = new List<BoneVertData> { new() { Index = 4, Weight = 0.5f }, new() { Index = 5, Weight = 0.5f }, new() { Index = 6, Weight = 1 }, new() { Index = 7, Weight = 1 } }, NumVertices = 4 },
            },
        };
        int di = f.AddBlock(data);
        var inst = new NiSkinInstance { Data = new(di), SkeletonRoot = new(f.Blocks.IndexOf(root)), NumBones = 2 };
        inst.Bones ??= new();
        inst.Bones.AddBlockRef(ai); inst.Bones.AddBlockRef(bi);
        int ii = f.AddBlock(inst);
        // the skin instance alone marks the shape skinned for the reader; setting IsSkinned as well would move SSE geometry
        // into a skin partition this fixture does not build, and the save would drop the triangles
        if (shape is BSTriShape bt) bt.SkinInstanceRef = new(ii); else ((NiTriShape)shape).SkinInstanceRef = new(ii);
        if (sseShape is not null)
        {
            // NiflySharp partitions the skin from the skin data (identity palette), then the palette is permuted to
            // [BoneB, BoneA] and every vertex's partition-local indices written to match: the skin is unchanged, only its
            // encoding. The skin data keeps its weights, so a wrong palette reading disagrees with it.
            // one seed partition holding every triangle; UpdateSkinPartitions assigns triangles from the existing partitions
            var seed = new SkinPartition
            {
                NumTriangles = (ushort)tris.Count, Triangles = tris.ToList(), TrianglesCopy = tris.ToList(), HasFaces = true, Bones = new(), VertexMap = new(),
                VertexWeights = new(), BoneIndices = new(), Strips = new(), StripLengths = new(),
            };
            inst.SkinPartition = new(f.AddBlock(new NiSkinPartition { NumPartitions = 1, Partitions = new List<SkinPartition> { seed }, VertexData = new List<BSVertexDataSSE>() }));
            sseShape.IsSkinned = true;
            f.UpdateSkinPartitions(sseShape);
            var sp = f.GetBlock<NiSkinPartition>(inst.SkinPartition);
            var p0 = sp.Partitions[0];
            if (p0.Bones.Count != 2 || p0.Bones[0] != 0 || p0.Bones[1] != 1) throw new InvalidOperationException("SSE fixture: unexpected palette " + string.Join(",", p0.Bones));
            p0.Bones = new List<ushort> { 1, 0 };
            sp.Partitions[0] = p0;
            // the SSE vertex record carries up to four (partition-local index, weight) pairs; NiflySharp's partitioning
            // leaves them empty here, so they are written from the skin data through the permuted palette (local = 1 - bone)
            var perVertex = Enumerable.Range(0, verts.Count).Select(_ => new List<(byte, float)>()).ToArray();
            for (int bone = 0; bone < data.BoneList.Count; bone++)
                foreach (var vw in data.BoneList[bone].VertexWeights) perVertex[vw.Index].Add(((byte)(1 - bone), vw.Weight));
            void Fill(List<BSVertexDataSSE>? list)
            {
                if (list is null) return;
                for (int v = 0; v < list.Count; v++)
                {
                    var vd = list[v];
                    for (int k = 0; k < 4; k++) { vd.BoneIndices[k] = k < perVertex[v].Count ? perVertex[v][k].Item1 : (byte)0; vd.BoneWeights[k] = (Half)(k < perVertex[v].Count ? perVertex[v][k].Item2 : 0f); }
                    list[v] = vd;
                }
            }
            Fill(sseShape.VertexDataSSE);
            Fill(sp.VertexData);
        }
        if (partitioned)
        {
            // a pre-SSE (LE) skin: the skin data carries no vertex weights, two partitions carry them, each with the
            // PERMUTED palette [BoneB, BoneA] (local 0 -> skin bone 1, local 1 -> skin bone 0). Partition 0: vertices 0-3
            // (x 0, 10) on local 1 = BoneA. Partition 1: vertices 4-5 (x 30) local 0 / local 1 at 0.5 each, 6-7 (x 40) local 0.
            for (int k = 0; k < 2; k++) { var bd = data.BoneList[k]; bd.VertexWeights = new(); bd.NumVertices = 0; data.BoneList[k] = bd; }
            SkinPartition Part(ushort[] map, float[] w, byte[] bi, List<Triangle> t) => new()
            {
                NumVertices = (ushort)map.Length, NumTriangles = (ushort)t.Count, NumBones = 2, NumStrips = 0, NumWeightsPerVertex = 2,
                Bones = new List<ushort> { 1, 0 }, HasVertexMap = true, VertexMap = map.ToList(), HasVertexWeights = true, VertexWeights = w.ToList(),
                StripLengths = new(), HasFaces = true, Strips = new(), Triangles = t, HasBoneIndices = true, BoneIndices = bi.ToList(),
            };
            var p0 = Part(new ushort[] { 0, 1, 2, 3 }, new[] { 1f, 0, 1, 0, 1, 0, 1, 0 }, new byte[] { 1, 0, 1, 0, 1, 0, 1, 0 },
                          new List<Triangle> { new(0, 2, 1), new(1, 2, 3) });
            var p1 = Part(new ushort[] { 2, 3, 4, 5, 6, 7 }, new[] { 1f, 0, 1, 0, 0.5f, 0.5f, 0.5f, 0.5f, 1, 0, 1, 0 },
                          new byte[] { 1, 0, 1, 0, 0, 1, 0, 1, 0, 1, 0, 1 }, new List<Triangle> { new(0, 2, 1), new(1, 2, 3), new(2, 4, 3), new(3, 4, 5) });
            var part = new NiSkinPartition { NumPartitions = 2, Partitions = new List<SkinPartition> { p0, p1 } };
            inst.SkinPartition = new(f.AddBlock(part));
        }
        // sequence on BoneB
        var kd = Xyz(FG(KeyType.LINEAR_KEY, FK(0, 0)), FG(KeyType.LINEAR_KEY, FK(0, 0)), FG(KeyType.LINEAR_KEY, FK(0, 0), FK(1, angleB)));
        int kdi = f.AddBlock(kd);
        int interp = f.AddBlock(new NiTransformInterpolator { Data = new(kdi) });
        int mtc = f.AddBlock(new NiMultiTargetTransformController());
        var seq = new NiControllerSequence { Name = new NiStringRef("Open"), CycleType = CycleType.CYCLE_CLAMP, StartTime = 0, StopTime = 1, Frequency = 1, Weight = 1 };
        seq.ControlledBlocks = new List<ControlledBlock> { new() { NodeName = new NiStringRef("BoneB"), ControllerType = new NiStringRef("NiTransformController"), Interpolator = new(interp), Controller = new(mtc) } };
        seq.NumControlledBlocks = 1;
        var mgr = new NiControllerManager(); int mgri = f.AddBlock(mgr);
        mgr.ControllerSequences ??= new(); mgr.ControllerSequences.AddBlockRef(f.AddBlock(seq)); mgr.NumControllerSequences = 1;
        mgr.NextController = new(mtc);
        root.Controller = new(mgri);
        extra?.Invoke(f, root);
        return Save(f, "skin rig");
    }

    /// <summary>A rigid body with a box shape and one bhkLimitedHingeConstraint, as the collision object of the root
    /// (an unreferenced constraint block is dropped on save).</summary>
    static void AddConstraint(NifFile f, NiNode root)
    {
        int shape = f.AddBlock(new bhkBoxShape());
        int cons = f.AddBlock(new bhkLimitedHingeConstraint());
        var body = new bhkRigidBody { Shape = new(shape) };
        body.Constraints ??= new();
        body.Constraints.AddBlockRef(cons); body.NumConstraints = 1;
        int bi = f.AddBlock(body);
        int co = f.AddBlock(new bhkCollisionObject { Body = new(bi), Target = new(f.Blocks.IndexOf(root)) });
        root.CollisionObject = new(co);
    }

    static Vector3 RotZ(Vector3 p, Vector3 pivot, float a)
    {
        var d = p - pivot; float c = MathF.Cos(a), s = MathF.Sin(a);
        return pivot + new Vector3(c * d.X - s * d.Y, s * d.X + c * d.Y, d.Z);
    }
    static float AngleZ(Matrix4x4 rows) => MathF.Atan2(rows.M12, rows.M11);   // rows = Rz^T: first row (cos, sin, 0)
    static bool Near(double a, double b, double tol) => Math.Abs(a - b) <= tol;
    static bool NearV(Vector3 a, Vector3 b, float tol) => Vector3.Distance(a, b) <= tol;
    static string S(Vector3 v) => $"({v.X:0.###},{v.Y:0.###},{v.Z:0.###})";

    // Column-form rotation matrices written out explicitly (an oracle independent of NifMotionKeys).
    static double[,] Cx(double a) => new double[,] { { 1, 0, 0 }, { 0, Math.Cos(a), -Math.Sin(a) }, { 0, Math.Sin(a), Math.Cos(a) } };
    static double[,] Cy(double a) => new double[,] { { Math.Cos(a), 0, Math.Sin(a) }, { 0, 1, 0 }, { -Math.Sin(a), 0, Math.Cos(a) } };
    static double[,] Cz(double a) => new double[,] { { Math.Cos(a), -Math.Sin(a), 0 }, { Math.Sin(a), Math.Cos(a), 0 }, { 0, 0, 1 } };
    static double[,] Mul(double[,] a, double[,] b)
    {
        var r = new double[3, 3];
        for (int i = 0; i < 3; i++) for (int j = 0; j < 3; j++) for (int k = 0; k < 3; k++) r[i, j] += a[i, k] * b[k, j];
        return r;
    }
    static bool RowsEqualTransposeOf(Matrix4x4 rows, double[,] col, double tol)
    {
        float[,] m = { { rows.M11, rows.M12, rows.M13 }, { rows.M21, rows.M22, rows.M23 }, { rows.M31, rows.M32, rows.M33 } };
        for (int i = 0; i < 3; i++) for (int j = 0; j < 3; j++) if (Math.Abs(m[i, j] - col[j, i]) > tol) return false;
        return true;
    }

    // Analytic swept volume of a box r in [r1, r2], y in [-w, 0], z in [0, h] turning about Z by theta (PLAN §8 K1):
    // the angular set at each radius is one interval, so the union adds theta at every radius of the support.
    // Every Rig carries a static 20 u cube "Frame" (Box(-200,-200,-40,-180,-180,-20)); a whole-mesh volume includes it.
    const double FrameVolume = 20 * 20 * 20;

    static double PlateSweep(double r1, double r2, double w, double h, double theta) =>
        h * (w * (r2 - r1) + theta / 2 * (r2 * r2 + w * w - r1 * r1));

    public static void Run(Action<string, bool, string> check)
    {
        // ================= fixture controls (existing code; hold on the base) =================
        var leaf0 = Box(0, -2, 0, 40, 2, 80);
        var pivot0 = new Vector3(5, 0, 0);
        var plateSoup = Box(10, -4, 0, 60, 0, 50);
        byte[] plate = Rig(plateSoup, Vector3.Zero, Xyz(FG(KeyType.LINEAR_KEY, FK(0, 0)), FG(KeyType.LINEAR_KEY, FK(0, 0)), FG(KeyType.LINEAR_KEY, FK(0, 0), FK(1, Pi / 2))));
        Case(check, "motion fixture control: the rotating-plate rig round-trips its Z keys (LINEAR 0 -> pi/2) on node Mover", () =>
        {
            var f = Load(plate);
            var seq = f.Blocks.OfType<NiControllerSequence>().Single();
            var kd = f.Blocks.OfType<NiTransformData>().Single();
            var z = kd.XYZRotations[2] is KeyGroup<float> g ? g : default;
            return (seq.ControlledBlocks.Single().NodeName?.ToString() == "Mover" && kd.RotationType == KeyType.XYZ_ROTATION_KEY && z.Keys.Count == 2
                    && Near(z.Keys[1].Value, Pi / 2, 1e-6) && z.Interpolation == KeyType.LINEAR_KEY, $"{seq.ControlledBlocks.Single().NodeName} {kd.RotationType} z {z.Interpolation} n {z.Keys?.Count}");
        });
        byte[] skin = SkinRig(Pi / 2);
        Case(check, "motion fixture control: the skin rig round-trips two bones, BoneB under BoneA, and 10 weights", () =>
        {
            var f = Load(skin);
            var inst = f.Blocks.OfType<NiSkinInstance>().Single();
            var data = f.Blocks.OfType<NiSkinData>().Single();
            var shape = f.GetShapes().OfType<NiTriShape>().Single();
            var bnames = inst.Bones.References.Select(r => (f.Blocks[r.Index] as NiAVObject)?.Name?.String).ToList();
            var parentB = f.GetParentNode((NiAVObject)f.Blocks[inst.Bones.References.Last().Index])?.Name?.String;
            return (bnames.SequenceEqual(new[] { "BoneA", "BoneB" }) && parentB == "BoneA" && data.BoneList.Sum(b => b.VertexWeights.Count) == 10
                    && shape.HasSkinInstance && shape.SkinInstanceRef.Index >= 0 && shape.Triangles?.Count == 6, $"bones {string.Join(",", bnames)} parentB {parentB} weights {data.BoneList.Sum(b => b.VertexWeights.Count)} skin instance {shape.HasSkinInstance} triangles {shape.Triangles?.Count}");
        });
        Case(check, "motion fixture control: analytic plate sweep (r 10..60, w 4, h 50, theta pi/2) = 50*(200 + pi/4*(3600+16-100))", () =>
        {
            double v = PlateSweep(10, 60, 4, 50, Math.PI / 2);
            return (Near(v, 50 * (200 + Math.PI / 4 * 3516), 1e-9), $"{v:0.###}");
        });

        Case(check, "motion fixture control: the refusal fixtures carry what they claim (constraint block, cloth data, TBC keys, inactive and active LOOP direct controllers, graph extra data, particle system)", () =>
        {
            var z1 = Xyz(NoKeys(), NoKeys(), FG(KeyType.LINEAR_KEY, FK(0, 0), FK(1, 1)));
            var cons = Load(Rig(leaf0, pivot0, z1, extra: AddConstraint));
            var cloth = Load(Rig(leaf0, pivot0, null, extra: (f, root) => { root.ExtraDataList ??= new(); root.ExtraDataList.AddBlockRef(f.AddBlock(new BSClothExtraData())); root.NumExtraDataList = 1; }));
            var tbc = Load(Rig(leaf0, pivot0, Xyz(NoKeys(), NoKeys(), FG(KeyType.TBC_KEY, FK(0, 0), FK(1, 1)))));
            var off = Load(Rig(leaf0, pivot0, z1, direct: true, directActive: false));
            var on = Load(Rig(leaf0, pivot0, z1, direct: true, cycle: CycleType.CYCLE_LOOP));
            var part = Load(Rig(leaf0, pivot0, null, extra: (f, root) => root.Children.AddBlockRef(f.AddBlock(new NiParticleSystem { Name = new NiStringRef("Sparks"), Flags_ui = 0xE, Scale = 1f }))));
            var tz = tbc.Blocks.OfType<NiTransformData>().Single().XYZRotations[2];
            var offc = off.Blocks.OfType<NiTransformController>().Single();
            var onc = on.Blocks.OfType<NiTransformController>().Single();
            bool ok = cons.Blocks.OfType<bhkLimitedHingeConstraint>().Count() == 1 && cloth.Blocks.OfType<BSClothExtraData>().Count() == 1
                      && tz.Interpolation == KeyType.TBC_KEY && !offc.Flags.Active && onc.Flags.Active && onc.Flags.CycleType == CycleType.CYCLE_LOOP
                      && part.Blocks.OfType<NiParticleSystem>().Count() == 1;
            return (ok, $"constraint {cons.Blocks.OfType<bhkLimitedHingeConstraint>().Count()} cloth {cloth.Blocks.OfType<BSClothExtraData>().Count()} tbc {tz.Interpolation} off.active {offc.Flags.Active} on {onc.Flags.Active}/{onc.Flags.CycleType} particles {part.Blocks.OfType<NiParticleSystem>().Count()}");
        });

        // ================= MK1: rotating plate, swept volume against the analytic annulus sector =================
        Case(check, "MK1 rotating plate: swept volume interval contains 50*(4*50 + (pi/4)*(60^2+4^2-10^2)) + the static 20 u frame cube, and is certified", () =>
        {
            var m = NifMotion.Read(plate, null);
            var r = SweptGeometry.Build(plate, m, new[] { "seq:Open" }, VolumeGeometry.Render, new MotionOptions(DeltaTarget: 0.5));
            var v = SweptGeometry.Volume(r, null);
            double want = PlateSweep(10, 60, 4, 50, Math.PI / 2) + FrameVolume;
            return (v.Swept is { } s && s.Lower <= want && want <= s.Upper && r.Converged && s.Upper - s.Lower <= 0.15 * want,
                    $"want {want:0.#} got [{v.Swept?.Lower:0.#}, {v.Swept?.Upper:0.#}] converged {r.Converged} maxDelta {r.MaxDelta:0.###}");
        });
        Case(check, "MK1 rotating plate: halving delta narrows the interval and keeps the analytic value inside (rejects delta = 0)", () =>
        {
            var m = NifMotion.Read(plate, null);
            var coarse = SweptGeometry.Volume(SweptGeometry.Build(plate, m, null, VolumeGeometry.Render, new MotionOptions(DeltaTarget: 2.0)), null).Swept!;
            var fine = SweptGeometry.Volume(SweptGeometry.Build(plate, m, null, VolumeGeometry.Render, new MotionOptions(DeltaTarget: 1.0)), null).Swept!;
            double want = PlateSweep(10, 60, 4, 50, Math.PI / 2) + FrameVolume;
            return (fine.Width < coarse.Width && fine.Lower <= want && want <= fine.Upper && coarse.Lower <= want && want <= coarse.Upper,
                    $"coarse [{coarse.Lower:0.#},{coarse.Upper:0.#}] fine [{fine.Lower:0.#},{fine.Upper:0.#}] want {want:0.#}");
        });
        Case(check, "MK1 rotating plate: rotation sense — a cube at +45 deg (r 35) is witnessed, its mirror at -45 deg is never", () =>
        {
            var m = NifMotion.Read(plate, null);
            var r = SweptGeometry.Build(plate, m, null, VolumeGeometry.Render, new MotionOptions(DeltaTarget: 0.5));
            float c = 35 * MathF.Cos(Pi / 4);
            var up = SweptGeometry.Contact(r, Cube(c, c, 25, 3), null, partnerIsSolid: true);
            var down = SweptGeometry.Contact(r, Cube(c, -c, 25, 3), null, partnerIsSolid: true);
            return (up.Decision == "witness" && down.Decision == "never", $"+45 {up.Decision} t={up.Time}; -45 {down.Decision} {down.Reason}");
        });

        // ================= MK2: translating box, the prism =================
        byte[] slide = Rig(Box(0, 0, 0, 20, 20, 20), Vector3.Zero,
                           Xyz(NoKeys(), NoKeys(), NoKeys(), VG(KeyType.LINEAR_KEY, (0f, Vector3.Zero), (1f, new Vector3(0, 0, 100)))));
        Case(check, "MK2 translating box: swept volume interval contains the prism 20*20*(20+100) = 48000 + the static 8000 frame cube (width <= 15 % of the prism)", () =>
        {
            var m = NifMotion.Read(slide, null);
            var v = SweptGeometry.Volume(SweptGeometry.Build(slide, m, null, VolumeGeometry.Render, new MotionOptions(DeltaTarget: 0.5)), null);
            return (v.Swept is { } s && s.Lower <= 48000 + FrameVolume && 48000 + FrameVolume <= s.Upper && s.Width <= 0.15 * 48000, $"[{v.Swept?.Lower:0.#}, {v.Swept?.Upper:0.#}]");
        });
        Case(check, "MK2 translating box: the end pose is the box lifted 100 u exactly (rejects a dropped last key)", () =>
        {
            var m = NifMotion.Read(slide, null);
            var p = NifMotion.Pose(slide, m, "seq:Open", 1f, VolumeGeometry.Render);
            var part = p.Soup!.Vertices.Where(v => v.X > -100).ToArray();
            float zmin = part.Min(v => v.Z), zmax = part.Max(v => v.Z);
            return (p.Status == "posed" && Near(zmin, 100, 1e-4) && Near(zmax, 120, 1e-4), $"{p.Status} z [{zmin}, {zmax}]");
        });
        Case(check, "MK2 translating box: a cube above the travel (z 125..131) is never; one at z 60 is a witness", () =>
        {
            var m = NifMotion.Read(slide, null);
            var r = SweptGeometry.Build(slide, m, null, VolumeGeometry.Render);
            var above = SweptGeometry.Contact(r, Cube(10, 10, 128, 3), null, true);
            var inside = SweptGeometry.Contact(r, Cube(10, 10, 60, 3), null, true);
            return (above.Decision == "never" && inside.Decision == "witness", $"above {above.Decision} inside {inside.Decision} t={inside.Time}");
        });

        // ================= MK3: tier-1 key conventions on synthetic keys (encode D2; the real-data tests are M-R4/M-R1) =================
        var leaf = Box(0, -2, 0, 40, 2, 80);
        var pivot = new Vector3(5, 0, 0);
        byte[] door = Rig(leaf, pivot, Xyz(FG(KeyType.LINEAR_KEY, FK(0, 0)), FG(KeyType.LINEAR_KEY, FK(0, 0)), FG(KeyType.QUADRATIC_KEY, FK(0, 0), FK(1, -Pi / 2))),
                          second: Xyz(FG(KeyType.LINEAR_KEY, FK(0, 0)), FG(KeyType.LINEAR_KEY, FK(0, 0)), FG(KeyType.QUADRATIC_KEY, FK(0, -Pi / 2), FK(1, 0))));
        Case(check, "MK3a CLAMP end pose: Open@end turns the leaf -90 deg about Z at the pivot (rejects a transposed rotation: +90)", () =>
        {
            var m = NifMotion.Read(door, null);
            var p = NifMotion.Pose(door, m, "seq:Open", 1f, VolumeGeometry.Render);
            var want = RotZ(pivot + new Vector3(40, 2, 80), pivot, -Pi / 2);
            bool has = p.Soup!.Vertices.Any(v => NearV(v, want, 1e-3f));
            return (p.Status == "posed" && has, $"{p.Status} want {S(want)} among {p.Soup.Vertices.Length}");
        });
        Case(check, "MK3b named poses: closed = Open@start (stored), open = Open@end, rest = closed without IsOpenByDefault", () =>
        {
            var m = NifMotion.Read(door, null, new MotionPlacement("DOOR", false));
            var rest = m.Poses.SingleOrDefault(p => p.Rest);
            bool names = m.Poses.Any(p => p.Name == "closed" && p.AnimationId == "seq:Open" && p.Time == 0)
                         && m.Poses.Any(p => p.Name == "open" && p.AnimationId == "seq:Open" && p.Time == 1);
            return (names && rest?.Name == "closed", $"{string.Join(", ", m.Poses.Select(p => $"{p.Name}={p.AnimationId}@{p.Time}{(p.Rest ? "*" : "")}"))}");
        });
        Case(check, "MK3c IsOpenByDefault: the rest pose is open, labelled as set by the reference", () =>
        {
            var m = NifMotion.Read(door, null, new MotionPlacement("DOOR", true));
            var rest = m.Poses.SingleOrDefault(p => p.Rest);
            return (rest?.Name == "open" && (rest.Label ?? "").Contains("IsOpenByDefault", StringComparison.Ordinal), $"{rest?.Name} {rest?.Label}");
        });
        byte[] twoAxis = Rig(leaf, pivot, Xyz(FG(KeyType.LINEAR_KEY, FK(0, Pi / 6)), FG(KeyType.LINEAR_KEY, FK(0, 0)), FG(KeyType.LINEAR_KEY, FK(0, Pi / 2))));
        Case(check, "MK3d Euler order: x 30 deg + z 90 deg gives rows = (Rz Ry Rx)^T (rejects Rx Ry Rz and the transpose)", () =>
        {
            var m = NifMotion.Read(twoAxis, null);
            var (r, _, _) = NifMotion.NodeLocal(twoAxis, m, "seq:Open", "Mover", 0.5f);
            var want = Mul(Mul(Cz(Math.PI / 2), Cy(0)), Cx(Math.PI / 6));
            var wrongOrder = Mul(Mul(Cx(Math.PI / 6), Cy(0)), Cz(Math.PI / 2));
            return (RowsEqualTransposeOf(r, want, 1e-5) && !RowsEqualTransposeOf(r, wrongOrder, 1e-3),
                    $"rows [{r.M11:0.###} {r.M12:0.###} {r.M13:0.###} | {r.M21:0.###} {r.M22:0.###} {r.M23:0.###} | {r.M31:0.###} {r.M32:0.###} {r.M33:0.###}]");
        });
        byte[] tangents = Rig(leaf, pivot, Xyz(FG(KeyType.LINEAR_KEY, FK(0, 0)), FG(KeyType.LINEAR_KEY, FK(0, 0)),
                                               FG(KeyType.QUADRATIC_KEY, FK(0, 0, fw: 0.3f, bw: 0.9f), FK(1, 1, fw: 0.5f, bw: 0.2f))));
        Case(check, "MK3e quadratic tangents: z(0.5) = 0.55 rad with out = Backward, in = Forward (swapped reading gives 0.5125)", () =>
        {
            var m = NifMotion.Read(tangents, null);
            var (r, _, _) = NifMotion.NodeLocal(tangents, m, "seq:Open", "Mover", 0.5f);
            float a = AngleZ(r);
            return (Near(a, 0.55, 1e-4), $"z(0.5) = {a:0.#####} rad");
        });
        Case(check, "MK3f FloatRange: the Hermite piece's hull on [0, 1] contains every value and its ends (0 and 1)", () =>
        {
            var g = FG(KeyType.QUADRATIC_KEY, FK(0, 0, fw: 0.3f, bw: 0.9f), FK(1, 1, fw: 0.5f, bw: 0.2f));
            var range = NifMotionKeys.FloatRange(g, 0, 1);
            bool all = Enumerable.Range(0, 101).All(k => { float v = NifMotionKeys.EvalFloat(g, k / 100f); return v >= range.Min - 1e-6f && v <= range.Max + 1e-6f; });
            return (all && range.Min <= 0 && range.Max >= 1, $"[{range.Min:0.####}, {range.Max:0.####}]");
        });

        // ================= MK5: skinned two-bone rotation =================
        Case(check, "MK5 skinning: BoneB turned 90 deg -> (40,1) goes to (19,20); the 0.5/0.5 vertex (30,1) to (24.5,5.5) (rejects skin and bone composed the wrong way)", () =>
        {
            var m = NifMotion.Read(skin, null);
            var p = NifMotion.Pose(skin, m, "seq:Open", 1f, VolumeGeometry.Render);
            if (p.Soup is null) return (false, $"{p.Status} {p.Reason}");
            var v = p.Soup.Vertices;
            var tip = RotZ(new Vector3(40, 1, 0), new Vector3(20, 0, 0), Pi / 2);              // (19, 20, 0)
            var mid = (new Vector3(30, 1, 0) + RotZ(new Vector3(30, 1, 0), new Vector3(20, 0, 0), Pi / 2)) / 2;   // (24.5, 5.5, 0)
            return (p.Status == "posed" && v.Any(x => NearV(x, tip, 1e-3f)) && v.Any(x => NearV(x, mid, 1e-3f)), $"{p.Status} want {S(tip)} {S(mid)}");
        });
        // MK5c (added at Gate C): the partition palette maps a partition-local bone index to the skin bone. No lab mesh the
        // reader skins has a permuted palette (gatec/partitions-census.jsonl), so this convention is protected here only.
        Case(check, "motion fixture control (palette): the LE rig round-trips two partitions with palette [1, 0] and no skin-data weights", () =>
        {
            var n = Load(SkinRig(Pi / 2, partitioned: true));
            var sp = n.Blocks.OfType<NiSkinPartition>().SingleOrDefault();
            var sd = n.Blocks.OfType<NiSkinData>().Single();
            bool ok = sp is not null && sp.Partitions.Count == 2 && sp.Partitions.All(x => x.Bones.SequenceEqual(new ushort[] { 1, 0 }) && x.BoneIndices.Count == x.NumVertices * 2)
                      && sd.BoneList.All(x => (x.VertexWeights?.Count ?? 0) == 0);
            return (ok, $"partitions {sp?.Partitions.Count} palettes {string.Join("|", sp?.Partitions.Select(x => string.Join(",", x.Bones)) ?? [])}");
        });
        Case(check, "MK5c skinning through a permuted partition palette: BoneB turned 90 deg -> (40,1) goes to (19,20), (30,1) to (24.5,5.5), (0,1) stays (rejects the local index read as the skin bone)", () =>
        {
            var b = SkinRig(Pi / 2, partitioned: true);
            var m = NifMotion.Read(b, null);
            var p = NifMotion.Pose(b, m, "seq:Open", 1f, VolumeGeometry.Render);
            if (p.Soup is null) return (false, $"{m.Status} {m.Reason} {p.Status} {p.Reason}");
            var v = p.Soup.Vertices;
            var tip = RotZ(new Vector3(40, 1, 0), new Vector3(20, 0, 0), Pi / 2);
            var mid = (new Vector3(30, 1, 0) + RotZ(new Vector3(30, 1, 0), new Vector3(20, 0, 0), Pi / 2)) / 2;
            var root = new Vector3(0, 1, 0);
            return (p.Status == "posed" && v.Any(x => NearV(x, tip, 1e-3f)) && v.Any(x => NearV(x, mid, 1e-3f)) && v.Any(x => NearV(x, root, 1e-3f)),
                    $"{p.Status} {m.Skins.FirstOrDefault()?.Authority} want {S(tip)} {S(mid)} {S(root)}");
        });
        Case(check, "motion fixture control (SSE palette): the SSE rig round-trips one partition with palette [1, 0], its vertex data and the skin-data weights", () =>
        {
            var n = Load(SkinRig(Pi / 2, ssePermuted: true));
            var sp = n.Blocks.OfType<NiSkinPartition>().SingleOrDefault();
            var sh = n.GetShapes().OfType<BSTriShape>().SingleOrDefault();
            bool ok = sp is not null && sp.Partitions.Count == 1 && sp.Partitions[0].Bones.SequenceEqual(new ushort[] { 1, 0 })
                      && sh?.VertexDataSSE is { Count: 8 } && n.Blocks.OfType<NiSkinData>().Single().BoneList.All(x => (x.VertexWeights?.Count ?? 0) > 0);
            return (ok, $"partitions {sp?.Partitions.Count} palette {string.Join(",", sp?.Partitions.FirstOrDefault().Bones ?? new())} sse vertices {sh?.VertexDataSSE?.Count}");
        });
        Case(check, "MK5d skinning through a permuted SSE partition palette: the partition agrees with the skin data and BoneB turned 90 deg -> (40,1) goes to (19,20), (0,1) stays (rejects the local index read as the skin bone)", () =>
        {
            var b = SkinRig(Pi / 2, ssePermuted: true);
            var m = NifMotion.Read(b, null);
            var p = NifMotion.Pose(b, m, "seq:Open", 1f, VolumeGeometry.Render);
            var sk = m.Skins.FirstOrDefault();
            if (p.Soup is null) return (false, $"{m.Status} {m.Reason} {p.Status} {p.Reason}");
            var v = p.Soup.Vertices;
            var tip = RotZ(new Vector3(40, 1, 0), new Vector3(20, 0, 0), Pi / 2);
            var root = new Vector3(0, 1, 0);
            return (p.Status == "posed" && sk?.Authority == "partition" && sk.WeightSourceDifference == 0 && v.Any(x => NearV(x, tip, 1e-3f)) && v.Any(x => NearV(x, root, 1e-3f)),
                    $"{p.Status} {sk?.Authority} differing {sk?.WeightSourceDifference} want {S(tip)} {S(root)}");
        });
        Case(check, "MK5 skinning: bind check — at the stored transforms the skinned strip equals its stored vertices (composite spread 0)", () =>
        {
            var m = NifMotion.Read(skin, null);
            var s = m.Skins.Single();
            return (s.BindVsStored <= 1e-4 && s.CompositeSpread <= 1e-4 && s.Authority == "skin-data", $"bind {s.BindVsStored} spread {s.CompositeSpread} {s.Authority}");
        });

        // ================= MK9: unbounded motion between samples =================
        byte[] half = Rig(Box(10, -2, 0, 30, 2, 10), Vector3.Zero, Xyz(FG(KeyType.LINEAR_KEY, FK(0, 0)), FG(KeyType.LINEAR_KEY, FK(0, 0)), FG(KeyType.LINEAR_KEY, FK(0, 0), FK(1, Pi))));
        Case(check, "MK9 a 180 deg turn in one key interval: a cube at the 90 deg position (0, 20) is a witness after refinement", () =>
        {
            var m = NifMotion.Read(half, null);
            var r = SweptGeometry.Build(half, m, null, VolumeGeometry.Render, new MotionOptions(DeltaTarget: 0.5));
            var d = SweptGeometry.Contact(r, Cube(0, 20, 5, 2), null, true);
            return (d.Decision == "witness" && r.Converged, $"{d.Decision} t={d.Time} samples {r.Samples.Count}");
        });
        Case(check, "MK9 budget of 2 samples: the same cube is cannot_tell with converged false, never 'never' (rejects an inner bound reported as certified)", () =>
        {
            var m = NifMotion.Read(half, null);
            var r = SweptGeometry.Build(half, m, null, VolumeGeometry.Render, new MotionOptions(DeltaTarget: 0.5, SampleBudget: 2));
            var d = SweptGeometry.Contact(r, Cube(0, 20, 5, 2), null, true);
            return (d.Decision == "cannot_tell" && !d.Converged && d.Width > 0.5, $"{d.Decision} converged {d.Converged} width {d.Width:0.##}");
        });

        // ================= MK10: witness / never / cannot_tell on a door =================
        Case(check, "MK10 door: a box in the swing but clear of both rest poses is a witness labelled seq:Open with a time in (0, 1]", () =>
        {
            var m = NifMotion.Read(door, null, new MotionPlacement("DOOR", false));
            var r = SweptGeometry.Build(door, m, null, VolumeGeometry.Render);
            var at45 = RotZ(pivot + new Vector3(30, 0, 40), pivot, -Pi / 4);
            var d = SweptGeometry.Contact(r, Cube(at45.X, at45.Y, at45.Z, 2), null, true);
            var closed = NifMotion.Pose(door, m, "closed", VolumeGeometry.Render);
            var vs = MeshVolume.Measure(closed.Soup!, Cube(at45.X, at45.Y, at45.Z, 2), VolumeGeometry.Render);
            return (d.Decision == "witness" && (d.AnimationId == "seq:Open" || d.AnimationId == "seq:Close") && d.Time is > 0 and <= 1
                    && (d.Label ?? "").Contains("trigger: not settled by the files", StringComparison.Ordinal) && vs.Intersection?.Upper == 0,
                    $"{d.Decision} {d.AnimationId}@{d.Time} label '{d.Label}' rest-overlap {vs.Intersection?.Upper}");
        });
        // Gate C change (review): the 0.2 u box must be cannot_tell at the coarse delta (achieved delta above 0.2 u), not
        // merely "not a witness" - a zero interval bound answers never there, and the old assertion could not see that.
        Case(check, "MK10 door: a box 5 u outside the swing is never; one 0.2 u outside is cannot_tell at delta 0.5 (achieved delta > 0.2 u) and never at delta 0.02", () =>
        {
            var m = NifMotion.Read(door, null);
            // outside the far edge: the leaf (x 0..40, y -2..2 in the mover frame) reaches radius sqrt(40^2+2^2) from the pivot
            float reach = MathF.Sqrt(40 * 40 + 2 * 2);
            // the cube is axis-aligned and centred on the -45 deg ray: its corner nearest the pivot lies 2*sqrt(2) inside its centre
            float diag = 2 * MathF.Sqrt(2);
            var far5 = RotZ(pivot + new Vector3(reach + 5 + diag, 0, 40), pivot, -Pi / 4);
            var far02 = RotZ(pivot + new Vector3(reach + 0.2f + diag, 0, 40), pivot, -Pi / 4);
            var coarse = SweptGeometry.Build(door, m, null, VolumeGeometry.Render, new MotionOptions(DeltaTarget: 0.5));
            var fine = SweptGeometry.Build(door, m, null, VolumeGeometry.Render, new MotionOptions(DeltaTarget: 0.02, SampleBudget: 65536));
            var a = SweptGeometry.Contact(coarse, Cube(far5.X, far5.Y, far5.Z, 2), null, true);
            var b = SweptGeometry.Contact(coarse, Cube(far02.X, far02.Y, far02.Z, 2), null, true);
            var c = SweptGeometry.Contact(fine, Cube(far02.X, far02.Y, far02.Z, 2), null, true);
            return (a.Decision == "never" && coarse.MaxDelta > 0.2 && b.Decision == "cannot_tell" && c.Decision == "never",
                    $"5u {a.Decision}; 0.2u coarse {b.Decision} (achieved delta {coarse.MaxDelta:0.###}) fine {c.Decision}");
        });

        // ================= MK12: refusals by name =================
        Case(check, "MK12 physics: a bhkLimitedHingeConstraint in the file refuses the moving shapes with reason 'physics'", () =>
        {
            var b = Rig(leaf, pivot, Xyz(NoKeys(), NoKeys(), FG(KeyType.LINEAR_KEY, FK(0, 0), FK(1, 1))), extra: AddConstraint);
            var m = NifMotion.Read(b, null);
            return (m.Status == "refused" && (m.Reason ?? "").Contains("physics", StringComparison.Ordinal), $"{m.Status} {m.Reason}");
        });
        Case(check, "MK12 cloth: BSClothExtraData refuses with reason 'cloth'", () =>
        {
            var b = Rig(leaf, pivot, null, extra: (f, root) => { root.ExtraDataList ??= new(); root.ExtraDataList.AddBlockRef(f.AddBlock(new BSClothExtraData())); root.NumExtraDataList = 1; });
            var m = NifMotion.Read(b, null);
            return (m.Status == "refused" && (m.Reason ?? "").Contains("cloth", StringComparison.Ordinal), $"{m.Status} {m.Reason}");
        });
        Case(check, "MK12 TBC keys refuse with reason naming TBC_KEY", () =>
        {
            var b = Rig(leaf, pivot, Xyz(NoKeys(), NoKeys(), FG(KeyType.TBC_KEY, FK(0, 0), FK(1, 1))));
            var m = NifMotion.Read(b, null);
            return (m.Status == "refused" && (m.Reason ?? "").Contains("TBC_KEY", StringComparison.Ordinal), $"{m.Status} {m.Reason}");
        });
        Case(check, "MK12 played LINEAR quaternion keys are supported (G1): Open@end turns the leaf +90 deg about Z (rejects the misread component order)", () =>
        {
            var q = new NiTransformData
            {
                RotationType = KeyType.LINEAR_KEY, NumRotationKeys = 2,
                QuaternionKeys = new List<QuatKey<Quaternion>> { new() { Time = 0, Value = Raw(Quaternion.Identity) }, new() { Time = 1, Value = Raw(Quaternion.CreateFromAxisAngle(Vector3.UnitZ, Pi / 2)) } },
                Translations = NoVKeys(), Scales = NoKeys(),
            };
            var b = Rig(leaf, pivot, q);
            var m = NifMotion.Read(b, null);
            var p = NifMotion.Pose(b, m, "seq:Open", 1f, VolumeGeometry.Render);
            var want = RotZ(pivot + new Vector3(40, 2, 80), pivot, Pi / 2);
            return (m.Status == "moving" && p.Status == "posed" && p.Soup!.Vertices.Any(v => NearV(v, want, 1e-3f)), $"{m.Status} {m.Reason} {p.Status} want {S(want)}");
        });
        Case(check, "MK12 QUADRATIC quaternion keys refuse with reason naming the quaternion key type", () =>
        {
            var q = new NiTransformData
            {
                RotationType = KeyType.QUADRATIC_KEY, NumRotationKeys = 2,
                QuaternionKeys = new List<QuatKey<Quaternion>> { new() { Time = 0, Value = Raw(Quaternion.Identity) }, new() { Time = 1, Value = Raw(Quaternion.CreateFromAxisAngle(Vector3.UnitZ, Pi / 2)) } },
                Translations = NoVKeys(), Scales = NoKeys(),
            };
            var m = NifMotion.Read(Rig(leaf, pivot, q), null);
            return (m.Status == "refused" && (m.Reason ?? "").Contains("quaternion key type QUADRATIC_KEY", StringComparison.Ordinal), $"{m.Status} {m.Reason}");
        });
        Case(check, "MK12 an inactive direct controller refuses with reason 'controller inactive'", () =>
        {
            var b = Rig(leaf, pivot, Xyz(NoKeys(), NoKeys(), FG(KeyType.LINEAR_KEY, FK(0, 0), FK(1, 1))), direct: true, directActive: false);
            var m = NifMotion.Read(b, null);
            return (m.Status == "refused" && (m.Reason ?? "").Contains("controller inactive", StringComparison.Ordinal), $"{m.Status} {m.Reason}");
        });
        Case(check, "MK12 an active direct LOOP controller is an animation ctl:Mover (CYCLE_LOOP)", () =>
        {
            var b = Rig(leaf, pivot, Xyz(NoKeys(), NoKeys(), FG(KeyType.LINEAR_KEY, FK(0, 0), FK(1, 1))), direct: true, cycle: CycleType.CYCLE_LOOP);
            var m = NifMotion.Read(b, null);
            return (m.Status == "moving" && m.Animations.Any(a => a.Id == "ctl:Mover" && a.Cycle == "CYCLE_LOOP"), $"{m.Status} {string.Join(",", m.Animations.Select(a => a.Id + ":" + a.Cycle))}");
        });
        Case(check, "MK12 a mesh whose graph cannot be read (no asset access) refuses naming the graph file", () =>
        {
            var b = Rig(leaf, pivot, null, extra: (f, root) => { root.ExtraDataList ??= new(); root.ExtraDataList.AddBlockRef(f.AddBlock(new BSBehaviorGraphExtraData { BehaviourGraphFile = new NiStringRef(@"Fixture\Rig.hkx") })); root.NumExtraDataList = 1; });
            var m = NifMotion.Read(b, _ => null);
            // Gate C change (S15k): the reason must name the missing file as missing, not merely mention the path
            return (m.Status == "refused" && (m.Reason ?? "").Contains(@"behaviour graph file missing: meshes\Fixture\Rig.hkx", StringComparison.OrdinalIgnoreCase), $"{m.Status} {m.Reason}");
        });
        Case(check, "MK12 particles (F5): a particle-system shape refuses as a shape with reason 'particles'; the static rest of the mesh is not refused for it", () =>
        {
            var b = Rig(leaf, pivot, null, extra: (f, root) => root.Children.AddBlockRef(f.AddBlock(new NiParticleSystem { Name = new NiStringRef("Sparks"), Flags_ui = 0xE, Scale = 1f })));
            var m = NifMotion.Read(b, null);
            var sp = m.Shapes.FirstOrDefault(x => x.Name == "Sparks");
            return (sp?.Refusal is { } why && why.Contains("particles", StringComparison.Ordinal) && m.Shapes.Where(x => x.Name != "Sparks").All(x => x.Refusal is null),
                    $"{m.Status} {string.Join("; ", m.Shapes.Select(x => x.Name + ":" + (x.Refusal ?? x.Mover)))}");
        });
        // wind (Tree_Anim) and SMP physics: runtime motion the files declare outside any controller
        static void FrameShader(NifFile f, bool treeAnim)
        {
            var frame = (BSTriShape)f.GetShapes().First(x => x.Name?.String == "Frame");
            var flags = SkyrimShaderPropertyFlags2.ZBuffer_Write | (treeAnim ? SkyrimShaderPropertyFlags2.Tree_Anim : 0);
            frame.ShaderPropertyRef = new NiBlockRef<BSShaderProperty>(f.AddBlock(new BSLightingShaderProperty { ShaderFlags_SSPF2 = flags }));
        }
        Case(check, "motion fixture control (wind): the Frame shape's lighting shader round-trips with and without Tree_Anim, and without it the mesh reads static", () =>
        {
            var on = Load(Rig(leaf, pivot, null, extra: (f, root) => FrameShader(f, true)));
            var off = Rig(leaf, pivot, null, extra: (f, root) => FrameShader(f, false));
            bool flagOn = on.GetShader(on.GetShapes().First(x => x.Name?.String == "Frame")) is BSLightingShaderProperty l1 && l1.ShaderFlags_SSPF2.HasFlag(SkyrimShaderPropertyFlags2.Tree_Anim);
            var offNif = Load(off);
            bool flagOff = offNif.GetShader(offNif.GetShapes().First(x => x.Name?.String == "Frame")) is BSLightingShaderProperty l0 && !l0.ShaderFlags_SSPF2.HasFlag(SkyrimShaderPropertyFlags2.Tree_Anim);
            var m = NifMotion.Read(off, null);
            return (flagOn && flagOff && m.Status == "static", $"on {flagOn} off {flagOff} {m.Status} {m.Reason}");
        });
        Case(check, "MK12 wind: a shape whose lighting shader carries Tree_Anim refuses with reason 'wind'; the other shape is not refused for it", () =>
        {
            var m = NifMotion.Read(Rig(leaf, pivot, null, extra: (f, root) => FrameShader(f, true)), null);
            var fr = m.Shapes.FirstOrDefault(x => x.Name == "Frame");
            return (m.Status == "refused" && fr?.Refusal is { } why && why.StartsWith("wind:", StringComparison.Ordinal) && why.Contains("Tree_Anim", StringComparison.Ordinal)
                    && m.Shapes.Where(x => x.Name != "Frame").All(x => x.Refusal is null), $"{m.Status} {string.Join("; ", m.Shapes.Select(x => x.Name + ":" + (x.Refusal ?? x.Mover)))}");
        });
        static void SmpMarker(NifFile f, NiNode root)
        {
            root.ExtraDataList ??= new();
            root.ExtraDataList.AddBlockRef(f.AddBlock(new NiStringExtraData { Name = new NiStringRef(NifMotion.SmpMarker), StringData = new NiStringRef(@"meshes\Fixture\physics.xml") }));
            root.NumExtraDataList = (uint)root.ExtraDataList.Count;
        }
        Case(check, "motion fixture control (SMP): the marker round-trips on the skinned rig's root, and without it the rig reads as moving", () =>
        {
            var n = Load(SkinRig(Pi / 2, SmpMarker));
            bool has = n.Blocks.OfType<NiStringExtraData>().Any(x => x.Name?.String == NifMotion.SmpMarker && x.StringData?.String == @"meshes\Fixture\physics.xml");
            var m = NifMotion.Read(SkinRig(Pi / 2), null);
            return (has && m.Status == "moving", $"marker {has} without: {m.Status} {m.Reason}");
        });
        Case(check, "MK12 SMP: the 'HDT Skinned Mesh Physics Object' marker refuses the skinned shape with reason 'SMP physics' naming the XML", () =>
        {
            var m = NifMotion.Read(SkinRig(Pi / 2, SmpMarker), null);
            var st = m.Shapes.FirstOrDefault(x => x.Name == "Strip");
            return (m.Status == "refused" && st?.Refusal is { } why && why.StartsWith("SMP physics:", StringComparison.Ordinal) && why.Contains("physics.xml", StringComparison.Ordinal),
                    $"{m.Status} {string.Join("; ", m.Shapes.Select(x => x.Name + ":" + (x.Refusal ?? x.Mover)))}");
        });
        Case(check, "MK12 a static mesh reads as static with no animation", () =>
        {
            var m = NifMotion.Read(Rig(leaf, pivot, null), null);
            return (m.Status == "static" && m.Animations.Count == 0, $"{m.Status} {m.Animations.Count}");
        });

        // ================= F3: quaternion conjugation arms (shared function) =================
        Case(check, "MF3a QuaternionRows(90 deg about Z) equals XyzRows(0,0,pi/2) (rejects the conjugate, which is -90 deg)", () =>
        {
            var q = NifMotionKeys.QuaternionRows(Quaternion.CreateFromAxisAngle(Vector3.UnitZ, Pi / 2));
            var e = NifMotionKeys.XyzRows(0, 0, Pi / 2);
            bool eq = Math.Abs(q.M11 - e.M11) < 1e-6 && Math.Abs(q.M12 - e.M12) < 1e-6 && Math.Abs(q.M21 - e.M21) < 1e-6 && Math.Abs(q.M22 - e.M22) < 1e-6;
            return (eq && RowsEqualTransposeOf(e, Cz(Math.PI / 2), 1e-6), $"q rows [{q.M11:0.###} {q.M12:0.###} | {q.M21:0.###} {q.M22:0.###}]");
        });
        Case(check, "MF3b EvalQuaternion LINEAR on keys as NiflySharp holds them: halfway from identity to 90 deg about Z is 45 deg about +Z", () =>
        {
            var keys = new List<QuatKey<Quaternion>> { new() { Time = 0, Value = Raw(Quaternion.Identity) }, new() { Time = 1, Value = Raw(Quaternion.CreateFromAxisAngle(Vector3.UnitZ, Pi / 2)) } };
            var q = NifMotionKeys.EvalQuaternion(keys, KeyType.LINEAR_KEY, 0.5f);
            var want = Quaternion.CreateFromAxisAngle(Vector3.UnitZ, Pi / 4);
            return (MathF.Abs(Quaternion.Dot(q, want)) > 1 - 1e-6 && q.Z * want.Z > 0, $"q ({q.X:0.####},{q.Y:0.####},{q.Z:0.####},{q.W:0.####})");
        });

        Case(check, "motion fixture control (G1): a saved quaternion key writes NiflySharp's X, Y, Z, W as the file's four floats in that order", () =>
        {
            var raw = new Quaternion(0.1f, 0.2f, 0.3f, 0.4f);
            var q = new NiTransformData
            {
                RotationType = KeyType.LINEAR_KEY, NumRotationKeys = 1,
                QuaternionKeys = new List<QuatKey<Quaternion>> { new() { Time = 0.5f, Value = raw } }, Translations = NoVKeys(), Scales = NoKeys(),
            };
            var bytes = Rig(leaf, pivot, q);
            var pat = new byte[20];
            BitConverter.GetBytes(0.5f).CopyTo(pat, 0); BitConverter.GetBytes(0.1f).CopyTo(pat, 4); BitConverter.GetBytes(0.2f).CopyTo(pat, 8);
            BitConverter.GetBytes(0.3f).CopyTo(pat, 12); BitConverter.GetBytes(0.4f).CopyTo(pat, 16);
            int at = bytes.AsSpan().IndexOf(pat);
            return (at >= 0, $"time + (X, Y, Z, W) found at byte {at}");
        });
        Case(check, "MF3c FromNifly: NiflySharp (X=w, Y=x, Z=y, W=z) -> true (x, y, z, w) (rejects the field order taken as is)", () =>
        {
            var t = NifMotionKeys.FromNifly(new Quaternion(0.9f, 0.1f, 0.2f, 0.3f));
            return (t.X == 0.1f && t.Y == 0.2f && t.Z == 0.3f && t.W == 0.9f, $"({t.X}, {t.Y}, {t.Z}, {t.W})");
        });

        // ================= Gate C note C1: the rotation bound takes one sign =================
        Case(check, "MC1a RotationBound: points on both sides of s0's hemisphere (dots 0.1 and -0.1 after aligning to the first) -> pi (rejects |dot| per point: 168.5 deg < the 180 deg blend)", () =>
        {
            var s0 = new Quaternion(1, 0, 0, 0);
            var c1 = new Quaternion(0.1f, MathF.Sqrt(0.99f), 0, 0); var c2 = new Quaternion(-0.1f, MathF.Sqrt(0.99f), 0, 0);
            float b = HavokClip.RotationBound(s0, new[] { c1, c2 });
            var mid = Quaternion.Normalize(new Quaternion((c1.X + c2.X) / 2, (c1.Y + c2.Y) / 2, 0, 0));
            float midAngle = 2 * MathF.Acos(MathF.Min(1, MathF.Abs(Quaternion.Dot(mid, s0))));
            return (b >= midAngle - 1e-6f && b >= MathF.PI - 1e-6f, $"bound {b * 180 / MathF.PI:0.##} deg, blend midpoint {midAngle * 180 / MathF.PI:0.##} deg");
        });
        Case(check, "MC1b RotationBound: one-sided points -> the largest point angle (finite, contains every blend)", () =>
        {
            var s0 = Quaternion.Identity;
            var c1 = Quaternion.CreateFromAxisAngle(Vector3.UnitZ, 0.4f); var c2 = Quaternion.CreateFromAxisAngle(Vector3.UnitX, 0.7f);
            float b = HavokClip.RotationBound(Quaternion.Negate(s0), new[] { c1, c2 });       // s0 given with the opposite sign
            bool all = Enumerable.Range(0, 21).All(k =>
            {
                float u = k / 20f;
                var q = Quaternion.Normalize(new Quaternion((1 - u) * c1.X + u * c2.X, (1 - u) * c1.Y + u * c2.Y, (1 - u) * c1.Z + u * c2.Z, (1 - u) * c1.W + u * c2.W));
                return 2 * MathF.Acos(MathF.Min(1, MathF.Abs(Quaternion.Dot(q, s0)))) <= b + 1e-6f;
            });
            return (all && b < MathF.PI && MathF.Abs(b - 0.7f) < 1e-4f, $"bound {b:0.#####} rad (largest point 0.7)");
        });

        // ================= Q3: the 1-DOF blend rule =================
        Case(check, "MQ3a 1-DOF: rotations about one axis (Y, 0..45 deg) with fixed translation -> covered (null)", () =>
        {
            var poses = new[] { 0f, 15f, 45f }.Select(d => (Quaternion.CreateFromAxisAngle(Vector3.UnitY, d * Pi / 180), new Vector3(1, 2, 3))).ToList();
            var r = SweptGeometry.OneDofRefusal(poses);
            return (r is null, r ?? "covered");
        });
        Case(check, "MQ3b 1-DOF: translations along one line with fixed rotation -> covered", () =>
        {
            var poses = new[] { 0f, 50f, 200f }.Select(z => (Quaternion.Identity, new Vector3(0, 0, z))).ToList();
            var r = SweptGeometry.OneDofRefusal(poses);
            return (r is null, r ?? "covered");
        });
        Case(check, "MQ3c 2-DOF: rotations about Y and about X -> refused with a reason", () =>
        {
            var poses = new List<(Quaternion, Vector3)> { (Quaternion.Identity, Vector3.Zero), (Quaternion.CreateFromAxisAngle(Vector3.UnitY, 0.5f), Vector3.Zero), (Quaternion.CreateFromAxisAngle(Vector3.UnitX, 0.5f), Vector3.Zero) };
            var r = SweptGeometry.OneDofRefusal(poses);
            return (r is not null, r ?? "null");
        });
        Case(check, "MQ3d 2-DOF: rotation and translation together -> refused", () =>
        {
            var poses = new List<(Quaternion, Vector3)> { (Quaternion.Identity, Vector3.Zero), (Quaternion.CreateFromAxisAngle(Vector3.UnitY, 0.5f), new Vector3(0, 0, 10)) };
            var r = SweptGeometry.OneDofRefusal(poses);
            return (r is not null, r ?? "null");
        });

        // ================= Havok reader: refusal of an unsupported packfile =================
        Case(check, "MH1 HavokPackfile refuses bytes that are not a binary packfile, naming the magic", () =>
        {
            try { HavokPackfile.Read(new byte[64]); return (false, "accepted"); }
            catch (HavokFormatException ex) { return (ex.Message.Contains("magic", StringComparison.OrdinalIgnoreCase), ex.Message); }
        });
    }
}
