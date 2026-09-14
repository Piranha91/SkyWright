using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
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
// The original cut used enclosing-box depth/fraction gates. Those are historical diagnostics only.
// This cut asks whether the actual placed strict render surfaces intersect and who created that relation:
// for every cross-responsible actual-shape hit, with L the LATER of the two
// responsible plugins, rebuild the arm WITHOUT L's contribution (L's reference at its previous snap
// in the placement history, or absent) — no overlap there means L INTRODUCED it, overlap already
// there means L INHERITED it. And was the other reference's winning placement set by L's own masters
// (SEEN — an author's choice) or outside its closure (BLIND — it could not have been in the editor)?
// The class is Introduced + Blind after an actual-shape intersection. Surface relation is retained as
// evidence and does not become a severity score. Provenance remains DetectorModel.OverlapProvenance;
// _shared/OverlapIndex.cs, over PlacementHistory — the same history driftprobe and depprobe run on.
//
// "Responsible" = the plugin that last CHANGED a placement (position, cell, or enable/delete flag) —
// PlacementHistory's changed-only rule. An override that forwards a position unchanged takes no
// responsibility; one that changes only rotation or scale is not a step either, and is COUNTED.
//
// Boundary (the standing rules, applied):
//   - Geometry is complete decoded visible render vertices from the winning VFS NIF. Record OBND and stored NIF
//     spheres have zero influence; unavailable geometry = not judged, counted (rule 6). Editor markers are set aside.
//   - Declared dependency is the transitive closure (DetectorModel.MasterClosure); Seen IS that
//     relation read from the later side, and the run checks the two counts agree.
//   - MeshIntersection symmetrically distinguishes crossing, coplanar area overlap, boundary touch,
//     separation and validated solid containment. Bounds only reject provably separated soups.
//   - These relations are geometry evidence, not compatibility severity, intent or a game verdict.
//
// usage: overlapprobe [--list N] [--surface-tolerance U] [--max-triangle-pairs N] [--pair-seconds S]
//                     [--emit path] [--base emit.json] [--dump path] [--json path]
//                     [--bos on|off] [--enable X.esp]* [--disable X.esp]* [--instance DIR] [--game DIR] [--profile NAME]
//                     [--responsible-pair A.esp B.esp]* [--historical-pairs crosswalk.json] [--geometry-manifest output.json]
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
if (args.Any(a => a is "--depth" or "--frac" or "--maxdim"))
{
    Console.Error.WriteLine("--depth, --frac and --maxdim are unsupported: actual-shape discovery has no box depth, fraction or size decision gates");
    return 3;
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
double surfaceTolerance = 1e-5;
long maxTrianglePairs = 2_000_000;
double pairSeconds = 120;
string? emitPath = null;
// --base: the emit this run will be diffed against — every record of it is re-measured under THIS order and carried in
// the emit as Predicates (the changed arm of a differential; conflictdiff reads both). See ConflictReport.cs.
string? basePath = null;
// --dump: one line per candidate (key, responsible plugin, base type, mesh, geometry source, rotation, scale,
// world box). A diagnostic for comparing two runs ref by ref — which boxes moved under a transform change,
// and what meshes they were — not part of the report. --json: every judged row, for spot-checks.
string? dumpPath = null;
string? jsonPath = null;
string? geometryManifestPath = null;
string? historicalPairsPath = null;
var responsiblePairNames = new List<(string A, string B)>();
bool bosOn = true;
bool flagsOnly = false;
var enablePlugins = new List<string>();
var disablePlugins = new List<string>();
string? orderFile = null;
for (int i = 0; i < args.Length; i++)
{
    if (args[i] == "--list" && i + 1 < args.Length) listN = int.Parse(args[++i]);
    else if (args[i] == "--surface-tolerance" && i + 1 < args.Length) surfaceTolerance = double.Parse(args[++i]);
    else if (args[i] == "--max-triangle-pairs" && i + 1 < args.Length) maxTrianglePairs = long.Parse(args[++i]);
    else if (args[i] == "--pair-seconds" && i + 1 < args.Length) pairSeconds = double.Parse(args[++i]);
    else if (args[i] == "--emit" && i + 1 < args.Length) emitPath = args[++i];
    else if (args[i] == "--base" && i + 1 < args.Length) basePath = args[++i];
    else if (args[i] == "--dump" && i + 1 < args.Length) dumpPath = args[++i];
    else if (args[i] == "--json" && i + 1 < args.Length) jsonPath = args[++i];
    else if (args[i] == "--geometry-manifest" && i + 1 < args.Length) geometryManifestPath = args[++i];
    else if (args[i] == "--historical-pairs" && i + 1 < args.Length) historicalPairsPath = args[++i];
    else if (args[i] == "--responsible-pair" && i + 2 < args.Length)
        responsiblePairNames.Add((args[++i], args[++i]));
    else if (args[i] == "--order" && i + 1 < args.Length) orderFile = args[++i];
    else if (args[i] == "--enable" && i + 1 < args.Length) enablePlugins.Add(args[++i]);
    else if (args[i] == "--disable" && i + 1 < args.Length) disablePlugins.Add(args[++i]);
    else if (args[i] == "--bos" && i + 1 < args.Length) bosOn = BosHook.ParseFlag(args[++i]) ?? throw new ArgumentException("--bos takes on|off");
    else if (args[i] == "--state" && i + 1 < args.Length) stateSpec = args[++i];
    else if (args[i] == "--switch-map" && i + 1 < args.Length) switchMapPath = args[++i];
    else if (args[i] == "--flags") flagsOnly = true;
}
if (!double.IsFinite(surfaceTolerance) || surfaceTolerance < 0 || maxTrianglePairs <= 0 || !double.IsFinite(pairSeconds) || pairSeconds <= 0)
{
    Console.Error.WriteLine("surface tolerance must be finite and nonnegative; max triangle pairs and pair seconds must be finite positive values");
    return 3;
}
PopulationMode popMode;
try { popMode = PopulationCli.Resolve("overlapprobe", flagsOnly, stateSpec); }
catch (ArgumentException ex) { Console.Error.WriteLine(ex.Message); return 2; }

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
Console.WriteLine($"load order: {paths.Count} plugins\n");
HarnessOrderCli.Print(cfg);

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

if (geometryManifestPath is not null && responsiblePairNames.Count == 0)
{
    Console.Error.WriteLine("--geometry-manifest requires at least one --responsible-pair <plugin-a> <plugin-b>; an unscoped whole-order geometry catalogue is refused");
    return 2;
}
var pluginByName = Enumerable.Range(0, paths.Count)
    .Where(p => !pluginKey[p].IsNull)
    .ToDictionary(p => PluginName(p), p => p, StringComparer.OrdinalIgnoreCase);
var responsiblePairs = new List<(int A, int B, string AName, string BName)>();
foreach (var named in responsiblePairNames)
{
    if (!pluginByName.TryGetValue(named.A, out int a) || !pluginByName.TryGetValue(named.B, out int b))
    {
        Console.Error.WriteLine($"--responsible-pair names a plugin outside the evaluated order: '{named.A}' / '{named.B}'");
        return 2;
    }
    if (a == b) { Console.Error.WriteLine("--responsible-pair requires two distinct plugins"); return 2; }
    responsiblePairs.Add(a < b ? (a, b, PluginName(a), PluginName(b)) : (b, a, PluginName(b), PluginName(a)));
}
responsiblePairs = responsiblePairs.DistinctBy(p => (p.A, p.B)).ToList();
var scopedResponsiblePlugins = responsiblePairs.SelectMany(p => new[] { p.A, p.B }).ToHashSet();
bool ScopedPair(int a, int b) => responsiblePairs.Count == 0
    || responsiblePairs.Any(p => (p.A == a && p.B == b) || (p.A == b && p.B == a));
HistoricalCrosswalk? historicalCrosswalk = null;
string? historicalCrosswalkSha256 = null;
if (historicalPairsPath is not null)
{
    byte[] crosswalkBytes;
    try { crosswalkBytes = File.ReadAllBytes(historicalPairsPath); }
    catch (Exception ex) { Console.Error.WriteLine($"could not read --historical-pairs: {ex.Message}"); return 2; }
    try
    {
        historicalCrosswalk = JsonSerializer.Deserialize<HistoricalCrosswalk>(crosswalkBytes,
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
            ?? throw new InvalidDataException("the file decoded as null");
    }
    catch (Exception ex) { Console.Error.WriteLine($"could not parse --historical-pairs: {ex.Message}"); return 2; }
    if (!historicalCrosswalk.Complete || historicalCrosswalk.Pairs is null)
    { Console.Error.WriteLine("--historical-pairs must be a complete crosswalk with pairs[]"); return 2; }
    historicalCrosswalkSha256 = Convert.ToHexString(SHA256.HashData(crosswalkBytes)).ToLowerInvariant();
}

// ---------------------------------------------------------------------------------------
// Pass 2 — strict decoded render geometry per base record. Record ObjectBounds never enter this path.
// ---------------------------------------------------------------------------------------
var geometryByBase = new Dictionary<FormKey, GeometryRead>();
var geometryByPath = new Dictionary<string, GeometryRead>(StringComparer.OrdinalIgnoreCase);
var renderCache = new NifRenderGeometryCache();
var geometryCounts = new Dictionary<string, long>(StringComparer.Ordinal);
long geometryLookupHits = 0;
long pathCacheHits = 0;

static string GeometryStatus(NifRenderGeometryStatus s) => s switch
{
    NifRenderGeometryStatus.Measured => "measured",
    NifRenderGeometryStatus.DegenerateGeometry => "degenerate_geometry",
    NifRenderGeometryStatus.MissingVertices => "missing_vertices",
    NifRenderGeometryStatus.PartialGeometry => "partial_geometry",
    NifRenderGeometryStatus.UnsupportedGeometry => "unsupported_geometry",
    NifRenderGeometryStatus.UnsupportedPose => "unsupported_pose",
    NifRenderGeometryStatus.InvalidTransform => "invalid_transform",
    NifRenderGeometryStatus.ParseError => "parse_error",
    NifRenderGeometryStatus.MissingAsset => "missing_asset",
    _ => "measurement_error",
};

GeometryRead GeometryOf(FormKey baseKey)
{
    if (geometryByBase.TryGetValue(baseKey, out var byBase)) { geometryLookupHits++; return byBase; }
    GeometryRead Finish(GeometryRead result)
    {
        geometryCounts[result.Status] = geometryCounts.GetValueOrDefault(result.Status) + 1;
        return geometryByBase[baseKey] = result;
    }
    if (!idx.BaseMesh.TryGetValue(baseKey, out var rel) || string.IsNullOrWhiteSpace(rel))
        return Finish(new("missing_asset", "the winning base record has no model path", null, null, null, null, null));

    var full = rel.StartsWith("meshes", StringComparison.OrdinalIgnoreCase) ? rel : Path.Combine("meshes", rel);
    full = full.Replace('/', '\\');
    if (geometryByPath.TryGetValue(full, out var byPath)) { pathCacheHits++; return Finish(byPath); }
    var resolution = assets.ResolveForPlacement(full);
    string pathAssetId = "asset-" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(full.ToLowerInvariant()))).ToLowerInvariant()[..20];
    GeometryRead result;
    if (resolution.Sources.Count == 0)
    {
        result = new(resolution.ReadIncomplete ? "asset_resolution_incomplete" : "missing_asset",
            resolution.ReadIncomplete
                ? "asset resolution is incomplete because at least one active archive could not be scanned"
                : "no active provider supplies the winning model path",
            full, pathAssetId, resolution, null, null);
    }
    else if (resolution.ReadIncomplete && resolution.Sources[0].Kind == AssetKind.Bsa)
    {
        result = new("asset_resolution_incomplete",
            "the apparent winner is archived while at least one active archive could not be scanned, so precedence is not established",
            full, pathAssetId, resolution, resolution.Sources[0], null);
    }
    else
    {
        var source = resolution.Sources[0];
        var (bytes, readError) = AssetResolver.ReadPlacementSource(source);
        if (bytes is null)
            result = new("asset_read_error", readError ?? "the resolved winner could not be read", full, pathAssetId,
                         resolution, source, null);
        else
        {
            var measured = renderCache.Measure(bytes, includeTriangles: true);
            result = new(GeometryStatus(measured.Status), measured.Reason, full, pathAssetId, resolution, source, measured,
                         bytes.LongLength);
        }
    }
    geometryByPath[full] = result;
    return Finish(result);
}

