using HousecarlCore;

namespace SkyWright.Engine;

/// <summary>All tested surface contacts, never an occupied-solid or exact intersection-volume measurement.</summary>
public sealed record MeshContactEvidenceValue(long CrossingPairs, long CoplanarPairs, long TouchingPairs,
    int ParticipatingTrianglesA, int ParticipatingTrianglesB, double[]? EnvelopeMin, double[]? EnvelopeMax);

/// <summary>Final value is null on incomplete work; PartialValue is diagnostic evidence only.</summary>
public sealed record MeshContactEvidence(string Status, string? Reason, MeshContactEvidenceValue? Value,
    MeshContactEvidenceValue? PartialValue, long TrianglePairsTested, string PolicyVersion)
{
    /// <summary>The origin both members were shifted by when world placement collapsed a triangle (<see cref="PairFrame"/>); the
    /// envelopes are back in world coordinates. Null (and not written) in the world frame.</summary>
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public double[]? Frame { get; init; }
    /// <summary>Zero-area-in-model triangles left out of the measure, [A, B]; null (and not written) when none were.</summary>
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public int[]? ZeroAreaSkipped { get; init; }
}

public static partial class MeshIntersection
{
    public const string ContactEvidencePolicyVersion = "contact_triangle_evidence_envelope_v1";

    /// <summary>Contact evidence on two placed members, prepared as the actual-shape relation and --surface prepare a pair
    /// (<see cref="PairFrame.PreparePlaced"/>): a pair with no zero-area triangle where placed is <see cref="MeasureContactEvidence"/>
    /// on the world soups, unchanged. Otherwise triangles with zero area in their own model are left out (ZeroAreaSkipped), a pair whose
    /// placement collapses a triangle is measured in the pair frame (Frame) with its envelopes shifted back to world coordinates, and the
    /// pair refuses, naming the collapse, only when no frame keeps every triangle that has model area.</summary>
    public static MeshContactEvidence MeasureContactEvidencePlaced(TriangleSoup worldA, PairFrame.Member? a, TriangleSoup worldB,
        PairFrame.Member? b, double tolerance = 1e-5, long maxTrianglePairs = 2_000_000, CancellationToken cancellationToken = default)
    {
        var p = worldA is null || worldB is null ? null : PairFrame.PreparePlaced(worldA, a, worldB, b);
        if (p is null) return MeasureContactEvidence(worldA!, worldB!, tolerance, maxTrianglePairs, cancellationToken);
        if (p.Refusal is { } why)
            return new("unknown", why, null, null, 0, ContactEvidencePolicyVersion) { Frame = p.Frame, ZeroAreaSkipped = p.ZeroAreaSkipped };
        var r = MeasureContactEvidence(p.A, p.B, tolerance, maxTrianglePairs, cancellationToken);
        MeshContactEvidenceValue? ToWorld(MeshContactEvidenceValue? v) => v is null || PairFrame.IsWorld(p.Origin) ? v
            : v with { EnvelopeMin = PairFrame.ToWorld(v.EnvelopeMin, p.Origin), EnvelopeMax = PairFrame.ToWorld(v.EnvelopeMax, p.Origin) };
        return r with { Value = ToWorld(r.Value), PartialValue = ToWorld(r.PartialValue), Frame = p.Frame, ZeroAreaSkipped = p.ZeroAreaSkipped };
    }

