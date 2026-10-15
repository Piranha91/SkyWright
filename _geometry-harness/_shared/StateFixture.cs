using System.Numerics;
using Mutagen.Bethesda;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Skyrim;
using NiflySharp.Blocks;
using SkCell = Mutagen.Bethesda.Skyrim.Cell;

namespace HarnessShared;

// THE STATE CLASS FIXTURE (stateprobe --self-test, probe-decisions slice 4): seven switches whose flips bring collision
// boxes into, or take them out of, two rooms: one without a navmesh (contact rows) and two corridors (route rows). Test-only;
// written with Mutagen, the boxes with NifBoxFixture, the corridors with BlockFixture.Grid. Expectations are stated by each
// test, worked out by hand from this table.
//
// Load order (masters): 0 Skyrim.esm; 1 Dawnguard.esm [0]; 2 StMod.esp [0]. Skyrim.esm: STAT StSlab 0x801 (st\slab.nif,
// one rigid body, bhkBoxShape 1 x 1 x 1 Havok units, about +-70 u), STAT StCup 0x802 (st\cup.nif, a 0.4 box, about +-28 u:
// under the 64 u host minimum), STAT StSwitchBase 0x803 (no model), DOOR StDoor 0x804 and MSTT StPush 0x805 (slab mesh).
// Cells: SA 0x810 (no navmesh); SB 0x811, a corridor navmesh 2 x 30 squares of 64 u (x -64..64, y -960..960) whose triangles
// north of y 640 are Deleted-flagged; SB2 0x812, the same corridor unflagged. Every switch is enabled at load; "opp" = the
// child's XESP has the opposite flag, so it is off at load and ARRIVES when its switch flips.
//
//   switch (owner, cell)   child (placer; position)                                   row when flipped alone
//   StM1 (StMod, SA)       C1 cup opp (StMod; 0,0,88) on slab S1 (Skyrim; 0,0,0)       contact Sunk, ModSetOverVanilla: class
//   StM2 (Dawnguard, SA)   C2 cup opp (Dawnguard; 1000,0,88) on S2 (Skyrim)            contact Sunk, BethesdaFloor: class, not emitted
//   StM3 (StMod, SA)       C3a slab opp (StMod; 2000,0,0); C3b cup placed by Skyrim    contact rows, same set, not same author
//                          (2000,0,88), attached opp by StMod at the same position
//   StM4 (StMod, SA)       C4 cup opp (StMod; 3000,0,88) on S4 (StMod; 3000,0,0)       contact Sunk, same author
//   StM5 (StMod, SB)       W1 slab opp (0,0,0); Dr door opp (0,-560,0); Ms MSTT opp    W1 route; Dr, Ms set aside; O3 over Deleted
//                          (0,-300,0); O3 slab opp (0,800,0); Hv slab opp (0,240,400)  triangles and Hv above the band never enter
//   StM6 (StMod, SB2)      W2 slab, same state (0,0,0): it LEAVES                      route, StateResolved
//   StM7 (Dawnguard, SB2)  W3 slab opp (Dawnguard; 0,400,0)                            route by a Bethesda plugin
public static class StateFixture
{
    public const SkyrimRelease Rel = SkyrimRelease.SkyrimSE;
    public static readonly ModKey Skyrim = new("Skyrim", ModType.Master), Dawnguard = new("Dawnguard", ModType.Master),
        StMod = new("StMod", ModType.Plugin);
    static FormKey Sk(uint id) => new(Skyrim, id);
    static FormKey Dg(uint id) => new(Dawnguard, id);
    static FormKey Md(uint id) => new(StMod, id);
    public static readonly FormKey Slab = Sk(0x801), Cup = Sk(0x802), SwitchBase = Sk(0x803), DoorBase = Sk(0x804), PushBase = Sk(0x805),
        SA = Sk(0x810), SB = Sk(0x811), SB2 = Sk(0x812), NavB = Sk(0x820), NavB2 = Sk(0x821);
    public static readonly FormKey S1 = Sk(0x901), S2 = Sk(0x902), C3b = Sk(0x903);
    public static readonly FormKey M2 = Dg(0x901), C2 = Dg(0x902), M7 = Dg(0x903), W3 = Dg(0x904);
    public static readonly FormKey M1 = Md(0x901), C1 = Md(0x902), M3 = Md(0x903), C3a = Md(0x904), M4 = Md(0x905), S4 = Md(0x906),
        C4 = Md(0x907), M5 = Md(0x908), W1 = Md(0x909), Dr = Md(0x90A), Ms = Md(0x90B), O3 = Md(0x90C), Hv = Md(0x90D),
        M6 = Md(0x90E), W2 = Md(0x90F);
    public const string SlabMesh = "st\\slab.nif", CupMesh = "st\\cup.nif";
    public static readonly string[] Order = { "Skyrim.esm", "Dawnguard.esm", "StMod.esp" };
    /// <summary>The corridor's Deleted-flag rule in SB: a triangle whose centroid lies north of y 640.</summary>
    public static bool DeletedInSB(float cx, float cy) => cy > 640f;

