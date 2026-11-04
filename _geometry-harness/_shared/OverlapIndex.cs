using System.Numerics;
using HousecarlCore;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Plugins.Aspects;
using Mutagen.Bethesda.Skyrim;

namespace HarnessShared;

// THE OVERLAP INDEX — the I/O half of the cross-plugin overlap pass (milestone 5, roadmap 4.1).
//
// overlapprobe's first cut (2026-08-27) carried its own "last plugin to SET a placement" bookkeeping.
// This moves that onto PlacementHistory — the same changed-only history and authoring view that
// driftprobe's scenario check and depprobe's dependency pass run on — so "responsible" means one
// thing across the detectors, and so the pass can ask the question the first cut could not: what
// did this reference look like BEFORE the later plugin touched it? That is the differential arm.
//
// Everything here is bookkeeping and geometry plumbing: placements into the history (with the
// rotation and scale the world box needs), base-record types and mesh paths, the candidate
// build, the spatial hash, and the facts for one hit. The DECISION is DetectorModel.OverlapProvenance,
// owned by proofprobe; the broad-phase test is DetectorModel.BoxOverlap, likewise. Shared between
// overlapprobe (the real order, meshes through the VFS) and proofprobe (a synthetic order written with
// Mutagen, injected synthetic boxes), so the proof exercises the same extraction and the same arm construction
// the live run uses.
//
// Standing rules applied: complete decoded NIF render geometry only; no geometry = not
// judged, counted (rule 6); the same-author floor measured before any cross count is read; a broad
// phase is a candidate, never a verdict; nothing verified in game.

/// <summary>A winning, enabled, obstructing-typed reference with a world box. <paramref name="Win"/> is the winning
/// snap — its Plugin is the RESPONSIBLE plugin, the last one to change the placement.</summary>
public readonly record struct OverlapCandidate(FormKey Key, Snap Win, FormKey Base, Aabb World, char Src);

/// <summary>Two candidates whose world boxes overlap at the run's threshold. <paramref name="Declared"/> is the
/// closure relation between the two responsible plugins in either direction — the first cut's exclusion, kept
/// as a tally so the run can check that Seen reproduces it.</summary>
public readonly record struct OverlapHit(OverlapCandidate A, OverlapCandidate B, float Depth, float Frac, bool Declared);

/// <summary>One cross-responsible hit judged: which side is the later plugin, the arm without it, and the verdict.
/// <paramref name="LaterPrevious"/> is the later plugin's reference at its previous snap (null when it introduced
/// it); <paramref name="OtherAsSeenByLater"/> is the other reference as the later plugin's masters last placed it
/// (null when none of them ever did); <paramref name="WithoutDepth"/>/<paramref name="WithoutFrac"/> are the overlap
/// in the arm without the later plugin (NaN when no box could be rebuilt).</summary>
public sealed record OverlapRow(OverlapHit Hit, OverlapCandidate LaterRef, OverlapCandidate OtherRef,
                                DetectorModel.OverlapFacts Facts, OverlapOrigin Origin, OverlapSight Sight, string Why,
                                Snap? LaterPrevious, Snap? OtherAsSeenByLater, float WithoutDepth, float WithoutFrac,
                                string CounterfactualStatus = "measured", string? CounterfactualReason = null)
{
    public int Later => LaterRef.Win.Plugin;
    public int Earlier => OtherRef.Win.Plugin;
    /// <summary>The ranker's score: how deep times how much of the smaller box. Reported, not thresholded.</summary>
    public float Rank => Hit.Depth * Hit.Frac;
    /// <summary>Largest dimension of the smaller box — the thing that is buried.</summary>
    public float SmallerMaxDim
    {
        get
        {
            static float Vol(Aabb b) => (b.Max.X - b.Min.X) * (b.Max.Y - b.Min.Y) * (b.Max.Z - b.Min.Z);
            static float Dim(Aabb b) => MathF.Max(b.Max.X - b.Min.X, MathF.Max(b.Max.Y - b.Min.Y, b.Max.Z - b.Min.Z));
            return Vol(Hit.A.World) <= Vol(Hit.B.World) ? Dim(Hit.A.World) : Dim(Hit.B.World);
        }
    }
    public OverlapScale Scale => DetectorModel.ClassifyOverlapScale(SmallerMaxDim);
}

/// <summary>One actual placed render surface eligible for symmetric mesh intersection. The bounds enclose
/// <paramref name="WorldSoup"/> and are used only for conservative indexing/rejection.</summary>
public readonly record struct SurfaceOverlapCandidate(FormKey Key, Snap Win, FormKey Base, Aabb WorldBounds,
                                                       TriangleSoup WorldSoup, char Src, TriangleSoup? Model = null)
{
    /// <summary>The member as placed, for SurfaceQueryOutcome.QueryPlaced; null without its model.</summary>
    public SurfaceQueryOutcome.Placed? Placed => Model is null ? null : new(Model, Win.Pos, Win.Rot, Win.Scale);
}

/// <summary>The stable subset of the core mesh result consumed by probes and serialized evidence.</summary>
public sealed record SurfaceQueryOutcome(string Status, string? Reason, string SurfaceRelation, string Containment,
                                         bool? Intersects, long TrianglePairsTested)
{
    public bool Unknown => Intersects is null || Status == "unknown" || SurfaceRelation == "unknown";

    /// <summary>The origin both members were shifted by when world placement collapsed a triangle (PairFrame); null in the world frame.</summary>
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public double[]? Frame { get; init; }
    /// <summary>Zero-area-in-model triangles left out of the query, [A, B]; null when none were.</summary>
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public int[]? ZeroAreaSkipped { get; init; }

    /// <summary>One member as placed: its model soup and pose, from which <paramref name="world"/> soups are made.</summary>
    public readonly record struct Placed(TriangleSoup Model, Vec3 Pos, Vec3 Rot, float Scale)
    {
        /// <summary>The same member as core's PairFrame.Member.</summary>
        public PairFrame.Member Member => new(Model, Pos, Rot, Scale);
    }

    /// <summary>The query on two placed members, by --surface's rule (MeshSurface.MeasurePlaced): a pair with no zero-area triangle
    /// where placed is the world-soup query, unchanged. Otherwise a triangle with zero area in its own model has nothing to cross and
    /// is left out (counted in ZeroAreaSkipped), and a pair whose world placement collapses a triangle with model area is queried in
    /// the pair frame (PairFrame.Origin; Frame is set); the pair refuses, naming the collapse, only when no frame keeps every such
    /// triangle. Containment and the relation do not change under the shared translation. <paramref name="worldA"/> and
    /// <paramref name="worldB"/> are the members' models placed at their poses; a member given without its model keeps the world query.</summary>
    public static SurfaceQueryOutcome QueryPlaced(TriangleSoup worldA, Placed? a, TriangleSoup worldB, Placed? b, double tolerance,
                                                  long maxTrianglePairs, TimeSpan pairTimeout, CancellationToken cancellationToken = default,
                                                  PairTimer? timer = null, PreparedMeshCache? prepared = null,
                                                  PairResultCache? results = null)
        => QueryPlaced(worldA, a, worldB, b, tolerance, maxTrianglePairs, pairTimeout, cancellationToken, timer, prepared, results, out _);

    /// <summary>The same query, returning the pair's path to this call, whichever thread makes it.</summary>
    public static SurfaceQueryOutcome QueryPlaced(TriangleSoup worldA, Placed? a, TriangleSoup worldB, Placed? b, double tolerance,
                                                  long maxTrianglePairs, TimeSpan pairTimeout, CancellationToken cancellationToken,
                                                  PairTimer? timer, PreparedMeshCache? prepared, PairResultCache? results, out PairPath path)
    {
        long started = System.Diagnostics.Stopwatch.GetTimestamp();
        string? key = null;
        try
        {
            // P3: a result measured in an earlier run of the same build, for the same arguments (PairResultCache.Key).
            if (results is not null)
            {
                key = results.Key(worldA, a, worldB, b, tolerance, maxTrianglePairs);
                if (results.TryGet(key, out var hit, out var hitPath))
                {
                    if (timer is not null) { timer.LastPath = hitPath; timer.Paths[(int)hitPath]++; timer.CacheHits++; }
                    path = hitPath;
                    return hit;
                }
            }
            var outcome = QueryPlacedCore(worldA, a, worldB, b, tolerance, maxTrianglePairs, pairTimeout, cancellationToken, timer, prepared, out path);
            if (results is not null) results.Put(key!, outcome, path);
            return outcome;
        }
        finally
        {
            if (timer is not null) { timer.Pairs++; timer.WallTicks += System.Diagnostics.Stopwatch.GetTimestamp() - started; }
        }
    }

    static SurfaceQueryOutcome QueryPlacedCore(TriangleSoup worldA, Placed? a, TriangleSoup worldB, Placed? b, double tolerance,
                                               long maxTrianglePairs, TimeSpan pairTimeout, CancellationToken cancellationToken, PairTimer? timer,
                                               PreparedMeshCache? prepared, out PairPath path)
    {
        long t0 = timer is null ? 0 : System.Diagnostics.Stopwatch.GetTimestamp();
        var p = PairFrame.PreparePlaced(worldA, a?.Member, worldB, b?.Member, prepared is null ? null : prepared.AnyZeroArea);
        path = p is null ? PairPath.World : p.Refusal is not null ? PairPath.Refused : p.Frame is not null ? PairPath.Shifted : PairPath.WorldSkip;
        if (timer is not null)
        {
            timer.PairFrameTicks += System.Diagnostics.Stopwatch.GetTimestamp() - t0;
            timer.LastPath = path;
            timer.Paths[(int)path]++;
        }
        // P2: in the world frame the soups are the candidates' own, so each is prepared once (PreparedMeshCache); the pair
        // frame's re-placed and zero-area-skipped soups are made for this pair and are prepared here, as before.
        if (p is null) return prepared is null ? Query(worldA, worldB, tolerance, maxTrianglePairs, pairTimeout, cancellationToken, timer)
            : QueryViaCache(prepared, worldA, worldB, tolerance, maxTrianglePairs, pairTimeout, cancellationToken, timer);
        if (p.Refusal is { } why)
            return new SurfaceQueryOutcome("unknown", why, "unknown", "unknown", null, 0) { Frame = p.Frame, ZeroAreaSkipped = p.ZeroAreaSkipped };
        return Query(p.A, p.B, tolerance, maxTrianglePairs, pairTimeout, cancellationToken, timer) with { Frame = p.Frame, ZeroAreaSkipped = p.ZeroAreaSkipped };
    }

    public static SurfaceQueryOutcome Query(TriangleSoup a, TriangleSoup b, double tolerance, long maxTrianglePairs,
                                            TimeSpan pairTimeout, CancellationToken cancellationToken = default,
                                            PairTimer? timer = null)
    {
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(pairTimeout);
            var result = MeshIntersection.Query(a, b, tolerance, maxTrianglePairs, timeout.Token, timer?.Core);
            if (timer is not null && result.Reason == "cancelled") timer.Timeouts++;
            return new(Normalize(result.Status), result.Reason, Normalize(result.SurfaceRelation),
                       Normalize(result.Containment), result.Intersects, result.TrianglePairsTested);
        }
        catch (OperationCanceledException)
        {
            if (timer is not null) timer.Timeouts++;
            return new("unknown", "pair time budget exhausted", "unknown", "unknown", null, 0);
        }
        catch (Exception ex)
        {
            return new("unknown", $"mesh query failed: {ex.GetType().Name}: {ex.Message}", "unknown", "unknown", null, 0);
        }
    }

    /// <summary>The world-frame query through the prepared-mesh cache, in the soup query's order and with its outcomes: A is
    /// prepared first, and a refusal of A answers without touching B; an exception while preparing either side is this pair's
    /// "mesh query failed" outcome, exactly as when the soup query prepares, and the cache keeps nothing for that soup.</summary>
    static SurfaceQueryOutcome QueryViaCache(PreparedMeshCache cache, TriangleSoup worldA, TriangleSoup worldB, double tolerance,
                                             long maxTrianglePairs, TimeSpan pairTimeout, CancellationToken cancellationToken, PairTimer? timer)
    {
        MeshIntersection.PreparedMesh pa, pb;
        try
        {
            pa = cache.Get(worldA, timer?.Core);
            // A refused: the query answers A's refusal before reading B (B stands in for itself unread, as the soup query never prepares it).
            pb = pa.Refused ? pa : cache.Get(worldB, timer?.Core);
        }
        catch (Exception ex)
        {
            return new("unknown", $"mesh query failed: {ex.GetType().Name}: {ex.Message}", "unknown", "unknown", null, 0);
        }
        return Query(pa, pb, tolerance, maxTrianglePairs, pairTimeout, cancellationToken, timer);
    }

    /// <summary><see cref="Query(TriangleSoup, TriangleSoup, double, long, TimeSpan, CancellationToken, PairTimer?)"/> on two
    /// prepared meshes: the same result, the same time limit over the phases that read the clock.</summary>
    public static SurfaceQueryOutcome Query(MeshIntersection.PreparedMesh a, MeshIntersection.PreparedMesh b, double tolerance, long maxTrianglePairs,
                                            TimeSpan pairTimeout, CancellationToken cancellationToken = default, PairTimer? timer = null)
    {
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(pairTimeout);
            var result = MeshIntersection.Query(a, b, tolerance, maxTrianglePairs, timeout.Token, timer?.Core);
            if (timer is not null && result.Reason == "cancelled") timer.Timeouts++;
            return new(Normalize(result.Status), result.Reason, Normalize(result.SurfaceRelation),
                       Normalize(result.Containment), result.Intersects, result.TrianglePairsTested);
        }
        catch (OperationCanceledException)
        {
            if (timer is not null) timer.Timeouts++;
            return new("unknown", "pair time budget exhausted", "unknown", "unknown", null, 0);
        }
        catch (Exception ex)
        {
            return new("unknown", $"mesh query failed: {ex.GetType().Name}: {ex.Message}", "unknown", "unknown", null, 0);
        }
    }

    static string Normalize(object? value)
    {
        string text = Convert.ToString(value, System.Globalization.CultureInfo.InvariantCulture) ?? "unknown";
        if (text.Length == 0) return "unknown";
        var b = new System.Text.StringBuilder(text.Length + 4);
        for (int i = 0; i < text.Length; i++)
        {
            char c = text[i];
            if (char.IsUpper(c) && i > 0 && text[i - 1] != '_') b.Append('_');
            b.Append(char.ToLowerInvariant(c));
        }
        return b.ToString();
    }
}

