using System.Numerics;
using Mutagen.Bethesda;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Plugins.Records;
using HousecarlCore;
using Mutagen.Bethesda.Skyrim;
using Noggog;

namespace HarnessShared;

// Synthetic orders for the cell-boundary passes (waterprobe, seamprobe), written with Mutagen. proofprobe judges them
// through CellBoundaryIndex; the probes' --self-test judge them through the probes' own decisions, and a kept copy is
// the fixed input for a probe run. The plugins are NAMED for the truth rule (Skyrim.esm, Update.esm, the USSEP file
// name, DynDOLOD.esp), because PluginKinds decides who a plugin is by its file name.

/// <summary>The water order: Skyrim, Update, USSEP, WaterMod, Port, Later, DynDOLOD, ModX, Later2; and WfFix.esp, a
/// patch that restores C20's water (not in <see cref="Written.Order"/>).</summary>
public static class WaterFixture
{
    public sealed record Written(IReadOnlyList<string> Order, string Fix);

    static readonly SkyrimRelease Rel = SkyrimRelease.SkyrimSE;
    public static readonly ModKey SkyK = new("Skyrim", ModType.Master);
    public static readonly ModKey UpdK = new("Update", ModType.Master);
    public static readonly ModKey UsK = ModKey.FromNameAndExtension(PluginKinds.Ussep);
    public static readonly ModKey WmK = new("WaterMod", ModType.Plugin);
    public static readonly ModKey PortK = new("Port", ModType.Plugin);
    public static readonly ModKey LaterK = new("Later", ModType.Plugin);
    public static readonly ModKey GenK = new("DynDOLOD", ModType.Plugin);
    public static readonly ModKey ModxK = new("ModX", ModType.Plugin);
    public static readonly ModKey Later2K = new("Later2", ModType.Plugin);
    public static readonly ModKey FixK = new("WfFix", ModType.Plugin);
    static FormKey S(uint id) => new(SkyK, id);
    public static readonly FormKey World = S(0x800), Top = S(0x801);
    public static readonly FormKey DefaultWater = S(0x810), RiverWater = S(0x811);
    public static readonly FormKey FlowBlend = new(UpdK, 0x801);
    public static readonly FormKey WmWater = new(WmK, 0x801);
    /// <summary>Grid cell (x, 0) and its LAND.</summary>
    public static FormKey C(int x) => S((uint)(0x900 + x));
    public static FormKey L(int x) => S((uint)(0xA00 + x));
    public static readonly FormKey C01 = S(0x920), L01 = S(0xA20);          // grid cell (0, 1)
    public static readonly FormKey C11 = S(0x921), L11 = S(0xA21);          // grid cell (1, 1), dry land
    public static readonly FormKey C30b = S(0x930), L30b = S(0xA30);        // the duplicate at (3, 0)
    public static readonly FormKey C50 = new(ModxK, 0x801), L50 = new(ModxK, 0x802);   // a mod-defined cell at (5, 0)

