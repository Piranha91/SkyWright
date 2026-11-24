using System.Numerics;
using HousecarlCore;
using SkyWright.Engine;
using Xunit;

namespace SkyWright.Engine.Tests;

/// <summary>The convex hull the collision reader builds (<see cref="ConvexHull.FromVertices"/>) and the radius-grown hull
/// (<see cref="ConvexHull.Offset"/>), on a spike whose apex is stored once per side triangle within a few 1e-4 of itself,
/// the way an exporter writes a cone. Cases ND-1 to ND-3 (201-203). The hull check: every edge shared by exactly two
/// faces, each direction once; at most 2n-4 faces over the n vertices used; every input point inside every face plane
/// within the tolerance.</summary>
[Trait("tier", "unit")]
public class ConvexHullRobustnessTests
{
    const int Sides = 24, ApexCopies = 22;
    const float BaseRadius = 1.7055f, Height = 17.7408f, BaseZ = 0.0085f;

    /// <summary>The spike: <see cref="ApexCopies"/> apex points scattered by up to 1.5e-5 across and 1.9e-4 along the axis,
    /// then the base ring. Deterministic.</summary>
    internal static List<Vector3> Spike()
    {
        var p = new List<Vector3>();
        for (int k = 0; k < ApexCopies; k++)
            p.Add(new Vector3(1.5e-5f * MathF.Sin(1.7f * k + 0.3f), 1.5e-5f * MathF.Cos(2.3f * k + 0.1f), Height - 1.9e-4f * (0.5f + 0.5f * MathF.Sin(0.9f * k))));
        for (int k = 0; k < Sides; k++)
        {
            float a = 2 * MathF.PI * k / Sides;
            p.Add(new Vector3(BaseRadius * MathF.Cos(a), BaseRadius * MathF.Sin(a), BaseZ));
        }
        return p;
    }

    /// <summary>The spike's stored half-space planes, as a bhkConvexVerticesShape carries them: one per side, and the base.</summary>
    internal static List<Vector4> SpikePlanes(IReadOnlyList<Vector3> pts)
    {
        var apex = new Vector3(0, 0, Height);
        var planes = new List<Vector4>();
        for (int k = 0; k < Sides; k++)
        {
            var b0 = pts[ApexCopies + k]; var b1 = pts[ApexCopies + (k + 1) % Sides];
            var n = Vector3.Normalize(Vector3.Cross(b0 - apex, b1 - apex));
            if (Vector3.Dot(n, b0) < 0) n = -n;
            planes.Add(new Vector4(n, -pts.Max(q => Vector3.Dot(n, q))));
        }
        planes.Add(new Vector4(0, 0, -1, BaseZ));
        return planes;
    }

