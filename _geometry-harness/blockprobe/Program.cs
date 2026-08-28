using System.Diagnostics;
using System.Numerics;
using HousecarlCore;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Skyrim;

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
// usage: blockprobe <TargetPlugin.esp> [--exclude Other.esp ...] [--list N]

string Instance = HarnessTarget.Require(HarnessTarget.Instance, "MO2 instance directory", "instance");
string GamePath = HarnessTarget.Require(HarnessTarget.Game, "game directory", "game");
string profileDir = Path.Combine(Instance, "profiles", HarnessTarget.Require(HarnessTarget.Profile, "MO2 profile name", "profile"));
string modsDir = Path.Combine(Instance, "mods");
string overwriteDir = Path.Combine(Instance, "overwrite");
string dataDir = Path.Combine(GamePath, "Data");

if (args.Length == 0) { Console.Error.WriteLine("usage: blockprobe <TargetPlugin.esp> [--exclude Other.esp ...] [--list N]"); return 1; }
string target = args[0];
var exclude = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
int listN = 25;
for (int i = 1; i < args.Length; i++)
{
    if (args[i] == "--exclude" && i + 1 < args.Length) exclude.Add(args[++i]);
    else if (args[i] == "--list" && i + 1 < args.Length) listN = int.Parse(args[++i]);
}

var sw = Stopwatch.StartNew();
var comp = Mo2LoadOrder.ReadComposition(profileDir);
var arch = ArchiveDiscovery.Discover(profileDir, modsDir, dataDir, overwriteDir, GamePath);
using var assets = AssetResolver.Build(overwriteDir, modsDir, dataDir, comp.EnabledMods, arch.Archives);
var order = Mo2LoadOrder.Build(profileDir, modsDir, dataDir, overwriteDir);

var paths = order.OrderedPaths.Where(p => !exclude.Contains(Path.GetFileName(p))).ToList();
string? targetPath = paths.FirstOrDefault(p => string.Equals(Path.GetFileName(p), target, StringComparison.OrdinalIgnoreCase));
if (targetPath is null) { Console.Error.WriteLine($"'{target}' is not in the active load order."); return 1; }

Console.WriteLine($"load order: {paths.Count} plugins" + (exclude.Count > 0 ? $"  (EXCLUDING {string.Join(", ", exclude)})" : ""));
Console.WriteLine($"target    : {target}   position {paths.FindIndex(p => p == targetPath) + 1}/{paths.Count}\n");

// ---------- 1. which cells does the target touch? ----------
var targetCells = new Dictionary<FormKey, CellPlacements>();
using (var tm = SkyrimMod.CreateFromBinaryOverlay(targetPath, SkyrimRelease.SkyrimSE))
    foreach (var cp in PlacementExtract.FromMod(tm))
        targetCells[cp.Cell] = cp;

var targetModKey = ModKey.FromFileName(target);
int targetNewRefs = targetCells.Values.Sum(c => c.Refs.Count(r => r.FormKey.ModKey == targetModKey));
int targetOverrides = targetCells.Values.Sum(c => c.Refs.Count(r => r.FormKey.ModKey != targetModKey));
Console.WriteLine($"target touches {targetCells.Count:N0} cells: "
                + $"{targetNewRefs:N0} refs it INTRODUCES, {targetOverrides:N0} it overrides, "
                + $"{targetCells.Values.Sum(c => c.Navmeshes.Count):N0} navmeshes it supplies");

// ---------- 2. resolve the WINNING state for exactly those cells ----------
// Diff-based, per the charter: only the cells the target touches are ever loaded.
sw.Restart();
var winRef = new Dictionary<FormKey, PlacedRef>();                       // ref FormKey -> winning placement
var winRefCell = new Dictionary<FormKey, FormKey>();                     // ref FormKey -> winning cell
var winNav = new Dictionary<FormKey, (NavmeshGeometry Nav, string Plugin, FormKey Cell)>();
var baseMesh = new Dictionary<FormKey, (string Mesh, string Type)>();
var cellMeta = new Dictionary<FormKey, CellPlacements>();

foreach (var p in paths)
{
    ISkyrimModGetter m;
    try { m = SkyrimMod.CreateFromBinaryOverlay(p, SkyrimRelease.SkyrimSE); } catch { continue; }
    using (m as IDisposable)
    {
        string name = Path.GetFileName(p);
        foreach (var rec in m.EnumerateMajorRecords())
            if (rec is IModeledGetter mo && mo.Model?.File is { } f && !string.IsNullOrWhiteSpace(f.GivenPath))
                baseMesh[rec.FormKey] = (f.GivenPath, rec.GetType().Name.Replace("BinaryOverlay", ""));

        foreach (var cp in PlacementExtract.FromMod(m))
        {
            if (!targetCells.ContainsKey(cp.Cell)) continue;
            cellMeta[cp.Cell] = cp;
            foreach (var r in cp.Refs) { winRef[r.FormKey] = r; winRefCell[r.FormKey] = cp.Cell; }
            foreach (var nav in cp.Navmeshes) winNav[nav.FormKey] = (nav, name, cp.Cell);
        }
    }
}
Console.WriteLine($"winning state for those cells: {winRef.Count:N0} refs, {winNav.Count:N0} navmeshes, "
                + $"{baseMesh.Count:N0} base records  ({sw.Elapsed.TotalSeconds:0.0}s)\n");

