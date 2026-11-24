using HousecarlCore;

namespace SkyWright.Engine;

/// <summary>Approximate acceleration storage and complete-face observability.
/// No omitted IDs are excluded from queries. ID arrays are diagnostic copies.</summary>
public sealed record TriangleGridSizing(float LegacyCell, double ChosenCell, int GroupingPower,
    long TargetBucketApproxBytes, long EstimatedBucketApproxBytes, long BucketApproxBytes,
    long IndexedIncidences, int IndexedCells, int[] LegacyOmittedIds, int[] OverflowIds,
    bool AllFacesQueryable, bool TargetMet, string? TargetReason, long ApproxBytes);
