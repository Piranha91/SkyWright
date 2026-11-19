using System.Diagnostics;
using System.Globalization;
using HousecarlCore;
using Mutagen.Bethesda;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Plugins.Records;
using Mutagen.Bethesda.Skyrim;
using Noggog;
using Workbench;
using Xunit;
using Xunit.Abstractions;

namespace WorkbenchTests;

/// <summary>A three-plugin load order written with Mutagen into a temp folder: a master with two interior cells and a
/// worldspace (a persistent cell and two grid cells), a plugin that moves a reference to the other interior cell and adds
/// an actor, and a plugin that deletes a reference and overrides the worldspace without its persistent cell.</summary>
public sealed class WorldTreeWorld : IDisposable
{
    static readonly SkyrimRelease Rel = SkyrimRelease.SkyrimSE;
    internal static readonly ModKey Base = new("HcWiBase", ModType.Master), Move = new("HcWiMove", ModType.Plugin), Gone = new("HcWiGone", ModType.Plugin);

    internal static readonly FormKey Rock = new(Base, 0x801), Chest = new(Base, 0x802), Guard = new(Base, 0x803);
    // 0x8A5 is 2213 (block 3, sub-block 1); 0x8B0 is 2224 (block 4, sub-block 2).
    internal static readonly FormKey Cellar = new(Base, 0x8A5), Hall = new(Base, 0x8B0);
    internal static readonly FormKey RefPersistent = new(Base, 0x811), RefMoved = new(Base, 0x812), RefDeleted = new(Base, 0x813),
        RefStays = new(Base, 0x814), ActorAdded = new(Move, 0x800);
    internal static readonly FormKey Land = new(Base, 0x900), EmptyLand = new(Base, 0x950), LandPersistent = new(Base, 0x901),
        Wild01 = new(Base, 0x902), Wild02 = new(Base, 0x903);
    internal static readonly FormKey RefInWorld = new(Base, 0x911), RefNowhere = new(Base, 0x912), RefWild = new(Base, 0x913),
        NavWild = new(Base, 0x914), TerrainWild02 = new(Base, 0x915), RefAddedWild = new(Gone, 0x800);

    public string Root { get; }
    internal string[] Paths { get; }

