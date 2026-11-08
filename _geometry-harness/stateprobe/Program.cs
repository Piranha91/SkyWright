// HousecarlCore now has its own PluginUnreadableException (upstream 2.0); this probe means the harness one.
using PluginUnreadableException = HarnessShared.PluginUnreadableException;
using System.Diagnostics;
using System.Numerics;
using System.Text.Json;
using HarnessShared;
using HousecarlCore;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Skyrim;
using HousecarlCore.Progress;

// STATEPROBE — the state-conditional class (roadmap 4.11, handoff 2026-09-20 Part C.2): every switch (or the states
// named) flipped, and the geometry detectors run across the flip on the switch's own sets. Contact and route retain
// their established predicates. State overlap is explicitly withheld until this evaluator consumes the strict placed
// render surfaces used by the current overlap predicate; legacy box overlap is never emitted as a current finding.
//
// The evaluation is _shared/StateClass.cs (StateEvaluator over ContactIndex + SwitchIndex); the census is
// _shared/WorldState.cs; the decision DetectorModel.StateDifferential. This file is I/O: the load order into the
// index, meshes through the VFS (contactprobe's geometry resolver), the winning navmesh and actor band per cell for
// the route oracle (blockprobe's obstacle step), the tallies, the listings, --json and --emit.
//
// usage: stateprobe [--state SPEC]* [--states-file F] [--all] [--cell CELL]* [--switch-map FILE] [--list N] [--json FILE] [--emit FILE]
//                   [--route on|off] [--threads N] [--enable X.esp]* [--disable X.esp]* [--instance DIR] [--game DIR] [--profile NAME]
//   --threads N      arms evaluated side by side (default 8); each arm's rows merge in arm order, so the rows are a sequential
//                    run's. 1 is the plain sequential loop. --phase-timers' state-loop and contact splits then sum every worker's time.
//   --state SPEC     a state to evaluate: <switch>[,<switch>…] by editor id or FormKey, or stage:<quest>:<n> (needs --switch-map)
//   --states-file F  one --state SPEC per line (the Deep evaluation's list); every line is parsed as --state is
//   --all            every switch alone, plus every joint flip in --switch-map when given
//   --cell CELL      every switch with a set member in the cell (editor id prefix or FormKey) — the per-contested-cell mode
//   --switch-map     switchprobe --json output: the joint flips
//   --route on|off   the route half (default on): the cell's winning navmesh and actor band are read for every cell a
//                    changed reference stands in; off skips it (counted, never invented). Since Q27 (2026-09-06) the
//                    floor the band stands on and the band's top belong to the STATE being judged, not to the baseline:
//                    a flip can bring in a floor piece or a taller actor, and measuring an arriving obstacle against the
//                    baseline's floor mixes two worlds. Fields are keyed on (cell, state) and the baseline's field is
//                    reused where the flip changes nothing in that cell, so the cost is a lookup, not a rebuild.
//   --emit FILE      the measured class as a ConflictFile (STATE-CONTACT / STATE-ROUTE @ state); STATE-OVERLAP is explicitly unmeasured
//   --population F   the plan's population (_shared/PopulationScope.cs; window 2): only the changed references it can reach are
//                    processed (contact: a population reference or one within reach of it; route: an interior population or
//                    delta-terrain/navmesh cell, or an exterior cell within 5 cells of a seed, a delta terrain/navmesh cell or a
//                    reference a delta worldspace lists as large), and per-state empty space judges fixtures of the population's
//                    interior cells; the index stays whole, so every kept row is the whole-order run's. A delta carrying RACE
//                    records is refused (every actor band reads it). --population-audit EMIT OUT: per row of EMIT, kept or not.
//   --empty-space F  LEFT IN EMPTY SPACE for each state's ARRIVING references (_shared/ContactAssembly.cs, DetectorModel.EmptySpace):
//                    in the state, a fixture whose assembly reached a floor or wall some plugin removed or moved, and reaches none
//                    under the winner; rows to F, the class to --emit as STATE-EMPTY @ state (a fixture that only exists after a
//                    quest flips its switch is judged in the state that brings it in, which the load-time contact pass cannot see)

using var __run = RunLog.BeginProcess("stateprobe", args);
// Known answers for which version of a moved or deleted reference this probe's walk keeps (_shared/MovedRefFixture.cs).
if (args.Length >= 1 && args[0] == "--moved-ref-selftest") return RouteSelfTest.Run();
// Known answers for the class, emit and route-obstacle decisions over a synthetic order (_shared/StateFixture.cs).
if (args.Length >= 1 && args[0] == "--self-test") return StateSelfTest.Run();
// The modelless light fixture: written for tests/test_modelless_light.py, which runs the real stateprobe and contactprobe on it.
if (args.Length >= 2 && args[0] == "--write-modelless-activator-fixture") { var w = ModellessActivatorFixture.Write(args[1]); Console.WriteLine(string.Join(Environment.NewLine, w.Order.Append(w.ModsDir))); return 0; }
if (args.Length >= 2 && args[0] == "--write-modelless-light-fixture") { var w = ModellessLightFixture.Write(args[1]); Console.WriteLine(string.Join(Environment.NewLine, w.Order.Append(w.ModsDir))); return 0; }
// The row identity fixture: written for tests/test_state_row_identity.py, which runs the real probe on it.
if (args.Length >= 2 && args[0] == "--write-row-identity-fixture") { var w = RowIdentityFixture.Write(args[1]); Console.WriteLine(string.Join(Environment.NewLine, w.Order.Append(w.ModsDir))); return 0; }
// The scope fixture (window 2): written for tests/test_population_scope.py, which runs the real probes on it.
if (args.Length >= 2 && args[0] == "--write-scope-fixture") { var w = ScopeFixture.Write(args[1]); Console.WriteLine(string.Join(Environment.NewLine, w.Order.Append(w.RacePlugin).Append(w.ModsDir))); return 0; }
// state-contact-route-v2 (2026-10-24): a light whose base record has no model is no dependent (ContactIndex.IsBodiless), so
// conflictdiff refuses a pair of emits across the change rather than credit the vanished rows.
// state-contact-route-v3 (2026-10-25): nor is an activator or a door with no model (station AD), and the changed references
// with no box are split by name (editor markers set aside; markers carrying a primitive unjudged apart).
// state-contact-route-v4 (2026-11-08): with --primitive-solidity, marker primitives station Q measured are decided (not solid
// set aside, solid unjudged by name); the rest of the changed references with no box are named by cause.
const string StateMeasurementPolicy = "state-contact-route-v4|state-overlap-unmeasured-v1";

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
string? jsonPath = null, emitPath = null, mapPath = null, emptySpaceOut = null;
string? phaseTimersPath = null;   // --phase-timers FILE: per-phase seconds and one line per state (lane O P0); changes no decision
bool all = false, routeOn = true;
int threads = 8;
var stateSpecs = new List<string>(); var cellFilters = new List<string>();
var enablePlugins = new List<string>(); var disablePlugins = new List<string>();
string? orderFile = null;
string? populationPath = null, populationAuditEmit = null, populationAuditOut = null;
string? primitiveSolidityPath = null;   // --primitive-solidity FILE: station Q's measured (base, layer) table for marker primitives
for (int i = 0; i < args.Length; i++)
{
    if (args[i] == "--list" && i + 1 < args.Length) listN = int.Parse(args[++i]);
    else if (args[i] == "--json" && i + 1 < args.Length) jsonPath = args[++i];
    else if (args[i] == "--emit" && i + 1 < args.Length) emitPath = args[++i];
    else if (args[i] == "--empty-space" && i + 1 < args.Length) emptySpaceOut = args[++i];
    else if (args[i] == "--switch-map" && i + 1 < args.Length) mapPath = args[++i];
    else if (args[i] == "--state" && i + 1 < args.Length) stateSpecs.Add(args[++i]);
    else if (args[i] == "--states-file" && i + 1 < args.Length)
    {
        // One --state SPEC per line (a Deep evaluation's list does not fit a command line); blank lines skipped.
        var sf = args[++i];
        if (!File.Exists(sf)) { Console.Error.WriteLine($"--states-file {sf}: no such file"); return 2; }
        foreach (var line in File.ReadAllLines(sf)) if (line.Trim() is { Length: > 0 } s) stateSpecs.Add(s);
        if (stateSpecs.Count == 0) { Console.Error.WriteLine($"--states-file {sf}: the file names no state"); return 2; }
    }
    else if (args[i] == "--cell" && i + 1 < args.Length) cellFilters.Add(args[++i]);
    else if (args[i] == "--all") all = true;
    else if (args[i] == "--route" && i + 1 < args.Length) routeOn = args[++i].ToLowerInvariant() switch { "on" => true, "off" => false, _ => throw new ArgumentException("--route takes on|off") };
    else if (args[i] == "--threads" && i + 1 < args.Length)
    {
        if (!int.TryParse(args[++i], out threads) || threads < 1) { Console.Error.WriteLine($"--threads {args[i]}: give a whole number of at least 1"); return 2; }
    }
    else if (args[i] == "--order" && i + 1 < args.Length) orderFile = args[++i];
    else if (args[i] == "--enable" && i + 1 < args.Length) enablePlugins.Add(args[++i]);
    else if (args[i] == "--disable" && i + 1 < args.Length) disablePlugins.Add(args[++i]);
    else if (args[i] == "--population" && i + 1 < args.Length) populationPath = args[++i];
    else if (args[i] == "--phase-timers" && i + 1 < args.Length) phaseTimersPath = args[++i];
    else if (args[i] == "--primitive-solidity" && i + 1 < args.Length) primitiveSolidityPath = args[++i];
    else if (args[i] == "--population-audit" && i + 2 < args.Length) { populationAuditEmit = args[++i]; populationAuditOut = args[++i]; }
}
if (populationAuditEmit is not null && populationPath is null) { Console.Error.WriteLine("--population-audit needs --population"); return 2; }
if (populationAuditEmit is not null && !File.Exists(populationAuditEmit)) { Console.Error.WriteLine($"--population-audit input does not exist: {populationAuditEmit}"); return 2; }
PopulationScope? population = null;
if (populationPath is not null)
{
    try { population = PopulationScope.Load(populationPath); }
    catch (PopulationRefused ex) { Console.Error.WriteLine(ex.Message); return 2; }
    if (population.RaceCarriers.Count > 0)
    {
        Console.Error.WriteLine($"{string.Join(", ", population.RaceCarriers)} carries RACE records, which change every actor band the route half reads; run stateprobe on the whole order (no --population).");
        return 2;
    }
}
IReadOnlyList<PrimitiveMeasurement>? primitiveTable = null;
if (primitiveSolidityPath is not null)
{
    if (!File.Exists(primitiveSolidityPath)) { Console.Error.WriteLine($"--primitive-solidity {primitiveSolidityPath}: no such file"); return 2; }
    try { primitiveTable = PrimitiveSolidity.Read(File.ReadAllText(primitiveSolidityPath)); }
    catch (Exception ex) when (ex is ArgumentException or System.Text.Json.JsonException) { Console.Error.WriteLine($"--primitive-solidity {primitiveSolidityPath}: {ex.Message}; fix the table or leave the flag out."); return 2; }
}
if (stateSpecs.Count == 0 && cellFilters.Count == 0 && !all) { Console.Error.WriteLine("nothing to evaluate: give --state, --cell or --all"); return 2; }

