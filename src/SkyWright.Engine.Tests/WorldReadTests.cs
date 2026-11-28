using System.Security.Cryptography;
using Mutagen.Bethesda;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Plugins.Records;
using Mutagen.Bethesda.Skyrim;
using Noggog;
using Xunit;
using SkCell = Mutagen.Bethesda.Skyrim.Cell;

namespace SkyWright.Engine.Tests;

/// <summary>Writes synthetic plugins for the world-read tests (the MovedRefFixture way: Mutagen, a temp folder).</summary>
static class WrPlugins
{
    public static readonly SkyrimRelease Rel = SkyrimRelease.SkyrimSE;

    public static PlacedObject Ref(FormKey key, FormKey? baseKey, float x, float y, float rotZ = 0)
    {
        var r = new PlacedObject(key, Rel) { Placement = new Placement { Position = new P3Float(x, y, 0), Rotation = new P3Float(0, 0, rotZ) } };
        if (baseKey is { } b) r.Base.SetTo(b);
        return r;
    }
    public static PlacedObject Deleted(FormKey key) => new(key, Rel) { IsDeleted = true };
    public static SkCell Ext(FormKey key, int x, int y) => new(key, Rel) { Flags = default, Grid = new CellGrid { Point = new P2Int(x, y) } };
    public static SkCell Int(FormKey key) => new(key, Rel) { Flags = SkCell.Flag.IsInteriorCell };
    public static SkCell Top(FormKey key) => new(key, Rel) { Flags = default };

    public static void FileInterior(SkyrimMod mod, SkCell cell)
    {
        uint id = cell.FormKey.ID;
        int blockN = (int)(id % 10), subN = (int)((id / 10) % 10);
        var block = mod.Cells.Records.FirstOrDefault(b => b.BlockNumber == blockN);
        if (block is null) { block = new CellBlock { BlockNumber = blockN, GroupType = GroupTypeEnum.InteriorCellBlock }; mod.Cells.Records.Add(block); }
        var sub = block.SubBlocks.FirstOrDefault(s => s.BlockNumber == subN);
        if (sub is null) { sub = new CellSubBlock { BlockNumber = subN, GroupType = GroupTypeEnum.InteriorCellSubBlock }; block.SubBlocks.Add(sub); }
        sub.Cells.Add(cell);
    }

    public static Worldspace World(FormKey wk, SkCell? top, params SkCell[] exteriors)
    {
        var ws = new Worldspace(wk, Rel);
        if (top is not null) ws.TopCell = top;
        if (exteriors.Length > 0)
        {
            var blk = new WorldspaceBlock { BlockNumberX = 0, BlockNumberY = 0, GroupType = GroupTypeEnum.ExteriorCellBlock };
            var sub = new WorldspaceSubBlock { BlockNumberX = 0, BlockNumberY = 0, GroupType = GroupTypeEnum.ExteriorCellSubBlock };
            foreach (var c in exteriors) sub.Items.Add(c);
            blk.Items.Add(sub); ws.SubCells.Add(blk);
        }
        return ws;
    }

    public static string Save(string dir, SkyrimMod mod, params ISkyrimModGetter[] masters)
    {
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, mod.ModKey.FileName.String);
        mod.BeginWrite.ToPath(path).WithLoadOrder(masters).Write();
        return path;
    }

    public static ObjectBounds Box(short x, short y, short z) => new() { First = new P3Int16((short)-x, (short)-y, 0), Second = new P3Int16(x, y, z) };
}

/// <summary>
/// The shared load order for rules 1 to 8, prepared once. Order: WrMaster.esm (0), WrA.esp (1), WrB.esp (2), WrC.esp (3).
/// World W: persistent cell T, grid cells E00 (0,0), E10 (1,0); interior I1.
///
///   ref  master                        later plugins                                   expected winner
///   R1   E00 (100,100)                 A: E00 (200,100); B: E00 (300,100)              B, x 300
///   R2   E00 (110,100) rot 0           C: E00 (110,100) rot z 1 (rotation only)        C, rot z 1
///   R3   E00 (120,100)                 B: E10 (4200,100)                               B in E10 (moved out of E00)
///   R4   E10 (4300,100)                A: E00 (130,100)                                A in E00 (moved in)
///   R5   E00 (140,100)                 B: deleted, no placement data                   B, deleted
///   R6   T (150,150) persistent        untouched                                       master, persistent, grid (0,0)
///   R6b  T (20530,50) persistent       untouched                                       master, persistent, grid (5,0)
///   R7   T (160,160) persistent        A: E00 (160,160) temporary                      A in E00, temporary
///   R8   E00 (170,100) temporary       A: T (170,100) persistent                       A in T, persistent
///   R9   E10 (4100,100), base Big      untouched                                       master in E10 (its box crosses into E00)
///   R10  (A's own) E00 (180,100), enable parent P1                                     A
///   P1   I1 (0,0)                      B: I1 (5,5)                                     B in I1
///   Bases: S (model s.nif); S2 (s2.nif; A: s2a.nif, marker flag, OBND 80x80x40); Big (OBND 6000); L light (carried;
///   B: not carried).
/// </summary>
public sealed class WorldReadWorld : IDisposable
{
    public static readonly ModKey MKey = new("WrMaster", ModType.Master), AKey = new("WrA", ModType.Plugin),
        BKey = new("WrB", ModType.Plugin), CKey = new("WrC", ModType.Plugin);
    static FormKey Mk(uint id) => new(MKey, id);
    public static readonly FormKey S = Mk(0x801), S2 = Mk(0x802), Big = Mk(0x803), L = Mk(0x804);
    public static readonly FormKey W = Mk(0x830), T = Mk(0x831), E00 = Mk(0x840), E10 = Mk(0x841), I1 = Mk(0x850);
    public static readonly FormKey R1 = Mk(0x901), R2 = Mk(0x902), R3 = Mk(0x903), R4 = Mk(0x904), R5 = Mk(0x905), R6 = Mk(0x906),
        R6b = Mk(0x90A), R7 = Mk(0x907), R8 = Mk(0x908), R9 = Mk(0x909), P1 = Mk(0x90C);
    public static readonly FormKey R10 = new(AKey, 0x950);

