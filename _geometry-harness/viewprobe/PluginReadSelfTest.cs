using System.Diagnostics;
using System.Text;
using System.Text.Json;
using HousecarlCore;
using Mutagen.Bethesda;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Skyrim;
using Noggog;

namespace ViewProbe;

// --plugin-read-selftest: what the render does when a plugin, or one record in it, cannot be read. No game data.
//
// Mutagen writes a small master and one override plugin (PrA.esp) that moves reference R1 out of the target cell and
// overrides the target cell's LAND (other heights, one more texture layer). Each arm then damages PrA.esp in one
// way, lays out a minimal MO2 instance and game folder around the files, and runs THIS viewprobe executable as a child
// process -- the real order resolution, the real prepass, the real walk and the real terrain walk -- reading its exit
// code, its console and its --winning-refs-dump line. Every arm first proves its own precondition (for example, that
// the damaged file really does make Mutagen throw where the arm says), so an arm cannot pass or fail for the wrong
// reason.
//
// The expectation in every arm is the CORRECT behaviour: a plugin, or a record, that cannot be read is never silently
// replaced by an earlier plugin's version. It is refused with the plugin named (and the grid square, for LAND), or,
// where the damage is cosmetic (a terrain texture layer), counted and named on the console. Two control arms (the
// master alone, and the master with the undamaged override) pin what "the earlier version" and "the later version" look
// like in the dump.
//
// Exit 0 when every arm passes; 1 otherwise. VP_PLUGINREAD_KEEP=1 keeps the temp folder and prints where it is.
internal static class PluginReadSelfTest
{
    const SkyrimRelease Rel = SkyrimRelease.SkyrimSE;
    static int _pass, _fail;

    static void Check(string name, bool ok, string detail)
    {
        if (ok) _pass++; else _fail++;
        Console.WriteLine($"   {(ok ? "PASS" : "FAIL"),-5} {name,-84} {detail}");
    }

    static void Note(string text) => Console.WriteLine($"          {text}");

    public static int Run()
    {
        _pass = _fail = 0;
        var root = Path.Combine(Path.GetTempPath(), "vp-plugread-" + Environment.ProcessId);
        Directory.CreateDirectory(root);
        bool keep = Environment.GetEnvironmentVariable("VP_PLUGINREAD_KEEP") == "1";
        try { Body(root); }
        catch (Exception ex) { Check("fixture", false, $"threw {ex.GetType().Name}: {ex.Message} at {ex.StackTrace?.Split('\n').FirstOrDefault()?.Trim()}"); }
        finally
        {
            if (keep) Console.WriteLine($"   kept {root}");
            else { try { Directory.Delete(root, true); } catch { } }
        }
        Console.WriteLine($"plugin-read-selftest: {_pass} passed, {_fail} failed");
        return _fail == 0 ? 0 : 1;
    }

    // ---- the fixture -------------------------------------------------------------------------------------------------
    static readonly ModKey MKey = new("PrMaster", ModType.Master);
    static FormKey M(uint id) => new(MKey, id);
    static readonly FormKey Stat = M(0x801), Ltex = M(0x802), W = M(0x830), E00 = M(0x840), E50 = M(0x842), Land = M(0x880), R1 = M(0x900);

    /// <summary>A layer header for quadrant q (0 bottom-left, 1 bottom-right, as viewprobe's terrain census indexes them).
    /// The quadrant is set by reflection so this file does not depend on where Mutagen declares the enum.</summary>
    static LayerHeader Header(int q, ushort layer)
    {
        var h = new LayerHeader { LayerNumber = layer };
        var prop = typeof(LayerHeader).GetProperty("Quadrant")!;
        prop.SetValue(h, Enum.ToObject(prop.PropertyType, q));
        return h;
    }

    static Landscape LandOf(float offset, int alphaLayers)
    {
        var land = new Landscape(Land, Rel)
        {
            VertexHeightMap = new LandscapeVertexHeightMap { Offset = offset, HeightMap = new Array2d<sbyte>(new sbyte[33, 33]) },
        };
        var b = new BaseLayer { Header = Header(0, 0) };
        b.Header.Texture.SetTo(Ltex);
        land.Layers.Add(b);
        for (int i = 0; i < alphaLayers; i++)
        {
            var a = new AlphaLayer { Header = Header(1, (ushort)(i + 1)) };
            a.Header.Texture.SetTo(Ltex);
            a.AlphaLayerData ??= new();
            a.AlphaLayerData.Add(new AlphaLayerData { Position = 0, Opacity = 1f });
            land.Layers.Add(a);
        }
        return land;
    }

