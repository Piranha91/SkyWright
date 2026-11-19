using System.Numerics;
using HousecarlCore;
using Mutagen.Bethesda.Plugins;
using SkyRender;
using ViewProbe;
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
    readonly IReadOnlyCollection<(int X, int Y)> _cells;
    readonly (int X, int Y)? _own;
    readonly Func<float, float, float?> _height;
    List<float>? _ownTris, _ringTris;

    internal CellBorders(IReadOnlyCollection<(int X, int Y)> cells, (int X, int Y)? own, Func<float, float, float?> height)
    { _cells = cells; _own = own; _height = height; }

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
/// yellow diamond. Drawn by the overlay line pass, through anything in front, as the conflict marks are.</summary>
internal sealed class MarkerGizmos : IRenderOverlay
{
    internal static readonly GlVec3 MarkerColour = new(0.65f, 0.45f, 1f), NoModelColour = new(1f, 0.9f, 0.3f);
    internal bool Enabled;
    readonly List<OverlayLineSegment> _lines = new();
    internal int Markers { get; }
    internal int NoModel { get; }

    internal MarkerGizmos(IEnumerable<(FormKey Key, Vec3 Position, Vec3 Rotation, bool Marker)> places)
    {
        foreach (var (_, p, r, marker) in places)
        {
            var at = new Vector3(p.X, p.Y, p.Z);
            if (marker) { Markers++; foreach (var (a, b) in MarkerLines(at, r)) Add(a, b, MarkerColour); }
            else { NoModel++; foreach (var (a, b) in DiamondLines(at, 20f)) Add(a, b, NoModelColour); }
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

    public void Draw(GlRenderer renderer, in OverlayContext context)
    {
        if (!Enabled || _lines.Count == 0) return;
        if (!renderer.BeginOverlayLinePass(context, out var state)) return;
        renderer.DrawOverlayLines(_lines, 2f, context);
        renderer.EndOverlayLinePass(state);
    }
}