/// <summary>Candidates' world soups prepared once each for the pair sweep (overlap acceleration P2), least recently used first
/// out, bounded by estimated bytes; and each soup's zero-area answer, which PairFrame asks of every pair. Keyed by the soup
/// object: a candidate's world soup is made once and kept for the run. Nothing a time limit cut short is kept: preparation
/// reads no clock, and a prepared mesh keeps only completed validations. Thread-safe; one thread at a time prepares a given soup,
/// and the others wait and share it, so no thread repeats its preparation or its validation.</summary>
public sealed class PreparedMeshCache(long maxBytes)
{
    readonly object _sync = new();
    readonly Dictionary<TriangleSoup, LinkedListNode<MeshIntersection.PreparedMesh>> _map = new(ReferenceEqualityComparer.Instance);
    readonly LinkedList<MeshIntersection.PreparedMesh> _lru = new();
    readonly System.Collections.Concurrent.ConcurrentDictionary<TriangleSoup, bool> _zero = new(ReferenceEqualityComparer.Instance);
    readonly HashSet<TriangleSoup> _evicted = new(ReferenceEqualityComparer.Instance);
    public long Bytes, PeakBytes, Hits, Misses, Evictions, Reprepared;
    public long MaxBytes => maxBytes;

    public MeshIntersection.PreparedMesh Get(TriangleSoup soup, MeshQueryTimer? timer)
    {
        if (TryHit(soup, out var held)) return held;
        // The soup's own lock, never held with _sync across the preparation: other soups' pairs carry on meanwhile.
        lock (soup)
        {
            if (TryHit(soup, out held)) return held;
            bool repeat;
            lock (_sync) { Misses++; repeat = _evicted.Contains(soup); if (repeat) Reprepared++; }
            var prepared = MeshIntersection.PreparedMesh.Create(soup, timer);
            if (repeat) prepared.MarkRepeat();
            lock (_sync)
            {
                _map[soup] = _lru.AddFirst(prepared);
                Bytes += prepared.EstimatedBytes;
                PeakBytes = Math.Max(PeakBytes, Bytes);
                while (Bytes > maxBytes && _lru.Count > 1)
                {
                    var last = _lru.Last!;
                    _lru.RemoveLast(); _map.Remove(last.Value.Source);
                    _evicted.Add(last.Value.Source);
                    Bytes -= last.Value.EstimatedBytes; Evictions++;
                }
            }
            return prepared;
        }
    }

    bool TryHit(TriangleSoup soup, out MeshIntersection.PreparedMesh held)
    {
        lock (_sync)
        {
            if (_map.TryGetValue(soup, out var node))
            {
                Hits++;
                _lru.Remove(node); _lru.AddFirst(node);
                held = node.Value;
                return true;
            }
        }
        held = null!;
        return false;
    }

    /// <summary>Whether a prepared mesh for this soup object is held.</summary>
    public bool Holds(TriangleSoup soup) { lock (_sync) return _map.ContainsKey(soup); }

    public bool AnyZeroArea(TriangleSoup soup) => _zero.TryGetValue(soup, out var z) ? z : _zero[soup] = PairFrame.AnyZeroArea(soup);
}

/// <summary>Placed-pair results across runs (overlap acceleration P3). A result is kept under a key over EVERY argument of
/// <see cref="SurfaceQueryOutcome.QueryPlaced"/> that can change it (<see cref="KeyedParameters"/>: both world soups' content,
/// both members' model content and exact pose, the tolerance, the work cap) and the code that computes it (the probe's and
/// housecarl-core's dll hashes and the mesh policy). The parameters left out are <see cref="HeaderParameters"/> (constant per
/// run, recorded in each file's header and required equal to read it) and <see cref="Unkeyed"/> (cannot change a stored
/// result: a result a time limit or cancellation cut short, or that threw, is never stored). Each run reads earlier runs'
/// files and writes its own (gzip JSON lines, written to a temporary name and renamed when complete). Key, TryGet, Contains and
/// Put are thread-safe; Read and Write run with no query in flight.</summary>
public sealed class PairResultCache
{
    /// <summary>QueryPlaced's parameters the key covers.</summary>
    public static readonly string[] KeyedParameters = { "worldA", "a", "worldB", "b", "tolerance", "maxTrianglePairs" };
    /// <summary>Constant per run: recorded in the file header; a file whose header differs is not read.</summary>
    public static readonly string[] HeaderParameters = { "pairTimeout" };
    /// <summary>Left out, each with why it cannot change a stored result.</summary>
    public static readonly IReadOnlyDictionary<string, string> Unkeyed = new Dictionary<string, string>
    {
        ["cancellationToken"] = "a cancelled result is never stored, and a completed one does not depend on the token",
        ["timer"] = "observation only (P0)",
        ["prepared"] = "the prepared-mesh cache gives the soup query's own result (P2, every pair identical on B1 and B2)",
        ["results"] = "this cache",
        ["path"] = "an output: the call's path, stored with the result",
    };
    public const string Format = "overlapprobe-pair-results-1";

    readonly object _sync = new();
    readonly string _probeSha, _coreSha, _header;
    readonly double _pairSeconds;
    readonly Dictionary<string, (SurfaceQueryOutcome Outcome, PairPath Path)> _read = new(StringComparer.Ordinal);
    readonly Dictionary<string, (SurfaceQueryOutcome Outcome, PairPath Path)> _mine = new(StringComparer.Ordinal);
    readonly System.Runtime.CompilerServices.ConditionalWeakTable<TriangleSoup, string> _soupHash = new();
    public long Hits, Misses, NotStored, ReadEntries;
    public readonly List<string> FilesRead = new(), FilesSkipped = new();

    public PairResultCache(string probeSha, string coreSha, double pairSeconds)
    {
        _probeSha = probeSha; _coreSha = coreSha; _pairSeconds = pairSeconds;
        _header = System.Text.Json.JsonSerializer.Serialize(new Header(Format, probeSha, coreSha, MeshIntersection.PolicyVersion, pairSeconds));
    }

