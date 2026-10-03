using System.Numerics;
using HousecarlCore;
using NiflySharp;
using NiflySharp.Blocks;
using NiflySharp.Enums;
using NiflySharp.Structs;

/// <summary>Known answers for particle_model_v2 (`ParticleReach.BoundsFromModifiers`, the reach rule `particles`): the particle
/// extent from the file's modifiers, every field named from nif.xml (NiPSysEmitter Speed / Speed Variation / Life Span / Life
/// Span Variation / Initial Radius / Radius Variation; NiPSysGravityModifier Gravity Object / Gravity Axis / Strength / Force Type
/// / World Aligned; NiPSysCollider; BSPSysScaleModifier Scales; BSPSysLODModifier End Size; NiPSysGrowFadeModifier Base Scale;
/// NiPSysData vertices). Expected values are analytic, stated beside each case, on the Stage A particle fixture (sphere emitter
/// radius 5 on the root, Speed 20 +/- 5, Life Span 2 +/- 0.5, Initial Radius 1, planar gravity 10 along the root's -z):
/// v T = 25 x 2.5 = 62.5; a T^2/2 = 10 x 2.5^2 / 2 = 31.25; quad corner r sqrt 2 = 1.41421356. Red on the stub (v1).</summary>
static partial class MotionProof
{
    const double Sqrt2 = 1.4142135623730951;
    static readonly double IsoPM = 62.5 + Sqrt2;           // v T + r sqrt2
    static readonly double LatPM = 5 + IsoPM;              // emitter radius + iso
    static readonly double AxPM = 5 + IsoPM + 31.25;       // + the planar gravity term

    static byte[] ParticleVariant(Action<NifFile, NiParticleSystem, NiNode> mutate, string what)
    {
        var f = Load(ParticleNif());
        var psys = f.Blocks.OfType<NiParticleSystem>().Single();
        mutate(f, psys, f.GetRootNodes().First());
        return Save(f, what);
    }

    static void AddModifier(NifFile f, NiParticleSystem psys, NiPSysModifier m)
    {
        m.Target = new(f.Blocks.IndexOf(psys)); m.Active = true;
        psys.Modifiers.AddBlockRef(f.AddBlock(m)); psys.NumModifiers = (uint)psys.Modifiers.Count;
    }

    /// <summary>A child node of the root rotated a quarter turn about y (rows (0,0,-1), (0,1,0), (1,0,0)): its local z lies along the
    /// model x axis under either matrix sense.</summary>
    static int TurnedNode(NifFile f, NiNode root)
    {
        var n = new NiNode { Name = new NiStringRef("GravFrame"), Flags_ui = 0xE, Scale = 1f,
                             Rotation = new Matrix33 { M11 = 0, M12 = 0, M13 = -1, M21 = 0, M22 = 1, M23 = 0, M31 = 1, M32 = 0, M33 = 0 } };
        int i = f.AddBlock(n); root.Children.AddBlockRef(i); return i;
    }

    static bool HalfIs(Aabb b, double hx, double hy, double hz, double tol = 1e-3) =>
        !b.IsEmpty && NearA7(-b.Min.X, hx, tol) && NearA7(b.Max.X, hx, tol) && NearA7(-b.Min.Y, hy, tol) && NearA7(b.Max.Y, hy, tol)
        && NearA7(-b.Min.Z, hz, tol) && NearA7(b.Max.Z, hz, tol);

    static string ShowPB(ParticleBound p) => $"{p.Status} {p.Reason} D {p.Displacement:0.####} r {p.ParticleRadius:0.####} box {ShowA7(p.ModelBox)}"
        + $" worldReading {(p.WorldReadingBox is { } w ? ShowA7(w) : "-")} worldGrow ({p.WorldGrow.X:0.####},{p.WorldGrow.Y:0.####},{p.WorldGrow.Z:0.####})";

