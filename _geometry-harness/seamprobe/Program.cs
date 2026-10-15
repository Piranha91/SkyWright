using System.Diagnostics;
using System.Text.Json;
using HarnessShared;
using HousecarlCore;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Skyrim;
using HousecarlCore.Progress;

// LANDSCAPE SEAMS — mismatched heights and textures where two cells meet (roadmap 4.9, Part B; 2026-09-04).
//
// The engine draws each cell's LAND on its own from its 33×33 heightmap; vertex 32 of one cell is
// vertex 0 of the next, and when the two records disagree about that shared vertex the ground shows a
// step or a gap. Two mods re-shaping adjacent cells produce it; so does one mod re-shaping a cell without
// matching the neighbour it left alone; so, in places, does Bethesda. This pass measures every
// edge-adjacent pair — max |Δ| and the count of vertices over ε along the shared edge — and says whose
// the seam is, with PlacementHistory's authoring-view rule applied to LAND: the later plugin's own cell
// against the other side as the later plugin could see it (DetectorModel.LandSeam). Introduced+Blind is
// the compatibility shape — each side matches what it saw, the combination does not, and a patch that
// re-cuts one edge repairs it. Introduced+Seen is a mod's own seam against a neighbour it could see.
// Inherited pre-dates the later plugin's edit. Textures (the base layer of the quadrants along the edge)
// are a seam of their own, reported apart, never folded into the height verdict.
//
// Heights come from HousecarlCore.LandHeight — one decode for the renderer and the detector. The
// facts come from CellBoundaryIndex, shared with waterprobe; the decisions are DetectorModel's.
//
// Standing rules: the vanilla floor (both winners Bethesda) measured and printed before any class count
// (rule 12); the distribution at ≥8 / ≥32 / ≥128 u carried on every line, because 8 u is one heightmap
// unit and a lip the eye barely catches while 128 u is a wall; unjudgeable counted (rule 6); every emitted
// row carries its physical predicate (the edge still mismatches under this arm) and --base re-measures.
//
// --labelled X.esp: the labelled-set check. With X in the order, the edges its LAND overrides touch are
// the positives; the same edges under the order WITHOUT X (--disable X.esp on a second run, or the
// differential sweep) are what the rule should flag. This run prints, for the edges X touches, how many
// read as seams under THIS order, against the rate on vanilla edges X leaves alone — the confusion table.
//
// NOT verified in game. Part C looks at a station.
//
// usage: seamprobe [--list N] [--eps U] [--near X,Y] [--worldspace EDID] [--labelled X.esp] [--json FILE] [--emit FILE] [--base EMIT]
//                  [--enable X.esp]* [--disable X.esp]* [--instance DIR] [--game DIR] [--profile NAME]

using var __run = RunLog.BeginProcess("seamprobe", args);
if (args.Length >= 1 && args[0] == "--self-test") return SeamSelfTest.Run();

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
float eps = 8f;
(int X, int Y)? near = null;
string? worldFilter = null;
string? labelled = null;
string? jsonPath = null, emitPath = null, basePath = null;
var enablePlugins = new List<string>();
var disablePlugins = new List<string>();
string? orderFile = null;
for (int i = 0; i < args.Length; i++)
{
    if (args[i] == "--list" && i + 1 < args.Length) listN = int.Parse(args[++i]);
    else if (args[i] == "--eps" && i + 1 < args.Length) eps = float.Parse(args[++i]);
    else if (args[i] == "--near" && i + 1 < args.Length) { var s = args[++i].Split(','); near = (int.Parse(s[0]), int.Parse(s[1])); }
    else if (args[i] == "--worldspace" && i + 1 < args.Length) worldFilter = args[++i];
    else if (args[i] == "--labelled" && i + 1 < args.Length) labelled = args[++i];
    else if (args[i] == "--json" && i + 1 < args.Length) jsonPath = args[++i];
    else if (args[i] == "--emit" && i + 1 < args.Length) emitPath = args[++i];
    else if (args[i] == "--base" && i + 1 < args.Length) basePath = args[++i];
    else if (args[i] == "--order" && i + 1 < args.Length) orderFile = args[++i];
    else if (args[i] == "--enable" && i + 1 < args.Length) enablePlugins.Add(args[++i]);
    else if (args[i] == "--disable" && i + 1 < args.Length) disablePlugins.Add(args[++i]);
}