var sw = Stopwatch.StartNew();
var phase = new PhaseClock(phaseTimersPath is not null);
// WHAT THIS RUN EVALUATES, decided in one place (_shared/HarnessOrderCli.cs): the real profile, a --enable/--disable
// what-if, or a --order candidate file - and, in the same step, the ASSET VIEW that configuration implies, so a run
// whose plugins changed no longer reads the profile's meshes and BSAs regardless. The identity it produces is written
// into the emit, so a reader (and conflictdiff) can see exactly what was consumed instead of inferring it.
var cfg = HarnessOrderCli.Resolve(profileName, profileDir, modsDir, dataDir, overwriteDir, GamePath,
                                  orderFile, enablePlugins, disablePlugins);
if (cfg.Refused) { Console.Error.WriteLine(cfg.RefusedReason); return 2; }
var paths = cfg.OrderedPaths;
using var assets = AssetResolver.Build(overwriteDir, modsDir, dataDir, cfg.EnabledMods, cfg.Archives.Archives);
Console.WriteLine($"load order: {paths.Count} plugins; VFS: {cfg.EnabledMods.Count} enabled mods, {cfg.Archives.Archives.Count} archives\n");
HarnessOrderCli.Print(cfg);

// ---------------------------------------------------------------------------------------
// Geometry — contactprobe's resolver: the actor-solid collision as the host surface, every solid body as the
// dependent's, the render soup as a labelled fallback, OBND as the last resort.
// ---------------------------------------------------------------------------------------
ContactIndex? cidx = null;
var geo = new StateGeometry(full => assets.ResolveForPlacement(full) is { Sources.Count: > 0 } res ? AssetResolver.ReadPlacementSource(res.Sources[0]).Bytes : null);   // stateprobe/StateGeometry.cs
try { cidx = new ContactIndex(geo.Of, automaticThreads: threads); geo.Contact = cidx; }
catch (CacheBudget.AutomaticBudgetUnavailableException ex) { Console.Error.WriteLine(ex.Message + "; " + ex.Selection.Describe()); return 2; }
var idx = cidx.Index;

// ---------------------------------------------------------------------------------------
// Pass 1 — every plugin once, into the shared index.
// ---------------------------------------------------------------------------------------
var pluginKey = new ModKey[paths.Count];
var masterIdx = new HashSet<int>[paths.Count];   // the master closure, for the empty-space pass's arm without a mover's side
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
Console.WriteLine($"indexed {idx.Placements:N0} placements over {idx.History.Count:N0} refs  ({sw.Elapsed.TotalSeconds:0.0}s); unreadable plugins {unreadable.Count}");
phase.Mark("configuration+index");
string PluginName(int p) => p < 0 ? "(none)" : pluginKey[p].FileName.String;
bool IsVanilla(int p) => p >= 0 && PluginKinds.IsBethesda(PluginName(p));
bool IsUssep(int p) => p >= 0 && PluginKinds.IsUssep(PluginName(p));
string ClassOf(int p) => IsVanilla(p) ? "Bethesda" : IsUssep(p) ? "USSEP" : "mod";
string CellOf(FormKey c) => idx.CellName.TryGetValue(c, out var v) ? (v.Edid ?? c.ToString()) + (v.Interior ? " (int)" : idx.PersistentCells.Contains(c) ? " (persistent cell)" : " (ext)") : c.ToString();
string BaseLabel(FormKey k) => idx.BaseInfo.TryGetValue(k, out var bi) ? $"{bi.Type} {bi.Edid ?? k.ToString()}" : k.ToString();
string RefLabel(FormKey k) => $"{k}{(idx.RefEditorId.TryGetValue(k, out var e) ? " " + e : "")} [{(idx.RefBase.TryGetValue(k, out var b) ? BaseLabel(b) : "?")}]";

// ---------------------------------------------------------------------------------------
// The census and the states.
// ---------------------------------------------------------------------------------------
var sx = SwitchIndex.Build(idx);
sx.NameSwitches(idx.RefEditorId);
SwitchMap? map = mapPath is null ? null : SwitchMap.Read(mapPath);
Console.WriteLine($"switches: {sx.Switches.Count:N0} ({sx.Switches.Values.Sum(s => s.Descendants.Count):N0} set members); navcuts {sx.Navcuts.Count:N0}, under a switch {sx.NavcutUnderSwitch.Count:N0}"
                + (map is null ? "" : $"; switch map: {map.Stages.Count:N0} quest stages"));
var arms = new List<StateArm>();
try
{
    foreach (var spec in stateSpecs) arms.Add(StateArm.Parse(spec, sx, map));
    foreach (var cf in cellFilters)
    {
        var cells = idx.CellName.Where(kv => (kv.Value.Edid ?? "").StartsWith(cf, StringComparison.OrdinalIgnoreCase) || kv.Key.ToString().Equals(cf, StringComparison.OrdinalIgnoreCase)).Select(kv => kv.Key).ToHashSet();
        if (cells.Count == 0) { Console.Error.WriteLine($"--cell {cf}: no such cell in this order"); return 2; }
        foreach (var s in sx.Switches.Values)
            if (s.Cells.Any(cells.Contains) || cells.Contains(s.Cell))
                if (!arms.Any(a => a.Flips.Count == 1 && a.Flips[0] == s.Key)) arms.Add(new StateArm(s.Label, new[] { s.Key }));
    }
    if (all)
    {
        foreach (var s in sx.Switches.Values.OrderBy(s => s.Key.ToString()))
            if (!arms.Any(a => a.Flips.Count == 1 && a.Flips[0] == s.Key)) arms.Add(new StateArm(s.Label, new[] { s.Key }));
        if (map is not null)
            foreach (var st in map.Stages.Keys.OrderBy(k => k))
                try { var a = StateArm.Parse("stage:" + st, sx, map); if (a.Flips.Count >= 2) arms.Add(a); } catch { }
    }
}
catch (ArgumentException ex) { Console.Error.WriteLine(ex.Message); return 2; }
Console.WriteLine($"states to evaluate: {arms.Count:N0} ({arms.Count(a => a.Flips.Count == 1):N0} single switches, {arms.Count(a => a.Flips.Count > 1):N0} joint)");
phase.Mark("switches+states");

