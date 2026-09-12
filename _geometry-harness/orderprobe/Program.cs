using System.Diagnostics;
using HarnessShared;
using HousecarlCore;
using HousecarlCore.Progress;

// ORDERPROBE — bounded, constrained alternatives to the user's load order (roadmap 4.20, 2026-09-11).
//
// Every other probe in this harness answers a question about ONE order: the one the profile happens to hold. That
// makes every finding conditional on a choice nobody was asked to justify, and the tool has never been able to say
// "this would also work" or even "here is what you would have to move". orderprobe is the input side of that: it
// reads the profile, works out which plugins actually interact, and writes a small, ordered, deterministic set of
// LEGAL alternative orders for the detectors to judge. It judges nothing itself.
//
// WHAT IT WRITES
//
//   candidates.json          the manifest — the baseline's configuration identity, the rules that were read and
//                            where each came from, the contradictions, the interaction groups, every candidate
//                            with its changes and what it costs the user, and the budget outcome in words.
//   candidate-00-baseline.json
//   candidate-NN-<slug>.json one CandidateFile each (_shared/Configuration.cs), the input a probe takes with
//                            --order FILE.
//
// usage: orderprobe [--rules FILE] [--budget N] [--out DIR] [--instance DIR] [--profile NAME] [--game DIR]
//
//   --rules FILE   the ordering constraints, with provenance. Schema and the refusals are documented in the header
//                  of _shared/OrderRules.cs — read it before writing one; advice without a source is refused.
//   --budget N     how many candidates to emit, baseline excluded (default 8). The outcome always says what the
//                  budget cost: how many legal candidates were held back, and whether enumeration itself stopped
//                  early. A search that stopped early reports its unexplored count as unknown, never as zero.
//   --out DIR      where the manifest and the candidate files go (default: ./orderprobe-out).
//
// WHAT IT DOES NOT DO, said here so nobody has to infer it:
//   * it does not decide whether an order WORKS. That is a detector's answer, and "no working order found" is never
//     a proof that none exists.
//   * it does not change asset priority. Every candidate's Mods is null, so the asset view stays the profile's,
//     and the manifest says so under limits.
//   * it compares LOOSE files only when looking for asset contention. BSA contents are not opened.

using var __run = RunLog.BeginProcess("orderprobe", args);

string Instance = HarnessTarget.Instance;
string GamePath = HarnessTarget.Game;
string profileName = HarnessTarget.Profile;
string? rulesPath = null;
string outDir = Path.Combine(Directory.GetCurrentDirectory(), "orderprobe-out");
int budget = 8;

for (int i = 0; i < args.Length; i++)
{
    if (args[i] == "--instance" && i + 1 < args.Length) Instance = args[++i];
    else if (args[i] == "--game" && i + 1 < args.Length) GamePath = args[++i];
    else if (args[i] == "--profile" && i + 1 < args.Length) profileName = args[++i];
    else if (args[i] == "--rules" && i + 1 < args.Length) rulesPath = args[++i];
    else if (args[i] == "--out" && i + 1 < args.Length) outDir = args[++i];
    else if (args[i] == "--budget" && i + 1 < args.Length)
    {
        if (!int.TryParse(args[++i], out budget) || budget < 0)
        { Console.Error.WriteLine($"--budget needs a whole number of candidates (0 or more); got '{args[i]}'."); return 2; }
    }
    else if (args[i].StartsWith("--"))
    { Console.Error.WriteLine($"unknown option '{args[i]}'. See the header of orderprobe/Program.cs for the flag list."); return 2; }
}

Instance = HarnessTarget.Require(Instance, "MO2 instance directory", "instance", "--instance");
GamePath = HarnessTarget.Require(GamePath, "game directory", "game", "--game");
profileName = HarnessTarget.Require(profileName, "MO2 profile name", "profile", "--profile");

string profileDir = Path.Combine(Instance, "profiles", profileName);
string modsDir = Path.Combine(Instance, "mods");
string overwriteDir = Path.Combine(Instance, "overwrite");
string dataDir = Path.Combine(GamePath, "Data");

