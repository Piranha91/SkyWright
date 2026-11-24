using System.Numerics;
using Mutagen.Bethesda;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Skyrim;
using Noggog;
using NiflySharp.Blocks;
using SkCell = Mutagen.Bethesda.Skyrim.Cell;

namespace HarnessShared;

// THE MODELLESS LIGHT FIXTURE (`stateprobe --write-modelless-light-fixture DIR`, read by tests/test_modelless_light.py, which
// runs the real stateprobe and contactprobe on it). Test-only, written with Mutagen, the boxes with NifBoxFixture. A light
// whose base record has no model has no body in game: nothing is drawn and nothing collides. Three light bases, each with the
// same object bounds (a 20 u box centred on the reference):
//   bare     no model                              (the case: no row in any probe)
//   lantern  model ml\lantern.nif, a closed collision cube of half extent 10 u (control: a light with a body keeps its row)
//   missing  model ml\missing.nif, a file no mod supplies (control: a model that could not be read is not dropped)
//
// Plugins (load order): MlRoom.esm (bases, cells, the walls); MlLights.esp [MlRoom] (the lights, and the switches of the
// state cells); MlMover.esp [MlRoom] (moves the three contact cells' walls away).
//
//   cell  MlRoom.esm                    MlLights.esp                                   MlMover.esp          rows on the base build
//   CB    wall WB (0,0,0), half 70 u    bare light LB at (80,0,0): against the         WB to (1000,0,0)     CONTACT Orphaned for LB;
//   CL    wall WL, the same             wall's +X face (box 70..90 in x)               WL the same          EMPTY-SPACE (--empty-space)
//   CM    wall WM, the same             lantern LL / missing LM, the same              WM the same          LeftInEmptySpace for each
//   SB    slab SB (0,0,0), half 70 u    switch SwB (enabled) at (0,0,400); bare light  -                    STATE-CONTACT Sunk under
//   SL    slab SL, the same             XB at (0,0,0), its child set opposite (it      -                    MlSwB / MlSwL / MlSwM: the
//   SM    slab SM, the same             arrives when the switch flips), inside the     -                    light 60 u inside the slab
//                                       slab; lantern XL / missing XM the same
public static class ModellessLightFixture
{
    public const SkyrimRelease Rel = SkyrimRelease.SkyrimSE;
    public static readonly ModKey RoomKey = new("MlRoom", ModType.Master), LightsKey = new("MlLights", ModType.Plugin), MoverKey = new("MlMover", ModType.Plugin);
    static FormKey K(uint id) => new(RoomKey, id);
    static FormKey KL(uint id) => new(LightsKey, id);
    public static readonly FormKey Wall = K(0x801), Switch = K(0x802), Bare = K(0x803), Lantern = K(0x804), Missing = K(0x805);
    public static readonly FormKey CB = K(0x811), CL = K(0x812), CM = K(0x813), SB = K(0x814), SL = K(0x815), SM = K(0x816);
    public static readonly FormKey WB = K(0x901), WL = K(0x902), WM = K(0x903), SlabB = K(0x904), SlabL = K(0x905), SlabM = K(0x906);
    public static readonly FormKey LB = KL(0x901), LL = KL(0x902), LM = KL(0x903),
        SwB = KL(0x904), XB = KL(0x905), SwL = KL(0x906), XL = KL(0x907), SwM = KL(0x908), XM = KL(0x909);
    public const string WallMesh = "ml\\wall.nif", LanternMesh = "ml\\lantern.nif", MissingMesh = "ml\\missing.nif";
    public static readonly string[] Order = { "MlRoom.esm", "MlLights.esp", "MlMover.esp" };
    public static readonly ModKey MoreKey = new("MlMoreLights", ModType.Plugin);
    static FormKey KM(uint id) => new(MoreKey, id);

    public sealed record Written(IReadOnlyList<string> Order, string ModsDir);

    static PlacedObject Ref(FormKey key, FormKey baseKey, float x, float y, float z, string? edid = null, FormKey? parent = null)
    {
        var r = new PlacedObject(key, Rel) { EditorID = edid, Placement = new Placement { Position = new(x, y, z), Rotation = new(0, 0, 0) } };
        r.Base.SetTo(baseKey);
        if (parent is { } pk)
        {
            r.EnableParent = new EnableParent { Flags = EnableParent.Flag.SetEnableStateToOppositeOfParent };
            r.EnableParent.Reference.SetTo(pk);
        }
        return r;
    }

    /// <summary>A collision cube of half extent <paramref name="halfUnits"/> Skyrim units.</summary>
    static byte[] Cube(float halfUnits)
    {
        var f = NifBoxFixture.NewFile();
        NifBoxFixture.Attach(f, new bhkBoxShape { Dimensions = new Vector3(halfUnits / SkyWright.Engine.HavokCollision.SkyrimUnitsPerHavokUnit) });
        return NifBoxFixture.Save(f);
    }

    static ObjectBounds Box10() => new() { First = new P3Int16(-10, -10, -10), Second = new P3Int16(10, 10, 10) };

