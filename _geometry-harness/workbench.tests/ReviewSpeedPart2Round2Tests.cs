using System.Numerics;
using HarnessShared;
using HousecarlCore;
using Mutagen.Bethesda;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Skyrim;
using Noggog;
using ViewProbe;
using Workbench;
using Xunit;

namespace WorkbenchTests;

/// <summary>A world for item 5's server reads: a house with a door paired with a door in another cell, an enable child,
/// a linked reference and a misc item; and a crowd of 205 doors enabled by one host, to pass the server's 200 cap.</summary>
public sealed class DoorWorld : IDisposable
{
    internal static readonly ModKey Master = new("HcWbDoors", ModType.Master);
    static FormKey F(uint id) => new(Master, id);
    internal static readonly FormKey House = F(0x900), DoorA = F(0x901), Child = F(0x902), Linked = F(0x903), Item = F(0x904), Crowd = F(0x905), DoorB = F(0x920);
    internal static readonly FormKey CellA = F(0x810), CellB = F(0x811);
    /// <summary>Door A's own destination: where a player going in through A arrives, on B's side.</summary>
    internal static readonly Vec3 ADest = new(510, 480, 0);
    /// <summary>Door B's destination: the doorstep outside A, where a player coming out through A arrives.</summary>
    internal static readonly Vec3 BDest = new(90, -40, 0);
    internal const int CrowdSize = 205;

    public string Root { get; }
    public string Instance => Path.Combine(Root, "instance");
    public string DataDir => Path.Combine(Root, "game", "Data");

    public DoorWorld()
    {
        Root = Path.Combine(Path.GetTempPath(), "hc-wb-doors-" + Guid.NewGuid().ToString("N"));
        var profile = Path.Combine(Instance, "profiles", "Default");
        var mods = Path.Combine(Instance, "mods");
        Directory.CreateDirectory(profile); Directory.CreateDirectory(mods); Directory.CreateDirectory(DataDir);
        var rel = SkyrimRelease.SkyrimSE;
        var m = new SkyrimMod(Master, rel);
        static ObjectBounds B(short x, short y, short z) => new() { First = new P3Int16((short)-x, (short)-y, 0), Second = new P3Int16(x, y, z) };
        var stat = new Static(F(0x801), rel) { EditorID = "HcWbHouse", ObjectBounds = B(100, 100, 200) };
        var door = new Door(F(0x802), rel) { EditorID = "HcWbDoor", ObjectBounds = B(10, 5, 100) };
        var misc = new MiscItem(F(0x803), rel) { EditorID = "HcWbItem", ObjectBounds = B(5, 5, 5) };
        m.Statics.Add(stat); m.Doors.Add(door); m.MiscItems.Add(misc);
        PlacedObject P(FormKey k, IPlaceableObjectGetter b, float x, float y, float z) => new(k, rel)
        {
            Base = b.ToNullableLink<IPlaceableObjectGetter>(),
            Placement = new Placement { Position = new P3Float(x, y, z), Rotation = new P3Float(0, 0, 0) },
        };
        static void To(PlacedObject r, FormKey door, Vec3 at)
        {
            var td = new TeleportDestination { Position = new P3Float(at.X, at.Y, at.Z), Rotation = new P3Float(0, 0, 0) };
            td.Door.SetTo(door);
            r.TeleportDestination = td;
        }
        static void EnabledBy(PlacedObject r, FormKey host) { r.EnableParent = new EnableParent(); r.EnableParent.Reference.SetTo(host); }

        var a = new Cell(CellA, rel) { EditorID = "HcWbOutside", Flags = Cell.Flag.IsInteriorCell };
        a.Temporary.Add(P(House, stat, 0, 0, 0));
        var da = P(DoorA, door, 105, 0, 0); To(da, DoorB, ADest); a.Temporary.Add(da);
        var child = P(Child, stat, 600, 0, 0); EnabledBy(child, House); a.Temporary.Add(child);
        var linked = P(Linked, stat, 900, 0, 0); var l = new LinkedReferences(); l.Reference.SetTo(House); linked.LinkedReferences.Add(l); a.Temporary.Add(linked);
        a.Temporary.Add(P(Item, misc, 0, 0, 200.5f));
        a.Temporary.Add(P(Crowd, stat, 5000, 5000, 0));
        for (int i = 0; i < CrowdSize; i++)
        {
            var d = P(F(0xA00 + (uint)i), door, 5000 + (i % 15) * 8, 5000 + (i / 15) * 8, 0);
            EnabledBy(d, Crowd); To(d, DoorB, ADest); a.Temporary.Add(d);
        }
        var b = new Cell(CellB, rel) { EditorID = "HcWbInside", Flags = Cell.Flag.IsInteriorCell };
        var db = P(DoorB, door, 500, 500, 0); To(db, DoorA, BDest); b.Temporary.Add(db);
        var block = new CellBlock { BlockNumber = 2, GroupType = GroupTypeEnum.InteriorCellBlock };
        var sub = new CellSubBlock { BlockNumber = 0, GroupType = GroupTypeEnum.InteriorCellSubBlock };
        sub.Cells.Add(a); sub.Cells.Add(b); block.SubBlocks.Add(sub); m.Cells.Records.Add(block);
        var dir = Path.Combine(mods, "DoorMod"); Directory.CreateDirectory(dir);
        m.BeginWrite.ToPath(Path.Combine(dir, Master.FileName.String)).WithLoadOrder(Array.Empty<ISkyrimModGetter>()).Write();
        File.WriteAllText(Path.Combine(profile, "loadorder.txt"), "# header\r\n" + Master.FileName + "\r\n");
        File.WriteAllText(Path.Combine(profile, "plugins.txt"), "*" + Master.FileName + "\r\n");
        File.WriteAllText(Path.Combine(profile, "modlist.txt"), "# header\r\n+DoorMod\r\n");
    }