    static Worldspace World(params Cell[] cells)
    {
        var ws = new Worldspace(W, Rel) { EditorID = "PrWorld" };
        var blk = new WorldspaceBlock { BlockNumberX = 0, BlockNumberY = 0, GroupType = GroupTypeEnum.ExteriorCellBlock };
        var sub = new WorldspaceSubBlock { BlockNumberX = 0, BlockNumberY = 0, GroupType = GroupTypeEnum.ExteriorCellSubBlock };
        foreach (var c in cells) sub.Items.Add(c);
        blk.Items.Add(sub); ws.SubCells.Add(blk);
        return ws;
    }

    static PlacedObject Ref(float x, float y)
    {
        var r = new PlacedObject(R1, Rel) { Placement = new Placement { Position = new P3Float(x, y, 0), Rotation = new P3Float(0, 0, 0) } };
        r.Base.SetTo(Stat);
        return r;
    }

    static (string Master, string A) WriteFixture(string dir)
    {
        Directory.CreateDirectory(dir);
        var master = new SkyrimMod(MKey, Rel);
        master.Statics.Add(new Static(Stat, Rel) { EditorID = "PrStatic", Model = new Model { File = "Meshes\\pr\\static.nif" } });
        master.LandscapeTextures.Add(new LandscapeTexture(Ltex, Rel) { EditorID = "PrLtex" });
        var e00 = new Cell(E00, Rel) { Flags = default, Grid = new CellGrid { Point = new P2Int(0, 0) }, EditorID = "PrE00" };
        e00.Temporary.Add(Ref(100, 100));
        e00.Landscape = LandOf(0f, 1);
        var e50 = new Cell(E50, Rel) { Flags = default, Grid = new CellGrid { Point = new P2Int(5, 0) }, EditorID = "PrE50" };
        master.Worldspaces.Add(World(e00, e50));
        var mPath = Path.Combine(dir, "PrMaster.esm");
        master.BeginWrite.ToPath(mPath).WithLoadOrder(Array.Empty<ISkyrimModGetter>()).Write();
        using var mOv = SkyrimMod.CreateFromBinaryOverlay(mPath, Rel);

        var a = new SkyrimMod(new ModKey("PrA", ModType.Plugin), Rel);
        var a00 = new Cell(E00, Rel) { Flags = default, Grid = new CellGrid { Point = new P2Int(0, 0) }, EditorID = "PrE00" };
        a00.Landscape = LandOf(10f, 2);
        var a50 = new Cell(E50, Rel) { Flags = default, Grid = new CellGrid { Point = new P2Int(5, 0) }, EditorID = "PrE50" };
        a50.Temporary.Add(Ref(5 * 4096 + 100, 300));
        a.Worldspaces.Add(World(a00, a50));
        var aPath = Path.Combine(dir, "PrA.esp");
        a.BeginWrite.ToPath(aPath).WithLoadOrder(mOv).Write();
        return (mPath, aPath);
    }

    // ---- byte-level damage, independent of Mutagen ---------------------------------------------------------------------
    sealed record Rec(int Off, string Sig, uint FormId, int DataSize, List<int> Ancestors);

    static List<Rec> Records(byte[] b)
    {
        var list = new List<Rec>();
        int first = 24 + BitConverter.ToInt32(b, 4);
        void Walk(int off, int end, List<int> anc)
        {
            while (off < end)
            {
                string sig = Encoding.ASCII.GetString(b, off, 4);
                int size = BitConverter.ToInt32(b, off + 4);
                if (sig == "GRUP") { Walk(off + 24, off + size, new List<int>(anc) { off }); off += size; }
                else { list.Add(new Rec(off, sig, BitConverter.ToUInt32(b, off + 12), size, anc)); off += 24 + size; }
            }
        }
        Walk(first, b.Length, new List<int>());
        return list;
    }

    /// <summary>The Nth subrecord of this signature in a record: (offset of its header, its data length).</summary>
    static (int Off, int Len) Sub(byte[] b, Rec r, string subSig, int nth = 0)
    {
        if ((BitConverter.ToUInt32(b, r.Off + 8) & 0x40000) != 0) throw new InvalidOperationException("record is compressed");
        int off = r.Off + 24, end = off + r.DataSize, seen = 0;
        while (off < end)
        {
            string s = Encoding.ASCII.GetString(b, off, 4);
            int len = BitConverter.ToUInt16(b, off + 4);
            if (s == subSig && seen++ == nth) return (off, len);
            off += 6 + len;
        }
        throw new InvalidOperationException($"{r.Sig} has no {subSig} #{nth}");
    }

