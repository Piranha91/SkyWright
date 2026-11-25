using System.Diagnostics;
using HarnessShared;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Skyrim;
using HousecarlCore.Progress;

// PATCH AVAILABILITY — "is there a patch for this anywhere on this machine?" (patchfind: the houseCARL integration
// module's patch finder, patchprobe's discovery modes moved here in step 3 of the SkyWright / houseCARL boundary;
// verifying a candidate stays SkyWright's patchprobe --verify, and both read one sweep, SkyWright's _geometry-harness/_shared/PatchIndex.cs)
//
// A detected conflict is only half a deliverable. The other half is what to DO about it, and the
// answer is usually not "write a patch" — it is "someone already wrote one, and you have it".
//
// A patch declares what it reconciles: its MASTERS. A plugin mastering both A and B is, by
// construction, an assertion that A and B need reconciling. So indexing every plugin reachable on
// this machine by its master pairs turns the question into a lookup.
//
// Four places a patch can be, in descending order of "already yours":
//   ACTIVE     installed and enabled in the target profile         -> nothing to do
//   INSTALLED  installed in the target instance but NOT enabled     -> tick the box
//   OTHER LIST installed in another MO2 instance (a curated Wabbajack list) -> import it, and note
//              that a list author already judged this conflict worth patching
//   DOWNLOAD   sitting in the shared archive pool, never installed   -> install it
//
// The third is the one worth having: a curated list is a second opinion from someone who shipped
// these mods together on purpose. If a curated list ships a patch for a pair the TARGET list
// has unpatched, that is a strong recommendation. If a list ships BOTH mods and NO
// patch, that is evidence the pair is tolerable — a weaker signal, but not nothing, and it is
// reported too rather than silently read as "no data".
//
// ⚠️ MASTERING BOTH IS NOT FIXING. A plugin that masters A and B asserts they need reconciling
// about SOMETHING — RRSC-SurWR-Patch.esp masters USSEP and SurWR and is a children's-appearance
// patch. --verify closes that gap by reading the candidate's RECORDS and checking them against the
// exact FormKeys a detector emitted with --emit. See SkyWright's _geometry-harness/_shared/ConflictReport.cs.
//
// usage: patchfind [--pair A.esp B.esp] [--list N] [--roots "C:\a;D:\b"] [--json PATH]
//
// --json PATH writes the hit list (schema 1, DiscoveryHits.cs beside this file) beside the printed answer: every plugin
// that masters an asked pair, every cross-list patch the survey reports, each with the sha256 of its file, and each
// download matched by name with no hash and the reason.

// Nothing is compiled in — see SkyWright's _geometry-harness/_shared/HarnessTarget.cs. --roots overrides the sweep list;
// Require runs after the flag loop so passing it never trips the check.
using var __run = RunLog.BeginProcess("patchfind", args);

var roots = HarnessTarget.SearchRoots.ToList();
string downloadPool = HarnessTarget.DownloadPool;
string target = HarnessTarget.Instance;
var wantPairs = new List<(string A, string B)>();
int listN = 30;
string? jsonPath = null;
bool jsonMalformed = false;

for (int i = 0; i < args.Length; i++)
{
    if (args[i] == "--pair" && i + 2 < args.Length) { wantPairs.Add((args[i + 1], args[i + 2])); i += 2; }
    else if (args[i] == "--list" && i + 1 < args.Length) listN = int.Parse(args[++i]);
    else if (args[i] == "--roots" && i + 1 < args.Length) { roots = args[++i].Split(';').ToList(); }
    else if (args[i] == "--verify")
    {
        Console.Error.WriteLine("--verify is not patchfind's: whether a candidate covers a detector's conflicting records is SkyWright's patchprobe --verify; patchfind finds the patches for a pair (--pair) and lists them (--json).");
        return 2;
    }
    else if (args[i] == "--json" && i + 1 < args.Length && !args[i + 1].StartsWith("--")) jsonPath = args[++i];
    else if (args[i] == "--json") jsonMalformed = true;
}
if (jsonMalformed) { Console.Error.WriteLine("--json needs a file path: pass --json PATH."); return 2; }
var hits = jsonPath is null ? null : new DiscoveryHits("patchfind", wantPairs.Count > 0 ? "pair" : "survey");
roots = HarnessTarget.RequireRoots(roots, "--roots");
downloadPool = HarnessTarget.Require(downloadPool, "download pool directory", "downloadPool");
target = HarnessTarget.Require(target, "MO2 instance directory", "instance");
// The run's own instance is searched even when no search root holds it (a manual run picks any instance).
if (File.Exists(Path.Combine(target, "ModOrganizer.ini")) && !roots.Any(r => (Norm(target) + Path.DirectorySeparatorChar).StartsWith(Norm(r) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)))
    roots.Add(target);

var sw = Stopwatch.StartNew();

