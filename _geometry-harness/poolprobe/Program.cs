using System.Diagnostics;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Skyrim;
using HousecarlCore.Progress;

// DOWNLOADS NEVER INSTALLED — the patches you already own and have never unpacked.
//
// The shared download pool holds 14,062 archives. patchprobe can only see the ones somebody
// installed somewhere, because its index is built from plugins on disk; for the rest it falls back
// to matching a .meta file's mod NAME, which surfaces candidates and cannot confirm one. A name is
// a guess. A master list is a fact.
//
// So this opens them. For every archive that no MO2 instance on this machine ever installed, read
// the plugins inside and ask the same question the applicability filter asks: is every master this
// plugin declares present and active in the TARGET instance right now? If it is, that archive is a patch
// that would install cleanly today, sitting unopened in a folder you already have.
//
// ARCHIVE GROUNDING, per the standing rule. Reading is done by the 7-Zip command line, checked
// against its own usage output rather than assumed, so there is no
// third-party library surface to guess at:
//     7z l -slt -ba -sccUTF-8 <archive>            technical listing, one "Path = ..." per entry
//     7z x -o<dir> <archive> <entry>               that entry to disk, keeping its filename
// The filename has to survive extraction because Mutagen derives a plugin's ModKey from it.
//
// usage: poolprobe [--list N] [--jobs N] [--max N]

// Nothing is compiled in — see ../_shared/HarnessTarget.cs. Search roots are a drive layout,
// the most machine-specific thing a probe can carry.
using var __run = RunLog.BeginProcess("poolprobe", args);

var roots = HarnessTarget.RequireRoots(HarnessTarget.SearchRoots.ToList());
string Pool = HarnessTarget.Require(HarnessTarget.DownloadPool, "download pool directory", "downloadPool");
string Instance = HarnessTarget.Require(HarnessTarget.Instance, "MO2 instance directory", "instance");
string Profile = HarnessTarget.Require(HarnessTarget.Profile, "MO2 profile name", "profile");
string SevenZip = HarnessTarget.Require(HarnessTarget.SevenZip, "path to 7z.exe", "sevenZip");

int listN = 40;
int jobs = Math.Max(2, Environment.ProcessorCount - 2);
int max = int.MaxValue;
for (int i = 0; i < args.Length; i++)
{
    if (args[i] == "--list" && i + 1 < args.Length) listN = int.Parse(args[++i]);
    else if (args[i] == "--jobs" && i + 1 < args.Length) jobs = int.Parse(args[++i]);
    else if (args[i] == "--max" && i + 1 < args.Length) max = int.Parse(args[++i]);
}
if (!File.Exists(SevenZip)) { Console.Error.WriteLine($"7-Zip not found at {SevenZip}"); return 1; }

// USSEP is not vanilla, but nearly everything masters it, so declaring it says as little about a
// plugin's purpose as declaring Skyrim.esm does.
var Ubiquitous = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
{
    "Skyrim.esm", "Update.esm", "Dawnguard.esm", "HearthFires.esm", "Dragonborn.esm",
    "SkyrimVR.esm", "Unofficial Skyrim Special Edition Patch.esp",
};

// Creation Club plugins belong in the same bucket for this instance, which has the whole CC
// catalogue active. Without this the ranking is led by CC compatibility megapatches declaring
// 38 masters apiece — RAEQuestRequirements.esp, CBBE 3BA CC Beasts.esp — which score highest on
// master count while saying nothing about the mods this project is actually reconciling.
static bool IsCreationClub(string m) => m.StartsWith("cc", StringComparison.OrdinalIgnoreCase)
                                     && m.Length > 6
                                     && (m.EndsWith(".esl", StringComparison.OrdinalIgnoreCase)
                                      || m.EndsWith(".esm", StringComparison.OrdinalIgnoreCase));

var sw = Stopwatch.StartNew();

// ---------------------------------------------------------------------------------------
// 1. What is active in the target profile
// ---------------------------------------------------------------------------------------
// plugins.txt omits the base-game masters entirely; anything loadorder.txt lists that plugins.txt
// never mentions is exactly one of those implicit always-on plugins.
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

// Everything that already exists as a plugin file anywhere in the target instance. An archive whose
// plugin is already unpacked here is not a "never installed" finding, whatever its meta.ini says.
var here = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
foreach (var d in Directory.EnumerateDirectories(Path.Combine(Instance, "mods")))
{
    try { foreach (var f in Directory.GetFiles(d)) if (IsPlugin(f)) here.Add(Path.GetFileName(f)); }
    catch { }
}

Console.WriteLine($"target: {Instance}  profile '{Profile}'");
Console.WriteLine($"   {active.Count:N0} active plugins, {here.Count:N0} plugin files present in the instance");

