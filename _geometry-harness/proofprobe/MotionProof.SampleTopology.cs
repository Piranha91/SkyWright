// Known answers for sharing identical index and tag arrays across a Build's samples (task
// 2026-10-07-overlap-scene-memory-fit). Exercises the actual Build and the unchanged public Pose, then the
// anchor, contact, volume and box consumers on shared samples.
using System.Numerics;
using HousecarlCore;
using NiflySharp.Enums;

static partial class MotionProof
{
    static bool TopologyFloat(float a, float b) => BitConverter.SingleToInt32Bits(a) == BitConverter.SingleToInt32Bits(b);
    static bool TopologyDouble(double a, double b) => BitConverter.DoubleToInt64Bits(a) == BitConverter.DoubleToInt64Bits(b);
    static bool TopologyVertices(Vector3[] a, Vector3[] b) => a.Length == b.Length && a.Zip(b).All(p =>
        TopologyFloat(p.First.X, p.Second.X) && TopologyFloat(p.First.Y, p.Second.Y) && TopologyFloat(p.First.Z, p.Second.Z));
    static bool TopologyStrings(string?[]? a, string?[]? b) => a is null || b is null ? a is null && b is null :
        a.Length == b.Length && a.Zip(b).All(p => string.Equals(p.First, p.Second, StringComparison.Ordinal));
    static bool TopologySoup(TriangleSoup a, TriangleSoup b) => TopologyVertices(a.Vertices, b.Vertices)
        && a.Indices.SequenceEqual(b.Indices) && TopologyStrings(a.TriangleMaterials, b.TriangleMaterials)
        && (a.TriangleRadii is null || b.TriangleRadii is null ? a.TriangleRadii is null && b.TriangleRadii is null :
            a.TriangleRadii.Length == b.TriangleRadii.Length && a.TriangleRadii.Zip(b.TriangleRadii).All(p => TopologyFloat(p.First, p.Second)));
    static bool TopologyRegion(SweptRegion a, SweptRegion b) => a.Status == b.Status && a.Reason == b.Reason && a.Geometry == b.Geometry
        && TopologyDouble(a.MaxDelta, b.MaxDelta) && a.Converged == b.Converged && a.Solid == b.Solid
        && a.Animations.Count == b.Animations.Count && a.Animations.Zip(b.Animations).All(p =>
            p.First.Id == p.Second.Id && p.First.Source == p.Second.Source && p.First.Name == p.Second.Name && p.First.Cycle == p.Second.Cycle
            && TopologyFloat(p.First.Start, p.Second.Start) && TopologyFloat(p.First.End, p.Second.End)
            && p.First.ClipPath == p.Second.ClipPath && p.First.Trigger == p.Second.Trigger)
        && a.Samples.Count == b.Samples.Count && a.Samples.Zip(b.Samples).All(p =>
            p.First.AnimationId == p.Second.AnimationId && TopologyFloat(p.First.Time, p.Second.Time)
            && TopologyDouble(p.First.Delta, p.Second.Delta) && TopologySoup(p.First.Soup, p.Second.Soup) && TopologyStrings(p.First.Tags, p.Second.Tags));
    static bool TopologyShared(SweptRegion region) => region.Status == "built" && region.Samples.Count > 1
        && region.Samples.Skip(1).All(s => ReferenceEquals(s.Soup.Indices, region.Samples[0].Soup.Indices)
            && ReferenceEquals(s.Tags, region.Samples[0].Tags) && !ReferenceEquals(s.Soup.Vertices, region.Samples[0].Soup.Vertices))
        && region.Samples.Select(s => s.Soup.Vertices).Distinct(ReferenceEqualityComparer.Instance).Count() == region.Samples.Count;

