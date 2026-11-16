using System.Buffers.Binary;
using System.Diagnostics;
using System.Reflection;
using System.Text;
using System.Text.Json;
using HarnessShared;
using HousecarlCore;
using HousecarlCore.Progress;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Plugins.Binary.Headers;
using Mutagen.Bethesda.Plugins.Binary.Translations;
using Mutagen.Bethesda.Plugins.Meta;
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
//   --ring N        also name the plugins whose CELL records sit within N grid cells of an exterior footprint cell
//                   (nearby.ring in scope.json; default 0, none). The loaded grid is 2.
//   --also-scan F   a plugin file outside the configuration (a disabled mod's) to check, records only, for CELL records
//                   in the footprint or its ring (nearby.alsoScanned); repeatable. It is never loaded into the order.
//
//   unreadableRecords
//                 a target holding a record Mutagen cannot read is not measured: the record is named here (plugin,
//                 FormKey, signature, why, and whether the file's bytes located it or it is the record being read),
//                 a walk failure says so in one sentence, and the scope is incomplete. Never an unhandled exception.
//
// EXIT CODES: 0 complete, 1 written but incomplete (walk failures named in the file and on the console), 2 refused.

// The probe's own output encoding: without it the bytes follow whatever code page the console was left in.
Console.OutputEncoding = Encoding.UTF8;
using var __run = RunLog.BeginProcess("scopeprobe", args);
if (args.Length >= 1 && args[0] == "--self-test") return ScopeSelfTest.Run();

string Instance = HarnessTarget.Instance;
string GamePath = HarnessTarget.Game;
string profileName = HarnessTarget.Profile;
string? orderFile = null;
string? jsonPath = null;
int assetsListed = 50;
int ring = 0;
var alsoScan = new List<string>();
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
    else if (args[i] == "--ring" && i + 1 < args.Length)
    {
        if (!int.TryParse(args[++i], out ring) || ring < 0 || ring > 5)
        { Console.Error.WriteLine($"--ring needs a whole number from 0 to 5; got '{args[i]}'."); return 2; }
    }
    else if (args[i] == "--also-scan" && i + 1 < args.Length) alsoScan.Add(args[++i]);
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
if (cfg.Refused) return ScopeOutcome.Refuse(cfg.RefusedReason);
HarnessOrderCli.Print(cfg);

var identity = cfg.Identity("load-time", "baseline");
var byName = new Dictionary<string, ConfigPlugin>(StringComparer.OrdinalIgnoreCase);
foreach (var p in cfg.Plugins) byName[p.Name] = p;
var providerKind = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
foreach (var v in cfg.Providers) providerKind[v.Name] = v.Kind;

// ------------------------------------------------------------------------------------------------------------
// Pass 1 — each target's own footprint.
// ------------------------------------------------------------------------------------------------------------
var lists = new FootprintLists();
var walkFailures = lists.WalkFailures;
var unreadableRecords = lists.UnreadableRecords;          // records a target holds that cannot be read, each named
var targetJson = lists.TargetJson;
var summaries = lists.Summaries;
var targetPlacedReports = lists.TargetPlacedReports;
var destinationDoors = lists.DestinationDoors;          // teleport destinations, resolved in pass 2
var perTargetDoors = lists.PerTargetDoors;

__run.Feature("targets", targetNames.Count, "targets")
     .Stage("footprint", "walking targets", targetNames.Count, "targets");

foreach (var wanted in targetNames)
    TargetFootprint.Walk(wanted, byName, cfg.Plugins.Count, cfg.Source, providerKind, dataDir, modsDir, assetsListed, lists);

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
        walkFailures.Add($"the winning-order place walk failed — {TargetFootprint.Concise(ex)}; no door destination was resolved, "
                       + "so the destination cells are missing rather than empty");
    }
}
if (places is not null)
    foreach (var f in places.WalkFailures) walkFailures.Add("door-destination walk: " + f);

var (expansions, doorsResolved, doorsUnresolved) = ScopeOutcome.Doors(perTargetDoors, to => places?.PlaceOf(to));

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

bool complete = ScopeOutcome.Complete(walkFailures.Count, summaries.Count, targetNames.Count);

