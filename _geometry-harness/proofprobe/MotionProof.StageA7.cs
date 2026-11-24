using System.Numerics;
using HousecarlCore;
using SkyWright.Engine;
using NiflySharp;
using NiflySharp.Blocks;
using NiflySharp.Enums;
using NiflySharp.Structs;

/// <summary>Known answers for the G1 build list, Stage A (plan `g1/PLAN-7.md`, Gate A review F1-F5 and the review note on
/// wind margins): the swept witness's material split (row 1, W*), and the reach region (rows 1-3, R*). Expected values
/// are analytic, stated beside each case; the pinned placement is crossprobe's hand-computed case D. Each case names the
/// wrong answer it rejects. Every case here is red on the base: the API is a stub that throws, or (W*) a field the base
/// never fills.</summary>
static partial class MotionProof
{
    const float DegA7 = MathF.PI / 180f;
    static bool NearA7(double a, double b, double tol = 1e-3) => Math.Abs(a - b) <= tol;
    static bool BoxIs(Aabb b, float x0, float y0, float z0, float x1, float y1, float z1, float tol = 0.02f) =>
        !b.IsEmpty && MathF.Abs(b.Min.X - x0) < tol && MathF.Abs(b.Min.Y - y0) < tol && MathF.Abs(b.Min.Z - z0) < tol
        && MathF.Abs(b.Max.X - x1) < tol && MathF.Abs(b.Max.Y - y1) < tol && MathF.Abs(b.Max.Z - z1) < tol;
    static string ShowA7(Aabb b) => b.IsEmpty ? "<empty>" : $"({b.Min.X:0.###},{b.Min.Y:0.###},{b.Min.Z:0.###})..({b.Max.X:0.###},{b.Max.Y:0.###},{b.Max.Z:0.###})";
    static string SayA7(ReachRegion r) => $"{r.Status} {r.Reason} {ShowA7(r.Box)} labels [{string.Join("; ", r.Labels)}] parts {r.UncertifiedParts?.Count}";
    static readonly Vec3 Origin = new(0, 0, 0);

    // ---- fixtures ----
    /// <summary>A static NIF: root -> one shape per soup.</summary>
    static byte[] StaticNif(params (string Name, TriangleSoup Soup)[] shapes)
    {
        var f = new NifFile(); f.Create(Ver, withRootNode: true);
        var root = f.GetRootNodes().First(); root.Flags_ui = 0xE;
        foreach (var (n, s) in shapes) root.Children.AddBlockRef(f.AddBlock(Shape(n, s)));
        return Save(f, "static");
    }

    /// <summary>root -> NiSwitchNode "Sw" with child 0 = cube [0,10]^3 and child 1 = a column [0,10]x[0,10]x[0,300].</summary>
    static byte[] SwitchNif()
    {
        var f = new NifFile(); f.Create(Ver, withRootNode: true);
        var root = f.GetRootNodes().First(); root.Flags_ui = 0xE;
        var sw = new NiSwitchNode { Name = new NiStringRef("Sw"), Flags_ui = 0xE, Scale = 1f, Rotation = Identity33() };
        root.Children.AddBlockRef(f.AddBlock(sw));
        sw.Children.AddBlockRef(f.AddBlock(Shape("Short", Box(0, 0, 0, 10, 10, 10))));
        sw.Children.AddBlockRef(f.AddBlock(Shape("Tall", Box(0, 0, 0, 10, 10, 300))));
        return Save(f, "switch");
    }

