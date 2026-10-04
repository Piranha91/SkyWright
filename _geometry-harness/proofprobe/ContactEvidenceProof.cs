using System.Numerics;
using HousecarlCore;

static class ContactEvidenceProof
{
    static TriangleSoup Triangle(Vector3 a, Vector3 b, Vector3 c) =>
        new(new[] { a, b, c }, new[] { 0, 1, 2 });

    static TriangleSoup Combine(params TriangleSoup[] soups)
    {
        var vertices = new List<Vector3>(); var indices = new List<int>();
        foreach (var soup in soups)
        {
            var offset = vertices.Count; vertices.AddRange(soup.Vertices);
            indices.AddRange(soup.Indices.Select(i => i + offset));
        }
        return new(vertices.ToArray(), indices.ToArray());
    }

    static TriangleSoup Transform(TriangleSoup soup, Matrix4x4 transform) =>
        new(soup.Vertices.Select(v => Vector3.Transform(v, transform)).ToArray(),
            (int[])soup.Indices.Clone());

    static bool Near(double[]? actual, double[] expected, double tolerance = 1e-6) =>
        actual is { Length: 3 } && actual.Zip(expected).All(x => Math.Abs(x.First - x.Second) <= tolerance);

    static (TriangleSoup A, TriangleSoup B) Crossing(float x)
    {
        var a = Triangle(new(x - 2, -2, 0), new(x + 2, -2, 0), new(x, 2, 0));
        var b = Triangle(new(x, -1, -1), new(x, 1, -1), new(x, 0, 1));
        return (a, b);
    }

