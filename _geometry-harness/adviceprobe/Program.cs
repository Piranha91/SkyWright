using System.Diagnostics;
using System.Text.Json;
using HarnessShared;
using HousecarlCore;
using HousecarlCore.Progress;
using HousecarlNexus;

// ADVICEPROBE — turn a mod page's own words into ordering rules for orderprobe
// (Q39, a roadmap 4.20 follow-up; the per-file ordering evidence roadmap 4.21's catalogue now supplies. 2026-09-11,
//  moved onto the shared catalogue 2026-09-12.)
//
// orderprobe will not move a plugin unless something SAYS it may, and _shared/OrderRules.cs refuses an advice rule
// with no provenance. The provenance most compatibility patches actually have is a sentence the author wrote on the
// mod's page — and until now the only way it reached the tool was a person reading the page and retyping it. This
// probe reads it. The judgement about what a page does and does not state lives in _shared/OrderAdvice.cs (pure,
// proof-pinned); this file does the I/O: get the page, read the MO2 instance, write the artifacts.
//
// WHERE THE PAGE COMES FROM (roadmap 4.21). The transport and the store are now HousecarlNexus — the SAME library
// and the SAME store the MCP server's catalogue tools use, so a page one of them read is a page the other already
// has. This probe no longer carries its own copy of the POST. A run that finds every page it needs in the store
// makes ZERO requests and says so, per page, in the report's pagesRead lines.
//
// usage: adviceprobe --mod <id|url> [--page [<id>=]FILE]... [--out DIR] [--offline]
//                    [--catalog DIR] [--refresh offline|if-stale|force] [--max-requests N] [--no-import]
//                    [--plugin-map FILE] [--with <id|url>]... [--instance DIR] [--profile NAME] [--game DIR]
//
//   --mod <id|url>   the page to read. A full Nexus URL is accepted and the id taken out of it.
//   --with <id|url>  a NEIGHBOUR: another mod in the order. Its page name becomes a known name on the --mod page,
//                    and its own page is read too: a clash it states against the --mod mod (S4) is reported in
//                    `clashes`, and an order rule naming both mods enters the rules fragment with that page as its
//                    source. A neighbour page that cannot be read is a warning naming it, never a quiet empty answer.
//                    Repeatable.
//   --page FILE      replay an ARCHIVED response instead of reading the catalogue. `--page 45686=file.json` says
//                    which mod the file is for; a bare `--page file.json` reads the id out of the file. Repeatable.
//                    The replay is byte-identical to what it always was; unless --no-import the archive is ALSO
//                    imported into the catalogue, keeping its own read date.
//   --catalog DIR    the catalogue store (default: HOUSECARL_NEXUS_CATALOG_DIR, else the plugin data dir, else
//                    %LOCALAPPDATA%\houseCARL\nexus-catalog).
//   --refresh MODE   force (the DEFAULT: ask Nexus for every page this run needs, and fall back to the stored
//                    copy only when the source does not answer) | if-stale (accept a stored copy under 24 h)
//                    | offline (never ask; a page with no stored copy is a refusal).
//   --max-requests N a ceiling on HTTP attempts for the whole run. 0 proves a run makes none.
//   --no-import      replay a --page archive WITHOUT putting it in the catalogue.
//   --plugin-map F   JSON { "<fileId>": ["Plugin.esp", ...] } — which plugin(s) a Nexus file installs. The one
//                    caller-supplied arm of D6's "this patch" resolution, for a mod folder that ships several.
//   --offline        refuse every network call (implies --refresh offline). A page with neither an archive nor a
//                    catalogue snapshot is then a REFUSAL naming the mod, the flag and the state, never a quietly
//                    empty answer.
//   --out DIR        where description-<id>.txt, rules-<id>.json, report-<id>.json and page-<id>.json go
//                    (default: ./adviceprobe-out).
//
// WHAT IT WRITES
//   (the raw page)         NOT written beside the output any more: the catalogue archives every observation itself,
//                          verbatim, under <store>/pages/<modId>/raw/, with readAt/readDate/source beside the data
//                          and never inside it. One archive, one owner. The report's pagesRead lines say which
//                          route each page took and the catalogue's own request counts.
//   description-<id>.txt   the page description with markup stripped — what the extractor actually read.
//   rules-<id>.json        an OrderRules schema-1 fragment, ready for `orderprobe --rules`.
//   report-<id>.json       every statement found, every rule with its resolution chain, every refusal with the
//                          sentence it refused, the staleness line per named mod, the selected installed file per
//                          named mod, the catalogue's request accounting, and the choices this implementation made
//                          where the definition left one.
//
// WHAT IT DOES NOT DO
//   * it does not decide whether the author is right. A rule from a page is `advice`, never `required`.
//   * it does not guess a name. A mention that does not resolve is a refusal with the sentence quoted.
//   * it does not read asset priority. "X should overwrite Y" is noted as asset priority and produces no rule.
//   * it does not open an archive or a FOMOD to learn which plugin a file installs. Without --plugin-map that stays
//     a NOTE naming the candidates.
//   * it never writes an empty rules file that looks like "no rules were needed": a page that states no order says
//     so in one explicit line, in the report and in the fragment (Q3).

using var __run = RunLog.BeginProcess("adviceprobe", args);

string Instance = HarnessTarget.Instance;
string GamePath = HarnessTarget.Game;
string profileName = HarnessTarget.Profile;
string outDir = Path.Combine(Directory.GetCurrentDirectory(), "adviceprobe-out");
int? modId = null;
bool offline = false;
bool noImport = false;
string? catalogDir = null;
string? refreshWord = null;
string? pluginMapPath = null;
int? maxRequests = null;
var archives = new Dictionary<int, string>();
var withIds = new List<int>();
var bareArchives = new List<string>();

for (int i = 0; i < args.Length; i++)
{
    if (args[i] == "--instance" && i + 1 < args.Length) Instance = args[++i];
    else if (args[i] == "--game" && i + 1 < args.Length) GamePath = args[++i];
    else if (args[i] == "--profile" && i + 1 < args.Length) profileName = args[++i];
    else if (args[i] == "--out" && i + 1 < args.Length) outDir = args[++i];
    else if (args[i] == "--catalog" && i + 1 < args.Length) catalogDir = args[++i];
    else if (args[i] == "--refresh" && i + 1 < args.Length) refreshWord = args[++i];
    else if (args[i] == "--plugin-map" && i + 1 < args.Length) pluginMapPath = args[++i];
    else if (args[i] == "--no-import") noImport = true;
    else if (args[i] == "--max-requests" && i + 1 < args.Length)
    {
        if (!int.TryParse(args[++i], out var mr) || mr < 0)
        { Console.Error.WriteLine($"--max-requests needs a number of HTTP attempts (0 or more); got '{args[i]}'."); return 2; }
        maxRequests = mr;
    }
    else if (args[i] == "--offline") offline = true;
    else if (args[i] == "--mod" && i + 1 < args.Length)
    {
        var raw = args[++i];
        var id = ModIdOf(raw);
        if (id is null) { Console.Error.WriteLine($"--mod needs a Nexus mod id or a mod URL; got '{raw}'."); return 2; }
        modId = id;
    }
    else if (args[i] == "--with" && i + 1 < args.Length)
    {
        var raw = args[++i];
        var id = ModIdOf(raw);
        if (id is null) { Console.Error.WriteLine($"--with needs a Nexus mod id or a mod URL; got '{raw}'."); return 2; }
        if (!withIds.Contains(id.Value)) withIds.Add(id.Value);
    }
    else if (args[i] == "--page" && i + 1 < args.Length)
    {
        var spec = args[++i];
        int eq = spec.IndexOf('=');
        // A Windows drive letter ("C:\x") has no '=', so an '=' here is always the id separator.
        if (eq > 0 && int.TryParse(spec[..eq], out var pid)) archives[pid] = spec[(eq + 1)..];
        else bareArchives.Add(spec);
    }
    else if (args[i].StartsWith("--"))
    { Console.Error.WriteLine($"unknown option '{args[i]}'. See the header of adviceprobe/Program.cs for the flag list."); return 2; }
}

