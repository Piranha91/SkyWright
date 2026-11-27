using System.Numerics;
using Mutagen.Bethesda.Plugins;

using HousecarlCore;

namespace SkyWright.Engine;

/// <summary>
/// The winning navmesh as a WALKABLE GRAPH, and a path across it.
///
/// Written for viewprobe's <c>--route-solve</c> (2026-08-30) and moved here 2026-09-02 for the
/// route-cut rule (roadmap 4.7): the corridor's natural path line is this pathfinder's answer, and
/// the measurement that asks for it (<see cref="RouteConnectivity"/>) lives in this library, which
/// cannot see the harness's <c>_shared</c> folder. One pathfinder, referenced by every consumer —
/// the renderer and the detector cannot end up with two opinions about what connects to what.
///
/// This exists because a straight line between two waypoints is the wrong question in a building.
/// A leg drawn straight in plan and refused if any sample leaves walkable ground is the right
/// default for a planned route: it catches a path drawn through a wall. But it makes route
/// AUTHORING a puzzle — placing waypoints so that every straight segment stays on the mesh means
/// solving the navigation problem by hand — and it is not what an actor does. Hand this the two
/// ENDS and it finds the walk.
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
/// Doors are RECORDED and deliberately NOT traversed here: a caller loads one cell, so the far side
/// of a door is not in the graph at all, and a solver that stepped through one would be inventing
/// ground it has never seen. They are reported instead, so "no path" and "no path without leaving
/// this cell" are told apart.
///
/// CROSS-MESH LINKS HAVE A TYPE, and only a Portal is level ground. Each EdgeLinks entry carries xEdit's
/// edge-link Type — 0 Portal, 1 Ledge Up, 2 Ledge Down, 3 Enable/Disable Portal. MUTAGEN LAGS XEDIT HERE:
/// Mutagen 0.54.4 names the field <c>EdgeLink.Unknown</c> (<see cref="NavEdgeLink.Type"/>). In the base game's own
/// records a Ledge Up rises and is answered by a Ledge Down on the same triangle pair; Type 3 appears only in mod
/// plugins, across height gaps no walker steps. Walking them as Portals let a path
/// climb a ledge for the price of a level step and merged components across it (fixed 2026-11-08). Which types
/// are walked is <see cref="LinkPolicy"/>. The default walks Ledge Down and holds Ledge Up, from an in-game test
/// (2026-11-08, a commanded walker on two vanilla ledges in each direction): going up, the pathfinder took the long
/// way round every time although the ledge was the short way; going down, it walked off the ledge edge and dropped
/// every time, through the drop's own animation, with no warp. Enable/Disable Portal links stay held: no in-game
/// evidence yet. Held links are recorded and counted, like doors.
/// </summary>
public static class NavGraph
{
    /// <summary>A triangle, addressed the way the graph addresses it: which navmesh, which index.</summary>
    public readonly record struct TriRef(FormKey Mesh, int Index);

    /// <summary>One triangle's place in the graph: its corners, its centroid, and its neighbours
    /// with the EDGE each neighbour is reached across. The edge is not decoration — the funnel that
    /// pulls a path taut walks the portal edges, not the centroids.</summary>
    public sealed class Node
    {
        public Vector3 A, B, C;
        public Vector3 Centroid;
        // Neighbour, plus the shared edge as THIS triangle states it (left/right in this triangle's
        // winding). Stored per-link rather than recomputed, because the record says which edge the
        // link crosses and re-deriving it from shared vertices would reintroduce exactly the
        // geometric guess this class exists to avoid.
        public List<(TriRef To, Vector3 EdgeL, Vector3 EdgeR)> Links = new();
        // The record's type of each walked edge-link-table link, by target (Portal, Ledge Down, ...): a path over a ledge
        // link crosses the plan gap between the two triangles in the air.
        public Dictionary<TriRef, int> LinkTypes = new();
    }

