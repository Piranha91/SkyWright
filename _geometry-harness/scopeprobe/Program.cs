using System.Diagnostics;
using System.Reflection;
using System.Text.Json;
using HarnessShared;
using HousecarlCore;
using HousecarlCore.Progress;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Plugins.Records;
using Mutagen.Bethesda.Skyrim;

// SCOPEPROBE — what a target plugin actually CONTAINS, measured, so a checklist is never guessed (Q42, roadmap 4.23).
//
// WHY IT EXISTS. The evaluation controller must decide which checks a mod needs. That decision cannot come from a
// list of record types in a tool: the moment a tool carries such a list, coverage becomes something someone has to
// finish by hand, which is the failure the whole rebuild exists to end. So the FOOTPRINT is measured here, by
// construction, out of Mutagen's own record enumeration, and the registry's triggers read the measurement. This
// probe names no record type anywhere. It groups what it finds by each record's own registration signature, read
// off the registration Mutagen itself carries; a signature it cannot resolve is COUNTED and named, never dropped.
//
// WHAT IT REPORTS, per target plugin (PLAN D2 of _claude/runs/2026-09-12-evaluation-controller/PLAN.md):
//   recordTypes   every major record type present, with new / override counts (override = the record's defining
//                 ModKey is not the target's). From EnumerateMajorRecords(), grouped by signature.
//   placed        what the target files under a cell, read with the group walk PlaceIndex makes: the persistent
//                 and temporary children of interior cells, a worldspace's persistent (top) cell, and the exterior
//                 sub-block cells; NAVM and LAND counted; the exterior grid cells listed; worldspaces named.
//   expansion     the four-neighbour exterior cells of every touched exterior cell; each placed door's teleport
//                 destination resolved IN THE WINNING ORDER through PlaceIndex to the cell that holds it, with the
//                 ones that resolve to nothing COUNTED; outbound form links by the master that defines them.
//   masters, scripts, assets, nexusId
//                 the masters and the content hash come from the resolved configuration's own identity, so the
//                 scope and the emits agree by construction. Scripts are records carrying a VirtualMachineAdapter,
//                 counted through Mutagen's IHaveVirtualMachineAdapterGetter. Assets are the model paths the
//                 target's records carry through IModeledGetter (count, and the first N paths).
//   unmeasured    stated, never blank: the extents this probe does NOT read. The registry answers UNKNOWN for any
//                 trigger reading one of them, and unknown never becomes "not applicable".
//
// WHAT IT IS NOT. It judges nothing. A footprint is not a finding: "this mod places 4,000 references" is not a
// conflict, and a mod with an empty footprint is not thereby compatible. It also does not resolve asset paths
// against the VFS (that is housecarl_asset_status) and does not read scripts (only counts their carriers).
//
// usage: scopeprobe --target X.esp [--target Y.esp]... [--order FILE | --enable X.esp | --disable X.esp]
//                   [--json FILE] [--assets N] [--instance DIR] [--profile NAME] [--game DIR]
//
//   --order/--enable/--disable  the configuration to read the targets in, through HarnessOrderCli — the same three
//                   sources every other probe takes. A configuration the engine would refuse is REFUSED here too:
//                   exit 2, the reason printed, and NO scope.json written. A scope file for an order that cannot
//                   load would be a footprint of a game that cannot start.
//   --json FILE     where scope.json goes. Without it the summary is printed and nothing is written.
//   --assets N      how many model paths to list (default 50; the count is always complete).
//
// EXIT CODES: 0 complete, 1 written but incomplete (walk failures named in the file and on the console), 2 refused.

using var __run = RunLog.BeginProcess("scopeprobe", args);

string Instance = HarnessTarget.Instance;
string GamePath = HarnessTarget.Game;
string profileName = HarnessTarget.Profile;
string? orderFile = null;
string? jsonPath = null;
int assetsListed = 50;
var targetNames = new List<string>();
var enablePlugins = new List<string>();
var disablePlugins = new List<string>();