// ---------------------------------------------------------------------------------------
// The route oracle — blockprobe's obstacle step over the cells a changed reference stands in.
// ---------------------------------------------------------------------------------------
var wantedCells = new HashSet<FormKey>();
foreach (var a in arms) foreach (var c in a.Changes(sx)) if (idx.History.Winning(c.Key) is { } w) wantedCells.Add(w.Cell);
Console.WriteLine($"cells touched by the states' changes: {wantedCells.Count:N0}");
RouteAsk? oracle = null;
Func<string>? routeFloorLine = null;
Dictionary<FormKey, (FormKey Wrld, int X, int Y)>? routeCellGrid = null;
long routeMeasured = 0, routeNoNav = 0, routeNoGeo = 0, routeSetAside = 0, routeNoCross = 0;
// The (cell, state) fields in the order a sequential run first asks for them: each arm's asks, merged in arm order.
var fieldOrder = new List<(FormKey Cell, string State)>();
var fieldSeen = new HashSet<(FormKey Cell, string State)>();
var fieldAsks = new ThreadLocal<List<(FormKey Cell, string State)>?>();
if (routeOn)
{
    sw.Restart();
    var npcFacts = new Dictionary<FormKey, ActorBand.NpcFacts>();
    var raceFacts = new Dictionary<FormKey, ActorBand.RaceFacts>();
    // The walk's accumulation (the wanted cells' navmeshes and references, the floor filing) lives in RouteWalk.cs, so
    // --moved-ref-selftest runs the same code.
    var rwalk = new RouteWalk(wantedCells);
    var winNav = rwalk.WinNav;
    var cellRefs = rwalk.CellRefs;
    var cellGridOf = rwalk.CellGridOf;
    routeCellGrid = cellGridOf;
    var floorRefsByWorld = rwalk.FloorRefsByWorld;
    var floorLandByWorld = rwalk.FloorLandByWorld;
    // A plugin the first pass could not read is counted there (rule 6); this walk decides which version of a reference
    // the route stands on, so it refuses one it cannot read rather than skip it (_shared/PluginOpen.cs).
    try { if (paths.Count > 0) rwalk.Seed(paths[0]); }
    catch (PluginUnreadableException ex) { Console.Error.WriteLine(ex.Message); return 2; }
    for (int p = 0; p < paths.Count; p++)
    {
        ISkyrimModGetter m;
        try { m = RouteWalk.Open(paths[p])!; }
        catch (PluginUnreadableException ex) { Console.Error.WriteLine(ex.Message); return 2; }
        try
        {
            using (m as IDisposable)
            {
                foreach (var rec in m.EnumerateMajorRecords()) ActorBand.CollectRecord(rec, npcFacts, raceFacts);
                rwalk.Plugin(m);
            }
        }
        // A plugin opens lazily: one whose groups fail while this walk reads them is refused, named (_shared/PluginOpen.cs).
        catch (Exception ex) when (ex is not PluginUnreadableException)
        { Console.Error.WriteLine(new PluginUnreadableException(paths[p], "stateprobe's route walk", ex, midWalk: true).Message); return 2; }
        catch (PluginUnreadableException ex) { Console.Error.WriteLine(ex.Message); return 2; }
    }
    rwalk.Dump();
    ActorBand.ResolveControllers(raceFacts, assets);
    var (fallbackTop, fallbackSrc) = ActorBand.TallestPlayable(raceFacts);
    Console.WriteLine($"route oracle: winning navmesh read for {winNav.Count:N0} of {wantedCells.Count:N0} cells; actor band fallback {fallbackSrc}; the band stands on the physical floor (Q24: {floorRefsByWorld.Values.Sum(w => w.Values.Sum(d => d.Count)):N0} exterior floor refs filed for {cellGridOf.Count:N0} exterior cells' neighbourhoods)  ({sw.Elapsed.TotalSeconds:0.0}s)");
    // The navmesh and its walkable graph do not depend on the state; the floor and the band do (see FieldFor).
    var navIndex = new System.Collections.Concurrent.ConcurrentDictionary<FormKey, Lazy<NavCell>>();
    // One field per (cell, state) — see FieldFor below. Null when the cell's band is unjudgeable in that state.
    var fieldByCellState = new System.Collections.Concurrent.ConcurrentDictionary<(FormKey Cell, string State), Lazy<((float Top, FloorField Floor)? Field, string Why)>>();
    routeFloorLine = () =>
    {
        var fields = fieldOrder.Select(k => (Key: k, Value: fieldByCellState[k].Value.Field)).ToList();
        var t = new FloorFeed.Tally(); foreach (var f in fields) if (f.Value is { } ff) t.Add(ff.Floor);
        var sb = new System.Text.StringBuilder(t.Line());
        sb.Append($"\n   {fields.Count:N0} (cell, state) fields built; the floor each stood on (δ = navmesh − floor over the raster reads; a strongly negative median is a floor laid ABOVE the winning navmesh):");
        foreach (var kv in fields.Where(kv => kv.Value is { } v && v.Floor.Reads > 0).OrderBy(kv => kv.Value!.Value.Floor.OffsetQuantile(0.5f)).Take(40))
            sb.Append("\n      " + FloorFeed.Tally.CellLine($"{kv.Key.Cell} @ {kv.Key.State}", kv.Value!.Value.Floor));
        return sb.ToString();
    };
    var soupCache = new System.Collections.Concurrent.ConcurrentDictionary<string, Lazy<(Aabb Box, TriangleSoup? Soup, string Source)>>(StringComparer.OrdinalIgnoreCase);
    (Aabb Box, TriangleSoup? Soup, string Source) Obstacle(string rel)
        => soupCache.GetOrAdd(rel, r => new Lazy<(Aabb Box, TriangleSoup? Soup, string Source)>(() => { lock (StateGeometry.MeshGate) return ReadObstacle(r); })).Value;
    (Aabb Box, TriangleSoup? Soup, string Source) ReadObstacle(string rel)
    {
        var full = rel.StartsWith("meshes", StringComparison.OrdinalIgnoreCase) ? rel : Path.Combine("meshes", rel);
        var res = assets.ResolveForPlacement(full);
        if (res.Sources.Count == 0) return (Aabb.Empty, null, "unresolved");
        var (bytes, _) = AssetResolver.ReadPlacementSource(res.Sources[0]);
        if (bytes is null) return (Aabb.Empty, null, "unresolved");
        var o = NifGeometry.Measure(bytes, includeTriangles: true);
        if (o.Model is null) return (Aabb.Empty, null, "unreadable");
        var m = o.Model;
        bool anySolid = m.CollisionBodies is not null && m.CollisionBodies.Any(b => b.Solid && CollisionLayers.IsActorSolid(b.Layer));
        if (!m.HasCollision || !anySolid) return (Aabb.Empty, null, "none");
        var solid = m.CollisionSoupFor(b => b.Solid && CollisionLayers.IsActorSolid(b.Layer));
        if (m.CollisionComplete && solid is { TriangleCount: > 0 })
        {
            var bx = Aabb.Empty;
            foreach (var v in solid.Vertices) bx = bx.Add(v);
            return (bx, solid, "collision");
        }
        return (m.Bounds, m.Soup is { TriangleCount: > 0 and <= 200_000 } ? m.Soup : null, "fallback");
    }
    var Ladder = new[] { 0f, 32f, 64f, 96f, 128f };
    // The floor the band stands on and the band's top belong to the STATE being judged (Q27): a flip can bring in a floor
    // piece or a taller actor, so a cell's field is keyed on (cell, state). The navmesh and its graph do not depend on the
    // state and stay keyed on the cell alone. Rebuilding for every state would be waste — a state's population differs
    // from the baseline's only on the references the flip changes — so the baseline field is reused unless the arm moves
    // one of the cell's own references (the band's actors) or one of its floor candidates (for an exterior cell the 3x3
    // and the reach, not the cell alone): StateFieldKey.For, over a neighbourhood memoised per cell.
    var nbByCell = new Dictionary<FormKey, (List<PlacedRef> Refs, List<((int X, int Y) Grid, float[,] Heights)> Land)>();
    (Aabb Box, TriangleSoup Soup)? FloorGeometry(PlacedRef r)
    {
        if (r.Base is not { } b || !idx.BaseMesh.TryGetValue(b, out var rel)) return null;
        var g = Obstacle(rel);
        return g.Soup is not null && !g.Box.IsEmpty ? (g.Box, g.Soup) : null;
    }
    var baselinePop = EnablePopulation.Of(cidx!.Arms.WinnerRawFacts, EnablePopulation.BaselineStateName);
    var armPops = new System.Collections.Concurrent.ConcurrentDictionary<string, EnablePopulation>();
    EnablePopulation PopOfArm(StateArm arm)
    {
        if (arm.IsBaseline) return baselinePop;
        if (armPops.TryGetValue(arm.Name, out var hit)) return hit;
        var a = new EnableArms(idx) { State = arm };
        return armPops[arm.Name] = EnablePopulation.Of(a.WinnerStateFacts, arm.Name);
    }
    (float Top, FloorField Floor)? FieldFor(FormKey cell, StateArm arm, out string why)
    {
        why = "";
        var pop = PopOfArm(arm);
        var crefs = cellRefs.TryGetValue(cell, out var cr) ? cr : new List<PlacedRef>();
        // An exterior cell's floor comes from its 3x3 and the reach, not from its own references alone.
        (List<PlacedRef> Refs, List<((int X, int Y) Grid, float[,] Heights)> Land)? nb = null;
        if (cellGridOf.TryGetValue(cell, out var cg) && floorRefsByWorld.ContainsKey(cg.Wrld))
        {
            // The floor index fills its worldspace maps as it is asked, so one asker at a time.
            lock (nbByCell)
            {
                if (!nbByCell.TryGetValue(cell, out var got0))
                    nbByCell[cell] = got0 = rwalk.Floor.NeighbourhoodOf(cg.Wrld, (cg.X, cg.Y));   // the 3x3 by position plus the reach (FloorFeed)
                nb = got0;
            }
        }
        // Does this state differ from the baseline anywhere the field is built from? If not, its field IS the
        // baseline's, and the key collapses.
        string key = StateFieldKey.For(arm, crefs, nb?.Refs, pop, baselinePop);
        var usePop = key == EnablePopulation.BaselineStateName ? baselinePop : pop;
        fieldAsks.Value?.Add((cell, key));
        var nbHere = nb;
        var made = fieldByCellState.GetOrAdd((cell, key), _ => new Lazy<((float Top, FloorField Floor)? Field, string Why)>(() =>
        {
            var band = ActorBand.ForCell(crefs, npcFacts, raceFacts, fallbackTop, fallbackSrc, usePop.Predicate);
            if (!float.IsFinite(band.Top)) return (null, $"UNJUDGEABLE actor band: {band.Source}");
            if (cellGridOf.TryGetValue(cell, out var cgL) && rwalk.Floor.UnreadableNear(cgL.Wrld, (cgL.X, cgL.Y)) is { Count: > 0 } badLand)
                return (null, $"UNJUDGEABLE floor: a LAND in the neighbourhood gives no heights ({string.Join("; ", badLand)})");
            FloorField floor;
            if (nbHere is { } n) floor = FloorFeed.Build(n.Refs, FloorGeometry, n.Land, enabled: usePop.Predicate);
            else floor = FloorFeed.Build(crefs, FloorGeometry, null, enabled: usePop.Predicate);
            return ((ActorBand.PhysicalControllerTop(band.Top), floor), "");
        })).Value;
        why = made.Why;
        return made.Field;
    }
    oracle = (StateArm arm, FormKey key, bool navcut, out string why) =>
    {
        why = "";
        var win = idx.History.Winning(key);
        if (win is not { } w) { why = "no winning placement"; return null; }
        if (!winNav.TryGetValue(w.Cell, out var navs) || navs.Count == 0) { why = "the cell has no navmesh"; Interlocked.Increment(ref routeNoNav); return null; }
        var ni = navIndex.GetOrAdd(w.Cell, _ => new Lazy<NavCell>(() => new NavCell(NavmeshIndex.Build(navs.Values.ToList(), out var _)))).Value;
        if (FieldFor(w.Cell, arm, out var fieldWhy) is not { } field) { why = fieldWhy; return null; }
        if (ni.Index.TriangleCount == 0) { why = "the cell's navmesh is empty"; Interlocked.Increment(ref routeNoNav); return null; }
        TriangleSoup soup; Aabb model;
        if (navcut)
        {
            if (!idx.RefPrimitive.TryGetValue(key, out var prim)) { why = "navcut without a primitive"; Interlocked.Increment(ref routeNoGeo); return null; }
            model = NavcutGeometry.ModelBox(prim); soup = NavcutGeometry.Soup(prim);
            if (model.IsEmpty) { why = "navcut with an empty box"; Interlocked.Increment(ref routeNoGeo); return null; }
        }
        else
        {
            var b = cidx!.WinnerBase(key);
            if (b.IsNull || !idx.BaseMesh.TryGetValue(b, out var rel)) { why = "no mesh"; Interlocked.Increment(ref routeNoGeo); return null; }
            string type = idx.BaseInfo.TryGetValue(b, out var bi) ? bi.Type : "?";
            if (RouteObstacle.SetAside(type, out var asideWhy)) { why = asideWhy; Interlocked.Increment(ref routeSetAside); return null; }   // stateprobe/RouteObstacle.cs
            var g = Obstacle(rel);
            if (g.Soup is null || g.Box.IsEmpty) { why = $"no actor-solid geometry ({g.Source})"; Interlocked.Increment(ref routeNoGeo); return null; }
            model = g.Box; soup = g.Soup;
        }
        var world = SpatialOverlap.ToWorld(model, w.Pos, w.Rot, w.Scale);
        var verts = NavcutGeometry.WorldVertices(soup, w.Pos, w.Rot, w.Scale);
        var columns = MeshColumnIndex.Build(verts, soup.Indices, triangleMaterials: soup.TriangleMaterials);
        // The cheap test first, as blockprobe does: does the solid enter an actor column over any walkable triangle?
        var q = SpatialOverlap.GrowDown(world, field.Top);
        bool crosses = RouteObstacle.EntersActorColumn(ni.Index, columns, q, field.Top);
        if (!crosses) { why = "never enters an actor column over walkable ground"; Interlocked.Increment(ref routeNoCross); return RouteCutVerdict.None; }
        var graph = ni.Graph.Value;
        var facts = RouteConnectivity.Measure(ni.Index, columns, world, 256f, 16f, ActorBand.PhysicalStep, field.Top, Ladder, graph: graph, floorAt: field.Floor.FloorAt, isStairsMaterial: DetectorModel.IsStairsMaterial);
        var line = facts.Line;
        var vc = DetectorModel.RouteCut(facts.Judged, facts.Touched, line?.Found ?? false, line?.Why ?? "", line?.Cut ?? false, line?.Passage ?? float.NaN, cutRise: line?.CutRise ?? float.NaN, cutFloat: line?.CutFloat ?? float.NaN, cutOnStairs: line?.CutOnStairs ?? false);
        Interlocked.Increment(ref routeMeasured);
        why = vc.Why;
        return vc.Verdict;
    };
}
else Console.WriteLine("route oracle: off (--route on to measure the route half)");

