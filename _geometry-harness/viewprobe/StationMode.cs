using System.Numerics;
using System.Security.Cryptography;
using System.Text.Json;
using HarnessShared;
using HousecarlCore;
using Mutagen.Bethesda.Plugins;

namespace ViewProbe;

// ======================================================================
//  --stations - viewprobe's non-rendering mode: it composes the camera
//  stations for a cell's occlusion primitives, writes the pose file with
//  its yield header, and exits.
//
//  It rides the load-order walk the renderer already does rather than
//  starting a second one, and it decides nothing itself. The rule is
//  HarnessShared.StationRule (composition and the closed refusal set), the
//  water is HarnessShared.StationLookup through the ONE resolver, the
//  floor is FloorFeed over HousecarlCore.FloorField, and the file is
//  HarnessShared.StationPoseFile with its closure assertion. Every one of
//  those has cases in proofprobe; this file is the plumbing between them
//  and the load order, and it is kept out of Program.cs because a new
//  domain does not get appended to a four-thousand-line file.
//
//  EXTERIORS ONLY, said rather than assumed: the water and the floor are
//  both looked up by the 4,096 u grid square a station's XY falls in, and
//  an interior cell has no grid. An interior target is refused with a
//  message instead of being run through a path nothing has tested.
// ======================================================================

static class StationMode
{
    /// <summary>One occlusion primitive as the winning state places it, already scaled.</summary>
    public readonly record struct Primitive(string Key, PrimitiveKind Kind, Vector3 ExtentFull,
                                            Vector3 Position, Vector3 RotationRad, string DefinedBy,
                                            string PrimitiveCell);

    public sealed record Inputs
    {
        public required string SpecPath { get; init; }
        public required string OutPath { get; init; }
        /// <summary>The target worldspace as a FormKey string - the key the cell and worldspace
        /// lookups are both keyed by, so a pose names something unambiguous.</summary>
        public required string Worldspace { get; init; }
        public required string WorldspaceEditorId { get; init; }
        public required string TargetCell { get; init; }
        public required IReadOnlyList<Primitive> Primitives { get; init; }
        public required IReadOnlyCollection<NavmeshGeometry> Navmeshes { get; init; }
        public required Dictionary<(int, int), Dictionary<FormKey, PlacedRef>> RefsByGrid { get; init; }
        public required Dictionary<(int, int), float[,]> LandByGrid { get; init; }
        public required FloorFeed.GeometryOf Geometry { get; init; }
        public required Func<FormKey, bool> Enabled { get; init; }
        public required Func<(int X, int Y), GridCellFacts?> CellAt { get; init; }
        public required Func<string, WorldFacts?> WorldAt { get; init; }
        public required string CommandLine { get; init; }
    }

    public static int Run(Inputs input)
    {
        if (!File.Exists(input.SpecPath))
        {
            Console.Error.WriteLine($"--stations: no spec file at {input.SpecPath}.");
            return 2;
        }
        string specText = File.ReadAllText(input.SpecPath);
        string setup; float[] kValues; float[] laterals; float fov; string label;
        HashSet<string>? wanted;
        try
        {
            using var spec = JsonDocument.Parse(specText);
            var root = spec.RootElement;
            setup = Str(root, "setup") ?? "";
            label = Str(root, "label") ?? setup;
            if (setup.Length == 0)
            {
                Console.Error.WriteLine("--stations: the spec needs a \"setup\" label - the arm "
                                      + "this pose set belongs to. The yield is kept per setup.");
                return 2;
            }
            kValues = Floats(root, "k") ?? StationRule.DefaultK;
            laterals = Floats(root, "laterals") ?? StationRule.DefaultLaterals;
            fov = root.TryGetProperty("fovDeg", out var f) ? f.GetSingle() : StationRule.DefaultFovDeg;
            // The eye height is SEALED in the rule, not a spec knob. A spec may state it, and then
            // it is checked against the constant and refused if it differs - which is how a seal is
            // made checkable from the outside rather than merely asserted.
            if (root.TryGetProperty("eyeHeight", out var eh)
                && MathF.Abs(eh.GetSingle() - StationRule.EyeHeight) > 1e-4f)
            {
                Console.Error.WriteLine($"--stations: the spec asks for eyeHeight {eh.GetSingle()}, "
                                      + $"but the sealed constant is {StationRule.EyeHeight}. The eye "
                                      + "height is part of the rule, not an argument; refusing.");
                return 2;
            }
            wanted = null;
            if (root.TryGetProperty("primitives", out var pr) && pr.ValueKind == JsonValueKind.Array)
            {
                wanted = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var e in pr.EnumerateArray())
                    if (e.GetString() is { Length: > 0 } s) wanted.Add(s);
                if (wanted.Count == 0) wanted = null;
            }
        }
        catch (JsonException ex)
        {
            Console.Error.WriteLine($"--stations: {input.SpecPath} is not readable JSON: {ex.Message}");
            return 2;
        }

