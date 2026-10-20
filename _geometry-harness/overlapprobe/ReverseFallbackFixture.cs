using Mutagen.Bethesda;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Skyrim;
using HarnessShared;
using SkCell = Mutagen.Bethesda.Skyrim.Cell;

// THE REVERSE-OF FALLBACK FIXTURE (overlap acceleration, Gate B finding 7): an order in which a --base row reaches a base
// record the candidate stage never looks up, so overlapprobe --reverse-of must measure the full reverse. Written by
// overlapprobe --self-test under rf\ (PD_FIXTURE_KEEP keeps it); tests/test_reverse_reconcile.py runs the real probe on it.
//   Skyrim.esm  STAT RfBox and STAT RfOther, both the closed render box rf\box.nif (half extent 50 u, loose, mods\Skyrim);
//               interior cell RfCell; V1 (RfOther) at (50,30,20). Skyrim.esm is a base-game name: V1 is set aside as
//               base-game-responsible before its base is looked up, and no candidate uses RfOther.
//   CrA.esp     masters Skyrim.esm; A1 (RfBox) at (0,0,0), A2 (RfBox) at (3000,0,0).
// The test's --base row is V1 against A1: re-measuring it looks RfOther up, a base outside the candidates'.
static class ReverseFallbackFixture
{
    const SkyrimRelease Rel = SkyrimRelease.SkyrimSE;
    public static readonly ModKey SkyrimKey = new("Skyrim", ModType.Master), AKey = new("CrA", ModType.Plugin);
    public static readonly FormKey RfBox = new(SkyrimKey, 0x801), RfOther = new(SkyrimKey, 0x802), RfCell = new(SkyrimKey, 0x810),
        V1 = new(SkyrimKey, 0x901), A1 = new(AKey, 0x901), A2 = new(AKey, 0x902);
    public const string MeshRel = "rf\\box.nif";

    static PlacedObject Ref(FormKey key, FormKey baseKey, float x, float y, float z)
    {
        var r = new PlacedObject(key, Rel) { Placement = new Placement { Position = new(x, y, z), Rotation = new(0, 0, 0) } };
        r.Base.SetTo(baseKey);
        return r;
    }

    /// <summary>Write the two plugins under dir\rf and the box under dir\rf\mods\Skyrim; returns the plugin paths in order.</summary>
    public static IReadOnlyList<string> Write(string dir)
    {
        var root = Path.Combine(dir, "rf"); Directory.CreateDirectory(root);
        string Save(SkyrimMod mod, params ISkyrimModGetter[] masters)
        {
            var path = Path.Combine(root, mod.ModKey.FileName.String);
            mod.BeginWrite.ToPath(path).WithLoadOrder(masters).Write();
            return path;
        }
        var sk = new SkyrimMod(SkyrimKey, Rel);
        sk.Statics.Add(new Static(RfBox, Rel) { EditorID = "RfBox", Model = new Model { File = MeshRel } });
        sk.Statics.Add(new Static(RfOther, Rel) { EditorID = "RfOther", Model = new Model { File = MeshRel } });
        var c = new SkCell(RfCell, Rel) { EditorID = "RfCell", Flags = SkCell.Flag.IsInteriorCell };
        c.Temporary.Add(Ref(V1, RfOther, 50, 30, 20));
        MovedRefFixture.FileInterior(sk, c);
        var skPath = Save(sk);
        string aPath;
        using (var skOv = SkyrimMod.CreateFromBinaryOverlay(skPath, Rel))
        {
            var a = new SkyrimMod(AKey, Rel);
            var ca = new SkCell(RfCell, Rel) { EditorID = "RfCell", Flags = SkCell.Flag.IsInteriorCell };
            ca.Temporary.Add(Ref(A1, RfBox, 0, 0, 0)); ca.Temporary.Add(Ref(A2, RfBox, 3000, 0, 0));
            MovedRefFixture.FileInterior(a, ca);
            aPath = Save(a, skOv);
        }
        var mesh = Path.Combine(root, "mods", SkyrimKey.Name, "meshes", MeshRel);
        Directory.CreateDirectory(Path.GetDirectoryName(mesh)!);
        File.WriteAllBytes(mesh, BaseGameFixture.RenderBox(50f));
        return new[] { skPath, aPath };
    }
}
