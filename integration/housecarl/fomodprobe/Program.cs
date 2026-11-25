using System.Diagnostics;
using System.Text;
using System.Xml.Linq;
using HarnessShared;
using HousecarlCore.Progress;

// FOMOD OPTIONS NEVER SELECTED — what the installer offered you and you did not take.
//
// MO2 installs only the options chosen at install time. Everything else the archive contained is
// still in the archive and nowhere else, so no amount of reading the mods folder can see it. This
// is a genuine blind spot in every check built so far: patchfind can only find a patch that
// someone unpacked somewhere.
//
// The blind spot is worth closing because compatibility patches are overwhelmingly delivered this
// way: a patch hub can ship dozens of optional patches in one FOMOD, each a single ESP for a
// specific other mod.
//
// ⚠️ THE PART THAT MAKES THIS EXACT. A FOMOD does not just list options — it declares what each
// option is FOR:
//
//     <plugin name="Fall of Granite Hill">
//       <files><file source="00 Data\...\Lux Via - Fall of Granite Hill patch.esp" .../></files>
//       <typeDescriptor><dependencyType><patterns><pattern>
//         <dependencies><fileDependency file="Fall of Granite Hill.esp" state="Active"/></dependencies>
//         <type name="Recommended"/>
//
// So the question is not "which options exist" (interesting, unactionable) but "which options did
// the FOMOD itself say to take, given the plugins that are actually active here, and which of
// those are missing from the installed mod". That is a lookup, not a judgement, and the FOMOD's
// own words are the evidence.
//
// SCHEMA GROUNDING. Not guessed: the element and attribute names below are read off real
// ModuleConfig.xml files in this download pool (see the excerpt above), which conform to
// ModConfig5.0.xsd. Anything the parse does not recognise is COUNTED as unjudgeable rather than
// silently treated as absent.
//
// ARCHIVE GROUNDING. Reading is done by the 7-Zip command line,
// whose surface was checked against its own usage output rather than assumed:
//     7z l -slt -ba -sccUTF-8 <archive>        technical listing, one "Path = ..." per entry
//     7z e -so -bso0 -bse0 <archive> <entry>   that entry's bytes on stdout, nothing else
// No third-party archive library is involved, so there is no library surface to get wrong. RAR,
// 7z and ZIP all work through the same call.
//
// usage: fomodprobe [--list N] [--mod Name] [--jobs N] [--json PATH]
//
// --json PATH writes the hit list (schema 1, ../_shared/DiscoveryHits.cs): one hit per plugin an unselected option
// in the WRONG-VARIANT, ABSENT or INACTIVE tier delivers, with the sha256 of that archive entry (extracted with
// `7z e -so`, the same call that reads ModuleConfig.xml), its condition plugins and its tier; a NO-PLUGIN option is a
// hit with no hash and the reason. The added extraction time is reported. Hits are listed by (archive, option, entry)
// ordinal, then step, group and mod folder, at any --jobs; an archive installed into several folders is reported once, for the
// ordinal-first folder, and an INACTIVE hit's onDiskIn lists folders in ordinal order.

using var __run = RunLog.BeginProcess("fomodprobe", args);

string Instance = HarnessTarget.Require(HarnessTarget.Instance, "MO2 instance directory", "instance");
string Profile = HarnessTarget.Require(HarnessTarget.Profile, "MO2 profile name", "profile");
string Pool = HarnessTarget.Require(HarnessTarget.DownloadPool, "download pool directory", "downloadPool");
string SevenZip = HarnessTarget.Require(HarnessTarget.SevenZip, "path to 7z.exe", "sevenZip");

