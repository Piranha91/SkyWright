// Known answers for instanced pose storage in SweptGeometry.Volume (task 2026-10-21-swept-instanced-pose-storage): the near
// test stores a triangle whose vertices are bit-equal in every sample once, keeps each sample's moving triangles in its own
// compact tree, and gives the combined mesh's answer on every cell.
using System.Numerics;
using HousecarlCore;
using NiflySharp;
using NiflySharp.Blocks;

static partial class MotionProof
{
    // A flat grid of n x n quads (2 n^2 triangles) on z = z0 over [x0, x0 + size] x [y0, y0 + size].
    static TriangleSoup NearGrid(int n, float x0, float y0, float z0, float size)
    {
        var v = new List<Vector3>(); var ix = new List<int>();
        for (int j = 0; j <= n; j++) for (int i = 0; i <= n; i++) v.Add(new Vector3(x0 + size * i / n, y0 + size * j / n, z0 + 0.25f * ((i * 7 + j * 3) % 5)));
        for (int j = 0; j < n; j++) for (int i = 0; i < n; i++)
        {
            int a = j * (n + 1) + i, b = a + 1, c = a + n + 1, d = c + 1;
            ix.AddRange(new[] { a, b, d, a, d, c });
        }
        return new TriangleSoup(v.ToArray(), ix.ToArray());
    }

    // The open plate of MV1 swinging 90 deg about Z beside a 2,048-triangle static grid (shape "Big") and the rig's Frame cube.
    static byte[] NearWindmill()
    {
        var plate = new TriangleSoup(new[] { new Vector3(10, 0, 0), new Vector3(60, 0, 0), new Vector3(60, 0, 50), new Vector3(10, 0, 50) }, new[] { 0, 1, 2, 0, 2, 3 });
        return Rig(plate, Vector3.Zero, Xyz(FG(NiflySharp.Enums.KeyType.LINEAR_KEY, FK(0, 0)), FG(NiflySharp.Enums.KeyType.LINEAR_KEY, FK(0, 0)),
                                            FG(NiflySharp.Enums.KeyType.LINEAR_KEY, FK(0, 0), FK(1, Pi / 2))),
                   extra: (f, root) => root.Children.AddBlockRef(f.AddBlock(Shape("Big", NearGrid(32, -40, -40, 20, 120)))));
    }

    static TriangleSoup NearCombined(SweptRegion r)
    {
        var v = new List<Vector3>(); var ix = new List<int>();
        foreach (var s in r.Samples) { int b0 = v.Count; v.AddRange(s.Soup.Vertices); ix.AddRange(s.Soup.Indices.Select(x => x + b0)); }
        return new TriangleSoup(v.ToArray(), ix.ToArray());
    }

