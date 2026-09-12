using HarnessShared;
using HousecarlCore;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Skyrim;
using Noggog;

// PROOF BY CONSTRUCTION — place-based grouping of the load-order search
// (_shared/PlaceIndex.cs and the place terms of _shared/OrderSearch.cs; roadmap 4.20, 2026-09-12).
//
// The fixture is a set of REAL plugins written by Mutagen into a temp folder: a master with interior cells, a
// worldspace with a persistent cell and exterior cells, statics, references, navmeshes, a landscape and items; then
// thirteen override plugins that contest those records in the shapes the definition (the run plan's P1–P4 and its
// amendment D-1) has to get right — the same interior cell, two interior cells, items only, a joined exterior block
// beside a separate one, a diagonal that must NOT join, a CELL record on its own cell, a WRLD record as a worldspace
// block, a pair sharing only the worldspace-wide containers, a pair sharing one persistent reference, a disputed
// filing, and the three ways a cell gets its name. Every expected number below is worked out from that fixture BY
// HAND in the case text — which records each plugin carries and where the master files them — never from what the
// code printed.
//
// The cases follow the ways this layer can be quietly wrong:
//   placed      a record given no place, or the wrong cell (the later plugin's filing instead of the first's);
//   joined      two cells joined that are not edge-adjacent (a diagonal), or split that are;
//   ranked      a pair contesting five items outranking one rebuilding a cell, because "contention" was one number —
//               or two plugins that each drop one reference somewhere in a worldspace outranking it, because the
//               WRLD record and the persistent cell they both carry as containers were counted as meeting there;
//   lost        a candidate with no cell dropped, or a place table that names a place no movable pair contests;
//   drifted     a manifest whose new fields never reached the bytes, or reached them after the lists ordersweep cuts.
static class PlaceProof
{
    public static void Run(Action<string, bool, string> Check)
    {
        Console.WriteLine("\nPROOF — place-based grouping (_shared/PlaceIndex.cs, _shared/OrderSearch.cs, roadmap 4.20)");
        Console.WriteLine("   Mutagen-written plugins in a temp folder; every count below hand-derived from what each plugin files where.\n");
        var root = Path.Combine(Path.GetTempPath(), "proofprobe-place-" + Environment.ProcessId);
        Directory.CreateDirectory(root);
        try { Body(Check, root); }
        finally { try { Directory.Delete(root, true); } catch { } }
    }