if (modId is null)
{
    Console.Error.WriteLine("adviceprobe needs --mod <id|url>: the page whose stated load order is to be read.");
    return 2;
}
withIds.Remove(modId.Value);

// --offline is the loud spelling of --refresh offline. Given both, offline wins: the flag that FORBIDS a request
// beats the one that permits it, because the other way round a stray --refresh force would silently spend one.
RefreshMode mode;
if (offline) mode = RefreshMode.Offline;
else switch ((refreshWord ?? "force").Trim().ToLowerInvariant())
{
    case "offline": mode = RefreshMode.Offline; offline = true; break;
    case "if-stale" or "ifstale": mode = RefreshMode.IfStale; break;
    case "force": mode = RefreshMode.Force; break;
    default:
        Console.Error.WriteLine($"--refresh '{refreshWord}' is not recognized — use offline, if-stale or force.");
        return 2;
}

// A bare --page FILE has to say which mod it is for, and the file itself is the only place that can. A file that
// cannot say is a refusal, not a guess — replaying the wrong page under the right id would be a silent wrong answer.
foreach (var f in bareArchives)
{
    if (!File.Exists(f)) { Console.Error.WriteLine($"--page '{f}': no such file."); return 2; }
    int? id;
    try { id = ArchiveModId(f); }
    catch (Exception ex) { Console.Error.WriteLine($"--page '{f}': not readable as JSON — {ex.Message}"); return 2; }
    if (id is null)
    {
        Console.Error.WriteLine($"--page '{f}': the file does not say which mod it is for. Pass it as --page <id>={f}.");
        return 2;
    }
    archives[id.Value] = f;
}

// The plugin map: the ONE caller-supplied arm of D6's "this patch" resolution. Read before anything else touches
// the instance, so a malformed map fails the run at the top rather than half way through.
var pluginMap = new Dictionary<int, List<string>>();
if (pluginMapPath is not null)
{
    try
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(pluginMapPath));
        if (doc.RootElement.ValueKind != JsonValueKind.Object)
            throw new InvalidDataException("the map must be a JSON object keyed by Nexus file id.");
        foreach (var prop in doc.RootElement.EnumerateObject())
        {
            if (!int.TryParse(prop.Name, out var fid))
                throw new InvalidDataException($"'{prop.Name}' is not a Nexus file id.");
            if (prop.Value.ValueKind != JsonValueKind.Array)
                throw new InvalidDataException($"file {fid}'s value must be an ARRAY of plugin filenames.");
            var list = new List<string>();
            foreach (var v in prop.Value.EnumerateArray())
                if (v.ValueKind == JsonValueKind.String && v.GetString() is { Length: > 0 } s) list.Add(s);
            pluginMap[fid] = list;
        }
    }
    catch (Exception ex)
    {
        Console.Error.WriteLine($"--plugin-map '{pluginMapPath}' could not be read — {ex.Message}");
        return 2;
    }
}

Instance = HarnessTarget.Require(Instance, "MO2 instance directory", "instance", "--instance");
GamePath = HarnessTarget.Require(GamePath, "game directory", "game", "--game");
profileName = HarnessTarget.Require(profileName, "MO2 profile name", "profile", "--profile");

var sw = Stopwatch.StartNew();
Directory.CreateDirectory(outDir);
var warnings = new List<string>();
var readLog = new List<string>();
var importLog = new List<string>();

// The page transport and the store, both from the shared library. The HttpClient is created even for an offline
// run and never used by one: the catalogue only reaches its source when the MODE says it may, so "did this run
// make a request" is answered by the accounting below rather than by whether a client exists.
using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(60) };
http.DefaultRequestHeaders.UserAgent.ParseAdd("houseCARL-adviceprobe/1.0");
http.DefaultRequestHeaders.Add("Application-Name", "houseCARL");
http.DefaultRequestHeaders.Add("Application-Version", "adviceprobe");

var catalogOptions = new NexusCatalogOptions
{
    Clock = () => DateTimeOffset.UtcNow,
    Source = new NexusHttpSource(http),
    RequestBudget = maxRequests,
};
if (catalogDir is not null) catalogOptions.Dir = catalogDir;
using var catalog = new NexusCatalog(catalogOptions);

var pageCache = new Dictionary<int, PageRead>();
int requestsAttempted = 0, requestsSucceeded = 0, pagesFromSnapshot = 0, pagesFetched = 0, pagesFromArchive = 0;
long waitedMs = 0;

Console.WriteLine($"adviceprobe — mod {modId} @ {profileName} / {Instance}");
Console.WriteLine($"   catalogue: {catalogOptions.Dir}  (refresh {mode.ToString().ToLowerInvariant()}"
                + (maxRequests is { } mrq ? $", at most {mrq} request(s)" : "") + ")");
Console.WriteLine(offline ? "   --offline: no network call will be made; every page must come from an archive or the catalogue.\n" : "");

// ---------------------------------------------------------------------------------------------------------
//  The page.
// ---------------------------------------------------------------------------------------------------------
__run.Feature("pages", 1, "pages").Stage("page", "reading the mod page", 1, "pages");

PageRead page;
try { page = ReadPage(modId.Value, isPageMod: true); }
catch (Exception ex) { Console.Error.WriteLine($"REFUSED: {ex.Message}"); __run.Refuse(ex.Message); return 2; }
__run.Progress(1);
readLog.Add($"mod {modId}: {page.Provenance}");

var pageName = page.Name;
var description = page.Description;
var updatedAt = page.UpdatedAt;
var pageUrl = UrlOf(modId.Value);
var requirements = page.Requirements;
var files = page.Files;
var newestMain = files.Where(f => f.Category == "MAIN").OrderByDescending(f => f.Date).FirstOrDefault();

