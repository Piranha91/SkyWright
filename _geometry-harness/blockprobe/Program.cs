// HousecarlCore now has its own PluginUnreadableException (upstream 2.0); this probe means the harness one.
using PluginUnreadableException = HarnessShared.PluginUnreadableException;
using HarnessShared;
using System.Diagnostics;
using System.Numerics;
using HousecarlCore;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Skyrim;
using HousecarlCore.Progress;

// Milestone 4, second half: the KNOWN-POSITIVE test.
//
// navprobe established that the detector is QUIET — a 0.4% false-positive floor over 495K
// vanilla references. That is only half a claim. This asks whether it FIRES on a real
// problem: a plugin that drops objects into a cell without re-cutting the navmesh.
//
// The discriminator, and the whole point of the run:
//   cells where the target ALSO supplied a navmesh  -> the author reconciled it, expect QUIET
//   cells where it did NOT                          -> expect blockage to concentrate here
// If those two rates come out the same, the detector is not measuring what it claims to.
//
// usage: blockprobe <TargetPlugin.esp> [<Target2.esp> ...] [--exclude Other.esp ...] [--list N] [--geometry collision|render] [--bos on|off]
//                   [--state SPEC] [--switch-map FILE] [--flags] [--order FILE]
//   targets   one or more plugins, judged as ONE set (P51): the union of their cells and references; each listed row
//              names the target(s) that place it in the cell it is judged in ({by ...}), and the route-cut verdicts
//              are tallied per target. An unknown argument is refused (exit 2), never skipped.
//   --order    evaluate a candidate configuration (a CandidateFile: the loaded plugins in order, and optionally the mod
//              folders) instead of the profile's own, through _shared/HarnessOrderCli.cs as stateprobe and navownprobe do.
//              Without it the run reads the profile exactly as before.
//   POPULATION (Q27, 2026-09-06). By DEFAULT every physical read of this run — the target's obstacles, the floor under
//              the band, the actor band itself, and the navcut boxes standing in the target's cells — judges the
//              LOAD-TIME state: a reference is there when EnableState resolves its XESP parent chain enabled, not when
//              its own initially-disabled flag happens to be clear (that flag is CK bookkeeping the engine ignores
//              wherever a reference has a parent). Q25: on the flag alone a room variant switched off at load stood as
//              the floor 48 u over a whole room's navmesh and moved verdicts.
//   --state    another world state (roadmap 4.11): <switch>[,<switch>…], stage:<quest>:<n> (with --switch-map) or
//              baseline. `--state baseline` is the default population, named explicitly; a switch flip resolves the
//              chain with those switches inverted. Every population above moves together.
//   --flags    the flag-only population: each record's own initially-disabled flag, no chain — what every run before
//              2026-09-06 judged, kept so those numbers can be reproduced. Not with --state.
//   NAVCUTS    a placed reference whose XTRI collision layer is a COLL record flagged NavmeshObstacle cuts the navmesh
//              wherever it stands. Since Q27 the boxes standing in the target's cells are measured in EVERY run, on the
//              run's own population (before then, only under --state), tallied apart and listed with {NAVCUT}.
//              These rows are UNCONFIRMED CANDIDATES and the output says so. The only in-game reading of a navcut is
//              Q20's Part D, where a SET's boxes were switched on and made a room unreachable; a box that is already on
//              at load has never been watched, and the rule still measures each box alone where Part D showed a set
//              cutting more together than any one of them does. Whether such a row is a problem is a question for a
//              review of the findings, not for the measurement.
//   --geometry as navprobe's: collision (default since 2026-09-02) runs the rules on the decoded Havok solid restricted
//              to actor-solid layers, skipping objects with no such body and falling back to render (labelled) when a
//              decode is incomplete; render reproduces every run before that date.
//   --bos      on (default, since 2026-09-04): every reference is judged by the base the game LOADS for it — the Base
//              Object Swapper layer (*_SWAP.ini, read from the profile's VFS; _shared/BosHook.cs) — at the transform its
//              overrides give it, and a reference an override disables at init is not an obstacle. A random line yields a
//              set: the first alternative is taken and counted. off: the record bases and placements, as every run before.
//              No rule changes either way; the hook changes the input, and the summary says how much it moved.

using var __run = RunLog.BeginProcess("blockprobe", args);
// Known answers for which version of a moved or deleted reference this probe's walk keeps (_shared/MovedRefFixture.cs).
if (args.Length >= 1 && args[0] == "--moved-ref-selftest") return BlockSelfTest.Run();
// Known answers for the per-reference decisions over a synthetic two-room order (_shared/BlockFixture.cs).
if (args.Length >= 1 && args[0] == "--self-test") return BlockRouteSelfTest.Run();
// The route-cut fixture: written for tests/test_route_cut_rows.py, which runs the real probe on it.
if (args.Length >= 2 && args[0] == "--write-route-cut-fixture") { var w = RouteCutFixture.Write(args[1]); Console.WriteLine(string.Join(Environment.NewLine, w.Order.Append(w.ModsDir))); return 0; }

string Instance = HarnessTarget.Require(HarnessTarget.Instance, "MO2 instance directory", "instance");
string GamePath = HarnessTarget.Require(HarnessTarget.Game, "game directory", "game");
string profileName = HarnessTarget.Require(HarnessTarget.Profile, "MO2 profile name", "profile");
string profileDir = Path.Combine(Instance, "profiles", profileName);
string modsDir = Path.Combine(Instance, "mods");
string overwriteDir = Path.Combine(Instance, "overwrite");
string dataDir = Path.Combine(GamePath, "Data");

if (args.Length == 0) { Console.Error.WriteLine("usage: blockprobe <TargetPlugin.esp> [<Target2.esp> ...] [--exclude Other.esp ...] [--list N]"); return 1; }
// One or more target plugins, positionally: their union is judged as one set, and every listed row names which target
// placed or overrode it (P51). Any argument this loop does not know is refused, never skipped.
var targets = new List<string>();
var exclude = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
int listN = 25;
string geometrySource = "collision";
bool controllerBand = true;   // --band controller|nominal: the route rules' band top (the surface rules keep the nominal, as every run before)
bool bosOn = true;
string? stateSpec = null, switchMapPath = null, orderFile = null;
bool flagsOnly = false;
for (int i = 0; i < args.Length; i++)
{
    if (!args[i].StartsWith("--", StringComparison.Ordinal))
    {
        if (!targets.Contains(args[i], StringComparer.OrdinalIgnoreCase)) targets.Add(args[i]);
        continue;
    }
    if (args[i] == "--order" && i + 1 < args.Length) { orderFile = args[++i]; continue; }
    if (args[i] == "--state" && i + 1 < args.Length) { stateSpec = args[++i]; continue; }
    if (args[i] == "--switch-map" && i + 1 < args.Length) { switchMapPath = args[++i]; continue; }
    if (args[i] == "--flags") { flagsOnly = true; continue; }
    if (args[i] == "--exclude" && i + 1 < args.Length) exclude.Add(args[++i]);
    else if (args[i] == "--list" && i + 1 < args.Length) listN = int.Parse(args[++i]);
    else if (args[i] == "--geometry" && i + 1 < args.Length) geometrySource = args[++i];
    else if (args[i] == "--band" && i + 1 < args.Length) controllerBand = args[++i] != "nominal";
    else if (args[i] == "--bos" && i + 1 < args.Length) bosOn = BosHook.ParseFlag(args[++i]) ?? throw new ArgumentException("--bos takes on|off");
    else
    {
        Console.Error.WriteLine(args[i] is "--instance" or "--profile" or "--game"
            ? $"blockprobe does not take {args[i]}: it reads the instance, profile and game from HOUSECARL_MO2_INSTANCE, HOUSECARL_MO2_PROFILE and HOUSECARL_GAME_PATH (or the harness config); set those instead."
            : $"blockprobe does not know the argument '{args[i]}' (or it is missing its value); check the usage line and run again.");
        return 2;
    }
}
if (targets.Count == 0) { Console.Error.WriteLine("blockprobe needs at least one target plugin, named positionally."); return 2; }
if (geometrySource is not ("collision" or "render")) { Console.Error.WriteLine("--geometry must be collision or render"); return 1; }
PopulationMode popMode;
try { popMode = PopulationCli.Resolve("blockprobe", flagsOnly, stateSpec); }
catch (ArgumentException ex) { Console.Error.WriteLine(ex.Message); return 2; }

