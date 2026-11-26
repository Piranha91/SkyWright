using HousecarlCore;
using Mutagen.Bethesda;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Skyrim;
using Noggog;
using Xunit;

namespace SkyWright.Engine.Tests;

/// <summary>What stands near a set of placed references (<see cref="NearReferences"/>), credited to the plugin whose
/// version reaches the seed: houseCARL's near= cases 480-489, kept by name against SkyWright's own query when near= left
/// the fork (step 5 of the boundary). 488 and three rows of 489 tested the server's grammar and retired with it.</summary>
[Trait("tier", "unit")]
public sealed class NearSelectionTests : IClassFixture<NearSelectionTests.World>
{
    /// <summary>One worldspace with cells at grid (0,0), (1,0), (6,0) and (20,20), its persistent cell, two interiors,
    /// and five plugins: the master, the seed mod, a neighbour mod, a mover and a terrain mod.</summary>
    public sealed class World : IDisposable
    {
        readonly string _dir;
        public LoadOrderResolver Resolver { get; }
        /// <summary>The plugin files this world wrote, in load order (what <see cref="Resolver"/> was built from), for a
        /// test that lays them out as a load order of its own.</summary>
        public IReadOnlyList<string> PluginPaths { get; }

        public static readonly ModKey M = ModKey.FromNameAndExtension("hcNearMaster.esp");
        public static readonly ModKey S = ModKey.FromNameAndExtension("hcNearSeed.esp");
        public static readonly ModKey A = ModKey.FromNameAndExtension("hcNearA.esp");
        public static readonly ModKey B = ModKey.FromNameAndExtension("hcNearMove.esp");
        public static readonly ModKey C = ModKey.FromNameAndExtension("hcNearTerrain.esp");

        // Master records.
        public static readonly FormKey Small = new(M, 0x800), Large = new(M, 0x801), Wrld = new(M, 0x900), Top = new(M, 0x901),
            C00 = new(M, 0x910), C10 = new(M, 0x911), C60 = new(M, 0x912), C2020 = new(M, 0x913),
            I1 = new(M, 0x920), I2 = new(M, 0x921), Vanilla = new(M, 0xA00);
        // Seeds.
        public static readonly FormKey Seed = new(S, 0x800), SeedInterior = new(S, 0x801), SeedIsolated = new(S, 0x802);
        // Neighbour mod.
        public static readonly FormKey Touching = new(A, 0x800), AcrossBorder = new(A, 0x801), SixCellsAway = new(A, 0x802),
            LargeFar = new(A, 0x803), InteriorSame = new(A, 0x804), InteriorOther = new(A, 0x805), NoBounds = new(A, 0x806);
        // Terrain mod.
        public static readonly FormKey LandHere = new(C, 0x800), LandNext = new(C, 0x801), NavHere = new(C, 0x802),
            Occluder = new(C, 0x803);

        // The seed stands 96 units west of the (0,0)/(1,0) border.
        public static readonly P3Float SeedAt = new(4000, 2000, 0);
        const float CellSize = 4096;

