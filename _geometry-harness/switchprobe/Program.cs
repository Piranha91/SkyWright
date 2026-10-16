using System.Diagnostics;
using System.Text.Json;
using System.Text.RegularExpressions;
using HarnessShared;
using HousecarlCore;
using Mutagen.Bethesda;
using Mutagen.Bethesda.Pex;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Plugins.Records;
using Mutagen.Bethesda.Skyrim;
using HousecarlCore.Progress;

// SWITCHPROBE — the world-state switch census and the trigger map (roadmap 4.11, handoff 2026-09-20 Parts A and B).
//
// The suite judges the load-time state. A quest stage, a scene or a trigger volume can enable a marker
// later, and everything hung on that marker swaps in one instant: the on-set arrives as the off-set
// leaves. This probe enumerates those markers — the SWITCHES — from the records: every reference that
// is the winning XESP parent of another reference, its on-set and off-set (chains followed with
// parity), the cells each set touches, the plugin that placed the marker and the plugins that placed
// and attached each child, the navcut boxes in each set, and the initially-disabled singletons that no
// parent names (an Unknown trigger on a set of one, counted apart). Then the vanilla floor — Bethesda's
// own switch density per cell and per region — so a mod's count has a baseline.
//
// Part B reads the load order's script bindings for each switch (a ScriptObjectProperty naming the
// marker on any record's VMAD; a quest alias whose forced reference is the marker), decompiles the
// bound script through the VFS, and records whether it calls Enable / Disable on that property and
// from which quest stage, scene or event. Per switch: TRIGGERED, BOUND BUT UNREAD, UNKNOWN — printed
// every run. A quest stage that flips several switches is a JOINT FLIP, written to --json for the
// state arm's `--state stage:<quest>:<n>`.
//
// The census is _shared/WorldState.cs (SwitchIndex over OverlapIndex) so the state arm reads the same
// enumeration; this file is I/O, tallies and the trigger map. Standing rules: the floor measured before
// any count is read (rule 2), populations split (rule 12), unjudgeable counted never cleared (rule 6),
// nothing verified in game (Part D flips one marker by console).
//
// usage: switchprobe [--list N] [--json FILE] [--triggers on|off] [--cell CELL]* [--switch SWITCH]*
//                    [--enable X.esp]* [--disable X.esp]* [--instance DIR] [--game DIR] [--profile NAME]
//   --triggers   on (default): Part B — bindings, decompile, the three counts, the joint flips; off: Part A only
//   --cell       list every switch with a set member in that cell (editor id or FormKey)
//   --switch     one switch in full: its sets, member by member, and its triggers
//   --json       the census and the trigger map (switches[], jointFlips[]) — what --switch-map on the other probes reads
//   --show-script NAME   print the decompiled source of one script from the VFS (to read what a bound script does)
//   --targets K1,K2|@FILE   for named references (switches or not, disabled singletons included): the load-time enable
//                state and whether anything in the order can change it later (_shared/TargetTriggers.cs); one line each
//   --targets-json FILE   the --targets reading in full; --pex-scan on|off (default on): its GetFormFromFile script scan
//   --extra-plugin P.esp  load this plugin file after the order (a test fixture); repeatable

using var __run = RunLog.BeginProcess("switchprobe", args);
// Known answers for the trigger map over a synthetic order (_shared/SwitchFixture.cs).
if (args.Length >= 1 && args[0] == "--self-test") return SwitchSelfTest.Run();

string Instance = HarnessTarget.Instance;
string GamePath = HarnessTarget.Game;
string profileName = HarnessTarget.Profile;
for (int i = 0; i < args.Length; i++)
{
    if (args[i] == "--instance" && i + 1 < args.Length) Instance = args[i + 1];
    else if (args[i] == "--game" && i + 1 < args.Length) GamePath = args[i + 1];
    else if (args[i] == "--profile" && i + 1 < args.Length) profileName = args[i + 1];
}
Instance = HarnessTarget.Require(Instance, "MO2 instance directory", "instance", "--instance");
GamePath = HarnessTarget.Require(GamePath, "game directory", "game", "--game");
profileName = HarnessTarget.Require(profileName, "MO2 profile name", "profile", "--profile");
string profileDir = Path.Combine(Instance, "profiles", profileName);
string modsDir = Path.Combine(Instance, "mods");
string overwriteDir = Path.Combine(Instance, "overwrite");
string dataDir = Path.Combine(GamePath, "Data");