var sw = Stopwatch.StartNew();
// --order: a candidate configuration through the shared resolver (its plugins, its asset view); otherwise the profile.
ResolvedConfiguration? cfg = null;
if (orderFile is not null)
{
    cfg = HarnessOrderCli.Resolve(profileName, profileDir, modsDir, dataDir, overwriteDir, GamePath, orderFile, null, null);
    if (cfg.Refused) { Console.Error.WriteLine(cfg.RefusedReason); return 2; }
    HarnessOrderCli.Print(cfg);
}
IReadOnlyList<string> orderedPaths;
AssetResolver assetsBuilt;
if (cfg is null)
{
    var comp = Mo2LoadOrder.ReadComposition(profileDir);
    var arch = ArchiveDiscovery.Discover(profileDir, modsDir, dataDir, overwriteDir, GamePath);
    assetsBuilt = AssetResolver.Build(overwriteDir, modsDir, dataDir, comp.EnabledMods, arch.Archives);
    orderedPaths = Mo2LoadOrder.Build(profileDir, modsDir, dataDir, overwriteDir).OrderedPaths;
    if (HarnessShared.OrderMasters.RefusalForProfile(profileDir, modsDir, dataDir, overwriteDir, orderedPaths) is { } missingMaster) { Console.Error.WriteLine(missingMaster); return 2; }   // ruling 12: the game will not load it
}
else
{
    assetsBuilt = AssetResolver.Build(overwriteDir, modsDir, dataDir, cfg.EnabledMods, cfg.Archives.Archives);
    orderedPaths = cfg.OrderedPaths;
}
using var assets = assetsBuilt;

var paths = orderedPaths.Where(p => !exclude.Contains(Path.GetFileName(p))).ToList();
var targetPaths = new List<string>();
foreach (var t in targets)
{
    string? tp = paths.FirstOrDefault(p => string.Equals(Path.GetFileName(p), t, StringComparison.OrdinalIgnoreCase));
    if (tp is null) { Console.Error.WriteLine($"'{t}' is not in the {(cfg is null ? "active load order" : "candidate configuration")}."); return 1; }
    targetPaths.Add(tp);
}
string target = string.Join(" + ", targets);

Console.WriteLine($"load order: {paths.Count} plugins" + (exclude.Count > 0 ? $"  (EXCLUDING {string.Join(", ", exclude)})" : ""));
for (int t = 0; t < targets.Count; t++)
    Console.WriteLine($"target    : {targets[t]}   position {paths.FindIndex(p => p == targetPaths[t]) + 1}/{paths.Count}");
Console.WriteLine(targets.Count > 1 ? $"   {targets.Count} targets judged as one set; each listed row names the target(s) that place it {{by …}}\n" : "");

// ---------- 1. which cells do the targets touch? ----------
// The union over the targets: a cell two targets touch carries both targets' references (each once) and navmeshes.
// refBy records which target plugin(s) carry each reference IN EACH CELL; a reference is judged only in its winning
// cell, so a listed row names the targets that place it there (judgedBy), never one that placed it elsewhere.
var targetCells = new Dictionary<FormKey, CellPlacements>();
var refBy = new Dictionary<(FormKey Cell, FormKey Ref), SortedSet<string>>();
var judgedBy = new Dictionary<FormKey, SortedSet<string>>();
for (int t = 0; t < targets.Count; t++)
    using (var tm = SkyrimMod.CreateFromBinaryOverlay(targetPaths[t], SkyrimRelease.SkyrimSE))
        foreach (var cp in PlacementExtract.FromMod(tm))
        {
            foreach (var r in cp.Refs)
            {
                if (!refBy.TryGetValue((cp.Cell, r.FormKey), out var by)) refBy[(cp.Cell, r.FormKey)] = by = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
                by.Add(targets[t]);
            }
            if (!targetCells.TryGetValue(cp.Cell, out var have)) { targetCells[cp.Cell] = cp; continue; }
            var seen = have.Refs.Select(r => r.FormKey).ToHashSet();
            targetCells[cp.Cell] = have with
            {
                Refs = have.Refs.Concat(cp.Refs.Where(r => !seen.Contains(r.FormKey))).ToList(),
                Navmeshes = have.Navmeshes.Concat(cp.Navmeshes).ToList(),
            };
        }
string By(FormKey k) => judgedBy.TryGetValue(k, out var s) ? "{by " + string.Join("+", s) + "}" : "{by ?}";

var targetModKeys = targets.Select(t => ModKey.FromFileName(t)).ToHashSet();
int targetNewRefs = targetCells.Values.Sum(c => c.Refs.Count(r => targetModKeys.Contains(r.FormKey.ModKey)));
int targetOverrides = targetCells.Values.Sum(c => c.Refs.Count(r => !targetModKeys.Contains(r.FormKey.ModKey)));
Console.WriteLine($"target touches {targetCells.Count:N0} cells: "
                + $"{targetNewRefs:N0} refs it INTRODUCES, {targetOverrides:N0} it overrides, "
                + $"{targetCells.Values.Sum(c => c.Navmeshes.Count):N0} navmeshes it supplies");

// The floor under the target's exterior cells reaches across their borders (Q24, 2026-09-05 — _shared/FloorFeed.cs): the
// 3×3 neighbourhood's refs and LAND, by worldspace and grid, are filed during the winning-state walk below. Nothing else
// about the neighbours is loaded; the diff-based charter stands.
// The walk's accumulation lives in BlockWalk.cs (so --moved-ref-selftest runs the same code); these are its maps.
var walk = new BlockWalk(targetCells);
var floorRefsByWorld = walk.FloorRefsByWorld;
var floorLandByWorld = walk.FloorLandByWorld;
var floorTally = new FloorFeed.Tally();

// ---------- 2. resolve the WINNING state for exactly those cells ----------
// Diff-based, per the charter: only the cells the target touches are ever loaded (plus the floor neighbourhood above).
sw.Restart();
var winRef = walk.WinRef;                 // ref FormKey -> winning placement
var winRefCell = walk.WinRefCell;         // ref FormKey -> winning cell
var winNav = walk.WinNav;
var baseMesh = new Dictionary<FormKey, (string Mesh, string Type)>();
var cellMeta = walk.CellMeta;
var npcFacts = new Dictionary<FormKey, ActorBand.NpcFacts>();
var raceFacts = new Dictionary<FormKey, ActorBand.RaceFacts>();
// The Base Object Swapper layer's index fills during this SAME walk (one pass): every plugin's slot in the runtime-id
// table, every record the filters and ids read, every cell and reference (_shared/BosHook.cs, BosFeed). Off, every call
// is a no-op.
var bos = new BosFeed(bosOn);
// The run's POPULATION (Q27): the enable facts of EVERY placement in the order, gathered in this one walk — a parent
// chain crosses cells (a marker in a worldspace's persistent cell governs refs in the grid cells), so the facts have to
// come from the whole order even though only the target's cells are ever judged. Deletions are read off the record walk
// as well as the placements: a deleted reference usually strips its placement subrecord, and a deleted parent that read
// as a live one would silently keep its children off.
var enableFacts = new EnableFactsIndex();
// The COLL table by INDEX, and which layers are flagged NavmeshObstacle — what a navcut box is read by (XTRI carries the
// layer's Index, not a FormID).
var collByIndex = new Dictionary<uint, (string? Edid, bool NavmeshObstacle)>();
var definedBy = walk.DefinedBy;     // ref -> the first plugin that placed it, for the {NAVCUT by …} line
__run.Feature("plugins", paths.Count, "plugins").Stage("scan", "reading plugins", paths.Count, "plugins");