Console.WriteLine($"page: '{pageName}', {description.Length:N0} characters of description, {requirements.Count} Nexus requirement(s), {files.Count} file(s)");
Console.WriteLine($"page: last updated {updatedAt ?? "(unknown)"}; newest MAIN file {(newestMain is null ? "(none)" : $"v{newestMain.Version ?? "?"} uploaded {Day(newestMain.Date)} UTC")}");
Console.WriteLine($"page: {page.Provenance}");
foreach (var r in requirements) Console.WriteLine($"   requirement: {r.Name} (mod {r.Id})");

var cleaned = OrderAdvice.Clean(description);
File.WriteAllText(Path.Combine(outDir, $"description-{modId}.txt"), cleaned);

// The neighbours' pages (--with). Read before anything is extracted, because their names are known names on the
// page above. A page that cannot be read is a WARNING naming it: what that page states is unknown, not absent.
var neighbours = new List<(int Id, PageRead Page)>();
if (withIds.Count > 0) __run.Stage("neighbours", "reading the neighbours' pages", withIds.Count, "pages");
int nRead = 0;
foreach (var wid in withIds)
{
    try
    {
        var np = ReadPage(wid);
        neighbours.Add((wid, np));
        readLog.Add($"mod {wid} (--with): {np.Provenance}");
    }
    catch (Exception ex)
    {
        warnings.Add($"mod {wid}'s page (--with) could not be read: {ex.Message} What it states about mod {modId} is UNKNOWN for this run, not absent.");
    }
    __run.Progress(++nRead);
}

// ---------------------------------------------------------------------------------------------------------
//  The instance: which mod folders carry which Nexus ids, and which plugins they put in the LOADED order.
// ---------------------------------------------------------------------------------------------------------
__run.Stage("resolve", "resolving names against the instance", 1, "instances");

string profileDir = Path.Combine(Instance, "profiles", profileName);
string modsDir = Path.Combine(Instance, "mods");
string overwriteDir = Path.Combine(Instance, "overwrite");
string dataDir = Path.Combine(GamePath, "Data");

// Exempt from the missing-master refusal (ruling 12): only mod metadata is read to write rules; no record is read.
var comp = Mo2LoadOrder.ReadComposition(profileDir, warnings);
var filenameMap = SyntheticOrder.BuildFilenameMap(comp, modsDir, dataDir, overwriteDir);
var inactive = new HashSet<string>(comp.InactivePluginNames, StringComparer.OrdinalIgnoreCase);

// The LOADED BASELINE — loadorder.txt minus what plugins.txt leaves unticked. Exactly the set orderprobe builds a
// baseline from, and the only set a rule may name: an unticked plugin is not in the order the game loads, so it can
// neither satisfy a mention nor make one ambiguous.
var loaded = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
foreach (var n in comp.OrderedPluginNames) if (!inactive.Contains(n)) loaded.Add(n);

// Each enabled mod folder's Nexus id, version and installed file ids, straight out of MO2's own cache.
var byModId = new Dictionary<int, List<(string Folder, ModMetaIni Meta, List<string> Plugins)>>();
foreach (var folder in comp.EnabledMods)
{
    var root = Path.Combine(modsDir, folder);
    var meta = Mo2ModMeta.Read(Path.Combine(root, "meta.ini"));
    if (meta is null || meta.ModId <= 0) continue;                 // a hand-installed mod or a separator: no Nexus id
    var plugins = new List<string>();
    try
    {
        foreach (var f in Directory.EnumerateFiles(root, "*", SearchOption.TopDirectoryOnly))
        {
            var ext = Path.GetExtension(f);
            if (!ext.Equals(".esp", StringComparison.OrdinalIgnoreCase) && !ext.Equals(".esm", StringComparison.OrdinalIgnoreCase)
             && !ext.Equals(".esl", StringComparison.OrdinalIgnoreCase)) continue;
            var name = Path.GetFileName(f);
            if (loaded.Contains(name)) plugins.Add(name);
        }
    }
    catch (Exception ex) { warnings.Add($"mod folder '{folder}' could not be listed — {ex.Message}; its plugins are UNKNOWN for this run, not absent."); }
    plugins.Sort(StringComparer.OrdinalIgnoreCase);
    if (!byModId.TryGetValue(meta.ModId, out var l)) byModId[meta.ModId] = l = new();
    l.Add((folder, meta, plugins));
}

Console.WriteLine($"instance: {comp.EnabledMods.Count} enabled mod folder(s), {byModId.Count} with a Nexus id; " +
                  $"{loaded.Count} plugin(s) in the loaded baseline of {comp.OrderedPluginNames.Count} in loadorder.txt");

// The page's own plugin's master list, which is the only thing that can break a "this mod owns two plugins" tie.
// Read only when the page's mod contributes EXACTLY one plugin: a plugin's master list cannot choose between two of
// its own siblings, and reading one of them arbitrarily would be the guess this whole probe exists to avoid.
var pageMasters = new List<string>();
if (byModId.TryGetValue(modId.Value, out var own) && own.Count == 1 && own[0].Plugins.Count == 1)
{
    var pn = own[0].Plugins[0];
    if (filenameMap.TryGetValue(pn, out var path))
    {
        var h = PluginHeader.TryRead(path);
        if (h is null) warnings.Add($"the page's own plugin '{pn}' has an unreadable header, so its master list cannot break a tie for this run.");
        else pageMasters.AddRange(h.Masters);
    }
}
else if (own is { Count: > 0 })
    warnings.Add($"the page's own mod contributes {own.Sum(x => x.Plugins.Count)} plugin(s) from {own.Count} folder(s) to the loaded order, " +
                 "so there is no single 'the page's plugin' whose master list could break a tie. The tie-break is UNAVAILABLE, not empty.");

// ---------------------------------------------------------------------------------------------------------
//  D6 — which uploaded FILE is the one installed, and (with a map) which plugin it installed.
// ---------------------------------------------------------------------------------------------------------
var selections = new Dictionary<int, FileSelection>();
var mapApplied = new Dictionary<int, MapApplication>();

FileSelection SelectFile(int id, IReadOnlyList<PageFile> pageFiles)
{
    if (!byModId.TryGetValue(id, out var entries) || entries.Count == 0)
        return new FileSelection(id, null, null, "unresolved: mod " + id + " is not installed in this profile", new List<int>());
    if (entries.Count > 1)
        return new FileSelection(id, null, null,
            $"unresolved: {entries.Count} enabled folders carry mod {id} ({string.Join(", ", entries.Select(e => $"'{e.Folder}'"))}), so no single meta.ini says which file is installed",
            entries.SelectMany(e => e.Meta.InstalledFileIds).Distinct().ToList());

    var (folder, meta, _) = entries[0];
    var ids = meta.InstalledFileIds;
    if (ids.Count == 0)
        return new FileSelection(id, folder, null, "unresolved: meta.ini records no file id", new List<int>());
    if (ids.Count > 1)
        return new FileSelection(id, folder, null, "unresolved: several installed files", ids.ToList());

    var hit = pageFiles.FirstOrDefault(f => f.FileId == ids[0]);
    if (hit is null)
        return new FileSelection(id, folder, null, $"unresolved: file {ids[0]} is not on the page (removed/hidden?)", ids.ToList());
    return new FileSelection(id, folder, hit, "meta.ini installedFiles (evidenced)", ids.ToList());
}