// The small load-order slice for the exact cells/grid coordinates in the target footprints. This walks every
// plugin, but retains only membership and record kinds at those named locations; it is not a conflict detector.
var contributorFailures = new List<string>();
// summaries and targetPlacedReports are appended together, one per target FOUND; targetNames also holds absent targets.
foreach (var (summary, report) in summaries.Zip(targetPlacedReports))
    if (report.PersistentRefsWithoutPlacement > 0)
        contributorFailures.Add($"'{summary.Name}' has {report.PersistentRefsWithoutPlacement:N0} persistent placed record(s) without placement coordinates; those records could not be assigned to a local worldspace grid");
var wantedCells = targetPlacedReports.SelectMany(x => x.CellScopes).Where(x => x.Kind != "persistent")
    .GroupBy(x => x.Cell).ToDictionary(x => x.Key, x => x.First());
var wantedGrids = targetPlacedReports.SelectMany(x => x.CellScopes).Where(x => x.X is not null && x.Y is not null)
    .GroupBy(x => (x.WorldspaceKey ?? "", x.X!.Value, x.Y!.Value)).ToDictionary(x => x.Key, x => x.ToList());
var contributors = wantedCells.Values.ToDictionary(x => x.Cell, _ => new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase));
// --ring: the exterior grid points within `ring` cells of a footprint grid point, not in the footprint, and per plugin
// the cells it carries there (a CELL record is how a plugin places, moves or edits anything in a cell)
var ringGrids = new HashSet<(string, int, int)>();
if (ring > 0)
    foreach (var (ws0, x0, y0) in wantedGrids.Keys)
        for (int dx = -ring; dx <= ring; dx++)
            for (int dy = -ring; dy <= ring; dy++)
                if (!wantedGrids.ContainsKey((ws0, x0 + dx, y0 + dy))) ringGrids.Add((ws0, x0 + dx, y0 + dy));
var ringCarriers = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
bool InRing(FormKey worldspace, ICellGetter cell, bool persistent)
    => ring > 0 && !persistent && !worldspace.IsNull && cell.Grid?.Point is { } rp && ringGrids.Contains((worldspace.ToString(), rp.X, rp.Y));
