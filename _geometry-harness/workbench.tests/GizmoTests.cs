using System.Numerics;
using Workbench;
using Xunit;

namespace WorkbenchTests;

/// <summary>The handle's drag maths, from synthetic eye rays (no window).</summary>
[Trait("tier", "unit")]
public class GizmoTests
{
    // An eye above and to the south of the ring, looking down at it obliquely: the case where a screen angle and a
    // world angle disagree.
    static readonly Vector3 Eye = new(0, -600, 450);
    static (Vector3 O, Vector3 D) RayTo(Vector3 p) => (Eye, Vector3.Normalize(p - Eye));

    [Theory]
    [InlineData(30f)]
    [InlineData(-75f)]
    [InlineData(120f)]
    public void A_drag_that_follows_the_ring_by_some_degrees_turns_by_those_degrees(float degrees)
    {
        var g = new Gizmo { Centre = new Vector3(100, 50, 0), Size = 200 };
        float r = g.Size * 0.8f, t = float.DegreesToRadians(degrees);
        var a = g.Centre!.Value + new Vector3(r, 0, 0);
        var b = g.Centre!.Value + new Vector3(MathF.Cos(t) * r, MathF.Sin(t) * r, 0);
        // The screen points stand in for the two rays: the ray function maps them back.
        var rays = new Dictionary<Vector2, (Vector3, Vector3)> { [new(1, 1)] = RayTo(a), [new(2, 2)] = RayTo(b) };
        float got = g.TurnAmount(new(1, 1), new(2, 2), p => rays[p]);
        Assert.Equal(t, got, 4);
    }

    [Theory]
    [InlineData("x", 400f)]
    [InlineData("y", 400f)]
    [InlineData("z", -150f)]
    public void A_drag_that_follows_an_arrow_by_some_units_moves_by_those_units(string axis, float units)
    {
        var h = axis == "x" ? Handle.MoveX : axis == "y" ? Handle.MoveY : Handle.MoveZ;
        var g = new Gizmo { Centre = new Vector3(100, 50, 0), Size = 200 };
        var u = h == Handle.MoveX ? Vector3.UnitX : h == Handle.MoveY ? Vector3.UnitY : Vector3.UnitZ;
        var a = g.Centre!.Value + u * g.Size * 0.9f;
        var b = a + u * units;
        var rays = new Dictionary<Vector2, (Vector3, Vector3)> { [new(1, 1)] = RayTo(a), [new(2, 2)] = RayTo(b) };
        Assert.Equal(units, g.MoveAmount(h, new(1, 1), new(2, 2), p => rays[p]), 2);
    }
}
