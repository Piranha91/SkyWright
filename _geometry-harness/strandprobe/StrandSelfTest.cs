using HarnessShared;
using Mutagen.Bethesda;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Plugins.Records;
using Mutagen.Bethesda.Skyrim;
using Noggog;
using SkyWright.Engine;
using SkCell = Mutagen.Bethesda.Skyrim.Cell;
using SkActivator = Mutagen.Bethesda.Skyrim.Activator;

// strandprobe --self-test: the known answers KS01-KS28 (Gate A §4 and the Gate A review's findings 2 and 3), on a synthetic
// order written with Mutagen (no game data, no meshes). Every expectation is written by hand below, before the rule.
//
// Plugins: SfMaster.esm (the bases, one interior cell per case with its navmesh: a flat 1024 x 1024 u floor of 128 triangles
// at z 0, centred on the origin); SfA.esp [SfMaster] places the subjects; SfA - Extra.esp [SfMaster, SfA] (SfA's own mod by
// name); SfD.esp [SfMaster] and SfB.esp [SfMaster] override navmeshes (SfD before SfB); SfB2.esp [SfMaster, SfA] overrides a
// navmesh with SfA among its masters; SfC.esp [SfMaster] places blockers; SfP.esp [SfMaster, SfA, SfB] is a patch.
// Primitive table (PrimitiveSolidity): SfCollisionMarker solid, SfRoomMarker not solid, SfUnmeasured has no entry.
// Unless a case says otherwise, SfC places SfCollisionMarker (64 u box) at (0, 450, 100) in the case's cell: the far blocker.
// The reach is 150 u and the tolerance 12 u; lights and markers are measured from their position, triggers by the band.
static class StrandSelfTest
{
    const SkyrimRelease Rel = SkyrimRelease.SkyrimSE;
    static readonly ModKey M = new("SfMaster", ModType.Master), A = new("SfA", ModType.Plugin), AX = ModKey.FromNameAndExtension("SfA - Extra.esp"),
        D = new("SfD", ModType.Plugin), B = new("SfB", ModType.Plugin), B2 = new("SfB2", ModType.Plugin), C = new("SfC", ModType.Plugin),
        P = new("SfP", ModType.Plugin);
    static FormKey K(ModKey m, uint id) => new(m, id);
    static readonly FormKey Light = K(M, 0x800), Stat = K(M, 0x801), Missing = K(M, 0x802), Idle = K(M, 0x803), Bk = K(M, 0x804), Act = K(M, 0x805),
        Trig = K(M, 0x806), Cm = K(M, 0x807), Rm = K(M, 0x808), Unk = K(M, 0x809), Marker = K(M, 0x80A), AThing = K(A, 0xA00), BThing = K(B, 0xB00);
    static FormKey CellKey(int n) => K(M, 0x900u + (uint)n);
    static FormKey NavKey(int n) => K(M, 0xC00u + (uint)n);

    sealed class Build
    {
        public readonly SkyrimMod Mod;
        public readonly Dictionary<FormKey, SkCell> Cells = new();
        uint _next = 0x800;
        public Build(ModKey k) => Mod = new SkyrimMod(k, Rel);
        public FormKey New() => new(Mod.ModKey, _next++);
        public SkCell Cell(int n) => Cells.TryGetValue(CellKey(n), out var c) ? c : Cells[CellKey(n)] = new SkCell(CellKey(n), Rel) { EditorID = $"SfRoom{n:00}", Flags = SkCell.Flag.IsInteriorCell };
        public void File()
        {
            foreach (var c in Cells.Values)
            {
                uint id = c.FormKey.ID; int blockN = (int)(id % 10), subN = (int)((id / 10) % 10);
                var block = Mod.Cells.Records.FirstOrDefault(b => b.BlockNumber == blockN);
                if (block is null) { block = new CellBlock { BlockNumber = blockN, GroupType = GroupTypeEnum.InteriorCellBlock }; Mod.Cells.Records.Add(block); }
                var sub = block.SubBlocks.FirstOrDefault(s => s.BlockNumber == subN);
                if (sub is null) { sub = new CellSubBlock { BlockNumber = subN, GroupType = GroupTypeEnum.InteriorCellSubBlock }; block.SubBlocks.Add(sub); }
                sub.Cells.Add(c);
            }
        }
    }

