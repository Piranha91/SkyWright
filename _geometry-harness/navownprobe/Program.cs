using System.Diagnostics;
using HarnessShared;
using HousecarlCore;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Skyrim;
using HousecarlCore.Progress;

// NAVMESH OWNERSHIP — who actually supplies the navmesh the game will use, and whose work got
// thrown away getting there.
//
// This is Tier 0 of the triage from Piranha's Whiterun hypothetical, and it deliberately contains
// NO geometry. Milestone 4 spent two sessions learning that "is this object standing on walkable
// navmesh" has no answer worth reading. "Which plugin's navmesh wins, and did that silently revert
// another mod's navmesh work" has an exact answer, straight out of the records, in seconds.
//
// Three questions, three failure classes:
//
//   CLOBBERED  Mod A re-cuts a vanilla navmesh. Mod B re-cuts the SAME navmesh and wins. If B
//              declares no dependency on A (direct or transitive), nothing shows B accounted for
//              A's work — A's edit may be silently gone. (Declaring is not seeing: a CK author
//              with A loaded inherits A's cut without mastering it, so the claim stops at "no
//              declared dependency".) This is invisible to a geometric check — the winning navmesh
//              is perfectly self-consistent, it is just missing the hole another mod cut for its
//              own objects.
//
//   DELETED    A winning navmesh record that is deleted, or that arrives empty where an earlier
//              provider had geometry. The classic hard break.
//
//   UNCUT      A plugin introduces obstructing references into a cell, and NOTHING re-cut that
//              cell's navmesh afterwards. Nobody can have accounted for those objects, because the
//              last word on the navmesh was spoken before they existed.
//
// usage: navownprobe [--list N] [--plugin Name.esp] [--emit out.json] [--base emit.json] [--enable X.esp]
//                    [--disable X.esp] [--order FILE] [--door-idx] [--reach-candidates] [--reach-json out.json]
//                    [--reach-top N] [--reach-witness] [--flags] [--list-master-deletions]
//
//   An argument not listed here is refused by name, exit 2, before anything is read (NavownArgs.cs).
//
//   --list-master-deletions  also LIST the navmeshes a Bethesda master deletes (the DLCs delete Skyrim.esm navmeshes
//                    they replace). By default they are only counted, on the line "deleted by a Bethesda master (the
//                    shipped game; not findings)": the shipped game, not a clash between mods. With the flag they are
//                    listed after the findings, each line labelled a shipped-game deletion; the findings, the count and
//                    the emit are unchanged, and a mod's deletion is a finding either way. See README.md.
//
//   --order FILE     evaluate a CANDIDATE configuration (a CandidateFile: the loaded plugins in order and, optionally,
//                    the mod folders in asset priority) instead of the profile's own. Mutually exclusive with
//                    --enable/--disable — two descriptions of one order, and picking one silently is how a result
//                    ends up being about an order nobody asked for. The emit records which one was consumed.
//
//   POPULATION (Q27, 2026-09-06): the two PHYSICAL populations here — the obstructing references UNCUT counts, and the
//                    placed actors and package anchors the needs-witness join calls witnesses — are the LOAD-TIME state:
//                    a reference counts when EnableState resolves its XESP parent chain enabled, not when its own
//                    initially-disabled flag happens to be clear. Before Q27 UNCUT read the flag, and a witness with ANY
//                    enable parent counted as live whatever that parent's own state was. The navmesh ownership half —
//                    who supplied what, and whose edit was discarded — is a RECORD census and is not filtered: a plugin's
//                    navmesh work is its work whether or not the room is switched on today.
//   --flags          the flag-only population, what every run before 2026-09-06 judged.
//
//   --base emit.json re-measure every record of that emit's rows under THIS order and carry the results in
//                    the emit as Predicates — the changed arm of a differential (conflictdiff reads both).
//   --reach-witness  the needs-witness join (NeedsWitness.cs): for every record with victim-only
//                    ground, find what the records say still needs it — package anchors, placed
//                    actors, targeted markers — and judge each against both arms. Implies
//                    --reach-candidates; extends --reach-json with a witnesses[] array per record.

// Defaults are the live VR instance; --instance / --game / --profile point every path at
// another MO2 layout — the SE conflict-testing lab's pre-flight runs exist so a finding is
// confirmed to reproduce in a minimal test order BEFORE anyone launches the game on it.
// Nothing is compiled in — see _shared/HarnessTarget.cs. Flags win over the environment and
// over harness.local.json; Require() runs AFTER them, so a run that passes --instance never
// trips it, and a run that configures nothing fails with guidance instead of walking a
// directory that isn't there.
using var __run = RunLog.BeginProcess("navownprobe", args);
// Known answers for which version of a moved or deleted reference this probe's walk keeps (_shared/MovedRefFixture.cs).
if (args.Length >= 1 && args[0] == "--moved-ref-selftest") return NavownSelfTest.Run();
// The edge-link identity fixture: written for tests/test_navown_edge_identity.py, which runs the real probe on it.
if (args.Length >= 2 && args[0] == "--write-edge-link-fixture") { Console.WriteLine(string.Join(Environment.NewLine, EdgeLinkFixture.Write(args[1]))); return 0; }
if (args.Length >= 3 && args[0] == "--nav-invariants") return NavInvariants.Run(args[1], args[2..]);
if (args.Length >= 2 && args[0] == "--write-navrepair-fixture") { Console.WriteLine(string.Join(Environment.NewLine, NavRepairFixture.Write(args[1]))); return 0; }

// The command line (NavownArgs.cs): an argument it does not know is refused by name, before anything is read.
var cli = NavownArgs.Parse(args);
if (cli.Error is not null) { Console.Error.WriteLine(cli.Error); return 2; }
string Instance = cli.Instance ?? HarnessTarget.Instance;
string GamePath = cli.Game ?? HarnessTarget.Game;
string profileName = cli.Profile ?? HarnessTarget.Profile;
Instance = HarnessTarget.Require(Instance, "MO2 instance directory", "instance", "--instance");
GamePath = HarnessTarget.Require(GamePath, "game directory", "game", "--game");
profileName = HarnessTarget.Require(profileName, "MO2 profile name", "profile", "--profile");
string profileDir = Path.Combine(Instance, "profiles", profileName);
string modsDir = Path.Combine(Instance, "mods");
string overwriteDir = Path.Combine(Instance, "overwrite");
string dataDir = Path.Combine(GamePath, "Data");

bool reportDoorIdx = cli.DoorIdx;
bool reachCandidates = cli.ReachCandidates;
bool reachWitness = cli.ReachWitness;
string? reachJsonPath = cli.ReachJsonPath;
int reachTopN = cli.ReachTopN;
bool flagsOnly = cli.FlagsOnly; string? stateSpec = cli.StateSpec;
int listN = cli.ListN;
string? onlyPlugin = cli.OnlyPlugin;
string? emitPath = cli.EmitPath;
string? basePath = cli.BasePath;
string? orderFile = cli.OrderFile;
var enablePlugins = cli.Enable;
var disablePlugins = cli.Disable;
PopulationMode popMode;
try { popMode = PopulationCli.Resolve("navownprobe", flagsOnly, stateSpec, stateSupported: false); }
catch (ArgumentException ex) { Console.Error.WriteLine(ex.Message); return 2; }

var sw = Stopwatch.StartNew();
// WHAT THIS RUN EVALUATES, decided in one place (_shared/HarnessOrderCli.cs): the real profile, a --enable/--disable
// what-if, or a --order candidate file — and, in the same step, the ASSET VIEW that configuration implies, so a run
// whose plugins changed no longer reads the profile's meshes and BSAs regardless. The identity it produces is written
// into the emit, so a reader (and conflictdiff) can see exactly what was consumed instead of inferring it.
var cfg = HarnessOrderCli.Resolve(profileName, profileDir, modsDir, dataDir, overwriteDir, GamePath,
                                  orderFile, enablePlugins, disablePlugins);
if (cfg.Refused) { Console.Error.WriteLine(cfg.RefusedReason); return 2; }
using var assets = AssetResolver.Build(overwriteDir, modsDir, dataDir, cfg.EnabledMods, cfg.Archives.Archives);
var paths = cfg.OrderedPaths.ToList();
Console.WriteLine($"load order: {paths.Count} plugins");
HarnessOrderCli.Print(cfg);
Console.WriteLine();

// Which base records can physically obstruct. XMarkers have no mesh and no collision; a dropped
// coin is not a wall. Kept as data here rather than hard-coded judgement anywhere deeper.
var Vanilla = BaseGamePlugins.Names;   // Bethesda's own shipped content (_shared/BaseGamePlugins.cs)

var Obstructing = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
{ "Static", "MoveableStatic", "Tree", "Furniture", "Door", "Container", "Activator", "Flora" };

var pluginKey = new ModKey[paths.Count];
var masterIdx = new HashSet<int>[paths.Count];
// The winning navmeshes with geometry and their owners, and every plugin's LAND, for the border and ground rules.
var winNav = new Dictionary<FormKey, (WinningNav W, int Owner)>();
var landIndex = new CellBoundaryIndex();
var navVersions = new NavVersions();                                      // NavVersions.cs
var navProviders = navVersions.Providers;
var navCell = navVersions.Cell;
var navSize = navVersions.Size;
var cellNavPlugins = navVersions.CellPlugins;
var cellNewRefs = new Dictionary<(FormKey Cell, int Plugin), List<FormKey>>();   // base records a plugin INTRODUCES
// The enable facts of every placement in the order, and the UNCUT candidates BEFORE any enable filter — the winning
// enable state of a reference is not known until every plugin has been read, so the filter is applied after the walk
// rather than during it (Q27). Before then the walk filtered on the record's own flag as it went, which could not see a
// parent an override enabled or disabled later.
var enableFacts = new EnableFactsIndex();
var uncutPop = new UncutPopulation();                 // the UNCUT candidates (UncutPopulation.cs)
var uncutCandidates = uncutPop.Candidates;
var cellName = new Dictionary<FormKey, (string? Edid, bool Interior)>();
// A cell's spatial identity, for the reach pass: an exterior candidate near a cell border can be
// rescued by the NEIGHBOUR cell's mesh, so "nearby coverage" must span the ±1 grid ring.
var cellSpace = new Dictionary<FormKey, (FormKey Wrld, int X, int Y, bool Interior)>();
var baseType = new Dictionary<FormKey, string>();
var baseMesh = new Dictionary<FormKey, string>();
var baseObnd = new Dictionary<FormKey, Vec3>();     // labelled fallback only
// Winner-takes-all, written in load order so the last write IS the winning state.
var links = new DanglingLinks();              // the winning link state and the dangling-link checks (DanglingLinks.cs)
var navTriCount = links.TriCount;
var navDoorTris = links.DoorTris;
var navEdgeLinks = links.EdgeLinks;
var doorNavLink = links.DoorNavLink;
var refExists = links.RefExists;
var refOverriders = links.RefOverriders;
var refGone = links.RefGone;