// ---------------------------------------------------------------------------------------------------------
//  Pass one: which mods does the page's statement actually name? Only those need their own pages read for D3.
// ---------------------------------------------------------------------------------------------------------
AdviceWorld BuildWorld(IReadOnlyDictionary<int, string?> installedDates, IReadOnlyDictionary<int, string?> dateSources)
{
    var known = new List<AdviceKnownName> { new(modId.Value, pageName, true) };
    foreach (var r in requirements) if (r.Id != modId.Value) known.Add(new AdviceKnownName(r.Id, r.Name, false));
    foreach (var (nid, np) in neighbours) if (known.All(k => k.ModId != nid)) known.Add(new AdviceKnownName(nid, np.Name, false));

    var installed = new Dictionary<int, AdviceWorldMod>();
    foreach (var (id, entries) in byModId)
    {
        var name = known.FirstOrDefault(k => k.ModId == id)?.Name ?? entries[0].Folder;
        var plugins = entries.SelectMany(e => e.Plugins).Distinct(StringComparer.OrdinalIgnoreCase)
                             .OrderBy(s => s, StringComparer.OrdinalIgnoreCase).ToList();
        // D6's one new arm: the caller supplied which plugin the INSTALLED FILE puts in the order, so the mod's
        // several plugins stop being an ambiguity. The narrowing is recorded — the report still lists every plugin
        // the folder contributes, so a reader can see what the map chose between.
        if (plugins.Count > 1 && mapApplied.TryGetValue(id, out var app))
            plugins = new List<string> { app.Plugin };
        installed[id] = new AdviceWorldMod(
            id, name, entries.Select(e => e.Folder).ToList(), plugins,
            entries[0].Meta.Version,
            installedDates.TryGetValue(id, out var d) ? d : null,
            dateSources.TryGetValue(id, out var s) ? s : null);
    }

    return new AdviceWorld(
        modId.Value, pageName, pageUrl, page.ReadDate, known, installed, pageMasters,
        updatedAt is null ? null : DayIso(updatedAt), newestMain?.Version, newestMain is null ? null : Day(newestMain.Date));
}

var noDates = new Dictionary<int, string?>();
var pass1 = OrderAdvice.Extract(description, BuildWorld(noDates, noDates));

// ---------------------------------------------------------------------------------------------------------
//  The base mods' pages, for the installed file dates D3 compares. One read each — from the catalogue, so a warm
//  run reads no base-mod page from the network either.
// ---------------------------------------------------------------------------------------------------------
var dates = new Dictionary<int, string?>();
var sources = new Dictionary<int, string?>();
var need = pass1.NamedModIds.Where(id => byModId.ContainsKey(id)).ToList();
if (!need.Contains(modId.Value) && byModId.ContainsKey(modId.Value)) selections[modId.Value] = SelectFile(modId.Value, files);
if (need.Count > 0) __run.Stage("dates", "reading the named mods' file dates", need.Count, "pages");
int done = 0;
foreach (var id in need)
{
    var meta = byModId[id][0].Meta;
    if (id == modId.Value)
    {
        // The page's own mod: its file list is already in hand, no second read.
        var (d, s) = DateOf(files, meta);
        dates[id] = d; sources[id] = s;
        selections[id] = SelectFile(id, files);
        readLog.Add($"mod {id}: installed date from the page already read — {s ?? "unknown"}");
        __run.Progress(++done);
        continue;
    }
    try
    {
        var basePage = ReadPage(id);
        var (d, s) = DateOf(basePage.Files, meta);
        dates[id] = d; sources[id] = s;
        selections[id] = SelectFile(id, basePage.Files);
        readLog.Add($"mod {id}: {basePage.Provenance} — installed date {d ?? "unknown"} ({s ?? "no arm resolved it"})");
    }
    catch (Exception ex)
    {
        dates[id] = null; sources[id] = null;
        warnings.Add($"mod {id}'s page could not be read ({ex.Message}), so its installed file's upload date is UNKNOWN for this run, not old.");
        readLog.Add($"mod {id}: page NOT read — {ex.Message}");
    }
    __run.Progress(++done);
}

// The map is applied AFTER the selections exist, because it keys on the evidenced installed file. A map entry for a
// file nobody installed changes nothing and says so.
foreach (var (id, sel) in selections)
{
    if (sel.File is null || !pluginMap.TryGetValue(sel.File.FileId, out var mapped)) continue;
    var folderPlugins = byModId.TryGetValue(id, out var e)
        ? e.SelectMany(x => x.Plugins).Distinct(StringComparer.OrdinalIgnoreCase).ToList()
        : new List<string>();
    var inOrder = mapped.Where(p => folderPlugins.Contains(p, StringComparer.OrdinalIgnoreCase)).ToList();
    if (inOrder.Count == 1)
        mapApplied[id] = new MapApplication(id, sel.File.FileId, inOrder[0], folderPlugins, mapped);
    else
        warnings.Add($"--plugin-map lists {mapped.Count} plugin(s) for file {sel.File.FileId} (mod {id}) and {inOrder.Count} of them "
                   + $"are in the loaded order ({string.Join(", ", folderPlugins)}); the map resolves 'this patch' only when EXACTLY one is, "
                   + "so this run's resolution is unchanged by it.");
}

// Pass two, with the dates filled in (and the plugin map applied). Extract is pure and the statements do not depend
// on a date, so the only differences between the two passes are the staleness annotation and any map narrowing —
// and only this pass's answer is written.
var pageReport = OrderAdvice.Extract(description, BuildWorld(dates, sources));

// Each neighbour's page, read with the --mod mod as a known name. What it states about the PAIR is kept: a clash
// naming the --mod mod, and an order rule naming both. Everything else on that page is about other mods.
var neighbourReports = new List<(int Id, PageRead Page, AdviceReport Report, List<AdviceClash> Clashes, List<AdviceRuleOut> Rules)>();
foreach (var (nid, np) in neighbours)
{
    var nMain = np.Files.Where(f => f.Category == "MAIN").OrderByDescending(f => f.Date).FirstOrDefault();
    var nKnown = new List<AdviceKnownName> { new(nid, np.Name, true), new(modId.Value, pageName, false) };
    foreach (var r in np.Requirements) if (nKnown.All(k => k.ModId != r.Id)) nKnown.Add(new AdviceKnownName(r.Id, r.Name, false));
    var nWorld = BuildWorld(dates, sources) with
    {
        PageModId = nid, PageModName = np.Name, PageUrl = UrlOf(nid), ReadDate = np.ReadDate, Known = nKnown,
        PagePluginMasters = Array.Empty<string>(),
        PageUpdatedDate = np.UpdatedAt is null ? null : DayIso(np.UpdatedAt),
        NewestMainVersion = nMain?.Version, NewestMainDate = nMain is null ? null : Day(nMain.Date),
    };
    var nr = OrderAdvice.Extract(np.Description, nWorld);
    neighbourReports.Add((nid, np, nr,
        nr.Clashes.Where(c => c.NamedModId == modId.Value).ToList(),
        nr.Rules.Where(x => (x.BeforeModId == modId.Value && x.AfterModId == nid) || (x.BeforeModId == nid && x.AfterModId == modId.Value)).ToList()));
}

