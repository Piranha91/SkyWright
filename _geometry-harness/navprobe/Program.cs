using HarnessShared;
using System.Diagnostics;
using HousecarlCore;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Skyrim;

// Milestone 4 CALIBRATION.
//
// The detector's value is precision, not recall (charter). So the first question is not
// "what does it flag" but "what does it flag on VANILLA" — where Bethesda authored the
// objects and the navmesh together, so every hit is by definition a false positive.
// That rate is the noise floor, and no threshold is worth using until it is near zero.
//
// usage: navprobe [maxCells] [--dump dir] [--geometry collision|render] [--bos on|off]
//   --dump writes the flagged reference keys per narrow rule, one file each — a diagnostic for comparing two
//   runs ref by ref (which verdicts moved under a transform change), not part of the report.
//   --geometry which triangle soup the narrow rules run on (default collision, since 2026-09-02 — roadmap 4.7 Part A):
//       collision  the decoded Havok solid, restricted to the layers an actor meets (_shared/CollisionLayers.cs).
//                  A mesh with no actor-solid body is NOT TESTED — it blocks nothing — and counted; a mesh whose
//                  decode is incomplete falls back to its render soup, labelled and counted.
//       render     the render soup, as every run before 2026-09-02 (a pine judged by its canopy).
//   --bos      on (default, since 2026-09-04): the Base Object Swapper layer (*_SWAP.ini, from the profile's VFS;
//              _shared/BosHook.cs) applied to Skyrim.esm's placements — each reference judged by the base the game
//              LOADS for it, at the transform its overrides give it; one an override disables at init is not tested.
//              The whole order is walked once for the layer's index and for the winning model of a swapped base.
//              READ THE FLOOR WITH CARE under on: a row whose object the layer swapped or moved is no longer
//              "Bethesda authored the object and the navmesh together" — it is a finding against the swap layer, and
//              the route-cut section reports those rows apart. off: the pure vanilla floor, as every run before.

string Instance = HarnessTarget.Require(HarnessTarget.Instance, "MO2 instance directory", "instance");
string GamePath = HarnessTarget.Require(HarnessTarget.Game, "game directory", "game");
string profileDir = Path.Combine(Instance, "profiles", HarnessTarget.Require(HarnessTarget.Profile, "MO2 profile name", "profile"));
string modsDir = Path.Combine(Instance, "mods");
string overwriteDir = Path.Combine(Instance, "overwrite");
string dataDir = Path.Combine(GamePath, "Data");
string vanilla = Path.Combine(dataDir, "Skyrim.esm");

int maxCells = args.Length > 0 && int.TryParse(args[0], out var mc) ? mc : int.MaxValue;
string? dumpDir = null;
string geometrySource = "collision";
bool controllerBand = true;   // --band controller|nominal: the route rules' band top (the surface rules keep the nominal, as every run before)
bool bosOn = true;
for (int i = 0; i < args.Length; i++)
{
    if (args[i] == "--dump" && i + 1 < args.Length) dumpDir = args[++i];
    else if (args[i] == "--geometry" && i + 1 < args.Length) geometrySource = args[++i];
    else if (args[i] == "--band" && i + 1 < args.Length) controllerBand = args[++i] != "nominal";
    else if (args[i] == "--bos" && i + 1 < args.Length) bosOn = BosHook.ParseFlag(args[++i]) ?? throw new ArgumentException("--bos takes on|off");
}
if (geometrySource is not ("collision" or "render")) { Console.Error.WriteLine("--geometry must be collision or render"); return 1; }
Console.WriteLine($"geometry source: {geometrySource}" + (geometrySource == "collision" ? $"  (actor-solid layers: {string.Join(", ", CollisionLayers.ActorSolid)})" : ""));

var sw = Stopwatch.StartNew();
var comp = Mo2LoadOrder.ReadComposition(profileDir);
var arch = ArchiveDiscovery.Discover(profileDir, modsDir, dataDir, overwriteDir, GamePath);
using var assets = AssetResolver.Build(overwriteDir, modsDir, dataDir, comp.EnabledMods, arch.Archives);
Console.WriteLine($"assets: {arch.Archives.Count} BSAs indexed in {sw.ElapsedMilliseconds} ms");

// ---- vanilla base records: FormKey -> (mesh path, record type) ----
sw.Restart();
using var mod = SkyrimMod.CreateFromBinaryOverlay(vanilla, SkyrimRelease.SkyrimSE);
var baseMesh = new Dictionary<FormKey, (string Mesh, string Type)>();
var npcFacts = new Dictionary<FormKey, ActorBand.NpcFacts>();
var raceFacts = new Dictionary<FormKey, ActorBand.RaceFacts>();
foreach (var rec in mod.EnumerateMajorRecords())
{
    if (rec is IModeledGetter m && m.Model?.File is { } f && !string.IsNullOrWhiteSpace(f.GivenPath))
        baseMesh[rec.FormKey] = (f.GivenPath, rec.GetType().Name.Replace("BinaryOverlay", ""));
    ActorBand.CollectRecord(rec, npcFacts, raceFacts);
}
Console.WriteLine($"vanilla base records with a mesh: {baseMesh.Count:N0}  ({sw.Elapsed.TotalSeconds:0.0}s)");

// The physical floor under each cell's navmesh (Q24, 2026-09-05 — _shared/FloorFeed.cs): an exterior floor reaches across
// cell borders, so the plugin's exterior refs and LAND are filed by grid per worldspace first (one pass over the cells;
// the sweep below streams them again). The floor candidates are the record's own placements — the swap layer is not
// applied to floors; the obstacles keep their BOS-resolved bases.
sw.Restart();
var floorRefsByWorld = new Dictionary<FormKey, Dictionary<(int, int), Dictionary<FormKey, PlacedRef>>>();
var floorLandByWorld = new Dictionary<FormKey, Dictionary<(int, int), float[,]>>();
foreach (var cp in PlacementExtract.FromMod(mod))
{
    if (cp.Interior) continue;
    if (!floorRefsByWorld.TryGetValue(cp.Worldspace, out var frg)) { floorRefsByWorld[cp.Worldspace] = frg = new(); floorLandByWorld[cp.Worldspace] = new(); }
    FloorFeed.FileByGrid(cp, frg, floorLandByWorld[cp.Worldspace]);
}
Console.WriteLine($"floor candidates filed by grid: {floorRefsByWorld.Values.Sum(w => w.Values.Sum(d => d.Count)):N0} exterior refs, {floorLandByWorld.Values.Sum(w => w.Count):N0} LAND records in {floorRefsByWorld.Count} worldspaces  ({sw.Elapsed.TotalSeconds:0.0}s)");
var floorTally = new FloorFeed.Tally();

