using System.Numerics;
using System.Text.Json;
using HousecarlCore;
using NiflySharp;
using NiflySharp.Blocks;
using NiflySharp.Enums;
using NiflySharp.Structs;

/// <summary>Known answers for the shared reader of members under motion (lane M, GATE-A §3: K-M1..K-M13). Synthetic NIFs and
/// behaviour graphs (MotionProof.MemberHkx) go through the production readers; every expected value is analytic, stated beside
/// its case, and each case names the wrong answer it rejects. Fixture controls hold on the base: they prove each fixture
/// discriminates the named wrong answer with code that already exists or with the case's own arithmetic. Every K-case is red at
/// the Gate B base, where <see cref="MemberMotion"/> is a stub that reads as the pair-measure reader does today. The canary is
/// wrong by construction and must stay red.</summary>
static partial class MotionProof
{
    // ---------------- predicate (the consumer side; the test's own) ----------------
    /// <summary>The contact predicate a consumer would supply: crosses when the posed soup crosses or buries itself in the partner
    /// (note "alpha-only" when every crossing pair of tags has a non-opaque side); clear with a certified margin when no surface
    /// lies within delta and neither is inside the other; else open.</summary>
    internal static MemberPredicate Touch(TriangleSoup partner)
    {
        var pMesh = MeshIntersection.PrepareSurfaceMesh(partner, out var why) ?? throw new InvalidOperationException("partner: " + why);
        var pTags = Enumerable.Repeat(SurfaceMaterial.Opaque, partner.TriangleCount).ToArray();
        return (soup, tags, delta) =>
        {
            double d = delta * (1 + 1e-9) + 1e-6;
            var sMesh = MeshIntersection.PrepareSurfaceMesh(soup, out _);
            bool near = sMesh is null || MeshIntersection.MeshesWithin(sMesh, pMesh, d);
            if (!near)
            {
                var v = soup.Vertices[0]; var pv = partner.Vertices[0];
                bool inside = MeshIntersection.PointInSolid(pMesh, v.X, v.Y, v.Z) != 0 || MeshIntersection.PointInSolid(sMesh!, pv.X, pv.Y, pv.Z) != 0;
                return inside ? MemberTest.Crosses() : MemberTest.Clear(d);
            }
            var c = MeshSurface.Measure(soup, tags, partner, pTags, VolumeGeometry.Render);
            if (c.Status != "measured") return MemberTest.Open;
            double cross = c.CrossingLength?.Lower ?? 0, buried = Math.Max(c.AInB?.Inside?.Lower ?? 0, c.BInA?.Inside?.Lower ?? 0);
            if (cross > c.RoundingBound || buried > 0)
            {
                var op = MeshSurface.OpaqueCrossing(c);
                return MemberTest.Crosses(op is { Lower: > 0 } ? null : "alpha-only");
            }
            return MemberTest.Open;
        };
    }

    static readonly Vec3 V0 = new(0, 0, 0);
    static Vector3 Polar(double deg, double r, double z = 0) => new((float)(r * Math.Cos(deg * Math.PI / 180)), (float)(r * Math.Sin(deg * Math.PI / 180)), (float)z);
    static string ShowMD(MemberDecision d) => $"{d.Decision} origin={d.Origin} {d.AnimationId}@{d.Time} B={d.AnimationB}@{d.TimeB} w=[{(d.Weights is null ? "" : string.Join(",", d.Weights.Select(x => x.ToString("0.###"))))}] a=[{(d.Angles is null ? "" : string.Join(",", d.Angles.Select(x => (x * 180 / Math.PI).ToString("0.#"))))}] note={d.Note} label={d.Label} reason={d.Reason} width={d.Width:0.###} evals={d.Evaluations}";
    static string ShowMM(MemberModel m) => $"{m.Status} {m.Reason} mech=[{string.Join(",", m.Mechanisms.Select(x => x.Kind))}] T={m.TransitionSeconds}";

    /// <summary>The angle (degrees) of nlerp(q(a), identity, w) for a rotation of a degrees about one axis (sign-aligned): the
    /// engine's per-bone blend of a node one state turns and the other leaves stored.</summary>
    static double NlerpDeg(double a, double w)
    {
        double h = a * Math.PI / 360;
        return 2 * Math.Atan2((1 - w) * Math.Sin(h), (1 - w) * Math.Cos(h) + w) * 180 / Math.PI;
    }

    // ---------------- fixtures ----------------
    static readonly FxKeys TurnA = new("N1", 0, 60), SlideB = new("N2", X0: 20, X1: 0);
    static Func<string, byte[]?> Files(Dictionary<string, byte[]> f) => p => f.TryGetValue(p, out var b) ? b : null;

    /// <summary>K-M1: root -> N1 -> N2 -> "Part" (cube half <paramref name="half"/> at (50,0,0) in N2). State A plays SeqA (N1 turns
    /// 0 -> 60 deg about Z), state B plays SeqB (N2 slides x 20 -> 0); a 0.25 s blending transition. Shape centre at a blend:
    /// R(theta) (50 + x, 0, 0), theta = nlerp angle of (60 tA, 0, w), x = w (20 - 20 tB).</summary>
    static (byte[] Nif, Func<string, byte[]?> Read) KM1(float half = 1f, string name = "KM1")
    {
        var nif = ChainNif($@"Lab\{name}.hkx", new[] { ("Part", "N2", Cube(50, 0, 0, half), false) },
                           new[] { ("SeqA", new[] { TurnA }, CycleType.CYCLE_CLAMP), ("SeqB", new[] { SlideB }, CycleType.CYCLE_CLAMP) });
        return (nif, Files(GraphFiles(name, new[] { new FxState("A", "sequence", new[] { "SeqA" }), new FxState("B", "sequence", new[] { "SeqB" }) })));
    }

    /// <summary>The K-M1 blend partner: the shape centre at w = 0.5, tA = 1 (60 deg), tB = 0 (x = 20): R(30)(60,0,0).</summary>
    static readonly Vector3 KM1P1 = Polar(30, 60);
    /// <summary>The K-M1 partner at w = 0.3, tA = 1, tB = 0: R(nlerp(60, 0.3))(56, 0, 0).</summary>
    static Vector3 KM1P1b => Polar(NlerpDeg(60, 0.3), 56);

