using HousecarlCore;
using Mutagen.Bethesda.Plugins;

namespace HarnessShared;

// THE STATION MARKER CHECK (request R5 of houseCARL's navmesh regeneration plan, 2026-11-21; the DevBench rule "check the
// route offline before launch": a start or target marker must sit well inside a navmesh triangle, not on an edge with no
// neighbour beyond it, nor next to a gap or a rise above the step limit). A pure function over navmesh geometry: `navedit
// marker` reads the winning versions and calls it. What it reports for one marker:
//   - where it stands: the walkable triangle under its XY whose height there is nearest the marker's z (a deleted triangle,
//     flag 0x08, is not walkable), and the marker's height above it;
//   - the nearest OPEN edge an actor reaches over connected ground: walked from the standing triangle across intra-mesh
//     neighbours and edge links, through every triangle within Reach of the marker. An open edge has no neighbour (its
//     link is -1 with the edge's link bit clear), a deleted or out-of-range neighbour, or an edge link into a navmesh the
//     load order does not have (`absent`: missing or deleted). An edge link into a navmesh that was simply not given is
//     UNFOLLOWED: never read as open or as closed, and a refusal when it lies inside the margin;
//   - what lies beyond that edge, walked outward in BeyondStep steps up to BeyondReach: the first walkable triangle, how
//     wide the gap before it is (0 when it starts at the edge) and its height step from the edge, or nothing within reach;
//   - the height step across each edge link within the margin, measured at the nearest point of the linked triangle.
// The verdict is "inside" only when the marker stands on the navmesh within MaxAbove of it, its nearest open edge is at
// least Margin away, no unfollowed link lies within Margin, and no linked edge within Margin steps up or down more than
// StepLimit (a drop is refused too: a station next to a ledge is not a crossing station). Defaults: Margin 64 u, StepLimit
// 64 u (CLAIMS Q25: a 64 u step is sure, 76 u is refused), MaxAbove 32 u (Q24: navmesh sits -9..+12 u from the floor).
// Each navmesh's box is taken from its vertices, never its record's stated bounds, which can be stale.

public sealed record MarkerEdge(FormKey Mesh, int Triangle, int Edge, double Distance, Vec3 At);

public sealed record MarkerBeyond(FormKey? Mesh, int Triangle, double Step, double Gap);

public sealed record MarkerLinkStep(FormKey Mesh, int Triangle, int Edge, FormKey ToMesh, int ToTriangle, int Type, double Distance, double Step);

public sealed record MarkerResult(string Verdict, IReadOnlyList<string> Why, FormKey? Mesh, int Triangle, double? StandZ, double? Above,
                                  MarkerEdge? OpenEdge, MarkerBeyond? Beyond, IReadOnlyList<MarkerLinkStep> LinkSteps,
                                  IReadOnlyList<(FormKey Mesh, double Distance)> Unfollowed, double Reach);

public static class NavMarker
{
    public const double DefaultMargin = 64, DefaultStepLimit = 64, DefaultMaxAbove = 32, BeyondStep = 8, BeyondReach = 128;
    const int DeletedFlag = 0x08;

    static (Vec3 A, Vec3 B, Vec3 C) Corners(NavmeshGeometry g, NavTri t) => (g.Vertices[t.A], g.Vertices[t.B], g.Vertices[t.C]);

    /// <summary>The triangle's height at (x, y) on its own plane, and whether (x, y) lies on it in plan (within 1e-6 of
    /// its barycentric range). Null for a triangle with no area in plan.</summary>
    public static (double Z, bool Inside)? PlaneAt(NavmeshGeometry g, NavTri t, double x, double y)
    {
        var (a, b, c) = Corners(g, t);
        double den = ((double)b.Y - c.Y) * ((double)a.X - c.X) + ((double)c.X - b.X) * ((double)a.Y - c.Y);
        if (Math.Abs(den) < 1e-9) return null;
        double l1 = (((double)b.Y - c.Y) * (x - c.X) + ((double)c.X - b.X) * (y - c.Y)) / den;
        double l2 = (((double)c.Y - a.Y) * (x - c.X) + ((double)a.X - c.X) * (y - c.Y)) / den;
        double l3 = 1 - l1 - l2;
        return (l1 * a.Z + l2 * b.Z + l3 * c.Z, l1 >= -1e-6 && l2 >= -1e-6 && l3 >= -1e-6);
    }

