using System.Diagnostics;
using System.Numerics;
using HousecarlCore;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Skyrim;

// EULER ORDER — settling a question the charter has carried as "evidence, not proof" since day one.
//
// Skyrim stores a placed reference's rotation as three angles and the record does not say which
// order the engine composes them in. `SpatialOverlap.ToWorld` picked Rz·Ry·Rx (X applied first) on
// documentation, and the only support for it at the time was that the vanilla false-positive floor
// came out low. That is an argument about ONE order; it never compared the other five.
//
// SETTLED SINCE (2026-09-01): the engine composes Rx(-x)·Ry(-y)·Rz(-z) — `SpatialOverlap.RefrRotation`,
// derived from CommonLibVR and confirmed in game — and `ToWorld` now uses it. That matrix is the
// TRANSPOSE of the old one: the angles are negated as well as re-ordered, and this sweep never varied
// the sign (it is common to all six arms), which is why the six orders tied here and the question was
// settled by derivation and a render instead. The sweep is kept as a tool; its "XYZ" arm is the OLD
// composition, and none of its six arms is the engine's.
//
// The measurement. Vanilla navmesh was authored against vanilla geometry, so under the correct
// composition the navmesh should sit ON surfaces and rarely INSIDE them. Under a wrong one, every
// rotated object lands askew and the navmesh starts intersecting material that is not really there.
// So: run the calibrated containment test under all six orders and read off which is quietest.
//
// The amplifier that makes this decisive. When only one rotation component is non-zero, all six
// orders give the SAME matrix — most Skyrim clutter is Z-only, so a whole-corpus sweep would
// average six identical answers and separate nothing. Restricting to references with two or three
// non-zero components measures only the population where the orders actually disagree.
//
// usage: eulerprobe [maxCells]

string Instance = HarnessTarget.Require(HarnessTarget.Instance, "MO2 instance directory", "instance");
string GamePath = HarnessTarget.Require(HarnessTarget.Game, "game directory", "game");
string profileDir = Path.Combine(Instance, "profiles", HarnessTarget.Require(HarnessTarget.Profile, "MO2 profile name", "profile"));
string modsDir = Path.Combine(Instance, "mods");
string overwriteDir = Path.Combine(Instance, "overwrite");
string dataDir = Path.Combine(GamePath, "Data");
string vanilla = Path.Combine(dataDir, "Skyrim.esm");

int maxCells = args.Length > 0 && int.TryParse(args[0], out var mc) ? mc : int.MaxValue;

// Self-check first: the general composer must reproduce the hand-written matrix the whole project
// has been using, or the comparison below is between two different things.
{
    var r = new Vec3(0.3f, -1.1f, 2.4f);
    var a = SpatialOverlap.EulerZyx(r);
    var b = SpatialOverlap.Euler(r, SpatialOverlap.EulerOrder.Xyz);
    float worst = 0;
    for (int i = 0; i < 3; i++)
        for (int j = 0; j < 3; j++)
            worst = MathF.Max(worst, MathF.Abs(Get(a, i, j) - Get(b, i, j)));
    Console.WriteLine($"self-check: EulerZyx vs Euler(.., Xyz) max element difference = {worst:0.000000}"
                    + (worst < 1e-5f ? "   OK — same matrix" : "   *** MISMATCH ***"));
    if (worst >= 1e-5f) return 1;
    static float Get(Matrix4x4 m, int i, int j) => i switch
    {
        0 => j == 0 ? m.M11 : j == 1 ? m.M12 : m.M13,
        1 => j == 0 ? m.M21 : j == 1 ? m.M22 : m.M23,
        _ => j == 0 ? m.M31 : j == 1 ? m.M32 : m.M33,
    };
}

var sw = Stopwatch.StartNew();
var comp = Mo2LoadOrder.ReadComposition(profileDir);
var arch = ArchiveDiscovery.Discover(profileDir, modsDir, dataDir, overwriteDir, GamePath);
using var assets = AssetResolver.Build(overwriteDir, modsDir, dataDir, comp.EnabledMods, arch.Archives);

