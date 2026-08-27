using System.Diagnostics;
using System.Numerics;
using HarnessShared;
using HousecarlCore;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Skyrim;

// MILESTONE 5 — CROSS-PLUGIN OBJECT OVERLAP. The geometry charter's own motivating example: two
// mods drop a wall and a tree into the same cubic metre, SSEEdit reports "no conflict", and the
// player finds out by walking into it.
//
// Why this survives the ruling-out that killed milestone 4: the absolute question ("does this
// object overlap something?") drowns in the authored norm — clutter interpenetrates constantly.
// The CROSS-PLUGIN question does not have that excuse: an object placed by mod A intersecting an
// object placed by mod B, where NEITHER declares a dependency on the other, is a coincidence of
// two authors who never saw each other's work. The floor still exists (two mods CAN legitimately
// meet — a city overhaul and a lantern mod decorating the same street) and is MEASURED here, not
// assumed: the same-source overlap rate is the "authored deliberately" baseline every cross-plugin
// number is read against, split by interior/exterior.
//
// What "responsible" means: the plugin that last SET a reference's placement — the introducer,
// or the latest override that actually moved it. An override that forwards a position unchanged
// takes no responsibility for where it stands.
//
// Boundary (the standing rules, applied):
//   - Geometry is the measured NIF, resolved through the VFS; OBND only as a labelled fallback;
//     no geometry = not judged, counted (rule 6).
//   - Declared dependency is the transitive closure (DetectorModel.MasterClosure).
//   - The broad phase decides with DetectorModel.BoxOverlap (proofprobe-owned); exact contact is
//     no overlap, because flush placement is how modular architecture is authored.
//   - This is a BROAD phase: world AABBs of rotated boxes are conservative, so a "deep" hit is a
//     candidate for eyes or a narrow phase, never a verdict. NOT verified in game.
//
// usage: overlapprobe [--list N] [--depth U] [--frac F] [--emit path]

const string Instance = @"C:\Games\Skyrim VR\PiranhaRim VR";
const string GamePath = @"C:\games\steam\steamapps\common\SkyrimVR";
string profileDir = Path.Combine(Instance, "profiles", "PiranhaRimVR");
string modsDir = Path.Combine(Instance, "mods");
string overwriteDir = Path.Combine(Instance, "overwrite");
string dataDir = Path.Combine(GamePath, "Data");

int listN = 25;
float minDepth = 16f;      // a hit must need at least this much separation on some axis
float minFrac = 0.25f;     // ...and bury at least this share of the smaller box
// An "object" whose box spans more than this on every... on ANY axis is a VOLUME — mountain fog,
// a LOD slab, a skydome, a city-wide clutter shell. Volumes overlap everything by design and are
// not obstructions; the first calibration run was dominated by them (worst hits at 11,000+u).
// A wall piece is 128–512u; the biggest genuine architecture runs ~1–2k. Parameter, not constant.
float maxDim = 1024f;
string? emitPath = null;
for (int i = 0; i < args.Length; i++)
{
    if (args[i] == "--list" && i + 1 < args.Length) listN = int.Parse(args[++i]);
    else if (args[i] == "--depth" && i + 1 < args.Length) minDepth = float.Parse(args[++i]);
    else if (args[i] == "--frac" && i + 1 < args.Length) minFrac = float.Parse(args[++i]);
    else if (args[i] == "--maxdim" && i + 1 < args.Length) maxDim = float.Parse(args[++i]);
    else if (args[i] == "--emit" && i + 1 < args.Length) emitPath = args[++i];
}

var sw = Stopwatch.StartNew();
var order = Mo2LoadOrder.Build(profileDir, modsDir, dataDir, overwriteDir);
var paths = order.OrderedPaths.ToList();
var comp = Mo2LoadOrder.ReadComposition(profileDir);
var arch = ArchiveDiscovery.Discover(profileDir, modsDir, dataDir, overwriteDir, GamePath);
using var assets = AssetResolver.Build(overwriteDir, modsDir, dataDir, comp.EnabledMods, arch.Archives);
Console.WriteLine($"load order: {paths.Count} plugins\n");

