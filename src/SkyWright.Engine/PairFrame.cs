using HousecarlCore;

namespace SkyWright.Engine;

// PAIR FRAME (contactprobe pair-measure gaps, lane 3b, gap c). SpatialOverlap.TransformSoup places a model in single precision, and at
// city-scale world coordinates (about 1e5 u) a float step is 1/128 u: a sliver triangle that is sound in model space can collapse to
// zero area once placed (seen on real architecture meshes), and the exact predicates then refuse the whole pair. Lengths, areas and
// volumes do not depend on where the pair sits, so such a pair is measured in a frame centred on member B's placement (both members
// shifted by the same origin before rounding), and world-space outputs are shifted back. A pair whose placement collapses nothing keeps
// the world frame, byte for byte.
// Lane D (2026-10-10, review D1): what collapses a sliver is its distance from the frame origin, and a reference's origin can sit far
// from its own geometry (an interior shell's model is offset from its reference). When the frame centred on B still collapses a
// triangle, the frame centred on the pair's geometry (the centre of the overlap of the two placed world bounds, or the midpoint of their
// centres when they do not overlap) is tried, then the frame centred on A; a frame is taken only when neither member collapses in it.
//
// STATUS: implemented (red at 7bbbe518; lane D red at 2e8e464d, 6328ede1).

public static class PairFrame
{
    /// <summary>One member as placed: its model soup and its world pose.</summary>
    public readonly record struct Member(TriangleSoup Model, Vec3 Pos, Vec3 Rot, float Scale);

    /// <summary>A pair prepared by <see cref="PreparePlaced"/>: the soups to measure (in the frame shifted by <see cref="Origin"/>, with
    /// zero-area-in-model triangles left out), or the refusal naming why the pair cannot be measured.</summary>
    public sealed record Prepared(TriangleSoup A, TriangleSoup B, Vec3 Origin, int[]? ZeroAreaSkipped, string? Refusal)
    {
        /// <summary>The origin as written to output; null in the world frame.</summary>
        public double[]? Frame => IsWorld(Origin) ? null : [Origin.X, Origin.Y, Origin.Z];
    }

    /// <summary>The preparation the actual-shape relation and contact evidence share, by --surface's rule: null when neither placed soup
    /// has a zero-area triangle (or a member comes without its model, or a model's triangle count differs from its placed soup's), and
    /// the caller measures the world soups unchanged. Otherwise both models are re-placed in the frame <see cref="Origin"/> picks
    /// (the given world soups when that is the world frame), and each member's zero-area-in-model triangles are left out
    /// (<see cref="SkipZeroArea"/>, labels "a" and "b").</summary>
    /// <param name="anyZeroArea">Whether a world soup has a zero-area triangle; a caller that asks about one soup for many pairs
    /// can remember the answer (it depends on the soup alone). Null computes it.</param>
    public static Prepared? PreparePlaced(TriangleSoup worldA, Member? a, TriangleSoup worldB, Member? b, Func<TriangleSoup, bool>? anyZeroArea = null)
    {
        var zero = anyZeroArea ?? AnyZeroArea;
        if (a is not { } pa || b is not { } pb || pa.Model is null || pb.Model is null
            || pa.Model.TriangleCount != worldA.TriangleCount || pb.Model.TriangleCount != worldB.TriangleCount
            || (!zero(worldA) && !zero(worldB)))
            return null;
        var o = Origin(pa.Model, pa.Pos, pa.Rot, pa.Scale, pb.Model, pb.Pos, pb.Rot, pb.Scale);
        bool world = IsWorld(o);
        var fa = world ? worldA : SpatialOverlap.TransformSoup(pa.Model, Shift(pa.Pos, o), pa.Rot, pa.Scale);
        var fb = world ? worldB : SpatialOverlap.TransformSoup(pb.Model, Shift(pb.Pos, o), pb.Rot, pb.Scale);
        var (sa, _, na, whyA) = SkipZeroArea(pa.Model, fa, null, "a");
        var (sb, _, nb, whyB) = SkipZeroArea(pb.Model, fb, null, "b");
        return new(sa, sb, o, na + nb == 0 ? null : [na, nb], whyA ?? whyB);
    }