    public static void RunSampleTopology(Action<string, bool, string> check)
    {
        TriangleSoup Soup(float x, int[]? ix = null) => new(
            [new Vector3(x, 0, 0), new Vector3(x + 1, 0, 0), new Vector3(x, 1, 0)],
            ix ?? [0, 1, 2], ["material"], [0.375f]);
        Case(check, "topology T1: exact values share indices/tags; vertices/materials/radii preserve identity", () =>
        {
            var helper = new SweptGeometry.SampleTopology(); var a = Soup(0); var b = Soup(10);
            string[] ta = ["opaque"], tb = [new string("opaque".ToCharArray())];
            var first = helper.Share(a, ta); var actual = helper.Share(b, tb);
            bool ok = ReferenceEquals(first.Soup, a) && ReferenceEquals(actual.Soup.Indices, a.Indices) && ReferenceEquals(actual.Tags, ta)
                && ReferenceEquals(actual.Soup.Vertices, b.Vertices) && ReferenceEquals(actual.Soup.TriangleMaterials, b.TriangleMaterials)
                && ReferenceEquals(actual.Soup.TriangleRadii, b.TriangleRadii) && !ReferenceEquals(actual.Soup.Vertices, a.Vertices)
                && b.Indices.SequenceEqual([0, 1, 2]) && tb.SequenceEqual(["opaque"]);
            return (ok, "derived topology shared; other fields/source arrays preserved");
        });
        Case(check, "topology T2: index value/order/length and tag ordinal/order/length differences preserve arrays", () =>
        {
            var helper = new SweptGeometry.SampleTopology(); var first = Soup(0); string[] firstTags = ["opaque", "alpha"];
            helper.Share(first, firstTags); bool ok = true;
            foreach (int[] indices in new int[][] { [0, 2, 1], [0, 1, 1], [0, 1, 2, 0, 2, 1] })
            {
                var soup = Soup(1, indices); var actual = helper.Share(soup, firstTags);
                ok &= ReferenceEquals(actual.Soup, soup) && ReferenceEquals(actual.Soup.Indices, indices);
            }
            foreach (string[] tags in new string[][] { ["Opaque", "alpha"], ["alpha", "opaque"], ["opaque"], ["opaque", "alpha", "opaque"], ["opaque", null!] })
                ok &= ReferenceEquals(helper.Share(Soup(2), tags).Tags, tags);
            return (ok, "each full-content mismatch keeps its own arrays");
        });
        Case(check, "topology T3: null/empty tags and runtime-null/empty indices never coalesce", () =>
        {
            var absent = new SweptGeometry.SampleTopology(); absent.Share(Soup(0), null);
            string[] empty = []; var present = absent.Share(Soup(1), empty); var nullAgain = absent.Share(Soup(2), null);
            var other = new SweptGeometry.SampleTopology(); other.Share(Soup(3), empty); var missing = other.Share(Soup(4), null);
            var invalid = new SweptGeometry.SampleTopology(); invalid.Share(new TriangleSoup([], null!), null);
            int[] emptyIndices = []; var valid = invalid.Share(new TriangleSoup([], emptyIndices), null);
            var validFirst = new SweptGeometry.SampleTopology(); validFirst.Share(new TriangleSoup([], emptyIndices), null);
            var invalidSoup = new TriangleSoup([], null!); var invalidLater = validFirst.Share(invalidSoup, null);
            return (ReferenceEquals(present.Tags, empty) && nullAgain.Tags is null && missing.Tags is null
                && ReferenceEquals(valid.Soup.Indices, emptyIndices) && ReferenceEquals(invalidLater.Soup, invalidSoup)
                && invalidLater.Soup.Indices is null, "availability preserved, including runtime-invalid original soup");
        });
        Case(check, "topology T4: first template persists across misses and belongs to one Build", () =>
        {
            var one = new SweptGeometry.SampleTopology(); var two = new SweptGeometry.SampleTopology();
            var a = Soup(0); var b = Soup(1); string[] ta = ["opaque"], tb = ["opaque"];
            one.Share(a, ta); two.Share(b, tb);
            for (int i = 0; i < 32; i++) one.Share(Soup(i + 2, [0, 2, 1]), ["alpha"]);
            var lastOne = one.Share(Soup(100), ["opaque"]); var lastTwo = two.Share(Soup(101), ["opaque"]);
            return (ReferenceEquals(lastOne.Soup.Indices, a.Indices) && ReferenceEquals(lastOne.Tags, ta)
                && ReferenceEquals(lastTwo.Soup.Indices, b.Indices) && ReferenceEquals(lastTwo.Tags, tb)
                && !ReferenceEquals(lastOne.Soup.Indices, lastTwo.Soup.Indices) && !ReferenceEquals(lastOne.Tags, lastTwo.Tags),
                "one template per invocation, no replacement/global storage");
        });

        byte[] collision = Rig(Box(0, -1, 0, 100, 1, 200), Vector3.Zero,
            Xyz(NoKeys(), NoKeys(), FG(KeyType.LINEAR_KEY, FK(0, 0), FK(1, Pi / 2))),
            extra: (f, _) => AddBody(f, f.Blocks.OfType<NiflySharp.Blocks.NiNode>().Single(n => n.Name?.String == "Mover"),
                ConvexBox(0, -1, 0, 100, 1, 200), 0f, hkMotionType.MO_SYS_BOX_STABILIZED, SkyrimLayer.SKYL_STATIC));
        // Expected populations are fixed by the synthetic writers: two 8-vertex boxes
        // (24 triangles), one 8-vertex strip (6 triangles), one convex box (12 triangles).
        // Direct Pose is an unchanged production entry point; no copied Build oracle.
        var fixtures = new (string Name, byte[] Bytes, VolumeGeometry Geometry, int Vertices, int Indices, string Tag, bool Solid)[]
        {
            ("rigid", DoorNif(), VolumeGeometry.Render, 16, 72, SurfaceMaterial.Opaque, true),
            ("skin", SkinRig(Pi / 2, ssePermuted: true), VolumeGeometry.Render, 8, 18, SurfaceMaterial.Opaque, false),
            ("collision", collision, VolumeGeometry.Collision, 8, 36, SurfaceMaterial.Collision, true)
        };
        var expectedAnimation = new MotionAnimation("seq:Open", "nif-sequence", "Open", "CYCLE_CLAMP", 0f, 1f, null,
            "trigger: not settled by the files");
        foreach (var fixture in fixtures)
        {
            Case(check, "topology T5 " + fixture.Name + ": actual Build preserves public Pose bits, fixed populations and isolated templates", () =>
            {
                var bytesBefore = fixture.Bytes.ToArray(); var model = NifMotion.Read(fixture.Bytes, null);
                bool ExpectedFields(SweptRegion region, bool coarse)
                {
                    if (region.Status != "built" || region.Reason is not null || region.Geometry != fixture.Geometry
                        || region.Solid != fixture.Solid || region.Animations.Count != 1 || region.Animations[0] != expectedAnimation
                        || !TopologyFloat(region.Animations[0].Start, 0f) || !TopologyFloat(region.Animations[0].End, 1f)
                        || region.Samples.Count < 2 || region.Samples.Count > 64 || !TopologyShared(region)
                        || !TopologyFloat(region.Samples[0].Time, 0f) || !TopologyFloat(region.Samples[^1].Time, 1f)
                        || !TopologyDouble(region.Samples[^1].Delta, 0.0)
                        || !TopologyDouble(region.MaxDelta, region.Samples.Max(s => s.Delta))) return false;
                    if (coarse && (region.Samples.Count != 2 || !region.Converged || region.Samples[0].Delta <= 0)) return false;
                    for (int i = 0; i < region.Samples.Count; i++)
                    {
                        var sample = region.Samples[i];
                        if (sample.AnimationId != "seq:Open" || !double.IsFinite(sample.Delta) || sample.Delta < 0
                            || (i > 0 && sample.Time <= region.Samples[i - 1].Time)
                            || sample.Soup.Vertices.Length != fixture.Vertices || sample.Soup.Indices.Length != fixture.Indices
                            || sample.Soup.TriangleMaterials is not null || sample.Soup.TriangleRadii is not null
                            || sample.Tags is null || sample.Tags.Length != fixture.Indices / 3
                            || sample.Tags.Any(t => t != fixture.Tag)) return false;
                        var pose = NifMotion.Pose(fixture.Bytes, model, sample.AnimationId, sample.Time, fixture.Geometry);
                        if (pose.Status != "posed" || pose.Reason is not null || pose.Soup is null
                            || pose.AnimationId != sample.AnimationId || !TopologyFloat(pose.Time, sample.Time)
                            || !TopologySoup(pose.Soup, sample.Soup) || !TopologyStrings(pose.Tags, sample.Tags)) return false;
                    }
                    return true;
                }
                var coarse = SweptGeometry.Build(fixture.Bytes, model, null, fixture.Geometry,
                    new MotionOptions(DeltaTarget: 1_000_000, SampleBudget: 64));
                var options = new MotionOptions(DeltaTarget: 2, SampleBudget: 64);
                var actual = SweptGeometry.Build(fixture.Bytes, model, null, fixture.Geometry, options);
                var repeated = SweptGeometry.Build(fixture.Bytes, model, null, fixture.Geometry, options);
                return (ExpectedFields(coarse, true) && ExpectedFields(actual, false) && ExpectedFields(repeated, false)
                    && TopologyRegion(actual, repeated)
                    && !ReferenceEquals(coarse.Samples[0].Soup.Indices, actual.Samples[0].Soup.Indices)
                    && !ReferenceEquals(actual.Samples[0].Soup.Indices, repeated.Samples[0].Soup.Indices)
                    && !ReferenceEquals(actual.Samples[0].Tags, repeated.Samples[0].Tags)
                    && fixture.Bytes.SequenceEqual(bytesBefore), $"{actual.Status} samples {actual.Samples.Count}; direct Pose bits and bytes preserved");
            });
        }
        Case(check, "topology T6: refusal and empty Build preserve every explicitly expected region field", () =>
        {
            var bytes = DoorNif(); var bytesBefore = bytes.ToArray(); var model = NifMotion.Read(bytes, null);
            bool ExpectedRefusal(SweptRegion actual, MotionModel input, VolumeGeometry geometry, string reason) =>
                actual.Status == "refused" && actual.Reason == reason && actual.Geometry == geometry
                && ReferenceEquals(actual.Animations, input.Animations) && actual.Animations.SequenceEqual(input.Animations)
                && actual.Samples.Count == 0 && TopologyDouble(actual.MaxDelta, 0.0) && !actual.Converged && !actual.Solid;
            bool ok = true;
            foreach (var geometry in new[] { VolumeGeometry.Render, VolumeGeometry.Collision })
            {
                var refused = model with { Status = "refused", Reason = "topology-control refusal" };
                var missingReason = refused with { Reason = null };
                ok &= ExpectedRefusal(SweptGeometry.Build(bytes, refused, null, geometry), refused, geometry, "topology-control refusal")
                    && ExpectedRefusal(SweptGeometry.Build(bytes, missingReason, null, geometry), missingReason, geometry, "refused")
                    && ExpectedRefusal(SweptGeometry.Build(bytes, model, [], geometry), model, geometry, "no animation to sweep")
                    && ExpectedRefusal(SweptGeometry.Build(bytes, model, ["missing"], geometry), model, geometry, "no animation 'missing'");
            }
            return (ok && bytes.SequenceEqual(bytesBefore), "complete expected status/reason/geometry/animation/sample/delta/convergence/solid fields");
        });
        Case(check, "topology T7: actual anchor/contact/volume/box consumers preserve every shared sample field", () =>
        {
            var region = DoorRegion(new MotionOptions(DeltaTarget: 2, SampleBudget: 64));
            var before = region with { Samples = region.Samples.Select(s => s with {
                Soup = new TriangleSoup(s.Soup.Vertices.ToArray(), s.Soup.Indices.ToArray(),
                    s.Soup.TriangleMaterials?.ToArray(), s.Soup.TriangleRadii?.ToArray()), Tags = s.Tags?.ToArray() }).ToArray() };
            var anchor = AnchorMotion.TestSwept(region, new Vector3(-60, -60, 0), DoorBody);
            var partner = Cube(1000, 1000, 1000, 1);
            var contact = SweptGeometry.Contact(region, partner, Enumerable.Repeat(SurfaceMaterial.Opaque, partner.TriangleCount).ToArray(), true);
            var volume = SweptGeometry.Volume(region, null, new VolumeOptions(MaxDepth: 6, MaxCells: 2048), run: new SweptVolumeRun { Threads = 1 });
            var box = MeshSurfaceBox.BoxContactSwept(region, new Aabb(new Vec3(-65, -65, 90), new Vec3(-55, -55, 110), false));
            return (TopologyShared(region) && TopologyRegion(before, region) && anchor.Samples == region.Samples.Count
                && anchor.Decision != "refused" && contact.Decision != "refused" && volume.Status == "measured"
                && box.Samples == region.Samples.Count && box.Decision != "refused", "all fields/float bits preserved through executed consumers");
        });
    }
}
