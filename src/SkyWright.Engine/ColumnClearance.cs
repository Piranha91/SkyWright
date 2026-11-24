using HousecarlCore;

namespace SkyWright.Engine;

// COLUMN CLEARANCE: how far, horizontally, the nearest surface of a soup stands from a standing body's column. The column is
// the vertical slab Z0..Z1 over a ground point (x, y). Every triangle is clipped to that slab and projected onto the ground
// plane; the clearance is the smallest horizontal distance from (x, y) to any clipped piece, 0 when the point lies inside
// one. A surface wholly below Z0 (the floor the body stands on, a kerb it steps over) or wholly above Z1 (an arch
// overhead) takes no part, and a wall that only enters the slab over part of its height counts by that part alone.
// The caller chooses which triangles count (an actor-solid collision soup) and the slab (a body band above a read floor).

/// <summary>A column clearance: the horizontal distance, and the triangle index that set it (-1 when none was nearer than
/// the cap; the distance is then the cap).</summary>
public readonly record struct ColumnReading(double Distance, int Triangle);

public static class ColumnClearance
{
    const double Eps = 1e-9;

    /// <summary>The horizontal clearance of the column over (x, y) between heights z0 and z1 (z1 &gt; z0) against a soup
    /// already in world space. Triangles farther than <paramref name="cap"/> are not resolved.</summary>
    public static ColumnReading Measure(TriangleSoup world, double x, double y, double z0, double z1, double cap = double.PositiveInfinity)
    {
        if (!(z1 > z0)) throw new ArgumentException("the column needs z1 > z0");
        var v = world.Vertices; var ix = world.Indices;
        double best = cap; int who = -1;
        var poly = new (double X, double Y, double Z)[8];
        var tmp = new (double X, double Y, double Z)[8];
        for (int t = 0; t < world.TriangleCount; t++)
        {
            var a = v[ix[3 * t]]; var b = v[ix[3 * t + 1]]; var c = v[ix[3 * t + 2]];
            double lo = Math.Min(a.Z, Math.Min(b.Z, c.Z)), hi = Math.Max(a.Z, Math.Max(b.Z, c.Z));
            if (hi < z0 || lo > z1) continue;
            double bx0 = Math.Min(a.X, Math.Min(b.X, c.X)), bx1 = Math.Max(a.X, Math.Max(b.X, c.X));
            double by0 = Math.Min(a.Y, Math.Min(b.Y, c.Y)), by1 = Math.Max(a.Y, Math.Max(b.Y, c.Y));
            double bdx = Math.Max(0, Math.Max(bx0 - x, x - bx1)), bdy = Math.Max(0, Math.Max(by0 - y, y - by1));
            if (Math.Sqrt(bdx * bdx + bdy * bdy) >= best) continue;
            poly[0] = (a.X, a.Y, a.Z); poly[1] = (b.X, b.Y, b.Z); poly[2] = (c.X, c.Y, c.Z);
            int n = Clip(poly, 3, tmp, z0, true);
            n = Clip(tmp, n, poly, z1, false);
            if (n == 0) continue;
            double d = Distance2D(poly, n, x, y);
            if (d < best) { best = d; who = t; if (d <= 0) break; }
        }
        return new ColumnReading(best, who);
    }

    // Sutherland-Hodgman against one horizontal plane: keep z >= h (keepAbove) or z <= h.
    static int Clip((double X, double Y, double Z)[] src, int n, (double X, double Y, double Z)[] dst, double h, bool keepAbove)
    {
        int m = 0;
        for (int i = 0; i < n; i++)
        {
            var p = src[i]; var q = src[(i + 1) % n];
            bool pin = keepAbove ? p.Z >= h : p.Z <= h, qin = keepAbove ? q.Z >= h : q.Z <= h;
            if (pin) dst[m++] = p;
            if (pin != qin)
            {
                double s = (h - p.Z) / (q.Z - p.Z);
                dst[m++] = (p.X + s * (q.X - p.X), p.Y + s * (q.Y - p.Y), h);
            }
        }
        return m;
    }

    // Horizontal distance from (x, y) to a convex polygon's projection: 0 inside, else to its nearest edge or vertex.
    static double Distance2D((double X, double Y, double Z)[] p, int n, double x, double y)
    {
        double area = 0;
        for (int i = 0; i < n; i++) { var a = p[i]; var b = p[(i + 1) % n]; area += a.X * b.Y - b.X * a.Y; }
        if (Math.Abs(area) > Eps)
        {
            bool inside = true;
            for (int i = 0; i < n && inside; i++)
            {
                var a = p[i]; var b = p[(i + 1) % n];
                double cross = (b.X - a.X) * (y - a.Y) - (b.Y - a.Y) * (x - a.X);
                if (area > 0 ? cross < -Eps : cross > Eps) inside = false;
            }
            if (inside) return 0;
        }
        double best = double.PositiveInfinity;
        for (int i = 0; i < n; i++)
        {
            var a = p[i]; var b = p[(i + 1) % n];
            double ex = b.X - a.X, ey = b.Y - a.Y, l2 = ex * ex + ey * ey;
            double s = l2 > Eps ? Math.Clamp(((x - a.X) * ex + (y - a.Y) * ey) / l2, 0, 1) : 0;
            double dx = a.X + s * ex - x, dy = a.Y + s * ey - y;
            best = Math.Min(best, Math.Sqrt(dx * dx + dy * dy));
        }
        return best;
    }
}