    public readonly string Dir = Path.Combine(Path.GetTempPath(), "sw-worldread-" + Guid.NewGuid().ToString("N"));
    public string Cache => Path.Combine(Dir, "cache");
    public readonly IReadOnlyList<string> Paths;
    public readonly WorldRead Read;

    public WorldReadWorld()
    {
        var rel = WrPlugins.Rel;
        string plugins = Path.Combine(Dir, "plugins");

        var m = new SkyrimMod(MKey, rel);
        m.Statics.Add(new Static(S, rel) { EditorID = "WrS", Model = new Model { File = "meshes\\wr\\s.nif" }, ObjectBounds = WrPlugins.Box(10, 10, 20) });
        m.Statics.Add(new Static(S2, rel) { EditorID = "WrS2", Model = new Model { File = "meshes\\wr\\s2.nif" }, ObjectBounds = WrPlugins.Box(5, 5, 5) });
        m.Statics.Add(new Static(Big, rel) { EditorID = "WrBig", Model = new Model { File = "meshes\\wr\\big.nif" }, ObjectBounds = WrPlugins.Box(3000, 3000, 100) });
        m.Lights.Add(new Light(L, rel) { EditorID = "WrTorch", Model = new Model { File = "meshes\\wr\\torch.nif" }, Flags = Light.Flag.CanBeCarried });
        {
            var t = WrPlugins.Top(T);
            t.Persistent.Add(WrPlugins.Ref(R6, S, 150, 150)); t.Persistent.Add(WrPlugins.Ref(R6b, S, 5 * 4096 + 50, 50));
            t.Persistent.Add(WrPlugins.Ref(R7, S, 160, 160));
            var e00 = WrPlugins.Ext(E00, 0, 0);
            foreach (var (k, x) in new[] { (R1, 100f), (R2, 110f), (R3, 120f), (R5, 140f), (R8, 170f) }) e00.Temporary.Add(WrPlugins.Ref(k, S, x, 100));
            var e10 = WrPlugins.Ext(E10, 1, 0);
            e10.Temporary.Add(WrPlugins.Ref(R4, S, 4300, 100)); e10.Temporary.Add(WrPlugins.Ref(R9, Big, 4100, 100));
            m.Worldspaces.Add(WrPlugins.World(W, t, e00, e10));
            var i1 = WrPlugins.Int(I1); i1.Temporary.Add(WrPlugins.Ref(P1, S, 0, 0)); WrPlugins.FileInterior(m, i1);
        }
        var mPath = WrPlugins.Save(plugins, m);

        var a = new SkyrimMod(AKey, rel);
        a.Statics.Add(new Static(S2, rel) { EditorID = "WrS2", Model = new Model { File = "meshes\\wr\\s2a.nif" }, ObjectBounds = WrPlugins.Box(40, 40, 40),
                                             MajorRecordFlagsRaw = (int)EnableState.IsMarkerFlag });
        {
            var t = WrPlugins.Top(T); t.Persistent.Add(WrPlugins.Ref(R8, S, 170, 100));
            var e00 = WrPlugins.Ext(E00, 0, 0);
            e00.Temporary.Add(WrPlugins.Ref(R1, S, 200, 100)); e00.Temporary.Add(WrPlugins.Ref(R4, S, 130, 100));
            e00.Temporary.Add(WrPlugins.Ref(R7, S, 160, 160));
            var r10 = WrPlugins.Ref(R10, S, 180, 100); r10.EnableParent = new EnableParent(); r10.EnableParent.Reference.SetTo(P1);
            e00.Temporary.Add(r10);
            a.Worldspaces.Add(WrPlugins.World(W, t, e00));
        }
        var aPath = WrPlugins.Save(plugins, a, m);

        var b = new SkyrimMod(BKey, rel);
        b.Lights.Add(new Light(L, rel) { EditorID = "WrTorch", Model = new Model { File = "meshes\\wr\\torch.nif" }, Flags = (Light.Flag)0 });
        {
            var e00 = WrPlugins.Ext(E00, 0, 0);
            e00.Temporary.Add(WrPlugins.Ref(R1, S, 300, 100)); e00.Temporary.Add(WrPlugins.Deleted(R5));
            var e10 = WrPlugins.Ext(E10, 1, 0); e10.Temporary.Add(WrPlugins.Ref(R3, S, 4200, 100));
            b.Worldspaces.Add(WrPlugins.World(W, null, e00, e10));
            var i1 = WrPlugins.Int(I1); i1.Temporary.Add(WrPlugins.Ref(P1, S, 5, 5)); WrPlugins.FileInterior(b, i1);
        }
        var bPath = WrPlugins.Save(plugins, b, m, a);

        var c = new SkyrimMod(CKey, rel);
        {
            var e00 = WrPlugins.Ext(E00, 0, 0); e00.Temporary.Add(WrPlugins.Ref(R2, S, 110, 100, rotZ: 1f));
            c.Worldspaces.Add(WrPlugins.World(W, null, e00));
        }
        var cPath = WrPlugins.Save(plugins, c, m, a, b);

        Paths = new[] { mPath, aPath, bPath, cPath };
        Read = WorldRead.Open(Paths, Cache);
    }

