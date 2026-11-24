using System.Numerics;

using HousecarlCore;

namespace SkyWright.Engine;

// SUPPORT-PLANE TERM (Part 3b round 2; Gate B question 1, accepted with the Gate B review's F1 condition) for a particle system whose
// gravity an in-game observation settled toward world -z (ParticleReach's SettledFold):
//  * a particle below the surface under it is inside that surface's body and is not drawn, PROVIDED the body is solid down to the
//    bottom of the unfolded region at every point of the region's footprint (or the surface is landscape). A thin plate with open
//    space below fails this, so the region keeps its downward extent (F1);
//  * the drawn particles are then above the plane P = the lowest surface over the footprint, so their horizontal distance from the
//    emitter is bounded by the drag-free projectile range from the emitter's bottom down to P, (v/g) sqrt(v^2 + 2 g h), capped by v T
//    (ParticleMotionReach.ProjectileRange), with g the WEAKER pull (x 1.0, the smallest strength key: a weaker pull throws farther).
// Applied only with no other acceleration on the system (an isotropic push would change the range law) and with no surface above the
// emitter's bottom inside the footprint. The footprint is sampled on a grid (step stated in the label): a hole narrower than one step
// is not seen (limit).

public sealed record SupportPlaneResult(Aabb? Region, string? Label, string? NotApplied);

public static class SupportPlane
{
    /// <summary>Every z at which a vertical line through (x, y) meets a triangle of <paramref name="soup"/> (vertical triangles
    /// skipped), with the soup's index.</summary>
    static void Hits(TriangleSoup soup, float x, float y, int id, List<(float Z, int Id)> into, Aabb? prefilter = null)
    {
        var v = soup.Vertices; var ix = soup.Indices;
        for (int t = 0; t + 2 < ix.Length; t += 3)
        {
            Vector3 a = v[ix[t]], b = v[ix[t + 1]], c = v[ix[t + 2]];
            if (x < MathF.Min(a.X, MathF.Min(b.X, c.X)) || x > MathF.Max(a.X, MathF.Max(b.X, c.X)) || y < MathF.Min(a.Y, MathF.Min(b.Y, c.Y)) || y > MathF.Max(a.Y, MathF.Max(b.Y, c.Y))) continue;
            float d = (b.Y - c.Y) * (a.X - c.X) + (c.X - b.X) * (a.Y - c.Y);
            if (MathF.Abs(d) < 1e-9f) continue;
            float l1 = ((b.Y - c.Y) * (x - c.X) + (c.X - b.X) * (y - c.Y)) / d;
            float l2 = ((c.Y - a.Y) * (x - c.X) + (a.X - c.X) * (y - c.Y)) / d;
            float l3 = 1 - l1 - l2;
            if (l1 < -1e-6f || l2 < -1e-6f || l3 < -1e-6f) continue;
            into.Add((l1 * a.Z + l2 * b.Z + l3 * c.Z, id));
        }
    }