    // A flat LAND at world height h: offset h/8, gradients all zero.
    static Landscape Flat(FormKey key, float h, FormKey? baseTex = null)
    {
        var land = new Landscape(key, Rel) { VertexHeightMap = new LandscapeVertexHeightMap { Offset = h / 8f, HeightMap = new Array2d<sbyte>(new sbyte[33, 33]) } };
        if (baseTex is { } bt)
            foreach (var q in new[] { Quadrant.BottomLeft, Quadrant.BottomRight, Quadrant.TopLeft, Quadrant.TopRight })
            { var bl = new BaseLayer { Header = new LayerHeader { Quadrant = q, LayerNumber = 0 } }; bl.Header.Texture.SetTo(bt); land.Layers.Add(bl); }
        return land;
    }
    static Cell Grid(FormKey key, int x, int y, bool hasWater, FormKey? water, float? height, Landscape? land, string? edid = null, Vector3? vel = null, string? noise = null)
    {
        var c = new Cell(key, Rel) { EditorID = edid, Flags = hasWater ? Cell.Flag.HasWater : default, Grid = new CellGrid { Point = new P2Int(x, y) }, WaterHeight = height, Landscape = land };
        if (water is { } w) c.Water.SetTo(w);
        if (vel is { } v) c.WaterVelocity = new CellWaterVelocity { Angle = new P3Float(v.X, v.Y, v.Z), Offset = new P3Float(0, 0, 0) };
        if (noise is not null) c.WaterNoiseTexture = noise;
        return c;
    }
    static Worldspace WorldWith(FormKey wKey, FormKey topK, FormKey? water, float? defaultHeight, params Cell[] cells)
    {
        var ws = new Worldspace(wKey, Rel) { EditorID = "WaterWorld" };
        if (water is { } w) ws.Water.SetTo(w);
        if (defaultHeight is { } dh) ws.LandDefaults = new WorldspaceLandDefaults { DefaultLandHeight = 0, DefaultWaterHeight = dh };
        ws.TopCell = new Cell(topK, Rel) { Flags = default, Grid = new CellGrid { Point = new P2Int(0, 0) } };   // the persistent cell, stamped (0,0) like the CK does
        var blk = new WorldspaceBlock { BlockNumberX = 0, BlockNumberY = 0, GroupType = GroupTypeEnum.ExteriorCellBlock };
        var sub = new WorldspaceSubBlock { BlockNumberX = 0, BlockNumberY = 0, GroupType = GroupTypeEnum.ExteriorCellSubBlock };
        foreach (var c in cells) sub.Items.Add(c);
        blk.Items.Add(sub); ws.SubCells.Add(blk);
        return ws;
    }

