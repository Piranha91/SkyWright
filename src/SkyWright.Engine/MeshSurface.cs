using System.Text.Json.Serialization;

using HousecarlCore;

namespace SkyWright.Engine;

// MEASURED SURFACE CONTACT (patch-miss packet, session 6): how two placed surfaces meet where the overlap VOLUME
// refuses (open meshes, alpha materials, no solid collision).
//
// Quantities, one geometry at a time (render or collision, never mixed), both members placed by their own transforms:
//  * Crossing length L: the length of the curve along which the two surfaces cross transversally. Interior segments
//    (a triangle of each surface straddling the other's plane) come from MeshIntersection's exact predicates. A segment
//    lying along an edge of one surface (or of both) counts only when the surfaces actually cross there: the two faces
//    incident to the edge lie strictly on opposite sides of the other surface (edge on a face), or the four half-planes
//    around a common edge interleave (edge on edge). Such a segment is counted once. Where an edge has other than two
//    incident faces, the side test cannot decide and the length is added to the upper bound only: L is an interval.
//  * Coplanar overlap area, split same-facing (two visible surfaces in one plane: z-fighting) and opposite-facing
//    (resting contact).
//  * Buried area: the area of one surface inside the other, only when the other is a validated closed solid; a
//    certified interval from triangle subdivision (inside/outside by ray parity), also as a fraction of the surface's
//    own area. "Fully buried" only when the lower bound reaches the area within the rounding bound.
// Every crossing length and coplanar area is split by the material classes of the two shapes involved; the opaque x
// opaque part is the one that may decide an attribution. Nothing is estimated from bounds or stored poses: a member
// whose live pose is not established is refused with its cause.
//
// STATUS: API frozen at the session 6 red gate (proofprobe OverlapSurfaceProof).

/// <summary>Material class of a render shape (the flags <see cref="MeshVolume"/>'s material gate reads), or
/// <c>collision</c> for a Havok body triangle.</summary>
public static class SurfaceMaterial
{
    public const string Opaque = "opaque", AlphaTest = "alpha_test", AlphaBlend = "alpha_blend", Effect = "effect",
                        NoDepthWrite = "no_depth_write", Collision = "collision";
    /// <summary>A render triangle of a shape the game does not draw (<see cref="ShapeDrawing"/>): kept in the soup for index
    /// alignment, left out of every contact measure (surface contact, box contact, the anchor body). Usage-week M4 (2026-10-14).</summary>
    public const string NotDrawn = "not_drawn";
    /// <summary>The key a per-class split uses: "tagA x tagB".</summary>
    public static string Pair(string a, string b) => a + " x " + b;
}

/// <summary>How a render shape's live pose is decided. <c>Blanket</c> is the existing rule (any controller on the
/// shape's node chain, a skin or a switch node refuses). <c>Targeted</c> refuses a shape only when something the reader
/// can see moves it: a skin, a switch node on its chain, a transform controlled block of any sequence or a direct
/// transform controller targeting a node on its chain, or a behaviour graph referenced by the file.</summary>
public enum LivePosePolicy { Blanket, Targeted }

/// <summary>The live-pose rule a caller gets when it names none (G1 build list, row 5: Targeted, after the check run
/// outside G1; Blanket stays selectable).</summary>
public static class LivePoseDefault { public const LivePosePolicy Policy = LivePosePolicy.Targeted; }

public sealed record SurfaceInterval(double Lower, double Upper)
{
    public bool Contains(double value, double tolerance = 0) => value >= Lower - tolerance && value <= Upper + tolerance;
    public double Width => Upper - Lower;
}

/// <summary>Area of one surface inside the other member's closed solid. Status <c>measured</c>, <c>refused</c> (the
/// container is not a validated closed solid; Reason names it) or <c>unknown</c> (budget/cancellation). Area is the
/// buried surface's own total area; Fraction = Inside / Area.</summary>
public sealed record BuriedSurface(string Status, string? Reason, double? Area, SurfaceInterval? Inside,
                                   SurfaceInterval? Fraction, bool Converged, bool FullyBuried, long Pieces);

