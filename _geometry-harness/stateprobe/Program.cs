using System.Diagnostics;
using System.Numerics;
using System.Text.Json;
using HarnessShared;
using HousecarlCore;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Skyrim;

// STATEPROBE — the state-conditional class (roadmap 4.11, handoff 2026-09-20 Part C.2): every switch (or the states
// named) flipped, and the geometry detectors run across the flip on the switch's own sets — overlap, contact, the
// route rule — with StateConditional rows (present only once the switch is flipped) and StateResolved rows (present at
// load time, gone once it is flipped), split by population (rule 12) with the Bethesda floor and the same-defect
// control first.
//
// The evaluation is _shared/StateClass.cs (StateEvaluator over ContactIndex + SwitchIndex); the census is
// _shared/WorldState.cs; the decision DetectorModel.StateDifferential. This file is I/O: the load order into the
// index, meshes through the VFS (contactprobe's geometry resolver), the winning navmesh and actor band per cell for
// the route oracle (blockprobe's obstacle step), the tallies, the listings, --json and --emit.
//
// usage: stateprobe [--state SPEC]* [--all] [--cell CELL]* [--switch-map FILE] [--list N] [--json FILE] [--emit FILE]
//                   [--route on|off] [--enable X.esp]* [--disable X.esp]* [--instance DIR] [--game DIR] [--profile NAME]
//   --state SPEC     a state to evaluate: <switch>[,<switch>…] by editor id or FormKey, or stage:<quest>:<n> (needs --switch-map)
//   --all            every switch alone, plus every joint flip in --switch-map when given
//   --cell CELL      every switch with a set member in the cell (editor id prefix or FormKey) — the per-contested-cell mode
//   --switch-map     switchprobe --json output: the joint flips
//   --route on|off   the route half (default on): the cell's winning navmesh and actor band are read for every cell a
//                    changed reference stands in; off skips it (counted, never invented)
//   --emit FILE      the class as a ConflictFile (kind STATE-OVERLAP / STATE-CONTACT / STATE-ROUTE @ state), the Bethesda floor withheld

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
string? jsonPath = null, emitPath = null, mapPath = null;
bool all = false, routeOn = true;
var stateSpecs = new List<string>(); var cellFilters = new List<string>();
var enablePlugins = new List<string>(); var disablePlugins = new List<string>();
for (int i = 0; i < args.Length; i++)
{
    if (args[i] == "--list" && i + 1 < args.Length) listN = int.Parse(args[++i]);
    else if (args[i] == "--json" && i + 1 < args.Length) jsonPath = args[++i];
    else if (args[i] == "--emit" && i + 1 < args.Length) emitPath = args[++i];
    else if (args[i] == "--switch-map" && i + 1 < args.Length) mapPath = args[++i];
    else if (args[i] == "--state" && i + 1 < args.Length) stateSpecs.Add(args[++i]);
    else if (args[i] == "--cell" && i + 1 < args.Length) cellFilters.Add(args[++i]);
    else if (args[i] == "--all") all = true;
    else if (args[i] == "--route" && i + 1 < args.Length) routeOn = args[++i].ToLowerInvariant() switch { "on" => true, "off" => false, _ => throw new ArgumentException("--route takes on|off") };
    else if (args[i] == "--enable" && i + 1 < args.Length) enablePlugins.Add(args[++i]);
    else if (args[i] == "--disable" && i + 1 < args.Length) disablePlugins.Add(args[++i]);
}
if (stateSpecs.Count == 0 && cellFilters.Count == 0 && !all) { Console.Error.WriteLine("nothing to evaluate: give --state, --cell or --all"); return 2; }

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
Console.WriteLine($"load order: {paths.Count} plugins; VFS: {comp.EnabledMods.Count} enabled mods, {arch.Archives.Count} archives\n");