int listN = 25;
string? jsonPath = null;
bool triggers = true;
var cellFilters = new List<string>(); var switchFilters = new List<string>(); var showScripts = new List<string>();
var enablePlugins = new List<string>(); var disablePlugins = new List<string>();
string? orderFile = null;
string? targetsArg = null, targetsJson = null; bool pexScan = true; var extraPlugins = new List<string>();
for (int i = 0; i < args.Length; i++)
{
    if (args[i] == "--list" && i + 1 < args.Length) listN = int.Parse(args[++i]);
    else if (args[i] == "--json" && i + 1 < args.Length) jsonPath = args[++i];
    else if (args[i] == "--triggers" && i + 1 < args.Length) triggers = args[++i].ToLowerInvariant() switch { "on" => true, "off" => false, _ => throw new ArgumentException("--triggers takes on|off") };
    else if (args[i] == "--cell" && i + 1 < args.Length) cellFilters.Add(args[++i]);
    else if (args[i] == "--switch" && i + 1 < args.Length) switchFilters.Add(args[++i]);
    else if (args[i] == "--show-script" && i + 1 < args.Length) showScripts.Add(args[++i]);
    else if (args[i] == "--order" && i + 1 < args.Length) orderFile = args[++i];
    else if (args[i] == "--enable" && i + 1 < args.Length) enablePlugins.Add(args[++i]);
    else if (args[i] == "--disable" && i + 1 < args.Length) disablePlugins.Add(args[++i]);
    else if (args[i] == "--targets" && i + 1 < args.Length) targetsArg = args[++i];
    else if (args[i] == "--targets-json" && i + 1 < args.Length) targetsJson = args[++i];
    else if (args[i] == "--pex-scan" && i + 1 < args.Length) pexScan = args[++i].ToLowerInvariant() switch { "on" => true, "off" => false, _ => throw new ArgumentException("--pex-scan takes on|off") };
    else if (args[i] == "--extra-plugin" && i + 1 < args.Length) extraPlugins.Add(args[++i]);
}
if (targetsJson is not null && targetsArg is null) { Console.Error.WriteLine("--targets-json needs --targets"); return 2; }
foreach (var x in extraPlugins) if (!File.Exists(x)) { Console.Error.WriteLine($"--extra-plugin {x}: no such file"); return 2; }

var sw = Stopwatch.StartNew();
// WHAT THIS RUN EVALUATES, decided in one place (_shared/HarnessOrderCli.cs): the real profile, a --enable/--disable
// what-if, or a --order candidate file - and, in the same step, the ASSET VIEW that configuration implies, so a run
// whose plugins changed no longer reads the profile's meshes and BSAs regardless. The identity it produces is written
// into the emit, so a reader (and conflictdiff) can see exactly what was consumed instead of inferring it.
var cfg = HarnessOrderCli.Resolve(profileName, profileDir, modsDir, dataDir, overwriteDir, GamePath,
                                  orderFile, enablePlugins, disablePlugins);
if (cfg.Refused) { Console.Error.WriteLine(cfg.RefusedReason); return 2; }
IReadOnlyList<string> paths = extraPlugins.Count == 0 ? cfg.OrderedPaths : cfg.OrderedPaths.Concat(extraPlugins).ToList();
// An --extra-plugin is loaded into the order measured, so its masters are checked like the order's own (ruling 12).
if (extraPlugins.Count > 0 && OrderMasters.RefusalForProfile(profileDir, modsDir, dataDir, overwriteDir, paths) is { } missingMaster)
{ Console.Error.WriteLine(missingMaster); return 2; }
Console.WriteLine($"load order: {paths.Count} plugins ({sw.ElapsedMilliseconds} ms)\n");
HarnessOrderCli.Print(cfg);

// ---------------------------------------------------------------------------------------
// Pass 1 — every plugin once, into the shared index (placements, XESP changes, collision layers, primitives).
// ---------------------------------------------------------------------------------------
var idx = new OverlapIndex();
var pluginKey = new ModKey[paths.Count];
var masterIdx = new HashSet<int>[paths.Count];
var unreadable = new List<string>();
__run.Feature("plugins", paths.Count, "plugins").Stage("scan", "reading plugins", paths.Count, "plugins");

for (int p = 0; p < paths.Count; p++)
{
    __run.Advance();
    ISkyrimModGetter mod;
    try { mod = SkyrimMod.CreateFromBinaryOverlay(paths[p], SkyrimRelease.SkyrimSE); }
    catch { unreadable.Add(Path.GetFileName(paths[p])); pluginKey[p] = ModKey.Null; masterIdx[p] = new HashSet<int>(); continue; }
    using (mod as IDisposable)
    {
        pluginKey[p] = mod.ModKey;
        var mset = new HashSet<int>();
        try
        {
            var names = mod.ModHeader.MasterReferences.Select(m => m.Master).ToHashSet();
            for (int q = 0; q < p; q++) if (names.Contains(pluginKey[q])) mset.Add(q);
        }
        catch { }
        masterIdx[p] = mset;
        idx.Ingest(p, mod);
    }
    if ((p + 1) % 150 == 0) Console.WriteLine($"   ... {p + 1}/{paths.Count} plugins, {idx.Placements:N0} placements, {sw.Elapsed.TotalSeconds:0}s");
}
var closure = DetectorModel.MasterClosure(masterIdx);
Console.WriteLine($"indexed {idx.Placements:N0} placements over {idx.History.Count:N0} refs; {idx.EnableChanges.Count:N0} refs with an XESP statement; "
                + $"{idx.Deletions.EverCount:N0} deleted somewhere  ({sw.Elapsed.TotalSeconds:0.0}s)");
Console.WriteLine($"   plugins that could not be read (contribute nothing, counted per rule 6): {unreadable.Count:N0}"
                + (unreadable.Count > 0 ? "  — " + string.Join(", ", unreadable) : ""));

