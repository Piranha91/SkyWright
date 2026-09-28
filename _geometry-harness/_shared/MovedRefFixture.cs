using System.Text;
using Mutagen.Bethesda;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Plugins.Records;
using Mutagen.Bethesda.Skyrim;
using Noggog;
using SkCell = Mutagen.Bethesda.Skyrim.Cell;

namespace HarnessShared;

// THE MOVED/DELETED-REFERENCE FIXTURE: one small synthetic load order, written with Mutagen, that every "which version
// of a reference wins" site is tested against (FloorFeed, the probes' winning-state walks, the core LAND read).
// Test-only; no game data. The expectations are NOT computed here: each test states its own, worked out by hand from
// the table below. The one shared expectation, the floor neighbourhood of grid (0,0), is a literal (ExpectedFloor00).
//
// Load order: MrMaster.esm, MrA.esp, MrB.esp, MrX.esp, MrL.esp. World: worldspace W (persistent cell T at grid (0,0); exteriors
// E00 (0,0), E10 (1,0), E50 (5,0), Em10 (-1,0), E20 (2,0), E11 (1,1)); interiors I1, I2; a second worldspace W2 (persistent cell, and a cell
// at grid (0,0), so the same XY lies in two worlds). Bases: a modeled static S, a plane-marker base P (no model), NPC
// bases N1 and N2.
//
//   id    master                    later plugins                                        last sighting
//   M1    E00 (100,100)             A: E50                                               E50
//   M2    E00 (110,100)             A: E10 (4200,100)                                    E10
//   M3    E00 (120,100)             A: interior I2                                       I2
//   M4    E00 (130,100)             A: W2's cell at the same XY                          W2
//   M5    E00 (140,100) temporary   A: persistent in T at (1400,1400)                    T, persistent
//   M6    E00 (150,100)             A: E50; B: E00 at (1500,1500)                        E00 (1500,1500)
//   M7    E00 (220,100)             A: filed in E50's CELL at position (700,700)         E50 cell, position in grid (0,0)
//   D1    E00 (160,100)             A: deleted, no placement data                        gone
//   D2    E10 (4300,100)            X: deleted, in an E10 copy holding nothing else      gone
//   D3    E00 (170,100)             A: deleted WITH placement data, filed in E10 (4400,200)  deleted (flag), E10
//   D4    E00 (180,100)             A: deleted, no data; B: placed in E50                E50
//   B0    E00 (190,100)             A: in E10 at (4500,100) with no base                 E10, no base
//   C1    E00 (200,100)             A: E00 (1200,1100)                                   E00 (control)
//   C2    E00 (210,100)             untouched                                            E00 (control)
//   P1    E00 (300,300), base P     A: base S, same place                                E00, not a plane
//   P2    E00 (310,300), base P     A: W2's cell, base P                                 W2
//   P3    E00 (320,300), base P     untouched                                            E00, a plane (control)
//   NPC1  PlacedNpc N1, E00 (400,400)  A: base N2                                        E00, base N2
//   NPC2  PlacedNpc N2, E10 (4600,400) A: deleted, no data                               gone
//   NPC3  PlacedNpc N1, E50 (20780,300) A: same place, base cleared                      no base (N1 not placed by it)
//   NPC4  PlacedNpc N2, E50 (20800,300) A: same place, deleted WITH data (flag set in bytes)  deleted (flag), E50
//   U4-5  (introduced by A in E50 at (20880/20890,400), base S)  B: U4 re-based to the marker M; U5 base cleared
//   U1-3  (introduced by A in E00 at (500..520,500), base S)  B: U1 to E50; U2 deleted, no data; U3 to E00 (600,600)
//   Q1    persistent in T at (20530,50) (grid 5,0)   untouched                           T (control, far)
//
//   LAND  E00: master offset 0; L (last) offset 10 with its VHGT cut to 100 bytes, unreadable   L1
//         E10: master offset 0; A's and X's E10 copies carry no LAND                       L2 (master heights stand)
//         E50: master offset 0; A's copy no LAND; B offset 20, readable                    L3 (B's heights, 160 u)
//         Em10: master offset 0 (a cell with nothing but its LAND); A's copy: a LAND whose DATA flag says it has
//               heights (VertexNormalsHeightMap) and no VHGT: it CLAIMS heights it lacks, unreadable   L4-claimed
//         E11 (1,1): master offset 0; A's copy: a LAND with that flag clear and no VHGT: it DECLARES no heights (the
//               engine draws no ground there and has no terrain collision, Q5 playtest 2026-09-27)   L4-declared
//         E20: master offset 0 and one ref (L5, at (8292,100)); B's copy carries ONLY a readable LAND, offset 20 L5
//   NAVM (each named by its first vertex's X, which differs per version; one triangle each)
//   NVM   E00 (x 1000)             A: filed in E50 (x 21000)                            E50, A's
//   NVD   E00 (x 1100)             A: deleted, no data                                  gone
//   NVK   E00 (x 1200)             A: deleted WITH data (x 1210, flag set in bytes)     gone
//   NVX   E11 (x 5000)             X: deleted, no data, in an E11 copy holding nothing else  gone
//   NVR   E00 (x 1400)             A: deleted, no data; B: E00 again (x 1420)           E00, B's
//   NVC   E00 (x 1500)             A: E00 (x 1510)                                      E00, A's (control)
//   NVI   E00 (x 1600)             A: filed in interior I2 (x 1610)                     I2, A's
//   NVW   E00 (x 1700)             A: filed in W2's cell at the same grid (x 1710)       W2E00, A's
//   L1 lives in its own last plugin, so the cut VHGT is the end of the file: the read then throws. A VHGT cut the same way
//   in the middle of a plugin is read SILENTLY by Mutagen (measured 2026-09-27 on this fixture: heights of 5,128 and
//   4,776 u from the bytes that follow, no exception), which no catch can see.
public static class MovedRefFixture
{
    public const SkyrimRelease Rel = SkyrimRelease.SkyrimSE;
    public static readonly ModKey MasterKey = new("MrMaster", ModType.Master);
    public static readonly ModKey AKey = new("MrA", ModType.Plugin), BKey = new("MrB", ModType.Plugin), XKey = new("MrX", ModType.Plugin),
        LKey = new("MrL", ModType.Plugin);
    static FormKey Mk(uint id) => new(MasterKey, id);