    // The instanced near test against MeshMeetsBox on the combined mesh, on random cells and on cells at posed vertices.
    static (int Cells, int Yes, int Mismatch, string First) NearIdentity(SweptRegion r, int seed, int count = 3000)
    {
        var near = SweptGeometry.NearIndex(r) ?? throw new InvalidOperationException("no near index");
        var combined = MeshIntersection.PrepareSurfaceMesh(NearCombined(r), out var why) ?? throw new InvalidOperationException(why);
        var ob = MeshIntersection.MeshBounds(combined);
        var rng = new Random(seed);
        double maxDelta = r.Samples.Max(s => s.Delta);
        int yes = 0, bad = 0; string first = ""; long hint = -1, tested = 0;
        for (int k = 0; k < count; k++)
        {
            double cx, cy, cz, h;
            if (k % 3 == 2)
            {
                // faces on a posed vertex's coordinates, a few ulps either way: where a culling step that rounds would show
                var s = r.Samples[rng.Next(r.Samples.Count)]; var p = s.Soup.Vertices[rng.Next(s.Soup.Vertices.Length)];
                double Ulps(double x, int n) { for (; n > 0; n--) x = Math.BitIncrement(x); for (; n < 0; n++) x = Math.BitDecrement(x); return x; }
                double side = Math.Pow(10, -3 + 3 * rng.NextDouble());
                double lx = Ulps(p.X, rng.Next(-3, 4)), ly = Ulps(p.Y, rng.Next(-3, 4)), lz = Ulps(p.Z, rng.Next(-3, 4));
                var face = new MeshIntersection.SweptBox(rng.Next(2) == 0 ? lx : lx - side, rng.Next(2) == 0 ? ly : ly - side, rng.Next(2) == 0 ? lz : lz - side, 0, 0, 0);
                var fb = new MeshIntersection.SweptBox(face.MinX, face.MinY, face.MinZ, face.MinX + side, face.MinY + side, face.MinZ + side);
                double g0 = (k / 3) % 2 == 0 ? 0 : maxDelta;
                bool w0 = MeshIntersection.MeshMeetsBox(combined, fb, g0);
                long r0 = MeshIntersection.NearFirst(near, fb, g0, hint, ref tested);
                if (r0 >= 0) hint = r0;
                if (w0) yes++;
                if (w0 != r0 >= 0 && bad++ == 0) first = $"face cell {k} [{fb.MinX:R},{fb.MinY:R},{fb.MinZ:R}] side {side:R} grow {g0:R}: combined {w0}, instanced {r0}";
                continue;
            }
            if (k % 3 == 0)
            {
                cx = ob.MinX + rng.NextDouble() * (ob.MaxX - ob.MinX); cy = ob.MinY + rng.NextDouble() * (ob.MaxY - ob.MinY); cz = ob.MinZ + rng.NextDouble() * (ob.MaxZ - ob.MinZ);
                h = (0.002 + 0.15 * rng.NextDouble()) * Math.Max(ob.MaxX - ob.MinX, Math.Max(ob.MaxY - ob.MinY, ob.MaxZ - ob.MinZ));
            }
            else
            {
                var s = r.Samples[rng.Next(r.Samples.Count)]; var p = s.Soup.Vertices[rng.Next(s.Soup.Vertices.Length)];
                h = Math.Pow(10, -4 + 4.5 * rng.NextDouble());
                cx = p.X + (rng.NextDouble() - 0.5) * 3 * h; cy = p.Y + (rng.NextDouble() - 0.5) * 3 * h; cz = p.Z + (rng.NextDouble() - 0.5) * 3 * h;
            }
            var box = new MeshIntersection.SweptBox(cx - h, cy - h, cz - h, cx + h, cy + h, cz + h);
            double grow = (k / 3) % 2 == 0 ? 0 : maxDelta;
            bool want = MeshIntersection.MeshMeetsBox(combined, box, grow);
            long got = MeshIntersection.NearFirst(near, box, grow, hint, ref tested);
            if (got >= 0) hint = got;
            if (want) yes++;
            if (want != got >= 0 && bad++ == 0) first = $"cell {k} [{box.MinX:R},{box.MinY:R},{box.MinZ:R}]..[{box.MaxX:R},{box.MaxY:R},{box.MaxZ:R}] grow {grow:R}: combined {want}, instanced {got}";
        }
        return (count, yes, bad, first);
    }

