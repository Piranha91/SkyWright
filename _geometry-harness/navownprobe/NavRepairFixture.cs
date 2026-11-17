using Mutagen.Bethesda;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Plugins.Records;
using Mutagen.Bethesda.Skyrim;
using Noggog;
using SkCell = Mutagen.Bethesda.Skyrim.Cell;

// THE NAVMESH REPAIR FIXTURE (`navownprobe --write-navrepair-fixture DIR`, read by tests/test_navmesh_repair_rules.py, which
// lays it out as an MO2 instance and runs the real probe on it). Test-only; no game data. The expectations are in the test,
// worked out by hand from this table. The master is NAMED Skyrim.esm and the shipped deleter Dawnguard.esm, so the probe's
// file-name populations (Bethesda floor against mods) apply as they do on a real order.
//
// Load order: Skyrim.esm, Dawnguard.esm, RpA.esp (masters Skyrim, Dawnguard), RpB.esp (masters Skyrim), RpD.esp (masters
// Skyrim). Interior cell IC; worldspace W with cells (0,0), (1,0), (3,0), (4,0), each with a flat LAND at z 0 in Skyrim.esm.
//
// Deletions (IC):
//   TD  2 triangles; SL (1 triangle) edge-links TD at 0.          RpB deletes TD.   EDGE-NAV SL->TD naming RpB; DELETED TD
//   SD  1 triangle; RpA gives it a link into TT at 7 (TT has 2).  RpB deletes SD.   no EDGE-IDX from SD; DELETED SD
//   TE  2 triangles; RpA writes it with no triangles.                               EMPTIED TE (2 -> 0)
//   TV  1 triangle; SV links TV at 0.                              Dawnguard deletes TV.  floor: no row, counted
// Borders (x = 4096 between (0,0) and (1,0)); pair k's border edge runs (4096, 500k) to (4096, 500k + 300):
//   L1/R1  linked both ways, never changed                                          control: no row
//   L2/R2  R2 = [border, away]; RpA swaps them to [away, border]                    EDGE-NOMATCH L2->R2 0 (candidate R2 1);
//                                                                                   EDGE-ONEWAY R2->L2 0
//   L3/R3  RpA removes R3's link back                                               EDGE-ONEWAY L3->R3 0
//   L4/R4  RpA moves R4 away (1 triangle) and adds R4a and R4b, whose border edges  EDGE-NOMATCH L4->R4 0, ambiguous
//          sit 2 u and 5 u inside (1,0), each linking L4 back                       (R4a 0, R4b 0); EDGE-ONEWAY R4a, R4b
//   L5/R5  R5 = 3 away + border at 3; RpA re-cuts it to [away, border]              EDGE-IDX L5->R5 3 (candidate R5 1)
//   L6/R6  R6 = 2 away + border at 2; RpA leaves R6 one away triangle and adds R6n  EDGE-IDX L6->R6 2 (candidate R6n 0,
//          (the border, linking L6 back)                                            another navmesh)
//   L7/R7  R7 = away + border at 1; RpA leaves one away triangle                    EDGE-IDX L7->R7 1 (no candidate)
// Ground ((3,0) and (4,0)):
//   G1  Skyrim, z 0 in (3,0); RpA raises (3,0)'s LAND to z 200                       NAV-GROUND G1 (Skyrim x RpA)
//   G2  Skyrim, z 600 in (3,0) (a deck, not on the ground)                           control: no row
//   G3  Skyrim, z 0 in (4,0); RpA raises (4,0)'s LAND to 200 and moves G3 to 200     control: the owner saw the ground
//   G4  Skyrim, z 0 in (4,0); RpD (masters Skyrim only) re-cuts it at z 0             NAV-GROUND G4 (RpD x RpA)
static class NavRepairFixture
{
    public const SkyrimRelease Rel = SkyrimRelease.SkyrimSE;
    public static readonly ModKey SkyKey = new("Skyrim", ModType.Master), DgKey = new("Dawnguard", ModType.Master),
        AKey = new("RpA", ModType.Plugin), BKey = new("RpB", ModType.Plugin), DKey = new("RpD", ModType.Plugin);
    static FormKey S(uint id) => new(SkyKey, id);

