using System.Numerics;
using HousecarlCore;
using SkyWright.Engine;
using NiflySharp;
using NiflySharp.Blocks;
using NiflySharp.Structs;

static class OverlapGeometryProof
{
    static bool Near(double a, double b) => Math.Abs(a - b) <= 1e-6;
    static Aabb Box(float x0, float y0, float z0, float x1, float y1, float z1) =>
        new(new(x0, y0, z0), new(x1, y1, z1), false);

    static byte[] RenderFile(bool hidden = false, bool addUnreadable = false, bool controlled = false,
                             float radius = 9999f, float nodeScale = 1f, float maxX = 4f)
    {
        var f = new NifFile();
        var version = new NiVersion { FileVersion = NiVersion.ToFile("20.2.0.7"), UserVersion = 12, StreamVersion = 100 };
        f.Create(version, withRootNode: true);
        var root = f.GetRootNodes().First();
        var node = new NiNode { Scale = nodeScale };
        if (controlled) node.Controller = new(f.AddBlock(new NiTransformController()));
        root.Children.AddBlockRef(f.AddBlock(node));
        var shape = new BSTriShape { Name = new("render"), Flags_ui = hidden ? 1u : 0xEu, Scale = 1f };
        shape.Create(version,
                     new() { new Vector3(-1, -2, -3), new Vector3(maxX, -2, -3),
                             new Vector3(-1, 5, -3), new Vector3(-1, -2, 6) },
                     new() { new Triangle(0, 1, 2), new Triangle(0, 2, 3) }, new(), new());
        shape.IsSkinned = false;
        var stored = shape.Bounds; stored.Radius = radius; shape.Bounds = stored;
        node.Children.AddBlockRef(f.AddBlock(shape));
        if (addUnreadable)
        {
            var unreadable = new BSTriShape { Name = new("unsupported"), Flags_ui = 0xEu, Scale = 1f };
            unreadable.IsSkinned = false;
            node.Children.AddBlockRef(f.AddBlock(unreadable));
        }
        using var stream = new MemoryStream();
        if (f.Save(stream) != 0) throw new InvalidOperationException("overlap render fixture failed to save");
        return stream.ToArray();
    }

