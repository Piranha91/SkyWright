using System.Numerics;
using System.Security.Cryptography;
using System.Text;
using HousecarlCore;
using Mutagen.Bethesda.Plugins;

namespace HarnessShared;

// ======================================================================
//  RouteStations - the station rule's ROUTE mode: player camera stations
//  sampled from the walkable navmesh around what one plugin changes in a
//  worldspace, and the coverage of that ground.
//
//  The rule is g5-route-sampling/1 (a sealed run record) with its
//  AMENDMENT-01 readings. In outline:
//    Classify  X_W, the changed content, as plan points (§2); the area
//              squares, the loaded set and the navmesh graph (§3.1);
//              walkable = a node triangle in an undirected component that
//              holds a door triangle or an exit (§3.2); A_W = walkable
//              ground clipped EXACTLY to the union of R_area discs, or
//              whole on the changed plugin's own navmesh (§3.3).
//    Sample    route candidates along solved door-to-door and door-to-exit
//              paths (§4.1), then seeded fill candidates on sub-triangles
//              of the ground still farther than S from a station, with a
//              seeded retry (§4.2); each tested by Test; the gaze set per
//              accepted station (§6).
//    Test      StationRule's own ground and water checks (CheckGround,
//              CheckWater - the code the face-on and observing composers
//              run), plus Unreachable, OutsideArea, the child-worldspace
//              footprint, TightClearance (NavCover.Clearance, the function
//              --route uses) and an eye inside an occlusion primitive, in
//              one precedence (§5).
//    Coverage  the area of A_W within D of a station set, exactly (§7).
//
//  REUSED, NOT COPIED: the graph, A*, the funnel and the corner inset are
//  NavGraph's; the navmesh cover and clearance are NavCover's; the ground
//  and water checks are StationRule's. What is new here is the area, the
//  sampler, the gaze and the coverage.
//
//  THE AREA GEOMETRY IS EXACT. The part of a triangle inside a union of
//  discs (and, for coverage, inside a second union as well) is measured by
//  Green's theorem over the region's boundary: the pieces of the triangle's
//  edges inside the unions, and the arcs of each circle that lie on its own
//  union's boundary and inside the triangle and the other union. Both
//  reduce to interval arithmetic - parameter intervals on a segment, angle
//  intervals on a circle - in double precision. No raster.
// ======================================================================

/// <summary>Which composer produced a route-mode candidate.</summary>
public enum RouteFamily : byte { Route, Fill, FillRetry }

/// <summary>Why a composed candidate was dropped without being tested. Not a refusal: a refusal
/// is a <see cref="StationRejection"/> decided by testing the candidate.</summary>
public enum RouteDrop : byte { None, Deduplicated, SkippedNearAccepted, SkippedNotInArea }

/// <summary>Which gaze of a route-mode station.</summary>
public enum RouteGazeKind : byte { Fwd, Back, Content }

/// <summary>One plugin's version of a placed record: its position (null when the version carries
/// no placement, as a deleted one typically does) and its raw record-header flag word.</summary>
public sealed record RoutePlacedVersion(string Plugin, Vector3? Position, int HeaderFlags);

/// <summary>A placed record the changed plugin carries in the worldspace, with every version in
/// the arm, in load order.</summary>
public sealed record RoutePlaced(FormKey FormKey, FormKey? Base, bool BaseHasModel,
                                 IReadOnlyList<RoutePlacedVersion> Versions);

/// <summary>One WINNING navmesh of the worldspace: its geometry, the plugins carrying it in load
/// order (winner last) and the grid square of the exterior cell the winner is filed under.</summary>
public sealed record RouteNavmesh(NavmeshGeometry Mesh, IReadOnlyList<string> Chain, (int X, int Y)? Square);

/// <summary>Everything the route mode reads about one worldspace, raw: nothing here is classified.</summary>
public sealed record RouteWorld
{
    public required FormKey Worldspace { get; init; }
    public required string WorldspaceEditorId { get; init; }
    public required IReadOnlyList<string> ArmPlugins { get; init; }
    /// <summary>The plugin whose changes the area is built around.</summary>
    public required string CityPlugin { get; init; }
    /// <summary>Every winning navmesh of the worldspace the caller holds (at least the loaded set).</summary>
    public required IReadOnlyList<RouteNavmesh> Navmeshes { get; init; }
    /// <summary>Every placed record the city plugin carries in the worldspace.</summary>
    public required IReadOnlyList<RoutePlaced> CityPlaced { get; init; }
    /// <summary>The navmesh records the city plugin defines or overrides in the worldspace.</summary>
    public required IReadOnlyCollection<FormKey> CityNavmeshes { get; init; }
    /// <summary>Winning, undeleted occlusion primitives (PortalBox or Box) of the worldspace.</summary>
    public IReadOnlyList<PrimitiveGeometry> Primitives { get; init; } = Array.Empty<PrimitiveGeometry>();
    /// <summary>The winning navmeshes of a CHILD worldspace whose footprint refuses a station of
    /// this one (<see cref="StationRejection.InChildWorldspaceFootprint"/>); null when none applies.</summary>
    public IReadOnlyList<NavmeshGeometry>? ChildFootprint { get; init; }
}

/// <summary>One member of the changed-content set X_W: a plan point, tagged with its record and why.</summary>
public readonly record struct RouteXPoint(Vector2 P, FormKey Source, bool HasModel, string Why);

/// <summary>What the rule decided about one triangle of a loaded navmesh.</summary>
public sealed record RouteTriangle(
    NavGraph.TriRef Tri, bool Node, bool Walkable, int Component, bool Reachable,
    bool Door, bool Exit, bool CityOwn, int Flags, Vector2 A, Vector2 B, Vector2 C,
    double AreaFull, double AreaClipped)
{
    public bool InArea => Walkable && AreaClipped > 0.0;
}

/// <summary>The classified area of one worldspace: X_W, the squares, the loaded set, the graph and
/// every loaded triangle's verdict.</summary>
public sealed class RouteArea
{
    public required RouteWorld World { get; init; }
    public required IReadOnlyList<RouteXPoint> XPoints { get; init; }
    public required IReadOnlySet<(int X, int Y)> AreaSquares { get; init; }
    public required IReadOnlySet<(int X, int Y)> LoadedSquares { get; init; }
    /// <summary>The loaded set: winning navmeshes in the area squares plus a one-square ring.</summary>
    public required IReadOnlyDictionary<FormKey, NavmeshGeometry> Loaded { get; init; }
    /// <summary>Every winning navmesh of the worldspace the caller supplied, loaded or not.</summary>
    public required IReadOnlyDictionary<FormKey, NavmeshGeometry> Winning { get; init; }
    public required IReadOnlyDictionary<NavGraph.TriRef, NavGraph.Node> Nodes { get; init; }
    public required NavGraph.BuildReport Report { get; init; }
    /// <summary>Every triangle of every loaded navmesh.</summary>
    public required IReadOnlyDictionary<NavGraph.TriRef, RouteTriangle> Triangles { get; init; }
    public int NavmeshesWithoutSquare { get; init; }
    public int Components { get; init; }
    public int ReachableComponents { get; init; }
    public double AreaA { get { double a = 0; foreach (var t in Triangles.Values) if (t.InArea) a += t.AreaClipped; return a; } }

    internal RouteGeometry.DiscSet XDiscs { get; init; } = null!;
    internal RouteGeometry.MeshBuckets MeshIndex { get; init; } = null!;
    internal RouteGeometry.MeshBuckets? ChildIndex { get; init; }
    internal IReadOnlyList<PrimitiveGeometry> LoadedPrimitives { get; init; } = Array.Empty<PrimitiveGeometry>();
    internal IReadOnlyDictionary<NavGraph.TriRef, List<NavGraph.TriRef>> Adjacent { get; init; }
        = new Dictionary<NavGraph.TriRef, List<NavGraph.TriRef>>();

    /// <summary>The winning navmeshes whose plan bounds come within <paramref name="r"/> of (x, y), in
    /// FormKey order: a prefilter only - a navmesh left out cannot cover any point within r.</summary>
    public IReadOnlyList<NavmeshGeometry> MeshesNear(float x, float y, float r) => MeshIndex.Near(x, y, r);
}

/// <summary>The load order's answer at one XY: the station environment the per-candidate checks
/// read, and the covering triangle (C5's, the highest drawn one) it was read on.</summary>
public readonly record struct RouteEnv(StationEnvironment Env, NavGraph.TriRef? Covering);

/// <summary>One gaze of an accepted station: level at the eye, bearing 0 = +Y clockwise.</summary>
public sealed record RouteGaze(RouteGazeKind Kind, float BearingDeg, Vector3 LookAt);

/// <summary>The verdict of the per-candidate checks on one XY.</summary>
public sealed record RouteTestVerdict(
    StationRejection Reason, string Detail, StationEnvironment Env, NavGraph.TriRef? Covering,
    float EyeZ, float? FeetAboveSurface, int CoveringFlags);

/// <summary>One composed candidate, tested or dropped.</summary>
public sealed record RouteStation
{
    public required string Id { get; init; }
    public required RouteFamily Family { get; init; }
    public required float X { get; init; }
    public required float Y { get; init; }
    public required bool Accepted { get; init; }
    public RouteDrop Drop { get; init; } = RouteDrop.None;
    /// <summary>Null for a dropped candidate, which was never tested.</summary>
    public RouteTestVerdict? Verdict { get; init; }
    public IReadOnlyList<RouteGaze> Gazes { get; init; } = Array.Empty<RouteGaze>();
    /// <summary>Route family: the kept path it stands on, and how far along it (plan).</summary>
    public int? PathId { get; init; }
    public float? WalkedAt { get; init; }
    /// <summary>Fill families: the source triangle and the subdivision path; retry: the alternate j.</summary>
    public NavGraph.TriRef? SourceTri { get; init; }
    public string? SubPath { get; init; }
    public int? RetryJ { get; init; }
    /// <summary>Route family, a disclosure (not a rule term): the angle between the station's G-fwd (its own
    /// funnel segment) and the bearing to the path point 512 u further along (or the path's end), which
    /// measures how far a channel-taut path's local direction departs from where the walk goes.</summary>
    public float? FwdVsAheadDeg { get; init; }
    public StationRejection Refusal => Verdict?.Reason ?? StationRejection.None;
}

