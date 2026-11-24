using System.Linq;
using System.Numerics;
using HousecarlCore;
using SkyWright.Engine;
using Mutagen.Bethesda.Plugins;

namespace HarnessShared;

// ======================================================================
//  NavCover - what the winning navmesh answers at one XY, as ONE set of
//  functions every caller shares.
//
//  Moved here from viewprobe (BatEmit.NearestCoverZ / Clearance and
//  StationMode.NavAt) on 2026-09-25, unchanged, so that the station rule's
//  route mode (RouteStations, which proofprobe also compiles) asks the
//  navmesh the same question --route and --stations ask. viewprobe keeps
//  thin delegating members under the old names.
//
//  The containment test is --nav-at's 2D barycentric test throughout.
// ======================================================================

public static class NavCover
{
    /// <summary>The player capsule is ~35 u in radius, so a point that is technically ON navmesh but
    /// hard against a wall still embeds the tester. Probe the point plus this much in x and y.</summary>
    public const float ClearanceRadius = 64f;

    /// <summary>How far above or below the shot's own floor a covering triangle may sit and still be
    /// taken as the floor that shot meant. Beyond this it is another storey.</summary>
    public const float SameFloorZ = 192f;

    static Vector3 V(Vec3 v) => new(v.X, v.Y, v.Z);

    /// <summary>The winning navmesh's answer at one XY: whether there was a navmesh to ask at all,
    /// whether a triangle covers the point, whether any covering triangle is DRAWN (not flagged
    /// deleted), the height, how many triangles cover it, and WHICH drawn triangle was taken.
    ///
    /// Several drawn triangles can cover one XY where storeys overlap - a bridge deck over a path.
    /// The HIGHEST is taken and the count is reported, because a station stands on the top surface;
    /// the lower one would put the eye inside the deck above it. When nothing drawn covers the XY,
    /// the height is the highest covering (deleted) triangle's and <paramref name="Tri"/> is null.
    /// A caller that knows the storey it stands on (a route candidate on its path, 2026-11-15) passes
    /// <paramref name="nearZ"/>, and the drawn triangle nearest that height is taken instead.</summary>
    public static (bool Answered, bool Covered, bool Drawn, float Z, int Covers, NavGraph.TriRef? Tri) At(
        IEnumerable<NavmeshGeometry> navs, float qx, float qy, float? nearZ = null)
    {
        bool answered = false, covered = false;
        float bestAny = float.NaN, bestDrawn = float.NaN;
        NavGraph.TriRef? bestTri = null;
        int covers = 0;
        foreach (var nav in navs)
        {
            if (nav.Triangles.Count > 0) answered = true;
            for (int ti = 0; ti < nav.Triangles.Count; ti++)
            {
                var tri = nav.Triangles[ti];
                if (tri.A < 0 || tri.B < 0 || tri.C < 0
                    || tri.A >= nav.Vertices.Count || tri.B >= nav.Vertices.Count
                    || tri.C >= nav.Vertices.Count) continue;
                var a = V(nav.Vertices[tri.A]); var b = V(nav.Vertices[tri.B]); var c = V(nav.Vertices[tri.C]);
                float d = (b.Y - c.Y) * (a.X - c.X) + (c.X - b.X) * (a.Y - c.Y);
                if (MathF.Abs(d) < 1e-6f) continue;
                float u = ((b.Y - c.Y) * (qx - c.X) + (c.X - b.X) * (qy - c.Y)) / d;
                float v = ((c.Y - a.Y) * (qx - c.X) + (a.X - c.X) * (qy - c.Y)) / d;
                float w = 1f - u - v;
                if (u < 0f || v < 0f || w < 0f) continue;
                covered = true; covers++;
                float z = u * a.Z + v * b.Z + w * c.Z;
                if (float.IsNaN(bestAny) || z > bestAny) bestAny = z;
                if ((tri.Flags & 0x08) == 0 && (float.IsNaN(bestDrawn)
                    || (nearZ is { } nz ? MathF.Abs(z - nz) < MathF.Abs(bestDrawn - nz) : z > bestDrawn)))
                {
                    bestDrawn = z;
                    bestTri = new NavGraph.TriRef(nav.FormKey, ti);
                }
            }
        }
        bool drawn = !float.IsNaN(bestDrawn);
        return (answered, covered, drawn, drawn ? bestDrawn : bestAny, covers, drawn ? bestTri : null);
    }

