using System.Diagnostics;
using System.Text.Json;
using HousecarlCore;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Skyrim;
using HousecarlCore.Progress;

// Milestone 2+3 proof: resolve every Model.File through the REAL MO2 VFS, measure the winning
// NIF's geometry, and diff it against the plugin's OBND.
//
// This is the direct test of Piranha's standing rule (2026-08-26): OBND goes stale when a
// standalone mesh replacer swaps the .nif without touching the plugin. If that is right, the
// disagreement should concentrate in records whose winning mesh comes from a replacer mod
// rather than from Skyrim's own BSAs.

using var __run = RunLog.BeginProcess("geoprobe", args);

string Instance = HarnessTarget.Require(HarnessTarget.Instance, "MO2 instance directory", "instance");
string GamePath = HarnessTarget.Require(HarnessTarget.Game, "game directory", "game");
string profileDir = Path.Combine(Instance, "profiles", HarnessTarget.Require(HarnessTarget.Profile, "MO2 profile name", "profile"));
string modsDir = Path.Combine(Instance, "mods");
string overwriteDir = Path.Combine(Instance, "overwrite");
string dataDir = Path.Combine(GamePath, "Data");

int limit = args.Length > 0 && int.TryParse(args[0], out var n) ? n : int.MaxValue;
// --dump <file.tsv>: one row per measured mesh — the record's OBND, the reader's RENDER and COLLISION bounds in model
// space, the source — so a comparison can be grouped outside (Q28's ckbounds: by where the mesh's node chain turns).
string? dumpPath = null;
for (int ai = 0; ai + 1 < args.Length; ai++) if (args[ai] == "--dump") dumpPath = args[ai + 1];

var sw = Stopwatch.StartNew();
var order = Mo2LoadOrder.Build(profileDir, modsDir, dataDir, overwriteDir);
Console.WriteLine($"load order: {order.ResolvedCount} plugins resolved of {order.ActiveCount} active "
                + $"({order.Warnings.Count} warnings) in {sw.ElapsedMilliseconds} ms");

var comp = Mo2LoadOrder.ReadComposition(profileDir);
var arch = ArchiveDiscovery.Discover(profileDir, modsDir, dataDir, overwriteDir, GamePath);
Console.WriteLine($"archives: {arch.Archives.Count} active BSAs ({arch.Warnings.Count} warnings)");

using var assets = AssetResolver.Build(overwriteDir, modsDir, dataDir, comp.EnabledMods, arch.Archives);
using var resolver = LoadOrderResolver.Build(order.OrderedPaths);
Console.WriteLine($"resolver: {resolver.PluginCount} plugins, {resolver.RecordCount:N0} records, "
                + $"{resolver.ConflictCount:N0} contested   ({sw.ElapsedMilliseconds / 1000.0:0.0}s)");
foreach (var f in resolver.LoadFailures.Take(5)) Console.WriteLine($"   ! {f}");

// ---- collect winning base records that have BOTH a model and object bounds ----
sw.Restart();
using var session = resolver.OpenSession();

var byModel = new Dictionary<string, (string RecType, FormKey Fk, Vec3 Min, Vec3 Max, int Count)>(StringComparer.OrdinalIgnoreCase);
int withModel = 0, withoutBounds = 0;

foreach (var path in order.OrderedPaths)
{
    ISkyrimModGetter mod;
    try { mod = SkyrimMod.CreateFromBinaryOverlay(path, SkyrimRelease.SkyrimSE); }
    catch { continue; }
    using (mod as IDisposable)
    {
        foreach (var rec in mod.EnumerateMajorRecords())
        {
            if (rec is not IModeledGetter m || m.Model?.File is not { } file) continue;
            var rel = file.GivenPath;
            if (string.IsNullOrWhiteSpace(rel)) continue;
            withModel++;

            Vec3 mn = default, mx = default;
            if (rec is IObjectBoundedGetter ob && ob.ObjectBounds is { } b)
            {
                mn = new Vec3(b.First.X, b.First.Y, b.First.Z);
                mx = new Vec3(b.Second.X, b.Second.Y, b.Second.Z);
            }
            else { withoutBounds++; continue; }

            // Keep the LAST writer per mesh path — later in load order = the winning record.
            byModel.TryGetValue(rel, out var prev);
            byModel[rel] = (rec.GetType().Name, rec.FormKey, mn, mx, prev.Count + 1);
        }
    }
}
Console.WriteLine($"\nbase records with a Model.File: {withModel:N0}  (no ObjectBounds: {withoutBounds:N0})");
Console.WriteLine($"distinct mesh paths: {byModel.Count:N0}   scanned in {sw.Elapsed.TotalSeconds:0.0}s");