// ---------------------------------------------------------------------------------------
// Evaluate.
// ---------------------------------------------------------------------------------------
sw.Restart();
phase.Mark("route-oracle");
var ev = new StateEvaluator(cidx, sx) { Route = oracle, PrimitiveTable = primitiveTable };
var stateTimer = phaseTimersPath is not null ? new StatePhaseTimer() : null;
ev.Timer = stateTimer;
if (phaseTimersPath is not null) cidx.Timer = new ContactTimer();   // --phase-timers: the contact code's own split (changes no output)
var stateLines = new List<string>();
// The population rule (window 2, task 1), decided once per changed reference and the same in every state.
var keyRule = new System.Collections.Concurrent.ConcurrentDictionary<FormKey, string?>();
bool RefInPopulation(FormKey key) => population!.Seeds.Contains(key) || (idx.History.Of(key) is { } hs && hs.Any(population.PlaceInScope));
float contactNeighbourhood = cidx.Reach + cidx.BoxBand + cidx.MaxDependentDim;
string? RuleOf(FormKey k)
{
    if (keyRule.TryGetValue(k, out var hit)) return hit;
    string? r = null;
    if (RefInPopulation(k) || ev.AnyNear(k, contactNeighbourhood, RefInPopulation)) r = "contact";
    else if (idx.History.Winning(k) is { } w)
    {
        (int X, int Y)? grid = null;
        if (!w.Interior)
            grid = routeCellGrid is not null && routeCellGrid.TryGetValue(w.Cell, out var cg) && cg.Wrld == w.Worldspace ? (cg.X, cg.Y) : PopulationScope.GridAt(w.Pos);
        r = population!.RouteRule(w.Cell, w.Interior, w.Worldspace, grid);
    }
    return keyRule[k] = r;
}
if (population is not null)
{
    population.Bind(idx);
    population.BindRoute(idx);
    Console.WriteLine("   " + population.Describe() + $"; route ring {population.RouteRingUsed}, delta terrain/navmesh cells {population.FileGeometryInterior.Count + population.FileGeometryExterior.Count:N0}, listed large {population.FileListedLarge.Count:N0}");
    ev.KeyInScope = k => RuleOf(k) is not null;
}
// The evaluator constructor prepares the winning geometry; refresh after that work, before evaluating any arm.
try { cidx.RefreshAutomaticBudget(); }
catch (CacheBudget.AutomaticBudgetUnavailableException ex) { Console.Error.WriteLine(ex.Message + "; " + ex.Selection.Describe()); return 2; }
Console.WriteLine("   memory after state geometry preparation: " + cidx.AutomaticBudgetReport());
Console.WriteLine($"evaluator: {ev.Boxed:N0} refs boxed (meshes measured {geo.MeshesMeasured:N0}, OBND fallback {geo.ObndFallback:N0}, no geometry {geo.NoGeometry:N0}; markers set aside {ev.BoxedMarkers:N0}, effect volumes kept out of the overlap role {ev.BoxedFxVolumes:N0})  ({sw.Elapsed.TotalSeconds:0.0}s)");
// The placed lights, activators and doors whose base record has no model (ContactIndex.IsBodiless): no body, so never a
// dependent or host; counted by type, not judged.
var (bodilessLights, bodilessActivators, bodilessDoors) = cidx.BodilessReferences();
Console.WriteLine($"   {bodilessLights:N0} placed lights, {bodilessActivators:N0} activators and {bodilessDoors:N0} doors with no model: no body, not judged");
sw.Restart();
var tally = new StateTally();
var rows = new List<StateRow>();
var perState = new List<(StateArm Arm, int Changes, int Rows, int Conditional, double Ms)>();
int done = 0;
phase.Mark("evaluator-boxing");
// What keeps an arm's answers from being its own: a shared memo a later arm reads first decides a cycle's answer, or an arm named as
// the baseline shares the baseline's field key. Either keeps the loop sequential, said once.
int stateThreads = threads;
if (threads > 1 && routeOn && ParallelArms.ChainCycleOrderMatters(idx.EnableChanges.Keys, cidx.Arms.WinnerRawFacts) is { } orderCycle)
{
    stateThreads = 1;
    Console.WriteLine($"   state loop stays sequential: the answers of the enable-chain cycle through {orderCycle} depend on which reference is asked first");
}
else if (threads > 1 && routeOn && arms.Any(a => !a.IsBaseline && a.Name == EnablePopulation.BaselineStateName))
{
    stateThreads = 1;
    Console.WriteLine($"   state loop stays sequential: a flipped state is named '{EnablePopulation.BaselineStateName}', which its fields share with the load-time state");
}
ParallelArms.Run(arms, stateThreads, arm =>
{
    var t = new StateTally();
    var asks = new List<(FormKey Cell, string State)>();
    fieldAsks.Value = asks;
    var r = ev.Evaluate(arm, IsVanilla, IsUssep, t);
    fieldAsks.Value = null;
    string? line = phaseTimersPath is null ? null : PhaseClock.StateLine("state", arm.Name, t.ElapsedMs, t.Changes, r.Count, PhaseClock.Digest(r.Select(x => x.ToString())), PhaseClock.TallyDigest(t));
    return (Tally: t, Rows: r, Asks: asks, Line: line);
}, (i, res) =>
{
    var (t, r, asks, line) = res;
    if (line is not null) stateLines.Add(line);
    foreach (var ask in asks) if (fieldSeen.Add(ask)) fieldOrder.Add(ask);
    rows.AddRange(r);
    perState.Add((arms[i], t.Changes, r.Count, r.Count(x => x.Verdict == StateVerdict.StateConditional && !x.SameSet), t.ElapsedMs));
    Merge(tally, t);
    if (++done % 250 == 0) Console.WriteLine($"   ... {done:N0}/{arms.Count:N0} states, {rows.Count:N0} rows, {sw.Elapsed.TotalSeconds:0}s");
});
static void Merge(StateTally a, StateTally b)
{
    a.States += b.States; a.Changes += b.Changes; a.Arrivals += b.Arrivals; a.Departures += b.Departures; a.SwitchesThemselves += b.SwitchesThemselves;
    a.NoGeometry += b.NoGeometry; a.NoBox += b.NoBox; a.NoBody += b.NoBody; a.NoBodyActivator += b.NoBodyActivator; a.NoBodyDoor += b.NoBodyDoor; a.EditorMarker += b.EditorMarker; a.MarkerPrimitive += b.MarkerPrimitive; a.MarkerPrimitiveSolid += b.MarkerPrimitiveSolid; a.MarkerPrimitiveNotSolid += b.MarkerPrimitiveNotSolid; a.Decal += b.Decal; a.PlacedTrap += b.PlacedTrap; a.Actor += b.Actor; a.EffectVolume += b.EffectVolume; a.TypeOutsideRoles += b.TypeOutsideRoles; a.OverSizeCap += b.OverSizeCap; a.NotObstructing += b.NotObstructing; a.Volume += b.Volume; a.PairsTested += b.PairsTested; a.SameSetPairs += b.SameSetPairs; a.Unchanged += b.Unchanged; a.StateOverlapUnmeasured += b.StateOverlapUnmeasured;
    a.HostsLeaving += b.HostsLeaving; a.HostsArriving += b.HostsArriving; a.DepsArriving += b.DepsArriving; a.DepsLeaving += b.DepsLeaving; a.ReSupported += b.ReSupported; a.FloatingOnArrival += b.FloatingOnArrival; a.TouchingOnArrival += b.TouchingOnArrival; a.ContactUnmeasured += b.ContactUnmeasured;
    a.RouteAsked += b.RouteAsked; a.RouteUnjudged += b.RouteUnjudged; a.RouteNoLine += b.RouteNoLine; a.RouteClipped += b.RouteClipped; a.RouteNone += b.RouteNone; a.ElapsedMs += b.ElapsedMs;
    a.OutsidePopulation += b.OutsidePopulation; a.MergedContactRows += b.MergedContactRows;
}
double totalS = sw.Elapsed.TotalSeconds;
cidx.MergeThreadTimers();
phase.Mark("state-loop");
var cellStates = perState.Sum(p => arms.First(a => a == p.Arm).Changes(sx).Select(c => idx.History.Winning(c.Key)?.Cell ?? FormKey.Null).Distinct().Count());
Console.WriteLine($"\n==== COST ====");
Console.WriteLine($"   {arms.Count:N0} states over {tally.Changes:N0} changed references ({tally.Arrivals:N0} arrivals, {tally.Departures:N0} departures) in {totalS:0.0}s — {(arms.Count == 0 ? 0 : 1000 * totalS / arms.Count):0} ms per state; "
                + $"{cellStates:N0} cell-states, {(cellStates == 0 ? 0 : 1000 * totalS / cellStates):0} ms per cell-state; the route half measured {routeMeasured:N0} obstacles");