    /// <summary>Z of the drawable triangle covering (x,y) whose height is nearest <paramref name="nearZ"/>;
    /// null when nothing covers it. The same 2D barycentric containment --nav-at reports with.</summary>
    public static float? NearestCoverZ(IEnumerable<NavmeshGeometry> navs, float x, float y, float nearZ)
    {
        float? best = null;
        foreach (var nv in navs)
            for (int ti = 0; ti < nv.Triangles.Count; ti++)
            {
                var tri = nv.Triangles[ti];
                if ((tri.Flags & 0x08) != 0) continue;                              // deleted: the game has no such floor
                if (tri.A < 0 || tri.B < 0 || tri.C < 0
                    || tri.A >= nv.Vertices.Count || tri.B >= nv.Vertices.Count || tri.C >= nv.Vertices.Count) continue;
                var a = V(nv.Vertices[tri.A]); var b = V(nv.Vertices[tri.B]); var c = V(nv.Vertices[tri.C]);
                float d = (b.Y - c.Y) * (a.X - c.X) + (c.X - b.X) * (a.Y - c.Y);
                if (MathF.Abs(d) < 1e-6f) continue;
                float u = ((b.Y - c.Y) * (x - c.X) + (c.X - b.X) * (y - c.Y)) / d;
                float v = ((c.Y - a.Y) * (x - c.X) + (a.X - c.X) * (y - c.Y)) / d;
                float w = 1f - u - v;
                if (u < 0f || v < 0f || w < 0f) continue;
                float z = u * a.Z + v * b.Z + w * c.Z;
                // A multi-storey interior covers one X,Y several times; the floor the shot means is
                // the one nearest its own z, not whichever record happened to be enumerated first.
                if (best is null || MathF.Abs(z - nearZ) < MathF.Abs(best.Value - nearZ)) best = z;
            }
        return best;
    }

    /// <summary>
    /// Is this a place a person can stand? Five probes — the point and plus/minus
    /// <see cref="ClearanceRadius"/> in x and y — all required to be covered by a drawable triangle
    /// on the same floor. Returns the sentence the manifest prints either way, because "we checked
    /// and it is fine" is worth as much to the reader as the warning is.
    ///
    /// --route validates its waypoints with THIS test, --emit-bats warns with it, and the station
    /// rule's route mode refuses TightClearance with it, so a spot one calls standable and a spot
    /// another walks over cannot disagree. <paramref name="Covered"/> is how the callers differ.
    /// </summary>
    public static (bool Ok, int Covered, string Why) Clearance(
        IEnumerable<NavmeshGeometry> navs, float x, float y, float floorZ)
    {
        if (!navs.Any()) return (false, 0, "NO NAVMESH in this cell - clearance unverifiable");

        var probes = new[]
        {
            (X: x, Y: y),
            (X: x + ClearanceRadius, Y: y),
            (X: x - ClearanceRadius, Y: y),
            (X: x, Y: y + ClearanceRadius),
            (X: x, Y: y - ClearanceRadius),
        };

        int covered = 0;
        float worstDz = 0f;
        foreach (var (px, py) in probes)
        {
            if (NearestCoverZ(navs, px, py, floorZ) is not { } z) continue;
            if (MathF.Abs(z - floorZ) > SameFloorZ) continue;      // covered, but by another storey
            covered++;
            worstDz = MathF.Max(worstDz, MathF.Abs(z - floorZ));
        }

        if (covered == 0) return (false, 0, "NOT STANDABLE: no walkable triangle covers this point at this height");
        if (covered < 5) return (false, covered, $"TIGHT: only {covered}/5 clearance probes are on navmesh - the capsule may clip a wall");
        return (true, 5, $"standable: 5/5 clearance probes on navmesh (floor within {worstDz:0} u)");
    }
}