    /// <summary>The cache for this process: the probe's entry assembly and housecarl-core, hashed.</summary>
    public static PairResultCache ForThisBuild(double pairSeconds)
    {
        static string Sha(string path) => Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(path))).ToLowerInvariant();
        string probe = System.Reflection.Assembly.GetEntryAssembly()?.Location is { Length: > 0 } e ? Sha(e) : "no-entry-assembly";
        return new PairResultCache(probe, Sha(typeof(MeshIntersection).Assembly.Location), pairSeconds);
    }

    sealed record Header(string Format, string Probe, string Core, string Policy, double PairSeconds);
    sealed record Value(string Status, string? Reason, string SurfaceRelation, string Containment, bool? Intersects,
                        long TrianglePairsTested, double[]? Frame, int[]? ZeroAreaSkipped, PairPath Path);
    sealed record Line(string K, Value V);

    /// <summary>The key: the code, then every keyed argument in the signature's order.</summary>
    public string Key(TriangleSoup worldA, SurfaceQueryOutcome.Placed? a, TriangleSoup worldB, SurfaceQueryOutcome.Placed? b,
                      double tolerance, long maxTrianglePairs)
    {
        var text = string.Join("|", _probeSha, _coreSha, MeshIntersection.PolicyVersion,
            "worldA=" + SoupHash(worldA), "a=" + Member(a), "worldB=" + SoupHash(worldB), "b=" + Member(b),
            "tolerance=" + BitConverter.DoubleToInt64Bits(tolerance).ToString("x16"), "maxTrianglePairs=" + maxTrianglePairs);
        return Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(text))).ToLowerInvariant();
    }

    string Member(SurfaceQueryOutcome.Placed? m) => m is not { } p ? "none"
        : $"{SoupHash(p.Model)}:{Bits(p.Pos)}:{Bits(p.Rot)}:{BitConverter.SingleToInt32Bits(p.Scale):x8}";

    static string Bits(Vec3 v) => $"{BitConverter.SingleToInt32Bits(v.X):x8},{BitConverter.SingleToInt32Bits(v.Y):x8},{BitConverter.SingleToInt32Bits(v.Z):x8}";

    /// <summary>A soup's content hash (every field of the record), once per soup object.</summary>
    string SoupHash(TriangleSoup soup)
    {
        if (soup is null) return "null";
        return _soupHash.GetValue(soup, s =>
        {
            using var h = System.Security.Cryptography.IncrementalHash.CreateHash(System.Security.Cryptography.HashAlgorithmName.SHA256);
            void Add(ReadOnlySpan<byte> bytes) { h.AppendData(BitConverter.GetBytes(bytes.Length)); h.AppendData(bytes); }
            Add(System.Runtime.InteropServices.MemoryMarshal.AsBytes(s.Vertices.AsSpan()));
            Add(System.Runtime.InteropServices.MemoryMarshal.AsBytes(s.Indices.AsSpan()));
            if (s.TriangleMaterials is null) h.AppendData(new byte[] { 0 });
            else
            {
                h.AppendData(new byte[] { 1 });
                foreach (var m in s.TriangleMaterials)
                    if (m is null) h.AppendData(BitConverter.GetBytes(-1)); else Add(System.Text.Encoding.UTF8.GetBytes(m));
            }
            if (s.TriangleRadii is null) h.AppendData(new byte[] { 0 });
            else { h.AppendData(new byte[] { 1 }); Add(System.Runtime.InteropServices.MemoryMarshal.AsBytes(s.TriangleRadii.AsSpan())); }
            return Convert.ToHexString(h.GetHashAndReset()).ToLowerInvariant();
        });
    }

    public bool TryGet(string key, out SurfaceQueryOutcome outcome, out PairPath path)
    {
        lock (_sync)
        {
            if (_mine.TryGetValue(key, out var v) || _read.TryGetValue(key, out v)) { Hits++; (outcome, path) = v; return true; }
            Misses++; outcome = null!; path = default; return false;
        }
    }

    /// <summary>Whether <see cref="TryGet"/> would serve this key, counting nothing.</summary>
    public bool Contains(string key) { lock (_sync) return _mine.ContainsKey(key) || _read.ContainsKey(key); }

    /// <summary>Keep a result unless a time limit or cancellation cut it short, or it threw.</summary>
    public void Put(string key, SurfaceQueryOutcome outcome, PairPath path)
    {
        lock (_sync)
        {
            if (outcome.Reason is "cancelled" or "pair time budget exhausted" || (outcome.Reason?.StartsWith("mesh query failed", StringComparison.Ordinal) ?? false))
            { NotStored++; return; }
            _mine[key] = (outcome, path);
        }
    }

    public int Stored { get { lock (_sync) return _mine.Count; } }

    /// <summary>Read an earlier run's file. Returns null when read, else why it was not (the header names another build or
    /// pair time limit, or the file is unreadable); a file is read whole or not at all.</summary>
    public string? Read(string path)
    {
        var entries = new List<(string, (SurfaceQueryOutcome, PairPath))>();
        try
        {
            using var fs = File.OpenRead(path);
            using var gz = new System.IO.Compression.GZipStream(fs, System.IO.Compression.CompressionMode.Decompress);
            using var r = new StreamReader(gz, System.Text.Encoding.UTF8);
            string? head = r.ReadLine();
            if (head != _header)
            {
                var h = head is null ? null : System.Text.Json.JsonSerializer.Deserialize<Header>(head);
                string why = h is null ? "no header" : h.Format != Format ? $"format {h.Format}" : h.Probe != _probeSha ? "another overlapprobe build"
                    : h.Core != _coreSha ? "another housecarl-core build" : h.Policy != MeshIntersection.PolicyVersion ? $"policy {h.Policy}"
                    : h.PairSeconds != _pairSeconds ? $"pair time limit {h.PairSeconds:R} s, this run {_pairSeconds:R} s" : "a different header";
                FilesSkipped.Add($"{Path.GetFileName(path)}: {why}");
                return why;
            }
            string? line;
            while ((line = r.ReadLine()) is not null)
            {
                var l = System.Text.Json.JsonSerializer.Deserialize<Line>(line) ?? throw new InvalidDataException("an empty line");
                var v = l.V;
                entries.Add((l.K, (new SurfaceQueryOutcome(v.Status, v.Reason, v.SurfaceRelation, v.Containment, v.Intersects, v.TrianglePairsTested)
                                   { Frame = v.Frame, ZeroAreaSkipped = v.ZeroAreaSkipped }, v.Path)));
            }
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or System.Text.Json.JsonException)
        {
            FilesSkipped.Add($"{Path.GetFileName(path)}: unreadable ({ex.GetType().Name}: {ex.Message})");
            return "unreadable";
        }
        foreach (var (k, v) in entries) _read[k] = v;
        ReadEntries += entries.Count;
        FilesRead.Add(Path.GetFileName(path));
        return null;
    }

    /// <summary>Write the results this run measured (not those it read) to <paramref name="path"/>, through a temporary file.</summary>
    public void Write(string path)
    {
        var dir = Path.GetDirectoryName(Path.GetFullPath(path));
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        var tmp = path + ".partial";
        using (var fs = File.Create(tmp))
        using (var gz = new System.IO.Compression.GZipStream(fs, System.IO.Compression.CompressionLevel.Fastest))
        using (var w = new StreamWriter(gz, new System.Text.UTF8Encoding(false)))
        {
            w.WriteLine(_header);
            foreach (var (k, (o, p)) in _mine)
                w.WriteLine(System.Text.Json.JsonSerializer.Serialize(new Line(k, new Value(o.Status, o.Reason, o.SurfaceRelation, o.Containment,
                    o.Intersects, o.TrianglePairsTested, o.Frame, o.ZeroAreaSkipped, p))));
        }
        File.Move(tmp, path, overwrite: true);
    }
}

/// <summary>Which preparation a placed pair took (PairFrame): the world soups unchanged, the world frame with zero-area-in-model
/// triangles left out, a shifted pair frame, or a refusal naming a collapse.</summary>
public enum PairPath { World, WorldSkip, Shifted, Refused }

/// <summary>Cumulative time and counts of placed-pair queries for one class of pairs (overlap acceleration P0). Observation only;
/// not thread-safe: one timer per thread, merged with <see cref="Add"/> when the threads have finished.</summary>
public sealed class PairTimer
{
    public readonly MeshQueryTimer Core = new();
    /// <summary>Stopwatch ticks in the PairFrame check, and whole-pair wall time as the caller measured it.</summary>
    public long PairFrameTicks, WallTicks;
    public long Pairs, Timeouts, CacheHits;
    public readonly long[] Paths = new long[4];
    public PairPath LastPath;
    /// <summary>The slowest pairs seen, longest first, at most <see cref="Keep"/>.</summary>
    public readonly List<(double Seconds, string A, string B, SurfaceQueryOutcome Query, PairPath Path)> Slowest = new();
    public const int Keep = 20;

    public void Note(double seconds, string a, string b, SurfaceQueryOutcome q) => Note(seconds, a, b, q, LastPath);

    public void Note(double seconds, string a, string b, SurfaceQueryOutcome q, PairPath path)
    {
        if (Slowest.Count == Keep && seconds <= Slowest[^1].Seconds) return;
        Slowest.Add((seconds, a, b, q, path));
        Slowest.Sort((x, y) => y.Seconds.CompareTo(x.Seconds));
        if (Slowest.Count > Keep) Slowest.RemoveAt(Keep);
    }

    /// <summary>Adds a finished thread's totals and slowest pairs to this timer.</summary>
    public void Add(PairTimer o)
    {
        Core.Add(o.Core);
        PairFrameTicks += o.PairFrameTicks; WallTicks += o.WallTicks;
        Pairs += o.Pairs; Timeouts += o.Timeouts; CacheHits += o.CacheHits;
        for (int i = 0; i < Paths.Length; i++) Paths[i] += o.Paths[i];
        foreach (var s in o.Slowest) Note(s.Seconds, s.A, s.B, s.Query, s.Path);
    }
}

public readonly record struct SurfaceOverlapHit(SurfaceOverlapCandidate A, SurfaceOverlapCandidate B,
                                                SurfaceQueryOutcome Query, bool Declared);

public sealed record SurfaceOverlapRow(SurfaceOverlapHit Hit, SurfaceOverlapCandidate LaterRef,
    SurfaceOverlapCandidate OtherRef, DetectorModel.OverlapFacts Facts, OverlapOrigin? Origin, OverlapSight Sight,
    string Why, Snap? LaterPrevious, Snap? OtherAsSeenByLater, SurfaceQueryOutcome? Counterfactual,
    string CounterfactualStatus, string? CounterfactualReason)
{
    public int Later => LaterRef.Win.Plugin;
    public int Earlier => OtherRef.Win.Plugin;
}

public sealed class SurfacePairSweep
{
    public long PairsChecked, SameChecked, SameIntersects, SameUnknown,
                CrossDeclaredChecked, CrossDeclaredIntersects, CrossUndeclaredChecked,
                CrossUndeclaredIntersects, CrossUnknown;
    /// <summary>Pairs a worker thread timed out on that were measured again on the calling thread.</summary>
    public long Remeasured;
    public readonly Dictionary<string, (long Intersects, long Unknown, long Checked)> SameByDomain = new();
    /// <summary>Same-responsible queries already measured by the sweep, retained only when requested by a caller.</summary>
    public readonly List<SurfaceOverlapHit> SameResults = new();
    /// <summary>Every cross-responsible query reached by the tolerance-aware broad phase, including separation and unknown.</summary>
    public readonly List<SurfaceOverlapHit> CrossResults = new();
    /// <summary>Every cross-responsible actual-shape intersection; unknown is never promoted into this list.</summary>
    public readonly List<SurfaceOverlapHit> CrossHits = new();
}

/// <summary>What the candidate build set aside, counted per rule 6.</summary>
public sealed class CandidateSkips
{
    public long Deleted, Disabled, NonObstructingType, NoGeometry, InvalidTransform, DegenerateGeometry,
                ExcludedResponsible, ScopeExcluded, Volume, MarkerMesh, EnableStateUnresolved, BosUnresolved;
}