// ---------------------------------------------------------------------------------------
// Geometry — contactprobe's resolver: the actor-solid collision as the host surface, every solid body as the
// dependent's, the render soup as a labelled fallback, OBND as the last resort.
// ---------------------------------------------------------------------------------------
ContactIndex? cidx = null;
var meshGeom = new Dictionary<string, RefGeometry?>(StringComparer.OrdinalIgnoreCase);
long meshesMeasured = 0, obndFallback = 0, noGeometry = 0;
RefGeometry? GeomOf(FormKey baseKey)
{
    RefGeometry? g = null;
    if (cidx!.Index.BaseMesh.TryGetValue(baseKey, out var rel))
    {
        var full = rel.StartsWith("meshes", StringComparison.OrdinalIgnoreCase) ? rel : Path.Combine("meshes", rel);
        if (!meshGeom.TryGetValue(full, out g))
        {
            g = null;
            var res = assets.ResolveForPlacement(full);
            if (res.Sources.Count > 0)
            {
                var (bytes, _) = AssetResolver.ReadPlacementSource(res.Sources[0]);
                if (bytes is not null && NifGeometry.Measure(bytes, includeTriangles: true) is { Model: { } m })
                {
                    meshesMeasured++;
                    var hc = m.CollisionSoupFor(b => b.Solid && CollisionLayers.IsActorSolid(b.Layer));
                    var dc = m.CollisionSoupFor(b => b.Solid && b.Layer != "SKYL_NONCOLLIDABLE");
                    TriangleSoup host, dep; char hs, ds;
                    if (m.CollisionComplete && hc is { TriangleCount: > 0 }) { host = hc; hs = 'c'; }
                    else if (m.Soup is { TriangleCount: > 0 }) { host = m.Soup; hs = 'r'; }
                    else { host = TriangleSoup.Empty; hs = '-'; }
                    if (m.CollisionComplete && dc is { TriangleCount: > 0 }) { dep = dc; ds = 'c'; }
                    else if (m.Soup is { TriangleCount: > 0 }) { dep = m.Soup; ds = 'r'; }
                    else { dep = TriangleSoup.Empty; ds = '-'; }
                    var box = SpatialOverlap.SoupBounds(host).Union(SpatialOverlap.SoupBounds(dep));
                    var bodies = m.CollisionBodies?.Where(b => b.Solid).Select(b => (b.Layer, b.MotionSystem)).ToList() ?? new List<(string, string)>();
                    bool phantom = m.HasCollision && !m.HasSolidCollision;
                    g = box.IsEmpty ? null : new RefGeometry(box, host, dep, hs, ds, HostUsable: hs == 'c', bodies, phantom);
                }
            }
            meshGeom[full] = g;
        }
    }
    if (g is null)
    {
        var ob = cidx.Index.ObndBox(baseKey);
        if (!ob.IsEmpty) { obndFallback++; g = new RefGeometry(ob, ContactIndex.BoxSoup(ob), ContactIndex.BoxSoup(ob), 'o', 'o', HostUsable: false, Array.Empty<(string, string)>(), false); }
        else noGeometry++;
    }
    return g;
}
cidx = new ContactIndex(GeomOf);
var idx = cidx.Index;

// ---------------------------------------------------------------------------------------
// Pass 1 — every plugin once, into the shared index.
// ---------------------------------------------------------------------------------------
var pluginKey = new ModKey[paths.Count];
var unreadable = new List<string>();
for (int p = 0; p < paths.Count; p++)
{
    ISkyrimModGetter mod;
    try { mod = SkyrimMod.CreateFromBinaryOverlay(paths[p], SkyrimRelease.SkyrimSE); }
    catch { unreadable.Add(Path.GetFileName(paths[p])); pluginKey[p] = ModKey.Null; continue; }
    using (mod as IDisposable) { pluginKey[p] = mod.ModKey; idx.Ingest(p, mod); }
    if ((p + 1) % 150 == 0) Console.WriteLine($"   ... {p + 1}/{paths.Count} plugins, {idx.Placements:N0} placements, {sw.Elapsed.TotalSeconds:0}s");
}
Console.WriteLine($"indexed {idx.Placements:N0} placements over {idx.History.Count:N0} refs  ({sw.Elapsed.TotalSeconds:0.0}s); unreadable plugins {unreadable.Count}");
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

