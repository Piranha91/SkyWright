// HousecarlCore now has its own PluginUnreadableException (upstream 2.0); this probe means the harness one.
using PluginUnreadableException = HarnessShared.PluginUnreadableException;
using System.Diagnostics;
using System.Globalization;
using System.Numerics;
using HarnessShared;
using HousecarlCore;
using SkyWright.Engine;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Skyrim;
using HousecarlCore.Progress;

// floorgapprobe — the navmesh-to-floor offset δ (roadmap 4.7's band, Q24).
//
// Every vertical number in the route work is stated above the NAVMESH; the walker steps and ducks against its FEET,
// which rest on the physical floor beneath it. At the one station where both were measured (the Ratway corridor,
// 2026-09-05) the navmesh floats 10 u above the floor, 5 u at 64 u west and 17 u at 64 u east. This measures the
// offset from the records — for every navmesh triangle the route rule can stand on, the vertical distance from the
// navmesh surface down (or up) to the floor a walker's feet rest on: the highest up-facing actor-solid collision
// surface within reach (SkyWright.Engine.NavmeshFloor, asked through a FloorField built by _shared/FloorFeed.cs — the
// same field the route rule stands on), read from the same collision soup the route rules run on (CollisionLayers: the
// layers an actor meets; a mesh whose decode is incomplete falls back to its render soup) and, in an exterior, the
// cell's LAND heightmap. Positive δ = the navmesh above the floor.
//
// usage: floorgapprobe sweep [maxCells] [--tsv cells.tsv] [--tris tris.tsv] [--verdicts DIR]
//        floorgapprobe cell <CellEditorID|FormKey> [--tris tris.tsv]
//        floorgapprobe line <CellEditorID> --at X,Y,Z --heading DEG [--span 384] [--every 16]
//        floorgapprobe points <CellEditorID> <file>     (lines of "x y z": a walker's positions; prints z − floor per point)
//   common: [--plugin Skyrim.esm] [--winning] [--flags] [--instance DIR] [--game DIR] [--profile NAME] [--depth 128] [--reach 48] [--split 192] [--own]
//           [--order FILE]   with --winning: walk a candidate configuration (a CandidateFile) instead of the profile's order,
//                            through _shared/HarnessOrderCli.cs, assets included. Without it nothing changes.
//   sweep     every cell of the plugin with navmesh and refs (navprobe's population): δ per triangle (the centroid; four
//             interior points on a triangle longer than --split), per cell (median, spread, storeys), the histograms by
//             interior / exterior / floor source / worldspace, the no-floor share by reason, the widest within-storey
//             spreads. --verdicts reads navprobe's route-cut-blocked.tsv / route-cut-likely.tsv from a --dump directory
//             and reports the δ under each object those verdicts stand on (the samples in its plan footprint + capsule,
//             the object itself not a floor candidate).
//   cell      one cell, per storey, with the per-triangle listing to a TSV.
//   line      the calibration read: δ along a heading through a point (yaw, degrees, 0 = +Y, clockwise), every --every u
//             for --span u each way — the same line corridorprobe width walks.
//   reach     (G1 build list, Stage A, row 4) `reach <Cell> <subjects.json> --json OUT`: for each subject reference
//             ({"subjects":[{"id":"FORMKEY"}]}), the exact 3D distance from its placed render geometry at the stored
//             pose (its position when the mesh is unreadable, partial or has no model) to the nearest winning navmesh
//             triangle of the cell not flagged Deleted (SkyWright.Engine.NavmeshReach), with its horizontal and vertical
//             parts, the triangle, and the floor under that point (this probe's floor rule). A navmesh triangle is where
//             NPCs path, not every place a player can stand. Interior: the cell's navmesh is the whole set. Exterior:
//             only the cell's own navmesh is searched, so the distance is an upper bound and says so.
//             A subject filed in its worldspace's persistent cell is looked up in the grid cell its position falls in
//             (it stands there; no plugin files it in that cell), and its row says so in `placement`.
//             A model-less subject with an XPRM Box (a trigger) is measured from its box at the reference's pose. Opt-in
//             (G1 Stage B): --band LO,HI adds `band`, the exact distance to the navmesh swept straight up from +LO to +HI
//             (where a standing body is; 0 when the subject meets it); --under adds `under`, the floor straight under the
//             subject's position by this probe's floor rule (--depth below, --reach above). Without them nothing changes.
//   points    the calibration against a walker: for each recorded position (feet), the navmesh and floor under it and
//             feet − floor; a floor that is the physical one reads that difference near zero.
//   --depth   how far below the navmesh a floor may lie (deeper is a drop: no floor); --reach how far above (the step
//             height: a surface the walker steps onto is where its feet rest). NavmeshFloor's defaults.
//   --own     an exterior cell's floor from its OWN refs and its own LAND only, no neighbourhood — what a caller that
//             cannot reach the neighbourhood would build; the difference against the default is that caller's loss.
//   --winning the cell as the PROFILE's load order resolves it (Q25, 2026-09-05): every plugin walked in order, a later
//             plugin's ref or navmesh of the same FormKey replacing an earlier one — blockprobe's walk — so the navmesh
//             AND the floor candidates are the winning state, the floor the route rule stands on. Without it the one
//             --plugin supplies both (that plugin's own navmesh against its own refs). cell / line / points only.
//   --flags   floor candidates by the record's initially-disabled flag alone — the population every run before
//             2026-09-06 judged, kept so those numbers can be reproduced. The default resolves the load-time enable
//             CHAIN (R1): a switch child whose marker is disabled at load is not a floor — Q25: a room variant switched
//             off at load had read as the floor 48 u over a whole room. Since Q27 the chain's facts come from every
//             plugin walked, not just the refs of the cell being read, so a marker in the worldspace's PERSISTENT cell
//             (or in any other cell) governs its children correctly instead of resolving unread.
//   --state   not taken: floorgapprobe reads one source (a --plugin, or the winning order under --winning) and carries no
//             switch census to flip. Its default IS the load-time state of that source.
//   Every line / points row names the floor's SUPPLIER — the placed object whose surface the reading came from, with its
//   base's model — because a floor laid over the winning navmesh is only actionable once the object laying it is named.
//   Refs are the plugin's placements (persistent ones included, bucketed by position); an exterior cell's navmesh is
//   read against the 3×3 neighbourhood's refs, because a floor mesh placed in the next cell reaches under this one.
//   Initially-disabled and deleted refs are not floors. No Base Object Swapper layer (FloorFeed's header says why).

using var __run = RunLog.BeginProcess("floorgapprobe", args);
// Known answers for which version of a moved or deleted reference this probe's walk keeps (_shared/MovedRefFixture.cs).
if (args.Length >= 1 && args[0] == "--moved-ref-selftest") return FloorgapSelfTest.Run();

