using System.Numerics;
using HousecarlCore;
using Xunit;

namespace WorkbenchTests;

/// <summary>houseCARL's <see cref="MeshVolume.SurfaceInside"/>, which the 3D view draws as the part of one object buried
/// in another: on shapes whose buried area is known exactly.</summary>
public class BuriedSurfaceTests
{
    /// <summary>A closed box, faces wound outward.</summary>
    static TriangleSoup Box(Vector3 lo, Vector3 hi, int skipFace = -1)
    {
        var v = new Vector3[8];
        for (int i = 0; i < 8; i++) v[i] = new((i & 1) != 0 ? hi.X : lo.X, (i & 2) != 0 ? hi.Y : lo.Y, (i & 4) != 0 ? hi.Z : lo.Z);
        int[][] faces = [[0, 2, 3, 0, 3, 1], [4, 5, 7, 4, 7, 6], [0, 1, 5, 0, 5, 4], [2, 6, 7, 2, 7, 3], [0, 4, 6, 0, 6, 2], [1, 3, 7, 1, 7, 5]];
        return new(v, faces.Where((_, f) => f != skipFace).SelectMany(f => f).ToArray());
    }

    static TriangleSoup Square(float lo, float hi, float z)
        => new([new(lo, lo, z), new(hi, lo, z), new(hi, hi, z), new(lo, hi, z)], [0, 1, 2, 0, 2, 3]);

    static double Area(TriangleSoup s)
    {
        double sum = 0;
        for (int t = 0; t < s.TriangleCount; t++)
        {
            Vector3 a = s.Vertices[s.Indices[3 * t]], b = s.Vertices[s.Indices[3 * t + 1]], c = s.Vertices[s.Indices[3 * t + 2]];
            sum += Vector3.Cross(b - a, c - a).Length() / 2;
        }
        return sum;
    }

    [Fact]
    public void A_sheet_through_a_box_is_buried_where_it_crosses_the_box()
    {
        // a 30 x 30 sheet through the middle of a 10-unit box: exactly 100 square units of it are inside
        var r = MeshVolume.SurfaceInside(Square(-10, 20, 5), Box(Vector3.Zero, new(10, 10, 10)), 0.25);
        Assert.Equal("measured", r.Status);
        Assert.Equal(2, r.Split);
        Assert.Equal(0, r.Undecided);
        Assert.InRange(Area(r.Inside!), 97.0, 103.0);
        // no piece lies outside the box
        Assert.All(r.Inside!.Vertices, p => Assert.True(p.X >= -0.26f && p.X <= 10.26f && p.Y >= -0.26f && p.Y <= 10.26f));
    }

    [Fact]
    public void A_surface_wholly_inside_is_kept_whole_and_one_outside_is_left_out()
    {
        var box = Box(Vector3.Zero, new(10, 10, 10));
        var inner = MeshVolume.SurfaceInside(Box(new(2, 2, 2), new(8, 8, 8)), box, 0.5);
        Assert.Equal("measured", inner.Status);
        Assert.Equal(12, inner.Whole);
        Assert.Equal(0, inner.Split);
        Assert.Equal(6 * 36.0, Area(inner.Inside!), 3);

        var outer = MeshVolume.SurfaceInside(Square(20, 30, 5), box, 0.5);
        Assert.Equal("measured", outer.Status);
        Assert.Equal(0, outer.Inside!.TriangleCount);
    }

    [Fact]
    public void An_open_solid_is_refused_and_says_so()
    {
        var r = MeshVolume.SurfaceInside(Square(-10, 20, 5), Box(Vector3.Zero, new(10, 10, 10), skipFace: 1), 0.5);
        Assert.Equal("refused", r.Status);
        Assert.StartsWith("solid: ", r.Reason);
        Assert.Null(r.Inside);
    }

    [Fact]
    public void A_bad_surface_index_is_refused()
    {
        var bad = new TriangleSoup([Vector3.Zero, Vector3.UnitX, Vector3.UnitY], [0, 1, 7]);
        var r = MeshVolume.SurfaceInside(bad, Box(Vector3.Zero, new(10, 10, 10)), 0.5);
        Assert.Equal("refused", r.Status);
        Assert.Equal("surface_index_out_of_range", r.Reason);
    }
}
