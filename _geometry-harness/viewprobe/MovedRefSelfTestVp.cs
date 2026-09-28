using HarnessShared;
using HousecarlCore;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Skyrim;
using F = HarnessShared.MovedRefFixture;

namespace ViewProbe;

// viewprobe --moved-ref-selftest: the fixture's order through the side tables the render's walk fills beside the winning
// state (WalkSideTables.cs: the --stations floor filing and the "where is it" tables) and through the route mode's
// worldspace pass (RouteStationMode.Walk). The winning state itself is --winning-refs-selftest's. Expectations are worked
// out by hand from MovedRefFixture's table.
internal static class MovedRefSelfTestVp
{
    // The walk's enumeration, as Program.cs's winning-state walk asks it.
    static IEnumerable<CellPlacements> Cells(ISkyrimModGetter m) => HarnessShared.LastSighting.Cells(m);

    // ---- the adapter for the --stations floor filing: the only part of this file that knows how it is called ----
    sealed class StationFloor
    {
        readonly FloorFeed.FloorIndex? _index = StationFloorFiling.Begin(true, F.W, (0, 0), 4);
        public readonly Dictionary<(int, int), Dictionary<FormKey, PlacedRef>> Refs = new();
        public readonly Dictionary<(int, int), float[,]> Land = new();
        readonly Dictionary<(int, int), string> _unreadable = new();
        public void Observe(CellPlacements cp) => _index!.Observe(cp);
        public void Finish() => StationFloorFiling.Finish(_index, F.W, Refs, Land, _unreadable);
        public ISet<(int X, int Y)>? Unreadable => _unreadable.Keys.ToHashSet();
    }

    internal static int Run() => MovedRefSelfTest.Run("viewprobe", t =>
    {
        // ---- V1: the --stations floor, target E00 (grid 0,0) in W, radius 4 ----
        var sf = new StationFloor();
        var tables = new RefWhere.Tables(new(), new(), new(), new());
        var watch = new HashSet<FormKey> { F.N1, F.N2 };
        foreach (var (_, mod, path) in t.Plugins())
            foreach (var cp in Cells(mod))
            {
                sf.Observe(cp);
                RefWhere.Observe(cp, Path.GetFileName(path), tables, watch, new HashSet<FormKey>(), mod.ModKey);
            }
        sf.Finish();
        t.CheckFloor("V1", sf.Refs, sf.Land, sf.Unreadable, new[] { (0, 0), (1, 0), (-1, 0), (2, 0), (1, 1) });
        // The station reader refuses a floor whose neighbourhood holds an unreadable LAND (grid 0,0 by L1), and builds one
        // whose neighbourhood does not (grid 5,0: B's readable LAND).
        var unreadable = (sf.Unreadable ?? new HashSet<(int X, int Y)>()).ToDictionary(g => (g.X, g.Y), g => "fixture");
        var reader = new StationMode.EnvironmentReader(_ => Array.Empty<NavmeshGeometry>(), sf.Refs, sf.Land, _ => null, _ => true,
                                                       _ => null, _ => null, F.W.ToString(), unreadable);
        string Try((int, int) g) { try { reader.FieldFor(g); return "built"; } catch (ReadRefusal ex) { return "refused: " + ex.GetType().Name; } }
        t.Check("V1-refuse the station reader refuses a floor over an unreadable LAND (grid 0,0)", Try((0, 0)).StartsWith("refused"), Try((0, 0)));
        t.Check("V1-build and builds one whose neighbourhood's LAND is sound (grid 5,0; control)", Try((5, 0)) == "built", Try((5, 0)));
        t.Check("V1-declared and builds one whose only heightless grid DECLARES no heights (grid 2,1, over the declared 1,1: no terrain there, no refusal)",
                Try((2, 1)) == "built", Try((2, 1)));

        // ---- V2: NPC references by base ----
        string Under(FormKey b) => tables.NpcBase.TryGetValue(b, out var per) ? string.Join(" ", per.Keys.Select(MovedRefSelfTest.Name).OrderBy(x => x)) : "";
        t.Check("V2  an NPC reference re-based N1 -> N2 is listed under N2 only", Under(F.N1) == "" && Under(F.N2) == "NPC1", $"N1 [{Under(F.N1)}] N2 [{Under(F.N2)}]");
        t.Check("V2-NPC4 a watched NPC reference deleted WITH its data is in no table and is noted as deleted",
                !tables.NpcBase.Values.Any(p => p.ContainsKey(F.NPC4)) && tables.DeletedBy.ContainsKey(F.NPC4),
                (tables.NpcBase.Values.Any(p => p.ContainsKey(F.NPC4)) ? "listed under a base" : "in no table") + "; "
                + (tables.DeletedBy.TryGetValue(F.NPC4, out var db4) ? $"deleted by {db4}" : "not noted as deleted"));
        t.Check("V2-NPC2 an NPC reference deleted with no data is in no table (control)", !tables.NpcBase.Values.Any(p => p.ContainsKey(F.NPC2)) && tables.DeletedBy.ContainsKey(F.NPC2),
                tables.DeletedBy.TryGetValue(F.NPC2, out var db) ? $"deleted by {db}" : "not noted as deleted");
        t.Check("C2 control: V2's NPC1 is in E00" + (t.Canary ? " [CANARY: expectation planted wrong]" : ""),
                tables.NpcBase.TryGetValue(F.N2, out var n2) && n2.TryGetValue(F.NPC1, out var w1) && (t.Canary ? w1.Cell == F.E10 : w1.Cell == F.E00),
                tables.NpcBase.TryGetValue(F.N2, out var n2b) && n2b.TryGetValue(F.NPC1, out var w2) ? $"{w2.Cell}" : "absent");

        // ---- R1, R2: the route mode's worldspace pass, W with no child worldspaces, the plane base P, A as the changed plugin ----
        var walk = new RouteStationMode.Walk(F.W, Array.Empty<FormKey>(), F.P);
        walk.PreRead(t.Paths[1], 1);
        t.Check("R precondition: the bound covers grids -1..2 and (1,1) around the city's content", new[] { (-1, 0), (0, 0), (1, 0), (2, 0), (1, 1) }.All(g => walk.Bound.Contains(g)),
                $"{walk.Bound.Count} squares");
        foreach (var path in t.Paths) walk.Plugin(path);
        t.CheckFloor("R1", walk.RefsByGrid, walk.LandByGrid, MovedRefSelfTest.UnreadableLandOf(walk, F.W), new[] { (0, 0), (1, 0), (-1, 0), (2, 0), (1, 1) });
        var prims = walk.Prims.Where(p => p.Value is not null).Select(p => MovedRefSelfTest.Name(p.Key)).OrderBy(x => x).ToList();
        t.Check("R2  the planes: P3 only (P1 re-based to a static, P2 moved to another worldspace)",
                prims.SequenceEqual(new[] { "P3" }), string.Join(", ", prims));
    });
}