/// <summary>The pair sweep's tallies: the same-author floor by domain, and the two cross populations.</summary>
public sealed class PairSweep
{
    public long PairsChecked, SameChecked, SameDeep, CrossDeclaredChecked, CrossDeclaredDeep, CrossUndeclaredChecked;
    public readonly Dictionary<string, (long Deep, long Checked)> SameByDomain = new();
    /// <summary>Every cross-responsible deep hit, declared or not — the population the provenance rule runs over.</summary>
    public readonly List<OverlapHit> CrossDeep = new();
    /// <summary>Every cross-responsible pair whose finite boxes intersect, including intersections below the detector
    /// thresholds. This keeps "measurable but below threshold" distinct from an unmeasurable pair in review exports.</summary>
    public readonly List<(OverlapCandidate A, OverlapCandidate B, float Depth, float Frac, bool Deep, bool Declared)> CrossIntersections = new();
}

public sealed class OverlapIndex
{
    public PlacementHistory History { get; } = new();
    /// <summary>base record -> its Mutagen type name and editor id, winner wins. Modeled records only.</summary>
    public Dictionary<FormKey, (string Type, string? Edid)> BaseInfo { get; } = new();
    /// <summary>Light bases carrying the Can Be Carried flag (torches, lanterns): loose items under overlapprobe's rest rule.</summary>
    public HashSet<FormKey> LightCarryable { get; } = new();
    public Dictionary<FormKey, string> BaseMesh { get; } = new();
    /// <summary>An armour's FEMALE ground model where the record gives one (BaseMesh holds the male) — G1 build list row 2.</summary>
    public Dictionary<FormKey, string> BaseMeshFemale { get; } = new();
    /// <summary>base record -> its model's alternate textures (MODL alternate texture entries: 3D shape name, 3D index, texture set),
    /// winner wins; absent when the winning record has none. Read by the visible-crossing measure (texture alpha).</summary>
    public Dictionary<FormKey, List<(string Name, int Index, FormKey TextureSet)>> BaseAlternateTextures { get; } = new();
    /// <summary>texture set (TXST) -> its diffuse path as given, winner wins (null when the winning record names none).</summary>
    public Dictionary<FormKey, string?> TextureSetDiffuse { get; } = new();
    /// <summary>base record -> OBND extents (|second − first| per axis).</summary>
    public Dictionary<FormKey, Vec3> BaseObnd { get; } = new();
    /// <summary>The latest record/runtime pose for each reference, including rotation-only and scale-only overrides.
    /// This is deliberately separate from <see cref="History"/>, whose last snap identifies the last plugin that made
    /// an attributable placement step. Geometry uses this pose while provenance keeps the changed-only attribution.</summary>
    readonly Dictionary<FormKey, Snap> _effectivePose = new();
    /// <summary>ref -> its winning base record.</summary>
    public Dictionary<FormKey, FormKey> RefBase { get; } = new();
    public Dictionary<FormKey, (string? Edid, bool Interior)> CellName { get; } = new();
    public long Placements { get; private set; }
    /// <summary>Overrides that changed ONLY rotation or scale — same position, cell and flags as the latest snap. The
    /// history does not record them (driftprobe's rule), so they are not a step and do not make the plugin responsible.
    /// Counted so the limit is a number in the output, not a sentence.</summary>
    public long SilentRePlacements { get; private set; }
    /// <summary>ref -> every plugin carrying a record of it, in load order, whether or not the placement changed. What
    /// "the mover's side overrode it without moving it" is read from (the contact pass; DependencyIndex keeps the same).</summary>
    public Dictionary<FormKey, List<int>> Touchers { get; } = new();
    /// <summary>ref -> the plugins that CHANGED its base record, with the base each set, in load order — the first entry
    /// is the defining plugin's. A previous snap's box is rebuilt with the base the reference had THEN, so an override
    /// that swapped the mesh as it moved is not rebuilt with the wrong mesh (4.1's stated limit, closed for the
    /// contact pass). Not part of the history's changed-only rule: a base swap without a move is still not a step.</summary>
    public Dictionary<FormKey, List<(int Plugin, FormKey Base)>> BaseChanges { get; } = new();
    /// <summary>Deletion, per view (Q34, _shared/DeletionHistory.cs). A deleted reference usually loses its placement
    /// subrecord and is invisible to PlacementExtract, so deletions are read off the major-record walk — else a deleted
    /// dependent would read as still where it was. The view selects the record and only then is its Deleted flag read:
    /// the last deletion and the last placement STEP are different plugins whenever a deletion stripped the placement or
    /// a later record restored the reference where it already stood.</summary>
    public DeletionHistory Deletions { get; }

    public OverlapIndex() => Deletions = new DeletionHistory(Touchers);
    /// <summary>Bases carrying the Is-Marker header flag (0x00800000) under the winner — the engine renders no mesh for
    /// them, whatever the record type or the mesh name says (JK's sit-marker chairs: Furniture, CommonChair01.nif, the
    /// flag set). Winner-takes-all like the mesh path; an override that clears the flag removes the entry. The marker
    /// rule for the contact pass since the 2026-09-03 fold (R2).</summary>
    public HashSet<FormKey> BaseIsMarker { get; } = new();
    /// <summary>ref -> the plugins that CHANGED its XESP enable parent (or the opposite-of-parent flag), with what each
    /// set, in load order; a ref born without a parent and never given one has no entry, and an override that drops
    /// the parent records a null. The runtime enable state of an arm is resolved from the last entry not on the
    /// mover's side (R1, 2026-09-03), the way <see cref="BaseChanges"/> gives a previous snap the base it had then.
    /// Not part of the history's changed-only rule: an XESP change without a move is not a placement step.</summary>
    public Dictionary<FormKey, List<(int Plugin, FormKey? Parent, bool Opposite)>> EnableChanges { get; } = new();
    /// <summary>ref -> the cell and worldspace of its winning placement (Null worldspace for an interior). What the
    /// Base Object Swapper reader's filters read (BosHook).</summary>
    public Dictionary<FormKey, (FormKey Cell, FormKey Worldspace)> RefCell { get; } = new();
    /// <summary>ref -> its placed arm ("PlacedObject", "PlacedNpc", …), winner wins. BOS swaps three arms and no actor.</summary>
    public Dictionary<FormKey, string> RefArm { get; } = new();
    /// <summary>ref -> its own XEZN under the winner; absent when none.</summary>
    public Dictionary<FormKey, FormKey> RefEncounterZone { get; } = new();
    /// <summary>The Base Object Swapper form index, filled during <see cref="Ingest"/> when a caller sets it before
    /// the walk (one pass over the order serves both). Null: no BOS.</summary>
    public BosFormIndex? Bos { get; set; }
    /// <summary>ref -> its RECORD base, kept when BosHook replaced <see cref="RefBase"/> with the effective one. Empty
    /// when BOS was not applied.</summary>
    public Dictionary<FormKey, FormKey> RecordBase { get; } = new();
    /// <summary>ref -> what BOS does to it, for every reference it swaps, moves or may swap (BosHook).</summary>
    public Dictionary<FormKey, BaseObjectSwaps.BosResolution> BosSwaps { get; } = new();
    /// <summary>The pseudo-plugin index BosHook recorded its steps under; -1 when BOS was not applied.</summary>
    public int BosPlugin { get; set; } = -1;
    /// <summary>Collision layers (COLL) by their INDEX — the number a placed reference's XTRI carries — under the winner:
    /// the record, its editor id, and whether it is flagged NavmeshObstacle. L_NAVCUT is index 49 in the base game
    /// (measured 2026-09-04). What the world-state census reads a navcut box by (WorldState.cs).</summary>
    public Dictionary<uint, (FormKey Key, string? Edid, bool NavmeshObstacle)> CollisionLayersByIndex { get; } = new();
    /// <summary>COLL records as the reach rules' drop reads them, by INDEX under the winner: the TriggerVolume and Sensor
    /// flags and the records listed in CollidesWith (resolved to indices by <see cref="CollisionLayerRules"/>).</summary>
    public Dictionary<uint, (FormKey Key, string? Edid, bool TriggerVolume, bool Sensor, List<FormKey> CollidesWith)> CollisionLayerRecords { get; } = new();
    /// <summary>Set before ingesting to keep each exterior grid's terrain under the winner (the reach rules' drop): the
    /// decoded heights, or why a LAND that says it has heights gives none. A LAND that declares no heights removes the grid.
    /// Off by default: nothing else reads it.</summary>
    public bool TrackLand { get; set; }
    public Dictionary<(FormKey Ws, int X, int Y), float[,]> Land { get; } = new();
    public Dictionary<(FormKey Ws, int X, int Y), string> LandUnreadable { get; } = new();

    /// <summary>BOOK -> the Static its InventoryArt names (a placed Static of that base is book art: the reach rules read it
    /// closed). Under the winner.</summary>
    public Dictionary<FormKey, FormKey> BookInventoryArt { get; } = new();

    /// <summary>The COLL records as drop-rule layers: CollidesWith resolved from record keys to layer indices.</summary>
    public Dictionary<uint, CollisionLayerRule> CollisionLayerRules()
    {
        var byKey = CollisionLayerRecords.ToDictionary(kv => kv.Value.Key, kv => kv.Key);
        return CollisionLayerRecords.ToDictionary(kv => kv.Key, kv => new CollisionLayerRule(kv.Key, kv.Value.Edid, kv.Value.TriggerVolume, kv.Value.Sensor,
            kv.Value.CollidesWith.Where(byKey.ContainsKey).Select(k => byKey[k]).ToHashSet()));
    }
    /// <summary>ref -> its XTRI collision-layer index under the winner; absent when none.</summary>
    public Dictionary<FormKey, uint> RefCollisionLayer { get; } = new();
    /// <summary>ref -> its winning record's flags (the load-settle rule reads them).</summary>
    public Dictionary<FormKey, uint> RefRecordFlags { get; } = new();
    /// <summary>WEAP base -> its animation type by the record enum name (the load-settle rule reads it).</summary>
    /// <summary>The worldspace persistent cells (the top cell of each worldspace, no grid) — a marker placed persistent
    /// sits here whatever its coordinates, so a per-cell census labels it rather than reading it as a crowded cell.</summary>
    public HashSet<FormKey> PersistentCells { get; } = new();
    /// <summary>ref -> its XPRM primitive under the winner; absent when none. A navcut's obstacle is this box.</summary>
    public Dictionary<FormKey, PlacedPrimitiveInfo> RefPrimitive { get; } = new();
    /// <summary>Refs whose winning record carries ONAM (IsOpenByDefault), the motion reader's rest pose (follow-ups E3).</summary>
    public HashSet<FormKey> RefOpenByDefault { get; } = new();
    /// <summary>ref -> its winning record's XLKR links and XAPR parents (absent when none): the two-mover coupling (E4).</summary>
    public Dictionary<FormKey, IReadOnlyList<PlacedLink>> RefLinkedRefs { get; } = new();
    public Dictionary<FormKey, IReadOnlyList<PlacedActivateParent>> RefActivateParents { get; } = new();
    /// <summary>Rest rule part 2: ref -> the script names its own VMAD attaches (absent when none).</summary>
    public Dictionary<FormKey, IReadOnlyList<string>> RefScripts { get; } = new();
    /// <summary>Rest rule part 2: weapon and armour base -> its keywords (winning record).</summary>
    public Dictionary<FormKey, IReadOnlyList<FormKey>> BaseKeywords { get; } = new();
    /// <summary>Rest rule part 2: activator base carrying the weapon rack script -> the script's properties (winning record).</summary>
    public Dictionary<FormKey, RackScriptInfo> RackActivators { get; } = new();
    /// <summary>ref -> its editor id, the last non-empty one in load order (an override that omits it keeps the earlier
    /// name). What the world-state census names a switch by.</summary>
    public Dictionary<FormKey, string> RefEditorId { get; } = new();

