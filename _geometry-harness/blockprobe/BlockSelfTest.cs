using HarnessShared;
using HousecarlCore;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Skyrim;
using F = HarnessShared.MovedRefFixture;

// blockprobe --moved-ref-selftest: the fixture's order walked by BlockWalk (the code step 2 of a real run calls), with the
// fixture's E00 and E10 as the target's cells. Expectations are worked out by hand from MovedRefFixture's table.
static class BlockSelfTest
{
    public static int Run() => MovedRefSelfTest.Run("blockprobe", t =>
    {
        var targets = new Dictionary<FormKey, CellPlacements>();
        using (var m0 = SkyrimMod.CreateFromBinaryOverlay(t.Paths[0], F.Rel))
            foreach (var cp in PlacementExtract.FromMod(m0))
                if (cp.Cell == F.E00 || cp.Cell == F.E10) targets[cp.Cell] = cp;
        t.Check("precondition: the target cells are E00 and E10", targets.Count == 2, string.Join(", ", targets.Keys));

        var walk = new BlockWalk(targets);
        var winNavTakenEarly = walk.WinNav;   // Program.cs takes the table before the walk and reads it after (B3-live)
        foreach (var (_, mod, path) in t.Plugins())
        {
            if (BlockWalk.Open(path) is not { } m) { t.Check($"open {Path.GetFileName(path)}", false, "not readable"); continue; }
            using (m as IDisposable) walk.Plugin(m, Path.GetFileName(path), _ => { });
        }

        // The winning state of the target cells: the last sighting of each reference, where it is admitted.
        var expect = new Dictionary<FormKey, (FormKey? Cell, float X, float Y, bool Deleted)>
        {
            [F.M6] = (F.E00, 1500, 1500, false), [F.C1] = (F.E00, 1200, 1100, false), [F.C2] = (F.E00, 210, 100, false),
            [F.P1] = (F.E00, 300, 300, false), [F.P3] = (F.E00, 320, 300, false), [F.NPC1] = (F.E00, 400, 400, false),
            [F.U3] = (F.E00, 600, 600, false),
            [F.M2] = (F.E10, 4200, 100, false), [F.D3] = (F.E10, 4400, 200, true), [F.B0] = (F.E10, 4500, 100, false),
        };
        t.CheckSet("B1  winning state of the target cells: moved out, deleted and moved elsewhere are gone; each at its last sighting",
                   walk.WinRef.Select(kv => (kv.Key, (FormKey?)walk.WinRefCell[kv.Key], kv.Value)), expect);
        string Where(FormKey k) => walk.WinRef.TryGetValue(k, out var r) ? $"at {r.Position.X:0},{r.Position.Y:0} cell {walk.WinRefCell[k]}" : "absent";
        t.Check("B1-M1 moved to a far cell: not judged in the old one", !walk.WinRef.ContainsKey(F.M1), Where(F.M1));
        t.Check("B1-M5 made persistent (another cell): not judged in the old one", !walk.WinRef.ContainsKey(F.M5), Where(F.M5));
        t.Check("B1-D1 deleted with no placement data: not judged", !walk.WinRef.ContainsKey(F.D1), Where(F.D1));
        t.Check("B1-D2 deleted in a copy holding only the deletion: not judged", !walk.WinRef.ContainsKey(F.D2), Where(F.D2));
        t.Check("B1-M2 moved between two target cells: in E10 only", walk.WinRefCell.TryGetValue(F.M2, out var c2) && c2 == F.E10, Where(F.M2));
        t.Check("C2 control: untouched, in E00 at its place" + (t.Canary ? " [CANARY: expectation planted wrong]" : ""),
                Where(F.C2) == (t.Canary ? "at 999,999 cell " + F.E00 : "at 210,100 cell " + F.E00), Where(F.C2));

        // The target cells' navmeshes, under the same rule (the NAVM table): moved to E50, an interior or W2, and deleted with
        // or without data, are gone; NVR is B's restoration; NVC is A's override.
        t.CheckNavSet("B3  navmeshes of the target cells: moved out and deleted are gone; each at its last sighting",
                      walk.WinNav.Select(kv => (kv.Key, (FormKey?)kv.Value.Cell, kv.Value.Nav)),
                      new Dictionary<FormKey, (FormKey? Cell, float X)> { [F.NVR] = (F.E00, 1420), [F.NVC] = (F.E00, 1510) });
        t.CheckNavSet("B3-live the table taken before the walk, as Program.cs takes it, holds the same navmeshes after it",
                      winNavTakenEarly.Select(kv => (kv.Key, (FormKey?)kv.Value.Cell, kv.Value.Nav)),
                      new Dictionary<FormKey, (FormKey? Cell, float X)> { [F.NVR] = (F.E00, 1420), [F.NVC] = (F.E00, 1510) });
        t.Check("B3-prov NVR's provider is the plugin of its last sighting (MrB.esp)",
                walk.WinNav.TryGetValue(F.NVR, out var nvr) && nvr.Plugin == "MrB.esp", walk.WinNav.TryGetValue(F.NVR, out var nvr2) ? nvr2.Plugin : "absent");

        // The floor under the target cells (their 3x3 neighbourhoods), and the LAND of the grids inside them.
        t.CheckFloor("B2", walk.FloorRefsByWorld.GetValueOrDefault(F.W), walk.FloorLandByWorld.GetValueOrDefault(F.W),
                     MovedRefSelfTest.UnreadableLandOf(walk, F.W), new[] { (0, 0), (1, 0), (-1, 0), (2, 0), (1, 1) });

        // ---- BN: the navcut boxes measured against target E10's navmesh (NavcutReach.ForCell), by where they stand. Synthetic
        // references (an XPRM box of half extent h, layer 49 = L_NAVCUT unless stated), filed as the floor filing files them:
        // by the grid their position is in. E10 is grid (1,0), x 4096..8192, y 0..4096. ----
        static PlacedRef Box(uint id, float x, float y, float h, uint layer = 49, bool persistent = false) =>
            new(new FormKey(F.MasterKey, id), "PlacedObject", null, new Vec3(x, y, 0), new Vec3(0, 0, 0), 1f, F.S, persistent, false, false,
                Primitive: new PlacedPrimitiveInfo("Box", new Vec3(2 * h, 2 * h, 2 * h)), CollisionLayerIndex: layer);
        var e10 = new CellPlacements(F.E10, null, false, F.W, (1, 0), Array.Empty<PlacedRef>(), Array.Empty<NavmeshGeometry>());
        var nHeld = Box(0xA01, 5000, 100, 100);                    // held in E10, standing in it
        var nPersist = Box(0xA02, 6000, 300, 100, persistent: true); // held in the persistent cell, standing in grid (1,0)
        var nAcross = Box(0xA03, 4000, 200, 300);                  // standing in grid (0,0); its box spans x 3700..4300
        var nInside = Box(0xA04, 2000, 200, 100);                  // standing in grid (0,0), wholly inside it
        var nOther = Box(0xA05, 5500, 500, 100, layer: 7);         // in grid (1,0), not a navcut layer
        var nGrids = new Dictionary<(int, int), Dictionary<FormKey, PlacedRef>>
        {
            [(1, 0)] = new[] { nHeld, nPersist, nOther }.ToDictionary(r => r.FormKey),
            [(0, 0)] = new[] { nAcross, nInside }.ToDictionary(r => r.FormKey),
        };
        var nPicked = NavcutReach.ForCell(e10, new[] { nHeld }, nGrids, r => r.CollisionLayerIndex == 49);
        string NShow() => string.Join(", ", nPicked.Select(r => $"{r.FormKey.ID:X3}"));
        t.Check("BN-1 a navcut box held in the persistent cell and standing in E10's square is measured for E10",
                nPicked.Any(r => r.FormKey == nPersist.FormKey), NShow());
        t.Check("BN-2 a navcut box held in a neighbour whose box reaches across the border into E10 is measured for E10",
                nPicked.Any(r => r.FormKey == nAcross.FormKey), NShow());
        t.Check("BN-3 control: a navcut box held in E10 is measured, once", nPicked.Count(r => r.FormKey == nHeld.FormKey) == 1, NShow());
        t.Check("BN-4 control: a box on another collision layer is never measured", nPicked.All(r => r.FormKey != nOther.FormKey), NShow());
        t.Check("BN-5 control: a navcut box wholly inside the neighbour's square is not measured for E10",
                nPicked.All(r => r.FormKey != nInside.FormKey), NShow());

        // The floor reach (FloorFeed.FloorReach) on its own fixture, through BlockWalk with the centre cells as targets.
        t.CheckReach("B-R", paths =>
        {
            var tg = new Dictionary<FormKey, CellPlacements>();
            using (var r0 = SkyrimMod.CreateFromBinaryOverlay(paths[0], FloorReachFixture.Rel))
                foreach (var cp in PlacementExtract.FromMod(r0))
                    if (cp.Cell == FloorReachFixture.R00 || cp.Cell == FloorReachFixture.RC00) tg[cp.Cell] = cp;
            t.Check("B-R precondition: the reach fixture's target cells are R00 and RC00", tg.Count == 2, string.Join(", ", tg.Keys));
            var w = new BlockWalk(tg);
            foreach (var p in paths)
                if (BlockWalk.Open(p) is { } m) { using (m as IDisposable) w.Plugin(m, Path.GetFileName(p), _ => { }); }
            return w.Floor;
        });

        // A plugin that cannot be read is refused, not skipped (P1).
        t.CheckRefusesUnreadable("P1 ", p => BlockWalk.Open(p));
        t.CheckRefusesMidWalk("P1-mid", p => { var m = BlockWalk.Open(p)!; using (m as IDisposable) new BlockWalk(targets).Plugin(m, Path.GetFileName(p), _ => { }); });
    });
}