// ---------------------------------------------------------------------------------------
// The route oracle — blockprobe's obstacle step over the cells a changed reference stands in.
// ---------------------------------------------------------------------------------------
var wantedCells = new HashSet<FormKey>();
foreach (var a in arms) foreach (var c in a.Changes(sx)) if (idx.History.Winning(c.Key) is { } w) wantedCells.Add(w.Cell);
Console.WriteLine($"cells touched by the states' changes: {wantedCells.Count:N0}");
RouteAsk? oracle = null;
Func<string>? routeFloorLine = null;
long routeMeasured = 0, routeNoNav = 0, routeNoGeo = 0, routeSetAside = 0, routeNoCross = 0;
if (routeOn)
{
    sw.Restart();
    var winNav = new Dictionary<FormKey, Dictionary<FormKey, NavmeshGeometry>>();      // cell -> navmesh key -> geometry (winner)
    var cellRefs = new Dictionary<FormKey, List<PlacedRef>>();
    var npcFacts = new Dictionary<FormKey, ActorBand.NpcFacts>();
    var raceFacts = new Dictionary<FormKey, ActorBand.RaceFacts>();
    // The floor under a wanted exterior cell reaches across its borders (Q24, 2026-09-05 — _shared/FloorFeed.cs): the 3×3
    // neighbourhood's refs and LAND are filed by worldspace and grid during the walk. A wanted cell's grid is learned from
    // the first plugin that defines it (Skyrim.esm for all but a mod's own cells), read ahead of the walk below so the
    // first plugin's own neighbours are filed too.
    var cellGridOf = new Dictionary<FormKey, (FormKey Wrld, int X, int Y)>();
    var floorGrids = new HashSet<(FormKey Wrld, int X, int Y)>();
    var floorRefsByWorld = new Dictionary<FormKey, Dictionary<(int, int), Dictionary<FormKey, PlacedRef>>>();
    var floorLandByWorld = new Dictionary<FormKey, Dictionary<(int, int), float[,]>>();
    void LearnGrid(CellPlacements cp)
    {
        if (cp.Interior || cp.Grid is not { } g || !wantedCells.Contains(cp.Cell) || cellGridOf.ContainsKey(cp.Cell)) return;
        cellGridOf[cp.Cell] = (cp.Worldspace, g.X, g.Y);
        for (int dx = -1; dx <= 1; dx++) for (int dy = -1; dy <= 1; dy++) floorGrids.Add((cp.Worldspace, g.X + dx, g.Y + dy));
    }
    if (paths.Count > 0 && !pluginKey[0].IsNull)
    {
        try { using var m0 = SkyrimMod.CreateFromBinaryOverlay(paths[0], SkyrimRelease.SkyrimSE); foreach (var cp in PlacementExtract.FromMod(m0)) LearnGrid(cp); }
        catch { }
    }
    for (int p = 0; p < paths.Count; p++)
    {
        if (pluginKey[p].IsNull) continue;
        ISkyrimModGetter m;
        try { m = SkyrimMod.CreateFromBinaryOverlay(paths[p], SkyrimRelease.SkyrimSE); } catch { continue; }
        using (m as IDisposable)
        {
            foreach (var rec in m.EnumerateMajorRecords()) ActorBand.CollectRecord(rec, npcFacts, raceFacts);
            foreach (var cp in PlacementExtract.FromMod(m))
            {
                LearnGrid(cp);
                if (!cp.Interior && cp.Grid is { } fg && floorGrids.Contains((cp.Worldspace, fg.X, fg.Y)))
                {
                    if (!floorRefsByWorld.TryGetValue(cp.Worldspace, out var frg)) { floorRefsByWorld[cp.Worldspace] = frg = new(); floorLandByWorld[cp.Worldspace] = new(); }
                    FloorFeed.FileByGrid(cp, frg, floorLandByWorld[cp.Worldspace]);
                }
                if (!wantedCells.Contains(cp.Cell)) continue;
                if (cp.Navmeshes.Count > 0)
                {
                    if (!winNav.TryGetValue(cp.Cell, out var d)) winNav[cp.Cell] = d = new Dictionary<FormKey, NavmeshGeometry>();
                    foreach (var nav in cp.Navmeshes) d[nav.FormKey] = nav;
                }
                if (cp.Refs.Count > 0)
                {
                    if (!cellRefs.TryGetValue(cp.Cell, out var l)) cellRefs[cp.Cell] = l = new List<PlacedRef>();
                    var seen = new HashSet<FormKey>(l.Select(r => r.FormKey));
                    l.RemoveAll(r => cp.Refs.Any(x => x.FormKey == r.FormKey));
                    l.AddRange(cp.Refs);
                }
            }
        }
    }
    var (fallbackTop, fallbackSrc) = ActorBand.TallestPlayable(raceFacts);
    Console.WriteLine($"route oracle: winning navmesh read for {winNav.Count:N0} of {wantedCells.Count:N0} cells; actor band fallback {fallbackSrc}; the band stands on the physical floor (Q24: {floorRefsByWorld.Values.Sum(w => w.Values.Sum(d => d.Count)):N0} exterior floor refs filed for {cellGridOf.Count:N0} exterior cells' neighbourhoods)  ({sw.Elapsed.TotalSeconds:0.0}s)");
    var navIndex = new Dictionary<FormKey, (NavmeshIndex Index, float Top, Dictionary<NavGraph.TriRef, NavGraph.Node>? Graph, FloorField Floor)>();
    var floorTally = new FloorFeed.Tally();
    routeFloorLine = () =>
    {
        var t = new FloorFeed.Tally(); foreach (var f in navIndex.Values) t.Add(f.Floor);
        var sb = new System.Text.StringBuilder(t.Line());
        sb.Append("\n   the floor each cell's band stood on (δ = navmesh − floor over the raster reads; a strongly negative median is a floor laid ABOVE the winning navmesh):");
        foreach (var kv in navIndex.Where(kv => kv.Value.Floor.Reads > 0).OrderBy(kv => kv.Value.Floor.OffsetQuantile(0.5f)).Take(40))
            sb.Append("\n      " + FloorFeed.Tally.CellLine(kv.Key.ToString(), kv.Value.Floor));
        return sb.ToString();
    };
    var soupCache = new Dictionary<string, (Aabb Box, TriangleSoup? Soup, string Source)>(StringComparer.OrdinalIgnoreCase);
    (Aabb Box, TriangleSoup? Soup, string Source) Obstacle(string rel)
    {
        if (soupCache.TryGetValue(rel, out var hit)) return hit;
        var full = rel.StartsWith("meshes", StringComparison.OrdinalIgnoreCase) ? rel : Path.Combine("meshes", rel);
        var res = assets.ResolveForPlacement(full);
        if (res.Sources.Count == 0) return soupCache[rel] = (Aabb.Empty, null, "unresolved");
        var (bytes, _) = AssetResolver.ReadPlacementSource(res.Sources[0]);
        if (bytes is null) return soupCache[rel] = (Aabb.Empty, null, "unresolved");
        var o = NifGeometry.Measure(bytes, includeTriangles: true);
        if (o.Model is null) return soupCache[rel] = (Aabb.Empty, null, "unreadable");
        var m = o.Model;
        bool anySolid = m.CollisionBodies is not null && m.CollisionBodies.Any(b => b.Solid && CollisionLayers.IsActorSolid(b.Layer));
        if (!m.HasCollision || !anySolid) return soupCache[rel] = (Aabb.Empty, null, "none");
        var solid = m.CollisionSoupFor(b => b.Solid && CollisionLayers.IsActorSolid(b.Layer));
        if (m.CollisionComplete && solid is { TriangleCount: > 0 })
        {
            var bx = Aabb.Empty;
            foreach (var v in solid.Vertices) bx = bx.Add(v);
            return soupCache[rel] = (bx, solid, "collision");
        }
        return soupCache[rel] = (m.Bounds, m.Soup is { TriangleCount: > 0 and <= 200_000 } ? m.Soup : null, "fallback");
    }
    var Ladder = new[] { 0f, 32f, 64f, 96f, 128f };
    oracle = (FormKey key, bool navcut, out string why) =>
    {
        why = "";
        var win = idx.History.Winning(key);
        if (win is not { } w) { why = "no winning placement"; return null; }
        if (!winNav.TryGetValue(w.Cell, out var navs) || navs.Count == 0) { why = "the cell has no navmesh"; routeNoNav++; return null; }
        if (!navIndex.TryGetValue(w.Cell, out var ni))
        {
            var index = NavmeshIndex.Build(navs.Values.ToList(), out _);
            var band = ActorBand.ForCell(cellRefs.TryGetValue(w.Cell, out var crefs) ? crefs : Array.Empty<PlacedRef>(), npcFacts, raceFacts, fallbackTop, fallbackSrc);
            // The floor the band stands on (Q24): the 3×3 neighbourhood of an exterior, else the cell's own refs.
            (Aabb Box, TriangleSoup Soup)? FloorGeometry(PlacedRef r)
            {
                if (r.Base is not { } b || !idx.BaseMesh.TryGetValue(b, out var rel)) return null;
                var g = Obstacle(rel);
                return g.Soup is not null && !g.Box.IsEmpty ? (g.Box, g.Soup) : null;
            }
            FloorField floor;
            if (cellGridOf.TryGetValue(w.Cell, out var cg) && floorRefsByWorld.TryGetValue(cg.Wrld, out var frw))
            {
                var (nbRefs, nbLand) = FloorFeed.Neighbourhood((cg.X, cg.Y), frw, floorLandByWorld[cg.Wrld]);
                floor = FloorFeed.Build(nbRefs, FloorGeometry, nbLand);
            }
            else floor = FloorFeed.Build(crefs ?? new List<PlacedRef>(), FloorGeometry, null);
            navIndex[w.Cell] = ni = (index, ActorBand.PhysicalControllerTop(band.Top), null, floor);
        }
        if (ni.Index.TriangleCount == 0) { why = "the cell's navmesh is empty"; routeNoNav++; return null; }
        TriangleSoup soup; Aabb model;
        if (navcut)
        {
            if (!idx.RefPrimitive.TryGetValue(key, out var prim)) { why = "navcut without a primitive"; routeNoGeo++; return null; }
            model = NavcutGeometry.ModelBox(prim); soup = NavcutGeometry.Soup(prim);
            if (model.IsEmpty) { why = "navcut with an empty box"; routeNoGeo++; return null; }
        }
        else
        {
            var b = cidx!.WinnerBase(key);
            if (b.IsNull || !idx.BaseMesh.TryGetValue(b, out var rel)) { why = "no mesh"; routeNoGeo++; return null; }
            string type = idx.BaseInfo.TryGetValue(b, out var bi) ? bi.Type : "?";
            if (type == "Door") { why = "a Door: passable by design, set aside"; routeSetAside++; return null; }
            if (type == "MoveableStatic") { why = "a MoveableStatic: pushed aside, set aside"; routeSetAside++; return null; }
            var g = Obstacle(rel);
            if (g.Soup is null || g.Box.IsEmpty) { why = $"no actor-solid geometry ({g.Source})"; routeNoGeo++; return null; }
            model = g.Box; soup = g.Soup;
        }
        var world = SpatialOverlap.ToWorld(model, w.Pos, w.Rot, w.Scale);
        var verts = NavcutGeometry.WorldVertices(soup, w.Pos, w.Rot, w.Scale);
        var columns = MeshColumnIndex.Build(verts, soup.Indices);
        // The cheap test first, as blockprobe does: does the solid enter an actor column over any walkable triangle?
        var q = SpatialOverlap.GrowDown(world, ni.Top);
        bool crosses = false;
        foreach (var t in ni.Index.Query(q))
        {
            if ((t.Flags & 0x08) != 0) continue;
            var nav = ni.Index.NavAt(t.NavIndex); var tri = nav.Triangles[t.TriangleIndex];
            var pa = nav.Vertices[tri.A]; var pb = nav.Vertices[tri.B]; var pc = nav.Vertices[tri.C];
            int x = columns.CountSurfaceCrossings(new Vector3(pa.X, pa.Y, pa.Z), new Vector3(pb.X, pb.Y, pb.Z), new Vector3(pc.X, pc.Y, pc.Z), ActorBand.StepHeight, ni.Top, 12f, 1f, 0.5f, stopAfter: 1);
            if (x > 0) { crosses = true; break; }
        }
        if (!crosses) { why = "never enters an actor column over walkable ground"; routeNoCross++; return RouteCutVerdict.None; }
        if (ni.Graph is null) { ni.Graph = NavGraph.Build(ni.Index).Nodes; navIndex[w.Cell] = ni; }
        var facts = RouteConnectivity.Measure(ni.Index, columns, world, 256f, 16f, ActorBand.PhysicalStep, ni.Top, Ladder, graph: ni.Graph, floorAt: ni.Floor.FloorAt);
        var line = facts.Line;
        var vc = DetectorModel.RouteCut(facts.Judged, facts.Touched, line?.Found ?? false, line?.Why ?? "", line?.Cut ?? false, line?.Passage ?? float.NaN, cutRise: line?.CutRise ?? float.NaN, cutFloat: line?.CutFloat ?? float.NaN);
        routeMeasured++;
        why = vc.Why;
        return vc.Verdict;
    };
}
else Console.WriteLine("route oracle: off (--route on to measure the route half)");

