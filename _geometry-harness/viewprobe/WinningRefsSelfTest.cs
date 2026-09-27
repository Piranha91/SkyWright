using HousecarlCore;
using Mutagen.Bethesda;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Plugins.Records;
using Mutagen.Bethesda.Skyrim;
using Noggog;

namespace ViewProbe;

// --winning-refs-selftest: known answers for WinningRefWalk (WinningRefs.cs), the code the render's load-order walk
// runs. No game data. Mutagen writes a small master and override plugins into a temp folder; they are read back through
// PlacementExtract.FromMod in load order, exactly as the render reads a real order, classified with the render's own
// Classify, fed to Observe, and merged with MergePersistent. Each case asserts which references the winning state
// admits, at which position, and in which cell.
//
// The world: worldspace W with a persistent cell T and exterior cells E00 (0,0), E10 (1,0), E50 (5,0), E55 (5,5);
// interior cells I1 and I2; a second worldspace W2 with a persistent cell and a cell at the same grid (0,0), so the
// same XY lies in two worlds. One modelled static base. Every position is the fixture's own.
//
// Exit 0 when every case passes (KNOWN-GAP rows do not fail the run); 1 otherwise. VP_WINREF_CANARY=1 plants one
// deliberately wrong expectation (in C1), which must fail: a sweep whose canary passes is a broken sweep.
internal static class WinningRefsSelfTest
{
    const SkyrimRelease Rel = SkyrimRelease.SkyrimSE;
    static int _pass, _fail, _gap;

    static void Check(string name, bool ok, string detail)
    {
        if (ok) _pass++; else _fail++;
        Console.WriteLine($"   {(ok ? "PASS" : "FAIL"),-9} {name,-78} {detail}");
    }

    /// <summary>A case the tool is known not to handle yet: reported, never counted as a pass.</summary>
    static void KnownGap(string name, bool closed, string detail)
    {
        if (closed) { _fail++; Console.WriteLine($"   {"FAIL",-9} {name,-78} the known gap now passes: update the case and the README. {detail}"); return; }
        _gap++;
        Console.WriteLine($"   {"KNOWN-GAP",-9} {name,-78} {detail}");
    }

    public static int Run()
    {
        _pass = _fail = _gap = 0;
        // VP_WINREF_KEEP=<dir>: write the fixture plugins there and keep them, so an independent reader can be checked
        // against the same files and the same answers. The folder must be empty or absent.
        string? keep = Environment.GetEnvironmentVariable("VP_WINREF_KEEP") is { Length: > 0 } k ? Path.GetFullPath(k) : null;
        if (keep is not null && Directory.Exists(keep) && Directory.EnumerateFileSystemEntries(keep).Any())
        {
            Console.WriteLine($"winning-refs-selftest: VP_WINREF_KEEP folder '{keep}' is not empty; refusing to write into it");
            return 2;
        }
        var root = keep ?? Path.Combine(Path.GetTempPath(), "vp-winref-" + Environment.ProcessId);
        Directory.CreateDirectory(root);
        try { Body(root); }
        catch (Exception ex) { Check("fixture", false, $"threw {ex.GetType().Name}: {ex.Message}"); }
        finally { if (keep is null) { try { Directory.Delete(root, true); } catch { } } else Console.WriteLine($"   fixture kept in {keep}"); }
        Console.WriteLine($"winning-refs-selftest: {_pass} passed, {_fail} failed, {_gap} known gap(s)");
        return _fail == 0 ? 0 : 1;
    }

    sealed record Walked(Dictionary<FormKey, PlacedRef> WinRef, Dictionary<FormKey, (FormKey Ws, PlacedRef Ref)> Persistent,
                         Dictionary<FormKey, FormKey> CellOf)
    {
        public HashSet<FormKey>? TakenOut { get; set; }
    }