(TriangleSoup? Soup, char Source, string? Reason) SurfaceGeometryOf(FormKey baseKey)
{
    var geometry = GeometryOf(baseKey);
    return geometry.Outcome is { UsableForSurfaceIntersection: true, Soup: { TriangleCount: > 0 } soup }
        ? (soup, 'm', null)
        : (null, '-', geometry.Reason ?? "no complete strict render triangles");
}

// THE PHYSICAL PREDICATE of an OVERLAP row: the two winning strict render surfaces still intersect under this arm.
// Bounds can reject only provable separation inside MeshIntersection; their depth, volume and dimensions never decide.
EnableArms? resolvedArms = null;
(PhysicalState State, string Detail) OverlapPredicate(FormKey laterKey, FormKey otherKey)
{
    var sl = idx.WinningGeometryPose(laterKey); var so = idx.WinningGeometryPose(otherKey);
    if (sl is null || so is null) return (PhysicalState.Unmeasured, "a ref is not placed in this order");
    if (sl.Value.Deleted || idx.IsDeletedNow(laterKey) || so.Value.Deleted || idx.IsDeletedNow(otherKey)) return (PhysicalState.Gone, "a ref is deleted under this order");
    if (popMode != PopulationMode.Flags)
    {
        var el = resolvedArms!.Winner(laterKey); var eo = resolvedArms.Winner(otherKey);
        if (!el.Resolved || !eo.Resolved) return (PhysicalState.Unmeasured, "a ref's enable-parent chain is unresolved");
        if (!el.Enabled || !eo.Enabled) return (PhysicalState.Gone, "a ref is disabled at runtime under this state");
    }
    else if (sl.Value.Disabled || so.Value.Disabled) return (PhysicalState.Gone, "a ref is initially disabled under this order");
    if ((idx.BosSwaps.TryGetValue(laterKey, out var bosLater) && (bosLater.Random || bosLater.PerSession || bosLater.PropertiesRandom || bosLater.Alternatives.Count != 1))
     || (idx.BosSwaps.TryGetValue(otherKey, out var bosOther) && (bosOther.Random || bosOther.PerSession || bosOther.PropertiesRandom || bosOther.Alternatives.Count != 1)))
        return (PhysicalState.Unmeasured, "a ref has more than one possible BOS runtime outcome");
    if (sl.Value.Space != so.Value.Space) return (PhysicalState.Gone, "the refs are no longer in the same space");
    if (!idx.RefBase.TryGetValue(laterKey, out var bl) || !idx.RefBase.TryGetValue(otherKey, out var bo)) return (PhysicalState.Unmeasured, "a ref has no base record");
    var (modelL, srcL, whyL) = SurfaceGeometryOf(bl); var (modelO, srcO, whyO) = SurfaceGeometryOf(bo);
    if (modelL is not { TriangleCount: > 0 } || modelO is not { TriangleCount: > 0 })
        return (PhysicalState.Unmeasured, "a ref's strict decoded render geometry is unavailable");
    var worldL = SpatialOverlap.TransformSoup(modelL, sl.Value.Pos, sl.Value.Rot, sl.Value.Scale);
    var worldO = SpatialOverlap.TransformSoup(modelO, so.Value.Pos, so.Value.Rot, so.Value.Scale);
    var q = SurfaceQueryOutcome.Query(worldL, worldO, surfaceTolerance, maxTrianglePairs, TimeSpan.FromSeconds(pairSeconds));
    if (q.Intersects == true)
        return (PhysicalState.Holds, $"actual render surfaces: {q.SurfaceRelation}; containment {q.Containment}; {q.TrianglePairsTested:N0} triangle pairs [{srcL}{srcO}]");
    if (q.Intersects == false)
        return (PhysicalState.Gone, $"actual render surfaces are separated; containment {q.Containment}; {q.TrianglePairsTested:N0} triangle pairs [{srcL}{srcO}]");
    return (PhysicalState.Unmeasured, q.Reason ?? whyL ?? whyO ?? "actual render-surface query is unknown");
}