    public static void Run(Action<string, bool, string> check)
    {
        const double t = 1e-5;
        var c0 = Crossing(0); var c1 = Crossing(100);
        var disconnected = MeshIntersection.MeasureContactEvidence(
            Combine(c0.A, c1.A), Combine(c0.B, c1.B), t);
        var dv = disconnected.Value;
        check("contact evidence: disconnected crossings enumerate beyond first witness",
              disconnected.Status == "complete" && dv is not null
              && dv.CrossingPairs == 2 && dv.CoplanarPairs == 0 && dv.TouchingPairs == 0
              && dv.ParticipatingTrianglesA == 2 && dv.ParticipatingTrianglesB == 2
              && Near(dv.EnvelopeMin, new[] { -t, -1 - t, -t })
              && Near(dv.EnvelopeMax, new[] { 100 + t, 1 + t, t }),
              $"status={disconnected.Status} cross={dv?.CrossingPairs} a={dv?.ParticipatingTrianglesA} b={dv?.ParticipatingTrianglesB} "
              + $"min={string.Join(',', dv?.EnvelopeMin ?? Array.Empty<double>())} max={string.Join(',', dv?.EnvelopeMax ?? Array.Empty<double>())}");

        var swapped = MeshIntersection.MeasureContactEvidence(
            Combine(c0.B, c1.B), Combine(c0.A, c1.A), t);
        check("contact evidence: swapping inputs preserves counts and envelope and swaps participants",
              swapped.Status == "complete" && swapped.Value is not null && dv is not null
              && swapped.TrianglePairsTested == disconnected.TrianglePairsTested
              && swapped.Value.CrossingPairs == dv.CrossingPairs
              && swapped.Value.CoplanarPairs == dv.CoplanarPairs
              && swapped.Value.TouchingPairs == dv.TouchingPairs
              && swapped.Value.ParticipatingTrianglesA == dv.ParticipatingTrianglesB
              && swapped.Value.ParticipatingTrianglesB == dv.ParticipatingTrianglesA
              && Near(swapped.Value.EnvelopeMin, dv.EnvelopeMin!)
              && Near(swapped.Value.EnvelopeMax, dv.EnvelopeMax!),
              $"status={swapped.Status} tested={swapped.TrianglePairsTested}/{disconnected.TrianglePairsTested} "
              + $"counts={swapped.Value?.CrossingPairs}/{swapped.Value?.CoplanarPairs}/{swapped.Value?.TouchingPairs} "
              + $"participants={swapped.Value?.ParticipatingTrianglesA}/{swapped.Value?.ParticipatingTrianglesB}");

        using var cancelledSource = new CancellationTokenSource();
        cancelledSource.Cancel();
        var cancelled = MeshIntersection.MeasureContactEvidence(c0.A, c0.B, t,
            cancellationToken: cancelledSource.Token);
        check("contact evidence: cancellation before work cannot produce a final or partial value",
              cancelled.Status == "incomplete" && cancelled.Reason == "cancelled"
              && cancelled.Value is null && cancelled.PartialValue is null
              && cancelled.TrianglePairsTested == 0,
              $"status={cancelled.Status} reason={cancelled.Reason} tested={cancelled.TrianglePairsTested} "
              + $"partial={cancelled.PartialValue is not null} final={cancelled.Value is not null}");

        var lower = Triangle(new(0, 0, 0), new(4, 0, 0), new(0, 4, 0));
        var upper = Triangle(new(4, 4, 0), new(4, 1, 0), new(1, 4, 0));
        var separated = MeshIntersection.MeasureContactEvidence(lower, upper, t);
        check("contact evidence: overlapping AABBs cannot manufacture triangle contact",
              separated.Status == "complete" && separated.Value is { CrossingPairs: 0, CoplanarPairs: 0, TouchingPairs: 0,
                  ParticipatingTrianglesA: 0, ParticipatingTrianglesB: 0, EnvelopeMin: null, EnvelopeMax: null },
              $"status={separated.Status} value={separated.Value}");

        var copA = Triangle(new(100, 0, 0), new(103, 0, 0), new(100, 3, 0));
        var copB = Triangle(new(101, 0, 0), new(102, 0, 0), new(101, 1, 0));
        var touchA = Triangle(new(200, 0, 0), new(201, 0, 0), new(200, 1, 0));
        var touchB = Triangle(new(200, 0, 0), new(201, 0, 0), new(201, -1, 0));
        var sepA = Transform(lower, Matrix4x4.CreateTranslation(300, 0, 0));
        var sepB = Transform(upper, Matrix4x4.CreateTranslation(300, 0, 0));
        var matrix = MeshIntersection.MeasureContactEvidence(
            Combine(c0.A, copA, touchA, sepA), Combine(c0.B, copB, touchB, sepB), t);
        check("contact evidence: crossing coplanar touch and separation stay distinct",
              matrix.Status == "complete" && matrix.Value is { CrossingPairs: 1, CoplanarPairs: 1, TouchingPairs: 1,
                  ParticipatingTrianglesA: 3, ParticipatingTrianglesB: 3 },
              $"status={matrix.Status} cross={matrix.Value?.CrossingPairs} coplanar={matrix.Value?.CoplanarPairs} "
              + $"touch={matrix.Value?.TouchingPairs} a={matrix.Value?.ParticipatingTrianglesA} b={matrix.Value?.ParticipatingTrianglesB}");

        var pose = Matrix4x4.CreateScale(-2f) * Matrix4x4.CreateRotationZ(MathF.PI / 2f)
                   * Matrix4x4.CreateTranslation(10, 20, 30);
        var transformed = MeshIntersection.MeasureContactEvidence(
            Transform(Combine(c0.A, c1.A), pose), Transform(Combine(c0.B, c1.B), pose), t);
        var known = new[] { new Vector3(0,-1,0), new(0,1,0), new(100,-1,0), new(100,1,0) }
            .Select(v => Vector3.Transform(v, pose)).ToArray();
        var expectedMin = new[] { known.Min(v => (double)v.X)-t, known.Min(v => (double)v.Y)-t, known.Min(v => (double)v.Z)-t };
        var expectedMax = new[] { known.Max(v => (double)v.X)+t, known.Max(v => (double)v.Y)+t, known.Max(v => (double)v.Z)+t };
        check("contact evidence: translation rotation and negative scale transform support envelope",
              transformed.Status == "complete" && transformed.Value is { CrossingPairs: 2 }
              && Near(transformed.Value.EnvelopeMin, expectedMin, 2e-5)
              && Near(transformed.Value.EnvelopeMax, expectedMax, 2e-5)
              && transformed.Value.EnvelopeMin!.Zip(transformed.Value.EnvelopeMax!).All(x => x.First <= x.Second),
              $"status={transformed.Status} min={string.Join(',', transformed.Value?.EnvelopeMin ?? Array.Empty<double>())} "
              + $"max={string.Join(',', transformed.Value?.EnvelopeMax ?? Array.Empty<double>())}");

        var budget = MeshIntersection.MeasureContactEvidence(
            Combine(c0.A, c1.A), Combine(c0.B, c1.B), t, maxTrianglePairs: 1);
        check("contact evidence: exhausted budget separates partial from completed value",
              budget.Status == "incomplete" && budget.Value is null && budget.PartialValue is not null
              && budget.Reason == "triangle_pair_budget_exhausted" && budget.TrianglePairsTested == 1
              && budget.PartialValue.CrossingPairs == 1
              && budget.PartialValue.CoplanarPairs == 0 && budget.PartialValue.TouchingPairs == 0
              && budget.PartialValue.ParticipatingTrianglesA == 1 && budget.PartialValue.ParticipatingTrianglesB == 1
              && Near(budget.PartialValue.EnvelopeMin, new[] { -t, -1 - t, -t })
              && Near(budget.PartialValue.EnvelopeMax, new[] { t, 1 + t, t }),
              $"status={budget.Status} reason={budget.Reason} tested={budget.TrianglePairsTested} "
              + $"partial={budget.PartialValue} final={budget.Value is not null}");

        var invalid = MeshIntersection.MeasureContactEvidence(
            Triangle(new(float.NaN,0,0), new(1,0,0), new(0,1,0)), c0.B, t);
        check("contact evidence: invalid source remains unknown without envelopes",
              invalid.Status == "unknown" && invalid.Value is null && invalid.PartialValue is null
              && !string.IsNullOrWhiteSpace(invalid.Reason),
              $"status={invalid.Status} reason={invalid.Reason} partial={invalid.PartialValue is not null} final={invalid.Value is not null}");

        RunContactShell(check);
    }

