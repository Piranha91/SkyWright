using System.Diagnostics;
using System.Text.Json;
using HarnessShared;
using HousecarlCore;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Skyrim;
using HousecarlCore.Progress;

// PLACEMENT DEPENDENCY — the records-layer pass for stranded support and orphaned attachment
// (roadmap 4.5 / 4.6, the cheap half).
//
// The two classes, in Piranha's words: mod A puts apples on a table, mod B moves the table, nothing
// moves the apples; mod A hangs a sconce on a wall, mod B moves the wall, the sconce is neither
// moved nor deleted. Both are one failure — an object left behind when the thing it depends on
// moved — and "rests on" is a contact fact no record declares, so most of it is geometry. But a
// minority of references DO declare a host: an enable parent (XESP), a linked reference (XLKR), an
// activate parent (XAPR). Those edges are authored statements that "this belongs with that", and
// when the host moves and the dependent does not, this pass can say so without reading a mesh.
//
// What it measures before it lists anything: how small the declared slice is. The fraction of refs
// in mover-touched cells that carry ANY host edge is the ceiling on what a records-only pass can
// see, and it is the number that sizes the geometry half. The first live run added a second cut:
// the host class alone is not enough, because the physical-host rows were all actors and triggers
// linked to furniture, doors and FX — AI and control relationships, not support. A row is
// SUPPORT-SHAPED only when the host is physical AND the dependent is physical, and that is the
// count the two classes are about. The spot-checks added a third cut: the AUTHORED OFFSET between
// host and dependent. An object resting on another is within its footprint; the one support-shaped
// row on the first order was a bench linked to a chopping block several hundred units away — a work-station link.
// So every support-shaped count is also split by offset, and CONTACT-PLAUSIBLE means within 128u.
//
// The rule is DetectorModel.PlacementDependency (proofprobe-owned, with a synthetic order written
// with Mutagen behind it); the facts come from DependencyIndex over PlacementHistory — the same
// history driftprobe's scenario check runs on, moved to _shared for this. Everything here is I/O
// and tallies. Standing rules applied: differential only (a LeftBehind row means the declared
// arrangement changed, never that anything floats); the floor measured and split by population
// before any count is read (rules 2 and 12); unjudgeable counted, never cleared (rule 6); the
// movement census must reproduce driftprobe --generic before anything else is believed (rule 8).
//
// Since Q32 (2026-09-06) the host's presence is its EFFECTIVE enable state, resolved through its XESP
// chain SEPARATELY in each of the two configurations the rule compares — the edge author's view and the
// winning order — instead of its own initially-disabled checkbox, which the engine ignores wherever a
// reference has a parent. A chain that cannot be read to the end in either view withholds the row under
// its own reason rather than being judged on the resolver's fallback. Section 3b reports the coverage.
//
// Since Q35 (2026-09-07) a row is also withheld when the DEPENDENT's record is a deletion in the edge author's own
// view while the winning order still has it. An edge is only extracted from a record that carried a placement, so the
// author is always a toucher and the RECORD view selects its own record — which means the position that view offers is
// either that deleted record's own leftover data or, when the author merely repeated an earlier plugin's deleted
// placement and so left no snap, an older LIVE position its record contradicts. Judging against it fed the same-space
// test, the authored offset and the re-placement comparison, so LeftBehind, MovedTogether and the third-party read
// could all rest on it. Section 3b reports the population, the rows and the record each view chose.
//
// NOT verified in game, and no geometry has been checked. A LeftBehind row is a candidate.
//
// usage: depprobe [--list N] [--threshold U] [--json FILE] [--bos on|off] [--enable X.esp]* [--disable X.esp]*
//                 [--instance DIR] [--game DIR] [--profile NAME]
//   --bos on|off    apply the Base Object Swapper layer (*_SWAP.ini) so every ref is judged by the base the game loads
//                   (default on; _shared/BosHook.cs); off = the record bases

// Nothing is compiled in — see _shared/HarnessTarget.cs. Flags win over the environment and over
// harness.local.json; Require() runs AFTER them.
using var __run = RunLog.BeginProcess("depprobe", args);

string Instance = HarnessTarget.Instance;
string GamePath = HarnessTarget.Game;
string profileName = HarnessTarget.Profile;
{
    for (int i = 0; i < args.Length; i++)
    {
        if (args[i] == "--instance" && i + 1 < args.Length) Instance = args[i + 1];
        else if (args[i] == "--game" && i + 1 < args.Length) GamePath = args[i + 1];
        else if (args[i] == "--profile" && i + 1 < args.Length) profileName = args[i + 1];
    }
}
Instance = HarnessTarget.Require(Instance, "MO2 instance directory", "instance", "--instance");
GamePath = HarnessTarget.Require(GamePath, "game directory", "game", "--game");
profileName = HarnessTarget.Require(profileName, "MO2 profile name", "profile", "--profile");
string profileDir = Path.Combine(Instance, "profiles", profileName);
string modsDir = Path.Combine(Instance, "mods");
string overwriteDir = Path.Combine(Instance, "overwrite");
string dataDir = Path.Combine(GamePath, "Data");