    /// <summary>Shrink one subrecord's data to newLen bytes, fixing the record's and every enclosing group's size.</summary>
    static byte[] Shrink(byte[] b, string recSig, uint idLow, string subSig, int newLen, int nth = 0)
    {
        var r = Records(b).Single(x => x.Sig == recSig && (x.FormId & 0xFFFFFF) == idLow);
        var (so, len) = Sub(b, r, subSig, nth);
        int delta = len - newLen;
        if (delta <= 0) throw new InvalidOperationException("not a shrink");
        var outp = new byte[b.Length - delta];
        Buffer.BlockCopy(b, 0, outp, 0, so + 6 + newLen);
        Buffer.BlockCopy(b, so + 6 + len, outp, so + 6 + newLen, b.Length - (so + 6 + len));
        BitConverter.GetBytes((ushort)newLen).CopyTo(outp, so + 4);
        BitConverter.GetBytes(r.DataSize - delta).CopyTo(outp, r.Off + 4);
        foreach (var g in r.Ancestors) BitConverter.GetBytes(BitConverter.ToInt32(b, g + 4) - delta).CopyTo(outp, g + 4);
        return outp;
    }

    // ---- the child run -----------------------------------------------------------------------------------------------
    sealed record Child(int Exit, string Out, string Err, List<JsonElement> Dump);

    static Child RunViewprobe(string armDir, string master, string? plugin, bool listPlugin, string[] extraArgs, string? dumpTo)
    {
        string inst = Path.Combine(armDir, "inst"), game = Path.Combine(armDir, "game"), prof = Path.Combine(inst, "profiles", "P");
        Directory.CreateDirectory(Path.Combine(game, "Data"));
        Directory.CreateDirectory(prof);
        Directory.CreateDirectory(Path.Combine(inst, "mods", "Pr"));
        Directory.CreateDirectory(Path.Combine(inst, "overwrite"));
        File.Copy(master, Path.Combine(game, "Data", "PrMaster.esm"), true);
        if (plugin is not null) File.Copy(plugin, Path.Combine(inst, "mods", "Pr", "PrA.esp"), true);
        var names = listPlugin ? new[] { "PrMaster.esm", "PrA.esp" } : new[] { "PrMaster.esm" };
        File.WriteAllLines(Path.Combine(prof, "plugins.txt"), names.Select(n => "*" + n));
        File.WriteAllLines(Path.Combine(prof, "loadorder.txt"), names);
        File.WriteAllLines(Path.Combine(prof, "modlist.txt"), new[] { "+Pr" });
        var psi = new ProcessStartInfo(Environment.ProcessPath!) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        foreach (var a in extraArgs) psi.ArgumentList.Add(a);
        foreach (var a in new[] { "--instance", inst, "--game", game, "--profile", "P" }) psi.ArgumentList.Add(a);
        if (dumpTo is not null) { psi.ArgumentList.Add("--winning-refs-dump"); psi.ArgumentList.Add(dumpTo); }
        foreach (var v in new[] { "HOUSECARL_HARNESS_CONFIG", "HOUSECARL_MO2_INSTANCE", "HOUSECARL_MO2_PROFILE", "HOUSECARL_GAME_PATH" })
            psi.Environment.Remove(v);
        psi.Environment["HOUSECARL_RUNLOG_DIR"] = Path.Combine(armDir, "runlog");
        using var p = Process.Start(psi)!;
        var so = p.StandardOutput.ReadToEndAsync();
        var se = p.StandardError.ReadToEndAsync();
        if (!p.WaitForExit(180_000)) { p.Kill(true); throw new TimeoutException("viewprobe child did not finish in 180 s"); }
        var dump = new List<JsonElement>();
        if (dumpTo is not null && File.Exists(dumpTo))
            foreach (var line in File.ReadAllLines(dumpTo).Where(l => l.Length > 0)) dump.Add(JsonDocument.Parse(line).RootElement.Clone());
        return new Child(p.ExitCode, so.Result, se.Result, dump);
    }

