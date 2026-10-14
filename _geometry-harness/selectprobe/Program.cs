// SELECTPROBE — which installed plugins one new mod should be evaluated against (roadmap 4.26, part 1).
//
// Input: a newcomer (a mod folder, or plugin files) and an installed order (an MO2 profile, read by path and never
// switched, or a --order candidate file). Output: selection.json — the candidates with their evidence per kind, the
// plugins left out with the reason for each, the newcomer's dependencies, the plugins that leave the order with it,
// the patches packaged in its folder (set aside, never evidence), asset-only folders sharing its paths, what was not
// measured, and the cost. The rule is _shared/NewcomerSelection.cs; read its header before reading a result.
//
// usage: selectprobe --newcomer PATH [--newcomer PATH ...] [--instance DIR] [--profile NAME] [--game DIR]
//                    [--order FILE] [--out DIR] [--no-reverse-links]
//        selectprobe --batch FILE.jsonl [same order flags] --out DIR      many newcomers against one indexed order
//        selectprobe --selftest                                           synthetic plugins with known answers
//
//   --newcomer PATH     a mod folder (its plugins, loose files and BSAs) or a plugin file; repeat for several files
//                       of one newcomer, or for further folders of one mod (the first folder gives the plugins, the
//                       others only assets). An enabled folder of the order that ships a losing copy of a newcomer
//                       plugin is taken as a further folder automatically. A newcomer already in the order is taken
//                       out of it first, with everything that masters it, and the output lists what left.
//   --batch FILE        JSON lines {"id": "...", "newcomer": ["PATH", ...]}; one <id>.json per line in --out. A line
//                       whose <id>.json already exists is skipped (resume). A refusal is written as the line's result.
//   --no-reverse-links  do not read installed plugins' form links (the noted reverseFormLinks becomes null; faster).
//   --stop-file PATH    batch only, repeatable: before each newcomer, if any PATH exists, stop there (exit 3); a rerun
//                       resumes.
//
// Exit codes: 0 selection written (batch: every line has a result), 2 refused, 3 stopped by --stop-file, 1 crashed. Read-only on the instance.

using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using HarnessShared;
using HousecarlCore;
using HousecarlCore.Progress;

if (args.Length > 0 && args[0] == "--selftest") return SelfTest.Run();

using var __run = RunLog.BeginProcess("selectprobe", args);

string instance = HarnessTarget.Instance, game = HarnessTarget.Game, profile = HarnessTarget.Profile;
string? orderFile = null, batchFile = null;
var stopFiles = new List<string>();
string outDir = Path.Combine(Directory.GetCurrentDirectory(), "selectprobe-out");
var newcomer = new List<string>();
bool reverseLinks = true;
for (int i = 0; i < args.Length; i++)
{
    if (args[i] == "--newcomer" && i + 1 < args.Length) newcomer.Add(args[++i]);
    else if (args[i] == "--instance" && i + 1 < args.Length) instance = args[++i];
    else if (args[i] == "--profile" && i + 1 < args.Length) profile = args[++i];
    else if (args[i] == "--game" && i + 1 < args.Length) game = args[++i];
    else if (args[i] == "--order" && i + 1 < args.Length) orderFile = args[++i];
    else if (args[i] == "--batch" && i + 1 < args.Length) batchFile = args[++i];
    else if (args[i] == "--out" && i + 1 < args.Length) outDir = args[++i];
    else if (args[i] == "--no-reverse-links") reverseLinks = false;
    else if (args[i] == "--stop-file" && i + 1 < args.Length) stopFiles.Add(args[++i]);
    else { Console.Error.WriteLine($"unknown option '{args[i]}'. See the header of selectprobe/Program.cs for the flag list."); return 2; }
}
if ((newcomer.Count == 0) == (batchFile is null))
{ Console.Error.WriteLine("Name the newcomer with --newcomer, or many with --batch FILE — exactly one of the two."); return 2; }

instance = HarnessTarget.Require(instance, "MO2 instance directory", "instance", "--instance");
game = HarnessTarget.Require(game, "game directory", "game", "--game");
profile = HarnessTarget.Require(profile, "MO2 profile name", "profile", "--profile");

return Probe.Run(instance, profile, game, orderFile, outDir, reverseLinks,
                 batchFile is null ? new List<(string, List<string>)> { ("selection", newcomer) } : Probe.ReadBatch(batchFile),
                 batchFile is not null, __run, stopFiles: stopFiles);

