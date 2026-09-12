using System.Diagnostics;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using HarnessShared;
using HousecarlCore;
using HousecarlCore.Progress;

// ADVICEPROBE — turn a mod page's own words into ordering rules for orderprobe
// (Q39, a roadmap 4.20 follow-up; the per-file ordering evidence roadmap 4.21's catalogue will consume. 2026-09-11.)
//
// orderprobe will not move a plugin unless something SAYS it may, and _shared/OrderRules.cs refuses an advice rule
// with no provenance. The provenance most compatibility patches actually have is a sentence the author wrote on the
// mod's page — and until now the only way it reached the tool was a person reading the page and retyping it. This
// probe reads it. The judgement about what a page does and does not state lives in _shared/OrderAdvice.cs (pure,
// proof-pinned); this file does the I/O: fetch or replay the page, read the MO2 instance, write the artifacts.
//
// usage: adviceprobe --mod <id|url> [--page [<id>=]FILE]... [--out DIR] [--offline]
//                    [--instance DIR] [--profile NAME] [--game DIR]
//
//   --mod <id|url>  the page to read. A full Nexus URL is accepted and the id taken out of it.
//   --page FILE     replay an ARCHIVED response instead of fetching. `--page 45686=file.json` says which mod the
//                   file is for; a bare `--page file.json` reads the id out of the file. Repeatable: the base mods'
//                   pages are archived and replayed the same way, so a re-run needs no network at all.
//   --offline       refuse every network call. A page with no archive is then a REFUSAL naming the mod and the flag,
//                   never a quietly empty answer.
//   --out DIR       where description-<id>.txt, rules-<id>.json, report-<id>.json and page-<id>.json go
//                   (default: ./adviceprobe-out).
//
// WHAT IT WRITES
//   page-<id>.json         the RAW GraphQL `data` element, verbatim, with readAt/readDate/source added BESIDE it and
//                          never inside it. This is the evidence; everything else is derived from it.
//   description-<id>.txt   the page description with markup stripped — what the extractor actually read.
//   rules-<id>.json        an OrderRules schema-1 fragment, ready for `orderprobe --rules`.
//   report-<id>.json       every statement found, every rule with its resolution chain, every refusal with the
//                          sentence it refused, the staleness line per named mod, and the choices this
//                          implementation made where the definition left one.
//
// WHAT IT DOES NOT DO
//   * it does not decide whether the author is right. A rule from a page is `advice`, never `required`.
//   * it does not guess a name. A mention that does not resolve is a refusal with the sentence quoted.
//   * it does not read asset priority. "X should overwrite Y" is noted as asset priority and produces no rule.
//   * it never writes an empty rules file that looks like "no rules were needed": a page that states no order says
//     so in one explicit line, in the report and in the fragment (Q3).

using var __run = RunLog.BeginProcess("adviceprobe", args);

const string Endpoint = "https://api.nexusmods.com/v2/graphql";
const int SkyrimSeGameId = 1704;

// The query text and the POST are COPIED from src/housecarl-mcp/NexusClient.cs (ModQuery / PostAsync), not
// referenced: housecarl-mcp is an exe and its renderer is internal, so the harness cannot take a project reference
// on it. Keep the field list in step with that file if it grows — the fields used here are description (the statement
// lives in it), updatedAt (the statement's date, D3), modRequirements (the only names a mention may resolve to, D2)
// and modFiles (version + date, for the newest MAIN file and for the installed file's upload date, D3).
const string ModQuery =
    @"query ModDetail($modId: ID!, $gameId: ID!) {
        mod(modId: $modId, gameId: $gameId) {
          modId name version summary description author category endorsements downloads
          updatedAt createdAt adultContent status directDownloadEnabled
          tags { name }
          modRequirements { nexusRequirements { nodes { modId modName url notes externalRequirement } } }
        }
        modFiles(modId: $modId, gameId: $gameId) {
          fileId name version category date description changelogText
        }
      }";