    /// <summary>Boundary of the union of K-M1 shape centres over (tA, tB, w) in polar (theta, r): theta &lt;= thetaMax(r), r in
    /// [50, 70], thetaMax(r) = the larger of the nlerp and slerp angles at w = (r - 50) / 20 (the case holds under either).</summary>
    static double ThetaMax(double r) { double w = Math.Clamp((r - 50) / 20, 0, 1); return Math.Max(NlerpDeg(60, w), 60 * (1 - w)); }
    static IEnumerable<Vector3> KM1UnionBoundary()
    {
        for (int k = 0; k <= 20000; k++) { double r = 50 + 20.0 * k / 20000; yield return Polar(ThetaMax(r), r); yield return Polar(0, r); }
        for (int k = 0; k <= 20000; k++) { yield return Polar(60.0 * k / 20000, 50); yield return Polar(ThetaMax(70) * k / 20000, 70); }
    }
    static double BoxDistance(Vector3 p, Vector3 c, float half)
    {
        var d = Vector3.Max(Vector3.Abs(p - c) - new Vector3(half), Vector3.Zero);
        return d.Length();
    }

    /// <summary>K-M2 partner (cube half 0.5): outside the union of shape centres along the outward normal at the w = 0.5 boundary
    /// point, so that the posed tiny cube (half 0.05) lies between 2 delta (1.0) and 1.6 u from it.</summary>
    static Vector3 KM2P2
    {
        get
        {
            Vector3 B(double w) => Polar(ThetaMax(50 + 20 * w), 50 + 20 * w);
            var t = B(0.51) - B(0.49); var n = Vector3.Normalize(new Vector3(t.Y, -t.X, 0));
            if (Vector3.Dot(n, B(0.5)) < 0) n = -n;
            return B(0.5) + n * (1.2f + 0.5f * (MathF.Abs(n.X) + MathF.Abs(n.Y)) + 0.09f);
        }
    }

    /// <summary>K-M3: N1 holds +85 deg about Z in state A and -85 deg in state B, B's keys stored with the opposite sign
    /// (quaternions 170 deg apart, dot &lt; 0 as stored); "Part" cube half 1 at (50,0,0) in N1.</summary>
    static (byte[] Nif, Func<string, byte[]?> Read) KM3()
    {
        var nif = ChainNif(@"Lab\KM3.hkx", new[] { ("Part", "N1", Cube(50, 0, 0, 1), false) },
                           new[] { ("SeqA", new[] { new FxKeys("N1", 85, 85) }, CycleType.CYCLE_CLAMP),
                                   ("SeqB", new[] { new FxKeys("N1", -85, -85, Negate: true) }, CycleType.CYCLE_CLAMP) });
        return (nif, Files(GraphFiles("KM3", new[] { new FxState("A", "sequence", new[] { "SeqA" }), new FxState("B", "sequence", new[] { "SeqB" }) })));
    }

    /// <summary>K-M5: the K-M1 chain with an opaque "Lid" (cube half 1 at (30,0,0) in N1) and an alpha-blended "Glow" (cube half 1 at
    /// (50,0,0) in N2): the forge's shape.</summary>
    static (byte[] Nif, Func<string, byte[]?> Read) KM5()
    {
        var nif = ChainNif(@"Lab\KM5.hkx", new[] { ("Lid", "N1", Cube(30, 0, 0, 1), false), ("Glow", "N2", Cube(50, 0, 0, 1), true) },
                           new[] { ("SeqA", new[] { TurnA }, CycleType.CYCLE_CLAMP), ("SeqB", new[] { SlideB }, CycleType.CYCLE_CLAMP) });
        return (nif, Files(GraphFiles("KM5", new[] { new FxState("A", "sequence", new[] { "SeqA" }), new FxState("B", "sequence", new[] { "SeqB" }) })));
    }

    /// <summary>K-M6: one state whose generator is an hkbBlenderGenerator over the named sequences (SeqA, SeqB as K-M1; SeqC turns
    /// N1 0 -> -40 deg; SeqD slides N2 0 -> 10).</summary>
    static (byte[] Nif, Func<string, byte[]?> Read) KM6(string name, string[] children, bool subtract = false)
    {
        var nif = ChainNif($@"Lab\{name}.hkx", new[] { ("Part", "N2", Cube(50, 0, 0, 1), false) },
                           new[] { ("SeqA", new[] { TurnA }, CycleType.CYCLE_CLAMP), ("SeqB", new[] { SlideB }, CycleType.CYCLE_CLAMP),
                                   ("SeqC", new[] { new FxKeys("N1", 0, -40) }, CycleType.CYCLE_CLAMP),
                                   ("SeqD", new[] { new FxKeys("N2", X0: 0, X1: 10) }, CycleType.CYCLE_CLAMP) });
        return (nif, Files(GraphFiles(name, new[] { new FxState("Blend", "blender", children, subtract) })));
    }

    /// <summary>K-M10: "Part" (cube half 1 at (50,0,0) in N1, N1 stored at identity); one state, an hkbReferencePoseGenerator; the
    /// skeleton's reference pose turns N1 +90 deg about Z.</summary>
    static (byte[] Nif, Func<string, byte[]?> Read) KM10()
    {
        var nif = ChainNif(@"Lab\KM10.hkx", new[] { ("Part", "N1", Cube(50, 0, 0, 1), false) }, Array.Empty<(string, FxKeys[], CycleType)>());
        return (nif, Files(GraphFiles("KM10", new[] { new FxState("Idle", "reference", Array.Empty<string>()) },
                                      skeleton: new[] { ("N1", Quaternion.CreateFromAxisAngle(Vector3.UnitZ, Pi / 2), Vector3.Zero) })));
    }

    /// <summary>K-M11: one node, two states at disjoint angle ranges about Z: SeqA 0 -> 10 deg, SeqB 50 -> 60 deg; "Part" cube half 1
    /// at (50,0,0) in N1.</summary>
    static (byte[] Nif, Func<string, byte[]?> Read) KM11()
    {
        var nif = ChainNif(@"Lab\KM11.hkx", new[] { ("Part", "N1", Cube(50, 0, 0, 1), false) },
                           new[] { ("SeqA", new[] { new FxKeys("N1", 0, 10) }, CycleType.CYCLE_CLAMP), ("SeqB", new[] { new FxKeys("N1", 50, 60) }, CycleType.CYCLE_CLAMP) });
        return (nif, Files(GraphFiles("KM11", new[] { new FxState("A", "sequence", new[] { "SeqA" }), new FxState("B", "sequence", new[] { "SeqB" }) })));
    }

