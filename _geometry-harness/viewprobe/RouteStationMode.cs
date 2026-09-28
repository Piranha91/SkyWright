using System.Collections.Concurrent;
using System.Numerics;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using HarnessShared;
using HousecarlCore;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Skyrim;

namespace ViewProbe;

// ======================================================================
//  --stations with "mode": "route" - the station rule's route mode: player
//  camera stations sampled from the walkable navmesh around what ONE plugin
//  changes in a worldspace, and the coverage of that ground.
//
//  The rule is HarnessShared.RouteStations (proofprobe's RT cases); this
//  file is the plumbing between it and the load order, and the raw dump.
//
//  LOADING (design (a)): one process per worldspace. The area is not known
//  until the changed plugin's placed records and their masters' positions
//  are read, so the renderer's one-cell walk cannot bound it; this mode
//  runs its own pass over the worldspace's cells after that walk:
//    1. the changed plugin alone, for what it carries in the worldspace;
//    2. the arm in load order, over the worldspace's cells (and a child
//       worldspace's navmeshes, for the footprint refusal): every winning
//       navmesh with its plugin chain and grid square, every version of
//       the changed plugin's placed records read RAW (a deleted version
//       with no position is kept), the floor refs and terrain filed by grid
//       within a bound, the cells and their water, the occlusion primitives.
//  From the renderer's walk it reuses what is already worldwide: base model
//  paths, the enable state and the floor-solid lookup. The floor is filed
//  within a bound around the changed plugin's own content; a loaded square
//  outside it is REFUSED, never composed on a partial floor.
//
//  THE DUMP (navdump-<worldspace>.json) is written from the same raw pass
//  by code that classifies nothing: an independent oracle recomputes the
//  area, walkable, the three area refusals and the coverage from it.
// ======================================================================

static class RouteStationMode
{
    public sealed record Inputs
    {
        public required string SpecPath { get; init; }
        public required string OutPath { get; init; }
        public required string CommandLine { get; init; }
        /// <summary>The worldspace of the target cell: the one this run samples.</summary>
        public required FormKey Worldspace { get; init; }
        public required IReadOnlyList<string> OrderedPaths { get; init; }
        /// <summary>A base record's winning model path, or null (the renderer walk's own table).</summary>
        public required Func<FormKey, string?> BaseModel { get; init; }
        public required FloorFeed.GeometryOf Geometry { get; init; }
        public required Func<FormKey, bool> Enabled { get; init; }
        /// <summary>The base every occlusion primitive (PortalBox, Box) is placed from.</summary>
        public required FormKey PlaneMarkerBase { get; init; }
    }

    /// <summary>Whether a --stations spec asks for this mode. An unreadable spec is not a route spec:
    /// the face-on path then reports why it cannot be read.</summary>
    public static bool IsRouteSpec(string path)
    {
        try
        {
            using var d = JsonDocument.Parse(File.ReadAllText(path));
            return d.RootElement.TryGetProperty("mode", out var m) && m.ValueKind == JsonValueKind.String
                   && m.GetString() == "route";
        }
        catch { return false; }
    }

    static readonly JsonSerializerOptions Indented = new() { WriteIndented = true };
    static readonly JsonSerializerOptions Compact = new() { WriteIndented = false };