if (routeFloorLine is not null) Console.WriteLine("   " + routeFloorLine());
var keptByRule = keyRule.Values.Where(v => v is not null).GroupBy(v => v!).ToDictionary(g => g.Key, g => (long)g.Count());
long keysDropped = keyRule.Values.LongCount(v => v is null);
if (population is not null)
    Console.WriteLine($"   population: {tally.OutsidePopulation:N0} changed reference-states not processed; distinct changed references kept by rule "
                    + string.Join(", ", new[] { "contact", "route-interior", "route-ring", "large-ref" }.Select(k => $"{k} {keptByRule.GetValueOrDefault(k):N0}"))
                    + $", dropped {keysDropped:N0}");
Console.WriteLine($"   set aside (rule 6): {tally.NoBox:N0} changed refs without a box (no geometry or a navcut without a primitive); {tally.MarkerPrimitive:N0} changed markers carrying a primitive (unjudged); {tally.EditorMarker:N0} changed editor markers; {tally.NoBody:N0} changed lights, {tally.NoBodyActivator:N0} activators and {tally.NoBodyDoor:N0} doors with no model (no body); route: {routeNoNav:N0} in a cell without navmesh, {routeNoGeo:N0} without actor-solid geometry, {routeSetAside:N0} doors / movables, {routeNoCross:N0} never entering an actor column, {tally.RouteUnjudged:N0} unjudged");
Console.WriteLine($"   changed references without a box, by cause: {tally.Actor:N0} placed actors, {tally.PlacedTrap:N0} placed hazards or traps (base not read), {tally.EffectVolume:N0} effects meshes in no role, {tally.TypeOutsideRoles:N0} of a type in no role, {tally.OverSizeCap:N0} over the volume cap in no role (unjudged); {tally.Decal:N0} decals (set aside); {tally.NoBox:N0} with no geometry (NoBox)");
Console.WriteLine(primitiveTable is null
    ? $"   marker primitives: no --primitive-solidity table given, so all {tally.MarkerPrimitive:N0} stay unjudged (MarkerPrimitive)"
    : $"   marker primitives by station Q's table ({primitiveTable.Count} entries): {tally.MarkerPrimitiveNotSolid:N0} measured not solid (set aside), {tally.MarkerPrimitiveSolid:N0} measured solid (unjudged: the check does not measure primitive boxes), {tally.MarkerPrimitive:N0} not in the measurement (unjudged)");
Console.WriteLine($"   state overlap: unmeasured for {tally.StateOverlapUnmeasured:N0} changed obstructing reference-state(s); strict placed render-surface migration is pending, so no box overlap is a finding or repair predicate");

