using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using HarnessShared;
using HousecarlCore;
using SkyWright.Engine;
using HousecarlCore.Progress;
using LeftBehind;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Skyrim;

// LEFT-BEHIND OCCLUSION PANELS — for a mod combination, was the geometry each occlusion panel was authored against
// moved, removed, disabled (directly or through its enable parent), base-swapped or replaced by the mod under test?
//
// Two load orders, each read as its own WINNING state: the base arm (without the mod under test) and the city arm
// (with it), each with its own asset view. Every panel (a placed XPRM primitive the named occlusion plugin defines or
// overrides) is sampled; each sample is "backed" in an arm when a present occluder's mesh surface is within delta of
// it, a closed solid contains it, or the terrain is within delta below it. The class compares the arms
// (LeftBehindRule.cs). The enable state is the load-time chain (opposite-state honoured); an unresolved chain is
// unknown, and a class that depends on it is CANNOT-ASSESS. See README.md.
//
// usage: leftbehindprobe --self-test
//        leftbehindprobe --base-order BASE.json --city-order CITY.json --panel-plugin OCCLUSION.esp --json OUT.json
//                        --instance DIR --game DIR --profile NAME [--require-instance DIR] [--panel-list PANELS.json]

if (args.Contains("--help") || args.Contains("-h"))
{
    Console.WriteLine("""
leftbehindprobe: left-behind occlusion panels between a base order and a city order
  --self-test                       synthetic fixtures, no game data; exit code 0 = all pass
  --base-order FILE --city-order FILE --panel-plugin NAME --json OUT
  --instance DIR --game DIR --profile NAME   (all required; no fallback to the harness config)
  [--require-instance DIR] [--panel-list FILE]
""");
    return 0;
}
using var __run = RunLog.BeginProcess("leftbehindprobe", args);

if (args.Contains("--self-test"))
{
    __run.Stage("selftest", "synthetic fixtures", null, "cases");
    return SelfTest.Run() == 0 ? 0 : 1;
}

string? baseOrder = null, cityOrder = null, panelPlugin = null, jsonPath = null, panelList = null;
string? instance = null, game = null, profile = null, requireInstance = null;
double maxMemoryGb = 12;
try
{
    for (int i = 0; i < args.Length; i++)
    {
        string V() => i + 1 < args.Length && !args[i + 1].StartsWith("--", StringComparison.Ordinal) ? args[++i] : throw new ArgumentException($"{args[i]} requires a value");
        switch (args[i])
        {
            case "--base-order": baseOrder = V(); break;
            case "--city-order": cityOrder = V(); break;
            case "--panel-plugin": panelPlugin = V(); break;
            case "--json": jsonPath = V(); break;
            case "--panel-list": panelList = V(); break;
            case "--instance": instance = V(); break;
            case "--game": game = V(); break;
            case "--profile": profile = V(); break;
            case "--require-instance": requireInstance = V(); break;
            case "--max-memory-gb": maxMemoryGb = double.Parse(V(), System.Globalization.CultureInfo.InvariantCulture); break;
            default: throw new ArgumentException($"unknown argument: {args[i]}");
        }
    }
    if (baseOrder is null || cityOrder is null || panelPlugin is null || jsonPath is null)
        throw new ArgumentException("--base-order, --city-order, --panel-plugin and --json are required");
    foreach (var f in new[] { baseOrder, cityOrder }.Concat(panelList is null ? Array.Empty<string>() : new[] { panelList }))
        if (!File.Exists(f)) throw new ArgumentException($"input does not exist: {f}");
}
catch (ArgumentException ex) { Console.Error.WriteLine(ex.Message); return 2; }

var sw = Stopwatch.StartNew();
CandidateFile baseCand, cityCand;
try { baseCand = CandidateFile.Read(baseOrder); cityCand = CandidateFile.Read(cityOrder); }
catch (Exception ex) { Console.Error.WriteLine($"order file unreadable: {ex.Message}"); return 2; }

Console.WriteLine($"instance: {instance ?? "(not given)"}");
Console.WriteLine($"game:     {game ?? "(not given)"}");
Console.WriteLine($"profile:  {profile ?? "(not given)"}; base order names '{baseCand.BaseProfile}', city order names '{cityCand.BaseProfile}'");
if (instance is not null) Console.WriteLine($"instance's configured game: {RunPin.InstanceGamePath(instance) ?? "(unreadable)"}");
var refusal = RunPin.Check(instance, game, profile, requireInstance, new[] { (baseOrder, baseCand.BaseProfile), (cityOrder, cityCand.BaseProfile) });
if (refusal is not null) { Console.Error.WriteLine($"REFUSED: {refusal}"); return 2; }
Console.WriteLine("pin: holds");

