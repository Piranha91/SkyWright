using NiflySharp;
using NiflySharp.Blocks;

using HousecarlCore;

namespace SkyWright.Engine;

// MEASURED OVERLAP VOLUME (patch-miss packet, 2026-09-28): how much of placed object A lies inside placed object B, and
// of B inside A, for validated closed solids only.
//
// Design (GATE-A §5, §12; GATE-B §2; the implementation's two refinements are stated in the run's DESIGN-ADDENDA):
//  * A solid is the union of the welded components of a world-space triangle soup, each validated as a closed,
//    consistently oriented 2-manifold with positive enclosed volume (MeshIntersection's validator). A negatively
//    oriented (cavity) component, an open or invalid component, or a degenerate triangle refuses the object.
//  * Volume of a solid: exact signed-tetrahedron sum (validated components are pairwise disjoint).
//  * Intersection volume: a certified octree interval. Boundary cells (a triangle of either solid may meet the open cell;
//    the three box axes compared exactly, the other ten separating axes against the cell inflated by Inflate) subdivide;
//    other cells are wholly inside or outside each solid, decided at the centre by ray parity (an ambiguous answer makes
//    the cell boundary). A boundary cell whose candidate triangles of each solid lie exactly on one plane is measured by
//    clipping, each side's status read by ray parity, with a 1e-9 relative rounding margin. Lower bound = cells inside
//    both; upper = lower + leftover boundary cells. Converged when upper - lower <= max(RelativeTarget * min(vol A,
//    vol B), AbsoluteTarget). A midpoint is never reported alone.
//  * Material gate (ModelSolid, render geometry): alpha blend or alpha test, an effect shader, or a lighting shader that
//    does not write depth refuses the object; collision geometry has no material gate.
//  * Rounding: cell bounds are exact halvings of the float root box, but a cell's VOLUME (a product of three double
//    differences) and each running sum are rounded to double, so the certified bounds carry double rounding of order
//    1e-16 relative per cell on top of the stated clipping margin. The interval is therefore sound up to that relative
//    rounding, not in exact arithmetic; at the 1 % convergence target this is many orders of magnitude below the width.

public enum VolumeGeometry { Render, Collision }

public sealed record VolumeOptions(double RelativeTarget = 0.01, double AbsoluteTarget = 1.0, int MaxDepth = 14,
                                   long MaxCells = 4_000_000, double Inflate = 1e-4, long MaxTrianglePairs = 50_000_000);

/// <summary>A closed interval [Lower, Upper] guaranteed to contain the true value for the stated geometry.</summary>
public sealed record VolumeInterval(double Lower, double Upper)
{
    public bool Contains(double value, double tolerance = 0) => value >= Lower - tolerance && value <= Upper + tolerance;
    public double Width => Upper - Lower;
}

/// <summary>Status: <c>solid</c>, <c>refused</c> (Reason names the failing component or material), or <c>unknown</c>
/// (budget or cancellation). Soup is the accepted solid (model space for ModelSolid, as given for Solid).</summary>
public sealed record SolidResult(string Status, string? Reason, double? Volume, int Components, TriangleSoup? Soup);

/// <summary>Status: <c>measured</c>, <c>refused</c> (Reason names member A or B), or <c>unknown</c> (budget or
/// cancellation). A measured result that did not converge says so in Reason and keeps its interval.</summary>
public sealed record OverlapVolumeResult(string Status, string? Reason, string Geometry,
                                         double? VolumeA, double? VolumeB, VolumeInterval? Intersection,
                                         VolumeInterval? AInB, VolumeInterval? BInA, bool Converged, long Cells, int Depth)
{
    public string PolicyVersion => MeshVolume.PolicyVersion;
}

/// <summary>Status: <c>measured</c> (Inside holds the surface's part inside the solid, possibly empty), <c>refused</c>
/// (Reason names the surface or the solid), or <c>unknown</c> (budget or cancellation; Inside holds what was decided
/// before it stopped). Whole counts surface triangles wholly inside, Split those split along the solid's surface, and
/// Undecided the pieces the rays could not decide (left out).</summary>
public sealed record SurfaceInsideResult(string Status, string? Reason, TriangleSoup? Inside, int Whole, int Split, int Undecided);