    public static readonly FormKey S = Mk(0x801), P = Mk(0x805), N1 = Mk(0x806), N2 = Mk(0x807), Race = Mk(0x808), Marker = Mk(0x809);
    public static readonly FormKey W = Mk(0x830), T = Mk(0x831), E00 = Mk(0x840), E10 = Mk(0x841), E50 = Mk(0x842), Em10 = Mk(0x843);
    public static readonly FormKey I1 = Mk(0x850), I2 = Mk(0x851);
    public static readonly FormKey W2 = Mk(0x860), W2Top = Mk(0x861), W2E00 = Mk(0x862);
    public static readonly FormKey E20 = Mk(0x844), E11 = Mk(0x845), Ltex = Mk(0x802), Land11 = Mk(0x885);
    public static readonly FormKey Land00 = Mk(0x880), Land10 = Mk(0x881), Land50 = Mk(0x882), Landm10 = Mk(0x883), Land20 = Mk(0x884);

    public static readonly FormKey M1 = Mk(0x901), M2 = Mk(0x902), M3 = Mk(0x903), M4 = Mk(0x904), M5 = Mk(0x905), M6 = Mk(0x906),
        M7 = Mk(0x90E), D1 = Mk(0x907), D2 = Mk(0x908), D3 = Mk(0x909), D4 = Mk(0x90A), B0 = Mk(0x90B), C1 = Mk(0x90C), C2 = Mk(0x90D),
        P1 = Mk(0x910), P2 = Mk(0x911), P3 = Mk(0x912), NPC1 = Mk(0x913), NPC2 = Mk(0x914), NPC3 = Mk(0x915), NPC4 = Mk(0x916), Q1 = Mk(0x920), L5 = Mk(0x921);
    public static readonly FormKey NVM = Mk(0x960), NVD = Mk(0x961), NVK = Mk(0x962), NVX = Mk(0x963), NVR = Mk(0x964),
        NVC = Mk(0x965), NVI = Mk(0x966), NVW = Mk(0x967);

