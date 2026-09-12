using HarnessShared;
using HousecarlCore.Progress;

// CONFLICT-SET DIFF — the second half of the differential chain (roadmap 2.4).
//
// --verify's CONFIRMED means COVERAGE: a candidate overrides every broken record. It does not mean
// repair — the TGC run proved a CONFIRMED patch can repair 8 records and create 8 others. The only
// way to know what a patch actually DOES to the conflict set is to enable it synthetically and
// diff, and this tool is the diff:
//
//     navownprobe --emit base.json
//     navownprobe --enable "Candidate.esp" --base base.json --emit with-candidate.json
//     conflictdiff base.json with-candidate.json
//
// WHAT COUNTS AS REPAIR (2026-09-03; handoff 2026-09-12). A row leaving the conflict set is not a repair by
// itself: every class has a physical half and a provenance half, and a sham patch that masters both plugins
// and re-states the winner's record flips only the provenance half — the geometry is untouched, the row is
// gone, and the first version of this tool called that DIFFERENTIAL-VERIFIED (reproduced on the lab in four
// of nine sham cells across the three detectors). So a row is REPAIRED only when its physical predicate — the
// class's own physical half, carried on every emitted record as `Physical` and, for rows that vanished,
// re-measured under the changed arm by the probe's `--base` and carried as `Predicates` — went Holds -> Gone.
// A row that changed class membership without that flip is RECLASSIFIED; a row that entered with the
// predicate holding is CREATED; anything resting on an unmeasured predicate is UNMEASURED, counted apart.
// The rule is DetectorModel.Credit; the verdict DetectorModel.DifferentialVerdict; both proofprobe-owned.
//
// A ROW'S IDENTITY is the records its predicate is about — (navmesh, loser) for a clobber, the two ends of a
// dangling link, the two refs of an overlap, (dependent, host) for a contact row — never the plugin pair.
// The pair is attribution: a patch that re-provides a dangling navmesh moves the row to a new pair without
// touching the defect, and keyed on the pair that read as one repaired plus one created.
//
// COVERAGE (Part B). Each emit carries what its probe judged and could not, per population. Both blocks are
// printed, and when the changed arm's unjudgeable share rose by more than --tolerance on any population the
// verdict is DIFFERENTIAL-UNJUDGED: a change that made rows unreadable is not repairing them.
//
// The verdict line is DIFFERENTIAL-VERIFIED / -NEUTRAL / -HARMFUL by repaired against created, or
// DIFFERENTIAL-RECLASSIFIED / -UNMEASURED / -INERT when neither moved, or -UNJUDGED. `differential-sweep.ps1`
// drives the whole loop over the candidates --verify names.
//
// POPULATION AND STATE (Q27, 2026-09-06). Credit is a statement about two runs of the same detector over the same
// world. Two emits made on different POPULATIONS (the load-time enable chain against the record's own flag) or in
// different world STATES answer different questions, and a row that vanishes between them tells you the question
// changed, not that a patch repaired anything. So the two identities are compared before anything is credited:
// - different, and named: the run is REFUSED unless --compare-states says the comparison is deliberate, in which case
//   it is labelled on every line of the output rather than silently blessed;
// - unknown on either side (an emit written before Q27 carries neither): also refused, for the same reason. An old
//   emit is not certified equal to a new one; re-emit with the current probe.
//
// CONFIGURATION (roadmap 4.20, 2026-09-11). Population and state say which references a run judged; the CONFIGURATION says
// what it read to judge them — which plugins loaded, in what order, with what content, over which asset providers and runtime
// overlays. Every emit carries one (ConflictFile.Configuration, built by HarnessOrderCli). Unlike the population gate, a
// configuration DIFFERENCE is never refused: it is exactly what the differential is a difference of, so both identities and
// every line of the delta are printed and the run proceeds. Refused instead: an emit that names no configuration at all
// (UNKNOWN is not "the same" — re-emit), and two emits from different probes. And the case with no honest verdict — identical
// configurations that produced different rows — is called DIFFERENTIAL-UNJUDGED with the reason said out loud, because
// nothing the identity covers changed and whatever moved came from outside it. The rule is ConfigurationGate, proof-pinned.
//
// usage: conflictdiff <base.json> <changed.json> [--tolerance PCT] [--compare-states]   (PCT in points; default 1)
//   --compare-states  the two emits are DELIBERATELY of different populations or world states. The diff runs and every
//                     line says so; the verdict is not an ordinary repair verdict.

using var __run = RunLog.BeginProcess("conflictdiff", args);

