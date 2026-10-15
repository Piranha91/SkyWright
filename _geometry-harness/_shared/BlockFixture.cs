using System.Numerics;
using Mutagen.Bethesda;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Skyrim;
using NiflySharp.Blocks;
using Noggog;
using SkCell = Mutagen.Bethesda.Skyrim.Cell;

namespace HarnessShared;

// THE ROUTE-RULE FIXTURE (blockprobe --self-test, probe-decisions slice 3): two interior cells with flat navmesh grids and
// three objects standing on one of them, each with the same box-collision mesh, so the route rule's set-aside choice is
// reached; and one reference a later plugin moves to the other cell. Test-only; written with Mutagen and NiflySharp
// (_shared/NifBoxFixture.cs). Expectations are stated by each test, worked out by hand from this table.
//
// Load order: BkMaster.esm, BkMover.esp (masters BkMaster). The run's target is BkMaster.esm.
//
//   ref  base                    master: I1 at              BkMover
//   Dr   DOOR   BkDoor           (-400, 0)                  -
//   Ms   MSTT   BkPushable       (0, 0)                     -
//   Sx   STAT   BkWall           (400, 0)                   -
//   Rm   STAT   BkWall           (0, 400)                   to I2 (0, 400)
// I1 and I2 each hold one navmesh: a 20 x 20 grid of 64 u squares from -640 to 640, flat at z 0, two triangles a square,
// linked to their neighbours. The box mesh (meshes\pd3\box.nif, loose) is one rigid body with a bhkBoxShape of half
// extents 1 x 1 x 1 Havok units (about 70 u), centred on the reference: it stands from z -70 to 70.
public static class BlockFixture
{
    public const SkyrimRelease Rel = SkyrimRelease.SkyrimSE;
    public static readonly ModKey MasterKey = new("BkMaster", ModType.Master), MoverKey = new("BkMover", ModType.Plugin);
    static FormKey Mk(uint id) => new(MasterKey, id);
    public static readonly FormKey DoorBase = Mk(0x801), PushBase = Mk(0x802), WallBase = Mk(0x803), I1 = Mk(0x810), I2 = Mk(0x811),
                                   Nav1 = Mk(0x820), Nav2 = Mk(0x821);
    public static readonly FormKey Dr = Mk(0x901), Ms = Mk(0x902), Sx = Mk(0x903), Rm = Mk(0x904);
    public const string MeshRel = "pd3\\box.nif";

    /// <summary>The plugins in load order, and a mods folder holding the mesh loose under the master's mod folder
    /// (mods\BkMaster\meshes\pd3\box.nif), as an MO2 instance would.</summary>
    public sealed record Written(IReadOnlyList<string> Order, string ModsDir);

    static PlacedObject Ref(FormKey key, FormKey baseKey, float x, float y)
    {
        var r = new PlacedObject(key, Rel) { Placement = new Placement { Position = new(x, y, 0), Rotation = new(0, 0, 0) } };
        r.Base.SetTo(baseKey);
        return r;
    }
    static SkCell Cell(FormKey key, string edid) => new(key, Rel) { EditorID = edid, Flags = SkCell.Flag.IsInteriorCell };

    /// <summary>A flat grid navmesh in an interior cell: n x n squares of <paramref name="step"/> from <paramref name="from"/>,
    /// each split into two triangles, every shared edge linked.</summary>
    static NavigationMesh Grid(FormKey key, FormKey cell, int n = 20, float step = 64f, float from = -640f)
        => Grid(key, cell, n, n, step, from, from);