var sw = Stopwatch.StartNew();
var warnings = new List<string>();
var comp = Mo2LoadOrder.ReadComposition(profileDir, warnings);
var map = SyntheticOrder.BuildFilenameMap(comp, modsDir, dataDir, overwriteDir);
var inactive = new HashSet<string>(comp.InactivePluginNames, StringComparer.OrdinalIgnoreCase);

// The baseline: the plugins loadorder.txt lists that plugins.txt does not switch off, in loadorder.txt's order.
// Exactly the set and the order SyntheticOrder's no-op build produces, so a candidate is comparable to a synthetic
// what-if without either having to explain its own idea of "loaded".
var baseline = new List<string>();
var paths = new List<string>();
foreach (var name in comp.OrderedPluginNames)
{
    if (inactive.Contains(name)) continue;
    if (!map.TryGetValue(name, out var p))
    { warnings.Add($"'{name}' is in the load order but no mod folder, overwrite or Data provides it — it is left out of the baseline."); continue; }
    baseline.Add(name);
    paths.Add(p);
}

Console.WriteLine($"orderprobe — {profileName} @ {Instance}");
Console.WriteLine($"baseline: {baseline.Count} loaded plugin(s) of {comp.OrderedPluginNames.Count} in loadorder.txt\n");
foreach (var w in warnings) Console.WriteLine($"   ! {w}");

// ---------------------------------------------------------------------------------------------------------
//  The configuration the candidates are relative to.
// ---------------------------------------------------------------------------------------------------------
__run.Feature("plugins", baseline.Count, "plugins").Stage("identity", "hashing the baseline configuration", baseline.Count, "plugins");

var headers = new Dictionary<string, PluginHeader?>(StringComparer.OrdinalIgnoreCase);
var configPlugins = new List<ConfigPlugin>();
for (int i = 0; i < baseline.Count; i++)
{
    var name = baseline[i]; var path = paths[i];
    var h = PluginHeader.TryRead(path);
    headers[name] = h;
    string sha; long bytes;
    try { sha = ConfigurationIdentity.Sha256File(path); bytes = new FileInfo(path).Length; }
    catch (Exception ex) { sha = "unreadable"; bytes = -1; warnings.Add($"'{name}': could not be hashed — {ex.Message}"); }
    configPlugins.Add(new ConfigPlugin(name, path, sha, bytes, ProviderOf(path),
                                       h?.MasterFlag ?? false, h?.LightFlag ?? false,
                                       h?.Masters ?? Array.Empty<string>()));
    __run.Progress(i + 1);
}

var providers = new List<ConfigProvider> { new("overwrite", "overwrite") };
foreach (var m in comp.EnabledMods) providers.Add(new ConfigProvider(m, "mod"));
providers.Add(new ConfigProvider("Data", "data"));

var limits = new List<string>
{
    "candidates change PLUGIN ORDER only: every candidate's Mods is null, so the asset view stays this profile's and asset-priority alternatives are not explored by this milestone",
    "asset contention: loose files only, BSA contents not compared",
    "orderprobe resolves no enable population of its own; the identity names the load-time/baseline configuration a default detector run consumes, so the fingerprints are comparable",
};

var identity = ConfigurationIdentity.Compute($"profile:{profileName}", configPlugins, providers,
                                             Array.Empty<ConfigOverlay>(), "load-time", "baseline", limits);
Console.WriteLine($"\nbaseline identity: {identity.Short}");

// ---------------------------------------------------------------------------------------------------------
//  The rules.
// ---------------------------------------------------------------------------------------------------------
ParsedRules parsed;
try { parsed = rulesPath is null ? new ParsedRules(Array.Empty<OrderRule>(), Array.Empty<string>(), new[] { "no --rules file: only the engine's own rules apply" }) : OrderRules.ParseFile(rulesPath); }
catch (Exception ex) { Console.Error.WriteLine($"REFUSED to read the rules: {ex.Message}"); return 2; }

var ruleSet = OrderRuleSet.Build(baseline, headers, parsed.Fixed, parsed.Rules);
Console.WriteLine($"rules: {parsed.Rules.Count} from the file, {ruleSet.EngineRules.Count} derived from the plugins, {parsed.Fixed.Count} fixed");
foreach (var n in parsed.Notes.Concat(ruleSet.Notes).Take(20)) Console.WriteLine($"   note: {n}");
if (parsed.Notes.Count + ruleSet.Notes.Count > 20) Console.WriteLine($"   ... and {parsed.Notes.Count + ruleSet.Notes.Count - 20} more note(s) — see the manifest");

