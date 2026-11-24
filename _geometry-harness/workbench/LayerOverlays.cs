using System.Numerics;
using HousecarlCore;
using SkyWright.Engine;
using Mutagen.Bethesda.Plugins;
using SkyRender;
using ViewProbe;
using OpenTK.Graphics.OpenGL4;
using GlVec3 = OpenTK.Mathematics.Vector3;

namespace Workbench;

/// <summary>The exterior cell grid as curtains standing on the drawn ground: a gold one around the scene's own cell and a
/// pale blue one around each cell loaded beside it, translucent and depth-tested, so a border behind a hill stays behind
/// it. The ground is viewprobe's own terrain height (<see cref="InteractiveOptions.TerrainHeightAt"/>).</summary>
internal sealed class CellBorders : IRenderOverlay
{
    internal const float Cell = 4096f, Step = 128f, Height = 192f, Below = 8f;
    internal static readonly GlVec3 OwnColour = new(1f, 0.78f, 0.2f), RingColour = new(0.55f, 0.75f, 1f);
    internal bool Enabled;
    /// <summary>The white dashed outline of the scene's own cell, riding the ground; <see cref="Through"/> draws it through
    /// whatever stands in front of it, otherwise it is hidden behind it like the scene.</summary>
    internal bool Outline, Through;
    readonly List<OverlayLineSegment> _outline = new();
    readonly IReadOnlyCollection<(int X, int Y)> _cells;
    readonly (int X, int Y)? _own;
    readonly Func<float, float, float?> _height;
    List<float>? _ownTris, _ringTris;

    internal CellBorders(IReadOnlyCollection<(int X, int Y)> cells, (int X, int Y)? own, Func<float, float, float?> height)
    {
        _cells = cells; _own = own; _height = height;
        if (own is { } o) OutlineOf(o);
    }

    /// <summary>The own cell's edges as dashes 64 units long with 64-unit gaps, 6 units above the ground (viewprobe's
    /// outline, which a live scene cannot recentre).</summary>
    void OutlineOf((int X, int Y) o)
    {
        float x0 = o.X * Cell, y0 = o.Y * Cell, x1 = x0 + Cell, y1 = y0 + Cell;
        void Edge(float ax, float ay, float bx, float by)
        {
            const int n = (int)(Cell / 64f);
            for (int i = 0; i < n; i += 2)
            {
                float fa = (float)i / n, fb = (float)(i + 1) / n;
                float xa = ax + (bx - ax) * fa, ya = ay + (by - ay) * fa, xb = ax + (bx - ax) * fb, yb = ay + (by - ay) * fb;
                if (Ground(xa, ya) is not { } za || Ground(xb, yb) is not { } zb) continue;
                var pa = TexturedScene.YUp(new Vector3(xa, ya, za + 6f)); var pb = TexturedScene.YUp(new Vector3(xb, yb, zb + 6f));
                _outline.Add(new OverlayLineSegment { A = new GlVec3(pa.X, pa.Y, pa.Z), B = new GlVec3(pb.X, pb.Y, pb.Z), Color = new GlVec3(1f, 1f, 1f) });
            }
        }
        Edge(x0, y0, x1, y0); Edge(x1, y0, x1, y1); Edge(x1, y1, x0, y1); Edge(x0, y1, x0, y0);
    }

    internal int OutlineDashes => _outline.Count;

    /// <summary>Each cell edge once, as (start, end) on the grid, with whether it borders the scene's own cell.</summary>
    internal static List<((int X, int Y) A, (int X, int Y) B, bool Own)> Edges(IReadOnlyCollection<(int X, int Y)> cells, (int X, int Y)? own)
    {
        var seen = new Dictionary<((int, int), (int, int)), bool>();
        foreach (var (x, y) in cells)
        {
            bool mine = own is { } o && o.X == x && o.Y == y;
            foreach (var e in new[] { ((x, y), (x + 1, y)), ((x + 1, y), (x + 1, y + 1)), ((x, y + 1), (x + 1, y + 1)), ((x, y), (x, y + 1)) })
                seen[e] = seen.GetValueOrDefault(e) || mine;
        }
        return seen.Select(kv => (kv.Key.Item1, kv.Key.Item2, kv.Value)).ToList();
    }

    /// <summary>The ground under a point on a cell edge: the drawn land on either side of it.</summary>
    float? Ground(float x, float y) => _height(x, y) ?? _height(x - 0.5f, y) ?? _height(x, y - 0.5f) ?? _height(x - 0.5f, y - 0.5f);