    /// <summary>Write the ten plugins into <paramref name="dir"/> (created).</summary>
    public static Written Write(string dir)
    {
        Directory.CreateDirectory(dir);
        var opened = new List<IDisposable>();
        string Save(SkyrimMod mod, params ISkyrimModGetter[] masters)
        {
            var path = Path.Combine(dir, mod.ModKey.FileName.String);
            mod.BeginWrite.ToPath(path).WithLoadOrder(masters).Write();
            return path;
        }
        ISkyrimModGetter Open(string path)
        {
            var m = SkyrimMod.CreateFromBinaryOverlay(path, Rel);
            opened.Add((IDisposable)m);
            return m;
        }
        try
        {
            // Skyrim.esm — the pre-Update state: HasWater, no WATR link, the sentinel; the sea floor at -15000 (visible), C11 dry land.
            var sky = new SkyrimMod(SkyK, Rel);
            sky.Waters.Add(new Water(DefaultWater, Rel) { EditorID = "DefaultWater" });
            sky.Waters.Add(new Water(RiverWater, Rel) { EditorID = "RiverWater" });
            sky.Worldspaces.Add(WorldWith(World, Top, DefaultWater, -14000f,
                Grid(C(0), 0, 0, true, null, float.MaxValue, Flat(L(0), -15000f, DefaultWater), "C00"),
                Grid(C(1), 1, 0, true, null, float.MaxValue, Flat(L(1), -15000f), "C10"),
                Grid(C(2), 2, 0, true, null, -14000f, Flat(L(2), -15000f), "C20"),
                Grid(C(3), 3, 0, true, null, float.MaxValue, Flat(L(3), -15000f), "C30"),
                Grid(C30b, 3, 0, true, null, float.MaxValue, Flat(L30b, -15000f), "C30b"),
                Grid(C(4), 4, 0, true, null, float.MaxValue, Flat(L(4), 0f), "C40"),                 // the control: land above the sea everywhere
                Grid(C(6), 6, 0, true, null, float.MaxValue, Flat(L(6), -15000f), "C60"),
                Grid(C(7), 7, 0, true, null, float.MaxValue, Flat(L(7), -15000f), "C70"),
                Grid(C01, 0, 1, true, null, float.MaxValue, Flat(L01, -15000f), "C01"),
                Grid(C11, 1, 1, false, null, null, Flat(L11, 0f), "C11")));
            var skyPath = Save(sky);
            var skyOv = Open(skyPath);

            // Update.esm — the SE water: a flow-blend WATR, velocity, noise, on every sea cell but C30/C30b (the wrinkle stays vanilla).
            var upd = new SkyrimMod(UpdK, Rel);
            upd.Waters.Add(new Water(FlowBlend, Rel) { EditorID = "DefaultWaterFlowBlend" });
            var vel = new Vector3(1, 0, 0);
            upd.Worldspaces.Add(WorldWith(World, Top, DefaultWater, -14000f,
                Grid(C(0), 0, 0, true, FlowBlend, float.MaxValue, null, "C00", vel, "n.dds"),
                Grid(C(1), 1, 0, true, FlowBlend, float.MaxValue, null, "C10", vel, "n.dds"),
                Grid(C(2), 2, 0, true, FlowBlend, -14000f, null, "C20", vel, "n.dds"),
                Grid(C(4), 4, 0, true, FlowBlend, float.MaxValue, null, "C40", vel, "n.dds"),
                Grid(C(6), 6, 0, true, FlowBlend, float.MaxValue, null, "C60", vel, "n.dds"),
                Grid(C(7), 7, 0, true, FlowBlend, float.MaxValue, null, "C70", vel, "n.dds"),
                Grid(C01, 0, 1, true, FlowBlend, float.MaxValue, null, "C01", vel, "n.dds")));
            var updPath = Save(upd, skyOv);
            var updOv = Open(updPath);

            // USSEP — forwards C00 exactly (no snap; the truth is still Update's tuple).
            var us = new SkyrimMod(UsK, Rel);
            us.Worldspaces.Add(WorldWith(World, Top, DefaultWater, -14000f, Grid(C(0), 0, 0, true, FlowBlend, float.MaxValue, null, "C00", vel, "n.dds")));
            var usPath = Save(us, skyOv, updOv);

            // WaterMod.esp — overrides a vanilla WATR (the rule's first clause) and retypes C01 to a WATR of its own.
            var wm = new SkyrimMod(WmK, Rel);
            wm.Waters.Add(new Water(DefaultWater, Rel) { EditorID = "DefaultWater" });
            wm.Waters.Add(new Water(WmWater, Rel) { EditorID = "WaterModWater" });
            wm.Worldspaces.Add(WorldWith(World, Top, DefaultWater, -14000f, Grid(C01, 0, 1, true, WmWater, float.MaxValue, null, "C01", vel, "n.dds")));
            var wmPath = Save(wm, skyOv, updOv);
            var wmOv = Open(wmPath);

            // Port.esp — an Oldrim port: C10 back to Skyrim.esm's tuple; C20 HasWater cleared; C60 the link nulled; C70 a real height where the sentinel was.
            var port = new SkyrimMod(PortK, Rel);
            port.Worldspaces.Add(WorldWith(World, Top, DefaultWater, -14000f,
                Grid(C(1), 1, 0, true, null, float.MaxValue, null, "C10"),
                Grid(C(2), 2, 0, false, FlowBlend, -14000f, null, "C20", vel, "n.dds"),
                Grid(C(6), 6, 0, true, null, float.MaxValue, null, "C60", vel, "n.dds"),
                Grid(C(7), 7, 0, true, FlowBlend, -14000f, null, "C70", vel, "n.dds")));
            var portPath = Save(port, skyOv, updOv);

            // Later.esp (masters WaterMod) — forwards the water mod's C01 exactly.
            var later = new SkyrimMod(LaterK, Rel);
            later.Worldspaces.Add(WorldWith(World, Top, DefaultWater, -14000f, Grid(C01, 0, 1, true, WmWater, float.MaxValue, null, "C01", vel, "n.dds")));
            var laterPath = Save(later, skyOv, updOv, wmOv);

            // DynDOLOD.esp — a generated plugin winning C40 (forwarded, but unjudgeable by kind).
            var gen = new SkyrimMod(GenK, Rel);
            gen.Worldspaces.Add(WorldWith(World, Top, DefaultWater, -14000f, Grid(C(4), 4, 0, true, FlowBlend, float.MaxValue, null, "C40", vel, "n.dds")));
            var genPath = Save(gen, skyOv, updOv);

            // ModX.esp — defines its own cell at (5,0) with river water; Later2 (below) retypes it.
            var modx = new SkyrimMod(ModxK, Rel);
            modx.Worldspaces.Add(WorldWith(World, Top, DefaultWater, -14000f, Grid(C50, 5, 0, true, RiverWater, float.MaxValue, Flat(L50, -15000f), "C50")));
            var modxPath = Save(modx, skyOv);
            var modxOv = Open(modxPath);
            var later2 = new SkyrimMod(Later2K, Rel);
            later2.Worldspaces.Add(WorldWith(World, Top, DefaultWater, -14000f, Grid(C50, 5, 0, true, DefaultWater, float.MaxValue, null, "C50")));
            var later2Path = Save(later2, skyOv, modxOv);

            // WfFix.esp (masters Skyrim, Update) — the patch: C20 back to Update's tuple exactly, so C20 forwards the truth.
            var fix = new SkyrimMod(FixK, Rel);
            fix.Worldspaces.Add(WorldWith(World, Top, DefaultWater, -14000f, Grid(C(2), 2, 0, true, FlowBlend, -14000f, null, "C20", vel, "n.dds")));
            var fixPath = Save(fix, skyOv, updOv);

            return new Written(new[] { skyPath, updPath, usPath, wmPath, portPath, laterPath, genPath, modxPath, later2Path }, fixPath);
        }
        finally { foreach (var d in opened) d.Dispose(); }
    }
}

