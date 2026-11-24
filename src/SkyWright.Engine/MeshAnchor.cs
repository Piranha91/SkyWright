using System.Numerics;
using NiflySharp;
using NiflySharp.Blocks;
using static SkyWright.Engine.ExactDist;

using HousecarlCore;

namespace SkyWright.Engine;

// ANCHOR TEST (G1 build list, Stage A, row 2): does the other mod's solid geometry occupy the spot an actor takes at a
// model-less marker, idle marker, placed actor or furniture entry? Three readings against one partner:
//  * Point: the anchor inside / outside / on_surface / undecided. The partner is split into connected components (shared
//    vertex positions); each is validated as a closed solid (MeshIntersection's validator). Inside any closed component
//    (ray parity) is `inside`; `outside` needs every component closed and none containing it; an open or invalid
//    component that could contain it leaves `undecided` with its reason. Distance 0 to a triangle is `on_surface`.
//  * Line: the vertical segment [anchor.z + Bottom, anchor.z + Top] — the heights where it crosses opaque triangles, and
//    (when every component is closed) the length of it inside the partner.
//  * Body: the capsule of radius Radius whose axis runs from Bottom + Radius to Top − Radius above the anchor — `meets`
//    when an opaque triangle lies within Radius of the axis, or the axis lies inside a closed component; else `clear` with
//    its clearance (distance − Radius).
// The BODY decides (Gate A review Q5/F1); point and line are reported. Only opaque triangles decide (session 6's rule): a
// body met only by alpha / effect surfaces is `undecided`, naming them. An optional half-space (Lean furniture's declared
// support side, red-gate review ruling) removes the part of each triangle behind it from the body test only; the detail
// names the exclusion whenever it was applied.

/// <summary>The actor's body above its feet: band bottom and top (u above the anchor) and horizontal radius.</summary>
public sealed record BodyExtent(float Bottom, float Top, float Radius);

/// <summary>Points p with Dot(Normal, p) &lt; Offset (world) are excluded from the body test.</summary>
public readonly record struct HalfSpace(Vector3 Normal, float Offset);

public sealed record AnchorResult(string Point, string? PointReason, string Line, IReadOnlyList<double> LineCrossingHeights,
                                  double? LineInsideLength, string Body, double? BodyClearance, string Decision, string? Reason);

/// <summary>One furniture marker position from a NIF's BSFurnitureMarker data (model space).</summary>
public sealed record FurnitureEntry(Vector3 Offset, float Heading, string AnimationType, string EntryPoints);

public static class MeshAnchor
{
    public const string PolicyVersion = "anchor_body_v1";
    const double Eps = 1e-9;

    /// <summary>Test an anchor against a partner model placed by its reference transform.</summary>
    public static AnchorResult TestPlaced(TriangleSoup partnerModel, string[]? tags, Vec3 position, Vec3 rotation, float scale,
                                          Vector3 anchor, BodyExtent body, HalfSpace? exclude = null)
    {
        var world = SpatialOverlap.TransformSoup(partnerModel, position, rotation, scale);
        return Test(world, tags, anchor, body, exclude);
    }

