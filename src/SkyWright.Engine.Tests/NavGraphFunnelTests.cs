using System.Numerics;
using HousecarlCore;
using SkyWright.Engine;
using Mutagen.Bethesda.Plugins;
using Xunit;

namespace SkyWright.Engine.Tests;

/// <summary>
/// The taut path NavGraph gives between two triangles (A*, then the funnel): known answers C2302-C2305 on the lower grid of
/// viewprobe's CfLedgeGap fixture (LedgeLinkFixture.GapMeshes), built the same way: 4 x 4 squares of 64 u over x -256..0,
/// y -128..128, each split along its south-west to north-east diagonal, with the squares x -128..-64, y -64..64 deleted to
/// make a notch a path must bend round. A taut path bends only at the mesh boundary, so a corner at a vertex the mesh
/// surrounds on every side is slack. C2740-C2742: a hole whose side A* prefers by centroid costs is the longer taut walk
/// (HoleCourtyard), so the taut path tries the other side of each hole it bends round.
/// </summary>
[Trait("tier", "unit")]
public sealed class NavGraphFunnelTests
{
    static readonly ModKey Mod = new("HcNavFunnel", ModType.Master);
    static readonly FormKey Mesh = new(Mod, 0x961);
    const int N = 4;
    const float Step = 64f, X0 = -256f, Y0 = -128f;

    static bool Notch(float cx, float cy) => cx > -128 && cx < -64 && cy > -64 && cy < 64;

    // The same vertices, triangles, links and deleted flags as BlockFixture.Grid with LedgeLinkFixture.Notch.
    static Dictionary<NavGraph.TriRef, NavGraph.Node> NotchGrid()
    {
        var v = new List<Vec3>();
        for (int j = 0; j <= N; j++)
            for (int i = 0; i <= N; i++) v.Add(new Vec3(X0 + i * Step, Y0 + j * Step, 0f));
        short V(int i, int j) => (short)(j * (N + 1) + i);
        var tris = new List<(short A, short B, short C)>();
        for (int j = 0; j < N; j++)
            for (int i = 0; i < N; i++)
            {
                tris.Add((V(i, j), V(i + 1, j), V(i + 1, j + 1)));
                tris.Add((V(i, j), V(i + 1, j + 1), V(i, j + 1)));
            }
        var byEdge = new Dictionary<(short, short), List<int>>();
        static (short, short) E(short a, short b) => a < b ? (a, b) : (b, a);
        for (int t = 0; t < tris.Count; t++)
            foreach (var e in new[] { E(tris[t].A, tris[t].B), E(tris[t].B, tris[t].C), E(tris[t].C, tris[t].A) })
            { if (!byEdge.TryGetValue(e, out var l)) byEdge[e] = l = new List<int>(); l.Add(t); }
        short Other(int t, short a, short b) => byEdge[E(a, b)].Where(x => x != t).Select(x => (short)x).DefaultIfEmpty((short)-1).First();
        var nt = new List<NavTri>();
        for (int t = 0; t < tris.Count; t++)
        {
            var (a, b, c) = tris[t];
            float cx = (v[a].X + v[b].X + v[c].X) / 3f, cy = (v[a].Y + v[b].Y + v[c].Y) / 3f;
            nt.Add(new NavTri(a, b, c, Notch(cx, cy) ? 0x08 : 0, Other(t, a, b), Other(t, b, c), Other(t, c, a)));
        }
        var geo = new NavmeshGeometry(Mesh, v, nt, v[0], v[^1], Array.Empty<NavEdgeLink>(), Array.Empty<NavDoorTriangle>());
        return NavGraph.Build(new Dictionary<FormKey, NavmeshGeometry> { [Mesh] = geo }).Nodes;
    }

    // As viewprobe --route-solve starts a leg: the drawable triangle under each waypoint, then the taut path.
    static (List<NavGraph.TriRef> Channel, List<Vector2> Corners) Solve(Dictionary<NavGraph.TriRef, NavGraph.Node> nodes, Vector2 a, Vector2 b)
    {
        var s = NavGraph.TriangleAt(nodes, a.X, a.Y, 0f)!.Value.Tri;
        var g = NavGraph.TriangleAt(nodes, b.X, b.Y, 0f)!.Value.Tri;
        var t = NavGraph.TautPath(nodes, s, g, a, b)!;
        return (t.Channel, t.Corners);
    }

