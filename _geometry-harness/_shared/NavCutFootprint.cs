using System.Numerics;
using HousecarlCore;

namespace HarnessShared;

// THE CUT OUTLINE OF A PLACED OBSTACLE (navedit's cut by reference; task 2026-11-16-navmesh-repair). Pure: from the
// obstacle's collision already placed in the world and the navmesh under it, the convex outline NavEdit.Cut takes.
//   floor   the navmesh's height under the obstacle's position, on the triangle nearest the obstacle in height (a navmesh
//           can have several levels); none under it, the lowest navmesh point within its plan box and 256 u of its height;
//   slice   its collision between step height above that floor (DetectorModel.StepHeight, 48 u: what an actor steps onto
//           is no obstacle) and a scale-1 humanoid controller's top (ActorBand.ControllerTop of ActorBand.ScaleOneHeight,
//           108.8 u above the navmesh: what it would walk into); a tree's canopy and a stone sunk below the step are out;
//   outline the convex hull of that slice in plan, grown by an optional margin (a square grown by the margin at each
//           point, then the hull again), so a concave obstacle is cut as its hull;
//   band    the navmesh cut runs from step height below the floor to the controller's top above it.
// The collision itself (the reference's base mesh, its bodies on the layers an actor meets, its transform) comes from
// the owners navedit calls: OverlapIndex.ModelOf, AssetResolver, NifGeometry, CollisionLayers.RoleSoups and
// SpatialOverlap.TransformSoup.

public sealed record CutFootprint(string Status, string? Why, IReadOnlyList<(float X, float Y)> Outline, float Floor, float ZLo, float ZHi, int SliceTriangles)
{
    public static CutFootprint Refused(string why) => new("refused", why, Array.Empty<(float, float)>(), float.NaN, float.NaN, float.NaN, 0);
}

public static class NavCutFootprint
{
    public static float SliceBottom => DetectorModel.StepHeight;
    public static float SliceTop => ActorBand.ControllerTop(ActorBand.ScaleOneHeight);

    /// <summary>The navmesh's height under (x, y): on the triangle under that point nearest <paramref name="z"/>; else the
    /// lowest navmesh vertex inside the plan box (lo, hi) within 256 u of z; null when there is neither.</summary>
    public static float? FloorUnder(NavmeshGeometry g, float x, float y, float z, Vector2 lo, Vector2 hi)
    {
        float? best = null;
        foreach (var t in g.Triangles)
        {
            Vec3 a = g.Vertices[t.A], b = g.Vertices[t.B], c = g.Vertices[t.C];
            double den = ((double)b.Y - c.Y) * (a.X - c.X) + ((double)c.X - b.X) * (a.Y - c.Y);
            if (den == 0) continue;
            double l1 = ((b.Y - (double)c.Y) * (x - c.X) + (c.X - (double)b.X) * (y - c.Y)) / den, l2 = ((c.Y - (double)a.Y) * (x - c.X) + (a.X - (double)c.X) * (y - c.Y)) / den;
            if (l1 < -1e-9 || l2 < -1e-9 || 1 - l1 - l2 < -1e-9) continue;
            float h = (float)(l1 * a.Z + l2 * b.Z + (1 - l1 - l2) * c.Z);
            if (best is null || Math.Abs(h - z) < Math.Abs(best.Value - z)) best = h;
        }
        if (best is not null) return best;
        foreach (var v in g.Vertices)
            if (v.X >= lo.X && v.X <= hi.X && v.Y >= lo.Y && v.Y <= hi.Y && Math.Abs(v.Z - z) <= 256 && (best is null || v.Z < best)) best = v.Z;
        return best;
    }

    /// <summary>The cut outline of an obstacle whose collision, in the world, is <paramref name="world"/>, standing on a
    /// navmesh floor at <paramref name="floor"/>; refused in one sentence when nothing of it stands in the actor's band.</summary>
    public static CutFootprint Of(TriangleSoup world, float floor, float margin = 0)
    {
        if (world.TriangleCount == 0) return CutFootprint.Refused("The obstacle has no collision triangles to cut by.");
        float lo = floor + SliceBottom, hi = floor + SliceTop;
        var pts = new List<(double X, double Y)>();
        int sliced = 0;
        for (int k = 0; k + 2 < world.Indices.Length; k += 3)
        {
            var poly = new List<Vector3> { world.Vertices[world.Indices[k]], world.Vertices[world.Indices[k + 1]], world.Vertices[world.Indices[k + 2]] };
            poly = Clip(poly, p => p.Z - lo); poly = Clip(poly, p => hi - p.Z);
            if (poly.Count == 0) continue;
            sliced++;
            foreach (var p in poly) pts.Add((p.X, p.Y));
        }
        if (sliced == 0)
            return CutFootprint.Refused($"Nothing of the obstacle's collision stands between step height ({SliceBottom:0.#} u) and an actor's top ({SliceTop:0.#} u) above the navmesh at {floor:0.#}, so an actor is not blocked by it and there is nothing to cut.");
        if (margin > 0) pts = pts.SelectMany(p => new[] { (p.X - margin, p.Y - margin), (p.X + margin, p.Y - margin), (p.X + margin, p.Y + margin), (p.X - margin, p.Y + margin) }).ToList();
        var hull = Hull(pts);
        double area2 = 0;
        for (int i = 0; i < hull.Count; i++) { var (a, b) = (hull[i], hull[(i + 1) % hull.Count]); area2 += a.X * b.Y - b.X * a.Y; }
        if (hull.Count < 3 || Math.Abs(area2) / 2 < 1)
            return CutFootprint.Refused("The obstacle's collision in the actor's band is flat in plan (under 1 u²), so it leaves no outline to cut; give a margin.");
        return new CutFootprint("planned", null, hull.Select(p => ((float)p.X, (float)p.Y)).ToList(), floor, floor - SliceBottom, hi, sliced);
    }

    /// <summary>A convex polygon clipped to where <paramref name="side"/> is zero or more (Sutherland-Hodgman, one plane).</summary>
    static List<Vector3> Clip(List<Vector3> poly, Func<Vector3, float> side)
    {
        var o = new List<Vector3>();
        for (int i = 0; i < poly.Count; i++)
        {
            var (p, q) = (poly[i], poly[(i + 1) % poly.Count]);
            float sp = side(p), sq = side(q);
            if (sp >= 0) o.Add(p);
            if ((sp >= 0) != (sq >= 0)) o.Add(p + (q - p) * (sp / (sp - sq)));
        }
        return o;
    }

    /// <summary>The convex hull, counter-clockwise, without collinear points (Andrew's monotone chain).</summary>
    public static List<(double X, double Y)> Hull(List<(double X, double Y)> pts)
    {
        var p = pts.Distinct().OrderBy(a => a.X).ThenBy(a => a.Y).ToList();
        if (p.Count < 3) return p;
        double Cr((double X, double Y) o, (double X, double Y) a, (double X, double Y) b) => (a.X - o.X) * (b.Y - o.Y) - (a.Y - o.Y) * (b.X - o.X);
        var h = new List<(double X, double Y)>();
        foreach (var pass in new[] { p, Enumerable.Reverse(p).ToList() })
        {
            int start = h.Count;
            foreach (var q in pass)
            {
                while (h.Count >= start + 2 && Cr(h[^2], h[^1], q) <= 1e-9) h.RemoveAt(h.Count - 1);
                h.Add(q);
            }
            h.RemoveAt(h.Count - 1);
        }
        return h;
    }
}