/// <summary>One family's accounting: accepted + refused + dropped must equal composed.</summary>
public sealed class RouteYield
{
    public required RouteFamily Family { get; init; }
    public int Composed { get; set; }
    public int Accepted { get; set; }
    public Dictionary<StationRejection, int> Rejected { get; } = new();
    public Dictionary<RouteDrop, int> Dropped { get; } = new();
    public int RejectedTotal { get { int n = 0; foreach (var v in Rejected.Values) n += v; return n; } }
    public int DroppedTotal { get { int n = 0; foreach (var v in Dropped.Values) n += v; return n; } }
    public bool Closes => Accepted + RejectedTotal + DroppedTotal == Composed;
}

/// <summary>A solved door-to-door or door-to-exit path.</summary>
public sealed record RoutePath(int Id, NavGraph.TriRef From, NavGraph.TriRef To,
                               IReadOnlyList<Vector2> Corners, float Length, bool Kept);

/// <summary>Everything one sampling produced.</summary>
public sealed class RouteResult
{
    public List<RouteStation> Stations { get; } = new();
    public Dictionary<RouteFamily, RouteYield> Yields { get; } = new();
    public List<RoutePath> Paths { get; } = new();
    public int DoorEndpoints { get; set; }
    public int ExitEndpoints { get; set; }
    public int PairsConsidered { get; set; }
    public int PairsNoPath { get; set; }
    public int FillStationsWithoutPathGaze { get; set; }
    public int PiecesNeedingFill { get; set; }
}

/// <summary>A connected piece of A_W no station of the level covers.</summary>
public sealed record RouteUncoveredPiece(double Area, Vector2 Centroid, int Triangles, string Reason);

/// <summary>Coverage of A_W by one station set (§7).</summary>
public sealed class RouteCoverage
{
    public double AreaA { get; init; }
    public double Covered { get; init; }
    public double Fraction => AreaA > 0 ? Covered / AreaA : 0.0;
    public IReadOnlyDictionary<NavGraph.TriRef, double> CoveredPerTriangle { get; init; }
        = new Dictionary<NavGraph.TriRef, double>();
    public IReadOnlyList<RouteUncoveredPiece> Pieces { get; init; } = Array.Empty<RouteUncoveredPiece>();
    public IReadOnlyDictionary<string, double> UncoveredByReason { get; init; } = new Dictionary<string, double>();
}

public static class RouteStations
{
    // ---- the sealed parameters (g5-route-sampling/1 §11) ---------------------------------------
    public const float GridSize = 4096f;
    public const float RArea = 2048f;
    public const float Spacing = 512f;
    public const float CoverageDistance = 512f;
    public const float StartOffset = 256f;
    public const float PathSampleStep = 32f;
    public const float PathDedupRadius = 64f;
    public const int DoorNearestK = 4;
    public const int ExitNearestK = 1;
    public const float CandidateDedup = 128f;
    public const int RetryAlternates = 6;
    public const float ContentReach = 4096f;
    public const float ContentBearingStepDeg = 5f;
    public const float GazeDedupDeg = 30f;
    public const float MovedThreshold = 1f;
    public const float HalfFovDeg = StationRule.DefaultFovDeg / 2f;
    public const float LookAtDistance = 1000f;
    public const int FlagInitiallyDisabled = 0x0800;
    public const int FlagDeleted = 0x0020;
    public const string FillSeed = "g5-fill:20260925";
    public const string RetrySeed = "g5-retry:20260925";

    static readonly StringComparer Ci = StringComparer.OrdinalIgnoreCase;

    // ============================================================ §2-§3 classify ==================

    /// <summary>§2-§3: X_W, the squares, the loaded set, walkable and A_W.</summary>
    public static RouteArea Classify(RouteWorld w)
    {
        var winning = new Dictionary<FormKey, NavmeshGeometry>();
        var squareOf = new Dictionary<FormKey, (int X, int Y)?>();
        foreach (var n in w.Navmeshes)
            if (winning.TryAdd(n.Mesh.FormKey, n.Mesh)) squareOf[n.Mesh.FormKey] = n.Square;
        var cityNav = new HashSet<FormKey>(w.CityNavmeshes);

        // ---- §2 X_W ------------------------------------------------------------------------------
        var xs = XPointsOf(w);

        // ---- §3.1 the squares and the loaded set ---------------------------------------------------
        var areaSq = new HashSet<(int X, int Y)>();
        foreach (var p in xs) AddDiscSquares(areaSq, p.P.X, p.P.Y, RArea);
        foreach (var k in cityNav)
            if (winning.TryGetValue(k, out var nv))
                for (int i = 0; i < nv.Triangles.Count; i++)
                    if (InRange(nv, nv.Triangles[i]))
                    {
                        var (a, b, c) = Plan(nv, nv.Triangles[i]);
                        var cen = (a + b + c) / 3f;
                        areaSq.Add(SquareOf(cen.X, cen.Y));   // AMENDMENT-01 r4: the centroid's square
                    }
        var loadedSq = new HashSet<(int X, int Y)>();
        foreach (var (sx, sy) in areaSq)
            for (int dx = -1; dx <= 1; dx++)
                for (int dy = -1; dy <= 1; dy++) loadedSq.Add((sx + dx, sy + dy));
        var loaded = new Dictionary<FormKey, NavmeshGeometry>();
        int noSquare = 0;
        foreach (var (k, nv) in winning)
        {
            if (squareOf[k] is not { } sq) { noSquare++; continue; }        // AMENDMENT-01 r5: counted
            if (loadedSq.Contains(sq)) loaded[k] = nv;
        }

        // ---- §3.2 the graph: nodes and edges from the record (NavGraph) ------------------------------
        var (nodes, report) = NavGraph.Build(loaded);
        var keys = new List<NavGraph.TriRef>(nodes.Keys);
        keys.Sort(CompareTri);
        var index = new Dictionary<NavGraph.TriRef, int>(keys.Count);
        for (int i = 0; i < keys.Count; i++) index[keys[i]] = i;
        var parent = new int[keys.Count];
        for (int i = 0; i < parent.Length; i++) parent[i] = i;
        int Find(int i) { while (parent[i] != i) { parent[i] = parent[parent[i]]; i = parent[i]; } return i; }
        var adjacent = new Dictionary<NavGraph.TriRef, List<NavGraph.TriRef>>(keys.Count);
        foreach (var k in keys) adjacent[k] = new List<NavGraph.TriRef>();
        foreach (var (k, node) in nodes)
            foreach (var (to, _, _) in node.Links)
            {
                // AMENDMENT-01 r2: an edge exists if EITHER triangle's record links to the other.
                int ra = Find(index[k]), rb = Find(index[to]);
                if (ra != rb) parent[ra] = rb;
                if (!adjacent[k].Contains(to)) adjacent[k].Add(to);
                if (!adjacent[to].Contains(k)) adjacent[to].Add(k);
            }
        // component ids in first-appearance order over the sorted keys, so they are stable
        var compId = new Dictionary<int, int>();
        var compOf = new int[keys.Count];
        for (int i = 0; i < keys.Count; i++)
        {
            int r = Find(i);
            if (!compId.TryGetValue(r, out int id)) compId[r] = id = compId.Count;
            compOf[i] = id;
        }

        // doors and exits (AMENDMENT-01 r1, r3)
        var doorTris = new HashSet<NavGraph.TriRef>();
        var exitTris = new HashSet<NavGraph.TriRef>();
        foreach (var (k, nv) in loaded)
        {
            foreach (var d in nv.DoorTriangles ?? Array.Empty<NavDoorTriangle>())
            {
                var t = new NavGraph.TriRef(k, d.TriangleBeforeDoor);
                if (nodes.ContainsKey(t)) doorTris.Add(t);
            }
            var table = nv.EdgeLinks;
            for (int ti = 0; ti < nv.Triangles.Count; ti++)
            {
                var t = new NavGraph.TriRef(k, ti);
                if (!nodes.ContainsKey(t)) continue;
                var tri = nv.Triangles[ti];
                foreach (var (link, bit) in new[] { (tri.EdgeLink01, 0x01), (tri.EdgeLink12, 0x02), (tri.EdgeLink20, 0x04) })
                {
                    if ((tri.Flags & bit) == 0 || link < 0 || table is null || link >= table.Count) continue;
                    // an exit is a link the graph would walk if its far side were loaded: a held ledge is not one
                    if (!loaded.ContainsKey(table[link].Mesh) && NavGraph.Traverses(report.Policy ?? NavGraph.LinkPolicy.Default, table[link].Type))
                    { exitTris.Add(t); break; }
                }
            }
        }
        var reachableComp = new HashSet<int>();
        foreach (var t in doorTris) reachableComp.Add(compOf[index[t]]);
        foreach (var t in exitTris) reachableComp.Add(compOf[index[t]]);

        // ---- §3.3 A_W, exactly --------------------------------------------------------------------
        var discs = new RouteGeometry.DiscSet(xs.Select(p => new RouteGeometry.D2(p.P.X, p.P.Y)), RArea);
        var tris = new Dictionary<NavGraph.TriRef, RouteTriangle>();
        foreach (var (k, nv) in loaded)
        {
            bool own = cityNav.Contains(k);
            for (int ti = 0; ti < nv.Triangles.Count; ti++)
            {
                var t = new NavGraph.TriRef(k, ti);
                var tri = nv.Triangles[ti];
                bool inRange = InRange(nv, tri);
                var (a, b, c) = inRange ? Plan(nv, tri) : (Vector2.Zero, Vector2.Zero, Vector2.Zero);
                double full = inRange ? Math.Abs(RouteGeometry.Cross(a, b, c)) / 2.0 : 0.0;
                bool node = nodes.ContainsKey(t);
                int comp = node ? compOf[index[t]] : -1;
                bool reach = node && reachableComp.Contains(comp);
                double clipped = 0.0;
                if (reach && full > 0.0)
                    clipped = own ? full : RouteGeometry.RegionArea(D(a), D(b), D(c), discs, null);
                tris[t] = new RouteTriangle(t, node, reach, comp, reach, doorTris.Contains(t), exitTris.Contains(t),
                                            own, tri.Flags, a, b, c, full, clipped);
            }
        }

        var prims = new List<PrimitiveGeometry>();
        foreach (var p in w.Primitives)
            if (loadedSq.Contains(SquareOf(p.Position.X, p.Position.Y))) prims.Add(p);

        return new RouteArea
        {
            World = w, XPoints = xs, AreaSquares = areaSq, LoadedSquares = loadedSq,
            Loaded = loaded, Winning = winning, Nodes = nodes, Report = report, Triangles = tris,
            NavmeshesWithoutSquare = noSquare, Components = compId.Count, ReachableComponents = reachableComp.Count,
            XDiscs = discs, MeshIndex = new RouteGeometry.MeshBuckets(winning.Values),
            ChildIndex = w.ChildFootprint is { } ch ? new RouteGeometry.MeshBuckets(ch) : null,
            LoadedPrimitives = prims, Adjacent = adjacent,
        };
    }