var baseLegality = ruleSet.Check(baseline);
Console.WriteLine($"the user's own order: {baseLegality.Summary}");
foreach (var v in baseLegality.Violations.Take(10)) Console.WriteLine($"   VIOLATION {v}");

foreach (var c in ruleSet.Contradictions)
{ Console.WriteLine($"\nCONTRADICTION ({c.Kind}) — {c.Explanation}"); }
foreach (var c in ruleSet.UnsatisfiableSoft)
{ Console.WriteLine($"\nUNSATISFIABLE ADVICE ({c.Kind}) — {c.Explanation}"); }

// ---------------------------------------------------------------------------------------------------------
//  The contention graph: who actually interacts with whom.
// ---------------------------------------------------------------------------------------------------------
__run.Stage("contention", "finding what interacts with what", baseline.Count, "plugins");
var edges = new Dictionary<string, ContentionEdge>(StringComparer.OrdinalIgnoreCase);
void Bump(string a, string b, string kind, int n)
{
    if (a.Equals(b, StringComparison.OrdinalIgnoreCase)) return;
    var (x, y) = string.Compare(a, b, StringComparison.OrdinalIgnoreCase) <= 0 ? (a, b) : (b, a);
    var key = $"{kind}|{x}|{y}";
    edges[key] = edges.TryGetValue(key, out var e) ? e with { Count = e.Count + n } : new ContentionEdge(x, y, kind, n);
}

int contestedRecords = 0;
try
{
    using var resolver = LoadOrderResolver.Build(paths);
    var view = resolver.Capture();
    foreach (var fk in view.ConflictKeys())
    {
        var touching = view.TouchingPlugins(fk);
        if (touching is null || touching.Count < 2) continue;
        contestedRecords++;
        for (int a = 0; a < touching.Count; a++)
            for (int b = a + 1; b < touching.Count; b++)
                Bump(touching[a], touching[b], "record", 1);
    }
    foreach (var f in view.LoadFailures.Take(10)) warnings.Add($"record contention: {f}");
}
catch (Exception ex)
{
    warnings.Add($"record contention could not be computed — {ex.Message}. The groups below rest on asset contention alone, " +
                 "so a pair that only contests records is MISSING from them, not proven absent.");
}
Console.WriteLine($"\ncontention: {contestedRecords} record(s) overridden by more than one plugin");

// assets: two enabled mod folders that provide the same relative path. Mapped to plugin pairs through the folders'
// own plugins — a contended folder that ships no plugin in this order produces no edge, and that is counted, not hidden.
int assetPaths = 0, unmappedFolders = 0, filesWalked = 0;
const int FileCap = 400_000;
bool fileCapHit = false;
{
    var byPath = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
    var folderPlugins = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
    var baselineSet = new HashSet<string>(baseline, StringComparer.OrdinalIgnoreCase);
    foreach (var mod in comp.EnabledMods)
    {
        var root = Path.Combine(modsDir, mod);
        if (!Directory.Exists(root)) continue;
        folderPlugins[mod] = new List<string>();
        IEnumerable<string> files;
        try { files = Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories); }
        catch { continue; }
        foreach (var f in files)
        {
            if (++filesWalked > FileCap) { fileCapHit = true; break; }
            var rel = Path.GetRelativePath(root, f);
            var ext = Path.GetExtension(f);
            if (ext.Equals(".esp", StringComparison.OrdinalIgnoreCase) || ext.Equals(".esm", StringComparison.OrdinalIgnoreCase)
             || ext.Equals(".esl", StringComparison.OrdinalIgnoreCase))
            {
                var pn = Path.GetFileName(f);
                if (baselineSet.Contains(pn)) folderPlugins[mod].Add(pn);
                continue;                                    // a duplicated plugin FILE is not asset contention
            }
            if (rel.Equals("meta.ini", StringComparison.OrdinalIgnoreCase)) continue;
            if (!byPath.TryGetValue(rel, out var l)) byPath[rel] = l = new List<string>();
            l.Add(mod);
        }
        if (fileCapHit) break;
    }
    if (fileCapHit)
        warnings.Add($"asset scan stopped at {FileCap:N0} files; asset contention below is PARTIAL, and a pair missing from it is unmeasured, not clean.");

    foreach (var (rel, mods) in byPath)
    {
        if (mods.Count < 2) continue;
        assetPaths++;
        var distinct = mods.Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(s => s, StringComparer.OrdinalIgnoreCase).ToList();
        for (int a = 0; a < distinct.Count; a++)
            for (int b = a + 1; b < distinct.Count; b++)
            {
                var pa = folderPlugins.TryGetValue(distinct[a], out var la) ? la : new List<string>();
                var pb = folderPlugins.TryGetValue(distinct[b], out var lb) ? lb : new List<string>();
                if (pa.Count == 0 || pb.Count == 0) { unmappedFolders++; continue; }
                foreach (var x in pa) foreach (var y in pb) Bump(x, y, "asset", 1);
            }
    }
}
Console.WriteLine($"contention: {assetPaths} relative path(s) provided by more than one enabled mod folder ({filesWalked:N0} loose files walked)");
if (unmappedFolders > 0)
{
    var note = $"{unmappedFolders} asset-contending folder pair(s) produced no plugin-level edge because at least one side ships no plugin in this order";
    limits.Add(note);
    Console.WriteLine($"   {note}");
}