var baseSet = new HashSet<string>(baseCand.Plugins, StringComparer.OrdinalIgnoreCase);
var citySet = new HashSet<string>(cityCand.Plugins, StringComparer.OrdinalIgnoreCase);
if (!baseSet.IsSubsetOf(citySet)) { Console.Error.WriteLine("REFUSED: the base order's plugins are not a subset of the city order's"); return 2; }
Console.WriteLine($"mod under test (city minus base): plugins [{string.Join(", ", cityCand.Plugins.Where(p => !baseSet.Contains(p)))}], "
                + $"mod folders [{string.Join(", ", (cityCand.Mods ?? Array.Empty<string>()).Except(baseCand.Mods ?? Array.Empty<string>(), StringComparer.OrdinalIgnoreCase))}]");

string profileDir = Path.Combine(instance!, "profiles", profile!), modsDir = Path.Combine(instance!, "mods");
string overwriteDir = Path.Combine(instance!, "overwrite"), dataDir = Path.Combine(game!, "Data");

// ---- the two arms -------------------------------------------------------------------------------------------------
var parsed = new Dictionary<string, ArmGeometry?>(StringComparer.OrdinalIgnoreCase);
long parsedTriangles = 0; const long ParsedBudget = 8_000_000;   // parsed soups are re-read on demand past this
var overlaysAll = new List<string>();
var armInfo = new List<(string Name, string Order, List<string> Plugins, IReadOnlyList<string> Mods)>();

LeftBehindArm? BuildArm(string name, string orderFile, CandidateFile cand)
{
    var cfg = HarnessOrderCli.Resolve(profile!, profileDir, modsDir, dataDir, overwriteDir, game!, orderFile, new List<string>(), new List<string>());
    if (cfg.Refused) { Console.Error.WriteLine($"REFUSED ({name} arm): {cfg.RefusedReason}"); return null; }
    Console.WriteLine($"--- {name} arm: {Path.GetFileName(orderFile)}");
    Console.WriteLine($"   plugins ({cfg.OrderedPaths.Count}): {string.Join(", ", cfg.OrderedPaths.Select(Path.GetFileName))}");
    Console.WriteLine($"   asset view mods ({cfg.EnabledMods.Count}): {string.Join(" | ", cfg.EnabledMods)}");
    Console.WriteLine($"   archives ({cfg.Archives.Archives.Count}): {string.Join(", ", cfg.Archives.Archives.Select(a => Path.GetFileName(a.Path)))}");
    Console.WriteLine($"   BOS ini in this view ({cfg.Overlays.Count}): {(cfg.Overlays.Count == 0 ? "none" : string.Join(", ", cfg.Overlays.Select(o => o.Path)))}");
    // C3: the asset view is the order's own Mods list and nothing else.
    var want = new HashSet<string>(cand.Mods ?? Array.Empty<string>(), StringComparer.OrdinalIgnoreCase);
    if (cand.Mods is null || !cfg.EnabledMods.All(want.Contains) || cfg.EnabledMods.Count != want.Count)
    {
        Console.Error.WriteLine($"REFUSED ({name} arm): the asset view is not exactly the order file's Mods list");
        return null;
    }
    foreach (var o in cfg.Overlays) overlaysAll.Add($"{name}: {o.Kind} {o.Path}");
    var assets = AssetResolver.Build(overwriteDir, modsDir, dataDir, cfg.EnabledMods, cfg.Archives.Archives);
    var arm = new LeftBehindArm(name);
    armInfo.Add((name, orderFile, cfg.OrderedPaths.Select(p => Path.GetFileName(p)!).ToList(), cfg.EnabledMods));
    __run.Stage($"read-{name}", $"reading the {name} arm's plugins", cfg.OrderedPaths.Count, "plugins");
    for (int p = 0; p < cfg.OrderedPaths.Count; p++)
    {
        var path = cfg.OrderedPaths[p];
        arm.Plugins.Add(Path.GetFileName(path));
        using var mod = SkyrimMod.CreateFromBinaryOverlay(path, SkyrimRelease.SkyrimSE);
        arm.Index.Ingest(p, mod);
        foreach (var w in mod.Worldspaces.Records)
        {
            if (w.EditorID is { } e) arm.WorldspaceName[w.FormKey] = e;
            foreach (var block in w.SubCells)
                foreach (var sub in block.Items)
                    foreach (var cell in sub.Items)
                    {
                        if (cell.Landscape is not { } land || cell.Grid is not { } g) continue;
                        float[,]? h;
                        try { h = LandHeight.Decode(land.VertexHeightMap); } catch { h = null; }
                        arm.Land[(w.FormKey, g.Point.X, g.Point.Y)] = h;
                    }
        }
        __run.Advance();
        Console.WriteLine($"   read {Path.GetFileName(path)} ({sw.Elapsed.TotalSeconds:0}s)");
    }
    arm.Geometry = baseKey => GeometryFor(arm, assets, baseKey);
    return arm;
}

