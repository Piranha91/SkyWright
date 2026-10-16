using System.Numerics;
using Mutagen.Bethesda;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Skyrim;
using Noggog;
using NiflySharp;
using NiflySharp.Blocks;
using NiflySharp.Structs;
using SkCell = Mutagen.Bethesda.Skyrim.Cell;

namespace HarnessShared;

// THE POPULATION FIXTURE (P25, overlapprobe --self-test with PD_FIXTURE_KEEP; read by tests/test_population_scope.py, which
// runs the real overlapprobe and contactprobe on it whole-order and with --population). Test-only. Written with Mutagen,
// the meshes with NiflySharp. Positions are world units; an exterior cell is 4096 u. Expectations are worked out by hand.
//
// Plugins (load order): PpBase.esm; PpA.esp, PpB.esp, PpMover.esp, PpDelta.esp, each mastering PpBase.esm only, so a row
// between two of them is Blind. Mod folders: one per plugin; PpMeshMod ships only meshes\pp\mshb.nif (a bigger PpMeshB).
// The arm WITH the delta loads everything; the arm WITHOUT drops PpDelta.esp and the PpMeshMod folder. The population
// names PpDelta.esp and the PpMeshMod folder.
//
// Bases (PpBase): PpBox (pp\box.nif, render and collision +-50), PpSlab (pp\slab.nif, +-100), PpCup (pp\cup.nif, +-20),
// PpStatX (pp\box.nif; PpDelta overrides its model to pp\big.nif, +-100), PpMeshB (pp\mshb.nif +-20; PpMeshMod's is +-60),
// PpMarker (no model), PpHuge (pp\huge.nif, render +-4200, OBND +-4200).
// Cells: interiors IPop, IOther, IOut; worldspace PpWorld with persistent cell T and exteriors E00 (0,0), E10 (1,0),
// E50 (5,0), E90 (9,0), E200 (20,0), E600 (60,0).
//
//  case  refs (plugin)                                             overlap row                    population
//  O1    a1 (A) x b1 (B) in IPop; PpDelta files d0 in IPop        A x B, both arms               in: IPop is a delta cell
//  O2    a2 (A) x b2 (B) in IOut                                  A x B, both arms               out
//  O3    d3 (Delta) x a3 (A) in IOther                            with only                      in: S1
//  O3b   b4 (PpBase, moved onto a4 by Delta) x a4 (A) in IOther   with only                      in: S1 override, also WITHOUT
//  O4    dE (Delta, E00) x hA (A, PpHuge filed in E10)            with only                      in: the large partner is found
//  O4b   hA (A) x bE (B) in E10                                   both arms                      out: E10 is not a delta cell
//  O5    pA (PpBase persistent in T, moved by Delta) x bP (B, E90) with only                     in: persistent ref by key
//  O5b   pA2 (A, persistent in T) x bP2 (B, E90)                  both arms                      out: T is never a region
//  O6    k (A, E200, XESP parent mk) x b6 (B); Delta disables mk   without only                   in: S3 (mk is S1)
//  O7    x7 (A, PpStatX) x b7 (B) in E90                          with only (bigger model)       in: S4
//  O8    m8 (A, PpMeshB) x b8 (B) in E90                          with only (PpMeshMod's mesh)   in: S2
//  O9    a9 (A) x b9 (B) in E90                                   both arms                      out
//  contact (mover PpMover)
//  K1    cup dc1 (Delta) on slab hs1 (PpBase, IOther), moved      Stranded, with only            in: dependent is S1
//  K2    cup c2 (PpBase) on slab hs2 (PpBase, E50), moved; Delta  Stranded, WITHOUT only         in: E50's ring (ds2)
//        places slab ds2 where hs2 stood, so c2 is re-supported
//  K3    cup c3 on slab hs3 (both PpBase, E600), moved             Stranded, both arms            out
public static class PopulationFixture
{
    public const SkyrimRelease Rel = SkyrimRelease.SkyrimSE;
    public static readonly ModKey BaseKey = new("PpBase", ModType.Master), AKey = new("PpA", ModType.Plugin), BKey = new("PpB", ModType.Plugin),
        MoverKey = new("PpMover", ModType.Plugin), DeltaKey = new("PpDelta", ModType.Plugin);
    public const string MeshModName = "PpMeshMod";
    static FormKey K(uint id) => new(BaseKey, id);
    static FormKey KA(uint id) => new(AKey, id);
    static FormKey KB(uint id) => new(BKey, id);
    static FormKey KD(uint id) => new(DeltaKey, id);
    public static readonly FormKey Box = K(0x801), Slab = K(0x802), Cup = K(0x803), StatX = K(0x804), MeshB = K(0x805), Marker = K(0x806), Huge = K(0x807);
    public static readonly FormKey IPop = K(0x810), IOther = K(0x811), IOut = K(0x812), World = K(0x830), T = K(0x831),
        E00 = K(0x840), E10 = K(0x841), E50 = K(0x842), E90 = K(0x843), E200 = K(0x844), E600 = K(0x845);
    public static readonly FormKey B4 = K(0x901), Hs1 = K(0x902), PA = K(0x903), Mk = K(0x904), Hs2 = K(0x905), C2 = K(0x906), Hs3 = K(0x907), C3 = K(0x908);
    public static readonly FormKey A1 = KA(0x901), A2 = KA(0x902), A3 = KA(0x903), A4 = KA(0x904), HA = KA(0x905), Kc = KA(0x906), X7 = KA(0x907),
        M8 = KA(0x908), A9 = KA(0x909), PA2 = KA(0x90A);
    public static readonly FormKey B1 = KB(0x901), B2 = KB(0x902), BE = KB(0x903), BP = KB(0x904), BP2 = KB(0x905), B7 = KB(0x906), B8 = KB(0x907),
        B9 = KB(0x908), B6 = KB(0x909);
    public static readonly FormKey D0 = KD(0x901), D3 = KD(0x902), DE = KD(0x903), Dc1 = KD(0x904), Ds2 = KD(0x905);
    public static readonly string[] Order = { "PpBase.esm", "PpA.esp", "PpB.esp", "PpMover.esp", "PpDelta.esp" };
    const float C = 4096f;

