using System.Diagnostics;
using System.Text.Json;
using HarnessShared;
using HousecarlCore;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Skyrim;
using HousecarlCore.Progress;

// MILESTONE 5 — CROSS-PLUGIN OBJECT OVERLAP, differential. The geometry charter's own motivating
// example: two mods drop a wall and a tree into the same cubic metre, SSEEdit reports "no conflict",
// and the player finds out by walking into it.
//
// Why this survives the ruling-out that killed milestone 4: the absolute question ("does this
// object overlap something?") drowns in the authored norm — clutter interpenetrates constantly.
// The CROSS-PLUGIN question does not have that excuse: an object placed by mod A intersecting an
// object placed by mod B, where neither declares a dependency on the other, is a coincidence of two
// authors who never saw each other's work. The floor still exists (two mods CAN legitimately meet —
// a city overhaul and a lantern mod decorating the same street) and is MEASURED here, not assumed:
// the same-source overlap rate is the "authored deliberately" baseline every cross-plugin number is
// read against, split by interior/exterior.
//
// The first cut (2026-08-27) stopped at the absolute count. A deep overlap says two boxes intersect
// NOW; it does not say who created the intersection or whether the later plugin could have seen the
// other object. This cut asks both, the way the clobber, needs-witness and placement-dependency
// detectors earned their findings: for every cross-responsible deep hit, with L the LATER of the two
// responsible plugins, rebuild the arm WITHOUT L's contribution (L's reference at its previous snap
// in the placement history, or absent) — no overlap there means L INTRODUCED it, overlap already
// there means L INHERITED it. And was the other reference's winning placement set by L's own masters
// (SEEN — an author's choice) or outside its closure (BLIND — it could not have been in the editor)?
// The class is Introduced + Blind. Geometry RANKS within it (depth × fraction, scale class); it does
// not decide. Rule: DetectorModel.OverlapProvenance, proofprobe-owned; arm construction:
// _shared/OverlapIndex.cs, over PlacementHistory — the same history driftprobe and depprobe run on.
//
// "Responsible" = the plugin that last CHANGED a placement (position, cell, or enable/delete flag) —
// PlacementHistory's changed-only rule. An override that forwards a position unchanged takes no
// responsibility; one that changes only rotation or scale is not a step either, and is COUNTED.
//
// Boundary (the standing rules, applied):
//   - Geometry is the measured NIF, resolved through the VFS; OBND only as a labelled fallback;
//     no geometry = not judged, counted (rule 6). Editor markers are not obstructions and are set aside.
//   - Declared dependency is the transitive closure (DetectorModel.MasterClosure); Seen IS that
//     relation read from the later side, and the run checks the two counts agree.
//   - The broad phase decides with DetectorModel.BoxOverlap (proofprobe-owned); exact contact is
//     no overlap, because flush placement is how modular architecture is authored.
//   - This is a BROAD phase: world AABBs of rotated boxes are conservative, so a "deep" hit is a
//     candidate for eyes or a narrow phase, never a verdict. Render geometry stands in for collision.
//     NOT verified in game.
//
// usage: overlapprobe [--list N] [--depth U] [--frac F] [--maxdim U] [--emit path] [--base emit.json] [--dump path] [--json path]
//                     [--bos on|off] [--enable X.esp]* [--disable X.esp]* [--instance DIR] [--game DIR] [--profile NAME]
//   POPULATION      (Q27, 2026-09-06) a candidate exists when its runtime enable state resolves ENABLED at load — the
//                   XESP parent chain through EnableState (the R1 rule) — not when the winning snap's own
//                   initially-disabled flag happens to be clear. The counterfactual arm the provenance rule rests on
//                   ("was the intersection there before the later plugin?") resolves its OWN chain too: each element's
//                   flag at its authoring snap and its XESP as last set outside that plugin's side. Before Q27 the
//                   winning arm read the flag and the counterfactual read the previous snap's flag, so neither saw a
//                   switch and Introduced-vs-Inherited could turn on the wrong one.
//   --state SPEC    another world state (roadmap 4.11): <switch>[,<switch>…] by editor id or FormKey, stage:<quest>:<n>
//                   (with --switch-map, switchprobe's --json), or baseline — baseline IS the default, named explicitly;
//                   a flip resolves the same chain with those switches inverted (the state ROWS are stateprobe's
//                   business; this arm is for judging one whole state).
//   --flags         the flag-only population, what every run before 2026-09-06 judged. Kept so those numbers can be
//                   reproduced. Not with --state.
//   --bos on|off    apply the Base Object Swapper layer (*_SWAP.ini) so every ref is judged by the base the game loads
//                   (default on; _shared/BosHook.cs); off = the record bases