    public void Dispose() { try { Directory.Delete(Root, true); } catch { /* best effort */ } }
}

/// <summary>C1: item 5's server reads through the built server on the door world.</summary>
[Trait("tier", "integration")]
public class ServerContextSourceTests : IClassFixture<DoorWorld>
{
    readonly DoorWorld _w;
    public ServerContextSourceTests(DoorWorld w) => _w = w;

    Task<PatchWriter> Start() => PatchWriter.StartAsync(
        Path.Combine(ReviewSpeedPart2Tests.RepoRoot(), "src", "housecarl-mcp", "bin", "Release", "net9.0", "housecarl-mcp.exe"),
        new WriteTarget(_w.Instance, "Default", _w.DataDir, Path.Combine(_w.Instance, "mods")),
        Path.Combine(_w.Root, "hcdata-" + Guid.NewGuid().ToString("N")), TestCorpus.Path, _ => { });

    [Fact]
    public async Task C1a_the_server_reads_name_the_door_its_far_side_the_far_doors_destination_what_names_it_and_base_types()
    {
        var writer = Start();
        IContextSource src = new ServerContextSource(writer, _ => null);
        try
        {
            var (doors, _) = await src.DoorsNearCountedAsync(DoorWorld.House, ContextRead.DoorRadius);
            Assert.Contains(DoorWorld.DoorA.ToString(), doors);
            Assert.DoesNotContain(DoorWorld.DoorB.ToString(), doors);
            var (far, cell, at) = await src.DestinationAsync(DoorWorld.DoorA.ToString());
            Assert.Equal(DoorWorld.DoorB.ToString(), far);
            Assert.Equal(DoorWorld.CellB.ToString(), cell);
            Assert.Equal(DoorWorld.BDest, at);                       // the far door's destination: the doorstep outside A
            var (names, _) = await src.NamesCountedAsync(DoorWorld.House);
            Assert.Contains((DoorWorld.Child.ToString(), "EnableParent"), names);
            Assert.Contains((DoorWorld.Linked.ToString(), "LinkedReferences"), names);
            Assert.Equal(DoorWorld.House.ToString(), await src.EnableParentAsync(DoorWorld.Child));
            Assert.Equal("MiscItem", await src.BaseTypeAsync(DoorWorld.Item.ToString()));
            Assert.Equal("Static", await src.BaseTypeAsync(DoorWorld.House.ToString()));
        }
        finally { await (await writer).DisposeAsync(); }
    }