/// <summary>The land-seam order: Skyrim, SeamA, SeamB; and SeamC, which masters Skyrim and SeamA and joins two of
/// SeamA's seams (the patch; not in <see cref="Written.Order"/>).</summary>
public static class SeamFixture
{
    public sealed record Written(IReadOnlyList<string> Order, string Fix);

    static readonly SkyrimRelease Rel = SkyrimRelease.SkyrimSE;
    public static readonly ModKey SkyK = new("Skyrim", ModType.Master);
    public static readonly ModKey AK = new("SeamA", ModType.Plugin);
    public static readonly ModKey BK = new("SeamB", ModType.Plugin);
    public static readonly ModKey CK = new("SeamC", ModType.Plugin);
    static FormKey S(uint id) => new(SkyK, id);
    public static readonly FormKey World = S(0x800), Top = S(0x801);
    public static readonly FormKey TexA = S(0x810), TexB = S(0x811);
    /// <summary>Grid cell (x, y) and its LAND.</summary>
    public static FormKey C(int x, int y) => S((uint)(0x900 + y * 16 + x));
    public static FormKey L(int x, int y) => S((uint)(0xA00 + y * 16 + x));

    static Landscape Land(FormKey key, float h, FormKey tex, Action<sbyte[,]>? shape = null)
    {
        var g = new sbyte[33, 33];
        shape?.Invoke(g);
        var land = new Landscape(key, Rel) { VertexHeightMap = new LandscapeVertexHeightMap { Offset = h / 8f, HeightMap = new Array2d<sbyte>(g) } };
        foreach (var q in new[] { Quadrant.BottomLeft, Quadrant.BottomRight, Quadrant.TopLeft, Quadrant.TopRight })
        { var bl = new BaseLayer { Header = new LayerHeader { Quadrant = q, LayerNumber = 0 } }; bl.Header.Texture.SetTo(tex); land.Layers.Add(bl); }
        return land;
    }
    static Cell Grid(FormKey key, int x, int y, Landscape land, string edid)
        => new(key, Rel) { EditorID = edid, Flags = default, Grid = new CellGrid { Point = new P2Int(x, y) }, Landscape = land };
    static Worldspace WorldWith(FormKey wKey, FormKey topK, params Cell[] cells)
    {
        var ws = new Worldspace(wKey, Rel) { EditorID = "SeamWorld" };
        ws.TopCell = new Cell(topK, Rel) { Flags = default };
        var blk = new WorldspaceBlock { BlockNumberX = 0, BlockNumberY = 0, GroupType = GroupTypeEnum.ExteriorCellBlock };
        var sub = new WorldspaceSubBlock { BlockNumberX = 0, BlockNumberY = 0, GroupType = GroupTypeEnum.ExteriorCellSubBlock };
        foreach (var c in cells) sub.Items.Add(c);
        blk.Items.Add(sub); ws.SubCells.Add(blk);
        return ws;
    }
    // Raise the INTERIOR of the east edge (x = 32, rows 1..31) by `units` heightmap units: a gradient at x=32 on those
    // rows. The corner vertices are left alone on purpose — a corner is shared by four cells, so a fixture that moved
    // one would put a step on two edges at once and the cases could not name whose seam is whose.
    static Action<sbyte[,]> EastEdge(sbyte units) => g => { for (int y = 1; y < 32; y++) g[32, y] = units; };
    // Raise the interior of the west edge: rows 1..31 start `units` higher (column 0 accumulates down the rows: +units
    // at row 1, back at row 32), and each of those rows steps back down at x = 1.
    static Action<sbyte[,]> WestEdge(sbyte units) => g => { g[0, 1] = units; g[0, 32] = (sbyte)-units; for (int y = 1; y < 32; y++) g[1, y] = (sbyte)-units; };

