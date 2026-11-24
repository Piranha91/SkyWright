using System.Security.Cryptography;

using HousecarlCore;

namespace SkyWright.Engine;

/// <summary>Closed outcomes for the static render-geometry contract used by object-overlap discovery.</summary>
public enum NifRenderGeometryStatus
{
    Measured,
    DegenerateGeometry,
    MissingVertices,
    PartialGeometry,
    UnsupportedGeometry,
    UnsupportedPose,
    InvalidTransform,
    ParseError,
    MissingAsset,
    MeasurementError,
}

/// <summary>A strict, versioned reading of one NIF's static render vertices. Bounds may be retained for an
/// unsupported or degenerate result so a catalogue can explain the refusal. Surface queries use
/// <see cref="UsableForSurfaceIntersection"/>: a flat box can enclose a valid triangle surface.
/// Stored NIF spheres and plugin ObjectBounds never enter this result.</summary>
public sealed record NifRenderGeometryOutcome(
    NifRenderGeometryStatus Status,
    string? Reason,
    Aabb? ModelBounds,
    TriangleSoup? Soup,
    NifGeometryModel? Model,
    string ContentSha256,
    string CacheKey)
{
    public bool Complete => Status is NifRenderGeometryStatus.Measured or NifRenderGeometryStatus.DegenerateGeometry;
    /// <summary>Legacy positive-volume bounds eligibility, retained for historical bounds diagnostics.</summary>
    public bool UsableForOverlap => Status == NifRenderGeometryStatus.Measured;
    /// <summary>Complete decoded surfaces, including flat models. The mesh predicate separately validates indices,
    /// finite coordinates and triangle degeneracy; this property does not certify a closed solid.</summary>
    public bool UsableForSurfaceIntersection => Complete && Soup is { TriangleCount: > 0 };
}

/// <summary>The NIF-only geometry policy for reusable overlap measurements.
/// <para>It measures decoded vertices from every statically visible Nifly-exposed render shape. A shape hidden by bit
/// zero on itself or an ancestor is excluded. Visible LOD shapes are included conservatively, independent of viewing
/// distance. Editor-marker RECORDS are excluded by the caller; names are never used as geometry classifications.
/// Skinned shapes, controlled node chains, switch nodes and every shape of a file that references a behaviour graph
/// (BSBehaviorGraphExtraData) are refused because their stored pose does not establish the live pose. Any unreadable
/// visible shape makes the result partial/unsupported instead of silently shrinking the
/// bounds. Negative uniform node scale is supported as a mirror; zero scale remains finite degenerate geometry.</para>
/// </summary>
public static class NifOverlapGeometry
{
    public const string ReaderVersion = "NiflySharp-1.1.0+houseCARL-render-reader-v2";
    public const string PolicyVersion = "nif-render-vertices-aabb-v1";
    /// <summary>Complete measurement identity for reports and cross-run compatibility gates.</summary>
    public const string MeasurementPolicy = ReaderVersion + "|" + PolicyVersion;
    public const string SurfacePolicyVersion = "nif-render-triangles-v1";
    /// <summary>Actual-shape policy identity; never comparable to historical enclosing-box measurements.</summary>
    public const string SurfaceMeasurementPolicy = ReaderVersion + "|" + SurfacePolicyVersion + "|" + MeshIntersection.PolicyVersion;

    /// <summary>Tolerance changes the measured contact predicate, so differential credit requires the same value.</summary>
    public static string SurfaceMeasurementIdentity(double tolerance) =>
        SurfaceMeasurementPolicy + "|tolerance=" + tolerance.ToString("R", System.Globalization.CultureInfo.InvariantCulture);