// Defaults are the configured instance; --instance / --game / --profile point every path at another
// MO2 layout. Nothing is compiled in — see _shared/HarnessTarget.cs. Flags win over the environment
// and over harness.local.json; Require() runs AFTER them.
using var __run = RunLog.BeginProcess("overlapprobe", args);

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
string? stateSpec = null, switchMapPath = null;
// --state: the runtime enable state per reference under the arm (null = the winning flag, the default population).
Func<FormKey, bool>? enabledOf = null;
StateArm? stateArm = null;
float minDepth = 16f;      // a hit must need at least this much separation on some axis
float minFrac = 0.25f;     // ...and bury at least this share of the smaller box
// An "object" whose box spans more than this on ANY axis is a VOLUME — mountain fog, a LOD slab, a
// skydome, a city-wide clutter shell. Volumes overlap everything by design and are not obstructions;
// the first calibration run was dominated by them (worst hits at 11,000+u). A wall piece is 128–512u;
// the biggest genuine architecture runs ~1–2k. Parameter, not constant.
float maxDim = 1024f;
string? emitPath = null;
// --base: the emit this run will be diffed against — every record of it is re-measured under THIS order and carried in
// the emit as Predicates (the changed arm of a differential; conflictdiff reads both). See ConflictReport.cs.
string? basePath = null;
// --dump: one line per candidate (key, responsible plugin, base type, mesh, geometry source, rotation, scale,
// world box). A diagnostic for comparing two runs ref by ref — which boxes moved under a transform change,
// and what meshes they were — not part of the report. --json: every judged row, for spot-checks.
string? dumpPath = null;
string? jsonPath = null;
bool bosOn = true;
bool flagsOnly = false;
var enablePlugins = new List<string>();
var disablePlugins = new List<string>();
for (int i = 0; i < args.Length; i++)
{
    if (args[i] == "--list" && i + 1 < args.Length) listN = int.Parse(args[++i]);
    else if (args[i] == "--depth" && i + 1 < args.Length) minDepth = float.Parse(args[++i]);
    else if (args[i] == "--frac" && i + 1 < args.Length) minFrac = float.Parse(args[++i]);
    else if (args[i] == "--maxdim" && i + 1 < args.Length) maxDim = float.Parse(args[++i]);
    else if (args[i] == "--emit" && i + 1 < args.Length) emitPath = args[++i];
    else if (args[i] == "--base" && i + 1 < args.Length) basePath = args[++i];
    else if (args[i] == "--dump" && i + 1 < args.Length) dumpPath = args[++i];
    else if (args[i] == "--json" && i + 1 < args.Length) jsonPath = args[++i];
    else if (args[i] == "--enable" && i + 1 < args.Length) enablePlugins.Add(args[++i]);
    else if (args[i] == "--disable" && i + 1 < args.Length) disablePlugins.Add(args[++i]);
    else if (args[i] == "--bos" && i + 1 < args.Length) bosOn = BosHook.ParseFlag(args[++i]) ?? throw new ArgumentException("--bos takes on|off");
    else if (args[i] == "--state" && i + 1 < args.Length) stateSpec = args[++i];
    else if (args[i] == "--switch-map" && i + 1 < args.Length) switchMapPath = args[++i];
    else if (args[i] == "--flags") flagsOnly = true;
}
PopulationMode popMode;
try { popMode = PopulationCli.Resolve("overlapprobe", flagsOnly, stateSpec); }
catch (ArgumentException ex) { Console.Error.WriteLine(ex.Message); return 2; }

var sw = Stopwatch.StartNew();
IReadOnlyList<string> paths;
if (enablePlugins.Count > 0 || disablePlugins.Count > 0)
{
    var syn = SyntheticOrder.Build(profileDir, modsDir, dataDir, overwriteDir, enablePlugins, disablePlugins);
    foreach (var w in syn.Warnings) Console.WriteLine($"   ! {w}");
    foreach (var a in syn.Applied) Console.WriteLine($"   synthetic: {a}");
    paths = syn.OrderedPaths;
}
else paths = Mo2LoadOrder.Build(profileDir, modsDir, dataDir, overwriteDir).OrderedPaths;
var comp = Mo2LoadOrder.ReadComposition(profileDir);
var arch = ArchiveDiscovery.Discover(profileDir, modsDir, dataDir, overwriteDir, GamePath);
using var assets = AssetResolver.Build(overwriteDir, modsDir, dataDir, comp.EnabledMods, arch.Archives);
Console.WriteLine($"load order: {paths.Count} plugins\n");

var Vanilla = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
{ "Skyrim.esm", "Update.esm", "Dawnguard.esm", "HearthFires.esm", "Dragonborn.esm", "SkyrimVR.esm", "_ResourcePack.esl" };
const string Ussep = "Unofficial Skyrim Special Edition Patch.esp";
var Obstructing = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
{ "Static", "MoveableStatic", "Tree", "Furniture", "Door", "Container", "Activator", "Flora" };

// ---------------------------------------------------------------------------------------
// Pass 1 — every plugin once, into the shared index: placements into the history, base geometry keys.
// ---------------------------------------------------------------------------------------
// --bos on (default): the Base Object Swapper reader replaces each reference's record base with the base the
// game loads, and a moving override becomes a placement step under a pseudo-plugin after the last real one.
// --bos off: the record bases, as before 2026-09-04. The summary says how much the pass's input moved.
var idx = new OverlapIndex { Bos = bosOn ? new BosFormIndex() : null };
var pluginKey = new ModKey[paths.Count];
var masterIdx = new HashSet<int>[paths.Count + (bosOn ? 1 : 0)];
var lightness = new List<(ModKey, bool)>();
var unreadablePlugins = new List<string>();
__run.Feature("plugins", paths.Count, "plugins").Stage("scan", "reading plugins", paths.Count, "plugins");