    public static int Run(Inputs input)
    {
        // ---- the spec -------------------------------------------------------------------------------
        string specText = File.ReadAllText(input.SpecPath);
        string setup, label, changed;
        var children = new List<FormKey>();
        int margin = 4; bool dumpAll = false;
        var levels = new List<(string Name, HashSet<string> Ids, Dictionary<string, string> Reasons)>();
        try
        {
            using var spec = JsonDocument.Parse(specText);
            var root = spec.RootElement;
            setup = Str(root, "setup") ?? "";
            label = Str(root, "label") ?? setup;
            changed = Str(root, "changedPlugin") ?? "";
            if (setup.Length == 0 || changed.Length == 0)
            {
                Console.Error.WriteLine("--stations (route): the spec needs \"setup\" (the arm's label) and \"changedPlugin\" "
                                      + "(the plugin whose changes the area is built around).");
                return 2;
            }
            if (root.TryGetProperty("childWorldspaces", out var cw) && cw.ValueKind == JsonValueKind.Array)
                foreach (var e in cw.EnumerateArray())
                    if (e.GetString() is { Length: > 0 } s) children.Add(FormKey.Factory(s));
            if (root.TryGetProperty("fileMarginSquares", out var fm)) margin = fm.GetInt32();
            if (root.TryGetProperty("dumpAllNavmeshes", out var da)) dumpAll = da.GetBoolean();
            // Later coverage levels (L2-L4): each names the station set that supports it, inline or as
            // a path to a JSON file of the same shape: { "stations": [ids], "droppedReasons": { id: reason } }.
            if (root.TryGetProperty("coverageLevels", out var cl) && cl.ValueKind == JsonValueKind.Object)
                foreach (var lvl in cl.EnumerateObject())
                {
                    JsonDocument? owned = null;
                    var body = lvl.Value;
                    if (body.ValueKind == JsonValueKind.String)
                    {
                        owned = JsonDocument.Parse(File.ReadAllText(body.GetString()!));
                        body = owned.RootElement;
                    }
                    var ids = new HashSet<string>(StringComparer.Ordinal);
                    var reasons = new Dictionary<string, string>(StringComparer.Ordinal);
                    if (body.TryGetProperty("stations", out var st))
                        foreach (var e in st.EnumerateArray()) if (e.GetString() is { } id) ids.Add(id);
                    if (body.TryGetProperty("droppedReasons", out var dr))
                        foreach (var e in dr.EnumerateObject()) reasons[e.Name] = e.Value.GetString() ?? "";
                    levels.Add((lvl.Name, ids, reasons));
                    owned?.Dispose();
                }
            // The sampling parameters are SEALED in the rule. A spec may state them, and each stated one is
            // checked against the constant and refused if it differs.
            foreach (var (name, value) in new (string, float)[]
            {
                ("rArea", RouteStations.RArea), ("spacing", RouteStations.Spacing),
                ("coverageDistance", RouteStations.CoverageDistance), ("eyeHeight", StationRule.EyeHeight),
                ("retryAlternates", RouteStations.RetryAlternates), ("fovDeg", StationRule.DefaultFovDeg),
            })
                if (root.TryGetProperty(name, out var v) && MathF.Abs(v.GetSingle() - value) > 1e-4f)
                {
                    Console.Error.WriteLine($"--stations (route): the spec asks for {name} {v.GetSingle()}, but the sealed "
                                          + $"value is {value}. The parameters are part of the rule; refusing.");
                    return 2;
                }
        }
        catch (Exception ex) when (ex is JsonException or FormatException or InvalidOperationException or ArgumentException)
        {
            Console.Error.WriteLine($"--stations (route): {input.SpecPath} is not a usable route spec: {ex.Message}");
            return 2;
        }

        string? changedPath = input.OrderedPaths.FirstOrDefault(p =>
            string.Equals(Path.GetFileName(p), changed, StringComparison.OrdinalIgnoreCase));
        if (changedPath is null)
        {
            Console.Error.WriteLine($"--stations (route): {changed} is not in this run's load order; refusing.");
            return 2;
        }

        // ---- the pass ---------------------------------------------------------------------------------
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var walk = new Walk(input.Worldspace, children, input.PlaneMarkerBase);
        // A throw while a plugin's records are read is a named refusal, as in every other walk (PluginRead.cs).
        try { walk.PreRead(changedPath, margin); }
        catch (Exception ex) when (PluginRead.Wraps(ex)) { throw PluginRead.Failed(changedPath, -1, 0, "--stations (route) pre-read", ex); }
        foreach (var path in input.OrderedPaths)
        {
            try { walk.Plugin(path); }
            catch (Exception ex) when (PluginRead.Wraps(ex)) { throw PluginRead.Failed(path, -1, 0, "--stations (route) worldspace pass", ex); }
        }
        Console.WriteLine($"   --stations (route): worldspace pass over {input.OrderedPaths.Count} plugin(s) in "
                        + $"{sw.Elapsed.TotalSeconds:0.0}s: {walk.NavWin.Count:N0} winning navmesh(es), "
                        + $"{walk.CityPlacedKeys.Count:N0} placed record(s) and {walk.CityNavKeys.Count:N0} navmesh(es) "
                        + $"carried by {changed}; floor filed over {walk.Bound.Count} grid square(s)");
        walk.DumpWinState();

        string ed = walk.WsEditorId.Length > 0 ? walk.WsEditorId : input.Worldspace.ToString().Replace(':', '_');
        var world = walk.World(ed, changed, input.OrderedPaths.Select(p => Path.GetFileName(p)).ToList(), input.BaseModel);
        var area = RouteStations.Classify(world);

        // The floor bound: every loaded square and its 3x3 floor neighbourhood must have been filed.
        var missing = new SortedSet<(int, int)>();
        foreach (var (x, y) in area.LoadedSquares)
            for (int dx = -1; dx <= 1; dx++)
                for (int dy = -1; dy <= 1; dy++)
                    if (!walk.Bound.Contains((x + dx, y + dy))) missing.Add((x + dx, y + dy));
        if (missing.Count > 0)
        {
            Console.Error.WriteLine($"--stations (route): REFUSED. {missing.Count} grid square(s) the loaded set's floor needs lie "
                                  + $"outside the {margin}-square bound the floor was filed within (a master position far from "
                                  + $"{changed}'s own content): {string.Join(" ", missing.Take(12).Select(s => $"{s.Item1},{s.Item2}"))}"
                                  + (missing.Count > 12 ? " ..." : "") + ". Raise \"fileMarginSquares\" in the spec.");
            return 1;
        }

        // ---- sample ---------------------------------------------------------------------------------------
        var reader = new StationMode.EnvironmentReader(p => area.MeshesNear(p.X, p.Y, 1f), walk.RefsByGrid, walk.LandByGrid,
            input.Geometry, input.Enabled,
            g => walk.Cells.TryGetValue(g, out var c)
                ? new GridCellFacts(c.Cell, input.Worldspace.ToString(), c.Water) : null,
            name => walk.WorldChain.TryGetValue(name, out var wc) ? new WorldFacts(wc.Default, wc.Parent) : null,
            input.Worldspace.ToString(), walk.UnreadableLandWhy);
        RouteEnv Env(float x, float y) { var (e, t) = reader.Read(x, y); return new RouteEnv(e, t); }
        sw.Restart();
        var result = RouteStations.Sample(area, Env);
        var cov = RouteStations.CoverageL1(area, result);
        var routeCov = RouteStations.Coverage(area,
            result.Stations.Where(s => s.Accepted && s.Family == RouteFamily.Route).Select(s => new Vector2(s.X, s.Y)));
        var levelDocs = new Dictionary<string, object?>();
        foreach (var (name, ids, reasons) in levels)
        {
            var inLevel = result.Stations.Where(s => s.Accepted && ids.Contains(s.Id)).ToList();
            var unknown = ids.Where(id => !result.Stations.Any(s => s.Accepted && s.Id == id)).ToList();
            var lc = RouteStations.Coverage(area, inLevel.Select(s => new Vector2(s.X, s.Y)), result.Stations,
                s => s.Accepted && !ids.Contains(s.Id) ? (reasons.TryGetValue(s.Id, out var why) ? why : "NotInLevel") : null);
            levelDocs[name] = new Dictionary<string, object?>
            {
                ["stations"] = inLevel.Count, ["idsNotAcceptedAtL1"] = unknown,
                ["areaA"] = lc.AreaA, ["covered"] = lc.Covered, ["fraction"] = lc.Fraction,
                ["uncoveredByReason"] = lc.UncoveredByReason, ["uncoveredPieces"] = lc.Pieces.Count,
                ["largestUncoveredPieces"] = lc.Pieces.Take(20).Select(p => new Dictionary<string, object?>
                { ["area"] = p.Area, ["x"] = p.Centroid.X, ["y"] = p.Centroid.Y, ["triangles"] = p.Triangles, ["reason"] = p.Reason }).ToList(),
            };
            Console.WriteLine($"   --stations (route): coverage level {name}: {inLevel.Count} station(s), {lc.Fraction:P2}"
                            + (unknown.Count > 0 ? $"; {unknown.Count} listed id(s) are not accepted stations of this run" : ""));
        }
        double ownArea = 0, ownCovered = 0;
        foreach (var t in area.Triangles.Values)
            if (t.InArea && t.CityOwn) { ownArea += t.AreaClipped; ownCovered += cov.CoveredPerTriangle.TryGetValue(t.Tri, out var c) ? c : 0; }
        Console.WriteLine($"   --stations (route): sampled in {sw.Elapsed.TotalSeconds:0.0}s");

        // ---- write ---------------------------------------------------------------------------------------
        string dir = Path.GetDirectoryName(Path.GetFullPath(input.OutPath)) ?? ".";
        Directory.CreateDirectory(dir);
        string dumpPath = Path.Combine(dir, $"navdump-{ed}.json");
        string classifyPath = Path.Combine(dir, $"classify-{ed}.json");
        string stationsPath = Path.Combine(dir, $"stations-{ed}.json");
        var dumpSquares = walk.DumpSquares(world, dumpAll);
        File.WriteAllText(dumpPath, JsonSerializer.Serialize(walk.Dump(world, dumpSquares, dumpAll), Compact));
        File.WriteAllText(classifyPath, JsonSerializer.Serialize(ClassifyDoc(area, cov, walk, dumpSquares, dumpAll), Compact));
        File.WriteAllText(stationsPath, JsonSerializer.Serialize(StationsDoc(area, result), Compact));

        int noModel = area.XPoints.Count(p => !p.HasModel);
        var header = new Dictionary<string, object?>
        {
            ["label"] = label, ["setup"] = setup, ["stationsMode"] = "route", ["ruleId"] = "g5-route-sampling/1",
            ["worldspace"] = input.Worldspace.ToString(), ["worldspaceEditorId"] = ed, ["changedPlugin"] = changed,
            ["composedAtUtc"] = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ"),
            ["parameters"] = new Dictionary<string, object?>
            {
                ["rArea"] = RouteStations.RArea, ["spacing"] = RouteStations.Spacing,
                ["coverageDistance"] = RouteStations.CoverageDistance, ["startOffset"] = RouteStations.StartOffset,
                ["pathSampleStep"] = RouteStations.PathSampleStep, ["pathDedupRadius"] = RouteStations.PathDedupRadius,
                ["doorNearestK"] = RouteStations.DoorNearestK, ["exitNearestK"] = RouteStations.ExitNearestK,
                ["cornerInset"] = NavGraph.CornerInsetDistance, ["candidateDedup"] = RouteStations.CandidateDedup,
                ["retryAlternates"] = RouteStations.RetryAlternates, ["eyeHeight"] = StationRule.EyeHeight,
                ["fovDeg"] = StationRule.DefaultFovDeg, ["contentReach"] = RouteStations.ContentReach,
                ["contentBearingStepDeg"] = RouteStations.ContentBearingStepDeg, ["gazeDedupDeg"] = RouteStations.GazeDedupDeg,
                ["clearance"] = $"+-{NavCover.ClearanceRadius} u, 5 of 5, same floor within {NavCover.SameFloorZ} u",
                ["seeds"] = new[] { RouteStations.FillSeed, RouteStations.RetrySeed },
            },
            ["area"] = new Dictionary<string, object?>
            {
                ["xPoints"] = area.XPoints.Count, ["xPointsWhoseBaseHasNoModel"] = noModel,
                ["areaSquares"] = area.AreaSquares.Count, ["loadedSquares"] = area.LoadedSquares.Count,
                ["loadedNavmeshes"] = area.Loaded.Count, ["winningNavmeshes"] = area.Winning.Count,
                ["winningNavmeshesWithoutGrid"] = area.NavmeshesWithoutSquare,
                ["graph"] = new Dictionary<string, object?>
                {
                    ["triangles"] = area.Report.Triangles, ["intraLinks"] = area.Report.IntraLinks,
                    ["crossLinks"] = area.Report.CrossLinks, ["externalLinks"] = area.Report.ExternalLinks,
                    ["danglingLinks"] = area.Report.DanglingLinks, ["doorTriangles"] = area.Report.DoorTriangles,
                    ["components"] = area.Components, ["reachableComponents"] = area.ReachableComponents,
                },
                ["walkableTriangles"] = area.Triangles.Values.Count(t => t.Walkable),
                ["trianglesInArea"] = area.Triangles.Values.Count(t => t.InArea),
                ["areaA"] = area.AreaA,
                ["loadedPrimitives"] = world.Primitives.Count(p => area.LoadedSquares.Contains(RouteStations.SquareOf(p.Position.X, p.Position.Y))),
            },
            ["sampling"] = new Dictionary<string, object?>
            {
                ["doorEndpoints"] = result.DoorEndpoints, ["exitEndpoints"] = result.ExitEndpoints,
                ["pairs"] = result.PairsConsidered, ["pairsNoPath"] = result.PairsNoPath,
                ["pathsSolved"] = result.Paths.Count, ["pathsKept"] = result.Paths.Count(p => p.Kept),
                ["piecesNeedingFill"] = result.PiecesNeedingFill,
                ["fillStationsWithoutPathGaze"] = result.FillStationsWithoutPathGaze,
                ["acceptedStations"] = result.Stations.Count(s => s.Accepted),
            },
            ["coverageL1"] = new Dictionary<string, object?>
            {
                ["note"] = "plan distance, not line of sight: covered ground may be out of view from every station",
                ["areaA"] = cov.AreaA, ["covered"] = cov.Covered, ["fraction"] = cov.Fraction,
                ["routeOnlyCovered"] = routeCov.Covered, ["routeOnlyFraction"] = routeCov.Fraction,
                ["changedPluginNavmeshArea"] = ownArea, ["changedPluginNavmeshCovered"] = ownCovered,
                ["uncoveredByReason"] = cov.UncoveredByReason,
                ["uncoveredPieces"] = cov.Pieces.Count,
                ["largestUncoveredPieces"] = cov.Pieces.Take(20).Select(p => new Dictionary<string, object?>
                {
                    ["area"] = p.Area, ["x"] = p.Centroid.X, ["y"] = p.Centroid.Y, ["triangles"] = p.Triangles, ["reason"] = p.Reason,
                }).ToList(),
            },
            ["coverageLevels"] = levelDocs,
            ["environment"] = new Dictionary<string, object?>
            {
                ["readsWithMoreThanOneCoveringTriangle"] = reader.MultiCovered,
                ["readsWhereTerrainIsAnotherStorey"] = reader.Buried,
                ["readsInASquareWithNoFloorFiled"] = reader.OffGrid,
            },
            ["outputs"] = new Dictionary<string, object?>
            {
                ["navdump"] = dumpPath, ["classify"] = classifyPath, ["stations"] = stationsPath,
            },
            ["spec"] = new Dictionary<string, object?> { ["path"] = Path.GetFullPath(input.SpecPath), ["sha256"] = Sha256(specText) },
            ["instrument"] = Instrument(),
            ["commandLine"] = input.CommandLine,
        };
        string written;
        try
        {
            File.WriteAllText(input.OutPath, StationPoseFile.RenderRoute(header, result));
            written = Path.GetFullPath(input.OutPath);
        }
        catch (InvalidOperationException ex)
        {
            Console.Error.WriteLine("--stations (route): REFUSED to write the pose file. " + ex.Message);
            return 1;
        }

        foreach (var (fam, y) in result.Yields)
            Console.WriteLine($"   --stations (route): {StationPoseFile.FamilyName(fam)}: {y.Composed:N0} composed, {y.Accepted:N0} accepted, "
                            + $"{y.RejectedTotal:N0} refused ("
                            + string.Join(", ", StationRule.Precedence.Where(y.Rejected.ContainsKey).Select(r => $"{r} {y.Rejected[r]}"))
                            + $"), {y.DroppedTotal:N0} dropped ("
                            + string.Join(", ", y.Dropped.Select(kv => $"{kv.Key} {kv.Value}")) + ")");
        Console.WriteLine($"   --stations (route): A_W {cov.AreaA:N0} u^2 over {area.Triangles.Values.Count(t => t.InArea):N0} triangle(s); "
                        + $"L1 coverage {cov.Fraction:P2} (route stations alone {routeCov.Fraction:P2}); "
                        + $"{cov.Pieces.Count:N0} uncovered piece(s)");
        var tally = new FloorFeed.Tally();
        foreach (var f in reader.Fields.Values) tally.Add(f);
        Console.WriteLine("   " + tally.Line());
        Console.WriteLine($"   --stations (route): wrote {written}");
        Console.WriteLine($"   --stations (route): wrote {stationsPath}, {classifyPath}, {dumpPath}");
        return 0;
    }