// ---------------------------------------------------------------------------------------
// Pass 3 — candidates, placed triangle soups, conservative bounds sweep, actual-shape pairs.
// ---------------------------------------------------------------------------------------
// A candidate is a winning, enabled, obstructing-typed ref whose responsible plugin is not the
// base game — vanilla-vs-mod pairs are excluded by the closure anyway (every mod masters
// Skyrim.esm), so hashing vanilla-responsible refs would only manufacture same-source pairs
// nobody asked about. DLC/CC-responsible refs stay in: DLC-on-DLC is the measured Bethesda floor.
// THE POPULATION (Q27). The default is the load-time state — the same facts the census would give, read through the
// shared arms so the default run and an explicit `--state baseline` are the same run by construction. --state needs the
// census as well, because a switch has to be looked up by name and its sets known.
var arms = new EnableArms(idx);
resolvedArms = arms;
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
bool ExcludeResponsible(int p) => responsiblePairs.Count > 0 ? !scopedResponsiblePlugins.Contains(p) : IsBaseGame(p);
long candidateGeometryLookups = 0;
__run.Stage("render-geometry", "preparing strict render geometry and candidates", total: null, unit: "geometry lookups");
(TriangleSoup? Soup, char Source, string? Reason) CandidateGeometryOf(FormKey baseKey)
{
    var result = SurfaceGeometryOf(baseKey);
    candidateGeometryLookups++;
    __run.Advance();
    return result;
}
var candidates = idx.SurfaceCandidates(CandidateGeometryOf, Obstructing, ExcludeResponsible, out var skips, enabledOf);
candidates = candidates.Where(c =>
{
    if (popMode != PopulationMode.Flags && !arms.Winner(c.Key).Resolved) { skips.EnableStateUnresolved++; return false; }
    if (idx.BosSwaps.TryGetValue(c.Key, out var bos) && (bos.Random || bos.PerSession || bos.PropertiesRandom || bos.Alternatives.Count != 1))
    { skips.BosUnresolved++; return false; }
    return true;
}).ToList();
__run.ReviseTotal(candidateGeometryLookups, "candidate source and state gates established the exact geometry lookup count");
Console.WriteLine("\n" + pop.Describe());

if (dumpPath is not null)
{
    Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(dumpPath))!);
    using var dw = new StreamWriter(dumpPath);
    dw.WriteLine("key\tresp\ttype\tmesh\tsrc\trotX\trotY\trotZ\tscale\tminX\tminY\tminZ\tmaxX\tmaxY\tmaxZ");
    foreach (var c in candidates)
        dw.WriteLine($"{c.Key}\t{PluginName(c.Win.Plugin)}\t{TypeOf(c.Base)}\t{MeshOf(c.Base)}\t{c.Src}"
                   + $"\t{c.Win.Rot.X:R}\t{c.Win.Rot.Y:R}\t{c.Win.Rot.Z:R}\t{c.Win.Scale:R}"
                   + $"\t{c.WorldBounds.Min.X:R}\t{c.WorldBounds.Min.Y:R}\t{c.WorldBounds.Min.Z:R}\t{c.WorldBounds.Max.X:R}\t{c.WorldBounds.Max.Y:R}\t{c.WorldBounds.Max.Z:R}");
    __run.Artifact(dumpPath);
    Console.WriteLine($"\ndumped {candidates.Count:N0} candidates -> {dumpPath}");
}

Console.WriteLine($"\ncandidates: {candidates.Count:N0} refs" + (responsiblePairs.Count > 0 ? " in the responsible-pair scope" : " whose responsible plugin is not the base game"));
Console.WriteLine("   geometry (strict decoded render vertices; record ObjectBounds unused): "
                + string.Join(", ", geometryCounts.OrderBy(k => k.Key).Select(k => $"{k.Key} {k.Value:N0}")));
Console.WriteLine($"   skipped (counted, per rule 6): {skips.Deleted:N0} deleted, {skips.Disabled:N0} not present at load, "
                + $"{skips.NonObstructingType:N0} non-obstructing type, {skips.NoGeometry:N0} no geometry, {skips.ExcludedResponsible:N0} base-game-responsible,");
Console.WriteLine($"                                  {skips.InvalidTransform:N0} invalid transforms, {skips.DegenerateGeometry:N0} zero-scale transforms, {skips.MarkerMesh:N0} editor markers (no rendered mesh, cannot obstruct), "
                + $"{skips.EnableStateUnresolved:N0} unresolved enable state, {skips.BosUnresolved:N0} unresolved BOS outcome");

long lastPairProgress = 0;
__run.Stage("actual-shape-queries", "querying candidate render surfaces", total: null, unit: "pairs");
var sweep = OverlapIndex.SurfacePairs(candidates, closure, surfaceTolerance, maxTrianglePairs,
    TimeSpan.FromSeconds(pairSeconds), responsiblePairs.Count > 0 ? ScopedPair : null,
    checkedPairs =>
    {
        __run.Progress(checkedPairs);
        if (checkedPairs - lastPairProgress < 250) return;
        lastPairProgress = checkedPairs;
        Console.WriteLine($"   ... {checkedPairs:N0} actual-shape pairs tested ({sw.Elapsed.TotalSeconds:0}s)");
    });
__run.ReviseTotal(sweep.PairsChecked, "the bounds sweep established the exact number of mesh queries");

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

Console.WriteLine($"\npairs checked by actual strict render surfaces: {sweep.PairsChecked:N0}; tolerance {surfaceTolerance:R}, work cap {maxTrianglePairs:N0} triangle pairs, time cap {pairSeconds:R}s/pair");
Console.WriteLine($"\n==== THE FLOOR — same author, deliberate actual-surface intersections ====");
Console.WriteLine($"   same-responsible pairs   : {sweep.SameChecked,10:N0} checked, {sweep.SameIntersects,7:N0} intersect, {sweep.SameUnknown,7:N0} unknown");
foreach (var (dom, v) in sweep.SameByDomain.OrderBy(k => k.Key))
    Console.WriteLine($"      {dom,-8}              : {v.Checked,10:N0} checked, {v.Intersects,7:N0} intersect, {v.Unknown,7:N0} unknown");
Console.WriteLine($"   declared-dependency pairs: {sweep.CrossDeclaredChecked,10:N0} checked, {sweep.CrossDeclaredIntersects,7:N0} intersect");

var undeclared = sweep.CrossHits.Where(h => !h.Declared).ToList();
Console.WriteLine($"\n==== CROSS-PLUGIN, NO DECLARED DEPENDENCY — ACTUAL SURFACES ====");
Console.WriteLine($"   pairs checked: {sweep.CrossUndeclaredChecked:N0}, intersections: {undeclared.Count:N0}, unknown: {sweep.CrossResults.Count(h => !h.Declared && h.Query.Unknown):N0}");
OverlapPopulation PopOf(SurfaceOverlapHit h) => DetectorModel.ClassifyOverlapPopulation(
    IsVanilla(h.A.Win.Plugin), IsVanilla(h.B.Win.Plugin), IsUssep(h.A.Win.Plugin), IsUssep(h.B.Win.Plugin));
