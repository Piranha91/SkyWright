using System.Diagnostics;
using HarnessShared;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Skyrim;
using HousecarlCore.Progress;

// PATCH AVAILABILITY — "is there a patch for this anywhere on this machine?"
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
// exact FormKeys a detector emitted with --emit. See ../_shared/ConflictReport.cs.
//
// usage: patchprobe [--pair A.esp B.esp] [--verify conflicts.json] [--list N] [--roots "C:\a;D:\b"]

// Nothing is compiled in — see ../_shared/HarnessTarget.cs. --roots overrides the sweep list;
// Require runs after the flag loop so passing it never trips the check.
using var __run = RunLog.BeginProcess("patchprobe", args);

var roots = HarnessTarget.SearchRoots.ToList();
string downloadPool = HarnessTarget.DownloadPool;
string target = HarnessTarget.Instance;
var wantPairs = new List<(string A, string B)>();
int listN = 30;
var verifyFiles = new List<string>();

for (int i = 0; i < args.Length; i++)
{
    if (args[i] == "--pair" && i + 2 < args.Length) { wantPairs.Add((args[i + 1], args[i + 2])); i += 2; }
    else if (args[i] == "--list" && i + 1 < args.Length) listN = int.Parse(args[++i]);
    else if (args[i] == "--roots" && i + 1 < args.Length) { roots = args[++i].Split(';').ToList(); }
    else if (args[i] == "--verify" && i + 1 < args.Length) verifyFiles.Add(args[++i]);
}
roots = HarnessTarget.RequireRoots(roots, "--roots");
downloadPool = HarnessTarget.Require(downloadPool, "download pool directory", "downloadPool");
target = HarnessTarget.Require(target, "MO2 instance directory", "instance");

var sw = Stopwatch.StartNew();

// ---------------------------------------------------------------------------------------
// 1. Find every MO2 instance
// ---------------------------------------------------------------------------------------
var instances = new List<Instance>();
foreach (var root in roots)
{
    if (!Directory.Exists(root)) continue;
    // A recursive enumeration THROWS partway through on the first unreadable directory (a drive
    // root has System Volume Information), losing every result found so far. Walk it manually so
    // one denied folder costs that folder and nothing else.
    var inis = new List<string>();
    var stack = new Stack<(string Dir, int Depth)>();
    stack.Push((root, 0));
    while (stack.Count > 0)
    {
        var (dir0, depth) = stack.Pop();
        try
        {
            foreach (var f in Directory.EnumerateFiles(dir0, "ModOrganizer.ini")) inis.Add(f);
            if (depth < 3)
                foreach (var sub in Directory.EnumerateDirectories(dir0))
                {
                    var leaf = Path.GetFileName(sub);
                    if (leaf.StartsWith('$') || leaf.Equals("System Volume Information", StringComparison.OrdinalIgnoreCase)) continue;
                    stack.Push((sub, depth + 1));
                }
        }
        catch { /* unreadable directory: skip it, keep everything else */ }
    }

    foreach (var ini in inis)
    {
        var dir = Path.GetDirectoryName(ini)!;
        if (dir.Contains("$RECYCLE", StringComparison.OrdinalIgnoreCase)) continue;
        var mods = Path.Combine(dir, "mods");
        if (!Directory.Exists(mods)) continue;

        string? profile = null;
        foreach (var line in File.ReadLines(ini))
            if (line.StartsWith("selected_profile", StringComparison.OrdinalIgnoreCase))
            {
                // MO2 writes this as @ByteArray(Name); the name is what is between the parentheses.
                int o = line.IndexOf('('), c = line.LastIndexOf(')');
                profile = o >= 0 && c > o ? line[(o + 1)..c] : line.Split('=', 2).Last().Trim();
            }

        var active = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (profile is not null)
        {
            // plugins.txt does NOT list the base-game masters at all — MO2 treats Skyrim.esm,
            // Update.esm, the DLC and SkyrimVR.esm as implicitly always on. Reading only plugins.txt
            // therefore reports Skyrim.esm as INACTIVE, which made the applicability filter reject
            // 134 patches for "missing" Skyrim.esm.
            //
            // Derived, not hardcoded to a game: loadorder.txt lists EVERY plugin in order, so
            // anything in loadorder.txt that plugins.txt never mentions is exactly an implicit
            // always-on master. Here that is the 6 that make 484 -> 490, which is the plugin count
            // Mo2LoadOrder.Build independently arrives at.
            var pdir = Path.Combine(dir, "profiles", profile);
            var plugins = Path.Combine(pdir, "plugins.txt");
            var listed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (File.Exists(plugins))
                foreach (var line in File.ReadLines(plugins))
                {
                    var t = line.Trim();
                    if (t.Length == 0 || t.StartsWith('#')) continue;
                    var name = t.StartsWith('*') ? t[1..].Trim() : t;
                    listed.Add(name);
                    if (t.StartsWith('*')) active.Add(name);
                }

            var loadorder = Path.Combine(pdir, "loadorder.txt");
            if (File.Exists(loadorder))
                foreach (var line in File.ReadLines(loadorder))
                {
                    var t = line.Trim();
                    if (t.Length == 0 || t.StartsWith('#')) continue;
                    if (!listed.Contains(t)) active.Add(t);
                }
        }
        instances.Add(new Instance(Path.GetFileName(dir), dir, mods, profile ?? "?", active));
    }
}