foreach (var p in paths)
{
    __run.Advance();
    ISkyrimModGetter m;
    try { m = BlockWalk.Open(p)!; }
    catch (PluginUnreadableException ex) { Console.Error.WriteLine(ex.Message); return 2; }
    try
    {
        using (m as IDisposable)
        {
            bos.Plugin(m);
            string name = Path.GetFileName(p);
            foreach (var rec in m.EnumerateMajorRecords())
            {
                bos.Record(rec);
                if (rec is IPlacedGetter pg) { if (pg.IsDeleted) enableFacts.NoteDeleted(pg.FormKey); continue; }
                if (rec is ICollisionLayerGetter coll)
                { collByIndex[coll.Index] = (coll.EditorID, coll.Flags.HasFlag(CollisionLayer.Flag.NavmeshObstacle)); continue; }
                if (rec is IModeledGetter mo && mo.Model?.File is { } f && !string.IsNullOrWhiteSpace(f.GivenPath))
                    baseMesh[rec.FormKey] = (f.GivenPath, rec.GetType().Name.Replace("BinaryOverlay", ""));
                ActorBand.CollectRecord(rec, npcFacts, raceFacts);
            }

            walk.Plugin(m, name, cp =>
            {
                bos.Cell(cp);
                foreach (var r in cp.Refs) enableFacts.Note(r);
            });
        }
    }
    // A plugin opens lazily: one whose groups fail while this walk reads them is refused, named (_shared/PluginOpen.cs).
    catch (Exception ex) when (ex is not PluginUnreadableException)
    { Console.Error.WriteLine(new PluginUnreadableException(p, "blockprobe's walk of the load order", ex, midWalk: true).Message); return 2; }
    catch (PluginUnreadableException ex) { Console.Error.WriteLine(ex.Message); return 2; }
}
Console.WriteLine($"winning state for those cells: {winRef.Count:N0} refs, {winNav.Count:N0} navmeshes, "
                + $"{baseMesh.Count:N0} base records  ({sw.Elapsed.TotalSeconds:0.0}s)");
walk.Dump();
if (walk.TakenOutCount > 0)
    Console.WriteLine($"   {walk.TakenOutCount:N0} reference(s) a target cell held until a later plugin moved them out of those cells or "
                    + "deleted them: not judged (the last sighting decides)");
// --bos on (default): the layer over the inis the game would read from this profile's VFS, resolved per reference below.
// --bos off: the record bases and placements, as every run before 2026-09-04.
if (bosOn) bos.Load(assets.LooseRoots);
Console.WriteLine("   " + bos.LayerLine() + "\n");
// Q33 (measured in game 2026-09-06): a flags(0x800)/flagsC(0x800) override replaces the reference's OWN
// initially-disabled flag; the enable chain then decides. Feeding it into the facts BEFORE the population is built is
// what gets the engine's precedence from one rule — under an XESP parent the override is bookkeeping the chain
// ignores, with no parent it decides in both directions. Filtering on it after the chain (what this probe did before)
// dropped a reference the parent keeps, and could never let a clear bring one back.
var bosOwnFlags = bos.OwnFlagOverrides(winRef.Select(kv => (kv.Value, winRefCell[kv.Key])));
int bosReflagged = enableFacts.ApplyBosOwnFlags(bosOwnFlags);
if (bosOwnFlags.Count > 0)
    Console.WriteLine($"   BOS own-flag overrides: {bosOwnFlags.Count:N0} reference(s) re-flagged, "
                    + $"{bosReflagged:N0} applied to the enable facts (the rest name a reference this walk does not place)\n");
var bosRep = new BosApplyReport();
var cutTallyBos = new Dictionary<RouteCutVerdict, int>();   // the route-cut verdicts of the refs BOS swapped or moved, reported apart

// ---------- 2b. THE POPULATION (Q27) ----------
// The default is the load-time state over the facts the walk above gathered — no second ingest, no OverlapIndex.
// --state needs the switch CENSUS (an editor id or a quest stage has to be looked up, and a flip has to know a switch's
// sets), and that does need the shared index, so it keeps its own ingest.
StateArm? stateArm = null;
EnablePopulation pop;
if (stateSpec is not null)
{
    sw.Restart();
    var stateIdx = new OverlapIndex();
    for (int p = 0; p < paths.Count; p++)
    {
        ISkyrimModGetter m;
        try { m = BlockWalk.Open(paths[p])!; }
        catch (PluginUnreadableException ex) { Console.Error.WriteLine(ex.Message); return 2; }
        try
        {
            using (m as IDisposable) stateIdx.Ingest(p, m);
        }
        // A plugin opens lazily: one whose groups fail while this walk reads them is refused, named (_shared/PluginOpen.cs).
        catch (Exception ex) when (ex is not PluginUnreadableException)
        { Console.Error.WriteLine(new PluginUnreadableException(paths[p], "blockprobe's --state index walk", ex, midWalk: true).Message); return 2; }
        catch (PluginUnreadableException ex) { Console.Error.WriteLine(ex.Message); return 2; }
    }
    var sx = SwitchIndex.Build(stateIdx);
    sx.NameSwitches(stateIdx.RefEditorId);
    try { stateArm = StateArm.Parse(stateSpec, sx, switchMapPath is null ? null : SwitchMap.Read(switchMapPath)); }
    catch (ArgumentException ex) { Console.Error.WriteLine(ex.Message); return 2; }
    // The census's facts are not the walk's, so the Q33 own-flag overlay has to be applied to them here too —
    // otherwise a --state run would judge BOS's re-flags by the pre-Q33 rule while the default run judged them by
    // the measured one. It goes UNDER the arm rather than over it, because BOS's write is a load-time OWN-FLAG fact
    // and the arm flips FROM the load-time state: laid over the wrap instead, it would overwrite a flipped switch's
    // own flag with the load-time one and silently undo that flip, and the flip's baseline would still be read off
    // the un-overlaid census.
    var censusFacts = EnableFactsIndex.WithBosOwnFlags(sx.WinnerFacts, bosOwnFlags);
    var censusBosMemo = new Dictionary<FormKey, EnableResolution>();
    EnableResolution CensusBaseline(FormKey k) => EnableState.Resolve(k, censusFacts, censusBosMemo);
    var armFacts = stateArm.Wrap(censusFacts, CensusBaseline);
    pop = EnablePopulation.Of(armFacts, stateArm.Name);
    // The sentence below and the population above must describe ONE world: Changes over the same overlaid facts, or a
    // switch whose own flag BOS clears reads as arriving here while the run's own population has it departing.
    var changes = stateArm.Changes(sx, censusFacts, CensusBaseline).ToList();
    Console.WriteLine($"state {stateArm.Name}: {sx.Switches.Count:N0} switches in the order; {stateArm.Flips.Count} flipped — "
                    + $"{changes.Count(c => c.After):N0} refs arrive, {changes.Count(c => !c.After):N0} depart  ({sw.Elapsed.TotalSeconds:0.0}s)");
}
else pop = popMode == PopulationMode.Flags ? EnablePopulation.Flags(enableFacts) : EnablePopulation.LoadTime(enableFacts);
// NULL in flags mode on purpose: every consumer then runs the initially-disabled branch it always ran, so the legacy
// numbers come out of the legacy code rather than a re-implementation of it.
Func<FormKey, bool>? enabledOf = pop.Predicate;

// The NAVCUT boxes standing in the target's cells — any plugin's — measured on the run's own population since Q27
// (before then, only under --state). A navcut is a placed reference whose XTRI names a COLL record flagged
// NavmeshObstacle; its obstacle is its XPRM box at its transform.
// Which boxes can cut a target cell's navmesh is NavcutReach.ForCell's (NavcutReach.cs).
var navcutsByCell = new Dictionary<FormKey, List<FormKey>>();
var navcutRef = new Dictionary<FormKey, PlacedRef>();
bool IsNavcut(PlacedRef r) => r.CollisionLayerIndex is { } cl && collByIndex.TryGetValue(cl, out var layer) && layer.NavmeshObstacle;
var heldByCell = winRef.Values.GroupBy(r => winRefCell[r.FormKey]).ToDictionary(g => g.Key, g => g.ToList());
// Cells in the order their first held navcut appears in the walk, then the rest in target order: the order the pass and its
// listing have always used.
var walkOrder = winRef.Keys.Select((k, i) => (k, i)).ToDictionary(x => x.k, x => x.i);
var pickedByCell = new List<(FormKey Cell, List<PlacedRef> Refs, int First)>();
foreach (var (cellKey, tc) in targetCells)
{
    var grids = tc.Interior || tc.Worldspace.IsNull ? null : walk.FloorRefsByWorld.GetValueOrDefault(tc.Worldspace);
    var picked = NavcutReach.ForCell(tc, heldByCell.TryGetValue(cellKey, out var hl) ? hl : new List<PlacedRef>(), grids, IsNavcut);
    if (picked.Count == 0) continue;
    int first = picked.Where(r => winRefCell.TryGetValue(r.FormKey, out var c) && c == cellKey).Select(r => walkOrder[r.FormKey]).DefaultIfEmpty(int.MaxValue).Min();
    pickedByCell.Add((cellKey, picked, first));
}
foreach (var (cellKey, picked, _) in pickedByCell.OrderBy(x => x.First))
{
    navcutsByCell[cellKey] = picked.Select(r => r.FormKey).ToList();
    foreach (var r in picked) navcutRef[r.FormKey] = r;
}
bool NavcutPresent(FormKey k) => enabledOf is null ? !navcutRef[k].InitiallyDisabled : enabledOf(k);
Console.WriteLine($"   {navcutsByCell.Values.Sum(l => l.Count):N0} navcut boxes stand in the target's cells "
                + $"({navcutsByCell.Values.Sum(l => l.Count(NavcutPresent)):N0} present in this population; listed with {{NAVCUT}}"
                + ")");