Console.WriteLine("   relations: " + string.Join(", ", undeclared.GroupBy(h => h.Query.SurfaceRelation).OrderBy(g => g.Key).Select(g => $"{g.Key} {g.Count():N0}")));

__run.Stage("provenance", "rebuilding actual-surface intersection provenance", sweep.CrossHits.Count, "intersections");
var rows = new List<SurfaceOverlapRow>(sweep.CrossHits.Count);
foreach (var h in sweep.CrossHits)
{
    rows.Add(idx.JudgeSurface(h, closure, SurfaceGeometryOf, surfaceTolerance,
        maxTrianglePairs, TimeSpan.FromSeconds(pairSeconds), popMode == PopulationMode.Flags ? null : arms));
    __run.Advance();
}
var provenanceUnknown = rows.Where(r => r.CounterfactualStatus == "unknown").ToList();
var provenanceRows = rows.Where(r => r.CounterfactualStatus != "unknown").ToList();
int seenRows = rows.Count(r => r.Sight == OverlapSight.Seen);
Console.WriteLine($"\n==== PROVENANCE — actual surface with vs without the later plugin ({rows.Count:N0} intersections) ====");
Console.WriteLine($"   Seen rows {seenRows:N0}; declared intersections {sweep.CrossDeclaredIntersects:N0}; counterfactual unknown {provenanceUnknown.Count:N0}");

string Verdict(SurfaceOverlapRow r) => $"{r.Origin} + {r.Sight}";
var introduced = provenanceRows.Where(r => r.Origin == OverlapOrigin.Introduced).ToList();
Console.WriteLine($"   Introduced {introduced.Count:N0} of {provenanceRows.Count:N0} assessed; Blind {introduced.Count(r => r.Sight == OverlapSight.Blind):N0}");
var cls = provenanceRows.Where(r => r.Origin == OverlapOrigin.Introduced && r.Sight == OverlapSight.Blind
    && PopOf(r.Hit) != OverlapPopulation.BethesdaFloor).ToList();
var floorRows = provenanceRows.Where(r => PopOf(r.Hit) == OverlapPopulation.BethesdaFloor).ToList();

(string, string) PairKey(SurfaceOverlapRow r)
{
    var (x, y) = (PluginName(r.Later), PluginName(r.Earlier));
    return string.CompareOrdinal(x, y) <= 0 ? (x, y) : (y, x);
}
int DistinctRefs(IEnumerable<SurfaceOverlapRow> g) => g.SelectMany(r => new[] { r.Hit.A.Key, r.Hit.B.Key }).Distinct().Count();
string Describe(SurfaceOverlapRow r) =>
    $"{TypeOf(r.LaterRef.Base)} from {PluginName(r.Later)} / {TypeOf(r.OtherRef.Base)} from {PluginName(r.Earlier)}: "
  + $"{r.Hit.Query.SurfaceRelation}, containment {r.Hit.Query.Containment}, {r.Hit.Query.TrianglePairsTested:N0} triangle pairs"
  + $" in {CellOf(r.LaterRef.Win.Cell)} [{r.LaterRef.Src}{r.OtherRef.Src}] {r.LaterRef.Key} / {r.OtherRef.Key}";

Console.WriteLine($"\n==== THE CLASS — INTRODUCED + BLIND ACTUAL-SURFACE INTERSECTIONS: {cls.Count:N0} hits over {DistinctRefs(cls):N0} refs ====");
var byPair = cls.GroupBy(PairKey).OrderByDescending(g => g.Count()).ThenBy(g => g.Key.Item1, StringComparer.Ordinal).ToList();
foreach (var g in byPair.Take(listN))
{
    var first = g.OrderBy(r => r.Hit.A.Key.ToString(), StringComparer.Ordinal).ThenBy(r => r.Hit.B.Key.ToString(), StringComparer.Ordinal).First();
    Console.WriteLine($"   {g.Count(),5:N0} hits over {DistinctRefs(g),5:N0} refs {(Reconciled(first.Later, first.Earlier) ? "[patched, residual]" : "[NO RECONCILER]")} {g.Key.Item1} x {g.Key.Item2}");
    Console.WriteLine($"          first: {Describe(first)}");
}
Console.WriteLine($"\n   ---- first {listN} by canonical reference identity; relation is evidence, not severity ----");
foreach (var r in cls.OrderBy(r => r.Hit.A.Key.ToString(), StringComparer.Ordinal).ThenBy(r => r.Hit.B.Key.ToString(), StringComparer.Ordinal).Take(listN))
    Console.WriteLine("   " + Describe(r));

var knowing = provenanceRows.Where(r => r.Origin == OverlapOrigin.Introduced && r.Sight == OverlapSight.Seen).ToList();
var inherited = provenanceRows.Where(r => r.Origin == OverlapOrigin.Inherited).ToList();
Console.WriteLine($"\nknowingly intersecting (Introduced + Seen): {knowing.Count:N0}; inherited: {inherited.Count:N0}; Bethesda floor: {floorRows.Count:N0}");
foreach (var r in floorRows.OrderBy(r => r.Hit.A.Key.ToString(), StringComparer.Ordinal).Take(listN))
    Console.WriteLine($"   {Verdict(r),-22} {Describe(r)}");

