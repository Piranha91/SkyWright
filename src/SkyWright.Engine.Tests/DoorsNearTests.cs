using HousecarlCore;
using Mutagen.Bethesda;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Skyrim;
using Noggog;
using Xunit;

namespace SkyWright.Engine.Tests;

/// <summary>Known answers for SkyWright's own "doors near this reference" query (<see cref="DoorsNear"/>), the context
/// panel's door list. Each case holds a positive control, so a query that answers nothing fails it on its own assertion.
/// D1-D20 (D19 and D20 on their own world, DoorsNearUnreadTests).</summary>
[Trait("tier", "unit")]
public sealed class DoorsNearTests : IClassFixture<DoorsNearTests.World>
{
    /// <summary>One worldspace with cells at grid (20,20) and (21,20) and a persistent cell that, like Skyrim's, carries
    /// grid 0,0; three interiors; four plugins: the master, the seed mod, a door mod and a later mod that deletes, strips
    /// and moves doors and is the last to copy the worldspace record (its copy names no persistent cell).</summary>
    public sealed class World : IDisposable
    {
        readonly string _dir;
        public LoadOrderResolver Resolver { get; }
        /// <summary>The plugin files this world wrote, in load order (what <see cref="Resolver"/> was built from), for a
        /// test that lays them out as a load order of its own.</summary>
        public IReadOnlyList<string> PluginPaths { get; }

        static readonly SkyrimRelease Rel = SkyrimRelease.SkyrimSE;
        public static readonly ModKey M = ModKey.FromNameAndExtension("hcDnMaster.esp");
        public static readonly ModKey S = ModKey.FromNameAndExtension("hcDnSeed.esp");
        public static readonly ModKey A = ModKey.FromNameAndExtension("hcDnDoors.esp");
        public static readonly ModKey D = ModKey.FromNameAndExtension("hcDnLater.esp");

        public static readonly FormKey Small = new(M, 0x800), DoorBase = new(M, 0x801), DoorZero = new(M, 0x802), NpcBase = new(M, 0x803), DoorWide = new(M, 0x804),
            Wrld = new(M, 0x900), Top = new(M, 0x901), C2020 = new(M, 0x910), C2120 = new(M, 0x911),
            I1 = new(M, 0x920), I2 = new(M, 0x921), I3 = new(M, 0x922);
        public static readonly FormKey Seed = new(S, 0x800), SeedInterior = new(S, 0x801), SeedCrowd = new(S, 0x802), SeedDeleted = new(S, 0x803),
            SeedEast = new(S, 0x804), SeedIsolated = new(S, 0x805), SeedPersistent = new(S, 0x806);
        public static readonly FormKey Touching = new(A, 0x800), JustIn = new(A, 0x801), JustOut = new(A, 0x802), Scaled = new(A, 0x803),
            NoBounds = new(A, 0x804), NonLoad = new(A, 0x805), Npc = new(A, 0x806), Persistent = new(A, 0x807),
            WinnerDeleted = new(A, 0x808), WinnerStripped = new(A, 0x809), MovedAway = new(A, 0x80A),
            InteriorSame = new(A, 0x80B), InteriorOther = new(A, 0x80C),
            AcrossBorder = new(A, 0x80D), WideFar = new(A, 0x80E), PrimitiveDoor = new(A, 0x80F);
        public const int CrowdSize = 205;
        public static FormKey CrowdDoor(int i) => new(A, 0xA00 + (uint)i);

        const float Cell = 4096;
        /// <summary>The seed, in the middle of cell (20,20).</summary>
        public static readonly P3Float SeedAt = new(20 * Cell + 2000, 20 * Cell + 2000, 0);
        static P3Float At(float dx, float dy) => new(SeedAt.X + dx, SeedAt.Y + dy, 0);