string Instance = HarnessTarget.Instance;
string GamePath = HarnessTarget.Game;
string profileName = HarnessTarget.Profile;
string outDir = Path.Combine(Directory.GetCurrentDirectory(), "adviceprobe-out");
int? modId = null;
bool offline = false;
var archives = new Dictionary<int, string>();
var bareArchives = new List<string>();

for (int i = 0; i < args.Length; i++)
{
    if (args[i] == "--instance" && i + 1 < args.Length) Instance = args[++i];
    else if (args[i] == "--game" && i + 1 < args.Length) GamePath = args[++i];
    else if (args[i] == "--profile" && i + 1 < args.Length) profileName = args[++i];
    else if (args[i] == "--out" && i + 1 < args.Length) outDir = args[++i];
    else if (args[i] == "--offline") offline = true;
    else if (args[i] == "--mod" && i + 1 < args.Length)
    {
        var raw = args[++i];
        var id = ModIdOf(raw);
        if (id is null) { Console.Error.WriteLine($"--mod needs a Nexus mod id or a mod URL; got '{raw}'."); return 2; }
        modId = id;
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

Instance = HarnessTarget.Require(Instance, "MO2 instance directory", "instance", "--instance");
GamePath = HarnessTarget.Require(GamePath, "game directory", "game", "--game");
profileName = HarnessTarget.Require(profileName, "MO2 profile name", "profile", "--profile");

var sw = Stopwatch.StartNew();
int archivesWritten = 0;
Directory.CreateDirectory(outDir);
var warnings = new List<string>();
var readLog = new List<string>();

using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(60) };
http.DefaultRequestHeaders.UserAgent.ParseAdd("houseCARL-adviceprobe/1.0");

Console.WriteLine($"adviceprobe — mod {modId} @ {profileName} / {Instance}");
Console.WriteLine(offline ? "   --offline: no network call will be made; every page must be replayed from an archive.\n" : "");

// ---------------------------------------------------------------------------------------------------------
//  The page.
// ---------------------------------------------------------------------------------------------------------
__run.Feature("pages", 1, "pages").Stage("page", "reading the mod page", 1, "pages");

PageRead page;
try { page = ReadPage(modId.Value); }
catch (Exception ex) { Console.Error.WriteLine($"REFUSED: {ex.Message}"); __run.Refuse(ex.Message); return 2; }
__run.Progress(1);

var mod = page.Data.TryGetProperty("mod", out var m) && m.ValueKind == JsonValueKind.Object ? m : default;
if (mod.ValueKind != JsonValueKind.Object)
{
    var why = $"the response for mod {modId} carries no 'mod' object, so there is no page to read.";
    Console.Error.WriteLine($"REFUSED: {why}"); __run.Refuse(why); return 2;
}

var pageName = Str(mod, "name") ?? $"mod {modId}";
var description = Str(mod, "description") ?? "";
var updatedAt = Str(mod, "updatedAt");
var pageUrl = $"https://www.nexusmods.com/skyrimspecialedition/mods/{modId}";

var requirements = new List<(int Id, string Name)>();
if (mod.TryGetProperty("modRequirements", out var mr) && mr.ValueKind == JsonValueKind.Object
    && mr.TryGetProperty("nexusRequirements", out var nr) && nr.ValueKind == JsonValueKind.Object
    && nr.TryGetProperty("nodes", out var nodes) && nodes.ValueKind == JsonValueKind.Array)
    foreach (var r in nodes.EnumerateArray())
    {
        var rid = Str(r, "modId");
        var rname = Str(r, "modName");
        if (int.TryParse(rid, out var ri) && !string.IsNullOrWhiteSpace(rname)) requirements.Add((ri, rname!));
        else warnings.Add($"a requirement came back without a usable modId/modName (modId '{rid}', modName '{rname}') and is NOT a known name for this run.");
    }

var files = Files(page.Data);
var newestMain = files.Where(f => f.Category == "MAIN").OrderByDescending(f => f.Date).FirstOrDefault();

Console.WriteLine($"page: '{pageName}', {description.Length:N0} characters of description, {requirements.Count} Nexus requirement(s), {files.Count} file(s)");
Console.WriteLine($"page: last updated {updatedAt ?? "(unknown)"}; newest MAIN file {(newestMain is null ? "(none)" : $"v{newestMain.Version ?? "?"} uploaded {Day(newestMain.Date)} UTC")}");
foreach (var r in requirements) Console.WriteLine($"   requirement: {r.Name} (mod {r.Id})");

var cleaned = OrderAdvice.Clean(description);
File.WriteAllText(Path.Combine(outDir, $"description-{modId}.txt"), cleaned);

// ---------------------------------------------------------------------------------------------------------
//  The instance: which mod folders carry which Nexus ids, and which plugins they put in the LOADED order.
// ---------------------------------------------------------------------------------------------------------
__run.Stage("resolve", "resolving names against the instance", 1, "instances");

string profileDir = Path.Combine(Instance, "profiles", profileName);
string modsDir = Path.Combine(Instance, "mods");
string overwriteDir = Path.Combine(Instance, "overwrite");
string dataDir = Path.Combine(GamePath, "Data");

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
//  Pass one: which mods does the page's statement actually name? Only those need their own pages read for D3.
// ---------------------------------------------------------------------------------------------------------
AdviceWorld BuildWorld(IReadOnlyDictionary<int, string?> installedDates, IReadOnlyDictionary<int, string?> dateSources)
{
    var known = new List<AdviceKnownName> { new(modId.Value, pageName, true) };
    foreach (var r in requirements) if (r.Id != modId.Value) known.Add(new AdviceKnownName(r.Id, r.Name, false));

    var installed = new Dictionary<int, AdviceWorldMod>();
    foreach (var (id, entries) in byModId)
    {
        var name = known.FirstOrDefault(k => k.ModId == id)?.Name ?? entries[0].Folder;
        var plugins = entries.SelectMany(e => e.Plugins).Distinct(StringComparer.OrdinalIgnoreCase)
                             .OrderBy(s => s, StringComparer.OrdinalIgnoreCase).ToList();
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
//  The base mods' pages, for the installed file dates D3 compares. One read each, archived, replayable.
// ---------------------------------------------------------------------------------------------------------
var dates = new Dictionary<int, string?>();
var sources = new Dictionary<int, string?>();
var need = pass1.NamedModIds.Where(id => byModId.ContainsKey(id)).ToList();
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
        readLog.Add($"mod {id}: installed date from the page already read — {s ?? "unknown"}");
        __run.Progress(++done);
        continue;
    }
    try
    {
        var basePage = ReadPage(id);
        var (d, s) = DateOf(Files(basePage.Data), meta);
        dates[id] = d; sources[id] = s;
        readLog.Add($"mod {id}: page {(basePage.FromArchive ? "replayed from " + Path.GetFileName(basePage.Origin) : "fetched")} — installed date {d ?? "unknown"} ({s ?? "no arm resolved it"})");
    }
    catch (Exception ex)
    {
        dates[id] = null; sources[id] = null;
        warnings.Add($"mod {id}'s page could not be read ({ex.Message}), so its installed file's upload date is UNKNOWN for this run, not old.");
        readLog.Add($"mod {id}: page NOT read — {ex.Message}");
    }
    __run.Progress(++done);
}

// Pass two, with the dates filled in. Extract is pure and the statements do not depend on a date, so the only
// difference between the two passes is the staleness annotation — and only this pass's answer is written.
var report = OrderAdvice.Extract(description, BuildWorld(dates, sources));

// ---------------------------------------------------------------------------------------------------------
//  The artifacts.
// ---------------------------------------------------------------------------------------------------------
__run.Stage("write", "writing the rules fragment and the report", 3, "files");

var rulesPath = Path.Combine(outDir, $"rules-{modId}.json");
File.WriteAllText(rulesPath, OrderAdvice.ToRulesJson(report));
__run.Progress(1);

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
    ["statements"] = report.Statements.Select(s => new Dictionary<string, object?>
    {
        ["shape"] = OrderAdvice.Shape(s.Shape), ["text"] = s.Text, ["items"] = s.Items, ["symptom"] = s.Symptom,
    }).ToList(),
    ["rules"] = report.Rules.Select(x => new Dictionary<string, object?>
    {
        ["before"] = x.Before, ["after"] = x.After, ["hardness"] = "advice", ["reason"] = x.Reason,
        ["provenance"] = new Dictionary<string, object?>
        { ["source"] = x.Source, ["checked"] = x.Checked, ["versionScope"] = x.VersionScope, ["note"] = x.Note },
        ["resolutionChain"] = x.Chain,
    }).ToList(),
    ["resolutions"] = report.Resolutions.Select(x => new Dictionary<string, object?>
    { ["mention"] = x.Mention, ["modId"] = x.ModId, ["modName"] = x.ModName, ["plugin"] = x.Plugin, ["how"] = x.How }).ToList(),
    ["notes"] = report.Notes,
    ["staleness"] = report.StalenessLines,
    ["noOrderStated"] = report.NothingStated,
    ["pagesRead"] = readLog,
    ["warnings"] = warnings,
    ["choices"] = report.Choices,
};
var reportPath = Path.Combine(outDir, $"report-{modId}.json");
File.WriteAllText(reportPath, JsonSerializer.Serialize(reportDoc, new JsonSerializerOptions { WriteIndented = true }));
__run.Progress(2);