    static void Body(string root)
    {
        var mKey = new ModKey("WrMaster", ModType.Master);
        FormKey M(uint id) => new(mKey, id);
        var stat = M(0x801);
        var wKey = M(0x830); var topK = M(0x831);
        var e00 = M(0x840); var e10 = M(0x841); var e50 = M(0x842); var e55 = M(0x843);
        var i1 = M(0x850); var i2 = M(0x851);
        var w2Key = M(0x860); var w2Top = M(0x861); var w2e00 = M(0x862);   // a second worldspace: the same XY, another world
        FormKey R(int n) => M((uint)(0x900 + n));

        static PlacedObject Ref(FormKey key, FormKey baseKey, float x, float y)
        {
            var r = new PlacedObject(key, Rel) { Placement = new Placement { Position = new P3Float(x, y, 0), Rotation = new P3Float(0, 0, 0) } };
            r.Base.SetTo(baseKey);
            return r;
        }
        static PlacedObject DeletedRef(FormKey key) => new(key, Rel) { IsDeleted = true };
        static Cell Ext(FormKey key, int x, int y) => new(key, Rel) { Flags = default, Grid = new CellGrid { Point = new P2Int(x, y) } };
        static Cell Int(FormKey key) => new(key, Rel) { Flags = Cell.Flag.IsInteriorCell };
        static void FileInterior(SkyrimMod mod, Cell cell)
        {
            uint id = cell.FormKey.ID;
            int blockN = (int)(id % 10), subN = (int)((id / 10) % 10);
            var block = mod.Cells.Records.FirstOrDefault(b => b.BlockNumber == blockN);
            if (block is null) { block = new CellBlock { BlockNumber = blockN, GroupType = GroupTypeEnum.InteriorCellBlock }; mod.Cells.Records.Add(block); }
            var sub = block.SubBlocks.FirstOrDefault(s => s.BlockNumber == subN);
            if (sub is null) { sub = new CellSubBlock { BlockNumber = subN, GroupType = GroupTypeEnum.InteriorCellSubBlock }; block.SubBlocks.Add(sub); }
            sub.Cells.Add(cell);
        }
        static Worldspace World(FormKey wk, Cell? top, params Cell[] exteriors)
        {
            var ws = new Worldspace(wk, Rel);
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

        // ---- THE MASTER -----------------------------------------------------------------------------------------
        //   E00: R1 R5 R6 R7 R12 R13        E10: R2 R8        E55: R9        T (persistent): R3 R10 R11        I1: R4
        var master = new SkyrimMod(mKey, Rel);
        master.Statics.Add(new Static(stat, Rel) { EditorID = "WrStatic", Model = new Model { File = "Meshes\\wr\\static.nif" } });
        var mTop = new Cell(topK, Rel) { Flags = default };
        mTop.Persistent.Add(Ref(R(3), stat, 200, 200));                     // inside (0,0)
        mTop.Persistent.Add(Ref(R(10), stat, 300, 300));                    // inside (0,0)
        mTop.Persistent.Add(Ref(R(15), stat, 400, 400));                    // inside (0,0); A moves it to W2
        mTop.Persistent.Add(Ref(R(11), stat, 5 * 4096 + 100, 100));         // inside (5,0): outside the 3x3 of (0,0)
        // The merge box's edges, target E00 (0,0): ring on spans [-4096, 8192) on each axis, ring off [0, 4096).
        mTop.Persistent.Add(Ref(R(22), stat, -4096, 100));                  // ring on: on the low X edge, inside
        mTop.Persistent.Add(Ref(R(23), stat, 8192, 100));                   // ring on: on the high X edge, outside
        mTop.Persistent.Add(Ref(R(24), stat, 100, -4096));                  // ring on: on the low Y edge, inside
        mTop.Persistent.Add(Ref(R(25), stat, 100, 8192));                   // ring on: on the high Y edge, outside
        mTop.Persistent.Add(Ref(R(26), stat, 0, 50));                       // ring off: on the low X edge, inside
        mTop.Persistent.Add(Ref(R(27), stat, 4096, 50));                    // ring off: on the high X edge, outside
        var mE00 = Ext(e00, 0, 0);
        foreach (var (n, px0) in new[] { (1, 100f), (5, 110f), (6, 120f), (7, 130f), (12, 140f), (13, 150f), (14, 160f), (16, 170f), (17, 180f) }) mE00.Temporary.Add(Ref(R(n), stat, px0, 100));
        var mE10 = Ext(e10, 1, 0); mE10.Temporary.Add(Ref(R(2), stat, 4200, 100)); mE10.Temporary.Add(Ref(R(8), stat, 4210, 100));
        mE10.Persistent.Add(Ref(R(20), stat, 4250, 120));                   // persistent under an ORDINARY exterior cell (ring)
        mE10.Temporary.Add(Ref(R(29), stat, 4220, 110));                    // X deletes it in a copy of E10 holding nothing else
        var mE55 = Ext(e55, 5, 5); mE55.Temporary.Add(Ref(R(9), stat, 5 * 4096 + 100, 5 * 4096 + 100));
        var mE50 = Ext(e50, 5, 0);
        // A second CELL record at the target's own grid (0,0) in the same worldspace.
        var mE00b = Ext(M(0x844), 0, 0); mE00b.Temporary.Add(Ref(R(28), stat, 200, 300));
        master.Worldspaces.Add(World(wKey, mTop, mE00, mE10, mE50, mE55, mE00b));
        var mI1 = Int(i1); mI1.Temporary.Add(Ref(R(4), stat, 400, 0)); FileInterior(master, mI1);
        var mI2 = Int(i2); FileInterior(master, mI2);
        var mPath = Write(master);
        using var mOv = SkyrimMod.CreateFromBinaryOverlay(mPath, Rel);

        SkyrimMod Plugin(string s) => new(new ModKey(s, ModType.Plugin), Rel);

        // ---- A: the moves ---------------------------------------------------------------------------------------
        //   R1  E00 -> E50 (outside the 3x3 of E00)                      K1, K1b
        //   R2  E10 temporary -> T persistent, still inside the 3x3      K2
        //   R3  T persistent -> E55 temporary                            K3
        //   R4  I1 -> I2                                                 K4
        //   R5  E00 -> E50 (B moves it back)                             K5
        //   R6  E00 -> E00, new position                                 C1
        //   R7  E00 -> E10 (ring)                                        C2
        //   R8  E10 -> E00                                               C3
        //   R9  E55 -> E00                                               C4
        //   R12 deleted in E00, no placement data                        K6 (known gap)
        var a = Plugin("WrA");
        {
            var top = new Cell(topK, Rel) { Flags = default }; top.Persistent.Add(Ref(R(2), stat, 4300, 150));
            var c00 = Ext(e00, 0, 0);
            c00.Temporary.Add(Ref(R(6), stat, 1120, 1100)); c00.Temporary.Add(Ref(R(8), stat, 1080, 1000));
            c00.Temporary.Add(Ref(R(9), stat, 1090, 1000)); c00.Temporary.Add(DeletedRef(R(12)));
            var c10 = Ext(e10, 1, 0); c10.Temporary.Add(Ref(R(7), stat, 4400, 200)); c10.Temporary.Add(Ref(R(17), stat, 4500, 200));
            var c50 = Ext(e50, 5, 0); c50.Temporary.Add(Ref(R(1), stat, 5 * 4096 + 200, 300)); c50.Temporary.Add(Ref(R(5), stat, 5 * 4096 + 210, 300));
            var c55 = Ext(e55, 5, 5); c55.Temporary.Add(Ref(R(3), stat, 5 * 4096 + 300, 5 * 4096 + 300));
            a.Worldspaces.Add(World(wKey, top, c00, c10, c50, c55));
            var w2t = new Cell(w2Top, Rel) { Flags = default };
            w2t.Persistent.Add(Ref(R(15), stat, 400, 400)); w2t.Persistent.Add(Ref(R(16), stat, 170, 100));
            var w2c = Ext(w2e00, 0, 0); w2c.Temporary.Add(Ref(R(14), stat, 160, 100));
            a.Worldspaces.Add(World(w2Key, w2t, w2c));
            var ci2 = Int(i2); ci2.Temporary.Add(Ref(R(4), stat, 450, 50)); FileInterior(a, ci2);
        }
        // ---- B: R5 moves back into E00, at a third position ---------------------------------------------------------
        var b = Plugin("WrB");
        { var c00 = Ext(e00, 0, 0); c00.Temporary.Add(Ref(R(5), stat, 1500, 1500)); b.Worldspaces.Add(World(wKey, null, c00)); }
        // ---- X and Y: the order-permutation pair for R13 (X files it in E50, Y moves it within E00) ------------------
        var x = Plugin("WrX");
        {
            var c50 = Ext(e50, 5, 0); c50.Temporary.Add(Ref(R(13), stat, 5 * 4096 + 400, 400));
            var c10 = Ext(e10, 1, 0); c10.Temporary.Add(DeletedRef(R(29)));   // a cell copy whose ONLY content is a deletion
            x.Worldspaces.Add(World(wKey, null, c50, c10));
        }
        var y = Plugin("WrY");
        { var c00 = Ext(e00, 0, 0); c00.Temporary.Add(Ref(R(13), stat, 1800, 1800)); y.Worldspaces.Add(World(wKey, null, c00)); }

        var pa = Write(a, mOv); var pb = Write(b, mOv); var px = Write(x, mOv); var py = Write(y, mOv);

        Walked Walk(FormKey cell, bool interior, (int, int)? grid, params string[] order) => WalkR(cell, interior, grid, true, order);
        Walked WalkR(FormKey cell, bool interior, (int, int)? grid, bool ringRefs, params string[] order)
        {
            var w = new Walked(new(), new(), new());
            var walk = new WinningRefWalk(w.WinRef, w.Persistent, w.CellOf);
            foreach (var path in order)
            {
                using var mod = SkyrimMod.CreateFromBinaryOverlay(path, Rel);
                foreach (var cp in PlacementExtract.FromMod(mod, includeDeletions: true))   // as the render's walk asks
                {
                    var (isTarget, inRing) = WinningRefWalk.Classify(cp, cell, interior, ringRefs, interior ? FormKey.Null : wKey, grid);
                    walk.Observe(cp, isTarget, inRing);
                }
            }
            if (!interior && grid is { } g) walk.MergePersistent(wKey, g, ringRefs);
            // Read by name: the count and its keys belong to the fix, and this file must compile against the walk
            // before it (where the case then fails as "no taken-out keys").
            w.TakenOut = walk.GetType().GetMethod("TakenOutKeys", System.Reflection.BindingFlags.Instance
                | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Public)?.Invoke(walk, null) as HashSet<FormKey>;
            return w;
        }
        static string Pos(PlacedRef? r) => r is null ? "absent" : $"at {r.Position.X:0},{r.Position.Y:0}";
        static bool At(Walked w, FormKey k, float x, float y, FormKey cell) =>
            w.WinRef.TryGetValue(k, out var r) && MathF.Abs(r.Position.X - x) < 0.01f && MathF.Abs(r.Position.Y - y) < 0.01f
            && w.CellOf.TryGetValue(k, out var c) && c == cell;
        static string Where(Walked w, FormKey k) =>
            Pos(w.WinRef.GetValueOrDefault(k)) + (w.CellOf.TryGetValue(k, out var c) ? $" cell {c}" : " no cell")
            + (w.Persistent.TryGetValue(k, out var p) ? $"; persistent candidate {Pos(p.Ref)}" : "");

        bool canary = Environment.GetEnvironmentVariable("VP_WINREF_CANARY") == "1";
        Console.WriteLine("winning-refs-selftest: Mutagen-written plugins, read through PlacementExtract, walked by WinningRefWalk");

        var main = Walk(e00, false, (0, 0), mPath, pa, pb, px, py);
        Check("K1  moved by a later plugin to a cell outside the 3x3: not drawn in the old cell (R1)",
              !main.WinRef.ContainsKey(R(1)), Where(main, R(1)));
        var far = Walk(e50, false, (5, 0), mPath, pa, pb, px, py);
        Check("K1b the same ref, its new cell loaded: once, at the winner's position and cell",
              At(far, R(1), 5 * 4096 + 200, 300, e50), Where(far, R(1)));
        Check("K2  ring temporary made persistent (inside the 3x3) by a later plugin: at the winner's position (R2)",
              At(main, R(2), 4300, 150, topK), Where(main, R(2)));
        Check("K3  persistent made temporary in a far cell by a later plugin: not drawn (R3)",
              !main.WinRef.ContainsKey(R(3)) && !main.Persistent.ContainsKey(R(3)), Where(main, R(3)));
        var inner = Walk(i1, true, null, mPath, pa);
        Check("K4  interior ref moved to another interior: not drawn in the old one (R4)",
              !inner.WinRef.ContainsKey(R(4)), Where(inner, R(4)));
        Check("K5  moved out, then moved back by a third plugin: once, at the last plugin's position (R5)",
              At(main, R(5), 1500, 1500, e00), Where(main, R(5)));
        var perm = Walk(e00, false, (0, 0), mPath, pa, pb, py, px);
        Check("K7a order M,X,Y: Y wins, inside the loaded cell, at Y's position (R13)",
              At(main, R(13), 1800, 1800, e00), Where(main, R(13)));
        Check("K7b order M,Y,X: X wins, outside the 3x3, so not drawn (R13)",
              !perm.WinRef.ContainsKey(R(13)), Where(perm, R(13)));
        Check("C1  overridden in place: the later position wins (R6)",
              canary ? At(main, R(6), 120, 100, e00) : At(main, R(6), 1120, 1100, e00), Where(main, R(6)) + (canary ? " [CANARY: expectation planted wrong]" : ""));
        Check("C2  moved from the target into the ring: at the ring position (R7)", At(main, R(7), 4400, 200, e10), Where(main, R(7)));
        Check("C3  moved from the ring into the target: at the target position (R8)", At(main, R(8), 1080, 1000, e00), Where(main, R(8)));
        Check("C4  moved from a far cell into the target: at the target position (R9)", At(main, R(9), 1090, 1000, e00), Where(main, R(9)));
        Check("C5  a persistent ref inside the 3x3 merges once; one outside does not (R10, R11)",
              At(main, R(10), 300, 300, topK) && !main.WinRef.ContainsKey(R(11)), Where(main, R(10)) + " | " + Where(main, R(11)));
        Check("K8  temporary ref moved to ANOTHER worldspace's cell at the same XY: not drawn (R14)",
              !main.WinRef.ContainsKey(R(14)), Where(main, R(14)));
        Check("K9  persistent ref moved to another worldspace's persistent cell, same XY: not drawn (R15)",
              !main.WinRef.ContainsKey(R(15)), Where(main, R(15)));
        Check("K9b temporary ref made persistent in another worldspace, same XY: not drawn (R16)",
              !main.WinRef.ContainsKey(R(16)), Where(main, R(16)));
        var noRing = WalkR(e00, false, (0, 0), false, mPath, pa, pb, px, py);
        Check("K10 ringRefs off: moved from the target into a neighbour cell, not drawn (R17)",
              !noRing.WinRef.ContainsKey(R(17)), Where(noRing, R(17)));
        Check("C6  ringRefs off: target overrides still admitted, at the later position (R6, R8 control)",
              At(noRing, R(6), 1120, 1100, e00) && At(noRing, R(8), 1080, 1000, e00),
              Where(noRing, R(6)) + " | " + Where(noRing, R(8)));
        var expectOut = new HashSet<FormKey> { R(1), R(14), R(16) };
        Check("K11 the taken-out count names exactly the refs moved out of the loaded cells (R1, R14, R16)",
              main.TakenOut is { } to && to.SetEquals(expectOut),
              main.TakenOut is null ? "no taken-out keys" : "[" + string.Join(", ", main.TakenOut.Select(k => k.ToString()).OrderBy(x => x)) + "]");
        // The merge box's edge convention: low edge inside, high edge outside, on both axes, ring on and off.
        Check("C7  merge box, ring on: the low X and Y edges are inside, the high edges outside (R22-R27)",
              At(main, R(22), -4096, 100, topK) && !main.WinRef.ContainsKey(R(23)) && At(main, R(24), 100, -4096, topK)
              && !main.WinRef.ContainsKey(R(25)) && At(main, R(26), 0, 50, topK) && At(main, R(27), 4096, 50, topK),
              string.Join(" | ", new[] { 22, 23, 24, 25, 26, 27 }.Select(n => $"R{n} " + Where(main, R(n)))));
        Check("C7b merge box, ring off: [0,4096) (R22, R24, R26, R27)",
              !noRing.WinRef.ContainsKey(R(22)) && !noRing.WinRef.ContainsKey(R(24))
              && At(noRing, R(26), 0, 50, topK) && !noRing.WinRef.ContainsKey(R(27)),
              string.Join(" | ", new[] { 22, 24, 26, 27 }.Select(n => $"R{n} " + Where(noRing, R(n)))));
        // A persistent ref under an ordinary exterior cell is not a ring admission; it merges by position.
        Check("C8  persistent ref under an ordinary ring cell: merged by position, in its own cell (R20)",
              At(main, R(20), 4250, 120, e10) && !noRing.WinRef.ContainsKey(R(20)),
              Where(main, R(20)) + " | ring off: " + Where(noRing, R(20)));
        // Pins current behaviour, not a verified engine rule: the ring is chosen by grid, so another CELL record at the
        // target's own grid is loaded as a ring cell.
        Check("C9  a second CELL at the target's grid is loaded like a ring cell (R28; pins current behaviour)",
              At(main, R(28), 200, 300, M(0x844)) && !noRing.WinRef.ContainsKey(R(28)),
              Where(main, R(28)) + " | ring off: " + Where(noRing, R(28)));
        // K6: a later plugin deletes R12 with a record that carries no placement data. The game does not load a deleted
        // reference, so the earlier version must not be drawn, in any walk; and a deletion is not a move, so it is not in
        // the taken-out set either.
        Check("K6b deleted in a cell copy whose only content is the deletion: not drawn (R29, ring cell E10)",
              !main.WinRef.ContainsKey(R(29)) && !perm.WinRef.ContainsKey(R(29)) && !main.CellOf.ContainsKey(R(29))
              && (main.TakenOut is null || !main.TakenOut.Contains(R(29))),
              Where(main, R(29)));
        Check("K6  deleted by a later plugin with no placement data: not drawn, not a merge candidate, not 'moved' (R12)",
              !main.WinRef.ContainsKey(R(12)) && !main.Persistent.ContainsKey(R(12)) && !main.CellOf.ContainsKey(R(12))
              && !noRing.WinRef.ContainsKey(R(12)) && !perm.WinRef.ContainsKey(R(12))
              && (main.TakenOut is null || !main.TakenOut.Contains(R(12))),
              Where(main, R(12)) + (main.TakenOut?.Contains(R(12)) == true ? "; IN the taken-out set" : ""));
    }
}