        public World()
        {
            _dir = Path.Combine(Path.GetTempPath(), "sw-doors-near-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_dir);
            string P(ModKey k) => Path.Combine(_dir, k.FileName);

            var master = new SkyrimMod(M, Rel);
            master.Statics.Add(new Static(Small, Rel) { EditorID = "hcDnSmall", ObjectBounds = Bounds(-16, -16, -16, 16, 16, 16) });   // radius 27.71
            master.Doors.Add(new Door(DoorBase, Rel) { EditorID = "hcDnDoor", ObjectBounds = Bounds(-10, -5, 0, 10, 5, 100) });       // radius 100.62
            master.Doors.Add(new Door(DoorZero, Rel) { EditorID = "hcDnDoorZero", ObjectBounds = Bounds(0, 0, 0, 0, 0, 0) });
            master.Doors.Add(new Door(DoorWide, Rel) { EditorID = "hcDnDoorWide", ObjectBounds = Bounds(-2000, -5, 0, 2000, 5, 100) });  // radius 2002.5
            master.Npcs.Add(new Npc(NpcBase, Rel) { EditorID = "hcDnNpc", ObjectBounds = Bounds(-20, -20, 0, 20, 20, 120) });
            var ws = new Worldspace(Wrld, Rel) { EditorID = "hcDnWorld" };
            ws.TopCell = new Mutagen.Bethesda.Skyrim.Cell(Top, Rel) { EditorID = "hcDnPersistent", Grid = new CellGrid { Point = new P2Int(0, 0) } };
            File(ws, Exterior(C2020, 20, 20), Exterior(C2120, 21, 20));
            master.Worldspaces.Add(ws);
            foreach (var i in new[] { I1, I2, I3 }) FileInterior(master, Interior(i));
            master.BeginWrite.ToPath(P(M)).WithLoadOrder(Array.Empty<ISkyrimModGetter>()).Write();

            var seed = new SkyrimMod(S, Rel);
            var sws = new Worldspace(Wrld, Rel) { EditorID = "hcDnWorld" };
            sws.TopCell = new Mutagen.Bethesda.Skyrim.Cell(Top, Rel) { EditorID = "hcDnPersistent", Grid = new CellGrid { Point = new P2Int(0, 0) } };
            sws.TopCell.Persistent.Add(Ref(SeedPersistent, Small, At(10, 70)));                       // a seed that itself stands in the persistent cell
            var s2020 = Exterior(C2020, 20, 20);
            s2020.Temporary.Add(Ref(Seed, Small, SeedAt));
            s2020.Temporary.Add(new PlacedObject(SeedDeleted, Rel) { IsDeleted = true });
            s2020.Temporary.Add(Ref(SeedEast, Small, new P3Float(21 * Cell - 50, SeedAt.Y, 0)));   // 50 units west of the (20,20)/(21,20) border
            var s2120 = Exterior(C2120, 21, 20);
            s2120.Temporary.Add(Ref(SeedIsolated, Small, new P3Float(21 * Cell + 3000, 20 * Cell + 3500, 0)));
            File(sws, s2020, s2120);
            seed.Worldspaces.Add(sws);
            var si1 = Interior(I1); si1.Temporary.Add(Ref(SeedInterior, Small, new P3Float(0, 0, 0))); FileInterior(seed, si1);
            var si2 = Interior(I2); si2.Temporary.Add(Ref(SeedCrowd, Small, new P3Float(0, 0, 0))); FileInterior(seed, si2);
            Write(seed, P(S), master);

            var a = new SkyrimMod(A, Rel);
            var aws = new Worldspace(Wrld, Rel) { EditorID = "hcDnWorld" };
            aws.TopCell = new Mutagen.Bethesda.Skyrim.Cell(Top, Rel) { EditorID = "hcDnPersistent", Grid = new CellGrid { Point = new P2Int(0, 0) } };
            aws.TopCell.Persistent.Add(LoadDoor(Persistent, DoorBase, At(0, 60)));                     // filed in the persistent cell, gap 0
            var a2020 = Exterior(C2020, 20, 20);
            a2020.Temporary.Add(LoadDoor(Touching, DoorBase, At(40, 0)));                              // gap 0
            a2020.Temporary.Add(LoadDoor(JustIn, DoorBase, At(0, 378)));                              // gap 249.7
            a2020.Temporary.Add(LoadDoor(JustOut, DoorBase, At(0, -390)));                            // gap 261.7
            var scaled = LoadDoor(Scaled, DoorBase, At(-575, 0)); scaled.Scale = 3f; a2020.Temporary.Add(scaled);   // gap 245.4 at scale 3, 446.7 at 1
            a2020.Temporary.Add(LoadDoor(NoBounds, DoorZero, At(250, 0)));                            // origin only, gap 222.3
            a2020.Temporary.Add(Ref(NonLoad, DoorBase, At(30, 30)));                                  // a door with no TeleportDestination
            var npc = new PlacedNpc(Npc, Rel) { Placement = new Placement { Position = At(-30, 30) } }; npc.Base.SetTo(NpcBase);
            a2020.Temporary.Add(npc);
            a2020.Temporary.Add(LoadDoor(WinnerDeleted, DoorBase, At(50, 50)));
            a2020.Temporary.Add(LoadDoor(WinnerStripped, DoorBase, At(60, -60)));
            a2020.Temporary.Add(LoadDoor(MovedAway, DoorBase, At(20, -20)));
            a2020.Temporary.Add(LoadDoor(WideFar, DoorWide, At(0, 2050)));
            var prim = LoadDoor(PrimitiveDoor, DoorZero, At(-300, -300));                              // no size of its own; its primitive's half-diagonal 424.3 reaches: gap 0
            prim.Primitive = new PlacedPrimitive { Type = PlacedPrimitive.TypeEnum.Box, Bounds = new P3Float(600, 600, 0) };
            a2020.Temporary.Add(prim);                         // origin 2,050 away, bounds reach: gap 19.8
            var a2120 = Exterior(C2120, 21, 20);
            a2120.Temporary.Add(LoadDoor(AcrossBorder, DoorBase, new P3Float(21 * Cell + 60, SeedAt.Y, 0)));   // 110 from SeedEast, across the border
            File(aws, a2020, a2120);
            a.Worldspaces.Add(aws);
            var ai1 = Interior(I1); ai1.Temporary.Add(LoadDoor(InteriorSame, DoorBase, new P3Float(30, 0, 0))); FileInterior(a, ai1);
            var ai3 = Interior(I3); ai3.Temporary.Add(LoadDoor(InteriorOther, DoorBase, new P3Float(30, 0, 0))); FileInterior(a, ai3);
            var ai2 = Interior(I2);
            for (int i = 0; i < CrowdSize; i++) ai2.Temporary.Add(LoadDoor(CrowdDoor(i), DoorBase, new P3Float((i % 15) * 8, (i / 15) * 8, 0)));
            FileInterior(a, ai2);
            Write(a, P(A), master);

            // The later mod: deletes one door, strips another's destination, moves a third away; its worldspace copy is the
            // winner and names no persistent cell.
            var d = new SkyrimMod(D, Rel);
            var dws = new Worldspace(Wrld, Rel) { EditorID = "hcDnWorld" };
            var d2020 = Exterior(C2020, 20, 20);
            d2020.Temporary.Add(new PlacedObject(WinnerDeleted, Rel) { IsDeleted = true });
            d2020.Temporary.Add(Ref(WinnerStripped, DoorBase, At(60, -60)));
            d2020.Temporary.Add(LoadDoor(MovedAway, DoorBase, At(1500, 0)));                          // its earlier version stood at gap 0
            File(dws, d2020);
            d.Worldspaces.Add(dws);
            d.BeginWrite.ToPath(P(D)).WithLoadOrder(new ISkyrimModGetter[] { master, a }).Write();

            PluginPaths = new[] { P(M), P(S), P(A), P(D) };
            Resolver = LoadOrderResolver.Build(PluginPaths);
        }

        static ObjectBounds Bounds(short x0, short y0, short z0, short x1, short y1, short z1) =>
            new() { First = new P3Int16(x0, y0, z0), Second = new P3Int16(x1, y1, z1) };

        static void Write(SkyrimMod mod, string path, SkyrimMod master) =>
            mod.BeginWrite.ToPath(path).WithLoadOrder(new ISkyrimModGetter[] { master }).Write();

        static Mutagen.Bethesda.Skyrim.Cell Exterior(FormKey fk, int x, int y) =>
            new(fk, Rel) { EditorID = $"hcDnCell{x}_{y}", Grid = new CellGrid { Point = new P2Int(x, y) } };

        static Mutagen.Bethesda.Skyrim.Cell Interior(FormKey fk) =>
            new(fk, Rel) { EditorID = $"hcDnInterior{fk.ID:X}", Flags = Mutagen.Bethesda.Skyrim.Cell.Flag.IsInteriorCell };

        static PlacedObject Ref(FormKey fk, FormKey baseKey, P3Float at)
        {
            var r = new PlacedObject(fk, Rel) { Placement = new Placement { Position = at } };
            r.Base.SetTo(baseKey);
            return r;
        }

        static PlacedObject LoadDoor(FormKey fk, FormKey baseKey, P3Float at)
        {
            var r = Ref(fk, baseKey, at);
            var td = new TeleportDestination { Position = new P3Float(0, 0, 0), Rotation = new P3Float(0, 0, 0) };
            td.Door.SetTo(new FormKey(M, 0xF00));
            r.TeleportDestination = td;
            return r;
        }

        static void File(Worldspace ws, params Mutagen.Bethesda.Skyrim.Cell[] cells)
        {
            var blk = new WorldspaceBlock { BlockNumberX = 0, BlockNumberY = 0, GroupType = GroupTypeEnum.ExteriorCellBlock };
            var sub = new WorldspaceSubBlock { BlockNumberX = 0, BlockNumberY = 0, GroupType = GroupTypeEnum.ExteriorCellSubBlock };
            foreach (var cell in cells) sub.Items.Add(cell);
            blk.Items.Add(sub);
            ws.SubCells.Add(blk);
        }

        static void FileInterior(SkyrimMod mod, Mutagen.Bethesda.Skyrim.Cell cell)
        {
            uint id = cell.FormKey.ID;
            int blockN = (int)(id % 10), subN = (int)((id / 10) % 10);
            var records = mod.Cells.Records;
            var block = records.FirstOrDefault(x => x.BlockNumber == blockN);
            if (block is null) { block = new CellBlock { BlockNumber = blockN, GroupType = GroupTypeEnum.InteriorCellBlock }; records.Add(block); }
            var sub = block.SubBlocks.FirstOrDefault(x => x.BlockNumber == subN);
            if (sub is null) { sub = new CellSubBlock { BlockNumber = subN, GroupType = GroupTypeEnum.InteriorCellSubBlock }; block.SubBlocks.Add(sub); }
            sub.Cells.Add(cell);
        }

        public void Dispose()
        {
            Resolver.Dispose();
            try { Directory.Delete(_dir, true); } catch { /* best effort */ }
        }
    }