public static class MeshVolume
{
    public const string PolicyVersion = "certified_octree_interval_v1";

    /// <summary>The part of <paramref name="surface"/> (any world-space soup, open or closed) that lies inside
    /// <paramref name="solid"/>, which is validated as <see cref="Solid"/> validates: the surface's triangles split along
    /// the solid's surface down to about <paramref name="resolution"/> units. For showing what one object buries in another;
    /// how much volume they share is <see cref="Measure"/>'s answer.</summary>
    public static SurfaceInsideResult SurfaceInside(TriangleSoup surface, TriangleSoup solid, double resolution,
                                                    long maxWork = 50_000_000, CancellationToken cancellationToken = default)
    {
        static SurfaceInsideResult Refuse(string status, string reason) => new(status, reason, null, 0, 0, 0);
        if (!(resolution > 0)) throw new ArgumentOutOfRangeException(nameof(resolution), resolution, "the resolution must be a positive length");
        if (surface is null || surface.TriangleCount == 0) return Refuse("refused", "surface: empty_mesh");
        if (solid is null || solid.TriangleCount == 0) return Refuse("refused", "solid: empty_mesh");
        var prep = MeshIntersection.PrepareVolumeSolid(solid, maxWork, cancellationToken);
        if (prep.Solid is null) return Refuse(prep.Unknown ? "unknown" : "refused", "solid: " + prep.Reason);
        MeshIntersection.InsideSurface r;
        try { r = MeshIntersection.SurfaceInside(surface, prep.Solid, resolution, maxWork, cancellationToken); }
        catch (OperationCanceledException) { return Refuse("unknown", "cancelled"); }
        if (r.Error is { } error) return Refuse("refused", error);
        var inside = new TriangleSoup(r.Pieces, Enumerable.Range(0, r.Pieces.Length).ToArray());
        return r.BudgetHit ? new("unknown", "work budget reached", inside, r.Whole, r.Split, r.Undecided)
                           : new("measured", null, inside, r.Whole, r.Split, r.Undecided);
    }

    /// <summary>Validate a world-space soup as a solid and measure its volume.</summary>
    public static SolidResult Solid(TriangleSoup world, VolumeOptions? options = null, CancellationToken cancellationToken = default)
    {
        options ??= new VolumeOptions();
        if (world is null || world.TriangleCount == 0) return new("refused", "empty_mesh", null, 0, null);
        var prep = MeshIntersection.PrepareVolumeSolid(world, options.MaxTrianglePairs, cancellationToken);
        if (prep.Solid is null) return new(prep.Unknown ? "unknown" : "refused", prep.Reason, null, 0, null);
        return new("solid", null, prep.Solid.Volume, prep.Solid.Components, world);
    }

    /// <summary>Intersection volume of two world-space solids, and each one's fraction inside the other.</summary>
    public static OverlapVolumeResult Measure(TriangleSoup a, TriangleSoup b, VolumeGeometry geometry,
                                              VolumeOptions? options = null, CancellationToken cancellationToken = default)
    {
        options ??= new VolumeOptions();
        string g = geometry.ToString().ToLowerInvariant();
        OverlapVolumeResult Refuse(string status, string reason) => new(status, reason, g, null, null, null, null, null, false, 0, 0);
        if (a is null || a.TriangleCount == 0) return Refuse("refused", "A: empty_mesh");
        if (b is null || b.TriangleCount == 0) return Refuse("refused", "B: empty_mesh");
        var pa = MeshIntersection.PrepareVolumeSolid(a, options.MaxTrianglePairs, cancellationToken);
        if (pa.Solid is null) return Refuse(pa.Unknown ? "unknown" : "refused", "A: " + pa.Reason);
        var pb = MeshIntersection.PrepareVolumeSolid(b, options.MaxTrianglePairs, cancellationToken);
        if (pb.Solid is null) return Refuse(pb.Unknown ? "unknown" : "refused", "B: " + pb.Reason);
        double va = pa.Solid.Volume, vb = pb.Solid.Volume;
        double target = Math.Max(options.RelativeTarget * Math.Min(va, vb), options.AbsoluteTarget);
        MeshIntersection.OctreeInterval o;
        try { o = MeshIntersection.IntersectionInterval(pa.Solid, pb.Solid, target, options.MaxDepth, options.MaxCells, options.Inflate, cancellationToken); }
        catch (OperationCanceledException) { return new("unknown", "cancelled", g, va, vb, null, null, null, false, 0, 0); }
        var inter = new VolumeInterval(o.Lower, o.Upper);
        static VolumeInterval Frac(VolumeInterval i, double v) => new(Math.Clamp(i.Lower / v, 0, 1), Math.Clamp(i.Upper / v, 0, 1));
        string? reason = o.Converged ? null : o.BudgetHit ? "bounded, not converged: cell or depth budget reached" : "bounded, not converged";
        return new("measured", reason, g, va, vb, inter, Frac(inter, va), Frac(inter, vb), o.Converged, o.Cells, o.Depth);
    }