// ---- LEFT IN EMPTY SPACE per state (--empty-space): each state's arriving fixtures against every plugin that stepped on a
// floor or wall near them; the ContactIndex arms carry the state, and the state is cleared again before anything else reads them.
var esRows = new List<EmptySpaceRow>();
var esTrigger = new Dictionary<EmptySpaceRow, string>(ReferenceEqualityComparer.Instance);
long esAbsent = 0, esNoRelation = 0, esFalls = 0, esPairs = 0;
long esOutside = 0, esJudgedDropped = 0;
EmptySpaceSearch? es = null;
EmptySpaceScope? esScope = null;
if (emptySpaceOut is not null)
{
    sw.Restart();
    var closure = DetectorModel.MasterClosure(masterIdx);
    var nodeCheck = EmptySpaceOutput.NodeCheck(idx, assets);
    Func<FormKey, string, bool?> nodeCheckOneAtATime = (b, n) => { lock (StateGeometry.MeshGate) return nodeCheck(b, n); };
    es = new EmptySpaceSearch(cidx, closure, PluginName, nodeCheckOneAtATime, f => PluginKinds.IsBethesda(f), p => IsVanilla(p) || IsUssep(p));
    if (population is not null)
    {
        esScope = new EmptySpaceScope(idx, population.InteriorClosure(idx, es.RackPartners));
        Console.WriteLine($"   empty space population scope: {esScope.Cells.Count:N0} interior cells");
    }
    int esDone = 0;
    var esSearch = es; var esArea = esScope;
    if (threads > 1) esSearch.PrepareStateViews();
    ParallelArms.Run(arms, threads, arm =>
    {
        long esT0 = Stopwatch.GetTimestamp();
        // Each state's memos start empty either way, so a state's own view reads what the shared pass reads.
        int o1, o2;
        var judgedHere = threads > 1 ? esSearch.JudgeArrivalsAlone(arm, sx, esArea, out o1, out o2) : esSearch.JudgeArrivals(arm, sx, esArea, out o1, out o2);
        string? line = phaseTimersPath is null ? null : PhaseClock.StateLine("empty-space", arm.Name, Stopwatch.GetElapsedTime(esT0).TotalMilliseconds, 0, judgedHere.Count,
            PhaseClock.Digest(judgedHere.Select(x => x.Item1.ToString() + "|" + x.Item2)), "");
        return (Judged: judgedHere, O1: o1, O2: o2, Line: line);
    }, (_, res) =>
    {
        var (judgedHere, o1, o2, line) = res;
        if (++esDone % 250 == 0) Console.WriteLine($"   ... empty space: {esDone:N0}/{arms.Count:N0} states, {esRows.Count:N0} rows, {sw.Elapsed.TotalSeconds:0}s");
        if (line is not null) stateLines.Add(line);
        esOutside += o1; esJudgedDropped += o2;
        foreach (var (r, sk) in judgedHere)
        {
            esPairs++;
            if (r.Verdict == EmptySpaceVerdict.Absent) { esAbsent++; continue; }
            if (r.Verdict == EmptySpaceVerdict.NoRelation) { esNoRelation++; continue; }
            if (r.Verdict == EmptySpaceVerdict.Falls) { esFalls++; continue; }
            esRows.Add(r); esTrigger[r] = map?.TriggerOf(sk).Status ?? "(no map)";
        }
    });
    cidx.MergeThreadTimers();
    phase.Mark("empty-space");
    var esCls = esRows.Where(r => r.Verdict == EmptySpaceVerdict.LeftInEmptySpace).ToList();
    Console.WriteLine($"\n==== EMPTY SPACE per state - {esPairs:N0} arriving-fixture x mover pairs (absent {esAbsent:N0}, no relation {esNoRelation:N0}, falls {esFalls:N0}; counted, not listed) in {sw.Elapsed.TotalSeconds:0.0}s; touch tests {es.TouchTests:N0} ====");
    foreach (var g in esRows.GroupBy(r => r.Verdict).OrderBy(g => g.Key))
        Console.WriteLine($"      {g.Key,-20} {g.Count(),8:N0}   distinct fixtures {g.Select(r => r.Fixture).Distinct().Count(),7:N0}   states {g.Select(r => r.State).Distinct().Count(),6:N0}");
    foreach (var r in esCls.OrderBy(r => r.State).ThenBy(r => r.Fixture.ToString()).Take(listN))
        Console.WriteLine($"   @ {r.State}: {RefLabel(r.Fixture)} ({r.HeldBy}) in {CellOf(r.Cell)}: {PluginName(r.Mover)} removed or moved {string.Join(" ", r.Authoring.ChangedAnchors.Take(3))}; assembly {r.Winner.Members}");
    var dirE = Path.GetDirectoryName(Path.GetFullPath(emptySpaceOut));
    if (!string.IsNullOrEmpty(dirE)) Directory.CreateDirectory(dirE);
    var esOpts = new JsonSerializerOptions { WriteIndented = false, NumberHandling = System.Text.Json.Serialization.JsonNumberHandling.AllowNamedFloatingPointLiterals };
    string esText = JsonSerializer.Serialize(new
    {
        Schema = "housecarl.empty-space/1", States = arms.Count,
        Parameters = new { Epsilon = cidx.Epsilon, es.MinAnchorDim, es.MaxMembers, es.AnchorReport, Reach = es.Reach },
        Counts = esRows.GroupBy(r => r.Verdict.ToString()).ToDictionary(g => g.Key, g => g.Count()),
        Pairs = esPairs, Absent = esAbsent, NoRelation = esNoRelation, Falls = esFalls,
        Rows = esRows.Select(r => EmptySpaceOutput.Row(r, PluginName, BaseLabel, CellOf)).ToList(),
    }, esOpts);
    if (population is not null)
    {
        // a scoped side file names its population (an unscoped one keeps its shape byte for byte)
        var node = System.Text.Json.Nodes.JsonNode.Parse(esText)!.AsObject();
        node["Population"] = population.Identity("load-time");
        esText = node.ToJsonString(esOpts);
    }
    File.WriteAllText(emptySpaceOut, esText);
    if (esScope is not null)
        Console.WriteLine($"   empty space population: {esOutside:N0} arrivals outside it not judged; {esJudgedDropped:N0} pairs judged only to keep the exterior memos exact, not reported");
    Console.WriteLine($"wrote {esRows.Count:N0} empty-space rows -> {emptySpaceOut}");
}

var split = StateFindings.Of(rows);   // stateprobe/StateFindings.cs
var cls = split.Class;
var resolved = split.Resolved;
var ownFloor = split.OwnFloor;
string TriggerOf(FormKey k) => map?.TriggerOf(k).Status ?? "(no map)";
Console.WriteLine($"\n==== THE CLASS — StateConditional rows: {cls.Count:N0} (apart: {rows.Count(r => r.Verdict == StateVerdict.StateConditional && r.SameSet):N0} same-set pairs, {ownFloor.Count:N0} same-author — the set against its own author's geometry); StateResolved: {resolved.Count:N0} ====");
Console.WriteLine($"   by the switch's trigger (switchprobe's map): " + string.Join(", ", cls.GroupBy(r => TriggerOf(r.Switch)).OrderByDescending(g => g.Count()).Select(g => $"{g.Key} {g.Count():N0} rows / {g.Select(r => r.Switch).Distinct().Count():N0} switches"))
                + "   (an Unknown switch's state is evaluated all the same; a baseline-off marker with no trigger and a 'disable' name is the private permanently-disabled idiom — read those rows as such)");
Console.WriteLine($"   {"population",-26} {"overlap",8} {"contact",8} {"route",6} {"| resolved",11}   {"switches",8}");
foreach (var pop in Enum.GetValues<StatePopulation>())
    Console.WriteLine($"   {pop,-26} {cls.Count(r => r.Population == pop && r.Kind == StateRowKind.Overlap),8:N0} {cls.Count(r => r.Population == pop && r.Kind == StateRowKind.Contact),8:N0} {cls.Count(r => r.Population == pop && r.Kind == StateRowKind.Route),6:N0} {resolved.Count(r => r.Population == pop),11:N0}   {cls.Where(r => r.Population == pop).Select(r => r.Switch).Distinct().Count(),8:N0}");
Console.WriteLine($"   by switch owner: " + string.Join(", ", cls.GroupBy(r => ClassOf(r.SwitchOwner)).OrderByDescending(g => g.Count()).Select(g => $"{g.Key} {g.Count():N0}")));
Console.WriteLine($"   same-author rows (the mod's own set against its own geometry, not the class): " + string.Join(", ", ownFloor.GroupBy(r => PluginName(r.MemberPlacer)).OrderByDescending(g => g.Count()).Take(6).Select(g => $"{Short(g.Key, 40)} {g.Count():N0}")));
Console.WriteLine($"   contact verdicts: " + string.Join(", ", cls.Where(r => r.Kind == StateRowKind.Contact).GroupBy(r => $"{r.ContactVerdict} {(r.Arrives ? "(member arrives)" : "(host leaves)")}").OrderByDescending(g => g.Count()).Select(g => $"{g.Key} {g.Count():N0}")));
Console.WriteLine($"   route verdicts: " + string.Join(", ", cls.Where(r => r.Kind == StateRowKind.Route).GroupBy(r => $"{r.Route}{(r.Navcut ? " navcut" : "")}").OrderByDescending(g => g.Count()).Select(g => $"{g.Key} {g.Count():N0}"))
                + $"; counted: {tally.RouteAsked:N0} asked, NoLine {tally.RouteNoLine:N0}, Clipped {tally.RouteClipped:N0}, None {tally.RouteNone:N0}");
Console.WriteLine("   contact geometry: the CORE collision surface (no Havok radius) — contactprobe reads the radius for its gap by default since 2026-10-03; this probe does not yet (follow-up)");
Console.WriteLine($"   contact counted: hosts leaving {tally.HostsLeaving:N0}, arriving {tally.HostsArriving:N0}; dependents arriving {tally.DepsArriving:N0} (touching {tally.TouchingOnArrival:N0}, floating {tally.FloatingOnArrival:N0}), re-supported {tally.ReSupported:N0}, unmeasured {tally.ContactUnmeasured:N0}; {tally.MergedContactRows:N0} rows read one pair a second way, merged");
Console.WriteLine($"   by trigger kind of the switch: (see switchprobe's map; a state is evaluated whatever its trigger)");

void PrintRow(StateRow r)
{
    var s = sx.Switches[r.Switch];
    string other = r.Other is { } o ? RefLabel(o) + $" (placed by {PluginName(r.OtherPlacer)}{(r.OtherResponsible != r.OtherPlacer ? $", geometry by {PluginName(r.OtherResponsible)}" : "")})" : "-";
    string detail = r.Kind switch
    {
        StateRowKind.Overlap => $"boxes {r.Depth:0}u deep, {r.Frac:0.00} of the smaller",
        StateRowKind.Contact => $"{r.ContactVerdict} {r.Relation} {r.Motion}; nearest {r.Distance:0.#}u, penetration {r.Penetration:0.#}u",
        _ => $"{r.Route}{(r.Navcut ? " (navcut box)" : "")}",
    };
    Console.WriteLine($"      {r.Kind,-8} {r.Verdict,-16} {r.Population,-24} switch {s.Label} ({ClassOf(r.SwitchOwner)}, baseline {(s.Baseline.Enabled ? "on" : "off")}, trigger {TriggerOf(r.Switch)}) in {CellOf(r.Cell)}");
    Console.WriteLine($"         member {(r.Arrives ? "ARRIVES" : "LEAVES ")} {RefLabel(r.Member)} (placed by {PluginName(r.MemberPlacer)}{(r.MemberResponsible != r.MemberPlacer ? $", geometry by {PluginName(r.MemberResponsible)}" : "")}, attached by {PluginName(r.MemberAttacher)})");
    Console.WriteLine($"         other  {other}   {detail}   {r.Why}");
}
Console.WriteLine($"\n==== THE FLOOR — Bethesda-on-Bethesda StateConditional rows: {cls.Count(r => r.Population == StatePopulation.BethesdaFloor):N0} (the same-defect control is chosen from these) ====");
foreach (var r in cls.Where(r => r.Population == StatePopulation.BethesdaFloor).OrderByDescending(r => r.Kind == StateRowKind.Overlap ? r.Depth * r.Frac : r.Kind == StateRowKind.Contact ? r.Penetration + r.Distance : 1000).Take(Math.Min(listN, 12))) PrintRow(r);
Console.WriteLine($"\n==== MODS — StateConditional rows with a mod on either side: {cls.Count(r => r.Population != StatePopulation.BethesdaFloor):N0} ====");
Console.WriteLine($"   by (switch owner, member's responsible plugin, other's responsible plugin) — responsible = the later of the last placement step and the last base change:");
foreach (var g in cls.Where(r => r.Population != StatePopulation.BethesdaFloor).GroupBy(r => (PluginName(r.SwitchOwner), PluginName(r.MemberResponsible), r.Other is null ? "(navmesh)" : PluginName(r.OtherResponsible))).OrderByDescending(g => g.Count()).Take(listN))
    Console.WriteLine($"   {g.Count(),6:N0}  switch by {Short(g.Key.Item1, 36),-36} member by {Short(g.Key.Item2, 36),-36} other by {Short(g.Key.Item3, 36)}   [{string.Join(", ", g.GroupBy(r => r.Kind).Select(k => $"{k.Count()} {k.Key}"))}]");