    /// <summary>
    /// Enumerates every actual triangle contact under a bounded query. After the surface predicate
    /// establishes contact, intersects each triangle's tolerance-expanded bounds and unions those
    /// intersections. This is a conservative envelope locating contact evidence, not the exact
    /// contact locus, volume, penetration depth or severity. No first-witness early return is used.
    /// </summary>
    public static MeshContactEvidence MeasureContactEvidence(TriangleSoup a, TriangleSoup b,
        double tolerance = 1e-5, long maxTrianglePairs = 2_000_000,
        CancellationToken cancellationToken = default)
    {
        MeshContactEvidence Refuse(string reason) => new("unknown", reason, null, null, 0, ContactEvidencePolicyVersion);
        if (a is null || b is null) return Refuse("null_mesh");
        if (!double.IsFinite(tolerance) || tolerance < 0) return Refuse("invalid_tolerance");
        if (maxTrianglePairs <= 0) return Refuse("invalid_triangle_pair_budget");
        var budget = new WorkBudget(maxTrianglePairs, cancellationToken);
        var participatedA = new HashSet<int>();
        var participatedB = new HashSet<int>();
        long crossing = 0, coplanar = 0, touching = 0;
        var envelope = Bounds.Empty;
        bool prepared = false;
        MeshContactEvidenceValue Snapshot() => new(crossing, coplanar, touching,
            participatedA.Count, participatedB.Count,
            participatedA.Count == 0 ? null : [envelope.Min.X, envelope.Min.Y, envelope.Min.Z],
            participatedA.Count == 0 ? null : [envelope.Max.X, envelope.Max.Y, envelope.Max.Z]);
        MeshContactEvidence Incomplete(string reason) => new("incomplete", reason, null,
            prepared ? Snapshot() : null, budget.Used, ContactEvidencePolicyVersion);
        try
        {
            if (!TryPrepare(a, "a", budget, out var ma, out var error)) return Refuse(error!);
            if (!TryPrepare(b, "b", budget, out var mb, out error)) return Refuse(error!);
            prepared = true;
            var stack = new Stack<(BvhNode A, BvhNode B)>();
            stack.Push((ma!.Root, mb!.Root));
            while (stack.Count > 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var pair = stack.Pop();
                if (!pair.A.Bounds.Overlaps(pair.B.Bounds, tolerance)) continue;
                if (pair.A.IsLeaf && pair.B.IsLeaf)
                {
                    for (int ia = pair.A.Start; ia < pair.A.Start + pair.A.Count; ia++)
                    for (int ib = pair.B.Start; ib < pair.B.Start + pair.B.Count; ib++)
                    {
                        int ta = ma.Order[ia], tb = mb.Order[ib];
                        var at = ma.Triangles[ta]; var bt = mb.Triangles[tb];
                        if (!at.Bounds.Overlaps(bt.Bounds, tolerance)) continue;
                        budget.Consume();
                        var relation = Classify(at, bt, tolerance);
                        if (relation == PairRelation.Separated) continue;
                        var margin = new DVec3(tolerance, tolerance, tolerance);
                        var low = DVec3.Max(at.Bounds.Min - margin, bt.Bounds.Min - margin);
                        var high = DVec3.Min(at.Bounds.Max + margin, bt.Bounds.Max + margin);
                        if (low.X > high.X || low.Y > high.Y || low.Z > high.Z ||
                            !double.IsFinite(low.X) || !double.IsFinite(low.Y) || !double.IsFinite(low.Z) ||
                            !double.IsFinite(high.X) || !double.IsFinite(high.Y) || !double.IsFinite(high.Z))
                            return Incomplete("contact_envelope_numerically_unresolved");
                        switch (relation)
                        {
                            case PairRelation.Crossing: crossing++; break;
                            case PairRelation.CoplanarOverlap: coplanar++; break;
                            case PairRelation.Touching: touching++; break;
                        }
                        participatedA.Add(ta); participatedB.Add(tb);
                        envelope = envelope.Union(new Bounds(low, high));
                    }
                    continue;
                }
                if (pair.B.IsLeaf || (!pair.A.IsLeaf && pair.A.Bounds.SurfaceArea >= pair.B.Bounds.SurfaceArea))
                {
                    stack.Push((pair.A.Left!, pair.B)); stack.Push((pair.A.Right!, pair.B));
                }
                else
                {
                    stack.Push((pair.A, pair.B.Left!)); stack.Push((pair.A, pair.B.Right!));
                }
            }
            return new("complete", null, Snapshot(), null, budget.Used, ContactEvidencePolicyVersion);
        }
        catch (BudgetExceededException) { return Incomplete("triangle_pair_budget_exhausted"); }
        catch (OperationCanceledException) { return Incomplete("cancelled"); }
    }
}
