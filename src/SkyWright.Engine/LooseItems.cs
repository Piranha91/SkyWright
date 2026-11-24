using HousecarlCore;

namespace SkyWright.Engine;

// LOOSE ITEMS — which placed bases are loose items a player sees and moves (loose-item clashes, 2026-10-03): the item record
// types overlapprobe's reachLoose has always read, plus, under the rest rule, Light bases carrying the Can Be Carried flag
// (torches, lanterns). Flora is excluded (fixed harvestables).

public static class LooseItems
{
    /// <summary>The item record types (Mutagen record type names) read as loose items since Parts 1-2.</summary>
    public static readonly IReadOnlySet<string> ItemTypes = new HashSet<string>(StringComparer.Ordinal)
        { "Ingestible", "MiscItem", "Ingredient", "Book", "Armor", "Weapon", "SoulGem", "Scroll", "Ammunition", "Key" };

    /// <summary>Whether a base of <paramref name="recordType"/> is a loose item. A Light counts only when
    /// <paramref name="includeCarryableLights"/> (the rest rule) and its Can Be Carried flag is set.</summary>
    public static bool IsLooseBase(string? recordType, bool lightCanBeCarried, bool includeCarryableLights)
        => recordType is not null && (ItemTypes.Contains(recordType) || (recordType == "Light" && lightCanBeCarried && includeCarryableLights));
}