// ---------------------------------------------------------------------------------------
// Evaluate.
// ---------------------------------------------------------------------------------------
sw.Restart();
var ev = new StateEvaluator(cidx, sx) { Route = oracle };
Console.WriteLine($"evaluator: {ev.Boxed:N0} refs boxed (meshes measured {meshesMeasured:N0}, OBND fallback {obndFallback:N0}, no geometry {noGeometry:N0}; markers set aside {ev.BoxedMarkers:N0}, effect volumes kept out of the overlap role {ev.BoxedFxVolumes:N0})  ({sw.Elapsed.TotalSeconds:0.0}s)");
sw.Restart();
var tally = new StateTally();
var rows = new List<StateRow>();
var perState = new List<(StateArm Arm, int Changes, int Rows, int Conditional, double Ms)>();
int done = 0;
foreach (var arm in arms)
{
    var t = new StateTally();
    var r = ev.Evaluate(arm, IsVanilla, IsUssep, t);
    rows.AddRange(r);
    perState.Add((arm, t.Changes, r.Count, r.Count(x => x.Verdict == StateVerdict.StateConditional && !x.SameSet), t.ElapsedMs));
    Merge(tally, t);
    if (++done % 250 == 0) Console.WriteLine($"   ... {done:N0}/{arms.Count:N0} states, {rows.Count:N0} rows, {sw.Elapsed.TotalSeconds:0}s");
}
static void Merge(StateTally a, StateTally b)
{
    a.States += b.States; a.Changes += b.Changes; a.Arrivals += b.Arrivals; a.Departures += b.Departures; a.SwitchesThemselves += b.SwitchesThemselves;
    a.NoGeometry += b.NoGeometry; a.NoBox += b.NoBox; a.NotObstructing += b.NotObstructing; a.Volume += b.Volume; a.PairsTested += b.PairsTested; a.SameSetPairs += b.SameSetPairs; a.Unchanged += b.Unchanged;
    a.HostsLeaving += b.HostsLeaving; a.HostsArriving += b.HostsArriving; a.DepsArriving += b.DepsArriving; a.DepsLeaving += b.DepsLeaving; a.ReSupported += b.ReSupported; a.FloatingOnArrival += b.FloatingOnArrival; a.TouchingOnArrival += b.TouchingOnArrival; a.ContactUnmeasured += b.ContactUnmeasured;
    a.RouteAsked += b.RouteAsked; a.RouteUnjudged += b.RouteUnjudged; a.RouteNoLine += b.RouteNoLine; a.RouteClipped += b.RouteClipped; a.RouteNone += b.RouteNone; a.ElapsedMs += b.ElapsedMs;
}
double totalS = sw.Elapsed.TotalSeconds;
var cellStates = perState.Sum(p => arms.First(a => a == p.Arm).Changes(sx).Select(c => idx.History.Winning(c.Key)?.Cell ?? FormKey.Null).Distinct().Count());
Console.WriteLine($"\n==== COST ====");
Console.WriteLine($"   {arms.Count:N0} states over {tally.Changes:N0} changed references ({tally.Arrivals:N0} arrivals, {tally.Departures:N0} departures) in {totalS:0.0}s — {(arms.Count == 0 ? 0 : 1000 * totalS / arms.Count):0} ms per state; "
                + $"{cellStates:N0} cell-states, {(cellStates == 0 ? 0 : 1000 * totalS / cellStates):0} ms per cell-state; the route half measured {routeMeasured:N0} obstacles");
