// HousecarlCore now has its own PluginUnreadableException (upstream 2.0); this probe means the harness one.
using PluginUnreadableException = HarnessShared.PluginUnreadableException;
using System.Numerics;
using System.Text;
using System.Text.Json;
using HarnessShared;
using HousecarlCore;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Skyrim;
using HousecarlCore.Progress;

// corridorprobe — choose and measure navmesh CORRIDORS from the records, for roadmap 4.7 Part C; and read the in-game
// arms back through the route-cut rule (4.7's closing step).
//
// The in-game matrix needs navmesh strips of known widths. This reads a cell's winning navmesh straight out of the
// plugin and (a) draws it as a coverage map so a corridor can be picked by eye, (b) measures the walkable width across
// a chosen point and heading, plus the clearance ladder the route rule would report for the corridor on its own, and
// (c) reads a list of in-game arms — a base record stood at the engine's own pose, the outcome the walker produced,
// the line it was commanded along — through the route-cut rule and prints the confusion matrix: the acceptance set.
//
// usage: corridorprobe map <CellEditorID> [--step 32] [--plugin Skyrim.esm]
//        corridorprobe width <CellEditorID> --at X,Y,Z --heading DEG [--span 600] [--plugin Skyrim.esm]
//        corridorprobe arms <arms.json> [--band controller|nominal] [--plugin Skyrim.esm] [--bos on|off]
//        corridorprobe cut <CellEditorID> --ref XXXXXX:Plugin.esm [--ref …] [--band controller|nominal] [--band-top U] [--plugin Skyrim.esm]
//        corridorprobe find <mesh substring>
//   map    an ASCII coverage map of the cell's navmesh at --step units per character (rows = Y descending)
//   cut    one placed reference through the route-cut rule, SHOWN rather than tallied: the verdict and its reason, the
//          rule's own path line (its points in plan, where along it the object cuts, the rise and the float there), the
//          navmesh and the physical floor every 32 u along that line, the reader's world footprint (the collision soup as
//          NifGeometry reads the mesh, under the record's pose), the Creation Kit's baked ObjectBounds under the SAME
//          pose, and the storey raster with both footprints drawn — so an in-game arm can be planned on the line the
//          rule tested and the two footprints compared against where the walker actually stops (Q28, 2026-09-05: the
//          reader applied a NIF's root-node transform and the CK's bounds do not carry it). The record's own base and
//          pose, no swap layer; --band-top gives the nominal head height (default ActorBand.ScaleOneHeight).
//   width  the walkable width across the corridor at the point, perpendicular to the heading (yaw, degrees, 0 = +Y,
//          clockwise), sampled every 4 u; and the width every 64 u along the heading for --span each way
//   arms   arms.json: { "cell": "<CellEditorID>", "bandTop": <the walker's nominal head height, u>,
//                       "arms": [ { "tag", "base": "XXXXXX:Plugin.esp", "pos": [x,y,z], "yaw": <deg>, "scale": <s>,
//                                   "outcome": "CROSSED|DETOURED|STALLED", "line": [[x0,y0],[x1,y1]] }, … ] }
//          Each arm is measured twice: on the rule's own line (the raster's ends, pathfound) and on the commanded line
//          the walker was given, so a disagreement between the two is separable from a wrong threshold. Meshes come
//          through the instance's VFS (HarnessTarget instance/profile/game); the navmesh from --plugin, as the other modes.
//   --band controller (default) scales the band top by ActorBand.ControllerFactor and adds the station's offset — the
//          controller's top above the FEET; nominal uses bandTop as given (a head height is above the feet by definition).
//          Since Q24 (2026-09-05) the band stands on the PHYSICAL floor under each raster cell (_shared/FloorFeed.cs): the
//          cell's refs (the 3×3 neighbourhood by position for an exterior) and its LAND from --plugin, the same actor-solid
//          soup the obstacles use; where no floor is within reach the navmesh − 10.4 u stands in, counted.
//   POPULATION (Q27, 2026-09-06): the floor and the cell's own references are read in the LOAD-TIME state — a reference
//          is there when EnableState resolves its XESP parent chain enabled over --plugin's own placements, not when its
//          own initially-disabled flag happens to be clear. The SOURCE is unchanged (still one plugin), so a vanilla
//          calibration stays a vanilla calibration. `arms` places its own experimental objects at the poses the arms file
//          records — those are commanded placements and no population applies to them.
//   --flags the flag-only population, what every run before 2026-09-06 measured. --state is NOT taken: a named world
//          state needs the switch census over the whole order.
//   --bos  arms only. on (default, since 2026-09-04): each arm's base is resolved through the Base Object Swapper layer
//          (*_SWAP.ini, from the profile's VFS; _shared/BosHook.cs) for the cell it stood in — the obstacle the ENGINE
//          loaded for that base record — and the row says so. The pose stays as the arms file gives it (the engine's own,
//          read back after any override applied), so a transform line is named, never re-applied. off: the record base.

using var __run = RunLog.BeginProcess("corridorprobe", args);
// Known answers for which version of a moved or deleted reference this probe's walk keeps (_shared/MovedRefFixture.cs).
if (args.Length >= 1 && args[0] == "--moved-ref-selftest") return BosOpenSelfTest.Run("corridorprobe");

string GamePath = HarnessTarget.Require(HarnessTarget.Game, "game directory", "game");
string dataDir = Path.Combine(GamePath, "Data");

if (args.Length < 2) { Console.Error.WriteLine("usage: corridorprobe map|width|cut <CellEditorID> [...]  |  corridorprobe arms <arms.json>  |  corridorprobe find <mesh substring>"); return 1; }
string mode = args[0], cellId = args[1];