    /// <summary>K-M12: tier 1 (no graph): sequence "Turn" turns N1 0 -> 90 deg about Z over [0, 1] (two keys); "Part" cube half 0.5 at
    /// (50,0,0) in N1.</summary>
    static byte[] KM12() => ChainNif(null, new[] { ("Part", "N1", Cube(50, 0, 0, 0.5f), false) },
                                     new[] { ("Turn", new[] { new FxKeys("N1", 0, 90) }, CycleType.CYCLE_CLAMP) });

    /// <summary>K-M7b: <see cref="HingeRod"/> with limits [-30, 45] and the anchor's zero direction turned 20 deg about the axis, so the
    /// stored angle is 20 deg from the constraint's zero (either sense: offsets from the stored pose [-50, 25] or [-25, 50]).</summary>
    static byte[] HingeRodOffset()
    {
        var f = Load(HingeRod(0f, -30, 45));
        var cons = f.Blocks.OfType<bhkLimitedHingeConstraint>().Single();
        var c = cons.Constraint; float a = 20 * Pi / 180;
        c.PerpAxisInB1 = new Vector4(MathF.Cos(a), MathF.Sin(a), 0, 0); c.PerpAxisInB2 = new Vector4(-MathF.Sin(a), MathF.Cos(a), 0, 0);
        cons.Constraint = c;
        return Save(f, "hinge rod offset");
    }

    /// <summary>K-M8: an immovable anchor at the origin; "Upper" (render and movable body [0,50]x[-1,1]x[-1,1]) on a limited hinge
    /// about +Z through the origin, limits +-30 deg; "Lower" ([50,100]x[-1,1]x[-1,1]) on a limited hinge about +Z through (50,0,0) to
    /// Upper, +-30 deg. All nodes at identity under the root. Tip at both +30 deg: R30((50,0,0) + R30(50,0,0)) = (68.3, 68.3, 0).</summary>
    static byte[] HingeChain()
    {
        var f = new NifFile(); f.Create(Ver, withRootNode: true);
        var root = f.GetRootNodes().First(); root.Flags_ui = 0xE;
        NiNode Node(string n) { var x = new NiNode { Name = new NiStringRef(n), Flags_ui = 0xE, Scale = 1f, Rotation = Identity33() }; root.Children.AddBlockRef(f.AddBlock(x)); return x; }
        var anchor = Node("Anchor"); var upper = Node("Upper"); var lower = Node("Lower");
        upper.Children.AddBlockRef(f.AddBlock(Shape("UpperMesh", Box(0, -1, -1, 50, 1, 1))));
        lower.Children.AddBlockRef(f.AddBlock(Shape("LowerMesh", Box(50, -1, -1, 100, 1, 1))));
        int ab = AddBody(f, anchor, ConvexBox(-1, -1, -1, 1, 1, 1), 0f, hkMotionType.MO_SYS_BOX_STABILIZED, SkyrimLayer.SKYL_STATIC);
        int ub = AddBody(f, upper, ConvexBox(0, -1, -1, 50, 1, 1), 5f, hkMotionType.MO_SYS_SPHERE_STABILIZED, SkyrimLayer.SKYL_CLUTTER);
        int lb = AddBody(f, lower, ConvexBox(50, -1, -1, 100, 1, 1), 5f, hkMotionType.MO_SYS_SPHERE_STABILIZED, SkyrimLayer.SKYL_CLUTTER);
        void Hinge(int a, int b, float pivotX)
        {
            var cons = new bhkLimitedHingeConstraint(); var c = cons.Constraint;
            c.PivotA = new Vector4(pivotX / HavokU, 0, 0, 0); c.AxisA = new Vector4(0, 0, 1, 0); c.PerpAxisInA1 = new Vector4(1, 0, 0, 0); c.PerpAxisInA2 = new Vector4(0, 1, 0, 0);
            c.PivotB = new Vector4(pivotX / HavokU, 0, 0, 0); c.AxisB = new Vector4(0, 0, 1, 0); c.PerpAxisInB1 = new Vector4(1, 0, 0, 0); c.PerpAxisInB2 = new Vector4(0, 1, 0, 0);
            c.MinAngle = -30 * Pi / 180; c.MaxAngle = 30 * Pi / 180; cons.Constraint = c;
            var ci = cons.ConstraintInfo; ci.NumEntities = 2; ci.EntityA = new(a); ci.EntityB = new(b); cons.ConstraintInfo = ci;
            int idx = f.AddBlock(cons);
            var rb = (bhkRigidBody)f.Blocks[a]; rb.Constraints ??= new(); rb.Constraints.AddBlockRef(idx); rb.NumConstraints = (uint)rb.Constraints.Count;
        }
        Hinge(ub, ab, 0); Hinge(lb, ub, 50);
        return Save(f, "hinge chain");
    }

    static readonly MotionPlacement Mstt = new("MSTT", false), BookBase = new("BOOK", false), MiscBase = new("MISC", false);

    static MemberDecision DecideAt(byte[] nif, Func<string, byte[]?>? read, MotionPlacement? placement, TriangleSoup partner, MemberSweep? sweep = null)
    {
        var m = MemberMotion.Read(nif, read, placement);
        return MemberMotion.Decide(nif, m, VolumeGeometry.Render, V0, V0, 1f, sweep ?? new MemberSweep(), Touch(partner), read);
    }