    // ---- the classification and stations files (the reviewer's contract, extended only by added fields) --

    static Dictionary<string, object?> ClassifyDoc(RouteArea area, RouteCoverage cov, Walk walk,
                                                   HashSet<(int, int)> dumpSquares, bool dumpAll)
    {
        var tris = new List<object>();
        foreach (var t in area.Triangles.Values.OrderBy(t => t.Tri, RouteStations.TriComparer))
            tris.Add(new Dictionary<string, object?>
            {
                ["mesh"] = t.Tri.Mesh.ToString(), ["tri"] = t.Tri.Index, ["loaded"] = true,
                ["walkable"] = t.Walkable, ["component"] = t.Component, ["reachable"] = t.Reachable,
                ["inArea"] = t.InArea, ["areaClipped"] = t.AreaClipped,
                ["coveredL1"] = cov.CoveredPerTriangle.TryGetValue(t.Tri, out var c) ? c : 0.0,
                // added fields
                ["node"] = t.Node, ["door"] = t.Door, ["exit"] = t.Exit, ["cityOwn"] = t.CityOwn,
                ["flags"] = t.Flags, ["areaFull"] = t.AreaFull,
            });
        // triangles of dumped navmeshes outside the loaded set, so the two files can be joined one to one
        foreach (var (k, nv) in walk.DumpedNavmeshes(dumpSquares, dumpAll).OrderBy(x => x.Key.ToString(), StringComparer.Ordinal))
        {
            if (area.Loaded.ContainsKey(k)) continue;
            for (int i = 0; i < nv.Triangles.Count; i++)
                tris.Add(new Dictionary<string, object?>
                {
                    ["mesh"] = k.ToString(), ["tri"] = i, ["loaded"] = false, ["walkable"] = false, ["component"] = -1,
                    ["reachable"] = false, ["inArea"] = false, ["areaClipped"] = 0.0, ["coveredL1"] = 0.0,
                });
        }
        return new Dictionary<string, object?>
        {
            ["schema"] = "g5-classify/1",
            ["worldspace"] = area.World.Worldspace.ToString(),
            ["triangles"] = tris,
            ["totals"] = new Dictionary<string, object?>
            {
                ["areaA"] = cov.AreaA, ["coveredL1"] = cov.Covered,
                // added fields
                ["xPoints"] = area.XPoints.Count, ["areaSquares"] = area.AreaSquares.OrderBy(s => s).Select(s => new[] { s.X, s.Y }).ToList(),
                ["loadedSquares"] = area.LoadedSquares.OrderBy(s => s).Select(s => new[] { s.X, s.Y }).ToList(),
                ["loadedNavmeshes"] = area.Loaded.Keys.Select(k => k.ToString()).OrderBy(k => k, StringComparer.Ordinal).ToList(),
            },
        };
    }