    static JsonElement? RefIn(Child c, FormKey k)
    {
        if (c.Dump.Count == 0) return null;
        foreach (var r in c.Dump[^1].GetProperty("refs").EnumerateArray())
            if (r.GetProperty("formKey").GetString() == k.ToString()) return r;
        return null;
    }

    static string? TerrainDigest(Child c, int gx, int gy, out int layers)
    {
        layers = -1;
        if (c.Dump.Count == 0 || !c.Dump[^1].TryGetProperty("terrain", out var t)) return null;
        foreach (var e in t.EnumerateArray())
        {
            var g = e.GetProperty("grid");
            if (g[0].GetInt32() == gx && g[1].GetInt32() == gy)
            {
                if (e.TryGetProperty("layers", out var l)) layers = l.GetInt32();
                return e.GetProperty("heightsSha256").GetString();
            }
        }
        return null;
    }

    static string Brief(Child c) =>
        $"exit {c.Exit}; err: {Tail(c.Err)}; dump lines {c.Dump.Count}";

    static string Tail(string s)
    {
        var lines = s.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return lines.Length == 0 ? "(none)" : string.Join(" / ", lines.TakeLast(2)).Replace("\r", "");
    }

    static bool Named(Child c, params string[] words) =>
        (c.Out + "\n" + c.Err).Split('\n').Any(l => words.All(w => l.Contains(w, StringComparison.OrdinalIgnoreCase)));

    static bool Unhandled(Child c) => c.Err.Contains("Unhandled exception", StringComparison.OrdinalIgnoreCase);

