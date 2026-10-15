using Mutagen.Bethesda;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Plugins.Records;
using Mutagen.Bethesda.Skyrim;
using Noggog;
using SkCell = Mutagen.Bethesda.Skyrim.Cell;

// THE CLOBBER AND DANGLING-LINK FIXTURE: one small synthetic order, written with Mutagen, for navownprobe's own
// --moved-ref-selftest. Test-only; no game data. The expectations are NOT computed here: each case in NavownSelfTest.cs
// states its own, worked out by hand from the table below.
//
// Load order: NvMaster.esm, Dawnguard.esm, HearthFires.esm, Unofficial Skyrim Special Edition Patch.esp, NvA.esp, NvB.esp,
// NvFix.esp. The two .esm names and the USSEP name are the probe's own file-name populations, used on purpose. Every
// navmesh is filed in one interior cell IC. G0 = one triangle (0,0)(400,0)(0,400); ISLAND = G0 plus a second triangle at
// (2000,2000), 1,600 u and more from G0; NEAR = G0 with its third vertex at (0,450), within 50 u of G0.
//
//   id     master        later plugins                                   answer (worked by hand)
//   NMOD   G0            NvA: ISLAND; NvB (masters only NvMaster): G0     CLOBBERED A->B, class C, material, predicate Holds
//   NFWD   G0            NvA: ISLAND; NvB: ISLAND (A's exact bytes)       clobbered, NOT material: no finding
//   NNEAR  G0            NvA: NEAR;   NvB: G0                             CLOBBERED A->B, class C, material, predicate Gone
//   NUS    G0            USSEP: ISLAND; NvB: G0                           CLOBBERED USSEP->B, class B, predicate Holds
//   NVAN   G0            Dawnguard: ISLAND; HearthFires: G0               clobbered, class A (both vanilla-named): withheld
//   NT     2 triangles   (NvFix: 3 triangles)                            the target of the edge links
//   NE     -             NvA: edge link into NT at triangle 2 (= NT's count)     EDGE-IDX
//   NE2    -             NvA: edge link into NT at triangle 1 (count - 1)        nothing
//   ND     -             NvA: 1 triangle, door triangle 1 (= its count), door DR1 DOOR-TRI
//   NG     -             NvA: 1 triangle, door triangle 0, door DR2              DOOR-GONE (DR2 deleted by NvB)
//   NX     -             NvA: edge link into NvA:0xBAE, which nothing defines    EDGE-NAV
//   DR1, DR2, DR4 placed doors in the master; DR2 deleted by NvB; DR4 links NT at triangle 5 (DOOR-IDX: retired, counted)
//   DR3    -             NvA: a placed door linking NvA:0xBAD, which nothing defines   DOOR-NAV
//   VF, VT Dawnguard defines both; VF edge-links VT (1 triangle) at triangle 5   EDGE-IDX, both ends vanilla-named: withheld
//   NvFix (masters NvA and NvB through two Static overrides): NT with 3 triangles; NMOD = ISLAND (A's ground restored).
//   Under --enable NvFix.esp: NMOD's and NE's base rows re-measure Gone; ND, NG, NX, DR3 and NUS stay Holds.
static class NavownFixture
{
    public const SkyrimRelease Rel = SkyrimRelease.SkyrimSE;
    public static readonly ModKey MasterKey = new("NvMaster", ModType.Master), DgKey = new("Dawnguard", ModType.Master),
        HfKey = new("HearthFires", ModType.Master), UsKey = new("Unofficial Skyrim Special Edition Patch", ModType.Plugin),
        AKey = new("NvA", ModType.Plugin), BKey = new("NvB", ModType.Plugin), FixKey = new("NvFix", ModType.Plugin);
    static FormKey Mk(uint id) => new(MasterKey, id);

    public static readonly FormKey IC = Mk(0x840), DoorBase = Mk(0x801);
    public static readonly FormKey NMOD = Mk(0x960), NFWD = Mk(0x961), NNEAR = Mk(0x962), NUS = Mk(0x963), NVAN = Mk(0x964), NT = Mk(0x965);
    public static readonly FormKey DR1 = Mk(0x901), DR2 = Mk(0x902), DR4 = Mk(0x904);
    public static readonly FormKey NE = new(AKey, 0x970), NE2 = new(AKey, 0x971), ND = new(AKey, 0x972), NG = new(AKey, 0x973), NX = new(AKey, 0x974),
        DR3 = new(AKey, 0x903), AbsentNav = new(AKey, 0xBAD), AbsentMesh = new(AKey, 0xBAE), AStat = new(AKey, 0x980);
    public static readonly FormKey VF = new(DgKey, 0x990), VT = new(DgKey, 0x991), BStat = new(BKey, 0x981);