    public static readonly string[] Order = { "Skyrim.esm", "Dawnguard.esm", "RpA.esp", "RpB.esp", "RpD.esp" };

    public static readonly FormKey IC = S(0x840), W = S(0x800), Top = S(0x801);
    public static readonly FormKey TD = S(0x960), SL = S(0x961), SD = S(0x962), TT = S(0x963), TE = S(0x964), TV = S(0x965), SV = S(0x966);
    public static FormKey L(int k) => S((uint)(0xA00 + k));
    public static FormKey R(int k) => S((uint)(0xA10 + k));
    public static readonly FormKey R4a = new(AKey, 0x801), R4b = new(AKey, 0x802), R6n = new(AKey, 0x803);
    public static readonly FormKey G1 = S(0xB01), G2 = S(0xB02), G3 = S(0xB03), G4 = S(0xB04);
    static FormKey CellK(int gx) => S((uint)(0x900 + gx));
    static FormKey LandK(int gx) => S((uint)(0x910 + gx));
    static readonly int[] Grids = { 0, 1, 3, 4 };

    /// <summary>A triangle: three vertex indices and, per edge, the navmesh and triangle it links across (null: none).</summary>
    public sealed record T3(short A, short B, short C, (FormKey Mesh, short Tri)? E01 = null, (FormKey Mesh, short Tri)? E12 = null,
                            (FormKey Mesh, short Tri)? E20 = null);

    /// <summary>A navmesh whose edge-link table is built from its triangles' links in order, flag bits set to match, the box
    /// the used vertices' extent, and a one-cell grid listing every triangle.</summary>
    static NavigationMesh Nav(FormKey key, ANavmeshParent parent, (float X, float Y, float Z)[] v, params T3[] tris)
    {
        var d = new NavigationMeshData { NavmeshVersion = 12, CrcHash = 0xA5E9A03C, Parent = parent };
        foreach (var (x, y, z) in v) d.Vertices.Add(new P3Float(x, y, z));
        foreach (var t in tris)
        {
            var nt = new NavmeshTriangle { Vertices = new P3Int16(t.A, t.B, t.C), EdgeLink_0_1 = -1, EdgeLink_1_2 = -1, EdgeLink_2_0 = -1 };
            NavmeshTriangle.Flag f = 0;
            short Link((FormKey Mesh, short Tri) e)
            {
                d.EdgeLinks.Add(new EdgeLink { Mesh = new FormLink<INavigationMeshGetter>(e.Mesh), TriangleIndex = e.Tri });
                return (short)(d.EdgeLinks.Count - 1);
            }
            if (t.E01 is { } e01) { nt.EdgeLink_0_1 = Link(e01); f |= NavmeshTriangle.Flag.EdgeLink_0_1; }
            if (t.E12 is { } e12) { nt.EdgeLink_1_2 = Link(e12); f |= NavmeshTriangle.Flag.EdgeLink_1_2; }
            if (t.E20 is { } e20) { nt.EdgeLink_2_0 = Link(e20); f |= NavmeshTriangle.Flag.EdgeLink_2_0; }
            nt.Flags = f;
            d.Triangles.Add(nt);
        }
        var used = tris.SelectMany(t => new[] { t.A, t.B, t.C }).Distinct().Select(i => v[i]).ToList();
        if (used.Count > 0)
        {
            d.Min = new P3Float(used.Min(p => p.X), used.Min(p => p.Y), used.Min(p => p.Z));
            d.Max = new P3Float(used.Max(p => p.X), used.Max(p => p.Y), used.Max(p => p.Z));
            d.NavmeshGridDivisor = 1;
            d.MaxDistanceX = d.Max.X - d.Min.X; d.MaxDistanceY = d.Max.Y - d.Min.Y;
            var grid = new List<byte>(BitConverter.GetBytes((uint)tris.Length));
            for (ushort i = 0; i < tris.Length; i++) grid.AddRange(BitConverter.GetBytes(i));
            d.NavmeshGrid = grid.ToArray();
        }
        return new NavigationMesh(key, Rel) { Data = d };
    }