/// <summary>Status <c>measured</c>, <c>incomplete</c> (budget or cancellation: no final values; PartialCrossing is
/// diagnostic only), <c>refused</c> (Reason names member A or B) or <c>unknown</c>.</summary>
public sealed record SurfaceContactResult(
    string Status, string? Reason, string Geometry,
    SurfaceInterval? CrossingLength,
    IReadOnlyDictionary<string, double>? CrossingLengthByTags,
    double? UndecidedEdgeLength,
    double? CoplanarSameFacingArea, double? CoplanarOppositeFacingArea,
    IReadOnlyDictionary<string, double>? CoplanarSameFacingByTags,
    IReadOnlyDictionary<string, double>? CoplanarOppositeFacingByTags,
    double RoundingBound, long InteriorSegments, long EdgeSegments,
    double[]? EnvelopeMin, double[]? EnvelopeMax,
    BuriedSurface? AInB, BuriedSurface? BInA,
    double? PartialCrossing, long TrianglePairsTested)
{
    public string PolicyVersion => MeshSurface.PolicyVersion;
    /// <summary>Triangles left out of the measure because they have zero area in the member's own model space (a repeated index,
    /// or three collinear vertices): they carry no area to cross or cover and no normal. Null (and not written) when none were.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public ZeroAreaSkip? ZeroAreaSkipped { get; init; }
    /// <summary>Triangles left out of the measure because their shape is not drawn in game (<see cref="ShapeDrawing"/>; tagged
    /// <see cref="SurfaceMaterial.NotDrawn"/>). Null (and not written) when none were.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public ZeroAreaSkip? NotDrawnSkipped { get; init; }
}

/// <summary>How many zero-area triangles were left out of each member's measured surface (<see cref="SurfaceContactResult.ZeroAreaSkipped"/>).</summary>
public sealed record ZeroAreaSkip(int A, int B);

public sealed record SurfaceOptions(double Tolerance = 1e-5, long MaxTrianglePairs = 50_000_000,
                                    double BuriedRelativeTarget = 0.01, double BuriedAbsoluteTarget = 1.0,
                                    int BuriedMaxDepth = 18, long BuriedMaxPieces = 4_000_000);

/// <summary>A model-space surface with one material tag per triangle, or a refusal (Status <c>refused</c>, Reason
/// naming the cause: e.g. <c>live pose: skinned shape 'X'</c>, <c>live pose: animated node 'N' (sequence 'S',
/// CYCLE_LOOP)</c>, <c>live pose: switch node 'N'</c>, <c>live pose: behaviour graph 'path'</c>).</summary>
public sealed record TaggedSurface(string Status, string? Reason, TriangleSoup? Soup, string[]? Tags, IReadOnlyList<string>? Labels = null)
{
    /// <summary>Visible render shapes that declare no vertices and no triangles: read as empty (they draw nothing) and named here,
    /// not refused as unreadable. Null when there are none.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyList<string>? EmptyShapes { get; init; }
}

/// <summary>Whether a world box meets a placed surface's triangles (exact triangle-box test): OpaqueMeets counts opaque triangles
/// only, AnyMeets every triangle; Reason names a refusal (both false then). Lane 3b, gap e: a particle region against a partner.
/// Implemented (red at 7bbbe518).</summary>
public sealed record BoxContactResult(bool OpaqueMeets, bool AnyMeets, string? Reason);

public static partial class MeshSurfaceBox
{
    /// <summary>Exact closed triangle-box overlap (separating axes: the box's 3 face normals, the triangle's normal, the 9 edge
    /// cross products) in double precision on the float vertices. A non-finite vertex or box refuses.</summary>
    public static BoxContactResult BoxContact(TriangleSoup world, string[]? tags, Aabb box)
    {
        if (box.IsEmpty) return new(false, false, "box contact: empty box");
        if (world is null || world.TriangleCount == 0) return new(false, false, "box contact: empty surface");
        if (tags is not null && tags.Length != world.TriangleCount) return new(false, false, "box contact: tag count differs from triangle count");
        double[] lo = [box.Min.X, box.Min.Y, box.Min.Z], hi = [box.Max.X, box.Max.Y, box.Max.Z];
        if (lo.Concat(hi).Any(v => !double.IsFinite(v))) return new(false, false, "box contact: non-finite box");
        double[] c = [(lo[0] + hi[0]) / 2, (lo[1] + hi[1]) / 2, (lo[2] + hi[2]) / 2], h = [(hi[0] - lo[0]) / 2, (hi[1] - lo[1]) / 2, (hi[2] - lo[2]) / 2];
        bool opaque = false, any = false;
        for (int t = 0; t < world.TriangleCount; t++)
        {
            if (tags is not null && tags[t] == SurfaceMaterial.NotDrawn) continue;   // not drawn in game: takes no part (usage-week M4)
            bool isOpaque = tags is null || tags[t] == SurfaceMaterial.Opaque;
            if (any && (opaque || !isOpaque)) continue;   // nothing more this triangle could add
            var tri = new double[3][];
            for (int k = 0; k < 3; k++)
            {
                var p = world.Vertices[world.Indices[3 * t + k]];
                if (!float.IsFinite(p.X) || !float.IsFinite(p.Y) || !float.IsFinite(p.Z)) return new(false, false, "box contact: non-finite vertex");
                tri[k] = [p.X - c[0], p.Y - c[1], p.Z - c[2]];
            }
            if (TriBox(tri, h)) { any = true; if (isOpaque) opaque = true; }
            if (opaque) break;
        }
        return new(opaque, any, null);
    }