if (mode == "find")
{
    // Base records whose model path contains the substring — the FormIDs the in-game arms place with PlaceAtMe.
    using var fm = SkyrimMod.CreateFromBinaryOverlay(Path.Combine(dataDir, "Skyrim.esm"), SkyrimRelease.SkyrimSE);
    foreach (var rec in fm.EnumerateMajorRecords())
        if (rec is IModeledGetter mg && mg.Model?.File is { } f && f.GivenPath.Contains(cellId, StringComparison.OrdinalIgnoreCase))
            Console.WriteLine($"   {rec.FormKey}  {rec.GetType().Name.Replace("BinaryOverlay", ""),-16} {rec.EditorID,-32} {f.GivenPath}");
    return 0;
}
float step = 32f, span = 600f, heading = 0f; Vec3? at = null; string plugin = "Skyrim.esm"; bool controllerBand = true; string? ascii = null;
bool bosOn = true;
bool flagsOnly = false; string? stateSpec = null;
var refKeys = new List<FormKey>(); float? bandTopArg = null;
for (int i = 2; i < args.Length; i++)
{
    if (args[i] == "--flags") { flagsOnly = true; continue; }
    if (args[i] == "--state" && i + 1 < args.Length) { stateSpec = args[++i]; continue; }
    if (args[i] == "--step" && i + 1 < args.Length) step = float.Parse(args[++i]);
    else if (args[i] == "--ref" && i + 1 < args.Length) refKeys.Add(FormKey.Factory(args[++i]));
    else if (args[i] == "--band-top" && i + 1 < args.Length) bandTopArg = float.Parse(args[++i]);
    else if (args[i] == "--bos" && i + 1 < args.Length) bosOn = BosHook.ParseFlag(args[++i]) ?? throw new ArgumentException("--bos takes on|off");
    else if (args[i] == "--ascii" && i + 1 < args.Length) ascii = args[++i];   // arms: draw the raster of every arm whose tag contains this
    else if (args[i] == "--span" && i + 1 < args.Length) span = float.Parse(args[++i]);
    else if (args[i] == "--heading" && i + 1 < args.Length) heading = float.Parse(args[++i]);
    else if (args[i] == "--plugin" && i + 1 < args.Length) plugin = args[++i];
    else if (args[i] == "--band" && i + 1 < args.Length) controllerBand = args[++i] != "nominal";
    else if (args[i] == "--at" && i + 1 < args.Length) { var p = args[++i].Split(','); at = new Vec3(float.Parse(p[0]), float.Parse(p[1]), float.Parse(p[2])); }
}

JsonElement armsDoc = default;
if (mode == "arms")
{
    armsDoc = JsonDocument.Parse(File.ReadAllText(cellId)).RootElement;
    cellId = armsDoc.GetProperty("cell").GetString() ?? throw new InvalidDataException("arms.json needs \"cell\"");
}

PopulationMode popMode;
try { popMode = PopulationCli.Resolve("corridorprobe", flagsOnly, stateSpec, stateSupported: false); }
catch (ArgumentException ex) { Console.Error.WriteLine(ex.Message); return 2; }

using var mod = SkyrimMod.CreateFromBinaryOverlay(Path.Combine(dataDir, plugin), SkyrimRelease.SkyrimSE);
CellPlacements? cell = null;
foreach (var cp in PlacementExtract.FromMod(mod))
    if (string.Equals(cp.CellEditorId, cellId, StringComparison.OrdinalIgnoreCase)) { cell = cp; break; }
if (cell is null) { Console.Error.WriteLine($"cell '{cellId}' not found in {plugin}"); return 1; }
var navs = NavmeshIndex.Build(cell.Navmeshes, out int malformed);
Console.WriteLine($"{cellId}: {cell.Navmeshes.Count} navmesh(es), {navs.TriangleCount:N0} triangles ({malformed} malformed), {cell.Refs.Count:N0} refs, {(cell.Interior ? "interior" : "exterior")}");

var box = Aabb.Empty;
foreach (var nv in cell.Navmeshes) foreach (var v in nv.Vertices) box = box.Add(new Vector3(v.X, v.Y, v.Z));
Console.WriteLine($"navmesh extent: {box}");

// Ground under (x,y): every non-deleted triangle covering the point; returns the heights found.
List<float> GroundAt(float x, float y)
{
    var zs = new List<float>();
    var probe = new Aabb(new Vec3(x - 0.5f, y - 0.5f, box.Min.Z - 512), new Vec3(x + 0.5f, y + 0.5f, box.Max.Z + 512), false);
    foreach (var hit in navs.Query(probe))
    {
        if ((hit.Flags & 0x08) != 0) continue;
        var nav = navs.NavAt(hit.NavIndex); var tri = nav.Triangles[hit.TriangleIndex];
        if (RouteConnectivity.HeightAt(nav.Vertices[tri.A], nav.Vertices[tri.B], nav.Vertices[tri.C], x, y) is { } z) zs.Add(z);
    }
    return zs;
}

if (mode == "map")
{
    int nx = (int)MathF.Ceiling((box.Max.X - box.Min.X) / step) + 1, ny = (int)MathF.Ceiling((box.Max.Y - box.Min.Y) / step) + 1;
    Console.WriteLine($"map: {nx} x {ny} cells of {step} u; '#' = one storey, 'N' = several storeys, '.' = none; columns every 8 = {8 * step} u");
    Console.WriteLine($"     x from {box.Min.X:0} (left) to {box.Max.X:0}; y from {box.Max.Y:0} (top) to {box.Min.Y:0}");
    var header = new StringBuilder("        ");
    for (int i = 0; i < nx; i++) header.Append(i % 8 == 0 ? '|' : ' ');
    Console.WriteLine(header);
    for (int j = ny - 1; j >= 0; j--)
    {
        float y = box.Min.Y + j * step;
        var sb = new StringBuilder($"{y,7:0} ");
        for (int i = 0; i < nx; i++)
        {
            float x = box.Min.X + i * step;
            var zs = GroundAt(x, y);
            sb.Append(zs.Count == 0 ? '.' : zs.Max() - zs.Min() > 128f ? 'N' : '#');
        }
        Console.WriteLine(sb);
    }
    return 0;
}