string? basePath = null, changedPath = null;
double tolerance = 0.01;
bool compareStates = false;
for (int i = 0; i < args.Length; i++)
{
    if (args[i] == "--tolerance" && i + 1 < args.Length) tolerance = double.Parse(args[++i], System.Globalization.CultureInfo.InvariantCulture) / 100.0;
    else if (args[i] == "--compare-states") compareStates = true;
    else if (basePath is null) basePath = args[i];
    else if (changedPath is null) changedPath = args[i];
    else { Console.Error.WriteLine("usage: conflictdiff <base.json> <changed.json> [--tolerance PCT] [--compare-states]"); return 2; }
}
if (basePath is null || changedPath is null) { Console.Error.WriteLine("usage: conflictdiff <base.json> <changed.json> [--tolerance PCT] [--compare-states]"); return 2; }

ConflictFile baseFile, changedFile;
try { baseFile = ConflictIo.Read(basePath); changedFile = ConflictIo.Read(changedPath); }
catch (Exception ex) { Console.Error.WriteLine(ex.Message); return 2; }

static List<Row> Rows(ConflictFile f)
{
    var rows = new List<Row>();
    foreach (var c in f.Conflicts)
    {
        string? other = c.Alternative is { Count: > 0 } alt ? alt[0].FormKey : null;
        foreach (var r in c.Necessary)
            rows.Add(new Row(ConflictIo.RowIdentity(c.Kind, r.FormKey, other, c.A), c.Kind, c.A, c.B, r.FormKey, other, r.Physical, r.PhysicalDetail));
    }
    return rows;
}

var before = Rows(baseFile).GroupBy(r => r.Id).ToDictionary(g => g.Key, g => g.First());
var after = Rows(changedFile).GroupBy(r => r.Id).ToDictionary(g => g.Key, g => g.First());
var predicates = (changedFile.Predicates ?? Array.Empty<Predicate>())
    .GroupBy(p => ConflictIo.RowIdentity(p.Kind, p.FormKey, p.Other, p.A)).ToDictionary(g => g.Key, g => g.First());

int basePredicated = before.Values.Count(r => r.Physical is not null);
Console.WriteLine($"base    : {basePath}  ({baseFile.Conflicts.Count:N0} conflicts, {before.Count:N0} rows; predicates on {basePredicated:N0})");
Console.WriteLine($"changed : {changedPath}  ({changedFile.Conflicts.Count:N0} conflicts, {after.Count:N0} rows; base-row predicates carried: {predicates.Count:N0}"
                + (changedFile.BaseEmit is { } be ? $", measured against {Path.GetFileName(be)}" : "") + ")");
// The population/state gate (Q27), before anything is credited.
bool identityKnown = baseFile.Population is not null && changedFile.Population is not null
                  && baseFile.State is not null && changedFile.State is not null;
bool identitySame = identityKnown
                 && string.Equals(baseFile.Population, changedFile.Population, StringComparison.Ordinal)
                 && string.Equals(baseFile.State, changedFile.State, StringComparison.Ordinal);
Console.WriteLine($"population: base {baseFile.PopulationIdentity} | changed {changedFile.PopulationIdentity}");
if (!identityKnown)
{
    Console.WriteLine("   ! one or both emits do not say which population or world state produced them (written before 2026-09-06).");
    Console.WriteLine("     An unlabelled emit is UNKNOWN, not \"the same\": a row that vanished between two different populations");
    Console.WriteLine("     is not evidence of a repair. Re-emit both with the current probe, or pass --compare-states to say the");
    Console.WriteLine("     comparison is deliberate and read the result as a population diff rather than a repair verdict.");
    if (!compareStates) return 3;
}
else if (!identitySame)
{
    Console.WriteLine("   ! the two emits were made on different populations or in different world states — they answer different");
    Console.WriteLine("     questions, and the difference between them is that change, not a repair.");
    if (!compareStates) return 3;
}
if (compareStates && !identitySame) Console.WriteLine("   (--compare-states: running anyway; every number below is a POPULATION/STATE difference, not repair credit)");
// The CONFIGURATION gate (roadmap 4.20), the same posture one level down: the population gate asks which references the
// two runs judged, this asks which plugins, in what order, over which assets they read to judge them. A named difference
// is printed and the run proceeds — the delta IS what the differential is of. An emit that names no configuration, or a
// pair from different probes, is refused: a comparison that cannot state what it compares states nothing.
var gate = ConfigurationGate.Check(baseFile, changedFile);
foreach (var line in gate.Lines) Console.WriteLine($"   {line}");
if (gate.Refused) return 3;
Console.WriteLine();
if (basePredicated == 0) Console.WriteLine("   ! the base emit carries no physical predicates (written before 2026-09-03?) — every vanished row reads UNMEASURED; re-emit with the current probe");
if (predicates.Count == 0 && before.Keys.Any(k => !after.ContainsKey(k)))
    Console.WriteLine("   ! the changed emit carries no base-row predicates — was the probe run with --base <base.json>? Vanished rows read UNMEASURED");