    /// <summary>Write the four plugins into <paramref name="dir"/> (created).</summary>
    public static Written Write(string dir)
    {
        Directory.CreateDirectory(dir);
        var opened = new List<IDisposable>();
        string Save(SkyrimMod mod, params ISkyrimModGetter[] masters)
        {
            var path = Path.Combine(dir, mod.ModKey.FileName.String);
            mod.BeginWrite.ToPath(path).WithLoadOrder(masters).Write();
            return path;
        }
        ISkyrimModGetter Open(string path)
        {
            var m = SkyrimMod.CreateFromBinaryOverlay(path, Rel);
            opened.Add((IDisposable)m);
            return m;
        }
        try
        {
            // Skyrim.esm: a 3×2 block of flat cells at 0, matching edges everywhere, except a vanilla seam between (2,0) and
            // (2,1) (the north edge of (2,0) 16 u up — Bethesda ships one). Cells (4,0)-(9,0), flat, carry the probe cases.
            var sky = new SkyrimMod(SkyK, Rel);
            sky.Worldspaces.Add(WorldWith(World, Top,
                Grid(C(0, 0), 0, 0, Land(L(0, 0), 0, TexA), "S00"), Grid(C(1, 0), 1, 0, Land(L(1, 0), 0, TexA), "S10"),
                Grid(C(2, 0), 2, 0, Land(L(2, 0), 0, TexA, g => { g[1, 32] = 2; g[32, 32] = -2; }), "S20"),   // row 32, x = 1..31, 16 u up: a vanilla seam, corners untouched
                Grid(C(0, 1), 0, 1, Land(L(0, 1), 0, TexA), "S01"), Grid(C(1, 1), 1, 1, Land(L(1, 1), 0, TexB), "S11"),
                Grid(C(2, 1), 2, 1, Land(L(2, 1), 0, TexA), "S21"),
                Grid(C(4, 0), 4, 0, Land(L(4, 0), 0, TexA), "S40"), Grid(C(5, 0), 5, 0, Land(L(5, 0), 0, TexA), "S50"),
                Grid(C(6, 0), 6, 0, Land(L(6, 0), 0, TexA), "S60"), Grid(C(7, 0), 7, 0, Land(L(7, 0), 0, TexA), "S70"),
                Grid(C(8, 0), 8, 0, Land(L(8, 0), 0, TexA), "S80"), Grid(C(9, 0), 9, 0, Land(L(9, 0), 0, TexA), "S90")));
            var skyPath = Save(sky);
            var skyOv = Open(skyPath);

            // SeamA (masters Skyrim): re-shapes (0,0) AND (1,0) consistently — the shared edge at +2 units (16 u) on both sides;
            // and (0,1)'s east edge alone (+4 units, 32 u) against a (1,1) it left alone — its own seam, Seen.
            // The probe cases: (4,0)'s east edge +1 unit (8 u, exactly eps: no seam); (6,0)'s east edge +4 units against
            // vanilla (7,0) (Seen, 32 u); (8,0) a LAND with no heightmap beside vanilla (9,0).
            var a = new SkyrimMod(AK, Rel);
            a.Worldspaces.Add(WorldWith(World, Top,
                Grid(C(0, 0), 0, 0, Land(L(0, 0), 0, TexA, EastEdge(2)), "S00"),
                Grid(C(1, 0), 1, 0, Land(L(1, 0), 0, TexA, WestEdge(2)), "S10"),
                Grid(C(0, 1), 0, 1, Land(L(0, 1), 0, TexA, EastEdge(4)), "S01"),
                Grid(C(4, 0), 4, 0, Land(L(4, 0), 0, TexA, EastEdge(1)), "S40"),
                Grid(C(6, 0), 6, 0, Land(L(6, 0), 0, TexA, EastEdge(4)), "S60"),
                Grid(C(8, 0), 8, 0, new Landscape(L(8, 0), Rel), "S80")));
            var aPath = Save(a, skyOv);
            var aOv = Open(aPath);

            // SeamB (masters Skyrim only — blind to A): re-shapes (1,0) from the VANILLA base: west edge back to 0, its own interior
            // texture changed to texB. Loads after A. Under B's view (1,0)'s west edge matches vanilla (0,0) (Δ 0); together: 16 u.
            var b = new SkyrimMod(BK, Rel);
            b.Worldspaces.Add(WorldWith(World, Top, Grid(C(1, 0), 1, 0, Land(L(1, 0), 0, TexB), "S10")));
            var bPath = Save(b, skyOv);

            // SeamC (masters Skyrim and A): re-cuts (1,1)'s west edge to +4 units to meet A's (0,1) — the patch shape (joins the seam);
            // re-states (2,1) with the vanilla seam against (2,0) untouched — Inherited; and re-cuts (7,0)'s west edge to +3 units
            // against A's +4 at (6,0): 8 u left, exactly eps.
            var c = new SkyrimMod(CK, Rel);
            c.Worldspaces.Add(WorldWith(World, Top,
                Grid(C(1, 1), 1, 1, Land(L(1, 1), 0, TexB, WestEdge(4)), "S11"),
                Grid(C(2, 1), 2, 1, Land(L(2, 1), 0, TexB), "S21"),
                Grid(C(7, 0), 7, 0, Land(L(7, 0), 0, TexA, WestEdge(3)), "S70")));
            var cPath = Save(c, skyOv, aOv);

            return new Written(new[] { skyPath, aPath, bPath }, cPath);
        }
        finally { foreach (var d in opened) d.Dispose(); }
    }
}