// ---------------------------------------------------------------------------------------
// 2. Every archive any MO2 instance on this machine ever installed
// ---------------------------------------------------------------------------------------
var everInstalled = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
int instances = 0;
__run.Feature("roots", roots.Count, "roots").Stage("walk", "walking the download roots", roots.Count, "roots");

foreach (var root in roots)
{
    __run.Advance();
    if (!Directory.Exists(root)) continue;
    // A recursive enumeration throws partway through on the first unreadable directory and loses
    // everything found so far, so the walk is manual with a per-directory catch.
    var stack = new Stack<(string Dir, int Depth)>();
    stack.Push((root, 0));
    while (stack.Count > 0)
    {
        var (dir, depth) = stack.Pop();
        try
        {
            foreach (var ini in Directory.EnumerateFiles(dir, "ModOrganizer.ini"))
            {
                var mods = Path.Combine(Path.GetDirectoryName(ini)!, "mods");
                if (!Directory.Exists(mods)) continue;
                instances++;
                foreach (var modDir in Directory.EnumerateDirectories(mods))
                {
                    var meta = Path.Combine(modDir, "meta.ini");
                    if (!File.Exists(meta)) continue;
                    try
                    {
                        foreach (var line in File.ReadLines(meta))
                            if (line.StartsWith("installationFile=", StringComparison.OrdinalIgnoreCase))
                            {
                                var v = line.Split('=', 2)[1].Trim();
                                if (v.Length > 0) everInstalled.Add(Path.GetFileName(v));
                                break;
                            }
                    }
                    catch { }
                }
            }
            if (depth < 3)
                foreach (var sub in Directory.EnumerateDirectories(dir))
                {
                    var leaf = Path.GetFileName(sub);
                    if (leaf.StartsWith('$') || leaf.Equals("System Volume Information", StringComparison.OrdinalIgnoreCase)) continue;
                    stack.Push((sub, depth + 1));
                }
        }
        catch { }
    }
}
Console.WriteLine($"   {instances} MO2 instances scanned; {everInstalled.Count:N0} distinct archives were installed by at least one of them  ({sw.Elapsed.TotalSeconds:0.0}s)");

// ---------------------------------------------------------------------------------------
// 3. The archives nobody ever installed
// ---------------------------------------------------------------------------------------
var pool = Directory.EnumerateFiles(Pool)
    .Where(f => { var e = Path.GetExtension(f); return e.Equals(".7z", StringComparison.OrdinalIgnoreCase)
                                                    || e.Equals(".zip", StringComparison.OrdinalIgnoreCase)
                                                    || e.Equals(".rar", StringComparison.OrdinalIgnoreCase); })
    .ToList();
var never = pool.Where(f => !everInstalled.Contains(Path.GetFileName(f))).Take(max).ToList();
Console.WriteLine($"   pool holds {pool.Count:N0} archives; {never.Count:N0} of them were never installed anywhere");

// ---------------------------------------------------------------------------------------
// 4. Look inside
// ---------------------------------------------------------------------------------------
var scratch = Path.Combine(Path.GetTempPath(), "poolprobe-" + Environment.ProcessId);
Directory.CreateDirectory(scratch);

var found = new List<Hit>();
int done = 0, withPlugins = 0, unreadable = 0, pluginsUnreadable = 0;
var whyUnreadable = new Dictionary<string, int>(StringComparer.Ordinal);
var unreadableExamples = new List<string>();
var unlistableArchives = new List<string>();
var noExtractExamples = new List<string>();
static void Bump(Dictionary<string, int> d, string k) => d[k] = d.GetValueOrDefault(k) + 1;
var padlock = new object();