Console.OutputEncoding = System.Text.Encoding.UTF8;
var sw = Stopwatch.StartNew();
// WHAT THIS RUN EVALUATES, decided in one place (_shared/HarnessOrderCli.cs): the real profile, a --enable/--disable
// what-if, or a --order candidate file - and, in the same step, the ASSET VIEW that configuration implies, so a run
// whose plugins changed no longer reads the profile's meshes and BSAs regardless. The identity it produces is written
// into the emit, so a reader (and conflictdiff) can see exactly what was consumed instead of inferring it.
var cfg = HarnessOrderCli.Resolve(profileName, profileDir, modsDir, dataDir, overwriteDir, GamePath,
                                  orderFile, enablePlugins, disablePlugins);
if (cfg.Refused) { Console.Error.WriteLine(cfg.RefusedReason); return 2; }
var paths = cfg.OrderedPaths;
Console.WriteLine($"seamprobe — {Instance} / {profileName}");
Console.WriteLine($"load order: {paths.Count} plugins ({sw.ElapsedMilliseconds} ms)   eps {eps:0.#} u (one heightmap unit is {LandHeight.Unit:0} u)\n");
HarnessOrderCli.Print(cfg);

// ---------------------------------------------------------------------------------------
// Pass 1 — every plugin once, into the shared index; the master closure for the authoring view.
// ---------------------------------------------------------------------------------------
var idx = new CellBoundaryIndex();
var pluginKey = new ModKey[paths.Count];
var masterIdx = new HashSet<int>[paths.Count];
var unreadable = new List<string>();
__run.Feature("plugins", paths.Count, "plugins").Stage("scan", "reading plugins", paths.Count, "plugins");

for (int p = 0; p < paths.Count; p++)
{
    __run.Advance();
    ISkyrimModGetter mod;
    try { mod = SkyrimMod.CreateFromBinaryOverlay(paths[p], SkyrimRelease.SkyrimSE); }
    catch { unreadable.Add(Path.GetFileName(paths[p])); pluginKey[p] = ModKey.Null; masterIdx[p] = new HashSet<int>(); continue; }
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
        idx.Ingest(p, mod, land: true);
    }
    if ((p + 1) % 150 == 0) Console.WriteLine($"   ... {p + 1}/{paths.Count} plugins, {idx.LandSnaps:N0} LAND snaps, {sw.Elapsed.TotalSeconds:0}s");
}
var closure = DetectorModel.MasterClosure(masterIdx);
var landCells = SeamFindings.LandCells(idx);
int heightlessCells = landCells.Count(c => c.LandWinnerDeclaresNone);
Console.WriteLine($"indexed {landCells.Count:N0} exterior cells with a LAND over {idx.LandSnaps:N0} distinct statements; "
                + $"{landCells.Sum(c => c.LandUnreadable):N0} unreadable LAND provision(s)  ({sw.Elapsed.TotalSeconds:0.0}s)");
Console.WriteLine($"   LAND provisions declaring no heights: {idx.LandDeclaresNoneProvisions:N0}; cells whose winning LAND declares none "
                + $"(no ground; their edges are not measured): {heightlessCells:N0}");
Console.WriteLine($"   plugins that could not be read (contribute nothing, counted per rule 6): {unreadable.Count:N0}" + (unreadable.Count > 0 ? "  — " + string.Join(", ", unreadable) : ""));