if (routeFloorLine is not null) Console.WriteLine("   " + routeFloorLine());
Console.WriteLine($"   set aside (rule 6): {tally.NoBox:N0} changed refs without a box (no geometry, a marker, or a navcut without a primitive); route: {routeNoNav:N0} in a cell without navmesh, {routeNoGeo:N0} without actor-solid geometry, {routeSetAside:N0} doors / movables, {routeNoCross:N0} never entering an actor column, {tally.RouteUnjudged:N0} unjudged");

var cls = rows.Where(r => r.Verdict == StateVerdict.StateConditional && !r.SameSet && !r.SameAuthor).ToList();
var resolved = rows.Where(r => r.Verdict == StateVerdict.StateResolved && !r.SameSet && !r.SameAuthor).ToList();
var ownFloor = rows.Where(r => r.Verdict == StateVerdict.StateConditional && !r.SameSet && r.SameAuthor).ToList();
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
Console.WriteLine($"   contact counted: hosts leaving {tally.HostsLeaving:N0}, arriving {tally.HostsArriving:N0}; dependents arriving {tally.DepsArriving:N0} (touching {tally.TouchingOnArrival:N0}, floating {tally.FloatingOnArrival:N0}), re-supported {tally.ReSupported:N0}, unmeasured {tally.ContactUnmeasured:N0}");
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
        states = arms.Count, elapsedSeconds = totalS, cellStates, routeMeasured,
        tally = new { tally.Changes, tally.Arrivals, tally.Departures, tally.NoBox, tally.PairsTested, tally.SameSetPairs, tally.HostsLeaving, tally.HostsArriving, tally.DepsArriving, tally.ReSupported, tally.FloatingOnArrival, tally.TouchingOnArrival, tally.ContactUnmeasured, tally.RouteAsked, tally.RouteUnjudged, tally.RouteNoLine, tally.RouteClipped, tally.RouteNone, routeNoNav, routeNoGeo, routeSetAside, routeNoCross },
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
    foreach (var r in cls.Where(r => r.Population != StatePopulation.BethesdaFloor))
        conflicts.Add(new Conflict($"STATE-{r.Kind.ToString().ToUpperInvariant()}@{r.State}@{TriggerOf(r.Switch)}", PluginName(r.MemberResponsible), r.Other is null ? PluginName(r.SwitchOwner) : PluginName(r.OtherResponsible),
            $"{r.Kind} {r.Verdict} under {sx.Switches[r.Switch].Label} ({PluginName(r.SwitchOwner)}): {RefLabel(r.Member)} {(r.Arrives ? "arrives" : "leaves")} — {r.Why} in {CellOf(r.Cell)}",
            new List<ConflictRecord> { new(r.Member.ToString(), "placed-ref", CellOf(r.Cell), r.Physical, r.PhysicalDetail) },
            r.Other is { } o ? new List<ConflictRecord> { new(o.ToString(), "placed-ref", null) } : null));
    var coverage = new List<CoverageLine>
    {
        new("plugins", paths.Count - unreadable.Count, new Dictionary<string, long> { ["UnreadablePlugin"] = unreadable.Count }),
        new("states", arms.Count, null, null, $"{tally.Changes:N0} changed references"),
        new("changed references with a box", tally.Changes - tally.NoBox, new Dictionary<string, long> { ["NoBox"] = tally.NoBox }),
        new("route obstacles", routeMeasured, new Dictionary<string, long> { ["NoNavmesh"] = routeNoNav, ["NoGeometry"] = routeNoGeo, ["Unjudged"] = tally.RouteUnjudged }, new Dictionary<string, long> { ["DoorOrMovable"] = routeSetAside, ["NeverEntersColumn"] = routeNoCross }),
        new("contact pairs", tally.HostsLeaving + tally.HostsArriving + tally.DepsArriving, new Dictionary<string, long> { ["Unmeasured"] = tally.ContactUnmeasured }, new Dictionary<string, long> { ["ReSupported"] = tally.ReSupported, ["TouchingOnArrival"] = tally.TouchingOnArrival, ["FloatingOnArrival"] = tally.FloatingOnArrival }),
    };
    ConflictIo.Write(emitPath, new ConflictFile("stateprobe", Instance, DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm:ssZ"), conflicts, coverage));
    Console.WriteLine($"emitted {conflicts.Count:N0} StateConditional conflicts -> {emitPath}");
}

Console.WriteLine();
Console.WriteLine("A StateConditional row exists only once its switch is flipped — a quest stage, a scene or a trigger the records name, or one they do not (Unknown");
Console.WriteLine("triggers are flipped all the same). Records only; boxes and soups, not the game. Part D flips one marker by console and reads the sets back.");
Console.WriteLine($"elapsed {sw.Elapsed.TotalSeconds:0.0}s");
return 0;

static string Short(string s, int n) => s.Length <= n ? s : s[..(n - 3)] + "...";