    /// <summary>A point or envelope corner measured in the frame shifted by <paramref name="origin"/>, back in world coordinates.</summary>
    public static double[]? ToWorld(double[]? v, Vec3 origin) => v is null ? null : [v[0] + origin.X, v[1] + origin.Y, v[2] + origin.Z];

    /// <summary>Whether any triangle of <paramref name="s"/> has zero area (<see cref="ZeroArea"/>).</summary>
    public static bool AnyZeroArea(TriangleSoup s)
    {
        for (int t = 0; t < s.TriangleCount; t++) if (ZeroArea(s, t)) return true;
        return false;
    }

    /// <summary>Group 6 D1's rule (2026-10-05), one home for --surface, the actual-shape relation and contact evidence: a triangle with
    /// zero area in its own model is left out (with its tag, material and radius) and counted; a triangle with model area that is zero
    /// where placed was collapsed by placement and refuses the member, naming the collapse; a member whose every triangle is zero-area
    /// refuses. Anything the measure refuses by name on its own (no model, empty, count or tag mismatch) passes through unchanged.</summary>
    public static (TriangleSoup Soup, string[]? Tags, int Skipped, string? Refusal) SkipZeroArea(TriangleSoup model, TriangleSoup placed, string[]? tags, string label)
    {
        int n = placed?.TriangleCount ?? 0;
        // anything Measure refuses by name on its own (empty, tag count) is passed through to it unchanged
        if (model is null || placed is null || n == 0 || model.TriangleCount != n || (tags is not null && tags.Length != n)) return (placed!, tags, 0, null);
        var skip = new bool[n];
        int skipped = 0, collapsed = 0;
        for (int t = 0; t < n; t++)
        {
            if (ZeroArea(model, t)) { skip[t] = true; skipped++; }
            else if (ZeroArea(placed, t)) collapsed++;
        }
        if (collapsed > 0)
            return (placed, tags, 0, $"{label}_degenerate_triangle: {collapsed} triangle(s) with model-space area have zero area where placed; centring the pair on either member or on the geometry does not undo it");
        if (skipped == 0) return (placed, tags, 0, null);
        if (skipped == n) return (placed, tags, 0, $"{label}_degenerate_triangle: every triangle has zero area ({skipped} in the model)");
        var idx = new int[(n - skipped) * 3];
        var kept = tags is null ? null : new string[n - skipped];
        var mats = placed.TriangleMaterials is null ? null : new string?[n - skipped];
        var radii = placed.TriangleRadii is null ? null : new float[n - skipped];
        for (int t = 0, k = 0; t < n; t++)
        {
            if (skip[t]) continue;
            idx[3 * k] = placed.Indices[3 * t]; idx[3 * k + 1] = placed.Indices[3 * t + 1]; idx[3 * k + 2] = placed.Indices[3 * t + 2];
            if (kept is not null) kept[k] = tags![t];
            if (mats is not null) mats[k] = placed.TriangleMaterials![t];
            if (radii is not null) radii[k] = placed.TriangleRadii![t];
            k++;
        }
        return (new TriangleSoup(placed.Vertices, idx, mats, radii), kept, skipped, null);
    }

    /// <summary>The origin to subtract from both members' positions: (0,0,0) unless placing either member at its world pose turns a
    /// triangle that has non-zero area in model space into a zero-area one; then the first of member B's position, the pair's geometry
    /// centre (<see cref="GeometryCentre"/>) and member A's position in which neither member collapses a triangle; if none, B's
    /// position (the measure then refuses, naming the collapse).</summary>
    public static Vec3 Origin(TriangleSoup modelA, Vec3 posA, Vec3 rotA, float scaleA, TriangleSoup modelB, Vec3 posB, Vec3 rotB, float scaleB)
    {
        var zero = new Vec3(0, 0, 0);
        if (!Collapses(modelA, posA, rotA, scaleA) && !Collapses(modelB, posB, rotB, scaleB)) return zero;
        var centre = GeometryCentre(modelA, posA, rotA, scaleA, modelB, posB, rotB, scaleB);
        foreach (var o in new[] { posB, centre, posA })
            if (!IsWorld(o) && !Collapses(modelA, Shift(posA, o), rotA, scaleA) && !Collapses(modelB, Shift(posB, o), rotB, scaleB)) return o;
        return posB;
    }