    /// <summary>Two model-space solids placed by their own references' transforms (the production placement,
    /// <see cref="SpatialOverlap.TransformSoup"/>), then measured. The probes' volume pass calls this, so the proof's
    /// placed cases and a real pair go through the same transform code.</summary>
    public static OverlapVolumeResult MeasurePlaced(TriangleSoup modelA, Vec3 posA, Vec3 rotA, float scaleA,
                                                    TriangleSoup modelB, Vec3 posB, Vec3 rotB, float scaleB, VolumeGeometry geometry,
                                                    VolumeOptions? options = null, CancellationToken cancellationToken = default)
        => MeasurePlacedFramed(modelA, posA, rotA, scaleA, modelB, posB, rotB, scaleB, geometry, options, cancellationToken).Result;

    /// <summary><see cref="MeasurePlaced"/> with the frame it was measured in (<see cref="PairFrame"/>): (0,0,0) is the world frame;
    /// otherwise world placement collapsed a triangle and the pair was measured shifted by -Origin (volumes do not depend on it).</summary>
    public static (OverlapVolumeResult Result, Vec3 Origin) MeasurePlacedFramed(TriangleSoup modelA, Vec3 posA, Vec3 rotA, float scaleA,
                                                    TriangleSoup modelB, Vec3 posB, Vec3 rotB, float scaleB, VolumeGeometry geometry,
                                                    VolumeOptions? options = null, CancellationToken cancellationToken = default)
    {
        var o = PairFrame.Origin(modelA, posA, rotA, scaleA, modelB, posB, rotB, scaleB);
        if (PairFrame.IsWorld(o))
            return (Measure(SpatialOverlap.TransformSoup(modelA, posA, rotA, scaleA), SpatialOverlap.TransformSoup(modelB, posB, rotB, scaleB),
                            geometry, options, cancellationToken), o);
        return (Measure(SpatialOverlap.TransformSoup(modelA, PairFrame.Shift(posA, o), rotA, scaleA),
                        SpatialOverlap.TransformSoup(modelB, PairFrame.Shift(posB, o), rotB, scaleB), geometry, options, cancellationToken), o);
    }

    /// <summary>Read a NIF's render or solid-collision geometry as a model-space solid (node chain applied below the
    /// root, the root's own transform left out), with the material gate for render geometry.</summary>
    public static SolidResult ModelSolid(byte[] nifBytes, VolumeGeometry geometry)
    {
        if (geometry == VolumeGeometry.Render)
        {
            var m = NifOverlapGeometry.Measure(nifBytes, includeTriangles: true);
            if (m.Status != NifRenderGeometryStatus.Measured || m.Soup is not { TriangleCount: > 0 } soup)
                return new("refused", $"render_geometry_{m.Status}: {m.Reason}", null, 0, null);
            if (MaterialRefusal(nifBytes) is { } why) return new("refused", why, null, 0, null);
            return Solid(soup);
        }
        var c = NifGeometry.Measure(nifBytes, includeTriangles: true);
        if (c.Model is null) return new("refused", $"collision_unreadable: {c.Error}", null, 0, null);
        if (!c.Model.HasSolidCollision) return new("refused", "no_solid_collision", null, 0, null);
        if (!c.Model.CollisionComplete) return new("refused", "collision_incomplete: a solid body has an unread shape", null, 0, null);
        var cs = c.Model.CollisionSoupFor(body => body.Solid);
        if (cs is not { TriangleCount: > 0 }) return new("refused", "no_solid_collision", null, 0, null);
        return Solid(cs);
    }