    static bool TriBox(double[][] v, double[] h)
    {
        double scale = 1 + h.Max() + v.Max(p => p.Max(Math.Abs));
        double eps = 1e-12 * scale;
        bool Separated(double[] axis)
        {
            double len2 = axis[0] * axis[0] + axis[1] * axis[1] + axis[2] * axis[2];
            if (len2 <= 1e-30) return false;   // a degenerate axis separates nothing
            double p0 = Dot(v[0], axis), p1 = Dot(v[1], axis), p2 = Dot(v[2], axis);
            double r = h[0] * Math.Abs(axis[0]) + h[1] * Math.Abs(axis[1]) + h[2] * Math.Abs(axis[2]);
            double tol = eps * Math.Sqrt(len2) * scale;
            return Math.Min(p0, Math.Min(p1, p2)) > r + tol || Math.Max(p0, Math.Max(p1, p2)) < -r - tol;
        }
        double[][] e = [Sub(v[1], v[0]), Sub(v[2], v[1]), Sub(v[0], v[2])];
        double[][] u = [[1, 0, 0], [0, 1, 0], [0, 0, 1]];
        foreach (var a in u) if (Separated(a)) return false;
        if (Separated(Cross(e[0], e[1]))) return false;
        foreach (var a in u) foreach (var ed in e) if (Separated(Cross(a, ed))) return false;
        return true;
        static double Dot(double[] a, double[] b) => a[0] * b[0] + a[1] * b[1] + a[2] * b[2];
        static double[] Sub(double[] a, double[] b) => [a[0] - b[0], a[1] - b[1], a[2] - b[2]];
        static double[] Cross(double[] a, double[] b) => [a[1] * b[2] - a[2] * b[1], a[2] * b[0] - a[0] * b[2], a[0] * b[1] - a[1] * b[0]];
    }
}

public static class MeshSurface
{
    public const string PolicyVersion = "surface_crossing_curve_v1";
    const double AreaRounding = 1e-9;

    /// <summary>Surface contact of two world-space soups. Tags are per triangle (null: every triangle
    /// <see cref="SurfaceMaterial.Opaque"/> for render, <see cref="SurfaceMaterial.Collision"/> for collision).</summary>
    public static SurfaceContactResult Measure(TriangleSoup a, string[]? tagsA, TriangleSoup b, string[]? tagsB,
                                               VolumeGeometry geometry, SurfaceOptions? options = null,
                                               CancellationToken cancellationToken = default)
        => Measure(a, tagsA, b, tagsB, geometry, options, cancellationToken, sink: null);