// --bos on (default): the swap layer's index wants the WHOLE order — the runtime-id table, every EditorID, the cells'
// locations and keywords — and a swapped base may be defined anywhere in it, so the order is walked once for the index
// (_shared/BosHook.cs, BosFeed) and for the winning model of every modeled record. The population swept below is still
// Skyrim.esm's own placements: what changes is which object stands on each of them, and where. --bos off: nothing here runs.
var bos = new BosFeed(bosOn);
var orderBaseMesh = new Dictionary<FormKey, (string Mesh, string Type)>();   // the ORDER's winning model per base — read for a swapped base only
if (bosOn)
{
    sw.Restart();
    var order = Mo2LoadOrder.Build(profileDir, modsDir, dataDir, overwriteDir);
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
                if (rec is IModeledGetter mo && mo.Model?.File is { } mf && !string.IsNullOrWhiteSpace(mf.GivenPath))
                    orderBaseMesh[rec.FormKey] = (mf.GivenPath, rec.GetType().Name.Replace("BinaryOverlay", ""));
            }
            foreach (var cp in PlacementExtract.FromMod(om)) bos.Cell(cp);
        }
    }
    bos.Load(assets.LooseRoots);
    Console.WriteLine($"swap layer: {order.OrderedPaths.Count} plugins walked for its index, {orderBaseMesh.Count:N0} winning models  ({sw.Elapsed.TotalSeconds:0.0}s)");
}
Console.WriteLine("   " + bos.LayerLine());
var bosRep = new BosApplyReport();
// The actor band's top is DERIVED per cell (the tallest placed actor, else the tallest playable race) since
// 2026-09-02; the settings' Height column is what the BROAD rules still use and what every earlier run used.
ActorBand.ResolveControllers(raceFacts, assets);
var (fallbackTop, fallbackSrc) = ActorBand.TallestPlayable(raceFacts);
Console.WriteLine(FloorFeed.BandLine(controllerBand) + $"; top fallback {fallbackSrc}");
int cellsWithActors = 0; float bandMin = float.MaxValue, bandMax = 0f;
var bandHist = new SortedDictionary<int, int>();   // band top rounded to 4 u -> cells

// The route rule (Part B) on the vanilla floor: every reference whose solid entered an actor column (the CROSS
// clr32 rule) is measured with vs without on the clearance ladder; verdicts tallied by class and record type.
var Ladder = new[] { 0f, 32f, 64f, 96f, 128f };
const float RouteMargin = 256f, RouteStep = 16f;
var routeByClass = new Dictionary<string, int[]>(StringComparer.OrdinalIgnoreCase);   // class -> [Blocked, Narrowed64, Narrowed128, Clipped, None, Unjudgeable, measured]
var routeByType = new Dictionary<string, int[]>();
var routeBlocked = new List<(string Cell, string Mesh, string Type, FormKey Fk, string Why)>();
long routeMeasured = 0, routeDoors = 0, routeMovable = 0;
int[] RouteRow(Dictionary<string, int[]> d, string k) { if (!d.TryGetValue(k, out var row)) d[k] = row = new int[7]; return row; }

// The route-CUT rule (4.7's closing step) on the same references: [Blocked, LikelyBlocked, Clipped, NoLine, None,
// Unjudgeable, measured] by class and record type, the NoLine reasons, the top meshes of the two finding classes, and
// the old-rule × new-rule cross-tab. Band top: derived × the controller factor unless --band nominal.
var cutByClass = new Dictionary<string, int[]>(StringComparer.OrdinalIgnoreCase);
var cutByType = new Dictionary<string, int[]>();
var cutNoLine = new Dictionary<LineStatus, int>();
var cutCross = new Dictionary<(RouteVerdict Old, RouteCutVerdict New), int>();
var cutBlocked = new List<(string Cell, string Mesh, string Type, FormKey Fk, string Why)>();
var cutLikely = new List<(string Cell, string Mesh, string Type, FormKey Fk, string Why)>();
// The rows the BOS layer swapped or moved, tallied apart: under --bos on these are not floor noise but the swap layer's own
// contribution — the object on that placement is no longer the one Bethesda navmeshed around.
var cutBos = new int[7];
var cutBosRows = new List<(string Cell, string Mesh, string Type, FormKey Fk, RouteCutVerdict V, string Why)>();

// ---- lazy, cached geometry per mesh path (AABB + model-space triangle soup) ----
var geoCache = new Dictionary<string, Geo>(StringComparer.OrdinalIgnoreCase);
int measured = 0, measureFail = 0;
long cachedTris = 0;
const int MaxTrisCached = 200_000;   // a mesh past this is AABB-only; nothing in vanilla clutter comes close

