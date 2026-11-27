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

    // ---- THE NEAR FIXTURE (Gate E: blockprobe --near, its --self-test) ------------------------------------------------------
    // One interior corridor, RnCorridor, 6 x 64 = 384 u wide (y -192..192) and 1,280 u long (x -640..640), with four boxes:
    //   K  (0, 0)       the object asked about (--near K)
    //   N1 (300, 0)     a neighbour: its route window (its box grown 256 u) reaches K's box, so K's move can change its verdict
    //   F  (-560, 0)    far: its window (-886..-234) does not reach K's box (-70..70); it does reach K moved to (-400, 0)
    //   G  (-300, 700)  off the corridor (no navmesh under it): not measured
    // and a second plugin, RnMover.esp, that moves G onto the corridor at (-300, 0): the cut a move creates.
    public static readonly ModKey NearKey = new("RnMaster", ModType.Master), NearMoverKey = new("RnMover", ModType.Plugin);
    static FormKey Nk(uint id) => new(NearKey, id);
    public static readonly FormKey NearCell = Nk(0x811), NearNav = Nk(0x821), NearBase = Nk(0x801);
    public static readonly FormKey K = Nk(0x901), N1 = Nk(0x902), F = Nk(0x903), G = Nk(0x904);
    public static readonly (FormKey Key, float X, float Y)[] NearBoxes = { (K, 0, 0), (N1, 300, 0), (F, -560, 0), (G, -300, 700) };
    public static readonly (float X, float Y) GMoved = (-300, 0);

    public sealed record NearWritten(string Master, string Mover, string ModsDir);

    public static NearWritten WriteNear(string dir)
    {
        var d = Path.Combine(dir, "rn"); Directory.CreateDirectory(d);
        var m = new SkyrimMod(NearKey, Rel);
        m.Statics.Add(new Static(NearBase, Rel) { EditorID = "RnBox", Model = new Model { File = BlockFixture.MeshRel } });
        PlacedObject Box(FormKey k, float x, float y)
        {
            var r = new PlacedObject(k, Rel) { Placement = new Placement { Position = new(x, y, 0), Rotation = new(0, 0, 0) } };
            r.Base.SetTo(NearBase);
            return r;
        }
        var cell = new SkCell(NearCell, Rel) { EditorID = "RnCorridor", Flags = SkCell.Flag.IsInteriorCell };
        foreach (var (k, x, y) in NearBoxes) cell.Temporary.Add(Box(k, x, y));
        cell.NavigationMeshes.Add(BlockFixture.Grid(NearNav, NearCell, 20, 6, 64f, -640f, -6 * 32f));
        MovedRefFixture.FileInterior(m, cell);
        var master = Path.Combine(d, m.ModKey.FileName.String);
        m.BeginWrite.ToPath(master).WithNoLoadOrder().Write();
        using var mOv = SkyrimMod.CreateFromBinaryOverlay(master, Rel);

        var mv = new SkyrimMod(NearMoverKey, Rel);
        var c2 = new SkCell(NearCell, Rel) { EditorID = "RnCorridor", Flags = SkCell.Flag.IsInteriorCell };
        c2.Temporary.Add(Box(G, GMoved.X, GMoved.Y));
        MovedRefFixture.FileInterior(mv, c2);
        var mover = Path.Combine(d, mv.ModKey.FileName.String);
        mv.BeginWrite.ToPath(mover).WithLoadOrder(mOv).Write();

        var mods = Path.Combine(d, "mods");
        var mesh = Path.Combine(mods, NearKey.Name, "meshes", BlockFixture.MeshRel);
        Directory.CreateDirectory(Path.GetDirectoryName(mesh)!);
        File.WriteAllBytes(mesh, BlockFixture.BoxNif());
        return new NearWritten(master, mover, mods);
    }
}