string PluginName(int p) => p < 0 ? "(none)" : pluginKey[p].FileName.String;
bool IsVanilla(int p) => p >= 0 && PluginKinds.IsBethesda(PluginName(p));
bool IsUssep(int p) => p >= 0 && PluginKinds.IsUssep(PluginName(p));
string ClassOf(int p) => IsVanilla(p) ? "Bethesda" : IsUssep(p) ? "USSEP" : "mod";
string CellOf(FormKey c) => idx.CellName.TryGetValue(c, out var v) ? (v.Edid ?? c.ToString()) + (v.Interior ? " (int)" : idx.PersistentCells.Contains(c) ? " (persistent cell)" : " (ext)") : c.ToString();
string BaseLabel(FormKey k) => idx.RefBase.TryGetValue(k, out var b) && idx.BaseInfo.TryGetValue(b, out var bi) ? $"{bi.Type} {bi.Edid ?? b.ToString()}" : "?";
string RefLabel(FormKey k) => $"{k}{(idx.RefEditorId.TryGetValue(k, out var e) ? " " + e : "")} [{BaseLabel(k)}]";
float ScaleOf(FormKey k) => idx.History.Winning(k)?.Scale ?? 1f;

// ---------------------------------------------------------------------------------------
// The census (Part A) — _shared/WorldState.cs.
// ---------------------------------------------------------------------------------------
sw.Restart();
var sx = SwitchIndex.Build(idx);
sx.NameSwitches(idx.RefEditorId);
var switches = sx.Switches.Values.ToList();
int children = switches.Sum(s => s.Children.Count);
int descendants = switches.Sum(s => s.Descendants.Count);
Console.WriteLine($"\n==== 1. THE CENSUS — switches (a winning XESP parent that is a placed, non-player reference)  ({sw.Elapsed.TotalSeconds:0.0}s) ====");
Console.WriteLine($"   switches                         : {switches.Count:N0}   with a marker base (Is-Marker flag) {switches.Count(s => s.BaseIsMarker):N0}, an ordinary base {switches.Count(s => !s.BaseIsMarker):N0}");
Console.WriteLine($"   direct children                  : {children:N0}   on-set (same state) {switches.Sum(s => s.Children.Count(c => !c.Opposite)):N0}, off-set (opposite) {switches.Sum(s => s.Children.Count(c => c.Opposite)):N0}");
Console.WriteLine($"   descendants (chains followed)    : {descendants:N0}   on-set {switches.Sum(s => s.OnCount):N0}, off-set {switches.Sum(s => s.OffCount):N0}; switches with BOTH sets {switches.Count(s => s.OnCount > 0 && s.OffCount > 0):N0}");
Console.WriteLine($"   chained (the switch has a parent): {switches.Count(s => s.Parent is not null):N0}   deepest chain {switches.Select(s => s.Descendants.Count == 0 ? 0 : s.Descendants.Max(m => m.Depth)).DefaultIfEmpty(0).Max()}; cycles {sx.ChainCycles:N0} (counted, not walked)");
Console.WriteLine($"   baseline (EnableState, R1)       : enabled {switches.Count(s => s.Baseline.Enabled):N0}, disabled {switches.Count(s => !s.Baseline.Enabled):N0}, chain unresolved {switches.Count(s => !s.Baseline.Resolved):N0}");
Console.WriteLine($"   NOT switches, counted apart      : {sx.PlayerParentRefs:N0} refs whose parent is the player (permanently disabled), {sx.UnplacedParentRefs:N0} whose parent nobody places, "
                + $"{sx.DisabledSingletons:N0} initially-disabled refs with no parent (an Unknown trigger on a set of one)");
Console.WriteLine($"   set sizes                        : " + string.Join(", ", new[] { (1, 1), (2, 5), (6, 20), (21, 100), (101, int.MaxValue) }
    .Select(b => $"{(b.Item2 == int.MaxValue ? $">{b.Item1 - 1}" : b.Item1 == b.Item2 ? $"{b.Item1}" : $"{b.Item1}–{b.Item2}")} members: {switches.Count(s => s.Descendants.Count >= b.Item1 && s.Descendants.Count <= b.Item2):N0}")));
Console.WriteLine($"   switch base types (top)          : " + string.Join(", ", switches.GroupBy(s => $"{s.BaseType ?? "?"}{(s.BaseIsMarker ? "*" : "")}").OrderByDescending(g => g.Count()).Take(8).Select(g => $"{g.Key} {g.Count():N0}")) + "   (* = Is-Marker flag)");
var memberTypes = switches.SelectMany(s => s.Descendants).GroupBy(m => idx.RefBase.TryGetValue(m.Key, out var b) && idx.BaseInfo.TryGetValue(b, out var bi) ? bi.Type : "?").OrderByDescending(g => g.Count()).ToList();
Console.WriteLine($"   set member base types (top)      : " + string.Join(", ", memberTypes.Take(12).Select(g => $"{g.Key} {g.Count():N0}")));
Console.WriteLine($"   set member scale                 : " + string.Join(", ", new[] { ("<0.5", 0f, 0.5f), ("0.5–0.99", 0.5f, 0.999f), ("1", 0.999f, 1.001f), ("1.01–2", 1.001f, 2f), (">2", 2f, 1e9f) }
    .Select(b => $"{b.Item1} {switches.SelectMany(s => s.Descendants).Count(m => ScaleOf(m.Key) >= b.Item2 && ScaleOf(m.Key) < b.Item3):N0}")));