for (int i = 0; i < args.Length; i++)
{
    if (args[i] == "--instance" && i + 1 < args.Length) Instance = args[++i];
    else if (args[i] == "--game" && i + 1 < args.Length) GamePath = args[++i];
    else if (args[i] == "--profile" && i + 1 < args.Length) profileName = args[++i];
    else if (args[i] == "--order" && i + 1 < args.Length) orderFile = args[++i];
    else if (args[i] == "--enable" && i + 1 < args.Length) enablePlugins.Add(args[++i]);
    else if (args[i] == "--disable" && i + 1 < args.Length) disablePlugins.Add(args[++i]);
    else if (args[i] == "--target" && i + 1 < args.Length) targetNames.Add(args[++i]);
    else if (args[i] == "--json" && i + 1 < args.Length) jsonPath = args[++i];
    else if (args[i] == "--assets" && i + 1 < args.Length)
    {
        if (!int.TryParse(args[++i], out assetsListed) || assetsListed < 0)
        { Console.Error.WriteLine($"--assets needs a whole number (0 or more); got '{args[i]}'."); return 2; }
    }
    else if (args[i].StartsWith("--"))
    { Console.Error.WriteLine($"unknown option '{args[i]}'. See the header of scopeprobe/Program.cs for the flag list."); return 2; }
    else targetNames.Add(args[i]);
}

if (targetNames.Count == 0)
{
    Console.Error.WriteLine("scopeprobe needs at least one --target Plugin.esp: the footprint is a fact about a "
                          + "NAMED plugin, and a scope with no target would be a checklist derived from nothing.");
    return 2;
}

Instance = HarnessTarget.Require(Instance, "MO2 instance directory", "instance", "--instance");
GamePath = HarnessTarget.Require(GamePath, "game directory", "game", "--game");
profileName = HarnessTarget.Require(profileName, "MO2 profile name", "profile", "--profile");

string profileDir = Path.Combine(Instance, "profiles", profileName);
string modsDir = Path.Combine(Instance, "mods");
string overwriteDir = Path.Combine(Instance, "overwrite");
string dataDir = Path.Combine(GamePath, "Data");

var sw = Stopwatch.StartNew();
var cfg = HarnessOrderCli.Resolve(profileName, profileDir, modsDir, dataDir, overwriteDir, GamePath,
                                  orderFile, enablePlugins, disablePlugins);
if (cfg.Refused)
{
    // The word REFUSED is what the controller's receipt validator looks for in the log (PLAN D4's `refused` and
    // `illegal-configuration` causes), so it is printed here whether or not the reason happens to carry it.
    Console.Error.WriteLine("REFUSED: " + cfg.RefusedReason);
    Console.Error.WriteLine("no scope.json was written: a footprint measured in an order the engine would not load "
                          + "describes a game that cannot start.");
    return 2;
}
HarnessOrderCli.Print(cfg);

var identity = cfg.Identity("load-time", "baseline");
var byName = new Dictionary<string, ConfigPlugin>(StringComparer.OrdinalIgnoreCase);
foreach (var p in cfg.Plugins) byName[p.Name] = p;
var providerKind = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
foreach (var v in cfg.Providers) providerKind[v.Name] = v.Kind;

// ------------------------------------------------------------------------------------------------------------
// Pass 1 — each target's own footprint.
// ------------------------------------------------------------------------------------------------------------
var walkFailures = new List<string>();
var targetJson = new List<object>();
var summaries = new List<TargetSummary>();
var destinationDoors = new HashSet<FormKey>();          // teleport destinations, resolved in pass 2
var perTargetDoors = new List<(string Target, List<(FormKey From, FormKey To)> Links)>();

__run.Feature("targets", targetNames.Count, "targets")
     .Stage("footprint", "walking targets", targetNames.Count, "targets");

