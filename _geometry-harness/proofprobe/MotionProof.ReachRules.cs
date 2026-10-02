using System.Numerics;
using HousecarlCore;
using NiflySharp;
using NiflySharp.Blocks;
using NiflySharp.Enums;
using NiflySharp.Structs;

/// <summary>Known answers for the reach rules (task unbounded-neighbour-classes, Parts 1-2; Gate A review A1-A3 and the
/// answers to Q1-Q7): physics bodies classified from the file (nif.xml: mass 0 = immovable; hkMotionType), the vertical
/// first contact (K-P1, K-P2), the support filter (K-P3, K-P4, K-P6), the drop and its landed box (A3), the hinge envelope
/// of a pinned body (K-P8 to K-P11), the Static gate (K-P12), no rule labelled certified (K-P13), the book closed-pose rule
/// (K-B1), the switch with animated children and its wind set (K-G6, A2) and the binding policy (K-G3). Expected values are
/// analytic, stated beside each case; each case names the wrong answer it rejects. Fixture controls hold on the base; every
/// other case is red until the rules are implemented (the API is a stub that throws). The canary is wrong by construction
/// and must stay red. Real-data cases (lab files) run through the CLIs in the run folder's KAT.</summary>
static partial class MotionProof
{
    const float HavokU = 69.99f;   // HavokCollision.SkyrimUnitsPerHavokUnit, written out: the fixtures state Havok units
    static string ShowRR(ReachRegion r) => $"{r.Status} {r.Reason} {ShowA7(r.Box)} labels [{string.Join("; ", r.Labels)}] drop {(r.Drop is { } d ? $"{d.Status} {d.Distance:0.###} landed {ShowA7(d.Landed)} support {d.SupportId}" : "-")}";
    static bool Contains(Aabb outer, Aabb inner, float tol = 1e-3f) =>
        !outer.IsEmpty && !inner.IsEmpty && outer.Min.X <= inner.Min.X + tol && outer.Min.Y <= inner.Min.Y + tol && outer.Min.Z <= inner.Min.Z + tol
        && outer.Max.X >= inner.Max.X - tol && outer.Max.Y >= inner.Max.Y - tol && outer.Max.Z >= inner.Max.Z - tol;
    static Aabb AB(float x0, float y0, float z0, float x1, float y1, float z1) => new(new Vec3(x0, y0, z0), new Vec3(x1, y1, z1), false);

    // ---- layers (COLL Index = nif.xml SkyrimLayer number): STATIC 1, CLUTTER 4, TRIGGER 12, an unlisted 20 ----
    static readonly IReadOnlyDictionary<uint, CollisionLayerRule> RRLayers = new Dictionary<uint, CollisionLayerRule>
    {
        [1] = new(1, "L_STATIC", false, false, new HashSet<uint> { 4 }),
        [4] = new(4, "L_CLUTTER", false, false, new HashSet<uint> { 1, 4, 12 }),
        [12] = new(12, "L_TRIGGER", true, false, new HashSet<uint> { 4 }),
        [20] = new(20, "L_OTHER", false, false, new HashSet<uint>()),
    };

    // ---- soups ----
    static TriangleSoup Plate(float half, float z, float cx = 0, float cy = 0) =>
        new(new[] { new Vector3(cx - half, cy - half, z), new Vector3(cx + half, cy - half, z), new Vector3(cx + half, cy + half, z), new Vector3(cx - half, cy + half, z) }, new[] { 0, 1, 2, 0, 2, 3 });
    /// <summary>A tetrahedral spike: base triangle around (cx,cy) at z0 (half-size h), apex (cx,cy,apexZ).</summary>
    static TriangleSoup Spike(float cx, float cy, float z0, float h, float apexZ) =>
        new(new[] { new Vector3(cx - h, cy - h, z0), new Vector3(cx + h, cy - h, z0), new Vector3(cx, cy + h, z0), new Vector3(cx, cy, apexZ) },
            new[] { 0, 2, 1, 0, 1, 3, 1, 2, 3, 2, 0, 3 });
    /// <summary>A triangular bar along X, x in [-50,50]: cross-section (y,z) = (-2,54), (2,54), (0,50) — its lowest edge is the
    /// line y = 0, z = 50.</summary>
    static TriangleSoup BarX() => Prism(x => new Vector3(x, -2, 54), x => new Vector3(x, 2, 54), x => new Vector3(x, 0, 50));
    /// <summary>A triangular bar along Y, y in [-50,50]: cross-section (x,z) = (-2,36), (2,36), (0,40) — its top edge is the line
    /// x = 0, z = 40.</summary>
    static TriangleSoup BarY() => Prism(y => new Vector3(-2, y, 36), y => new Vector3(2, y, 36), y => new Vector3(0, y, 40));
    static TriangleSoup Prism(Func<float, Vector3> a, Func<float, Vector3> b, Func<float, Vector3> c)
    {
        var v = new[] { a(-50), b(-50), c(-50), a(50), b(50), c(50) };
        var i = new[] { 0, 1, 2, 3, 5, 4, 0, 3, 4, 0, 4, 1, 1, 4, 5, 1, 5, 2, 2, 5, 3, 2, 3, 0 };
        return new TriangleSoup(v, i);
    }

    // ---- physics fixtures ----
    static int AddBody(NifFile f, NiNode node, bhkShape shape, float mass, hkMotionType motion, SkyrimLayer layer)
    {
        int si = f.AddBlock(shape);
        var rb = new bhkRigidBody { Shape = new(si) };
        var info = rb.RigidBodyInfo_bRBCI2010;
        info.Mass = mass; info.MotionSystem = motion; info.QualityType = mass > 0 ? hkQualityType.MO_QUAL_MOVING : hkQualityType.MO_QUAL_FIXED;
        var ihf = info.HavokFilter; ihf.Layer_SL = layer; info.HavokFilter = ihf;
        rb.RigidBodyInfo_bRBCI2010 = info;
        var hf = rb.HavokFilter; hf.Layer_SL = layer; rb.HavokFilter = hf;
        int bi = f.AddBlock(rb);
        int co = f.AddBlock(new bhkCollisionObject { Body = new(bi), Target = new(f.Blocks.IndexOf(node)) });
        node.CollisionObject = new(co);
        return bi;
    }
    static bhkConvexVerticesShape ConvexBox(float x0, float y0, float z0, float x1, float y1, float z1)
    {
        var v = new List<Vector4>();
        for (int k = 0; k < 8; k++) v.Add(new Vector4(((k & 1) == 0 ? x0 : x1) / HavokU, ((k & 2) == 0 ? y0 : y1) / HavokU, ((k & 4) == 0 ? z0 : z1) / HavokU, 0));
        // the six face planes (nif.xml: exterior half spaces, w = -n.v): without them the decoder reads the vertices only and
        // names the shape unread
        var n = new List<Vector4>
        {
            new(1, 0, 0, -x1 / HavokU), new(-1, 0, 0, x0 / HavokU), new(0, 1, 0, -y1 / HavokU),
            new(0, -1, 0, y0 / HavokU), new(0, 0, 1, -z1 / HavokU), new(0, 0, -1, z0 / HavokU),
        };
        return new bhkConvexVerticesShape { Vertices = v, NumVertices = 8, Normals = n, NumNormals = 6 };
    }

    /// <summary>A free item: root -> node "Item" -> render box [-5,5]^3 and a movable convex body of the same box (mass 5,
    /// MO_SYS_SPHERE_STABILIZED, SKYL_CLUTTER).</summary>
    static byte[] FreeItem()
    {
        var f = new NifFile(); f.Create(Ver, withRootNode: true);
        var root = f.GetRootNodes().First(); root.Flags_ui = 0xE;
        var item = new NiNode { Name = new NiStringRef("Item"), Flags_ui = 0xE, Scale = 1f, Rotation = Identity33() };
        root.Children.AddBlockRef(f.AddBlock(item));
        item.Children.AddBlockRef(f.AddBlock(Shape("ItemMesh", Box(-5, -5, -5, 5, 5, 5))));
        AddBody(f, item, ConvexBox(-5, -5, -5, 5, 5, 5), 5f, hkMotionType.MO_SYS_SPHERE_STABILIZED, SkyrimLayer.SKYL_CLUTTER);
        return Save(f, "free item");
    }

    /// <summary>A rod on a hinge: root -> node "Anchor" at (-10,0,0) with a body (mass <paramref name="anchorMass"/>; 0 = the
    /// file's immovable body, the nail) and node "Rod" at the origin with a render box [0,100]x[-1,1]x[-1,1] and a movable body
    /// of the same box. One bhkLimitedHingeConstraint: entity A the rod, entity B the anchor; Pivot A (0,0,0) in the rod's frame,
    /// Pivot B (10,0,0)/69.99 in the anchor's frame (the same model point, the origin); axis +Z in both; limits
    /// [<paramref name="minDeg"/>, <paramref name="maxDeg"/>] degrees.</summary>
    static byte[] HingeRod(float anchorMass = 0f, float minDeg = -30, float maxDeg = 30)
    {
        var f = new NifFile(); f.Create(Ver, withRootNode: true);
        var root = f.GetRootNodes().First(); root.Flags_ui = 0xE;
        var anchor = new NiNode { Name = new NiStringRef("Anchor"), Flags_ui = 0xE, Scale = 1f, Rotation = Identity33(), Translation = new(-10, 0, 0) };
        var rod = new NiNode { Name = new NiStringRef("Rod"), Flags_ui = 0xE, Scale = 1f, Rotation = Identity33() };
        root.Children.AddBlockRef(f.AddBlock(anchor));
        root.Children.AddBlockRef(f.AddBlock(rod));
        rod.Children.AddBlockRef(f.AddBlock(Shape("RodMesh", Box(0, -1, -1, 100, 1, 1))));
        int ab = AddBody(f, anchor, ConvexBox(-1, -1, -1, 1, 1, 1), anchorMass, anchorMass > 0 ? hkMotionType.MO_SYS_SPHERE_STABILIZED : hkMotionType.MO_SYS_BOX_STABILIZED,
                         anchorMass > 0 ? SkyrimLayer.SKYL_CLUTTER : SkyrimLayer.SKYL_STATIC);
        int rbIdx = AddBody(f, rod, ConvexBox(0, -1, -1, 100, 1, 1), 5f, hkMotionType.MO_SYS_SPHERE_STABILIZED, SkyrimLayer.SKYL_CLUTTER);
        var cons = new bhkLimitedHingeConstraint();
        var c = cons.Constraint;
        c.PivotA = new Vector4(0, 0, 0, 0); c.AxisA = new Vector4(0, 0, 1, 0); c.PerpAxisInA1 = new Vector4(1, 0, 0, 0); c.PerpAxisInA2 = new Vector4(0, 1, 0, 0);
        c.PivotB = new Vector4(10 / HavokU, 0, 0, 0); c.AxisB = new Vector4(0, 0, 1, 0); c.PerpAxisInB1 = new Vector4(1, 0, 0, 0); c.PerpAxisInB2 = new Vector4(0, 1, 0, 0);
        c.MinAngle = minDeg * MathF.PI / 180f; c.MaxAngle = maxDeg * MathF.PI / 180f;
        cons.Constraint = c;
        var ci = cons.ConstraintInfo; ci.NumEntities = 2; ci.EntityA = new(rbIdx); ci.EntityB = new(ab); cons.ConstraintInfo = ci;
        int consIdx = f.AddBlock(cons);
        var rodBody = (bhkRigidBody)f.Blocks[rbIdx];
        rodBody.Constraints ??= new();
        rodBody.Constraints.AddBlockRef(consIdx); rodBody.NumConstraints = 1;
        return Save(f, "hinge rod");
    }

    /// <summary>A hinge between an immovable anchor (plain body, node at (-10,0,0)) and a rod whose body is a bhkRigidBodyT
    /// turned +90 degrees about Z (Havok quaternion (0,0,sin45,cos45), no translation). The rod's Pivot A (0,-10,0)/69.99 in its
    /// body frame lies at model (10,0,0) when the frame's rotation is applied in the collision decoder's sense (the quaternion
    /// rotates the point); the anchor's Pivot B (20,0,0)/69.99 lies at model (10,0,0) too. Applied transposed (the inverse
    /// rotation) A would land at (-10,0,0).</summary>
    static byte[] HingeRodT()
    {
        var f = new NifFile(); f.Create(Ver, withRootNode: true);
        var root = f.GetRootNodes().First(); root.Flags_ui = 0xE;
        var anchor = new NiNode { Name = new NiStringRef("Anchor"), Flags_ui = 0xE, Scale = 1f, Rotation = Identity33(), Translation = new(-10, 0, 0) };
        var rod = new NiNode { Name = new NiStringRef("Rod"), Flags_ui = 0xE, Scale = 1f, Rotation = Identity33() };
        root.Children.AddBlockRef(f.AddBlock(anchor)); root.Children.AddBlockRef(f.AddBlock(rod));
        rod.Children.AddBlockRef(f.AddBlock(Shape("RodMesh", Box(0, -1, -1, 100, 1, 1))));
        int ab = AddBody(f, anchor, ConvexBox(-1, -1, -1, 1, 1, 1), 0f, hkMotionType.MO_SYS_BOX_STABILIZED, SkyrimLayer.SKYL_STATIC);
        int si = f.AddBlock(ConvexBox(-1, -1, -1, 1, 1, 1));
        var rbT = new bhkRigidBodyT { Shape = new(si) };
        var info = rbT.RigidBodyInfo_bRBCI2010;
        info.Mass = 5f; info.MotionSystem = hkMotionType.MO_SYS_SPHERE_STABILIZED; info.QualityType = hkQualityType.MO_QUAL_MOVING;
        float s45 = MathF.Sqrt(0.5f);
        var q = info.Rotation; q.X = 0; q.Y = 0; q.Z = s45; q.W = s45; info.Rotation = q;
        info.Translation = new Vector4(0, 0, 0, 0);
        var ihf = info.HavokFilter; ihf.Layer_SL = SkyrimLayer.SKYL_CLUTTER; info.HavokFilter = ihf;
        rbT.RigidBodyInfo_bRBCI2010 = info;
        var hf = rbT.HavokFilter; hf.Layer_SL = SkyrimLayer.SKYL_CLUTTER; rbT.HavokFilter = hf;
        int rbIdx = f.AddBlock(rbT);
        int co = f.AddBlock(new bhkCollisionObject { Body = new(rbIdx), Target = new(f.Blocks.IndexOf(rod)) });
        rod.CollisionObject = new(co);
        var cons = new bhkLimitedHingeConstraint();
        var c = cons.Constraint;
        c.PivotA = new Vector4(0, -10 / HavokU, 0, 0); c.AxisA = new Vector4(0, 0, 1, 0); c.PerpAxisInA1 = new Vector4(1, 0, 0, 0); c.PerpAxisInA2 = new Vector4(0, 1, 0, 0);
        c.PivotB = new Vector4(20 / HavokU, 0, 0, 0); c.AxisB = new Vector4(0, 0, 1, 0); c.PerpAxisInB1 = new Vector4(1, 0, 0, 0); c.PerpAxisInB2 = new Vector4(0, 1, 0, 0);
        c.MinAngle = -0.5f; c.MaxAngle = 0.5f;
        cons.Constraint = c;
        var ci = cons.ConstraintInfo; ci.NumEntities = 2; ci.EntityA = new(rbIdx); ci.EntityB = new(ab); cons.ConstraintInfo = ci;
        int consIdx = f.AddBlock(cons);
        rbT.Constraints ??= new(); rbT.Constraints.AddBlockRef(consIdx); rbT.NumConstraints = 1;
        return Save(f, "hinge rod T");
    }

    /// <summary>root -> NiSwitchNode "Sw" -> child 0 shape "Short" box [0,10]^3; child 1 node "Mover" (a direct active
    /// NiTransformController sliding +100 in x over 1 s, CLAMP) -> shape "Part" box [0,10]^3. Optionally Tree_Anim on "Short".</summary>
    static byte[] SwitchMover(bool windOnShort)
    {
        var f = new NifFile(); f.Create(Ver, withRootNode: true);
        var root = f.GetRootNodes().First(); root.Flags_ui = 0xE;
        var sw = new NiSwitchNode { Name = new NiStringRef("Sw"), Flags_ui = 0xE, Scale = 1f, Rotation = Identity33() };
        root.Children.AddBlockRef(f.AddBlock(sw));
        sw.Children.AddBlockRef(f.AddBlock(Shape("Short", Box(0, 0, 0, 10, 10, 10))));
        var mover = new NiNode { Name = new NiStringRef("Mover"), Flags_ui = 0xE, Scale = 1f, Rotation = Identity33() };
        sw.Children.AddBlockRef(f.AddBlock(mover));
        mover.Children.AddBlockRef(f.AddBlock(Shape("Part", Box(0, 0, 0, 10, 10, 10))));
        int dataIndex = f.AddBlock(Slide(new(100, 0, 0)));
        int interpIndex = f.AddBlock(new NiTransformInterpolator { Data = new(dataIndex) });
        var ctl = new NiTransformController { Interpolator = new(interpIndex), StartTime = 0, StopTime = 1, Frequency = 1 };
        ctl.Flags.Active = true; ctl.Flags.CycleType = CycleType.CYCLE_CLAMP;
        int ci = f.AddBlock(ctl);
        ctl.Target = new(f.Blocks.IndexOf(mover));
        mover.Controller = new(ci);
        if (windOnShort) WindShader(f, "Short", treeAnim: true);
        return Save(f, "switch mover");
    }

    static ReachOptions RulesOn(bool physics = false, bool graph = false, bool switchMotion = false, bool bookArt = false, string? baseType = null,
                                IEnumerable<DropSupport>? supports = null) =>
        new(0.5, 65536, new ReachRules(physics, graph, switchMotion, bookArt, baseType, supports is null ? null : _ => supports, RRLayers));

    public static void RunReachRules(Action<string, bool, string> check)
    {
        // ================= canary (wrong by construction; must stay RED) =================
        Case(check, "reach-rules CANARY (must FAIL): K-P1 with the gap's sign flipped — box 50 u above a plate drops -50", () =>
        {
            var d = PhysicsReach.FirstContact(Box(0, 0, 50, 10, 10, 60), Plate(100, 0));
            return (d is { } x && NearA7(x, -50), $"d {d}");
        });

        // ================= fixture controls (existing code; hold on the base) =================
        Case(check, "reach-rules fixture control: the hinge rod has two rigid bodies (masses 0 and 5) and one limited hinge (±30°) between them", () =>
        {
            var n = Load(HingeRod());
            var bodies = n.Blocks.OfType<bhkRigidBody>().ToList();
            var h = n.Blocks.OfType<bhkLimitedHingeConstraint>().SingleOrDefault();
            return (bodies.Count == 2 && bodies.Select(b => b.RigidBodyInfo_bRBCI2010.Mass).OrderBy(x => x).SequenceEqual(new[] { 0f, 5f })
                    && h is not null && NearA7(h.Constraint.MaxAngle, Math.PI / 6, 1e-6) && NearA7(h.Constraint.MinAngle, -Math.PI / 6, 1e-6)
                    && h.ConstraintInfo.EntityA.Index >= 0 && h.ConstraintInfo.EntityB.Index >= 0,
                    $"bodies {bodies.Count} masses [{string.Join(",", bodies.Select(b => b.RigidBodyInfo_bRBCI2010.Mass))}] hinge {h?.Constraint.MinAngle}..{h?.Constraint.MaxAngle} entities {h?.ConstraintInfo.EntityA.Index},{h?.ConstraintInfo.EntityB.Index}");
        });
        Case(check, "reach-rules fixture control: HavokCollision decodes the free item's body as solid, SKYL_CLUTTER, MO_SYS_SPHERE_STABILIZED, box [-5,5]^3", () =>
        {
            var g = NifGeometry.Measure(FreeItem(), includeTriangles: true);
            var b = g.Model?.CollisionBodies?.SingleOrDefault();
            return (b is { Solid: true, Layer: "SKYL_CLUTTER", MotionSystem: "MO_SYS_SPHERE_STABILIZED" } && b.Unread.Count == 0 && BoxIs(b.Bounds, -5, -5, -5, 5, 5, 5, 0.01f),
                    $"{b?.Layer} {b?.MotionSystem} {b?.Solid} unread [{(b is null ? "" : string.Join("; ", b.Unread))}] {(b is null ? "" : ShowA7(b.Bounds))}");
        });
        Case(check, "reach-rules fixture control: rules OFF, the switch mover refuses as today ('switch node … runtime state')", () =>
        {
            var r = MeshReach.OfModel(SwitchMover(false), Origin, Origin, 1f);
            return (r.Status == "refused" && (r.Reason ?? "").Contains("switch node", StringComparison.Ordinal), SayA7(r));
        });
        Case(check, "reach-rules fixture control: rules OFF, the free item reads certified at its placed box [-5,5]^3 (what Stage A said of the coin purses)", () =>
        {
            var r = MeshReach.OfModel(FreeItem(), Origin, Origin, 1f);
            return (r.Status == "certified" && BoxIs(r.Box, -5, -5, -5, 5, 5, 5), SayA7(r));
        });

        // ================= K-P1 / K-P2: vertical first contact =================
        // K-P1: box z 50..60 over a plate at z 0 -> d = 50. Rejects d = 0, the render box used, the lowest instead of the first surface.
        Case(check, "reach-rules K-P1: box 50 u above a plate — first contact d = 50", () =>
        {
            var d = PhysicsReach.FirstContact(Box(0, 0, 50, 10, 10, 60), Plate(100, 0));
            return (d is { } x && NearA7(x, 50, 1e-4), $"d {d}");
        });
        // K-P1b: two plates (z 0 and z 20) under the box -> the first (z 20) is met: d = 30 through Drop. Rejects the lowest.
        Case(check, "reach-rules K-P1b: two supports below (z 20 and z 0) — Drop meets z 20 first: d 30, landed z 20..30, prism z 20..60", () =>
        {
            var r = PhysicsReach.Drop(Box(0, 0, 50, 10, 10, 60), AB(0, 0, 50, 10, 10, 60), 4,
                new[] { new DropSupport("low", 1, true, false, Plate(100, 0)), new DropSupport("high", 1, true, false, Plate(100, 20)) }, RRLayers);
            return (r.Status == "dropped" && NearA7(r.Distance, 30, 1e-4) && r.SupportId == "high" && BoxIs(r.Landed, 0, 0, 20, 10, 10, 30) && BoxIs(r.Prism, 0, 0, 20, 10, 10, 60) && BoxIs(r.Placed, 0, 0, 50, 10, 10, 60),
                    $"{r.Status} d {r.Distance} support {r.SupportId} landed {ShowA7(r.Landed)} prism {ShowA7(r.Prism)}");
        });
        // K-P2a: a slab (z 50..60, 100 x 100) over a spike whose apex is at z 30 inside the slab's footprint -> d = 20 (the spike's
        // vertex cast up onto the slab's bottom face). Rejects casting moving vertices down only (no contact found).
        // (the spike stands off the slab's bottom diagonal x = y: at (50,50) the Gate C sweep found the edge-edge family meeting
        // the apex through that diagonal, so the up-cast family went untested)
        Case(check, "reach-rules K-P2a: slab over a spike (apex z 30, off the slab's diagonal) — d = 20 (support vertex up onto the moving face)", () =>
        {
            var d = PhysicsReach.FirstContact(Box(0, 0, 50, 100, 100, 60), Spike(30, 60, 0, 5, 30));
            return (d is { } x && NearA7(x, 20, 1e-4), $"d {d}");
        });
        // K-P2c (found by the Gate C sweep: K-P1's square plate is two triangles whose diagonal crosses the box's footprint, so
        // the edge-edge family alone finds its contact): a box over ONE large triangle whose edges and corners all lie outside
        // the box's footprint -> only the moving vertices cast down meet it: d = 50. Rejects that family dropped.
        Case(check, "reach-rules K-P2c: box over a single large triangle (no support edge or corner under it) - d = 50 (moving vertices down)", () =>
        {
            var tri = new TriangleSoup(new[] { new Vector3(-300, -300, 0), new Vector3(300, -300, 0), new Vector3(0, 300, 0) }, new[] { 0, 1, 2 });
            var d = PhysicsReach.FirstContact(Box(0, 0, 50, 10, 10, 60), tri);
            return (d is { } x && NearA7(x, 50, 1e-4), $"d {d}");
        });
        // K-P2b: two crossed triangular bars (lowest edge z 50 along X; top edge z 40 along Y) -> d = 10 (edge-edge only).
        Case(check, "reach-rules K-P2b: crossed bars — d = 10 (edge–edge contact; no vertex lies over the other bar)", () =>
        {
            var d = PhysicsReach.FirstContact(BarX(), BarY());
            return (d is { } x && NearA7(x, 10, 1e-4), $"d {d}");
        });

        // ================= K-T: terrain (review note on Gate C: LAND is read as a support) =================
        // A saddle quad (100 x 100 u, corners z 0 at (0,0),(100,100) and z 10 at (100,0),(0,100)) cut along each diagonal is two
        // alternative supports (one Group). A 2 x 2 x 2 box at (49..51, 49..51, 20..22): cut 00-11 the surface under it peaks at
        // 0.2 (d 19.8); cut 10-01 it peaks at 10 (d 10). The drop meets the earlier (d 10) and reports the range to 19.8; the
        // prism reaches 0.2. Rejects one diagonal guessed, and a landing reported as determined when the file cannot settle it.
        Case(check, "reach-rules K-T1: terrain cut both ways - d 10, DistanceMax 19.8, status dropped-range, prism z 0.2..22, landed z 10..12", () =>
        {
            var p = new[] { new Vector3(0, 0, 0), new Vector3(100, 0, 10), new Vector3(100, 100, 0), new Vector3(0, 100, 10) };
            var cutA = new TriangleSoup(p, new[] { 0, 1, 2, 0, 2, 3 }); var cutB = new TriangleSoup(p, new[] { 0, 1, 3, 1, 2, 3 });
            var r = PhysicsReach.Drop(Box(49, 49, 20, 51, 51, 22), AB(49, 49, 20, 51, 51, 22), 4,
                new[] { new DropSupport("land", 1, true, false, cutA, Group: "land"), new DropSupport("land", 1, true, false, cutB, Group: "land") }, RRLayers);
            return (r.Status == "dropped-range" && NearA7(r.Distance, 10, 1e-4) && NearA7(r.DistanceMax, 19.8, 1e-3)
                    && BoxIs(r.Prism, 49, 49, 0.2f, 51, 51, 22, 0.01f) && BoxIs(r.Landed, 49, 49, 10, 51, 51, 12, 0.01f),
                    $"{r.Status} d {r.Distance} max {r.DistanceMax} prism {ShowA7(r.Prism)} landed {ShowA7(r.Landed)}");
        });
        // K-T2: a flat heightmap (all 33 x 33 vertices at z 100, cell 0,0) under a column at (500,500): TerrainSupports gives two
        // alternatives whose triangles lie at z 100 and cover the column; a box at z 150..160 drops d 50, a determined landing.
        Case(check, "reach-rules K-T2: flat LAND patch -> two alternative supports at z 100 under the column; box drops d 50 (determined)", () =>
        {
            var h = new float[33, 33]; for (int i = 0; i < 33; i++) for (int j = 0; j < 33; j++) h[i, j] = 100;
            var s = PhysicsReach.TerrainSupports("LAND 0,0", h, 0, 0, new Aabb(new Vec3(495, 495, float.NegativeInfinity), new Vec3(505, 505, 160), false), 13);
            var r = PhysicsReach.Drop(Box(495, 495, 150, 505, 505, 160), AB(495, 495, 150, 505, 505, 160), 4, s,
                new Dictionary<uint, CollisionLayerRule>(RRLayers) { [13] = new(13, "L_TERRAIN", false, false, new HashSet<uint> { 4 }), [4] = new(4, "L_CLUTTER", false, false, new HashSet<uint> { 1, 4, 12, 13 }) });
            return (s.Count == 2 && s.All(x => x.Group == "LAND 0,0" && x.World.Vertices.All(v => v.Z == 100)) && r.Status == "dropped" && NearA7(r.Distance, 50, 1e-4),
                    $"supports {s.Count} groups [{string.Join(",", s.Select(x => x.Group))}] -> {r.Status} d {r.Distance} max {r.DistanceMax}");
        });

        // ================= K-P3 / K-P4 / K-P6: supports =================
        // K-P3: the only plate is above -> refused with the named reason; FirstContact null. Rejects an invented floor.
        Case(check, "reach-rules K-P3: nothing below (the only plate is above) — refused '" + PhysicsReach.NothingBelow + "'", () =>
        {
            var fc = PhysicsReach.FirstContact(Box(0, 0, 50, 10, 10, 60), Plate(100, 100));
            var r = PhysicsReach.Drop(Box(0, 0, 50, 10, 10, 60), AB(0, 0, 50, 10, 10, 60), 4, new[] { new DropSupport("above", 1, true, false, Plate(100, 100)) }, RRLayers);
            return (fc is null && r.Status == "refused" && r.Reason == PhysicsReach.NothingBelow, $"fc {fc}; {r.Status} {r.Reason}");
        });
        // K-P4: supports at z 40 (layer 20, not in L_CLUTTER's list), z 30 (a phantom on L_STATIC), z 20 (L_TRIGGER: listed but a
        // trigger volume) and z 0 (L_STATIC) -> only z 0 holds: d = 50, support "floor". Rejects counting every solid / every layer.
        Case(check, "reach-rules K-P4: layer filter — an unlisted layer, a phantom and a trigger volume are skipped; the floor at z 0 holds (d 50)", () =>
        {
            var s = new[]
            {
                new DropSupport("unlisted", 20, true, false, Plate(100, 40)), new DropSupport("phantom", 1, false, false, Plate(100, 30)),
                new DropSupport("trigger", 12, true, false, Plate(100, 20)), new DropSupport("floor", 1, true, false, Plate(100, 0)),
            };
            var flags = s.Select(x => PhysicsReach.IsSupport(4, x, RRLayers)).ToArray();
            var r = PhysicsReach.Drop(Box(0, 0, 50, 10, 10, 60), AB(0, 0, 50, 10, 10, 60), 4, s, RRLayers);
            return (flags.SequenceEqual(new[] { false, false, false, true }) && r.SupportId == "floor" && NearA7(r.Distance, 50, 1e-4),
                    $"isSupport [{string.Join(",", flags)}] -> {r.Status} {r.SupportId} d {r.Distance}");
        });
        // K-P6: a movable body at z 30 is not a support; the floor at z 0 holds (d 50). Rejects a movable object holding another up.
        Case(check, "reach-rules K-P6: a movable support at z 30 is ignored — falls to the floor (d 50)", () =>
        {
            var r = PhysicsReach.Drop(Box(0, 0, 50, 10, 10, 60), AB(0, 0, 50, 10, 10, 60), 4,
                new[] { new DropSupport("crate", 4, true, true, Plate(100, 30)), new DropSupport("floor", 1, true, false, Plate(100, 0)) }, RRLayers);
            return (r.SupportId == "floor" && NearA7(r.Distance, 50, 1e-4), $"{r.Status} {r.SupportId} d {r.Distance}");
        });

        // ================= drop through the reach region (A3, K-P13) =================
        // Free item at the origin, floor plate at z -100 (L_STATIC): d = 95; landed [-5,5]^2 x [-100,-90]; region = prism
        // [-5,5]^2 x [-100,5]; label the rule; status uncertified; the Drop carries d and the landed box. Rejects the placed box
        // reported as certified (Stage A) and a region without the landed pose.
        Case(check, "reach-rules A3/K-P13: free item over a floor 100 u down — prism [-5,5]^2x[-100,5], landed z -100..-90, d 95, '" + PhysicsReach.DropLabel + "', not certified", () =>
        {
            var r = MeshReach.OfModel(FreeItem(), Origin, Origin, 1f, null, RulesOn(physics: true, baseType: "MISC", supports: new[] { new DropSupport("floor", 1, true, false, Plate(500, -100)) }));
            return (r.Status == "uncertified" && r.Labels.Contains(PhysicsReach.DropLabel) && BoxIs(r.Box, -5, -5, -100, 5, 5, 5, 0.01f)
                    && r.Drop is { } d && NearA7(d.Distance, 95, 1e-3) && BoxIs(d.Landed, -5, -5, -100, 5, 5, -90, 0.01f) && d.SupportId == "floor", ShowRR(r));
        });
        // K-P12: the same model on a Static base is not dropped: its placed box, labelled as the untested assumption. Rejects
        // dropping Statics, and an unlabelled Static.
        Case(check, "reach-rules K-P12: free item on a STAT base — not dropped: box [-5,5]^3, '" + PhysicsReach.StaticAssumptionLabel + "', no drop", () =>
        {
            var r = MeshReach.OfModel(FreeItem(), Origin, Origin, 1f, null, RulesOn(physics: true, baseType: "STAT", supports: new[] { new DropSupport("floor", 1, true, false, Plate(500, -100)) }));
            return (r.Status == "uncertified" && r.Labels.Contains(PhysicsReach.StaticAssumptionLabel) && !r.Labels.Contains(PhysicsReach.DropLabel)
                    && r.Drop is null && BoxIs(r.Box, -5, -5, -5, 5, 5, 5), ShowRR(r));
        });
        // Q6 (review note on Gate C): the free item lands on a Static whose own file body is movable (a bowl): the drop row also
        // carries the Static assumption, because the assumption decides where it lands. Rejects a landing on an assumed-static
        // support reported without the assumption.
        Case(check, "reach-rules Q6b: free item lands on an assumed-static support (z -100) — '" + PhysicsReach.DropLabel + "' and '" + PhysicsReach.StaticAssumptionLabel + "'", () =>
        {
            var r = MeshReach.OfModel(FreeItem(), Origin, Origin, 1f, null, RulesOn(physics: true, baseType: "MISC",
                supports: new[] { new DropSupport("bowl", 1, true, false, Plate(500, -100), AssumedStatic: true) }));
            return (r.Status == "uncertified" && r.Labels.Contains(PhysicsReach.DropLabel) && r.Labels.Contains(PhysicsReach.StaticAssumptionLabel)
                    && r.Drop is { SupportId: "bowl" }, ShowRR(r));
        });
        // Nothing below through the reach region: refused, the named reason. Rejects an infinite or invented region.
        Case(check, "reach-rules K-P3b: free item with no support — region refused '" + PhysicsReach.NothingBelow + "'", () =>
        {
            var r = MeshReach.OfModel(FreeItem(), Origin, Origin, 1f, null, RulesOn(physics: true, baseType: "MISC", supports: Array.Empty<DropSupport>()));
            return (r.Status == "refused" && (r.Reason ?? "").Contains(PhysicsReach.NothingBelow, StringComparison.Ordinal), ShowRR(r));
        });

        // ================= K-P8 / K-P11: classification and constraint frames =================
        // K-P11: the hinge rod with an immovable anchor is pinned; with a movable anchor a free chain; the free item free; a plain
        // static mesh none. Rejects mass 0 read as movable, and the constraint ignored.
        Case(check, "reach-rules K-P11: classify — anchor mass 0: pinned; anchor mass 5: free-chain; free item: free; no body: none", () =>
        {
            var p = PhysicsReach.Classify(HingeRod()); var c = PhysicsReach.Classify(HingeRod(anchorMass: 5)); var f = PhysicsReach.Classify(FreeItem());
            var n = PhysicsReach.Classify(StaticNif(("Plain", Box(0, 0, 0, 10, 10, 10))));
            return (p.Kind == "pinned" && c.Kind == "free-chain" && f.Kind == "free" && n.Kind == "none"
                    && p.Bodies.Count(b => b.Movable) == 1 && p.Bodies.Count == 2 && p.Constraints.Count == 1 && p.Constraints[0].Class == "bhkLimitedHingeConstraint",
                    $"{p.Kind}/{c.Kind}/{f.Kind}/{n.Kind}; pinned bodies {p.Bodies.Count} movable {p.Bodies.Count(b => b.Movable)} constraints {p.Constraints.Count}");
        });
        // K-P8: the hinge's two pivots in the model frame: A = rod origin (0,0,0); B = anchor (-10,0,0) + (10/69.99)*69.99 = (0,0,0);
        // axes +Z; limits ±pi/6. Rejects a missing unit scale (B at -9.857), swapped entities, degrees for radians.
        Case(check, "reach-rules K-P8: hinge pivots meet at the model origin (A from the rod, B from the anchor × 69.99), axis +Z, limits ±π/6", () =>
        {
            var c = PhysicsReach.Classify(HingeRod()).Constraints.Single();
            bool ok = Vector3.Distance(c.PivotAModel, Vector3.Zero) < 1e-3f && Vector3.Distance(c.PivotBModel, Vector3.Zero) < 1e-3f
                      && Vector3.Distance(Vector3.Normalize(c.AxisAModel), Vector3.UnitZ) < 1e-4f && c.MinAngle is { } lo && NearA7(lo, -Math.PI / 6, 1e-5)
                      && c.MaxAngle is { } hi && NearA7(hi, Math.PI / 6, 1e-5);
            return (ok, $"A {c.PivotAModel} B {c.PivotBModel} axis {c.AxisAModel} limits {c.MinAngle}..{c.MaxAngle} entities {c.BodyA}->{c.BodyB}");
        });

        // K-P8b (review note on K-P8: on the lab's T-body fish the frame and its transpose cannot be told apart): a T-body turned
        // 90 degrees: pivot A at model (10,0,0), equal to B. Rejects the T frame's rotation applied transposed ((-10,0,0)) or not
        // at all ((0,-10,0)).
        Case(check, "reach-rules K-P8b: a bhkRigidBodyT turned 90° about Z places its pivot at model (10,0,0), meeting the anchor's", () =>
        {
            var c = PhysicsReach.Classify(HingeRodT()).Constraints.Single();
            return (Vector3.Distance(c.PivotAModel, new Vector3(10, 0, 0)) < 1e-3f && Vector3.Distance(c.PivotBModel, new Vector3(10, 0, 0)) < 1e-3f,
                    $"A {c.PivotAModel} B {c.PivotBModel}");
        });

        // ================= K-P9 / K-P10: the hinge envelope =================
        // The rod [0,100]x[-1,1]x[-1,1] turning about +Z through the origin within ±30°: analytic box x [-0.5, 100.005],
        // y ±(50 + cos30°) = ±50.866, z ±1. The region holds it and lies within delta (0.5) + 0.02 of it; label the file's limits;
        // no drop although a floor is offered 1000 u below (K-P10). Rejects degrees for radians (no turn), a full revolution, the
        // stored pose only, and the drop applied to a pinned body.
        Case(check, "reach-rules K-P9/K-P10: pinned rod ±30° — region ⊇ [-0.5,100.005]x[-50.866,50.866]x[-1,1] within 0.52; '" + PhysicsReach.HingeLabel + "'; not dropped", () =>
        {
            var r = MeshReach.OfModel(HingeRod(), Origin, Origin, 1f, null, RulesOn(physics: true, baseType: "FLOR", supports: new[] { new DropSupport("floor", 1, true, false, Plate(2000, -1000)) }));
            var ana = AB(-0.5f, -50.866f, -1, 100.005f, 50.866f, 1);
            bool tight = r.Box.Min.X >= ana.Min.X - 0.52f && r.Box.Min.Y >= ana.Min.Y - 0.52f && r.Box.Min.Z >= ana.Min.Z - 0.52f
                         && r.Box.Max.X <= ana.Max.X + 0.52f && r.Box.Max.Y <= ana.Max.Y + 0.52f && r.Box.Max.Z <= ana.Max.Z + 0.52f;
            return (r.Status == "uncertified" && r.Labels.Contains(PhysicsReach.HingeLabel) && !r.Labels.Contains(PhysicsReach.DropLabel) && r.Drop is null
                    && Contains(r.Box, ana) && tight, ShowRR(r));
        });
        // Free chain (Q5): the same rod with a movable anchor drops rigidly: rod and anchor lowest point z -1 (anchor box ±1 at
        // x -10) onto a floor at z -100: d 99; labels the rule and the rigid-chain note. Rejects the hinge envelope for an unpinned
        // chain and an unlabelled rigid drop.
        Case(check, "reach-rules Q5: free chain drops rigidly — d 99 onto z -100, '" + PhysicsReach.DropLabel + "' + '" + PhysicsReach.RigidChainLabel + "'", () =>
        {
            var r = MeshReach.OfModel(HingeRod(anchorMass: 5), Origin, Origin, 1f, null, RulesOn(physics: true, baseType: "MISC", supports: new[] { new DropSupport("floor", 1, true, false, Plate(500, -100)) }));
            return (r.Status == "uncertified" && r.Labels.Contains(PhysicsReach.DropLabel) && r.Labels.Contains(PhysicsReach.RigidChainLabel)
                    && r.Drop is { } d && NearA7(d.Distance, 99, 1e-3), ShowRR(r));
        });

        // ================= K-B1: books, closed pose (modelling rule, Q1) =================
        // The two-bone skinned strip with an animation (bone B turns 90°): under BookArt the region is the stored (closed) skinned
        // pose — the strip's own box x 0..40, y -1..1, z 0 — labelled the rule; not the animation's sweep. Rejects the graph's or
        // sequence's clips used for a placed book.
        Case(check, "reach-rules K-B1: book art — region = stored (closed) skinned pose [0,40]x[-1,1]x[0,0], '" + MeshReach.BookClosedLabel + "', not the sweep", () =>
        {
            var nif = SkinRig(90 * MathF.PI / 180f);
            var off = MeshReach.OfModel(nif, Origin, Origin, 1f);
            var r = MeshReach.OfModel(nif, Origin, Origin, 1f, null, RulesOn(bookArt: true, baseType: "STAT"));
            return (r.Status == "uncertified" && r.Labels.Contains(MeshReach.BookClosedLabel) && BoxIs(r.Box, 0, -1, 0, 40, 1, 0, 0.01f)
                    && !BoxIs(off.Box, 0, -1, 0, 40, 1, 0, 0.01f), $"rules on: {ShowRR(r)} | rules off: {SayA7(off)}");
        });

        // ================= K-G6 / A2: a switch whose children animate =================
        // Union of the children's regions: Short [0,10]^3 and Part sliding +100 x: [0,110]x[0,10]x[0,10] (outer, within 0.52);
        // certified (no wind shape). Rejects one child only, and the animation dropped.
        Case(check, "reach-rules K-G6: switch with an animated child — union [0,110]x[0,10]x[0,10] (within 0.52), certified", () =>
        {
            var r = MeshReach.OfModel(SwitchMover(false), Origin, Origin, 1f, null, RulesOn(switchMotion: true));
            bool ok = Contains(r.Box, AB(0, 0, 0, 110, 10, 10)) && Contains(AB(-0.52f, -0.52f, -0.52f, 110.52f, 10.52f, 10.52f), r.Box);
            return (r.Status == "certified" && ok && r.Labels.Count == 0, ShowRR(r));
        });
        // A2: the same switch with Tree_Anim on the static child: uncertified, the wind label, the wind part's box [0,10]^3. Rejects
        // the switch-with-motion path skipping the shader wind set (Stage A's R9b seam).
        Case(check, "reach-rules A2: switch with an animated child and a Tree_Anim child — '" + MeshReach.WindLabel + "', part box [0,10]^3", () =>
        {
            var r = MeshReach.OfModel(SwitchMover(true), Origin, Origin, 1f, null, RulesOn(switchMotion: true));
            var part = r.UncertifiedParts.FirstOrDefault(x => x.Label == MeshReach.WindLabel);
            return (r.Status == "uncertified" && r.Labels.Contains(MeshReach.WindLabel) && part is not null && BoxIs(part.Box, 0, 0, 0, 10, 10, 10), ShowRR(r));
        });

        // ================= K-C: pose confirmations (Piranha, animation parse failures) =================
        // A model whose animation refuses (an inactive direct controller) is read at its stored pose when a confirmation names
        // its path AND the sha256 of its bytes: Part [0,10]^3 and Frame [-200,-180]x[-200,-180]x[-40,-20] -> box
        // [-200,10]x[-200,10]x[-40,10], labelled. Rejects a confirmation applied without one, and a stored pose unlabelled.
        byte[] inactive = Rig(Box(0, 0, 0, 10, 10, 10), Vector3.Zero, Slide(new(100, 0, 0)), direct: true, directActive: false);
        string sha = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(inactive)).ToLowerInvariant();
        Case(check, "reach-rules fixture control: the inactive-controller rig refuses under the rules with no confirmation", () =>
        {
            var r = MeshReach.OfModel(inactive, Origin, Origin, 1f, null, new ReachOptions(0.5, 4096, new ReachRules(ModelPath: @"meshes\fx\Rig.nif")));
            return (r.Status == "refused" && (r.Reason ?? "").Contains("controller inactive", StringComparison.Ordinal), ShowRR(r));
        });
        Case(check, "reach-rules K-C1: a confirmation matching path and sha256 reads the stored pose [-200,10]x[-200,10]x[-40,10], '" + MeshReach.ConfirmedPoseLabel + "'", () =>
        {
            var conf = new[] { new PoseConfirmation(@"Meshes\FX\rig.nif", sha, "evidence/rig") };
            var r = MeshReach.OfModel(inactive, Origin, Origin, 1f, null, new ReachOptions(0.5, 4096, new ReachRules(Confirmations: conf, ModelPath: @"meshes\fx\Rig.nif")));
            return (r.Status == "uncertified" && r.Labels.Contains(MeshReach.ConfirmedPoseLabel) && BoxIs(r.Box, -200, -200, -40, 10, 10, 10), ShowRR(r));
        });
        // K-C2: the same path with a different sha256 (the file changed since the comparison) does not match: refused as before.
        Case(check, "reach-rules K-C2: a confirmation for the same path but other bytes (sha256) does not match — still refused", () =>
        {
            var conf = new[] { new PoseConfirmation(@"meshes\fx\Rig.nif", new string('0', 64), "evidence/rig") };
            var r = MeshReach.OfModel(inactive, Origin, Origin, 1f, null, new ReachOptions(0.5, 4096, new ReachRules(Confirmations: conf, ModelPath: @"meshes\fx\Rig.nif")));
            return (r.Status == "refused" && !r.Labels.Contains(MeshReach.ConfirmedPoseLabel), ShowRR(r));
        });
        // K-C3: the right bytes under another path do not match either.
        Case(check, "reach-rules K-C3: a confirmation with the right sha256 but another model path does not match — still refused", () =>
        {
            var conf = new[] { new PoseConfirmation(@"meshes\fx\Other.nif", sha, "evidence/rig") };
            var r = MeshReach.OfModel(inactive, Origin, Origin, 1f, null, new ReachOptions(0.5, 4096, new ReachRules(Confirmations: conf, ModelPath: @"meshes\fx\Rig.nif")));
            return (r.Status == "refused", ShowRR(r));
        });

        // ================= K-G3: the binding policy =================
        // Member paths from HKX2 (hkbClipGenerator, hkbBlendingTransitionEffect, BSIsActiveModifier). Rejects refusing every binding
        // (crossbow 'Release' binds playbackSpeed) and ignoring every binding (an unknown member must refuse).
        Case(check, "reach-rules K-G3: binding effects — playbackSpeed none, userControlledTimeFraction span, cropStart/cropEnd/startTime clip, transition duration none, bIsActive0 none, animationName (which clip plays) null", () =>
        {
            var got = new[]
            {
                HavokBehaviour.BindingEffect("hkbClipGenerator", "playbackSpeed"), HavokBehaviour.BindingEffect("hkbClipGenerator", "userControlledTimeFraction"),
                HavokBehaviour.BindingEffect("hkbClipGenerator", "cropStartAmountLocalTime"), HavokBehaviour.BindingEffect("hkbClipGenerator", "cropEndAmountLocalTime"),
                HavokBehaviour.BindingEffect("hkbClipGenerator", "startTime"), HavokBehaviour.BindingEffect("hkbBlendingTransitionEffect", "duration"),
                HavokBehaviour.BindingEffect("BSIsActiveModifier", "bIsActive0"), HavokBehaviour.BindingEffect("hkbClipGenerator", "animationName"),
            };
            var want = new string?[] { "none", "span", "clip", "clip", "clip", "none", "none", null };
            return (got.SequenceEqual(want), $"[{string.Join(", ", got.Select(x => x ?? "null"))}]");
        });
    }
}