Console.WriteLine($"\n   ---- navcut boxes (XTRI on a COLL flagged NavmeshObstacle) ----");
Console.WriteLine($"   navcut layers in the order       : {idx.CollisionLayersByIndex.Count(kv => kv.Value.NavmeshObstacle):N0} flagged NavmeshObstacle — "
                + string.Join(", ", idx.CollisionLayersByIndex.Where(kv => kv.Value.NavmeshObstacle).OrderBy(kv => kv.Key).Select(kv => $"{kv.Value.Edid ?? "?"} (index {kv.Key}) {kv.Value.Key}"))
                + $"   (of {idx.CollisionLayersByIndex.Count:N0} COLL records)");
Console.WriteLine($"   navcut refs (winner, undeleted)  : {sx.Navcuts.Count:N0}   under a switch {sx.NavcutUnderSwitch.Count:N0} ({switches.Count(s => s.NavcutCount > 0):N0} switches carry one; on-set {switches.Sum(s => s.OnSet.Count(m => m.Navcut)):N0}, off-set {switches.Sum(s => s.OffSet.Count(m => m.Navcut)):N0}); without a primitive {sx.NavcutsWithoutPrimitive:N0} (counted, not measurable)");
Console.WriteLine($"   navcut base types                : " + string.Join(", ", sx.Navcuts.GroupBy(k => BaseLabel(k)).OrderByDescending(g => g.Count()).Take(6).Select(g => $"{g.Key} {g.Count():N0}")));
Console.WriteLine($"   navcut primitive types           : " + string.Join(", ", sx.Navcuts.GroupBy(k => idx.RefPrimitive.TryGetValue(k, out var pr) ? pr.Type : "(none)").OrderByDescending(g => g.Count()).Select(g => $"{g.Key} {g.Count():N0}")));
Console.WriteLine($"   refs with an XTRI at all         : {idx.RefCollisionLayer.Count:N0}; by layer: " + string.Join(", ", idx.RefCollisionLayer.Keys.GroupBy(k => sx.LayerName(k)).OrderByDescending(g => g.Count()).Take(8).Select(g => $"{g.Key} {g.Count():N0}")));

// ---------------------------------------------------------------------------------------
// 2. Per plugin — switches it adds, children it hangs on which switches, and the cells where it ignores one.
// ---------------------------------------------------------------------------------------
Console.WriteLine($"\n==== 2. PER PLUGIN — switches added; members attached to its own / a vanilla / another mod's switch; cells with a vanilla switch where the plugin places refs but attaches nothing ====");
var switchCells = new Dictionary<FormKey, List<SwitchInfo>>();
foreach (var s in switches)
    foreach (var c in s.Cells.Append(s.Cell).Distinct())
    { if (!switchCells.TryGetValue(c, out var l)) switchCells[c] = l = new List<SwitchInfo>(); if (!l.Contains(s)) l.Add(s); }
var refsByPluginCell = new Dictionary<int, HashSet<FormKey>>();      // plugin -> cells where it DEFINED a ref
foreach (var (key, snaps) in idx.History.Refs)
{
    var first = snaps[0];
    if (!refsByPluginCell.TryGetValue(first.Plugin, out var cells)) refsByPluginCell[first.Plugin] = cells = new HashSet<FormKey>();
    cells.Add(snaps[^1].Cell);
}
var perPlugin = new List<(int P, int Owned, int OnOwn, int OnVanilla, int OnOtherMod, int CellsWithVanillaSwitch, int Ignored)>();
for (int p = 0; p < paths.Count; p++)
{
    if (pluginKey[p].IsNull) continue;
    int owned = switches.Count(s => s.Owner == p);
    int onOwn = 0, onVan = 0, onOther = 0;
    var attachedIn = new HashSet<FormKey>();
    foreach (var s in switches)
        foreach (var m in s.Descendants)
        {
            if (m.Placer != p) continue;
            attachedIn.Add(m.Cell);
            if (s.Owner == p) onOwn++; else if (IsVanilla(s.Owner)) onVan++; else onOther++;
        }
    int cellsWithVan = 0, ignored = 0;
    if (refsByPluginCell.TryGetValue(p, out var cells))
        foreach (var c in cells)
            if (switchCells.TryGetValue(c, out var sl) && sl.Any(s => IsVanilla(s.Owner) && s.Owner != p))
            { cellsWithVan++; if (!attachedIn.Contains(c)) ignored++; }
    if (owned + onOwn + onVan + onOther + cellsWithVan == 0) continue;
    perPlugin.Add((p, owned, onOwn, onVan, onOther, cellsWithVan, ignored));
}
Console.WriteLine($"   {"plugin",-58} {"switches",8} {"on own",7} {"on vanilla",10} {"on other",8} {"cells w/ vanilla switch",22} {"ignored",8}");
foreach (var r in perPlugin.Where(r => !IsVanilla(r.P)).OrderByDescending(r => r.Owned + r.OnOwn + r.OnVanilla + r.OnOtherMod).Take(listN))
    Console.WriteLine($"   {Short(PluginName(r.P), 58),-58} {r.Owned,8:N0} {r.OnOwn,7:N0} {r.OnVanilla,10:N0} {r.OnOtherMod,8:N0} {r.CellsWithVanillaSwitch,22:N0} {r.Ignored,8:N0}");