    internal static AnchorResult Test(TriangleSoup world, string[]? tags, Vector3 anchor, BodyExtent body, HalfSpace? exclude)
    {
        // usage-week M4 (2026-10-14): triangles of a shape the game does not draw take no part in any reading
        if (tags is not null && tags.Length == world.TriangleCount && tags.Contains(SurfaceMaterial.NotDrawn))
            (world, tags, _, _) = MeshSurface.DropNotDrawn(world, tags);
        var v = world.Vertices; var ix = world.Indices; int nt = world.TriangleCount;
        bool Opaque(int t) => tags is null || t >= tags.Length || tags[t] == SurfaceMaterial.Opaque;
        (DV A, DV B, DV C) Tri(int t) => (D(v[ix[3 * t]]), D(v[ix[3 * t + 1]]), D(v[ix[3 * t + 2]]));
        double scaleRef = 1 + Math.Abs(anchor.X) + Math.Abs(anchor.Y) + Math.Abs(anchor.Z);
        double tol = Eps * scaleRef;

        // ---- components ----
        var comps = Components(world);
        var closed = new List<(TriangleSoup Soup, object Handle)>();
        var invalid = new List<(Aabb Box, string Reason)>();
        foreach (var c in comps)
        {
            var prep = MeshIntersection.PrepareVolumeSolid(c, 50_000_000, CancellationToken.None);
            if (prep.Solid is { } s) closed.Add((c, s.Handle)); else invalid.Add((SpatialOverlap.SoupBounds(c), prep.Reason ?? "invalid component"));
        }
        string? openReason = invalid.Count > 0 ? invalid[0].Reason : null;
        // 1 inside a closed component, 0 outside all, -1 undecided. An open or invalid component (an inward-wound cavity
        // among them) whose box holds the point could change the answer, so its presence there makes it undecided.
        int Inside(DV p)
        {
            foreach (var (b, _) in invalid)
                if (p.X >= b.Min.X && p.X <= b.Max.X && p.Y >= b.Min.Y && p.Y <= b.Max.Y && p.Z >= b.Min.Z && p.Z <= b.Max.Z) return -1;
            foreach (var (_, h) in closed)
            {
                int r = MeshIntersection.PointInSolid(h, p.X, p.Y, p.Z);
                if (r == 1) return 1;
                if (r < 0) return -1;
            }
            return 0;
        }
        string? ReasonAt(DV p)
        {
            foreach (var (b, why) in invalid)
                if (p.X >= b.Min.X && p.X <= b.Max.X && p.Y >= b.Min.Y && p.Y <= b.Max.Y && p.Z >= b.Min.Z && p.Z <= b.Max.Z) return why;
            return null;
        }

        // ---- point ----
        var pa = D(anchor);
        double dmin = double.MaxValue;
        bool nonFinite = false;
        for (int t = 0; t < nt; t++) { var (a, b, c) = Tri(t); double d0 = (pa - Closest(pa, a, b, c)).Len; if (!double.IsFinite(d0)) nonFinite = true; else dmin = Math.Min(dmin, d0); }
        string point; string? pointReason = null;
        if (nt == 0) { point = "outside"; }
        else if (dmin <= tol) point = "on_surface";
        else
        {
            int r = Inside(pa);
            point = r == 1 ? "inside" : r == 0 ? "outside" : "undecided";
            if (r < 0) pointReason = ReasonAt(pa) ?? "ray parity undecided";
        }

        // ---- line ----
        var lo = pa + new DV(0, 0, body.Bottom); var hi = pa + new DV(0, 0, body.Top);
        var heights = new List<double>();
        for (int t = 0; t < nt; t++)
        {
            if (!Opaque(t)) continue;
            var (a, b, c) = Tri(t);
            if (SegmentHit(lo, hi, a, b, c) is { } x) heights.Add(Math.Round(x.Z - pa.Z, 9));
        }
        heights = heights.Distinct().OrderBy(h => h).ToList();
        double? inside = null;
        if (nt > 0)
        {
            // inside length: parity alternates at each crossing, starting from the bottom point's state
            var cuts = new List<double> { body.Bottom }; cuts.AddRange(heights.Where(h => h > body.Bottom && h < body.Top)); cuts.Add(body.Top);
            double len = 0; bool undecidedPiece = false;
            for (int k = 0; k + 1 < cuts.Count; k++)
            {
                var mid = pa + new DV(0, 0, (cuts[k] + cuts[k + 1]) / 2);
                int r = Inside(mid);
                if (r < 0) { undecidedPiece = true; break; }
                if (r == 1) len += cuts[k + 1] - cuts[k];
            }
            inside = undecidedPiece ? null : len;
        }
        string line = heights.Count > 0 ? "crosses" : inside is > 0 ? "inside" : "clear";

        // ---- body ----
        float r0 = body.Radius;
        double axLo = body.Bottom + r0, axHi = body.Top - r0;
        if (axHi < axLo) axLo = axHi = (body.Bottom + body.Top) / 2.0;
        var p0 = pa + new DV(0, 0, axLo); var p1 = pa + new DV(0, 0, axHi);
        double dOpaque = double.MaxValue, dOther = double.MaxValue; var otherTags = new SortedSet<string>();
        bool excluded = false;
        for (int t = 0; t < nt; t++)
        {
            var (a, b, c) = Tri(t);
            foreach (var (x, y, z) in Clip(a, b, c, exclude, ref excluded))
            {
                var pr = SegTri(p0, p1, x, y, z);
                double d = (pr.P - pr.Q).Len;
                if (!double.IsFinite(d)) { nonFinite = true; continue; }
                if (Opaque(t)) dOpaque = Math.Min(dOpaque, d);
                else if (d <= r0) { dOther = Math.Min(dOther, d); otherTags.Add(tags![t]); }
            }
        }
        bool axisInside = closed.Count > 0 && Inside(pa + new DV(0, 0, (axLo + axHi) / 2)) == 1 && exclude is null;
        string bodyRes; double? clearance = null; string decision; string? reason = null;
        if (dOpaque <= r0 || axisInside) { bodyRes = "meets"; decision = "meets"; reason = axisInside && dOpaque > r0 ? "the body lies inside a closed component" : null; }
        else if (dOther <= r0) { bodyRes = "undecided"; decision = "undecided"; reason = "only non-opaque surfaces in the body: " + string.Join(", ", otherTags); }
        else { bodyRes = "clear"; clearance = dOpaque == double.MaxValue ? null : dOpaque - r0; decision = "clear"; }
        // any non-finite distance anywhere refuses the readings by name: it never compares its way into clear / outside
        if (nonFinite) { point = "undecided"; pointReason = "non-finite distance"; bodyRes = "undecided"; clearance = null; decision = "undecided"; reason = "non-finite distance"; }
        if (exclude is not null) reason = (reason is null ? "" : reason + "; ") + (excluded ? "support half-space excluded from the body test" : "support half-space excluded from the body test (no geometry behind it)");
        return new(point, pointReason, line, heights, inside, bodyRes, clearance, decision, reason);
    }

