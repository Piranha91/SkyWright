// HousecarlCore has its own PluginUnreadableException; this probe means the harness one (_shared/PluginOpen.cs).
using PluginUnreadableException = HarnessShared.PluginUnreadableException;
using System.Diagnostics;
using System.Text.Json;
using HarnessShared;
using HousecarlCore;
using HousecarlCore.Progress;
using SkyWright.Engine;

// strandprobe — objects another mod's navmesh leaves out of reach (the geometry.stranded-object check).
//
// An interior object is stranded when it lies beyond reach of the winning navmesh while it is within reach of the navmesh its
// own plugin was made with (that plugin and its declared masters: its own view), and it touches no solid object of another
// mod. The rule, its thresholds and its reasons are SkyWright.Engine.StrandedRule; the walk and the measurements are
// StrandScan.cs. Every reference the rule cannot judge is a row "not judged" with its reason; every reference without a
// row is counted under its reason in the emit's coverage.
//
// usage: strandprobe --self-test
//        strandprobe [--order CANDIDATE.json] --emit OUT.json [--base EMIT.json] [--primitive-solidity FILE] [--cells A,B]
//                    [--no-mirror] --instance DIR --profile NAME --game DIR
//   --emit     the conflict file: one STRANDED row per reported or not-judged object (Physical Holds / Unmeasured), the
//              coverage counts; beside it OUT.json.reach.json, every measured object's distances and verdict
//   --base     an earlier emit: each of its rows re-measured under this order (Predicates: Holds, Unmeasured, or Gone when
//              this order has no row for it)
//   --primitive-solidity  station Q's measured table (SkyWright.Engine.PrimitiveSolidity); without it a primitive near an
//              object is a bound, and makes the row not judged when it comes nearer than the nearest known body
//   --cells    only these cells (editor ids or FormKeys): an instrument run
//   --no-mirror  an instrument flag: judge rows the navmesh's own author had in the editor too (for measuring the reach
//              rule alone on Bethesda's files); never for a check run

using var __run = RunLog.BeginProcess("strandprobe", args);
if (args.Length == 1 && args[0] == "--self-test") return StrandSelfTest.Run();

string Instance = HarnessTarget.Instance, GamePath = HarnessTarget.Game, profileName = HarnessTarget.Profile;
string? orderFile = null, emitPath = null, basePath = null, tablePath = null; HashSet<string>? cells = null; bool mirror = true;
for (int i = 0; i < args.Length; i++)
{
    string a = args[i];
    string Next() => i + 1 < args.Length ? args[++i] : throw new ArgumentException($"{a} needs a value");
    try
    {
        switch (a)
        {
            case "--instance": Instance = Next(); break;
            case "--game": GamePath = Next(); break;
            case "--profile": profileName = Next(); break;
            case "--order": orderFile = Next(); break;
            case "--emit": emitPath = Next(); break;
            case "--base": basePath = Next(); break;
            case "--primitive-solidity": tablePath = Next(); break;
            case "--cells": cells = Next().Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToHashSet(StringComparer.OrdinalIgnoreCase); break;
            case "--no-mirror": mirror = false; break;
            default: Console.Error.WriteLine($"strandprobe: unknown argument {a}; see the usage at the top of Program.cs."); return 2;
        }
    }
    catch (ArgumentException ex) { Console.Error.WriteLine($"strandprobe: {ex.Message}."); return 2; }
}
if (emitPath is null) { Console.Error.WriteLine("strandprobe: no --emit OUT.json was given, so the rows would go nowhere; name the file to write."); return 2; }
Instance = HarnessTarget.Require(Instance, "MO2 instance directory", "instance", "--instance");
GamePath = HarnessTarget.Require(GamePath, "game directory", "game", "--game");
profileName = HarnessTarget.Require(profileName, "MO2 profile name", "profile", "--profile");
string profileDir = Path.Combine(Instance, "profiles", profileName), modsDir = Path.Combine(Instance, "mods");
string overwriteDir = Path.Combine(Instance, "overwrite"), dataDir = Path.Combine(GamePath, "Data");