    /// <summary>One particle system under the root, emitter object = the root (at the origin): a sphere emitter of radius
    /// 5, speed 20 +/- 5, life 2 +/- 0.5, initial radius 1, a gravity modifier of the given strength along -Z, an age/death
    /// and a position modifier; optionally a spawn modifier, turbulence, an unknown (vortex field) modifier, and a static
    /// cube [0,10]^3 beside it.</summary>
    static byte[] ParticleNif(float strength = 10f, ushort spawnGenerations = 0, float spawnLife = 1f, float spawnSpeedVariation = 3f,
                              float turbulence = 0f, bool vortex = false, bool withCube = false)
    {
        var f = new NifFile(); f.Create(Ver, withRootNode: true);
        var root = f.GetRootNodes().First(); root.Flags_ui = 0xE;
        int rootIndex = f.Blocks.IndexOf(root);
        if (withCube) root.Children.AddBlockRef(f.AddBlock(Shape("Cube", Box(0, 0, 0, 10, 10, 10))));
        var psys = new NiParticleSystem { Name = new NiStringRef("Sparks"), Flags_ui = 0xE, Scale = 1f, Rotation = Identity33(), WorldSpace = true };
        int psysIndex = f.AddBlock(psys);
        root.Children.AddBlockRef(psysIndex);
        psys.Data_NiBlockRef_NPSD = new(f.AddBlock(new NiPSysData { NumParticles = 0 }));
        psys.Modifiers ??= new();
        void Add(NiPSysModifier m) { m.Target = new(psysIndex); m.Active = true; psys.Modifiers.AddBlockRef(f.AddBlock(m)); }
        var age = new NiPSysAgeDeathModifier { Name = new NiStringRef("Age") };
        Add(age);
        Add(new NiPSysSphereEmitter { Name = new NiStringRef("Emitter"), Radius = 5f, Speed = 20f, SpeedVariation = 5f, LifeSpan = 2f,
                                      LifeSpanVariation = 0.5f, InitialRadius = 1f, RadiusVariation = 0f, EmitterObject = new(rootIndex) });
        if (spawnGenerations > 0)
        {
            var spawn = new NiPSysSpawnModifier { Name = new NiStringRef("Spawn"), NumSpawnGenerations = spawnGenerations, PercentageSpawned = 1f,
                                                  MinNumtoSpawn = 1, MaxNumtoSpawn = 1, SpawnSpeedVariation = spawnSpeedVariation,
                                                  LifeSpan = spawnLife, LifeSpanVariation = 0f };
            spawn.Target = new(psysIndex); spawn.Active = true;
            int si = f.AddBlock(spawn);
            psys.Modifiers.AddBlockRef(si);
            age.SpawnonDeath = true; age.SpawnModifier = new(si);
        }
        Add(new NiPSysGravityModifier { Name = new NiStringRef("Gravity"), GravityAxis = new Vector3(0, 0, -1), Strength = strength,
                                        Turbulence = turbulence, TurbulenceScale = 1f, GravityObject = new(rootIndex) });
        if (vortex) Add(new NiPSysVortexFieldModifier { Name = new NiStringRef("Vortex"), Magnitude = 50f, Direction = new Vector3(0, 0, 1) });
        Add(new NiPSysPositionModifier { Name = new NiStringRef("Position") });
        psys.NumModifiers = (uint)psys.Modifiers.Count;
        return Save(f, "particles");
    }

    /// <summary>A static NIF whose one shape "Frame" (box [100,110] x [0,10] x [0,10]) carries Tree_Anim.</summary>
    static byte[] WindNif()
    {
        var f = new NifFile(); f.Create(Ver, withRootNode: true);
        var root = f.GetRootNodes().First(); root.Flags_ui = 0xE;
        root.Children.AddBlockRef(f.AddBlock(Shape("Frame", Box(100, 0, 0, 110, 10, 10))));
        WindShader(f, "Frame", treeAnim: true);
        return Save(f, "wind");
    }

