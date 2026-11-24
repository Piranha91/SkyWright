using System.Buffers.Binary;
using System.Numerics;
using System.Text;
using HousecarlCore;
using SkyWright.Engine;
using NiflySharp;
using NiflySharp.Blocks;
using NiflySharp.Enums;
using NiflySharp.Structs;

/// <summary>Fixture writer for the member-motion known answers (lane M): a minimal Havok binary packfile (hk_2010.2.0-r1,
/// pointer size 8, little endian; one class-name section and one data section with local, global and virtual fixups) holding
/// only the objects and fields <see cref="HavokBehaviour"/> and <see cref="HavokAnimation.ReadSkeleton"/> read, at the field
/// offsets those readers state (decision record D5 of the swept-volume task). A fixture control proves every file it writes
/// resolves through the production readers; nothing here is a second reader.</summary>
static partial class MotionProof
{
    internal sealed class Hkx
    {
        sealed class Obj { public string Cls = ""; public byte[] Body = Array.Empty<byte>(); public int Offset; }
        readonly List<Obj> objs = new();
        readonly List<(int Obj, int Field, int Target)> ptrs = new();
        readonly List<(int Obj, int Field, string S)> strs = new();
        readonly List<(int Obj, int Field, int[] Targets)> ptrArrays = new();
        readonly List<(int Obj, int Field, string?[] S)> strArrays = new();
        readonly List<(int Obj, int Field, byte[] Elems, (int Elem, int Off, string S)[] Strs, int Stride)> structArrays = new();

        public int Add(string cls, int size) { objs.Add(new Obj { Cls = cls, Body = new byte[size] }); return objs.Count - 1; }
        public Hkx I32(int o, int off, int v) { BinaryPrimitives.WriteInt32LittleEndian(objs[o].Body.AsSpan(off), v); return this; }
        public Hkx F32(int o, int off, float v) { BinaryPrimitives.WriteSingleLittleEndian(objs[o].Body.AsSpan(off), v); return this; }
        public Hkx I8(int o, int off, sbyte v) { objs[o].Body[off] = (byte)v; return this; }
        public Hkx Ptr(int o, int off, int target) { ptrs.Add((o, off, target)); return this; }
        public Hkx Str(int o, int off, string s) { strs.Add((o, off, s)); return this; }
        public Hkx PtrArray(int o, int off, params int[] targets) { ptrArrays.Add((o, off, targets)); return I32(o, off + 8, targets.Length).I32(o, off + 12, targets.Length); }
        public Hkx StrArray(int o, int off, params string?[] s) { strArrays.Add((o, off, s)); return I32(o, off + 8, s.Length).I32(o, off + 12, s.Length); }
        public Hkx StructArray(int o, int off, int stride, int count, byte[] elems, params (int Elem, int Off, string S)[] elemStrs)
        { structArrays.Add((o, off, elems, elemStrs, stride)); return I32(o, off + 8, count).I32(o, off + 12, count); }