// Compare as normalised full paths: the configured target may carry forward slashes or a trailing separator
// (harness.local.json is hand-written), the discovered directory never does.
static string Norm(string d) => Path.TrimEndingDirectorySeparator(Path.GetFullPath(d));
var targetInst = instances.FirstOrDefault(i => string.Equals(Norm(i.Dir), Norm(target), StringComparison.OrdinalIgnoreCase));
Console.WriteLine($"MO2 instances found: {instances.Count}");
foreach (var inst in instances)
    Console.WriteLine($"   {(inst == targetInst ? "TARGET " : "       ")}{inst.Name,-28} profile '{inst.Profile}'  {inst.Active.Count,5} active plugins");
if (targetInst is null) { Console.Error.WriteLine("target instance not found."); return 1; }

// ---------------------------------------------------------------------------------------
// 2. Index every plugin in every instance by the master pairs it reconciles
// ---------------------------------------------------------------------------------------
// A plugin with N masters asserts up to N*(N-1)/2 pairwise reconciliations. Capped, because a
// merged patch or a big overhaul can master dozens of plugins and would otherwise dominate the
// index with pairs it does not meaningfully reconcile.
const int MaxMasters = 12;
var byPair = new Dictionary<(string, string), List<Found>>();
var byMaster = new Dictionary<string, List<Found>>(StringComparer.OrdinalIgnoreCase);
var seenPlugin = new Dictionary<string, List<Found>>(StringComparer.OrdinalIgnoreCase);
int scanned = 0, unreadable = 0;
__run.Feature("instances", instances.Count, "instances").Stage("index", "indexing every instance's plugins", instances.Count, "instances");