Geo Geometry(string rel)
{
    if (geoCache.TryGetValue(rel, out var hit)) return hit;
    var full = rel.StartsWith("meshes", StringComparison.OrdinalIgnoreCase) ? rel : Path.Combine("meshes", rel);
    var res = assets.ResolveForPlacement(full);
    if (res.Sources.Count == 0) { measureFail++; return geoCache[rel] = new Geo(Aabb.Empty, false, false, null, "unresolved"); }
    var (bytes, _) = AssetResolver.ReadPlacementSource(res.Sources[0]);
    if (bytes is null) { measureFail++; return geoCache[rel] = new Geo(Aabb.Empty, false, false, null, "unresolved"); }
    var o = NifGeometry.Measure(bytes, includeTriangles: true);
    if (o.Model is null) { measureFail++; return geoCache[rel] = new Geo(Aabb.Empty, false, false, null, "unreadable"); }
    measured++;
    var m = o.Model;
    var render = m.Soup;
    if (render is not null && render.TriangleCount > MaxTrisCached) render = null;

    if (geometrySource == "render")
    {
        if (render is not null) cachedTris += render.TriangleCount;
        return geoCache[rel] = new Geo(m.Bounds, m.HasCollision, true, render, "render");
    }

    // Collision: the solid an actor meets. Three outcomes, each counted by the caller —
    //   none      no body on an actor-solid layer: the object blocks nothing and is not tested;
    //   collision the complete solid soup on those layers, with ITS box for the broad phase;
    //   fallback  the decode was incomplete (an unread shape in a solid body): the render soup stands in, labelled.
    bool anySolid = m.CollisionBodies is not null && m.CollisionBodies.Any(b => b.Solid && CollisionLayers.IsActorSolid(b.Layer));
    if (!m.HasCollision || !anySolid)
        return geoCache[rel] = new Geo(Aabb.Empty, m.HasCollision, true, null, "none");
    var solid = m.CollisionSoupFor(b => b.Solid && CollisionLayers.IsActorSolid(b.Layer));
    if (m.CollisionComplete && solid is { TriangleCount: > 0 })
    {
        var box = Aabb.Empty;
        foreach (var v in solid.Vertices) box = box.Add(v);
        cachedTris += solid.TriangleCount;
        return geoCache[rel] = new Geo(box, true, true, solid, "collision");
    }
    if (render is not null) cachedTris += render.TriangleCount;
    return geoCache[rel] = new Geo(m.Bounds, m.HasCollision, true, render, "fallback");
}

// A floor candidate's solid: the same soup the obstacles are read from (the collision soup on the actor-solid layers, the
// render soup where the decode is incomplete), by the record's own base.
(Aabb Box, TriangleSoup Soup)? FloorGeometry(PlacedRef r)
{
    if (r.Base is not { } b || !baseMesh.TryGetValue(b, out var bm)) return null;
    var g = Geometry(bm.Mesh);
    return g.Ok && g.Soup is not null && !g.Box.IsEmpty ? (g.Box, g.Soup) : null;
}

// world-space column index per (mesh, transform), memoised within a cell — many refs repeat the same
// mesh at different transforms, but a single ref is queried once per navmesh triangle it touches.
static MeshColumnIndex? BuildColumns(TriangleSoup soup, Vec3 pos, Vec3 rot, float scale)
{
    if (soup.TriangleCount == 0) return null;
    var m = SpatialOverlap.RefrRotation(rot);
    var t = new System.Numerics.Vector3(pos.X, pos.Y, pos.Z);
    var w = new System.Numerics.Vector3[soup.Vertices.Length];
    for (int i = 0; i < w.Length; i++)
        w[i] = System.Numerics.Vector3.Transform(soup.Vertices[i] * scale, m) + t;
    return MeshColumnIndex.Build(w, soup.Indices, triangleMaterials: soup.TriangleMaterials);
}

// ---- sweep vanilla cells ----
// Three rule families, swept together so the trade-off is visible rather than asserted.
//
//   BROAD    the reference's world box vs the navmesh triangle. No narrow phase.
//   CONTAIN  the OLD narrow phase: how many of the triangle's 3 vertices sit INSIDE the object.
//            Structurally blind to thin blockers — a wall leaves all three vertices in open air.
//   CROSS    the replacement: does the object's SURFACE cut across the column an actor standing
//            on that triangle would occupy? Sees the wall; a floor the navmesh lies on it does not.
var settings = new[]
{
    Broad ("BROAD   raw touch",                        0f,   0f, false),
    Broad ("BROAD   actor volume h=128",             128f,   0f, false),
    Broad ("BROAD   + collision only",               128f,   0f, true),
    Contain("CONTAIN all-3 verts inside",              3),
    Contain("CONTAIN any-2 verts inside",              2),
    Contain("CONTAIN any-1 vert inside",               1),
    Cross ("CROSS   clr24 in8  any-facing",           24f,  8f, 1.0f),
    Cross ("CROSS   clr32 in12 any-facing",           32f, 12f, 1.0f),
    Cross ("CROSS   clr48 in16 any-facing",           48f, 16f, 1.0f),
    Sever ("SEVER   clr32 in12 step32",               32f, 12f, 32f),
    Sever ("SEVER   clr32 in12 step48",               32f, 12f, 48f),
    Sever ("SEVER   clr32 in12 step32 sup<0.50",      32f, 12f, 32f, 0.50f),
    Sever ("SEVER   clr32 in12 step32 sup<0.25",      32f, 12f, 32f, 0.25f),
    Sever ("SEVER   clr32 in12 step48 sup<0.25",      32f, 12f, 48f, 0.25f),
    Sever ("SEVER   clr48 in16 step32 sup<0.25",      48f, 16f, 32f, 0.25f),
};

static Setting Broad(string n, float h, float bi, bool co) => new(n, 0, h, bi, co, 0, 0, 0, 1f, 0f, 1f);
static Setting Contain(string n, int min) => new(n, 1, 128f, 0f, true, min, 0, 0, 1f, 0f, 1f);
static Setting Cross(string n, float clr, float ti, float up) => new(n, 2, 128f, 0f, true, 0, clr, ti, up, 0f, 1f);
// SEVER runs CROSS first and only measures connectivity where something actually crossed — the cheap test gates the
// expensive one, and the two counts stay directly comparable because they see the same triangles.
static Setting Sever(string n, float clr, float ti, float step, float sup = 1f) => new(n, 3, 128f, 0f, true, 0, clr, ti, 1f, step, sup);