if (mode == "width")
{
    if (at is null) { Console.Error.WriteLine("width needs --at X,Y,Z (Z may be -99999 for the lowest storey under X,Y)"); return 1; }
    if (at.Value.Z <= -99999f)
    {
        var zs = GroundAt(at.Value.X, at.Value.Y);
        if (zs.Count == 0) { Console.Error.WriteLine("no navmesh under that point"); return 1; }
        at = new Vec3(at.Value.X, at.Value.Y, zs.Min());
        Console.WriteLine($"storeys under the point: {string.Join(", ", zs.OrderBy(z => z).Select(z => z.ToString("0")))} -> using {at.Value.Z:0}");
    }
    float rad = heading * MathF.PI / 180f;
    var dir = new Vector2(MathF.Sin(rad), MathF.Cos(rad));          // yaw 0 = +Y, clockwise
    var perp = new Vector2(dir.Y, -dir.X);                           // to the right of travel
    float z0 = at.Value.Z;

    (float left, float right, float w) WidthAt(float cx, float cy)
    {
        float Ground(float x, float y)
        {
            var zs = GroundAt(x, y).Where(z => MathF.Abs(z - z0) <= 160f).ToList();
            return zs.Count == 0 ? float.NaN : zs.OrderBy(z => MathF.Abs(z - z0)).First();
        }
        if (float.IsNaN(Ground(cx, cy))) return (0, 0, 0);
        float l = 0, r = 0;
        for (float s = 4; s <= 2000; s += 4) { if (float.IsNaN(Ground(cx - perp.X * s, cy - perp.Y * s))) break; l = s; }
        for (float s = 4; s <= 2000; s += 4) { if (float.IsNaN(Ground(cx + perp.X * s, cy + perp.Y * s))) break; r = s; }
        return (l, r, l + r);
    }

    var w0 = WidthAt(at.Value.X, at.Value.Y);
    Console.WriteLine($"at ({at.Value.X:0}, {at.Value.Y:0}, {z0:0}) heading {heading:0}: walkable width across = {w0.w:0} u  (left {w0.left:0}, right {w0.right:0}; 4 u resolution)");
    Console.WriteLine($"   along the heading, every 64 u for {span:0} u each way:");
    for (float s = -span; s <= span; s += 64)
    {
        float cx = at.Value.X + dir.X * s, cy = at.Value.Y + dir.Y * s;
        var w = WidthAt(cx, cy);
        var gz = GroundAt(cx, cy).Where(z => MathF.Abs(z - z0) <= 160f).OrderBy(z => MathF.Abs(z - z0)).ToList();
        Console.WriteLine($"   {s,6:+0;-0} u  ({cx,7:0}, {cy,7:0})  z {(gz.Count > 0 ? gz[0].ToString("0") : "  -"),5}  width {w.w,5:0}  left {w.left,4:0} right {w.right,4:0}");
    }

    // The corridor's own clearance ladder as the route rule sees it: a phantom 1 u obstacle at the point.
    var phantom = MeshColumnIndex.Build(Array.Empty<Vector3>(), Array.Empty<int>());
    var pbox = new Aabb(new Vec3(at.Value.X - 0.5f, at.Value.Y - 0.5f, z0), new Vec3(at.Value.X + 0.5f, at.Value.Y + 0.5f, z0 + 100), false);
    var ladder = new[] { 0f, 32f, 64f, 96f, 128f, 160f, 192f, 256f };
    var facts = RouteConnectivity.Measure(navs, phantom, pbox, 256f, 16f, 32f, 128f, ladder);
    Console.WriteLine($"   route rule, corridor alone (window ±256 u, step 16): judged={facts.Judged} storeys={facts.Storeys} intact-without rung {facts.MaxIntactRungWithout} = {(facts.MaxIntactRungWithout < 0 ? 0 : ladder[facts.MaxIntactRungWithout]):0} u  {facts.Why}");
    return 0;
}

