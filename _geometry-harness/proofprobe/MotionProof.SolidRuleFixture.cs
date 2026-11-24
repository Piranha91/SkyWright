using HousecarlCore;
using SkyWright.Engine;
using NiflySharp;
using NiflySharp.Blocks;

// The solid-rule fixture (`proofprobe --write-motion-fixture DIR`, read by tests/test_solid_rule_named.py, which runs the real
// motionprobe and lightprobe on it). Test-only; no proof case reads it. Files, under DIR:
//   meshes/srfix/mover.nif    MoverA: a 10 u box sliding 100 u in x over sequence "Open", and the static "Frame" box
//   meshes/srfix/open.nif     the same slide with an open 10 u square for the box: the region builds and is not a solid
//   meshes/srfix/still.nif    the same rig with no animation (Build refuses: nothing to sweep)
//   meshes/srfix/lantern.nif  a closed cube of half extent 10 u at the origin, one lit shape (lighting shader)
//   meshes/srfix/wall.nif     the same, half extent 70 u
static partial class MotionProof
{
    static byte[] LitCube(float half)
    {
        var f = new NifFile(); f.Create(Ver, withRootNode: true);
        var root = f.GetRootNodes().First(); root.Flags_ui = 0xE;
        var shape = Shape("Cube", Cube(0, 0, 0, half));
        shape.ShaderPropertyRef = new NiBlockRef<BSShaderProperty>(f.AddBlock(new BSLightingShaderProperty()));
        root.Children.AddBlockRef(f.AddBlock(shape));
        return Save(f, "lit cube");
    }

    internal static int WriteSolidRuleFixture(string dir)
    {
        var d = Path.Combine(dir, "meshes", "srfix");
        Directory.CreateDirectory(d);
        var files = new (string Name, byte[] Bytes)[]
        {
            ("mover.nif", MoverA()),
            ("open.nif", Rig(new TriangleSoup(new System.Numerics.Vector3[] { new(0, 0, 0), new(10, 0, 0), new(10, 10, 0), new(0, 10, 0) },
                                              new[] { 0, 1, 2, 0, 2, 3 }), System.Numerics.Vector3.Zero, Slide(new(100, 0, 0)))),
            ("still.nif", Rig(Box(0, 0, 0, 10, 10, 10), System.Numerics.Vector3.Zero, null)),
            ("lantern.nif", LitCube(10)),
            ("wall.nif", LitCube(70)),
        };
        foreach (var (name, bytes) in files)
        {
            var p = Path.Combine(d, name);
            File.WriteAllBytes(p, bytes);
            Console.WriteLine(p);
        }
        return 0;
    }
}