var worldHeaders = new Dictionary<FormKey, RecordFields>();
var localLargeReferences = new Dictionary<(FormKey Worldspace, int X, int Y), string>();
var localMaxHeights = new Dictionary<(FormKey Worldspace, int X, int Y), string?>();
var worldHeaderAudit = new List<object>();
var worldspaceWide = new List<object>();
const int WorldHeaderReadDepth = 16;
RecordFields ReadWorldHeader(IWorldspaceGetter ws)
{
    var childFields = OwnedChildContent.Fields(ws).Keys.ToHashSet(StringComparer.Ordinal);
    // Mutagen exposes the worldspace child-group seek table/header beside the modeled WRLD fields. They describe
    // where SubCells are stored, rather than a change to the worldspace itself, and therefore travel with an
    // otherwise unchanged carrier. Keep that engine-container metadata out of the parent-record comparison.
    // Pinned Mutagen 0.54.4 source: OffsetData is the rebuildable OFST seek cache; SubCellsTimestamp and
    // SubCellsUnknown are read from and written to the WorldChildren GRUP header (Worldspace.cs 58-59, 146-147).
    // LargeReferences (RNAM rows keyed by GridPosition) and MaxHeight (MHDT, one entry per grid cell) are compared
    // per measured grid below, not as whole tables.
    // VersionControl, Version2 and FormVersion are the main-record header's VCS1, VCS2 and form-version words, shared
    // by every record type. xEdit (wbImplementation.pas, dev-4.1.5, TwbMainRecordStruct and the override copy path)
    // resets VCS1 and VCS2 to defaults whenever it copies a record as an override: they are bookkeeping, not content.
    // FormVersion is the serialization format version the Creation Kit wrote (43 LE, 44 SE), not worldspace content.
    childFields.UnionWith(new[] { nameof(IWorldspaceGetter.OffsetData), nameof(IWorldspaceGetter.SubCellsTimestamp),
        nameof(IWorldspaceGetter.SubCellsUnknown), nameof(IWorldspaceGetter.LargeReferences),
        nameof(IWorldspaceGetter.MaxHeight), nameof(IMajorRecordGetter.VersionControl),
        nameof(ISkyrimMajorRecordGetter.Version2), nameof(ISkyrimMajorRecordGetter.FormVersion) });
    var root = ReadEngine.ReadFields(ws, null, depth: 1, containerHint: null);
    var parentNames = root.Fields.Select(x => x.Path).Where(x => !childFields.Contains(x)).ToList();
    return ReadEngine.ReadFields(ws, parentNames, depth: WorldHeaderReadDepth, containerHint: null);
}
// One grid cell's MaxHeight (MHDT) entry as hex, or null when the table is absent or does not cover the cell.
// Mutagen 0.54.4 models CellData as an opaque byte array (Worldspace.xml WorldspaceMaxHeight); xEdit (wbDefinitionsTES5.pas)
// names its element as four quad-height bytes per cell. Neither states the cell order, so it was measured: on
// Skyrim.esm Tamriel terrain against Update.esm's table, Min..Max is inclusive (120 x 95 cells x 4 = 45,600 bytes exactly)
// and the cells run column-major, index (x - Min.X) * height + (y - Min.Y): per-cell terrain maximum correlates
// r = 0.977 with that mapping and |r| < 0.01 with row-major.
// A length that does not match the bounds throws, so the caller reports the entry as unreadable instead of guessing.
string? MaxHeightEntry(IWorldspaceMaxHeightGetter? mh, int x, int y)
{
    if (mh is null) return null;
    int minX = mh.Min.X, minY = mh.Min.Y, width = mh.Max.X - minX + 1, height = mh.Max.Y - minY + 1;
    if (width <= 0 || height <= 0 || mh.CellData.Length != width * height * 4)
        throw new InvalidDataException($"MHDT CellData has {mh.CellData.Length} bytes for bounds {mh.Min.X},{mh.Min.Y}..{mh.Max.X},{mh.Max.Y}; "
                                       + $"the measured layout needs {Math.Max(width, 0) * Math.Max(height, 0) * 4}");
    if (x < minX || x >= minX + width || y < minY || y >= minY + height) return null;
    int i = ((x - minX) * height + (y - minY)) * 4;
    return Convert.ToHexString(mh.CellData.Slice(i, 4));
}
void AddContributor(string cell, string plugin, string kind)
{
    if (!contributors.TryGetValue(cell, out var byPlugin)) return;
    if (!byPlugin.TryGetValue(plugin, out var kindsAtPlace)) byPlugin[plugin] = kindsAtPlace = new HashSet<string>(StringComparer.Ordinal);
    kindsAtPlace.Add(kind);
}
__run.Stage("contributors", "reading which plugins edit the footprint's cells", cfg.Plugins.Count + alsoScan.Count, "plugins");
foreach (var plugin in cfg.Plugins)
{
    __run.Advance();
    ISkyrimModGetter mod;
    try { mod = LoadOrderResolver.OpenOverlay(plugin.Path, dataDir); }
    catch (Exception ex) { contributorFailures.Add($"'{plugin.Name}' could not be opened for the location-contributor walk — {TargetFootprint.Concise(ex)}"); continue; }
    try
    {
        void ScanCell(ICellGetter cell, FormKey worldspace, bool persistent)
        {
            string cellKey = cell.FormKey.ToString();
            var destinations = new List<ScopeCell>();
            if (wantedCells.TryGetValue(cellKey, out var exact)) destinations.Add(exact);
            if (!persistent && !worldspace.IsNull && cell.Grid?.Point is { } gp
                && wantedGrids.TryGetValue((worldspace.ToString(), gp.X, gp.Y), out var gridDestinations))
                destinations.AddRange(gridDestinations);
            if (InRing(worldspace, cell, persistent))
            {
                if (!ringCarriers.TryGetValue(plugin.Name, out var rc)) ringCarriers[plugin.Name] = rc = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                rc.Add(cellKey);
            }
            foreach (var destination in destinations.DistinctBy(x => x.Cell))
            {
                AddContributor(destination.Cell, plugin.Name, "CELL");
                if (cell.NavigationMeshes.Any()) AddContributor(destination.Cell, plugin.Name, "NAVM");
                try { if (cell.Landscape is not null) AddContributor(destination.Cell, plugin.Name, "LAND"); }
                catch { contributorFailures.Add($"'{plugin.Name}' has unreadable LAND in {cellKey}"); }
                if (cell.Persistent.Any() || cell.Temporary.Any()) AddContributor(destination.Cell, plugin.Name, "placed-record");
            }
        }
        foreach (var ws in mod.Worldspaces.Records)
        {
            var cellsInWs = wantedCells.Values.Where(x => string.Equals(x.WorldspaceKey, ws.FormKey.ToString(), StringComparison.OrdinalIgnoreCase)).ToList();
            if (cellsInWs.Count > 0)
            {
                var header = ReadWorldHeader(ws);
                bool readable = header.Fields.All(x => x.Readable && x.Path != "…");
                bool changed = !worldHeaders.TryGetValue(ws.FormKey, out var previous);
                if (previous is not null)
                {
                    var diff = FieldsDiff.Compare(header, previous, "previous provider");
                    readable &= diff.Complete;
                    changed = diff.Deltas.Count > 0;
                    worldHeaderAudit.Add(new { worldspace = ws.FormKey.ToString(), plugin = plugin.Name,
                        status = readable ? (changed ? "changed" : "unchanged-container-excluded") : "unmeasured",
                        deltas = readable && changed ? diff.Deltas.Take(3).Select(x => x.Length <= 240 ? x : x[..240] + "…").ToList() : new List<string>(),
                        unreadable = header.Fields.Where(x => !x.Readable || x.Path == "…").Select(x => new { x.Path, x.Note }).ToList() });
                }
                else worldHeaderAudit.Add(new { worldspace = ws.FormKey.ToString(), plugin = plugin.Name,
                    status = readable ? "definition" : "unmeasured", deltas = new List<string>(),
                    unreadable = header.Fields.Where(x => !x.Readable || x.Path == "…").Select(x => new { x.Path, x.Note }).ToList() });
                // A parent WRLD field (climate, water, flags, ...) applies to the whole worldspace. It is recorded as
                // worldspace-wide, never stamped onto each measured cell: that would claim a locality nobody measured.
                if (!readable)
                    contributorFailures.Add($"'{plugin.Name}' has an incomplete parent-field read for worldspace {ws.FormKey}; its WRLD contribution could not be classified");
                else if (changed)
                    worldspaceWide.Add(new { worldspace = ws.FormKey.ToString(), plugin = plugin.Name,
                        change = previous is null ? "definition" : "parent-fields" });
                worldHeaders[ws.FormKey] = header;

                // MaxHeight: compare this cell's own entry only. A table added or removed as a whole, or bounds that
                // start or stop covering the cell, is a worldspace-wide change, not evidence at the cell.
                bool maxHeightPresenceChanged = false;
                foreach (var gridGroup in cellsInWs.Where(x => x.X is not null && x.Y is not null)
                                                     .GroupBy(x => (x.X!.Value, x.Y!.Value)))
                {
                    var key = (ws.FormKey, gridGroup.Key.Item1, gridGroup.Key.Item2);
                    string? current;
                    try { current = MaxHeightEntry(ws.MaxHeight, key.Item2, key.Item3); }
                    catch (Exception ex)
                    {
                        contributorFailures.Add($"'{plugin.Name}' has an unreadable MaxHeight entry at worldspace {ws.FormKey} grid {key.Item2},{key.Item3} — {TargetFootprint.Concise(ex)}");
                        localMaxHeights.Remove(key);
                        continue;
                    }
                    bool hadPrevious = localMaxHeights.TryGetValue(key, out var previousEntry);
                    if (hadPrevious && (previousEntry is null) != (current is null)) maxHeightPresenceChanged = true;
                    else if ((hadPrevious && previousEntry is not null && !string.Equals(previousEntry, current, StringComparison.Ordinal))
                             || (!hadPrevious && current is not null))
                        foreach (var destination in gridGroup) AddContributor(destination.Cell, plugin.Name, "WRLD-max-height");
                    localMaxHeights[key] = current;
                }
                if (maxHeightPresenceChanged)
                    worldspaceWide.Add(new { worldspace = ws.FormKey.ToString(), plugin = plugin.Name,
                        change = "max-height-coverage" });

                // Worldspace.xml defines LargeReferences as RNAM rows with GridPosition and reference/position
                // children. Compare only rows at measured coordinates; a table delta elsewhere is not local evidence.
                foreach (var gridGroup in cellsInWs.Where(x => x.X is not null && x.Y is not null)
                                                     .GroupBy(x => (x.X!.Value, x.Y!.Value)))
                {
                    var key = (ws.FormKey, gridGroup.Key.Item1, gridGroup.Key.Item2);
                    string current;
                    try
                    {
                        current = string.Join("|", ws.LargeReferences
                            .Where(x => x.GridPosition.X == key.Item2 && x.GridPosition.Y == key.Item3)
                            .SelectMany(x => x.References)
                            .Select(x => $"{x.Reference.FormKey}@{x.Position.X},{x.Position.Y}")
                            .Order(StringComparer.Ordinal));
                    }
                    catch (Exception ex)
                    {
                        contributorFailures.Add($"'{plugin.Name}' has unreadable large-reference entries at worldspace {ws.FormKey} grid {key.Item2},{key.Item3} — {TargetFootprint.Concise(ex)}");
                        continue;
                    }
                    bool hadPrevious = localLargeReferences.TryGetValue(key, out var previousLocal);
                    if ((hadPrevious && !string.Equals(previousLocal, current, StringComparison.Ordinal)) || (!hadPrevious && current.Length > 0))
                        foreach (var destination in gridGroup) AddContributor(destination.Cell, plugin.Name, "WRLD-large-reference");
                    localLargeReferences[key] = current;
                }
            }
            if (ws.TopCell is { } top) ScanCell(top, ws.FormKey, true);
            foreach (var block in ws.SubCells) foreach (var sub in block.Items) foreach (var cell in sub.Items) ScanCell(cell, ws.FormKey, false);
        }
        foreach (var block in mod.Cells.Records) foreach (var sub in block.SubBlocks) foreach (var cell in sub.Cells) ScanCell(cell, FormKey.Null, false);
        foreach (var cp in PlacementExtract.FromMod(mod))
            if (!cp.Worldspace.IsNull)
                foreach (var placedRef in cp.Refs.Where(x => x.Persistent))
                {
                    var grid = (cp.Worldspace.ToString(), (int)MathF.Floor(placedRef.Position.X / 4096f),
                                         (int)MathF.Floor(placedRef.Position.Y / 4096f));
                    if (wantedGrids.TryGetValue(grid, out var destinations))
                        foreach (var destination in destinations)
                            AddContributor(destination.Cell, plugin.Name, "persistent-placed-record");
                }
    }
    catch (Exception ex) { contributorFailures.Add($"'{plugin.Name}' could not be walked for location contributors — {TargetFootprint.Concise(ex)}"); }
    finally { (mod as IDisposable)?.Dispose(); }
}
// The map's completeness is its own. It is kept out of walkFailures and the top-level `complete`, which say whether the
// target FOOTPRINT is whole (PLAN D2): evalrun reads walkFailures to recognise a legal target omission, and a
// contributor hiccup in some unrelated plugin must not turn the footprint's triggers unknown. The console and the
// JSON both state it; a consumer of the map reads locationContributors.complete.
// --also-scan: plugin files outside the configuration, read for their CELL records alone, never loaded into the order
var alsoScanned = new List<object>();
foreach (var extra in alsoScan)
{
    __run.Advance();
    var footprintHits = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    var ringHits = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    string? failed = null;
    ISkyrimModGetter? xm = null;
    try
    {
        xm = LoadOrderResolver.OpenOverlay(extra, dataDir);
        void Hit(ICellGetter cell, FormKey worldspace, bool persistent)
        {
            bool inFootprint = wantedCells.ContainsKey(cell.FormKey.ToString())
                || (!persistent && !worldspace.IsNull && cell.Grid?.Point is { } gp && wantedGrids.ContainsKey((worldspace.ToString(), gp.X, gp.Y)));
            if (inFootprint) footprintHits.Add(cell.FormKey.ToString());
            else if (InRing(worldspace, cell, persistent)) ringHits.Add(cell.FormKey.ToString());
        }
        foreach (var ws in xm.Worldspaces.Records)
        {
            if (ws.TopCell is { } top) Hit(top, ws.FormKey, true);
            foreach (var block in ws.SubCells) foreach (var sub in block.Items) foreach (var cell in sub.Items) Hit(cell, ws.FormKey, false);
        }
        foreach (var block in xm.Cells.Records) foreach (var sub in block.SubBlocks) foreach (var cell in sub.Cells) Hit(cell, FormKey.Null, false);
    }
    catch (Exception ex) { failed = TargetFootprint.Concise(ex); }
    finally { (xm as IDisposable)?.Dispose(); }
    alsoScanned.Add(new { name = Path.GetFileName(extra), path = extra, footprintCells = footprintHits.Order().ToList(), ringCells = ringHits.Order().ToList(), unreadable = failed });
}
var nearby = new
{
    ring,
    plugins = ringCarriers.Select(kv => new { name = kv.Key, cells = kv.Value.Order().ToList() }).OrderBy(x => x.name, StringComparer.OrdinalIgnoreCase).ToList(),
    alsoScanned,
    note = "a plugin listed here carries a CELL record within the ring of an exterior footprint cell, outside the footprint itself; "
         + "persistent references placed into the ring are not read, and alsoScanned plugins were read for CELL records only.",
};

