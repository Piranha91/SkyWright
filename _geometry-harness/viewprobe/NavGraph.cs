using System.Numerics;
using HousecarlCore;
using Mutagen.Bethesda.Plugins;

namespace ViewProbe;

/// <summary>
/// The winning navmesh as a WALKABLE GRAPH, and a path across it.
///
/// This exists because a straight line between two waypoints is the wrong question in a building.
/// --route's default — a leg is straight in plan and REFUSED if any sample leaves walkable ground —
/// is the right default and stays: it catches a path drawn through a wall, which is the failure that
/// matters, and it looks exactly as authoritative as a correct one. But it makes route AUTHORING a
/// puzzle. Placing waypoints so that every straight segment between them stays on the mesh means
/// solving the navigation problem by hand, one probe at a time, and that is what --route-solve is
/// for: hand it the two ENDS and it finds the walk.
///
/// EDGES COME FROM THE RECORD, NOT FROM THE SHAPES. Two triangles that share an edge on paper are
/// neighbours only if the navmesh says they are — the game reads NAVM's own link fields and nothing
/// else, so a graph derived from shared vertices would answer a question about geometry while
/// claiming to answer one about walking. Three sources, all of them the record's:
///
///   intra-mesh  NavTri.EdgeLink01/12/20 as a triangle index in the same mesh, when the matching
///               flag bit (0x01 / 0x02 / 0x04) is CLEAR;
///   cross-mesh  the same fields as an index into NAVM's EdgeLinks table when that bit is SET —
///               the table names another navmesh and a triangle in it;
///   doors       NAVM's DoorTriangles, which is how two navmeshes in DIFFERENT CELLS connect.
///
/// Doors are RECORDED and deliberately NOT traversed here: viewprobe loads one cell, so the far side
/// of a door is not in the graph at all, and a solver that stepped through one would be inventing
/// ground it has never seen. They are reported instead, so "no path" and "no path without leaving
/// this cell" are told apart.
///
/// This is also the structure the proposed reachability detector needs (see PENDING.md): same
/// edges, same three sources. It lives here so the pathfinder and that detector cannot end up with
/// two different opinions about what connects to what.
/// </summary>
static class NavGraph
{
    /// <summary>A triangle, addressed the way the graph addresses it: which navmesh, which index.</summary>
    internal readonly record struct TriRef(FormKey Mesh, int Index);

    /// <summary>One triangle's place in the graph: its corners, its centroid, and its neighbours
    /// with the EDGE each neighbour is reached across. The edge is not decoration — the funnel that
    /// pulls a path taut walks the portal edges, not the centroids.</summary>
    internal sealed class Node
    {
        public Vector3 A, B, C;
        public Vector3 Centroid;
        // Neighbour, plus the shared edge as THIS triangle states it (left/right in this triangle's
        // winding). Stored per-link rather than recomputed, because the record says which edge the
        // link crosses and re-deriving it from shared vertices would reintroduce exactly the
        // geometric guess this class exists to avoid.
        public List<(TriRef To, Vector3 EdgeL, Vector3 EdgeR)> Links = new();
    }

    /// <summary>What a build found, said out loud. A graph that silently came out with no edges
    /// looks identical to one that could not find a path, and this project has been bitten by that
    /// shape of error before: the counts are the build's own positive control.</summary>
    /// <param name="ExternalLinks">Cross-mesh links whose target navmesh this run never loaded — a
    /// CELL BOUNDARY, not a defect. Counted separately and reported because without it a run that
    /// read the edge-link table perfectly and found every target outside the cell is indistinguishable
    /// from one that never read the table at all: both print "0 cross-mesh". A zero needs a control.</param>
    internal sealed record BuildReport(
        int Triangles, int IntraLinks, int CrossLinks, int ExternalLinks, int DanglingLinks, int DoorTriangles,
        int Components, bool LinkTablesPresent,
        IReadOnlyList<(int Size, Vector3 Where)> ComponentSizes);

    static Vector3 V(Vec3 v) => new(v.X, v.Y, v.Z);