        public byte[] Save()
        {
            // class-name section: (u32 signature, 0x09, name\0) records
            var cn = new List<byte>(); var nameAt = new Dictionary<string, int>(StringComparer.Ordinal);
            foreach (var c in objs.Select(x => x.Cls).Distinct())
            {
                cn.AddRange(BitConverter.GetBytes(0x2D2D2D2D)); cn.Add(0x09);
                nameAt[c] = cn.Count; cn.AddRange(Encoding.ASCII.GetBytes(c)); cn.Add(0);
            }
            while (cn.Count % 16 != 0) cn.Add(0xFF);
            // data section: objects first, then strings and array contents
            var data = new List<byte>();
            void Align(int a, byte fill = 0) { while (data.Count % a != 0) data.Add(fill); }
            foreach (var o in objs) { Align(16); o.Offset = data.Count; data.AddRange(o.Body); }
            var local = new List<(int Src, int Dst)>(); var global = new List<(int Src, int Dst)>();
            int Blob(byte[] b, int align = 16) { Align(align); int at = data.Count; data.AddRange(b); return at; }
            int CStr(string s) => Blob(Encoding.ASCII.GetBytes(s).Append((byte)0).ToArray(), 2);
            foreach (var (o, f, s) in strs) local.Add((objs[o].Offset + f, CStr(s)));
            foreach (var (o, f, t) in ptrs) global.Add((objs[o].Offset + f, objs[t].Offset));
            foreach (var (o, f, t) in ptrArrays)
            {
                if (t.Length == 0) continue;
                int at = Blob(new byte[8 * t.Length]);
                local.Add((objs[o].Offset + f, at));
                for (int i = 0; i < t.Length; i++) global.Add((at + 8 * i, objs[t[i]].Offset));
            }
            foreach (var (o, f, s) in strArrays)
            {
                if (s.Length == 0) continue;
                int at = Blob(new byte[8 * s.Length]);
                local.Add((objs[o].Offset + f, at));
                for (int i = 0; i < s.Length; i++) if (s[i] is { } str) local.Add((at + 8 * i, CStr(str)));
            }
            foreach (var (o, f, elems, es, stride) in structArrays)
            {
                if (elems.Length == 0) continue;
                int at = Blob(elems);
                local.Add((objs[o].Offset + f, at));
                foreach (var (e, off, s) in es) local.Add((at + stride * e + off, CStr(s)));
            }
            Align(16, 0);
            // the patched object bodies (data holds copies made before the fixups; pointers stay zero, as fixups resolve them)
            int dataLen = data.Count;
            var tables = new List<byte>();
            foreach (var (s, d) in local) { tables.AddRange(BitConverter.GetBytes(s)); tables.AddRange(BitConverter.GetBytes(d)); }
            while (tables.Count % 16 != 0) tables.Add(0xFF);
            int globalAt = dataLen + tables.Count;
            foreach (var (s, d) in global) { tables.AddRange(BitConverter.GetBytes(s)); tables.AddRange(BitConverter.GetBytes(1)); tables.AddRange(BitConverter.GetBytes(d)); }
            while (tables.Count % 16 != 0) tables.Add(0xFF);
            int virtAt = dataLen + tables.Count;
            foreach (var o in objs) { tables.AddRange(BitConverter.GetBytes(o.Offset)); tables.AddRange(BitConverter.GetBytes(0)); tables.AddRange(BitConverter.GetBytes(nameAt[o.Cls])); }
            while (tables.Count % 16 != 0) tables.Add(0xFF);
            int endAt = dataLen + tables.Count;
            // header (64) + two section headers (48 each)
            var file = new byte[64 + 96];
            BinaryPrimitives.WriteUInt32LittleEndian(file.AsSpan(0), 0x57E0E057u);
            BinaryPrimitives.WriteUInt32LittleEndian(file.AsSpan(4), 0x10C0C010u);
            BinaryPrimitives.WriteInt32LittleEndian(file.AsSpan(12), 8);
            file[16] = 8; file[17] = 1; file[18] = 0; file[19] = 1;
            BinaryPrimitives.WriteInt32LittleEndian(file.AsSpan(20), 2);   // sections
            BinaryPrimitives.WriteInt32LittleEndian(file.AsSpan(24), 1);   // contents section
            BinaryPrimitives.WriteInt32LittleEndian(file.AsSpan(32), 0);   // class-name section
            Encoding.ASCII.GetBytes(HavokPackfile.SupportedVersion).CopyTo(file, 40);
            BinaryPrimitives.WriteInt32LittleEndian(file.AsSpan(56), -1);
            int cnStart = file.Length, dStart = cnStart + cn.Count;
            void Section(int s, string tag, int start, int localAt, int glob, int virt, int end)
            {
                int o = 64 + 48 * s;
                Encoding.ASCII.GetBytes(tag).CopyTo(file, o); file[o + 19] = 0xFF;
                BinaryPrimitives.WriteInt32LittleEndian(file.AsSpan(o + 20), start);
                BinaryPrimitives.WriteInt32LittleEndian(file.AsSpan(o + 24), localAt);
                BinaryPrimitives.WriteInt32LittleEndian(file.AsSpan(o + 28), glob);
                BinaryPrimitives.WriteInt32LittleEndian(file.AsSpan(o + 32), virt);
                BinaryPrimitives.WriteInt32LittleEndian(file.AsSpan(o + 36), end);
                BinaryPrimitives.WriteInt32LittleEndian(file.AsSpan(o + 40), end);
                BinaryPrimitives.WriteInt32LittleEndian(file.AsSpan(o + 44), end);
            }
            Section(0, "__classnames__", cnStart, cn.Count, cn.Count, cn.Count, cn.Count);
            Section(1, "__data__", dStart, dataLen, globalAt, virtAt, endAt);
            return file.Concat(cn).Concat(data).Concat(tables).ToArray();
        }
    }