    /// <summary><see cref="Measure(TriangleSoup, string[], TriangleSoup, string[], VolumeGeometry, SurfaceOptions, CancellationToken)"/>
    /// with an opt-in receiver of every crossing segment as it is summed (lane D, <see cref="SurfaceVisibility"/>). The result is the
    /// same with or without one.</summary>
    internal static SurfaceContactResult Measure(TriangleSoup a, string[]? tagsA, TriangleSoup b, string[]? tagsB,
                                                 VolumeGeometry geometry, SurfaceOptions? options, CancellationToken cancellationToken,
                                                 Action<CrossingPiece>? sink)
    {
        options ??= new SurfaceOptions();
        string g = geometry.ToString().ToLowerInvariant();
        string fill = geometry == VolumeGeometry.Collision ? SurfaceMaterial.Collision : SurfaceMaterial.Opaque;
        SurfaceContactResult Refuse(string status, string reason) =>
            new(status, reason, g, null, null, null, null, null, null, null, 0, 0, 0, null, null, null, null, null, 0);
        if (a is null || a.TriangleCount == 0) return Refuse("refused", "A: empty_mesh");
        if (b is null || b.TriangleCount == 0) return Refuse("refused", "B: empty_mesh");
        tagsA ??= Enumerable.Repeat(fill, a.TriangleCount).ToArray();
        tagsB ??= Enumerable.Repeat(fill, b.TriangleCount).ToArray();
        if (tagsA.Length != a.TriangleCount) return Refuse("refused", "A: tag count differs from triangle count");
        if (tagsB.Length != b.TriangleCount) return Refuse("refused", "B: tag count differs from triangle count");
        // Usage-week M4 (2026-10-14): triangles of a shape the game does not draw (tagged not_drawn by ModelSurface) take no part in the
        // measure, whatever path reached here; a member left with nothing drawn has nothing to cross (measured, no crossing).
        if (tagsA.Contains(SurfaceMaterial.NotDrawn) || tagsB.Contains(SurfaceMaterial.NotDrawn))
        {
            var (a2, ta2, ka, da) = DropNotDrawn(a, tagsA);
            var (b2, tb2, kb, db) = DropNotDrawn(b, tagsB);
            if (a2.TriangleCount == 0 || b2.TriangleCount == 0) return NothingDrawn(geometry) with { NotDrawnSkipped = new(da, db) };
            var inner = sink;
            var mapped = inner is null ? null : (Action<CrossingPiece>)(p => inner(p with { TrianglesA = p.TrianglesA.Select(t => ka[t]).ToArray(), TrianglesB = p.TrianglesB.Select(t => kb[t]).ToArray() }));
            return Measure(a2, ta2, b2, tb2, geometry, options, cancellationToken, mapped) with { NotDrawnSkipped = new(da, db) };
        }

        var core = MeshIntersection.SurfaceContact(a, tagsA, b, tagsB, options.MaxTrianglePairs, cancellationToken, sink);
        if (core.Status == "refused") return Refuse("refused", core.Reason!);
        var t = core.Totals!;
        if (core.Status != "measured")
            return new("incomplete", core.Reason, g, null, null, null, null, null, null, null, t.Rounding, t.InteriorSegments, t.EdgeSegments,
                       null, null, null, null, t.Decided, t.Pairs);
        var aInB = Buried(a, b, "B", options, cancellationToken);
        var bInA = Buried(b, a, "A", options, cancellationToken);
        double[]? envMin = t.Envelope.IsEmpty ? null : [t.Envelope.MinX, t.Envelope.MinY, t.Envelope.MinZ];
        double[]? envMax = t.Envelope.IsEmpty ? null : [t.Envelope.MaxX, t.Envelope.MaxY, t.Envelope.MaxZ];
        return new("measured", null, g, new SurfaceInterval(t.Decided, t.Decided + t.Undecided), t.Crossing, t.Undecided,
                   t.SameFacing.Values.Sum(), t.OppositeFacing.Values.Sum(), t.SameFacing, t.OppositeFacing,
                   t.Rounding, t.InteriorSegments, t.EdgeSegments, envMin, envMax, aInB, bInA, null, t.Pairs);
    }

    // The area of `surface` inside `container` when the container is a validated closed solid.
    static BuriedSurface Buried(TriangleSoup surface, TriangleSoup container, string containerName, SurfaceOptions o, CancellationToken ct)
    {
        var prep = MeshIntersection.PrepareVolumeSolid(container, o.MaxTrianglePairs, ct);
        if (prep.Solid is null)
            return new(prep.Unknown ? "unknown" : "refused", $"{containerName}: {prep.Reason}", null, null, null, false, false, 0);
        var r = MeshIntersection.BuriedArea(surface, prep.Solid, o.BuriedRelativeTarget, o.BuriedAbsoluteTarget, o.BuriedMaxDepth, o.BuriedMaxPieces, ct);
        if (r.Status != "measured") return new(r.Status, r.Reason, r.Area, null, null, false, false, r.Pieces);
        var inside = new SurfaceInterval(r.Lower, r.Upper);
        var fraction = r.Area > 0 ? new SurfaceInterval(Math.Clamp(r.Lower / r.Area, 0, 1), Math.Clamp(r.Upper / r.Area, 0, 1)) : new SurfaceInterval(0, 0);
        bool full = r.Area > 0 && r.Lower >= r.Area * (1 - AreaRounding);
        return new("measured", r.Reason, r.Area, inside, fraction, r.Converged, full, r.Pieces);
    }

    /// <summary>Two model-space soups placed by their own references' transforms (<see cref="SpatialOverlap.TransformSoup"/>),
    /// then measured.</summary>
    public static SurfaceContactResult MeasurePlaced(TriangleSoup modelA, string[]? tagsA, Vec3 posA, Vec3 rotA, float scaleA,
                                                     TriangleSoup modelB, string[]? tagsB, Vec3 posB, Vec3 rotB, float scaleB,
                                                     VolumeGeometry geometry, SurfaceOptions? options = null,
                                                     CancellationToken cancellationToken = default)
        => MeasurePlacedFramed(modelA, tagsA, posA, rotA, scaleA, modelB, tagsB, posB, rotB, scaleB, geometry, options, cancellationToken).Result;

    /// <summary><see cref="MeasurePlaced"/> with the frame it was measured in: Origin (0,0,0) is the world frame; otherwise the pair
    /// was measured shifted by -Origin because world placement collapsed a triangle (<see cref="PairFrame"/>: B's position, the pair's
    /// geometry centre or A's position), and the envelope has been shifted back to world coordinates.</summary>
    public static (SurfaceContactResult Result, Vec3 Origin) MeasurePlacedFramed(TriangleSoup modelA, string[]? tagsA, Vec3 posA, Vec3 rotA, float scaleA,
                                                     TriangleSoup modelB, string[]? tagsB, Vec3 posB, Vec3 rotB, float scaleB,
                                                     VolumeGeometry geometry, SurfaceOptions? options = null,
                                                     CancellationToken cancellationToken = default)
        => MeasurePlacedFramed(modelA, tagsA, posA, rotA, scaleA, modelB, tagsB, posB, rotB, scaleB, geometry, options, cancellationToken, null, out _, out _);