    public static void RunParticleModel(Action<string, bool, string> check)
    {
        // ================= fixture controls (existing code; hold on the base) =================
        Case(check, "pmodel fixture control: v1 reads the fixture as before (D 93.75, box +/-99.75)", () =>
        {
            var p = ParticleReach.Bounds(ParticleNif()).Single();
            return (p.Status == "modelled" && NearA7(p.Displacement, 93.75) && HalfIs(p.ModelBox, 99.75, 99.75, 99.75), ShowPB(p));
        });
        Case(check, "pmodel fixture control: a turned gravity frame and a collider round-trip through save/load", () =>
        {
            var b = ParticleVariant((f, ps, root) =>
            {
                int gi = TurnedNode(f, root);
                foreach (var g in f.Blocks.OfType<NiPSysGravityModifier>()) g.GravityObject = new(gi);
            }, "turned gravity frame");
            var f2 = Load(b); var gm = f2.Blocks.OfType<NiPSysGravityModifier>().Single();
            var gn = f2.Blocks[gm.GravityObject.Index] as NiNode;
            return (gn?.Name?.String == "GravFrame" && gn.Rotation.M31 == 1, $"{gn?.Name?.String} M31 {gn?.Rotation.M31}");
        });

        // ================= K-PM1: planar gravity grows only its own axis =================
        // Force Type FORCE_PLANAR (0), Gravity Axis (0,0,-1) in the root's frame: x, y half = 5 + 62.5 + 1.41421 = 68.91421; z half = 68.91421
        // + 31.25 = 100.16421. Rejects gravity applied on every axis (x 100.16), gravity dropped (z 68.91) and the quad corner dropped (x 68.5).
        Case(check, "pmodel K-PM1: planar gravity — x, y half 68.91421, z half 100.16421 (modelled, policy v2)", () =>
        {
            var p = ParticleReach.BoundsFromModifiers(ParticleNif()).Single();
            return (p.Status == "modelled" && HalfIs(p.ModelBox, LatPM, LatPM, AxPM) && p.WorldReadingBox is null && NearA7(p.ParticleRadius, Sqrt2), ShowPB(p));
        });
        // ================= K-PM2: a spherical force has no axis =================
        Case(check, "pmodel K-PM2: FORCE_SPHERICAL — every half 100.16421", () =>
        {
            var b = ParticleVariant((f, ps, root) => { foreach (var g in f.Blocks.OfType<NiPSysGravityModifier>()) g.ForceType = ForceType.FORCE_SPHERICAL; }, "spherical gravity");
            var p = ParticleReach.BoundsFromModifiers(b).Single();
            return (p.Status == "modelled" && HalfIs(p.ModelBox, AxPM, AxPM, AxPM), ShowPB(p));
        });
        // ================= K-PM3: a collider can turn gravity's speed sideways =================
        // A spherical collider with bounce 0.5 (accepted, <= 1): the anisotropic bound no longer holds; every half 100.16421.
        Case(check, "pmodel K-PM3: a collider (bounce 0.5) with planar gravity — every half 100.16421", () =>
        {
            var b = ParticleVariant((f, ps, root) =>
            {
                int ci = f.AddBlock(new NiPSysSphericalCollider { Radius = 3f, Bounce = 0.5f, NextCollider = new(-1), SpawnModifier = new(-1), ColliderObject = new(f.Blocks.IndexOf(root)) });
                AddModifier(f, ps, new NiPSysColliderManager { Name = new NiStringRef("Colliders"), Collider = new(ci) });
            }, "collider");
            var p = ParticleReach.BoundsFromModifiers(b).Single();
            return (p.Status == "modelled" && HalfIs(p.ModelBox, AxPM, AxPM, AxPM), ShowPB(p));
        });
        // ================= K-PM4: the gravity axis is in the Gravity Object's frame =================
        // Gravity Object = a node turned a quarter about y: the axis lies along model x. x half 100.16421, y, z 68.91421. Rejects the
        // object's rotation ignored (z 100.16).
        Case(check, "pmodel K-PM4: gravity object turned (local z -> model x), World Aligned false — x half 100.16421, y, z 68.91421", () =>
        {
            var b = ParticleVariant((f, ps, root) =>
            {
                int gi = TurnedNode(f, root);
                foreach (var g in f.Blocks.OfType<NiPSysGravityModifier>()) { g.GravityObject = new(gi); g.WorldAligned = false; }
            }, "turned gravity frame");
            var p = ParticleReach.BoundsFromModifiers(b).Single();
            return (p.Status == "modelled" && HalfIs(p.ModelBox, AxPM, LatPM, LatPM) && p.WorldReadingBox is null, ShowPB(p));
        });
        // ================= K-PM5: World Aligned has no description in nif.xml: both readings are held =================
        // The same turned frame with World Aligned true. Reading A (the object's frame): model box x half 100.16421. Reading B (the world
        // frame): the box without gravity (every half 68.91421) plus a world-axis growth (0, 0, 31.25) applied after placement. Rejects
        // either reading alone.
        byte[] aligned = ParticleVariant((f, ps, root) =>
        {
            int gi = TurnedNode(f, root);
            foreach (var g in f.Blocks.OfType<NiPSysGravityModifier>()) { g.GravityObject = new(gi); g.WorldAligned = true; }
        }, "world aligned gravity");
        Case(check, "pmodel K-PM5: World Aligned true — object-frame box x 100.16421 AND world reading (box 68.91421, world growth z 31.25)", () =>
        {
            var p = ParticleReach.BoundsFromModifiers(aligned).Single();
            return (p.Status == "modelled" && HalfIs(p.ModelBox, AxPM, LatPM, LatPM) && p.WorldReadingBox is { } w && HalfIs(w, LatPM, LatPM, LatPM)
                    && NearA7(p.WorldGrow.X, 0) && NearA7(p.WorldGrow.Y, 0) && NearA7(p.WorldGrow.Z, 31.25), ShowPB(p));
        });
        // K-PM5b: through MeshReach at the origin, unrotated, scale 1: the union of the two readings, x and z half 100.16421, y 68.91421.
        Case(check, "pmodel K-PM5b: MeshReach, particles rule — the world-aligned fixture's region is the union of both readings (x, z 100.16421; y 68.91421)", () =>
        {
            var r = MeshReach.OfModel(aligned, Origin, Origin, 1f, null, new ReachOptions(0.5, 4096, new ReachRules(Particles: true)));
            return (r.Status == "uncertified" && HalfIs(r.Box, AxPM, LatPM, AxPM, 1e-2), ShowRR(r));
        });
        // ================= K-PM6: particle size terms =================
        // Scales key 1.5 (BSPSysScaleModifier), End Size 2 (BSPSysLODModifier), Base Scale 3 (NiPSysGrowFadeModifier): r = 1 x 1.5 x 2 x 3
        // x sqrt2 = 12.72792; x half = 5 + 62.5 + 12.72792 = 80.22792. Rejects any one multiplier dropped (x 71.2, 67.86, 63.3...).
        Case(check, "pmodel K-PM6: size — scale key 1.5, LOD End Size 2, grow-fade Base Scale 3: r 12.72792, x half 80.22792", () =>
        {
            var b = ParticleVariant((f, ps, root) =>
            {
                AddModifier(f, ps, new BSPSysScaleModifier { Name = new NiStringRef("Scale"), Scales = new List<float> { 0.5f, 1.5f, 1f }, NumScales = 3 });
                AddModifier(f, ps, new BSPSysLODModifier { Name = new NiStringRef("LOD"), LODBeginDistance = 0.1f, LODEndDistance = 0.7f, EndEmitScale = 0.2f, EndSize = 2f });
                AddModifier(f, ps, new NiPSysGrowFadeModifier { Name = new NiStringRef("GrowFade"), GrowTime = 0.1f, FadeTime = 0.1f, BaseScale = 3f });
            }, "size terms");
            var p = ParticleReach.BoundsFromModifiers(b).Single();
            double r = 9 * Sqrt2;
            return (p.Status == "modelled" && NearA7(p.ParticleRadius, r) && HalfIs(p.ModelBox, 5 + 62.5 + r, 5 + 62.5 + r, 5 + 62.5 + r + 31.25), ShowPB(p));
        });
        // ================= K-PM7: the particle system node's own scale =================
        // NiParticleSystem Scale 2: the growth doubles (the emitter sits on the root, unscaled): x half 5 + 2 x 63.91421 = 132.82843,
        // z half 5 + 2 x 95.16421 = 195.32843. Rejects the system node's scale ignored (x 68.91).
        Case(check, "pmodel K-PM7: particle system node Scale 2 — x half 132.82843, z half 195.32843", () =>
        {
            var b = ParticleVariant((f, ps, root) => ps.Scale = 2f, "scaled system");
            var p = ParticleReach.BoundsFromModifiers(b).Single();
            return (p.Status == "modelled" && HalfIs(p.ModelBox, 5 + 2 * IsoPM, 5 + 2 * IsoPM, 5 + 2 * (IsoPM + 31.25)), ShowPB(p));
        });
        // ================= K-PM8: refusals by name =================
        Case(check, "pmodel K-PM8: FORCE_UNKNOWN refuses naming the gravity modifier and the force type", () =>
        {
            var b = ParticleVariant((f, ps, root) => { foreach (var g in f.Blocks.OfType<NiPSysGravityModifier>()) g.ForceType = ForceType.FORCE_UNKNOWN; }, "unknown force");
            var p = ParticleReach.BoundsFromModifiers(b).Single();
            return (p.Status == "refused" && (p.Reason ?? "").Contains("Gravity") && (p.Reason ?? "").Contains("FORCE_UNKNOWN"), ShowPB(p));
        });
        Case(check, "pmodel K-PM8b: an unknown modifier (vortex) and turbulence 5 still refuse by name under v2", () =>
        {
            var v = ParticleReach.BoundsFromModifiers(ParticleNif(vortex: true)).Single();
            var t = ParticleReach.BoundsFromModifiers(ParticleNif(turbulence: 5f)).Single();
            return (v.Status == "refused" && (v.Reason ?? "").Contains("NiPSysVortexFieldModifier") && t.Status == "refused" && (t.Reason ?? "").Contains("turbulence"),
                    $"{ShowPB(v)}; {ShowPB(t)}");
        });
        // ================= K-PM9: the reach rule uses v2 with its label; rules off is v1 =================
        Case(check, "pmodel K-PM9: MeshReach, particles rule — cube + particles: label names the file's modifiers, box x, y 68.91421, z 100.16421", () =>
        {
            var r = MeshReach.OfModel(ParticleNif(withCube: true), Origin, Origin, 1f, null, new ReachOptions(0.5, 4096, new ReachRules(Particles: true)));
            return (r.Status == "uncertified" && r.Labels.Contains(MeshReach.ParticleModelLabel) && !r.Labels.Contains(MeshReach.ParticleLabel)
                    && HalfIs(r.Box, LatPM, LatPM, AxPM, 1e-2), ShowRR(r));
        });
        Case(check, "pmodel K-PM9b: rules off — the same mesh keeps the v1 label and box +/-99.75 (default output unchanged)", () =>
        {
            var r = MeshReach.OfModel(ParticleNif(withCube: true), Origin, Origin, 1f);
            return (r.Status == "uncertified" && r.Labels.Contains(MeshReach.ParticleLabel) && HalfIs(r.Box, 99.75, 99.75, 99.75), ShowRR(r));
        });
    }
}