var flagged = new long[settings.Length];
var refsTested = new long[settings.Length];
var unjudgeable = new long[settings.Length];
// Per record TYPE, for the narrow rules. The residue's composition is the whole diagnosis: a floor made of
// Trees says the geometry source is wrong (render canopy vs a trunk's collision), a floor made of Statics
// says the rule is wrong. Those two look identical in a single percentage.
// Interior vs exterior, split because they are not one population: Bethesda navmeshes an interior tightly
// around its clutter and drapes a coarse mesh over open terrain, so a single corpus-wide floor averages two
// very different authoring standards and describes neither.
var byPlace = new long[settings.Length, 2, 2];       // [setting, interior?, tested/flagged]
// ...and by mesh CLASS, because 'exterior' is not one population either: architecture placed on a street and a
// mountain slab draped in navmesh fail for opposite reasons, and one averaged percentage hides which is which.
var byClass = new Dictionary<string, long[]>[settings.Length];
var byType = new Dictionary<string, long[]>[settings.Length];
for (int s = 0; s < settings.Length; s++) { byType[s] = new Dictionary<string, long[]>(); byClass[s] = new Dictionary<string, long[]>(StringComparer.OrdinalIgnoreCase); }
long[] ClassRow(int s, string t) { if (!byClass[s].TryGetValue(t, out var row)) byClass[s][t] = row = new long[2]; return row; }
var SepChars = new[] { (char)47, (char)92 };
string MeshClass(string mesh) { int i = mesh.IndexOfAny(SepChars); return i <= 0 ? "(root)" : mesh[..i]; }
long[] TypeRow(int s, string t) { if (!byType[s].TryGetValue(t, out var row)) byType[s][t] = row = new long[2]; return row; }
long cells = 0, totalRefs = 0, skippedNoGeo = 0, skippedDisabled = 0, skippedNoSolid = 0, navTris = 0, malformedTotal = 0;
// Which soup each tested ref ran on, per exterior class and interior: [collision, fallback, none(skipped)].
var bySource = new Dictionary<string, long[]>(StringComparer.OrdinalIgnoreCase);
long[] SourceRow(string cls) { if (!bySource.TryGetValue(cls, out var row)) bySource[cls] = row = new long[3]; return row; }
// Keep a per-mesh breakdown for the rules whose residue is worth reading, not just the last one.
var worstOf = new List<(string Cell, string Mesh, string Type, int Tris, FormKey Fk)>?[settings.Length];
for (int s = 0; s < settings.Length; s++)
    if (settings[s].Kind != 0) worstOf[s] = new List<(string, string, string, int, FormKey)>();