    /// <summary><see cref="MeasurePlacedFramed(TriangleSoup, string[], Vec3, Vec3, float, TriangleSoup, string[], Vec3, Vec3, float, VolumeGeometry, SurfaceOptions, CancellationToken)"/>
    /// with an opt-in receiver of every crossing segment (<see cref="SurfaceVisibility"/>), in the measuring frame and with triangle
    /// indices of the full placed soups (zero-area triangles left out of the measure are mapped back). PlacedA / PlacedB are those
    /// soups. The result is the same with or without a sink.</summary>
    internal static (SurfaceContactResult Result, Vec3 Origin) MeasurePlacedFramed(TriangleSoup modelA, string[]? tagsA, Vec3 posA, Vec3 rotA, float scaleA,
                                                     TriangleSoup modelB, string[]? tagsB, Vec3 posB, Vec3 rotB, float scaleB,
                                                     VolumeGeometry geometry, SurfaceOptions? options, CancellationToken cancellationToken,
                                                     Action<CrossingPiece>? sink, out TriangleSoup placedA, out TriangleSoup placedB)
    {
        var o = PairFrame.Origin(modelA, posA, rotA, scaleA, modelB, posB, rotB, scaleB);
        bool world = PairFrame.IsWorld(o);
        var wa = SpatialOverlap.TransformSoup(modelA, world ? posA : PairFrame.Shift(posA, o), rotA, scaleA);
        var wb = SpatialOverlap.TransformSoup(modelB, world ? posB : PairFrame.Shift(posB, o), rotB, scaleB);
        placedA = wa; placedB = wb;
        var r = MeasureSkippingZeroArea(modelA, wa, tagsA, modelB, wb, tagsB, geometry, options, cancellationToken, sink);
        if (world) return (r, o);
        return (r with { EnvelopeMin = PairFrame.ToWorld(r.EnvelopeMin, o), EnvelopeMax = PairFrame.ToWorld(r.EnvelopeMax, o) }, o);
    }

    // Group 6 D1 (2026-10-05): a triangle with zero area in its member's own model space (a repeated index, or three collinear
    // vertices) has no area to cross or cover and no normal, so it is left out of the measure and counted (ZeroAreaSkipped) rather
    // than refusing the pair. A triangle with model-space area that is zero-area where it is measured was collapsed by placement:
    // it has real area, so it is not skipped and the pair still refuses, naming the collapse. That happens only when PairFrame.Origin
    // found no frame (centred on B, on the geometry or on A) that keeps every triangle (lane D, 2026-10-10).
    static SurfaceContactResult MeasureSkippingZeroArea(TriangleSoup modelA, TriangleSoup placedA, string[]? tagsA,
                                                        TriangleSoup modelB, TriangleSoup placedB, string[]? tagsB,
                                                        VolumeGeometry geometry, SurfaceOptions? options, CancellationToken ct,
                                                        Action<CrossingPiece>? sink = null)
    {
        var (sa, ta, na, whyA) = PairFrame.SkipZeroArea(modelA, placedA, tagsA, "A: a");
        var (sb, tb, nb, whyB) = PairFrame.SkipZeroArea(modelB, placedB, tagsB, "B: b");
        if ((whyA ?? whyB) is { } why)
            return new("refused", why, geometry.ToString().ToLowerInvariant(), null, null, null, null, null, null, null, 0, 0, 0, null, null, null, null, null, 0);
        // a sink sees the full soups' triangle indices: the kept triangles are mapped back to their place in the placed soup
        if (sink is not null && na + nb > 0)
        {
            int[] mapA = KeptIndex(modelA, placedA, na), mapB = KeptIndex(modelB, placedB, nb);
            var inner = sink;
            sink = p => inner(p with { TrianglesA = p.TrianglesA.Select(t => mapA[t]).ToArray(), TrianglesB = p.TrianglesB.Select(t => mapB[t]).ToArray() });
        }
        var r = Measure(sa, ta, sb, tb, geometry, options, ct, sink);
        return na + nb == 0 ? r : r with { ZeroAreaSkipped = new(na, nb) };
    }