    public WorldTreeWorld()
    {
        Root = Path.Combine(Path.GetTempPath(), "hc-wb-worldindex-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Root);
        string P(ModKey k) => Path.Combine(Root, k.FileName);

        var m = new SkyrimMod(Base, Rel);
        m.Statics.Add(new Static(Rock, Rel) { EditorID = "HcWiRock" });
        m.Containers.Add(new Container(Chest, Rel) { EditorID = "HcWiChest" });
        m.Npcs.Add(new Npc(Guard, Rel) { EditorID = "HcWiGuard" });
        var cellar = Interior(Cellar, "HcWiCellar", "Old Cellar");
        var disabled = Ref(RefPersistent, Rock, new P3Float(10, 20, 30), "HcWiPersistentRock");
        disabled.SkyrimMajorRecordFlags |= SkyrimMajorRecord.SkyrimMajorRecordFlag.InitiallyDisabled;
        cellar.Persistent.Add(disabled);
        cellar.Temporary.Add(Ref(RefMoved, Rock, new P3Float(100, 0, 0), "HcWiMovedRock"));
        cellar.Temporary.Add(Ref(RefDeleted, Chest, new P3Float(200, 0, 0), "HcWiDeletedChest"));
        cellar.Temporary.Add(Ref(RefStays, Rock, new P3Float(300, 0, 0), "HcWiStayingRock"));
        FileInterior(m, cellar);
        FileInterior(m, Interior(Hall, "HcWiHall", "Great Hall"));
        var land = new Worldspace(Land, Rel) { EditorID = "HcWiLand", Name = "Test Land" };
        land.TopCell = new Cell(LandPersistent, Rel) { EditorID = "HcWiLandPersistent" };
        land.TopCell.Persistent.Add(Ref(RefInWorld, Rock, new P3Float(-5000, 9000, 10), "HcWiWorldRock"));     // grid -2,2
        land.TopCell.Persistent.Add(Ref(RefNowhere, Rock, new P3Float(40000, 40000, 0), "HcWiFarRock"));      // grid 9,9: no cell
        var wild01 = Exterior(Wild01, "HcWiWild01", -2, 2);
        wild01.Temporary.Add(Ref(RefWild, Rock, new P3Float(-4200, 8300, 0), null));
        wild01.NavigationMeshes.Add(new NavigationMesh(NavWild, Rel)
        {
            Data = new NavigationMeshData { Parent = new WorldspaceNavmeshParent { Parent = Land.ToLink<IWorldspaceGetter>(), Coordinates = new P2Int16(-2, 2) } },
        });
        var wild02 = Exterior(Wild02, "HcWiWild02", 33, -9);
        wild02.Landscape = new Landscape(TerrainWild02, Rel);
        FileExterior(land, wild01); FileExterior(land, wild02);
        m.Worldspaces.Add(land);
        m.Worldspaces.Add(new Worldspace(EmptyLand, Rel) { EditorID = "HcWiEmptyLand" });
        m.BeginWrite.ToPath(P(Base)).WithLoadOrder(Array.Empty<ISkyrimModGetter>()).Write();

        // The mover files the master's reference under the other interior cell, and adds an actor to the first.
        var mv = new SkyrimMod(Move, Rel);
        var hall = Interior(Hall, "HcWiHall", "Great Hall");
        hall.Temporary.Add(Ref(RefMoved, Rock, new P3Float(1, 2, 3), "HcWiMovedRock"));
        FileInterior(mv, hall);
        var cellarMv = Interior(Cellar, "HcWiCellar", "Old Cellar");
        var actor = new PlacedNpc(ActorAdded, Rel) { EditorID = "HcWiAddedGuard", Placement = new Placement { Position = new P3Float(50, 50, 0) } };
        actor.Base.SetTo(Guard);
        cellarMv.Temporary.Add(actor);
        FileInterior(mv, cellarMv);
        mv.BeginWrite.ToPath(P(Move)).WithLoadOrder(new ISkyrimModGetter[] { m }).Write();

        // The deleter deletes a reference, and overrides the worldspace with one grid cell and no persistent cell.
        var gn = new SkyrimMod(Gone, Rel);
        var cellarGn = Interior(Cellar, "HcWiCellar", "Old Cellar");
        cellarGn.Temporary.Add(new PlacedObject(RefDeleted, Rel) { IsDeleted = true });
        FileInterior(gn, cellarGn);
        var landGn = new Worldspace(Land, Rel) { EditorID = "HcWiLand", Name = "Test Land" };
        var wild01Gn = Exterior(Wild01, "HcWiWild01", -2, 2);
        wild01Gn.Temporary.Add(Ref(RefAddedWild, Chest, new P3Float(-4100, 8400, 0), "HcWiAddedChest"));
        FileExterior(landGn, wild01Gn);
        gn.Worldspaces.Add(landGn);
        gn.BeginWrite.ToPath(P(Gone)).WithLoadOrder(new ISkyrimModGetter[] { m }).Write();

        Paths = new[] { P(Base), P(Move), P(Gone) };
    }

    internal static string Key(FormKey fk) => $"{fk.ID:X6}:{fk.ModKey.FileName}";

    static Cell Interior(FormKey fk, string editorId, string name) =>
        new(fk, Rel) { EditorID = editorId, Name = name, Flags = Cell.Flag.IsInteriorCell };

    static Cell Exterior(FormKey fk, string editorId, int x, int y) =>
        new(fk, Rel) { EditorID = editorId, Grid = new CellGrid { Point = new P2Int(x, y) } };

    static PlacedObject Ref(FormKey fk, FormKey baseKey, P3Float at, string? editorId)
    {
        var r = new PlacedObject(fk, Rel) { EditorID = editorId, Placement = new Placement { Position = at } };
        r.Base.SetTo(baseKey);
        return r;
    }

    static void FileInterior(SkyrimMod mod, Cell cell)
    {
        int blockN = (int)(cell.FormKey.ID % 10), subN = (int)(cell.FormKey.ID / 10 % 10);
        var block = mod.Cells.Records.FirstOrDefault(b => b.BlockNumber == blockN);
        if (block is null) { block = new CellBlock { BlockNumber = blockN, GroupType = GroupTypeEnum.InteriorCellBlock }; mod.Cells.Records.Add(block); }
        var sub = block.SubBlocks.FirstOrDefault(s => s.BlockNumber == subN);
        if (sub is null) { sub = new CellSubBlock { BlockNumber = subN, GroupType = GroupTypeEnum.InteriorCellSubBlock }; block.SubBlocks.Add(sub); }
        sub.Cells.Add(cell);
    }

    static void FileExterior(Worldspace ws, Cell cell)
    {
        var at = cell.Grid!.Point;
        short bx = (short)Math.Floor(at.X / 32.0), by = (short)Math.Floor(at.Y / 32.0), sx = (short)Math.Floor(at.X / 8.0), sy = (short)Math.Floor(at.Y / 8.0);
        var block = ws.SubCells.FirstOrDefault(b => b.BlockNumberX == bx && b.BlockNumberY == by);
        if (block is null) { block = new WorldspaceBlock { BlockNumberX = bx, BlockNumberY = by, GroupType = GroupTypeEnum.ExteriorCellBlock }; ws.SubCells.Add(block); }
        var sub = block.Items.FirstOrDefault(s => s.BlockNumberX == sx && s.BlockNumberY == sy);
        if (sub is null) { sub = new WorldspaceSubBlock { BlockNumberX = sx, BlockNumberY = sy, GroupType = GroupTypeEnum.ExteriorCellSubBlock }; block.Items.Add(sub); }
        sub.Items.Add(cell);
    }

    public void Dispose() { try { Directory.Delete(Root, true); } catch { /* best effort */ } }
}

/// <summary>The world and one index built over it, shared by the tests that only read.</summary>
public sealed class WorldTreeIndex : IDisposable
{
    public WorldTreeWorld World { get; } = new();
    internal WorldIndex Index { get; }
    public WorldTreeIndex() => Index = WorldIndex.BuildAsync(World.Paths, null, CancellationToken.None, TestCorpus.Path).GetAwaiter().GetResult();
    public void Dispose() { Index.Dispose(); World.Dispose(); }
}

/// <summary>The World tree's data over a synthetic load order: winners, last filing, the persistent and temporary split,
/// blocks, search and locate.</summary>
[Trait("tier", "unit")]
public class WorldIndexTests : IClassFixture<WorldTreeIndex>
{
    readonly WorldIndex _i;
    public WorldIndexTests(WorldTreeIndex f) => _i = f.Index;