    /// <summary>What a build found, said out loud. A graph that silently came out with no edges
    /// looks identical to one that could not find a path, and this project has been bitten by that
    /// shape of error before: the counts are the build's own positive control.</summary>
    /// <param name="ExternalLinks">Cross-mesh links whose target navmesh this run never loaded — a
    /// CELL BOUNDARY, not a defect. Counted separately and reported because without it a run that
    /// read the edge-link table perfectly and found every target outside the cell is indistinguishable
    /// from one that never read the table at all: both print "0 cross-mesh". A zero needs a control.</param>
    /// <param name="CrossLinks">Cross-mesh links WIRED into the graph (traversed under the policy).</param>
    /// <param name="LedgeUpLinks">Cross-mesh links into a loaded triangle, by type, wired or held. Portals are
    /// <c>CrossLinks</c> minus the wired ledge and toggle links.</param>
    /// <param name="UnknownTypeLinks">Links whose type is none of 0-3: never walked, counted so a reader sees them.</param>
    /// <param name="HeldLinks">Of the typed links into a loaded triangle, those the policy does not traverse.</param>
    public sealed record BuildReport(
        int Triangles, int IntraLinks, int CrossLinks, int ExternalLinks, int DanglingLinks, int DoorTriangles,
        int Components, bool LinkTablesPresent,
        IReadOnlyList<(int Size, Vector3 Where)> ComponentSizes,
        int LedgeUpLinks = 0, int LedgeDownLinks = 0, int EnableDisableLinks = 0, int UnknownTypeLinks = 0,
        int HeldLinks = 0, LinkPolicy? Policy = null);

    /// <summary>xEdit's NAVM edge-link Type (Mutagen 0.54.4: <c>EdgeLink.Unknown</c>).</summary>
    public enum EdgeLinkType { Portal = 0, LedgeUp = 1, LedgeDown = 2, EnableDisablePortal = 3 }

    /// <summary>Which non-Portal cross-mesh links the graph walks. A link is a directed edge from the triangle
    /// whose record carries it, so a one-way policy gives a one-way step.</summary>
    public sealed record LinkPolicy(bool LedgeUp, bool LedgeDown, bool EnableDisablePortal)
    {
        /// <summary>What a walking actor was seen to do in game: it drops down a ledge but never climbs one; Type 3 is
        /// held until it is tested.</summary>
        public static readonly LinkPolicy Default = new(LedgeUp: false, LedgeDown: true, EnableDisablePortal: false);
        /// <summary>Every typed link walked: the graph as it was before 2026-11-08.</summary>
        public static readonly LinkPolicy AllLinks = new(true, true, true);
    }

    /// <summary>Whether <paramref name="policy"/> walks a link of raw <paramref name="type"/>. Portal always;
    /// a type outside 0-3 never.</summary>
    public static bool Traverses(LinkPolicy policy, int type) => type switch
    {
        (int)EdgeLinkType.Portal => true,
        (int)EdgeLinkType.LedgeUp => policy.LedgeUp,
        (int)EdgeLinkType.LedgeDown => policy.LedgeDown,
        (int)EdgeLinkType.EnableDisablePortal => policy.EnableDisablePortal,
        _ => false,
    };

    static Vector3 V(Vec3 v) => new(v.X, v.Y, v.Z);

    /// <summary>Build over the navmeshes a <see cref="NavmeshIndex"/> holds — the same set the route raster
    /// reads its ground from, so the line and the ground are measured on one mesh set.</summary>
    public static (Dictionary<TriRef, Node> Nodes, BuildReport Report) Build(NavmeshIndex index, LinkPolicy? policy = null)
    {
        var navs = new Dictionary<FormKey, NavmeshGeometry>();
        for (int i = 0; i < index.NavmeshCount; i++)
        {
            var nv = index.NavAt(i);
            // Two navmeshes under one key cannot both be addressed; the later one is dropped and the
            // caller's report shows fewer triangles than the index — a synthetic-order smell, never a
            // silent merge.
            navs.TryAdd(nv.FormKey, nv);
        }
        return Build(navs, policy);
    }