    /// <summary>The fixture's plugins in load order, by file name. NvFix.esp is last; the base arm leaves it out.</summary>
    public static readonly string[] Order =
    {
        "NvMaster.esm", "Dawnguard.esm", "HearthFires.esm", "Unofficial Skyrim Special Edition Patch.esp", "NvA.esp", "NvB.esp", "NvFix.esp",
    };

    static readonly (float X, float Y)[] G0 = { (0, 0), (400, 0), (0, 400) };
    static readonly (float X, float Y)[] Island = { (0, 0), (400, 0), (0, 400), (2000, 2000), (2100, 2000), (2000, 2100) };
    static readonly (float X, float Y)[] Near = { (0, 0), (400, 0), (0, 450) };
    static readonly (float X, float Y)[] Two = { (0, 0), (400, 0), (0, 400), (400, 400) };
    static readonly (float X, float Y)[] Three = { (0, 0), (400, 0), (0, 400), (400, 400), (800, 0) };

    /// <summary>A navmesh in IC with the given vertices, one triangle per three vertices in a fan over vertex 0 for
    /// <paramref name="fan"/> (vertex count - 2 triangles) or consecutive triples otherwise.</summary>
    static NavigationMesh Nav(FormKey key, (float X, float Y)[] v, bool fan = false,
                              (FormKey Mesh, short Tri)[]? edges = null, (FormKey Door, short Tri)[]? doors = null)
    {
        var d = new NavigationMeshData
        {
            NavmeshVersion = 12, CrcHash = 0xA5E9A03C,
            Parent = new CellNavmeshParent { Parent = new FormLink<ICellGetter>(IC) },
            Min = new P3Float(v.Min(p => p.X), v.Min(p => p.Y), 0), Max = new P3Float(v.Max(p => p.X), v.Max(p => p.Y), 0),
        };
        foreach (var (x, y) in v) d.Vertices.Add(new P3Float(x, y, 0));
        if (fan) for (short i = 1; i + 1 < v.Length; i++) d.Triangles.Add(Tri(0, i, (short)(i + 1)));
        else for (short i = 0; i + 2 < v.Length; i += 3) d.Triangles.Add(Tri(i, (short)(i + 1), (short)(i + 2)));
        foreach (var (m, t) in edges ?? Array.Empty<(FormKey, short)>())
            d.EdgeLinks.Add(new EdgeLink { Mesh = new FormLink<INavigationMeshGetter>(m), TriangleIndex = t });
        foreach (var (door, t) in doors ?? Array.Empty<(FormKey, short)>())
            d.DoorTriangles.Add(new DoorTriangle { Door = new FormLink<IPlacedObjectGetter>(door), TriangleBeforeDoor = t });
        return new NavigationMesh(key, Rel) { Data = d };
    }
    static NavmeshTriangle Tri(short a, short b, short c)
        => new() { Vertices = new P3Int16(a, b, c), EdgeLink_0_1 = -1, EdgeLink_1_2 = -1, EdgeLink_2_0 = -1 };
    static PlacedObject Door(FormKey key, float x, FormKey? linkNav = null, short linkTri = 0)
    {
        var r = new PlacedObject(key, Rel) { Placement = new Placement { Position = new P3Float(x, 0, 0), Rotation = new P3Float(0, 0, 0) } };
        r.Base.SetTo(DoorBase);
        if (linkNav is { } n) r.NavigationDoorLink = new NavigationDoorLink { NavMesh = new FormLink<INavigationMeshGetter>(n), TeleportMarkerTriangle = linkTri };
        return r;
    }
    static SkCell Int() => new(IC, Rel) { Flags = SkCell.Flag.IsInteriorCell };
    static void File(SkyrimMod mod, SkCell cell)
    {
        uint id = cell.FormKey.ID;
        int blockN = (int)(id % 10), subN = (int)((id / 10) % 10);
        var block = new CellBlock { BlockNumber = blockN, GroupType = GroupTypeEnum.InteriorCellBlock };
        var sub = new CellSubBlock { BlockNumber = subN, GroupType = GroupTypeEnum.InteriorCellSubBlock };
        sub.Cells.Add(cell); block.SubBlocks.Add(sub); mod.Cells.Records.Add(block);
    }