    /// <summary>§2: every placed record the city plugin defines or overrides, at the city's own
    /// position; an override's master position too when the city moved it by more than 1 u, or when
    /// the city's version sets the initially-disabled (0x0800) or deleted (0x0020) header flag.</summary>
    static List<RouteXPoint> XPointsOf(RouteWorld w)
    {
        var xs = new List<RouteXPoint>();
        foreach (var p in w.CityPlaced)
        {
            int ci = -1;
            for (int i = 0; i < p.Versions.Count; i++) if (Ci.Equals(p.Versions[i].Plugin, w.CityPlugin)) ci = i;
            if (ci < 0) continue;
            var cv = p.Versions[ci];
            var mv = ci > 0 ? p.Versions[ci - 1] : null;             // the highest-priority plugin before the city
            if (cv.Position is { } cp)
                xs.Add(new RouteXPoint(new Vector2(cp.X, cp.Y), p.FormKey, p.BaseHasModel,
                                       mv is null ? "city defines" : "city overrides"));
            if (mv?.Position is not { } mp) continue;
            bool moved = cv.Position is { } cp2 && Vector2.Distance(new Vector2(mp.X, mp.Y), new Vector2(cp2.X, cp2.Y)) > MovedThreshold;
            bool offOrDeleted = (cv.HeaderFlags & (FlagInitiallyDisabled | FlagDeleted)) != 0;
            if (moved)
                xs.Add(new RouteXPoint(new Vector2(mp.X, mp.Y), p.FormKey, p.BaseHasModel, "master position (moved by the city)"));
            else if (offOrDeleted)
                xs.Add(new RouteXPoint(new Vector2(mp.X, mp.Y), p.FormKey, p.BaseHasModel,
                                       "master position (the city's version is disabled or deleted)"));
        }
        return xs;
    }

    /// <summary>AMENDMENT-01 r6: every square [4096 gx, 4096 (gx+1)) whose closed rectangle lies within
    /// <paramref name="r"/> (plan) of the point.</summary>
    static void AddDiscSquares(HashSet<(int X, int Y)> into, float x, float y, float r)
    {
        int x0 = (int)MathF.Floor((x - r) / GridSize), x1 = (int)MathF.Floor((x + r) / GridSize);
        int y0 = (int)MathF.Floor((y - r) / GridSize), y1 = (int)MathF.Floor((y + r) / GridSize);
        for (int gx = x0; gx <= x1; gx++)
            for (int gy = y0; gy <= y1; gy++)
            {
                double lx = gx * (double)GridSize, hx = lx + GridSize, ly = gy * (double)GridSize, hy = ly + GridSize;
                double dx = x < lx ? lx - x : x > hx ? x - hx : 0.0;
                double dy = y < ly ? ly - y : y > hy ? y - hy : 0.0;
                if (dx * dx + dy * dy <= (double)r * r) into.Add((gx, gy));
            }
    }

    // ============================================================ §5 test =========================

    /// <summary>§5: the per-candidate checks on one XY, in the sealed precedence. The ground and water
    /// checks are <see cref="StationRule.CheckGround"/> and <see cref="StationRule.CheckWater"/>, the
    /// code the face-on and observing composers run.</summary>
    public static RouteTestVerdict Test(RouteArea area, Func<float, float, RouteEnv> environment, float x, float y)
    {
        var e = environment(x, y);
        var env = e.Env;
        var reasons = new List<StationRejection>(6);
        var (floorKnown, floorZ, eyeZ) = StationRule.CheckGround(env, reasons);    // C5, C2
        bool drawn = env.Answered && env.Covered && env.Drawn;
        RouteTriangle? rt = null;
        (bool Ok, int Covered, string Why) clear = (true, 5, "");
        if (drawn)
        {
            // AMENDMENT-01 r8: the covering triangle may lie in a winning navmesh outside the loaded
            // set, which is Unreachable.
            if (e.Covering is not { } ct || !area.Triangles.TryGetValue(ct, out rt)) reasons.Add(StationRejection.Unreachable);
            else if (!rt.Walkable) reasons.Add(StationRejection.Unreachable);
            else if (!rt.InArea) reasons.Add(StationRejection.OutsideArea);
            // Clearance at the station's navmesh height, as --route asks it of a waypoint.
            clear = NavCover.Clearance(area.MeshesNear(x, y, NavCover.ClearanceRadius + 1f), x, y, env.NavZ);
            if (!clear.Ok) reasons.Add(StationRejection.TightClearance);
        }
        var child = ChildCover(area, x, y);
        if (child is not null) reasons.Add(StationRejection.InChildWorldspaceFootprint);
        float? feet = StationRule.CheckWater(env, floorKnown, floorZ, reasons);   // C4, C3
        var eye = new Vector3(x, y, eyeZ);
        PrimitiveGeometry? inside = null;
        if (floorKnown)
            foreach (var p in area.LoadedPrimitives)
                if (EyeInsideRotated(p, eye)) { inside = p; reasons.Add(StationRejection.EyeInsidePrimitive); break; }

        var reason = StationRule.FirstByPrecedence(reasons);
        int flags = 0;
        if (e.Covering is { } cov && area.Winning.TryGetValue(cov.Mesh, out var cm) && cov.Index >= 0 && cov.Index < cm.Triangles.Count)
            flags = cm.Triangles[cov.Index].Flags;
        string detail = reason switch
        {
            StationRejection.None => "",
            StationRejection.NoNavmeshAnswer => "the navmesh was asked about this XY and returned nothing at all",
            StationRejection.NoCoveringTriangle => $"no winning triangle covers the XY; nearest navmesh height read {env.NavZ:0.###}",
            StationRejection.AllCoveringTrianglesDeleted => $"every covering triangle is flagged deleted; navmesh height {env.NavZ:0.###}",
            StationRejection.Unreachable => rt is null
                ? $"the covering triangle {e.Covering} lies in a winning navmesh outside the loaded set"
                : $"the covering triangle {rt.Tri} is in component {rt.Component}, which holds no door triangle and no exit",
            StationRejection.OutsideArea => $"the covering triangle {rt!.Tri} is walkable but has no part in A_W "
                                          + $"(no point within {RArea:0} u of an X_W point, not the changed plugin's navmesh)",
            StationRejection.InChildWorldspaceFootprint => $"the XY lies under the child worldspace's winning triangle {child}",
            StationRejection.TightClearance => clear.Why + $" (navmesh height {env.NavZ:0.###})",
            StationRejection.FloorUnknown => $"navmesh {env.NavZ:0.###}, no physical floor: {env.Floor.Reason}, "
                                           + $"{env.Floor.Surfaces} surface(s) in the column",
            StationRejection.WaterUnknown => $"cell WaterHeight {env.Water.CellHeight:R} ({env.Water.Kind}), worldspace default "
                                           + $"{env.Water.WorldDefault:R} from {env.Water.FromWorldspace}: {env.Water.How}",
            StationRejection.StationUnderwater => $"floor {floorZ:0.###} is below the surface {env.Water.SurfaceZ:0.###} "
                                                + $"({env.Water.Verdict}: {env.Water.How})",
            StationRejection.EyeInsidePrimitive => $"eye ({x:0.#}, {y:0.#}, {eyeZ:0.###}) lies inside or on primitive {inside?.Key}",
            _ => reason.ToString(),
        };
        return new RouteTestVerdict(reason, detail, env, e.Covering, eyeZ, feet, flags);
    }

