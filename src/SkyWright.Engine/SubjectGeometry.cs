using System.Numerics;
using HousecarlCore;

namespace SkyWright.Engine;

// THE SUBJECT'S GEOMETRY for a distance to the navmesh: which shape of a placed reference the distance is measured from.
// One choice, called by floorgapprobe reach (the instrument) and strandprobe (the stranded check):
//   a base with a model: its visible render geometry at the stored pose, when the mesh is read, complete, and no visible
//     shape is posed at run time; otherwise the reference has no readable body (Unread), and the reason says why;
//   no model and a Box primitive (a trigger): the box at the reference's pose;
//   no model and no Box primitive (a light, a marker): its position, the anchor.
// What a caller does with an unread body is its own rule: the instrument measures from the position and says so; the
// check does not judge the reference.

/// <summary>The shape a distance is measured from. <paramref name="World"/> is null for an anchor (measure from
/// <paramref name="Anchor"/>) and for an unread body. <paramref name="Geometry"/> is "render (stored pose)", "primitive box"
/// or "position"; <paramref name="Unread"/> is the reason a model's body was not read, null otherwise; <paramref name="Note"/>
/// the instrument's sentence for a position used in place of a body or a primitive it does not measure.</summary>
public sealed record SubjectShape(TriangleSoup? World, Vector3 Anchor, string Geometry, string? Note, string? Unread);

public static class SubjectGeometry
{
    /// <summary>The shape of reference <paramref name="r"/>. <paramref name="modelPath"/> is its base's model path (null:
    /// no model); <paramref name="readMesh"/> returns the winning bytes of a model path, or null when it resolves nowhere.</summary>
    public static SubjectShape Of(PlacedRef r, string? modelPath, Func<string, byte[]?> readMesh)
    {
        var anchor = new Vector3(r.Position.X, r.Position.Y, r.Position.Z);
        if (modelPath is not null)
        {
            var bytes = readMesh(modelPath);
            if (bytes is null) return new(null, anchor, "position", "mesh unresolved: the position is used", "its model file could not be found");
            var g = NifGeometry.MeasureVisibleRender(bytes, includeTriangles: true);
            if (g?.Model is { Soup: { TriangleCount: > 0 } soup } m && m.RenderComplete && m.Shapes.Where(x => !x.Hidden).All(x => x.PoseIssue is null))
                return new(SpatialOverlap.TransformSoup(soup, r.Position, r.Rotation, r.Scale), anchor, "render (stored pose)", null, null);
            return new(null, anchor, "position", "render geometry partial or posed at runtime: the position is used",
                       g?.Model is null ? "its model could not be read" : !g.Model.RenderComplete ? "its model was read only in part" : "its shape is posed at run time");
        }
        if (r.Primitive is { } prim && NavmeshReach.PrimitiveBoxSoup(prim.Type, new Vector3(prim.Bounds.X, prim.Bounds.Y, prim.Bounds.Z)) is { } boxSoup)
            return new(SpatialOverlap.TransformSoup(boxSoup, r.Position, r.Rotation, r.Scale), anchor, "primitive box", null, null);
        return new(null, anchor, "position", r.Primitive is { } pr2 ? $"primitive {pr2.Type} is not a box: the position is used" : "no model: the position is used", null);
    }
}