/// <summary>The repeat order: Skyrim, RepA, RepB, RepC, RepD. RepC masters Skyrim and RepA but not RepB, and restates
/// RepB's LAND at (0,0) byte for byte; RepD masters RepB and restates RepB's (3,0) — the ordinary forward.</summary>
public static class LandRepeatFixture
{
    static readonly SkyrimRelease Rel = SkyrimRelease.SkyrimSE;
    public static readonly ModKey SkyK = new("Skyrim", ModType.Master);
    public static readonly ModKey AK = new("RepA", ModType.Plugin);
    public static readonly ModKey BK = new("RepB", ModType.Plugin);
    public static readonly ModKey CK = new("RepC", ModType.Plugin);
    public static readonly ModKey DK = new("RepD", ModType.Plugin);
    static FormKey S(uint id) => new(SkyK, id);
    public static readonly FormKey World = S(0x800), Top = S(0x801);
    public static readonly FormKey TexA = S(0x810), TexB = S(0x811);
    /// <summary>Grid cell (x, 0) and its LAND.</summary>
    public static FormKey C(int x) => S((uint)(0x900 + x));
    public static FormKey L(int x) => S((uint)(0xA00 + x));

    static Landscape Land(FormKey key, FormKey tex, Action<sbyte[,]>? shape = null)
    {
        var g = new sbyte[33, 33];
        shape?.Invoke(g);
        var land = new Landscape(key, Rel) { VertexHeightMap = new LandscapeVertexHeightMap { Offset = 0f, HeightMap = new Array2d<sbyte>(g) } };
        foreach (var q in new[] { Quadrant.BottomLeft, Quadrant.BottomRight, Quadrant.TopLeft, Quadrant.TopRight })
        { var bl = new BaseLayer { Header = new LayerHeader { Quadrant = q, LayerNumber = 0 } }; bl.Header.Texture.SetTo(tex); land.Layers.Add(bl); }
        return land;
    }
    static Cell Grid(int x, Landscape land)
        => new(C(x), Rel) { EditorID = $"R{x}0", Flags = default, Grid = new CellGrid { Point = new P2Int(x, 0) }, Landscape = land };
    static Worldspace WorldWith(params Cell[] cells)
    {
        var ws = new Worldspace(World, Rel) { EditorID = "RepeatWorld" };
        ws.TopCell = new Cell(Top, Rel) { Flags = default };
        var blk = new WorldspaceBlock { BlockNumberX = 0, BlockNumberY = 0, GroupType = GroupTypeEnum.ExteriorCellBlock };
        var sub = new WorldspaceSubBlock { BlockNumberX = 0, BlockNumberY = 0, GroupType = GroupTypeEnum.ExteriorCellSubBlock };
        foreach (var c in cells) sub.Items.Add(c);
        blk.Items.Add(sub); ws.SubCells.Add(blk);
        return ws;
    }
    // The interior of the east edge (rows 1..31) and of the west edge raised by `units`; corners untouched (see SeamFixture).
    static Action<sbyte[,]> EastEdge(sbyte units) => g => { for (int y = 1; y < 32; y++) g[32, y] = units; };
    static Action<sbyte[,]> WestEdge(sbyte units) => g => { g[0, 1] = units; g[0, 32] = (sbyte)-units; for (int y = 1; y < 32; y++) g[1, y] = (sbyte)-units; };