    /// <summary>AMENDMENT-01 r9: any winning child-worldspace triangle, any flags, containing the XY.</summary>
    static string? ChildCover(RouteArea area, float x, float y)
    {
        if (area.ChildIndex is not { } ci) return null;
        foreach (var nv in ci.Near(x, y, 0.5f))
            for (int ti = 0; ti < nv.Triangles.Count; ti++)
            {
                var tri = nv.Triangles[ti];
                if (!InRange(nv, tri)) continue;
                var (a, b, c) = Plan(nv, tri);
                if (RouteGeometry.Contains(D(a), D(b), D(c), new RouteGeometry.D2(x, y)))
                    return $"{nv.FormKey}#{ti} (flags 0x{tri.Flags:X})";
            }
        return null;
    }

    /// <summary>Whether an eye lies inside or on a primitive's box under its FULL rotation (the renderer's
    /// <c>RefrRotation</c>): the face-on rule's <c>EyeInside</c> models a Z-only turn, which would test
    /// a horizontal panel as if it stood upright.</summary>
    internal static bool EyeInsideRotated(PrimitiveGeometry p, Vector3 eye)
    {
        if (!float.IsFinite(eye.X) || !float.IsFinite(eye.Y) || !float.IsFinite(eye.Z)) return false;
        var r = SpatialOverlap.RefrRotation(new Vec3(p.RotationRad.X, p.RotationRad.Y, p.RotationRad.Z));
        var l = Vector3.Transform(eye - p.Position, Matrix4x4.Transpose(r));
        const float eps = 1e-3f;
        return MathF.Abs(l.X) <= p.ExtentFull.X / 2f + eps && MathF.Abs(l.Y) <= p.ExtentFull.Y / 2f + eps
            && MathF.Abs(l.Z) <= p.ExtentFull.Z / 2f + eps;
    }

    // ============================================================ §4 and §6 sample ===============

    /// <summary>§4 and §6: route and fill candidates, each tested, with the gaze set per accepted station.</summary>
    public static RouteResult Sample(RouteArea area, Func<float, float, RouteEnv> environment)
    {
        var r = new RouteResult();
        foreach (var f in new[] { RouteFamily.Route, RouteFamily.Fill, RouteFamily.FillRetry })
            r.Yields[f] = new RouteYield { Family = f };
        string ws = area.World.WorldspaceEditorId;
        var accepted = new RouteGeometry.PointGrid(Spacing);
        var fwdOf = new Dictionary<int, float>();                       // station index -> route fwd bearing
        int nRoute = 0, nFill = 0, nRetry = 0;

        void Count(RouteYield y, RouteStation s)
        {
            y.Composed++;
            if (s.Accepted) y.Accepted++;
            else if (s.Drop != RouteDrop.None) y.Dropped[s.Drop] = (y.Dropped.TryGetValue(s.Drop, out int d) ? d : 0) + 1;
            else y.Rejected[s.Refusal] = (y.Rejected.TryGetValue(s.Refusal, out int n) ? n : 0) + 1;
        }

        // ---- §4.1 route candidates ----------------------------------------------------------------
        var doors = new List<RouteTriangle>(); var exits = new List<RouteTriangle>();
        foreach (var t in area.Triangles.Values)
        {
            if (!t.InArea) continue;
            if (t.Door) doors.Add(t);
            if (t.Exit) exits.Add(t);
        }
        doors.Sort((p, q) => CompareTri(p.Tri, q.Tri));
        exits.Sort((p, q) => CompareTri(p.Tri, q.Tri));
        r.DoorEndpoints = doors.Count; r.ExitEndpoints = exits.Count;

        var pairs = new SortedSet<(NavGraph.TriRef A, NavGraph.TriRef B)>(Comparer<(NavGraph.TriRef A, NavGraph.TriRef B)>.Create(
            (p, q) => { int c = CompareTri(p.A, q.A); return c != 0 ? c : CompareTri(p.B, q.B); }));
        void AddPair(NavGraph.TriRef a, NavGraph.TriRef b)
        {
            if (a.Equals(b)) return;
            pairs.Add(CompareTri(a, b) < 0 ? (a, b) : (b, a));
        }
        foreach (var d in doors)
        {
            var dc = Centroid(d);
            foreach (var o in Nearest(doors.Where(o => !o.Tri.Equals(d.Tri)), dc, DoorNearestK)) AddPair(d.Tri, o.Tri);
            foreach (var o in Nearest(exits, dc, ExitNearestK)) AddPair(d.Tri, o.Tri);
        }
        r.PairsConsidered = pairs.Count;

        var solved = new List<(NavGraph.TriRef A, NavGraph.TriRef B, List<Vector2> Corners, float Length)>();
        foreach (var (a, b) in pairs)
        {
            var ta = area.Triangles[a]; var tb = area.Triangles[b];
            if (ta.Component != tb.Component) { r.PairsNoPath++; continue; }     // counted, not solved
            var ca = area.Nodes[a].Centroid; var cb = area.Nodes[b].Centroid;
            var corners = Solve(area, a, new Vector2(ca.X, ca.Y), ca.Z, b, new Vector2(cb.X, cb.Y));
            if (corners is null) { r.PairsNoPath++; continue; }
            solved.Add((a, b, corners, Length(corners)));
        }
        // §4.1 step 4: dedupe paths, shortest first, ties by the endpoints
        solved.Sort((p, q) =>
        {
            int c = p.Length.CompareTo(q.Length);
            if (c != 0) return c;
            c = CompareTri(p.A, q.A);
            return c != 0 ? c : CompareTri(p.B, q.B);
        });
        var kept = new List<List<Vector2>>();
        int pathId = 0;
        foreach (var (a, b, corners, len) in solved)
        {
            bool dup = kept.Count > 0;
            if (dup)
                foreach (var s in SamplesAlong(corners, PathSampleStep))
                {
                    bool near = false;
                    foreach (var k in kept) if (DistToPolyline(s, k) <= PathDedupRadius) { near = true; break; }
                    if (!near) { dup = false; break; }
                }
            var path = new RoutePath(pathId++, a, b, corners, len, !dup);
            r.Paths.Add(path);
            if (dup) continue;
            kept.Add(corners);
            // §4.1 step 5: place at 256 u, then every S
            for (float at = StartOffset; at <= len + 1e-3f; at += Spacing)
            {
                var (pt, seg) = PointAt(corners, at);
                var dir = corners[seg + 1] - corners[seg];
                float fwd = StationRule.Bearing(dir.X, dir.Y);
                var (aheadPt, _) = PointAt(corners, MathF.Min(at + Spacing, len));
                var toAhead = aheadPt - pt;
                float? vsAhead = toAhead.LengthSquared() > 1e-6f ? AngDiff(fwd, StationRule.Bearing(toAhead.X, toAhead.Y)) : null;
                var v = Test(area, environment, pt.X, pt.Y);
                RouteStation st;
                string id = $"{ws}:R{nRoute++:D5}";
                bool notKept = v.Reason is StationRejection.NoNavmeshAnswer or StationRejection.NoCoveringTriangle
                    or StationRejection.AllCoveringTrianglesDeleted or StationRejection.Unreachable or StationRejection.OutsideArea;
                if (!notKept && accepted.AnyWithin(pt, CandidateDedup))
                    st = new RouteStation { Id = id, Family = RouteFamily.Route, X = pt.X, Y = pt.Y, Accepted = false,
                                            Drop = RouteDrop.Deduplicated, PathId = path.Id, WalkedAt = at, FwdVsAheadDeg = vsAhead };
                else
                {
                    st = new RouteStation { Id = id, Family = RouteFamily.Route, X = pt.X, Y = pt.Y,
                                            Accepted = v.Reason == StationRejection.None, Verdict = v,
                                            PathId = path.Id, WalkedAt = at, FwdVsAheadDeg = vsAhead };
                    if (st.Accepted) { accepted.Add(pt); fwdOf[r.Stations.Count] = fwd; }
                }
                r.Stations.Add(st);
                Count(r.Yields[RouteFamily.Route], st);
            }
        }

        // ---- §4.2 fill candidates -----------------------------------------------------------------
        var accDiscs = new RouteGeometry.DiscSet(accepted.All.Select(p => new RouteGeometry.D2(p.X, p.Y)), Spacing);
        var cands = new List<(string Key, RouteTriangle Src, string Sub, Vector2 A, Vector2 B, Vector2 C)>();
        var pieces = area.Triangles.Values.Where(t => t.InArea).OrderBy(t => t.Tri, TriComparer).ToList();
        foreach (var t in pieces)
        {
            var u1 = t.CityOwn ? null : area.XDiscs;
            double covered = accDiscs.Count == 0 ? 0.0 : RouteGeometry.RegionArea(D(t.A), D(t.B), D(t.C), u1, accDiscs);
            if (t.AreaClipped - covered <= 1e-6 * Math.Max(1.0, t.AreaClipped)) continue;
            r.PiecesNeedingFill++;
            foreach (var (sa, sb, sc, sub) in Subdivide(t.A, t.B, t.C, ""))
            {
                bool meets = t.CityOwn || RouteGeometry.RegionArea(D(sa), D(sb), D(sc), area.XDiscs, null) > 1e-9;
                if (meets) cands.Add((FillKey(t.Tri.Mesh, t.Tri.Index, sub), t, sub, sa, sb, sc));
            }
        }
        cands.Sort((p, q) => string.CompareOrdinal(p.Key, q.Key));
        foreach (var cd in cands)
        {
            var cen = (cd.A + cd.B + cd.C) / 3f;
            string id = $"{ws}:F{nFill++:D5}";
            if (accepted.AnyWithin(cen, Spacing))
            {
                var sk = new RouteStation { Id = id, Family = RouteFamily.Fill, X = cen.X, Y = cen.Y, Accepted = false,
                                            Drop = RouteDrop.SkippedNearAccepted, SourceTri = cd.Src.Tri, SubPath = cd.Sub };
                r.Stations.Add(sk); Count(r.Yields[RouteFamily.Fill], sk);
                continue;
            }
            var v = Test(area, environment, cen.X, cen.Y);
            var st = new RouteStation { Id = id, Family = RouteFamily.Fill, X = cen.X, Y = cen.Y,
                                        Accepted = v.Reason == StationRejection.None, Verdict = v,
                                        SourceTri = cd.Src.Tri, SubPath = cd.Sub };
            r.Stations.Add(st); Count(r.Yields[RouteFamily.Fill], st);
            if (st.Accepted) { accepted.Add(cen); continue; }
            // §4.2 step 5: the seeded retry, in the same sub-triangle
            for (int j = 1; j <= RetryAlternates; j++)
            {
                var pt = RetryPoint(cd.Src.Tri.Mesh, cd.Src.Tri.Index, cd.Sub, j, cd.A, cd.B, cd.C);
                string rid = $"{ws}:X{nRetry++:D5}";
                RouteDrop drop = RouteDrop.None;
                if (!cd.Src.CityOwn && !area.XDiscs.Contains(pt.X, pt.Y)) drop = RouteDrop.SkippedNotInArea;
                else if (accepted.AnyWithin(pt, Spacing)) drop = RouteDrop.SkippedNearAccepted;
                if (drop != RouteDrop.None)
                {
                    var sk = new RouteStation { Id = rid, Family = RouteFamily.FillRetry, X = pt.X, Y = pt.Y, Accepted = false,
                                                Drop = drop, SourceTri = cd.Src.Tri, SubPath = cd.Sub, RetryJ = j };
                    r.Stations.Add(sk); Count(r.Yields[RouteFamily.FillRetry], sk);
                    continue;
                }
                var rv = Test(area, environment, pt.X, pt.Y);
                var rs = new RouteStation { Id = rid, Family = RouteFamily.FillRetry, X = pt.X, Y = pt.Y,
                                            Accepted = rv.Reason == StationRejection.None, Verdict = rv,
                                            SourceTri = cd.Src.Tri, SubPath = cd.Sub, RetryJ = j };
                r.Stations.Add(rs); Count(r.Yields[RouteFamily.FillRetry], rs);
                if (rs.Accepted) { accepted.Add(pt); break; }
            }
        }

        // ---- §6 the gaze set per accepted station --------------------------------------------------
        var content = new List<Vector2>();
        foreach (var p in area.XPoints) if (p.HasModel) content.Add(p.P);
        var endpoints = doors.Concat(exits).GroupBy(t => t.Tri).Select(g => g.First()).ToList();
        for (int i = 0; i < r.Stations.Count; i++)
        {
            var s = r.Stations[i];
            if (!s.Accepted) continue;
            var eye = new Vector3(s.X, s.Y, s.Verdict!.EyeZ);
            float? fwd = null;
            if (s.Family == RouteFamily.Route) fwd = fwdOf.TryGetValue(i, out var fb) ? fb : null;
            else
            {
                fwd = FillFwd(area, s, endpoints);
                if (fwd is null) r.FillStationsWithoutPathGaze++;
            }
            var gazes = new List<RouteGaze>();
            void Offer(RouteGazeKind k, float b)
            {
                b = ((b % 360f) + 360f) % 360f;
                foreach (var g in gazes) if (AngDiff(g.BearingDeg, b) < GazeDedupDeg) return;
                float rad = b * MathF.PI / 180f;
                gazes.Add(new RouteGaze(k, b, eye + new Vector3(MathF.Sin(rad), MathF.Cos(rad), 0f) * LookAtDistance));
            }
            if (fwd is { } f0) { Offer(RouteGazeKind.Fwd, f0); Offer(RouteGazeKind.Back, f0 + 180f); }
            Offer(RouteGazeKind.Content, ContentBearing(new Vector2(s.X, s.Y), content, fwd));
            // C1, the level-gaze guard, on every gaze
            bool level = gazes.All(g => StationRule.CheckLevel(eye, g.LookAt) == StationRejection.None);
            if (level) r.Stations[i] = s with { Gazes = gazes };
            else
            {
                var y = r.Yields[s.Family];
                y.Accepted--; y.Rejected[StationRejection.NonLevelGaze] = (y.Rejected.TryGetValue(StationRejection.NonLevelGaze, out int n) ? n : 0) + 1;
                r.Stations[i] = s with { Accepted = false, Verdict = s.Verdict with
                    { Reason = StationRejection.NonLevelGaze, Detail = "a composed gaze left the eye's height" } };
            }
        }
        return r;
    }