// The merge. A neighbour's rule that states the REVERSE of a rule already held is a disagreement between two pages:
// neither is emitted, and both are named, the same answer one page disagreeing with itself gets.
var mergedRules = new List<AdviceRuleOut>(pageReport.Rules);
var mergeNotes = new List<string>();
foreach (var nr in neighbourReports.SelectMany(n => n.Rules))
{
    var rev = mergedRules.FirstOrDefault(x => x.Before.Equals(nr.After, StringComparison.OrdinalIgnoreCase)
                                           && x.After.Equals(nr.Before, StringComparison.OrdinalIgnoreCase));
    if (rev is not null)
    {
        mergedRules.Remove(rev);
        mergeNotes.Add($"{nr.Source} orders '{nr.Before}' before '{nr.After}' and {rev.Source} states the reverse; no rule is " +
                       "emitted for that pair, and both statements are reported so a reader can decide.");
        continue;
    }
    if (!mergedRules.Any(x => x.Before.Equals(nr.Before, StringComparison.OrdinalIgnoreCase)
                           && x.After.Equals(nr.After, StringComparison.OrdinalIgnoreCase)))
        mergedRules.Add(nr);
}
var report = pageReport with
{
    Rules = mergedRules,
    Clashes = pageReport.Clashes.Concat(neighbourReports.SelectMany(n => n.Clashes)).ToList(),
    Notes = pageReport.Notes.Concat(mergeNotes).ToList(),
};

// ---------------------------------------------------------------------------------------------------------
//  The artifacts.
// ---------------------------------------------------------------------------------------------------------
__run.Stage("write", "writing the rules fragment and the report", 3, "files");

var rulesPath = Path.Combine(outDir, $"rules-{modId}.json");
File.WriteAllText(rulesPath, OrderAdvice.ToRulesJson(report));
__run.Progress(1);

// The chain line the map earns. The pure layer says "puts exactly one plugin in the order" because that is the
// world it was handed; this line says WHO narrowed that world and what it chose between, so the rule is checkable.
string MapChainLine(MapApplication a) =>
    $"supplied by --plugin-map: installed file {a.FileId} puts '{a.Plugin}' in the order; the mod's folder contributes "
  + $"{a.FolderPlugins.Count} loaded plugin(s) ({string.Join(", ", a.FolderPlugins)}), and the map named "
  + $"{a.MappedPlugins.Count} for that file ({string.Join(", ", a.MappedPlugins)})";

var reportDoc = new Dictionary<string, object?>
{
    ["probe"] = "adviceprobe",
    ["modId"] = report.ModId,
    ["modName"] = report.ModName,
    ["url"] = report.Url,
    ["readDate"] = report.ReadDate,
    ["readDateZone"] = "local (it records when the page was looked at, not an instant being compared)",
    ["pageUpdatedDateUtc"] = report.PageUpdatedDate,
    ["newestMainVersion"] = report.NewestMainVersion,
    ["newestMainDateUtc"] = report.NewestMainDate,
    ["instance"] = Instance,
    ["profile"] = profileName,
    ["pagePluginMasters"] = pageMasters,
    ["requirements"] = requirements.Select(r => new Dictionary<string, object?> { ["modId"] = r.Id, ["modName"] = r.Name }).ToList(),
    // The world the extractor was handed, for the KNOWN mods: which enabled folders carry each Nexus id, which of
    // their plugins survive into the loaded baseline, and what dated the installed file. Without this a reader can
    // see the answer but not the evidence — and "ambiguous" or "contributes no plugin" is unarguable only if the
    // plugin list it was computed from is on the page.
    ["installedKnownMods"] = new[] { (Id: modId.Value, Name: pageName) }.Concat(requirements.Select(r => (Id: r.Id, Name: r.Name)))
        .Where(k => byModId.ContainsKey(k.Id))
        .Select(k => new Dictionary<string, object?>
        {
            ["modId"] = k.Id,
            ["modName"] = k.Name,
            ["folders"] = byModId[k.Id].Select(e => e.Folder).ToList(),
            ["pluginsInFolders"] = byModId[k.Id].SelectMany(e =>
            {
                try { return Directory.EnumerateFiles(Path.Combine(modsDir, e.Folder), "*", SearchOption.TopDirectoryOnly)
                             .Select(Path.GetFileName)
                             .Where(n => n is not null && (n.EndsWith(".esp", StringComparison.OrdinalIgnoreCase)
                                                        || n.EndsWith(".esm", StringComparison.OrdinalIgnoreCase)
                                                        || n.EndsWith(".esl", StringComparison.OrdinalIgnoreCase))); }
                catch { return Enumerable.Empty<string?>(); }
            }).OrderBy(s => s, StringComparer.OrdinalIgnoreCase).ToList(),
            ["pluginsInLoadedBaseline"] = byModId[k.Id].SelectMany(e => e.Plugins).Distinct(StringComparer.OrdinalIgnoreCase)
                                                      .OrderBy(s => s, StringComparer.OrdinalIgnoreCase).ToList(),
            ["metaIniVersion"] = byModId[k.Id][0].Meta.Version,
            ["metaIniFileIds"] = byModId[k.Id][0].Meta.InstalledFileIds,
            ["installedFileDateUtc"] = dates.TryGetValue(k.Id, out var dd) ? dd : null,
            ["installedDateFrom"] = sources.TryGetValue(k.Id, out var ss) ? ss : "not looked up (no rule names this mod)",
        }).ToList(),
    // D6 — the installed FILE behind each named mod, and how (or whether) it was evidenced. A file that cannot be
    // named is a stated unresolution with its candidates, never a silent omission.
    ["selectedFiles"] = selections.OrderBy(kv => kv.Key).Select(kv => new Dictionary<string, object?>
    {
        ["modId"] = kv.Key,
        ["folder"] = kv.Value.Folder,
        ["mappingSource"] = kv.Value.MappingSource,
        ["candidates"] = kv.Value.Candidates,
        ["selectedFile"] = kv.Value.File is null ? null : new Dictionary<string, object?>
        {
            ["fileId"] = kv.Value.File.FileId,
            ["name"] = kv.Value.File.Name,
            ["version"] = kv.Value.File.Version,
            ["category"] = kv.Value.File.Category,
            ["status"] = StatusOf(kv.Value.File.Category),
            ["dateUtc"] = kv.Value.File.Date > 0 ? Day(kv.Value.File.Date) : null,
        },
    }).ToList(),
    ["pluginMap"] = pluginMapPath is null ? null : new Dictionary<string, object?>
    {
        ["file"] = pluginMapPath,
        ["entries"] = pluginMap.OrderBy(kv => kv.Key).ToDictionary(kv => kv.Key.ToString(), kv => (object?)kv.Value),
        ["applied"] = mapApplied.OrderBy(kv => kv.Key).Select(kv => new Dictionary<string, object?>
        {
            ["modId"] = kv.Key,
            ["fileId"] = kv.Value.FileId,
            ["plugin"] = kv.Value.Plugin,
            ["folderPluginsInLoadedOrder"] = kv.Value.FolderPlugins,
            ["chain"] = MapChainLine(kv.Value),
        }).ToList(),
    },
    ["with"] = withIds,
    // S4: clashes an author states, on this page or on a neighbour's page, each with the page it came from.
    ["clashes"] = report.Clashes.Select(c => new Dictionary<string, object?>
    {
        ["namedModId"] = c.NamedModId, ["namedModName"] = c.NamedModName, ["pageModId"] = c.PageModId,
        ["page"] = c.PageUrl, ["readDate"] = c.ReadDate, ["line"] = c.Line, ["symptom"] = c.Symptom,
    }).ToList(),
    ["neighbourPages"] = neighbourReports.Select(n => new Dictionary<string, object?>
    {
        ["modId"] = n.Id, ["modName"] = n.Page.Name, ["url"] = UrlOf(n.Id), ["readDate"] = n.Page.ReadDate,
        ["provenance"] = n.Page.Provenance, ["statementsOnPage"] = n.Report.Statements.Count,
        ["clashesNamingThisMod"] = n.Clashes.Count, ["rulesNamingBoth"] = n.Rules.Count,
    }).ToList(),
    ["statements"] = report.Statements.Select(s => new Dictionary<string, object?>
    {
        ["shape"] = OrderAdvice.Shape(s.Shape), ["text"] = s.Text, ["items"] = s.Items, ["symptom"] = s.Symptom,
    }).ToList(),
    ["rules"] = report.Rules.Select(x => new Dictionary<string, object?>
    {
        ["before"] = x.Before, ["after"] = x.After, ["hardness"] = "advice", ["reason"] = x.Reason,
        ["provenance"] = new Dictionary<string, object?>
        { ["source"] = x.Source, ["checked"] = x.Checked, ["versionScope"] = x.VersionScope, ["note"] = x.Note },
        ["resolutionChain"] = x.Chain.Concat(mapApplied.Values
            .Where(a => string.Equals(a.Plugin, x.Before, StringComparison.OrdinalIgnoreCase)
                     || string.Equals(a.Plugin, x.After, StringComparison.OrdinalIgnoreCase))
            .Select(MapChainLine)).ToList(),
    }).ToList(),
    ["resolutions"] = report.Resolutions.Select(x => new Dictionary<string, object?>
    { ["mention"] = x.Mention, ["modId"] = x.ModId, ["modName"] = x.ModName, ["plugin"] = x.Plugin, ["how"] = x.How }).ToList(),
    ["notes"] = report.Notes,
    ["staleness"] = report.StalenessLines,
    ["noOrderStated"] = report.NothingStated,
    ["pagesRead"] = readLog,
    // The catalogue's own accounting. A run that says "requests 0" here made none: the number is the library's,
    // counted per attempt, not this file's opinion about what it thought it was doing.
    ["catalogue"] = new Dictionary<string, object?>
    {
        ["dir"] = catalogOptions.Dir,
        ["refresh"] = mode.ToString().ToLowerInvariant(),
        ["maxRequests"] = maxRequests,
        ["requestsAttempted"] = requestsAttempted,
        ["requestsSucceeded"] = requestsSucceeded,
        ["waitedMs"] = waitedMs,
        ["pagesServedFromSnapshot"] = pagesFromSnapshot,
        ["pagesFetched"] = pagesFetched,
        ["pagesReplayedFromArchive"] = pagesFromArchive,
        ["imports"] = importLog,
    },
    ["warnings"] = warnings,
    ["choices"] = report.Choices,
};
var reportPath = Path.Combine(outDir, $"report-{modId}.json");
File.WriteAllText(reportPath, JsonSerializer.Serialize(reportDoc, new JsonSerializerOptions { WriteIndented = true }));
__run.Progress(2);

