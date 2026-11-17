using HousecarlCore;
using Mutagen.Bethesda.Plugins;

namespace HarnessShared;

// WHERE TWO NAVMESHES MEET: the geometric half of a cross-navmesh edge link, shared by navownprobe's border rules and the
// relink plan (task 2026-11-16-navmesh-repair). A link is a source triangle edge whose flag bit switches its link field to the
// navmesh's edge-link table; the entry names a navmesh and a triangle on the far side. The link is MATCHED when one edge of
// that far triangle lies along the source edge: their projections on the source edge overlap over at least half the shorter
// edge, and over that overlap the far edge stays within the plan and height tolerance of the source edge.
//
// A CANDIDATE is any open edge (no neighbour inside its own navmesh) of another navmesh in the same space that matches the
// source edge the same way. One candidate triangle is a match a relink can take; two or more are AMBIGUOUS, and nothing takes
// either (a nearest-wins choice would silently pick one, review finding 2, 2026-11-16).

/// <summary>One winning navmesh as the border rules read it: its geometry and where it is.</summary>
public sealed record WinningNav(NavmeshGeometry Geom, string Space, (int X, int Y)? Grid, FormKey Cell);

/// <summary>The plan and height tolerance an edge match is held to.</summary>
public readonly record struct EdgeTolerance(float Plan, float Height)
{
    public static EdgeTolerance Parse(string s)
    {
        var p = s.Split(',');
        if (p.Length != 2 || !float.TryParse(p[0], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var a)
            || !float.TryParse(p[1], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var b) || a < 0 || b < 0)
            throw new ArgumentException($"--edge-tolerance '{s}' is not PLAN,HEIGHT in game units (two numbers, for example 8,64).");
        return new EdgeTolerance(a, b);
    }
    public override string ToString() => FormattableString.Invariant($"{Plan:0.###} u in plan, {Height:0.###} u in height");
}

/// <summary>How well a far edge lies along a source edge: the overlap share of the shorter edge, and the largest plan and
/// height distance from the source edge over the overlap. <see cref="Overlap"/> 0 means the projections do not overlap.</summary>
public readonly record struct EdgeGap(float Overlap, float Plan, float Height)
{
    public static readonly EdgeGap None = new(0, float.PositiveInfinity, float.PositiveInfinity);
    public bool Within(EdgeTolerance t) => Overlap >= 0.5f && Plan <= t.Plan && Height <= t.Height;
}

public static class NavBorderMatch
{
    /// <summary>The three edges of a triangle by the edge-link field order: 0-1, 1-2, 2-0.</summary>
    public static (int A, int B) EdgeVerts(NavTri t, int e) => e switch { 0 => (t.A, t.B), 1 => (t.B, t.C), _ => (t.C, t.A) };
    public static short EdgeLink(NavTri t, int e) => e switch { 0 => t.EdgeLink01, 1 => t.EdgeLink12, _ => t.EdgeLink20 };
    public static bool EdgeIsLink(NavTri t, int e) => (t.Flags & (1 << e)) != 0;

    /// <summary>The gap between source edge <paramref name="a"/>-<paramref name="b"/> and far edge <paramref name="c"/>-<paramref name="d"/>.</summary>
    public static EdgeGap Gap(Vec3 a, Vec3 b, Vec3 c, Vec3 d)
    {
        float dx = b.X - a.X, dy = b.Y - a.Y, len2 = dx * dx + dy * dy;
        if (len2 < 1e-6f) return EdgeGap.None;
        float len = MathF.Sqrt(len2);
        float Proj(Vec3 p) => ((p.X - a.X) * dx + (p.Y - a.Y) * dy) / len;
        float pc = Proj(c), pd = Proj(d);
        float lo = Math.Max(0, Math.Min(pc, pd)), hi = Math.Min(len, Math.Max(pc, pd));
        float farLen = MathF.Sqrt((d.X - c.X) * (d.X - c.X) + (d.Y - c.Y) * (d.Y - c.Y));
        float shorter = Math.Min(len, farLen);
        if (hi <= lo || shorter < 1e-3f) return EdgeGap.None;
        // the far edge's point at source parameter s (by its own projection), and the source edge's point there
        float plan = 0, height = 0;
        foreach (var s in new[] { lo, hi })
        {
            float u = Math.Abs(pd - pc) < 1e-6f ? 0 : (s - pc) / (pd - pc);
            var f = new Vec3(c.X + (d.X - c.X) * u, c.Y + (d.Y - c.Y) * u, c.Z + (d.Z - c.Z) * u);
            float v = s / len;
            var g = new Vec3(a.X + dx * v, a.Y + dy * v, a.Z + (b.Z - a.Z) * v);
            plan = Math.Max(plan, MathF.Sqrt((f.X - g.X) * (f.X - g.X) + (f.Y - g.Y) * (f.Y - g.Y)));
            height = Math.Max(height, Math.Abs(f.Z - g.Z));
        }
        return new EdgeGap((hi - lo) / shorter, plan, height);
    }