    static ANavmeshParent InIC => new CellNavmeshParent { Parent = new FormLink<ICellGetter>(IC) };
    static ANavmeshParent InGrid(int gx) => new WorldspaceNavmeshParent { Parent = new FormLink<IWorldspaceGetter>(W), Coordinates = new P2Int16((short)gx, 0) };

    // The interior's small shapes: one triangle at x0, or a quad of two.
    static (float, float, float)[] Tri1(float x0) => new[] { (x0, 0f, 0f), (x0 + 200, 0f, 0f), (x0, 200f, 0f) };
    static (float, float, float)[] Quad(float x0) => new[] { (x0, 0f, 0f), (x0 + 200, 0f, 0f), (x0, 200f, 0f), (x0 + 200, 200f, 0f) };

    // The border pairs. A left triangle's edge 1-2 and a right triangle's edge 2-0 lie on x = 4096 from y0 to y0 + 300.
    const float Border = 4096f;
    static float Y0(int k) => 500f * k;
    static (float, float, float)[] LeftV(int k) => new[] { (Border - 200, Y0(k), 0f), (Border, Y0(k), 0f), (Border, Y0(k) + 300, 0f) };
    /// <summary>The right side's vertices: the border triangle's three (with its border edge <paramref name="dx"/> inside the
    /// right cell), then an away triangle's three, well inside (1,0).</summary>
    static (float, float, float)[] RightV(int k, float dx = 0)
        => new[] { (Border + dx, Y0(k), 0f), (Border + dx + 200, Y0(k), 0f), (Border + dx, Y0(k) + 300, 0f),
                   (Border + 600, Y0(k), 0f), (Border + 800, Y0(k), 0f), (Border + 600, Y0(k) + 200, 0f) };
    static T3 LeftTri(FormKey to, short tri) => new(0, 1, 2, E12: (to, tri));
    static T3 RightBorder((FormKey, short)? back) => new(0, 1, 2, E20: back);
    static readonly T3 Away = new(3, 4, 5);
    static NavigationMesh Left(int k, FormKey to, short tri) => Nav(L(k), InGrid(0), LeftV(k), LeftTri(to, tri));

    // The ground cells' navmeshes: a fan of three triangles over five vertices at height z, around (x0, y0).
    static NavigationMesh Fan(FormKey key, int gx, float x0, float y0, float z)
        => Nav(key, InGrid(gx), new[] { (x0, y0, z), (x0 + 300, y0, z), (x0 + 300, y0 + 300, z), (x0, y0 + 300, z), (x0 - 200, y0 + 150, z) },
               new T3(0, 1, 2), new T3(0, 2, 3), new T3(0, 3, 4));