int listN = 40;
string? onlyMod = null;
int jobs = Math.Max(2, Environment.ProcessorCount - 2);
string? jsonPath = null;
bool jsonMalformed = false;
for (int i = 0; i < args.Length; i++)
{
    if (args[i] == "--list" && i + 1 < args.Length) listN = int.Parse(args[++i]);
    else if (args[i] == "--mod" && i + 1 < args.Length) onlyMod = args[++i];
    else if (args[i] == "--jobs" && i + 1 < args.Length) jobs = int.Parse(args[++i]);
    else if (args[i] == "--json" && i + 1 < args.Length && !args[i + 1].StartsWith("--")) jsonPath = args[++i];
    else if (args[i] == "--json") jsonMalformed = true;
    else
    {
        // never drop an argument: a build that ignored --json once exited 0 with no hit list written
        Console.Error.WriteLine($"unknown argument {args[i]}: fomodprobe takes --list N, --mod NAME, --jobs N and --json PATH.");
        return 2;
    }
}
if (jsonMalformed) { Console.Error.WriteLine("--json needs a file path: pass --json PATH."); return 2; }
var hitList = jsonPath is null ? null : new DiscoveryHits("fomodprobe", "fomod");

if (!File.Exists(SevenZip)) { Console.Error.WriteLine($"7-Zip not found at {SevenZip}"); return 1; }

var sw = Stopwatch.StartNew();

// ---------------------------------------------------------------------------------------
// 1. What is active in the target profile
// ---------------------------------------------------------------------------------------
// plugins.txt does NOT list the base-game masters — MO2 treats them as implicitly always on — so
// anything named in loadorder.txt that plugins.txt never mentions is exactly one of those.
var profileDir = Path.Combine(Instance, "profiles", Profile);
var active = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
{
    var listed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    foreach (var line in File.ReadLines(Path.Combine(profileDir, "plugins.txt")))
    {
        var t = line.Trim();
        if (t.Length == 0 || t.StartsWith('#')) continue;
        var name = t.StartsWith('*') ? t[1..].Trim() : t;
        listed.Add(name);
        if (t.StartsWith('*')) active.Add(name);
    }
    var lo = Path.Combine(profileDir, "loadorder.txt");
    if (File.Exists(lo))
        foreach (var line in File.ReadLines(lo))
        {
            var t = line.Trim();
            if (t.Length == 0 || t.StartsWith('#')) continue;
            if (!listed.Contains(t)) active.Add(t);
        }
}

// Only mods MO2 has switched on. An option missing from a mod that is itself disabled is noise.
var enabledMods = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
foreach (var line in File.ReadLines(Path.Combine(profileDir, "modlist.txt")))
{
    var t = line.Trim();
    if (t.StartsWith('+')) enabledMods.Add(t[1..].Trim());
}

Console.WriteLine($"target: {Instance}  profile '{Profile}'");
Console.WriteLine($"   {active.Count:N0} active plugins, {enabledMods.Count:N0} enabled mods");

// ---------------------------------------------------------------------------------------
// 2. Every enabled mod that came from an archive we can still find
// ---------------------------------------------------------------------------------------
var modsDir = Path.Combine(Instance, "mods");
var targets = new List<(string Mod, string ModDir, string Archive)>();
int noMeta = 0, noInstallFile = 0, archiveGone = 0;

// EVERY folder each archive was installed into, enabled or not — MO2's "install each option as
// its own mod" spreads ONE archive across MANY folders, and the trust test was still asking "did
// the required files land in THIS folder". That is method rule 9 never applied to the trust test
// itself: 8 of its 9 refusals were patch collections, i.e. exactly the multi-folder case. The
// content a FOMOD delivered is the union over its folders; disabled folders count, because
// presence on disk is what the trust test and Selected are about (activation is judged later,
// instance-wide, as before).
static string? InstallationFile(string modDir)
{
    var meta = Path.Combine(modDir, "meta.ini");
    if (!File.Exists(meta)) return null;
    foreach (var line in File.ReadLines(meta))
        if (line.StartsWith("installationFile=", StringComparison.OrdinalIgnoreCase))
            return line.Split('=', 2)[1].Trim();
    return null;
}

var archiveFolders = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
foreach (var modDir in Directory.EnumerateDirectories(modsDir))
{
    if (InstallationFile(modDir) is { Length: > 0 } inst)
    {
        if (!archiveFolders.TryGetValue(inst, out var l)) archiveFolders[inst] = l = new List<string>();
        l.Add(modDir);
    }
}

