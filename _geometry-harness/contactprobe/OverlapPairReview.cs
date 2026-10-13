using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Serialization;
using HarnessShared;
using HousecarlCore;
using HousecarlCore.Progress;
using Mutagen.Bethesda.Plugins;

/// <summary>Batch narrow-phase evidence for explicitly supplied pairs. Render surfaces on both
/// sides, with no record-bounds or collision substitute. A completed test is not a defect verdict.</summary>
internal static class OverlapPairReview
{
    static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true, PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        NumberHandling = JsonNumberHandling.AllowNamedFloatingPointLiterals,
        Converters = { new JsonStringEnumConverter() }
    };

    /// <summary>A supplied pair. <c>ADz</c>/<c>BDz</c>: a pair row's optional <c>aPose</c>/<c>bPose</c> <c>{"dz": d}</c> — the member
    /// measured translated by d in z (a rest pose written from a drop, loose-item clashes); null = the placed pose. Read by
    /// --surface and --volume, which echo the pose on the pair's record.</summary>
    internal sealed record InputPair(int InputIndex, string A, string B, string PairKey, double? ADz = null, double? BDz = null);

    /// <summary>The pose with its z moved by <paramref name="dz"/> (null: unchanged).</summary>
    internal static Snap? Shift(Snap? pose, double? dz)
        => pose is { } s && dz is { } d ? s with { Pos = new Vec3(s.Pos.X, s.Pos.Y, (float)(s.Pos.Z + d)) } : pose;

    /// <summary>A pose object for the record echo ({dz}), or null.</summary>
    internal static object? PoseEcho(double? dz) => dz is { } d ? new { dz = d } : null;
    internal sealed class PairResult(InputPair input)
    {
        public int InputIndex { get; } = input.InputIndex;
        public string PairKey { get; } = input.PairKey;
        public string A { get; } = input.A;
        public string B { get; } = input.B;
        public string Status { get; set; } = "budget_unrun";
        public string? Reason { get; set; } = "not reached within the declared batch budget";
        public SurfaceQueryOutcome? ActualShape { get; set; }
        public MeshContactEvidence? ContactEvidence { get; set; }
        public object? GeometryA { get; set; }
        public object? GeometryB { get; set; }
        /// <summary>E1: the member's mesh has a visible Tree_Anim shape (NifWind.Json); omitted otherwise.</summary>
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public object? WindA { get; set; }
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public object? WindB { get; set; }
        public double Seconds { get; set; }
    }

    internal static List<InputPair> ReadPairs(JsonElement root)
    {
        var result = new List<InputPair>();
        void Add(string? a, string? b, double? adz = null, double? bdz = null)
        {
            a ??= ""; b ??= "";
            string key = string.CompareOrdinal(a, b) <= 0 ? a + "||" + b : b + "||" + a;
            result.Add(new(result.Count, a, b, key, adz, bdz));
        }
        static double? Dz(JsonElement row, string key)
        {
            if (!row.TryGetProperty(key, out var pose)) return null;
            if (pose.ValueKind != JsonValueKind.Object || !pose.TryGetProperty("dz", out var dz) || dz.ValueKind != JsonValueKind.Number
                || pose.EnumerateObject().Count() != 1 || !double.IsFinite(dz.GetDouble()))
                throw new InvalidDataException($"pair '{key}' must be {{\"dz\": <finite number>}}");
            return dz.GetDouble();
        }
        static string? Str(JsonElement row, string key) => row.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
        if (root.ValueKind == JsonValueKind.Array)
            foreach (var row in root.EnumerateArray()) Add(Str(row, "Later") ?? Str(row, "a"), Str(row, "Other") ?? Str(row, "b"));
        else if (root.TryGetProperty("pairs", out var pairs))
            foreach (var row in pairs.EnumerateArray()) Add(Str(row, "a"), Str(row, "b"), Dz(row, "aPose"), Dz(row, "bPose"));
        else if (root.TryGetProperty("Conflicts", out var conflicts))
        {
            foreach (var c in conflicts.EnumerateArray())
            {
                if (Str(c, "Kind") != "OVERLAP") continue;
                string? other = c.TryGetProperty("Alternative", out var alt) && alt.ValueKind == JsonValueKind.Array && alt.GetArrayLength() > 0
                    ? Str(alt[0], "FormKey") : null;
                foreach (var r in c.GetProperty("Necessary").EnumerateArray()) Add(Str(r, "FormKey"), other);
            }
        }
        else throw new InvalidDataException("expected an overlap row array, pairs envelope, or overlap ConflictFile");
        return result;
    }

    sealed record Geometry(string Status, string? Reason, TriangleSoup? Soup, Aabb Box, object? Evidence, WindLabel? Wind = null,
                           SurfaceQueryOutcome.Placed? Placed = null);

    public static int Run(string inputPath, string outputPath, double budgetSeconds, double pairSeconds,
                          int maxPairs, double surfaceTolerance, long maxTrianglePairs, bool contactEvidence,
                          AssetResolver assets, OverlapIndex index,
                          EnableArms arms, ConfigurationIdentity configuration, Func<FormKey, Snap?> winningPose,
                          Run run)
    {
        var inputBytes = File.ReadAllBytes(inputPath);
        string inputSha256 = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(inputBytes)).ToLowerInvariant();
        using var input = JsonDocument.Parse(inputBytes);
        var inputPairs = ReadPairs(input.RootElement);
        var rowPose = inputPairs.Select(p => p.ADz is not null || p.BDz is not null).ToArray();
        var rows = inputPairs.Select(p => new PairResult(p)).ToList();
        var cache = new NifRenderGeometryCache();
        var clock = Stopwatch.StartNew();
        int attempts = 0;
        var fullOutput = Path.GetFullPath(outputPath);
        Directory.CreateDirectory(Path.GetDirectoryName(fullOutput)!);
        void Persist(bool finished)
        {
            var packet = new
            {
                schemaVersion = 2, job = "actual render-surface relation for supplied reference pairs",
                geometryBasis = "symmetric intersection of placed strict decoded NIF render triangles; no collision or record-bounds substitute",
                readerVersion = NifOverlapGeometry.ReaderVersion, policyVersion = NifOverlapGeometry.SurfaceMeasurementIdentity(surfaceTolerance),
                inputPath = Path.GetFullPath(inputPath), inputSha256,
                configuration, budget = new { budgetSeconds, pairSeconds, maxPairs, surfaceTolerance, maxTrianglePairs,
                    contactEvidence, contactEvidencePolicy = MeshIntersection.ContactEvidencePolicyVersion,
                    cancellation = "cooperative during each mesh query; asset decode may finish after the deadline" },
                total = rows.Count, complete = finished && rows.All(r => r.Status is not ("budget_unrun" or "running")), attempted = attempts,
                measured = rows.Count(r => r.ActualShape?.Intersects is not null),
                unknown = rows.Count(r => r.ActualShape?.Intersects is null && r.Status is not ("excluded" or "invalid_reference")),
                elapsedSeconds = clock.Elapsed.TotalSeconds,
                statusVocabulary = new[] { "budget_unrun", "running", "measured", "unknown", "unmeasurable", "excluded", "invalid_reference", "error" },
                surfaceRelationVocabulary = new[] { "crossing", "coplanar_overlap", "touching", "separated", "unknown" },
                containmentVocabulary = new[] { "a_inside_b", "b_inside_a", "component_containment", "none", "not_applicable_open_mesh", "unassessed", "unknown" },
                notes = new[] { "The predicate is symmetric; A and B retain input identity and order.",
                    "A surface relation is geometry evidence, not compatibility severity or a visible-defect verdict.",
                    "Containment is reported only after closed-manifold validation; open or invalid meshes retain an explicit limitation." },
                records = rows
            };
            File.WriteAllText(fullOutput + ".tmp", JsonSerializer.Serialize(packet, Json));
            File.Move(fullOutput + ".tmp", fullOutput, overwrite: true);
        }
        Geometry Load(FormKey key)
        {
            var pose = winningPose(key);
            if (pose is null) return new("unmeasurable", "reference has no winning placement", null, Aabb.Empty, null);
            if (index.IsDeletedNow(key) || pose.Value.Deleted) return new("excluded", "winning reference is deleted", null, Aabb.Empty, null);
            var enabled = arms.Winner(key);
            if (!enabled.Resolved) return new("unmeasurable", "enable chain is unresolved", null, Aabb.Empty, null);
            if (!enabled.Enabled) return new("excluded", "reference is disabled in the declared state", null, Aabb.Empty, null);
            if (!index.RefBase.TryGetValue(key, out var basis)) return new("unmeasurable", "winning reference has no base", null, Aabb.Empty, null);
            if (index.BaseIsMarker.Contains(basis)) return new("excluded", "winning base is an editor marker", null, Aabb.Empty, null);
            if (index.BosSwaps.TryGetValue(key, out var bos) && (bos.Random || bos.PropertiesRandom || bos.PerSession || bos.Alternatives.Count != 1))
                return new("unmeasurable", "BOS effective base or pose depends on an unresolved random/session choice", null, Aabb.Empty, null);
            if (!index.BaseMesh.TryGetValue(basis, out var path)) return new("unmeasurable", "missing_asset: winning base has no model path", null, Aabb.Empty, null);
            var relative = path.StartsWith("meshes", StringComparison.OrdinalIgnoreCase) ? path : Path.Combine("meshes", path);
            var resolution = assets.ResolveForPlacement(relative);
            if (resolution.ReadIncomplete && (resolution.Sources.Count == 0 || resolution.Sources[0].Kind == AssetKind.Bsa))
                return new("unmeasurable", "asset resolution incomplete: archived precedence cannot be established", null, Aabb.Empty, resolution);
            if (resolution.Sources.Count == 0) return new("unmeasurable", "missing_asset: no provider for the winning model path", null, Aabb.Empty, resolution);
            var source = resolution.Sources[0];
            var (bytes, error) = AssetResolver.ReadPlacementSource(source);
            if (bytes is null) return new("unmeasurable", "asset read failed: " + error, null, Aabb.Empty, resolution);
            var decoded = cache.Measure(bytes, includeTriangles: true);
            var wind = NifWind.Label(bytes);
            var evidence = new { formid = key.ToString(), baseFormid = basis.ToString(), relativePath = relative,
                source, providers = resolution.Sources, resolution.Ambiguous, decoded.ContentSha256,
                decoded.Status, decoded.Reason, decoded.CacheKey, pose = pose.Value };
            if (!decoded.UsableForSurfaceIntersection || decoded.Soup is not { TriangleCount: > 0 } soup)
                return new("unmeasurable", decoded.Status + ": " + (decoded.Reason ?? "no complete render triangles"), null, Aabb.Empty, evidence, wind);
            var s = pose.Value;
            var world = SpatialOverlap.TransformSoup(soup, s.Pos, s.Rot, s.Scale);
            var worldBounds = SpatialOverlap.SoupBounds(world);
            if (worldBounds.IsEmpty || !SpatialOverlap.IsFinite(worldBounds))
                return new("unmeasurable", "invalid_transform: placed render triangles have no finite bounds", null, Aabb.Empty, evidence, wind);
            return new("measured", null, world, worldBounds, evidence, wind, new SurfaceQueryOutcome.Placed(soup, s.Pos, s.Rot, s.Scale));
        }
        Persist(false);
        run.Feature("pairs", rows.Count, "pairs").Stage("overlap-contact", "testing render surface pairs", rows.Count, "pairs");
        foreach (var row in rows)
        {
            if (attempts >= maxPairs || clock.Elapsed.TotalSeconds >= budgetSeconds) break;
            attempts++;
            row.Status = "running"; row.Reason = "contact query started; no result yet"; Persist(false);
            var timer = Stopwatch.StartNew();
            try
            {
                if (!FormKey.TryFactory(row.A, out var a) || !FormKey.TryFactory(row.B, out var b) || a.IsNull || b.IsNull || a == b)
                { row.Status = "invalid_reference"; row.Reason = "two distinct full reference keys are required"; continue; }
                if (rowPose[row.InputIndex])
                { row.Status = "excluded"; row.Reason = "the pair carries aPose/bPose, which only --surface and --volume read"; continue; }
                var ga = Load(a); var gb = Load(b); row.GeometryA = ga.Evidence; row.GeometryB = gb.Evidence;
                row.WindA = NifWind.Json(ga.Wind); row.WindB = NifWind.Json(gb.Wind);
                if (ga.Status != "measured" || gb.Status != "measured")
                { row.Status = ga.Status == "excluded" || gb.Status == "excluded" ? "excluded" : "unmeasurable"; row.Reason = $"A: {ga.Status}: {ga.Reason}; B: {gb.Status}: {gb.Reason}"; continue; }
                var pa = winningPose(a)!.Value; var pb = winningPose(b)!.Value;
                if (pa.Space != pb.Space) { row.Status = "excluded"; row.Reason = "references occupy different spaces"; continue; }
                double secondsLeft = Math.Min(pairSeconds - timer.Elapsed.TotalSeconds, budgetSeconds - clock.Elapsed.TotalSeconds);
                if (secondsLeft <= 0) throw new OperationCanceledException("budget expired during geometry preparation");
                using var cancel = new CancellationTokenSource(TimeSpan.FromSeconds(secondsLeft));
                row.ActualShape = SurfaceQueryOutcome.QueryPlaced(ga.Soup!, ga.Placed, gb.Soup!, gb.Placed, surfaceTolerance,
                    maxTrianglePairs, TimeSpan.FromSeconds(secondsLeft), cancel.Token);
                if (contactEvidence)
                    row.ContactEvidence = MeshIntersection.MeasureContactEvidence(
                        ga.Soup!, gb.Soup!, surfaceTolerance, maxTrianglePairs, cancel.Token);
                row.Status = row.ActualShape.Intersects is null ? "unknown" : "measured";
                row.Reason = row.ActualShape.Reason;
            }
            catch (OperationCanceledException ex) { row.Status = "unknown"; row.Reason = ex.Message; }
            catch (Exception ex) { row.Status = "error"; row.Reason = ex.GetType().Name + ": " + ex.Message; }
            finally { row.Seconds = timer.Elapsed.TotalSeconds; Persist(false); run.Advance(); }
        }
        Persist(true);
        run.Tally(ok: rows.Count(r => r.Status == "measured"), skipped: rows.Count(r => r.Status is "excluded" or "budget_unrun"),
                  inconclusive: rows.Count(r => r.Status is "unmeasurable" or "unknown" or "invalid_reference"), failed: rows.Count(r => r.Status == "error"));
        run.Feature("attemptedPairs", attempts, "pairs").Feature("cacheHits", cache.Hits, "lookups").Feature("decodedAssets", cache.Misses, "assets").Artifact(fullOutput);
        Console.WriteLine($"actual render surfaces: {rows.Count} supplied rows, {attempts} attempted, {rows.Count(r => r.Status == "measured")} measured, {rows.Count(r => r.Status == "unknown")} unknown in {clock.Elapsed.TotalSeconds:0.0}s; {fullOutput}");
        return rows.Any(r => r.Status == "error") ? 2 : 0;
    }
}
