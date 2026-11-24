using System.Numerics;
using HousecarlCore;
using SkyWright.Engine;
using NiflySharp;
using NiflySharp.Blocks;
using NiflySharp.Structs;

/// <summary>The collision reader's node chain (Q28, 2026-09-05): <see cref="NifGeometry.WorldTransform"/> composes every
/// node's local transform from the root down to a shape — and must LEAVE THE ROOT'S OWN OUT. The engine places a mesh by
/// its reference and discards the root node's transform; the exporter leaves a quarter turn on many kit roots (299 vanilla
/// placed meshes), and with the root applied those meshes read a quarter turn off — the Creation Kit's baked bounds said so
/// from the records (Solitude statics: 78 of 110 root-turned centres off, the unturned controls matching) and a commanded
/// walker said so in the game (9 of 9 crossings of a line the turned copy cut). A turn BELOW the root is real and stays
/// (12 of 15 below-root centres match the CK). The proof cannot load a mesh from disk, so it authors the chain: a root
/// turned a quarter turn about Z and lifted 50 u, a child translated 100 u along +X, a shape with an identity transform —
/// saved and re-loaded so the parent links are what a file has.</summary>
static class NifChainProof
{
    public static void Run(Action<string, bool, string> Check)
    {
        // A quarter turn about Z in nifly's row-major Matrix33: R·(x, y, z) = (-y, x, z).
        static Matrix33 QuarterTurnZ() => new() { M11 = 0, M12 = -1, M13 = 0, M21 = 1, M22 = 0, M23 = 0, M31 = 0, M32 = 0, M33 = 1 };
        static bool Near(Vector3 a, Vector3 b) => (a - b).Length() < 1e-3f;

        static NifFile Author(bool turnRoot, bool turnChild)
        {
            var ver = new NiVersion { FileVersion = NiVersion.ToFile("20.2.0.7"), UserVersion = 12, StreamVersion = 100 };
            var f = new NifFile();
            f.Create(ver, withRootNode: true);
            var root = f.GetRootNodes().First();
            root.Name = new NiStringRef("ChainRoot");
            root.Flags_ui = 0xE;
            if (turnRoot) { root.Rotation = QuarterTurnZ(); root.Translation = new Vector3(0, 0, 50); }
            var child = new NiNode { Name = new NiStringRef("ChainChild"), Flags_ui = 0xE, Scale = 1f, Translation = new Vector3(100, 0, 0) };
            if (turnChild) child.Rotation = QuarterTurnZ();
            root.Children.AddBlockRef(f.AddBlock(child));
            var shape = new BSTriShape { Name = new NiStringRef("ChainShape"), Flags_ui = 0xE, Scale = 1f };
            child.Children.AddBlockRef(f.AddBlock(shape));
            using var ms = new MemoryStream();
            if (f.Save(ms) != 0) throw new InvalidOperationException("nif-chain proof: the synthetic chain failed to save");
            var back = new NifFile();
            if (back.Load(new MemoryStream(ms.ToArray())) != 0) throw new InvalidOperationException("nif-chain proof: the synthetic chain failed to load");
            return back;
        }

        // 1. The root turned and lifted: the shape sits at the child's translation, unturned and unlifted.
        {
            var nif = Author(turnRoot: true, turnChild: false);
            var shape = nif.GetShapes().First() as NiAVObject;
            Check("nif chain: the synthetic chain round-trips with root -> child -> shape", shape is not null && nif.GetParentNode(shape!) is { } p && nif.GetParentNode(p) is { } r && nif.GetParentNode(r) is null,
                  $"shape {(shape is null ? "missing" : "found")}");
            var xf = NifGeometry.WorldTransform(nif, shape);
            var p0 = xf.Apply(Vector3.Zero); var p1 = xf.Apply(new Vector3(10, 0, 0));
            Check("nif chain: the ROOT's own transform is left out — the shape's origin sits at the child's (100, 0, 0), not turned to (0, 100, 50)",
                  Near(p0, new Vector3(100, 0, 0)), $"origin -> {p0}");
            Check("nif chain: ...and a point along the shape's +X stays along +X (110, 0, 0), not turned to (0, 110, 50)",
                  Near(p1, new Vector3(110, 0, 0)), $"(10,0,0) -> {p1}");
        }
        // 2. The same turn one level DOWN, on the child: it applies — only the root's transform is dropped — and in the
        //    SENSE the engine reads a Matrix33: the stored rows act on a row vector, so a matrix whose first row is
        //    (0, -1, 0) and second (1, 0, 0) turns the child's +X onto -Y. (Measured 2026-09-05 against the Creation Kit's
        //    bounds of 10,834 meshes and an upright pine trunk; the transpose — +Y here — was the reader's sense until then.)
        {
            var nif = Author(turnRoot: false, turnChild: true);
            var shape = nif.GetShapes().First() as NiAVObject;
            var xf = NifGeometry.WorldTransform(nif, shape);
            var q0 = xf.Apply(Vector3.Zero); var q1 = xf.Apply(new Vector3(10, 0, 0));
            Check("nif chain: a turn BELOW the root still applies — the origin at the child's (100, 0, 0) and +X turned onto ±Y (100, ±10, 0)",
                  Near(q0, new Vector3(100, 0, 0)) && MathF.Abs(q1.X - 100) < 1e-3f && MathF.Abs(MathF.Abs(q1.Y) - 10) < 1e-3f && MathF.Abs(q1.Z) < 1e-3f,
                  $"origin -> {q0}, (10,0,0) -> {q1}");
            Check("nif chain: ...in the engine's SENSE — rows (0,-1,0) / (1,0,0) turn +X onto -Y (100, -10, 0), not +Y (the transpose the reader had until 2026-09-05)",
                  Near(q1, new Vector3(100, -10, 0)), $"(10,0,0) -> {q1}");
        }
        // 3. Both turned: the child's turn alone reaches the shape.
        {
            var nif = Author(turnRoot: true, turnChild: true);
            var shape = nif.GetShapes().First() as NiAVObject;
            var xf = NifGeometry.WorldTransform(nif, shape);
            var r1 = xf.Apply(new Vector3(10, 0, 0));
            Check("nif chain: root AND child turned — one quarter turn reaches the shape, not two (the root's would put +X back on -X)",
                  MathF.Abs(r1.X - 100) < 1e-3f && MathF.Abs(MathF.Abs(r1.Y) - 10) < 1e-3f, $"(10,0,0) -> {r1}");
        }
    }
}
