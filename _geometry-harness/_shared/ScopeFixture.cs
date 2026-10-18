using Mutagen.Bethesda;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Skyrim;
using Noggog;
using SkCell = Mutagen.Bethesda.Skyrim.Cell;

namespace HarnessShared;

// THE SCOPE FIXTURE (window 2, task 1; `stateprobe --write-scope-fixture DIR`, read by tests/test_population_scope.py, which runs
// the real contactprobe --empty-space and stateprobe on it whole-order and with --population). Test-only, written with Mutagen,
// the meshes with NifBoxFixture. Expectations are worked out by hand from this table.
//
// Plugins (load order): ScBase.esm; ScA.esp, ScReloc.esp, ScMover.esp, ScDelta.esp, each mastering ScBase.esm (ScReloc also ScA.esp). ScRace.esp
// (one RACE record) is written beside them and is in no order: a population naming it must be refused by stateprobe.
// The arm WITH the delta loads every plugin of the order; the arm WITHOUT drops ScDelta.esp. The population names ScDelta.esp.
// Bases (ScBase): ScSlab (sc\slab.nif, a collision box of about +-70 u), ScCup (sc\cup.nif, about +-28 u), ScSwitch (no model).
// Cells: interiors IPop, IOut, IFar; worldspace ScWorld with exteriors E0 (0,0), E4 (4,0), E7 (7,0), Em5 (-5,0), Em9 (-9,0),
// Em22 (-22,0), Em25 (-25,0).
//
// Empty space (contactprobe --empty-space; ScMover moves each slab 2000 u along x inside its cell):
//  F1  cup f1 (ScA) on slab s1 (ScBase) in IPop; ScDelta files d0 in IPop        LEFT IN EMPTY SPACE    in: IPop is a delta cell
//  F2  cup f2 (ScA) on slab s2 in IOut                                         LEFT IN EMPTY SPACE    out
//  F3  cup f3 (ScA) on slab s3 in IFar; ref r (ScBase, IPop) is relocated to   LEFT IN EMPTY SPACE    in: the closure pulls IFar
//      IFar by ScReloc
//  F4  cup f4 (ScA, filed in E7) relocated by ScReloc into IOut, on s2 beside  LEFT IN EMPTY SPACE    out; judged in the scoped run
//      f2 (the two cups touch: one assembly)                                                          (ever exterior), not reported
// State (stateprobe --all; each switch m (ScA, enabled) has one child cup (ScA, opposite flag: it ARRIVES on the flip) set 60 u
// into a slab t (ScBase): contact Sunk, a StateConditional row):
//  T1  IPop                                                                   kept: contact (an interior population cell)
//  T2  IOut                                                                   dropped
//  T3  E4: 4 cells from the delta's de (E0)                                   kept: route-ring (outside de's 3x3)
//  T4  E7: 7 cells from de                                                    dropped
//  T5  Em9: 4 cells from Em5, where ScDelta carries only a terrain record       kept: route-ring (delta geometry cell)
//  T6  Em25: 3 cells from l (ScBase, Em22), which ScDelta's worldspace record  kept: large-ref
//      lists as a large reference
public static class ScopeFixture
{
    public const SkyrimRelease Rel = SkyrimRelease.SkyrimSE;
    public static readonly ModKey BaseKey = new("ScBase", ModType.Master), AKey = new("ScA", ModType.Plugin), RelocKey = new("ScReloc", ModType.Plugin),
        MoverKey = new("ScMover", ModType.Plugin), DeltaKey = new("ScDelta", ModType.Plugin), RaceKey = new("ScRace", ModType.Plugin);
    static FormKey K(uint id) => new(BaseKey, id);
    static FormKey KA(uint id) => new(AKey, id);
    static FormKey KD(uint id) => new(DeltaKey, id);
    public static readonly FormKey Slab = K(0x801), Cup = K(0x802), Switch = K(0x803);
    public static readonly FormKey IPop = K(0x810), IOut = K(0x811), IFar = K(0x812), World = K(0x830),
        E0 = K(0x840), E4 = K(0x841), E7 = K(0x842), Em5 = K(0x843), Em9 = K(0x844), Em22 = K(0x845), Em25 = K(0x846);
    // ScBase references
    public static readonly FormKey S1 = K(0x901), S2 = K(0x902), S3 = K(0x903), R = K(0x904), L = K(0x905),
        T1 = K(0x911), T2 = K(0x912), T3 = K(0x913), T4 = K(0x914), T5 = K(0x915), T6 = K(0x916);
    // ScA references
    public static readonly FormKey F1 = KA(0x901), F2 = KA(0x902), F3 = KA(0x903), F4 = KA(0x904),
        M1 = KA(0x911), M2 = KA(0x912), M3 = KA(0x913), M4 = KA(0x914), M5 = KA(0x915), M6 = KA(0x916),
        C1 = KA(0x921), C2 = KA(0x922), C3 = KA(0x923), C4 = KA(0x924), C5 = KA(0x925), C6 = KA(0x926);
    // ScDelta references
    public static readonly FormKey D0 = KD(0x901), De = KD(0x902), DLand = KD(0x903);
    public static readonly string[] Order = { "ScBase.esm", "ScA.esp", "ScReloc.esp", "ScMover.esp", "ScDelta.esp" };
    const float C = 4096f;