string Instance = HarnessTarget.Instance, GamePath = HarnessTarget.Game, profileName = HarnessTarget.Profile;
string plugin = "Skyrim.esm";
float depth = NavmeshFloor.DefaultMaxDepth, reach = NavmeshFloor.DefaultUpReach, split = NavmeshFloor.DefaultSplitAbove;
string? tsvPath = null, trisPath = null, verdictsDir = null, orderFile = null, jsonPath = null;
bool ownOnly = false, winning = false, flagsOnly = false;
// reach mode, opt-in (G1 Stage B): --band LO,HI adds the distance to the navmesh swept up from +LO to +HI (a standing
// body); --under adds the floor straight under the subject (this probe's floor rule, --depth / --reach window).
float[]? band = null; bool under = false;
// clear mode: --band LO,HI is the body's column above the floor (default the humanoid band); --within R names a point
// blocked when an actor-solid surface stands nearer than R to the column, and caps the search there.
float within = float.NaN;
int maxCells = int.MaxValue;
Vec3? at = null; float heading = 0f, span = 384f, every = 16f;
var positional = new List<string>();
for (int i = 0; i < args.Length; i++)
{
    string a = args[i];
    string Next() => i + 1 < args.Length ? args[++i] : throw new ArgumentException($"{a} needs a value");
    switch (a)
    {
        case "--instance": Instance = Next(); break;
        case "--game": GamePath = Next(); break;
        case "--profile": profileName = Next(); break;
        case "--plugin": plugin = Next(); break;
        case "--depth": depth = float.Parse(Next(), CultureInfo.InvariantCulture); break;
        case "--reach": reach = float.Parse(Next(), CultureInfo.InvariantCulture); break;
        case "--split": split = float.Parse(Next(), CultureInfo.InvariantCulture); break;
        case "--tsv": tsvPath = Next(); break;
        case "--json": jsonPath = Next(); break;
        case "--band": { var b = Next().Split(','); band = new[] { F(b[0]), F(b[1]) }; if (!(band[1] > band[0])) { Console.Error.WriteLine("--band LO,HI needs HI > LO"); return 2; } break; }
        case "--under": under = true; break;
        case "--within": within = float.Parse(Next(), CultureInfo.InvariantCulture); break;
        case "--tris": trisPath = Next(); break;
        case "--verdicts": verdictsDir = Next(); break;
        case "--own": ownOnly = true; break;
        case "--winning": winning = true; break;
        case "--order": orderFile = Next(); break;
        case "--flags": flagsOnly = true; break;
        case "--heading": heading = float.Parse(Next(), CultureInfo.InvariantCulture); break;
        case "--span": span = float.Parse(Next(), CultureInfo.InvariantCulture); break;
        case "--every": every = float.Parse(Next(), CultureInfo.InvariantCulture); break;
        case "--at": { var p = Next().Split(','); at = new Vec3(F(p[0]), F(p[1]), p.Length > 2 ? F(p[2]) : -99999f); break; }
        // --state is refused rather than ignored: this probe reads one source and has no census to flip.
        case "--state":
            try { PopulationCli.Resolve("floorgapprobe", false, Next(), stateSupported: false); }
            catch (ArgumentException ex) { Console.Error.WriteLine(ex.Message); }
            return 2;
        default:
            if (a.StartsWith("--")) { Console.Error.WriteLine($"unknown flag {a}"); return 2; }
            positional.Add(a); break;
    }
}
static float F(string s) => float.Parse(s, CultureInfo.InvariantCulture);
if (positional.Count == 0) { Console.Error.WriteLine("usage: floorgapprobe sweep [maxCells] | cell <Cell> | line <Cell> --at X,Y,Z --heading DEG | points <Cell> <file> | clear <Cell> <file>"); return 2; }
string mode = positional[0];
Instance = HarnessTarget.Require(Instance, "MO2 instance directory", "instance", "--instance");
GamePath = HarnessTarget.Require(GamePath, "game directory", "game", "--game");
profileName = HarnessTarget.Require(profileName, "MO2 profile name", "profile", "--profile");
string profileDir = Path.Combine(Instance, "profiles", profileName);
string modsDir = Path.Combine(Instance, "mods"), overwriteDir = Path.Combine(Instance, "overwrite"), dataDir = Path.Combine(GamePath, "Data");
if (mode == "sweep" && positional.Count > 1 && int.TryParse(positional[1], out var mc)) maxCells = mc;

