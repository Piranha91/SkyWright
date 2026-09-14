using HarnessShared;
using HousecarlCore;
using Mutagen.Bethesda;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Skyrim;
using Noggog;

// PROOF BY CONSTRUCTION — world-state switches (roadmap 4.11): the census (_shared/WorldState.cs, SwitchIndex), the
// state arm (StateArm), the navcut box (NavcutGeometry), the state-differential decision and its population split
// (DetectorModel), and the class evaluator (_shared/StateClass.cs) over a synthetic two-plugin order written with
// Mutagen and read back through OverlapIndex with OBND boxes for geometry. Cases follow the handoff's list: a marker
// with an on-set and an off-set; chained parents with parity; the player-parent permanently-disabled idiom (never a
// switch); a child in another cell; a navcut box in an on-set (by the COLL record's index, the way XTRI stores it);
// a mod hanging its ref on a vanilla marker and a mod ignoring one; a dropped XESP edge; a cycle; then the arm flipped
// and each row shape: a StateConditional overlap, a StateResolved one, a same-set pair, a host leaving (Stranded), a
// host arriving (Sunk), a dependent arriving touching / floating / stranded-on-arrival, a navcut arriving (route),
// a navcut departing.
static class StateProof
{
    public static void Run(Action<string, bool, string> Check)
    {
        Console.WriteLine("\nPROOF — world-state switches (_shared/WorldState.cs, _shared/StateClass.cs, DetectorModel.StateDifferential)");
        Console.WriteLine("   A synthetic order written with Mutagen; every set, state and row known by construction.\n");

        // ---- the decision, at fact level ----
        Check("state/rule: present in the state and absent at load time is StateConditional; the reverse StateResolved; both Unchanged; neither Absent",
              DetectorModel.StateDifferential(false, true).Verdict == StateVerdict.StateConditional
           && DetectorModel.StateDifferential(true, false).Verdict == StateVerdict.StateResolved
           && DetectorModel.StateDifferential(true, true).Verdict == StateVerdict.Unchanged
           && DetectorModel.StateDifferential(false, false).Verdict == StateVerdict.Absent, "four cells");
        Check("state/population: vanilla member vs vanilla other is the floor; USSEP on either side its own row; vanilla member vs mod other = a mod's ref under a vanilla set; mod member vs vanilla other = a mod's set over vanilla; mod vs mod",
              DetectorModel.ClassifyStatePopulation(true, true, false, false) == StatePopulation.BethesdaFloor
           && DetectorModel.ClassifyStatePopulation(true, false, false, true) == StatePopulation.UssepInvolved
           && DetectorModel.ClassifyStatePopulation(false, true, true, false) == StatePopulation.UssepInvolved
           && DetectorModel.ClassifyStatePopulation(true, false, false, false) == StatePopulation.ModRefUnderVanillaSet
           && DetectorModel.ClassifyStatePopulation(false, true, false, false) == StatePopulation.ModSetOverVanilla
           && DetectorModel.ClassifyStatePopulation(false, false, false, false) == StatePopulation.ModOnMod, "five outcomes");

        // ---- the navcut box ----
        {
            var prim = new PlacedPrimitiveInfo("Box", new Vec3(100, 200, 50));
            var box = NavcutGeometry.ModelBox(prim);
            var soup = NavcutGeometry.Soup(prim);
            var w = NavcutGeometry.WorldVertices(soup, new Vec3(1000, 0, 0), new Vec3(0, 0, MathF.PI / 2f), 1f);
            float minX = w.Min(v => v.X), maxX = w.Max(v => v.X), minY = w.Min(v => v.Y), maxY = w.Max(v => v.Y);
            Check("navcut/box: the XPRM bounds are FULL extents about the reference — ±50, ±100, ±25 — closed as twelve triangles",
                  box.Min.X == -50 && box.Max.X == 50 && box.Min.Y == -100 && box.Max.Y == 100 && box.Min.Z == -25 && box.Max.Z == 25 && soup.TriangleCount == 12,
                  $"{box.Min} {box.Max} {soup.TriangleCount} tris");
            Check("navcut/box: placed with the reference's transform — a 90° yaw swaps the x and y extents about the position",
                  MathF.Abs(maxX - minX - 200f) < 0.01f && MathF.Abs(maxY - minY - 100f) < 0.01f && MathF.Abs((minX + maxX) / 2 - 1000f) < 0.01f,
                  $"x {minX:0}..{maxX:0} y {minY:0}..{maxY:0}");
        }

        var root = Path.Combine(Path.GetTempPath(), "proofprobe-st-" + Environment.ProcessId);
        Directory.CreateDirectory(root);
        try { RunOrder(Check, root); }
        finally { try { Directory.Delete(root, true); } catch { } }
    }