    void Build()
    {
        _ownTris = new(); _ringTris = new();
        foreach (var (a, b, own) in Edges(_cells, _own))
        {
            var into = own ? _ownTris : _ringTris;
            float ax = a.X * Cell, ay = a.Y * Cell, bx = b.X * Cell, by = b.Y * Cell;
            int n = (int)(Cell / Step);
            for (int i = 0; i < n; i++)
            {
                float x0 = ax + (bx - ax) * i / n, y0 = ay + (by - ay) * i / n, x1 = ax + (bx - ax) * (i + 1) / n, y1 = ay + (by - ay) * (i + 1) / n;
                if (Ground(x0, y0) is not { } z0 || Ground(x1, y1) is not { } z1) continue;
                Quad(into, new Vector3(x0, y0, z0 - Below), new Vector3(x1, y1, z1 - Below), new Vector3(x1, y1, z1 + Height), new Vector3(x0, y0, z0 + Height));
            }
        }
    }

    // both faces, so the curtain reads from either side
    static void Quad(List<float> into, Vector3 a, Vector3 b, Vector3 c, Vector3 d)
    {
        void Tri(Vector3 p, Vector3 q, Vector3 r)
        {
            var n = Vector3.Cross(q - p, r - p); n = n.LengthSquared() > 0 ? Vector3.Normalize(n) : Vector3.UnitZ;
            foreach (var v in new[] { p, q, r })
            {
                var y = TexturedScene.YUp(v); var ny = TexturedScene.YUp(n);
                into.Add(y.X); into.Add(y.Y); into.Add(y.Z); into.Add(ny.X); into.Add(ny.Y); into.Add(ny.Z);
            }
        }
        Tri(a, b, c); Tri(a, c, d); Tri(a, c, b); Tri(a, d, c);
    }

    /// <summary>How many curtain triangles the borders draw (built on first ask).</summary>
    internal int Triangles { get { if (_ownTris is null) Build(); return (_ownTris!.Count + _ringTris!.Count) / 18; } }

    public void Draw(GlRenderer renderer, in OverlayContext context)
    {
        if (Outline && _outline.Count > 0 && renderer.BeginOverlayLinePass(context, out var lines))
        {
            // the line pass turns the depth test off; behind-objects turns it back on (the pass restores what it saved)
            if (!Through) { GL.Enable(EnableCap.DepthTest); GL.DepthFunc(DepthFunction.Lequal); }
            renderer.DrawOverlayLines(_outline, 3f, context);
            renderer.EndOverlayLinePass(lines);
        }
        if (!Enabled) return;
        if (_ownTris is null) Build();
        if (_ownTris!.Count < 18 && _ringTris!.Count < 18) return;
        if (!renderer.BeginOverlaySurfacePass(context, out var st)) return;
        var none = Array.Empty<(int, int, GlVec3)>();
        if (_ringTris!.Count >= 18) renderer.DrawOverlayTranslucentTint(_ringTris, RingColour, 0.16f, none);
        if (_ownTris.Count >= 18) renderer.DrawOverlayTranslucentTint(_ownTris, OwnColour, 0.3f, none);
        renderer.EndOverlaySurfacePass(st);
    }
}

/// <summary>What the game does not draw, where the walk met it (<see cref="InteractiveOptions.UndrawnAt"/>): an editor
/// marker as a violet stem with an arrow the way it faces, and an object with no model (most lights, sound markers) as a
/// yellow diamond, drawn solid (a wire diamond was not read as a shape until seen filled). Drawn through anything in front.</summary>
internal sealed class MarkerGizmos : IRenderOverlay
{
    internal static readonly GlVec3 MarkerColour = new(0.65f, 0.45f, 1f), NoModelColour = new(1f, 0.9f, 0.3f);
    internal bool Enabled;
    readonly List<OverlayLineSegment> _lines = new();
    readonly List<float> _diamonds = new();
    internal int Markers { get; }
    internal int NoModel { get; }

    internal MarkerGizmos(IEnumerable<(FormKey Key, Vec3 Position, Vec3 Rotation, bool Marker)> places)
    {
        foreach (var (_, p, r, marker) in places)
        {
            var at = new Vector3(p.X, p.Y, p.Z);
            if (marker) { Markers++; foreach (var (a, b) in MarkerLines(at, r)) Add(a, b, MarkerColour); }
            else { NoModel++; DiamondTriangles(_diamonds, at, 20f); }
        }
    }

