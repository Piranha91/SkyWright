using System.Numerics;
using Mutagen.Bethesda;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Skyrim;
using NiflySharp;
using NiflySharp.Blocks;
using NiflySharp.Structs;
using SkCell = Mutagen.Bethesda.Skyrim.Cell;

namespace HarnessShared;

// THE BASE-GAME-NAME FIXTURE (overlapprobe and depprobe --self-test, copied-rules task 2026-10-15): synthetic orders whose
// plugins carry Bethesda's file names, so a probe's by-name population split has rows to decide. Test-only; written with
// Mutagen, the render box with NiflySharp. Two orders, each in its own folder:
//
// ov\  (overlapprobe): Skyrim.esm, Dawnguard.esm, HearthFires.esm, CrMod.esp, CrA.esp, CrB.esp; every plugin but Skyrim.esm
//   masters only it.
//   One interior cell C1 in Skyrim.esm; one STAT base CrBox whose model is a closed render box, half extent 50 u
//   (mods\Skyrim\meshes\cr\box.nif, loose). Each pair is offset (50, 30, 20); clusters stand 2,000 u apart.
//     Skyrim.esm   V1 (0,0,0), V2 (50,30,20), V3 (2000,0,0), V4 (4000,0,0)
//     Dawnguard    D1 (2050,30,20)       CrMod  M1 (4050,30,20)
//     HearthFires  E1 (2110,60,40): it meets D1 and not V3, a DLC-on-DLC pair neither declares
//     CrA          A1 (6000,0,0)         CrB    B1 (6050,30,20)
// dep\ (depprobe): Skyrim.esm, Dawnguard.esm, CrMod.esp, Unofficial Skyrim Special Edition Patch.esp, each mastering only
//   Skyrim.esm. One interior cell C2 in Skyrim.esm; STAT base DpThing. Hosts H1-H3 at x 0, 1000, 2000; dependents A1-A3
//   64 u above each, each linked (XLKR) to its host. Dawnguard moves H1, CrMod moves H2, the USSEP-named plugin moves H3,
//   each 500 u along x.
public static class BaseGameFixture
{
    public const SkyrimRelease Rel = SkyrimRelease.SkyrimSE;
    public static readonly ModKey SkyrimKey = new("Skyrim", ModType.Master), DawnguardKey = new("Dawnguard", ModType.Master),
        HearthFiresKey = new("HearthFires", ModType.Master);
    public static readonly ModKey ModKeyM = new("CrMod", ModType.Plugin), AKey = new("CrA", ModType.Plugin), BKey = new("CrB", ModType.Plugin);
    public static readonly ModKey UssepKey = new("Unofficial Skyrim Special Edition Patch", ModType.Plugin);
    static FormKey Sk(uint id) => new(SkyrimKey, id);
    public static readonly FormKey Box = Sk(0x801), Thing = Sk(0x802), C1 = Sk(0x810), C2 = Sk(0x811);
    public static readonly FormKey V1 = Sk(0x901), V2 = Sk(0x902), V3 = Sk(0x903), V4 = Sk(0x904);
    public static readonly FormKey D1 = new(DawnguardKey, 0x901), M1 = new(ModKeyM, 0x901), A1 = new(AKey, 0x901), B1 = new(BKey, 0x901),
        E1 = new(HearthFiresKey, 0x901);
    public static readonly FormKey H1 = Sk(0xA01), H2 = Sk(0xA02), H3 = Sk(0xA03), Dep1 = Sk(0xA11), Dep2 = Sk(0xA12), Dep3 = Sk(0xA13);
    public const string MeshRel = "cr\\box.nif";
    public const float Half = 50f;

    /// <summary>Each order's plugins in load order, and its mods folder (loose files under the plugin's mod folder).</summary>
    public sealed record Written(IReadOnlyList<string> Overlap, string OverlapMods, IReadOnlyList<string> Dependency);

