using System.Buffers.Binary;

namespace HarnessShared;

// What makes a delta unfit for a population (PopulationScope refuses it; evalrun's plan leaves the checks on the whole
// order instead of planning a population the probes would refuse). The probes test a delta plugin's records as Mutagen
// reads them; the controller, which loads no records, counts the plugin's top-level groups from their headers.
public static class PopulationRules
{
    /// <summary>The record type whose presence in a delta plugin refuses a population: it changes how every reference collides.</summary>
    public const string CollisionLayers = "COLL";
    /// <summary>The record type that sends the state route check to the whole order: every actor band reads it.</summary>
    public const string Races = "RACE";

    /// <summary>A scoped emit's population: the enable population, then the first 12 hex of the population file's sha256.</summary>
    public static string Identity(string basePopulation, string sha256) => $"{basePopulation}+population:{sha256[..12].ToLowerInvariant()}";

    /// <summary>A Base Object Swapper ini, by its name or relative path: a mod folder shipping one cannot be scoped.</summary>
    public static bool IsSwapIni(string path) => path.EndsWith("_swap.ini", StringComparison.OrdinalIgnoreCase);

    public static bool CarriesCollisionLayers(IReadOnlyDictionary<string, long>? topGroups) =>
        topGroups is not null && topGroups.GetValueOrDefault(CollisionLayers) > 0;

    public static bool CarriesRaces(IReadOnlyDictionary<string, long>? topGroups) =>
        topGroups is not null && topGroups.GetValueOrDefault(Races) > 0;

    /// <summary>A plugin's top-level groups and how many records each holds, nested groups' records included, read from
    /// the group headers alone (they are never compressed). Null when the bytes do not start with a TES4 header.</summary>
    public static Dictionary<string, long>? TopGroupRecords(byte[] data)
    {
        if (data.Length < 24 || data[0] != 'T' || data[1] != 'E' || data[2] != 'S' || data[3] != '4') return null;
        long Size(long at) => BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan((int)at + 4, 4));
        bool Group(long at) => data[at] == 'G' && data[at + 1] == 'R' && data[at + 2] == 'U' && data[at + 3] == 'P';

        long Count(long from, long to)
        {
            long n = 0, q = from;
            while (q + 24 <= to && q + 24 <= data.Length)
            {
                long size = Size(q);
                if (Group(q)) { n += Count(q + 24, q + size); q += Math.Max(size, 24); }
                else { n++; q += 24 + size; }
            }
            return n;
        }

        var groups = new Dictionary<string, long>(StringComparer.Ordinal);
        long p = 24 + Size(0);
        while (p + 24 <= data.Length && Group(p))
        {
            long size = Size(p);
            string label = string.Concat(data.AsSpan((int)p + 8, 4).ToArray().Select(b => b < 0x80 ? (char)b : '\uFFFD'));
            groups[label] = groups.GetValueOrDefault(label) + Count(p + 24, p + size);
            p += Math.Max(size, 24);
        }
        return groups;
    }
}