var index = PatchIndex.Build(roots, target, downloadPool, __run, sw,
    (kind, path, name) => hits?.Searched(kind, path, name), (kind, path, why) => hits?.NotSearched(kind, path, why));
if (index is null) return 1;
// the sweep's members, under the names the rest of this program reads (PatchIndex, shared with SkyWright's patchprobe --verify)
var instances = index.Instances;
var targetInst = index.Target;
var byPair = index.ByPair;
var byMaster = index.ByMaster;
var seenPlugin = index.SeenPlugin;
int scanned = index.Scanned, unreadable = index.Unreadable;
var poolNames = index.PoolNames;
var Ubiquitous = PatchIndex.Ubiquitous;
var Generated = PatchIndex.Generated;
static string Norm(string d) => PatchIndex.Norm(d);
static bool IsPlugin(string p) => PatchIndex.IsPlugin(p);
static (string, string) Key(string a, string b) => PatchIndex.Key(a, b);

void FileHit(Found f, string kind, Dictionary<string, object?> detail)
{
    var hash = DiscoveryHits.HashFile(f.FullPath, out var why);
    hits!.Hit(f.Plugin, kind, new() { ["instance"] = f.Instance, ["mod"] = f.Mod, ["path"] = f.FullPath },
              hash, "file", f.Masters, detail, why, ioFailure: hash is null);
}
static string KindOf(Where w) => w switch
{
    Where.Active => "active", Where.Installed => "installed", _ => "other-list",
};

// ---------------------------------------------------------------------------------------
// 4. Answer
// ---------------------------------------------------------------------------------------
var targetActive = targetInst.Active;

void Report(string a, string b)
{
    var key = Key(a, b);
    Console.WriteLine();
    Console.WriteLine($"=== {a}  x  {b}");

    bool bothActive = targetActive.Contains(a) && targetActive.Contains(b);
    Console.WriteLine($"    both active in {targetInst.Name}: {(bothActive ? "yes" : "no")}");

    if (!byPair.TryGetValue(key, out var found) || found.Count == 0)
    {
        Console.WriteLine("    NO PATCH anywhere on this machine masters both.");
    }
    else
    {
        foreach (var g in found.GroupBy(f => f.Plugin, StringComparer.OrdinalIgnoreCase).OrderBy(g => g.Min(x => (int)x.State)))
        {
            var best = g.OrderBy(x => (int)x.State).First();
            Console.WriteLine($"    [{best.State,-16}] {g.Key}");
            foreach (var f in g.OrderBy(x => x.Instance))
            {
                Console.WriteLine($"          {f.Instance,-26} mod '{f.Mod}'  ({f.State})");
                if (hits is not null)
                    FileHit(f, KindOf(f.State), new() { ["pair"] = new[] { a, b }, ["state"] = f.State.ToString() });
            }
        }
    }

    // Which OTHER lists ship both mods, and what did they do about it?
    foreach (var inst in instances.Where(i => i != targetInst))
    {
        bool hasA = inst.Active.Contains(a), hasB = inst.Active.Contains(b);
        if (!hasA || !hasB) continue;
        bool patched = byPair.TryGetValue(key, out var f2)
                    && f2.Any(x => x.Instance == inst.Name && (x.State == Where.OtherListActive));
        Console.WriteLine($"    second opinion: {inst.Name} ships BOTH and {(patched ? "PATCHES them" : "does NOT patch them")}");
    }

    // Uninstalled candidates, by name.
    string sa = Stem(a), sb = Stem(b);
    var cand = poolNames.Where(p => Mentions(p, sa) && Mentions(p, sb)).Take(6).ToList();
    foreach (var c in cand)
    {
        Console.WriteLine($"    [DOWNLOAD (name)] {c.File}    (mod page: {c.ModName})");
        hits?.Hit(c.File, "download-name", new() { ["archive"] = c.File, ["modName"] = c.ModName }, null, "", null,
                  new() { ["pair"] = new[] { a, b } },
                  "matched by archive name only; patchprobe does not open archives (poolprobe reads their plugins)");
    }
}