    /// <summary>The plugins in load order, and a mods folder holding both meshes loose under Skyrim's mod folder.</summary>
    public sealed record Written(IReadOnlyList<string> Order, string ModsDir);

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
    static SkCell Cell(FormKey key, string edid) => new(key, Rel) { EditorID = edid, Flags = SkCell.Flag.IsInteriorCell };

    /// <summary>A box mesh: one rigid body with a bhkBoxShape of the given half extent in Havok units.</summary>
    public static byte[] BoxNif(float half)
    {
        var f = NifBoxFixture.NewFile();
        NifBoxFixture.Attach(f, new bhkBoxShape { Dimensions = new Vector3(half) });
        return NifBoxFixture.Save(f);
    }

    /// <summary>Write the three plugins into <paramref name="dir"/>\st and the meshes under dir\st\mods.</summary>
    public static Written Write(string dir)
    {
        var d = Path.Combine(dir, "st"); Directory.CreateDirectory(d);
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
            var c = Cell(ck, edid);
            foreach (var r in refs) c.Temporary.Add(r);
            MovedRefFixture.FileInterior(m, c);
            return c;
        }

        var sk = new SkyrimMod(Skyrim, Rel);
        sk.Statics.Add(new Static(Slab, Rel) { EditorID = "StSlab", Model = new Model { File = SlabMesh } });
        sk.Statics.Add(new Static(Cup, Rel) { EditorID = "StCup", Model = new Model { File = CupMesh } });
        sk.Statics.Add(new Static(SwitchBase, Rel) { EditorID = "StSwitchBase" });
        sk.Doors.Add(new Door(DoorBase, Rel) { EditorID = "StDoor", Model = new Model { File = SlabMesh } });
        sk.MoveableStatics.Add(new MoveableStatic(PushBase, Rel) { EditorID = "StPush", Model = new Model { File = SlabMesh } });
        Room(sk, SA, "StRoomA", Ref(S1, Slab, 0, 0, 0), Ref(S2, Slab, 1000, 0, 0), Ref(C3b, Cup, 2000, 0, 88));
        Room(sk, SB, "StCorridor").NavigationMeshes.Add(BlockFixture.Grid(NavB, SB, 2, 30, 64f, -64f, -960f, DeletedInSB));
        Room(sk, SB2, "StCorridorTwo").NavigationMeshes.Add(BlockFixture.Grid(NavB2, SB2, 2, 30, 64f, -64f, -960f));
        var skPath = Save(sk);
        using var skOv = SkyrimMod.CreateFromBinaryOverlay(skPath, Rel);

        var dg = new SkyrimMod(Dawnguard, Rel);
        Room(dg, SA, "StRoomA", Ref(M2, SwitchBase, 1000, 0, 300, "StM2"), Ref(C2, Cup, 1000, 0, 88, null, M2, true));
        Room(dg, SB2, "StCorridorTwo", Ref(M7, SwitchBase, 0, 0, 300, "StM7"), Ref(W3, Slab, 0, 400, 0, null, M7, true));
        Save(dg, skOv);

        var md = new SkyrimMod(StMod, Rel);
        Room(md, SA, "StRoomA",
            Ref(M1, SwitchBase, 0, 0, 300, "StM1"), Ref(C1, Cup, 0, 0, 88, null, M1, true),
            Ref(M3, SwitchBase, 2000, 0, 300, "StM3"), Ref(C3a, Slab, 2000, 0, 0, null, M3, true), Ref(C3b, Cup, 2000, 0, 88, null, M3, true),
            Ref(M4, SwitchBase, 3000, 0, 300, "StM4"), Ref(S4, Slab, 3000, 0, 0), Ref(C4, Cup, 3000, 0, 88, null, M4, true));
        Room(md, SB, "StCorridor",
            Ref(M5, SwitchBase, 0, 0, 300, "StM5"), Ref(W1, Slab, 0, 0, 0, null, M5, true), Ref(Dr, DoorBase, 0, -560, 0, null, M5, true),
            Ref(Ms, PushBase, 0, -300, 0, null, M5, true), Ref(O3, Slab, 0, 800, 0, null, M5, true), Ref(Hv, Slab, 0, 240, 400, null, M5, true));
        Room(md, SB2, "StCorridorTwo", Ref(M6, SwitchBase, 0, 0, 300, "StM6"), Ref(W2, Slab, 0, 0, 0, null, M6, false));
        Save(md, skOv);

        var mods = Path.Combine(d, "mods");
        var meshes = Path.Combine(mods, Skyrim.Name, "meshes");
        Directory.CreateDirectory(Path.Combine(meshes, "st"));
        File.WriteAllBytes(Path.Combine(meshes, SlabMesh), BoxNif(1f));
        File.WriteAllBytes(Path.Combine(meshes, CupMesh), BoxNif(0.4f));
        return new Written(paths, mods);
    }
}