    /// <summary>Build the graph over the DRAWABLE triangles of the given navmeshes — the same
    /// deleted/out-of-range filter the render and --nav-at use, so the ground a path may cross and
    /// the ground the picture shows are the same ground.</summary>
    internal static (Dictionary<TriRef, Node> Nodes, BuildReport Report) Build(
        IReadOnlyDictionary<FormKey, NavmeshGeometry> navs)
    {
        var nodes = new Dictionary<TriRef, Node>();
        foreach (var nv in navs.Values)
            for (int ti = 0; ti < nv.Triangles.Count; ti++)
            {
                var tri = nv.Triangles[ti];
                if ((tri.Flags & 0x08) != 0) continue;
                if (tri.A < 0 || tri.B < 0 || tri.C < 0
                    || tri.A >= nv.Vertices.Count || tri.B >= nv.Vertices.Count || tri.C >= nv.Vertices.Count) continue;
                var a = V(nv.Vertices[tri.A]); var b = V(nv.Vertices[tri.B]); var c = V(nv.Vertices[tri.C]);
                nodes[new TriRef(nv.FormKey, ti)] = new Node
                {
                    A = a, B = b, C = c,
                    Centroid = (a + b + c) / 3f,
                };
            }

        int intra = 0, cross = 0, external = 0, dangling = 0, doors = 0;
        bool anyLinkTable = false;
        foreach (var nv in navs.Values)
        {
            var table = nv.EdgeLinks;
            if (table is { Count: > 0 }) anyLinkTable = true;
            doors += nv.DoorTriangles?.Count ?? 0;
            for (int ti = 0; ti < nv.Triangles.Count; ti++)
            {
                if (!nodes.TryGetValue(new TriRef(nv.FormKey, ti), out var node)) continue;
                var tri = nv.Triangles[ti];
                // Edge i of a triangle joins corner i to corner i+1, and the link field for that
                // edge is switched from "index in this mesh" to "index into the link table" by the
                // matching flag bit. Getting the pairing wrong wires the graph to plausible
                // nonsense, so the three are spelled out rather than looped over a packed array.
                var edges = new (short Link, int FlagBit, Vector3 L, Vector3 R)[]
                {
                    (tri.EdgeLink01, 0x01, node.A, node.B),
                    (tri.EdgeLink12, 0x02, node.B, node.C),
                    (tri.EdgeLink20, 0x04, node.C, node.A),
                };
                foreach (var (link, bit, l, r) in edges)
                {
                    if (link < 0) continue;                                   // no neighbour: a mesh boundary
                    TriRef to;
                    if ((tri.Flags & bit) != 0)
                    {
                        if (table is null || link >= table.Count) { dangling++; continue; }
                        var e = table[link];
                        // A cross-mesh link into a navmesh this run never loaded (another cell) is
                        // NOT a defect and NOT a step: it is a boundary, and counting it as dangling
                        // would turn every cell edge into a finding. Checked FIRST, so a boundary
                        // link with an unset index is reported as the boundary it is.
                        if (!navs.ContainsKey(e.Mesh)) { external++; continue; }
                        if (e.TriangleIndex < 0) { dangling++; continue; }
                        to = new TriRef(e.Mesh, e.TriangleIndex);
                        if (!nodes.ContainsKey(to)) { dangling++; continue; }
                        cross++;
                    }
                    else
                    {
                        to = new TriRef(nv.FormKey, link);
                        if (!nodes.ContainsKey(to)) { dangling++; continue; }
                        intra++;
                    }
                    node.Links.Add((to, l, r));
                }
            }
        }

        // Connected components, over the graph as WIRED — the number that says whether "no path"
        // means "these two rooms are separate" or "the whole mesh fell apart". Each one is measured
        // AND located: "2 components" is a fact a reader can do nothing with, while "a 3-triangle
        // island at 1200,1900" is a coordinate they can probe. Sorted biggest first, so the mainland
        // is entry zero and everything after it is an island.
        var comps = new List<(int Size, Vector3 Where)>();
        var seen = new HashSet<TriRef>();
        foreach (var key in nodes.Keys)
        {
            if (!seen.Add(key)) continue;
            var members = new List<TriRef> { key };
            var stack = new Stack<TriRef>();
            stack.Push(key);
            while (stack.Count > 0)
                foreach (var (to, _, _) in nodes[stack.Pop()].Links)
                    if (seen.Add(to)) { members.Add(to); stack.Push(to); }
            var mid = Vector3.Zero;
            foreach (var m in members) mid += nodes[m].Centroid;
            comps.Add((members.Count, mid / members.Count));
        }
        comps.Sort((x, y) => y.Size.CompareTo(x.Size));

        return (nodes, new BuildReport(nodes.Count, intra, cross, external, dangling, doors, comps.Count, anyLinkTable, comps));
    }