for (int p = 0; p < paths.Count; p++)
{
    __run.Advance();
    ISkyrimModGetter mod;
    try { mod = SkyrimMod.CreateFromBinaryOverlay(paths[p], SkyrimRelease.SkyrimSE); }
    catch { unreadablePlugins.Add(Path.GetFileName(paths[p])); pluginKey[p] = ModKey.Null; masterIdx[p] = new HashSet<int>(); lightness.Add((ModKey.Null, false)); continue; }
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
int bosPlugin = bosOn ? paths.Count : -1;
if (bosOn) masterIdx[bosPlugin] = BosHook.PseudoMasters(paths.Count);
var closure = DetectorModel.MasterClosure(masterIdx);
Console.WriteLine($"\nindexed {idx.History.Count:N0} winning refs ({idx.Placements:N0} placements)  ({sw.Elapsed.TotalSeconds:0.0}s)");
Console.WriteLine($"   plugins that could not be read (contribute nothing, counted per rule 6): {unreadablePlugins.Count:N0}"
                + (unreadablePlugins.Count > 0 ? "  — " + string.Join(", ", unreadablePlugins) : ""));
Console.WriteLine($"   overrides that changed only rotation or scale (not a step in the history; the plugin is not responsible): {idx.SilentRePlacements:N0}");
if (bosOn)
{
    idx.Bos!.SetLoadOrder(lightness);
    var bosLayer = BosHook.Load(assets.LooseRoots, idx.Bos);
    var bosRep = BosHook.Apply(idx, bosLayer, bosPlugin);
    Console.WriteLine($"   BOS: {bosLayer.Inis.Count} *_SWAP.ini read ({bosLayer.LinesRead:N0} lines, {bosLayer.LinesFailed:N0} the game would refuse, {bosLayer.Anomalies.Count:N0} anomalies — bosprobe lists them)");
    foreach (var l in bosRep.Summary()) Console.WriteLine("   " + l);
}
else Console.WriteLine("   BOS: off — record bases (--bos on to apply the *_SWAP.ini layer)");

string PluginName(int p) => p == bosPlugin ? BosApplyReport.PseudoPluginName : pluginKey[p].FileName.String;
bool IsVanilla(int p) => Vanilla.Contains(PluginName(p));
bool IsUssep(int p) => string.Equals(PluginName(p), Ussep, StringComparison.OrdinalIgnoreCase);
bool IsBaseGame(int p) => PluginName(p) is "Skyrim.esm" or "Update.esm";
string CellOf(FormKey c) => idx.CellName.TryGetValue(c, out var v) ? (v.Edid ?? c.ToString()) + (v.Interior ? " (int)" : " (ext)") : c.ToString();
string TypeOf(FormKey baseKey) => idx.BaseInfo.TryGetValue(baseKey, out var bi) ? bi.Type : "?";
string MeshOf(FormKey baseKey) => idx.BaseMesh.TryGetValue(baseKey, out var m) ? m : "";
static string Short(string s, int n) => s.Length <= n ? s : "..." + s[^(n - 3)..];

// ---------------------------------------------------------------------------------------
// Pass 2 — measured geometry per base record (mesh through the VFS; OBND labelled fallback).
// ---------------------------------------------------------------------------------------
var boxCache = new Dictionary<FormKey, (Aabb Box, char Source)>();   // 'm' mesh, 'o' OBND, '-' none
int measured = 0, fellBack = 0, noGeometry = 0;
var meshBoxByPath = new Dictionary<string, Aabb?>(StringComparer.OrdinalIgnoreCase);

(Aabb Box, char Source) BoxOf(FormKey baseKey)
{
    if (boxCache.TryGetValue(baseKey, out var hit)) return hit;
    (Aabb, char) result = (Aabb.Empty, '-');
    if (idx.BaseMesh.TryGetValue(baseKey, out var rel))
    {
        var full = rel.StartsWith("meshes", StringComparison.OrdinalIgnoreCase) ? rel : Path.Combine("meshes", rel);
        if (!meshBoxByPath.TryGetValue(full, out var mb))
        {
            mb = null;
            var res = assets.ResolveForPlacement(full);
            if (res.Sources.Count > 0)
            {
                var (bytes, _) = AssetResolver.ReadPlacementSource(res.Sources[0]);
                if (bytes is not null && NifGeometry.Measure(bytes) is { Model: { Bounds.IsEmpty: false } mdl })
                    mb = mdl.Bounds;
            }
            meshBoxByPath[full] = mb;
        }
        if (mb is { } box) { result = (box, 'm'); measured++; }
    }
    if (result.Item2 == '-')
    {
        var ob = idx.ObndBox(baseKey);
        if (!ob.IsEmpty) { result = (ob, 'o'); fellBack++; }
    }
    if (result.Item2 == '-') noGeometry++;
    boxCache[baseKey] = result;
    return result;
}

// THE PHYSICAL PREDICATE of an OVERLAP row (2026-09-03; handoff 2026-09-12): the two refs' winning world boxes still meet
// at the run's threshold under the arm being read — the class's own broad-phase test with the provenance half
// (Introduced + Blind) taken off. A sham that masters both plugins and nudges the later ref 1 u becomes responsible for
// it, reads Inherited + Seen and leaves the class; the boxes have not moved. Not used: the narrow-phase contact
// (contactprobe --overlap-json, 4.1's ranker refinement — 56 % of class hits read "no contact" on it, so it would call
// base rows Gone for reasons unrelated to the change; that reading belongs in the ranker). A ref deleted or initially
// disabled under the arm is Gone (one side of the intersection is gone); one without geometry is Unmeasured.
(PhysicalState State, string Detail) OverlapPredicate(FormKey laterKey, FormKey otherKey)
{
    var sl = idx.History.Winning(laterKey); var so = idx.History.Winning(otherKey);
    if (sl is null || so is null) return (PhysicalState.Unmeasured, "a ref is not placed in this order");
    if (sl.Value.Deleted || idx.IsDeletedNow(laterKey) || so.Value.Deleted || idx.IsDeletedNow(otherKey)) return (PhysicalState.Gone, "a ref is deleted under this order");
    if (enabledOf is null ? (sl.Value.Disabled || so.Value.Disabled) : (!enabledOf(laterKey) || !enabledOf(otherKey))) return (PhysicalState.Gone, enabledOf is null ? "a ref is initially disabled under this order" : "a ref is disabled at runtime under this state");
    if (sl.Value.Space != so.Value.Space) return (PhysicalState.Gone, "the refs are no longer in the same space");
    if (!idx.RefBase.TryGetValue(laterKey, out var bl) || !idx.RefBase.TryGetValue(otherKey, out var bo)) return (PhysicalState.Unmeasured, "a ref has no base record");
    var (boxL, srcL) = BoxOf(bl); var (boxO, srcO) = BoxOf(bo);
    if (srcL == '-' || srcO == '-' || boxL.IsEmpty || boxO.IsEmpty) return (PhysicalState.Unmeasured, "a ref has no geometry");
    var wl = SpatialOverlap.ToWorld(boxL, sl.Value.Pos, sl.Value.Rot, sl.Value.Scale);
    var wo = SpatialOverlap.ToWorld(boxO, so.Value.Pos, so.Value.Rot, so.Value.Scale);
    var ov = OverlapIndex.Overlap(wl, wo);
    if (ov is { } o && o.MinDepth >= minDepth && o.VolumeFraction >= minFrac)
        return (PhysicalState.Holds, $"the winning boxes overlap {o.MinDepth:0}u deep, {o.VolumeFraction:0.00} of the smaller [{srcL}{srcO}]");
    return (PhysicalState.Gone, ov is { } o2 ? $"the winning boxes overlap only {o2.MinDepth:0}u / {o2.VolumeFraction:0.00}, under the threshold" : "the winning boxes no longer intersect");
}

// ---------------------------------------------------------------------------------------
// Pass 3 — candidates, world boxes, spatial hash, pairs.
// ---------------------------------------------------------------------------------------
// A candidate is a winning, enabled, obstructing-typed ref whose responsible plugin is not the
// base game — vanilla-vs-mod pairs are excluded by the closure anyway (every mod masters
// Skyrim.esm), so hashing vanilla-responsible refs would only manufacture same-source pairs
// nobody asked about. DLC/CC-responsible refs stay in: DLC-on-DLC is the measured Bethesda floor.
// THE POPULATION (Q27). The default is the load-time state — the same facts the census would give, read through the
// shared arms so the default run and an explicit `--state baseline` are the same run by construction. --state needs the
// census as well, because a switch has to be looked up by name and its sets known.
var arms = new EnableArms(idx);
EnablePopulation pop;
if (stateSpec is not null)
{
    var sx = SwitchIndex.Build(idx);
    sx.NameSwitches(idx.RefEditorId);
    try { stateArm = StateArm.Parse(stateSpec, sx, switchMapPath is null ? null : SwitchMap.Read(switchMapPath)); }
    catch (ArgumentException ex) { Console.Error.WriteLine(ex.Message); return 2; }
    arms.State = stateArm.IsBaseline ? null : stateArm;
    pop = EnablePopulation.Of(arms.WinnerStateFacts, stateArm.Name);
    var changes = stateArm.Changes(sx).ToList();
    Console.WriteLine($"\nstate {stateArm.Name}: {sx.Switches.Count:N0} switches in the order; {stateArm.Flips.Count} flipped — {changes.Count(c => c.After):N0} refs arrive, {changes.Count(c => !c.After):N0} depart");
}
else pop = popMode == PopulationMode.Flags ? EnablePopulation.Flags() : EnablePopulation.Of(arms.WinnerRawFacts, EnablePopulation.BaselineStateName);
// Null in flags mode: Candidates then reads the winning snap's own flag, exactly as it always did.
enabledOf = pop.Predicate;
var candidates = idx.Candidates(BoxOf, Obstructing, IsBaseGame, maxDim, out var skips, enabledOf);
Console.WriteLine("\n" + pop.Describe());

if (dumpPath is not null)
{
    Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(dumpPath))!);
    using var dw = new StreamWriter(dumpPath);
    dw.WriteLine("key\tresp\ttype\tmesh\tsrc\trotX\trotY\trotZ\tscale\tminX\tminY\tminZ\tmaxX\tmaxY\tmaxZ");
    foreach (var c in candidates)
        dw.WriteLine($"{c.Key}\t{PluginName(c.Win.Plugin)}\t{TypeOf(c.Base)}\t{MeshOf(c.Base)}\t{c.Src}"
                   + $"\t{c.Win.Rot.X:R}\t{c.Win.Rot.Y:R}\t{c.Win.Rot.Z:R}\t{c.Win.Scale:R}"
                   + $"\t{c.World.Min.X:R}\t{c.World.Min.Y:R}\t{c.World.Min.Z:R}\t{c.World.Max.X:R}\t{c.World.Max.Y:R}\t{c.World.Max.Z:R}");
    Console.WriteLine($"\ndumped {candidates.Count:N0} candidates -> {dumpPath}");
}