    static NavigationMesh Floor(FormKey key, FormKey cell, float z, Func<float, float, bool>? deleted = null)
    {
        var d = new NavigationMeshData
        {
            NavmeshVersion = 12, CrcHash = 0xA5E9A03C, Parent = new CellNavmeshParent { Parent = new FormLink<ICellGetter>(cell) },
            Min = new P3Float(-512, -512, z), Max = new P3Float(512, 512, z),
        };
        for (int j = 0; j <= 8; j++) for (int i = 0; i <= 8; i++) d.Vertices.Add(new P3Float(-512 + 128 * i, -512 + 128 * j, z));
        for (int j = 0; j < 8; j++)
            for (int i = 0; i < 8; i++)
            {
                short v0 = (short)(j * 9 + i), v1 = (short)(v0 + 1), v2 = (short)(v0 + 9), v3 = (short)(v0 + 10);
                foreach (var (a, b, c) in new[] { (v0, v1, v3), (v0, v3, v2) })
                {
                    var t = new NavmeshTriangle { Vertices = new P3Int16(a, b, c), EdgeLink_0_1 = -1, EdgeLink_1_2 = -1, EdgeLink_2_0 = -1 };
                    float cx = (d.Vertices[a].X + d.Vertices[b].X + d.Vertices[c].X) / 3, cy = (d.Vertices[a].Y + d.Vertices[b].Y + d.Vertices[c].Y) / 3;
                    if (deleted is not null && deleted(cx, cy)) t.Flags |= NavmeshTriangle.Flag.Deleted;
                    d.Triangles.Add(t);
                }
            }
        return new NavigationMesh(key, Rel) { Data = d };
    }

    static PlacedObject Ref(FormKey key, FormKey baseKey, float x, float y, float z, P3Float? box = null)
    {
        var r = new PlacedObject(key, Rel) { Placement = new Placement { Position = new P3Float(x, y, z), Rotation = new P3Float(0, 0, 0) } };
        r.Base.SetTo(baseKey);
        if (box is { } bx) r.Primitive = new PlacedPrimitive { Type = PlacedPrimitive.TypeEnum.Box, Bounds = bx };
        return r;
    }

    sealed record Expect(string Id, string What, FormKey Subject, StrandedState State, string? ReasonHas = null, string? ClassIs = null,
                         Func<ScanRow, string?>? Extra = null);

