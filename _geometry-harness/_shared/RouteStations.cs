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
//  API STUB (the red-first commit). Every body below is a deliberately
//  WRONG answer of the kind each proofprobe RT case rejects: the area is
//  every triangle, whole; walkable is every triangle; the changed set is
//  the winner's position only; the sampler puts one accepted station on
//  every triangle's centroid; coverage counts triangles; no per-candidate
//  check refuses anything. The cases fail on those answers, not on a
//  missing member.
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
    public double AreaA { get { double a = 0; foreach (var t in Triangles.Values) if (t.InArea) a += t.AreaClipped; return a; } }
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
    public const int FlagInitiallyDisabled = 0x0800;
    public const int FlagDeleted = 0x0020;
    public const string FillSeed = "g5-fill:20260925";
    public const string RetrySeed = "g5-retry:20260925";

    /// <summary>§2-§3: X_W, the squares, the loaded set, walkable and A_W.</summary>
    public static RouteArea Classify(RouteWorld w)
    {
        // STUB: every triangle of every navmesh, whole, walkable; X_W from the winner's position only.
        var xs = new List<RouteXPoint>();
        foreach (var p in w.CityPlaced)
            if (p.Versions.Count > 0 && p.Versions[^1].Position is { } pos)
                xs.Add(new RouteXPoint(new Vector2(pos.X, pos.Y), p.FormKey, p.BaseHasModel, "winner"));
        var all = new Dictionary<FormKey, NavmeshGeometry>();
        foreach (var n in w.Navmeshes) all.TryAdd(n.Mesh.FormKey, n.Mesh);
        var (nodes, report) = NavGraph.Build(all);
        var tris = new Dictionary<NavGraph.TriRef, RouteTriangle>();
        foreach (var nv in all.Values)
            for (int i = 0; i < nv.Triangles.Count; i++)
            {
                var t = nv.Triangles[i];
                var a = P2(nv, t.A); var b = P2(nv, t.B); var c = P2(nv, t.C);
                double area = Math.Abs(Cross(b - a, c - a)) / 2.0;
                tris[new(nv.FormKey, i)] = new RouteTriangle(new(nv.FormKey, i), true, true, 0, true,
                    false, false, false, t.Flags, a, b, c, area, area);
            }
        var squares = new HashSet<(int, int)>();
        return new RouteArea
        {
            World = w, XPoints = xs, AreaSquares = squares, LoadedSquares = squares,
            Loaded = all, Winning = all, Nodes = nodes, Report = report, Triangles = tris,
        };
    }

    /// <summary>§5: the per-candidate checks on one XY, in the sealed precedence.</summary>
    public static RouteTestVerdict Test(RouteArea area, Func<float, float, RouteEnv> environment, float x, float y)
    {
        // STUB: nothing is refused.
        var e = environment(x, y);
        return new RouteTestVerdict(StationRejection.None, "", e.Env, e.Covering,
                                e.Env.NavZ + StationRule.EyeHeight, null, 0);
    }

    /// <summary>§4 and §6: route and fill candidates, each tested, with the gaze set per accepted station.</summary>
    public static RouteResult Sample(RouteArea area, Func<float, float, RouteEnv> environment)
    {
        // STUB: one accepted fill station on every walkable triangle's centroid.
        var r = new RouteResult();
        var y = new RouteYield { Family = RouteFamily.Fill };
        r.Yields[RouteFamily.Fill] = y;
        r.Yields[RouteFamily.Route] = new RouteYield { Family = RouteFamily.Route };
        r.Yields[RouteFamily.FillRetry] = new RouteYield { Family = RouteFamily.FillRetry };
        int n = 0;
        foreach (var t in area.Triangles.Values)
        {
            if (!t.Walkable) continue;
            var c = (t.A + t.B + t.C) / 3f;
            var v = Test(area, environment, c.X, c.Y);
            y.Composed++; y.Accepted++;
            r.Stations.Add(new RouteStation
            {
                Id = $"{area.World.WorldspaceEditorId}:F{n++:D5}", Family = RouteFamily.Fill,
                X = c.X, Y = c.Y, Accepted = true, Verdict = v, SourceTri = t.Tri, SubPath = "",
                Gazes = new[] { new RouteGaze(RouteGazeKind.Content, 0f, new Vector3(c.X, c.Y + 1f, v.EyeZ)) },
            });
        }
        return r;
    }

    /// <summary>§6 G-content: the bearing (every 5 deg, 0 = +Y clockwise) whose level 80 deg frustum
    /// holds the most of <paramref name="points"/> within the content reach; ties to the bearing
    /// nearest <paramref name="fwdBearingDeg"/>, then the smaller bearing.</summary>
    public static float ContentBearing(Vector2 eye, IReadOnlyList<Vector2> points, float? fwdBearingDeg)
        => fwdBearingDeg ?? 0f;   // STUB: path-only gaze

    /// <summary>§7: the part of A_W within D of at least one of <paramref name="stations"/>.</summary>
    public static RouteCoverage Coverage(RouteArea area, IEnumerable<Vector2> stations,
                                         IReadOnlyList<RouteStation>? refused = null)
    {
        // STUB: triangle-count coverage.
        var st = stations.ToList();
        int total = 0, hit = 0;
        foreach (var t in area.Triangles.Values)
        {
            if (!t.InArea) continue;
            total++;
            foreach (var s in st) if (Inside(s, t.A, t.B, t.C)) { hit++; break; }
        }
        double a = area.AreaA;
        return new RouteCoverage { AreaA = a, Covered = total > 0 ? a * hit / total : 0.0 };
    }

    /// <summary>§7 L1: coverage by the ACCEPTED stations of a sampling.</summary>
    public static RouteCoverage CoverageL1(RouteArea area, RouteResult result)
    {
        var pts = new List<Vector2>();
        foreach (var s in result.Stations) pts.Add(new Vector2(s.X, s.Y));   // STUB: refused counted too
        return Coverage(area, pts, result.Stations);
    }

    /// <summary>§4.2 step 3: the fill order key, lower-case hex.</summary>
    public static string FillKey(FormKey mesh, int tri, string subPath) => "";   // STUB

    /// <summary>§4.2 step 5: alternate j (1..6) inside the sub-triangle A, B, C (stored vertex order).</summary>
    public static Vector2 RetryPoint(FormKey mesh, int tri, string subPath, int j, Vector2 a, Vector2 b, Vector2 c)
        => (a + b + c) / 3f;   // STUB

    // ---- helpers ---------------------------------------------------------------------------------
    static Vector2 P2(NavmeshGeometry nv, short i)
        => i >= 0 && i < nv.Vertices.Count ? new Vector2(nv.Vertices[i].X, nv.Vertices[i].Y) : Vector2.Zero;

    static double Cross(Vector2 a, Vector2 b) => (double)a.X * b.Y - (double)a.Y * b.X;

    static bool Inside(Vector2 p, Vector2 a, Vector2 b, Vector2 c)
    {
        double d1 = Cross(b - a, p - a), d2 = Cross(c - b, p - b), d3 = Cross(a - c, p - c);
        bool neg = d1 < 0 || d2 < 0 || d3 < 0, pos = d1 > 0 || d2 > 0 || d3 > 0;
        return !(neg && pos);
    }

    internal static string Sha256Hex(string s)
        => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(s))).ToLowerInvariant();
}