var Vanilla = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
{ "Skyrim.esm", "Update.esm", "Dawnguard.esm", "HearthFires.esm", "Dragonborn.esm", "SkyrimVR.esm", "_ResourcePack.esl" };
var Obstructing = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
{ "Static", "MoveableStatic", "Tree", "Furniture", "Door", "Container", "Activator", "Flora" };

// ---------------------------------------------------------------------------------------
// Pass 1 — winning placements with a RESPONSIBLE plugin each, plus base-record geometry keys.
// ---------------------------------------------------------------------------------------
var pluginKey = new ModKey[paths.Count];
var masterIdx = new HashSet<int>[paths.Count];
var baseType = new Dictionary<FormKey, string>();
var baseMesh = new Dictionary<FormKey, string>();
var baseObnd = new Dictionary<FormKey, Vec3>();
var cellName = new Dictionary<FormKey, (string? Edid, bool Interior)>();

// FormKey -> winning state. Resp = plugin that last CHANGED the placement.
var state = new Dictionary<FormKey, RefState>();
var unreadablePlugins = new List<string>();
long refsSeen = 0;

for (int p = 0; p < paths.Count; p++)
{
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

        foreach (var rec in mod.EnumerateMajorRecords())
        {
            if (rec is IModeledGetter mo && rec is ISkyrimMajorRecordGetter sm)
            {
                baseType[sm.FormKey] = rec.GetType().Name.Replace("BinaryOverlay", "");
                if (mo.Model?.File is { } mf && !string.IsNullOrWhiteSpace(mf.GivenPath)) baseMesh[sm.FormKey] = mf.GivenPath;
                if (rec is IObjectBoundedGetter ob && ob.ObjectBounds is { } bnd)
                    baseObnd[sm.FormKey] = new Vec3(MathF.Abs(bnd.Second.X - bnd.First.X),
                                                    MathF.Abs(bnd.Second.Y - bnd.First.Y),
                                                    MathF.Abs(bnd.Second.Z - bnd.First.Z));
            }
        }

        foreach (var cp in PlacementExtract.FromMod(mod))
        {
            cellName[cp.Cell] = (cp.CellEditorId, cp.Interior);
            foreach (var r in cp.Refs)
            {
                refsSeen++;
                bool moved;
                if (state.TryGetValue(r.FormKey, out var prev))
                    moved = prev.Cell != cp.Cell
                         || MathF.Abs(prev.Pos.X - r.Position.X) > 0.01f
                         || MathF.Abs(prev.Pos.Y - r.Position.Y) > 0.01f
                         || MathF.Abs(prev.Pos.Z - r.Position.Z) > 0.01f
                         || MathF.Abs(prev.Scale - r.Scale) > 0.001f;
                else moved = true;                                    // first sight: the introducer is responsible
                state[r.FormKey] = new RefState(
                    r.Position, r.Rotation, r.Scale, cp.Cell, cp.Interior, cp.Worldspace,
                    r.Base, moved ? p : prev.Resp, r.Deleted, r.InitiallyDisabled);
            }
        }
    }
    if ((p + 1) % 150 == 0) Console.WriteLine($"   ... {p + 1}/{paths.Count} plugins, {sw.Elapsed.TotalSeconds:0}s");
}

var closure = DetectorModel.MasterClosure(masterIdx);
Console.WriteLine($"\nindexed {state.Count:N0} winning refs ({refsSeen:N0} placements)  ({sw.Elapsed.TotalSeconds:0.0}s)");
Console.WriteLine($"   plugins that could not be read (contribute nothing, counted per rule 6): {unreadablePlugins.Count:N0}"
                + (unreadablePlugins.Count > 0 ? "  — " + string.Join(", ", unreadablePlugins) : ""));