Console.WriteLine();
Console.WriteLine(OrderAdvice.Describe(report));
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

Console.WriteLine($"wrote {rulesPath}, {reportPath}, description-{modId}.txt and {archivesWritten} page archive(s) in {sw.Elapsed.TotalSeconds:F1}s");
__run.Progress(3);
return 0;

// ---------------------------------------------------------------------------------------------------------

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

PageRead ReadPage(int id)
{
    if (archives.TryGetValue(id, out var file))
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(file));
        var root = doc.RootElement;
        var data = root.TryGetProperty("data", out var d) && d.ValueKind == JsonValueKind.Object ? d.Clone() : root.Clone();
        var readDate = root.TryGetProperty("readDate", out var rd) && rd.ValueKind == JsonValueKind.String ? rd.GetString()
                     : root.TryGetProperty("readAt", out var ra) && ra.ValueKind == JsonValueKind.String && ra.GetString() is { Length: >= 10 } s ? s[..10]
                     : null;
        if (string.IsNullOrWhiteSpace(readDate))
            throw new InvalidDataException(
                $"the archive '{Path.GetFileName(file)}' carries no readAt/readDate, so the date a rule's provenance would " +
                "claim as 'checked' is unknown. An archive without its read date is refused rather than stamped with today's.");
        return new PageRead(data, readDate!, true, file);
    }
    if (offline)
        throw new InvalidOperationException(
            $"mod {id}'s page is needed and --offline was given, and no archive for it was passed. " +
            $"Pass --page {id}=<file> with an archived response, or drop --offline.");

    var today = DateTime.Now.ToString("yyyy-MM-dd");
    var nowUtc = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ");
    var (ok, error, fetched) = Post(ModQuery, new { modId = id.ToString(), gameId = SkyrimSeGameId.ToString() }).GetAwaiter().GetResult();
    if (!ok) throw new InvalidOperationException($"mod {id}'s page could not be read — {error}");

    // The archive: the raw `data` element verbatim, with our own fields BESIDE it. Nothing is added inside `data`,
    // so a reader can tell what Nexus said from what this probe said about when it asked.
    var envelope = new Dictionary<string, object?>
    {
        ["$comment"] = "archived by adviceprobe. 'data' is the raw GraphQL data element, verbatim; readAt/readDate/source are this probe's, beside it and never inside it.",
        ["readAt"] = nowUtc,
        ["readDate"] = today,
        ["modId"] = id,
        ["source"] = $"{Endpoint} (query ModDetail, gameId {SkyrimSeGameId}, keyless)",
        ["data"] = JsonSerializer.Deserialize<JsonElement>(fetched.GetRawText()),
    };
    var archivePath = Path.Combine(outDir, $"page-{id}.json");
    File.WriteAllText(archivePath, JsonSerializer.Serialize(envelope, new JsonSerializerOptions { WriteIndented = true }));
    archivesWritten++;
    Console.WriteLine($"   read mod {id} from Nexus and archived it as {Path.GetFileName(archivePath)} (readAt {nowUtc})");
    return new PageRead(fetched, today, false, archivePath);
}