    /// <summary>§6 G-fwd for a fill station (DESIGN reading 14): the first segment of the solved path
    /// from the station's covering triangle to its nearest door or exit endpoint by walked length.
    /// Endpoints in the station's component are taken in ascending plan distance, and the search stops
    /// once the next plan distance is not below the best walked length.</summary>
    static float? FillFwd(RouteArea area, RouteStation s, IReadOnlyList<RouteTriangle> endpoints)
    {
        if (s.Verdict?.Covering is not { } cov || !area.Triangles.TryGetValue(cov, out var ct)) return null;
        var here = new Vector2(s.X, s.Y);
        var cand = endpoints.Where(e => e.Component == ct.Component)
            .Select(e => (E: e, D: Vector2.Distance(here, Centroid(e))))
            .OrderBy(p => p.D).ThenBy(p => p.E.Tri, TriComparer).ToList();
        float best = float.MaxValue; List<Vector2>? bestPath = null;
        foreach (var (e, d) in cand)
        {
            if (d >= best) break;
            var ec = area.Nodes[e.Tri].Centroid;
            var corners = Solve(area, cov, here, s.Verdict.Env.NavZ, e.Tri, new Vector2(ec.X, ec.Y));
            if (corners is null) continue;
            float len = Length(corners);
            if (len < best) { best = len; bestPath = corners; }
        }
        if (bestPath is null) return null;
        for (int i = 0; i + 1 < bestPath.Count; i++)
        {
            var dir = bestPath[i + 1] - bestPath[i];
            if (dir.LengthSquared() > 1e-6f) return StationRule.Bearing(dir.X, dir.Y);
        }
        return null;
    }

    /// <summary>§4.1 step 3: A* over the graph, the funnel, the corner inset - viewprobe
    /// <c>--route-solve</c>'s method, through the same NavGraph functions.</summary>
    static List<Vector2>? Solve(RouteArea area, NavGraph.TriRef from, Vector2 start, float startZ,
                                NavGraph.TriRef to, Vector2 end)
    {
        var channel = NavGraph.FindChannel(area.Nodes, from, to);
        if (channel is null) return null;
        var corners = NavGraph.Funnel(area.Nodes, channel, start, end);
        NavGraph.InsetCorners(corners, startZ,
            (ix, iy, iz) => NavCover.NearestCoverZ(area.MeshesNear(ix, iy, 1f), ix, iy, iz), NavCover.SameFloorZ);
        return corners;
    }

    /// <summary>§6 G-content: the bearing (every 5 deg, 0 = +Y clockwise) whose level 80 deg frustum
    /// holds the most of <paramref name="points"/> within the content reach; ties to the bearing
    /// nearest <paramref name="fwdBearingDeg"/>, then the smaller bearing. X_W is a set of plan points,
    /// so the frustum test is the plan wedge (DESIGN reading 15).</summary>
    public static float ContentBearing(Vector2 eye, IReadOnlyList<Vector2> points, float? fwdBearingDeg)
    {
        var bearings = new List<float>();
        foreach (var p in points)
        {
            var d = p - eye;
            float dist = d.Length();
            if (dist <= 0f || dist > ContentReach) continue;
            bearings.Add(StationRule.Bearing(d.X, d.Y));
        }
        float bestB = 0f; int bestN = -1; float bestTie = float.MaxValue;
        for (int k = 0; k * ContentBearingStepDeg < 360f; k++)
        {
            float b = k * ContentBearingStepDeg;
            int n = 0;
            foreach (var pb in bearings) if (AngDiff(pb, b) <= HalfFovDeg) n++;
            float tie = fwdBearingDeg is { } f ? AngDiff(b, f) : 0f;
            if (n > bestN || (n == bestN && tie < bestTie))
            { bestN = n; bestB = b; bestTie = tie; }
        }
        return bestB;
    }

    // ============================================================ §7 coverage =====================