// ---------------------------------------------------------------------------------------
// Pass 2 — measured geometry per base record (mesh through the VFS; OBND labelled fallback).
// ---------------------------------------------------------------------------------------
var boxCache = new Dictionary<FormKey, (Aabb Box, char Source)>();   // 'm' mesh, 'o' OBND, '-' none
int measured = 0, fellBack = 0, noGeometry = 0;
var meshBoxByPath = new Dictionary<string, Aabb?>(StringComparer.OrdinalIgnoreCase);

(Aabb Box, char Source) BoxOf(FormKey baseKey)
{
    if (boxCache.TryGetValue(baseKey, out var hit)) return hit;
    (Aabb, char) result = (Aabb.Empty, '-');
    if (baseMesh.TryGetValue(baseKey, out var rel))
    {
        var full = rel.StartsWith("meshes", StringComparison.OrdinalIgnoreCase) ? rel : Path.Combine("meshes", rel);
        if (!meshBoxByPath.TryGetValue(full, out var mb))
        {
            mb = null;
            var res = assets.ResolveForPlacement(full);
            if (res.Sources.Count > 0)
            {
                var (bytes, _) = AssetResolver.ReadPlacementSource(res.Sources[0]);
                if (bytes is not null && NifGeometry.Measure(bytes) is { Model: { Bounds.IsEmpty: false } mdl })
                    mb = mdl.Bounds;
            }
            meshBoxByPath[full] = mb;
        }
        if (mb is { } box) { result = (box, 'm'); measured++; }
    }
    if (result.Item2 == '-' && baseObnd.TryGetValue(baseKey, out var ob) && (ob.X > 0 || ob.Y > 0 || ob.Z > 0))
    {
        // OBND stores extents; centre it on the origin as the model box. Labelled, never blended.
        var half = new Vec3(ob.X / 2, ob.Y / 2, ob.Z / 2);
        result = (new Aabb(new Vec3(-half.X, -half.Y, -half.Z), half, false), 'o');
        fellBack++;
    }
    if (result.Item2 == '-') noGeometry++;
    boxCache[baseKey] = result;
    return result;
}

// ---------------------------------------------------------------------------------------
// Pass 3 — candidates, world boxes, spatial hash, pairs.
// ---------------------------------------------------------------------------------------
// A candidate is a winning, enabled, obstructing-typed ref whose responsible plugin is not the
// base game — vanilla-vs-mod pairs are excluded by the closure anyway (every mod masters
// Skyrim.esm), so hashing vanilla-responsible refs would only manufacture same-source pairs
// nobody asked about. DLC/CC-responsible refs stay in: DLC-on-DLC is the measured class-A floor.
var candidates = new List<Cand>();
long skippedDeleted = 0, skippedDisabled = 0, skippedType = 0, skippedNoGeom = 0, skippedVanillaResp = 0, skippedVolume = 0;

foreach (var (fk, s) in state)
{
    if (s.Deleted) { skippedDeleted++; continue; }
    if (s.Disabled) { skippedDisabled++; continue; }
    if (Vanilla.Contains(pluginKey[s.Resp].FileName.String)
        && pluginKey[s.Resp].FileName.String is "Skyrim.esm" or "Update.esm") { skippedVanillaResp++; continue; }
    if (s.Base is not { } bk || !baseType.TryGetValue(bk, out var bt) || !Obstructing.Contains(bt)) { skippedType++; continue; }
    var (box, src) = BoxOf(bk);
    if (src == '-') { skippedNoGeom++; continue; }
    var world = SpatialOverlap.ToWorld(box, s.Pos, s.Rot, s.Scale);
    if (world.IsEmpty) { skippedNoGeom++; continue; }
    if (MathF.Max(world.Max.X - world.Min.X, MathF.Max(world.Max.Y - world.Min.Y, world.Max.Z - world.Min.Z)) > maxDim)
    { skippedVolume++; continue; }
    candidates.Add(new Cand(fk, s, bk, world, src));
}