foreach (var inst in instances)
{
    __run.Advance();
    foreach (var modDir in Directory.EnumerateDirectories(inst.Mods))
    {
        foreach (var pl in Directory.EnumerateFiles(modDir).Where(IsPlugin))
        {
            scanned++;
            string[] masters;
            try
            {
                using var m = SkyrimMod.CreateFromBinaryOverlay(pl, SkyrimRelease.SkyrimSE);
                masters = m.ModHeader.MasterReferences.Select(x => x.Master.FileName.String).ToArray();
            }
            catch { unreadable++; continue; }
            if (masters.Length < 2) continue;

            string file = Path.GetFileName(pl);
            var state = inst == targetInst
                ? (inst.Active.Contains(file) ? Where.Active : Where.Installed)
                : (inst.Active.Contains(file) ? Where.OtherListActive : Where.OtherListPresent);
            var f = new Found(file, inst.Name, Path.GetFileName(modDir), state, pl, masters);

            if (!seenPlugin.TryGetValue(file, out var lst)) seenPlugin[file] = lst = new List<Found>();
            lst.Add(f);

            // Indexed by EACH master as well as by pair. The pair index is capped at 12 masters so a
            // merged megapatch cannot flood it, but --verify must still be able to find that megapatch:
            // there the question is not "does this look like a patch for A and B" but "does this file
            // override the record that is broken", and a big patch is a perfectly good answer to it.
            foreach (var m in masters)
            {
                var mk = m.ToLowerInvariant();
                if (!byMaster.TryGetValue(mk, out var mb)) byMaster[mk] = mb = new List<Found>();
                mb.Add(f);
            }

            if (masters.Length > MaxMasters) continue;
            for (int a = 0; a < masters.Length; a++)
                for (int b = a + 1; b < masters.Length; b++)
                {
                    var key = Key(masters[a], masters[b]);
                    if (!byPair.TryGetValue(key, out var bag)) byPair[key] = bag = new List<Found>();
                    bag.Add(f);
                }
        }
    }
    Console.WriteLine($"   scanned {inst.Name,-28} {scanned:N0} plugins so far  ({sw.Elapsed.TotalSeconds:0}s)");
}
Console.WriteLine($"\nindexed {scanned:N0} plugins ({unreadable:N0} unreadable), {byPair.Count:N0} distinct master pairs  ({sw.Elapsed.TotalSeconds:0.0}s)");

// ---------------------------------------------------------------------------------------
// 3. The shared download pool, by name only
// ---------------------------------------------------------------------------------------
// No archive is opened. MO2 writes a .meta beside every download naming the mod it came from, so
// the pool can be searched for CANDIDATES cheaply. This is a weaker signal than a master pair and
// is labelled as such — it says "an archive with a promising name exists", never "a patch exists".
var poolNames = new List<(string File, string Name, string ModName)>();
if (Directory.Exists(downloadPool))
{
    foreach (var meta in Directory.EnumerateFiles(downloadPool, "*.meta"))
    {
        string? name = null, modName = null;
        try
        {
            foreach (var line in File.ReadLines(meta))
            {
                if (line.StartsWith("name =", StringComparison.OrdinalIgnoreCase) || line.StartsWith("name=", StringComparison.OrdinalIgnoreCase))
                    name ??= line.Split('=', 2)[1].Trim();
                else if (line.StartsWith("modName", StringComparison.OrdinalIgnoreCase))
                    modName ??= line.Split('=', 2)[1].Trim();
                if (name is not null && modName is not null) break;
            }
        }
        catch { continue; }
        poolNames.Add((Path.GetFileNameWithoutExtension(meta), name ?? "", modName ?? ""));
    }
}
Console.WriteLine($"download pool: {poolNames.Count:N0} archives with metadata at {downloadPool}");

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
            foreach (var f in g.OrderBy(x => x.Instance)) Console.WriteLine($"          {f.Instance,-26} mod '{f.Mod}'  ({f.State})");
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
    foreach (var c in cand) Console.WriteLine($"    [DOWNLOAD (name)] {c.File}    (mod page: {c.ModName})");
}

// ---------------------------------------------------------------------------------------
// 5. --verify — does the candidate touch the RECORDS that are broken?
// ---------------------------------------------------------------------------------------
// The honest limitation of everything above: mastering both mods is an assertion that they need
// reconciling ABOUT SOMETHING, not about this. RRSC-SurWR-Patch.esp masters USSEP and SurWR and is
// a children's-appearance patch. The detectors already know the exact records, so this reads the
// candidate's records and scores the overlap. Nothing is inferred and there is no threshold: a
// FormKey is in the patch or it is not.
var recordCache = new Dictionary<string, HashSet<FormKey>?>(StringComparer.OrdinalIgnoreCase);

