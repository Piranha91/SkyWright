using System.Numerics;
using System.Text;
using System.Text.Json;
using HarnessShared;
using HousecarlCore;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Skyrim;

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
//        corridorprobe find <mesh substring>
//   map    an ASCII coverage map of the cell's navmesh at --step units per character (rows = Y descending)
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
//   --bos  arms only. on (default, since 2026-09-04): each arm's base is resolved through the Base Object Swapper layer
//          (*_SWAP.ini, from the profile's VFS; _shared/BosHook.cs) for the cell it stood in — the obstacle the ENGINE
//          loaded for that base record — and the row says so. The pose stays as the arms file gives it (the engine's own,
//          read back after any override applied), so a transform line is named, never re-applied. off: the record base.

string GamePath = HarnessTarget.Require(HarnessTarget.Game, "game directory", "game");
string dataDir = Path.Combine(GamePath, "Data");

if (args.Length < 2) { Console.Error.WriteLine("usage: corridorprobe map|width <CellEditorID> [...]  |  corridorprobe arms <arms.json>  |  corridorprobe find <mesh substring>"); return 1; }
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
for (int i = 2; i < args.Length; i++)
{
    if (args[i] == "--step" && i + 1 < args.Length) step = float.Parse(args[++i]);
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

if (mode == "arms")
{
    // ---- the VFS, for the obstacles' meshes ----
    string Instance = HarnessTarget.Require(HarnessTarget.Instance, "MO2 instance directory", "instance");
    string profileDir = Path.Combine(Instance, "profiles", HarnessTarget.Require(HarnessTarget.Profile, "MO2 profile name", "profile"));
    string modsDir = Path.Combine(Instance, "mods"), overwriteDir = Path.Combine(Instance, "overwrite");
    var comp = Mo2LoadOrder.ReadComposition(profileDir);
    var arch = ArchiveDiscovery.Discover(profileDir, modsDir, dataDir, overwriteDir, GamePath);
    using var assets = AssetResolver.Build(overwriteDir, modsDir, dataDir, comp.EnabledMods, arch.Archives);
    var order = Mo2LoadOrder.Build(profileDir, modsDir, dataDir, overwriteDir);

    // ---- the arms, and the base records they place ----
    var arms = new List<Arm>();
    foreach (var a in armsDoc.GetProperty("arms").EnumerateArray())
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
    if (bosOn)
    {
        foreach (var p in order.OrderedPaths)
        {
            ISkyrimModGetter om;
            try { om = SkyrimMod.CreateFromBinaryOverlay(p, SkyrimRelease.SkyrimSE); } catch { bos.Plugin(null); continue; }
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
    Console.WriteLine("   " + bos.LayerLine());
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
    FloorField floor;
    if (!cell.Interior && cell.Grid is { } fg)
    {
        var frg = new Dictionary<(int, int), Dictionary<FormKey, PlacedRef>>(); var flg = new Dictionary<(int, int), float[,]>();
        foreach (var cp in PlacementExtract.FromMod(mod)) if (!cp.Interior && cp.Worldspace == cell.Worldspace) FloorFeed.FileByGrid(cp, frg, flg);
        var (nbRefs, nbLand) = FloorFeed.Neighbourhood(fg, frg, flg);
        floor = FloorFeed.Build(nbRefs, FloorGeometry, nbLand);
    }
    else floor = FloorFeed.Build(cell.Refs, FloorGeometry, null);

    // ---- the band and the graph ----
    float nominalTop = armsDoc.TryGetProperty("bandTop", out var bt) ? bt.GetSingle() : ActorBand.ScaleOneHeight;
    float top = controllerBand ? ActorBand.PhysicalControllerTop(nominalTop) : nominalTop;
    var ladder = new[] { 0f, 32f, 64f, 96f, 128f };
    var (gnodes, grpt) = NavGraph.Build(navs);
    Console.WriteLine($"graph: {grpt.Triangles:N0} triangles, {grpt.IntraLinks:N0} intra + {grpt.CrossLinks:N0} cross links, {grpt.DanglingLinks} dangling, {grpt.ExternalLinks} external, {grpt.Components} component(s)");
    Console.WriteLine($"band: {ActorBand.PhysicalStep:0.#}..{top:0.#} u above the FEET ({(controllerBand ? $"controller: nominal {nominalTop:0} × {ActorBand.ControllerFactor} + {ActorBand.StationOffset:0.#}" : "nominal")}; bottom measured 2026-09-05 + the station's {ActorBand.StationOffset:0.#} u offset (Q24), step band to {ActorBand.PhysicalStepBandTop:0.#} u named in the verdict); capsule 22 u; window ±256 u, step 16");
    Console.WriteLine($"floor: physical, {floor.Candidates:N0} candidates, {floor.LandCells} land cell(s) (Q24)\n");

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
        var columns = MeshColumnIndex.Build(w, g.Soup.Indices);

        bool draw = ascii is not null && a.Tag.Contains(ascii, StringComparison.OrdinalIgnoreCase);
        var fr = RouteConnectivity.Measure(navs, columns, world, 256f, 16f, ActorBand.PhysicalStep, top, ladder, graph: gnodes, keepRaster: draw, floorAt: floor.FloorAt);
        var vr = Verdict(fr);
        if (draw && fr.Raster is { } ras)
        {
            Console.WriteLine($"\n   ---- raster: {a.Tag} — {vr}; {fr.Line?.Status} {fr.Line?.Why} ----");
            Console.Write(ras.Ascii());
        }
        RouteFacts? fc = null; RouteCutVerdict? vc = null;
        if (a.Line is { Length: >= 2 })
        {
            fc = RouteConnectivity.Measure(navs, columns, world, 256f, 16f, ActorBand.PhysicalStep, top, ladder, graph: gnodes, lineOverride: a.Line, floorAt: floor.FloorAt);
            vc = Verdict(fc);
        }
        rows.Add((a, bm.Type, vr, fr, vc, fc, g.Source + bosNote));
    }

    static RouteCutVerdict Verdict(RouteFacts f) =>
        DetectorModel.RouteCut(f.Judged, f.Touched, f.Line?.Found ?? false, f.Line?.Why ?? "", f.Line?.Cut ?? false, f.Line?.Passage ?? float.NaN, cutRise: f.Line?.CutRise ?? float.NaN).Verdict;
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
