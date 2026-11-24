// Known answers for the reach region's memory (task 2026-10-08-overlapprobe-memory-g2, lane M 2026-10-11). A reach call
// kept every posed sample of a mesh whose many large clips never converge within the sample budget, and the process ran out
// of memory under its heap cap. R1/R1b: the reach of a non-converging rig of about 20,000 vertices completes in a child
// process under a 512 MiB managed-heap cap with the uncapped box, and the uncapped child's peak private bytes stay under 1 GiB
// (red while reach keeps every sample). R2: the reach box equals the per-sample bounds of SweptGeometry.Build + Place grown by
// their deltas, bit for bit (green; guards the change). R3: on a converging rig with three clips, the whole sweep equals the
// clips swept one at a time (the per-clip reference a field check relies on; green on the base).
using System.Diagnostics;
using System.Numerics;
using HousecarlCore;
using SkyWright.Engine;
using NiflySharp;
using NiflySharp.Blocks;
using NiflySharp.Enums;
using NiflySharp.Structs;

static partial class MotionProof
{
    const int MemoryGrid = 142;   // 142 x 142 = 20,164 vertices in the moving part
    const string MemoryCap = "0x20000000";   // 512 MiB managed heap for the capped child
    const long MemoryPeakLine = 1L << 30;   // 1 GiB of peak private bytes for the uncapped child

    /// <summary>A flat grid of n x n vertices on z = 0 over [0,100]^2 (two triangles per cell).</summary>
    static TriangleSoup Grid(int n)
    {
        var v = new Vector3[n * n];
        for (int j = 0; j < n; j++) for (int i = 0; i < n; i++) v[j * n + i] = new Vector3(100f * i / (n - 1), 100f * j / (n - 1), 0);
        var ix = new List<int>();
        for (int j = 0; j + 1 < n; j++)
            for (int i = 0; i + 1 < n; i++)
            {
                int a = j * n + i, b = a + 1, c = a + n, d = c + 1;
                ix.AddRange([a, b, d, a, d, c]);
            }
        return new TriangleSoup(v, ix.ToArray());
    }

    /// <summary>Root -> "Mover" -> shape "Part"; one NiControllerSequence per clip, each driving "Mover" with its own data.</summary>
    static byte[] MultiClipRig(TriangleSoup part, Vector3 pivot, (string Name, NiTransformData Data)[] clips, float stop = 1f)
    {
        var f = new NiflySharp.NifFile(); f.Create(Ver, withRootNode: true);
        var root = f.GetRootNodes().First(); root.Flags_ui = 0xE;
        var mover = new NiNode { Name = new NiStringRef("Mover"), Flags_ui = 0xE, Scale = 1f, Translation = pivot, Rotation = Identity33() };
        root.Children.AddBlockRef(f.AddBlock(mover));
        mover.Children.AddBlockRef(f.AddBlock(Shape("Part", part)));
        var mtc = new NiMultiTargetTransformController();
        int mtcIndex = f.AddBlock(mtc);
        var mgr = new NiControllerManager();
        int mgrIndex = f.AddBlock(mgr);
        mgr.ControllerSequences ??= new();
        foreach (var (name, data) in clips)
        {
            int interp = f.AddBlock(new NiTransformInterpolator { Data = new(f.AddBlock(data)) });
            var seq = new NiControllerSequence { Name = new NiStringRef(name), CycleType = CycleType.CYCLE_CLAMP, StartTime = 0, StopTime = stop, Frequency = 1, Weight = 1 };
            seq.ControlledBlocks = new List<ControlledBlock>
            {
                new() { NodeName = new NiStringRef("Mover"), ControllerType = new NiStringRef("NiTransformController"),
                        Interpolator = new(interp), Controller = new(mtcIndex) }
            };
            seq.NumControlledBlocks = 1;
            mgr.ControllerSequences.AddBlockRef(f.AddBlock(seq));
        }
        mgr.NumControllerSequences = (uint)mgr.ControllerSequences.Count;
        mgr.NextController = new(mtcIndex);
        root.Controller = new(mgrIndex);
        return Save(f, "multi-clip rig");
    }

    static byte[] MemoryRig() => MultiClipRig(Grid(MemoryGrid), Vector3.Zero,
        [("Turn", Xyz(NoKeys(), NoKeys(), FG(KeyType.LINEAR_KEY, FK(0, 0), FK(1, Pi / 2))))]);

    /// <summary>R1's options: a delta target no interval of a 100-unit quarter turn meets within 4,096 samples.</summary>
    static ReachOptions MemoryOptions(bool rules) => new(1e-3, 4096, rules ? new ReachRules(Particles: true) : null);

    static string Bits(Aabb b) => b.IsEmpty ? "empty" : string.Join(",", new[] { b.Min.X, b.Min.Y, b.Min.Z, b.Max.X, b.Max.Y, b.Max.Z }
        .Select(x => BitConverter.SingleToInt32Bits(x).ToString("X8")));