    /// <summary>Is the WINNING record of the reference a deletion? The last record in the evaluated order decides —
    /// not the last plugin that ever deleted it, and not the last plugin to change its placement (Q34).</summary>
    public bool IsDeletedNow(FormKey key) => Deletions.AtWinner(key);

    /// <summary>The effective winning geometry pose with the responsible placement plugin retained in
    /// <see cref="Snap.Plugin"/>. Returns null when the reference has no placement history. A rotation-only or scale-only
    /// override therefore changes geometry without taking placement responsibility.</summary>
    public Snap? WinningGeometryPose(FormKey key)
    {
        if (History.Winning(key) is not { } responsible || !_effectivePose.TryGetValue(key, out var effective)) return null;
        return effective with { Plugin = responsible.Plugin };
    }

    /// <summary>Update the runtime pose after a non-record layer such as BOS applies properties.</summary>
    public void NoteEffectivePose(FormKey key, Snap pose) => _effectivePose[key] = pose;

    /// <summary>…and the same question in the arm WITHOUT <paramref name="mover"/>'s side: the last record by a plugin
    /// that is neither the mover nor downstream of it. What the counterfactual arms ask, so an arm that excludes a
    /// RESTORING plugin sees the deletion that stands without it.</summary>
    public bool IsDeletedWithout(FormKey key, int mover, IReadOnlyList<HashSet<int>> closure)
        => Deletions.Without(key, mover, closure);

    void NoteAlternates(FormKey key, IReadOnlyList<IAlternateTextureGetter>? alternates)
    {
        if (alternates is not { Count: > 0 }) { BaseAlternateTextures.Remove(key); return; }
        BaseAlternateTextures[key] = alternates.Select(a => (a.Name ?? "", a.Index, a.NewTexture.FormKey)).ToList();
    }

    /// <summary>Read one plugin, in load order: placements into the history and winning base-record names/meshes.</summary>
    public void Ingest(int plugin, ISkyrimModGetter mod)
    {
        foreach (var rec in mod.EnumerateMajorRecords())
        {
            Bos?.Note(rec);
            if (rec is ITextureSetGetter txst) { TextureSetDiffuse[txst.FormKey] = txst.Diffuse?.GivenPath is { Length: > 0 } dp ? dp : null; continue; }
            // Every placed record states whether it is deleted, whether or not it kept its placement — that is what
            // makes a later restoration expressible at all (Q34).
            if (rec is IPlacedGetter pg) { Deletions.Note(plugin, pg.FormKey, pg.IsDeleted, pg.Placement is not null); continue; }
            if (rec is ICollisionLayerGetter coll)
            {
                CollisionLayersByIndex[coll.Index] = (coll.FormKey, coll.EditorID, coll.Flags.HasFlag(CollisionLayer.Flag.NavmeshObstacle));
                CollisionLayerRecords[coll.Index] = (coll.FormKey, coll.EditorID, coll.Flags.HasFlag(CollisionLayer.Flag.TriggerVolume),
                                                     coll.Flags.HasFlag(CollisionLayer.Flag.Sensor), (coll.CollidesWith ?? []).Select(l => l.FormKey).ToList());
                continue;
            }
            // rest rule part 2: item keywords (weapons, armour) and the weapon rack script's properties on activator bases
            if ((rec is IWeaponGetter || rec is IArmorGetter) && rec is IKeywordedGetter<IKeywordGetter> kwr && rec is ISkyrimMajorRecordGetter kmr)
                BaseKeywords[kmr.FormKey] = kwr.Keywords is { } kws ? kws.Select(k => k.FormKey).Where(k => !k.IsNull).ToArray() : Array.Empty<FormKey>();
            if (rec is IActivatorGetter actr)
            {
                var sc = actr.VirtualMachineAdapter?.Scripts.FirstOrDefault(x => string.Equals(x.Name, RackMount.ActivatorScript, StringComparison.OrdinalIgnoreCase));
                if (sc is null) RackActivators.Remove(actr.FormKey);
                else
                {
                    int? rt = null; bool? coa = null; var objs = new Dictionary<string, FormKey>(StringComparer.OrdinalIgnoreCase);
                    foreach (var pr in sc.Properties)
                    {
                        if (pr is IScriptIntPropertyGetter ip && string.Equals(pr.Name, "RackType", StringComparison.OrdinalIgnoreCase)) rt = ip.Data;
                        else if (pr is IScriptBoolPropertyGetter bp && string.Equals(pr.Name, "Patch14COARacks", StringComparison.OrdinalIgnoreCase)) coa = bp.Data;
                        else if (pr is IScriptObjectPropertyGetter op && !op.Object.FormKey.IsNull) objs[pr.Name] = op.Object.FormKey;
                    }
                    RackActivators[actr.FormKey] = new RackScriptInfo(sc.Name, rt, coa, objs);
                }
            }
            // Armour carries its ground model per gender in WorldModel, not Model (G1 build list row 2, Gate A review Q6): the
            // male path goes to BaseMesh (the female when only it is given), the female to BaseMeshFemale.
            if (rec is IArmorGetter ar)
            {
                BaseInfo[ar.FormKey] = (rec.GetType().Name.Replace("BinaryOverlay", ""), ar.EditorID);
                string? male = ar.WorldModel?.Male?.Model?.File is { } mf0 && !string.IsNullOrWhiteSpace(mf0.GivenPath) ? mf0.GivenPath : null;
                string? female = ar.WorldModel?.Female?.Model?.File is { } ff0 && !string.IsNullOrWhiteSpace(ff0.GivenPath) ? ff0.GivenPath : null;
                if ((male ?? female) is { } any) BaseMesh[ar.FormKey] = any; else BaseMesh.Remove(ar.FormKey);
                NoteAlternates(ar.FormKey, male is not null ? ar.WorldModel?.Male?.Model?.AlternateTextures : ar.WorldModel?.Female?.Model?.AlternateTextures);
                if ((female ?? male) is { } anyF) BaseMeshFemale[ar.FormKey] = anyF; else BaseMeshFemale.Remove(ar.FormKey);
                if ((ar.MajorRecordFlagsRaw & EnableState.IsMarkerFlag) != 0) BaseIsMarker.Add(ar.FormKey); else BaseIsMarker.Remove(ar.FormKey);
                if (ar.ObjectBounds is { } ab)
                    BaseObnd[ar.FormKey] = new Vec3(MathF.Abs(ab.Second.X - ab.First.X), MathF.Abs(ab.Second.Y - ab.First.Y), MathF.Abs(ab.Second.Z - ab.First.Z));
                continue;
            }
            if (rec is IBookGetter bk)
            {
                if (!bk.InventoryArt.IsNull) BookInventoryArt[bk.FormKey] = bk.InventoryArt.FormKey; else BookInventoryArt.Remove(bk.FormKey);
            }
            if (rec is ILightGetter lg)
            {
                if (lg.Flags.HasFlag(Light.Flag.CanBeCarried)) LightCarryable.Add(lg.FormKey); else LightCarryable.Remove(lg.FormKey);
            }
            if (rec is IWeaponGetter wp)
            {
            }
            if (rec is IModeledGetter mo && rec is ISkyrimMajorRecordGetter sm)
            {
                BaseInfo[sm.FormKey] = (rec.GetType().Name.Replace("BinaryOverlay", ""), sm.EditorID);
                if (mo.Model?.File is { } mf && !string.IsNullOrWhiteSpace(mf.GivenPath)) BaseMesh[sm.FormKey] = mf.GivenPath;
                else BaseMesh.Remove(sm.FormKey);
                NoteAlternates(sm.FormKey, mo.Model?.AlternateTextures);
                if ((sm.MajorRecordFlagsRaw & EnableState.IsMarkerFlag) != 0) BaseIsMarker.Add(sm.FormKey); else BaseIsMarker.Remove(sm.FormKey);
                if (rec is IObjectBoundedGetter ob && ob.ObjectBounds is { } bnd)
                    BaseObnd[sm.FormKey] = new Vec3(MathF.Abs(bnd.Second.X - bnd.First.X),
                                                    MathF.Abs(bnd.Second.Y - bnd.First.Y),
                                                    MathF.Abs(bnd.Second.Z - bnd.First.Z));
            }
        }
        if (TrackLand)
            foreach (var lc in PlacementExtract.FromMod(mod, includeEmpty: true))
            {
                if (lc.Interior || lc.Worldspace.IsNull || lc.Grid is not { } g) continue;
                var gk = (lc.Worldspace, g.X, g.Y);
                if (lc.LandUnreadableReason is { } why) { Land.Remove(gk); LandUnreadable[gk] = why; }
                else if (lc.LandDeclaresNoHeights) { Land.Remove(gk); LandUnreadable.Remove(gk); }
                else if (lc.Land is { } h) { Land[gk] = h; LandUnreadable.Remove(gk); }
            }
        foreach (var cp in PlacementExtract.FromMod(mod))
        {
            CellName[cp.Cell] = (cp.CellEditorId, cp.Interior);
            if (!cp.Interior && cp.Grid is null && !cp.Worldspace.IsNull) PersistentCells.Add(cp.Cell);
            Bos?.NoteCell(cp.Cell, cp.Worldspace);
            foreach (var r in cp.Refs) Record(plugin, cp.Cell, cp.Worldspace, r);
        }
    }

