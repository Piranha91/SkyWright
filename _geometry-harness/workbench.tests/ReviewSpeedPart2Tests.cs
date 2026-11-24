using System.Diagnostics;
using System.Numerics;
using HarnessShared;
using HousecarlCore;
using SkyWright.Engine;
using Mutagen.Bethesda.Plugins;
using ViewProbe;
using Workbench;
using Xunit;
using Xunit.Abstractions;

namespace WorkbenchTests;

/// <summary>Part 2 of the review-speed task on synthetic data: item 7 (scenes), item 5 (the context of a selected
/// reference) and item 8 (candidate positions judged by the re-check), with the Gate A review's findings P1-P9.</summary>
[Trait("tier", "unit")]
public sealed class ReviewSpeedPart2Tests : IDisposable
{
    readonly ITestOutputHelper _out;
    readonly string _root = Path.Combine(Path.GetTempPath(), "hc-wb-p2-" + Guid.NewGuid().ToString("N"));
    public ReviewSpeedPart2Tests(ITestOutputHelper o) { _out = o; Directory.CreateDirectory(_root); }
    public void Dispose() { try { Directory.Delete(_root, true); } catch { } }

    // ---------------------------------------------------------------- item 7

    internal static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, HarnessShared.CheckoutLayout.Marker))) dir = dir.Parent;
        Assert.NotNull(dir);
        return dir!.FullName;
    }

    internal static string SkillPolicy() => Path.Combine(HarnessShared.CheckoutLayout.Housecarl(RepoRoot()), ".claude", "skills", "evaluation-reporting", "references", "scene-policy.json");

    static RankedRow Row(int rank, string refKey, string? other, string cell = "000100:HcWbTest.esp", string check = "geometry.object-overlap",
                         string kind = "OVERLAP", string a = "Subject.esp", string b = "Other.esp") =>
        new(rank, new ConflictRow($"row-{rank}", check, kind, a, b, $"row {rank}", refKey, other, PhysicalState.Holds, cell,
                                  "Static", "S", "Static", "O", "calibrated-inference", false),
            false, 0, "", 0, "", 0, "", 0, "");

    static RankedFinding Finding(int rank, string? refKey, string? cell) =>
        new(rank, new FindingRow("geometry.navmesh-ownership", "NAVMESH", "Subject.esp", "Other.esp", $"finding {rank}", refKey, cell, null, "finding", $"f{rank}"),
            false, "", 0);

    static SceneKeyPolicy Policy() => SceneKeyPolicy.Load(SkillPolicy());

    const string C1 = "000100:HcWbTest.esp", C2 = "000200:HcWbTest.esp";

    [Fact]
    public void G7a_rows_in_one_cell_sharing_a_reference_are_one_cluster_of_one_scene()
    {
        var s = Scenes.Build(new[] { Row(1, "000801:HcWbTest.esp", "000901:HcWbTest.esp"), Row(2, "000801:HcWbTest.esp", "000902:HcWbTest.esp") },
                             Array.Empty<RankedFinding>(), Policy());
        var scene = Assert.Single(s.Scenes);
        var cluster = Assert.Single(scene.Clusters);
        Assert.Equal(new[] { 1, 2 }, cluster.Ranks);
    }

    // P1: the scene is the cell; rows of one cell with no common reference are two clusters of ONE scene
    [Fact]
    public void G7b_rows_in_one_cell_with_no_common_reference_are_one_scene_with_two_clusters()
    {
        var s = Scenes.Build(new[] { Row(1, "000801:HcWbTest.esp", "000901:HcWbTest.esp"), Row(2, "000802:HcWbTest.esp", "000902:HcWbTest.esp") },
                             Array.Empty<RankedFinding>(), Policy());
        var scene = Assert.Single(s.Scenes);
        Assert.Equal(C1, scene.Cell);
        Assert.Equal(2, scene.Clusters.Count);
    }

    [Fact]
    public void G7c_clusters_join_through_a_chain_of_common_references_and_compare_without_case()
    {
        var s = Scenes.Build(new[]
        {
            Row(1, "000801:HcWbTest.esp", "000802:HcWbTest.esp"), Row(2, "000802:hcwbtest.esp", "000803:HcWbTest.esp"),
            Row(3, "000803:HcWbTest.esp", "000804:HcWbTest.esp"), Row(4, "000805:HcWbTest.esp", "000806:HcWbTest.esp"),
        }, Array.Empty<RankedFinding>(), Policy());
        var scene = Assert.Single(s.Scenes);
        Assert.Equal(new[] { "1,2,3", "4" }, scene.Clusters.Select(c => string.Join(",", c.Ranks)).OrderBy(x => x));
    }

    [Fact]
    public void G7d_equal_group_key_values_fold_into_one_group_and_another_pair_splits_it()
    {
        var s = Scenes.Build(new[]
        {
            Row(1, "000801:HcWbTest.esp", "000901:HcWbTest.esp"), Row(2, "000801:HcWbTest.esp", "000902:HcWbTest.esp"),
            Row(3, "000801:HcWbTest.esp", "000903:HcWbTest.esp", b: "Third.esp"),
        }, Array.Empty<RankedFinding>(), Policy());
        var cluster = Assert.Single(Assert.Single(s.Scenes).Clusters);
        Assert.Equal(new[] { "1,2", "3" }, cluster.Groups.Select(g => string.Join(",", g.Ranks)).OrderBy(x => x));
    }

    [Fact]
    public void G7e_rows_sharing_a_reference_across_two_cells_are_two_scenes()
    {
        var s = Scenes.Build(new[] { Row(1, "000801:HcWbTest.esp", "000901:HcWbTest.esp", C1), Row(2, "000801:HcWbTest.esp", "000902:HcWbTest.esp", C2) },
                             Array.Empty<RankedFinding>(), Policy());
        Assert.Equal(new[] { C1, C2 }, s.Scenes.Select(x => x.Cell).OrderBy(x => x));
    }

    [Fact]
    public void G7f_scenes_rank_by_their_best_row_and_are_numbered_in_that_order()
    {
        var s = Scenes.Build(new[] { Row(1, "000801:HcWbTest.esp", null, C2), Row(2, "000802:HcWbTest.esp", null, C1), Row(3, "000803:HcWbTest.esp", null, C1) },
                             Array.Empty<RankedFinding>(), Policy());
        Assert.Equal(new[] { C2, C1 }, s.Scenes.Select(x => x.Cell));
        Assert.Equal(new int?[] { 1, 2 }, s.Scenes.Select(x => x.BestRank));
        Assert.Equal(new[] { 1, 2 }, s.Scenes.Select(x => x.Number));
    }

    [Fact]
    public void G7g_the_key_names_come_from_the_skill_file_and_an_unknown_name_is_refused()
    {
        var rows = new[] { Row(1, "000801:HcWbTest.esp", "000901:HcWbTest.esp"), Row(2, "000801:HcWbTest.esp", "000902:HcWbTest.esp", b: "Third.esp") };
        Assert.Equal(2, Assert.Single(Assert.Single(Scenes.Build(rows, Array.Empty<RankedFinding>(), Policy()).Scenes).Clusters).Groups.Count);
        var narrow = Path.Combine(_root, "narrow.json");
        File.WriteAllText(narrow, File.ReadAllText(SkillPolicy()).Replace("\"groupKey\": [\"type\", \"pair\", \"cell\", \"field\"]", "\"groupKey\": [\"type\", \"cell\"]"));
        var folded = Scenes.Build(rows, Array.Empty<RankedFinding>(), SceneKeyPolicy.Load(narrow));
        Assert.Single(Assert.Single(Assert.Single(folded.Scenes).Clusters).Groups);
        var odd = Path.Combine(_root, "odd.json");
        File.WriteAllText(odd, File.ReadAllText(SkillPolicy()).Replace("\"groupKey\": [\"type\", \"pair\", \"cell\", \"field\"]", "\"groupKey\": [\"type\", \"storey\"]"));
        var ex = Assert.Throws<InvalidOperationException>(() => SceneKeyPolicy.Load(odd));
        Assert.Contains("storey", ex.Message);
    }

    // P3: the group key's type is the skill's type by check id, not the row's kind
    [Fact]
    public void G7i_two_checks_the_skill_calls_one_type_fold_together_and_an_unmapped_check_takes_the_other_type()
    {
        var p = Policy();
        Assert.Equal("contact", p.TypeOf("geometry.placement-contact"));
        Assert.Equal("contact", p.TypeOf("geometry.fixture-empty-space"));
        Assert.Equal("other", p.TypeOf("geometry.no-such-check"));
        var s = Scenes.Build(new[]
        {
            Row(1, "000801:HcWbTest.esp", "000901:HcWbTest.esp", check: "geometry.placement-contact", kind: "CONTACT"),
            Row(2, "000801:HcWbTest.esp", "000902:HcWbTest.esp", check: "geometry.fixture-empty-space", kind: "EMPTY"),
        }, Array.Empty<RankedFinding>(), p);
        Assert.Single(Assert.Single(Assert.Single(s.Scenes).Clusters).Groups);
    }

    // P2: a cell's clickable Findings rows are listed in its scene; rows naming no cell are counted, not placed
    [Fact]
    public void G7j_clickable_findings_are_listed_in_their_cells_scene_and_unplaceable_ones_are_counted()
    {
        var s = Scenes.Build(new[] { Row(1, "000801:HcWbTest.esp", null, C1) },
                             new[] { Finding(1, "000811:HcWbTest.esp", C1), Finding(2, null, null), Finding(3, "000812:HcWbTest.esp", C2) }, Policy(), includeFindingsOnly: true);
        var first = Assert.Single(s.Scenes, x => x.Cell == C1);
        Assert.Equal(new[] { 1 }, first.FindingRanks);
        Assert.Equal(1, s.UnplacedFindings);
        var findingsOnly = Assert.Single(s.Scenes, x => x.Cell == C2);
        Assert.Null(findingsOnly.BestRank);
        Assert.Equal(new[] { 3 }, findingsOnly.FindingRanks);
        Assert.Equal(C1, s.Scenes[0].Cell);                                   // a scene with Conflicts rows ranks first
    }

    // ---------------------------------------------------------------- item 5

    sealed class Source : IContextSource
    {
        internal List<string> Doors = new();
        internal List<(string, string)> Names = new();
        internal Dictionary<string, (string?, string?, Vec3?)> Far = new();
        internal Dictionary<string, string> BaseTypes = new();
        internal string? Mismatch, Parent;
        internal Exception? NamesFail;
        internal TaskCompletionSource? Gate;
        public async Task<IReadOnlyList<string>> DoorsNearAsync(FormKey key, double within) { if (Gate is { } g) await g.Task; return Doors; }
        public Task<IReadOnlyList<(string Key, string Field)>> NamesAsync(FormKey key) =>
            NamesFail is { } f ? Task.FromException<IReadOnlyList<(string, string)>>(f) : Task.FromResult<IReadOnlyList<(string, string)>>(Names);
        public Task<(string? FarDoor, string? FarCell, Vec3? Arrival)> DestinationAsync(string door) =>
            Task.FromResult(Far.TryGetValue(door, out var d) ? d : ((string?)null, (string?)null, (Vec3?)null));
        public Task<string?> BaseTypeAsync(string key) => Task.FromResult(BaseTypes.TryGetValue(key, out var t) ? t : null);
        public Task<string?> EnableParentAsync(FormKey key) => Task.FromResult(Parent);
        public Task<string?> MismatchAsync(FormKey key) => Task.FromResult(Mismatch);
    }

    const uint Building = 0x900, DoorTouching = 0x901, DoorNear = 0x902, Crate = 0x903, Plank = 0x904, Statue = 0x905;
    static string K(uint id) => Boxes.Key(id).ToString();

    /// <summary>A 400 x 400 x 300 building at the origin; a door 1 unit off its east face; a door 90 units off it; a misc item, a
    /// static and a moveable static standing on its roof.</summary>
    static CaptureViews.Geometry Town()
    {
        var shapes = new List<PickShape>();
        foreach (var ro in new[]
        {
            Boxes.Box(Building, new Vec3(0, 0, 0), new Vector3(200, 200, 150), name: "House"),
            Boxes.Box(DoorTouching, new Vec3(211, 0, 0), new Vector3(10, 40, 100), name: "DoorA"),   // 1 unit off the east face: in contact, not on its base edge
            Boxes.Box(DoorNear, new Vec3(300, 0, 0), new Vector3(10, 40, 100), name: "DoorB"),
            Boxes.Box(Crate, new Vec3(0, 0, 300.2f), new Vector3(10, 10, 10), name: "Basket"),
            Boxes.Box(Plank, new Vec3(80, 80, 300.2f), new Vector3(10, 10, 10), name: "Plank"),
            Boxes.Box(Statue, new Vec3(-80, -80, 300.2f), new Vector3(10, 10, 10), name: "Statue"),
        }) shapes.AddRange(ro.Shapes);
        return CaptureViews.FromPickShapes(shapes);
    }

    static readonly (FormKey, int, Vector3, Vector3, Vector3)[] NoNav = Array.Empty<(FormKey, int, Vector3, Vector3, Vector3)>();

    static Source TownSource() => new()
    {
        Doors = { K(DoorTouching), K(DoorNear) },
        Far = { [K(DoorTouching)] = ("000AAA:HcWbTest.esp", "00BBBB:HcWbTest.esp", new Vec3(1, 2, 3)) },
        BaseTypes = { [K(Crate)] = "MiscItem", [K(Plank)] = "Static", [K(Statue)] = "MoveableStatic", [K(DoorTouching)] = "Door", [K(DoorNear)] = "Door" },
    };

    [Fact]
    public async Task C5a_a_door_in_contact_reads_touches_it_and_one_only_near_reads_near_only()
    {
        var a = await ContextRead.ReadAsync(TownSource(), Town(), K(Building), NoNav, _ => null);
        Assert.Equal(2, a.Doors.Count);
        Assert.Equal("touches it", a.Doors.Single(d => d.Key == K(DoorTouching)).Label);
        Assert.Equal("near only", a.Doors.Single(d => d.Key == K(DoorNear)).Label);
    }

    [Fact]
    public async Task C5b_a_doors_other_side_is_named_from_its_teleport_destination()
    {
        var a = await ContextRead.ReadAsync(TownSource(), Town(), K(Building), NoNav, _ => null);
        var d = Assert.Single(a.Doors, x => x.Key == K(DoorTouching));
        Assert.Equal("000AAA:HcWbTest.esp", d.FarDoor);
        Assert.Equal("00BBBB:HcWbTest.esp", d.FarCell);
        Assert.Equal(new Vec3(1, 2, 3), d.Arrival);
    }

    [Fact]
    public async Task C5c_everything_that_names_it_is_listed_under_its_field_and_none_is_dropped()
    {
        var src = TownSource();
        src.Names.AddRange(new[] { ("000C01:HcWbTest.esp", "EnableParent"), ("000C02:HcWbTest.esp", "LinkedReferences"), ("000C03:HcWbTest.esp", "SomeOtherField") });
        var a = await ContextRead.ReadAsync(src, Town(), K(Building), NoNav, _ => null);
        Assert.Equal(new[] { "EnableParent", "LinkedReferences", "SomeOtherField" }, a.Names.Select(n => n.Field).OrderBy(x => x));
    }

    [Fact]
    public async Task C5d_what_rests_on_it_is_said_to_fall_stay_or_be_unknown_by_the_motion_rule()
    {
        var a = await ContextRead.ReadAsync(TownSource(), Town(), K(Building), NoNav, _ => null);
        Assert.Equal(3, a.Resting.Count);
        var basket = a.Resting.Single(r => r.Key == K(Crate));
        Assert.Equal(DependentMotion.Movable, basket.Motion);
        Assert.Contains("falls when the cell loads", basket.Sentence);
        Assert.Equal(DependentMotion.Fixed, a.Resting.Single(r => r.Key == K(Plank)).Motion);
        Assert.Contains("stays in the air", a.Resting.Single(r => r.Key == K(Plank)).Sentence);
        var statue = a.Resting.Single(r => r.Key == K(Statue));
        Assert.Equal(DependentMotion.Unknown, statue.Motion);          // a moveable static whose bodies were not read is not Fixed
        Assert.Contains("unknown", statue.Sentence);
    }

    [Fact]
    public void C5e_navmesh_triangles_under_the_boxs_xy_bounds_are_counted_by_navmesh_at_any_height()
    {
        var n1 = new FormKey(new ModKey("HcWbTest", ModType.Plugin), 0xA01);
        var n2 = new FormKey(new ModKey("HcWbTest", ModType.Plugin), 0xA02);
        var box = new Aabb(new Vec3(-200, -200, 0), new Vec3(200, 200, 300), false);
        var tris = new[]
        {
            (n1, 0, new Vector3(-10, -10, -500), new Vector3(10, -10, -500), new Vector3(0, 10, -500)),   // far below: under it all the same
            (n1, 1, new Vector3(150, 150, 0), new Vector3(190, 150, 0), new Vector3(170, 190, 0)),
            (n2, 0, new Vector3(900, 900, 0), new Vector3(950, 900, 0), new Vector3(925, 950, 0)),         // outside
        };
        var nav = ContextRead.NavUnder(tris, box);
        var only = Assert.Single(nav);
        Assert.Equal(n1.ToString(), only.Navmesh);
        Assert.Equal(2, only.Triangles);
    }

    // P6: move_with carries the host's rigid move, so a turned host swings its dependent along the arc and turns it
    [Fact]
    public void C5f_a_dependent_follows_a_turned_host_along_the_arc_and_turns_with_it()
    {
        var hostBefore = new RefPlacementEdit.Pose(new Vec3(0, 0, 0), new Vec3(0, 0, 0), 1f);
        var hostAfter = new RefPlacementEdit.Pose(new Vec3(100, 0, 0), new Vec3(0, 0, MathF.PI / 2), 1f);
        var dep = new RefPlacementEdit.Pose(new Vec3(50, 0, 10), new Vec3(0, 0, 0), 1f);
        var got = MoveRules.Carry(hostBefore, hostAfter, dep);
        Assert.Equal(10f, got.Position.Z, 3);
        Assert.Equal(50f, MathF.Sqrt(MathF.Pow(got.Position.X - 100, 2) + MathF.Pow(got.Position.Y, 2)), 2);   // same distance from the host
        Assert.True(MathF.Abs(got.Position.X - 150) > 1f, $"the dependent slid with the host's translation only: {got.Position}");
        var want = SpatialOverlap.RefrRotation(dep.Rotation) * SpatialOverlap.RefrRotation(hostAfter.Rotation);
        var have = SpatialOverlap.RefrRotation(got.Rotation);
        for (int r = 0; r < 3; r++) for (int c = 0; c < 3; c++) Assert.Equal(want[r, c], have[r, c], 3);
    }

    // P6: the commit sentence names the far door, its cell and the old arrival point
    [Fact]
    public async Task C5g_the_commit_sentence_says_what_stays_behind_and_for_a_ticked_door_names_its_far_side()
    {
        var a = await ContextRead.ReadAsync(TownSource(), Town(), K(Building), NoNav, _ => null);
        var none = ContextRead.StaysBehind(a, Array.Empty<string>());
        Assert.StartsWith("Nothing moves with it on its own.", none);
        Assert.Contains(K(DoorNear), none);
        Assert.Contains(K(Crate), none);
        var ticked = ContextRead.StaysBehind(a, new[] { K(DoorTouching) });
        Assert.Contains("000AAA:HcWbTest.esp", ticked);
        Assert.Contains("00BBBB:HcWbTest.esp", ticked);
        Assert.Contains("(1, 2, 3)", ticked);
        Assert.Contains("arrives at the old doorstep", ticked);
        Assert.Contains("navmesh", ticked);
    }

    [Fact]
    public async Task C5h_a_placement_mismatch_refuses_the_context_answer()
    {
        var src = TownSource(); src.Mismatch = "the server's record for it is not the one drawn";
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => ContextRead.ReadAsync(src, Town(), K(Building), NoNav, _ => null));
        Assert.Contains("not the one drawn", ex.Message);
    }

    [Fact]
    public async Task C5i_a_failed_read_is_its_sections_sentence_and_the_other_sections_still_answer()
    {
        var src = TownSource(); src.NamesFail = new IOException("the server closed the pipe");
        ContextAnswer? got = null;
        var thrown = await Record.ExceptionAsync(async () => got = await ContextRead.ReadAsync(src, Town(), K(Building), NoNav, _ => null));
        Assert.True(thrown is null, "one failed read failed the whole panel: " + thrown?.Message);
        var a = got!;
        Assert.True(a.Sections.TryGetValue("names", out var why), "the names section said nothing about its failure");
        Assert.Contains("the server closed the pipe", why);
        Assert.Equal(2, a.Doors.Count);
    }

    [Fact]
    public async Task C5j_a_reference_with_no_doors_and_nothing_naming_it_says_so_with_the_radius()
    {
        var a = await ContextRead.ReadAsync(new Source(), Town(), K(Plank), NoNav, _ => null);
        Assert.True(a.Sections.TryGetValue("doors", out var doors), "the doors section said nothing");
        Assert.Contains("within 256 units", doors);
        Assert.True(a.Sections.TryGetValue("names", out var names), "the names section said nothing");
        Assert.Contains("nothing names it", names);
        Assert.Contains("does not move with its host", a.Statement);
    }

    /// <summary>A building-sized reference (20,000 vertices) with <paramref name="n"/> small boxes along its roof edge, each
    /// touching it and resting on it.</summary>
    static (CaptureViews.Geometry G, string Key) BigTown(int n)
    {
        var verts = new List<Vector3>(); var tris = new List<int>();
        int side = 141;
        for (int y = 0; y < side; y++) for (int x = 0; x < side; x++) verts.Add(TexturedScene.YUp(new Vector3(x * 10, y * 10, 300)));
        for (int y = 0; y < side - 1; y++) for (int x = 0; x < side - 1; x++) { int i = y * side + x; tris.AddRange(new[] { i, i + 1, i + side, i + 1, i + side + 1, i + side }); }
        verts.Add(TexturedScene.YUp(new Vector3(700, 700, 0))); tris.AddRange(new[] { 0, side - 1, verts.Count - 1 });   // the walls' foot
        var key = Boxes.Key(0x990);
        var r = new SceneRef("meshes\\big.nif", new Vec3(0, 0, 0), new Vec3(0, 0, 0), 1f, false, RefKey: key, BaseEditorId: "BigHouse");
        var big = new PickShape { Kind = PickKind.Ref, Verts = verts.ToArray(), Tris = tris.ToArray(), Ref = r, Mesh = new SkyRender.GlMesh() };
        big.ComputeBounds();
        var shapes = new List<PickShape> { big };
        for (int i = 0; i < n; i++) shapes.AddRange(Boxes.Box((uint)(0xB00 + i), new Vec3(20 + i * 40, 20, 300.2f), new Vector3(8, 8, 8), name: "Crate").Shapes);
        return (CaptureViews.FromPickShapes(shapes), key.ToString());
    }

    // P7: the measurement taken before the plan freezes (passes; it prints what the read costs as Gate A planned it)
    [Fact]
    public void C5m_measure_the_contact_reads_of_a_building_sized_reference()
    {
        var (g, key) = BigTown(30);
        var sw = Stopwatch.StartNew();
        var host = CaptureViews.Contacts(g, key);
        var one = sw.Elapsed.TotalMilliseconds;
        foreach (var t in host.Touches) CaptureViews.Contacts(g, t);
        sw.Stop();
        _out.WriteLine($"building of {g.Refs[key].V.Count} vertices, {host.Touches.Count} touching: its own contacts {one:0} ms; " +
                       $"with a contacts read per touching reference (Gate A's plan) {sw.Elapsed.TotalMilliseconds:0} ms");
        Assert.Equal(30, host.Touches.Count);
    }

    // P7: the context read builds one triangle grid and casts at most the support rays of each touching reference
    [Fact]
    public async Task C5n_the_context_read_of_a_building_is_bounded_by_count_not_by_the_clock()
    {
        var (g, key) = BigTown(30);
        var counter = new CaptureViews.WorkCounter();
        var a = await ContextRead.ReadAsync(new Source(), g, key, NoNav, _ => null, counter: counter);
        _out.WriteLine($"grids {counter.Grids}, rays {counter.Rays}");
        Assert.Equal(30, a.Touches.Count);
        Assert.Equal(30, a.Resting.Count);
        Assert.True(counter.Grids == 1, $"the read built {counter.Grids} triangle grids; one (the building's) is allowed");
        Assert.True(counter.Rays > 0 && counter.Rays <= (30 + 1) * (CaptureViews.SupportRays + 1), $"the read cast {counter.Rays} rays");
    }

    // ---------------------------------------------------------------- item 8

    static Aabb Bx(float x0, float y0, float z0, float x1, float y1, float z1) => new(new Vec3(x0, y0, z0), new Vec3(x1, y1, z1), false);

    [Fact]
    public void P8a_candidates_are_the_smallest_separating_moves_at_one_and_one_and_a_half_times_nearest_first()
    {
        var subject = Bx(0, 0, 0, 20, 20, 20);
        var other = Bx(10, -10, 0, 50, 30, 40);
        var three = Candidates.Generate(subject, other);
        Assert.Equal(new[] { (-10f, 0f), (-15f, 0f), (0f, 30f) }, three.Select(c => (c.Move.X, c.Move.Y)));
        Assert.All(three, c => Assert.Equal(0f, c.Move.Z));
        Assert.Equal(new[] { 1, 2, 3 }, three.Select(c => c.N));
        Assert.All(three, c => Assert.Contains("separate", c.Rule));
        Assert.Equal(6, Candidates.Generate(subject, other, 6).Count);
    }

    static RowVerdict V(string id, string word) => new(id, word, word, word + ": sentence.");

    [Fact]
    public void P4a_clear_says_its_limit_in_its_own_sentence()
    {
        var v = ClearRule.Judge(V("open", "resolved"), new[] { V("other", "resolved") }, Array.Empty<RowVerdict>(), Array.Empty<string>());
        Assert.Equal("clear", v.Word);
        Assert.Contains(ClearRule.ClearLimit, v.Sentence);
        Assert.Contains("scenery no mod is responsible for", v.Sentence);
    }

    // P4: a carried predicate still holding for the moved reference keeps it from clear even with no other row's word
    [Fact]
    public void P4b_a_still_holding_carried_predicate_reads_resolved_here_but_and_is_named()
    {
        var v = ClearRule.Judge(V("open", "resolved"), Array.Empty<RowVerdict>(), Array.Empty<RowVerdict>(), new[] { "09A3BB:Skyrim.esm / 09A3C4:Skyrim.esm: holds" });
        Assert.Equal("resolved here, but", v.Word);
        Assert.Contains(v.Rows, r => r.Contains("09A3C4"));
    }

    [Fact]
    public void P4c_a_row_absent_from_both_runs_keeps_its_own_word()
    {
        var v = ClearRule.Judge(V("open", "not in either run"), Array.Empty<RowVerdict>(), Array.Empty<RowVerdict>(), Array.Empty<string>());
        Assert.Equal("not in either run", v.Word);
    }

    [Fact]
    public void P8c_the_open_row_resolved_with_another_row_of_the_reference_reclassified_reads_resolved_here_but()
    {
        var v = ClearRule.Judge(V("open", "resolved"), new[] { V("overlap|09a3bb|09a3c4", "reclassified") }, Array.Empty<RowVerdict>(), Array.Empty<string>());
        Assert.Equal("resolved here, but", v.Word);
        Assert.Contains("overlap|09a3bb|09a3c4", v.Rows);
        Assert.Contains("reclassified", v.Sentence);
    }

    [Fact]
    public void P8d_a_created_row_reads_new_conflict_and_is_named()
    {
        var v = ClearRule.Judge(V("open", "resolved"), Array.Empty<RowVerdict>(), new[] { V("overlap|049e54|09a3bb", "new") }, Array.Empty<string>());
        Assert.Equal("new conflict", v.Word);
        Assert.Contains("overlap|049e54|09a3bb", v.Rows);
    }

    [Fact]
    public void P8l_the_open_rows_other_words_pass_through()
    {
        Assert.Equal("still there", ClearRule.Judge(V("open", "still there"), Array.Empty<RowVerdict>(), Array.Empty<RowVerdict>(), Array.Empty<string>()).Word);
        Assert.Equal("reclassified", ClearRule.Judge(V("open", "reclassified"), Array.Empty<RowVerdict>(), Array.Empty<RowVerdict>(), Array.Empty<string>()).Word);
    }

    [Fact]
    public void P4d_still_holding_predicates_are_read_from_the_patched_emit_for_the_moved_reference_only()
    {
        var emit = Path.Combine(_root, "patched.json");
        File.WriteAllText(emit, """
            { "Predicates": [
              { "Kind": "OVERLAP", "FormKey": "09A3BB:Skyrim.esm", "Other": "09A3C4:Skyrim.esm", "Physical": "Holds", "Detail": "crossing; 109 triangle pairs" },
              { "Kind": "OVERLAP", "FormKey": "09A3BB:Skyrim.esm", "Other": "09A3C2:Skyrim.esm", "Physical": "Gone", "Detail": "separated" },
              { "Kind": "OVERLAP", "FormKey": "049E59:Clockwork.esp", "Other": "09A3C5:Skyrim.esm", "Physical": "Holds", "Detail": "crossing" },
              { "Kind": "OVERLAP", "FormKey": "049E53:Clockwork.esp", "Other": "09a3bb:skyrim.esm", "Physical": "Holds", "Detail": "crossing; 2 triangle pairs" }
            ] }
            """);
        var held = ClearRule.StillHolding(emit, "09A3BB:Skyrim.esm");
        Assert.Equal(2, held.Count);
        Assert.Contains(held, h => h.Contains("09A3C4") && h.Contains("holds"));
        Assert.Contains(held, h => h.Contains("049E53"));
    }

    // the trial lane on fakes: a writer that records, a re-check that answers by candidate number
    sealed class Lane
    {
        internal readonly List<(PendingEdit Edit, string Patch, string OutPath)> Writes = new();
        internal readonly List<string> WorkDirs = new();
        internal TaskCompletionSource? Gate, WriteGate;
        internal Func<int, TrialRecheck> Answer = n => Recheck("resolved");
        internal Func<RefPlacementEdit.Pose, (RefPlacementEdit.Pose, string)> Settle = p => (p, "lowered it 2 units onto the terrain");
        internal Func<RefPlacementEdit.Pose, IReadOnlyList<string>> ContactsAt = _ => Array.Empty<string>();
        internal TrialLane Make(string? work) => new(work,
            async (e, patch, outPath) => { if (WriteGate is { } wg) await wg.Task; Writes.Add((e, patch, outPath)); return "wrote " + patch; },
            async (patch, folder, workDir) => { WorkDirs.Add(workDir); if (Gate is { } g) await g.Task; return Answer(WorkDirs.Count); },
            p => Settle(p), p => ContactsAt(p));
    }

    internal static TrialRecheck Recheck(string word, params RowVerdict[] created) =>
        new(new RecheckResult(V("open", word), created, "verdict : DIFFERENTIAL", Array.Empty<string>(), "overlapprobe: the bundle's own build", 70),
            Array.Empty<RowVerdict>(), Array.Empty<string>(), "population: 1 plugin(s), 1 cell, seeds 1");

    static readonly FormKey Subject = Boxes.Key(0x800);
    static readonly RefPlacementEdit.Pose Home = new(new Vec3(100, 100, 0), new Vec3(0, 0, 0.5f), 1f);
    static IReadOnlyList<Candidate> Three() => Candidates.Generate(Bx(90, 90, 0, 110, 110, 20), Bx(100, 80, 0, 140, 120, 40)) is { Count: 3 } c ? c
        : new[] { new Candidate(1, new Vector3(-30, 0, 0), "separate -x"), new Candidate(2, new Vector3(-45, 0, 0), "separate -x 1.5"), new Candidate(3, new Vector3(0, 40, 0), "separate +y") };

    static async Task Done(TrialLane lane) { if (lane.Running is { } t) await t; }

    [Fact]
    public void P8e_the_cost_is_said_before_any_run()
    {
        var f = new Lane { WriteGate = new TaskCompletionSource() };
        var lane = f.Make(_root);
        var said = lane.Start(Subject, C1, Home, Three(), "overlapprobe", 0, false);
        Assert.Contains("3 positions", said);
        Assert.Contains("minutes", said);
        Assert.Empty(f.Writes);                                               // the writer is the first thing a trial does
        Assert.Empty(f.WorkDirs);
        f.WriteGate.SetResult();
        Assert.Contains("minutes", TrialLane.CostSentence(3));
    }

    [Fact]
    public async Task P8f_each_trial_is_its_own_patch_under_the_work_folders_trials_folder()
    {
        var f = new Lane();
        var lane = f.Make(_root);
        var said = lane.Start(Subject, C1, Home, Three(), "overlapprobe", 0, false);
        await Done(lane);
        Assert.Equal(3, f.Writes.Count);
        var trials = Path.Combine(_root, "trials");
        Assert.All(f.Writes, w => Assert.StartsWith(trials, w.OutPath, StringComparison.OrdinalIgnoreCase));
        Assert.Equal(3, f.Writes.Select(w => w.OutPath).Distinct(StringComparer.OrdinalIgnoreCase).Count());
        Assert.Contains(trials, said);                                        // P9: the folder the modder clears out is said
    }

    // P8: a trial is a made-up net edit of the selected reference alone, from its load-order pose
    [Fact]
    public async Task P8m_a_trial_holds_only_the_selected_references_move_from_its_load_order_pose_and_says_so()
    {
        var f = new Lane();
        var lane = f.Make(_root);
        var said = lane.Start(Subject, C1, Home, Three(), "overlapprobe", otherStaged: 2, writtenSame: true);
        await Done(lane);
        Assert.Equal(3, f.Writes.Count);
        Assert.All(f.Writes, w => { Assert.Equal(Subject, w.Edit.Ref); Assert.Equal(Home, w.Edit.Before); });
        var first = f.Writes[0].Edit.After.Position;
        Assert.Equal(Home.Position.X + Three()[0].Move.X, first.X, 2);
        Assert.Contains("2 other staged edits are not in the trials", said);
        Assert.Contains("written edit", said);
    }

    // every candidate separates the boxes by construction; the word is the re-check's alone
    [Fact]
    public async Task P8b_a_candidate_whose_boxes_separate_but_whose_re_check_finds_the_row_reads_still_there()
    {
        var f = new Lane { Answer = _ => Recheck("still there") };
        var lane = f.Make(_root);
        lane.Start(Subject, C1, Home, Three(), "overlapprobe", 0, false);
        await Done(lane);
        Assert.Equal(3, lane.Placements.Count);
        Assert.All(lane.Placements, p => Assert.Equal("still there", p.Verdict?.Word));
    }

    [Fact]
    public async Task P8g_each_candidate_runs_its_own_re_check()
    {
        var f = new Lane();
        var lane = f.Make(_root);
        lane.Start(Subject, C1, Home, Three(), "overlapprobe", 0, false);
        await Done(lane);
        Assert.Equal(3, f.WorkDirs.Distinct(StringComparer.OrdinalIgnoreCase).Count());
    }

    [Fact]
    public async Task P8h_every_candidate_says_what_was_and_was_not_checked_with_the_population()
    {
        var f = new Lane();
        var lane = f.Make(_root);
        lane.Start(Subject, C1, Home, Three(), "overlapprobe", 0, false);
        await Done(lane);
        Assert.Equal(3, lane.Placements.Count);
        Assert.All(lane.Placements, p =>
        {
            Assert.Contains(p.Checked, c => c.Contains("overlapprobe"));
            Assert.Contains(p.NotChecked, c => c.Contains("population: 1 plugin(s)"));
            Assert.Contains(p.NotChecked, c => c.Contains("geometry.placement-contact"));
            Assert.Contains(p.NotChecked, c => c.Contains("navmesh"));
            Assert.Contains(p.NotChecked, c => c.Contains("collision"));
            Assert.Contains(p.NotChecked, c => c.Contains("doors and what names it"));
            Assert.Contains(p.NotChecked, c => c.Contains("scenery no mod is responsible for"));
            Assert.Contains(p.NotChecked, c => c.Contains("what rests on it stays where it is"));
        });
        Assert.Contains(TrialLane.NotCheckedFor("contactprobe", "pop"), c => c.Contains("geometry.object-overlap"));
    }

    // P4: the contact owner's answer at the pose is shown as a fact; it never changes the word
    [Fact]
    public async Task P4e_a_crossing_the_contact_read_sees_is_listed_but_the_word_stays_the_re_checks()
    {
        var f = new Lane { ContactsAt = _ => new[] { "09A3C4:Skyrim.esm (TreeDeadShrubSnow) crosses it" } };
        var lane = f.Make(_root);
        lane.Start(Subject, C1, Home, Three(), "overlapprobe", 0, false);
        await Done(lane);
        var p = Assert.Single(lane.Placements, x => x.N == 1);
        Assert.Equal("clear", p.Verdict?.Word);
        Assert.Contains(ClearRule.ClearLimit, p.Verdict!.Sentence);
        Assert.Contains(p.Contacts, c => c.Contains("09A3C4"));
    }

    [Fact]
    public async Task P8k_a_refused_drop_keeps_the_candidate_at_its_own_height_and_is_its_drop_sentence()
    {
        var f = new Lane { Settle = p => (p, "09A3C4:Skyrim.esm crosses it above its lowest 16 units, so it is already sunk into it") };
        var lane = f.Make(_root);
        lane.Start(Subject, C1, Home, Three(), "overlapprobe", 0, false);
        await Done(lane);
        var p = Assert.Single(lane.Placements, x => x.N == 1);
        Assert.Contains("already sunk", p.Drop);
        Assert.Equal(Home.Position.Z, p.Pose.Position.Z, 3);
    }

    // B8: the settle rule itself: a refused drop keeps the reference's own height, a landed one lowers from 20 up
    [Fact]
    public void P8r_a_refused_drop_keeps_the_own_height_and_a_landed_one_lowers_from_twenty_up()
    {
        var target = Home;
        var (kept, said) = TrialLane.SettleBy(target, p => new CaptureViews.DropAnswer("k", null, null, null, "it is already sunk"));
        Assert.Equal(target.Position.Z, kept.Position.Z, 3);
        Assert.Equal("it is already sunk", said);
        var (landed, said2) = TrialLane.SettleBy(target, p => new CaptureViews.DropAnswer("k", 7f, CaptureViews.TerrainKey, CaptureViews.TerrainKey, null));
        Assert.Equal(target.Position.Z + 13f, landed.Position.Z, 3);
        Assert.Contains("onto the terrain", said2);
    }

    [Fact]
    public void P8j_a_second_ask_while_one_runs_is_refused()
    {
        var f = new Lane { Gate = new TaskCompletionSource() };
        var lane = f.Make(_root);
        lane.Start(Subject, C1, Home, Three(), "overlapprobe", 0, false);
        var ex = Assert.Throws<InvalidOperationException>(() => lane.Start(Subject, C1, Home, Three(), "overlapprobe", 0, false));
        Assert.Contains("already", ex.Message);
        f.Gate.SetResult();
    }

    // P8: with the Output Folder at the instance's mods folder there is no default Work Folder
    [Fact]
    public void P8n_with_no_work_folder_find_clear_is_refused_naming_set_work_folder()
    {
        var lane = new Lane().Make(null);
        var ex = Assert.Throws<InvalidOperationException>(() => lane.Start(Subject, C1, Home, Three(), "overlapprobe", 0, false));
        Assert.Contains("set_work_folder", ex.Message);
    }

    [Fact]
    public async Task P8i_choose_gives_the_candidates_pose_and_is_refused_in_another_cell_or_after_an_edit()
    {
        var f = new Lane();
        var lane = f.Make(_root);
        lane.Start(Subject, C1, Home, Three(), "overlapprobe", 0, false);
        await Done(lane);
        Assert.Equal(3, lane.Placements.Count);
        var (pose, _) = lane.Choose(2, C1, Home);
        Assert.Equal(lane.Placements[1].Pose, pose);
        var (none1, said1) = lane.Choose(2, C2, Home);
        Assert.Null(none1); Assert.Contains("cell", said1);
        var (none2, said2) = lane.Choose(2, C1, Home with { Position = new Vec3(0, 0, 0) });
        Assert.Null(none2); Assert.Contains("edited", said2);
    }

    // P5: each candidate is previewed at its pose as a numbered tint, view only
    [Fact]
    public void P5a_each_candidate_is_drawn_as_a_numbered_tint_at_its_pose_and_cleared_on_restore()
    {
        var ro = PlacementTests.OneRef(out var key);
        var session = new EditSession(new[] { ro });
        var view = new ViewState(session, () => new[] { ro });
        var home = RefPlacementEdit.BakedPose(ro.Ref);
        var poses = new[] { home with { Position = new Vec3(home.Position.X + 100, home.Position.Y, home.Position.Z) },
                            home with { Position = new Vec3(home.Position.X, home.Position.Y + 200, home.Position.Z) } };
        view.ShowCandidates(key, poses);
        var t = view.CandidateTriangles();
        Assert.Equal(new[] { 1, 2 }, t.Select(x => x.N));
        int floats = ro.Shapes.Sum(s => s.Tris.Length) * 6;
        Assert.All(t, x => Assert.Equal(floats, x.Triangles.Count));
        Assert.NotEqual(t[0].Triangles[0], t[1].Triangles[0]);
        Assert.Empty(session.Pending);                                        // view only
        view.Restore();
        Assert.Empty(view.CandidateTriangles());
        view.ShowCandidates(key, poses); view.ClearCandidates();
        Assert.Empty(view.CandidateTriangles());
    }

    // ---------------------------------------------------------------- Gate B review: B2, B5, B6, B7

    const string RuleProbe = "overlapprobe", RulePolicy = "synthetic-policy", RulePop = "load-time+population:000000000000", RuleState = "baseline";

    static Conflict Ov(int a, int b, PhysicalState ph) =>
        new("OVERLAP", "Base.esm", "Subject.esp", "synthetic",
            new[] { new ConflictRecord($"{a:X6}:Base.esm", "placed-ref", null, ph) },
            new[] { new ConflictRecord($"{b:X6}:Base.esm", "placed-ref", null) });

    static string RowId(int a, int b) => ConflictIo.RowIdentity("OVERLAP", $"{a:X6}:Base.esm", $"{b:X6}:Base.esm", "Base.esm", null);

    static ConfigurationIdentity Cfg(params string[] plugins) =>
        ConfigurationIdentity.Compute("synthetic:test",
            plugins.Select((p, i) => new ConfigPlugin(p, "C:/synthetic/" + p, new string((char)('a' + i), 64), 100, "data", i == 0, false, Array.Empty<string>())).ToList(),
            new[] { new ConfigProvider("data", "data") }, Array.Empty<ConfigOverlay>(), RulePop, RuleState);

    // B2: the other rows of the moved reference, on either side and in any case, each with its frozen word
    [Fact]
    public void P4f_the_other_rows_of_the_moved_reference_are_found_on_either_side_with_their_words()
    {
        var cov = new[] { new CoverageLine("placed references", 10) };
        var un = new ConflictFile(RuleProbe, "synthetic", "2026-11-15T00:00:00Z",
            new[] { Ov(0x801, 0x901, PhysicalState.Holds), Ov(0x902, 0x801, PhysicalState.Holds), Ov(0x803, 0x903, PhysicalState.Holds), Ov(0x801, 0x904, PhysicalState.Holds) },
            cov, Population: RulePop, State: RuleState, Configuration: Cfg("Base.esm", "Subject.esp"), MeasurementPolicy: RulePolicy);
        var pa = new ConflictFile(RuleProbe, "synthetic", "2026-11-15T00:00:01Z",
            new[] { Ov(0x803, 0x903, PhysicalState.Holds) }, cov,
            Predicates: new[]
            {
                new Predicate("OVERLAP", "Base.esm", "Subject.esp", "000801:Base.esm", "000901:Base.esm", PhysicalState.Gone, null),
                new Predicate("OVERLAP", "Base.esm", "Subject.esp", "000902:Base.esm", "000801:Base.esm", PhysicalState.Unmeasured, null),
                new Predicate("OVERLAP", "Base.esm", "Subject.esp", "000801:Base.esm", "000904:Base.esm", PhysicalState.Gone, null),
            },
            BaseEmit: Path.Combine(_root, "un.json"), Population: RulePop, State: RuleState, Configuration: Cfg("Base.esm", "Subject.esp", "Patch.esp"), MeasurementPolicy: RulePolicy);
        ConflictIo.Write(Path.Combine(_root, "un.json"), un);
        ConflictIo.Write(Path.Combine(_root, "pa.json"), pa);
        var others = ClearRule.OthersOfRef(Path.Combine(_root, "un.json"), Path.Combine(_root, "pa.json"), RowId(0x801, 0x901), "000801:base.esm");
        Assert.Equal(2, others.Count);
        Assert.Equal("unmeasured", others.Single(o => o.Id == RowId(0x902, 0x801)).Word);
        Assert.Equal("resolved", others.Single(o => o.Id == RowId(0x801, 0x904)).Word);
        Assert.DoesNotContain(others, o => o.Id == RowId(0x801, 0x901) || o.Id == RowId(0x803, 0x903));
    }

    [Fact]
    public void P4g_another_row_of_the_reference_reading_unmeasured_gives_resolved_here_but()
    {
        var v = ClearRule.Judge(V("open", "resolved"), new[] { V("overlap|902|801", "unmeasured") }, Array.Empty<RowVerdict>(), Array.Empty<string>());
        Assert.Equal("resolved here, but", v.Word);
        Assert.Contains("overlap|902|801", v.Rows);
    }

    // B7: a created row outranks another row that did not resolve; both are named
    [Fact]
    public void P8q_a_created_row_and_an_unresolved_other_row_read_new_conflict_naming_both()
    {
        var v = ClearRule.Judge(V("open", "resolved"), new[] { V("other", "reclassified") }, new[] { V("made", "new") }, Array.Empty<string>());
        Assert.Equal("new conflict", v.Word);
        Assert.Contains("made", v.Rows);
        Assert.Contains("other", v.Rows);
    }

    // B7: the candidate is snapped to the current grid on the axes it moved
    [Fact]
    public async Task P8p_a_candidate_is_snapped_to_the_current_grid()
    {
        var f = new Lane();
        var lane = f.Make(_root);
        lane.Start(Subject, C1, Home, Three(), "overlapprobe", 0, false, grid: 32f);
        await Done(lane);
        Assert.Equal(3, f.Writes.Count);
        // snapping rounds the axes the candidate moved (part 1's rule), not the ones it left alone
        Assert.All(f.Writes, w =>
        {
            bool mx = w.Edit.After.Position.X != Home.Position.X, my = w.Edit.After.Position.Y != Home.Position.Y;
            Assert.True(mx || my, "a candidate that did not move");
            if (mx) Assert.Equal(0f, w.Edit.After.Position.X % 32f, 3);
            if (my) Assert.Equal(0f, w.Edit.After.Position.Y % 32f, 3);
        });
    }

    // B5: scenes on the one list model, with decisions
    [Fact]
    public void G7k_a_scene_orders_its_rows_by_decision_hides_intentional_ones_and_says_how_many()
    {
        var rows = new[] { Row(1, "000801:HcWbTest.esp", null), Row(2, "000802:HcWbTest.esp", null), Row(3, "000803:HcWbTest.esp", null) };
        Decision D(string key, string verdict) => new("C:/I", "P", "geometry.object-overlap", key, verdict, "why", DateTimeOffset.UtcNow,
                                                      new RowState("Holds", "", ""), Array.Empty<ObjectState>(), "srv");
        (Decision?, DecisionMatch) Of(RankedRow r) => r.Rank switch
        {
            1 => (D("row-1", "intentional"), new DecisionMatch(Standing.Applies, "applies")),
            2 => (D("row-2", "real problem"), new DecisionMatch(Standing.Reconsider, "reconsider: Placement.Position changed")),
            _ => (null, new DecisionMatch(Standing.Undecided, "undecided")),
        };
        var view = Assert.Single(ReviewList.BuildScenes(rows, Array.Empty<RankedFinding>(), Policy(), Of, includeDecided: false));
        Assert.Equal(new[] { 2, 3 }, view.Rows.Select(e => e.Row.Rank));
        Assert.Equal(1, view.Hidden);
        Assert.Contains("1 hidden", view.Header);
        var all = Assert.Single(ReviewList.BuildScenes(rows, Array.Empty<RankedFinding>(), Policy(), Of, includeDecided: true));
        Assert.Equal(new[] { 2, 3, 1 }, all.Rows.Select(e => e.Row.Rank));
        Assert.Equal(0, all.Hidden);
    }

    // B6: the declared-dependencies sentence, both ways
    [Fact]
    public async Task C5o_the_dependency_statement_says_the_check_was_not_run_or_names_the_bundles_rows()
    {
        var none = await ContextRead.ReadAsync(TownSource(), Town(), K(Building), NoNav, _ => null);
        Assert.Contains("the declared-dependencies check was not run on this bundle", none.Statement);
        var some = await ContextRead.ReadAsync(TownSource(), Town(), K(Building), NoNav, _ => null,
                                               dependencyRows: new[] { "LeftBehind: 000C01:HcWbTest.esp names it as its enable parent" });
        Assert.Contains("000C01:HcWbTest.esp", some.Statement);
        Assert.DoesNotContain("was not run", some.Statement);
    }

    [Fact]
    public async Task C5p_its_own_enable_parent_is_named()
    {
        var src = TownSource(); src.Parent = "000C09:HcWbTest.esp";
        var a = await ContextRead.ReadAsync(src, Town(), K(Building), NoNav, _ => null);
        Assert.Equal("000C09:HcWbTest.esp", a.EnableParent);
    }

    [Fact]
    public async Task C5q_a_door_the_scene_does_not_draw_reads_near_only_not_drawn_here()
    {
        var src = TownSource(); src.Doors.Add("000D01:HcWbTest.esp");
        var a = await ContextRead.ReadAsync(src, Town(), K(Building), NoNav, _ => null);
        Assert.Equal("near only (not drawn here, contact not assessed)", Assert.Single(a.Doors, d => d.Key == "000D01:HcWbTest.esp").Label);
    }

    [Fact]
    public async Task C5r_the_navmesh_section_says_its_rule()
    {
        var a = await ContextRead.ReadAsync(TownSource(), Town(), K(Building), NoNav, _ => null);
        Assert.True(a.Sections.TryGetValue("navmesh", out var nav), "the navmesh section said nothing");
        Assert.Contains("under its axis-aligned bounds, any height", nav);
        Assert.Contains("shown, not judged", nav);
    }

    // ---------------------------------------------------------------- Gate C round 2: C2

    [Fact]
    public async Task C2c_a_clear_candidate_with_a_crossing_says_the_re_check_counts_none_of_them()
    {
        var f = new Lane
        {
            ContactsAt = _ => new[] { "000D01:HcWbTest.esp (Drift) crosses it", "000D02:HcWbTest.esp (Thicket) is within 8 units of it" },
            Settle = p => (p, "000D03:HcWbTest.esp (Rock) crosses 000800:HcWbTest.esp above its lowest 16 units, so it is already sunk into it; drop lowers only, so move it up first."),
        };
        var lane = f.Make(_root);
        lane.Start(Subject, C1, Home, Three(), "overlapprobe", 0, false);
        await Done(lane);
        var p = Assert.Single(lane.Placements, x => x.N == 1);
        Assert.Equal("clear", p.Verdict?.Word);
        Assert.Contains(ClearRule.ClearLimit, p.Verdict!.Sentence);
        Assert.Contains("counts none of these as a conflict", p.Verdict.Sentence);
        Assert.Contains("000D01", p.Verdict.Sentence);
        Assert.Contains("000D03", p.Verdict.Sentence);
        Assert.DoesNotContain("000D02", p.Verdict.Sentence);                   // a near touch is not a crossing
    }

    [Fact]
    public async Task C2d_a_clear_candidate_with_no_crossing_carries_only_its_limit()
    {
        var f = new Lane();
        var lane = f.Make(_root);
        lane.Start(Subject, C1, Home, Three(), "overlapprobe", 0, false);
        await Done(lane);
        Assert.All(lane.Placements, p => Assert.Equal($"clear: {ClearRule.ClearLimit}.", p.Verdict?.Sentence));
    }
}