var sw = Stopwatch.StartNew();
Console.WriteLine($"floorgapprobe {mode}: {(winning ? "the WINNING state of the profile's load order" : $"plugin {plugin}")}; instance {Instance} / {profileName};{(ownOnly ? " OWN-CELL refs and land only;" : "")} floor rule: highest up-facing actor-solid surface within {depth:0} u below .. {reach:0} u above the navmesh; triangles longer than {split:0} u read at four points");
Console.WriteLine($"   actor-solid layers: {string.Join(", ", CollisionLayers.ActorSolid)}");

// ---- the VFS, for the meshes ----
// --order (with --winning): a candidate configuration through the shared resolver, its asset view included.
ResolvedConfiguration? cfg = null;
if (orderFile is not null)
{
    if (!winning) { Console.Error.WriteLine("--order describes a load order to walk, so it needs --winning"); return 2; }
    cfg = HarnessOrderCli.Resolve(profileName, profileDir, modsDir, dataDir, overwriteDir, GamePath, orderFile, null, null);
    if (cfg.Refused) { Console.Error.WriteLine(cfg.RefusedReason); return 2; }
    HarnessOrderCli.Print(cfg);
}
AssetResolver assetsBuilt;
int bsaCount;
if (cfg is null)
{
    var comp = Mo2LoadOrder.ReadComposition(profileDir);
    var arch = ArchiveDiscovery.Discover(profileDir, modsDir, dataDir, overwriteDir, GamePath);
    assetsBuilt = AssetResolver.Build(overwriteDir, modsDir, dataDir, comp.EnabledMods, arch.Archives);
    bsaCount = arch.Archives.Count;
}
else
{
    assetsBuilt = AssetResolver.Build(overwriteDir, modsDir, dataDir, cfg.EnabledMods, cfg.Archives.Archives);
    bsaCount = cfg.Archives.Archives.Count;
}
using var assets = assetsBuilt;
Console.WriteLine($"assets: {bsaCount} BSAs indexed ({sw.ElapsedMilliseconds} ms)");

// ---- the plugin: base records with a mesh, worldspace names, every cell with its landscape ----
// --plugin: the game's Data folder first, else the profile's load order (PluginOpen.Locate; a mod's plugin lives in its mod folder).
var walk = new List<string>();
if (winning)
{
    if (mode == "sweep") { Console.Error.WriteLine("--winning is for cell, line, points and reach (one cell resolved through the load order), not sweep"); return 2; }
    walk.AddRange(cfg is not null ? cfg.OrderedPaths : Mo2LoadOrder.Build(profileDir, modsDir, dataDir, overwriteDir).OrderedPaths);
    if (cfg is null && HarnessShared.OrderMasters.RefusalForProfile(profileDir, modsDir, dataDir, overwriteDir, walk) is { } missingMaster) { Console.Error.WriteLine(missingMaster); return 2; }   // ruling 12: the game will not load it
}
else
{
    // Exempt from the missing-master refusal (ruling 12): the order only finds this one plugin's file; no order is evaluated.
    var pluginPath = HarnessShared.PluginOpen.Locate(plugin, dataDir, () => Mo2LoadOrder.Build(profileDir, modsDir, dataDir, overwriteDir).OrderedPaths);
    if (pluginPath is null) { Console.Error.WriteLine(HarnessShared.PluginOpen.NotLocated(plugin, dataDir)); return 2; }
    walk.Add(pluginPath);
}
var baseMesh = new Dictionary<FormKey, (string Mesh, string Type)>();
// The enable facts of every placement the walk sees (Q27): a floor candidate's enable parent is often in another cell —
// a worldspace's persistent cell holds the markers — and resolving the chain over one cell's own refs answered "enabled,
// unresolved" for those. Gathered here, used by the population below.
var enableFacts = new EnableFactsIndex();
var worldName = new Dictionary<FormKey, string>();
// The walk's accumulation (the floor filing, and under --winning the one merged cell) lives in FloorgapWalk.cs, so
// --moved-ref-selftest runs the same code.
string? wantCell = winning && positional.Count > 1 ? positional[1] : null;
var fwalk = new FloorgapWalk(winning, wantCell);
var refsByWorld = fwalk.RefsByWorld;
var landByWorld = fwalk.LandByWorld;
var cells = fwalk.Cells;
foreach (var path in walk)
{
    ISkyrimModGetter mod;
    try { mod = fwalk.Open(path)!; }
    catch (PluginUnreadableException ex) { Console.Error.WriteLine(ex.Message); return 2; }
    try
    {
        using (mod as IDisposable)
        {
            foreach (var rec in mod.EnumerateMajorRecords())
            {
                if (rec is IPlacedGetter pg) { if (pg.IsDeleted) enableFacts.NoteDeleted(pg.FormKey); continue; }
                if (rec is IModeledGetter m && m.Model?.File is { } f && !string.IsNullOrWhiteSpace(f.GivenPath))
                    baseMesh[rec.FormKey] = (f.GivenPath, rec.GetType().Name.Replace("BinaryOverlay", ""));
            }
            foreach (var wrld in mod.Worldspaces.Records) worldName[wrld.FormKey] = wrld.EditorID ?? wrld.FormKey.ToString();
            fwalk.Plugin(mod, path, cp => { foreach (var r in cp.Refs) enableFacts.Note(r); });
        }
    }
    // A plugin opens lazily: one whose groups fail while this walk reads them is refused, named (_shared/PluginOpen.cs).
    catch (Exception ex) when (ex is not PluginUnreadableException)
    { Console.Error.WriteLine(new PluginUnreadableException(path, "floorgapprobe's walk", ex, midWalk: true).Message); return 2; }
    catch (PluginUnreadableException ex) { Console.Error.WriteLine(ex.Message); return 2; }
}
fwalk.Dump();
var mergedMeta = fwalk.MergedMeta; var mergedLand = fwalk.MergedLand;
var mergedRefs = fwalk.MergedRefs; var mergedNavs = fwalk.MergedNavs; var pluginsWithCell = fwalk.PluginsWithCell;
if (winning)
{
    if (mergedMeta is null) { Console.Error.WriteLine($"cell '{wantCell}' is in no plugin of the load order"); return 2; }
    cells.Add(mergedMeta with { Refs = mergedRefs.Values.ToList(), Navmeshes = mergedNavs.Values.ToList(), Land = mergedLand,
                                LandUnreadableReason = fwalk.MergedLandUnreadable });
    Console.WriteLine($"winning state of {mergedMeta.CellEditorId ?? mergedMeta.Cell.ToString()}: {pluginsWithCell.Count} plugin(s) touch it ({string.Join(", ", pluginsWithCell)}); {mergedRefs.Count:N0} refs, {mergedNavs.Count:N0} navmeshes after the walk of {walk.Count} plugins");
}
// The run's population (Q27, _shared/EnablePopulation.cs): the load-time enable chain over every placement walked, or
// the record's own flag under --flags.
var pop = flagsOnly ? EnablePopulation.Flags(enableFacts) : EnablePopulation.LoadTime(enableFacts);
Console.WriteLine($"base records with a mesh: {baseMesh.Count:N0} ({sw.Elapsed.TotalSeconds:0.0} s)");
Console.WriteLine($"cells: {cells.Count:N0} with content ({cells.Count(c => c.Interior):N0} interior, {refsByWorld.Count} worldspaces; {landByWorld.Values.Sum(w => w.Count):N0} LAND records) ({sw.Elapsed.TotalSeconds:0.0} s)");

// ---- geometry per mesh path: the actor-solid collision soup, as navprobe reads it ----
var geoCache = new Dictionary<string, Geo>(StringComparer.OrdinalIgnoreCase);
int measured = 0, measureFail = 0;
long fallbackRefs = 0, solidRefs = 0;
Geo Geometry(string rel)
{
    if (geoCache.TryGetValue(rel, out var hit)) return hit;
    var full = rel.StartsWith("meshes", StringComparison.OrdinalIgnoreCase) ? rel : Path.Combine("meshes", rel);
    var res = assets.ResolveForPlacement(full);
    if (res.Sources.Count == 0) { measureFail++; return geoCache[rel] = new Geo(Aabb.Empty, null, "unresolved"); }
    var (bytes, _) = AssetResolver.ReadPlacementSource(res.Sources[0]);
    if (bytes is null) { measureFail++; return geoCache[rel] = new Geo(Aabb.Empty, null, "unresolved"); }
    var o = NifGeometry.Measure(bytes, includeTriangles: true);
    if (o.Model is null) { measureFail++; return geoCache[rel] = new Geo(Aabb.Empty, null, "unreadable"); }
    measured++;
    var m = o.Model;
    bool anySolid = m.CollisionBodies is not null && m.CollisionBodies.Any(b => b.Solid && CollisionLayers.IsActorSolid(b.Layer));
    if (!m.HasCollision || !anySolid) return geoCache[rel] = new Geo(Aabb.Empty, null, "none");
    var solid = m.CollisionSoupFor(b => b.Solid && CollisionLayers.IsActorSolid(b.Layer));
    if (m.CollisionComplete && solid is { TriangleCount: > 0 })
    {
        var box = Aabb.Empty;
        foreach (var v in solid.Vertices) box = box.Add(v);
        return geoCache[rel] = new Geo(box, solid, "collision");
    }
    return geoCache[rel] = new Geo(m.Bounds, m.Soup, "fallback");
}
(Aabb Box, TriangleSoup Soup)? FloorGeometry(PlacedRef r)
{
    if (r.Base is not { } b || !baseMesh.TryGetValue(b, out var bm)) return null;
    var g = Geometry(bm.Mesh);
    if (g.Soup is null || g.Box.IsEmpty) return null;
    if (g.Source == "fallback") fallbackRefs++; else solidRefs++;
    return (g.Box, g.Soup);
}

// ---- a cell's floor field: own refs and land (an interior, or --own), else the 3×3 neighbourhood by position ----
// No fallback offset here: this probe reports "no floor" as such, never a stand-in.
// The refs go through the load-time enable chain (R1: a switch child whose marker is initially disabled is not there when
// the cell loads — Q25: Obscure's council dais read as the floor 48 u over the Arch-Mage quarters' navmesh until this);
// --flags reads the record flag alone, the population the route rule's default run judges.
// A floor over a LAND that gives no heights (FloorFeed.FloorIndex, CellPlacements.LandUnreadableReason) is not measured:
// the one-cell modes refuse, the sweep counts and names the cell. Null when the floor's LAND is sound.
string? LandProblem(CellPlacements cp)
{
    if (cp.Interior) return null;
    if (ownOnly || cp.Grid is not { } g) return cp.LandUnreadableReason;
    var bad = fwalk.Floor.UnreadableNear(cp.Worldspace, g);
    if (cp.LandUnreadableReason is { } own && bad.Count == 0) bad.Add($"the cell's own LAND: {own}");
    return bad.Count == 0 ? null : string.Join("; ", bad);
}
long landUnjudged = 0;
FloorField Field(CellPlacements cp)
{
    if (cp.Interior || ownOnly || cp.Grid is not { } g || !refsByWorld.TryGetValue(cp.Worldspace, out var rg))
        return FloorFeed.Build(cp.Refs, FloorGeometry, cp.Land is { } own && cp.Grid is { } og ? new[] { (og, own) } : null, 0f, depth, reach,
                               pop.Predicate);
    var (nbRefs, nbLand) = fwalk.Floor.NeighbourhoodOf(cp.Worldspace, g);   // the 3x3 by position plus the reach (FloorFeed)
    return FloorFeed.Build(nbRefs, FloorGeometry, nbLand, 0f, depth, reach, pop.Predicate);
}

// Ground under (x,y) in a cell's navmesh: every non-deleted triangle covering the point (corridorprobe's GroundAt).
List<float> GroundAt(NavmeshIndex navs, Aabb extent, float x, float y)
{
    var zs = new List<float>();
    var probe = new Aabb(new Vec3(x - 0.5f, y - 0.5f, extent.Min.Z - 512), new Vec3(x + 0.5f, y + 0.5f, extent.Max.Z + 512), false);
    foreach (var hit in navs.Query(probe))
    {
        if ((hit.Flags & 0x08) != 0) continue;
        var nav = navs.NavAt(hit.NavIndex); var tri = nav.Triangles[hit.TriangleIndex];
        if (RouteConnectivity.HeightAt(nav.Vertices[tri.A], nav.Vertices[tri.B], nav.Vertices[tri.C], x, y) is { } z) zs.Add(z);
    }
    return zs;
}

CellPlacements? FindCell(string id)
{
    FormKey? fk = null;
    try { fk = FormKey.Factory(id); } catch { }
    return cells.FirstOrDefault(c => (fk is { } k && c.Cell == k) || string.Equals(c.CellEditorId, id, StringComparison.OrdinalIgnoreCase));
}
string Name(CellPlacements e) => e.CellEditorId ?? e.Cell.ToString();
string World(CellPlacements e) => e.Interior ? "(interior)" : worldName.TryGetValue(e.Worldspace, out var n) ? n : e.Worldspace.ToString();

// ================================================================================================ reach ====
if (mode == "reach")
{
    if (positional.Count < 3 || jsonPath is null) { Console.Error.WriteLine("reach needs <Cell> <subjects.json> --json OUT"); return 2; }
    var e = FindCell(positional[1]);
    if (e is null) { Console.Error.WriteLine($"cell '{positional[1]}' not found"); return 2; }
    using var sdoc = System.Text.Json.JsonDocument.Parse(File.ReadAllBytes(positional[2]));
    var ids = sdoc.RootElement.GetProperty("subjects").EnumerateArray().Select(x => x.GetProperty("id").GetString()!).ToList();
    var tris = new List<NavTriangle>();
    foreach (var nv in e.Navmeshes)
    {
        if (nv.Deleted) continue;
        for (int t = 0; t < nv.Triangles.Count; t++)
        {
            var tr = nv.Triangles[t];
            if (tr.A < 0 || tr.B < 0 || tr.C < 0 || tr.A >= nv.Vertices.Count || tr.B >= nv.Vertices.Count || tr.C >= nv.Vertices.Count) continue;
            Vector3 V3(Vec3 v) => new(v.X, v.Y, v.Z);
            tris.Add(new NavTriangle(V3(nv.Vertices[tr.A]), V3(nv.Vertices[tr.B]), V3(nv.Vertices[tr.C]), (ushort)tr.Flags, nv.FormKey.ToString(), t));
        }
    }
    var field = Field(e);
    var byKey = e.Refs.ToDictionary(r => r.FormKey.ToString(), r => r, StringComparer.OrdinalIgnoreCase);
    // A reference filed in the worldspace's persistent cell stands in the grid cell its position falls in (FloorgapWalk).
    var persistentHere = fwalk.PersistentStandingIn(e).Values.ToDictionary(r => r.FormKey.ToString(), r => r, StringComparer.OrdinalIgnoreCase);
    var rows = new List<object>();
    foreach (var id in ids)
    {
        string? placement = null;
        if (!byKey.TryGetValue(id, out var r))
        {
            if (!persistentHere.TryGetValue(id, out r)) { rows.Add(new { id, status = "refused", reason = "no winning placement of this reference in the cell" }); continue; }
            placement = "the worldspace's persistent cell; stands in this cell's grid by position";
        }
        // A row found through the persistent cell carries its placement; every other row is written exactly as before.
        void AddRow(object row)
        {
            if (placement is null) { rows.Add(row); return; }
            var node = System.Text.Json.JsonSerializer.SerializeToNode(row)!.AsObject();
            node.Add("placement", placement);
            rows.Add(node);
        }
        // the shape the distance is measured from (SkyWright.Engine.SubjectGeometry, the choice the stranded check makes too);
        // this instrument measures an unread body from the position and says so in geometryNote
        string? model = r.Base is { } b && baseMesh.TryGetValue(b, out var bm) ? bm.Mesh : null;
        var shape = SubjectGeometry.Of(r, model, mesh =>
        {
            var full = mesh.StartsWith("meshes", StringComparison.OrdinalIgnoreCase) ? mesh : Path.Combine("meshes", mesh);
            var res = assets.ResolveForPlacement(full);
            return res.Sources.Count == 0 ? null : AssetResolver.ReadPlacementSource(res.Sources[0]).Bytes;
        });
        TriangleSoup? world = shape.World; string geometry = shape.Geometry;
        string? geometryNote = shape.Note;
        var d = world is not null ? NavmeshReach.FromSoup(world, tris) : NavmeshReach.FromPoint(new Vector3(r.Position.X, r.Position.Y, r.Position.Z), tris);
        object? floor = null;
        if (d.Status == "measured" && d.Triangle is { } ti && tris.FirstOrDefault(x => x.Index == ti && x.Navmesh == d.Navmesh) is var nt)
        {
            var c = (nt.A + nt.B + nt.C) / 3f;
            var fr = field.Read(c.X, c.Y, c.Z, out _);
            floor = new { at = "the nearest triangle's centroid", found = fr.Found, offset = fr.Found ? fr.Offset : (float?)null, reason = fr.Found ? null : fr.Reason.ToString() };
        }
        if (band is null && !under)
            AddRow(new { id, geometry, geometryNote, status = d.Status, reason = d.Reason, distance = d.Distance, horizontal = d.Horizontal, vertical = d.Vertical,
                           navmesh = d.Navmesh, triangle = d.Triangle, floor });
        else
        {
            object? bandRow = null, underRow = null;
            if (band is not null)
            {
                var bd = world is not null ? NavmeshReach.FromSoupBand(world, tris, band[0], band[1]) : NavmeshReach.FromPointBand(new Vector3(r.Position.X, r.Position.Y, r.Position.Z), tris, band[0], band[1]);
                bandRow = new { lo = band[0], hi = band[1], status = bd.Status, reason = bd.Reason, distance = bd.Distance, horizontal = bd.Horizontal, vertical = bd.Vertical, navmesh = bd.Navmesh, triangle = bd.Triangle };
            }
            if (under)
            {
                var ur = field.Read(r.Position.X, r.Position.Y, r.Position.Z, out _);
                underRow = new { at = "the subject's position", window = new { below = depth, above = reach }, found = ur.Found, offset = ur.Found ? ur.Offset : (float?)null, reason = ur.Found ? null : ur.Reason.ToString() };
            }
            AddRow(new { id, geometry, geometryNote, status = d.Status, reason = d.Reason, distance = d.Distance, horizontal = d.Horizontal, vertical = d.Vertical,
                           navmesh = d.Navmesh, triangle = d.Triangle, floor, band = bandRow, under = underRow });
        }
    }
    var outDoc = new
    {
        schema = "housecarl.navmesh-distance/1", policy = NavmeshReach.PolicyVersion, cell = Name(e), interior = e.Interior,
        scope = e.Interior ? "the cell's navmesh" : "the cell's own navmesh only: an upper bound (neighbouring cells not searched)",
        note = "distance to the nearest winning navmesh triangle (where NPCs path), not every place a player can stand",
        triangles = tris.Count, deletedSkipped = tris.Count(t => NavmeshReach.IsDeleted(t.Flags)), subjects = rows,
    };
    File.WriteAllText(jsonPath, System.Text.Json.JsonSerializer.Serialize(outDoc, new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
    Console.WriteLine($"wrote navmesh distances: {rows.Count} subject(s), {tris.Count:N0} triangles -> {jsonPath}");
    return 0;
}

// ================================================================================================ clear ====
// clear <Cell> <file>: per line "x y floorZ [label]", the horizontal clearance of a standing body's column, floorZ + LO ..
// floorZ + HI (--band; default the humanoid band ActorBand.PhysicalStep .. PhysicalControllerTop(ScaleOneHeight)), against
// the floor rule's own population and geometry: every reference of the cell's 3x3 neighbourhood that is there at load
// (the enable chain, or the flag under --flags), by its actor-solid collision where complete, else its render soup
// (labelled fallback). SkyWright.Engine.ColumnClearance clips each triangle to the column's heights and reads the horizontal
// distance; the nearest reference is named. --within R reads "blocked" below R and caps the search at R.
if (mode == "clear")
{
    if (positional.Count < 3) { Console.Error.WriteLine("clear needs <Cell> <file of x y floorZ [label] lines>"); return 2; }
    var e = FindCell(positional[1]);
    if (e is null) { Console.Error.WriteLine($"cell '{positional[1]}' not found"); return 2; }
    float lo = band?[0] ?? ActorBand.PhysicalStep, hi = band?[1] ?? ActorBand.PhysicalControllerTop(ActorBand.ScaleOneHeight);
    float cap = float.IsNaN(within) ? 1024f : within;
    var refs = e.Interior || ownOnly || e.Grid is not { } cg || !refsByWorld.ContainsKey(e.Worldspace)
        ? e.Refs : fwalk.Floor.NeighbourhoodOf(e.Worldspace, cg).Refs;
    var enabledAt = pop.Predicate;
    var placed = new List<(PlacedRef R, Aabb World, Geo G)>();
    int skippedOff = 0;
    foreach (var r in refs)
    {
        if (r.Deleted || r.Base is not { } b || !baseMesh.TryGetValue(b, out var bm)) continue;
        if (enabledAt is null ? r.InitiallyDisabled : !enabledAt(r.FormKey)) { skippedOff++; continue; }
        var g = Geometry(bm.Mesh);
        if (g.Soup is null || g.Box.IsEmpty) continue;
        placed.Add((r, SpatialOverlap.ToWorld(g.Box, r.Position, r.Rotation, r.Scale), g));
    }
    var worldSoup = new Dictionary<FormKey, TriangleSoup>();
    Console.WriteLine($"{Name(e)} ({World(e)}): {placed.Count:N0} references with geometry ({skippedOff:N0} not there at load); column floor +{lo:0.#} .. +{hi:0.#} u; search cap {cap:0} u{(float.IsNaN(within) ? "" : $", blocked below {within:0} u")}");
    Console.WriteLine($"   {"x",9} {"y",9} {"floorZ",8} {"clear",7}  verdict  nearest (geometry)  label");
    var rows = new List<object>();
    foreach (var raw in File.ReadLines(positional[2]))
    {
        var parts = raw.Split(new[] { ' ', '	', ',' }, StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 3 || !float.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out float x)) continue;
        float y = F(parts[1]), fz = F(parts[2]);
        string label = parts.Length > 3 ? string.Join(" ", parts.Skip(3)) : "";
        double z0 = fz + lo, z1 = fz + hi, best = cap; PlacedRef? who = null; string? src = null;
        foreach (var (r, wb, g) in placed)
        {
            if (wb.Max.Z < z0 || wb.Min.Z > z1) continue;
            double dx = Math.Max(0, Math.Max(wb.Min.X - x, x - wb.Max.X)), dy = Math.Max(0, Math.Max(wb.Min.Y - y, y - wb.Max.Y));
            if (Math.Sqrt(dx * dx + dy * dy) >= best) continue;
            if (!worldSoup.TryGetValue(r.FormKey, out var ws))
                worldSoup[r.FormKey] = ws = SpatialOverlap.TransformSoup(g.Soup!, r.Position, r.Rotation, r.Scale);
            var rd = ColumnClearance.Measure(ws, x, y, z0, z1, best);
            if (rd.Triangle >= 0 && rd.Distance < best) { best = rd.Distance; who = r; src = g.Source; }
        }
        string verdict = float.IsNaN(within) ? "-" : best < within ? "blocked" : "clear";
        string model = who?.Base is { } wbk && baseMesh.TryGetValue(wbk, out var wm) ? Path.GetFileName(wm.Mesh) : "";
        Console.WriteLine($"   {x,9:0.0} {y,9:0.0} {fz,8:0.0} {(who is null ? $">={cap:0}" : best.ToString("0.0")),7}  {verdict,-7}  {(who is null ? "none within the cap" : $"{who.FormKey} {model} ({src})")}  {label}");
        rows.Add(new { x, y, floorZ = fz, label, clearance = who is null ? (double?)null : Math.Round(best, 2), atLeast = who is null ? cap : (float?)null,
                       verdict, nearest = who?.FormKey.ToString(), model, geometry = src });
    }
    if (jsonPath is not null)
    {
        File.WriteAllText(jsonPath, System.Text.Json.JsonSerializer.Serialize(new
        {
            schema = "housecarl.column-clearance/1", cell = Name(e), profile = profileName, winning, columnAboveFloor = new[] { lo, hi },
            cap, within = float.IsNaN(within) ? (float?)null : within,
            note = "horizontal distance from the point to the nearest actor-solid surface inside the column's heights (render soup where a model's collision is incomplete: geometry 'fallback')",
            points = rows,
        }, new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
        Console.WriteLine($"wrote {rows.Count} point(s) -> {jsonPath}");
    }
    return 0;
}

// ================================================================================================ line / points ====
if (mode is "line" or "points")
{
    if (positional.Count < 2) { Console.Error.WriteLine($"{mode} needs a cell"); return 2; }
    var e = FindCell(positional[1]);
    if (e is null) { Console.Error.WriteLine($"cell '{positional[1]}' not found in {plugin}"); return 2; }
    if (LandProblem(e) is { } lw)
    { Console.Error.WriteLine($"{Name(e)}: REFUSED — its floor needs a LAND that gives no heights ({lw}); an earlier plugin's heights, or none, would be read in its place."); return 2; }
    var navs = NavmeshIndex.Build(e.Navmeshes, out int malformed);
    var extent = Aabb.Empty;
    foreach (var nv in e.Navmeshes) foreach (var v in nv.Vertices) extent = extent.Add(new Vector3(v.X, v.Y, v.Z));
    var field = Field(e);
    Console.WriteLine($"{Name(e)} ({World(e)}): {e.Navmeshes.Count} navmesh(es), {navs.TriangleCount:N0} triangles ({malformed} malformed); {field.Candidates:N0} floor candidates ({e.Refs.Count:N0} refs in the cell{(e.Interior ? "" : ", 3×3 neighbourhood by position")}; {field.SkippedDisabled:N0} disabled at load by their enable chain, {(flagsOnly ? "flag only" : "R1")}); terrain {(e.Interior ? "none (interior)" : $"{field.LandCells} LAND records")}");
    var refBase = new Dictionary<FormKey, FormKey?>();
    foreach (var r in e.Refs) refBase[r.FormKey] = r.Base;
    // the placed object whose surface a reading's floor came from, and its base's model file
    string Supplier(float x, float y, float floorZ)
    {
        var k = field.Supplier(x, y, floorZ);
        if (k is null) return "";
        string model = refBase.TryGetValue(k.Value, out var b) && b is { } bk && baseMesh.TryGetValue(bk, out var bm) ? Path.GetFileName(bm.Mesh) : "?";
        return $"{k.Value} {model}";
    }

    float NearestStorey(List<float> zs, float z0) => zs.Count == 0 ? float.NaN : zs.OrderBy(z => MathF.Abs(z - z0)).First();

    if (mode == "line")
    {
        if (at is null) { Console.Error.WriteLine("line needs --at X,Y,Z"); return 2; }
        float z0 = at.Value.Z;
        if (z0 <= -99999f)
        {
            var zs = GroundAt(navs, extent, at.Value.X, at.Value.Y);
            if (zs.Count == 0) { Console.Error.WriteLine("no navmesh under the point"); return 2; }
            z0 = zs.Min();
        }
        float rad = heading * MathF.PI / 180f;
        var dir = new Vector2(MathF.Sin(rad), MathF.Cos(rad));
        Console.WriteLine($"along heading {heading:0} through ({at.Value.X:0}, {at.Value.Y:0}, {z0:0}), every {every:0} u for {span:0} u each way; δ = navmesh − floor (+ = the navmesh floats above the floor)");
        Console.WriteLine($"   {"s",6} {"x",8} {"y",8} {"navZ",7} {"floorZ",7} {"δ",6}  {"source",-10} {"note"}  supplier");
        float zPrev = z0;
        for (float s = -span; s <= span + 1e-3f; s += every)
        {
            float x = at.Value.X + dir.X * s, y = at.Value.Y + dir.Y * s;
            float navZ = NearestStorey(GroundAt(navs, extent, x, y), zPrev);
            if (float.IsNaN(navZ)) { Console.WriteLine($"   {s,6:+0;-0} {x,8:0} {y,8:0} {"-",7}  (no navmesh)"); continue; }
            zPrev = navZ;
            var r = field.Read(x, y, navZ, out float saddle);
            Console.WriteLine($"   {s,6:+0;-0} {x,8:0} {y,8:0} {navZ,7:0.0} {(r.Found ? r.FloorZ.ToString("0.0") : "-"),7} {(r.Found ? r.Offset.ToString("+0.0;-0.0") : "-"),6}  {(r.Found ? r.Source.ToString() : r.Reason.ToString()),-10} {(r.Embedded ? "embedded " : "")}{r.Surfaces} surface(s){(saddle > 0.5f ? $", land ±{saddle:0.0}" : "")}{(r.Found && r.Source == FloorSource.Mesh ? "  " + Supplier(x, y, r.FloorZ) : "")}");
        }
        return 0;
    }

    // points: a walker's recorded positions (feet), each read against the navmesh storey nearest its own z.
    if (positional.Count < 3) { Console.Error.WriteLine("points needs a file of x y z lines"); return 2; }
    var diffs = new List<float>(); var offs = new List<float>(); int noNav = 0, noFloorPts = 0, n = 0;
    Console.WriteLine($"   {"x",8} {"y",8} {"feet",7} {"navZ",7} {"floorZ",7} {"δ",6} {"feet−floor",10}  source  supplier");
    foreach (var raw in File.ReadLines(positional[2]))
    {
        var parts = raw.Split(new[] { ' ', '\t', ',' }, StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 3 || !float.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out float x)) continue;
        float y = F(parts[1]), z = F(parts[2]);
        n++;
        float navZ = NearestStorey(GroundAt(navs, extent, x, y), z);
        if (float.IsNaN(navZ) || MathF.Abs(navZ - z) > depth) { noNav++; continue; }
        var r = field.Read(x, y, navZ);
        if (!r.Found) { noFloorPts++; Console.WriteLine($"   {x,8:0} {y,8:0} {z,7:0.0} {navZ,7:0.0} {"-",7} {"-",6} {"-",10}  {r.Reason}"); continue; }
        diffs.Add(z - r.FloorZ); offs.Add(r.Offset);
        Console.WriteLine($"   {x,8:0} {y,8:0} {z,7:0.0} {navZ,7:0.0} {r.FloorZ,7:0.0} {r.Offset,6:+0.0;-0.0} {z - r.FloorZ,10:+0.0;-0.0}  {r.Source}{(r.Embedded ? " embedded" : "")}{(r.Source == FloorSource.Mesh ? "  " + Supplier(x, y, r.FloorZ) : "")}");
    }
    Console.WriteLine($"\n   {n} points: {diffs.Count} read, {noNav} with no navmesh storey within {depth:0} u of the feet, {noFloorPts} with no floor");
    if (diffs.Count > 0)
    {
        Console.WriteLine($"   feet − floor: median {NavmeshFloor.Quantile(diffs, 0.5f):+0.0;-0.0}, p10 {NavmeshFloor.Quantile(diffs, 0.1f):+0.0;-0.0}, p90 {NavmeshFloor.Quantile(diffs, 0.9f):+0.0;-0.0}, within ±2 u: {100.0 * diffs.Count(d => MathF.Abs(d) <= 2f) / diffs.Count:0}%");
        Console.WriteLine($"   δ (navmesh − floor) at those points: median {NavmeshFloor.Quantile(offs, 0.5f):+0.0;-0.0}, p10 {NavmeshFloor.Quantile(offs, 0.1f):+0.0;-0.0}, p90 {NavmeshFloor.Quantile(offs, 0.9f):+0.0;-0.0}");
    }
    return 0;
}

// ================================================================================================ sweep / cell ====
var verdicts = new Dictionary<FormKey, (string Verdict, string Mesh)>();
if (verdictsDir is not null)
{
    foreach (var (file, v) in new[] { ("route-cut-blocked.tsv", "BLOCKED"), ("route-cut-likely.tsv", "LIKELY") })
    {
        var p = Path.Combine(verdictsDir, file);
        if (!File.Exists(p)) { Console.Error.WriteLine($"--verdicts: {p} not found"); return 2; }
        foreach (var line in File.ReadLines(p).Skip(1))
        {
            var cols = line.Split('\t');
            if (cols.Length < 4) continue;
            try { verdicts[FormKey.Factory(cols[0])] = (v, cols[3]); } catch { }
        }
    }
    Console.WriteLine($"verdicts: {verdicts.Count(kv => kv.Value.Verdict == "BLOCKED"):N0} BLOCKED, {verdicts.Count(kv => kv.Value.Verdict == "LIKELY"):N0} LIKELY refs from {verdictsDir}");
}

List<CellPlacements> toSweep;
if (mode == "cell")
{
    if (positional.Count < 2) { Console.Error.WriteLine("cell needs a cell"); return 2; }
    var e = FindCell(positional[1]);
    if (e is null) { Console.Error.WriteLine($"cell '{positional[1]}' not found in {plugin}"); return 2; }
    toSweep = new List<CellPlacements> { e };
}
else if (mode == "sweep") toSweep = cells.Where(c => c.Navmeshes.Count > 0 && c.Refs.Count > 0).Take(maxCells).ToList();
else { Console.Error.WriteLine("mode must be sweep, cell, line or points"); return 2; }

using var trisOut = trisPath is null ? null : new StreamWriter(trisPath);
trisOut?.WriteLine("cell\tworldspace\tnavmesh\ttri\tcx\tcy\tnavZ\tfloorZ\tdelta\tsource\treason\tembedded\tsamples\tfound\twater");
using var cellsOut = tsvPath is null ? null : new StreamWriter(tsvPath);
cellsOut?.WriteLine("cell\tworldspace\tinterior\tgrid\ttris\tfound\tmesh\tland\tnoSurface\tonlyDeeper\tonlyHigher\tonlyDownFacing\twaterNoFloor\tembedded\tmedian\tp10\tp90\tstoreys\tmaxStoreySpread\tlandSaddleP90");

// ---- accumulators ----
var hist = new Dictionary<string, List<float>>();   // group -> per-triangle δ
List<float> Group(string g) { if (!hist.TryGetValue(g, out var l)) hist[g] = l = new List<float>(); return l; }
var cellMedians = new Dictionary<string, List<float>>();
var cellRows = new List<CellRow>();
long trisTotal = 0, trisDeleted = 0, samplesTotal = 0, trisFound = 0, trisMesh = 0, trisLand = 0, trisEmbedded = 0, trisWaterNoFloor = 0;
var noFloor = new long[5];
var saddles = new List<float>();
var verdictRows = new List<(FormKey Key, string Cell, string Verdict, string Mesh, int N, float Local, float CellMedian)>();
int verdictNoSample = 0;
Span<Vector3> pts = stackalloc Vector3[4];
Span<FloorReading> reads = stackalloc FloorReading[4];
int swept = 0;
__run.Feature("cells", toSweep.Count, "cells").Stage("sweep", "sweeping cells", toSweep.Count, "cells");


foreach (var e in toSweep)
{
    __run.Advance();
    if (LandProblem(e) is { } lw)
    {
        if (mode != "sweep")
        { Console.Error.WriteLine($"{Name(e)}: REFUSED — its floor needs a LAND that gives no heights ({lw}); an earlier plugin's heights, or none, would be read in its place."); return 2; }
        Console.WriteLine($"   UNJUDGEABLE {Name(e)}: its floor needs a LAND that gives no heights ({lw})");
        landUnjudged++;
        continue;
    }
    swept++;
    var field = Field(e);
    var cellOffs = new List<float>(); var cellNavZ = new List<float>();
    var samples = new List<(float X, float Y, float NavZ)>();
    int cFound = 0, cMesh = 0, cLand = 0, cEmb = 0, cWater = 0, cTris = 0; var cNo = new int[5];
    foreach (var nav in e.Navmeshes)
    {
        for (int t = 0; t < nav.Triangles.Count; t++)
        {
            var tri = nav.Triangles[t];
            if (tri.A < 0 || tri.B < 0 || tri.C < 0 || tri.A >= nav.Vertices.Count || tri.B >= nav.Vertices.Count || tri.C >= nav.Vertices.Count) continue;
            if ((tri.Flags & 0x08) != 0) { trisDeleted++; continue; }
            bool water = (tri.Flags & 0x200) != 0;
            trisTotal++; cTris++;
            int n = NavmeshFloor.SamplePoints(nav.Vertices[tri.A], nav.Vertices[tri.B], nav.Vertices[tri.C], split, pts);
            for (int k = 0; k < n; k++)
            {
                reads[k] = field.Read(pts[k].X, pts[k].Y, pts[k].Z, out float saddle);
                if (reads[k].Found && reads[k].Source == FloorSource.Land) saddles.Add(saddle);
                samples.Add((pts[k].X, pts[k].Y, pts[k].Z));
            }
            samplesTotal += n;
            var red = NavmeshFloor.Reduce(reads[..n]);
            cellNavZ.Add(pts[0].Z);
            if (red.Found)
            {
                trisFound++; cFound++;
                if (red.AnyLand) { trisLand++; cLand++; } else { trisMesh++; cMesh++; }
                if (red.AnyEmbedded) { trisEmbedded++; cEmb++; }
                cellOffs.Add(red.Offset);
                string g = e.Interior ? "interior" : red.AnyLand ? "exterior (land floor)" : "exterior (mesh floor)";
                Group(g).Add(red.Offset);
                Group("all").Add(red.Offset);
                if (!e.Interior) Group("world:" + World(e)).Add(red.Offset);
            }
            else
            {
                noFloor[(int)red.Reason]++; cNo[(int)red.Reason]++;
                cellOffs.Add(float.NaN);
                if (water) { trisWaterNoFloor++; cWater++; }
            }
            trisOut?.WriteLine(string.Join('\t', Name(e), World(e), nav.FormKey, t, pts[0].X.ToString("0.0", CultureInfo.InvariantCulture), pts[0].Y.ToString("0.0", CultureInfo.InvariantCulture),
                pts[0].Z.ToString("0.0", CultureInfo.InvariantCulture), red.Found ? (pts[0].Z - red.Offset).ToString("0.0", CultureInfo.InvariantCulture) : "",
                red.Found ? red.Offset.ToString("0.0", CultureInfo.InvariantCulture) : "", red.Found ? (red.AnyLand ? "land" : "mesh") : "", red.Reason, red.AnyEmbedded ? 1 : 0, red.Samples, red.FoundSamples, water ? 1 : 0));
        }
    }

    // storeys: the triangle centroids' navmesh heights clustered the route rule's way; the spread within each
    var (storeyOf, storeys) = NavmeshFloor.Storeys(cellNavZ);
    float maxSpread = float.NaN;
    for (int s = 0; s < storeys; s++)
    {
        var so = new List<float>();
        for (int k = 0; k < cellOffs.Count; k++) if (storeyOf[k] == s && !float.IsNaN(cellOffs[k])) so.Add(cellOffs[k]);
        if (so.Count < 20) continue;
        float spread = NavmeshFloor.Quantile(so, 0.9f) - NavmeshFloor.Quantile(so, 0.1f);
        if (float.IsNaN(maxSpread) || spread > maxSpread) maxSpread = spread;
    }
    var found = cellOffs.Where(o => !float.IsNaN(o)).ToList();
    float med = NavmeshFloor.Quantile(found, 0.5f), p10 = NavmeshFloor.Quantile(found, 0.1f), p90 = NavmeshFloor.Quantile(found, 0.9f);
    var row = new CellRow(Name(e), World(e), e.Interior, e.Grid, cTris, cFound, cMesh, cLand, cNo, cWater, cEmb, med, p10, p90, storeys, maxSpread);
    cellRows.Add(row);
    if (found.Count >= 20)
    {
        if (!cellMedians.TryGetValue(e.Interior ? "interior" : "exterior", out var cm)) cellMedians[e.Interior ? "interior" : "exterior"] = cm = new List<float>();
        cm.Add(med);
    }
    cellsOut?.WriteLine(string.Join('\t', row.Cell, row.World, row.Interior ? 1 : 0, row.Grid is { } gg ? $"{gg.X},{gg.Y}" : "", row.Tris, row.Found, row.Mesh, row.Land,
        cNo[1], cNo[2], cNo[3], cNo[4], cWater, cEmb, Fmt(med), Fmt(p10), Fmt(p90), storeys, Fmt(maxSpread), ""));

    // The δ under the objects a route-cut verdict stands on: the floor the walker stands on when it MEETS the object — the
    // samples in the object's plan footprint grown by the capsule, within its vertical reach, read with the object itself
    // out of the candidates (with it in, a steppable object's own top reads as the floor and δ comes out as minus its height).
    if (verdicts.Count > 0)
        foreach (var r in e.Refs)
        {
            if (!verdicts.TryGetValue(r.FormKey, out var v) || r.Base is null || !baseMesh.TryGetValue(r.Base.Value, out var bm)) continue;
            var geo = Geometry(bm.Mesh);
            if (geo.Box.IsEmpty) { verdictNoSample++; continue; }
            var world = SpatialOverlap.ToWorld(geo.Box, r.Position, r.Rotation, r.Scale);
            var local = new List<float>();
            foreach (var s in samples)
                if (s.X >= world.Min.X - 22f && s.X <= world.Max.X + 22f && s.Y >= world.Min.Y - 22f && s.Y <= world.Max.Y + 22f
                    && s.NavZ >= world.Min.Z - 130f && s.NavZ <= world.Max.Z + 1f)
                {
                    var fr = field.Read(s.X, s.Y, s.NavZ, out _, exclude: r.FormKey);
                    if (fr.Found) local.Add(fr.Offset);
                }
            if (local.Count == 0) { verdictNoSample++; continue; }
            verdictRows.Add((r.FormKey, Name(e), v.Verdict, bm.Mesh, local.Count, NavmeshFloor.Quantile(local, 0.5f), med));
        }

    if (mode == "cell")
    {
        Console.WriteLine($"\n{Name(e)} ({World(e)}): {cTris:N0} triangles, {cFound:N0} with a floor ({cMesh:N0} mesh, {cLand:N0} land), no floor {cTris - cFound:N0} (no surface {cNo[1]}, only deeper {cNo[2]}, only higher {cNo[3]}, only down-facing {cNo[4]}; {cWater} water-flagged), embedded {cEmb}");
        Console.WriteLine($"   δ over the cell: median {Fmt(med)}, p10 {Fmt(p10)}, p90 {Fmt(p90)}; {storeys} storey(s), widest within-storey p10–p90 spread {Fmt(maxSpread)} u");
        for (int s = 0; s < storeys; s++)
        {
            var so = new List<float>(); var sz = new List<float>(); int sn = 0;
            for (int k = 0; k < cellOffs.Count; k++) if (storeyOf[k] == s) { sn++; sz.Add(cellNavZ[k]); if (!float.IsNaN(cellOffs[k])) so.Add(cellOffs[k]); }
            Console.WriteLine($"   storey {s}: navmesh z {sz.Min():0}..{sz.Max():0}, {sn} triangles, {so.Count} floored: median {Fmt(NavmeshFloor.Quantile(so, 0.5f))}, p10 {Fmt(NavmeshFloor.Quantile(so, 0.1f))}, p90 {Fmt(NavmeshFloor.Quantile(so, 0.9f))}");
        }
    }
    if (mode == "sweep" && swept % 500 == 0)
        Console.WriteLine($"   ... {swept:N0} cells, {trisTotal:N0} triangles, {measured:N0} meshes measured, {sw.Elapsed.TotalSeconds:0} s");
}

static string Fmt(float v) => float.IsNaN(v) ? "-" : v.ToString("+0.0;-0.0", CultureInfo.InvariantCulture);

if (mode == "cell") { Console.WriteLine($"\nelapsed {sw.Elapsed.TotalSeconds:0.0} s"); return 0; }

// ================================================================================================ the report ====
Console.WriteLine($"\n==== NAVMESH-TO-FLOOR OFFSET δ (navmesh − floor; + = the navmesh floats above the floor) ====");
Console.WriteLine($"cells swept              : {swept:N0} ({cellRows.Count(r => r.Interior):N0} interior, {cellRows.Count(r => !r.Interior):N0} exterior)");
if (landUnjudged > 0) Console.WriteLine($"cells not measured       : {landUnjudged:N0} UNJUDGEABLE, their floor needs a LAND that gives no heights (named above)");
Console.WriteLine($"navmesh triangles        : {trisTotal:N0} read ({trisDeleted:N0} deleted skipped); {samplesTotal:N0} sample points");
Console.WriteLine($"floor found              : {trisFound:N0} ({100.0 * trisFound / Math.Max(1, trisTotal):0.0}%) — on a mesh {trisMesh:N0}, on the terrain {trisLand:N0}; navmesh embedded in a solid at {trisEmbedded:N0}");
Console.WriteLine($"no floor within reach    : {trisTotal - trisFound:N0} ({100.0 * (trisTotal - trisFound) / Math.Max(1, trisTotal):0.0}%) — no surface {noFloor[1]:N0}, only deeper than {depth:0} u {noFloor[2]:N0}, only higher than {reach:0} u {noFloor[3]:N0}, only down-facing {noFloor[4]:N0}; of these water-flagged {trisWaterNoFloor:N0}");
Console.WriteLine($"floor candidates         : {solidRefs:N0} ref placements on the collision soup, {fallbackRefs:N0} on the render fallback; {measured:N0} meshes measured ({measureFail:N0} unreadable)");
if (saddles.Count > 0) Console.WriteLine($"terrain model            : bilinear over the 33×33 heightmap; the triangulation error bound at the land-floored samples: median {NavmeshFloor.Quantile(saddles, 0.5f):0.0} u, p90 {NavmeshFloor.Quantile(saddles, 0.9f):0.0} u, max {saddles.Max():0.0} u");
Console.WriteLine($"elapsed                  : {sw.Elapsed.TotalSeconds:0.0} s");

Console.WriteLine($"\n   ---- δ over triangles, by population (u) ----");
Console.WriteLine($"   {"group",-24} {"n",9} {"p5",6} {"p10",6} {"p25",6} {"median",7} {"p75",6} {"p90",6} {"p95",6}  {"±4 of median",12} {"±4 of 10",9} {"< -8",6}");
foreach (var g in new[] { "interior", "exterior (mesh floor)", "exterior (land floor)", "all" })
{
    if (!hist.TryGetValue(g, out var l) || l.Count == 0) continue;
    float m = NavmeshFloor.Quantile(l, 0.5f);
    Console.WriteLine($"   {g,-24} {l.Count,9:N0} {NavmeshFloor.Quantile(l, 0.05f),6:0.0} {NavmeshFloor.Quantile(l, 0.1f),6:0.0} {NavmeshFloor.Quantile(l, 0.25f),6:0.0} {m,7:0.0} {NavmeshFloor.Quantile(l, 0.75f),6:0.0} {NavmeshFloor.Quantile(l, 0.9f),6:0.0} {NavmeshFloor.Quantile(l, 0.95f),6:0.0}  {100.0 * l.Count(v => MathF.Abs(v - m) <= 4f) / l.Count,11:0.0}% {100.0 * l.Count(v => MathF.Abs(v - 10f) <= 4f) / l.Count,8:0.0}% {100.0 * l.Count(v => v < -8f) / l.Count,5:0.0}%");
}

Console.WriteLine($"\n   ---- histogram of triangle δ (2 u bins; % of the group) ----");
var groups = new[] { "interior", "exterior (mesh floor)", "exterior (land floor)" }.Where(hist.ContainsKey).ToArray();
Console.WriteLine($"   {"bin",-12} " + string.Join(" ", groups.Select(g => $"{g,22}")));
int[] Bins(List<float> l) { var b = new int[47]; foreach (var v in l) { int k = (int)MathF.Floor((v + 24f) / 2f) + 1; b[Math.Clamp(k, 0, 46)]++; } return b; }
var binsOf = groups.ToDictionary(g => g, g => Bins(hist[g]));
for (int k = 0; k < 47; k++)
{
    string label = k == 0 ? "< -24" : k == 46 ? ">= 66" : $"{-24 + (k - 1) * 2,4} .. {-24 + k * 2,3}";
    if (groups.All(g => binsOf[g][k] == 0)) continue;
    Console.WriteLine($"   {label,-12} " + string.Join(" ", groups.Select(g => $"{100.0 * binsOf[g][k] / hist[g].Count,21:0.00}%")));
}

Console.WriteLine($"\n   ---- exterior δ by worldspace (top 14 by triangles) ----");
Console.WriteLine($"   {"worldspace",-28} {"n",9} {"p10",6} {"median",7} {"p90",6}");
foreach (var kv in hist.Where(kv => kv.Key.StartsWith("world:")).OrderByDescending(kv => kv.Value.Count).Take(14))
    Console.WriteLine($"   {kv.Key[6..],-28} {kv.Value.Count,9:N0} {NavmeshFloor.Quantile(kv.Value, 0.1f),6:0.0} {NavmeshFloor.Quantile(kv.Value, 0.5f),7:0.0} {NavmeshFloor.Quantile(kv.Value, 0.9f),6:0.0}");

Console.WriteLine($"\n   ---- cell medians (cells with 20+ floored triangles) ----");
foreach (var kv in cellMedians)
{
    var l = kv.Value; float m = NavmeshFloor.Quantile(l, 0.5f);
    Console.WriteLine($"   {kv.Key,-10} {l.Count,6:N0} cells: median of medians {m:0.0}, p10 {NavmeshFloor.Quantile(l, 0.1f):0.0}, p90 {NavmeshFloor.Quantile(l, 0.9f):0.0}; cells within ±4 u of it {100.0 * l.Count(v => MathF.Abs(v - m) <= 4f) / l.Count:0.0}%, within ±4 of 10 {100.0 * l.Count(v => MathF.Abs(v - 10f) <= 4f) / l.Count:0.0}%");
}
var spreads = cellRows.Where(r => !float.IsNaN(r.MaxSpread)).Select(r => r.MaxSpread).ToList();
if (spreads.Count > 0)
    Console.WriteLine($"   within-storey p10–p90 spread over {spreads.Count:N0} cells: median {NavmeshFloor.Quantile(spreads, 0.5f):0.0} u, p90 {NavmeshFloor.Quantile(spreads, 0.9f):0.0} u; cells whose widest storey spreads over 8 u: {100.0 * spreads.Count(s => s > 8f) / spreads.Count:0.0}%, over 16 u: {100.0 * spreads.Count(s => s > 16f) / spreads.Count:0.0}%");

Console.WriteLine($"\n   ---- the widest within-storey spreads (p90 − p10 of δ inside one storey; cells with 50+ floored triangles) ----");
Console.WriteLine($"   {"cell",-30} {"worldspace",-20} {"tris",6} {"floored",7} {"storeys",7} {"median",7} {"p10",6} {"p90",6} {"spread",7}");
foreach (var r in cellRows.Where(r => r.Found >= 50 && !float.IsNaN(r.MaxSpread)).OrderByDescending(r => r.MaxSpread).Take(20))
    Console.WriteLine($"   {r.Cell,-30} {r.World,-20} {r.Tris,6:N0} {r.Found,7:N0} {r.Storeys,7} {r.Median,7:0.0} {r.P10,6:0.0} {r.P90,6:0.0} {r.MaxSpread,7:0.0}");
Console.WriteLine($"\n   ---- the highest and lowest cell medians (50+ floored triangles) ----");
foreach (var r in cellRows.Where(r => r.Found >= 50).OrderByDescending(r => r.Median).Take(8).Concat(cellRows.Where(r => r.Found >= 50).OrderBy(r => r.Median).Take(8)))
    Console.WriteLine($"   {r.Cell,-30} {r.World,-20} {r.Tris,6:N0} {r.Found,7:N0} {r.Storeys,7} {r.Median,7:0.0} {r.P10,6:0.0} {r.P90,6:0.0}");
Console.WriteLine($"\n   ---- the cells with the most triangles lacking a floor (share; 50+ triangles) ----");
foreach (var r in cellRows.Where(r => r.Tris >= 50).OrderByDescending(r => (double)(r.Tris - r.Found) / r.Tris).Take(12))
    Console.WriteLine($"   {r.Cell,-30} {r.World,-20} {r.Tris,6:N0} no floor {r.Tris - r.Found,6:N0} ({100.0 * (r.Tris - r.Found) / r.Tris,5:0.0}%): no surface {r.No[1]}, deeper {r.No[2]}, higher {r.No[3]}, down-facing {r.No[4]}; water-flagged {r.WaterNoFloor}");

if (verdicts.Count > 0)
{
    Console.WriteLine($"\n   ---- δ under the objects the route-cut verdicts stand on (median of the floored samples in the object's footprint + 22 u, the object itself not a floor candidate) ----");
    Console.WriteLine($"   {"verdict",-8} {"objects",8} {"no sample",9} {"p10",6} {"median",7} {"p90",6}  {"|δ−10| > 8",10} {"|δ−10| > 4",10}");
    foreach (var v in new[] { "BLOCKED", "LIKELY" })
    {
        var rows = verdictRows.Where(r => r.Verdict == v).ToList();
        int listed = verdicts.Count(kv => kv.Value.Verdict == v);
        if (rows.Count == 0) { Console.WriteLine($"   {v,-8} {0,8} ({listed:N0} listed, none in the swept cells)"); continue; }
        var l = rows.Select(r => r.Local).ToList();
        Console.WriteLine($"   {v,-8} {rows.Count,8:N0} {listed - rows.Count,9:N0} {NavmeshFloor.Quantile(l, 0.1f),6:0.0} {NavmeshFloor.Quantile(l, 0.5f),7:0.0} {NavmeshFloor.Quantile(l, 0.9f),6:0.0}  {100.0 * l.Count(x => MathF.Abs(x - 10f) > 8f) / l.Count,9:0.0}% {100.0 * l.Count(x => MathF.Abs(x - 10f) > 4f) / l.Count,9:0.0}%");
    }
    var vb = Bins(verdictRows.Where(r => r.Verdict == "BLOCKED").Select(r => r.Local).ToList());
    var vl = Bins(verdictRows.Where(r => r.Verdict == "LIKELY").Select(r => r.Local).ToList());
    Console.WriteLine($"   {"bin",-12} {"BLOCKED",9} {"LIKELY",9}");
    for (int k = 0; k < 47; k++)
    {
        if (vb[k] == 0 && vl[k] == 0) continue;
        string label = k == 0 ? "< -24" : k == 46 ? ">= 66" : $"{-24 + (k - 1) * 2,4} .. {-24 + k * 2,3}";
        Console.WriteLine($"   {label,-12} {vb[k],9:N0} {vl[k],9:N0}");
    }
    if (tsvPath is not null)
    {
        var vp = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(tsvPath)) ?? ".", "verdict-offsets.tsv");
        using var vw = new StreamWriter(vp);
        vw.WriteLine("key\tcell\tverdict\tmesh\tsamples\tlocalDelta\tcellMedian");
        foreach (var r in verdictRows) vw.WriteLine($"{r.Key}\t{r.Cell}\t{r.Verdict}\t{r.Mesh}\t{r.N}\t{Fmt(r.Local)}\t{Fmt(r.CellMedian)}");
        Console.WriteLine($"   per-object rows -> {vp}");
    }
}
if (tsvPath is not null) Console.WriteLine($"\nper-cell rows -> {tsvPath}");
if (trisPath is not null) Console.WriteLine($"per-triangle rows -> {trisPath}");
// Last, so the counters describe the whole run rather than nothing.
Console.WriteLine("\n" + pop.Describe());
return 0;

record Geo(Aabb Box, TriangleSoup? Soup, string Source);
record CellRow(string Cell, string World, bool Interior, (int X, int Y)? Grid, int Tris, int Found, int Mesh, int Land, int[] No, int WaterNoFloor, int Embedded,
               float Median, float P10, float P90, int Storeys, float MaxSpread);
