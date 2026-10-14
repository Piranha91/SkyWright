using HarnessShared;
using HousecarlCore;
using Mutagen.Bethesda;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Plugins.Records;
using Mutagen.Bethesda.Skyrim;
using Noggog;

// PROOF BY CONSTRUCTION — LEFT IN EMPTY SPACE (_shared/ContactAssembly.cs, DetectorModel.EmptySpace; 2026-10-14). Cases ES-2000..2044
// (even numbers). Two levels: the rule on facts, and the search on a synthetic order written with Mutagen and read back with OBND box
// soups: a master (the room: floors and walls), the fixture's mod S, the mover D, a patch X that masters D, and a floor-population
// mover U. Every box is fixed by construction (centred on its placement: OBND x,y half-extents, z extent halved).
static class EmptySpaceProof
{
    public static void Run(Action<string, bool, string> Check)
    {
        Console.WriteLine("\nPROOF — LEFT IN EMPTY SPACE (_shared/ContactAssembly.cs EmptySpaceSearch, DetectorModel.EmptySpace)");
        Facts(Check);
        var root = Path.Combine(Path.GetTempPath(), "proofprobe-es-" + Environment.ProcessId);
        Directory.CreateDirectory(root);
        try { Order(Check, root); }
        finally { try { Directory.Delete(root, true); } catch { } }
    }

    static void Facts(Action<string, bool, string> Check)
    {
        var f = new DetectorModel.EmptySpaceFacts(Interior: true, ExistsWinner: true, ExistsAuthoring: true, Motion: DependentMotion.Fixed, Held: true,
            WinnerOverCap: false, AuthoringOverCap: false, WinnerGrounded: false, AuthoringGrounded: true, AuthoringAnchorChangedByMover: true,
            RePlacedByMoverSide: false, BethesdaFloor: false);
        (EmptySpaceVerdict V, string W) E(DetectorModel.EmptySpaceFacts x) => DetectorModel.EmptySpace(x);
        Check("ES-facts: grounded as authored on a floor the mover removed, nothing grounds it now -> LEFT IN EMPTY SPACE", E(f).V == EmptySpaceVerdict.LeftInEmptySpace, E(f).W);
        Check("ES-2034 (facts): exterior -> refused by name (the land is not read)", E(f with { Interior = false }) is { V: EmptySpaceVerdict.Refused } r34 && r34.W.Contains("land"), E(f with { Interior = false }).W);
        Check("ES-2038 (facts): absent under the winner (its own plugin disables it) -> Absent, not the class", E(f with { ExistsWinner = false }).V == EmptySpaceVerdict.Absent, E(f with { ExistsWinner = false }).W);
        Check("ES-facts: a loose body not held -> Falls (the rest rule's class)", E(f with { Held = false, Motion = DependentMotion.Movable }).V == EmptySpaceVerdict.Falls, "");
        Check("ES-facts: motion unknown and not held -> refused by name", E(f with { Held = false, Motion = DependentMotion.Unknown }).V == EmptySpaceVerdict.Refused, "");
        Check("ES-facts: ungrounded in both arms -> floats as shipped (the absolute floor)", E(f with { AuthoringGrounded = false }).V == EmptySpaceVerdict.FloatsAsShipped, "");
        Check("ES-facts: ungrounded as authored, grounded now -> no relation", E(f with { AuthoringGrounded = false, WinnerGrounded = true }).V == EmptySpaceVerdict.NoRelation, "");
        Check("ES-facts: grounded as authored by nothing the mover changed -> no relation", E(f with { AuthoringAnchorChangedByMover = false }).V == EmptySpaceVerdict.NoRelation, "");
        Check("ES-facts: the mover's side re-placed it -> re-placed (grounded / still empty), never the class",
              E(f with { RePlacedByMoverSide = true }).V == EmptySpaceVerdict.RePlacedStillEmpty && E(f with { RePlacedByMoverSide = true, WinnerGrounded = true }).V == EmptySpaceVerdict.RePlacedGrounded, "");
        Check("ES-facts: grounded now -> re-grounded", E(f with { WinnerGrounded = true }).V == EmptySpaceVerdict.ReGrounded, "");
        Check("ES-facts: Bethesda or USSEP on both sides -> the floor population", E(f with { BethesdaFloor = true }).V == EmptySpaceVerdict.FloorPopulation, "");
        Check("ES-facts: over the cap in either arm -> refused by name",
              E(f with { AuthoringOverCap = true }).V == EmptySpaceVerdict.Refused && E(f with { WinnerOverCap = true }).V == EmptySpaceVerdict.Refused, "");
    }