static class Probe
{
    public static List<(string Id, List<string> Paths)> ReadBatch(string file)
    {
        var list = new List<(string, List<string>)>();
        foreach (var line in File.ReadLines(file))
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            using var doc = JsonDocument.Parse(line);
            var id = doc.RootElement.GetProperty("id").GetString()!;
            var paths = doc.RootElement.GetProperty("newcomer").EnumerateArray().Select(e => e.GetString()!).ToList();
            list.Add((id, paths));
        }
        return list;
    }

    public static int Run(string instance, string profile, string game, string? orderFile, string outDir, bool reverseLinks,
                          List<(string Id, List<string> Paths)> jobs, bool batch, Run? run, TextWriter? log = null, IReadOnlyList<string>? stopFiles = null)
    {
        log ??= Console.Out;
        var wall = Stopwatch.StartNew();
        var io0 = Cost.ReadBytes();
        string profileDir = Path.Combine(instance, "profiles", profile), modsDir = Path.Combine(instance, "mods");
        string overwriteDir = Path.Combine(instance, "overwrite"), dataDir = Path.Combine(game, "Data");
        Directory.CreateDirectory(outDir);

        var cfg = HarnessOrderCli.Resolve(profile, profileDir, modsDir, dataDir, overwriteDir, game, orderFile, null, null);
        if (cfg.Refused) { Console.Error.WriteLine($"REFUSED: {cfg.RefusedReason}"); return 2; }
        double resolveSeconds = wall.Elapsed.TotalSeconds;
        log.WriteLine($"selectprobe — {cfg.Source}: {cfg.OrderedPaths.Count} plugins, {cfg.EnabledMods.Count} enabled folders, {cfg.Archives.Archives.Count} archives (resolved in {resolveSeconds:0.0}s)");
        run?.Feature("plugins", cfg.OrderedPaths.Count, "plugins").Feature("newcomers", jobs.Count, "newcomers");

        var sel = new Selector(cfg, modsDir, dataDir, overwriteDir, reverseLinks);

        // resume: a line whose result exists is done
        var todo = jobs.Where(j => !batch || !File.Exists(Path.Combine(outDir, Safe(j.Id) + ".json"))).ToList();
        if (batch && todo.Count < jobs.Count) log.WriteLine($"resume: {jobs.Count - todo.Count} of {jobs.Count} already have a result and are skipped");

        // the newcomers first, so the asset index knows every path it is asked about
        run?.Stage("newcomers", "choosing the newcomers' plugins and reading their asset paths", todo.Count, "newcomers");
        var specs = new List<NewcomerSpec>();
        var prepTimes = new Dictionary<string, double>();
        foreach (var (id, paths) in todo)
        {
            var t = Stopwatch.StartNew();
            specs.Add(sel.Prepare(id, paths));
            prepTimes[id] = t.Elapsed.TotalSeconds;
            run?.Advance();
        }
        var wanted = new HashSet<string>(specs.Where(s => s.Assets is not null).SelectMany(s => s.Assets!), StringComparer.Ordinal);

        run?.Stage("index", "reading the installed plugins once", cfg.OrderedPaths.Count, "plugins");
        sel.IndexRecords(i => run?.Progress(i));
        run?.Stage("assets", "finding which folders provide the newcomers' paths", 1, "pass");
        sel.IndexAssets(wanted);
        run?.Advance();
        double indexSeconds = wall.Elapsed.TotalSeconds - resolveSeconds;
        log.WriteLine($"index: {sel.FootprintsRead} plugin footprint(s) in {sel.FootprintSeconds:0.0}s; asset index over {wanted.Count:N0} wanted path(s) in {sel.AssetSeconds:0.0}s");

        run?.Stage("select", "selecting", specs.Count, "newcomers");
        int refused = 0, done = 0;
        bool stopped = false;
        foreach (var s in specs)
        {
            if (batch && stopFiles?.FirstOrDefault(File.Exists) is { } stopFile)
            {
                stopped = true;
                log.WriteLine($"stopped: '{stopFile}' exists; {specs.Count - done} newcomer(s) left for a rerun to resume");
                break;
            }
            var t = Stopwatch.StartNew();
            object result;
            int candidates = -1;
            string? refusal = null;
            try
            {
                var tr = Stopwatch.StartNew();
                sel.LoadRecords(s);
                prepTimes[s.Id] += tr.Elapsed.TotalSeconds;
                if (s.Refused is not null) throw new RefusalException(s.Refused);
                var r = sel.Select(s);
                candidates = r.Candidates.Count;
                result = r with
                {
                    Cost = new Dictionary<string, object?>
                    {
                        ["newcomerReadSeconds"] = Math.Round(prepTimes[s.Id], 3),
                        ["selectSeconds"] = Math.Round(t.Elapsed.TotalSeconds, 3),
                        ["sharedResolveSeconds"] = Math.Round(resolveSeconds, 3),
                        ["sharedIndexSeconds"] = Math.Round(indexSeconds, 3),
                        ["sharedIndexNewcomers"] = specs.Count,
                    },
                };
            }
            catch (RefusalException ex) { refused++; refusal = ex.Message; result = new { tool = "selectprobe", refused = ex.Message, newcomer = s.Id }; }
            finally { sel.Release(s); }
            var file = Path.Combine(outDir, (batch ? Safe(s.Id) : "selection") + ".json");
            var tmp = file + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(result, SelectionRule.Json));
            File.Move(tmp, file, overwrite: true);
            done++;
            run?.Advance();
            log.WriteLine(candidates >= 0 ? $"   {s.Id}: {candidates} candidate(s) — {t.Elapsed.TotalSeconds:0.00}s"
                                          : $"   {s.Id}: REFUSED — {refusal}");
        }

        var mem = Cost.Memory();
        var summary = new Dictionary<string, object?>
        {
            ["source"] = cfg.Source, ["plugins"] = cfg.OrderedPaths.Count, ["newcomers"] = jobs.Count, ["selectedThisRun"] = done,
            ["refused"] = refused, ["stopped"] = stopped, ["wallSeconds"] = Math.Round(wall.Elapsed.TotalSeconds, 2), ["resolveSeconds"] = Math.Round(resolveSeconds, 2),
            ["footprintSeconds"] = Math.Round(sel.FootprintSeconds, 2), ["assetIndexSeconds"] = Math.Round(sel.AssetSeconds, 2),
            ["footprintsRead"] = sel.FootprintsRead, ["wantedAssetPaths"] = wanted.Count, ["reverseLinks"] = reverseLinks,
            ["bytesRead"] = io0 is null ? null : Cost.ReadBytes() - io0,
            ["peakWorkingSetBytes"] = mem.PeakWorkingSet, ["peakPrivateBytes"] = mem.PeakPrivate,
        };
        File.WriteAllText(Path.Combine(outDir, batch ? "batch-summary.json" : "cost.json"), JsonSerializer.Serialize(summary, SelectionRule.Json));
        log.WriteLine($"done: {done} selected ({refused} refused) in {wall.Elapsed.TotalSeconds:0.0}s; read {Gb(summary["bytesRead"])}; peak private {Gb(mem.PeakPrivate)}, peak working set {Gb(mem.PeakWorkingSet)}");
        run?.Tally(ok: done - refused, failed: refused);
        return stopped ? 3 : !batch && refused > 0 ? 2 : 0;
    }

    static string Gb(object? b) => b is long l ? $"{l / 1073741824.0:0.00} GiB" : "unknown";
    public static string Safe(string id) => string.Concat(id.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c));
}

