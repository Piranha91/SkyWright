using Mutagen.Bethesda;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Skyrim;
using SkCell = Mutagen.Bethesda.Skyrim.Cell;

namespace HarnessShared;

// THE OVERLAP CLASS FIXTURE (overlapprobe --self-test, probe-decisions slice 4): a synthetic order whose render-box
// intersections reach every branch of overlapprobe's class split, its provenance-unknown count and its physical predicate,
// and a base emit naming one row per predicate exit. Test-only; written with Mutagen, the box with BaseGameFixture.RenderBox.
//
// Load order (masters): 0 Skyrim.esm; 1 Dawnguard.esm [0]; 2 HearthFires.esm [0]; 3 OcA.esp [0]; 4 OcB.esp [0];
// 5 OcSeen.esp [0,3]; 6 OcMover.esp [0,3]; 7 OcRebase.esp [0,3]. Skyrim.esm: STAT OcBox 0x801 (oc\box.nif, a closed 100 u
// cube), STAT OcBare 0x802 (no model), STAT OcBox2 0x803 (the same model); interior cells OcCell 0x810 and OcCellB 0x811.
// Loose: mods\Skyrim\meshes\oc\box.nif; mods\OcA\OcFixture_SWAP.ini, which gives R2 two possible bases.
//
//   ref  placed by (base OcBox)      cell, position         later                                  for
//   V1   Skyrim                      OcCell (10000,0,0)     -                                      base-game responsible
//   D1   Dawnguard / E1 HearthFires  (2000,0,0)/(2050,30,20) -                                     Bethesda floor
//   A1   OcA / B1 OcB                (0,0,0)/(50,30,20)     -                                      the class row
//   A2   OcA / S1 OcSeen (XLKR A2)   (4000,0,0)/(4050,30,20) -                                     introduced + seen
//   X    OcA / Y OcB                 (6000,0,0)/(6050,30,20) OcMover moves X to (6020,0,0)         inherited
//   Z    OcA, OcBare / W OcB         (8000,0,0)/(8050,30,20) OcRebase: (8020,0,0), base OcBox      unknown counterfactual
//   Kd   OcA                         (-50,30,-20)           OcMover deletes it (placement kept)    deleted
//   K    OcA, initially disabled     (-50,-30,-20)          -                                      disabled
//   Q    OcA                         OcCellB (50,30,20)     -                                      another space
//   G    OcA, OcBare                 (16000,0,0)            -                                      no geometry
//   U    OcA, XESP -> OcA:0009FF     (18000,0,0)            -                                      unresolved enable chain
//   R2   OcA (0x909)                 (20000,0,0)            the _SWAP.ini line                     two BOS outcomes
public static class OverlapClassFixture
{
    public const SkyrimRelease Rel = SkyrimRelease.SkyrimSE;
    public static readonly ModKey Skyrim = new("Skyrim", ModType.Master), Dawnguard = new("Dawnguard", ModType.Master),
        HearthFires = new("HearthFires", ModType.Master), OcA = new("OcA", ModType.Plugin), OcB = new("OcB", ModType.Plugin),
        OcSeen = new("OcSeen", ModType.Plugin), OcMover = new("OcMover", ModType.Plugin), OcRebase = new("OcRebase", ModType.Plugin);
    static FormKey Sk(uint id) => new(Skyrim, id);
    static FormKey A(uint id) => new(OcA, id);
    public static readonly FormKey Box = Sk(0x801), Bare = Sk(0x802), Box2 = Sk(0x803), Cell1 = Sk(0x810), Cell2 = Sk(0x811);
    public static readonly FormKey V1 = Sk(0x901), D1 = new(Dawnguard, 0x901), E1 = new(HearthFires, 0x901),
        A1 = A(0x901), A2 = A(0x902), X = A(0x903), Z = A(0x904), Kd = A(0x905), K = A(0x906), Q = A(0x907), G = A(0x908),
        R2 = A(0x909), U = A(0x90A), MissingParent = A(0x9FF),
        B1 = new(OcB, 0x901), Y = new(OcB, 0x902), W = new(OcB, 0x903), S1 = new(OcSeen, 0x901);
    /// <summary>A FormKey no plugin of the order holds: the base row "not placed in this order".</summary>
    public static readonly FormKey Ghost = new(new ModKey("OcGhost", ModType.Plugin), 0xABC);
    public const string MeshRel = "oc\\box.nif";
    public const string SwapIni = "OcFixture_SWAP.ini";
    public static readonly string[] Order = { "Skyrim.esm", "Dawnguard.esm", "HearthFires.esm", "OcA.esp", "OcB.esp", "OcSeen.esp", "OcMover.esp", "OcRebase.esp" };

    /// <summary>The plugins in load order, the mods folder (loose files under each plugin's mod folder) and the base emit.</summary>
    public sealed record Written(IReadOnlyList<string> Order, string ModsDir, string BaseEmit);

    static PlacedObject Ref(FormKey key, FormKey baseKey, float x, float y, float z)
    {
        var r = new PlacedObject(key, Rel) { Placement = new Placement { Position = new(x, y, z), Rotation = new(0, 0, 0) } };
        r.Base.SetTo(baseKey);
        return r;
    }
    static SkCell Cell(FormKey key, string edid) => new(key, Rel) { EditorID = edid, Flags = SkCell.Flag.IsInteriorCell };