if (wantPairs.Count > 0)
{
    foreach (var (a, b) in wantPairs) Report(a, b);
}
else
{
    // No pair was asked for: answer none rather than guess, say how to ask, then run the cross-list survey.
    Console.WriteLine("\n──────── pairs ────────");
    Console.WriteLine("   no pair asked: pass --pair A.esp B.esp, or --verify FILE (a detector's --emit output), for a per-pair answer");

    // ---------------------------------------------------------------------------------------
    // Cross-list patches, filtered to the ones that would actually apply.
    // ---------------------------------------------------------------------------------------
    // Unfiltered, this list runs to thousands of plugins and is unreadable, because most of them
    // patch mods the target list does not have. A patch is only
    // installable if EVERY master it declares is present and active here — that is a hard,
    // checkable property of the file, not a heuristic, and it turns the list into "patches that
    // would apply to you right now".
    //
    // The near-miss tier is kept deliberately: a patch missing exactly one master is a different
    // and still-useful answer ("this becomes available if you add that one mod"), and folding it
    // into either bucket would lose it.
    Console.WriteLine($"\n──────── patches present in ANOTHER list but not active here ────────");

    var missing = seenPlugin
        .Where(kv => !targetActive.Contains(kv.Key)
                  && kv.Value.Any(f => f.State == Where.OtherListActive))
        .Select(kv =>
        {
            var occs = kv.Value.Where(f => f.State == Where.OtherListActive).ToList();
            // Per OCCURRENCE: the same filename can carry different masters in different lists, and
            // the applicable one is the copy you would actually install.
            var scored = occs.Select(f => (F: f, Absent: f.Masters
                                .Where(m => !string.Equals(m, kv.Key, StringComparison.OrdinalIgnoreCase))
                                .Where(m => !targetActive.Contains(m))
                                .Distinct(StringComparer.OrdinalIgnoreCase).ToArray()))
                             .OrderBy(x => x.Absent.Length).ToList();
            return (Plugin: kv.Key,
                    Lists: occs.Select(f => f.Instance).Distinct().ToList(),
                    Best: scored[0]);
        })
        .ToList();

    var applicable = missing.Where(m => m.Best.Absent.Length == 0).ToList();
    var nearMiss = missing.Where(m => m.Best.Absent.Length == 1).ToList();

    // Applicability alone still lets through body, hair, quest and head mods whose only masters are
    // vanilla. Those are MODS, and a mod whose only
    // masters are vanilla is trivially "applicable" while answering a question nobody asked. What
    // makes a plugin a PATCH is that it masters something a mod author had to opt into — so the
    // filter is: at least one master outside the set nearly everything declares.
    bool PatchShaped(Found f) => f.Masters.Any(m => !Ubiquitous.Contains(m));
    var patches = applicable.Where(m => PatchShaped(m.Best.F) && !Generated.Contains(m.Plugin)).ToList();

    Console.WriteLine($"   {missing.Count:N0} plugins are active in a curated list here but not in {targetInst.Name}");
    Console.WriteLine($"   ...APPLICABLE now (every declared master is active here) : {applicable.Count:N0}");
    Console.WriteLine($"   ...one master short                                      : {nearMiss.Count:N0}");
    Console.WriteLine($"   ...for mods this list does not have                      : {missing.Count - applicable.Count - nearMiss.Count:N0}");
    Console.WriteLine($"   ...and of the applicable, PATCH-SHAPED (masters something");
    Console.WriteLine($"      other than vanilla + USSEP, and is not tool output)   : {patches.Count:N0}   <- the answer");

    Console.WriteLine($"\n   ---- APPLICABLE PATCHES, most-corroborated first ----");
    Console.WriteLine($"   (the list count is the signal: N curated lists shipped this patch and {targetInst.Name} does not)");
    if (hits is not null)
        foreach (var m in patches.Concat(nearMiss.Where(n => PatchShaped(n.Best.F) && !Generated.Contains(n.Plugin))))
            FileHit(m.Best.F, "other-list", new()
            {
                ["lists"] = m.Lists, ["absentMasters"] = m.Best.Absent, ["applicable"] = m.Best.Absent.Length == 0,
            });
    foreach (var m in patches.OrderByDescending(m => m.Lists.Count).ThenBy(m => m.Plugin).Take(listN))
        Console.WriteLine($"   {m.Lists.Count,2} lists  {m.Plugin}"
                        + $"\n           masters: {string.Join(", ", m.Best.F.Masters.Where(x => !Ubiquitous.Contains(x)))}"
                        + $"\n           in: {string.Join(", ", m.Lists)}");

    Console.WriteLine($"\n   ---- ONE MASTER SHORT: install that one mod and these become available ----");
    foreach (var g in nearMiss.GroupBy(m => m.Best.Absent[0], StringComparer.OrdinalIgnoreCase)
                              .OrderByDescending(g => g.Count()).Take(listN))
        Console.WriteLine($"   {g.Count(),4} patches wait on   {g.Key}    e.g. {g.First().Plugin}");
}

if (hits is not null)
{
    hits.Detail("masterPairs", byPair.Count);
    hits.Write(jsonPath!, scanned, unreadable);
}
Console.WriteLine($"\nelapsed {sw.Elapsed.TotalSeconds:0.0}s");
return 0;

static string Stem(string plugin)
{
    var s = Path.GetFileNameWithoutExtension(plugin);
    int dash = s.IndexOf(" - ", StringComparison.Ordinal);
    return (dash > 3 ? s[..dash] : s).Trim();
}
static bool Mentions((string File, string Name, string ModName) p, string stem)
    => p.File.Contains(stem, StringComparison.OrdinalIgnoreCase)
    || p.Name.Contains(stem, StringComparison.OrdinalIgnoreCase)
    || p.ModName.Contains(stem, StringComparison.OrdinalIgnoreCase);