/// <summary>P8: a trial patch is a NEW patch written at a named folder, not into the Output Folder.</summary>
[Trait("tier", "unit")]
public class TrialPatchWriteTests : IClassFixture<TiltedRefWorld>
{
    readonly TiltedRefWorld _w;
    public TrialPatchWriteTests(TiltedRefWorld w) => _w = w;

    [Fact]
    public async Task P8o_commit_with_out_path_writes_a_new_patch_in_that_folder()
    {
        var exe = TestBuild.Server;
        Assert.True(File.Exists(exe), $"build src/housecarl-mcp in Release first: {exe}");
        await using var w = await PatchWriter.StartAsync(exe, new WriteTarget(_w.Instance, "Default", _w.DataDir, Path.Combine(_w.Instance, "mods")),
            Path.Combine(_w.Root, "hcdata-" + Guid.NewGuid().ToString("N")), TestCorpus.Path, _ => { });
        var before = new RefPlacementEdit.Pose(TiltedRefWorld.Pos, TiltedRefWorld.Rot, 1f);
        var trial = Path.Combine(_w.Root, "work", "trials", "1");
        Directory.CreateDirectory(trial);
        var said = await w.CommitAsync(new[] { new PendingEdit(TiltedRefWorld.Ref, before, before with { Position = new Vec3(600f, -256f, 64f) }) },
                                       "WbTrial1", dryRun: false, outPath: trial);
        Assert.True(Directory.GetFiles(trial, "WbTrial1*.esp", SearchOption.AllDirectories).Length == 1, "no patch in the trial folder; the server said: " + said);
        Assert.Empty(Directory.GetFiles(Path.Combine(_w.Instance, "mods"), "WbTrial1*.esp", SearchOption.AllDirectories));
    }
}
