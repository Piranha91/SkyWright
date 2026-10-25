using System.Numerics;
using Mutagen.Bethesda;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Skyrim;
using Noggog;
using NiflySharp.Blocks;
using SkCell = Mutagen.Bethesda.Skyrim.Cell;
using SkActivator = Mutagen.Bethesda.Skyrim.Activator;

namespace HarnessShared;

// THE MODELLESS ACTIVATOR, DOOR AND MARKER FIXTURE (`stateprobe --write-modelless-activator-fixture DIR`, read by
// tests/test_modelless_activators_doors.py, which runs the real stateprobe and contactprobe on it). Test-only, written with
// Mutagen, the boxes with NifBoxFixture. The objects, each with the same 20 u object bounds unless stated:
//   act-box      activator, no model; its reference carries a Box primitive with no collision layer stated
//   act-nc       the same base; its reference's Box primitive states L_NONCOLLIDABLE (index 15 here, as in the base game)
//   act-bare     the same base; its reference carries no primitive
//   act-model    activator with model ma\lever.nif, a closed collision cube of half extent 10 u (control: has a body)
//   door-bare    door, no model, no object bounds, no primitive (the shape of every modelless door in the two census orders)
//   markers      a static with the Is-Marker flag and a model (an XMarker), and one with the flag and no model
//
// Plugins (load order): MaRoom.esm (bases, collision layers, cells, the walls and slabs); MaObjects.esp [MaRoom] (the objects
// and the switches of the state cells); MaMover.esp [MaRoom] (moves the contact cells' walls away); MaMoreMarkers.esp
// [MaRoom, MaObjects] (three markers that arrive with the switch of cell SG, loaded only by the test's second candidate).
//
//   cell   MaRoom.esm                  MaObjects.esp                                       MaMover.esp
//   CA..CD wall (0,0,0), half 70 u     act-box / act-nc / act-bare / act-model / door-bare  the wall to (1000,0,0)
//   CE                                 at (80,0,0): against the wall's +X face
//   SA..SE slab (0,0,0), half 70 u     a switch (enabled) at (0,0,400); the same five objects at (0,0,0), each the opposite
//                                      of its switch (it arrives when the switch flips), inside the slab
//   SK     slab, the same              a switch; the two markers at (0,0,0), arriving with it
//   SP     slab, the same              a switch; a marker-flagged static with no model whose reference carries a Box primitive
//                                      (the CollisionMarker shape: a box that may block), arriving with it
//   SG     slab, the same              a switch; a static whose model file is missing and has no bounds (no geometry, not a
//                                      marker: what the check cannot judge), arriving with it
public static class ModellessActivatorFixture
{
    public const SkyrimRelease Rel = SkyrimRelease.SkyrimSE;
    public static readonly ModKey RoomKey = new("MaRoom", ModType.Master), ObjectsKey = new("MaObjects", ModType.Plugin),
        MoverKey = new("MaMover", ModType.Plugin), MoreKey = new("MaMoreMarkers", ModType.Plugin);
    static FormKey K(uint id) => new(RoomKey, id);
    static FormKey KO(uint id) => new(ObjectsKey, id);
    static FormKey KM(uint id) => new(MoreKey, id);
    public static readonly FormKey Wall = K(0x801), Switch = K(0x802), ActBox = K(0x803), ActModel = K(0x804), DoorBare = K(0x805),
        XMarker = K(0x806), BareMarker = K(0x807), NoGeom = K(0x808), CollisionBox = K(0x80C), LayerNc = K(0x809), LayerTrigger = K(0x80A), LayerStatic = K(0x80B);
    public static readonly string[] Objects = { "A", "N", "B", "M", "D" };   // act-box, act-nc, act-bare, act-model, door-bare
    public const string WallMesh = "ma\\wall.nif", LeverMesh = "ma\\lever.nif", XMarkerMesh = "ma\\xmarker.nif", MissingMesh = "ma\\missing.nif";
    public static readonly string[] Order = { "MaRoom.esm", "MaObjects.esp", "MaMover.esp" };
    public const uint NoncollidableIndex = 15;
    const int IsMarkerFlag = 0x00800000;

    public sealed record Written(IReadOnlyList<string> Order, string ModsDir);