foreach (var wanted in targetNames)
{
    if (!byName.TryGetValue(wanted, out var plugin))
    {
        walkFailures.Add($"'{wanted}' is not in the resolved configuration ({cfg.Plugins.Count} plugins, {cfg.Source}); "
                       + "its footprint was not measured, so nothing below describes it");
        continue;
    }

    ISkyrimModGetter mod;
    try { mod = LoadOrderResolver.OpenOverlay(plugin.Path, dataDir); }
    catch (Exception ex)
    {
        walkFailures.Add($"'{wanted}' could not be opened — {Concise(ex)}; its footprint was not measured");
        continue;
    }

    try
    {
        var self = mod.ModKey;
        var types = new SortedDictionary<string, long[]>(StringComparer.Ordinal);   // sig -> [new, override]
        var classNames = new Dictionary<string, string>(StringComparer.Ordinal);
        var linksByMaster = new SortedDictionary<string, long>(StringComparer.OrdinalIgnoreCase);
        var assetPaths = new List<string>();
        long assetCount = 0, scriptCarriers = 0, records = 0;
        int signatureUnresolved = 0;

        foreach (var rec in mod.EnumerateMajorRecords())
        {
            records++;
            var (sig, cls, resolved) = SignatureOf(rec.GetType());
            if (!resolved) signatureUnresolved++;
            if (!types.TryGetValue(sig, out var counts)) types[sig] = counts = new long[2];
            counts[rec.FormKey.ModKey == self ? 0 : 1]++;
            classNames[sig] = cls;

            if (rec is IHaveVirtualMachineAdapterGetter { VirtualMachineAdapter: not null }) scriptCarriers++;
            if (rec is IModeledGetter { Model: { } model } && model.File.ToString() is { Length: > 0 } path)
            {
                assetCount++;
                if (assetPaths.Count < assetsListed) assetPaths.Add(path);
            }
            foreach (var link in rec.EnumerateFormLinks())
            {
                if (link.FormKey.IsNull) continue;
                var master = link.FormKey.ModKey.FileName.String;
                linksByMaster[master] = linksByMaster.TryGetValue(master, out var n) ? n + 1 : 1;
            }
        }

        // The group walk PlaceIndex makes, over this one plugin: what it FILES, and where.
        var placed = new PlacedWalk();
        var doors = new List<(FormKey From, FormKey To)>();
        try { placed.Walk(mod, doors); }
        catch (Exception ex)
        {
            walkFailures.Add($"'{wanted}' could not be walked for its placements — {Concise(ex)}; its placement "
                           + "footprint is not reported, so no placement trigger reads it");
            continue;
        }
        foreach (var d in doors) destinationDoors.Add(d.To);
        perTargetDoors.Add((plugin.Name, doors));

        int nexusId = 0;
        string? providerNote = null;
        providerKind.TryGetValue(plugin.Provider, out var kind);
        if (kind == "mod")
        {
            var meta = Mo2ModMeta.Read(Path.Combine(modsDir, plugin.Provider, "meta.ini"));
            if (meta is null) providerNote = "the provider mod folder has no readable meta.ini";
            else if (meta.ModId <= 0) providerNote = "the provider mod folder's meta.ini records no Nexus mod id";
            else nexusId = meta.ModId;
        }
        else providerNote = $"the provider is '{plugin.Provider}' ({kind ?? "unknown kind"}), not a mod folder, "
                          + "so there is no meta.ini to read a Nexus id from";

        var report = placed.Report();
        summaries.Add(new TargetSummary(
            plugin.Name, plugin.Sha256, plugin.Provider, kind ?? "unknown", records, types.Count,
            plugin.Masters.Count, scriptCarriers, assetCount, nexusId > 0 ? nexusId : null,
            report.Total, report.Interior, report.Exterior, report.Persistent, report.Navmesh, report.Land,
            report.Cells.Count, report.ExteriorCells, report.Worldspaces.Count));

        targetJson.Add(new
        {
            name = plugin.Name,
            sha256 = plugin.Sha256,
            bytes = plugin.Bytes,
            provider = plugin.Provider,
            providerKind = kind ?? "unknown",
            masterFlag = plugin.MasterFlag,
            lightFlag = plugin.LightFlag,
            footprint = new
            {
                records,
                recordTypes = types.ToDictionary(
                    t => t.Key,
                    t => (object)new { @new = t.Value[0], @override = t.Value[1], className = classNames[t.Key] }),
                signatureUnresolved,
                placed = report.ToJson(),
                linksByMaster,
                masters = plugin.Masters,
                scripts = new { carriers = scriptCarriers },
                assets = new { modelPaths = assetCount, listed = assetPaths },
                nexusId = nexusId > 0 ? nexusId : (int?)null,
                nexusIdNote = providerNote,
            },
        });
    }
    finally { (mod as IDisposable)?.Dispose(); }
}