PluginKind Kind(int p) => idx.KindOf(p);
string PluginName(int p) => idx.PluginName(p);
string WsName(FormKey ws) => idx.WorldEditorId.TryGetValue(ws, out var e) && e is { Length: > 0 } ? e : ws.ToString();
string CellLabel(CellInfo c) => $"{WsName(c.Worldspace)} ({c.GX},{c.GY}){(c.EditorId is { } e ? " " + e : "")}";
// --labelled: the cells the plugin states a LAND for, read from its FILE whether or not it is loaded — the negative arm
// (--disable it) has to measure the SAME edges, and those are the plugin's, not the order's.
int labelledIdx = labelled is null ? -1 : Array.FindIndex(pluginKey, k => string.Equals(k.FileName.String, labelled, StringComparison.OrdinalIgnoreCase));
var labelledCells = new HashSet<FormKey>();
if (labelled is not null)
{
    string? lp = labelledIdx >= 0 ? paths[labelledIdx]
               : Directory.EnumerateFiles(modsDir, labelled, SearchOption.AllDirectories).FirstOrDefault()
                 ?? (File.Exists(Path.Combine(dataDir, labelled)) ? Path.Combine(dataDir, labelled) : null);
    if (lp is null) Console.WriteLine($"   ! --labelled {labelled}: not in this order and not found under the mods folder or Data — no confusion table");
    else
    {
        try
        {
            using var lm = SkyrimMod.CreateFromBinaryOverlay(lp, SkyrimRelease.SkyrimSE);
            foreach (var ws in lm.Worldspaces.Records)
                foreach (var blk in ws.SubCells) foreach (var sub in blk.Items) foreach (var c in sub.Items)
                    if (c.Landscape?.VertexHeightMap is not null) labelledCells.Add(c.FormKey);
        }
        catch (Exception ex) { Console.WriteLine($"   ! --labelled {labelled}: could not be read ({ex.GetType().Name}) — no confusion table"); }
        Console.WriteLine($"   --labelled {labelled}: {labelledCells.Count:N0} cells with a LAND in the file{(labelledIdx >= 0 ? " (loaded in this order)" : " (NOT loaded in this order — the negative arm)")}");
    }
}

// Who changed LAND at all — the census the class sits on.
Console.WriteLine($"\n==== LAND OVERRIDES that changed heights or textures, by plugin ====");
var landChangers = landCells.SelectMany(c => c.Land.Skip(1).Select(l => l.Plugin)).GroupBy(p => p).OrderByDescending(g => g.Count()).ToList();
foreach (var g in landChangers.Take(listN)) Console.WriteLine($"   {g.Count(),6}  {PluginName(g.Key),-52} {Kind(g.Key)}");

// ---------------------------------------------------------------------------------------
// Pass 2 — every edge-adjacent pair.
// ---------------------------------------------------------------------------------------
var (pairs, dupSkipped, heightlessPairs) = SeamFindings.MeasurePairs(idx, landCells, closure, eps, Kind);
Console.WriteLine($"\nmeasured {pairs.Count:N0} edge-adjacent pairs ({dupSkipped:N0} cells at duplicate grid points skipped; "
                + $"{heightlessPairs:N0} pairs not measurable, a winning LAND declaring no heights)  ({sw.Elapsed.TotalSeconds:0.0}s)");

// ---------------------------------------------------------------------------------------
// The floor, then the class.
// ---------------------------------------------------------------------------------------
string Dist(IEnumerable<Pair> ps)
{
    var l = ps.ToList(); int n = l.Count;
    if (n == 0) return "(none)";
    int a8 = l.Count(p => p.MaxDelta >= 8f), a32 = l.Count(p => p.MaxDelta >= 32f), a128 = l.Count(p => p.MaxDelta >= 128f);
    return $"{n,7:N0} pairs — ≥8 u {a8:N0} ({(double)a8 / n:P2}), ≥32 u {a32:N0} ({(double)a32 / n:P2}), ≥128 u {a128:N0} ({(double)a128 / n:P3}); textures differ on {l.Count(p => p.TextureSeam):N0} ({(double)l.Count(p => p.TextureSeam) / n:P2})";
}
Console.WriteLine($"\n==== THE VANILLA FLOOR — both winners Bethesda ====");
Console.WriteLine($"   all worldspaces: {Dist(pairs.Where(p => p.BothBethesda))}");
foreach (var g in pairs.Where(p => p.BothBethesda).GroupBy(p => p.A.Worldspace).OrderByDescending(g => g.Count()).Take(6))
    Console.WriteLine($"   {WsName(g.Key),-24} {Dist(g)}");