    static Dictionary<string, object?> StationsDoc(RouteArea area, RouteResult result)
    {
        var rows = new List<object>();
        foreach (var s in result.Stations)
        {
            var v = s.Verdict;
            rows.Add(new Dictionary<string, object?>
            {
                ["id"] = s.Id, ["x"] = s.X, ["y"] = s.Y,
                ["z"] = v is not null && float.IsFinite(v.Env.NavZ) ? v.Env.NavZ : null,
                ["family"] = StationPoseFile.FamilyName(s.Family), ["accepted"] = s.Accepted,
                ["refusal"] = v is not null && v.Reason != StationRejection.None ? v.Reason.ToString() : null,
                ["mesh"] = v?.Covering?.Mesh.ToString(), ["tri"] = v?.Covering?.Index,
                // added fields
                ["dropped"] = s.Drop == RouteDrop.None ? null : s.Drop.ToString(),
                ["detail"] = v?.Detail,
                ["eyeZ"] = v is not null && float.IsFinite(v.EyeZ) ? v.EyeZ : null,
                ["gazes"] = s.Gazes.Select(g => new Dictionary<string, object?> { ["kind"] = g.Kind.ToString(), ["bearingDeg"] = g.BearingDeg }).ToList(),
                ["pathId"] = s.PathId, ["walkedAt"] = s.WalkedAt, ["fwdVsAheadDeg"] = s.FwdVsAheadDeg,
                ["sourceMesh"] = s.SourceTri?.Mesh.ToString(), ["sourceTri"] = s.SourceTri?.Index,
                ["subPath"] = s.SubPath, ["retryJ"] = s.RetryJ,
                ["coveringFlags"] = v?.CoveringFlags,
            });
        }
        return new Dictionary<string, object?>
        {
            ["schema"] = "g5-stations/1", ["worldspace"] = area.World.Worldspace.ToString(), ["stations"] = rows,
        };
    }