Console.WriteLine($"   (vanilla plugins: " + string.Join("; ", perPlugin.Where(r => IsVanilla(r.P)).Select(r => $"{PluginName(r.P)} {r.Owned:N0} switches, {r.OnOwn + r.OnVanilla:N0} members")) + ")");
int modsWithVan = perPlugin.Count(r => !IsVanilla(r.P) && r.CellsWithVanillaSwitch > 0);
int modsAttaching = perPlugin.Count(r => !IsVanilla(r.P) && r.OnVanilla > 0);
Console.WriteLine($"   mods placing refs in a cell with a vanilla switch: {modsWithVan:N0}; of those, mods attaching anything to a vanilla switch anywhere: {modsAttaching:N0} ({Rate(modsAttaching, modsWithVan):0.0}%)");

// ---------------------------------------------------------------------------------------
// 3. Per cell — the vanilla floor by cell and by region; mods' cells beside it.
// ---------------------------------------------------------------------------------------
Console.WriteLine($"\n==== 3. PER CELL — switches with a set member in the cell; the VANILLA FLOOR (Bethesda-owned switches only) first ====");
static string Region(SwitchInfo s)
{
    var e = s.EditorId ?? s.BaseEditorId ?? "";
    var m = Regex.Match(e, @"^(CW|MQ|DLC1|DLC2|MS|DA|TG|DB|MG|C\d|BQ|FreeForm|dun|WE|WI|TGTQ|TGR|MSFC|ccBGS|cc)", RegexOptions.IgnoreCase);
    if (m.Success) return m.Value.ToUpperInvariant();
    var m2 = Regex.Match(e, @"^[A-Za-z]{2,10}");
    return m2.Success ? m2.Value : "(no editor id)";
}
var cellRows = switchCells.Select(kv => (Cell: kv.Key, All: kv.Value, Vanilla: kv.Value.Where(s => IsVanilla(s.Owner)).ToList(), Mod: kv.Value.Where(s => !IsVanilla(s.Owner) && !IsUssep(s.Owner)).ToList())).ToList();
Console.WriteLine($"   cells holding a switch or a set member: {cellRows.Count:N0}   (with a vanilla switch {cellRows.Count(r => r.Vanilla.Count > 0):N0}; with a mod's {cellRows.Count(r => r.Mod.Count > 0):N0}; both {cellRows.Count(r => r.Vanilla.Count > 0 && r.Mod.Count > 0):N0})");
Console.WriteLine($"   ---- the floor: top cells by vanilla switches (members in the cell) ----");
Console.WriteLine($"   {"cell",-44} {"switches",8} {"on-set",7} {"off-set",8} {"navcut",7}   regions");
foreach (var r in cellRows.OrderByDescending(r => r.Vanilla.Count).Take(listN))
    Console.WriteLine($"   {Short(CellOf(r.Cell), 44),-44} {r.Vanilla.Count,8:N0} {r.Vanilla.Sum(s => s.OnSet.Count(m => m.Cell == r.Cell)),7:N0} {r.Vanilla.Sum(s => s.OffSet.Count(m => m.Cell == r.Cell)),8:N0} {r.Vanilla.Sum(s => s.Descendants.Count(m => m.Navcut && m.Cell == r.Cell)),7:N0}   "
                    + string.Join(", ", r.Vanilla.GroupBy(Region).OrderByDescending(g => g.Count()).Take(4).Select(g => $"{g.Key} {g.Count()}")));
Console.WriteLine($"   ---- the floor by region (vanilla switches, editor-id prefix) ----");
foreach (var g in switches.Where(s => IsVanilla(s.Owner)).GroupBy(Region).OrderByDescending(g => g.Count()).Take(listN))
    Console.WriteLine($"   {g.Key,-14} {g.Count(),6:N0} switches, {g.Sum(s => s.Descendants.Count),7:N0} members (on {g.Sum(s => s.OnCount):N0} / off {g.Sum(s => s.OffCount):N0}), navcut {g.Sum(s => s.NavcutCount):N0}, baseline disabled {g.Count(s => !s.Baseline.Enabled):N0}, in {g.SelectMany(s => s.Cells).Distinct().Count():N0} cells");
Console.WriteLine($"   ---- by worldspace / interior (vanilla switches) ----");
foreach (var g in switches.Where(s => IsVanilla(s.Owner)).GroupBy(s => s.Interior ? "(interiors)" : s.Space.ToString()).OrderByDescending(g => g.Count()).Take(10))
    Console.WriteLine($"   {g.Key,-36} {g.Count(),6:N0} switches in {g.Select(s => s.Cell).Distinct().Count():N0} cells");
Console.WriteLine($"   ---- mods: top cells by mod-owned switches ----");
foreach (var r in cellRows.Where(r => r.Mod.Count > 0).OrderByDescending(r => r.Mod.Count).Take(Math.Min(listN, 15)))
    Console.WriteLine($"   {Short(CellOf(r.Cell), 44),-44} {r.Mod.Count,8:N0} mod switches ({string.Join(", ", r.Mod.GroupBy(s => PluginName(s.Owner)).OrderByDescending(g => g.Count()).Take(3).Select(g => $"{Short(g.Key, 32)} {g.Count()}"))}); vanilla switches here {r.Vanilla.Count:N0}");