// rules name pairs too: a rule is a statement that two plugins interact, whatever the files say
foreach (var r in ruleSet.Input) Bump(r.Before, r.After, "explicit", 1);

// ---------------------------------------------------------------------------------------------------------
//  The search.
// ---------------------------------------------------------------------------------------------------------
var edgeList = edges.Values.OrderBy(e => e.A, StringComparer.OrdinalIgnoreCase)
                           .ThenBy(e => e.B, StringComparer.OrdinalIgnoreCase)
                           .ThenBy(e => e.Kind, StringComparer.Ordinal).ToList();
__run.Stage("candidates", "building constrained alternatives", Math.Max(1, budget), "candidates");
var report = OrderSearch.Run(ruleSet, edgeList, budget);
__run.Progress(report.Emitted);

Console.WriteLine($"\ngroups: {report.Groups.Count} interaction group(s) over {edgeList.Count} contention edge(s)");
foreach (var g in report.Groups.Take(15))
    Console.WriteLine($"   [{g.Key}] {g.Members.Count} member(s), contention {g.Contention}: {string.Join(", ", g.Members.Take(8))}{(g.Members.Count > 8 ? ", ..." : "")}");
if (report.Groups.Count > 15) Console.WriteLine($"   ... and {report.Groups.Count - 15} more — see the manifest");

Console.WriteLine($"\nresult: {report.Result}");
Console.WriteLine(Wrap(report.Outcome, 110));
foreach (var n in report.Notes) Console.WriteLine($"   note: {n}");
foreach (var r in report.RejectedMoves.Take(10)) Console.WriteLine($"   rejected: {r}");
if (report.RejectedMoves.Count > 10) Console.WriteLine($"   ... and {report.RejectedMoves.Count - 10} more rejected move(s) — see the manifest");

Console.WriteLine();
foreach (var c in report.Candidates)
{
    Console.WriteLine($"   {c.Name,-44} contested {c.Contention,6}  displaced {c.Displaced,4}  phase {c.Phase}  {(c.Legality.Legal ? "legal" : "ILLEGAL")}"
                    + (c.SatisfiesAdvice.Count > 0 ? $"  satisfies {c.SatisfiesAdvice.Count} advice rule(s)" : "")
                    + (c.ViolatesAdvice.Count > 0 ? $"  violates {c.ViolatesAdvice.Count} advice rule(s)" : ""));
    foreach (var ch in c.Changes) Console.WriteLine($"        {ch}");
}

