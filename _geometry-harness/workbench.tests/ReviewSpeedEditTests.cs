using System.Numerics;
using HousecarlCore;
using Mutagen.Bethesda.Plugins;
using ViewProbe;
using Workbench;
using Xunit;

namespace WorkbenchTests;

/// <summary>Item 4: the pending-edits list in words, per-reference revert, and the saved session.</summary>
[Trait("tier", "unit")]
public class ReviewSpeedEditTests : IDisposable
{
    readonly string _root = Path.Combine(Path.GetTempPath(), "hc-wb-session-" + Guid.NewGuid().ToString("N"));
    public void Dispose() { try { Directory.Delete(_root, true); } catch { } }

    static PendingEdit Edit(RefPlacementEdit.Pose before, RefPlacementEdit.Pose after) => new(Boxes.Key(0x800), before, after);
    static RefPlacementEdit.Pose At(float x, float y, float z) => new(new Vec3(x, y, z), new Vec3(0, 0, 0), 1f);

    // E4a
    [Fact]
    public void A_move_reads_in_world_directions_with_the_objects_name()
    {
        Assert.Equal("Moved barrel 150 units east", EditSession.Sentence(Edit(At(0, 0, 0), At(150, 0, 0)), "barrel"));
        Assert.Equal("Moved barrel 40 units north and 12 units down", EditSession.Sentence(Edit(At(0, 0, 0), At(0, 40, -12)), "barrel"));
        Assert.Equal("Moved 000800:HcWbTest.esp 30 units west", EditSession.Sentence(Edit(At(0, 0, 0), At(-30, 0, 0)), null));
    }

    // E4b: turns about Z read left/right; about X or Y read as a tilt about the named axis
    [Fact]
    public void A_turn_reads_left_or_right_and_a_tilt_names_its_axis()
    {
        var b = At(0, 0, 0);
        var left = b with { Rotation = RefPlacementEdit.Turned(b.Rotation, Vector3.UnitZ, float.DegreesToRadians(15)) };
        Assert.Equal("Turned shrub 15° left", EditSession.Sentence(Edit(b, left), "shrub"));
        var right = b with { Rotation = RefPlacementEdit.Turned(b.Rotation, Vector3.UnitZ, float.DegreesToRadians(-90)) };
        Assert.Equal("Turned shrub 90° right", EditSession.Sentence(Edit(b, right), "shrub"));
        var tilt = b with { Rotation = RefPlacementEdit.Turned(b.Rotation, Vector3.UnitX, float.DegreesToRadians(10)) };
        Assert.Equal("Tilted shrub 10° about the east-west axis", EditSession.Sentence(Edit(b, tilt), "shrub"));
        var moveAndTurn = At(150, 0, 0) with { Rotation = left.Rotation };
        Assert.Equal("Moved shrub 150 units east; turned it 15° left", EditSession.Sentence(Edit(b, moveAndTurn), "shrub"));
    }

    // E4c
    [Fact]
    public void Revert_drops_one_references_edits_and_leaves_the_others()
    {
        var a = Boxes.Box(0x800, new Vec3(0, 0, 0), new Vector3(20, 20, 20));
        var b = Boxes.Box(0x801, new Vec3(100, 0, 0), new Vector3(20, 20, 20));
        var c = Boxes.Box(0x802, new Vec3(200, 0, 0), new Vector3(20, 20, 20));
        var s = new EditSession(new[] { a, b, c });
        s.Stage(a.Ref.RefKey, Boxes.Moved(a, 10));
        s.Stage(b.Ref.RefKey, Boxes.Moved(b, 20));
        s.Stage(b.Ref.RefKey, Boxes.Moved(b, 25));
        s.Stage(c.Ref.RefKey, Boxes.Moved(c, 30));
        var r = s.Revert(b.Ref.RefKey);
        Assert.NotNull(r);
        Assert.DoesNotContain(s.Pending, e => e.Ref == b.Ref.RefKey);
        Assert.Equal(new[] { a.Ref.RefKey, c.Ref.RefKey }, s.Net().Select(e => e.Ref));
        Assert.Equal(RefPlacementEdit.BakedPose(b.Ref), s.Current(b.Ref.RefKey));
        Assert.Null(b.Meshes[0].ModelTransform);
        Assert.Equal(Boxes.Moved(c, 30), s.Current(c.Ref.RefKey));
    }