    /// <summary>The term for one settled system: <paramref name="region"/> its unfolded world region, <paramref name="emitter"/> its
    /// emitter volume in world space, <paramref name="supports"/> the scene's supports under the footprint (terrain ids begin "LAND").</summary>
    public static SupportPlaneResult Apply(Aabb region, Aabb emitter, SettledFold fold, IReadOnlyList<DropSupport> supports, int gridCells = 48)
    {
        if (fold.AIso > 0) return new(null, null, "an isotropic acceleration (turbulence or a spherical force) acts: the range law does not hold");
        if (!(fold.GClimb > 0)) return new(null, null, "the weaker gravity is zero: no range bound");
        if (region.IsEmpty || emitter.IsEmpty) return new(null, null, "empty region");
        float step = MathF.Max(4f, MathF.Max(region.Max.X - region.Min.X, region.Max.Y - region.Min.Y) / gridCells);
        var hits = new List<(float Z, int Id)>();
        double plane = double.PositiveInfinity;
        var surf = new List<(float X, float Y, float Surface, bool Solid)>();
        var boxes = supports.Select(s => SpatialOverlap.SoupBounds(s.World)).ToList();
        for (float x = region.Min.X; x <= region.Max.X + 1e-3f; x += step)
            for (float y = region.Min.Y; y <= region.Max.Y + 1e-3f; y += step)
            {
                hits.Clear();
                for (int i = 0; i < supports.Count; i++)
                {
                    var bb = boxes[i];
                    if (x < bb.Min.X || x > bb.Max.X || y < bb.Min.Y || y > bb.Max.Y) continue;
                    Hits(supports[i].World, x, y, i, hits);
                }
                if (hits.Count == 0) return new(null, null, $"no surface under ({x:0}, {y:0}) inside the region's footprint");
                hits.Sort((p, q) => q.Z.CompareTo(p.Z));
                // a line through a shared edge meets both triangles: one face, one hit
                for (int k = hits.Count - 1; k > 0; k--) if (hits[k].Id == hits[k - 1].Id && Math.Abs(hits[k].Z - hits[k - 1].Z) < 1e-3f) hits.RemoveAt(k);
                // a surface between the emitter's bottom and the region's top inside the footprint: the plane is not under the emitter
                if (hits.Any(h => h.Z > emitter.Min.Z && h.Z < region.Max.Z)) return new(null, null, $"a surface above the emitter's bottom at ({x:0}, {y:0})");
                float top = hits[0].Z;
                bool solid;
                if (supports[hits[0].Id].Id.StartsWith("LAND", StringComparison.Ordinal)) solid = true;
                else
                {
                    // the body is solid from its surface down to its exit face; a body entered again within 1 u continues it; landscape
                    // below ends the question (nothing is drawn beneath it)
                    float exit = float.NaN; int k = 0; solid = false;
                    while (k < hits.Count)
                    {
                        if (supports[hits[k].Id].Id.StartsWith("LAND", StringComparison.Ordinal)) { solid = true; break; }
                        if (k + 1 >= hits.Count) break;                    // an open surface: no exit face, depth unknown
                        exit = hits[k + 1].Z;
                        if (exit <= region.Min.Z) { solid = true; break; }
                        if (k + 2 < hits.Count && hits[k + 2].Z >= exit - 1f) { k += 2; continue; }
                        break;                                            // open space below the exit face, within the fall
                    }
                }
                if (!solid) return new(null, null, $"open space beneath the surface at ({x:0}, {y:0}) within the remaining fall (F1)");
                plane = Math.Min(plane, top);
                surf.Add((x, y, top, solid));
            }
        if (!double.IsFinite(plane)) return new(null, null, "no plane");
        double h = Math.Max(0, emitter.Min.Z - plane);
        double range = ParticleMotionReach.ProjectileRange(fold.V, fold.GClimb, h, fold.T) + fold.R;
        float zmin = (float)(plane - fold.R);
        if (zmin <= region.Min.Z && range >= Math.Max(region.Max.X - region.Min.X, region.Max.Y - region.Min.Y)) return new(null, null, "the term does not tighten the region");
        float rr = (float)range;
        var tight = new Aabb(new Vec3(MathF.Max(region.Min.X, emitter.Min.X - rr), MathF.Max(region.Min.Y, emitter.Min.Y - rr), MathF.Max(region.Min.Z, MathF.BitDecrement(zmin))),
                             new Vec3(MathF.Min(region.Max.X, emitter.Max.X + rr), MathF.Min(region.Max.Y, emitter.Max.Y + rr), region.Max.Z), false);
        string label = $"uncertified: support-plane term - particles below the surface under the whole region are inside its solid body (checked from the scene's collision on a {step:0.#} u grid: no open space beneath within the fall, F1) and the drawn ones lie within the projectile range {range:0.#} u of the emitter from {h:0.#} u above the plane (drag-free, the weaker gravity)";
        return new(tight, label, null);
    }
}