    static void FileInterior(SkyrimMod mod, Cell cell)
    {
        uint id = cell.FormKey.ID;
        int blockN = (int)(id % 10), subN = (int)((id / 10) % 10);
        var records = mod.Cells.Records;
        var block = records.FirstOrDefault(b => b.BlockNumber == blockN);
        if (block is null) { block = new CellBlock { BlockNumber = blockN, GroupType = GroupTypeEnum.InteriorCellBlock }; records.Add(block); }
        var sub = block.SubBlocks.FirstOrDefault(s => s.BlockNumber == subN);
        if (sub is null) { sub = new CellSubBlock { BlockNumber = subN, GroupType = GroupTypeEnum.InteriorCellSubBlock }; block.SubBlocks.Add(sub); }
        sub.Cells.Add(cell);
    }
    static PlacedObject Ref(FormKey key, FormKey baseKey, float x, float y = 0, float z = 0, string? edid = null, bool disabled = false,
                            FormKey? parent = null, bool opposite = false, uint? layer = null, P3Float? prim = null)
    {
        var r = new PlacedObject(key, SkyrimRelease.SkyrimSE) { EditorID = edid, Placement = new Placement { Position = new P3Float(x, y, z), Rotation = new P3Float(0, 0, 0) } };
        r.Base.SetTo(baseKey);
        if (disabled) r.SkyrimMajorRecordFlags |= SkyrimMajorRecord.SkyrimMajorRecordFlag.InitiallyDisabled;
        if (parent is { } pk)
        {
            r.EnableParent = new EnableParent { Flags = opposite ? EnableParent.Flag.SetEnableStateToOppositeOfParent : 0 };
            r.EnableParent.Reference.SetTo(pk);
        }
        if (layer is { } l) r.CollisionLayer = l;
        if (prim is { } p) r.Primitive = new PlacedPrimitive { Type = PlacedPrimitive.TypeEnum.Box, Bounds = p };
        return r;
    }
    static Cell Interior(FormKey key, string? edid = null) => new(key, SkyrimRelease.SkyrimSE) { EditorID = edid, Flags = Cell.Flag.IsInteriorCell };
    static Static Stat(FormKey k, string edid, int x, int y, int z, bool marker = false) => new(k, SkyrimRelease.SkyrimSE)
        { EditorID = edid, Model = new Model { File = $"meshes\\st\\{edid}.nif" }, ObjectBounds = new ObjectBounds { First = new P3Int16((short)-x, (short)-y, 0), Second = new P3Int16((short)x, (short)y, (short)z) },
          MajorRecordFlagsRaw = marker ? (int)EnableState.IsMarkerFlag : 0 };