    /// <summary>The hull check. Null when <paramref name="idx"/> over <paramref name="v"/> is a closed convex hull that holds
    /// every point of <paramref name="inside"/> within <paramref name="tol"/>; otherwise what fails.</summary>
    internal static string? HullFailure(IReadOnlyList<Vector3> v, IReadOnlyList<int> idx, IReadOnlyList<Vector3> inside, float tol)
    {
        if (idx.Count == 0 || idx.Count % 3 != 0) return $"{idx.Count} indices";
        var used = new HashSet<int>(idx);
        int faces = idx.Count / 3;
        if (faces > 2 * used.Count - 4) return $"{faces} faces over {used.Count} vertices, more than 2n-4 = {2 * used.Count - 4}";
        var directed = new Dictionary<(int, int), int>();
        for (int k = 0; k < idx.Count; k += 3)
            for (int e = 0; e < 3; e++)
            {
                var d = (idx[k + e], idx[k + (e + 1) % 3]);
                directed[d] = directed.GetValueOrDefault(d) + 1;
            }
        int bad = directed.Count(d => d.Value != 1 || !directed.ContainsKey((d.Key.Item2, d.Key.Item1)));
        if (bad > 0) return $"{bad} of {directed.Count} directed edges not matched once by their reverse";
        // planes in double: a face between near-coincident points is thin, and its float normal tilts by more than the tolerance
        double worst = 0; int outside = 0;
        for (int k = 0; k < idx.Count; k += 3)
        {
            var (a, b, c) = (v[idx[k]], v[idx[k + 1]], v[idx[k + 2]]);
            double ux = (double)b.X - a.X, uy = (double)b.Y - a.Y, uz = (double)b.Z - a.Z, vx = (double)c.X - a.X, vy = (double)c.Y - a.Y, vz = (double)c.Z - a.Z;
            double nx = uy * vz - uz * vy, ny = uz * vx - ux * vz, nz = ux * vy - uy * vx, len = Math.Sqrt(nx * nx + ny * ny + nz * nz);
            if (!(len > 0)) return $"face {k / 3} has no area";
            foreach (var q in inside)
            {
                double d = (nx * ((double)q.X - a.X) + ny * ((double)q.Y - a.Y) + nz * ((double)q.Z - a.Z)) / len;
                if (d > tol) { outside++; worst = Math.Max(worst, d); }
            }
        }
        return outside > 0 ? $"{outside} point-face pairs outside by more than {tol}, worst {worst}" : null;
    }

    [Fact]
    public void HullCheckRejectsAnOpenAndAnInvertedHull()
    {
        var cube = new List<Vector3>();
        for (int k = 0; k < 8; k++) cube.Add(new Vector3(k & 1, (k >> 1) & 1, (k >> 2) & 1));
        var (idx, degenerate) = ConvexHull.FromVertices(cube);
        Assert.False(degenerate);
        Assert.Null(HullFailure(cube, idx, cube, 1e-5f));
        Assert.NotNull(HullFailure(cube, idx.Skip(3).ToList(), cube, 1e-5f));                          // one face missing
        var flipped = idx.ToList(); (flipped[1], flipped[2]) = (flipped[2], flipped[1]);
        Assert.NotNull(HullFailure(cube, flipped, cube, 1e-5f));                                         // one face wound inward
        Assert.NotNull(HullFailure(cube, idx, cube.Append(new Vector3(0.5f, 0.5f, 1.1f)).ToList(), 1e-5f)); // a point outside
    }

    // ND-1 (201): the hull of the spike's own vertices is a closed convex hull holding every vertex
    [Fact]
    public void Nd1_TheSpikesHullIsClosedAndHoldsEveryVertex()
    {
        var pts = Spike();
        const float eps = 1e-5f;                                   // the default, which Offset builds its grown hull with
        var (idx, degenerate) = ConvexHull.FromVertices(pts, eps);
        Assert.False(degenerate);
        var fail = HullFailure(pts, idx, pts, 2 * eps);
        Assert.True(fail is null, $"ND-1 (201) the spike's hull: {fail}");
    }

    // ND-2 (202): the radius-grown spike, from the plane fans the decoder hands Offset, is a closed convex hull holding the
    // core and every grown point, and building it allocates what its points need (the old reader: 4.6 GB for this spike)
    [Fact]
    public void Nd2_TheGrownSpikeIsClosedHoldsTheCoreAndAllocatesLittle()
    {
        var pts = Spike();
        var fans = HavokCollision.ConvexHullFromPlanes(pts, SpikePlanes(pts), out int dropped);
        Assert.Equal(0, dropped);
        const float radius = 0.008541149f;
        var grown = ConvexHull.GrownPoints(pts, fans, radius);
        long before = GC.GetAllocatedBytesForCurrentThread();
        var (ov, oi) = ConvexHull.Offset(pts, fans, radius);
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        var fail = HullFailure(ov, oi, pts.Concat(grown).ToList(), 1e-4f);
        Assert.True(fail is null, $"ND-2 (202) the grown spike ({grown.Count} grown points, {ov.Length} vertices, {oi.Count / 3} faces): {fail}");
        Assert.True(allocated < 8_000_000, $"ND-2 (202) the grown spike allocated {allocated:N0} bytes");
    }