foreach (var modDir in Directory.EnumerateDirectories(modsDir))
{
    var mod = Path.GetFileName(modDir);
    if (onlyMod is not null && !mod.Contains(onlyMod, StringComparison.OrdinalIgnoreCase)) continue;
    if (!enabledMods.Contains(mod)) continue;

    if (!File.Exists(Path.Combine(modDir, "meta.ini"))) { noMeta++; continue; }
    var inst = InstallationFile(modDir);
    if (string.IsNullOrWhiteSpace(inst)) { noInstallFile++; continue; }
    var archive = Path.Combine(Pool, inst);
    if (!File.Exists(archive)) { archiveGone++; continue; }
    targets.Add((mod, modDir, archive));
}

// ---------------------------------------------------------------------------------------
// 2b. Every plugin file that exists ANYWHERE in this instance
// ---------------------------------------------------------------------------------------
// ⚠️ THE CORRECTION THAT SHAPES THE VERDICTS. Asking "is this option's file in THIS mod folder"
// gets the wrong answer whenever the file arrived some other way, and here it does constantly:
//   - MO2 installs one archive into several mod folders, each taking a different subset, so an
//     option not taken in folder A was taken in folder B from the same archive (many folders in a
//     large instance share an installationFile with another).
//   - A user can copy a hub's patch ESPs into a hand-made mod folder of their own.
// Asked per folder, both read as "never selected" options whose files are on disk.
// So the question is asked of the whole instance, and the answer splits three ways: the file is
// absent entirely, or it is present but not active, or it is active and there is nothing to say.
// Every mod folder is scanned, disabled ones included — a disabled folder is exactly where an
// already-installed patch hides.
var pluginHome = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
foreach (var modDir in Directory.EnumerateDirectories(modsDir))
{
    string[] files;
    try { files = Directory.GetFiles(modDir); } catch { continue; }
    foreach (var f in files)
    {
        if (!IsPlugin(f)) continue;                   // MO2 only sees plugins at a mod's root
        var leaf = Path.GetFileName(f);
        if (!pluginHome.TryGetValue(leaf, out var l)) pluginHome[leaf] = l = new List<string>();
        l.Add(Path.GetFileName(modDir));
    }
}
Console.WriteLine($"   {pluginHome.Count:N0} distinct plugin files exist somewhere in this instance ({active.Count:N0} of them active)");

Console.WriteLine($"   {targets.Count:N0} enabled mods whose source archive is on disk");
Console.WriteLine($"   not examined: {noMeta:N0} without meta.ini, {noInstallFile:N0} with no installationFile, {archiveGone:N0} whose archive is gone");
hitList?.Searched("instance", Instance, Path.GetFileName(Path.TrimEndingDirectorySeparator(Instance)));
hitList?.Searched("download-pool", Pool, "the source archive of every enabled mod installed from it");
if (noMeta > 0) hitList?.Unjudgeable($"{noMeta} enabled mod(s)", "no meta.ini, so the source archive is unknown");
if (noInstallFile > 0) hitList?.Unjudgeable($"{noInstallFile} enabled mod(s)", "meta.ini names no installationFile");
if (archiveGone > 0) hitList?.Unjudgeable($"{archiveGone} enabled mod(s)", "the source archive is no longer in the pool");

// ---------------------------------------------------------------------------------------
// 3. Read each archive
// ---------------------------------------------------------------------------------------
var results = new List<ModResult>();
var padlock = new object();
int done = 0;