    static string K(FormKey fk) => WorldTreeWorld.Key(fk);
    Task<CellObjects> Objects(FormKey cell) => _i.ObjectsOfAsync(K(cell), CancellationToken.None);

    [Fact]
    public void The_plugins_are_listed_in_load_order()
        => Assert.Equal(new[] { "HcWiBase.esm", "HcWiMove.esp", "HcWiGone.esp" }, _i.Plugins);

    [Fact]
    public void Worldspaces_are_winners_by_cell_count_and_keep_a_persistent_cell_the_winner_does_not_carry()
    {
        Assert.Equal(new[] { "HcWiLand", "HcWiEmptyLand" }, _i.Worldspaces.Select(w => w.EditorId));
        var land = _i.Worldspaces[0];
        Assert.Equal("Test Land", land.Name);
        Assert.Equal("HcWiGone.esp", land.Winner);
        Assert.Equal(K(WorldTreeWorld.LandPersistent), land.PersistentCell);
        Assert.Equal(2, land.CellCount);
        Assert.Equal(0, _i.Worldspaces[1].CellCount);
        Assert.Null(_i.Worldspaces[1].PersistentCell);
    }

    [Fact]
    public void Every_cell_is_listed_once_under_interiors_a_worldspace_or_as_a_persistent_cell()
    {
        Assert.Equal(5, _i.Cells.Count);
        Assert.Equal(new[] { "HcWiCellar", "HcWiHall" }, _i.Interiors.Select(c => c.EditorId));
        Assert.Equal(new[] { "HcWiWild01", "HcWiWild02" }, _i.CellsOf(K(WorldTreeWorld.Land)).Select(c => c.EditorId));
        Assert.Equal(_i.Cells.Count, _i.Interiors.Count + _i.Worldspaces.Sum(w => _i.CellsOf(w.Key).Count) + _i.Cells.Count(c => c.IsPersistentCell));
        var persistent = Assert.Single(_i.Cells, c => c.IsPersistentCell);
        Assert.Equal("HcWiLandPersistent", persistent.EditorId);
        Assert.Equal(K(WorldTreeWorld.Land), persistent.Worldspace);
        Assert.Null(persistent.GridX);
    }

