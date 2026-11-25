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
// usage: patchprobe --verify conflicts.json [--verify ...] [--list N] [--roots "C:\a;D:\b"]
//
// Finding the patches for a pair (--pair), the cross-list survey and their hit list (--json) are the houseCARL
// integration module's (step 3 of the SkyWright / houseCARL boundary); this program verifies. Both read the same
// sweep of the machine (../_shared/PatchIndex.cs).

// Nothing is compiled in — see ../_shared/HarnessTarget.cs. --roots overrides the sweep list;
// Require runs after the flag loop so passing it never trips the check.
using var __run = RunLog.BeginProcess("patchprobe", args);

var roots = HarnessTarget.SearchRoots.ToList();
string downloadPool = HarnessTarget.DownloadPool;
string target = HarnessTarget.Instance;
int listN = 30;
var verifyFiles = new List<string>();

for (int i = 0; i < args.Length; i++)
{
    if (args[i] is "--pair" or "--json")
    {
        Console.Error.WriteLine($"{args[i]} is not patchprobe's: finding the patches on this machine for a pair, or listing them as a hit list, is the houseCARL integration module's; patchprobe verifies a candidate against a detector's conflicts (--verify FILE).");
        return 2;
    }
    else if (args[i] == "--list" && i + 1 < args.Length) listN = int.Parse(args[++i]);
    else if (args[i] == "--roots" && i + 1 < args.Length) { roots = args[++i].Split(';').ToList(); }
    else if (args[i] == "--verify" && i + 1 < args.Length) verifyFiles.Add(args[++i]);
}
if (verifyFiles.Count == 0)
{
    Console.Error.WriteLine("patchprobe needs --verify FILE (a detector's --emit output): it verifies whether a candidate covers the records that conflict; finding patches for a pair is the houseCARL integration module's.");
    return 2;
}
roots = HarnessTarget.RequireRoots(roots, "--roots");
downloadPool = HarnessTarget.Require(downloadPool, "download pool directory", "downloadPool");
target = HarnessTarget.Require(target, "MO2 instance directory", "instance");
// The run's own instance is searched even when no search root holds it (a manual run picks any instance).
if (File.Exists(Path.Combine(target, "ModOrganizer.ini")) && !roots.Any(r => (Norm(target) + Path.DirectorySeparatorChar).StartsWith(Norm(r) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)))
    roots.Add(target);

var sw = Stopwatch.StartNew();

var index = PatchIndex.Build(roots, target, downloadPool, __run, sw);
if (index is null) return 1;
// the sweep's members, under the names the rest of this program reads (PatchIndex, shared with the integration module's patch finder)
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

// ---------------------------------------------------------------------------------------
// 5. --verify — does the candidate touch the RECORDS that are broken?
// ---------------------------------------------------------------------------------------
// The honest limitation of everything above: mastering both mods is an assertion that they need
// reconciling ABOUT SOMETHING, not about this. RRSC-SurWR-Patch.esp masters USSEP and SurWR and is
// a children's-appearance patch. The detectors already know the exact records, so this reads the
// candidate's records and scores the overlap. Nothing is inferred and there is no threshold: a
// FormKey is in the patch or it is not.
var recordCache = new Dictionary<string, HashSet<FormKey>?>(StringComparer.OrdinalIgnoreCase);


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

foreach (var v in verifyFiles) Verify(v);
Console.WriteLine($"\nelapsed {sw.Elapsed.TotalSeconds:0.0}s");
return 0;
