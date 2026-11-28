using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Mutagen.Bethesda.Plugins;
using Xunit;
using Xunit.Abstractions;

namespace SkyWright.Engine.Tests;

/// <summary>A test that runs only when WR_LAB_ORDER names a file of ordered plugin paths; otherwise it is reported as
/// skipped, so the default run of this assembly gains no failing test.</summary>
public sealed class LabOrderFactAttribute : FactAttribute
{
    public LabOrderFactAttribute()
    {
        if (Environment.GetEnvironmentVariable("WR_LAB_ORDER") is not { Length: > 0 })
            Skip = "set WR_LAB_ORDER (a file of ordered plugin paths, one per line) and WR_LAB_CACHE (a cache folder) to run the shared world read on a real load order";
    }
}

/// <summary>The shared world read on a real load order against the tests' whole-order replay, cell by cell, with its
/// times and peak memory. WR_LAB_ORDER names a file of ordered plugin paths (one per line), WR_LAB_CACHE the cache
/// folder, WR_LAB_REPORT (optional) a file the printed lines are written to. Only reads the plugins.</summary>
[Trait("tier", "real-run")]
public sealed class WorldReadLabTests
{
    readonly ITestOutputHelper _out;
    readonly List<string> _lines = new();
    public WorldReadLabTests(ITestOutputHelper o) => _out = o;

    void Say(FormattableString line)
    {
        var text = line.ToString(CultureInfo.InvariantCulture);
        _lines.Add(text); _out.WriteLine(text);
        if (Environment.GetEnvironmentVariable("WR_LAB_REPORT") is { Length: > 0 } report) File.AppendAllText(report, text + Environment.NewLine);
    }

    static double PeakMiB() { using var p = Process.GetCurrentProcess(); p.Refresh(); return p.PeakWorkingSet64 / 1048576.0; }
    static string Digest(IEnumerable<string> lines) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(string.Join("\n", lines))))[..16].ToLowerInvariant();

    [LabOrderFact]
    public void The_world_read_equals_the_whole_order_replay_on_a_real_load_order()
    {
        var paths = File.ReadAllLines(Environment.GetEnvironmentVariable("WR_LAB_ORDER")!).Where(l => l.Trim().Length > 0).Select(l => l.Trim()).ToList();
        var cache = Environment.GetEnvironmentVariable("WR_LAB_CACHE") is { Length: > 0 } c ? c
            : throw new Xunit.Sdk.XunitException("set WR_LAB_CACHE to the world read's cache folder");
        Say($"order: {paths.Count} plugins, {paths.Sum(p => new FileInfo(p).Length) / 1048576.0:0.0} MiB; cache {cache}; files present before: {(Directory.Exists(cache) ? Directory.GetFiles(cache, "*.swr").Length : 0)}");

        var sw = Stopwatch.StartNew();
        var read = WorldRead.Open(paths, cache);
        double open = sw.Elapsed.TotalSeconds;
        Say($"Open: {open:0.000} s (hashing {read.Timings.HashSeconds:0.000} s, preparing {read.Timings.PrepareSeconds:0.000} s, loading {read.Timings.LoadSeconds:0.000} s); prepared {read.Prepared}, reused {read.Reused}");

        var tamriel = new FormKey(ModKey.FromFileName("Skyrim.esm"), 0x3C);
        sw.Restart();
        var t = read.Exterior(tamriel, 5, -2, 2, includePersistent: true);
        double answer = sw.Elapsed.TotalSeconds;
        Say($"Tamriel 5,-2 ring 2 with the persistent cell: {answer:0.000} s; cells {t.Cells.Count}, winners {t.Winners.Count} ({t.Winners.Count(w => w.Persistent)} persistent), moved out {t.MovedOut.Count}, deleted {t.Deleted.Count}, unplaced {t.Unplaced.Count}");
        Say($"Open plus the Tamriel answer: {open + answer:0.000} s; peak working set so far {PeakMiB():0} MiB");
        sw.Restart();
        var t2 = read.Exterior(tamriel, 5, -2, 2, includePersistent: true);
        Say($"the same answer again: {sw.Elapsed.TotalSeconds:0.000} s, {t2.Winners.Count} winners");

        sw.Restart();
        var oracle = WorldReadOracle.Replay(paths);
        Say($"whole-order replay: {sw.Elapsed.TotalSeconds:0.000} s, {oracle.Sightings.Count} references, {oracle.ByCell.Count} cells");

        sw.Restart();
        var allCells = read.AllCells.Select(x => x.Cell).Concat(oracle.ByCell.Keys).Distinct().OrderBy(x => x.ToString(), StringComparer.Ordinal).ToList();
        var readerDigests = new List<string>(); var oracleDigests = new List<string>(); var differing = new List<string>();
        long rw = 0, rm = 0, rd = 0, ru = 0, ow = 0, om = 0, od = 0, ou = 0;
        foreach (var cell in allCells)
        {
            var have = WorldReadOracle.Lines(read.Cell(cell));
            var want = oracle.ByCell.TryGetValue(cell, out var cands) ? oracle.Expected(cands, s => s.Cell == cell) : new List<string>();
            foreach (var l in have) { if (l[0] == 'W') rw++; else if (l[0] == 'M') rm++; else if (l[0] == 'D') rd++; else ru++; }
            foreach (var l in want) { if (l[0] == 'W') ow++; else if (l[0] == 'M') om++; else if (l[0] == 'D') od++; else ou++; }
            string hd = Digest(have), wd = Digest(want);
            readerDigests.Add($"{cell}:{hd}"); oracleDigests.Add($"{cell}:{wd}");
            if (hd != wd && differing.Count < 5)
                differing.Add($"{cell}: reader {have.Count} lines, replay {want.Count}; first missing [{want.Except(have).FirstOrDefault()}]; first extra [{have.Except(want).FirstOrDefault()}]");
            else if (hd != wd) differing.Add(cell.ToString());
        }
        Say($"equality over {allCells.Count} cells: {sw.Elapsed.TotalSeconds:0.000} s");
        Say($"reader: winners {rw}, moved out {rm}, deleted {rd}, unplaced {ru}; digest {Digest(readerDigests)}");
        Say($"replay: winners {ow}, moved out {om}, deleted {od}, unplaced {ou}; digest {Digest(oracleDigests)}");
        Say($"cells whose answers differ: {differing.Count}");
        foreach (var d in differing.Take(5)) Say($"  {d}");
        Say($"peak working set at the end {PeakMiB():0} MiB");
        Assert.True(differing.Count == 0, $"the world read differs from the whole-order replay in {differing.Count} cells; first: {differing.FirstOrDefault()}");
    }
}