    const double Radius = 256;     // the context panel's
    readonly World _w;
    public DoorsNearTests(World w) => _w = w;

    DoorsNearAnswer Near(FormKey seed, int listAtMost = 200) => DoorsNear.Of(_w.Resolver.Capture(), seed, Radius, listAtMost);

    static void Answered(DoorsNearAnswer a) => Assert.True(a.Refusal is null, "refused: " + a.Refusal);

    // D1: a load door whose extent touches the reference's is listed; the reference is not its own neighbour.
    [Fact]
    public void D1_a_touching_load_door_is_listed()
    {
        var a = Near(World.Seed);
        Answered(a);
        Assert.Contains(World.Touching, a.Listed);
        Assert.DoesNotContain(World.Seed, a.Listed);
    }

    // D2: the radius is between the two extents, not the two origins: a gap of 249.7 is in, 261.7 is out.
    [Fact]
    public void D2_the_gap_between_the_extents_decides_at_the_radius()
    {
        var a = Near(World.Seed);
        Answered(a);
        Assert.Contains(World.JustIn, a.Listed);
        Assert.DoesNotContain(World.JustOut, a.Listed);
    }

    // D3: a reference's extent grows with its scale.
    [Fact]
    public void D3_the_scale_sizes_the_extent() => Assert.Contains(World.Scaled, Near(World.Seed).Listed);

