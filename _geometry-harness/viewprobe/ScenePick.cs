using System.Numerics;

namespace ViewProbe;

/// <summary>The CPU ray pick over the scene's pick shapes, shared by the fly-through and the conflict review workbench:
/// an AABB shortlist sorted by entry distance, then double-sided ray-triangle tests on the same world-baked triangles the
/// GPU draws. Returns the nearest hit, carrying the surface behind a plane and the planes the ray crosses beyond.</summary>
static class ScenePick
{
    /// <summary>The frustum's near plane, the game's <c>fNearDistance</c>; nothing nearer is a hit.</summary>
    internal const float NearPlane = 15f;

    internal static PickHit? Pick(IEnumerable<PickShape> shapes, Vector3 ro, Vector3 rd, Func<PickShape, bool> drawn)
    {
        var candidates = new List<(PickShape S, float T0)>();
        foreach (var s in shapes)
        {
            if (!drawn(s)) continue;
            if (RayAabb(ro, rd, s.Min, s.Max, out var t0)) candidates.Add((s, t0));
        }
        candidates.Sort((a, b) => a.T0.CompareTo(b.T0));

        // Three answers, not one: the nearest hit, the nearest hit that is NOT a plane, and every
        // drawn plane the ray crosses. A plane is translucent by nature — what it stands in front
        // of is half of its identity — so a plane hit carries the surface behind it; and a plane
        // BEHIND the nearest surface is still in the picture (its outline is dashed there), so a
        // hit on a wall carries the planes the ray goes on to cross. Planes are few, so the walk
        // tests all of them; the solid walk still stops at the first box past the best hit.
        PickHit? best = null, solid = null;
        List<PickHit>? planeHits = null;
        foreach (var (s, t0) in candidates)
        {
            bool isPlane = s.Kind == PickKind.Plane;
            if (!isPlane && solid is { } s0 && t0 > s0.T) continue;
            var v = s.Verts; var tr = s.Tris;
            for (int i = 0; i + 2 < tr.Length; i += 3)
                if (RayTri(ro, rd, v[tr[i]], v[tr[i + 1]], v[tr[i + 2]], out var t))
                {
                    var hit = new PickHit(s, i / 3, t, ro + rd * t);
                    if (best is not { } bb || t < bb.T) best = hit;
                    if (isPlane) (planeHits ??= new()).Add(hit);
                    else if (solid is not { } ss || t < ss.T) solid = hit;
                }
        }
        if (best is null) return null;
        planeHits?.Sort((a, b) => a.T.CompareTo(b.T));
        if (best.Shape.Kind == PickKind.Plane)
            return best with { Behind = solid, PlanesBeyond = planeHits?.Where(p => p.T > best.T + 0.5f).ToList() };
        var beyond = planeHits?.Where(p => p.T > best.T).ToList();
        return beyond is { Count: > 0 } ? best with { PlanesBeyond = beyond } : best;
    }

    // Ray-AABB slab test; tEnter is the entry distance (0 when the origin is inside).
    internal static bool RayAabb(in Vector3 o, in Vector3 d, in Vector3 mn, in Vector3 mx, out float tEnter)
    {
        tEnter = 0f;
        float t0 = 0f, t1 = float.MaxValue;
        for (int i = 0; i < 3; i++)
        {
            float oi = i == 0 ? o.X : i == 1 ? o.Y : o.Z;
            float di = i == 0 ? d.X : i == 1 ? d.Y : d.Z;
            float lo = i == 0 ? mn.X : i == 1 ? mn.Y : mn.Z;
            float hi = i == 0 ? mx.X : i == 1 ? mx.Y : mx.Z;
            if (MathF.Abs(di) < 1e-12f)
            {
                if (oi < lo || oi > hi) return false;
                continue;
            }
            float inv = 1f / di;
            float ta = (lo - oi) * inv, tb = (hi - oi) * inv;
            if (ta > tb) (ta, tb) = (tb, ta);
            t0 = MathF.Max(t0, ta); t1 = MathF.Min(t1, tb);
            if (t0 > t1) return false;
        }
        tEnter = t0;
        return true;
    }

    // Möller–Trumbore, double-sided (the scene draws plenty of double-sided geometry).
    internal static bool RayTri(in Vector3 o, in Vector3 d, in Vector3 a, in Vector3 b, in Vector3 c, out float t)
    {
        t = 0f;
        var e1 = b - a; var e2 = c - a;
        var p = Vector3.Cross(d, e2);
        float det = Vector3.Dot(e1, p);
        if (MathF.Abs(det) < 1e-9f) return false;
        float inv = 1f / det;
        var s = o - a;
        float u = Vector3.Dot(s, p) * inv;
        if (u < 0f || u > 1f) return false;
        var q = Vector3.Cross(s, e1);
        float v = Vector3.Dot(d, q) * inv;
        if (v < 0f || u + v > 1f) return false;
        t = Vector3.Dot(e2, q) * inv;
        return t > NearPlane;                   // nothing nearer than the near plane is a hit
    }
}