    [Fact]
    public async Task C1b_more_than_the_server_lists_is_counted_never_cut_silently()
    {
        var writer = Start();
        IContextSource src = new ServerContextSource(writer, _ => null);
        try
        {
            var (names, notListed) = await src.NamesCountedAsync(DoorWorld.Crowd);
            Assert.True(names.Count + notListed >= DoorWorld.CrowdSize, $"{names.Count} listed and {notListed} counted of {DoorWorld.CrowdSize}");
            Assert.True(notListed > 0, "the cut list said nothing about the rest");
            var (doors, notListedDoors) = await src.DoorsNearCountedAsync(DoorWorld.Crowd, ContextRead.DoorRadius);
            Assert.True(doors.Count + notListedDoors >= DoorWorld.CrowdSize && notListedDoors > 0, $"{doors.Count} doors listed and {notListedDoors} counted");
        }
        finally { await (await writer).DisposeAsync(); }
    }

    [Fact]
    public async Task C1c_the_placement_check_refuses_a_record_the_scene_draws_elsewhere()
    {
        var writer = Start();
        try
        {
            IContextSource same = new ServerContextSource(writer, _ => new RefPlacementEdit.Pose(new Vec3(0, 0, 0), new Vec3(0, 0, 0), 1f));
            Assert.Null(await same.MismatchAsync(DoorWorld.House));
            IContextSource moved = new ServerContextSource(writer, _ => new RefPlacementEdit.Pose(new Vec3(40, 0, 0), new Vec3(0, 0, 0), 1f));
            Assert.NotNull(await moved.MismatchAsync(DoorWorld.House));
        }
        finally { await (await writer).DisposeAsync(); }
    }
}

/// <summary>Gate C round 2 on synthetic data: the counted reads in words (C1), clear's two limits and the contact words (C2),
/// findings-only scenes behind a toggle, folded Findings runs and the list's size (C4), and cell names in headers (C5).</summary>
[Trait("tier", "unit")]
public sealed class ReviewSpeedPart2Round2Tests
{
    sealed class Counted : IContextSource
    {
        public Task<IReadOnlyList<string>> DoorsNearAsync(FormKey key, double within) => Task.FromResult<IReadOnlyList<string>>(new[] { "000D01:HcWbTest.esp" });
        public Task<IReadOnlyList<(string Key, string Field)>> NamesAsync(FormKey key) => Task.FromResult<IReadOnlyList<(string, string)>>(new[] { ("000C01:HcWbTest.esp", "EnableParent") });
        public Task<(IReadOnlyList<string> Doors, int NotListed)> DoorsNearCountedAsync(FormKey key, double within) =>
            Task.FromResult<(IReadOnlyList<string>, int)>((new[] { "000D01:HcWbTest.esp" }, 4));
        public Task<(IReadOnlyList<(string Key, string Field)> Names, int NotListed)> NamesCountedAsync(FormKey key) =>
            Task.FromResult<(IReadOnlyList<(string, string)>, int)>((new[] { ("000C01:HcWbTest.esp", "EnableParent") }, 7));
        public Task<(string? FarDoor, string? FarCell, Vec3? Arrival)> DestinationAsync(string door) => Task.FromResult(((string?)null, (string?)null, (Vec3?)null));
        public Task<string?> BaseTypeAsync(string key) => Task.FromResult<string?>(null);
        public Task<string?> EnableParentAsync(FormKey key) => Task.FromResult<string?>(null);
        public Task<string?> MismatchAsync(FormKey key) => Task.FromResult<string?>(null);
    }