    // E4d
    [Fact]
    public void The_session_file_lives_under_the_work_folder_and_without_one_saving_is_refused_in_one_sentence()
    {
        var (path, why) = SessionFile.PathFor(_root, "plan123", "candidate-01");
        Assert.Null(why);
        Assert.NotNull(path);
        Assert.StartsWith(Path.Combine(_root, "sessions"), path);
        var (none, noWhy) = SessionFile.PathFor(null, "plan123", "candidate-01");
        Assert.Null(none);
        Assert.NotNull(noWhy);
        Assert.Contains("Work Folder", noWhy);
        Assert.DoesNotContain("\n", noWhy);
    }

    static SessionData Saved(RefOcclusion a, RefOcclusion b, RefPlacementEdit.Pose bBefore) => new("plan123", "candidate-01", a.Ref.RefKey.ToString(), 2,
        new[] { 1f, 2f, 3f, 90f, 10f },
        new[] { new SavedEdit(a.Ref.RefKey.ToString(), RefPlacementEdit.BakedPose(a.Ref), Boxes.Moved(a, 40)),
                new SavedEdit(b.Ref.RefKey.ToString(), bBefore, Boxes.Moved(b, 50)) },
        new[] { new SavedWritten(Boxes.Key(0x803).ToString(), "WbOld.esp", "C:\\out\\houseCARL - WbOld") });

    // E4e
    [Fact]
    public void A_session_round_trips_through_its_file()
    {
        var a = Boxes.Box(0x800, new Vec3(0, 0, 0), new Vector3(20, 20, 20));
        var b = Boxes.Box(0x801, new Vec3(100, 0, 0), new Vector3(20, 20, 20));
        var d = Saved(a, b, RefPlacementEdit.BakedPose(b.Ref));
        var path = Path.Combine(_root, "sessions", "s.json");
        SessionFile.Write(path, d);
        var back = SessionFile.Read(path);
        Assert.NotNull(back);
        Assert.Equal(d.Selected, back!.Selected);
        Assert.Equal(d.OpenConflict, back.OpenConflict);
        Assert.Equal(d.Camera, back.Camera);
        Assert.Equal(d.Staged, back.Staged);
        Assert.Equal(d.Written, back.Written);
    }

    // E4f: restore re-stages what still fits, redraws written edits from the patch, and says what it did not restore
    [Fact]
    public void Restore_restages_edits_whose_reference_is_unchanged_redraws_written_ones_and_names_the_rest()
    {
        var a = Boxes.Box(0x800, new Vec3(0, 0, 0), new Vector3(20, 20, 20));
        var b = Boxes.Box(0x801, new Vec3(100, 0, 0), new Vector3(20, 20, 20));
        var w = Boxes.Box(0x803, new Vec3(300, 0, 0), new Vector3(20, 20, 20));
        var s = new EditSession(new[] { a, b, w });
        var stale = At(999, 0, 0);                                     // b was elsewhere when the session was saved
        var fromPatch = Boxes.Moved(w, 0, 75);
        var notes = SessionFile.Restore(s, Saved(a, b, stale), (k, sw) => k == w.Ref.RefKey && sw.Patch == "WbOld.esp" ? fromPatch : null);
        Assert.Equal(new[] { a.Ref.RefKey }, s.Net().Select(e => e.Ref));
        Assert.Equal(Boxes.Moved(a, 40), s.Current(a.Ref.RefKey));
        Assert.True(s.Written.TryGetValue(w.Ref.RefKey, out var written), "the written edit was not redrawn from its patch");
        Assert.Equal("WbOld.esp", written.Patch);
        Assert.Equal(fromPatch, s.Current(w.Ref.RefKey));
        var note = Assert.Single(notes);
        Assert.Contains(b.Ref.RefKey.ToString(), note);
        Assert.Contains("not restored", note);
    }

    // E4g: after its override is removed, a written edit is forgotten and drawn at the load-order place
    [Fact]
    public void Forgetting_a_written_edit_draws_the_reference_where_the_load_order_puts_it()
    {
        var a = Boxes.Box(0x800, new Vec3(0, 0, 0), new Vector3(20, 20, 20));
        var s = new EditSession(new[] { a });
        s.Stage(a.Ref.RefKey, Boxes.Moved(a, 60));
        s.MarkWritten("WbTest.esp");
        s.ForgetWritten(a.Ref.RefKey);
        Assert.False(s.Written.ContainsKey(a.Ref.RefKey), "the written edit is still recorded");
        Assert.Equal(RefPlacementEdit.BakedPose(a.Ref), s.Current(a.Ref.RefKey));
        Assert.Null(a.Meshes[0].ModelTransform);
    }
}
