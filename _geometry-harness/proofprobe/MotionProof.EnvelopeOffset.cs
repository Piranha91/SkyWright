using System.Numerics;
using HousecarlCore;

/// <summary>Known answers for the surface crossing envelope (2026-10-05, task surface-envelope-offset): EnvelopeMin / EnvelopeMax
/// must bound the crossing curve itself. The interior-segment endpoints were formed from a triangle vertex that is off the
/// intersection line, so the envelope sat up to a triangle's size away from the curve while the summed lengths stayed right. Each
/// case names the wrong answer it rejects. Red at the test commit except the case marked guard.</summary>
static partial class MotionProof
{
    static TriangleSoup EoTris(params Vector3[][] tris)
    {
        var v = new List<Vector3>(); var i = new List<int>();
        foreach (var t in tris) foreach (var p in t) { i.Add(v.Count); v.Add(p); }
        return new TriangleSoup(v.ToArray(), i.ToArray());
    }
    static Vector3 EoV(float x, float y, float z) => new(x, y, z);
    // The alpha-sampling quad: z = 0, x, y in [0, 100], split along (0,0)-(100,100).
    static TriangleSoup EoQuadA() => EoTris(new[] { EoV(0, 0, 0), EoV(100, 0, 0), EoV(100, 100, 0) }, new[] { EoV(0, 0, 0), EoV(100, 100, 0), EoV(0, 100, 0) });
    // A vertical wall in y = y0, x in [-10, 110], z in [-10, 10]: crosses EoQuadA along y = y0, x 0..100.
    static TriangleSoup EoWallY(float y0) => EoTris(new[] { EoV(-10, y0, -10), EoV(110, y0, -10), EoV(110, y0, 10) }, new[] { EoV(-10, y0, -10), EoV(110, y0, 10), EoV(-10, y0, 10) });
    // A plate tilted about the x axis, plane z = y - 50, first vertex (0, 100, 50): crosses the floor z = 0 along y = 50, x 0..100.
    static TriangleSoup EoTiltedPlate() => EoTris(new[] { EoV(0, 100, 50), EoV(0, 0, -50), EoV(100, 0, -50) }, new[] { EoV(0, 100, 50), EoV(100, 0, -50), EoV(100, 100, 50) });
    static TriangleSoup EoFloor() => EoTris(new[] { EoV(-10, -10, 0), EoV(110, -10, 0), EoV(110, 110, 0) }, new[] { EoV(-10, -10, 0), EoV(110, 110, 0), EoV(-10, 110, 0) });
    // An oblique pair with no axis alignment: A in the plane z = 0.5x + 10 over x, y in [0, 100]; B the vertical plane y = 0.5x + 20
    // over x in [-10, 110], z in [-100, 200]. Curve (x, 0.5x + 20, 0.5x + 10) for x 0..100: from (0, 20, 10) to (100, 70, 60),
    // length 100 sqrt(1.5).
    static TriangleSoup EoObliqueA() => EoTris(new[] { EoV(0, 0, 10), EoV(100, 0, 60), EoV(100, 100, 60) }, new[] { EoV(0, 0, 10), EoV(100, 100, 60), EoV(0, 100, 10) });
    static TriangleSoup EoObliqueB() => EoTris(new[] { EoV(-10, 15, -100), EoV(110, 75, -100), EoV(110, 75, 200) }, new[] { EoV(-10, 15, -100), EoV(110, 75, 200), EoV(-10, 15, 200) });

    static bool EoNear(double[]? got, double[] want, double tol) => got is { Length: 3 } g && g.Zip(want).All(p => Math.Abs(p.First - p.Second) <= tol);
    static string EoSay(SurfaceContactResult r) =>
        $"{r.Status} {r.Reason} L {r.CrossingLength} interior {r.InteriorSegments} edge {r.EdgeSegments} "
        + $"min ({string.Join(", ", r.EnvelopeMin ?? Array.Empty<double>())}) max ({string.Join(", ", r.EnvelopeMax ?? Array.Empty<double>())})";
    static (bool, string) EoCase(SurfaceContactResult r, double length, double[] min, double[] max, double tol) =>
        (r.Status == "measured" && r.CrossingLength is { } L && Math.Abs(L.Lower - length) <= 1e-6 * Math.Max(1, length) && r.InteriorSegments > 0
         && EoNear(r.EnvelopeMin, min, tol) && EoNear(r.EnvelopeMax, max, tol), EoSay(r));