    static (double D, double Px, double Py, double T) SegDist(double x, double y, Vec3 p, Vec3 q)
    {
        double dx = (double)q.X - p.X, dy = (double)q.Y - p.Y, l2 = dx * dx + dy * dy;
        double t = l2 == 0 ? 0 : Math.Clamp(((x - p.X) * dx + (y - p.Y) * dy) / l2, 0, 1);
        double px = p.X + t * dx, py = p.Y + t * dy;
        return (Math.Sqrt((x - px) * (x - px) + (y - py) * (y - py)), px, py, t);
    }

    static (Vec3 P, Vec3 Q) EdgeEnds(NavmeshGeometry g, NavTri t, int e) => e switch
    {
        0 => (g.Vertices[t.A], g.Vertices[t.B]),
        1 => (g.Vertices[t.B], g.Vertices[t.C]),
        _ => (g.Vertices[t.C], g.Vertices[t.A]),
    };

    /// <summary>The neighbour across edge e (0 A-B, 1 B-C, 2 C-A): ("open", -), ("same", tri), ("link", link index).</summary>
    static (string Kind, int Index) Across(NavTri t, int e)
    {
        short link = e switch { 0 => t.EdgeLink01, 1 => t.EdgeLink12, _ => t.EdgeLink20 };
        bool viaTable = (t.Flags & (1 << e)) != 0;
        if (viaTable) return ("link", link);
        return link < 0 ? ("open", -1) : ("same", link);
    }

    static double PlanDist(NavmeshGeometry g, NavTri t, double x, double y)
    {
        if (PlaneAt(g, t, x, y) is { Inside: true }) return 0;
        double d = double.MaxValue;
        for (int e = 0; e < 3; e++) { var (p, q) = EdgeEnds(g, t, e); d = Math.Min(d, SegDist(x, y, p, q).D); }
        return d;
    }

    /// <summary>The triangle's height at its nearest point to (x, y) in plan: on its plane when (x, y) lies on it, else on
    /// its nearest edge (never extrapolated past the triangle).</summary>
    static double? NearestZ(NavmeshGeometry g, NavTri t, double x, double y)
    {
        if (PlaneAt(g, t, x, y) is { Inside: true } h) return h.Z;
        double best = double.MaxValue; double? z = null;
        for (int e = 0; e < 3; e++)
        {
            var (p, q) = EdgeEnds(g, t, e);
            var sd = SegDist(x, y, p, q);
            if (sd.D < best) { best = sd.D; z = p.Z + sd.T * ((double)q.Z - p.Z); }
        }
        return z;
    }

    static bool Walkable(NavTri t) => (t.Flags & DeletedFlag) == 0;

    /// <summary>A navmesh's box in plan, from its vertices.</summary>
    static (double X0, double Y0, double X1, double Y1) Box(NavmeshGeometry g)
    {
        double x0 = double.MaxValue, y0 = double.MaxValue, x1 = double.MinValue, y1 = double.MinValue;
        foreach (var v in g.Vertices) { x0 = Math.Min(x0, v.X); y0 = Math.Min(y0, v.Y); x1 = Math.Max(x1, v.X); y1 = Math.Max(y1, v.Y); }
        return (x0, y0, x1, y1);
    }

    /// <summary>Walkable triangles of every loaded navmesh under (x, y), with their height there.</summary>
    static IEnumerable<(FormKey Mesh, int Tri, double Z)> Under(IReadOnlyDictionary<FormKey, NavmeshGeometry> meshes,
                                                                Dictionary<FormKey, (double X0, double Y0, double X1, double Y1)> boxes, double x, double y)
    {
        foreach (var (k, g) in meshes)
        {
            if (!boxes.TryGetValue(k, out var b)) boxes[k] = b = Box(g);
            if (x < b.X0 - 1 || x > b.X1 + 1 || y < b.Y0 - 1 || y > b.Y1 + 1) continue;
            for (int i = 0; i < g.Triangles.Count; i++)
                if (Walkable(g.Triangles[i]) && PlaneAt(g, g.Triangles[i], x, y) is { Inside: true } h) yield return (k, i, h.Z);
        }
    }