// Rule 6, applied to the plumbing: a plugin this loop cannot read contributes nothing, and that
// must be COUNTED, not swallowed. The review found both detectors dropping unreadable plugins with
// a bare catch{continue} and no output saying so.
var unreadablePlugins = new List<string>();
var landUnreadable = new List<string>();
__run.Feature("plugins", paths.Count, "plugins").Stage("scan", "reading plugins", paths.Count, "plugins");


for (int p = 0; p < paths.Count; p++)
{
    __run.Advance();
    ISkyrimModGetter mod;
    try { mod = SkyrimMod.CreateFromBinaryOverlay(paths[p], SkyrimRelease.SkyrimSE); }
    catch { unreadablePlugins.Add(Path.GetFileName(paths[p])); pluginKey[p] = ModKey.Null; masterIdx[p] = new HashSet<int>(); continue; }

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

        // Deletion lives on the record, not on the extracted geometry, so it is read directly.
        var deletedHere = new HashSet<FormKey>();
        foreach (var rec in mod.EnumerateMajorRecords())
        {
            if (rec is INavigationMeshGetter nm)
            {
                if (nm.IsDeleted) deletedHere.Add(nm.FormKey);
                links.NoteNavmesh(nm, p);
            }
            else if (rec is IPlacedObjectGetter po)
            {
                links.NotePlaced(po, p);
            }
            else if (rec is IModeledGetter mo && rec is ISkyrimMajorRecordGetter sm)
            {
                baseType[sm.FormKey] = rec.GetType().Name.Replace("BinaryOverlay", "");
                if (mo.Model?.File is { } mf && !string.IsNullOrWhiteSpace(mf.GivenPath)) baseMesh[sm.FormKey] = mf.GivenPath;
                if (rec is IObjectBoundedGetter ob && ob.ObjectBounds is { } bnd)
                    baseObnd[sm.FormKey] = new Vec3(MathF.Abs(bnd.Second.X - bnd.First.X),
                                                    MathF.Abs(bnd.Second.Y - bnd.First.Y),
                                                    MathF.Abs(bnd.Second.Z - bnd.First.Z));
            }
        }

        try { landIndex.Ingest(p, mod); } catch { landUnreadable.Add(Path.GetFileName(paths[p])); }
        foreach (var cp in UncutPopulation.Cells(mod))
        {
            cellName[cp.Cell] = (cp.CellEditorId, cp.Interior);
            cellSpace[cp.Cell] = (cp.Worldspace, cp.Grid?.X ?? int.MinValue, cp.Grid?.Y ?? int.MinValue, cp.Interior);

            navVersions.Note(cp, p, deletedHere, ClobberFindings.GeomHash);
            foreach (var nv in cp.Navmeshes)
                if (nv.Deleted || deletedHere.Contains(nv.FormKey)) winNav.Remove(nv.FormKey);
                else winNav[nv.FormKey] = (new WinningNav(nv, cp.SpaceKey, cp.Grid, cp.Cell), p);
            foreach (var dn in cp.DeletedNavmeshes ?? Array.Empty<DeletedNavmesh>()) winNav.Remove(dn.FormKey);

            foreach (var r in cp.Refs) enableFacts.Note(r);
            uncutPop.Note(cp, p, mod.ModKey);
        }
    }
    if ((p + 1) % 150 == 0) Console.WriteLine($"   ... {p + 1}/{paths.Count} plugins, {sw.Elapsed.TotalSeconds:0}s");
}

// The UNCUT population, once every plugin has spoken: an obstructing base, and the reference present at load under the
// run's population (Q27). The base type could not be tested during the walk either — a later plugin can define the base.
var pop = flagsOnly ? EnablePopulation.Flags(enableFacts) : EnablePopulation.LoadTime(enableFacts);
uncutPop.Resolve(k => baseType.TryGetValue(k, out var bt) ? bt : null, Obstructing, pop.Enabled, cellNewRefs,
              out long uncutSkippedAbsent, out long uncutSkippedType);
uncutPop.Dump();

Console.WriteLine($"\nindexed {navProviders.Count:N0} navmesh records across {cellNavPlugins.Count:N0} cells  ({sw.Elapsed.TotalSeconds:0.0}s)");
Console.WriteLine($"   {pop.Describe()}");
Console.WriteLine($"   UNCUT candidates: {uncutCandidates.Count:N0} introduced references — {uncutPop.SkippedMovedOrDeleted:N0} moved out of their cell or deleted by a later plugin, {uncutSkippedType:N0} of a non-obstructing type, "
                + $"{uncutSkippedAbsent:N0} not present at load, {cellNewRefs.Values.Sum(b => b.Count):N0} counted");
Console.WriteLine($"   plugins that could not be read (contribute nothing, counted per rule 6): {unreadablePlugins.Count:N0}"
                + (unreadablePlugins.Count > 0 ? "  — " + string.Join(", ", unreadablePlugins) : ""));

// TRANSITIVE master closure. The review found 3 of 28 clobber pairs where the winner masters an
// intermediate that masters the loser — the winner's author demonstrably had the loser installed
// (their dependency requires it), so "no declared dependency" was false one hop out. The rule
// lives in _shared/DetectorModel.cs and is owned by proofprobe cases.
var masterClosure = DetectorModel.MasterClosure(masterIdx);
string Cell(FormKey c) => cellName.TryGetValue(c, out var v) ? (v.Edid ?? c.ToString()) + (v.Interior ? " (int)" : " (ext)") : c.ToString();
bool Wanted(int p) => onlyPlugin is null || string.Equals(pluginKey[p].FileName, onlyPlugin, StringComparison.OrdinalIgnoreCase);

// ---------------------------------------------------------------------------------------
// CLOBBERED — B overrode the same navmesh A had already re-cut, declaring no dependency on A.
// ---------------------------------------------------------------------------------------
// Precisely what is measured: the winner declares no dependency on the loser, DIRECT OR TRANSITIVE
// (the closure matters: 3 pairs here had the winner mastering an intermediate that masters the
// loser). What is NOT measured is visibility — an author re-saving a vanilla navmesh with the
// loser's plugin loaded in the CK inherits its cut without gaining a master — so "reverted" and
// "extended without declaring the dependency" are indistinguishable at this layer. The claim is
// "no declared dependency", never "cannot have seen".
// The decision itself is DetectorModel.FindClobber — proven by construction in proofprobe, so
// this loop only feeds it providers, the closure, and the per-provider geometry triple.
var clobber = ClobberFindings.Find(navProviders, masterClosure, navSize, Wanted);   // ClobberFindings.cs

// Rule 12, finally applied to CLOBBERED itself. The count carried an unmeasured floor for two
// sessions: Dawnguard × HearthFires is Bethesda re-cutting Bethesda — permanent, shipped that way,
// and exactly the class-A population the dangling-link split already names. USSEP-as-loser is kept
// as its own line (a mod discarding USSEP's navmesh fixes IS a finding, but it is a different
// conversation from mod-on-mod, because nearly nothing masters USSEP on purpose).
ClobberPopulation ClobberPop((FormKey Nav, int Loser, int Winner, int LoserT, int WinnerT, bool Material) c) =>
    ClobberFindings.Population(pluginKey[c.Loser].FileName.String, pluginKey[c.Winner].FileName.String, Vanilla);
string ClobberClass((FormKey Nav, int Loser, int Winner, int LoserT, int WinnerT, bool Material) c) =>
    ClobberFindings.ClassLabel(ClobberPop(c));

var clobberFindings = clobber.Where(c => ClobberFindings.IsFinding(c.Material, ClobberPop(c))).ToList();
Console.WriteLine($"\n==== CLOBBERED: one mod's navmesh work silently discarded by another ====");
Console.WriteLine($"   navmesh records affected : {clobber.Count:N0}"
                + $"   ({clobber.Count(c => c.Material):N0} where the geometry actually differs)");
Console.WriteLine($"   (the winner declares no dependency on the loser — direct or transitive — so nothing");
Console.WriteLine($"    shows it accounted for that edit; whether the author SAW it is not decidable here)");
Console.WriteLine($"   ---- the material records, split by population (rule 12) ----");
foreach (var g in clobber.Where(c => c.Material).GroupBy(ClobberClass).OrderBy(g => g.Key))
    Console.WriteLine($"   {g.Count(),4}  {g.Key}");
Console.WriteLine($"   Findings are B and C: {clobberFindings.Count:N0} records. A is measured floor and excluded from --emit.");
int sameCountMoved = clobber.Count(c =>
{
    var l = navSize.GetValueOrDefault((c.Nav, c.Loser));
    var w = navSize.GetValueOrDefault((c.Nav, c.Winner));
    return c.Material && l.V == w.V && l.T == w.T;
});
Console.WriteLine($"   (material by content hash; {sameCountMoved} of them have identical counts but moved geometry —");
Console.WriteLine($"    a class the count-only test used before 2026-08-27 could not see)");
foreach (var g in clobber.Where(c => c.Material)
                         .GroupBy(c => (Loser: pluginKey[c.Loser].FileName, Winner: pluginKey[c.Winner].FileName))
                         .OrderByDescending(g => g.Count()).Take(listN))
{
    var ex = g.First();
    Console.WriteLine($"   {g.Count(),5} navmesh  {g.Key.Loser}");
    Console.WriteLine($"            reverted by  {g.Key.Winner}    e.g. {ex.Nav} in {Cell(navCell.GetValueOrDefault(ex.Nav))}"
                    + $"  ({ex.LoserT} tris -> {ex.WinnerT})");
}

// The reach pass's constants, shared with the clobber predicate below.
const float ReachClearance = ClobberFindings.ReachClearance;
const float ReachSpacing = ClobberFindings.ReachSpacing;
const float WellClear = 240f;        // 2x resolution: what a probe-worthy candidate must exceed