Console.WriteLine($"\ncandidates: {candidates.Count:N0} refs whose responsible plugin is not the base game");
Console.WriteLine($"   geometry: {measured:N0} base records measured from the mesh, {fellBack:N0} from OBND (labelled fallback), {noGeometry:N0} with neither");
Console.WriteLine($"   skipped (counted, per rule 6): {skippedDeleted:N0} deleted, {skippedDisabled:N0} initially-disabled, "
                + $"{skippedType:N0} non-obstructing type, {skippedNoGeom:N0} no geometry, {skippedVanillaResp:N0} base-game-responsible,");
Console.WriteLine($"                                  {skippedVolume:N0} volume-scale (a box axis > {maxDim:0}u — fog, LOD, skydomes; not obstructions)");

// Spatial hash: 256u tiles inside a space partition (interior cell | worldspace).
const float Tile = 256f;
var grid = new Dictionary<(FormKey Space, int X, int Y), List<int>>();
for (int i = 0; i < candidates.Count; i++)
{
    var c = candidates[i];
    var space = c.S.Interior ? c.S.Cell : c.S.Worldspace;
    int x0 = (int)MathF.Floor(c.World.Min.X / Tile), x1 = (int)MathF.Floor(c.World.Max.X / Tile);
    int y0 = (int)MathF.Floor(c.World.Min.Y / Tile), y1 = (int)MathF.Floor(c.World.Max.Y / Tile);
    for (int x = x0; x <= x1; x++)
        for (int y = y0; y <= y1; y++)
        {
            if (!grid.TryGetValue((space, x, y), out var l)) grid[(space, x, y)] = l = new List<int>();
            l.Add(i);
        }
}

long pairsChecked = 0;
var seenPair = new HashSet<(FormKey, FormKey)>();
// Same-source hits are the FLOOR: how often one author's own objects interpenetrate on purpose.
long sameChecked = 0, sameHit = 0, crossDeclaredChecked = 0, crossDeclaredHit = 0, crossChecked = 0;
var crossHits = new List<Hit>();
var floorByDomain = new Dictionary<string, (long Hit, long Checked)>();

foreach (var bucket in grid.Values)
{
    if (bucket.Count < 2) continue;
    for (int a = 0; a < bucket.Count; a++)
        for (int b = a + 1; b < bucket.Count; b++)
        {
            var ca = candidates[bucket[a]];
            var cb = candidates[bucket[b]];
            // A total order over FormKeys (ID, then ModKey name) so the same pair seen from two
            // tiles dedupes regardless of which side came first.
            bool aFirst = ca.Key.ID != cb.Key.ID
                ? ca.Key.ID < cb.Key.ID
                : string.CompareOrdinal(ca.Key.ModKey.FileName.String, cb.Key.ModKey.FileName.String) <= 0;
            var key = aFirst ? (ca.Key, cb.Key) : (cb.Key, ca.Key);
            if (!seenPair.Add(key)) continue;
            pairsChecked++;

            var ov = DetectorModel.BoxOverlap(
                new Vector3(ca.World.Min.X, ca.World.Min.Y, ca.World.Min.Z),
                new Vector3(ca.World.Max.X, ca.World.Max.Y, ca.World.Max.Z),
                new Vector3(cb.World.Min.X, cb.World.Min.Y, cb.World.Min.Z),
                new Vector3(cb.World.Max.X, cb.World.Max.Y, cb.World.Max.Z));
            bool deep = ov is { } o && o.MinDepth >= minDepth && o.VolumeFraction >= minFrac;

            if (ca.S.Resp == cb.S.Resp)
            {
                sameChecked++;
                var dom = ca.S.Interior ? "interior" : "exterior";
                var cur = floorByDomain.GetValueOrDefault(dom);
                floorByDomain[dom] = (cur.Hit + (deep ? 1 : 0), cur.Checked + 1);
                if (deep) sameHit++;
                continue;
            }
            // Cross-plugin — but a declared dependency (either direction, transitive) means one
            // author SAW the other's placement; that is coordination, not coincidence.
            if (closure[ca.S.Resp].Contains(cb.S.Resp) || closure[cb.S.Resp].Contains(ca.S.Resp))
            {
                crossDeclaredChecked++;
                if (deep) crossDeclaredHit++;
                continue;
            }
            crossChecked++;
            if (deep) crossHits.Add(new Hit(ca, cb, ov!.Value.MinDepth, ov.Value.VolumeFraction));
        }
}