static class Cost
{
    [StructLayout(LayoutKind.Sequential)]
    struct IO_COUNTERS { public ulong ReadOperationCount, WriteOperationCount, OtherOperationCount, ReadTransferCount, WriteTransferCount, OtherTransferCount; }
    [StructLayout(LayoutKind.Sequential)]
    struct PROCESS_MEMORY_COUNTERS
    {
        public uint cb, PageFaultCount;
        public UIntPtr PeakWorkingSetSize, WorkingSetSize, QuotaPeakPagedPoolUsage, QuotaPagedPoolUsage,
                       QuotaPeakNonPagedPoolUsage, QuotaNonPagedPoolUsage, PagefileUsage, PeakPagefileUsage;
    }
    [DllImport("kernel32.dll")] static extern bool GetProcessIoCounters(IntPtr h, out IO_COUNTERS c);
    [DllImport("psapi.dll")] static extern bool GetProcessMemoryInfo(IntPtr h, out PROCESS_MEMORY_COUNTERS c, uint cb);

    /// <summary>Bytes this process has read (all I/O read transfers, Windows). Null elsewhere: unknown, not zero.</summary>
    public static long? ReadBytes()
    {
        if (!OperatingSystem.IsWindows()) return null;
        return GetProcessIoCounters(Process.GetCurrentProcess().Handle, out var c) ? (long)c.ReadTransferCount : null;
    }

    public static (long? PeakWorkingSet, long? PeakPrivate) Memory()
    {
        if (!OperatingSystem.IsWindows()) return (null, null);
        var c = new PROCESS_MEMORY_COUNTERS { cb = (uint)Marshal.SizeOf<PROCESS_MEMORY_COUNTERS>() };
        return GetProcessMemoryInfo(Process.GetCurrentProcess().Handle, out c, c.cb)
            ? ((long)c.PeakWorkingSetSize.ToUInt64(), (long)c.PeakPagefileUsage.ToUInt64()) : (null, null);
    }
}