    static Static Stat(FormKey k, string edid, int x, int y, int z, bool marker = false) => new(k, SkyrimRelease.SkyrimSE)
    {
        EditorID = edid, Model = new Model { File = $"meshes\\es\\{edid}.nif" },
        ObjectBounds = new ObjectBounds { First = new P3Int16((short)-x, (short)-y, 0), Second = new P3Int16((short)x, (short)y, (short)z) },
        MajorRecordFlagsRaw = marker ? (int)EnableState.IsMarkerFlag : 0,
    };
    static ObjectBounds Ob(int x, int y, int z) => new() { First = new P3Int16((short)-x, (short)-y, 0), Second = new P3Int16((short)x, (short)y, (short)z) };
    static PlacedObject Ref(FormKey key, FormKey baseKey, float x, float y, float z, string edid, bool disabled = false, FormKey? parent = null, bool deleted = false)
    {
        var r = new PlacedObject(key, SkyrimRelease.SkyrimSE) { EditorID = edid, Placement = new Placement { Position = new P3Float(x, y, z), Rotation = new P3Float(0, 0, 0) } };
        r.Base.SetTo(baseKey);
        if (disabled) r.SkyrimMajorRecordFlags |= SkyrimMajorRecord.SkyrimMajorRecordFlag.InitiallyDisabled;
        if (deleted) r.IsDeleted = true;
        if (parent is { } pk) { r.EnableParent = new EnableParent(); r.EnableParent.Reference.SetTo(pk); }
        return r;
    }
    static void FileInterior(SkyrimMod mod, Cell cell)
    {
        uint id = cell.FormKey.ID;
        var block = new CellBlock { BlockNumber = (int)(id % 10), GroupType = GroupTypeEnum.InteriorCellBlock };
        var sub = new CellSubBlock { BlockNumber = (int)((id / 10) % 10), GroupType = GroupTypeEnum.InteriorCellSubBlock };
        sub.Cells.Add(cell); block.SubBlocks.Add(sub); mod.Cells.Records.Add(block);
    }