    /// <summary>§7: the part of A_W within D of at least one of <paramref name="stations"/>, exactly,
    /// with the uncovered pieces located and given the refusal reason of the refused candidates of
    /// <paramref name="refused"/> that fall in them (DESIGN reading 19). For a later level (L2-L4),
    /// <paramref name="reasonOf"/> names why a candidate is not in the level's set (null = not a
    /// drop-out); the piece takes the most frequent such reason, ties to the ordinal-first.</summary>
    public static RouteCoverage Coverage(RouteArea area, IEnumerable<Vector2> stations,
                                         IReadOnlyList<RouteStation>? refused = null,
                                         Func<RouteStation, string?>? reasonOf = null)
    {
        var ds = new RouteGeometry.DiscSet(stations.Select(p => new RouteGeometry.D2(p.X, p.Y)), CoverageDistance);
        var per = new Dictionary<NavGraph.TriRef, double>();
        double total = 0, covered = 0;
        var uncovered = new Dictionary<NavGraph.TriRef, double>();
        foreach (var t in area.Triangles.Values)
        {
            if (!t.InArea) continue;
            total += t.AreaClipped;
            double c = ds.Count == 0 ? 0.0
                : RouteGeometry.RegionArea(D(t.A), D(t.B), D(t.C), t.CityOwn ? null : area.XDiscs, ds);
            c = Math.Min(c, t.AreaClipped);
            per[t.Tri] = c;
            covered += c;
            double u = t.AreaClipped - c;
            if (u > 1e-6 * Math.Max(1.0, t.AreaClipped)) uncovered[t.Tri] = u;
        }

        // the uncovered pieces: triangles with uncovered area, joined by the graph's links
        var pieceOf = new Dictionary<NavGraph.TriRef, int>();
        var members = new List<List<NavGraph.TriRef>>();
        foreach (var start in uncovered.Keys.OrderBy(k => k, TriComparer))
        {
            if (pieceOf.ContainsKey(start)) continue;
            int id = members.Count; var list = new List<NavGraph.TriRef>();
            var stack = new Stack<NavGraph.TriRef>(); stack.Push(start); pieceOf[start] = id;
            while (stack.Count > 0)
            {
                var cur = stack.Pop(); list.Add(cur);
                if (!area.Adjacent.TryGetValue(cur, out var nb)) continue;
                foreach (var n in nb)
                    if (uncovered.ContainsKey(n) && !pieceOf.ContainsKey(n)) { pieceOf[n] = id; stack.Push(n); }
            }
            members.Add(list);
        }
        // a candidate carrying no triangle is placed by its XY, among the uncovered triangles
        NavGraph.TriRef? UncoveredAt(float x, float y)
        {
            foreach (var k in uncovered.Keys)
            {
                var t = area.Triangles[k];
                if (RouteGeometry.Contains(D(t.A), D(t.B), D(t.C), new RouteGeometry.D2(x, y))) return k;
            }
            return null;
        }
        var tally = new Dictionary<int, Dictionary<StationRejection, int>>();
        var named = new Dictionary<int, Dictionary<string, int>>();
        if (refused is not null)
            foreach (var s in refused)
            {
                string? why = null;
                if (reasonOf is not null) { why = reasonOf(s); if (why is null) continue; }
                else if (s.Accepted || s.Verdict is null || s.Verdict.Reason == StationRejection.None) continue;
                var tri = s.Verdict?.Covering ?? s.SourceTri ?? UncoveredAt(s.X, s.Y);
                if (tri is not { } tr || !pieceOf.TryGetValue(tr, out int pid)) continue;
                if (why is not null)
                {
                    if (!named.TryGetValue(pid, out var dn)) named[pid] = dn = new Dictionary<string, int>();
                    dn[why] = (dn.TryGetValue(why, out int m) ? m : 0) + 1;
                    continue;
                }
                if (!tally.TryGetValue(pid, out var d)) tally[pid] = d = new Dictionary<StationRejection, int>();
                d[s.Verdict!.Reason] = (d.TryGetValue(s.Verdict.Reason, out int n) ? n : 0) + 1;
            }
        var pieces = new List<RouteUncoveredPiece>();
        var byReason = new Dictionary<string, double>();
        for (int id = 0; id < members.Count; id++)
        {
            double a = 0; var cen = Vector2.Zero; double wsum = 0;
            foreach (var t in members[id])
            {
                double u = uncovered[t]; a += u;
                var tt = area.Triangles[t];
                cen += Centroid(tt) * (float)u; wsum += u;
            }
            string reason = "NoCandidate";
            if (tally.TryGetValue(id, out var d) && d.Count > 0)
            {
                int best = -1;
                foreach (var rr in StationRule.Precedence)
                    if (d.TryGetValue(rr, out int n) && n > best) { best = n; reason = rr.ToString(); }
            }
            else if (named.TryGetValue(id, out var dn) && dn.Count > 0)
                reason = dn.OrderByDescending(kv => kv.Value).ThenBy(kv => kv.Key, StringComparer.Ordinal).First().Key;
            pieces.Add(new RouteUncoveredPiece(a, wsum > 0 ? cen / (float)wsum : Vector2.Zero, members[id].Count, reason));
            byReason[reason] = (byReason.TryGetValue(reason, out double v) ? v : 0) + a;
        }
        pieces.Sort((p, q) => q.Area.CompareTo(p.Area));
        return new RouteCoverage
        {
            AreaA = total, Covered = covered, CoveredPerTriangle = per, Pieces = pieces, UncoveredByReason = byReason,
        };
    }

    /// <summary>§7 L1: coverage by the ACCEPTED stations of a sampling; the refused candidates name the
    /// uncovered pieces' reasons.</summary>
    public static RouteCoverage CoverageL1(RouteArea area, RouteResult result)
    {
        var pts = new List<Vector2>();
        foreach (var s in result.Stations) if (s.Accepted) pts.Add(new Vector2(s.X, s.Y));
        return Coverage(area, pts, result.Stations);
    }

    // ============================================================ seeds ===========================

    /// <summary>§4.2 step 3: the fill order key, lower-case hex.</summary>
    public static string FillKey(FormKey mesh, int tri, string subPath)
        => Sha256Hex($"{FillSeed}:{mesh}:{tri}:{subPath}");

    /// <summary>§4.2 step 5: alternate j (1..6) inside the sub-triangle A, B, C (stored vertex order).</summary>
    public static Vector2 RetryPoint(FormKey mesh, int tri, string subPath, int j, Vector2 a, Vector2 b, Vector2 c)
    {
        string h = Sha256Hex($"{RetrySeed}:{mesh}:{tri}:{subPath}:{j}");
        double u = Convert.ToUInt32(h.Substring(0, 8), 16) / 4294967296.0;
        double v = Convert.ToUInt32(h.Substring(8, 8), 16) / 4294967296.0;
        if (u + v > 1.0) { u = 1.0 - u; v = 1.0 - v; }
        return new Vector2((float)(a.X + u * (b.X - a.X) + v * (c.X - a.X)),
                           (float)(a.Y + u * (b.Y - a.Y) + v * (c.Y - a.Y)));
    }

    /// <summary>§4.2 step 2: 4-way midpoint subdivision until each sub-triangle's plan area is at most
    /// S². Children, in digit order (DESIGN addendum): 0 = (A, mAB, mCA), 1 = (mAB, B, mBC),
    /// 2 = (mCA, mBC, C), 3 = (mBC, mCA, mAB); the root's path is the empty string.</summary>
    internal static IEnumerable<(Vector2 A, Vector2 B, Vector2 C, string Path)> Subdivide(Vector2 a, Vector2 b, Vector2 c, string path)
    {
        double area = Math.Abs(RouteGeometry.Cross(a, b, c)) / 2.0;
        if (area <= (double)Spacing * Spacing || path.Length >= 16) { yield return (a, b, c, path); yield break; }
        var ab = (a + b) / 2f; var bc = (b + c) / 2f; var ca = (c + a) / 2f;
        foreach (var x in Subdivide(a, ab, ca, path + "0")) yield return x;
        foreach (var x in Subdivide(ab, b, bc, path + "1")) yield return x;
        foreach (var x in Subdivide(ca, bc, c, path + "2")) yield return x;
        foreach (var x in Subdivide(bc, ca, ab, path + "3")) yield return x;
    }

    // ============================================================ helpers =========================

    internal static readonly IComparer<NavGraph.TriRef> TriComparer = Comparer<NavGraph.TriRef>.Create(CompareTri);

    /// <summary>(mesh FormKey text, triangle index) ascending: the rule's tie-break.</summary>
    internal static int CompareTri(NavGraph.TriRef a, NavGraph.TriRef b)
    {
        int c = string.CompareOrdinal(a.Mesh.ToString(), b.Mesh.ToString());
        return c != 0 ? c : a.Index.CompareTo(b.Index);
    }

    static IEnumerable<RouteTriangle> Nearest(IEnumerable<RouteTriangle> set, Vector2 from, int k)
        => set.Select(t => (T: t, D: Vector2.Distance(from, Centroid(t))))
              .OrderBy(p => p.D).ThenBy(p => p.T.Tri, TriComparer).Take(k).Select(p => p.T);

    static Vector2 Centroid(RouteTriangle t) => (t.A + t.B + t.C) / 3f;

    static float Length(IReadOnlyList<Vector2> pts)
    {
        float s = 0f;
        for (int i = 0; i + 1 < pts.Count; i++) s += Vector2.Distance(pts[i], pts[i + 1]);
        return s;
    }

    static (Vector2 P, int Seg) PointAt(IReadOnlyList<Vector2> pts, float at)
    {
        float s = 0f;
        for (int i = 0; i + 1 < pts.Count; i++)
        {
            float l = Vector2.Distance(pts[i], pts[i + 1]);
            if (l <= 0f) continue;
            if (s + l >= at || i + 2 == pts.Count)
                return (pts[i] + (pts[i + 1] - pts[i]) * Math.Clamp((at - s) / l, 0f, 1f), i);
            s += l;
        }
        return (pts[^1], Math.Max(0, pts.Count - 2));
    }

    static IEnumerable<Vector2> SamplesAlong(IReadOnlyList<Vector2> pts, float step)
    {
        float len = Length(pts);
        for (float at = 0f; at < len; at += step) yield return PointAt(pts, at).P;
        yield return pts[^1];
    }

    static float DistToPolyline(Vector2 p, IReadOnlyList<Vector2> pts)
    {
        float best = float.MaxValue;
        for (int i = 0; i + 1 < pts.Count; i++)
        {
            var a = pts[i]; var ab = pts[i + 1] - a;
            float l2 = ab.LengthSquared();
            float t = l2 > 0 ? Math.Clamp(Vector2.Dot(p - a, ab) / l2, 0f, 1f) : 0f;
            best = MathF.Min(best, Vector2.Distance(p, a + ab * t));
        }
        if (pts.Count == 1) best = Vector2.Distance(p, pts[0]);
        return best;
    }

    static float AngDiff(float a, float b)
    {
        float d = MathF.Abs(a - b) % 360f;
        return d > 180f ? 360f - d : d;
    }