    // Form ids: contact cells 0x811+i, state cells 0x821+i (i = 0..4 for A N B M D), SK 0x826, SG 0x827; walls 0x901+i, slabs
    // 0x911+i, SK slab 0x916, SG slab 0x917. Objects in MaObjects: contact refs 0x901+i, state switches 0x911+i and refs 0x921+i,
    // SK switch 0x916, markers 0x926 (XMarker) and 0x927 (bare marker), SG switch 0x917, no-geometry static 0x928; SP (cell 0x828, slab 0x918) switch 0x918, collision box 0x929.
    public static FormKey ContactCell(int i) => K(0x811u + (uint)i);
    public static FormKey StateCell(int i) => K(0x821u + (uint)i);
    public static FormKey ContactRef(int i) => KO(0x901u + (uint)i);
    public static FormKey StateSwitch(int i) => KO(0x911u + (uint)i);
    public static FormKey StateRef(int i) => KO(0x921u + (uint)i);
    public static readonly FormKey SwK = KO(0x916), SwG = KO(0x917), XMarkerRef = KO(0x926), BareMarkerRef = KO(0x927), NoGeomRef = KO(0x928),
        SwP = KO(0x918), CollisionBoxRef = KO(0x929);

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

    /// <summary>The object of kind <paramref name="kind"/> (A N B M D) as a reference: its base, and its primitive and layer.</summary>
    static PlacedObject Obj(string kind, FormKey key, float x, FormKey? parent)
    {
        var r = Ref(key, kind switch { "M" => ActModel, "D" => DoorBare, _ => ActBox }, x, 0, 0, null, parent);
        if (kind is "A" or "N")
            r.Primitive = new PlacedPrimitive { Type = PlacedPrimitive.TypeEnum.Box, Bounds = new P3Float(20, 20, 20),
                                                Color = System.Drawing.Color.FromArgb(255, 0, 255, 0), Unknown = 0.15f };
        if (kind == "N") r.CollisionLayer = NoncollidableIndex;
        return r;
    }

    /// <summary>A collision cube of half extent <paramref name="halfUnits"/> Skyrim units.</summary>
    static byte[] Cube(float halfUnits)
    {
        var f = NifBoxFixture.NewFile();
        NifBoxFixture.Attach(f, new bhkBoxShape { Dimensions = new Vector3(halfUnits / HousecarlCore.HavokCollision.SkyrimUnitsPerHavokUnit) });
        return NifBoxFixture.Save(f);
    }

    static ObjectBounds Box10() => new() { First = new P3Int16(-10, -10, -10), Second = new P3Int16(10, 10, 10) };