Console.WriteLine($"\ncandidates: {candidates.Count:N0} refs whose responsible plugin is not the base game");
Console.WriteLine($"   geometry: {measured:N0} base records measured from the mesh, {fellBack:N0} from OBND (labelled fallback), {noGeometry:N0} with neither");
Console.WriteLine($"   skipped (counted, per rule 6): {skips.Deleted:N0} deleted, {skips.Disabled:N0} not present at load, "
                + $"{skips.NonObstructingType:N0} non-obstructing type, {skips.NoGeometry:N0} no geometry, {skips.ExcludedResponsible:N0} base-game-responsible,");
Console.WriteLine($"                                  {skips.Volume:N0} volume-scale (a box axis > {maxDim:0}u — fog, LOD, skydomes; not obstructions), "
                + $"{skips.MarkerMesh:N0} editor markers (no rendered mesh, cannot obstruct)");

var sweep = OverlapIndex.Pairs(candidates, closure, minDepth, minFrac);

// A published, ACTIVE patch mastering both sides is the community saying "these two are known to
// meet" — the pair is a LABELLED conflict, and its residual overlaps are what the patch did not
// reconcile. A pair with hits and NO reconciler anywhere in the order is the real shortlist.
var patchMemo = new Dictionary<(int, int), bool>();
bool Reconciled(int a, int b)
{
    var k = a <= b ? (a, b) : (b, a);
    if (patchMemo.TryGetValue(k, out var hit)) return hit;
    bool found = false;
    for (int p = 0; p < masterIdx.Length && !found; p++)
        if (p != a && p != b && masterIdx[p].Contains(a) && masterIdx[p].Contains(b)) found = true;
    return patchMemo[k] = found;
}