    public static int Run()
    {
        string dir = Path.Combine(Path.GetTempPath(), "strandprobe-selftest-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dir);
        try { return RunIn(dir); }
        finally { try { Directory.Delete(dir, true); } catch (IOException) { } catch (UnauthorizedAccessException) { } }
    }

    static int RunIn(string dir)
    {
        var m = new Build(M); var a = new Build(A); var ax = new Build(AX); var d = new Build(D); var b = new Build(B); var b2 = new Build(B2);
        var c = new Build(C); var p = new Build(P);
        m.Mod.Lights.Add(new Light(Light, Rel) { EditorID = "SfLight" });
        m.Mod.Statics.Add(new Static(Stat, Rel) { EditorID = "SfStatic" });
        m.Mod.Statics.Add(new Static(Missing, Rel) { EditorID = "SfMissingModel", Model = new Model { File = "sf\\missing.nif" } });
        m.Mod.IdleMarkers.Add(new IdleMarker(Idle, Rel) { EditorID = "SfIdleMarker" });
        m.Mod.Books.Add(new Book(Bk, Rel) { EditorID = "SfBook" });
        m.Mod.Activators.Add(new SkActivator(Act, Rel) { EditorID = "SfActivator" });
        m.Mod.Activators.Add(new SkActivator(Trig, Rel) { EditorID = "SfTrigger" });
        m.Mod.Statics.Add(new Static(Cm, Rel) { EditorID = "SfCollisionMarker" });
        m.Mod.Statics.Add(new Static(Rm, Rel) { EditorID = "SfRoomMarker" });
        m.Mod.Statics.Add(new Static(Unk, Rel) { EditorID = "SfUnmeasured" });
        m.Mod.Statics.Add(new Static(Marker, Rel) { EditorID = "SfEnableMarker" });
        a.Mod.Statics.Add(new Static(AThing, Rel) { EditorID = "SfAThing" });
        b.Mod.Statics.Add(new Static(BThing, Rel) { EditorID = "SfBThing" });
        var box64 = new P3Float(64, 64, 64);

        void Room(int n, float? floor = 0) { var cl = m.Cell(n); if (floor is { } z) cl.NavigationMeshes.Add(Floor(NavKey(n), CellKey(n), z)); }
        void Raise(Build pb, int n, float z, Func<float, float, bool>? del = null) => pb.Cell(n).NavigationMeshes.Add(Floor(NavKey(n), CellKey(n), z, del));
        void DeleteNav(Build pb, int n) => pb.Cell(n).NavigationMeshes.Add(new NavigationMesh(NavKey(n), Rel) { IsDeleted = true });
        FormKey Place(Build pb, int n, FormKey baseKey, float x, float y, float z, P3Float? box = null)
        { var k = pb.New(); pb.Cell(n).Temporary.Add(Ref(k, baseKey, x, y, z, box)); return k; }
        void Far(int n) => Place(c, n, Cm, 0, 450, 100, box64);
        FormKey Stranded(int n, FormKey baseKey, float z = 100, float raise = 400, bool far = true)
        { Room(n); var s = Place(a, n, baseKey, 0, 0, z); Raise(b, n, raise); if (far) Far(n); return s; }

        var ex = new List<Expect>();
        Func<ScanRow, string?> Dist(double w, double o) => r => r.Winning == w && r.Own == o ? null : $"winning {r.Winning} own {r.Own}, expected {w} / {o}";

        var s01 = Stranded(1, Light);
        ex.Add(new("KS01", "beyond reach with both, within alone: reported, the light, SfB named", s01, StrandedState.Reported, ClassIs: "decoration (light)",
                   Extra: r => r.NavPlugin != "SfB.esp" ? $"navmesh plugin {r.NavPlugin}" : Dist(300, 100)(r)));
        ex.Add(new("KS02", "within the tolerance on the winning side (160 u)", Stranded(2, Light, z: 40, raise: 200), StrandedState.NotJudged, "within 12 u"));
        ex.Add(new("KS03", "within the tolerance on its own side (140 u)", Stranded(3, Light, z: 140, raise: 440), StrandedState.NotJudged, "within 12 u"));
        ex.Add(new("KS04a", "own view exactly 138.0 u", Stranded(4, Light, z: 138, raise: 300), StrandedState.NotJudged, "within 12 u"));
        ex.Add(new("KS04b", "own view 137.9 u", Stranded(5, Light, z: 137.9f, raise: 300), StrandedState.Reported));
        ex.Add(new("KS04c", "winning exactly 162.0 u", Stranded(6, Light, z: 100, raise: 262), StrandedState.NotJudged, "within 12 u"));
        ex.Add(new("KS04d", "winning 162.1 u", Stranded(7, Light, z: 100, raise: 262.1f), StrandedState.Reported));
        var s05 = Stranded(8, Light); var cm05 = Place(c, 8, Cm, 0, 0, 100, new P3Float(100, 100, 100));
        ex.Add(new("KS05", "inside another mod's collision marker: touching, named", s05, StrandedState.NotJudged, "touches SfCollisionMarker"));
        var s06 = Stranded(9, Light); Place(c, 9, Rm, 0, 0, 100, new P3Float(100, 100, 100));
        ex.Add(new("KS06", "the same box on a room marker base: reported", s06, StrandedState.Reported));
        var s07 = Stranded(10, Light); Place(ax, 10, Cm, 0, 0, 100, new P3Float(100, 100, 100));
        ex.Add(new("KS07", "the same box from its own mod (SfA - Extra.esp): reported", s07, StrandedState.Reported));
        Room(11); var s08 = Place(a, 11, Light, 0, 0, 100); DeleteNav(b, 11); Far(11);
        ex.Add(new("KS08", "the winning navmesh deleted outright: a refused distance, not judged", s08, StrandedState.NotJudged, "could not be measured"));
        Room(12); var s10 = Place(a, 12, Light, 0, 0, 50); Raise(b, 12, 0, (x, y) => x * x + y * y < 300 * 300); Far(12);
        ex.Add(new("KS10", "SfB flags deleted the triangles near the light: reported", s10, StrandedState.Reported));
        Room(13); var s10g = Place(a, 13, Light, 0, 0, 50); Raise(b, 13, 0); Far(13);
        ex.Add(new("KS10g", "guard: the same triangles not flagged: no row (within reach)", s10g, StrandedState.NoRow, "within reach"));
        Room(14); var s11 = Place(a, 14, Trig, 0, 0, 32, box64); Raise(b, 14, 184); Far(14);
        ex.Add(new("KS11", "a trigger 120 u out of the walkable band, in contact alone: reported by the band", s11, StrandedState.Reported, ClassIs: "scripted or quest object (demands remediation)"));
        Room(15); var s12 = Place(a, 15, Trig, 0, 0, 216, box64); Raise(b, 15, 184); Far(15);
        ex.Add(new("KS12", "a trigger touching the raised floor: no row", s12, StrandedState.NoRow, "within reach"));
        Room(16); var s13 = Place(a, 16, Trig, 0, 0, 32, box64); DeleteNav(b, 16); Far(16);
        ex.Add(new("KS13", "a trigger whose band is refused: not judged", s13, StrandedState.NotJudged, "could not be measured"));
        // KS14: outdoors, in SfA's own worldspace
        var wk = K(A, 0xF00); var ek = K(A, 0xF01); var s14 = K(A, 0xF02);
        var ws = new Worldspace(wk, Rel) { EditorID = "SfWorld" };
        var ext = new SkCell(ek, Rel) { EditorID = "SfOutside", Grid = new CellGrid { Point = new P2Int(0, 0) } };
        ext.Temporary.Add(Ref(s14, Light, 100, 100, 100));
        var blk = new WorldspaceBlock { BlockNumberX = 0, BlockNumberY = 0, GroupType = GroupTypeEnum.ExteriorCellBlock };
        var sbk = new WorldspaceSubBlock { BlockNumberX = 0, BlockNumberY = 0, GroupType = GroupTypeEnum.ExteriorCellSubBlock };
        sbk.Items.Add(ext); blk.Items.Add(sbk); ws.SubCells.Add(blk); a.Mod.Worldspaces.Add(ws);
        ex.Add(new("KS14", "outdoors: no row, counted", s14, StrandedState.NoRow, "outdoors"));
        Room(17); var mk = Place(a, 17, Marker, 0, 0, 500); var s15 = Place(a, 17, Light, 0, 0, 100); Raise(b, 17, 400); Far(17);
        foreach (var r in a.Cell(17).Temporary.OfType<PlacedObject>())
        {
            if (r.FormKey == mk) r.SkyrimMajorRecordFlags |= SkyrimMajorRecord.SkyrimMajorRecordFlag.InitiallyDisabled;
            if (r.FormKey == s15) { r.EnableParent = new EnableParent(); r.EnableParent.Reference.SetTo(mk); }
        }
        ex.Add(new("KS15", "disabled at load through its enable parent: no row", s15, StrandedState.NoRow, "not there when the area loads"));
        ex.Add(new("KS16", "out of reach in its own view too (400 u): no row", Stranded(18, Light, z: 400, raise: 800), StrandedState.NoRow, "own plugin's navmesh too"));
        Room(19); var s17 = Place(a, 19, Light, 0, 0, 50); Far(19);
        ex.Add(new("KS17", "no plugin changes the navmesh: no row (the same navmesh)", s17, StrandedState.NoRow, "made with"));
        ex.Add(new("KS18", "no other mod's object in the room: not judged", Stranded(20, Light, far: false), StrandedState.NotJudged, "no other mod"));
        ex.Add(new("KS19a", "class: an activator", Stranded(21, Act), StrandedState.Reported, ClassIs: "scripted or quest object (demands remediation)"));
        ex.Add(new("KS19b", "class: an idle marker", Stranded(22, Idle), StrandedState.Reported, ClassIs: "AI marker"));
        ex.Add(new("KS19c", "class: a static", Stranded(23, Stat), StrandedState.Reported, ClassIs: "decoration"));
        ex.Add(new("KS19d", "class: a book", Stranded(24, Bk), StrandedState.Reported, ClassIs: "scripted or quest object (demands remediation)"));
        ex.Add(new("KS19e", "class: a light", Stranded(25, Light), StrandedState.Reported, ClassIs: "decoration (light)"));
        Room(26); var s20 = Place(a, 26, Light, 0, 0, 100); Raise(d, 26, 250); Raise(b, 26, 400); Far(26);
        ex.Add(new("KS20", "a third plugin (SfD) also moves the navmesh: reported, SfB named, own view the master's", s20, StrandedState.Reported,
                   Extra: r => r.NavPlugin != "SfB.esp" ? $"navmesh plugin {r.NavPlugin}" : Dist(300, 100)(r)));
        Room(27, floor: null); var s21 = Place(a, 27, Light, 0, 0, 100); b.Cell(27).NavigationMeshes.Add(Floor(K(B, 0xC27), CellKey(27), 400)); Far(27);
        ex.Add(new("KS21", "no navmesh in its own view, beyond reach with SfB's: a refused own distance, not judged", s21, StrandedState.NotJudged, "could not be measured"));
        Room(28); var s22 = Place(a, 28, Trig, 0, 0, 165, box64); Raise(b, 28, 600); Far(28);
        ex.Add(new("KS22", "a trigger 5 u out of contact in its own view: no row", s22, StrandedState.NoRow, "own plugin's navmesh too"));
        var s23 = Stranded(29, Light); Place(c, 29, Unk, 0, 200, 100, box64);
        ex.Add(new("KS23", "a nearer primitive with no measurement: not judged, named", s23, StrandedState.NotJudged, "SfUnmeasured"));
        var s24 = Stranded(30, Light, z: 140, raise: 440); Place(c, 30, Cm, 0, 0, 140, new P3Float(100, 100, 100));
        ex.Add(new("KS24", "touching and near the reach together: touching wins", s24, StrandedState.NotJudged, "touches"));
        Room(31); var s25 = Place(a, 31, Light, 0, 0, 100); Raise(b2, 31, 400); Place(b2, 31, AThing, 0, -450, 100); Far(31);
        ex.Add(new("KS25", "the navmesh's plugin has the object's plugin among its masters: no row, counted", s25, StrandedState.NoRow, "among its masters"));
        var s26 = Stranded(32, Light);
        p.Cell(32).Temporary.Add(Ref(s26, Light, 0, 0, 100)); Place(p, 32, BThing, 0, -450, 100);
        ex.Add(new("KS26", "a patch with both as masters overrides it in place: no row", s26, StrandedState.NoRow, "made with"));
        var s27 = Stranded(33, Light);
        p.Cell(33).Temporary.Add(Ref(s27, Light, 0, 0, 360));
        ex.Add(new("KS27", "the same patch moves it within reach: no row", s27, StrandedState.NoRow));
        ex.Add(new("KS28", "a static whose model cannot be read: not judged, its shape", Stranded(34, Missing), StrandedState.NotJudged, "shape could not be read"));

        // write the order
        var written = new Dictionary<ModKey, string>();
        string Save(Build pb, params ModKey[] masters)
        {
            pb.File();
            var path = Path.Combine(dir, pb.Mod.ModKey.FileName.String);
            var ovs = masters.Select(mk2 => SkyrimMod.CreateFromBinaryOverlay(written[mk2], Rel)).ToList();
            try { pb.Mod.BeginWrite.ToPath(path).WithLoadOrder(ovs.Cast<ISkyrimModGetter>().ToArray()).Write(); }
            finally { foreach (var o in ovs) o.Dispose(); }
            return written[pb.Mod.ModKey] = path;
        }
        Save(m); Save(a, M); Save(ax, M, A); Save(d, M); Save(b, M); Save(b2, M, A); Save(c, M); Save(p, M, A, B);
        var full = new[] { M, A, AX, D, B, B2, C, P }.Select(k => written[k]).ToList();
        var table = new List<PrimitiveMeasurement>
        {
            new("base", Cm.ToString(), "none", "solid", new[] { "player", "items" }, "self-test fixture"),
            new("base", Rm.ToString(), "none", "not_solid", Array.Empty<string>(), "self-test fixture"),
        };
        ScanResult Scan(IReadOnlyList<string> paths) =>
            StrandScan.Run(new ScanInput(paths, StrandFamily.ByName, _ => null, table, Mirror: true, Cells: null, KeepAll: true));

        int pass = 0, fail = 0;
        void Line(string id, string what, string? problem)
        {
            if (problem is null) pass++; else fail++;
            Console.WriteLine($"   {(problem is null ? "PASS" : "FAIL")}  {id,-6} {what}{(problem is null ? "" : "  -- " + problem)}");
        }
        var res = Scan(full);
        var rows = res.Rows.ToDictionary(r => r.Ref);
        foreach (var e in ex)
        {
            string got = res.All.TryGetValue(e.Subject, out var g) ? $"{g.State}: {g.Reason}" : "not seen";
            string? problem = null;
            if (!res.All.TryGetValue(e.Subject, out var st)) problem = "the subject was not seen by the scan";
            else if (st.State != e.State) problem = $"got {got}";
            else if (e.ReasonHas is { } rh && !st.Reason.Contains(rh, StringComparison.Ordinal)) problem = $"got {got}, the reason does not say '{rh}'";
            else if (rows.TryGetValue(e.Subject, out var row))
            {
                if (e.ClassIs is { } cl && row.Verdict.Class != cl) problem = $"class '{row.Verdict.Class}', expected '{cl}'";
                else if (e.Extra?.Invoke(row) is { } x) problem = x;
            }
            Line(e.Id, e.What, problem);
        }
        // KS09: the scan's two setups. Without SfA the light is absent; re-measured there with the full order's emit as --base,
        // its row reads Gone (not stranded without the ticked mod), so the differential credits it new with the ticked mod.
        var fullFile = StrandEmit.File(res, null, null, "self-test", null, true);
        var without = Scan(new[] { M, D, B, C }.Select(k => written[k]).ToList());
        var pred09 = StrandEmit.Predicates(fullFile, without).FirstOrDefault(x => x.FormKey == s01.ToString());
        Line("KS09", "absent from the setup without SfA: reported with it, its row Gone without it",
             !rows.TryGetValue(s01, out var r09) || r09.Verdict.State != StrandedState.Reported ? "not reported in the full order"
             : pred09 is null ? "no predicate for its row" : pred09.Physical != PhysicalState.Gone ? $"predicate {pred09.Physical}" : null);
        // KS27b: the patch's order re-measures the order without it: the row it fixes reads Gone
        var noPatch = Scan(new[] { M, A, AX, D, B, B2, C }.Select(k => written[k]).ToList());
        var pred27 = StrandEmit.Predicates(StrandEmit.File(noPatch, null, null, "self-test", null, true), res).FirstOrDefault(x => x.FormKey == s27.ToString());
        Line("KS27b", "without the patch it is reported; with the patch, --base reads its row Gone",
             noPatch.Rows.FirstOrDefault(x => x.Ref == s27)?.Verdict.State != StrandedState.Reported ? "not reported without the patch"
             : pred27 is null ? "no predicate for its row" : pred27.Physical != PhysicalState.Gone ? $"predicate {pred27.Physical}" : null);
        Console.WriteLine($"   identity cut: {res.Population} interior objects there at load, {res.LeftByCut} left to measure");
        Console.WriteLine($"strandprobe --self-test: {pass} passed, {fail} failed");
        return fail == 0 ? 0 : 1;
    }
}