    /// <summary>One placed record from one plugin. Exposed so a proof can feed extracted refs directly.</summary>
    public void Record(int plugin, FormKey cell, FormKey worldspace, PlacedRef r)
    {
        Placements++;
        RefCell[r.FormKey] = (cell, worldspace);
        RefArm[r.FormKey] = r.RecordType;
        Bos?.NotePlaced(r.FormKey, r.Persistent);
        if (r.EncounterZone is { } ez) RefEncounterZone[r.FormKey] = ez; else RefEncounterZone.Remove(r.FormKey);
        if (r.CollisionLayerIndex is { } cl) RefCollisionLayer[r.FormKey] = cl; else RefCollisionLayer.Remove(r.FormKey);
        if (!string.IsNullOrWhiteSpace(r.EditorId)) RefEditorId[r.FormKey] = r.EditorId!;
        if (r.Primitive is { } pr) RefPrimitive[r.FormKey] = pr; else RefPrimitive.Remove(r.FormKey);
        if (r.OpenByDefault) RefOpenByDefault.Add(r.FormKey); else RefOpenByDefault.Remove(r.FormKey);
        RefRecordFlags[r.FormKey] = r.RecordFlags;
        if (r.LinkedReferences is { Count: > 0 } links) RefLinkedRefs[r.FormKey] = links; else RefLinkedRefs.Remove(r.FormKey);
        if (r.ActivateParents is { Count: > 0 } parents) RefActivateParents[r.FormKey] = parents; else RefActivateParents.Remove(r.FormKey);
        if (r.Scripts is { Count: > 0 } scripts) RefScripts[r.FormKey] = scripts; else RefScripts.Remove(r.FormKey);
        Deletions.Note(plugin, r.FormKey, r.Deleted, hasPlacement: true);
        var snap = new Snap(plugin, r.Position, cell, r.InitiallyDisabled, r.Deleted, worldspace, r.Rotation, r.Scale);
        if (History.Winning(r.FormKey) is { } prev && prev.SameAs(snap)
            && (MathF.Abs(prev.Rot.X - snap.Rot.X) > 1e-4f || MathF.Abs(prev.Rot.Y - snap.Rot.Y) > 1e-4f
             || MathF.Abs(prev.Rot.Z - snap.Rot.Z) > 1e-4f || MathF.Abs(prev.Scale - snap.Scale) > 1e-3f))
            SilentRePlacements++;
        History.Record(r.FormKey, snap);
        _effectivePose[r.FormKey] = snap;
        if (!Touchers.TryGetValue(r.FormKey, out var touchers)) Touchers[r.FormKey] = touchers = new List<int>(1);
        if (touchers.Count == 0 || touchers[^1] != plugin) touchers.Add(plugin);
        {
            // The XESP as this plugin states it; an entry only when it differs from the last one recorded (none = no parent).
            var had = EnableChanges.TryGetValue(r.FormKey, out var ec) && ec.Count > 0 ? ec[^1] : (Plugin: -1, Parent: (FormKey?)null, Opposite: false);
            bool same = had.Parent == r.EnableParent && had.Opposite == r.EnableParentOpposite;
            if (!same)
            {
                if (ec is null) EnableChanges[r.FormKey] = ec = new List<(int, FormKey?, bool)>(1);
                ec.Add((plugin, r.EnableParent, r.EnableParentOpposite));
            }
        }
        if (r.Base is { } b)
        {
            if (!RefBase.TryGetValue(r.FormKey, out var prevBase) || prevBase != b)
            {
                if (!BaseChanges.TryGetValue(r.FormKey, out var changes)) BaseChanges[r.FormKey] = changes = new List<(int, FormKey)>(1);
                changes.Add((plugin, b));
            }
            RefBase[r.FormKey] = b;
        }
    }

    /// <summary>A synthetic/test helper retained for historical proof fixtures. Production overlap discovery and
    /// remeasurement never call it; record ObjectBounds are not a geometry source or fallback.</summary>
    public Aabb ObndBox(FormKey baseKey)
    {
        if (!BaseObnd.TryGetValue(baseKey, out var ob) || (ob.X <= 0 && ob.Y <= 0 && ob.Z <= 0)) return Aabb.Empty;
        var half = new Vec3(ob.X / 2, ob.Y / 2, ob.Z / 2);
        return new Aabb(new Vec3(-half.X, -half.Y, -half.Z), half, false);
    }

    /// <summary>Build the candidates: every winning, enabled, undeleted reference whose base is an obstructing type
    /// and has geometry, whose responsible plugin <paramref name="excludeResponsible"/> does not set aside, whose base
    /// is not an editor marker (the engine renders no marker mesh, so a marker cannot obstruct anything), and whose
    /// world box is no larger than <paramref name="maxDim"/> on any axis (volumes — fog, LOD, skydomes — overlap
    /// everything by design).</summary>
    /// <param name="enabledOf">the run's population — <c>EnablePopulation.Predicate</c>: the load-time enable chain
    /// by default since Q27 (2026-09-06), a world-state arm's under <c>--state</c> (EnableState under StateArm.Wrap,
    /// roadmap 4.11). NULL reads the winning snap's own flag, the population every run before Q27 judged, byte for
    /// byte — what <c>--flags</c> asks for.</param>
    public List<OverlapCandidate> Candidates(Func<FormKey, (Aabb Box, char Src)> boxOf, ISet<string> obstructing,
                                             Func<int, bool> excludeResponsible, float maxDim, out CandidateSkips skips,
                                             Func<FormKey, bool>? enabledOf = null)
    {
        skips = new CandidateSkips();
        var list = new List<OverlapCandidate>();
        foreach (var (key, snaps) in History.Refs)
        {
            var s = WinningGeometryPose(key) ?? snaps[^1];
            // The winning RECORD decides, not the last placement step's own flag: a deletion that stripped the
            // placement leaves no step at all, so a reference removed that way used to stay a candidate (Q34).
            if (s.Deleted || IsDeletedNow(key)) { skips.Deleted++; continue; }
            if (enabledOf is null ? s.Disabled : !enabledOf(key)) { skips.Disabled++; continue; }
            if (excludeResponsible(s.Plugin)) { skips.ExcludedResponsible++; continue; }
            if (!RefBase.TryGetValue(key, out var bk) || !BaseInfo.TryGetValue(bk, out var bi) || !obstructing.Contains(bi.Type))
            { skips.NonObstructingType++; continue; }
            if (BaseIsMarker.Contains(bk) || DetectorModel.ClassifyHost(bi.Type, bi.Edid) == HostClass.Marker
                || (BaseMesh.TryGetValue(bk, out var mesh) && Path.GetFileName(mesh).StartsWith("MarkerX", StringComparison.OrdinalIgnoreCase)))
            { skips.MarkerMesh++; continue; }
            var (box, src) = boxOf(bk);
            if (src == '-' || box.IsEmpty) { skips.NoGeometry++; continue; }
            var placed = SpatialOverlap.PlaceBounds(box, s.Pos, s.Rot, s.Scale);
            if (placed.Status == "invalid_transform") { skips.InvalidTransform++; continue; }
            if (placed.Status == "degenerate_geometry") { skips.DegenerateGeometry++; continue; }
            if (!placed.UsableForOverlap || placed.Bounds is not { } world) { skips.NoGeometry++; continue; }
            if (MathF.Max(world.Max.X - world.Min.X, MathF.Max(world.Max.Y - world.Min.Y, world.Max.Z - world.Min.Z)) > maxDim)
            { skips.Volume++; continue; }
            list.Add(new OverlapCandidate(key, s, bk, world, src));
        }
        return list;
    }

    /// <summary>Build actual-surface candidates under the same declared source/state/type/marker rules as the historical
    /// overlap pass. No box size, depth or volume gate applies. Flat triangle soups remain eligible.</summary>
    public List<SurfaceOverlapCandidate> SurfaceCandidates(
        Func<FormKey, (TriangleSoup? Soup, char Src, string? Reason)> geometryOf,
        ISet<string> obstructing, Func<int, bool> excludeResponsible, out CandidateSkips skips,
        Func<FormKey, bool>? enabledOf = null, Func<FormKey, Snap, bool>? includeReference = null)
    {
        skips = new CandidateSkips();
        var list = new List<SurfaceOverlapCandidate>();
        foreach (var (key, snaps) in History.Refs)
        {
            var s = WinningGeometryPose(key) ?? snaps[^1];
            if (includeReference is not null && !includeReference(key, s)) { skips.ScopeExcluded++; continue; }
            if (s.Deleted || IsDeletedNow(key)) { skips.Deleted++; continue; }
            if (enabledOf is null ? s.Disabled : !enabledOf(key)) { skips.Disabled++; continue; }
            if (excludeResponsible(s.Plugin)) { skips.ExcludedResponsible++; continue; }
            if (!RefBase.TryGetValue(key, out var bk) || !BaseInfo.TryGetValue(bk, out var bi) || !obstructing.Contains(bi.Type))
            { skips.NonObstructingType++; continue; }
            if (BaseIsMarker.Contains(bk) || DetectorModel.ClassifyHost(bi.Type, bi.Edid) == HostClass.Marker
                || (BaseMesh.TryGetValue(bk, out var mesh) && Path.GetFileName(mesh).StartsWith("MarkerX", StringComparison.OrdinalIgnoreCase)))
            { skips.MarkerMesh++; continue; }
            var (model, src, _) = geometryOf(bk);
            if (src == '-' || model is not { TriangleCount: > 0 }) { skips.NoGeometry++; continue; }
            if (s.Scale == 0) { skips.DegenerateGeometry++; continue; }
            var world = SpatialOverlap.TransformSoup(model, s.Pos, s.Rot, s.Scale);
            var bounds = SpatialOverlap.SoupBounds(world);
            if (bounds.IsEmpty || !SpatialOverlap.IsFinite(bounds)) { skips.InvalidTransform++; continue; }
            list.Add(new(key, s, bk, bounds, world, src, model));
        }
        return list;
    }

    /// <summary>Symmetric actual-surface discovery. Bounds only decide a provably separated broad-phase rejection;
    /// every surviving pair reaches the generic mesh predicate with a per-pair work and time budget. On many threads the rows are
    /// one thread's, with one known limit: a pair that would time out alone may complete when another pair filled its validation memo
    /// first, because a memo hit reads no clock. A worker's time-out is measured again in order (<see cref="SurfacePairSweep.Remeasured"/>).</summary>
    public static SurfacePairSweep SurfacePairs(IReadOnlyList<SurfaceOverlapCandidate> candidates,
        IReadOnlyList<HashSet<int>> closure, double tolerance = 1e-5, long maxTrianglePairs = 2_000_000,
        TimeSpan? pairTimeout = null, Func<int, int, bool>? includeResponsiblePair = null,
        Action<long>? onProgress = null, float tile = 256f, CancellationToken cancellationToken = default,
        bool retainSameResults = false, Func<SurfaceOverlapCandidate, SurfaceOverlapCandidate, bool>? includePair = null,
        PairTimer? sameTimer = null, PairTimer? crossTimer = null,
        Action<SurfaceOverlapCandidate, SurfaceOverlapCandidate, SurfaceQueryOutcome, PairPath?, double>? onPair = null,
        PreparedMeshCache? prepared = null, PairResultCache? results = null, int threads = 1)
    {
        var timeout = pairTimeout ?? TimeSpan.FromSeconds(120);
        var sweep = new SurfacePairSweep();

        // A sweep per coordinate space has no allocation proportional to an object's dimensions. This matters now that
        // large real surfaces are eligible. Bounds reject only axis separation greater than the predicate tolerance.
        IEnumerable<(SurfaceOverlapCandidate A, SurfaceOverlapCandidate B)> Reached()
        {
            foreach (var space in candidates.GroupBy(c => c.Win.Space))
            {
                var ordered = space.OrderBy(c => (double)c.WorldBounds.Min.X).ThenBy(c => c.Key.ToString(), StringComparer.Ordinal).ToList();
                for (int ai = 0; ai < ordered.Count; ai++)
                    for (int bi = ai + 1; bi < ordered.Count; bi++)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        var a = ordered[ai]; var b = ordered[bi];
                        if ((double)b.WorldBounds.Min.X - a.WorldBounds.Max.X > tolerance) break;
                        if ((double)b.WorldBounds.Min.Y - a.WorldBounds.Max.Y > tolerance
                            || (double)a.WorldBounds.Min.Y - b.WorldBounds.Max.Y > tolerance
                            || (double)b.WorldBounds.Min.Z - a.WorldBounds.Max.Z > tolerance
                            || (double)a.WorldBounds.Min.Z - b.WorldBounds.Max.Z > tolerance) continue;
                        if (includeResponsiblePair is not null && !includeResponsiblePair(a.Win.Plugin, b.Win.Plugin)) continue;
                        // --population (P25): a pair neither of whose references is in the plan's population is not measured.
                        if (includePair is not null && !includePair(a, b)) continue;
                        yield return (a, b);
                    }
            }
        }

