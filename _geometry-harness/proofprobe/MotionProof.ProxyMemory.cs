// Known answers for the discovery proxy's memory (task 2026-10-11-swept-build-retains-samples, option 1, 2026-10-14). overlapprobe's
// --motion swept proxy reads only each sample's box and delta, but the region it read kept every posed sample. P2/P4: the proxy of a
// non-converging rig of 20,164 vertices (clip-only path P2, member path P4) completes in a child process under a 512 MiB managed-heap
// cap with the uncapped answer, and the uncapped child's peak private bytes stay under 1 GiB (red while the proxy keeps every sample).
// P6: the proxy equals the boxes built from MemberMotion.DiscoveryRegion's samples, bit for bit, on clip-only and member rigs,
// converged, not converged and refused (green; guards the manifest).
using System.Diagnostics;
using System.Numerics;
using HousecarlCore;
using SkyWright.Engine;
using NiflySharp.Enums;

static partial class MotionProof
{
    /// <summary>P4's member rig: K-M1's two-state blend graph with the 20,164-vertex grid as the part.</summary>
    static (byte[] Nif, Func<string, byte[]?> Read) ProxyMemberRig(int grid)
    {
        var nif = ChainNif(@"Lab\PM4.hkx", new[] { ("Part", "N2", Grid(grid), false) },
                           new[] { ("SeqA", new[] { TurnA }, CycleType.CYCLE_CLAMP), ("SeqB", new[] { SlideB }, CycleType.CYCLE_CLAMP) });
        return (nif, Files(GraphFiles("PM4", new[] { new FxState("A", "sequence", new[] { "SeqA" }), new FxState("B", "sequence", new[] { "SeqB" }) })));
    }

    static string ProxyLine(DiscoveryProxyBoxes p) =>
        $"{p.Status}|{p.Reason}|{p.SampleCount}|{Bits(p.Box)}|{(p.Soup is null ? "-" : SoupDigest(p.Soup))}";

    static string SoupDigest(TriangleSoup s)
    {
        using var h = System.Security.Cryptography.IncrementalHash.CreateHash(System.Security.Cryptography.HashAlgorithmName.SHA256);
        foreach (var v in s.Vertices) { h.AppendData(BitConverter.GetBytes(v.X)); h.AppendData(BitConverter.GetBytes(v.Y)); h.AppendData(BitConverter.GetBytes(v.Z)); }
        foreach (var i in s.Indices) h.AppendData(BitConverter.GetBytes(i));
        return $"{s.Vertices.Length}v{s.Indices.Length}i:{Convert.ToHexString(h.GetHashAndReset())[..16]}";
    }

    /// <summary>Child mode (proofprobe --proxy-memory-child clip|member): the P2/P4 proxy; one line of
    /// "status|reason|samples|boxbits|soup|peakPrivate"; exit 0, or 3 with "OutOfMemory|...|peakPrivate" when the heap ran out.</summary>
    public static int ProxyMemoryChild(string[] args)
    {
        bool member = args.Length > 1 && args[1] == "member";
        try
        {
            DiscoveryProxyBoxes p;
            if (member)
            {
                var (nif, read) = ProxyMemberRig(MemoryGrid);
                p = MemberMotion.DiscoveryProxy(nif, MemberMotion.Read(nif, read, Mstt), VolumeGeometry.Render, new MemberSweep(1e-3, 4096), read);
            }
            else
            {
                var nif = MemoryRig();
                p = MemberMotion.DiscoveryProxy(nif, MemberMotion.Read(nif, null, null), VolumeGeometry.Render, new MemberSweep(1e-3, 4096), null);
            }
            using var proc = Process.GetCurrentProcess();
            Console.WriteLine($"{ProxyLine(p)}|{proc.PeakPagedMemorySize64}");
            return 0;
        }
        catch (OutOfMemoryException)
        {
            using var proc = Process.GetCurrentProcess();
            Console.WriteLine($"OutOfMemory|-|-|-|-|{proc.PeakPagedMemorySize64}");
            return 3;
        }
    }