    /// <summary>Write the five plugins into <paramref name="dir"/> (created); the paths in load order.</summary>
    public static IReadOnlyList<string> Write(string dir)
    {
        Directory.CreateDirectory(dir);
        var opened = new List<IDisposable>();
        // Every listed master is declared even when no record references it (the writer drops it otherwise): the closure
        // is what the cases are about.
        string Save(SkyrimMod mod, params ISkyrimModGetter[] masters)
        {
            var path = Path.Combine(dir, mod.ModKey.FileName.String);
            mod.BeginWrite.ToPath(path).WithLoadOrder(masters).WithExtraIncludedMasters(masters.Select(m => m.ModKey).ToArray()).Write();
            return path;
        }
        ISkyrimModGetter Open(string path)
        {
            var m = SkyrimMod.CreateFromBinaryOverlay(path, Rel);
            opened.Add((IDisposable)m);
            return m;
        }
        try
        {
            // Skyrim.esm: (0,0), (1,0) and (3,0) flat at 0, texA.
            var sky = new SkyrimMod(SkyK, Rel);
            sky.Worldspaces.Add(WorldWith(Grid(0, Land(L(0), TexA)), Grid(1, Land(L(1), TexA)), Grid(3, Land(L(3), TexA))));
            var skyPath = Save(sky);
            var skyOv = Open(skyPath);

            // RepA (masters Skyrim): raises the shared edge of (0,0) and (1,0) by 2 units (16 u) on both sides: joined.
            var a = new SkyrimMod(AK, Rel);
            a.Worldspaces.Add(WorldWith(Grid(0, Land(L(0), TexA, EastEdge(2))), Grid(1, Land(L(1), TexA, WestEdge(2)))));
            var aPath = Save(a, skyOv);
            var aOv = Open(aPath);

            // RepB (masters Skyrim only): (0,0) flat with texB (vanilla heights, new texture); (3,0) the same.
            var b = new SkyrimMod(BK, Rel);
            b.Worldspaces.Add(WorldWith(Grid(0, Land(L(0), TexB)), Grid(3, Land(L(3), TexB))));
            var bPath = Save(b, skyOv);
            var bOv = Open(bPath);

            // RepC (masters Skyrim and RepA, blind to RepB): (0,0) flat with texB, byte for byte RepB's. RepC saw RepA's
            // raised (1,0) and flattened its own side: a 16 u seam RepC introduced, Seen.
            var c = new SkyrimMod(CK, Rel);
            c.Worldspaces.Add(WorldWith(Grid(0, Land(L(0), TexB))));
            var cPath = Save(c, skyOv, aOv);

            // RepD (masters Skyrim and RepB): (3,0) restated exactly as RepB left it — an ordinary forward, no new statement.
            var d = new SkyrimMod(DK, Rel);
            d.Worldspaces.Add(WorldWith(Grid(3, Land(L(3), TexB))));
            var dPath = Save(d, skyOv, bOv);

            return new[] { skyPath, aPath, bPath, cPath, dPath };
        }
        finally { foreach (var o in opened) o.Dispose(); }
    }
}
