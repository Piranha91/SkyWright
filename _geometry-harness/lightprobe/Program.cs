// lightprobe — the placed lights of selected interior cells, as a load order resolves them, and what each does to the
// render surfaces around it (light-clash measure, lane L 2026-10-04).
//
// usage: lightprobe --self-test [--meshes-from GAME_DATA]   (reader cases on vanilla meshes run only with --meshes-from)
//        lightprobe [--instance DIR] [--game DIR] [--profile NAME] [--order FILE] --json OUT
//                   (--cell EDID ... | --cells-file FILE | --list-interiors) [--geometry] [--max-mb N]
//
// Per selected cell it writes every reference the winning order files there (base, type, editor id, model path, pose,
// load-time enable state through the shared enable chain, deletion, responsible plugin) and, for each placed Light,
// its base's radius / flags / fade / colour with the reference's XRDS radius offset added. With --geometry,
// every enabled reference's render model is read (src/SkyWright.Engine/LightReach.cs), placed, and each enabled light
// gets three readings: facing (front / back / no first hit over 64 rays inside its radius), leak (attenuated area lit
// through an opaque surface, and the unblocked area beside it, per receiving reference), and point-in-solid against
// every reference whose placed bounds contain the light (MeshAnchor's point reading). Every lit shape (drawn, lighting
// shader; editor markers and Is-Marker bases are not drawn) a light's radius reaches (its placed vertex box; the bound sphere count printed beside) gets a row with the lights that reach it (count, shadow-casting, portal-strict). A reference whose
// model cannot be read is listed with its reason, and every light whose radius reaches its record bounds is labelled
// incomplete. No decision is made here: attribution and verdicts are the caller's (the fold).
// Emitting parts (lane A 2026-10-13, with --geometry): every enabled, drawn reference with a model carries `emitters` (its placed
// emissive / self-lit effect / particle emitter boxes, LightReach.ReadEmitters) or `emitterStatus` when its mesh could not be read for them.
// identity.solidPolicy / anchorPolicy: MeshIntersection.PolicyVersion and MeshAnchor.PolicyVersion when the run wrote at least one
// pointInSolid row (identity.pointInSolidRows counts the rows written, refused ones included), else "none" (no --geometry,
// --list-interiors, or no light inside any placed box).
// Base Object Swapper: not applied (stated in the output).
// --list-interiors: every interior cell of the order with its placed-light count (the calibration sample frame).
// --check (the lighting check, 2026-11-26): lightprobe --check --dependencies D.json --selection S.json --rest R.json --combined C.json
//   --json OUT [--dump-arrangements DIR] [--scenes-file FILE] [--max-mb N]. Four order files (the arms runcontrol writes), each read as
//   itself with one model cache shared across them; the sides are read from the arms; the scenes are those both sides touch (or the
//   named ones); the rules are src/SkyWright.Engine/LightCheck.cs. Writes skywright.light-check/1 (CheckMode.cs).

using System.Diagnostics;
using System.Numerics;
using System.Text.Json;
using System.Text.Json.Serialization;
using HarnessShared;
using HousecarlCore;
using SkyWright.Engine;
using HousecarlCore.Progress;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Skyrim;

using var __run = RunLog.BeginProcess("lightprobe", args);
if (args.Length >= 1 && args[0] == "--self-test") return LightSelfTest.Run(args);