    static PlacedObject Ref(FormKey key, FormKey baseKey, float x, float y, float z)
    {
        var r = new PlacedObject(key, Rel) { Placement = new Placement { Position = new(x, y, z), Rotation = new(0, 0, 0) } };
        r.Base.SetTo(baseKey);
        return r;
    }
    static SkCell Cell(FormKey key, string edid) => new(key, Rel) { EditorID = edid, Flags = SkCell.Flag.IsInteriorCell };

    /// <summary>A closed render box: 8 corners at +-<paramref name="half"/>, 12 outward triangles, one BSTriShape.</summary>
    public static byte[] RenderBox(float half)
    {
        var f = new NifFile();
        var version = new NiVersion { FileVersion = NiVersion.ToFile("20.2.0.7"), UserVersion = 12, StreamVersion = 100 };
        f.Create(version, withRootNode: true);
        var root = f.GetRootNodes().First();
        var node = new NiNode { Scale = 1f };
        root.Children.AddBlockRef(f.AddBlock(node));
        var v = new List<Vector3>();
        for (int i = 0; i < 8; i++) v.Add(new Vector3((i & 1) != 0 ? half : -half, (i & 2) != 0 ? half : -half, (i & 4) != 0 ? half : -half));
        var t = new List<Triangle>
        {
            new(0, 2, 1), new(1, 2, 3),   // z-
            new(4, 5, 6), new(5, 7, 6),   // z+
            new(0, 1, 4), new(1, 5, 4),   // y-
            new(2, 6, 3), new(3, 6, 7),   // y+
            new(0, 4, 2), new(2, 4, 6),   // x-
            new(1, 3, 5), new(3, 7, 5),   // x+
        };
        var shape = new BSTriShape { Name = new("render"), Flags_ui = 0xEu, Scale = 1f };
        shape.Create(version, v, t, new(), new());
        shape.IsSkinned = false;
        node.Children.AddBlockRef(f.AddBlock(shape));
        using var s = new MemoryStream();
        if (f.Save(s) != 0) throw new InvalidOperationException("base-game fixture render box failed to save");
        return s.ToArray();
    }

