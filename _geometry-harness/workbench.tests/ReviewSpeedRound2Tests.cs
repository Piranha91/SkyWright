using System.Diagnostics;
using System.Numerics;
using HarnessShared;
using HousecarlCore;
using Mutagen.Bethesda.Plugins;
using ViewProbe;
using Workbench;
using Xunit;
using Xunit.Abstractions;

namespace WorkbenchTests;

/// <summary>The Gate C review's findings C1-C9 on synthetic data: the session across cells, decisions compared per row
/// and shown in the one list model, no handle while originals show, the mod tint's left-out references, the hide
/// rollback, the see-through tint while originals show, and drop's sampled sunk check and its stated limits.</summary>
[Trait("tier", "unit")]
public sealed class ReviewSpeedRound2Tests : IDisposable
{
    readonly ITestOutputHelper _out;
    readonly string _root = Path.Combine(Path.GetTempPath(), "hc-wb-r2-" + Guid.NewGuid().ToString("N"));
    public ReviewSpeedRound2Tests(ITestOutputHelper o) { _out = o; }
    public void Dispose() { try { Directory.Delete(_root, true); } catch { } }

    static RankedRow Row(int rank, string id, string summary, string refKey = "000800:HcWbTest.esp", string cell = "000100:HcWbTest.esp") =>
        new(rank, new ConflictRow(id, "geometry.object-overlap", "OVERLAP", "Subject.esp", "Other.esp", summary, refKey, "000900:HcWbTest.esp",
                                  PhysicalState.Holds, cell, "Static", "TestBox", "Static", "TestBox", "calibrated-inference", false),
            true, 0, "holds", 1, "calibrated-inference", 0, "created", 50, "Static");

    static Decision D(string key, string verdict, string summary) => new("C:/Instance", "Default", "geometry.object-overlap", key, verdict, "why",
        DateTimeOffset.Parse("2026-11-15T12:00:00Z"), new RowState("Holds", summary, ""), Array.Empty<ObjectState>(), "srv");

    // ---- C1: the session across cells

    static SessionData S(IReadOnlyList<SavedEdit> staged, IReadOnlyList<SavedWritten> written, int? open = 7, string? cameraCell = "000200:HcWbTest.esp") =>
        new("plan", "arm", "000801:HcWbTest.esp", open, new[] { 1f, 2f, 3f, 90f, 10f }, staged, written, cameraCell);

    static readonly RefPlacementEdit.Pose P0 = new(new Vec3(0, 0, 0), new Vec3(0, 0, 0), 1f);
    static readonly RefPlacementEdit.Pose P1 = new(new Vec3(50, 0, 0), new Vec3(0, 0, 0), 1f);

    [Fact]
    public void C1a_saving_keeps_the_entries_of_references_this_scene_does_not_draw()
    {
        var previous = S(new[] { new SavedEdit("000A01:HcWbTest.esp", P0, P1), new SavedEdit("000800:HcWbTest.esp", P0, P1) },
                         new[] { new SavedWritten("000A02:HcWbTest.esp", "WbOld.esp", "C:/out/houseCARL - WbOld") });
        var now = S(Array.Empty<SavedEdit>(), Array.Empty<SavedWritten>(), cameraCell: "000100:HcWbTest.esp");
        var merged = SessionFile.Merge(previous, now, k => k == "000800:HcWbTest.esp");
        Assert.Equal(new[] { "000A01:HcWbTest.esp" }, merged.Staged.Select(e => e.Ref));          // the drawn one's edit was reverted here
        Assert.Equal(new[] { "000A02:HcWbTest.esp" }, merged.Written.Select(e => e.Ref));
    }

    [Fact]
    public void C1b_a_restore_keeps_other_cells_edits_and_says_so()
    {
        var a = Boxes.Box(0x800, new Vec3(0, 0, 0), new Vector3(20, 20, 20));
        var s = new EditSession(new[] { a });
        var notes = SessionFile.Restore(s, S(new[] { new SavedEdit("000A01:HcWbTest.esp", P0, P1) },
                                             new[] { new SavedWritten("000A02:HcWbTest.esp", "WbOld.esp", "C:/out/houseCARL - WbOld") }), (_, _) => null);
        Assert.Equal(2, notes.Count);
        Assert.All(notes, n => Assert.Contains("kept for its cell", n));
    }

    [Fact]
    public void C1c_a_reopened_review_starts_on_the_saved_row_and_the_camera_only_in_its_own_cell()
    {
        var rows = new[] { Row(1, "a", "s"), Row(7, "g", "s", cell: "000200:HcWbTest.esp") };
        Assert.Equal(7, SessionFile.StartRank(S(Array.Empty<SavedEdit>(), Array.Empty<SavedWritten>()), rows));
        Assert.Null(SessionFile.StartRank(S(Array.Empty<SavedEdit>(), Array.Empty<SavedWritten>(), open: 9), rows));
        var saved = S(Array.Empty<SavedEdit>(), Array.Empty<SavedWritten>());
        Assert.NotNull(SessionFile.CameraFor(saved, "000200:HcWbTest.esp"));
        Assert.Null(SessionFile.CameraFor(saved, "000100:HcWbTest.esp"));
    }