    // ---- the worldspace pass: raw, and classifying nothing ----------------------------------------------

    internal sealed class Walk
    {
        readonly FormKey _ws;
        readonly HashSet<FormKey> _children;
        readonly FormKey _plane;
        public string WsEditorId = "";
        public readonly Dictionary<FormKey, (NavmeshGeometry Nav, (int X, int Y)? Square)> NavWin = new();
        public readonly Dictionary<FormKey, List<string>> NavChain = new();
        public readonly Dictionary<FormKey, List<(string Plugin, Vector3? Pos, int Flags, FormKey? Base)>> Versions = new();
        public readonly HashSet<FormKey> CityPlacedKeys = new();
        public readonly HashSet<FormKey> CityNavKeys = new();
        /// <summary>The floor filing (FloorFeed.FloorIndex): this worldspace, by position inside <see cref="Bound"/>, over
        /// every cell of every plugin (a move to another worldspace or an interior takes the earlier filing out).</summary>
        public readonly FloorFeed.FloorIndex Floor;
        public Dictionary<(int, int), Dictionary<FormKey, PlacedRef>> RefsByGrid => Floor.RefsOf(_ws);
        public Dictionary<(int, int), float[,]> LandByGrid => Floor.LandOf(_ws);
        public IEnumerable<(FormKey Ws, int X, int Y)> UnreadableLand => Floor.UnreadableLand;
        /// <summary>This worldspace's grids whose last LAND gives no heights, with the reason: the station reader refuses them.</summary>
        public Dictionary<(int, int), string> UnreadableLandWhy =>
            Floor.UnreadableLandWhy.Where(x => x.Key.Ws == _ws).ToDictionary(x => (x.Key.X, x.Key.Y), x => x.Value);
        public readonly Dictionary<(int, int), (string Cell, DetectorModel.WaterTuple Water)> Cells = new();
        public readonly Dictionary<string, (float? Default, string? Parent)> WorldChain = new(StringComparer.OrdinalIgnoreCase);
        public readonly Dictionary<FormKey, PlacedRef?> Prims = new();
        public readonly Dictionary<FormKey, NavmeshGeometry> Child = new();
        public readonly HashSet<(int X, int Y)> Bound = new();