var navcutTally = new Dictionary<RouteCutVerdict, int>();
var navcutHits = new List<(RouteCutVerdict V, string Cell, FormKey Fk, string Why, int Touched, RouteLine? Line, string Plugin)>();
long navcutMeasured = 0, navcutSkippedDisabled = 0, navcutNoCross = 0;

// ---------- 3. geometry cache ----------
var geoCache = new Dictionary<string, Geo>(StringComparer.OrdinalIgnoreCase);
int measured = 0;
Geo Geometry(string rel)
{
    if (geoCache.TryGetValue(rel, out var hit)) return hit;
    var full = rel.StartsWith("meshes", StringComparison.OrdinalIgnoreCase) ? rel : Path.Combine("meshes", rel);
    var res = assets.ResolveForPlacement(full);
    if (res.Sources.Count == 0) return geoCache[rel] = new Geo(Aabb.Empty, false, false, null, "unresolved");
    var (bytes, _) = AssetResolver.ReadPlacementSource(res.Sources[0]);
    if (bytes is null) return geoCache[rel] = new Geo(Aabb.Empty, false, false, null, "unresolved");
    var o = NifGeometry.Measure(bytes, includeTriangles: true);
    if (o.Model is null) return geoCache[rel] = new Geo(Aabb.Empty, false, false, null, "unreadable");
    measured++;
    var m = o.Model;
    var render = m.Soup is { TriangleCount: > 0 and <= 200_000 } s ? s : null;
    if (geometrySource == "render") return geoCache[rel] = new Geo(m.Bounds, m.HasCollision, true, render, "render");

    // Collision: the solid an actor meets (see navprobe for the three outcomes; same layer set, same labels).
    bool anySolid = m.CollisionBodies is not null && m.CollisionBodies.Any(b => b.Solid && CollisionLayers.IsActorSolid(b.Layer));
    if (!m.HasCollision || !anySolid) return geoCache[rel] = new Geo(Aabb.Empty, m.HasCollision, true, null, "none");
    var solid = m.CollisionSoupFor(b => b.Solid && CollisionLayers.IsActorSolid(b.Layer));
    if (m.CollisionComplete && solid is { TriangleCount: > 0 })
    {
        var box = Aabb.Empty;
        foreach (var v in solid.Vertices) box = box.Add(v);
        return geoCache[rel] = new Geo(box, true, true, solid, "collision");
    }
    return geoCache[rel] = new Geo(m.Bounds, m.HasCollision, true, render, "fallback");
}