    // ---- C2: the row half needs no scene

    [Fact]
    public void C2a_the_row_half_flags_a_changed_summary_and_otherwise_says_the_objects_are_not_compared()
    {
        var stored = D("k", "intentional", "box A inside box B");
        var same = Decisions.CompareRow(stored, new RowState("Holds", "box A inside box B", ""));
        Assert.Equal(Standing.Applies, same.Standing);
        Assert.Contains("not compared until its cell is open", same.Sentence);
        var changed = Decisions.CompareRow(stored, new RowState("Holds", "box A clear of box B", ""));
        Assert.Equal(Standing.Reconsider, changed.Standing);
    }

    // ---- C3: one list model

    [Fact]
    public void C3a_the_list_orders_by_decision_hides_intentional_rows_and_carries_the_sentence()
    {
        var rows = new[] { Row(1, "i", "s"), Row(2, "u", "s"), Row(3, "c", "s"), Row(4, "r", "s") };
        (Decision?, DecisionMatch) Of(RankedRow r) => r.Row.Identity switch
        {
            "i" => (D("i", "intentional", "s"), new DecisionMatch(Standing.Applies, "applies")),
            "c" => (D("c", "intentional", "old"), new DecisionMatch(Standing.Reconsider, "reconsider: the detector reads the row differently now")),
            "r" => (D("r", "real problem", "s"), new DecisionMatch(Standing.Applies, "applies")),
            _ => (null, new DecisionMatch(Standing.Undecided, "undecided")),
        };
        var shown = ReviewList.Build(rows, Of, includeDecided: false);
        Assert.Equal(new[] { "c", "u", "r" }, shown.Select(e => e.Row.Row.Identity));
        Assert.Contains("reconsider", shown[0].Standing.Sentence);
        Assert.Equal("intentional", shown[0].Decision?.Verdict);
        var all = ReviewList.Build(rows, Of, includeDecided: true);
        Assert.Equal(new[] { "c", "u", "r", "i" }, all.Select(e => e.Row.Row.Identity));
    }

    [Fact]
    public void C3b_next_undecided_offers_a_reconsider_row_first()
    {
        var rows = new[] { Row(1, "a", "s"), Row(2, "b", "s"), Row(3, "c", "s") };
        var entries = ReviewList.Build(rows, r => r.Rank switch
        {
            1 => (D("a", "real problem", "s"), new DecisionMatch(Standing.Applies, "applies")),
            2 => (null, new DecisionMatch(Standing.Undecided, "undecided")),
            _ => (D("c", "intentional", "old"), new DecisionMatch(Standing.Reconsider, "reconsider")),
        }, includeDecided: true);
        Assert.Equal(3, ReviewList.NextUndecided(entries, 1)?.Rank);
        Assert.Equal(2, ReviewList.NextUndecided(entries, 3)?.Rank);
    }

    // ---- (c): no handle while originals show

    [Fact]
    public void Cc_the_handle_is_not_drawn_while_originals_show()
    {
        var a = Boxes.Box(0x800, new Vec3(0, 0, 0), new Vector3(20, 20, 20));
        var s = new EditSession(new[] { a });
        var g = new Gizmo { Centre = new Vector3(50, 0, 0), Size = 100f };
        var v = new ViewState(s, () => new[] { a }, g);
        s.Stage(a.Ref.RefKey, Boxes.Moved(a, 50));
        v.ShowOriginal(true);
        Assert.Empty(g.Segments());
        v.ShowOriginal(false);
        Assert.NotEmpty(g.Segments());
    }

    // ---- (d1) / C8: the mod tint says what it leaves out

    [Fact]
    public void Cd1_the_mod_tint_counts_and_names_what_it_leaves_out()
    {
        FormKey K(uint id) => new(new ModKey("HcWbTest", ModType.Plugin), id);
        var winners = new Dictionary<FormKey, string> { [K(1)] = "A.esp", [K(2)] = "A.esp", [K(3)] = "A.esp", [K(4)] = "A.esp", [K(5)] = "B.esp" };
        var (refs, note) = ModTint.Pick(new[] { (K(1), false), (K(2), false), (K(3), true), (K(4), false), (K(5), true) }, winners, new[] { K(2) }, new[] { "A.esp" });
        Assert.Equal(new[] { K(1), K(4) }, refs);
        Assert.Contains("1 Base Object Swapper", note);
        Assert.Contains(K(3).ToString(), note);
        Assert.Contains("1 whose placement", note);
        Assert.Contains(K(2).ToString(), note);
    }

    // ---- C4, C5: the view state