// "Masters both" carries information only when both sides are things a plugin could choose not to
// master. Everything masters Skyrim.esm, so a pair with a base-game master on one side narrows
// nothing, and a match found through it rests ENTIRELY on the record overlap. Marked, not hidden:
// the record test is still exact, but the pair test behind it is doing no work.
// USSEP is not base game but behaves the same way here — nearly everything masters it — so it is
// listed for the same reason, not because it is vanilla.
var Ubiquitous = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
{
    "Skyrim.esm", "Update.esm", "Dawnguard.esm", "HearthFires.esm", "Dragonborn.esm",
    "SkyrimVR.esm", "Unofficial Skyrim Special Edition Patch.esp",
};

// GENERATED plugins are never an answer to "where can I get a patch". DynDOLOD, ParallaxGen,
// Synthesis and Reqtificator write these from whatever load order they were run against, so the
// copy sitting in another list describes THAT list's mods and paths, not the target's. Worse, they
// are regenerated: a fix imported from one goes stale on the next run. They do genuinely override
// the records — the first run had DynDOLOD.esp and PG_1.esp filling candidate lists — so they are
// excluded explicitly and COUNTED, not silently dropped.
var Generated = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
{
    "DynDOLOD.esm", "DynDOLOD.esp", "Occlusion.esp", "PGPatcher.esp", "PG_1.esp",
    "Synthesis.esp", "Requiem for the Indifferent.esp", "TexGen_Output.esp",
};

HashSet<FormKey>? RecordsOf(string path)
{
    if (recordCache.TryGetValue(path, out var hit)) return hit;
    HashSet<FormKey>? set = null;
    try
    {
        using var m = SkyrimMod.CreateFromBinaryOverlay(path, SkyrimRelease.SkyrimSE);
        set = new HashSet<FormKey>();
        // Every major record, cell and worldspace children included — a navmesh or a placed
        // reference is exactly the kind of record a patch here would be overriding.
        foreach (var rec in m.EnumerateMajorRecords()) set.Add(rec.FormKey);
    }
    catch { set = null; }                      // unreadable: report as unjudgeable, never as clean
    recordCache[path] = set;
    return set;
}