// --emit: the CLASS only — Introduced + Blind, Bethesda floor withheld (the way navownprobe withholds
// its floor). The LATER plugin's ref is the necessary fix target (it placed into existing context);
// moving the other side is the legitimate alternative. Every record carries its physical predicate under this
// arm (OverlapPredicate, Holds by construction for a class row); with --base, every record of the named emit is
// re-measured under this arm too (2026-09-03).
if (emitPath is not null)
{
    long reconciliationPredicates = 0;
    __run.Stage("reconciliation", "remeasuring emitted and prior overlap predicates", total: null, unit: "predicates");
    var conflicts = new List<Conflict>();
    foreach (var r in cls)
    {
        var (st, why) = OverlapPredicate(r.LaterRef.Key, r.OtherRef.Key);
        conflicts.Add(new Conflict(
            "OVERLAP", PluginName(r.Earlier), PluginName(r.Later), Describe(r),
            new List<ConflictRecord> { new(r.LaterRef.Key.ToString(), "placed-ref", CellOf(r.LaterRef.Win.Cell), st, why) },
            new List<ConflictRecord> { new(r.OtherRef.Key.ToString(), "placed-ref", null) }));
        reconciliationPredicates++;
        __run.Advance();
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
                reconciliationPredicates++;
                __run.Advance();
                if (c.Kind != "OVERLAP" || other is null) { predicates.Add(new Predicate(c.Kind, c.A, c.B, rec.FormKey, other, PhysicalState.Unmeasured, "not an overlap row with two refs")); continue; }
                var (st, why) = OverlapPredicate(FormKey.Factory(rec.FormKey), FormKey.Factory(other));
                predicates.Add(new Predicate(c.Kind, c.A, c.B, rec.FormKey, other, st, why));
            }
        }
        Console.WriteLine($"\n   --base {Path.GetFileName(basePath)}: {predicates.Count:N0} base-row predicate(s) re-measured under this order — "
                        + string.Join(", ", predicates.GroupBy(p => p.Physical).OrderBy(g => g.Key).Select(g => $"{g.Key} {g.Count():N0}")));
    }
    int notRebuilt = rows.Count(r => r.CounterfactualStatus == "unknown");
    long measuredBases = geometryCounts.GetValueOrDefault("measured") + geometryCounts.GetValueOrDefault("degenerate_geometry");
    var geometryUnknown = geometryCounts.Where(k => k.Key is not ("measured" or "degenerate_geometry") && k.Value > 0)
        .ToDictionary(k => k.Key, k => k.Value, StringComparer.Ordinal);
    var coverage = new List<CoverageLine>
    {
        new("plugins", paths.Count - unreadablePlugins.Count, new Dictionary<string, long> { ["UnreadablePlugin"] = unreadablePlugins.Count }),
        new("base records (strict render geometry)", measuredBases, geometryUnknown, null,
            $"policy {NifOverlapGeometry.SurfaceMeasurementIdentity(surfaceTolerance)}; record ObjectBounds unused"),
        // The unresolved enable chains join NoGeometry as unjudgeable: a candidate whose chain could not be read to the
        // end is in the population on EnableState's stated fallback, not on a measurement, and conflictdiff's coverage
        // test must be able to see that share rise (Q27).
        new("candidate refs", candidates.Count,
            new Dictionary<string, long>(pop.Unjudgeable) { ["NoGeometry"] = skips.NoGeometry,
                ["InvalidTransform"] = skips.InvalidTransform, ["DegenerateTransform"] = skips.DegenerateGeometry },
            new Dictionary<string, long> { ["Deleted"] = skips.Deleted, ["NotPresentAtLoad"] = skips.Disabled, ["NonObstructingType"] = skips.NonObstructingType,
                                           ["BaseGameResponsible"] = skips.ExcludedResponsible, ["EditorMarker"] = skips.MarkerMesh },
            $"population {pop.Identity} / state {pop.StateName}"),
        new("cross-responsible actual-shape queries", sweep.CrossResults.Count - sweep.CrossUnknown,
            sweep.CrossUnknown == 0 ? new Dictionary<string, long>() : new Dictionary<string, long> { ["MeshQueryUnknown"] = sweep.CrossUnknown }, null,
            $"intersections {rows.Count:N0}; separated results remain measured and are not conflicts"),
        new("actual-surface intersection counterfactuals", rows.Count - notRebuilt,
            notRebuilt == 0 ? new Dictionary<string, long>() : new Dictionary<string, long> { ["CounterfactualGeometryUnmeasurable"] = notRebuilt }, null,
            $"class {cls.Count:N0}; unknown counterfactual rows have null provenance and are withheld from the class"),
    };
    var identity = cfg.Identity(pop.Identity, pop.StateName);
    __run.ReviseTotal(reconciliationPredicates, "the current class and optional base emit established the exact predicate count");
    __run.Stage("emit-artifact", "writing overlap conflict artifact", 1, "artifacts");
    ConflictIo.Write(emitPath, new ConflictFile("overlapprobe", Instance,
        DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm:ssZ"), conflicts, coverage, predicates, basePath,
        pop.Identity, pop.StateName, identity, MeasurementPolicy: NifOverlapGeometry.SurfaceMeasurementIdentity(surfaceTolerance)));
    __run.Advance().Artifact(emitPath);
    Console.WriteLine($"configuration: {identity.Short}");
    Console.WriteLine($"\nemitted {conflicts.Count:N0} Introduced + Blind conflicts -> {emitPath}");
}

if (jsonPath is not null)
{
    __run.Stage("json-artifact", "writing judged overlap rows", 1, "artifacts");
    var dir = Path.GetDirectoryName(Path.GetFullPath(jsonPath));
    if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
    var dump = rows.Select(r => new
    {
        Later = r.LaterRef.Key.ToString(), LaterPlugin = PluginName(r.Later), LaterType = TypeOf(r.LaterRef.Base), LaterMesh = MeshOf(r.LaterRef.Base), LaterSrc = r.LaterRef.Src.ToString(),
        Other = r.OtherRef.Key.ToString(), OtherPlugin = PluginName(r.Earlier), OtherType = TypeOf(r.OtherRef.Base), OtherMesh = MeshOf(r.OtherRef.Base), OtherSrc = r.OtherRef.Src.ToString(),
        Cell = CellOf(r.LaterRef.Win.Cell), Origin = r.Origin.ToString(), Sight = r.Sight.ToString(), Population = PopOf(r.Hit).ToString(),
        Declared = r.Hit.Declared, r.Hit.Query.Status, r.Hit.Query.Reason, r.Hit.Query.SurfaceRelation,
        r.Hit.Query.Containment, r.Hit.Query.Intersects, r.Hit.Query.TrianglePairsTested,
        Counterfactual = r.Counterfactual,
        r.CounterfactualStatus, r.CounterfactualReason,
        LaterPrevious = r.LaterPrevious is { } lp ? new { Plugin = PluginName(lp.Plugin), lp.Pos.X, lp.Pos.Y, lp.Pos.Z, Cell = lp.Cell.ToString(), lp.Disabled, lp.Deleted } : null,
        OtherAsSeenByLater = r.OtherAsSeenByLater is { } os ? new { Plugin = PluginName(os.Plugin), os.Pos.X, os.Pos.Y, os.Pos.Z } : null,
        Reconciled = Reconciled(r.Later, r.Earlier), r.Why,
    });
    File.WriteAllText(jsonPath, JsonSerializer.Serialize(dump, new JsonSerializerOptions { WriteIndented = false, NumberHandling = System.Text.Json.Serialization.JsonNumberHandling.AllowNamedFloatingPointLiterals }));
    __run.Advance().Artifact(jsonPath);
    Console.WriteLine($"wrote {rows.Count:N0} judged rows -> {jsonPath}");
}