    static void Body(Action<string, bool, string> Check, string root)
    {
        var release = SkyrimRelease.SkyrimSE;
        var mKey = new ModKey("PlaceMaster", ModType.Master);
        ModKey K(string s) => new("Place" + s, ModType.Plugin);
        FormKey M(uint id) => new(mKey, id);

        // ---- the master's records, by hand -------------------------------------------------------------------
        var tableK = M(0x801);
        FormKey Item(int n) => M((uint)(0x8A0 + n));                    // I1..I5 — MISC, never in a cell
        var ci1 = M(0x810); var ci2 = M(0x811); var ci3 = M(0x812); var ci4 = M(0x813);
        FormKey R(int n) => M((uint)(0x900 + n));                       // interior references
        var n1 = M(0x920); var n2 = M(0x921);                           // navmeshes
        var wKey = M(0x830); var topK = M(0x831);                       // the worldspace and its persistent cell
        var rp1 = M(0x931); var rp2 = M(0x932);                         // persistent references
        var e00 = M(0x840); var e10 = M(0x841); var e11 = M(0x842); var e01 = M(0x843); var e55 = M(0x844);
        FormKey X(int n) => M((uint)(0x940 + n));                       // exterior references
        var l1 = M(0x950);                                              // the LAND of (0,0)

        static PlacedObject Ref(FormKey key, FormKey baseKey, float x, string? edid = null)
        {
            var r = new PlacedObject(key, SkyrimRelease.SkyrimSE) { EditorID = edid, Placement = new Placement { Position = new P3Float(x, 0, 0), Rotation = new P3Float(0, 0, 0) } };
            r.Base.SetTo(baseKey);
            return r;
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
        static Cell Interior(FormKey key, string? edid) => new(key, SkyrimRelease.SkyrimSE) { EditorID = edid, Flags = Cell.Flag.IsInteriorCell };
        static Cell Exterior(FormKey key, int x, int y, string? edid) => new(key, SkyrimRelease.SkyrimSE) { EditorID = edid, Flags = default, Grid = new CellGrid { Point = new P2Int(x, y) } };
        static Landscape Land(FormKey key) => new(key, SkyrimRelease.SkyrimSE) { VertexHeightMap = new LandscapeVertexHeightMap { Offset = 0f, HeightMap = new Array2d<sbyte>(new sbyte[33, 33]) } };
        static Worldspace World(FormKey wKey, string? edid, Cell? top, params Cell[] exteriors)
        {
            var ws = new Worldspace(wKey, SkyrimRelease.SkyrimSE) { EditorID = edid };
            if (top is not null) ws.TopCell = top;
            if (exteriors.Length > 0)
            {
                var blk = new WorldspaceBlock { BlockNumberX = 0, BlockNumberY = 0, GroupType = GroupTypeEnum.ExteriorCellBlock };
                var sub = new WorldspaceSubBlock { BlockNumberX = 0, BlockNumberY = 0, GroupType = GroupTypeEnum.ExteriorCellSubBlock };
                foreach (var c in exteriors) sub.Items.Add(c);
                blk.Items.Add(sub); ws.SubCells.Add(blk);
            }
            return ws;
        }
        string Write(SkyrimMod mod, params ISkyrimModGetter[] masters)
        {
            var path = Path.Combine(root, mod.ModKey.FileName.String);
            mod.BeginWrite.ToPath(path).WithLoadOrder(masters).Write();
            return path;
        }

        // THE MASTER. Interior cells: CI1 "PlaceInt1" (R1..R6, navmesh N1), CI2 "PlaceInt2" (R7..R9, N2), CI3 with NO
        // EditorID (R10, R11), CI4 with NO EditorID (R12). Worldspace W "PlaceWorld": persistent cell T (no EditorID,
        // RP1, RP2); exterior (0,0) "PlaceExt00" X1 X2 + LAND L1; (1,0) "PlaceExt10" X3; (1,1) "PlaceExt11" X4 X5;
        // (0,1) "PlaceExt01" X6; (5,5) "PlaceExt55" X7 X8 X9. Items I1..I5 at the top level.
        var master = new SkyrimMod(mKey, release);
        master.Statics.Add(new Static(tableK, release) { EditorID = "PlaceTable" });
        for (int i = 1; i <= 5; i++) master.MiscItems.Add(new MiscItem(Item(i), release) { EditorID = $"PlaceItem{i}" });
        var mCi1 = Interior(ci1, "PlaceInt1"); foreach (int n in new[] { 1, 2, 3, 4, 5, 6 }) mCi1.Temporary.Add(Ref(R(n), tableK, 100 * n, $"R{n}"));
        mCi1.NavigationMeshes.Add(new NavigationMesh(n1, release));
        var mCi2 = Interior(ci2, "PlaceInt2"); foreach (int n in new[] { 7, 8, 9 }) mCi2.Temporary.Add(Ref(R(n), tableK, 100 * n, $"R{n}"));
        mCi2.NavigationMeshes.Add(new NavigationMesh(n2, release));
        var mCi3 = Interior(ci3, null); foreach (int n in new[] { 10, 11 }) mCi3.Temporary.Add(Ref(R(n), tableK, 100 * n, $"R{n}"));
        var mCi4 = Interior(ci4, null); mCi4.Temporary.Add(Ref(R(12), tableK, 1200, "R12"));
        FileInterior(master, mCi1); FileInterior(master, mCi2); FileInterior(master, mCi3); FileInterior(master, mCi4);
        var mTop = new Cell(topK, release) { Flags = default };
        mTop.Persistent.Add(Ref(rp1, tableK, 10, "RP1")); mTop.Persistent.Add(Ref(rp2, tableK, 20, "RP2"));
        var mE00 = Exterior(e00, 0, 0, "PlaceExt00"); mE00.Temporary.Add(Ref(X(1), tableK, 1, "X1")); mE00.Temporary.Add(Ref(X(2), tableK, 2, "X2")); mE00.Landscape = Land(l1);
        var mE10 = Exterior(e10, 1, 0, "PlaceExt10"); mE10.Temporary.Add(Ref(X(3), tableK, 3, "X3"));
        var mE11 = Exterior(e11, 1, 1, "PlaceExt11"); mE11.Temporary.Add(Ref(X(4), tableK, 4, "X4")); mE11.Temporary.Add(Ref(X(5), tableK, 5, "X5"));
        var mE01 = Exterior(e01, 0, 1, "PlaceExt01"); mE01.Temporary.Add(Ref(X(6), tableK, 6, "X6"));
        var mE55 = Exterior(e55, 5, 5, "PlaceExt55"); foreach (int n in new[] { 7, 8, 9 }) mE55.Temporary.Add(Ref(X(n), tableK, n, $"X{n}"));
        master.Worldspaces.Add(World(wKey, "PlaceWorld", mTop, mE00, mE10, mE11, mE01, mE55));
        var mPath = Write(master);
        using var mOv = SkyrimMod.CreateFromBinaryOverlay(mPath, release);

        // THE OVERRIDES, each mastering PlaceMaster.esm only, so every pair of them is MOVABLE and every pair with the
        // master is fixed by the master rule. What each carries (a plugin filing a reference in a cell carries the
        // CELL record too; one filing an exterior cell carries the WRLD record; one filing a persistent reference
        // carries the persistent cell's CELL record and the WRLD):
        //   A: CI1 { R1 R2 R3 N1 }          B: CI1 { R1 R2 N1 }               -> A|B contest R1 R2 N1 + CELL CI1 = 4, all in CI1
        //   C: CI2 { R7 R8 } CI3 { R10 }, and C names CI3 "PlaceInt3Late"
        //   D: CI2 { R7 R8 } CI3 { R10 }, and D names CI3 "PlaceInt3Other"    -> C|D: CI2 { R7 R8 CELL } = 3, CI3 { R10 CELL } = 2
        //   E: I1..I5                        F: I1..I5                        -> E|F: 5 records, none in any place
        //   G: T { RP1 } (0,0) { X1 X2 L1 } (1,0) { X3 } (5,5) { X7 X8 }
        //   H: the same                                                        -> G|H: (0,0) 4 + (1,0) 2 joined = 6; (5,5) 3;
        //                                                                         T { RP1 CELL } = 2; WRLD 1 -> place 12,
        //                                                                         cell 10 (CELL T and the WRLD are containers)
        //   I: (0,0) { X1 } (1,1) { X4 X5 }  J: the same                       -> I|J: (1,1) 3, (0,0) 2, WRLD 1 = place 6, cell 5;
        //                                                                         TWO exterior blocks: (0,0) and (1,1) are diagonal
        //   G|I, G|J, H|I, H|J: X1, CELL (0,0), WRLD = place 3, cell 2 each
        //   K: files R12 under CI1 (the master files it under CI4)             -> disputed; K|A and K|B: CELL CI1 = 1 each
        //   O (a master-flagged .esm loading right after the master): files R12 under a cell of ITS OWN ("PlaceOwnCell",
        //      defined by O) -> a second disagreeing filing of the same record. O shares R12 with K, but O|K is fixed
        //      by the grouping rule and M|O by the master rule, so no MOVABLE pair changes and CI4 stays out of the table
        //   L: T { RP2 }                     N: T { RP2 }                      -> L|N: RP2, CELL T, WRLD = place 3, cell 1
        //      and against G, H: CELL T + WRLD = place 2, cell 0 (containers only); against I, J: WRLD = place 1, cell 0
        SkyrimMod Plugin(string s) => new(K(s), release);
        var a = Plugin("A"); { var c = Interior(ci1, null); foreach (int n in new[] { 1, 2, 3 }) c.Temporary.Add(Ref(R(n), tableK, 100 * n + 1, $"R{n}")); c.NavigationMeshes.Add(new NavigationMesh(n1, release)); FileInterior(a, c); }
        var b = Plugin("B"); { var c = Interior(ci1, null); foreach (int n in new[] { 1, 2 }) c.Temporary.Add(Ref(R(n), tableK, 100 * n + 2, $"R{n}")); c.NavigationMeshes.Add(new NavigationMesh(n1, release)); FileInterior(b, c); }
        var cc = Plugin("C"); { var c2 = Interior(ci2, null); foreach (int n in new[] { 7, 8 }) c2.Temporary.Add(Ref(R(n), tableK, 100 * n + 3, $"R{n}")); FileInterior(cc, c2); var c3 = Interior(ci3, "PlaceInt3Late"); c3.Temporary.Add(Ref(R(10), tableK, 1003, "R10")); FileInterior(cc, c3); }
        var d = Plugin("D"); { var c2 = Interior(ci2, null); foreach (int n in new[] { 7, 8 }) c2.Temporary.Add(Ref(R(n), tableK, 100 * n + 4, $"R{n}")); FileInterior(d, c2); var c3 = Interior(ci3, "PlaceInt3Other"); c3.Temporary.Add(Ref(R(10), tableK, 1004, "R10")); FileInterior(d, c3); }
        var e = Plugin("E"); for (int i = 1; i <= 5; i++) e.MiscItems.Add(new MiscItem(Item(i), release) { EditorID = $"PlaceItem{i}", Value = 5 });
        var f = Plugin("F"); for (int i = 1; i <= 5; i++) f.MiscItems.Add(new MiscItem(Item(i), release) { EditorID = $"PlaceItem{i}", Value = 6 });
        SkyrimMod ExteriorPair(string s, float dx)
        {
            var mod = Plugin(s);
            var top = new Cell(topK, release) { Flags = default }; top.Persistent.Add(Ref(rp1, tableK, 10 + dx, "RP1"));
            var c00 = Exterior(e00, 0, 0, null); c00.Temporary.Add(Ref(X(1), tableK, 1 + dx, "X1")); c00.Temporary.Add(Ref(X(2), tableK, 2 + dx, "X2")); c00.Landscape = Land(l1);
            var c10 = Exterior(e10, 1, 0, null); c10.Temporary.Add(Ref(X(3), tableK, 3 + dx, "X3"));
            var c55 = Exterior(e55, 5, 5, null); c55.Temporary.Add(Ref(X(7), tableK, 7 + dx, "X7")); c55.Temporary.Add(Ref(X(8), tableK, 8 + dx, "X8"));
            mod.Worldspaces.Add(World(wKey, null, top, c00, c10, c55));
            return mod;
        }
        SkyrimMod DiagonalPair(string s, float dx)
        {
            var mod = Plugin(s);
            var c00 = Exterior(e00, 0, 0, null); c00.Temporary.Add(Ref(X(1), tableK, 1 + dx, "X1"));
            var c11 = Exterior(e11, 1, 1, null); c11.Temporary.Add(Ref(X(4), tableK, 4 + dx, "X4")); c11.Temporary.Add(Ref(X(5), tableK, 5 + dx, "X5"));
            mod.Worldspaces.Add(World(wKey, null, null, c00, c11));
            return mod;
        }
        SkyrimMod PersistentPair(string s, float dx)
        {
            var mod = Plugin(s);
            var top = new Cell(topK, release) { Flags = default }; top.Persistent.Add(Ref(rp2, tableK, 20 + dx, "RP2"));
            mod.Worldspaces.Add(World(wKey, null, top));
            return mod;
        }
        var g = ExteriorPair("G", 0.5f); var h = ExteriorPair("H", 0.25f);
        var ii = DiagonalPair("I", 0.5f); var jj = DiagonalPair("J", 0.25f);
        var k = Plugin("K"); { var c = Interior(ci1, null); c.Temporary.Add(Ref(R(12), tableK, 1212, "R12")); FileInterior(k, c); }
        var l = PersistentPair("L", 0.5f); var nn = PersistentPair("N", 0.25f);
        var oKey = new ModKey("PlaceO", ModType.Master);
        var o = new SkyrimMod(oKey, release); var oCell = new FormKey(oKey, 0x800);
        { var c = Interior(oCell, "PlaceOwnCell"); c.Temporary.Add(Ref(R(12), tableK, 1213, "R12")); FileInterior(o, c); }

        var names = new List<string> { "PlaceMaster.esm" };
        var paths = new List<string> { mPath };
        foreach (var mod in new[] { o, a, b, cc, d, e, f, g, h, ii, jj, k, l, nn }) { names.Add(mod.ModKey.FileName.String); paths.Add(Write(mod, mOv)); }
        var baseline = names.ToList();

        // ---- read the order back the way orderprobe does ----------------------------------------------------
        var headers = new Dictionary<string, PluginHeader?>(StringComparer.OrdinalIgnoreCase);
        foreach (var n in names) headers[n] = PluginHeader.TryRead(Path.Combine(root, n));
        Check("place/fixture: the fifteen plugins read back with their headers — the master and PlaceO.esm are master-flagged, every override declares the master",
              names.Count == 15 && headers.Values.All(x => x is not null) && headers["PlaceMaster.esm"]!.MasterFlag && headers["PlaceO.esm"]!.MasterFlag
              && names.Skip(1).All(n => headers[n]!.Masters.Contains("PlaceMaster.esm")) && names.Skip(2).All(n => !headers[n]!.MasterFlag),
              string.Join(", ", names));

        using var resolver = LoadOrderResolver.Build(paths);
        var view = resolver.Capture();
        var contested = new HashSet<FormKey>(view.ConflictKeys());
        var excluded = new HashSet<string>(view.ExcludedPlugins.Keys, StringComparer.OrdinalIgnoreCase);
        var places = PlaceIndex.Build(paths, view.DataDir, contested, excluded);
        Check("place/fixture: the index excluded nothing and the walk opened all fifteen plugins without a failure",
              view.LoadFailures.Count == 0 && places.PluginsWalked == 15 && places.WalkFailures.Count == 0 && places.LandscapeUnreadable == 0,
              $"{places.PluginsWalked} walked, {places.WalkFailures.Count} failure(s): {string.Join(" | ", places.WalkFailures)}");

        var edges = new Dictionary<string, ContentionEdge>(StringComparer.OrdinalIgnoreCase);
        void Bump(string x, string y)
        {
            var (p, q) = string.Compare(x, y, StringComparison.OrdinalIgnoreCase) <= 0 ? (x, y) : (y, x);
            var key = $"record|{p}|{q}";
            edges[key] = edges.TryGetValue(key, out var ed) ? ed with { Count = ed.Count + 1 } : new ContentionEdge(p, q, "record", 1);
        }
        var evidence = places.Weigh(view, Bump, out int contestedRecords);
        var edgeList = edges.Values.OrderBy(x => x.A, StringComparer.OrdinalIgnoreCase).ThenBy(x => x.B, StringComparer.OrdinalIgnoreCase).ThenBy(x => x.Kind, StringComparer.Ordinal).ToList();

        // ---- P1: the place of a record ------------------------------------------------------------------------
        string P(FormKey rec) => places.PlaceOf(rec)?.Id ?? "(no place)";
        Check("place/P1: a temporary reference is placed in the interior cell that files it (R1 -> PlaceInt1)",
              P(R(1)) == ci1.ToString() && places.PlaceOf(R(1))!.Kind == "interior" && places.PlaceOf(R(1))!.Name == "PlaceInt1",
              $"{P(R(1))} {places.PlaceOf(R(1))?.Kind} {places.PlaceOf(R(1))?.Name}");
        Check("place/P1: a navmesh and a LAND are placed in their cell too (N1 -> PlaceInt1, L1 -> PlaceExt00)",
              P(n1) == ci1.ToString() && P(l1) == e00.ToString(), $"N1 {P(n1)}, L1 {P(l1)}");
        Check("place/P1: an exterior reference's cell is exterior, in its worldspace, with its XCLC grid (X3 -> (1,0) of PlaceWorld)",
              places.PlaceOf(X(3)) is { Kind: "exterior", Name: "PlaceExt10", WorldspaceName: "PlaceWorld", X: 1, Y: 0 },
              $"{places.PlaceOf(X(3))}");
        Check("place/P1: a persistent reference's cell is the worldspace's PERSISTENT cell — gridless, in PlaceWorld",
              places.PlaceOf(rp1) is { Kind: "persistent", WorldspaceName: "PlaceWorld", X: null, Y: null } && P(rp1) == topK.ToString(),
              $"{places.PlaceOf(rp1)}");
        Check("place/P1: a CELL record's place is the cell ITSELF (CELL CI1 -> CI1; CELL (0,0) -> (0,0); CELL T -> T)",
              P(ci1) == ci1.ToString() && P(e00) == e00.ToString() && P(topK) == topK.ToString()
              && places.PlaceOf(ci1)!.Kind == "interior" && places.PlaceOf(e00)!.Kind == "exterior" && places.PlaceOf(topK)!.Kind == "persistent",
              $"CI1 {P(ci1)}, E00 {P(e00)}, T {P(topK)}");
        Check("place/P1: a WRLD record's place is the worldspace itself, of kind worldspace",
              P(wKey) == wKey.ToString() && places.PlaceOf(wKey) is { Kind: "worldspace", Name: "PlaceWorld" }, $"{places.PlaceOf(wKey)}");
        Check("place/P1: an item has NO place (I1 is a MISC at the top level of every plugin that carries it)",
              places.PlaceOf(Item(1)) is null && contested.Contains(Item(1)), P(Item(1)));
        Check("place/P1: FIRST sighting wins — K files R12 under CI1, but the master filed it under CI4 first, so R12 stays in CI4",
              P(R(12)) == ci4.ToString() && places.FirstSightingOf(R(12)) == "PlaceMaster.esm", $"{P(R(12))} by {places.FirstSightingOf(R(12))}");
        // O (second in the order) and K each file R12 somewhere else: TWO disagreeing filings, ONE disputed record — the
        // count is of records, once each on the first disagreement; the note listing keeps one line per filing, in order.
        Check("place/P1: a record two later plugins file elsewhere is ONE disputed record (PlaceDisputed 1, filings 2), and both filings are listed in the plan's spelling",
              places.PlaceDisputed == 1 && places.DisputedFilings == 2 && places.DisputeNotes.Count == 2
              && places.DisputeNotes[0] == $"{R(12)}: 'PlaceMaster.esm' files it in {ci4}, 'PlaceO.esm' in PlaceOwnCell"
              && places.DisputeNotes[1] == $"{R(12)}: 'PlaceMaster.esm' files it in {ci4}, 'PlaceK.esp' in PlaceInt1"
              && P(R(12)) == ci4.ToString(),
              $"{places.PlaceDisputed} record(s), {places.DisputedFilings} filing(s): " + string.Join(" | ", places.DisputeNotes));
        Check("place/P1: the name rule, three ways — first sighting's EditorID; empty there but the first later one; never named -> the FormKey",
              places.Describe(ci1)!.Name == "PlaceInt1" && places.Describe(ci3)!.Name == "PlaceInt3Late"
              && places.Describe(ci4)!.Name == ci4.ToString() && places.Describe(topK)!.Name == topK.ToString(),
              $"CI1 {places.Describe(ci1)!.Name}; CI3 {places.Describe(ci3)!.Name} (D's 'PlaceInt3Other' came later); CI4 {places.Describe(ci4)!.Name}");
        Check("place/P1: a record no second plugin carries is not in the kept map (X6, X9, R4 — the walk keeps the contested set only)",
              places.PlaceOf(X(6)) is null && places.PlaceOf(X(9)) is null && places.PlaceOf(R(4)) is null
              && !contested.Contains(X(6)) && !contested.Contains(R(4)),
              "the contested set is the index's, unchanged; place is a second dimension on it");
        Check("place/D-1: the WRLD record and the persistent cell's own CELL record are the worldspace-wide containers; a persistent REFERENCE and an interior CELL are not",
              places.IsWorldwideContainer(wKey) && places.IsWorldwideContainer(topK)
              && !places.IsWorldwideContainer(rp1) && !places.IsWorldwideContainer(ci1) && !places.IsWorldwideContainer(e00) && !places.IsWorldwideContainer(X(1)),
              $"W {places.IsWorldwideContainer(wKey)}, T {places.IsWorldwideContainer(topK)}, RP1 {places.IsWorldwideContainer(rp1)}, CI1 {places.IsWorldwideContainer(ci1)}");

        // ---- P2: the totals and the pairs ----------------------------------------------------------------------
        // Contested records, by hand: CI1 { R1 R2 R3 CELL N1 } 5, CI2 { R7 R8 CELL } 3, CI3 { R10 CELL } 2, CI4 { R12 } 1
        // -> interior 11; (0,0) { X1 X2 CELL L1 } 4, (1,0) { X3 CELL } 2, (1,1) { X4 X5 CELL } 3, (5,5) { X7 X8 CELL } 3
        // -> exterior 12; T { RP1 RP2 CELL } persistent 3; WRLD worldspace 1; items I1..I5 without 5. Total 32.
        // Containers: the WRLD and CELL T = 2.
        Check("place/P2: the whole-order totals by kind — 11 interior, 12 exterior, 3 persistent, 1 worldspace = 27 with a place, 5 without, 2 containers",
              contestedRecords == 32 && evidence.RecordsWithPlace == 27 && evidence.RecordsInterior == 11 && evidence.RecordsExterior == 12
              && evidence.RecordsPersistent == 3 && evidence.RecordsWorldspace == 1 && evidence.RecordsWithoutPlace == 5 && evidence.PlaceDisputed == 1
              && evidence.RecordsWorldwideContainers == 2,
              $"{contestedRecords} contested: {evidence.RecordsWithPlace} placed ({evidence.RecordsInterior}/{evidence.RecordsExterior}/{evidence.RecordsPersistent}/{evidence.RecordsWorldspace}), {evidence.RecordsWithoutPlace} without, {evidence.PlaceDisputed} disputed, {evidence.RecordsWorldwideContainers} containers");
        PlacePairCounts Pair(string x, string y) => evidence.Of("Place" + x + ".esp", "Place" + y + ".esp")!;
        Check("place/P2: A|B contest 4 records, all in CI1 (R1, R2, N1 and the CELL record itself) — place 4, cell 4, no-place 0",
              Pair("A", "B") is { Place: 4, Cell: 4, NoPlace: 0 } && Pair("A", "B").ByPlace[ci1.ToString()] == 4 && Pair("A", "B").ByPlace.Count == 1,
              $"place {Pair("A", "B").Place}, cell {Pair("A", "B").Cell}, no place {Pair("A", "B").NoPlace}, at CI1 {Pair("A", "B").ByPlace.GetValueOrDefault(ci1.ToString())}");
        Check("place/P2: E|F contest 5 records and none has a place — the pair's record contention is still 5",
              Pair("E", "F") is { Place: 0, Cell: 0, NoPlace: 5 } && Pair("E", "F").ByPlace.Count == 0
              && edgeList.Single(x => x.A == "PlaceE.esp" && x.B == "PlaceF.esp").Count == 5,
              $"place {Pair("E", "F").Place}, no place {Pair("E", "F").NoPlace}");
        Check("place/P2: G|H contest 12 with a place: (0,0) 4, (1,0) 2, (5,5) 3, T 2, WRLD 1 — and 10 in a CELL (the WRLD and CELL T excluded)",
              Pair("G", "H") is { Place: 12, Cell: 10, NoPlace: 0 }
              && Pair("G", "H").ByPlace[e00.ToString()] == 4 && Pair("G", "H").ByPlace[e10.ToString()] == 2 && Pair("G", "H").ByPlace[e55.ToString()] == 3
              && Pair("G", "H").ByPlace[topK.ToString()] == 2 && Pair("G", "H").ByPlace[wKey.ToString()] == 1,
              $"place {Pair("G", "H").Place}, cell {Pair("G", "H").Cell}: " + string.Join(", ", Pair("G", "H").ByPlace.OrderBy(kv => kv.Key, StringComparer.Ordinal).Select(kv => $"{kv.Key}={kv.Value}")));
        Check("place/D-1: L|N share one persistent REFERENCE (RP2) plus the two containers — place 3, cell 1",
              Pair("L", "N") is { Place: 3, Cell: 1, NoPlace: 0 }, $"place {Pair("L", "N").Place}, cell {Pair("L", "N").Cell}");
        Check("place/D-1: L|G share ONLY the WRLD record and the persistent cell's CELL record — place 2, cell 0; L|I only the WRLD — place 1, cell 0",
              Pair("L", "G") is { Place: 2, Cell: 0, NoPlace: 0 } && Pair("L", "I") is { Place: 1, Cell: 0, NoPlace: 0 },
              $"L|G place {Pair("L", "G").Place} cell {Pair("L", "G").Cell}; L|I place {Pair("L", "I").Place} cell {Pair("L", "I").Cell}");
        Check("place/P2: place + no-place is the pair's record contention, and cell never exceeds place, for every pair the evidence knows",
              edgeList.All(x => { var pp = evidence.Of(x.A, x.B); return pp is not null && pp.Place + pp.NoPlace == x.Count && pp.Cell <= pp.Place; }),
              $"{edgeList.Count} record edge(s) checked");

        // ---- the search with the evidence ------------------------------------------------------------------------
        var rules = OrderRuleSet.Build(baseline, headers, Array.Empty<string>(), Array.Empty<OrderRule>());
        var report = OrderSearch.Run(rules, edgeList, 20, places: evidence);
        Check("place/search: 35 pairs supplied, the 14 with the master and O|K pruned as order-fixed, 20 movable pairs walked to completion; the budget of 20 skips composition",
              report.Search.PairsSupplied == 35 && report.Search.OrderFixedPairs == 15 && report.Search.WeightedPairs == 20
              && report.Search.PairsEnumerated == 20 && report.Emitted == 20 && report.EnumerationCutShort,
              $"{report.Search.PairsSupplied} supplied, {report.Search.OrderFixedPairs} fixed, {report.Search.PairsEnumerated} of {report.Search.WeightedPairs} enumerated, {report.Emitted} emitted");
        OrderCandidate? Single(string x, string y) => report.Candidates.FirstOrDefault(c => c.Moves.Count == 1 && PairIs(c.Moves[0], "Place" + x + ".esp", "Place" + y + ".esp"));
        int Rank(string x, string y) => report.Candidates.ToList().FindIndex(c => c.Moves.Count == 1 && PairIs(c.Moves[0], "Place" + x + ".esp", "Place" + y + ".esp"));

        // ---- P3: blocks ----------------------------------------------------------------------------------------
        Check("place/P3: A|B — one interior block, spelled '<cell name> — <count> record(s)'",
              Single("A", "B") is { PlaceContention: 4, CellContention: 4, NoPlaceContention: 0 } && Single("A", "B")!.Blocks.Count == 1
              && Single("A", "B")!.Places.SequenceEqual(new[] { "PlaceInt1 — 4 record(s)" }),
              string.Join(" | ", Single("A", "B")?.Places ?? Array.Empty<string>()));
        Check("place/P3: C|D — two interior cells, two blocks, heaviest first, the late-named cell by its late name",
              Single("C", "D") is { PlaceContention: 5, CellContention: 5 } && Single("C", "D")!.Places.SequenceEqual(new[] { "PlaceInt2 — 3 record(s)", "PlaceInt3Late — 2 record(s)" }),
              string.Join(" | ", Single("C", "D")?.Places ?? Array.Empty<string>()));
        Check("place/P3: G|H — (0,0) and (1,0) JOIN into one block named by the heavier cell, (5,5) stays apart, then T and the WRLD",
              Single("G", "H") is { PlaceContention: 12, CellContention: 10, NoPlaceContention: 0 } && Single("G", "H")!.Blocks.Count == 4
              && Single("G", "H")!.Places.SequenceEqual(new[]
              {
                  "PlaceExt00 + 1 more cell(s) [PlaceWorld (0..1, 0..0)] — 6 record(s)",
                  "PlaceExt55 [PlaceWorld (5,5)] — 3 record(s)",
                  $"{topK} — 2 record(s)",
                  "PlaceWorld (worldspace record) — 1 record(s)",
              }),
              string.Join(" | ", Single("G", "H")?.Places ?? Array.Empty<string>()));
        Check("place/P3: I|J — (0,0) and (1,1) are DIAGONAL, so they are two blocks, not one; the WRLD is a third",
              Single("I", "J") is { PlaceContention: 6, CellContention: 5 } && Single("I", "J")!.Blocks.Count == 3
              && Single("I", "J")!.Places.SequenceEqual(new[]
              {
                  "PlaceExt11 [PlaceWorld (1,1)] — 3 record(s)",
                  "PlaceExt00 [PlaceWorld (0,0)] — 2 record(s)",
                  "PlaceWorld (worldspace record) — 1 record(s)",
              }),
              string.Join(" | ", Single("I", "J")?.Places ?? Array.Empty<string>()));
        Check("place/P3: G|I — X1, the CELL record of (0,0) and the WRLD: an exterior block of one cell and a worldspace block",
              Single("G", "I") is { PlaceContention: 3, CellContention: 2, NoPlaceContention: 0 }
              && Single("G", "I")!.Places.SequenceEqual(new[] { "PlaceExt00 [PlaceWorld (0,0)] — 2 record(s)", "PlaceWorld (worldspace record) — 1 record(s)" }),
              string.Join(" | ", Single("G", "I")?.Places ?? Array.Empty<string>()));
        Check("place/P3: K|A — the CELL record alone (K files a stray reference under CI1, A files three of its own)",
              Single("K", "A") is { PlaceContention: 1, CellContention: 1, NoPlaceContention: 0 } && Single("K", "A")!.Places.SequenceEqual(new[] { "PlaceInt1 — 1 record(s)" }),
              string.Join(" | ", Single("K", "A")?.Places ?? Array.Empty<string>()));
        Check("place/P3: L|N — the persistent cell (RP2 + its CELL record) as a block, and the WRLD; the containers are still NAMED, they just do not rank",
              Single("L", "N") is { PlaceContention: 3, CellContention: 1 }
              && Single("L", "N")!.Places.SequenceEqual(new[] { $"{topK} — 2 record(s)", "PlaceWorld (worldspace record) — 1 record(s)" }),
              string.Join(" | ", Single("L", "N")?.Places ?? Array.Empty<string>()));
        Check("place/P3: E|F — no block, no place line; 5 no-place records, still a candidate",
              Single("E", "F") is { PlaceContention: 0, CellContention: 0, NoPlaceContention: 5, Contention: 5 } && Single("E", "F")!.Blocks.Count == 0 && Single("E", "F")!.Places.Count == 0,
              $"{Single("E", "F")?.PlaceContention}/{Single("E", "F")?.NoPlaceContention}/{Single("E", "F")?.Contention}");

        // The adjacency rule on its own, with hand-placed grids, so a failure names the rule rather than a fixture number.
        var synth = new Dictionary<string, PlaceDescriptor>
        {
            ["a"] = new("a", "exterior", "A", "w", "W", 0, 0),
            ["b"] = new("b", "exterior", "B", "w", "W", 1, 1),
            ["c"] = new("c", "exterior", "C", "w", "W", 1, 0),
            ["d"] = new("d", "exterior", "D", "w", "W", 0, -1),
            ["e"] = new("e", "exterior", "E", "v", "V", 0, 1),
            ["g"] = new("g", "exterior", "G", "w", "W", null, null),
        };
        IReadOnlyList<PlaceBlock> Blocks(params (string, int)[] cells) =>
            OrderSearch.PlaceBlocksOf(cells.ToDictionary(x => x.Item1, x => x.Item2, StringComparer.Ordinal), id => synth.GetValueOrDefault(id));
        Check("place/P3-rule: a diagonal neighbour is NOT adjacent — (0,0) and (1,1) make two blocks",
              Blocks(("a", 1), ("b", 1)).Count == 2, string.Join(" | ", Blocks(("a", 1), ("b", 1)).Select(x => x.Spelling)));
        // Three cells at 1 each: the tie goes to the lowest x (A and D at 0), then the lowest y — D at -1 names the block.
        Check("place/P3-rule: an x-neighbour and a y-neighbour ARE adjacent — (0,0),(1,0),(0,-1) make one block of three, named by (0,-1) on the tie",
              Blocks(("a", 1), ("c", 1), ("d", 1)).Count == 1 && Blocks(("a", 1), ("c", 1), ("d", 1))[0].Cells == 3
              && Blocks(("a", 1), ("c", 1), ("d", 1))[0].Spelling == "D + 2 more cell(s) [W (0..1, -1..0)] — 3 record(s)",
              Blocks(("a", 1), ("c", 1), ("d", 1))[0].Spelling);
        Check("place/P3-rule: adjacency needs the SAME worldspace — (0,0) of W and (0,1) of V stay apart",
              Blocks(("a", 1), ("e", 1)).Count == 2, string.Join(" | ", Blocks(("a", 1), ("e", 1)).Select(x => x.Spelling)));
        Check("place/P3-rule: a block is named by its heaviest cell; ties go to the lowest x, then the lowest y",
              Blocks(("a", 1), ("c", 5))[0].Name == "C" && Blocks(("a", 2), ("c", 2))[0].Name == "A" && Blocks(("a", 2), ("d", 2))[0].Name == "D",
              $"{Blocks(("a", 1), ("c", 5))[0].Name} / {Blocks(("a", 2), ("c", 2))[0].Name} / {Blocks(("a", 2), ("d", 2))[0].Name}");
        Check("place/P3-rule: a gridless exterior cell is a block of its own, spelled (?,?)",
              Blocks(("a", 1), ("g", 1)).Count == 2 && Blocks(("a", 1), ("g", 1)).Any(x => x.Spelling == "G [W (?,?)] — 1 record(s)"),
              string.Join(" | ", Blocks(("a", 1), ("g", 1)).Select(x => x.Spelling)));
        var many = Enumerable.Range(1, 15).Select(i => new PlaceBlock($"B{i:00}", $"B{i:00} — {100 - i} record(s)", 100 - i, 1)).ToList();
        var listing = OrderSearch.PlaceListing(many);
        // Blocks 13, 14 and 15 weigh 87, 86 and 85: the rest line counts 258 records.
        Check("place/P3: a candidate names its first 12 blocks and counts the rest — 15 blocks give 12 lines and 'and 3 more block(s), 258 record(s)'",
              listing.Count == 13 && listing[0] == "B01 — 99 record(s)" && listing[11] == "B12 — 88 record(s)" && listing[12] == "and 3 more block(s), 258 record(s)",
              listing[^1]);

        // ---- P4 (D-1): the ranking ---------------------------------------------------------------------------------
        // By hand: CELL contention descending, then total, then displaced (adjacent pairs cost 2; I before G costs 3,
        // J before G 4, J before H 3; L before H 5, L before G 6, N before H 6, N before G 7; L before J 3, L before I 4,
        // N before J 4, N before I 5; K before B 10, K before A 11), then the sort key (I|G before J|H; L|G before N|H;
        // L|I before N|J). Cell: G|H 10; I|J 5 and C|D 5 (I|J's total 6 wins); A|B 4; the four 2s; L|N, K|B, K|A at 1
        // (L|N's total 3 first); then the nine with none, by total: E|F 5, the four container-only pairs at 2, the
        // four WRLD-only pairs at 1.
        var expectedP4 = new[]
        {
            ("G", "H"), ("I", "J"), ("C", "D"), ("A", "B"), ("H", "I"), ("G", "I"), ("H", "J"), ("G", "J"), ("L", "N"), ("K", "B"), ("K", "A"),
            ("E", "F"), ("L", "H"), ("L", "G"), ("N", "H"), ("N", "G"), ("L", "J"), ("L", "I"), ("N", "J"), ("N", "I"),
        };
        var singlesInOrder = report.Candidates.Where(c => c.Moves.Count == 1).ToList();
        string Show(IEnumerable<OrderCandidate> cs) => string.Join(" > ", cs.Select(c => $"{c.Moves[0].Moved[5..^4]}|{c.Moves[0].Reference[5..^4]} {c.CellContention}/{c.PlaceContention}/{c.Contention}/{c.Displaced}"));
        Check("place/P4: the twenty single-move candidates rank by CELL contention, then total, then displaced, then the sort key — the hand order",
              singlesInOrder.Count == 20 && expectedP4.Select((p, i) => PairIs(singlesInOrder[i].Moves[0], "Place" + p.Item1 + ".esp", "Place" + p.Item2 + ".esp")).All(ok => ok),
              Show(singlesInOrder));
        Check("place/P4: the item-only pair with MORE records (E|F, 5) ranks BELOW the same-cell pair with fewer (A|B, 4); its total is still reported",
              Rank("E", "F") > Rank("A", "B") && Single("E", "F")!.Contention == 5 && Single("A", "B")!.Contention == 4,
              $"E|F at {Rank("E", "F")} (contention {Single("E", "F")!.Contention}), A|B at {Rank("A", "B")} (contention {Single("A", "B")!.Contention})");
        Check("place/D-1: a pair sharing only the WRLD and the persistent cell's CELL record (L|G, place 2, cell 0) ranks BELOW the item-only pair with more total (E|F, 5) — containers do not rank",
              Rank("L", "G") > Rank("E", "F") && Single("L", "G") is { PlaceContention: 2, CellContention: 0, Contention: 2 },
              $"L|G at {Rank("L", "G")} ({Single("L", "G")!.CellContention}/{Single("L", "G")!.PlaceContention}/{Single("L", "G")!.Contention}), E|F at {Rank("E", "F")}");
        Check("place/D-1: a pair sharing one persistent REFERENCE (L|N, cell 1) ranks ABOVE the no-cell pairs and below every pair with more cell contention",
              Single("L", "N") is { CellContention: 1, PlaceContention: 3 } && Rank("L", "N") < Rank("E", "F") && Rank("L", "N") > Rank("G", "J"),
              $"L|N at {Rank("L", "N")}");
        Check("place/P4: the no-cell candidates are emitted and counted — GeneratedWithoutCell 9, GeneratedWithoutPlace 1; weighted pairs 19 with a place (11 with a cell), 1 without",
              Single("E", "F") is not null && Single("E", "F")!.Name.StartsWith("12-") && Single("N", "I")!.Name.StartsWith("20-")
              && report.Search.GeneratedWithoutCell == 9 && report.Search.GeneratedWithoutPlace == 1
              && report.Search.WeightedPairsWithPlace == 19 && report.Search.WeightedPairsWithCell == 11 && report.Search.WeightedPairsWithoutPlace == 1
              && report.PlaceEvidenceSupplied,
              $"{Single("E", "F")?.Name}, {Single("N", "I")?.Name}; generated without cell {report.Search.GeneratedWithoutCell}, without place {report.Search.GeneratedWithoutPlace}; weighted {report.Search.WeightedPairsWithPlace}/{report.Search.WeightedPairsWithCell}/{report.Search.WeightedPairsWithoutPlace}");
        Check("place/P4: the manifest's Search section carries the totals the evidence gave it",
              report.Search is { RecordsWithPlace: 27, RecordsInterior: 11, RecordsExterior: 12, RecordsPersistent: 3, RecordsWorldspace: 1, RecordsWithoutPlace: 5, PlaceDisputed: 1, RecordsWorldwideContainers: 2 },
              $"{report.Search.RecordsWithPlace}/{report.Search.RecordsInterior}/{report.Search.RecordsExterior}/{report.Search.RecordsPersistent}/{report.Search.RecordsWorldspace}/{report.Search.RecordsWithoutPlace}/{report.Search.PlaceDisputed}/{report.Search.RecordsWorldwideContainers}");
        var roomy = OrderSearch.Run(rules, edgeList, 30, places: evidence);
        var composed = roomy.Candidates.FirstOrDefault(c => c.Moves.Count == 2
            && PairIs(c.Moves[0], "PlaceG.esp", "PlaceH.esp") && PairIs(c.Moves[1], "PlaceI.esp", "PlaceJ.esp"));
        Check("place/P4: a composed candidate sums its two pairs' place (12 + 6) and cell (10 + 5) contention and lists the first pair's blocks then the second's (4 + 3) — first of its phase",
              composed is { PlaceContention: 18, CellContention: 15, NoPlaceContention: 0, Contention: 18 } && composed.Blocks.Count == 7
              && composed.Places[0] == Single("G", "H")!.Places[0] && composed.Places[4] == Single("I", "J")!.Places[0]
              && roomy.Candidates.First(c => c.Moves.Count == 2) == composed,
              composed is null ? "(not emitted)" : $"{composed.CellContention}/{composed.PlaceContention} over {composed.Blocks.Count} block(s): {string.Join(" | ", composed.Places.Take(5))}");

        // ---- P4: the places table (D-2: cell contention) --------------------------------------------------------
        // By hand, over the 20 movable pairs. The WRLD is a container, so it has NO row. (0,0) is met by G|H 4, I|J 2,
        // G|I 2, G|J 2, H|I 2, H|J 2 -> 6 pairs, 14; CI1 by A|B 4, K|A 1, K|B 1 -> 3, 6; (1,1) 3, (5,5) 3 and CI2 3 by one
        // pair each; the persistent cell T by the PLACED records inside it with its own CELL record excluded — G|H's RP1
        // and L|N's RP2 — so L|G, L|H, N|G and N|H, which share only that CELL record, do not count there: 2 pairs, 2;
        // (1,0) 2 and CI3 2 by one pair each; CI4 by NO movable pair (only the master and K). Eight rows.
        var t = report.Places;
        Check("place/table: eight rows — no row for the WRLD; (0,0) first with 6 pairs and 14 pair-record incidences, then CI1 (3, 6)",
              t.Count == 8 && !t.Any(x => x.Kind == "worldspace") && !t.Any(x => x.Id == wKey.ToString())
              && t[0] is { Name: "PlaceExt00", Kind: "exterior", Worldspace: "PlaceWorld", Grid: "(0,0)", Pairs: 6, Contention: 14 }
              && t[1] is { Name: "PlaceInt1", Kind: "interior", Worldspace: null, Grid: null, Pairs: 3, Contention: 6 },
              string.Join(" | ", t.Select(x => $"{x.Name} {x.Pairs}/{x.Contention}")));
        Check("place/table: the persistent cell's row counts the references inside it and not its CELL record — RP1 (G|H) and RP2 (L|N): 2 pairs, 2; the four container-only pairs are not counted",
              t.Count == 8 && t[5] is { Kind: "persistent", Worldspace: "PlaceWorld", Grid: null, Pairs: 2, Contention: 2 } && t[5].Name == topK.ToString()
              && t[5].Plugins.SequenceEqual(new[] { "PlaceG.esp", "PlaceH.esp", "PlaceL.esp", "PlaceN.esp" }),
              t.Count < 6 ? $"{t.Count} row(s)" : $"{t[5].Name} {t[5].Pairs}/{t[5].Contention}: {string.Join(", ", t[5].Plugins)}");
        Check("place/table: ties sort by contention, then pairs, then name (ordinal) — PlaceExt11, PlaceExt55, PlaceInt2 at 3; then T (2 pairs), PlaceExt10, PlaceInt3Late at 2",
              t.Count == 8 && t.Skip(2).Select(x => x.Name).SequenceEqual(new[] { "PlaceExt11", "PlaceExt55", "PlaceInt2", topK.ToString(), "PlaceExt10", "PlaceInt3Late" }),
              string.Join(" | ", t.Skip(2).Select(x => $"{x.Name} {x.Pairs}/{x.Contention}")));
        Check("place/table: a place only a PRUNED pair contests (CI4, the master against K) is not in the table; plugins are the union of the meeting pairs",
              t.Count == 8 && !t.Any(x => x.Id == ci4.ToString())
              && t[0].Plugins.SequenceEqual(new[] { "PlaceG.esp", "PlaceH.esp", "PlaceI.esp", "PlaceJ.esp" })
              && t[1].Plugins.SequenceEqual(new[] { "PlaceA.esp", "PlaceB.esp", "PlaceK.esp" }),
              t.Count < 2 ? $"{t.Count} row(s)" : $"(0,0): {string.Join(", ", t[0].Plugins)}; CI1: {string.Join(", ", t[1].Plugins)}");
        Check("place/table: the table's rule changes no block — L|N still names the persistent cell WITH its CELL record (2) and the WRLD (1), and G|H's T block is still 2",
              Single("L", "N")!.Places.SequenceEqual(new[] { $"{topK} — 2 record(s)", "PlaceWorld (worldspace record) — 1 record(s)" })
              && Single("G", "H")!.Places[2] == $"{topK} — 2 record(s)" && Pair("L", "N").ByPlace[topK.ToString()] == 2,
              string.Join(" | ", Single("L", "N")!.Places));

        // ---- null evidence ---------------------------------------------------------------------------------------
        // By hand, Q38's order: total descending, then displaced, then the sort key — C|D ("PlaceD.esp|before|PlaceC.esp")
        // precedes E|F ("PlaceF.esp|before|PlaceE.esp") at 5, E|F precedes A|B at 4, and at 3 the displaced-2 pairs
        // H|I and L|N come before G|I, H|J (3) and G|J (4); K|B and K|A at 1 come last.
        var without = OrderSearch.Run(rules, edgeList, 20);
        var expectedQ38 = new[]
        {
            ("G", "H"), ("I", "J"), ("C", "D"), ("E", "F"), ("A", "B"), ("H", "I"), ("L", "N"), ("G", "I"), ("H", "J"), ("G", "J"),
            ("L", "H"), ("L", "G"), ("N", "H"), ("N", "G"), ("L", "J"), ("L", "I"), ("N", "J"), ("N", "I"), ("K", "B"), ("K", "A"),
        };
        var withoutSingles = without.Candidates.Where(c => c.Moves.Count == 1).ToList();
        Check("place/null: with no evidence the ranking is the old one — E|F at 5 ahead of A|B at 4, K|B and K|A last — and every place term is 0",
              withoutSingles.Count == 20 && expectedQ38.Select((p, i) => PairIs(withoutSingles[i].Moves[0], "Place" + p.Item1 + ".esp", "Place" + p.Item2 + ".esp")).All(ok => ok)
              && without.Candidates.All(c => c.PlaceContention == 0 && c.CellContention == 0 && c.NoPlaceContention == 0 && c.Blocks.Count == 0 && c.Places.Count == 0),
              Show(withoutSingles));
        // With no evidence the record totals are 0 (nothing was counted), and the search's own counts read literally:
        // no pair has a place or a cell, so every weighted pair is "without" and every generated candidate is "without".
        Check("place/null: no evidence means no heading, an empty table and zero record totals — every weighted pair and generated candidate reads as without",
              !without.PlaceEvidenceSupplied && without.Places.Count == 0
              && without.Search is { RecordsWithPlace: 0, RecordsWithoutPlace: 0, PlaceDisputed: 0, RecordsWorldwideContainers: 0, WeightedPairsWithPlace: 0, WeightedPairsWithCell: 0 }
              && without.Search.WeightedPairsWithoutPlace == without.Search.WeightedPairs && without.Search.WeightedPairs == 20
              && without.Search.GeneratedWithoutPlace == 20 && without.Search.GeneratedWithoutCell == 20,
              $"table {without.Places.Count}, weighted without {without.Search.WeightedPairsWithoutPlace} of {without.Search.WeightedPairs}, generated without {without.Search.GeneratedWithoutPlace}");
        Check("place/null: the same candidate SET either way — place re-ranks, it never adds or drops a candidate",
              without.Candidates.Select(c => c.SortKey).OrderBy(s => s, StringComparer.Ordinal)
                     .SequenceEqual(report.Candidates.Select(c => c.SortKey).OrderBy(s => s, StringComparer.Ordinal), StringComparer.Ordinal),
              $"{without.Candidates.Count} vs {report.Candidates.Count}");

        // ---- the manifest on the bytes -----------------------------------------------------------------------------
        var m1 = Manifest(root, baseline, headers, report);
        var m2 = Manifest(root, baseline, headers, OrderSearch.Run(rules, edgeList, 20, places: evidence));
        Check("place/manifest: two runs over the same evidence serialize byte-identically", m1 == m2 && m1.Length > 0, $"{m1.Length} bytes");
        string detail = "(unreadable)";
        bool ok = false;
        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(m1);
            var sec = doc.RootElement.GetProperty("Search");
            var order = sec.EnumerateObject().Select(p => p.Name).ToList();
            var cand = doc.RootElement.GetProperty("Candidates").EnumerateArray().Skip(1).First();
            var table = doc.RootElement.GetProperty("Places").EnumerateArray().ToList();
            ok = sec.GetProperty("RecordsWithPlace").GetInt32() == 27 && sec.GetProperty("RecordsWithoutPlace").GetInt32() == 5
              && sec.GetProperty("PlaceDisputed").GetInt32() == 1 && sec.GetProperty("RecordsWorldwideContainers").GetInt32() == 2
              && sec.GetProperty("GeneratedWithoutPlace").GetInt32() == 1 && sec.GetProperty("GeneratedWithoutCell").GetInt32() == 9
              && sec.GetProperty("WeightedPairsWithCell").GetInt32() == 11
              && order.IndexOf("GeneratedWithoutCell") < order.IndexOf("OrderFixedByRule") && order.IndexOf("RecordsWithPlace") < order.IndexOf("HeaviestOrderFixed")
              && cand.GetProperty("PlaceContention").GetInt32() == 12 && cand.GetProperty("CellContention").GetInt32() == 10
              && cand.GetProperty("NoPlaceContention").GetInt32() == 0 && cand.GetProperty("PlaceBlocks").GetInt32() == 4
              && cand.GetProperty("Places")[0].GetString() == "PlaceExt00 + 1 more cell(s) [PlaceWorld (0..1, 0..0)] — 6 record(s)"
              && table.Count == 8 && table[0].GetProperty("Name").GetString() == "PlaceExt00" && table[0].GetProperty("Pairs").GetInt32() == 6
              && table[0].GetProperty("Contention").GetInt64() == 14 && table[0].GetProperty("Grid").GetString() == "(0,0)"
              && table[5].GetProperty("Kind").GetString() == "persistent" && table[5].GetProperty("Pairs").GetInt32() == 2
              && table[5].GetProperty("Contention").GetInt64() == 2;
            detail = $"Search keys: {string.Join(",", order.Skip(order.IndexOf("RecordsWithPlace")))}; candidate 1 cell {cand.GetProperty("CellContention").GetInt32()} of place {cand.GetProperty("PlaceContention").GetInt32()}";
        }
        catch (Exception ex) { detail = $"threw: {ex.Message}"; }
        Check("place/manifest: the new fields reach the JSON — the Search counts BEFORE the two lists, the candidate's cell/place/places, the top of the table",
              ok, detail);

        // A manifest built with NO evidence must say so on its own bytes: its place and cell counts read 0 and "all",
        // which a reader who never saw the console would take for a measurement.
        var mNone = Manifest(root, baseline, headers, without);
        string noneDetail = "(unreadable)";
        bool noneOk = false;
        try
        {
            using var docNone = System.Text.Json.JsonDocument.Parse(mNone);
            using var docWith = System.Text.Json.JsonDocument.Parse(m1);
            var secNone = docNone.RootElement.GetProperty("Search");
            var orderNone = secNone.EnumerateObject().Select(p => p.Name).ToList();
            var notesNone = docNone.RootElement.GetProperty("Notes").EnumerateArray().Select(n => n.GetString()).ToList();
            var notesWith = docWith.RootElement.GetProperty("Notes").EnumerateArray().Select(n => n.GetString()).ToList();
            noneOk = !secNone.GetProperty("PlaceEvidenceSupplied").GetBoolean()
                  && orderNone.IndexOf("PlaceEvidenceSupplied") < orderNone.IndexOf("OrderFixedByRule")
                  && notesNone.Contains(SearchReport.NoPlaceEvidence)
                  && docWith.RootElement.GetProperty("Search").GetProperty("PlaceEvidenceSupplied").GetBoolean()
                  && !notesWith.Contains(SearchReport.NoPlaceEvidence)
                  && SearchReport.NoPlaceEvidence.Contains("UNMEASURED");
            noneDetail = $"without: supplied {secNone.GetProperty("PlaceEvidenceSupplied").GetBoolean()}, note present {notesNone.Contains(SearchReport.NoPlaceEvidence)}; with: supplied {docWith.RootElement.GetProperty("Search").GetProperty("PlaceEvidenceSupplied").GetBoolean()}";
        }
        catch (Exception ex) { noneDetail = $"threw: {ex.Message}"; }
        Check("place/manifest: a manifest built without evidence says so on the bytes — PlaceEvidenceSupplied false before the two lists and the 'unmeasured' note; with evidence true and no note",
              noneOk, noneDetail);
    }