// ---------------------------------------------------------------------------------------
// 4. The trigger map (Part B) — bindings, decompile, the three counts, the joint flips.
// ---------------------------------------------------------------------------------------
var bindings = new Dictionary<FormKey, List<Binding>>();          // switch -> bindings
var jointFlips = new Dictionary<string, HashSet<FormKey>>(StringComparer.OrdinalIgnoreCase);   // "quest:stage" -> switches
var triggerStatus = new Dictionary<FormKey, (string Status, string Kind, List<string> By)>();
int pexUnreadable = 0, pexDecompileFailed = 0, scriptsDecompiled = 0, recordsWithBindings = 0;
if (triggers)
{
    sw.Restart();
    Console.WriteLine($"\n==== 4. THE TRIGGER MAP (Part B) — script bindings that name a switch, and what the bound script does with it ====");
    using var assets = AssetResolver.Build(overwriteDir, modsDir, dataDir, cfg.EnabledMods, cfg.Archives.Archives);
    var av = assets.Capture();
    foreach (var name in showScripts)
    {
        Console.WriteLine($"\n---- --show-script {name} ----");
        var rel = $@"Scripts\{name.Replace(':', '\\')}.pex";
        var res = av.ResolveForPlacement(rel);
        if (res.Sources.Count == 0) { Console.WriteLine("   not in the VFS"); continue; }
        PexFile? pex = null;
        try
        {
            var src = res.Sources[0];
            if (src.LooseFilePath is not null) pex = PexFile.CreateFromFile(src.LooseFilePath, GameCategory.Skyrim);
            else if (src.ArchivePath is not null && AssetResolver.TryReadArchiveEntry(src.ArchivePath, src.EntryPath) is { } bytes)
            { using var ms = new MemoryStream(bytes); pex = PexFile.CreateFromStream(ms, GameCategory.Skyrim); }
        }
        catch (Exception ex) { Console.WriteLine($"   unreadable: {ex.Message}"); continue; }
        if (pex is null) { Console.WriteLine("   unreadable"); continue; }
        var dec = PapyrusDecompiler.DecompileFile(pex);
        Console.WriteLine(dec.Source);
        Console.WriteLine($"   ({dec.FunctionsTotal} functions, {dec.FunctionsFailed} failed)");
    }

    // 4a. the bindings, winner-takes-all per record (the winning override's VMAD is what the game loads).
    var recordBindings = new Dictionary<FormKey, List<Binding>>();
    var questFragmentStage = new Dictionary<(FormKey Quest, string Fragment), int>();
    var questAliasName = new Dictionary<(FormKey Quest, int Alias), string>();
    var questEdid = new Dictionary<FormKey, string?>();
    var recordEdid = new Dictionary<FormKey, string?>();
    var recordKind = new Dictionary<FormKey, string>();
    recordsWithBindings += TriggerMap.Bindings(paths, pluginKey, sx, sw, recordBindings, questFragmentStage, questAliasName, questEdid, recordEdid, recordKind, bindings);
    Console.WriteLine($"   bindings: {bindings.Values.Sum(l => l.Count):N0} on {recordsWithBindings:N0} records name {bindings.Count:N0} of {switches.Count:N0} switches  ({sw.Elapsed.TotalSeconds:0.0}s)");

    // 4b. decompile each bound script once; find the calls.
    var scripts = bindings.Values.SelectMany(l => l).Select(b => b.Script).Where(s => s.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    var source = new Dictionary<string, ScriptFlipCalls?>(StringComparer.OrdinalIgnoreCase);
    var unreadWhy = new Dictionary<string, int>();
    TriggerMap.Decompile(av, scripts, source, unreadWhy, ref pexUnreadable, ref pexDecompileFailed, ref scriptsDecompiled);
    Console.WriteLine($"   scripts bound: {scripts.Count:N0}; decompiled {scriptsDecompiled:N0}; pex missing or unreadable {pexUnreadable:N0} ({string.Join(", ", unreadWhy.Select(kv => $"{kv.Key} {kv.Value}"))}); with a function the decompiler refused {pexDecompileFailed:N0}  ({sw.Elapsed.TotalSeconds:0.0}s)");

    // 4c. per switch: Triggered / BoundUnread / Unknown, with the kind and the callers.
    TriggerMap.Status(switches, bindings, source, questFragmentStage, recordEdid, jointFlips, triggerStatus);
    int nT = triggerStatus.Count(kv => kv.Value.Status == "Triggered"), nB = triggerStatus.Count(kv => kv.Value.Status == "BoundUnread"), nU = triggerStatus.Count(kv => kv.Value.Status == "Unknown");
    Console.WriteLine($"\n   TRIGGERED {nT:N0} ({Rate(nT, switches.Count):0.0}%)   BOUND BUT UNREAD {nB:N0} ({Rate(nB, switches.Count):0.0}%)   UNKNOWN {nU:N0} ({Rate(nU, switches.Count):0.0}%)   of {switches.Count:N0} switches");
    Console.WriteLine($"   vanilla-owned: " + string.Join("   ", new[] { "Triggered", "BoundUnread", "Unknown" }.Select(st => $"{st} {switches.Count(s => IsVanilla(s.Owner) && triggerStatus[s.Key].Status == st):N0}")) + $"   of {switches.Count(s => IsVanilla(s.Owner)):N0}");
    Console.WriteLine($"   mod-owned    : " + string.Join("   ", new[] { "Triggered", "BoundUnread", "Unknown" }.Select(st => $"{st} {switches.Count(s => !IsVanilla(s.Owner) && triggerStatus[s.Key].Status == st):N0}")) + $"   of {switches.Count(s => !IsVanilla(s.Owner)):N0}");
    Console.WriteLine($"   triggered by kind: " + string.Join(", ", triggerStatus.Values.Where(v => v.Status == "Triggered").GroupBy(v => v.Kind).OrderByDescending(g => g.Count()).Take(12).Select(g => $"{g.Key} {g.Count():N0}")));
    Console.WriteLine($"   bound-unread by reason: " + string.Join(", ", triggerStatus.Values.Where(v => v.Status == "BoundUnread").GroupBy(v => v.Kind).Select(g => $"{g.Key} {g.Count():N0}")));
    Console.WriteLine($"   joint flips: {jointFlips.Count:N0} quest stages flip a switch; {jointFlips.Count(kv => kv.Value.Count >= 2):N0} flip two or more (largest: "
                    + string.Join(", ", jointFlips.OrderByDescending(kv => kv.Value.Count).Take(5).Select(kv => $"{kv.Key} {kv.Value.Count}")) + ")");
    Console.WriteLine($"   an Unknown switch is still flipped by the state arm — the absence of a binding is not evidence the state is unreachable.");
}

// ---------------------------------------------------------------------------------------
// 5. Listings: --cell and --switch.
// ---------------------------------------------------------------------------------------
void PrintSwitch(SwitchInfo s, bool members)
{
    var ts = triggerStatus.TryGetValue(s.Key, out var t) ? t : ("(triggers off)", "", new List<string>());
    Console.WriteLine($"   {s.Label}  {s.Key}  [{s.BaseType ?? "?"} {s.BaseEditorId ?? ""}{(s.BaseIsMarker ? " *marker" : "")}]  owner {PluginName(s.Owner)} ({ClassOf(s.Owner)})  in {CellOf(s.Cell)} at {s.Position}");
    Console.WriteLine($"      baseline {(s.Baseline.Enabled ? "ENABLED" : "DISABLED")}{(s.Baseline.ViaParent ? " via parent" : "")}{(s.Baseline.Resolved ? "" : " (chain unresolved)")}{(s.Parent is { } pp ? $"; own parent {pp}{(s.ParentOpposite ? " (opposite)" : "")}" : "")}"
                    + $"; on-set {s.OnCount} / off-set {s.OffCount} members in {s.Cells.Count()} cell(s); navcut {s.NavcutCount}{(s.ChainCycle ? "; CYCLE in the chain" : "")}");
    Console.WriteLine($"      trigger: {ts.Item1} — {ts.Item2}" + (ts.Item3.Count > 0 ? "\n         " + string.Join("\n         ", ts.Item3.Take(8)) + (ts.Item3.Count > 8 ? $"\n         ... and {ts.Item3.Count - 8} more" : "") : ""));
    if (!members) return;
    foreach (var m in s.Descendants.OrderBy(m => m.Parity).ThenBy(m => m.Depth).Take(listN * 2))
        Console.WriteLine($"         {(m.Parity ? "OFF" : "ON "),-3} d{m.Depth} {RefLabel(m.Key)} scale {ScaleOf(m.Key):0.##} in {CellOf(m.Cell)}; placed by {PluginName(m.Placer)}, attached by {PluginName(m.Attacher)}{(m.Navcut ? "  NAVCUT" : "")}");
    if (s.Descendants.Count > listN * 2) Console.WriteLine($"         ... and {s.Descendants.Count - listN * 2} more members");
}
foreach (var cf in cellFilters)
{
    var hits = cellRows.Where(r => CellOf(r.Cell).StartsWith(cf, StringComparison.OrdinalIgnoreCase) || r.Cell.ToString().Equals(cf, StringComparison.OrdinalIgnoreCase)).ToList();
    Console.WriteLine($"\n==== --cell {cf}: {hits.Count} cell(s) ====");
    foreach (var r in hits)
    {
        Console.WriteLine($"   {CellOf(r.Cell)} {r.Cell}: {r.All.Count} switch(es) with a member here");
        foreach (var s in r.All.OrderBy(s => IsVanilla(s.Owner) ? 0 : 1).ThenBy(s => s.Label)) PrintSwitch(s, false);
    }
}
foreach (var sf in switchFilters)
{
    var s = sx.Find(sf);
    Console.WriteLine($"\n==== --switch {sf} ====");
    if (s is null) { Console.WriteLine("   not a switch in this order"); continue; }
    PrintSwitch(s, true);
}

// ---------------------------------------------------------------------------------------
// --json: the census and the trigger map.
// ---------------------------------------------------------------------------------------
if (jsonPath is not null)
{
    var dir = Path.GetDirectoryName(Path.GetFullPath(jsonPath));
    if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
    object Member(SetMember m) => new
    {
        key = m.Key.ToString(), edid = idx.RefEditorId.GetValueOrDefault(m.Key), set = m.Parity ? "off" : "on", opposite = m.Opposite, depth = m.Depth,
        placer = PluginName(m.Placer), attacher = PluginName(m.Attacher), cell = CellOf(m.Cell), cellKey = m.Cell.ToString(),
        baseType = idx.RefBase.TryGetValue(m.Key, out var b) && idx.BaseInfo.TryGetValue(b, out var bi) ? bi.Type : null,
        baseEdid = idx.RefBase.TryGetValue(m.Key, out var b2) && idx.BaseInfo.TryGetValue(b2, out var bi2) ? bi2.Edid : null,
        scale = ScaleOf(m.Key), navcut = m.Navcut, position = idx.History.Winning(m.Key)?.Pos.ToString(),
    };
    var doc = new
    {
        generated = DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm:ssZ"), instance = Instance, profile = profileName, plugins = paths.Count, unreadable,
        summary = new
        {
            switches = switches.Count, children, descendants, onSet = switches.Sum(s => s.OnCount), offSet = switches.Sum(s => s.OffCount),
            chained = switches.Count(s => s.Parent is not null), cycles = sx.ChainCycles, playerParentRefs = sx.PlayerParentRefs, unplacedParentRefs = sx.UnplacedParentRefs,
            disabledSingletons = sx.DisabledSingletons, baselineDisabled = switches.Count(s => !s.Baseline.Enabled),
            navcutLayers = idx.CollisionLayersByIndex.Where(kv => kv.Value.NavmeshObstacle).Select(kv => $"{kv.Value.Edid} index {kv.Key} {kv.Value.Key}"), navcuts = sx.Navcuts.Count, navcutsUnderSwitch = sx.NavcutUnderSwitch.Count, navcutsWithoutPrimitive = sx.NavcutsWithoutPrimitive,
            triggers = triggers ? new { triggered = triggerStatus.Count(kv => kv.Value.Status == "Triggered"), boundUnread = triggerStatus.Count(kv => kv.Value.Status == "BoundUnread"), unknown = triggerStatus.Count(kv => kv.Value.Status == "Unknown"),
                                        scriptsDecompiled, pexUnreadable, pexDecompileFailed, recordsWithBindings, jointFlips = jointFlips.Count } : null,
        },
        perPlugin = perPlugin.Select(r => new { plugin = PluginName(r.P), @class = ClassOf(r.P), switches = r.Owned, membersOnOwn = r.OnOwn, membersOnVanilla = r.OnVanilla, membersOnOtherMod = r.OnOtherMod, cellsWithVanillaSwitch = r.CellsWithVanillaSwitch, ignored = r.Ignored }),
        switches = switches.OrderBy(s => s.Key.ToString()).Select(s => new
        {
            key = s.Key.ToString(), edid = s.EditorId, baseType = s.BaseType, baseEdid = s.BaseEditorId, marker = s.BaseIsMarker,
            owner = PluginName(s.Owner), ownerClass = ClassOf(s.Owner), cell = CellOf(s.Cell), cellKey = s.Cell.ToString(), interior = s.Interior, position = s.Position.ToString(),
            baselineEnabled = s.Baseline.Enabled, baselineViaParent = s.Baseline.ViaParent, baselineResolved = s.Baseline.Resolved,
            parent = s.Parent?.ToString(), parentOpposite = s.ParentOpposite, cycle = s.ChainCycle, region = Region(s),
            onSet = s.OnSet.Select(Member), offSet = s.OffSet.Select(Member),
            trigger = triggerStatus.TryGetValue(s.Key, out var t) ? new { status = t.Status, kind = t.Kind, by = t.By } : null,
        }),
        jointFlips = jointFlips.OrderBy(kv => kv.Key).Select(kv => new { stage = kv.Key, switches = kv.Value.Select(k => sx.Switches[k].Label).ToList() }),
    };
    File.WriteAllText(jsonPath, JsonSerializer.Serialize(doc, new JsonSerializerOptions { WriteIndented = false }));
    Console.WriteLine($"\nwrote {jsonPath}");
}

// ---------------------------------------------------------------------------------------
// --targets: named references, singletons included (_shared/TargetTriggers.cs).
// ---------------------------------------------------------------------------------------
if (targetsArg is not null)
{
    Console.WriteLine($"\n==== --targets: load-time enable state and enablers of {targetsArg} ====");
    using var tassets = AssetResolver.Build(overwriteDir, modsDir, dataDir, cfg.EnabledMods, cfg.Archives.Archives);
    TargetTriggers.Run(paths, tassets.Capture(), TargetTriggers.ParseTargets(targetsArg), pexScan, targetsJson,
                       new { instance = Instance, profile = profileName, order = orderFile, extraPlugins });
}

Console.WriteLine();
Console.WriteLine("Records only. A switch's sets are what the winning XESP edges say; the trigger map sees only script properties bound to the marker");
Console.WriteLine("(a GetLinkedRef, a runtime-filled alias, a property on some other object, the console are all Unknown). Nothing here is verified in game.");
Console.WriteLine($"elapsed {sw.Elapsed.TotalSeconds:0.0}s");
return 0;

static float Rate(long hit, long total) => total == 0 ? 0 : 100f * hit / total;
static string Short(string s, int n) => s.Length <= n ? s : s[..(n - 3)] + "...";

/// <summary>One script property (or alias route) on one record that names a switch.</summary>
sealed record Binding(FormKey Record, string Kind, string Script, string Property, FormKey Switch, string Via, string? AliasName);