Console.WriteLine();
Console.WriteLine(OrderAdvice.Describe(report));
foreach (var s in selections.OrderBy(kv => kv.Key))
    Console.WriteLine($"   installed file for mod {s.Key}: "
        + (s.Value.File is null
            ? s.Value.MappingSource + (s.Value.Candidates.Count > 0 ? $" (candidates: {string.Join(", ", s.Value.Candidates)})" : "")
            : $"{s.Value.File.FileId} '{s.Value.File.Name}' v{s.Value.File.Version ?? "?"} [{s.Value.File.Category}] — {s.Value.MappingSource}"));
foreach (var a in mapApplied.Values) Console.WriteLine($"   {MapChainLine(a)}");
foreach (var w in warnings) Console.WriteLine($"   ! {w}");
Console.WriteLine("  choices this implementation made where the definition left one:");
foreach (var c in report.Choices) Console.WriteLine($"     - {c}");

// The fragment is checked through the reader that will consume it, here, in this run. A file that orderprobe would
// refuse is not a deliverable, and finding that out at the next command is finding it out too late.
try
{
    var check = OrderRules.ParseFile(rulesPath);
    Console.WriteLine($"\n{rulesPath}: parses as OrderRules schema {OrderRules.CurrentSchema} — {check.Rules.Count} rule(s), " +
                      $"{check.Rules.Count(r => r.Hardness == RuleHardness.Advice)} advice, {check.Rules.Count(r => r.Hardness == RuleHardness.Required)} required");
    if (check.Rules.Any(r => r.Hardness != RuleHardness.Advice))
    { Console.Error.WriteLine("REFUSED: a rule read off a page must be advice. This is a bug in OrderAdvice, not in the page."); return 1; }
}
catch (Exception ex)
{
    Console.Error.WriteLine($"REFUSED: the fragment this run just wrote would be refused by orderprobe — {ex.Message}");
    __run.Refuse("the generated fragment does not parse"); return 1;
}

__run.Feature("requestsAttempted", requestsAttempted, "requests", FeatureAt.Final);
__run.Feature("pagesServedFromSnapshot", pagesFromSnapshot, "pages", FeatureAt.Final);
__run.Feature("pagesFetched", pagesFetched, "pages", FeatureAt.Final);
__run.Feature("waitedMs", waitedMs, "ms", FeatureAt.Final);
__run.Note($"catalogue {catalogOptions.Dir}: {requestsAttempted} request(s) attempted, {pagesFromSnapshot} page(s) from a snapshot, "
         + $"{pagesFetched} fetched, {pagesFromArchive} replayed from an archive, {waitedMs} ms waiting");

Console.WriteLine($"wrote {rulesPath}, {reportPath} and description-{modId}.txt in {sw.Elapsed.TotalSeconds:F1}s");
Console.WriteLine($"catalogue: {requestsAttempted} request(s) attempted, {requestsSucceeded} succeeded, {pagesFromSnapshot} page(s) served from a snapshot, "
                + $"{pagesFetched} fetched, {pagesFromArchive} replayed from an archive, {waitedMs} ms waiting");
__run.Progress(3);
return 0;