    public static Written Write(string dir)
    {
        var d = Path.Combine(dir, "ma"); Directory.CreateDirectory(d);
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
        room.Statics.Add(new Static(Wall, Rel) { EditorID = "MaWall", Model = new Model { File = WallMesh },
                                                 ObjectBounds = new ObjectBounds { First = new P3Int16(-70, -70, -70), Second = new P3Int16(70, 70, 70) } });
        room.Statics.Add(new Static(Switch, Rel) { EditorID = "MaSwitch" });
        room.Activators.Add(new SkActivator(ActBox, Rel) { EditorID = "MaActivatorBox", ObjectBounds = Box10() });
        room.Activators.Add(new SkActivator(ActModel, Rel) { EditorID = "MaActivatorLever", Model = new Model { File = LeverMesh }, ObjectBounds = Box10() });
        room.Doors.Add(new Door(DoorBare, Rel) { EditorID = "MaDoorPlaceholder" });
        room.Statics.Add(new Static(XMarker, Rel) { EditorID = "MaXMarker", Model = new Model { File = XMarkerMesh }, ObjectBounds = Box10(),
                                                    MajorRecordFlagsRaw = IsMarkerFlag });
        room.Statics.Add(new Static(BareMarker, Rel) { EditorID = "MaBareMarker", MajorRecordFlagsRaw = IsMarkerFlag });
        room.Statics.Add(new Static(CollisionBox, Rel) { EditorID = "MaCollisionMarker", MajorRecordFlagsRaw = IsMarkerFlag });
        room.Statics.Add(new Static(NoGeom, Rel) { EditorID = "MaMissingModel", Model = new Model { File = MissingMesh } });
        room.CollisionLayers.Add(new CollisionLayer(LayerNc, Rel) { EditorID = "L_NONCOLLIDABLE", Name = "L_NONCOLLIDABLE", Index = NoncollidableIndex, Description = "collides with nothing" });
        room.CollisionLayers.Add(new CollisionLayer(LayerTrigger, Rel) { EditorID = "L_TRIGGER", Name = "L_TRIGGER", Index = 12, Flags = CollisionLayer.Flag.TriggerVolume, Description = "a trigger" });
        room.CollisionLayers.Add(new CollisionLayer(LayerStatic, Rel) { EditorID = "L_STATIC", Name = "L_STATIC", Index = 1, Description = "static" });
        for (int i = 0; i < Objects.Length; i++)
        {
            Room(room, ContactCell(i), $"MaRoomC{Objects[i]}", Ref(K(0x901u + (uint)i), Wall, 0, 0, 0));
            Room(room, StateCell(i), $"MaRoomS{Objects[i]}", Ref(K(0x911u + (uint)i), Wall, 0, 0, 0));
        }
        Room(room, K(0x826), "MaRoomSK", Ref(K(0x916), Wall, 0, 0, 0));
        Room(room, K(0x827), "MaRoomSG", Ref(K(0x917), Wall, 0, 0, 0));
        Room(room, K(0x828), "MaRoomSP", Ref(K(0x918), Wall, 0, 0, 0));
        var roomPath = Save(room);
        using var roomOv = SkyrimMod.CreateFromBinaryOverlay(roomPath, Rel);

        var objs = new SkyrimMod(ObjectsKey, Rel);
        for (int i = 0; i < Objects.Length; i++)
        {
            Room(objs, ContactCell(i), $"MaRoomC{Objects[i]}", Obj(Objects[i], ContactRef(i), 80, null));
            Room(objs, StateCell(i), $"MaRoomS{Objects[i]}", Ref(StateSwitch(i), Switch, 0, 0, 400, $"MaSw{Objects[i]}"),
                 Obj(Objects[i], StateRef(i), 0, StateSwitch(i)));
        }
        Room(objs, K(0x826), "MaRoomSK", Ref(SwK, Switch, 0, 0, 400, "MaSwK"), Ref(XMarkerRef, XMarker, 0, 0, 0, null, SwK),
             Ref(BareMarkerRef, BareMarker, 0, 0, 20, null, SwK));
        Room(objs, K(0x827), "MaRoomSG", Ref(SwG, Switch, 0, 0, 400, "MaSwG"), Ref(NoGeomRef, NoGeom, 0, 0, 0, null, SwG));
        var cbox = Ref(CollisionBoxRef, CollisionBox, 0, 0, 0, null, SwP);
        cbox.Primitive = new PlacedPrimitive { Type = PlacedPrimitive.TypeEnum.Box, Bounds = new P3Float(40, 40, 40),
                                               Color = System.Drawing.Color.FromArgb(255, 0, 255, 0), Unknown = 0.15f };
        Room(objs, K(0x828), "MaRoomSP", Ref(SwP, Switch, 0, 0, 400, "MaSwP"), cbox);
        Save(objs, roomOv);

        var mover = new SkyrimMod(MoverKey, Rel);
        for (int i = 0; i < Objects.Length; i++)
            Room(mover, ContactCell(i), $"MaRoomC{Objects[i]}", Ref(K(0x901u + (uint)i), Wall, 1000, 0, 0));
        Save(mover, roomOv);

        // MaMoreMarkers.esp [MaRoom, MaObjects]: three more markers that arrive with MaSwG, inside its slab. Loaded only by the
        // test's second candidate, so a setup that adds editor markers can be compared with one that does not: what the state
        // check could not judge must not move.
        using var objsOv = SkyrimMod.CreateFromBinaryOverlay(Path.Combine(d, ObjectsKey.FileName.String), Rel);
        var more = new SkyrimMod(MoreKey, Rel);
        Room(more, K(0x827), "MaRoomSG", Ref(KM(0x901), XMarker, 0, 0, 30, null, SwG), Ref(KM(0x902), XMarker, 0, 0, -30, null, SwG),
             Ref(KM(0x903), BareMarker, 30, 0, 0, null, SwG));
        Save(more, roomOv, objsOv);

        var mods = Path.Combine(d, "mods");
        var meshes = Path.Combine(mods, "MaRoom", "meshes");
        Directory.CreateDirectory(Path.Combine(meshes, "ma"));
        File.WriteAllBytes(Path.Combine(meshes, WallMesh), Cube(70f));
        File.WriteAllBytes(Path.Combine(meshes, LeverMesh), Cube(10f));
        return new Written(paths, mods);
    }
}