ArmGeometry? GeometryFor(LeftBehindArm arm, AssetResolver assets, FormKey baseKey)
{
    ArmGeometry? g = null;
    if (arm.Index.BaseMesh.TryGetValue(baseKey, out var rel))
    {
        var full = rel.StartsWith("meshes", StringComparison.OrdinalIgnoreCase) ? rel : Path.Combine("meshes", rel);
        var res = assets.ResolveForPlacement(full);
        if (res.Sources.Count > 0)
        {
            var src = res.Sources[0];
            string id = src.LooseFilePath ?? $"{src.ArchivePath}|{src.EntryPath}";
            if (!parsed.TryGetValue(id, out g))
            {
                g = null;
                var (bytes, _) = AssetResolver.ReadPlacementSource(src);
                if (bytes is not null && NifGeometry.Measure(bytes, includeTriangles: true) is { Model: { } m })
                {
                    var render = m.Soup is { TriangleCount: > 0 } rs ? rs : null;
                    var coll = m.CollisionComplete ? m.CollisionSoupFor(b => b.Solid && b.Layer != "SKYL_NONCOLLIDABLE") : null;
                    if (coll is { TriangleCount: 0 }) coll = null;
                    var surface = Concat(render, coll);
                    if (surface.TriangleCount > 0)
                    {
                        var closed = coll is not null && TriangleGrid.Build(coll, 64f).IsClosed ? coll : null;
                        char c = render is not null && coll is not null ? 'm' : render is not null ? 'r' : 'c';
                        g = new ArmGeometry(surface, closed, c, id);
                    }
                }
                if (parsedTriangles > ParsedBudget) { parsed.Clear(); parsedTriangles = 0; }
                parsedTriangles += g?.Surface.TriangleCount ?? 0;
                parsed[id] = g;
            }
        }
    }
    if (g is null && arm.HasModel(baseKey))   // the OBND box stands in only for a drawn base whose mesh could not be read
    {
        var ob = arm.Index.ObndBox(baseKey);
        if (!ob.IsEmpty) { var box = ContactIndex.BoxSoup(ob); g = new ArmGeometry(box, box, 'o', "obnd"); }
    }
    return g;
}

static TriangleSoup Concat(TriangleSoup? a, TriangleSoup? b)
{
    if (a is null) return b ?? TriangleSoup.Empty;
    if (b is null) return a;
    var v = a.Vertices.Concat(b.Vertices).ToArray();
    var idx = a.Indices.Concat(b.Indices.Select(i => i + a.Vertices.Length)).ToArray();
    return new TriangleSoup(v, idx);
}

var armB = BuildArm("base", baseOrder, baseCand);
if (armB is null) return 2;
var armC = BuildArm("city", cityOrder, cityCand);
if (armC is null) return 2;
string Mem() { long heap = GC.GetTotalMemory(true); var pr = Process.GetCurrentProcess(); return $"private {pr.PrivateMemorySize64 / 1e9:0.0} GB, working set {pr.WorkingSet64 / 1e9:0.0} GB, managed heap {heap / 1e9:0.0} GB"; }
Console.WriteLine($"memory after reading both arms: {Mem()}");

// ---- panels -------------------------------------------------------------------------------------------------------
var panels = armB.PanelsOf(panelPlugin!);
Console.WriteLine($"panels of {panelPlugin} in the base arm: {panels.Count}");
if (panelList is not null)
{
    using var doc = JsonDocument.Parse(File.ReadAllText(panelList));
    var listed = doc.RootElement.GetProperty("panels").EnumerateArray().Select(e => e.GetProperty("formKey").GetString()!).ToHashSet(StringComparer.Ordinal);
    var derived = panels.Select(p => p.Key.ToString()).ToHashSet(StringComparer.Ordinal);
    var missing = listed.Where(k => !derived.Contains(k)).ToList();
    Console.WriteLine($"--panel-list: {listed.Count} listed, {derived.Count} derived from records, {missing.Count} listed but not derived, {derived.Count(k => !listed.Contains(k))} derived but not listed");
    if (missing.Count > 0) { Console.Error.WriteLine($"REFUSED: listed panels the records do not have: {string.Join(", ", missing.Take(20))}"); return 2; }
    panels = panels.Where(p => listed.Contains(p.Key.ToString())).ToList();
}

