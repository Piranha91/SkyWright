using System.Numerics;
using Mutagen.Bethesda;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Skyrim;
using Noggog;
using NiflySharp.Blocks;
using SkCell = Mutagen.Bethesda.Skyrim.Cell;

namespace HarnessShared;

// THE MARKER-BOX AND PLACED-ACTOR FIXTURE (`stateprobe --write-state-lane-fixture DIR`, read by
// tests/test_state_marker_box_and_actors.py, which runs the real stateprobe and conflictdiff on it, route on). Test-only,
// written with Mutagen, the meshes with NifBoxFixture, the navmeshes with BlockFixture.Grid. Every cell is interior and
// holds one switch MxSw<X> (a static with no model, enabled, at (0,0,400)); its one member arrives when the switch flips,
// except AD's, which leaves.
//
// Plugins: MxRoom.esm (bases, races, keyword, collision layers, cells, navmeshes, floors); MxObjects.esp [MxRoom] (switches
// and members).
//
//   cell  navmesh                                 member (arrives with MxSw<cell>)
//   RW    corridor: 20 x 3 squares of 64 u,       a barrier static: a box collision mesh, half extents 20 x 150 x 60 u, at
//         x -640..640, y -96..96, z 0            (0,0,60): across the corridor (control: an object the route rule reads today)
//   RB    the same corridor                       a CollisionMarker-shaped marker (Is-Marker static, no model) whose reference
//                                                 carries a Box primitive 40 x 300 x 120 u, no layer, at (0,0,60)
//   RN    the same corridor                       the same marker box, stating L_NONCOLLIDABLE (measured not solid)
//   RS    the same corridor                       the same marker base with a Sphere primitive of radius 150 (not measured)
//   AC    20 x 20 squares of 64 u, -640..640;     a humanoid (race MxHumanRace, keyword ActorTypeNPC) at (0,0,0): clear
//         a floor slab (box 700 x 700 x 10 u
//         half extents) at (0,0,-10), top z 0
//   AO    the same                                the humanoid at (0,940,0): 300 u beyond the navmesh's edge
//   AF    the same                                the humanoid at (0,0,200): 200 u above the floor
//   AB    the same                                the humanoid at (0,0,-200): 200 u below the floor, its body under the slab
//   AS    the same, and the barrier at (0,0,60)   the humanoid at (0,0,0), its body through the barrier
//   AD    the same                                the humanoid at (0,0,0), enabled at baseline: it LEAVES with the switch
//   AK    the same                                a creature (race MxGoatRace, no ActorTypeNPC keyword) at (0,0,0)
//   AL    the same                                a placed leveled actor (base a LeveledNpc) at (0,0,0)
//
// And one exterior worldspace, MxOutside, of two cells side by side: EA at grid (0,0), EB at grid (1,0). EA's navmesh is
// 10 x 10 squares of 64 u at x 3456..4096, y 0..640; EB's the same at x 4096..4736. A floor slab (the AC slab) at
// (4096,320,-10) in EA. Both switches and members are in EA:
//   EN    the humanoid at (4200,320,0): in EA, 104 u inside EB's navmesh (the cell's own navmesh ends at x 4096)
//   EO    the humanoid at (4200,1000,0): in EA, beyond both navmeshes (they end at y 640)
public static class StateLaneFixture
{
    public const SkyrimRelease Rel = SkyrimRelease.SkyrimSE;
    public static readonly ModKey RoomKey = new("MxRoom", ModType.Master), ObjectsKey = new("MxObjects", ModType.Plugin);
    static FormKey K(uint id) => new(RoomKey, id);
    static FormKey KO(uint id) => new(ObjectsKey, id);
    public static readonly FormKey Switch = K(0x801), Barrier = K(0x802), Floor = K(0x803), CollisionBox = K(0x804),
        HumanRace = K(0x805), GoatRace = K(0x806), ActorTypeNpc = K(0x807), Human = K(0x808), Goat = K(0x809), Leveled = K(0x80A),
        LayerNc = K(0x80B);
    public const string BarrierMesh = "mx\\barrier.nif", FloorMesh = "mx\\floor.nif";
    public static readonly string[] Order = { "MxRoom.esm", "MxObjects.esp" };
    public const uint NoncollidableIndex = 15;
    const int IsMarkerFlag = 0x00800000;

    public static readonly string[] Cells = { "RW", "RB", "RN", "RS", "AC", "AO", "AF", "AS", "AD", "AK", "AL", "AB" };
    public static FormKey CellKey(int j) => K(0x820u + (uint)j);
    public static FormKey NavKey(int j) => K(0x840u + (uint)j);
    public static FormKey FloorRef(int j) => K(0x860u + (uint)j);
    public static FormKey BarrierRef(int j) => K(0x880u + (uint)j);
    public static FormKey SwitchRef(int j) => KO(0x900u + (uint)j);
    public static FormKey MemberRef(int j) => KO(0x920u + (uint)j);