    public static void RunStageA7(Action<string, bool, string> check)
    {
        // ================= fixture controls (existing code; hold on the base) =================
        Case(check, "stageA7 fixture control: the particle NIF round-trips one NiParticleSystem with its modifiers (emitter radius 5, gravity 10)", () =>
        {
            var n = Load(ParticleNif(spawnGenerations: 2));
            var ps = n.Blocks.OfType<NiParticleSystem>().ToList();
            var em = n.Blocks.OfType<NiPSysSphereEmitter>().FirstOrDefault();
            var gr = n.Blocks.OfType<NiPSysGravityModifier>().FirstOrDefault();
            var sp = n.Blocks.OfType<NiPSysSpawnModifier>().FirstOrDefault();
            return (ps.Count == 1 && ps[0].NumModifiers == 5 && em is { Radius: 5f, Speed: 20f, LifeSpan: 2f } && gr is { Strength: 10f } && sp is { NumSpawnGenerations: 2 },
                    $"systems {ps.Count} modifiers {ps.FirstOrDefault()?.NumModifiers} emitter r {em?.Radius} gravity {gr?.Strength} spawn gens {sp?.NumSpawnGenerations}");
        });
        Case(check, "stageA7 fixture control: the switch NIF has one NiSwitchNode with two child shapes", () =>
        {
            var n = Load(SwitchNif());
            var sw = n.Blocks.OfType<NiSwitchNode>().ToList();
            return (sw.Count == 1 && sw[0].Children.Count == 2, $"switch nodes {sw.Count} children {sw.FirstOrDefault()?.Children.Count}");
        });

        // ================= W: the swept witness's material split (row 1, review Q1) =================
        // A vertical plate x = 0 (y -50..50, z -50..50) through the cube [-20,20]^3: crossing length 4 x 40 = 160.
        // W1 tags the plate alpha_test: the witness must say "alpha_test x opaque" = 160 and no opaque x opaque.
        // Rejects a witness reported without materials, or read as opaque x opaque.
        SweptRegion OnePlate(string tag)
        {
            var plate = new TriangleSoup(new[] { new Vector3(0, -50, -50), new Vector3(0, 50, -50), new Vector3(0, 50, 50), new Vector3(0, -50, 50) }, new[] { 0, 1, 2, 0, 2, 3 });
            return new SweptRegion("built", null, VolumeGeometry.Render, Array.Empty<MotionAnimation>(),
                                   new[] { new SweptSample("seq:Fixture", 0f, plate, new[] { tag, tag }, 0) }, 0, true, false);
        }
        var cube = Cube(0, 0, 0, 20);
        var opaqueTags = Enumerable.Repeat(SurfaceMaterial.Opaque, cube.TriangleCount).ToArray();
        static double TagOf(IReadOnlyDictionary<string, double>? d, string a, string b) => d is not null && d.TryGetValue(SurfaceMaterial.Pair(a, b), out var v) ? v : 0;
        Case(check, "stageA7 W1: alpha-tested swept plate x opaque cube — witness carries 'alpha_test x opaque' = 160, opaque x opaque 0", () =>
        {
            var d = SweptGeometry.Contact(OnePlate(SurfaceMaterial.AlphaTest), cube, opaqueTags, true);
            double at = TagOf(d.WitnessCrossingByTags, SurfaceMaterial.AlphaTest, SurfaceMaterial.Opaque), op = TagOf(d.WitnessCrossingByTags, SurfaceMaterial.Opaque, SurfaceMaterial.Opaque);
            return (d.Decision == "witness" && d.WitnessCrossingByTags is not null && NearA7(at, 160) && op == 0,
                    $"{d.Decision} tags {(d.WitnessCrossingByTags is null ? "null" : string.Join("; ", d.WitnessCrossingByTags.Select(kv => kv.Key + "=" + kv.Value)))}");
        });
        // W2: the same plate opaque: "opaque x opaque" = 160. Rejects the split dropping the opaque crossing.
        Case(check, "stageA7 W2: opaque swept plate x opaque cube — witness carries 'opaque x opaque' = 160", () =>
        {
            var d = SweptGeometry.Contact(OnePlate(SurfaceMaterial.Opaque), cube, opaqueTags, true);
            double op = TagOf(d.WitnessCrossingByTags, SurfaceMaterial.Opaque, SurfaceMaterial.Opaque);
            return (d.Decision == "witness" && NearA7(op, 160), $"{d.Decision} opaque x opaque {op}");
        });

        // ================= R: reach regions (rows 1-3) =================
        // R1: a static off-centre box [0,200]x[-20,20]x[0,100] placed at (500,-300,40), rotation (20,-35,110) deg, scale 2:
        // crossprobe's pinned hand-computed case D, (357.1434,-646.6001,24.4199)..(645.5053,-223.7372,411.8254).
        // Rejects a dropped rotation, a transposed composition and a missing scale (each gives another box).
        Case(check, "stageA7 R1: static mesh placed (500,-300,40) rot (20,-35,110) scale 2 — certified box = crossprobe case D", () =>
        {
            var r = MeshReach.OfModel(StaticNif(("Part", Box(0, -20, 0, 200, 20, 100))), new Vec3(500, -300, 40), new Vec3(20 * DegA7, -35 * DegA7, 110 * DegA7), 2f);
            return (r.Status == "certified" && r.Labels.Count == 0 && BoxIs(r.Box, 357.1434f, -646.6001f, 24.4199f, 645.5053f, -223.7372f, 411.8254f), SayA7(r));
        });
        // R2: MoverA (cube [0,10]^3 sliding +100 in x, CLAMP 0..1 s, plus its static Frame [-200,-180]x[-200,-180]x[-40,-20]) at
        // the origin: the region holds [-200,110]x[-200,10]x[-40,10] and is no wider than the delta in x. Rejects the stored
        // pose used for a moving part (x max 10).
        Case(check, "stageA7 R2: sliding cube — certified region holds the swept box [-200,110]x[-200,10]x[-40,10] (x max <= 110 + delta)", () =>
        {
            var o = new ReachOptions();
            var r = MeshReach.OfModel(MoverA(), Origin, Origin, 1f, null, o);
            var b = r.Box;
            bool holds = !b.IsEmpty && b.Min.X <= -200 + 1e-3 && b.Min.Y <= -200 + 1e-3 && b.Min.Z <= -40 + 1e-3 && b.Max.X >= 110 - 1e-3 && b.Max.Y >= 10 - 1e-3 && b.Max.Z >= 10 - 1e-3;
            return (r.Status == "certified" && holds && b.Max.X <= 110 + o.MotionDelta + 1e-3, SayA7(r));
        });
        // R2b (commit-gate review: R2 cannot see a dropped delta, because a pure translation keeps every pose between samples
        // inside the samples' box). A box x [90,100], y [-1,1] turning about Z from -30 to +60 degrees (LINEAR): its far
        // corners reach x = sqrt(100^2 + 1^2) = 100.005 at 0.573 degrees, BETWEEN samples. At --motion-delta 50 the samples
        // are coarse (quarter turns of the span), so the samples alone reach about 99.3; only the certified delta carries the
        // region past 100.005. Rejects the inner bound (samples without their delta) reported as certified.
        Case(check, "stageA7 R2b: turning box, coarse delta 50 — region x max >= sqrt(10001) (samples alone fall short)", () =>
        {
            var data = Xyz(NoKeys(), NoKeys(), FG(KeyType.LINEAR_KEY, FK(0f, -30 * DegA7), FK(1f, 60 * DegA7)));
            var r = MeshReach.OfModel(Rig(Box(90, -1, -1, 100, 1, 1), Vector3.Zero, data), Origin, Origin, 1f, null, new ReachOptions(MotionDelta: 50));
            double want = Math.Sqrt(10001);
            return (r.Status == "certified" && r.Box.Max.X >= want && r.Box.Max.X <= want + 51, SayA7(r) + $" (want x max >= {want:0.#####})");
        });
        // R3: a switch node's region is the union of its children: z max 300 (the tall child). Rejects the first/active child
        // only (z max 10) and a switch refusal (the motion reader's) where only reach is asked.
        Case(check, "stageA7 R3: switch node — certified region = union of both children, [0,10]x[0,10]x[0,300]", () =>
        {
            var r = MeshReach.OfModel(SwitchNif(), Origin, Origin, 1f);
            return (r.Status == "certified" && BoxIs(r.Box, 0, 0, 0, 10, 10, 300), SayA7(r));
        });
        // R4 (particle_model_v1): T = 2 + 0.5 = 2.5 s, v = 20 + 5 = 25 u/s, a = 10 u/s^2: D = 25*2.5 + 10*2.5^2/2 = 93.75;
        // radius 1; sphere radius 5: box +/-99.75 on every axis. Rejects life variation ignored (D 70), speed variation
        // ignored (D 81.25) and gravity ignored (D 62.5).
        Case(check, "stageA7 R4: particle bound — D 93.75, radius 1, model box +/-99.75 (modelled, assumptions listed)", () =>
        {
            var b = ParticleReach.Bounds(ParticleNif());
            var p = b.Single();
            return (p.Status == "modelled" && NearA7(p.Displacement, 93.75) && NearA7(p.ParticleRadius, 1) && BoxIs(p.ModelBox, -99.75f, -99.75f, -99.75f, 99.75f, 99.75f, 99.75f)
                    && p.Assumptions.Count > 0, $"{p.Status} {p.Reason} D {p.Displacement} r {p.ParticleRadius} {ShowA7(p.ModelBox)} assumptions {p.Assumptions.Count}");
        });
        // R5: two spawn generations of life 1 and speed variation 3: T = 2.5 + 2 = 4.5, v = 25 + 6 = 31, D = 31*4.5 + 10*4.5^2/2
        // = 240.75; box +/-246.75. Rejects spawn generations ignored (box +/-99.75).
        Case(check, "stageA7 R5: particle bound with 2 spawn generations — D 240.75, box +/-246.75", () =>
        {
            var p = ParticleReach.Bounds(ParticleNif(spawnGenerations: 2)).Single();
            return (p.Status == "modelled" && NearA7(p.Displacement, 240.75) && BoxIs(p.ModelBox, -246.75f, -246.75f, -246.75f, 246.75f, 246.75f, 246.75f),
                    $"{p.Status} {p.Reason} D {p.Displacement} {ShowA7(p.ModelBox)}");
        });
        // R6: an unknown modifier (a vortex field) and a non-zero turbulence each refuse by name. Rejects a number.
        Case(check, "stageA7 R6: vortex field modifier and turbulence 5 each refuse by name (no number)", () =>
        {
            var v = ParticleReach.Bounds(ParticleNif(vortex: true)).Single();
            var t = ParticleReach.Bounds(ParticleNif(turbulence: 5f)).Single();
            return (v.Status == "refused" && (v.Reason ?? "").Contains("NiPSysVortexFieldModifier") && t.Status == "refused" && (t.Reason ?? "").Contains("turbulence"),
                    $"vortex {v.Status} {v.Reason}; turbulence {t.Status} {t.Reason}");
        });
        // R4b: a mesh with a static cube and a particle system: region uncertified, labelled with the particle label, box = the
        // union of the cube and the particle model box, the particle part listed. Rejects a particle mesh reported certified.
        Case(check, "stageA7 R4b: cube + particles — region 'uncertified: particle dynamics model', box +/-99.75 with the part listed", () =>
        {
            var r = MeshReach.OfModel(ParticleNif(withCube: true), Origin, Origin, 1f);
            return (r.Status == "uncertified" && r.Labels.Contains(MeshReach.ParticleLabel) && BoxIs(r.Box, -99.75f, -99.75f, -99.75f, 99.75f, 99.75f, 99.75f)
                    && r.UncertifiedParts.Any(x => x.Label == MeshReach.ParticleLabel), SayA7(r));
        });
        // R9 (review F2): a Tree_Anim shape makes the region uncertified with the wind label; the part's own box is its file
        // pose [100,110]x[0,10]x[0,10]. Rejects a wind-swayed region reported certified.
        Case(check, "stageA7 R9: Tree_Anim shape — region 'uncertified: wind amplitude', part box [100,110]x[0,10]x[0,10]", () =>
        {
            var r = MeshReach.OfModel(WindNif(), Origin, Origin, 1f);
            var part = r.UncertifiedParts?.FirstOrDefault(x => x.Label == MeshReach.WindLabel);
            return (r.Status == "uncertified" && r.Labels.Contains(MeshReach.WindLabel) && part is not null && BoxIs(part.Box, 100, 0, 0, 110, 10, 10), SayA7(r));
        });
        // R9b (found in the G1 run: FloraDeathBell01 is a switch mesh whose shapes also carry Tree_Anim; the motion reader
        // reports the switch first, so the wind label was lost): a Tree_Anim shape under a switch node still makes the
        // region uncertified with the wind label and the part's own box. Rejects a wind-swayed switch child read certified.
        Case(check, "stageA7 R9b: Tree_Anim shape under a switch node — 'uncertified: wind amplitude', part box [0,10]x[0,10]x[0,300]", () =>
        {
            var f = new NifFile(); f.Create(Ver, withRootNode: true);
            var root = f.GetRootNodes().First(); root.Flags_ui = 0xE;
            var sw = new NiSwitchNode { Name = new NiStringRef("Sw"), Flags_ui = 0xE, Scale = 1f, Rotation = Identity33() };
            root.Children.AddBlockRef(f.AddBlock(sw));
            sw.Children.AddBlockRef(f.AddBlock(Shape("Short", Box(0, 0, 0, 10, 10, 10))));
            sw.Children.AddBlockRef(f.AddBlock(Shape("Tall", Box(0, 0, 0, 10, 10, 300))));
            WindShader(f, "Tall", treeAnim: true);
            var r = MeshReach.OfModel(Save(f, "switch wind"), Origin, Origin, 1f);
            var part = r.UncertifiedParts?.FirstOrDefault(x => x.Label == MeshReach.WindLabel);
            return (r.Status == "uncertified" && r.Labels.Contains(MeshReach.WindLabel) && part is not null && BoxIs(part.Box, 0, 0, 0, 10, 10, 300)
                    && BoxIs(r.Box, 0, 0, 0, 10, 10, 300), SayA7(r));
        });
        // R10 (review note, wind margin): the margin printed beside a wind label is the gap from the wind PART's box to the
        // record's box [200,300]x[0,10]x[0,10]: 90. Rejects the margin measured from the reference position (200).
        Case(check, "stageA7 R10: wind margin = gap from the wind part's box to the record box = 90 (not 200 from the reference position)", () =>
        {
            var r = MeshReach.OfModel(WindNif(), Origin, Origin, 1f);
            var rec = new Aabb(new Vec3(200, 0, 0), new Vec3(300, 10, 10), false);
            var part = r.UncertifiedParts.First(x => x.Label == MeshReach.WindLabel);
            double g = MeshReach.Gap(part.Box, rec);
            double diag = MeshReach.Gap(new Aabb(new Vec3(0, 0, 0), new Vec3(1, 1, 1), false), new Aabb(new Vec3(4, 5, 1), new Vec3(6, 6, 2), false));
            return (NearA7(g, 90) && NearA7(diag, 5), $"gap {g}; diagonal gap {diag} (want 5: dx 3, dy 4, dz 0)");
        });
        // R7: a model-less primitive box (half 10, 20, 30) at (100,0,0), yaw 90: solid layer -> certified box
        // [80,120]x[-10,10]x[-30,30] (+X lands on -Y); non-solid -> no_geometry; no layer stated -> refused naming the layer.
        // Rejects an invisible collision box treated as no geometry, and a guessed default layer.
        Case(check, "stageA7 R7: primitive box — solid: certified [80,120]x[-10,10]x[-30,30]; non-solid: no_geometry; absent layer: refused", () =>
        {
            var at = new Vec3(100, 0, 0); var yaw = new Vec3(0, 0, 90 * DegA7);
            var s = MeshReach.OfPrimitive(new Vector3(10, 20, 30), at, yaw, true);
            var n = MeshReach.OfPrimitive(new Vector3(10, 20, 30), at, yaw, false);
            var u = MeshReach.OfPrimitive(new Vector3(10, 20, 30), at, yaw, null);
            return (s.Status == "certified" && BoxIs(s.Box, 80, -10, -30, 120, 10, 30) && n.Status == "no_geometry" && u.Status == "refused" && (u.Reason ?? "").Contains("layer"),
                    $"solid {SayA7(s)}; non-solid {n.Status}; absent {u.Status} {u.Reason}");
        });
        // R8: Meets is closed: a box touching at x = 10 meets; at x = 10.01 does not; a refused region meets everything.
        // Rejects an off-by-rounding exclusion of a touching neighbour and a refusal read as "out of reach".
        Case(check, "stageA7 R8: Meets — touching meets, 0.01 u apart does not, a refused region meets everything", () =>
        {
            var r = MeshReach.OfModel(StaticNif(("C", Box(0, 0, 0, 10, 10, 10))), Origin, Origin, 1f);
            var touch = new Aabb(new Vec3(10, 0, 0), new Vec3(20, 10, 10), false);
            var apart = new Aabb(new Vec3(10.01f, 0, 0), new Vec3(20, 10, 10), false);
            var refused = new ReachRegion("refused", "fixture", Aabb.Empty, MeshReach.PolicyVersion, Array.Empty<string>(), Array.Empty<ReachPart>());
            return (MeshReach.Meets(r, touch) && !MeshReach.Meets(r, apart) && MeshReach.Meets(refused, apart), SayA7(r));
        });
    }
}