// ---------------------------------------------------------------------------------------------------------

static string UrlOf(int id) => $"https://www.nexusmods.com/skyrimspecialedition/mods/{id}";

/// <summary>The mod id out of a bare number or a Nexus URL.</summary>
static int? ModIdOf(string raw)
{
    var s = raw.Trim();
    if (int.TryParse(s, out var n) && n > 0) return n;
    var m = System.Text.RegularExpressions.Regex.Match(s, @"/mods/(\d+)");
    return m.Success && int.TryParse(m.Groups[1].Value, out var u) ? u : null;
}

/// <summary>Which mod an archive file is for: our own `modId` key, or the response's own `data.mod.modId`.</summary>
static int? ArchiveModId(string path)
{
    using var doc = JsonDocument.Parse(File.ReadAllText(path));
    var root = doc.RootElement;
    if (root.TryGetProperty("modId", out var v))
    {
        if (v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var i)) return i;
        if (v.ValueKind == JsonValueKind.String && int.TryParse(v.GetString(), out var s)) return s;
    }
    var data = root.TryGetProperty("data", out var d) ? d : root;
    if (data.ValueKind == JsonValueKind.Object && data.TryGetProperty("mod", out var mm)
        && mm.ValueKind == JsonValueKind.Object && mm.TryGetProperty("modId", out var mi)
        && mi.ValueKind == JsonValueKind.Number && mi.TryGetInt32(out var k)) return k;
    return null;
}

/// <summary>One page, however it arrived: a --page archive replayed verbatim, or the shared catalogue's snapshot.
/// Both routes end here with the same five things the extractor needs, so the answer cannot depend on the route.</summary>
PageRead ReadPage(int id, bool isPageMod = false)
{
    // One read per page per run: a neighbour that is also a named mod's base page is not counted twice.
    if (pageCache.TryGetValue(id, out var cached)) return cached;
    var read = ReadPageOnce(id, isPageMod);
    pageCache[id] = read;
    return read;
}

PageRead ReadPageOnce(int id, bool isPageMod)
{
    // Only the page's OWN requirements become known names (D2), so only they can produce the
    // "not a known name" warning. A base mod's requirement list is read and discarded, and warning about
    // it would put a line in the report about a name nothing was ever going to match.
    var reqWarnings = isPageMod ? warnings : new List<string>();
    // ---- the --page replay: byte-identical to what it always did ----------------------------------------
    if (archives.TryGetValue(id, out var file))
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(file));
        var root = doc.RootElement;
        var data = root.TryGetProperty("data", out var d) && d.ValueKind == JsonValueKind.Object ? d : root;
        var readDate = root.TryGetProperty("readDate", out var rd) && rd.ValueKind == JsonValueKind.String ? rd.GetString()
                     : root.TryGetProperty("readAt", out var ra) && ra.ValueKind == JsonValueKind.String && ra.GetString() is { Length: >= 10 } s ? s[..10]
                     : null;
        if (string.IsNullOrWhiteSpace(readDate))
            throw new InvalidDataException(
                $"the archive '{Path.GetFileName(file)}' carries no readAt/readDate, so the date a rule's provenance would " +
                "claim as 'checked' is unknown. An archive without its read date is refused rather than stamped with today's.");

        var mod = data.TryGetProperty("mod", out var m) && m.ValueKind == JsonValueKind.Object ? m : default;
        if (mod.ValueKind != JsonValueKind.Object)
            throw new InvalidDataException($"the response for mod {id} carries no 'mod' object, so there is no page to read.");

        // The archive is evidence the store should have too — the same envelope, keeping its OWN read date, so a
        // later run of anything can serve this page without a request. It is an import, not a check: nothing here
        // advances what the source was last verified at.
        if (!noImport)
        {
            try
            {
                var imp = catalog.ImportFile(file);
                importLog.Add($"mod {imp.ModId}: {imp.Outcome} from {Path.GetFileName(file)}"
                            + (imp.Reason is null ? "" : $" — {imp.Reason}"));
            }
            catch (Exception ex)
            {
                warnings.Add($"the archive '{Path.GetFileName(file)}' replayed fine but could not be imported into the catalogue ({ex.Message}); "
                           + "this run is unaffected, but the store does not have the page.");
            }
        }

        pagesFromArchive++;
        return new PageRead(
            Str(mod, "name") ?? $"mod {id}",
            Str(mod, "description") ?? "",
            Str(mod, "updatedAt"),
            RequirementsOf(mod, reqWarnings),
            FilesOf(data),
            readDate!,
            $"archive {Path.GetFileName(file)}");
    }

    // ---- the catalogue -----------------------------------------------------------------------------------
    var result = catalog.GetPageAsync(id, mode, CancellationToken.None).GetAwaiter().GetResult();
    requestsAttempted += result.RequestsAttempted;
    requestsSucceeded += result.RequestsSucceeded;
    waitedMs += result.WaitedMs;

    if (result.Snapshot is null)
    {
        var state = result.Provenance.State.ToString().ToLowerInvariant();
        var detail = result.Provenance.StateDetail is null ? "" : $" — {result.Provenance.StateDetail}";
        if (offline)
            throw new InvalidOperationException(
                $"mod {id}'s page is needed and --offline was given: the catalogue has no snapshot for it (state {state}{detail}), " +
                $"and no --page archive for it was passed. Pass --page {id}=<file> with an archived response, put the page in the " +
                $"catalogue first, or drop --offline.");
        throw new InvalidOperationException(
            $"mod {id}'s page could not be read — the catalogue has no snapshot for it (state {state}{detail}) after " +
            $"{result.RequestsAttempted} request attempt(s).");
    }

    var snap = result.Snapshot;
    if (result.ServedFrom == "fetch") pagesFetched++; else pagesFromSnapshot++;

    // D7: the provenance date a rule claims as 'checked' is the SNAPSHOT's read date — an import's original date, a
    // fetch's own date — and never today merely because today is when it was served out of the store.
    var provenance = result.ServedFrom == "fetch"
        ? $"fetched now ({result.RequestsAttempted} request(s))"
        : $"catalogue snapshot, fetched {Iso(snap.FetchedAt)}, checked {Iso(snap.CheckedAt)}, "
          + $"state {result.Provenance.State.ToString().ToLowerInvariant()}, requests {result.RequestsAttempted}";
    // The state is computed from AGE alone, so a refresh that was refused (a spent budget, a rate limit) leaves a
    // FRESH page with a failed attempt beside it. Printing the state without the attempt would read as "nothing
    // needed doing" for a run where the fetch was actually stopped — the two are opposite answers.
    if (result.Provenance.LastAttempt is { } la && la.Outcome != "complete")
        provenance += $"; last attempt {Iso(la.At)} {la.Outcome}"
                    + (la.Kind is null ? "" : $" ({la.Kind})") + $", {la.Requests} request(s)";

    return new PageRead(
        snap.Name,
        snap.DescriptionRaw ?? "",
        snap.UpdatedAt,
        RequirementsOfSnapshot(snap, reqWarnings),
        snap.Files.Select(f => new PageFile(f.FileId, f.Name, f.Version, f.Category, f.Date)).ToList(),
        snap.ReadDate,
        provenance);
}