Console.WriteLine($"\npairs checked: {sweep.PairsChecked:N0}   (threshold: depth >= {minDepth:0}u AND fraction >= {minFrac:0.00})");
Console.WriteLine($"\n==== THE FLOOR — same author, deliberate: how often one plugin's own objects interpenetrate ====");
Console.WriteLine($"   same-responsible pairs   : {sweep.SameChecked,10:N0} checked, {sweep.SameDeep,7:N0} deep  ({Rate(sweep.SameDeep, sweep.SameChecked),6:0.00}%)");
foreach (var (dom, v) in sweep.SameByDomain.OrderBy(k => k.Key))
    Console.WriteLine($"      {dom,-8}              : {v.Checked,10:N0} checked, {v.Deep,7:N0} deep  ({Rate(v.Deep, v.Checked),6:0.00}%)");
Console.WriteLine($"   declared-dependency pairs: {sweep.CrossDeclaredChecked,10:N0} checked, {sweep.CrossDeclaredDeep,7:N0} deep  ({Rate(sweep.CrossDeclaredDeep, sweep.CrossDeclaredChecked),6:0.00}%)   <- coordination; reads SEEN below");

var undeclared = sweep.CrossDeep.Where(h => !h.Declared).ToList();
Console.WriteLine($"\n==== CROSS-PLUGIN, NO DECLARED DEPENDENCY — the milestone 5 question, absolute ====");
Console.WriteLine($"   pairs checked: {sweep.CrossUndeclaredChecked:N0}, deep overlaps: {undeclared.Count:N0}  ({Rate(undeclared.Count, sweep.CrossUndeclaredChecked),6:0.00}%)");
OverlapPopulation PopOf(OverlapHit h) => DetectorModel.ClassifyOverlapPopulation(
    IsVanilla(h.A.Win.Plugin), IsVanilla(h.B.Win.Plugin), IsUssep(h.A.Win.Plugin), IsUssep(h.B.Win.Plugin));
Console.WriteLine($"   {undeclared.Count(h => PopOf(h) == OverlapPopulation.BethesdaFloor),6:N0}  A. Bethesda-on-Bethesda (floor)");
Console.WriteLine($"   {undeclared.Count(h => PopOf(h) != OverlapPopulation.BethesdaFloor),6:N0}  C. cross-plugin  (of which USSEP-involved {undeclared.Count(h => PopOf(h) == OverlapPopulation.UssepInvolved):N0})");

// ---------------------------------------------------------------------------------------
// Pass 4 — the differential: every cross-responsible deep hit judged for provenance.
// ---------------------------------------------------------------------------------------
// The counterfactual arm resolves its own enable chain (Q27) — in flags mode it stays on the previous snap's own flag,
// which is what every run before then did.
var rows = sweep.CrossDeep.Select(h => idx.Judge(h, closure, BoxOf, minDepth, minFrac, popMode == PopulationMode.Flags ? null : arms)).ToList();
int seenRows = rows.Count(r => r.Sight == OverlapSight.Seen);
Console.WriteLine($"\n==== PROVENANCE — with vs without the later plugin, and who saw what ({rows.Count:N0} cross-responsible deep hits) ====");
Console.WriteLine(seenRows == sweep.CrossDeclaredDeep
    ? $"   plumbing: SEEN rows = {seenRows:N0} = declared-dependency deep hits  OK — Seen is the closure relation read from the later side"
    : $"   plumbing: SEEN rows = {seenRows:N0} but declared-dependency deep hits = {sweep.CrossDeclaredDeep:N0}  *** MISMATCH — the two tests should agree ***");

