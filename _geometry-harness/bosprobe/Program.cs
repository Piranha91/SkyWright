using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using HarnessShared;
using HousecarlCore;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Skyrim;
using HousecarlCore.Progress;

// BOSPROBE — the Base Object Swapper reader, run over a load order and read back against the game.
//
// What it answers: which *_SWAP.ini files the game will read from this profile and in what order;
// what each one says, counted the way the game counts it (so the reader's account can be diffed
// line for line against po3_BaseObjectSwapper.log from a real run of the same order — the
// parse-level proof); what every reference in a cell ends up as under BOS — its effective base, the
// alternatives when a line is random, its transform after the overrides, the ini line responsible;
// and how much the detectors' inputs move when the reader is applied to the shared indexes.
//
// usage: bosprobe [--instance DIR] [--game DIR] [--profile NAME] [--enable X.esp]* [--disable X.esp]*
//                 [--compare-log FILE]   diff the reader's log-shaped account against a po3_BaseObjectSwapper.log
//                 [--dump-log FILE]      write the reader's account in that shape
//                 [--anomalies N]        print up to N of the lines the reader could not read as written (default 30)
//                 [--cell CELL]*         every winning ref in the cell (EditorID or FormKey) with what BOS does to it
//                 [--ref FORMKEY]*       one reference
//                 [--all-swapped]        every swapped ref in the order (with --list N)
//                 [--json FILE]          the rows of --cell / --ref / --all-swapped
//                 [--list N]             rows per listing (default 60)
//                 [--no-apply]           stop after the parse-level account
//
// Nothing is compiled in — see _shared/HarnessTarget.cs.

using var __run = RunLog.BeginProcess("bosprobe", args);

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

string? compareLog = null, dumpLog = null, jsonPath = null;
int anomaliesN = 30, listN = 60;
bool allSwapped = false, noApply = false;
var cells = new List<string>(); var refs = new List<string>(); var resolveTokens = new List<string>();
var enablePlugins = new List<string>(); var disablePlugins = new List<string>();
string? orderFile = null;
for (int i = 0; i < args.Length; i++)
{
    if (args[i] == "--compare-log" && i + 1 < args.Length) compareLog = args[++i];
    else if (args[i] == "--dump-log" && i + 1 < args.Length) dumpLog = args[++i];
    else if (args[i] == "--anomalies" && i + 1 < args.Length) anomaliesN = int.Parse(args[++i]);
    else if (args[i] == "--list" && i + 1 < args.Length) listN = int.Parse(args[++i]);
    else if (args[i] == "--cell" && i + 1 < args.Length) cells.Add(args[++i]);
    else if (args[i] == "--ref" && i + 1 < args.Length) refs.Add(args[++i]);
    else if (args[i] == "--resolve" && i + 1 < args.Length) resolveTokens.Add(args[++i]);
    else if (args[i] == "--json" && i + 1 < args.Length) jsonPath = args[++i];
    else if (args[i] == "--all-swapped") allSwapped = true;
    else if (args[i] == "--no-apply") noApply = true;
    else if (args[i] == "--order" && i + 1 < args.Length) orderFile = args[++i];
    else if (args[i] == "--enable" && i + 1 < args.Length) enablePlugins.Add(args[++i]);
    else if (args[i] == "--disable" && i + 1 < args.Length) disablePlugins.Add(args[++i]);
}

var sw = Stopwatch.StartNew();
// WHAT THIS RUN EVALUATES, decided in one place (_shared/HarnessOrderCli.cs): the real profile, a --enable/--disable
// what-if, or a --order candidate file - and, in the same step, the ASSET VIEW that configuration implies, so a run
// whose plugins changed no longer reads the profile's meshes and BSAs regardless. The identity it produces is written
// into the emit, so a reader (and conflictdiff) can see exactly what was consumed instead of inferring it.
var cfg = HarnessOrderCli.Resolve(profileName, profileDir, modsDir, dataDir, overwriteDir, GamePath,
                                  orderFile, enablePlugins, disablePlugins);