    static bool PairIs(CandidateMove m, string x, string y) =>
        (m.Moved.Equals(x, StringComparison.OrdinalIgnoreCase) && m.Reference.Equals(y, StringComparison.OrdinalIgnoreCase))
     || (m.Moved.Equals(y, StringComparison.OrdinalIgnoreCase) && m.Reference.Equals(x, StringComparison.OrdinalIgnoreCase));

    /// <summary>The manifest orderprobe writes, from the same parts and with the same place fields, so the proof is on
    /// the bytes a consumer reads.</summary>
    static string Manifest(string root, IReadOnlyList<string> baseline, Dictionary<string, PluginHeader?> headers, SearchReport report)
    {
        var rules = OrderRuleSet.Build(baseline, headers, Array.Empty<string>(), Array.Empty<OrderRule>());
        var plugins = baseline.Select(n => new ConfigPlugin(n, Path.Combine(root, n), ConfigurationIdentity.Sha256File(Path.Combine(root, n)),
                                                            new FileInfo(Path.Combine(root, n)).Length, "mod",
                                                            headers[n]!.MasterFlag, headers[n]!.LightFlag, headers[n]!.Masters)).ToList();
        var identity = ConfigurationIdentity.Compute("profile:place", plugins, new[] { new ConfigProvider("Data", "data") },
                                                     Array.Empty<ConfigOverlay>(), "load-time", "baseline");
        var manifest = new OrderManifest(
            ConfigurationIdentity.CurrentSchema, "orderprobe", root, "place", identity, rules.Fixed,
            Array.Empty<ManifestRule>(), rules.Notes, Array.Empty<ManifestContradiction>(), Array.Empty<ManifestContradiction>(),
            report.Groups.Select(g => new ManifestGroup(g.Key, g.Members, g.Contention,
                g.Edges.Select(e => $"{e.A} | {e.B} | {e.Kind} | {e.Count}").ToList())).ToList(),
            report.Candidates.Select(c => new ManifestCandidate(c.Name, $"candidate-{c.Name}.json", c.Changes, c.Displaced,
                c.Contention, c.Moves.Count, c.Phase, c.Groups, c.Legality.Legal,
                c.Legality.Violations.Select(v => v.ToString()).ToList(),
                c.SatisfiesAdvice.Select(x => x.Full).ToList(),
                c.ViolatesAdvice.Select(x => x.Full).ToList(),
                c.PlaceContention, c.CellContention, c.NoPlaceContention, c.Blocks.Count, c.Places)).ToList(),
            new ManifestBudget(20, report.Result.ToString(), report.Emitted, report.GeneratedNotEmitted,
                               report.Unexplored, report.EnumerationCutShort, report.Outcome),
            report.Search, report.RejectedMoves, report.Notes, new[] { "synthetic fixture" },
            report.Places.Take(50).ToList());
        return manifest.Serialize();
    }
}