    public sealed record Written(IReadOnlyList<string> Order, string ModsDir, string RacePlugin);

    static PlacedObject Ref(FormKey key, FormKey baseKey, float x, float y, float z, string? edid = null, FormKey? parent = null, bool opposite = false)
    {
        var r = new PlacedObject(key, Rel) { EditorID = edid, Placement = new Placement { Position = new(x, y, z), Rotation = new(0, 0, 0) } };
        r.Base.SetTo(baseKey);
        if (parent is { } pk)
        {
            r.EnableParent = new EnableParent { Flags = opposite ? EnableParent.Flag.SetEnableStateToOppositeOfParent : 0 };
            r.EnableParent.Reference.SetTo(pk);
        }
        return r;
    }
    static SkCell Int(FormKey key, string edid) => new(key, Rel) { EditorID = edid, Flags = SkCell.Flag.IsInteriorCell };
    static SkCell Ext(FormKey key, int x, int y) => new(key, Rel) { Flags = default, Grid = new CellGrid { Point = new P2Int(x, y) } };
    static Worldspace Wrld(IEnumerable<FormKey>? listed, params SkCell[] exteriors)
    {
        var ws = new Worldspace(World, Rel) { EditorID = "ScWorld" };
        if (listed is not null)
        {
            var grid = new WorldspaceGridReference { GridPosition = new P2Int16(-22, 0) };
            foreach (var k in listed) grid.References.Add(new WorldspaceReference { Position = new P2Int16(-22, 0), Reference = new FormLink<IPlacedObjectGetter>(k) });
            if (grid.References.Count > 0) ws.LargeReferences.Add(grid);
        }
        if (exteriors.Length > 0)
        {
            var blk = new WorldspaceBlock { BlockNumberX = 0, BlockNumberY = 0, GroupType = GroupTypeEnum.ExteriorCellBlock };
            var sub = new WorldspaceSubBlock { BlockNumberX = 0, BlockNumberY = 0, GroupType = GroupTypeEnum.ExteriorCellSubBlock };
            foreach (var c in exteriors) sub.Items.Add(c);
            blk.Items.Add(sub); ws.SubCells.Add(blk);
        }
        return ws;
    }