    // ---------------- graph files ----------------
    /// <summary>One state of a fixture's state machine: a sequence generator (the NIF's own sequence), a reference-pose generator,
    /// or a blender over sequence generators.</summary>
    internal sealed record FxState(string Name, string Kind, string[] Sequences, bool SubtractLastChild = false);

    /// <summary>A blender's own words for lane N (fixed blender weights): each child's weight (+64; worldFromModelWeight +68 is
    /// written 1 for every child, as on the sharpening wheel), the blender's flags (int16 at +90), the children whose weight is bound
    /// to a graph variable (a variable binding set on the child, member path "weight"), and a binding on the blender itself
    /// (<paramref name="BlenderBinding"/> names its member path) and referencePoseWeightThreshold (+72). Offsets and the flag
    /// values: HKX2 / hkxcmd class tables.</summary>
    internal sealed record FxBlender(float[] Weights, short Flags = 0, int[]? BoundChildren = null, string? BlenderBinding = null,
                                     float Threshold = 0f);

    /// <summary>The files a NIF naming <c>Lab\{name}.hkx</c> reads: project, character, behaviour and (with bones) skeleton, keyed by
    /// their Data-relative paths. Every graph carries a transition effect (duration <paramref name="duration"/>) as its class list;
    /// <paramref name="boneWeights"/> adds an hkbBoneWeightArray object.</summary>
    internal static Dictionary<string, byte[]> GraphFiles(string name, IReadOnlyList<FxState> states, float duration = 0.25f,
                                                         (string Bone, Quaternion R, Vector3 T)[]? skeleton = null, bool boneWeights = false,
                                                         string transitionClass = "hkbBlendingTransitionEffect", FxBlender? blender = null)
    {
        var files = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
        var pj = new Hkx(); int p = pj.Add("hkbProjectStringData", 64); pj.StrArray(p, 48, $"{name}Char.hkx");
        files[$@"meshes\Lab\{name}.hkx"] = pj.Save();
        var ch = new Hkx(); int c = ch.Add("hkbCharacterStringData", 200);
        ch.StrArray(c, 48).Str(c, 184, $"{name}Beh.hkx");
        if (skeleton is not null) ch.Str(c, 168, $"{name}Skel.hkx");
        files[$@"meshes\Lab\{name}Char.hkx"] = ch.Save();
        var bh = new Hkx();
        int graph = bh.Add("hkbBehaviorGraph", 144);
        int sm = bh.Add("hkbStateMachine", 160);
        bh.Ptr(graph, 128, sm).Str(sm, 56, "Machine").I32(sm, 104, 0);
        var infos = new List<int>();
        int Seq(string seq) { int g = bh.Add("BGSGamebryoSequenceGenerator", 96); bh.Str(g, 56, "Gen" + seq).Str(g, 72, seq); return g; }
        for (int i = 0; i < states.Count; i++)
        {
            var s = states[i];
            int gen;
            if (s.Kind == "sequence") gen = Seq(s.Sequences[0]);
            else if (s.Kind == "reference") { gen = bh.Add("hkbReferencePoseGenerator", 80); bh.Str(gen, 56, "RefPose" + i); }
            else
            {
                gen = bh.Add("hkbBlenderGenerator", 112); bh.Str(gen, 56, "Blend" + i).I8(gen, 92, (sbyte)(s.SubtractLastChild ? 1 : 0));
                if (blender is null)
                {
                    var kids = s.Sequences.Select(q => { int k = bh.Add("hkbBlenderGeneratorChild", 80); bh.Ptr(k, 48, Seq(q)).F32(k, 64, 1f); return k; }).ToArray();
                    bh.PtrArray(gen, 96, kids);
                }
                else
                {
                    // lane N: the blender's stated weights, flags and bindings (every blender state of the fixture takes them)
                    int g0 = gen;
                    bh.I8(g0, 90, unchecked((sbyte)(blender.Flags & 0xFF))).I8(g0, 91, unchecked((sbyte)(blender.Flags >> 8)));
                    bh.F32(g0, 72, blender.Threshold);
                    var kids = s.Sequences.Select((q, ci) =>
                    {
                        int k = bh.Add("hkbBlenderGeneratorChild", 80);
                        bh.Ptr(k, 48, Seq(q)).F32(k, 64, blender.Weights[ci]).F32(k, 68, 1f);
                        if (blender.BoundChildren?.Contains(ci) == true) bh.Ptr(k, 16, BindingSet(bh, "weight"));
                        return k;
                    }).ToArray();
                    bh.PtrArray(g0, 96, kids);
                    if (blender.BlenderBinding is { } path) bh.Ptr(g0, 16, BindingSet(bh, path));
                }
            }
            int info = bh.Add("hkbStateMachineStateInfo", 112);
            bh.Ptr(info, 88, gen).Str(info, 96, s.Name).I32(info, 104, i);
            infos.Add(info);
        }
        bh.PtrArray(sm, 144, infos.ToArray());
        int fx = bh.Add(transitionClass, 96); bh.Str(fx, 56, "Transition").F32(fx, 80, duration);
        if (boneWeights) bh.Add("hkbBoneWeightArray", 64);
        files[$@"meshes\Lab\{name}Beh.hkx"] = bh.Save();
        if (skeleton is not null)
        {
            var sk = new Hkx(); int o = sk.Add("hkaSkeleton", 96);
            int n = skeleton.Length;
            var parents = new byte[2 * n]; for (int i = 0; i < n; i++) BinaryPrimitives.WriteInt16LittleEndian(parents.AsSpan(2 * i), (short)(i - 1));
            var pose = new byte[48 * n];
            for (int i = 0; i < n; i++)
            {
                var (_, r, t) = skeleton[i];
                float[] v = { t.X, t.Y, t.Z, 0, r.X, r.Y, r.Z, r.W, 1, 1, 1, 0 };
                for (int k = 0; k < 12; k++) BinaryPrimitives.WriteSingleLittleEndian(pose.AsSpan(48 * i + 4 * k), v[k]);
            }
            sk.Str(o, 16, name + "Skeleton").StructArray(o, 24, 2, n, parents)
              .StructArray(o, 40, 16, n, new byte[16 * n], skeleton.Select((b, i) => (i, 0, b.Bone)).ToArray())
              .StructArray(o, 56, 48, n, pose);
            files[$@"meshes\Lab\{name}Skel.hkx"] = sk.Save();
        }
        return files;
    }