    static (int Exit, string Line, string Err) RunProxyChild(string mode, string? heapLimit)
    {
        var psi = new ProcessStartInfo(Environment.ProcessPath!, $"--proxy-memory-child {mode}")
        { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        if (heapLimit is not null) psi.Environment["DOTNET_GCHeapHardLimit"] = heapLimit;
        using var p = Process.Start(psi)!;
        var outTask = p.StandardOutput.ReadToEndAsync(); var errTask = p.StandardError.ReadToEndAsync();
        if (!p.WaitForExit(600_000)) { p.Kill(true); return (-1, "timeout after 600 s", ""); }
        var line = outTask.Result.Split('\n', StringSplitOptions.RemoveEmptyEntries).LastOrDefault()?.Trim() ?? "";
        var err = errTask.Result.Trim();
        return (p.ExitCode, line, err.Length > 200 ? err[..200] : err);
    }

    /// <summary>The proxy as overlapprobe built it before the fix: every sample of DiscoveryRegion, boxed from its soup.</summary>
    static DiscoveryProxyBoxes ProxyOracle(byte[] nif, MemberModel model, MemberSweep sweep, Func<string, byte[]?>? read)
    {
        var region = MemberMotion.DiscoveryRegion(nif, model, VolumeGeometry.Render, sweep, read);
        if (region.Status != "built") return new(region.Status, region.Reason, null, Aabb.Empty, 0, region.MaxDelta, region.Converged, region.Animations);
        var v = new List<Vector3>(); var idx = new List<int>();
        var total = Aabb.Empty;
        foreach (var smp in region.Samples)
        {
            var lo = smp.Soup.Vertices.Aggregate(new Vector3(float.MaxValue), Vector3.Min) - new Vector3((float)smp.Delta);
            var hi = smp.Soup.Vertices.Aggregate(new Vector3(float.MinValue), Vector3.Max) + new Vector3((float)smp.Delta);
            int b0 = v.Count;
            for (int k = 0; k < 8; k++) v.Add(new((k & 1) == 0 ? lo.X : hi.X, (k & 2) == 0 ? lo.Y : hi.Y, (k & 4) == 0 ? lo.Z : hi.Z));
            foreach (var t in new[] { 0,2,1, 1,2,3, 4,5,6, 5,7,6, 0,1,4, 1,5,4, 2,6,3, 3,6,7, 0,4,2, 2,4,6, 1,3,5, 3,7,5 }) idx.Add(b0 + t);
            total = total.Add(lo).Add(hi);
        }
        return new("built", null, new TriangleSoup(v.ToArray(), idx.ToArray()), total, region.Samples.Count, region.MaxDelta, region.Converged, region.Animations);
    }

    public static void RunProxyMemory(Action<string, bool, string> check)
    {
        foreach (var (id, mode, expect) in new[] { ("P2", "clip", "built"), ("P4", "member", "refused") })
            Case(check, $"proxy memory {id} ({mode} path): the discovery proxy of a non-converging 20,164-vertex rig ({expect}) completes under a 512 MiB heap cap with the uncapped answer; uncapped peak private < 1 GiB", () =>
            {
                var free = RunProxyChild(mode, null);
                var capped = RunProxyChild(mode, MemoryCap);
                var f = free.Line.Split('|'); var c = capped.Line.Split('|');
                if (free.Exit != 0 || f.Length < 6) return (false, $"uncapped child exit {free.Exit} '{free.Line}' {free.Err}");
                long freePeak = long.Parse(f[^1]);
                string answer(string[] x) => string.Join("|", x[..^1]);
                bool cappedOk = capped.Exit == 0 && c.Length >= 6 && answer(c) == answer(f);
                bool ok = f[0] == expect && cappedOk && freePeak < MemoryPeakLine;
                string cappedText = capped.Exit == 0 ? $"capped: answer {(answer(c) == answer(f) ? "equal" : "DIFFERS")}"
                    : capped.Line.StartsWith("OutOfMemory") ? $"capped child ran out of memory under {MemoryCap} (exit {capped.Exit}, peak private {c[^1]} B)"
                    : $"capped child exit {capped.Exit} '{capped.Line}' {capped.Err}";
                return (ok, $"uncapped: {answer(f)}, peak private {freePeak} B (line {MemoryPeakLine}); {cappedText}");
            });

        // P6: the proxy equals the pre-fix construction over DiscoveryRegion's samples, bit for bit, and carries its facts
        var clip = MultiClipRig(Grid(6), new Vector3(3, -2, 1), [("Turn", Xyz(NoKeys(), NoKeys(), FG(KeyType.LINEAR_KEY, FK(0, 0), FK(1, Pi / 2))))]);
        var three = MultiClipRig(Grid(5), new Vector3(2, 1, 0),
        [
            ("Spin", Xyz(NoKeys(), NoKeys(), FG(KeyType.LINEAR_KEY, FK(0, 0), FK(1, Pi / 2)))),
            ("Tilt", Xyz(FG(KeyType.LINEAR_KEY, FK(0, 0), FK(1, Pi / 3)), NoKeys(), NoKeys())),
        ]);
        var (km1, km1Read) = KM1();
        var (pm, pmRead) = ProxyMemberRig(4);
        var rigs = new (string Name, byte[] Nif, Func<string, byte[]?>? Read, MotionPlacement? Placement, MemberSweep Sweep, string Expect)[]
        {
            ("clip converged", clip, null, null, new MemberSweep(0.5, 4096), "built"),
            ("clip not converged", clip, null, null, new MemberSweep(1e-3, 64), "built"),
            ("two clips", three, null, null, new MemberSweep(0.5, 4096), "built"),
            ("K-M12 tier 1", KM12(), null, Mstt, new MemberSweep(), "built"),
            ("K-M1 blend (not converged at 4,096)", km1, km1Read, Mstt, new MemberSweep(), "refused"),
            ("K-M1 blend at 64", km1, km1Read, Mstt, new MemberSweep(0.5, 64), "refused"),
            ("grid blend at 64", pm, pmRead, Mstt, new MemberSweep(0.5, 64), "refused"),
            ("K-M7 hinge swing", HingeRod(0f, -30, 45), null, Mstt, new MemberSweep(), "built"),
            ("K-M8 hinge chain (not converged at 4,096)", HingeChain(), null, Mstt, new MemberSweep(), "refused"),
            ("K-M7c pinned animated hinge", AnimatedHingeRod(), null, Mstt, new MemberSweep(), "built"),
        };
        foreach (var r in rigs)
            Case(check, $"proxy memory P6: proxy = boxes of DiscoveryRegion's samples, bit for bit, with its count, width, convergence and animations ({r.Name})", () =>
            {
                var model = MemberMotion.Read(r.Nif, r.Read, r.Placement);
                var p = MemberMotion.DiscoveryProxy(r.Nif, model, VolumeGeometry.Render, r.Sweep, r.Read);
                var o = ProxyOracle(r.Nif, MemberMotion.Read(r.Nif, r.Read, r.Placement), r.Sweep, r.Read);
                bool same = ProxyLine(p) == ProxyLine(o) && BitConverter.DoubleToInt64Bits(p.MaxDelta) == BitConverter.DoubleToInt64Bits(o.MaxDelta)
                    && p.Converged == o.Converged && p.Animations.Select(a => $"{a.Id} {a.Cycle}").SequenceEqual(o.Animations.Select(a => $"{a.Id} {a.Cycle}"));
                return (same && p.Status == r.Expect && (p.Status != "built" || p.SampleCount > 2),
                    $"proxy {ProxyLine(p)} max {p.MaxDelta} conv {p.Converged}; oracle {ProxyLine(o)} max {o.MaxDelta} conv {o.Converged}");
            });
    }
}