    // The measured result of a pair one of whose members draws nothing: no crossing, no coplanar area, nothing buried.
    static SurfaceContactResult NothingDrawn(VolumeGeometry geometry)
    {
        var none = new Dictionary<string, double>();
        var buried = new BuriedSurface("measured", "a member draws nothing (every triangle not drawn)", 0, new SurfaceInterval(0, 0), new SurfaceInterval(0, 0), true, false, 0);
        return new("measured", null, geometry.ToString().ToLowerInvariant(), new SurfaceInterval(0, 0), none, 0, 0, 0, none, none,
                   0, 0, 0, null, null, buried, buried, null, 0);
    }

    // Triangles tagged not_drawn left out of a soup; kept[k] is the original index of kept triangle k.
    internal static (TriangleSoup Soup, string[] Tags, int[] Kept, int Dropped) DropNotDrawn(TriangleSoup s, string[] tags)
    {
        var kept = Enumerable.Range(0, s.TriangleCount).Where(t => tags[t] != SurfaceMaterial.NotDrawn).ToArray();
        var idx = new int[kept.Length * 3];
        var mats = s.TriangleMaterials is null ? null : new string?[kept.Length];
        var radii = s.TriangleRadii is null ? null : new float[kept.Length];
        for (int k = 0; k < kept.Length; k++)
        {
            int t = kept[k];
            idx[3 * k] = s.Indices[3 * t]; idx[3 * k + 1] = s.Indices[3 * t + 1]; idx[3 * k + 2] = s.Indices[3 * t + 2];
            if (mats is not null) mats[k] = s.TriangleMaterials![t];
            if (radii is not null) radii[k] = s.TriangleRadii![t];
        }
        return (new TriangleSoup(s.Vertices, idx, mats, radii), kept.Select(t => tags[t]).ToArray(), kept, s.TriangleCount - kept.Length);
    }

    /// <summary>The drawn view of a soup (ShapeDrawing): the soup itself when no triangle is tagged not_drawn, else the soup
    /// without them. The geometry-only steps of the swept tests read this view of both members.</summary>
    public static (TriangleSoup Soup, string[]? Tags) Drawn(TriangleSoup soup, string[]? tags)
    {
        if (tags is null || tags.Length != soup.TriangleCount || Array.IndexOf(tags, SurfaceMaterial.NotDrawn) < 0) return (soup, tags);
        var (s, t, _, _) = DropNotDrawn(soup, tags);
        return (s, t);
    }

    // kept triangle k -> its index in the placed soup (identity when nothing was skipped); the rule is SkipZeroArea's
    static int[] KeptIndex(TriangleSoup model, TriangleSoup placed, int skipped)
    {
        int n = placed.TriangleCount;
        var map = new List<int>(n - skipped);
        for (int t = 0; t < n; t++) if (skipped == 0 || !PairFrame.ZeroArea(model, t)) map.Add(t);
        return map.ToArray();
    }