        public Walk(FormKey ws, IEnumerable<FormKey> children, FormKey plane)
        {
            _ws = ws; _children = new HashSet<FormKey>(children); _plane = plane;
            Floor = new FloorFeed.FloorIndex((w, g) => w == _ws && Bound.Contains(g));
        }

        static IEnumerable<ICellGetter> CellsOf(IWorldspaceGetter ws)
        {
            if (ws.TopCell is { } tc) yield return tc;
            foreach (var blk in ws.SubCells)
                foreach (var sub in blk.Items)
                    foreach (var c in sub.Items) yield return c;
        }

        static IEnumerable<IPlacedGetter> PlacedOf(ICellGetter c) => c.Persistent.Concat(c.Temporary);

        /// <summary>The changed plugin alone: what it carries in the worldspace, and the bound its
        /// content sets for the floor filing.</summary>
        public void PreRead(string path, int margin)
        {
            var seeds = new HashSet<(int, int)>();
            var mod = PluginRead.Open(path, -1, 0, "--stations (route) pre-read");   // refused, not raw (PluginRead.cs)
            using var _ = mod as IDisposable;
            foreach (var ws in mod.Worldspaces.Records)
            {
                if (ws.FormKey != _ws) continue;
                foreach (var c in CellsOf(ws))
                {
                    foreach (var p in PlacedOf(c))
                    {
                        CityPlacedKeys.Add(p.FormKey);
                        if (p.Placement is { } pl) seeds.Add(FloorFeed.GridOf(Vec3.From(pl.Position)));
                    }
                    foreach (var n in c.NavigationMeshes) CityNavKeys.Add(n.FormKey);
                    foreach (var nav in PlacementExtract.FromCell(c, ws.FormKey).Navmeshes)
                        foreach (var v in nav.Vertices) seeds.Add(FloorFeed.GridOf(v));
                }
            }
            foreach (var (x, y) in seeds)
                for (int dx = -margin; dx <= margin; dx++)
                    for (int dy = -margin; dy <= margin; dy++) Bound.Add((x + dx, y + dy));
        }

        public void Plugin(string path)
        {
            string plugin = Path.GetFileName(path);
            // Refused with a named exit through the render's top-level handler (PluginRead.cs).
            ISkyrimModGetter mod = PluginRead.Open(path, -1, 0, "--stations (route) worldspace pass");
            using (mod as IDisposable)
                foreach (var ws in mod.Worldspaces.Records)
                {
                    WorldChain[ws.FormKey.ToString()] = (ws.LandDefaults?.DefaultWaterHeight,
                        ws.Parent?.Worldspace.FormKeyNullable is { IsNull: false } wp ? wp.ToString() : null);
                    bool isW = ws.FormKey == _ws, isChild = _children.Contains(ws.FormKey);
                    if (!isW && !isChild) continue;
                    if (isW && ws.EditorID is { Length: > 0 } ed) WsEditorId = ed;
                    foreach (var c in CellsOf(ws))
                    {
                        if (isChild)
                        {
                            foreach (var n in c.NavigationMeshes) if (n.IsDeleted || n.Data is null) Child.Remove(n.FormKey);
                            foreach (var nav in PlacementExtract.FromCell(c, ws.FormKey).Navmeshes) Child[nav.FormKey] = nav;
                            continue;
                        }
                        (int X, int Y)? grid = c.Grid?.Point is { } gp ? (gp.X, gp.Y) : null;
                        if (grid is { } g && Bound.Contains(g))
                            Cells[g] = (c.FormKey.ToString(), new DetectorModel.WaterTuple(
                                c.Flags.HasFlag(Cell.Flag.HasWater), false,
                                c.Water.FormKeyNullable is { IsNull: false } cw ? cw.ToString() : null,
                                c.WaterHeight ?? float.MaxValue, null, null, null, null));
                        foreach (var p in PlacedOf(c))
                        {
                            if (CityPlacedKeys.Contains(p.FormKey))
                            {
                                if (!Versions.TryGetValue(p.FormKey, out var vl)) Versions[p.FormKey] = vl = new();
                                Vector3? pos = p.Placement is { } pl ? new Vector3(pl.Position.X, pl.Position.Y, pl.Position.Z) : null;
                                vl.Add((plugin, pos, p.MajorRecordFlagsRaw, ReadBase(p)));
                            }
                            if (p.IsDeleted && Prims.ContainsKey(p.FormKey)) Prims[p.FormKey] = null;
                        }
                        foreach (var n in c.NavigationMeshes)
                        {
                            if (!NavChain.TryGetValue(n.FormKey, out var ch)) NavChain[n.FormKey] = ch = new();
                            ch.Add(plugin);
                            if (n.IsDeleted || n.Data is null) NavWin.Remove(n.FormKey);
                        }
                        var cp = PlacementExtract.FromCell(c, ws.FormKey);
                        foreach (var nav in cp.Navmeshes) NavWin[nav.FormKey] = (nav, grid);
                        foreach (var r in cp.Refs)
                            if (r.Base is { } b && b == _plane) Prims[r.FormKey] = r.Deleted ? null : r;
                    }
                }
            // The floor: every cell of the plugin, every worldspace and interior (LastSighting.Cells), in load order.
            var fmod = PluginRead.Open(path, -1, 0, "--stations (route) floor pass");
            using (fmod as IDisposable)
                foreach (var fcp in LastSighting.Cells(fmod)) Floor.Observe(fcp, plugin);
        }

