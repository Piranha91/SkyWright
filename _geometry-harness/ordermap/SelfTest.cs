using System.Text.Json;
using Mutagen.Bethesda.Plugins.Records;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Skyrim;
using Noggog;

// Hand-derived answers on a synthetic instance. Each plugin carries one way of meeting Base-owning A, so a missing edge kind fails on its own check.
// Far.esp is the planted miss for the audit: ring 2 from A, so the map must say no edge while the audit's spatial predicate must flag it.
static class OrderMapSelfTest
{
    static int pass, fail;
    static void Check(string name, bool ok, string detail = "") { if (ok) pass++; else fail++; Console.WriteLine($"   {(ok ? "PASS" : "FAIL")}  {name} {detail}"); }
    const SkyrimRelease Rel = SkyrimRelease.SkyrimSE;

    public static int Run()
    {
        var root = Path.Combine(Environment.GetEnvironmentVariable("ORDERMAP_TEST_DIR") ?? Path.GetTempPath(), "ordermap-selftest-" + Environment.ProcessId);
        Directory.CreateDirectory(root);
        try { Body(root); }
        catch (Exception ex) { fail++; Console.WriteLine($"   FAIL  crashed: {ex}"); }
        finally { if (Environment.GetEnvironmentVariable("ORDERMAP_KEEP") is null) try { Directory.Delete(root, true); } catch { } }
        Console.WriteLine($"selftest: {pass} passed, {fail} failed");
        return fail == 0 ? 0 : 1;
    }

    static Cell Ext(FormKey k, int x, int y) => new(k, Rel) { Grid = new CellGrid { Point = new P2Int(x, y) }, Flags = default };