// ---------- 4. test the target's OWN references against the winning navmesh ----------
// Settings match navprobe's calibrated NARROW column test exactly, so the 0.4% vanilla floor
// is the directly comparable baseline.
// The actor band's TOP is derived per cell (ActorBand: the tallest placed actor, else the load order's tallest
// playable race) and printed; its bottom is the step height, still an assumption.
float ActorHeight = ActorBand.ScaleOneHeight;
const float Clearance = ActorBand.StepHeight;
const float TriInset = 12f;       // navprobe's edge-graze forgiveness
ActorBand.ResolveControllers(raceFacts, assets);
var (fallbackTop, fallbackSrc) = ActorBand.TallestPlayable(raceFacts);
Console.WriteLine(FloorFeed.BandLine(controllerBand) + $"; top fallback {fallbackSrc}; the surface rules keep the navmesh-relative band {Clearance:0}..top");
Console.WriteLine($"   population: {(pop.Mode == PopulationMode.Flags ? "FLAGS (the record's own initially-disabled flag; the pre-Q27 default)" : $"LOAD-TIME state '{pop.StateName}' (the XESP enable chain) — obstacles, floors, the band and the navcuts all judged on it")}");
var winRefsByCell = winRef.Values.GroupBy(r => winRefCell[r.FormKey]).ToDictionary(g => g.Key, g => g.ToList());

// A floor candidate's solid, by the record's own base (the swap layer is not applied to floors — FloorFeed's header).
(Aabb Box, TriangleSoup Soup)? FloorGeometry(PlacedRef r)
{
    if (r.Base is not { } b || !baseMesh.TryGetValue(b, out var bm)) return null;
    var g = Geometry(bm.Mesh);
    return g.Ok && g.Soup is not null && !g.Box.IsEmpty ? (g.Box, g.Soup) : null;
}
// The floor the band stands on in a cell (Q24): the winning refs of an interior; for an exterior the 3×3 neighbourhood
// filed above, else its own refs. Every field built is kept for the summary tally. The floors go through the SAME
// population as the obstacles and the band — since Q27 that is the load-time enable chain in an ordinary run, not only
// under --state (Q25: a room variant switched off at load had been the floor over a whole room).
// One field per cell, kept: the navcut pass asks for the same cells the reference pass did, and building a second field
// for them would double every raster read in the summary tally and print the cell twice in the per-cell table.
var floorFields = new List<(string Cell, FloorField Field)>();
var floorByCell = new Dictionary<FormKey, FloorField>();
// A cell whose floor neighbourhood holds a LAND that gives no heights (FloorFeed.FloorIndex) is not judged: its floor
// would come from an earlier plugin's heights or from none. Counted and named, never guessed at.
long floorLandUnjudged = 0;
string? FloorLandProblem(FormKey cellKey)
{
    if (!cellMeta.TryGetValue(cellKey, out var m) || m.Interior || m.Grid is not { } g) return null;
    var bad = walk.Floor.UnreadableNear(m.Worldspace, g);
    return bad.Count == 0 ? null : string.Join("; ", bad);
}
FloorField FloorFor(FormKey cellKey, List<PlacedRef>? own)
{
    if (floorByCell.TryGetValue(cellKey, out var cached)) return cached;
    string cellName = cellMeta.TryGetValue(cellKey, out var cm) ? (cm.CellEditorId ?? cellKey.ToString()) : cellKey.ToString();
    FloorField built;
    if (cellMeta.TryGetValue(cellKey, out var meta) && !meta.Interior && meta.Grid is { } g && floorRefsByWorld.TryGetValue(meta.Worldspace, out var frw))
    {
        var (nbRefs, nbLand) = walk.Floor.NeighbourhoodOf(meta.Worldspace, g);   // the 3x3 by position plus the reach (FloorFeed)
        built = FloorFeed.Build(nbRefs, FloorGeometry, nbLand, enabled: enabledOf);
    }
    else built = FloorFeed.Build(own ?? new List<PlacedRef>(), FloorGeometry, null, enabled: enabledOf);
    floorFields.Add((cellName, built));
    return floorByCell[cellKey] = built;
}
int cellsWithActors = 0, cellsTemplatedOnly = 0; float bandMin = float.MaxValue, bandMax = 0f;

// The route rule (Part B): every reference whose solid crosses an actor column is measured in a window of
// walkable ground around it, with vs without, on a clearance ladder; the verdict at two provisional thresholds
// (Part C decides the real one) is tallied and the Blocked / Narrowed refs are listed.
var Ladder = new[] { 0f, 32f, 64f, 96f, 128f };
const float RouteMargin = 256f, RouteStep = 16f;
var routeTally64 = new Dictionary<RouteVerdict, int>();
var routeTally128 = new Dictionary<RouteVerdict, int>();
var routeHits = new List<(RouteVerdict V64, RouteVerdict V128, string Cell, string Mesh, FormKey Fk, string Why, int Touched, int Intact, int IntactWithout)>();
long routeMeasured = 0, routeDoors = 0, routeMovable = 0;
void Tally(Dictionary<RouteVerdict, int> d, RouteVerdict v) { d.TryGetValue(v, out var n); d[v] = n + 1; }

// The route-CUT rule (4.7's closing step): the same raster, asked whether the object cuts the corridor's path line at
// capsule width, and how wide the passage beside it is. Tallied beside the old rule, and cross-tabbed against it, so
// the re-pricing is like for like in one pass. Band top: the derived top × the controller factor (or the nominal).
var cutTally = new Dictionary<RouteCutVerdict, int>();
var crossTab = new Dictionary<(RouteVerdict Old, RouteCutVerdict New), int>();
var noLineWhy = new Dictionary<LineStatus, int>();
var cutHits = new List<(RouteCutVerdict V, RouteVerdict Old, string Cell, string Mesh, FormKey Fk, string Why, int Touched, RouteLine? Line)>();
void TallyCut(RouteCutVerdict v) { cutTally.TryGetValue(v, out var n); cutTally[v] = n + 1; }
// The same verdicts per target plugin (P51): a reference two targets both carry counts under each.
var cutByTarget = new Dictionary<(string Target, RouteCutVerdict V), int>();

var navByCell = winNav.Values.GroupBy(v => v.Cell)
                             .ToDictionary(g => g.Key, g => g.ToList());

long tested = 0, blocked = 0, noGeo = 0, skipped = 0, noSolid = 0, onCollision = 0, onFallback = 0;
long refsWithBroadHits = 0, broadHitTotal = 0, partialTotal = 0, refsBroadOnly = 0, blockedAny = 0;
// The surface-crossing rules, priced against the same vanilla floor navprobe measures.
long blockedCross = 0, blockedSever = 0, blockedSeverTight = 0, unjudged = 0;
var severHits = new List<(string Cell, string Mesh, string Type, int Tris, FormKey Fk)>();
var anyHits = new List<(string Cell, string Mesh, string Type, int Tris, FormKey Fk, bool TargetGaveNav, string NavOwner)>();
long testedWithNav = 0, blockedWithNav = 0, testedNoNav = 0, blockedNoNav = 0;
var hits = new List<(string Cell, string Mesh, string Type, int Tris, FormKey Fk, bool TargetGaveNav, string NavOwner)>();

sw.Restart();
foreach (var (cellKey, tcell) in targetCells)
{
    if (!navByCell.TryGetValue(cellKey, out var navs) || navs.Count == 0) continue;

    bool targetGaveNav = tcell.Navmeshes.Count > 0;
    string navOwner = string.Join("+", navs.Select(n => n.Plugin).Distinct().OrderBy(x => x));

    var index = NavmeshIndex.Build(navs.Select(n => n.Nav).ToList(), out _);
    if (index.TriangleCount == 0) continue;

    var band = ActorBand.ForCell(winRefsByCell.TryGetValue(cellKey, out var cellRefs) ? cellRefs : Array.Empty<PlacedRef>(),
                                 npcFacts, raceFacts, fallbackTop, fallbackSrc, enabledOf);
    if (!float.IsFinite(band.Top)) { Console.WriteLine($"UNJUDGEABLE actor band in {cellKey}: {band.Source}"); continue; }
    if (FloorLandProblem(cellKey) is { } landWhy) { Console.WriteLine($"UNJUDGEABLE floor in {cellKey}: LAND gives no heights at {landWhy}"); floorLandUnjudged++; continue; }
    ActorHeight = band.Top;
    float physicalTop = controllerBand ? ActorBand.PhysicalControllerTop(band.Top) : band.Top;
    var floor = FloorFor(cellKey, cellRefs);
    if (band.Actors > 0) cellsWithActors++;
    if (band.Actors > 0 && band.Templated == band.Actors) cellsTemplatedOnly++;
    bandMin = MathF.Min(bandMin, band.Top); bandMax = MathF.Max(bandMax, band.Top);
    Dictionary<NavGraph.TriRef, NavGraph.Node>? gnodes = null;   // the cell's walkable graph, built once when a route needs it

    foreach (var tref in tcell.Refs)
    {
        // Test the WINNING placement — if a later patch moved or removed this reference,
        // the target's own copy is not what the game uses.
        if (RefAdmission.Winning(tref.FormKey, cellKey, winRef, winRefCell) is not { } r) { skipped++; continue; }
        if (refBy.TryGetValue((cellKey, tref.FormKey), out var placedBy)) judgedBy[r.FormKey] = placedBy;
        // The Base Object Swapper layer: the base and transform the game LOADS for this reference (the record's own under
        // --bos off).
        var eff = bos.Effective(r, cellKey, bosRep);
        // The run's population decides presence, and it already carries the layer's own-flag overrides
        // (ApplyBosOwnFlags above) — so an override under an enable parent is ignored by the chain, exactly as the
        // engine ignores it, and a clear with no parent brings the reference in. In --flags mode there is no chain and
        // the BOS-effective own flag IS the population, which is the same single test the old default ran.
        if (enabledOf is null ? eff.InitiallyDisabled : !enabledOf(r.FormKey)) { skipped++; continue; }
        if (r.Base is null || !baseMesh.TryGetValue(eff.Base, out var bm)) { noGeo++; continue; }

        var g = Geometry(bm.Mesh);
        if (g.Ok && g.Source == "none") { noSolid++; continue; }
        if (!g.Ok || g.Box.IsEmpty || g.Soup is null || !g.HasCollision) { noGeo++; continue; }
        if (g.Source == "collision") onCollision++; else onFallback++;

        var world = SpatialOverlap.ToWorld(g.Box, eff.Position, eff.Rotation, eff.Scale);
        var q = SpatialOverlap.GrowDown(world, ActorHeight);

        tested++;
        if (targetGaveNav) testedWithNav++; else testedNoNav++;

        MeshColumnIndex? columns = null; bool built = false;
        int nTris = 0, broadTris = 0, partial = 0, nTrisAny = 0, nCross = 0, nSever = 0, nSeverTight = 0;
        foreach (var t in index.Query(q))
        {
            if ((t.Flags & 0x08) != 0) continue;               // Deleted triangle
            broadTris++;
            if (!built) { columns = BuildColumns(g.Soup, eff.Position, eff.Rotation, eff.Scale); built = true; }
            if (columns is null) continue;

            var nav = index.NavAt(t.NavIndex);
            var tri = nav.Triangles[t.TriangleIndex];
            var pa = nav.Vertices[tri.A]; var pb = nav.Vertices[tri.B]; var pc = nav.Vertices[tri.C];
            var va = new Vector3(pa.X, pa.Y, pa.Z);
            var vb = new Vector3(pb.X, pb.Y, pb.Z);
            var vc = new Vector3(pc.X, pc.Y, pc.Z);

            // --- the OLD rule: vertex containment ---
            int inside = 0;
            foreach (var v in new[] { va, vb, vc })
                if (columns.Classify(v) == ColumnPosition.Inside) inside++;
            if (inside == 3) nTris++;
            else if (inside > 0) partial++;

            // A THIN blocker — a wall, a fence, a gate — can never put all three vertices of a
            // navmesh triangle inside itself: the triangle straddles it, vertices on both sides,
            // only the middle crossing. The all-3 rule is structurally blind to exactly the
            // objects most likely to block a path. Count the any-vertex variant alongside so the
            // two rules can be compared on the same run rather than argued about.
            if (inside >= 1) nTrisAny++;

            // --- the NEW rules, at navprobe's calibrated settings ---
            // CROSS: does the object's surface enter the column an actor standing here occupies?
            int x = columns.CountSurfaceCrossings(va, vb, vc, Clearance, ActorHeight, TriInset, 1f, 0.5f, stopAfter: 1);
            if (x < 0) { unjudged++; continue; }
            if (x == 0) continue;
            nCross++;

            // SEVER: ...and does that CUT the triangle, or merely stand on it?
            var sev = columns.MeasureSeverance(va, vb, vc, Clearance, ActorHeight, TriInset, 32f);
            if (!sev.Judged) { unjudged++; continue; }
            if (sev.Severed) nSever++;

            var sevT = columns.MeasureSeverance(va, vb, vc, Clearance, ActorHeight, TriInset, 48f);
            if (sevT.Judged && sevT.Severed) nSeverTight++;
        }
        // DIAGNOSTIC. Separates "this object is nowhere near navmesh" (broad==0, a legitimate
        // quiet answer) from "the broad phase saw plenty and the narrow phase rejected it all"
        // (broad>0, narrow==0 — which would mean the narrow phase is over-filtering).
        if (broadTris > 0) refsWithBroadHits++;
        broadHitTotal += broadTris;
        partialTotal += partial;
        if (broadTris > 0 && nTris == 0) refsBroadOnly++;

        // ---- the route rule, for every reference whose solid entered an actor column ----
        // A Door base record is passable by design — the navmesh runs through the doorway and actors open it — so
        // it is counted apart, never judged Blocked.
        // A MoveableStatic is pushed aside, not a blocker (Part C's urn) — set aside the same way, counted.
        if (nCross > 0 && columns is not null)
        {
            var aside = RefAdmission.RouteSetAside(bm.Type);
            if (aside == RefAdmission.SetAside.Door) { routeDoors++; }
            else if (aside == RefAdmission.SetAside.Movable) { routeMovable++; }
            else
            {
                routeMeasured++;
                gnodes ??= NavGraph.Build(index).Nodes;
                var facts = RouteConnectivity.Measure(index, columns, world, RouteMargin, RouteStep, ActorBand.PhysicalStep, physicalTop, Ladder, graph: gnodes, floorAt: floor.FloorAt, isStairsMaterial: DetectorModel.IsStairsMaterial);
                var v64 = DetectorModel.RouteBlockage(facts.Judged, facts.Touched, facts.MaxIntactRung, facts.MaxIntactRungWithout, Ladder, 64f);
                var v128 = DetectorModel.RouteBlockage(facts.Judged, facts.Touched, facts.MaxIntactRung, facts.MaxIntactRungWithout, Ladder, 128f);
                Tally(routeTally64, v64.Verdict); Tally(routeTally128, v128.Verdict);
                if (v64.Verdict is RouteVerdict.Blocked or RouteVerdict.Narrowed || v128.Verdict is RouteVerdict.Narrowed || v64.Verdict == RouteVerdict.Unjudgeable)
                    routeHits.Add((v64.Verdict, v128.Verdict, tcell.CellEditorId ?? cellKey.ToString(), bm.Mesh, r.FormKey,
                                   facts.Judged ? v128.Why + $" [storeys {facts.Storeys}, z {facts.StoreyZ:0}]" : facts.Why,
                                   facts.Touched, facts.MaxIntactRung, facts.MaxIntactRungWithout));

                var line = facts.Line;
                var vc = DetectorModel.RouteCut(facts.Judged, facts.Touched, line?.Found ?? false, line?.Why ?? "", line?.Cut ?? false, line?.Passage ?? float.NaN, cutRise: line?.CutRise ?? float.NaN, cutFloat: line?.CutFloat ?? float.NaN, cutOnStairs: line?.CutOnStairs ?? false);
                TallyCut(vc.Verdict);
                if (judgedBy.TryGetValue(r.FormKey, out var byTargets))
                    foreach (var bt in byTargets) { cutByTarget.TryGetValue((bt, vc.Verdict), out var bn); cutByTarget[(bt, vc.Verdict)] = bn + 1; }
                if (eff.Touched) { cutTallyBos.TryGetValue(vc.Verdict, out var bt); cutTallyBos[vc.Verdict] = bt + 1; }
                crossTab.TryGetValue((v64.Verdict, vc.Verdict), out var xt); crossTab[(v64.Verdict, vc.Verdict)] = xt + 1;
                if (vc.Verdict == RouteCutVerdict.NoLine && line is not null) { noLineWhy.TryGetValue(line.Status, out var nl); noLineWhy[line.Status] = nl + 1; }
                if (vc.Verdict is RouteCutVerdict.Blocked or RouteCutVerdict.LikelyBlocked or RouteCutVerdict.NoLine or RouteCutVerdict.Unjudgeable)
                    cutHits.Add((vc.Verdict, v64.Verdict, tcell.CellEditorId ?? cellKey.ToString(), bm.Mesh, r.FormKey,
                                 (facts.Judged ? vc.Why + $" [storeys {facts.Storeys}, z {facts.StoreyZ:0}]" : facts.Why)
                                 + (eff.Resolution is { } bres ? " {" + BosHook.Describe(bres) + "}" : ""), facts.Touched, line));
            }
        }

        if (nTrisAny > 0) blockedAny++;
        if (nCross > 0) blockedCross++;
        if (nSeverTight > 0) blockedSeverTight++;
        if (nSever > 0)
        {
            blockedSever++;
            severHits.Add((tcell.CellEditorId ?? cellKey.ToString(), bm.Mesh, bm.Type, nSever, r.FormKey));
        }
        if (nTris > 0)
        {
            blocked++;
            if (targetGaveNav) blockedWithNav++; else blockedNoNav++;
            hits.Add((tcell.CellEditorId ?? cellKey.ToString(), bm.Mesh, bm.Type, nTris, r.FormKey, targetGaveNav, navOwner));
        }
        else if (nTrisAny > 0 && anyHits.Count < 4000)
            anyHits.Add((tcell.CellEditorId ?? cellKey.ToString(), bm.Mesh, bm.Type, nTrisAny, r.FormKey, targetGaveNav, navOwner));
    }
}

// The navcut boxes standing in the target's cells, present in this run's population, through the route-cut rule — the
// cut a quest stage (or a marker already on at load) brings to the target's navmesh. Measured after the target's own
// refs so the tallies stay apart. Since Q27 this runs in every mode, on that mode's population.
    foreach (var (cellKey, navcuts) in navcutsByCell)
    {
        if (!navByCell.TryGetValue(cellKey, out var navs) || navs.Count == 0) continue;
        var index = NavmeshIndex.Build(navs.Select(n => n.Nav).ToList(), out _);
        if (index.TriangleCount == 0) continue;
        var band = ActorBand.ForCell(winRefsByCell.TryGetValue(cellKey, out var cellRefs2) ? cellRefs2 : Array.Empty<PlacedRef>(), npcFacts, raceFacts, fallbackTop, fallbackSrc, enabledOf);
        if (!float.IsFinite(band.Top)) { Console.WriteLine($"UNJUDGEABLE actor band in {cellKey}: {band.Source}"); continue; }
        if (FloorLandProblem(cellKey) is { } landWhy2) { Console.WriteLine($"UNJUDGEABLE floor in {cellKey}: LAND gives no heights at {landWhy2}"); floorLandUnjudged++; continue; }
        float routeTop = controllerBand ? ActorBand.PhysicalControllerTop(band.Top) : band.Top;
        var floor = FloorFor(cellKey, cellRefs2);
        Dictionary<NavGraph.TriRef, NavGraph.Node>? gnodes = null;
        foreach (var nk in navcuts)
        {
            if (!NavcutPresent(nk)) { navcutSkippedDisabled++; continue; }
            var wr = navcutRef[nk];
            var w = (Pos: wr.Position, Rot: wr.Rotation, Scale: wr.Scale);
            var prim = wr.Primitive!.Value;
            var model = NavcutGeometry.ModelBox(prim);
            if (model.IsEmpty) continue;
            var soup = NavcutGeometry.Soup(prim);
            var world = SpatialOverlap.ToWorld(model, w.Pos, w.Rot, w.Scale);
            var columns = MeshColumnIndex.Build(NavcutGeometry.WorldVertices(soup, w.Pos, w.Rot, w.Scale), soup.Indices, triangleMaterials: soup.TriangleMaterials);
            bool crosses = false;
            foreach (var t in index.Query(SpatialOverlap.GrowDown(world, routeTop)))
            {
                if ((t.Flags & 0x08) != 0) continue;
                var nav = index.NavAt(t.NavIndex); var tri = nav.Triangles[t.TriangleIndex];
                var pa = nav.Vertices[tri.A]; var pb = nav.Vertices[tri.B]; var pc = nav.Vertices[tri.C];
                if (columns.CountSurfaceCrossings(new Vector3(pa.X, pa.Y, pa.Z), new Vector3(pb.X, pb.Y, pb.Z), new Vector3(pc.X, pc.Y, pc.Z), Clearance, routeTop, TriInset, 1f, 0.5f, stopAfter: 1) > 0) { crosses = true; break; }
            }
            if (!crosses) { navcutNoCross++; continue; }
            gnodes ??= NavGraph.Build(index).Nodes;
            var facts = RouteConnectivity.Measure(index, columns, world, RouteMargin, RouteStep, ActorBand.PhysicalStep, routeTop, Ladder, graph: gnodes, floorAt: floor.FloorAt, isStairsMaterial: DetectorModel.IsStairsMaterial);
            var line = facts.Line;
            var vc = DetectorModel.RouteCut(facts.Judged, facts.Touched, line?.Found ?? false, line?.Why ?? "", line?.Cut ?? false, line?.Passage ?? float.NaN, cutRise: line?.CutRise ?? float.NaN, cutFloat: line?.CutFloat ?? float.NaN, cutOnStairs: line?.CutOnStairs ?? false);
            navcutMeasured++;
            navcutTally.TryGetValue(vc.Verdict, out var nt); navcutTally[vc.Verdict] = nt + 1;
            string plugin = definedBy.TryGetValue(nk, out var dp) ? dp : "?";
            navcutHits.Add((vc.Verdict, targetCells[cellKey].CellEditorId ?? cellKey.ToString(), nk, facts.Judged ? vc.Why : facts.Why, facts.Touched, line, plugin));
        }
    }

Console.WriteLine($"==== {target} — navmesh blockage ====");
Console.WriteLine($"population        : {pop.Describe()}");
Console.WriteLine($"references tested : {tested:N0}   (no usable geometry {noGeo:N0}, skipped {skipped:N0}, no actor-solid collision {noSolid:N0})");
Console.WriteLine($"geometry source   : {geometrySource}" + (geometrySource == "collision" ? $"   on collision {onCollision:N0}, render fallback {onFallback:N0}" : ""));
Console.WriteLine($"distinct meshes   : {measured:N0}");
Console.WriteLine($"elapsed           : {sw.Elapsed.TotalSeconds:0.0}s");
if (bosOn) foreach (var l in bosRep.Summary()) Console.WriteLine("   " + l);
Console.WriteLine();

double Rate(long b, long t) => t == 0 ? 0 : 100.0 * b / t;
Console.WriteLine($"   {"population",-46} {"tested",9} {"blocked",9} {"rate",8}");
Console.WriteLine($"   {"ALL target references",-46} {tested,9:N0} {blocked,9:N0} {Rate(blocked, tested),7:0.00}%");
Console.WriteLine($"   {"  cells the target ALSO navmeshed",-46} {testedWithNav,9:N0} {blockedWithNav,9:N0} {Rate(blockedWithNav, testedWithNav),7:0.00}%");
Console.WriteLine($"   {"  cells it did NOT navmesh",-46} {testedNoNav,9:N0} {blockedNoNav,9:N0} {Rate(blockedNoNav, testedNoNav),7:0.00}%");
Console.WriteLine($"\n   vanilla false-positive floor for comparison: 0.4%");

Console.WriteLine($"\n   ---- broad vs narrow: is the quiet REAL, or over-filtering? ----");
Console.WriteLine($"   refs whose box touched ANY navmesh triangle : {refsWithBroadHits,7:N0} of {tested:N0}  ({Rate(refsWithBroadHits, tested):0.0}%)");
Console.WriteLine($"   navmesh triangles seen by the broad phase   : {broadHitTotal,7:N0}");
Console.WriteLine($"   triangles PARTIALLY inside (1-2 of 3 verts) : {partialTotal,7:N0}");
Console.WriteLine($"   refs the narrow phase rejected entirely     : {refsBroadOnly,7:N0}");
Console.WriteLine(refsWithBroadHits == 0
    ? "   => objects are NOWHERE NEAR navmesh. Quiet is legitimate; this case CANNOT validate the detector."
    : "   => the broad phase had material; narrow-phase rejection is a real judgement, not an empty query.");
Console.WriteLine();
Console.WriteLine($"   ---- rule comparison, all on the same {tested:N0} references ----");
Console.WriteLine($"   {"rule",-32} {"refs",9} {"rate",9}   vanilla floor");
Console.WriteLine($"   {"CONTAIN all-3 verts inside",-32} {blocked,9:N0} {Rate(blocked, tested),8:0.00}%      0.40%");
Console.WriteLine($"   {"CONTAIN any-1 vert inside",-32} {blockedAny,9:N0} {Rate(blockedAny, tested),8:0.00}%      4.00%");
Console.WriteLine($"   {"CROSS   clr32 in12",-32} {blockedCross,9:N0} {Rate(blockedCross, tested),8:0.00}%     11.09%");
Console.WriteLine($"   {"SEVER   clr32 in12 step32",-32} {blockedSever,9:N0} {Rate(blockedSever, tested),8:0.00}%      2.84%");
Console.WriteLine($"   {"SEVER   clr32 in12 step48",-32} {blockedSeverTight,9:N0} {Rate(blockedSeverTight, tested),8:0.00}%      1.82%");
Console.WriteLine($"   navmesh triangles that could not be judged (inset consumed them): {unjudged:N0}");

Console.WriteLine($"\n   ---- actor band, derived per cell ----");
Console.WriteLine($"   cells with placed actors {cellsWithActors:N0} (templated-only {cellsTemplatedOnly:N0}); band top {(bandMin == float.MaxValue ? 0 : bandMin):0}..{bandMax:0} u; fallback {fallbackSrc}");
foreach (var (_, ff) in floorFields) floorTally.Add(ff);
Console.WriteLine("   " + floorTally.Line());
if (floorLandUnjudged > 0)
    Console.WriteLine($"   {floorLandUnjudged:N0} cell pass(es) UNJUDGEABLE: the floor neighbourhood holds a LAND that gives no heights (named above)");
Console.WriteLine("   the floor each cell's band stood on (δ = navmesh − floor over the raster reads; a strongly negative median is a floor laid ABOVE the winning navmesh):");
foreach (var (cell, ff) in floorFields.Where(x => x.Field.Reads > 0).OrderBy(x => x.Field.OffsetQuantile(0.5f)).Take(40))
    Console.WriteLine("      " + FloorFeed.Tally.CellLine(cell, ff));

Console.WriteLine($"\n   ---- the ROUTE rule (Part B): with vs without, ladder {string.Join("/", Ladder.Select(l => l.ToString("0")))} u, margin {RouteMargin:0}, step {RouteStep:0} ----");
Console.WriteLine($"   references measured (solid entered an actor column): {routeMeasured:N0}   doors set aside as passable by design: {routeDoors:N0}");
Console.WriteLine($"   {"verdict",-14} {"threshold 64 u",16} {"threshold 128 u",16}");
foreach (var v in new[] { RouteVerdict.Blocked, RouteVerdict.Narrowed, RouteVerdict.Clipped, RouteVerdict.None, RouteVerdict.Unjudgeable })
    Console.WriteLine($"   {v,-14} {(routeTally64.TryGetValue(v, out var a) ? a : 0),16:N0} {(routeTally128.TryGetValue(v, out var b) ? b : 0),16:N0}");
if (routeHits.Count > 0)
{
    Console.WriteLine($"\n   ---- Blocked / Narrowed / Unjudgeable references ----");
    foreach (var h in routeHits.OrderBy(h => h.V64 == RouteVerdict.Blocked ? 0 : h.V64 == RouteVerdict.Narrowed ? 1 : 2).ThenBy(h => h.Cell).Take(listN * 2))
        Console.WriteLine($"   {h.V64,-11} {h.V128,-11} touched {h.Touched,4} intact {h.Intact,2}/{h.IntactWithout,2}  {h.Cell,-26} {Short(h.Mesh, 46),-46} {h.Fk} {By(h.Fk)}   {h.Why}");
}

Console.WriteLine($"\n   ---- the ROUTE-CUT rule (4.7's closing step): the corridor's path line, cut at capsule 22 u inside the band 32..top ----");
Console.WriteLine($"   band above the feet on the physical floor (Q24): bottom {ActorBand.PhysicalStep:0.#} u, top {(controllerBand ? $"derived × {ActorBand.ControllerFactor} + {ActorBand.StationOffset:0.#} (the controller's, Part C)" : "derived, nominal")}; passage boundary 48 u; step band to {ActorBand.PhysicalStepBandTop:0.#} u and a float of {ActorBand.FloatWall:0} u or more named in the verdict (Q26)");
Console.WriteLine($"   references measured: {routeMeasured:N0}   doors set aside: {routeDoors:N0}   MoveableStatics set aside (pushed aside, Part C): {routeMovable:N0}");
Console.WriteLine($"   {"verdict",-14} {"refs",8}");
foreach (var v in new[] { RouteCutVerdict.Blocked, RouteCutVerdict.LikelyBlocked, RouteCutVerdict.Clipped, RouteCutVerdict.NoLine, RouteCutVerdict.None, RouteCutVerdict.Unjudgeable })
    Console.WriteLine($"   {v,-14} {(cutTally.TryGetValue(v, out var a) ? a : 0),8:N0}" + (v == RouteCutVerdict.NoLine && noLineWhy.Count > 0 ? "   (" + string.Join(", ", noLineWhy.OrderByDescending(kv => kv.Value).Select(kv => $"{kv.Key} {kv.Value}")) + ")" : ""));
if (targets.Count > 1)
{
    Console.WriteLine("   by target (a reference carried by two targets counts under each):");
    foreach (var t in targets)
        Console.WriteLine($"      {t,-40} " + string.Join(", ", new[] { RouteCutVerdict.Blocked, RouteCutVerdict.LikelyBlocked, RouteCutVerdict.Clipped, RouteCutVerdict.NoLine, RouteCutVerdict.None, RouteCutVerdict.Unjudgeable }
                                                  .Select(v => $"{v} {(cutByTarget.TryGetValue((t, v), out var c) ? c : 0):N0}")));
}
{
    Console.WriteLine($"   navcut boxes in the target's cells (population '{pop.StateName}') — {navcutMeasured:N0} measured, {navcutSkippedDisabled:N0} not present in it, {navcutNoCross:N0} over no walkable ground: "
                    + string.Join(", ", new[] { RouteCutVerdict.Blocked, RouteCutVerdict.LikelyBlocked, RouteCutVerdict.Clipped, RouteCutVerdict.NoLine, RouteCutVerdict.None, RouteCutVerdict.Unjudgeable }
                                        .Select(v => $"{v} {(navcutTally.TryGetValue(v, out var b) ? b : 0):N0}")) + "   (the cut a navmesh-obstacle box makes in the target's navmesh; listed with {NAVCUT})");
    Console.WriteLine("      UNCONFIRMED CANDIDATES: a navcut box is authored to cut the navmesh, and no load-time navcut has been watched in game "
                    + "(Q20 Part D watched a SET switched on). The rule also reads each box alone, where that set cut more together than any one box did. "
                    + "Read these rows as candidates for review, never as findings.");
    foreach (var h in navcutHits.Where(h => h.V is RouteCutVerdict.Blocked or RouteCutVerdict.LikelyBlocked or RouteCutVerdict.NoLine).OrderBy(h => h.V == RouteCutVerdict.Blocked ? 0 : h.V == RouteCutVerdict.LikelyBlocked ? 1 : 2).Take(listN))
        Console.WriteLine($"      {h.V,-14} touched {h.Touched,4}  {h.Cell,-26} {h.Fk} {{NAVCUT by {h.Plugin}}}   {h.Why}");
}
if (bosOn)
    Console.WriteLine($"   of which BOS swapped or moved: {cutTallyBos.Values.Sum():N0} measured — "
                    + string.Join(", ", new[] { RouteCutVerdict.Blocked, RouteCutVerdict.LikelyBlocked, RouteCutVerdict.Clipped, RouteCutVerdict.NoLine, RouteCutVerdict.None, RouteCutVerdict.Unjudgeable }
                                        .Select(v => $"{v} {(cutTallyBos.TryGetValue(v, out var b) ? b : 0):N0}"))
                    + "   (the swap layer's own contribution; listed rows carry {BOS …})");
Console.WriteLine($"\n   ---- old rule (64 u) × route-cut rule, the same references ----");
Console.WriteLine($"   {"old \\ new",-12} {"Blocked",9} {"Likely",9} {"Clipped",9} {"NoLine",9} {"None",9} {"Unjudg.",9}");
foreach (var o in new[] { RouteVerdict.Blocked, RouteVerdict.Narrowed, RouteVerdict.Clipped, RouteVerdict.None, RouteVerdict.Unjudgeable })
{
    var cols = new[] { RouteCutVerdict.Blocked, RouteCutVerdict.LikelyBlocked, RouteCutVerdict.Clipped, RouteCutVerdict.NoLine, RouteCutVerdict.None, RouteCutVerdict.Unjudgeable }
               .Select(n => crossTab.TryGetValue((o, n), out var c) ? c : 0).ToArray();
    if (cols.Sum() == 0) continue;
    Console.WriteLine($"   {o,-12} {cols[0],9:N0} {cols[1],9:N0} {cols[2],9:N0} {cols[3],9:N0} {cols[4],9:N0} {cols[5],9:N0}");
}
if (cutHits.Count > 0)
{
    Console.WriteLine($"\n   ---- Blocked / LikelyBlocked / NoLine / Unjudgeable references (route-cut) ----");
    static string Pass(RouteLine? l) => l is null || float.IsNaN(l.Passage) ? "   -" : l.Passage >= l.PassageCap ? $"≥{l.PassageCap:0}" : $"{l.Passage,4:0}";
    foreach (var h in cutHits.OrderBy(h => h.V == RouteCutVerdict.Blocked ? 0 : h.V == RouteCutVerdict.LikelyBlocked ? 1 : h.V == RouteCutVerdict.NoLine ? 2 : 3).ThenBy(h => h.Cell).Take(listN * 3))
        Console.WriteLine($"   {h.V,-14} was {h.Old,-9} touched {h.Touched,4} passage {Pass(h.Line)} (L {(h.Line is null || float.IsNaN(h.Line.PassageLeft) ? "-" : h.Line.PassageLeft.ToString("0")),3} / R {(h.Line is null || float.IsNaN(h.Line.PassageRight) ? "-" : h.Line.PassageRight.ToString("0")),3})  {h.Cell,-26} {Short(h.Mesh, 44),-44} {h.Fk} {By(h.Fk)}   {h.Why}");
}

if (severHits.Count > 0)
{
    Console.WriteLine();
    Console.WriteLine($"   ---- what SEVER catches (top meshes) ----");
    foreach (var grp in severHits.GroupBy(h => h.Mesh, StringComparer.OrdinalIgnoreCase)
                                 .OrderByDescending(grp => grp.Sum(h => h.Tris)).Take(listN))
        Console.WriteLine($"   {grp.Sum(h => h.Tris),5} tri  {grp.Count(),4} refs  {grp.First().Type,-12} {Short(grp.Key, 52)}");
}

if (hits.Count > 0)
{
    Console.WriteLine($"\n   ---- worst blockages ----");
    foreach (var h in hits.OrderByDescending(h => h.Tris).Take(listN))
        Console.WriteLine($"   {h.Tris,5} tri  {(h.TargetGaveNav ? "own-nav" : "NO-NAV "),-8} {h.Cell,-28} "
                        + $"{Short(h.Mesh, 42),-42} nav:{Short(h.NavOwner, 28)}");

    Console.WriteLine($"\n   ---- by cell ----");
    foreach (var g in hits.GroupBy(h => h.Cell).OrderByDescending(g => g.Sum(h => h.Tris)).Take(listN))
        Console.WriteLine($"   {g.Sum(h => h.Tris),6} tri  {g.Count(),4} refs  {(g.First().TargetGaveNav ? "own-nav" : "NO-NAV "),-8} "
                        + $"{g.Key,-30} nav:{Short(g.First().NavOwner, 40)}");
}

return 0;

static string Short(string s, int n) => s.Length <= n ? s : "..." + s[^(n - 3)..];

static MeshColumnIndex? BuildColumns(TriangleSoup soup, Vec3 pos, Vec3 rot, float scale)
{
    if (soup.TriangleCount == 0) return null;
    var m = SpatialOverlap.RefrRotation(rot);
    var t = new Vector3(pos.X, pos.Y, pos.Z);
    var w = new Vector3[soup.Vertices.Length];
    for (int i = 0; i < w.Length; i++) w[i] = Vector3.Transform(soup.Vertices[i] * scale, m) + t;
    return MeshColumnIndex.Build(w, soup.Indices, triangleMaterials: soup.TriangleMaterials);
}

record Geo(Aabb Box, bool HasCollision, bool Ok, TriangleSoup? Soup, string Source);