    public void Dispose() { try { Directory.Delete(Dir, true); } catch (IOException) { } catch (UnauthorizedAccessException) { } }
}

/// <summary>The shared world read, one named test per rule (shared world read, unit 1). Every expectation is worked out
/// by hand from <see cref="WorldReadWorld"/>'s table or, for rule 12, by an independent whole-order replay.</summary>
[Trait("tier", "unit")]
public sealed class WorldReadTests : IClassFixture<WorldReadWorld>, IDisposable
{
    readonly WorldReadWorld _w;
    readonly string _dir = Path.Combine(Path.GetTempPath(), "sw-worldread-own-" + Guid.NewGuid().ToString("N"));
    public WorldReadTests(WorldReadWorld w) => _w = w;
    public void Dispose() { try { Directory.Delete(_dir, true); } catch (IOException) { } catch (UnauthorizedAccessException) { } }

    static FormKey W => WorldReadWorld.W;
    WorldAnswer E00 => _w.Read.Exterior(W, 0, 0, 0, includePersistent: false);
    WorldAnswer E00P => _w.Read.Exterior(W, 0, 0, 0, includePersistent: true);
    WorldAnswer E10 => _w.Read.Exterior(W, 1, 0, 0, includePersistent: false);

    static WorldRef? Win(WorldAnswer a, FormKey key) => a.Winners.SingleOrDefault(x => x.Reference == key);
    static string Names(IEnumerable<WorldRef> refs) => string.Join(", ", refs.Select(x => $"{x.Reference.ID:X3}@p{x.Plugin}"));

    [Fact]
    public void Rule01_the_last_plugin_holding_a_record_of_a_reference_wins()
    {
        var a = E00;
        var r1 = Win(a, WorldReadWorld.R1);
        Assert.True(r1 is { Plugin: 2, PluginName: "WrB.esp" } && r1.Ref!.Position.X == 300f,
            $"[rule 1] R1 is placed by the master, A and B: B's record (x 300) must win, not the middle one; got {(r1 is null ? "no winner" : $"p{r1.Plugin} {r1.PluginName} x {r1.Ref?.Position.X}")} among [{Names(a.Winners)}]");
        Assert.Equal(1, a.Winners.Count(x => x.Reference == WorldReadWorld.R1));
        var r2 = Win(a, WorldReadWorld.R2);
        Assert.True(r2 is { Plugin: 3 } && r2.Ref!.Rotation.Z == 1f,
            $"[rule 1] R2's last record (C) changes only its rotation and must still win with rotation z 1; got {(r2 is null ? "no winner" : $"p{r2.Plugin} rot z {r2.Ref?.Rotation.Z}")}");
    }

    [Fact]
    public void Rule02_a_reference_a_later_plugin_moves_out_of_the_cell_is_not_a_winner_there()
    {
        var a = E00;
        Assert.True(Win(a, WorldReadWorld.R3) is null,
            $"[rule 2] R3 is moved by B from E00 to E10, so it must not stand in E00; E00 winners [{Names(a.Winners)}]");
        var mo = a.MovedOut.SingleOrDefault(x => x.Reference == WorldReadWorld.R3);
        Assert.True(mo is { FromPlugin: 0 } && mo.FromCell == WorldReadWorld.E00 && mo.Winner.Cell == WorldReadWorld.E10 && mo.Winner.Plugin == 2,
            $"[rule 2] R3 must be listed as moved out of E00 by the master's record, its winner B's record in E10; got {(mo is null ? "nothing" : $"from p{mo.FromPlugin} {mo.FromCell} to p{mo.Winner.Plugin} {mo.Winner.Cell}")}");
        var there = Win(E10, WorldReadWorld.R3);
        Assert.True(there is { Plugin: 2 } && there.Ref!.Position.X == 4200f, "[rule 2] R3 must stand in E10 under B's record at x 4200");
    }