// ---- resolve + measure ----
sw.Restart();
int measured = 0, unresolved = 0, parseFail = 0, emptyGeo = 0, noCollision = 0, collisionUnread = 0;
var rows = new List<Row>();
var errs = new Dictionary<string, int>(StringComparer.Ordinal);

foreach (var (rel, info) in byModel.OrderBy(k => k.Key, StringComparer.OrdinalIgnoreCase))
{
    if (rows.Count >= limit) break;
    var full = rel.StartsWith("meshes", StringComparison.OrdinalIgnoreCase) ? rel : Path.Combine("meshes", rel);

    var res = assets.ResolveForPlacement(full);
    if (res.Sources.Count == 0) { unresolved++; continue; }
    var src = res.Sources[0];

    var (bytes, err) = AssetResolver.ReadPlacementSource(src);
    if (bytes is null) { parseFail++; Bump(err ?? "read failed"); continue; }

    var outcome = NifGeometry.Measure(bytes);
    if (outcome.Model is null) { parseFail++; Bump(Short(outcome.Error)); continue; }
    var g = outcome.Model;
    measured++;
    if (g.Bounds.IsEmpty) { emptyGeo++; continue; }
    if (!g.HasCollision) noCollision++;
    if (g.CollisionUnread.Count > 0) collisionUnread++;

    // VANILLA = the winning file physically lives under the game's own Data folder (loose there, or in a
    // BSA there). ProviderName is the ARCHIVE FILENAME for a BSA source, never the literal "Data", so
    // comparing the name was wrong: it classified every vanilla BSA mesh as modded.
    var physical = src.LooseFilePath ?? src.ArchivePath ?? "";
    bool vanillaSrc = physical.StartsWith(dataDir, StringComparison.OrdinalIgnoreCase);

    rows.Add(new Row(rel, info.RecType, info.Fk, info.Min, info.Max,
                     g.Bounds, g.TotalVertices, g.TotalTriangles,
                     src.ProviderName, src.Kind == AssetKind.Bsa, res.Sources.Count, vanillaSrc, g.CollisionBounds));
}
Console.WriteLine($"\nmeasured {measured:N0} meshes in {sw.Elapsed.TotalSeconds:0.0}s "
                + $"| unresolved {unresolved:N0} | parse/read failures {parseFail:N0} | empty geometry {emptyGeo:N0}");
Console.WriteLine($"   no Havok collision: {noCollision:N0}   collision only partly decodable: {collisionUnread:N0}");
foreach (var (k, v) in errs.OrderByDescending(x => x.Value).Take(6)) Console.WriteLine($"   {v,6:N0}  {k}");

if (dumpPath is not null)
{
    using var dw = new StreamWriter(dumpPath);
    dw.WriteLine("mesh\trec\tform\tprovider\tvanilla\tobnd_min\tobnd_max\trender_min\trender_max\tcoll_min\tcoll_max\tverts\ttris");
    static string V(Vec3 v) => $"{v.X:0.##},{v.Y:0.##},{v.Z:0.##}";
    foreach (var r in rows)
        dw.WriteLine($"{r.Mesh}\t{Clean(r.RecType)}\t{r.Fk}\t{r.Provider}\t{(r.Vanilla ? 1 : 0)}\t{V(r.ObndMin)}\t{V(r.ObndMax)}\t{V(r.Measured.Min)}\t{V(r.Measured.Max)}\t"
                     + $"{(r.Coll.IsEmpty ? "" : V(r.Coll.Min))}\t{(r.Coll.IsEmpty ? "" : V(r.Coll.Max))}\t{r.Verts}\t{r.Tris}");
    Console.WriteLine($"   dumped {rows.Count:N0} rows to {dumpPath}");
}