void Verify(string file)
{
    ConflictFile cf;
    try { cf = ConflictIo.Read(file); }
    catch (Exception ex) { Console.Error.WriteLine($"could not read {file}: {ex.Message}"); return; }

    Console.WriteLine();
    Console.WriteLine($"════════ RECORD-LEVEL VERIFICATION — {cf.Conflicts.Count:N0} conflicts from {cf.Probe} ({cf.GeneratedUtc}) ════════");

    int confirmed = 0, partial = 0, unrelated = 0, noCandidate = 0, unpaired = 0, unreadable = 0, weakPair = 0;
    int generatedSkipped = 0, alreadyActive = 0;
    var lines = new List<string>();
    // Roadmap 2.4: the candidates a differential run can actually test — installed in THIS
    // instance, inactive, and covering at least one broken record. Coverage is not repair; these
    // are the inputs to differential-sweep.ps1, which decides what each one really does.
    var differentialCandidates = new Dictionary<string, (int Hits, int Conflicts)>(StringComparer.OrdinalIgnoreCase);

    foreach (var c in cf.Conflicts)
    {
        if (c.B.Length == 0 || c.A.Length == 0) { unpaired++; continue; }

        // Candidates: every plugin whose master set contains BOTH sides. Taken from the per-master
        // index rather than the pair index so the 12-master cap does not hide a big combo patch.
        List<Found> cands = new();
        if (byMaster.TryGetValue(c.A, out var la) && byMaster.TryGetValue(c.B, out var lb))
        {
            var setB = lb.Select(f => f.FullPath).ToHashSet(StringComparer.OrdinalIgnoreCase);
            cands = la.Where(f => setB.Contains(f.FullPath))
                      .Where(f => !Generated.Contains(f.Plugin))
                      // The same filename can sit in several mod folders of one instance (a combo
                      // pack and a standalone shipping the same patch). One row per list, not per copy.
                      .GroupBy(f => (f.Plugin.ToLowerInvariant(), f.Instance))
                      .Select(g => g.OrderBy(x => (int)x.State).First())
                      .OrderBy(f => (int)f.State).ToList();
            generatedSkipped += la.Count(f => setB.Contains(f.FullPath) && Generated.Contains(f.Plugin));
        }
        if (cands.Count == 0) { noCandidate++; continue; }

        var need = c.Necessary.Select(r => FormKey.Factory(r.FormKey)).ToHashSet();
        var alt = (c.Alternative ?? Array.Empty<ConflictRecord>()).Select(r => FormKey.Factory(r.FormKey)).ToHashSet();

        var scored = new List<(Found F, int Hit, int Alt, bool Readable)>();
        foreach (var cand in cands)
        {
            var recs = RecordsOf(cand.FullPath);
            if (recs is null) { scored.Add((cand, 0, 0, false)); continue; }
            scored.Add((cand, need.Count(k => recs.Contains(k)), alt.Count(k => recs.Contains(k)), true));
        }

        var best = scored.OrderByDescending(s => s.Hit).ThenByDescending(s => s.Alt).ThenBy(s => (int)s.F.State).First();
        // The verdict rule lives in _shared/DetectorModel.cs, owned by proofprobe cases. CONFIRMED
        // is COVERAGE (every necessary record overridden), not repair — see the differential run.
        string verdict = DetectorModel.Verdict(best.Readable, best.Hit, need.Count, best.Alt);
        if (!best.Readable) unreadable++;
        else if (verdict == "CONFIRMED") confirmed++;
        else if (verdict == "PARTIAL") partial++;
        else unrelated++;

        bool weak = Ubiquitous.Contains(c.A) || Ubiquitous.Contains(c.B);
        if (weak && (verdict == "CONFIRMED" || verdict == "PARTIAL")) weakPair++;

        lines.Add($"\n{verdict + (weak ? "*" : ""),-12}{c.Kind}   {c.A}  x  {c.B}");
        lines.Add($"            {c.Summary}");
        // Every candidate that scored, then a short tail of the zero-scoring ones as evidence that
        // "masters both" was checked and came back empty rather than never being asked.
        var ranked = scored.OrderByDescending(s => s.Hit).ThenByDescending(s => s.Alt).ThenBy(s => (int)s.F.State).ToList();
        foreach (var s in ranked.Where(s => s.Hit > 0 || s.Alt > 0 || !s.Readable)
                                .Concat(ranked.Where(s => s.Readable && s.Hit == 0 && s.Alt == 0).Take(3)))
            lines.Add(!s.Readable
                ? $"            [{s.F.State,-16}] {s.F.Plugin}  — UNREADABLE, cannot be judged"
                : $"            [{s.F.State,-16}] {s.F.Plugin}  overrides {s.Hit} of {need.Count} broken record(s)"
                  + (s.Alt > 0 ? $", plus {s.Alt} of {alt.Count} alternative-fix record(s)" : "")
                  + $"   [{s.F.Instance}]");
        int zeros = ranked.Count(s => s.Readable && s.Hit == 0 && s.Alt == 0);
        if (zeros > 3) lines.Add($"            ... and {zeros - 3} more that master both and override none of them");
        if (best.Hit == 0 && best.Alt == 0 && best.Readable)
            lines.Add($"            -> masters both, touches none of the broken records. Not a fix for THIS.");

        // The uncomfortable case, and the one most worth surfacing: the fix is already switched on
        // and the conflict is STILL detected. That is not "no patch needed" — it means the patch
        // loses to something later, or covers less than it looks like it covers.
        if (best.Hit > 0 && best.F.State == Where.Active)
        {
            alreadyActive++;
            lines.Add($"            -> ALREADY ACTIVE here and the conflict still stands: check load order, or the cover is partial.");
        }

        foreach (var s in scored.Where(s => s.Readable && s.Hit > 0 && s.F.State == Where.Installed))
        {
            var cur = differentialCandidates.GetValueOrDefault(s.F.Plugin);
            differentialCandidates[s.F.Plugin] = (cur.Hits + s.Hit, cur.Conflicts + 1);
        }
    }

    Console.WriteLine($"   CONFIRMED (a candidate overrides every broken record) : {confirmed:N0}");
    Console.WriteLine($"   PARTIAL   (overrides some)                            : {partial:N0}");
    Console.WriteLine($"   UNRELATED (masters both, touches none)                : {unrelated:N0}");
    Console.WriteLine($"   ---- not judged, and why ----");
    Console.WriteLine($"   no candidate plugin masters both                      : {noCandidate:N0}");
    Console.WriteLine($"   conflict names only one party (nothing to look up)    : {unpaired:N0}");
    Console.WriteLine($"   candidate unreadable                                  : {unreadable:N0}");
    Console.WriteLine($"   ---- notes ----");
    Console.WriteLine($"   best candidate is ALREADY ACTIVE, conflict still stands: {alreadyActive:N0}   <- load order, or partial cover");
    Console.WriteLine($"   generated plugins excluded from candidates (DynDOLOD, ParallaxGen, ...): {generatedSkipped:N0}");
    Console.WriteLine($"   * = one side is a plugin nearly everything masters (Skyrim.esm, USSEP...), so 'masters both'");
    Console.WriteLine($"     narrowed nothing and the record overlap is the entire evidence: {weakPair:N0} of the hits above.");
    if (differentialCandidates.Count > 0)
    {
        Console.WriteLine($"   ---- DIFFERENTIAL CANDIDATES: installed here, inactive, cover >0 broken records ----");
        Console.WriteLine($"   (coverage is not repair — run differential-sweep.ps1 over these to get a real verdict)");
        foreach (var (plugin, v) in differentialCandidates.OrderByDescending(kv => kv.Value.Hits))
            Console.WriteLine($"   {v.Hits,4} record(s) over {v.Conflicts,3} conflict(s)   {plugin}");
    }
    foreach (var l in lines.Take(listN * 10)) Console.WriteLine(l);
}