    /// <summary>An hkbVariableBindingSet with one binding (member path <paramref name="memberPath"/>, variable 0): bindings hkArray at
    /// +16, hkbVariableBindingSetBinding stride 40, memberPath at 0, variableIndex at 28 (the layout HavokBehaviour reads).</summary>
    static int BindingSet(Hkx bh, string memberPath)
    {
        int set = bh.Add("hkbVariableBindingSet", 40);
        bh.StructArray(set, 16, 40, 1, new byte[40], (0, 0, memberPath));
        return set;
    }

    // ---------------- NIF chains ----------------
    /// <summary>A key source for one node: rotation about Z from <paramref name="deg0"/> to <paramref name="deg1"/> degrees
    /// (LINEAR quaternion keys over [0, 1]; Negate stores both keys with the opposite sign), or a translation
    /// along X from <paramref name="x0"/> to <paramref name="x1"/>.</summary>
    internal sealed record FxKeys(string Node, float? Deg0 = null, float? Deg1 = null, float? X0 = null, float? X1 = null, bool Negate = false);

    static NiTransformData KeyData(FxKeys k)
    {
        if (k.Deg0 is { } d0 && k.Deg1 is { } d1)
        {
            var q0 = Quaternion.CreateFromAxisAngle(Vector3.UnitZ, d0 * Pi / 180); var q1 = Quaternion.CreateFromAxisAngle(Vector3.UnitZ, d1 * Pi / 180);
            if (k.Negate) { q0 = Quaternion.Negate(q0); q1 = Quaternion.Negate(q1); }
            return new NiTransformData
            {
                RotationType = KeyType.LINEAR_KEY, NumRotationKeys = 2,
                QuaternionKeys = new List<QuatKey<Quaternion>> { new() { Time = 0, Value = Raw(q0) }, new() { Time = 1, Value = Raw(q1) } },
                Translations = NoVKeys(), Scales = NoKeys(),
            };
        }
        return new NiTransformData { RotationType = KeyType.LINEAR_KEY, NumRotationKeys = 0,
                                     Translations = VG(KeyType.LINEAR_KEY, (0f, new Vector3(k.X0 ?? 0, 0, 0)), (1f, new Vector3(k.X1 ?? 0, 0, 0))), Scales = NoKeys() };
    }