__run.Feature("panels", panels.Count, "panels").Stage("measure", "measuring panels in both arms", panels.Count, "panels");
var measures = new List<PanelMeasure>();
int done = 0;
long peak = 0, limit = (long)(maxMemoryGb * 1024 * 1024 * 1024);
// Measured in space order (worldspace, then 4096 u grid) so neighbouring panels reuse placed geometry; the output is
// written in FormKey order whatever the measuring order.
foreach (var p in panels.OrderBy(p => p.Worldspace, StringComparer.Ordinal).ThenBy(p => (int)MathF.Floor(p.Position.X / 4096f))
                        .ThenBy(p => (int)MathF.Floor(p.Position.Y / 4096f)).ThenBy(p => p.Key.ToString(), StringComparer.Ordinal))
{
    measures.Add(LeftBehindRule.Measure(p, armC.PanelOf(p.Key), armB, armC));
    __run.Advance();
    // The guard reads PRIVATE (committed) bytes: the working set also counts file-backed pages of memory-mapped
    // archives, which the system can drop at will.
    long ws = Process.GetCurrentProcess().PrivateMemorySize64;
    if (done == 0) Console.WriteLine($"memory after the first panel (worldspace indices built): {Mem()}");
    if (ws > limit)
    {
        armB.ClearPlaced(); armC.ClearPlaced(); GC.Collect(2, GCCollectionMode.Aggressive, blocking: true, compacting: true); GC.WaitForPendingFinalizers();
        ws = Process.GetCurrentProcess().PrivateMemorySize64;
        if (ws > limit) { Console.Error.WriteLine($"REFUSED: {Mem()} stays above --max-memory-gb {maxMemoryGb} after clearing caches"); return 4; }
    }
    peak = Math.Max(peak, ws);
    if (++done % 100 == 0) Console.WriteLine($"   measured {done}/{panels.Count} ({sw.Elapsed.TotalSeconds:0}s, private {ws / 1e9:0.0} GB, working set {Process.GetCurrentProcess().WorkingSet64 / 1e9:0.0} GB, cache clears {armB.CacheClears}+{armC.CacheClears})");
}
measures = measures.OrderBy(m => m.Panel.Key.ToString(), StringComparer.Ordinal).ToList();
Console.WriteLine($"peak private bytes while measuring {peak / 1e9:0.0} GB (limit {maxMemoryGb} GB); cache clears base {armB.CacheClears}, city {armC.CacheClears}");
Console.WriteLine($"measured {done} panels ({sw.Elapsed.TotalSeconds:0}s); geometry per base lookup: "
                + string.Join("; ", new[] { armB, armC }.Select(a => $"{a.Name} " + string.Join(", ", a.SourceLetters.Values.GroupBy(c => c).OrderBy(g => g.Key).Select(g => $"{g.Key}={g.Count()}"))))
                + " bases (m render+collision, r render, c collision, o OBND box, - none)");

var t0 = Thresholds.Proposed;
var verdicts = measures.Select(m => LeftBehindRule.Classify(m, armB, armC, t0)).ToList();
Dictionary<string, int> Counts(IEnumerable<PanelVerdict> vs)
{
    var d = PanelClass.All.ToDictionary(c => c, _ => 0);
    foreach (var v in vs) d[v.Class]++;
    return d;
}
var counts = Counts(verdicts);
if (counts.Values.Sum() != panels.Count) { Console.Error.WriteLine("REFUSED: class counts do not sum to the panel total"); return 3; }
var sensitivity = new List<object>();
foreach (var d in new[] { 32f, 64f, 128f })
    foreach (var tp in new[] { 0.3f, 0.5f, 0.7f })
        foreach (var tl in new[] { 0.3f, 0.5f, 0.7f })
            foreach (var tm in new[] { 0.1f, 0.2f, 0.3f })
            {
                var t = new Thresholds(d, tp, tl, tm);
                var c = Counts(measures.Select(m => LeftBehindRule.Classify(m, armB, armC, t)));
                if (c.Values.Sum() != panels.Count) { Console.Error.WriteLine("REFUSED: sensitivity counts do not sum"); return 3; }
                sensitivity.Add(new { deltaU = d, tauPair = tp, tauLost = tl, tauMinor = tm, counts = c });
            }