var floorRows = pairs.Where(p => p.BothBethesda && p.Verdict == LandSeamVerdict.Seam).OrderByDescending(p => p.MaxDelta).ToList();
Console.WriteLine($"   the largest vanilla seams (control candidates — Bethesda ships these):");
foreach (var p in floorRows.Take(8))
    Console.WriteLine($"      {CellLabel(p.A),-40} {p.Dir} {CellLabel(p.B),-40} Δ {p.MaxDelta,6:0} u over {p.OverEps,2} vtx — {p.Origin}/{p.Sight} by {PluginName(p.Later)}");

Console.WriteLine($"\n==== POPULATION SPLIT (rule 12, by the later winner) ====");
foreach (var g in pairs.GroupBy(p => DetectorModel.ClassifyLandSeamPopulation(Kind(p.Later))).OrderBy(g => g.Key))
    Console.WriteLine($"   {g.Key,-14} {Dist(g)}");

var cls = SeamFindings.Class(pairs);
Console.WriteLine($"\n==== THE CLASS — mod-involved pairs whose shared edge mismatches by more than {eps:0.#} u: {cls.Count:N0} ====");
foreach (var g in cls.GroupBy(p => (p.Origin, p.Sight)).OrderBy(g => g.Key))
    Console.WriteLine($"   {g.Key.Origin}/{g.Key.Sight,-6} {Dist(g)}");
Console.WriteLine($"   unjudgeable: {pairs.Count(p => p.Verdict == LandSeamVerdict.Unjudgeable):N0}; view/winner disagreement counted on Blind rows: {cls.Count(p => p.Sight == SeamSight.Blind && p.Seen):N0}");
Console.WriteLine($"\n   top mover pairs (later winner × other winner), Introduced rows:");
foreach (var g in cls.Where(p => p.Origin == SeamOrigin.Introduced).GroupBy(p => (L: p.Later, O: p.OtherCell.Land[^1].Plugin)).OrderByDescending(g => g.Count()).Take(listN))
    Console.WriteLine($"   {g.Count(),5}  {PluginName(g.Key.L),-44} × {PluginName(g.Key.O),-44} {g.Count(p => p.Sight == SeamSight.Blind)} Blind, max Δ {g.Max(p => p.MaxDelta):0} u");
double Rank(CellInfo c)
{
    if (near is { } n) return Math.Max(Math.Abs(c.GX - n.X), Math.Abs(c.GY - n.Y)) + (worldFilter is not null && !string.Equals(WsName(c.Worldspace), worldFilter, StringComparison.OrdinalIgnoreCase) ? 10_000 : 0);
    return 0;
}
Console.WriteLine($"\n   rows, by Δ{(near is { } nn ? $" near ({nn.X},{nn.Y})" : "")}:");
foreach (var p in cls.OrderBy(p => Rank(p.A)).ThenByDescending(p => p.MaxDelta).Take(listN))
    Console.WriteLine($"   {CellLabel(p.A),-40} {p.Dir} {CellLabel(p.B),-40} Δ {p.MaxDelta,6:0} u over {p.OverEps,2} vtx  {p.Origin}/{p.Sight}  later {PluginName(p.Later)} vs {PluginName(p.OtherCell.Land[^1].Plugin)}\n        {p.Why}{(p.TextureSeam ? $"; texture seam ({p.TexWhy})" : "")}");

var texOnly = pairs.Where(p => !p.BothBethesda && p.TextureSeam && p.Verdict == LandSeamVerdict.None).ToList();
Console.WriteLine($"\n==== TEXTURE SEAMS apart from height — mod-involved pairs whose base layers differ across the edge with heights matching: {texOnly.Count:N0} (vanilla floor {pairs.Count(p => p.BothBethesda && p.TextureSeam && p.Verdict == LandSeamVerdict.None):N0}) ====");
foreach (var g in texOnly.GroupBy(p => p.Later).OrderByDescending(g => g.Count()).Take(10))
    Console.WriteLine($"   {g.Count(),5}  {PluginName(g.Key)}");

