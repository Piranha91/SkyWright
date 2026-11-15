using System.Numerics;
using SkyRender;
using GlVec3 = OpenTK.Mathematics.Vector3;
using GlMatrix4 = OpenTK.Mathematics.Matrix4;

namespace Workbench;

/// <summary>Which part of the handle a press grabbed.</summary>
internal enum Handle { None, MoveX, MoveY, MoveZ, TurnZ, TurnX, TurnY }

/// <summary>The move/rotate handle on the selected reference: three world-axis arrows (Skyrim X red, Y green, Z blue)
/// that move it along that axis, and a ring about world Z that turns it (a yaw, composed with the reference's existing
/// rotation). Sized to a fixed share of the view at the reference. Pure geometry plus one overlay; the window decides
/// what a press and a drag do with it.</summary>
internal sealed class Gizmo : IRenderOverlay
{
    internal Vector3? Centre;          // Skyrim space; null = nothing selected, nothing drawn
    internal float Size = 100f;        // arrow length in world units, set per frame from the eye distance
    internal Handle Hot;               // the part under the cursor or being dragged, drawn brighter
    internal bool Hidden;              // while originals show: no handle where the object is not
    const int RingSegments = 48;

    static Vector3 Axis(Handle h) => h switch { Handle.MoveX => Vector3.UnitX, Handle.MoveY => Vector3.UnitY, _ => Vector3.UnitZ };
    static Vector3 YUp(Vector3 z) => new(z.X, z.Z, -z.Y);

    internal void FitTo(Vector3 eyeSkyrim)
    {
        if (Centre is { } c) Size = MathF.Max(Vector3.Distance(eyeSkyrim, c) * 0.15f, 8f);
    }

    /// <summary>Segments of each part, Skyrim space.</summary>
    internal IEnumerable<(Handle H, Vector3 A, Vector3 B)> Segments()
    {
        if (Centre is not { } c) yield break;
        foreach (var h in new[] { Handle.MoveX, Handle.MoveY, Handle.MoveZ }) yield return (h, c, c + Axis(h) * Size);
        float r = Size * 0.8f;
        for (int i = 0; i < RingSegments; i++)
        {
            float a0 = i * MathF.Tau / RingSegments, a1 = (i + 1) * MathF.Tau / RingSegments;
            yield return (Handle.TurnZ, c + new Vector3(MathF.Cos(a0), MathF.Sin(a0), 0) * r, c + new Vector3(MathF.Cos(a1), MathF.Sin(a1), 0) * r);
        }
        // the tilt rings, a little smaller: about world X (in the Y-Z plane) and about world Y (in the X-Z plane)
        float rt = Size * 0.6f;
        for (int i = 0; i < RingSegments; i++)
        {
            float a0 = i * MathF.Tau / RingSegments, a1 = (i + 1) * MathF.Tau / RingSegments;
            yield return (Handle.TurnX, c + new Vector3(0, MathF.Cos(a0), MathF.Sin(a0)) * rt, c + new Vector3(0, MathF.Cos(a1), MathF.Sin(a1)) * rt);
            yield return (Handle.TurnY, c + new Vector3(MathF.Cos(a0), 0, MathF.Sin(a0)) * rt, c + new Vector3(MathF.Cos(a1), 0, MathF.Sin(a1)) * rt);
        }
    }

    /// <summary>The part within <paramref name="pixels"/> of a screen point, nearest first; <see cref="Handle.None"/> if none.</summary>
    internal Handle HitTest(Vector2 p, Func<Vector3, Vector2?> project, float pixels = 8f)
    {
        Handle best = Handle.None; float bestD = pixels;
        foreach (var (h, a, b) in Segments())
        {
            if (project(a) is not { } sa || project(b) is not { } sb) continue;
            float d = DistanceToSegment(p, sa, sb);
            if (d <= bestD) { bestD = d; best = h; }
        }
        return best;
    }

    /// <summary>World units along a move axis for a drag from <paramref name="from"/> to <paramref name="to"/>: for each
    /// screen point, the point of the axis line (through the centre) nearest its eye ray (Skyrim space); the move is the
    /// distance between the two along the axis, so a drag that follows the arrow 400 units moves 400 units from any eye.</summary>
    internal float MoveAmount(Handle h, Vector2 from, Vector2 to, Func<Vector2, (Vector3 O, Vector3 D)> ray)
    {
        if (Centre is not { } c) return 0f;
        var u = Axis(h);
        if (Along(ray(from)) is not { } s0 || Along(ray(to)) is not { } s1) return 0f;
        return s1 - s0;

        float? Along((Vector3 O, Vector3 D) r)
        {
            var d = Vector3.Normalize(r.D);
            float b = Vector3.Dot(u, d);
            float den = 1f - b * b;
            if (den < 1e-6f) return null;                            // the axis points at the eye: nothing to read
            var w0 = c - r.O;
            return (b * Vector3.Dot(d, w0) - Vector3.Dot(u, w0)) / den;
        }
    }