if (geometryManifestPath is not null)
{
    __run.Stage("manifest-records", "assembling actual-shape manifest records", idx.History.Refs.Count(), "references");
    static ManifestBounds? BoundsArray(Aabb? b) => b is not { IsEmpty: false } box ? null
        : new(new[] { (double)box.Min.X, (double)box.Min.Y, (double)box.Min.Z },
              new[] { (double)box.Max.X, (double)box.Max.Y, (double)box.Max.Z });
    static double[] Dimensions(Aabb b) => new[] { (double)b.Max.X - b.Min.X,
                                                   (double)b.Max.Y - b.Min.Y,
                                                   (double)b.Max.Z - b.Min.Z };
    static double Volume(Aabb b) { var d = Dimensions(b); return d[0] * d[1] * d[2]; }
    static string PairWire(FormKey a, FormKey b)
    {
        string x = a.ToString(), y = b.ToString();
        return string.CompareOrdinal(x, y) <= 0 ? x + "||" + y : y + "||" + x;
    }

    var historicalPairs = historicalCrosswalk?.Pairs ?? new List<HistoricalPair>();
    var historicalRefKeys = new HashSet<FormKey>();
    try
    {
        foreach (var hp in historicalPairs) { historicalRefKeys.Add(FormKey.Factory(hp.A)); historicalRefKeys.Add(FormKey.Factory(hp.B)); }
    }
    catch (Exception ex) { Console.Error.WriteLine($"--historical-pairs contains an invalid FormKey: {ex.Message}"); return 2; }
    var reviewByKey = new Dictionary<FormKey, ReviewRef>();
    var recordRows = new List<object>();
    foreach (var (key, _) in idx.History.Refs.OrderBy(k => k.Key.ToString(), StringComparer.Ordinal))
    {
        __run.Advance();
        var pose = idx.WinningGeometryPose(key);
        if (pose is null || (!scopedResponsiblePlugins.Contains(pose.Value.Plugin) && !historicalRefKeys.Contains(key))) continue;
        var p = pose.Value;
        bool deleted = p.Deleted || idx.IsDeletedNow(key);
        EnableResolution? enable = popMode == PopulationMode.Flags ? null : arms.Winner(key);
        bool enabled = popMode == PopulationMode.Flags ? !p.Disabled : enable!.Value.Enabled;
        bool enableResolved = popMode == PopulationMode.Flags || enable!.Value.Resolved;
        bool hasBase = idx.RefBase.TryGetValue(key, out var effectiveBase);
        GeometryRead? geometry = hasBase ? GeometryOf(effectiveBase) : null;
        bool geometryKnown = geometry?.Outcome is { UsableForSurfaceIntersection: true, Soup: { TriangleCount: > 0 } };
        Aabb? modelBounds = geometryKnown ? geometry!.Outcome!.ModelBounds : null;
        TriangleSoup? worldSoup = geometryKnown
            ? SpatialOverlap.TransformSoup(geometry!.Outcome!.Soup!, p.Pos, p.Rot, p.Scale)
            : null;
        Aabb? worldBounds = worldSoup is null ? null : SpatialOverlap.SoupBounds(worldSoup);
        bool zeroScale = p.Scale == 0;
        bool worldGeometryKnown = !zeroScale && worldBounds is { IsEmpty: false } wb0 && SpatialOverlap.IsFinite(wb0);
        bool bosUnknown = idx.BosSwaps.TryGetValue(key, out var bosResolution)
                       && (bosResolution.Random || bosResolution.PerSession || bosResolution.PropertiesRandom || bosResolution.Alternatives.Count != 1);

        string eligibility, eligibilityReason;
        if (deleted) { eligibility = "state_excluded"; eligibilityReason = "the winning record is deleted"; }
        else if (!enableResolved) { eligibility = "state_unresolved"; eligibilityReason = "the enable-parent chain did not resolve"; }
        else if (!enabled) { eligibility = "state_excluded"; eligibilityReason = "the reference is absent in the selected state"; }
        else if (bosUnknown) { eligibility = "bos_unresolved"; eligibilityReason = "BOS has more than one possible runtime outcome"; }
        else if (!hasBase) { eligibility = "source_excluded"; eligibilityReason = "the winning reference has no base record"; }
        else if (!idx.BaseInfo.TryGetValue(effectiveBase, out var bi) || !Obstructing.Contains(bi.Type))
        { eligibility = "type_excluded"; eligibilityReason = "the winning base is not an obstructing record type"; }
        else if (idx.BaseIsMarker.Contains(effectiveBase)
              || (idx.BaseMesh.TryGetValue(effectiveBase, out var markerMesh) && Path.GetFileName(markerMesh).StartsWith("MarkerX", StringComparison.OrdinalIgnoreCase)))
        { eligibility = "marker_excluded"; eligibilityReason = "the winning base is an editor marker"; }
        else if (!geometryKnown) { eligibility = "geometry_unmeasurable"; eligibilityReason = geometry?.Reason ?? "no complete strict render triangles"; }
        else if (zeroScale)
        { eligibility = "degenerate_geometry"; eligibilityReason = "the placed reference has zero scale"; }
        else if (!worldGeometryKnown)
        { eligibility = "invalid_transform"; eligibilityReason = "placed render triangles have no finite bounds"; }
        else { eligibility = "eligible"; eligibilityReason = "passes the discovery record gates"; }

        bool runtimePoseKnown = !deleted && enableResolved && enabled && !bosUnknown;
        Aabb? reportedWorldBounds = runtimePoseKnown && worldGeometryKnown ? worldBounds : null;
        // This is the placed surface's availability, not a classification of its diagnostic AABB.
        // In particular, a valid flat triangle sheet is render-ready even though the older bounds reader
        // describes its zero-volume box as degenerate. Source failures must also survive unchanged rather
        // than being relabelled as transform failures merely because there was no soup to transform.
        string reportedGeometryStatus = !runtimePoseKnown ? eligibility
            : !hasBase ? eligibility
            : !geometryKnown ? geometry?.Status ?? eligibility
            : zeroScale ? "degenerate_geometry"
            : !worldGeometryKnown ? "invalid_transform"
            : "measured";
        string? reportedGeometryReason = !runtimePoseKnown ? eligibilityReason
            : !hasBase ? eligibilityReason
            : !geometryKnown ? geometry?.Reason ?? eligibilityReason
            : !worldGeometryKnown ? eligibilityReason
            : null;
        reviewByKey[key] = new ReviewRef(key, eligibility, eligibilityReason, reportedGeometryStatus,
                                         reportedGeometryReason, hasBase ? effectiveBase : null, p, reportedWorldBounds,
                                         runtimePoseKnown && worldGeometryKnown ? worldSoup : null);

        idx.Touchers.TryGetValue(key, out var touchers);
        string winner = touchers is { Count: > 0 } ? PluginName(touchers[^1]) : PluginName(p.Plugin);
        var recordBase = idx.RecordBase.TryGetValue(key, out var rb) ? rb : hasBase ? effectiveBase : FormKey.Null;
        object bos = !bosOn ? new { status = "off", resolution = (object?)null }
            : bosResolution is null ? new { status = "unchanged", resolution = (object?)null }
            : new
            {
                status = bosUnknown ? "unresolved" : bosResolution.Swapped || bosResolution.HasTransform || bosResolution.SetsFlags ? "resolved" : "unchanged",
                resolution = (object?)new
                {
                    effectiveBase = bosResolution.EffectiveBase.ToString(), bosResolution.Swapped, bosResolution.Random,
                    bosResolution.Leveled, bosResolution.PerSession, bosResolution.PropertiesRandom,
                    alternatives = bosResolution.Alternatives.Select(a => new { @base = a.Base.ToString(), a.Probability }).ToArray(),
                    bosResolution.PropertiesSource, notes = bosResolution.Notes
                }
            };
        recordRows.Add(new
        {
            formid = key.ToString(),
            type = idx.RefArm.GetValueOrDefault(key, "placed-ref"),
            editorid = idx.RefEditorId.GetValueOrDefault(key),
            name = (string?)null,
            winner,
            fields = new
            {
                responsiblePlugin = PluginName(p.Plugin),
                effectiveBase = !hasBase ? null : new
                {
                    formid = effectiveBase.ToString(),
                    type = idx.BaseInfo.TryGetValue(effectiveBase, out var effectiveInfo) ? effectiveInfo.Type : null,
                    editorid = idx.BaseInfo.TryGetValue(effectiveBase, out effectiveInfo) ? effectiveInfo.Edid : null,
                    name = (string?)null
                },
                recordBase = recordBase.IsNull ? null : new
                {
                    formid = recordBase.ToString(),
                    type = idx.BaseInfo.TryGetValue(recordBase, out var recordInfo) ? recordInfo.Type : null,
                    editorid = idx.BaseInfo.TryGetValue(recordBase, out recordInfo) ? recordInfo.Edid : null,
                    name = (string?)null
                },
                cell = new
                {
                    formid = p.Cell.ToString(),
                    editorid = idx.CellName.TryGetValue(p.Cell, out var cellInfo) ? cellInfo.Edid : null,
                    interior = idx.CellName.TryGetValue(p.Cell, out cellInfo) && cellInfo.Interior
                },
                space = p.Space.ToString(),
                position = new[] { (double)p.Pos.X, (double)p.Pos.Y, (double)p.Pos.Z },
                rotation = new[] { (double)p.Rot.X, (double)p.Rot.Y, (double)p.Rot.Z },
                scale = (double)p.Scale, deletion = deleted,
                resolvedEnable = new { enabled, resolved = enableResolved, population = pop.Identity, state = pop.StateName },
                bos
            },
            eligibility = new { status = eligibility, reason = eligibilityReason },
            geometry = new
            {
                assetId = geometry?.AssetId, status = reportedGeometryStatus, reason = reportedGeometryReason,
                modelBounds = BoundsArray(modelBounds), worldBounds = BoundsArray(reportedWorldBounds),
                dimensions = reportedWorldBounds is { } wb ? Dimensions(wb) : null,
                volume = reportedWorldBounds is { } vb ? Volume(vb) : (double?)null
            }
        });
    }

    var assetRows = geometryByPath.Values.OrderBy(g => g.RelativePath, StringComparer.OrdinalIgnoreCase).Select(g => new
    {
        assetId = g.AssetId,
        relativePath = g.RelativePath,
        resolutionStatus = g.Status,
        reason = g.Reason,
        winner = g.Source is null ? null : new
        {
            providerName = g.Source.ProviderName,
            kind = g.Source.Kind.ToString().ToLowerInvariant(),
            looseFilePath = g.Source.LooseFilePath,
            archivePath = g.Source.ArchivePath,
            archiveEntry = g.Source.EntryPath
        },
        providers = g.Resolution?.Sources.Select(s => new
        {
            providerName = s.ProviderName, kind = s.Kind.ToString().ToLowerInvariant(),
            looseFilePath = s.LooseFilePath, archivePath = s.ArchivePath, archiveEntry = s.EntryPath
        }).ToArray() ?? Array.Empty<object>(),
        ambiguity = g.Resolution?.Ambiguous ?? false,
        readIncomplete = g.Resolution?.ReadIncomplete ?? assets.ReadIncomplete,
        consumedSha256 = g.Outcome?.ContentSha256,
        byteCount = g.ByteCount,
        geometryStatus = g.Status,
        modelBounds = BoundsArray(g.Outcome is { Complete: true } ? g.Outcome.ModelBounds : null),
        totalVertices = g.Outcome?.Model?.TotalVertices,
        totalTriangles = g.Outcome?.Model?.TotalTriangles,
        visibleShapeCount = g.Outcome?.Model?.Shapes.Count(s => !s.Hidden),
        decodedVisibleShapeCount = g.Outcome?.Model?.Shapes.Count(s => !s.Hidden && s.VerticesRead && !s.Bounds.IsEmpty)
    }).ToList();

    ManifestMeasurements PairMeasurements(ReviewRef? a, ReviewRef? b)
    {
        var ab = new ManifestBoxMeasure(a?.GeometryStatus ?? "missing_reference", a?.GeometryReason,
            a?.WorldBounds is { } aw ? Dimensions(aw) : null, a?.WorldBounds is { } av ? Volume(av) : null);
        var bb = new ManifestBoxMeasure(b?.GeometryStatus ?? "missing_reference", b?.GeometryReason,
            b?.WorldBounds is { } bw ? Dimensions(bw) : null, b?.WorldBounds is { } bv ? Volume(bv) : null);
        if (a?.WorldBounds is not { } aworld || b?.WorldBounds is not { } bworld)
            return new("unmeasurable", a is null || b is null ? "a historical reference is absent from the evaluated order" : "at least one reference has no finite eligible world bounds",
                       ab, bb, null, null, null, null, null, null);
        var m = SpatialOverlap.MeasureAabbOverlap(aworld, bworld);
        var extents = new[] {
            Math.Max(0d, Math.Min((double)aworld.Max.X, bworld.Max.X) - Math.Max((double)aworld.Min.X, bworld.Min.X)),
            Math.Max(0d, Math.Min((double)aworld.Max.Y, bworld.Max.Y) - Math.Max((double)aworld.Min.Y, bworld.Min.Y)),
            Math.Max(0d, Math.Min((double)aworld.Max.Z, bworld.Max.Z) - Math.Max((double)aworld.Min.Z, bworld.Min.Z)) };
        return new("measured", null, ab, bb, extents, m.IntersectionVolume, m.IntersectionAreaXy,
                   m.FractionOfSmaller, m.FractionReason, new(m.ZRelation, m.SignedZIntersection, m.ZGap));
    }
    static string PairEligibilityReason(ReviewRef? a, ReviewRef? b) =>
        a is null || b is null ? "a historical reference is absent from the evaluated order"
      : a.Eligibility != "eligible" ? a.EligibilityReason
      : b.Eligibility != "eligible" ? b.EligibilityReason
      : "both records pass discovery gates";

    var freshByKey = sweep.CrossResults.ToDictionary(x => PairWire(x.A.Key, x.B.Key), StringComparer.Ordinal);
    var directQueryByKey = new Dictionary<string, SurfaceQueryOutcome>(StringComparer.Ordinal);
    SurfaceQueryOutcome ActualShape(string key, ReviewRef? a, ReviewRef? b)
    {
        if (freshByKey.TryGetValue(key, out var fresh)) return fresh.Query;
        if (directQueryByKey.TryGetValue(key, out var memo)) return memo;
        SurfaceQueryOutcome result;
        if (a is null || b is null)
            result = new("unknown", "a historical reference is absent from the evaluated order", "unknown", "unknown", null, 0);
        else if (a.Eligibility != "eligible" || b.Eligibility != "eligible")
            result = new("unknown", PairEligibilityReason(a, b), "unknown", "unknown", null, 0);
        else if (a.Pose.Space != b.Pose.Space)
            result = new("measured", "references occupy different coordinate spaces", "separated", "none", false, 0);
        else if (a.WorldSoup is not { TriangleCount: > 0 } sa || b.WorldSoup is not { TriangleCount: > 0 } sb)
            result = new("unknown", "at least one reference has no complete placed render triangles", "unknown", "unknown", null, 0);
        else
            result = SurfaceQueryOutcome.Query(sa, sb, surfaceTolerance, maxTrianglePairs, TimeSpan.FromSeconds(pairSeconds));
        return directQueryByKey[key] = result;
    }
    static ManifestStatus Discovery(SurfaceQueryOutcome q) => q.Intersects switch
    {
        true => new("actual_surface_intersection", $"{q.SurfaceRelation}; containment {q.Containment}"),
        false => new("actual_surface_separated", $"surface relation {q.SurfaceRelation}; containment {q.Containment}"),
        _ => new("actual_surface_unknown", q.Reason ?? "mesh predicate returned unknown")
    };
    static ManifestActualShape ShapeWire(SurfaceQueryOutcome q) =>
        new(q.Status, q.Reason, q.SurfaceRelation, q.Containment, q.Intersects, q.TrianglePairsTested);

    var pairRows = new List<ManifestPair>();
    var historicalGroups = historicalPairs.GroupBy(p => p.PairKey, StringComparer.Ordinal).ToList();
    var historicalKeys = historicalGroups.Select(g => g.Key).ToHashSet(StringComparer.Ordinal);
    __run.Stage("manifest-historical-shape", "resolving historical pairs against current actual surfaces", historicalGroups.Count, "pairs");
    foreach (var group in historicalGroups)
    {
        var first = group.First();
        var ak = FormKey.Factory(first.A); var bk = FormKey.Factory(first.B);
        reviewByKey.TryGetValue(ak, out var ar); reviewByKey.TryGetValue(bk, out var br);
        var actual = ActualShape(group.Key, ar, br);
        pairRows.Add(new(group.Key, first.ConflictIoRowIdentity, first.A, first.B,
            group.Select(p => p.HistoricalRowId).ToArray(), false, Discovery(actual),
            new(ar?.Eligibility == "eligible" && br?.Eligibility == "eligible" ? "eligible" : "ineligible",
                PairEligibilityReason(ar, br)), ShapeWire(actual), PairMeasurements(ar, br)));
        __run.Advance();
    }
    foreach (var fresh in sweep.CrossResults.Where(x => (x.Query.Intersects == true || x.Query.Unknown)
             && !historicalKeys.Contains(PairWire(x.A.Key, x.B.Key))))
    {
        string key = PairWire(fresh.A.Key, fresh.B.Key);
        var ar = reviewByKey[fresh.A.Key]; var br = reviewByKey[fresh.B.Key];
        pairRows.Add(new(key, ConflictIo.RowIdentity("OVERLAP", fresh.A.Key.ToString(), fresh.B.Key.ToString(), ""),
            fresh.A.Key.ToString(), fresh.B.Key.ToString(), Array.Empty<string>(), true,
            Discovery(fresh.Query), new("eligible", "both records pass source, state, type and strict render-geometry gates"),
            ShapeWire(fresh.Query), PairMeasurements(ar, br)));
    }
    pairRows = pairRows.OrderBy(p => p.pairKey, StringComparer.Ordinal).ToList();

    var manifestIdentity = cfg.Identity(pop.Identity, pop.StateName);
    var manifest = new
    {
        schemaVersion = "nif-overlap-actual-shape-manifest-v2",
        readerVersion = NifOverlapGeometry.ReaderVersion,
        policyVersion = NifOverlapGeometry.SurfaceMeasurementIdentity(surfaceTolerance),
        job = "nif-overlap-actual-shape",
        scope = new
        {
            responsiblePairs = responsiblePairs.Select(p => new[] { p.AName, p.BName }).ToArray(),
            population = pop.Identity, state = pop.StateName, bos = bosOn ? "on" : "off",
            historicalPairs = historicalPairsPath is null ? null : new { path = Path.GetFullPath(historicalPairsPath), sha256 = historicalCrosswalkSha256 },
            predicate = new { tolerance = surfaceTolerance, maxTrianglePairs, pairSeconds, policyVersion = MeshIntersection.PolicyVersion },
            configuration = manifestIdentity
        },
        total = recordRows.Count,
        complete = unreadablePlugins.Count == 0 && !assets.ReadIncomplete && pairRows.All(p => p.actualShape.intersects is not null),
        notes = new[]
        {
            "Actual-shape results come from symmetric intersection of placed strict decoded render triangles; record ObjectBounds and stored NIF spheres have zero influence.",
            "Bounds enclose the placed triangle soups and reject only provably separated geometry; their overlap magnitudes are diagnostics and never discovery or severity gates.",
            "LOD shapes are conservatively included; statically hidden shapes are excluded; unsupported live poses and partial reads are unmeasurable.",
            "The scoped run resolves the full order, winner history, selected state and BOS layer before restricting candidates to the named responsible pair."
        },
        records = recordRows,
        assets = assetRows,
        pairs = pairRows,
        measurementAccounting = new
        {
            recordCount = recordRows.Count, pairCount = pairRows.Count,
            historicalRowCount = historicalPairs.Count, historicalPairCount = historicalGroups.Count,
            duplicateHistoricalRowCount = historicalPairs.Count - historicalGroups.Count,
            newCandidatePairCount = pairRows.Count(p => p.newCandidate),
            measuredPairCount = pairRows.Count(p => p.actualShape.intersects is not null),
            intersectingPairCount = pairRows.Count(p => p.actualShape.intersects == true),
            separatedPairCount = pairRows.Count(p => p.actualShape.intersects == false),
            unknownPairCount = pairRows.Count(p => p.actualShape.intersects is null),
            relationCounts = pairRows.GroupBy(p => p.actualShape.surfaceRelation).ToDictionary(g => g.Key, g => g.Count(), StringComparer.Ordinal),
            containmentCounts = pairRows.GroupBy(p => p.actualShape.containment).ToDictionary(g => g.Key, g => g.Count(), StringComparer.Ordinal)
        },
        workload = new
        {
            consumedBytes = assetRows.Sum(a => a.byteCount ?? 0L),
            decodedVertices = assetRows.Sum(a => (long)(a.totalVertices ?? 0)),
            decodedTriangles = assetRows.Sum(a => (long)(a.totalTriangles ?? 0)),
            cacheHits = geometryLookupHits + pathCacheHits + renderCache.Hits,
            cacheMisses = renderCache.Misses,
            baseCacheHits = geometryLookupHits,
            pathCacheHits,
            contentCacheHits = renderCache.Hits,
            trianglePairsTested = pairRows.Sum(p => p.actualShape.trianglePairsTested)
        },
        limitations = new { unreadablePlugins, archiveReadFailures = assets.BsaFailures }
    };
    var manifestDir = Path.GetDirectoryName(Path.GetFullPath(geometryManifestPath));
    if (!string.IsNullOrEmpty(manifestDir)) Directory.CreateDirectory(manifestDir);
    __run.Feature("nif_bytes", manifest.workload.consumedBytes, "bytes", FeatureAt.Final)
         .Feature("nif_vertices", manifest.workload.decodedVertices, "vertices", FeatureAt.Final)
         .Feature("nif_triangles", manifest.workload.decodedTriangles, "triangles", FeatureAt.Final)
         .Feature("nif_cache_hits", manifest.workload.cacheHits, "lookups", FeatureAt.Final)
         .Feature("nif_cache_misses", manifest.workload.cacheMisses, "lookups", FeatureAt.Final);
    __run.Stage("manifest-artifact", "writing actual-shape manifest", 1, "artifacts");
    File.WriteAllText(geometryManifestPath, JsonSerializer.Serialize(manifest, new JsonSerializerOptions
    {
        WriteIndented = true,
        NumberHandling = System.Text.Json.Serialization.JsonNumberHandling.AllowNamedFloatingPointLiterals
    }));
    __run.Advance().Artifact(geometryManifestPath);
    Console.WriteLine($"wrote geometry manifest: {recordRows.Count:N0} records, {pairRows.Count:N0} union pairs -> {geometryManifestPath}");
}