    [Fact]
    public void C4_a_missed_hide_in_front_does_not_show_a_part_hidden_before()
    {
        var subject = Boxes.Box(0x850, new Vec3(2000, 0, 0), new Vector3(40, 40, 60));
        var wall = Boxes.Box(0x851, new Vec3(0, -300, 0), new Vector3(60, 20, 80), parts: 2);
        var all = new[] { subject, wall };
        var v = new ViewState(new EditSession(all), () => all);
        v.HidePart(wall.Shapes[1]);
        var shapes = all.SelectMany(r => r.Shapes).ToList();
        var o = TexturedScene.YUp(new Vector3(0, -1000, 20)); var d = TexturedScene.YUp(new Vector3(0, 1, 0));
        v.HideInFront(subject.Ref.RefKey, () => ScenePick.Pick(shapes, o, d, WorkbenchHost.Pickable));
        Assert.True(wall.Meshes[1].IsHiddenByHost, "the rollback showed a part the modder had hidden");
        Assert.Equal(1, v.HiddenParts);
    }

    [Fact]
    public void C5_a_see_through_tint_follows_the_originals_while_they_show()
    {
        var a = Boxes.Box(0x800, new Vec3(0, 0, 0), new Vector3(20, 20, 20));
        var baked = a.Shapes[0].Verts.ToArray();
        var s = new EditSession(new[] { a });
        var v = new ViewState(s, () => new[] { a });
        v.SeeThrough(a.Ref.RefKey);
        s.Stage(a.Ref.RefKey, Boxes.Moved(a, 150));
        _ = v.TintTriangles();                                        // built at the edited place
        v.ShowOriginal(true);
        var t = v.TintTriangles();
        var first = baked[a.Shapes[0].Tris[0]];
        Assert.Equal(first.X, t[0], 3);
    }

    // ---- C6: drop's sunk check, sampled and said

    [Fact]
    public void C6a_a_sunk_refusal_says_something_crosses_it_and_the_limits()
    {
        var sunk = Boxes.Box(0x904, new Vec3(0, 0, -30), new Vector3(20, 20, 40));
        var d = CaptureViews.Drop(CaptureViews.FromPickShapes(sunk.Shapes.Append(Boxes.Terrain(0)).ToArray()), sunk.Ref.RefKey.ToString());
        Assert.Contains("crosses", d.Refusal);
        Assert.Contains("lowest", d.Refusal);
    }

    [Fact]
    public void C6b_a_large_reference_over_dense_terrain_drops_in_well_under_a_second()
    {
        // a building-sized grid of 20,000 low vertices over terrain of 32,768 triangles whose box spans the building's height
        // (a hillside): every unsampled ray would scan every terrain triangle; the sunk check is sampled like the support rays
        var verts = new List<Vector3>(); var tris = new List<int>();
        int n = 141;
        for (int y = 0; y < n; y++) for (int x = 0; x < n; x++) verts.Add(TexturedScene.YUp(new Vector3(x * 10, y * 10, 100)));
        for (int y = 0; y < n - 1; y++) for (int x = 0; x < n - 1; x++) { int i = y * n + x; tris.AddRange(new[] { i, i + 1, i + n, i + 1, i + n + 1, i + n }); }
        verts.Add(TexturedScene.YUp(new Vector3(700, 700, 400)));            // a roof point: the reference is 300 units tall
        tris.AddRange(new[] { 0, n - 1, verts.Count - 1 });
        var key = Boxes.Key(0x990);
        var r = new SceneRef("meshes\\big.nif", new Vec3(0, 0, 100), new Vec3(0, 0, 0), 1f, false, RefKey: key, BaseEditorId: "BigFloor");
        var big = new PickShape { Kind = PickKind.Ref, Verts = verts.ToArray(), Tris = tris.ToArray(), Ref = r, Mesh = new SkyRender.GlMesh() };
        big.ComputeBounds();
        var tv = new List<Vector3>(); var tt = new List<int>(); int m = 129;
        for (int y = 0; y < m; y++) for (int x = 0; x < m; x++) tv.Add(TexturedScene.YUp(new Vector3(-100 + x * 12, -100 + y * 12, x == m - 1 && y == m - 1 ? 600 : (x + y) % 3)));   // a peak in one far corner: the terrain's box spans the building's height
        for (int y = 0; y < m - 1; y++) for (int x = 0; x < m - 1; x++) { int i = y * m + x; tt.AddRange(new[] { i, i + 1, i + m, i + 1, i + m + 1, i + m }); }
        var ground = new PickShape { Kind = PickKind.Terrain, Verts = tv.ToArray(), Tris = tt.ToArray(), Mesh = new SkyRender.GlMesh() };
        ground.ComputeBounds();
        var sw = Stopwatch.StartNew();
        var d = CaptureViews.Drop(CaptureViews.FromPickShapes(new[] { big, ground }), key.ToString());
        sw.Stop();
        _out.WriteLine($"drop of {verts.Count} low vertices over {tt.Count / 3} terrain triangles: {sw.Elapsed.TotalMilliseconds:0} ms; {d.Refusal ?? d.Distance?.ToString()}");
        Assert.True(d.Refusal is null, "refused: " + d.Refusal);
        Assert.True(sw.Elapsed.TotalSeconds < 1.0, $"the drop took {sw.Elapsed.TotalSeconds:0.00} s");
    }
}