    /// <summary>Write both orders under <paramref name="dir"/> (dir\ov, dir\dep) and the box under dir\ov\mods.</summary>
    public static Written Write(string dir)
    {
        string Save(string sub, SkyrimMod mod, params ISkyrimModGetter[] masters)
        {
            var d = Path.Combine(dir, sub); Directory.CreateDirectory(d);
            var path = Path.Combine(d, mod.ModKey.FileName.String);
            mod.BeginWrite.ToPath(path).WithLoadOrder(masters).Write();
            return path;
        }

        // ---- ov ----
        var sk = new SkyrimMod(SkyrimKey, Rel);
        sk.Statics.Add(new Static(Box, Rel) { EditorID = "CrBox", Model = new Model { File = MeshRel } });
        var c1 = Cell(C1, "CrCell");
        c1.Temporary.Add(Ref(V1, Box, 0, 0, 0)); c1.Temporary.Add(Ref(V2, Box, 50, 30, 20));
        c1.Temporary.Add(Ref(V3, Box, 2000, 0, 0)); c1.Temporary.Add(Ref(V4, Box, 4000, 0, 0));
        MovedRefFixture.FileInterior(sk, c1);
        var skPath = Save("ov", sk);
        var ov = new List<string> { skPath };
        using (var skOv = SkyrimMod.CreateFromBinaryOverlay(skPath, Rel))
        {
            foreach (var (key, refKey, x, y, z) in new[] { (DawnguardKey, D1, 2050f, 30f, 20f), (HearthFiresKey, E1, 2110f, 60f, 40f),
                                                          (ModKeyM, M1, 4050f, 30f, 20f), (AKey, A1, 6000f, 0f, 0f), (BKey, B1, 6050f, 30f, 20f) })
            {
                var m = new SkyrimMod(key, Rel);
                var c = Cell(C1, "CrCell");
                c.Temporary.Add(Ref(refKey, Box, x, y, z));
                MovedRefFixture.FileInterior(m, c);
                ov.Add(Save("ov", m, skOv));
            }
        }
        var mods = Path.Combine(dir, "ov", "mods");
        var mesh = Path.Combine(mods, SkyrimKey.Name, "meshes", MeshRel);
        Directory.CreateDirectory(Path.GetDirectoryName(mesh)!);
        File.WriteAllBytes(mesh, RenderBox(Half));

        // ---- dep ----
        static PlacedObject Linked(PlacedObject r, FormKey host) { var l = new LinkedReferences(); l.Reference.SetTo(host); r.LinkedReferences.Add(l); return r; }
        var dm = new SkyrimMod(SkyrimKey, Rel);
        dm.Statics.Add(new Static(Thing, Rel) { EditorID = "DpThing" });
        var c2 = Cell(C2, "DpCell");
        var hosts = new[] { (H1, Dep1, 0f), (H2, Dep2, 1000f), (H3, Dep3, 2000f) };
        foreach (var (h, a, x) in hosts) { c2.Temporary.Add(Ref(h, Thing, x, 0, 0)); c2.Temporary.Add(Linked(Ref(a, Thing, x, 0, 64), h)); }
        MovedRefFixture.FileInterior(dm, c2);
        var dmPath = Save("dep", dm);
        var dep = new List<string> { dmPath };
        using (var dmOv = SkyrimMod.CreateFromBinaryOverlay(dmPath, Rel))
        {
            foreach (var (key, i) in new[] { (DawnguardKey, 0), (ModKeyM, 1), (UssepKey, 2) })
            {
                var m = new SkyrimMod(key, Rel);
                var c = Cell(C2, "DpCell");
                c.Temporary.Add(Ref(hosts[i].Item1, Thing, hosts[i].Item3 + 500, 0, 0));
                MovedRefFixture.FileInterior(m, c);
                dep.Add(Save("dep", m, dmOv));
            }
        }
        return new Written(ov, mods, dep);
    }

    // THE PATCH ADDITION (F5, 2026-10-15): CrPatch.esp after the ov order, written by its own method so no other byte changes.
    // It masters CrA and CrB (and Skyrim.esm) through one new reference P1 at (8000,0,0) in C1 whose base CrPatchThing has no
    // model, linked (XLKR) to A1 and B1; so it is a patch of the pair A1 x B1 and places nothing that renders.
    public static readonly ModKey PatchKey = new("CrPatch", ModType.Plugin);
    public static readonly FormKey PatchThing = new(PatchKey, 0x801), P1 = new(PatchKey, 0x901);

    /// <summary>Write CrPatch.esp into dir\ov (which must already hold the ov order from <see cref="Write"/>). Returns its path.</summary>
    public static string WritePatch(string dir)
    {
        var ov = Path.Combine(dir, "ov");
        using var skOv = SkyrimMod.CreateFromBinaryOverlay(Path.Combine(ov, SkyrimKey.FileName.String), Rel);
        using var aOv = SkyrimMod.CreateFromBinaryOverlay(Path.Combine(ov, AKey.FileName.String), Rel);
        using var bOv = SkyrimMod.CreateFromBinaryOverlay(Path.Combine(ov, BKey.FileName.String), Rel);
        var m = new SkyrimMod(PatchKey, Rel);
        m.Statics.Add(new Static(PatchThing, Rel) { EditorID = "CrPatchThing" });
        var c = Cell(C1, "CrCell");
        var p1 = Ref(P1, PatchThing, 8000, 0, 0);
        foreach (var target in new[] { A1, B1 }) { var l = new LinkedReferences(); l.Reference.SetTo(target); p1.LinkedReferences.Add(l); }
        c.Temporary.Add(p1);
        MovedRefFixture.FileInterior(m, c);
        var path = Path.Combine(ov, PatchKey.FileName.String);
        m.BeginWrite.ToPath(path).WithLoadOrder(skOv, aOv, bOv).Write();
        return path;
    }
}
