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
// usage: navprobe [maxCells] [--dump dir]
//   --dump writes the flagged reference keys per narrow rule, one file each — a diagnostic for comparing two
//   runs ref by ref (which verdicts moved under a transform change), not part of the report.

string Instance = HarnessTarget.Require(HarnessTarget.Instance, "MO2 instance directory", "instance");
string GamePath = HarnessTarget.Require(HarnessTarget.Game, "game directory", "game");
string profileDir = Path.Combine(Instance, "profiles", HarnessTarget.Require(HarnessTarget.Profile, "MO2 profile name", "profile"));
string modsDir = Path.Combine(Instance, "mods");
string overwriteDir = Path.Combine(Instance, "overwrite");
string dataDir = Path.Combine(GamePath, "Data");
string vanilla = Path.Combine(dataDir, "Skyrim.esm");

int maxCells = args.Length > 0 && int.TryParse(args[0], out var mc) ? mc : int.MaxValue;
string? dumpDir = null;
for (int i = 0; i < args.Length; i++) if (args[i] == "--dump" && i + 1 < args.Length) dumpDir = args[++i];

var sw = Stopwatch.StartNew();
var comp = Mo2LoadOrder.ReadComposition(profileDir);
var arch = ArchiveDiscovery.Discover(profileDir, modsDir, dataDir, overwriteDir, GamePath);
using var assets = AssetResolver.Build(overwriteDir, modsDir, dataDir, comp.EnabledMods, arch.Archives);
Console.WriteLine($"assets: {arch.Archives.Count} BSAs indexed in {sw.ElapsedMilliseconds} ms");

// ---- vanilla base records: FormKey -> (mesh path, record type) ----
sw.Restart();
using var mod = SkyrimMod.CreateFromBinaryOverlay(vanilla, SkyrimRelease.SkyrimSE);
var baseMesh = new Dictionary<FormKey, (string Mesh, string Type)>();
foreach (var rec in mod.EnumerateMajorRecords())
    if (rec is IModeledGetter m && m.Model?.File is { } f && !string.IsNullOrWhiteSpace(f.GivenPath))
        baseMesh[rec.FormKey] = (f.GivenPath, rec.GetType().Name.Replace("BinaryOverlay", ""));
Console.WriteLine($"vanilla base records with a mesh: {baseMesh.Count:N0}  ({sw.Elapsed.TotalSeconds:0.0}s)");

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
    if (res.Sources.Count == 0) { measureFail++; return geoCache[rel] = new Geo(Aabb.Empty, false, false, null); }
    var (bytes, _) = AssetResolver.ReadPlacementSource(res.Sources[0]);
    if (bytes is null) { measureFail++; return geoCache[rel] = new Geo(Aabb.Empty, false, false, null); }
    var o = NifGeometry.Measure(bytes, includeTriangles: true);
    if (o.Model is null) { measureFail++; return geoCache[rel] = new Geo(Aabb.Empty, false, false, null); }
    measured++;
    var soup = o.Model.Soup;
    if (soup is not null && soup.TriangleCount > MaxTrisCached) soup = null;
    if (soup is not null) cachedTris += soup.TriangleCount;
    return geoCache[rel] = new Geo(o.Model.Bounds, o.Model.HasCollision, true, soup);
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
    return MeshColumnIndex.Build(w, soup.Indices);
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
long cells = 0, totalRefs = 0, skippedNoGeo = 0, skippedDisabled = 0, navTris = 0, malformedTotal = 0;
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

    foreach (var r in cp.Refs)
    {
        totalRefs++;
        if (r.InitiallyDisabled || r.Deleted) { skippedDisabled++; continue; }
        if (r.Base is not { } bk || !baseMesh.TryGetValue(bk, out var bm)) { skippedNoGeo++; continue; }

        var g = Geometry(bm.Mesh);
        if (!g.Ok || g.Box.IsEmpty) { skippedNoGeo++; continue; }

        var world = SpatialOverlap.ToWorld(g.Box, r.Position, r.Rotation, r.Scale);

        MeshColumnIndex? columns = null;
        bool columnsTried = false;

        for (int s = 0; s < settings.Length; s++)
        {
            var st = settings[s];
            if (st.CollisionOnly && !g.HasCollision) continue;
            if (st.Kind != 0 && g.Soup is null) continue;

            var q = SpatialOverlap.InsetHorizontal(world, st.BoxInset);
            if (q.IsEmpty) continue;
            q = SpatialOverlap.GrowDown(q, st.Height);

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
                if (!columnsTried) { columns = BuildColumns(g.Soup!, r.Position, r.Rotation, r.Scale); columnsTried = true; }
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
                        st.Clearance, st.Height, st.TriInset, st.MaxUpFacing, 0.5f, stopAfter: 1);
                    if (x < 0) { unjudgeable[s]++; continue; }   // inset consumed it: NOT the same as clear
                    if (x == 0) continue;
                    if (st.Kind == 2) { nTris++; continue; }

                    // SEVER: material is in the column — but does it CUT the triangle, or just stand on it?
                    var sev = columns.MeasureSeverance(
                        new System.Numerics.Vector3(va.X, va.Y, va.Z),
                        new System.Numerics.Vector3(vb.X, vb.Y, vb.Z),
                        new System.Numerics.Vector3(vc.X, vc.Y, vc.Z),
                        st.Clearance, st.Height, st.TriInset, st.Step);
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
            }
        }
    }

    if (cells % 1000 == 0)
        Console.WriteLine($"   ... {cells:N0} cells, {totalRefs:N0} refs, {measured:N0} meshes measured, {sw.Elapsed.TotalSeconds:0}s");
}

Console.WriteLine($"\n==== VANILLA FALSE-POSITIVE FLOOR ====");
Console.WriteLine($"cells with navmesh + refs : {cells:N0}");
Console.WriteLine($"navmesh triangles indexed : {navTris:N0}   (malformed vertex index: {malformedTotal:N0})");
Console.WriteLine($"placed refs seen          : {totalRefs:N0}");
Console.WriteLine($"  skipped, initially disabled/deleted : {skippedDisabled:N0}");
Console.WriteLine($"  skipped, no usable geometry         : {skippedNoGeo:N0}");
Console.WriteLine($"distinct meshes measured  : {measured:N0}   (unreadable: {measureFail:N0})   cached triangles: {cachedTris:N0}");
Console.WriteLine($"elapsed                   : {sw.Elapsed.TotalSeconds:0.0}s\n");

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

if (dumpDir is not null)
{
    Directory.CreateDirectory(dumpDir);
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

record Setting(string Name, int Kind, float Height, float BoxInset, bool CollisionOnly,
               int MinInside, float Clearance, float TriInset, float MaxUpFacing, float Step, float MaxSupport);

record Geo(Aabb Box, bool HasCollision, bool Ok, TriangleSoup? Soup);
