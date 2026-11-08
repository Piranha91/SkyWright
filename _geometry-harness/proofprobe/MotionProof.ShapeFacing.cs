using System.Numerics;
using HousecarlCore;
using NiflySharp;
using NiflySharp.Blocks;
using NiflySharp.Enums;

// What viewprobe's --inside-at facing reads off a shape (2026-11-08): whether its node chain mirrors it, which flips the
// winding so front cannot be told from back, and whether its shader draws both sides. Run alone with `--only facing`.
static partial class MotionProof
{
    static byte[] ShadedNif(SkyrimShaderPropertyFlags2 flags)
    {
        var f = new NifFile(); f.Create(Ver, withRootNode: true);
        var root = f.GetRootNodes().First(); root.Flags_ui = 0xE;
        var s = Shape("Plate", new TriangleSoup(new[] { new Vector3(2, 0, 0), new Vector3(0, 2, 0), new Vector3(0, 0, 2) }, new[] { 0, 1, 2 }));
        s.ShaderPropertyRef = new NiBlockRef<BSShaderProperty>(f.AddBlock(new BSLightingShaderProperty { ShaderFlags_SSPF2 = flags }));
        root.Children.AddBlockRef(f.AddBlock(s));
        return Save(f, "shaded plate");
    }

    public static void RunShapeFacing(Action<string, bool, string> check)
    {
        // SF-1: rows 0.5 x diag(1,1,-1) (K-A2's mirrored factor, determinant < 0): the shape is read as mirrored. Rejects a
        // reading that ignores the determinant, which would print a confident back or front from a flipped winding.
        Case(check, "SF-1: a mirrored node transform marks the shape mirrored", () =>
        {
            var o = NifGeometry.MeasureVisibleRender(MatrixNif(M33(0.5f, 0, 0, 0, 0.5f, 0, 0, 0, -0.5f)), includeTriangles: true);
            return o.Model is { } m ? (m.Shapes.Count == 1 && m.Shapes[0].Mirrored, $"mirrored {m.Shapes[0].Mirrored}") : (false, "refused: " + o.Error);
        });
        // SF-2 (guard): rows 0.5 x Rz(90) (K-A1, a proper rotation with a factor): not mirrored. Rejects marking every shape.
        Case(check, "SF-2 (guard): a proper rotation is not mirrored", () =>
        {
            var o = NifGeometry.MeasureVisibleRender(MatrixNif(M33(0, 0.5f, 0, -0.5f, 0, 0, 0, 0, 0.5f)), includeTriangles: true);
            return o.Model is { } m ? (m.Shapes.Count == 1 && !m.Shapes[0].Mirrored, $"mirrored {m.Shapes[0].Mirrored}") : (false, "refused: " + o.Error);
        });
        // SF-3: a lighting shader with Double_Sided: the shape is two-sided. SF-4 (guard): without it, one-sided.
        Case(check, "SF-3: a Double_Sided shader marks the shape two-sided", () =>
        {
            var o = NifGeometry.MeasureVisibleRender(ShadedNif(SkyrimShaderPropertyFlags2.Double_Sided), includeTriangles: true);
            return o.Model is { } m ? (m.Shapes.Count == 1 && m.Shapes[0].DoubleSided, $"two-sided {m.Shapes[0].DoubleSided}") : (false, "refused: " + o.Error);
        });
        Case(check, "SF-4 (guard): a shader without Double_Sided is one-sided", () =>
        {
            var o = NifGeometry.MeasureVisibleRender(ShadedNif(SkyrimShaderPropertyFlags2.ZBuffer_Write), includeTriangles: true);
            return o.Model is { } m ? (m.Shapes.Count == 1 && !m.Shapes[0].DoubleSided, $"two-sided {m.Shapes[0].DoubleSided}") : (false, "refused: " + o.Error);
        });
    }
}