    public static void RunEnvelopeOffset(Action<string, bool, string> check)
    {
        // EO-1: the oblique synthetic pair, all interior segments. Envelope (0, 20, 10)..(100, 70, 60), length 100 sqrt(1.5). Rejects
        // endpoints formed from A's first vertex (0, 0, 10): the line through that vertex has y = 0.5x, an envelope y of 0..50.
        Case(check, "envelope EO-1: oblique pair - envelope (0,20,10)..(100,70,60) on the curve, length 100 sqrt 1.5", () =>
            EoCase(MeshSurface.Measure(EoObliqueA(), null, EoObliqueB(), null, VolumeGeometry.Render), 100 * Math.Sqrt(1.5),
                   new double[] { 0, 20, 10 }, new double[] { 100, 70, 60 }, 1e-4));
        // EO-2: the alpha-sampling Gate C pair QuadA x WallY(31.25). Envelope (0, 31.25, 0)..(100, 31.25, 0), length 100. Rejects the
        // off-curve envelope y = 0 Gate C recorded.
        Case(check, "envelope EO-2: QuadA x WallY(31.25) - envelope y 31.25 (not 0), x 0..100, length 100", () =>
            EoCase(MeshSurface.Measure(EoQuadA(), null, EoWallY(31.25f), null, VolumeGeometry.Render), 100,
                   new double[] { 0, 31.25, 0 }, new double[] { 100, 31.25, 0 }, 1e-4));
        // EO-3: QuadA x WallY(75). Envelope y 75 (Gate C: 0).
        Case(check, "envelope EO-3: QuadA x WallY(75) - envelope y 75 (not 0), x 0..100, length 100", () =>
            EoCase(MeshSurface.Measure(EoQuadA(), null, EoWallY(75f), null, VolumeGeometry.Render), 100,
                   new double[] { 0, 75, 0 }, new double[] { 100, 75, 0 }, 1e-4));
        // EO-4: a tilted plate x the floor. Curve y = 50, z = 0, x 0..100, length 100. Rejects the envelope at the plate's first
        // vertex height z = 50 (and y = 100) Gate C recorded.
        Case(check, "envelope EO-4: tilted plate x floor - envelope y 50, z 0 (not z 50), x 0..100, length 100", () =>
            EoCase(MeshSurface.Measure(EoTiltedPlate(), null, EoFloor(), null, VolumeGeometry.Render), 100,
                   new double[] { 0, 50, 0 }, new double[] { 100, 50, 0 }, 1e-4));
        // EO-5: EO-1 placed at FarOrigin (city-scale coordinates) through MeasurePlaced. The envelope is the curve shifted by FarOrigin,
        // within float placement error. Rejects the off-curve endpoints in the world frame (y 20 u low).
        Case(check, "envelope EO-5: oblique pair at FarOrigin - envelope is the curve + FarOrigin", () =>
        {
            var r = MeshSurface.MeasurePlaced(EoObliqueA(), null, FarOrigin, Zero3, 1f, EoObliqueB(), null, FarOrigin, Zero3, 1f, VolumeGeometry.Render);
            double ox = FarOrigin.X, oy = FarOrigin.Y, oz = FarOrigin.Z;
            return EoCase(r, 100 * Math.Sqrt(1.5), new[] { ox, oy + 20, oz + 10 }, new[] { ox + 100, oy + 70, oz + 60 }, 0.05);
        });
        // EO-6 (guard): an edge-lying crossing already reports points on its edge. The plate's shared edge lies in the floor; the
        // crossing is (0, 50, 0)..(100, 50, 0) from the edge pass alone (no interior segment).
        Case(check, "envelope EO-6 (guard): edge-lying crossing - envelope on the edge (0,50,0)..(100,50,0)", () =>
        {
            var a = EoTris(new[] { EoV(0, 50, 0), EoV(100, 50, 0), EoV(50, 0, 30) }, new[] { EoV(100, 50, 0), EoV(0, 50, 0), EoV(50, 100, -30) });
            var r = MeshSurface.Measure(a, null, EoFloor(), null, VolumeGeometry.Render);
            return (r.Status == "measured" && r.CrossingLength is { } L && Math.Abs(L.Lower - 100) <= 1e-6 && r.EdgeSegments > 0
                    && EoNear(r.EnvelopeMin, new double[] { 0, 50, 0 }, 1e-4) && EoNear(r.EnvelopeMax, new double[] { 100, 50, 0 }, 1e-4), EoSay(r));
        });
    }
}