    /// <summary>root -> "N1" -> "N2" -> shapes. <paramref name="shapes"/>: (name, parent node "N1" or "N2", soup in that node's frame,
    /// alpha-blended). Sequences: name -> key sources (NiControllerSequence blocks under a manager). <paramref name="graph"/>: the
    /// BSBehaviorGraphExtraData path (null = none; the sequences then play as tier-1 animations).</summary>
    internal static byte[] ChainNif(string? graph, (string Name, string Parent, TriangleSoup Soup, bool Alpha)[] shapes,
                                    (string Name, FxKeys[] Keys, CycleType Cycle)[] sequences, Quaternion? n1Stored = null)
    {
        var f = new NifFile(); f.Create(Ver, withRootNode: true);
        var root = f.GetRootNodes().First(); root.Flags_ui = 0xE;
        var r1 = n1Stored is { } q ? Matrix4x4.CreateFromQuaternion(q) : Matrix4x4.Identity;
        var n1 = new NiNode { Name = new NiStringRef("N1"), Flags_ui = 0xE, Scale = 1f,
                              Rotation = new Matrix33 { M11 = r1.M11, M12 = r1.M12, M13 = r1.M13, M21 = r1.M21, M22 = r1.M22, M23 = r1.M23, M31 = r1.M31, M32 = r1.M32, M33 = r1.M33 } };
        var n2 = new NiNode { Name = new NiStringRef("N2"), Flags_ui = 0xE, Scale = 1f, Rotation = Identity33() };
        root.Children.AddBlockRef(f.AddBlock(n1));
        n1.Children.AddBlockRef(f.AddBlock(n2));
        foreach (var (name, parent, soup, alpha) in shapes)
        {
            var sh = Shape(name, soup);
            if (alpha)
            {
                var ap = new NiAlphaProperty { Threshold = 0 };
                ap.Flags.Value = 0x12ED;   // blend on, test on
                sh.AlphaPropertyRef = new NiBlockRef<NiAlphaProperty>(f.AddBlock(ap));
            }
            (parent == "N1" ? n1 : n2).Children.AddBlockRef(f.AddBlock(sh));
        }
        if (sequences.Length > 0)
        {
            var mtc = new NiMultiTargetTransformController();
            int mtcIndex = f.AddBlock(mtc);
            var mgr = new NiControllerManager();
            int mgrIndex = f.AddBlock(mgr);
            mgr.ControllerSequences ??= new();
            foreach (var (sname, keys, cycle) in sequences)
            {
                var seq = new NiControllerSequence { Name = new NiStringRef(sname), CycleType = cycle, StartTime = 0, StopTime = 1, Frequency = 1, Weight = 1 };
                seq.ControlledBlocks = keys.Select(k => new ControlledBlock
                {
                    NodeName = new NiStringRef(k.Node), ControllerType = new NiStringRef("NiTransformController"),
                    Interpolator = new(f.AddBlock(new NiTransformInterpolator { Data = new(f.AddBlock(KeyData(k))) })), Controller = new(mtcIndex),
                }).ToList();
                seq.NumControlledBlocks = (uint)seq.ControlledBlocks.Count;
                mgr.ControllerSequences.AddBlockRef(f.AddBlock(seq));
            }
            mgr.NumControllerSequences = (uint)mgr.ControllerSequences.Count;
            mgr.NextController = new(mtcIndex);
            root.Controller = new(mgrIndex);
        }
        if (graph is not null)
        {
            root.ExtraDataList ??= new();
            root.ExtraDataList.AddBlockRef(f.AddBlock(new BSBehaviorGraphExtraData { Name = new NiStringRef("BGED"), BehaviourGraphFile = new NiStringRef(graph) }));
            root.NumExtraDataList = 1;
        }
        return Save(f, "member chain");
    }
}