Console.WriteLine();

// Credit every row of the union.
var outcome = new Dictionary<string, CreditOutcome>();
foreach (var id in before.Keys.Union(after.Keys))
{
    bool inB = before.TryGetValue(id, out var b), inA = after.TryGetValue(id, out var a);
    PhysicalState? changedPhys = inA ? a!.Physical : predicates.TryGetValue(id, out var p) ? p.Physical : null;
    outcome[id] = DetectorModel.Credit(inB, inB ? b!.Physical : null, inA, changedPhys);
}
int N(CreditOutcome o) => outcome.Values.Count(v => v == o);

// The group view (attribution), as before, each line with its credit breakdown.
static string Breakdown(IEnumerable<CreditOutcome> os)
{
    var l = os.ToList();
    var parts = new List<string>();
    foreach (var o in new[] { CreditOutcome.Repaired, CreditOutcome.Reclassified, CreditOutcome.Created, CreditOutcome.Unmeasured })
    { int n = l.Count(x => x == o); if (n > 0) parts.Add($"{o.ToString().ToLowerInvariant()} {n}"); }
    return string.Join(", ", parts);
}
var groupsBefore = before.Values.GroupBy(r => (r.Kind, r.A, r.B)).ToDictionary(g => g.Key, g => g.Select(r => r.Id).ToHashSet());
var groupsAfter = after.Values.GroupBy(r => (r.Kind, r.A, r.B)).ToDictionary(g => g.Key, g => g.Select(r => r.Id).ToHashSet());
foreach (var (k, ids) in groupsBefore.OrderBy(kv => kv.Key.Kind).ThenBy(kv => kv.Key.A))
{
    var now = groupsAfter.GetValueOrDefault(k);
    var gone = ids.Where(id => !after.ContainsKey(id)).ToList();
    var moved = ids.Where(id => after.ContainsKey(id) && (now is null || !now.Contains(id))).ToList();
    if (gone.Count == 0 && moved.Count == 0) continue;
    if (gone.Count > 0)
        Console.WriteLine($"   {(now is null || now.Count == 0 ? "GONE  " : "SHRANK"),-7} {k.Kind,-10} {k.A}  x  {k.B}   -{gone.Count} record(s): {Breakdown(gone.Select(id => outcome[id]))}");
    if (moved.Count > 0)
        Console.WriteLine($"   {"RE-ATTR",-7} {k.Kind,-10} {k.A}  x  {k.B}   {moved.Count} record(s) persist under another plugin pair ({string.Join("; ", moved.Select(id => $"{after[id].A} x {after[id].B}").Distinct())}) — unchanged");
}
foreach (var (k, ids) in groupsAfter.OrderBy(kv => kv.Key.Kind).ThenBy(kv => kv.Key.A))
{
    var was = groupsBefore.GetValueOrDefault(k);
    var fresh = ids.Where(id => !before.ContainsKey(id)).ToList();
    if (fresh.Count == 0) continue;
    Console.WriteLine($"   {(was is null || was.Count == 0 ? "NEW   " : "GREW  "),-7} {k.Kind,-10} {k.A}  x  {k.B}   +{fresh.Count} record(s): {Breakdown(fresh.Select(id => outcome[id]))}");
}

// The predicate flips on rows in both arms — reported, never credited.
var both = outcome.Where(kv => kv.Value == CreditOutcome.Unchanged).Select(kv => kv.Key).ToList();
int flipGone = both.Count(id => before[id].Physical == PhysicalState.Holds && after[id].Physical == PhysicalState.Gone);
int flipHolds = both.Count(id => before[id].Physical == PhysicalState.Gone && after[id].Physical == PhysicalState.Holds);

// Why the reclassified rows were reclassified.
var recl = outcome.Where(kv => kv.Value == CreditOutcome.Reclassified).Select(kv => kv.Key).ToList();
int leftHolding = recl.Count(id => before.ContainsKey(id) && before[id].Physical == PhysicalState.Holds);
int leftNone = recl.Count(id => before.ContainsKey(id) && before[id].Physical == PhysicalState.Gone);
int enteredNone = recl.Count(id => !before.ContainsKey(id));

if (outcome.Values.All(v => v == CreditOutcome.Unchanged)) Console.WriteLine("   IDENTICAL — the change touched nothing the detector sees.");