// Full geometry for exactly the (record, plugin) pairs a pass names: the main pass keeps counts and hashes only, because
// every version of every navmesh would be most of the load order in memory. Re-opens only the plugins the plan names.
(Dictionary<(FormKey Nav, int Plugin), NavmeshGeometry> Geom, int Unreadable) LoadNavGeometry(Dictionary<int, HashSet<FormKey>> plan)
{
    var geom = new Dictionary<(FormKey Nav, int Plugin), NavmeshGeometry>();
    int unreadable = 0;
    foreach (var (pi, wantedNavs) in plan)
    {
        ISkyrimModGetter mod;
        try { mod = SkyrimMod.CreateFromBinaryOverlay(paths[pi], SkyrimRelease.SkyrimSE); }
        catch { unreadable += wantedNavs.Count; continue; }
        using (mod as IDisposable)
            foreach (var cp in PlacementExtract.FromMod(mod))
                foreach (var nav in cp.Navmeshes)
                    if (wantedNavs.Contains(nav.FormKey)) geom[(nav.FormKey, pi)] = nav;
    }
    return (geom, unreadable);
}
static void PlanInto(Dictionary<int, HashSet<FormKey>> plan, int plugin, FormKey nav)
{
    if (plugin < 0) return;
    if (!plan.TryGetValue(plugin, out var s)) plan[plugin] = s = new HashSet<FormKey>();
    s.Add(nav);
}
static (List<System.Numerics.Vector3> V, List<(int A, int B, int C, int Flags)> T) Mesh(NavmeshGeometry g)
    => (g.Vertices.Select(v => new System.Numerics.Vector3(v.X, v.Y, v.Z)).ToList(),
        g.Triangles.Select(t => ((int)t.A, (int)t.B, (int)t.C, t.Flags)).ToList());

// THE PHYSICAL PREDICATE of a CLOBBERED row: ClobberFindings.Predicate (ClobberFindings.cs).
(PhysicalState State, string Detail) ClobberPredicate(FormKey nav, int loser, int winner, Dictionary<(FormKey Nav, int Plugin), NavmeshGeometry> geom)
    => ClobberFindings.Predicate(nav, loser, winner, geom, $"{pluginKey[winner].FileName}");

// The physical predicate of a dangling-link row: the same exact check the finding came from, re-asked of the winning
// state under this arm. Exact, no geometry: the index still exceeds the winning count (or still points into a navmesh the
// winning state lacks), or it does not.
(PhysicalState State, string Detail) DanglePredicate(string kind, FormKey rec, FormKey other, int? tri = null) => links.Predicate(kind, rec, other, tri);
var coverageExtra = new List<CoverageLine>();

