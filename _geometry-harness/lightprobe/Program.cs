// lightprobe — the placed lights of selected interior cells, as a load order resolves them, and what each does to the
// render surfaces around it (light-clash measure, lane L 2026-10-04).
//
// usage: lightprobe --self-test [--meshes-from GAME_DATA]   (reader cases on vanilla meshes run only with --meshes-from)
//        lightprobe [--instance DIR] [--game DIR] [--profile NAME] [--order FILE] --json OUT
//                   (--cell EDID ... | --cells-file FILE | --list-interiors) [--geometry] [--max-mb N]
//
// Per selected cell it writes every reference the winning order files there (base, type, editor id, model path, pose,
// load-time enable state through the shared enable chain, deletion, responsible plugin) and, for each placed Light,
// its base's radius / flags / fade / colour with the reference's XRDS radius offset added. With --geometry,
// every enabled reference's render model is read (src/housecarl-core/LightReach.cs), placed, and each enabled light
// gets three readings: facing (front / back / no first hit over 64 rays inside its radius), leak (attenuated area lit
// through an opaque surface, and the unblocked area beside it, per receiving reference), and point-in-solid against
// every reference whose placed bounds contain the light (MeshAnchor's point reading). Every lit shape (drawn, lighting
// shader; editor markers and Is-Marker bases are not drawn) a light's radius reaches (its placed vertex box; the bound sphere count printed beside) gets a row with the lights that reach it (count, shadow-casting, portal-strict). A reference whose
// model cannot be read is listed with its reason, and every light whose radius reaches its record bounds is labelled
// incomplete. No decision is made here: attribution and verdicts are the caller's (the fold).
// Base Object Swapper: not applied (stated in the output).
// --list-interiors: every interior cell of the order with its placed-light count (the calibration sample frame).

using System.Diagnostics;
using System.Numerics;
using System.Text.Json;
using System.Text.Json.Serialization;
using HarnessShared;
using HousecarlCore;
using HousecarlCore.Progress;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Skyrim;

using var __run = RunLog.BeginProcess("lightprobe", args);
if (args.Length >= 1 && args[0] == "--self-test") return LightSelfTest.Run(args);

string Instance = HarnessTarget.Instance, GamePath = HarnessTarget.Game, profileName = HarnessTarget.Profile;
string? orderFile = null, jsonPath = null, cellsFile = null;
var wantCells = new List<string>();
bool geometry = false, listInteriors = false;
long maxMb = 0;   // --max-mb: stop (exit 3) when private bytes pass this many MB at a checkpoint; 0 = no cap
long peakPrivate = 0;   // the largest private-bytes reading at a checkpoint (working set also counts mapped plugin files)
for (int i = 0; i < args.Length; i++)
{
    string a = args[i];
    string Next() => i + 1 < args.Length ? args[++i] : throw new ArgumentException($"{a} needs a value");
    switch (a)
    {
        case "--instance": Instance = Next(); break;
        case "--game": GamePath = Next(); break;
        case "--profile": profileName = Next(); break;
        case "--order": orderFile = Next(); break;
        case "--json": jsonPath = Next(); break;
        case "--cell": wantCells.Add(Next()); break;
        case "--cells-file": cellsFile = Next(); break;
        case "--geometry": geometry = true; break;
        case "--list-interiors": listInteriors = true; break;
        case "--max-mb": maxMb = long.Parse(Next()); break;
        default: Console.Error.WriteLine($"unknown argument {a}"); return 2;
    }
}
if (jsonPath is null) { Console.Error.WriteLine("--json OUT is required"); return 2; }
if (cellsFile is not null) wantCells.AddRange(File.ReadAllLines(cellsFile).Select(l => l.Trim()).Where(l => l.Length > 0 && !l.StartsWith('#')));
if (!listInteriors && wantCells.Count == 0) { Console.Error.WriteLine("name cells with --cell / --cells-file, or --list-interiors"); return 2; }

Instance = HarnessTarget.Require(Instance, "MO2 instance directory", "instance", "--instance");
GamePath = HarnessTarget.Require(GamePath, "game directory", "game", "--game");
profileName = HarnessTarget.Require(profileName, "MO2 profile name", "profile", "--profile");
string profileDir = Path.Combine(Instance, "profiles", profileName), modsDir = Path.Combine(Instance, "mods"),
       overwriteDir = Path.Combine(Instance, "overwrite"), dataDir = Path.Combine(GamePath, "Data");