Console.WriteLine();
Console.WriteLine($"   repaired     : {N(CreditOutcome.Repaired):N0} record(s)   (the physical predicate went Holds -> Gone)");
Console.WriteLine($"   reclassified : {N(CreditOutcome.Reclassified):N0} record(s)   (left the class still holding {leftHolding:N0}; left with no physical defect to begin with {leftNone:N0}; entered reading Gone {enteredNone:N0})");
Console.WriteLine($"   created      : {N(CreditOutcome.Created):N0} record(s)   (entered the class with the predicate holding)");
Console.WriteLine($"   unmeasured   : {N(CreditOutcome.Unmeasured):N0} record(s)   (a predicate the credit rests on could not be measured — counted apart)");
Console.WriteLine($"   unchanged    : {N(CreditOutcome.Unchanged):N0} record(s)   (predicate flips beside: Holds -> Gone {flipGone:N0}, Gone -> Holds {flipHolds:N0})");

// Coverage — both blocks, then the refusal test.
static IEnumerable<(string, long, long)> Cov(ConflictFile f) => (f.Coverage ?? Array.Empty<CoverageLine>()).Select(c => (c.Population, c.Judged, c.UnjudgeableTotal));
static void PrintCoverage(string label, ConflictFile f)
{
    if (f.Coverage is not { Count: > 0 } cov) { Console.WriteLine($"   coverage ({label}): none carried"); return; }
    Console.WriteLine($"   coverage ({label}):");
    foreach (var c in cov)
    {
        string unj = c.Unjudgeable is { Count: > 0 } u ? string.Join(", ", u.Where(kv => kv.Value > 0).Select(kv => $"{kv.Key} {kv.Value:N0}")) : "";
        string aside = c.SetAside is { Count: > 0 } s ? string.Join(", ", s.Where(kv => kv.Value > 0).Select(kv => $"{kv.Key} {kv.Value:N0}")) : "";
        Console.WriteLine($"      {c.Population,-32} judged {c.Judged,10:N0}   unjudgeable {c.UnjudgeableTotal,8:N0} ({c.UnjudgeableFraction:P2}){(unj.Length > 0 ? $"  [{unj}]" : "")}{(aside.Length > 0 ? $"   set aside: {aside}" : "")}{(c.Note is { } n ? $"   {n}" : "")}");
    }
}
Console.WriteLine();
PrintCoverage("base", baseFile);
PrintCoverage("changed", changedFile);
var refusals = DetectorModel.CoverageRefusals(Cov(baseFile), Cov(changedFile), tolerance);
foreach (var r in refusals) Console.WriteLine($"   ! REFUSED: {r}");
if (baseFile.Coverage is null || changedFile.Coverage is null) Console.WriteLine("   ! one or both emits carry no coverage block — the unjudgeable-share test could not run");

// Identical configurations that produced different rows have no honest verdict (roadmap 4.20): nothing the identity covers
// changed, so whatever moved came from outside it. Said loud, and the verdict is UNJUDGED rather than a repair number.
bool anyRowDiffers = outcome.Values.Any(v => v != CreditOutcome.Unchanged) || flipGone > 0 || flipHolds > 0;
bool nondeterministic = ConfigurationGate.Nondeterministic(gate, anyRowDiffers);

string verdict = DetectorModel.DifferentialVerdict(N(CreditOutcome.Repaired), N(CreditOutcome.Created), N(CreditOutcome.Reclassified), N(CreditOutcome.Unmeasured), refusals.Count);
if (nondeterministic)
{
    Console.WriteLine();
    Console.WriteLine($"   {ConfigurationGate.NondeterminismLine}");
    verdict = "DIFFERENTIAL-UNJUDGED";
}
Console.WriteLine();
Console.WriteLine($"   verdict : {verdict}{(identitySame ? "" : "   [POPULATION/STATE DIFFERENCE — not repair credit]")}  (repaired {N(CreditOutcome.Repaired):N0} - created {N(CreditOutcome.Created):N0} = net {N(CreditOutcome.Repaired) - N(CreditOutcome.Created):+#;-#;0}; reclassified {N(CreditOutcome.Reclassified):N0}, unmeasured {N(CreditOutcome.Unmeasured):N0})");
Console.WriteLine();
Console.WriteLine("   A verdict here outranks --verify's CONFIRMED: coverage says a patch touches the broken records;");
Console.WriteLine("   only the differential says what happens to the conflict set — and only a row whose physical predicate");
Console.WriteLine("   went away is credited as repaired. A row that changed hands with the defect intact is reclassified.");
return 0;

// One row per necessary record: its identity (what the predicate is about), its attribution (the plugin pair) and
// its predicate under the emitting arm.
sealed record Row(string Id, string Kind, string A, string B, string FormKey, string? Other, PhysicalState? Physical, string? Detail);