__run.Feature("archives", targets.Count, "archives").Stage("installers", "reading the installers' options", targets.Count, "archives");
Parallel.ForEach(targets, new ParallelOptions { MaxDegreeOfParallelism = jobs }, t =>
{
    var r = Examine(t.Mod, t.ModDir, t.Archive);
    __run.Advance();
    lock (padlock)
    {
        results.Add(r);
        if (++done % 100 == 0) Console.WriteLine($"   ... {done}/{targets.Count} archives, {sw.Elapsed.TotalSeconds:0}s");
    }
});
// The workers finish in any order: everything below reads results in (archive, mod folder) ordinal order.
results.Sort((a, b) =>
{
    int c = string.CompareOrdinal(Path.GetFileName(a.Archive), Path.GetFileName(b.Archive));
    return c != 0 ? c : string.CompareOrdinal(a.Mod, b.Mod);
});

// ---------------------------------------------------------------------------------------
// 4. Answer
// ---------------------------------------------------------------------------------------
var fomods = results.Where(r => r.IsFomod).ToList();
var parsed = fomods.Where(r => r.Parsed).ToList();
var trusted = parsed.Where(r => r.PathsTrusted).ToList();

Console.WriteLine();
Console.WriteLine($"==== FOMOD INSTALLS ====");
Console.WriteLine($"   archives read                : {results.Count:N0}");
Console.WriteLine($"   ...that carry a FOMOD        : {fomods.Count:N0}");
Console.WriteLine($"   ...whose ModuleConfig parsed : {parsed.Count:N0}   ({fomods.Count - parsed.Count:N0} unreadable — counted, not assumed empty)");
Console.WriteLine($"   ...where path matching is TRUSTED: {trusted.Count:N0}");
Console.WriteLine();
Console.WriteLine($"   The trust test is the detector validating itself before it reports anything.");
Console.WriteLine($"   A FOMOD's requiredInstallFiles are installed UNCONDITIONALLY, so if those files are");
Console.WriteLine($"   not found in the mod folder then this code is matching archive paths to installed");
Console.WriteLine($"   paths wrongly, and every 'not selected' it would report is worthless. Mods that fail");
Console.WriteLine($"   it are excluded and counted rather than quietly reported:");
Console.WriteLine($"      failed the trust test        : {parsed.Count - trusted.Count:N0}");
Console.WriteLine($"      of which, no required files to test with : {parsed.Count(p => !p.PathsTrusted && p.RequiredFiles == 0):N0}");
// The excluded set BY NAME, with the score that excluded each. A count alone let this list drift
// from 2 to 9 across a refactor with nothing in the output saying which mods' verdicts vanished.
foreach (var p in parsed.Where(p => !p.PathsTrusted).OrderBy(p => p.Mod, StringComparer.OrdinalIgnoreCase))
    Console.WriteLine($"         NOT JUDGED  {p.Mod}   (required found {p.RequiredFound}/{p.RequiredFiles}"
                    + (p.RequiredFiles == 0 ? ", no option destination matched anything on disk)" : ")"));

long optTotal = trusted.Sum(r => (long)r.Options.Count);
long optTaken = trusted.Sum(r => (long)r.Options.Count(o => o.Selected));
Console.WriteLine();
Console.WriteLine($"==== OPTIONS ====");
Console.WriteLine($"   options offered across those FOMODs : {optTotal:N0}");
Console.WriteLine($"   ...installed                        : {optTaken:N0}");
Console.WriteLine($"   ...NOT installed                    : {optTotal - optTaken:N0}");
Console.WriteLine($"   ...of those, RULED OUT by an exclusive group where a sibling was taken : {trusted.Sum(r => (long)r.Options.Count(o => o.RuledOut)):N0}");
Console.WriteLine($"   NEVER OFFERED — in a step the installer did not show, given the branch taken : {trusted.Sum(r => (long)r.OptionsNotOffered):N0}");