    static void Body(string root)
    {
        string instance = Path.Combine(root, "i"), game = Path.Combine(root, "g"), mods = Path.Combine(instance, "mods"), data = Path.Combine(game, "Data"), prof = Path.Combine(instance, "profiles", "P");
        foreach (var d in new[] { prof, data, Path.Combine(instance, "overwrite") }) Directory.CreateDirectory(d);
        var baseKey = new ModKey("Skyrim", ModType.Master);
        FormKey Bk(uint id) => new(baseKey, id);
        var bm = new SkyrimMod(baseKey, Rel);
        bm.MiscItems.Add(new MiscItem(Bk(0x810), Rel) { EditorID = "M1" });
        bm.Statics.Add(new Static(Bk(0x811), Rel) { EditorID = "S1" });
        var ws = new Worldspace(Bk(0x830), Rel) { EditorID = "W" };
        bm.Worldspaces.Add(ws);
        var basePath = Path.Combine(data, "Skyrim.esm");
        bm.BeginWrite.ToPath(basePath).WithNoLoadOrder().Write();
        using var ov = SkyrimMod.CreateFromBinaryOverlay(basePath, Rel);

        string Write(SkyrimMod m, string folder)
        {
            var d = Path.Combine(mods, folder); Directory.CreateDirectory(d);
            var p = Path.Combine(d, m.ModKey.FileName);
            m.BeginWrite.ToPath(p).WithLoadOrder(ov).Write();
            return p;
        }
        SkyrimMod M(string n) => new(new ModKey(n, ModType.Plugin), Rel);
        void Place(SkyrimMod m, uint cellId, int x, int y)
        {
            var w = new Worldspace(Bk(0x830), Rel) { EditorID = "W" };
            var cell = Ext(new FormKey(m.ModKey, cellId), x, y);
            cell.Temporary.Add(new PlacedObject(new FormKey(m.ModKey, cellId + 1), Rel) { Base = new FormLinkNullable<IPlaceableObjectGetter>(Bk(0x811)), Placement = new Placement { Position = new P3Float(x * 4096 + 10, y * 4096 + 10, 0) } });
            var sub = new WorldspaceSubBlock { BlockNumberX = 0, BlockNumberY = 0 }; sub.Items.Add(cell);
            var blk = new WorldspaceBlock { BlockNumberX = 0, BlockNumberY = 0 }; blk.Items.Add(sub);
            w.SubCells.Add(blk);
            m.Worldspaces.Add(w);
        }
        var a = M("A"); a.MiscItems.Add(new MiscItem(Bk(0x810), Rel) { EditorID = "M1a" }); Place(a, 0x800, 0, 0); Write(a, "ModA");
        var rec = M("Rec"); rec.MiscItems.Add(new MiscItem(Bk(0x810), Rel) { EditorID = "M1b" }); Write(rec, "ModRec");
        var same = M("Same"); Place(same, 0x800, 0, 0); Write(same, "ModSame");
        var ring = M("Ring"); Place(ring, 0x800, 1, 0); Write(ring, "ModRing");
        var far = M("Far"); Place(far, 0x800, 2, 0); Write(far, "ModFar");
        // cell names: Same's exterior cell gets an EditorID; Lone carries one interior cell with one
        same.Worldspaces.First().SubCells[0].Items[0].Items[0].EditorID = "SameCell"; Write(same, "ModSame");
        var lone = M("Lone"); lone.MiscItems.Add(new MiscItem(new FormKey(lone.ModKey, 0x800), Rel) { EditorID = "OwnM" });
        var room = new Cell(new FormKey(lone.ModKey, 0x801), Rel) { EditorID = "LoneRoom", Flags = Cell.Flag.IsInteriorCell };
        var rb = new CellBlock { BlockNumber = 1, GroupType = GroupTypeEnum.InteriorCellBlock };
        var rs = new CellSubBlock { BlockNumber = 0, GroupType = GroupTypeEnum.InteriorCellSubBlock };
        rs.Cells.Add(room); rb.SubBlocks.Add(rs); lone.Cells.Records.Add(rb);
        Write(lone, "ModLone");
        var clean = M("Clean"); clean.MiscItems.Add(new MiscItem(new FormKey(clean.ModKey, 0x800), Rel) { EditorID = "CleanM" }); Write(clean, "ModClean");

        // assets: ModA and ModRec identical; ModA and ModSame different, ModA is higher priority so wins; ModLone and ModFar have runtime files only
        void File1(string folder, string rel, string body) { var p = Path.Combine(mods, folder, rel); Directory.CreateDirectory(Path.GetDirectoryName(p)!); File.WriteAllText(p, body); }
        File1("ModA", "meshes\\x.nif", "one"); File1("ModRec", "meshes\\x.nif", "one");
        File1("ModA", "textures\\y.dds", "aaa"); File1("ModSame", "textures\\y.dds", "bbb");
        File1("ModLone", "skse\\plugins\\thing.dll", "z"); File.WriteAllText(Path.Combine(mods, "ModLone", "Lone_DISTR.ini"), "x");
        File.WriteAllText(Path.Combine(prof, "modlist.txt"), "+ModA\n+ModRec\n+ModSame\n+ModRing\n+ModFar\n+ModLone\n+ModClean\n");
        File.WriteAllText(Path.Combine(prof, "plugins.txt"), "*A.esp\n*Rec.esp\n*Same.esp\n*Ring.esp\n*Far.esp\n*Lone.esp\n*Clean.esp\n");
        File.WriteAllText(Path.Combine(prof, "loadorder.txt"), "Skyrim.esm\nA.esp\nRec.esp\nSame.esp\nRing.esp\nFar.esp\nLone.esp\nClean.esp\n");
        var o = Environment.GetEnvironmentVariable("ORDERMAP_KEEP") ?? Path.Combine(root, "map.json");
        int rc = OrderMap.Build(instance, "P", game, null, o, true, Console.Out);
        Check("runs", rc == 0, $"exit {rc}");
        using var j = JsonDocument.Parse(File.ReadAllText(o));
        JsonElement? Ed(string x, string y) { foreach (var e in j.RootElement.GetProperty("edges").EnumerateArray()) { var (p, q) = (e.GetProperty("a").GetString(), e.GetProperty("b").GetString()); if ((p == x && q == y) || (p == y && q == x)) return e; } return null; }
        Check("shared record makes a record edge", Ed("A.esp", "Rec.esp")?.GetProperty("records").GetInt32() == 1);
        Check("shared exterior cell makes a cell edge", Ed("A.esp", "Same.esp")?.GetProperty("exterior").GetInt32() == 1);
        Check("ring-1 makes a neighbour edge", Ed("A.esp", "Ring.esp")?.GetProperty("neighbours").GetInt32() > 0);
        Check("ring-2 makes no edge with A (the planted miss)", Ed("A.esp", "Far.esp") is null);
        Check("a plugin with only its own record has no edge", !j.RootElement.GetProperty("edges").EnumerateArray().Any(e => e.GetProperty("a").GetString() == "Lone.esp" || e.GetProperty("b").GetString() == "Lone.esp"));
        JsonElement? As(string x, string y) { foreach (var e in j.RootElement.GetProperty("assetEdges").EnumerateArray()) { var (p, q) = (e.GetProperty("a").GetString(), e.GetProperty("b").GetString()); if ((p == x && q == y) || (p == y && q == x)) return e; } return null; }
        Check("identical bytes read identical", As("ModA", "ModRec")?.GetProperty("identical").GetInt32() == 1 && As("ModA", "ModRec")?.GetProperty("different").GetInt32() == 0);
        var diff = As("ModA", "ModSame");
        Check("different bytes read different", diff?.GetProperty("different").GetInt32() == 1);
        Check("the higher MO2 priority wins a loose path", diff?.GetProperty("winsA").GetInt32() == 1 && diff?.GetProperty("examples")[0].GetString()!.Contains("wins: ModA") == true);
        Check("a plugin's size in bytes is recorded", j.RootElement.GetProperty("plugins").EnumerateArray().All(p => p.GetProperty("bytes").GetInt64() > 0));
        Check("the recorded size is the file's length", j.RootElement.GetProperty("plugins").EnumerateArray().First(p => p.GetProperty("name").GetString() == "Rec.esp").GetProperty("bytes").GetInt64()
              == new FileInfo(Path.Combine(mods, "ModRec", "Rec.esp")).Length);
        JsonElement? CellOf(string fk) => j.RootElement.TryGetProperty("cells", out var cs) && cs.TryGetProperty(fk, out var c) ? c : null;
        var roomCell = CellOf(new FormKey(new ModKey("Lone", ModType.Plugin), 0x801).ToString());
        Check("an interior cell's EditorID is recorded by its FormKey", roomCell?.GetProperty("edid").GetString() == "LoneRoom", $"{roomCell}");
        var extCell = CellOf(new FormKey(new ModKey("Same", ModType.Plugin), 0x800).ToString());
        Check("an exterior cell is recorded with its EditorID, worldspace and grid",
              extCell?.GetProperty("edid").GetString() == "SameCell" && extCell?.GetProperty("worldspace").GetString() == "W"
              && extCell?.GetProperty("grid")[0].GetInt32() == 0 && extCell?.GetProperty("grid")[1].GetInt32() == 0, $"{extCell}");
        var ringCell = CellOf(new FormKey(new ModKey("Ring", ModType.Plugin), 0x800).ToString());
        Check("an exterior cell with no EditorID is recorded by worldspace and grid", ringCell is { } rcell && rcell.GetProperty("edid").ValueKind == JsonValueKind.Null
              && rcell.GetProperty("worldspace").GetString() == "W" && rcell.GetProperty("grid")[0].GetInt32() == 1, $"{ringCell}");
        var rt = j.RootElement.GetProperty("runtimeLayers");
        Check("runtime files are counted", rt.TryGetProperty("ModLone", out var l) && l.GetProperty("SKSE dll").GetInt32() == 1 && l.GetProperty("SPID").GetInt32() == 1);
    }
}