    /// <summary>The part of triangle abc on the kept side (Dot(n, p) >= offset), fan-triangulated.</summary>
    static IEnumerable<(DV, DV, DV)> Clip(DV a, DV b, DV c, HalfSpace? h, ref bool excluded)
    {
        if (h is not { } hs) return new[] { (a, b, c) };
        var n = D(hs.Normal); double off = hs.Offset;
        var poly = new List<DV> { a, b, c }; var outp = new List<DV>();
        for (int i = 0; i < poly.Count; i++)
        {
            var s = poly[i]; var e = poly[(i + 1) % poly.Count];
            double ds = Dot(n, s) - off, de = Dot(n, e) - off;
            if (ds >= 0) outp.Add(s);
            if ((ds >= 0) != (de >= 0)) outp.Add(s + (e - s) * (ds / (ds - de)));
        }
        if (!(outp.Count == 3 && outp[0].Equals(a) && outp[1].Equals(b) && outp[2].Equals(c))) excluded = true;
        var res = new List<(DV, DV, DV)>();
        for (int i = 1; i + 1 < outp.Count; i++) res.Add((outp[0], outp[i], outp[i + 1]));
        return res;
    }

    /// <summary>Split a soup into connected components by shared vertex positions.</summary>
    static List<TriangleSoup> Components(TriangleSoup s)
    {
        int n = s.TriangleCount;
        var parent = Enumerable.Range(0, n).ToArray();
        int Find(int x) { while (parent[x] != x) x = parent[x] = parent[parent[x]]; return x; }
        var owner = new Dictionary<Vector3, int>();
        for (int t = 0; t < n; t++)
            for (int k = 0; k < 3; k++)
            {
                var p = s.Vertices[s.Indices[3 * t + k]];
                if (owner.TryGetValue(p, out var o)) { int a = Find(o), b = Find(t); if (a != b) parent[a] = b; }
                else owner[p] = t;
            }
        var groups = new Dictionary<int, List<int>>();
        for (int t = 0; t < n; t++) { int r = Find(t); if (!groups.TryGetValue(r, out var l)) groups[r] = l = new(); l.Add(t); }
        var res = new List<TriangleSoup>();
        foreach (var g in groups.Values)
        {
            var map = new Dictionary<int, int>(); var vs = new List<Vector3>(); var ix = new List<int>();
            foreach (int t in g)
                for (int k = 0; k < 3; k++)
                {
                    int vi = s.Indices[3 * t + k];
                    if (!map.TryGetValue(vi, out var m)) { m = vs.Count; map[vi] = m; vs.Add(s.Vertices[vi]); }
                    ix.Add(m);
                }
            res.Add(new TriangleSoup(vs.ToArray(), ix.ToArray()));
        }
        return res;
    }

    /// <summary>The furniture marker positions a NIF declares.</summary>
    public static IReadOnlyList<FurnitureEntry> FurnitureEntries(byte[] nif)
    {
        var f = new NifFile();
        using (var ms = new MemoryStream(nif, writable: false))
            if (f.Load(ms) != 0) throw new InvalidDataException("the NIF could not be read");
        var list = new List<FurnitureEntry>();
        foreach (var fm in f.Blocks.OfType<BSFurnitureMarker>())
            foreach (var p in fm.Positions ?? new())
                list.Add(new FurnitureEntry(p.Offset, p.Heading, p.AnimationType.ToString(), p.EntryProperties.ToString()));
        return list;
    }

    /// <summary>Where the engine puts the actor for an entry: the entry offset under the reference transform.</summary>
    public static Vector3 EntryWorld(FurnitureEntry entry, Vec3 position, Vec3 rotation, float scale)
        => SpatialOverlap.TransformSoup(new TriangleSoup(new[] { entry.Offset }, Array.Empty<int>()), position, rotation, scale).Vertices[0];
}