    /// <param name="absent">Navmeshes the load order does not have (missing or deleted): an edge link into one is open.</param>
    public static MarkerResult Check(IReadOnlyDictionary<FormKey, NavmeshGeometry> meshes, Vec3 at,
                                     double margin = DefaultMargin, double stepLimit = DefaultStepLimit,
                                     IReadOnlySet<FormKey>? absent = null, double maxAbove = DefaultMaxAbove)
    {
        double reach = Math.Max(2 * margin, 128);
        var boxes = new Dictionary<FormKey, (double, double, double, double)>();
        var stand = Under(meshes, boxes, at.X, at.Y).OrderBy(u => Math.Abs(u.Z - at.Z)).Cast<(FormKey Mesh, int Tri, double Z)?>().FirstOrDefault();
        var noSteps = Array.Empty<MarkerLinkStep>();
        var noUnf = Array.Empty<(FormKey, double)>();
        if (stand is null)
        {
            double near = double.MaxValue;
            foreach (var g in meshes.Values)
                for (int i = 0; i < g.Triangles.Count; i++)
                    if (Walkable(g.Triangles[i])) near = Math.Min(near, PlanDist(g, g.Triangles[i], at.X, at.Y));
            string where = near == double.MaxValue ? "no loaded navmesh has a walkable triangle" : $"the nearest walkable triangle is {near:0} u away in plan";
            return new MarkerResult("refused", new[] { $"The marker at {at.X:0},{at.Y:0},{at.Z:0} stands on no walkable navmesh triangle ({where}); move it onto the navmesh." },
                                    null, -1, null, null, null, null, noSteps, noUnf, reach);
        }
        var (sm, st, sz) = stand.Value;

        // walk connected ground from the standing triangle, through every triangle within reach of the marker
        var seen = new HashSet<(FormKey, int)> { (sm, st) };
        var queue = new Queue<(FormKey, int)>(); queue.Enqueue((sm, st));
        MarkerEdge? open = null;
        var steps = new Dictionary<(string, string), MarkerLinkStep>();
        var unfollowed = new Dictionary<FormKey, double>();
        void Open(FormKey mk, int ti, int e, (double D, double Px, double Py, double T) sd, Vec3 p, Vec3 q)
        {
            if (open is null || sd.D < open.Distance)
                open = new MarkerEdge(mk, ti, e, sd.D, new Vec3((float)sd.Px, (float)sd.Py, (float)(p.Z + sd.T * ((double)q.Z - p.Z))));
        }
        while (queue.Count > 0)
        {
            var (mk, ti) = queue.Dequeue();
            var g = meshes[mk]; var t = g.Triangles[ti];
            for (int e = 0; e < 3; e++)
            {
                var (p, q) = EdgeEnds(g, t, e);
                var sd = SegDist(at.X, at.Y, p, q);
                var (kind, idx) = Across(t, e);
                FormKey nm = mk; int nt;
                if (kind == "open") { Open(mk, ti, e, sd, p, q); continue; }
                if (kind == "same") nt = idx;
                else
                {
                    var links = g.EdgeLinks ?? Array.Empty<NavEdgeLink>();
                    if (idx < 0 || idx >= links.Count) { Open(mk, ti, e, sd, p, q); continue; }   // a link entry that is not there
                    var l = links[idx];
                    if (absent is not null && absent.Contains(l.Mesh)) { Open(mk, ti, e, sd, p, q); continue; }   // into nothing
                    if (!meshes.TryGetValue(l.Mesh, out var lg))
                    {
                        unfollowed[l.Mesh] = Math.Min(unfollowed.GetValueOrDefault(l.Mesh, double.MaxValue), sd.D);
                        continue;
                    }
                    nm = l.Mesh; nt = l.TriangleIndex;
                    if (nt >= 0 && nt < lg.Triangles.Count && Walkable(lg.Triangles[nt]) && sd.D <= margin)
                    {
                        double ez = p.Z + sd.T * ((double)q.Z - p.Z);
                        if (NearestZ(lg, lg.Triangles[nt], sd.Px, sd.Py) is { } oz)
                        {
                            // one row per crossing (the two triangles), whichever side it was reached from; where two
                            // readings of one crossing differ, the larger step stands
                            var key = string.CompareOrdinal($"{mk}#{ti}", $"{nm}#{nt}") <= 0 ? ($"{mk}#{ti}", $"{nm}#{nt}") : ($"{nm}#{nt}", $"{mk}#{ti}");
                            var row = new MarkerLinkStep(mk, ti, e, nm, nt, l.Type, sd.D, oz - ez);
                            if (!steps.TryGetValue(key, out var had) || Math.Abs(row.Step) > Math.Abs(had.Step)) steps[key] = row;
                        }
                    }
                }
                if (nt < 0 || nt >= meshes[nm].Triangles.Count || !Walkable(meshes[nm].Triangles[nt]))
                {   // a neighbour that is not there or is deleted leaves the edge as open as one with no neighbour
                    Open(mk, ti, e, sd, p, q);
                    continue;
                }
                if (seen.Contains((nm, nt))) continue;
                if (PlanDist(meshes[nm], meshes[nm].Triangles[nt], at.X, at.Y) > reach) continue;
                seen.Add((nm, nt)); queue.Enqueue((nm, nt));
            }
        }

        MarkerBeyond? beyond = null;
        if (open is not null)
        {
            var g = meshes[open.Mesh]; var t = g.Triangles[open.Triangle];
            var (p, q) = EdgeEnds(g, t, open.Edge);
            var (a, b, c) = Corners(g, t);
            double cx = ((double)a.X + b.X + c.X) / 3, cy = ((double)a.Y + b.Y + c.Y) / 3;
            double nx = (double)q.Y - p.Y, ny = -((double)q.X - p.X), nl = Math.Sqrt(nx * nx + ny * ny);
            if (nl > 0)
            {
                nx /= nl; ny /= nl;
                if ((open.At.X - cx) * nx + (open.At.Y - cy) * ny < 0) { nx = -nx; ny = -ny; }
                beyond = new MarkerBeyond(null, -1, double.NaN, double.NaN);
                for (double d = BeyondStep; d <= BeyondReach + 1e-9; d += BeyondStep)
                {
                    double bx = open.At.X + d * nx, by = open.At.Y + d * ny;
                    var hit = Under(meshes, boxes, bx, by).OrderBy(u => Math.Abs(u.Z - open.At.Z)).Cast<(FormKey Mesh, int Tri, double Z)?>().FirstOrDefault();
                    if (hit is { } h) { beyond = new MarkerBeyond(h.Mesh, h.Tri, h.Z - open.At.Z, d - BeyondStep); break; }
                }
            }
        }

        var why = new List<string>();
        double above = at.Z - sz;
        if (Math.Abs(above) > maxAbove)
            why.Add($"The marker is not on the navmesh: it stands {Math.Abs(above):0} u {(above > 0 ? "above" : "below")} the nearest navmesh level under it ({sm} triangle {st}), more than {maxAbove:0} u; put it at the navmesh's own height.");
        if (open is not null && open.Distance < margin)
        {
            string past = beyond is null ? "" : beyond.Mesh is null ? $", with no navmesh within {BeyondReach:0} u beyond it"
                : $", with {beyond.Mesh} triangle {beyond.Triangle} {(beyond.Gap > 0 ? $"{beyond.Gap:0}-{beyond.Gap + BeyondStep:0} u beyond it" : "just beyond it")}, {Math.Abs(beyond.Step):0} u {(beyond.Step >= 0 ? "higher" : "lower")}";
            why.Add($"The marker is {open.Distance:0} u from an open edge of {open.Mesh} triangle {open.Triangle}{past}, inside the {margin:0} u margin; move it further in.");
        }
        foreach (var (mk, d) in unfollowed.Where(u => u.Value < margin).OrderBy(u => u.Value))
            why.Add($"The marker is {d:0} u from an edge link into {mk}, which was not loaded, so what lies across it is not known; name {mk} among the navmeshes.");
        foreach (var s in steps.Values.Where(s => Math.Abs(s.Step) > stepLimit).OrderBy(s => s.Distance))
            why.Add($"The marker is {s.Distance:0} u from an edge link of {s.Mesh} triangle {s.Triangle} to {s.ToMesh} triangle {s.ToTriangle} whose sides stand {Math.Abs(s.Step):0} u apart in height, over the {stepLimit:0} u step limit; move it away from that edge.");
        return new MarkerResult(why.Count == 0 ? "inside" : "refused", why, sm, st, sz, above, open, beyond,
                                steps.Values.OrderBy(s => s.Distance).ToList(),
                                unfollowed.OrderBy(u => u.Value).Select(u => (u.Key, u.Value)).ToList(), reach);
    }
}