    /// <summary>The render material gate: a statically visible shape with alpha blend or alpha test, an effect shader, or a
    /// lighting shader that does not write depth refuses the object (the shape is named). Null when every visible shape
    /// is opaque and depth-writing, or states no shader.</summary>
    static string? MaterialRefusal(byte[] bytes)
    {
        var classes = ShapeMaterials(bytes);
        if (classes is null) return "material: the NIF could not be re-read for its materials";
        foreach (var (name, hidden, cls) in classes)
        {
            if (hidden || cls == SurfaceMaterial.Opaque) continue;
            return cls switch
            {
                SurfaceMaterial.AlphaBlend => $"material: alpha blend on shape '{name}'",
                SurfaceMaterial.AlphaTest => $"material: alpha test on shape '{name}'",
                SurfaceMaterial.Effect => $"material: effect shader on shape '{name}'",
                _ => $"material: lighting shader without depth write on shape '{name}'",
            };
        }
        return null;
    }

    /// <summary>The material class of every render shape, in <c>NifFile.GetShapes</c> order (the order
    /// <see cref="NifGeometry"/> reads them in): alpha blend, then alpha test, then an effect shader, then a lighting
    /// shader that does not write depth; otherwise opaque. One classifier for the volume's material gate and the surface
    /// measure's tags. Null when the NIF cannot be re-read.</summary>
    internal static List<(string Name, bool Hidden, string Class)>? ShapeMaterials(byte[] bytes)
    {
        var nif = new NifFile();
        using (var ms = new MemoryStream(bytes, writable: false))
            if (nif.Load(ms) != 0) return null;
        var list = new List<(string, bool, string)>();
        int index = 0;
        foreach (var shape in nif.GetShapes())
        {
            index++;
            bool hidden = false;
            for (NiAVObject? cur = shape as NiAVObject; cur is not null; cur = nif.GetParentNode(cur))
                if (((cur.Flags_ui | cur.Flags_us) & 1) != 0) { hidden = true; break; }
            string name = shape.Name?.String ?? $"#{index}";
            string cls = SurfaceMaterial.Opaque;
            var shader = nif.GetShader(shape);
            if (shape.HasAlphaProperty && nif.GetBlock<NiAlphaProperty>(shape.AlphaPropertyRef) is { } ap && (ap.Flags.AlphaBlend || ap.Flags.AlphaTest))
                cls = ap.Flags.AlphaBlend ? SurfaceMaterial.AlphaBlend : SurfaceMaterial.AlphaTest;
            else if (shader is BSEffectShaderProperty) cls = SurfaceMaterial.Effect;
            else if (shader is BSLightingShaderProperty ls && !ls.ShaderFlags_SSPF2.HasFlag(NiflySharp.Enums.SkyrimShaderPropertyFlags2.ZBuffer_Write))
                cls = SurfaceMaterial.NoDepthWrite;
            list.Add((name, hidden, cls));
        }
        return list;
    }

    /// <summary>Four-setup attribution of an intersection: <c>no_overlap</c> when the combined upper bound is zero;
    /// <c>needs_both</c> when the combined arm's lower bound exceeds the upper bound of every single-mod and
    /// dependency-only arm (an absent arm or member counts as zero); <c>present_without_both</c> when a single-mod or
    /// dependency arm's lower bound is positive; else <c>unresolved</c>.</summary>
    public static string Attribution(VolumeInterval combined, VolumeInterval? dependencyOnly, VolumeInterval? plusA, VolumeInterval? plusB)
    {
        if (combined.Upper <= 0) return "no_overlap";
        var others = new[] { dependencyOnly, plusA, plusB };
        double maxUpper = others.Max(o => o?.Upper ?? 0);
        if (combined.Lower > maxUpper) return "needs_both";
        if (others.Any(o => (o?.Lower ?? 0) > 0)) return "present_without_both";
        return "unresolved";
    }
}