Parallel.ForEach(never, new ParallelOptions { MaxDegreeOfParallelism = jobs }, arc =>
{
    var listing = Run7z($"l -slt -ba -sccUTF-8 \"{arc}\"", null, out var ok);
    if (!ok) { lock (padlock) { unreadable++; unlistableArchives.Add(Path.GetFileName(arc)); done++; } return; }

    // ⚠️ A .esp IS NOT ALWAYS A PLUGIN. FaceGen, voice and LOD mods ship DIRECTORIES named after the
    // plugin they belong to — `meshes\...\FaceGeom\Whatever.esp\` holds per-NPC .nif files, and
    // `Sound\Voice\Whatever.esp\` holds .fuz. Matching on the extension alone treated all of them as
    // plugins, 7-Zip declined to extract a directory as a file, and the run reported 221 "plugins
    // that would not extract or parse" — every one of them a false alarm, and none of them a lost
    // finding. `-slt` marks them `Attributes = D`, so the fix is to read the attribute rather than
    // trust the name. An entry with no Attributes line is treated as a FILE, so an unusual format
    // costs a wasted extraction rather than a silently skipped plugin.
    var plugins = new List<string>();
    {
        string? pending = null;
        void Flush(bool isDir) { if (pending is not null && !isDir) plugins.Add(pending); pending = null; }
        foreach (var raw in listing.Split('\n'))
        {
            var line = raw.TrimEnd('\r');
            if (line.StartsWith("Path = ", StringComparison.Ordinal))
            {
                Flush(false);                                    // previous entry had no Attributes line
                var p = line[7..];
                if (IsPlugin(p)) pending = p;
            }
            else if (pending is not null && line.StartsWith("Attributes = ", StringComparison.Ordinal))
                Flush(line[13..].Contains('D'));
        }
        Flush(false);
    }

    if (plugins.Count == 0) { lock (padlock) { done++; } return; }

    // ⚠️ ONE EXTRACTION PER ARCHIVE, NOT PER PLUGIN. The first version called 7z once per entry and
    // took 201s for 300 archives — a full pass would have been well over two hours. A solid .7z or
    // .rar has to walk the compressed stream from the start for every extraction, so N calls cost
    // N passes over the whole archive. Passing every wanted entry to a single call collapses that
    // to one pass, and the same 300 archives finish in a fraction of the time.
    var want = plugins.Take(60)
                      .Where(e => !here.Contains(Path.GetFileName(e.Replace('/', '\\'))))
                      .ToList();
    if (want.Count == 0) { lock (padlock) { done++; } return; }

    var local = new List<Hit>();
    // A private folder per archive: two archives in flight can hold plugins with the same leaf
    // name, and the leaf name has to survive because Mutagen derives the ModKey from it. `x` keeps
    // the archive's directory structure, so duplicate leaves inside ONE archive stay separate too.
    var dir = Path.Combine(scratch, Guid.NewGuid().ToString("N"));
    try
    {
        Directory.CreateDirectory(dir);
        var entryArgs = string.Join(" ", want.Select(e => $"\"{e}\""));
        Run7z($"x -y -o\"{dir}\" \"{arc}\" {entryArgs}", null, out var exOk);

        var extracted = Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories).Where(IsPlugin).ToList();
        // "Counted but unexplained" is only half of method rule 6. A tally of failures says how much
        // was not judged; it does not say whether the failures are one systematic class hiding real
        // findings or scattered corruption that costs nothing. So the REASON is recorded too.
        if (extracted.Count == 0)
            lock (padlock)
            {
                pluginsUnreadable += want.Count;
                // Count PLUGINS, not archives — the first version bumped the reason once per archive
                // while the total counted per plugin, so the tally explained 30 of 221 and looked
                // like most failures had no reason at all.
                var why = exOk ? "7z reported success but extracted nothing (entry-name matching)" : "7z extract failed";
                whyUnreadable[why] = whyUnreadable.GetValueOrDefault(why) + want.Count;
                if (noExtractExamples.Count < 8)
                    noExtractExamples.Add($"{Path.GetFileName(arc)}   wanted e.g. \"{want[0]}\"");
            }

        foreach (var onDisk in extracted)
        {
            string[] masters;
            try
            {
                using var m = SkyrimMod.CreateFromBinaryOverlay(onDisk, SkyrimRelease.SkyrimSE);
                masters = m.ModHeader.MasterReferences.Select(x => x.Master.FileName.String).ToArray();
            }
            catch (Exception ex)
            {
                long size = 0;
                try { size = new FileInfo(onDisk).Length; } catch { }
                lock (padlock)
                {
                    pluginsUnreadable++;
                    Bump(whyUnreadable, size == 0 ? "zero-byte file" : $"{ex.GetType().Name}");
                    if (unreadableExamples.Count < 12)
                        unreadableExamples.Add($"{Path.GetFileName(onDisk)}  ({size:N0} bytes)  {ex.GetType().Name}: {ex.Message.Split('\n')[0]}");
                }
                continue;
            }

            var absent = masters.Where(x => !active.Contains(x)).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
            var real = masters.Where(x => !Ubiquitous.Contains(x) && !IsCreationClub(x)).ToArray();
            local.Add(new Hit(Path.GetFileName(arc), Path.GetFileName(onDisk), masters, absent, real));
        }
    }
    finally { try { if (Directory.Exists(dir)) Directory.Delete(dir, true); } catch { } }

    lock (padlock)
    {
        found.AddRange(local);
        if (local.Count > 0) withPlugins++;
        if (++done % 500 == 0) Console.WriteLine($"   ... {done:N0}/{never.Count:N0} archives, {found.Count:N0} plugins read, {sw.Elapsed.TotalSeconds:0}s");
    }
});