    static void RunOrder(Action<string, bool, string> Check, string root)
    {
        var release = SkyrimRelease.SkyrimSE;
        var mKey = new ModKey("StMaster", ModType.Master);
        var pKey = new ModKey("StP", ModType.Plugin);
        FormKey M(uint id) => new(mKey, id);
        FormKey P(uint id) => new(pKey, id);
        var markerK = M(0x801); var tableK = M(0x802); var bowlK = M(0x803); var collK = M(0x804); var treeK = M(0x805); var tree2K = M(0x806); var navcutLayerK = M(0x810); var triggerLayerK = M(0x811);
        var cell1K = M(0x820); var cell2K = M(0x821);
        FormKey S(int n) => M((uint)(0x900 + n));      // switches
        FormKey R(int n) => M((uint)(0xA00 + n));      // members and neighbours
        const float TableTop = 32f, BowlHalf = 5f;

        var master = new SkyrimMod(mKey, release);
        master.Statics.Add(Stat(markerK, "StMarker", 2, 2, 4, marker: true));
        master.Statics.Add(Stat(tableK, "StTable", 100, 100, 64));                // ±100, ±100, ±32 about the placement
        master.Statics.Add(Stat(bowlK, "StBowl", 12, 12, 10));                    // ±12, ±12, ±5
        master.Statics.Add(Stat(collK, "CollisionMarker", 2, 2, 4, marker: true));
        // A tree: obstructing (the overlap pass's set), neither a host nor — at 300 u — a dependent, so an overlap case reads
        // as overlap alone.
        master.Trees.Add(new Tree(treeK, release) { EditorID = "StTree", Model = new Model { File = "meshes\\st\\StTree.nif" }, ObjectBounds = new ObjectBounds { First = new P3Int16(-100, -100, 0), Second = new P3Int16(100, 100, 300) } });
        master.Trees.Add(new Tree(tree2K, release) { EditorID = "StTree2", Model = new Model { File = "meshes\\st\\StTree2.nif" }, ObjectBounds = new ObjectBounds { First = new P3Int16(-100, -100, 0), Second = new P3Int16(100, 100, 300) } });
        master.CollisionLayers.Add(new CollisionLayer(navcutLayerK, release) { EditorID = "L_NAVCUT", Name = "L_NAVCUT", Index = 49, Flags = CollisionLayer.Flag.NavmeshObstacle, Description = "cuts navmesh" });
        master.CollisionLayers.Add(new CollisionLayer(triggerLayerK, release) { EditorID = "L_TRIGGER", Name = "L_TRIGGER", Index = 12, Flags = CollisionLayer.Flag.TriggerVolume, Description = "a trigger" });
        var c1 = Interior(cell1K, "StCell1"); var c2 = Interior(cell2K, "StCell2");

        // M1 (enabled): on-set A1, off-set B1, A2 in the other cell; M3 chained under it (its child D1 at depth 2); Q1 from plugin P.
        c1.Temporary.Add(Ref(S(1), markerK, 1000, 0, 0, "M1"));
        c1.Temporary.Add(Ref(R(1), tableK, 1000, 0, 0, "A1", parent: S(1)));
        c1.Temporary.Add(Ref(R(2), tableK, 1000, 600, 0, "B1", parent: S(1), opposite: true));
        c2.Temporary.Add(Ref(R(3), bowlK, 1000, 0, TableTop + BowlHalf, "A2", parent: S(1)));
        c1.Temporary.Add(Ref(S(3), markerK, 3000, 0, 0, "M3", parent: S(1)));
        c1.Temporary.Add(Ref(R(4), tableK, 3000, 0, 0, "D1", parent: S(3)));
        c1.Temporary.Add(Ref(S(13), markerK, 3500, 0, 0, "M3b", parent: S(1), opposite: true));       // chained the OTHER way
        c1.Temporary.Add(Ref(R(32), tableK, 3500, 0, 0, "D2", parent: S(13)));                        // parity under M1: off-set
        c1.Temporary.Add(Ref(R(5), tableK, 1050, -500, 0, "K1"));                                       // P's Q1 will stand in it
        // M2 (disabled): on-set W2 overlapping K2 (enabled) — StateConditional overlap; W2s beside W2 — the same-set pair;
        // N1 a navcut box; W2b, whose edge P will drop.
        c1.Temporary.Add(Ref(S(2), markerK, 2000, 0, 0, "M2", disabled: true));
        c1.Temporary.Add(Ref(R(6), treeK, 2000, 0, 0, "W2", parent: S(2)));
        c1.Temporary.Add(Ref(R(7), treeK, 2050, 0, 0, "K2"));
        c1.Temporary.Add(Ref(R(8), treeK, 1880, 0, 0, "W2s", parent: S(2)));                            // 80 u into W2, 30 u into K2 (under the fraction)
        c1.Temporary.Add(Ref(R(9), collK, 2000, 500, 50, "N1", parent: S(2), layer: 49, prim: new P3Float(200, 200, 100)));
        c1.Temporary.Add(Ref(R(10), tableK, 2300, 0, 0, "W2b", parent: S(2)));
        // M4 (enabled): on-set W4 overlapping K4 — StateResolved when flipped.
        c1.Temporary.Add(Ref(S(4), markerK, 4000, 0, 0, "M4"));
        c1.Temporary.Add(Ref(R(11), treeK, 4000, 0, 0, "W4", parent: S(4)));
        c1.Temporary.Add(Ref(R(12), treeK, 4050, 0, 0, "K4"));
        // M5 (enabled): on-set T5, a host; B5 rests on it (Stranded when T5 leaves); B5c rests on it AND on T5r (ReSupported).
        c1.Temporary.Add(Ref(S(5), markerK, 5000, 0, 0, "M5"));
        c1.Temporary.Add(Ref(R(13), tableK, 5000, 0, 0, "T5", parent: S(5)));
        c1.Temporary.Add(Ref(R(14), bowlK, 5000, 0, TableTop + BowlHalf, "B5"));
        c1.Temporary.Add(Ref(R(15), bowlK, 5100, 0, TableTop + BowlHalf, "B5c"));                        // on T5's edge, 2 u onto T5r
        c1.Temporary.Add(Ref(R(16), tableK, 5212, 0, 0, "T5r"));                                          // 12 u clear of T5's box
        // M6 (disabled): on-set T6 arrives under B6 (sunk 17 u) and B6t (touching).
        c1.Temporary.Add(Ref(S(6), markerK, 6000, 0, 0, "M6", disabled: true));
        c1.Temporary.Add(Ref(R(17), tableK, 6000, 0, 0, "T6", parent: S(6)));
        c1.Temporary.Add(Ref(R(18), bowlK, 6000, 0, 20, "B6"));
        c1.Temporary.Add(Ref(R(19), bowlK, 6000, 0, TableTop + BowlHalf, "B6t"));
        // M7 (disabled): on-set bowls arrive — B7 onto T7 (touching), B7f into nothing (floating), B7s over T7m which P moves (stranded on arrival).
        c1.Temporary.Add(Ref(S(7), markerK, 7000, 0, 0, "M7", disabled: true));
        c1.Temporary.Add(Ref(R(20), bowlK, 7000, 0, TableTop + BowlHalf, "B7", parent: S(7)));
        c1.Temporary.Add(Ref(R(21), tableK, 7000, 0, 0, "T7"));
        c1.Temporary.Add(Ref(R(22), bowlK, 7500, 0, TableTop + BowlHalf, "B7f", parent: S(7)));
        c1.Temporary.Add(Ref(R(23), bowlK, 7000, 300, TableTop + BowlHalf, "B7s", parent: S(7)));
        c1.Temporary.Add(Ref(R(24), tableK, 7000, 300, 0, "T7m"));
        // M8 (disabled): on-set N8 navcut + S8 a plain table; M9 (enabled): on-set N9 navcut.
        c1.Temporary.Add(Ref(S(8), markerK, 8000, 0, 0, "M8", disabled: true));
        c1.Temporary.Add(Ref(R(25), collK, 8000, 0, 25, "N8", parent: S(8), layer: 49, prim: new P3Float(100, 100, 50)));
        c1.Temporary.Add(Ref(R(26), tableK, 8000, 500, 0, "S8", parent: S(8)));
        c1.Temporary.Add(Ref(S(9), markerK, 8500, 0, 0, "M9"));
        c1.Temporary.Add(Ref(R(27), collK, 8500, 0, 25, "N9", parent: S(9), layer: 49, prim: new P3Float(100, 100, 50)));
        // M11 (disabled): on-set W11 overlapping K11 — a vanilla reference whose BASE plugin P swaps (the re-based shell shape).
        c1.Temporary.Add(Ref(S(14), markerK, 11000, 0, 0, "M11", disabled: true));
        c1.Temporary.Add(Ref(R(33), treeK, 11000, 0, 0, "W11", parent: S(14)));
        c1.Temporary.Add(Ref(R(34), treeK, 11050, 0, 0, "K11"));
        // Not switches: the player-parent idiom, an unplaced parent, a disabled singleton, a trigger primitive on another layer.
        c1.Temporary.Add(Ref(R(28), tableK, 9000, 0, 0, "E1", parent: new FormKey(mKey, 0x14), opposite: true));
        c1.Temporary.Add(Ref(R(29), tableK, 9100, 0, 0, "F1", parent: M(0xFFF)));
        c1.Temporary.Add(Ref(R(30), tableK, 9200, 0, 0, "G1", disabled: true));
        c1.Temporary.Add(Ref(R(31), collK, 9400, 0, 0, "T9", layer: 12, prim: new P3Float(50, 50, 50)));
        // A cycle.
        c1.Temporary.Add(Ref(S(11), markerK, 9300, 0, 0, "X1", parent: S(12)));
        c1.Temporary.Add(Ref(S(12), markerK, 9350, 0, 0, "X2", parent: S(11)));
        FileInterior(master, c1); FileInterior(master, c2);
        var mPath = Path.Combine(root, mKey.FileName.String);
        master.BeginWrite.ToPath(mPath).WithNoLoadOrder().Write();
        using var mOv = SkyrimMod.CreateFromBinaryOverlay(mPath, release);

        // PLUGIN P (masters M): hangs Q1 on M1 (over K1), places R1 with no parent (ignores M1), adds its own switch M10 with Q2,
        // drops W2b's edge, moves T7m 200 u away.
        var plug = new SkyrimMod(pKey, release);
        var p1 = Interior(cell1K);
        p1.Temporary.Add(Ref(P(0xB01), tableK, 1000, -500, 0, "Q1", parent: S(1)));
        p1.Temporary.Add(Ref(P(0xB02), tableK, 1000, -800, 0, "R1"));
        p1.Temporary.Add(Ref(P(0xB03), markerK, 10000, 0, 0, "M10"));
        p1.Temporary.Add(Ref(P(0xB04), tableK, 10000, 0, 0, "Q2", parent: P(0xB03)));
        p1.Temporary.Add(Ref(R(10), tableK, 2300, 0, 0, "W2b"));                                          // the edge dropped
        p1.Temporary.Add(Ref(R(24), tableK, 7200, 300, 0, "T7m"));                                         // moved 200 u
        p1.Temporary.Add(Ref(R(34), tree2K, 11050, 0, 0, "K11"));                                          // re-based, not moved: P's geometry
        FileInterior(plug, p1);
        var pPath = Path.Combine(root, pKey.FileName.String);
        plug.BeginWrite.ToPath(pPath).WithLoadOrder(mOv).Write();

        // Read back through the same index the probes use; OBND boxes as the geometry.
        ContactIndex? cidx = null;
        cidx = new ContactIndex(k => cidx!.Index.ObndBox(k) is { IsEmpty: false } bx
            ? new RefGeometry(bx, ContactIndex.BoxSoup(bx), ContactIndex.BoxSoup(bx), 'o', 'o', HostUsable: true, Array.Empty<(string, string)>(), false) : null);
        var paths = new[] { mPath, pPath };
        for (int p = 0; p < paths.Length; p++)
        {
            using var mod = SkyrimMod.CreateFromBinaryOverlay(paths[p], release);
            cidx.Index.Ingest(p, mod);
        }
        var idx = cidx.Index;
        var sx = SwitchIndex.Build(idx);
        sx.NameSwitches(idx.RefEditorId);
        SwitchInfo Sw(string e) => sx.Find(e) ?? throw new Exception($"switch {e} missing");

        // ---- the census ----
        Check("census: fourteen switches — the nine markers, the chained M3 and M3b, P's M10, and the cycle's X1 and X2; the player, the unplaced parent and G1 are none",
              sx.Switches.Count == 14 && !sx.Switches.ContainsKey(R(30)) && sx.PlayerParentRefs == 1 && sx.UnplacedParentRefs == 1 && sx.DisabledSingletons == 1,
              $"{sx.Switches.Count} switches; player {sx.PlayerParentRefs} unplaced {sx.UnplacedParentRefs} singletons {sx.DisabledSingletons}");
        var m1 = Sw("M1");
        Check("census: M1's direct children are A1, B1, A2, M3, M3b and P's Q1; its descendants add D1 (depth 2, on-set parity) and D2 (depth 2 under the opposite-chained M3b: OFF-set parity though its own flag is clear); B1, M3b itself and D2 are the off-set",
              m1.Children.Count == 6 && m1.Descendants.Count == 8 && m1.OffCount == 3 && m1.OnCount == 5
           && m1.Descendants.Any(d => d.Key == R(4) && d.Depth == 2 && !d.Parity) && m1.Descendants.Any(d => d.Key == R(2) && d.Parity && d.Depth == 1)
           && m1.Descendants.Any(d => d.Key == R(32) && d.Depth == 2 && d.Parity && !d.Opposite),
              $"children {m1.Children.Count} descendants {m1.Descendants.Count} on {m1.OnCount} off {m1.OffCount}");
        Check("census: A2 is a member in the OTHER cell; M3 is chained (its parent is M1); the cycle is counted and both its switches flagged",
              m1.Descendants.First(d => d.Key == R(3)).Cell == cell2K && m1.Cells.Count() == 2 && Sw("M3").Parent == S(1)
           && sx.ChainCycles >= 2 && Sw("X1").ChainCycle && Sw("X2").ChainCycle, $"cells {m1.Cells.Count()} cycles {sx.ChainCycles}");
        Check("census: owner, placer and attacher — M1 is the master's; Q1 was placed and attached by P; A1 by the master; P's own M10 has Q2",
              m1.Owner == 0 && m1.Descendants.First(d => d.Key == P(0xB01)) is { Placer: 1, Attacher: 1 } && m1.Descendants.First(d => d.Key == R(1)) is { Placer: 0, Attacher: 0 }
           && Sw("M10").Owner == 1 && Sw("M10").Children.Count == 1, "");
        Check("census: P's override dropped W2b's XESP — it is no longer in M2's set", !Sw("M2").Descendants.Any(d => d.Key == R(10)) && Sw("M2").Children.Count == 3, $"{Sw("M2").Children.Count} children");
        Check("census: baseline is EnableState — M1 enabled, its on-set enabled, its off-set B1 disabled, D1 enabled through the chain, D2 disabled through the opposite chain; M2 disabled, W2 with it",
              sx.Baseline(S(1)).Enabled && sx.Baseline(R(1)).Enabled && !sx.Baseline(R(2)).Enabled && sx.Baseline(R(4)).Enabled && !sx.Baseline(R(32)).Enabled && !sx.Baseline(S(2)).Enabled && !sx.Baseline(R(6)).Enabled, "");
        Check("census/navcut: the COLL table is by INDEX — 49 is flagged NavmeshObstacle, 12 is not; N1, N8, N9 are navcuts (all under a switch), T9 on layer 12 is not",
              idx.CollisionLayersByIndex.TryGetValue(49, out var l49) && l49.NavmeshObstacle && idx.CollisionLayersByIndex.TryGetValue(12, out var l12) && !l12.NavmeshObstacle
           && sx.Navcuts.SetEquals(new[] { R(9), R(25), R(27) }) && sx.NavcutUnderSwitch.Count == 3 && !sx.IsNavcut(R(31)) && Sw("M2").NavcutCount == 1 && sx.NavcutsWithoutPrimitive == 0
           && sx.LayerName(R(9)) == "L_NAVCUT" && sx.LayerName(R(31)) == "L_TRIGGER",
              $"navcuts {sx.Navcuts.Count} under {sx.NavcutUnderSwitch.Count}; {sx.LayerName(R(9))} / {sx.LayerName(R(31))}");
        Check("census/navcut: the primitive is kept on the reference — N8's box is 100 x 100 x 50",
              idx.RefPrimitive.TryGetValue(R(25), out var pr) && pr.Type == "Box" && pr.Bounds.X == 100 && pr.Bounds.Z == 50, pr.ToString());

        // ---- the arm ----
        Check("arm/parse: a switch by editor id, a list of two, the baseline; a name the order does not carry is refused, never ignored",
              StateArm.Parse("M1", sx).Flips.SequenceEqual(new[] { S(1) }) && StateArm.Parse("M1,M2", sx).Flips.Count == 2 && StateArm.Parse("baseline", sx).IsBaseline
           && Throws(() => StateArm.Parse("NoSuchMarker", sx)) && Throws(() => StateArm.Parse("M1,NoSuchMarker", sx)) && StateArm.Parse(S(2).ToString(), sx).Flips[0] == S(2), "");
        var map = new SwitchMap(); map.Stages["Q:10"] = new List<string> { "M1", "M2" };
        Check("arm/parse: stage:<quest>:<n> reads the switch map's joint flip; without a map it is refused",
              StateArm.Parse("stage:Q:10", sx, map).Flips.Count == 2 && Throws(() => StateArm.Parse("stage:Q:10", sx)) && Throws(() => StateArm.Parse("stage:Q:99", sx, map)), "");
        var armM1 = StateArm.Parse("M1", sx);
        var ch = armM1.Changes(sx).ToDictionary(c => c.Key, c => c);
        Check("arm/changes: flipping M1 turns off M1, A1, A2, M3, D1 and Q1 and turns ON the off-set B1 and M3b's D2 — nine changes, three arrivals (B1, M3b, D2)",
              ch.Count == 9 && ch[R(2)] is { Before: false, After: true } && ch[R(32)] is { Before: false, After: true } && ch[R(1)] is { Before: true, After: false } && ch[R(4)] is { Before: true, After: false } && ch[P(0xB01)].After == false && ch[S(1)].Member is null,
              $"{ch.Count} changes: " + string.Join(", ", ch.Select(c => $"{idx.RefEditorId.GetValueOrDefault(c.Key)} {c.Value.Before}->{c.Value.After}")));
        {
            var facts = armM1.Wrap(sx.WinnerFacts, sx.Baseline);
            var memo = new Dictionary<FormKey, EnableResolution>();
            Check("arm/wrap: under the flip EnableState reads A1 disabled and B1 enabled, K1 (no parent) unchanged, and the flipped switch parentless",
                  !EnableState.Resolve(R(1), facts, memo).Enabled && EnableState.Resolve(R(2), facts, memo).Enabled && EnableState.Resolve(R(5), facts, memo).Enabled
               && facts(S(1)) is { Parent: null, InitiallyDisabled: true }, "");
        }
        Check("arm/changes: the baseline arm changes nothing", !StateArm.Baseline().Changes(sx).Any(), "");

        // ---- the class ----
        RouteAsk route = (StateArm _, FormKey k, bool navcut, out string why) =>
        {
            why = navcut ? "stand-in: a navcut box cuts the line" : "stand-in: beside the line";
            if (k == R(9)) return RouteCutVerdict.LikelyBlocked;
            if (k == R(25) || k == R(27)) return RouteCutVerdict.Blocked;
            if (k == R(26)) return RouteCutVerdict.Clipped;
            return null;
        };
        var defaultEv = new StateEvaluator(cidx, sx) { Route = route };
        var defaultTallyM2 = new StateTally();
        var defaultM2 = defaultEv.Evaluate(StateArm.Parse("M2", sx), p => p == 0, _ => false, defaultTallyM2);
        var defaultM5 = defaultEv.Evaluate(StateArm.Parse("M5", sx), p => p == 0, _ => false, new StateTally());
        Check("class/overlap policy: the default evaluator withholds legacy box overlap while contact and route remain active",
              defaultM2.All(r => r.Kind != StateRowKind.Overlap)
           && defaultTallyM2.StateOverlapUnmeasured > 0 && defaultTallyM2.PairsTested == 0
           && defaultM2.Count(r => r.Kind == StateRowKind.Route) == 1
           && defaultM5.All(r => r.Kind != StateRowKind.Overlap)
           && defaultM5.Count(r => r.Kind == StateRowKind.Contact) == 1,
              $"M2 {string.Join(", ", defaultM2.Select(r => r.Kind))}; M5 {string.Join(", ", defaultM5.Select(r => r.Kind))}");

        // Historical box-overlap assertions below remain a construction proof only. Production uses the default.
        var ev = new StateEvaluator(cidx, sx, allowLegacyBoundsOverlapForProof: true)
        {
            Route = route,
        };
        bool Van(int p) => p == 0;
        bool Us(int p) => false;
        var tally = new StateTally();
        var rowsM2 = ev.Evaluate(StateArm.Parse("M2", sx), Van, Us, tally);
        var ov2 = rowsM2.Where(r => r.Kind == StateRowKind.Overlap).ToList();
        Check("class/overlap: flipping M2 brings W2 into K2's box — one StateConditional overlap, 150 u along x (the least axis 150 wide, the z 300), 0.75 of the smaller, Bethesda floor; W2 x W2s is a same-set pair, flagged",
              ov2.Count(r => !r.SameSet) == 1 && ov2.Single(r => !r.SameSet) is { Verdict: StateVerdict.StateConditional, Member: var mm, Other: var oo, Depth: 150f, Population: StatePopulation.BethesdaFloor } && mm == R(6) && oo == R(7)
           && ov2.Count(r => r.SameSet) == 1 && tally.SameSetPairs == 1,
              string.Join("; ", ov2.Select(r => $"{r.Verdict} {idx.RefEditorId.GetValueOrDefault(r.Member)} x {(r.Other is { } o2 ? idx.RefEditorId.GetValueOrDefault(o2) : "-")} d{r.Depth:0} f{r.Frac:0.00} same={r.SameSet}")));
        Check("class/route: M2's navcut N1 arrives LIKELY-BLOCKED on the stand-in oracle — a StateConditional route row, navcut = true",
              rowsM2.Count(r => r.Kind == StateRowKind.Route) == 1 && rowsM2.Single(r => r.Kind == StateRowKind.Route) is { Verdict: StateVerdict.StateConditional, Navcut: true, Route: RouteCutVerdict.LikelyBlocked, Member: var nm } && nm == R(9),
              string.Join("; ", rowsM2.Where(r => r.Kind == StateRowKind.Route).Select(r => $"{r.Verdict} {r.Route} navcut={r.Navcut}")));
        var rowsM4 = ev.Evaluate(StateArm.Parse("M4", sx), Van, Us, tally);
        Check("class/overlap: flipping M4 takes W4 out of K4's box — one StateResolved overlap, reported, not a finding",
              rowsM4.Count == 1 && rowsM4[0] is { Kind: StateRowKind.Overlap, Verdict: StateVerdict.StateResolved, Arrives: false }, string.Join("; ", rowsM4.Select(r => $"{r.Kind} {r.Verdict}")));
        var rowsM1 = ev.Evaluate(armM1, Van, Us, tally);
        Check("class/population: flipping M1 takes P's Q1 out of the master's K1 — StateResolved, a mod's set over vanilla geometry, the switch owner riding on the row; the master's W2 x K2 is a same-author row, P's Q1 x K1 is not",
              rowsM1.Any(r => r.Member == P(0xB01) && r.Verdict == StateVerdict.StateResolved && r.Population == StatePopulation.ModSetOverVanilla && r.SwitchOwner == 0 && r.MemberPlacer == 1 && !r.SameAuthor)
           && ov2.Single(r => !r.SameSet).SameAuthor,
              string.Join("; ", rowsM1.Select(r => $"{r.Kind} {r.Verdict} {idx.RefEditorId.GetValueOrDefault(r.Member)} {r.Population}")));
        var t5 = new StateTally();
        var rowsM5 = ev.Evaluate(StateArm.Parse("M5", sx), Van, Us, t5);
        Check("class/contact: flipping M5 takes T5 out from under B5 — Stranded, StateConditional, Supported; B5c is held by T5r (ReSupported, no row)",
              rowsM5.Count(r => r.Kind == StateRowKind.Contact) == 1 && rowsM5.Single(r => r.Kind == StateRowKind.Contact) is { Verdict: StateVerdict.StateConditional, ContactVerdict: ContactVerdict.Stranded, Relation: ContactRelation.Supported, Other: var b5, Arrives: false } && b5 == R(14)
           && t5.ReSupported == 1 && t5.HostsLeaving == 1,
              string.Join("; ", rowsM5.Select(r => $"{r.Kind} {r.Verdict} {r.ContactVerdict} {r.Relation} other {(r.Other is { } o5 ? idx.RefEditorId.GetValueOrDefault(o5) : "-")}")) + $" resupported {t5.ReSupported}");
        var t6 = new StateTally();
        var rowsM6 = ev.Evaluate(StateArm.Parse("M6", sx), Van, Us, t6);
        Check("class/contact: flipping M6 brings T6 in under B6 — Sunk 17 u, StateConditional; B6t on its top is touching (counted)",
              rowsM6.Count(r => r.Kind == StateRowKind.Contact) == 1 && rowsM6.Single(r => r.Kind == StateRowKind.Contact) is { ContactVerdict: ContactVerdict.Sunk, Verdict: StateVerdict.StateConditional, Other: var b6, Penetration: > 16f and < 18f } && b6 == R(18)
           && t6.HostsArriving == 1 && t6.ReSupported == 1,
              string.Join("; ", rowsM6.Select(r => $"{r.Kind} {r.Verdict} {r.ContactVerdict} pen {r.Penetration:0.#} other {(r.Other is { } o6 ? idx.RefEditorId.GetValueOrDefault(o6) : "-")}")));
        var t7 = new StateTally();
        var rowsM7 = ev.Evaluate(StateArm.Parse("M7", sx), Van, Us, t7);
        Check("class/contact: M7's arriving bowls — B7 lands on T7 (touching, counted), B7f lands on nothing (floating, counted, no row), B7s lands over the table P moved away — Stranded on arrival, a row",
              t7.DepsArriving == 3 && t7.TouchingOnArrival == 1 && t7.FloatingOnArrival == 2
           && rowsM7.Count(r => r.Kind == StateRowKind.Contact) == 1 && rowsM7.Single(r => r.Kind == StateRowKind.Contact) is { ContactVerdict: ContactVerdict.Stranded, Member: var b7s, Other: var t7m, Arrives: true } && b7s == R(23) && t7m == R(24),
              $"arriving {t7.DepsArriving} touching {t7.TouchingOnArrival} floating {t7.FloatingOnArrival}; " + string.Join("; ", rowsM7.Select(r => $"{r.Kind} {r.Verdict} {r.ContactVerdict} {idx.RefEditorId.GetValueOrDefault(r.Member)}")));
        var t8 = new StateTally();
        var rowsM8 = ev.Evaluate(StateArm.Parse("M8", sx), Van, Us, t8);
        var rowsM9 = ev.Evaluate(StateArm.Parse("M9", sx), Van, Us, t8);
        Check("class/route: N8 arriving BLOCKED is a StateConditional route row, S8 CLIPPED is counted; N9 departing BLOCKED is StateResolved",
              rowsM8.Count(r => r.Kind == StateRowKind.Route) == 1 && rowsM8.Single(r => r.Kind == StateRowKind.Route) is { Verdict: StateVerdict.StateConditional, Route: RouteCutVerdict.Blocked, Navcut: true }
           && t8.RouteClipped == 1 && rowsM9.Count(r => r.Kind == StateRowKind.Route) == 1 && rowsM9.Single(r => r.Kind == StateRowKind.Route).Verdict == StateVerdict.StateResolved,
              $"M8: {string.Join("; ", rowsM8.Select(r => $"{r.Kind} {r.Verdict} {r.Route}"))} clipped {t8.RouteClipped}; M9: {string.Join("; ", rowsM9.Select(r => $"{r.Kind} {r.Verdict}"))}");
        Check("class/route: without an oracle the route half is skipped and counted, never invented",
              new StateEvaluator(cidx, sx).Evaluate(StateArm.Parse("M8", sx), Van, Us, new StateTally()).All(r => r.Kind != StateRowKind.Route), "");
        Check("class/baseline: the baseline arm yields no rows", ev.Evaluate(StateArm.Baseline(), Van, Us, new StateTally()).Count == 0, "");
        var rowsM11 = ev.Evaluate(StateArm.Parse("M11", sx), Van, Us, new StateTally());
        Check("class/responsible: W11 arrives into K11, a vanilla reference P re-based without moving — the other side's geometry is P's, so the row is a mod's ref under a vanilla set, not the floor and not same-author",
              rowsM11.Count(r => r.Kind == StateRowKind.Overlap) == 1 && rowsM11.Single(r => r.Kind == StateRowKind.Overlap) is { Population: StatePopulation.ModRefUnderVanillaSet, SameAuthor: false, OtherPlacer: 0, OtherResponsible: 1, MemberResponsible: 0 },
              string.Join("; ", rowsM11.Select(r => $"{r.Kind} {r.Population} placer {r.OtherPlacer} resp {r.OtherResponsible} same={r.SameAuthor}")));
        Check("class/joint: flipping M2 and M4 together yields both rows — the union of the singles (the sets do not meet)",
              ev.Evaluate(StateArm.Parse("M2,M4", sx), Van, Us, new StateTally()) is { } j && j.Count(r => r.Kind == StateRowKind.Overlap && !r.SameSet) == 2 && j.Count(r => r.Verdict == StateVerdict.StateResolved) == 1, "");
    }

    static bool Throws(Action a) { try { a(); return false; } catch { return true; } }
}