foreach (var kind in new[] { StateRowKind.Overlap, StateRowKind.Contact, StateRowKind.Route })
{
    var kr = cls.Where(r => r.Population != StatePopulation.BethesdaFloor && r.Kind == kind).ToList();
    if (kr.Count == 0) continue;
    Console.WriteLine($"\n   ---- {kind}: top {Math.Min(listN, kr.Count)} of {kr.Count:N0} ----");
    foreach (var r in kr.OrderByDescending(r => kind == StateRowKind.Overlap ? r.Depth * r.Frac : kind == StateRowKind.Contact ? r.Penetration + r.Distance : r.Route == RouteCutVerdict.Blocked ? 2 : 1).Take(listN)) PrintRow(r);
}
Console.WriteLine($"\n==== StateResolved — reported, not a finding: {resolved.Count:N0} ====");
foreach (var r in resolved.OrderByDescending(r => r.Population != StatePopulation.BethesdaFloor).ThenByDescending(r => r.Depth).Take(Math.Min(listN, 10))) PrintRow(r);
if (perState.Count > 1)
{
    Console.WriteLine($"\n==== PER STATE — top {Math.Min(listN, perState.Count)} by StateConditional rows ====");
    foreach (var p in perState.OrderByDescending(p => p.Conditional).Take(listN))
        Console.WriteLine($"   {p.Conditional,6:N0} rows  {p.Rows,6:N0} total  {p.Changes,6:N0} changes  {p.Ms,7:0} ms  {p.Arm.Name}");
}

if (jsonPath is not null)
{
    var dir = Path.GetDirectoryName(Path.GetFullPath(jsonPath));
    if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
    var dump = new
    {
        generated = DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm:ssZ"), instance = Instance, profile = profileName, plugins = paths.Count,
        measurementPolicy = StateMeasurementPolicy,
        states = arms.Count, elapsedSeconds = totalS, cellStates, routeMeasured, bodilessLights, bodilessActivators, bodilessDoors,
        tally = new { tally.Changes, tally.Arrivals, tally.Departures, tally.NoBox, tally.NoBody, tally.NoBodyActivator, tally.NoBodyDoor, tally.EditorMarker, tally.MarkerPrimitive, tally.MarkerPrimitiveSolid, tally.MarkerPrimitiveNotSolid, tally.Decal, tally.PlacedTrap, tally.Actor, tally.EffectVolume, tally.TypeOutsideRoles, tally.OverSizeCap, tally.PairsTested, tally.SameSetPairs, tally.StateOverlapUnmeasured, tally.HostsLeaving, tally.HostsArriving, tally.DepsArriving, tally.ReSupported, tally.FloatingOnArrival, tally.TouchingOnArrival, tally.ContactUnmeasured, tally.MergedContactRows, tally.RouteAsked, tally.RouteUnjudged, tally.RouteNoLine, tally.RouteClipped, tally.RouteNone, routeNoNav, routeNoGeo, routeSetAside, routeNoCross },
        perState = perState.Select(p => new { state = p.Arm.Name, flips = p.Arm.Flips.Select(f => f.ToString()), p.Changes, p.Rows, p.Conditional, ms = p.Ms }),
        rows = rows.Select(r => new
        {
            state = r.State, @switch = r.Switch.ToString(), switchLabel = sx.Switches[r.Switch].Label, switchOwner = PluginName(r.SwitchOwner), switchBaselineEnabled = sx.Switches[r.Switch].Baseline.Enabled,
            kind = r.Kind.ToString(), verdict = r.Verdict.ToString(), r.Why, population = r.Population.ToString(), sameSet = r.SameSet, sameAuthor = r.SameAuthor, trigger = TriggerOf(r.Switch),
            member = r.Member.ToString(), memberEdid = idx.RefEditorId.GetValueOrDefault(r.Member), memberBase = BaseLabel(r.MemberBase), arrives = r.Arrives, memberPlacer = PluginName(r.MemberPlacer), memberResponsible = PluginName(r.MemberResponsible), memberAttacher = PluginName(r.MemberAttacher),
            other = r.Other?.ToString(), otherEdid = r.Other is { } o ? idx.RefEditorId.GetValueOrDefault(o) : null, otherBase = r.Other is null ? null : BaseLabel(r.OtherBase), otherPlacer = r.Other is null ? null : PluginName(r.OtherPlacer), otherResponsible = r.Other is null ? null : PluginName(r.OtherResponsible),
            cell = CellOf(r.Cell), cellKey = r.Cell.ToString(), interior = r.Interior,
            depth = float.IsNaN(r.Depth) ? (float?)null : r.Depth, frac = float.IsNaN(r.Frac) ? (float?)null : r.Frac,
            contact = r.ContactVerdict?.ToString(), relation = r.Relation.ToString(), distance = float.IsNaN(r.Distance) ? (float?)null : r.Distance, penetration = float.IsNaN(r.Penetration) ? (float?)null : r.Penetration, motion = r.Motion.ToString(),
            route = r.Route?.ToString(), navcut = r.Navcut, physical = r.Physical.ToString(), physicalDetail = r.PhysicalDetail,
            memberPosition = idx.History.Winning(r.Member)?.Pos.ToString(), otherPosition = r.Other is { } o2 ? idx.History.Winning(o2)?.Pos.ToString() : null,
        }),
    };
    File.WriteAllText(jsonPath, JsonSerializer.Serialize(dump, new JsonSerializerOptions { WriteIndented = false }));
    Console.WriteLine($"\nwrote {rows.Count:N0} rows -> {jsonPath}");
}
if (emitPath is not null)
{
    // The class only — StateConditional, the Bethesda floor withheld, same-set pairs withheld. The member is the necessary
    // record (a patch re-parents, moves or removes it); the other side the alternative. The kind carries the state so a
    // row in one state never collides with the same pair in another (ConflictIo.RowIdentity keys on kind + records).
    var conflicts = new List<Conflict>();
    foreach (var r in StateFindings.Emitted(cls))
        conflicts.Add(new Conflict($"STATE-{r.Kind.ToString().ToUpperInvariant()}@{r.State}@{TriggerOf(r.Switch)}", PluginName(r.MemberResponsible), r.Other is null ? PluginName(r.SwitchOwner) : PluginName(r.OtherResponsible),
            $"{r.Kind} {r.Verdict} under {sx.Switches[r.Switch].Label} ({PluginName(r.SwitchOwner)}): {RefLabel(r.Member)} {(r.Arrives ? "arrives" : "leaves")} — {r.Why} in {CellOf(r.Cell)}",
            new List<ConflictRecord> { new(r.Member.ToString(), "placed-ref", CellOf(r.Cell), r.Physical, r.PhysicalDetail) },
            r.Other is { } o ? new List<ConflictRecord> { new(o.ToString(), "placed-ref", null) } : null));
    foreach (var r in esRows.Where(r => r.Verdict == EmptySpaceVerdict.LeftInEmptySpace))
        conflicts.Add(EmptySpaceOutput.ToConflict(r, PluginName, BaseLabel, CellOf, state: true) with { Kind = $"{EmptySpaceOutput.StateKind}@{r.State}@{esTrigger[r]}" });
    var coverage = new List<CoverageLine>
    {
        new("plugins", paths.Count - unreadable.Count, new Dictionary<string, long> { ["UnreadablePlugin"] = unreadable.Count }),
        new("states", arms.Count, null, null, $"{tally.Changes:N0} changed references"),
        new("changed references with a box", tally.Changes - tally.NoBox - tally.MarkerPrimitive - tally.MarkerPrimitiveSolid - tally.MarkerPrimitiveNotSolid
                                                  - tally.NoBody - tally.NoBodyActivator - tally.NoBodyDoor - tally.EditorMarker
                                                  - tally.Decal - tally.PlacedTrap - tally.Actor - tally.EffectVolume - tally.TypeOutsideRoles - tally.OverSizeCap,
            new Dictionary<string, long> { ["NoBox"] = tally.NoBox, ["MarkerPrimitive"] = tally.MarkerPrimitive, ["MarkerPrimitiveSolid"] = tally.MarkerPrimitiveSolid,
                                           ["Actor"] = tally.Actor, ["PlacedTrap"] = tally.PlacedTrap, ["EffectVolume"] = tally.EffectVolume, ["TypeOutsideRoles"] = tally.TypeOutsideRoles,
                                           ["OverSizeCap"] = tally.OverSizeCap },
            new Dictionary<string, long> { ["LightWithNoModel"] = tally.NoBody, ["ActivatorWithNoModel"] = tally.NoBodyActivator,
                                           ["DoorWithNoModel"] = tally.NoBodyDoor, ["EditorMarker"] = tally.EditorMarker,
                                           ["MarkerPrimitiveNotSolid"] = tally.MarkerPrimitiveNotSolid, ["Decal"] = tally.Decal }),
        new("state-overlap changed obstructing references", 0,
            new Dictionary<string, long> { ["ActualSurfacePredicateUnavailable"] = tally.StateOverlapUnmeasured }, null,
            "STATE-OVERLAP is withheld: this evaluator does not yet carry strict placed render surfaces; diagnostic boxes provide no finding or repair credit"),
        new("route obstacles", routeMeasured, new Dictionary<string, long> { ["NoNavmesh"] = routeNoNav, ["NoGeometry"] = routeNoGeo, ["Unjudged"] = tally.RouteUnjudged }, new Dictionary<string, long> { ["DoorOrMovable"] = routeSetAside, ["NeverEntersColumn"] = routeNoCross }),
        new("contact pairs", tally.HostsLeaving + tally.HostsArriving + tally.DepsArriving, new Dictionary<string, long> { ["Unmeasured"] = tally.ContactUnmeasured }, new Dictionary<string, long> { ["ReSupported"] = tally.ReSupported, ["TouchingOnArrival"] = tally.TouchingOnArrival, ["FloatingOnArrival"] = tally.FloatingOnArrival }),
    };
    if (es is not null)
        coverage.Add(new("empty-space arriving-fixture x mover pairs", esPairs - esAbsent,
            esRows.Where(r => r.Verdict == EmptySpaceVerdict.Refused).GroupBy(r => r.Why.StartsWith("exterior") ? "Exterior" : "AssemblyOverCap").ToDictionary(g => g.Key, g => (long)g.Count()),
            new Dictionary<string, long> { ["AbsentInArm"] = esAbsent, ["Falls"] = esFalls, ["NoRelation"] = esNoRelation,
                                           ["FloatsAsShipped"] = esRows.Count(r => r.Verdict == EmptySpaceVerdict.FloatsAsShipped), ["FloorPopulation"] = esRows.Count(r => r.Verdict == EmptySpaceVerdict.FloorPopulation) },
            $"class {esRows.Count(r => r.Verdict == EmptySpaceVerdict.LeftInEmptySpace):N0}"));
    string populationIdentity = population?.Identity("load-time") ?? "load-time";
    if (population is not null)
        coverage.Add(new("population scope", keyRule.Count - keysDropped, new Dictionary<string, long>(),
            new Dictionary<string, long> { ["ChangedReferenceOutsidePopulation"] = keysDropped, ["ReferenceStateOutsidePopulation"] = tally.OutsidePopulation,
                                           ["EmptySpaceArrivalOutsidePopulation"] = esOutside, ["EmptySpaceJudgedForExteriorMemo"] = esJudgedDropped },
            "changed references processed only where the plan's population reaches them (" + string.Join(", ", keptByRule.OrderBy(k => k.Key, StringComparer.Ordinal).Select(k => $"{k.Key} {k.Value:N0}"))
            + $"); {population.Describe()}"));
    // Every row of this emit is a DIFFERENTIAL between the baseline and one flipped state, so the emit's own state is
    // the baseline it differs from; the state each row belongs to is in the row's own kind (STATE-<kind>@<state>@<trigger>).
    ConflictIo.Write(emitPath, new ConflictFile("stateprobe", Instance, DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm:ssZ"), conflicts, coverage,
                                                Population: populationIdentity, State: EnablePopulation.BaselineStateName,
                                                Configuration: cfg.Identity(populationIdentity, EnablePopulation.BaselineStateName),
                                                MeasurementPolicy: StateMeasurementPolicy));
    Console.WriteLine($"emitted {conflicts.Count:N0} StateConditional conflicts -> {emitPath}");
}