    /// <summary>The far triangle's edge that lies best along the source edge: of the edges whose projection overlaps at least
    /// half the shorter edge, the one nearest in plan (a slanted edge can overlap fully and lie far off: the first vanilla
    /// floor run, 2026-11-16, took the largest overlap and left about a tenth of the base game's links unmatched); else the
    /// largest overlap. -1 when none overlaps.</summary>
    public static (int Edge, EdgeGap Gap) BestEdge(Vec3 a, Vec3 b, NavmeshGeometry far, int tri)
    {
        var t = far.Triangles[tri];
        int best = -1; var bg = EdgeGap.None;
        for (int e = 0; e < 3; e++)
        {
            var (i, j) = EdgeVerts(t, e);
            if (i < 0 || j < 0 || i >= far.Vertices.Count || j >= far.Vertices.Count) continue;
            var g = Gap(a, b, far.Vertices[i], far.Vertices[j]);
            if (g.Overlap <= 0) continue;
            bool half = g.Overlap >= 0.5f, bestHalf = bg.Overlap >= 0.5f;
            if (best < 0 || (half && !bestHalf) || (half == bestHalf && (half ? g.Plan < bg.Plan : g.Overlap > bg.Overlap))) { best = e; bg = g; }
        }
        return (best, bg);
    }
}

/// <summary>Every open edge of the winning navmeshes, bucketed by space and a 256 u plan cell, for the candidate search.</summary>
public sealed class OpenEdgeIndex
{
    const float Bucket = 256f;
    public readonly record struct OpenEdge(FormKey Nav, int Tri, int Edge, Vec3 A, Vec3 B);
    readonly Dictionary<(string Space, int X, int Y), List<OpenEdge>> _b = new();
    public long Count { get; private set; }

    public OpenEdgeIndex(IReadOnlyDictionary<FormKey, WinningNav> nav)
    {
        foreach (var (k, w) in nav)
        {
            var g = w.Geom;
            for (int t = 0; t < g.Triangles.Count; t++)
                for (int e = 0; e < 3; e++)
                {
                    var tr = g.Triangles[t];
                    // open: the edge links across (flag set), or it has no neighbour in its own navmesh
                    if (!NavBorderMatch.EdgeIsLink(tr, e) && NavBorderMatch.EdgeLink(tr, e) >= 0) continue;
                    var (i, j) = NavBorderMatch.EdgeVerts(tr, e);
                    if (i < 0 || j < 0 || i >= g.Vertices.Count || j >= g.Vertices.Count) continue;
                    var oe = new OpenEdge(k, t, e, g.Vertices[i], g.Vertices[j]);
                    foreach (var key in Keys(w.Space, oe.A, oe.B))
                    {
                        if (!_b.TryGetValue(key, out var l)) _b[key] = l = new List<OpenEdge>();
                        l.Add(oe);
                    }
                    Count++;
                }
        }
    }

    static IEnumerable<(string, int, int)> Keys(string space, Vec3 a, Vec3 b)
    {
        int x0 = (int)MathF.Floor(Math.Min(a.X, b.X) / Bucket), x1 = (int)MathF.Floor(Math.Max(a.X, b.X) / Bucket);
        int y0 = (int)MathF.Floor(Math.Min(a.Y, b.Y) / Bucket), y1 = (int)MathF.Floor(Math.Max(a.Y, b.Y) / Bucket);
        for (int x = x0; x <= x1; x++) for (int y = y0; y <= y1; y++) yield return (space, x, y);
    }

    /// <summary>The triangles of other navmeshes in <paramref name="space"/> with an open edge lying along a-b within
    /// <paramref name="tol"/>, best edge per triangle, nearest first. <paramref name="self"/> is left out.</summary>
    public List<(FormKey Nav, int Tri, int Edge, EdgeGap Gap)> Candidates(string space, Vec3 a, Vec3 b, EdgeTolerance tol, FormKey self)
    {
        var seen = new Dictionary<(FormKey, int), (int Edge, EdgeGap Gap)>();
        var pad = new Vec3(tol.Plan, tol.Plan, 0);
        foreach (var key in Keys(space, new Vec3(Math.Min(a.X, b.X) - pad.X, Math.Min(a.Y, b.Y) - pad.Y, 0), new Vec3(Math.Max(a.X, b.X) + pad.X, Math.Max(a.Y, b.Y) + pad.Y, 0)))
        {
            if (!_b.TryGetValue(key, out var l)) continue;
            foreach (var oe in l)
            {
                if (oe.Nav == self) continue;
                var g = NavBorderMatch.Gap(a, b, oe.A, oe.B);
                if (!g.Within(tol)) continue;
                if (!seen.TryGetValue((oe.Nav, oe.Tri), out var prev) || g.Plan < prev.Gap.Plan) seen[(oe.Nav, oe.Tri)] = (oe.Edge, g);
            }
        }
        return seen.Select(kv => (kv.Key.Item1, kv.Key.Item2, kv.Value.Edge, kv.Value.Gap)).OrderBy(c => c.Gap.Plan).ThenBy(c => c.Item1.ToString()).ToList();
    }
}