string Instance = HarnessTarget.Instance, GamePath = HarnessTarget.Game, profileName = HarnessTarget.Profile;
string? orderFile = null, jsonPath = null, cellsFile = null, dumpDir = null, scenesFile = null;
var wantCells = new List<string>();
var arrangements = new Dictionary<string, string>();   // --check: the four order files by role
bool geometry = false, listInteriors = false, check = false;
long maxMb = 0;   // --max-mb: stop (exit 3) when private bytes pass this many MB at a checkpoint; 0 = no cap
long peakPrivate = 0;   // the largest private-bytes reading at a checkpoint (working set also counts mapped plugin files)
for (int i = 0; i < args.Length; i++)
{
    string a = args[i];
    string Next() => i + 1 < args.Length ? args[++i] : throw new ArgumentException($"{a} needs a value");
    switch (a)
    {
        case "--instance": Instance = Next(); break;
        case "--game": GamePath = Next(); break;
        case "--profile": profileName = Next(); break;
        case "--order": orderFile = Next(); break;
        case "--json": jsonPath = Next(); break;
        case "--cell": wantCells.Add(Next()); break;
        case "--cells-file": cellsFile = Next(); break;
        case "--geometry": geometry = true; break;
        case "--list-interiors": listInteriors = true; break;
        case "--max-mb": maxMb = long.Parse(Next()); break;
        case "--check": check = true; break;
        case "--dependencies": arrangements["dependencies"] = Next(); break;
        case "--selection": arrangements["selection"] = Next(); break;
        case "--rest": arrangements["rest"] = Next(); break;
        case "--combined": arrangements["combined"] = Next(); break;
        case "--dump-arrangements": dumpDir = Next(); break;
        case "--scenes-file": scenesFile = Next(); break;
        default: Console.Error.WriteLine($"unknown argument {a}"); return 2;
    }
}
if (check && (orderFile is not null || cellsFile is not null || wantCells.Count > 0 || listInteriors))
{ Console.Error.WriteLine("--check reads the four arrangements and finds its own scenes: drop --order, --cell, --cells-file and --list-interiors (name scenes with --scenes-file)."); return 2; }
if (check) return CheckMode.Run(arrangements, jsonPath, dumpDir, scenesFile, maxMb, Instance, GamePath, profileName, __run);
if (jsonPath is null) { Console.Error.WriteLine("--json OUT is required"); return 2; }
if (cellsFile is not null) wantCells.AddRange(File.ReadAllLines(cellsFile).Select(l => l.Trim()).Where(l => l.Length > 0 && !l.StartsWith('#')));
if (!listInteriors && wantCells.Count == 0) { Console.Error.WriteLine("name cells with --cell / --cells-file, or --list-interiors"); return 2; }

Instance = HarnessTarget.Require(Instance, "MO2 instance directory", "instance", "--instance");
GamePath = HarnessTarget.Require(GamePath, "game directory", "game", "--game");
profileName = HarnessTarget.Require(profileName, "MO2 profile name", "profile", "--profile");
string profileDir = Path.Combine(Instance, "profiles", profileName), modsDir = Path.Combine(Instance, "mods"),
       overwriteDir = Path.Combine(Instance, "overwrite"), dataDir = Path.Combine(GamePath, "Data");
var sw = Stopwatch.StartNew();
void Cap(string where)
{
    if (maxMb <= 0) return;
    var pr = Process.GetCurrentProcess();
    long priv = pr.PrivateMemorySize64 / 1048576, ws = pr.WorkingSet64 / 1048576;
    peakPrivate = Math.Max(peakPrivate, priv);
    if (priv > maxMb) { Console.Error.WriteLine($"memory cap: private bytes {priv} MB (working set {ws} MB) > --max-mb {maxMb} at {where}; stopped"); Environment.Exit(3); }
}
var cache = new LightMeshCache();
using var rd = OrderReading.Open(profileName, profileDir, modsDir, dataDir, overwriteDir, GamePath, orderFile, __run, Cap, sw, cache, trackLand: false, bos: false, out var refusal);
if (rd is null) { Console.Error.WriteLine(refusal); return 2; }
var cfg = rd.Cfg; var idx = rd.Idx; var arms = rd.Arms; var pluginName = rd.PluginName;

// cells by editor id (interior only)
var cellByEdid = new Dictionary<string, FormKey>(StringComparer.OrdinalIgnoreCase);
foreach (var (k, (ed, interior)) in idx.CellName) if (interior && ed is not null) cellByEdid[ed] = k;
var refsByCell = new Dictionary<FormKey, List<FormKey>>();
foreach (var (r, (c, ws)) in idx.RefCell) if (ws.IsNull) (refsByCell.TryGetValue(c, out var l) ? l : refsByCell[c] = new()).Add(r);

var scopeCells = new List<(string Edid, FormKey Cell)>();
if (listInteriors) scopeCells.AddRange(cellByEdid.Select(kv => (kv.Key, kv.Value)));
else
    foreach (var c in wantCells)
    {
        if (!cellByEdid.TryGetValue(c, out var ck)) { Console.Error.WriteLine($"interior cell '{c}' is not in this order"); return 2; }
        scopeCells.Add((c, ck));
    }
