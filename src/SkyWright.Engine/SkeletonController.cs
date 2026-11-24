using NiflySharp;
using NiflySharp.Blocks;

using HousecarlCore;

namespace SkyWright.Engine;

/// <summary>A skeleton's authored controller, separate from its ragdoll rigid bodies. A generated controller
/// follows reference scale when the actor's 3D is built; a custom phantom shape does not. Unknown stays explicit.</summary>
public sealed record ControllerShapeFact(bool? ControllerCustom, string Why);

public static class SkeletonController
{
    public static ControllerShapeFact Inspect(byte[] bytes)
    {
        try
        {
            var nif = new NifFile();
            using var stream = new MemoryStream(bytes, writable: false);
            int result = nif.Load(stream);
            if (result != 0) return new(null, $"skeleton NIF load returned {result}");
            return Inspect(nif);
        }
        catch (Exception ex) { return new(null, $"skeleton read failed: {ex.Message}"); }
    }

    public static ControllerShapeFact Inspect(NifFile nif)
    {
        if (nif.HasUnknownBlocks) return new(null, "skeleton contains unread NIF blocks");
        if (nif.Blocks is null || !nif.GetRootNodes().Any()) return new(null, "skeleton has no root node");
        var custom = nif.Blocks.OfType<bhkSPCollisionObject>().ToList();
        if (custom.Count == 0)
        {
            if (nif.Blocks.OfType<bhkSimpleShapePhantom>().Any())
                return new(null, "skeleton has a shape phantom without its controller collision object");
            return new(false, "skeleton has no custom controller; generated at 3D build");
        }
        foreach (var c in custom)
            if (nif.GetBlock(c.Body) is not bhkSimpleShapePhantom ph || nif.GetBlock(ph.Shape) is not bhkShape)
                return new(null, "skeleton controller has an unread phantom or shape link");
        return new(true, "skeleton has bhkSPCollisionObject -> bhkSimpleShapePhantom -> shape");
    }
}