// The shortlist. An unselected option whose own declared condition names a plugin that IS active
// here is the FOMOD saying, in its own words, that this option applies to this load order.
var wanted = new List<(ModResult M, FomodOption O, List<string> Hits, string Verdict, string Where)>();
int optAlreadyActive = 0;
foreach (var r in trusted)
    foreach (var o in r.Options.Where(o => !o.Selected && o.Judgeable && !o.RuledOut))
    {
        var hits = o.Conditions.Where(active.Contains).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (hits.Count == 0) continue;

        var plugins = o.Files.Where(IsPlugin).ToList();
        if (plugins.Count == 0)
        {
            // No plugin to trace instance-wide. Meshes and INIs are real content but this code
            // cannot tell whether they arrived, so the option is reported at the weakest tier.
            wanted.Add((r, o, hits, "NO-PLUGIN", ""));
            continue;
        }
        if (plugins.All(active.Contains)) { optAlreadyActive++; continue; }

        var homes = plugins.SelectMany(p => pluginHome.GetValueOrDefault(p) ?? new List<string>())
                           .Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(h => h, StringComparer.Ordinal).ToList();
        if (o.WrongVariant) wanted.Add((r, o, hits, "WRONG-VARIANT", string.Join(", ", homes.Take(3))));
        else if (homes.Count == 0) wanted.Add((r, o, hits, "ABSENT", ""));
        else wanted.Add((r, o, hits, "INACTIVE", string.Join(", ", homes.Take(3))));
    }
// Hits are listed by (archive, option, entry) ordinal; step, group and mod folder break ties, so one archive installed into
// several folders is reported for the ordinal-first folder.
wanted = wanted.OrderBy(w => Path.GetFileName(w.M.Archive), StringComparer.Ordinal).ThenBy(w => w.O.Name, StringComparer.Ordinal)
               .ThenBy(w => w.O.Step, StringComparer.Ordinal).ThenBy(w => w.O.Group, StringComparer.Ordinal)
               .ThenBy(w => w.M.Mod, StringComparer.Ordinal).ToList();

if (hitList is not null)
{
    // One hit per (archive, option, plugin entry): MO2 installs one archive into several folders, which repeats
    // the same option once per folder, and the bytes are the archive's either way.
    var hashTimer = Stopwatch.StartNew();
    int extracted = 0;
    var seenHit = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    foreach (var (m, o, condHits, verdict, where) in wanted)
    {
        var detail = new Dictionary<string, object?>
        {
            ["verdict"] = verdict, ["conditions"] = o.Conditions, ["activeConditions"] = condHits, ["mod"] = m.Mod,
            ["option"] = o.Name, ["step"] = o.Step, ["group"] = o.Group, ["onDiskIn"] = where,
        };
        var archiveName = Path.GetFileName(m.Archive);
        var plugins = o.Files.Where(IsPlugin).ToList();
        if (plugins.Count == 0)
        {
            if (seenHit.Add($"{archiveName}|{o.Name}|"))
                hitList.Hit(o.Name, "fomod-option", new() { ["archive"] = archiveName, ["path"] = m.Archive }, null, "",
                            null, detail, "the option delivers no plugin, so there are no plugin bytes to hash");
            continue;
        }
        // within an option, by archive entry ordinal (by the plugin's name when the archive has no entry for it)
        var items = plugins.SelectMany(plugin =>
        {
            var entries = m.Entries.Where(e => string.Equals(Path.GetFileName(e.Replace('/', '\\').TrimEnd('\\')), plugin,
                                                             StringComparison.OrdinalIgnoreCase)).ToList();
            return entries.Count == 0
                ? new[] { (Key: plugin, Plugin: plugin, Entry: (string?)null, Count: 0) }
                : entries.Select(e => (Key: e, Plugin: plugin, Entry: (string?)e, Count: entries.Count)).ToArray();
        }).OrderBy(i => i.Key, StringComparer.Ordinal).ThenBy(i => i.Plugin, StringComparer.Ordinal).ToList();
        foreach (var (_, plugin, entryOrNull, entryCount) in items)
        {
            if (entryOrNull is null)
            {
                if (seenHit.Add($"{archiveName}|{o.Name}|{plugin}"))
                    hitList.Hit(plugin, "fomod-option", new() { ["archive"] = archiveName, ["path"] = m.Archive }, null, "",
                                null, detail, $"no entry named {plugin} in the archive listing, so its bytes were not read");
                continue;
            }
            var entry = entryOrNull;
            if (!seenHit.Add($"{archiveName}|{o.Name}|{entry}")) continue;
            var bytes = Run7z($"e -so -bso0 -bse0 \"{m.Archive}\" \"{entry}\"", out var ok);
            extracted++;
            var d = new Dictionary<string, object?>(detail) { ["ambiguousEntry"] = entryCount > 1 };
            var w = new Dictionary<string, object?> { ["archive"] = archiveName, ["entry"] = entry, ["path"] = m.Archive };
            if (ok && bytes.Length > 0)
                hitList.Hit(plugin, "fomod-option", w, DiscoveryHits.HashBytes(bytes), "archive-entry", null, d);
            else
                hitList.Hit(plugin, "fomod-option", w, null, "", null, d,
                            $"7-Zip could not extract {entry} from {archiveName}", ioFailure: true);
        }
    }
    hitList.Detail("hashExtraction", new Dictionary<string, object?>
    {
        ["plugins"] = extracted, ["seconds"] = Math.Round(hashTimer.Elapsed.TotalSeconds, 3),
    });
    Console.WriteLine($"   hash extraction: {extracted} plugin(s) read from their archives in {hashTimer.Elapsed.TotalSeconds:0.000}s");
}