sw.Restart();
foreach (var cp in PlacementExtract.FromMod(mod))
{
    int interiorBucket = cp.Interior ? 1 : 0;
    if (cp.Navmeshes.Count == 0 || cp.Refs.Count == 0) continue;
    if (cells >= maxCells) break;
    cells++;

    var index = NavmeshIndex.Build(cp.Navmeshes, out int malformed);
    malformedTotal += malformed;
    navTris += index.TriangleCount;
    if (index.TriangleCount == 0) continue;

    var band = ActorBand.ForCell(cp.Refs, npcFacts, raceFacts, fallbackTop, fallbackSrc);
    if (!float.IsFinite(band.Top)) { Console.WriteLine($"UNJUDGEABLE actor band in {cp.Cell}: {band.Source}"); continue; }
    float bandTop = band.Top;
    float physicalTop = controllerBand ? ActorBand.PhysicalControllerTop(bandTop) : bandTop;
    // The floor the band stands on in this cell: its own refs for an interior, the 3×3 neighbourhood by position for an
    // exterior, and the terrain (Q24).
    FloorField floor;
    if (!cp.Interior && cp.Grid is { } floorGrid && floorRefsByWorld.TryGetValue(cp.Worldspace, out var frw))
    {
        var (nbRefs, nbLand) = FloorFeed.Neighbourhood(floorGrid, frw, floorLandByWorld[cp.Worldspace]);
        floor = FloorFeed.Build(nbRefs, FloorGeometry, nbLand);
    }
    else floor = FloorFeed.Build(cp.Refs, FloorGeometry, null);
    Dictionary<NavGraph.TriRef, NavGraph.Node>? gnodes = null;   // the cell's walkable graph, built once when a route needs it
    if (band.Actors > 0) cellsWithActors++;
    bandMin = MathF.Min(bandMin, bandTop); bandMax = MathF.Max(bandMax, bandTop);
    { int key = (int)MathF.Round(bandTop / 4f) * 4; bandHist.TryGetValue(key, out var bn); bandHist[key] = bn + 1; }

    foreach (var r in cp.Refs)
    {
        totalRefs++;
        if (r.Deleted) { skippedDisabled++; continue; }
        // The Base Object Swapper layer: the base and transform the game LOADS for this placement (the record's own under
        // --bos off). An override that disables it at init is skipped the way the record's own flag is. A swapped base is
        // looked up in the ORDER's winning models (it may be defined anywhere); an unswapped one in Skyrim.esm's, the
        // floor's standing population.
        var eff = bos.Effective(r, cp.Cell, bosRep);
        if (eff.InitiallyDisabled) { skippedDisabled++; continue; }
        if (r.Base is null || !(eff.Swapped ? orderBaseMesh : baseMesh).TryGetValue(eff.Base, out var bm)) { skippedNoGeo++; continue; }

        var g = Geometry(bm.Mesh);
        string srcClass = cp.Interior ? "(interior)" : MeshClass(bm.Mesh);
        if (g.Ok && g.Source == "none") { skippedNoSolid++; SourceRow(srcClass)[2]++; continue; }
        if (!g.Ok || g.Box.IsEmpty) { skippedNoGeo++; continue; }
        SourceRow(srcClass)[g.Source == "collision" ? 0 : 1]++;

        var world = SpatialOverlap.ToWorld(g.Box, eff.Position, eff.Rotation, eff.Scale);

        MeshColumnIndex? columns = null;
        bool columnsTried = false;
        bool crossHit = false;

        for (int s = 0; s < settings.Length; s++)
        {
            var st = settings[s];
            if (st.CollisionOnly && !g.HasCollision) continue;
            if (st.Kind != 0 && g.Soup is null) continue;

            var q = SpatialOverlap.InsetHorizontal(world, st.BoxInset);
            if (q.IsEmpty) continue;
            q = SpatialOverlap.GrowDown(q, st.Kind == 0 ? st.Height : bandTop);

            refsTested[s]++;
            TypeRow(s, bm.Type)[0]++;
            byPlace[s, interiorBucket, 0]++;
            if (interiorBucket == 0) ClassRow(s, MeshClass(bm.Mesh))[0]++;
            int nTris = 0;

            foreach (var t in index.Query(q))
            {
                if ((t.Flags & 0x08) != 0) continue;   // Deleted triangle — not walkable

                if (st.Kind == 0) { nTris++; continue; }

                // NARROW PHASE. The broad box only says the navmesh triangle is somewhere in this
                // object's volume; the narrow phase decides whether that means blocked.
                if (!columnsTried) { columns = BuildColumns(g.Soup!, eff.Position, eff.Rotation, eff.Scale); columnsTried = true; }
                if (columns is null) { nTris++; continue; }

                var nav = index.NavAt(t.NavIndex);
                var tri = nav.Triangles[t.TriangleIndex];
                var va = nav.Vertices[tri.A]; var vb = nav.Vertices[tri.B]; var vc = nav.Vertices[tri.C];

                if (st.Kind == 1)
                {
                    int inside = 0;
                    foreach (var v in new[] { va, vb, vc })
                        if (columns.Classify(new System.Numerics.Vector3(v.X, v.Y, v.Z)) == ColumnPosition.Inside) inside++;
                    if (inside >= st.MinInside) nTris++;
                }
                else
                {
                    // stopAfter:1 — the question is "any crossing at all", so the scan can quit on the first.
                    int x = columns.CountSurfaceCrossings(
                        new System.Numerics.Vector3(va.X, va.Y, va.Z),
                        new System.Numerics.Vector3(vb.X, vb.Y, vb.Z),
                        new System.Numerics.Vector3(vc.X, vc.Y, vc.Z),
                        st.Clearance, bandTop, st.TriInset, st.MaxUpFacing, 0.5f, stopAfter: 1);
                    if (x < 0) { unjudgeable[s]++; continue; }   // inset consumed it: NOT the same as clear
                    if (x == 0) continue;
                    if (st.Kind == 2) { nTris++; continue; }

                    // SEVER: material is in the column — but does it CUT the triangle, or just stand on it?
                    var sev = columns.MeasureSeverance(
                        new System.Numerics.Vector3(va.X, va.Y, va.Z),
                        new System.Numerics.Vector3(vb.X, vb.Y, vb.Z),
                        new System.Numerics.Vector3(vc.X, vc.Y, vc.Z),
                        st.Clearance, bandTop, st.TriInset, st.Step);
                    if (!sev.Judged) unjudgeable[s]++;
                    else if (sev.Severed && sev.SupportedFraction < st.MaxSupport) nTris++;
                }
            }

            if (nTris > 0)
            {
                flagged[s]++;
                TypeRow(s, bm.Type)[1]++;
                byPlace[s, interiorBucket, 1]++;
                if (interiorBucket == 0) ClassRow(s, MeshClass(bm.Mesh))[1]++;
                if (worstOf[s] is { } bag && bag.Count < 400000)
                    bag.Add((cp.CellEditorId ?? cp.Cell.ToString(), bm.Mesh, bm.Type, nTris, r.FormKey));
                if (st.Name.StartsWith("CROSS   clr32")) crossHit = true;
            }
        }

        // ---- the route rule, for every reference whose solid entered an actor column (Doors set aside: passable by design) ----
        // A MoveableStatic is pushed aside, not a blocker (Part C's urn) — set aside like a Door, counted.
        if (crossHit && columns is not null && bm.Type == "Door") routeDoors++;
        else if (crossHit && columns is not null && bm.Type == "MoveableStatic") routeMovable++;
        else if (crossHit && columns is not null)
        {
            routeMeasured++;
            gnodes ??= NavGraph.Build(index).Nodes;
            var facts = RouteConnectivity.Measure(index, columns, world, RouteMargin, RouteStep, ActorBand.PhysicalStep, physicalTop, Ladder, graph: gnodes, floorAt: floor.FloorAt, isStairsMaterial: DetectorModel.IsStairsMaterial);
            var v64 = DetectorModel.RouteBlockage(facts.Judged, facts.Touched, facts.MaxIntactRung, facts.MaxIntactRungWithout, Ladder, 64f);
            var v128 = DetectorModel.RouteBlockage(facts.Judged, facts.Touched, facts.MaxIntactRung, facts.MaxIntactRungWithout, Ladder, 128f);
            int col = v64.Verdict switch { RouteVerdict.Blocked => 0, RouteVerdict.Narrowed => 1, RouteVerdict.Clipped => v128.Verdict == RouteVerdict.Narrowed ? 2 : 3, RouteVerdict.None => 4, _ => 5 };
            string cls = cp.Interior ? "(interior)" : MeshClass(bm.Mesh);
            var rowC = RouteRow(routeByClass, cls); rowC[col]++; rowC[6]++;
            var rowT = RouteRow(routeByType, bm.Type); rowT[col]++; rowT[6]++;
            if (v64.Verdict == RouteVerdict.Blocked && routeBlocked.Count < 20000)
                routeBlocked.Add((cp.CellEditorId ?? cp.Cell.ToString(), bm.Mesh, bm.Type, r.FormKey, v64.Why));

            var line = facts.Line;
            var vc = DetectorModel.RouteCut(facts.Judged, facts.Touched, line?.Found ?? false, line?.Why ?? "", line?.Cut ?? false, line?.Passage ?? float.NaN, cutRise: line?.CutRise ?? float.NaN, cutFloat: line?.CutFloat ?? float.NaN, cutOnStairs: line?.CutOnStairs ?? false);
            int ccol = vc.Verdict switch { RouteCutVerdict.Blocked => 0, RouteCutVerdict.LikelyBlocked => 1, RouteCutVerdict.Clipped => 2, RouteCutVerdict.NoLine => 3, RouteCutVerdict.None => 4, _ => 5 };
            var cC = RouteRow(cutByClass, cls); cC[ccol]++; cC[6]++;
            var cT = RouteRow(cutByType, bm.Type); cT[ccol]++; cT[6]++;
            if (eff.Touched)
            {
                cutBos[ccol]++; cutBos[6]++;
                if (cutBosRows.Count < 4000) cutBosRows.Add((cp.CellEditorId ?? cp.Cell.ToString(), bm.Mesh, bm.Type, r.FormKey, vc.Verdict, vc.Why + " {" + BosHook.Describe(eff.Resolution!) + "}"));
            }
            cutCross.TryGetValue((v64.Verdict, vc.Verdict), out var xt); cutCross[(v64.Verdict, vc.Verdict)] = xt + 1;
            if (vc.Verdict == RouteCutVerdict.NoLine && line is not null) { cutNoLine.TryGetValue(line.Status, out var nl); cutNoLine[line.Status] = nl + 1; }
            if (vc.Verdict == RouteCutVerdict.Blocked && cutBlocked.Count < 20000)
                cutBlocked.Add((cp.CellEditorId ?? cp.Cell.ToString(), bm.Mesh, bm.Type, r.FormKey, vc.Why));
            else if (vc.Verdict == RouteCutVerdict.LikelyBlocked && cutLikely.Count < 40000)
                cutLikely.Add((cp.CellEditorId ?? cp.Cell.ToString(), bm.Mesh, bm.Type, r.FormKey, vc.Why));
        }
    }

    floorTally.Add(floor);
    if (cells % 1000 == 0)
        Console.WriteLine($"   ... {cells:N0} cells, {totalRefs:N0} refs, {measured:N0} meshes measured, {sw.Elapsed.TotalSeconds:0}s");
}