// ---------------------------------------------------------------------------------------------------------
//  The output files.
// ---------------------------------------------------------------------------------------------------------
Directory.CreateDirectory(outDir);
var manifestCandidates = new List<ManifestCandidate>();
foreach (var c in report.Candidates)
{
    var file = $"candidate-{c.Name}.json";
    var prov = new Dictionary<string, string>
    {
        ["search"] = c.Phase == 0 ? "the user's own order, unchanged" : $"phase {c.Phase} ({(c.Phase == 1 ? "one move" : "two composed moves")})",
        ["groups"] = c.Groups.Count == 0 ? "-" : string.Join(", ", c.Groups),
        ["satisfies"] = c.SatisfiesAdvice.Count == 0 ? "-" : string.Join(" | ", c.SatisfiesAdvice.Select(r => r.Full)),
        ["violates"] = c.ViolatesAdvice.Count == 0 ? "-" : string.Join(" | ", c.ViolatesAdvice.Select(r => r.Full)),
        ["legality"] = c.Legality.Summary,
        ["contention"] = $"{c.Contention} contested record(s) and path(s) between the plugins this candidate moves",
    };
    new CandidateFile(ConfigurationIdentity.CurrentSchema, c.Name, profileName, c.Plugins, null, c.Changes, prov)
        .Write(Path.Combine(outDir, file));
    manifestCandidates.Add(new ManifestCandidate(
        c.Name, file, c.Changes, c.Displaced, c.Contention, c.Moves.Count, c.Phase, c.Groups, c.Legality.Legal,
        c.Legality.Violations.Select(v => v.ToString()).ToList(),
        c.SatisfiesAdvice.Select(r => r.Full).ToList(),
        c.ViolatesAdvice.Select(r => r.Full).ToList()));
}

var presentNames = new HashSet<string>(baseline, StringComparer.OrdinalIgnoreCase);
var manifest = new OrderManifest(
    ConfigurationIdentity.CurrentSchema, "orderprobe", Instance, profileName, identity,
    ruleSet.Fixed,
    ruleSet.Input.Select(r => new ManifestRule(r.Before, r.After, r.Hardness.ToString().ToLowerInvariant(), r.Origin,
                                               r.Reason, OrderManifest.Prov(r.Provenance),
                                               presentNames.Contains(r.Before) && presentNames.Contains(r.After))).ToList(),
    parsed.Notes.Concat(ruleSet.Notes).ToList(),
    ruleSet.Contradictions.Select(OrderManifest.Of).ToList(),
    ruleSet.UnsatisfiableSoft.Select(OrderManifest.Of).ToList(),
    report.Groups.Select(g => new ManifestGroup(g.Key, g.Members, g.Contention,
        g.Edges.Select(e => $"{e.A} | {e.B} | {e.Kind} | {e.Count}").ToList())).ToList(),
    manifestCandidates,
    new ManifestBudget(budget, report.Result.ToString(), report.Emitted, report.GeneratedNotEmitted,
                       report.Unexplored, report.EnumerationCutShort, report.Outcome),
    report.RejectedMoves, report.Notes.Concat(warnings).ToList(), limits);

var manifestPath = Path.Combine(outDir, "candidates.json");
manifest.Write(manifestPath);
Console.WriteLine($"\nwrote {manifestPath} and {report.Candidates.Count} candidate file(s) in {sw.Elapsed.TotalSeconds:F1}s");
return report.Result == SearchResult.ConstraintsUnsatisfiable ? 1 : 0;

// ---------------------------------------------------------------------------------------------------------

string ProviderOf(string path)
{
    var full = Path.GetFullPath(path);
    if (!string.IsNullOrWhiteSpace(overwriteDir) && full.StartsWith(Path.GetFullPath(overwriteDir), StringComparison.OrdinalIgnoreCase)) return "overwrite";
    var mroot = Path.GetFullPath(modsDir);
    if (full.StartsWith(mroot, StringComparison.OrdinalIgnoreCase))
    {
        var rel = Path.GetRelativePath(mroot, full);
        var i = rel.IndexOfAny(new[] { Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar });
        return i > 0 ? rel[..i] : rel;
    }
    if (!string.IsNullOrWhiteSpace(dataDir) && full.StartsWith(Path.GetFullPath(dataDir), StringComparison.OrdinalIgnoreCase)) return "Data";
    return "unknown";
}

static string Wrap(string s, int width)
{
    var words = s.Split(' ');
    var sb = new System.Text.StringBuilder();
    int col = 0;
    foreach (var w in words)
    {
        if (col + w.Length + 1 > width) { sb.Append('\n'); col = 0; }
        else if (col > 0) { sb.Append(' '); col++; }
        sb.Append(w); col += w.Length;
    }
    return sb.ToString();
}
