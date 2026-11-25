using HousecarlCore;
using SkyWright.Engine;

namespace HarnessShared;

/// <summary>Which state a cell's route field (actor band and floor) is built for. The floor and the band belong to the
/// STATE being judged (Q27), but a state differs from the baseline only on the references its flip changes, so the
/// baseline field is reused when none of the references the field is built from changes.</summary>
public static class StateFieldKey
{
    /// <summary>The key for <paramref name="arm"/>'s field: the arm's name, or the baseline's when no reference in
    /// <paramref name="ownRefs"/> (the band's) or <paramref name="floorRefs"/> (the floor's: the 3x3 and its reach for an
    /// exterior cell) is enabled differently under the arm than at baseline.</summary>
    public static string For(StateArm arm, IEnumerable<PlacedRef> ownRefs, IEnumerable<PlacedRef>? floorRefs,
                             EnablePopulation pop, EnablePopulation baseline)
    {
        if (arm.IsBaseline) return EnablePopulation.BaselineStateName;
        bool Differs(PlacedRef r) => pop.Enabled(r.FormKey) != baseline.Enabled(r.FormKey);
        return ownRefs.Any(Differs) || (floorRefs?.Any(Differs) ?? false) ? arm.Name : EnablePopulation.BaselineStateName;
    }
}