    static void Body(string root)
    {
        var (mPath, aPath) = WriteFixture(Path.Combine(root, "fixture"));
        var aBytes = File.ReadAllBytes(aPath);
        string target = E00.ToString();
        string[] terrainArgs = { target, "--eye", "100,100,600", "--look", "2000,2000,0", "--dump-terrain" };
        Console.WriteLine("plugin-read-selftest: a Mutagen-written master and override, damaged byte by byte, read by this viewprobe as a child process");

        // ---- controls ----------------------------------------------------------------------------------------------------
        var cM = RunViewprobe(Path.Combine(root, "ctrl-M"), mPath, null, false, terrainArgs, Path.Combine(root, "ctrl-M.jsonl"));
        var mDigest = TerrainDigest(cM, 0, 0, out int mLayers);
        Check("CTRL-M  the master alone: R1 in E00 at the master's position; terrain (0,0) read",
              cM.Exit == 0 && RefIn(cM, R1) is { } r0 && MathF.Abs(r0.GetProperty("pos")[0].GetSingle() - 100) < 0.01f && mDigest is not null,
              Brief(cM) + $"; terrain {mDigest?[..12]} layers {mLayers}");
        var cA = RunViewprobe(Path.Combine(root, "ctrl-MA"), mPath, aPath, true, terrainArgs, Path.Combine(root, "ctrl-MA.jsonl"));
        var aDigest = TerrainDigest(cA, 0, 0, out int aLayers);
        Check("CTRL-MA master + undamaged override: R1 moved out (absent); terrain (0,0) is the override's",
              cA.Exit == 0 && cA.Dump.Count == 1 && RefIn(cA, R1) is null && aDigest is not null && aDigest != mDigest && aLayers == mLayers + 1,
              Brief(cA) + $"; terrain {aDigest?[..12]} layers {aLayers}");

        // ---- P2-open: the plugin's header reads, its body does not open -----------------------------------------------------
        {
            var bad = (byte[])aBytes.Clone();
            int top = 24 + BitConverter.ToInt32(bad, 4);
            BitConverter.GetBytes(bad.Length * 4).CopyTo(bad, top + 4);   // the first top group claims to run past the file
            var p = Path.Combine(root, "open", "PrA.esp");
            Directory.CreateDirectory(Path.GetDirectoryName(p)!);
            File.WriteAllBytes(p, bad);
            bool headerReads = Encoding.ASCII.GetString(bad, 0, 4) == "TES4";
            string? openThrows = null;
            try { using var ov = SkyrimMod.CreateFromBinaryOverlay(p, Rel); foreach (var _ in PlacementExtract.FromMod(ov)) { } }
            catch (Exception ex) { openThrows = ex.GetType().Name; }
            Note($"P2-open precondition: TES4 header intact {headerReads}; Mutagen open+enumerate throws: {openThrows ?? "NO"}");
            var c = RunViewprobe(Path.Combine(root, "arm-open"), mPath, p, true, new[] { target }, Path.Combine(root, "open.jsonl"));
            Check("P2-open an override whose body cannot be read: refused, the plugin named (not the earlier R1 drawn)",
                  headerReads && openThrows is not null && c.Exit != 0 && c.Dump.Count == 0 && !Unhandled(c) && Named(c, "PrA.esp"),
                  Brief(c) + (RefIn(c, R1) is { } rr ? $"; R1 in the dump at {rr.GetProperty("pos")[0].GetSingle():0},{rr.GetProperty("pos")[1].GetSingle():0}" : ""));
        }

        // ---- P2-rec: the plugin opens; one record inside it does not parse -----------------------------------------------------
        {
            var recs = Records(aBytes);
            var refr = recs.Single(x => x.Sig == "REFR" && (x.FormId & 0xFFFFFF) == 0x900);
            var (so, len) = Sub(aBytes, refr, "DATA");
            var bad = (byte[])aBytes.Clone();
            BitConverter.GetBytes((ushort)(len + 40)).CopyTo(bad, so + 4);   // DATA claims 40 bytes more than the record holds
            var p = Path.Combine(root, "rec", "PrA.esp");
            Directory.CreateDirectory(Path.GetDirectoryName(p)!);
            File.WriteAllBytes(p, bad);
            string? opens = "yes", enumThrows = null;
            try
            {
                using var ov = SkyrimMod.CreateFromBinaryOverlay(p, Rel);
                try { foreach (var cp in PlacementExtract.FromMod(ov)) _ = cp.Refs.Count; } catch (Exception ex) { enumThrows = ex.GetType().Name; }
            }
            catch (Exception ex) { opens = "no: " + ex.GetType().Name; }
            Note($"P2-rec precondition (measured): Mutagen opens it: {opens}; enumerating the placements throws: {enumThrows ?? "NO (read silently)"}");
            var c = RunViewprobe(Path.Combine(root, "arm-rec"), mPath, p, true, new[] { target }, Path.Combine(root, "rec.jsonl"));
            Check("P2-rec an override with one unparseable REFR: refused with the plugin named, not a crash, not the earlier R1",
                  c.Exit != 0 && c.Dump.Count == 0 && !Unhandled(c) && Named(c, "PrA.esp"),
                  Brief(c) + (Unhandled(c) ? "; UNHANDLED EXCEPTION" : "")
                  + (RefIn(c, R1) is { } rr ? $"; R1 in the dump at {rr.GetProperty("pos")[0].GetSingle():0},{rr.GetProperty("pos")[1].GetSingle():0}" : ""));
        }

        // ---- S13: the profile lists the override, and its file is missing -------------------------------------------------------
        {
            var c = RunViewprobe(Path.Combine(root, "arm-missing"), mPath, null, true, new[] { target }, Path.Combine(root, "missing.jsonl"));
            Check("S13     the profile lists PrA.esp and no file provides it: refused, the plugin named",
                  c.Exit != 0 && c.Dump.Count == 0 && Named(c, "PrA.esp"),
                  Brief(c) + (c.Dump.Count > 0 ? $"; plugins walked {c.Dump[^1].GetProperty("plugins").GetArrayLength()}" : ""));
        }

        // ---- S9: one LAND's heightmap cannot be read ------------------------------------------------------------------------
        {
            var bad = Shrink(aBytes, "LAND", 0x880, "VHGT", 100);
            var p = Path.Combine(root, "vhgt", "PrA.esp");
            Directory.CreateDirectory(Path.GetDirectoryName(p)!);
            File.WriteAllBytes(p, bad);
            string where = "nowhere";
            try
            {
                using var ov = SkyrimMod.CreateFromBinaryOverlay(p, Rel);
                foreach (var ws in ov.Worldspaces.Records)
                    foreach (var blk in ws.SubCells) foreach (var sub in blk.Items) foreach (var cell in sub.Items)
                    {
                        if (cell.FormKey != E00) continue;
                        ILandscapeVertexHeightMapGetter? vh;
                        try { vh = cell.Landscape?.VertexHeightMap; } catch (Exception ex) { where = "reading VertexHeightMap: " + ex.GetType().Name; continue; }
                        try { _ = vh!.HeightMap[32, 32]; } catch (Exception ex) { where = "reading HeightMap[x,y]: " + ex.GetType().Name; }
                    }
            }
            catch (Exception ex) { where = "opening: " + ex.GetType().Name; }
            Note($"S9 precondition (measured): the damaged VHGT throws when {where}");
            var c = RunViewprobe(Path.Combine(root, "arm-vhgt"), mPath, p, true, terrainArgs, Path.Combine(root, "vhgt.jsonl"));
            var d = TerrainDigest(c, 0, 0, out _);
            Check("S9      the override's LAND heightmap cannot be read: refused, plugin and grid named (not the master's heights)",
                  where.StartsWith("reading") && c.Exit != 0 && c.Dump.Count == 0 && !Unhandled(c) && Named(c, "PrA.esp", "0,0"),
                  Brief(c) + $"; terrain (0,0) {(d is null ? "none" : d == mDigest ? "= the MASTER's heights" : d == aDigest ? "= the override's" : d[..12])}");
        }

        // ---- S10 / S11: one texture layer cannot be read ----------------------------------------------------------------------
        {
            // Two ways to damage the second texture layer; each is measured the way viewprobe's terrain census reads a
            // layer (header quadrant, number, texture, then each alpha point's position and opacity). The arm uses the
            // first damage that makes that read throw; a damage Mutagen reads without throwing is reported, not used.
            (string Where, int Layers) Measure(string path)
            {
                string w = "nowhere"; int n = -1;
                try
                {
                    using var ov = SkyrimMod.CreateFromBinaryOverlay(path, Rel);
                    foreach (var ws in ov.Worldspaces.Records)
                        foreach (var blk in ws.SubCells) foreach (var sub in blk.Items) foreach (var cell in sub.Items)
                        {
                            if (cell.FormKey != E00 || cell.Landscape is not { } ls) continue;
                            try
                            {
                                n = 0;
                                foreach (var l in ls.Layers)
                                {
                                    n++;
                                    _ = (int)(object)l.Header!.Quadrant; _ = l.Header.LayerNumber; _ = l.Header.Texture.FormKeyNullable;
                                    if (l is IAlphaLayerGetter al) foreach (var pt in al.AlphaLayerData) { _ = pt.Position; _ = pt.Opacity; }
                                }
                            }
                            catch (Exception ex) { w = $"enumerating Layers (layer {n}): " + ex.GetType().Name; }
                        }
                }
                catch (Exception ex) { w = "opening: " + ex.GetType().Name; }
                return (w, n);
            }
            var variants = new List<(string Name, byte[] Bytes)>
            {
                ("ATXT cut to 4 bytes", Shrink(aBytes, "LAND", 0x880, "ATXT", 4, nth: 1)),
                ("VTXT cut to 7 bytes", Shrink(aBytes, "LAND", 0x880, "VTXT", 7, nth: 0)),
                ("VTXT cut to 4 bytes", Shrink(aBytes, "LAND", 0x880, "VTXT", 4, nth: 0)),
            };
            string where = "nowhere", used = "none";
            byte[] bad = variants[0].Bytes;
            var p = Path.Combine(root, "atxt", "PrA.esp");
            Directory.CreateDirectory(Path.GetDirectoryName(p)!);
            foreach (var (name, bytes) in variants)
            {
                File.WriteAllBytes(p, bytes);
                var (w, n) = Measure(p);
                Note($"S10 precondition (measured): {name}: Mutagen reads {n} layer(s) (undamaged 3); the census read throws when {w}");
                if (used == "none" && w.StartsWith("enumerating")) { used = name; where = w; bad = bytes; }
            }
            File.WriteAllBytes(p, bad);
            Note($"S10 arm uses: {used}");
            var c = RunViewprobe(Path.Combine(root, "arm-atxt"), mPath, p, true, terrainArgs, Path.Combine(root, "atxt.jsonl"));
            var d = TerrainDigest(c, 0, 0, out int layers);
            Check("S10     one of the override's texture layers cannot be read: drawn, and the failure counted and named on the console",
                  where.StartsWith("enumerating") && c.Exit == 0 && d == aDigest && Named(c, "terrain layer", "could not be read", "PrA.esp", "0,0"),
                  Brief(c) + $"; terrain (0,0) {(d is null ? "none" : d == aDigest ? "= the override's heights" : d == mDigest ? "= the MASTER's" : d[..12])} layers {layers} (undamaged {aLayers})");
            // S11 reads less of each layer than the census does (texture and alpha points, not the quadrant), so the
            // ATXT cut above reads cleanly there. Its own damage cuts the same ATXT to 2 bytes, so the texture link
            // itself cannot be read, and the precondition is measured with --find-ltex's own reads.
            var bad11 = Shrink(aBytes, "LAND", 0x880, "ATXT", 2, nth: 1);
            var p11 = Path.Combine(root, "atxt2", "PrA.esp");
            Directory.CreateDirectory(Path.GetDirectoryName(p11)!);
            File.WriteAllBytes(p11, bad11);
            string where11 = "nowhere";
            try
            {
                using var ov = SkyrimMod.CreateFromBinaryOverlay(p11, Rel);
                foreach (var ws in ov.Worldspaces.Records)
                    foreach (var blk in ws.SubCells) foreach (var sub in blk.Items) foreach (var cell in sub.Items)
                    {
                        if (cell.FormKey != E00 || cell.Landscape is not { } ls) continue;
                        int n = 0;
                        try
                        {
                            foreach (var l in ls.Layers)
                            {
                                n++;
                                _ = l.Header?.Texture.FormKeyNullable;
                                if (l is IAlphaLayerGetter al) _ = al.AlphaLayerData.Count(pt => pt.Opacity > 0.004f);
                            }
                        }
                        catch (Exception ex) { where11 = $"enumerating Layers (layer {n}): " + ex.GetType().Name; }
                    }
            }
            catch (Exception ex) { where11 = "opening: " + ex.GetType().Name; }
            Note($"S11 precondition (measured): ATXT cut to 2 bytes: --find-ltex's read throws when {where11}");
            var f = RunViewprobe(Path.Combine(root, "arm-findltex"), mPath, p11, true,
                                 new[] { target, "--eye", "100,100,600", "--look", "2000,2000,0", "--find-ltex", "PrLtex" }, null);
            Check("S11     --find-ltex over a layer whose texture cannot be read: the failure counted and named on the console",
                  where11.StartsWith("enumerating") && f.Exit == 0 && Named(f, "terrain layer", "could not be read", "PrA.esp", "0,0"),
                  Brief(f));
        }

        // ---- S5: --emit-bats's own walk over an override whose body cannot be read ----------------------------------------------
        {
            var bad = (byte[])aBytes.Clone();
            int top = 24 + BitConverter.ToInt32(bad, 4);
            BitConverter.GetBytes(bad.Length * 4).CopyTo(bad, top + 4);
            var p = Path.Combine(root, "bats", "PrA.esp");
            Directory.CreateDirectory(Path.GetDirectoryName(p)!);
            File.WriteAllBytes(p, bad);
            var wl = Path.Combine(root, "bats", "pr.worklist.json");
            File.WriteAllText(wl, "{\"Label\":\"pr\",\"Cells\":[{\"Cell\":\"" + target + "\",\"Shots\":[{\"Label\":\"s\",\"Eye\":[100,100,600],\"Look\":[2000,2000,0]}]}]}");
            var c = RunViewprobe(Path.Combine(root, "arm-bats"), mPath, p, true,
                                 new[] { wl, "--emit-bats", Path.Combine(root, "bats", "out") }, null);
            Check("S5      --emit-bats over an override whose body cannot be read: refused, the plugin named",
                  c.Exit != 0 && !Unhandled(c) && Named(c, "PrA.esp"), Brief(c));
            // The other direction: the same walk over the undamaged override still emits.
            var ok = RunViewprobe(Path.Combine(root, "arm-bats-ok"), mPath, aPath, true,
                                  new[] { wl, "--emit-bats", Path.Combine(root, "bats", "out-ok") }, null);
            Check("S5b     --emit-bats over the undamaged override: runs (exit 0)", ok.Exit == 0, Brief(ok));
        }

        // ---- the other direction of every refusal and count: an undamaged order is not refused and names nothing ----
        Check("S13b    a profile whose every plugin has a file: runs, and walks both plugins (CTRL-MA)",
              cA.Exit == 0 && cA.Dump.Count == 1 && cA.Dump[0].GetProperty("plugins").GetArrayLength() == 2, Brief(cA));
        Check("OPEN-b  no plugin or record refusal on the undamaged order (CTRL-MA)",
              cA.Exit == 0 && !Named(cA, "could not be read"), Brief(cA));
        {
            var f = RunViewprobe(Path.Combine(root, "arm-findltex-ok"), mPath, aPath, true,
                                 new[] { target, "--eye", "100,100,600", "--look", "2000,2000,0", "--find-ltex", "PrLtex" }, null);
            Check("S11b    --find-ltex over the undamaged override: exit 0, no layer named as unreadable",
                  f.Exit == 0 && !Named(f, "could not be read"), Brief(f));
        }

        // ---- the report's limits: terrainLayersUnreadable in the planes report, both directions --------------------------
        {
            string atxt = Path.Combine(root, "atxt", "PrA.esp");
            JsonElement? ReportKey(Child c, string outDir, out bool found)
            {
                found = false;
                if (!Directory.Exists(outDir)) return null;
                var rep = Directory.GetFiles(outDir, "*-planes.json", SearchOption.AllDirectories).FirstOrDefault();
                if (rep is null) return null;
                using var doc = JsonDocument.Parse(File.ReadAllText(rep));
                if (!doc.RootElement.TryGetProperty("terrainLayersUnreadable", out var k)) return null;
                found = true;
                return k.Clone();
            }
            string[] RenderArgs(string outDir) => new[] { target, "--eye", "100,100,600", "--look", "2000,2000,0", "--size", "64",
                                                          "--planes-report", "--out", outDir };
            var dOut = Path.Combine(root, "render-bad");
            var d = RunViewprobe(Path.Combine(root, "arm-render-bad"), mPath, atxt, true, RenderArgs(dOut), null);
            var dk = ReportKey(d, dOut, out bool dFound);
            Check("S10r    a render with the unreadable layer: its planes report names it (terrainLayersUnreadable, count 1)",
                  dFound && dk is { ValueKind: JsonValueKind.Object } o && o.GetProperty("count").GetInt32() == 1
                  && o.GetProperty("layers")[0].GetProperty("plugin").GetString() == "PrA.esp",
                  Brief(d) + $"; key {(dFound ? dk?.ValueKind.ToString() : "ABSENT or no report")}");
            var gOut = Path.Combine(root, "render-ok");
            var g = RunViewprobe(Path.Combine(root, "arm-render-ok"), mPath, aPath, true, RenderArgs(gOut), null);
            var gk = ReportKey(g, gOut, out bool gFound);
            Check("S10rb   a render of the undamaged override: the key is present and null",
                  gFound && gk is { ValueKind: JsonValueKind.Null }, Brief(g) + $"; key {(gFound ? gk?.ValueKind.ToString() : "ABSENT or no report")}");
        }

        // ---- L1: a conflict file naming a FormKey that does not parse --------------------------------------------------------
        {
            string Conflicts(string dir, params string[] keys)
            {
                Directory.CreateDirectory(dir);
                var recs = string.Join(",", keys.Select(k => "{\"FormKey\":\"" + k + "\",\"Role\":\"door\",\"Label\":null}"));
                var path = Path.Combine(dir, "c.json");
                File.WriteAllText(path, "{\"Probe\":\"selftest\",\"Instance\":\"selftest\",\"GeneratedUtc\":\"2026-01-01T00:00:00Z\","
                                      + "\"Conflicts\":[{\"Kind\":\"OVERLAP\",\"A\":\"PrMaster.esm\",\"B\":\"PrA.esp\",\"Summary\":\"s\","
                                      + "\"Necessary\":[" + recs + "]}]}");
                return path;
            }
            var bad = Conflicts(Path.Combine(root, "conf-bad"), "NOT-A-FORMKEY", R1.ToString());
            var c = RunViewprobe(Path.Combine(root, "arm-conf-bad"), mPath, aPath, true,
                                 terrainArgs.Concat(new[] { "--conflicts", bad }).ToArray(), Path.Combine(root, "conf-bad.jsonl"));
            Check("L1      a conflict file with an unparseable FormKey: the run goes on, the key named and counted",
                  c.Exit == 0 && Named(c, "NOT-A-FORMKEY", "is not a FormKey") && Named(c, "1 record(s) with an unparseable FormKey"),
                  Brief(c));
            var good = Conflicts(Path.Combine(root, "conf-ok"), R1.ToString());
            var g = RunViewprobe(Path.Combine(root, "arm-conf-ok"), mPath, aPath, true,
                                 terrainArgs.Concat(new[] { "--conflicts", good }).ToArray(), Path.Combine(root, "conf-ok.jsonl"));
            Check("L1b     a conflict file whose keys all parse: nothing named as unparseable",
                  g.Exit == 0 && !Named(g, "unparseable FormKey") && !Named(g, "is not a FormKey"), Brief(g));
        }
    }
}