var sw = Stopwatch.StartNew();
void Cap(string where)
{
    if (maxMb <= 0) return;
    var pr = Process.GetCurrentProcess();
    long priv = pr.PrivateMemorySize64 / 1048576, ws = pr.WorkingSet64 / 1048576;
    peakPrivate = Math.Max(peakPrivate, priv);
    if (priv > maxMb) { Console.Error.WriteLine($"memory cap: private bytes {priv} MB (working set {ws} MB) > --max-mb {maxMb} at {where}; stopped"); Environment.Exit(3); }
}
var cfg = HarnessOrderCli.Resolve(profileName, profileDir, modsDir, dataDir, overwriteDir, GamePath, orderFile, null, null);
if (cfg.Refused) { Console.Error.WriteLine(cfg.RefusedReason); return 2; }
var paths = cfg.OrderedPaths;
using var assets = AssetResolver.Build(overwriteDir, modsDir, dataDir, cfg.EnabledMods, cfg.Archives.Archives);
HarnessOrderCli.Print(cfg);

// ---- pass 1: the shared index (placements, bases with models, enable chain facts, deletions) ----
var idx = new OverlapIndex();
var pluginName = new string[paths.Count];
__run.Feature("plugins", paths.Count, "plugins").Stage("scan", "reading plugins", paths.Count, "plugins");
for (int p = 0; p < paths.Count; p++)
{
    __run.Advance();
    ISkyrimModGetter mod;
    try { mod = SkyrimMod.CreateFromBinaryOverlay(paths[p], SkyrimRelease.SkyrimSE); }
    catch (Exception ex) { Console.Error.WriteLine($"{Path.GetFileName(paths[p])} unreadable: {ex.Message}"); return 2; }
    using (mod as IDisposable) { pluginName[p] = mod.ModKey.FileName.String; idx.Ingest(p, mod); }
    Cap("plugin " + pluginName[p]);
}
var arms = new EnableArms(idx);
// The plugin overlays are released; compact the heap so the working-set cap reads what is live, not what was committed.
System.Runtime.GCSettings.LargeObjectHeapCompactionMode = System.Runtime.GCLargeObjectHeapCompactionMode.CompactOnce;
GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, blocking: true, compacting: true);
Cap("after indexing");
Console.WriteLine($"indexed {idx.History.Count:N0} refs over {paths.Count} plugins ({sw.Elapsed.TotalSeconds:0.0}s)");

// cells by editor id (interior only)
var cellByEdid = new Dictionary<string, FormKey>(StringComparer.OrdinalIgnoreCase);
foreach (var (k, (ed, interior)) in idx.CellName) if (interior && ed is not null) cellByEdid[ed] = k;
var refsByCell = new Dictionary<FormKey, List<FormKey>>();
foreach (var (r, (c, ws)) in idx.RefCell) if (ws.IsNull) (refsByCell.TryGetValue(c, out var l) ? l : refsByCell[c] = new()).Add(r);

// ---- pass 2: every base the selected refs name (type, editor id, light data) and the refs' own light overrides ----
var scopeCells = new List<(string Edid, FormKey Cell)>();
if (listInteriors) scopeCells.AddRange(cellByEdid.Select(kv => (kv.Key, kv.Value)));
else
    foreach (var c in wantCells)
    {
        if (!cellByEdid.TryGetValue(c, out var ck)) { Console.Error.WriteLine($"interior cell '{c}' is not in this order"); return 2; }
        scopeCells.Add((c, ck));
    }