// ---- OBND vs measured ----
// OBND is a box in MODEL space, as is our measured AABB, so they are directly comparable.
// Score by the worst per-axis half-extent ratio: how much bigger/smaller is the real mesh?
Console.WriteLine($"\n================ OBND vs MEASURED GEOMETRY ({rows.Count:N0} meshes) ================");

// OBND with a zero (or sub-unit) extent on any axis is NOT a stale bound — it is an ABSENT one, and a
// ratio against it is manufactured by the clamp rather than measured. Split it out as its own category
// instead of letting it dominate the tail.
static bool Degenerate(Vec3 a, Vec3 b) =>
    MathF.Abs(b.X - a.X) < 1f || MathF.Abs(b.Y - a.Y) < 1f || MathF.Abs(b.Z - a.Z) < 1f;

// Record types whose OBND is a real spatial bound — the ones that occupy room in a cell and can collide.
// Excluded deliberately: Projectile / Explosion / Hazard / Light / AddonNode etc., where OBND is an
// engine hint rather than a mesh measurement (vanilla cloud + lightning-sheet meshes were dominating
// the tail for exactly that reason, and they are not what this rule is about).
var Spatial = new HashSet<string>(StringComparer.Ordinal)
{ "Static", "MoveableStatic", "Furniture", "Container", "Door", "Tree", "Flora", "Activator", "TalkingActivator" };
static string Clean(string t) => t.Replace("BinaryOverlay", "");

static float Ext(float a, float b) => MathF.Max(1f, MathF.Abs(b - a));
var allScored = rows.Select(r =>
{
    var oS = new[] { Ext(r.ObndMin.X, r.ObndMax.X), Ext(r.ObndMin.Y, r.ObndMax.Y), Ext(r.ObndMin.Z, r.ObndMax.Z) };
    var mS = new[] { Ext(r.Measured.Min.X, r.Measured.Max.X), Ext(r.Measured.Min.Y, r.Measured.Max.Y), Ext(r.Measured.Min.Z, r.Measured.Max.Z) };
    float worst = 1f;
    for (int i = 0; i < 3; i++) worst = MathF.Max(worst, MathF.Max(mS[i] / oS[i], oS[i] / mS[i]));
    float maxAbs = 0;
    for (int i = 0; i < 3; i++) maxAbs = MathF.Max(maxAbs, MathF.Abs(mS[i] - oS[i]));
    return (r, worst, maxAbs);
}).ToList();

int degen = allScored.Count(x => Degenerate(x.r.ObndMin, x.r.ObndMax));
int nonSpatial = allScored.Count(x => !Spatial.Contains(Clean(x.r.RecType)));
var scored = allScored.Where(x => Spatial.Contains(Clean(x.r.RecType))
                               && !Degenerate(x.r.ObndMin, x.r.ObndMax)).ToList();

Console.WriteLine($"   excluded: {nonSpatial:N0} non-spatial record types (Projectile/Explosion/Light/…)");
Console.WriteLine($"   excluded: {degen:N0} with a DEGENERATE OBND (zero extent on an axis = absent, not stale)");
Console.WriteLine($"   scoring {scored.Count:N0} meshes on spatial record types with a real OBND\n");

void Band(string label, Func<float, bool> pred)
{
    var hit = scored.Where(x => pred(x.worst)).ToList();
    int van = hit.Count(x => x.r.Vanilla);
    Console.WriteLine($"   {label,-22} {hit.Count,7:N0}  ({100.0 * hit.Count / Math.Max(1, scored.Count),5:0.0}%)"
                    + $"   vanilla-sourced {van,6:N0} | mod-sourced {hit.Count - van,6:N0}");
}
Band("within 5%", w => w < 1.05f);
Band("5-25% off", w => w >= 1.05f && w < 1.25f);
Band("25-100% off", w => w >= 1.25f && w < 2.0f);
Band("2x - 5x off", w => w >= 2.0f && w < 5.0f);
Band(">5x off", w => w >= 5.0f);