    [Fact]
    public void Rule03_a_reference_a_later_plugin_moves_into_the_cell_is_a_winner_there()
    {
        var r4 = Win(E00, WorldReadWorld.R4);
        Assert.True(r4 is { Plugin: 1 } && r4.Ref!.Position.X == 130f && r4.Cell == WorldReadWorld.E00,
            $"[rule 3] R4 is moved by A from E10 into E00 and must stand in E00 under A's record at x 130; got {(r4 is null ? "no winner" : $"p{r4.Plugin} x {r4.Ref?.Position.X}")}");
        var e10 = E10;
        Assert.True(Win(e10, WorldReadWorld.R4) is null && e10.MovedOut.Any(x => x.Reference == WorldReadWorld.R4 && x.Winner.Cell == WorldReadWorld.E00),
            "[rule 3] R4 must not stand in E10 and must be listed as moved out of E10 into E00");
    }

    [Fact]
    public void Rule04_a_reference_deleted_with_no_placement_data_does_not_stand()
    {
        var a = E00;
        Assert.True(Win(a, WorldReadWorld.R5) is null,
            $"[rule 4] R5 is deleted by B with no placement data and must not stand where the master put it; E00 winners [{Names(a.Winners)}]");
        var d = a.Deleted.SingleOrDefault(x => x.Reference == WorldReadWorld.R5);
        Assert.True(d is { Plugin: 2, Deleted: true, HasPlacement: false } && d.Cell == WorldReadWorld.E00,
            $"[rule 4] R5 must be listed as deleted by B's record in E00, with no placement; got {(d is null ? "nothing" : $"p{d.Plugin} deleted {d.Deleted} placement {d.HasPlacement}")}");
        Assert.True(!a.MovedOut.Any(x => x.Reference == WorldReadWorld.R5), "[rule 4] R5 is deleted, not moved out");
        var l = _w.Read.Lookup(WorldReadWorld.R5);
        Assert.True(l is { Deleted: true, Plugin: 2, Ref: null }, "[rule 4] Lookup(R5) must give B's deleted record with no placement");
    }

    [Fact]
    public void Rule05_persistent_references_come_from_the_persistent_cell_by_position()
    {
        var p = E00P;
        var r6 = Win(p, WorldReadWorld.R6);
        Assert.True(r6 is { Persistent: true, Plugin: 0 } && r6.Cell == WorldReadWorld.T,
            $"[rule 5] R6 is persistent in T standing in grid (0,0): asked with the persistent cell it must be returned, marked persistent, filed in T; got {(r6 is null ? "nothing" : $"persistent {r6.Persistent} cell {r6.Cell}")}");
        Assert.True(Win(p, WorldReadWorld.R6b) is null, "[rule 5] R6b is persistent in T standing in grid (5,0) and must not be returned for grid (0,0)");
        var plain = E00;
        Assert.True(Win(plain, WorldReadWorld.R6) is null, "[rule 5] R6 must not be returned when the persistent cell is not asked for");
        Assert.True(p.Cells.Any(c => c.Cell == WorldReadWorld.T && c.IsPersistentCell && c.Grid is null), "[rule 5] the answer's cells must name T as the persistent cell");
        var r7 = Win(plain, WorldReadWorld.R7);
        Assert.True(r7 is { Persistent: false, Plugin: 1 } && r7.Cell == WorldReadWorld.E00,
            $"[rule 5] R7 is moved by A from the persistent cell into E00 as a temporary reference and must stand in E00, not persistent; got {(r7 is null ? "nothing" : $"persistent {r7.Persistent} p{r7.Plugin}")}");
        var mo8 = plain.MovedOut.SingleOrDefault(x => x.Reference == WorldReadWorld.R8);
        Assert.True(Win(plain, WorldReadWorld.R8) is null && mo8 is not null && mo8.Winner.Cell == WorldReadWorld.T,
            "[rule 5] R8 is moved by A from E00 into the persistent cell: without the persistent cell it must be listed as moved out to T");
        var r8 = Win(p, WorldReadWorld.R8);
        Assert.True(r8 is { Persistent: true, Plugin: 1 } && r8.Cell == WorldReadWorld.T && !p.MovedOut.Any(x => x.Reference == WorldReadWorld.R8),
            "[rule 5] R8 must stand, persistent, under A's record in T when the persistent cell is asked for");
    }

    [Fact]
    public void Rule06_a_large_reference_is_answered_by_the_cell_it_is_filed_under()
    {
        var ring1 = _w.Read.Exterior(W, 0, 0, 1, includePersistent: false);
        var r9 = Win(ring1, WorldReadWorld.R9);
        Assert.True(r9 is not null && r9.Cell == WorldReadWorld.E10 && r9.Ref!.Base == WorldReadWorld.Big,
            $"[rule 6] R9 (a large reference anchored in E10) must be returned when the ring covers E10, with its anchor cell E10 named; got {(r9 is null ? "nothing" : r9.Cell.ToString())} among [{Names(ring1.Winners)}]");
        var ring0 = E00;
        Assert.True(Win(ring0, WorldReadWorld.R9) is null && !ring0.MovedOut.Any(x => x.Reference == WorldReadWorld.R9),
            "[rule 6] R9 must be absent from grid (0,0) with ring 0");
    }