string Verdict(OverlapRow r) => $"{r.Origin} + {r.Sight}";
var pops = new[] { OverlapPopulation.BethesdaFloor, OverlapPopulation.UssepInvolved, OverlapPopulation.ModOnMod };
Console.WriteLine($"\n   {"verdict",-22} {"Bethesda floor",16} {"USSEP involved",16} {"mod-on-mod",16} {"all",10}   (hits / distinct refs)");
foreach (var (o, s) in new[] { (OverlapOrigin.Introduced, OverlapSight.Blind), (OverlapOrigin.Introduced, OverlapSight.Seen),
                                (OverlapOrigin.Inherited, OverlapSight.Blind), (OverlapOrigin.Inherited, OverlapSight.Seen) })
{
    var vr = rows.Where(r => r.Origin == o && r.Sight == s).ToList();
    string Cell(IEnumerable<OverlapRow> g) { var l = g.ToList(); return $"{l.Count:N0} / {l.SelectMany(r => new[] { r.Hit.A.Key, r.Hit.B.Key }).Distinct().Count():N0}"; }
    Console.WriteLine($"   {o + " + " + s,-22} {Cell(vr.Where(r => PopOf(r.Hit) == pops[0])),16} {Cell(vr.Where(r => PopOf(r.Hit) == pops[1])),16} {Cell(vr.Where(r => PopOf(r.Hit) == pops[2])),16} {Cell(vr),10}");
}
var introduced = rows.Where(r => r.Origin == OverlapOrigin.Introduced).ToList();
Console.WriteLine($"\n   Introduced {introduced.Count:N0} of {rows.Count:N0} ({Rate(introduced.Count, rows.Count):0.0}%); Blind share of Introduced {Rate(introduced.Count(r => r.Sight == OverlapSight.Blind), introduced.Count):0.0}%");
Console.WriteLine($"   how the later plugin introduced it: {introduced.Count(r => !r.Facts.LaterHadPreviousPlacement):N0} new refs, "
                + $"{introduced.Count(r => r.Facts.LaterHadPreviousPlacement && r.Facts.PreviousPlacementPresent):N0} moved in, "
                + $"{introduced.Count(r => r.Facts.LaterHadPreviousPlacement && !r.Facts.PreviousPlacementPresent):N0} enabled or relocated in");

// THE CLASS: Introduced + Blind. Seen is the declared relation, so this is all class-C by construction.
var cls = rows.Where(r => r.Origin == OverlapOrigin.Introduced && r.Sight == OverlapSight.Blind && PopOf(r.Hit) != OverlapPopulation.BethesdaFloor).ToList();
var floorRows = rows.Where(r => PopOf(r.Hit) == OverlapPopulation.BethesdaFloor).ToList();

(string, string) PairKey(OverlapRow r)
{
    var (x, y) = (PluginName(r.Later), PluginName(r.Earlier));
    return string.CompareOrdinal(x, y) <= 0 ? (x, y) : (y, x);
}
int DistinctRefs(IEnumerable<OverlapRow> g) => g.SelectMany(r => new[] { r.Hit.A.Key, r.Hit.B.Key }).Distinct().Count();
string Describe(OverlapRow r) =>
    $"{TypeOf(r.LaterRef.Base)} from {PluginName(r.Later)} buried {r.Hit.Frac:0.00} deep ({r.Hit.Depth:0}u) in {TypeOf(r.OtherRef.Base)} from {PluginName(r.Earlier)}"
  + $" in {CellOf(r.LaterRef.Win.Cell)}  [{r.LaterRef.Src}{r.OtherRef.Src}]  {r.LaterRef.Key} / {r.OtherRef.Key}";

Console.WriteLine($"\n==== THE CLASS — INTRODUCED + BLIND, mod-on-mod and USSEP-involved: {cls.Count:N0} hits over {DistinctRefs(cls):N0} distinct refs ====");
Console.WriteLine($"   by scale of the buried box: " + string.Join(", ",
    new[] { OverlapScale.Architecture, OverlapScale.Furniture, OverlapScale.Clutter }.Select(sc => $"{sc} {cls.Count(r => r.Scale == sc):N0}")));
Console.WriteLine($"\n   ---- by responsible-plugin pair (Introduced + Blind) ----");
var byPair = cls.GroupBy(PairKey).OrderByDescending(g => g.Count()).ToList();
foreach (var g in byPair.Take(listN))
{
    var worst = g.OrderByDescending(r => r.Rank).First();
    bool rec = Reconciled(worst.Later, worst.Earlier);
    Console.WriteLine($"   {g.Count(),5:N0} hits over {DistinctRefs(g),5:N0} distinct refs  "
                    + $"{(rec ? "[patched, residual]" : "[NO RECONCILER]    "),-19} {g.Key.Item1}  x  {g.Key.Item2}");
    Console.WriteLine($"          worst: {Describe(worst)}");
}
Console.WriteLine($"\n   ---- the shortlist: Introduced + Blind pairs with NO plugin in this order reconciling them ----");
foreach (var g in byPair.Where(g => { var r0 = g.First(); return !Reconciled(r0.Later, r0.Earlier); }).Take(listN))
    Console.WriteLine($"   {g.Count(),5:N0} hits over {DistinctRefs(g),5:N0} distinct refs   {g.Key.Item1}  x  {g.Key.Item2}");
Console.WriteLine($"   (a pair WITH a reconciler is a labelled conflict whose residue the patch did not cover —");
Console.WriteLine($"    second-priority, and the count says how much residue)");

