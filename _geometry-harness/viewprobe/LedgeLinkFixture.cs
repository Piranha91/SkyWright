using HarnessShared;
using Mutagen.Bethesda;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Skyrim;
using Noggog;
using SkCell = Mutagen.Bethesda.Skyrim.Cell;

namespace ViewProbe;

// THE LEDGE-LINK FIXTURE: three flat 4 x 4 grids of 64 u squares (32 triangles each) joined only by typed cross-mesh
// links (NAVM edge-link Type, Mutagen EdgeLink.Unknown: 0 Portal, 1 Ledge Up, 2 Ledge Down, 3 Enable/Disable Portal).
// Test-only, written with Mutagen. The same three meshes go into CfFixture.esp's interior cell CfLedge (the --route-solve
// known answers, tests/test_ledge_link_counts.py) and into a one-cell worldspace (the navdump known answer in
// --moved-ref-selftest).
//
//   L (lower)  x -256..0,  y -128..128, z 0
//   U (upper)  x 0..256,   y -128..128, z 200   L's east edge and U's west edge meet in plan at x 0
//   S (side)   x -256..0,  y 128..384,  z 0     L's north edge and S's south edge meet at y 128
//
//   L east  -> U west   Type 1 (Ledge Up)            4 links
//   U west  -> L east   Type 2 (Ledge Down)          4 links
//   L north -> S south  Type 3 (Enable/Disable)      4 links
//   S south -> L north  Type 3 on squares 0-2, Type 7 (no xEdit type) on square 3: 3 + 1 links
//
// Under NavGraph's default policy (walk Ledge Down, hold Ledge Up and Enable/Disable, never walk an unknown type): 96
// triangles, 4 up + 4 down, 7 enable/disable, 1 unknown type, 12 held, 4 cross-mesh links wired; L and U are one piece
// (a drop joins them), S is a second piece; no path climbs from L to U or crosses into S.
public static class LedgeLinkFixture
{
    public const int N = 4;
    public const float Step = 64f, Rise = 200f;
    public const int Up = 4, Down = 4, Toggle = 7, Unknown = 1, Held = 12, Triangles = 96;

    // A grid square (i, j) holds triangle 2(jN+i) = (V(i,j), V(i+1,j), V(i+1,j+1)) and 2(jN+i)+1 = (V(i,j), V(i+1,j+1), V(i,j+1)).
    // East edge (i = N-1): the first triangle's edge 1-2 (flag 0x02). West edge (i = 0): the second's edge 2-0 (0x04).
    // North edge (j = N-1): the second's edge 1-2 (0x02). South edge (j = 0): the first's edge 0-1 (0x01).
    static int East(int j) => 2 * (j * N + N - 1);
    static int West(int j) => 2 * (j * N) + 1;
    static int North(int i) => 2 * ((N - 1) * N + i) + 1;
    static int South(int i) => 2 * i;

    static void Link(NavigationMeshData d, int tri, int edgeBit, FormKey to, int toTri, int type)
    {
        var t = (NavmeshTriangle)d.Triangles[tri];
        short idx = (short)d.EdgeLinks.Count;
        d.EdgeLinks.Add(new EdgeLink { Mesh = new FormLink<INavigationMeshGetter>(to), TriangleIndex = (short)toTri, Unknown = type });
        if (edgeBit == 0x01) t.EdgeLink_0_1 = idx; else if (edgeBit == 0x02) t.EdgeLink_1_2 = idx; else t.EdgeLink_2_0 = idx;
        t.Flags |= (NavmeshTriangle.Flag)edgeBit;
    }

    /// <summary>The three navmeshes, parented by <paramref name="parent"/> (a cell or a worldspace square).</summary>
    public static NavigationMesh[] Meshes(FormKey l, FormKey u, FormKey s, Func<ANavmeshParent> parent)
    {
        NavigationMeshData Grid(FormKey k, float x0, float y0, float z)
        {
            var d = BlockFixture.Grid(k, FormKey.Null, N, N, Step, x0, y0).Data!;
            d.Parent = parent();
            for (int v = 0; v < d.Vertices.Count; v++) d.Vertices[v] = new P3Float(d.Vertices[v].X, d.Vertices[v].Y, z);
            d.Min = new P3Float(d.Min.X, d.Min.Y, z); d.Max = new P3Float(d.Max.X, d.Max.Y, z);
            return d;
        }
        var dl = Grid(l, -N * Step, -N * Step / 2, 0);
        var du = Grid(u, 0, -N * Step / 2, Rise);
        var ds = Grid(s, -N * Step, N * Step / 2, 0);
        for (int j = 0; j < N; j++)
        {
            Link(dl, East(j), 0x02, u, West(j), 1);
            Link(du, West(j), 0x04, l, East(j), 2);
        }
        for (int i = 0; i < N; i++)
        {
            Link(dl, North(i), 0x02, s, South(i), 3);
            Link(ds, South(i), 0x01, l, North(i), i < N - 1 ? 3 : 7);
        }
        return new[] { new NavigationMesh(l, CaptureFixture.Rel) { Data = dl }, new NavigationMesh(u, CaptureFixture.Rel) { Data = du },
                       new NavigationMesh(s, CaptureFixture.Rel) { Data = ds } };
    }

    // ---- the worldspace copy, for the navdump: LlWorld with one exterior cell at grid (0, 0) ----
    public static readonly ModKey WorldKey = new("LlWorld", ModType.Plugin);
    public static readonly FormKey World = new(WorldKey, 0x800), Cell00 = new(WorldKey, 0x801), PlaneBase = new(WorldKey, 0x802),
        L = new(WorldKey, 0x810), U = new(WorldKey, 0x811), S = new(WorldKey, 0x812);

    /// <summary>Write LlWorld.esp into <paramref name="dir"/> and return its path.</summary>
    public static string WriteWorld(string dir)
    {
        Directory.CreateDirectory(dir);
        var m = new SkyrimMod(WorldKey, CaptureFixture.Rel);
        m.Statics.Add(new Static(PlaneBase, CaptureFixture.Rel) { EditorID = "LlPlaneMarker" });
        var c = new SkCell(Cell00, CaptureFixture.Rel) { Flags = default, Grid = new CellGrid { Point = new P2Int(0, 0) } };
        foreach (var nav in Meshes(L, U, S, () => new WorldspaceNavmeshParent
                 { Parent = new FormLink<IWorldspaceGetter>(World), Coordinates = new P2Int16(0, 0) }))
            c.NavigationMeshes.Add(nav);
        var ws = new Worldspace(World, CaptureFixture.Rel) { EditorID = "LlWorld" };
        var blk = new WorldspaceBlock { BlockNumberX = 0, BlockNumberY = 0, GroupType = GroupTypeEnum.ExteriorCellBlock };
        var sub = new WorldspaceSubBlock { BlockNumberX = 0, BlockNumberY = 0, GroupType = GroupTypeEnum.ExteriorCellSubBlock };
        sub.Items.Add(c); blk.Items.Add(sub); ws.SubCells.Add(blk);
        m.Worldspaces.Add(ws);
        var path = Path.Combine(dir, WorldKey.FileName.String);
        m.BeginWrite.ToPath(path).WithNoLoadOrder().Write();
        return path;
    }
}
