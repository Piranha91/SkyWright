using System.Buffers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace ViewProbe;

/// <summary>
/// A static worklist launch's immutable selection and create-new per-shot completion receipts.
/// The renderer still consumes the original worklist: coordinates are filtered, never sliced or
/// renumbered.  Composite rows additionally bind each copied shot to its original one-shot caller.
/// </summary>
internal sealed class BatchProgress
{
    internal sealed record Coordinate([property: JsonRequired] int Cell,
        [property: JsonRequired] int Shot);
    internal sealed record CallerArtifact([property: JsonRequired] string Path,
        [property: JsonRequired] string Kind);
    internal sealed record SourceRow(
        [property: JsonRequired] int Ordinal,
        [property: JsonRequired] string SourceWorklist,
        [property: JsonRequired] string SourceWorklistSha256,
        [property: JsonRequired] Coordinate SourceCoordinate,
        [property: JsonRequired] Coordinate CompositeCoordinate,
        [property: JsonRequired] string Label,
        [property: JsonRequired] string ShotSha256,
        [property: JsonRequired] string OutputStem,
        [property: JsonRequired] string ScratchDirectory,
        [property: JsonRequired] List<CallerArtifact> CallerArtifacts);
    internal sealed record Artifact(string Path, string Kind, string Sha256);
    internal sealed record ShotPlan(string ReplayWorklistPath, string? OutputStem,
        string? ScratchDirectory, SourceRow? Source);

    private sealed record Selection(
        [property: JsonRequired] string Schema,
        [property: JsonRequired] string WorklistSha256,
        [property: JsonRequired] string ReadinessSha256,
        [property: JsonRequired] List<Coordinate> Selected,
        string? SourceMapPath = null,
        string? SourceMapSha256 = null,
        string? SourceMapIdentityAlgorithm = null,
        List<SourceRow>? Sources = null);

    private sealed record SourceMap(
        [property: JsonRequired] string Schema,
        [property: JsonRequired] string SourceMapPath,
        [property: JsonRequired] string IdentityAlgorithm,
        [property: JsonRequired] string CompositeWorklist,
        [property: JsonRequired] string CompositeWorklistSha256,
        [property: JsonRequired] List<SourceRow> Rows);

    internal sealed class ShotArtifacts
    {
        internal static readonly HashSet<string> Kinds = new(StringComparer.Ordinal)
            { "frame", "report", "mask", "console", "census", "depth", "navmesh", "sidecar", "replay" };
        private readonly List<(string Path, string Kind)> files = new();
        private readonly HashSet<string> paths = new(StringComparer.OrdinalIgnoreCase);

        internal string? ReplayArgs { get; private set; }
        internal IReadOnlyList<(string Path, string Kind)> Files => files;

        internal void SetReplay(string replay) => ReplayArgs = replay;

        internal void AddClosed(string path, string kind)
        {
            if (!Kinds.Contains(kind)) throw new InvalidDataException($"unknown artifact kind: {kind}");
            string full = Path.GetFullPath(path);
            if (!File.Exists(full)) throw new FileNotFoundException("closed artifact is missing", full);
            if (!paths.Add(full)) throw new InvalidDataException($"duplicate artifact path: {full}");
            files.Add((full, kind));
        }
    }