    [Fact]
    public void An_exterior_cell_carries_its_grid_and_block_numbers_a_negative_grid_included()
    {
        var cells = _i.CellsOf(K(WorldTreeWorld.Land));
        var a = cells[0];
        Assert.False(a.Interior);
        Assert.Equal((-2, 2), (a.GridX, a.GridY));
        Assert.Equal((-1, 0, -1, 0), (a.BlockA, a.BlockB, a.SubBlockA, a.SubBlockB));
        Assert.Equal("HcWiGone.esp", a.Winner);
        var b = cells[1];
        Assert.Equal((33, -9), (b.GridX, b.GridY));
        Assert.Equal((1, -1, 4, -2), (b.BlockA, b.BlockB, b.SubBlockA, b.SubBlockB));
        Assert.Equal("HcWiBase.esm", b.Winner);
    }

    [Fact]
    public void An_interior_cell_carries_its_name_and_the_block_numbers_of_its_FormID()
    {
        var cellar = _i.Interiors[0];
        Assert.True(cellar.Interior);
        Assert.Equal("Old Cellar", cellar.Name);
        Assert.Null(cellar.Worldspace);
        Assert.Equal((3, 0, 1, 0), (cellar.BlockA, cellar.BlockB, cellar.SubBlockA, cellar.SubBlockB));
        Assert.Equal("HcWiGone.esp", cellar.Winner);
        var hall = _i.Interiors[1];
        Assert.Equal((4, 2), (hall.BlockA, hall.SubBlockA));
        Assert.Equal("HcWiMove.esp", hall.Winner);
    }

    [Fact]
    public async Task A_reference_a_later_plugin_moved_is_listed_under_its_new_cell_only()
    {
        var cellar = await Objects(WorldTreeWorld.Cellar);
        Assert.DoesNotContain(cellar.Persistent.Concat(cellar.Temporary), r => r.Key == K(WorldTreeWorld.RefMoved));
        var hall = await Objects(WorldTreeWorld.Hall);
        var moved = Assert.Single(hall.Temporary);
        Assert.Empty(hall.Persistent);
        Assert.Equal(K(WorldTreeWorld.RefMoved), moved.Key);
        Assert.Equal("HcWiMove.esp", moved.Winner);
        Assert.Equal(new[] { 1f, 2f, 3f }, moved.Position);
        Assert.True(hall.HasPlacedOrNavmesh);
    }

    [Fact]
    public async Task A_deleted_reference_stays_listed_flagged_deleted_under_the_deleting_plugin()
    {
        var cellar = await Objects(WorldTreeWorld.Cellar);
        var gone = Assert.Single(cellar.Temporary, r => r.Key == K(WorldTreeWorld.RefDeleted));
        Assert.True(gone.Deleted);
        Assert.Equal("HcWiGone.esp", gone.Winner);
        Assert.Equal("REFR", gone.Type);
        Assert.Null(gone.Position);
    }

    [Fact]
    public async Task Persistent_and_temporary_follow_the_winning_plugins_copy_of_the_cell()
    {
        var cellar = await Objects(WorldTreeWorld.Cellar);
        var persistent = Assert.Single(cellar.Persistent);
        Assert.Equal(K(WorldTreeWorld.RefPersistent), persistent.Key);
        Assert.True(persistent.Persistent);
        Assert.True(persistent.InitiallyDisabled);
        Assert.Equal("HcWiBase.esm", persistent.Winner);
        Assert.Equal(new[] { K(WorldTreeWorld.RefDeleted), K(WorldTreeWorld.RefStays), K(WorldTreeWorld.ActorAdded) }, cellar.Temporary.Select(r => r.Key));
        Assert.All(cellar.Temporary, r => Assert.False(r.Persistent));
        Assert.Empty(cellar.Unfiled);
        Assert.Empty(cellar.Notes);
    }

    [Fact]
    public async Task A_row_names_its_type_and_its_base_in_four_letters()
    {
        var cellar = await Objects(WorldTreeWorld.Cellar);
        var rock = Assert.Single(cellar.Temporary, r => r.Key == K(WorldTreeWorld.RefStays));
        Assert.Equal(("REFR", "HcWiStayingRock", K(WorldTreeWorld.Rock), "HcWiRock", "STAT"), (rock.Type, rock.EditorId, rock.BaseKey, rock.BaseEditorId, rock.BaseType));
        Assert.False(rock.Deleted);
        Assert.False(rock.InitiallyDisabled);
        var actor = Assert.Single(cellar.Temporary, r => r.Key == K(WorldTreeWorld.ActorAdded));
        Assert.Equal(("ACHR", "HcWiGuard", "NPC_", "HcWiMove.esp"), (actor.Type, actor.BaseEditorId, actor.BaseType, actor.Winner));
    }