    public static NifRenderGeometryOutcome Measure(byte[] bytes, bool includeTriangles = false)
    {
        string sha = bytes is null ? "" : Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
        string key = CacheIdentity(sha, includeTriangles);
        var raw = NifGeometry.MeasureVisibleRender(bytes!, includeTriangles);
        if (raw.Model is null)
        {
            var fatalStatus = raw.FailureKind switch
            {
                NifGeometryFailureKind.EmptyAsset => NifRenderGeometryStatus.MissingAsset,
                NifGeometryFailureKind.ParseError => NifRenderGeometryStatus.ParseError,
                NifGeometryFailureKind.InvalidTransform => NifRenderGeometryStatus.InvalidTransform,
                _ => NifRenderGeometryStatus.MeasurementError,
            };
            return new(fatalStatus, raw.Error, null, null, null, sha, key);
        }

        var model = raw.Model;
        var visible = model.Shapes.Where(s => !s.Hidden).ToList();
        var decoded = visible.Where(s => s.VerticesRead && !s.Bounds.IsEmpty).ToList();
        var poseUnsupported = visible.Where(s => s.PoseIssue is not null).ToList();
        var vertexUnsupported = visible.Where(s => !s.VerticesRead).ToList();
        var emptyShapes = visible.Where(s => s.VerticesRead && s.Bounds.IsEmpty).ToList();
        var triangleUnsupported = includeTriangles
            ? visible.Where(s => !s.TrianglesRead || s.InvalidTriangleCount > 0).ToList()
            : new List<NifShapeGeometry>();

        NifRenderGeometryStatus status;
        string? reason;
        if (visible.Count == 0)
        {
            status = NifRenderGeometryStatus.MissingVertices;
            reason = model.Shapes.Count == 0
                ? "the NIF exposes no render shapes"
                : "every render shape is statically hidden";
        }
        else if (decoded.Count == 0 && poseUnsupported.Count > 0)
        {
            status = NifRenderGeometryStatus.UnsupportedPose;
            reason = JoinReasons(poseUnsupported.Select(s => s.PoseIssue!));
        }
        else if (decoded.Count == 0 && vertexUnsupported.Count > 0)
        {
            status = NifRenderGeometryStatus.UnsupportedGeometry;
            reason = $"none of the {visible.Count} visible render shapes exposes readable vertices";
        }
        else if (decoded.Count == 0)
        {
            status = NifRenderGeometryStatus.MissingVertices;
            reason = $"the {visible.Count} visible render shapes contain no decoded vertices";
        }
        else if (!model.RenderComplete || poseUnsupported.Count > 0 || vertexUnsupported.Count > 0 || emptyShapes.Count > 0 || triangleUnsupported.Count > 0)
        {
            status = NifRenderGeometryStatus.PartialGeometry;
            var reasons = new List<string>();
            if (poseUnsupported.Count > 0) reasons.Add($"{poseUnsupported.Count} visible shape(s) have unsupported live pose");
            if (vertexUnsupported.Count > 0) reasons.Add($"{vertexUnsupported.Count} visible shape(s) have unreadable vertices");
            if (emptyShapes.Count > 0) reasons.Add($"{emptyShapes.Count} visible shape(s) decode no vertices");
            if (triangleUnsupported.Count > 0) reasons.Add($"{triangleUnsupported.Count} visible shape(s) have incomplete triangles");
            if (!model.RenderComplete) reasons.AddRange(model.RenderUnread!);
            reason = string.Join("; ", reasons);
        }
        else if (model.Bounds.IsEmpty)
        {
            status = NifRenderGeometryStatus.MissingVertices;
            reason = "decoded render vertices produced no model bounds";
        }
        else if (!SpatialOverlap.IsFinite(model.Bounds))
        {
            status = NifRenderGeometryStatus.InvalidTransform;
            reason = "decoded render vertices produced non-finite model bounds";
        }
        else if (SpatialOverlap.IsDegenerate(model.Bounds))
        {
            status = NifRenderGeometryStatus.DegenerateGeometry;
            reason = "decoded render bounds have zero extent on at least one axis";
        }
        else
        {
            status = NifRenderGeometryStatus.Measured;
            reason = null;
        }

        return new(status, reason, model.Bounds.IsEmpty ? null : model.Bounds,
                   includeTriangles ? model.Soup : null, model, sha, key);
    }

    public static string CacheIdentity(string contentSha256, bool includeTriangles = false) =>
        $"{ReaderVersion}|{PolicyVersion}|triangles={(includeTriangles ? 1 : 0)}|sha256={contentSha256.ToLowerInvariant()}";

    static string JoinReasons(IEnumerable<string> reasons) =>
        string.Join("; ", reasons.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal));
}

/// <summary>Process-local cache whose identity includes consumed bytes, reader version, geometry policy and whether
/// triangle soup was requested. Placement is deliberately outside this cache: two references sharing bytes retain
/// their own winning transforms.</summary>
public sealed class NifRenderGeometryCache
{
    readonly Dictionary<string, NifRenderGeometryOutcome> _entries = new(StringComparer.Ordinal);
    public long Hits { get; private set; }
    public long Misses { get; private set; }

    public NifRenderGeometryOutcome Measure(byte[] bytes, bool includeTriangles = false)
    {
        string sha = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
        string key = NifOverlapGeometry.CacheIdentity(sha, includeTriangles);
        if (_entries.TryGetValue(key, out var hit)) { Hits++; return hit; }
        Misses++;
        return _entries[key] = NifOverlapGeometry.Measure(bytes, includeTriangles);
    }
}