    /// <summary>Write the seven plugins into <paramref name="dir"/> (created) and return their paths in load order.</summary>
    public static IReadOnlyList<string> Write(string dir)
    {
        Directory.CreateDirectory(dir);
        var written = new List<string>();
        var overlays = new Dictionary<ModKey, ISkyrimModGetter>();
        string Save(SkyrimMod mod, params ModKey[] masters)
        {
            var path = Path.Combine(dir, mod.ModKey.FileName.String);
            mod.BeginWrite.ToPath(path).WithLoadOrder(masters.Select(k => overlays[k]).ToArray()).Write();
            overlays[mod.ModKey] = SkyrimMod.CreateFromBinaryOverlay(path, Rel);
            written.Add(path);
            return path;
        }

        // ---- the master: G0 everywhere, NT with two triangles, three doors ----
        var m = new SkyrimMod(MasterKey, Rel);
        m.Doors.Add(new Mutagen.Bethesda.Skyrim.Door(DoorBase, Rel) { EditorID = "NvDoor" });
        var mc = Int();
        foreach (var k in new[] { NMOD, NFWD, NNEAR, NUS, NVAN }) mc.NavigationMeshes.Add(Nav(k, G0));
        mc.NavigationMeshes.Add(Nav(NT, Two, fan: true));
        mc.Temporary.Add(Door(DR1, 10)); mc.Temporary.Add(Door(DR2, 20)); mc.Temporary.Add(Door(DR4, 40, NT, 5));
        File(m, mc);
        Save(m);

        // ---- Dawnguard.esm and HearthFires.esm: the vanilla-named clobber, and a dangle with only vanilla-named ends ----
        var dg = new SkyrimMod(DgKey, Rel);
        var dc = Int();
        dc.NavigationMeshes.Add(Nav(NVAN, Island));
        dc.NavigationMeshes.Add(Nav(VT, G0));
        dc.NavigationMeshes.Add(Nav(VF, G0, edges: new[] { (VT, (short)5) }));
        File(dg, dc);
        Save(dg, MasterKey);
        var hf = new SkyrimMod(HfKey, Rel);
        var hc = Int(); hc.NavigationMeshes.Add(Nav(NVAN, G0)); File(hf, hc);
        Save(hf, MasterKey, DgKey);

        // ---- USSEP: re-cuts NUS ----
        var us = new SkyrimMod(UsKey, Rel);
        var uc = Int(); uc.NavigationMeshes.Add(Nav(NUS, Island)); File(us, uc);
        Save(us, MasterKey, DgKey, HfKey);

        // ---- NvA: the re-cuts B discards, and the bad links ----
        var a = new SkyrimMod(AKey, Rel);
        a.Statics.Add(new Static(AStat, Rel) { EditorID = "NvAStat" });
        var ac = Int();
        ac.NavigationMeshes.Add(Nav(NMOD, Island)); ac.NavigationMeshes.Add(Nav(NFWD, Island)); ac.NavigationMeshes.Add(Nav(NNEAR, Near));
        ac.NavigationMeshes.Add(Nav(NE, G0, edges: new[] { (NT, (short)2) }));
        ac.NavigationMeshes.Add(Nav(NE2, G0, edges: new[] { (NT, (short)1) }));
        ac.NavigationMeshes.Add(Nav(ND, G0, doors: new[] { (DR1, (short)1) }));
        ac.NavigationMeshes.Add(Nav(NG, G0, doors: new[] { (DR2, (short)0) }));
        ac.NavigationMeshes.Add(Nav(NX, G0, edges: new[] { (AbsentMesh, (short)0) }));
        ac.Temporary.Add(Door(DR3, 30, AbsentNav, 0));
        File(a, ac);
        Save(a, MasterKey, DgKey, HfKey, UsKey);

        // ---- NvB: masters only NvMaster; G0 back over NMOD, NNEAR and NUS, A's bytes over NFWD; DR2 deleted ----
        var b = new SkyrimMod(BKey, Rel);
        b.Statics.Add(new Static(BStat, Rel) { EditorID = "NvBStat" });
        var bc = Int();
        bc.NavigationMeshes.Add(Nav(NMOD, G0)); bc.NavigationMeshes.Add(Nav(NFWD, Island));
        bc.NavigationMeshes.Add(Nav(NNEAR, G0)); bc.NavigationMeshes.Add(Nav(NUS, G0));
        bc.Temporary.Add(new PlacedObject(DR2, Rel) { IsDeleted = true });
        File(b, bc);
        Save(b, MasterKey, DgKey, HfKey, UsKey, AKey);

        // ---- NvFix: masters NvA and NvB (through their Statics); NT with three triangles; A's ground back on NMOD ----
        var f = new SkyrimMod(FixKey, Rel);
        f.Statics.Add(new Static(AStat, Rel) { EditorID = "NvAStat" });
        f.Statics.Add(new Static(BStat, Rel) { EditorID = "NvBStat" });
        var fc = Int();
        fc.NavigationMeshes.Add(Nav(NT, Three, fan: true));
        fc.NavigationMeshes.Add(Nav(NMOD, Island));
        File(f, fc);
        Save(f, MasterKey, DgKey, HfKey, UsKey, AKey, BKey);

        foreach (var o in overlays.Values) (o as IDisposable)?.Dispose();
        return written;
    }
}