    // D4: a door whose base has no size counts by its origin.
    [Fact]
    public void D4_a_door_with_no_bounds_counts_by_its_origin() => Assert.Contains(World.NoBounds, Near(World.Seed).Listed);

    // D5: only placed objects with a TeleportDestination are doors: a door base without one, and an actor, are not.
    [Fact]
    public void D5_only_load_doors_are_listed()
    {
        var a = Near(World.Seed);
        Assert.Contains(World.Touching, a.Listed);
        Assert.DoesNotContain(World.NonLoad, a.Listed);
        Assert.DoesNotContain(World.Npc, a.Listed);
    }

    // D6: the winning version decides whether it is a door: deleted, or stripped of its destination, by a later plugin,
    // it is not listed.
    [Fact]
    public void D6_the_winner_decides_whether_it_is_a_door()
    {
        var a = Near(World.Seed);
        Assert.Contains(World.Touching, a.Listed);
        Assert.DoesNotContain(World.WinnerDeleted, a.Listed);
        Assert.DoesNotContain(World.WinnerStripped, a.Listed);
    }

    // D7: every plugin's version of a candidate is tested for the distance (as today's): a door a later plugin moved away
    // is still listed when an earlier version stood beside the reference.
    [Fact]
    public void D7_every_version_is_tested_for_the_distance() => Assert.Contains(World.MovedAway, Near(World.Seed).Listed);