    /// <summary>The centre of the overlap of the two members' world bounds (each placed at its world pose), or the midpoint of the two
    /// bounds' centres when they do not overlap; computed in double, then to float. B's position when either member has no vertices.</summary>
    public static Vec3 GeometryCentre(TriangleSoup modelA, Vec3 posA, Vec3 rotA, float scaleA, TriangleSoup modelB, Vec3 posB, Vec3 rotB, float scaleB)
    {
        if (modelA is null || modelB is null || modelA.Vertices.Length == 0 || modelB.Vertices.Length == 0) return posB;
        var a = SpatialOverlap.SoupBounds(SpatialOverlap.TransformSoup(modelA, posA, rotA, scaleA));
        var b = SpatialOverlap.SoupBounds(SpatialOverlap.TransformSoup(modelB, posB, rotB, scaleB));
        double[] lo = [Math.Max(a.Min.X, b.Min.X), Math.Max(a.Min.Y, b.Min.Y), Math.Max(a.Min.Z, b.Min.Z)];
        double[] hi = [Math.Min(a.Max.X, b.Max.X), Math.Min(a.Max.Y, b.Max.Y), Math.Min(a.Max.Z, b.Max.Z)];
        if (lo[0] <= hi[0] && lo[1] <= hi[1] && lo[2] <= hi[2])
            return new((float)((lo[0] + hi[0]) / 2), (float)((lo[1] + hi[1]) / 2), (float)((lo[2] + hi[2]) / 2));
        return new((float)(((double)a.Min.X + a.Max.X + b.Min.X + b.Max.X) / 4), (float)(((double)a.Min.Y + a.Max.Y + b.Min.Y + b.Max.Y) / 4),
                   (float)(((double)a.Min.Z + a.Max.Z + b.Min.Z + b.Max.Z) / 4));
    }

    /// <summary>Shift a position by -origin (in double, then to float): the member's position in the pair frame.</summary>
    public static Vec3 Shift(Vec3 position, Vec3 origin) =>
        new((float)((double)position.X - origin.X), (float)((double)position.Y - origin.Y), (float)((double)position.Z - origin.Z));

    public static bool IsWorld(Vec3 origin) => origin.X == 0 && origin.Y == 0 && origin.Z == 0;

    /// <summary>Whether placing the model at its world pose turns a triangle of non-zero area (model space, exact double cross product of
    /// the float vertices) into one of zero area.</summary>
    public static bool Collapses(TriangleSoup model, Vec3 position, Vec3 rotation, float scale)
    {
        if (model is null || model.TriangleCount == 0) return false;
        var world = SpatialOverlap.TransformSoup(model, position, rotation, scale);
        for (int t = 0; t < model.TriangleCount; t++)
            if (!ZeroArea(model, t) && ZeroArea(world, t)) return true;
        return false;
    }

    /// <summary>Whether triangle t has zero area: a repeated index, or an exact double cross product of its float vertices of zero.
    /// An out-of-range index answers false (the measure refuses it by name).</summary>
    public static bool ZeroArea(TriangleSoup s, int t)
    {
        int a = s.Indices[3 * t], b = s.Indices[3 * t + 1], c = s.Indices[3 * t + 2];
        if ((uint)a >= (uint)s.Vertices.Length || (uint)b >= (uint)s.Vertices.Length || (uint)c >= (uint)s.Vertices.Length) return false;
        if (a == b || b == c || a == c) return true;
        var A = s.Vertices[a]; var B = s.Vertices[b]; var C = s.Vertices[c];
        double ux = (double)B.X - A.X, uy = (double)B.Y - A.Y, uz = (double)B.Z - A.Z, vx = (double)C.X - A.X, vy = (double)C.Y - A.Y, vz = (double)C.Z - A.Z;
        double nx = uy * vz - uz * vy, ny = uz * vx - ux * vz, nz = ux * vy - uy * vx;
        return nx * nx + ny * ny + nz * nz == 0;
    }
}