if (cfg.Refused) { Console.Error.WriteLine(cfg.RefusedReason); return 2; }
var paths = cfg.OrderedPaths;
using var assets = AssetResolver.Build(overwriteDir, modsDir, dataDir, cfg.EnabledMods, cfg.Archives.Archives);
Console.WriteLine($"load order: {paths.Count} plugins; VFS: {cfg.EnabledMods.Count} enabled mods\n");
HarnessOrderCli.Print(cfg);

// ---------------------------------------------------------------------------------------
// Discovery — what the game reads, in the order it reads it.
// ---------------------------------------------------------------------------------------
var inis = BosDiscovery.Discover(assets.LooseRoots);
Console.WriteLine($"BOS inis: {inis.Count} matching inis found (Data root only, name contains '_SWAP', extension '.ini', ordinal order)");
foreach (var ini in inis)
    Console.WriteLine($"   {ini.Name,-70} <- {ini.Provider}" + (ini.ShadowedProviders.Count > 0 ? $"  (shadows: {string.Join(", ", ini.ShadowedProviders)})" : ""));
// Near misses: files the game will NOT read that a human would expect it to (rule 6).
int nearMisses = 0;
foreach (var (name, dir) in assets.LooseRoots)
{
    if (!Directory.Exists(dir)) continue;
    try
    {
        foreach (var f in Directory.EnumerateFiles(dir, "*", SearchOption.TopDirectoryOnly))
        {
            var fn = Path.GetFileName(f);
            if (BosDiscovery.Matches(fn)) continue;
            if (fn.Contains("_SWAP", StringComparison.OrdinalIgnoreCase) && fn.EndsWith(".ini", StringComparison.OrdinalIgnoreCase))
            { nearMisses++; Console.WriteLine($"   ! not read by the game: {name}\\{fn} (the match is case-sensitive: '_SWAP' and '.ini')"); }
        }
    }
    catch { }
}
if (nearMisses == 0) Console.WriteLine("   (no case-mismatched near misses at any root)");
Console.WriteLine();