try { Directory.Delete(scratch, true); } catch { }

// ---------------------------------------------------------------------------------------
// 5. Answer
// ---------------------------------------------------------------------------------------
Console.WriteLine();
Console.WriteLine($"==== NEVER-INSTALLED ARCHIVES ====");
Console.WriteLine($"   examined                        : {never.Count:N0}");
Console.WriteLine($"   ...that hold a plugin not already unpacked here : {withPlugins:N0}");
Console.WriteLine($"   plugins read                    : {found.Count:N0}");
Console.WriteLine($"   ---- not judged, and why ----");
Console.WriteLine($"   archives 7-Zip could not list   : {unreadable:N0}   (verified by hand: truncated downloads — \"Cannot open the file as [7z] archive\")");
Console.WriteLine($"   plugins that would not extract or parse : {pluginsUnreadable:N0}");
foreach (var w in whyUnreadable.OrderByDescending(w => w.Value))
    Console.WriteLine($"      {w.Value,5}  {w.Key}");
foreach (var e in unreadableExamples) Console.WriteLine($"      e.g. {e}");
foreach (var e in noExtractExamples) Console.WriteLine($"      no-extract: {e}");
foreach (var a in unlistableArchives) Console.WriteLine($"      unlistable archive: {a}");

// Same two tests the cross-list filter uses, for the same reasons: every master must be active for
// the file to install cleanly, and a plugin whose only masters are vanilla + USSEP is a mod rather
// than a patch, so it answers a question nobody asked.
var applicable = found.Where(h => h.Absent.Length == 0).ToList();
var patches = applicable.Where(h => h.RealMasters.Length > 0).ToList();
var nearMiss = found.Where(h => h.Absent.Length == 1 && h.RealMasters.Length > 0).ToList();

Console.WriteLine();
Console.WriteLine($"   APPLICABLE now (every declared master active here)      : {applicable.Count:N0}");
Console.WriteLine($"   ...and PATCH-SHAPED (masters more than vanilla + USSEP) : {patches.Count:N0}   <- the answer");
Console.WriteLine($"   one master short                                        : {nearMiss.Count:N0}");
Console.WriteLine();
Console.WriteLine($"   Downloaded, never installed by any instance on this machine, and every master it");
Console.WriteLine($"   needs is active in {Path.GetFileName(Instance)} right now. Not verified in game, and this says");
Console.WriteLine($"   nothing about whether the patch is correct or wanted — only that it would install.");

// One plugin can sit in several archive VERSIONS of the same mod (Placed Light 2.3.6 / 2.4.2 /
// 2.4.3 all carry the same ESP). That is one thing to do, not three, so the list is by plugin and
// the extra versions are counted rather than repeated as rows.
var distinct = patches.GroupBy(h => h.Plugin, StringComparer.OrdinalIgnoreCase)
                      .OrderByDescending(g => g.First().RealMasters.Length)
                      .ThenBy(g => g.Key).ToList();
Console.WriteLine($"   ...which are {distinct.Count:N0} DISTINCT plugins (the rest are other versions of the same download)");

foreach (var g in distinct.Take(listN))
{
    var h = g.OrderByDescending(x => x.Archive, StringComparer.Ordinal).First();
    Console.WriteLine($"\n   {h.Plugin}");
    Console.WriteLine($"      reconciles: {string.Join(", ", h.RealMasters)}");
    Console.WriteLine($"      in archive: {h.Archive}" + (g.Count() > 1 ? $"   (+{g.Count() - 1} other version(s) of the same download)" : ""));
}

Console.WriteLine($"\n   ---- ONE MASTER SHORT ----");
foreach (var g in nearMiss.GroupBy(h => h.Absent[0], StringComparer.OrdinalIgnoreCase)
                          .OrderByDescending(g => g.Count()).Take(listN))
    Console.WriteLine($"   {g.Count(),4} downloaded patches wait on   {g.Key}    e.g. {g.First().Plugin}");

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

// NOT static: it reads SevenZip, which is now a resolved local rather than a compile-time
// const (see ../_shared/HarnessTarget.cs), and a static local function cannot capture one.
string Run7z(string arguments, string? _, out bool ok)
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
        if (p is null) return "";
        var outT = p.StandardOutput.ReadToEndAsync();
        var errT = p.StandardError.ReadToEndAsync();
        if (!p.WaitForExit(120_000)) { try { p.Kill(true); } catch { } return ""; }
        outT.Wait(10_000);
        errT.Wait(5_000);
        ok = p.ExitCode == 0;
        return outT.IsCompletedSuccessfully ? outT.Result : "";
    }
    catch { return ""; }
}

record Hit(string Archive, string Plugin, string[] Masters, string[] Absent, string[] RealMasters);