using var mod = SkyrimMod.CreateFromBinaryOverlay(vanilla, SkyrimRelease.SkyrimSE);
var baseMesh = new Dictionary<FormKey, (string Mesh, string Type)>();
foreach (var rec in mod.EnumerateMajorRecords())
    if (rec is IModeledGetter m && m.Model?.File is { } f && !string.IsNullOrWhiteSpace(f.GivenPath))
        baseMesh[rec.FormKey] = (f.GivenPath, rec.GetType().Name.Replace("BinaryOverlay", ""));
Console.WriteLine($"vanilla base records with a mesh: {baseMesh.Count:N0}  ({sw.Elapsed.TotalSeconds:0.0}s)\n");

var geoCache = new Dictionary<string, Geo>(StringComparer.OrdinalIgnoreCase);
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
    var soup = o.Model.Soup is { TriangleCount: > 0 and <= 200_000 } s ? s : null;
    return geoCache[rel] = new Geo(o.Model.Bounds, o.Model.HasCollision, true, soup);
}

static MeshColumnIndex? Columns(TriangleSoup soup, Vec3 pos, Vec3 rot, float scale, SpatialOverlap.EulerOrder order)
{
    if (soup.TriangleCount == 0) return null;
    var m = SpatialOverlap.Euler(rot, order);
    var t = new Vector3(pos.X, pos.Y, pos.Z);
    var w = new Vector3[soup.Vertices.Length];
    for (int i = 0; i < w.Length; i++) w[i] = Vector3.Transform(soup.Vertices[i] * scale, m) + t;
    return MeshColumnIndex.Build(w, soup.Indices);
}

// How far apart do the six compositions put this reference's geometry? Uses the model box's eight
// corners, which is where the disagreement is largest and is exactly what the world box is built from.
static float CornerSpread(Aabb model, Vec3 rot, float scale)
{
    if (model.IsEmpty) return 0;
    var orders = Enum.GetValues<SpatialOverlap.EulerOrder>();
    var mats = orders.Select(o => SpatialOverlap.Euler(rot, o)).ToArray();
    float worst = 0;
    for (int i = 0; i < 8; i++)
    {
        var c = new Vector3(
            (i & 1) == 0 ? model.Min.X : model.Max.X,
            (i & 2) == 0 ? model.Min.Y : model.Max.Y,
            (i & 4) == 0 ? model.Min.Z : model.Max.Z) * scale;
        for (int a = 0; a < mats.Length; a++)
            for (int b = a + 1; b < mats.Length; b++)
                worst = MathF.Max(worst, Vector3.Distance(Vector3.Transform(c, mats[a]), Vector3.Transform(c, mats[b])));
    }
    return worst;
}

static Aabb ToWorld(Aabb model, Vec3 pos, Vec3 rot, float scale, SpatialOverlap.EulerOrder order)
{
    if (model.IsEmpty) return Aabb.Empty;
    var r = SpatialOverlap.Euler(rot, order);
    var t = new Vector3(pos.X, pos.Y, pos.Z);
    var box = Aabb.Empty;
    for (int i = 0; i < 8; i++)
    {
        var c = new Vector3(
            (i & 1) == 0 ? model.Min.X : model.Max.X,
            (i & 2) == 0 ? model.Min.Y : model.Max.Y,
            (i & 4) == 0 ? model.Min.Z : model.Max.Z);
        box = box.Add(Vector3.Transform(c * scale, r) + t);
    }
    return box;
}

var orders = Enum.GetValues<SpatialOverlap.EulerOrder>();
var tested = new long[orders.Length];
var flagged = new long[orders.Length];
var buried = new long[orders.Length];        // navmesh vertices classified Inside — the raw signal
var vertsSeen = new long[orders.Length];
// SECOND MEASUREMENT, with real orientation sensitivity. Containment turns out to be nearly blind
// to which order is used — see the result table — so it cannot settle the question on its own.
// This one can: vanilla objects are placed to SIT ON the ground, and the lowest point of a rotated
// body moves a great deal when you recompose its rotation. Navmesh gives the ground height for free.
var groundGap = new List<float>[orders.Length];
for (int i = 0; i < orders.Length; i++) groundGap[i] = new List<float>();