    /// <summary>Every navmesh the fixture places, by its table id.</summary>
    public static readonly IReadOnlyDictionary<string, FormKey> Navs = new Dictionary<string, FormKey>
    {
        ["NVM"] = NVM, ["NVD"] = NVD, ["NVK"] = NVK, ["NVX"] = NVX, ["NVR"] = NVR, ["NVC"] = NVC, ["NVI"] = NVI, ["NVW"] = NVW,
    };
    public static readonly FormKey U1 = new(AKey, 0x950), U2 = new(AKey, 0x951), U3 = new(AKey, 0x952), U4 = new(AKey, 0x953), U5 = new(AKey, 0x954);

    /// <summary>Every reference the fixture places, by its table id.</summary>
    public static readonly IReadOnlyDictionary<string, FormKey> Refs = new Dictionary<string, FormKey>
    {
        ["M1"] = M1, ["M2"] = M2, ["M3"] = M3, ["M4"] = M4, ["M5"] = M5, ["M6"] = M6, ["M7"] = M7,
        ["D1"] = D1, ["D2"] = D2, ["D3"] = D3, ["D4"] = D4, ["B0"] = B0, ["C1"] = C1, ["C2"] = C2,
        ["P1"] = P1, ["P2"] = P2, ["P3"] = P3, ["NPC1"] = NPC1, ["NPC2"] = NPC2, ["NPC3"] = NPC3, ["NPC4"] = NPC4, ["U1"] = U1, ["U2"] = U2, ["U3"] = U3, ["U4"] = U4, ["U5"] = U5, ["Q1"] = Q1, ["L5"] = L5,
    };

    /// <summary>Hand-derived: the floor candidates of worldspace W whose position lies in the 3x3 around grid (0,0)
    /// (x and y in [-4096, 8192)), each exactly once, at its last sighting's position. A candidate is a placed reference
    /// that is not deleted and has a base. Worked from the table: M1, M3, M4, D1, D2, D4, P2, NPC2, U1, U2 are elsewhere
    /// or gone; D3 is deleted; B0 has no base; Q1 stands in grid (5,0).</summary>
    public static readonly IReadOnlyDictionary<FormKey, (float X, float Y)> ExpectedFloor00 = new Dictionary<FormKey, (float, float)>
    {
        [M2] = (4200, 100), [M5] = (1400, 1400), [M6] = (1500, 1500), [M7] = (700, 700), [C1] = (1200, 1100), [C2] = (210, 100),
        [P1] = (300, 300), [P3] = (320, 300), [NPC1] = (400, 400), [U3] = (600, 600),
    };

    /// <summary>World heights of the fixture's LAND by grid: offset x 8 (the gradients are all zero).</summary>
    public const float MasterHeight = 0f, BHeight50 = 160f;