if (population is not null && populationAuditEmit is not null)
    EmptySpaceAudit.Write(populationAuditEmit, populationAuditOut!, "stateprobe", population.Identity("load-time"),
        (kind, k) => kind.StartsWith(EmptySpaceOutput.StateKind, StringComparison.Ordinal) ? (esScope?.InScope(k) == true ? "empty-space" : null) : RuleOf(k));

Console.WriteLine();
Console.WriteLine("A StateConditional row exists only once its switch is flipped — a quest stage, a scene or a trigger the records name, or one they do not (Unknown");
Console.WriteLine("triggers are flipped all the same). Records only; contact/route geometry, not the game. STATE-OVERLAP is withheld until strict placed render surfaces are wired. Part D flips one marker by console and reads the sets back.");
Console.WriteLine($"elapsed {sw.Elapsed.TotalSeconds:0.0}s");
if (phaseTimersPath is not null)
{
    phase.Mark("listing+emit");
    phase.Report(__run, stateTimer!, phaseTimersPath, stateLines, cidx.Timer, (cidx.MeasureMemoCount, cidx.MeasureMemoApproxBytes));
}
return 0;

static string Short(string s, int n) => s.Length <= n ? s : s[..(n - 3)] + "...";

/// <summary>stateprobe --phase-timers (lane O P0): wall seconds per top-level phase, the state evaluator's own phase split, and one
/// JSON line per state (its milliseconds, changes, row count and a digest of its rows and tally) so two arms' states can be
/// compared offline. Off: no marks are kept. Changes no decision and nothing in --emit, --json or --empty-space.</summary>
sealed class PhaseClock(bool on)
{
    readonly Stopwatch _sw = Stopwatch.StartNew();
    double _last;
    readonly List<(string Name, double Seconds)> _marks = new();
    public void Mark(string name)
    {
        if (!on) return;
        double now = _sw.Elapsed.TotalSeconds; _marks.Add((name, now - _last)); _last = now;
    }
    public static string Digest(IEnumerable<string> items)
    {
        using var h = System.Security.Cryptography.SHA256.Create();
        foreach (var it in items) { var b = System.Text.Encoding.UTF8.GetBytes(it + "\n"); h.TransformBlock(b, 0, b.Length, null, 0); }
        h.TransformFinalBlock(Array.Empty<byte>(), 0, 0);
        return Convert.ToHexString(h.Hash!).ToLowerInvariant();
    }
    public static string TallyDigest(StateTally t)
        => Digest(typeof(StateTally).GetFields().Where(f => f.Name != nameof(StateTally.ElapsedMs)).OrderBy(f => f.Name, StringComparer.Ordinal)
                  .Select(f => $"{f.Name}={f.GetValue(t)}"));
    public static string StateLine(string pass, string state, double ms, int changes, int rows, string rowsDigest, string tallyDigest)
        => JsonSerializer.Serialize(new { pass, state, ms = Math.Round(ms, 3), changes, rows, rowsDigest, tallyDigest });
    public void Report(Run run, StatePhaseTimer t, string path, List<string> stateLines, ContactTimer? contact = null, (int Count, long Bytes) memo = default)
    {
        Console.WriteLine("\n==== PHASE TIMERS (--phase-timers) ====");
        foreach (var (n, s) in _marks) { Console.WriteLine($"   phase {n,-22} {s,9:0.0}s"); run.Feature($"phase.{n}.seconds", s, "seconds", FeatureAt.Final); }
        var inner = new (string, long)[] { ("setup", t.Setup), ("route", t.Route), ("host-leaving", t.HostLeaving), ("host-arriving", t.HostArriving), ("dependent-arriving", t.DependentArriving), ("other", t.Other) };
        foreach (var (n, ticks) in inner) { var s = StatePhaseTimer.Seconds(ticks); Console.WriteLine($"   state-loop {n,-19} {s,9:0.0}s"); run.Feature($"state-loop.{n}.seconds", s, "seconds", FeatureAt.Final); }
        Console.WriteLine($"   route calls {t.RouteCalls:N0}, route memo hits {t.RouteMemoHits:N0}");
        run.Feature("state-loop.route.calls", t.RouteCalls, "calls", FeatureAt.Final).Feature("state-loop.route.memo-hits", t.RouteMemoHits, "calls", FeatureAt.Final);
        var dir = Path.GetDirectoryName(Path.GetFullPath(path));
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        File.WriteAllLines(path, stateLines);
        Console.WriteLine($"   wrote {stateLines.Count:N0} per-state lines -> {path}");
        if (contact is not null)
        {
            Console.WriteLine("\n==== CONTACT TIMERS (--phase-timers) ====");
            foreach (var ln in contact.Lines()) Console.WriteLine(ln);
            Console.WriteLine($"   measure memo: {memo.Count:N0} entries, about {memo.Bytes / 1e6:0.0} MB");
            run.Feature("contact.memo.entries", memo.Count, "count", FeatureAt.Final).Feature("contact.memo.bytes", memo.Bytes, "bytes", FeatureAt.Final);
            foreach (var (n, v, u) in contact.Features()) run.Feature(n, v, u, FeatureAt.Final);
            File.WriteAllText(path + ".contact.json", contact.ToJson());
            Console.WriteLine($"   wrote the contact timer table -> {path}.contact.json");
        }
    }
}
