using System.Numerics;

using HousecarlCore;

namespace SkyWright.Engine;

/// <summary>A host face a dependent triangle faces within the band, gathered in step 1 of the contact and read in step 2.</summary>
internal readonly record struct FacingCandidate(int Tri, int HostTri, float Dist, Vector3 HostNormal, float Weight, bool Behind, bool Crosses);

public sealed partial class TriangleGrid
{
    /// <summary>Whether <see cref="NearestTriangle"/> with the same arguments would find a triangle: the same walk and the same tests, stopping at the first.</summary>
    internal bool AnyNearer(Vector3 a, Vector3 b, Vector3 c, float reach)
    {
        var qb = Aabb.Empty.Add(a).Add(b).Add(c);
        float limit = SpatialOverlap.LimitSquared(reach);
        if (_completeOrder is not null)
        {
            using var order = _completeOrder.Box(qb, reach);
            for (int k = 0; k < order.Count; k++)
            {
                int t = order[k]; if (SpatialOverlap.BoxDistance(qb, _tb[t]) >= reach) continue;
                var (ha, hb, hc) = GetTriangle(t);
                if (SpatialOverlap.TriangleDistanceUnder(a, b, c, ha, hb, hc, limit) < reach) return true;
            }
            return false;
        }
        var lo = new Vector3(qb.Min.X - reach, qb.Min.Y - reach, qb.Min.Z - reach);
        var hi = new Vector3(qb.Max.X + reach, qb.Max.Y + reach, qb.Max.Z + reach);
        var (x0, y0, z0) = CellOf(lo); var (x1, y1, z1) = CellOf(hi);
        if (SpatialOverlap.BoxDistance(qb, _bounds) >= reach) return false;
        var s = _cells.Span;
        x0 = Math.Max(x0, s.X0); y0 = Math.Max(y0, s.Y0); z0 = Math.Max(z0, s.Z0);
        x1 = Math.Min(x1, s.X1); y1 = Math.Min(y1, s.Y1); z1 = Math.Min(z1, s.Z1);
        for (int x = x0; x <= x1; x++)
            for (int y = y0; y <= y1; y++)
                for (int z = z0; z <= z1; z++)
                    foreach (var t in _cells.At(x, y, z))
                    {
                        if (!FirstInBox(_ranges[t], x, y, z, x0, y0, z0)) continue;
                        if (SpatialOverlap.BoxDistance(qb, _tb[t]) >= reach) continue;
                        if (SpatialOverlap.TriangleDistanceUnder(a, b, c, _v[_i[t * 3]], _v[_i[t * 3 + 1]], _v[_i[t * 3 + 2]], limit) < reach) return true;
                    }
        return false;
    }

    /// <summary>Throws what <see cref="NearestTriangle"/> with the same arguments would throw for a query outside the grid's domain, doing no other work.</summary>
    internal void CheckQueryDomain(Vector3 a, Vector3 b, Vector3 c, float reach)
    {
        var qb = Aabb.Empty.Add(a).Add(b).Add(c);
        if (_completeOrder is not null) { _completeOrder.CheckBox(qb, reach); return; }
        CellOf(new Vector3(qb.Min.X - reach, qb.Min.Y - reach, qb.Min.Z - reach));
        CellOf(new Vector3(qb.Max.X + reach, qb.Max.Y + reach, qb.Max.Z + reach));
    }

    /// <summary>Gathers the host faces dependent triangle <paramref name="tri"/> (a,b,c; unit normal <paramref name="nd"/>) faces within
    /// <paramref name="band"/>, in the order of <see cref="ForEachWithin"/>. With <paramref name="facingOnly"/>, leaves out what cannot lower the
    /// facing gap and returns true once a counted face at distance 0 settles it. <paramref name="facingBound"/> is the least counted distance so far.</summary>
    internal bool CollectFacing(Vector3 a, Vector3 b, Vector3 c, float band, Vector3 nd, float area, int tri, List<FacingCandidate> candidates, bool facingOnly, ref float facingBound, CancellationToken cancellationToken)
    {
        var qb = Aabb.Empty.Add(a).Add(b).Add(c);
        if (_completeOrder is not null)
        {
            using var order = _completeOrder.Box(qb, band);
            for (int k = 0; k < order.Count; k++)
                if (ConsiderFacing(order[k], a, b, c, qb, band, nd, area, tri, candidates, facingOnly, ref facingBound, cancellationToken)) return true;
            return false;
        }
        var lo = new Vector3(qb.Min.X - band, qb.Min.Y - band, qb.Min.Z - band);
        var hi = new Vector3(qb.Max.X + band, qb.Max.Y + band, qb.Max.Z + band);
        var (x0, y0, z0) = CellOf(lo); var (x1, y1, z1) = CellOf(hi);
        var s = _cells.Span;
        x0 = Math.Max(x0, s.X0); y0 = Math.Max(y0, s.Y0); z0 = Math.Max(z0, s.Z0);
        x1 = Math.Min(x1, s.X1); y1 = Math.Min(y1, s.Y1); z1 = Math.Min(z1, s.Z1);
        for (int x = x0; x <= x1; x++)
            for (int y = y0; y <= y1; y++)
                for (int z = z0; z <= z1; z++)
                    foreach (var t in _cells.At(x, y, z))
                    {
                        if (!FirstInBox(_ranges[t], x, y, z, x0, y0, z0)) continue;
                        if (ConsiderFacing(t, a, b, c, qb, band, nd, area, tri, candidates, facingOnly, ref facingBound, cancellationToken)) return true;
                    }
        return false;
    }

    bool ConsiderFacing(int t, Vector3 a, Vector3 b, Vector3 c, in Aabb qb, float band, Vector3 nd, float area, int tri, List<FacingCandidate> candidates, bool facingOnly, ref float facingBound, CancellationToken cancellationToken)
    {
        if (_n[t] == Vector3.Zero) return false;
        if (SpatialOverlap.BoxDistance(qb, _tb[t]) > band) return false;
        var nh = _n[t];
        // A face turned the same way is dropped whatever its distance, so it needs no distance.
        float par = Vector3.Dot(nd, nh);
        if (par > 0.9f) return false;
        float under = facingOnly && facingBound < band ? facingBound : band;
        float d = SpatialOverlap.TriangleClosestPointsUnder(a, b, c, _v[_i[t * 3]], _v[_i[t * 3 + 1]], _v[_i[t * 3 + 2]], SpatialOverlap.LimitSquared(under), out var p, out var q, out var crosses);
        if (!(d <= band)) return false;
        cancellationToken.ThrowIfCancellationRequested();
        var off = p - q;
        float len = off.Length();
        float s = Vector3.Dot(off, nh);
        // Past the bound and not crossing, this face changes neither the gap nor which faces count as crossed.
        if (facingOnly && !crosses && d >= facingBound) return false;
        if (len > 1e-3f && MathF.Abs(s) < 0.7f * len) return false;
        bool behind = len > 1e-3f && s < 0f;
        candidates.Add(new FacingCandidate(tri, t, d, nh, area * (MathF.Abs(par) + 0.05f), behind, crosses));
        if (!facingOnly || behind) return false;
        if (d < facingBound) facingBound = d;
        return d <= 0f;
    }
}