    /// <summary>Radians about world +Z (counter-clockwise seen from above) for a drag around the ring: each screen point's
    /// ray (Skyrim space) is met with the ring's own horizontal plane and the turn is the world angle between the two
    /// meeting points about the centre, so a drag that follows the ring by 30° turns the reference by 30° from any eye.</summary>
    internal float TurnAmount(Vector2 from, Vector2 to, Func<Vector2, (Vector3 O, Vector3 D)> ray)
    {
        if (Centre is not { } c) return 0f;
        if (OnPlane(ray(from), c.Z) is not { } p0 || OnPlane(ray(to), c.Z) is not { } p1) return 0f;
        float d = MathF.Atan2(p1.Y - c.Y, p1.X - c.X) - MathF.Atan2(p0.Y - c.Y, p0.X - c.X);
        while (d > MathF.PI) d -= MathF.Tau;
        while (d < -MathF.PI) d += MathF.Tau;
        return d;

        static Vector3? OnPlane((Vector3 O, Vector3 D) r, float z)
        {
            if (MathF.Abs(r.D.Z) < 1e-6f) return null;              // looking along the plane: no angle to read
            float t = (z - r.O.Z) / r.D.Z;
            return t <= 0 ? null : r.O + r.D * t;
        }
    }

    /// <summary>The axis a turn handle turns about, world space.</summary>
    internal static Vector3 TurnAxis(Handle h) => h switch { Handle.TurnX => Vector3.UnitX, Handle.TurnY => Vector3.UnitY, _ => Vector3.UnitZ };

    /// <summary>Radians about <paramref name="axis"/> (counter-clockwise seen from its tip) for a drag around that ring:
    /// each screen point's ray meets the ring's plane, and the turn is the angle between the two meeting points.</summary>
    internal float TurnAmountAbout(Vector3 axis, Vector2 from, Vector2 to, Func<Vector2, (Vector3 O, Vector3 D)> ray)
    {
        if (Centre is not { } c) return 0f;
        if (axis == Vector3.UnitZ) return TurnAmount(from, to, ray);
        if (Meet(ray(from)) is not { } p0 || Meet(ray(to)) is not { } p1) return 0f;
        var u = Vector3.Normalize(Vector3.Cross(axis, MathF.Abs(axis.Z) < 0.9f ? Vector3.UnitZ : Vector3.UnitX));
        var v = Vector3.Cross(axis, u);
        float A(Vector3 p) => MathF.Atan2(Vector3.Dot(p - c, v), Vector3.Dot(p - c, u));
        float d = A(p1) - A(p0);
        while (d > MathF.PI) d -= MathF.Tau;
        while (d < -MathF.PI) d += MathF.Tau;
        return d;

        Vector3? Meet((Vector3 O, Vector3 D) r)
        {
            float den = Vector3.Dot(r.D, axis);
            if (MathF.Abs(den) < 1e-6f) return null;
            float t = Vector3.Dot(c - r.O, axis) / den;
            return t <= 0 ? null : r.O + r.D * t;
        }
    }

    static float DistanceToSegment(Vector2 p, Vector2 a, Vector2 b)
    {
        var ab = b - a; float l2 = ab.LengthSquared();
        float t = l2 < 1e-6f ? 0f : Math.Clamp(Vector2.Dot(p - a, ab) / l2, 0f, 1f);
        return Vector2.Distance(p, a + ab * t);
    }

    readonly List<OverlayLineSegment> _lines = new();

    public void Draw(GlRenderer renderer, in OverlayContext context)
    {
        if (Centre is null) return;
        _lines.Clear();
        foreach (var (h, a, b) in Segments())
        {
            var col = h switch { Handle.MoveX => new GlVec3(1f, .2f, .2f), Handle.MoveY => new GlVec3(.2f, 1f, .2f),
                                 Handle.MoveZ => new GlVec3(.3f, .5f, 1f), Handle.TurnX => new GlVec3(1f, .5f, .5f),
                                 Handle.TurnY => new GlVec3(.5f, 1f, .5f), _ => new GlVec3(1f, .85f, .2f) };
            if (h == Hot) col = new GlVec3(1f, 1f, 1f);
            var ya = YUp(a); var yb = YUp(b);
            _lines.Add(new OverlayLineSegment { A = new GlVec3(ya.X, ya.Y, ya.Z), B = new GlVec3(yb.X, yb.Y, yb.Z), Color = col });
        }
        if (!renderer.BeginOverlayLinePass(context, out var state)) return;
        renderer.DrawOverlayLines(_lines, 3f, context);
        renderer.EndOverlayLinePass(state);
    }
}