    public static Written Write(string dir)
    {
        var d = Path.Combine(dir, "ml"); Directory.CreateDirectory(d);
        var paths = new List<string>();
        string Save(SkyrimMod mod, params ISkyrimModGetter[] masters)
        {
            var path = Path.Combine(d, mod.ModKey.FileName.String);
            mod.BeginWrite.ToPath(path).WithLoadOrder(masters).Write();
            paths.Add(path);
            return path;
        }
        void Room(SkyrimMod m, FormKey ck, string edid, params PlacedObject[] refs)
        {
            var c = new SkCell(ck, Rel) { EditorID = edid, Flags = SkCell.Flag.IsInteriorCell };
            foreach (var r in refs) c.Temporary.Add(r);
            MovedRefFixture.FileInterior(m, c);
        }

        var room = new SkyrimMod(RoomKey, Rel);
        room.Statics.Add(new Static(Wall, Rel) { EditorID = "MlWall", Model = new Model { File = WallMesh },
                                                 ObjectBounds = new ObjectBounds { First = new P3Int16(-70, -70, -70), Second = new P3Int16(70, 70, 70) } });
        room.Statics.Add(new Static(Switch, Rel) { EditorID = "MlSwitch" });
        room.Lights.Add(new Light(Bare, Rel) { EditorID = "MlLightBare", ObjectBounds = Box10() });
        room.Lights.Add(new Light(Lantern, Rel) { EditorID = "MlLightLantern", Model = new Model { File = LanternMesh }, ObjectBounds = Box10() });
        room.Lights.Add(new Light(Missing, Rel) { EditorID = "MlLightMissing", Model = new Model { File = MissingMesh }, ObjectBounds = Box10() });
        Room(room, CB, "MlRoomCB", Ref(WB, Wall, 0, 0, 0));
        Room(room, CL, "MlRoomCL", Ref(WL, Wall, 0, 0, 0));
        Room(room, CM, "MlRoomCM", Ref(WM, Wall, 0, 0, 0));
        Room(room, SB, "MlRoomSB", Ref(SlabB, Wall, 0, 0, 0));
        Room(room, SL, "MlRoomSL", Ref(SlabL, Wall, 0, 0, 0));
        Room(room, SM, "MlRoomSM", Ref(SlabM, Wall, 0, 0, 0));
        var roomPath = Save(room);
        using var roomOv = SkyrimMod.CreateFromBinaryOverlay(roomPath, Rel);

        var lights = new SkyrimMod(LightsKey, Rel);
        Room(lights, CB, "MlRoomCB", Ref(LB, Bare, 80, 0, 0));
        Room(lights, CL, "MlRoomCL", Ref(LL, Lantern, 80, 0, 0));
        Room(lights, CM, "MlRoomCM", Ref(LM, Missing, 80, 0, 0));
        Room(lights, SB, "MlRoomSB", Ref(SwB, Switch, 0, 0, 400, "MlSwB"), Ref(XB, Bare, 0, 0, 0, null, SwB));
        Room(lights, SL, "MlRoomSL", Ref(SwL, Switch, 0, 0, 400, "MlSwL"), Ref(XL, Lantern, 0, 0, 0, null, SwL));
        Room(lights, SM, "MlRoomSM", Ref(SwM, Switch, 0, 0, 400, "MlSwM"), Ref(XM, Missing, 0, 0, 0, null, SwM));
        Save(lights, roomOv);

        var mover = new SkyrimMod(MoverKey, Rel);
        Room(mover, CB, "MlRoomCB", Ref(WB, Wall, 1000, 0, 0));
        Room(mover, CL, "MlRoomCL", Ref(WL, Wall, 1000, 0, 0));
        Room(mover, CM, "MlRoomCM", Ref(WM, Wall, 1000, 0, 0));
        Save(mover, roomOv);

        // MlMoreLights.esp [MlRoom, MlLights] (2026-10-24, Gate C review M3): three more bare lights that arrive with MlSwL, inside
        // its slab. Loaded only by the test's candidate order, so a setup that adds lights with no model can be compared with
        // one that does not: what the state check could not judge must not move.
        var lightsPath = Path.Combine(d, LightsKey.FileName.String);
        using var lightsOv = SkyrimMod.CreateFromBinaryOverlay(lightsPath, Rel);
        var more = new SkyrimMod(MoreKey, Rel);
        Room(more, SL, "MlRoomSL", Ref(KM(0x901), Bare, 0, 0, 30, null, SwL), Ref(KM(0x902), Bare, 0, 0, -30, null, SwL),
             Ref(KM(0x903), Bare, 30, 0, 0, null, SwL));
        Save(more, roomOv, lightsOv);

        var mods = Path.Combine(d, "mods");
        var meshes = Path.Combine(mods, "MlRoom", "meshes");
        Directory.CreateDirectory(Path.Combine(meshes, "ml"));
        File.WriteAllBytes(Path.Combine(meshes, WallMesh), Cube(70f));
        File.WriteAllBytes(Path.Combine(meshes, LanternMesh), Cube(10f));
        return new Written(paths, mods);
    }
}