IReadOnlyList<PrimitiveMeasurement>? table = null;
if (tablePath is not null)
{
    try { table = PrimitiveSolidity.Read(File.ReadAllText(tablePath)); }
    catch (Exception ex) when (ex is IOException or ArgumentException or JsonException)
    { Console.Error.WriteLine($"strandprobe: the primitive table {tablePath} could not be read ({ex.Message}); point --primitive-solidity at station Q's table."); return 2; }
}
ConflictFile? baseFile = null;
if (basePath is not null)
{
    try { baseFile = ConflictIo.Read(basePath); }
    catch (Exception ex) when (ex is IOException or JsonException or InvalidDataException)
    { Console.Error.WriteLine($"strandprobe: the base emit {basePath} could not be read ({ex.Message}); name a strandprobe emit."); return 2; }
}

var sw = Stopwatch.StartNew();
var cfg = HarnessOrderCli.Resolve(profileName, profileDir, modsDir, dataDir, overwriteDir, GamePath, orderFile, null, null);
if (cfg.Refused) { Console.Error.WriteLine(cfg.RefusedReason); return 2; }
using var assets = AssetResolver.Build(overwriteDir, modsDir, dataDir, cfg.EnabledMods, cfg.Archives.Archives);
HarnessOrderCli.Print(cfg);
var paths = cfg.OrderedPaths.ToList();
if (orderFile is null && OrderMasters.RefusalForProfile(profileDir, modsDir, dataDir, overwriteDir, paths) is { } missingMaster) { Console.Error.WriteLine(missingMaster); return 2; }

// the mod a plugin belongs to: the MO2 mod folder its file is in; a plugin of the game's Data folder (or the overwrite) by its
// name cut at " - " and "." (StrandFamily)
var folderOf = paths.ToDictionary(p => Path.GetFileName(p), p =>
{
    var dir = Path.GetDirectoryName(Path.GetFullPath(p)) ?? "";
    var parent = Path.GetDirectoryName(dir) ?? "";
    return string.Equals(Path.GetFullPath(parent).TrimEnd('\\'), Path.GetFullPath(modsDir).TrimEnd('\\'), StringComparison.OrdinalIgnoreCase)
        ? "mod folder " + Path.GetFileName(dir) : StrandFamily.ByName(Path.GetFileName(p));
}, StringComparer.OrdinalIgnoreCase);
var meshCache = new Dictionary<string, byte[]?>(StringComparer.OrdinalIgnoreCase);
byte[]? ReadMesh(string mesh)
{
    if (meshCache.TryGetValue(mesh, out var hit)) return hit;
    var full = mesh.StartsWith("meshes", StringComparison.OrdinalIgnoreCase) ? mesh : Path.Combine("meshes", mesh);
    var res = assets.ResolveForPlacement(full);
    return meshCache[mesh] = res.Sources.Count == 0 ? null : AssetResolver.ReadPlacementSource(res.Sources[0]).Bytes;
}

ScanResult result;
try { result = StrandScan.Run(new ScanInput(paths, p => folderOf.GetValueOrDefault(p) ?? StrandFamily.ByName(p), ReadMesh, table, mirror, cells, KeepAll: cells is not null)); }
catch (PluginUnreadableException ex) { Console.Error.WriteLine(ex.Message); return 2; }
Console.WriteLine($"strandprobe: {result.Population:N0} interior objects there at load; the identity cut leaves {result.LeftByCut:N0} to measure; {result.Measured:N0} measured; {sw.Elapsed.TotalSeconds:0.0} s");
foreach (var (why, n) in result.Coverage) Console.WriteLine($"   {n,8:N0}  {why}");
StrandEmit.Write(emitPath, result, baseFile, basePath, Instance, cfg.Identity("load-time", "baseline"), mirror);
Console.WriteLine($"wrote {result.Rows.Count:N0} row(s) -> {emitPath}; elapsed {sw.Elapsed.TotalSeconds:0.0} s");
return 0;