// ------------------------------------------------------------------------------------------------------------
// Pass 2 — expansion: where the targets reach outside their own cells.
// ------------------------------------------------------------------------------------------------------------
// Door destinations are resolved IN THE WINNING ORDER, not in the target: a door points at another door reference,
// and which cell THAT reference is filed in is a fact about the whole order. PlaceIndex is the existing walk for
// exactly that question, restricted here to the destination doors so a big order does not build a whole map.
__run.Stage("expansion", "resolving destinations", 1, "walk");
PlaceIndex? places = null;
if (destinationDoors.Count > 0)
{
    try { places = PlaceIndex.Build(cfg.OrderedPaths, dataDir, destinationDoors); }
    catch (Exception ex)
    {
        walkFailures.Add($"the winning-order place walk failed — {Concise(ex)}; no door destination was resolved, "
                       + "so the destination cells are missing rather than empty");
    }
}
if (places is not null)
    foreach (var f in places.WalkFailures) walkFailures.Add("door-destination walk: " + f);

var expansions = new List<object>();
long doorsResolved = 0, doorsUnresolved = 0;
foreach (var (target, doors) in perTargetDoors)
{
    var resolved = new List<object>();
    int unresolved = 0;
    foreach (var (from, to) in doors)
    {
        var place = places?.PlaceOf(to);
        if (place is null) { unresolved++; continue; }
        resolved.Add(new { door = from.ToString(), destinationDoor = to.ToString(), cell = place });
    }
    doorsResolved += resolved.Count;
    doorsUnresolved += unresolved;
    expansions.Add(new { target, doors = doors.Count, doorDestinations = resolved, unresolved });
}

// Four-neighbour expansion of every touched exterior cell, over all targets together: the neighbours a change can
// reach across a cell boundary. Cells the targets already touch are not repeated.
var touched = new HashSet<(string Ws, int X, int Y)>();
foreach (var s in summaries)
    foreach (var c in s.ExteriorCells)
        touched.Add((c.Worldspace, c.X, c.Y));
var neighbours = new List<object>();
var seenNeighbour = new HashSet<(string, int, int)>();
foreach (var (ws, x, y) in touched)
    foreach (var (dx, dy) in new[] { (1, 0), (-1, 0), (0, 1), (0, -1) })
        if (!touched.Contains((ws, x + dx, y + dy)) && seenNeighbour.Add((ws, x + dx, y + dy)))
            neighbours.Add(new { worldspace = ws, x = x + dx, y = y + dy });

bool complete = walkFailures.Count == 0 && summaries.Count == targetNames.Count;