// CONTROL vs TEST. Vanilla: plugin and mesh shipped together, so OBND should agree — any disagreement
// here is baseline noise in the method. Mod-sourced: the mesh was replaced without touching the plugin.
// If the standing rule holds, the two distributions separate.
var vanilla = scored.Where(x => x.r.Vanilla).ToList();
var modded = scored.Where(x => !x.r.Vanilla).ToList();
Console.WriteLine($"\n   ---- CONTROL vs TEST ----");
foreach (var (label, set) in new[] { ("CONTROL  mesh from game Data", vanilla), ("TEST     mesh from a mod", modded) })
{
    if (set.Count == 0) { Console.WriteLine($"   {label,-30} (none)"); continue; }
    var ws = set.Select(x => x.worst).OrderBy(v => v).ToArray();
    float Pc(double q) => ws[Math.Min(ws.Length - 1, (int)(q * ws.Length))];
    Console.WriteLine($"   {label,-30} n={set.Count,6:N0}  p50={Pc(.5),6:0.00}x  p90={Pc(.9),6:0.00}x  "
                    + $"p99={Pc(.99),6:0.00}x   >25% off: {100.0 * set.Count(x => x.worst >= 1.25f) / set.Count,5:0.0}%");
}

// The CLEAN test of the replacer hypothesis. "mod-sourced" above is too coarse: USSEP, _ResourcePack,
// and the CC archives all ship a plugin AND its meshes together, so their OBND is not stale at all.
// A true standalone replacer is: MORE THAN ONE source provides this mesh path, and the winner is not
// the game's own Data. That is exactly "somebody overrode a mesh that already existed".
var replaced = scored.Where(x => !x.r.Vanilla && x.r.ProviderCount >= 2).ToList();
var untouched = scored.Where(x => x.r.Vanilla && x.r.ProviderCount == 1).ToList();
Console.WriteLine($"\n   ---- the clean cut: was this mesh OVERRIDDEN? ----");
foreach (var (label, set) in new[] { ("UNTOUCHED  sole provider = Data", untouched),
                                     ("OVERRIDDEN mesh replaced by a mod", replaced) })
{
    if (set.Count == 0) { Console.WriteLine($"   {label,-36} (none)"); continue; }
    var ws = set.Select(x => x.worst).OrderBy(v => v).ToArray();
    float Pc(double q) => ws[Math.Min(ws.Length - 1, (int)(q * ws.Length))];
    Console.WriteLine($"   {label,-36} n={set.Count,6:N0}  p50={Pc(.5),6:0.00}x  p90={Pc(.9),6:0.00}x   "
                    + $">25% off: {100.0 * set.Count(x => x.worst >= 1.25f) / set.Count,5:0.0}%"
                    + $"   >2x off: {100.0 * set.Count(x => x.worst >= 2.0f) / set.Count,5:0.0}%");
}