// ---------------------------------------------------------------------------------------
// REACH CANDIDATES — 3.5.5's selector: ground where the two arms PREDICT different answers.
// ---------------------------------------------------------------------------------------
// The reachability differential ("reachable under the loser's navmesh, not under the winner's")
// is confirmed in game by an oracle costing ~10-30 s per target with a ~120 u resolution — a
// confirmer, never a scanner. This pass finds the only ground worth confirming: points on one
// version's standable surface farther than the resolution from EVERYTHING the other arm offers
// (the other version of this record, plus every other winning navmesh in the same cell — the
// arms differ in one record, so the rest of the cell rescues candidates on shared ground). The
// rule is DetectorModel.CoverageLoss / DistanceToCoverage, owned by proofprobe. E5's lesson,
// encoded: 26 targets on ground where the arms were geometrically identical proved nothing.
if (reachCandidates)
{
    var pool = clobber.Where(c => c.Material).ToList();
    Console.WriteLine($"\n==== REACH CANDIDATES: ground where the two arms predict different answers (3.5.5) ====");
    Console.WriteLine($"   pool: {pool.Count:N0} material clobbers ({clobber.Count:N0} raw; the non-material rest CANNOT differ and are excluded)");
    Console.WriteLine($"   clearance {ReachClearance:0}u (oracle resolution), spacing {ReachSpacing:0}u, probe-worthy at >{WellClear:0}u");

    // Everything the winner arm offers near a candidate: the winner's version of the record,
    // every OTHER winning navmesh in the record's cell, and — for exteriors — every winning
    // navmesh in the ±1 grid ring, because a candidate near a cell border can be rescued by the
    // neighbour cell's mesh. (The ring earned its place: the first exterior candidate spot-checked
    // with --nav-at had a neighbour-record triangle 109 u away that the same-cell view missed.)
    var cellNavs = new Dictionary<FormKey, List<FormKey>>();
    foreach (var (nav, cell) in navCell)
    {
        if (!cellNavs.TryGetValue(cell, out var l)) cellNavs[cell] = l = new List<FormKey>();
        l.Add(nav);
    }
    var gridCells = new Dictionary<(FormKey Wrld, int X, int Y), List<FormKey>>();
    foreach (var (cell, sp) in cellSpace)
    {
        if (sp.Interior || sp.X == int.MinValue) continue;
        if (!gridCells.TryGetValue((sp.Wrld, sp.X, sp.Y), out var l)) gridCells[(sp.Wrld, sp.X, sp.Y)] = l = new List<FormKey>();
        l.Add(cell);
    }
    // The record's cell first, then (exteriors) its ±1 grid ring — the same neighbourhood for
    // coverage and, in the witness join, for the actors and anchors that can stand on it.
    List<FormKey> NearbyCells(FormKey cell)
    {
        var cells = new List<FormKey> { cell };
        if (cellSpace.TryGetValue(cell, out var sp) && !sp.Interior && sp.X != int.MinValue)
            for (int dx = -1; dx <= 1; dx++)
                for (int dy = -1; dy <= 1; dy++)
                    if ((dx != 0 || dy != 0) && gridCells.TryGetValue((sp.Wrld, sp.X + dx, sp.Y + dy), out var nb))
                        cells.AddRange(nb);
        return cells;
    }
    List<FormKey> NearbyNavs(FormKey ofNav)
        => NearbyCells(navCell[ofNav]).Where(cellNavs.ContainsKey)
                    .SelectMany(cl => cellNavs[cl])
                    .Where(n => n != ofNav)
                    .Distinct().ToList();

    var plan = new Dictionary<int, HashSet<FormKey>>();
    void Want(int plugin, FormKey nav)
    {
        if (plugin < 0) return;
        if (!plan.TryGetValue(plugin, out var s)) plan[plugin] = s = new HashSet<FormKey>();
        s.Add(nav);
    }
    foreach (var c in pool)
    {
        Want(c.Loser, c.Nav);
        Want(c.Winner, c.Nav);
        foreach (var other in NearbyNavs(c.Nav)) Want(NavOwner(other), other);
    }

    // Second pass: re-open only the plugins the plan names and keep only the wanted geometry.
    // The main pass deliberately retains counts+hash, not shapes — full geometry for every
    // (record, plugin) pair would be most of the load order in memory.
    var (geom, unreadableGeom) = LoadNavGeometry(plan);
    Console.WriteLine($"   geometry: {geom.Count:N0} (record, plugin) pairs from {plan.Count:N0} plugins"
                    + (unreadableGeom > 0 ? $"; {unreadableGeom:N0} wanted extractions unreadable (counted per rule 6)" : ""));

    // The needs-witness census: one more pass over every plugin, reading packages and NPC records
    // from anywhere and placed references only in the pool's cells (+ring). Runs before the loop
    // so the coverage sets are computed once and the join rides them.
    NeedsWitnessJoin.Census? census = null;
    if (reachWitness)
    {
        var wantedCells = pool.SelectMany(c => NearbyCells(navCell[c.Nav])).ToHashSet();
        census = NeedsWitnessJoin.Build(paths, wantedCells, popMode == PopulationMode.Flags);
        NeedsWitnessJoin.Dump(census, wantedCells);
        Console.WriteLine($"   witness census: {census.RefsSeen:N0} placed refs in {wantedCells.Count:N0} cells (pool + ring), "
                        + $"{census.Packs.Count:N0} packages, {census.NpcPackages.Count:N0} NPC records, {census.NpcPlacedAnywhere.Count:N0} placed somewhere"
                        + (census.UnreadablePlugins > 0 ? $"; {census.UnreadablePlugins} plugins unreadable (counted per rule 6)" : "")
                        + $"  ({sw.Elapsed.TotalSeconds:0.0}s)");
        Console.WriteLine(popMode == PopulationMode.Flags
            ? "   population: FLAGS — a witness is judged by its own initially-disabled flag and no chain is resolved, so a "
              + "witness whose marker is off at load still counts (the pre-Q27 reading, kept for reproducing an old number)"
            : $"   of those, {census.RefsAbsentAtLoad:N0} are not in the world when the cell loads (their enable chain says so) "
              + $"and {census.RefsChainUnresolved:N0} rest on a chain that could not be read to the end — a witness there is counted, not trusted");
    }

    int geomMissing = 0, recordsVictim = 0, recordsVictimWellClear = 0, recordsMirrorOnly = 0;
    int movedSubset = 0, movedSubsetWithLoss = 0;
    var perClass = new Dictionary<string, (int Records, int WithLoss)>();
    var jsonOut = new List<Dictionary<string, object?>>();
    // Witness tallies, per record and per row, for the summary and the population split.
    var witnessByRecord = new List<(FormKey Nav, string Class, WitnessReport Report)>();
    foreach (var c in pool)
    {
        var cls = ClobberClass(c);
        var tally = perClass.GetValueOrDefault(cls);
        bool moved = navSize.GetValueOrDefault((c.Nav, c.Loser)).V == navSize.GetValueOrDefault((c.Nav, c.Winner)).V
                  && c.LoserT == c.WinnerT;
        if (moved) movedSubset++;
        if (!geom.TryGetValue((c.Nav, c.Loser), out var lg) || !geom.TryGetValue((c.Nav, c.Winner), out var wg))
        {
            geomMissing++;
            perClass[cls] = (tally.Records + 1, tally.WithLoss);
            continue;
        }
        var L = Mesh(lg);
        var W = Mesh(wg);
        var victim = DetectorModel.CoverageLoss(L.V, L.T, W.V, W.T, ReachClearance, ReachSpacing);
        var mirror = DetectorModel.CoverageLoss(W.V, W.T, L.V, L.T, ReachClearance, ReachSpacing);

        var others = NearbyNavs(c.Nav)
            .Select(n => (Nav: n, Plugin: NavOwner(n)))
            .Where(x => geom.ContainsKey((x.Nav, x.Plugin)))
            .Select(x => Mesh(geom[(x.Nav, x.Plugin)]))
            .ToList();
        // A survivor's Clearance is re-stated as the EFFECTIVE clearance: the distance to the
        // nearest coverage the other arm offers anywhere nearby, not just to the winner's version
        // of this record — that is the number a probe target's "well clear of resolution" is
        // judged on.
        List<DetectorModel.CoverageSample> Surviving(List<DetectorModel.CoverageSample> raw)
            => raw.Select(s =>
                  {
                      float eff = s.Clearance;
                      foreach (var o in others)
                      {
                          if (eff <= ReachClearance) break;
                          eff = MathF.Min(eff, DetectorModel.DistanceToCoverage(s.Point, o.V, o.T));
                      }
                      return s with { Clearance = eff };
                  })
                  .Where(s => s.Clearance > ReachClearance)
                  .OrderByDescending(s => s.Clearance).ToList();
        var victimS = Surviving(victim);
        var mirrorS = Surviving(mirror);

        bool hasVictim = victimS.Count > 0;
        if (hasVictim) recordsVictim++;
        if (victimS.Any(s => s.Clearance > WellClear)) recordsVictimWellClear++;
        if (!hasVictim && mirrorS.Count > 0) recordsMirrorOnly++;
        if (moved && hasVictim) movedSubsetWithLoss++;
        perClass[cls] = (tally.Records + 1, tally.WithLoss + (hasVictim ? 1 : 0));

        if (hasVictim || mirrorS.Count > 0)
        {
            var best = hasVictim ? victimS[0] : mirrorS[0];
            Console.WriteLine($"   {(hasVictim ? "VICTIM-ONLY GROUND" : "winner-only ground"),-19} {c.Nav} in {Cell(navCell.GetValueOrDefault(c.Nav))}");
            Console.WriteLine($"      loser {pluginKey[c.Loser].FileName} ({c.LoserT} tris) vs winner {pluginKey[c.Winner].FileName} ({c.WinnerT} tris)   [{cls[..1]}]"
                            + (moved ? "   [same counts, moved geometry]" : ""));
            Console.WriteLine($"      victim-arm-only points {victimS.Count:N0}/{victim.Count:N0} raw"
                            + $", winner-arm-only {mirrorS.Count:N0}/{mirror.Count:N0} raw"
                            + $"; best clearance {(float.IsPositiveInfinity(best.Clearance) ? "inf" : $"{best.Clearance:0}u")}"
                            + $" at ({best.Point.X:0}, {best.Point.Y:0}, {best.Point.Z:0})");
        }

        // The needs-witness join for this record: everything the records say still needs the
        // ground, measured against the same L / W / others sets, judged by DetectorModel.NeedsWitness.
        WitnessReport? wit = null;
        if (census is not null && (hasVictim || mirrorS.Count > 0))
        {
            var cellKey = navCell[c.Nav];
            wit = NeedsWitnessJoin.Judge(census, cellKey, NearbyCells(cellKey), c.Winner, masterClosure,
                                         L, W, others, ReachClearance, bk => baseType.GetValueOrDefault(bk));
            witnessByRecord.Add((c.Nav, cls, wit));
            int w1 = wit.Rows.Count(r => r.Kind == WitnessKind.PackageAnchor);
            int w2 = wit.Rows.Count(r => r.Kind == WitnessKind.PlacedActor);
            int w3 = wit.Rows.Count(r => r.Kind == WitnessKind.TargetedRef);
            Console.WriteLine($"      witnesses on this record's ground: W1 {w1}, W2 {w2}, W3 {w3}"
                            + $"   (actors in cell {wit.ActorsInCell}, on this ground {wit.ActorsOnGround}; anchors in other cells {wit.AnchorsElsewhere})"
                            + $"   stranded {wit.Rows.Count(r => r.Verdict == WitnessVerdict.Stranded)}"
                            + $", mirror {wit.Rows.Count(r => r.Verdict == WitnessVerdict.Mirror)}"
                            + $", covered {wit.Rows.Count(r => r.Verdict == WitnessVerdict.Covered)}"
                            + $", uncovered {wit.Rows.Count(r => r.Verdict == WitnessVerdict.Uncovered)}"
                            + $", unjudgeable {wit.Rows.Count(r => r.Verdict == WitnessVerdict.Unjudgeable)}");
            foreach (var r in wit.Rows.Where(r => r.Verdict is WitnessVerdict.Stranded or WitnessVerdict.Mirror
                                               || r.Reason == WitnessBlocker.WithinResolutionBand || r.ElevatedStrand))
                Console.WriteLine($"         {r.Verdict,-11} {r.Kind,-13} {r.Form} {r.Edid ?? ""}"
                                + (r.Actor is not null ? $"  actor {r.Actor}" : "")
                                + (r.Package is not null ? $"  pkg {r.Package}[{r.Slot}]" : "")
                                + $"  {r.Arm}"
                                + (r.Point is { } pt ? $" @({pt.X:0},{pt.Y:0},{pt.Z:0})" : "")
                                + (r.Radius > 0 ? $" r={r.Radius:0}" : "")
                                + $"  [{r.Source}] {r.Authorship}{(r.Governing ? " governing" : "")}{(r.ElevatedStrand ? " ELEVATED-ANCHOR(horizontal reading strands)" : "")}"
                                + $" — {r.Why}");
        }
        if (reachJsonPath is not null)
            jsonOut.Add(new Dictionary<string, object?>
            {
                ["witnesses"] = wit?.Rows.Select(r => new
                {
                    kind = r.Kind.ToString(), form = r.Form.ToString(), edid = r.Edid, actor = r.Actor, package = r.Package,
                    slot = r.Slot, arm = r.Arm, x = r.Point?.X, y = r.Point?.Y, z = r.Point?.Z, radius = r.Radius,
                    dV = Num(r.DV), dW = Num(r.DW), hV = Num(r.HV), hW = Num(r.HW),
                    verdict = r.Verdict.ToString(), reason = r.Reason.ToString(), authorship = r.Authorship.ToString(),
                    why = r.Why, governing = r.Governing, state = r.State, source = r.Source, elevatedStrand = r.ElevatedStrand,
                }).ToList(),
                ["witnessCounts"] = wit is null ? null : new
                {
                    actorsInCell = wit.ActorsInCell, actorsOnGround = wit.ActorsOnGround, anchorsElsewhere = wit.AnchorsElsewhere,
                    onLostGround = wit.AnyOnLostGround,
                    w1 = wit.Rows.Count(r => r.Kind == WitnessKind.PackageAnchor),
                    w2 = wit.Rows.Count(r => r.Kind == WitnessKind.PlacedActor),
                    w3 = wit.Rows.Count(r => r.Kind == WitnessKind.TargetedRef),
                    stranded = wit.Rows.Count(r => r.Verdict == WitnessVerdict.Stranded),
                    strandedOversight = wit.Rows.Count(r => r.Verdict == WitnessVerdict.Stranded && r.Authorship == WitnessAuthorship.Oversight),
                    mirror = wit.Rows.Count(r => r.Verdict == WitnessVerdict.Mirror),
                    covered = wit.Rows.Count(r => r.Verdict == WitnessVerdict.Covered),
                    uncovered = wit.Rows.Count(r => r.Verdict == WitnessVerdict.Uncovered),
                    unjudgeable = wit.Rows.Where(r => r.Verdict == WitnessVerdict.Unjudgeable)
                                          .GroupBy(r => r.Reason.ToString()).ToDictionary(g => g.Key, g => g.Count()),
                    elevatedAnchors = wit.Rows.Count(r => r.ElevatedStrand),
                    w2BlindBand = wit.BlindBand(ReachClearance).Count(),
                },
                ["nav"] = c.Nav.ToString(),
                ["cell"] = Cell(navCell.GetValueOrDefault(c.Nav)),
                ["loser"] = pluginKey[c.Loser].FileName.String,
                ["winner"] = pluginKey[c.Winner].FileName.String,
                ["class"] = cls,
                ["loserTris"] = c.LoserT,
                ["winnerTris"] = c.WinnerT,
                ["sameCountsMovedGeometry"] = moved,
                ["victimOnly"] = victimS.Take(reachTopN).Select(s => new { x = s.Point.X, y = s.Point.Y, z = s.Point.Z, clearance = s.Clearance }).ToList(),
                ["victimOnlyTotal"] = victimS.Count,
                ["winnerOnly"] = mirrorS.Take(reachTopN).Select(s => new { x = s.Point.X, y = s.Point.Y, z = s.Point.Z, clearance = s.Clearance }).ToList(),
                ["winnerOnlyTotal"] = mirrorS.Count,
            });
    }

    Console.WriteLine($"\n   ---- the gate (3.5.5): does any material clobber carry an actual coverage difference? ----");
    Console.WriteLine($"   records with victim-arm-only ground beyond {ReachClearance:0}u : {recordsVictim:N0}"
                    + $"   (of those, beyond the probe-worthy {WellClear:0}u: {recordsVictimWellClear:N0})");
    Console.WriteLine($"   records with winner-arm-only ground (the mirror)  : {recordsMirrorOnly:N0} (mirror only)");
    Console.WriteLine($"   identical-counts-but-moved-geometry subset        : {movedSubset:N0} records, {movedSubsetWithLoss:N0} with victim-only ground");
    Console.WriteLine($"   pairs skipped for unreadable/missing geometry     : {geomMissing:N0} (counted, not cleared)");
    coverageExtra.Add(new CoverageLine("material clobbers (reach pass)", pool.Count - geomMissing, new Dictionary<string, long> { ["GeometryMissing"] = geomMissing },
                                       null, $"{recordsVictim:N0} with victim-only ground beyond {ReachClearance:0}u"));
    Console.WriteLine($"   ---- by population (rule 12) ----");
    foreach (var (k, t) in perClass.OrderBy(kv => kv.Key))
        Console.WriteLine($"   {t.WithLoss,4} of {t.Records,4}  {k}");
    Console.WriteLine($"   (connectivity is NOT judged here — a point covered in both arms but cut off in one is");
    Console.WriteLine($"    invisible to this pass; and every candidate still needs the --nav-at confirmation and");
    Console.WriteLine($"    the in-game oracle's own controls before it becomes a finding)");

    // ---- the needs-witness summary: what still needs the lost ground, and who knew ----
    object? witnessSummary = null;
    if (census is not null)
    {
        var rows = witnessByRecord.SelectMany(w => w.Report.Rows.Select(r => (w.Nav, w.Class, Row: r))).ToList();
        int Rows(WitnessKind k, WitnessVerdict v) => rows.Count(x => x.Row.Kind == k && x.Row.Verdict == v);
        var strandedRecords = witnessByRecord.Where(w => w.Report.Stranded.Any()).ToList();
        var strandedOversightRecords = witnessByRecord.Where(w => w.Report.Stranded.Any(r => r.Authorship == WitnessAuthorship.Oversight)).ToList();
        Console.WriteLine($"\n   ---- the needs-witness join (3.5.5's tail): what the records say still needs the lost ground ----");
        Console.WriteLine($"   records judged {witnessByRecord.Count:N0}; carrying any W1/W2 witness on their own ground: "
                        + $"{witnessByRecord.Count(w => w.Report.AnyWitness):N0}"
                        + $"; with a witness on the LOST ground (Stranded or in-band): {witnessByRecord.Count(w => w.Report.AnyOnLostGround):N0}"
                        + $"   (W3 apart: {witnessByRecord.Count(w => w.Report.Rows.Any(r => r.Kind == WitnessKind.TargetedRef)):N0} records)");
        Console.WriteLine($"   package anchors placed in OTHER cells (another record's witness, not counted here): "
                        + $"{witnessByRecord.Sum(w => w.Report.AnchorsElsewhere):N0}");
        Console.WriteLine($"   {"witness rows",-14} {"stranded",9} {"mirror",7} {"covered",8} {"uncovered",10} {"unjudgeable",12}");
        foreach (var k in new[] { WitnessKind.PackageAnchor, WitnessKind.PlacedActor, WitnessKind.TargetedRef })
            Console.WriteLine($"   {(k == WitnessKind.PackageAnchor ? "W1 anchors" : k == WitnessKind.PlacedActor ? "W2 actors" : "W3 targeted"),-14}"
                            + $" {Rows(k, WitnessVerdict.Stranded),9:N0} {Rows(k, WitnessVerdict.Mirror),7:N0} {Rows(k, WitnessVerdict.Covered),8:N0}"
                            + $" {Rows(k, WitnessVerdict.Uncovered),10:N0} {Rows(k, WitnessVerdict.Unjudgeable),12:N0}");
        Console.WriteLine($"   records with a STRANDED witness : {strandedRecords.Count:N0}"
                        + $"   of which Oversight (the finding-shaped rows): {strandedOversightRecords.Count:N0}"
                        + $"   Deliberate-only: {strandedRecords.Count - strandedOversightRecords.Count:N0}");
        Console.WriteLine($"   stranded rows by authorship     : Oversight {rows.Count(x => x.Row.Verdict == WitnessVerdict.Stranded && x.Row.Authorship == WitnessAuthorship.Oversight):N0}"
                        + $", Deliberate {rows.Count(x => x.Row.Verdict == WitnessVerdict.Stranded && x.Row.Authorship == WitnessAuthorship.Deliberate):N0}"
                        + $"   (W1 governing-package rows among them: {rows.Count(x => x.Row.Verdict == WitnessVerdict.Stranded && x.Row.Governing):N0})");
        Console.WriteLine($"   unjudgeable, by reason (counted, never cleared):");
        foreach (var g in rows.Where(x => x.Row.Verdict == WitnessVerdict.Unjudgeable)
                               .GroupBy(x => (x.Row.Reason, Arm: x.Row.Reason == WitnessBlocker.UnreadableAnchor ? x.Row.Arm : ""))
                               .OrderByDescending(g => g.Count()))
            Console.WriteLine($"   {g.Count(),6}  {g.Key.Reason}{(g.Key.Arm.Length > 0 ? $" ({g.Key.Arm} arm)" : "")}");
        Console.WriteLine($"   elevated anchors (3-D Uncovered, horizontal reading would strand): {rows.Count(x => x.Row.ElevatedStrand):N0}");
        coverageExtra.Add(new CoverageLine("witness rows (needs-witness join)", rows.Count(x => x.Row.Verdict != WitnessVerdict.Unjudgeable),
                                           rows.Where(x => x.Row.Verdict == WitnessVerdict.Unjudgeable).GroupBy(x => x.Row.Reason.ToString()).ToDictionary(g => g.Key, g => (long)g.Count()),
                                           null, $"W2 blind band {witnessByRecord.SelectMany(w => w.Report.BlindBand(ReachClearance)).Count():N0} (counted, never a verdict)"));
        // The blind band the rule states for placements: below clearance nothing is measured for a
        // W2, so a placed actor the winner arm leaves that far off its mesh reads Covered. Counted,
        // never a verdict, and split by distance because the 3-D measure includes standing height.
        var blind = witnessByRecord.SelectMany(w => w.Report.BlindBand(ReachClearance)).ToList();
        int Blind(float lo, float hi) => blind.Count(r => r.DW > lo && r.DW <= hi);
        Console.WriteLine($"   W2 blind band (placements the winner arm leaves within (0, {ReachClearance:0}u] of its mesh, read Covered —"
                        + $" below {ReachClearance:0}u nothing is measured for a placement): {blind.Count:N0} of {Rows(WitnessKind.PlacedActor, WitnessVerdict.Covered):N0} Covered W2 rows");
        Console.WriteLine($"     by 3-D distance to the winner's mesh, which includes the actor's standing height over its own triangle:"
                        + $" <={ReachClearance / 4:0}u {Blind(0, ReachClearance / 4):N0}, ({ReachClearance / 4:0}, {ReachClearance / 2:0}]u {Blind(ReachClearance / 4, ReachClearance / 2):N0},"
                        + $" ({ReachClearance / 2:0}, {ReachClearance:0}]u {Blind(ReachClearance / 2, ReachClearance):N0}   (a count, not a verdict)");
        Console.WriteLine($"   ---- by population (rule 12): records judged -> with a witness on their ground -> on the LOST ground -> Stranded -> Stranded+Oversight ----");
        foreach (var g in witnessByRecord.GroupBy(w => w.Class).OrderBy(g => g.Key))
            Console.WriteLine($"   {g.Count(),4} -> {g.Count(w => w.Report.AnyWitness),4} -> {g.Count(w => w.Report.AnyOnLostGround),4} -> {g.Count(w => w.Report.Stranded.Any()),4}"
                            + $" -> {g.Count(w => w.Report.Stranded.Any(r => r.Authorship == WitnessAuthorship.Oversight)),4}  {g.Key}");
        Console.WriteLine($"   (a Stranded+Oversight row is the records-side needs-witness — what the game showed for one (2026-09-01): a placed");
        Console.WriteLine($"    actor ABANDONS victim-only ground, sandboxing on the winner's mesh from the first read, never at its placement;");
        Console.WriteLine($"    Stranded+Deliberate says the winner's side placed or kept the witness knowingly; connectivity,");
        Console.WriteLine($"    which package runs when, and whether the actor ever loads are still not judged)");
        witnessSummary = new
        {
            recordsJudged = witnessByRecord.Count,
            recordsWithWitness = witnessByRecord.Count(w => w.Report.AnyWitness),
            recordsWithWitnessOnLostGround = witnessByRecord.Count(w => w.Report.AnyOnLostGround),
            anchorsInOtherCells = witnessByRecord.Sum(w => w.Report.AnchorsElsewhere),
            recordsStranded = strandedRecords.Count,
            recordsStrandedOversight = strandedOversightRecords.Count,
            byClass = witnessByRecord.GroupBy(w => w.Class).ToDictionary(g => g.Key, g => new
            {
                records = g.Count(), withWitness = g.Count(w => w.Report.AnyWitness),
                withWitnessOnLostGround = g.Count(w => w.Report.AnyOnLostGround),
                stranded = g.Count(w => w.Report.Stranded.Any()),
                strandedOversight = g.Count(w => w.Report.Stranded.Any(r => r.Authorship == WitnessAuthorship.Oversight)),
            }),
            rowsByKindAndVerdict = rows.GroupBy(x => x.Row.Kind.ToString()).ToDictionary(g => g.Key,
                g => g.GroupBy(x => x.Row.Verdict.ToString()).ToDictionary(h => h.Key, h => h.Count())),
            unjudgeableByReason = rows.Where(x => x.Row.Verdict == WitnessVerdict.Unjudgeable)
                                      .GroupBy(x => x.Row.Reason.ToString()).ToDictionary(g => g.Key, g => g.Count()),
            elevatedAnchors = rows.Count(x => x.Row.ElevatedStrand),
            w2BlindBand = new
            {
                count = blind.Count,
                coveredW2 = Rows(WitnessKind.PlacedActor, WitnessVerdict.Covered),
                byDistance = new Dictionary<string, int>
                {
                    [$"0-{ReachClearance / 4:0}u"] = Blind(0, ReachClearance / 4),
                    [$"{ReachClearance / 4:0}-{ReachClearance / 2:0}u"] = Blind(ReachClearance / 4, ReachClearance / 2),
                    [$"{ReachClearance / 2:0}-{ReachClearance:0}u"] = Blind(ReachClearance / 2, ReachClearance),
                },
            },
        };
    }

    if (reachJsonPath is not null)
    {
        File.WriteAllText(reachJsonPath, System.Text.Json.JsonSerializer.Serialize(new
        {
            probe = census is null ? "navownprobe --reach-candidates" : "navownprobe --reach-witness",
            instance = Instance,
            clearance = ReachClearance,
            spacing = ReachSpacing,
            generated = DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm:ssZ"),
            witnessSummary,
            records = jsonOut,
        }, new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
        Console.WriteLine($"   candidate worklist -> {reachJsonPath}");
    }
}

// JSON cannot carry NaN or infinity; null means "no number" and the string means unbounded.
static object? Num(float f) => float.IsNaN(f) ? null : float.IsPositiveInfinity(f) ? "inf" : f;

// ---------------------------------------------------------------------------------------
// DELETED / EMPTIED — the hard break.
// ---------------------------------------------------------------------------------------
var deletedAtWinner = navVersions.DeletedAtWinner();
if (WinStateDump.Asked)
    WinStateDump.Write("navownprobe-navdeleted", new
    {
        deleted = deletedAtWinner.OrderBy(d => d.Key.ToString(), StringComparer.Ordinal)
                                 .Select(d => new { formKey = d.Key.ToString(), plugin = pluginKey[d.Value].FileName.String }).ToList(),
    });
// A navmesh a Bethesda master deletes is the shipped game, not a clash between mods: counted apart, never a finding
// (2026-09-28); --list-master-deletions lists them after the findings, labelled (NavDeletions.cs).
foreach (var line in NavDeletions.Report(navVersions, Wanted, p => Vanilla.Contains(pluginKey[p].FileName.String),
                                         p => pluginKey[p].FileName.String, Cell, listN, cli.ListMasterDeletions))
    Console.WriteLine(line);

// ---------------------------------------------------------------------------------------
// DANGLING LINKS — a navmesh index pointing at a triangle that the winning record does not have.
// ---------------------------------------------------------------------------------------
// Validated against the real world before it was written: Skyrim's "Navigation Door Link" errors
// and broken door teleports are a known, reported failure class, and the xEdit issue tracker has an
// open report that xEdit mis-reports it for OVERRIDDEN doors (TES5Edit#1265). That is exactly the
// case a winning-state resolver gets right and a per-plugin view cannot: the index has to be checked
// against the navmesh the GAME will load, not the one the plugin shipped with.
//
// Three links, all exact, no thresholds:
//   navmesh DoorTriangle -> a triangle index in ITSELF, plus a door reference that must still exist
//   placed door          -> a navmesh and a triangle index in it
//   navmesh EdgeLink     -> ANOTHER navmesh and a triangle index in that one (cross-cell pathing)
int NavOwner(FormKey nav) => navProviders.TryGetValue(nav, out var pr) && pr.Count > 0 ? pr[^1] : -1;
var found = links.Find(NavOwner, nav => Cell(navCell.GetValueOrDefault(nav)), reportDoorIdx);
var dangling = found.Lines;
var dangleStruct = found.Rows;
int doorTriChecked = found.DoorTriChecked, doorLinkChecked = found.DoorLinkChecked, edgeChecked = found.EdgeChecked;
int doorIdxSuppressed = found.DoorIdxSuppressed;

// Who, in this load order, already overrides the record carrying the bad index? A dangling link
// names one party, so the patch verifier has no master pair to look up; this is the record-level
// answer to the same question, and it is the shortlist of plugins a fix could live in.
string Overriders(FormKey rec, string role)
{
    var l = role == "navmesh"
        ? navProviders.GetValueOrDefault(rec)
        : refOverriders.GetValueOrDefault(rec);
    if (l is null || l.Count == 0) return "(nothing in this order provides it)";
    return string.Join(" -> ", l.Select(i => pluginKey[i].FileName.String));
}

Console.WriteLine();
Console.WriteLine($"==== DANGLING NAVMESH LINKS (exact; no thresholds) ====");
Console.WriteLine($"   checked: {doorTriChecked:N0} navmesh door triangles, {doorLinkChecked:N0} door->navmesh links, {edgeChecked:N0} edge links");
Console.WriteLine($"   (doors whose winning override removed the link or permanently disabled them are out of scope;");
Console.WriteLine($"    {links.PermanentlyDisabledDoors:N0} permanently-disabled door overrides seen — counted per rule 6)");
Console.WriteLine($"   findings: {dangling.Count:N0}");
if (doorIdxSuppressed > 0 || reportDoorIdx)
{
    Console.WriteLine($"   DOOR-IDX is RETIRED — {doorIdxSuppressed:N0} link(s)"
                    + $" {(reportDoorIdx ? "RE-ENABLED by --door-idx and INCLUDED above" : "found and NOT counted above")}."
                    + $" Field-tested as not player-visible: it reads only the"
                    + $" door ref's own link, while the navmesh's door list and the coarse routing index — which"
                    + $" are what the engine walks — stay healthy. See the comment at the check.");
}
foreach (var g in dangling.GroupBy(d => d.Trim().Split(' ')[0]).OrderByDescending(g => g.Count()))
    Console.WriteLine($"      {g.Count(),6}  {g.Key}");
foreach (var d in dangling.Take(listN)) Console.WriteLine(d);

// ⚠️ NAMING ONE END IS NOT ENOUGH, and the first version of this made that mistake. A dangling
// link has two ends: the record carrying the bad INDEX, and the record it points INTO. Printing
// only the index end said "33 of these are provided by Skyrim.esm alone", which reads as "vanilla's
// own fault" — and that is not what it means. Vanilla's index dangles the moment a MOD re-cuts the
// navmesh it points into. Classifying both ends separates Bethesda's permanent floor from the
// cross-plugin breakage this project exists to find, which is method rule 1 applied to a check that
// had quietly gone absolute.
bool ModTouched(FormKey rec, string role)
{
    var l = role == "navmesh" ? navProviders.GetValueOrDefault(rec) : refOverriders.GetValueOrDefault(rec);
    // the plugin that deleted a navmesh touched it too: a mod's deletion of a vanilla navmesh is not Bethesda's floor
    if (role == "navmesh" && links.Deleter.TryGetValue(rec, out var deleter) && !Vanilla.Contains(pluginKey[deleter].FileName.String)) return true;
    return DanglingLinks.ModTouched(l, i => Vanilla.Contains(pluginKey[i].FileName.String));
}

Console.WriteLine();
Console.WriteLine($"   ---- the two ends of each dangling link, split by population ----");
var byClass = dangleStruct.GroupBy(d =>
    DetectorModel.ClassifyDangle(ModTouched(d.Rec, d.RecRole), ModTouched(d.Other, d.OtherRole)) switch
    {
        DanglePopulation.BethesdaFloor  => "A. neither end touched by a mod   -> Bethesda's own, permanent FLOOR",
        DanglePopulation.ModBrokeTarget => "B. a mod re-cut the TARGET, breaking a vanilla index   -> cross-plugin",
        DanglePopulation.ModWroteIndex  => "C. a mod wrote the INDEX, target is vanilla            -> cross-plugin",
        _                               => "D. mods on both ends                                   -> cross-plugin",
    }).OrderBy(g => g.Key).ToList();
foreach (var g in byClass) Console.WriteLine($"   {g.Count(),4}  {g.Key}");
Console.WriteLine($"   Only B, C and D are findings. A is the floor, and it is measured rather than assumed.");

Console.WriteLine();
Console.WriteLine($"   ---- who already overrides the record carrying each bad index (B/C/D only) ----");
Console.WriteLine($"   (a dangling link names one party, so there is no master pair to look a patch up against;");
Console.WriteLine($"    this is the record-level answer, and the shortlist of plugins a fix could live in)");
foreach (var g in dangleStruct.Where(d => ModTouched(d.Rec, d.RecRole) || ModTouched(d.Other, d.OtherRole))
                              .GroupBy(d => $"{Overriders(d.Rec, d.RecRole)}   ==> into: {Overriders(d.Other, d.OtherRole)}")
                              .OrderByDescending(g => g.Count()).Take(listN))
    Console.WriteLine($"   {g.Count(),4}  {g.Key}");

// ---------------------------------------------------------------------------------------
// Is this base record big enough to obstruct anything?
// ---------------------------------------------------------------------------------------
// Counting every Static floods the result with candles and light fixtures, which a lighting mod
// places by the thousand. Type is necessary and nowhere near sufficient. Measured geometry per
// the standing rule (OBND is not ground truth); OBND only when no mesh resolves, and counted so the
// fallback's share is visible rather than blended in.
float MinFootprint = 96f, MinHeight = 64f;
var sizeCache = new Dictionary<FormKey, bool?>();
int measured = 0, fellBack = 0, unknown = 0;

bool IsBlocker(FormKey baseKey)
{
    if (sizeCache.TryGetValue(baseKey, out var hit)) return hit ?? false;

    Vec3? size = null;
    if (baseMesh.TryGetValue(baseKey, out var rel))
    {
        var full = rel.StartsWith("meshes", StringComparison.OrdinalIgnoreCase) ? rel : Path.Combine("meshes", rel);
        var res = assets.ResolveForPlacement(full);
        if (res.Sources.Count > 0)
        {
            var (bytes, _) = AssetResolver.ReadPlacementSource(res.Sources[0]);
            if (bytes is not null)
            {
                var o = NifGeometry.Measure(bytes);
                if (o.Model is { Bounds.IsEmpty: false } mdl) { size = mdl.Bounds.Size; measured++; }
            }
        }
    }
    if (size is null && baseObnd.TryGetValue(baseKey, out var ob)) { size = ob; fellBack++; }
    if (size is null) { unknown++; sizeCache[baseKey] = false; return false; }

    var v = size.Value;
    bool blocker = MathF.Max(v.X, v.Y) >= MinFootprint && v.Z >= MinHeight;
    sizeCache[baseKey] = blocker;
    return blocker;
}

// ---------------------------------------------------------------------------------------
// UNCUT — objects introduced after the last word on the navmesh.
// ---------------------------------------------------------------------------------------
// Not "these objects block the navmesh" — that question has no answer worth reading. This is the
// weaker, exact claim: nobody re-cut the navmesh after these objects existed, so nobody can have
// accounted for them. It is a statement about who did what, in what order, not about space.
var uncut = new List<(FormKey Cell, int Plugin, int Refs, string NavOwners)>();
foreach (var ((cell, plugin), bag) in cellNewRefs)
{
    if (!Wanted(plugin)) continue;
    int n = bag.Count(IsBlocker);
    if (n == 0) continue;
    if (!cellNavPlugins.TryGetValue(cell, out var navPlugins) || navPlugins.Count == 0) continue;
    if (navPlugins.Contains(plugin)) continue;                       // it navmeshed its own work
    // A later navmesh edit only accounts for these objects if that plugin could SEE them, which
    // means it masters the plugin that introduced them. A later edit by a stranger saw nothing.
    if (navPlugins.Any(np => np > plugin && masterClosure[np].Contains(plugin))) continue;
    uncut.Add((cell, plugin, n, string.Join("+", navPlugins.Select(np => pluginKey[np].FileName))));
}

Console.WriteLine($"\n==== UNCUT: obstructing refs introduced after the navmesh's last word ====");
Console.WriteLine($"   cell/plugin pairs : {uncut.Count:N0}   ({uncut.Sum(u => u.Refs):N0} references)");
Console.WriteLine($"   size source: {measured:N0} base records measured from the mesh, {fellBack:N0} from OBND (fallback), {unknown:N0} unresolvable");
Console.WriteLine($"   blocker = footprint >= {MinFootprint:0}u and height >= {MinHeight:0}u");
foreach (var g in uncut.GroupBy(u => pluginKey[u.Plugin].FileName).OrderByDescending(g => g.Sum(u => u.Refs)).Take(listN))
    Console.WriteLine($"   {g.Sum(u => u.Refs),6} refs in {g.Count(),4} cells   {g.Key}");

Console.WriteLine($"\n   ---- worst individual cells ----");
foreach (var u in uncut.OrderByDescending(u => u.Refs).Take(listN))
    Console.WriteLine($"   {u.Refs,5} refs  {pluginKey[u.Plugin].FileName,-46} into {Cell(u.Cell),-34} navmesh owned by {u.NavOwners}");

// ---------------------------------------------------------------------------------------
// BORDER LINKS BY THEIR GEOMETRY and NAVMESH LEFT ON MOVED GROUND (NavBorderLinks.cs, NavGround.cs; 2026-11-16).
// ---------------------------------------------------------------------------------------
var edgeTol = cli.EdgeTolerance is { } et ? EdgeTolerance.Parse(et) : NavRuleDefaults.Edge;
var border = NavBorderLinks.Find(winNav.ToDictionary(kv => kv.Key, kv => kv.Value.W), edgeTol);
var ground = NavGround.Find(winNav, landIndex, masterClosure, NavRuleDefaults.GroundBand, NavRuleDefaults.GroundMove,
                            NavRuleDefaults.GroundMinVertices, census: cli.FloorCensusPath is not null);
bool BorderFinding(NavBorderLinks.Row r) => ModTouched(r.Src, "navmesh") || ModTouched(r.Target, "navmesh");
bool GroundFinding(NavGround.Row r) => !Vanilla.Contains(pluginKey[r.Owner].FileName.String) || !Vanilla.Contains(pluginKey[r.LandWinner].FileName.String);
var borderFindings = border.Rows.Where(BorderFinding).ToList();
var groundFindings = ground.Rows.Where(GroundFinding).ToList();
Console.WriteLine("\n==== BORDER LINKS BY THEIR GEOMETRY (tolerance " + edgeTol + ") ====");
Console.WriteLine($"   judged: {border.Judged.Count:N0} cross-navmesh links to an existing triangle; {border.Judged.Count(j => j.Matched):N0} matched, "
                + $"{border.Judged.Count(j => j.Matched && j.Reciprocal):N0} two-way; {border.LinkFieldPastTable:N0} link fields past their table (not judged)");
Console.WriteLine($"   findings: {borderFindings.Count:N0} ({borderFindings.Count(r => r.Kind == "EDGE-NOMATCH"):N0} EDGE-NOMATCH, "
                + $"{borderFindings.Count(r => r.Kind == "EDGE-ONEWAY"):N0} EDGE-ONEWAY); floor (both ends vanilla): {border.Rows.Count - borderFindings.Count:N0}");
foreach (var r in borderFindings.Take(listN)) Console.WriteLine($"   {r.Kind,-12} navmesh {r.Src} in {Cell(navCell.GetValueOrDefault(r.Src))} {r.Why}");
Console.WriteLine($"\n==== NAVMESH LEFT ON GROUND ANOTHER PLUGIN MOVED (band {ground.Band:0} u, move {ground.Move:0} u, at least {ground.MinVertices} vertices) ====");
Console.WriteLine($"   judged: {ground.VerticesJudged:N0} exterior vertices; cannot tell (terrain diagonal) {ground.CannotTell:N0}; "
                + $"no LAND {ground.NoLand:N0}; no authored ground {ground.NoAuthoredGround:N0}");
Console.WriteLine($"   findings: {groundFindings.Count:N0}; floor (vanilla owner and vanilla ground): {ground.Rows.Count - groundFindings.Count:N0}");
foreach (var r in groundFindings.Take(listN))
    Console.WriteLine($"   NAV-GROUND   navmesh {r.Nav} in {Cell(r.Cell)} by {pluginKey[r.Owner].FileName}: {r.Counted} of {r.Vertices} vertices on ground {pluginKey[r.LandWinner].FileName} moved up to {r.MaxMove:0} u");
if (cli.FloorCensusPath is { } censusPath)
{
    var census = new
    {
        tolerance = new { plan = edgeTol.Plan, height = edgeTol.Height },
        links = border.Judged.Select(j => new { src = j.Src.ToString(), tri = j.Tri, edge = j.Edge, target = j.Target.ToString(), targetTri = j.TargetTri,
                                               overlap = float.IsFinite(j.Gap.Plan) ? j.Gap.Overlap : 0, plan = float.IsFinite(j.Gap.Plan) ? j.Gap.Plan : (float?)null,
                                               height = float.IsFinite(j.Gap.Height) ? j.Gap.Height : (float?)null, matched = j.Matched, reciprocal = j.Reciprocal,
                                               floor = !ModTouched(j.Src, "navmesh") && !ModTouched(j.Target, "navmesh") }),
        groundHeights = ground.OnOwnGround,
        groundRows = ground.Rows.Select(r => new { nav = r.Nav.ToString(), owner = pluginKey[r.Owner].FileName.String, land = pluginKey[r.LandWinner].FileName.String,
                                                   counted = r.Counted, vertices = r.Vertices, maxMove = r.MaxMove }),
        borderRows = border.Rows.Select(r => new { kind = r.Kind, src = r.Src.ToString(), tri = r.SrcTri, target = r.Target.ToString(), targetTri = r.TargetTri,
                                                   plan = float.IsFinite(r.Gap.Plan) ? r.Gap.Plan : (float?)null, why = r.Why, floor = !BorderFinding(r) }),
        candidates = border.Candidates.Select(kv => new { src = kv.Key.Src.ToString(), target = kv.Key.Target.ToString(), tri = kv.Key.Tri,
                                                         found = kv.Value.All.Select(c => new { nav = c.Nav.ToString(), tri = c.Tri, plan = c.Gap.Plan }) }),
    };
    File.WriteAllText(censusPath, System.Text.Json.JsonSerializer.Serialize(census));
    Console.WriteLine($"   floor census -> {censusPath}");
}

// ---------------------------------------------------------------------------------------
// --emit — hand the exact broken records to the patch verifier.
// ---------------------------------------------------------------------------------------
// Only the two EXACT checks are emitted. UNCUT is deliberately excluded: it is baseline-limited
// (official Bethesda content trips it 5,100 times) and is ranking, not a finding, so feeding it to
// a verifier would dress a ranking up as a defect.
// Every record carries its PHYSICAL predicate under this arm (ClobberPredicate / DanglePredicate above); with --base,
// every record of the named emit is re-measured under this arm too, so conflictdiff can credit a vanished row as
// repaired only when the predicate went away (2026-09-03).
if (emitPath is not null)
{
    var conflicts = new List<Conflict>();

    // The clobber predicate needs each finding's two versions in memory — the reach pass's plan, for the findings only.
    var clobberPlan = new Dictionary<int, HashSet<FormKey>>();
    foreach (var c in clobberFindings) { PlanInto(clobberPlan, c.Loser, c.Nav); PlanInto(clobberPlan, c.Winner, c.Nav); }
    var (clobberGeom, clobberUnreadable) = LoadNavGeometry(clobberPlan);
    var predicateTally = new Dictionary<PhysicalState, int>();

    // Floor stays out of the handoff. Feeding class-A records (Bethesda on both ends) to the
    // verifier dressed 28 dangles and 3 clobber records up as defects for a whole session —
    // the same reasoning that already kept UNCUT out of --emit.
    foreach (var g in clobberFindings.GroupBy(c => (c.Loser, c.Winner)))
    {
        var loser = pluginKey[g.Key.Loser].FileName.String;
        var winner = pluginKey[g.Key.Winner].FileName.String;
        conflicts.Add(new Conflict(
            "CLOBBERED", loser, winner,
            $"{g.Count()} navmesh record(s) re-cut by {loser} and then re-cut again by {winner}, which declares no dependency on it",
            g.Select(c =>
            {
                var (st, why) = ClobberPredicate(c.Nav, c.Loser, c.Winner, clobberGeom);
                predicateTally[st] = predicateTally.GetValueOrDefault(st) + 1;
                return new ConflictRecord(c.Nav.ToString(), "navmesh",
                              $"{Cell(navCell.GetValueOrDefault(c.Nav))}  {c.LoserT} tris -> {c.WinnerT}", st, why);
            }).ToList()));
    }

    int floorDangles = dangleStruct.Count(d => !ModTouched(d.Rec, d.RecRole) && !ModTouched(d.Other, d.OtherRole));
    foreach (var d in dangleStruct.Where(d => DanglingLinks.IsFinding(ModTouched(d.Rec, d.RecRole), ModTouched(d.Other, d.OtherRole))))
    {
        // A fix has to override the record carrying the bad index. The other end is context: a
        // patch that only re-states the target navmesh has not repaired the pointer into it.
        var (st, why) = DanglePredicate(d.Kind, d.Rec, d.Other, d.Tri);
        var necessary = new List<ConflictRecord> { new(d.Rec.ToString(), d.RecRole, Cell(navCell.GetValueOrDefault(d.Rec)), st, why) };
        // For an unpaired finding this chain IS the deliverable: it names every plugin that already
        // touches the broken record, which is where a fix would have to go.
        var chain = Overriders(d.Rec, d.RecRole);
        // An EDGE-IDX row's other end names the target triangle: the row is that one link (ConflictIo.RowIdentity's part).
        var other = new List<ConflictRecord> { new(d.Other.ToString(), d.OtherRole, null, Triangle: d.Tri) };
        string cand = "";
        NavBorderLinks.CandidateResult? cr = null;
        if (d.Kind == "EDGE-IDX") border.Candidates.TryGetValue((d.Rec, d.Other, d.Tri ?? -1), out cr);
        else if (d.Kind == "EDGE-NAV") cr = border.Candidates.Where(kv => kv.Key.Src == d.Rec && kv.Key.Target == d.Other).Select(kv => kv.Value).FirstOrDefault();
        if (cr is not null)
        {
            cand = "; " + cr.Sentence(d.Other);
            if (cr.Unique) other.Add(new ConflictRecord(cr.All[0].Nav.ToString(), "candidate", null, Triangle: cr.All[0].Tri));
        }
        conflicts.Add(new Conflict(
            d.Kind,
            d.PluginA >= 0 ? pluginKey[d.PluginA].FileName.String : "",
            d.PluginB >= 0 ? pluginKey[d.PluginB].FileName.String : "",
            d.Summary + cand + $"   [record provided by: {chain}]", necessary, other));
    }

    // EDGE-NOMATCH / EDGE-ONEWAY rows: A owns the linking navmesh, B the far one; the far end names the triangle (the
    // row's part, as EDGE-IDX's), and a NOMATCH row's unique candidate rides as a second record.
    foreach (var r in borderFindings)
    {
        var why = r.Kind == "EDGE-NOMATCH" ? $"no edge of the far triangle lies along this one within {edgeTol}" : "the far triangle does not link back";
        var other = new List<ConflictRecord> { new(r.Target.ToString(), "navmesh", null, Triangle: r.TargetTri) };
        if (r.Kind == "EDGE-NOMATCH" && border.Candidates.TryGetValue((r.Src, r.Target, r.TargetTri), out var cr) && cr.Unique)
            other.Add(new ConflictRecord(cr.All[0].Nav.ToString(), "candidate", null, Triangle: cr.All[0].Tri));
        int oa = NavOwner(r.Src), ob = NavOwner(r.Target);
        conflicts.Add(new Conflict(r.Kind, oa >= 0 ? pluginKey[oa].FileName.String : "", ob >= 0 ? pluginKey[ob].FileName.String : "",
            $"navmesh {r.Src} in {Cell(navCell.GetValueOrDefault(r.Src))} {r.Why}",
            new List<ConflictRecord> { new(r.Src.ToString(), "navmesh", Cell(navCell.GetValueOrDefault(r.Src)), PhysicalState.Holds, why) }, other));
    }
    // NAV-GROUND rows: A owns the navmesh, B wins the ground under it.
    foreach (var r in groundFindings)
        conflicts.Add(new Conflict("NAV-GROUND", pluginKey[r.Owner].FileName.String, pluginKey[r.LandWinner].FileName.String,
            $"navmesh {r.Nav} in {Cell(r.Cell)}: {r.Counted} of {r.Vertices} vertices sat on the ground its author saw, which {pluginKey[r.LandWinner].FileName} moved up to {r.MaxMove:0} u",
            new List<ConflictRecord> { new(r.Nav.ToString(), "navmesh", Cell(r.Cell), PhysicalState.Holds,
                $"{r.Counted} vertices within {ground.Band:0} u of the author's ground and more than {ground.Band:0} u off the winning ground") }));

    // DELETED / EMPTIED rows (2026-11-16): the section's findings, one row each, with the deleting (or emptying) plugin as A
    // and the last earlier plugin whose version had triangles as B. The shipped game's deletions stay out, counted.
    var (delFindings, delShipped) = NavDeletions.Find(navVersions, Wanted, p => Vanilla.Contains(pluginKey[p].FileName.String));
    foreach (var r in delFindings)
    {
        var (st, why) = NavDeletions.Predicate(r.Kind, r.Nav, navVersions);
        string by = pluginKey[r.By].FileName.String, good = r.LastGood >= 0 ? pluginKey[r.LastGood].FileName.String : "";
        string what = r.Kind == "EMPTIED" ? $"navmesh {r.Nav} in {Cell(r.Cell)} has no triangles in {by}'s version ({r.FirstT} before)"
                    : $"navmesh {r.Nav} in {Cell(r.Cell)} is deleted by {by}" + (good.Length > 0 ? $"; the last version with geometry is {good}'s" : "; no version in the order has geometry");
        conflicts.Add(new Conflict(r.Kind, by, good, what,
            new List<ConflictRecord> { new(r.Nav.ToString(), "navmesh", Cell(r.Cell), st, why) }));
    }

    // --base: every record of the base emit, re-measured under THIS arm. The loser of a clobber row is the base row's;
    // the winner is whoever wins the record here. A dangling link is re-asked of this arm's winning state.
    List<Predicate>? predicates = null;
    int baseUnreadable = 0;
    if (basePath is not null)
    {
        var baseFile = ConflictIo.Read(basePath);
        predicates = new List<Predicate>();
        var basePlan = new Dictionary<int, HashSet<FormKey>>();
        var pending = new List<(Conflict C, ConflictRecord R, FormKey Nav, int Loser, int Winner)>();
        foreach (var c in baseFile.Conflicts)
        {
            if (c.Kind == "CLOBBERED")
            {
                int loser = Array.FindIndex(pluginKey, k => string.Equals(k.FileName.String, c.A, StringComparison.OrdinalIgnoreCase));
                foreach (var r in c.Necessary)
                {
                    var nav = FormKey.Factory(r.FormKey);
                    var provs = navProviders.GetValueOrDefault(nav);
                    int winner = provs is { Count: > 0 } ? provs[^1] : -1;
                    if (loser < 0) predicates.Add(new Predicate(c.Kind, c.A, c.B, r.FormKey, null, PhysicalState.Unmeasured, "the loser plugin is not loaded in this order"));
                    else if (winner < 0) predicates.Add(new Predicate(c.Kind, c.A, c.B, r.FormKey, null, PhysicalState.Unmeasured, "no plugin in this order provides the record"));
                    else if (!provs!.Contains(loser)) predicates.Add(new Predicate(c.Kind, c.A, c.B, r.FormKey, null, PhysicalState.Unmeasured, "the loser does not provide this record in this order"));
                    else { PlanInto(basePlan, loser, nav); PlanInto(basePlan, winner, nav); pending.Add((c, r, nav, loser, winner)); }
                }
            }
            else if (c.Kind is "EDGE-NOMATCH" or "EDGE-ONEWAY" or "NAV-GROUND")
            {
                string? other = c.Alternative is { Count: > 0 } alt ? alt[0].FormKey : null;
                int? otherTri = c.Alternative is { Count: > 0 } alt2 ? alt2[0].Triangle : null;
                foreach (var r in c.Necessary)
                {
                    var st = c.Kind == "NAV-GROUND" ? ground.Predicate(FormKey.Factory(r.FormKey))
                           : other is null ? PhysicalState.Unmeasured : border.Predicate(c.Kind, FormKey.Factory(r.FormKey), FormKey.Factory(other), otherTri);
                    predicates.Add(new Predicate(c.Kind, c.A, c.B, r.FormKey, other, st,
                        st == PhysicalState.Holds ? "the class still holds under this order" : "the class no longer holds under this order", otherTri));
                }
            }
            else if (c.Kind is "DELETED" or "EMPTIED")
            {
                foreach (var r in c.Necessary)
                {
                    var (st, why) = NavDeletions.Predicate(c.Kind, FormKey.Factory(r.FormKey), navVersions);
                    predicates.Add(new Predicate(c.Kind, c.A, c.B, r.FormKey, null, st, why));
                }
            }
            else
            {
                string? other = c.Alternative is { Count: > 0 } alt ? alt[0].FormKey : null;
                int? otherTri = c.Alternative is { Count: > 0 } alt2 ? alt2[0].Triangle : null;
                foreach (var r in c.Necessary)
                {
                    if (other is null) { predicates.Add(new Predicate(c.Kind, c.A, c.B, r.FormKey, null, PhysicalState.Unmeasured, "the base row names no second record to re-ask the link of")); continue; }
                    var (st, why) = DanglePredicate(c.Kind, FormKey.Factory(r.FormKey), FormKey.Factory(other), otherTri);
                    predicates.Add(new Predicate(c.Kind, c.A, c.B, r.FormKey, other, st, why, otherTri));
                }
            }
        }
        var (baseGeom, unreadableBase) = LoadNavGeometry(basePlan);
        baseUnreadable = unreadableBase;
        foreach (var (c, r, nav, loser, winner) in pending)
        {
            var (st, why) = ClobberPredicate(nav, loser, winner, baseGeom);
            predicates.Add(new Predicate(c.Kind, c.A, c.B, r.FormKey, null, st, why));
        }
        Console.WriteLine($"\n   --base {Path.GetFileName(basePath)}: {predicates.Count:N0} base-row predicate(s) re-measured under this order — "
                        + string.Join(", ", predicates.GroupBy(p => p.Physical).OrderBy(g => g.Key).Select(g => $"{g.Key} {g.Count():N0}"))
                        + (baseUnreadable > 0 ? $"; {baseUnreadable} wanted extraction(s) unreadable" : ""));
    }

    var coverage = new List<CoverageLine>
    {
        new("plugins", paths.Count - unreadablePlugins.Count, new Dictionary<string, long> { ["UnreadablePlugin"] = unreadablePlugins.Count }),
        new("navmesh records (clobber test)", navProviders.Count, new Dictionary<string, long>(), null,
            $"{clobber.Count:N0} clobbered, {clobber.Count(c => c.Material):N0} material, {clobberFindings.Count:N0} findings (class A withheld)"),
        new("clobber predicates (findings)", predicateTally.GetValueOrDefault(PhysicalState.Holds) + predicateTally.GetValueOrDefault(PhysicalState.Gone),
            new Dictionary<string, long> { ["GeometryUnreadable"] = predicateTally.GetValueOrDefault(PhysicalState.Unmeasured) }, null,
            $"Holds {predicateTally.GetValueOrDefault(PhysicalState.Holds):N0}, Gone {predicateTally.GetValueOrDefault(PhysicalState.Gone):N0} (a Gone row: a declared-dependency clobber with no coverage loss beyond {ReachClearance:0}u)"),
        new("border links (geometry)", border.Judged.Count, new Dictionary<string, long> { ["LinkFieldPastTable"] = border.LinkFieldPastTable },
            new Dictionary<string, long> { ["FloorRows"] = border.Rows.Count - borderFindings.Count },
            $"tolerance {edgeTol}; {border.Judged.Count(j => j.Matched):N0} matched, {border.Judged.Count(j => j.Matched && j.Reciprocal):N0} two-way"),
        new("navmesh vertices on LAND (ground)", ground.VerticesJudged,
            new Dictionary<string, long> { ["CannotTell"] = ground.CannotTell, ["NoAuthoredGround"] = ground.NoAuthoredGround, ["NoLand"] = ground.NoLand },
            new Dictionary<string, long> { ["FloorRows"] = ground.Rows.Count - groundFindings.Count, ["LandUnreadablePlugins"] = landUnreadable.Count },
            $"band {ground.Band:0} u, move {ground.Move:0} u, at least {ground.MinVertices} vertices"),
        new("navmesh deletions", delFindings.Count, new Dictionary<string, long>(),
            new Dictionary<string, long> { ["ShippedGame"] = delShipped.Count },
            $"{delFindings.Count(r => r.Kind == "DELETED"):N0} deleted and {delFindings.Count(r => r.Kind == "EMPTIED"):N0} emptied by a mod; the shipped game's deletions are counted, not rows"),
        new("dangling-link checks", doorTriChecked + doorLinkChecked + edgeChecked, new Dictionary<string, long>(),
            new Dictionary<string, long> { ["PermanentlyDisabledDoor"] = links.PermanentlyDisabledDoors, ["DoorIdxRetired"] = doorIdxSuppressed, ["FloorDangles"] = floorDangles }),
        // The physical population this run judged, with the chains it could not read to the end counted as
        // unjudgeable — conflictdiff's coverage test must be able to see that share rise (Q27).
        new("obstructing refs (UNCUT population)", cellNewRefs.Values.Sum(b => b.Count),
            new Dictionary<string, long>(pop.Unjudgeable),
            new Dictionary<string, long> { ["NotPresentAtLoad"] = uncutSkippedAbsent, ["NonObstructingType"] = uncutSkippedType },
            $"population {pop.Identity} / state {pop.StateName}"),
    };
    coverage.AddRange(coverageExtra);

    var identity = cfg.Identity(pop.Identity, pop.StateName);
    ConflictIo.Write(emitPath, new ConflictFile("navownprobe", Instance,
        DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm:ssZ"), conflicts, coverage, predicates, basePath,
        pop.Identity, pop.StateName, identity));
    Console.WriteLine($"configuration: {identity.Short}");
    Console.WriteLine($"\nemitted {conflicts.Count:N0} conflicts ({conflicts.Count(c => c.B.Length == 0)} with no second party to name;"
                    + $" {floorDangles} class-A dangles and the class-A clobbers withheld as floor;"
                    + $" clobber predicates Holds {predicateTally.GetValueOrDefault(PhysicalState.Holds)} / Gone {predicateTally.GetValueOrDefault(PhysicalState.Gone)} / Unmeasured {predicateTally.GetValueOrDefault(PhysicalState.Unmeasured)}) -> {emitPath}");
}

Console.WriteLine($"\nelapsed {sw.Elapsed.TotalSeconds:0.0}s");
return 0;