    /// <summary>The drawable triangle covering (x,y) whose height is nearest <paramref name="nearZ"/>,
    /// as a graph address. This is <see cref="BatEmit.NearestCoverZ"/>'s test — deliberately the same
    /// containment and the same nearest-storey rule — returning WHICH triangle rather than just how
    /// high it is, because a solve needs a node to start from and picking a different triangle than
    /// the waypoint's own z came from would start the walk on another floor.</summary>
    internal static (TriRef Tri, float Z)? TriangleAt(
        IReadOnlyDictionary<TriRef, Node> nodes, float x, float y, float nearZ)
    {
        (TriRef, float)? best = null;
        float bestDz = float.MaxValue;
        foreach (var (key, n) in nodes)
        {
            var a = n.A; var b = n.B; var c = n.C;
            float d = (b.Y - c.Y) * (a.X - c.X) + (c.X - b.X) * (a.Y - c.Y);
            if (MathF.Abs(d) < 1e-6f) continue;
            float u = ((b.Y - c.Y) * (x - c.X) + (c.X - b.X) * (y - c.Y)) / d;
            float v = ((c.Y - a.Y) * (x - c.X) + (a.X - c.X) * (y - c.Y)) / d;
            float w = 1f - u - v;
            if (u < 0f || v < 0f || w < 0f) continue;
            float z = u * a.Z + v * b.Z + w * c.Z;
            float dz = MathF.Abs(z - nearZ);
            if (dz < bestDz) { bestDz = dz; best = (key, z); }
        }
        return best;
    }

    /// <summary>A* from one triangle to another, over the graph's own links. Cost and heuristic are
    /// PLAN distance between centroids: the heuristic never overestimates (a walk cannot be shorter
    /// than its own projection), which is what keeps A* admissible, and using 3D distance would make
    /// a staircase look longer than the ground it leads to for no benefit — the taut length is
    /// measured afterwards, on the smoothed path, in 3D.
    /// Returns the triangle channel, start first, or null when the two are not connected.</summary>
    internal static List<TriRef>? FindChannel(IReadOnlyDictionary<TriRef, Node> nodes, TriRef start, TriRef goal)
    {
        if (!nodes.ContainsKey(start) || !nodes.ContainsKey(goal)) return null;
        if (start.Equals(goal)) return new List<TriRef> { start };

        static float Plan(Vector3 p, Vector3 q)
        {
            float dx = p.X - q.X, dy = p.Y - q.Y;
            return MathF.Sqrt(dx * dx + dy * dy);
        }

        var goalC = nodes[goal].Centroid;
        var g = new Dictionary<TriRef, float> { [start] = 0f };
        var came = new Dictionary<TriRef, TriRef>();
        var closed = new HashSet<TriRef>();
        var open = new PriorityQueue<TriRef, float>();
        open.Enqueue(start, Plan(nodes[start].Centroid, goalC));

        while (open.TryDequeue(out var cur, out _))
        {
            if (cur.Equals(goal))
            {
                var path = new List<TriRef> { cur };
                while (came.TryGetValue(cur, out var prev)) { cur = prev; path.Add(cur); }
                path.Reverse();
                return path;
            }
            if (!closed.Add(cur)) continue;
            var node = nodes[cur];
            foreach (var (to, _, _) in node.Links)
            {
                if (closed.Contains(to) || !nodes.TryGetValue(to, out var tn)) continue;
                float tentative = g[cur] + Plan(node.Centroid, tn.Centroid);
                if (g.TryGetValue(to, out var known) && tentative >= known) continue;
                g[to] = tentative;
                came[to] = cur;
                open.Enqueue(to, tentative + Plan(tn.Centroid, goalC));
            }
        }
        return null;
    }