Console.WriteLine();
Console.WriteLine($"==== NOT SELECTED, and the FOMOD's OWN condition names a plugin active here ====");
Console.WriteLine($"   WRONG-VARIANT  an exclusive group where the option that WAS taken does not match");
Console.WriteLine($"                  this load order, and this one does : {wanted.Count(w => w.Verdict == "WRONG-VARIANT"):N0}   <- the strongest tier");
Console.WriteLine($"   ABSENT    the patch exists nowhere in this instance : {wanted.Count(w => w.Verdict == "ABSENT"):N0}");
Console.WriteLine($"   INACTIVE  it IS on disk here, just not switched on  : {wanted.Count(w => w.Verdict == "INACTIVE"):N0}");
Console.WriteLine($"   NO-PLUGIN the option ships no ESP, so it cannot be traced: {wanted.Count(w => w.Verdict == "NO-PLUGIN"):N0}");
Console.WriteLine($"   (a further {optAlreadyActive:N0} unselected options deliver a plugin that is already ACTIVE from elsewhere");
Console.WriteLine($"    — taken through another mod folder or installed by hand, and correctly not reported)");
Console.WriteLine($"   Not verified in game. This says the installer offered a patch for something you run;");
Console.WriteLine($"   whether you WANT it is a separate question the data cannot answer.");

foreach (var tier in new[] { "WRONG-VARIANT", "ABSENT", "INACTIVE", "NO-PLUGIN" })
{
    var bucket = wanted.Where(w => w.Verdict == tier).ToList();
    if (bucket.Count == 0) continue;
    Console.WriteLine($"\n   ════ {tier} ({bucket.Count:N0}) ════");
    foreach (var g in bucket.GroupBy(w => w.M.Mod).OrderByDescending(g => g.Count()).Take(listN))
    {
        Console.WriteLine($"\n   ---- {g.Key}  ({g.Count()} options) ----");
        foreach (var (_, o, hits, _, where) in g.Take(10))
        {
            Console.WriteLine($"      '{o.Name}'   [step '{o.Step}' / group '{o.Group}' ({o.GroupType})]");
            if (o.Files.Count > 0) Console.WriteLine($"         installs: {string.Join(", ", o.Files.Take(4))}{(o.Files.Count > 4 ? $" (+{o.Files.Count - 4})" : "")}");
            Console.WriteLine($"         its condition names ACTIVE: {string.Join(", ", hits.Take(4))}");
            if (where.Length > 0) Console.WriteLine($"         already on disk in: {where}");
            var d = (o.Description ?? "").Replace('\n', ' ').Replace('\r', ' ').Trim();
            if (d.Length > 0) Console.WriteLine($"         \"{d[..Math.Min(110, d.Length)]}\"");
        }
        if (g.Count() > 10) Console.WriteLine($"      ... and {g.Count() - 10} more in this mod");
    }
}