/// <summary>The core POST, copied down from NexusClient.PostAsync: every failure comes back as a message rather than
/// an exception, so a refusal says what was tried and what to do next (Q3).</summary>
async Task<(bool ok, string? error, JsonElement data)> Post(string query, object variables)
{
    string body; int status; string? reason; bool success;
    try
    {
        using var req = new HttpRequestMessage(HttpMethod.Post, Endpoint)
        { Content = JsonContent.Create(new { query, variables }, options: new JsonSerializerOptions { PropertyNamingPolicy = null }) };
        using var resp = await http.SendAsync(req);
        body = await resp.Content.ReadAsStringAsync();
        status = (int)resp.StatusCode; reason = resp.ReasonPhrase; success = resp.IsSuccessStatusCode;
    }
    catch (TaskCanceledException) { return (false, "the Nexus Mods request timed out.", default); }
    catch (HttpRequestException ex) { return (false, $"couldn't reach Nexus Mods ({ex.Message}).", default); }

    if (status == 429) return (false, "Nexus Mods is rate-limiting the connection (HTTP 429). Wait a moment and try again.", default);
    if (!success) return (false, $"Nexus Mods returned HTTP {status} ({reason}).", default);

    JsonDocument doc;
    try { doc = JsonDocument.Parse(body); }
    catch (Exception ex) { return (false, $"Nexus Mods returned an unreadable response ({ex.Message}).", default); }
    using (doc)
    {
        var root = doc.RootElement;
        if (root.TryGetProperty("errors", out var errs) && errs.ValueKind == JsonValueKind.Array && errs.GetArrayLength() > 0)
            return (false, "Nexus Mods query error: " + string.Join("; ", errs.EnumerateArray()
                .Select(e => e.TryGetProperty("message", out var mm) ? mm.GetString() : null).Where(s => !string.IsNullOrWhiteSpace(s))), default);
        if (!root.TryGetProperty("data", out var dataEl) || dataEl.ValueKind != JsonValueKind.Object)
            return (false, "Nexus Mods returned no data.", default);
        return (true, null, dataEl.Clone());
    }
}

static List<PageFile> Files(JsonElement data)
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
static (string? Date, string? Source) DateOf(List<PageFile> files, ModMetaIni meta)
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

static string? Str(JsonElement e, string p) => e.TryGetProperty(p, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
static int Int(JsonElement e, string p) => e.TryGetProperty(p, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var i) ? i : 0;
static long Long(JsonElement e, string p)
{
    if (!e.TryGetProperty(p, out var v)) return 0;
    if (v.ValueKind == JsonValueKind.Number && v.TryGetInt64(out var l)) return l;
    if (v.ValueKind == JsonValueKind.String && long.TryParse(v.GetString(), out var s)) return s;
    return 0;
}

sealed record PageRead(JsonElement Data, string ReadDate, bool FromArchive, string Origin);
sealed record PageFile(int FileId, string Name, string? Version, string Category, long Date);
