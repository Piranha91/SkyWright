using System.Numerics;
using Xunit;
using Xunit.Abstractions;

namespace SkyWright.Engine.Tests;

/// <summary>The pruned navmesh distance (<see cref="NavmeshReach"/>) against the brute force it replaces, on seeded
/// random soups and navmeshes: the distance must be equal, and the triangle each names must lie at that same distance
/// under the brute force (on a tie the two may name different triangles). Deleted and degenerate triangles, an
/// intersecting pair, the band, an all-deleted and an empty navmesh are all in it.</summary>
[Trait("tier", "unit")]
public sealed class NavmeshReachPruningTests
{
    readonly ITestOutputHelper _out;
    public NavmeshReachPruningTests(ITestOutputHelper output) => _out = output;

    const ushort Deleted = 0x08;   // Mutagen NavmeshTriangle.Flag.Deleted, the flag NavmeshReach.IsDeleted reads

    static Vector3 R(Random r, float spread) => new((float)(r.NextDouble() * 2 - 1) * spread, (float)(r.NextDouble() * 2 - 1) * spread,
                                                    (float)(r.NextDouble() * 2 - 1) * spread);

    /// <summary>A navmesh of <paramref name="n"/> triangles over a floor, with deleted (every 7th) and degenerate
    /// (every 11th: two coincident vertices; every 13th: collinear) triangles among them.</summary>
    static List<NavTriangle> Nav(Random r, int n, float extent)
    {
        var list = new List<NavTriangle>();
        for (int i = 0; i < n; i++)
        {
            var c = new Vector3((float)(r.NextDouble() * 2 - 1) * extent, (float)(r.NextDouble() * 2 - 1) * extent, (float)(r.NextDouble() * 40 - 20));
            var a = c + R(r, 60); var b = c + R(r, 60); var d = c + R(r, 60);
            if (i % 11 == 5) d = b;
            if (i % 13 == 6) d = a + (b - a) * 0.5f;
            list.Add(new NavTriangle(a, b, d, (ushort)(i % 7 == 3 ? Deleted : 0), "000800:Test.esp", i));
        }
        return list;
    }

    /// <summary>An object of <paramref name="n"/> triangles around a centre, one of them degenerate.</summary>
    static TriangleSoup Soup(Random r, int n, Vector3 centre, float size)
    {
        var v = new List<Vector3>(); var ix = new List<int>();
        for (int i = 0; i < n; i++)
        {
            var c = centre + R(r, size);
            v.Add(c + R(r, size / 3)); v.Add(c + R(r, size / 3)); v.Add(i == 1 ? v[^1] : c + R(r, size / 3));
            ix.AddRange(new[] { v.Count - 3, v.Count - 2, v.Count - 1 });
        }
        return new TriangleSoup(v.ToArray(), ix.ToArray());
    }

    // The named triangle lies at the reported distance under the brute force: re-measure that one triangle alone.
    static void SameAnswer(string what, NavDistance pruned, NavDistance brute, IReadOnlyList<NavTriangle> nav, Func<IReadOnlyList<NavTriangle>, NavDistance> bruteOn)
    {
        Assert.True(brute.Status == pruned.Status, $"{what}: status {pruned.Status} pruned, {brute.Status} brute");
        Assert.True(brute.Reason == pruned.Reason, $"{what}: reason '{pruned.Reason}' pruned, '{brute.Reason}' brute");
        Assert.True(brute.Distance == pruned.Distance, $"{what}: distance {pruned.Distance:R} pruned, {brute.Distance:R} brute");
        if (pruned.Status != "measured") return;
        var named = nav.Where(t => t.Index == pruned.Triangle && t.Navmesh == pruned.Navmesh).ToList();
        Assert.True(named.Count == 1, $"{what}: the pruned answer names triangle {pruned.Triangle}, which is not one triangle of the navmesh");
        var alone = bruteOn(named);
        Assert.True(alone.Distance == pruned.Distance, $"{what}: the named triangle {pruned.Triangle} lies {alone.Distance:R} away, not {pruned.Distance:R}");
    }