    static bool InRange(NavmeshGeometry nv, NavTri t)
        => t.A >= 0 && t.B >= 0 && t.C >= 0 && t.A < nv.Vertices.Count && t.B < nv.Vertices.Count && t.C < nv.Vertices.Count;

    static (Vector2 A, Vector2 B, Vector2 C) Plan(NavmeshGeometry nv, NavTri t)
        => (new Vector2(nv.Vertices[t.A].X, nv.Vertices[t.A].Y), new Vector2(nv.Vertices[t.B].X, nv.Vertices[t.B].Y),
            new Vector2(nv.Vertices[t.C].X, nv.Vertices[t.C].Y));

    internal static (int X, int Y) SquareOf(float x, float y)
        => ((int)MathF.Floor(x / GridSize), (int)MathF.Floor(y / GridSize));

    static RouteGeometry.D2 D(Vector2 v) => new(v.X, v.Y);

    internal static string Sha256Hex(string s)
        => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(s))).ToLowerInvariant();
}

/// <summary>The exact plan geometry the route mode's area and coverage stand on.</summary>
internal static class RouteGeometry
{
    internal readonly record struct D2(double X, double Y)
    {
        public static D2 operator +(D2 a, D2 b) => new(a.X + b.X, a.Y + b.Y);
        public static D2 operator -(D2 a, D2 b) => new(a.X - b.X, a.Y - b.Y);
        public static D2 operator *(D2 a, double s) => new(a.X * s, a.Y * s);
    }

    const double TwoPi = 2.0 * Math.PI;

    internal static double Cross(Vector2 a, Vector2 b, Vector2 c)
        => ((double)b.X - a.X) * ((double)c.Y - a.Y) - ((double)b.Y - a.Y) * ((double)c.X - a.X);

    static double Cross(D2 a, D2 b) => a.X * b.Y - a.Y * b.X;
    static double Dot(D2 a, D2 b) => a.X * b.X + a.Y * b.Y;
    static double Len(D2 a) => Math.Sqrt(a.X * a.X + a.Y * a.Y);

    /// <summary>Plan containment, edges included, either winding.</summary>
    internal static bool Contains(D2 a, D2 b, D2 c, D2 p)
    {
        double d1 = Cross(b - a, p - a), d2 = Cross(c - b, p - b), d3 = Cross(a - c, p - c);
        bool neg = d1 < 0 || d2 < 0 || d3 < 0, pos = d1 > 0 || d2 > 0 || d3 > 0;
        return !(neg && pos);
    }

    static double DistToSegment(D2 p, D2 a, D2 b)
    {
        var ab = b - a; double l2 = Dot(ab, ab);
        double t = l2 > 0 ? Math.Clamp(Dot(p - a, ab) / l2, 0.0, 1.0) : 0.0;
        return Len(p - (a + ab * t));
    }

    static double DistToTriangle(D2 p, D2 a, D2 b, D2 c)
        => Contains(a, b, c, p) ? 0.0 : Math.Min(DistToSegment(p, a, b), Math.Min(DistToSegment(p, b, c), DistToSegment(p, c, a)));

    // ---- interval arithmetic ------------------------------------------------------------------

    /// <summary>Sorted, merged union of closed intervals.</summary>
    static List<(double A, double B)> Union(List<(double A, double B)> xs)
    {
        xs.Sort((p, q) => p.A.CompareTo(q.A));
        var r = new List<(double A, double B)>();
        foreach (var x in xs)
        {
            if (x.B <= x.A) continue;
            if (r.Count > 0 && x.A <= r[^1].B) r[^1] = (r[^1].A, Math.Max(r[^1].B, x.B));
            else r.Add(x);
        }
        return r;
    }

    static List<(double A, double B)> Intersect(List<(double A, double B)> a, List<(double A, double B)> b)
    {
        var r = new List<(double A, double B)>();
        int i = 0, j = 0;
        while (i < a.Count && j < b.Count)
        {
            double lo = Math.Max(a[i].A, b[j].A), hi = Math.Min(a[i].B, b[j].B);
            if (hi > lo) r.Add((lo, hi));
            if (a[i].B < b[j].B) i++; else j++;
        }
        return r;
    }

    static List<(double A, double B)> Complement(List<(double A, double B)> u, double lo, double hi)
    {
        var r = new List<(double A, double B)>();
        double at = lo;
        foreach (var (a, b) in u)
        {
            if (a > at) r.Add((at, a));
            at = Math.Max(at, b);
        }
        if (at < hi) r.Add((at, hi));
        return r;
    }

    static readonly List<(double A, double B)> FullCircle = new() { (0.0, TwoPi) };

    /// <summary>Add the angle interval [start, start + width] (width in [0, 2 pi]) in [0, 2 pi) pieces.</summary>
    static void AddAngles(List<(double A, double B)> into, double start, double width)
    {
        if (width >= TwoPi) { into.Add((0.0, TwoPi)); return; }
        if (width <= 0) return;
        double s = start % TwoPi; if (s < 0) s += TwoPi;
        double e = s + width;
        if (e <= TwoPi) into.Add((s, e));
        else { into.Add((s, TwoPi)); into.Add((0.0, e - TwoPi)); }
    }

    /// <summary>The angles of circle (c, r) inside disc (d, s), closed.</summary>
    static void CoveredAngles(D2 c, double r, D2 d, double s, List<(double A, double B)> into)
    {
        var v = d - c; double dist = Len(v);
        if (dist + r <= s) { into.Add((0.0, TwoPi)); return; }
        if (dist >= r + s || dist + s <= r) return;
        double cosA = Math.Clamp((r * r + dist * dist - s * s) / (2.0 * r * dist), -1.0, 1.0);
        double alpha = Math.Acos(cosA), phi = Math.Atan2(v.Y, v.X);
        AddAngles(into, phi - alpha, 2.0 * alpha);
    }

    /// <summary>The parameters t in [0, 1] of segment p->q inside disc (d, s), closed.</summary>
    static void CoveredParams(D2 p, D2 q, D2 d, double s, List<(double A, double B)> into)
    {
        var v = q - p; var w = p - d;
        double a = Dot(v, v), b = 2.0 * Dot(v, w), cc = Dot(w, w) - s * s;
        if (a <= 0) { if (cc <= 0) into.Add((0.0, 1.0)); return; }
        double disc = b * b - 4.0 * a * cc;
        if (disc <= 0) return;
        double sq = Math.Sqrt(disc);
        double t1 = Math.Max(0.0, (-b - sq) / (2.0 * a)), t2 = Math.Min(1.0, (-b + sq) / (2.0 * a));
        if (t2 > t1) into.Add((t1, t2));
    }

    /// <summary>The angles of circle (c, r) on the inner side of each edge of CCW triangle a, b, c.</summary>
    static List<(double A, double B)> InsideTriangle(D2 cen, double r, D2 a, D2 b, D2 c)
    {
        List<(double A, double B)> acc = FullCircle;
        foreach (var (p, q) in new[] { (a, b), (b, c), (c, a) })
        {
            var e = q - p; double le = Len(e);
            if (le <= 0) continue;
            // inner: cross(e, cen + r u - p) >= 0  <=>  sin(theta - psi) >= k
            double k = -Cross(e, cen - p) / (r * le);
            var one = new List<(double A, double B)>();
            if (k <= -1.0) one.Add((0.0, TwoPi));
            else if (k < 1.0)
            {
                double psi = Math.Atan2(e.Y, e.X), asn = Math.Asin(k);
                AddAngles(one, psi + asn, Math.PI - 2.0 * asn);
            }
            acc = Intersect(acc, Union(one));
            if (acc.Count == 0) break;
        }
        return acc;
    }

    /// <summary>A union of equal discs, bucketed, with each circle's own-union boundary memoised.</summary>
    internal sealed class DiscSet
    {
        public readonly double R;
        public readonly D2[] C;
        readonly double _cell;
        readonly Dictionary<(long, long), List<int>> _grid = new();
        readonly Dictionary<int, List<(double A, double B)>> _boundary = new();
        public int Count => C.Length;

        public DiscSet(IEnumerable<D2> centers, double r)
        {
            R = r;
            var seen = new HashSet<(double, double)>();
            var list = new List<D2>();
            foreach (var c in centers) if (seen.Add((c.X, c.Y))) list.Add(c);   // identical centres merged
            C = list.ToArray();
            _cell = Math.Max(2.0 * r, 1.0);
            for (int i = 0; i < C.Length; i++)
            {
                var k = Cell(C[i].X, C[i].Y);
                if (!_grid.TryGetValue(k, out var l)) _grid[k] = l = new List<int>();
                l.Add(i);
            }
        }

        (long, long) Cell(double x, double y) => ((long)Math.Floor(x / _cell), (long)Math.Floor(y / _cell));

        public IEnumerable<int> InBox(double x0, double y0, double x1, double y1)
        {
            var (cx0, cy0) = Cell(x0, y0); var (cx1, cy1) = Cell(x1, y1);
            for (long gx = cx0; gx <= cx1; gx++)
                for (long gy = cy0; gy <= cy1; gy++)
                    if (_grid.TryGetValue((gx, gy), out var l))
                        foreach (int i in l)
                        {
                            var c = C[i];
                            if (c.X >= x0 && c.X <= x1 && c.Y >= y0 && c.Y <= y1) yield return i;
                        }
        }

        public bool Contains(double x, double y)
        {
            foreach (int i in InBox(x - R, y - R, x + R, y + R))
            {
                double dx = C[i].X - x, dy = C[i].Y - y;
                if (dx * dx + dy * dy <= R * R) return true;
            }
            return false;
        }