var scopeRefs = scopeCells.SelectMany(c => refsByCell.GetValueOrDefault(c.Cell) ?? new()).ToHashSet();
var wantBases = scopeRefs.Where(idx.RefBase.ContainsKey).Select(r => idx.RefBase[r]).ToHashSet();
var baseType = new Dictionary<FormKey, (string Type, string? Edid)>();
var lightBase = new Dictionary<FormKey, LightBase>();
var refRadius = new Dictionary<FormKey, float?>();
__run.Stage("bases", "reading bases and light overrides", paths.Count, "plugins");
for (int p = 0; p < paths.Count; p++)
{
    __run.Advance();
    using var mod0 = SkyrimMod.CreateFromBinaryOverlay(paths[p], SkyrimRelease.SkyrimSE) as IDisposable;
    var mod = (ISkyrimModGetter)mod0!;
    foreach (var rec in mod.EnumerateMajorRecords())
    {
        if (rec is IPlacedObjectGetter po)
        {
            if (scopeRefs.Contains(po.FormKey)) refRadius[po.FormKey] = po.Radius;   // winner wins: the last record of the ref
            continue;
        }
        if (!wantBases.Contains(rec.FormKey)) continue;
        baseType[rec.FormKey] = (rec.GetType().Name.Replace("BinaryOverlay", ""), rec.EditorID);
        if (rec is ILightGetter lg)
        {
            var f = lg.Flags;
            lightBase[rec.FormKey] = new LightBase(lg.Radius, f.ToString().Split(", ", StringSplitOptions.RemoveEmptyEntries),
                f.HasFlag(Light.Flag.ShadowSpotlight) || f.HasFlag(Light.Flag.ShadowHemisphere) || f.HasFlag(Light.Flag.ShadowOmnidirectional),
                f.HasFlag(Light.Flag.PortalStrict) || lg.MajorFlags.HasFlag(Light.MajorFlag.PortalStrict),
                f.HasFlag(Light.Flag.OffByDefault), f.HasFlag(Light.Flag.SpotLight) || f.HasFlag(Light.Flag.ShadowSpotlight),
                lg.FadeValue, new[] { (int)lg.Color.R, (int)lg.Color.G, (int)lg.Color.B }, lg.FOV);
        }
        else lightBase.Remove(rec.FormKey);   // a later winner of another type
    }
    Cap("bases " + Path.GetFileName(paths[p]));
}
Console.WriteLine($"bases read: {baseType.Count:N0} ({lightBase.Count:N0} lights); {scopeRefs.Count:N0} refs in {scopeCells.Count} cell(s) ({sw.Elapsed.TotalSeconds:0.0}s)");

// Light Limit Fix in the evaluated instance (decides which C4 reading the fold uses): the standalone SKSE plugin, or Community
// Shaders with its Light Limit Fix feature, resolved through this order's virtual file system (enabled mods + game Data).
object LlfDetect()
{
    string[] probes = { "SKSE/Plugins/LightLimitFix.dll", "SKSE/Plugins/CommunityShaders.dll", "Shaders/Features/LightLimitFix.ini" };
    var hits = probes.Select(p => (p, h: assets.Resolve(p))).Where(x => x.h.Exists).Select(x => new { path = x.p, provider = x.h.Winner?.Source }).ToList();
    bool standalone = hits.Any(h => h.path.EndsWith("LightLimitFix.dll", StringComparison.OrdinalIgnoreCase));
    bool cs = hits.Any(h => h.path.EndsWith("CommunityShaders.dll", StringComparison.OrdinalIgnoreCase))
              && hits.Any(h => h.path.EndsWith("LightLimitFix.ini", StringComparison.OrdinalIgnoreCase));
    return new { installed = standalone || cs, how = standalone ? "SKSE/Plugins/LightLimitFix.dll" : cs ? "Community Shaders + Shaders/Features/LightLimitFix.ini" : "none of " + string.Join(", ", probes),
                 found = hits };
}
var llf = LlfDetect();

var jsonOpts = new JsonSerializerOptions { WriteIndented = true, PropertyNamingPolicy = JsonNamingPolicy.CamelCase, DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
                                           NumberHandling = JsonNumberHandling.AllowNamedFloatingPointLiterals };
object Identity() => new { schema = "housecarl.light/1", policy = LightReach.PolicyVersion, bos = "off", pose = "stored pose (an animated shape is read where the file stores it)", population = "load-time", state = "baseline",
                          configuration = cfg.Identity("load-time", "baseline").Short, plugins = pluginName, llf };

