using NiflySharp;
using NiflySharp.Blocks;
using NiflySharp.Enums;
using HousecarlCore;

/// <summary>Known answers for the bomb modifier's axis (2026-10-13, lane B item 4). The Gamebryo 2.3 SDK's NiPSysBombModifier::Update
/// adds fDecay x DeltaV x dt x kForce to a particle's velocity, where kForce is the bomb axis under PLANAR_SYMMETRY (sign by the side of
/// the plane), the direction from the bomb under SPHERICAL_SYMMETRY and the direction from the axis under CYLINDRICAL_SYMMETRY. The
/// particle law took every bomb as an acceleration on every axis; a planar bomb on a still frame pushes along its axis only. The
/// fixture is ParticleNif (v 25, T 2.5, planar gravity 10 along -z, emitter half 5): x, y half LatPM, z half AxPM; a bomb of DeltaV 10
/// adds 10 x (T^2/2 + T dt/2) = 31.666667 on each axis it acts on. Red at the test commit except the controls.</summary>
static partial class MotionProof
{
    static readonly double BombTerm = 10 * (3.125 + 2.5 / 60);   // 31.666667

    static byte[] BombVariant(SymmetryType symm, System.Numerics.Vector3 axis, string what) => ParticleVariant((f, ps, root) =>
        AddModifier(f, ps, new NiPSysBombModifier
        {
            Name = new NiStringRef("Bomb"), BombObject = new(f.Blocks.IndexOf(root)), BombAxis = axis, Decay = 0f, DeltaV = 10f,
            DecayType = DecayType.DECAY_NONE, SymmetryType = symm, Order = NiPSysModifierOrder.ORDER_FORCE,
        }), what);

    public static void RunBombAxis(Action<string, bool, string> check)
    {
        // K-PB1: a planar bomb along +z on the still root: its push is added on z only. x, y half LatPM (68.91421); z half AxPM +
        // 31.666667. Rejects the bomb on every axis (x, y half 100.58088).
        Case(check, "pmodel K-PB1: a planar bomb along z - x, y half 68.91421, z half AxPM + 31.666667", () =>
        {
            var p = ParticleReach.BoundsFromModifiers(BombVariant(SymmetryType.PLANAR_SYMMETRY, new(0, 0, 1), "planar bomb z")).Single();
            return (p.Status == "modelled" && HalfIs(p.ModelBox, LatPM, LatPM, AxPM + BombTerm), ShowPB(p));
        });
        // K-PB2: the same bomb along +x: its push is added on x only (z keeps gravity alone). Rejects the bomb on every axis.
        Case(check, "pmodel K-PB2: a planar bomb along x - x half LatPM + 31.666667, y half LatPM, z half AxPM", () =>
        {
            var p = ParticleReach.BoundsFromModifiers(BombVariant(SymmetryType.PLANAR_SYMMETRY, new(1, 0, 0), "planar bomb x")).Single();
            return (p.Status == "modelled" && HalfIs(p.ModelBox, LatPM + BombTerm, LatPM, AxPM), ShowPB(p));
        });
        // K-PB3 (control): a spherical bomb has no axis: every half grows by the bomb term. Rejects dropping the bomb.
        Case(check, "pmodel K-PB3 (control): a spherical bomb - every axis grows by 31.666667", () =>
        {
            var p = ParticleReach.BoundsFromModifiers(BombVariant(SymmetryType.SPHERICAL_SYMMETRY, new(0, 0, 1), "spherical bomb")).Single();
            return (p.Status == "modelled" && HalfIs(p.ModelBox, LatPM + BombTerm, LatPM + BombTerm, AxPM + BombTerm), ShowPB(p));
        });
        // K-PB4 (control): a cylindrical bomb pushes away from its axis, so every axis keeps the term (the bound is not tightened).
        Case(check, "pmodel K-PB4 (control): a cylindrical bomb - every axis grows by 31.666667", () =>
        {
            var p = ParticleReach.BoundsFromModifiers(BombVariant(SymmetryType.CYLINDRICAL_SYMMETRY, new(0, 0, 1), "cylindrical bomb")).Single();
            return (p.Status == "modelled" && HalfIs(p.ModelBox, LatPM + BombTerm, LatPM + BombTerm, AxPM + BombTerm), ShowPB(p));
        });
    }
}
