using HousecarlCore;
using Mutagen.Bethesda.Plugins;

namespace HarnessShared;

// The floor half of the --moved-ref-selftest frame (MovedRefSelfTest.cs), compiled only into probes that file a floor.
public sealed partial class MovedRefSelfTest
{
    /// <summary>The floor filing of worldspace W: no reference in two buckets, the (0,0) neighbourhood equal to the
    /// fixture's literal, and the LAND of each named grid as the fixture's table says (L1 and L4 unreadable and named, L2
    /// the master's, L3 and L5 the later plugin's). <paramref name="unreadable"/> is the walk's named-unreadable set, or
    /// null when the walk has none (then L1 and L4 fail by name).</summary>
    public void CheckFloor(string prefix, Dictionary<(int, int), Dictionary<FormKey, PlacedRef>>? w, Dictionary<(int, int), float[,]>? land,
                           ISet<(int X, int Y)>? unreadable, IEnumerable<(int X, int Y)> landGrids)
    {
        w ??= new(); land ??= new();
        var seen = new Dictionary<FormKey, List<(int, int)>>();
        foreach (var (g, d) in w) foreach (var key in d.Keys) { if (!seen.TryGetValue(key, out var l)) seen[key] = l = new(); l.Add(g); }
        var dup = seen.Where(kv => kv.Value.Count > 1).Select(kv => $"{Name(kv.Key)} in {string.Join(" ", kv.Value.OrderBy(x => x))}").OrderBy(x => x).ToList();
        Check($"{prefix} F-once  no reference in two grid buckets", dup.Count == 0, string.Join("; ", dup));
        var (nb, _) = FloorFeed.Neighbourhood((0, 0), w, land);
        var expect = MovedRefFixture.ExpectedFloor00.ToDictionary(kv => kv.Key, kv => ((FormKey?)null, kv.Value.X, kv.Value.Y, false));
        CheckSet($"{prefix} F-nb00  the (0,0) neighbourhood: each floor candidate once, at its last position",
                 nb.Select(r => (r.FormKey, (FormKey?)null, r)), expect);
        string LandAt((int, int) g) => land.TryGetValue(g, out var h) ? $"heights {h[16, 16]:0}" : "no heights";
        string Named((int, int) g) => unreadable is null ? "no UnreadableLand member" : $"named {unreadable.Contains(g)}";
        bool IsNamed((int X, int Y) g) => unreadable is not null && unreadable.Contains(g);
        foreach (var g in landGrids)
        {
            switch (g)
            {
                case (0, 0):
                    Check($"{prefix} F-L1  an unreadable later LAND: no heights, the grid named", !land.ContainsKey(g) && IsNamed(g), $"{LandAt(g)}; {Named(g)}"); break;
                case (1, 0):
                    Check($"{prefix} F-L2  a later cell copy with no LAND: the master's heights",
                          land.TryGetValue(g, out var h10) && h10[16, 16] == MovedRefFixture.MasterHeight && !IsNamed(g), $"{LandAt(g)}; {Named(g)}"); break;
                case (5, 0):
                    Check($"{prefix} F-L3  a later readable LAND: its heights",
                          land.TryGetValue(g, out var h50) && h50[16, 16] == MovedRefFixture.BHeight50 && !IsNamed(g), $"{LandAt(g)}; {Named(g)}"); break;
                case (-1, 0):
                    Check($"{prefix} F-L4  a later LAND with no VHGT: no heights, named", !land.ContainsKey(g) && IsNamed(g), $"{LandAt(g)}; {Named(g)}"); break;
                case (2, 0):
                    Check($"{prefix} F-L5  a later cell copy carrying only a readable LAND: its heights",
                          land.TryGetValue(g, out var h20) && h20[16, 16] == MovedRefFixture.BHeight50 && !IsNamed(g), $"{LandAt(g)}; {Named(g)}"); break;
            }
        }
    }

}