if (listInteriors)
{
    var rows = scopeCells.Select(c =>
    {
        var refs = refsByCell.GetValueOrDefault(c.Cell) ?? new();
        int lights = refs.Count(r => idx.RefBase.TryGetValue(r, out var b) && lightBase.ContainsKey(b));
        int en = refs.Count(r => idx.RefBase.TryGetValue(r, out var b) && lightBase.ContainsKey(b) && !idx.IsDeletedNow(r) && arms.Winner(r) is { Resolved: true, Enabled: true });
        return new { edid = c.Edid, cell = c.Cell.ToString(), refs = refs.Count, lights, lightsEnabled = en };
    }).OrderBy(x => x.edid, StringComparer.Ordinal).ToList();
    File.WriteAllText(jsonPath, JsonSerializer.Serialize(new { identity = Identity(), interiors = rows }, jsonOpts));
    Cap("end");
    Console.WriteLine($"{rows.Count:N0} interior cells -> {jsonPath}; peak working set {Process.GetCurrentProcess().PeakWorkingSet64 / 1048576.0:N0} MB; largest private bytes at a checkpoint {peakPrivate:N0} MB");
    return 0;
}

// ---- per cell: references, lights, and (with --geometry) the readings ----
var modelCache = new Dictionary<string, (LightModel? Model, string? Why)>(StringComparer.OrdinalIgnoreCase);
(LightModel? Model, string? Why) Model(FormKey b)
{
    if (!idx.BaseMesh.TryGetValue(b, out var mesh)) return (null, "no model");
    if (modelCache.TryGetValue(mesh, out var hit)) return hit;
    var rel = mesh.StartsWith("meshes", StringComparison.OrdinalIgnoreCase) ? mesh : Path.Combine("meshes", mesh);
    var res = assets.ResolveForPlacement(rel);
    if (res.Sources.Count == 0) return modelCache[mesh] = (null, "missing_asset: " + rel);
    var (bytes, err) = AssetResolver.ReadPlacementSource(res.Sources[0]);
    if (bytes is null) return modelCache[mesh] = (null, "asset read failed: " + err);
    try
    {
        var m = LightReach.ReadModel(bytes);
        return modelCache[mesh] = m.Status == "refused" ? (null, m.Reason) : (m, null);
    }
    catch (Exception ex) { return modelCache[mesh] = (null, "read threw: " + ex.Message); }
}
static double[] V(Vec3 v) => new double[] { v.X, v.Y, v.Z };
static double[] W(Vector3 v) => new double[] { v.X, v.Y, v.Z };

