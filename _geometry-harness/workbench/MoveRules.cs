using System.Numerics;
using ViewProbe;

namespace Workbench;

/// <summary>Which world axes a move may change.</summary>
internal enum AxisLock { None, X, Y, Z, XY }

/// <summary>Precise movement: axis locks, snapping and typed offsets, applied to a staged edit.</summary>
internal static class MoveRules
{
    /// <summary>Round the result of an edit to the grid and the angle step, on the axes the edit changed only.</summary>
    internal static RefPlacementEdit.Pose Snap(RefPlacementEdit.Pose before, RefPlacementEdit.Pose after, float step, float angleStepDegrees) => after;

    /// <summary>The part of a move the lock allows.</summary>
    internal static Vector3 Lock(Vector3 delta, AxisLock axes) => delta;

    /// <summary>A typed offset such as "+120 x", "-30 z" or "15°" (a turn about world Z), or one sentence saying why not.</summary>
    internal static (Vector3 Delta, float TurnDegrees, string? Refusal) ParseOffset(string text) => (Vector3.Zero, 0f, "typed offsets are not built yet.");
}