var cannot = verdicts.Where(v => v.Class == PanelClass.CannotAssess)
    .GroupBy(v => ReasonClass(v.Reason ?? "")).ToDictionary(g => g.Key, g => g.Count());
static string ReasonClass(string r) { int i = r.IndexOf(':'); return i < 0 ? r : r[..i]; }

// ---- producer identity --------------------------------------------------------------------------------------------
static string Sha(string path) { using var s = File.OpenRead(path); return Convert.ToHexString(SHA256.HashData(s)).ToLowerInvariant(); }
string dll = typeof(LeftBehindRule).Assembly.Location, exe = Environment.ProcessPath ?? "";
string commit = "unknown";
try
{
    var dir = new DirectoryInfo(Path.GetDirectoryName(dll)!);
    while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "housecarl.sln"))) dir = dir.Parent;
    if (dir is not null)
    {
        string Git(string a)
        {
            var psi = new ProcessStartInfo("git", $"-C \"{dir.FullName}\" {a}") { RedirectStandardOutput = true, UseShellExecute = false };
            using var pr = Process.Start(psi)!; var o = pr.StandardOutput.ReadToEnd().Trim(); pr.WaitForExit(); return o;
        }
        commit = Git("rev-parse HEAD");
        if (Git("status --porcelain -- _geometry-harness/leftbehindprobe").Length > 0) commit += "+uncommitted(_geometry-harness/leftbehindprobe)";
    }
}
catch { }

string bos = overlaysAll.Count == 0 ? "none shipped" : string.Join("; ", overlaysAll);
var outDoc = new Dictionary<string, object?>
{
    ["schema"] = "housecarl.leftbehind.classes/1",
    ["producer"] = new { tool = "leftbehindprobe", dllSha256 = Sha(dll), exeSha256 = exe.Length > 0 ? Sha(exe) : null, commit, argv = args },
    ["arms"] = armInfo.ToDictionary(a => a.Name, a => (object)new { orderFile = a.Order, plugins = a.Plugins, mods = a.Mods }),
    ["enableState"] = "load-time chain (opposite-state honoured); unresolved = unknown",
    ["thresholds"] = new { deltaU = t0.DeltaU, tauPair = t0.TauPair, tauLost = t0.TauLost, tauMinor = t0.TauMinor },
    ["defaults"] = new { blindRadiusU = LeftBehindRule.BlindRadiusU, moveThresholdU = LeftBehindRule.MoveThresholdU,
                         twoExtentMinU = LeftBehindArm.TwoExtentMinU, backingBaseTypes = LeftBehindArm.BackingBaseTypes },
    ["baseObjectSwapper"] = bos,
    ["sensitivity"] = sensitivity,
    ["panels"] = verdicts.Select(v => new
    {
        panel = v.Panel.Key.ToString(), worldspace = v.Panel.Worldspace, kind = v.Panel.Type, @class = v.Class, reason = v.Reason,
        backingBase = v.BackingBase, backingCity = v.BackingCity, lostShare = v.LostShare, lostArea = v.LostArea, lostAreaUnit = v.LostAreaUnit,
        unknownBackers = v.UnknownBackers,
        partners = v.Partners.Select(p => new { @ref = p.Ref, @base = p.Base, editorId = p.EditorId, shareOfBacking = p.ShareOfBacking,
                                               cause = p.Cause, moveU = p.MoveU, replacedBy = p.ReplacedBy, geometry = p.Geometry }),
        terrain = v.Terrain,
        gainedBy = (v.GainedBy ?? Array.Empty<Gain>()).Select(g => new { @ref = g.Ref, @base = g.Base, editorId = g.EditorId,
                                               shareOfGain = g.ShareOfGain, how = g.How, geometry = g.Geometry }),
    }),
    ["counts"] = counts,
    ["cannotAssess"] = cannot,
};
File.WriteAllText(jsonPath!, JsonSerializer.Serialize(outDoc, new JsonSerializerOptions { WriteIndented = true }));
Console.WriteLine($"counts: {string.Join(", ", counts.Select(x => $"{x.Key} {x.Value}"))} (sum {counts.Values.Sum()} = {panels.Count} panels)");
Console.WriteLine($"cannot assess: {string.Join("; ", cannot.Select(x => $"{x.Key} {x.Value}"))}");
Console.WriteLine($"Base Object Swapper: {bos}");
Console.WriteLine($"wrote {jsonPath} ({sw.Elapsed.TotalSeconds:0}s)");
return 0;