        public World()
        {
            _dir = Path.Combine(Path.GetTempPath(), "hc-near-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_dir);
            string P(ModKey k) => Path.Combine(_dir, k.FileName);

            var master = new SkyrimMod(M, SkyrimRelease.SkyrimSE);
            master.Statics.Add(new Static(Small, SkyrimRelease.SkyrimSE)
                { EditorID = "hcNearSmall", ObjectBounds = new ObjectBounds { First = new P3Int16(-16, -16, -16), Second = new P3Int16(16, 16, 16) } });
            master.Statics.Add(new Static(Large, SkyrimRelease.SkyrimSE)
                { EditorID = "hcNearLarge", ObjectBounds = new ObjectBounds { First = new P3Int16(-3000, -3000, 0), Second = new P3Int16(3000, 3000, 500) } });
            var ws = Worldspace(Wrld);
            ws.TopCell = new Cell(Top, SkyrimRelease.SkyrimSE) { EditorID = "hcNearPersistent" };
            var c60 = ExteriorCell(C60, 6, 0);
            c60.Temporary.Add(Ref(Vanilla, Small, new P3Float(6 * CellSize + 100, 2000, 0)));
            File(ws, ExteriorCell(C00, 0, 0), ExteriorCell(C10, 1, 0), c60, ExteriorCell(C2020, 20, 20));
            master.Worldspaces.Add(ws);
            FileInterior(master, new Cell(I1, SkyrimRelease.SkyrimSE) { EditorID = "hcNearI1", Flags = Cell.Flag.IsInteriorCell });
            FileInterior(master, new Cell(I2, SkyrimRelease.SkyrimSE) { EditorID = "hcNearI2", Flags = Cell.Flag.IsInteriorCell });
            master.BeginWrite.ToPath(P(M)).WithLoadOrder(Array.Empty<ISkyrimModGetter>()).Write();

            // The seed mod: one persistent board-like ref near the border, one in an interior, one far away.
            var seed = new SkyrimMod(S, SkyrimRelease.SkyrimSE);
            var sws = Worldspace(Wrld);
            sws.TopCell = new Cell(Top, SkyrimRelease.SkyrimSE) { EditorID = "hcNearPersistent" };
            sws.TopCell.Persistent.Add(Ref(Seed, Small, SeedAt));
            var s2020 = ExteriorCell(C2020, 20, 20);
            s2020.Temporary.Add(Ref(SeedIsolated, Small, new P3Float(20 * CellSize + 100, 20 * CellSize + 100, 0)));
            File(sws, s2020);
            seed.Worldspaces.Add(sws);
            var si1 = new Cell(I1, SkyrimRelease.SkyrimSE) { EditorID = "hcNearI1", Flags = Cell.Flag.IsInteriorCell };
            si1.Temporary.Add(Ref(SeedInterior, Small, new P3Float(0, 0, 0)));
            FileInterior(seed, si1);
            Write(seed, P(S), master);

            // The neighbour mod.
            var a = new SkyrimMod(A, SkyrimRelease.SkyrimSE);
            var aws = Worldspace(Wrld);
            var a00 = ExteriorCell(C00, 0, 0);
            a00.Temporary.Add(Ref(Touching, Small, new P3Float(4010, 2000, 0)));                       // gap 0
            a00.Temporary.Add(Ref(NoBounds, new FormKey(M, 0xFFF), new P3Float(4040, 2000, 0)));       // base not in the order: origin only, gap 12.3
            var a10 = ExteriorCell(C10, 1, 0);
            a10.Temporary.Add(Ref(AcrossBorder, Small, new P3Float(4120, 2000, 0)));                   // gap 64.6, across the border
            a10.Temporary.Add(Ref(LargeFar, Large, new P3Float(7096, 2000, 0)));                       // origin 3,096 away, bounds reach
            var a60 = ExteriorCell(C60, 6, 0);
            a60.Temporary.Add(Ref(SixCellsAway, Small, new P3Float(6 * CellSize + 200, 2000, 0)));        // six cells away
            File(aws, a00, a10, a60);
            a.Worldspaces.Add(aws);
            var ai1 = new Cell(I1, SkyrimRelease.SkyrimSE) { EditorID = "hcNearI1", Flags = Cell.Flag.IsInteriorCell };
            ai1.Temporary.Add(Ref(InteriorSame, Small, new P3Float(10, 0, 0)));
            FileInterior(a, ai1);
            var ai2 = new Cell(I2, SkyrimRelease.SkyrimSE) { EditorID = "hcNearI2", Flags = Cell.Flag.IsInteriorCell };
            ai2.Temporary.Add(Ref(InteriorOther, Small, new P3Float(0, 0, 0)));
            FileInterior(a, ai2);
            Write(a, P(A), master);

            // The mover: brings the master's ref from cell (6,0) to beside the seed.
            var b = new SkyrimMod(B, SkyrimRelease.SkyrimSE);
            var bws = Worldspace(Wrld);
            var b00 = ExteriorCell(C00, 0, 0);
            b00.Temporary.Add(Ref(Vanilla, Small, new P3Float(4020, 2010, 0)));
            File(bws, b00);
            b.Worldspaces.Add(bws);
            Write(b, P(B), master);

            // The terrain mod: terrain and a navmesh in the seed's cell, terrain in the next cell east.
            var c = new SkyrimMod(C, SkyrimRelease.SkyrimSE);
            var cws = Worldspace(Wrld);
            var c00 = ExteriorCell(C00, 0, 0);
            c00.Landscape = new Landscape(LandHere, SkyrimRelease.SkyrimSE);
            c00.NavigationMeshes.Add(new NavigationMesh(NavHere, SkyrimRelease.SkyrimSE)
            {
                Data = new NavigationMeshData
                {
                    Parent = new WorldspaceNavmeshParent { Parent = Wrld.ToLink<IWorldspaceGetter>(), Coordinates = new P2Int16(0, 0) },
                },
            });
            var c10 = ExteriorCell(C10, 1, 0);
            c10.Landscape = new Landscape(LandNext, SkyrimRelease.SkyrimSE);
            // An occlusion-plane-like reference: a small base, but a primitive 8,000 units across, 4,096 units away.
            var occluder = Ref(Occluder, Small, new P3Float(8096, 2000, 0));
            occluder.Primitive = new PlacedPrimitive { Type = PlacedPrimitive.TypeEnum.Box, Bounds = new P3Float(8000, 8000, 0) };
            c10.Temporary.Add(occluder);
            File(cws, c00, c10);
            c.Worldspaces.Add(cws);
            Write(c, P(C), master);

            PluginPaths = new[] { P(M), P(S), P(A), P(B), P(C) };
            Resolver = LoadOrderResolver.Build(PluginPaths);
        }

        static void Write(SkyrimMod mod, string path, SkyrimMod master) =>
            mod.BeginWrite.ToPath(path).WithLoadOrder(new ISkyrimModGetter[] { master }).Write();

        static Worldspace Worldspace(FormKey fk) => new(fk, SkyrimRelease.SkyrimSE) { EditorID = "hcNearWorld" };

        static Cell ExteriorCell(FormKey fk, int x, int y) =>
            new(fk, SkyrimRelease.SkyrimSE) { EditorID = $"hcNearCell{x}_{y}", Grid = new CellGrid { Point = new P2Int(x, y) } };

        static PlacedObject Ref(FormKey fk, FormKey baseKey, P3Float at)
        {
            var r = new PlacedObject(fk, SkyrimRelease.SkyrimSE) { Placement = new Placement { Position = at } };
            r.Base.SetTo(baseKey);
            return r;
        }

        static void File(Worldspace ws, params Cell[] cells)
        {
            var blk = new WorldspaceBlock { BlockNumberX = 0, BlockNumberY = 0, GroupType = GroupTypeEnum.ExteriorCellBlock };
            var sub = new WorldspaceSubBlock { BlockNumberX = 0, BlockNumberY = 0, GroupType = GroupTypeEnum.ExteriorCellSubBlock };
            foreach (var cell in cells) sub.Items.Add(cell);
            blk.Items.Add(sub);
            ws.SubCells.Add(blk);
        }

        static void FileInterior(SkyrimMod mod, Cell cell)
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
            try { Directory.Delete(_dir, true); } catch { /* temp cleanup best-effort */ }
        }
    }

