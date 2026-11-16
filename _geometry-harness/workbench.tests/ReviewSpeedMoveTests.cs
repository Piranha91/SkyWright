using System.Numerics;
using HousecarlCore;
using ViewProbe;
using Workbench;
using Xunit;

namespace WorkbenchTests;

/// <summary>Item 6: axis locks, snapping, typed offsets, the X and Y turn rings, and drop to surface through the contact
/// owner (viewprobe's CaptureViews), on synthetic geometry.</summary>
[Trait("tier", "unit")]
public class ReviewSpeedMoveTests
{
    static RefPlacementEdit.Pose P(float x, float y, float z, float yaw = 0f) => new(new Vec3(x, y, z), new Vec3(0, 0, yaw), 1f);

    // M6a: a one-axis move keeps the axes it did not move
    [Fact]
    public void Snapping_rounds_only_the_axes_the_edit_moved()
    {
        var before = P(10.3f, 20.6f, 57.25f);
        var after = P(10.3f + 101f, 20.6f, 57.25f);               // a drag along X
        var snapped = MoveRules.Snap(before, after, step: 128f, angleStepDegrees: 15f);
        Assert.Equal(128f, snapped.Position.X, 3);
        Assert.Equal(20.6f, snapped.Position.Y, 3);
        Assert.True(MathF.Abs(snapped.Position.Z - 57.25f) < 1e-3f, $"a drag along X moved the height to {snapped.Position.Z}");
    }

    // M6b: the result lands on the grid, not the delta
    [Fact]
    public void Snapping_puts_the_result_on_the_grid_not_the_delta()
    {
        var before = P(5f, 0f, 0f);
        var snapped = MoveRules.Snap(before, P(5f + 30f, 0f, 0f), step: 32f, angleStepDegrees: 15f);
        Assert.Equal(32f, snapped.Position.X, 3);                 // 35 rounds to 32; a snapped delta would give 37
    }

    // M6c
    [Fact]
    public void A_turn_snaps_to_the_angle_step_and_a_move_keeps_its_rotation()
    {
        var before = P(0, 0, 0, yaw: 0.1f);
        var turned = MoveRules.Snap(before, P(0, 0, 0, yaw: 0.1f + float.DegreesToRadians(22f)), step: 1f, angleStepDegrees: 15f);
        Assert.Equal(float.DegreesToRadians(30f), turned.Rotation.Z, 4);
        var moved = MoveRules.Snap(before, P(64f, 0, 0, yaw: 0.1f), step: 128f, angleStepDegrees: 15f);
        Assert.Equal(0.1f, moved.Rotation.Z, 5);
    }

    // M6d
    [Fact]
    public void An_axis_lock_keeps_only_the_locked_part_of_a_move()
    {
        var d = new Vector3(30, -40, 12);
        Assert.Equal(new Vector3(30, 0, 0), MoveRules.Lock(d, AxisLock.X));
        Assert.Equal(new Vector3(0, 0, 12), MoveRules.Lock(d, AxisLock.Z));
        Assert.Equal(new Vector3(30, -40, 0), MoveRules.Lock(d, AxisLock.XY));
        Assert.Equal(d, MoveRules.Lock(d, AxisLock.None));
    }

    // M6e
    [Fact]
    public void A_typed_offset_is_read_as_a_move_or_a_turn_and_anything_else_is_refused_in_one_sentence()
    {
        var a = MoveRules.ParseOffset("+120 x");
        Assert.Null(a.Refusal); Assert.Equal(new Vector3(120, 0, 0), a.Delta);
        var b = MoveRules.ParseOffset("-30 z");
        Assert.Null(b.Refusal); Assert.Equal(new Vector3(0, 0, -30), b.Delta);
        var c = MoveRules.ParseOffset("15°");
        Assert.Null(c.Refusal); Assert.Equal(15f, c.TurnDegrees);
        var bad = MoveRules.ParseOffset("a bit left");
        Assert.NotNull(bad.Refusal);
        Assert.Contains("+120 x", bad.Refusal);                   // it says what to type
        Assert.DoesNotContain("\n", bad.Refusal);
    }