var scopeRefs = scopeCells.SelectMany(c => refsByCell.GetValueOrDefault(c.Cell) ?? new()).ToHashSet();
rd.ReadBases(scopeRefs, sw);
var baseType = rd.BaseType; var lightBase = rd.LightBase;
Console.WriteLine($"bases read: {baseType.Count:N0} ({lightBase.Count:N0} lights); {scopeRefs.Count:N0} refs in {scopeCells.Count} cell(s) ({sw.Elapsed.TotalSeconds:0.0}s)");

var (llf, llfInstalled) = rd.Llf();

var jsonOpts = new JsonSerializerOptions { WriteIndented = true, PropertyNamingPolicy = JsonNamingPolicy.CamelCase, DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
                                           NumberHandling = JsonNumberHandling.AllowNamedFloatingPointLiterals };
// the solid rule and anchor reading the pointInSolid rows depend on: named when the run wrote at least one row (refused ones
// included), else "none"; Identity() is evaluated when the JSON is written, after every cell
int pointInSolidRows = 0;
object Identity() => LightProbeIdentity.Of(cfg, pluginName, llf, pointInSolidRows);

if (listInteriors)
{
    var rows = scopeCells.Select(c =>
    {
        var refs = refsByCell.GetValueOrDefault(c.Cell) ?? new();
        int lights = refs.Count(r => idx.RefBase.TryGetValue(r, out var b) && lightBase.ContainsKey(b));
        int en = refs.Count(r => idx.RefBase.TryGetValue(r, out var b) && lightBase.ContainsKey(b) && !idx.IsDeletedNow(r) && arms.Winner(r) is { Resolved: true, Enabled: true });
        return new { edid = c.Edid, cell = c.Cell.ToString(), refs = refs.Count, lights, lightsEnabled = en };
    }).OrderBy(x => x.edid, StringComparer.Ordinal).ToList();
    File.WriteAllText(jsonPath, JsonSerializer.Serialize(new { identity = Identity(), interiors = rows }, jsonOpts));
    Cap("end");
    Console.WriteLine($"{rows.Count:N0} interior cells -> {jsonPath}; peak working set {Process.GetCurrentProcess().PeakWorkingSet64 / 1048576.0:N0} MB; largest private bytes at a checkpoint {peakPrivate:N0} MB");
    return 0;
}

// ---- per cell: references, lights, and (with --geometry) the readings ----
var cellsOut = new List<object>();
__run.Stage("cells", "measuring cells", scopeCells.Count, "cells");
foreach (var (cedid, ck) in scopeCells)
{
    __run.Advance();
    var (cellJson, reading) = rd.ReadScene(cedid, ck, refsByCell.GetValueOrDefault(ck) ?? new(), _ => true, geometry, FormKey.Null,
                                           Array.Empty<(int, int)>(), llfInstalled, ref pointInSolidRows);
    cellsOut.Add(cellJson);
    Console.WriteLine($"{cedid}: {reading.Refs.Count} refs, {reading.Lights.Count} lights ({reading.Lights.Values.Count(l => l.Enabled)} enabled), {reading.Shapes.Count} lit shapes ({sw.Elapsed.TotalSeconds:0.0}s)");
}
File.WriteAllText(jsonPath, JsonSerializer.Serialize(new { identity = Identity(), geometry, cells = cellsOut }, jsonOpts));
Cap("end");
Console.WriteLine($"-> {jsonPath}; peak working set {Process.GetCurrentProcess().PeakWorkingSet64 / 1048576.0:N0} MB; largest private bytes at a checkpoint {peakPrivate:N0} MB");
return 0;

/// <summary>housecarl.light/1's identity block: one owner for both modes (the --check run's per-arrangement dumps write it too).</summary>
static class LightProbeIdentity
{
    public static object Of(ResolvedConfiguration cfg, string[] pluginName, object llf, int pointInSolidRows) =>
        new { schema = "housecarl.light/1", policy = LightReach.PolicyVersion,
              solidPolicy = pointInSolidRows > 0 ? MeshIntersection.PolicyVersion : "none",
              anchorPolicy = pointInSolidRows > 0 ? MeshAnchor.PolicyVersion : "none", pointInSolidRows, bos = "off", pose = "stored pose (an animated shape is read where the file stores it)", population = "load-time", state = "baseline",
              configuration = cfg.Identity("load-time", "baseline").Short, plugins = pluginName, llf };
}