const float ActorHeight = 128f;
long cells = 0, candidates = 0, oneAxis = 0, lowSpread = 0;
double spreadSum = 0;
// Which cells could actually SHOW the difference? Reported so a visual check can be aimed at a
// cell full of multi-axis references instead of one where every order agrees.
var cellSpread = new Dictionary<string, (int Count, float Sum)>();
float minSpread = args.Length > 1 && float.TryParse(args[1], out var ms) ? ms : 64f;

sw.Restart();
foreach (var cp in PlacementExtract.FromMod(mod))
{
    if (cp.Navmeshes.Count == 0 || cp.Refs.Count == 0) continue;
    if (cells >= maxCells) break;
    cells++;

    var index = NavmeshIndex.Build(cp.Navmeshes, out _);
    if (index.TriangleCount == 0) continue;

    // The cell's navmesh as one column-queryable surface: "how high is the floor at (x,y)".
    MeshColumnIndex? ground = null;
    {
        var gv = new List<Vector3>(); var gi = new List<int>();
        foreach (var nav in cp.Navmeshes)
        {
            int b = gv.Count;
            foreach (var v in nav.Vertices) gv.Add(new Vector3(v.X, v.Y, v.Z));
            foreach (var t in nav.Triangles)
            {
                if (t.A < 0 || t.B < 0 || t.C < 0) continue;
                if (t.A >= nav.Vertices.Count || t.B >= nav.Vertices.Count || t.C >= nav.Vertices.Count) continue;
                if ((t.Flags & 0x08) != 0) continue;
                gi.Add(b + t.A); gi.Add(b + t.B); gi.Add(b + t.C);
            }
        }
        if (gi.Count >= 3) ground = MeshColumnIndex.Build(gv.ToArray(), gi.ToArray());
    }

    foreach (var r in cp.Refs)
    {
        if (r.InitiallyDisabled || r.Deleted) continue;

        // THE POPULATION THAT DISCRIMINATES. With fewer than two non-zero angles every order
        // yields the same matrix, so such a reference votes identically six times and only dilutes.
        int nz = (MathF.Abs(r.Rotation.X) > 1e-4f ? 1 : 0)
               + (MathF.Abs(r.Rotation.Y) > 1e-4f ? 1 : 0)
               + (MathF.Abs(r.Rotation.Z) > 1e-4f ? 1 : 0);
        if (nz < 2) { oneAxis++; continue; }

        if (r.Base is not { } bk || !baseMesh.TryGetValue(bk, out var bm)) continue;
        var g = Geometry(bm.Mesh);
        if (!g.Ok || g.Box.IsEmpty || g.Soup is null || !g.HasCollision) continue;

        // SECOND AMPLIFIER. Two non-zero angles is necessary but not sufficient: a rock tilted two
        // degrees on two axes lands in almost the same place under every composition, so it votes
        // six near-identical times and dilutes exactly like a single-axis reference does. Keep only
        // references the orders actually disagree about, measured as the largest displacement of any
        // model corner between any two orders.
        float spread = CornerSpread(g.Box, r.Rotation, r.Scale);
        if (spread < minSpread) { lowSpread++; continue; }
        spreadSum += spread;
        candidates++;
        {
            string cn = cp.CellEditorId ?? cp.Cell.ToString();
            var cur = cellSpread.GetValueOrDefault(cn);
            cellSpread[cn] = (cur.Count + 1, cur.Sum + spread);
        }

        for (int o = 0; o < orders.Length; o++)
        {
            var world = ToWorld(g.Box, r.Position, r.Rotation, r.Scale, orders[o]);
            var q = SpatialOverlap.GrowDown(world, ActorHeight);
            if (q.IsEmpty) continue;
            tested[o]++;

            MeshColumnIndex? cols = null; bool built = false;
            int nTris = 0;
            foreach (var t in index.Query(q))
            {
                if ((t.Flags & 0x08) != 0) continue;
                if (!built) { cols = Columns(g.Soup, r.Position, r.Rotation, r.Scale, orders[o]); built = true; }
                if (cols is null) continue;

                var nav = index.NavAt(t.NavIndex);
                var tri = nav.Triangles[t.TriangleIndex];
                int inside = 0;
                foreach (var vi in new[] { tri.A, tri.B, tri.C })
                {
                    var v = nav.Vertices[vi];
                    vertsSeen[o]++;
                    if (cols.Classify(new Vector3(v.X, v.Y, v.Z)) == ColumnPosition.Inside) { inside++; buried[o]++; }
                }
                if (inside == 3) nTris++;
            }
            if (nTris > 0) flagged[o]++;

            // Where does this body's LOWEST point sit relative to the floor beneath its centre?
            // Vanilla places objects on the ground, so the correct composition should concentrate
            // this near zero and a wrong one should scatter it.
            if (ground is not null)
            {
                var c = world.Center;
                var span = ground.SurfaceSpan(c.X, c.Y);
                if (span.Any) groundGap[o].Add(MathF.Abs(world.Min.Z - span.Hi));
            }
        }
    }

    if (cells % 1000 == 0) Console.WriteLine($"   ... {cells:N0} cells, {candidates:N0} candidates, {sw.Elapsed.TotalSeconds:0}s");
}

