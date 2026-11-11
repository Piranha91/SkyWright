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
        float u = HousecarlCore.HavokCollision.SkyrimUnitsPerHavokUnit;
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
        Save(objs, roomOv);

        var mods = Path.Combine(d, "mods");
        var meshes = Path.Combine(mods, "MxRoom", "meshes", "mx");
        Directory.CreateDirectory(meshes);
        File.WriteAllBytes(Path.Combine(mods, "MxRoom", "meshes", BarrierMesh), BoxMesh(20, 150, 60));
        File.WriteAllBytes(Path.Combine(mods, "MxRoom", "meshes", FloorMesh), BoxMesh(700, 700, 10));
        return new Written(paths, mods);
    }
}
