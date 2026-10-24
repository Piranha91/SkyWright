using Mutagen.Bethesda;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Skyrim;
using Noggog;
using SkCell = Mutagen.Bethesda.Skyrim.Cell;

// THE EDGE-LINK IDENTITY FIXTURE (`navownprobe --write-edge-link-fixture DIR`, read by tests/test_navown_edge_identity.py,
// which lays it out as an MO2 instance and runs the real probe and conflictdiff on it). Test-only; no game data. The
// expectations are in the test, worked out by hand from this table. Every navmesh is in one interior cell IC of ElBase.esm.
//
//   id    ElBase.esm                                   later plugins                      answer
//   T     8 triangles                                  ElA: 5 triangles (re-cut)          the target
//   S     4 triangles; links T at 5, 6, 7, 1           ElFix: links T at 2, 3, 7, 1       EDGE-IDX at 5, 6, 7; 1 is in range
//                                                      ElRen: 6 triangles; links T at 1, 7, 5, 6 (same links, renumbered)
//   S2    1 triangle; links T at 6                     -                                  EDGE-IDX at 6 (another source)
//   S3    1 triangle; links T at 7 twice               -                                  EDGE-IDX at 7, two links, one triangle
//   P, Q  2 triangles each; P links Q at 4, Q links P at 4   -                            EDGE-IDX both ways, one pair
//   NX    1 triangle; links ElBase:0xBAE (undefined)   -                                  EDGE-NAV (another kind)
//   ND    1 triangle; door triangle 1 (= count), DR    -                                  DOOR-TRI (another kind)
//
// Arms: base = ElBase.esm, ElA.esp. fix = base + ElFix.esp: S's links at 5 and 6 repaired, at 7 not. ren = base + ElRen.esp:
// S re-cut and its link table re-ordered, every link into the same target triangle as before.
static class EdgeLinkFixture
{
    public const SkyrimRelease Rel = SkyrimRelease.SkyrimSE;
    public static readonly ModKey BaseKey = new("ElBase", ModType.Master), AKey = new("ElA", ModType.Plugin),
        FixKey = new("ElFix", ModType.Plugin), RenKey = new("ElRen", ModType.Plugin);
    static FormKey Mk(uint id) => new(BaseKey, id);

    public static readonly FormKey IC = Mk(0x840), DoorBase = Mk(0x801), DR = Mk(0x901);
    public static readonly FormKey T = Mk(0x960), S = Mk(0x961), S2 = Mk(0x962), S3 = Mk(0x963), P = Mk(0x964), Q = Mk(0x965),
        NX = Mk(0x966), ND = Mk(0x967), Absent = Mk(0xBAE);

    public static readonly string[] Order = { "ElBase.esm", "ElA.esp", "ElFix.esp", "ElRen.esp" };

