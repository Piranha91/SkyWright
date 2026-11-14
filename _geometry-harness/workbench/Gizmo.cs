using System.Numerics;
using SkyRender;
using GlVec3 = OpenTK.Mathematics.Vector3;
using GlMatrix4 = OpenTK.Mathematics.Matrix4;

namespace Workbench;

/// <summary>Which part of the handle a press grabbed.</summary>
internal enum Handle { None, MoveX, MoveY, MoveZ, TurnZ }

/// <summary>The move/rotate handle on the selected reference: three world-axis arrows (Skyrim X red, Y green, Z blue)
/// that move it along that axis, and a ring about world Z that turns it (a yaw, composed with the reference's existing
/// rotation). Sized to a fixed share of the view at the reference. Pure geometry plus one overlay; the window decides
/// what a press and a drag do with it.</summary>
internal sealed class Gizmo : IRenderOverlay
{
    internal Vector3? Centre;          // Skyrim space; null = nothing selected, nothing drawn
    internal float Size = 100f;        // arrow length in world units, set per frame from the eye distance
    internal Handle Hot;               // the part under the cursor or being dragged, drawn brighter
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

    /// <summary>World units along a move axis for a drag from <paramref name="from"/> to <paramref name="to"/> on screen:
    /// the drag's component along the axis's projected direction, scaled by the arrow's world length over its screen length.</summary>
    internal float MoveAmount(Handle h, Vector2 from, Vector2 to, Func<Vector3, Vector2?> project)
    {
        if (Centre is not { } c || project(c) is not { } s0 || project(c + Axis(h) * Size) is not { } s1) return 0f;
        var dir = s1 - s0; float len = dir.Length();
        if (len < 1e-3f) return 0f;                      // the axis points at the eye: nothing sensible to drag along
        return Vector2.Dot(to - from, dir / len) * (Size / len);
    }

    /// <summary>Radians about world +Z (counter-clockwise seen from above) for a drag around the ring's centre on screen.</summary>
    internal float TurnAmount(Vector2 from, Vector2 to, Func<Vector3, Vector2?> project)
    {
        if (Centre is not { } c || project(c) is not { } sc) return 0f;
        float a0 = MathF.Atan2(from.Y - sc.Y, from.X - sc.X), a1 = MathF.Atan2(to.Y - sc.Y, to.X - sc.X);
        float d = a1 - a0;
        while (d > MathF.PI) d -= MathF.Tau;
        while (d < -MathF.PI) d += MathF.Tau;
        // Screen y runs down, so a screen angle increases clockwise as drawn. Which way the ring's own counter-clockwise
        // runs on screen depends on whether the eye is above or below it: read it off two projected ring points.
        float r = Size * 0.8f;
        if (project(c + Vector3.UnitX * r) is not { } px || project(c + Vector3.UnitY * r) is not { } py) return 0f;
        float ax = MathF.Atan2(px.Y - sc.Y, px.X - sc.X), ay = MathF.Atan2(py.Y - sc.Y, py.X - sc.X);
        float q = ay - ax; while (q > MathF.PI) q -= MathF.Tau; while (q < -MathF.PI) q += MathF.Tau;
        return q >= 0 ? d : -d;
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
                                 Handle.MoveZ => new GlVec3(.3f, .5f, 1f), _ => new GlVec3(1f, .85f, .2f) };
            if (h == Hot) col = new GlVec3(1f, 1f, 1f);
            var ya = YUp(a); var yb = YUp(b);
            _lines.Add(new OverlayLineSegment { A = new GlVec3(ya.X, ya.Y, ya.Z), B = new GlVec3(yb.X, yb.Y, yb.Z), Color = col });
        }
        if (!renderer.BeginOverlayLinePass(context, out var state)) return;
        renderer.DrawOverlayLines(_lines, 3f, context);
        renderer.EndOverlayLinePass(state);
    }
}