    private sealed record ReportReferences(string Image, string? SceneDepth,
        string? SceneDepthSha256, string ReplayArgs, string? ViewLabel,
        IReadOnlyList<string> VisibilityMasks);

    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNameCaseInsensitive = true,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
    };
    private static readonly JsonWriterOptions CanonicalWriter = new()
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        Indented = false,
    };

    private readonly HashSet<(int Cell, int Shot)> selected;
    private readonly Dictionary<(int Cell, int Shot), SourceRow> sources;
    private readonly string originalWorklistPath;
    private readonly string worklistSha256;
    private readonly string readinessSha256;
    private readonly string progressDir;
    private readonly string? sourceMapPath;
    private readonly string? sourceMapSha256;

    private BatchProgress(Selection value, string dir, string worklistPath)
    {
        selected = value.Selected.Select(x => (x.Cell, x.Shot)).ToHashSet();
        sources = (value.Sources ?? new()).ToDictionary(x => (x.CompositeCoordinate.Cell,
                                                               x.CompositeCoordinate.Shot));
        originalWorklistPath = Path.GetFullPath(worklistPath);
        worklistSha256 = value.WorklistSha256;
        readinessSha256 = value.ReadinessSha256;
        progressDir = Path.GetFullPath(dir);
        sourceMapPath = value.SourceMapPath is null ? null : Path.GetFullPath(value.SourceMapPath);
        sourceMapSha256 = value.SourceMapSha256;
    }

    internal static BatchProgress Load(string selectionPath, string progressDir,
        string originalWorklistPath, string expectedReadinessSha256,
        IReadOnlyList<int> shotCounts)
    {
        Selection value = ReadJson<Selection>(selectionPath, "selection");
        if (value.Schema != "viewprobe-worklist-selection-v1")
            throw new InvalidDataException("selection schema differs");
        RequireSha(value.WorklistSha256, "selection worklist identity");
        RequireSha(value.ReadinessSha256, "selection readiness identity");
        RequireSha(expectedReadinessSha256, "pinned readiness identity");
        if (!value.WorklistSha256.Equals(Hash(originalWorklistPath), StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("selection worklist digest differs");
        if (!value.ReadinessSha256.Equals(expectedReadinessSha256, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("selection readiness identity differs from the pinned command value");
        if (value.Selected.Count == 0 || value.Selected.Any(x => x is null)
            || value.Selected.Select(x => (x.Cell, x.Shot)).Distinct().Count() != value.Selected.Count)
            throw new InvalidDataException("selection is empty or contains duplicate/null coordinates");
        if (value.Selected.Select(x => x.Cell).Distinct().Count() != 1)
            throw new InvalidDataException("one launch must select one original cell");
        foreach (var x in value.Selected)
            if (x.Cell < 0 || x.Cell >= shotCounts.Count || x.Shot < 0 || x.Shot >= shotCounts[x.Cell])
                throw new InvalidDataException($"selection coordinate is out of range: {x.Cell},{x.Shot}");

        bool anyComposite = value.SourceMapPath is not null || value.SourceMapSha256 is not null
            || value.SourceMapIdentityAlgorithm is not null || value.Sources is not null;
        if (anyComposite)
            ValidateComposite(value, originalWorklistPath);

        // No directory is created until every selection, worklist, source and map check succeeded.
        Directory.CreateDirectory(progressDir);
        return new BatchProgress(value, progressDir, originalWorklistPath);
    }

    internal bool Contains(int cell, int shot) => selected.Contains((cell, shot));
    internal int SelectedCount => selected.Count;
    // Conservative process-wide guard: archive extraction failures are recorded in Entries/Count
    // but not ShotEntries, and a repeated failure may not increment Count. Once any unsuppressed
    // unreadable input exists, no later shot can be certified clean inside this process.
    internal static bool ShotMayCommit(int processUnreadableCount, string? optOutSource)
        => processUnreadableCount == 0 || optOutSource is not null;

    internal static void RefuseExistingOutputs(IEnumerable<string> files,
                                                IEnumerable<string> directories)
    {
        foreach (string directory in directories.Select(Path.GetFullPath)
                     .Distinct(StringComparer.OrdinalIgnoreCase))
            if (Directory.Exists(directory))
                throw new IOException("Refusing existing batch output directory: " + directory);
        foreach (string file in files.Select(Path.GetFullPath).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (File.Exists(file)) throw new IOException("Refusing existing batch output: " + file);
            if (File.Exists(file + ".writing"))
                throw new IOException("Refusing existing partial batch output: " + file + ".writing");
        }
    }

    internal static void WriteTextCreateNew(string path, string text)
    {
        using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        using var writer = new StreamWriter(stream, new UTF8Encoding(false));
        writer.Write(text); writer.Flush(); stream.Flush(true);
    }

    internal ShotPlan PlanFor(int cell, int shot)
    {
        if (!Contains(cell, shot)) throw new InvalidOperationException("shot is not selected");
        if (sources.TryGetValue((cell, shot), out var source))
            return new ShotPlan(Path.GetFullPath(source.SourceWorklist), source.OutputStem,
                                source.ScratchDirectory, source);
        return new ShotPlan(originalWorklistPath, null, null, null);
    }

    internal void CommitShot(int cell, int shot, string label, ShotArtifacts completed)
    {
        if (!Contains(cell, shot)) throw new InvalidOperationException("shot is not selected");
        VerifyInputsUnchanged();
        if (completed.Files.Count == 0) throw new InvalidDataException("shot has no declared outputs");
        if (completed.ReplayArgs is null) throw new InvalidDataException("shot replay was not captured");

        var artifacts = completed.Files.Select(x => new Artifact(x.Path, x.Kind, Hash(x.Path))).ToList();
        SourceRow? source = sources.GetValueOrDefault((cell, shot));
        if (source is not null)
        {
            if (!string.Equals(label, source.Label, StringComparison.Ordinal))
                throw new InvalidDataException("rendered label differs from the composite source row");
            var byIdentity = artifacts.ToDictionary(x => x.Kind + "\0" + Path.GetFileName(x.Path),
                x => x, StringComparer.OrdinalIgnoreCase);
            var ordered = new List<Artifact>();
            foreach (var expected in source.CallerArtifacts)
            {
                string identity = expected.Kind + "\0" + Path.GetFileName(expected.Path);
                if (!byIdentity.Remove(identity, out var actual))
                    throw new InvalidDataException($"composite artifact is missing or renamed: {expected.Kind} {Path.GetFileName(expected.Path)}");
                ordered.Add(actual);
            }
            if (byIdentity.Count != 0)
                throw new InvalidDataException("composite shot produced artifacts absent from the frozen caller map: "
                    + string.Join(", ", byIdentity.Keys.Select(x => x.Replace('\0', ':'))));
            artifacts = ordered;
        }

        var frames = artifacts.Where(x => x.Kind == "frame").Select(x => x.Path)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var depths = artifacts.Where(x => x.Kind == "depth").ToDictionary(x => x.Path,
            StringComparer.OrdinalIgnoreCase);
        var masks = artifacts.Where(x => x.Kind == "mask").Select(x => x.Path)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var referencedMasks = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var report in artifacts.Where(x => x.Kind == "report"))
        {
            var refs = ReadReportReferences(report.Path);
            if (!frames.Contains(FullReference(refs.Image)))
                throw new InvalidDataException($"report image is not a declared frame: {refs.Image}");
            if (!string.Equals(refs.ReplayArgs, completed.ReplayArgs, StringComparison.Ordinal))
                throw new InvalidDataException("report replay differs from the rendered shot replay");
            if (source is not null && !string.Equals(refs.ViewLabel, source.Label, StringComparison.Ordinal))
                throw new InvalidDataException("report label differs from the composite source row");
            if (refs.SceneDepth is not null)
            {
                string path = FullReference(refs.SceneDepth);
                if (!depths.TryGetValue(path, out var depth))
                    throw new InvalidDataException($"report sceneDepth is not a declared depth artifact: {path}");
                if (refs.SceneDepthSha256 is null
                    || !depth.Sha256.Equals(refs.SceneDepthSha256, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("report sceneDepthSha256 differs from the closed depth artifact");
            }
            else if (refs.SceneDepthSha256 is not null)
                throw new InvalidDataException("report has a sceneDepthSha256 without sceneDepth");
            foreach (var mask in refs.VisibilityMasks)
            {
                string full = FullReference(mask);
                if (!masks.Contains(full))
                    throw new InvalidDataException($"report visibilityMask is not a declared mask: {full}");
                referencedMasks.Add(full);
            }
        }
        if (!masks.SetEquals(referencedMasks))
            throw new InvalidDataException("declared mask set differs from the union of report visibilityMask references");
        if (artifacts.Count(x => x.Kind == "report") == 0 && (depths.Count != 0 || masks.Count != 0))
            throw new InvalidDataException("depth/mask artifacts exist without a report that owns them");

        var sourceReceipt = source is null ? null : new
        {
            sourceMapPath, sourceMapSha256,
            source.Ordinal, source.SourceWorklist, source.SourceWorklistSha256,
            source.SourceCoordinate, source.CompositeCoordinate, source.Label,
            source.ShotSha256, source.OutputStem, source.ScratchDirectory,
        };
        var receipt = new
        {
            schema = "viewprobe-shot-progress-v1", status = "complete", exitCode = 0,
            readinessSha256, worklistSha256, coordinate = new { cell, shot }, label,
            replayArgs = completed.ReplayArgs, source = sourceReceipt,
            artifacts, evidenceMasks = masks.Order(StringComparer.OrdinalIgnoreCase).ToArray(),
        };
        string destination = Path.Combine(progressDir, $"c{cell + 1}s{shot + 1}.json");
        string temporary = destination + ".tmp";
        if (File.Exists(destination) || File.Exists(temporary))
            throw new IOException($"progress receipt already exists: {destination}");
        try
        {
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write,
                                               FileShare.None))
            {
                JsonSerializer.Serialize(stream, receipt, new JsonSerializerOptions
                {
                    WriteIndented = true,
                    PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
                });
                stream.Flush(true);
            }
            File.Move(temporary, destination, false);
        }
        catch
        {
            try { if (File.Exists(temporary)) File.Delete(temporary); } catch { }
            throw;
        }
    }

    internal static string Hash(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        return Convert.ToHexString(SHA256.HashData(stream));
    }

    private void VerifyInputsUnchanged()
    {
        if (!worklistSha256.Equals(Hash(originalWorklistPath), StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("worklist changed before shot completion");
        if (sourceMapPath is not null && !sourceMapSha256!.Equals(Hash(sourceMapPath), StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("source map changed before shot completion");
        foreach (var source in sources.Values)
            if (!source.SourceWorklistSha256.Equals(Hash(source.SourceWorklist), StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException($"source worklist changed before shot completion: {source.SourceWorklist}");
    }

    private static void ValidateComposite(Selection value, string originalWorklistPath)
    {
        if (value.SourceMapPath is null || value.SourceMapSha256 is null
            || value.SourceMapIdentityAlgorithm is null || value.Sources is null)
            throw new InvalidDataException("composite selection has only part of its source-map identity");
        RequireSha(value.SourceMapSha256, "source-map identity");
        if (value.SourceMapIdentityAlgorithm != "sha256-file-bytes-v1")
            throw new InvalidDataException("source-map identity algorithm differs");
        string mapPath = Path.GetFullPath(value.SourceMapPath);
        if (!value.SourceMapSha256.Equals(Hash(mapPath), StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("source-map file digest differs");
        SourceMap map = ReadJson<SourceMap>(mapPath, "source map");
        if (map.Schema != "viewprobe-composite-source-map-v1"
            || map.IdentityAlgorithm != "sha256-file-bytes-v1")
            throw new InvalidDataException("source-map schema or identity algorithm differs");
        if (!Path.GetFullPath(map.SourceMapPath).Equals(mapPath, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("source map names a different sourceMapPath");
        string worklistPath = Path.GetFullPath(originalWorklistPath);
        if (!Path.GetFullPath(map.CompositeWorklist).Equals(worklistPath, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("source map names a different composite worklist");
        RequireSha(map.CompositeWorklistSha256, "source-map composite worklist identity");
        if (!map.CompositeWorklistSha256.Equals(value.WorklistSha256, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("source-map composite worklist digest differs");
        if (map.Rows.Count == 0 || map.Rows.Any(x => x is null)
            || map.Rows.Select(x => (x.CompositeCoordinate.Cell, x.CompositeCoordinate.Shot)).Distinct().Count() != map.Rows.Count)
            throw new InvalidDataException("source map has no rows or duplicate/null composite coordinates");
        if (map.Rows.Select(x => x.ScratchDirectory).Distinct(StringComparer.OrdinalIgnoreCase).Count() != map.Rows.Count)
            throw new InvalidDataException("source-map rows have duplicate scratch destinations");
        using (var compositeDoc = JsonDocument.Parse(File.ReadAllBytes(originalWorklistPath)))
        {
            var cells = Property(compositeDoc.RootElement, "cells");
            if (cells.ValueKind != JsonValueKind.Array || cells.GetArrayLength() != 1)
                throw new InvalidDataException("composite worklist must contain exactly one cell");
            var shots = Property(cells[0], "shots");
            if (shots.ValueKind != JsonValueKind.Array || shots.GetArrayLength() != map.Rows.Count)
                throw new InvalidDataException("source map does not cover every composite shot exactly once");
        }
        for (int i = 0; i < map.Rows.Count; i++)
        {
            var row = map.Rows[i];
            if (row.Ordinal != i + 1 || row.CompositeCoordinate.Cell != 0 || row.CompositeCoordinate.Shot != i)
                throw new InvalidDataException("source-map rows are reordered or do not cover consecutive composite coordinates");
            ValidateSourceRow(row, originalWorklistPath);
        }
        if (value.Sources.Count != value.Selected.Count)
            throw new InvalidDataException("selection must carry one source row per selected coordinate");
        var mapRows = map.Rows.ToDictionary(x => (x.CompositeCoordinate.Cell, x.CompositeCoordinate.Shot));
        for (int i = 0; i < value.Selected.Count; i++)
        {
            var coordinate = value.Selected[i];
            var row = value.Sources[i];
            if (row is null || row.CompositeCoordinate != coordinate)
                throw new InvalidDataException("source rows are missing, extra or reordered against selected coordinates");
            if (!mapRows.TryGetValue((coordinate.Cell, coordinate.Shot), out var mapped)
                || !SameSourceRow(row, mapped))
                throw new InvalidDataException("inline source row differs from the immutable source map");
        }
    }

    private static void ValidateSourceRow(SourceRow row, string compositeWorklistPath)
    {
        if (row.Ordinal <= 0 || row.SourceCoordinate.Cell != 0 || row.SourceCoordinate.Shot != 0)
            throw new InvalidDataException("source row ordinal/one-shot coordinate is invalid");
        RequireSha(row.SourceWorklistSha256, "source worklist identity");
        RequireSha(row.ShotSha256, "source shot identity");
        if (!row.SourceWorklistSha256.Equals(Hash(row.SourceWorklist), StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException($"source worklist digest differs: {row.SourceWorklist}");
        if (!SafeStem(row.OutputStem)) throw new InvalidDataException("source outputStem is not a safe file stem");
        if (!SafeDirectoryName(row.ScratchDirectory))
            throw new InvalidDataException("source scratchDirectory is not a safe single directory name");
        if (row.CallerArtifacts.Count == 0 || row.CallerArtifacts.Any(x => x is null))
            throw new InvalidDataException("source row has no caller artifacts");
        var artifactIdentities = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var artifact in row.CallerArtifacts)
        {
            if (!ShotArtifacts.Kinds.Contains(artifact.Kind))
                throw new InvalidDataException($"source row has unknown artifact kind: {artifact.Kind}");
            string name = Path.GetFileName(artifact.Path);
            if (string.IsNullOrEmpty(name) || !artifactIdentities.Add(artifact.Kind + "\0" + name))
                throw new InvalidDataException("source row has an invalid or duplicate caller artifact identity");
            _ = Path.GetFullPath(artifact.Path);
        }

        using var sourceDoc = JsonDocument.Parse(File.ReadAllBytes(row.SourceWorklist));
        using var compositeDoc = JsonDocument.Parse(File.ReadAllBytes(compositeWorklistPath));
        var sourceCells = Property(sourceDoc.RootElement, "cells");
        if (sourceCells.ValueKind != JsonValueKind.Array || sourceCells.GetArrayLength() != 1)
            throw new InvalidDataException("source worklist is not exactly one cell");
        var sourceShots = Property(sourceCells[0], "shots");
        if (sourceShots.ValueKind != JsonValueKind.Array || sourceShots.GetArrayLength() != 1)
            throw new InvalidDataException("source worklist is not exactly one shot");
        var compositeCells = Property(compositeDoc.RootElement, "cells");
        if (!CanonicalObjectWithout(sourceDoc.RootElement, "cells", "label", "generatedUtc")
                .AsSpan().SequenceEqual(CanonicalObjectWithout(compositeDoc.RootElement,
                    "cells", "label", "generatedUtc")))
            throw new InvalidDataException("source/composite worklist common options differ");
        if (!CanonicalObjectWithout(sourceCells[0], "shots").AsSpan().SequenceEqual(
                CanonicalObjectWithout(compositeCells[row.CompositeCoordinate.Cell], "shots")))
            throw new InvalidDataException("source/composite cell options differ");
        var sourceShot = Shot(sourceDoc.RootElement, 0, 0);
        var compositeShot = Shot(compositeDoc.RootElement, row.CompositeCoordinate.Cell,
                                 row.CompositeCoordinate.Shot);
        byte[] sourceCanonical = Canonical(sourceShot);
        byte[] compositeCanonical = Canonical(compositeShot);
        if (!sourceCanonical.AsSpan().SequenceEqual(compositeCanonical))
            throw new InvalidDataException("composite shot differs from its original source shot");
        string shotHash = Convert.ToHexString(SHA256.HashData(compositeCanonical));
        if (!shotHash.Equals(row.ShotSha256, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("source row shotSha256 differs from the copied shot");
        string label = Property(compositeShot, "label").GetString() ?? "";
        if (!string.Equals(label, row.Label, StringComparison.Ordinal))
            throw new InvalidDataException("source row label differs from the copied shot");
    }

    private static bool SameSourceRow(SourceRow a, SourceRow b)
        => a.Ordinal == b.Ordinal && Path.GetFullPath(a.SourceWorklist).Equals(Path.GetFullPath(b.SourceWorklist), StringComparison.OrdinalIgnoreCase)
        && a.SourceWorklistSha256.Equals(b.SourceWorklistSha256, StringComparison.OrdinalIgnoreCase)
        && a.SourceCoordinate == b.SourceCoordinate && a.CompositeCoordinate == b.CompositeCoordinate
        && a.Label == b.Label && a.ShotSha256.Equals(b.ShotSha256, StringComparison.OrdinalIgnoreCase)
        && a.OutputStem == b.OutputStem && a.ScratchDirectory == b.ScratchDirectory
        && a.CallerArtifacts.Count == b.CallerArtifacts.Count
        && a.CallerArtifacts.Zip(b.CallerArtifacts).All(x => x.First.Kind == x.Second.Kind
            && Path.GetFullPath(x.First.Path).Equals(Path.GetFullPath(x.Second.Path), StringComparison.OrdinalIgnoreCase));

    private static T ReadJson<T>(string path, string label)
    {
        try
        {
            return JsonSerializer.Deserialize<T>(File.ReadAllBytes(path), Json)
                ?? throw new InvalidDataException($"{label} is empty");
        }
        catch (JsonException ex) { throw new InvalidDataException($"{label} shape is invalid", ex); }
    }

    private static JsonElement Shot(JsonElement root, int cell, int shot)
    {
        var cells = Property(root, "cells");
        if (cells.ValueKind != JsonValueKind.Array || cell < 0 || cell >= cells.GetArrayLength())
            throw new InvalidDataException("worklist source cell is absent");
        var shots = Property(cells[cell], "shots");
        if (shots.ValueKind != JsonValueKind.Array || shot < 0 || shot >= shots.GetArrayLength())
            throw new InvalidDataException("worklist source shot is absent");
        return shots[shot];
    }

    private static JsonElement Property(JsonElement element, string name)
    {
        foreach (var property in element.EnumerateObject())
            if (property.Name.Equals(name, StringComparison.OrdinalIgnoreCase)) return property.Value;
        throw new InvalidDataException($"JSON object lacks '{name}'");
    }

    private static byte[] Canonical(JsonElement value)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream, CanonicalWriter)) WriteCanonical(writer, value);
        return stream.ToArray();
    }

    private static byte[] CanonicalObjectWithout(JsonElement value, params string[] excluded)
    {
        if (value.ValueKind != JsonValueKind.Object) throw new InvalidDataException("expected a JSON object");
        var skip = excluded.ToHashSet(StringComparer.OrdinalIgnoreCase);
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream, CanonicalWriter))
        {
            writer.WriteStartObject();
            foreach (var property in value.EnumerateObject().Where(x => !skip.Contains(x.Name))
                         .OrderBy(x => x.Name, StringComparer.Ordinal))
            { writer.WritePropertyName(property.Name); WriteCanonical(writer, property.Value); }
            writer.WriteEndObject();
        }
        return stream.ToArray();
    }

    private static void WriteCanonical(Utf8JsonWriter writer, JsonElement value)
    {
        switch (value.ValueKind)
        {
            case JsonValueKind.Object:
                writer.WriteStartObject();
                foreach (var property in value.EnumerateObject().OrderBy(x => x.Name, StringComparer.Ordinal))
                { writer.WritePropertyName(property.Name); WriteCanonical(writer, property.Value); }
                writer.WriteEndObject();
                break;
            case JsonValueKind.Array:
                writer.WriteStartArray(); foreach (var item in value.EnumerateArray()) WriteCanonical(writer, item); writer.WriteEndArray();
                break;
            case JsonValueKind.String: writer.WriteStringValue(value.GetString()); break;
            case JsonValueKind.Number: writer.WriteRawValue(value.GetRawText(), skipInputValidation: true); break;
            case JsonValueKind.True: writer.WriteBooleanValue(true); break;
            case JsonValueKind.False: writer.WriteBooleanValue(false); break;
            case JsonValueKind.Null: writer.WriteNullValue(); break;
            default: throw new InvalidDataException("unsupported JSON value in shot identity");
        }
    }

    private static ReportReferences ReadReportReferences(string path)
    {
        string? image = null, sceneDepth = null, sceneDepthSha = null, replay = null, viewLabel = null;
        var seenTop = new HashSet<string>(StringComparer.Ordinal);
        var masks = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        byte[] buffer = ArrayPool<byte>.Shared.Rent(64 * 1024);
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            var state = new JsonReaderState(new JsonReaderOptions { MaxDepth = 256 });
            int buffered = 0;
            string? pending = null;
            int pendingDepth = -1;
            while (true)
            {
                int read = stream.Read(buffer, buffered, buffer.Length - buffered);
                bool final = read == 0;
                int available = buffered + read;
                var reader = new Utf8JsonReader(buffer.AsSpan(0, available), final, state);
                while (reader.Read())
                {
                    if (reader.TokenType == JsonTokenType.PropertyName)
                    {
                        pending = reader.GetString(); pendingDepth = reader.CurrentDepth; continue;
                    }
                    if (pending is not null)
                    {
                        string name = pending; int depth = pendingDepth; pending = null; pendingDepth = -1;
                        bool top = depth == 1;
                        bool relevant = (top && (name is "image" or "sceneDepth" or "sceneDepthSha256" or "replayArgs" or "viewLabel"))
                                     || name == "visibilityMask";
                        if (!relevant) continue;
                        string? text = reader.TokenType == JsonTokenType.String ? reader.GetString()
                            : reader.TokenType == JsonTokenType.Null ? null
                            : throw new InvalidDataException($"report property '{name}' is not string/null");
                        if (top && !seenTop.Add(name))
                            throw new InvalidDataException($"report repeats '{name}'");
                        if (top && name == "image") SetValue(ref image, text, name);
                        else if (top && name == "sceneDepth") sceneDepth = text;
                        else if (top && name == "sceneDepthSha256") sceneDepthSha = text;
                        else if (top && name == "replayArgs") SetValue(ref replay, text, name);
                        else if (top && name == "viewLabel") viewLabel = text;
                        else if (name == "visibilityMask" && text is not null) masks.Add(text);
                    }
                }
                int consumed = checked((int)reader.BytesConsumed);
                int remaining = available - consumed;
                if (remaining > 0) Buffer.BlockCopy(buffer, consumed, buffer, 0, remaining);
                state = reader.CurrentState;
                buffered = remaining;
                if (final)
                {
                    if (remaining != 0) throw new InvalidDataException("report JSON ended with an incomplete token");
                    break;
                }
                if (buffered == buffer.Length)
                {
                    if (buffer.Length >= 1024 * 1024)
                        throw new InvalidDataException("report contains a JSON token larger than the 1 MiB validation bound");
                    byte[] larger = ArrayPool<byte>.Shared.Rent(buffer.Length * 2);
                    Buffer.BlockCopy(buffer, 0, larger, 0, buffered);
                    ArrayPool<byte>.Shared.Return(buffer); buffer = larger;
                }
            }
        }
        catch (JsonException ex) { throw new InvalidDataException("report JSON is malformed", ex); }
        finally { ArrayPool<byte>.Shared.Return(buffer); }
        if (image is null || replay is null)
            throw new InvalidDataException("report lacks image or replayArgs");
        return new ReportReferences(image, sceneDepth, sceneDepthSha, replay, viewLabel, masks.ToArray());
    }

    private static void SetValue(ref string? target, string? value, string name)
    {
        if (value is null) throw new InvalidDataException($"report '{name}' is null");
        target = value;
    }

    private static string FullReference(string value) => Path.GetFullPath(value);
    private static void RequireSha(string value, string label)
    {
        if (value is null || value.Length != 64 || value.Any(c => !Uri.IsHexDigit(c)))
            throw new InvalidDataException($"{label} is not a SHA-256 digest");
    }
    private static bool SafeStem(string value) => !string.IsNullOrWhiteSpace(value)
        && value is not "." and not ".." && Path.GetFileName(value) == value
        && value.IndexOfAny(Path.GetInvalidFileNameChars()) < 0;
    private static bool SafeDirectoryName(string value) => !string.IsNullOrEmpty(value)
        && char.IsAsciiLetterOrDigit(value[0])
        && value.All(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '_' or '-');
}