    [Fact]
    public async Task Navmesh_and_terrain_are_listed_under_temporary_with_their_type()
    {
        var wild01 = await Objects(WorldTreeWorld.Wild01);
        Assert.Equal(new[] { "REFR", "NAVM", "REFR" }, wild01.Temporary.Select(r => r.Type));
        Assert.Equal("CONT", wild01.Temporary[2].BaseType);
        Assert.Equal("HcWiGone.esp", wild01.Temporary[2].Winner);
        Assert.True(wild01.HasPlacedOrNavmesh);
    }

    [Fact]
    public async Task A_cell_with_only_terrain_has_no_placed_object_or_navmesh()
    {
        var wild02 = await Objects(WorldTreeWorld.Wild02);
        var terrain = Assert.Single(wild02.Temporary);
        Assert.Equal("LAND", terrain.Type);
        Assert.Empty(wild02.Persistent);
        Assert.False(wild02.HasPlacedOrNavmesh);
    }

    [Fact]
    public async Task A_persistent_reference_of_a_worldspace_opens_the_grid_cell_it_stands_in()
    {
        var persistent = await Objects(WorldTreeWorld.LandPersistent);
        Assert.Equal(new[] { K(WorldTreeWorld.RefInWorld), K(WorldTreeWorld.RefNowhere) }, persistent.Persistent.Select(r => r.Key));
        Assert.Empty(persistent.Temporary);
        Assert.Equal(K(WorldTreeWorld.Wild01), _i.GridCellAt(K(WorldTreeWorld.Land), -5000, 9000));
        Assert.Null(_i.GridCellAt(K(WorldTreeWorld.Land), 0, 0));
        Assert.Equal(K(WorldTreeWorld.Wild01), _i.OpenableCellOf(K(WorldTreeWorld.RefInWorld)));
        Assert.Null(_i.OpenableCellOf(K(WorldTreeWorld.RefNowhere)));
        Assert.Equal(K(WorldTreeWorld.Cellar), _i.OpenableCellOf(K(WorldTreeWorld.RefStays)));
        Assert.Equal(K(WorldTreeWorld.Hall), _i.OpenableCellOf(K(WorldTreeWorld.RefMoved)));
        Assert.Null(_i.OpenableCellOf(K(WorldTreeWorld.LandPersistent)));
    }

    [Fact]
    public void Search_finds_cells_by_EditorID_part_name_part_FormKey_and_grid()
    {
        Assert.Equal(new[] { "HcWiCellar" }, _i.Search("CELLAR").Select(c => c.EditorId));
        Assert.Equal(new[] { "HcWiHall" }, _i.Search("great h").Select(c => c.EditorId));
        Assert.Equal(new[] { "HcWiCellar" }, _i.Search(K(WorldTreeWorld.Cellar)).Select(c => c.EditorId));
        Assert.Equal(new[] { "HcWiCellar" }, _i.Search(K(WorldTreeWorld.RefStays)).Select(c => c.EditorId));
        Assert.Equal(new[] { "HcWiWild02" }, _i.Search("33,-9").Select(c => c.EditorId));
        Assert.Equal(new[] { "HcWiWild01" }, _i.Search(" -2 , 2 ").Select(c => c.EditorId));
        Assert.Equal(new[] { "HcWiWild01", "HcWiWild02" }, _i.Search("wild").Select(c => c.EditorId));
        Assert.Single(_i.Search("wild", 1));
        Assert.Empty(_i.Search("no such cell"));
        Assert.Empty(_i.Search("7,7"));
    }

    [Fact]
    public void Locate_answers_a_cell_a_placed_object_and_nothing_for_an_unknown_key()
    {
        var cell = _i.Locate(K(WorldTreeWorld.Cellar));
        Assert.NotNull(cell);
        Assert.True(cell!.IsCell);
        Assert.Equal(("CELL", K(WorldTreeWorld.Cellar), (string?)null), (cell.RecordType, cell.Cell, cell.Worldspace));

        var moved = _i.Locate(K(WorldTreeWorld.RefMoved));
        Assert.NotNull(moved);
        Assert.False(moved!.IsCell);
        Assert.Equal(("REFR", K(WorldTreeWorld.Hall)), (moved.RecordType, moved.Cell));
        Assert.Equal(new[] { 1f, 2f, 3f }, moved.Position);

        var inWorld = _i.Locate(K(WorldTreeWorld.RefInWorld));
        Assert.Equal((K(WorldTreeWorld.LandPersistent), K(WorldTreeWorld.Land)), (inWorld!.Cell, inWorld.Worldspace));

        var rock = _i.Locate(K(WorldTreeWorld.Rock));
        Assert.Equal(("STAT", (string?)null), (rock!.RecordType, rock.Cell));

        Assert.Null(_i.Locate("123456:HcWiNoSuch.esp"));
        Assert.Null(_i.Locate("0008FF:HcWiBase.esm"));
        Assert.Null(_i.Locate("not a key"));
    }