    public static readonly string[] ExteriorCases = { "EN", "EO" };
    public static readonly FormKey Outside = K(0x8A0), CellEA = K(0x8A1), CellEB = K(0x8A2), NavEA = K(0x8A3), NavEB = K(0x8A4),
        FloorEA = K(0x8A5);
    public static FormKey ExteriorSwitch(int k) => KO(0x940u + (uint)k);
    public static FormKey ExteriorMember(int k) => KO(0x960u + (uint)k);

    static SkCell Exterior(FormKey key, string edid, int gx) =>
        new(key, Rel) { EditorID = edid, Flags = default, Grid = new CellGrid { Point = new P2Int(gx, 0) } };

    static NavigationMesh ExteriorNav(FormKey key, FormKey cell, int gx, float fromX)
    {
        var nav = BlockFixture.Grid(key, cell, 10, 10, 64f, fromX, 0f);
        nav.Data!.Parent = new WorldspaceNavmeshParent { Parent = new FormLink<IWorldspaceGetter>(Outside), Coordinates = new P2Int16((short)gx, 0) };
        return nav;
    }

    public sealed record Written(IReadOnlyList<string> Order, string ModsDir);

    static Placement At(float x, float y, float z) => new() { Position = new(x, y, z), Rotation = new(0, 0, 0) };

    static EnableParent Parent(FormKey p, bool opposite)
    {
        var e = new EnableParent { Flags = opposite ? EnableParent.Flag.SetEnableStateToOppositeOfParent : 0 };
        e.Reference.SetTo(p);
        return e;
    }

    static PlacedObject Ref(FormKey key, FormKey baseKey, float x, float y, float z, string? edid = null)
    {
        var r = new PlacedObject(key, Rel) { EditorID = edid, Placement = At(x, y, z) };
        r.Base.SetTo(baseKey);
        return r;
    }

    static IPlaced Member(int j, FormKey sw)
    {
        string c = Cells[j];
        var key = MemberRef(j);
        if (c[0] == 'A')
        {
            var (x, y, z) = c switch { "AO" => (0f, 940f, 0f), "AF" => (0f, 0f, 200f), "AB" => (0f, 0f, -200f), _ => (0f, 0f, 0f) };
            var n = new PlacedNpc(key, Rel) { Placement = At(x, y, z), EnableParent = Parent(sw, opposite: c != "AD") };
            n.Base.SetTo(c switch { "AK" => Goat, "AL" => Leveled, _ => Human });
            return n;
        }
        var r = Ref(key, c == "RW" ? Barrier : CollisionBox, 0, 0, 60);
        r.EnableParent = Parent(sw, opposite: true);
        if (c != "RW")
            r.Primitive = new PlacedPrimitive { Type = c == "RS" ? PlacedPrimitive.TypeEnum.Sphere : PlacedPrimitive.TypeEnum.Box,
                                                Bounds = c == "RS" ? new P3Float(300, 300, 300) : new P3Float(40, 300, 120),
                                                Color = System.Drawing.Color.FromArgb(255, 0, 255, 0), Unknown = 0.15f };
        if (c == "RN") r.CollisionLayer = NoncollidableIndex;
        return r;
    }

    /// <summary>A box collision mesh of the given half extents in Skyrim units.</summary>
    static byte[] BoxMesh(float hx, float hy, float hz)
    {
        float u = SkyWright.Engine.HavokCollision.SkyrimUnitsPerHavokUnit;
        var f = NifBoxFixture.NewFile();
        NifBoxFixture.Attach(f, new bhkBoxShape { Dimensions = new Vector3(hx / u, hy / u, hz / u) });
        return NifBoxFixture.Save(f);
    }