// The extents this probe does not read. Stated, never blank (PLAN D2, amended A-1): each is a key from the CLOSED
// extent vocabulary with one sentence saying why. A registry check that declares `dependsOnExtents` naming one of
// these answers UNKNOWN, whatever its trigger says — and unknown never becomes "not applicable".
var unmeasured = new List<object>
{
    new { extent = "script-placement", why = "a Papyrus script can place, move or delete references at runtime; this probe reads records only, so the placement footprint omits anything a script puts there." },
    new { extent = "alias-fill", why = "a quest alias resolves to a reference chosen at runtime, so the actor or object a quest actually moves is not in this footprint." },
    new { extent = "bos-swaps", why = "a *_SWAP.ini replaces a reference's base object at load; the record types and model paths counted here are the RECORD's, not the swapped base's." },
    new { extent = "asset-replacers", why = "a mod that only ships meshes or textures has no records to enumerate and so no footprint here, though it changes what the game draws; model paths are read off records and never resolved against the VFS." },
    new { extent = "generated-output", why = "LOD, occlusion and patcher output generated from an earlier placement is not read, so whether any of it is now stale is outside this measurement." },
};
if (doorsUnresolved > 0)
    unmeasured.Add(new { extent = "door-destinations", why = $"{doorsUnresolved} placed door(s) name a destination the winning order does not file in any cell; where those doors lead is not measured." });

var scope = new
{
    schemaVersion = 1,
    probe = "scopeprobe",
    generatedUtc = DateTime.UtcNow.ToString("o"),
    complete,
    walkFailures,
    targets = targetJson,
    expansion = new
    {
        perTarget = expansions,
        doorsResolved,
        doorsUnresolved,
        neighbourCells = neighbours,
        note = "outbound form links are counted per target under its footprint's linksByMaster; a link into the "
             + "target's own ModKey is internal and counted there too.",
    },
    unmeasured,
    limits = new[]
    {
        "model paths are read off records and never resolved against the asset view, so a path inside a BSA and a path that exists nowhere read alike here.",
        "nexusId is MO2's cached meta.ini value; a hand-installed, merged or FOMOD-built mod folder has none, and its absence is not evidence that the mod is not on Nexus.",
        "the record enumeration is Mutagen's, so a record type Mutagen does not model is absent from recordTypes rather than reported as unknown; signatureUnresolved counts the types whose signature would not resolve.",
    },
    configuration = new
    {
        fingerprint = identity.Fingerprint,
        source = identity.Source,
        population = identity.Population,
        state = identity.State,
        plugins = identity.Plugins.Select(p => new { p.Name, p.Path, p.Sha256, p.Bytes, p.Provider }).ToList(),
        providers = identity.Providers.Select(p => new { p.Name, p.Kind }).ToList(),
        overlays = identity.Overlays.Select(o => new { o.Kind, o.Path, o.Sha256 }).ToList(),
        limits = identity.Limits,
        unknown = identity.Unknown,
    },
    wallSeconds = Math.Round(sw.Elapsed.TotalSeconds, 2),
};

Console.WriteLine();
foreach (var s in summaries)
{
    Console.WriteLine($"{s.Name}  {s.Sha256[..12]}  provider '{s.Provider}' ({s.ProviderKind})");
    Console.WriteLine($"  records {s.Records:N0} in {s.TypeCount} types   masters {s.Masters}   "
                    + $"scripts {s.Scripts:N0}   model paths {s.Assets:N0}   "
                    + $"nexus id {(s.NexusId is null ? "none" : s.NexusId.ToString())}");
    Console.WriteLine($"  placed {s.Placed:N0} (interior {s.Interior:N0}, exterior {s.Exterior:N0}, "
                    + $"persistent {s.Persistent:N0})   navmesh {s.Navmesh:N0}   land {s.Land:N0}   "
                    + $"cells {s.Cells:N0}   exterior grid cells {s.ExteriorCells.Count:N0}   "
                    + $"worldspaces {s.Worldspaces}");
}
Console.WriteLine($"  door destinations: {doorsResolved} resolved, {doorsUnresolved} unresolved");
Console.WriteLine($"  four-neighbour expansion: {neighbours.Count} exterior cell(s) adjacent to the footprint");
Console.WriteLine($"  unmeasured extents stated: {unmeasured.Count}");
foreach (var f in walkFailures) Console.WriteLine("  WALK FAILURE: " + f);
Console.WriteLine($"  complete: {(complete ? "true" : "false — the footprint is partial, so every trigger over it reads unknown")}");
Console.WriteLine($"  configuration {identity.Short}");
Console.WriteLine($"  {sw.Elapsed.TotalSeconds:0.00} s");