    [Fact]
    public void Pruned_distance_equals_brute_force_on_seeded_random_scenes()
    {
        int cases = 0, zero = 0;
        for (int seed = 1; seed <= 24; seed++)
        {
            var r = new Random(seed);
            var nav = Nav(r, 60 + seed * 3, 900);
            var centre = new Vector3((float)(r.NextDouble() * 1600 - 800), (float)(r.NextDouble() * 1600 - 800), (float)(r.NextDouble() * 600 - 100));
            var soup = Soup(r, 5 + seed % 9, centre, 40 + seed * 5);
            var point = centre + R(r, 200);
            string s = $"seed {seed}";

            var a = NavmeshReach.FromSoup(soup, nav);
            SameAnswer($"{s} soup", a, NavmeshReach.FromSoupBrute(soup, nav), nav, t => NavmeshReach.FromSoupBrute(soup, t));
            var b = NavmeshReach.FromPoint(point, nav);
            SameAnswer($"{s} point", b, NavmeshReach.FromPointBrute(point, nav), nav, t => NavmeshReach.FromPointBrute(point, t));
            var c = NavmeshReach.FromSoupBand(soup, nav, 0, 128);
            SameAnswer($"{s} soup band", c, NavmeshReach.FromSoupBandBrute(soup, nav, 0, 128), nav, t => NavmeshReach.FromSoupBandBrute(soup, t, 0, 128));
            var d = NavmeshReach.FromPointBand(point, nav, 0, 128);
            SameAnswer($"{s} point band", d, NavmeshReach.FromPointBandBrute(point, nav, 0, 128), nav, t => NavmeshReach.FromPointBandBrute(point, t, 0, 128));
            cases += 4;
            zero += new[] { a, b, c, d }.Count(x => x.Distance == 0);
        }
        _out.WriteLine($"random scenes: {cases} comparisons equal, {zero} of them at distance 0");
        Assert.True(zero > 0, "no random comparison reached distance 0, so the band's and the crossing's zero is not exercised");
    }

    [Fact]
    public void An_intersecting_pair_is_zero_in_both()
    {
        var nav = new List<NavTriangle>
        {
            new(new(-100, -100, 0), new(100, -100, 0), new(0, 100, 0), 0, "000800:Test.esp", 0),
            new(new(500, 500, 0), new(600, 500, 0), new(550, 600, 0), 0, "000800:Test.esp", 1),
        };
        var soup = new TriangleSoup(new Vector3[] { new(0, 0, -20), new(10, 0, 20), new(0, 10, 20) }, new[] { 0, 1, 2 });
        var p = NavmeshReach.FromSoup(soup, nav);
        SameAnswer("crossing", p, NavmeshReach.FromSoupBrute(soup, nav), nav, t => NavmeshReach.FromSoupBrute(soup, t));
        Assert.True(p.Distance == 0, $"crossing: distance {p.Distance} pruned, expected 0");
        _out.WriteLine($"crossing: {p.Distance} pruned and brute, triangle {p.Triangle}");
    }

    [Fact]
    public void An_all_deleted_or_empty_navmesh_refuses_with_the_same_sentence()
    {
        var r = new Random(7);
        var dead = Nav(r, 30, 300).Select(t => t with { Flags = Deleted }).ToList();
        var soup = Soup(r, 4, Vector3.Zero, 50);
        foreach (var (name, nav) in new[] { ("all deleted", dead), ("empty", new List<NavTriangle>()) })
        {
            SameAnswer($"{name} soup", NavmeshReach.FromSoup(soup, nav), NavmeshReach.FromSoupBrute(soup, nav), nav, t => NavmeshReach.FromSoupBrute(soup, t));
            SameAnswer($"{name} point", NavmeshReach.FromPoint(Vector3.Zero, nav), NavmeshReach.FromPointBrute(Vector3.Zero, nav), nav, t => NavmeshReach.FromPointBrute(Vector3.Zero, t));
            SameAnswer($"{name} soup band", NavmeshReach.FromSoupBand(soup, nav, 0, 128), NavmeshReach.FromSoupBandBrute(soup, nav, 0, 128), nav, t => NavmeshReach.FromSoupBandBrute(soup, t, 0, 128));
            SameAnswer($"{name} point band", NavmeshReach.FromPointBand(Vector3.Zero, nav, 0, 128), NavmeshReach.FromPointBandBrute(Vector3.Zero, nav, 0, 128), nav, t => NavmeshReach.FromPointBandBrute(Vector3.Zero, t, 0, 128));
            _out.WriteLine($"{name}: '{NavmeshReach.FromSoup(soup, nav).Reason}' pruned and brute");
        }
    }
}