        // Runs in sweep order on the calling thread, whatever measured the pair.
        void Accumulate(SurfaceOverlapCandidate a, SurfaceOverlapCandidate b, SurfaceQueryOutcome query, PairPath path, double seconds)
        {
            sweep.PairsChecked++;
            var timer = a.Win.Plugin == b.Win.Plugin ? sameTimer : crossTimer;
            timer?.Note(seconds, a.Key.ToString(), b.Key.ToString(), query, path);
            onPair?.Invoke(a, b, query, timer is null ? null : path, seconds);
            bool intersects = query.Intersects == true;
            bool unknown = query.Unknown;

            if (a.Win.Plugin == b.Win.Plugin)
            {
                if (retainSameResults) sweep.SameResults.Add(new(a, b, query, true));
                sweep.SameChecked++;
                if (intersects) sweep.SameIntersects++;
                if (unknown) sweep.SameUnknown++;
                string domain = a.Win.Interior ? "interior" : "exterior";
                var old = sweep.SameByDomain.GetValueOrDefault(domain);
                sweep.SameByDomain[domain] = (old.Intersects + (intersects ? 1 : 0), old.Unknown + (unknown ? 1 : 0), old.Checked + 1);
            }
            else
            {
                bool declared = closure[a.Win.Plugin].Contains(b.Win.Plugin) || closure[b.Win.Plugin].Contains(a.Win.Plugin);
                var hit = new SurfaceOverlapHit(a, b, query, declared);
                sweep.CrossResults.Add(hit);
                if (intersects) sweep.CrossHits.Add(hit);
                if (unknown) sweep.CrossUnknown++;
                if (declared)
                {
                    sweep.CrossDeclaredChecked++;
                    if (intersects) sweep.CrossDeclaredIntersects++;
                }
                else
                {
                    sweep.CrossUndeclaredChecked++;
                    if (intersects) sweep.CrossUndeclaredIntersects++;
                }
            }
            onProgress?.Invoke(sweep.PairsChecked);
        }

        SurfaceQueryOutcome Timed(SurfaceOverlapCandidate a, SurfaceOverlapCandidate b, PairTimer? timer, PairResultCache? cache, CancellationToken token, out PairPath path, out double seconds)
        {
            long started = System.Diagnostics.Stopwatch.GetTimestamp();
            var query = SurfaceQueryOutcome.QueryPlaced(a.WorldSoup, a.Placed, b.WorldSoup, b.Placed, tolerance, maxTrianglePairs, timeout, token, timer, prepared, cache, out path);
            seconds = MeshQueryTimer.Seconds(System.Diagnostics.Stopwatch.GetTimestamp() - started);
            return query;
        }

        if (threads <= 1)
        {
            foreach (var (a, b) in Reached())
            {
                var query = Timed(a, b, a.Win.Plugin == b.Win.Plugin ? sameTimer : crossTimer, results, cancellationToken, out var path, out var seconds);
                Accumulate(a, b, query, path, seconds);
            }
            return sweep;
        }

        var pairs = Reached().ToList();
        var slots = new PairSlot?[pairs.Count];
        // A pair the result cache serves, or that repeats an earlier pair's key, is left to the in-order pass, which treats it as one thread would.
        string[]? keys = null;
        bool[]? inOrder = null;
        if (results is not null)
        {
            keys = new string[pairs.Count];
            inOrder = new bool[pairs.Count];
            Parallel.For(0, pairs.Count, new ParallelOptions { MaxDegreeOfParallelism = threads },
                i => keys[i] = results.Key(pairs[i].A.WorldSoup, pairs[i].A.Placed, pairs[i].B.WorldSoup, pairs[i].B.Placed, tolerance, maxTrianglePairs));
            var first = new HashSet<string>(StringComparer.Ordinal);
            for (int i = 0; i < keys.Length; i++) inOrder[i] = results.Contains(keys[i]) || !first.Add(keys[i]);
        }