if (jsonPath is not null)
{
    var dir = Path.GetDirectoryName(Path.GetFullPath(jsonPath));
    if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
    File.WriteAllText(jsonPath, JsonSerializer.Serialize(scope, new JsonSerializerOptions { WriteIndented = true }));
    Console.WriteLine($"\nwrote {jsonPath}");
}
return complete ? 0 : 1;

static string Concise(Exception ex) => ex.Message.Split('\n')[0].Trim();

// The record's own signature, from the registration Mutagen carries. No list of record types: the corpus generator
// reads TriggeringRecordType off StaticRegistration and cross-checks GrupRecordType, and this is the same read,
// cached per runtime type. A type whose signature will not resolve is reported under its class name and counted in
// signatureUnresolved — visible, never silently folded into another bucket.
static (string Sig, string Class, bool Resolved) SignatureOf(Type runtime)
{
    if (SigCache.Map.TryGetValue(runtime, out var hit)) return hit;
    var cls = runtime.Name.EndsWith("BinaryOverlay", StringComparison.Ordinal)
        ? runtime.Name[..^"BinaryOverlay".Length]
        : runtime.Name;
    string? sig = null;
    try
    {
        var reg = runtime.GetProperty("StaticRegistration", BindingFlags.Public | BindingFlags.Static)?.GetValue(null);
        var field = reg?.GetType().GetField("TriggeringRecordType",
            BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static);
        if (field?.GetValue(reg) is RecordType rt) sig = rt.ToString();
    }
    catch { /* fall through to GrupRecordType */ }
    if (sig is null)
    {
        var grup = runtime.GetField("GrupRecordType", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);
        if (grup?.GetValue(null) is RecordType g) sig = g.ToString();
    }
    var answer = sig is null ? (cls, cls, false) : (sig, cls, true);
    SigCache.Map[runtime] = answer;
    return answer;
}

static class SigCache
{
    public static readonly Dictionary<Type, (string, string, bool)> Map = new();
}

/// <summary>The console's and the expansion's view of one target — typed, so neither reads the JSON tree back.</summary>
sealed record TargetSummary(
    string Name, string Sha256, string Provider, string ProviderKind, long Records, int TypeCount, int Masters,
    long Scripts, long Assets, int? NexusId, long Placed, long Interior, long Exterior, long Persistent,
    long Navmesh, long Land, int Cells, IReadOnlyList<GridCell> ExteriorCells, int Worldspaces);

sealed record GridCell(string Worldspace, int X, int Y);

/// <summary>One target's placement footprint: the counts, the cells and the grid cells.</summary>
sealed record PlacedReport(
    long Total, long Interior, long Exterior, long Persistent, long Navmesh, long Land, int LandscapeUnreadable,
    IReadOnlyList<object> Cells, IReadOnlyList<GridCell> ExteriorCells, IReadOnlyList<string> Worldspaces)
{
    public object ToJson() => new
    {
        total = Total, interior = Interior, exterior = Exterior, persistent = Persistent,
        navm = Navmesh, land = Land, landscapeUnreadable = LandscapeUnreadable,
        cells = Cells,
        exteriorCells = ExteriorCells.Select(c => new { worldspace = c.Worldspace, x = c.X, y = c.Y }).ToList(),
        worldspaces = Worldspaces,
    };
}

// The placement walk: the same group tree PlaceIndex walks (its WalkPlugin), kept to ONE plugin and to counts.
// Persistent and temporary children of interior cells; a worldspace's persistent (top) cell; the exterior
// sub-block cells; NAVM and LAND. A record filed nowhere (an item, a spell, a quest) is not placed.
sealed class PlacedWalk
{
    readonly Dictionary<FormKey, CellCount> _cells = new();
    readonly List<FormKey> _worldspaces = new();
    readonly Dictionary<FormKey, string> _worldspaceNames = new();