    readonly World _w;
    public NearSelectionTests(World w) => _w = w;

    static string Fid(FormKey fk) => $"{fk.ID:X6}:{fk.ModKey.FileName}";

    NearAnswer Near(FormKey[] seeds, double? within = null) => NearReferences.Of(_w.Resolver.Capture(), seeds, within ?? 0);

    /// <summary>The matched FormIDs with their near annotation; fails when the read refused.</summary>
    static Dictionary<string, string> Matches(NearAnswer a)
    {
        Assert.True(a.Refusal is null, "refused: " + a.Refusal);
        return a.Keys.ToDictionary(Fid, a.Annotate);
    }

    static string Notes(NearAnswer a) => a.Note;

    // 480: "a neighbour touching the seed is selected, naming its provider, the seed, the gap and the rule".
    [Fact]
    public void Case480_ATouchingNeighbourIsSelectedWithProviderSeedGapAndRule()
    {
        var m = Matches(Near(new[] { World.Seed }));
        var note = Assert.Contains(Fid(World.Touching), (IDictionary<string, string>)m);
        Assert.Contains(Fid(World.Seed), note);
        Assert.Contains("hcNearA.esp", note);
        Assert.Contains("gap 0", note);
        Assert.Contains("sphere", note);
        Assert.DoesNotContain(Fid(World.Seed), (IDictionary<string, string>)m);   // a seed is not its own neighbour
    }

    // 481: "an origin 3,096 units away is selected when its base's bounds reach the seed".
    [Fact]
    public void Case481_AFarOriginWithLargeBoundsIsSelected() =>
        Assert.Contains(Fid(World.LargeFar), (IDictionary<string, string>)Matches(Near(new[] { World.Seed })));

    // 482: "across a cell border it is the gap that decides; six cells away is outside the stated ±5 bound".
    [Fact]
    public void Case482_AcrossTheBorderTheGapDecidesAndSixCellsAwayIsOutsideTheBound()
    {
        Assert.DoesNotContain(Fid(World.AcrossBorder), (IDictionary<string, string>)Matches(Near(new[] { World.Seed })));
        Assert.Contains(Fid(World.AcrossBorder), (IDictionary<string, string>)Matches(Near(new[] { World.Seed }, within: 100)));
        var far = Near(new[] { World.Seed }, within: 100_000);
        Assert.DoesNotContain(Fid(World.SixCellsAway), (IDictionary<string, string>)Matches(far));
        Assert.Contains("5 grid cells", Notes(far));
    }