int listN = 25;
float threshold = PlacementHistory.MoveThreshold;
string? jsonPath = null;
bool bosOn = true;
var enablePlugins = new List<string>();
var disablePlugins = new List<string>();
string? orderFile = null;
for (int i = 0; i < args.Length; i++)
{
    if (args[i] == "--list" && i + 1 < args.Length) listN = int.Parse(args[++i]);
    else if (args[i] == "--threshold" && i + 1 < args.Length) threshold = float.Parse(args[++i]);
    else if (args[i] == "--json" && i + 1 < args.Length) jsonPath = args[++i];
    else if (args[i] == "--order" && i + 1 < args.Length) orderFile = args[++i];
    else if (args[i] == "--enable" && i + 1 < args.Length) enablePlugins.Add(args[++i]);
    else if (args[i] == "--disable" && i + 1 < args.Length) disablePlugins.Add(args[++i]);
    else if (args[i] == "--bos" && i + 1 < args.Length) bosOn = BosHook.ParseFlag(args[++i]) ?? throw new ArgumentException("--bos takes on|off");
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
Console.WriteLine($"load order: {paths.Count} plugins ({sw.ElapsedMilliseconds} ms)   threshold {threshold:0}u\n");
HarnessOrderCli.Print(cfg);

// ---------------------------------------------------------------------------------------
// Pass 1 — every plugin once, into the shared index.
// ---------------------------------------------------------------------------------------
// --bos on (default): the Base Object Swapper reader replaces each reference's record base with the base the
// game loads, and a moving override becomes a placement step under a pseudo-plugin after the last real one
// (_shared/BosHook.cs). --bos off: the record bases, as before 2026-09-04.
var idx = new DependencyIndex { Bos = bosOn ? new BosFormIndex() : null };
var pluginKey = new ModKey[paths.Count];
var masterIdx = new HashSet<int>[paths.Count + (bosOn ? 1 : 0)];
var lightness = new List<(ModKey, bool)>();
var unreadable = new List<string>();
__run.Feature("plugins", paths.Count, "plugins").Stage("scan", "reading plugins", paths.Count, "plugins");

for (int p = 0; p < paths.Count; p++)
{
    __run.Advance();
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
    if ((p + 1) % 150 == 0) Console.WriteLine($"   ... {p + 1}/{paths.Count} plugins, {idx.Placements:N0} placements, {sw.Elapsed.TotalSeconds:0}s");
}
int bosPlugin = bosOn ? paths.Count : -1;
if (bosOn) masterIdx[bosPlugin] = BosHook.PseudoMasters(paths.Count);
var closure = DetectorModel.MasterClosure(masterIdx);
if (bosOn)
{
    idx.Bos!.SetLoadOrder(lightness);
    var bosLayer = BosHook.Load(BosHook.LooseRoots(overwriteDir, modsDir, dataDir, cfg.EnabledMods), idx.Bos);
    var bosRep = BosHook.Apply(idx, bosLayer, bosPlugin);
    Console.WriteLine($"\n   BOS: {bosLayer.Inis.Count} *_SWAP.ini read ({bosLayer.LinesRead:N0} lines, {bosLayer.LinesFailed:N0} the game would refuse, {bosLayer.Anomalies.Count:N0} anomalies — bosprobe lists them)");
    foreach (var l in bosRep.Summary()) Console.WriteLine("   " + l);
}
else Console.WriteLine("\n   BOS: off — record bases (--bos on to apply the *_SWAP.ini layer)");
Console.WriteLine($"\nindexed {idx.Placements:N0} placements over {idx.History.Count:N0} refs; {idx.EdgeCount:N0} declared host edges; "
                + $"{idx.Deletions.EverCount:N0} refs deleted somewhere  ({sw.Elapsed.TotalSeconds:0.0}s)");
Console.WriteLine($"   plugins that could not be read (contribute nothing, counted per rule 6): {unreadable.Count:N0}"
                + (unreadable.Count > 0 ? "  — " + string.Join(", ", unreadable) : ""));

var Vanilla = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
{ "Skyrim.esm", "Update.esm", "Dawnguard.esm", "HearthFires.esm", "Dragonborn.esm", "SkyrimVR.esm", "_ResourcePack.esl" };
const string Ussep = "Unofficial Skyrim Special Edition Patch.esp";
string PluginName(int p) => p < 0 ? "(none)" : p == bosPlugin ? BosApplyReport.PseudoPluginName : pluginKey[p].FileName.String;
bool IsVanilla(int p) => p >= 0 && Vanilla.Contains(PluginName(p));
bool IsUssep(int p) => p >= 0 && string.Equals(PluginName(p), Ussep, StringComparison.OrdinalIgnoreCase);
string ClassOf(int p) => IsVanilla(p) ? "Bethesda" : IsUssep(p) ? "USSEP" : "mod";
string CellOf(FormKey c) => idx.CellName.TryGetValue(c, out var v) ? (v.Edid ?? c.ToString()) + (v.Interior ? " (int)" : " (ext)") : c.ToString();
string RefLabel(FormKey k)
{
    var ri = idx.RefInfo.TryGetValue(k, out var r) ? r : ("?", null);
    var bi = idx.RefBase.TryGetValue(k, out var b) && idx.BaseInfo.TryGetValue(b, out var x) ? x : ("?", null);
    return $"{k}{(ri.Item2 is null ? "" : " " + ri.Item2)} [{bi.Item1}{(bi.Item2 is null ? "" : " " + bi.Item2)}]";
}

// ---------------------------------------------------------------------------------------
// 1. The movement census — driftprobe --generic's numbers, reproduced (rule 8: the plumbing first).
// ---------------------------------------------------------------------------------------
long changed = 0, relocated = 0, movedFar = 0;
var movedRefs = new HashSet<FormKey>();
var movedCells = new HashSet<FormKey>();
var moverPlugins = new HashSet<int>();
var moverPairs = new Dictionary<string, int>();
foreach (var (key, h) in idx.History.Refs)
{
    if (h.Count < 2) continue;
    changed++;
    var first = h[0]; var last = h[^1];
    switch (PlacementHistory.Classify(first, last, threshold))
    {
        case Movement.Relocated: relocated++; break;
        case Movement.MovedFar: movedFar++; break;
        default: continue;
    }
    movedRefs.Add(key);
    movedCells.Add(first.Cell); movedCells.Add(last.Cell);
    if (last.Plugin != first.Plugin)
    {
        moverPlugins.Add(last.Plugin);
        var k = $"{PluginName(first.Plugin)}  -> moved by ->  {PluginName(last.Plugin)}";
        moverPairs[k] = moverPairs.GetValueOrDefault(k) + 1;
    }
}
var moverEditedCells = new HashSet<FormKey>();
foreach (var (_, h) in idx.History.Refs)
    foreach (var s in h) if (moverPlugins.Contains(s.Plugin)) moverEditedCells.Add(s.Cell);

Console.WriteLine();
Console.WriteLine("==== 1. MOVEMENT CENSUS (must equal driftprobe --generic on the same order) ====");
Console.WriteLine($"   refs whose placement any plugin altered : {changed:N0} of {idx.History.Count:N0}");
Console.WriteLine($"   ...relocated to another cell            : {relocated:N0}");
Console.WriteLine($"   ...moved more than {threshold:0}u within its cell  : {movedFar:N0}");
Console.WriteLine($"   moved refs (both)                       : {movedRefs.Count:N0}   in {movedCells.Count:N0} cells (source + destination)");
Console.WriteLine($"   plugins that moved something            : {moverPlugins.Count:N0}   touching {moverEditedCells.Count:N0} cells with any placement edit");
foreach (var g in moverPairs.OrderByDescending(g => g.Value).Take(Math.Min(listN, 12)))
    Console.WriteLine($"   {g.Value,6}  {g.Key}");

// ---------------------------------------------------------------------------------------
// 2. The declared slice — how much of the world a records-only pass can even ask about.
// ---------------------------------------------------------------------------------------
var edgesByDep = idx.Edges.GroupBy(e => e.Dependent).ToDictionary(g => g.Key, g => g.ToList());
var sliceJson = new Dictionary<string, object>();
void Slice(string label, string key, Func<Snap, bool> inSlice)
{
    long refs = 0, withEdge = 0;
    var edgesByKind = new Dictionary<string, long>();
    var refsByKind = new Dictionary<string, long>();
    foreach (var (k, h) in idx.History.Refs)
    {
        var win = h[^1];
        if (!inSlice(win)) continue;
        refs++;
        if (!edgesByDep.TryGetValue(k, out var es)) continue;
        withEdge++;
        foreach (var e in es) edgesByKind[e.Kind.ToString()] = edgesByKind.GetValueOrDefault(e.Kind.ToString()) + 1;
        foreach (var kind in es.Select(e => e.Kind.ToString()).Distinct()) refsByKind[kind] = refsByKind.GetValueOrDefault(kind) + 1;
    }
    Console.WriteLine($"   {label,-44} refs {refs,10:N0}   with any host edge {withEdge,8:N0}  ({Rate(withEdge, refs),6:0.00}%)");
    foreach (var kind in new[] { "EnableParent", "LinkedRef", "ActivateParent" })
        Console.WriteLine($"      {kind,-14} refs {refsByKind.GetValueOrDefault(kind),8:N0} ({Rate(refsByKind.GetValueOrDefault(kind), refs),5:0.00}%)   edges {edgesByKind.GetValueOrDefault(kind),8:N0}");
    sliceJson[key] = new { refs, withEdge, pct = Rate(withEdge, refs), refsByKind, edgesByKind };
}
Console.WriteLine();
Console.WriteLine("==== 2. THE DECLARED SLICE — refs that carry any host edge (XESP / XLKR / XAPR) ====");
Slice("whole order (context)", "all", _ => true);
Slice("cells holding a moved ref", "movedCells", s => movedCells.Contains(s.Cell));
Slice("cells any mover plugin edited at all", "moverEditedCells", s => moverEditedCells.Contains(s.Cell));

// ---------------------------------------------------------------------------------------
// 3. Every edge judged.
// ---------------------------------------------------------------------------------------
var rows = new List<Judged>(idx.EdgeCount);
foreach (var e in idx.Edges)
{
    var r = idx.Judge(e, closure);
    var (verdict, reason, why) = DetectorModel.PlacementDependency(r.Facts, threshold);
    var pop = DetectorModel.ClassifyDependencyPopulation(IsVanilla(r.Mover), IsVanilla(e.Author), IsUssep(r.Mover));
    rows.Add(new Judged(r, verdict, reason, why, pop));
}
// "This row is ABOUT a host that moved" — the population split's membership. Two blockers can fire BEFORE the
// movement test — the chain-unresolved one (Q32) and the dependent's invalid baseline (Q35), because neither a clean
// verdict nor a withheld one may rest on a value that is not a reading — so those rows join only when the facts say
// the host actually moved; that keeps the split's denominators meaning what they did.
bool FactsSayMoved(Judged j) => j.Row.Facts.HostChangedSpace || j.Row.Facts.HostMoved > threshold;
bool HostMovedRow(Judged j) => j.Verdict is DependencyVerdict.LeftBehind or DependencyVerdict.MovedTogether or DependencyVerdict.DependentRemoved
    || j.Reason is DependencyBlocker.HostDisabled or DependencyBlocker.CrossCell or DependencyBlocker.DependentMovedByThirdParty
    || (j.Reason is DependencyBlocker.HostStateUnresolved or DependencyBlocker.DependentDeletedInAuthorView && FactsSayMoved(j));
static bool SupportShaped(Judged j) => j.Verdict == DependencyVerdict.LeftBehind && j.Row.Facts.Host == HostClass.Physical && j.Row.Dependent == DependentClass.Physical;
const float ContactOffset = 128f;
static bool ContactPlausible(Judged j) => SupportShaped(j) && j.Row.AuthoredOffset <= ContactOffset;
static string OffsetBand(float d) => float.IsNaN(d) ? "n/a" : d <= 64 ? "<=64u" : d <= 128 ? "<=128u" : d <= 256 ? "<=256u" : d <= 1024 ? "<=1024u" : ">1024u";

Console.WriteLine();
Console.WriteLine("==== 3. EVERY EDGE JUDGED ====");
Console.WriteLine($"   host class of all edges:  " + string.Join("   ", new[] { HostClass.Physical, HostClass.Marker, HostClass.Other }
    .Select(c => $"{c} {rows.Count(j => j.Row.Facts.Host == c):N0}")));
foreach (var kind in Enum.GetValues<HostEdgeKind>())
    Console.WriteLine($"      {kind,-14} " + string.Join("   ", new[] { HostClass.Physical, HostClass.Marker, HostClass.Other }
        .Select(c => $"{c} {rows.Count(j => j.Row.Edge.Kind == kind && j.Row.Facts.Host == c),7:N0}")));
Console.WriteLine($"   dependent class of all edges: " + string.Join("   ", Enum.GetValues<DependentClass>()
    .Select(c => $"{c} {rows.Count(j => j.Row.Dependent == c):N0}")));
var suppEdges = rows.Where(j => j.Row.Facts.Host == HostClass.Physical && j.Row.Dependent == DependentClass.Physical).ToList();
Console.WriteLine($"   physical host AND physical dependent (the support-shaped edges): {suppEdges.Count:N0}");
Console.WriteLine($"      by authored host-dependent offset: " + string.Join("   ", new[] { "<=64u", "<=128u", "<=256u", "<=1024u", ">1024u", "n/a" }
    .Select(b => $"{b} {suppEdges.Count(j => OffsetBand(j.Row.AuthoredOffset) == b):N0}")));
Console.WriteLine($"      contact-plausible (offset <= {ContactOffset:0}u): {suppEdges.Count(j => j.Row.AuthoredOffset <= ContactOffset):N0}   <- the declared slice the two classes are actually about");
Console.WriteLine($"   verdicts:");
foreach (var v in Enum.GetValues<DependencyVerdict>())
    Console.WriteLine($"      {v,-18} {rows.Count(j => j.Verdict == v),9:N0}");
Console.WriteLine($"   unjudgeable by reason (counted, never cleared):");
foreach (var b in Enum.GetValues<DependencyBlocker>().Where(b => b != DependencyBlocker.None))
    Console.WriteLine($"      {b,-28} {rows.Count(j => j.Reason == b),9:N0}");
// The absent-host rows: the handoff counts them; here is what they would read if judged. Hypothetical, and it clears
// nothing — a chain-unresolved row is NOT in this count, because lifting the absence gate does not make an unread
// chain read (the unresolved gate fires first, and these rows never reached it).
int disabledWouldBe = rows.Where(j => j.Reason == DependencyBlocker.HostDisabled)
    .Count(j => DetectorModel.PlacementDependency(j.Row.Facts with { HostAbsent = false }, threshold).Verdict == DependencyVerdict.LeftBehind);
Console.WriteLine($"      (of the HostDisabled rows — the host absent at load in one of the two views — {disabledWouldBe:N0} would read LeftBehind if an absent host were judged like a present one)");

// ---------------------------------------------------------------------------------------
// 3b. Enable-state coverage (Q32) — the host's effective state per compared view, and what could not be read.
// ---------------------------------------------------------------------------------------
int edgesUnresolvedState = rows.Count(j => j.Row.Facts.HostStateUnresolved);
int edgesAbsent = rows.Count(j => j.Row.Facts.HostAbsent);
int edgesViaParentThen = rows.Count(j => j.Row.HostStateThen.ViaParent);
int edgesViaParentNow = rows.Count(j => j.Row.HostStateNow.ViaParent);
int edgesFlagDisagrees = rows.Count(j => j.Row.HostNow is { } hn && j.Row.HostStateNow.ViaParent && hn.Disabled == j.Row.HostStateNow.Enabled);
var unresolvedHosts = rows.Where(j => j.Row.Facts.HostStateUnresolved).Select(j => j.Row.Edge.Host).Distinct().Count();
Console.WriteLine();
Console.WriteLine("==== 3b. HOST ENABLE STATE — resolved per compared view, not read off the host's own checkbox ====");
Console.WriteLine($"   policy: effective XESP chain state; \"then\" = the edge author's view (its own and its transitive masters' versions),");
Console.WriteLine($"           \"now\" = the winning order{(bosOn ? " including the BOS layer's flag overrides" : " (BOS off)")}. A parent's state XOR the opposite flag; the player is always enabled.");
Console.WriteLine($"   edges judged                                  : {rows.Count:N0}");
Console.WriteLine($"   ...whose host takes its state FROM A PARENT   : then {edgesViaParentThen:N0}   now {edgesViaParentNow:N0}   <- the host's own checkbox has no authority over these; the line below is where it also gave the wrong answer");
Console.WriteLine($"   ...where the winner's chain state DISAGREES with the host's own checkbox: {edgesFlagDisagrees:N0}");
Console.WriteLine($"   ...host absent at load in one of the two views : {edgesAbsent:N0}");
Console.WriteLine($"   ...host chain UNRESOLVED in one of them        : {edgesUnresolvedState:N0}   over {unresolvedHosts:N0} distinct hosts (withheld, never judged)");
Console.WriteLine($"   references the chain was asked about (chain elements included, the denominator for the two below):");
Console.WriteLine($"      winner view          {idx.Enable.WinnerQueried,9:N0}   unresolved {idx.Enable.WinnerUnresolved,8:N0}   absent at load {idx.Enable.WinnerAbsent,8:N0}");
Console.WriteLine($"      authoring views      {idx.Enable.ViewQueried,9:N0}   unresolved {idx.Enable.ViewUnresolved,8:N0}   absent at load {idx.Enable.ViewAbsent,8:N0}   over {idx.Enable.ViewsBuilt:N0} distinct authors");
Console.WriteLine($"   enable-history size: {idx.Enable.Tracked:N0} refs carry a non-default statement; {idx.Enable.RecordOnly:N0} records had no placement data (deletions)");
Console.WriteLine($"   BOS re-flagged a reference that ALSO has an enable parent: {idx.Enable.BosReflaggedUnderParent.Count:N0}"
                + " (measured in game, Q33: the parent decides and the override changes neither the flag nor the");
Console.WriteLine( "   object's presence — these resolve through the chain like any other reference, and are counted here only as the population)");
// Q34 (2026-09-06): the deletion guard reads the record the VIEW selects, so "deleted somewhere in this order" and
// "deleted in this view" are now different numbers and both are printed. Q32's warning counted the disagreement between
// the old latest-only map and the winning record; it is replaced here by the population that disagreement was about —
// references a later record restored — with its denominators, the views that saw the deletion, and the identities.
var restored = idx.Deletions.RestoredAtWinner().ToList();
var restoredSet = restored.ToHashSet();
int restoredHosts = rows.Select(j => j.Row.Edge.Host).Distinct().Count(restoredSet.Contains);
int restoredDeps = rows.Select(j => j.Row.Edge.Dependent).Distinct().Count(restoredSet.Contains);
int hostDeletedNow = rows.Count(j => j.Row.HostDeletedNow);
int hostDeletedThenOnly = rows.Count(j => j.Row.HostDeletedThen && !j.Row.HostDeletedNow);
int depDeletedThenOnly = rows.Count(j => j.Row.DependentDeletedThen && !j.Row.Facts.DependentDeleted);
// ...of which the ones the new gate actually withheld: a row whose host was already deleted, or was the player, or
// had no placement anywhere, is stopped by an earlier blocker and never reaches it. The two counts are kept apart so
// neither is read as the other.
int depBaselineWithheld = rows.Count(j => j.Reason == DependencyBlocker.DependentDeletedInAuthorView);
Console.WriteLine($"   refs deleted somewhere in the order: {idx.Deletions.EverCount:N0}; of those, RESTORED by a later record");
Console.WriteLine($"   (not deleted at the winner): {restored.Count:N0} — {restoredHosts:N0} appear as a host and {restoredDeps:N0} as a dependent");
Console.WriteLine($"   across {rows.Count:N0} edges. A restoration that repeats an existing position is no placement step, so");
Console.WriteLine($"   these are exactly the references the pre-Q34 guard called deleted for good.");
Console.WriteLine($"   HostDeleted rows by the view that saw it: {hostDeletedNow:N0} deleted in the WINNING order,");
Console.WriteLine($"   {hostDeletedThenOnly:N0} deleted only in the EDGE AUTHOR's view (its own record, or one of its masters');");
Console.WriteLine($"   dependents deleted in the EDGE AUTHOR's view but restored at the winner: {depDeletedThenOnly:N0}");
Console.WriteLine($"   (Q35: that view holds no placement to compare against — a record it deletes keeps whatever position data it");
Console.WriteLine($"   carried, and the engine reads none of it — so those rows are WITHHELD under DependentDeletedInAuthorView");
Console.WriteLine($"   rather than judged. {depBaselineWithheld:N0} of them reached that gate; the rest were already withheld by an earlier one.)");
if (depBaselineWithheld > 0)
{
    Console.WriteLine($"   the withheld rows (up to 20), with the record each view selected:");
    foreach (var j in rows.Where(j => j.Reason == DependencyBlocker.DependentDeletedInAuthorView).Take(20))
        Console.WriteLine($"      {RefLabel(j.Row.Edge.Dependent)} --{j.Row.Edge.Kind}--> {RefLabel(j.Row.Edge.Host)}"
            + $"  edge by {PluginName(j.Row.Edge.Author)}; the author's view selected {PluginName(j.Row.DependentRecordProviderThen)}'s record"
            + $"; statements: {(idx.Deletions.Of(j.Row.Edge.Dependent) is { } dst ? string.Join(" -> ", dst.Select(e => $"{PluginName(e.Plugin)}{(e.Deleted ? " deletes" : " restores")}")) : "(none)")}");
}
if (restored.Count > 0)
{
    Console.WriteLine($"   the restored references (up to 20), with the deletion history each view selects from:");
    foreach (var k in restored.OrderBy(k => k.ID).Take(20))
        Console.WriteLine($"      {k}  {RefLabel(k)}  statements: "
            + string.Join(" -> ", idx.Deletions.Of(k)!.Select(e => $"{PluginName(e.Plugin)}{(e.Deleted ? " deletes" : " restores")}")));
}

var moved = rows.Where(HostMovedRow).ToList();
var movedHosts = moved.Select(j => j.Row.Edge.Host).Distinct().ToList();
Console.WriteLine();
Console.WriteLine($"   moved hosts (by the rule's authoring-view test) with at least one dependent: {movedHosts.Count:N0}");
foreach (var c in Enum.GetValues<HostClass>())
    Console.WriteLine($"      {c,-9} {moved.Where(j => j.Row.Facts.Host == c).Select(j => j.Row.Edge.Host).Distinct().Count(),7:N0}");
int censusHostsWithDeps = idx.Edges.Select(e => e.Host).Distinct().Count(movedRefs.Contains);
Console.WriteLine($"   moved refs (census, first -> winner) that are a host to any edge: {censusHostsWithDeps:N0} of {movedRefs.Count:N0}");
Console.WriteLine($"   dependents of moved hosts, by verdict:");
foreach (var g in moved.GroupBy(j => j.Verdict == DependencyVerdict.Unjudgeable ? $"Unjudgeable/{j.Reason}" : j.Verdict.ToString()).OrderByDescending(g => g.Count()))
    Console.WriteLine($"      {g.Key,-40} {g.Count(),7:N0}");
Console.WriteLine($"   LeftBehind rows by (host class, dependent class):");
foreach (var g in rows.Where(j => j.Verdict == DependencyVerdict.LeftBehind).GroupBy(j => (j.Row.Facts.Host, j.Row.Dependent)).OrderByDescending(g => g.Count()))
    Console.WriteLine($"      host {g.Key.Host,-9} dependent {g.Key.Dependent,-9} {g.Count(),7:N0}");
Console.WriteLine($"   SUPPORT-SHAPED LeftBehind rows (physical host, physical dependent): {rows.Count(SupportShaped):N0}"
                + $"   of which contact-plausible (authored offset <= {ContactOffset:0}u): {rows.Count(ContactPlausible):N0}");
Console.WriteLine($"   LeftBehind rows whose dependent the mover's side OVERRODE without moving it (seen, left — reported, not credited): "
                + $"{rows.Count(j => j.Verdict == DependencyVerdict.LeftBehind && j.Row.DependentTouchedByMoverSide):N0}");

// ---------------------------------------------------------------------------------------
// 4. The population split (rule 12): (mover class, edge-author class), over the moved-host rows.
// ---------------------------------------------------------------------------------------
Console.WriteLine();
Console.WriteLine("==== 4. POPULATION SPLIT of the moved-host rows — (mover, edge author) ====");
Console.WriteLine($"   {"population",-36} {"LeftBehind",10} {"  phys",6} {"  mark",6} {" other",6} {"support",8} {"contact",8} {"MovedTogether",14} {"Removed",8} {"Unjudg.",8} {"LB rate",8}");
foreach (var pop in Enum.GetValues<DependencyPopulation>())
{
    var rs = moved.Where(j => j.Population == pop).ToList();
    int lb = rs.Count(j => j.Verdict == DependencyVerdict.LeftBehind);
    int mt = rs.Count(j => j.Verdict == DependencyVerdict.MovedTogether);
    Console.WriteLine($"   {pop,-36} {lb,10:N0} {rs.Count(j => j.Verdict == DependencyVerdict.LeftBehind && j.Row.Facts.Host == HostClass.Physical),6:N0}"
                    + $" {rs.Count(j => j.Verdict == DependencyVerdict.LeftBehind && j.Row.Facts.Host == HostClass.Marker),6:N0}"
                    + $" {rs.Count(j => j.Verdict == DependencyVerdict.LeftBehind && j.Row.Facts.Host == HostClass.Other),6:N0}"
                    + $" {rs.Count(SupportShaped),8:N0} {rs.Count(ContactPlausible),8:N0}"
                    + $" {mt,14:N0} {rs.Count(j => j.Verdict == DependencyVerdict.DependentRemoved),8:N0} {rs.Count(j => j.Verdict == DependencyVerdict.Unjudgeable),8:N0}"
                    + $" {Rate(lb, lb + mt),7:0.0}%");
}
Console.WriteLine($"   (LB rate = LeftBehind / (LeftBehind + MovedTogether); support = LeftBehind with a physical host AND a physical dependent;");
Console.WriteLine($"    contact = support with an authored host-dependent offset <= {ContactOffset:0}u; the floor is the first row's)");
Console.WriteLine();
Console.WriteLine($"   LeftBehind rows by mover class x author class:");
foreach (var g in rows.Where(j => j.Verdict == DependencyVerdict.LeftBehind)
                      .GroupBy(j => (Mover: ClassOf(j.Row.Mover), Author: ClassOf(j.Row.Edge.Author))).OrderByDescending(g => g.Count()))
    Console.WriteLine($"      {g.Key.Mover,-9} moved, {g.Key.Author,-9} authored the edge : {g.Count(),7:N0}   "
                    + $"(physical {g.Count(j => j.Row.Facts.Host == HostClass.Physical):N0}, marker {g.Count(j => j.Row.Facts.Host == HostClass.Marker):N0}, other {g.Count(j => j.Row.Facts.Host == HostClass.Other):N0})");

// ---------------------------------------------------------------------------------------
// 5. The floor's own physical-host rows — the same-defect control is chosen from these.
// ---------------------------------------------------------------------------------------
void PrintRow(Judged j)
{
    var r = j.Row;
    string dist = r.Facts.HostChangedSpace ? "relocated" : $"{r.Facts.HostMoved,7:0}u";
    Console.WriteLine($"      {j.Row.Edge.Kind,-14} host {j.Row.Facts.Host,-8} dep {j.Row.Dependent,-8} {dist,9}  host {RefLabel(r.Edge.Host)}");
    Console.WriteLine($"         dependent {RefLabel(r.Edge.Dependent)}  in {CellOf(r.DepThen?.Cell ?? r.DepNow?.Cell ?? FormKey.Null)}"
                    + $"  authored offset {(float.IsNaN(r.AuthoredOffset) ? "n/a" : $"{r.AuthoredOffset:0}u")}"
                    + (r.DependentTouchedByMoverSide ? "  [mover's side overrode the dependent without moving it]" : ""));
    Console.WriteLine($"         edge by {PluginName(r.Edge.Author)}; host moved by {PluginName(r.Mover)}"
                    + (r.HostThen is { } ht && r.HostNow is { } hn ? $"; host {ht.Pos} -> {hn.Pos}" : ""));
}
Console.WriteLine();
Console.WriteLine("==== 5. THE FLOOR — Bethesda-on-Bethesda LeftBehind rows with a PHYSICAL host (control candidates) ====");
var floorPhys = rows.Where(j => j.Verdict == DependencyVerdict.LeftBehind && j.Population == DependencyPopulation.BethesdaFloor && j.Row.Facts.Host == HostClass.Physical)
                    .OrderByDescending(j => j.Row.Facts.HostChangedSpace).ThenByDescending(j => j.Row.Facts.HostMoved).ToList();
Console.WriteLine($"   {floorPhys.Count:N0} rows");
foreach (var j in floorPhys.Take(listN)) PrintRow(j);

// ---------------------------------------------------------------------------------------
// 6. Mod movers — the class. By mod pair, then the physical-host rows one by one.
// ---------------------------------------------------------------------------------------
Console.WriteLine();
Console.WriteLine("==== 6. MOD MOVERS — LeftBehind rows by (mover, edge author) pair; CANDIDATES, no geometry checked ====");
var modLb = rows.Where(j => j.Verdict == DependencyVerdict.LeftBehind && j.Population == DependencyPopulation.ModMover).ToList();
var byPair = modLb.GroupBy(j => (Mover: PluginName(j.Row.Mover), Author: PluginName(j.Row.Edge.Author)))
                  .OrderByDescending(g => g.Count(j => j.Row.Facts.Host == HostClass.Physical)).ThenByDescending(g => g.Count()).ToList();
Console.WriteLine($"   {modLb.Count:N0} rows over {byPair.Count:N0} pairs   (physical {modLb.Count(j => j.Row.Facts.Host == HostClass.Physical):N0}, "
                + $"marker {modLb.Count(j => j.Row.Facts.Host == HostClass.Marker):N0}, other {modLb.Count(j => j.Row.Facts.Host == HostClass.Other):N0})");
foreach (var g in byPair.Take(listN))
    Console.WriteLine($"   {g.Count(),5:N0}  phys {g.Count(j => j.Row.Facts.Host == HostClass.Physical),4:N0}  mark {g.Count(j => j.Row.Facts.Host == HostClass.Marker),4:N0}  other {g.Count(j => j.Row.Facts.Host == HostClass.Other),4:N0}   "
                    + $"{g.Key.Mover}  moved a host under an edge by  {g.Key.Author}"
                    + $"   [{string.Join(", ", g.GroupBy(j => j.Row.Edge.Kind).Select(k => $"{k.Count()} {k.Key}"))}]");
Console.WriteLine();
Console.WriteLine($"   ---- SUPPORT-SHAPED rows (physical host AND physical dependent — the 4.5 / 4.6 candidates), largest host move first ----");
var support = modLb.Where(SupportShaped).OrderByDescending(j => j.Row.Facts.HostChangedSpace).ThenByDescending(j => j.Row.Facts.HostMoved).ToList();
Console.WriteLine($"   {support.Count:N0} rows");
foreach (var j in support.Take(listN)) PrintRow(j);
Console.WriteLine();
Console.WriteLine($"   ---- the other physical-host rows (actor / control / other dependents — AI and trigger links, not support) ----");
foreach (var j in modLb.Where(j => j.Row.Facts.Host == HostClass.Physical && !SupportShaped(j))
                       .OrderByDescending(j => j.Row.Facts.HostChangedSpace).ThenByDescending(j => j.Row.Facts.HostMoved).Take(listN))
    PrintRow(j);

var ussepLb = rows.Where(j => j.Verdict == DependencyVerdict.LeftBehind && j.Population == DependencyPopulation.UssepMover).ToList();
Console.WriteLine();
Console.WriteLine($"==== 7. USSEP AS MOVER — {ussepLb.Count:N0} LeftBehind rows (physical host {ussepLb.Count(j => j.Row.Facts.Host == HostClass.Physical):N0}, support-shaped {ussepLb.Count(SupportShaped):N0}); support-shaped first, then physical-host ====");
foreach (var j in ussepLb.Where(j => j.Row.Facts.Host == HostClass.Physical).OrderByDescending(SupportShaped).ThenByDescending(j => j.Row.Facts.HostMoved).Take(Math.Min(listN, 12)))
    PrintRow(j);
var floorAll = rows.Where(j => j.Verdict == DependencyVerdict.LeftBehind && j.Population == DependencyPopulation.BethesdaFloor).ToList();
Console.WriteLine();
Console.WriteLine($"==== 7b. THE FLOOR, every row — {floorAll.Count:N0} Bethesda-on-Bethesda LeftBehind rows (support-shaped {floorAll.Count(SupportShaped):N0}) ====");
foreach (var j in floorAll.OrderByDescending(SupportShaped).ThenByDescending(j => j.Row.Facts.HostMoved).Take(listN)) PrintRow(j);
var vanUnderMod = rows.Where(j => j.Verdict == DependencyVerdict.LeftBehind && j.Population == DependencyPopulation.VanillaMoverUnderModEdge).ToList();
Console.WriteLine();
Console.WriteLine($"==== 8. VANILLA MOVER UNDER A MOD'S EDGE — {vanUnderMod.Count:N0} rows (the authoring-view assumption is weakest here; kept apart) ====");
foreach (var g in vanUnderMod.GroupBy(j => (PluginName(j.Row.Mover), PluginName(j.Row.Edge.Author))).OrderByDescending(g => g.Count()).Take(Math.Min(listN, 10)))
    Console.WriteLine($"   {g.Count(),5:N0}  {g.Key.Item1}  moved a host under an edge by  {g.Key.Item2}");

// ---------------------------------------------------------------------------------------
// --json: the summary and every LeftBehind row, for the spot-checks and the before/after diffs.
// ---------------------------------------------------------------------------------------
if (jsonPath is not null)
{
    object RowJson(Judged j)
    {
        var r = j.Row;
        var ri = idx.RefInfo.TryGetValue(r.Edge.Dependent, out var d) ? d : ("?", null);
        var hb = idx.RefBase.TryGetValue(r.Edge.Host, out var b) && idx.BaseInfo.TryGetValue(b, out var x) ? x : ("?", null);
        var dbse = idx.RefBase.TryGetValue(r.Edge.Dependent, out var db) && idx.BaseInfo.TryGetValue(db, out var y) ? y : ("?", null);
        return new
        {
            dependent = r.Edge.Dependent.ToString(), depType = ri.Item1, depEdid = ri.Item2, depBaseType = dbse.Item1, depBaseEdid = dbse.Item2,
            host = r.Edge.Host.ToString(), hostBaseType = hb.Item1, hostBaseEdid = hb.Item2, hostClass = r.Facts.Host.ToString(),
            depClass = r.Dependent.ToString(), supportShaped = SupportShaped(j), contactPlausible = ContactPlausible(j),
            authoredOffset = float.IsNaN(r.AuthoredOffset) ? (float?)null : r.AuthoredOffset, dependentTouchedByMoverSide = r.DependentTouchedByMoverSide,
            kind = r.Edge.Kind.ToString(),
            cell = r.DepThen is { } dt ? CellOf(dt.Cell) : null, cellKey = r.DepThen?.Cell.ToString(),
            hostMoved = float.IsNaN(r.Facts.HostMoved) ? (float?)null : r.Facts.HostMoved, hostChangedSpace = r.Facts.HostChangedSpace,
            hostThen = r.HostThen?.Pos.ToString(), hostNow = r.HostNow?.Pos.ToString(), dependentPos = r.DepNow?.Pos.ToString(),
            author = PluginName(r.Edge.Author), mover = PluginName(r.Mover), population = j.Population.ToString(),
            verdict = j.Verdict.ToString(), reason = j.Reason.ToString(), why = j.Why,
            // Q32: the host's effective enable state per compared view, and the chain evidence behind it. Additive —
            // every field above keeps its meaning.
            hostState = new
            {
                thenEnabled = r.HostStateThen.Enabled, thenViaParent = r.HostStateThen.ViaParent, thenResolved = r.HostStateThen.Resolved,
                nowEnabled = r.HostStateNow.Enabled, nowViaParent = r.HostStateNow.ViaParent, nowResolved = r.HostStateNow.Resolved,
                absent = r.Facts.HostAbsent, unresolved = r.Facts.HostStateUnresolved,
                hostOwnFlagNow = r.HostNow?.Disabled, hostOwnFlagThen = r.HostThen?.Disabled,
                enableParentNow = idx.Enable.Latest(r.Edge.Host).Parent?.ToString(),
                enableParentOppositeNow = idx.Enable.Latest(r.Edge.Host).Opposite,
                enableParentThen = idx.Enable.StatementAt(r.Edge.Host, idx.Enable.LastEligible(r.Edge.Host, r.Edge.Author, closure)).Parent?.ToString(),
                bosReflaggedUnderParent = idx.Enable.BosReflaggedUnderParent.Contains(r.Edge.Host),
            },
            // Q35: the dependent's record per compared view, and the history the selection reads. Present on every
            // row (null statements for the references nobody ever deleted, which is nearly all of them) so a row
            // withheld for having no baseline carries its own evidence rather than a summary count standing for it.
            dependentDeletion = new
            {
                deletedInAuthorView = r.DependentDeletedThen, deletedAtWinner = r.Facts.DependentDeleted,
                authorViewSelectedPlugin = PluginName(idx.Deletions.LastEligible(r.Edge.Dependent, r.Edge.Author, closure)),
                recordProviderThen = PluginName(r.DependentRecordProviderThen),
                recordProviderNow = PluginName(idx.Deletions.ProviderAt(r.Edge.Dependent, int.MaxValue)),
                statements = idx.Deletions.Of(r.Edge.Dependent)?.Select(e => new { plugin = PluginName(e.Plugin), e.Deleted }).ToList(),
            },
        };
    }
    var summary = new
    {
        generated = DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm:ssZ"), instance = Instance, profile = profileName, threshold,
        plugins = paths.Count, unreadable, placements = idx.Placements, refs = idx.History.Count, edges = idx.EdgeCount, deletedRefs = idx.Deletions.EverCount,
        restoredRefs = restored.Count, restoredAsHost = restoredHosts, restoredAsDependent = restoredDeps,
        census = new { changed, relocated, movedFar, movedRefs = movedRefs.Count, movedCells = movedCells.Count, moverPlugins = moverPlugins.Count, moverEditedCells = moverEditedCells.Count },
        slice = sliceJson,
        hostClassByKind = Enum.GetValues<HostEdgeKind>().ToDictionary(k => k.ToString(), k => Enum.GetValues<HostClass>().ToDictionary(c => c.ToString(), c => rows.Count(j => j.Row.Edge.Kind == k && j.Row.Facts.Host == c))),
        verdicts = Enum.GetValues<DependencyVerdict>().ToDictionary(v => v.ToString(), v => rows.Count(j => j.Verdict == v)),
        unjudgeable = Enum.GetValues<DependencyBlocker>().Where(b => b != DependencyBlocker.None).ToDictionary(b => b.ToString(), b => rows.Count(j => j.Reason == b)),
        disabledWouldBeLeftBehind = disabledWouldBe,
        // Q32 — the host enable-state policy, its per-view coverage and what could not be read. Counts of EDGES and
        // counts of REFERENCES are kept apart on purpose: an edge is a row, a reference may carry many rows and the
        // chain-element counts include hops no row asked about directly.
        hostEnableState = new
        {
            policy = "effective XESP chain state per compared view; then = the edge author's own and its transitive masters' versions, now = the winning order"
                   + (bosOn ? " with the BOS layer's flag overrides applied" : " (BOS off)"),
            edgesJudged = rows.Count,
            edgesHostViaParentThen = edgesViaParentThen, edgesHostViaParentNow = edgesViaParentNow,
            edgesWinnerChainDisagreesWithOwnFlag = edgesFlagDisagrees,
            edgesHostAbsent = edgesAbsent, edgesHostStateUnresolved = edgesUnresolvedState, unresolvedHosts,
            winnerQueried = idx.Enable.WinnerQueried, winnerUnresolved = idx.Enable.WinnerUnresolved, winnerAbsent = idx.Enable.WinnerAbsent,
            viewsBuilt = idx.Enable.ViewsBuilt, viewQueried = idx.Enable.ViewQueried,
            viewUnresolved = idx.Enable.ViewUnresolved, viewAbsent = idx.Enable.ViewAbsent,
            refsWithNonDefaultStatement = idx.Enable.Tracked, recordsWithoutPlacementData = idx.Enable.RecordOnly,
            bosReflaggedUnderParent = idx.Enable.BosReflaggedUnderParent.Count,
            hostDeletedRowsByView = new { winner = hostDeletedNow, authorViewOnly = hostDeletedThenOnly },
            dependentDeletedInAuthorViewButRestoredAtWinner = depDeletedThenOnly,
            // Q35: ...of which the rows the new gate actually withheld. Never larger than the line above; smaller
            // whenever an earlier blocker (a deleted or unplaceable host, the player) stopped the row first.
            dependentBaselineWithheldRows = depBaselineWithheld,
            restoredReferences = restored.OrderBy(k => k.ID).Select(k => new
            {
                formKey = k.ToString(), name = RefLabel(k),
                asHost = rows.Any(j => j.Row.Edge.Host == k), asDependent = rows.Any(j => j.Row.Edge.Dependent == k),
                statements = idx.Deletions.Of(k)!.Select(e => new { plugin = PluginName(e.Plugin), e.Deleted }).ToList(),
            }).ToList(),
        },
        dependentClassAll = Enum.GetValues<DependentClass>().ToDictionary(c => c.ToString(), c => rows.Count(j => j.Row.Dependent == c)),
        supportShapedEdges = rows.Count(j => j.Row.Facts.Host == HostClass.Physical && j.Row.Dependent == DependentClass.Physical),
        supportShapedLeftBehind = rows.Count(SupportShaped),
        contactPlausibleLeftBehind = rows.Count(ContactPlausible),
        contactOffset = ContactOffset,
        supportShapedEdgesByOffset = new[] { "<=64u", "<=128u", "<=256u", "<=1024u", ">1024u", "n/a" }.ToDictionary(b => b, b => suppEdges.Count(j => OffsetBand(j.Row.AuthoredOffset) == b)),
        leftBehindSeenByMoverSide = rows.Count(j => j.Verdict == DependencyVerdict.LeftBehind && j.Row.DependentTouchedByMoverSide),
        leftBehindByHostAndDependent = rows.Where(j => j.Verdict == DependencyVerdict.LeftBehind)
            .GroupBy(j => $"{j.Row.Facts.Host} host / {j.Row.Dependent} dependent").ToDictionary(g => g.Key, g => g.Count()),
        movedHosts = movedHosts.Count, censusHostsWithDeps,
        populations = Enum.GetValues<DependencyPopulation>().ToDictionary(p => p.ToString(), p => new
        {
            leftBehind = moved.Count(j => j.Population == p && j.Verdict == DependencyVerdict.LeftBehind),
            leftBehindPhysical = moved.Count(j => j.Population == p && j.Verdict == DependencyVerdict.LeftBehind && j.Row.Facts.Host == HostClass.Physical),
            leftBehindMarker = moved.Count(j => j.Population == p && j.Verdict == DependencyVerdict.LeftBehind && j.Row.Facts.Host == HostClass.Marker),
            leftBehindOther = moved.Count(j => j.Population == p && j.Verdict == DependencyVerdict.LeftBehind && j.Row.Facts.Host == HostClass.Other),
            supportShaped = moved.Count(j => j.Population == p && SupportShaped(j)),
            contactPlausible = moved.Count(j => j.Population == p && ContactPlausible(j)),
            movedTogether = moved.Count(j => j.Population == p && j.Verdict == DependencyVerdict.MovedTogether),
            removed = moved.Count(j => j.Population == p && j.Verdict == DependencyVerdict.DependentRemoved),
            unjudgeable = moved.Count(j => j.Population == p && j.Verdict == DependencyVerdict.Unjudgeable),
        }),
        leftBehindByClassPair = rows.Where(j => j.Verdict == DependencyVerdict.LeftBehind)
            .GroupBy(j => $"{ClassOf(j.Row.Mover)} moved / {ClassOf(j.Row.Edge.Author)} authored").ToDictionary(g => g.Key, g => g.Count()),
        modPairs = byPair.Select(g => new { mover = g.Key.Mover, author = g.Key.Author, rows = g.Count(),
                                            physical = g.Count(j => j.Row.Facts.Host == HostClass.Physical), marker = g.Count(j => j.Row.Facts.Host == HostClass.Marker), other = g.Count(j => j.Row.Facts.Host == HostClass.Other) }),
        rows = rows.Where(j => j.Verdict == DependencyVerdict.LeftBehind).Select(RowJson),
        // Q32 — the withheld rows, in their own collection. Putting the state evidence only on the LeftBehind rows
        // would hide every row the state gate withholds, which is exactly the population this change creates.
        withheldRows = rows.Where(j => j.Reason is DependencyBlocker.HostDisabled or DependencyBlocker.HostStateUnresolved
                                                 or DependencyBlocker.DependentDeletedInAuthorView).Select(RowJson),
    };
    File.WriteAllText(jsonPath, JsonSerializer.Serialize(summary, new JsonSerializerOptions { WriteIndented = true }));
    Console.WriteLine($"\nwrote {jsonPath}");
}

Console.WriteLine();
Console.WriteLine("NOT verified in game, and no geometry has been checked: a LeftBehind row says the declared arrangement changed");
Console.WriteLine("under the mover without the dependent following — a candidate for eyes, never that anything floats.");
Console.WriteLine($"elapsed {sw.Elapsed.TotalSeconds:0.0}s");
return 0;

static float Rate(long hit, long total) => total == 0 ? 0 : 100f * hit / total;

sealed record Judged(DependencyRow Row, DependencyVerdict Verdict, DependencyBlocker Reason, string Why, DependencyPopulation Population);