    public static void RunMemberMotion(Action<string, bool, string> check)
    {
        // ================= canary (wrong by construction; must stay RED) =================
        Case(check, "member-motion CANARY (must FAIL): K-M1's blend pose at w = 0.5 put at 31 deg", () =>
        {
            double a = NlerpDeg(60, 0.5);
            return (Math.Abs(a - 31) < 1e-6, $"nlerp angle {a:0.######}");
        });

        // ================= fixture controls (hold on the base) =================
        Case(check, "K-M fixture control: the packfile writer's graphs resolve through HavokBehaviour (K-M1: 2 sequence states, the transition class listed; K-M10: skeleton bone N1 with a +90 deg reference pose)", () =>
        {
            var (_, r1) = KM1();
            var g = HavokBehaviour.Resolve(@"Lab\KM1.hkx", r1);
            var (_, r10) = KM10();
            var g10 = HavokBehaviour.Resolve(@"Lab\KM10.hkx", r10, new GraphOptions(true, true, true));
            var sk = HavokAnimation.ReadSkeleton(r10(@"meshes\Lab\KM10Skel.hkx")!);
            double refDeg = 2 * Math.Atan2(sk.ReferencePose[0].Rotation.Z, sk.ReferencePose[0].Rotation.W) * 180 / Math.PI;
            bool ok = g.Status == "read" && g.States.Count == 2 && g.States.All(s => s.GeneratorKind == "sequence") && g.Classes.Contains("hkbBlendingTransitionEffect")
                      && g10.Status == "read" && g10.ReferencePoses is { Count: 1 } && sk.Bones.SequenceEqual(new[] { "N1" }) && Math.Abs(refDeg - 90) < 1e-3;
            return (ok, $"KM1 {g.Status} {g.Reason} states {g.States.Count} [{string.Join(",", g.Classes)}]; KM10 {g10.Status} {g10.Reason} refposes {g10.ReferencePoses?.Count} bones [{string.Join(",", sk.Bones)}] ref {refDeg:0.###}");
        });
        Case(check, "K-M fixture control: blender graphs resolve under the existing Blend option (2 and 4 sequence children read) and subtractLastChild refuses by name", () =>
        {
            var go = new GraphOptions(true, true, true, Blend: true);
            var g2 = HavokBehaviour.Resolve(@"Lab\KM6b2.hkx", KM6("KM6b2", new[] { "SeqA", "SeqB" }).Read, go);
            var g4 = HavokBehaviour.Resolve(@"Lab\KM6b4.hkx", KM6("KM6b4", new[] { "SeqA", "SeqB", "SeqC", "SeqD" }).Read, go);
            var gs = HavokBehaviour.Resolve(@"Lab\KM6bs.hkx", KM6("KM6bs", new[] { "SeqA", "SeqB" }, subtract: true).Read, go);
            return (g2.Status == "read" && g2.Sequences.Count == 2 && g4.Status == "read" && g4.Sequences.Count == 4 && gs.Status == "refused" && (gs.Reason ?? "").Contains("subtracts its last child"),
                    $"2: {g2.Status} {g2.Sequences.Count}; 4: {g4.Status} {g4.Sequences.Count}; sub: {gs.Status} {gs.Reason}");
        });
        Case(check, "K-M1 fixture control: today's clip-only swept region (the reach reading with the existing Blend option) reads 'never' at the w = 0.5 and w = 0.3 partners, and both lie > 3 u from every one-clock blend (the named wrong answers are what existing code gives)", () =>
        {
            var (nif, read) = KM1();
            var m = NifMotion.Read(nif, read, null, new MotionReadOptions(Graph: new GraphOptions(true, true, true, Blend: true)));
            var reg = SweptGeometry.Build(nif, m, null, VolumeGeometry.Render, new MotionOptions(), read);
            var d1 = SweptGeometry.Contact(reg, Cube(KM1P1.X, KM1P1.Y, 0, 1.5f), null, true);
            var d1b = SweptGeometry.Contact(reg, Cube(KM1P1b.X, KM1P1b.Y, 0, 1.5f), null, true);
            double oneClock = double.MaxValue, oneClockB = double.MaxValue;
            for (int i = 0; i <= 400; i++) for (int j = 0; j <= 400; j++)
            {
                double t = i / 400.0, w = j / 400.0;
                var c = Polar(NlerpDeg(60 * t, w), 50 + w * 20 * (1 - t));
                oneClock = Math.Min(oneClock, Vector3.Distance(c, KM1P1)); oneClockB = Math.Min(oneClockB, Vector3.Distance(c, KM1P1b));
            }
            return (m.Status == "moving" && d1.Decision == "never" && d1b.Decision == "never" && oneClock > 3 && oneClockB > 1.5,
                    $"{m.Status} {m.Reason}; P1 {d1.Decision}; P1b {d1b.Decision}; one-clock nearest centre to P1 {oneClock:0.##} u, to P1b {oneClockB:0.##} u");
        });
        Case(check, "K-M2/K-M4 fixture control: the partners lie outside the union of shape centres over (tA, tB, w) (nlerp and slerp) — K-M2 by 1.0..1.6 u, K-M4 by > 3 u — and inside the per-node product box the chord reader and per-node weights use", () =>
        {
            var p2 = KM2P2; var p4 = Polar(58, 69);
            double d2lo = KM1UnionBoundary().Min(b => BoxDistance(b, p2, 0.5f)) - 0.05 * Math.Sqrt(3);
            double d4 = KM1UnionBoundary().Min(b => BoxDistance(b, p4, 1f)) - 0.05 * Math.Sqrt(3);
            double th2 = Math.Atan2(p2.Y, p2.X) * 180 / Math.PI, r2 = p2.Length();
            // the existing blend envelope (BlendBox, the reach rule) holds it: a box-only reader cannot say never there
            var (nif, read) = KM1(0.05f, "KM2");
            var m = NifMotion.Read(nif, read, null, new MotionReadOptions(Graph: new GraphOptions(true, true, true, Blend: true)));
            var (bb, lab, why) = NifMotion.BlendBox(nif, m, read, 0.5f);
            bool inBox = bb.Min.X <= p2.X && p2.X <= bb.Max.X && bb.Min.Y <= p2.Y && p2.Y <= bb.Max.Y;
            return (d2lo >= 1.0 && d2lo <= 1.6 && d4 > 3 && th2 > 1 && th2 < 59 && r2 > 51 && r2 < 69 && inBox,
                    $"K-M2 partner {S(p2)} polar ({th2:0.##} deg, {r2:0.##}) gap {d2lo:0.###}; K-M4 gap {d4:0.##}; BlendBox {ShowA7(bb)} {lab} {why} holds it {inBox}");
        });
        Case(check, "K-M3 fixture control: as stored (dot < 0), nlerp without sign alignment passes through 180 deg at w = 0.5 and with alignment through 0 deg", () =>
        {
            var qa = Quaternion.CreateFromAxisAngle(Vector3.UnitZ, 85 * Pi / 180); var qb = Quaternion.Negate(Quaternion.CreateFromAxisAngle(Vector3.UnitZ, -85 * Pi / 180));
            var raw = Quaternion.Normalize(Quaternion.Add(Quaternion.Multiply(qa, 0.5f), Quaternion.Multiply(qb, 0.5f)));
            var al = Quaternion.Normalize(Quaternion.Add(Quaternion.Multiply(qa, 0.5f), Quaternion.Multiply(Quaternion.Negate(qb), 0.5f)));
            var pr = Vector3.Transform(new Vector3(50, 0, 0), raw); var pa = Vector3.Transform(new Vector3(50, 0, 0), al);
            return (Quaternion.Dot(qa, qb) < 0 && NearV(pr, new Vector3(-50, 0, 0), 1e-2f) && NearV(pa, new Vector3(50, 0, 0), 1e-2f), $"dot {Quaternion.Dot(qa, qb):0.###} unaligned {S(pr)} aligned {S(pa)}");
        });
        Case(check, "K-M5 fixture control: the shape materials read 'Lid' opaque and 'Glow' alpha_blend (the existing material reader)", () =>
        {
            var mats = MeshVolume.ShapeMaterials(KM5().Nif);
            return (mats is { Count: 2 } && mats.Single(x => x.Name == "Lid").Class == SurfaceMaterial.Opaque && mats.Single(x => x.Name == "Glow").Class == SurfaceMaterial.AlphaBlend,
                    string.Join(", ", (mats ?? new()).Select(x => $"{x.Name}:{x.Class}")));
        });
        Case(check, "K-M7/K-M8/K-M9 fixture control: the hinge fixtures classify (rod, offset rod and two-hinge chain pinned; rod with a movable anchor free-chain) and the existing hinge envelope reads the offset rod's 50 deg reach", () =>
        {
            var k1 = PhysicsReach.Classify(HingeRod(0f, -30, 45)).Kind; var k2 = PhysicsReach.Classify(HingeRodOffset()).Kind;
            var k3 = PhysicsReach.Classify(HingeChain()); var k4 = PhysicsReach.Classify(HingeRod(5f, -30, 30)).Kind;
            var nif = HingeRodOffset();
            var env = PhysicsReach.HingeEnvelope(nif, NifMotion.Read(nif, null, null, new MotionReadOptions(IgnorePhysics: true)), V0, V0, 1f, 0.5, 65536, null);
            return (k1 == "pinned" && k2 == "pinned" && k3.Kind == "pinned" && k3.Constraints.Count == 2 && k4 == "free-chain" && env.Refusal is null && env.Box.Max.Y > 76 && env.Box.Max.Y < 78,
                    $"{k1} {k2} {k3.Kind}/{k3.Constraints.Count} {k4}; offset envelope {ShowA7(env.Box)} {env.Refusal}");
        });

        // ================= K-M1: transition blend on a two-node chain =================
        Case(check, "K-M1 two-state blend, partner at the w = 0.5 pose (tA = 1, tB = 0): witness, origin blend, labelled a minor transient of at most 0.25 s (rejects clip poses only: never; one clock: never)", () =>
        {
            var (nif, read) = KM1();
            var d = DecideAt(nif, read, Mstt, Cube(KM1P1.X, KM1P1.Y, 0, 1.5f));
            return (d.Decision == "witness" && d.Origin == MemberMotion.OriginBlend && (d.Label ?? "").Contains("transient") && (d.Label ?? "").Contains("minor") && (d.Label ?? "").Contains("0.25 s"), ShowMD(d));
        });
        Case(check, "K-M1b partner at the w = 0.3 pose: witness, origin blend (rejects a bound without the w-cell chord: never)", () =>
        {
            var (nif, read) = KM1();
            var d = DecideAt(nif, read, Mstt, Cube(KM1P1b.X, KM1P1b.Y, 0, 1.5f));
            return (d.Decision == "witness" && d.Origin == MemberMotion.OriginBlend, ShowMD(d));
        });
        // Gate C fix (disclosed in RESULT.md): at Gate B this case ran with a 4-evaluation budget, which this loop spends on the clip
        // cells before any blend cell, so no under-bound blend cell could be certified; it runs with the default budget.
        Case(check, "K-M1c w = 0.3 partner with the default budget and a 1000 u initial target: witness or cannot_tell, never 'never' (rejects an under-bound w cell certified clear)", () =>
        {
            var (nif, read) = KM1();
            var d = DecideAt(nif, read, Mstt, Cube(KM1P1b.X, KM1P1b.Y, 0, 1.5f), new MemberSweep(1000, 4096));
            return (d.Decision is "witness" or "cannot_tell", ShowMD(d));
        });
        Case(check, "K-M1 model: moving, the 'two or more animated nodes' refusal gone, mechanisms clip + blend, transition 0.25 s read from the file", () =>
        {
            var (nif, read) = KM1();
            var m = MemberMotion.Read(nif, read, Mstt);
            return (m.Status == "moving" && m.Mechanisms.Any(x => x.Kind == MemberMotion.KindBlend) && m.TransitionSeconds is { } s && Math.Abs(s - 0.25) < 1e-6, ShowMM(m));
        });
        // ================= K-M2 / K-M4: tightness and one weight for every node =================
        Case(check, "K-M2 partner 1.0..1.6 u outside the union over (tA, tB, w), budget 65536: never (rejects a chord-box-only reader: cannot_tell)", () =>
        {
            var (nif, read) = KM1(0.05f, "KM2");
            var d = DecideAt(nif, read, Mstt, Cube(KM2P2.X, KM2P2.Y, 0, 0.5f), new MemberSweep(0.5, 65536));
            return (d.Decision == "never", ShowMD(d));
        });
        Case(check, "K-M4 partner at the (58 deg, x 19) corner, reached only with a different weight per node: never (rejects per-node w: a false witness)", () =>
        {
            var (nif, read) = KM1(0.05f, "KM4");
            var p = Polar(58, 69);
            var d = DecideAt(nif, read, Mstt, Cube(p.X, p.Y, 0, 1f));
            return (d.Decision == "never", ShowMD(d));
        });
        // ================= K-M3: sign alignment =================
        Case(check, "K-M3 quaternions 170 deg apart stored with opposite signs: partner on the short arc (0 deg) witness origin blend; partner on the long arc (180 deg) never (rejects no sign alignment)", () =>
        {
            var (nif, read) = KM3();
            var near = DecideAt(nif, read, Mstt, Cube(50, 0, 0, 1.5f)); var far = DecideAt(nif, read, Mstt, Cube(-50, 0, 0, 1.5f));
            return (near.Decision == "witness" && near.Origin == MemberMotion.OriginBlend && far.Decision == "never", $"short: {ShowMD(near)} | long: {ShowMD(far)}");
        });
        Case(check, "K-M3b still poses at +90 and -90 deg about one axis (quaternion product within float noise of 0): partners on both arcs (0 and 180 deg) are blend witnesses (Gate C review: the engine's own float product can take either sign; rejects a sign fixed from our arithmetic: never on one arc)", () =>
        {
            var nif = ChainNif(@"Lab\KM3b.hkx", new[] { ("Part", "N1", Cube(50, 0, 0, 1), false) },
                               new[] { ("SeqA", new[] { new FxKeys("N1", 90, 90) }, CycleType.CYCLE_CLAMP), ("SeqB", new[] { new FxKeys("N1", -90, -90) }, CycleType.CYCLE_CLAMP) });
            var read = Files(GraphFiles("KM3b", new[] { new FxState("A", "sequence", new[] { "SeqA" }), new FxState("B", "sequence", new[] { "SeqB" }) }));
            var a = DecideAt(nif, read, Mstt, Cube(50, 0, 0, 1.5f)); var b = DecideAt(nif, read, Mstt, Cube(-50, 0, 0, 1.5f));
            return (a.Decision == "witness" && a.Origin == MemberMotion.OriginBlend && b.Decision == "witness" && b.Origin == MemberMotion.OriginBlend, $"0 deg: {ShowMD(a)} | 180 deg: {ShowMD(b)}");
        });
        // ================= K-M5: the forge's shape =================
        Case(check, "K-M5 opaque lid + alpha glow on a two-node chain: moving; an opaque partner on the lid's sweep is a witness; a partner crossing only the glow's blend pose is a witness noted alpha-only (rejects the whole-model refusal; the glow dropped)", () =>
        {
            var (nif, read) = KM5();
            var m = MemberMotion.Read(nif, read, Mstt);
            var lid = Polar(40, 30); var dl = DecideAt(nif, read, Mstt, Cube(lid.X, lid.Y, 0, 1.5f));
            var dg = DecideAt(nif, read, Mstt, Cube(KM1P1.X, KM1P1.Y, 0, 1.5f));
            return (m.Status == "moving" && dl.Decision == "witness" && dl.Note is null && dg.Decision == "witness" && dg.Note == "alpha-only",
                    $"{ShowMM(m)} | lid: {ShowMD(dl)} | glow: {ShowMD(dg)}");
        });
        // ================= K-M6: hkbBlenderGenerator =================
        Case(check, "K-M6 blender with 2 children: measured; partner at the K-M1 w = 0.5 pose witness origin blend, mechanism blender (rejects the class refused)", () =>
        {
            var (nif, read) = KM6("KM6b2", new[] { "SeqA", "SeqB" });
            var m = MemberMotion.Read(nif, read, Mstt);
            var d = MemberMotion.Decide(nif, m, VolumeGeometry.Render, V0, V0, 1f, new MemberSweep(), Touch(Cube(KM1P1.X, KM1P1.Y, 0, 1.5f)), read);
            return (m.Status == "moving" && m.Mechanisms.Any(x => x.Kind == MemberMotion.KindBlender) && d.Decision == "witness" && d.Origin == MemberMotion.OriginBlend, $"{ShowMM(m)} | {ShowMD(d)}");
        });
        Case(check, "K-M6 blender with 3 children: measured (moving, the partner decided, not refused)", () =>
        {
            var (nif, read) = KM6("KM6b3", new[] { "SeqA", "SeqB", "SeqC" });
            var m = MemberMotion.Read(nif, read, Mstt);
            var d = MemberMotion.Decide(nif, m, VolumeGeometry.Render, V0, V0, 1f, new MemberSweep(), Touch(Cube(KM1P1.X, KM1P1.Y, 0, 1.5f)), read);
            return (m.Status == "moving" && d.Decision == "witness", $"{ShowMM(m)} | {ShowMD(d)}");
        });
        Case(check, "K-M6 blender with 4 children: refused by name ('blender generator 'Blend0' has 4 children')", () =>
        {
            var (nif, read) = KM6("KM6b4", new[] { "SeqA", "SeqB", "SeqC", "SeqD" });
            var m = MemberMotion.Read(nif, read, Mstt);
            return (m.Status == "refused" && (m.Reason ?? "").Contains("blender generator 'Blend0' has 4 children"), ShowMM(m));
        });
        Case(check, "K-M6 blender with subtractLastChild: refused by name ('subtracts its last child') (rejects an additive blend treated as convex)", () =>
        {
            var (nif, read) = KM6("KM6bs", new[] { "SeqA", "SeqB" }, subtract: true);
            var m = MemberMotion.Read(nif, read, Mstt);
            return (m.Status == "refused" && (m.Reason ?? "").Contains("subtracts its last child"), ShowMM(m));
        });
        // ================= kept refusals (GATE-A §6) =================
        Case(check, "K-M14 kept refusal: a graph holding an hkbBoneWeightArray refuses by name ('hkbBoneWeightArray') (rejects per-bone weights read as one w)", () =>
        {
            var nif = ChainNif(@"Lab\KM14w.hkx", new[] { ("Part", "N2", Cube(50, 0, 0, 1), false) },
                               new[] { ("SeqA", new[] { TurnA }, CycleType.CYCLE_CLAMP), ("SeqB", new[] { SlideB }, CycleType.CYCLE_CLAMP) });
            var read = Files(GraphFiles("KM14w", new[] { new FxState("A", "sequence", new[] { "SeqA" }), new FxState("B", "sequence", new[] { "SeqB" }) }, boneWeights: true));
            var m = MemberMotion.Read(nif, read, Mstt);
            return (m.Status == "refused" && (m.Reason ?? "").Contains("hkbBoneWeightArray"), ShowMM(m));
        });
        Case(check, "K-M14 kept refusal: a transition effect other than hkbBlendingTransitionEffect refuses by name ('hkbGeneratorTransitionEffect') (rejects an unread transition read as a blend)", () =>
        {
            var nif = ChainNif(@"Lab\KM14t.hkx", new[] { ("Part", "N2", Cube(50, 0, 0, 1), false) },
                               new[] { ("SeqA", new[] { TurnA }, CycleType.CYCLE_CLAMP), ("SeqB", new[] { SlideB }, CycleType.CYCLE_CLAMP) });
            var read = Files(GraphFiles("KM14t", new[] { new FxState("A", "sequence", new[] { "SeqA" }), new FxState("B", "sequence", new[] { "SeqB" }) },
                                        transitionClass: "hkbGeneratorTransitionEffect"));
            var m = MemberMotion.Read(nif, read, Mstt);
            return (m.Status == "refused" && (m.Reason ?? "").Contains("hkbGeneratorTransitionEffect"), ShowMM(m));
        });
        // ================= K-M7 / K-M7b: pinned single hinge =================
        Case(check, "K-M7 pinned rod, limits [-30, +45], stored 0: partners at +40 and -40 deg witness origin swing; at +55 and -55 never (rejects one sense only)", () =>
        {
            var nif = HingeRod(0f, -30, 45);
            MemberDecision At(double deg) { var p = Polar(deg, 80); return DecideAt(nif, null, Mstt, Cube(p.X, p.Y, 0, 2f)); }
            var a = At(40); var b = At(-40); var c = At(55); var d = At(-55);
            return (a.Decision == "witness" && a.Origin == MemberMotion.OriginSwing && a.Angles is { Count: 1 } && b.Decision == "witness" && b.Origin == MemberMotion.OriginSwing
                    && c.Decision == "never" && d.Decision == "never", $"+40 {ShowMD(a)} | -40 {ShowMD(b)} | +55 {c.Decision} | -55 {d.Decision}");
        });
        Case(check, "K-M7b offset rod (stored 20 deg from the constraint's zero, limits [-30, +45]): partner at +48 deg from the stored pose witness; at +55 never (rejects the stored-angle offset dropped)", () =>
        {
            var nif = HingeRodOffset();
            var p = Polar(48, 80); var q = Polar(55, 80);
            var a = DecideAt(nif, null, Mstt, Cube(p.X, p.Y, 0, 2f)); var c = DecideAt(nif, null, Mstt, Cube(q.X, q.Y, 0, 2f));
            return (a.Decision == "witness" && a.Origin == MemberMotion.OriginSwing && c.Decision == "never", $"+48 {ShowMD(a)} | +55 {ShowMD(c)}");
        });
        Case(check, "K-M7 meaning (P2): a swing witness reads 'can rest against when disturbed' against a colliding partner and a visible clip against a render-only one", () =>
            (MemberMotion.SwingMeaning(true) == "can rest against when disturbed" && MemberMotion.SwingMeaning(false).Contains("visible clip")
             && MemberMotion.Read(HingeRod(0f, -30, 45), null, Mstt).Mechanisms.Any(x => x.Kind == MemberMotion.KindHinge),
             $"{MemberMotion.SwingMeaning(true)} / {MemberMotion.SwingMeaning(false)} / {ShowMM(MemberMotion.Read(HingeRod(0f, -30, 45), null, Mstt))}"));
        Case(check, "K-M7c pinned rod whose hinged node also carries an active transform controller (the rabbits' shape): refused naming 'pinned body's node is also animated' (Gate B review decision 1: rejects a region that takes the hinge or the controller alone)", () =>
        {
            var f = Load(HingeRod(0f, -30, 45));
            var rod = f.Blocks.OfType<NiNode>().Single(n => n.Name?.String == "Rod");
            int ri = f.Blocks.IndexOf(rod);
            var ctl = new NiTransformController { Interpolator = new(f.AddBlock(new NiTransformInterpolator { Data = new(f.AddBlock(KeyData(new FxKeys("Rod", 0, 15)))) })),
                                                  StartTime = 0, StopTime = 1, Frequency = 1 };
            ctl.Flags.Active = true; ctl.Target = new(ri); rod.Controller = new(f.AddBlock(ctl));
            var m = MemberMotion.Read(Save(f, "animated hinge rod"), null, Mstt);
            return (m.Status == "refused" && (m.Reason ?? "").Contains("pinned body's node is also animated"), ShowMM(m));
        });
        // ================= K-M8: two hinges =================
        Case(check, "K-M8 two-hinge chain: a partner at the limb tip reached only with both hinges at +30 deg: witness origin swing (rejects the child turned about its stored pivot: never)", () =>
        {
            var nif = HingeChain();
            var d = DecideAt(nif, null, Mstt, Cube(67.55f, 67.0f, 0, 1f));
            return (d.Decision == "witness" && d.Origin == MemberMotion.OriginSwing && d.Angles is { Count: 2 }, ShowMD(d));
        });
        // Gate C fix (disclosed in RESULT.md): at Gate B this case used K-M8's partner at a limit corner, which this loop poses exactly,
        // and a 3-evaluation budget; it now uses a partner reached only inside the limits, with the default budget.
        Case(check, "K-M8b a partner at the limb tip with both hinges at +28 deg, inside the limits and more than the under-bound's reach (52 u) from the stored pose: witness or cannot_tell, never 'never' (rejects a Lipschitz bound without the pivot-to-pivot terms certifying a cell clear)", () =>
        {
            var nif = HingeChain();
            var tip = Vector3.Transform(new Vector3(50, 0, 0) + Vector3.Transform(new Vector3(50, 0, 0), Quaternion.CreateFromAxisAngle(Vector3.UnitZ, 28 * Pi / 180)),
                                        Quaternion.CreateFromAxisAngle(Vector3.UnitZ, 28 * Pi / 180));
            var d = DecideAt(nif, null, Mstt, Cube(tip.X, tip.Y, 0, 2f), new MemberSweep(1000, 4096));
            return (d.Decision is "witness" or "cannot_tell", ShowMD(d));
        });
        // ================= K-M9: books =================
        Case(check, "K-M9 free-chain hinge on a BOOK base: the closed stored pose, origin rest, labelled 'rest: closed (modelling rule)'; a partner only a swing would reach: never (rejects a hinge envelope on a free island)", () =>
        {
            var nif = HingeRod(5f, -30, 30);
            var m = MemberMotion.Read(nif, null, BookBase);
            var stored = DecideAt(nif, null, BookBase, Cube(80, 0, 0, 2f)); var p = Polar(25, 80); var swung = DecideAt(nif, null, BookBase, Cube(p.X, p.Y, 0, 2f));
            return (m.Status != "refused" && m.Mechanisms.Any(x => x.Kind == MemberMotion.KindBookClosed) && stored.Decision == "witness" && stored.Origin == MemberMotion.OriginRest
                    && (stored.Label ?? "").Contains(MeshReach.BookClosedLabel) && swung.Decision == "never", $"{ShowMM(m)} | stored {ShowMD(stored)} | swung {ShowMD(swung)}");
        });
        Case(check, "K-M9b the same free chain on a MISC base: refused by name ('free-chain')", () =>
        {
            var m = MemberMotion.Read(HingeRod(5f, -30, 30), null, MiscBase);
            return (m.Status == "refused" && (m.Reason ?? "").Contains("free-chain"), ShowMM(m));
        });
        // ================= K-M10: reference pose =================
        Case(check, "K-M10 reference-pose generator (+90 deg) vs stored locals (0): partner at the reference pose witness origin reference; at the stored pose witness origin rest; at 45 deg never (rejects reference only; stored only)", () =>
        {
            var (nif, read) = KM10();
            var r = DecideAt(nif, read, Mstt, Cube(0, 50, 0, 1.5f)); var s = DecideAt(nif, read, Mstt, Cube(50, 0, 0, 1.5f));
            var mid = Polar(45, 50); var n = DecideAt(nif, read, Mstt, Cube(mid.X, mid.Y, 0, 1.5f));
            return (r.Decision == "witness" && r.Origin == MemberMotion.OriginReference && (r.Label ?? "").Contains("reference pose")
                    && s.Decision == "witness" && s.Origin == MemberMotion.OriginRest && n.Decision == "never", $"ref {ShowMD(r)} | stored {ShowMD(s)} | 45 {ShowMD(n)}");
        });
        // ================= K-M11: the 1-DOF rule (decision P3) =================
        Case(check, "K-M11 fixture control (base reading): the 1-DOF rule accepts two clips at disjoint angle ranges about one axis: the default reading is 'moving' with both clips", () =>
        {
            var (nif, read) = KM11();
            var m = NifMotion.Read(nif, read);
            return (m.Status == "moving" && m.Animations.Count == 2, $"{m.Status} {m.Reason} [{string.Join(",", m.Animations.Select(a => a.Id))}]");
        });
        Case(check, "K-M11 two clips at 0-10 deg and 50-60 deg about one axis, partner at 30 deg: witness origin blend (P3: rejects today's 1-DOF rule, whose region is the clip poses only)", () =>
        {
            var (nif, read) = KM11();
            var p = Polar(30, 50);
            var d = DecideAt(nif, read, Mstt, Cube(p.X, p.Y, 0, 1.5f));
            return (d.Decision == "witness" && d.Origin == MemberMotion.OriginBlend, ShowMD(d));
        });
        // ================= K-M12: the decision loop refines where the partner keeps a cell open =================
        Case(check, "K-M12 one 0-90 deg turn, initial target 200 u, budget 64: a partner 14 u inside the arc (needs 3 splits) never; a partner at 50 deg witness at t in [0.53, 0.58]; both as a uniform grid at 1/16 (rejects a loop stopping at the coarse level: cannot_tell; certifying an unsplit cell: never)", () =>
        {
            var nif = KM12();
            var pa = Polar(45, 34.5); var pb = Polar(50, 50);
            var sweep = new MemberSweep(200, 64);
            var a = DecideAt(nif, null, Mstt, Cube(pa.X, pa.Y, 0, 1f), sweep); var b = DecideAt(nif, null, Mstt, Cube(pb.X, pb.Y, 0, 1f), sweep);
            // the oracle: posed at t = k/16 with the existing pose reader, the same predicate, each cell's bound from the arc
            var m0 = NifMotion.Read(nif, null);
            string Grid(Vector3 c)
            {
                var pred = Touch(Cube(c.X, c.Y, 0, 1f)); bool open = false;
                for (int k = 0; k <= 16; k++)
                {
                    var t = pred(NifMotion.Pose(nif, m0, "seq:Turn", k / 16f, VolumeGeometry.Render).Soup!, null, 50.71 * (Math.PI / 2) / 16);
                    if (t.Kind == "crosses") return "witness";
                    if (t.Kind != "clear") open = true;
                }
                return open ? "cannot_tell" : "never";
            }
            return (a.Decision == "never" && Grid(pa) == "never" && b.Decision == "witness" && b.Time is >= 0.53f and <= 0.58f && Grid(pb) == "witness" && a.Evaluations <= 64 && b.Evaluations <= 64,
                    $"a {ShowMD(a)} grid {Grid(pa)} | b {ShowMD(b)} grid {Grid(pb)}");
        });
        // ================= K-M13: every grown sample inside the reach box =================
        // Every sampled pose is a real pose, so it lies inside any sound outer bound of the poses. The samples grown by their deltas
        // and the reach box are two outer bounds, which need not nest (the hinge box is certified to within delta; a region that
        // does not converge carries large deltas), so the grown samples are not held to the box.
        Case(check, "K-M13 every sampled pose lies inside the reach box (K-M1, K-M6 blender, K-M7 rod, K-M8 chain), and the samples include blend / swing origins (rejects a sampler inconsistent with the reach reader)", () =>
        {
            var parts = new List<string>(); bool ok = true;
            void One(string name, byte[] nif, Func<string, byte[]?>? read, string origin)
            {
                var m = MemberMotion.Read(nif, read, Mstt);
                var reg = MemberMotion.Region(nif, m, VolumeGeometry.Render, new MemberSweep(), read);
                var (box, _, why) = MemberMotion.Box(nif, m, V0, V0, 1f, new MemberSweep(), read);
                int outside = 0;
                foreach (var s in reg.Samples)
                    if (!Contains(box, SpatialOverlap.SoupBounds(s.Soup))) outside++;
                bool has = reg.Samples.Any(s => s.Origin == origin);
                ok &= reg.Status == "built" && why is null && outside == 0 && has;
                parts.Add($"{name}: {reg.Status} {reg.Reason} {reg.Samples.Count} samples, {outside} outside, {origin} {has}, box {ShowA7(box)} {why}");
            }
            var k1 = KM1(); One("K-M1", k1.Nif, k1.Read, MemberMotion.OriginBlend);
            var k6 = KM6("KM6b2", new[] { "SeqA", "SeqB" }); One("K-M6", k6.Nif, k6.Read, MemberMotion.OriginBlend);
            One("K-M7", HingeRod(0f, -30, 45), null, MemberMotion.OriginSwing);
            One("K-M8", HingeChain(), null, MemberMotion.OriginSwing);
            return (ok, string.Join(" | ", parts));
        });
        Case(check, "K-M13b regression guard (holds on the base): a clip-only region's samples carry no origin, and a serialised sample has no Origin key (rejects Origin written for clip samples, which changes default output)", () =>
        {
            var nif = KM12();
            var m = MemberMotion.Read(nif, null, Mstt);
            var reg = MemberMotion.Region(nif, m, VolumeGeometry.Render, new MemberSweep(), null);
            var json = JsonSerializer.Serialize(reg.Samples[0] with { Soup = new TriangleSoup(Array.Empty<Vector3>(), Array.Empty<int>()) });
            return (reg.Status == "built" && reg.Samples.All(s => s.Origin is null) && !json.Contains("Origin"), $"{reg.Status} {reg.Samples.Count} samples; {json}");
        });
    }
}
