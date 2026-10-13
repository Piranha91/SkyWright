using Mutagen.Bethesda;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Skyrim;
using Noggog;
using SkCell = Mutagen.Bethesda.Skyrim.Cell;

namespace HarnessShared;

// THE FLOOR-REACH FIXTURE (lane F, 2026-10-12): a small synthetic load order for FloorFeed's reach (RingReach,
// FloorFeed.FloorReach), read through each floor probe's own walk by --moved-ref-selftest (MovedRefFloorCheck.CheckReach).
// Test-only; no game data. Every expectation is a literal worked out by hand from the table below.
//
// Load order: RrMaster.esm, RrA.esp. Worldspace R (root) and RC (a child: Parent = R). Centre square: grid (0,0), whose
// 3x3 spans x and y in [-4096, 8192); its cells R00 and RC00 (a probe's target or wanted
// cell) each hold one SMALL reference at (2048, 2048), F0 and G0, filed by position. Bases (OBND as stated; each has a model):
//   SMALL  (-100,-100,0)..(100,100,20)        BIG  (-6000,-6000,0)..(6000,6000,100)
//   GIANT  (-20000,-20000,0)..(20000,20000,100)   TALL (-50,-50,-6000)..(50,50,6000)
//
//   id   world  held in cell   position          base   rotation   lists                       at (0,0)
//   F1   R      (2,0)          (10240, 2048)     BIG    -          -                           admitted (ring 2, enters)
//   F2   R      (2,0)          (10240, 2048)     SMALL  -          -                           absent (box misses the 3x3)
//   F3   R      (4,0)          (18432, 2048)     GIANT  -          master's R list             admitted (listed, ring 4)
//   F4   R      (0,4)          (2048, 18432)     GIANT  -          -                           absent (unlisted, ring 4)
//   F5   R      (6,0)          (26624, 2048)     GIANT  -          master's R list             absent (ring 6)
//   F6   R      (2,0) master;  A: cell (7,0) at (30720, 2048)   BIG                           absent (moved out)
//   F7   R      (0,2)          (2048, 10240)     BIG    -          -   A: deleted, no data      absent (deleted)
//   F8   R      (2,0)          (100, 100)        SMALL  -          -                           once (filed by position)
//   F9   R      (0,2)          (2048, 10240)     TALL   X = pi/2   -                           admitted (enters only turned)
//   F10  R      (0,-4)         (2048, -14336)    GIANT  -          A's R list (not the master's)  admitted (the union)
//   F11  R      (2,0)          (12400, 2048)     BIG    -          -                           admitted (anchored by its cell, ring 2;
//                                                                                              by position it stands at ring 3)
//   G1   RC     (4,0)          (18432, 2048)     GIANT  -          R's list only (the parent)  absent from RC
//   G2   RC     (0,4)          (2048, 18432)     GIANT  -          RC's own list               admitted in RC
//   F0/G0 R/RC  (0,0)          (2048, 2048)      SMALL  -          -                           once (filed by position)
public static class FloorReachFixture
{
    public const SkyrimRelease Rel = SkyrimRelease.SkyrimSE;
    public static readonly ModKey MasterKey = new("RrMaster", ModType.Master);
    public static readonly ModKey AKey = new("RrA", ModType.Plugin);
    static FormKey Mk(uint id) => new(MasterKey, id);

    public static readonly FormKey Small = Mk(0x801), Big = Mk(0x802), Giant = Mk(0x803), Tall = Mk(0x804);
    public static readonly FormKey R = Mk(0x830), RC = Mk(0x831), R00 = Mk(0x847), RC00 = Mk(0x852);
    public static readonly FormKey F1 = Mk(0x901), F2 = Mk(0x902), F3 = Mk(0x903), F4 = Mk(0x904), F5 = Mk(0x905), F6 = Mk(0x906),
        F7 = Mk(0x907), F8 = Mk(0x908), F9 = Mk(0x909), F10 = Mk(0x90A), F11 = Mk(0x90B), G1 = Mk(0x921), G2 = Mk(0x922),
        F0 = Mk(0x900), G0 = Mk(0x920);

    public static readonly IReadOnlyDictionary<string, FormKey> Refs = new Dictionary<string, FormKey>
    {
        ["F1"] = F1, ["F2"] = F2, ["F3"] = F3, ["F4"] = F4, ["F5"] = F5, ["F6"] = F6, ["F7"] = F7, ["F8"] = F8, ["F9"] = F9,
        ["F10"] = F10, ["F11"] = F11, ["G1"] = G1, ["G2"] = G2, ["F0"] = F0, ["G0"] = G0,
    };

    /// <summary>Hand-derived: grid (0,0)'s neighbourhood in R, the 3x3 by position plus the reach, each exactly once.</summary>
    public static readonly IReadOnlySet<FormKey> ExpectedR00 = new HashSet<FormKey> { F0, F1, F3, F8, F9, F10, F11 };
    /// <summary>Hand-derived: grid (0,0)'s neighbourhood in RC.</summary>
    public static readonly IReadOnlySet<FormKey> ExpectedRC00 = new HashSet<FormKey> { G0, G2 };

    public static string Name(FormKey k) => Refs.FirstOrDefault(kv => kv.Value == k).Key ?? k.ToString();

