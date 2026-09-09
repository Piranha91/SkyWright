using System.Numerics;
using HousecarlCore;
using NiflySharp;
using NiflySharp.Blocks;
using NiflySharp.Structs;

static class NifBoundProof
{
    public static void Run(Action<string, bool, string> check)
    {
        static NifGeometryOutcome Read(float nodeScale = .5f, float radius = 17f,
                                       bool skew = false, bool duplicate = false, bool skinned = false,
                                       bool controlled = false, bool rootControlled = false)
        {
            var file = new NifFile();
            file.Create(new NiVersion { FileVersion = NiVersion.ToFile("20.2.0.7"),
                                        UserVersion = 12, StreamVersion = 100 }, withRootNode: true);
            var root = file.GetRootNodes().First();
            root.Scale = 7f;
            root.Translation = new(0, 0, 900);
            var parent = new NiNode { Scale = 2f, Translation = new(100, 0, 0),
                Rotation = new Matrix33 { M11=0, M12=-1, M13=0, M21=1, M22=0, M23=0,
                                          M31=0, M32=0, M33=skew ? 2 : 1 } };
            root.Children.AddBlockRef(file.AddBlock(parent));
            if (controlled) parent.Controller = new(file.AddBlock(new NiTransformController()));
            if (rootControlled) root.Controller = new(file.AddBlock(new NiTransformController()));
            for (int i=0; i < (duplicate ? 2 : 1); i++)
            {
                var shape = new BSTriShape { Name = new NiStringRef("same-name"), Scale = nodeScale,
                                            Translation = new(4, 0, 0) };
                shape.IsSkinned = skinned;
                var b = shape.Bounds;
                b.Center = new(2, 3, 5);
                b.Radius = radius;
                shape.Bounds = b;
                parent.Children.AddBlockRef(file.AddBlock(shape));
            }
            using var stream = new MemoryStream();
            if (file.Save(stream) != 0) throw new InvalidOperationException("bound fixture failed to save");
            return NifGeometry.Measure(stream.ToArray(), includeTriangles: false);
        }
        var read = Read();
        var shape = read.Model?.Shapes.Single();
        var bound = shape?.StoredBound;
        check("stored bound: raw file sphere survives independently of empty vertex geometry",
              bound is { StoredRadius: 17f } && bound.StoredCenter == new Vec3(2,3,5)
              && shape!.Bounds.IsEmpty, $"{bound}; {read.Error}");
        check("stored bound: child translation, rotation and both scales compose, root excluded",
              bound is not null && bound.ModelCenter == new Vec3(103,-10,5) && bound.ModelRadius == 17f,
              $"{bound}");
        check("stored bound: source block and complete below-root chain are retained",
              bound is not null && shape!.BlockIndex >= 0 && bound.AppliedNodeBlocks.Count == 2
              && bound.AppliedNodeBlocks[^1] == shape.BlockIndex
              && !bound.AppliedNodeBlocks.Contains(bound.IgnoredRootBlock), $"{bound}");
        var duplicate = Read(duplicate: true).Model!.Shapes;
        check("stored bound: duplicate names preserve distinct block identities",
              duplicate.Count == 2 && duplicate[0].Name == duplicate[1].Name
              && duplicate[0].BlockIndex != duplicate[1].BlockIndex, string.Join(",",duplicate.Select(s=>s.BlockIndex)));
        foreach (var (label, value) in new[] {("zero scale",Read(nodeScale:0)),
                                             ("skew",Read(skew:true)), ("negative radius",Read(radius:-1)),
                                             ("non-finite radius",Read(radius:float.PositiveInfinity))})
        {
            var invalid = value.Model?.Shapes.Single();
            check("stored bound: " + label + " is explicit unsupported coverage",
                  invalid is not null && invalid.StoredBound is null && !string.IsNullOrWhiteSpace(invalid.BoundIssue),
                  invalid?.BoundIssue ?? value.Error ?? "no issue recorded");
        }
        var mirrored = Read(nodeScale:-.5f).Model!.Shapes.Single().StoredBound;
        check("stored bound: negative uniform scale keeps a positive radius",
              mirrored is not null && mirrored.ModelRadius == 17f && mirrored.ModelCenter == new Vec3(97,-6,-5),
              $"{mirrored}");
        check("stored bound: rigid shape has no live-pose coverage issue", shape!.PoseIssue is null, shape.PoseIssue ?? "none");
        foreach (var (label, model) in new[] { ("skinned", Read(skinned: true)),
                    ("controlled child", Read(controlled: true)), ("controlled root", Read(rootControlled: true)) })
            check("stored bound: " + label + " retains file bounds but refuses live pose",
                model.Model?.Shapes.Single() is { StoredBound: not null, PoseIssue: not null },
                model.Model?.Shapes.Single().PoseIssue ?? "missing pose issue");
    }
}