    static string Show(IEnumerable<Vector2> cs) => string.Join("  ", cs.Select(c => $"{c.X:0.###},{c.Y:0.###}"));
    static float Length(IReadOnlyList<Vector2> cs) { float l = 0; for (int i = 0; i + 1 < cs.Count; i++) l += Vector2.Distance(cs[i], cs[i + 1]); return l; }

    // A lattice vertex the mesh surrounds on every side: not on the grid's outer edge and not a corner of the notch.
    static bool InteriorVertex(Vector2 c)
    {
        float i = (c.X - X0) / Step, j = (c.Y - Y0) / Step;
        if (MathF.Abs(i - MathF.Round(i)) > 1e-3f || MathF.Abs(j - MathF.Round(j)) > 1e-3f) return false;
        int ii = (int)MathF.Round(i), jj = (int)MathF.Round(j);
        if (ii <= 0 || ii >= N || jj <= 0 || jj >= N) return false;
        bool notchCorner = (ii == 2 || ii == 3) && jj >= 1 && jj <= 3;
        return !notchCorner;
    }

    static bool NotchCorner(Vector2 c) => (MathF.Abs(c.X + 128) < 1e-3f || MathF.Abs(c.X + 64) < 1e-3f)
                                           && (MathF.Abs(c.Y + 64) < 1e-3f || MathF.Abs(c.Y) < 1e-3f || MathF.Abs(c.Y - 64) < 1e-3f);

    static readonly Vector2[] Battery =
    {
        new(-224, 0), new(-32, 0), new(-224, 32), new(-32, -32), new(-160, -96), new(-96, 96), new(-224, -96), new(-32, 96),
    };

    // C2302: the funnel never emits the same corner twice in a row (on viewprobe today: -224,32 -> -32,-32 gave -64,64 twice).
    [Fact]
    public void C2302_the_funnel_never_emits_a_corner_twice_in_a_row()
    {
        var nodes = NotchGrid();
        var bad = new List<string>();
        foreach (var a in Battery)
            foreach (var b in Battery)
            {
                if (a == b) continue;
                var (_, cs) = Solve(nodes, a, b);
                for (int i = 0; i + 1 < cs.Count; i++)
                    if (Vector2.Distance(cs[i], cs[i + 1]) < 1e-3f) { bad.Add($"{a} -> {b}: {Show(cs)}"); break; }
            }
        Assert.True(bad.Count == 0, "consecutive equal corners: " + string.Join(" | ", bad));
        var (_, round) = Solve(nodes, new(-224, 32), new(-32, -32));
        Assert.Equal(new Vector2[] { new(-224, 32), new(-128, 64), new(-64, 64), new(-32, -32) }, round);
    }

    // C2303: round the notch the path bends at the notch's own corners, and its length is the taut 250.94 u
    // (on viewprobe today it bends at -192,-64, an interior vertex, and walks 272 u).
    [Fact]
    public void C2303_the_path_round_the_notch_bends_at_the_notch_corners()
    {
        var nodes = NotchGrid();
        var (channel, cs) = Solve(nodes, new(-224, 0), new(-32, 0));
        var south = new Vector2[] { new(-224, 0), new(-128, -64), new(-64, -64), new(-32, 0) };
        var north = new Vector2[] { new(-224, 0), new(-128, 64), new(-64, 64), new(-32, 0) };
        float taut = Vector2.Distance(south[0], south[1]) + 64f + Vector2.Distance(south[2], south[3]);
        Assert.True(cs.SequenceEqual(south) || cs.SequenceEqual(north),
                    $"corners {Show(cs)} (length {Length(cs):0.###}, taut {taut:0.###}); channel {string.Join(" ", channel.Select(t => t.Index))}");
        Assert.InRange(Length(cs), taut - 1e-2f, taut + 1e-2f);
    }

    // C2304: over every pair of a battery of points round the notch, every bend of the taut path is at a corner of the
    // notch: the grid's outer edge is convex, so a shortest path round the one hole touches nothing else. A bend at a vertex
    // the mesh surrounds means the channel went round the wrong side of it; a bend on the outer edge, a waypoint on a
    // portal handled as a constraint.
    [Fact]
    public void C2304_no_corner_sits_on_a_vertex_the_mesh_surrounds()
    {
        var nodes = NotchGrid();
        var bad = new List<string>();
        foreach (var a in Battery)
            foreach (var b in Battery)
            {
                if (a == b) continue;
                var (_, cs) = Solve(nodes, a, b);
                for (int i = 1; i + 1 < cs.Count; i++)
                {
                    var d0 = cs[i] - cs[i - 1]; var d1 = cs[i + 1] - cs[i];
                    bool collinear = MathF.Abs(d0.X * d1.Y - d0.Y * d1.X) < 1e-2f * d0.Length() * d1.Length();
                    if (!NotchCorner(cs[i]) && !collinear) { bad.Add($"{a} -> {b}: {Show(cs)}"); break; }
                }
            }
        Assert.True(bad.Count == 0, $"{bad.Count} path(s) bend off the notch: " + string.Join(" | ", bad));
    }