// ---------- 3. geometry cache ----------
var geoCache = new Dictionary<string, Geo>(StringComparer.OrdinalIgnoreCase);
int measured = 0;
Geo Geometry(string rel)
{
    if (geoCache.TryGetValue(rel, out var hit)) return hit;
    var full = rel.StartsWith("meshes", StringComparison.OrdinalIgnoreCase) ? rel : Path.Combine("meshes", rel);
    var res = assets.ResolveForPlacement(full);
    if (res.Sources.Count == 0) return geoCache[rel] = new Geo(Aabb.Empty, false, false, null);
    var (bytes, _) = AssetResolver.ReadPlacementSource(res.Sources[0]);
    if (bytes is null) return geoCache[rel] = new Geo(Aabb.Empty, false, false, null);
    var o = NifGeometry.Measure(bytes, includeTriangles: true);
    if (o.Model is null) return geoCache[rel] = new Geo(Aabb.Empty, false, false, null);
    measured++;
    var soup = o.Model.Soup is { TriangleCount: > 0 and <= 200_000 } s ? s : null;
    return geoCache[rel] = new Geo(o.Model.Bounds, o.Model.HasCollision, true, soup);
}

// ---------- 4. test the target's OWN references against the winning navmesh ----------
// Settings match navprobe's calibrated NARROW column test exactly, so the 0.4% vanilla floor
// is the directly comparable baseline.
const float ActorHeight = 128f;
const float Clearance = 32f;      // navprobe's calibrated step height
const float TriInset = 12f;       // ...and its edge-graze forgiveness

var navByCell = winNav.Values.GroupBy(v => v.Cell)
                             .ToDictionary(g => g.Key, g => g.ToList());

long tested = 0, blocked = 0, noGeo = 0, skipped = 0;
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

    foreach (var tref in tcell.Refs)
    {
        // Test the WINNING placement — if a later patch moved or removed this reference,
        // the target's own copy is not what the game uses.
        if (!winRef.TryGetValue(tref.FormKey, out var r)) { skipped++; continue; }
        if (winRefCell.TryGetValue(tref.FormKey, out var wc) && wc != cellKey) { skipped++; continue; }
        if (r.InitiallyDisabled || r.Deleted) { skipped++; continue; }
        if (r.Base is not { } bk || !baseMesh.TryGetValue(bk, out var bm)) { noGeo++; continue; }

        var g = Geometry(bm.Mesh);
        if (!g.Ok || g.Box.IsEmpty || g.Soup is null || !g.HasCollision) { noGeo++; continue; }

        var world = SpatialOverlap.ToWorld(g.Box, r.Position, r.Rotation, r.Scale);
        var q = SpatialOverlap.GrowDown(world, ActorHeight);

        tested++;
        if (targetGaveNav) testedWithNav++; else testedNoNav++;

        MeshColumnIndex? columns = null; bool built = false;
        int nTris = 0, broadTris = 0, partial = 0, nTrisAny = 0, nCross = 0, nSever = 0, nSeverTight = 0;
        foreach (var t in index.Query(q))
        {
            if ((t.Flags & 0x08) != 0) continue;               // Deleted triangle
            broadTris++;
            if (!built) { columns = BuildColumns(g.Soup, r.Position, r.Rotation, r.Scale); built = true; }
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

Console.WriteLine($"==== {target} — navmesh blockage ====");
Console.WriteLine($"references tested : {tested:N0}   (no usable geometry {noGeo:N0}, skipped {skipped:N0})");
Console.WriteLine($"distinct meshes   : {measured:N0}");
Console.WriteLine($"elapsed           : {sw.Elapsed.TotalSeconds:0.0}s\n");

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
    var m = SpatialOverlap.EulerZyx(rot);
    var t = new Vector3(pos.X, pos.Y, pos.Z);
    var w = new Vector3[soup.Vertices.Length];
    for (int i = 0; i < w.Length; i++) w[i] = Vector3.Transform(soup.Vertices[i] * scale, m) + t;
    return MeshColumnIndex.Build(w, soup.Indices);
}

record Geo(Aabb Box, bool HasCollision, bool Ok, TriangleSoup? Soup);