    public static Written Write(string dir)
    {
        var d = Path.Combine(dir, "mx"); Directory.CreateDirectory(d);
        var paths = new List<string>();
        string Save(SkyrimMod mod, params ISkyrimModGetter[] masters)
        {
            var path = Path.Combine(d, mod.ModKey.FileName.String);
            mod.BeginWrite.ToPath(path).WithLoadOrder(masters).Write();
            paths.Add(path);
            return path;
        }

        var room = new SkyrimMod(RoomKey, Rel);
        room.Statics.Add(new Static(Switch, Rel) { EditorID = "MxSwitch" });
        room.Statics.Add(new Static(Barrier, Rel) { EditorID = "MxBarrier", Model = new Model { File = BarrierMesh },
                                                    ObjectBounds = new ObjectBounds { First = new P3Int16(-20, -150, -60), Second = new P3Int16(20, 150, 60) } });
        room.Statics.Add(new Static(Floor, Rel) { EditorID = "MxFloor", Model = new Model { File = FloorMesh },
                                                  ObjectBounds = new ObjectBounds { First = new P3Int16(-700, -700, -10), Second = new P3Int16(700, 700, 10) } });
        room.Statics.Add(new Static(CollisionBox, Rel) { EditorID = "MxCollisionMarker", MajorRecordFlagsRaw = IsMarkerFlag });
        room.Keywords.Add(new Keyword(ActorTypeNpc, Rel) { EditorID = "ActorTypeNPC" });
        var human = new Race(HumanRace, Rel) { EditorID = "MxHumanRace", Flags = Race.Flag.Playable,
                                                Keywords = new ExtendedList<IFormLinkGetter<IKeywordGetter>> { new FormLink<IKeywordGetter>(ActorTypeNpc) } };
        human.Height.Male = 1f; human.Height.Female = 1f;
        room.Races.Add(human);
        var goat = new Race(GoatRace, Rel) { EditorID = "MxGoatRace" };
        goat.Height.Male = 1f; goat.Height.Female = 1f;
        room.Races.Add(goat);
        room.Npcs.Add(new Npc(Human, Rel) { EditorID = "MxHuman", Height = 1f, Race = new FormLinkNullable<IRaceGetter>(HumanRace) });
        room.Npcs.Add(new Npc(Goat, Rel) { EditorID = "MxGoat", Height = 1f, Race = new FormLinkNullable<IRaceGetter>(GoatRace) });
        room.LeveledNpcs.Add(new LeveledNpc(Leveled, Rel) { EditorID = "MxLeveled" });
        room.CollisionLayers.Add(new CollisionLayer(LayerNc, Rel) { EditorID = "L_NONCOLLIDABLE", Name = "L_NONCOLLIDABLE", Index = NoncollidableIndex, Description = "collides with nothing" });
        for (int j = 0; j < Cells.Length; j++)
        {
            string c = Cells[j];
            var cell = new SkCell(CellKey(j), Rel) { EditorID = $"MxRoom{c}", Flags = SkCell.Flag.IsInteriorCell };
            if (c[0] == 'R') cell.NavigationMeshes.Add(BlockFixture.Grid(NavKey(j), CellKey(j), 20, 3, 64f, -640f, -96f));
            else
            {
                cell.NavigationMeshes.Add(BlockFixture.Grid(NavKey(j), CellKey(j), 20, 20, 64f, -640f, -640f));
                cell.Temporary.Add(Ref(FloorRef(j), Floor, 0, 0, -10));
                if (c == "AS") cell.Temporary.Add(Ref(BarrierRef(j), Barrier, 0, 0, 60));
            }
            MovedRefFixture.FileInterior(room, cell);
        }
        var ea = Exterior(CellEA, "MxOutsideEA", 0);
        ea.NavigationMeshes.Add(ExteriorNav(NavEA, CellEA, 0, 3456f));
        ea.Temporary.Add(Ref(FloorEA, Floor, 4096, 320, -10));
        var eb = Exterior(CellEB, "MxOutsideEB", 1);
        eb.NavigationMeshes.Add(ExteriorNav(NavEB, CellEB, 1, 4096f));
        var outside = MovedRefFixture.World(Outside, null, ea, eb);
        outside.EditorID = "MxOutside";
        room.Worldspaces.Add(outside);
        var roomPath = Save(room);
        using var roomOv = SkyrimMod.CreateFromBinaryOverlay(roomPath, Rel);

        var objs = new SkyrimMod(ObjectsKey, Rel);
        for (int j = 0; j < Cells.Length; j++)
        {
            var cell = new SkCell(CellKey(j), Rel) { EditorID = $"MxRoom{Cells[j]}", Flags = SkCell.Flag.IsInteriorCell };
            cell.Temporary.Add(Ref(SwitchRef(j), Switch, 0, 0, 400, $"MxSw{Cells[j]}"));
            cell.Temporary.Add(Member(j, SwitchRef(j)));
            MovedRefFixture.FileInterior(objs, cell);
        }
        var eaObjs = Exterior(CellEA, "MxOutsideEA", 0);
        for (int k = 0; k < ExteriorCases.Length; k++)
        {
            string c = ExteriorCases[k];
            eaObjs.Temporary.Add(Ref(ExteriorSwitch(k), Switch, 4096, 320, 400, $"MxSw{c}"));
            var n = new PlacedNpc(ExteriorMember(k), Rel) { Placement = At(4200, c == "EN" ? 320 : 1000, 0), EnableParent = Parent(ExteriorSwitch(k), opposite: true) };
            n.Base.SetTo(Human);
            eaObjs.Temporary.Add(n);
        }
        var outsideObjs = MovedRefFixture.World(Outside, null, eaObjs);
        outsideObjs.EditorID = "MxOutside";
        objs.Worldspaces.Add(outsideObjs);
        Save(objs, roomOv);

        var mods = Path.Combine(d, "mods");
        var meshes = Path.Combine(mods, "MxRoom", "meshes", "mx");
        Directory.CreateDirectory(meshes);
        File.WriteAllBytes(Path.Combine(mods, "MxRoom", "meshes", BarrierMesh), BoxMesh(20, 150, 60));
        File.WriteAllBytes(Path.Combine(mods, "MxRoom", "meshes", FloorMesh), BoxMesh(700, 700, 10));
        return new Written(paths, mods);
    }
}