    /// <summary>Pull a channel taut: the Simple Stupid Funnel Algorithm over the channel's portal
    /// edges, in PLAN.
    ///
    /// Without this a solved route is a chain of triangle centroids — it zigzags across every
    /// triangle it crosses, it draws badly, and worst of all it OVER-REPORTS the walk, which is one
    /// of the two things a solve is for ("how far would an actor actually have to go" is the number,
    /// and a zigzag answers a different question). The funnel is exact for a channel: the path it
    /// returns stays inside the union of the channel's triangles, so it cannot leave walkable ground
    /// — and the caller re-checks every sample against the mesh anyway, so a disagreement between
    /// the two is surfaced rather than trusted.
    ///
    /// Plan, not 3D, because the portals are vertical seams between floor triangles and the height
    /// is resolved from the mesh afterwards — the same rule the straight-leg path already follows,
    /// which is what lets a solved route ride a staircase instead of cutting its chord.</summary>
    internal static List<Vector2> Funnel(IReadOnlyDictionary<TriRef, Node> nodes, IReadOnlyList<TriRef> channel,
                                         Vector2 start, Vector2 end)
    {
        var portals = new List<(Vector2 L, Vector2 R)> { (start, start) };
        for (int i = 0; i + 1 < channel.Count; i++)
        {
            var node = nodes[channel[i]];
            var next = channel[i + 1];
            // Found EXPLICITLY, not via FirstOrDefault: its sentinel is a zero tuple, and a portal
            // edge genuinely at the origin would then read as "no link". A channel step with no
            // link is impossible by construction (A* only walks links); if it ever happened,
            // degrading to the next triangle's centroid is the honest reading of "no portal here".
            int li = node.Links.FindIndex(l => l.To.Equals(next));
            if (li < 0)
            {
                var c = nodes[next].Centroid;
                portals.Add((new Vector2(c.X, c.Y), new Vector2(c.X, c.Y)));
            }
            else
            {
                var link = node.Links[li];
                portals.Add((new Vector2(link.EdgeL.X, link.EdgeL.Y), new Vector2(link.EdgeR.X, link.EdgeR.Y)));
            }
        }
        portals.Add((end, end));

        // Winding is not guaranteed to be consistent across a navmesh, and the funnel needs the
        // portal's LEFT to actually be on the left of travel. Orient each portal against the
        // direction the channel is heading at that step; a portal handed to the funnel backwards
        // makes it cut the corner on the wrong side and leave the channel.
        for (int i = 1; i + 1 < portals.Count; i++)
        {
            var prev = i == 1 ? start : (portals[i - 1].L + portals[i - 1].R) * 0.5f;
            var mid = (portals[i].L + portals[i].R) * 0.5f;
            var dir = mid - prev;
            var span = portals[i].R - portals[i].L;
            if (Cross(dir, span) < 0f) portals[i] = (portals[i].R, portals[i].L);
        }

        static float Cross(Vector2 a, Vector2 b) => a.X * b.Y - a.Y * b.X;
        static float TriArea2(Vector2 a, Vector2 b, Vector2 c) => Cross(b - a, c - a);
        static bool Near(Vector2 a, Vector2 b) => Vector2.DistanceSquared(a, b) < 1e-6f;

        var result = new List<Vector2> { start };
        var apex = start; var left = start; var right = start;
        int apexI = 0, leftI = 0, rightI = 0;
        for (int i = 1; i < portals.Count; i++)
        {
            var (pl, pr) = portals[i];
            // Tighten the RIGHT side.
            if (TriArea2(apex, right, pr) <= 0f)
            {
                if (Near(apex, right) || TriArea2(apex, left, pr) > 0f)
                { right = pr; rightI = i; }
                else
                {
                    // The right side crossed the left: the left vertex is a corner of the taut path.
                    result.Add(left);
                    apex = left; apexI = leftI;
                    left = apex; right = apex; leftI = apexI; rightI = apexI;
                    i = apexI;
                    continue;
                }
            }
            // Tighten the LEFT side.
            if (TriArea2(apex, left, pl) >= 0f)
            {
                if (Near(apex, left) || TriArea2(apex, right, pl) < 0f)
                { left = pl; leftI = i; }
                else
                {
                    result.Add(right);
                    apex = right; apexI = rightI;
                    left = apex; right = apex; leftI = apexI; rightI = apexI;
                    i = apexI;
                    continue;
                }
            }
        }
        if (result.Count == 0 || !Near(result[^1], end)) result.Add(end);
        return result;
    }
}
