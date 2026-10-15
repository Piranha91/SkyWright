using NiflySharp;
using NiflySharp.Blocks;
using NiflySharp.Structs;

namespace HarnessShared;

// A synthetic NIF with Havok collision, written with NiflySharp: an SE file with a root node, and one collision body per
// Attach on its own child node. Test-only. Moved out of proofprobe's MaterialScaleProof (2026-10-15) so a probe's
// --self-test fixture can write the same files; proofprobe calls it unchanged.
public static class NifBoxFixture
{
    public static NifFile NewFile()
    {
        var f = new NifFile();
        f.Create(new NiVersion { FileVersion = NiVersion.ToFile("20.2.0.7"), UserVersion = 12, StreamVersion = 100 }, withRootNode: true);
        return f;
    }
    public static byte[] Save(NifFile f)
    {
        using var s = new MemoryStream();
        if (f.Save(s) != 0) throw new InvalidOperationException("material/controller fixture failed to save");
        return s.ToArray();
    }
    public static NifFile Load(byte[] bytes)
    {
        var f = new NifFile();
        if (f.Load(new MemoryStream(bytes)) != 0) throw new InvalidOperationException("material/controller fixture failed to load");
        return f;
    }
    public static void Attach(NifFile f, bhkShape shape, bool phantom = false)
    {
        bhkWorldObject body = phantom ? new bhkSimpleShapePhantom() : new bhkRigidBody();
        body.Shape = new NiBlockRef<bhkShape>(f.AddBlock(shape));
        bhkNiCollisionObject col = phantom ? new bhkSPCollisionObject() : new bhkCollisionObject();
        col.Body = new NiBlockRef<bhkWorldObject>(f.AddBlock(body));
        var root = f.GetRootNodes().First();
        // Each body gets its own attached child so the body-filter proof also verifies material re-indexing.
        var node = new NiNode { Scale = 1f };
        node.CollisionObject = new NiBlockRef<NiCollisionObject>(f.AddBlock(col));
        root.Children.AddBlockRef(f.AddBlock(node));
    }
}
