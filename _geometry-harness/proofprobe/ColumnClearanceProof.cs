using System.Numerics;
using HousecarlCore;

// Column clearance (2026-10-15): the shared reader of how far the nearest surface stands from a standing body's column
// (HousecarlCore.ColumnClearance). Known answers on hand-built soups, cases cc 4300-4310.
static class ColumnClearanceProof
{
    static void Case(Action<string, bool, string> check, string name, Func<(bool Ok, string Detail)> body)
    {
        (bool ok, string detail) r;
        try { r = body(); }
        catch (Exception ex) { r = (false, ex.GetType().Name + ": " + ex.Message); }
        check(name, r.ok, r.detail);
    }

    static TriangleSoup Quad(Vector3 a, Vector3 b, Vector3 c, Vector3 d) => new(new[] { a, b, c, d }, new[] { 0, 1, 2, 0, 2, 3 });
    // a vertical wall in the plane x = wx, spanning y0..y1 and z0..z1
    static TriangleSoup WallX(float wx, float y0, float y1, float z0, float z1) =>
        Quad(new(wx, y0, z0), new(wx, y1, z0), new(wx, y1, z1), new(wx, y0, z1));
    static TriangleSoup Join(params TriangleSoup[] s)
    {
        var v = new List<Vector3>(); var ix = new List<int>();
        foreach (var q in s) { int o = v.Count; v.AddRange(q.Vertices); ix.AddRange(q.Indices.Select(i => i + o)); }
        return new TriangleSoup(v.ToArray(), ix.ToArray());
    }
    static bool Near(double a, double b, double tol = 1e-6) => Math.Abs(a - b) <= tol;

    public static void Run(Action<string, bool, string> check)
    {
        // 4300: a wall 100 u away reads 100, and names a triangle. Rejects a reader that misses a plain wall or reports it
        // at its centroid (the triangles' centroids lie off the nearest line).
        Case(check, "cc 4300: a vertical wall 100 u to the side reads 100", () =>
        {
            var r = ColumnClearance.Measure(WallX(100, -500, 500, 0, 300), 0, 0, 58, 120);
            return (Near(r.Distance, 100) && r.Triangle >= 0, $"distance {r.Distance} triangle {r.Triangle}");
        });
        // 4301: the floor the body stands on (below Z0) takes no part. Rejects a reader that counts every triangle.
        Case(check, "cc 4301: a floor under the point, below the column, is not an obstacle", () =>
        {
            var floor = Quad(new(-500, -500, 0), new(500, -500, 0), new(500, 500, 0), new(-500, 500, 0));
            var r = ColumnClearance.Measure(floor, 0, 0, 58, 120, 1000);
            return (Near(r.Distance, 1000) && r.Triangle == -1, $"distance {r.Distance} triangle {r.Triangle}");
        });
        // 4302: an arch wholly above Z1 takes no part.
        Case(check, "cc 4302: a slab overhead, above the column, is not an obstacle", () =>
        {
            var roof = Quad(new(-500, -500, 300), new(500, -500, 300), new(500, 500, 300), new(-500, 500, 300));
            var r = ColumnClearance.Measure(roof, 0, 0, 58, 120, 1000);
            return (Near(r.Distance, 1000) && r.Triangle == -1, $"distance {r.Distance} triangle {r.Triangle}");
        });
        // 4303: a horizontal surface INSIDE the slab over the point reads 0 (a tabletop at body height).
        Case(check, "cc 4303: a surface inside the column over the point reads 0", () =>
        {
            var top = Quad(new(-50, -50, 90), new(50, -50, 90), new(50, 50, 90), new(-50, 50, 90));
            var r = ColumnClearance.Measure(top, 0, 0, 58, 120);
            return (Near(r.Distance, 0), $"distance {r.Distance}");
        });
        // 4304: a sloped ramp counts only by its part inside the slab. The ramp rises along +x from z 0 at x 0 to z 300 at
        // x 300; inside 58..120 it spans x 58..120, so the clearance from the origin is 58 (not 0, its foot).
        Case(check, "cc 4304: a ramp counts by its part inside the column's heights", () =>
        {
            var ramp = Quad(new(0, -100, 0), new(300, -100, 300), new(300, 100, 300), new(0, 100, 0));
            var r = ColumnClearance.Measure(ramp, 0, 0, 58, 120);
            return (Near(r.Distance, 58, 1e-4), $"distance {r.Distance}");
        });
        // 4305: the nearest of several, with its triangle index.
        Case(check, "cc 4305: the nearest of two walls decides, and is named", () =>
        {
            var s = Join(WallX(300, -50, 50, 0, 300), WallX(-150, -50, 50, 0, 300));
            var r = ColumnClearance.Measure(s, 0, 0, 58, 120);
            return (Near(r.Distance, 150) && r.Triangle >= 2, $"distance {r.Distance} triangle {r.Triangle}");
        });
        // 4306: diagonal distance to a wall's end (vertex / edge, not the infinite plane).
        Case(check, "cc 4306: a wall's end reads the distance to its edge, not to its plane", () =>
        {
            var r = ColumnClearance.Measure(WallX(30, 40, 500, 0, 300), 0, 0, 58, 120);
            return (Near(r.Distance, 50, 1e-6), $"distance {r.Distance} (3-4-5: 30, 40 -> 50)");
        });
        // 4307: a wall that ends below Z0 is not an obstacle; one whose top just enters the slab is.
        Case(check, "cc 4307: a low kerb below the column is ignored, a wall entering it counts", () =>
        {
            var kerb = ColumnClearance.Measure(WallX(20, -50, 50, 0, 40), 0, 0, 58, 120, 1000);
            var wall = ColumnClearance.Measure(WallX(20, -50, 50, 0, 60), 0, 0, 58, 120, 1000);
            return (Near(kerb.Distance, 1000) && Near(wall.Distance, 20), $"kerb {kerb.Distance} wall {wall.Distance}");
        });
        // 4308: the cap bounds the search: a wall beyond it is reported at the cap, triangle -1.
        Case(check, "cc 4308: beyond the cap reads the cap", () =>
        {
            var r = ColumnClearance.Measure(WallX(400, -50, 50, 0, 300), 0, 0, 58, 120, 115);
            return (Near(r.Distance, 115) && r.Triangle == -1, $"distance {r.Distance} triangle {r.Triangle}");
        });
        // 4309: a closed box around the point (the body inside a building's walls) reads its nearest wall, and a point
        // inside a solid block at body height reads 0.
        Case(check, "cc 4309: inside a room reads the nearest wall; inside a solid block reads 0", () =>
        {
            var room = Join(WallX(80, -200, 200, 0, 300), WallX(-120, -200, 200, 0, 300));
            var block = Join(WallX(10, -10, 10, 0, 300), WallX(-10, -10, 10, 0, 300),
                             Quad(new(-10, -10, 100), new(10, -10, 100), new(10, 10, 100), new(-10, 10, 100)));
            var a = ColumnClearance.Measure(room, 0, 0, 58, 120);
            var b = ColumnClearance.Measure(block, 0, 0, 58, 120);
            return (Near(a.Distance, 80) && Near(b.Distance, 0), $"room {a.Distance} block {b.Distance}");
        });
        // 4310: the slab is refused when empty or inverted.
        Case(check, "cc 4310: z1 <= z0 is refused", () =>
        {
            try { ColumnClearance.Measure(WallX(1, 0, 1, 0, 1), 0, 0, 100, 100); return (false, "accepted"); }
            catch (ArgumentException e) { return (true, e.Message); }
        });
    }
}