    /// <summary>Read a NIF's statically visible render shapes (every material, tagged) or its solid collision as a
    /// model-space surface, under the given live-pose policy.</summary>
    public static TaggedSurface ModelSurface(byte[] nifBytes, VolumeGeometry geometry, LivePosePolicy pose = LivePoseDefault.Policy)
    {
        TaggedSurface Refuse(string reason) => new("refused", reason, null, null);
        if (geometry == VolumeGeometry.Collision)
        {
            var c = NifGeometry.Measure(nifBytes, includeTriangles: true);
            if (c.Model is null) return Refuse($"collision_unreadable: {c.Error}");
            if (!c.Model.HasSolidCollision) return Refuse("no_solid_collision");
            if (!c.Model.CollisionComplete) return Refuse("collision_incomplete: a solid body has an unread shape");
            var cs = c.Model.CollisionSoupFor(b => b.Solid);
            if (cs is not { TriangleCount: > 0 }) return Refuse("no_solid_collision");
            return new("measured", null, cs, Enumerable.Repeat(SurfaceMaterial.Collision, cs.TriangleCount).ToArray());
        }
        var raw = NifGeometry.MeasureVisibleRender(nifBytes, includeTriangles: true);
        if (raw.Model is not { } model) return Refuse($"render_unreadable: {raw.Error}");
        if (!model.RenderComplete) return Refuse("render_partial: " + string.Join("; ", model.RenderUnread!));
        // Particle systems carry no stored geometry (their particles are runtime state): they are set aside here, as the motion
        // reader sets them aside, and named in Labels; their reach is measured separately (lane 3b, gap e: box contact).
        var particles = model.Shapes.Where(s => !s.Hidden && IsParticleShape(s)).Select(s => s.Name).ToList();
        var visible = model.Shapes.Select((s, i) => (s, i)).Where(x => !x.s.Hidden && !IsParticleShape(x.s)).ToList();
        // Group 6 D2 (2026-10-05): a shape whose block was read and declares no vertices and no triangles draws nothing; it is read
        // as empty and named (EmptyShapes), not refused as unreadable. It still answers to the live-pose rule below (a skinned shape
        // keeps its geometry outside the shape block), and a shape with unreadable vertices is not empty.
        var empty = visible.Where(x => IsDeclaredEmpty(x.s)).Select(x => x.s.Name).ToList();
        IReadOnlyList<string>? labels = particles.Count == 0 ? null
            : particles.Select(n => $"particle system '{n}' set aside: its particles are runtime state, not stored geometry").ToList();
        if (visible.Count == 0) return Refuse("render: no visible shape" + (particles.Count > 0 ? $" (only particle systems: {string.Join(", ", particles.Select(n => $"'{n}'"))})" : ""));
        // Usage-week M4 (2026-10-14): a shape the game does not draw (ShapeDrawing: editor-marker geometry) is kept in the soup, so the
        // soup stays index-aligned with the stored render soup, and tagged not_drawn; the measures leave it out. Its pose and readability
        // do not decide the member.
        var notDrawn = visible.Where(x => ShapeDrawing.IsEditorMarker(x.s.Name)).Select(x => x.i).ToHashSet();
        if (notDrawn.Count > 0)
            labels = (labels ?? Array.Empty<string>()).Concat(visible.Where(x => notDrawn.Contains(x.i))
                .Select(x => $"shape '{x.s.Name}' not drawn in game (editor-marker geometry): kept, tagged {SurfaceMaterial.NotDrawn}, left out of every measure")).ToList();
        var drawnVisible = visible.Where(x => !notDrawn.Contains(x.i)).ToList();
        if (pose == LivePosePolicy.Blanket)
        {
            foreach (var (s, _) in drawnVisible)
                if (s.PoseIssue is not null) return Refuse($"live pose: {s.PoseIssue} (shape '{s.Name}')");
        }
        else
        {
            var causes = TargetedPoseCauses(nifBytes);
            if (causes is null) return Refuse("live pose: the NIF could not be re-read for its animation");
            foreach (var (_, i) in drawnVisible)
                if (causes.TryGetValue(i, out var why)) return Refuse("live pose: " + why);
        }
        foreach (var (s, _) in drawnVisible)
            if (!IsDeclaredEmpty(s) && (!s.VerticesRead || !s.TrianglesRead || s.InvalidTriangleCount > 0))
                return Refuse($"render_partial: shape '{s.Name}' has unreadable vertices or triangles");
        if (model.Soup is not { TriangleCount: > 0 } soup || model.SoupShapeOfTriangle is not { } map || map.Count != soup.TriangleCount)
            return Refuse("render: no triangles");
        var materials = MeshVolume.ShapeMaterials(nifBytes);
        if (materials is null || materials.Count != model.Shapes.Count) return Refuse("material: the NIF could not be re-read for its materials");
        var tags = new string[soup.TriangleCount];
        for (int t = 0; t < tags.Length; t++) tags[t] = notDrawn.Contains(map[t]) ? SurfaceMaterial.NotDrawn : materials[map[t]].Class;
        return new("measured", null, soup, tags, labels) { EmptyShapes = empty.Count == 0 ? null : empty };
    }

    static bool IsDeclaredEmpty(NifShapeGeometry s) =>
        s.VerticesRead && !s.TrianglesRead && s.VertexCount == 0 && s.TriangleCount == 0 && s.InvalidTriangleCount == 0 && s.Bounds.IsEmpty;

    /// <summary>A particle-system render block (NiParticleSystem, NiParticles, BSStripParticleSystem…), by its block type name,
    /// the test <see cref="NifMotion"/> applies to the same shapes.</summary>
    internal static bool IsParticleShape(NifShapeGeometry s) => s.BlockType.Contains("Particle", StringComparison.Ordinal);