        bool ladderSealed = Same(kValues, StationRule.DefaultK)
                            && Same(laterals, StationRule.DefaultLaterals);
        if (!ladderSealed)
            Console.WriteLine("   --stations: WARNING - this spec composes a ladder that is NOT the "
                            + "sealed one (A5). The pose file records both, and a yield composed on "
                            + "a changed ladder is not comparable with one composed on the sealed one.");

        var chosen = new List<Primitive>();
        var missing = new List<string>();
        foreach (var p in input.Primitives)
            if (wanted is null || wanted.Contains(p.Key)) chosen.Add(p);
        if (wanted is not null)
        {
            var have = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var p in chosen) have.Add(p.Key);
            foreach (var w in wanted) if (!have.Contains(w)) missing.Add(w);
        }
        if (missing.Count > 0)
        {
            // Refused, not shrugged off: a spec that names a primitive the arm does not place is
            // either the wrong arm or the wrong FormKey, and composing the rest would publish a
            // yield that silently answers a different question.
            Console.Error.WriteLine("--stations: the spec names primitive(s) this arm does not place "
                                  + $"in {input.TargetCell} or its ring: {string.Join(", ", missing)}");
            return 2;
        }
        if (chosen.Count == 0)
        {
            Console.Error.WriteLine($"--stations: no occlusion primitive to compose for in "
                                  + $"{input.TargetCell}. A cell with no PortalBox or Box has no "
                                  + "stations; this is an answer, but it is not a pose file.");
            return 2;
        }

        // The floor field per grid square, built once and kept: FloorFeed reads the 3x3
        // neighbourhood, because a floor mesh placed in the next cell reaches under this one.
        var fields = new Dictionary<(int, int), FloorField>();
        FloorField FieldFor((int X, int Y) g)
        {
            if (fields.TryGetValue(g, out var f)) return f;
            var (refs, land) = FloorFeed.Neighbourhood(g, input.RefsByGrid, input.LandByGrid);
            f = FloorFeed.Build(refs, input.Geometry, land, enabled: input.Enabled);
            fields[g] = f;
            return f;
        }

        int multiCovered = 0, offGrid = 0, buried = 0;
        StationEnvironment Environment(float x, float y)
        {
            var (answered, covered, drawn, navZ, covers) = NavAt(input.Navmeshes, x, y);
            if (covers > 1) multiCovered++;
            var place = StationLookup.At(x, y, input.CellAt, input.WorldAt, input.Worldspace);
            if (!input.RefsByGrid.ContainsKey(place.Grid) && !input.LandByGrid.ContainsKey(place.Grid))
                offGrid++;
            // No floor is READ where there is no navmesh height to read it against: the floor rule
            // is a band around a height, and asking it about NaN answers nothing. The station is
            // already refused for the navmesh, and the precedence counts it there.
            if (!(answered && covered && drawn))
                return new StationEnvironment(answered, covered, drawn, navZ,
                    new FloorReading(navZ, 0f, FloorSource.None, NoFloorReason.NoSurface, false, 0),
                    place.Cell, place.Worldspace, place.Water);
            // WHERE the read is anchored is StationGround's; the landscape height is read with the
            // same function the field reads it with, from the same filed heightmaps.
            float? terrainZ = null;
            if (input.LandByGrid.TryGetValue(place.Grid, out var heights))
                terrainZ = NavmeshFloor.LandHeightAt(heights, place.Grid.X, place.Grid.Y, x, y, out _);
            var field = FieldFor(place.Grid);
            var ground = StationGround.Read(z => field.Read(x, y, z), navZ, terrainZ);
            if (ground.NavmeshBuried) buried++;
            return new StationEnvironment(answered, covered, drawn, navZ, ground.Floor,
                                          place.Cell, place.Worldspace, place.Water,
                                          ground.Anchor, ground.NavmeshBurial, ground.NavmeshBuried);
        }

        var yields = new List<StationYield>(chosen.Count);
        int tilted = 0;
        var tiltedKeys = new List<string>();
        foreach (var p in chosen)
        {
            if (MathF.Abs(p.RotationRad.X) > 1e-4f || MathF.Abs(p.RotationRad.Y) > 1e-4f)
            {
                tilted++;
                if (tiltedKeys.Count < 8) tiltedKeys.Add(p.Key);
            }
            var geom = new PrimitiveGeometry(p.Key, p.Kind, p.ExtentFull, p.Position, p.RotationRad,
                                             input.Worldspace, p.PrimitiveCell);
            var y = StationRule.Compose(geom, setup, Environment, kValues, laterals);
            if (MathF.Abs(fov - StationRule.DefaultFovDeg) > 1e-4f)
            {
                var replaced = new List<StationPose>(y.Poses.Count);
                foreach (var pose in y.Poses) replaced.Add(pose with { FovDeg = fov });
                y.Poses.Clear(); y.Poses.AddRange(replaced);
            }
            yields.Add(y);
        }

        if (tilted > 0)
            Console.WriteLine($"   --stations: {tilted} primitive(s) carry a rotation about X or Y "
                            + $"({string.Join(", ", tiltedKeys)}{(tilted > tiltedKeys.Count ? ", ..." : "")}). "
                            + "The rule models an upright primitive turned about Z only, so their "
                            + "normals and extents are the upright reading - stated, not hidden.");
        if (buried > 0)
            Console.WriteLine($"   --stations: {buried} station read(s) stood on ground the navmesh "
                            + $"is buried more than {NavmeshFloor.DefaultUpReach:0} u beneath. The "
                            + "floor read is anchored at the landscape there, because a placed body "
                            + "stands on the ground and not on a mesh authored under it; every pose "
                            + "carries its anchor and its burial depth.");
        if (offGrid > 0)
            Console.WriteLine($"   --stations: {offGrid} station(s) fell in a grid square with no "
                            + "floor candidates and no terrain filed. They are refused for want of a "
                            + "floor, not stood on the navmesh.");

        var header = new Dictionary<string, object?>
        {
            ["label"] = label,
            ["setup"] = setup,
            ["worldspace"] = input.Worldspace,
            ["worldspaceEditorId"] = input.WorldspaceEditorId,
            ["targetCell"] = input.TargetCell,
            ["composedAtUtc"] = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ"),
            ["ladder"] = new Dictionary<string, object?>
            {
                ["k"] = kValues, ["laterals"] = laterals, ["side"] = new[] { 1, -1 },
                ["standoffFloor"] = StationRule.StandoffFloor,
                ["standoffCap"] = StationRule.StandoffCap,
                ["eyeHeight"] = StationRule.EyeHeight,
                ["fovDeg"] = fov,
                ["isTheSealedLadder"] = ladderSealed,
            },
            ["floorRule"] = new Dictionary<string, object?>
            {
                ["maxDepthBelowNavmesh"] = NavmeshFloor.DefaultMaxDepth,
                ["upReachAboveNavmesh"] = NavmeshFloor.DefaultUpReach,
                ["neighbourhood"] = "3x3 grid squares around the station's own square",
                ["anchor"] = "max(navmesh, terrain) at the station's own XY",
            },
            ["primitives"] = chosen.Count,
            ["groundReadsOnBuriedNavmesh"] = buried,
            ["primitivesTiltedOffZ"] = tilted,
            ["stationsWithMoreThanOneCoveringTriangle"] = multiCovered,
            ["spec"] = new Dictionary<string, object?>
            {
                ["path"] = Path.GetFullPath(input.SpecPath), ["sha256"] = Sha256(specText),
            },
            ["instrument"] = Instrument(),
            ["commandLine"] = input.CommandLine,
        };

        string written;
        try { written = StationPoseFile.Write(input.OutPath, header, yields); }
        catch (InvalidOperationException ex)
        {
            Console.Error.WriteLine("--stations: REFUSED to write the pose file. " + ex.Message);
            return 1;
        }

        int composed = 0, accepted = 0;
        var reasons = new Dictionary<StationRejection, int>();
        foreach (var y in yields)
        {
            composed += y.Composed; accepted += y.Accepted;
            foreach (var (r, n) in y.Rejected)
                reasons[r] = (reasons.TryGetValue(r, out int m) ? m : 0) + n;
        }
        var parts = new List<string>();
        foreach (var r in StationRule.Precedence)
            if (reasons.TryGetValue(r, out int n) && n > 0) parts.Add($"{r} {n}");
        // The tally is taken AFTER the composition: FloorFeed.Tally copies a field's counters when
        // it is added, so adding a field at build time reports a field that has answered nothing.
        var tally = new FloorFeed.Tally();
        foreach (var f in fields.Values) tally.Add(f);

        if (System.Environment.GetEnvironmentVariable("VP_STATION_COLUMN") is { Length: > 0 } cxy)
        {
            var pp = cxy.Split(',');
            if (pp.Length == 2 && float.TryParse(pp[0], out float cx) && float.TryParse(pp[1], out float cy))
            {
                var (_, _, _, cz, _) = NavAt(input.Navmeshes, cx, cy);
                var cg = StationLookup.GridOf(cx, cy);
                Column(input, FieldFor(cg), cg, cx, cy, cz, input.OutPath + ".column.json");
            }
            else Console.Error.WriteLine($"VP_STATION_COLUMN='{cxy}' is not X,Y; no diagnostic written.");
        }
        Console.WriteLine($"   --stations: {chosen.Count} primitive(s), {composed:N0} station(s) "
                        + $"composed, {accepted:N0} accepted, {composed - accepted:N0} refused"
                        + (parts.Count > 0 ? " (" + string.Join(", ", parts) + ")" : ""));
        Console.WriteLine("   " + tally.Line());
        Console.WriteLine($"   --stations: wrote {written}");
        return 0;
    }

    /// <summary>VP_STATION_COLUMN=X,Y — a DIAGNOSTIC, written beside the pose file as
    /// <c>&lt;poses&gt;.column.json</c>. It answers "why did the floor read what it read here" and it
    /// decides nothing: no pose, no verdict and no constant depends on it. Every surface the column
    /// holds at that XY is listed with its height, its facing, the reference that owns it and
    /// whether the floor rule's band could see it; every floor CANDIDATE the neighbourhood offered
    /// is listed with why it did or did not contribute, including the ones this mode's own geometry
    /// lookup returned nothing for - which is the difference between "there is no surface there"
    /// and "this instrument cannot see the surface that is there".</summary>
    static void Column(Inputs input, FloorField field, (int X, int Y) grid, float x, float y,
                       float navZ, string outPath)
    {
        var (refs, _) = FloorFeed.Neighbourhood(grid, input.RefsByGrid, input.LandByGrid);
        var surfaces = new List<object>();
        var candidates = new List<object>();
        float lo = navZ - NavmeshFloor.DefaultMaxDepth, hi = navZ + NavmeshFloor.DefaultUpReach;
        var col = new List<(float Z, bool Up)>();
        foreach (var r in refs)
        {
            string why = "";
            if (r.Deleted) why = "deleted";
            else if (r.Base is null) why = "no base";
            else if (!input.Enabled(r.FormKey)) why = "not enabled at load";
            var g = why.Length == 0 ? input.Geometry(r) : null;
            if (why.Length == 0 && g is null) why = "this mode's geometry lookup returned nothing";
            else if (why.Length == 0 && (g!.Value.Box.IsEmpty || g.Value.Soup.TriangleCount == 0))
                why = "empty box or empty soup";
            bool spans = false; int hits = 0;
            if (why.Length == 0 && g is { } gg)
            {
                var world = SpatialOverlap.ToWorld(gg.Box, r.Position, r.Rotation, r.Scale);
                spans = x >= world.Min.X && x <= world.Max.X && y >= world.Min.Y && y <= world.Max.Y;
                if (spans)
                {
                    col.Clear();
                    FloorFeed.Columns(gg.Soup, r.Position, r.Rotation, r.Scale)?.SurfacesAt(x, y, col);
                    hits = col.Count;
                    foreach (var (z, up) in col)
                        surfaces.Add(new Dictionary<string, object?>
                        {
                            ["z"] = z, ["upFacing"] = up,
                            ["ref"] = r.FormKey.ToString(), ["base"] = r.Base?.ToString(),
                            ["editorId"] = r.EditorId,
                            ["inBand"] = z >= lo && z <= hi,
                            ["why"] = z < lo ? "deeper than the band" : z > hi ? "higher than the band"
                                      : up ? "in the band, up-facing" : "in the band, DOWN-facing",
                        });
                }
                else why = "world box does not span this XY";
            }
            candidates.Add(new Dictionary<string, object?>
            {
                ["ref"] = r.FormKey.ToString(), ["base"] = r.Base?.ToString(),
                ["editorId"] = r.EditorId, ["scale"] = r.Scale,
                ["triangles"] = g?.Soup.TriangleCount,
                ["spansXY"] = spans, ["surfacesAtXY"] = hits,
                ["excludedBecause"] = why.Length == 0 ? null : why,
            });
        }
        // The TERRAIN is a floor too, and it is the one a mesh-only listing would miss: a station
        // over bare ground stands on the heightmap, not on a placed reference.
        float? landZ = null; float saddle = 0f;
        if (input.LandByGrid.TryGetValue(grid, out var heights))
            landZ = NavmeshFloor.LandHeightAt(heights, grid.X, grid.Y, x, y, out saddle);
        var reading = field.Read(x, y, navZ);
        var doc = new Dictionary<string, object?>
        {
            ["terrainAtXY"] = landZ is { } lz
                ? new Dictionary<string, object?>
                {
                    ["z"] = lz, ["saddle"] = saddle, ["inBand"] = lz >= lo && lz <= hi,
                    ["aboveNavmeshBy"] = lz - navZ,
                    ["why"] = lz < lo ? "deeper than the band" : lz > hi ? "higher than the band"
                              : "in the band",
                }
                : null,
            ["schema"] = "occl-transfer/station-column/1",
            ["note"] = "A diagnostic. Nothing in the station rule reads this file, and no constant "
                     + "was chosen from it.",
            ["xy"] = new[] { x, y }, ["grid"] = new[] { grid.X, grid.Y },
            ["navZ"] = navZ,
            ["band"] = new Dictionary<string, object?>
            {
                ["low"] = lo, ["high"] = hi,
                ["maxDepthBelowNavmesh"] = NavmeshFloor.DefaultMaxDepth,
                ["upReachAboveNavmesh"] = NavmeshFloor.DefaultUpReach,
            },
            ["fieldReading"] = new Dictionary<string, object?>
            {
                ["found"] = reading.Found, ["floorZ"] = StationPoseFileNumber(reading.FloorZ),
                ["source"] = reading.Source.ToString(), ["reason"] = reading.Reason.ToString(),
                ["surfacesSeen"] = reading.Surfaces, ["embedded"] = reading.Embedded,
            },
            ["candidatesInNeighbourhood"] = refs.Count,
            ["surfacesAtXY"] = surfaces,
            ["candidates"] = candidates,
        };
        File.WriteAllText(outPath, JsonSerializer.Serialize(doc, Opts));
        Console.WriteLine($"   --stations: VP_STATION_COLUMN diagnostic written to {outPath} "
                        + $"({surfaces.Count} surface(s) at the XY over {refs.Count} candidate(s))");
    }

    static readonly JsonSerializerOptions Opts = new() { WriteIndented = true };
    static float? StationPoseFileNumber(float v) => float.IsFinite(v) ? v : null;

    /// <summary>The winning navmesh's answer at one XY: whether there was a navmesh to ask at all,
    /// whether a triangle covers the point, whether any covering triangle is DRAWN (not flagged
    /// deleted), and the height. The barycentric containment test is --nav-at's, which is what the
    /// 2026-09-19 station set was measured with.
    ///
    /// Several drawn triangles can cover one XY where storeys overlap - a bridge deck over a path.
    /// The HIGHEST is taken and the count is reported, because a station stands on the top surface;
    /// the lower one would put the eye inside the deck above it.</summary>
    static (bool Answered, bool Covered, bool Drawn, float Z, int Covers) NavAt(
        IReadOnlyCollection<NavmeshGeometry> navs, float qx, float qy)
    {
        bool answered = false, covered = false;
        float bestAny = float.NaN, bestDrawn = float.NaN;
        int covers = 0;
        foreach (var nav in navs)
        {
            if (nav.Triangles.Count > 0) answered = true;
            for (int ti = 0; ti < nav.Triangles.Count; ti++)
            {
                var tri = nav.Triangles[ti];
                if (tri.A < 0 || tri.B < 0 || tri.C < 0
                    || tri.A >= nav.Vertices.Count || tri.B >= nav.Vertices.Count
                    || tri.C >= nav.Vertices.Count) continue;
                var a = V(nav.Vertices[tri.A]); var b = V(nav.Vertices[tri.B]); var c = V(nav.Vertices[tri.C]);
                float d = (b.Y - c.Y) * (a.X - c.X) + (c.X - b.X) * (a.Y - c.Y);
                if (MathF.Abs(d) < 1e-6f) continue;
                float u = ((b.Y - c.Y) * (qx - c.X) + (c.X - b.X) * (qy - c.Y)) / d;
                float v = ((c.Y - a.Y) * (qx - c.X) + (a.X - c.X) * (qy - c.Y)) / d;
                float w = 1f - u - v;
                if (u < 0f || v < 0f || w < 0f) continue;
                covered = true; covers++;
                float z = u * a.Z + v * b.Z + w * c.Z;
                if (float.IsNaN(bestAny) || z > bestAny) bestAny = z;
                if ((tri.Flags & 0x08) == 0 && (float.IsNaN(bestDrawn) || z > bestDrawn)) bestDrawn = z;
            }
        }
        bool drawn = !float.IsNaN(bestDrawn);
        return (answered, covered, drawn, drawn ? bestDrawn : bestAny, covers);
    }

    static Vector3 V(Vec3 p) => new(p.X, p.Y, p.Z);

    static bool Same(float[] a, float[] b)
    {
        if (a.Length != b.Length) return false;
        for (int i = 0; i < a.Length; i++) if (MathF.Abs(a[i] - b[i]) > 1e-6f) return false;
        return true;
    }

    static string? Str(JsonElement root, string name)
        => root.TryGetProperty(name, out var e) && e.ValueKind == JsonValueKind.String
            ? e.GetString() : null;

    static float[]? Floats(JsonElement root, string name)
    {
        if (!root.TryGetProperty(name, out var e) || e.ValueKind != JsonValueKind.Array) return null;
        var v = new List<float>();
        foreach (var x in e.EnumerateArray()) v.Add(x.GetSingle());
        return v.Count > 0 ? v.ToArray() : null;
    }

    static string Sha256(string text)
        => Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(text))).ToLowerInvariant();

    /// <summary>What composed this file, by content: the running assembly's own hash and its build
    /// time, so a pose file can be tied to the binary rather than to a claim about it.</summary>
    static Dictionary<string, object?> Instrument()
    {
        var d = new Dictionary<string, object?>();
        try
        {
            string loc = typeof(StationMode).Assembly.Location;
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