    static void Order(Action<string, bool, string> Check, string root)
    {
        var release = SkyrimRelease.SkyrimSE;
        var mKey = new ModKey("EsMaster", ModType.Master);
        var sKey = new ModKey("EsS", ModType.Plugin);
        var dKey = new ModKey("EsD", ModType.Plugin);
        var xKey = new ModKey("EsX", ModType.Plugin);
        var uKey = new ModKey("EsUssep", ModType.Plugin);
        FormKey M(uint id) => new(mKey, id); FormKey S(uint id) => new(sKey, id); FormKey D(uint id) => new(dKey, id);
        var cellK = M(0x810);
        // bases: the room's floor (256 x 256 x 20), wall (16 x 256 x 200), a 48 u slab, a marker; the mod's rack (an Activator), its
        // table (a Static), its own floor; an item; the weapon-rack chain (activator with the rack script, trigger, sword, keywords)
        var floorB = M(0x801); var wallB = M(0x802); var slabB = M(0x803); var markB = M(0x804); var vActB = M(0x805);
        var rackB = S(0x801); var tableB = S(0x802); var ownFloorB = S(0x803); var itemB = S(0x804);
        var rackActB = S(0x805); var trigB = S(0x806); var swordB = S(0x807); var kwTrig = S(0x808); var kwSword = S(0x809);
        float X0(int n) => 1000f * ((n - 2000) / 2 + 1);
        FormKey F(int n, int k = 0) => S((uint)(0x1000 + (n - 2000) * 8 + k));   // the mod's references of case n
        FormKey R(int n, int k = 0) => M((uint)(0x1000 + (n - 2000) * 8 + k));   // the room's references of case n
        FormKey Dn(int n, int k = 0) => D((uint)(0x1000 + (n - 2000) * 8 + k));  // the mover's own new references

        // ---- the master: the room ----
        var master = new SkyrimMod(mKey, release);
        master.Statics.Add(Stat(floorB, "EsFloor", 128, 128, 20));
        master.Statics.Add(Stat(wallB, "EsWall", 8, 128, 200));
        master.Statics.Add(Stat(slabB, "EsSlab", 24, 24, 10));
        master.Statics.Add(Stat(markB, "EsMarker", 8, 8, 16, marker: true));
        master.Activators.Add(new Mutagen.Bethesda.Skyrim.Activator(vActB, release) { EditorID = "EsVanillaActivator", Model = new Model { File = "meshes\\es\\va.nif" }, ObjectBounds = Ob(20, 20, 80) });
        var mc = new Cell(cellK, release) { EditorID = "EsCell", Flags = Cell.Flag.IsInteriorCell };
        int[] floored = { 2000, 2002, 2004, 2006, 2008, 2010, 2012, 2014, 2016, 2018, 2020, 2022, 2024, 2026, 2028, 2030, 2032, 2036, 2040, 2042, 2044 };
        foreach (int n in floored) mc.Temporary.Add(Ref(R(n), floorB, X0(n), 0, -10, $"EsFloor{n}", parent: n == 2002 ? R(2002, 1) : null));
        mc.Temporary.Add(Ref(R(2002, 1), markB, X0(2002) + 300, 0, 8, "EsParent2002"));                      // the floor's enable parent
        mc.Temporary.Add(Ref(R(2016, 1), wallB, X0(2016) + 28, 0, 100, "EsWall2016"));                       // touches the rack's +x face
        mc.Temporary.Add(Ref(R(2028, 1), slabB, X0(2028) + 44, 0, 5, "EsSlab2028"));                         // a 48 u slab the rack touches
        mc.Temporary.Add(Ref(R(2030, 1), wallB, X0(2030) + 108, 0, 100, "EsWall2030"));                      // touches the third rack only
        mc.Temporary.Add(Ref(R(2044, 1), wallB, X0(2044) + 108, 0, 100, "EsWall2044"));
        mc.Temporary.Add(Ref(R(2036, 1), vActB, X0(2036) + 40, 0, 40, "EsVanillaActivator2036"));            // a vanilla activator touching the rack
        mc.Temporary.Add(Ref(R(2024, 1), vActB, X0(2024), 0, 40, "EsVanillaRack2024"));                      // a vanilla fixture on the floor
        mc.Temporary.Add(Ref(R(2040, 1), markB, X0(2040) + 300, 0, 8, "EsSwitch2040", disabled: true));     // a switch, off at load
        mc.Temporary.Add(Ref(R(2042, 1), wallB, X0(2042) + 136, 0, 100, "EsWall2042"));                      // touches the own floor's +x edge
        FileInterior(master, mc);
        var mPath = Write(root, master);
        using var mOv = SkyrimMod.CreateFromBinaryOverlay(mPath, release);

        // ---- S: the fixtures ----
        var s = new SkyrimMod(sKey, release);
        s.Activators.Add(new Mutagen.Bethesda.Skyrim.Activator(rackB, release) { EditorID = "EsRack", Model = new Model { File = "meshes\\es\\rack.nif" }, ObjectBounds = Ob(20, 20, 80) });
        s.Statics.Add(Stat(tableB, "EsTable", 40, 40, 60));
        s.Statics.Add(Stat(ownFloorB, "EsOwnFloor", 128, 128, 20));
        s.MiscItems.Add(new MiscItem(itemB, release) { EditorID = "EsItem", Model = new Model { File = "meshes\\es\\item.nif" }, ObjectBounds = Ob(5, 5, 4) });
        s.Keywords.Add(new Keyword(kwTrig, release) { EditorID = "EsWRackTrigger" });
        s.Keywords.Add(new Keyword(kwSword, release) { EditorID = "EsWeaponTypeSword" });
        var rackAct = new Mutagen.Bethesda.Skyrim.Activator(rackActB, release) { EditorID = "EsRackActivator", Model = new Model { File = "meshes\\es\\rackact.nif" }, ObjectBounds = Ob(20, 20, 80) };
        var script = new ScriptEntry { Name = RackMount.ActivatorScript };
        var p1 = new ScriptObjectProperty { Name = "WRackTrigger" }; p1.Object.SetTo(kwTrig); script.Properties.Add(p1);
        var p2 = new ScriptObjectProperty { Name = "WeaponTypeSword" }; p2.Object.SetTo(kwSword); script.Properties.Add(p2);
        rackAct.VirtualMachineAdapter = new VirtualMachineAdapter(); rackAct.VirtualMachineAdapter.Scripts.Add(script);
        s.Activators.Add(rackAct);
        s.Activators.Add(new Mutagen.Bethesda.Skyrim.Activator(trigB, release) { EditorID = "EsRackTrigger", Model = new Model { File = "meshes\\es\\trig.nif" }, ObjectBounds = Ob(20, 20, 80) });
        var sword = new Weapon(swordB, release) { EditorID = "EsSword", Model = new Model { File = "meshes\\es\\sword.nif" }, ObjectBounds = Ob(3, 3, 40) };
        sword.Keywords = new ExtendedList<IFormLinkGetter<IKeywordGetter>> { kwSword.ToLink<IKeywordGetter>() };
        s.Weapons.Add(sword);
        var sc = new Cell(cellK, release) { Flags = Cell.Flag.IsInteriorCell };
        void Rack(int n, float dx = 0, float z = 40, int k = 0, FormKey? parent = null) => sc.Temporary.Add(Ref(F(n, k), rackB, X0(n) + dx, 0, z, $"EsRack{n}_{k}", parent: parent));
        foreach (int n in new[] { 2000, 2002, 2004, 2006, 2016, 2018, 2022, 2028, 2036 }) Rack(n);
        Rack(2008); Rack(2008, 40, k: 1);                                                   // two racks side by side, touching
        Rack(2020, z: 140);                                                                 // 100 u above the floor as shipped
        foreach (int n in new[] { 2030, 2044 }) { Rack(n, 0); Rack(n, 40, k: 1); Rack(n, 80, k: 2); }   // a chain; the third touches the wall
        for (int k = 0; k < 5; k++) Rack(2032, 40 * k, k: k);                                 // five in a chain (the cap proof reads it at 3)
        Rack(2040, parent: R(2040, 1));                                                     // enabled only when the switch flips
        // 2010: the weapon-rack chain on the floor; the sword 30 u above the trigger's top, touching nothing but the mount link
        sc.Temporary.Add(Ref(F(2010, 1), trigB, X0(2010), 0, 40, "EsTrig2010"));
        var act = Ref(F(2010, 2), rackActB, X0(2010) + 200, 0, 40, "EsAct2010");
        var l1 = new LinkedReferences(); l1.Reference.SetTo(F(2010)); act.LinkedReferences.Add(l1);
        var l2 = new LinkedReferences(); l2.KeywordOrReference.SetTo(kwTrig); l2.Reference.SetTo(F(2010, 1)); act.LinkedReferences.Add(l2);
        sc.Temporary.Add(act);
        sc.Temporary.Add(Ref(F(2010), swordB, X0(2010), 0, 130, "EsSword2010"));
        // 2012: the mod's table on the floor and an item on the table
        sc.Temporary.Add(Ref(F(2012, 1), tableB, X0(2012), 0, 30, "EsTable2012"));
        sc.Temporary.Add(Ref(F(2012), itemB, X0(2012), 0, 62, "EsItem2012"));
        sc.Temporary.Add(Ref(F(2014), itemB, X0(2014), 0, 2, "EsItem2014"));               // an item on the room's floor
        // 2026 / 2042 (Gate A review condition 2): the mod's own floor on the room's floor, a rack on the own floor
        foreach (int n in new[] { 2026, 2042 })
        {
            sc.Temporary.Add(Ref(F(n, 1), ownFloorB, X0(n), 0, 10, $"EsOwnFloor{n}"));
            Rack(n, z: 60);
        }
        FileInterior(s, sc);
        var sPath = Write(root, s, mOv);
        using var sOv = SkyrimMod.CreateFromBinaryOverlay(sPath, release);

        // ---- D: the mover ----
        var d = new SkyrimMod(dKey, release);
        var dc = new Cell(cellK, release) { Flags = Cell.Flag.IsInteriorCell };
        foreach (int n in new[] { 2000, 2008, 2010, 2012, 2014, 2016, 2018, 2020, 2022, 2026, 2028, 2030, 2032, 2036, 2040, 2044 })
            dc.Temporary.Add(Ref(R(n), floorB, X0(n), 0, -10, $"EsFloor{n}", disabled: true));
        dc.Temporary.Add(Ref(R(2002, 1), markB, X0(2002) + 300, 0, 8, "EsParent2002", disabled: true));     // removes the floor through its parent
        dc.Temporary.Add(Ref(R(2004), floorB, X0(2004), 0, -10, "EsFloor2004", deleted: true));
        dc.Temporary.Add(Ref(R(2006), floorB, X0(2006), 0, -3000, "EsFloor2006"));                          // moved far below
        dc.Temporary.Add(Ref(Dn(2018), floorB, X0(2018), 0, -10, "EsNewFloor2018"));                        // the mover's own new floor
        dc.Temporary.Add(Ref(Dn(2022), floorB, X0(2022) + 500, 0, -10, "EsNewFloor2022"));                  // the mover's floor elsewhere
        dc.Temporary.Add(Ref(R(2044, 1), wallB, X0(2044) + 108, 0, 100, "EsWall2044", disabled: true));     // and the wall
        dc.Temporary.Add(Ref(R(2042, 1), wallB, X0(2042) + 136, 0, 100, "EsWall2042", disabled: true));     // only the wall
        FileInterior(d, dc);
        var dPath = Write(root, d, mOv);
        using var dOv = SkyrimMod.CreateFromBinaryOverlay(dPath, release);

        // ---- X: a patch on the mover's side (masters M, S, D): moves 2022's rack onto the mover's floor ----
        var x = new SkyrimMod(xKey, release);
        var xc = new Cell(cellK, release) { Flags = Cell.Flag.IsInteriorCell };
        xc.Temporary.Add(Ref(F(2022), rackB, X0(2022) + 500, 0, 40, "EsRack2022_0"));
        xc.Temporary.Add(Ref(Dn(2022), floorB, X0(2022) + 500, 0, -10, "EsNewFloor2022"));     // an identical override, so the patch masters D
        FileInterior(x, xc);
        var xPath = Write(root, x, mOv, sOv, dOv);

        // ---- U: a floor-population mover (named as USSEP below): removes 2024's floor under the vanilla fixture ----
        var u = new SkyrimMod(uKey, release);
        var uc = new Cell(cellK, release) { Flags = Cell.Flag.IsInteriorCell };
        uc.Temporary.Add(Ref(R(2024), floorB, X0(2024), 0, -10, "EsFloor2024", disabled: true));
        FileInterior(u, uc);
        var uPath = Write(root, u, mOv);

        ContactIndex? cidx = null;
        cidx = new ContactIndex(k => cidx!.Index.ObndBox(k) is { IsEmpty: false } bx
            ? new RefGeometry(bx, ContactIndex.BoxSoup(bx), ContactIndex.BoxSoup(bx), 'o', 'o', HostUsable: true, Array.Empty<(string, string)>(), false) : null);
        var paths = new[] { mPath, sPath, dPath, xPath, uPath };
        var keys = new ModKey[paths.Length];
        var masterIdx = new HashSet<int>[paths.Length];
        for (int p = 0; p < paths.Length; p++)
        {
            using var mod = SkyrimMod.CreateFromBinaryOverlay(paths[p], release);
            keys[p] = mod.ModKey;
            var names = mod.ModHeader.MasterReferences.Select(q => q.Master).ToHashSet();
            masterIdx[p] = Enumerable.Range(0, p).Where(q => names.Contains(keys[q])).ToHashSet();
            cidx.Index.Ingest(p, mod);
        }
        var closure = DetectorModel.MasterClosure(masterIdx);
        string Name(int p) => p < 0 ? "(none)" : keys[p].FileName.String;
        bool IsMasterFile(string f) => string.Equals(f, mKey.FileName.String, StringComparison.OrdinalIgnoreCase);
        bool Floor(int p) => p == 0 || p == 4;                                                          // the master and the USSEP stand-in
        var es = new EmptySpaceSearch(cidx, closure, Name, (_, _) => true, IsMasterFile, Floor);
        const int Dm = 2, Um = 4;
        string Show(EmptySpaceRow r) => $"{r.Verdict} ({r.HeldBy}; winner {r.Winner.Members} members, grounded {r.Winner.Grounded}; authoring grounded {r.Authoring.Grounded}, changed [{string.Join(" ", r.Authoring.ChangedAnchors.Select(a => a.ID.ToString("X")))}]; own [{string.Join(" ", r.OwnSupport.Select(a => a.ID.ToString("X")))}]): {r.Why}";
        EmptySpaceRow J(FormKey f, int m = Dm) => es.Judge(f, m);

        var r2000 = J(F(2000));
        Check("ES-2000: a rack on the room's floor, the mover disables the floor (its own flag) -> LEFT IN EMPTY SPACE, the floor named as the changed anchor",
              r2000.Verdict == EmptySpaceVerdict.LeftInEmptySpace && r2000.Authoring.ChangedAnchors.SequenceEqual(new[] { R(2000) }) && r2000.HeldBy == "fixed", Show(r2000));
        var r2002 = J(F(2002));
        Check("ES-2002: the floor leaves through an enable parent the mover disables (R1) -> LEFT IN EMPTY SPACE", r2002.Verdict == EmptySpaceVerdict.LeftInEmptySpace, Show(r2002));
        var r2004 = J(F(2004));
        Check("ES-2004: the mover deletes the floor -> LEFT IN EMPTY SPACE", r2004.Verdict == EmptySpaceVerdict.LeftInEmptySpace, Show(r2004));
        var r2006 = J(F(2006));
        Check("ES-2006: the mover moves the floor 2990 u down -> LEFT IN EMPTY SPACE", r2006.Verdict == EmptySpaceVerdict.LeftInEmptySpace, Show(r2006));
        var r2008a = J(F(2008)); var r2008b = J(F(2008, 1));
        Check("ES-2008: two racks touching each other, the floor disabled -> BOTH left in empty space (assembly of 2): the neighbour does not hold it up",
              r2008a.Verdict == EmptySpaceVerdict.LeftInEmptySpace && r2008b.Verdict == EmptySpaceVerdict.LeftInEmptySpace && r2008a.Winner.Members == 2, $"{Show(r2008a)} | {Show(r2008b)}");
        {
            // the gap this class closes: STRANDED reads the same rack ReSupported by its neighbour
            var skips = new ContactSkips();
            var hosts = cidx.MovedHosts(skips, closure);
            var depHash = cidx.DependentSnaps(skips);
            var winHash = cidx.PhysicalWinners(null, skips);
            var h = hosts.FirstOrDefault(h => h.Key == R(2008));
            var row = h is null ? null : cidx.Judge(h, depHash, winHash, closure, skips).FirstOrDefault(r => r.Dependent == F(2008));
            Check("ES-2008b (the gap): the moved-host contact pass reads the same rack RESUPPORTED by its neighbour, not Stranded",
                  row is { Verdict: ContactVerdict.ReSupported }, row is null ? "(no row)" : $"{row.Verdict}: {row.Why}");
        }
        var r2010 = J(F(2010));
        Check("ES-2010: a sword mounted on the mod's weapon rack (30 u above it, held only by the mount), the floor disabled -> LEFT IN EMPTY SPACE, held 'mounted on' the trigger",
              r2010.Verdict == EmptySpaceVerdict.LeftInEmptySpace && r2010.HeldBy == $"mounted on {F(2010, 1)}", Show(r2010));
        var r2012 = J(F(2012)); var r2012t = J(F(2012, 1));
        Check("ES-2012: an item resting on the mod's own table, the floor disabled -> the item (held by the table) and the table both LEFT IN EMPTY SPACE",
              r2012.Verdict == EmptySpaceVerdict.LeftInEmptySpace && r2012.HeldBy.StartsWith("rests on") && r2012t.Verdict == EmptySpaceVerdict.LeftInEmptySpace, $"{Show(r2012)} | {Show(r2012t)}");
        var r2014 = J(F(2014));
        Check("ES-2014: an item alone on the floor, the floor disabled -> Falls (context, the rest rule's class)", r2014.Verdict == EmptySpaceVerdict.Falls, Show(r2014));
        var r2016 = J(F(2016));
        Check("ES-2016: a wall still touches the rack after its floor goes -> RE-GROUNDED", r2016.Verdict == EmptySpaceVerdict.ReGrounded, Show(r2016));
        var r2018 = J(F(2018));
        Check("ES-2018: the mover lays its own new floor under the rack -> RE-GROUNDED (the mover's floor is the room's)", r2018.Verdict == EmptySpaceVerdict.ReGrounded, Show(r2018));
        var r2020 = J(F(2020));
        Check("ES-2020: a rack 100 u above the floor as shipped -> FLOATS AS SHIPPED (the absolute floor), never the class", r2020.Verdict == EmptySpaceVerdict.FloatsAsShipped, Show(r2020));
        var r2022 = J(F(2022));
        Check("ES-2022: a patch on the mover's side re-places the rack onto the mover's floor -> RE-PLACED, GROUNDED", r2022.Verdict == EmptySpaceVerdict.RePlacedGrounded, Show(r2022));
        var r2024 = es.Judge(R(2024, 1), Um);
        Check("ES-2024: a USSEP-like mover removes the floor under a master's fixture -> the floor population, counted apart", r2024.Verdict == EmptySpaceVerdict.FloorPopulation, Show(r2024));
        var r2026 = J(F(2026));
        Check("ES-2026 (condition 2a): the rack stands on its own mod's floor; the room's floor under it goes -> LEFT IN EMPTY SPACE, own support named (the own floor)",
              r2026.Verdict == EmptySpaceVerdict.LeftInEmptySpace && r2026.OwnSupport.SequenceEqual(new[] { F(2026, 1) }), Show(r2026));
        var r2042 = J(F(2042));
        Check("ES-2042 (condition 2b): the rack on its own floor; the mover removes one wall while the room's floor stands -> RE-GROUNDED through the room's floor, own support named",
              r2042.Verdict == EmptySpaceVerdict.ReGrounded && r2042.OwnSupport.SequenceEqual(new[] { F(2042, 1) }) && r2042.Authoring.ChangedAnchors.Contains(R(2042, 1)), Show(r2042));
        var r2028 = J(F(2028));
        Check("ES-2028: the only thing left touching the rack is a 48 u slab of the room -> LEFT IN EMPTY SPACE (under the anchor size)", r2028.Verdict == EmptySpaceVerdict.LeftInEmptySpace, Show(r2028));
        var r2030 = J(F(2030)); var r2044 = J(F(2044));
        Check("ES-2030: three racks in a chain, the third touching a wall; the floor goes -> the first RE-GROUNDED through two neighbours",
              r2030.Verdict == EmptySpaceVerdict.ReGrounded, Show(r2030));
        Check("ES-2044: the same chain with the wall removed too -> the first LEFT IN EMPTY SPACE (assembly 3)", r2044.Verdict == EmptySpaceVerdict.LeftInEmptySpace && r2044.Winner.Members == 3, Show(r2044));
        var capped = new EmptySpaceSearch(cidx, closure, Name, (_, _) => true, IsMasterFile, Floor) { MaxMembers = 3 };
        var r2032 = capped.Judge(F(2032), Dm);
        Check("ES-2032: five racks in a chain read with a cap of 3 -> refused by name (assembly over the cap), not a finding",
              r2032.Verdict == EmptySpaceVerdict.Refused && r2032.Why.Contains("passes 3 references"), Show(r2032));
        var r2036 = J(F(2036));
        Check("ES-2036: the rack touches a master's activator that floats with it -> LEFT IN EMPTY SPACE (an activator is no floor or wall)",
              r2036.Verdict == EmptySpaceVerdict.LeftInEmptySpace && r2036.Winner.Members == 2, Show(r2036));
        var r2040 = J(F(2040));
        Check("ES-2040a: a rack enabled only under a switch -> ABSENT at load", r2040.Verdict == EmptySpaceVerdict.Absent, Show(r2040));
        var sx = SwitchIndex.Build(cidx.Index);
        var arm = new StateArm("EsSwitch2040", new[] { R(2040, 1) });
        var inState = es.JudgeArrivals(arm, sx).Where(t => t.Row.Fixture == F(2040)).Select(t => t.Row).ToList();
        Check("ES-2040b: the same rack in the state that flips the switch (stateprobe's per-state pass) -> LEFT IN EMPTY SPACE, the state named; the arms' state cleared afterwards",
              inState.Count == 1 && inState[0].Verdict == EmptySpaceVerdict.LeftInEmptySpace && inState[0].State == "EsSwitch2040" && cidx.State is null,
              inState.Count == 0 ? "(no row)" : Show(inState[0]));
        var cands = es.CandidatesNear(cidx.MovedHosts(new ContactSkips(), closure)).ToList();
        Check("ES-candidates: every case's fixture is paired with its mover by the host census (contactprobe's path)",
              new[] { 2000, 2002, 2004, 2006, 2008, 2012, 2016, 2018, 2022, 2026, 2028, 2036 }.All(n => cands.Contains((F(n), Dm))) && cands.Contains((R(2024, 1), Um)),
              $"{cands.Count} pairs");
        var (pst, pwhy) = es.Predicate(F(2000)); var (gst, _) = es.Predicate(F(2016));
        Check("ES-predicate (--base): a fixture still in empty space under this order reads Holds; a re-grounded one Gone",
              pst == PhysicalState.Holds && gst == PhysicalState.Gone, $"{pst}: {pwhy}; 2016 {gst}");
    }

    static string Write(string root, SkyrimMod mod, params ISkyrimModGetter[] masters)
    {
        var path = Path.Combine(root, mod.ModKey.FileName.String);
        mod.BeginWrite.ToPath(path).WithLoadOrder(masters).Write();
        return path;
    }
}