    /// <summary>A fan of <paramref name="tris"/> triangles over vertex 0, in IC.</summary>
    static NavigationMesh Nav(FormKey key, int tris, (FormKey Mesh, short Tri)[]? edges = null, (FormKey Door, short Tri)[]? doors = null,
                              float x0 = 0)
    {
        var d = new NavigationMeshData
        {
            NavmeshVersion = 12, CrcHash = 0xA5E9A03C,
            Parent = new CellNavmeshParent { Parent = new FormLink<ICellGetter>(IC) },
        };
        d.Vertices.Add(new P3Float(x0, 0, 0));
        for (int i = 0; i <= tris; i++) d.Vertices.Add(new P3Float(x0 + 100 * MathF.Cos(i * 0.3f), 100 * MathF.Sin(i * 0.3f), 0));
        d.Min = new P3Float(d.Vertices.Min(v => v.X), d.Vertices.Min(v => v.Y), 0);
        d.Max = new P3Float(d.Vertices.Max(v => v.X), d.Vertices.Max(v => v.Y), 0);
        for (short i = 1; i <= tris; i++)
            d.Triangles.Add(new NavmeshTriangle { Vertices = new P3Int16(0, i, (short)(i + 1)), EdgeLink_0_1 = -1, EdgeLink_1_2 = -1, EdgeLink_2_0 = -1 });
        foreach (var (m, t) in edges ?? Array.Empty<(FormKey, short)>())
            d.EdgeLinks.Add(new EdgeLink { Mesh = new FormLink<INavigationMeshGetter>(m), TriangleIndex = t });
        foreach (var (door, t) in doors ?? Array.Empty<(FormKey, short)>())
            d.DoorTriangles.Add(new DoorTriangle { Door = new FormLink<IPlacedObjectGetter>(door), TriangleBeforeDoor = t });
        return new NavigationMesh(key, Rel) { Data = d };
    }
    static SkCell Int() => new(IC, Rel) { Flags = SkCell.Flag.IsInteriorCell };
    static void File(SkyrimMod mod, SkCell cell)
    {
        uint id = cell.FormKey.ID;
        var block = new CellBlock { BlockNumber = (int)(id % 10), GroupType = GroupTypeEnum.InteriorCellBlock };
        var sub = new CellSubBlock { BlockNumber = (int)((id / 10) % 10), GroupType = GroupTypeEnum.InteriorCellSubBlock };
        sub.Cells.Add(cell); block.SubBlocks.Add(sub); mod.Cells.Records.Add(block);
    }

    /// <summary>Write the four plugins into <paramref name="dir"/> (created) and return their paths in <see cref="Order"/>.</summary>
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

        var m = new SkyrimMod(BaseKey, Rel);
        m.Doors.Add(new Mutagen.Bethesda.Skyrim.Door(DoorBase, Rel) { EditorID = "ElDoor" });
        var mc = Int();
        mc.NavigationMeshes.Add(Nav(T, 8));
        mc.NavigationMeshes.Add(Nav(S, 4, new[] { (T, (short)5), (T, (short)6), (T, (short)7), (T, (short)1) }, x0: 1000));
        mc.NavigationMeshes.Add(Nav(S2, 1, new[] { (T, (short)6) }, x0: 2000));
        mc.NavigationMeshes.Add(Nav(S3, 1, new[] { (T, (short)7), (T, (short)7) }, x0: 3000));
        mc.NavigationMeshes.Add(Nav(P, 2, new[] { (Q, (short)4) }, x0: 4000));
        mc.NavigationMeshes.Add(Nav(Q, 2, new[] { (P, (short)4) }, x0: 5000));
        mc.NavigationMeshes.Add(Nav(NX, 1, new[] { (Absent, (short)0) }, x0: 6000));
        mc.NavigationMeshes.Add(Nav(ND, 1, doors: new[] { (DR, (short)1) }, x0: 7000));
        var door = new PlacedObject(DR, Rel) { Placement = new Placement { Position = new P3Float(7000, 0, 0), Rotation = new P3Float(0, 0, 0) } };
        door.Base.SetTo(DoorBase);
        mc.Temporary.Add(door);
        File(m, mc);
        Save(m);

        var a = new SkyrimMod(AKey, Rel);
        var ac = Int(); ac.NavigationMeshes.Add(Nav(T, 5)); File(a, ac);
        Save(a, BaseKey);

        var f = new SkyrimMod(FixKey, Rel);
        var fc = Int(); fc.NavigationMeshes.Add(Nav(S, 4, new[] { (T, (short)2), (T, (short)3), (T, (short)7), (T, (short)1) }, x0: 1000)); File(f, fc);
        Save(f, BaseKey, AKey);

        var r = new SkyrimMod(RenKey, Rel);
        var rc = Int(); rc.NavigationMeshes.Add(Nav(S, 6, new[] { (T, (short)1), (T, (short)7), (T, (short)5), (T, (short)6) }, x0: 1000)); File(r, rc);
        Save(r, BaseKey, AKey);

        foreach (var o in overlays.Values) (o as IDisposable)?.Dispose();
        return written;
    }
}
