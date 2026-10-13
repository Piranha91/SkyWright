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
                    Check($"{prefix} F-L4-claimed  a later LAND claiming heights with no VHGT: no heights, named", !land.ContainsKey(g) && IsNamed(g), $"{LandAt(g)}; {Named(g)}"); break;
                case (1, 1):
                    Check($"{prefix} F-L4-declared  a later LAND declaring no heights: the master's heights removed, not named",
                          !land.ContainsKey(g) && unreadable is not null && !IsNamed(g), $"{LandAt(g)}; {Named(g)}"); break;
                case (2, 0):
                    Check($"{prefix} F-L5  a later cell copy carrying only a readable LAND: its heights",
                          land.TryGetValue(g, out var h20) && h20[16, 16] == MovedRefFixture.BHeight50 && !IsNamed(g), $"{LandAt(g)}; {Named(g)}"); break;
            }
        }
    }

    /// <summary>The floor REACH (FloorFeed.FloorReach) on FloorReachFixture: <paramref name="walk"/> runs the probe's own
    /// floor walk over the fixture's plugins, in load order, and returns its FloorIndex. Each case of the fixture's table
    /// is its own line, so a sabotage arm names the case it must turn red (FR1..FR11, FRC).</summary>
    internal void CheckReach(string prefix, Func<IReadOnlyList<string>, FloorFeed.FloorIndex> walk, bool child = true)
    {
        var paths = FloorReachFixture.Write(Path.Combine(Root, "reach"));
        var index = walk(paths);
        var r00 = index.NeighbourhoodOf(FloorReachFixture.R, (0, 0)).Refs.Select(r => r.FormKey).ToList();
        var rc00 = index.NeighbourhoodOf(FloorReachFixture.RC, (0, 0)).Refs.Select(r => r.FormKey).ToList();
        int N(List<FormKey> l, FormKey k) => l.Count(x => x == k);
        string Show(List<FormKey> l) => string.Join(" ", l.Select(FloorReachFixture.Name).OrderBy(x => x, StringComparer.Ordinal));
        var F = (Func<string, FormKey>)(n => FloorReachFixture.Refs[n]);
        void Once(string id, string what) => Check($"{prefix}-{id}  {what}: present once", N(r00, F(what.Split(' ')[0])) == 1, Show(r00));
        void Absent(string id, string what) => Check($"{prefix}-{id}  {what}: absent", N(r00, F(what.Split(' ')[0])) == 0, Show(r00));
        Once("FR1", "F1 anchored at ring 2, box enters the 3x3");
        Absent("FR2", "F2 anchored at ring 2, box misses the 3x3");
        Once("FR3", "F3 listed, anchored at ring 4, enters");
        Absent("FR4", "F4 unlisted, anchored at ring 4, enters");
        Absent("FR5", "F5 listed, anchored at ring 6, enters");
        Absent("FR6", "F6 moved by a later plugin to ring 7");
        Absent("FR7", "F7 deleted by a later plugin, no placement data");
        Once("FR8", "F8 held at ring 2, standing inside the 3x3 (filed by position too)");
        Once("FR9", "F9 a box that enters only with its X rotation");
        Once("FR10", "F10 listed by a later plugin's list only (the union)");
        Once("FR11", "F11 held at ring 2, standing at ring 3 (anchored by its cell)");
        Check($"{prefix}-FR-all  R (0,0): exactly the expected set, each once",
              r00.Count == FloorReachFixture.ExpectedR00.Count && r00.ToHashSet().SetEquals(FloorReachFixture.ExpectedR00), Show(r00));
        if (child) Check($"{prefix}-FRC  child worldspace RC (0,0): G0 and its own list's G2, not the parent's G1",
              rc00.Count == FloorReachFixture.ExpectedRC00.Count && rc00.ToHashSet().SetEquals(FloorReachFixture.ExpectedRC00), Show(rc00));
    }
}