    [Fact]
    public void Rule07_an_enable_parent_in_another_cell_and_plugin_is_found_under_its_winner()
    {
        var r10 = Win(E00, WorldReadWorld.R10);
        Assert.True(r10 is { Plugin: 1 } && r10.Ref!.EnableParent == WorldReadWorld.P1, "[rule 7] R10 must stand in E00 under A's record naming P1 as its enable parent");
        var p1 = _w.Read.Lookup(WorldReadWorld.P1);
        Assert.True(p1 is { Plugin: 2, Deleted: false } && p1.Cell == WorldReadWorld.I1 && p1.Ref!.Position.X == 5f,
            $"[rule 7] Lookup(P1) must give B's record in interior I1 at x 5; got {(p1 is null ? "nothing" : $"p{p1.Plugin} {p1.Cell} x {p1.Ref?.Position.X}")}");
        Assert.True(_w.Read.Lookup(new FormKey(WorldReadWorld.MKey, 0xEEE)) is null, "[rule 7] Lookup of a reference no plugin holds must be null");
    }

    [Fact]
    public void Rule08_base_facts_come_from_the_last_plugin_defining_the_base()
    {
        var s2 = _w.Read.Base(WorldReadWorld.S2);
        Assert.True(s2 is { Plugin: 1, PluginName: "WrA.esp", Model: "meshes\\wr\\s2a.nif", IsMarker: true, TypeName: "Static", EditorId: "WrS2" }
                    && s2.Obnd == new Vec3(80, 80, 40),
            $"[rule 8] S2 is overridden by A: its facts must be A's (s2a.nif, marker, OBND 80x80x40); got {s2}");
        var s = _w.Read.Base(WorldReadWorld.S);
        Assert.True(s is { Plugin: 0, Model: "meshes\\wr\\s.nif", IsMarker: false }, $"[rule 8] S must give the master's facts; got {s}");
        var l = _w.Read.Base(WorldReadWorld.L);
        Assert.True(l is { Plugin: 2, LightCanBeCarried: false, TypeName: "Light" }, $"[rule 8] B clears L's Can Be Carried flag: L must read not carried, from B; got {l}");
        Assert.True(_w.Read.Base(new FormKey(WorldReadWorld.MKey, 0xFFF)) is null, "[rule 8] a base no plugin holds must be not found (null)");
    }

    // ---- rules 9 to 11: the prepared file never answers stale, half-written or from another reader ----