    // M6f: turn rings about X and Y on the handle
    [Fact]
    public void The_handle_has_rings_about_X_and_Y()
    {
        var g = new Gizmo { Centre = new Vector3(0, 0, 0), Size = 100f };
        var parts = g.Segments().Select(s => s.H).Distinct().ToList();
        Assert.Contains(Handle.TurnX, parts);
        Assert.Contains(Handle.TurnY, parts);
    }

    static CaptureViews.Geometry World(params PickShape[] extra) => CaptureViews.FromPickShapes(extra);

    // M6g: onto the terrain
    [Fact]
    public void Drop_lowers_onto_the_terrain_and_names_it()
    {
        var box = Boxes.Box(0x900, new Vec3(0, 0, 37), new Vector3(30, 30, 40));
        var g = World(box.Shapes.Append(Boxes.Terrain(0)).ToArray());
        var d = CaptureViews.Drop(g, box.Ref.RefKey.ToString());
        Assert.True(d.Refusal is null, "drop refused: " + d.Refusal);
        Assert.NotNull(d.Distance);
        Assert.Equal(37f, d.Distance!.Value, 2);
        Assert.Equal("terrain", d.Onto);
    }

    // M6h: onto another reference, named
    [Fact]
    public void Drop_lowers_onto_the_nearest_reference_below_and_names_it()
    {
        var box = Boxes.Box(0x901, new Vec3(0, 0, 150), new Vector3(20, 20, 20));
        var table = Boxes.Box(0x902, new Vec3(0, 0, 0), new Vector3(60, 60, 50), name: "TestTable");
        var g = World(box.Shapes.Concat(table.Shapes).Append(Boxes.Terrain(0)).ToArray());
        var d = CaptureViews.Drop(g, box.Ref.RefKey.ToString());
        Assert.True(d.Refusal is null, "drop refused: " + d.Refusal);
        Assert.NotNull(d.Distance);
        Assert.Equal(50f, d.Distance!.Value, 2);                  // the table's top is at 100
        Assert.Equal(table.Ref.RefKey.ToString(), d.Onto);
        Assert.Equal("TestTable", d.OntoName);
    }

    // M6i: lowers only; nothing below is a sentence
    [Fact]
    public void Drop_with_nothing_below_and_drop_of_a_sunk_object_are_refused_saying_it_lowers_only()
    {
        var floating = Boxes.Box(0x903, new Vec3(0, 0, 2000), new Vector3(20, 20, 20));
        var alone = CaptureViews.Drop(World(floating.Shapes.ToArray()), floating.Ref.RefKey.ToString());
        Assert.NotNull(alone.Refusal);
        Assert.Contains("lowers only", alone.Refusal);
        var sunk = Boxes.Box(0x904, new Vec3(0, 0, -30), new Vector3(20, 20, 40));
        // a second surface under the terrain: a sunk object is refused, never lowered onto what lies below
        var s = CaptureViews.Drop(World(sunk.Shapes.Append(Boxes.Terrain(0)).Append(Boxes.Terrain(-500)).ToArray()), sunk.Ref.RefKey.ToString());
        Assert.Null(s.Distance);
        Assert.Contains("lowers only", s.Refusal);
    }

    // M6j: the refactor keeps what Contacts says rests on what
    [Fact]
    public void Contacts_still_names_the_reference_a_box_rests_on()
    {
        var box = Boxes.Box(0x905, new Vec3(0, 0, 104), new Vector3(20, 20, 20));        // 4 units above the table
        var table = Boxes.Box(0x906, new Vec3(0, 0, 0), new Vector3(60, 60, 50));
        var floor = Boxes.Box(0x907, new Vec3(0, 0, -40), new Vector3(200, 200, 10));     // under the table, within reach
        var g = new CaptureViews.Geometry();
        foreach (var ro in new[] { box, table, floor }) foreach (var s in ro.Shapes) g.Add(ro.Ref.RefKey.ToString(), "x", s.Verts, s.Tris);
        var c = CaptureViews.Contacts(g, box.Ref.RefKey.ToString());
        Assert.Contains(table.Ref.RefKey.ToString(), c.RestsOn);
        Assert.DoesNotContain(floor.Ref.RefKey.ToString(), c.RestsOn);
    }
}