    // C2305 (preservation): the taut path is never longer than the funnel over A*'s own channel, and where that funnel
    // already bends only at the boundary the channel and corners are A*'s, unchanged.
    [Fact]
    public void C2305_the_taut_path_is_never_longer_than_A_stars_channel_and_keeps_a_taut_one()
    {
        var nodes = NotchGrid();
        var bad = new List<string>();
        foreach (var a in Battery)
            foreach (var b in Battery)
            {
                if (a == b) continue;
                var s = NavGraph.TriangleAt(nodes, a.X, a.Y, 0f)!.Value.Tri;
                var g = NavGraph.TriangleAt(nodes, b.X, b.Y, 0f)!.Value.Tri;
                var astar = NavGraph.FindChannel(nodes, s, g)!;
                var plain = NavGraph.Funnel(nodes, astar, a, b);
                var (channel, cs) = Solve(nodes, a, b);
                if (Length(cs) > Length(plain) + 1e-3f) bad.Add($"{a} -> {b}: longer, {Length(cs):0.###} vs {Length(plain):0.###}");
                bool plainTaut = true;
                for (int i = 1; i + 1 < plain.Count; i++) if (InteriorVertex(plain[i])) plainTaut = false;
                if (plainTaut && !channel.SequenceEqual(astar)) bad.Add($"{a} -> {b}: a taut channel was changed");
            }
        Assert.True(bad.Count == 0, string.Join(" | ", bad));
    }

    // ---- C2740-C2742: the side of a hole (task 2026-11-15-astar-side-of-hole) ----
    // A hole x 40..60, y -60..10 in a mesh x 0..100, y -70..300, triangulated as a level courtyard would be: the narrow
    // corridor south of the hole in small triangles, the open ground north of it in four large ones whose centroids lie
    // far from the hole. A* over centroid distances costs the south side 153.3 u and the north side 462.4 u, so it picks the
    // south; but the taut walk from (10,0) to (90,0) is 154.2 u round the south side (corners (40,-60), (60,-60)) and
    // 83.2 u round the north side (corners (40,10), (60,10)). Every corner of either walk is on the hole's edge, so the
    // interior-vertex re-route has nothing to change.
    static readonly FormKey HoleMesh = new(Mod, 0x962);

    static Dictionary<NavGraph.TriRef, NavGraph.Node> HoleCourtyard()
    {
        // L, o1 .. o4, R on the outside; h1 .. h4 the hole's corners
        var v = new List<Vec3>
        {
            new(0, 10, 0), new(0, -70, 0), new(100, -70, 0), new(100, 300, 0), new(0, 300, 0), new(100, 10, 0),   // 0 L, 1 o1, 2 o2, 3 o3, 4 o4, 5 R
            new(40, -60, 0), new(60, -60, 0), new(60, 10, 0), new(40, 10, 0),                                  // 6 h1, 7 h2, 8 h3, 9 h4
        };
        var tris = new (short A, short B, short C)[]
        {
            (0, 6, 9),   // 0 start: L h1 h4
            (0, 1, 6),   // 1 west:  L o1 h1
            (1, 7, 6),   // 2 south: o1 h2 h1
            (1, 2, 7),   // 3 south: o1 o2 h2
            (5, 7, 2),   // 4 east:  R h2 o2
            (5, 8, 7),   // 5 goal:  R h3 h2
            (0, 9, 4),   // 6 north: L h4 o4
            (4, 9, 3),   // 7 north: o4 h4 o3
            (3, 9, 8),   // 8 north: o3 h4 h3
            (3, 8, 5),   // 9 north: o3 h3 R
        };
        var byEdge = new Dictionary<(short, short), List<int>>();
        static (short, short) E(short a, short b) => a < b ? (a, b) : (b, a);
        for (int t = 0; t < tris.Length; t++)
            foreach (var e in new[] { E(tris[t].A, tris[t].B), E(tris[t].B, tris[t].C), E(tris[t].C, tris[t].A) })
            { if (!byEdge.TryGetValue(e, out var l)) byEdge[e] = l = new List<int>(); l.Add(t); }
        short Other(int t, short a, short b) => byEdge[E(a, b)].Where(x => x != t).Select(x => (short)x).DefaultIfEmpty((short)-1).First();
        var nt = tris.Select((t, i) => new NavTri(t.A, t.B, t.C, 0, Other(i, t.A, t.B), Other(i, t.B, t.C), Other(i, t.C, t.A))).ToList();
        var geo = new NavmeshGeometry(HoleMesh, v, nt, new Vec3(0, -70, 0), new Vec3(100, 300, 0), Array.Empty<NavEdgeLink>(), Array.Empty<NavDoorTriangle>());
        return NavGraph.Build(new Dictionary<FormKey, NavmeshGeometry> { [HoleMesh] = geo }).Nodes;
    }