        int next = -1, consumed = 0, runAhead = PairRunAhead;
        bool noMoreLeases = false, stop = false;
        object filled = new(), window = new();
        var workerToken = TestWorkerToken ?? cancellationToken;
        var workers = new Thread[threads];
        for (int w = 0; w < threads; w++)
        {
            workers[w] = new Thread(() =>
            {
                while (!Volatile.Read(ref noMoreLeases) && !Volatile.Read(ref stop))
                {
                    int i = Interlocked.Increment(ref next);
                    if (i >= pairs.Count) return;
                    if (inOrder is not null && inOrder[i]) continue;
                    // The pair the in-order pass waits for is never past the window, so its holder never waits here.
                    if (i - Volatile.Read(ref consumed) > runAhead)
                        lock (window)
                            while (i - consumed > runAhead && !stop) Monitor.Wait(window);
                    if (Volatile.Read(ref stop)) return;
                    PairSlot slot;
                    try
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        var (a, b) = pairs[i];
                        // A timer per pair: only the pairs the in-order pass keeps reach the sweep's totals.
                        var mine = (a.Win.Plugin == b.Win.Plugin ? sameTimer : crossTimer) is null ? null : new PairTimer();
                        var query = StopOnOutOfMemory(Timed(a, b, mine, null, workerToken, out var path, out var seconds));
                        slot = new(query, path, seconds, mine, null);
                    }
                    catch (Exception ex)
                    {
                        // Leases stop, but pairs already leased finish, so the in-order pass reaches this one.
                        Volatile.Write(ref noMoreLeases, true);
                        slot = new(null, default, 0, null, System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(ex));
                    }
                    lock (filled) { slots[i] = slot; Monitor.PulseAll(filled); }
                }
            }, WorkerStackBytes) { IsBackground = true, Name = $"overlap pair {w}" };
            workers[w].Start();
        }
        try
        {
            for (int i = 0; i < pairs.Count; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var (a, b) = pairs[i];
                var timer = a.Win.Plugin == b.Win.Plugin ? sameTimer : crossTimer;
                if (inOrder is not null && inOrder[i])
                {
                    var served = StopOnOutOfMemory(Timed(a, b, timer, results, cancellationToken, out var servedPath, out var servedSeconds));
                    Accumulate(a, b, served, servedPath, servedSeconds);
                }
                else
                {
                    PairSlot? slot;
                    lock (filled)
                    {
                        while ((slot = slots[i]) is null) Monitor.Wait(filled);
                        slots[i] = null;
                    }
                    slot.Failure?.Throw();
                    var (query, path, seconds) = (slot.Query!, slot.Path, slot.Seconds);
                    // A worker's clock counts waits and contention: a time-out is measured again here, alone on a fresh clock.
                    if (TimedOut(query))
                    {
                        sweep.Remeasured++;
                        query = StopOnOutOfMemory(Timed(a, b, timer, null, cancellationToken, out path, out seconds));
                    }
                    else if (slot.Timer is { } mine) timer!.Add(mine);
                    // Measured without the cache: the cache sees it here, in sweep order, so its counts and its file's order are one thread's.
                    if (results is not null)
                    {
                        results.TryGet(keys![i], out _, out _);
                        results.Put(keys[i], query, path);
                    }
                    Accumulate(a, b, query, path, seconds);
                }
                lock (window) { consumed = i + 1; Monitor.PulseAll(window); }
            }
        }
        finally
        {
            lock (window) { stop = true; Monitor.PulseAll(window); }
            foreach (var worker in workers) worker.Join();
        }
        return sweep;
    }

    static bool TimedOut(SurfaceQueryOutcome q) => q.Reason is "cancelled" or "pair time budget exhausted";

    // The shared query turns any exception into a row; on many threads running out of memory stops the run instead.
    static SurfaceQueryOutcome StopOnOutOfMemory(SurfaceQueryOutcome q)
        => q.Reason?.StartsWith("mesh query failed: OutOfMemoryException", StringComparison.Ordinal) == true ? throw new OutOfMemoryException(q.Reason) : q;

    /// <summary>How many pairs past the in-order pass a worker may start, which bounds the finished results held.</summary>
    public static int PairRunAhead = 4096;

    /// <summary>Test hook only (overlapprobe --self-test): the token worker threads measure under, so a test can make a worker's
    /// measurement time out. Null in every real run.</summary>
    public static CancellationToken? TestWorkerToken;

    /// <summary>One pair's answer from a worker thread: the outcome, or the exception to raise at this pair's place in sweep order.</summary>
    sealed record PairSlot(SurfaceQueryOutcome? Query, PairPath Path, double Seconds, PairTimer? Timer, System.Runtime.ExceptionServices.ExceptionDispatchInfo? Failure);

    // Worker threads run the same recursive BVH build as the main thread, so they get a generous stack (reserved, not committed).
    const int WorkerStackBytes = 16 * 1024 * 1024;

    /// <summary>Apply the unchanged provenance rule to an actual-shape hit, rebuilding the arm without the later
    /// responsible plugin from strict model triangles. An unknown counterfactual is retained and withheld from credit.</summary>
    public SurfaceOverlapRow JudgeSurface(SurfaceOverlapHit hit, IReadOnlyList<HashSet<int>> closure,
        Func<FormKey, (TriangleSoup? Soup, char Src, string? Reason)> geometryOf, double tolerance = 1e-5,
        long maxTrianglePairs = 2_000_000, TimeSpan? pairTimeout = null, EnableArms? arms = null,
        CancellationToken cancellationToken = default, PairTimer? timer = null, PairResultCache? results = null)
    {
        var (later, other) = hit.A.Win.Plugin > hit.B.Win.Plugin ? (hit.A, hit.B) : (hit.B, hit.A);
        int mover = later.Win.Plugin;
        var snaps = History.Of(later.Key)!;
        int previousIndex = PlacementHistory.AuthoringSnapIndex(snaps, mover, closure);
        Snap? previous = previousIndex >= 0 ? snaps[previousIndex] : null;
        bool hadPrevious = previous is not null;
        var previousEnable = previous is not null && arms is not null ? arms.Without(later.Key, mover, closure) : default;
        bool enableKnown = previous is null || arms is null || previousEnable.Resolved;
        bool previousPresent = enableKnown && previous is { Deleted: false } p && p.Space == other.Win.Space
            && !IsDeletedWithout(later.Key, mover, closure)
            && (arms is null ? !p.Disabled : previousEnable.Enabled);
        SurfaceQueryOutcome? counterfactual = null;
        string counterfactualStatus = !enableKnown ? "unknown" : previousPresent ? "unknown" : "absent";
        string? counterfactualReason = !enableKnown ? "the previous arm's enable chain is unresolved"
            : previousPresent ? "the previous-placement render surface was not measured" : null;

        if (previousPresent)
        {
            FormKey previousBase = FormKey.Null;
            if (BaseChanges.TryGetValue(later.Key, out var changes))
                for (int i = changes.Count - 1; i >= 0; i--)
                {
                    int basePlugin = changes[i].Plugin;
                    if (basePlugin == mover || closure[basePlugin].Contains(mover)) continue;
                    previousBase = changes[i].Base;
                    break;
                }
            var (model, _, reason) = previousBase.IsNull ? (null, '-', "the previous arm has no base record") : geometryOf(previousBase);
            if (model is { TriangleCount: > 0 })
            {
                var world = SpatialOverlap.TransformSoup(model, previous!.Value.Pos, previous.Value.Rot, previous.Value.Scale);
                counterfactual = SurfaceQueryOutcome.QueryPlaced(world, new(model, previous.Value.Pos, previous.Value.Rot, previous.Value.Scale),
                    other.WorldSoup, other.Placed, tolerance, maxTrianglePairs,
                    pairTimeout ?? TimeSpan.FromSeconds(120), cancellationToken, timer, null, results);
                counterfactualStatus = counterfactual.Unknown ? "unknown" : "measured";
                counterfactualReason = counterfactual.Reason;
            }
            else counterfactualReason = reason ?? "the previous arm has no complete strict render triangles";
        }

        bool withoutIntersection = counterfactual?.Intersects == true;
        bool seen = closure[mover].Contains(other.Win.Plugin);
        var facts = new DetectorModel.OverlapFacts(hadPrevious, previousPresent, withoutIntersection, seen);
        var (origin, sight, why) = DetectorModel.OverlapProvenance(facts);
        OverlapOrigin? assessedOrigin = counterfactualStatus == "unknown" ? null : origin;
        if (assessedOrigin is null) why = counterfactualReason ?? "counterfactual actual-shape result is unknown";
        return new(hit, later, other, facts, assessedOrigin, sight, why, previous,
            History.AsSeenBy(other.Key, mover, closure), counterfactual, counterfactualStatus, counterfactualReason);
    }

    /// <summary>The pair sweep: a spatial hash of 256 u tiles inside a space partition (interior cell | worldspace),
    /// every pair in a tile tested once with <see cref="DetectorModel.BoxOverlap"/>. Same-responsible pairs are the
    /// authored floor, tallied by domain; cross-responsible deep hits are collected for judgement, with the declared
    /// relation (closure in either direction) recorded on each.</summary>
    public static PairSweep Pairs(IReadOnlyList<OverlapCandidate> candidates, IReadOnlyList<HashSet<int>> closure,
                                  float minDepth, float minFrac, float tile = 256f,
                                  Func<int, int, bool>? includeResponsiblePair = null)
    {
        var grid = new Dictionary<(FormKey Space, int X, int Y), List<int>>();
        for (int i = 0; i < candidates.Count; i++)
        {
            var c = candidates[i];
            var space = c.Win.Space;
            int x0 = (int)MathF.Floor(c.World.Min.X / tile), x1 = (int)MathF.Floor(c.World.Max.X / tile);
            int y0 = (int)MathF.Floor(c.World.Min.Y / tile), y1 = (int)MathF.Floor(c.World.Max.Y / tile);
            for (int x = x0; x <= x1; x++)
                for (int y = y0; y <= y1; y++)
                {
                    if (!grid.TryGetValue((space, x, y), out var l)) grid[(space, x, y)] = l = new List<int>();
                    l.Add(i);
                }
        }

        var sweep = new PairSweep();
        var seenPair = new HashSet<(FormKey, FormKey)>();
        foreach (var bucket in grid.Values)
        {
            if (bucket.Count < 2) continue;
            for (int a = 0; a < bucket.Count; a++)
                for (int b = a + 1; b < bucket.Count; b++)
                {
                    var ca = candidates[bucket[a]];
                    var cb = candidates[bucket[b]];
                    // A total order over FormKeys (ID, then ModKey name) so the same pair seen from two tiles
                    // dedupes regardless of which side came first.
                    bool aFirst = ca.Key.ID != cb.Key.ID
                        ? ca.Key.ID < cb.Key.ID
                        : string.CompareOrdinal(ca.Key.ModKey.FileName.String, cb.Key.ModKey.FileName.String) <= 0;
                    var key = aFirst ? (ca.Key, cb.Key) : (cb.Key, ca.Key);
                    if (!seenPair.Add(key)) continue;
                    if (includeResponsiblePair is not null && !includeResponsiblePair(ca.Win.Plugin, cb.Win.Plugin)) continue;
                    sweep.PairsChecked++;

                    var ov = Overlap(ca.World, cb.World);
                    bool deep = ov is { } o && o.MinDepth >= minDepth && o.VolumeFraction >= minFrac;

                    if (ca.Win.Plugin == cb.Win.Plugin)
                    {
                        sweep.SameChecked++;
                        var dom = ca.Win.Interior ? "interior" : "exterior";
                        var cur = sweep.SameByDomain.GetValueOrDefault(dom);
                        sweep.SameByDomain[dom] = (cur.Deep + (deep ? 1 : 0), cur.Checked + 1);
                        if (deep) sweep.SameDeep++;
                        continue;
                    }
                    bool declared = closure[ca.Win.Plugin].Contains(cb.Win.Plugin) || closure[cb.Win.Plugin].Contains(ca.Win.Plugin);
                    if (ov is { } intersection)
                        sweep.CrossIntersections.Add((ca, cb, intersection.MinDepth, intersection.VolumeFraction, deep, declared));
                    if (declared) { sweep.CrossDeclaredChecked++; if (deep) sweep.CrossDeclaredDeep++; }
                    else sweep.CrossUndeclaredChecked++;
                    if (deep) sweep.CrossDeep.Add(new OverlapHit(ca, cb, ov!.Value.MinDepth, ov.Value.VolumeFraction, declared));
                }
        }
        return sweep;
    }

    public static (float MinDepth, float VolumeFraction)? Overlap(Aabb a, Aabb b) =>
        DetectorModel.BoxOverlap(new Vector3(a.Min.X, a.Min.Y, a.Min.Z), new Vector3(a.Max.X, a.Max.Y, a.Max.Z),
                                 new Vector3(b.Min.X, b.Min.Y, b.Min.Z), new Vector3(b.Max.X, b.Max.Y, b.Max.Z));

    /// <summary>Assemble the rule's facts for one cross-responsible hit and decide it. The LATER of the two responsible
    /// plugins is L (plugin indices are load-order positions). The arm without L is L's reference at its previous
    /// snap: absent if L introduced it; not rebuildable if that snap was deleted, not there in the arm without L, or in
    /// another space; else its box under the same base record's geometry, tested against the other reference's WINNING
    /// box at the run's threshold. The base record is the winner's — an override that also swapped the base is a stated
    /// limit.
    ///
    /// <para><paramref name="arms"/> (Q27, 2026-09-06): the arm's own enable chain. Without it the counterfactual reads
    /// the previous snap's initially-disabled FLAG while the winning population goes through the chain — a winning-state
    /// answer standing in for a historical one, and it decides Introduced against Inherited, which is the class. With it,
    /// "present in the arm without L" is <see cref="EnableArms.Without"/>: each chain element's flag at its authoring
    /// snap and its XESP as last set outside L's side. The POSITION in that arm is still <c>snaps[^2]</c>, 4.1's stated
    /// simplification — where L placed the reference more than once the two halves come from different snaps, and that
    /// limit is 4.1's, not something this pairing introduces.</para></summary>
    public OverlapRow Judge(OverlapHit hit, IReadOnlyList<HashSet<int>> closure, Func<FormKey, (Aabb Box, char Src)> boxOf,
                            float minDepth, float minFrac, EnableArms? arms = null)
    {
        var (later, other) = hit.A.Win.Plugin > hit.B.Win.Plugin ? (hit.A, hit.B) : (hit.B, hit.A);
        int L = later.Win.Plugin;
        var snaps = History.Of(later.Key)!;
        Snap? prev = snaps.Count >= 2 ? snaps[^2] : null;
        bool hadPrev = prev is not null;
        bool prevPresent = prev is { Deleted: false } p && p.Space == other.Win.Space
                        && !IsDeletedWithout(later.Key, L, closure)
                        && (arms is null ? !p.Disabled : arms.Without(later.Key, L, closure).Enabled);
        float wDepth = float.NaN, wFrac = float.NaN;
        bool withoutDeep = false;
        string counterfactualStatus = prevPresent ? "unmeasurable" : "absent";
        string? counterfactualReason = prevPresent ? "the previous-placement geometry was not measured" : null;
        if (prevPresent)
        {
            var (box, src) = boxOf(later.Base);
            if (src != '-' && !box.IsEmpty)
            {
                var placed = SpatialOverlap.PlaceBounds(box, prev!.Value.Pos, prev.Value.Rot, prev.Value.Scale);
                if (placed.UsableForOverlap && placed.Bounds is { } world)
                {
                    counterfactualStatus = "measured";
                    counterfactualReason = null;
                    if (Overlap(world, other.World) is { } o)
                    {
                        wDepth = o.MinDepth; wFrac = o.VolumeFraction;
                        withoutDeep = o.MinDepth >= minDepth && o.VolumeFraction >= minFrac;
                    }
                    else { wDepth = 0; wFrac = 0; }
                }
                else counterfactualReason = placed.Reason;
            }
        }
        bool seen = closure[L].Contains(other.Win.Plugin);
        var facts = new DetectorModel.OverlapFacts(hadPrev, prevPresent, withoutDeep, seen);
        var (origin, sight, why) = DetectorModel.OverlapProvenance(facts);
        return new OverlapRow(hit, later, other, facts, origin, sight, why, prev,
                              History.AsSeenBy(other.Key, L, closure), wDepth, wFrac,
                              counterfactualStatus, counterfactualReason);
    }
}

/// <summary>Rest rule part 2: the weapon rack script on an activator base - its name, RackType and Patch14COARacks when set (null: the
/// script's default, 1 and false), and its object properties by name (keywords such as WeaponTypeBow, WRackTrigger, ArmorShield).</summary>
public sealed record RackScriptInfo(string Script, int? RackType, bool? CoaPatch, IReadOnlyDictionary<string, FormKey> Objects);