        /// <summary>HOUSECARL_WINSTATE_DUMP: the floor filing and the planes after the pass, for an independent reader.</summary>
        public void DumpWinState()
        {
            if (!HarnessShared.WinStateDump.Asked) return;
            HarnessShared.WinStateDump.Write("viewprobe-route", new
            {
                worldspace = _ws.ToString(), plane = _plane.ToString(),
                bound = Bound.OrderBy(g => g).Select(g => new { x = g.X, y = g.Y }).ToList(),
                prims = Prims.OrderBy(p => p.Key.ToString(), StringComparer.Ordinal)
                             .Select(p => new { formKey = p.Key.ToString(), present = p.Value is not null, x = p.Value?.Position.X, y = p.Value?.Position.Y }).ToList(),
                floor = HarnessShared.WinStateDump.Floor(_ws, RefsByGrid, LandByGrid, UnreadableLandWhy.Keys.Select(k => (k.Item1, k.Item2))),
            });
        }

        public RouteWorld World(string ed, string changed, IReadOnlyList<string> arm, Func<FormKey, string?> baseModel)
        {
            var navs = NavWin.OrderBy(x => x.Key.ToString(), StringComparer.Ordinal)
                .Select(x => new RouteNavmesh(x.Value.Nav, NavChain.TryGetValue(x.Key, out var ch) ? ch : new List<string>(), x.Value.Square))
                .ToList();
            var placed = new List<RoutePlaced>();
            foreach (var fk in CityPlacedKeys.OrderBy(k => k.ToString(), StringComparer.Ordinal))
            {
                if (!Versions.TryGetValue(fk, out var vl)) continue;
                FormKey? bs = null;
                foreach (var v in vl) if (v.Base is { } b) bs = b;          // the last version stating a base
                placed.Add(new RoutePlaced(fk, bs, bs is { } bb && baseModel(bb) is { Length: > 0 },
                    vl.Select(v => new RoutePlacedVersion(v.Plugin, v.Pos, v.Flags)).ToList()));
            }
            var prims = new List<PrimitiveGeometry>();
            foreach (var (fk, r) in Prims.OrderBy(x => x.Key.ToString(), StringComparer.Ordinal))
            {
                if (r is null || r.Primitive is not { } pr) continue;
                PrimitiveKind? kind = pr.Type switch { "PortalBox" => PrimitiveKind.PortalBox, "Box" => PrimitiveKind.Box, _ => null };
                if (kind is not { } k) continue;
                prims.Add(new PrimitiveGeometry(fk.ToString(), k, new Vector3(pr.Bounds.X, pr.Bounds.Y, pr.Bounds.Z) * r.Scale,
                    new Vector3(r.Position.X, r.Position.Y, r.Position.Z), new Vector3(r.Rotation.X, r.Rotation.Y, r.Rotation.Z),
                    _ws.ToString(), ""));
            }
            return new RouteWorld
            {
                Worldspace = _ws, WorldspaceEditorId = ed, ArmPlugins = arm, CityPlugin = changed,
                Navmeshes = navs, CityPlaced = placed,
                CityNavmeshes = CityNavKeys.Where(NavWin.ContainsKey).ToList(),
                Primitives = prims,
                ChildFootprint = _children.Count > 0 ? Child.Values.ToList() : null,
            };
        }

        /// <summary>The dump's squares: within 2 (Chebyshev) of any square holding any version's position of a
        /// placed record the changed plugin carries, or a vertex or triangle centroid of its navmeshes.</summary>
        public HashSet<(int, int)> DumpSquares(RouteWorld w, bool all)
        {
            var seed = new HashSet<(int, int)>();
            foreach (var p in w.CityPlaced)
                foreach (var v in p.Versions)
                    if (v.Position is { } pos) seed.Add(RouteStations.SquareOf(pos.X, pos.Y));
            foreach (var k in w.CityNavmeshes)
                if (NavWin.TryGetValue(k, out var nv))
                {
                    foreach (var v in nv.Nav.Vertices) seed.Add(RouteStations.SquareOf(v.X, v.Y));
                    foreach (var t in nv.Nav.Triangles)
                        if (t.A >= 0 && t.B >= 0 && t.C >= 0 && t.A < nv.Nav.Vertices.Count && t.B < nv.Nav.Vertices.Count && t.C < nv.Nav.Vertices.Count)
                        {
                            var a = nv.Nav.Vertices[t.A]; var b = nv.Nav.Vertices[t.B]; var c = nv.Nav.Vertices[t.C];
                            seed.Add(RouteStations.SquareOf((a.X + b.X + c.X) / 3f, (a.Y + b.Y + c.Y) / 3f));
                        }
                }
            var sq = new HashSet<(int, int)>();
            foreach (var (x, y) in seed)
                for (int dx = -2; dx <= 2; dx++)
                    for (int dy = -2; dy <= 2; dy++) sq.Add((x + dx, y + dy));
            if (all) foreach (var v in NavWin.Values) if (v.Square is { } s) sq.Add(s);
            return sq;
        }