Console.WriteLine($"\n==== VANILLA FALSE-POSITIVE FLOOR ====");
Console.WriteLine($"cells with navmesh + refs : {cells:N0}");
Console.WriteLine($"navmesh triangles indexed : {navTris:N0}   (malformed vertex index: {malformedTotal:N0})");
Console.WriteLine($"placed refs seen          : {totalRefs:N0}");
Console.WriteLine($"  skipped, initially disabled/deleted : {skippedDisabled:N0}");
Console.WriteLine($"  skipped, no usable geometry         : {skippedNoGeo:N0}");
Console.WriteLine($"  skipped, no actor-solid collision   : {skippedNoSolid:N0}   (collision mode only: the object blocks nothing)");
Console.WriteLine($"actor band                : cells with placed actors {cellsWithActors:N0}; top {(bandMin == float.MaxValue ? 0 : bandMin):0}..{bandMax:0} u; fallback {fallbackSrc}");
Console.WriteLine($"   band top histogram (4 u bins, cells): {string.Join(", ", bandHist.Select(kv => $"{kv.Key}:{kv.Value:N0}"))}");
Console.WriteLine($"geometry source           : {geometrySource}");
if (geometrySource == "collision")
{
    Console.WriteLine($"   {"class",-16} {"on collision",14} {"render fallback",16} {"not solid, skipped",20}");
    foreach (var kv in bySource.OrderByDescending(kv => kv.Value[0] + kv.Value[1] + kv.Value[2]))
        Console.WriteLine($"   {kv.Key,-16} {kv.Value[0],14:N0} {kv.Value[1],16:N0} {kv.Value[2],20:N0}");
}
Console.WriteLine($"distinct meshes measured  : {measured:N0}   (unreadable: {measureFail:N0})   cached triangles: {cachedTris:N0}");
Console.WriteLine($"elapsed                   : {sw.Elapsed.TotalSeconds:0.0}s");
Console.WriteLine("   " + floorTally.Line());
if (bosOn) foreach (var l in bosRep.Summary()) Console.WriteLine("   " + l);
Console.WriteLine();

Console.WriteLine($"   {"setting",-38} {"refs tested",12} {"flagged",10} {"rate",9} {"unjudgeable tri",16}");
for (int s = 0; s < settings.Length; s++)
{
    double rate = refsTested[s] == 0 ? 0 : 100.0 * flagged[s] / refsTested[s];
    Console.WriteLine($"   {settings[s].Name,-38} {refsTested[s],12:N0} {flagged[s],10:N0} {rate,8:0.00}% {unjudgeable[s],16:N0}");
}

Console.WriteLine();
Console.WriteLine($"   ---- the floor is TWO populations: interior vs exterior ----");
Console.WriteLine($"   {"setting",-34} {"interior",22} {"exterior",22}");
for (int s = 0; s < settings.Length; s++)
{
    double ri = byPlace[s, 1, 0] == 0 ? 0 : 100.0 * byPlace[s, 1, 1] / byPlace[s, 1, 0];
    double re = byPlace[s, 0, 0] == 0 ? 0 : 100.0 * byPlace[s, 0, 1] / byPlace[s, 0, 0];
    Console.WriteLine($"   {settings[s].Name,-34} {byPlace[s, 1, 1],9:N0}/{byPlace[s, 1, 0],-9:N0}{ri,6:0.00}% {byPlace[s, 0, 1],9:N0}/{byPlace[s, 0, 0],-9:N0}{re,6:0.00}%");
}

foreach (int s in new[] { 3, 9, 13 })
{
    Console.WriteLine();
    Console.WriteLine($"   ---- {settings[s].Name}: EXTERIOR floor by mesh class ----");
    foreach (var kv in byClass[s].Where(kv => kv.Value[0] >= 500).OrderByDescending(kv => kv.Value[0]).Take(10))
        Console.WriteLine($"   {kv.Key,-16} tested {kv.Value[0],9:N0}  flagged {kv.Value[1],8:N0}  {(kv.Value[0] == 0 ? 0 : 100.0 * kv.Value[1] / kv.Value[0]),7:0.00}%");
}