    static PlacedObject Ref(FormKey key, FormKey? baseKey, float x, float y)
    {
        var r = new PlacedObject(key, Rel) { Placement = new Placement { Position = new P3Float(x, y, 0), Rotation = new P3Float(0, 0, 0) } };
        if (baseKey is { } b) r.Base.SetTo(b);
        return r;
    }
    static PlacedNpc Npc(FormKey key, FormKey npc, float x, float y)
    {
        var r = new PlacedNpc(key, Rel) { Placement = new Placement { Position = new P3Float(x, y, 0), Rotation = new P3Float(0, 0, 0) } };
        r.Base.SetTo(npc);
        return r;
    }
    static PlacedObject Deleted(FormKey key) => new(key, Rel) { IsDeleted = true };
    /// <summary>A one-triangle navmesh whose first vertex stands at (x, y): exterior (parent worldspace and grid) or
    /// interior (parent cell).</summary>
    static NavigationMesh Nav(FormKey key, float x, float y, FormKey? ws, int gx = 0, int gy = 0, FormKey? cell = null)
    {
        var d = new NavigationMeshData
        {
            NavmeshVersion = 12, CrcHash = 0xA5E9A03C,
            Parent = ws is { } w
                ? new WorldspaceNavmeshParent { Parent = new FormLink<IWorldspaceGetter>(w), Coordinates = new P2Int16((short)gx, (short)gy) }
                : new CellNavmeshParent { Parent = new FormLink<ICellGetter>(cell!.Value) },
            Min = new P3Float(x, y, 0), Max = new P3Float(x + 100, y + 100, 0),
        };
        d.Vertices.Add(new P3Float(x, y, 0)); d.Vertices.Add(new P3Float(x + 100, y, 0)); d.Vertices.Add(new P3Float(x, y + 100, 0));
        d.Triangles.Add(new NavmeshTriangle { Vertices = new P3Int16(0, 1, 2), EdgeLink_0_1 = -1, EdgeLink_1_2 = -1, EdgeLink_2_0 = -1 });
        return new NavigationMesh(key, Rel) { Data = d };
    }
    static NavigationMesh DeletedNav(FormKey key) => new(key, Rel) { IsDeleted = true };
    static PlacedNpc DeletedNpc(FormKey key) => new(key, Rel) { IsDeleted = true };
    static SkCell Ext(FormKey key, int x, int y) => new(key, Rel) { Flags = default, Grid = new CellGrid { Point = new P2Int(x, y) } };
    static SkCell Int(FormKey key) => new(key, Rel) { Flags = SkCell.Flag.IsInteriorCell };
    static Landscape LandOf(FormKey key, float offset, bool withHeights = true, int alphaLayers = -1, Landscape.Flag? flags = null)
    {
        var land = withHeights
            ? new Landscape(key, Rel) { VertexHeightMap = new LandscapeVertexHeightMap { Offset = offset, HeightMap = new Array2d<sbyte>(new sbyte[33, 33]) } }
            : new Landscape(key, Rel);
        if (flags is { } fl) land.Flags = fl;
        if (alphaLayers < 0) return land;
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
    /// <summary>A layer header for quadrant q; the quadrant is set by reflection, as viewprobe's PluginReadSelfTest does.</summary>
    static LayerHeader Header(int q, ushort layer)
    {
        var h = new LayerHeader { LayerNumber = layer };
        var prop = typeof(LayerHeader).GetProperty("Quadrant")!;
        prop.SetValue(h, Enum.ToObject(prop.PropertyType, q));
        return h;
    }

    static void FileInterior(SkyrimMod mod, SkCell cell)
    {
        uint id = cell.FormKey.ID;
        int blockN = (int)(id % 10), subN = (int)((id / 10) % 10);
        var block = mod.Cells.Records.FirstOrDefault(b => b.BlockNumber == blockN);
        if (block is null) { block = new CellBlock { BlockNumber = blockN, GroupType = GroupTypeEnum.InteriorCellBlock }; mod.Cells.Records.Add(block); }
        var sub = block.SubBlocks.FirstOrDefault(s => s.BlockNumber == subN);
        if (sub is null) { sub = new CellSubBlock { BlockNumber = subN, GroupType = GroupTypeEnum.InteriorCellSubBlock }; block.SubBlocks.Add(sub); }
        sub.Cells.Add(cell);
    }
    static Worldspace World(FormKey wk, SkCell? top, params SkCell[] exteriors)
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
    static SkCell TopOf() => new(T, Rel) { Flags = default, Grid = new CellGrid { Point = new P2Int(0, 0) } };

    /// <summary>Write the four plugins into <paramref name="dir"/> (created; must be empty or absent) and return their
    /// paths in load order.</summary>
    public static IReadOnlyList<string> Write(string dir)
    {
        Directory.CreateDirectory(dir);
        string Save(SkyrimMod mod, params ISkyrimModGetter[] masters)
        {
            var path = Path.Combine(dir, mod.ModKey.FileName.String);
            mod.BeginWrite.ToPath(path).WithLoadOrder(masters).Write();
            return path;
        }

        // ---- the master ----
        var m = new SkyrimMod(MasterKey, Rel);
        m.Statics.Add(new Static(S, Rel) { EditorID = "MrStatic", Model = new Model { File = "Meshes\\mr\\static.nif" } });
        m.Statics.Add(new Static(P, Rel) { EditorID = "MrPlaneMarker" });
        m.Statics.Add(new Static(Marker, Rel) { EditorID = "MrMarker" });
        m.LandscapeTextures.Add(new LandscapeTexture(Ltex, Rel) { EditorID = "MrLtex" });
        m.Races.Add(new Race(Race, Rel) { EditorID = "MrRace", Flags = Mutagen.Bethesda.Skyrim.Race.Flag.Playable });
        m.Npcs.Add(new Npc(N1, Rel) { EditorID = "MrN1", Height = 1.0f, Race = new FormLinkNullable<IRaceGetter>(Race) });
        m.Npcs.Add(new Npc(N2, Rel) { EditorID = "MrN2", Height = 1.0f, Race = new FormLinkNullable<IRaceGetter>(Race) });
        var top = TopOf();
        top.Persistent.Add(Ref(Q1, S, 5 * 4096 + 50, 50));
        var e00 = Ext(E00, 0, 0);
        foreach (var (k, x) in new[] { (M1, 100f), (M2, 110f), (M3, 120f), (M4, 130f), (M5, 140f), (M6, 150f), (D1, 160f), (D3, 170f),
                                       (D4, 180f), (B0, 190f), (C1, 200f), (C2, 210f), (M7, 220f) })
            e00.Temporary.Add(Ref(k, S, x, 100));
        e00.Temporary.Add(Ref(P1, P, 300, 300)); e00.Temporary.Add(Ref(P2, P, 310, 300)); e00.Temporary.Add(Ref(P3, P, 320, 300));
        e00.Temporary.Add(Npc(NPC1, N1, 400, 400));
        e00.Landscape = LandOf(Land00, 0f);
        foreach (var (k, x) in new[] { (NVM, 1000f), (NVD, 1100f), (NVK, 1200f), (NVR, 1400f), (NVC, 1500f), (NVI, 1600f), (NVW, 1700f) })
            e00.NavigationMeshes.Add(Nav(k, x, 2000, W));
        var e10 = Ext(E10, 1, 0);
        e10.Temporary.Add(Ref(D2, S, 4300, 100));
        e10.Temporary.Add(Npc(NPC2, N2, 4600, 400));
        e10.Landscape = LandOf(Land10, 0f);
        var e50 = Ext(E50, 5, 0); e50.Landscape = LandOf(Land50, 0f);
        e50.Temporary.Add(Npc(NPC3, N1, 5 * 4096 + 300, 300));
        e50.Temporary.Add(Npc(NPC4, N2, 5 * 4096 + 320, 300));
        var em10 = Ext(Em10, -1, 0); em10.Landscape = LandOf(Landm10, 0f);
        var e20 = Ext(E20, 2, 0); e20.Landscape = LandOf(Land20, 0f); e20.Temporary.Add(Ref(L5, S, 2 * 4096 + 100, 100));
        var e11 = Ext(E11, 1, 1); e11.Landscape = LandOf(Land11, 0f);
        e11.NavigationMeshes.Add(Nav(NVX, 5000, 5000, W, 1, 1));
        m.Worldspaces.Add(World(W, top, e00, e10, e50, em10, e20, e11));
        m.Worldspaces.Add(World(W2, new SkCell(W2Top, Rel) { Flags = default }, Ext(W2E00, 0, 0)));
        FileInterior(m, Int(I1)); FileInterior(m, Int(I2));
        var mPath = Save(m);
        using var mOv = SkyrimMod.CreateFromBinaryOverlay(mPath, Rel);

        // ---- A: the moves, the deletions, the re-bases, the introductions ----
        var a = new SkyrimMod(AKey, Rel);
        {
            var t = TopOf(); t.Persistent.Add(Ref(M5, S, 1400, 1400));
            var c00 = Ext(E00, 0, 0);
            c00.Temporary.Add(Deleted(D1)); c00.Temporary.Add(Deleted(D4));
            c00.Temporary.Add(Ref(C1, S, 1200, 1100));
            c00.Temporary.Add(Ref(P1, S, 300, 300));
            c00.Temporary.Add(Npc(NPC1, N2, 400, 400));
            c00.Temporary.Add(Ref(U1, S, 500, 500)); c00.Temporary.Add(Ref(U2, S, 510, 500)); c00.Temporary.Add(Ref(U3, S, 520, 500));
            c00.NavigationMeshes.Add(DeletedNav(NVD)); c00.NavigationMeshes.Add(DeletedNav(NVR));
            c00.NavigationMeshes.Add(Nav(NVK, 1210, 2000, W));          // its Deleted flag is set in the bytes after writing
            c00.NavigationMeshes.Add(Nav(NVC, 1510, 2000, W));
            var c10 = Ext(E10, 1, 0);
            c10.Temporary.Add(Ref(M2, S, 4200, 100));
            c10.Temporary.Add(Ref(D3, S, 4400, 200));                 // its Deleted flag is set in the bytes after writing (D3)
            c10.Temporary.Add(Ref(B0, null, 4500, 100));
            c10.Temporary.Add(DeletedNpc(NPC2));
            var c50 = Ext(E50, 5, 0);
            c50.Temporary.Add(Ref(M1, S, 5 * 4096 + 200, 300)); c50.Temporary.Add(Ref(M6, S, 5 * 4096 + 210, 300));
            c50.Temporary.Add(Ref(M7, S, 700, 700));                   // filed in E50's cell, standing in grid (0,0)
            c50.Temporary.Add(Npc(NPC4, N2, 5 * 4096 + 320, 300));    // its Deleted flag is set in the bytes after writing
            c50.Temporary.Add(Ref(U4, S, 5 * 4096 + 400, 400)); c50.Temporary.Add(Ref(U5, S, 5 * 4096 + 410, 400));
            c50.NavigationMeshes.Add(Nav(NVM, 21000, 2000, W, 5, 0));
            c50.Temporary.Add(new PlacedNpc(NPC3, Rel) { Placement = new Placement { Position = new P3Float(5 * 4096 + 300, 300, 0), Rotation = new P3Float(0, 0, 0) } });   // base cleared
            var cm10 = Ext(Em10, -1, 0); cm10.Landscape = LandOf(Landm10, 0f, withHeights: false, flags: Landscape.Flag.VertexNormalsHeightMap);   // L4-claimed
            var c11 = Ext(E11, 1, 1); c11.Landscape = LandOf(Land11, 0f, withHeights: false, flags: (Landscape.Flag)0);   // L4-declared
            a.Worldspaces.Add(World(W, t, c00, c10, c50, cm10, c11));
            var w2c = Ext(W2E00, 0, 0);
            w2c.Temporary.Add(Ref(M4, S, 130, 100)); w2c.Temporary.Add(Ref(P2, P, 310, 300));
            w2c.NavigationMeshes.Add(Nav(NVW, 1710, 2000, W2));
            a.Worldspaces.Add(World(W2, null, w2c));
            var ci2 = Int(I2); ci2.Temporary.Add(Ref(M3, S, 400, 0)); ci2.NavigationMeshes.Add(Nav(NVI, 1610, 0, null, cell: I2)); FileInterior(a, ci2);
        }
        var aPath = Save(a, mOv);
        // D3: Mutagen writes a record marked deleted WITHOUT its placement, so the flag is set in the written bytes instead,
        // leaving the placement in place: a deletion that still carries data.
        File.WriteAllBytes(aPath, SetRecordFlag(File.ReadAllBytes(aPath), "REFR", D3.ID, 0x20));
        File.WriteAllBytes(aPath, SetRecordFlag(File.ReadAllBytes(aPath), "ACHR", NPC4.ID, 0x20));
        File.WriteAllBytes(aPath, SetRecordFlag(File.ReadAllBytes(aPath), "NAVM", NVK.ID, 0x20));

        // ---- B: back in, placed again, the introductions moved or deleted, a readable LAND ----
        var b = new SkyrimMod(BKey, Rel);
        {
            var c00 = Ext(E00, 0, 0);
            c00.Temporary.Add(Ref(M6, S, 1500, 1500));
            c00.Temporary.Add(Ref(U3, S, 600, 600)); c00.Temporary.Add(Deleted(U2));
            c00.NavigationMeshes.Add(Nav(NVR, 1420, 2000, W));
            var c50 = Ext(E50, 5, 0);
            c50.Temporary.Add(Ref(D4, S, 5 * 4096 + 220, 300)); c50.Temporary.Add(Ref(U1, S, 5 * 4096 + 230, 300));
            c50.Landscape = LandOf(Land50, 20f);
            c50.Temporary.Add(Ref(U4, Marker, 5 * 4096 + 400, 400)); c50.Temporary.Add(Ref(U5, null, 5 * 4096 + 410, 400));
            var c20 = Ext(E20, 2, 0); c20.Landscape = LandOf(Land20, 20f);   // L5: a cell copy carrying only its LAND
            b.Worldspaces.Add(World(W, null, c00, c50, c20));
        }
        // B overrides A's own references (U1-U3), so it masters A as well as the master.
        using var aOv = SkyrimMod.CreateFromBinaryOverlay(aPath, Rel);
        var bPath = Save(b, mOv, aOv);

        // ---- X: a cell copy whose only content is a deletion ----
        var xm = new SkyrimMod(XKey, Rel);
        {
            var c10 = Ext(E10, 1, 0); c10.Temporary.Add(Deleted(D2));
            var c11 = Ext(E11, 1, 1); c11.NavigationMeshes.Add(DeletedNav(NVX));   // NVX: a copy holding only the deletion
            xm.Worldspaces.Add(World(W, null, c10, c11));
        }
        var xPath = Save(xm, mOv);

        // ---- L: E00's LAND overridden, its heightmap cut so it cannot be read (the recipe --plugin-read-selftest measured) ----
        var lm = new SkyrimMod(LKey, Rel);
        { var c00 = Ext(E00, 0, 0); c00.Landscape = LandOf(Land00, 10f, alphaLayers: 2); lm.Worldspaces.Add(World(W, null, c00)); }
        var lPath = Save(lm, mOv);
        File.WriteAllBytes(lPath, Shrink(File.ReadAllBytes(lPath), "LAND", Land00.ID, "VHGT", 100));
        return new[] { mPath, aPath, bPath, xPath, lPath };
    }

    // ---- byte-level damage, independent of Mutagen (the same walk viewprobe's PluginReadSelfTest uses) ----
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

    static (int Off, int Len) Sub(byte[] b, Rec r, string subSig)
    {
        if ((BitConverter.ToUInt32(b, r.Off + 8) & 0x40000) != 0) throw new InvalidOperationException("record is compressed");
        int off = r.Off + 24, end = off + r.DataSize;
        while (off < end)
        {
            string s = Encoding.ASCII.GetString(b, off, 4);
            int len = BitConverter.ToUInt16(b, off + 4);
            if (s == subSig) return (off, len);
            off += 6 + len;
        }
        throw new InvalidOperationException($"{r.Sig} has no {subSig}");
    }

    /// <summary>A copy of plugin bytes whose first REFR claims a 65,535-byte DATA: the plugin OPENS, and reading its
    /// placements throws (the recipe viewprobe's --plugin-read-selftest measured, P2-rec).</summary>
    public static byte[] BreakFirstRefData(byte[] b)
    {
        var r = Records(b).First(x => x.Sig == "REFR");
        var (so, _) = Sub(b, r, "DATA");
        var outp = (byte[])b.Clone();
        BitConverter.GetBytes((ushort)65535).CopyTo(outp, so + 4);
        return outp;
    }

    /// <summary>Set a record-header flag bit on one record.</summary>
    static byte[] SetRecordFlag(byte[] b, string recSig, uint idLow, uint flag)
    {
        var r = Records(b).Single(x => x.Sig == recSig && (x.FormId & 0xFFFFFF) == idLow);
        var outp = (byte[])b.Clone();
        BitConverter.GetBytes(BitConverter.ToUInt32(b, r.Off + 8) | flag).CopyTo(outp, r.Off + 8);
        return outp;
    }

    /// <summary>What the table says the written plugins hold, checked on the files: the cases that depend on how Mutagen
    /// writes (a deletion that keeps its data, a heightmap that throws, a LAND with no VHGT, a reference with no base).</summary>
    public static IEnumerable<(string Name, bool Ok, string Detail)> Preconditions(IReadOnlyList<string> paths)
    {
        using var a = SkyrimMod.CreateFromBinaryOverlay(paths[1], Rel);
        using var l = SkyrimMod.CreateFromBinaryOverlay(paths[4], Rel);
        IPlacedGetter? d3 = null, b0 = null; bool noVhgt = false; string claimed = "absent", declared = "absent";
        static string LandShape(ILandscapeGetter? l) => l is null ? "no LAND"
            : $"VHGT {(l.VertexHeightMap is null ? "none" : "present")}, flag {((l.Flags ?? 0).HasFlag(Landscape.Flag.VertexNormalsHeightMap) ? "set" : "clear")}";
        foreach (var ws in a.Worldspaces.Records)
            foreach (var blk in ws.SubCells) foreach (var sub in blk.Items) foreach (var c in sub.Items)
            {
                foreach (var p in c.Temporary) { if (p.FormKey == D3) d3 = p; if (p.FormKey == B0) b0 = p; }
                if (c.FormKey == Em10) { noVhgt = c.Landscape is not null && c.Landscape.VertexHeightMap is null; claimed = LandShape(c.Landscape); }
                if (c.FormKey == E11) declared = LandShape(c.Landscape);
            }
        yield return ("fixture: A's Em10 LAND claims heights (flag set) and has no VHGT (L4-claimed)", claimed == "VHGT none, flag set", claimed);
        yield return ("fixture: A's E11 LAND declares none (flag clear) and has no VHGT (L4-declared)", declared == "VHGT none, flag clear", declared);
        yield return ("fixture: A's D3 is deleted and keeps its placement", d3 is { IsDeleted: true, Placement: not null },
                      d3 is null ? "absent" : $"deleted {d3.IsDeleted}, placement {(d3.Placement is null ? "none" : "kept")}");
        IPlacedGetter? npc4 = null;
        foreach (var ws in a.Worldspaces.Records)
            foreach (var blk in ws.SubCells) foreach (var sub in blk.Items) foreach (var c in sub.Items)
                foreach (var p in c.Temporary) if (p.FormKey == NPC4) npc4 = p;
        yield return ("fixture: A's NPC4 is deleted and keeps its placement", npc4 is { IsDeleted: true, Placement: not null },
                      npc4 is null ? "absent" : $"deleted {npc4.IsDeleted}, placement {(npc4.Placement is null ? "none" : "kept")}");
        INavigationMeshGetter? nvk = null, nvd = null;
        foreach (var ws in a.Worldspaces.Records)
            foreach (var blk in ws.SubCells) foreach (var sub in blk.Items) foreach (var c in sub.Items)
                foreach (var n in c.NavigationMeshes) { if (n.FormKey == NVK) nvk = n; if (n.FormKey == NVD) nvd = n; }
        yield return ("fixture: A's NVK navmesh is deleted and keeps its geometry", nvk is { IsDeleted: true, Data: not null },
                      nvk is null ? "absent" : $"deleted {nvk.IsDeleted}, data {(nvk.Data is null ? "none" : "kept")}");
        yield return ("fixture: A's NVD navmesh is deleted and carries no geometry", nvd is { IsDeleted: true, Data: null },
                      nvd is null ? "absent" : $"deleted {nvd.IsDeleted}, data {(nvd.Data is null ? "none" : "kept")}");
        yield return ("fixture: A's B0 has no base", b0 is IPlacedObjectGetter po && po.Base.FormKeyNullable is not { IsNull: false }, b0 is null ? "absent" : "present");
        yield return ("fixture: A's Em10 LAND has no VHGT (L4)", noVhgt, "");
        string where = "no throw";
        foreach (var ws in l.Worldspaces.Records)
            foreach (var blk in ws.SubCells) foreach (var sub in blk.Items) foreach (var c in sub.Items)
                if (c.FormKey == E00)
                    try { _ = c.Landscape!.VertexHeightMap!.HeightMap[32, 32]; } catch (Exception ex) { where = ex.GetType().Name; }
        yield return ("fixture: L's E00 heightmap throws when read (L1)", where != "no throw", where);
    }

    /// <summary>Shrink one subrecord's data to newLen bytes, fixing the record's and every enclosing group's size.</summary>
    static byte[] Shrink(byte[] b, string recSig, uint idLow, string subSig, int newLen)
    {
        var r = Records(b).Single(x => x.Sig == recSig && (x.FormId & 0xFFFFFF) == idLow);
        var (so, len) = Sub(b, r, subSig);
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
}