var cellsOut = new List<object>();
__run.Stage("cells", "measuring cells", scopeCells.Count, "cells");
foreach (var (cedid, ck) in scopeCells)
{
    __run.Advance();
    var refs = refsByCell.GetValueOrDefault(ck) ?? new();
    var refRows = new List<object>();
    var placed = new List<(FormKey Key, Snap Pose, FormKey Base, bool On)>();
    foreach (var r in refs.OrderBy(r => r.ToString(), StringComparer.Ordinal))
    {
        var pose = idx.WinningGeometryPose(r);
        if (pose is null) continue;
        var b = idx.RefBase.GetValueOrDefault(r);
        var en = arms.Winner(r);
        bool deleted = idx.IsDeletedNow(r) || pose.Value.Deleted;
        bool on = !deleted && en.Resolved && en.Enabled;
        var (bt, bed) = baseType.GetValueOrDefault(b, ("?", null));
        refRows.Add(new { key = r.ToString(), @base = b.IsNull ? null : b.ToString(), type = bt, edid = bed, mesh = idx.BaseMesh.GetValueOrDefault(b),
                          pos = V(pose.Value.Pos), rot = V(pose.Value.Rot), scale = pose.Value.Scale, enabled = en.Enabled, resolved = en.Resolved, viaParent = en.ViaParent,
                          deleted, responsible = pose.Value.Plugin >= 0 && pose.Value.Plugin < pluginName.Length ? pluginName[pose.Value.Plugin] : null,
                          obnd = idx.BaseObnd.TryGetValue(b, out var ob) ? V(ob) : null });
        placed.Add((r, pose.Value, b, on));
    }
    var lights = placed.Where(x => lightBase.ContainsKey(x.Base)).ToList();
    var lightRow = new Dictionary<FormKey, Dictionary<string, object?>>();
    foreach (var l in lights)
    {
        var lb = lightBase[l.Base];
        float? rr = refRadius.GetValueOrDefault(l.Key);
        lightRow[l.Key] = new Dictionary<string, object?>
        {
            ["key"] = l.Key.ToString(), ["base"] = l.Base.ToString(), ["edid"] = baseType.GetValueOrDefault(l.Base).Edid, ["enabled"] = l.On,
            ["pos"] = V(l.Pose.Pos), ["radiusBase"] = lb.Radius, ["radiusRef"] = rr, ["radius"] = LightReach.EffectiveRadius(lb.Radius, rr),
            ["flags"] = lb.Flags, ["shadow"] = lb.Shadow, ["strict"] = lb.Strict, ["offByDefault"] = lb.OffByDefault, ["spot"] = lb.Spot,
            ["fade"] = lb.Fade, ["color"] = lb.Color, ["fov"] = lb.Fov,
        };
    }
    object? geom = null;
    var shapeRows = new List<object>();
    if (geometry)
    {
        var parts = new List<(TriangleSoup, string[]?, int)>();
        var refused = new List<(FormKey Key, string Why, Vector3 At, float Reach)>();
        var bounds = new List<(int Owner, Aabb Box, TriangleSoup World, string[]? Tags, Snap Pose, TriangleSoup ModelSoup)>();
        var shapes = new List<(int Owner, LightShape Shape, Vector3 Center, float Radius, Vector3[]? Box)>();
        var on = placed.Where(x => x.On).ToList();
        for (int o = 0; o < on.Count; o++)
        {
            var x = on[o];
            if (!idx.BaseMesh.ContainsKey(x.Base)) continue;   // model-less (lights, markers): nothing to draw
            if (!LightReach.BaseDrawn(idx.BaseIsMarker.Contains(x.Base))) continue;   // an Is-Marker base: never drawn
            var (m, why) = Model(x.Base);
            if (m is null || m.Soup is null)
            {
                var ext = idx.BaseObnd.TryGetValue(x.Base, out var ob) ? new Vector3(ob.X, ob.Y, ob.Z).Length() / 2 * x.Pose.Scale : 0f;
                refused.Add((x.Key, why ?? m?.Reason ?? "no triangles", new Vector3(x.Pose.Pos.X, x.Pose.Pos.Y, x.Pose.Pos.Z), ext));
                continue;
            }
            var world = SpatialOverlap.TransformSoup(m.Soup, x.Pose.Pos, x.Pose.Rot, x.Pose.Scale);
            parts.Add((world, m.Tags, o));
            bounds.Add((o, SpatialOverlap.SoupBounds(world), world, m.Tags, x.Pose, m.Soup));
            foreach (var s in m.Shapes.Where(s => s.Lit && s.ModelRadius > 0))
            {
                var c = SpatialOverlap.TransformSoup(new TriangleSoup(new[] { s.ModelCenter }, Array.Empty<int>()), x.Pose.Pos, x.Pose.Rot, x.Pose.Scale).Vertices[0];
                Vector3[]? box = null;
                if (s.BoxMin is Vector3 mn && s.BoxMax is Vector3 mx)
                {
                    var w = SpatialOverlap.TransformSoup(new TriangleSoup(new[] { mn, new Vector3(mx.X, mn.Y, mn.Z), new Vector3(mn.X, mx.Y, mn.Z), new Vector3(mn.X, mn.Y, mx.Z) },
                                                                          Array.Empty<int>()), x.Pose.Pos, x.Pose.Rot, x.Pose.Scale).Vertices;
                    box = new[] { w[0], w[1] - w[0], w[2] - w[0], w[3] - w[0] };
                }
                shapes.Add((o, s, c, s.ModelRadius * x.Pose.Scale, box));
            }
            if (m.Status == "partial") refused.Add((x.Key, "partial: " + string.Join("; ", m.Labels), new Vector3(x.Pose.Pos.X, x.Pose.Pos.Y, x.Pose.Pos.Z), 0f));
        }
        var scene = new LightScene(parts);
        geom = new { triangles = scene.Triangles, degenerateSkipped = scene.DegenerateSkipped, nonOpaqueSkipped = scene.NonOpaqueSkipped,
                     unreadable = refused.Select(r => new { key = r.Key.ToString(), reason = r.Why }).ToList() };
        var litLights = lights.Where(l => l.On).ToList();
        foreach (var l in litLights)
        {
            var row = lightRow[l.Key];
            var at = new Vector3(l.Pose.Pos.X, l.Pose.Pos.Y, l.Pose.Pos.Z);
            float r = (float)row["radius"]!;
            var facing = scene.Facing(at, r);
            var leak = scene.Leak(at, r);
            row["facing"] = new { facing.Rays, facing.Front, facing.Back, facing.None };
            row["leak"] = new { leak.Lit, leak.Leak, leak.Samples, leak.ReceiverTriangles,
                                leakByRef = leak.LeakByOwner.ToDictionary(kv => on[kv.Key].Key.ToString(), kv => kv.Value),
                                litByRef = leak.LitByOwner.ToDictionary(kv => on[kv.Key].Key.ToString(), kv => kv.Value) };
            var pis = new List<object>();
            foreach (var bx in bounds)
            {
                if (at.X < bx.Box.Min.X || at.Y < bx.Box.Min.Y || at.Z < bx.Box.Min.Z || at.X > bx.Box.Max.X || at.Y > bx.Box.Max.Y || at.Z > bx.Box.Max.Z) continue;
                try
                {
                    var ar = MeshAnchor.TestPlaced(bx.ModelSoup, bx.Tags, bx.Pose.Pos, bx.Pose.Rot, bx.Pose.Scale, at, new BodyExtent(0, 10, 1));
                    pis.Add(new { @ref = on[bx.Owner].Key.ToString(), point = ar.Point, reason = ar.PointReason });
                }
                catch (Exception ex) { pis.Add(new { @ref = on[bx.Owner].Key.ToString(), point = "refused", reason = ex.Message }); }
            }
            row["pointInSolid"] = pis;
            var inc = refused.Where(u => Vector3.Distance(u.At, at) <= r + u.Reach).Select(u => u.Key.ToString()).ToList();
            if (inc.Count > 0) row["incomplete"] = inc;
        }
        foreach (var s in shapes)
        {
            // assignment (sixth fold, rule a): a light counts toward a shape's limit only if its radius reaches the shape's placed
            // box; the bound-sphere count is printed beside it. A shape with no vertex box falls back to the sphere, labelled.
            var sphere = litLights.Where(l => LightReach.MeetsShape(new Vector3(l.Pose.Pos.X, l.Pose.Pos.Y, l.Pose.Pos.Z), (float)lightRow[l.Key]["radius"]!, s.Center, s.Radius)).ToList();
            if (sphere.Count == 0) continue;
            var met = s.Box is { } bx4
                ? sphere.Where(l => LightReach.MeetsShapeBox(new Vector3(l.Pose.Pos.X, l.Pose.Pos.Y, l.Pose.Pos.Z), (float)lightRow[l.Key]["radius"]!, bx4[0], bx4[1], bx4[2], bx4[3])).ToList()
                : sphere;
            if (met.Count == 0) continue;
            shapeRows.Add(new { @ref = on[s.Owner].Key.ToString(), shape = s.Shape.Index, name = s.Shape.Name, center = W(s.Center), radius = s.Radius, boundSource = s.Shape.BoundSource,
                                assign = s.Box is null ? "sphere (no vertex box)" : "box", countSphere = sphere.Count,
                                count = met.Count, shadow = met.Count(l => lightBase[l.Base].Shadow), strict = met.Count(l => lightBase[l.Base].Strict),
                                lights = met.Select(l => l.Key.ToString()).ToList() });
        }
    }
    Cap("cell " + cedid);
    cellsOut.Add(new { edid = cedid, cell = ck.ToString(), refs = refRows, lights = lightRow.Values.ToList(), geometry = geom, shapes = shapeRows });
    Console.WriteLine($"{cedid}: {refRows.Count} refs, {lights.Count} lights ({lights.Count(l => l.On)} enabled), {shapeRows.Count} lit shapes ({sw.Elapsed.TotalSeconds:0.0}s)");
}
File.WriteAllText(jsonPath, JsonSerializer.Serialize(new { identity = Identity(), geometry, cells = cellsOut }, jsonOpts));
Cap("end");
Console.WriteLine($"-> {jsonPath}; peak working set {Process.GetCurrentProcess().PeakWorkingSet64 / 1048576.0:N0} MB; largest private bytes at a checkpoint {peakPrivate:N0} MB");
return 0;

sealed record LightBase(uint Radius, string[] Flags, bool Shadow, bool Strict, bool OffByDefault, bool Spot, float Fade, int[] Color, float Fov);