Console.WriteLine($"\n   ---- the ranker: top {listN} Introduced + Blind by depth × fraction (geometry ranks, it does not decide) ----");
Console.WriteLine($"   {"rank",7} {"scale",-12} {"depth",6} {"frac",5}  {"later ref (buried or burying) / other ref"}");
foreach (var r in cls.OrderByDescending(r => r.Rank).ThenByDescending(r => r.SmallerMaxDim).Take(listN))
    Console.WriteLine($"   {r.Rank,7:0} {r.Scale,-12} {r.Hit.Depth,6:0} {r.Hit.Frac,5:0.00}  {TypeOf(r.LaterRef.Base)} {Short(MeshOf(r.LaterRef.Base), 40)} ({PluginName(r.Later)})"
                    + $"  x  {TypeOf(r.OtherRef.Base)} {Short(MeshOf(r.OtherRef.Base), 40)} ({PluginName(r.Earlier)})  in {CellOf(r.LaterRef.Win.Cell)}  {r.LaterRef.Key} / {r.OtherRef.Key}");
Console.WriteLine($"\n   ---- the ranker, architecture-scale only (a wall through a house is a different finding from two bottles) ----");
foreach (var r in cls.Where(r => r.Scale == OverlapScale.Architecture).OrderByDescending(r => r.Rank).Take(listN))
    Console.WriteLine($"   {r.Rank,7:0} {r.Hit.Depth,6:0} {r.Hit.Frac,5:0.00}  {Describe(r)}");

var knowing = rows.Where(r => r.Origin == OverlapOrigin.Introduced && r.Sight == OverlapSight.Seen).ToList();
Console.WriteLine($"\n==== KNOWINGLY OVERLAPPING — Introduced + Seen: the author placed into context its own masters set ({knowing.Count:N0} hits) ====");
foreach (var g in knowing.GroupBy(PairKey).OrderByDescending(g => g.Count()).Take(Math.Min(listN, 12)))
    Console.WriteLine($"   {g.Count(),5:N0} hits over {DistinctRefs(g),5:N0} distinct refs   {g.Key.Item1}  x  {g.Key.Item2}");

var inherited = rows.Where(r => r.Origin == OverlapOrigin.Inherited).ToList();
Console.WriteLine($"\n==== INHERITED — the intersection pre-dates the later plugin; attributed to nobody here ({inherited.Count:N0} hits; Blind {inherited.Count(r => r.Sight == OverlapSight.Blind):N0}, Seen {inherited.Count(r => r.Sight == OverlapSight.Seen):N0}) ====");
foreach (var g in inherited.GroupBy(PairKey).OrderByDescending(g => g.Count()).Take(Math.Min(listN, 12)))
    Console.WriteLine($"   {g.Count(),5:N0} hits over {DistinctRefs(g),5:N0} distinct refs   {g.Key.Item1}  x  {g.Key.Item2}");

Console.WriteLine($"\n==== THE BETHESDA FLOOR — every Bethesda-on-Bethesda cross-responsible deep hit, with its provenance (control candidates) ====");
foreach (var r in floorRows.OrderByDescending(r => r.Rank))
    Console.WriteLine($"   {Verdict(r),-22} {r.Scale,-12} {Describe(r)}");

// Sensitivity, always-on: the class count at half and double both knobs.
Console.WriteLine($"\n==== SENSITIVITY (Introduced + Blind count at other thresholds) ====");
foreach (var (d, f) in new[] { (minDepth / 2, minFrac / 2), (minDepth, minFrac), (minDepth * 2, MathF.Min(1f, minFrac * 2)) })
{
    int n = cls.Count(r => r.Hit.Depth >= d && r.Hit.Frac >= f);
    Console.WriteLine(d < minDepth
        ? $"   depth>={d,3:0}u frac>={f:0.00}  ->  (not computable from this run's hit set — re-run with --depth {d:0} --frac {f:0.00})"
        : $"   depth>={d,3:0}u frac>={f:0.00}  ->  {n:N0}");
}