for (int s = 0; s < settings.Length; s++)
{
    if (settings[s].Kind == 0) continue;
    Console.WriteLine();
    Console.WriteLine($"   ---- {settings[s].Name}: vanilla floor BY RECORD TYPE ----");
    foreach (var kv in byType[s].OrderByDescending(kv => kv.Value[1]).Take(8))
        Console.WriteLine($"   {kv.Key,-16} tested {kv.Value[0],9:N0}  flagged {kv.Value[1],8:N0}  {(kv.Value[0] == 0 ? 0 : 100.0 * kv.Value[1] / kv.Value[0]),7:0.00}%");
    long statT = byType[s].Where(kv => kv.Key is "Static" or "MoveableStatic" or "Door" or "Furniture" or "Container" or "Activator").Sum(kv => kv.Value[0]);
    long statF = byType[s].Where(kv => kv.Key is "Static" or "MoveableStatic" or "Door" or "Furniture" or "Container" or "Activator").Sum(kv => kv.Value[1]);
    Console.WriteLine($"   {"[built forms]",-16} tested {statT,9:N0}  flagged {statF,8:N0}  {(statT == 0 ? 0 : 100.0 * statF / statT),7:0.00}%   <- excludes Tree/Flora");
}

for (int s = 0; s < settings.Length; s++)
{
    if (worstOf[s] is not { Count: > 0 } bag) continue;
    Console.WriteLine();
    Console.WriteLine($"   ---- {settings[s].Name}: what it still flags on VANILLA (top meshes) ----");
    foreach (var grp in bag.GroupBy(w => w.Mesh, StringComparer.OrdinalIgnoreCase)
                           .OrderByDescending(grp => grp.Count()).Take(12))
        Console.WriteLine($"   {grp.Count(),7:N0}  {grp.First().Type,-14} {grp.Key}");
}

Console.WriteLine();
Console.WriteLine($"   ---- the ROUTE rule (Part B) on the vanilla floor: refs whose solid entered an actor column (CROSS clr32), with vs without ----");
Console.WriteLine($"   ladder {string.Join("/", Ladder.Select(l => l.ToString("0")))} u, margin {RouteMargin:0}, step {RouteStep:0}; measured {routeMeasured:N0}; doors set aside as passable by design {routeDoors:N0}");
Console.WriteLine($"   {"class",-16} {"measured",9} {"BLOCKED",9} {"rate",8} {"NARROW<64",10} {"NARROW<128",11} {"CLIPPED",9} {"none",7} {"unjudged",9}");
foreach (var kv in routeByClass.OrderByDescending(kv => kv.Value[6]))
    Console.WriteLine($"   {kv.Key,-16} {kv.Value[6],9:N0} {kv.Value[0],9:N0} {(kv.Value[6] == 0 ? 0 : 100.0 * kv.Value[0] / kv.Value[6]),7:0.00}% {kv.Value[1],10:N0} {kv.Value[2],11:N0} {kv.Value[3],9:N0} {kv.Value[4],7:N0} {kv.Value[5],9:N0}");
Console.WriteLine($"   {"record type",-16} {"measured",9} {"BLOCKED",9} {"rate",8} {"NARROW<64",10} {"NARROW<128",11} {"CLIPPED",9} {"none",7} {"unjudged",9}");
foreach (var kv in routeByType.OrderByDescending(kv => kv.Value[6]).Take(10))
    Console.WriteLine($"   {kv.Key,-16} {kv.Value[6],9:N0} {kv.Value[0],9:N0} {(kv.Value[6] == 0 ? 0 : 100.0 * kv.Value[0] / kv.Value[6]),7:0.00}% {kv.Value[1],10:N0} {kv.Value[2],11:N0} {kv.Value[3],9:N0} {kv.Value[4],7:N0} {kv.Value[5],9:N0}");
if (routeBlocked.Count > 0)
{
    Console.WriteLine($"\n   ---- BLOCKED on vanilla: top meshes (the same-defect control candidates) ----");
    foreach (var grp in routeBlocked.GroupBy(b => b.Mesh, StringComparer.OrdinalIgnoreCase).OrderByDescending(g => g.Count()).Take(15))
        Console.WriteLine($"   {grp.Count(),6:N0}  {grp.First().Type,-14} {grp.Key}");
}