    /// <summary>The plugins in load order, and the mods folder (one folder per plugin stem, plus PpMeshMod).</summary>
    public sealed record Written(IReadOnlyList<string> Order, string ModsDir);

    static PlacedObject Ref(FormKey key, FormKey baseKey, float x, float y, float z, FormKey? parent = null)
    {
        var r = new PlacedObject(key, Rel) { Placement = new Placement { Position = new(x, y, z), Rotation = new(0, 0, 0) } };
        r.Base.SetTo(baseKey);
        if (parent is { } pk) { r.EnableParent = new EnableParent(); r.EnableParent.Reference.SetTo(pk); }
        return r;
    }
    static SkCell Int(FormKey key, string edid) => new(key, Rel) { EditorID = edid, Flags = SkCell.Flag.IsInteriorCell };
    static SkCell Ext(FormKey key, int x, int y) => new(key, Rel) { Flags = default, Grid = new CellGrid { Point = new P2Int(x, y) } };
    static SkCell Top() => new(T, Rel) { Flags = default };
    static Worldspace Wrld(SkCell? top, params SkCell[] exteriors)
    {
        var ws = new Worldspace(World, Rel) { EditorID = "PpWorld" };
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

    /// <summary>A closed render box (+-half) and, when asked, a collision box of the same size.</summary>
    public static byte[] Mesh(float half, bool collision)
    {
        var f = new NifFile();
        var version = new NiVersion { FileVersion = NiVersion.ToFile("20.2.0.7"), UserVersion = 12, StreamVersion = 100 };
        f.Create(version, withRootNode: true);
        var root = f.GetRootNodes().First();
        var node = new NiNode { Scale = 1f };
        root.Children.AddBlockRef(f.AddBlock(node));
        var v = new List<Vector3>();
        for (int i = 0; i < 8; i++) v.Add(new Vector3((i & 1) != 0 ? half : -half, (i & 2) != 0 ? half : -half, (i & 4) != 0 ? half : -half));
        var t = new List<Triangle>
        {
            new(0, 2, 1), new(1, 2, 3), new(4, 5, 6), new(5, 7, 6), new(0, 1, 4), new(1, 5, 4),
            new(2, 6, 3), new(3, 6, 7), new(0, 4, 2), new(2, 4, 6), new(1, 3, 5), new(3, 7, 5),
        };
        var shape = new BSTriShape { Name = new("render"), Flags_ui = 0xEu, Scale = 1f };
        shape.Create(version, v, t, new(), new());
        shape.IsSkinned = false;
        node.Children.AddBlockRef(f.AddBlock(shape));
        if (collision) NifBoxFixture.Attach(f, new bhkBoxShape { Dimensions = new Vector3(half / 69.99f) });
        return NifBoxFixture.Save(f);
    }

    public static Written Write(string dir)
    {
        var d = Path.Combine(dir, "pp"); Directory.CreateDirectory(d);
        var paths = new List<string>();
        string Save(SkyrimMod mod, params ISkyrimModGetter[] masters)
        {
            var path = Path.Combine(d, mod.ModKey.FileName.String);
            mod.BeginWrite.ToPath(path).WithLoadOrder(masters).Write();
            paths.Add(path);
            return path;
        }
        SkCell Room(SkyrimMod m, FormKey ck, string edid, params PlacedObject[] refs)
        {
            var c = Int(ck, edid);
            foreach (var r in refs) c.Temporary.Add(r);
            MovedRefFixture.FileInterior(m, c);
            return c;
        }

        // ---- PpBase.esm ----
        var b = new SkyrimMod(BaseKey, Rel);
        b.ModHeader.Flags |= SkyrimModHeader.HeaderFlag.Master;
        b.Statics.Add(new Static(Box, Rel) { EditorID = "PpBox", Model = new Model { File = "pp\\box.nif" } });
        b.Statics.Add(new Static(Slab, Rel) { EditorID = "PpSlab", Model = new Model { File = "pp\\slab.nif" } });
        b.Statics.Add(new Static(Cup, Rel) { EditorID = "PpCup", Model = new Model { File = "pp\\cup.nif" } });
        b.Statics.Add(new Static(StatX, Rel) { EditorID = "PpStatX", Model = new Model { File = "pp\\box.nif" } });
        b.Statics.Add(new Static(MeshB, Rel) { EditorID = "PpMeshB", Model = new Model { File = "pp\\mshb.nif" } });
        b.Statics.Add(new Static(Marker, Rel) { EditorID = "PpMarker" });
        b.Statics.Add(new Static(Huge, Rel)
        {
            EditorID = "PpHuge", Model = new Model { File = "pp\\huge.nif" },
            ObjectBounds = new ObjectBounds { First = new P3Int16(-4200, -4200, -4200), Second = new P3Int16(4200, 4200, 4200) },
        });
        Room(b, IPop, "PpRoomPop");
        Room(b, IOther, "PpRoomOther", Ref(B4, Box, 2000, 500, 0), Ref(Hs1, Slab, 5000, 0, 0));
        Room(b, IOut, "PpRoomOut");
        {
            var top = Top();
            top.Persistent.Add(Ref(PA, Box, 9 * C + 100, 2000, 0));
            top.Persistent.Add(Ref(Mk, Marker, 30 * C + 100, 100, 0));
            var e50 = Ext(E50, 5, 0); e50.Temporary.Add(Ref(Hs2, Slab, 5 * C + 500, 500, 0)); e50.Temporary.Add(Ref(C2, Cup, 5 * C + 500, 500, 120));
            var e600 = Ext(E600, 60, 0); e600.Temporary.Add(Ref(Hs3, Slab, 60 * C + 500, 500, 0)); e600.Temporary.Add(Ref(C3, Cup, 60 * C + 500, 500, 120));
            b.Worldspaces.Add(Wrld(top, Ext(E00, 0, 0), Ext(E10, 1, 0), e50, Ext(E90, 9, 0), Ext(E200, 20, 0), e600));
        }
        var bPath = Save(b);
        using var bOv = SkyrimMod.CreateFromBinaryOverlay(bPath, Rel);

        // ---- PpA.esp ----
        var a = new SkyrimMod(AKey, Rel);
        Room(a, IPop, "PpRoomPop", Ref(A1, Box, 0, 0, 0));
        Room(a, IOut, "PpRoomOut", Ref(A2, Box, 0, 0, 0));
        Room(a, IOther, "PpRoomOther", Ref(A3, Box, 1050, 30, 20), Ref(A4, Box, 2050, 30, 20));
        {
            var top = Top(); top.Persistent.Add(Ref(PA2, Box, 9 * C + 1000, 100, 0));
            var e10 = Ext(E10, 1, 0); e10.Temporary.Add(Ref(HA, Huge, 4200, 100, 0));
            var e90 = Ext(E90, 9, 0);
            e90.Temporary.Add(Ref(X7, StatX, 9 * C + 2000, 100, 0)); e90.Temporary.Add(Ref(M8, MeshB, 9 * C + 3000, 100, 0));
            e90.Temporary.Add(Ref(A9, Box, 9 * C + 3500, 100, 0));
            var e200 = Ext(E200, 20, 0); e200.Temporary.Add(Ref(Kc, Box, 20 * C + 100, 100, 0, Mk));
            a.Worldspaces.Add(Wrld(top, e10, e90, e200));
        }
        Save(a, bOv);

        // ---- PpB.esp ----
        var bb = new SkyrimMod(BKey, Rel);
        Room(bb, IPop, "PpRoomPop", Ref(B1, Box, 50, 30, 20));
        Room(bb, IOut, "PpRoomOut", Ref(B2, Box, 50, 30, 20));
        {
            var e10 = Ext(E10, 1, 0); e10.Temporary.Add(Ref(BE, Box, 5000, 100, 0));
            var e90 = Ext(E90, 9, 0);
            e90.Temporary.Add(Ref(BP, Box, 9 * C + 350, 2000, 0)); e90.Temporary.Add(Ref(BP2, Box, 9 * C + 1050, 130, 20));
            e90.Temporary.Add(Ref(B7, Box, 9 * C + 2120, 100, 0)); e90.Temporary.Add(Ref(B8, Box, 9 * C + 3080, 100, 0));
            e90.Temporary.Add(Ref(B9, Box, 9 * C + 3550, 130, 20));
            var e200 = Ext(E200, 20, 0); e200.Temporary.Add(Ref(B6, Box, 20 * C + 150, 130, 20));
            bb.Worldspaces.Add(Wrld(null, e10, e90, e200));
        }
        Save(bb, bOv);

        // ---- PpMover.esp: moves the three slabs 2000 u along x ----
        var mv = new SkyrimMod(MoverKey, Rel);
        Room(mv, IOther, "PpRoomOther", Ref(Hs1, Slab, 7000, 0, 0));
        {
            var e50 = Ext(E50, 5, 0); e50.Temporary.Add(Ref(Hs2, Slab, 5 * C + 2500, 500, 0));
            var e600 = Ext(E600, 60, 0); e600.Temporary.Add(Ref(Hs3, Slab, 60 * C + 2500, 500, 0));
            mv.Worldspaces.Add(Wrld(null, e50, e600));
        }
        Save(mv, bOv);

        // ---- PpDelta.esp ----
        var dl = new SkyrimMod(DeltaKey, Rel);
        dl.Statics.Add(new Static(StatX, Rel) { EditorID = "PpStatX", Model = new Model { File = "pp\\big.nif" } });
        Room(dl, IPop, "PpRoomPop", Ref(D0, Box, 3000, 0, 0));
        Room(dl, IOther, "PpRoomOther", Ref(D3, Box, 1000, 0, 0), Ref(B4, Box, 2000, 0, 0), Ref(Dc1, Cup, 5000, 0, 120));
        {
            var top = Top();
            top.Persistent.Add(Ref(PA, Box, 9 * C + 330, 2000, 0));
            var mk = Ref(Mk, Marker, 30 * C + 100, 100, 0);
            mk.MajorRecordFlagsRaw |= 0x800;                       // initially disabled: its enable child Kc leaves
            top.Persistent.Add(mk);
            var e00 = Ext(E00, 0, 0); e00.Temporary.Add(Ref(DE, Box, 100, 100, 0));
            var e50 = Ext(E50, 5, 0); e50.Temporary.Add(Ref(Ds2, Slab, 5 * C + 500, 500, 0));
            dl.Worldspaces.Add(Wrld(top, e00, e50));
        }
        Save(dl, bOv);

        // ---- meshes: one folder per plugin stem (the base's meshes under PpBase), and PpMeshMod ----
        var mods = Path.Combine(d, "mods");
        foreach (var n in Order) Directory.CreateDirectory(Path.Combine(mods, Path.GetFileNameWithoutExtension(n)));
        var bm = Path.Combine(mods, "PpBase", "meshes", "pp"); Directory.CreateDirectory(bm);
        File.WriteAllBytes(Path.Combine(bm, "box.nif"), Mesh(50, true));
        File.WriteAllBytes(Path.Combine(bm, "slab.nif"), Mesh(100, true));
        File.WriteAllBytes(Path.Combine(bm, "cup.nif"), Mesh(20, true));
        File.WriteAllBytes(Path.Combine(bm, "big.nif"), Mesh(100, true));
        File.WriteAllBytes(Path.Combine(bm, "mshb.nif"), Mesh(20, true));
        File.WriteAllBytes(Path.Combine(bm, "huge.nif"), Mesh(4200, false));
        var mm = Path.Combine(mods, MeshModName, "meshes", "pp"); Directory.CreateDirectory(mm);
        File.WriteAllBytes(Path.Combine(mm, "mshb.nif"), Mesh(60, true));
        return new Written(paths, mods);
    }

    /// <summary>A population file naming PpDelta.esp and the PpMeshMod folder, as evalrun writes it.</summary>
    public static string WritePopulation(Written w, string path)
    {
        var delta = w.Order.First(p => Path.GetFileName(p) == "PpDelta.esp");
        using var s = File.OpenRead(delta);
        string sha = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(s)).ToLowerInvariant();
        string Esc(string x) => x.Replace("\\", "\\\\");
        File.WriteAllText(path, "{\n \"schema\": 1,\n \"plugins\": [{\"name\": \"PpDelta.esp\", \"path\": \"" + Esc(delta) + "\", \"sha256\": \"" + sha + "\"}],\n"
                              + " \"mods\": [{\"name\": \"" + MeshModName + "\", \"path\": \"" + Esc(Path.Combine(w.ModsDir, MeshModName)) + "\"}],\n"
                              + " \"targets\": [\"PpDelta.esp\"]\n}\n");
        return path;
    }
}