// ---------------------------------------------------------------------------------------
// --labelled: the confusion table over the plugin's own LAND overrides.
// ---------------------------------------------------------------------------------------
if (labelledCells.Count > 0)
{
    var touched = pairs.Where(p => labelledCells.Contains(p.A.Key) || labelledCells.Contains(p.B.Key)).ToList();
    var untouched = pairs.Where(p => p.BothBethesda && !labelledCells.Contains(p.A.Key) && !labelledCells.Contains(p.B.Key)).ToList();
    string arm = labelledIdx >= 0 ? "the plugin LOADED" : "the plugin ABSENT (the positive arm: what it was written to fix)";
    Console.WriteLine($"\n==== LABELLED SET — {labelled}, {arm}: edges touching a cell it states a LAND for ({touched.Count:N0}) vs vanilla edges it leaves alone ({untouched.Count:N0}) ====");
    Console.WriteLine($"   touched edges: seams (≥ eps) {touched.Count(p => p.MaxDelta > eps):N0} ({Rate(touched, p => p.MaxDelta > eps)}), ≥32 u {touched.Count(p => p.MaxDelta >= 32f):N0}, ≥128 u {touched.Count(p => p.MaxDelta >= 128f):N0}; texture seams {touched.Count(p => p.TextureSeam):N0} ({Rate(touched, p => p.TextureSeam)})");
    Console.WriteLine($"   untouched vanilla edges: seams {untouched.Count(p => p.MaxDelta > eps):N0} ({Rate(untouched, p => p.MaxDelta > eps)}), ≥32 u {untouched.Count(p => p.MaxDelta >= 32f):N0}, ≥128 u {untouched.Count(p => p.MaxDelta >= 128f):N0}; texture seams {untouched.Count(p => p.TextureSeam):N0} ({Rate(untouched, p => p.TextureSeam)})");
    Console.WriteLine($"   touched edges by verdict: " + string.Join(", ", touched.GroupBy(p => p.Verdict + (p.Origin is { } o ? "/" + o : "")).OrderBy(g => g.Key).Select(g => $"{g.Key} {g.Count():N0}")));
    Console.WriteLine($"   run both arms (--disable \"{labelled}\" for the absent one) and compare the two touched-edge lines; the differential (--emit both, conflictdiff) credits the repairs.");
    foreach (var p in touched.Where(p => p.MaxDelta > eps).OrderByDescending(p => p.MaxDelta).Take(10))
        Console.WriteLine($"      {CellLabel(p.A),-40} {p.Dir} {CellLabel(p.B),-40} Δ {p.MaxDelta,6:0} u over {p.OverEps,2} vtx  {p.Verdict}{(p.Origin is { } o2 ? "/" + o2 : "")}  winners {PluginName(p.AWin)} | {PluginName(p.BWin)}");
}
static string Rate(List<Pair> l, Func<Pair, bool> f) => l.Count == 0 ? "n/a" : ((double)l.Count(f) / l.Count).ToString("P2");