if (mode == "arms" || mode == "cut")
{
    // ---- the VFS, for the obstacles' meshes ----
    string Instance = HarnessTarget.Require(HarnessTarget.Instance, "MO2 instance directory", "instance");
    string profileDir = Path.Combine(Instance, "profiles", HarnessTarget.Require(HarnessTarget.Profile, "MO2 profile name", "profile"));
    string modsDir = Path.Combine(Instance, "mods"), overwriteDir = Path.Combine(Instance, "overwrite");
    var comp = Mo2LoadOrder.ReadComposition(profileDir);
    var arch = ArchiveDiscovery.Discover(profileDir, modsDir, dataDir, overwriteDir, GamePath);
    using var assets = AssetResolver.Build(overwriteDir, modsDir, dataDir, comp.EnabledMods, arch.Archives);
    var order = Mo2LoadOrder.Build(profileDir, modsDir, dataDir, overwriteDir);

    // ---- the arms, and the base records they place (cut: none — the record's own base and pose, below) ----
    var arms = new List<Arm>();
    if (mode == "arms") foreach (var a in armsDoc.GetProperty("arms").EnumerateArray())
    {
        var pos = a.GetProperty("pos").EnumerateArray().Select(e => e.GetSingle()).ToArray();
        var line = a.TryGetProperty("line", out var ln) && ln.ValueKind == JsonValueKind.Array
            ? ln.EnumerateArray().Select(p => { var q = p.EnumerateArray().Select(e => e.GetSingle()).ToArray(); return new Vector2(q[0], q[1]); }).ToArray()
            : null;
        arms.Add(new Arm(a.GetProperty("tag").GetString() ?? "?", FormKey.Factory(a.GetProperty("base").GetString()!),
                         new Vec3(pos[0], pos[1], pos[2]), a.GetProperty("yaw").GetSingle(), a.TryGetProperty("scale", out var sc) ? sc.GetSingle() : 1f,
                         a.GetProperty("outcome").GetString() ?? "?", line));
    }
    var baseMesh = new Dictionary<FormKey, (string Mesh, string Type)>();
    // --bos on (default): the Base Object Swapper layer's index wants the whole order (the runtime-id table, every EditorID,
    // the cells' locations and keywords) and a swap target may be defined anywhere in it, so the order is walked once and
    // every modeled record's WINNING model kept; an arm's base is then the base the game loads for that record in this cell
    // (_shared/BosHook.cs, BosFeed). --bos off: the arm's own plugin's record, as every run before 2026-09-04.
    var bos = new BosFeed(bosOn);
    if (mode == "cut") { }   // the placed record's own base: no swap layer, so the row is the reader's reading of the record alone
    else if (bosOn)
    {
        foreach (var p in order.OrderedPaths)
        {
            ISkyrimModGetter om;
            try { om = BosWalkOpen.Open(p)!; }
            catch (PluginUnreadableException ex) { Console.Error.WriteLine(ex.Message); return 2; }
            try
            {
                using (om as IDisposable)
                {
                    bos.Plugin(om);
                    foreach (var rec in om.EnumerateMajorRecords())
                    {
                        bos.Record(rec);
                        if (rec is IModeledGetter mg && mg.Model?.File is { } f && !string.IsNullOrWhiteSpace(f.GivenPath))
                            baseMesh[rec.FormKey] = (f.GivenPath, rec.GetType().Name.Replace("BinaryOverlay", ""));
                    }
                    foreach (var cp in PlacementExtract.FromMod(om)) bos.Cell(cp);
                }
            }
            // A plugin opens lazily: one whose groups fail while this walk reads them is refused, named (_shared/PluginOpen.cs).
            catch (Exception ex) when (ex is not PluginUnreadableException)
            { Console.Error.WriteLine(new PluginUnreadableException(p, "corridorprobe's swap-layer walk", ex, midWalk: true).Message); return 2; }
            catch (PluginUnreadableException ex) { Console.Error.WriteLine(ex.Message); return 2; }
        }
        bos.Load(assets.LooseRoots);
    }
    else
        foreach (var modKey in arms.Select(a => a.Base.ModKey).Distinct())
        {
            string? path = order.OrderedPaths.FirstOrDefault(p => string.Equals(Path.GetFileName(p), modKey.FileName, StringComparison.OrdinalIgnoreCase))
                           ?? (File.Exists(Path.Combine(dataDir, modKey.FileName)) ? Path.Combine(dataDir, modKey.FileName) : null);
            if (path is null) { Console.Error.WriteLine($"{modKey.FileName} is neither in the load order nor in Data"); return 1; }
            var wanted = arms.Where(a => a.Base.ModKey == modKey).Select(a => a.Base).ToHashSet();
            using var bm = SkyrimMod.CreateFromBinaryOverlay(path, SkyrimRelease.SkyrimSE);
            foreach (var rec in bm.EnumerateMajorRecords())
                if (wanted.Contains(rec.FormKey) && rec is IModeledGetter mg && mg.Model?.File is { } f && !string.IsNullOrWhiteSpace(f.GivenPath))
                    baseMesh[rec.FormKey] = (f.GivenPath, rec.GetType().Name.Replace("BinaryOverlay", ""));
        }
    if (mode == "arms") Console.WriteLine("   " + bos.LayerLine());
    int bosSwapped = 0, bosRandom = 0;

    // ---- the geometry: the actor-solid collision soup, as blockprobe reads it ----
    var geoCache = new Dictionary<string, (Aabb Box, TriangleSoup? Soup, string Source)>(StringComparer.OrdinalIgnoreCase);
    (Aabb Box, TriangleSoup? Soup, string Source) Geometry(string rel)
    {
        if (geoCache.TryGetValue(rel, out var hit)) return hit;
        var full = rel.StartsWith("meshes", StringComparison.OrdinalIgnoreCase) ? rel : Path.Combine("meshes", rel);
        var res = assets.ResolveForPlacement(full);
        if (res.Sources.Count == 0) return geoCache[rel] = (Aabb.Empty, null, "unresolved");
        var (bytes, _) = AssetResolver.ReadPlacementSource(res.Sources[0]);
        if (bytes is null) return geoCache[rel] = (Aabb.Empty, null, "unresolved");
        var o = NifGeometry.Measure(bytes, includeTriangles: true);
        if (o.Model is null) return geoCache[rel] = (Aabb.Empty, null, "unreadable");
        var m = o.Model;
        bool anySolid = m.CollisionBodies is not null && m.CollisionBodies.Any(b => b.Solid && CollisionLayers.IsActorSolid(b.Layer));
        if (!m.HasCollision || !anySolid) return geoCache[rel] = (Aabb.Empty, null, "none");
        var solid = m.CollisionSoupFor(b => b.Solid && CollisionLayers.IsActorSolid(b.Layer));
        if (m.CollisionComplete && solid is { TriangleCount: > 0 })
        {
            var bx = Aabb.Empty;
            foreach (var v in solid.Vertices) bx = bx.Add(v);
            return geoCache[rel] = (bx, solid, "collision");
        }
        return geoCache[rel] = (m.Bounds, m.Soup, "fallback");
    }

    // ---- the floor (Q24): the cell's own refs and LAND, or the 3×3 neighbourhood of an exterior, from --plugin ----
    var floorMesh = new Dictionary<FormKey, string>();
    foreach (var rec in mod.EnumerateMajorRecords())
        if (rec is IModeledGetter fmg && fmg.Model?.File is { } ff && !string.IsNullOrWhiteSpace(ff.GivenPath)) floorMesh[rec.FormKey] = ff.GivenPath;
    (Aabb Box, TriangleSoup Soup)? FloorGeometry(PlacedRef r)
    {
        if (r.Base is not { } b || !floorMesh.TryGetValue(b, out var rel)) return null;
        var g = Geometry(rel);
        return g.Soup is not null && !g.Box.IsEmpty ? (g.Box, g.Soup) : null;
    }
    // The population (Q27): the enable facts of --plugin's own placements — the chain crosses cells (a worldspace's
    // persistent cell holds the markers), so they are gathered over every cell before the field is built.
    var enableFacts = new EnableFactsIndex();
    foreach (var rec in mod.EnumerateMajorRecords()) if (rec is IPlacedGetter dpg && dpg.IsDeleted) enableFacts.NoteDeleted(dpg.FormKey);
    // The floor filing (FloorFeed.FloorIndex): the plugin's exterior cells of this cell's worldspace, by position.
    // (FloorFeed.FileOnePlugin: the reach's lists and base boxes, then every cell.)
    var floorIndex = FloorFeed.FileOnePlugin(mod, (ws, _) => ws == cell.Worldspace,
                                             cp => { foreach (var r in cp.Refs) enableFacts.Note(r); }, plugin);
    var frg = floorIndex.RefsOf(cell.Worldspace); var flg = floorIndex.LandOf(cell.Worldspace);
    var pop = popMode == PopulationMode.Flags ? EnablePopulation.Flags(enableFacts) : EnablePopulation.LoadTime(enableFacts);
    FloorField floor;
    // A floor over a LAND that gives no heights (FloorFeed.FloorIndex) is refused: an earlier plugin's heights, or none,
    // would be read in its place.
    if (!cell.Interior && cell.Grid is { } lg && floorIndex.UnreadableNear(cell.Worldspace, lg) is { Count: > 0 } badLand)
    { Console.Error.WriteLine($"{cellId}: REFUSED — its floor needs a LAND that gives no heights ({string.Join("; ", badLand)})"); return 2; }
    if (!cell.Interior && cell.Grid is { } fg)
    {
        var (nbRefs, nbLand) = floorIndex.NeighbourhoodOf(cell.Worldspace, fg);   // the 3x3 by position plus the reach (FloorFeed)
        floor = FloorFeed.Build(nbRefs, FloorGeometry, nbLand, enabled: pop.Predicate);
    }
    else floor = FloorFeed.Build(cell.Refs, FloorGeometry, null, enabled: pop.Predicate);

    // ---- the band and the graph ----
    float nominalTop = mode == "arms" && armsDoc.TryGetProperty("bandTop", out var bt) ? bt.GetSingle() : (bandTopArg ?? ActorBand.ScaleOneHeight);
    float top = controllerBand ? ActorBand.PhysicalControllerTop(nominalTop) : nominalTop;
    var ladder = new[] { 0f, 32f, 64f, 96f, 128f };
    var (gnodes, grpt) = NavGraph.Build(navs);
    Console.WriteLine($"graph: {grpt.Triangles:N0} triangles, {grpt.IntraLinks:N0} intra + {grpt.CrossLinks:N0} cross links, {grpt.DanglingLinks} dangling, {grpt.ExternalLinks} external, {grpt.Components} component(s)");
    Console.WriteLine($"band: {ActorBand.PhysicalStep:0.#}..{top:0.#} u above the FEET ({(controllerBand ? $"controller: nominal {nominalTop:0} × {ActorBand.ControllerFactor} + {ActorBand.StationOffset:0.#}" : "nominal")}; bottom measured 2026-09-05 + the station's {ActorBand.StationOffset:0.#} u offset (Q24), step band to {ActorBand.PhysicalStepBandTop:0.#} u and a float of {ActorBand.FloatWall:0} u or more named in the verdict (Q26)); capsule 22 u; window ±256 u, step 16");
    Console.WriteLine($"floor: physical, {floor.Candidates:N0} candidates, {floor.LandCells} land cell(s) (Q24); {floor.SkippedDisabled:N0} refs not present in the population");
    Console.WriteLine(pop.Describe() + "\n");

    if (mode == "cut")
    {
        if (refKeys.Count == 0) { Console.Error.WriteLine("cut needs --ref XXXXXX:Plugin.esm (repeatable)"); return 1; }
        // The --plugin mod's base records: the mesh, the record type and the Creation Kit's baked ObjectBounds — the engine's
        // own reading of the mesh's extent, which carries no root-node turn (Q28).
        var plugBase = new Dictionary<FormKey, (string Mesh, string Type, (Vec3 Min, Vec3 Max)? Ck)>();
        foreach (var rec in mod.EnumerateMajorRecords())
            if (rec is IModeledGetter pmg && pmg.Model?.File is { } pf && !string.IsNullOrWhiteSpace(pf.GivenPath))
            {
                (Vec3, Vec3)? ck = rec is IObjectBoundedGetter ob && ob.ObjectBounds is { } b
                    ? (new Vec3(b.First.X, b.First.Y, b.First.Z), new Vec3(b.Second.X, b.Second.Y, b.Second.Z)) : null;
                plugBase[rec.FormKey] = (pf.GivenPath, rec.GetType().Name.Replace("BinaryOverlay", ""), ck);
            }
        Console.WriteLine("legend: '.' outside, '#' ground, 'X' ground the READER's object stands in, 'r' perimeter run, 'E' end, '=' the line, '%' the line over the reader's object;");
        Console.WriteLine("        'C' ('c' on a run) a cell whose centre lies inside the CREATION KIT's ObjectBounds under the same pose, where the reader has nothing");
        foreach (var key in refKeys)
        {
            var r = cell.Refs.FirstOrDefault(x => x.FormKey == key);
            if (r is null) { Console.WriteLine($"\n==== {key}: not a placed reference of {cellId} in {plugin}"); continue; }
            if (r.Base is not { } bk || !plugBase.TryGetValue(bk, out var bm)) { Console.WriteLine($"\n==== {key}: base record has no model in {plugin}"); continue; }
            var g = Geometry(bm.Mesh);
            float sc = r.Scale == 0f ? 1f : r.Scale;
            var pos = r.Position; var rot = r.Rotation;
            Console.WriteLine($"\n==== {key}  {bm.Type} {bm.Mesh}  base {bk}{(r.Deleted ? "  DELETED" : "")}");
            Console.WriteLine($"   pose: position ({pos.X:0.##}, {pos.Y:0.##}, {pos.Z:0.##})  rotation {DegOf(rot)} deg  scale {sc:0.###}  soup: {g.Source}");
            if (g.Soup is null || g.Box.IsEmpty) { Console.WriteLine("   no actor-solid geometry: nothing to measure"); continue; }
            var world = SpatialOverlap.ToWorld(g.Box, pos, rot, sc);
            var m = SpatialOverlap.RefrRotation(rot);
            var t = new Vector3(pos.X, pos.Y, pos.Z);
            var w = new Vector3[g.Soup.Vertices.Length];
            for (int i = 0; i < w.Length; i++) w[i] = Vector3.Transform(g.Soup.Vertices[i] * sc, m) + t;
            var columns = MeshColumnIndex.Build(w, g.Soup.Indices, triangleMaterials: g.Soup.TriangleMaterials);
            var fr = RouteConnectivity.Measure(navs, columns, world, 256f, 16f, ActorBand.PhysicalStep, top, ladder, graph: gnodes, keepRaster: true, floorAt: floor.FloorAt, isStairsMaterial: DetectorModel.IsStairsMaterial);
            var vc = DetectorModel.RouteCut(fr.Judged, fr.Touched, fr.Line?.Found ?? false, fr.Line?.Why ?? "", fr.Line?.Cut ?? false, fr.Line?.Passage ?? float.NaN, cutRise: fr.Line?.CutRise ?? float.NaN, cutFloat: fr.Line?.CutFloat ?? float.NaN, cutOnStairs: fr.Line?.CutOnStairs ?? false);
            Console.WriteLine($"   verdict {vc.Verdict}: {vc.Why}");
            Console.WriteLine($"   facts: {Facts(fr)}; judged={fr.Judged} storeys {fr.Storeys} (storey z {fr.StoreyZ:0.#}); band {fr.BandBottom:0.#}..{fr.BandTop:0.#} u above the floor; raster step {fr.Step:0}");
            Console.WriteLine($"   collision materials: {string.Join(", ", (g.Soup.TriangleMaterials ?? new string?[] { null }).Select(x => x ?? "unknown").Distinct().OrderBy(x => x))}; cut on stairs: {fr.Line?.CutOnStairs ?? false}");
            Console.WriteLine($"   READER (NifGeometry, root transform omitted): mesh-space solid box ({g.Box.Min.X:0}, {g.Box.Min.Y:0}, {g.Box.Min.Z:0})..({g.Box.Max.X:0}, {g.Box.Max.Y:0}, {g.Box.Max.Z:0})");
            Console.WriteLine($"      world box x {world.Min.X:0}..{world.Max.X:0}  y {world.Min.Y:0}..{world.Max.Y:0}  z {world.Min.Z:0}..{world.Max.Z:0};  plan corners {Corners(g.Box, m, t, sc)}");
            Aabb? ckWorld = null;
            if (bm.Ck is { } ck)
            {
                var ckBox = new Aabb(ck.Min, ck.Max, false);
                ckWorld = SpatialOverlap.ToWorld(ckBox, pos, rot, sc);
                Console.WriteLine($"   CREATION KIT ObjectBounds: record ({ck.Min.X:0}, {ck.Min.Y:0}, {ck.Min.Z:0})..({ck.Max.X:0}, {ck.Max.Y:0}, {ck.Max.Z:0})");
                Console.WriteLine($"      world box x {ckWorld.Value.Min.X:0}..{ckWorld.Value.Max.X:0}  y {ckWorld.Value.Min.Y:0}..{ckWorld.Value.Max.Y:0}  z {ckWorld.Value.Min.Z:0}..{ckWorld.Value.Max.Z:0};  plan corners {Corners(ckBox, m, t, sc)}");
            }
            else Console.WriteLine("   CREATION KIT ObjectBounds: none on the base record");
            var l = fr.Line;
            if (l is not null && l.Points.Length >= 2)
            {
                Console.WriteLine($"   path line ({l.Status}, {l.Points.Length} points, {l.Length:0} u, capsule {l.CapsuleRadius:0} u): {string.Join(" -> ", l.Points.Select(p => $"({p.X:0}, {p.Y:0})"))}");
                if (l.Cut)
                {
                    var (mx, my) = Along(l.Points, (l.CutFrom + l.CutTo) / 2f);
                    Console.WriteLine($"   cut from {l.CutFrom:0} to {l.CutTo:0} u along the line (midpoint ({mx:0}, {my:0})); rise {l.CutRise:0.#} u, float {l.CutFloat:0.#} u above the floor; passage {(float.IsNaN(l.Passage) ? "-" : l.Passage.ToString("0"))} (L {(float.IsNaN(l.PassageLeft) ? "-" : l.PassageLeft.ToString("0"))} / R {(float.IsNaN(l.PassageRight) ? "-" : l.PassageRight.ToString("0"))})");
                }
                else Console.WriteLine("   the line is not cut");
                Console.WriteLine("   along the line every 32 u:  s  (x, y)  navmesh z on the storey  physical floor z  [reader solid at the sample: cell state]");
                for (float s = 0; s <= l.Length + 0.5f; s += 32f)
                {
                    var (x, y) = Along(l.Points, MathF.Min(s, l.Length));
                    var zs = GroundAt(x, y).Where(z => MathF.Abs(z - fr.StoreyZ) <= 160f).OrderBy(z => MathF.Abs(z - fr.StoreyZ)).ToList();
                    string nz = zs.Count > 0 ? zs[0].ToString("0.#") : "-";
                    string fz = zs.Count > 0 ? floor.FloorAt(x, y, zs[0]).ToString("0.#") : "-";
                    string st = "-";
                    if (fr.Raster is { } rr)
                    {
                        int ci = (int)MathF.Floor((x - rr.MinX) / rr.Step), cj = (int)MathF.Floor((y - rr.MinY) / rr.Step);
                        if (ci >= 0 && cj >= 0 && ci < rr.Nx && cj < rr.Ny) st = rr.State[cj * rr.Nx + ci] switch { 0 => "outside", 1 => "ground", _ => "OBJECT" };
                    }
                    Console.WriteLine($"      {s,5:0}  ({x,8:0}, {y,8:0})  nav {nz,8}  floor {fz,8}  {st}");
                }
            }
            else Console.WriteLine($"   no path line: {l?.Status} {l?.Why}");
            if (fr.Raster is { } ras)
            {
                Console.WriteLine($"   raster ({ras.Nx} x {ras.Ny} cells of {ras.Step:0} u):");
                Console.Write(DrawCut(ras, bm.Ck, m, t, sc, fr.StoreyZ));
            }
        }
        return 0;

        static string DegOf(Vec3 r) => $"({r.X * 180f / MathF.PI:0.#}, {r.Y * 180f / MathF.PI:0.#}, {r.Z * 180f / MathF.PI:0.#})";
        static string Corners(Aabb b, Matrix4x4 m, Vector3 t, float sc)
        {
            var pts = new[] { new Vector3(b.Min.X, b.Min.Y, b.Min.Z), new Vector3(b.Max.X, b.Min.Y, b.Min.Z), new Vector3(b.Max.X, b.Max.Y, b.Min.Z), new Vector3(b.Min.X, b.Max.Y, b.Min.Z) };
            return string.Join(" ", pts.Select(p => { var q = Vector3.Transform(p * sc, m) + t; return $"({q.X:0}, {q.Y:0})"; }));
        }
        static (float X, float Y) Along(Vector2[] pts, float s)
        {
            float acc = 0;
            for (int i = 0; i + 1 < pts.Length; i++)
            {
                float len = (pts[i + 1] - pts[i]).Length();
                if (len < 1e-3f) continue;
                if (acc + len >= s) { var p = pts[i] + (pts[i + 1] - pts[i]) * ((s - acc) / len); return (p.X, p.Y); }
                acc += len;
            }
            return (pts[^1].X, pts[^1].Y);
        }
        // RouteRaster.Ascii() with the Creation Kit's footprint overlaid: a cell centre taken back into mesh space through the
        // inverse pose (the transpose of the rotation, scale divided out) and tested against the record's bounds in plan.
        static string DrawCut(RouteRaster ras, (Vec3 Min, Vec3 Max)? ck, Matrix4x4 m, Vector3 t, float sc, float storeyZ)
        {
            var g = new char[ras.Nx * ras.Ny];
            for (int k = 0; k < g.Length; k++) g[k] = ras.State[k] == 0 ? '.' : ras.State[k] == 1 ? '#' : 'X';
            foreach (var run in ras.Runs) foreach (var k in run) g[k] = 'r';
            if (ck is { } c)
            {
                var inv = Matrix4x4.Transpose(m);
                // The bounds grown by half a cell (in mesh units), so a slab thinner than the raster step — a 9 u wall
                // whose edge falls exactly on a row centre — is drawn in at least one row instead of between two.
                float grow = 0.5f * ras.Step / sc;
                for (int j = 0; j < ras.Ny; j++)
                    for (int i = 0; i < ras.Nx; i++)
                    {
                        var p = new Vector3(ras.MinX + (i + 0.5f) * ras.Step, ras.MinY + (j + 0.5f) * ras.Step, storeyZ);
                        var q = Vector3.Transform(p - t, inv) / sc;
                        if (q.X < c.Min.X - grow || q.X > c.Max.X + grow || q.Y < c.Min.Y - grow || q.Y > c.Max.Y + grow) continue;
                        int k = j * ras.Nx + i;
                        if (g[k] == '.' || g[k] == '#') g[k] = 'C'; else if (g[k] == 'r') g[k] = 'c';
                    }
            }
            for (int s = 0; s + 1 < ras.Line.Length; s++)
            {
                var p = ras.Line[s]; var q = ras.Line[s + 1]; var d = q - p; float len = d.Length();
                if (len < 1e-3f) continue;
                for (float u = 0; u <= len; u += ras.Step * 0.5f)
                {
                    var pt = p + d * (u / len);
                    int i = (int)MathF.Floor((pt.X - ras.MinX) / ras.Step), j = (int)MathF.Floor((pt.Y - ras.MinY) / ras.Step);
                    if (i < 0 || j < 0 || i >= ras.Nx || j >= ras.Ny) continue;
                    int k = j * ras.Nx + i;
                    g[k] = ras.State[k] == 2 ? '%' : '=';
                }
            }
            foreach (var e in ras.Ends)
            {
                int i = (int)MathF.Floor((e.X - ras.MinX) / ras.Step), j = (int)MathF.Floor((e.Y - ras.MinY) / ras.Step);
                if (i >= 0 && j >= 0 && i < ras.Nx && j < ras.Ny) g[j * ras.Nx + i] = 'E';
            }
            var sb = new StringBuilder();
            for (int j = ras.Ny - 1; j >= 0; j--)
            {
                sb.Append($"{ras.MinY + (j + 0.5f) * ras.Step,8:0} ");
                for (int i = 0; i < ras.Nx; i++) sb.Append(g[j * ras.Nx + i]);
                sb.Append('\n');
            }
            sb.Append($"         x from {ras.MinX + 0.5f * ras.Step:0} to {ras.MinX + (ras.Nx - 0.5f) * ras.Step:0}, {ras.Step:0} u per cell\n");
            return sb.ToString();
        }
    }

    var rows = new List<(Arm A, string Type, RouteCutVerdict? Rule, RouteFacts? FR, RouteCutVerdict? Cmd, RouteFacts? FC, string Note)>();
    foreach (var a in arms)
    {
        // The arm's base under the layer: the obstacle the engine loaded for this base record in this cell. The pose is the
        // one the arms file gives — the engine's own, read back after any override had applied — so a transform line is
        // NAMED and not applied again (the double step bosprobe's listing once had). A spawned reference has no persistent
        // FormKey, so no [References] line can name it: the id passed is Null and only [Forms] lines can match.
        var armBase = a.Base; string bosNote = "";
        if (bos.Layer is not null)
        {
            var res = BaseObjectSwaps.Resolve(bos.Layer, bos.Index!, FormKey.Null, "PlacedObject", a.Base, cell.Cell, null);
            if (res.Swapped || res.Random || res.Properties is { IsValid: true })
            {
                bosNote = "  {" + BosHook.Describe(res) + (res.Properties is { HasTransform: true } ? "; pose as the engine reported it, not re-applied" : "") + "}";
                if (res.Swapped) { armBase = res.EffectiveBase; bosSwapped++; }
                if (res.Random) bosRandom++;
            }
        }
        if (!baseMesh.TryGetValue(armBase, out var bm)) { rows.Add((a, "?", null, null, null, null, "base record has no model" + bosNote)); continue; }
        if (bm.Type == "MoveableStatic") { rows.Add((a, bm.Type, null, null, null, null, "MoveableStatic: set aside (pushed aside, Part C)" + bosNote)); continue; }
        if (bm.Type == "Door") { rows.Add((a, bm.Type, null, null, null, null, "Door: set aside (passable by design)" + bosNote)); continue; }
        var g = Geometry(bm.Mesh);
        if (g.Soup is null || g.Box.IsEmpty) { rows.Add((a, bm.Type, null, null, null, null, $"no actor-solid geometry ({g.Source})" + bosNote)); continue; }
        var rot = new Vec3(0, 0, a.YawDeg * MathF.PI / 180f);
        var world = SpatialOverlap.ToWorld(g.Box, a.Pos, rot, a.Scale);
        var m = SpatialOverlap.RefrRotation(rot);
        var t = new Vector3(a.Pos.X, a.Pos.Y, a.Pos.Z);
        var w = new Vector3[g.Soup.Vertices.Length];
        for (int i = 0; i < w.Length; i++) w[i] = Vector3.Transform(g.Soup.Vertices[i] * a.Scale, m) + t;
        var columns = MeshColumnIndex.Build(w, g.Soup.Indices, triangleMaterials: g.Soup.TriangleMaterials);

        bool draw = ascii is not null && a.Tag.Contains(ascii, StringComparison.OrdinalIgnoreCase);
        var fr = RouteConnectivity.Measure(navs, columns, world, 256f, 16f, ActorBand.PhysicalStep, top, ladder, graph: gnodes, keepRaster: draw, floorAt: floor.FloorAt, isStairsMaterial: DetectorModel.IsStairsMaterial);
        var vr = Verdict(fr);
        if (draw && fr.Raster is { } ras)
        {
            Console.WriteLine($"\n   ---- raster: {a.Tag} — {vr}; {fr.Line?.Status} {fr.Line?.Why} ----");
            Console.Write(ras.Ascii());
        }
        RouteFacts? fc = null; RouteCutVerdict? vc = null;
        if (a.Line is { Length: >= 2 })
        {
            fc = RouteConnectivity.Measure(navs, columns, world, 256f, 16f, ActorBand.PhysicalStep, top, ladder, graph: gnodes, lineOverride: a.Line, floorAt: floor.FloorAt, isStairsMaterial: DetectorModel.IsStairsMaterial);
            vc = Verdict(fc);
        }
        rows.Add((a, bm.Type, vr, fr, vc, fc, g.Source + bosNote));
    }

    static RouteCutVerdict Verdict(RouteFacts f) =>
        DetectorModel.RouteCut(f.Judged, f.Touched, f.Line?.Found ?? false, f.Line?.Why ?? "", f.Line?.Cut ?? false, f.Line?.Passage ?? float.NaN, cutRise: f.Line?.CutRise ?? float.NaN, cutFloat: f.Line?.CutFloat ?? float.NaN, cutOnStairs: f.Line?.CutOnStairs ?? false).Verdict;
    static string Facts(RouteFacts? f) => f is null ? "-" : f.Line is { } l
        ? $"touched {f.Touched,3} {l.Status,-10} cut={(l.Cut ? "Y" : "n")} passage {(float.IsNaN(l.Passage) ? "  -" : l.Passage >= l.PassageCap ? $"≥{l.PassageCap:0}" : $"{l.Passage,3:0}")} (L {(float.IsNaN(l.PassageLeft) ? "-" : l.PassageLeft.ToString("0")),3} / R {(float.IsNaN(l.PassageRight) ? "-" : l.PassageRight.ToString("0")),3})" + (l.Status == LineStatus.JunctionLine ? $" [{l.Ends} ends, {l.PairsCut}/{l.Pairs} pairs cut]" : l.Found ? "" : $" [{l.Ends} ends: {l.Why}]")
        : $"judged={f.Judged} {f.Why}";

    Console.WriteLine($"   {"arm",-34} {"outcome",-9} {"RULE LINE",-14} {"facts",-58} {"CMD LINE",-14} facts");
    foreach (var r in rows)
        Console.WriteLine($"   {r.A.Tag,-34} {r.A.Outcome,-9} {(r.Rule?.ToString() ?? "aside"),-14} {Facts(r.FR),-58} {(r.Cmd?.ToString() ?? "-"),-14} {Facts(r.FC)}   {r.Note}");

    var outcomes = new[] { "CROSSED", "DETOURED", "STALLED" };
    void Matrix(string title, Func<(Arm A, string Type, RouteCutVerdict? Rule, RouteFacts? FR, RouteCutVerdict? Cmd, RouteFacts? FC, string Note), RouteCutVerdict?> pick)
    {
        Console.WriteLine($"\n   ---- confusion matrix: {title} ----");
        Console.WriteLine($"   {"verdict",-14} {"CROSSED",8} {"DETOURED",9} {"STALLED",8}   {"stall rate",10}");
        foreach (var v in new[] { RouteCutVerdict.Blocked, RouteCutVerdict.LikelyBlocked, RouteCutVerdict.Clipped, RouteCutVerdict.None, RouteCutVerdict.NoLine, RouteCutVerdict.Unjudgeable })
        {
            var n = outcomes.Select(o => rows.Count(r => pick(r) == v && r.A.Outcome == o)).ToArray();
            int tot = n.Sum();
            if (tot == 0) continue;
            Console.WriteLine($"   {v,-14} {n[0],8} {n[1],9} {n[2],8}   {(100.0 * n[2] / tot),9:0}%");
        }
        int aside = rows.Count(r => pick(r) is null);
        if (aside > 0) Console.WriteLine($"   {"(set aside)",-14} {outcomes.Select(o => rows.Count(r => pick(r) is null && r.A.Outcome == o)).Select(n => n.ToString().PadLeft(8)).Aggregate((x, y) => x + " " + y)}");
    }
    Matrix("the rule's own line (raster ends, pathfound)", r => r.Rule);
    if (rows.Any(r => r.Cmd is not null)) Matrix("the commanded line (what the walker was given)", r => r.Cmd);
    int disagree = rows.Count(r => r.Rule is not null && r.Cmd is not null && r.Rule != r.Cmd);
    Console.WriteLine($"\n   arms where the two lines disagree on the verdict: {disagree}");
    { var ft = new FloorFeed.Tally(); ft.Add(floor); Console.WriteLine("   " + ft.Line()); }
    if (bosOn) Console.WriteLine($"   BOS: {bosSwapped} arm(s) resolve to a swapped base ({bosRandom} random — the first alternative taken); poses as the arms file gives them");
    return 0;
}

Console.Error.WriteLine("mode must be map, width, arms or find");
return 1;

record Arm(string Tag, FormKey Base, Vec3 Pos, float YawDeg, float Scale, string Outcome, Vector2[]? Line);
