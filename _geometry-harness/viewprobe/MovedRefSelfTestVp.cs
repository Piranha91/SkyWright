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

        // ---- T1: the render's terrain walk, one cell copy's LAND at a time (TerrainLand.Step), over W in load order ----
        var terrain = new Dictionary<(int X, int Y), float>();
        var terrainLayers = new Dictionary<(int X, int Y), string>();
        var refusedT = new Dictionary<(int X, int Y), string>();
        foreach (var (_, mod, path) in t.Plugins())
            foreach (var ws in mod.Worldspaces.Records)
            {
                if (ws.FormKey != F.W) continue;
                foreach (var blk in ws.SubCells) foreach (var sub in blk.Items) foreach (var c in sub.Items)
                {
                    if (c.Grid?.Point is not { } gp) continue;
                    var g = (gp.X, gp.Y);
                    try
                    {
                        if (TerrainLand.Step(c.Landscape, Path.GetFileName(path), g, terrain, terrainLayers) is { } lc)
                        { terrain[g] = lc.Offset; terrainLayers[g] = Path.GetFileName(path); }
                    }
                    catch (TerrainUnreadable) { refusedT[g] = Path.GetFileName(path); }
                }
            }
        string TerrainAt((int, int) g) => (terrain.TryGetValue(g, out var o) ? $"offset {o:0} from {terrainLayers.GetValueOrDefault(g)}" : "no terrain")
                                          + (refusedT.TryGetValue(g, out var rp) ? $"; refused ({rp})" : "");
        t.Check("T1-declared a later LAND declaring no heights: the master's terrain removed from every map, nothing refused",
                !terrain.ContainsKey((1, 1)) && !terrainLayers.ContainsKey((1, 1)) && !refusedT.ContainsKey((1, 1)), TerrainAt((1, 1)));
        t.Check("T1-claimed a later LAND claiming heights with no VHGT is refused", refusedT.GetValueOrDefault((-1, 0)) == "MrA.esp", TerrainAt((-1, 0)));
        t.Check("T1-L1 a later LAND whose heightmap cannot be read is refused", refusedT.GetValueOrDefault((0, 0)) == "MrL.esp", TerrainAt((0, 0)));
        t.Check("T1-absent later cell copies with no LAND leave the master's terrain (control)",
                terrain.TryGetValue((1, 0), out var o10) && o10 == 0f && terrainLayers.GetValueOrDefault((1, 0)) == "MrMaster.esm", TerrainAt((1, 0)));
        t.Check("T1-readable a later readable LAND is taken (control)",
                terrain.TryGetValue((5, 0), out var o50) && o50 == 20f && terrainLayers.GetValueOrDefault((5, 0)) == "MrB.esp", TerrainAt((5, 0)));

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

        // ---- R3: the route mode's winning navmeshes of W, under the last-sighting rule (the fixture's NAVM table): NVM stands
        // in E50 (square 5,0); NVR is B's restoration; NVC A's override; the deleted (NVD, NVK, NVX) and the moved out of W
        // (NVI to an interior, NVW to W2) are gone ----
        t.CheckNavSet("R3  the route's winning navmeshes of W: moved out of W and deleted are gone; each at its last sighting",
                      walk.NavWin.Select(kv => (kv.Key, (FormKey?)null, kv.Value.Nav)),
                      new Dictionary<FormKey, (FormKey? Cell, float X)> { [F.NVM] = (null, 21000), [F.NVR] = (null, 1420), [F.NVC] = (null, 1510) });
        t.Check("R3-sq NVM's square is its last sighting's (5,0)", walk.NavWin.TryGetValue(F.NVM, out var nvm) && nvm.Square == (5, 0),
                walk.NavWin.TryGetValue(F.NVM, out var nvm2) ? $"{nvm2.Square}" : "absent");
        // The child-worldspace footprint, with W2 standing in as the child: NVW's last sighting is there (A's).
        var cwalk = new RouteStationMode.Walk(F.W, new[] { F.W2 }, F.P);
        cwalk.PreRead(t.Paths[1], 1);
        foreach (var path in t.Paths) cwalk.Plugin(path);
        t.CheckNavSet("R3-child the child worldspace's navmeshes: NVW, moved into it, A's version",
                      cwalk.Child.Select(kv => (kv.Key, (FormKey?)null, kv.Value)),
                      new Dictionary<FormKey, (FormKey? Cell, float X)> { [F.NVW] = (null, 1710) });

        // The child footprint's removals: W as the child of W2. W's navmeshes by their last sighting: NVW, moved out of W into
        // W2, is gone; NVD, NVK and NVX, deleted, are gone; NVI moved to an interior is gone.
        var cwalk2 = new RouteStationMode.Walk(F.W2, new[] { F.W }, F.P);
        cwalk2.PreRead(t.Paths[1], 1);
        foreach (var path in t.Paths) cwalk2.Plugin(path);
        t.CheckNavSet("R3-child2 the child worldspace's navmeshes: moved out of it and deleted are gone",
                      cwalk2.Child.Select(kv => (kv.Key, (FormKey?)null, kv.Value)),
                      new Dictionary<FormKey, (FormKey? Cell, float X)> { [F.NVM] = (null, 21000), [F.NVR] = (null, 1420), [F.NVC] = (null, 1510) });

        // ---- N1: the render's navmesh selection (NavmeshSelection.Observe), target E00 with E10 as a participant, every
        // cell of every plugin in load order as the render's walk hands them ----
        var sel = new NavmeshSelection(F.E00, new[] { F.E10 + "=cyan" }, Array.Empty<string>());
        var selWin = new Dictionary<FormKey, NavmeshGeometry>();
        var selVer = new Dictionary<string, Dictionary<FormKey, NavmeshGeometry>>(StringComparer.OrdinalIgnoreCase);
        var watched = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "MrA.esp" };
        foreach (var (_, mod, path) in t.Plugins())
            foreach (var cp in Cells(mod)) sel.Observe(cp, Path.GetFileName(path), watched, selWin, selVer);
        t.CheckNavSet("N1  the render's selected navmeshes: moved out and deleted are gone; each at its last sighting",
                      selWin.Select(kv => (kv.Key, (FormKey?)sel.Provenance[kv.Value].Cell, kv.Value)),
                      new Dictionary<FormKey, (FormKey? Cell, float X)> { [F.NVR] = (F.E00, 1420), [F.NVC] = (F.E00, 1510) });
        t.Check("N1-prov NVR's provenance is its last sighting's plugin (MrB.esp)",
                selWin.TryGetValue(F.NVR, out var snvr) && sel.Provenance[snvr].Plugin == "MrB.esp",
                selWin.TryGetValue(F.NVR, out var snvr2) ? sel.Provenance[snvr2].Plugin : "absent");
        t.Check("N1-ver a watched plugin's navmesh deleted with its data is no version to substitute (NVK in MrA.esp)",
                !(selVer.TryGetValue("MrA.esp", out var av) && av.ContainsKey(F.NVK)),
                selVer.TryGetValue("MrA.esp", out var av2) ? string.Join(",", av2.Keys.Select(MovedRefSelfTest.NavName).OrderBy(x => x)) : "none");

        // N1-sub: --nav-version may substitute an earlier plugin's version of a navmesh that stood in a selected cell and that
        // a later plugin deleted (the pair view's "as if this plugin won" asks the same of the same record): NVD's master
        // version (x 1100) is accepted. A navmesh never in a selected cell (NVX, in E11) is refused.
        string Sub(FormKey key)
        {
            var s2 = new NavmeshSelection(F.E00, Array.Empty<string>(), new[] { key + "=MrMaster.esm" });
            var w2 = new Dictionary<FormKey, NavmeshGeometry>();
            var v2 = new Dictionary<string, Dictionary<FormKey, NavmeshGeometry>>(StringComparer.OrdinalIgnoreCase);
            var watch2 = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "MrMaster.esm" };
            foreach (var (_, mod, path) in t.Plugins())
                foreach (var cp in Cells(mod)) s2.Observe(cp, Path.GetFileName(path), watch2, w2, v2);
            try { return s2.Substitute(w2, v2).TryGetValue(key, out var n) ? $"substituted x {n.Vertices[0].X:0}" : "absent"; }
            catch (ArgumentException e) { return "refused: " + e.Message; }
        }
        // The same through the retained walk's copy (CopyGraph), as a render that reuses a cached walk substitutes.
        string SubCopy(FormKey key)
        {
            var s2 = new NavmeshSelection(F.E00, Array.Empty<string>(), new[] { key + "=MrMaster.esm" });
            var w2 = new Dictionary<FormKey, NavmeshGeometry>();
            var v2 = new Dictionary<string, Dictionary<FormKey, NavmeshGeometry>>(StringComparer.OrdinalIgnoreCase);
            var watch2 = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "MrMaster.esm" };
            foreach (var (_, mod, path) in t.Plugins())
                foreach (var cp in Cells(mod)) s2.Observe(cp, Path.GetFileName(path), watch2, w2, v2);
            var g = s2.CopyGraph(w2, v2);
            try { return g.Selection.Substitute(g.Winners, g.Versions).TryGetValue(key, out var n) ? $"substituted x {n.Vertices[0].X:0}" : "absent"; }
            catch (ArgumentException e) { return "refused: " + e.Message; }
        }
        t.Check("N1-sub --nav-version of a navmesh deleted at the winner, from a plugin whose version stood in the selected cell: accepted",
                Sub(F.NVD) == "substituted x 1100", Sub(F.NVD));
        t.Check("N1-sub-out --nav-version of a navmesh never in a selected cell: refused", Sub(F.NVX).StartsWith("refused"), Sub(F.NVX));
        t.Check("N1-sub-copy and through the retained walk's copy: accepted", SubCopy(F.NVD) == "substituted x 1100", SubCopy(F.NVD));

        // ---- B1: --emit-bats' per-cell navmeshes (BatEmit.WalkCells), worklist cells E00 and E11 ----
        var ident = new Dictionary<int, BatEmit.CellIdent>();
        var bnav = new Dictionary<int, HarnessShared.LastSightingNavSet> { [0] = new(), [1] = new() };
        var wantedBat = new[] { F.E00.ToString(), F.E11.ToString() };
        foreach (var (_, mod, _) in t.Plugins()) BatEmit.WalkCells(mod, wantedBat, ident, bnav);
        t.CheckNavSet("B1  --emit-bats E00's navmeshes: moved out and deleted are gone; each at its last sighting",
                      bnav[0].Navs.Select(kv => (kv.Key, (FormKey?)null, kv.Value)),
                      new Dictionary<FormKey, (FormKey? Cell, float X)> { [F.NVR] = (null, 1420), [F.NVC] = (null, 1510) });
        t.CheckNavSet("B1-X --emit-bats E11's navmesh, deleted in a copy holding only the deletion, is gone",
                      bnav[1].Navs.Select(kv => (kv.Key, (FormKey?)null, kv.Value)), new Dictionary<FormKey, (FormKey? Cell, float X)>());
        t.Check("B1-ident the cells' identity comes from the master (control)", ident.TryGetValue(0, out var i0) && i0.Key == F.E00
                && ident.TryGetValue(1, out var i1) && i1.Key == F.E11, string.Join(",", ident.Select(kv => $"{kv.Key}:{kv.Value.Key}")));
    });
}