    // D8: a load door filed in the worldspace's persistent cell is found when the winning copy of the worldspace names no
    // persistent cell and that cell carries grid 0,0, as Skyrim's does (today's near= misses it: a stated difference).
    [Fact]
    public void D8_a_door_in_the_persistent_cell_is_found_when_the_winner_names_none() =>
        Assert.Contains(World.Persistent, Near(World.Seed).Listed);

    // D9: an interior reference sees the doors of its own cell only.
    [Fact]
    public void D9_an_interior_reference_sees_its_own_cell_only()
    {
        var a = Near(World.SeedInterior);
        Answered(a);
        Assert.Equal(new[] { World.InteriorSame }, a.Listed);
    }

    // D10: more doors than the list shows are counted, never cut silently; the list is the nearest ones.
    [Fact]
    public void D10_more_doors_than_the_list_shows_are_counted()
    {
        var a = Near(World.SeedCrowd);
        Answered(a);
        Assert.Equal(200, a.Listed.Count);
        Assert.Equal(World.CrowdSize, a.Total);
        Assert.Contains(World.CrowdDoor(0), a.Listed);                                   // at the reference's own origin
    }

    // D11: the list is nearest first.
    [Fact]
    public void D11_the_list_is_nearest_first()
    {
        var a = Near(World.Seed).Listed.ToList();
        int noBounds = a.IndexOf(World.NoBounds), scaled = a.IndexOf(World.Scaled), justIn = a.IndexOf(World.JustIn), touching = a.IndexOf(World.Touching);
        Assert.True(touching >= 0 && touching < noBounds && noBounds < scaled && scaled < justIn, string.Join(", ", a));
    }

    // D13: across a cell border it is the gap that decides, not the cell.
    [Fact]
    public void D13_across_a_cell_border_the_gap_decides() => Assert.Contains(World.AcrossBorder, Near(World.SeedEast).Listed);

    // D14: a reference with no door in reach is answered with none, not refused (the control: the seed's world has doors).
    [Fact]
    public void D14_an_isolated_reference_is_answered_with_none()
    {
        Assert.NotEmpty(Near(World.Seed).Listed);
        var a = Near(World.SeedIsolated);
        Answered(a);
        Assert.Empty(a.Listed);
        Assert.Equal(0, a.Total);
    }

    // D15: a door whose origin is far but whose base's bounds reach is listed.
    [Fact]
    public void D15_a_far_origin_with_large_bounds_is_listed() => Assert.Contains(World.WideFar, Near(World.Seed).Listed);

    // D16: a door sized by its own primitive, not its base, is listed (origin 424 away, base of no size).
    [Fact]
    public void D16_a_door_sized_by_its_own_primitive_is_listed() => Assert.Contains(World.PrimitiveDoor, Near(World.Seed).Listed);

    // D17: an actor can be the reference asked about (today's answers it as any placed reference).
    [Fact]
    public void D17_an_actor_is_answered_like_any_placed_reference()
    {
        var a = Near(World.Npc);
        Answered(a);
        Assert.Contains(World.Touching, a.Listed);
        Assert.DoesNotContain(World.Npc, a.Listed);
    }

    // D18: a reference that itself stands in the persistent cell sees the persistent doors and the grid cells' doors.
    [Fact]
    public void D18_a_reference_in_the_persistent_cell_sees_both()
    {
        var a = Near(World.SeedPersistent);
        Answered(a);
        Assert.Contains(World.Persistent, a.Listed);
        Assert.Contains(World.Touching, a.Listed);
    }

    // D12: a reference the question cannot be asked of is refused in one sentence that names it: not in the load order,
    // a base record rather than a placed reference, a deleted reference with no position.
    [Theory]
    [InlineData("absent")]
    [InlineData("base")]
    [InlineData("deleted")]
    public void D12_a_reference_that_cannot_be_asked_of_is_refused_in_one_sentence(string which)
    {
        var key = which switch { "absent" => new FormKey(ModKey.FromNameAndExtension("hcDnAbsent.esp"), 0x800), "base" => World.DoorBase, _ => World.SeedDeleted };
        var a = Near(key);
        Assert.NotNull(a.Refusal);
        Assert.Contains(key.ToString(), a.Refusal);
        Assert.EndsWith(".", a.Refusal);
        Assert.Equal(1, a.Refusal!.Split(". ").Length);
        Assert.Empty(a.Listed);
    }
}