        /// <summary>The arcs of circle i that no OTHER disc of this set covers: its share of the union's boundary.</summary>
        public List<(double A, double B)> Boundary(int i)
        {
            if (_boundary.TryGetValue(i, out var b)) return b;
            var cov = new List<(double A, double B)>();
            var c = C[i];
            foreach (int j in InBox(c.X - 2 * R, c.Y - 2 * R, c.X + 2 * R, c.Y + 2 * R))
                if (j != i) CoveredAngles(c, R, C[j], R, cov);
            b = Complement(Union(cov), 0.0, TwoPi);
            _boundary[i] = b;
            return b;
        }

        /// <summary>The angles of an arbitrary circle (c, r) this union covers.</summary>
        public List<(double A, double B)> CoveredOn(D2 c, double r)
        {
            var cov = new List<(double A, double B)>();
            double m = r + R;
            foreach (int j in InBox(c.X - m, c.Y - m, c.X + m, c.Y + m)) CoveredAngles(c, r, C[j], R, cov);
            return Union(cov);
        }

        /// <summary>The parameters of segment p->q this union covers.</summary>
        public List<(double A, double B)> ChordsOn(D2 p, D2 q)
        {
            var cov = new List<(double A, double B)>();
            foreach (int j in InBox(Math.Min(p.X, q.X) - R, Math.Min(p.Y, q.Y) - R, Math.Max(p.X, q.X) + R, Math.Max(p.Y, q.Y) + R))
                CoveredParams(p, q, C[j], R, cov);
            return Union(cov);
        }

        /// <summary>For a triangle: -1 no disc meets it, 1 one disc holds it whole, 0 partial.</summary>
        public int Relation(D2 a, D2 b, D2 c)
        {
            double x0 = Math.Min(a.X, Math.Min(b.X, c.X)) - R, x1 = Math.Max(a.X, Math.Max(b.X, c.X)) + R;
            double y0 = Math.Min(a.Y, Math.Min(b.Y, c.Y)) - R, y1 = Math.Max(a.Y, Math.Max(b.Y, c.Y)) + R;
            bool meets = false; double r2 = R * R;
            foreach (int i in InBox(x0, y0, x1, y1))
            {
                var ci = C[i];
                double da = (a.X - ci.X) * (a.X - ci.X) + (a.Y - ci.Y) * (a.Y - ci.Y);
                double db = (b.X - ci.X) * (b.X - ci.X) + (b.Y - ci.Y) * (b.Y - ci.Y);
                double dc = (c.X - ci.X) * (c.X - ci.X) + (c.Y - ci.Y) * (c.Y - ci.Y);
                if (da <= r2 && db <= r2 && dc <= r2) return 1;
                if (!meets && DistToTriangle(ci, a, b, c) <= R) meets = true;
            }
            return meets ? 0 : -1;
        }

        public IEnumerable<int> Meeting(D2 a, D2 b, D2 c)
        {
            double x0 = Math.Min(a.X, Math.Min(b.X, c.X)) - R, x1 = Math.Max(a.X, Math.Max(b.X, c.X)) + R;
            double y0 = Math.Min(a.Y, Math.Min(b.Y, c.Y)) - R, y1 = Math.Max(a.Y, Math.Max(b.Y, c.Y)) + R;
            foreach (int i in InBox(x0, y0, x1, y1))
                if (DistToTriangle(C[i], a, b, c) <= R) yield return i;
        }
    }

    /// <summary>The plan area of triangle a, b, c ∩ (union U1, or everything when null) ∩ (union U2, or
    /// everything when null), exactly: Green's theorem over the boundary pieces.</summary>
    internal static double RegionArea(D2 a, D2 b, D2 c, DiscSet? u1, DiscSet? u2)
    {
        if (Cross(b - a, c - a) < 0) (b, c) = (c, b);
        double full = Cross(b - a, c - a) / 2.0;
        if (full <= 0) return 0.0;
        if (u1 is not null && u1.Count == 0) return 0.0;
        if (u2 is not null && u2.Count == 0) return 0.0;
        if (u1 is not null) { int rel = u1.Relation(a, b, c); if (rel < 0) return 0.0; if (rel > 0) u1 = null; }
        if (u2 is not null) { int rel = u2.Relation(a, b, c); if (rel < 0) return 0.0; if (rel > 0) u2 = null; }
        if (u1 is null && u2 is null) return full;

        var o = a;   // measured about the first vertex, for precision
        double sum = 0.0;
        foreach (var (p, q) in new[] { (a, b), (b, c), (c, a) })
        {
            var iv = new List<(double A, double B)> { (0.0, 1.0) };
            if (u1 is not null) iv = Intersect(iv, u1.ChordsOn(p, q));
            if (u2 is not null && iv.Count > 0) iv = Intersect(iv, u2.ChordsOn(p, q));
            foreach (var (t1, t2) in iv)
            {
                var p1 = p + (q - p) * t1 - o; var p2 = p + (q - p) * t2 - o;
                sum += 0.5 * (p1.X * p2.Y - p2.X * p1.Y);
            }
        }
        sum += Arcs(u1, u2, a, b, c, o);
        sum += Arcs(u2, u1, a, b, c, o);
        return Math.Clamp(sum, 0.0, full);
    }

    /// <summary>The boundary arcs of union <paramref name="own"/> inside the triangle and inside
    /// <paramref name="other"/> (everything when null), integrated.</summary>
    static double Arcs(DiscSet? own, DiscSet? other, D2 a, D2 b, D2 c, D2 o)
    {
        if (own is null) return 0.0;
        double sum = 0.0, r = own.R;
        foreach (int i in own.Meeting(a, b, c))
        {
            var cen = own.C[i];
            var arcs = InsideTriangle(cen, r, a, b, c);
            if (arcs.Count == 0) continue;
            arcs = Intersect(arcs, own.Boundary(i));
            if (arcs.Count == 0) continue;
            if (other is not null) arcs = Intersect(arcs, other.CoveredOn(cen, r));
            double cx = cen.X - o.X, cy = cen.Y - o.Y;
            foreach (var (t1, t2) in arcs)
                sum += 0.5 * (r * r * (t2 - t1) + cx * r * (Math.Sin(t2) - Math.Sin(t1)) - cy * r * (Math.Cos(t2) - Math.Cos(t1)));
        }
        return sum;
    }

    /// <summary>A bucketed set of points, for "is any accepted station within d".</summary>
    internal sealed class PointGrid
    {
        readonly double _cell;
        readonly Dictionary<(long, long), List<Vector2>> _g = new();
        public readonly List<Vector2> All = new();
        public PointGrid(double cell) => _cell = cell;
        (long, long) K(double x, double y) => ((long)Math.Floor(x / _cell), (long)Math.Floor(y / _cell));
        public void Add(Vector2 p)
        {
            var k = K(p.X, p.Y);
            if (!_g.TryGetValue(k, out var l)) _g[k] = l = new List<Vector2>();
            l.Add(p); All.Add(p);
        }
        public bool AnyWithin(Vector2 p, float d)
        {
            var (x0, y0) = K(p.X - d, p.Y - d); var (x1, y1) = K(p.X + d, p.Y + d);
            double d2 = (double)d * d;
            for (long gx = x0; gx <= x1; gx++)
                for (long gy = y0; gy <= y1; gy++)
                    if (_g.TryGetValue((gx, gy), out var l))
                        foreach (var q in l)
                        {
                            double dx = q.X - p.X, dy = q.Y - p.Y;
                            if (dx * dx + dy * dy < d2) return true;
                        }
            return false;
        }
    }

    /// <summary>Navmeshes bucketed by their plan bounds (from their vertices), for point queries.</summary>
    internal sealed class MeshBuckets
    {
        const float Cell = 1024f;
        readonly Dictionary<(int, int), List<int>> _g = new();
        readonly List<(NavmeshGeometry Nav, float X0, float Y0, float X1, float Y1)> _m = new();

        public MeshBuckets(IEnumerable<NavmeshGeometry> navs)
        {
            foreach (var nv in navs.OrderBy(n => n.FormKey.ToString(), StringComparer.Ordinal))
            {
                if (nv.Vertices.Count == 0) continue;
                float x0 = float.MaxValue, y0 = float.MaxValue, x1 = float.MinValue, y1 = float.MinValue;
                foreach (var v in nv.Vertices) { x0 = MathF.Min(x0, v.X); y0 = MathF.Min(y0, v.Y); x1 = MathF.Max(x1, v.X); y1 = MathF.Max(y1, v.Y); }
                int id = _m.Count; _m.Add((nv, x0, y0, x1, y1));
                for (int gx = (int)MathF.Floor(x0 / Cell); gx <= (int)MathF.Floor(x1 / Cell); gx++)
                    for (int gy = (int)MathF.Floor(y0 / Cell); gy <= (int)MathF.Floor(y1 / Cell); gy++)
                    {
                        if (!_g.TryGetValue((gx, gy), out var l)) _g[(gx, gy)] = l = new List<int>();
                        l.Add(id);
                    }
            }
        }

        public IReadOnlyList<NavmeshGeometry> Near(float x, float y, float r)
        {
            var ids = new SortedSet<int>();
            for (int gx = (int)MathF.Floor((x - r) / Cell); gx <= (int)MathF.Floor((x + r) / Cell); gx++)
                for (int gy = (int)MathF.Floor((y - r) / Cell); gy <= (int)MathF.Floor((y + r) / Cell); gy++)
                    if (_g.TryGetValue((gx, gy), out var l))
                        foreach (int id in l)
                        {
                            var m = _m[id];
                            if (m.X0 <= x + r && m.X1 >= x - r && m.Y0 <= y + r && m.Y1 >= y - r) ids.Add(id);
                        }
            var res = new List<NavmeshGeometry>(ids.Count);
            foreach (int id in ids) res.Add(_m[id].Nav);
            return res;
        }
    }
}