    /// <summary>A flat grid navmesh in an interior cell: <paramref name="nx"/> x <paramref name="ny"/> squares of
    /// <paramref name="step"/> from (<paramref name="fromX"/>, <paramref name="fromY"/>), each split into two triangles,
    /// every shared edge linked. A triangle whose centroid <paramref name="deleted"/> picks carries the Deleted flag (0x08).</summary>
    public static NavigationMesh Grid(FormKey key, FormKey cell, int nx, int ny, float step, float fromX, float fromY,
                                      Func<float, float, bool>? deleted = null)
    {
        var d = new NavigationMeshData
        {
            NavmeshVersion = 12, CrcHash = 0xA5E9A03C,
            Parent = new CellNavmeshParent { Parent = new FormLink<ICellGetter>(cell) },
            Min = new P3Float(fromX, fromY, 0), Max = new P3Float(fromX + nx * step, fromY + ny * step, 0),
        };
        for (int j = 0; j <= ny; j++)
            for (int i = 0; i <= nx; i++) d.Vertices.Add(new P3Float(fromX + i * step, fromY + j * step, 0));
        short V(int i, int j) => (short)(j * (nx + 1) + i);
        var tris = new List<(short A, short B, short C)>();
        for (int j = 0; j < ny; j++)
            for (int i = 0; i < nx; i++)
            {
                tris.Add((V(i, j), V(i + 1, j), V(i + 1, j + 1)));
                tris.Add((V(i, j), V(i + 1, j + 1), V(i, j + 1)));
            }
        var byEdge = new Dictionary<(short, short), List<int>>();
        (short, short) E(short a, short b) => a < b ? (a, b) : (b, a);
        for (int t = 0; t < tris.Count; t++)
            foreach (var e in new[] { E(tris[t].A, tris[t].B), E(tris[t].B, tris[t].C), E(tris[t].C, tris[t].A) })
            { if (!byEdge.TryGetValue(e, out var l)) byEdge[e] = l = new List<int>(); l.Add(t); }
        short Other(int t, short a, short b) => byEdge[E(a, b)].Where(x => x != t).Select(x => (short)x).DefaultIfEmpty((short)-1).First();
        for (int t = 0; t < tris.Count; t++)
        {
            var (a, b, c) = tris[t];
            var tri = new NavmeshTriangle
            {
                Vertices = new P3Int16(a, b, c),
                EdgeLink_0_1 = Other(t, a, b), EdgeLink_1_2 = Other(t, b, c), EdgeLink_2_0 = Other(t, c, a),
            };
            if (deleted is not null)
            {
                float cx = (d.Vertices[a].X + d.Vertices[b].X + d.Vertices[c].X) / 3f, cy = (d.Vertices[a].Y + d.Vertices[b].Y + d.Vertices[c].Y) / 3f;
                if (deleted(cx, cy)) tri.Flags |= NavmeshTriangle.Flag.Deleted;
            }
            d.Triangles.Add(tri);
        }
        return new NavigationMesh(key, Rel) { Data = d };
    }

    /// <summary>The box mesh's bytes: one rigid body, a bhkBoxShape of half extents 1 x 1 x 1 Havok units.</summary>
    public static byte[] BoxNif()
    {
        var f = NifBoxFixture.NewFile();
        NifBoxFixture.Attach(f, new bhkBoxShape { Dimensions = Vector3.One });
        return NifBoxFixture.Save(f);
    }

    /// <summary>Write the two plugins into <paramref name="dir"/> and the mesh under dir\mods.</summary>
    public static Written Write(string dir)
    {
        Directory.CreateDirectory(dir);
        string Save(SkyrimMod mod, params ISkyrimModGetter[] masters)
        {
            var path = Path.Combine(dir, mod.ModKey.FileName.String);
            mod.BeginWrite.ToPath(path).WithLoadOrder(masters).Write();
            return path;
        }

        var m = new SkyrimMod(MasterKey, Rel);
        m.Doors.Add(new Door(DoorBase, Rel) { EditorID = "BkDoor", Model = new Model { File = MeshRel } });
        m.MoveableStatics.Add(new MoveableStatic(PushBase, Rel) { EditorID = "BkPushable", Model = new Model { File = MeshRel } });
        m.Statics.Add(new Static(WallBase, Rel) { EditorID = "BkWall", Model = new Model { File = MeshRel } });
        var i1 = Cell(I1, "BkRoomOne");
        i1.Temporary.Add(Ref(Dr, DoorBase, -400, 0)); i1.Temporary.Add(Ref(Ms, PushBase, 0, 0));
        i1.Temporary.Add(Ref(Sx, WallBase, 400, 0)); i1.Temporary.Add(Ref(Rm, WallBase, 0, 400));
        i1.NavigationMeshes.Add(Grid(Nav1, I1));
        var i2 = Cell(I2, "BkRoomTwo");
        i2.NavigationMeshes.Add(Grid(Nav2, I2));
        MovedRefFixture.FileInterior(m, i1); MovedRefFixture.FileInterior(m, i2);
        var mPath = Save(m);
        using var mOv = SkyrimMod.CreateFromBinaryOverlay(mPath, Rel);

        var mv = new SkyrimMod(MoverKey, Rel);
        var c2 = Cell(I2, "BkRoomTwo");
        c2.Temporary.Add(Ref(Rm, WallBase, 0, 400));
        MovedRefFixture.FileInterior(mv, c2);
        var mvPath = Save(mv, mOv);

        var mods = Path.Combine(dir, "mods");
        var mesh = Path.Combine(mods, MasterKey.Name, "meshes", MeshRel);
        Directory.CreateDirectory(Path.GetDirectoryName(mesh)!);
        File.WriteAllBytes(mesh, BoxNif());
        return new Written(new[] { mPath, mvPath }, mods);
    }
}