string PluginName(int p) => pluginKey[p].FileName.String;
string CellOf(FormKey c) => cellName.TryGetValue(c, out var v) ? (v.Edid ?? c.ToString()) + (v.Interior ? " (int)" : " (ext)") : c.ToString();

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

Console.WriteLine($"\npairs checked: {pairsChecked:N0}   (threshold: depth >= {minDepth:0}u AND fraction >= {minFrac:0.00})");
Console.WriteLine($"\n==== THE FLOOR — same author, deliberate: how often one plugin's own objects interpenetrate ====");
Console.WriteLine($"   same-responsible pairs   : {sameChecked,10:N0} checked, {sameHit,7:N0} deep  ({Rate(sameHit, sameChecked),6:0.00}%)");
foreach (var (dom, v) in floorByDomain.OrderBy(k => k.Key))
    Console.WriteLine($"      {dom,-8}              : {v.Checked,10:N0} checked, {v.Hit,7:N0} deep  ({Rate(v.Hit, v.Checked),6:0.00}%)");
Console.WriteLine($"   declared-dependency pairs: {crossDeclaredChecked,10:N0} checked, {crossDeclaredHit,7:N0} deep  ({Rate(crossDeclaredHit, crossDeclaredChecked),6:0.00}%)   <- coordination, also floor-ish");
Console.WriteLine($"\n==== CROSS-PLUGIN, NO DECLARED DEPENDENCY — the milestone 5 question ====");
Console.WriteLine($"   pairs checked: {crossChecked:N0}, deep overlaps: {crossHits.Count:N0}  ({Rate(crossHits.Count, crossChecked),6:0.00}%)");

string HitClass(Hit h) =>
    Vanilla.Contains(PluginName(h.A.S.Resp)) && Vanilla.Contains(PluginName(h.B.S.Resp))
        ? "A. Bethesda-on-Bethesda (floor)" : "C. cross-plugin";
foreach (var g in crossHits.GroupBy(HitClass).OrderBy(g => g.Key))
    Console.WriteLine($"   {g.Count(),6:N0}  {g.Key}");

Console.WriteLine($"\n   ---- by mod pair (deep overlaps, class C) ----");
var byPair = crossHits.Where(h => HitClass(h).StartsWith("C"))
    .GroupBy(h =>
    {
        var (x, y) = (PluginName(h.A.S.Resp), PluginName(h.B.S.Resp));
        return string.CompareOrdinal(x, y) <= 0 ? (x, y) : (y, x);
    })
    .OrderByDescending(g => g.Count()).ToList();
foreach (var g in byPair.Take(listN))
{
    var worst = g.OrderByDescending(h => h.Frac).ThenByDescending(h => h.Depth).First();
    // Distinct refs on each side: 1 giant ref overlapping 500 things is one problem, not 500.
    int refsA = g.SelectMany(h => new[] { h.A.Key, h.B.Key }).Distinct().Count();
    bool rec = Reconciled(worst.A.S.Resp, worst.B.S.Resp);
    Console.WriteLine($"   {g.Count(),5:N0} hits over {refsA,5:N0} distinct refs  "
                    + $"{(rec ? "[patched, residual]" : "[NO RECONCILER]    "),-19} {g.Key.Item1}  x  {g.Key.Item2}");
    Console.WriteLine($"          worst: {baseType.GetValueOrDefault(worst.A.Base)} vs {baseType.GetValueOrDefault(worst.B.Base)}"
                    + $" in {CellOf(worst.A.S.Cell)}  depth {worst.Depth:0}u  frac {worst.Frac:0.00}"
                    + $"  [{worst.A.Src}{worst.B.Src}]  {worst.A.Key} / {worst.B.Key}");
}