    public static void RunSweptNear(Action<string, bool, string> check)
    {
        byte[] windmill = NearWindmill();
        var wm = NifMotion.Read(windmill, null);
        var region = SweptGeometry.Build(windmill, wm, null, VolumeGeometry.Render);
        int perSample = region.Status == "built" ? region.Samples[0].Soup.TriangleCount : -1;

        // MV4 (red on 90b78cd4): the combined mesh of all samples is the 25 GiB on SWindMill; no prepared soup may exceed one sample
        Case(check, "MV4 instanced storage: Volume on a swinging plate beside a 2,048-triangle static grid prepares no soup larger than one sample and gives 90b78cd4's interval [0, 211235.75746220053] (rejects the combined mesh of every sample)", () =>
        {
            long largest = 0, prepared = 0;
            var old = MeshIntersection.TestPrepareHook;
            MeshIntersection.TestPrepareHook = s => { Interlocked.Increment(ref prepared); long n = s.TriangleCount; long cur; while (n > (cur = Interlocked.Read(ref largest)) && Interlocked.CompareExchange(ref largest, n, cur) != cur) { } };
            SweptVolume v;
            try { v = SweptGeometry.Volume(region, null, new VolumeOptions(MaxCells: 20_000)); }
            finally { MeshIntersection.TestPrepareHook = old; }
            // the interval is 90b78cd4's own Volume on this input (the red run): the near index must not move it
            return (region.Status == "built" && !region.Solid && region.Samples.Count > 50 && v.Status == "measured" && largest <= perSample
                    && v.Swept is { Lower: 0, Upper: 211235.75746220053 },
                    $"samples {region.Samples.Count}, triangles per sample {perSample}, prepares {prepared}, largest prepared soup {largest} triangles; volume {v.Status} [{v.Swept?.Lower:R}, {v.Swept?.Upper:R}]");
        });

        // MV5: same yes/no as the combined mesh on every cell, and the structure the plan states
        Case(check, "MV5 near-test identity: the instanced near test answers as MeshMeetsBox on the combined mesh on 3,000 cells each (random, at posed vertices, faces on vertex coordinates +-3 ulps) for the windmill rig, the same placed (rotated, scale 1.7) and mirrored (scale -1.3), a skinned rig and a collision rig; the static shapes are stored once (rejects a culling step that drops a triangle, and storage per sample)", () =>
        {
            var details = new List<string>(); bool ok = region.Status == "built";
            var near = SweptGeometry.NearIndex(region);
            long staticTris = perSample - 2;   // everything but the swinging plate's 2 triangles
            ok &= near is not null && near.Instances == region.Samples.Count + 1 && near.StaticTriangles == staticTris
                  && near.StoredTriangles == staticTris + 2L * region.Samples.Count && near.OwnSamples == 0;
            details.Add($"windmill: samples {region.Samples.Count}, instances {near?.Instances}, static triangles {near?.StaticTriangles} (want {staticTris}), stored {near?.StoredTriangles}, own samples {near?.OwnSamples}");
            var placed = SweptGeometry.Place(region, new Vec3(1000, -500, 30), new Vec3(0.3f, -0.2f, 1.1f), 1.7f);
            var mirrored = SweptGeometry.Place(region, new Vec3(-20000, 35000, -700), new Vec3(-1.2f, 0.4f, 2.9f), -1.3f);
            byte[] skin = SkinRig(Pi / 2);
            var skinRegion = SweptGeometry.Build(skin, NifMotion.Read(skin, null), null, VolumeGeometry.Render);
            byte[] coll = Rig(Box(0, -1, 0, 100, 1, 200), Vector3.Zero, Xyz(NoKeys(), NoKeys(), FG(NiflySharp.Enums.KeyType.LINEAR_KEY, FK(0, 0), FK(1, Pi / 2))),
                extra: (f, _) => AddBody(f, f.Blocks.OfType<NiNode>().Single(n => n.Name?.String == "Mover"),
                    ConvexBox(0, -1, 0, 100, 1, 200), 0f, NiflySharp.Enums.hkMotionType.MO_SYS_BOX_STABILIZED, NiflySharp.Enums.SkyrimLayer.SKYL_STATIC));
            var collRegion = SweptGeometry.Build(coll, NifMotion.Read(coll, null), null, VolumeGeometry.Collision);
            ok &= skinRegion.Status == "built" && collRegion.Status == "built";
            details.Add($"skinned {skinRegion.Status} {skinRegion.Samples.Count} samples; collision {collRegion.Status} {collRegion.Samples.Count} samples");
            foreach (var (name, r, seed) in new[] { ("windmill", region, 1), ("placed", placed, 2), ("mirrored", mirrored, 5), ("skinned", skinRegion, 3), ("collision", collRegion, 6) })
            {
                if (r.Status != "built") continue;
                var (cells, yes, bad, first) = NearIdentity(r, seed);
                ok &= bad == 0 && yes >= cells / 10 && cells - yes >= cells / 10;
                details.Add($"{name}: cells {cells}, near {yes}, mismatches {bad} {first}");
            }
            return (ok, string.Join("; ", details));
        });

        // MV6 (sabotage control): the moving triangles of every sample are what the per-sample trees must keep
        Case(check, "MV6 per-sample copies are load-bearing: a build that calls every vertex static (sample 0's triangles only) answers differently from the combined mesh on the windmill cells, and the proper build on the same cells does not (rejects a static test that is not checked against every sample)", () =>
        {
            var old = MeshIntersection.SweptNearSabotage;
            try
            {
                MeshIntersection.SweptNearSabotage = false;
                var (c1, y1, b1, f1) = NearIdentity(region, 4, 1500);
                MeshIntersection.SweptNearSabotage = true;
                var (c2, y2, b2, f2) = NearIdentity(region, 4, 1500);
                return (b1 == 0 && y1 >= c1 / 10 && b2 > 0, $"proper: mismatches {b1} of {c1} ({y1} near) {f1}; all-static: mismatches {b2} of {c2} {f2}");
            }
            finally { MeshIntersection.SweptNearSabotage = old; }
        });
    }
}
