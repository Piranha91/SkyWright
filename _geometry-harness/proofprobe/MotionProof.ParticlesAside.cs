using HousecarlCore;

/// <summary>Known answers for ReachOptions.ParticlesAside (2026-11-08): the region of the render shapes alone, for a consumer that
/// needs a body (particles are runtime state that nothing rests on or meets). Fixture: ParticleNif(withCube: true), a cube
/// [0,10]^3 beside a particle system whose default region is +/-99.75 (stage A7 R4b). Red until the option is implemented.</summary>
static partial class MotionProof
{
    public static void RunParticlesAside(Action<string, bool, string> check)
    {
        var aside = new ReachOptions(ParticlesAside: true);
        // PA-1: no rules: the box is the cube, no particle part, labelled as set aside. Rejects the particle box kept.
        Case(check, "particlesAside PA-1: cube + particles, no rules — box [0,10]^3, no part, labelled", () =>
        {
            var r = MeshReach.OfModel(ParticleNif(withCube: true), Origin, Origin, 1f, null, aside);
            return (BoxIs(r.Box, 0, 0, 0, 10, 10, 10) && r.UncertifiedParts.Count == 0 && r.Labels.Contains(MeshReach.ParticlesAsideLabel)
                    && !r.Labels.Contains(MeshReach.ParticleLabel), SayA7(r));
        });
        // PA-2: under the reach rules with the particle model: the same cube. Rejects the rules path keeping the particle model box.
        Case(check, "particlesAside PA-2: cube + particles, rules particles — box [0,10]^3, no part, labelled", () =>
        {
            var r = MeshReach.OfModel(ParticleNif(withCube: true), Origin, Origin, 1f, null, aside with { Rules = new ReachRules(Particles: true) });
            return (BoxIs(r.Box, 0, 0, 0, 10, 10, 10) && r.UncertifiedParts.Count == 0 && r.Labels.Contains(MeshReach.ParticlesAsideLabel)
                    && !r.Labels.Contains(MeshReach.ParticleModelLabel), SayA7(r));
        });
        // PA-3 (guard): the option off leaves the default region (R4b): box +/-99.75 with the particle part.
        Case(check, "particlesAside PA-3: option off — default region unchanged (+/-99.75, part listed)", () =>
        {
            var r = MeshReach.OfModel(ParticleNif(withCube: true), Origin, Origin, 1f);
            return (BoxIs(r.Box, -99.75f, -99.75f, -99.75f, 99.75f, 99.75f, 99.75f) && r.UncertifiedParts.Any(x => x.Label == MeshReach.ParticleLabel), SayA7(r));
        });
        // PA-4 (guard): a particle-free mesh, placed and turned, is identical with and without the option.
        Case(check, "particlesAside PA-4: particle-free mesh — identical region with the option", () =>
        {
            var nif = StaticNif(("Part", Box(0, -20, 0, 200, 20, 100)));
            var p = new Vec3(500, -300, 40); var rot = new Vec3(20 * DegA7, -35 * DegA7, 110 * DegA7);
            var a = MeshReach.OfModel(nif, p, rot, 2f); var b = MeshReach.OfModel(nif, p, rot, 2f, null, aside);
            return (a.Status == b.Status && a.Box.Equals(b.Box) && a.Labels.SequenceEqual(b.Labels) && b.UncertifiedParts.Count == 0, SayA7(a) + " | " + SayA7(b));
        });
        // PA-5: a particle-only mesh has no body: no_geometry, labelled. Rejects the particle box returned as a body.
        Case(check, "particlesAside PA-5: particles only — no_geometry, labelled", () =>
        {
            var r = MeshReach.OfModel(ParticleNif(), Origin, Origin, 1f, null, aside);
            return (r.Status == "no_geometry" && r.Box.IsEmpty && r.Labels.Contains(MeshReach.ParticlesAsideLabel), SayA7(r));
        });
    }
}