    [Fact]
    public async Task Asking_for_a_key_that_is_not_a_cell_is_refused()
    {
        var ex = await Assert.ThrowsAsync<ArgumentException>(() => _i.ObjectsOfAsync(K(WorldTreeWorld.RefStays), CancellationToken.None));
        Assert.Contains("not a cell", ex.Message);
    }

    [Fact]
    public async Task A_cancelled_read_of_a_cell_stops()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => _i.ObjectsOfAsync(K(WorldTreeWorld.Cellar), cts.Token));
    }
}

/// <summary>Building and dropping the index: cancellation, the missing rulebook, and no plugin held open at rest.</summary>
[Trait("tier", "unit")]
public class WorldIndexBuildTests
{
    sealed class CancelOn(CancellationTokenSource cts, string part) : IProgress<string>
    {
        public readonly List<string> Said = new();
        public void Report(string value) { Said.Add(value); if (value.Contains(part, StringComparison.Ordinal)) cts.Cancel(); }
    }

    [Fact]
    public async Task A_build_cancelled_before_it_starts_stops()
    {
        using var w = new WorldTreeWorld();
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => WorldIndex.BuildAsync(w.Paths, null, cts.Token, TestCorpus.Path));
    }

    [Fact]
    public async Task A_build_cancelled_while_it_runs_stops()
    {
        using var w = new WorldTreeWorld();
        using var cts = new CancellationTokenSource();
        var say = new CancelOn(cts, "Indexing");
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => WorldIndex.BuildAsync(w.Paths, say, cts.Token, TestCorpus.Path));
        Assert.DoesNotContain(say.Said, s => s.Contains("ready", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_missing_rulebook_is_refused_by_name()
    {
        using var w = new WorldTreeWorld();
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => WorldIndex.BuildAsync(w.Paths, null, CancellationToken.None, Path.Combine(w.Root, "absent", "corpus.json")));
        Assert.Contains("WB_CORPUS", ex.Message);
    }

    [Fact]
    public async Task An_empty_load_order_is_refused()
    {
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => WorldIndex.BuildAsync(Array.Empty<string>(), null, CancellationToken.None, TestCorpus.Path));
        Assert.Contains("loadorder.txt", ex.Message);
    }

    [Fact]
    public async Task No_plugin_is_held_open_after_a_build_or_a_cell_read_and_a_dropped_index_refuses()
    {
        using var w = new WorldTreeWorld();
        var say = new CancelOn(new CancellationTokenSource(), "never");
        var index = await WorldIndex.BuildAsync(w.Paths, say, CancellationToken.None, TestCorpus.Path);
        Assert.Contains(say.Said, s => s.Contains("ready", StringComparison.Ordinal));
        Assert.True(index.BuildTime > TimeSpan.Zero);
        await index.ObjectsOfAsync(WorldTreeWorld.Key(WorldTreeWorld.Cellar), CancellationToken.None);
        Assert.NotNull(index.Locate(WorldTreeWorld.Key(WorldTreeWorld.RefInWorld)));
        // a file another handle holds open cannot be opened for exclusive use
        foreach (var p in w.Paths)
            using (new FileStream(p, FileMode.Open, FileAccess.ReadWrite, FileShare.None)) { }
        index.Dispose();
        Assert.Empty(index.Cells);
        await Assert.ThrowsAsync<ObjectDisposedException>(() => index.ObjectsOfAsync(WorldTreeWorld.Key(WorldTreeWorld.Cellar), CancellationToken.None));
        Assert.Throws<ObjectDisposedException>(() => index.Search("cellar"));
    }
}