    /// <summary>A mesh whose one static body is the spike as a bhkConvexVerticesShape with its planes and convex radius.</summary>
    internal static byte[] SpikeMesh()
    {
        var ver = new NiflySharp.NiVersion { FileVersion = NiflySharp.NiVersion.ToFile("20.2.0.7"), UserVersion = 12, StreamVersion = 100 };
        var f = new NiflySharp.NifFile();
        f.Create(ver, withRootNode: true);
        var root = f.GetRootNodes().First();
        root.Flags_ui = 0xE;
        var pts = Spike();
        var shape = new NiflySharp.Blocks.bhkConvexVerticesShape
        {
            Vertices = pts.Select(p => new Vector4(p, 0)).ToList(), NumVertices = (uint)pts.Count,
            Normals = SpikePlanes(pts), NumNormals = (uint)(Sides + 1), Radius = 0.008541149f,
        };
        int si = f.AddBlock(shape);
        var rb = new NiflySharp.Blocks.bhkRigidBody { Shape = new(si) };
        var info = rb.RigidBodyInfo_bRBCI2010;
        var ihf = info.HavokFilter; ihf.Layer_SL = NiflySharp.Enums.SkyrimLayer.SKYL_STATIC; info.HavokFilter = ihf;
        rb.RigidBodyInfo_bRBCI2010 = info;
        var hf = rb.HavokFilter; hf.Layer_SL = NiflySharp.Enums.SkyrimLayer.SKYL_STATIC; rb.HavokFilter = hf;
        int bi = f.AddBlock(rb);
        int co = f.AddBlock(new NiflySharp.Blocks.bhkCollisionObject { Body = new(bi), Target = new(f.Blocks.IndexOf(root)) });
        root.CollisionObject = new(co);
        using var ms = new MemoryStream();
        if (f.Save(ms) != 0) throw new InvalidOperationException("the spike mesh failed to save");
        return ms.ToArray();
    }

    // ND-3 (203): the collision reader, radius on, decodes the spike mesh to a closed convex soup within the face bound, with
    // the allocation a 46-vertex shape needs (the old reader: about 5 GB and 300,000 triangles for the vanilla shape)
    [Fact]
    public void Nd3_TheReaderDecodesTheGrownSpikeClosedAndCheaply()
    {
        var bytes = SpikeMesh();
        long before = GC.GetAllocatedBytesForCurrentThread();
        var o = NifGeometry.Measure(bytes, includeTriangles: true, collisionRadius: true);
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.True(o.Model is not null, $"ND-3 (203) the spike mesh: {o.Error}");
        var m = o.Model!;
        Assert.True(m.CollisionComplete && m.CollisionStats!.ConvexRadiusApplied == 1, $"ND-3 (203) complete {m.CollisionComplete}, radius applied {m.CollisionStats!.ConvexRadiusApplied}");
        var soup = m.CollisionSoup!;
        int tris = soup.TriangleCount, verts = new HashSet<int>(soup.Indices).Count;
        var edges = new Dictionary<(int, int), int>();
        for (int k = 0; k < soup.Indices.Length; k += 3)
            for (int e = 0; e < 3; e++)
            {
                int u = soup.Indices[k + e], v = soup.Indices[k + (e + 1) % 3];
                var key = u < v ? (u, v) : (v, u);
                edges[key] = edges.GetValueOrDefault(key) + 1;
            }
        int open = edges.Count(e => e.Value != 2);
        Assert.True(tris <= 2 * verts - 4 && open == 0, $"ND-3 (203) the grown spike soup: {tris} triangles over {verts} vertices (bound {2 * verts - 4}), {open} edges not shared by two");
        Assert.True(allocated < 32_000_000, $"ND-3 (203) the decode allocated {allocated:N0} bytes");
    }
}