    public static void Run(Action<string, bool, string> check)
    {
        var unit = Box(0, 0, 0, 1, 1, 1);
        var identical = SpatialOverlap.MeasureAabbOverlap(unit, unit);
        check("overlap quantities: identical unit boxes have V=1, XY=1, fraction=1",
              Near(identical.IntersectionVolume, 1) && Near(identical.IntersectionAreaXy, 1)
              && Near(identical.FractionOfSmaller!.Value, 1), $"{identical}");

        var half = SpatialOverlap.MeasureAabbOverlap(unit, Box(.5f, .5f, .5f, 1.5f, 1.5f, 1.5f));
        check("overlap quantities: half offset on every axis has V=.125, XY=.25, fraction=.125",
              Near(half.IntersectionVolume, .125) && Near(half.IntersectionAreaXy, .25)
              && Near(half.FractionOfSmaller!.Value, .125), $"{half}");

        var vertical = SpatialOverlap.MeasureAabbOverlap(unit, Box(.25f, .25f, 2, .75f, .75f, 3));
        check("overlap quantities: vertical separation retains XY area but has zero volume and a named Z gap",
              Near(vertical.IntersectionAreaXy, .25) && Near(vertical.IntersectionVolume, 0)
              && vertical.ZRelation == "separated" && Near(vertical.ZGap, 1), $"{vertical}");

        var touching = SpatialOverlap.MeasureAabbOverlap(unit, Box(0, 0, 1, 1, 1, 2));
        check("overlap quantities: touching boxes have zero volume and Z relation touching",
              Near(touching.IntersectionVolume, 0) && touching.ZRelation == "touching", $"{touching}");

        var degenerate = SpatialOverlap.MeasureAabbOverlap(Box(0, 0, 0, 1, 1, 0), unit);
        check("overlap quantities: zero-volume denominator is null with reason, never numeric zero",
              degenerate.FractionOfSmaller is null && degenerate.FractionReason == "zero_volume", $"{degenerate}");

        var source = Box(-1, -2, -3, 4, 5, 6);
        var one = SpatialOverlap.PlaceBounds(source, default, default, 1);
        var two = SpatialOverlap.PlaceBounds(source, default, default, 2);
        var scaled = SpatialOverlap.MeasureAabbOverlap(two.Bounds!.Value, two.Bounds.Value);
        var baseMeasure = SpatialOverlap.MeasureAabbOverlap(one.Bounds!.Value, one.Bounds.Value);
        check("overlap quantities: global scale two multiplies XY area by four and volume by eight",
              Near(scaled.IntersectionAreaXy, 4 * baseMeasure.IntersectionAreaXy)
              && Near(scaled.IntersectionVolume, 8 * baseMeasure.IntersectionVolume),
              $"area {baseMeasure.IntersectionAreaXy}->{scaled.IntersectionAreaXy}; volume {baseMeasure.IntersectionVolume}->{scaled.IntersectionVolume}");
        var mirrored = SpatialOverlap.PlaceBounds(source, default, default, -1);
        var zero = SpatialOverlap.PlaceBounds(source, default, default, 0);
        var invalid = SpatialOverlap.PlaceBounds(source, new(float.NaN, 0, 0), default, 1);
        check("overlap placement: finite negative scale is supported as a mirror",
              mirrored.UsableForOverlap && mirrored.Bounds is { } mb && mb.Min == new Vec3(-4, -5, -6) && mb.Max == new Vec3(1, 2, 3), $"{mirrored}");
        check("overlap placement: zero scale retains finite degenerate bounds and is excluded",
              zero.Status == "degenerate_geometry" && zero.Bounds is { } zb && zb.Min == zb.Max, $"{zero}");
        check("overlap placement: non-finite reference pose is invalid with null bounds",
              invalid.Status == "invalid_transform" && invalid.Bounds is null, $"{invalid}");

        var bytes = RenderFile();
        var measured = NifOverlapGeometry.Measure(bytes, includeTriangles: true);
        check("overlap NIF: decoded render vertices, not the stored sphere, define complete bounds",
              measured.Status == NifRenderGeometryStatus.Measured && measured.ModelBounds == source
              && measured.Soup?.TriangleCount == 2, $"{measured.Status}: {measured.ModelBounds}; {measured.Reason}");
        var hidden = NifOverlapGeometry.Measure(RenderFile(hidden: true));
        var rawHidden = NifGeometry.Measure(RenderFile(hidden: true));
        check("overlap NIF: statically hidden render shapes are excluded by policy",
              hidden.Status == NifRenderGeometryStatus.MissingVertices && hidden.ModelBounds is null
              && rawHidden.Model is { Bounds.IsEmpty: false }, $"{hidden.Status}: {hidden.Reason}; raw {rawHidden.Model?.Bounds}");
        var partial = NifOverlapGeometry.Measure(RenderFile(addUnreadable: true));
        check("overlap NIF: one unreadable visible shape makes otherwise decoded bounds partial and unusable",
              partial.Status == NifRenderGeometryStatus.PartialGeometry && !partial.UsableForOverlap && partial.ModelBounds == source,
              $"{partial.Status}: {partial.Reason}");
        var animated = NifOverlapGeometry.Measure(RenderFile(controlled: true));
        check("overlap NIF: a controlled node is unsupported live pose, never complete static overlap geometry",
              animated.Status == NifRenderGeometryStatus.PartialGeometry && !animated.UsableForOverlap,
              $"{animated.Status}: {animated.Reason}");
        var collapsed = NifOverlapGeometry.Measure(RenderFile(nodeScale: 0));
        check("overlap NIF: zero node scale retains bounds as explicit degenerate geometry",
              collapsed.Status == NifRenderGeometryStatus.DegenerateGeometry && collapsed.ModelBounds is not null,
              $"{collapsed.Status}: {collapsed.ModelBounds}; {collapsed.Reason}");

        var cache = new NifRenderGeometryCache();
        var c1 = cache.Measure(bytes); var c2 = cache.Measure(bytes);
        var c3 = cache.Measure(RenderFile(radius: 1234));
        var c4 = cache.Measure(RenderFile(maxX: 8));
        check("overlap NIF cache: reuse requires identical consumed bytes, reader and policy identity",
              cache.Hits == 1 && cache.Misses == 3 && c1.CacheKey == c2.CacheKey && c1.CacheKey != c3.CacheKey
              && c4.CacheKey != c1.CacheKey && c1.ModelBounds?.Max.X == 4f && c4.ModelBounds?.Max.X == 8f
              && c1.CacheKey.Contains(NifOverlapGeometry.ReaderVersion) && c1.CacheKey.Contains(NifOverlapGeometry.PolicyVersion),
              $"hits {cache.Hits}, misses {cache.Misses}; same {c1.CacheKey == c2.CacheKey}, changed {c1.CacheKey != c3.CacheKey}; x {c1.ModelBounds?.Max.X}->{c4.ModelBounds?.Max.X}");
    }
}