    /// <summary>Build the graph over the DRAWABLE triangles of the given navmeshes — the same
    /// deleted/out-of-range filter the render and --nav-at use, so the ground a path may cross and
    /// the ground the picture shows are the same ground.</summary>
    public static (Dictionary<TriRef, Node> Nodes, BuildReport Report) Build(
        IReadOnlyDictionary<FormKey, NavmeshGeometry> navs, LinkPolicy? policy = null)
    {
        policy ??= LinkPolicy.Default;
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
        int ledgeUp = 0, ledgeDown = 0, toggle = 0, unknownType = 0, held = 0;
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
                        switch (e.Type)
                        {
                            case (int)EdgeLinkType.Portal: break;
                            case (int)EdgeLinkType.LedgeUp: ledgeUp++; break;
                            case (int)EdgeLinkType.LedgeDown: ledgeDown++; break;
                            case (int)EdgeLinkType.EnableDisablePortal: toggle++; break;
                            default: unknownType++; break;
                        }
                        // A ledge is not level ground: recorded above, walked only if the policy says so.
                        if (!Traverses(policy, e.Type)) { held++; continue; }
                        cross++;
                        node.LinkTypes.TryAdd(to, e.Type);
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
        // Counted over the links taken in EITHER direction: a walked Ledge Down is a one-way step, and a search that
        // followed links one way only would split or join pieces depending on which triangle it started from. A
        // ledge you can drop from but not climb therefore joins two pieces here, as the route stations' own count does.
        var undirected = new Dictionary<TriRef, List<TriRef>>(nodes.Count);
        foreach (var key in nodes.Keys) undirected[key] = new List<TriRef>();
        foreach (var (key, node) in nodes)
            foreach (var (to, _, _) in node.Links) { undirected[key].Add(to); undirected[to].Add(key); }
        var comps = new List<(int Size, Vector3 Where)>();
        var seen = new HashSet<TriRef>();
        foreach (var key in nodes.Keys)
        {
            if (!seen.Add(key)) continue;
            var members = new List<TriRef> { key };
            var stack = new Stack<TriRef>();
            stack.Push(key);
            while (stack.Count > 0)
                foreach (var to in undirected[stack.Pop()])
                    if (seen.Add(to)) { members.Add(to); stack.Push(to); }
            var mid = Vector3.Zero;
            foreach (var m in members) mid += nodes[m].Centroid;
            comps.Add((members.Count, mid / members.Count));
        }
        comps.Sort((x, y) => y.Size.CompareTo(x.Size));

        return (nodes, new BuildReport(nodes.Count, intra, cross, external, dangling, doors, comps.Count, anyLinkTable, comps,
                                       ledgeUp, ledgeDown, toggle, unknownType, held, policy));
    }