/// <summary>The same read as <c>RequirementsOf</c>, off a catalogue snapshot instead of a raw `mod` object. Two
/// readers because the two routes hold the page in two shapes; ONE rule, stated once here and once there: a
/// requirement missing an id or a name is a warning and not a known name.</summary>
static List<(int Id, string Name)> RequirementsOfSnapshot(PageSnapshot snap, List<string> warnings)
{
    var reqs = new List<(int, string)>();
    foreach (var r in snap.Requirements)
    {
        if (int.TryParse(r.ModId, out var ri) && !string.IsNullOrWhiteSpace(r.ModName)) reqs.Add((ri, r.ModName));
        else warnings.Add($"a requirement came back without a usable modId/modName (modId '{r.ModId}', modName '{r.ModName}') and is NOT a known name for this run.");
    }
    return reqs;
}

/// <summary>The page's Nexus requirements out of a raw `mod` object — the only names a mention may resolve to (D2).
/// A requirement missing an id or a name is a WARNING and not a known name, never a silently dropped row.</summary>
static List<(int Id, string Name)> RequirementsOf(JsonElement mod, List<string> warnings)
{
    var reqs = new List<(int, string)>();
    if (mod.TryGetProperty("modRequirements", out var mr) && mr.ValueKind == JsonValueKind.Object
        && mr.TryGetProperty("nexusRequirements", out var nr) && nr.ValueKind == JsonValueKind.Object
        && nr.TryGetProperty("nodes", out var nodes) && nodes.ValueKind == JsonValueKind.Array)
        foreach (var r in nodes.EnumerateArray())
        {
            var rid = Str(r, "modId");
            var rname = Str(r, "modName");
            if (int.TryParse(rid, out var ri) && !string.IsNullOrWhiteSpace(rname)) reqs.Add((ri, rname!));
            else warnings.Add($"a requirement came back without a usable modId/modName (modId '{rid}', modName '{rname}') and is NOT a known name for this run.");
        }
    return reqs;
}

static List<PageFile> FilesOf(JsonElement data)
{
    var list = new List<PageFile>();
    if (data.TryGetProperty("modFiles", out var mf) && mf.ValueKind == JsonValueKind.Array)
        foreach (var f in mf.EnumerateArray())
            list.Add(new PageFile(Int(f, "fileId"), Str(f, "name") ?? "", Str(f, "version"), Str(f, "category") ?? "", Long(f, "date")));
    return list;
}

/// <summary>D3's installed date, by the two arms the definition allows, in order. The file-id arm is PRIMARY and is
/// the one that works when MO2's version string is not a version at all (a real instance carries
/// 'd2024.2.13.0' for a file the page calls '2.8'). The version arm tolerates MO2's four-part normalisation, which
/// appends a '.0' the page does not have. Neither arm firing is UNKNOWN, and the caller says so.</summary>
static (string? Date, string? Source) DateOf(IReadOnlyList<PageFile> files, ModMetaIni meta)
{
    foreach (var fid in meta.InstalledFileIds)
    {
        var hit = files.FirstOrDefault(f => f.FileId == fid);
        if (hit is not null && hit.Date > 0) return (Day(hit.Date), $"file id {fid} ('{hit.Name}' v{hit.Version ?? "?"})");
    }
    if (!string.IsNullOrWhiteSpace(meta.Version))
    {
        var v = meta.Version!.Trim();
        var trimmed = v.EndsWith(".0", StringComparison.Ordinal) ? v[..^2] : v;
        var hit = files.FirstOrDefault(f => f.Version is { Length: > 0 }
                                         && (f.Version.Equals(v, StringComparison.OrdinalIgnoreCase)
                                          || f.Version.Equals(trimmed, StringComparison.OrdinalIgnoreCase)));
        if (hit is not null && hit.Date > 0)
            return (Day(hit.Date), $"version string '{v}' matched file {hit.FileId} ('{hit.Name}' v{hit.Version})"
                                 + (hit.Version!.Equals(v, StringComparison.OrdinalIgnoreCase) ? "" : " with MO2's trailing '.0' dropped"));
    }
    return (null, meta.InstalledFileIds.Count == 0
        ? "meta.ini records no installed file id (a FOMOD or hand install) and its version string matched no file on the page"
        : $"meta.ini's file id(s) ({string.Join(", ", meta.InstalledFileIds)}) are not on the page any more and its version string matched no file");
}

/// <summary>D2's status word for a source category, spelled the same way the catalogue spells it.</summary>
static string StatusOf(string category) => category.ToUpperInvariant() switch
{
    "OLD_VERSION" or "ARCHIVED" => "retired",
    "REMOVED" => "removed",
    _ => "live",
};

/// <summary>Epoch seconds to yyyy-MM-dd in UTC. Both sides of every D3 comparison go through here or through the
/// ISO overload, so the two are in the same zone — a file uploaded 2025-05-02 UTC reads 2025-05-01 locally here,
/// and a comparison that mixed the two would be wrong by a day on real data.</summary>
static string Day(long unixSeconds)
{
    try { return DateTimeOffset.FromUnixTimeSeconds(unixSeconds).UtcDateTime.ToString("yyyy-MM-dd"); }
    catch { return unixSeconds.ToString(); }
}

/// <summary>An ISO-8601 instant to yyyy-MM-dd in UTC.</summary>
static string DayIso(string iso) =>
    DateTimeOffset.TryParse(iso, null, System.Globalization.DateTimeStyles.AdjustToUniversal, out var t)
        ? t.UtcDateTime.ToString("yyyy-MM-dd")
        : (iso.Length >= 10 ? iso[..10] : iso);

/// <summary>An instant as the provenance lines spell it.</summary>
static string Iso(DateTimeOffset t) => t.UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ssZ");

static string? Str(JsonElement e, string p) => e.TryGetProperty(p, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
static int Int(JsonElement e, string p) => e.TryGetProperty(p, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var i) ? i : 0;
static long Long(JsonElement e, string p)
{
    if (!e.TryGetProperty(p, out var v)) return 0;
    if (v.ValueKind == JsonValueKind.Number && v.TryGetInt64(out var l)) return l;
    if (v.ValueKind == JsonValueKind.String && long.TryParse(v.GetString(), out var s)) return s;
    return 0;
}

sealed record PageRead(string Name, string Description, string? UpdatedAt, List<(int Id, string Name)> Requirements,
                       List<PageFile> Files, string ReadDate, string Provenance);
sealed record PageFile(int FileId, string Name, string? Version, string Category, long Date);
/// <summary>D6's answer for one mod: which uploaded file the instance installed, or why that is unresolved.</summary>
sealed record FileSelection(int ModId, string? Folder, PageFile? File, string MappingSource, List<int> Candidates);
/// <summary>One --plugin-map entry that actually resolved something, with what it chose between.</summary>
sealed record MapApplication(int ModId, int FileId, string Plugin, List<string> FolderPlugins, List<string> MappedPlugins);
