using System.Numerics;
using HousecarlCore;
using SkyWright.Engine;
using NiflySharp.Enums;

/// <summary>Known answers for the shared sampling loop (MemberMotion.SampleRegion, task 2026-11-05-shared-sampling-loop-unguarded).
/// SL-1: a clip-only member's region is SweptGeometry.Build's, sample for sample. SL-2..SL-4: a member region's certified deltas
/// cover dense poses of the member's motion, measured as motionprobe deltacheck measures a clip's (every vertex's displacement
/// from a sample's pose against that sample's delta). The dense poses are analytic from each fixture's stated motion (rotations
/// about stated axes and pivots, the nlerp and slerp formulas), not from the loop, its cells or the pose reader.</summary>
static partial class MotionProof
{
    /// <summary>Absolute allowance for float posing (the loop poses in single precision at coordinates up to about 100 u).</summary>
    const double CoverTol = 1e-4;

    /// <summary>One dense pose's covering: the least, over the region's samples of the same layout, of the largest vertex
    /// displacement from the sample's pose over the sample's delta (0 when the pose is the sample's own within CoverTol).</summary>
    sealed record Covering(double Worst, string WorstAt, int Poses, int Uncovered, string? FirstUncovered, int Samples, double MaxDelta, bool Converged);

    /// <summary>Covering over every pose. A pose is covered when some sample's pose lies within that sample's delta + CoverTol of it at
    /// every vertex. Samples are pre-filtered on one key vertex (a necessary condition: no vertex moves more than the delta); a
    /// ratio above <paramref name="cap"/> is reported as the cap.</summary>
    static Covering Cover(SweptRegion reg, IEnumerable<(string Label, Vector3[] V)> poses, int key, double cap = 4)
    {
        var samples = reg.Samples;
        double maxD = samples.Count == 0 ? 0 : samples.Max(s => s.Delta);
        double cell = Math.Max(1e-2, cap * maxD + CoverTol);
        (long, long, long) Key(Vector3 p) => ((long)Math.Floor(p.X / cell), (long)Math.Floor(p.Y / cell), (long)Math.Floor(p.Z / cell));
        var grid = new Dictionary<(long, long, long), List<int>>();
        for (int i = 0; i < samples.Count; i++)
        {
            var k = Key(samples[i].Soup.Vertices[key]);
            if (!grid.TryGetValue(k, out var l)) grid[k] = l = new List<int>();
            l.Add(i);
        }
        double worst = 0; string worstAt = ""; int n = 0, unc = 0; string? firstUnc = null;
        foreach (var (label, v) in poses)
        {
            n++;
            var (kx, ky, kz) = Key(v[key]);
            double best = cap;
            for (long dx = -1; dx <= 1; dx++) for (long dy = -1; dy <= 1; dy++) for (long dz = -1; dz <= 1; dz++)
            {
                if (!grid.TryGetValue((kx + dx, ky + dy, kz + dz), out var l)) continue;
                foreach (int i in l)
                {
                    var s = samples[i]; var sv = s.Soup.Vertices;
                    if (sv.Length != v.Length) continue;
                    double lim = s.Delta * best + CoverTol;
                    if (Vector3.Distance(sv[key], v[key]) > lim) continue;
                    double dev = 0;
                    for (int j = 0; j < v.Length && dev <= lim; j++) dev = Math.Max(dev, Vector3.Distance(sv[j], v[j]));
                    double r = dev <= CoverTol ? 0 : s.Delta > 0 ? dev / s.Delta : double.PositiveInfinity;
                    if (r < best) best = r;
                    // covered with the rounding allowance even when the raw ratio is a hair above 1
                    if (dev <= s.Delta + CoverTol && best > 1) best = 1;
                }
            }
            if (best > 1) { unc++; firstUnc ??= $"{label} ratio {(best >= cap ? $">= {cap}" : best.ToString("0.####"))}"; }
            if (best > worst) { worst = best; worstAt = label; }
        }
        return new(worst, worstAt, n, unc, firstUnc, samples.Count, maxD, reg.Converged);
    }

    static string ShowCover(Covering c) =>
        $"{c.Poses} poses, {c.Samples} samples (max delta {c.MaxDelta:0.####}, converged {c.Converged}), worst displacement/delta {c.Worst:0.####} at {c.WorstAt}, uncovered {c.Uncovered}{(c.FirstUncovered is null ? "" : " first " + c.FirstUncovered)}";

    static Vector3 Rz(double deg, Vector3 p, Vector3 pivot = default)
    {
        double a = deg * Math.PI / 180, c = Math.Cos(a), s = Math.Sin(a);
        double x = p.X - pivot.X, y = p.Y - pivot.Y;
        return new((float)(pivot.X + c * x - s * y), (float)(pivot.Y + s * x + c * y), p.Z);
    }

    /// <summary>The region's rest sample (the stored pose, origin rest) and whether it is the fixture's stored mesh as a vertex set.</summary>
    static (Vector3[]? V, bool Stored) RestOf(SweptRegion reg, IEnumerable<TriangleSoup> stored)
    {
        var rest = reg.Samples.FirstOrDefault(s => s.Origin == MemberMotion.OriginRest);
        if (rest is null) return (null, false);
        var want = stored.SelectMany(s => s.Vertices).ToList();
        var got = rest.Soup.Vertices;
        bool same = got.Length == want.Count && want.All(w => got.Any(g => Vector3.Distance(g, w) <= CoverTol)) && got.All(g => want.Any(w => Vector3.Distance(g, w) <= CoverTol));
        return (got, same);
    }

    /// <summary>Per vertex, its connected component's id (through the soup's triangles).</summary>
    static int[] Components(TriangleSoup s)
    {
        var p = Enumerable.Range(0, s.Vertices.Length).ToArray();
        int F(int x) { while (p[x] != x) x = p[x] = p[p[x]]; return x; }
        for (int t = 0; t < s.Indices.Length; t += 3) { p[F(s.Indices[t + 1])] = F(s.Indices[t]); p[F(s.Indices[t + 2])] = F(s.Indices[t]); }
        return Enumerable.Range(0, p.Length).Select(F).ToArray();
    }

    static int FarVertex(Vector3[] v) => Enumerable.Range(0, v.Length).OrderByDescending(i => v[i].Length()).First();

    /// <summary>SL-1b fixture: tier 1 (no graph), two sequences on two nodes: "Turn" turns N1 0 -> 90 deg about Z, "Slide" slides N2
    /// x 0 -> 20; "Part" cube half 0.5 at (50,0,0) in N2.</summary>
    static byte[] SL1b() => ChainNif(null, new[] { ("Part", "N2", Cube(50, 0, 0, 0.5f), false) },
                                     new[] { ("Turn", new[] { new FxKeys("N1", 0, 90) }, CycleType.CYCLE_CLAMP),
                                             ("Slide", new[] { new FxKeys("N2", X0: 0, X1: 20) }, CycleType.CYCLE_CLAMP) });

    /// <summary>Region's samples against Build's for the same model and options: count, then per sample id, time, delta, origin
    /// (null), tags, vertices and indices, all exact. Null when equal, else the first difference.</summary>
    static string? SameSamples(SweptRegion a, SweptRegion b)
    {
        if (a.Samples.Count != b.Samples.Count) return $"count: Region {a.Samples.Count}, Build {b.Samples.Count}";
        for (int i = 0; i < a.Samples.Count; i++)
        {
            var x = a.Samples[i]; var y = b.Samples[i];
            if (x.AnimationId != y.AnimationId || x.Time != y.Time) return $"sample {i}: Region {x.AnimationId}@{x.Time}, Build {y.AnimationId}@{y.Time}";
            if (x.Delta != y.Delta) return $"sample {i} ({x.AnimationId}@{x.Time}): delta Region {x.Delta:R}, Build {y.Delta:R}";
            if (x.Origin is not null || y.Origin is not null) return $"sample {i}: origin Region {x.Origin}, Build {y.Origin}";
            if (!(x.Tags ?? Array.Empty<string>()).SequenceEqual(y.Tags ?? Array.Empty<string>()) || (x.Tags is null) != (y.Tags is null)) return $"sample {i}: tags differ";
            if (!x.Soup.Vertices.SequenceEqual(y.Soup.Vertices) || !x.Soup.Indices.SequenceEqual(y.Soup.Indices)) return $"sample {i} ({x.AnimationId}@{x.Time}): soup differs";
        }
        return null;
    }

    public static void RunSamplingLoop(Action<string, bool, string> check)
    {
        // ================= SL-1: clip-only identity =================
        Case(check, "SL-1 clip-only member (K-M12 one turn; SL-1b two sequences on two nodes): MemberMotion.Region's samples equal SweptGeometry.Build's for the same model and options in count, order, ids, times, deltas, tags and soups, at the default sweep and at (0.2 u, budget 24) (rejects the shared loop dropping, adding, reordering or re-posing a clip-only sample)", () =>
        {
            var parts = new List<string>(); bool ok = true;
            foreach (var (name, nif) in new[] { ("K-M12", KM12()), ("SL-1b", SL1b()) })
                foreach (var sweep in new[] { new MemberSweep(), new MemberSweep(0.2, 24) })
                {
                    var m = MemberMotion.Read(nif, null, Mstt);
                    bool clipOnly = MemberMotion.IsClipOnly(nif, m);
                    var reg = MemberMotion.Region(nif, m, VolumeGeometry.Render, sweep, null);
                    var bld = SweptGeometry.Build(nif, m.Motion, null, VolumeGeometry.Render, new MotionOptions(sweep.DeltaTarget, sweep.Budget), null);
                    string? diff = SameSamples(reg, bld);
                    bool one = clipOnly && reg.Status == "built" && bld.Status == "built" && bld.Samples.Count > 1 && diff is null
                               && reg.Converged == bld.Converged && reg.MaxDelta == bld.MaxDelta;
                    ok &= one;
                    parts.Add($"{name} ({sweep.DeltaTarget},{sweep.Budget}): clip-only {clipOnly}, Region {reg.Status} {reg.Samples.Count} conv {reg.Converged}, Build {bld.Status} {bld.Samples.Count} conv {bld.Converged}, {diff ?? "identical"}");
                }
            return (ok, string.Join(" | ", parts));
        });

        // ================= SL-2: hinge rod (K-M7) =================
        Case(check, "SL-2 K-M7 pinned rod (limits [-30, +45], stored 0; the region covers either sense: offsets [-45, +45] deg): every dense pose (the stored mesh turned about +Z through the origin, every 0.01 deg over [-45, 45]) lies within the certified delta of some sample, displacement measured per vertex as deltacheck does; control: turns of +-50 and +-60 deg are not covered (rejects member deltas below the true displacement)", () =>
        {
            var nif = HingeRod(0f, -30, 45);
            var m = MemberMotion.Read(nif, null, Mstt);
            var reg = MemberMotion.Region(nif, m, VolumeGeometry.Render, new MemberSweep(), null);
            var (rest, stored) = RestOf(reg, new[] { Box(0, -1, -1, 100, 1, 1) });
            if (rest is null || !stored) return (false, $"{reg.Status} {reg.Reason}: rest sample {(rest is null ? "missing" : "not the stored mesh")}");
            int key = FarVertex(rest);
            IEnumerable<(string, Vector3[])> Turns(IEnumerable<double> degs) => degs.Select(d => ($"{d:0.##} deg", rest.Select(p => Rz(d, p)).ToArray()));
            var c = Cover(reg, Turns(Enumerable.Range(0, 9001).Select(i => -45 + i * 0.01)), key);
            var ctl = Cover(reg, Turns(new[] { -60.0, -50, 50, 60 }), key);
            return (reg.Status == "built" && c.Uncovered == 0 && ctl.Uncovered == 4, $"{ShowCover(c)} | control {ctl.Uncovered}/4 uncovered ({ctl.FirstUncovered})");
        });

        // ================= SL-3: two-hinge chain (K-M8) =================
        Case(check, "SL-3 K-M8 two-hinge chain (+-30 deg each, Upper about +Z through the origin, Lower about +Z through (50,0,0) on Upper): every dense pose on a 0.25 deg grid over both angles lies within the certified delta of some sample; control: (+35, 0) and (0, +40) deg are not covered (rejects member deltas below the true displacement)", () =>
        {
            var nif = HingeChain();
            var m = MemberMotion.Read(nif, null, Mstt);
            var reg = MemberMotion.Region(nif, m, VolumeGeometry.Render, new MemberSweep(), null);
            var (rest, stored) = RestOf(reg, new[] { Box(0, -1, -1, 50, 1, 1), Box(50, -1, -1, 100, 1, 1) });
            if (rest is null || !stored) return (false, $"{reg.Status} {reg.Reason}: rest sample {(rest is null ? "missing" : "not the stored mesh")}");
            var restSoup = reg.Samples.First(s => s.Origin == MemberMotion.OriginRest).Soup;
            var comp = Components(restSoup);
            int upperId = comp[Enumerable.Range(0, rest.Length).First(i => Math.Abs(rest[i].X) < 1e-3)];
            bool lowerFar = Enumerable.Range(0, rest.Length).Where(i => comp[i] != upperId).All(i => rest[i].X >= 50 - 1e-3);
            var pivot = new Vector3(50, 0, 0);
            Vector3[] Pose(double a1, double a2) => rest.Select((p, i) => comp[i] == upperId ? Rz(a1, p) : Rz(a1, Rz(a2, p, pivot))).ToArray();
            int key = FarVertex(rest);
            var grid = new List<(string, Vector3[])>();
            for (int i = 0; i <= 240; i++) for (int j = 0; j <= 240; j++) { double a = -30 + i * 0.25, b = -30 + j * 0.25; grid.Add(($"({a:0.##}, {b:0.##}) deg", Pose(a, b))); }
            var c = Cover(reg, grid, key);
            var ctl = Cover(reg, new[] { ("(35, 0)", Pose(35, 0)), ("(0, 40)", Pose(0, 40)) }, key);
            return (reg.Status == "built" && lowerFar && c.Uncovered == 0 && ctl.Uncovered == 2, $"{ShowCover(c)} | control {ctl.Uncovered}/2 uncovered ({ctl.FirstUncovered}); lower body split {lowerFar}");
        });

        // ================= SL-4: transition blend (K-M1) =================
        Case(check, "SL-4 K-M1 two-state transition blend (N1 turns 0 -> 60 deg, N2 slides x 20 -> 0, one weight w on B): every dense pose R(theta)(Part + (w x, 0, 0)), theta the nlerp angle of (phi, 0, w) or the slerp angle phi (1 - w), over phi 0..60 deg by 1, x 0..20 by 1, w 0..1 by 0.02, lies within the certified delta of some sample; control: Part turned 75 deg at radius 50 is not covered (rejects member deltas below the true displacement)", () =>
        {
            var (nif, read) = KM1();
            var m = MemberMotion.Read(nif, read, Mstt);
            var reg = MemberMotion.Region(nif, m, VolumeGeometry.Render, new MemberSweep(), read);
            var part = Cube(50, 0, 0, 1).Vertices;
            bool layout = reg.Samples.Any(s => s.Soup.Vertices.Length == part.Length && s.Soup.Vertices.Select((v, i) => Vector3.Distance(v, part[i])).Max() <= CoverTol);
            Vector3[] Pose(double theta, double shift) => part.Select(p => Rz(theta, p + new Vector3((float)shift, 0, 0))).ToArray();
            int key = FarVertex(part);
            var dense = new List<(string, Vector3[])>();
            for (int a = 0; a <= 60; a++) for (int x = 0; x <= 20; x++) for (int k = 0; k <= 50; k++)
            {
                double w = k / 50.0;
                dense.Add(($"nlerp phi {a} x {x} w {w:0.##}", Pose(NlerpDeg(a, w), w * x)));
                dense.Add(($"slerp phi {a} x {x} w {w:0.##}", Pose(a * (1 - w), w * x)));
            }
            var c = Cover(reg, dense, key);
            var ctl = Cover(reg, new[] { ("75 deg", Pose(75, 0)) }, key);
            return (reg.Status == "built" && layout && c.Uncovered == 0 && ctl.Uncovered == 1, $"{ShowCover(c)} | control {ctl.Uncovered}/1 uncovered ({ctl.FirstUncovered}); stored layout {layout}");
        });
    }
}