    static CaptureViews.Geometry G(params RefOcclusion[] refs) => CaptureViews.FromPickShapes(refs.SelectMany(r => r.Shapes));

    // C1: a cut list says how many more there are
    [Fact]
    public async Task C1d_the_panel_says_how_many_more_the_server_did_not_list()
    {
        var g = G(Boxes.Box(0x800, new Vec3(0, 0, 0), new Vector3(20, 20, 20)));
        var a = await ContextRead.ReadAsync(new Counted(), g, Boxes.Key(0x800).ToString(), Array.Empty<(FormKey, int, Vector3, Vector3, Vector3)>(), _ => null);
        Assert.Contains("7 more not listed", a.Sections["names"]);
        Assert.Contains("4 more not listed", a.Sections["doors"]);
    }

    // C2: the contact read in words, a crossing apart from a near touch
    [Fact]
    public void C2a_the_contact_words_say_crosses_or_within_8_units()
    {
        var g = G(Boxes.Box(0x800, new Vec3(0, 0, 0), new Vector3(10, 10, 10), name: "Barrel"),
                  Boxes.Box(0x801, new Vec3(5, 4, 3), new Vector3(10, 10, 10), name: "Drift"),        // offset on every axis: its faces cross, none coplanar
                  Boxes.Box(0x802, new Vec3(23, 0, 0), new Vector3(10, 10, 10), name: "Thicket"));
        var words = ContextRead.ContactWords(CaptureViews.Contacts(g, Boxes.Key(0x800).ToString()), g);
        Assert.Contains(words, w => w.Contains("000801") && w.Contains("crosses it"));
        Assert.Contains(words, w => w.Contains("000802") && w.Contains("is within 8 units of it"));
        Assert.DoesNotContain(words, w => w.Contains("touches or crosses"));
    }

    [Fact]
    public void C2b_clear_names_both_limits()
    {
        Assert.Contains("scenery no mod is responsible for", ClearRule.ClearLimit);
        Assert.Contains("a crossing already there before the move is not counted", ClearRule.ClearLimit);
    }

    // C4: findings-only scenes behind a toggle, counted
    static RankedRow Row(int rank, string refKey, string cell, string summary = "") =>
        new(rank, new ConflictRow($"row-{rank}", "geometry.object-overlap", "OVERLAP", "Subject.esp", "Other.esp", summary, refKey, null, PhysicalState.Holds, cell,
                                  "Static", "S", "Static", "O", "calibrated-inference", false), false, 0, "", 0, "", 0, "", 0, "");

    static RankedFinding Finding(int rank, string cell) =>
        new(rank, new FindingRow("geometry.navmesh-ownership", "NAVMESH", "Subject.esp", "Other.esp", $"finding {rank}", $"{0xE00 + rank:X6}:HcWbTest.esp", cell, null, "finding", $"f{rank}"),
            false, "", 0);

    static SceneKeyPolicy Policy() => SceneKeyPolicy.Load(ReviewSpeedPart2Tests.SkillPolicy());
    static (Decision?, DecisionMatch) Undecided(RankedRow r) => (null, new DecisionMatch(Standing.Undecided, "undecided"));
    const string C1 = "000100:HcWbTest.esp";

    [Fact]
    public void C4a_findings_only_cells_are_left_out_by_default_and_counted()
    {
        var rows = new[] { Row(1, "000801:HcWbTest.esp", C1) };
        var findings = new[] { Finding(1, C1), Finding(2, "000200:HcWbTest.esp"), Finding(3, "000200:HcWbTest.esp"), Finding(4, "000300:HcWbTest.esp") };
        var v = ReviewList.BuildScenesView(rows, findings, Policy(), Undecided, false, includeFindingsOnly: false);
        Assert.Single(v.Scenes);
        Assert.Equal(2, v.FindingsOnlyCells);
        Assert.Equal(3, v.FindingsOnlyRows);
        var all = ReviewList.BuildScenesView(rows, findings, Policy(), Undecided, false, includeFindingsOnly: true);
        Assert.Equal(3, all.Scenes.Count);
        Assert.Equal(0, all.FindingsOnlyCells);
    }

