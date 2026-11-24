using System.Numerics;

using HousecarlCore;

namespace SkyWright.Engine;

// POSE TRANSFER (reach rule `graph`, Part 3b). A Havok behaviour graph outputs bone local transforms; what it outputs at rest
// depends only on its files. A model confirmed by in-game look to rest in its stored pose therefore carries that confirmation to
// another model when (1) every graph file (project, character, behaviour, skeleton, clips) is byte-identical (same Data-relative
// path set, same sha256) and (2) the other model's stored local transform equals the confirmed model's on every graph-driven
// node (tolerances below). The look on the other model is validation; on contradiction the look wins.
//
// STATUS: implemented (red at fb6de1ba).

/// <summary>A node's stored local transform.</summary>
public readonly record struct NodeLocal(Vector3 T, Quaternion R, float S);

/// <summary>A confirmed model as a transfer source: its graph files (path → sha256), its stored locals on the graph-driven nodes,
/// and the confirmation label it carries.</summary>
public sealed record TransferSource(string ModelPath, IReadOnlyDictionary<string, string> GraphFiles,
                                    IReadOnlyDictionary<string, NodeLocal> DrivenLocals, string Label);

/// <summary><c>From</c>: the source the confirmation transfers from, or null; <c>Reason</c>: why (the first differing file or
/// node when null).</summary>
public sealed record TransferResult(TransferSource? From, string Reason);

public static class PoseTransfer
{
    /// <summary>Decide a transfer. Paths compare case-insensitively with either slash; sha256 case-insensitively; translation
    /// within <paramref name="tolT"/> units, rotation within <paramref name="tolR"/> radians, scale within 1e-4 relative.</summary>
    public static TransferResult Decide(IReadOnlyDictionary<string, string> graphFiles, IReadOnlyDictionary<string, NodeLocal> drivenLocals,
                                        IReadOnlyList<TransferSource> confirmed, float tolT = 1e-3f, float tolR = 1e-4f)
    {
        static string Norm(string p) => p.Replace('/', '\\').TrimStart('\\').ToLowerInvariant();
        var mine = graphFiles.ToDictionary(kv => Norm(kv.Key), kv => kv.Value.ToLowerInvariant());
        var reasons = new List<string>();
        foreach (var src in confirmed)
        {
            var theirs = src.GraphFiles.ToDictionary(kv => Norm(kv.Key), kv => kv.Value.ToLowerInvariant());
            string? why = null;
            foreach (var k in mine.Keys.Union(theirs.Keys).OrderBy(x => x, StringComparer.Ordinal))
            {
                if (!mine.TryGetValue(k, out var a)) { why = $"graph file {k} only on {src.ModelPath}"; break; }
                if (!theirs.TryGetValue(k, out var b)) { why = $"graph file {k} not on {src.ModelPath}"; break; }
                if (a != b) { why = $"graph file {k} differs from {src.ModelPath}'s"; break; }
            }
            if (why is null)
                foreach (var n in drivenLocals.Keys.Union(src.DrivenLocals.Keys).OrderBy(x => x, StringComparer.Ordinal))
                {
                    if (!drivenLocals.TryGetValue(n, out var x) || !src.DrivenLocals.TryGetValue(n, out var y)) { why = $"driven node {n} missing on one side"; break; }
                    if (Vector3.Distance(x.T, y.T) > tolT) { why = $"driven node {n}: stored translation differs by {Vector3.Distance(x.T, y.T)}"; break; }
                    float ang = HavokClip.AngleBetween(Quaternion.Normalize(x.R), Quaternion.Normalize(y.R));
                    if (ang > tolR) { why = $"driven node {n}: stored rotation differs by {ang} rad"; break; }
                    if (Math.Abs(x.S - y.S) > 1e-4f * Math.Max(1f, Math.Abs(y.S))) { why = $"driven node {n}: stored scale differs"; break; }
                }
            if (why is null) return new(src, $"graph files identical to {src.ModelPath}'s and stored locals equal on {drivenLocals.Count} driven nodes");
            reasons.Add(why);
        }
        return new(null, reasons.Count == 0 ? "no confirmed model to transfer from" : string.Join("; ", reasons));
    }
}