bool contributorsComplete = complete && contributorFailures.Count == 0;
var locationContributors = new
{
    complete = contributorsComplete,
    locations = wantedCells.Values.Select(c => new
    {
        cell = c.Cell, kind = c.Kind, name = c.Name, worldspace = c.Worldspace, worldspaceKey = c.WorldspaceKey, x = c.X, y = c.Y,
        plugins = contributors[c.Cell].Select(p => new { name = p.Key, recordKinds = p.Value.Order().ToList() }).ToList()
    }).ToList(),
    limits = new[] { "Local world-record contributors only; asset-only replacers and runtime-created placements are outside this map.",
                     "A location lists only evidence indexed to that cell or grid coordinate. A WRLD parent-field change (climate, water, flags, ...) applies to the whole worldspace and is listed under worldspaceWide, not at each location. Rebuildable OFST data, WorldChildren group metadata, record-header version bookkeeping (VCS1, VCS2, form version) and unchanged containers are excluded.",
                     "Large-reference rows and MaxHeight entries are compared only at measured grid coordinates; a change to the same table elsewhere adds no local contributor, and a MaxHeight table added or removed as a whole is worldspace-wide." },
    missing = contributorFailures,
    worldspaceWide,
    worldspaceHeaderAudit = worldHeaderAudit,
};

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
if (ScopeOutcome.DoorExtent(doorsUnresolved) is { } doorExtent) unmeasured.Add(doorExtent);