    // 483: "a master reference moved beside the seed is credited to the mover, not the master".
    [Fact]
    public void Case483_AMovedReferenceIsCreditedToTheMoverOnly()
    {
        var note = Assert.Contains(Fid(World.Vanilla), (IDictionary<string, string>)Matches(Near(new[] { World.Seed })));
        Assert.Contains("hcNearMove.esp", note);
        Assert.DoesNotContain("hcNearMaster.esp", note);
    }

    // 484: "terrain and navmesh match by their cell square: the seed's own cell at once, the next cell only when the
    // square comes within reach" (review condition 3, both ways).
    [Fact]
    public void Case484_TerrainAndNavmeshMatchByCellSquareBothWaysAtTheBorder()
    {
        var at0 = Matches(Near(new[] { World.Seed }));
        var land = Assert.Contains(Fid(World.LandHere), (IDictionary<string, string>)at0);
        Assert.Contains("cell square", land);
        Assert.Contains("hcNearTerrain.esp", land);
        Assert.Contains(Fid(World.NavHere), (IDictionary<string, string>)at0);
        Assert.DoesNotContain(Fid(World.LandNext), (IDictionary<string, string>)at0);           // 68 units short
        Assert.Contains(Fid(World.LandNext), (IDictionary<string, string>)Matches(Near(new[] { World.Seed }, within: 100)));
    }

    // 485: "an interior seed matches its own cell's references only".
    [Fact]
    public void Case485_AnInteriorSeedMatchesItsOwnCellOnly()
    {
        var m = Matches(Near(new[] { World.SeedInterior }, within: 100_000));
        Assert.Contains(Fid(World.InteriorSame), (IDictionary<string, string>)m);
        Assert.DoesNotContain(Fid(World.InteriorOther), (IDictionary<string, string>)m);
        Assert.DoesNotContain(Fid(World.Touching), (IDictionary<string, string>)m);
    }

    // 486: "an isolated seed selects nothing, and the response says what was read".
    [Fact]
    public void Case486_AnIsolatedSeedSelectsNothingAndSaysWhatWasRead()
    {
        var doc = Near(new[] { World.SeedIsolated });
        Assert.Empty(Matches(doc));
        Assert.Matches(@"read \d+ cell\(s\)", Notes(doc));
    }

    // 487: "a base the order cannot size counts by its origin, and the response counts it".
    [Fact]
    public void Case487_ABaseWithoutBoundsCountsByOriginAndIsCounted()
    {
        Assert.DoesNotContain(Fid(World.NoBounds), (IDictionary<string, string>)Matches(Near(new[] { World.Seed })));
        var doc = Near(new[] { World.Seed }, within: 20);
        Assert.Contains(Fid(World.NoBounds), (IDictionary<string, string>)Matches(doc));
        Assert.Contains("origin only", Notes(doc));
    }

    // 487b: "a match made by a reference's primitive (an occlusion box), not its base's bounds, is labelled
    // 'primitive', so a reader can set it aside; the row stays" (delivery review).
    [Fact]
    public void Case487b_AMatchMadeByAPrimitiveIsLabelledPrimitive()
    {
        var m = Matches(Near(new[] { World.Seed }));
        var note = Assert.Contains(Fid(World.Occluder), (IDictionary<string, string>)m);
        Assert.Contains("primitive)", note);
        Assert.DoesNotContain("sphere)", note);
        Assert.Contains("sphere)", m[Fid(World.Touching)]);              // a base-sized match keeps its own label
    }

    // N1-N4: the radius up to which the answer is complete (NearAnswer.CompleteWithin), derived by hand from the fixture:
    // the cells read are the grid cells within 5 of the seed's own cell, so the read square is [(g-5)*4096, (g+6)*4096) on
    // each axis; the bound is the distance from the seed to the square's nearest edge, less the seed's own extent (base
    // hcNearSmall, bounds -16..16 on each axis, so its corner sphere is sqrt(3 * 16^2) = 27.712812921102035, scale 1).
    //   Seed (4000, 2000) in cell (0,0): square [-20480, 24576) on both axes; west 24480, east 20576, south 22480,
    //   north 22576; nearest 20576; bound 20576 - 27.712812921102035 = 20548.287187078898.
    //   SeedIsolated (82020, 82020) in cell (20,20): square [61440, 106496); west and south 20580, east and north 24476;
    //   bound 20580 - 27.712812921102035 = 20552.287187078898.
    //   SeedInterior reads its whole cell: no bound (+infinity).
    const double SeedBound = 20548.287187078898, IsolatedBound = 20552.287187078898;