// One archive installed into several mod folders reports the same option once per folder, so the
// finding count overstates the work. This is the list of actual distinct things to do.
var distinctAbsent = wanted.Where(w => w.Verdict == "ABSENT")
                           .SelectMany(w => w.O.Files.Where(IsPlugin).Select(f => (File: f, w.O.Name, Cond: string.Join("/", w.Hits))))
                           .GroupBy(x => x.File, StringComparer.OrdinalIgnoreCase)
                           .OrderBy(g => g.Key).ToList();
Console.WriteLine();
Console.WriteLine($"   ---- DISTINCT patches behind the ABSENT findings: {distinctAbsent.Count:N0} ----");
Console.WriteLine($"   (the finding count is higher because MO2 installs one archive into several mod folders)");
foreach (var g in distinctAbsent)
    Console.WriteLine($"      {g.Key,-58} for {g.First().Cond}");

// Weaker, and labelled so: no condition to check, but it delivers a plugin that is not installed.
var silent = trusted.SelectMany(r => r.Options.Where(o => !o.Selected && o.Judgeable && !o.RuledOut && o.Conditions.Count == 0 && o.Files.Any(IsPlugin))
                                              .Select(o => (M: r, O: o))).ToList();
Console.WriteLine();
Console.WriteLine($"==== NOT SELECTED, delivers a plugin, but states no condition ====");
Console.WriteLine($"   findings: {silent.Count:N0}   — WEAKER: nothing here says the option applies to you.");
Console.WriteLine($"   Listed by mod only, because reading anything into an unconditioned option would be a guess.");
foreach (var g in silent.GroupBy(s => s.M.Mod).OrderByDescending(g => g.Count()).Take(listN))
    Console.WriteLine($"   {g.Count(),4} options   {g.Key}");

hitList?.Write(jsonPath!, parsed.Count, fomods.Count - parsed.Count);
Console.WriteLine($"\nelapsed {sw.Elapsed.TotalSeconds:0.0}s");
return 0;

// ---------------------------------------------------------------------------------------

static bool IsPlugin(string p)
{
    var e = Path.GetExtension(p);
    return e.Equals(".esp", StringComparison.OrdinalIgnoreCase)
        || e.Equals(".esm", StringComparison.OrdinalIgnoreCase)
        || e.Equals(".esl", StringComparison.OrdinalIgnoreCase);
}

static string Norm(string p) => p.Replace('/', '\\').Trim('\\').ToLowerInvariant();

