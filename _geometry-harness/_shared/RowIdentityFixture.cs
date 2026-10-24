using System.Numerics;
using Mutagen.Bethesda;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Skyrim;
using NiflySharp.Blocks;
using SkCell = Mutagen.Bethesda.Skyrim.Cell;

namespace HarnessShared;

// THE ROW IDENTITY FIXTURE (`stateprobe --write-row-identity-fixture DIR`, read by tests/test_state_row_identity.py, which runs
// the real stateprobe on it). Test-only, written with Mutagen, the boxes with NifBoxFixture. Every box is a closed collision
// cube, unrotated; a box's depth inside another is the distance from its deepest vertex to the other's nearest face, so the
// two directions of one pair differ whenever a vertex sits nearer one of the other's faces. Expectations are worked out by
// hand from this table (u = Skyrim units; slab half 70, block half 49, cup half 28, floor half 140).
//
// Plugins (load order): RiBase.esm (bases, cells, the references that do not change); RiA.esp [RiBase]; RiB.esp [RiBase, RiA].
// Each switch is enabled at load and its children carry the opposite flag, so a child ARRIVES when its switch flips.
//
//   cell  state                 arriving (plugin; centre)            already there (RiBase)          rows the passes write today
//   R1    RiSwR1                M1 slab (RiA; 0,0,0)                 O1 cup (60,60,60)               host: O1 38 u inside M1; dependent: M1 18 u inside O1
//   C1    RiSwC1                M2 slab (RiA; 0,0,0)                 O2 cup (60,60,60);              host: O2 38 u inside M2; dependent: M2 30 u inside F2
//                                                                     F2 floor (0,0,-180)              (the slab's deepest neighbour is the floor, not the cup)
//   R2    RiSwR2h,RiSwR2d       H2 slab (RiA, RiSwR2h; 0,0,0);       -                               host: D2 38 u inside H2; dependent: D2 38 u inside H2
//                               D2 cup (RiB, RiSwR2d; 0,0,60)
//   R2S   RiSwR2s               H2s slab (RiA) and D2s cup (RiB),    -                               the same two rows, same set (withheld from the emit)
//                               both children of RiSwR2s
//   R3    RiSwR3a,RiSwR3b       A3 slab (RiA, RiSwR3a; 0,0,0);       -                               host A3: B3 59 u inside; host B3: A3 39 u inside;
//                               B3 block (RiB, RiSwR3b; 60,60,60)                                     dependent A3: 39 u inside B3; dependent B3: 59 u inside A3
//   -     RiSwR1,RiSwX          M1 as in R1; RiSwX has one child X   -                               R1's two rows again, in a second state
//         (RiSwX in RiB)        cup (RiB; cell X, far from all)
public static class RowIdentityFixture
{
    public const SkyrimRelease Rel = SkyrimRelease.SkyrimSE;
    public static readonly ModKey BaseKey = new("RiBase", ModType.Master), AKey = new("RiA", ModType.Plugin), BKey = new("RiB", ModType.Plugin);
    static FormKey K(uint id) => new(BaseKey, id);
    static FormKey KA(uint id) => new(AKey, id);
    static FormKey KB(uint id) => new(BKey, id);
    public static readonly FormKey Slab = K(0x801), Cup = K(0x802), Block = K(0x803), Floor = K(0x804), Switch = K(0x805);
    public static readonly FormKey R1 = K(0x811), C1 = K(0x812), R2 = K(0x813), R2S = K(0x814), R3 = K(0x815), X = K(0x816);
    public static readonly FormKey O1 = K(0x901), O2 = K(0x902), F2 = K(0x903);
    public static readonly FormKey SwR1 = KA(0x901), M1 = KA(0x902), SwC1 = KA(0x903), M2 = KA(0x904), SwR2h = KA(0x905), H2 = KA(0x906),
        SwR2s = KA(0x907), H2s = KA(0x908), SwR3a = KA(0x909), A3 = KA(0x90A);
    public static readonly FormKey SwR2d = KB(0x901), D2 = KB(0x902), D2s = KB(0x903), SwR3b = KB(0x904), B3 = KB(0x905), SwX = KB(0x906), XCup = KB(0x907);
    public const string SlabMesh = "ri\\slab.nif", CupMesh = "ri\\cup.nif", BlockMesh = "ri\\block.nif", FloorMesh = "ri\\floor.nif";
    public static readonly string[] Order = { "RiBase.esm", "RiA.esp", "RiB.esp" };

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
        NifBoxFixture.Attach(f, new bhkBoxShape { Dimensions = new Vector3(halfUnits / HousecarlCore.HavokCollision.SkyrimUnitsPerHavokUnit) });
        return NifBoxFixture.Save(f);
    }

    public static Written Write(string dir)
    {
        var d = Path.Combine(dir, "ri"); Directory.CreateDirectory(d);
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

        var b = new SkyrimMod(BaseKey, Rel);
        b.Statics.Add(new Static(Slab, Rel) { EditorID = "RiSlab", Model = new Model { File = SlabMesh } });
        b.Statics.Add(new Static(Cup, Rel) { EditorID = "RiCup", Model = new Model { File = CupMesh } });
        b.Statics.Add(new Static(Block, Rel) { EditorID = "RiBlock", Model = new Model { File = BlockMesh } });
        b.Statics.Add(new Static(Floor, Rel) { EditorID = "RiFloor", Model = new Model { File = FloorMesh } });
        b.Statics.Add(new Static(Switch, Rel) { EditorID = "RiSwitch" });
        Room(b, R1, "RiRoomR1", Ref(O1, Cup, 60, 60, 60));
        Room(b, C1, "RiRoomC1", Ref(O2, Cup, 60, 60, 60), Ref(F2, Floor, 0, 0, -180));
        Room(b, R2, "RiRoomR2");
        Room(b, R2S, "RiRoomR2S");
        Room(b, R3, "RiRoomR3");
        Room(b, X, "RiRoomX");
        var bPath = Save(b);
        using var bOv = SkyrimMod.CreateFromBinaryOverlay(bPath, Rel);

        var a = new SkyrimMod(AKey, Rel);
        Room(a, R1, "RiRoomR1", Ref(SwR1, Switch, 0, 0, 400, "RiSwR1"), Ref(M1, Slab, 0, 0, 0, null, SwR1));
        Room(a, C1, "RiRoomC1", Ref(SwC1, Switch, 0, 0, 400, "RiSwC1"), Ref(M2, Slab, 0, 0, 0, null, SwC1));
        Room(a, R2, "RiRoomR2", Ref(SwR2h, Switch, 0, 0, 400, "RiSwR2h"), Ref(H2, Slab, 0, 0, 0, null, SwR2h));
        Room(a, R2S, "RiRoomR2S", Ref(SwR2s, Switch, 0, 0, 400, "RiSwR2s"), Ref(H2s, Slab, 0, 0, 0, null, SwR2s));
        Room(a, R3, "RiRoomR3", Ref(SwR3a, Switch, 0, 0, 400, "RiSwR3a"), Ref(A3, Slab, 0, 0, 0, null, SwR3a));
        var aPath = Save(a, bOv);
        using var aOv = SkyrimMod.CreateFromBinaryOverlay(aPath, Rel);

        var m = new SkyrimMod(BKey, Rel);
        Room(m, R2, "RiRoomR2", Ref(SwR2d, Switch, 0, 0, 400, "RiSwR2d"), Ref(D2, Cup, 0, 0, 60, null, SwR2d));
        Room(m, R2S, "RiRoomR2S", Ref(D2s, Cup, 0, 0, 60, null, SwR2s));
        Room(m, R3, "RiRoomR3", Ref(SwR3b, Switch, 0, 0, 400, "RiSwR3b"), Ref(B3, Block, 60, 60, 60, null, SwR3b));
        Room(m, X, "RiRoomX", Ref(SwX, Switch, 0, 0, 400, "RiSwX"), Ref(XCup, Cup, 0, 0, 0, null, SwX));
        Save(m, bOv, aOv);

        var mods = Path.Combine(d, "mods");
        var meshes = Path.Combine(mods, "RiBase", "meshes");
        Directory.CreateDirectory(Path.Combine(meshes, "ri"));
        File.WriteAllBytes(Path.Combine(meshes, SlabMesh), Cube(70f));
        File.WriteAllBytes(Path.Combine(meshes, CupMesh), Cube(28f));
        File.WriteAllBytes(Path.Combine(meshes, BlockMesh), Cube(49f));
        File.WriteAllBytes(Path.Combine(meshes, FloorMesh), Cube(140f));
        return new Written(paths, mods);
    }
}