    /// <summary>CS-D: the decoder records each triangle's Havok radius on the core soup (contact shell recalibration, 2026-10-03) —
    /// box, compressed mesh and convex vertices, in Skyrim units, carried through the body cut; and a decode that already GREW the
    /// shape records 0, so no reader can count a radius twice. Expected radii are the fixtures' own (stated in Skyrim units).</summary>
    static void RunContactShell(Action<string, bool, string> check)
    {
        static (bool Ok, string Detail) Radii(byte[] nif, float want)
        {
            var m = NifGeometry.Measure(nif, includeTriangles: true).Model;
            var soup = m?.CollisionSoupFor(b => b.Solid);
            var r = soup?.TriangleRadii;
            bool ok = soup is { TriangleCount: > 0 } && r is not null && r.Length == soup.TriangleCount && r.All(x => MathF.Abs(x - want) <= 1e-3f);
            return (ok, $"triangles={soup?.TriangleCount} radii={(r is null ? "null" : string.Join(",", r.Select(x => x.ToString("0.###")).Distinct()))}");
        }
        static (bool Ok, string Detail) GrownZero(byte[] nif)
        {
            var f = new NiflySharp.NifFile();
            using var ms = new MemoryStream(nif, writable: false);
            if (f.Load(ms) != 0) return (false, "fixture did not parse");
            var bodies = HavokCollision.Decode(f, includeTriangles: true, convexRadius: true).Bodies.Where(b => b.Solid && b.Indices.Length > 0).ToList();
            bool ok = bodies.Count > 0 && bodies.All(b => b.TriangleRadii is { } r && r.Length == b.Indices.Length / 3 && r.All(x => x == 0f));
            return (ok, string.Join("; ", bodies.Select(b => $"{b.Indices.Length / 3} tris radii={(b.TriangleRadii is null ? "null" : string.Join(",", b.TriangleRadii.Distinct()))}")));
        }
        foreach (var (name, nif, want) in new[]
        {
            ("box shape, radius 2", MotionProof.CsSupportBox(50, 10, 2f), 2f),
            ("compressed-mesh plate, radius 0.35", MotionProof.CsSupportMeshPlate(50, 0.35f), 0.35f),
            ("convex vertices, radius 3.5", MotionProof.CsFreeItemRadius(3.5f), 3.5f),
        })
        {
            var (ok, detail) = Radii(nif, want);
            check($"CS-D core decode records the Havok radius per triangle: {name}", ok, detail);
            var (gz, gd) = GrownZero(nif);
            check($"CS-D grown decode records 0 (no double count): {name}", gz, gd);
        }

        // CS-Kscale: a placed reference's scale scales its radius with its geometry (the contact pass's broad-phase pad reads it)
        {
            var model = new TriangleSoup(new[] { new Vector3(0, 0, 0), new Vector3(1, 0, 0), new Vector3(0, 1, 0) }, new[] { 0, 1, 2 }, null, new[] { 1.5f });
            var placed = SpatialOverlap.TransformSoup(model, new Vec3(0, 0, 0), new Vec3(0, 0, 0), 2f);
            check("CS-Kscale reference scale 2 -> radius 1.5 reads 3.0",
                  placed.TriangleRadii is { Length: 1 } pr && MathF.Abs(pr[0] - 3f) <= 1e-4f,
                  $"radii={(placed.TriangleRadii is null ? "null" : string.Join(",", placed.TriangleRadii))}");
        }

        // CS-S: the gap between two shapes read with their radii grown (contactprobe --radius on reads the gap from these soups).
        // A box item (half 8, radius 3.5) over a box support (top at z 32, radius 0.35) at core gap g: the grown faces are the core
        // faces pushed out by each radius, so the shell gap is exactly max(0, g - 3.85) — pinned to 0.05 u, never "about 0" (Gate A
        // review finding 2). The core surface distance stays g (guard; the facing gap is capped at the 8 u band, so the core check
        // reads the plain distance).
        static TriangleSoup Solid(byte[] nif, bool grown) =>
            NifGeometry.Measure(nif, includeTriangles: true, collisionRadius: grown).Model!.CollisionSoupFor(b => b.Solid)!;
        var support = MotionProof.CsSupportBox(100, 32, 0.35f);
        var item = MotionProof.CsFreeItemBoxRadius(8, 3.5f);
        foreach (var (id, g, want) in new[] { ("CS-S1", 3.85f, 0f), ("CS-S1", 5.85f, 2f), ("CS-S2", 9.85f, 6f) })
        {
            var at = new Vec3(0, 0, 32 + g + 8);
            var shell = SpatialOverlap.Contact(SpatialOverlap.TransformSoup(Solid(item, true), at, new Vec3(0, 0, 0), 1f), Solid(support, true), 128f, 8f);
            var core = SpatialOverlap.Contact(SpatialOverlap.TransformSoup(Solid(item, false), at, new Vec3(0, 0, 0), 1f), Solid(support, false), 128f, 8f);
            check($"{id} box item over box support, core gap {g}, radii 3.5 + 0.35 -> grown gap {want}",
                  MathF.Abs(shell.FacingDistance - want) <= 0.05f && MathF.Abs(core.Distance - g) <= 0.05f,
                  $"grown facing={shell.FacingDistance:0.###} core distance={core.Distance:0.###}");
        }
        // CS-S3: a convex item (box hull half 5, convex radius 3.5) over a compressed-mesh plate (z 0, mesh radius 0.35), core gap 5.85
        {
            var convex = MotionProof.CsFreeItemRadius(3.5f);
            var plate = MotionProof.CsSupportMeshPlate(100, 0.35f);
            var at = new Vec3(0, 0, 5.85f + 5);
            var shell = SpatialOverlap.Contact(SpatialOverlap.TransformSoup(Solid(convex, true), at, new Vec3(0, 0, 0), 1f), Solid(plate, true), 128f, 8f);
            check("CS-S3 convex item over a compressed-mesh plate, core gap 5.85, radii 3.5 + 0.35 -> grown gap 2.00",
                  MathF.Abs(shell.FacingDistance - 2f) <= 0.05f, $"grown facing={shell.FacingDistance:0.###}");
        }
    }
}