    void Add(Vector3 a, Vector3 b, GlVec3 colour)
    {
        var ya = TexturedScene.YUp(a); var yb = TexturedScene.YUp(b);
        _lines.Add(new OverlayLineSegment { A = new GlVec3(ya.X, ya.Y, ya.Z), B = new GlVec3(yb.X, yb.Y, yb.Z), Color = colour });
    }

    /// <summary>A marker: a stem 96 units up, a cross at its foot, and an arrow 72 units long the way the marker faces (its local +Y).</summary>
    internal static IEnumerable<(Vector3 A, Vector3 B)> MarkerLines(Vector3 at, Vec3 rotation)
    {
        var face = Vector3.TransformNormal(Vector3.UnitY, SpatialOverlap.RefrRotation(rotation));
        face = new Vector3(face.X, face.Y, 0f);
        face = face.LengthSquared() > 1e-6f ? Vector3.Normalize(face) : Vector3.UnitY;
        var side = new Vector3(face.Y, -face.X, 0f);
        var lift = new Vector3(0, 0, 8f);
        yield return (at, at + new Vector3(0, 0, 96f));
        yield return (at - Vector3.UnitX * 18f, at + Vector3.UnitX * 18f);
        yield return (at - Vector3.UnitY * 18f, at + Vector3.UnitY * 18f);
        var tip = at + lift + face * 72f;
        yield return (at + lift, tip);
        yield return (tip, tip - face * 20f + side * 13f);
        yield return (tip, tip - face * 20f - side * 13f);
    }

    /// <summary>A diamond (an octahedron's 12 edges) of radius <paramref name="r"/> around the point.</summary>
    internal static IEnumerable<(Vector3 A, Vector3 B)> DiamondLines(Vector3 at, float r)
    {
        var up = at + new Vector3(0, 0, r); var down = at - new Vector3(0, 0, r);
        var ring = new[] { at + new Vector3(r, 0, 0), at + new Vector3(0, r, 0), at - new Vector3(r, 0, 0), at - new Vector3(0, r, 0) };
        for (int i = 0; i < 4; i++)
        {
            yield return (ring[i], ring[(i + 1) % 4]);
            yield return (ring[i], up);
            yield return (ring[i], down);
        }
    }

    /// <summary>A solid diamond (an octahedron's 8 faces, wound outward) of radius <paramref name="r"/> around the point,
    /// added to a 6-float (position, normal) triangle list in the view's Y-up space.</summary>
    internal static void DiamondTriangles(List<float> into, Vector3 at, float r)
    {
        var up = at + new Vector3(0, 0, r); var down = at - new Vector3(0, 0, r);
        var ring = new[] { at + new Vector3(r, 0, 0), at + new Vector3(0, r, 0), at - new Vector3(r, 0, 0), at - new Vector3(0, r, 0) };
        for (int i = 0; i < 4; i++)
        {
            Vector3 a = ring[i], b = ring[(i + 1) % 4];
            OutwardTriangle(into, at, a, b, up);
            OutwardTriangle(into, at, a, b, down);
        }
    }

    /// <summary>A Skyrim-space triangle into a Y-up list, wound so its face points away from <paramref name="centre"/>.</summary>
    static void OutwardTriangle(List<float> into, Vector3 centre, Vector3 a, Vector3 b, Vector3 c)
    {
        Vector3 ya = TexturedScene.YUp(a), yb = TexturedScene.YUp(b), yc = TexturedScene.YUp(c), yo = TexturedScene.YUp(centre);
        var n = Vector3.Cross(yb - ya, yc - ya);
        if (Vector3.Dot(n, (ya + yb + yc) / 3f - yo) < 0) (yb, yc) = (yc, yb);
        ConflictMarks.AddTriangle(into, ya, yb, yc);
    }

    public void Draw(GlRenderer renderer, in OverlayContext context)
    {
        if (!Enabled || (_lines.Count == 0 && _diamonds.Count == 0)) return;
        if (_diamonds.Count >= 18 && renderer.BeginOverlaySurfacePass(context, out var st))
        {
            renderer.DrawOverlaySolidTriangles(_diamonds, NoModelColour);
            renderer.EndOverlaySurfacePass(st);
        }
        if (_lines.Count == 0 || !renderer.BeginOverlayLinePass(context, out var state)) return;
        renderer.DrawOverlayLines(_lines, 2f, context);
        renderer.EndOverlayLinePass(state);
    }
}