    /// <summary>The drawable triangle covering (x,y) whose height is nearest <paramref name="nearZ"/>,
    /// as a graph address. The same containment and the same nearest-storey rule as viewprobe's
    /// waypoint-to-floor test, returning WHICH triangle rather than just how high it is, because a
    /// solve needs a node to start from and picking a different triangle than the waypoint's own z
    /// came from would start the walk on another floor. <paramref name="edgeTolerance"/> lets a point
    /// sitting a hair outside every triangle (a raster cell centre on a mesh edge) still resolve.</summary>
    public static (TriRef Tri, float Z)? TriangleAt(
        IReadOnlyDictionary<TriRef, Node> nodes, float x, float y, float nearZ, float edgeTolerance = 0f)
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
            if (u < -edgeTolerance || v < -edgeTolerance || w < -edgeTolerance) continue;
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
    /// Returns the triangle channel, start first, or null when the two are not connected (or, with
    /// <paramref name="avoid"/>, not connected without entering one of those triangles).</summary>
    public static List<TriRef>? FindChannel(IReadOnlyDictionary<TriRef, Node> nodes, TriRef start, TriRef goal,
                                            IReadOnlySet<TriRef>? avoid = null)
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
                if (closed.Contains(to) || (avoid is not null && avoid.Contains(to)) || !nodes.TryGetValue(to, out var tn)) continue;
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
    public static List<Vector2> Funnel(IReadOnlyDictionary<TriRef, Node> nodes, IReadOnlyList<TriRef> channel,
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
        static bool OnSegment(Vector2 p, Vector2 a, Vector2 b)
        {
            var ab = b - a; float len2 = ab.LengthSquared();
            if (len2 < 1e-6f) return false;
            float t = Vector2.Dot(p - a, ab) / len2;
            return t > 1e-4f && t < 1f - 1e-4f && MathF.Abs(Cross(ab, p - a)) <= 1e-3f * MathF.Sqrt(len2);
        }

        var result = new List<Vector2> { start };
        var apex = start; var left = start; var right = start;
        int apexI = 0, leftI = 0, rightI = 0;
        for (int i = 1; i < portals.Count; i++)
        {
            var (pl, pr) = portals[i];
            // A portal that passes through the apex itself (a waypoint on the edge of its own triangle, or a corner
            // the next portals share) constrains nothing: the path crosses it at the apex. Tightening against it
            // compares the apex with a line through the apex, the funnel opens to a half-plane, and the path bent
            // on the mesh's outer edge (fixed 2026-11-15).
            if (Near(apex, left) && Near(apex, right) && OnSegment(apex, pl, pr)) continue;
            // Tighten the RIGHT side.
            if (TriArea2(apex, right, pr) <= 0f)
            {
                // A side shrunk onto the apex has no direction, so nothing crosses it: testing against it read
                // "crossed" and re-added the apex as a second, identical corner (fixed 2026-11-15).
                if (Near(apex, right) || Near(apex, left) || TriArea2(apex, left, pr) > 0f)
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
                if (Near(apex, left) || Near(apex, right) || TriArea2(apex, right, pl) < 0f)
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

    /// <summary>The taut path between two triangles, with the channel it walks; <see cref="Reroutes"/> of them were
    /// re-routed round an interior vertex and <see cref="OtherSides"/> past a hole on its other side, saving
    /// <see cref="Saved"/> u of plan length in all.</summary>
    public sealed record Taut(List<TriRef> Channel, List<Vector2> Corners, int Reroutes, float Saved, int OtherSides = 0);

    /// <summary>The taut path from <paramref name="start"/> in triangle <paramref name="from"/> to <paramref name="end"/> in
    /// <paramref name="to"/>: A*, the funnel, then the channel straightened; null when the two are not connected.
    ///
    /// A shortest path bends only at the mesh boundary. A* over centroid distances can pick a channel that passes a vertex
    /// on the wrong side (on a 64 u grid two channels round a notch tie exactly), and the funnel, exact within its channel,
    /// then bends at a vertex the mesh surrounds on every side. Such a bend proves the channel slack, so that stretch of the
    /// channel is re-routed round the other side of the vertex's ring of triangles and the funnel run again. A re-route is
    /// kept only if the path gets strictly shorter, repeats no triangle and walks every link in its own direction (a one-way
    /// ledge is never walked backwards).
    ///
    /// A bend at a vertex on the mesh boundary is where the path passes a hole (or the mesh's edge), and A* chose that side
    /// by centroid costs: two sides can be close in centroid cost but not in taut length, so the other side is tried too.
    /// A* is run again with every triangle round that vertex forbidden (<see cref="PastOtherSide"/>), the funnel run on what
    /// it finds, and that channel kept on the same terms: only if the path gets strictly shorter.
    /// <paramref name="tryOtherSides"/> false leaves that out (a test's sabotage arm).</summary>
    public static Taut? TautPath(IReadOnlyDictionary<TriRef, Node> nodes, TriRef from, TriRef to, Vector2 start, Vector2 end,
                                 bool tryOtherSides = true)
    {
        var channel = FindChannel(nodes, from, to);
        if (channel is null) return null;
        var corners = Funnel(nodes, channel, start, end);
        float len = PlanLength(corners);
        int reroutes = 0, otherSides = 0; float saved = 0f;
        int budget = 2 * channel.Count + 4;
        // the boundary vertices whose other side was tried on the current channel and found no shorter (reset when it changes)
        var noShorter = new HashSet<Vector2>();
        bool improved = true;
        while (improved && budget-- > 0)
        {
            improved = false;
            for (int ci = 1; ci + 1 < corners.Count && !improved; ci++)
            {
                foreach (var alt in AroundOtherSide(nodes, channel, corners[ci]))
                {
                    var c2 = Funnel(nodes, alt, start, end);
                    float l2 = PlanLength(c2);
                    if (l2 >= len - 1e-3f) continue;
                    saved += len - l2; len = l2; channel = alt; corners = c2; reroutes++; improved = true;
                    break;
                }
                if (improved) { noShorter.Clear(); continue; }
                if (!tryOtherSides || noShorter.Contains(corners[ci])) continue;
                if (PastOtherSide(nodes, channel, corners[ci], from, to) is { } past)
                {
                    var c2 = Funnel(nodes, past, start, end);
                    float l2 = PlanLength(c2);
                    if (l2 < len - 1e-3f)
                    {
                        saved += len - l2; len = l2; channel = past; corners = c2; otherSides++; improved = true;
                        noShorter.Clear();
                        continue;
                    }
                }
                noShorter.Add(corners[ci]);
            }
        }
        return new Taut(channel, corners, reroutes, saved, otherSides);
    }

    static float PlanLength(IReadOnlyList<Vector2> cs)
    {
        float l = 0f;
        for (int i = 0; i + 1 < cs.Count; i++) l += Vector2.Distance(cs[i], cs[i + 1]);
        return l;
    }

    /// <summary>The channel A* finds when it may not touch the vertex at plan point <paramref name="v"/>: every triangle
    /// meeting that vertex on the storey the channel passes it (reached from the channel's own triangles there, across links
    /// to triangles with the same corner) is forbidden, except the two ends. For a vertex on a hole's edge that is the way
    /// round the hole's other side, which centroid costs can rank behind the longer one. Null when nothing is forbidden or no
    /// such channel exists.</summary>
    static List<TriRef>? PastOtherSide(IReadOnlyDictionary<TriRef, Node> nodes, List<TriRef> channel, Vector2 v, TriRef from, TriRef to)
    {
        static Vector3? CornerAt(Node n, Vector2 v)
        {
            static bool At(Vector3 p, Vector2 v) => MathF.Abs(p.X - v.X) < 1e-3f && MathF.Abs(p.Y - v.Y) < 1e-3f;
            return At(n.A, v) ? n.A : At(n.B, v) ? n.B : At(n.C, v) ? n.C : null;
        }
        var fan = new HashSet<TriRef>();
        var queue = new Queue<(TriRef T, float Z)>();
        foreach (var t in channel)
            if (nodes.TryGetValue(t, out var n) && CornerAt(n, v) is { } p && fan.Add(t)) queue.Enqueue((t, p.Z));
        while (queue.Count > 0)
        {
            var (t, z) = queue.Dequeue();
            foreach (var (nb, _, _) in nodes[t].Links)
                if (!fan.Contains(nb) && nodes.TryGetValue(nb, out var nn) && CornerAt(nn, v) is { } q && MathF.Abs(q.Z - z) < 1e-3f)
                { fan.Add(nb); queue.Enqueue((nb, q.Z)); }
        }
        fan.Remove(from); fan.Remove(to);
        return fan.Count == 0 ? null : FindChannel(nodes, from, to, fan);
    }

    /// <summary>The channels that pass the vertex at plan point <paramref name="v"/> on its other side: for each run of
    /// consecutive channel triangles meeting at that vertex, whose ring of triangles closes round it on one storey, the
    /// channel with that run replaced by the ring's other arc. None when the vertex is on the mesh boundary.</summary>
    static IEnumerable<List<TriRef>> AroundOtherSide(IReadOnlyDictionary<TriRef, Node> nodes, List<TriRef> channel, Vector2 v)
    {
        static bool At(Vector3 p, Vector2 v) => MathF.Abs(p.X - v.X) < 1e-3f && MathF.Abs(p.Y - v.Y) < 1e-3f;
        Vector3? Corner(TriRef t) => nodes.TryGetValue(t, out var n) ? (At(n.A, v) ? n.A : At(n.B, v) ? n.B : At(n.C, v) ? n.C : null) : null;
        for (int p = 0; p < channel.Count; p++)
        {
            if (Corner(channel[p]) is not { } vz) continue;
            int q = p;
            while (q + 1 < channel.Count && Corner(channel[q + 1]) is { } z1 && MathF.Abs(z1.Z - vz.Z) < 1e-3f) q++;
            int runEnd = q;
            if (q > p && Ring(nodes, channel[p], vz) is { } ring)
            {
                int m = ring.Count;
                // The run must be an arc of the ring, walked one way; the other way round from channel[p] to channel[q]
                // is the other side.
                int dir = m > 1 && ring[1].Equals(channel[p + 1]) ? 1 : m > 1 && ring[m - 1].Equals(channel[p + 1]) ? -1 : 0;
                bool arc = dir != 0;
                for (int k = 0; arc && k <= q - p; k++) arc = ring[((dir * k) % m + m) % m].Equals(channel[p + k]);
                if (arc)
                {
                    var other = new List<TriRef>();
                    for (int k = 0; ; k--)
                    {
                        var t = ring[((dir * k) % m + m) % m];
                        other.Add(t);
                        if (t.Equals(channel[q]) || other.Count > m) break;
                    }
                    var alt = new List<TriRef>(channel.Count + m);
                    alt.AddRange(channel.Take(p)); alt.AddRange(other); alt.AddRange(channel.Skip(q + 1));
                    bool ok = other[^1].Equals(channel[q]) && alt.Distinct().Count() == alt.Count;
                    for (int k = 0; ok && k + 1 < alt.Count; k++)
                        ok = nodes[alt[k]].Links.Any(l => l.To.Equals(alt[k + 1]));
                    if (ok) yield return alt;
                }
            }
            p = runEnd;
        }
    }

    /// <summary>The triangles round the vertex <paramref name="v"/> of <paramref name="t"/>, in order, walked across the
    /// links on the edges that meet at it; null when the walk reaches an edge with no link back (the mesh boundary) or
    /// another storey, so the ring does not close.</summary>
    static List<TriRef>? Ring(IReadOnlyDictionary<TriRef, Node> nodes, TriRef t, Vector3 v)
    {
        var ring = new List<TriRef> { t };
        TriRef? came = null; var cur = t;
        for (int guard = 0; guard < 64; guard++)
        {
            var n = nodes[cur];
            TriRef? next = null;
            foreach (var (to, l, r) in n.Links)
            {
                if (!(l == v || r == v) || (came is { } c && to.Equals(c))) continue;
                // the neighbour must meet the same vertex, on the same storey, and link back across the same edge
                if (!nodes.TryGetValue(to, out var tn) || !(tn.A == v || tn.B == v || tn.C == v)) continue;
                if (!tn.Links.Any(b => b.To.Equals(cur))) continue;
                next = to; break;
            }
            if (next is not { } nx) return null;
            if (nx.Equals(t)) return ring.Count >= 3 ? ring : null;
            if (ring.Contains(nx)) return null;
            ring.Add(nx); came = cur; cur = nx;
        }
        return null;
    }

    /// <summary>Which triangle of a solved <paramref name="channel"/> the point (x,y) on its taut path lies in, searched
    /// forward from index <paramref name="from"/>, and that triangle's height there; null when it lies in none of them from
    /// there on. A path's storey is the triangle it is in: the channel is the sequence an actor walks, so its triangles,
    /// not a chain of nearby heights, say which floor a sample is on.</summary>
    public static (int Index, float Z)? ChannelTriangleAt(IReadOnlyDictionary<TriRef, Node> nodes, IReadOnlyList<TriRef> channel,
                                                          float x, float y, int from, float edgeTolerance = 1e-3f)
    {
        for (int i = Math.Max(0, from); i < channel.Count; i++)
        {
            if (!nodes.TryGetValue(channel[i], out var n)) continue;
            var a = n.A; var b = n.B; var c = n.C;
            float d = (b.Y - c.Y) * (a.X - c.X) + (c.X - b.X) * (a.Y - c.Y);
            if (MathF.Abs(d) < 1e-6f) continue;
            float u = ((b.Y - c.Y) * (x - c.X) + (c.X - b.X) * (y - c.Y)) / d;
            float v = ((c.Y - a.Y) * (x - c.X) + (a.X - c.X) * (y - c.Y)) / d;
            float w = 1f - u - v;
            if (u < -edgeTolerance || v < -edgeTolerance || w < -edgeTolerance) continue;
            return (i, u * a.Z + v * b.Z + w * c.Z);
        }
        return null;
    }

    /// <summary>Whether the channel's step out of <c>channel[i]</c> crosses a walked ledge link (Ledge Up or Ledge Down).
    /// In game the two triangles of a ledge do not meet in plan (17-70 u apart measured), and an actor crosses the gap in
    /// the air, so ground between them is not a path leaving the mesh.</summary>
    public static bool LedgeStep(IReadOnlyDictionary<TriRef, Node> nodes, IReadOnlyList<TriRef> channel, int i)
        => i >= 0 && i + 1 < channel.Count && nodes.TryGetValue(channel[i], out var n)
           && n.LinkTypes.TryGetValue(channel[i + 1], out int t)
           && (t == (int)EdgeLinkType.LedgeDown || t == (int)EdgeLinkType.LedgeUp);

    /// <summary>How far each interior corner of a taut path is nudged off the mesh boundary.</summary>
    public const float CornerInsetDistance = 6f;

    /// <summary>The corner inset, applied IN PLACE to a funnel's corners. Moved here from viewprobe's
    /// <c>--route-solve</c> on 2026-09-25, unchanged, so the route drawing and the station rule's route
    /// sampler share one inset.
    ///
    /// A taut path HUGS THE WALL — that is what taut means — so its interior corners land exactly ON
    /// the mesh boundary, and a segment between two of them runs ALONG it. At that precision "is this
    /// point on navmesh" is decided by float rounding: the first leg tried grazed the boundary by
    /// 0.13 u and the off-mesh check, correctly by its own rule, called the path off the mesh.
    ///
    /// So each interior corner is nudged <see cref="CornerInsetDistance"/> off the boundary, along the
    /// bisector of the two path directions leaving it. Consecutive corners often lie along ONE boundary
    /// edge, and there the bisector is near-perpendicular to the path and points AT the wall, so BOTH
    /// directions are offered and the MESH decides which is free (measured: the bisector was the wrong
    /// way at both corners of the leg that grazed).
    ///
    /// VERIFIED, never assumed: a moved corner is kept only if it is itself on walkable ground at the
    /// storey the path is on (<paramref name="nearestCoverZ"/> within <paramref name="sameFloorZ"/> of
    /// the reference height, which starts at <paramref name="startZ"/>). Given the path's <paramref name="channel"/>, a
    /// corner's reference is the height of the channel triangle it lies in, so a corner after a drop is judged on the floor
    /// it is on; a corner in none of them keeps the chained reference. If neither side is walkable the original corner
    /// stands. The endpoints are never moved.</summary>
    public static void InsetCorners(List<Vector2> corners, float startZ,
                                    Func<float, float, float, float?> nearestCoverZ, float sameFloorZ,
                                    IReadOnlyDictionary<TriRef, Node>? nodes = null, IReadOnlyList<TriRef>? channel = null)
    {
        float insetRef = startZ;
        int cursor = 0;
        for (int ci = 1; ci + 1 < corners.Count; ci++)
        {
            var cv = corners[ci];
            if (nodes is not null && channel is not null && ChannelTriangleAt(nodes, channel, cv.X, cv.Y, cursor) is { } hit)
            { cursor = hit.Index; insetRef = hit.Z; }
            else if (nearestCoverZ(cv.X, cv.Y, insetRef) is { } cz
                && MathF.Abs(cz - insetRef) <= sameFloorZ) insetRef = cz;
            var d0 = corners[ci - 1] - cv; var d1 = corners[ci + 1] - cv;
            if (d0.LengthSquared() < 1e-6f || d1.LengthSquared() < 1e-6f) continue;
            var bis = Vector2.Normalize(d0) + Vector2.Normalize(d1);
            if (bis.LengthSquared() < 1e-6f) continue;      // collinear: no corner to stand off from
            var step = Vector2.Normalize(bis) * CornerInsetDistance;
            foreach (var cand in new[] { cv + step, cv - step })
                if (nearestCoverZ(cand.X, cand.Y, insetRef) is { } mz
                    && MathF.Abs(mz - insetRef) <= sameFloorZ)
                { corners[ci] = cand; break; }
        }
    }
}