Console.WriteLine($"\n   ---- the shortlist: pairs with deep overlaps and NO plugin in this order reconciling them ----");
foreach (var g in byPair.Where(g => { var h0 = g.First(); return !Reconciled(h0.A.S.Resp, h0.B.S.Resp); }).Take(listN))
{
    int refsA = g.SelectMany(h => new[] { h.A.Key, h.B.Key }).Distinct().Count();
    Console.WriteLine($"   {g.Count(),5:N0} hits over {refsA,5:N0} distinct refs   {g.Key.Item1}  x  {g.Key.Item2}");
}
Console.WriteLine($"   (a pair WITH a reconciler is a labelled conflict whose residue the patch did not cover —");
Console.WriteLine($"    second-priority, and the count says how much residue)");

// Sensitivity, always-on: the same counts at half and double both knobs.
Console.WriteLine($"\n==== SENSITIVITY (class C deep-overlap count at other thresholds) ====");
foreach (var (d, f) in new[] { (minDepth / 2, minFrac / 2), (minDepth, minFrac), (minDepth * 2, MathF.Min(1f, minFrac * 2)) })
{
    int n = 0;
    foreach (var h in crossHits) if (h.Depth >= d && h.Frac >= f && HitClass(h).StartsWith("C")) n++;
    // crossHits only holds hits at the BASE threshold, so the halved row needs its own pass — say so.
    Console.WriteLine(d < minDepth
        ? $"   depth>={d,3:0}u frac>={f:0.00}  ->  (not computable from this run's hit set — re-run with --depth {d:0} --frac {f:0.00})"
        : $"   depth>={d,3:0}u frac>={f:0.00}  ->  {n:N0}");
}

// --emit: the LATER plugin's ref is the necessary fix target (it placed into existing context);
// moving the other side is the legitimate alternative.
if (emitPath is not null)
{
    var conflicts = new List<Conflict>();
    foreach (var h in crossHits.Where(h => HitClass(h).StartsWith("C")))
    {
        var (later, earlier) = h.A.S.Resp >= h.B.S.Resp ? (h.A, h.B) : (h.B, h.A);
        conflicts.Add(new Conflict(
            "OVERLAP", PluginName(earlier.S.Resp), PluginName(later.S.Resp),
            $"{baseType.GetValueOrDefault(later.Base)} from {PluginName(later.S.Resp)} buried {h.Frac:0.00} deep ({h.Depth:0}u) in "
          + $"{baseType.GetValueOrDefault(earlier.Base)} from {PluginName(earlier.S.Resp)} in {CellOf(later.S.Cell)}",
            new List<ConflictRecord> { new(later.Key.ToString(), "placed-ref", CellOf(later.S.Cell)) },
            new List<ConflictRecord> { new(earlier.Key.ToString(), "placed-ref", null) }));
    }
    ConflictIo.Write(emitPath, new ConflictFile("overlapprobe", Instance,
        DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm:ssZ"), conflicts));
    Console.WriteLine($"\nemitted {conflicts.Count:N0} conflicts -> {emitPath}");
}

Console.WriteLine($"\nNOT verified in game; a broad-phase AABB hit is a candidate for eyes or a narrow phase, never a verdict.");
Console.WriteLine($"elapsed {sw.Elapsed.TotalSeconds:0.0}s");
return 0;

static float Rate(long hit, long total) => total == 0 ? 0 : 100f * hit / total;

readonly record struct RefState(Vec3 Pos, Vec3 Rot, float Scale, FormKey Cell, bool Interior,
                                FormKey Worldspace, FormKey? Base, int Resp, bool Deleted, bool Disabled);
readonly record struct Cand(FormKey Key, RefState S, FormKey Base, Aabb World, char Src);
readonly record struct Hit(Cand A, Cand B, float Depth, float Frac);