    // R1, the targeted live-pose rule: per shape (GetShapes order), what the reader can see moving it.
    static Dictionary<int, string>? TargetedPoseCauses(byte[] bytes)
    {
        var nif = new NiflySharp.NifFile();
        using (var ms = new MemoryStream(bytes, writable: false))
            if (nif.Load(ms) != 0) return null;
        string? graph = nif.Blocks.OfType<NiflySharp.Blocks.BSBehaviorGraphExtraData>().Select(b => b.BehaviourGraphFile?.ToString() ?? "").FirstOrDefault();
        var animated = new Dictionary<string, (string Sequence, string Cycle)>(StringComparer.Ordinal);
        foreach (var seq in nif.Blocks.OfType<NiflySharp.Blocks.NiControllerSequence>())
            foreach (var cb in seq.ControlledBlocks ?? new())
            {
                string ctype = cb.ControllerType?.ToString() ?? "";
                int ci = cb.Controller?.Index ?? -1;
                string ctlBlock = ci >= 0 && ci < nif.Blocks.Count ? nif.Blocks[ci].GetType().Name : "";
                bool transform = ctype.Contains("Transform", StringComparison.Ordinal) || ctype.Contains("Keyframe", StringComparison.Ordinal)
                                 || (ctype.Length == 0 && (ctlBlock.Contains("Transform", StringComparison.Ordinal) || ctlBlock.Contains("Keyframe", StringComparison.Ordinal)));
                string node = cb.NodeName?.ToString() ?? "";
                if (transform && node.Length > 0 && !animated.ContainsKey(node))
                    animated[node] = (seq.Name?.ToString() ?? "", seq.CycleType.ToString());
            }
        var causes = new Dictionary<int, string>();
        int index = 0;
        foreach (var shape in nif.GetShapes())
        {
            int i = index++;
            string name = shape.Name?.String ?? $"#{i + 1}";
            if (shape.IsSkinned || shape.HasSkinInstance) { causes[i] = $"skinned shape '{name}'"; continue; }
            var chain = new List<NiflySharp.Blocks.NiAVObject>();
            var seen = new HashSet<object>(ReferenceEqualityComparer.Instance);
            for (var cur = shape as NiflySharp.Blocks.NiAVObject; cur is not null && seen.Add(cur) && chain.Count < 256; cur = nif.GetParentNode(cur)) chain.Add(cur);
            var sw = chain.FirstOrDefault(n => n.GetType().Name.Contains("SwitchNode", StringComparison.OrdinalIgnoreCase));
            if (sw is not null) { causes[i] = $"switch node '{sw.Name?.String}'"; continue; }
            var hit = chain.FirstOrDefault(n => n.Name?.String is { } nn && animated.ContainsKey(nn));
            if (hit is not null)
            {
                var (sq, cy) = animated[hit.Name!.String!];
                causes[i] = $"animated node '{hit.Name!.String}' (sequence '{sq}', {cy})"; continue;
            }
            string? direct = null;
            foreach (var n in chain)
            {
                int ci = n.Controller?.Index ?? -1, guard = 0;
                while (ci >= 0 && ci < nif.Blocks.Count && guard++ < 64)
                {
                    var block = nif.Blocks[ci];
                    if (block is NiflySharp.Blocks.NiTransformController || block.GetType().Name.Contains("KeyframeController", StringComparison.Ordinal))
                    { direct = n.Name?.String ?? ""; break; }
                    ci = block is NiflySharp.Blocks.NiTimeController tc ? (tc.NextController?.Index ?? -1) : -1;
                }
                if (direct is not null) break;
            }
            if (direct is not null) { causes[i] = $"transform controller on '{direct}'"; continue; }
            if (graph is not null) causes[i] = $"behaviour graph '{graph}'";
        }
        return causes;
    }

    /// <summary>The opaque x opaque crossing length as an interval for attribution: lower = decided opaque length minus
    /// the rounding bound; upper = decided opaque length plus every undecided edge length plus the rounding bound.</summary>
    public static SurfaceInterval? OpaqueCrossing(SurfaceContactResult result)
    {
        if (result.Status != "measured" || result.CrossingLengthByTags is null) return null;
        double op = result.CrossingLengthByTags.TryGetValue(SurfaceMaterial.Pair(SurfaceMaterial.Opaque, SurfaceMaterial.Opaque), out var v) ? v : 0;
        double rb = op > 0 ? result.RoundingBound : 0;
        return new SurfaceInterval(Math.Max(0, op - rb), op + (result.UndecidedEdgeLength ?? 0) + rb);
    }

    /// <summary>Four-setup attribution on the opaque x opaque crossing length, by <see cref="MeshVolume.Attribution"/>'s
    /// rule (an absent arm or member counts as zero). A combined arm that was not measured makes the answer
    /// <c>unresolved</c>, and so does another arm that was not measured while the combined crossing is not zero: a
    /// refusal is never read as zero.</summary>
    public static string Attribution(SurfaceContactResult combined, SurfaceContactResult? dependencyOnly,
                                     SurfaceContactResult? plusA, SurfaceContactResult? plusB)
    {
        var c = OpaqueCrossing(combined);
        if (c is null) return "unresolved";
        bool anyUnmeasured = false;
        VolumeInterval? Arm(SurfaceContactResult? r)
        {
            if (r is null) return null;
            var i = OpaqueCrossing(r);
            if (i is null) { anyUnmeasured = true; return null; }
            return new VolumeInterval(i.Lower, i.Upper);
        }
        var d = Arm(dependencyOnly); var a = Arm(plusA); var b = Arm(plusB);
        if (anyUnmeasured && c.Upper > 0) return "unresolved";
        return MeshVolume.Attribution(new VolumeInterval(c.Lower, c.Upper), d, a, b);
    }
}