Console.WriteLine($"\n==== EULER ORDER, measured ====");
Console.WriteLine($"cells swept                    : {cells:N0}");
Console.WriteLine($"refs with <2 non-zero angles   : {oneAxis:N0}   (skipped — every order agrees on them)");
Console.WriteLine($"refs the orders barely move    : {lowSpread:N0}   (skipped — corner spread < {minSpread:0}u)");
Console.WriteLine($"refs where the orders DIFFER   : {candidates:N0}   (mean corner spread {(candidates == 0 ? 0 : spreadSum / candidates):N0}u)");
Console.WriteLine($"elapsed                        : {sw.Elapsed.TotalSeconds:0.0}s\n");

Console.WriteLine($"   {"order (axis applied first -> last)",-36} {"refs",9} {"flagged",9} {"rate",9} {"verts buried",14} {"vert rate",10}");
for (int o = 0; o < orders.Length; o++)
{
    double rate = tested[o] == 0 ? 0 : 100.0 * flagged[o] / tested[o];
    double vrate = vertsSeen[o] == 0 ? 0 : 100.0 * buried[o] / vertsSeen[o];
    string mark = rate == Enumerable.Range(0, orders.Length)
        .Select(i => tested[i] == 0 ? double.MaxValue : 100.0 * flagged[i] / tested[i]).Min() ? "   <== quietest" : "";
    Console.WriteLine($"   {orders[o].ToString().ToUpperInvariant(),-36} {tested[o],9:N0} {flagged[o],9:N0} {rate,8:0.00}% {buried[o],14:N0} {vrate,9:0.00}%{mark}");
}

Console.WriteLine($"\n   XYZ is the composition SpatialOverlap.ToWorld used until 2026-09-01 (Rz*Ry*Rx, X applied first).");
Console.WriteLine("   ToWorld now composes RefrRotation = Rx(-x)*Ry(-y)*Rz(-z), the engine's matrix — its transpose,");
Console.WriteLine("   which NONE of these six un-negated arms is: the sign is common to all of them, so this sweep");
Console.WriteLine("   could never have found it. A tie here says the containment metric is blind to orientation.");

Console.WriteLine();
Console.WriteLine("   ---- how far an object's BASE sits from the floor under it (orientation-sensitive) ----");
Console.WriteLine($"   {"order",-10} {"samples",10} {"median",10} {"p25",10} {"p75",10}");
for (int o = 0; o < orders.Length; o++)
{
    var gg = groundGap[o];
    if (gg.Count == 0) { Console.WriteLine($"   {orders[o],-10} {0,10}"); continue; }
    gg.Sort();
    Console.WriteLine($"   {orders[o].ToString().ToUpperInvariant(),-10} {gg.Count,10:N0} {gg[gg.Count / 2],10:N1} {gg[gg.Count / 4],10:N1} {gg[gg.Count * 3 / 4],10:N1}");
}

Console.WriteLine();
Console.WriteLine("   ---- cells where a VISUAL check would discriminate (most high-spread refs) ----");
foreach (var kv in cellSpread.OrderByDescending(kv => kv.Value.Count).Take(15))
    Console.WriteLine($"   {kv.Value.Count,5} refs  mean spread {kv.Value.Sum / kv.Value.Count,7:N0}u   {kv.Key}");

return 0;

record Geo(Aabb Box, bool HasCollision, bool Ok, TriangleSoup? Soup);
