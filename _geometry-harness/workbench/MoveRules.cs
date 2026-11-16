using System.Globalization;
using System.Numerics;
using System.Text.RegularExpressions;
using HousecarlCore;
using ViewProbe;

namespace Workbench;

/// <summary>Which world axes a move may change.</summary>
internal enum AxisLock { None, X, Y, Z, XY }

/// <summary>Precise movement: axis locks, snapping and typed offsets, applied to a staged edit.</summary>
internal static class MoveRules
{
    /// <summary>A dependent carried by its host's move: the host's rigid transform, before to after, applied to the
    /// dependent's placement (a turn of the host swings it along the arc and turns it too).</summary>
    internal static RefPlacementEdit.Pose Carry(RefPlacementEdit.Pose hostBefore, RefPlacementEdit.Pose hostAfter, RefPlacementEdit.Pose dependent)
        => dependent with { Position = new Vec3(dependent.Position.X + hostAfter.Position.X - hostBefore.Position.X,
                                                dependent.Position.Y + hostAfter.Position.Y - hostBefore.Position.Y,
                                                dependent.Position.Z + hostAfter.Position.Z - hostBefore.Position.Z) };

    internal static AxisLock ParseLock(string? text) => (text ?? "").Trim().ToLowerInvariant() switch
    {
        "" or "none" => AxisLock.None, "x" => AxisLock.X, "y" => AxisLock.Y, "z" => AxisLock.Z, "xy" => AxisLock.XY,
        var t => throw new InvalidOperationException($"axis '{t}' is not an axis lock; use x, y, z, xy or none."),
    };

    /// <summary>Round the result of an edit to the grid and the angle step, on the axes the edit changed only. A move
    /// keeps its rotation; a turn about Z snaps the yaw. A step of 0 leaves that part alone.</summary>
    internal static RefPlacementEdit.Pose Snap(RefPlacementEdit.Pose before, RefPlacementEdit.Pose after, float step, float angleStepDegrees)
    {
        static float Round(float v, float s) => s > 0 ? MathF.Round(v / s) * s : v;
        static bool Moved(float a, float b) => MathF.Abs(a - b) > 1e-4f;
        var p = after.Position; var b0 = before.Position;
        var pos = new Vec3(Moved(p.X, b0.X) ? Round(p.X, step) : p.X, Moved(p.Y, b0.Y) ? Round(p.Y, step) : p.Y, Moved(p.Z, b0.Z) ? Round(p.Z, step) : p.Z);
        var rot = after.Rotation;
        if (angleStepDegrees > 0 && Moved(rot.Z, before.Rotation.Z) && !Moved(rot.X, before.Rotation.X) && !Moved(rot.Y, before.Rotation.Y))
            rot = rot with { Z = float.DegreesToRadians(Round(float.RadiansToDegrees(rot.Z), angleStepDegrees)) };
        return after with { Position = pos, Rotation = rot };
    }

    /// <summary>A turn amount in degrees rounded to the step (0: as given).</summary>
    internal static float SnapTurn(float degrees, float stepDegrees) => stepDegrees > 0 ? MathF.Round(degrees / stepDegrees) * stepDegrees : degrees;

    /// <summary>The part of a move the lock allows.</summary>
    internal static Vector3 Lock(Vector3 delta, AxisLock axes) => axes switch
    {
        AxisLock.X => new Vector3(delta.X, 0, 0),
        AxisLock.Y => new Vector3(0, delta.Y, 0),
        AxisLock.Z => new Vector3(0, 0, delta.Z),
        AxisLock.XY => new Vector3(delta.X, delta.Y, 0),
        _ => delta,
    };

    static readonly Regex Move = new(@"^\s*([+-]?\d+(?:\.\d+)?)\s*([xyz])\s*$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    static readonly Regex Turn = new(@"^\s*([+-]?\d+(?:\.\d+)?)\s*(?:°|deg|degrees)\s*$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    /// <summary>A typed offset such as "+120 x", "-30 z" or "15°" (a turn about world Z), or one sentence saying why not.</summary>
    internal static (Vector3 Delta, float TurnDegrees, string? Refusal) ParseOffset(string text)
    {
        if (Move.Match(text ?? "") is { Success: true } m)
        {
            float v = float.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture);
            return (char.ToLowerInvariant(m.Groups[2].Value[0]) switch { 'x' => new Vector3(v, 0, 0), 'y' => new Vector3(0, v, 0), _ => new Vector3(0, 0, v) }, 0f, null);
        }
        if (Turn.Match(text ?? "") is { Success: true } t)
            return (Vector3.Zero, float.Parse(t.Groups[1].Value, CultureInfo.InvariantCulture), null);
        return (Vector3.Zero, 0f, $"'{text}' is not an offset; type a distance and an axis such as +120 x or -30 z, or a turn such as 15°.");
    }
}