// ---------------------------------------------------------------------------------------
// Pass 1 — every plugin once: the shared index, and the BOS form index alongside it.
// ---------------------------------------------------------------------------------------
var idx = new OverlapIndex { Bos = new BosFormIndex() };
var pluginKey = new ModKey[paths.Count];
var masterIdx = new HashSet<int>[paths.Count + 1];
var lightness = new List<(ModKey, bool)>();
var unreadable = new List<string>();
for (int p = 0; p < paths.Count; p++)
{
    ISkyrimModGetter mod;
    try { mod = SkyrimMod.CreateFromBinaryOverlay(paths[p], SkyrimRelease.SkyrimSE); }
    catch { unreadable.Add(Path.GetFileName(paths[p])); pluginKey[p] = ModKey.Null; masterIdx[p] = new HashSet<int>(); lightness.Add((ModKey.Null, false)); continue; }
    using (mod as IDisposable)
    {
        pluginKey[p] = mod.ModKey;
        lightness.Add((mod.ModKey, BosHook.IsLight(mod)));
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
    if ((p + 1) % 150 == 0) Console.WriteLine($"   ... {p + 1}/{paths.Count} plugins, {sw.Elapsed.TotalSeconds:0}s");
}
int bosPlugin = paths.Count;
masterIdx[bosPlugin] = BosHook.PseudoMasters(paths.Count);
var closure = DetectorModel.MasterClosure(masterIdx);
idx.Bos!.SetLoadOrder(lightness);
Console.WriteLine($"indexed {idx.History.Count:N0} refs ({idx.Placements:N0} placements), {idx.Bos.RecordsSeen:N0} records seen, "
                + $"{idx.Bos.EditorIdCollisions:N0} EditorID collisions (a later record could not claim a name the first holds)  ({sw.Elapsed.TotalSeconds:0.0}s)");
if (unreadable.Count > 0) Console.WriteLine($"   plugins that could not be read (contribute nothing): {string.Join(", ", unreadable)}");
string PluginName(int p) => p < 0 ? "(none)" : p == bosPlugin ? BosApplyReport.PseudoPluginName : pluginKey[p].FileName.String;

// --resolve TOKEN: how one ini id reads against this order (the three spellings; loaded-at-start; the runtime id).
foreach (var tok in resolveTokens)
{
    var id = idx.Bos.Resolve(tok);
    Console.WriteLine($"resolve '{tok}': {id.Kind} found={id.Found} key={id.Key?.ToString() ?? "-"} exists={id.Exists} loaded={id.Loaded} runtime={id.RuntimeId:X8}"
                    + (id.Key is { } rk ? $" type={idx.Bos.TypeOf(rk) ?? (idx.BaseInfo.TryGetValue(rk, out var bi2) ? bi2.Type : "?")}" : "") + (id.Note is { } nt ? $"  — {nt}" : ""));
}

// ---------------------------------------------------------------------------------------
// The layer — every ini read the way the game reads it.
// ---------------------------------------------------------------------------------------
var layer = BaseObjectSwaps.Load(inis, idx.Bos);
int secForms = layer.Sections.Count(s => !s.Section.Contains('|') && s.Section == "Forms");
int secRefs = layer.Sections.Count(s => !s.Section.Contains('|') && s.Section is not ("Forms" or "Transforms" or "Properties"));
int secProps = layer.Sections.Count(s => !s.Section.Contains('|') && s.Section is "Transforms" or "Properties");
int secCondForms = layer.Sections.Count(s => s.Section.StartsWith("Forms|", StringComparison.Ordinal));
int secCondProps = layer.Sections.Count(s => s.Section.Contains('|') && !s.Section.StartsWith("Forms|", StringComparison.Ordinal));
Console.WriteLine($"\nlayer: {layer.LinesRead:N0} lines read, {layer.LinesFailed:N0} failed (the game's own fail messages), in {layer.Sections.Count:N0} sections "
                + $"— [Forms] {secForms}, [References] {secRefs}, [Transforms] {secProps}, [Forms|filters] {secCondForms}, [other|filters] {secCondProps}");
Console.WriteLine($"   tables: {layer.SwapForms.Count:N0} bases with plain swaps, {layer.SwapFormsConditional.Count:N0} with filtered swaps, {layer.SwapRefs.Count:N0} refs with swaps, "
                + $"{layer.Properties.Count:N0} ids with property overrides, {layer.PropertiesConditional.Count:N0} with filtered ones");
int randomLines = layer.SwapForms.Values.Concat(layer.SwapRefs.Values).SelectMany(l => l).Concat(layer.SwapFormsConditional.Values.SelectMany(m => m.Values.SelectMany(v => v.Lines)))
                       .Count(e => e.RandomPick || !e.Chance.Certain || e.AssignedAtLoad);
int perSessionLines = layer.SwapForms.Values.Concat(layer.SwapRefs.Values).SelectMany(l => l).Concat(layer.SwapFormsConditional.Values.SelectMany(m => m.Values.SelectMany(v => v.Lines)))
                       .Count(e => e.Chance.PerSession);
Console.WriteLine($"   random: {randomLines:N0} swap entries carry a chance under 100 or several targets ({perSessionLines:N0} re-rolled every session); "
                + $"filters: {layer.FiltersAsText:N0} read as text (no such form), {layer.FiltersMissingForm:N0} name a FormID with no form (never match); "
                + $"{layer.EditorIdsUnresolved:N0} ids resolved to nothing, {layer.TargetsMissing:N0} swap targets name no record, {layer.UnknownPropertyTokens:N0} property tokens BOS ignores");
Console.WriteLine($"   anomalies (rule 6): {layer.Anomalies.Count:N0}" + (layer.Anomalies.Count > 0 ? $" — first {Math.Min(anomaliesN, layer.Anomalies.Count)}:" : ""));
foreach (var a in layer.Anomalies.Take(anomaliesN)) Console.WriteLine($"      {a}");
if (layer.Anomalies.Count > anomaliesN)
{
    var byKind = layer.Anomalies.Select(a => Regex.Replace(a.Split(": ", 2).Last(), @"'[^']*'|\d+", "_")).GroupBy(k => k).OrderByDescending(g => g.Count()).Take(12);
    Console.WriteLine("      by shape:");
    foreach (var g in byKind) Console.WriteLine($"      {g.Count(),6}  {g.Key}");
}

if (dumpLog is not null) { File.WriteAllLines(dumpLog, layer.GameLogLines()); Console.WriteLine($"\nwrote the reader's log-shaped account -> {dumpLog}"); }

// ---------------------------------------------------------------------------------------
// --compare-log: the parse-level proof — the game's own INI block against the reader's.
// ---------------------------------------------------------------------------------------
if (compareLog is not null)
{
    var game = new List<string>();
    bool inIni = false;
    foreach (var raw in File.ReadLines(compareLog))
    {
        // Two log shapes: 3.4.x "[HH:MM:SS:mmm] text"; 3.5.0 "[MM-DD HH:MM:SS.mmm] [I] text" (the level tag dropped with the stamp).
        var l = Regex.Replace(raw, @"^\[\d\d:\d\d:\d\d:\d\d\d\] |^\[\d\d-\d\d \d\d:\d\d:\d\d\.\d\d\d\] \[[A-Z]\] ", "");
        if (l.Contains("*INI*")) { inIni = true; continue; }
        if (l.Contains("*RESULT*")) break;
        if (inIni) game.Add(l);
    }
    var mine = layer.GameLogLines().ToList();
    Console.WriteLine($"\n==== parse-level comparison against {Path.GetFileName(compareLog)} ====");
    Console.WriteLine($"   game: {game.Count:N0} lines in the INI block; reader: {mine.Count:N0}");
    static Dictionary<string, List<string>> Blocks(List<string> lines, out List<string> order, out string? head)
    {
        var d = new Dictionary<string, List<string>>(StringComparer.Ordinal); order = new List<string>(); head = null;
        List<string>? cur = null;
        foreach (var l in lines)
        {
            if (l.StartsWith("INI : ", StringComparison.Ordinal)) { var k = l[6..]; order.Add(k); d[k] = cur = new List<string>(); continue; }
            if (cur is null) { head ??= l; continue; }
            cur.Add(l);
        }
        return d;
    }
    var gb = Blocks(game, out var gOrder, out var gHead);
    var mb = Blocks(mine, out var mOrder, out var mHead);
    Console.WriteLine($"   head: game '{gHead}' | reader '{mHead}' -> {(gHead == mHead ? "MATCH" : "DIFF")}");
    var onlyGame = gOrder.Where(k => !mb.ContainsKey(k)).ToList();
    var onlyMine = mOrder.Where(k => !gb.ContainsKey(k)).ToList();
    Console.WriteLine($"   inis: {gOrder.Count} in the game's list, {mOrder.Count} in the reader's; {gOrder.Count - onlyGame.Count} in both; "
                    + $"order of the common ones {(gOrder.Where(mb.ContainsKey).SequenceEqual(mOrder.Where(gb.ContainsKey)) ? "IDENTICAL" : "DIFFERS")}");
    foreach (var k in onlyGame) Console.WriteLine($"      only the game read: {k}");
    foreach (var k in onlyMine) Console.WriteLine($"      only the reader found: {k}");
    int exact = 0, differ = 0, gLines = 0, mLines = 0, matchedLines = 0;
    var diffKinds = new Dictionary<string, int>();
    var examples = new List<string>();
    static string Kind(string l) => l.Contains("reading [") ? "reading" : l.Contains(" found") ? "count" : l.Contains("fail :") ? "fail" : l.Contains("failed to process") ? "failed-to-process" : l.Contains("Filter [") ? "filter-note" : "other";
    foreach (var k in gOrder)
    {
        if (!mb.TryGetValue(k, out var m)) continue;
        var g = gb[k];
        gLines += g.Count; mLines += m.Count;
        if (g.SequenceEqual(m)) { exact++; matchedLines += g.Count; continue; }
        differ++;
        // Line-level account: multiset difference each way, then order.
        var gm = new Dictionary<string, int>(StringComparer.Ordinal); foreach (var l in g) gm[l] = gm.GetValueOrDefault(l) + 1;
        var mm = new Dictionary<string, int>(StringComparer.Ordinal); foreach (var l in m) mm[l] = mm.GetValueOrDefault(l) + 1;
        var gOnly = new List<string>(); var mOnly = new List<string>();
        foreach (var (l, n) in gm) { int d = n - mm.GetValueOrDefault(l); for (int i = 0; i < d; i++) gOnly.Add(l); }
        foreach (var (l, n) in mm) { int d = n - gm.GetValueOrDefault(l); for (int i = 0; i < d; i++) mOnly.Add(l); }
        matchedLines += g.Count - gOnly.Count;
        foreach (var l in gOnly) diffKinds["game-only " + Kind(l)] = diffKinds.GetValueOrDefault("game-only " + Kind(l)) + 1;
        foreach (var l in mOnly) diffKinds["reader-only " + Kind(l)] = diffKinds.GetValueOrDefault("reader-only " + Kind(l)) + 1;
        if (gOnly.Count == 0 && mOnly.Count == 0) diffKinds["order only"] = diffKinds.GetValueOrDefault("order only") + 1;
        if (examples.Count < 40)
        {
            examples.Add($"   -- {k}: {gOnly.Count} line(s) only in the game, {mOnly.Count} only in the reader" + (gOnly.Count == 0 && mOnly.Count == 0 ? " (same lines, different order)" : ""));
            foreach (var l in gOnly.Take(6)) examples.Add($"      game   : {l.Replace("\t", "  ")}");
            foreach (var l in mOnly.Take(6)) examples.Add($"      reader : {l.Replace("\t", "  ")}");
        }
    }
    Console.WriteLine($"   blocks: {exact} inis reproduced exactly, {differ} with differences; lines: {matchedLines:N0} of the game's {gLines:N0} matched ({(gLines == 0 ? 0 : 100.0 * matchedLines / gLines):0.00} %), reader wrote {mLines:N0}");
    foreach (var kv in diffKinds.OrderByDescending(k => k.Value)) Console.WriteLine($"      {kv.Value,6}  {kv.Key}");
    foreach (var e in examples) Console.WriteLine(e);
}

if (noApply) return 0;

// ---------------------------------------------------------------------------------------
// Apply — what moves in the shared index when the reader's answer replaces the record base.
// ---------------------------------------------------------------------------------------
var rep = BosHook.Apply(idx, layer, bosPlugin);
Console.WriteLine();
foreach (var l in rep.Summary()) Console.WriteLine(l);
if (rep.NewBaseTypes.Count > 0)
    Console.WriteLine("   effective base types: " + string.Join(", ", rep.NewBaseTypes.OrderByDescending(k => k.Value).Select(k => $"{k.Key} {k.Value:N0}")));
foreach (var n in rep.Notes) Console.WriteLine($"   {n}");

// ---------------------------------------------------------------------------------------
// Listings — the per-reference answer, for a cell, a reference, or every swapped reference.
// ---------------------------------------------------------------------------------------
string CellOf(FormKey c) => idx.CellName.TryGetValue(c, out var v) ? (v.Edid ?? c.ToString()) + (v.Interior ? " (int)" : " (ext)") : c.ToString();
string BaseLabel(FormKey b) => idx.BaseInfo.TryGetValue(b, out var bi) ? $"{bi.Type} {bi.Edid ?? "-"}" : (idx.Bos!.TypeOf(b) ?? "?") + " " + b;
string MeshOf(FormKey b) => idx.BaseMesh.TryGetValue(b, out var m) ? m : "-";
string RuntimeId(FormKey k) { var id = idx.Bos!.RuntimeIdOf(k); return id == 0 ? "-" : id.ToString("X8"); }
static string V(Vec3 v) => $"{v.X:0.##},{v.Y:0.##},{v.Z:0.##}";
static string Deg(Vec3 r) => $"{float.RadiansToDegrees(r.X):0.##},{float.RadiansToDegrees(r.Y):0.##},{float.RadiansToDegrees(r.Z):0.##}";

var rows = new List<object>();
void Row(FormKey key, bool onlySwapped)
{
    // The RECORD placement: the last snap not written by the BOS pseudo-plugin (Apply may have appended one), so the
    // effective transform printed below is applied once, not on top of itself.
    var snaps = idx.History.Of(key);
    if (snaps is null || snaps.Count == 0) { Console.WriteLine($"   {key}: no winning placement in this order"); return; }
    Snap? recordSnap = null;
    for (int i = snaps.Count - 1; i >= 0; i--) if (snaps[i].Plugin != bosPlugin) { recordSnap = snaps[i]; break; }
    if (recordSnap is not { } w) { Console.WriteLine($"   {key}: only a BOS placement step in this order"); return; }
    idx.BosSwaps.TryGetValue(key, out var res);
    var recordBase = idx.RecordBase.TryGetValue(key, out var rb) ? rb : idx.RefBase.GetValueOrDefault(key);
    if (onlySwapped && res is null) return;
    var arm = idx.RefArm.GetValueOrDefault(key, "?");
    string status = res is null ? (BaseObjectSwaps.Swappable(arm) ? "unchanged" : $"not swappable ({arm})")
                  : res.Swapped ? (res.Random ? "SWAPPED (random — first alternative shown)" : "SWAPPED")
                  : res.Random ? "MAY SWAP (record base is the first alternative)" : "properties only";
    var (pos, rot, scale, dis) = res?.Apply(w.Pos, w.Rot, w.Scale, w.Disabled) ?? (w.Pos, w.Rot, w.Scale, w.Disabled);
    Console.WriteLine($"   {key} = {RuntimeId(key)}  {arm}  in {CellOf(w.Cell)}  — {status}");
    Console.WriteLine($"      record base    {recordBase} {BaseLabel(recordBase)}  <{MeshOf(recordBase)}>");
    if (res is not null)
    {
        if (res.Swapped) Console.WriteLine($"      effective base {res.EffectiveBase} {BaseLabel(res.EffectiveBase)}  <{MeshOf(res.EffectiveBase)}>");
        if (res.Alternatives.Count > 1 || res.Random)
            foreach (var a in res.Alternatives.OrderByDescending(a => a.Probability))
                Console.WriteLine($"         {a.Probability * 100,6:0.0} %  {a.Base} {BaseLabel(a.Base)}{(a.ViaLeveledItem ? "  (via leveled item)" : "")}{(a.From is { } f ? $"  <- {f.Where}" : "  (the record base stays)")}");
        else if (res.SwapFrom is { } sf) Console.WriteLine($"         <- {sf.Where}");
        if (res.Properties is { IsValid: true } p)
        {
            Console.WriteLine($"      properties from {res.PropertiesSource} <- {res.PropertiesFrom}{(res.PropertiesRandom ? "  (a range or a chance — minimum shown)" : "")}");
            if (p.Position is { } pp) Console.WriteLine($"         position {V(w.Pos)} -> {V(pos)}  ({(pp.Relative ? "relative" : "absolute")} {pp.X},{pp.Y},{pp.Z})");
            if (p.Rotation is { } pr) Console.WriteLine($"         rotation {Deg(w.Rot)} -> {Deg(rot)} deg  ({(pr.Relative ? "relative" : "absolute")})");
            if (p.Scale is { } ps) Console.WriteLine($"         scale {w.Scale:0.00} -> {scale:0.00}  ({(ps.Absolute ? "absolute" : "multiplied")} {ps.Value})");
            if (p.FlagsSet != 0 || p.FlagsUnset != 0) Console.WriteLine($"         flags set 0x{p.FlagsSet:X8} cleared 0x{p.FlagsUnset:X8}{(dis != w.Disabled ? $" -> initially disabled {dis}" : "")}");
        }
        if (res.PerSession) Console.WriteLine("      per-session: a chanceR line without a seed — the game re-rolls it every launch");
        foreach (var n in res.Notes) Console.WriteLine($"      note: {n}");
    }
    rows.Add(new
    {
        formKey = key.ToString(), formId = RuntimeId(key), arm, cell = CellOf(w.Cell), status,
        recordBase = recordBase.ToString(), recordBaseLabel = BaseLabel(recordBase), recordMesh = MeshOf(recordBase),
        effectiveBase = res?.EffectiveBase.ToString() ?? recordBase.ToString(), effectiveBaseFormId = RuntimeId(res?.EffectiveBase ?? recordBase),
        effectiveLabel = BaseLabel(res?.EffectiveBase ?? recordBase), effectiveMesh = MeshOf(res?.EffectiveBase ?? recordBase),
        swapped = res?.Swapped ?? false, random = res?.Random ?? false, perSession = res?.PerSession ?? false, leveled = res?.Leveled ?? false,
        alternatives = res?.Alternatives.Select(a => new { @base = a.Base.ToString(), formId = RuntimeId(a.Base), label = BaseLabel(a.Base), probability = a.Probability, from = a.From?.Where.ToString(), viaLeveledItem = a.ViaLeveledItem }).ToList(),
        recordPosition = new[] { w.Pos.X, w.Pos.Y, w.Pos.Z }, recordRotationDeg = new[] { float.RadiansToDegrees(w.Rot.X), float.RadiansToDegrees(w.Rot.Y), float.RadiansToDegrees(w.Rot.Z) }, recordScale = w.Scale,
        position = new[] { pos.X, pos.Y, pos.Z }, rotationDeg = new[] { float.RadiansToDegrees(rot.X), float.RadiansToDegrees(rot.Y), float.RadiansToDegrees(rot.Z) }, scale,
        initiallyDisabled = dis, propertiesFrom = res?.PropertiesFrom?.ToString(), propertiesSource = res?.PropertiesSource, propertiesRandom = res?.PropertiesRandom ?? false,
        notes = res?.Notes,
    });
}

foreach (var spec in cells)
{
    FormKey? ck = null;
    try { ck = FormKey.Factory(spec); } catch { }
    if (ck is null)
    {
        var hits = idx.CellName.Where(kv => string.Equals(kv.Value.Edid, spec, StringComparison.OrdinalIgnoreCase)).Select(kv => kv.Key).ToList();
        if (hits.Count == 0) { Console.WriteLine($"\ncell '{spec}': no cell with that EditorID in the order"); continue; }
        if (hits.Count > 1) Console.WriteLine($"\ncell '{spec}': {hits.Count} cells carry that EditorID — using {hits[0]}");
        ck = hits[0];
    }
    var members = idx.RefCell.Where(kv => kv.Value.Cell == ck.Value).Select(kv => kv.Key)
                     .Where(k => idx.History.Winning(k) is { Deleted: false }).ToList();
    int swappedHere = members.Count(k => idx.BosSwaps.TryGetValue(k, out var r) && r.Swapped);
    int mayHere = members.Count(k => idx.BosSwaps.TryGetValue(k, out var r) && !r.Swapped && r.Random);
    int propsHere = members.Count(k => idx.BosSwaps.TryGetValue(k, out var r) && r.Properties is { IsValid: true });
    Console.WriteLine($"\n==== cell {CellOf(ck.Value)} {ck}: {members.Count} winning refs; {swappedHere} swapped, {mayHere} may swap, {propsHere} with property overrides ====");
    int shown = 0;
    foreach (var k in members.Where(k => idx.BosSwaps.ContainsKey(k)).OrderBy(k => k.ToString(), StringComparer.Ordinal))
    { if (shown++ >= listN) { Console.WriteLine($"   ... {members.Count(idx.BosSwaps.ContainsKey) - listN} more (--list N)"); break; } Row(k, false); }
}
foreach (var spec in refs)
{
    FormKey rk;
    try { rk = FormKey.Factory(spec); } catch { Console.WriteLine($"\n'{spec}' is not a FormKey (XXXXXX:Plugin.esp)"); continue; }
    Console.WriteLine();
    Row(rk, false);
}
if (allSwapped)
{
    Console.WriteLine($"\n==== every swapped reference in the order ({idx.BosSwaps.Count(kv => kv.Value.Swapped):N0}) ====");
    int shown = 0;
    foreach (var (k, r) in idx.BosSwaps.Where(kv => kv.Value.Swapped).OrderBy(kv => kv.Key.ToString(), StringComparer.Ordinal))
    { if (shown++ >= listN) { Console.WriteLine("   ... (--list N)"); break; } Row(k, true); }
}
if (jsonPath is not null)
{
    File.WriteAllText(jsonPath, JsonSerializer.Serialize(new { profile = profileName, plugins = paths.Count, inis = inis.Select(i => i.Name).ToList(), rows },
                                                         new JsonSerializerOptions { WriteIndented = true }));
    Console.WriteLine($"\nwrote {rows.Count} row(s) -> {jsonPath}");
}
Console.WriteLine($"\ndone in {sw.Elapsed.TotalSeconds:0.0}s");
return 0;