/// <summary>The index on a real load order. WB_WORLD_INSTANCE, WB_WORLD_PROFILE and WB_WORLD_GAME name an MO2 instance
/// folder, one of its profiles and the game folder; the test only reads them. Without them it fails saying so. The
/// measured times are written to the test output, and to the file WB_WORLD_REPORT names when it is set.</summary>
[Trait("tier", "real-run")]
public sealed class WorldIndexRealRunTests
{
    readonly ITestOutputHelper _out;
    readonly List<string> _lines = new();
    public WorldIndexRealRunTests(ITestOutputHelper o) => _out = o;

    static string Env(string name) => Environment.GetEnvironmentVariable(name) is { Length: > 0 } v ? v
        : throw new Xunit.Sdk.XunitException("set WB_WORLD_INSTANCE (an MO2 instance folder), WB_WORLD_PROFILE (a profile name) and WB_WORLD_GAME (the game folder) to build the World index on a real load order");

    void Say(FormattableString line)
    {
        var text = line.ToString(CultureInfo.InvariantCulture);
        _lines.Add(text); _out.WriteLine(text);
    }

    [Fact]
    public async Task The_index_of_a_real_load_order_holds_its_invariants()
    {
        string instance = Env("WB_WORLD_INSTANCE"), profile = Env("WB_WORLD_PROFILE"), game = Env("WB_WORLD_GAME");
        var order = Mo2LoadOrder.Build(Path.Combine(instance, "profiles", profile), Path.Combine(instance, "mods"),
                                       Path.Combine(game, "Data"), Path.Combine(instance, "overwrite"));
        Assert.NotEmpty(order.OrderedPaths);
        var corpus = Environment.GetEnvironmentVariable("WB_CORPUS") is { Length: > 0 } c ? c : TestCorpus.Path;

        long heapBefore = GC.GetTotalMemory(true);
        using var index = await WorldIndex.BuildAsync(order.OrderedPaths, null, CancellationToken.None, corpus);
        Say($"plugins {index.Plugins.Count}; build {index.BuildTime.TotalSeconds:0.00} s, of which the cell pass {index.CellPassTime.TotalSeconds:0.00} s; working set grew {index.BuildWorkingSetDeltaBytes / 1048576.0:0} MiB; managed heap kept after a collection {(GC.GetTotalMemory(true) - heapBefore) / 1048576.0:0} MiB");
        int persistent = index.Cells.Count(x => x.IsPersistentCell);
        int gridCells = index.Worldspaces.Sum(w => index.CellsOf(w.Key).Count);
        Say($"worldspaces {index.Worldspaces.Count}; interior cells {index.Interiors.Count}; exterior grid cells {gridCells}; persistent cells {persistent}; notes {index.Notes.Count}");
        foreach (var note in index.Notes.Take(5)) Say($"note: {note}");
        foreach (var w in index.Worldspaces.Take(3)) Say($"worldspace {w.EditorId}: {w.CellCount} grid cells, winner {w.Winner}");

        // every row's winner is a plugin of the order, and every cell is listed in exactly one place
        var plugins = index.Plugins.ToHashSet(StringComparer.OrdinalIgnoreCase);
        Assert.All(index.Cells, row => Assert.Contains(row.Winner, plugins));
        Assert.Equal(index.Cells.Count, index.Interiors.Count + gridCells + persistent);

        // the same files read a second way: each plugin's own block structure, the last plugin to file a cell standing
        var filedUnder = new Dictionary<FormKey, FormKey?>();
        var filedBlocks = new Dictionary<FormKey, (int, int, int, int)>();
        var topCells = new HashSet<FormKey>();
        foreach (var path in order.OrderedPaths)
        {
            if (index.Notes.Any(n => n.Contains(Path.GetFileName(path), StringComparison.OrdinalIgnoreCase))) continue;   // left out of the index
            var mod = LoadOrderResolver.OpenOverlay(path, Path.Combine(game, "Data"));
            try
            {
                foreach (var block in mod.Cells.Records)
                    foreach (var sub in block.SubBlocks)
                        foreach (var cell in sub.Cells)
                        { filedUnder[cell.FormKey] = null; filedBlocks[cell.FormKey] = (block.BlockNumber, 0, sub.BlockNumber, 0); }
                foreach (var w in mod.Worldspaces.Records)
                {
                    if (w.TopCell is { } top) { topCells.Add(top.FormKey); filedUnder[top.FormKey] = w.FormKey; filedBlocks[top.FormKey] = (0, 0, 0, 0); }
                    foreach (var block in w.SubCells)
                        foreach (var sub in block.Items)
                            foreach (var cell in sub.Items)
                            { filedUnder[cell.FormKey] = w.FormKey; filedBlocks[cell.FormKey] = (block.BlockNumberX, block.BlockNumberY, sub.BlockNumberX, sub.BlockNumberY); }
                }
            }
            finally { (mod as IDisposable)?.Dispose(); }
        }
        var rows = index.Cells.ToDictionary(row => FormKey.Factory(row.Key));
        Assert.Equal(filedUnder.Count, rows.Count);
        Assert.All(filedUnder, kv => Assert.Equal(kv.Value, rows[kv.Key].Worldspace is { } wk ? FormKey.Factory(wk) : (FormKey?)null));
        Assert.Equal(topCells.Count, persistent);
        Assert.All(topCells, top => Assert.True(rows[top].IsPersistentCell));
        var offBlock = rows.Where(kv => !kv.Value.IsPersistentCell && (kv.Value.Worldspace is null || kv.Value.GridX is not null)
                                        && filedBlocks[kv.Key] != (kv.Value.BlockA, kv.Value.BlockB, kv.Value.SubBlockA, kv.Value.SubBlockB)).ToList();
        Say($"cells whose block numbers by rule differ from the winning file's own blocks: {offBlock.Count(x => x.Value.Worldspace is null)} outside a worldspace, {offBlock.Count(x => x.Value.Worldspace is not null)} grid cells; grid cells with no grid: {rows.Values.Count(x => x.Worldspace is not null && !x.IsPersistentCell && x.GridX is null)}");
        foreach (var (key, row) in offBlock.Take(3)) Say($"  {row.Key} ({row.GridX},{row.GridY}) rule {(row.BlockA, row.BlockB, row.SubBlockA, row.SubBlockB)} file {filedBlocks[key]}");

        var counted = index.Cells.Select(row => (Row: row, Children: index.ChildCountOf(row.Key))).ToList();
        var largest = counted.MaxBy(x => x.Children);
        var interior = counted.Where(x => x.Row.Worldspace is null).MaxBy(x => x.Children);
        var exterior = counted.Where(x => x.Row.Worldspace is not null && !x.Row.IsPersistentCell).MaxBy(x => x.Children);

        async Task<CellObjects> Timed(string what, CellRow row, int children)
        {
            var clock = Stopwatch.StartNew();
            var first = await index.ObjectsOfAsync(row.Key, CancellationToken.None);
            double cold = clock.Elapsed.TotalSeconds;
            clock.Restart();
            await index.ObjectsOfAsync(row.Key, CancellationToken.None);
            int winners = first.Persistent.Concat(first.Temporary).Select(r => r.Winner).Distinct(StringComparer.OrdinalIgnoreCase).Count();
            Say($"{what}: {children} records ({first.Persistent.Count} persistent, {first.Temporary.Count} temporary, {first.Unfiled.Count} unfiled) won by {winners} plugin(s); first read {cold:0.00} s, again {clock.Elapsed.TotalSeconds:0.00} s; notes {first.Notes.Count}");
            foreach (var note in first.Notes.Take(3)) Say($"  note: {note}");
            return first;
        }
        if (interior.Row is not null) await Timed("largest interior cell", interior.Row, interior.Children);
        if (exterior.Row is not null) await Timed("largest exterior grid cell", exterior.Row, exterior.Children);
        var most = await Timed("cell with most records", largest.Row, largest.Children);
        Assert.Equal(largest.Children, most.Persistent.Count + most.Temporary.Count + most.Unfiled.Count);

        var search = Stopwatch.StartNew();
        int hits = index.Search("a", int.MaxValue).Count;
        Say($"search for one letter: {hits} cells in {search.Elapsed.TotalMilliseconds:0.0} ms");
        search.Restart();
        int gridHits = index.Search("0,0", int.MaxValue).Count;
        Say($"search for a grid: {gridHits} cells in {search.Elapsed.TotalMilliseconds:0.0} ms");
        if (most.Persistent.Concat(most.Temporary).FirstOrDefault(r => r.Position is not null) is { } placed)
        {
            search.Restart();
            var located = index.Locate(placed.Key);
            string? opens = index.OpenableCellOf(placed.Key);
            Say($"locate and openable cell of one placed object: {search.Elapsed.TotalMilliseconds:0.0} ms (opens a cell: {opens is not null})");
            Assert.NotNull(located);
        }

        if (Environment.GetEnvironmentVariable("WB_WORLD_REPORT") is { Length: > 0 } report) File.WriteAllLines(report, _lines);
    }
}