__run.Stage("finalize", "finalizing overlap run", 1, "runs").Advance();
Console.WriteLine($"\nActual render-surface relations are geometry evidence, not compatibility severity, intent or a game-verified defect.");
Console.WriteLine($"Limits: this measures static render surfaces; collision and runtime animation are unassessed; rotation- and scale-only re-placements are not provenance steps ({idx.SilentRePlacements:N0} counted above);");
Console.WriteLine($"        the arm without the later plugin uses the last placement and base outside that plugin's downstream side; unavailable historical geometry stays unknown.");
Console.WriteLine($"elapsed {sw.Elapsed.TotalSeconds:0.0}s");
return 0;

static float Rate(long hit, long total) => total == 0 ? 0 : 100f * hit / total;

sealed record GeometryRead(string Status, string? Reason, string? RelativePath, string? AssetId,
                           PlacementResolution? Resolution, PlacementSource? Source,
                           NifRenderGeometryOutcome? Outcome, long? ByteCount = null);
sealed record ManifestBounds(double[] min, double[] max);
sealed record ReviewRef(FormKey key, string Eligibility, string EligibilityReason,
                        string? GeometryStatus, string? GeometryReason, FormKey? Base, Snap Pose, Aabb? WorldBounds,
                        TriangleSoup? WorldSoup);
sealed record ManifestStatus(string status, string? reason);
sealed record ManifestBoxMeasure(string geometryStatus, string? reason, double[]? dimensions, double? volume);
sealed record ManifestZRelation(string relation, double signedIntersection, double gap);
sealed record ManifestMeasurements(string status, string? reason,
    ManifestBoxMeasure aBox, ManifestBoxMeasure bBox, double[]? intersectionExtents,
    double? intersectionVolume, double? xyIntersectionArea, double? overlapFractionSmaller,
    string? overlapFractionReason, ManifestZRelation? zRelation);
sealed record ManifestActualShape(string status, string? reason, string surfaceRelation, string containment,
                                  bool? intersects, long trianglePairsTested);
sealed record ManifestPair(string pairKey, string conflictIoRowIdentity, string a, string b,
    string[] historicalRowIds, bool newCandidate, ManifestStatus discovery, ManifestStatus eligibility,
    ManifestActualShape actualShape, ManifestMeasurements diagnosticBounds);
sealed record HistoricalCrosswalk(int SchemaVersion, bool Complete, List<HistoricalPair>? Pairs);
sealed record HistoricalPair(string HistoricalRowId, string PairKey, string ConflictIoRowIdentity, string A, string B);