Console.WriteLine();
Console.WriteLine($"   ---- the ROUTE-CUT rule (4.7's closing step) on the vanilla floor: the corridor's path line, cut at capsule 22 u ----");
Console.WriteLine($"   band above the feet on the physical floor (Q24): bottom {ActorBand.PhysicalStep:0.#} u, top {(controllerBand ? $"derived × {ActorBand.ControllerFactor} + {ActorBand.StationOffset:0.#} (the controller's, Part C)" : "derived, nominal")}; passage boundary 48 u; step band to {ActorBand.PhysicalStepBandTop:0.#} u and a float of {ActorBand.FloatWall:0} u or more named in the verdict (Q26); measured {routeMeasured:N0}; doors set aside {routeDoors:N0}; MoveableStatics set aside (pushed aside) {routeMovable:N0}");
Console.WriteLine($"   NoLine by reason: {string.Join(", ", cutNoLine.OrderByDescending(kv => kv.Value).Select(kv => $"{kv.Key} {kv.Value:N0}"))}");
Console.WriteLine($"   {"class",-16} {"measured",9} {"BLOCKED",9} {"rate",8} {"LIKELY",9} {"rate",8} {"CLIPPED",9} {"NoLine",9} {"none",7} {"unjudged",9}");
foreach (var kv in cutByClass.OrderByDescending(kv => kv.Value[6]))
    Console.WriteLine($"   {kv.Key,-16} {kv.Value[6],9:N0} {kv.Value[0],9:N0} {(kv.Value[6] == 0 ? 0 : 100.0 * kv.Value[0] / kv.Value[6]),7:0.00}% {kv.Value[1],9:N0} {(kv.Value[6] == 0 ? 0 : 100.0 * kv.Value[1] / kv.Value[6]),7:0.00}% {kv.Value[2],9:N0} {kv.Value[3],9:N0} {kv.Value[4],7:N0} {kv.Value[5],9:N0}");
Console.WriteLine($"   {"record type",-16} {"measured",9} {"BLOCKED",9} {"rate",8} {"LIKELY",9} {"rate",8} {"CLIPPED",9} {"NoLine",9} {"none",7} {"unjudged",9}");
foreach (var kv in cutByType.OrderByDescending(kv => kv.Value[6]).Take(10))
    Console.WriteLine($"   {kv.Key,-16} {kv.Value[6],9:N0} {kv.Value[0],9:N0} {(kv.Value[6] == 0 ? 0 : 100.0 * kv.Value[0] / kv.Value[6]),7:0.00}% {kv.Value[1],9:N0} {(kv.Value[6] == 0 ? 0 : 100.0 * kv.Value[1] / kv.Value[6]),7:0.00}% {kv.Value[2],9:N0} {kv.Value[3],9:N0} {kv.Value[4],7:N0} {kv.Value[5],9:N0}");
if (bosOn)
{
    Console.WriteLine($"\n   ---- of which the BOS layer swapped or moved the object: NOT floor noise — the swap layer's own rows ----");
    Console.WriteLine($"   {"(BOS-touched)",-16} {cutBos[6],9:N0} {cutBos[0],9:N0} {(cutBos[6] == 0 ? 0 : 100.0 * cutBos[0] / cutBos[6]),7:0.00}% {cutBos[1],9:N0} {(cutBos[6] == 0 ? 0 : 100.0 * cutBos[1] / cutBos[6]),7:0.00}% {cutBos[2],9:N0} {cutBos[3],9:N0} {cutBos[4],7:N0} {cutBos[5],9:N0}");
    foreach (var b in cutBosRows.Where(b => b.V is RouteCutVerdict.Blocked or RouteCutVerdict.LikelyBlocked).OrderBy(b => b.V == RouteCutVerdict.Blocked ? 0 : 1).ThenBy(b => b.Cell).Take(30))
        Console.WriteLine($"   {b.V,-14} {b.Cell,-26} {b.Type,-12} {b.Mesh}  {b.Fk}   {b.Why}");
}
Console.WriteLine($"\n   ---- old rule (64 u) × route-cut rule, the same references ----");
Console.WriteLine($"   {"old \\ new",-12} {"Blocked",9} {"Likely",9} {"Clipped",9} {"NoLine",9} {"None",9} {"Unjudg.",9}");
foreach (var o in new[] { RouteVerdict.Blocked, RouteVerdict.Narrowed, RouteVerdict.Clipped, RouteVerdict.None, RouteVerdict.Unjudgeable })
{
    var cols = new[] { RouteCutVerdict.Blocked, RouteCutVerdict.LikelyBlocked, RouteCutVerdict.Clipped, RouteCutVerdict.NoLine, RouteCutVerdict.None, RouteCutVerdict.Unjudgeable }
               .Select(n => cutCross.TryGetValue((o, n), out var c) ? c : 0).ToArray();
    if (cols.Sum() == 0) continue;
    Console.WriteLine($"   {o,-12} {cols[0],9:N0} {cols[1],9:N0} {cols[2],9:N0} {cols[3],9:N0} {cols[4],9:N0} {cols[5],9:N0}");
}
if (cutBlocked.Count > 0)
{
    Console.WriteLine($"\n   ---- route-cut BLOCKED on vanilla: top meshes ----");
    foreach (var grp in cutBlocked.GroupBy(b => b.Mesh, StringComparer.OrdinalIgnoreCase).OrderByDescending(g => g.Count()).Take(15))
        Console.WriteLine($"   {grp.Count(),6:N0}  {grp.First().Type,-14} {grp.Key}");
}
if (cutLikely.Count > 0)
{
    Console.WriteLine($"\n   ---- route-cut LIKELY-BLOCKED on vanilla: top meshes ----");
    foreach (var grp in cutLikely.GroupBy(b => b.Mesh, StringComparer.OrdinalIgnoreCase).OrderByDescending(g => g.Count()).Take(15))
        Console.WriteLine($"   {grp.Count(),6:N0}  {grp.First().Type,-14} {grp.Key}");
}

if (dumpDir is not null)
{
    Directory.CreateDirectory(dumpDir);
    using (var bw = new StreamWriter(Path.Combine(dumpDir, "route-blocked.tsv")))
    {
        bw.WriteLine("key\tcell\ttype\tmesh\twhy");
        foreach (var b in routeBlocked) bw.WriteLine($"{b.Fk}\t{b.Cell}\t{b.Type}\t{b.Mesh}\t{b.Why}");
    }
    using (var cw = new StreamWriter(Path.Combine(dumpDir, "route-cut-blocked.tsv")))
    {
        cw.WriteLine("key\tcell\ttype\tmesh\twhy");
        foreach (var b in cutBlocked) cw.WriteLine($"{b.Fk}\t{b.Cell}\t{b.Type}\t{b.Mesh}\t{b.Why}");
    }
    using (var lw = new StreamWriter(Path.Combine(dumpDir, "route-cut-likely.tsv")))
    {
        lw.WriteLine("key\tcell\ttype\tmesh\twhy");
        foreach (var b in cutLikely) lw.WriteLine($"{b.Fk}\t{b.Cell}\t{b.Type}\t{b.Mesh}\t{b.Why}");
    }
    for (int s = 0; s < settings.Length; s++)
    {
        if (worstOf[s] is not { } bag) continue;
        var safe = new string(settings[s].Name.Select(ch => char.IsLetterOrDigit(ch) ? ch : '_').ToArray());
        using var dw = new StreamWriter(Path.Combine(dumpDir, $"{s:00}-{safe}.tsv"));
        dw.WriteLine("key\tcell\ttype\tmesh\ttris");
        foreach (var w in bag) dw.WriteLine($"{w.Fk}\t{w.Cell}\t{w.Type}\t{w.Mesh}\t{w.Tris}");
    }
    Console.WriteLine($"\ndumped flagged refs per narrow rule -> {dumpDir}");
}

return 0;

record Setting(string Name, int Kind, float Height, float BoxInset, bool CollisionOnly,
               int MinInside, float Clearance, float TriInset, float MaxUpFacing, float Step, float MaxSupport);

record Geo(Aabb Box, bool HasCollision, bool Ok, TriangleSoup? Soup, string Source);