    /// <summary>Child mode (proofprobe --reach-memory-child rules|default): the R1 reach on the memory rig; one line of
    /// "status|bits|peakPrivate|peakWorkingSet"; exit 0, or 3 with "OutOfMemory|..." when the heap ran out.</summary>
    public static int ReachMemoryChild(string[] args)
    {
        bool rules = args.Length > 1 && args[1] == "rules";
        try
        {
            var nif = MemoryRig();
            var r = MeshReach.OfModel(nif, new Vec3(0, 0, 0), new Vec3(0, 0, 0), 1f, null, MemoryOptions(rules));
            using var p = Process.GetCurrentProcess();
            Console.WriteLine($"{r.Status}|{Bits(r.Box)}|{p.PeakPagedMemorySize64}|{p.PeakWorkingSet64}");
            return 0;
        }
        catch (OutOfMemoryException)
        {
            using var p = Process.GetCurrentProcess();
            Console.WriteLine($"OutOfMemory|-|{p.PeakPagedMemorySize64}|{p.PeakWorkingSet64}");
            return 3;
        }
    }

    static (int Exit, string Line, string Err) RunMemoryChild(string mode, string? heapLimit)
    {
        var psi = new ProcessStartInfo(Environment.ProcessPath!, $"--reach-memory-child {mode}")
        { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        if (heapLimit is not null) psi.Environment["DOTNET_GCHeapHardLimit"] = heapLimit;
        using var p = Process.Start(psi)!;
        var outTask = p.StandardOutput.ReadToEndAsync(); var errTask = p.StandardError.ReadToEndAsync();
        if (!p.WaitForExit(600_000)) { p.Kill(true); return (-1, "timeout after 600 s", ""); }
        var line = outTask.Result.Split('\n', StringSplitOptions.RemoveEmptyEntries).LastOrDefault()?.Trim() ?? "";
        var err = errTask.Result.Trim();
        return (p.ExitCode, line, err.Length > 200 ? err[..200] : err);
    }

    public static void RunReachMemory(Action<string, bool, string> check)
    {
        foreach (var mode in new[] { "rules", "default" })
        {
            string id = mode == "rules" ? "R1" : "R1b";
            Case(check, $"reach memory {id} ({mode} path): a non-converging 20,164-vertex rig reaches under a 512 MiB heap cap with the uncapped box; uncapped peak private < 1 GiB", () =>
            {
                var free = RunMemoryChild(mode, null);
                var capped = RunMemoryChild(mode, MemoryCap);
                var f = free.Line.Split('|'); var c = capped.Line.Split('|');
                if (free.Exit != 0 || f.Length < 4)
                    return (false, $"uncapped child exit {free.Exit} '{free.Line}' {free.Err}");
                long freePeak = long.Parse(f[2]);
                bool cappedOk = capped.Exit == 0 && c.Length >= 4 && c[0] == f[0] && c[1] == f[1];
                bool ok = cappedOk && freePeak < MemoryPeakLine;
                string cappedText = capped.Exit == 0 ? $"capped: {c[0]}, box {(c.Length > 1 && c[1] == f[1] ? "equal" : "DIFFERS")}"
                    : capped.Line.StartsWith("OutOfMemory") ? $"capped child ran out of memory under {MemoryCap} (exit {capped.Exit}, peak private {c[2]} B)"
                    : $"capped child exit {capped.Exit} '{capped.Line}' {capped.Err}";
                return (ok, $"uncapped: {f[0]}, peak private {freePeak} B (line {MemoryPeakLine}); {cappedText}");
            });
        }

        // R2: the reach box is the union of Build + Place's per-sample bounds grown by their deltas (MeshReach.Grow's definition)
        static Aabb GrowLikeReach(Aabb b, double d)
        {
            if (b.IsEmpty || d <= 0) return b;
            float g = (float)d;
            if (g < d) g = MathF.BitIncrement(g);
            return new Aabb(new Vec3(b.Min.X - g, b.Min.Y - g, b.Min.Z - g), new Vec3(b.Max.X + g, b.Max.Y + g, b.Max.Z + g), false);
        }
        static Aabb Oracle(byte[] nif, IEnumerable<string>? ids, double delta, int budget, Vec3 pos, Vec3 rot, float scale)
        {
            var model = NifMotion.Read(nif, null);
            var region = SweptGeometry.Build(nif, model, ids, VolumeGeometry.Render, new MotionOptions(delta, budget), null);
            var box = Aabb.Empty;
            foreach (var s in SweptGeometry.Place(region, pos, rot, scale).Samples) box = box.Union(GrowLikeReach(SpatialOverlap.SoupBounds(s.Soup), s.Delta));
            return box;
        }
        var small = MultiClipRig(Grid(6), new Vector3(3, -2, 1), [("Turn", Xyz(NoKeys(), NoKeys(), FG(KeyType.LINEAR_KEY, FK(0, 0), FK(1, Pi / 2))))]);
        var placements = new (string Name, Vec3 Pos, Vec3 Rot, float Scale)[]
        {
            ("origin", new Vec3(0, 0, 0), new Vec3(0, 0, 0), 1f),
            ("placed", new Vec3(1234.5f, -876.25f, 40.125f), new Vec3(0.3f, -0.2f, 1.1f), 1.7f),
            ("shrunk", new Vec3(-50f, 20f, -3f), new Vec3(0f, 0.5f, -2.0f), 0.45f),
        };
        foreach (var (delta, budget) in new[] { (0.5, 4096), (1e-3, 64) })
            foreach (var pl in placements)
                foreach (bool rules in new[] { false, true })
                    Case(check, $"reach memory R2: reach box = Build+Place per-sample bounds grown by delta, bit for bit ({pl.Name}, delta {delta}, budget {budget}, {(rules ? "rules" : "default")} path)", () =>
                    {
                        var r = MeshReach.OfModel(small, pl.Pos, pl.Rot, pl.Scale, null, new ReachOptions(delta, budget, rules ? new ReachRules(Particles: true) : null));
                        var o = Oracle(small, null, delta, budget, pl.Pos, pl.Rot, pl.Scale);
                        return (r.Status is "certified" or "uncertified" && Bits(r.Box) == Bits(o), $"{r.Status}: reach {Bits(r.Box)}; oracle {Bits(o)}");
                    });

        // R3: a converging three-clip rig: the whole sweep equals the clips swept one at a time, sample for sample, and the reach box
        // equals the union of the per-clip oracle boxes (the per-clip decomposition a field reference relies on)
        var three = MultiClipRig(Grid(5), new Vector3(2, 1, 0),
        [
            ("Spin", Xyz(NoKeys(), NoKeys(), FG(KeyType.LINEAR_KEY, FK(0, 0), FK(1, Pi / 2)))),
            ("Tilt", Xyz(FG(KeyType.LINEAR_KEY, FK(0, 0), FK(1, Pi / 3)), NoKeys(), NoKeys())),
            ("Roll", Xyz(NoKeys(), FG(KeyType.LINEAR_KEY, FK(0, 0), FK(1, -Pi / 4)), NoKeys())),
        ]);
        Case(check, "reach memory R3: three converging clips — whole Build = per-clip Builds concatenated (ids, times, deltas, vertex bits); reach box = union of per-clip oracles", () =>
        {
            var model = NifMotion.Read(three, null);
            var whole = SweptGeometry.Build(three, model, null, VolumeGeometry.Render, new MotionOptions(0.5, 4096), null);
            var ids = whole.Samples.Select(s => s.AnimationId).Distinct().ToList();   // the whole sweep's own clip order
            bool sameSet = ids.OrderBy(x => x, StringComparer.Ordinal).SequenceEqual(model.Animations.Select(a => a.Id).OrderBy(x => x, StringComparer.Ordinal));
            var parts = ids.Select(id => SweptGeometry.Build(three, model, [id], VolumeGeometry.Render, new MotionOptions(0.5, 4096), null)).ToList();
            var cat = parts.SelectMany(p => p.Samples).ToList();
            bool samplesEqual = whole.Status == "built" && parts.All(p => p.Status == "built") && whole.Converged && parts.All(p => p.Converged)
                && whole.Samples.Count == cat.Count && whole.Samples.Zip(cat).All(x => x.First.AnimationId == x.Second.AnimationId
                    && TopologyFloat(x.First.Time, x.Second.Time) && TopologyDouble(x.First.Delta, x.Second.Delta)
                    && TopologyVertices(x.First.Soup.Vertices, x.Second.Soup.Vertices) && x.First.Soup.Indices.SequenceEqual(x.Second.Soup.Indices));
            bool refined = parts.All(p => p.Samples.Count > 2);
            var pos = new Vec3(500.25f, -12f, 7.5f); var rot = new Vec3(0.1f, 0.2f, -0.7f); float scale = 1.3f;
            var reach = MeshReach.OfModel(three, pos, rot, scale, null, new ReachOptions(0.5, 4096));
            var union = ids.Aggregate(Aabb.Empty, (acc, id) => acc.Union(Oracle(three, [id], 0.5, 4096, pos, rot, scale)));
            return (ids.Count == 3 && sameSet && samplesEqual && refined && Bits(reach.Box) == Bits(union),
                $"clips {ids.Count}, samples whole {whole.Samples.Count} / per clip {string.Join("+", parts.Select(p => p.Samples.Count))}, equal {samplesEqual}; reach {Bits(reach.Box)}, union {Bits(union)}");
        });
    }
}