    static readonly Vector2 HoleStart = new(10, 0), HoleEnd = new(90, 0);
    static readonly float NorthWalk = 2 * MathF.Sqrt(30 * 30 + 10 * 10) + 20, SouthWalk = 2 * MathF.Sqrt(30 * 30 + 60 * 60) + 20;

    // C2740: the fixture is what it claims: A* alone picks the south side, and its taut walk is SouthWalk (154.2 u).
    [Fact]
    public void C2740_centroid_A_star_picks_the_hole_side_whose_taut_walk_is_longer()
    {
        var nodes = HoleCourtyard();
        var s = NavGraph.TriangleAt(nodes, HoleStart.X, HoleStart.Y, 0f)!.Value.Tri;
        var g = NavGraph.TriangleAt(nodes, HoleEnd.X, HoleEnd.Y, 0f)!.Value.Tri;
        Assert.Equal(new NavGraph.TriRef(HoleMesh, 0), s);
        Assert.Equal(new NavGraph.TriRef(HoleMesh, 5), g);
        var astar = NavGraph.FindChannel(nodes, s, g)!;
        Assert.Equal(new[] { 0, 1, 2, 3, 4, 5 }, astar.Select(t => t.Index));
        Assert.Equal(SouthWalk, Length(NavGraph.Funnel(nodes, astar, HoleStart, HoleEnd)), 2);
    }

    /// <summary>The check C2741 makes, as a sentence (null when it holds), so its sabotage arm fails on the same words.</summary>
    static string? NorthSideCheck(NavGraph.Taut t)
    {
        float l = Length(t.Corners);
        if (MathF.Abs(l - NorthWalk) < 0.01f && t.Corners.Count == 4 && t.Corners[1] == new Vector2(40, 10) && t.Corners[2] == new Vector2(60, 10))
            return null;
        return $"the taut path round the hole walks {l:0.0} u ({Show(t.Corners)}); the north side's taut walk is {NorthWalk:0.0} u via 40,10 and 60,10";
    }

    // C2741: the taut path takes the north side: 83.2 u via (40,10) and (60,10), and says one hole was passed on its other side.
    [Fact]
    public void C2741_the_taut_path_goes_round_the_side_of_the_hole_whose_walk_is_shorter()
    {
        var nodes = HoleCourtyard();
        var t = NavGraph.TautPath(nodes, new(HoleMesh, 0), new(HoleMesh, 5), HoleStart, HoleEnd)!;
        var why = NorthSideCheck(t);
        Assert.True(why is null, why);
        Assert.Equal(1, t.OtherSides);
        Assert.Equal(SouthWalk - NorthWalk, t.Saved, 2);
    }

    // C2742 (sabotage): TautPath with the other-side trial switched off gives the south side, and C2741's check says so on
    // its own sentence.
    [Fact]
    public void C2742_sabotage_without_the_other_side_trial_the_path_walks_the_longer_side()
    {
        var nodes = HoleCourtyard();
        var t = NavGraph.TautPath(nodes, new(HoleMesh, 0), new(HoleMesh, 5), HoleStart, HoleEnd, tryOtherSides: false)!;
        var why = NorthSideCheck(t);
        Assert.True(why is not null && why.StartsWith("the taut path round the hole walks 154.2 u", StringComparison.Ordinal), why ?? "the check passed");
        Assert.Equal(0, t.OtherSides);
    }
}