    static Landscape Flat(FormKey key, float h) => new(key, Rel)
    {
        Flags = Landscape.Flag.VertexNormalsHeightMap,
        VertexHeightMap = new LandscapeVertexHeightMap { Offset = h / 8f, HeightMap = new Array2d<sbyte>(new sbyte[33, 33]) },
    };
    static SkCell GridCell(int gx, Landscape? land) => new(CellK(gx), Rel) { Grid = new CellGrid { Point = new P2Int(gx, 0) }, Landscape = land };
    static Worldspace World(params SkCell[] cells)
    {
        var ws = new Worldspace(W, Rel) { EditorID = "RpWorld" };
        ws.TopCell = new SkCell(Top, Rel) { Grid = new CellGrid { Point = new P2Int(0, 0) } };
        var blk = new WorldspaceBlock { BlockNumberX = 0, BlockNumberY = 0, GroupType = GroupTypeEnum.ExteriorCellBlock };
        var sub = new WorldspaceSubBlock { BlockNumberX = 0, BlockNumberY = 0, GroupType = GroupTypeEnum.ExteriorCellSubBlock };
        foreach (var c in cells) sub.Items.Add(c);
        blk.Items.Add(sub); ws.SubCells.Add(blk);
        return ws;
    }
    static SkCell Int() => new(IC, Rel) { Flags = SkCell.Flag.IsInteriorCell };
    static void File(SkyrimMod mod, SkCell cell)
    {
        var block = new CellBlock { BlockNumber = 0, GroupType = GroupTypeEnum.InteriorCellBlock };
        var sub = new CellSubBlock { BlockNumber = 4, GroupType = GroupTypeEnum.InteriorCellSubBlock };
        sub.Cells.Add(cell); block.SubBlocks.Add(sub); mod.Cells.Records.Add(block);
    }
    static NavigationMesh Gone(FormKey key) => new(key, Rel) { IsDeleted = true };