    [Fact]
    public void C4b_a_long_findings_run_folds_and_the_left_out_cells_are_said()
    {
        var rows = new[] { Row(1, "000801:HcWbTest.esp", C1) };
        var findings = Enumerable.Range(1, 50).Select(i => Finding(i, C1)).Append(Finding(51, "000200:HcWbTest.esp")).ToList();
        var byRank = findings.ToDictionary(f => f.Rank);
        var v = ReviewList.BuildScenesView(rows, findings, Policy(), Undecided, false, includeFindingsOnly: false);
        var items = ReviewList.Items(v, byRank, ReviewList.FindingsFoldAt, new HashSet<int>());
        Assert.Equal(ReviewList.FindingsFoldAt, items.Count(i => i.Kind == "finding"));
        Assert.Contains(items, i => i.Kind == "fold" && i.Text.Contains("30 more Findings rows"));
        Assert.Contains(items, i => i.Kind == "count" && i.Text.Contains("1 more cell") && i.Text.Contains("1 row"));
        var open = ReviewList.Items(v, byRank, ReviewList.FindingsFoldAt, new HashSet<int> { v.Scenes[0].Scene.Number });
        Assert.Equal(50, open.Count(i => i.Kind == "finding"));
    }

    // C4: one rebuild at the lab bundle's size, pinned by the number of items it builds
    [Fact]
    public void C4c_a_rebuild_at_the_bundles_size_builds_a_bounded_number_of_items()
    {
        var rows = Enumerable.Range(1, 19).Select(i => Row(i, $"{0x800 + i:X6}:HcWbTest.esp", i <= 9 ? C1 : "000101:HcWbTest.esp")).ToList();
        var findings = new List<RankedFinding>();
        int rank = 1;
        for (int c = 0; c < 257; c++) for (int k = 0; k < (c == 0 ? 808 : 8); k++) findings.Add(Finding(rank++, $"{0x2000 + c:X6}:HcWbTest.esp"));
        var byRank = findings.ToDictionary(f => f.Rank);
        var v = ReviewList.BuildScenesView(rows, findings, Policy(), Undecided, false, includeFindingsOnly: false);
        var items = ReviewList.Items(v, byRank, ReviewList.FindingsFoldAt, new HashSet<int>());
        Assert.True(items.Count is > 0 and <= 2 + 19 + 1, $"the default list built {items.Count} items");
        var all = ReviewList.Items(ReviewList.BuildScenesView(rows, findings, Policy(), Undecided, false, includeFindingsOnly: true), byRank, ReviewList.FindingsFoldAt, new HashSet<int>());
        Assert.True(all.Count <= 259 + 19 + 257 * (ReviewList.FindingsFoldAt + 1), $"the full list built {all.Count} items");
        Assert.True(all.Count < findings.Count, $"the full list built {all.Count} items for {findings.Count} Findings rows: nothing folded");
    }

    // C5: the header names the cell, from the rows' own summary
    [Fact]
    public void C5u_a_scene_header_names_its_cell()
    {
        var rows = new[] { Row(1, "000801:HcWbTest.esp", C1, "Container from A / Tree from B: crossing, 43 triangle pairs in WindhelmExterior02 (ext) [mm]") };
        var v = ReviewList.BuildScenesView(rows, Array.Empty<RankedFinding>(), Policy(), Undecided, false, includeFindingsOnly: false);
        Assert.Contains("WindhelmExterior02", Assert.Single(v.Scenes).Header);
    }
}