    sealed class CellCount
    {
        public string Kind = "";
        public string? EditorId;
        public FormKey Worldspace;
        public bool HasGrid;
        public int X, Y;
        public long Refs, Navmesh, Land;
    }

    long _total, _interior, _exterior, _persistent, _navmesh, _land;
    int _landscapeUnreadable;

    public void Walk(ISkyrimModGetter mod, List<(FormKey From, FormKey To)> doors)
    {
        foreach (var ws in mod.Worldspaces.Records)
        {
            _worldspaces.Add(ws.FormKey);
            _worldspaceNames[ws.FormKey] = string.IsNullOrEmpty(ws.EditorID) ? ws.FormKey.ToString() : ws.EditorID!;
            if (ws.TopCell is { } tc) Cell(tc, "persistent", ws.FormKey, doors);
            foreach (var blk in ws.SubCells)
                foreach (var sub in blk.Items)
                    foreach (var c in sub.Items)
                        Cell(c, "exterior", ws.FormKey, doors);
        }
        foreach (var blk in mod.Cells.Records)
            foreach (var sub in blk.SubBlocks)
                foreach (var c in sub.Cells)
                    Cell(c, "interior", FormKey.Null, doors);
    }

    void Cell(ICellGetter c, string kind, FormKey ws, List<(FormKey From, FormKey To)> doors)
    {
        if (!_cells.TryGetValue(c.FormKey, out var rec))
        {
            _cells[c.FormKey] = rec = new CellCount { Kind = kind, Worldspace = ws };
            if (kind == "exterior" && c.Grid?.Point is { } gp) { rec.HasGrid = true; rec.X = gp.X; rec.Y = gp.Y; }
        }
        if (rec.EditorId is null && !string.IsNullOrEmpty(c.EditorID)) rec.EditorId = c.EditorID;

        foreach (var r in c.Persistent) Ref(rec, kind, r, doors);
        foreach (var r in c.Temporary) Ref(rec, kind, r, doors);
        foreach (var n in c.NavigationMeshes) { _ = n; rec.Navmesh++; _navmesh++; _total++; Bucket(kind); }
        ILandscapeGetter? land = null;
        try { land = c.Landscape; } catch { _landscapeUnreadable++; }
        if (land is not null) { rec.Land++; _land++; _total++; Bucket(kind); }
    }

    void Ref(CellCount rec, string kind, IPlacedGetter r, List<(FormKey From, FormKey To)> doors)
    {
        rec.Refs++; _total++; Bucket(kind);
        if (r is IPlacedObjectGetter { TeleportDestination: { } dest } && !dest.Door.FormKey.IsNull)
            doors.Add((r.FormKey, dest.Door.FormKey));
    }

    void Bucket(string kind)
    {
        if (kind == "interior") _interior++;
        else if (kind == "exterior") _exterior++;
        else _persistent++;
    }

    public PlacedReport Report() => new(
        _total, _interior, _exterior, _persistent, _navmesh, _land, _landscapeUnreadable,
        _cells.Select(kv => (object)new
        {
            cell = kv.Key.ToString(),
            kind = kv.Value.Kind,
            name = kv.Value.EditorId,
            worldspace = kv.Value.Worldspace.IsNull ? null : Name(kv.Value.Worldspace),
            x = kv.Value.HasGrid ? kv.Value.X : (int?)null,
            y = kv.Value.HasGrid ? kv.Value.Y : (int?)null,
            refs = kv.Value.Refs,
            navmesh = kv.Value.Navmesh,
            land = kv.Value.Land,
        }).ToList(),
        _cells.Values.Where(v => v.HasGrid).Select(v => new GridCell(Name(v.Worldspace), v.X, v.Y)).ToList(),
        _worldspaces.Select(Name).ToList());

    string Name(FormKey ws) => ws.IsNull ? "" : (_worldspaceNames.TryGetValue(ws, out var n) ? n : ws.ToString());
}