var scope = new
{
    schemaVersion = 1,
    probe = "scopeprobe",
    generatedUtc = DateTime.UtcNow.ToString("o"),
    complete,
    walkFailures,
    unreadableRecords,
    targets = targetJson,
    locationContributors,
    nearby,
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
Console.WriteLine($"  location contributors: {locationContributors.locations.Count} location(s), "
                + $"{locationContributors.locations.SelectMany(l => l.plugins).Select(p => p.name).Distinct(StringComparer.OrdinalIgnoreCase).Count()} plugin(s), "
                + $"{worldspaceWide.Count} worldspace-wide change(s); "
                + (contributorsComplete ? "complete" : $"INCOMPLETE — {contributorFailures.Count} unclassified input(s), see locationContributors.missing"));
foreach (var f in contributorFailures) Console.WriteLine("  CONTRIBUTOR FAILURE: " + f);
Console.WriteLine($"  configuration {identity.Short}");
Console.WriteLine($"  {sw.Elapsed.TotalSeconds:0.00} s");

if (jsonPath is not null)
{
    var dir = Path.GetDirectoryName(Path.GetFullPath(jsonPath));
    if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
    File.WriteAllText(jsonPath, JsonSerializer.Serialize(scope, new JsonSerializerOptions { WriteIndented = true }));
    Console.WriteLine($"\nwrote {jsonPath}");
}
return ScopeOutcome.ExitCode(complete);

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
sealed record ScopeCell(string Cell, string Kind, string? Name, string? Worldspace, string? WorldspaceKey, int? X, int? Y,
                        long Refs, long Navmesh, long Land);

/// <summary>One target's placement footprint: the counts, the cells and the grid cells.</summary>
sealed record PlacedReport(
    long Total, long Interior, long Exterior, long Persistent, long Navmesh, long Land, int LandscapeUnreadable,
    long PersistentRefs, long PersistentRefsMapped, long PersistentRefsWithoutPlacement,
    IReadOnlyList<ScopeCell> CellScopes, IReadOnlyList<GridCell> ExteriorCells, IReadOnlyList<string> Worldspaces)
{
    public IReadOnlyList<ScopeCell> Cells => CellScopes;
    public object ToJson() => new
    {
        total = Total, interior = Interior, exterior = Exterior, persistent = Persistent,
        navm = Navmesh, land = Land, landscapeUnreadable = LandscapeUnreadable,
        persistentRefs = PersistentRefs, persistentRefsMapped = PersistentRefsMapped,
        persistentRefsWithoutPlacement = PersistentRefsWithoutPlacement,
        cells = CellScopes.Select(c => new { cell = c.Cell, kind = c.Kind, name = c.Name, worldspace = c.Worldspace, worldspaceKey = c.WorldspaceKey,
                                             x = c.X, y = c.Y, refs = c.Refs, navmesh = c.Navmesh, land = c.Land }).ToList(),
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
    readonly Dictionary<(FormKey Worldspace, int X, int Y), long> _persistentGrids = new();

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
    long _persistentRefs, _persistentRefsMapped, _persistentRefsWithoutPlacement;
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
        foreach (var cp in PlacementExtract.FromMod(mod))
            if (!cp.Worldspace.IsNull)
                foreach (var r in cp.Refs.Where(x => x.Persistent))
                {
                    var key = (cp.Worldspace, (int)MathF.Floor(r.Position.X / 4096f),
                                              (int)MathF.Floor(r.Position.Y / 4096f));
                    _persistentGrids[key] = _persistentGrids.TryGetValue(key, out var n) ? n + 1 : 1;
                }
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
        if (kind == "persistent")
        {
            _persistentRefs++;
            if (r.Placement is null) _persistentRefsWithoutPlacement++; else _persistentRefsMapped++;
        }
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

    public PlacedReport Report()
    {
        var cells = _cells.Select(kv => new ScopeCell(kv.Key.ToString(), kv.Value.Kind, kv.Value.EditorId,
            kv.Value.Worldspace.IsNull ? null : Name(kv.Value.Worldspace),
            kv.Value.Worldspace.IsNull ? null : kv.Value.Worldspace.ToString(),
            kv.Value.HasGrid ? kv.Value.X : null, kv.Value.HasGrid ? kv.Value.Y : null,
            kv.Value.Refs, kv.Value.Navmesh, kv.Value.Land)).ToList();
        var existing = _cells.Values.Where(v => v.HasGrid).Select(v => (v.Worldspace, v.X, v.Y)).ToHashSet();
        foreach (var (key, refs) in _persistentGrids)
            if (!existing.Contains(key))
                cells.Add(new ScopeCell($"grid:{key.Worldspace}:{key.X}:{key.Y}", "exterior-grid", null,
                                        Name(key.Worldspace), key.Worldspace.ToString(), key.X, key.Y, refs, 0, 0));
        return new PlacedReport(_total, _interior, _exterior, _persistent, _navmesh, _land, _landscapeUnreadable,
            _persistentRefs, _persistentRefsMapped, _persistentRefsWithoutPlacement,
            cells, cells.Where(v => v.X is not null && v.Y is not null)
                        .Select(v => new GridCell(v.Worldspace ?? v.WorldspaceKey ?? "", v.X!.Value, v.Y!.Value)).ToList(),
            _worldspaces.Select(Name).ToList());
    }

    string Name(FormKey ws) => ws.IsNull ? "" : (_worldspaceNames.TryGetValue(ws, out var n) ? n : ws.ToString());
}

/// <summary>One record of a plugin that cannot be read: its FormKey (null when its header is not reached), signature and why.</summary>
sealed record BadRecord(string? FormKey, string Signature, string Reason);

// The records of one plugin whose bytes Mutagen's framing cannot read: a compressed body that will not inflate,
// subrecords that do not fit the body, a record or group running past its container. It reads the file's own bytes
// through Mutagen's frames, so it names a record whatever its type, and is run only after a read has already failed.
static class FrameCheck
{
    public static List<BadRecord> Unreadable(string path, ModKey self)
    {
        var found = new List<BadRecord>();
        var meta = GameConstants.SkyrimSE;
        byte[] bytes;
        try { bytes = File.ReadAllBytes(path); }
        catch (Exception ex) { found.Add(new BadRecord(null, "?", "the file could not be read: " + First(ex))); return found; }
        var all = new Noggog.ReadOnlyMemorySlice<byte>(bytes);
        var masters = new List<ModKey>();
        long pos;
        try
        {
            var header = new MajorRecordFrame(meta, all);
            foreach (var sub in RecordSpanExtensions.EnumerateSubrecords(header.Content, meta, 0))
                if (sub.RecordType.Type == "MAST")
                    masters.Add(ModKey.FromFileName(Encoding.Latin1.GetString(sub.Content.Span).TrimEnd('\0')));
            pos = header.TotalLength;
        }
        catch (Exception ex) { found.Add(new BadRecord(null, "TES4", "the file header cannot be framed: " + First(ex))); return found; }
        while (pos < bytes.Length)
        {
            long n = Group(meta, all, pos, bytes.Length, masters, self, found);
            if (n <= 0) break;
            pos += n;
        }
        return found;
    }

    // One group at `pos` inside [.., limit): its records checked, its subgroups walked. Returns its length, or 0 when
    // its own header cannot be framed, in which case the bytes after it cannot be located and the walk says so.
    static long Group(GameConstants meta, Noggog.ReadOnlyMemorySlice<byte> all, long pos, long limit, List<ModKey> masters,
                      ModKey self, List<BadRecord> found)
    {
        long total, head;
        try
        {
            var g = new GroupFrame(meta, all.Slice(checked((int)pos), checked((int)(limit - pos))));
            total = g.TotalLength;
            head = g.HeaderLength;
            if (total < head || pos + total > limit)
                throw new InvalidDataException($"its length {total} runs past its container's end at byte {limit}");
        }
        catch (Exception ex) { found.Add(new BadRecord(null, "GRUP", $"the group at byte {pos} cannot be framed: {First(ex)}")); return 0; }
        long end = pos + total, p = pos + head;
        while (p < end)
        {
            var here = all.Slice(checked((int)p), checked((int)(end - p)));
            string sig = here.Length >= 4 ? Encoding.Latin1.GetString(here.Span.Slice(0, 4)) : "?";
            if (sig == "GRUP")
            {
                long n = Group(meta, all, p, end, masters, self, found);
                if (n <= 0) return total;            // the rest of this group cannot be located; its container still can
                p += n;
                continue;
            }
            MajorRecordFrame rec;
            try
            {
                rec = new MajorRecordFrame(meta, here);
                if (p + rec.TotalLength > end)
                    throw new InvalidDataException($"its length {rec.TotalLength} runs past its group's end at byte {end}");
            }
            catch (Exception ex)
            {
                // The FormID sits at bytes 12..15 of a major record header; name it when the header itself is there.
                string? id = here.Length >= 24
                    ? FormKeyOf(BinaryPrimitives.ReadUInt32LittleEndian(here.Span.Slice(12, 4)), masters, self).ToString()
                    : null;
                found.Add(new BadRecord(id, sig, $"the record at byte {p} cannot be framed: {First(ex)}"));
                return total;
            }
            var key = FormKeyOf(rec.FormID.Raw, masters, self);
            try
            {
                var body = rec.IsCompressed ? rec.Decompress(out _).Content : rec.Content;
                foreach (var _ in RecordSpanExtensions.EnumerateSubrecords(body, meta, 0)) { }
            }
            catch (Exception ex)
            {
                found.Add(new BadRecord(key.ToString(), rec.RecordType.Type,
                    (rec.IsCompressed ? "its compressed body cannot be read: " : "its subrecords cannot be read: ") + First(ex)));
            }
            p += rec.TotalLength;
        }
        return total;
    }

    // A raw FormID through the file's own master list: index past the masters is the file itself.
    static FormKey FormKeyOf(uint raw, List<ModKey> masters, ModKey self)
    {
        int index = (int)(raw >> 24);
        return new FormKey(index < masters.Count ? masters[index] : self, raw & 0xFFFFFF);
    }

    static string First(Exception ex) => ex.Message.Split('\n')[0].Trim();
}