    /// <summary>Write the five plugins into <paramref name="dir"/> (created) and return their paths in <see cref="Order"/>.</summary>
    public static IReadOnlyList<string> Write(string dir)
    {
        Directory.CreateDirectory(dir);
        var written = new List<string>();
        var overlays = new Dictionary<ModKey, ISkyrimModGetter>();
        void Save(SkyrimMod mod, params ModKey[] masters)
        {
            var path = Path.Combine(dir, mod.ModKey.FileName.String);
            mod.BeginWrite.ToPath(path).WithLoadOrder(masters.Select(k => overlays[k]).ToArray()).Write();
            overlays[mod.ModKey] = SkyrimMod.CreateFromBinaryOverlay(path, Rel);
            written.Add(path);
        }

        // ---- Skyrim.esm ----
        var sky = new SkyrimMod(SkyKey, Rel);
        var ic = Int();
        ic.NavigationMeshes.Add(Nav(TD, InIC, Quad(0), new T3(0, 1, 2), new T3(1, 3, 2)));
        ic.NavigationMeshes.Add(Nav(SL, InIC, Tri1(1000), new T3(0, 1, 2, E01: (TD, 0))));
        ic.NavigationMeshes.Add(Nav(SD, InIC, Tri1(2000), new T3(0, 1, 2)));
        ic.NavigationMeshes.Add(Nav(TT, InIC, Quad(3000), new T3(0, 1, 2), new T3(1, 3, 2)));
        ic.NavigationMeshes.Add(Nav(TE, InIC, Quad(4000), new T3(0, 1, 2), new T3(1, 3, 2)));
        ic.NavigationMeshes.Add(Nav(TV, InIC, Tri1(5000), new T3(0, 1, 2)));
        ic.NavigationMeshes.Add(Nav(SV, InIC, Tri1(6000), new T3(0, 1, 2, E01: (TV, 0))));
        File(sky, ic);
        var c0 = GridCell(0, Flat(LandK(0), 0)); var c1 = GridCell(1, Flat(LandK(1), 0));
        var c3 = GridCell(3, Flat(LandK(3), 0)); var c4 = GridCell(4, Flat(LandK(4), 0));
        for (int k = 1; k <= 7; k++)
        {
            short borderAt = k switch { 5 => 3, 6 => 2, 7 => 1, _ => 0 };
            c0.NavigationMeshes.Add(Left(k, R(k), borderAt));
            var tris = new List<T3>();
            for (int i = 0; i < borderAt; i++) tris.Add(Away);
            tris.Add(RightBorder((L(k), 0)));
            if (k == 2) tris.Add(Away);                       // R2 = [border, away]
            c1.NavigationMeshes.Add(Nav(R(k), InGrid(1), RightV(k), tris.ToArray()));
        }
        c3.NavigationMeshes.Add(Fan(G1, 3, 12800, 500, 0));
        c3.NavigationMeshes.Add(Fan(G2, 3, 13600, 500, 600));
        c4.NavigationMeshes.Add(Fan(G3, 4, 16800, 500, 0));
        c4.NavigationMeshes.Add(Fan(G4, 4, 17600, 500, 0));
        sky.Worldspaces.Add(World(c0, c1, c3, c4));
        Save(sky);

        // ---- Dawnguard.esm: the shipped game deletes TV ----
        var dg = new SkyrimMod(DgKey, Rel);
        var dgc = Int(); dgc.NavigationMeshes.Add(Gone(TV)); File(dg, dgc);
        Save(dg, SkyKey);

        // ---- RpA.esp ----
        var a = new SkyrimMod(AKey, Rel);
        var aic = Int();
        aic.NavigationMeshes.Add(Nav(SD, InIC, Tri1(2000), new T3(0, 1, 2, E01: (TT, 7))));
        aic.NavigationMeshes.Add(new NavigationMesh(TE, Rel) { Data = new NavigationMeshData { NavmeshVersion = 12, CrcHash = 0xA5E9A03C, Parent = InIC } });
        File(a, aic);
        var a1 = GridCell(1, null);
        a1.NavigationMeshes.Add(Nav(R(2), InGrid(1), RightV(2), Away, RightBorder((L(2), 0))));
        a1.NavigationMeshes.Add(Nav(R(3), InGrid(1), RightV(3), RightBorder(null)));
        a1.NavigationMeshes.Add(Nav(R(4), InGrid(1), RightV(4), Away));
        a1.NavigationMeshes.Add(Nav(R4a, InGrid(1), RightV(4, 2), RightBorder((L(4), 0))));
        a1.NavigationMeshes.Add(Nav(R4b, InGrid(1), RightV(4, 5), RightBorder((L(4), 0))));
        a1.NavigationMeshes.Add(Nav(R(5), InGrid(1), RightV(5), Away, RightBorder((L(5), 0))));
        a1.NavigationMeshes.Add(Nav(R(6), InGrid(1), RightV(6), Away));
        a1.NavigationMeshes.Add(Nav(R6n, InGrid(1), RightV(6), RightBorder((L(6), 0))));
        a1.NavigationMeshes.Add(Nav(R(7), InGrid(1), RightV(7), Away));
        var a3 = GridCell(3, Flat(LandK(3), 200));
        var a4 = GridCell(4, Flat(LandK(4), 200));
        a4.NavigationMeshes.Add(Fan(G3, 4, 16800, 500, 200));
        a.Worldspaces.Add(World(a1, a3, a4));
        Save(a, SkyKey, DgKey);

        // ---- RpB.esp: deletes TD and SD ----
        var b = new SkyrimMod(BKey, Rel);
        var bic = Int(); bic.NavigationMeshes.Add(Gone(TD)); bic.NavigationMeshes.Add(Gone(SD)); File(b, bic);
        Save(b, SkyKey);

        // ---- RpD.esp: re-cuts G4 at z 0 without RpA's LAND in its view ----
        var dmod = new SkyrimMod(DKey, Rel);
        var d4 = GridCell(4, null);
        d4.NavigationMeshes.Add(Nav(G4, InGrid(4), new[] { (17600f, 500f, 0f), (17900f, 500f, 0f), (17900f, 800f, 0f), (17600f, 800f, 0f),
                                                            (17400f, 650f, 0f), (17750f, 950f, 0f) },
                                    new T3(0, 1, 2), new T3(0, 2, 3), new T3(0, 3, 4), new T3(2, 5, 3)));
        dmod.Worldspaces.Add(World(d4));
        Save(dmod, SkyKey);

        foreach (var o in overlays.Values) (o as IDisposable)?.Dispose();
        return written;
    }
}