// --emit: the CLASS only — Introduced + Blind, Bethesda floor withheld (the way navownprobe withholds
// its floor). The LATER plugin's ref is the necessary fix target (it placed into existing context);
// moving the other side is the legitimate alternative. Every record carries its physical predicate under this
// arm (OverlapPredicate, Holds by construction for a class row); with --base, every record of the named emit is
// re-measured under this arm too (2026-09-03).
if (emitPath is not null)
{
    var conflicts = new List<Conflict>();
    foreach (var r in cls)
    {
        var (st, why) = OverlapPredicate(r.LaterRef.Key, r.OtherRef.Key);
        conflicts.Add(new Conflict(
            "OVERLAP", PluginName(r.Earlier), PluginName(r.Later), Describe(r),
            new List<ConflictRecord> { new(r.LaterRef.Key.ToString(), "placed-ref", CellOf(r.LaterRef.Win.Cell), st, why) },
            new List<ConflictRecord> { new(r.OtherRef.Key.ToString(), "placed-ref", null) }));
    }
    List<Predicate>? predicates = null;
    if (basePath is not null)
    {
        var baseFile = ConflictIo.Read(basePath);
        predicates = new List<Predicate>();
        foreach (var c in baseFile.Conflicts)
        {
            string? other = c.Alternative is { Count: > 0 } alt ? alt[0].FormKey : null;
            foreach (var rec in c.Necessary)
            {
                if (c.Kind != "OVERLAP" || other is null) { predicates.Add(new Predicate(c.Kind, c.A, c.B, rec.FormKey, other, PhysicalState.Unmeasured, "not an overlap row with two refs")); continue; }
                var (st, why) = OverlapPredicate(FormKey.Factory(rec.FormKey), FormKey.Factory(other));
                predicates.Add(new Predicate(c.Kind, c.A, c.B, rec.FormKey, other, st, why));
            }
        }
        Console.WriteLine($"\n   --base {Path.GetFileName(basePath)}: {predicates.Count:N0} base-row predicate(s) re-measured under this order — "
                        + string.Join(", ", predicates.GroupBy(p => p.Physical).OrderBy(g => g.Key).Select(g => $"{g.Key} {g.Count():N0}")));
    }
    int notRebuilt = rows.Count(r => r.Facts.PreviousPlacementPresent && float.IsNaN(r.WithoutDepth));
    var coverage = new List<CoverageLine>
    {
        new("plugins", paths.Count - unreadablePlugins.Count, new Dictionary<string, long> { ["UnreadablePlugin"] = unreadablePlugins.Count }),
        new("base records (geometry)", measured + fellBack, new Dictionary<string, long> { ["NoGeometry"] = noGeometry }, null, $"OBND fallback (labelled) {fellBack:N0} of the judged"),
        // The unresolved enable chains join NoGeometry as unjudgeable: a candidate whose chain could not be read to the
        // end is in the population on EnableState's stated fallback, not on a measurement, and conflictdiff's coverage
        // test must be able to see that share rise (Q27).
        new("candidate refs", candidates.Count,
            new Dictionary<string, long>(pop.Unjudgeable) { ["NoGeometry"] = skips.NoGeometry },
            new Dictionary<string, long> { ["Deleted"] = skips.Deleted, ["NotPresentAtLoad"] = skips.Disabled, ["NonObstructingType"] = skips.NonObstructingType,
                                           ["BaseGameResponsible"] = skips.ExcludedResponsible, ["VolumeScale"] = skips.Volume, ["EditorMarker"] = skips.MarkerMesh },
            $"population {pop.Identity} / state {pop.StateName}"),
        new("cross-responsible deep hits", rows.Count, new Dictionary<string, long>(), null,
            $"class {cls.Count:N0}; {notRebuilt:N0} rows whose previous-snap box could not be rebuilt read Introduced by default (a stated limit)"),
    };
    ConflictIo.Write(emitPath, new ConflictFile("overlapprobe", Instance,
        DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm:ssZ"), conflicts, coverage, predicates, basePath,
        pop.Identity, pop.StateName));
    Console.WriteLine($"\nemitted {conflicts.Count:N0} Introduced + Blind conflicts -> {emitPath}");
}

if (jsonPath is not null)
{
    var dir = Path.GetDirectoryName(Path.GetFullPath(jsonPath));
    if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
    var dump = rows.Select(r => new
    {
        Later = r.LaterRef.Key.ToString(), LaterPlugin = PluginName(r.Later), LaterType = TypeOf(r.LaterRef.Base), LaterMesh = MeshOf(r.LaterRef.Base), LaterSrc = r.LaterRef.Src.ToString(),
        Other = r.OtherRef.Key.ToString(), OtherPlugin = PluginName(r.Earlier), OtherType = TypeOf(r.OtherRef.Base), OtherMesh = MeshOf(r.OtherRef.Base), OtherSrc = r.OtherRef.Src.ToString(),
        Cell = CellOf(r.LaterRef.Win.Cell), Origin = r.Origin.ToString(), Sight = r.Sight.ToString(), Population = PopOf(r.Hit).ToString(),
        Declared = r.Hit.Declared, Depth = r.Hit.Depth, Frac = r.Hit.Frac, Rank = r.Rank, Scale = r.Scale.ToString(), SmallerMaxDim = r.SmallerMaxDim,
        WithoutDepth = r.WithoutDepth, WithoutFrac = r.WithoutFrac,
        LaterPrevious = r.LaterPrevious is { } lp ? new { Plugin = PluginName(lp.Plugin), lp.Pos.X, lp.Pos.Y, lp.Pos.Z, Cell = lp.Cell.ToString(), lp.Disabled, lp.Deleted } : null,
        OtherAsSeenByLater = r.OtherAsSeenByLater is { } os ? new { Plugin = PluginName(os.Plugin), os.Pos.X, os.Pos.Y, os.Pos.Z } : null,
        Reconciled = Reconciled(r.Later, r.Earlier), r.Why,
    });
    File.WriteAllText(jsonPath, JsonSerializer.Serialize(dump, new JsonSerializerOptions { WriteIndented = false, NumberHandling = System.Text.Json.Serialization.JsonNumberHandling.AllowNamedFloatingPointLiterals }));
    Console.WriteLine($"wrote {rows.Count:N0} judged rows -> {jsonPath}");
}

Console.WriteLine($"\nNOT verified in game; a broad-phase AABB hit is a candidate for eyes or a narrow phase, never a verdict.");
Console.WriteLine($"Limits: render geometry stands in for collision; rotation- and scale-only re-placements are not steps in the history ({idx.SilentRePlacements:N0} counted above);");
Console.WriteLine($"        the arm without the later plugin steps back ONE placement — an intersection created two movers back reads Inherited and is attributed to nobody.");
Console.WriteLine($"elapsed {sw.Elapsed.TotalSeconds:0.0}s");
return 0;

static float Rate(long hit, long total) => total == 0 ? 0 : 100f * hit / total;