        public IEnumerable<KeyValuePair<FormKey, NavmeshGeometry>> DumpedNavmeshes(HashSet<(int, int)> squares, bool all)
        {
            foreach (var (k, v) in NavWin)
            {
                bool take = all || (v.Square is { } s ? squares.Contains(s)
                                   : v.Nav.Vertices.Any(p => squares.Contains(RouteStations.SquareOf(p.X, p.Y))));
                if (take) yield return new(k, v.Nav);
            }
        }

        /// <summary>navdump-*.json: the raw fields, in the reviewer's contract shape.</summary>
        public Dictionary<string, object?> Dump(RouteWorld w, HashSet<(int, int)> squares, bool all)
        {
            var navs = new List<object>();
            foreach (var (k, nv) in DumpedNavmeshes(squares, all).OrderBy(x => x.Key.ToString(), StringComparer.Ordinal))
            {
                var chain = NavChain.TryGetValue(k, out var ch) ? ch : new List<string>();
                var sq = NavWin[k].Square;
                navs.Add(new Dictionary<string, object?>
                {
                    ["formKey"] = k.ToString(), ["chain"] = chain, ["winner"] = chain.Count > 0 ? chain[^1] : null,
                    ["square"] = sq is { } s ? new[] { s.X, s.Y } : null,
                    ["vertices"] = nv.Vertices.Select(v => new[] { v.X, v.Y, v.Z }).ToList(),
                    ["triangles"] = nv.Triangles.Select(t => new Dictionary<string, object?>
                    {
                        ["v"] = new int[] { t.A, t.B, t.C }, ["flags"] = t.Flags,
                        ["edges"] = new int[] { t.EdgeLink01, t.EdgeLink12, t.EdgeLink20 },
                    }).ToList(),
                    ["edgeLinks"] = (nv.EdgeLinks ?? Array.Empty<NavEdgeLink>()).Select(e => new Dictionary<string, object?>
                    { ["mesh"] = e.Mesh.ToString(), ["tri"] = (int)e.TriangleIndex }).ToList(),
                    ["doorTriangles"] = (nv.DoorTriangles ?? Array.Empty<NavDoorTriangle>()).Select(d => new Dictionary<string, object?>
                    { ["tri"] = (int)d.TriangleBeforeDoor, ["door"] = d.Door.ToString() }).ToList(),
                });
            }
            var placed = w.CityPlaced.Select(p => new Dictionary<string, object?>
            {
                ["formKey"] = p.FormKey.ToString(), ["base"] = p.Base?.ToString(), ["baseHasModel"] = p.BaseHasModel,
                ["versions"] = p.Versions.Select(v => new Dictionary<string, object?>
                {
                    ["plugin"] = v.Plugin,
                    ["pos"] = v.Position is { } q ? new[] { q.X, q.Y, q.Z } : null,
                    ["headerFlags"] = v.HeaderFlags,
                }).ToList(),
            }).ToList();
            return new Dictionary<string, object?>
            {
                ["schema"] = "g5-navdump/1", ["worldspace"] = w.Worldspace.ToString(), ["armPlugins"] = w.ArmPlugins,
                ["dumpSquares"] = squares.OrderBy(s => s).Select(s => new[] { s.Item1, s.Item2 }).ToList(),
                ["navmeshes"] = navs, ["surwrPlaced"] = placed,
                ["surwrNavmeshes"] = w.CityNavmeshes.Select(k => k.ToString()).OrderBy(k => k, StringComparer.Ordinal).ToList(),
                // added fields
                ["changedPlugin"] = w.CityPlugin, ["dumpAllNavmeshes"] = all,
                ["note"] = "raw record fields, written by code that classifies nothing; 'surwr*' names the changed plugin's records",
            };
        }
    }

    static readonly ConcurrentDictionary<Type, PropertyInfo?> BaseProp = new();

    /// <summary>A placed record's base, read by reflection over the arm's Base link as PlacementExtract reads it
    /// (the link is declared per arm, not on IPlacedGetter).</summary>
    static FormKey? ReadBase(IPlacedGetter placed)
    {
        var prop = BaseProp.GetOrAdd(placed.GetType(), static t => t.GetProperty("Base", BindingFlags.Public | BindingFlags.Instance));
        return prop?.GetValue(placed) is Mutagen.Bethesda.Plugins.IFormLinkGetter link && link.FormKeyNullable is { IsNull: false } k ? k : null;
    }

    static string? Str(JsonElement root, string name)
        => root.TryGetProperty(name, out var e) && e.ValueKind == JsonValueKind.String ? e.GetString() : null;

    static string Sha256(string text)
        => Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(text))).ToLowerInvariant();

    static Dictionary<string, object?> Instrument()
    {
        var d = new Dictionary<string, object?>();
        try
        {
            string loc = typeof(RouteStationMode).Assembly.Location;
            d["assembly"] = loc;
            if (loc.Length > 0 && File.Exists(loc))
            {
                d["sha256"] = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(loc))).ToLowerInvariant();
                d["builtUtc"] = File.GetLastWriteTimeUtc(loc).ToString("yyyy-MM-ddTHH:mm:ssZ");
            }
        }
        catch (Exception ex) { d["error"] = ex.Message; }
        return d;
    }
}