// The population that actually matters for collision detection: solid architecture and clutter.
// Effects meshes are excluded because a particle emitter's render geometry is not its visible size, so
// a disagreement there is a category difference, not a stale bound — and LOD meshes because they are
// deliberately coarser than the record they hang off.
var Solid = new HashSet<string>(StringComparer.Ordinal) { "Static", "Furniture", "Container", "Door", "Activator" };
bool IsEffectish(string p) => p.Contains(@"effects\", StringComparison.OrdinalIgnoreCase)
                           || p.Contains(@"magic\", StringComparison.OrdinalIgnoreCase)
                           || p.Contains("lod", StringComparison.OrdinalIgnoreCase)
                           || p.Contains(@"sky\", StringComparison.OrdinalIgnoreCase);
var solid = scored.Where(x => Solid.Contains(Clean(x.r.RecType)) && !IsEffectish(x.r.Mesh)).ToList();
if (solid.Count > 0)
{
    var ws = solid.Select(x => x.worst).OrderBy(v => v).ToArray();
    float Pc(double q) => ws[Math.Min(ws.Length - 1, (int)(q * ws.Length))];
    Console.WriteLine($"\n   ---- SOLID GEOMETRY ONLY (architecture + clutter; no effects/magic/sky/LOD) ----");
    Console.WriteLine($"   n={solid.Count:N0}   p50={Pc(.5):0.00}x  p90={Pc(.9):0.00}x  p99={Pc(.99):0.00}x");
    Console.WriteLine($"   within 5%: {100.0 * solid.Count(x => x.worst < 1.05f) / solid.Count,5:0.0}%   "
                    + $">25% off: {100.0 * solid.Count(x => x.worst >= 1.25f) / solid.Count,5:0.0}%   "
                    + $">2x off: {100.0 * solid.Count(x => x.worst >= 2f) / solid.Count,5:0.0}%");
    var sv = solid.Where(x => x.r.Vanilla).ToList();
    var sm = solid.Where(x => !x.r.Vanilla).ToList();
    Console.WriteLine($"      vanilla-sourced n={sv.Count,5:N0}  >25% off: {(sv.Count == 0 ? 0 : 100.0 * sv.Count(x => x.worst >= 1.25f) / sv.Count),5:0.0}%");
    Console.WriteLine($"      mod-sourced     n={sm.Count,5:N0}  >25% off: {(sm.Count == 0 ? 0 : 100.0 * sm.Count(x => x.worst >= 1.25f) / sm.Count),5:0.0}%");
    Console.WriteLine($"\n   ---- 10 worst SOLID disagreements ----");
    foreach (var (r, worst, maxAbs) in solid.OrderByDescending(x => x.worst).Take(10))
        Console.WriteLine($"   {worst,7:0.0}x  d={maxAbs,7:0}u  {(r.Vanilla ? "VAN" : "MOD")}  {Clean(r.RecType),-12} "
                        + $"{Trim(r.Mesh, 46),-46} <- {Trim(r.Provider, 30)}");
}

Console.WriteLine($"\n   ---- 15 worst disagreements (spatial types, real OBND) ----");
foreach (var (r, worst, maxAbs) in scored.OrderByDescending(x => x.worst).Take(15))
    Console.WriteLine($"   {worst,7:0.0}x  d={maxAbs,7:0}u  {(r.Vanilla ? "VAN" : "MOD")}  {Clean(r.RecType),-12} "
                    + $"{Trim(r.Mesh, 46),-46} <- {Trim(r.Provider, 30)}");

Console.WriteLine($"\n   ---- mods contributing the most disagreement (>25% off) ----");
foreach (var g in scored.Where(x => x.worst >= 1.25f && !x.r.Vanilla)
                        .GroupBy(x => x.r.Provider).OrderByDescending(g => g.Count()).Take(12))
    Console.WriteLine($"   {g.Count(),6:N0}  median {g.OrderBy(x => x.worst).ElementAt(g.Count() / 2).worst,7:0.00}x   {Trim(g.Key, 60)}");

var outPath = Path.Combine(Path.GetDirectoryName(Environment.ProcessPath!)!, "obnd_vs_measured.json");
File.WriteAllText(outPath, JsonSerializer.Serialize(
    scored.OrderByDescending(x => x.worst).Take(4000).Select(x => new
    {
        mesh = x.r.Mesh, rec = x.r.RecType, form = x.r.Fk.ToString(), provider = x.r.Provider,
        fromBsa = x.r.FromBsa, vanilla = x.r.Vanilla, providers = x.r.ProviderCount, ratio = MathF.Round(x.worst, 3),
        obnd = new { min = x.r.ObndMin, max = x.r.ObndMax },
        measured = new { min = x.r.Measured.Min, max = x.r.Measured.Max },
        verts = x.r.Verts, tris = x.r.Tris,
    }), new JsonSerializerOptions { WriteIndented = false }));
Console.WriteLine($"\nwrote {outPath}");

void Bump(string k) { errs.TryGetValue(k, out var c); errs[k] = c + 1; }
static string Short(string? s) => s is null ? "?" : (s.Length > 90 ? s[..90] : s);
static string Trim(string s, int n) => s.Length <= n ? s : "..." + s[^(n - 3)..];

record Row(string Mesh, string RecType, FormKey Fk, Vec3 ObndMin, Vec3 ObndMax,
           Aabb Measured, int Verts, int Tris, string Provider, bool FromBsa, int ProviderCount, bool Vanilla, Aabb Coll);
