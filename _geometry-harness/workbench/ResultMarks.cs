using System.Numerics;
using SkyRender;
using ViewProbe;
using GlVec3 = OpenTK.Mathematics.Vector3;

namespace Workbench;

/// <summary>How a marked object's box is drawn. Each style is a different shape, so the groups are told apart without
/// colour: a full box, a dashed box, corner brackets only, and a box with a cross on its top.</summary>
internal enum MarkStyle { Solid, Dashed, Corners, Crossed }

/// <summary>The scene marks of an opened evaluation's rows: every marked object's box in its group's shape and colour,
/// drawn by SkyRender's overlay line pass (through anything in front, as the conflict marks are).</summary>
internal sealed class ResultMarks : IRenderOverlay
{
    internal static readonly IReadOnlyDictionary<MarkStyle, (GlVec3 Colour, string Shape, string ColourName)> Styles =
        new Dictionary<MarkStyle, (GlVec3, string, string)>
        {
            [MarkStyle.Solid] = (new GlVec3(0.30f, 0.90f, 0.40f), "a full box", "green"),
            [MarkStyle.Dashed] = (new GlVec3(1.00f, 0.60f, 0.10f), "a dashed box", "orange"),
            [MarkStyle.Corners] = (new GlVec3(0.40f, 0.70f, 1.00f), "corner brackets", "blue"),
            [MarkStyle.Crossed] = (new GlVec3(0.80f, 0.80f, 0.80f), "a box with a cross on top", "grey"),
        };

    internal bool Enabled = true;
    readonly List<OverlayLineSegment> _lines = new();
    internal int Marked { get; private set; }
    readonly HashSet<MarkStyle> _shown = new();
    /// <summary>The box styles drawn now, for the view's key.</summary>
    internal IReadOnlyCollection<MarkStyle> StylesShown => _shown;

    /// <summary>Replace the marks: each object's box (Skyrim space) and its style.</summary>
    internal void Set(IEnumerable<(Vector3 Min, Vector3 Max, MarkStyle Style)> boxes)
    {
        _lines.Clear(); Marked = 0; _shown.Clear();
        foreach (var (min, max, style) in boxes)
        {
            if (min.X > max.X) continue;                     // an object the scene gave no box
            Marked++; _shown.Add(style);
            var col = Styles[style].Colour;
            foreach (var (a, b) in BoxLines(min, max, style))
            {
                var ya = TexturedScene.YUp(a); var yb = TexturedScene.YUp(b);
                _lines.Add(new OverlayLineSegment { A = new GlVec3(ya.X, ya.Y, ya.Z), B = new GlVec3(yb.X, yb.Y, yb.Z), Color = col });
            }
        }
    }

    public void Draw(GlRenderer renderer, in OverlayContext context)
    {
        if (!Enabled || _lines.Count == 0) return;
        if (!renderer.BeginOverlayLinePass(context, out var state)) return;
        renderer.DrawOverlayLines(_lines, 2.0f, context);
        renderer.EndOverlayLinePass(state);
    }

    /// <summary>The line segments of one box in a style, in the box's own space. A box thinner than a few units on an
    /// axis is widened to 4 units there, so a flat object still shows a shape.</summary>
    internal static IEnumerable<(Vector3 A, Vector3 B)> BoxLines(Vector3 min, Vector3 max, MarkStyle style)
    {
        for (int i = 0; i < 3; i++)
            if (max[i] - min[i] < 4f) { float mid = (max[i] + min[i]) * 0.5f; min[i] = mid - 2f; max[i] = mid + 2f; }
        Vector3 C(int n) => new((n & 1) == 0 ? min.X : max.X, (n & 2) == 0 ? min.Y : max.Y, (n & 4) == 0 ? min.Z : max.Z);
        // the 12 edges: pairs of corners that differ on one axis
        var edges = new List<(Vector3 A, Vector3 B)>();
        for (int a = 0; a < 8; a++)
            foreach (int bit in new[] { 1, 2, 4 })
                if ((a & bit) == 0) edges.Add((C(a), C(a | bit)));
        switch (style)
        {
            case MarkStyle.Solid:
                foreach (var e in edges) yield return e;
                break;
            case MarkStyle.Dashed:
                foreach (var (a, b) in edges)
                {
                    // an odd number of equal pieces, drawn and skipped in turn, so each edge starts and ends on a dash
                    float len = Vector3.Distance(a, b);
                    int pieces = Math.Clamp((int)(len / 24f) | 1, 3, 41);
                    for (int p = 0; p < pieces; p += 2)
                        yield return (Vector3.Lerp(a, b, p / (float)pieces), Vector3.Lerp(a, b, (p + 1) / (float)pieces));
                }
                break;
            case MarkStyle.Corners:
                foreach (var (a, b) in edges)
                {
                    yield return (a, Vector3.Lerp(a, b, 0.22f));
                    yield return (Vector3.Lerp(a, b, 0.78f), b);
                }
                break;
            case MarkStyle.Crossed:
                foreach (var e in edges) yield return e;
                yield return (C(4), C(7));                   // the top face's two diagonals
                yield return (C(5), C(6));
                break;
        }
    }

    /// <summary>The legend: each style's shape and colour, with what it stands for.</summary>
    internal static string Legend(IEnumerable<(MarkStyle Style, string Means)> items) =>
        string.Join("; ", items.Select(i => $"{Styles[i.Style].Shape} ({Styles[i.Style].ColourName}): {i.Means}"));
}