    static PlacedObject Ref(FormKey key, FormKey baseKey, float x, float y, float rx = 0f)
    {
        var r = new PlacedObject(key, Rel) { Placement = new Placement { Position = new P3Float(x, y, 0), Rotation = new P3Float(rx, 0, 0) } };
        r.Base.SetTo(baseKey);
        return r;
    }
    static SkCell Ext(uint id, ModKey owner, int x, int y) => new(new FormKey(owner, id), Rel) { Flags = default, Grid = new CellGrid { Point = new P2Int(x, y) } };
    static Static Stat(FormKey k, string edid, short x0, short y0, short z0, short x1, short y1, short z1) => new(k, Rel)
    {
        EditorID = edid, Model = new Model { File = $"Meshes\\rr\\{edid}.nif" },
        ObjectBounds = new ObjectBounds { First = new P3Int16(x0, y0, z0), Second = new P3Int16(x1, y1, z1) },
    };
    static Worldspace World(FormKey wk, FormKey? parent, IEnumerable<FormKey> listed, params SkCell[] exteriors)
    {
        var ws = new Worldspace(wk, Rel);
        if (parent is { } p) ws.Parent = new WorldspaceParent { Worldspace = new FormLink<IWorldspaceGetter>(p) };
        var grid = new WorldspaceGridReference { GridPosition = new P2Int16(0, 0) };
        foreach (var k in listed) grid.References.Add(new WorldspaceReference { Position = new P2Int16(0, 0), Reference = new FormLink<IPlacedObjectGetter>(k) });
        if (grid.References.Count > 0) ws.LargeReferences.Add(grid);
        if (exteriors.Length > 0)
        {
            var blk = new WorldspaceBlock { BlockNumberX = 0, BlockNumberY = 0, GroupType = GroupTypeEnum.ExteriorCellBlock };
            var sub = new WorldspaceSubBlock { BlockNumberX = 0, BlockNumberY = 0, GroupType = GroupTypeEnum.ExteriorCellSubBlock };
            foreach (var c in exteriors) sub.Items.Add(c);
            blk.Items.Add(sub); ws.SubCells.Add(blk);
        }
        return ws;
    }

    /// <summary>Write the two plugins into <paramref name="dir"/> (created) and return their paths in load order.</summary>
    public static IReadOnlyList<string> Write(string dir)
    {
        Directory.CreateDirectory(dir);
        string Save(SkyrimMod mod, params ISkyrimModGetter[] masters)
        {
            var path = Path.Combine(dir, mod.ModKey.FileName.String);
            mod.BeginWrite.ToPath(path).WithLoadOrder(masters).Write();
            return path;
        }
        const float C = 4096f, H = 2048f;

        var m = new SkyrimMod(MasterKey, Rel);
        m.Statics.Add(Stat(Small, "RrSmall", -100, -100, 0, 100, 100, 20));
        m.Statics.Add(Stat(Big, "RrBig", -6000, -6000, 0, 6000, 6000, 100));
        m.Statics.Add(Stat(Giant, "RrGiant", -20000, -20000, 0, 20000, 20000, 100));
        m.Statics.Add(Stat(Tall, "RrTall", -50, -50, -6000, 50, 50, 6000));
        var r20 = Ext(0x840, MasterKey, 2, 0);
        r20.Temporary.Add(Ref(F1, Big, 2 * C + H, H)); r20.Temporary.Add(Ref(F2, Small, 2 * C + H, H));
        r20.Temporary.Add(Ref(F6, Big, 2 * C + H, H + 100)); r20.Temporary.Add(Ref(F8, Small, 100, 100));
        r20.Temporary.Add(Ref(F11, Big, 12400, H));
        var r40 = Ext(0x841, MasterKey, 4, 0); r40.Temporary.Add(Ref(F3, Giant, 4 * C + H, H));
        var r04 = Ext(0x842, MasterKey, 0, 4); r04.Temporary.Add(Ref(F4, Giant, H, 4 * C + H));
        var r60 = Ext(0x843, MasterKey, 6, 0); r60.Temporary.Add(Ref(F5, Giant, 6 * C + H, H));
        var r02 = Ext(0x844, MasterKey, 0, 2);
        r02.Temporary.Add(Ref(F7, Big, H, 2 * C + H)); r02.Temporary.Add(Ref(F9, Tall, H + 100, 2 * C + H, MathF.PI / 2f));
        var r0m4 = Ext(0x845, MasterKey, 0, -4); r0m4.Temporary.Add(Ref(F10, Giant, H, -4 * C + H));
        var r00 = Ext(0x847, MasterKey, 0, 0);   // the centre square's cell: a probe's target / wanted cell
        r00.Temporary.Add(Ref(F0, Small, H, H));
        m.Worldspaces.Add(World(R, null, new[] { F3, F5, G1 }, r00, r20, r40, r04, r60, r02, r0m4));
        var c40 = Ext(0x850, MasterKey, 4, 0); c40.Temporary.Add(Ref(G1, Giant, 4 * C + H, H));
        var c04 = Ext(0x851, MasterKey, 0, 4); c04.Temporary.Add(Ref(G2, Giant, H, 4 * C + H));
        var c00 = Ext(0x852, MasterKey, 0, 0); c00.Temporary.Add(Ref(G0, Small, H, H));
        m.Worldspaces.Add(World(RC, R, new[] { G2 }, c00, c40, c04));
        var mPath = Save(m);
        using var mOv = SkyrimMod.CreateFromBinaryOverlay(mPath, Rel);

        var a = new SkyrimMod(AKey, Rel);
        {
            var c20 = Ext(0x840, MasterKey, 2, 0);
            var c70 = Ext(0x846, AKey, 7, 0); c70.Temporary.Add(Ref(F6, Big, 7 * C + H, H));
            var c02 = Ext(0x844, MasterKey, 0, 2); c02.Temporary.Add(new PlacedObject(F7, Rel) { IsDeleted = true });
            a.Worldspaces.Add(World(R, null, new[] { F10 }, c20, c70, c02));
        }
        var aPath = Save(a, mOv);
        return new[] { mPath, aPath };
    }
}
