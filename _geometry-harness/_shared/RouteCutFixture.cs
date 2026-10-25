using Mutagen.Bethesda;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Skyrim;
using SkCell = Mutagen.Bethesda.Skyrim.Cell;

namespace HarnessShared;

// THE ROUTE-CUT FIXTURE (`blockprobe --write-route-cut-fixture DIR`, read by tests/test_route_cut_rows.py, which runs the real
// blockprobe on it). Test-only; written with Mutagen and BlockFixture's grid navmesh and box mesh (one rigid body, a
// bhkBoxShape about 70 u half extent, centred on the reference: 140 u across). Each cell is a straight corridor of navmesh
// running along x, 1,280 u long, made of 64 u squares, with one box standing in its middle. The corridor's width decides the
// passage left beside the box. The tests read the expected rows from the probe's own route-cut listing of the same run; the
// verdicts below are what the base build (95c58d67) lists for it.
//
// Load order: RcMaster.esm (one plugin; the run's target).
//
//   cell          corridor (squares across, width)   box at     route-cut verdict, passage (each side)
//   RcNarrowOne   3 x 64 = 192 u                     (0, 0)     Blocked, 32 u
//   RcNarrowTwo   3 x 64 = 192 u                     (0, 0)     Blocked, 32 u
//   RcWideOne     6 x 64 = 384 u                     (0, 0)     LikelyBlocked, 128 u
//   RcWideTwo     6 x 64 = 384 u                     (0, 0)     LikelyBlocked, 128 u
//   RcOffset      6 x 64 = 384 u                     (0, 150)   NoLine (the window holds open ground with no ends)
public static class RouteCutFixture
{
    public const SkyrimRelease Rel = SkyrimRelease.SkyrimSE;
    public static readonly ModKey MasterKey = new("RcMaster", ModType.Master);
    static FormKey Mk(uint id) => new(MasterKey, id);
    public static readonly FormKey WallBase = Mk(0x801);
    public static readonly (string Name, FormKey Cell, FormKey Nav, FormKey Box, int Squares, float BoxY)[] Corridors =
    {
        ("RcNarrowOne", Mk(0x811), Mk(0x821), Mk(0x901), 3, 0f),
        ("RcNarrowTwo", Mk(0x812), Mk(0x822), Mk(0x902), 3, 0f),
        ("RcWideOne", Mk(0x813), Mk(0x823), Mk(0x903), 6, 0f),
        ("RcWideTwo", Mk(0x814), Mk(0x824), Mk(0x904), 6, 0f),
        ("RcOffset", Mk(0x815), Mk(0x825), Mk(0x905), 6, 150f),
    };

    public sealed record Written(IReadOnlyList<string> Order, string ModsDir);

    public static Written Write(string dir)
    {
        var d = Path.Combine(dir, "rc"); Directory.CreateDirectory(d);
        var m = new SkyrimMod(MasterKey, Rel);
        m.Statics.Add(new Static(WallBase, Rel) { EditorID = "RcBox", Model = new Model { File = BlockFixture.MeshRel } });
        foreach (var c in Corridors)
        {
            var cell = new SkCell(c.Cell, Rel) { EditorID = c.Name, Flags = SkCell.Flag.IsInteriorCell };
            var r = new PlacedObject(c.Box, Rel) { Placement = new Placement { Position = new(0, c.BoxY, 0), Rotation = new(0, 0, 0) } };
            r.Base.SetTo(WallBase);
            cell.Temporary.Add(r);
            cell.NavigationMeshes.Add(BlockFixture.Grid(c.Nav, c.Cell, 20, c.Squares, 64f, -640f, -c.Squares * 32f));
            MovedRefFixture.FileInterior(m, cell);
        }
        var path = Path.Combine(d, m.ModKey.FileName.String);
        m.BeginWrite.ToPath(path).WithNoLoadOrder().Write();
        var mods = Path.Combine(d, "mods");
        var mesh = Path.Combine(mods, MasterKey.Name, "meshes", BlockFixture.MeshRel);
        Directory.CreateDirectory(Path.GetDirectoryName(mesh)!);
        File.WriteAllBytes(mesh, BlockFixture.BoxNif());
        return new Written(new[] { path }, mods);
    }
}
