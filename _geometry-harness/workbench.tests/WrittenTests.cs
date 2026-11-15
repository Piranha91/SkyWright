using HousecarlCore;
using Mutagen.Bethesda.Plugins;
using ViewProbe;
using Workbench;
using Xunit;

namespace WorkbenchTests;

/// <summary>Stage-4 review M-1: after a write, the view keeps the edited reference at its written placement and says so;
/// it does not fall back to the load order's placement (the patch is not enabled) without a word.</summary>
[Trait("tier", "unit")]
public class WrittenTests
{
    [Fact]
    public void After_a_write_the_reference_stays_drawn_where_the_patch_puts_it_and_is_labelled()
    {
        var ro = PlacementTests.OneRef(out var key);
        var s = new EditSession(new[] { ro });
        var baked = RefPlacementEdit.BakedPose(ro.Ref);
        var moved = new RefPlacementEdit.Pose(new Vec3(baked.Position.X - 150, baked.Position.Y, baked.Position.Z), baked.Rotation, baked.Scale);
        s.Stage(key, moved);
        s.MarkWritten("WbTest.esp");
        Assert.Equal(moved.Position, s.Current(key).Position);
        Assert.Empty(s.Pending);
        Assert.True(s.Written.TryGetValue(key, out var w), "the written edit is not recorded");
        Assert.Equal("WbTest.esp", w.Patch);
        Assert.Contains("written to WbTest.esp, not enabled", EditSession.WrittenLabel(key, "WbTest.esp"));
        // Undo does not silently put it back where the load order has it: a written edit is undone by a new write.
        s.Undo();
        Assert.Equal(moved.Position, s.Current(key).Position);
    }

    [Fact]
    public void A_placement_read_back_from_a_patch_is_drawn_and_labelled()
    {
        var ro = PlacementTests.OneRef(out var key);
        var s = new EditSession(new[] { ro });
        var baked = RefPlacementEdit.BakedPose(ro.Ref);
        var fromPatch = new RefPlacementEdit.Pose(new Vec3(baked.Position.X, baked.Position.Y + 80, baked.Position.Z), baked.Rotation, baked.Scale);
        s.ShowWritten(key, fromPatch, "WbTest.esp");
        Assert.Equal(fromPatch.Position, s.Current(key).Position);
        Assert.Equal("WbTest.esp", s.Written[key].Patch);
    }
}