// ---------------------------------------------------------------------------------------
// --emit / --base / --json
// ---------------------------------------------------------------------------------------
if (emitPath is not null)
{
    var conflicts = new List<Conflict>();
    foreach (var p in cls)
        conflicts.Add(new Conflict("LAND-SEAM", PluginName(p.OtherCell.Land[^1].Plugin), PluginName(p.Later),
            $"{CellLabel(p.A)} {p.Dir} {CellLabel(p.B)}: Δ {p.MaxDelta:0} u over {p.OverEps} vertices, {p.Origin}/{p.Sight} — {p.Why}",
            new List<ConflictRecord> { new(p.LaterCell.Key.ToString(), "cell", CellLabel(p.LaterCell), PhysicalState.Holds, $"the shared edge mismatches by {p.MaxDelta:0} u under this arm") },
            new List<ConflictRecord> { new(p.OtherCell.Key.ToString(), "cell", CellLabel(p.OtherCell)) }));
    List<Predicate>? predicates = null;
    if (basePath is not null)
    {
        var baseFile = ConflictIo.Read(basePath);
        predicates = SeamFindings.Predicates(baseFile.Conflicts, pairs, eps);
        Console.WriteLine($"\n   --base {Path.GetFileName(basePath)}: {predicates.Count:N0} base-row predicate(s) re-measured under this order — "
                        + string.Join(", ", predicates.GroupBy(p => p.Physical).OrderBy(g => g.Key).Select(g => $"{g.Key} {g.Count():N0}")));
    }
    var coverage = new List<CoverageLine>
    {
        new("plugins", paths.Count - unreadable.Count, new Dictionary<string, long> { ["UnreadablePlugin"] = unreadable.Count }),
        new("exterior cells with a LAND", landCells.Count - heightlessCells, new Dictionary<string, long> { ["UnreadableLand"] = landCells.Sum(c => c.LandUnreadable), ["DeclaresNoHeights"] = heightlessCells }, new Dictionary<string, long> { ["DuplicateGridPoint"] = dupSkipped }),
        new("edge-adjacent pairs", pairs.Count(p => p.Verdict != LandSeamVerdict.Unjudgeable), new Dictionary<string, long> { ["Undecodable"] = pairs.Count(p => p.Verdict == LandSeamVerdict.Unjudgeable), ["DeclaresNoHeights"] = heightlessPairs },
            new Dictionary<string, long> { ["BethesdaFloor"] = pairs.Count(p => p.BothBethesda) }, $"class {cls.Count:N0} at eps {eps:0.#}"),
    };
    // LAND heightmaps carry no placed references, so no enable population applies; said rather than left null.
    ConflictIo.Write(emitPath, new ConflictFile("seamprobe", Instance, DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm:ssZ"), conflicts, coverage, predicates, basePath,
                                                "n/a (LAND heightmaps; no placed-reference population)", "n/a",
                                                cfg.Identity("n/a (LAND heightmaps; no placed-reference population)", "n/a")));
    Console.WriteLine($"\nemitted {conflicts.Count:N0} LAND-SEAM rows -> {emitPath}");
}

if (jsonPath is not null)
{
    var dump = new
    {
        instance = Instance, profile = profileName, plugins = paths.Count, unreadable, eps,
        pairs = pairs.Select(p => new
        {
            a = p.A.Key.ToString(), b = p.B.Key.ToString(), aLabel = CellLabel(p.A), bLabel = CellLabel(p.B), dir = p.Dir,
            aWinner = PluginName(p.AWin), bWinner = PluginName(p.BWin), later = PluginName(p.Later),
            maxDelta = p.MaxDelta, overEps = p.OverEps, viewDelta = p.ViewDelta, prevDelta = p.PrevDelta, otherSeen = p.Seen,
            verdict = p.Verdict.ToString(), origin = p.Origin?.ToString(), sight = p.Sight?.ToString(), why = p.Why,
            textureSeam = p.TextureSeam, texWhy = p.TexWhy, bothBethesda = p.BothBethesda,
        }),
    };
    File.WriteAllText(jsonPath, JsonSerializer.Serialize(dump, new JsonSerializerOptions { WriteIndented = true }));
    Console.WriteLine($"json -> {jsonPath}");
}

Console.WriteLine($"\ndone in {sw.Elapsed.TotalSeconds:0.0}s");
return 0;

sealed record Pair(CellBoundaryIndex.LandPairMeasure M, string Dir, int AWin, int BWin, bool BothBethesda)
{
    public CellInfo A => M.A; public CellInfo B => M.B; public CellInfo LaterCell => M.LaterCell; public CellInfo OtherCell => M.OtherCell; public int Later => M.Later;
    public float MaxDelta => M.MaxDelta; public int OverEps => M.OverEps; public float ViewDelta => M.ViewDelta; public float? PrevDelta => M.PrevDelta; public bool Seen => M.OtherSeen;
    public LandSeamVerdict Verdict => M.Verdict; public SeamOrigin? Origin => M.Origin; public SeamSight? Sight => M.Sight; public string Why => M.Why;
    public bool TextureSeam => M.TextureSeam; public string TexWhy => M.TexWhy;
}