    /// <summary>Write the plugins into <paramref name="dir"/>\sc and the meshes under dir\sc\mods (one folder per plugin stem).</summary>
    public static Written Write(string dir)
    {
        var d = Path.Combine(dir, "sc"); Directory.CreateDirectory(d);
        var paths = new List<string>();
        string Save(SkyrimMod mod, params ISkyrimModGetter[] masters)
        {
            var path = Path.Combine(d, mod.ModKey.FileName.String);
            mod.BeginWrite.ToPath(path).WithLoadOrder(masters).Write();
            paths.Add(path);
            return path;
        }
        SkCell Room(SkyrimMod m, FormKey ck, string edid, params PlacedObject[] refs)
        {
            var c = Int(ck, edid);
            foreach (var r in refs) c.Temporary.Add(r);
            MovedRefFixture.FileInterior(m, c);
            return c;
        }
        // One switch and its arriving cup set 60 u into a ScBase slab (the slab is ScBase's, so the row is not same-author).
        PlacedObject[] Sw(FormKey m, FormKey c, float x, float y) =>
            new[] { Ref(m, Switch, x, y, 300, "Sc" + m.ID.ToString("X")), Ref(c, Cup, x, y, 82, null, m, true) };

        // ---- ScBase.esm ----
        var b = new SkyrimMod(BaseKey, Rel);
        b.ModHeader.Flags |= SkyrimModHeader.HeaderFlag.Master;
        b.Statics.Add(new Static(Slab, Rel) { EditorID = "ScSlab", Model = new Model { File = "sc\\slab.nif" } });
        b.Statics.Add(new Static(Cup, Rel) { EditorID = "ScCup", Model = new Model { File = "sc\\cup.nif" } });
        b.Statics.Add(new Static(Switch, Rel) { EditorID = "ScSwitch" });
        Room(b, IPop, "ScRoomPop", Ref(S1, Slab, 0, 0, 0), Ref(R, Slab, -3000, 0, 0), Ref(T1, Slab, 6000, 0, 0));
        Room(b, IOut, "ScRoomOut", Ref(S2, Slab, 0, 0, 0), Ref(T2, Slab, 6000, 0, 0));
        Room(b, IFar, "ScRoomFar", Ref(S3, Slab, 0, 0, 0));
        {
            var e4 = Ext(E4, 4, 0); e4.Temporary.Add(Ref(T3, Slab, 4 * C + 500, 500, 0));
            var e7 = Ext(E7, 7, 0); e7.Temporary.Add(Ref(T4, Slab, 7 * C + 500, 500, 0));
            var em9 = Ext(Em9, -9, 0); em9.Temporary.Add(Ref(T5, Slab, -9 * C + 500, 500, 0));
            var em22 = Ext(Em22, -22, 0); em22.Temporary.Add(Ref(L, Slab, -22 * C + 500, 500, 0));
            var em25 = Ext(Em25, -25, 0); em25.Temporary.Add(Ref(T6, Slab, -25 * C + 500, 500, 0));
            b.Worldspaces.Add(Wrld(null, Ext(E0, 0, 0), e4, e7, Ext(Em5, -5, 0), em9, em22, em25));
        }
        var bPath = Save(b);
        using var bOv = SkyrimMod.CreateFromBinaryOverlay(bPath, Rel);

        // ---- ScA.esp: the empty-space cups and the switches with their arriving cups ----
        var a = new SkyrimMod(AKey, Rel);
        Room(a, IPop, "ScRoomPop", new[] { Ref(F1, Cup, 0, 0, 98) }.Concat(Sw(M1, C1, 6000, 0)).ToArray());
        Room(a, IOut, "ScRoomOut", new[] { Ref(F2, Cup, 0, 0, 98) }.Concat(Sw(M2, C2, 6000, 0)).ToArray());
        Room(a, IFar, "ScRoomFar", Ref(F3, Cup, 0, 0, 98));
        {
            var e4 = Ext(E4, 4, 0); foreach (var r in Sw(M3, C3, 4 * C + 500, 500)) e4.Temporary.Add(r);
            var e7 = Ext(E7, 7, 0); foreach (var r in Sw(M4, C4, 7 * C + 500, 500)) e7.Temporary.Add(r);
            e7.Temporary.Add(Ref(F4, Cup, 7 * C + 2000, 2000, 0));
            var em9 = Ext(Em9, -9, 0); foreach (var r in Sw(M5, C5, -9 * C + 500, 500)) em9.Temporary.Add(r);
            var em25 = Ext(Em25, -25, 0); foreach (var r in Sw(M6, C6, -25 * C + 500, 500)) em25.Temporary.Add(r);
            a.Worldspaces.Add(Wrld(null, e4, e7, em9, em25));
        }
        var aPath = Save(a, bOv);
        using var aOv = SkyrimMod.CreateFromBinaryOverlay(aPath, Rel);

        // ---- ScReloc.esp: r from IPop into IFar; f4 from E7 into IOut beside f2 ----
        var rl = new SkyrimMod(RelocKey, Rel);
        Room(rl, IFar, "ScRoomFar", Ref(R, Slab, -3000, 0, 0));
        Room(rl, IOut, "ScRoomOut", Ref(F4, Cup, 60, 0, 98));
        Save(rl, bOv, aOv);

        // ---- ScMover.esp: moves s1, s2, s3 2000 u along x ----
        var mv = new SkyrimMod(MoverKey, Rel);
        Room(mv, IPop, "ScRoomPop", Ref(S1, Slab, 2000, 0, 0));
        Room(mv, IOut, "ScRoomOut", Ref(S2, Slab, 2000, 0, 0));
        Room(mv, IFar, "ScRoomFar", Ref(S3, Slab, 2000, 0, 0));
        Save(mv, bOv);

        // ---- ScDelta.esp: d0 in IPop; de in E0; a terrain record (no heights) for Em5; l listed as a large reference ----
        var dl = new SkyrimMod(DeltaKey, Rel);
        Room(dl, IPop, "ScRoomPop", Ref(D0, Slab, 3000, 3000, 0));
        {
            var e0 = Ext(E0, 0, 0); e0.Temporary.Add(Ref(De, Slab, 500, 500, 0));
            var em5 = Ext(Em5, -5, 0); em5.Landscape = new Landscape(DLand, Rel);
            dl.Worldspaces.Add(Wrld(new[] { L }, e0, em5));
        }
        Save(dl, bOv);

        // ---- ScRace.esp (in no order) ----
        var rc = new SkyrimMod(RaceKey, Rel);
        rc.Races.Add(new Race(new FormKey(RaceKey, 0x801), Rel) { EditorID = "ScRace" });
        var racePath = Path.Combine(d, rc.ModKey.FileName.String);
        rc.BeginWrite.ToPath(racePath).WithNoLoadOrder().Write();

        var mods = Path.Combine(d, "mods");
        foreach (var n in Order) Directory.CreateDirectory(Path.Combine(mods, Path.GetFileNameWithoutExtension(n)));
        var bm = Path.Combine(mods, "ScBase", "meshes", "sc"); Directory.CreateDirectory(bm);
        File.WriteAllBytes(Path.Combine(bm, "slab.nif"), StateFixture.BoxNif(1f));
        File.WriteAllBytes(Path.Combine(bm, "cup.nif"), StateFixture.BoxNif(0.4f));
        return new Written(paths, mods, racePath);
    }
}