    /// <summary>Write the order under <paramref name="dir"/>\ovc, the loose files under dir\ovc\mods and the base emit
    /// dir\ovc\base.json.</summary>
    public static Written Write(string dir)
    {
        var d = Path.Combine(dir, "ovc"); Directory.CreateDirectory(d);
        var paths = new List<string>();
        string Save(SkyrimMod mod, params ISkyrimModGetter[] masters)
        {
            var path = Path.Combine(d, mod.ModKey.FileName.String);
            mod.BeginWrite.ToPath(path).WithLoadOrder(masters).Write();
            paths.Add(path);
            return path;
        }
        // Each plugin holds the cells it places into, filed the way MovedRefFixture files an interior cell.
        SkyrimMod Plugin(ModKey key, params (FormKey Cell, string Edid, PlacedObject[] Refs)[] cells)
        {
            var m = new SkyrimMod(key, Rel);
            foreach (var (ck, edid, refs) in cells)
            {
                var c = Cell(ck, edid);
                foreach (var r in refs) c.Temporary.Add(r);
                MovedRefFixture.FileInterior(m, c);
            }
            return m;
        }

        var sk = Plugin(Skyrim, (Cell1, "OcCell", new[] { Ref(V1, Box, 10000, 0, 0) }), (Cell2, "OcCellB", Array.Empty<PlacedObject>()));
        sk.Statics.Add(new Static(Box, Rel) { EditorID = "OcBox", Model = new Model { File = MeshRel } });
        sk.Statics.Add(new Static(Bare, Rel) { EditorID = "OcBare" });
        sk.Statics.Add(new Static(Box2, Rel) { EditorID = "OcBox2", Model = new Model { File = MeshRel } });
        var skPath = Save(sk);
        using var skOv = SkyrimMod.CreateFromBinaryOverlay(skPath, Rel);

        Save(Plugin(Dawnguard, (Cell1, "OcCell", new[] { Ref(D1, Box, 2000, 0, 0) })), skOv);
        Save(Plugin(HearthFires, (Cell1, "OcCell", new[] { Ref(E1, Box, 2050, 30, 20) })), skOv);

        var k = Ref(K, Box, -50, -30, -20);
        k.SkyrimMajorRecordFlags |= SkyrimMajorRecord.SkyrimMajorRecordFlag.InitiallyDisabled;
        var u = Ref(U, Box, 18000, 0, 0);
        u.EnableParent = new EnableParent { Flags = 0 };
        u.EnableParent.Reference.SetTo(MissingParent);
        var aPath = Save(Plugin(OcA,
            (Cell1, "OcCell", new[] { Ref(A1, Box, 0, 0, 0), Ref(A2, Box, 4000, 0, 0), Ref(X, Box, 6000, 0, 0), Ref(Z, Bare, 8000, 0, 0),
                                       Ref(Kd, Box, -50, 30, -20), k, Ref(G, Bare, 16000, 0, 0), u, Ref(R2, Box, 20000, 0, 0) }),
            (Cell2, "OcCellB", new[] { Ref(Q, Box, 50, 30, 20) })), skOv);
        using var aOv = SkyrimMod.CreateFromBinaryOverlay(aPath, Rel);

        Save(Plugin(OcB, (Cell1, "OcCell", new[] { Ref(B1, Box, 50, 30, 20), Ref(Y, Box, 6050, 30, 20), Ref(W, Box, 8050, 30, 20) })), skOv);
        // S1 links (XLKR) to A2, so OcSeen.esp masters OcA.esp: A2 was in OcSeen's editor. A link is no placement step.
        var s1 = Ref(S1, Box, 4050, 30, 20);
        var link = new LinkedReferences(); link.Reference.SetTo(A2); s1.LinkedReferences.Add(link);
        Save(Plugin(OcSeen, (Cell1, "OcCell", new[] { s1 })), skOv, aOv);
        var kd = Ref(Kd, Box, -50, 30, -20);
        kd.IsDeleted = true;
        Save(Plugin(OcMover, (Cell1, "OcCell", new[] { Ref(X, Box, 6020, 0, 0), kd })), skOv, aOv);
        Save(Plugin(OcRebase, (Cell1, "OcCell", new[] { Ref(Z, Box, 8020, 0, 0) })), skOv, aOv);

        var mods = Path.Combine(d, "mods");
        var mesh = Path.Combine(mods, Skyrim.Name, "meshes", MeshRel);
        Directory.CreateDirectory(Path.GetDirectoryName(mesh)!);
        File.WriteAllBytes(mesh, BaseGameFixture.RenderBox(50f));
        var ini = Path.Combine(mods, OcA.Name, SwapIni);
        Directory.CreateDirectory(Path.GetDirectoryName(ini)!);
        File.WriteAllText(ini, "[References]\n0x909~OcA.esp|0x803~Skyrim.esm,0x801~Skyrim.esm\n");

        var baseEmit = Path.Combine(d, "base.json");
        ConflictIo.Write(baseEmit, new ConflictFile("overlapprobe", "fixture", "2026-10-15 00:00:00Z", BaseRows()));
        return new Written(paths, mods, baseEmit);
    }

    /// <summary>The base emit's rows, b1-b10: one per exit of the physical predicate, and one row of another kind.</summary>
    public static List<Conflict> BaseRows()
    {
        Conflict Row(string id, string kind, FormKey necessary) => new(kind, "OcA.esp", "OcB.esp", id,
            new List<ConflictRecord> { new(necessary.ToString(), "placed-ref", null) },
            new List<ConflictRecord> { new(A1.ToString(), "placed-ref", null) });
        return new List<Conflict>
        {
            Row("b1 holds", "OVERLAP", B1), Row("b2 separated", "OVERLAP", A2), Row("b3 deleted", "OVERLAP", Kd),
            Row("b4 disabled", "OVERLAP", K), Row("b5 another space", "OVERLAP", Q), Row("b6 no geometry", "OVERLAP", G),
            Row("b7 not placed", "OVERLAP", Ghost), Row("b8 unresolved chain", "OVERLAP", U), Row("b9 two BOS outcomes", "OVERLAP", R2),
            Row("b10 another kind", "CONTACT", B1),
        };
    }
}