if (verifyFiles.Count > 0)
{
    foreach (var v in verifyFiles) Verify(v);
}
else if (wantPairs.Count > 0)
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
    foreach (var m in patches.OrderByDescending(m => m.Lists.Count).ThenBy(m => m.Plugin).Take(listN))
        Console.WriteLine($"   {m.Lists.Count,2} lists  {m.Plugin}"
                        + $"\n           masters: {string.Join(", ", m.Best.F.Masters.Where(x => !Ubiquitous.Contains(x)))}"
                        + $"\n           in: {string.Join(", ", m.Lists)}");

    Console.WriteLine($"\n   ---- ONE MASTER SHORT: install that one mod and these become available ----");
    foreach (var g in nearMiss.GroupBy(m => m.Best.Absent[0], StringComparer.OrdinalIgnoreCase)
                              .OrderByDescending(g => g.Count()).Take(listN))
        Console.WriteLine($"   {g.Count(),4} patches wait on   {g.Key}    e.g. {g.First().Plugin}");
}

Console.WriteLine($"\nelapsed {sw.Elapsed.TotalSeconds:0.0}s");
return 0;

static bool IsPlugin(string p)
{
    var e = Path.GetExtension(p);
    return e.Equals(".esp", StringComparison.OrdinalIgnoreCase)
        || e.Equals(".esm", StringComparison.OrdinalIgnoreCase)
        || e.Equals(".esl", StringComparison.OrdinalIgnoreCase);
}
static (string, string) Key(string a, string b)
    => string.Compare(a, b, StringComparison.OrdinalIgnoreCase) <= 0 ? (a.ToLowerInvariant(), b.ToLowerInvariant())
                                                                     : (b.ToLowerInvariant(), a.ToLowerInvariant());
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

enum Where { Active = 0, Installed = 1, OtherListActive = 2, OtherListPresent = 3 }
/// <param name="FullPath">Kept so --verify can reopen the candidate and read its RECORDS, not just its header.</param>
/// <param name="Masters">The full declared master set — what --applicable is decided from.</param>
record Found(string Plugin, string Instance, string Mod, Where State, string FullPath, string[] Masters);
record Instance(string Name, string Dir, string Mods, string Profile, HashSet<string> Active);