    [Fact]
    public void N1_the_complete_radius_of_an_exterior_seed_is_the_read_square_edge_less_its_extent()
    {
        var a = Near(new[] { World.Seed });
        Assert.True(a.Refusal is null, "refused: " + a.Refusal);
        Assert.Equal(SeedBound, a.CompleteWithin, 6);
        Assert.Equal(IsolatedBound, Near(new[] { World.SeedIsolated }).CompleteWithin, 6);
    }

    [Fact]
    public void N2_several_seeds_are_complete_to_the_nearest_bound_and_an_interior_seed_has_none()
    {
        Assert.Equal(SeedBound, Near(new[] { World.SeedIsolated, World.Seed }).CompleteWithin, 6);
        Assert.Equal(double.PositiveInfinity, Near(new[] { World.SeedInterior }).CompleteWithin);
        Assert.Equal(SeedBound, Near(new[] { World.SeedInterior, World.Seed }).CompleteWithin, 6);
    }

    // N3: the asked radius is complete only strictly inside the bound: a point-sized record on the square's edge would
    // have a gap of exactly the bound, and the hit test keeps a gap equal to the radius, so the bound itself is not.
    [Fact]
    public void N3_the_asked_radius_is_complete_only_inside_the_bound()
    {
        Assert.True(Near(new[] { World.Seed }, within: 100).WithinIsComplete);
        Assert.True(Near(new[] { World.Seed }, within: SeedBound - 1e-6).WithinIsComplete);
        Assert.False(Near(new[] { World.Seed }, within: SeedBound).WithinIsComplete);
        Assert.False(Near(new[] { World.Seed }, within: 100_000).WithinIsComplete);         // case 482's far query
        Assert.True(Near(new[] { World.SeedInterior }, within: 100_000).WithinIsComplete);  // case 485's
    }

    // N4: a distance that is not a number, infinite or negative is refused in one sentence (before, NaN and +infinity
    // listed every version in the cells read, and a negative distance listed nothing); 0 and -0 are answered.
    [Theory]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    [InlineData(double.NegativeInfinity)]
    [InlineData(-1.0)]
    [InlineData(-1e-12)]
    public void N4_a_distance_that_is_not_a_finite_non_negative_number_is_refused(double within)
    {
        var a = NearReferences.Of(_w.Resolver.Capture(), new[] { World.Seed }, within);
        Assert.NotNull(a.Refusal);
        Assert.Contains("distance", a.Refusal);
        Assert.EndsWith(".", a.Refusal);
        Assert.Equal(1, a.Refusal!.Split(". ").Length);
        Assert.Empty(a.Keys);
    }

    // two rows by a flag, not by the value: xUnit's InlineData compares 0.0 and -0.0 equal and would fold them
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void N4b_zero_is_answered(bool negativeZero)
    {
        double within = negativeZero ? -0.0 : 0.0;
        Assert.Equal(negativeZero, double.IsNegative(within));
        var a = NearReferences.Of(_w.Resolver.Capture(), new[] { World.Seed }, within);
        Assert.True(a.Refusal is null, "refused: " + a.Refusal);
        Assert.Contains(Fid(World.Touching), (IDictionary<string, string>)Matches(a));
    }

    // 489: "each misuse is refused in one sentence" — the two that are not the server's grammar (an empty list, a
    // negative distance and walk= retired with near=).
    [Theory]
    [InlineData("not-placed")]
    [InlineData("not-in-order")]
    public void Case489_EachMisuseIsRefusedInOneSentence(string misuse)
    {
        var (seed, expect) = misuse == "not-placed"
            ? (World.Small, "not a placed reference")
            : (FormKey.Factory("000800:hcNearAbsent.esp"), "load order");
        var r = NearReferences.Of(_w.Resolver.Capture(), new[] { seed }, 0);
        Assert.NotNull(r.Refusal);
        Assert.True(r.Refusal!.Contains(expect, StringComparison.Ordinal), r.Refusal);
        Assert.Equal(1, r.Refusal.Split(". ").Length);
        Assert.Empty(r.Keys);
    }
}