ModResult Examine(string mod, string modDir, string archive)
{
    var r = new ModResult(mod, archive);

    var entries = Run7z($"l -slt -ba -sccUTF-8 \"{archive}\"", out var listOk);
    if (!listOk) return r;
    var paths = new List<string>();
    foreach (var line in Encoding.UTF8.GetString(entries).Split('\n'))
        if (line.StartsWith("Path = ", StringComparison.Ordinal))
            paths.Add(line[7..].TrimEnd('\r'));

    r.Entries.AddRange(paths);
    var config = paths.FirstOrDefault(p => Norm(p).EndsWith("\\fomod\\moduleconfig.xml", StringComparison.Ordinal)
                                        || Norm(p) == "fomod\\moduleconfig.xml");
    if (config is null) return r;
    r.IsFomod = true;

    var xmlBytes = Run7z($"e -so -bso0 -bse0 \"{archive}\" \"{config}\"", out var exOk);
    if (!exOk || xmlBytes.Length == 0) return r;

    XDocument doc;
    // The real files in this pool are a mix of UTF-8 and UTF-16LE-with-BOM. Loading from the raw
    // byte stream lets the XML reader detect that itself instead of this code deciding.
    try { using var ms = new MemoryStream(xmlBytes); doc = XDocument.Load(ms); }
    catch { return r; }
    r.Parsed = true;

    var root = doc.Root;
    if (root is null) return r;

    // What this ARCHIVE actually put on disk, as normalised relative paths — the union over every
    // mod folder installed from it (method rule 9, finally applied to the trust test: MO2's
    // "install each option as its own mod" spreads one archive across many folders, and asking
    // only this folder refused 8 patch collections a whole session).
    var folders = archiveFolders.TryGetValue(Path.GetFileName(archive), out var shared) && shared.Count > 0
        ? shared : new List<string> { modDir };
    var installed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    try
    {
        foreach (var dir in folders)
            foreach (var f in EnumerateFilesSafe(dir))
                installed.Add(Norm(Path.GetRelativePath(dir, f)));
    }
    catch { return r; }

    // The RULES live in _shared/FomodModel.cs so they can be proven without an archive, a mod
    // folder or a load order (see proofprobe). This function owns the I/O and the two predicates;
    // the model owns every decision the five corrections were about.
    bool Present(string dest)
    {
        var d = FomodModel.Norm(dest);
        // Destination "" is the Data root: its contents landed there if anything did at all.
        if (d.Length == 0) return installed.Count > 0;
        if (installed.Contains(d)) return true;
        // A folder source contributes its CONTENTS, so treat it as present when anything landed
        // under it. Matching only exact paths would call every folder-shaped option unselected.
        foreach (var f in installed) if (f.StartsWith(d + "\\", StringComparison.Ordinal)) return true;
        return false;
    }

    var parse = FomodModel.Parse(doc, Present, active.Contains);
    r.RequiredFiles = parse.RequiredFiles;
    r.RequiredFound = parse.RequiredFound;
    r.InstalledFiles = installed.Count;
    r.OptionsNotOffered = parse.OptionsNotOffered;
    r.PathsTrusted = parse.PathsTrusted;
    r.Options.AddRange(parse.Options);

    return r;
}

static IEnumerable<string> EnumerateFilesSafe(string root)
{
    var stack = new Stack<string>();
    stack.Push(root);
    while (stack.Count > 0)
    {
        var dir = stack.Pop();
        string[] files, subs;
        try { files = Directory.GetFiles(dir); subs = Directory.GetDirectories(dir); }
        catch { continue; }                       // one unreadable folder costs that folder only
        foreach (var f in files) yield return f;
        foreach (var s in subs) stack.Push(s);
    }
}

// NOT static: it reads SevenZip, which is now a resolved local rather than a compile-time
// const (see ../_shared/HarnessTarget.cs), and a static local function cannot capture one.
byte[] Run7z(string arguments, out bool ok)
{
    ok = false;
    try
    {
        using var p = Process.Start(new ProcessStartInfo(SevenZip, arguments)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        });
        if (p is null) return Array.Empty<byte>();
        // Read the RAW stream: a ModuleConfig.xml may be UTF-16, and letting a TextReader guess at
        // it would corrupt it before the XML reader ever sees the declaration.
        using var ms = new MemoryStream();
        var copy = p.StandardOutput.BaseStream.CopyToAsync(ms);
        var drainErr = p.StandardError.ReadToEndAsync();
        if (!p.WaitForExit(120_000)) { try { p.Kill(true); } catch { } return Array.Empty<byte>(); }
        copy.Wait(10_000);
        drainErr.Wait(5_000);
        ok = p.ExitCode == 0;
        return ms.ToArray();
    }
    catch { return Array.Empty<byte>(); }
}

sealed class ModResult(string mod, string archive)
{
    public string Mod { get; } = mod;
    public string Archive { get; } = archive;
    public bool IsFomod { get; set; }
    public bool Parsed { get; set; }
    public bool PathsTrusted { get; set; }
    public int RequiredFiles { get; set; }
    public int RequiredFound { get; set; }
    public int InstalledFiles { get; set; }
    /// <summary>Options in steps the installer never showed, because the branch they belong to was not taken.</summary>
    public int OptionsNotOffered { get; set; }
    public List<FomodOption> Options { get; } = new();
    /// <summary>Every entry path in the archive's listing, so an option's plugin can be found and hashed.</summary>
    public List<string> Entries { get; } = new();
}
