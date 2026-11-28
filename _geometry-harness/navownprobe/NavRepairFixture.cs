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
//   TD  2 triangles; SL (2 triangles) edge-links TD at 0 and 1.   RpB deletes TD.   EDGE-NAV SL->TD 0 and SL->TD 1, each
//                                                                                   naming its source triangle; DELETED TD
//   SD  1 triangle; RpA gives it a link into TT at 7 (TT has 2).  RpB deletes SD.   no EDGE-IDX from SD; DELETED SD
//   TE  2 triangles; RpA writes it with no triangles.                               EMPTIED TE (2 -> 0)
//   TV  1 triangle; SV links TV at 0.                              Dawnguard deletes TV.  floor: no row, counted
// Borders (x = 4096 between (0,0) and (1,0)); pair k's border edge runs (4096, 500k) to (4096, 500k + 300):
//   L1/R1  linked both ways; RpA forwards R1 unchanged (so a false row is a finding) control: no row
//   L2/R2  R2 = [border, away]; RpA swaps them to [away, border]                    EDGE-NOMATCH L2->R2 0 (candidate R2 1);
//                                                                                   EDGE-ONEWAY R2->L2 0
//   L3/R3  RpA removes R3's link back                                               EDGE-ONEWAY L3->R3 0
//   L4/R4  RpA moves R4 away (1 triangle) and adds R4a and R4b, whose border edges  EDGE-NOMATCH L4->R4 0, ambiguous
//          sit 2 u and 5 u inside (1,0), each linking L4 back                       (R4a 0, R4b 0); EDGE-ONEWAY R4a, R4b
//   L5/R5  R5 = 3 away + border at 3; RpA re-cuts it to [away, border]              EDGE-IDX L5->R5 3 (candidate R5 1)
//   L6/R6  R6 = 2 away + border at 2; RpA leaves R6 one away triangle and adds R6n  EDGE-IDX L6->R6 2 (candidate R6n 0,
//          (the border, linking L6 back)                                            another navmesh)
//   L7/R7  R7 = away + border at 1; RpA leaves one away triangle                    EDGE-IDX L7->R7 1 (no candidate)
//   L8/R8  RpA lifts R8 200 u, links kept both ways (same plan, other height)       EDGE-NOMATCH L8->R8 0 and R8->L8 0
//   L9/R9  R9's one triangle: its true border edge runs 20 u past L9's (overlap      control: matched both ways, no row
//          0.93), and its slanted edge projects over all of L9's edge (overlap 1.0)  (a largest-overlap pick reads the slanted
//          but lies up to 274 u off. RpA forwards R9 unchanged.                      edge and calls the link EDGE-NOMATCH)
//   L11/R11 R11 = 2 away + border at 2; RpA re-cuts it to [away, border] with no     EDGE-IDX L11->R11 2 (candidate R11 1);
//          link back                                                               the relink writes R11's link back
//   L12/R12 RpA moves R12's border edge 12 u inside (1,0), links kept both ways     unproven in game (plan band), no row
//   L13/R13 RpA lifts R13 100 u, links kept both ways                               unproven in game (height band), no row
//   L10/R10 a ledge: L10 links R10 by a Ledge Down (Type 2), R10 links back by a      control: no row; the emit counts 2 ledge
//          Ledge Up (Type 1); R10's edge lies 100 u inside (1,0) and 150 u lower.   links (judging a ledge by its edge in plan
//          RpA forwards R10 unchanged.                                              calls both EDGE-NOMATCH)
// Ground ((3,0) and (4,0)):
//   G1  Skyrim, z 0 in (3,0); RpA raises (3,0)'s LAND to z 200                       NAV-GROUND G1 (Skyrim x RpA)
//   G2  Skyrim, z 600 in (3,0) (a deck, not on the ground)                           control: no row
//   G3  Skyrim, z 0 in (4,0); RpA raises (4,0)'s LAND to 200 and moves G3 to 200     control: the owner saw the ground
//   G4  Skyrim, z 0 in (4,0); RpD (masters Skyrim only) re-cuts it at z 0             NAV-GROUND G4 (RpD x RpA)
//   G5  Skyrim, z 0 in (5,0); RpA raises (5,0) to 200 and moves G5 to 200; RpD raises    NAV-GROUND G5 (RpA x RpD): the author's
//       (5,0) to 400                                                                     ground is the owner's view, not the first LAND
//   G6  Skyrim, in (3,0): four vertices at z 0 and one up on a rock at z 300; RpA's    NAV-GROUND G6; the plan moves the four,
//       raise to 200 moves the ground under all five                                   not the one off the ground
// Containers:
//   RpD overrides the border cell (0,0), renaming it. A patch of L-navmeshes carries that   the read-back refuses the patch's
//   CELL from Skyrim.esm, the navmeshes' winner, unless the winner's CELL is forwarded     cell until RpD's is forwarded
// NAVI (NV): Skyrim.esm states every navmesh of its worldspace, each naming the navmeshes its links name; L6's entry also carries a linked door (the
//   placed ref DR in (0,0)) and island data. RpA, the last plugin carrying NAVI (its winner), states R5 unchanged, R6 with
//   no neighbour and its new R6n with none either (a stale entry, as Moon and Star's). The plan's relink of L6 into R6n adds
//   R6n to L6's entry, which only Skyrim.esm states (so the patch adds it whole), and L6 to R6n's, in RpA's list in place.
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
    public static readonly FormKey NV = S(0xC00), DR = S(0xC10);
    public static readonly FormKey G1 = S(0xB01), G2 = S(0xB02), G3 = S(0xB03), G4 = S(0xB04), G5 = S(0xB05), G6 = S(0xB06);
    static FormKey CellK(int gx) => S((uint)(0x900 + gx));
    static FormKey LandK(int gx) => S((uint)(0x910 + gx));

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
    /// <summary>Pair 10, a ledge: each link's Type (Mutagen's EdgeLink.Unknown) set to a Ledge Down from L10 and a Ledge Up back,
    /// R10's border edge 100 u inside the right cell and 150 u below L10's.</summary>
    static NavigationMesh Ledge(NavigationMesh nav, int type) { nav.Data!.EdgeLinks[0].Unknown = type; return nav; }
    static NavigationMesh L10() => Ledge(Left(10, R(10), 0), 2);
    static NavigationMesh R10() => Ledge(Nav(R(10), InGrid(1), RightV(10, 100).Select(v => (v.Item1, v.Item2, v.Item3 - 150)).ToArray(),
                                                    RightBorder((L(10), 0))), 1);
    /// <summary>Pair 9's far side: edge 0-1 the true border edge, offset 20 u down the border; edge 2-0 the slanted one.</summary>
    static NavigationMesh R9() => Nav(R(9), InGrid(1),
        new[] { (Border, Y0(9) - 20, 0f), (Border, Y0(9) + 280, 0f), (Border + 300, Y0(9) + 330, 0f) }, new T3(0, 1, 2, E01: (L(9), 0)));

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

    /// <summary>A NAVI entry for <paramref name="nav"/> in grid cell (<paramref name="gx"/>, 0), naming <paramref name="merged"/>.</summary>
    static NavigationMapInfo Info(FormKey nav, int gx, float x, float y, params FormKey[] merged)
    {
        var i = new NavigationMapInfo
        {
            NavigationMesh = new FormLink<INavigationMeshGetter>(nav), Point = new P3Float(x, y, 0), Unknown2 = 0x1234,
            Parent = new NavigationMapInfoWorldParent { ParentWorldspace = new FormLink<IWorldspaceGetter>(W), ParentWorldspaceCoord = new P2Int16((short)gx, 0) },
        };
        foreach (var m in merged) i.MergedTo.Add(new FormLink<INavigationMeshGetter>(m));
        return i;
    }
    static NavigationMeshInfoMap Navi(params NavigationMapInfo[] infos)
    {
        var n = new NavigationMeshInfoMap(NV, Rel) { NavMeshVersion = 12 };
        n.MapInfos.AddRange(infos);
        return n;
    }

    /// <summary>The NAVI-winner master case (houseCARL, 2026-11-22): a minimal Skyrim.esm (two navmeshes in one grid cell and
    /// their NAVI) and NmNavi.esp, which only re-saves that NAVI, its entries naming Skyrim.esm records alone; and PLAN.json, a
    /// repair plan in navownprobe's format that moves one vertex of L1 (Skyrim.esm wins it) and adds L2 to L1's NAVI entry (NmNavi
    /// wins NAVI). Written by navedit write --repair-plan, the patch masters NmNavi.esp only through the NAVI-winner step: no
    /// record of NmNavi's is referenced. Returns the plan's path.</summary>
    public static string WriteNaviMasterCase(string dir)
    {
        Directory.CreateDirectory(dir);
        string skyPath = Path.Combine(dir, "Skyrim.esm"), nPath = Path.Combine(dir, "NmNavi.esp");
        var nKey = new ModKey("NmNavi", ModType.Plugin);
        var sky = new SkyrimMod(SkyKey, Rel);
        var c0 = GridCell(0, Flat(LandK(0), 0));
        c0.NavigationMeshes.Add(Nav(L(1), InGrid(0), new[] { (100f, 100f, 0f), (400f, 100f, 5f), (400f, 400f, 10f), (100f, 400f, 0f) }, new T3(0, 1, 2), new T3(0, 2, 3)));
        c0.NavigationMeshes.Add(Nav(L(2), InGrid(0), new[] { (1100f, 100f, 0f), (1400f, 100f, 5f), (1400f, 400f, 10f), (1100f, 400f, 0f) }, new T3(0, 1, 2), new T3(0, 2, 3)));
        sky.Worldspaces.Add(World(c0));
        NavigationMeshInfoMap Map() => Navi(Info(L(1), 0, 100, 100), Info(L(2), 0, 1100, 100));
        sky.NavigationMeshInfoMaps.Add(Map());
        sky.BeginWrite.ToPath(skyPath).WithLoadOrder(Array.Empty<ISkyrimModGetter>()).Write();
        using var skyRead = SkyrimMod.CreateFromBinaryOverlay(skyPath, Rel);
        var n = new SkyrimMod(nKey, Rel);
        n.NavigationMeshInfoMaps.Add(Map());
        n.BeginWrite.ToPath(nPath).WithLoadOrder(skyRead).Write();
        string Sha(string pth) { using var fs = System.IO.File.OpenRead(pth); return Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(fs)).ToLowerInvariant(); }
        var moved = new HarnessShared.NavChange(); moved.Vertices[1] = new SkyWright.Engine.Vec3(400, 100, 6);
        var plan = new System.Text.Json.Nodes.JsonObject
        {
            ["order"] = new System.Text.Json.Nodes.JsonArray(skyPath, nPath),
            ["plans"] = new System.Text.Json.Nodes.JsonArray(new System.Text.Json.Nodes.JsonObject
            {
                ["source"] = L(1).ToString(), ["class"] = "ground", ["status"] = "planned", ["why"] = "fixture: one vertex moves 1 u up",
                ["winner"] = new System.Text.Json.Nodes.JsonObject { ["plugin"] = "Skyrim.esm", ["path"] = skyPath, ["sha256"] = Sha(skyPath) },
                ["links"] = new System.Text.Json.Nodes.JsonArray(), ["invariants"] = new System.Text.Json.Nodes.JsonArray(),
                ["change"] = NavRepairRun.ChangeJson(moved),
            }),
            ["navi"] = new System.Text.Json.Nodes.JsonObject
            {
                ["record"] = NV.ToString(), ["winner"] = new System.Text.Json.Nodes.JsonObject { ["plugin"] = "NmNavi.esp", ["path"] = nPath },
                ["why"] = new System.Text.Json.Nodes.JsonArray("fixture: L1's entry gains L2"),
                ["entries"] = new System.Text.Json.Nodes.JsonArray(new System.Text.Json.Nodes.JsonObject
                {
                    ["navmesh"] = L(1).ToString(), ["from"] = new System.Text.Json.Nodes.JsonObject { ["plugin"] = "NmNavi.esp", ["path"] = nPath, ["index"] = 0 },
                    ["winnerIndex"] = 0, ["add"] = new System.Text.Json.Nodes.JsonArray(L(2).ToString()),
                    ["mergedToBefore"] = new System.Text.Json.Nodes.JsonArray(), ["preferredMerges"] = new System.Text.Json.Nodes.JsonArray(),
                }),
            },
            ["ops"] = new System.Text.Json.Nodes.JsonArray(
                new System.Text.Json.Nodes.JsonObject { ["formid"] = L(1).ToString(), ["field_path"] = "Data.Vertices", ["op"] = "SetAtIndex", ["key"] = "1", ["value"] = "400,100,6" },
                new System.Text.Json.Nodes.JsonObject { ["formid"] = NV.ToString(), ["field_path"] = "MapInfos[0].MergedTo", ["op"] = "Add", ["value"] = L(2).ToString() }),
        };
        var planPath = Path.Combine(dir, "PLAN.json");
        System.IO.File.WriteAllText(planPath, plan.ToJsonString(new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
        return planPath;
    }

    /// <summary>The mover's re-cut navmesh, in the mover case.</summary>
    public static readonly FormKey GM = S(0xB10);
    public static readonly string[] MoverOrder = { "Skyrim.esm", "Mover.esp", "Revert.esp" };

    /// <summary>THE MOVER CASE (C2599-C2601; the use_version rows of 2026-11-22): Skyrim.esm has flat ground at z 0 in (3,0) and
    /// navmesh GM on it (a fan of three triangles over five vertices), stated in its NAVI; Mover.esp (masters Skyrim) raises
    /// (3,0)'s LAND to 200 and ships its own re-cut of GM at z 200 (four triangles round a centre vertex: other vertices, other
    /// triangles); Revert.esp (masters Skyrim only) overrides GM with Skyrim's geometry, as Clockwork does with the great cities'.
    /// So GM is a NAV-GROUND row owned by Revert whose ground Mover moved, and a CLOBBERED row (Mover > Revert); GM links nothing,
    /// so carrying Mover's version plans. Returns <paramref name="dir"/>, holding the three plugins of <see cref="MoverOrder"/>.
    /// With <paramref name="doors"/> (C2604, houseCARL's item 2): Skyrim.esm also places doors DA, DB and DC in (3,0); its GM (and
    /// Revert's copy) stands before DA and DB, and its NAVI entry for GM links [DA, DB], as the base game's entries follow their
    /// door table; Mover's re-cut stands before [DB, DC, DA]: reordered, with a door more. Carrying it, the entry's linked doors
    /// must become [DB, DC, DA]. With <paramref name="neighbour"/> (C2606, houseCARL's item 3): Skyrim.esm also has navmesh GN in
    /// (3,0), far east, and its GM (and Revert's copy) links one way into GN (GN links nothing back); GM's NAVI entry names GN,
    /// GN's names nothing, as each follows its own link table. Mover's re-cut links nothing, so carrying it, GM's entry must
    /// drop GN, and GN's entry stays as it is. Planned through navedit (navownprobe's detector would file the one-way link).</summary>
    public static readonly FormKey DA = S(0xD31), DB = S(0xD32), DC = S(0xD33), GN = S(0xB11);
    /// <summary>The island variants' extra records: GE, a second navmesh in (3,0) whose entry is an island with an empty mesh;
    /// GQ, a third, stated alone by Revert.esp's NAVI in the "whole" variant; DT and DP, a teleport door pair.</summary>
    public static readonly FormKey GE = S(0xB12), GQ = S(0xB13), DT = S(0xD34), DP = S(0xD35);
    /// <summary>The island variants (R7, Gate B; <paramref name="island"/> of <see cref="WriteMoverCase"/>), each a word after
    /// the directory on <c>--write-mover-fixture</c>. The vanilla GM (box (12300,500,0)-(12800,800,0)) has island data IS:
    /// that box and a mesh of 3 vertices, 1 triangle; Mover's re-cut (box (12300,500,200)-(12800,800,200)) has IM: its box and a
    /// mesh of 4 vertices, 2 triangles. NAVI is Skyrim.esm's record (NV) in every plugin that carries it.
    ///   island-version        Skyrim and Revert state GM as an island (32, IS), Mover as an island (32, IM); the NAVI winner
    ///                         is Revert. use_version GM from Mover: the entry, in place in Revert's list, takes (32, IM).
    ///   island-plain-version  as island-version, but Mover's entry is plain (0, none): the entry takes (0, none).
    ///   island-plain-winner   Skyrim's and Revert's entries plain, Mover's (32, IM): the entry takes (32, IM).
    ///   island-noentry        as island-version, but Mover.esp carries no NAVI: use_version from Mover is refused by name.
    ///   island-sloppy-version as island-version, but Mover.esp's entry gives its island the box top z 201, where its own
    ///                         navmesh stores 200: use_version from Mover is refused by name (review F2).
    ///   island-ground         Mover raises (3,0)'s LAND to 200 and ships no re-cut; Revert reverts GM to vanilla. Skyrim and
    ///                         Revert state GM as an island (32, IG: the vanilla box; vertices (12300,650,0) on a navmesh
    ///                         vertex, (12650,650,5) 5 u above the surface, (12900,650,0) 100 u east of the navmesh in plan;
    ///                         one triangle) and GE (a fan at x 13500, z 0) as an island with an empty mesh. Following the
    ///                         ground: GM's box becomes (12300,500,200)-(12800,800,200), its mesh (12300,650,200), (12650,650,205),
    ///                         (12900,650,200); GE's box rises 200 u, its mesh stays empty.
    ///   island-ground-whole   as island-ground, but Revert's NAVI states only GQ, so GM's and GE's entries are Skyrim's and the
    ///                         patch adds them whole, with their new island data.
    ///   island-door           GM an island (32, IS) in Skyrim's NAVI only (the winner); Skyrim places a teleport door pair DT and
    ///                         DP in (3,0); arriving through DT puts an actor at (12550, 650, 0), on GM's triangle 1. A door edit
    ///                         of GM with DT leaves the island data as it is.</summary>
    public static readonly string[] IslandVariants = { "island-version", "island-plain-version", "island-plain-winner", "island-noentry", "island-sloppy-version", "island-ground", "island-ground-whole", "island-door" };
    static IslandData Isl((float, float, float) mn, (float, float, float) mx, (float X, float Y, float Z)[] v, params (short, short, short)[] t)
    {
        var d = new IslandData { Min = new P3Float(mn.Item1, mn.Item2, mn.Item3), Max = new P3Float(mx.Item1, mx.Item2, mx.Item3) };
        foreach (var p in v) d.Vertices.Add(new P3Float(p.X, p.Y, p.Z));
        foreach (var (a, b, c) in t) d.Triangles.Add(new P3Int16(a, b, c));
        return d;
    }
    static IslandData IS() => Isl((12300, 500, 0), (12800, 800, 0), new[] { (12300f, 650f, 0f), (12800f, 500f, 0f), (12800f, 800f, 0f) }, (0, 1, 2));
    static IslandData IM() => Isl((12300, 500, 200), (12800, 800, 200), new[] { (12300f, 500f, 200f), (12800f, 500f, 200f), (12800f, 800f, 200f), (12300f, 800f, 200f) }, (0, 1, 2), (0, 2, 3));
    static IslandData IG() => Isl((12300, 500, 0), (12800, 800, 0), new[] { (12300f, 650f, 0f), (12650f, 650f, 5f), (12900f, 650f, 0f) }, (0, 1, 2));
    static NavigationMapInfo Island(NavigationMapInfo i, IslandData? d) { if (d is not null) { i.Unknown = 32; i.Island = d; } return i; }

    public static string WriteMoverCase(string dir, bool doors = false, bool neighbour = false, string? island = null)
    {
        if (island is not null && !IslandVariants.Contains(island)) throw new ArgumentException($"'{island}' is not an island variant ({string.Join(", ", IslandVariants)})");
        if (island is not null && (doors || neighbour)) throw new ArgumentException("an island variant is written without the doors and neighbour variants");
        if (island is not null) return WriteIslandCase(dir, island);
        // the vanilla GM: Fan's three triangles, the first's edge 1-2 linked into GN's triangle 0 when there is a neighbour
        NavigationMesh VanillaGM() => !neighbour ? Fan(GM, 3, 12500, 500, 0)
            : Nav(GM, InGrid(3), new[] { (12500f, 500f, 0f), (12800f, 500f, 0f), (12800f, 800f, 0f), (12500f, 800f, 0f), (12300f, 650f, 0f) },
                  new T3(0, 1, 2, E12: (GN, 0)), new T3(0, 2, 3), new T3(0, 3, 4));
        Directory.CreateDirectory(dir);
        var mKey = new ModKey("Mover", ModType.Plugin); var rKey = new ModKey("Revert", ModType.Plugin);
        string sp = Path.Combine(dir, "Skyrim.esm"), mp = Path.Combine(dir, "Mover.esp"), rp = Path.Combine(dir, "Revert.esp");
        NavigationMesh WithDoors(NavigationMesh n, params (FormKey Door, short Tri)[] ds)
        {
            if (doors) foreach (var (d, t) in ds) n.Data!.DoorTriangles.Add(new DoorTriangle { Door = new FormLink<IPlacedObjectGetter>(d), TriangleBeforeDoor = t, Unknown = HarnessShared.NavRepairNavi.LinkedDoorUnknown });
            return n;
        }
        var sky = new SkyrimMod(SkyKey, Rel);
        var c3 = GridCell(3, Flat(LandK(3), 0));
        c3.NavigationMeshes.Add(WithDoors(VanillaGM(), (DA, 0), (DB, 1)));
        if (neighbour) c3.NavigationMeshes.Add(Fan(GN, 3, 14000, 500, 0));
        if (doors)
            foreach (var (d, x) in new[] { (DA, 12520f), (DB, 12600f), (DC, 12700f) })
                c3.Temporary.Add(new PlacedObject(d, Rel) { EditorID = $"RpMoverDoor{d.ID:X}", Placement = new Placement { Position = new P3Float(x, 560, 0) } });
        sky.Worldspaces.Add(World(c3));
        var gmInfo = neighbour ? Info(GM, 3, 12500, 500, GN) : Info(GM, 3, 12500, 500);
        if (doors) foreach (var d in new[] { DA, DB }) gmInfo.LinkedDoors.Add(new LinkedDoor { Unknown = HarnessShared.NavRepairNavi.LinkedDoorUnknown, Door = new FormLink<IPlacedObjectGetter>(d) });
        sky.NavigationMeshInfoMaps.Add(neighbour ? Navi(gmInfo, Info(GN, 3, 14000, 500)) : Navi(gmInfo));
        sky.BeginWrite.ToPath(sp).WithLoadOrder(Array.Empty<ISkyrimModGetter>()).Write();
        using var skyRead = SkyrimMod.CreateFromBinaryOverlay(sp, Rel);
        var mover = new SkyrimMod(mKey, Rel);
        var m3 = GridCell(3, Flat(LandK(3), 200));
        m3.NavigationMeshes.Add(WithDoors(Nav(GM, InGrid(3), new[] { (12300f, 500f, 200f), (12800f, 500f, 200f), (12800f, 800f, 200f), (12300f, 800f, 200f), (12550f, 650f, 200f) },
                                    new T3(0, 1, 4), new T3(1, 2, 4), new T3(2, 3, 4), new T3(3, 0, 4)), (DB, 0), (DC, 2), (DA, 3)));
        mover.Worldspaces.Add(World(m3));
        mover.BeginWrite.ToPath(mp).WithLoadOrder(skyRead).Write();
        var rev = new SkyrimMod(rKey, Rel);
        var r3 = GridCell(3, null);
        r3.NavigationMeshes.Add(WithDoors(VanillaGM(), (DA, 0), (DB, 1)));
        rev.Worldspaces.Add(World(r3));
        rev.BeginWrite.ToPath(rp).WithLoadOrder(skyRead).Write();
        return dir;
    }

    /// <summary>One island variant of the mover case (<see cref="IslandVariants"/>), into <paramref name="dir"/>, holding the
    /// three plugins of <see cref="MoverOrder"/>.</summary>
    static string WriteIslandCase(string dir, string v)
    {
        Directory.CreateDirectory(dir);
        bool ground = v.StartsWith("island-ground"), door = v == "island-door";
        var mKey = new ModKey("Mover", ModType.Plugin); var rKey = new ModKey("Revert", ModType.Plugin);
        string sp = Path.Combine(dir, "Skyrim.esm"), mp = Path.Combine(dir, "Mover.esp"), rp = Path.Combine(dir, "Revert.esp");
        // the vanilla entries: GM an island unless the winner's side is plain; GE (ground variants) an island with an empty mesh
        IslandData? Vanilla() => v == "island-plain-winner" ? null : ground ? IG() : IS();
        IslandData GEIsland() => Isl((13300, 500, 0), (13800, 800, 0), Array.Empty<(float, float, float)>());
        NavigationMapInfo GmInfo() => Island(Info(GM, 3, 12500, 500), Vanilla());
        NavigationMapInfo GeInfo() => Island(Info(GE, 3, 13500, 500), GEIsland());
        NavigationMapInfo GqInfo() => Info(GQ, 3, 15000, 500);

        var sky = new SkyrimMod(SkyKey, Rel);
        var c3 = GridCell(3, Flat(LandK(3), 0));
        c3.NavigationMeshes.Add(Fan(GM, 3, 12500, 500, 0));
        if (ground) { c3.NavigationMeshes.Add(Fan(GE, 3, 13500, 500, 0)); c3.NavigationMeshes.Add(Fan(GQ, 3, 15000, 500, 0)); }
        if (door)
        {
            // arriving through DT puts an actor at DP's destination position, on GM; DT's own destination is beside it
            TeleportDestination To(FormKey door, float x, float y) { var td = new TeleportDestination { Position = new P3Float(x, y, 0), Rotation = new P3Float(0, 0, 0) }; td.Door.SetTo(door); return td; }
            c3.Temporary.Add(new PlacedObject(DT, Rel) { EditorID = "RpIslandDoorT", Placement = new Placement { Position = new P3Float(12520, 560, 0) }, TeleportDestination = To(DP, 12700, 700) });
            c3.Temporary.Add(new PlacedObject(DP, Rel) { EditorID = "RpIslandDoorP", Placement = new Placement { Position = new P3Float(12700, 700, 0) }, TeleportDestination = To(DT, 12550, 650) });
        }
        sky.Worldspaces.Add(World(c3));
        sky.NavigationMeshInfoMaps.Add(ground ? Navi(GmInfo(), GeInfo(), GqInfo()) : Navi(GmInfo()));
        sky.BeginWrite.ToPath(sp).WithLoadOrder(Array.Empty<ISkyrimModGetter>()).Write();
        using var skyRead = SkyrimMod.CreateFromBinaryOverlay(sp, Rel);

        // Mover.esp: the ground raised; its own re-cut of GM (not in the ground variants) and, but for island-noentry and the
        // ground and door variants, its own NAVI entry for it
        var mover = new SkyrimMod(mKey, Rel);
        var m3 = GridCell(3, Flat(LandK(3), 200));
        if (!ground)
            m3.NavigationMeshes.Add(Nav(GM, InGrid(3), new[] { (12300f, 500f, 200f), (12800f, 500f, 200f), (12800f, 800f, 200f), (12300f, 800f, 200f), (12550f, 650f, 200f) },
                                        new T3(0, 1, 4), new T3(1, 2, 4), new T3(2, 3, 4), new T3(3, 0, 4)));
        mover.Worldspaces.Add(World(m3));
        if (v is "island-version" or "island-plain-winner") mover.NavigationMeshInfoMaps.Add(Navi(Island(Info(GM, 3, 12300, 500), IM())));
        if (v is "island-sloppy-version") { var sloppy = IM(); sloppy.Max = new P3Float(12800, 800, 201); mover.NavigationMeshInfoMaps.Add(Navi(Island(Info(GM, 3, 12300, 500), sloppy))); }
        if (v is "island-plain-version") mover.NavigationMeshInfoMaps.Add(Navi(Info(GM, 3, 12300, 500)));
        mover.BeginWrite.ToPath(mp).WithLoadOrder(skyRead).Write();

        // Revert.esp: GM back to the vanilla geometry, and its NAVI (the winner) restating Skyrim's entries; in island-ground-whole
        // it states only GQ; in island-door it carries none
        var rev = new SkyrimMod(rKey, Rel);
        var r3 = GridCell(3, null);
        r3.NavigationMeshes.Add(Fan(GM, 3, 12500, 500, 0));
        rev.Worldspaces.Add(World(r3));
        if (v == "island-ground-whole") rev.NavigationMeshInfoMaps.Add(Navi(GqInfo()));
        else if (ground) rev.NavigationMeshInfoMaps.Add(Navi(GmInfo(), GeInfo(), GqInfo()));
        else if (!door) rev.NavigationMeshInfoMaps.Add(Navi(GmInfo()));
        rev.BeginWrite.ToPath(rp).WithLoadOrder(skyRead).Write();
        return dir;
    }

    // ---- THE SET CASES (R10: carry a set of navmeshes; C2675-C2680) ----
    // Cell (3,0) at z 0. Squares 300 u wide (y 500-800 unless said): SO x 12300, SA 12600, SB 12900, SR 13200; SS a strip under
    // SA and SB (y 200-500) linking into both. A vanilla square is two triangles (0: south and east edges, 1: north and west);
    // a re-cut is four round a centre vertex (0 north, 1 east, 2 south, 3 west), so every link into it is renumbered.
    //   Skyrim.esm  every navmesh vanilla. SA and SB touch at x 12900 but do not link; SO-SA, SA-SS, SB-SS, SB-SR link both
    //               ways. Row y 1500: SW x 12300, SC 12600, SX 12900, unlinked. SD (y 2500) alone. NAVI NV states each.
    //   SetP.esp    P. Re-cuts SA and SB, linking them to each other (and to SO, SS, SR); its SB links SR by a Ledge Down and
    //               its SR (which P wins) links back by a Ledge Up into P's numbering. Its own SO links nothing (P's SO is one
    //               way, Revert's wins). Re-cuts SC linking into SW (P wins SW; P's SW links nothing back) and SX (P has no
    //               SX). Re-cuts SD standing before door DZ, which no plugin places. Its NAVI states SA, SB (an island IB),
    //               SR, SO, SC, SW, SD as its own links have them.
    //   Revert.esp  SA, SO, SC and SD back to vanilla; its NAVI (the winner) restates Skyrim's.
    //   Touch.esp   SB back to vanilla (so SB's winner is a plugin SA's is not).
    //   (review of Gate C, 2026-11-27) SQ (x 12300, y 200): SS's west edge links into it one way in Skyrim.esm, an already
    //   broken border of a navmesh the set re-points, which the two-way read-back must not judge.
    // "set-ground" (navownprobe's repair plan): Skyrim.esm SA and SB vanilla at z 0, unlinked; Mover.esp raises (3,0) to 200
    // and re-cuts both at z 200 linked to each other; Revert.esp puts both back to vanilla.
    public static readonly FormKey SA = S(0xB20), SB = S(0xB21), SO = S(0xB22), SS = S(0xB23), SR = S(0xB24), SC = S(0xB25),
        SX = S(0xB26), SW = S(0xB27), SDn = S(0xB28), DZ = S(0xD40), SQ = S(0xB29);
    public static readonly string[] SetOrder = { "Skyrim.esm", "SetP.esp", "Revert.esp", "Touch.esp" };
    public static readonly string[] SetVariants = { "set", "set-ground" };

    static (float, float, float)[] SqV(float x0, float y0, float z, bool recut)
    {
        var v = new List<(float, float, float)> { (x0, y0, z), (x0 + 300, y0, z), (x0 + 300, y0 + 300, z), (x0, y0 + 300, z) };
        if (recut) v.Add((x0 + 150, y0 + 150, z));
        return v.ToArray();
    }
    /// <summary>A square navmesh, vanilla or re-cut, its south, east, north and west edges linked as given.</summary>
    static NavigationMesh Square(FormKey key, float x0, float y0, bool recut, (FormKey, short)? s = null, (FormKey, short)? e = null,
                                 (FormKey, short)? n = null, (FormKey, short)? w = null, float z = 0)
        => recut ? Nav(key, InGrid(3), SqV(x0, y0, z, true), new T3(2, 3, 4, E01: n), new T3(1, 2, 4, E01: e), new T3(0, 1, 4, E01: s), new T3(3, 0, 4, E01: w))
                 : Nav(key, InGrid(3), SqV(x0, y0, z, false), new T3(0, 1, 2, E01: s, E12: e), new T3(0, 2, 3, E12: n, E20: w));
    static NavigationMesh Strip((FormKey, short)? left, (FormKey, short)? right, (FormKey, short)? west = null)
        => Nav(SS, InGrid(3), new[] { (12600f, 200f, 0f), (12900f, 200f, 0f), (13200f, 200f, 0f), (12600f, 500f, 0f), (12900f, 500f, 0f), (13200f, 500f, 0f) },
               new T3(0, 4, 3, E12: left, E20: west), new T3(0, 1, 4), new T3(1, 5, 4, E12: right), new T3(1, 2, 5));

    public static string WriteSetCase(string dir, string variant)
    {
        if (!SetVariants.Contains(variant)) throw new ArgumentException($"'{variant}' is not a set variant ({string.Join(", ", SetVariants)})");
        Directory.CreateDirectory(dir);
        string sp = Path.Combine(dir, "Skyrim.esm");
        // vanilla triangle facing each side: 0 south and east, 1 north and west; re-cut: 0 north, 1 east, 2 south, 3 west
        if (variant == "set-ground")
        {
            var gs = new SkyrimMod(SkyKey, Rel);
            var g3 = GridCell(3, Flat(LandK(3), 0));
            g3.NavigationMeshes.Add(Square(SA, 12600, 500, false)); g3.NavigationMeshes.Add(Square(SB, 12900, 500, false));
            gs.Worldspaces.Add(World(g3));
            gs.NavigationMeshInfoMaps.Add(Navi(Info(SA, 3, 12600, 500), Info(SB, 3, 12900, 500)));
            gs.BeginWrite.ToPath(sp).WithLoadOrder(Array.Empty<ISkyrimModGetter>()).Write();
            using var gsr = SkyrimMod.CreateFromBinaryOverlay(sp, Rel);
            var mv = new SkyrimMod(new ModKey("Mover", ModType.Plugin), Rel);
            var m3 = GridCell(3, Flat(LandK(3), 200));
            m3.NavigationMeshes.Add(Square(SA, 12600, 500, true, e: (SB, 3), z: 200));
            m3.NavigationMeshes.Add(Square(SB, 12900, 500, true, w: (SA, 1), z: 200));
            mv.Worldspaces.Add(World(m3));
            mv.BeginWrite.ToPath(Path.Combine(dir, "Mover.esp")).WithLoadOrder(gsr).Write();
            var rv = new SkyrimMod(new ModKey("Revert", ModType.Plugin), Rel);
            var rr3 = GridCell(3, null);
            rr3.NavigationMeshes.Add(Square(SA, 12600, 500, false)); rr3.NavigationMeshes.Add(Square(SB, 12900, 500, false));
            rv.Worldspaces.Add(World(rr3));
            rv.BeginWrite.ToPath(Path.Combine(dir, "Revert.esp")).WithLoadOrder(gsr).Write();
            return dir;
        }
        NavigationMesh VSA() => Square(SA, 12600, 500, false, s: (SS, 0), w: (SO, 0));
        NavigationMesh VSB() => Square(SB, 12900, 500, false, s: (SS, 2), e: (SR, 1));
        NavigationMesh VSO() => Square(SO, 12300, 500, false, e: (SA, 1));
        NavigationMapInfo[] VanillaNavi() => new[]
        {
            Info(SA, 3, 12600, 500, SO, SS), Info(SB, 3, 12900, 500, SS, SR), Info(SO, 3, 12300, 500, SA), Info(SS, 3, 12600, 200, SA, SB, SQ),
            Info(SQ, 3, 12300, 200),
            Info(SR, 3, 13200, 500, SB), Info(SC, 3, 12600, 1500), Info(SX, 3, 12900, 1500), Info(SW, 3, 12300, 1500), Info(SDn, 3, 12600, 2500),
        };
        var sky = new SkyrimMod(SkyKey, Rel);
        var c3 = GridCell(3, Flat(LandK(3), 0));
        foreach (var n in new[]
        {
            VSA(), VSB(), VSO(), Strip((SA, 0), (SB, 0), (SQ, 0)), Square(SR, 13200, 500, false, w: (SB, 0)), Square(SQ, 12300, 200, false),
            Square(SW, 12300, 1500, false), Square(SC, 12600, 1500, false), Square(SX, 12900, 1500, false), Square(SDn, 12600, 2500, false),
        }) c3.NavigationMeshes.Add(n);
        sky.Worldspaces.Add(World(c3));
        sky.NavigationMeshInfoMaps.Add(Navi(VanillaNavi()));
        sky.BeginWrite.ToPath(sp).WithLoadOrder(Array.Empty<ISkyrimModGetter>()).Write();
        using var skyRead = SkyrimMod.CreateFromBinaryOverlay(sp, Rel);

        var p = new SkyrimMod(new ModKey("SetP", ModType.Plugin), Rel);
        var p3 = GridCell(3, null);
        var pSB = Square(SB, 12900, 500, true, s: (SS, 2), e: (SR, 1), w: (SA, 1));
        pSB.Data!.EdgeLinks.Single(l => l.Mesh.FormKey == SR).Unknown = 2;   // a Ledge Down into P's SR
        var pSR = Ledge(Square(SR, 13200, 500, false, w: (SB, 1)), 1);          // a Ledge Up back, into P's numbering
        var pSD = Square(SDn, 12600, 2500, true);
        pSD.Data!.DoorTriangles.Add(new DoorTriangle { Door = new FormLink<IPlacedObjectGetter>(DZ), TriangleBeforeDoor = 0, Unknown = HarnessShared.NavRepairNavi.LinkedDoorUnknown });
        foreach (var n in new[]
        {
            Square(SA, 12600, 500, true, s: (SS, 0), e: (SB, 3), w: (SO, 0)), pSB, pSR, Square(SO, 12300, 500, false),
            Square(SC, 12600, 1500, true, e: (SX, 1), w: (SW, 0)), Square(SW, 12300, 1500, false), pSD,
        }) p3.NavigationMeshes.Add(n);
        p.Worldspaces.Add(World(p3));
        var sbInfo = Island(Info(SB, 3, 12900, 500, SA, SS, SR), Isl((12900, 500, 0), (13200, 800, 0), new[] { (12900f, 500f, 0f), (13200f, 500f, 0f), (13200f, 800f, 0f) }, (0, 1, 2)));
        var sdInfo = Info(SDn, 3, 12600, 2500);
        sdInfo.LinkedDoors.Add(new LinkedDoor { Unknown = HarnessShared.NavRepairNavi.LinkedDoorUnknown, Door = new FormLink<IPlacedObjectGetter>(DZ) });
        p.NavigationMeshInfoMaps.Add(Navi(Info(SA, 3, 12600, 500, SO, SS, SB), sbInfo, Info(SR, 3, 13200, 500, SB), Info(SO, 3, 12300, 500),
                                          Info(SC, 3, 12600, 1500, SW, SX), Info(SW, 3, 12300, 1500), sdInfo));
        p.BeginWrite.ToPath(Path.Combine(dir, "SetP.esp")).WithLoadOrder(skyRead).Write();

        var rev = new SkyrimMod(new ModKey("Revert", ModType.Plugin), Rel);
        var r3 = GridCell(3, null);
        r3.NavigationMeshes.Add(VSA()); r3.NavigationMeshes.Add(VSO());
        r3.NavigationMeshes.Add(Square(SC, 12600, 1500, false)); r3.NavigationMeshes.Add(Square(SDn, 12600, 2500, false));   // so SetP's SC and SD lose
        rev.Worldspaces.Add(World(r3));
        rev.NavigationMeshInfoMaps.Add(Navi(VanillaNavi()));
        rev.BeginWrite.ToPath(Path.Combine(dir, "Revert.esp")).WithLoadOrder(skyRead).Write();

        var touch = new SkyrimMod(new ModKey("Touch", ModType.Plugin), Rel);
        var t3 = GridCell(3, null);
        t3.NavigationMeshes.Add(VSB());
        touch.Worldspaces.Add(World(t3));
        touch.BeginWrite.ToPath(Path.Combine(dir, "Touch.esp")).WithLoadOrder(skyRead).Write();
        return dir;
    }

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
        ic.NavigationMeshes.Add(Nav(SL, InIC, Quad(1000), new T3(0, 1, 2, E01: (TD, 0)), new T3(1, 3, 2, E12: (TD, 1))));
        ic.NavigationMeshes.Add(Nav(SD, InIC, Tri1(2000), new T3(0, 1, 2)));
        ic.NavigationMeshes.Add(Nav(TT, InIC, Quad(3000), new T3(0, 1, 2), new T3(1, 3, 2)));
        ic.NavigationMeshes.Add(Nav(TE, InIC, Quad(4000), new T3(0, 1, 2), new T3(1, 3, 2)));
        ic.NavigationMeshes.Add(Nav(TV, InIC, Tri1(5000), new T3(0, 1, 2)));
        ic.NavigationMeshes.Add(Nav(SV, InIC, Tri1(6000), new T3(0, 1, 2, E01: (TV, 0))));
        File(sky, ic);
        var c0 = GridCell(0, Flat(LandK(0), 0)); var c1 = GridCell(1, Flat(LandK(1), 0));
        var c3 = GridCell(3, Flat(LandK(3), 0)); var c4 = GridCell(4, Flat(LandK(4), 0)); var c5 = GridCell(5, Flat(LandK(5), 0));
        c0.NavigationMeshes.Add(L10());
        c1.NavigationMeshes.Add(R10());
        foreach (int k in new[] { 1, 2, 3, 4, 5, 6, 7, 8, 9, 11, 12, 13 })
        {
            short borderAt = k switch { 5 => 3, 6 => 2, 7 => 1, 11 => 2, _ => 0 };
            c0.NavigationMeshes.Add(Left(k, R(k), borderAt));
            var tris = new List<T3>();
            for (int i = 0; i < borderAt; i++) tris.Add(Away);
            tris.Add(RightBorder((L(k), 0)));
            if (k == 2) tris.Add(Away);                       // R2 = [border, away]
            c1.NavigationMeshes.Add(k == 9 ? R9() : Nav(R(k), InGrid(1), RightV(k), tris.ToArray()));
        }
        c3.NavigationMeshes.Add(Fan(G1, 3, 12800, 500, 0));
        c3.NavigationMeshes.Add(Fan(G2, 3, 13600, 500, 600));
        c3.NavigationMeshes.Add(Nav(G6, InGrid(3), new[] { (14600f, 500f, 0f), (14900f, 500f, 0f), (14900f, 800f, 0f), (14600f, 800f, 0f), (14400f, 650f, 300f) },
                                    new T3(0, 1, 2), new T3(0, 2, 3), new T3(0, 3, 4)));
        c4.NavigationMeshes.Add(Fan(G3, 4, 16800, 500, 0));
        c4.NavigationMeshes.Add(Fan(G4, 4, 17600, 500, 0));
        c5.NavigationMeshes.Add(Fan(G5, 5, 20800, 500, 0));
        c0.Temporary.Add(new PlacedObject(DR, Rel) { EditorID = "RpNavDoor", Placement = new Placement { Position = new P3Float(3900, 3100, 0) } });
        sky.Worldspaces.Add(World(c0, c1, c3, c4, c5));
        var l6 = Info(L(6), 0, 3996, 3100, R(6));
        l6.LinkedDoors.Add(new LinkedDoor { Unknown = 7, Door = new FormLink<IPlacedObjectGetter>(DR) });
        var isl = new IslandData { Min = new P3Float(3896, 3000, 0), Max = new P3Float(4096, 3300, 0) };
        isl.Vertices.AddRange(new[] { new P3Float(3896, 3000, 0), new P3Float(4096, 3000, 0), new P3Float(4096, 3300, 0) });
        isl.Triangles.Add(new P3Int16(0, 1, 2));
        l6.Island = isl;
        l6.Unknown = 32;   // an island's flag, as on every island of the base game (CORRECTED 2026-11-27, R7: was 0)
        var infos = new List<NavigationMapInfo>();
        foreach (var (cell, gx) in new[] { (c0, 0), (c1, 1), (c3, 3), (c4, 4), (c5, 5) })
            foreach (var n in cell.NavigationMeshes)
            {
                if (n.FormKey == L(6)) { infos.Add(l6); continue; }
                var d = n.Data!;
                infos.Add(Info(n.FormKey, gx, d.Min.X, d.Min.Y, d.EdgeLinks.Select(e => e.Mesh.FormKey).Distinct().ToArray()));
            }
        sky.NavigationMeshInfoMaps.Add(Navi(infos.ToArray()));
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
        a1.NavigationMeshes.Add(Nav(R(1), InGrid(1), RightV(1), RightBorder((L(1), 0))));   // forwarded unchanged: a mod touches pair 1
        a1.NavigationMeshes.Add(Nav(R(2), InGrid(1), RightV(2), Away, RightBorder((L(2), 0))));
        a1.NavigationMeshes.Add(Nav(R(3), InGrid(1), RightV(3), RightBorder(null)));
        a1.NavigationMeshes.Add(Nav(R(4), InGrid(1), RightV(4), Away));
        a1.NavigationMeshes.Add(Nav(R4a, InGrid(1), RightV(4, 2), RightBorder((L(4), 0))));
        a1.NavigationMeshes.Add(Nav(R4b, InGrid(1), RightV(4, 5), RightBorder((L(4), 0))));
        a1.NavigationMeshes.Add(Nav(R(5), InGrid(1), RightV(5), Away, RightBorder((L(5), 0))));
        a1.NavigationMeshes.Add(Nav(R(6), InGrid(1), RightV(6), Away));
        a1.NavigationMeshes.Add(Nav(R6n, InGrid(1), RightV(6), RightBorder((L(6), 0))));
        a1.NavigationMeshes.Add(Nav(R(7), InGrid(1), RightV(7), Away));
        a1.NavigationMeshes.Add(Nav(R(8), InGrid(1), RightV(8).Select(v => (v.Item1, v.Item2, v.Item3 + 200)).ToArray(), RightBorder((L(8), 0))));
        a1.NavigationMeshes.Add(R9());
        a1.NavigationMeshes.Add(R10());                                                      // forwarded unchanged: a mod touches pair 10
        a1.NavigationMeshes.Add(Nav(R(11), InGrid(1), RightV(11), Away, RightBorder(null)));
        a1.NavigationMeshes.Add(Nav(R(12), InGrid(1), RightV(12, 12), RightBorder((L(12), 0))));
        a1.NavigationMeshes.Add(Nav(R(13), InGrid(1), RightV(13).Select(v => (v.Item1, v.Item2, v.Item3 + 100)).ToArray(), RightBorder((L(13), 0))));
        var a3 = GridCell(3, Flat(LandK(3), 200));
        var a4 = GridCell(4, Flat(LandK(4), 200));
        a4.NavigationMeshes.Add(Fan(G3, 4, 16800, 500, 200));
        var a5 = GridCell(5, Flat(LandK(5), 200));
        a5.NavigationMeshes.Add(Fan(G5, 5, 20800, 500, 200));
        a.Worldspaces.Add(World(a1, a3, a4, a5));
        a.NavigationMeshInfoMaps.Add(Navi(Info(R(5), 1, 4196, 2600, L(5)), Info(R6n, 1, 4196, 3100), Info(R(6), 1, 4496, 3100)));
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
        // RpD renames the border cell (0,0): a patch carrying that cell from the navmeshes' own plugin reverts the name
        var d0 = GridCell(0, null); d0.EditorID = "RpBorderCell";
        dmod.Worldspaces.Add(World(d0, d4, GridCell(5, Flat(LandK(5), 400))));
        Save(dmod, SkyKey);

        foreach (var o in overlays.Values) (o as IDisposable)?.Dispose();
        return written;
    }
}