    static SkyrimMod OnePlugin(FormKey r, float x)
    {
        var mk = new ModKey("WrOne", ModType.Master);
        var m = new SkyrimMod(mk, WrPlugins.Rel);
        var cell = WrPlugins.Int(new FormKey(mk, 0x850)); cell.Temporary.Add(WrPlugins.Ref(r, null, x, 0));
        WrPlugins.FileInterior(m, cell);
        return m;
    }
    static readonly FormKey One = new(new ModKey("WrOne", ModType.Master), 0x901);
    static string Sha(string path) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))).ToLowerInvariant();

    [Fact]
    public void Rule09_a_changed_plugin_with_the_same_name_size_and_time_is_read_anew()
    {
        string plugins = Path.Combine(_dir, "plugins"), cache = Path.Combine(_dir, "cache");
        var path = WrPlugins.Save(plugins, OnePlugin(One, 100));
        var first = WorldRead.Open(new[] { path }, cache);
        Assert.True(first.Lookup(One)?.Ref?.Position.X == 100f, "[rule 9] the first read must see x 100");
        var len = new FileInfo(path).Length; var time = File.GetLastWriteTimeUtc(path);
        WrPlugins.Save(plugins, OnePlugin(One, 900));
        File.SetLastWriteTimeUtc(path, time);
        Assert.True(new FileInfo(path).Length == len && File.GetLastWriteTimeUtc(path) == time, "[rule 9] precondition: name, size and time unchanged");
        var second = WorldRead.Open(new[] { path }, cache);
        var x = second.Lookup(One)?.Ref?.Position.X;
        Assert.True(x == 900f, $"[rule 9] the plugin's content changed under the same name, size and time: the read must give the new x 900, got {x?.ToString() ?? "nothing"}");
        Assert.True(second.Prepared == 1, $"[rule 9] the changed plugin must be prepared again; prepared {second.Prepared}");
    }

    [Fact]
    public void Rule10_a_half_written_prepared_file_is_rebuilt_never_read()
    {
        string plugins = Path.Combine(_dir, "plugins"), cache = Path.Combine(_dir, "cache");
        var path = WrPlugins.Save(plugins, OnePlugin(One, 100));
        var first = WorldRead.Open(new[] { path }, cache);
        Assert.True(first.Prepared == 1, $"[rule 10] precondition: the first open prepares the plugin; prepared {first.Prepared}");
        var prepared = WorldReadFile.PathFor(cache, Sha(path), Path.GetFileName(path));
        var whole = File.ReadAllBytes(prepared);
        foreach (int cut in new[] { 1, 40, whole.Length / 3, whole.Length / 2, whole.Length - 9, whole.Length - 1 })
        {
            File.WriteAllBytes(prepared, whole[..cut]);
            var again = WorldRead.Open(new[] { path }, cache);
            Assert.True(again.Prepared == 1 && again.Reused == 0,
                $"[rule 10] a prepared file cut to {cut} of {whole.Length} bytes must be rebuilt, never read; prepared {again.Prepared}, reused {again.Reused}");
            Assert.True(again.Lookup(One)?.Ref?.Position.X == 100f, $"[rule 10] after rebuilding a file cut to {cut} bytes the read must give x 100");
        }
        File.Delete(prepared);
        File.WriteAllBytes(prepared + ".partial-leftover", whole[..(whole.Length / 2)]);
        var afterLeftover = WorldRead.Open(new[] { path }, cache);
        Assert.True(afterLeftover.Prepared == 1 && afterLeftover.Lookup(One)?.Ref?.Position.X == 100f,
            $"[rule 10] a leftover temporary file must not be read: the plugin must be prepared; prepared {afterLeftover.Prepared}");
    }

    [Fact]
    public void Rule11_a_prepared_file_from_another_schema_or_engine_build_is_rebuilt()
    {
        string cache = Path.Combine(_dir, "cache");
        var oldPath = WrPlugins.Save(Path.Combine(_dir, "old"), OnePlugin(One, 100));
        var path = WrPlugins.Save(Path.Combine(_dir, "plugins"), OnePlugin(One, 900));
        string sha = Sha(path), name = Path.GetFileName(path);
        Directory.CreateDirectory(cache);
        foreach (var (label, schema, engine) in new[] { ("another schema", WorldRead.Schema + 1, WorldRead.EngineVersion),
                                                       ("another Engine build", WorldRead.Schema, Guid.NewGuid()) })
        {
            // A file holding the OLD content but stating this plugin's hash: only its schema or Engine build gives it away.
            WorldReadFile.WriteFor(oldPath, sha, WorldReadFile.PathFor(cache, sha, name), schema, engine);
            var r = WorldRead.Open(new[] { path }, cache);
            var x = r.Lookup(One)?.Ref?.Position.X;
            Assert.True(r.Prepared == 1 && x == 900f,
                $"[rule 11] a prepared file made under {label} must be rebuilt, not read: prepared {r.Prepared}, x {x?.ToString() ?? "nothing"} (900 expected)");
        }
    }

    // ---- rule 12: an independent whole-order replay over generated orders ----

    static readonly FormKey GW = new(new ModKey("WrG0", ModType.Master), 0x830), GT = new(new ModKey("WrG0", ModType.Master), 0x831);
    static FormKey GCell(int gx, int gy) => new(new ModKey("WrG0", ModType.Master), (uint)(0x840 + gx * 3 + gy));
    static readonly FormKey[] GInteriors = { new(new ModKey("WrG0", ModType.Master), 0x850), new(new ModKey("WrG0", ModType.Master), 0x851) };

    /// <summary>A seeded order of 3 to 6 small plugins: moves, overrides, rotation-only overrides, deletions, introductions,
    /// persistent and temporary records, a 3x3 exterior and two interiors.</summary>
    static IReadOnlyList<string> Generate(string dir, int seed)
    {
        var rng = new Random(seed);
        int n = rng.Next(3, 7);
        var mods = new List<SkyrimMod>();
        var paths = new List<string>();
        var known = new List<FormKey>();
        var baseKey = new FormKey(new ModKey("WrG0", ModType.Master), 0x801);
        for (int i = 0; i < n; i++)
        {
            var mk = new ModKey($"WrG{i}", i == 0 ? ModType.Master : ModType.Plugin);
            var mod = new SkyrimMod(mk, WrPlugins.Rel);
            var grid = new Dictionary<(int, int), SkCell>();
            var interiors = new Dictionary<FormKey, SkCell>();
            SkCell? top = null;
            if (i == 0)
            {
                mod.Statics.Add(new Static(baseKey, WrPlugins.Rel) { EditorID = "WrGS", Model = new Model { File = "meshes\\g\\s.nif" } });
                top = WrPlugins.Top(GT);
                for (int gx = 0; gx < 3; gx++) for (int gy = 0; gy < 3; gy++) grid[(gx, gy)] = WrPlugins.Ext(GCell(gx, gy), gx, gy);
                foreach (var ik in GInteriors) interiors[ik] = WrPlugins.Int(ik);
            }
            void Put(IPlaced rec, int where, bool persistentInGrid)
            {
                if (where < 9)
                {
                    var g = (where / 3, where % 3);
                    if (!grid.TryGetValue(g, out var c)) grid[g] = c = WrPlugins.Ext(GCell(g.Item1, g.Item2), g.Item1, g.Item2);
                    if (persistentInGrid) c.Persistent.Add(rec); else c.Temporary.Add(rec);
                }
                else if (where == 9) { top ??= WrPlugins.Top(GT); top.Persistent.Add(rec); }
                else
                {
                    var ik = GInteriors[where - 10];
                    if (!interiors.TryGetValue(ik, out var c)) interiors[ik] = c = WrPlugins.Int(ik);
                    c.Temporary.Add(rec);
                }
            }
            PlacedObject Placed(FormKey key, int where)
            {
                float x, y;
                if (where < 9) { x = (where / 3) * 4096 + rng.Next(1, 4095); y = (where % 3) * 4096 + rng.Next(1, 4095); }
                else if (where == 9) { x = rng.Next(-2000, 4 * 4096); y = rng.Next(0, 3 * 4096); }
                else { x = rng.Next(-500, 500); y = rng.Next(-500, 500); }
                var r = WrPlugins.Ref(key, baseKey, x, y, rotZ: rng.Next(0, 4) * 0.5f);
                if (rng.Next(5) == 0) r.EditorID = $"WrG{i}Ref{key.ID:X}";
                return r;
            }
            if (i > 0)
                foreach (var key in known)
                {
                    double roll = rng.NextDouble();
                    int where = rng.Next(0, 12);
                    if (roll < 0.5) continue;
                    if (roll < 0.85) Put(Placed(key, where), where, rng.Next(10) == 0);
                    else Put(WrPlugins.Deleted(key), where, rng.Next(10) == 0);
                }
            int fresh = i == 0 ? 30 : 4;
            for (int k = 0; k < fresh; k++)
            {
                var key = new FormKey(mk, (uint)(0x900 + k));
                int where = rng.Next(0, 12);
                Put(Placed(key, where), where, rng.Next(10) == 0);
                known.Add(key);
            }
            if (top is not null || grid.Count > 0) mod.Worldspaces.Add(WrPlugins.World(GW, top, grid.Values.ToArray()));
            foreach (var c in interiors.Values) WrPlugins.FileInterior(mod, c);
            paths.Add(WrPlugins.Save(dir, mod, mods.ToArray()));
            mods.Add(mod);
        }
        return paths;
    }

    [Fact]
    public void Rule12_every_cell_equals_an_independent_whole_order_replay()
    {
        foreach (int seed in new[] { 1, 2, 3, 4, 5, 6 })
        {
            var paths = Generate(Path.Combine(_dir, $"g{seed}"), seed);
            var read = WorldRead.Open(paths, Path.Combine(_dir, $"g{seed}-cache"));
            var oracle = WorldReadOracle.Replay(paths);
            int views = 0;
            void Check(string view, WorldAnswer got, IEnumerable<FormKey> candidates, Func<WorldReadOracle.Sighting, bool> inArea)
            {
                var want = oracle.Expected(candidates, inArea);
                var have = WorldReadOracle.Lines(got);
                views++;
                if (want.SequenceEqual(have)) return;
                var missing = want.Except(have).Take(3); var extra = have.Except(want).Take(3);
                Assert.Fail($"[rule 12] seed {seed} ({paths.Count} plugins), {view}: the reader differs from the whole-order replay; expected but missing: [{string.Join(" || ", missing)}]; given but not expected: [{string.Join(" || ", extra)}]; {want.Count} expected lines, {have.Count} given");
            }
            foreach (var cell in oracle.ByCell.Keys)
                Check($"cell {cell}", read.Cell(cell), oracle.ByCell[cell], s => s.Cell == cell);
            for (int gx = 0; gx < 3; gx++)
                for (int gy = 0; gy < 3; gy++)
                {
                    var g = GCell(gx, gy); var (x0, y0) = (gx, gy);
                    var cands = (oracle.ByCell.TryGetValue(g, out var a) ? a : new HashSet<FormKey>()).Concat(oracle.ByCell.TryGetValue(GT, out var t) ? t : new HashSet<FormKey>());
                    Check($"grid ({gx},{gy}) with the persistent cell", read.Exterior(GW, gx, gy, 0, includePersistent: true), cands,
                          s => s.Cell == g || (s.Cell == GT && s.Ref is { } r && WorldReadOracle.GridOf(r.Position) == (x0, y0)));
                }
            var all9 = Enumerable.Range(0, 9).Select(k => GCell(k / 3, k % 3)).ToHashSet();
            Check("grid (1,1) ring 1", read.Exterior(GW, 1, 1, 1, includePersistent: false),
                  all9.SelectMany(c => oracle.ByCell.TryGetValue(c, out var s) ? s : new HashSet<FormKey>()), s => all9.Contains(s.Cell));
            Assert.True(views > 10, $"[rule 12] seed {seed}: only {views} views were compared");
            foreach (var key in oracle.Sightings.Keys)
            {
                var w = oracle.Sightings[key][^1];
                var l = read.Lookup(key);
                Assert.True(l is not null && l.Plugin == w.Plugin && l.Cell == w.Cell && l.Deleted == w.Deleted && WorldReadOracle.Canon(l.Ref) == WorldReadOracle.Canon(w.Ref),
                    $"[rule 12] seed {seed}: Lookup({key}) differs from the replay's last sighting p{w.Plugin} {w.Cell}");
            }
        }
    }

    [Fact]
    public void Rule13_a_second_open_on_the_same_folder_prepares_nothing()
    {
        Assert.True(_w.Read.Prepared == 4 && _w.Read.Reused == 0, $"[rule 13] the first open must prepare all 4 plugins; prepared {_w.Read.Prepared}, reused {_w.Read.Reused}");
        var again = WorldRead.Open(_w.Paths, _w.Cache);
        Assert.True(again.Prepared == 0 && again.Reused == 4, $"[rule 13] a second open on the same folder must prepare nothing and reuse 4; prepared {again.Prepared}, reused {again.Reused}");
        Assert.True(again.Identity == _w.Read.Identity && again.Identity.Contains("WrC.esp") && again.Identity.Contains($"schema {WorldRead.Schema}"),
            $"[rule 13] the identity must be the same on both opens and name the plugins and schema; got '{again.Identity}'");
    }

    // ---- rules 14 and 15: the recovery paths ----

    static readonly FormKey OneCell = new(new ModKey("WrOne", ModType.Master), 0x850);

    [Fact]
    public void Rule14_a_cell_damaged_on_disk_after_open_is_rebuilt_once_and_answered()
    {
        string cache = Path.Combine(_dir, "cache");
        var path = WrPlugins.Save(Path.Combine(_dir, "plugins"), OnePlugin(One, 100));
        var read = WorldRead.Open(new[] { path }, cache);
        var prepared = WorldReadFile.PathFor(cache, Sha(path), Path.GetFileName(path));
        var bytes = File.ReadAllBytes(prepared);
        bytes[WorldReadFile.HeaderLength + 12] ^= 0xFF;   // inside the first (only) cell's data, after the open loaded the tables
        File.WriteAllBytes(prepared, bytes);
        WorldAnswer? a = null; Exception? err = null;
        try { a = read.Cell(OneCell); } catch (Exception e) { err = e; }
        var x = a?.Winners.SingleOrDefault(w => w.Reference == One)?.Ref?.Position.X;
        Assert.True(err is null && x == 100f,
            $"[rule 14] a cell damaged on disk after the open must be answered from a rebuilt file: x 100 expected, got {x?.ToString() ?? "nothing"}{(err is null ? "" : $", threw {err.GetType().Name}: {err.Message}")}");
        Assert.True(read.Rebuilt == 1, $"[rule 14] the damaged file must be rebuilt once; rebuilt {read.Rebuilt}");
        read.Cell(OneCell);
        Assert.True(read.Rebuilt == 1, $"[rule 14] a second read of the cell must not rebuild again; rebuilt {read.Rebuilt}");
        var later = WorldRead.Open(new[] { path }, cache);
        Assert.True(later.Prepared == 0 && later.Reused == 1 && later.Lookup(One)?.Ref?.Position.X == 100f,
            $"[rule 14] a later open must reuse the rebuilt file; prepared {later.Prepared}, reused {later.Reused}");
    }

    [Fact]
    public void Rule15_a_prepared_file_held_open_by_another_reader_leaves_no_temporary_file()
    {
        foreach (var (label, share) in new[] { ("the reader's own sharing, read and delete", FileShare.Read | FileShare.Delete),
                                               ("read sharing only", FileShare.Read) })
        {
            string dir = Path.Combine(_dir, Guid.NewGuid().ToString("N")), cache = Path.Combine(dir, "cache");
            var path = WrPlugins.Save(Path.Combine(dir, "plugins"), OnePlugin(One, 100));
            WorldRead.Open(new[] { path }, cache);
            var prepared = WorldReadFile.PathFor(cache, Sha(path), Path.GetFileName(path));
            var whole = File.ReadAllBytes(prepared);
            File.WriteAllBytes(prepared, whole[..^1]);   // cut, so the next open rebuilds it while the old one is held
            WorldRead r;
            using (new FileStream(prepared, FileMode.Open, FileAccess.Read, share))
            {
                r = WorldRead.Open(new[] { path }, cache);
                var x = r.Lookup(One)?.Ref?.Position.X;
                Assert.True(r.Prepared == 1 && x == 100f,
                    $"[rule 15] with the old prepared file held open ({label}) the read must answer x 100 from a rebuilt file; prepared {r.Prepared}, x {x?.ToString() ?? "nothing"}");
            }
            var left = Directory.GetFiles(cache, "*.partial-*");
            Assert.True(left.Length == 0,
                $"[rule 15] with the old prepared file held open ({label}) no temporary file may be left in the cache folder; found {string.Join(", ", left.Select(Path.GetFileName))}");
            Assert.True(r.Lookup(One)?.Ref?.Position.X == 100f, $"[rule 15] ({label}) the read must keep answering after its temporary file is gone");
        }
    }
}
