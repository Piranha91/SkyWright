using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using HarnessShared;
using HousecarlCore;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Skyrim;
using HousecarlCore.Progress;

// MILESTONE 5 — CROSS-PLUGIN OBJECT OVERLAP, differential. The geometry charter's own motivating
// example: two mods drop a wall and a tree into the same cubic metre, SSEEdit reports "no conflict",
// and the player finds out by walking into it.
//
// Why this survives the ruling-out that killed milestone 4: the absolute question ("does this
// object overlap something?") drowns in the authored norm — clutter interpenetrates constantly.
// The CROSS-PLUGIN question does not have that excuse: an object placed by mod A intersecting an
// object placed by mod B, where neither declares a dependency on the other, is a coincidence of two
// authors who never saw each other's work. The floor still exists (two mods CAN legitimately meet —
// a city overhaul and a lantern mod decorating the same street) and is MEASURED here, not assumed:
// the same-source overlap rate is the "authored deliberately" baseline every cross-plugin number is
// read against, split by interior/exterior.
//
// The original cut used enclosing-box depth/fraction gates. Those are historical diagnostics only.
// This cut asks whether the actual placed strict render surfaces intersect and who created that relation:
// for every cross-responsible actual-shape hit, with L the LATER of the two
// responsible plugins, rebuild the arm WITHOUT L's contribution (L's reference at its previous snap
// in the placement history, or absent) — no overlap there means L INTRODUCED it, overlap already
// there means L INHERITED it. And was the other reference's winning placement set by L's own masters
// (SEEN — an author's choice) or outside its closure (BLIND — it could not have been in the editor)?
// The class is Introduced + Blind after an actual-shape intersection. Surface relation is retained as
// evidence and does not become a severity score. Provenance remains DetectorModel.OverlapProvenance;
// _shared/OverlapIndex.cs, over PlacementHistory — the same history driftprobe and depprobe run on.
//
// "Responsible" = the plugin that last CHANGED a placement (position, cell, or enable/delete flag) —
// PlacementHistory's changed-only rule. An override that forwards a position unchanged takes no
// responsibility; one that changes only rotation or scale is not a step either, and is COUNTED.
//
// Boundary (the standing rules, applied):
//   - Geometry is complete decoded visible render vertices from the winning VFS NIF. Record OBND and stored NIF
//     spheres have zero influence; unavailable geometry = not judged, counted (rule 6). Editor markers are set aside.
//   - Declared dependency is the transitive closure (DetectorModel.MasterClosure); Seen IS that
//     relation read from the later side, and the run checks the two counts agree.
//   - MeshIntersection symmetrically distinguishes crossing, coplanar area overlap, boundary touch,
//     separation and validated solid containment. Bounds only reject provably separated soups.
//   - These relations are geometry evidence, not compatibility severity, intent or a game verdict.
//
// usage: overlapprobe [--list N] [--surface-tolerance U] [--max-triangle-pairs N] [--pair-seconds S]
//                     [--emit path] [--base emit.json] [--reverse-of baseline.json] [--dump path] [--json path] [--pair-ledger path]
//                     [--bos on|off] [--enable X.esp]* [--disable X.esp]* [--instance DIR] [--game DIR] [--profile NAME]
//                     [--responsible-pair A.esp B.esp]* [--historical-pairs crosswalk.json] [--geometry-manifest output.json]
//                     [--motion swept [--motion-delta U]]
//   --motion swept  a base whose render geometry refuses as unsupported_pose but whose motion the files settle (NifMotion:
//                   NIF sequences and controllers, behaviour-graph clips) is discovered by a swept proxy instead of being
//                   unmeasurable: the boxes of its sampled poses, each grown by the sample's certified delta (default 0.5 u,
//                   --motion-delta) — an outer bound, labelled "swept proxy" in the geometry reason. Pairs found this way
//                   are candidates for contactprobe --motion swept, which decides witness / never / cannot_tell. Default off.
//   POPULATION      (Q27, 2026-09-06) a candidate exists when its runtime enable state resolves ENABLED at load — the
//                   XESP parent chain through EnableState (the R1 rule) — not when the winning snap's own
//                   initially-disabled flag happens to be clear. The counterfactual arm the provenance rule rests on
//                   ("was the intersection there before the later plugin?") resolves its OWN chain too: each element's
//                   flag at its authoring snap and its XESP as last set outside that plugin's side. Before Q27 the
//                   winning arm read the flag and the counterfactual read the previous snap's flag, so neither saw a
//                   switch and Introduced-vs-Inherited could turn on the wrong one.
//   --state SPEC    another world state (roadmap 4.11): <switch>[,<switch>…] by editor id or FormKey, stage:<quest>:<n>
//                   (with --switch-map, switchprobe's --json), or baseline — baseline IS the default, named explicitly;
//                   a flip resolves the same chain with those switches inverted (the state ROWS are stateprobe's
//                   business; this arm is for judging one whole state).
//   --flags         the flag-only population, what every run before 2026-09-06 judged. Kept so those numbers can be
//                   reproduced. Not with --state.
//   --bos on|off    apply the Base Object Swapper layer (*_SWAP.ini) so every ref is judged by the base the game loads
//                   (default on; _shared/BosHook.cs); off = the record bases

// Defaults are the configured instance; --instance / --game / --profile point every path at another
// MO2 layout. Nothing is compiled in — see _shared/HarnessTarget.cs. Flags win over the environment
// and over harness.local.json; Require() runs AFTER them.
if (args.Contains("--help") || args.Contains("-h"))
{
    Console.WriteLine("""
overlapprobe: bounded strict render-surface scene capture
  --scene-id ID --scene-role ROLE --geometry-manifest OUTPUT
  --scene-preflight --order CANDIDATE.json [--json VERDICT.json]
  --candidate-plugin PLUGIN (repeatable) --candidate-mod FOLDER (repeatable)
  --scene-scope-from MANIFEST (repeatable) --scene-cell FORMKEY (repeatable)
  --contact-evidence-pairs INPUT --surface-tolerance N --max-triangle-pairs N --pair-seconds N
  --instance DIR --game DIR --profile NAME --order CANDIDATE.json
  --self-check-imported-accounting FIXTURE.json (no load order; prints the accounting receipt)
  --self-check-declared-cells FIXTURE.json (no load order; prints each declared cell's presence outcome)
Scene candidates require explicit Mods asset priority; preflight checks MO2 names and relative priority.
An exterior persistent cell cannot define a bounded scope. Unknown geometry remains explicit.
--scene-scope-from accepts any scope declaring a bound: a non-empty scene.discovery.seedReferences[]
or a non-empty scene.discovery.cells[]. An imported declared cell joins this capture's --scene-cell
set, so a bounded cell scope and its pair/reference identities carry across configurations.
An imported cell whose owning plugin this setup neither loads nor masters does not exist here: it
bounds nothing, is named in scene.discovery.absentDeclaredCells and is not refused. A typed
--scene-cell with such an owner is refused.
Every imported reference identity -- every records[] formid AND both endpoints of every pairs[]
row -- then receives exactly one disposition in scene.discovery.importedReferenceAccounting:
emitted (a record row enumerates it), no_placement_history (missingImportedReferences, unchanged,
and now derived from this partition), no_winning_pose (placement history but no resolved winning
pose) or out_of_scope. Every input is about PLACEMENT: no_placement_history does NOT mean the order
declares no record, because a placement-less record (a deletion typically strips placement) never
enters the placement history. A disposition states what the manifest resolved about a placement; it
never asserts a reference is present, absent or separated in the game state, nor that a record for
it does or does not exist -- those are read from an enumerated record row's own fields.
Legacy mode: --responsible-pair PLUGIN_A PLUGIN_B with --geometry-manifest OUTPUT. --scene-cell does not apply in this mode:
the manifest reads every reference of the responsible pairs, whatever its cell (a note on stderr says so when it is given).
--motion swept [--motion-delta U]: a base refused for its pose alone (unsupported_pose, or partial_geometry when every
visible shape decoded and only the pose is refused) whose motion the files settle is discovered
by a swept proxy (its sampled poses' boxes, each grown by its certified delta): an outer bound, labelled in the reason.
--reach: every enabled record without a render box gains "reach": a world box guaranteed to hold its geometry (stored
pose, swept region, or a switch node's every child), "uncertified" with labels and per-part boxes where a wind-swayed
shape or the particle model enters, "refused" with the reason otherwise (a model-less primitive refuses naming its
collision layer until the layer's solidity is settled), "no_geometry" for a reference with neither model nor primitive.
Default off: the manifest is unchanged without it.
--reach-rules physics,graph,switch,book,skin,particles,primitive,rest (with --reach): the modelling rules and readers that turn reach refusals into
labelled regions; never certified. physics: a body the file pins (a constraint to a massless body) swings within the
constraint's limits; a free body (mass > 0, simulated motion system, no pin) is dropped straight down, rigidly, from its
placed pose to first contact with a support below (the modelling rule) - a support is a solid, non-movable body of a
reference enabled in the state, on a layer the falling body's COLL record collides with, not a trigger volume or sensor;
a Static base is held (labelled: measured on one model in the lab). One load-settle rule decides the drop: an object drops
only if the engine settles it when the cell loads, otherwise it rests at its placed pose until disturbed; the exemptions
are data (--settle-exemptions FILE, default load-settle-exemptions.json beside the exe: a REFR record-flag mask, a WEAP
animation type, the model's BSX bits and behaviour graph, or a reference a judged state enables after load, which holds the placed and dropped poses both). graph: binding member paths decide (playbackSpeed: no effect), the
reference-pose generator, a missing sequence where the file has no transform source. switch: every switch child, animated
children swept, Tree_Anim shapes labelled. book: a BOOK base, or a Static a BOOK names as InventoryArt, is read closed (its
stored pose: the modelling rule). skin: weight sources that disagree only on vertices no
triangle draws do not refuse; drawn disagreements are posed under both sources (labelled, the renderer's source untested);
a drawn vertex with no weight under one source still refuses. particles: a particle emitter or modifier-active controller
does not make the emitter move (only transform blocks do). primitive: a box on a model-less activator with no collision layer
stated is not solid (no_geometry, measured in the lab); stated layers and other bases keep the refusal. rest (needs physics):
a settling body with no support below is an outcome, not a refusal - drop "lost" (no_geometry; enabled after load, the placed
box with both readings), measured in the lab: the engine keeps such a body falling and returns it to its placed height every
few seconds; an exempt body records the drop it would take as "rest-gap" (distance, support; not applied) or "floats" over
nothing; a landed body's region is grown by its tilt margin (drop.tiltMargin); a render-boxed MoveableStatic with a free body
carries its drop too (measured: they settle at load); carryable Lights join reachLoose. Reach rows gain "drop" {status, distance, placed, landed, supportId,
stateExcludedCandidates}; the manifest gains "reachLoose" (the same region for each item-type reference: report only,
dispositions unchanged).
--pose-confirmations FILE (with --reach-rules): a JSON list of {modelPath, sha256, evidence}; a model whose animation the
files cannot settle, whose path and exact bytes match an entry (an in-game screenshot compared with a render of its stored
pose), is read at its stored pose, labelled "rest: stored pose (confirmed by in-game comparison)". An entry may carry "locals"
({node: {T, R (9, rows), S}}, a confirmed non-stored pose such as a graph start state), "label", and "pose": {"clip": "<clip path>", "sweep": "loop" |
[t0, t1], "label"} (a confirmed clip read alone from the model's behaviour graph and swept on those bytes; a clip the graph lacks
refuses, naming it). No entry, no change.
Part 3b keys (with --reach): clipnames (a behaviour clip resolves through its character's animation list by file name),
transfer (a refused graph model whose graph files are byte-identical to a --pose-confirmations model's and whose stored locals
on every graph-driven node equal that model's takes its confirmation), blend (state transitions and hkbBlenderGenerator children
bounded by the blend envelope: the product of per-node 1-DOF intervals, else the chord bound), missingseq (a sequence generator
naming a sequence absent from the NIF reads the stored pose and every sequence in the file), path (a NiPathInterpolator node within
its path's control-polygon hull grown by its subtree radius), emitter (a moving particle emitter: swept origins grown by the particle
offset box), gravity (--settled-gravity FILE: observed gravity frame and sign per model; with a scene, the support-plane term
under its F1 condition), approx (--measured-approximations FILE: an in-game measured box per model, a labelled part beside the file
bound), absent (--absent-models FILE: a model no
active provider supplies, measured drawing nothing, is no_geometry). primitive with --primitive-solidity FILE: the measured
(base, layer) table decides; without the file the earlier activator rule stands.
See overlapprobe/Program.cs usage documentation. Help performs no load-order scan.
""");
    return 0;
}
using var __run = RunLog.BeginProcess("overlapprobe", args);
// Known answers for the by-name population split over a synthetic order (_shared/BaseGameFixture.cs).
if (args.Length >= 1 && args[0] == "--self-test") return OverlapSelfTest.Run();

string Instance = HarnessTarget.Instance;
string GamePath = HarnessTarget.Game;
string profileName = HarnessTarget.Profile;
int listN = 25;
string? stateSpec = null, switchMapPath = null;
// --force-enable: initially-disabled singletons read as enabled after load (EnableArms.Force); refused by name otherwise.
var forceEnable = new List<string>();
// --state: the runtime enable state per reference under the arm (null = the winning flag, the default population).
Func<FormKey, bool>? enabledOf = null;
StateArm? stateArm = null;
double surfaceTolerance = 1e-5;
bool motionSwept = false; double motionDelta = 0.5;
bool reachOn = false;   // --reach (G1 build list, Stage A): a reach region for every record without a render box
HashSet<string>? reachRules = null;   // --reach-rules physics,graph,switch,book (unbounded neighbour classes)
string? poseConfirmationsPath = null;   // --pose-confirmations FILE (with --reach-rules)
string? settleExemptionsPath = null;     // --settle-exemptions FILE (with --reach-rules physics; default: the shipped list beside the exe)
string? primitiveSolidityPath = null;    // --primitive-solidity FILE (with --reach-rules primitive; Part 3b measured table)
string? absentModelsPath = null;         // --absent-models FILE (with --reach-rules absent; Part 3b measurements)
string? settledGravityPath = null;       // --settled-gravity FILE (with --reach-rules gravity; Part 3b observations)
string? approximationsPath = null;       // --measured-approximations FILE (with --reach-rules approx; Part 3b round 2 measurements)
long maxTrianglePairs = 2_000_000;
double pairSeconds = 120;
string? emitPath = null;
// --base: the emit this run will be diffed against — every record of it is re-measured under THIS order and carried in
// the emit as Predicates (the changed arm of a differential; conflictdiff reads both). See ConflictReport.cs.
string? basePath = null;
// --dump: one line per candidate (key, responsible plugin, base type, mesh, geometry source, rotation, scale,
// world box). A diagnostic for comparing two runs ref by ref — which boxes moved under a transform change,
// and what meshes they were — not part of the report. --json: every judged row, for spot-checks.
string? dumpPath = null;
string? jsonPath = null;
// --pair-ledger FILE: one JSON line per pair the actual-shape sweep measured, in sweep order (both FormKeys, same/cross, the
// PairFrame path, every result field, and the pair's seconds), plus FILE.slowest.json; for comparing two builds pair by pair.
string? pairLedgerPath = null;
// --reverse-of BASELINE_EMIT (with --base): a reverse run that takes this order's baseline emit's rows, coverage and class
// instead of sweeping again, and re-measures only the --base rows (ReverseFromBaseline.cs); refused unless the baseline emit
// is this run's own (configuration, population, state, policy, instance and the stage-independent coverage).
string? reverseOfPath = null;
string? geometryManifestPath = null;
string? historicalPairsPath = null;
string? contactEvidencePairsPath = null;
var responsiblePairNames = new List<(string A, string B)>();
string? sceneId = null, sceneRole = null;
var candidatePluginNames = new List<string>();
var candidateModNames = new List<string>();
var sceneScopeFromPaths = new List<string>();
var explicitSceneCells = new List<string>();
bool bosOn = true;
bool flagsOnly = false;
bool scenePreflight = false;
// A deterministic exercise of the imported-reference accounting partition, needing no load order:
// it takes a fixture naming the identities and their history/pose/row state and prints the receipt
// the real capture would emit. Handled before any target work, so a test can drive it anywhere.
string? accountingFixture = null;
// The same for the declared-cell presence rule (DeclaredCellPresence.cs).
string? declaredCellsFixture = null;
var enablePlugins = new List<string>();
var disablePlugins = new List<string>();
string? orderFile = null;
// --population FILE (P25): the plan's population (_shared/PopulationScope.cs); --population-audit EMIT OUT writes, for every
// row of EMIT, whether it is in that population (the equivalence check reads it).
string? populationPath = null, populationAuditEmit = null, populationAuditOut = null;
string NeedValue(int index, string flag)
{
    if (index + 1 >= args.Length || args[index + 1].StartsWith("--", StringComparison.Ordinal))
        throw new ArgumentException($"{flag} requires a value");
    return args[index + 1];
}
try
{
    for (int i = 0; i < args.Length; i++)
    {
        string flag = args[i];
        if (flag is "--depth" or "--frac" or "--maxdim")
            throw new ArgumentException("--depth, --frac and --maxdim are unsupported: actual-shape discovery has no box depth, fraction or size decision gates");
        if (flag == "--list") { if (!int.TryParse(NeedValue(i, flag), out listN)) throw new ArgumentException("--list requires an integer"); i++; }
        else if (flag == "--motion") { if (NeedValue(i, flag) != "swept") throw new ArgumentException("--motion takes swept"); motionSwept = true; i++; }
        else if (flag == "--reach") reachOn = true;
        else if (flag == "--pose-confirmations") { poseConfirmationsPath = NeedValue(i, flag); i++; }
        else if (flag == "--settle-exemptions") { settleExemptionsPath = NeedValue(i, flag); i++; }
        else if (flag == "--primitive-solidity") { primitiveSolidityPath = NeedValue(i, flag); i++; }
        else if (flag == "--absent-models") { absentModelsPath = NeedValue(i, flag); i++; }
        else if (flag == "--settled-gravity") { settledGravityPath = NeedValue(i, flag); i++; }
        else if (flag == "--measured-approximations") { approximationsPath = NeedValue(i, flag); i++; }
        else if (flag == "--reach-rules")
        {
            reachRules = NeedValue(i, flag).Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToHashSet(StringComparer.OrdinalIgnoreCase);
            if (reachRules.Count == 0 || reachRules.Any(x => x is not ("physics" or "graph" or "switch" or "book" or "skin" or "particles" or "primitive" or "rest"
                                                                      or "clipnames" or "transfer" or "blend" or "missingseq" or "path" or "emitter" or "gravity" or "absent" or "approx")))
                throw new ArgumentException("--reach-rules takes physics,graph,switch,book,skin,particles,primitive,rest,clipnames,transfer,blend,missingseq,path,emitter,gravity,absent,approx");
            if (reachRules.Contains("rest") && !reachRules.Contains("physics"))
                throw new ArgumentException("--reach-rules rest needs physics (it reads the drop and the load-settle rule)");
            i++;
        }
        else if (flag == "--motion-delta") { if (!double.TryParse(NeedValue(i, flag), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out motionDelta) || !(motionDelta > 0)) throw new ArgumentException("--motion-delta requires a positive number"); i++; }
        else if (flag == "--surface-tolerance") { if (!double.TryParse(NeedValue(i, flag), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out surfaceTolerance)) throw new ArgumentException("--surface-tolerance requires a number"); i++; }
        else if (flag == "--max-triangle-pairs") { if (!long.TryParse(NeedValue(i, flag), out maxTrianglePairs)) throw new ArgumentException("--max-triangle-pairs requires an integer"); i++; }
        else if (flag == "--pair-seconds") { if (!double.TryParse(NeedValue(i, flag), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out pairSeconds)) throw new ArgumentException("--pair-seconds requires a number"); i++; }
        else if (flag == "--emit") { emitPath = NeedValue(i, flag); i++; }
        else if (flag == "--base") { basePath = NeedValue(i, flag); i++; }
        else if (flag == "--dump") { dumpPath = NeedValue(i, flag); i++; }
        else if (flag == "--json") { jsonPath = NeedValue(i, flag); i++; }
        else if (flag == "--pair-ledger") { pairLedgerPath = NeedValue(i, flag); i++; }
        else if (flag == "--reverse-of") { reverseOfPath = NeedValue(i, flag); i++; }
        else if (flag == "--geometry-manifest") { geometryManifestPath = NeedValue(i, flag); i++; }
        else if (flag == "--historical-pairs") { historicalPairsPath = NeedValue(i, flag); i++; }
        else if (flag == "--contact-evidence-pairs") { contactEvidencePairsPath = NeedValue(i, flag); i++; }
        else if (flag == "--responsible-pair")
        {
            string a = NeedValue(i, flag);
            if (i + 2 >= args.Length || args[i + 2].StartsWith("--", StringComparison.Ordinal)) throw new ArgumentException("--responsible-pair requires two plugin names");
            responsiblePairNames.Add((a, args[i + 2])); i += 2;
        }
        else if (flag == "--scene-id") { sceneId = NeedValue(i, flag); i++; }
        else if (flag == "--scene-role") { sceneRole = NeedValue(i, flag); i++; }
        else if (flag == "--candidate-plugin") { candidatePluginNames.Add(NeedValue(i, flag)); i++; }
        else if (flag == "--candidate-mod") { candidateModNames.Add(NeedValue(i, flag)); i++; }
        else if (flag == "--scene-scope-from") { sceneScopeFromPaths.Add(NeedValue(i, flag)); i++; }
        else if (flag == "--scene-cell") { explicitSceneCells.Add(NeedValue(i, flag)); i++; }
        else if (flag == "--scene-space") throw new ArgumentException("--scene-space is refused because an exterior worldspace is unbounded; use bounded --scene-cell FormKey values");
        else if (flag == "--scene-preflight") scenePreflight = true;
        else if (flag == "--self-check-imported-accounting") { accountingFixture = NeedValue(i, flag); i++; }
        else if (flag == "--self-check-declared-cells") { declaredCellsFixture = NeedValue(i, flag); i++; }
        else if (flag == "--order") { orderFile = NeedValue(i, flag); i++; }
        else if (flag == "--population") { populationPath = NeedValue(i, flag); i++; }
        else if (flag == "--population-audit")
        {
            populationAuditEmit = NeedValue(i, flag);
            if (i + 2 >= args.Length || args[i + 2].StartsWith("--", StringComparison.Ordinal)) throw new ArgumentException("--population-audit requires EMIT and OUT");
            populationAuditOut = args[i + 2]; i += 2;
        }
        else if (flag == "--enable") { enablePlugins.Add(NeedValue(i, flag)); i++; }
        else if (flag == "--disable") { disablePlugins.Add(NeedValue(i, flag)); i++; }
        else if (flag == "--bos") { bosOn = BosHook.ParseFlag(NeedValue(i, flag)) ?? throw new ArgumentException("--bos takes on|off"); i++; }
        else if (flag == "--state") { stateSpec = NeedValue(i, flag); i++; }
        else if (flag == "--force-enable") { forceEnable.AddRange(NeedValue(i, flag).Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)); i++; }
        else if (flag == "--switch-map") { switchMapPath = NeedValue(i, flag); i++; }
        else if (flag == "--flags") flagsOnly = true;
        else if (flag == "--instance") { Instance = NeedValue(i, flag); i++; }
        else if (flag == "--game") { GamePath = NeedValue(i, flag); i++; }
        else if (flag == "--profile") { profileName = NeedValue(i, flag); i++; }
        else throw new ArgumentException($"unknown argument: {flag}");
    }
}
catch (ArgumentException ex) { Console.Error.WriteLine(ex.Message); return 2; }

if (accountingFixture is not null) return ImportedReferenceAccounting.SelfCheck(accountingFixture);
if (declaredCellsFixture is not null) return DeclaredCellPresence.SelfCheck(declaredCellsFixture);

bool sceneCapture = sceneId is not null;
if (sceneCapture && orderFile is null)
{
    Console.Error.WriteLine("scene capture requires --order CANDIDATE with an explicit Mods list; bare-profile and --enable/--disable source scenes are refused before loading the order");
    return 2;
}
if (scenePreflight && orderFile is null) { Console.Error.WriteLine("--scene-preflight requires --order CANDIDATE"); return 2; }
if (scenePreflight && sceneCapture) { Console.Error.WriteLine("--scene-preflight validates only the setup; omit --scene-id and capture in a separate invocation"); return 2; }
if (geometryManifestPath is not null && responsiblePairNames.Count == 0 && !sceneCapture)
{
    Console.Error.WriteLine("--geometry-manifest requires at least one --responsible-pair <plugin-a> <plugin-b>; an unscoped whole-order geometry catalogue is refused");
    return 2;
}
if (sceneCapture && geometryManifestPath is null) { Console.Error.WriteLine("--scene-id requires --geometry-manifest OUTPUT"); return 2; }
// --scene-cell bounds a scene capture only; without --scene-id it changes nothing, and the run says so (2026-10-13; whether it
// should narrow a --responsible-pair manifest is an open product choice).
if (!sceneCapture && explicitSceneCells.Count > 0)
    Console.Error.WriteLine($"note: --scene-cell is applied only in scene capture (--scene-id); without it the {explicitSceneCells.Count} cell(s) given are ignored and every reference of the responsible pairs is read, whatever its cell");
if (sceneCapture && candidatePluginNames.Count == 0 && candidateModNames.Count == 0 && sceneScopeFromPaths.Count == 0 && explicitSceneCells.Count == 0)
{
    Console.Error.WriteLine("scene capture requires at least one --candidate-plugin, --candidate-mod, --scene-scope-from or --scene-cell; an implicit whole-order scene is refused");
    return 2;
}
if (!double.IsFinite(surfaceTolerance) || surfaceTolerance < 0 || maxTrianglePairs <= 0 || !double.IsFinite(pairSeconds) || pairSeconds <= 0)
{
    Console.Error.WriteLine("surface tolerance must be finite and nonnegative; max triangle pairs and pair seconds must be finite positive values");
    return 3;
}
if (populationPath is not null && (sceneCapture || scenePreflight || responsiblePairNames.Count > 0 || geometryManifestPath is not null))
{
    Console.Error.WriteLine("--population scopes the emit pass; it does not combine with scene capture, --scene-preflight, --responsible-pair or --geometry-manifest");
    return 2;
}
if (populationAuditEmit is not null && populationPath is null) { Console.Error.WriteLine("--population-audit needs --population"); return 2; }
if (populationAuditEmit is not null && !File.Exists(populationAuditEmit)) { Console.Error.WriteLine($"--population-audit input does not exist: {populationAuditEmit}"); return 2; }
PopulationScope? population = null;
if (populationPath is not null)
{
    try { population = PopulationScope.Load(populationPath); }
    catch (PopulationRefused ex) { Console.Error.WriteLine(ex.Message); return 2; }
}
PopulationMode popMode;
try { popMode = PopulationCli.Resolve("overlapprobe", flagsOnly, stateSpec); }
catch (ArgumentException ex) { Console.Error.WriteLine(ex.Message); return 2; }

Instance = HarnessTarget.Require(Instance, "MO2 instance directory", "instance", "--instance");
GamePath = HarnessTarget.Require(GamePath, "game directory", "game", "--game");
profileName = HarnessTarget.Require(profileName, "MO2 profile name", "profile", "--profile");
string profileDir = Path.Combine(Instance, "profiles", profileName);
string modsDir = Path.Combine(Instance, "mods");
string overwriteDir = Path.Combine(Instance, "overwrite");
string dataDir = Path.Combine(GamePath, "Data");
SceneCapture.AssetPreflight? assetPreflight = null;
if (sceneCapture || scenePreflight)
{
    try { assetPreflight = SceneCapture.PreflightCandidateAssets(orderFile!, profileName, profileDir, modsDir); }
    catch (Exception ex)
    {
        var refusal = ex as SceneCapture.PreflightRefusal;
        string remedy = refusal?.Remedy ?? "read the reason above, correct the candidate or the profile, then try again";
        Console.Error.WriteLine($"scene asset preflight refused: {ex.Message}. To fix: {remedy}.");
        if (scenePreflight && jsonPath is not null)
            WritePreflightReceipt(jsonPath, new { schemaVersion = 1, verdict = "refused", code = refusal?.Code ?? "preflight-failed",
                reason = ex.Message, remedy, candidate = Path.GetFullPath(orderFile!), profile = profileName,
                providers = Array.Empty<object>() });
        return 2;
    }
    if (scenePreflight && jsonPath is not null)
        WritePreflightReceipt(jsonPath, new { schemaVersion = 1, verdict = "passed", code = (string?)null, reason = (string?)null,
            remedy = (string?)null, candidate = assetPreflight.CandidatePath, profile = assetPreflight.BaseProfile,
            profileProviderCount = assetPreflight.ProfileProviderCount, omittedProviderCount = assetPreflight.OmittedProviderCount,
            reorderedProviders = assetPreflight.ReorderedProviders,
            providers = assetPreflight.Providers.Select(p => new { candidateName = p.CandidateName, mo2Name = p.Mo2Name,
                candidateRank = p.CandidateRank, mo2Priority = p.Mo2Priority, enabledInBaseProfile = p.Enabled }).ToArray() });
    Console.WriteLine($"scene asset preflight: {assetPreflight.Providers.Count} selected provider(s), all match MO2 names; subset of {assetPreflight.ProfileProviderCount}, {assetPreflight.OmittedProviderCount} deliberately omitted by the candidate view");
    if (assetPreflight.ReorderedProviders.Count > 0)
        Console.WriteLine($"   the candidate states its own order for {assetPreflight.ReorderedProviders.Count} provider(s) ({string.Join(", ", assetPreflight.ReorderedProviders)}): the asset view follows the candidate's Mods order, not profile '{assetPreflight.BaseProfile}''s modlist.txt; to see it in game, give an MO2 profile that order");
    foreach (var provider in assetPreflight.Providers)
        Console.WriteLine($"   rank {provider.CandidateRank} (MO2 row {provider.Mo2Priority}): {provider.Mo2Name} ({(provider.Enabled ? "enabled" : "disabled in base profile; explicitly selected")})");
    if (scenePreflight) return 0;
}

var explicitCellKeys = new HashSet<FormKey>();
var cellBoundOrigin = new Dictionary<FormKey, string>();
foreach (var textCell in explicitSceneCells)
{
    FormKey cellKey;
    try { cellKey = FormKey.Factory(textCell); }
    catch (Exception ex) { Console.Error.WriteLine($"--scene-cell '{textCell}' is not a FormKey: {ex.Message}"); return 2; }
    if (explicitCellKeys.Add(cellKey)) cellBoundOrigin[cellKey] = "--scene-cell";
}
var importedSceneScopes = new List<SceneCapture.ImportedScope>();
foreach (var scopePath in sceneScopeFromPaths)
{
    try { importedSceneScopes.Add(SceneCapture.ReadScope(scopePath)); }
    catch (Exception ex) { Console.Error.WriteLine($"could not read --scene-scope-from '{scopePath}': {ex.Message}"); return 2; }
}
// An imported scope's declared cells are the bound its capture was taken under. Feeding them into
// the same explicit-cell set is what carries a bounded scope across configurations: the cells are
// interior-only by construction on the producing side and are re-validated as interior here, so
// the bound travels without ever widening to a worldspace.
var importedDeclaredCells = new HashSet<FormKey>();
foreach (var scope in importedSceneScopes)
    foreach (var cellKey in scope.DeclaredCells)
    {
        importedDeclaredCells.Add(cellKey);
        if (explicitCellKeys.Add(cellKey)) cellBoundOrigin[cellKey] = $"--scene-scope-from '{scope.Path}'";
    }
IReadOnlySet<string> contactEvidenceKeys;
string? contactEvidencePairsSha256 = null;
try
{
    contactEvidenceKeys = contactEvidencePairsPath is null ? new HashSet<string>() : SceneCapture.ReadPairKeys(contactEvidencePairsPath);
    if (contactEvidencePairsPath is not null) contactEvidencePairsSha256 = ConfigurationIdentity.Sha256File(contactEvidencePairsPath);
}
catch (Exception ex) { Console.Error.WriteLine($"could not read --contact-evidence-pairs: {ex.Message}"); return 2; }
HistoricalCrosswalk? historicalCrosswalk = null;
string? historicalCrosswalkSha256 = null;
if (historicalPairsPath is not null)
{
    byte[] crosswalkBytes;
    try { crosswalkBytes = File.ReadAllBytes(historicalPairsPath); }
    catch (Exception ex) { Console.Error.WriteLine($"could not read --historical-pairs: {ex.Message}"); return 2; }
    try
    {
        historicalCrosswalk = JsonSerializer.Deserialize<HistoricalCrosswalk>(crosswalkBytes,
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
            ?? throw new InvalidDataException("the file decoded as null");
    }
    catch (Exception ex) { Console.Error.WriteLine($"could not parse --historical-pairs: {ex.Message}"); return 2; }
    if (!historicalCrosswalk.Complete || historicalCrosswalk.Pairs is null)
    { Console.Error.WriteLine("--historical-pairs must be a complete crosswalk with pairs[]"); return 2; }
    historicalCrosswalkSha256 = Convert.ToHexString(SHA256.HashData(crosswalkBytes)).ToLowerInvariant();
}
if (reverseOfPath is not null)
{
    string? clash = basePath is null ? "--reverse-of needs --base (the candidate emit whose rows the reverse re-measures)"
        : emitPath is null ? "--reverse-of needs --emit"
        : jsonPath is not null || dumpPath is not null || pairLedgerPath is not null ? "--reverse-of writes no judged rows, dump or pair ledger (it runs no sweep); drop --json, --dump and --pair-ledger"
        : sceneCapture || geometryManifestPath is not null || responsiblePairNames.Count > 0 || populationAuditEmit is not null
            ? "--reverse-of combines only with a plain emit run; drop scene capture, --geometry-manifest, --responsible-pair and --population-audit"
        : null;
    if (clash is not null) { Console.Error.WriteLine(clash); return 2; }
}
foreach (var input in new[] { (Flag: "--base", Path: basePath), (Flag: "--switch-map", Path: switchMapPath), (Flag: "--reverse-of", Path: reverseOfPath) })
    if (input.Path is not null && !File.Exists(input.Path))
    { Console.Error.WriteLine($"{input.Flag} input does not exist: {input.Path}"); return 2; }

var sw = Stopwatch.StartNew();
// WHAT THIS RUN EVALUATES, decided in one place (_shared/HarnessOrderCli.cs): the real profile, a --enable/--disable
// what-if, or a --order candidate file - and, in the same step, the ASSET VIEW that configuration implies, so a run
// whose plugins changed no longer reads the profile's meshes and BSAs regardless. The identity it produces is written
// into the emit, so a reader (and conflictdiff) can see exactly what was consumed instead of inferring it.
var cfg = HarnessOrderCli.Resolve(profileName, profileDir, modsDir, dataDir, overwriteDir, GamePath,
                                  orderFile, enablePlugins, disablePlugins);
if (cfg.Refused) { Console.Error.WriteLine(cfg.RefusedReason); return 2; }
var paths = cfg.OrderedPaths;
using var assets = AssetResolver.Build(overwriteDir, modsDir, dataDir, cfg.EnabledMods, cfg.Archives.Archives);
Console.WriteLine($"load order: {paths.Count} plugins\n");
HarnessOrderCli.Print(cfg);

var Obstructing = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
{ "Static", "MoveableStatic", "Tree", "Furniture", "Door", "Container", "Activator", "Flora" };

// ---------------------------------------------------------------------------------------
// Pass 1 — every plugin once, into the shared index: placements into the history, base geometry keys.
// ---------------------------------------------------------------------------------------
// --bos on (default): the Base Object Swapper reader replaces each reference's record base with the base the
// game loads, and a moving override becomes a placement step under a pseudo-plugin after the last real one.
// --bos off: the record bases, as before 2026-09-04. The summary says how much the pass's input moved.
var idx = new OverlapIndex { Bos = bosOn ? new BosFormIndex() : null };
var pluginKey = new ModKey[paths.Count];
var masterIdx = new HashSet<int>[paths.Count + (bosOn ? 1 : 0)];
var lightness = new List<(ModKey, bool)>();
var unreadablePlugins = new List<string>();
__run.Feature("plugins", paths.Count, "plugins").Stage("scan", "reading plugins", paths.Count, "plugins");

for (int p = 0; p < paths.Count; p++)
{
    __run.Advance();
    ISkyrimModGetter mod;
    try { mod = SkyrimMod.CreateFromBinaryOverlay(paths[p], SkyrimRelease.SkyrimSE); }
    catch { unreadablePlugins.Add(Path.GetFileName(paths[p])); pluginKey[p] = ModKey.Null; masterIdx[p] = new HashSet<int>(); lightness.Add((ModKey.Null, false)); continue; }
    using (mod as IDisposable)
    {
        pluginKey[p] = mod.ModKey;
        lightness.Add((mod.ModKey, BosHook.IsLight(mod)));
        var mset = new HashSet<int>();
        try
        {
            var names = mod.ModHeader.MasterReferences.Select(m => m.Master).ToHashSet();
            for (int q = 0; q < p; q++) if (names.Contains(pluginKey[q])) mset.Add(q);
        }
        catch { }
        masterIdx[p] = mset;
        if (reachRules?.Contains("physics") == true) idx.TrackLand = true;
        idx.Ingest(p, mod);
    }
    if ((p + 1) % 150 == 0) Console.WriteLine($"   ... {p + 1}/{paths.Count} plugins, {sw.Elapsed.TotalSeconds:0}s");
}
int bosPlugin = bosOn ? paths.Count : -1;
if (bosOn) masterIdx[bosPlugin] = BosHook.PseudoMasters(paths.Count);
var closure = DetectorModel.MasterClosure(masterIdx);
Console.WriteLine($"\nindexed {idx.History.Count:N0} winning refs ({idx.Placements:N0} placements)  ({sw.Elapsed.TotalSeconds:0.0}s)");
Console.WriteLine($"   plugins that could not be read (contribute nothing, counted per rule 6): {unreadablePlugins.Count:N0}"
                + (unreadablePlugins.Count > 0 ? "  — " + string.Join(", ", unreadablePlugins) : ""));
Console.WriteLine($"   overrides that changed only rotation or scale (not a step in the history; the plugin is not responsible): {idx.SilentRePlacements:N0}");
BosLayer? activeBosLayer = null;
if (bosOn)
{
    idx.Bos!.SetLoadOrder(lightness);
    activeBosLayer = BosHook.Load(assets.LooseRoots, idx.Bos);
    var bosRep = BosHook.Apply(idx, activeBosLayer, bosPlugin);
    Console.WriteLine($"   BOS: {activeBosLayer.Inis.Count} *_SWAP.ini read ({activeBosLayer.LinesRead:N0} lines, {activeBosLayer.LinesFailed:N0} the game would refuse, {activeBosLayer.Anomalies.Count:N0} anomalies — bosprobe lists them)");
    foreach (var l in bosRep.Summary()) Console.WriteLine("   " + l);
}
else Console.WriteLine("   BOS: off — record bases (--bos on to apply the *_SWAP.ini layer)");
if (population is not null)
{
    population.Bind(idx);
    Console.WriteLine("   " + population.Describe());
}
// The overlap population rule: a pair is measured when either reference is in the plan's population.
bool PairInPopulation(SurfaceOverlapCandidate a, SurfaceOverlapCandidate b)
    => population is null || population.KeyInScope(a.Key, a.Win.Cell) || population.KeyInScope(b.Key, b.Win.Cell);

string PluginName(int p) => p == bosPlugin ? BosApplyReport.PseudoPluginName : pluginKey[p].FileName.String;
bool IsVanilla(int p) => BaseGamePlugins.IsBaseGame(PluginName(p));   // _shared/BaseGamePlugins.cs
bool IsUssep(int p) => BaseGamePlugins.IsUssep(PluginName(p));
bool IsBaseGame(int p) => BaseGamePlugins.IsCoreMaster(PluginName(p));   // ignoring case, as the file name on disk may differ
string CellOf(FormKey c) => idx.CellName.TryGetValue(c, out var v) ? (v.Edid ?? c.ToString()) + (v.Interior ? " (int)" : " (ext)") : c.ToString();
string TypeOf(FormKey baseKey) => idx.BaseInfo.TryGetValue(baseKey, out var bi) ? bi.Type : "?";
string MeshOf(FormKey baseKey) => idx.BaseMesh.TryGetValue(baseKey, out var m) ? m : "";
static string Short(string s, int n) => s.Length <= n ? s : "..." + s[^(n - 3)..];

var pluginByName = Enumerable.Range(0, paths.Count)
    .Where(p => !pluginKey[p].IsNull)
    .ToDictionary(p => PluginName(p), p => p, StringComparer.OrdinalIgnoreCase);
var responsiblePairs = new List<(int A, int B, string AName, string BName)>();
foreach (var named in responsiblePairNames)
{
    if (!pluginByName.TryGetValue(named.A, out int a) || !pluginByName.TryGetValue(named.B, out int b))
    {
        Console.Error.WriteLine($"--responsible-pair names a plugin outside the evaluated order: '{named.A}' / '{named.B}'");
        return 2;
    }
    if (a == b) { Console.Error.WriteLine("--responsible-pair requires two distinct plugins"); return 2; }
    responsiblePairs.Add(a < b ? (a, b, PluginName(a), PluginName(b)) : (b, a, PluginName(b), PluginName(a)));
}
responsiblePairs = responsiblePairs.DistinctBy(p => (p.A, p.B)).ToList();
var scopedResponsiblePlugins = responsiblePairs.SelectMany(p => new[] { p.A, p.B }).ToHashSet();
var candidatePlugins = candidatePluginNames.Where(pluginByName.ContainsKey).Select(n => pluginByName[n]).ToHashSet();
var absentCandidatePlugins = candidatePluginNames.Where(n => !pluginByName.ContainsKey(n))
    .Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(n => n, StringComparer.OrdinalIgnoreCase).ToList();
if (sceneCapture && absentCandidatePlugins.Count > 0 && sceneScopeFromPaths.Count == 0 && explicitSceneCells.Count == 0)
{
    Console.Error.WriteLine("candidate plugin(s) are absent from this setup and no explicit seeded scope proves that this is an intentional source pole: "
        + string.Join(", ", absentCandidatePlugins));
    return 2;
}
var candidateMods = candidateModNames.Where(n => cfg.EnabledMods.Contains(n, StringComparer.OrdinalIgnoreCase)).ToHashSet(StringComparer.OrdinalIgnoreCase);
var absentCandidateMods = candidateModNames.Where(n => !candidateMods.Contains(n)).Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(n => n, StringComparer.OrdinalIgnoreCase).ToList();
if (sceneCapture && absentCandidateMods.Count > 0 && sceneScopeFromPaths.Count == 0 && explicitSceneCells.Count == 0)
{
    Console.Error.WriteLine("candidate mod folder(s) are absent from this asset view and no explicit seeded scope proves an intentional source pole: " + string.Join(", ", absentCandidateMods));
    return 2;
}
var importedSceneReferences = importedSceneScopes.SelectMany(s => s.References).ToHashSet();
var sceneReferenceSeeds = importedSceneScopes.SelectMany(s => s.Seeds).Select(s => s.Reference).ToHashSet();
var sceneCells = new HashSet<FormKey>();
if (sceneCapture)
{
    foreach (var (key, _) in idx.History.Refs)
    {
        bool candidateTouched = idx.Touchers.TryGetValue(key, out var touchers) && touchers.Any(candidatePlugins.Contains);
        candidateTouched |= idx.BaseChanges.TryGetValue(key, out var bases) && bases.Any(b => candidatePlugins.Contains(b.Plugin));
        candidateTouched |= idx.WinningGeometryPose(key) is { } pose && candidatePlugins.Contains(pose.Plugin);
        if (!candidateTouched && !sceneReferenceSeeds.Contains(key)) continue;
        sceneReferenceSeeds.Add(key);
    }
}
var sceneSeedPoses = importedSceneScopes.SelectMany(s => s.Seeds)
    .Select(s => (s.Reference, s.Cell, s.Space, s.X, s.Y, s.Z, s.Interior, s.WorldBounds, s.BoundsReason, s.Provenance, IsImported: true)).ToList();
foreach (var key in sceneReferenceSeeds)
    if (idx.WinningGeometryPose(key) is { } pose)
    {
        bool interior = idx.CellName.TryGetValue(pose.Cell, out var ci) && ci.Interior;
        sceneSeedPoses.Add((key, pose.Cell, pose.Space, (double)pose.Pos.X, (double)pose.Pos.Y, (double)pose.Pos.Z, interior, (Aabb?)null, (string?)null, "current-candidate-or-imported", false));
    }
// A declared cell exists here only when this setup loads or masters its owning plugin (DeclaredCellPresence.cs).
var declarableOwners = DeclaredCellPresence.Declarable(cfg.Plugins.Select(p => (p.Name, p.Masters)));
var absentDeclaredCells = new List<DeclaredCellPresence.Row>();
// Ordered so a scope carrying several unusable cells always refuses on the same one. Without it
// the refusal depends on set-insertion order, which is a different message run to run for the same
// inputs — and a guard whose refusal is not reproducible cannot be asserted on.
foreach (var cell in explicitCellKeys.OrderBy(c => c.ToString(), StringComparer.Ordinal))
{
    string cellOrigin = cellBoundOrigin.TryGetValue(cell, out var declaredBy) ? declaredBy : "--scene-cell";
    var presence = DeclaredCellPresence.Classify(cell, cellOrigin, declarableOwners);
    if (presence.Outcome == DeclaredCellPresence.Refuse) { Console.Error.WriteLine(DeclaredCellPresence.RefuseLine(presence)); return 2; }
    if (presence.Outcome == DeclaredCellPresence.Absent) { absentDeclaredCells.Add(presence); Console.WriteLine("   " + DeclaredCellPresence.SkipLine(presence)); continue; }
    bool interior = idx.CellName.TryGetValue(cell, out var ci) && ci.Interior;
    if (interior) { sceneCells.Add(cell); continue; }
    if (idx.PersistentCells.Contains(cell))
    { Console.Error.WriteLine($"{cellOrigin} declares cell {cell}, which is an exterior persistent cell and has no bounded spatial footprint; supply --scene-scope-from or candidate seeds"); return 2; }
    var filed = idx.History.Refs.Select(kv => (kv.Key, Pose: idx.WinningGeometryPose(kv.Key)))
        .Where(x => x.Pose is { } p && p.Cell == cell).ToList();
    if (filed.Count == 0)
    { Console.Error.WriteLine($"{cellOrigin} declares cell {cell}, which has no placed reference from which to derive a bounded exterior footprint"); return 2; }
    foreach (var x in filed)
    {
        var p = x.Pose!.Value;
        sceneReferenceSeeds.Add(x.Key);
        sceneSeedPoses.Add((x.Key, p.Cell, p.Space, (double)p.Pos.X, (double)p.Pos.Y, (double)p.Pos.Z, false, (Aabb?)null, (string?)null, "explicit-exterior-cell", false));
    }
}
foreach (var seed in sceneSeedPoses.Where(s => s.Interior)) sceneCells.Add(seed.Cell);
var exteriorSeedSpaces = sceneSeedPoses.Where(s => !s.Interior).Select(s => s.Space).ToHashSet();
bool InSceneScope(FormKey key, Snap pose)
{
    if (!sceneCapture) return true;
    if (sceneCells.Contains(pose.Cell)) return true;
    // Origin distance cannot reject a mesh: a displaced-origin or very large NIF can reach the fixed seed
    // envelope from anywhere in the coordinate space. Geometry bounds perform the only spatial rejection.
    return exteriorSeedSpaces.Contains(pose.Space);
}
bool ScopedPair(int a, int b) => responsiblePairs.Count == 0
    || responsiblePairs.Any(p => (p.A == a && p.B == b) || (p.A == b && p.B == a));
// ---------------------------------------------------------------------------------------
// Pass 2 — strict decoded render geometry per base record. Record ObjectBounds never enter this path.
// ---------------------------------------------------------------------------------------
var geometryByBase = new Dictionary<FormKey, GeometryRead>();
var geometryByPath = new Dictionary<string, GeometryRead>(StringComparer.OrdinalIgnoreCase);
var renderCache = new NifRenderGeometryCache();
var geometryCounts = new Dictionary<string, long>(StringComparer.Ordinal);
long geometryLookupHits = 0;
long pathCacheHits = 0;

static string GeometryStatus(NifRenderGeometryStatus s) => s switch
{
    NifRenderGeometryStatus.Measured => "measured",
    NifRenderGeometryStatus.DegenerateGeometry => "degenerate_geometry",
    NifRenderGeometryStatus.MissingVertices => "missing_vertices",
    NifRenderGeometryStatus.PartialGeometry => "partial_geometry",
    NifRenderGeometryStatus.UnsupportedGeometry => "unsupported_geometry",
    NifRenderGeometryStatus.UnsupportedPose => "unsupported_pose",
    NifRenderGeometryStatus.InvalidTransform => "invalid_transform",
    NifRenderGeometryStatus.ParseError => "parse_error",
    NifRenderGeometryStatus.MissingAsset => "missing_asset",
    _ => "measurement_error",
};

GeometryRead GeometryOf(FormKey baseKey)
{
    if (geometryByBase.TryGetValue(baseKey, out var byBase)) { geometryLookupHits++; return byBase; }
    GeometryRead Finish(GeometryRead result)
    {
        geometryCounts[result.Status] = geometryCounts.GetValueOrDefault(result.Status) + 1;
        return geometryByBase[baseKey] = result;
    }
    if (!idx.BaseMesh.TryGetValue(baseKey, out var rel) || string.IsNullOrWhiteSpace(rel))
        return Finish(new("missing_asset", "the winning base record has no model path", null, null, null, null, null));

    var full = rel.StartsWith("meshes", StringComparison.OrdinalIgnoreCase) ? rel : Path.Combine("meshes", rel);
    full = full.Replace('/', '\\');
    if (geometryByPath.TryGetValue(full, out var byPath)) { pathCacheHits++; return Finish(byPath); }
    var resolution = assets.ResolveForPlacement(full);
    string pathAssetId = "asset-" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(full.ToLowerInvariant()))).ToLowerInvariant()[..20];
    GeometryRead result;
    if (resolution.Sources.Count == 0)
    {
        result = new(resolution.ReadIncomplete ? "asset_resolution_incomplete" : "missing_asset",
            resolution.ReadIncomplete
                ? "asset resolution is incomplete because at least one active archive could not be scanned"
                : "no active provider supplies the winning model path",
            full, pathAssetId, resolution, null, null);
    }
    else if (resolution.ReadIncomplete && resolution.Sources[0].Kind == AssetKind.Bsa)
    {
        result = new("asset_resolution_incomplete",
            "the apparent winner is archived while at least one active archive could not be scanned, so precedence is not established",
            full, pathAssetId, resolution, resolution.Sources[0], null);
    }
    else
    {
        var source = resolution.Sources[0];
        var (bytes, readError) = AssetResolver.ReadPlacementSource(source);
        if (bytes is null)
            result = new("asset_read_error", readError ?? "the resolved winner could not be read", full, pathAssetId,
                         resolution, source, null);
        else
        {
            var measured = renderCache.Measure(bytes, includeTriangles: true);
            if (motionSwept && measured.Status is NifRenderGeometryStatus.UnsupportedPose or NifRenderGeometryStatus.PartialGeometry) measured = SweptProxy(bytes, measured);
            result = new(GeometryStatus(measured.Status), measured.Reason, full, pathAssetId, resolution, source, measured,
                         bytes.LongLength, NifWind.Label(bytes));
        }
    }
    geometryByPath[full] = result;
    return Finish(result);
}

// --motion swept: the outer bound of a moving mesh's swept region as a soup of closed boxes, one per sampled pose, each
// grown by that sample's certified delta. A motion the files do not settle keeps the original unsupported_pose outcome.
NifRenderGeometryOutcome SweptProxy(byte[] bytes, NifRenderGeometryOutcome original)
{
    byte[]? Read(string r)
    {
        try { var res = assets.ResolveForPlacement(r); return res.Sources.Count == 0 ? null : AssetResolver.ReadPlacementSource(res.Sources[0]).Bytes; }
        catch (ArgumentException) { return null; }
    }
    // only a refusal caused by pose alone: every visible shape decoded completely, and at least one carries a pose issue
    var visible = original.Model?.Shapes.Where(s => !s.Hidden).ToList();
    if (visible is null || visible.Count == 0 || !visible.All(s => s.VerticesRead && s.TrianglesRead && s.InvalidTriangleCount == 0)
        || !visible.Any(s => s.PoseIssue is not null)) return original;
    // lane M: the shared reader (a clip-only member's region is exactly SweptGeometry.Build's; the base record is not known here)
    var model = MemberMotion.Read(bytes, Read, null);
    if (model.Status != "moving") return original;
    // review ruling (Gate C): only a region that converged in its budget is a proxy; otherwise today's refusal stands, named
    var region = MemberMotion.DiscoveryProxy(bytes, model, VolumeGeometry.Render, new MemberSweep(DeltaTarget: motionDelta), Read);
    if (region.Status != "built")
        return region.Reason is { } why && why.StartsWith("motion region did not converge", StringComparison.Ordinal)
            ? original with { Reason = $"{original.Reason}; {why}" } : original;
    // lane K: a pinned body whose controllers are left out is swept over its hinge swing, not over those controllers
    string anims = model.Labels.Contains(MemberMotion.AnimatedPinnedLabel)
        ? "the hinge swing (the stored pose and every angle within the file's limits); " + MemberMotion.AnimatedPinnedLabel
        : string.Join(", ", region.Animations.Select(a => $"{a.Id} {a.Cycle}"));
    return new NifRenderGeometryOutcome(NifRenderGeometryStatus.Measured,
        $"swept proxy: {region.SampleCount} sampled poses' boxes grown by their certified delta (max {region.MaxDelta:0.###} u, {(region.Converged ? "converged" : "not converged")}) over {anims}; an outer bound for discovery only",
        region.Box, region.Soup!, original.Model, original.ContentSha256, original.CacheKey + "|swept:" + motionDelta.ToString(System.Globalization.CultureInfo.InvariantCulture));
}

// --reach (G1 build list, Stage A): the reach region of a record that has no render box (MeshReach). The asset is the
// one discovery resolved for the base; a model-less reference with a primitive refuses naming its collision layer (the
// layer's solidity is not settled from the records yet); neither model nor primitive is no_geometry.
// --reach-rules: per reference, how many state-disabled candidate supports lay under it (the state filter dropped them)
var stateExcludedBelow = new Dictionary<FormKey, int>();
var terrainUnread = new HashSet<FormKey>();   // --reach-rules: a column over a LAND grid whose heights are unreadable
object ReachOf(FormKey key, FormKey baseKey, Snap p)
{
    object Out(ReachRegion r) => reachRules is null ? new
    {
        status = r.Status, reason = r.Reason, policy = r.Policy, box = ReachBox(r.Box), labels = r.Labels,
        parts = r.UncertifiedParts.Select(x => new { label = x.Label, box = ReachBox(x.Box) }).ToArray(),
    } : reachRules.Contains("rest") ? new
    {
        status = r.Status, reason = r.Reason, policy = r.Policy, box = ReachBox(r.Box), labels = r.Labels,
        parts = r.UncertifiedParts.Select(x => new { label = x.Label, box = ReachBox(x.Box) }).ToArray(),
        drop = r.Drop is not { } rd ? null : (object)new
        {
            status = rd.Status, distance = double.IsNaN(rd.Distance) ? (double?)null : rd.Distance,
            distanceMax = double.IsNaN(rd.DistanceMax) ? (double?)null : rd.DistanceMax,
            tiltMargin = double.IsNaN(rd.TiltMargin) ? (double?)null : rd.TiltMargin,
            renderBelowCollision = double.IsNaN(rd.RenderBelowCollision) ? (double?)null : rd.RenderBelowCollision,
            placed = ReachBox(rd.Placed), landed = ReachBox(rd.Landed), supportId = rd.SupportId,
            stateExcludedCandidates = stateExcludedBelow.GetValueOrDefault(key),
            lowerBound = rd.LowerBound, push = rd.Push == PushCause.None ? null : rd.Push.ToString(), pushDepth = Num(rd.PushDepth),
            pushUpDepth = Num(rd.PushUpDepth), movesOnLoad = rd.MovesOnLoad, shellCore = Num(rd.ShellCore),
            mount = rd.Mount is not { Pose: { } mp } mt ? null : (object)new
            {
                node = mt.Node, trigger = mt.Trigger, pos = new[] { (double)mp.Position.X, mp.Position.Y, mp.Position.Z },
                rotRad = new[] { (double)mp.Rotation.X, mp.Rotation.Y, mp.Rotation.Z },
            },
        },
    } : new
    {
        status = r.Status, reason = r.Reason, policy = r.Policy, box = ReachBox(r.Box), labels = r.Labels,
        parts = r.UncertifiedParts.Select(x => new { label = x.Label, box = ReachBox(x.Box) }).ToArray(),
        drop = r.Drop is not { } d ? null : (object)new
        {
            status = d.Status, distance = Num(d.Distance), distanceMax = double.IsNaN(d.DistanceMax) ? (double?)null : d.DistanceMax,
            placed = ReachBox(d.Placed), landed = ReachBox(d.Landed), supportId = d.SupportId,
            stateExcludedCandidates = stateExcludedBelow.GetValueOrDefault(key),
            lowerBound = d.LowerBound, push = d.Push == PushCause.None ? null : d.Push.ToString(), pushDepth = Num(d.PushDepth),
            pushUpDepth = Num(d.PushUpDepth), movesOnLoad = d.MovesOnLoad, shellCore = Num(d.ShellCore),
        },
    };
    static double? Num(double x) => double.IsNaN(x) ? null : x;
    static object? ReachBox(Aabb b) => b.IsEmpty ? null
        : new { min = new[] { (double)b.Min.X, b.Min.Y, b.Min.Z }, max = new[] { (double)b.Max.X, b.Max.Y, b.Max.Z } };
    byte[]? Read(string rel)
    {
        try { var res = assets.ResolveForPlacement(rel); return res.Sources.Count == 0 ? null : AssetResolver.ReadPlacementSource(res.Sources[0]).Bytes; }
        catch (ArgumentException) { return null; }
    }
    var g = GeometryOf(baseKey);
    if (g.Source is { } src)
    {
        var (bytes, err) = AssetResolver.ReadPlacementSource(src);
        if (bytes is null) return Out(new ReachRegion("refused", "asset read failed: " + err, Aabb.Empty, MeshReach.PolicyVersion, Array.Empty<string>(), Array.Empty<ReachPart>()));
        if (reachRules is null) return Out(MeshReach.OfModel(bytes, p.Pos, p.Rot, p.Scale, Read, new ReachOptions(motionDelta)));
        var region = RegionOf(key, baseKey, p, bytes);
        if (!p.Interior && region.Drop is not null && terrainUnread.Contains(key))
            region = region with { Status = "uncertified", Labels = region.Labels.Append(PhysicsReach.TerrainNotReadLabel).ToList() };
        return Out(region);
    }
    if (g.RelativePath is not null)
    {
        string why = $"{g.Status}: {g.Reason}";
        // Part 3b absent: a model no active provider supplies, measured in game to draw nothing, is no_geometry (with both facts)
        if (reachRules?.Contains("absent") == true && g.Status == "missing_asset" && AbsentModels() is { } absent)
            return Out(MeshReach.OfAbsentModel(g.RelativePath, absent, why));
        return Out(new ReachRegion("refused", why, Aabb.Empty, MeshReach.PolicyVersion, Array.Empty<string>(), Array.Empty<ReachPart>()));
    }
    if (idx.RefPrimitive.TryGetValue(key, out var prim))
    {
        string layer = idx.RefCollisionLayer.TryGetValue(key, out var li)
            ? (idx.CollisionLayersByIndex.TryGetValue(li, out var cl) ? cl.Edid ?? $"index {li}" : $"index {li} (no COLL record)")
            : "none stated";
        string primRefusal = $"primitive {prim.Type} (bounds {prim.Bounds.X} {prim.Bounds.Y} {prim.Bounds.Z}), collision layer {layer}: the layer's solidity is not settled";
        if (reachRules?.Contains("primitive") == true)
        {
            string? btype = idx.BaseInfo.TryGetValue(baseKey, out var pbi) ? pbi.Type : null;
            uint? li2 = idx.RefCollisionLayer.TryGetValue(key, out var lv) ? lv : null;
            var earlier = MeshReach.OfPrimitiveRule(btype, false, li2, primRefusal);   // reached only with no model path
            if (earlier.Status != "refused" || PrimitiveTable() is not { } table) return Out(earlier);
            // Part 3b: the measured (base, layer) table decides; XPRM bounds are full extents (station T and Q: the top sits at
            // centre + bounds_z / 2); a scaled reference is not in the measurement
            if (Math.Abs(p.Scale - 1f) > 1e-4f) return Out(new ReachRegion("refused", primRefusal + $"; reference scale {p.Scale} (primitives were measured at scale 1)", Aabb.Empty, MeshReach.PolicyVersion, Array.Empty<string>(), Array.Empty<ReachPart>()));
            string? lname = li2 is null ? null : (idx.CollisionLayersByIndex.TryGetValue(li2.Value, out var cl2) ? cl2.Edid : null);
            if (li2 is not null && lname is null) return Out(earlier);
            var facts = new PrimitiveFacts(baseKey.ToString(), btype, lname, new System.Numerics.Vector3(prim.Bounds.X / 2, prim.Bounds.Y / 2, prim.Bounds.Z / 2), p.Pos, p.Rot);
            return Out(PrimitiveSolidity.Decide(facts, table, primRefusal));
        }
        return Out(new ReachRegion("refused", primRefusal, Aabb.Empty, MeshReach.PolicyVersion, Array.Empty<string>(), Array.Empty<ReachPart>()));
    }
    return Out(new ReachRegion("no_geometry", "no model path and no primitive", Aabb.Empty, MeshReach.PolicyVersion, Array.Empty<string>(), Array.Empty<ReachPart>()));
}

// --reach-rules rest: whether a base's model has a free (unconstrained, movable) body, memoised per base
var freeBodyMemo = new Dictionary<FormKey, bool>();
bool HasFreeBody(FormKey baseKey)
{
    if (freeBodyMemo.TryGetValue(baseKey, out var known)) return known;
    bool free = false;
    if (GeometryOf(baseKey).Source is { } src && AssetResolver.ReadPlacementSource(src).Bytes is { } bytes)
        try { free = PhysicsReach.Classify(bytes).Kind is "free" or "free-chain"; } catch (InvalidDataException) { free = false; }
    return freeBodyMemo[baseKey] = free;
}

(TriangleSoup? Soup, char Source, string? Reason) SurfaceGeometryOf(FormKey baseKey)
{
    var geometry = GeometryOf(baseKey);
    return geometry.Outcome is { UsableForSurfaceIntersection: true, Soup: { TriangleCount: > 0 } soup }
        ? (soup, 'm', null)
        : (null, '-', geometry.Reason ?? "no complete strict render triangles");
}

if (sceneCapture && candidateMods.Count > 0)
    foreach (var (key, _) in idx.History.Refs)
    {
        bool candidateAsset = false;
        if (idx.RefBase.TryGetValue(key, out var baseKey) && idx.BaseMesh.TryGetValue(baseKey, out var rel) && !string.IsNullOrWhiteSpace(rel))
        {
            var full = (rel.StartsWith("meshes", StringComparison.OrdinalIgnoreCase) ? rel : Path.Combine("meshes", rel)).Replace('/', '\\');
            var source = assets.ResolveForPlacement(full).Sources.FirstOrDefault();
            candidateAsset = source is not null && candidateMods.Contains(source.ProviderName);
        }
        bool candidateBos = idx.BosSwaps.TryGetValue(key, out var bos) && activeBosLayer is not null
            && new[] { bos.PropertiesFrom?.Ini, bos.SwapFrom?.Where.Ini }.Where(x => x is not null)
                .Any(ini => activeBosLayer.Inis.Any(file => string.Equals(file.Name, ini, StringComparison.OrdinalIgnoreCase)
                    && candidateMods.Contains(file.Provider)));
        if (!candidateAsset && !candidateBos) continue;
        sceneReferenceSeeds.Add(key);
        if (idx.WinningGeometryPose(key) is { } pose && !sceneSeedPoses.Any(s => s.Reference == key))
        {
            bool interior = idx.CellName.TryGetValue(pose.Cell, out var ci) && ci.Interior;
            sceneSeedPoses.Add((key, pose.Cell, pose.Space, (double)pose.Pos.X, (double)pose.Pos.Y, (double)pose.Pos.Z, interior, (Aabb?)null, (string?)null, candidateAsset ? "candidate-winning-nif-provider" : "candidate-bos-provider", false));
            if (interior) sceneCells.Add(pose.Cell);
            else exteriorSeedSpaces.Add(pose.Space);
        }
    }
if (sceneCapture && sceneReferenceSeeds.Count == 0 && explicitCellKeys.Count == 0)
{
    Console.Error.WriteLine("scene scope resolved no seed references; candidate plugins/mods made no discoverable record, winning-NIF or BOS change, so supply --scene-scope-from or --scene-cell");
    return 2;
}

var sceneSeedBounds = sceneSeedPoses.Where(s => s.WorldBounds is { IsEmpty: false })
    .Select(s => (s.Reference, s.Space, Bounds: s.WorldBounds!.Value, Source: "imported")).ToList();
var unknownSceneSeedBounds = sceneSeedPoses.Where(s => s.IsImported && s.WorldBounds is null)
    .Select(s => $"{s.Reference}: {s.BoundsReason ?? "imported seed footprint is unknown"}").Distinct(StringComparer.Ordinal).ToList();
if (sceneCapture)
    foreach (var seed in sceneSeedPoses.DistinctBy(s => s.Reference))
    {
        if (idx.WinningGeometryPose(seed.Reference) is not { } pose || !idx.RefBase.TryGetValue(seed.Reference, out var baseKey))
        { if (seed.WorldBounds is null) unknownSceneSeedBounds.Add($"{seed.Reference}: absent here and imported scope has no bounds"); continue; }
        var (soup, _, reason) = SurfaceGeometryOf(baseKey);
        if (soup is not { TriangleCount: > 0 } || pose.Scale == 0)
        { if (seed.WorldBounds is null) unknownSceneSeedBounds.Add($"{seed.Reference}: {reason ?? "placed geometry unavailable"}"); continue; }
        var bounds = SpatialOverlap.SoupBounds(SpatialOverlap.TransformSoup(soup, pose.Pos, pose.Rot, pose.Scale));
        if (bounds.IsEmpty || !SpatialOverlap.IsFinite(bounds))
        { if (seed.WorldBounds is null) unknownSceneSeedBounds.Add($"{seed.Reference}: placed bounds invalid"); continue; }
        sceneSeedBounds.Add((seed.Reference, pose.Space, bounds, "current"));
    }
var sceneSeedBoundsBySpace = sceneSeedBounds.GroupBy(s => s.Space)
    .ToDictionary(g => g.Key, g => g.Select(s => s.Bounds).ToArray());
var exteriorScopeSeeds = new List<SceneCapture.ImportedSeed>();
foreach (var seed in sceneSeedPoses.Where(s => !s.Interior).DistinctBy(s => s.Reference))
{
    var known = sceneSeedBounds.Where(b => b.Reference == seed.Reference && b.Space == seed.Space).ToList();
    if (known.Count == 0)
        exteriorScopeSeeds.Add(new(seed.Reference, seed.Cell, seed.Space, seed.X, seed.Y, seed.Z, false,
            null, seed.BoundsReason ?? "seed footprint is unavailable", seed.Provenance));
    else
        exteriorScopeSeeds.AddRange(known.Select(b => new SceneCapture.ImportedSeed(seed.Reference, seed.Cell,
            seed.Space, seed.X, seed.Y, seed.Z, false, b.Bounds, null, seed.Provenance)));
}
bool BoundsInSceneScope(SurfaceOverlapCandidate candidate)
{
    if (!sceneCapture || sceneReferenceSeeds.Contains(candidate.Key) || sceneCells.Contains(candidate.Win.Cell)) return true;
    float margin = (float)SceneCapture.ExteriorContextMargin;
    return sceneSeedBoundsBySpace.TryGetValue(candidate.Win.Space, out var bounds)
        && bounds.Any(seed => candidate.WorldBounds.Max.X >= seed.Min.X - margin && candidate.WorldBounds.Min.X <= seed.Max.X + margin
        && candidate.WorldBounds.Max.Y >= seed.Min.Y - margin && candidate.WorldBounds.Min.Y <= seed.Max.Y + margin
        && candidate.WorldBounds.Max.Z >= seed.Min.Z - margin && candidate.WorldBounds.Min.Z <= seed.Max.Z + margin);
}

// THE PHYSICAL PREDICATE of an OVERLAP row: the two winning strict render surfaces still intersect under this arm.
// Bounds can reject only provable separation inside MeshIntersection; their depth, volume and dimensions never decide.
EnableArms? resolvedArms = null;
// --reach-rules: the drop's supports. Every reference of the cell (interior) or the 3x3 neighbourhood by position (exterior)
// that is not deleted and is ENABLED IN THE EVALUATED STATE (the same population as the records: the state arm's enable
// resolution, or the flag under --flags) and not BOS-unresolved; its model's bodies (frames, chain, placement) or a
// primitive on a stated layer. Terrain is not read (the fall can only be longer; labelled outdoors).
Dictionary<(FormKey Space, int X, int Y), List<(FormKey Key, Snap Pose, bool Enabled)>>? supportCells = null;
var regionMemo = new Dictionary<FormKey, ReachRegion>();
Dictionary<FormKey, (FormKey Act, FormKey Trig)>? rackStarting = null;
LooseReadings<FormKey>? loosePass = null;
var supportCache = new Dictionary<FormKey, IReadOnlyList<(DropSupport S, Aabb Box)>>();
var bodyCache = new Dictionary<FormKey, IReadOnlyList<(uint Layer, bool Solid, bool Movable, bool Complete, TriangleSoup Model)>?>();
var coreBodyCache = new Dictionary<FormKey, IReadOnlyList<(uint Layer, bool Solid, bool Movable, bool Complete, TriangleSoup Model)>?>();   // the same, radius off (shell-crossing check)
Dictionary<uint, CollisionLayerRule>? reachLayers = null;
HashSet<FormKey>? bookArtStatics = null;
(FormKey, int, int) SupportCellOf(Snap p) => p.Interior ? (p.Cell, 0, 0) : (p.Worldspace, (int)MathF.Floor(p.Pos.X / 4096f), (int)MathF.Floor(p.Pos.Y / 4096f));
// the state filter of the support population (one place, so a sabotage arm can switch it off)
bool SupportEnabled(FormKey key, Snap p)
{
    if (popMode == PopulationMode.Flags) return !p.Disabled;
    var e = resolvedArms!.Winner(key);
    return e.Enabled && e.Resolved;
}
void BuildSupportCells()
{
    supportCells = new();
    foreach (var (key, _) in idx.History.Refs)
    {
        var pose = idx.WinningGeometryPose(key);
        if (pose is null) continue;
        var sp = pose.Value;
        if (sp.Deleted || idx.IsDeletedNow(key)) continue;
        if (idx.BosSwaps.TryGetValue(key, out var bs) && (bs.Random || bs.PerSession || bs.PropertiesRandom || bs.Alternatives.Count != 1)) continue;
        var ck = SupportCellOf(sp);
        if (!supportCells.TryGetValue(ck, out var list)) supportCells[ck] = list = new();
        list.Add((key, sp, SupportEnabled(key, sp)));
    }
}
IReadOnlyList<(DropSupport S, Aabb Box)> PlacedSupports(FormKey k, Snap sp)
{
    if (supportCache.TryGetValue(k, out var hit)) return hit;
    var result = new List<(DropSupport, Aabb)>();
    if (idx.RefBase.TryGetValue(k, out var b))
    {
        var g = GeometryOf(b);
        if (g.Source is { } src)
        {
            if (!bodyCache.TryGetValue(b, out var bodies))
            {
                var (bytes, _) = AssetResolver.ReadPlacementSource(src);
                try { bodies = bytes is null ? null : PhysicsReach.ModelBodies(bytes); } catch (Exception) { bodies = null; }
                bodyCache[b] = bodies;
                try { coreBodyCache[b] = bytes is null ? null : PhysicsReach.ModelBodies(bytes, convexRadius: false); } catch (Exception) { coreBodyCache[b] = null; }
            }
            if (bodies is not null)
            {
                bool movableBase = !(idx.BaseInfo.TryGetValue(b, out var bi) && bi.Type is "Static");
                foreach (var s in PhysicsReach.PlaceSupports(k.ToString(), bodies, sp.Pos, sp.Rot, sp.Scale, movableBase, coreBodyCache.GetValueOrDefault(b)))
                    result.Add((s, SpatialOverlap.SoupBounds(s.World)));
            }
        }
        else if (idx.RefPrimitive.TryGetValue(k, out var prim) && prim.Type == "Box" && idx.RefCollisionLayer.TryGetValue(k, out var li))
        {
            var (bv, bi2) = HavokCollision.BoxSoup(new System.Numerics.Vector3(prim.Bounds.X / 2, prim.Bounds.Y / 2, prim.Bounds.Z / 2));
            var w = SpatialOverlap.TransformSoup(new TriangleSoup(bv.ToArray(), bi2.ToArray()), sp.Pos, sp.Rot, 1f);
            result.Add((new DropSupport(k.ToString(), li, true, false, w), SpatialOverlap.SoupBounds(w)));
        }
    }
    supportCache[k] = result;
    return result;
}
static TriangleSoup Raise(TriangleSoup t, float h) => new(t.Vertices.Select(v => v + new System.Numerics.Vector3(0, 0, h)).ToArray(), t.Indices);
IEnumerable<DropSupport> SupportsUnder(FormKey self, Snap p, Aabb column)
{
    if (supportCells is null) BuildSupportCells();
    var (space, gx, gy) = SupportCellOf(p);
    var cells = p.Interior ? new[] { (space, 0, 0) } : Enumerable.Range(-1, 3).SelectMany(dx => Enumerable.Range(-1, 3).Select(dy => (space, gx + dx, gy + dy))).ToArray();
    var found = new List<DropSupport>(); int excluded = 0;
    float selfBottom = PlacedSupports(self, p).Select(x => x.Box.Min.Z).DefaultIfEmpty(column.Max.Z).Min();
    foreach (var c in cells)
    {
        if (!supportCells!.TryGetValue(c, out var list)) continue;
        foreach (var (k, sp, enabled) in list)
        {
            if (k == self) continue;
            foreach (var (s, box) in PlacedSupports(k, sp))
            {
                if (box.Max.X < column.Min.X || box.Min.X > column.Max.X || box.Max.Y < column.Min.Y || box.Min.Y > column.Max.Y || box.Min.Z > column.Max.Z) continue;
                if (!enabled) { excluded++; continue; }
                // rest rule part 3, lowest first: only a loose body whose placed bottom lies below this body's can support it (a strict
                // order: two loose bodies cannot each be the other's support)
                if (!s.Movable || reachRules?.Contains("rest") != true || box.Min.Z >= selfBottom) { found.Add(s); continue; }
                // rest rule part 3: a loose body is a support when its own reading holds it; a lifted one also raised by its upward push
                var role = LooseRoleOf(k, sp);
                // the loose support's other readings (one group, so the body's landing is the range between them): a lifted body is
                // pushed up by up to its upward push and may turn as it is (its region reaches its tilt margin below its placed bottom,
                // part 1 KH-T; labr2: ingot 2B9EA9 turned 53 degrees under 2B9EA8); a body that may stay or drop (dropped-range from 0)
                // may be as low as its longest reading
                var alts = new List<float>();
                if (role == LooseRole.Holds && regionMemo.TryGetValue(k, out var lr) && lr.Drop is { } ld)
                {
                    if (ld.Status == "lifted" && ld.PushUpDepth > 0) alts.Add((float)ld.PushUpDepth);
                    if (ld.Status == "lifted" && ld.TiltMargin > 0) alts.Add(-(float)ld.TiltMargin);
                    if (ld.Status == "dropped-range" && ld.DistanceMax > 0) alts.Add(-(float)ld.DistanceMax);
                }
                if (alts.Count == 0) found.Add(s with { Loose = role });
                else
                {
                    var g = "loose " + k;
                    found.Add(s with { Loose = role, Group = g });
                    foreach (var h in alts)
                        found.Add(s with { Loose = role, Group = g, World = Raise(s.World, h), CoreWorld = s.CoreWorld is null ? null : Raise(s.CoreWorld, h) });
                }
            }
        }
    }
    stateExcludedBelow[self] = excluded;
    // outdoors the terrain is a support: each LAND grid of the 3x3 under the column, cut both ways (PhysicsReach.TerrainSupports)
    if (!p.Interior)
        foreach (var (ws, cx, cy) in cells)
        {
            if (idx.Land.TryGetValue((ws, cx, cy), out var heights))
                found.AddRange(PhysicsReach.TerrainSupports($"LAND {ws} {cx},{cy}", heights, cx, cy, column, (uint)NiflySharp.Enums.SkyrimLayer.SKYL_TERRAIN));
            else if (idx.LandUnreadable.ContainsKey((ws, cx, cy))) terrainUnread.Add(self);
        }
    return found;
}
List<PoseConfirmation>? poseConfirmations = null;
// a confirmation's optional "pose": {"clip": "<Data-relative clip path>", "sweep": "loop" | [t0, t1], "label": "..."} - a confirmed
// clip swept on the confirmed bytes ("loop": the clip's whole span)
static (string? Clip, float[]? Sweep, string? Label) ReadPose(System.Text.Json.JsonElement e)
{
    if (!e.TryGetProperty("pose", out var po) || po.ValueKind != System.Text.Json.JsonValueKind.Object) return (null, null, null);
    string clip = po.GetProperty("clip").GetString() ?? throw new ArgumentException("--pose-confirmations: pose.clip is empty");
    float[]? sweep = null;
    if (po.TryGetProperty("sweep", out var sw))
    {
        if (sw.ValueKind == System.Text.Json.JsonValueKind.Array) sweep = sw.EnumerateArray().Select(x => (float)x.GetDouble()).ToArray();
        else if (sw.GetString() != "loop") throw new ArgumentException("--pose-confirmations: pose.sweep is \"loop\" or [t0, t1]");
        if (sweep is { Length: not 2 }) throw new ArgumentException("--pose-confirmations: pose.sweep [t0, t1] needs two numbers");
    }
    return (clip, sweep, po.TryGetProperty("label", out var l) ? l.GetString() : null);
}
// a confirmation's optional "locals": {node: {"T": [x,y,z], "R": [9 numbers, rows], "S": s}} - a confirmed non-stored pose as data
static IReadOnlyDictionary<string, PoseLocal>? ReadLocals(System.Text.Json.JsonElement e)
{
    if (!e.TryGetProperty("locals", out var lo) || lo.ValueKind != System.Text.Json.JsonValueKind.Object) return null;
    var d = new Dictionary<string, PoseLocal>(StringComparer.Ordinal);
    foreach (var n in lo.EnumerateObject())
    {
        var t = n.Value.GetProperty("T").EnumerateArray().Select(x => (float)x.GetDouble()).ToArray();
        var r = n.Value.GetProperty("R").EnumerateArray().Select(x => (float)x.GetDouble()).ToArray();
        if (t.Length != 3 || r.Length != 9) throw new ArgumentException($"--pose-confirmations: locals '{n.Name}' needs T[3] and R[9]");
        d[n.Name] = new PoseLocal(new System.Numerics.Vector3(t[0], t[1], t[2]),
            new System.Numerics.Matrix4x4(r[0], r[1], r[2], 0, r[3], r[4], r[5], 0, r[6], r[7], r[8], 0, 0, 0, 0, 1), (float)n.Value.GetProperty("S").GetDouble());
    }
    return d;
}
IReadOnlyList<SettleExemption>? settleExemptions = null;
Dictionary<FormKey, EnableResolution> settleBaselineMemo = new();
// the load-settle facts of one reference: its winning REFR flags and whether it is enabled
// at load (false only when a judged state enables a reference the load-time reading has disabled)
SettleFacts SettleFactsOf(FormKey key, FormKey baseKey)
{
    if (settleExemptions is null)
    {
        var path = settleExemptionsPath ?? Path.Combine(AppContext.BaseDirectory, "load-settle-exemptions.json");
        if (!File.Exists(path)) throw new FileNotFoundException("--reach-rules physics: the load-settle exemption list is missing", path);
        settleExemptions = LoadSettle.Read(File.ReadAllText(path));
    }
    uint flags = idx.RefRecordFlags.TryGetValue(key, out var fl) ? fl : 0u;
    bool atLoad = true;
    if (stateArm is { IsBaseline: false } && resolvedArms is { } ra)
    {
        var b = EnableState.Resolve(key, ra.WinnerRawFacts, settleBaselineMemo);
        atLoad = b.Resolved && b.Enabled;
    }
    if (resolvedArms?.IsForced(key) == true) atLoad = false;   // --force-enable: enabled after load, by construction
    return new SettleFacts(flags, atLoad);   // the model's BSX is read by the reach reader
}
IReadOnlyList<PrimitiveMeasurement>? primitiveTable = null;
IReadOnlyList<PrimitiveMeasurement>? PrimitiveTable()
{
    if (primitiveSolidityPath is null) return null;
    return primitiveTable ??= PrimitiveSolidity.Read(File.ReadAllText(primitiveSolidityPath));
}
IReadOnlyList<AbsenceMeasurement>? absentModels = null;
IReadOnlyList<AbsenceMeasurement>? AbsentModels()
{
    if (absentModelsPath is null) return null;
    return absentModels ??= System.Text.Json.JsonSerializer.Deserialize<List<AbsenceMeasurement>>(File.ReadAllText(absentModelsPath),
        new System.Text.Json.JsonSerializerOptions { PropertyNameCaseInsensitive = true }) ?? new List<AbsenceMeasurement>();
}
List<TransferSource>? transferSources = null;
// Part 3b transfer: the confirmed models (--pose-confirmations, stored-pose entries) as transfer sources, their graph facts read
// through this order (bytes must match the confirmation's sha256)
List<TransferSource> TransferSources()
{
    if (transferSources is not null) return transferSources;
    transferSources = new();
    foreach (var c in poseConfirmations ?? new List<PoseConfirmation>())
    {
        if (c.Clip is not null || c.Locals is { Count: > 0 }) continue;
        var full = (c.ModelPath.StartsWith("meshes", StringComparison.OrdinalIgnoreCase) ? c.ModelPath : Path.Combine("meshes", c.ModelPath)).Replace('/', '\\');
        var src = assets.ResolveForPlacement(full).Sources.FirstOrDefault();
        if (src is null || AssetResolver.ReadPlacementSource(src).Bytes is not { } b) continue;
        if (!string.Equals(Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(b)), c.Sha256, StringComparison.OrdinalIgnoreCase)) continue;
        byte[]? TRead(string r) { try { var res = assets.ResolveForPlacement(r); return res.Sources.Count == 0 ? null : AssetResolver.ReadPlacementSource(res.Sources[0]).Bytes; } catch (ArgumentException) { return null; } }
        if (NifMotion.GraphFacts(b, TRead, new GraphOptions(true, true, true, ClipNames: reachRules!.Contains("clipnames"))) is { } f)
            transferSources.Add(new TransferSource(c.ModelPath, f.Files, f.DrivenLocals, c.Label ?? MeshReach.ConfirmedPoseLabel));
    }
    return transferSources;
}
// rest rule (2026-10-13): one region per reference, shared by its own row and by the loose-support pass over the bodies above it
ReachRegion RegionOf(FormKey key, FormKey baseKey, Snap p, byte[] bytes)
{
    if (regionMemo.TryGetValue(key, out var hit)) return hit;
    byte[]? ReadAsset(string rel)
    {
        try { var res = assets.ResolveForPlacement(rel); return res.Sources.Count == 0 ? null : AssetResolver.ReadPlacementSource(res.Sources[0]).Bytes; }
        catch (ArgumentException) { return null; }
    }
    var r = MeshReach.OfModel(bytes, p.Pos, p.Rot, p.Scale, ReadAsset, new ReachOptions(motionDelta, Rules: RulesFor(key, baseKey, p)));
    return regionMemo[key] = r;
}
// rest rule part 3: a loose body's role as a support, read once, lowest first by recursion (LooseReadings reports a cycle)
LooseRole LooseRoleOf(FormKey k, Snap sp)
{
    loosePass ??= new LooseReadings<FormKey>((key, _) =>
    {
        var pose = idx.WinningGeometryPose(key);
        if (pose is null || !idx.RefBase.TryGetValue(key, out var b) || GeometryOf(b).Source is not { } src) return LooseRole.Moves;
        var (bytes, _) = AssetResolver.ReadPlacementSource(src);
        if (bytes is null) return LooseRole.Moves;
        var r = RegionOf(key, b, pose.Value, bytes);
        if (r.Status == "refused") return LooseRole.Moves;
        // held where placed, or possibly so (a range from 0: its other readings are added as alternatives)
        return r.Drop?.Status is "touching" or "lifted" or "mounted" or "rest-gap" or "floats"
               || (r.Drop is { Status: "dropped-range" } dr && dr.Distance <= 1e-3) ? LooseRole.Holds : LooseRole.Moves;
    });
    return loosePass.Role(k);
}
// rest rule part 2: a weapon rack's starting item (the default linked ref of a WeaponRackActivateSCRIPT activator) and its trigger
RackMountReading? RackFor(FormKey key, Snap p)
{
    if (rackStarting is null)
    {
        rackStarting = new();
        foreach (var (k, links) in idx.RefLinkedRefs)
        {
            if (!idx.RefBase.TryGetValue(k, out var ab) || !idx.RackActivators.TryGetValue(ab, out var info)) continue;
            var trig = info.Objects.TryGetValue("WRackTrigger", out var wk) ? links.FirstOrDefault(l => l.Keyword == wk).Reference : FormKey.Null;
            foreach (var l in links) if (l.Keyword is null) rackStarting[l.Reference] = (k, trig);
        }
    }
    if (!rackStarting.TryGetValue(key, out var chain)) return null;
    var act = chain.Act; var info2 = idx.RackActivators[idx.RefBase[act]];
    RackMountReading Refused(string why) => new("refused", $"{RackMount.RefusedReason} ({why})", null, null, chain.Trig.IsNull ? null : chain.Trig.ToString());
    if (idx.RefScripts.TryGetValue(act, out var own) && own.Any(x => string.Equals(x, RackMount.ActivatorScript, StringComparison.OrdinalIgnoreCase)))
        return Refused($"the activator reference {act} carries its own script properties, which are not read");
    var ap = idx.WinningGeometryPose(act);
    bool actOn = ap is { } a0 && !a0.Deleted && !idx.IsDeletedNow(act) && SupportEnabled(act, a0);
    var tp = chain.Trig.IsNull ? null : idx.WinningGeometryPose(chain.Trig);
    bool trigPresent = tp is { } t0 && !t0.Deleted && !idx.IsDeletedNow(chain.Trig);
    bool trigOn = trigPresent && SupportEnabled(chain.Trig, tp!.Value);
    var names = info2.Objects.GroupBy(kv => kv.Value).ToDictionary(g => g.Key, g => g.Select(kv => kv.Key).ToList());
    var kws = new HashSet<string>(StringComparer.Ordinal);
    if (idx.RefBase.TryGetValue(key, out var ib) && idx.BaseKeywords.TryGetValue(ib, out var fks))
        foreach (var fk in fks) if (names.TryGetValue(fk, out var ns)) foreach (var n in ns) kws.Add(n);
    string? itype = idx.BaseInfo.TryGetValue(ib, out var bi2) ? bi2.Type : null;
    string kind = itype == "Weapon" ? "weapon" : itype == "Armor" && kws.Contains("ArmorShield") ? "shield" : "other";
    bool sameCell = idx.RefCell.TryGetValue(key, out var ic) && idx.RefCell.TryGetValue(act, out var ac) && ic.Cell == ac.Cell;
    var facts = new RackFacts(info2.Script, info2.RackType ?? 1, info2.CoaPatch ?? false, actOn, trigPresent, trigOn, SupportEnabled(key, p), sameCell, kind, kws);
    byte[]? trigNif = null;
    if (trigPresent && idx.RefBase.TryGetValue(chain.Trig, out var tb) && GeometryOf(tb).Source is { } tsrc) trigNif = AssetResolver.ReadPlacementSource(tsrc).Bytes;
    try
    {
        var rd = RackMount.Decide(facts, node => trigNif is null ? throw new InvalidOperationException("the trigger's model could not be read")
                                                            : RackMount.NodePose(trigNif, node, tp!.Value.Pos, tp.Value.Rot, tp.Value.Scale));
        return rd with { Trigger = chain.Trig.ToString() };
    }
    catch (Exception ex) when (ex is InvalidOperationException or InvalidDataException) { return Refused(ex.Message); }
}
ReachRules? RulesFor(FormKey key, FormKey baseKey, Snap p)
{
    if (reachRules is null) return null;
    if (poseConfirmationsPath is not null && poseConfirmations is null)
    {
        using var doc = System.Text.Json.JsonDocument.Parse(File.ReadAllText(poseConfirmationsPath));
        poseConfirmations = doc.RootElement.EnumerateArray()
            .Select(e => new PoseConfirmation(e.GetProperty("modelPath").GetString()!, e.GetProperty("sha256").GetString()!, e.GetProperty("evidence").GetString()!,
                                              ReadLocals(e), e.TryGetProperty("label", out var lb) ? lb.GetString() : (ReadPose(e).Label),
                                              ReadPose(e).Clip, ReadPose(e).Sweep)).ToList();
    }
    reachLayers ??= idx.CollisionLayerRules();
    bookArtStatics ??= idx.BookInventoryArt.Values.ToHashSet();
    string? type = idx.BaseInfo.TryGetValue(baseKey, out var bi) ? bi.Type : null;
    bool book = reachRules.Contains("book") && (type == "Book" || (type == "Static" && bookArtStatics.Contains(baseKey)));
    string? modelPath = GeometryOf(baseKey).RelativePath;
    return new ReachRules(reachRules.Contains("physics"), reachRules.Contains("graph"), reachRules.Contains("switch"), book, type,
                          q => SupportsUnder(key, p, q), reachLayers, poseConfirmations, modelPath, Skin: reachRules.Contains("skin"),
                          Particles: reachRules.Contains("particles"),
                          SettleFacts: reachRules.Contains("physics") ? SettleFactsOf(key, baseKey) : null,
                          SettleExemptions: reachRules.Contains("physics") ? settleExemptions : null, Rest: reachRules.Contains("rest"),
                          ClipNames: reachRules.Contains("clipnames"), Blend: reachRules.Contains("blend"), MissingSequence: reachRules.Contains("missingseq"),
                          PathHull: reachRules.Contains("path"), Emitter: reachRules.Contains("emitter"),
                          Transfers: reachRules.Contains("transfer") ? TransferSources() : null,
                          Gravity: reachRules.Contains("gravity") ? SettledGravities() : null,
                          Approximations: reachRules.Contains("approx") ? MeasuredApproximations() : null,
                          Rack: reachRules.Contains("rest") ? RackFor(key, p) : null);
}
// Part 3b round 2: --measured-approximations FILE, a JSON list of {modelPath, sha256, modelBox: {min: [3], max: [3]}, label, evidence}
IReadOnlyList<MeasuredApproximation>? measuredApproximations = null;
IReadOnlyList<MeasuredApproximation> MeasuredApproximations()
{
    if (measuredApproximations is not null) return measuredApproximations;
    if (approximationsPath is null) return measuredApproximations = Array.Empty<MeasuredApproximation>();
    using var doc = System.Text.Json.JsonDocument.Parse(File.ReadAllText(approximationsPath));
    var list = new List<MeasuredApproximation>();
    foreach (var e in doc.RootElement.EnumerateArray())
    {
        var mb = e.GetProperty("modelBox");
        float[] lo = mb.GetProperty("min").EnumerateArray().Select(x => x.GetSingle()).ToArray(), hi = mb.GetProperty("max").EnumerateArray().Select(x => x.GetSingle()).ToArray();
        string ev = e.GetProperty("evidence").GetString() ?? "";
        if (ev.Length == 0) throw new ArgumentException("--measured-approximations: an entry without evidence");
        list.Add(new MeasuredApproximation(e.GetProperty("modelPath").GetString()!, e.GetProperty("sha256").GetString()!,
                                           new Aabb(new Vec3(lo[0], lo[1], lo[2]), new Vec3(hi[0], hi[1], hi[2]), false), e.GetProperty("label").GetString()! + " (" + ev + ")"));
    }
    return measuredApproximations = list;
}
// Part 3b round 2: --settled-gravity FILE, a JSON list of {modelPath, sha256, systems: [..] (empty = every system),
// towardWorldNegativeZ: true, evidence}; an entry that does not say the particles fall toward world -z is refused (no other
// direction is modelled)
IReadOnlyList<SettledGravity>? settledGravities = null;
IReadOnlyList<SettledGravity> SettledGravities()
{
    if (settledGravities is not null) return settledGravities;
    if (settledGravityPath is null) return settledGravities = Array.Empty<SettledGravity>();
    using var doc = System.Text.Json.JsonDocument.Parse(File.ReadAllText(settledGravityPath));
    var list = new List<SettledGravity>();
    foreach (var e in doc.RootElement.EnumerateArray())
    {
        bool down = e.GetProperty("towardWorldNegativeZ").GetBoolean();
        if (!down) throw new ArgumentException("--settled-gravity: only towardWorldNegativeZ: true is modelled");
        string ev = e.GetProperty("evidence").GetString() ?? "";
        if (ev.Length == 0) throw new ArgumentException("--settled-gravity: an entry without evidence");
        list.Add(new SettledGravity(e.GetProperty("modelPath").GetString()!, e.GetProperty("sha256").GetString()!,
                                    e.TryGetProperty("systems", out var sy) ? sy.EnumerateArray().Select(x => x.GetString()!).ToList() : new List<string>(), true, ev));
    }
    return settledGravities = list;
}

// Pair-loop timers (P0): observation only; the console and the run record carry them after each stage.
var sameTimer = new PairTimer(); var crossTimer = new PairTimer(); var provenanceTimer = new PairTimer(); var reconcileTimer = new PairTimer();
(PhysicalState State, string Detail) OverlapPredicate(FormKey laterKey, FormKey otherKey)   // overlapprobe/OverlapCredit.cs
    => new OverlapCredit(idx, popMode, resolvedArms, SurfaceGeometryOf, surfaceTolerance, maxTrianglePairs, pairSeconds, reconcileTimer).Predicate(laterKey, otherKey);
void ReportTimer(string name, PairTimer t, double? stageSeconds)
{
    var c = t.Core;
    double S(long ticks) => MeshQueryTimer.Seconds(ticks);
    double wall = stageSeconds ?? S(t.WallTicks);
    double phases = S(t.PairFrameTicks + c.PrepareTicks + c.BvhTicks + c.SurfaceTicks + c.ValidateTicks + c.ContainmentTicks);
    Console.WriteLine($"   pair-loop timers {name}: {t.Pairs:N0} pairs ({c.Queries:N0} mesh queries), {wall:0.0}s; "
        + $"pairframe {S(t.PairFrameTicks):0.0}s, prepare {S(c.PrepareTicks):0.0}s, bvh {S(c.BvhTicks):0.0}s, surface {S(c.SurfaceTicks):0.0}s "
        + $"({c.SurfaceUnits:N0} u), validate {S(c.ValidateTicks):0.0}s ({c.ValidateUnits:N0} u), containment {S(c.ContainmentTicks):0.0}s "
        + $"({c.ContainmentUnits:N0} u), unaccounted {wall - phases:0.0}s; paths world {t.Paths[0]:N0} / zero-area skip {t.Paths[1]:N0} / "
        + $"shifted {t.Paths[2]:N0} / refused {t.Paths[3]:N0}; budget-exhausted {c.BudgetExhausted:N0}, time-outs {t.Timeouts:N0}, "
        + $"preparation refusals {c.PrepareRefused:N0}");
    string f = $"actual-shape-queries.{name}";
    foreach (var (k, v, u) in new (string, double, string)[] {
        ("pairs", t.Pairs, "pairs"), ("queries", c.Queries, "queries"), ("wall.seconds", wall, "seconds"),
        ("pairframe.seconds", S(t.PairFrameTicks), "seconds"), ("prepare.seconds", S(c.PrepareTicks), "seconds"),
        ("bvh.seconds", S(c.BvhTicks), "seconds"), ("surface.seconds", S(c.SurfaceTicks), "seconds"),
        ("validate.seconds", S(c.ValidateTicks), "seconds"), ("containment.seconds", S(c.ContainmentTicks), "seconds"),
        ("surface.units", c.SurfaceUnits, "units"), ("validate.units", c.ValidateUnits, "units"), ("containment.units", c.ContainmentUnits, "units"),
        ("timeouts", t.Timeouts, "pairs"), ("budget-exhausted", c.BudgetExhausted, "pairs") })
        __run.Feature($"{f}.{k}", v, u, FeatureAt.Final);
}

// ---------------------------------------------------------------------------------------
// Pass 3 — candidates, placed triangle soups, conservative bounds sweep, actual-shape pairs.
// ---------------------------------------------------------------------------------------
// A candidate is a winning, enabled, obstructing-typed ref whose responsible plugin is not the
// base game — vanilla-vs-mod pairs are excluded by the closure anyway (every mod masters
// Skyrim.esm), so hashing vanilla-responsible refs would only manufacture same-source pairs
// nobody asked about. DLC/CC-responsible refs stay in: DLC-on-DLC is the measured Bethesda floor.
// THE POPULATION (Q27). The default is the load-time state — the same facts the census would give, read through the
// shared arms so the default run and an explicit `--state baseline` are the same run by construction. --state needs the
// census as well, because a switch has to be looked up by name and its sets known.
var arms = new EnableArms(idx);
resolvedArms = arms;
if (forceEnable.Count > 0)
{
    var keys = new List<FormKey>();
    foreach (var t in forceEnable)
    {
        if (!FormKey.TryFactory(t, out var fk)) { Console.Error.WriteLine($"--force-enable: '{t}' is not a FormKey (XXXXXX:Plugin.esp)"); return 2; }
        keys.Add(fk);
    }
    var refused = arms.Force(keys);
    foreach (var (k, why) in refused) Console.Error.WriteLine($"--force-enable: {k} refused: {why}");
    if (refused.Count > 0) return 2;
    Console.WriteLine($"force-enable: {arms.Forced.Count} reference(s) {EnableArms.ForcedLabel}: {string.Join(", ", arms.Forced)}");
}
EnablePopulation pop;
if (stateSpec is not null)
{
    var sx = SwitchIndex.Build(idx);
    sx.NameSwitches(idx.RefEditorId);
    try { stateArm = StateArm.Parse(stateSpec, sx, switchMapPath is null ? null : SwitchMap.Read(switchMapPath)); }
    catch (ArgumentException ex) { Console.Error.WriteLine(ex.Message); return 2; }
    arms.State = stateArm.IsBaseline ? null : stateArm;
    pop = EnablePopulation.Of(arms.WinnerStateFacts, stateArm.Name);
    var changes = stateArm.Changes(sx).ToList();
    Console.WriteLine($"\nstate {stateArm.Name}: {sx.Switches.Count:N0} switches in the order; {stateArm.Flips.Count} flipped — {changes.Count(c => c.After):N0} refs arrive, {changes.Count(c => !c.After):N0} depart");
}
else pop = popMode == PopulationMode.Flags ? EnablePopulation.Flags() : EnablePopulation.Of(arms.WinnerRawFacts, EnablePopulation.BaselineStateName);
// Null in flags mode: Candidates then reads the winning snap's own flag, exactly as it always did.
enabledOf = pop.Predicate;
bool ExcludeResponsible(int p) => OverlapClass.ExcludeResponsible(sceneCapture, responsiblePairs.Count > 0, scopedResponsiblePlugins.Contains(p), IsBaseGame(p));
long candidateGeometryLookups = 0;
__run.Stage("render-geometry", "preparing strict render geometry and candidates", total: null, unit: "geometry lookups");
(TriangleSoup? Soup, char Source, string? Reason) CandidateGeometryOf(FormKey baseKey)
{
    var result = SurfaceGeometryOf(baseKey);
    candidateGeometryLookups++;
    __run.Advance();
    return result;
}
var candidates = idx.SurfaceCandidates(CandidateGeometryOf, Obstructing, ExcludeResponsible, out var skips, enabledOf,
    sceneCapture ? InSceneScope : null);
candidates = candidates.Where(BoundsInSceneScope).ToList();
var sceneUnknownScope = new Dictionary<FormKey, string>();
var sceneUnlocalizedExterior = new Dictionary<FormKey, string>();
var sceneOutsideExterior = new Dictionary<FormKey, string>();
if (sceneCapture)
    foreach (var (key, _) in idx.History.Refs)
    {
        var pose = idx.WinningGeometryPose(key);
        if (pose is null || !idx.RefBase.TryGetValue(key, out var baseKey)
            || !idx.BaseInfo.TryGetValue(baseKey, out var baseInfo) || !Obstructing.Contains(baseInfo.Type)) continue;
        var geometry = GeometryOf(baseKey);
        if (geometry.Outcome is { UsableForSurfaceIntersection: true, Soup.TriangleCount: > 0 }) continue;
        bool interior = idx.CellName.TryGetValue(pose.Value.Cell, out var ci) && ci.Interior;
        if (sceneReferenceSeeds.Contains(key) || (interior && sceneCells.Contains(pose.Value.Cell)))
            sceneUnknownScope[key] = geometry.Reason ?? "no complete strict render triangles; spatial membership is unresolved";
        else if (!interior)
        {
            var disposition = SceneCapture.ExteriorUnknownDisposition(pose.Value.Space,
                pose.Value.Pos.X, pose.Value.Pos.Y, pose.Value.Pos.Z, exteriorScopeSeeds);
            if (disposition.AdmitToScene)
                sceneUnknownScope[key] = geometry.Reason ?? "no complete strict render triangles in the bounded seed neighbourhood";
            else if (disposition.Membership == SceneCapture.ExteriorMembership.OutsideNeighbourhood)
                sceneOutsideExterior[key] = $"{geometry.Reason ?? "strict render geometry is unavailable"}; {disposition.ScopeReason}";
            else if (disposition.RetainUnassessed)
                sceneUnlocalizedExterior[key] = $"{geometry.Reason ?? "strict render geometry is unavailable"}; {disposition.ScopeReason}";
        }
    }
var sceneIncludedKeys = candidates.Select(c => c.Key).Concat(sceneReferenceSeeds).Concat(importedSceneReferences)
    .Concat(sceneUnknownScope.Keys).ToHashSet();
candidates = candidates.Where(c =>
{
    if (popMode != PopulationMode.Flags && !arms.Winner(c.Key).Resolved) { skips.EnableStateUnresolved++; return false; }
    if (idx.BosSwaps.TryGetValue(c.Key, out var bos) && (bos.Random || bos.PerSession || bos.PropertiesRandom || bos.Alternatives.Count != 1))
    { skips.BosUnresolved++; return false; }
    return true;
}).ToList();
__run.ReviseTotal(candidateGeometryLookups, "candidate source and state gates established the exact geometry lookup count");
Console.WriteLine("\n" + pop.Describe());

if (dumpPath is not null)
{
    Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(dumpPath))!);
    using var dw = new StreamWriter(dumpPath);
    dw.WriteLine("key\tresp\ttype\tmesh\tsrc\trotX\trotY\trotZ\tscale\tminX\tminY\tminZ\tmaxX\tmaxY\tmaxZ");
    foreach (var c in candidates)
        dw.WriteLine($"{c.Key}\t{PluginName(c.Win.Plugin)}\t{TypeOf(c.Base)}\t{MeshOf(c.Base)}\t{c.Src}"
                   + $"\t{c.Win.Rot.X:R}\t{c.Win.Rot.Y:R}\t{c.Win.Rot.Z:R}\t{c.Win.Scale:R}"
                   + $"\t{c.WorldBounds.Min.X:R}\t{c.WorldBounds.Min.Y:R}\t{c.WorldBounds.Min.Z:R}\t{c.WorldBounds.Max.X:R}\t{c.WorldBounds.Max.Y:R}\t{c.WorldBounds.Max.Z:R}");
    __run.Artifact(dumpPath);
    Console.WriteLine($"\ndumped {candidates.Count:N0} candidates -> {dumpPath}");
}

Console.WriteLine($"\ncandidates: {candidates.Count:N0} refs" + (sceneCapture ? " in the declared scene scope" : responsiblePairs.Count > 0 ? " in the responsible-pair scope" : " whose responsible plugin is not the base game"));
Console.WriteLine("   geometry (strict decoded render vertices; record ObjectBounds unused): "
                + string.Join(", ", geometryCounts.OrderBy(k => k.Key).Select(k => $"{k.Key} {k.Value:N0}")));
Console.WriteLine($"   skipped (counted, per rule 6): {skips.Deleted:N0} deleted, {skips.Disabled:N0} not present at load, "
                + $"{skips.NonObstructingType:N0} non-obstructing type, {skips.NoGeometry:N0} no geometry, {skips.ExcludedResponsible:N0} base-game-responsible, {skips.ScopeExcluded:N0} outside explicit scene scope,");
Console.WriteLine($"                                  {skips.InvalidTransform:N0} invalid transforms, {skips.DegenerateGeometry:N0} zero-scale transforms, {skips.MarkerMesh:N0} editor markers (no rendered mesh, cannot obstruct), "
                + $"{skips.EnableStateUnresolved:N0} unresolved enable state, {skips.BosUnresolved:N0} unresolved BOS outcome");

// The emit's coverage lines that the candidate stage settles, and the --base re-measurement: one definition each, used by
// the emit below and by --reverse-of.
long reconciliationPredicates = 0;
CoverageLine PluginsLine() => new("plugins", paths.Count - unreadablePlugins.Count, new Dictionary<string, long> { ["UnreadablePlugin"] = unreadablePlugins.Count });
CoverageLine BaseRecordsLine()
{
    long measuredBases = geometryCounts.GetValueOrDefault("measured") + geometryCounts.GetValueOrDefault("degenerate_geometry");
    var geometryUnknown = geometryCounts.Where(k => k.Key is not ("measured" or "degenerate_geometry") && k.Value > 0)
        .ToDictionary(k => k.Key, k => k.Value, StringComparer.Ordinal);
    return new("base records (strict render geometry)", measuredBases, geometryUnknown, null,
        $"policy {NifOverlapGeometry.SurfaceMeasurementIdentity(surfaceTolerance)}; record ObjectBounds unused");
}
// The unresolved enable chains join NoGeometry as unjudgeable: a candidate whose chain could not be read to the
// end is in the population on EnableState's stated fallback, not on a measurement, and conflictdiff's coverage
// test must be able to see that share rise (Q27).
CoverageLine CandidateRefsLine() => new("candidate refs", candidates.Count,
    new Dictionary<string, long>(pop.Unjudgeable) { ["NoGeometry"] = skips.NoGeometry,
        ["InvalidTransform"] = skips.InvalidTransform, ["DegenerateTransform"] = skips.DegenerateGeometry },
    new Dictionary<string, long> { ["Deleted"] = skips.Deleted, ["NotPresentAtLoad"] = skips.Disabled, ["NonObstructingType"] = skips.NonObstructingType,
                                   ["BaseGameResponsible"] = skips.ExcludedResponsible, ["EditorMarker"] = skips.MarkerMesh },
    $"population {pop.Identity} / state {pop.StateName}");
CoverageLine? PopulationScopeLine()
{
    if (population is null) return null;
    long inScope = candidates.Count(c => population.KeyInScope(c.Key, c.Win.Cell));
    return new("population scope", inScope, new Dictionary<string, long>(),
        new Dictionary<string, long> { ["OutsidePopulation"] = candidates.Count - inScope },
        $"pairs measured only where a reference is in the plan's population; {population.Describe()}");
}
string populationIdentity = population?.Identity(pop.Identity) ?? pop.Identity;
List<Predicate> MeasureBasePredicates(string path)
{
    var baseFile = ConflictIo.Read(path);
    var list = new List<Predicate>();
    foreach (var c in baseFile.Conflicts)
    {
        string? other = c.Alternative is { Count: > 0 } alt ? alt[0].FormKey : null;
        foreach (var rec in c.Necessary)
        {
            reconciliationPredicates++;
            __run.Advance();
            if (c.Kind != "OVERLAP" || other is null) { list.Add(new Predicate(c.Kind, c.A, c.B, rec.FormKey, other, PhysicalState.Unmeasured, "not an overlap row with two refs")); continue; }
            var (st, why) = OverlapPredicate(FormKey.Factory(rec.FormKey), FormKey.Factory(other));
            list.Add(new Predicate(c.Kind, c.A, c.B, rec.FormKey, other, st, why));
        }
    }
    Console.WriteLine($"\n   --base {Path.GetFileName(path)}: {list.Count:N0} base-row predicate(s) re-measured under this order — "
                    + string.Join(", ", list.GroupBy(p => p.Physical).OrderBy(g => g.Key).Select(g => $"{g.Key} {g.Count():N0}")));
    return list;
}
List<Predicate>? basePredicatesMeasured = null;
if (reverseOfPath is not null)
{
    var baseline = ConflictIo.Read(reverseOfPath);
    var identity = cfg.Identity(populationIdentity, pop.StateName);
    string policy = NifOverlapGeometry.SurfaceMeasurementIdentity(surfaceTolerance);
    var own = new List<CoverageLine> { PluginsLine(), CandidateRefsLine() };
    if (PopulationScopeLine() is { } scopeLine) own.Add(scopeLine);
    var baseRecords = BaseRecordsLine();
    string? refusal = ReverseFromBaseline.Refusal(baseline, reverseOfPath, Instance, identity, populationIdentity, pop.StateName, policy, own)
        ?? ReverseFromBaseline.BaseRecordsRefusal(baseline, reverseOfPath, baseRecords.Judged, baseRecords.Unjudgeable!);
    if (refusal is not null) { Console.Error.WriteLine(refusal); return 2; }
    // The baseline's base-record count also holds the bases its provenance stage looked up; a full reverse adds the bases
    // its --base rows reach. Carried only when those rows reach no base outside the candidates', so the two sets are equal.
    var candidateBases = geometryByBase.Keys.ToHashSet();
    __run.Stage("reconciliation", "remeasuring prior overlap predicates", total: null, unit: "predicates");
    basePredicatesMeasured = MeasureBasePredicates(basePath!);
    int reached = geometryByBase.Keys.Count(k => !candidateBases.Contains(k));
    ReportTimer("reconciliation", reconcileTimer, null);
    if (reached == 0)
    {
        __run.ReviseTotal(reconciliationPredicates, "the base emit established the exact predicate count");
        __run.Stage("emit-artifact", "writing overlap conflict artifact", 1, "artifacts");
        ConflictIo.Write(emitPath!, new ConflictFile("overlapprobe", Instance,
            DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm:ssZ"), baseline.Conflicts, baseline.Coverage, basePredicatesMeasured, basePath,
            populationIdentity, pop.StateName, identity, MeasurementPolicy: policy));
        __run.Advance().Artifact(emitPath!);
        Console.WriteLine($"configuration: {identity.Short}");
        Console.WriteLine($"\nreverse of {Path.GetFileName(reverseOfPath)}: its {baseline.Conflicts.Count:N0} Introduced + Blind conflicts and coverage "
                        + $"carried (no sweep run), {basePredicatesMeasured.Count:N0} --base predicate(s) measured -> {emitPath}");
        __run.Stage("finalize", "finalizing overlap run", 1, "runs").Advance();
        Console.WriteLine($"elapsed {sw.Elapsed.TotalSeconds:0.0}s");
        return 0;
    }
    Console.WriteLine($"   --reverse-of: the --base rows reach {reached:N0} base record(s) the candidates did not, so the baseline's base-record "
                    + "count cannot stand for this run's; measuring the full reverse");
}

long lastPairProgress = 0;
__run.Stage("actual-shape-queries", "querying candidate render surfaces", total: null, unit: "pairs");
StreamWriter? ledger = null;
if (pairLedgerPath is not null)
{
    Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(pairLedgerPath))!);
    ledger = new StreamWriter(pairLedgerPath, false, new System.Text.UTF8Encoding(false), 1 << 20);
}
void LedgerLine(SurfaceOverlapCandidate a, SurfaceOverlapCandidate b, SurfaceQueryOutcome q, PairPath? path, double seconds)
{
    if (ledger is null) return;
    ledger.WriteLine(JsonSerializer.Serialize(new
    {
        a = a.Key.ToString(), b = b.Key.ToString(), same = a.Win.Plugin == b.Win.Plugin, path = path?.ToString(),
        q.Status, q.Reason, q.SurfaceRelation, q.Containment, q.Intersects, q.TrianglePairsTested, q.Frame, q.ZeroAreaSkipped,
        seconds = Math.Round(seconds, 6),
    }));
}
long bvhNodesBefore = MeshIntersection.BvhNodesBuilt;
double sweepStarted = sw.Elapsed.TotalSeconds;
var sweep = OverlapIndex.SurfacePairs(candidates, closure, surfaceTolerance, maxTrianglePairs,
    TimeSpan.FromSeconds(pairSeconds), responsiblePairs.Count > 0 ? ScopedPair : null,
    checkedPairs =>
    {
        __run.Progress(checkedPairs);
        if (checkedPairs - lastPairProgress < 250) return;
        lastPairProgress = checkedPairs;
        Console.WriteLine($"   ... {checkedPairs:N0} actual-shape pairs tested ({sw.Elapsed.TotalSeconds:0}s)");
    }, retainSameResults: sceneCapture, includePair: population is null ? null : PairInPopulation,
    sameTimer: sameTimer, crossTimer: crossTimer, onPair: LedgerLine);
double sweepSeconds = sw.Elapsed.TotalSeconds - sweepStarted;
__run.ReviseTotal(sweep.PairsChecked, "the bounds sweep established the exact number of mesh queries");
ReportTimer("same", sameTimer, null);
ReportTimer("cross", crossTimer, null);
Console.WriteLine($"   pair-loop stage: {sweepSeconds:0.0}s for {sweep.PairsChecked:N0} pairs; same + cross pair wall "
    + $"{MeshQueryTimer.Seconds(sameTimer.WallTicks + crossTimer.WallTicks):0.0}s; BVH nodes built in the sweep "
    + $"{MeshIntersection.BvhNodesBuilt - bvhNodesBefore:N0} (process {MeshIntersection.BvhNodesBuilt:N0})");
__run.Feature("actual-shape-queries.stage.seconds", sweepSeconds, "seconds", FeatureAt.Final)
     .Feature("actual-shape-queries.bvh-nodes", MeshIntersection.BvhNodesBuilt - bvhNodesBefore, "nodes", FeatureAt.Final);
if (ledger is not null)
{
    ledger.Dispose();
    var slow = new[] { ("same", sameTimer), ("cross", crossTimer) }.ToDictionary(x => x.Item1, x => x.Item2.Slowest.Select(s => new
    {
        seconds = Math.Round(s.Seconds, 6), a = s.A, b = s.B, path = s.Path.ToString(), s.Query.Status, s.Query.Reason,
        s.Query.SurfaceRelation, s.Query.Containment, s.Query.TrianglePairsTested,
    }).ToList());
    File.WriteAllText(pairLedgerPath + ".slowest.json", JsonSerializer.Serialize(slow, new JsonSerializerOptions { WriteIndented = true }));
    __run.Artifact(pairLedgerPath!);
    Console.WriteLine($"   pair ledger: {sweep.PairsChecked:N0} line(s) -> {pairLedgerPath}");
}

// A published, ACTIVE patch mastering both sides is the community saying "these two are known to
// meet" — the pair is a LABELLED conflict, and its residual overlaps are what the patch did not
// reconcile. A pair with hits and NO reconciler anywhere in the order is the real shortlist.
// A patch is a plugin of the order: the BOS pseudo-plugin after them (--bos on) masters every plugin and patches nothing.
var patchMemo = new Dictionary<(int, int), bool>();
bool Reconciled(int a, int b)
{
    var k = a <= b ? (a, b) : (b, a);
    if (patchMemo.TryGetValue(k, out var hit)) return hit;
    bool found = false;
    for (int p = 0; p < paths.Count && !found; p++)
        if (p != a && p != b && masterIdx[p].Contains(a) && masterIdx[p].Contains(b)) found = true;
    return patchMemo[k] = found;
}

Console.WriteLine($"\npairs checked by actual strict render surfaces: {sweep.PairsChecked:N0}; tolerance {surfaceTolerance:R}, work cap {maxTrianglePairs:N0} triangle pairs, time cap {pairSeconds:R}s/pair");
Console.WriteLine($"\n==== SAME-RESPONSIBLE ACTUAL-SURFACE INTERSECTIONS ====");
Console.WriteLine($"   same-responsible pairs   : {sweep.SameChecked,10:N0} checked, {sweep.SameIntersects,7:N0} intersect, {sweep.SameUnknown,7:N0} unknown");
foreach (var (dom, v) in sweep.SameByDomain.OrderBy(k => k.Key))
    Console.WriteLine($"      {dom,-8}              : {v.Checked,10:N0} checked, {v.Intersects,7:N0} intersect, {v.Unknown,7:N0} unknown");
Console.WriteLine($"   declared-dependency pairs: {sweep.CrossDeclaredChecked,10:N0} checked, {sweep.CrossDeclaredIntersects,7:N0} intersect");

var undeclared = sweep.CrossHits.Where(h => !h.Declared).ToList();
Console.WriteLine($"\n==== CROSS-PLUGIN, NO DECLARED DEPENDENCY — ACTUAL SURFACES ====");
Console.WriteLine($"   pairs checked: {sweep.CrossUndeclaredChecked:N0}, intersections: {undeclared.Count:N0}, unknown: {sweep.CrossResults.Count(h => !h.Declared && h.Query.Unknown):N0}");
OverlapPopulation PopOf(SurfaceOverlapHit h) => DetectorModel.ClassifyOverlapPopulation(
    IsVanilla(h.A.Win.Plugin), IsVanilla(h.B.Win.Plugin), IsUssep(h.A.Win.Plugin), IsUssep(h.B.Win.Plugin));
Console.WriteLine("   relations: " + string.Join(", ", undeclared.GroupBy(h => h.Query.SurfaceRelation).OrderBy(g => g.Key).Select(g => $"{g.Key} {g.Count():N0}")));

__run.Stage("provenance", "rebuilding actual-surface intersection provenance", sweep.CrossHits.Count, "intersections");
var rows = new List<SurfaceOverlapRow>(sweep.CrossHits.Count);
foreach (var h in sweep.CrossHits)
{
    rows.Add(idx.JudgeSurface(h, closure, SurfaceGeometryOf, surfaceTolerance,
        maxTrianglePairs, TimeSpan.FromSeconds(pairSeconds), popMode == PopulationMode.Flags ? null : arms, default, provenanceTimer));
    __run.Advance();
}
ReportTimer("provenance", provenanceTimer, null);
var split = OverlapClass.Of(rows, PopOf);   // overlapprobe/OverlapClass.cs
var provenanceUnknown = split.ProvenanceUnknown;
var provenanceRows = split.Provenance;
int seenRows = rows.Count(r => r.Sight == OverlapSight.Seen);
Console.WriteLine($"\n==== PROVENANCE — actual surface with vs without the later plugin ({rows.Count:N0} intersections) ====");
Console.WriteLine($"   Seen rows {seenRows:N0}; declared intersections {sweep.CrossDeclaredIntersects:N0}; counterfactual unknown {provenanceUnknown.Count:N0}");

string Verdict(SurfaceOverlapRow r) => $"{r.Origin} + {r.Sight}";
var introduced = provenanceRows.Where(r => r.Origin == OverlapOrigin.Introduced).ToList();
Console.WriteLine($"   Introduced {introduced.Count:N0} of {provenanceRows.Count:N0} assessed; Blind {introduced.Count(r => r.Sight == OverlapSight.Blind):N0}");
var cls = split.Class;
var floorRows = split.Floor;

(string, string) PairKey(SurfaceOverlapRow r)
{
    var (x, y) = (PluginName(r.Later), PluginName(r.Earlier));
    return string.CompareOrdinal(x, y) <= 0 ? (x, y) : (y, x);
}
int DistinctRefs(IEnumerable<SurfaceOverlapRow> g) => g.SelectMany(r => new[] { r.Hit.A.Key, r.Hit.B.Key }).Distinct().Count();
string Describe(SurfaceOverlapRow r) =>
    $"{TypeOf(r.LaterRef.Base)} from {PluginName(r.Later)} / {TypeOf(r.OtherRef.Base)} from {PluginName(r.Earlier)}: "
  + $"{r.Hit.Query.SurfaceRelation}, containment {r.Hit.Query.Containment}, {r.Hit.Query.TrianglePairsTested:N0} triangle pairs"
  + $" in {CellOf(r.LaterRef.Win.Cell)} [{r.LaterRef.Src}{r.OtherRef.Src}] {r.LaterRef.Key} / {r.OtherRef.Key}";

Console.WriteLine($"\n==== THE CLASS — INTRODUCED + BLIND ACTUAL-SURFACE INTERSECTIONS: {cls.Count:N0} hits over {DistinctRefs(cls):N0} refs ====");
var byPair = cls.GroupBy(PairKey).OrderByDescending(g => g.Count()).ThenBy(g => g.Key.Item1, StringComparer.Ordinal).ToList();
foreach (var g in byPair.Take(listN))
{
    var first = g.OrderBy(r => r.Hit.A.Key.ToString(), StringComparer.Ordinal).ThenBy(r => r.Hit.B.Key.ToString(), StringComparer.Ordinal).First();
    Console.WriteLine($"   {g.Count(),5:N0} hits over {DistinctRefs(g),5:N0} refs {(Reconciled(first.Later, first.Earlier) ? "[patched, residual]" : "[NO RECONCILER]")} {g.Key.Item1} x {g.Key.Item2}");
    Console.WriteLine($"          first: {Describe(first)}");
}
Console.WriteLine($"\n   ---- first {listN} by canonical reference identity; relation is evidence, not severity ----");
foreach (var r in cls.OrderBy(r => r.Hit.A.Key.ToString(), StringComparer.Ordinal).ThenBy(r => r.Hit.B.Key.ToString(), StringComparer.Ordinal).Take(listN))
    Console.WriteLine("   " + Describe(r));

var knowing = split.Knowing;
var inherited = split.Inherited;
Console.WriteLine($"\nintroduced + seen intersections: {knowing.Count:N0}; inherited: {inherited.Count:N0}; Bethesda same-responsible relationships: {floorRows.Count:N0}");
foreach (var r in floorRows.OrderBy(r => r.Hit.A.Key.ToString(), StringComparer.Ordinal).Take(listN))
    Console.WriteLine($"   {Verdict(r),-22} {Describe(r)}");

// --emit: the CLASS only — Introduced + Blind, Bethesda floor withheld (the way navownprobe withholds
// its floor). The LATER plugin's ref is the necessary fix target (it placed into existing context);
// moving the other side is the legitimate alternative. Every record carries its physical predicate under this
// arm (OverlapPredicate, Holds by construction for a class row); with --base, every record of the named emit is
// re-measured under this arm too (2026-09-03).
if (emitPath is not null)
{
    __run.Stage("reconciliation", "remeasuring emitted and prior overlap predicates", total: null, unit: "predicates");
    var conflicts = new List<Conflict>();
    foreach (var r in cls)
    {
        var (st, why) = OverlapPredicate(r.LaterRef.Key, r.OtherRef.Key);
        conflicts.Add(new Conflict(
            "OVERLAP", PluginName(r.Earlier), PluginName(r.Later), Describe(r),
            new List<ConflictRecord> { new(r.LaterRef.Key.ToString(), "placed-ref", CellOf(r.LaterRef.Win.Cell), st, why) },
            new List<ConflictRecord> { new(r.OtherRef.Key.ToString(), "placed-ref", null) }));
        reconciliationPredicates++;
        __run.Advance();
    }
    // --reverse-of measured the --base rows before deciding it had to sweep: those are the rows, measured once.
    List<Predicate>? predicates = basePredicatesMeasured ?? (basePath is not null ? MeasureBasePredicates(basePath) : null);
    int notRebuilt = rows.Count(r => r.CounterfactualStatus == "unknown");
    var coverage = new List<CoverageLine>
    {
        PluginsLine(),
        BaseRecordsLine(),
        CandidateRefsLine(),
        new("cross-responsible actual-shape queries", sweep.CrossResults.Count - sweep.CrossUnknown,
            sweep.CrossUnknown == 0 ? new Dictionary<string, long>() : new Dictionary<string, long> { ["MeshQueryUnknown"] = sweep.CrossUnknown }, null,
            $"intersections {rows.Count:N0}; separated results remain measured and are not conflicts"),
        new("actual-surface intersection counterfactuals", rows.Count - notRebuilt,
            notRebuilt == 0 ? new Dictionary<string, long>() : new Dictionary<string, long> { ["CounterfactualGeometryUnmeasurable"] = notRebuilt }, null,
            $"class {cls.Count:N0}; unknown counterfactual rows have null provenance and are withheld from the class"),
    };
    if (PopulationScopeLine() is { } scopeLine) coverage.Add(scopeLine);
    var identity = cfg.Identity(populationIdentity, pop.StateName);
    ReportTimer("reconciliation", reconcileTimer, null);
    __run.ReviseTotal(reconciliationPredicates, "the current class and optional base emit established the exact predicate count");
    __run.Stage("emit-artifact", "writing overlap conflict artifact", 1, "artifacts");
    ConflictIo.Write(emitPath, new ConflictFile("overlapprobe", Instance,
        DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm:ssZ"), conflicts, coverage, predicates, basePath,
        populationIdentity, pop.StateName, identity, MeasurementPolicy: NifOverlapGeometry.SurfaceMeasurementIdentity(surfaceTolerance)));
    __run.Advance().Artifact(emitPath);
    Console.WriteLine($"configuration: {identity.Short}");
    Console.WriteLine($"\nemitted {conflicts.Count:N0} Introduced + Blind conflicts -> {emitPath}");
}
if (population is not null && populationAuditEmit is not null)
{
    // For each row of another emit (the whole-order run's), whether it is in this population under this order: the rule
    // the pair filter applies, on each reference's winning geometry pose.
    bool RefIn(string fk)
    {
        var key = FormKey.Factory(fk);
        var pose = idx.WinningGeometryPose(key) ?? idx.History.Winning(key);
        return population.KeyInScope(key, pose?.Cell ?? FormKey.Null);
    }
    var auditRows = new List<object>();
    foreach (var c in ConflictIo.Read(populationAuditEmit).Conflicts)
    {
        string? other = c.Alternative is { Count: > 0 } alt ? alt[0].FormKey : null;
        foreach (var rec in c.Necessary)
            auditRows.Add(new { identity = ConflictIo.RowIdentity(c.Kind, rec.FormKey, other, c.A), inScope = RefIn(rec.FormKey) || (other is not null && RefIn(other)) });
    }
    Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(populationAuditOut!))!);
    File.WriteAllText(populationAuditOut!, JsonSerializer.Serialize(new { probe = "overlapprobe", population = population.Identity(pop.Identity), emit = Path.GetFullPath(populationAuditEmit), rows = auditRows },
        new JsonSerializerOptions { WriteIndented = true }));
    Console.WriteLine($"population audit: {auditRows.Count:N0} row(s) of {Path.GetFileName(populationAuditEmit)} -> {populationAuditOut}");
}

if (jsonPath is not null)
{
    __run.Stage("json-artifact", "writing judged overlap rows", 1, "artifacts");
    var dir = Path.GetDirectoryName(Path.GetFullPath(jsonPath));
    if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
    var dump = rows.Select(r => new
    {
        Later = r.LaterRef.Key.ToString(), LaterPlugin = PluginName(r.Later), LaterType = TypeOf(r.LaterRef.Base), LaterMesh = MeshOf(r.LaterRef.Base), LaterSrc = r.LaterRef.Src.ToString(),
        Other = r.OtherRef.Key.ToString(), OtherPlugin = PluginName(r.Earlier), OtherType = TypeOf(r.OtherRef.Base), OtherMesh = MeshOf(r.OtherRef.Base), OtherSrc = r.OtherRef.Src.ToString(),
        Cell = CellOf(r.LaterRef.Win.Cell), Origin = r.Origin.ToString(), Sight = r.Sight.ToString(), Population = PopOf(r.Hit).ToString(),
        Declared = r.Hit.Declared, r.Hit.Query.Status, r.Hit.Query.Reason, r.Hit.Query.SurfaceRelation,
        r.Hit.Query.Containment, r.Hit.Query.Intersects, r.Hit.Query.TrianglePairsTested,
        Counterfactual = r.Counterfactual,
        r.CounterfactualStatus, r.CounterfactualReason,
        LaterPrevious = r.LaterPrevious is { } lp ? new { Plugin = PluginName(lp.Plugin), lp.Pos.X, lp.Pos.Y, lp.Pos.Z, Cell = lp.Cell.ToString(), lp.Disabled, lp.Deleted } : null,
        OtherAsSeenByLater = r.OtherAsSeenByLater is { } os ? new { Plugin = PluginName(os.Plugin), os.Pos.X, os.Pos.Y, os.Pos.Z } : null,
        Reconciled = Reconciled(r.Later, r.Earlier), r.Why,
    });
    File.WriteAllText(jsonPath, JsonSerializer.Serialize(dump, new JsonSerializerOptions { WriteIndented = false, NumberHandling = System.Text.Json.Serialization.JsonNumberHandling.AllowNamedFloatingPointLiterals }));
    __run.Advance().Artifact(jsonPath);
    Console.WriteLine($"wrote {rows.Count:N0} judged rows -> {jsonPath}");
}

if (geometryManifestPath is not null)
{
    __run.Stage("manifest-records", "assembling actual-shape manifest records", idx.History.Refs.Count(), "references");
    static ManifestBounds? BoundsArray(Aabb? b) => b is not { IsEmpty: false } box ? null
        : new(new[] { (double)box.Min.X, (double)box.Min.Y, (double)box.Min.Z },
              new[] { (double)box.Max.X, (double)box.Max.Y, (double)box.Max.Z });
    static double[] Dimensions(Aabb b) => new[] { (double)b.Max.X - b.Min.X,
                                                   (double)b.Max.Y - b.Min.Y,
                                                   (double)b.Max.Z - b.Min.Z };
    static double Volume(Aabb b) { var d = Dimensions(b); return d[0] * d[1] * d[2]; }
    static string PairWire(FormKey a, FormKey b)
    {
        string x = a.ToString(), y = b.ToString();
        return string.CompareOrdinal(x, y) <= 0 ? x + "||" + y : y + "||" + x;
    }

    var historicalPairs = historicalCrosswalk?.Pairs?.ToList() ?? new List<HistoricalPair>();
    var retainedHistoricalKeys = historicalPairs.Select(p => p.PairKey).ToHashSet(StringComparer.Ordinal);
    foreach (var pairKey in importedSceneScopes.SelectMany(s => s.PairKeys).Distinct(StringComparer.Ordinal))
    {
        var halves = pairKey.Split("||", StringSplitOptions.None);
        if (halves.Length == 2 && retainedHistoricalKeys.Add(pairKey))
            historicalPairs.Add(new HistoricalPair("scene-scope:" + pairKey, pairKey,
                ConflictIo.RowIdentity("OVERLAP", halves[0], halves[1], ""), halves[0], halves[1]));
    }
    var historicalRefKeys = new HashSet<FormKey>();
    try
    {
        foreach (var hp in historicalPairs) { historicalRefKeys.Add(FormKey.Factory(hp.A)); historicalRefKeys.Add(FormKey.Factory(hp.B)); }
    }
    catch (Exception ex) { Console.Error.WriteLine($"--historical-pairs contains an invalid FormKey: {ex.Message}"); return 2; }
    var reviewByKey = new Dictionary<FormKey, ReviewRef>();
    var recordRows = new List<object>();
    var reachRows = new List<(int Row, object Reach)>();
    var reachLooseRows = new List<object>();
    // Which identities this loop actually enumerated. The accounting below reads it rather than
    // re-deriving the loop's own gates, so it can never claim a row the manifest does not hold.
    var emittedRecordKeys = new HashSet<FormKey>();
    foreach (var (key, _) in idx.History.Refs.OrderBy(k => k.Key.ToString(), StringComparer.Ordinal))
    {
        __run.Advance();
        var pose = idx.WinningGeometryPose(key);
        if (pose is null || (sceneCapture
            ? !sceneIncludedKeys.Contains(key)
            : !scopedResponsiblePlugins.Contains(pose.Value.Plugin) && !historicalRefKeys.Contains(key))) continue;
        var p = pose.Value;
        bool deleted = p.Deleted || idx.IsDeletedNow(key);
        EnableResolution? enable = popMode == PopulationMode.Flags ? null : arms.Winner(key);
        bool enabled = popMode == PopulationMode.Flags ? !p.Disabled : enable!.Value.Enabled;
        bool enableResolved = popMode == PopulationMode.Flags || enable!.Value.Resolved;
        bool hasBase = idx.RefBase.TryGetValue(key, out var effectiveBase);
        GeometryRead? geometry = hasBase ? GeometryOf(effectiveBase) : null;
        bool geometryKnown = geometry?.Outcome is { UsableForSurfaceIntersection: true, Soup: { TriangleCount: > 0 } };
        Aabb? modelBounds = geometryKnown ? geometry!.Outcome!.ModelBounds : null;
        TriangleSoup? worldSoup = geometryKnown
            ? SpatialOverlap.TransformSoup(geometry!.Outcome!.Soup!, p.Pos, p.Rot, p.Scale)
            : null;
        Aabb? worldBounds = worldSoup is null ? null : SpatialOverlap.SoupBounds(worldSoup);
        bool zeroScale = p.Scale == 0;
        bool worldGeometryKnown = !zeroScale && worldBounds is { IsEmpty: false } wb0 && SpatialOverlap.IsFinite(wb0);
        bool bosUnknown = idx.BosSwaps.TryGetValue(key, out var bosResolution)
                       && (bosResolution.Random || bosResolution.PerSession || bosResolution.PropertiesRandom || bosResolution.Alternatives.Count != 1);

        string eligibility, eligibilityReason;
        if (deleted) { eligibility = "state_excluded"; eligibilityReason = "the winning record is deleted"; }
        else if (!enableResolved) { eligibility = "state_unresolved"; eligibilityReason = "the enable-parent chain did not resolve"; }
        else if (!enabled) { eligibility = "state_excluded"; eligibilityReason = "the reference is absent in the selected state"; }
        else if (bosUnknown) { eligibility = "bos_unresolved"; eligibilityReason = "BOS has more than one possible runtime outcome"; }
        else if (!hasBase) { eligibility = "source_excluded"; eligibilityReason = "the winning reference has no base record"; }
        else if (!idx.BaseInfo.TryGetValue(effectiveBase, out var bi) || !Obstructing.Contains(bi.Type))
        { eligibility = "type_excluded"; eligibilityReason = "the winning base is not an obstructing record type"; }
        else if (idx.BaseIsMarker.Contains(effectiveBase)
              || (idx.BaseMesh.TryGetValue(effectiveBase, out var markerMesh) && Path.GetFileName(markerMesh).StartsWith("MarkerX", StringComparison.OrdinalIgnoreCase)))
        { eligibility = "marker_excluded"; eligibilityReason = "the winning base is an editor marker"; }
        else if (!geometryKnown) { eligibility = "geometry_unmeasurable"; eligibilityReason = geometry?.Reason ?? "no complete strict render triangles"; }
        else if (zeroScale)
        { eligibility = "degenerate_geometry"; eligibilityReason = "the placed reference has zero scale"; }
        else if (!worldGeometryKnown)
        { eligibility = "invalid_transform"; eligibilityReason = "placed render triangles have no finite bounds"; }
        else { eligibility = "eligible"; eligibilityReason = "passes the discovery record gates"; }

        bool runtimePoseKnown = !deleted && enableResolved && enabled && !bosUnknown;
        Aabb? reportedWorldBounds = runtimePoseKnown && worldGeometryKnown ? worldBounds : null;
        // This is the placed surface's availability, not a classification of its diagnostic AABB.
        // In particular, a valid flat triangle sheet is render-ready even though the older bounds reader
        // describes its zero-volume box as degenerate. Source failures must also survive unchanged rather
        // than being relabelled as transform failures merely because there was no soup to transform.
        string reportedGeometryStatus = !runtimePoseKnown ? eligibility
            : !hasBase ? eligibility
            : !geometryKnown ? geometry?.Status ?? eligibility
            : zeroScale ? "degenerate_geometry"
            : !worldGeometryKnown ? "invalid_transform"
            : "measured";
        string? reportedGeometryReason = !runtimePoseKnown ? eligibilityReason
            : !hasBase ? eligibilityReason
            : !geometryKnown ? geometry?.Reason ?? eligibilityReason
            : !worldGeometryKnown ? eligibilityReason
            : null;
        reviewByKey[key] = new ReviewRef(key, eligibility, eligibilityReason, reportedGeometryStatus,
                                         reportedGeometryReason, hasBase ? effectiveBase : null, p, reportedWorldBounds,
                                         runtimePoseKnown && worldGeometryKnown ? worldSoup : null,
                                         runtimePoseKnown && worldGeometryKnown ? geometry!.Outcome!.Soup : null);

        idx.Touchers.TryGetValue(key, out var touchers);
        string winner = touchers is { Count: > 0 } ? PluginName(touchers[^1]) : PluginName(p.Plugin);
        var recordBase = idx.RecordBase.TryGetValue(key, out var rb) ? rb : hasBase ? effectiveBase : FormKey.Null;
        object bos = !bosOn ? new { status = "off", resolution = (object?)null }
            : bosResolution is null ? new { status = "unchanged", resolution = (object?)null }
            : new
            {
                status = bosUnknown ? "unresolved" : bosResolution.Swapped || bosResolution.HasTransform || bosResolution.SetsFlags ? "resolved" : "unchanged",
                resolution = (object?)new
                {
                    effectiveBase = bosResolution.EffectiveBase.ToString(), bosResolution.Swapped, bosResolution.Random,
                    bosResolution.Leveled, bosResolution.PerSession, bosResolution.PropertiesRandom,
                    alternatives = bosResolution.Alternatives.Select(a => new { @base = a.Base.ToString(), a.Probability }).ToArray(),
                    bosResolution.PropertiesSource, notes = bosResolution.Notes
                }
            };
        emittedRecordKeys.Add(key);
        int rowIndex = recordRows.Count;
        recordRows.Add(new
        {
            formid = key.ToString(),
            type = idx.RefArm.GetValueOrDefault(key, "placed-ref"),
            editorid = idx.RefEditorId.GetValueOrDefault(key),
            name = (string?)null,
            winner,
            fields = new
            {
                responsiblePlugin = PluginName(p.Plugin),
                effectiveBase = !hasBase ? null : new
                {
                    formid = effectiveBase.ToString(),
                    type = idx.BaseInfo.TryGetValue(effectiveBase, out var effectiveInfo) ? effectiveInfo.Type : null,
                    editorid = idx.BaseInfo.TryGetValue(effectiveBase, out effectiveInfo) ? effectiveInfo.Edid : null,
                    name = (string?)null
                },
                recordBase = recordBase.IsNull ? null : new
                {
                    formid = recordBase.ToString(),
                    type = idx.BaseInfo.TryGetValue(recordBase, out var recordInfo) ? recordInfo.Type : null,
                    editorid = idx.BaseInfo.TryGetValue(recordBase, out recordInfo) ? recordInfo.Edid : null,
                    name = (string?)null
                },
                cell = new
                {
                    formid = p.Cell.ToString(),
                    editorid = idx.CellName.TryGetValue(p.Cell, out var cellInfo) ? cellInfo.Edid : null,
                    interior = idx.CellName.TryGetValue(p.Cell, out cellInfo) && cellInfo.Interior
                },
                space = p.Space.ToString(),
                position = new[] { (double)p.Pos.X, (double)p.Pos.Y, (double)p.Pos.Z },
                rotation = new[] { (double)p.Rot.X, (double)p.Rot.Y, (double)p.Rot.Z },
                scale = (double)p.Scale, deletion = deleted,
                resolvedEnable = arms.IsForced(key)
                    ? (object)new { enabled, resolved = enableResolved, population = pop.Identity, state = pop.StateName, forced = EnableArms.ForcedLabel }
                    : new { enabled, resolved = enableResolved, population = pop.Identity, state = pop.StateName },
                bos
            },
            eligibility = new { status = eligibility, reason = eligibilityReason },
            geometry = new
            {
                assetId = geometry?.AssetId, status = reportedGeometryStatus, reason = reportedGeometryReason,
                modelBounds = BoundsArray(modelBounds), worldBounds = BoundsArray(reportedWorldBounds),
                dimensions = reportedWorldBounds is { } wb ? Dimensions(wb) : null,
                volume = reportedWorldBounds is { } vb ? Volume(vb) : (double?)null
            }
        });
        if (reachOn && runtimePoseKnown && hasBase && reportedWorldBounds is null)
            reachRows.Add((rowIndex, ReachOf(key, effectiveBase, p)));
        // --reach-rules: loose items (type_excluded item records) get the same region in a separate report; nothing in their
        // record row changes
        if (reachOn && reachRules is not null && runtimePoseKnown && hasBase && eligibility == "type_excluded"
            && idx.BaseInfo.TryGetValue(effectiveBase, out var looseInfo)
            && LooseItems.IsLooseBase(looseInfo.Type, idx.LightCarryable.Contains(effectiveBase), reachRules.Contains("rest")))
            reachLooseRows.Add(new { formid = key.ToString(), baseType = looseInfo.Type, reach = ReachOf(key, effectiveBase, p) });
        // --reach-rules rest: a render-boxed MoveableStatic whose model has a free body settles at load like a loose item (lab:
        // three MoveableStatic clutter bodies, flags 0, settled at load), so its row carries its drop as well as its box
        if (reachOn && reachRules?.Contains("rest") == true && runtimePoseKnown && hasBase && reportedWorldBounds is not null
            && idx.BaseInfo.TryGetValue(effectiveBase, out var msInfo) && msInfo.Type == "MoveableStatic" && HasFreeBody(effectiveBase))
            reachRows.Add((rowIndex, ReachOf(key, effectiveBase, p)));
    }

    var manifestJson = new JsonSerializerOptions { WriteIndented = true, NumberHandling = System.Text.Json.Serialization.JsonNumberHandling.AllowNamedFloatingPointLiterals };
    // --reach: attach the reach object to the rows it was computed for (nothing else in a row changes)
    foreach (var (rowAt, reachObj) in reachRows)
    {
        var node = JsonSerializer.SerializeToNode(recordRows[rowAt], recordRows[rowAt].GetType(), manifestJson)!.AsObject();
        node["reach"] = JsonSerializer.SerializeToNode(reachObj, reachObj.GetType(), manifestJson);
        recordRows[rowAt] = node;
    }
    // E1 (follow-ups item 1): an asset with a visible Tree_Anim shape gains "wind" (NifWind.Json); nothing else changes
    object WithWind(object row, WindLabel? wind)
    {
        if (wind is null) return row;
        var node = JsonSerializer.SerializeToNode(row, row.GetType(), manifestJson)!.AsObject();
        node["wind"] = JsonSerializer.SerializeToNode(NifWind.Json(wind), manifestJson);
        return node;
    }
    var assetGeoms = geometryByPath.Values.OrderBy(g => g.RelativePath, StringComparer.OrdinalIgnoreCase).ToList();
    var assetRows = assetGeoms.Select(g => new
    {
        assetId = g.AssetId,
        relativePath = g.RelativePath,
        resolutionStatus = g.Status,
        reason = g.Reason,
        winner = g.Source is null ? null : new
        {
            providerName = g.Source.ProviderName,
            kind = g.Source.Kind.ToString().ToLowerInvariant(),
            looseFilePath = g.Source.LooseFilePath,
            archivePath = g.Source.ArchivePath,
            archiveEntry = g.Source.EntryPath
        },
        providers = g.Resolution?.Sources.Select(s => new
        {
            providerName = s.ProviderName, kind = s.Kind.ToString().ToLowerInvariant(),
            looseFilePath = s.LooseFilePath, archivePath = s.ArchivePath, archiveEntry = s.EntryPath
        }).ToArray() ?? Array.Empty<object>(),
        ambiguity = g.Resolution?.Ambiguous ?? false,
        readIncomplete = g.Resolution?.ReadIncomplete ?? assets.ReadIncomplete,
        consumedSha256 = g.Outcome?.ContentSha256,
        byteCount = g.ByteCount,
        geometryStatus = g.Status,
        modelBounds = BoundsArray(g.Outcome is { Complete: true } ? g.Outcome.ModelBounds : null),
        totalVertices = g.Outcome?.Model?.TotalVertices,
        totalTriangles = g.Outcome?.Model?.TotalTriangles,
        visibleShapeCount = g.Outcome?.Model?.Shapes.Count(s => !s.Hidden),
        decodedVisibleShapeCount = g.Outcome?.Model?.Shapes.Count(s => !s.Hidden && s.VerticesRead && !s.Bounds.IsEmpty)
    }).ToList();

    ManifestMeasurements PairMeasurements(ReviewRef? a, ReviewRef? b)
    {
        var ab = new ManifestBoxMeasure(a?.GeometryStatus ?? "missing_reference", a?.GeometryReason,
            a?.WorldBounds is { } aw ? Dimensions(aw) : null, a?.WorldBounds is { } av ? Volume(av) : null);
        var bb = new ManifestBoxMeasure(b?.GeometryStatus ?? "missing_reference", b?.GeometryReason,
            b?.WorldBounds is { } bw ? Dimensions(bw) : null, b?.WorldBounds is { } bv ? Volume(bv) : null);
        if (a?.WorldBounds is not { } aworld || b?.WorldBounds is not { } bworld)
            return new("unmeasurable", a is null || b is null ? "a historical reference is absent from the evaluated order" : "at least one reference has no finite eligible world bounds",
                       ab, bb, null, null, null, null, null, null);
        var m = SpatialOverlap.MeasureAabbOverlap(aworld, bworld);
        var extents = new[] {
            Math.Max(0d, Math.Min((double)aworld.Max.X, bworld.Max.X) - Math.Max((double)aworld.Min.X, bworld.Min.X)),
            Math.Max(0d, Math.Min((double)aworld.Max.Y, bworld.Max.Y) - Math.Max((double)aworld.Min.Y, bworld.Min.Y)),
            Math.Max(0d, Math.Min((double)aworld.Max.Z, bworld.Max.Z) - Math.Max((double)aworld.Min.Z, bworld.Min.Z)) };
        return new("measured", null, ab, bb, extents, m.IntersectionVolume, m.IntersectionAreaXy,
                   m.FractionOfSmaller, m.FractionReason, new(m.ZRelation, m.SignedZIntersection, m.ZGap));
    }
    static string PairEligibilityReason(ReviewRef? a, ReviewRef? b) =>
        a is null || b is null ? "a historical reference is absent from the evaluated order"
      : a.Eligibility != "eligible" ? a.EligibilityReason
      : b.Eligibility != "eligible" ? b.EligibilityReason
      : "both records pass discovery gates";

    var freshSceneResults = sweep.CrossResults.Concat(sweep.SameResults).ToList();
    var freshByKey = freshSceneResults.ToDictionary(x => PairWire(x.A.Key, x.B.Key), StringComparer.Ordinal);
    var directQueryByKey = new Dictionary<string, SurfaceQueryOutcome>(StringComparer.Ordinal);
    SurfaceQueryOutcome ActualShape(string key, ReviewRef? a, ReviewRef? b)
    {
        if (freshByKey.TryGetValue(key, out var fresh)) return fresh.Query;
        if (directQueryByKey.TryGetValue(key, out var memo)) return memo;
        SurfaceQueryOutcome result;
        if (a is null || b is null)
            result = new("unknown", "a historical reference is absent from the evaluated order", "unknown", "unknown", null, 0);
        else if (a.Eligibility != "eligible" || b.Eligibility != "eligible")
            result = new("unknown", PairEligibilityReason(a, b), "unknown", "unknown", null, 0);
        else if (a.Pose.Space != b.Pose.Space)
            result = new("measured", "references occupy different coordinate spaces", "separated", "none", false, 0);
        else if (a.WorldSoup is not { TriangleCount: > 0 } sa || b.WorldSoup is not { TriangleCount: > 0 } sb)
            result = new("unknown", "at least one reference has no complete placed render triangles", "unknown", "unknown", null, 0);
        else
            result = SurfaceQueryOutcome.QueryPlaced(sa, a.Placed, sb, b.Placed, surfaceTolerance, maxTrianglePairs, TimeSpan.FromSeconds(pairSeconds));
        return directQueryByKey[key] = result;
    }
    static ManifestStatus Discovery(SurfaceQueryOutcome q) => q.Intersects switch
    {
        true => new("actual_surface_intersection", $"{q.SurfaceRelation}; containment {q.Containment}"),
        false => new("actual_surface_separated", $"surface relation {q.SurfaceRelation}; containment {q.Containment}"),
        _ => new("actual_surface_unknown", q.Reason ?? "mesh predicate returned unknown")
    };
    static ManifestActualShape ShapeWire(SurfaceQueryOutcome q) =>
        new(q.Status, q.Reason, q.SurfaceRelation, q.Containment, q.Intersects, q.TrianglePairsTested) { frame = q.Frame, zeroAreaSkipped = q.ZeroAreaSkipped };
    var narrowByKey = rows.ToDictionary(r => PairWire(r.Hit.A.Key, r.Hit.B.Key), r => new ManifestNarrowVerdict(
        "measured", r.Origin?.ToString().ToLowerInvariant(), r.Sight.ToString().ToLowerInvariant(), r.Why,
        r.CounterfactualStatus, r.CounterfactualReason), StringComparer.Ordinal);
    ManifestNarrowVerdict Narrow(string key) => narrowByKey.TryGetValue(key, out var n) ? n
        : new("unavailable", null, null, "Q46 narrow provenance is available only for current cross-responsible intersections", null, null);
    MeshContactEvidence ContactEvidence(string key, ReviewRef? a, ReviewRef? b)
    {
        if (!contactEvidenceKeys.Contains(key)) return new("unmeasured", "pair was not nominated by --contact-evidence-pairs", null, null, 0, MeshIntersection.ContactEvidencePolicyVersion);
        if (a?.WorldSoup is not { TriangleCount: > 0 } sa || b?.WorldSoup is not { TriangleCount: > 0 } sb)
            return new("unknown", "a nominated endpoint has no complete placed render triangles", null, null, 0, MeshIntersection.ContactEvidencePolicyVersion);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(pairSeconds));
        return MeshIntersection.MeasureContactEvidencePlaced(sa, a.Placed?.Member, sb, b.Placed?.Member, surfaceTolerance, maxTrianglePairs, deadline.Token);
    }

    var pairRows = new List<ManifestPair>();
    var historicalGroups = historicalPairs.GroupBy(p => p.PairKey, StringComparer.Ordinal).ToList();
    var historicalKeys = historicalGroups.Select(g => g.Key).ToHashSet(StringComparer.Ordinal);
    __run.Stage("manifest-historical-shape", "resolving historical pairs against current actual surfaces", historicalGroups.Count, "pairs");
    foreach (var group in historicalGroups)
    {
        var first = group.First();
        var ak = FormKey.Factory(first.A); var bk = FormKey.Factory(first.B);
        reviewByKey.TryGetValue(ak, out var ar); reviewByKey.TryGetValue(bk, out var br);
        var actual = ActualShape(group.Key, ar, br);
        pairRows.Add(new(group.Key, first.ConflictIoRowIdentity, first.A, first.B,
            group.Select(p => p.HistoricalRowId).ToArray(), false, Discovery(actual),
            new(ar?.Eligibility == "eligible" && br?.Eligibility == "eligible" ? "eligible" : "ineligible",
                PairEligibilityReason(ar, br)), ShapeWire(actual), PairMeasurements(ar, br), Narrow(group.Key), ContactEvidence(group.Key, ar, br)));
        __run.Advance();
    }
    foreach (var fresh in freshSceneResults.Where(x => (x.Query.Intersects == true || x.Query.Unknown)
             && !historicalKeys.Contains(PairWire(x.A.Key, x.B.Key))))
    {
        string key = PairWire(fresh.A.Key, fresh.B.Key);
        var ar = reviewByKey[fresh.A.Key]; var br = reviewByKey[fresh.B.Key];
        pairRows.Add(new(key, ConflictIo.RowIdentity("OVERLAP", fresh.A.Key.ToString(), fresh.B.Key.ToString(), ""),
            fresh.A.Key.ToString(), fresh.B.Key.ToString(), Array.Empty<string>(), true,
            Discovery(fresh.Query), new("eligible", "both records pass source, state, type and strict render-geometry gates"),
            ShapeWire(fresh.Query), PairMeasurements(ar, br), Narrow(key), ContactEvidence(key, ar, br)));
    }
    pairRows = pairRows.OrderBy(p => p.pairKey, StringComparer.Ordinal).ToList();

    var manifestIdentity = cfg.Identity(pop.Identity, pop.StateName);
    var seedDefinitionRows = sceneSeedPoses.DistinctBy(s => (s.Reference, s.Cell, s.Space, s.X, s.Y, s.Z,
            MinX: s.WorldBounds?.Min.X, MinY: s.WorldBounds?.Min.Y, MinZ: s.WorldBounds?.Min.Z,
            MaxX: s.WorldBounds?.Max.X, MaxY: s.WorldBounds?.Max.Y, MaxZ: s.WorldBounds?.Max.Z,
            s.BoundsReason, s.Provenance, s.IsImported))
        .OrderBy(s => s.Reference.ToString(), StringComparer.Ordinal).ThenBy(s => s.Provenance, StringComparer.Ordinal)
        .Select(seed =>
        {
            var measured = sceneSeedBounds.LastOrDefault(b => b.Reference == seed.Reference && b.Source == "current");
            Aabb? bounds = seed.WorldBounds ?? (seed.IsImported || measured.Reference.IsNull || measured.Bounds.IsEmpty ? null : measured.Bounds);
            return new
            {
                formid = seed.Reference.ToString(), cell = seed.Cell.ToString(), space = seed.Space.ToString(),
                position = new[] { seed.X, seed.Y, seed.Z }, interior = seed.Interior,
                worldBounds = BoundsArray(bounds),
                boundsReason = bounds is null ? seed.BoundsReason ?? "seed has no finite placed world bounds" : null,
                provenance = seed.WorldBounds is not null ? seed.Provenance : measured.Reference.IsNull ? seed.Provenance : measured.Source
            };
        }).ToArray();
    // Every imported identity, classified exactly once against what this capture resolved for it.
    var importedAccounting = ImportedReferenceAccounting.Partition(
        importedSceneReferences, emittedRecordKeys,
        hasHistory: k => idx.History.Of(k) is not null,
        hasWinningPose: k => idx.WinningGeometryPose(k) is not null);

    var manifest = new
    {
        schemaVersion = "nif-overlap-actual-shape-manifest-v2",
        readerVersion = NifOverlapGeometry.ReaderVersion,
        policyVersion = NifOverlapGeometry.SurfaceMeasurementIdentity(surfaceTolerance),
        job = "nif-overlap-actual-shape",
        scope = new
        {
            responsiblePairs = responsiblePairs.Select(p => new[] { p.AName, p.BName }).ToArray(),
            population = pop.Identity, state = pop.StateName, bos = bosOn ? "on" : "off",
            historicalPairs = historicalPairsPath is null ? null : new { path = Path.GetFullPath(historicalPairsPath), sha256 = historicalCrosswalkSha256 },
            contactEvidencePairs = contactEvidencePairsPath is null ? null : new { path = Path.GetFullPath(contactEvidencePairsPath), sha256 = contactEvidencePairsSha256, count = contactEvidenceKeys.Count },
            predicate = new { tolerance = surfaceTolerance, maxTrianglePairs, pairSeconds, policyVersion = MeshIntersection.PolicyVersion },
            configuration = manifestIdentity
        },
        scene = !sceneCapture ? null : new
        {
            sceneId,
            role = sceneRole ?? "unspecified",
            candidatePlugins = candidatePluginNames.Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(n => n, StringComparer.OrdinalIgnoreCase).ToArray(),
            candidatePluginsPresent = candidatePluginNames.Where(pluginByName.ContainsKey).Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(n => n, StringComparer.OrdinalIgnoreCase).ToArray(),
            candidatePluginsAbsent = absentCandidatePlugins,
            candidateMods = candidateModNames.Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(n => n, StringComparer.OrdinalIgnoreCase).ToArray(),
            candidateModsPresent = candidateMods.OrderBy(n => n, StringComparer.OrdinalIgnoreCase).ToArray(),
            candidateModsAbsent = absentCandidateMods,
            assetPreflight = assetPreflight is null ? null : new
            {
                status = "passed",
                candidate = assetPreflight.CandidatePath,
                baseProfile = assetPreflight.BaseProfile,
                selectedProviderCount = assetPreflight.Providers.Count,
                profileProviderCount = assetPreflight.ProfileProviderCount,
                omittedProviderCount = assetPreflight.OmittedProviderCount,
                subsetPolicy = "the candidate Mods list intentionally selects a subset; every selected provider must match an MO2 name, and the candidate's Mods order is the asset priority read, strongest first",
                reorderedProviders = assetPreflight.ReorderedProviders,
                providers = assetPreflight.Providers.Select(p => new { candidateName = p.CandidateName, mo2Name = p.Mo2Name,
                    candidateRank = p.CandidateRank, mo2Priority = p.Mo2Priority, enabledInBaseProfile = p.Enabled, directory = p.Directory }).ToArray()
            },
            discovery = new
            {
                method = "candidate-effective-bounded-neighbourhood-v2",
                actualBoundsContextMargin = SceneCapture.ExteriorContextMargin,
                cells = sceneCells.Select(s => s.ToString()).OrderBy(s => s, StringComparer.Ordinal).ToArray(),
                seedReferences = sceneReferenceSeeds.Select(s => s.ToString()).OrderBy(s => s, StringComparer.Ordinal).ToArray(),
                seedDefinitions = seedDefinitionRows,
                importedScopes = importedSceneScopes.Select(s => new { path = s.Path, sha256 = s.Sha256,
                    referenceCount = s.References.Count, pairCount = s.PairKeys.Count, cellCount = s.Cells.Count,
                    declaredCellCount = s.DeclaredCells.Count }).ToArray(),
                importedDeclaredCells = importedDeclaredCells.Select(c => c.ToString()).OrderBy(c => c, StringComparer.Ordinal).ToArray(),
                // Imported cells whose owning plugin this setup neither loads nor masters: they do not exist here.
                absentDeclaredCells = absentDeclaredCells.Select(DeclaredCellPresence.AbsentRow).ToArray(),
                // Unchanged in meaning: the imported identities the evaluated order has no winner
                // history for. DERIVED from the partition below rather than recomputed beside it, so
                // the two can no longer answer differently for the same reference.
                missingImportedReferences = importedAccounting.WithDisposition(ImportedReferenceAccounting.NoPlacementHistory).ToArray(),
                // SCHEMA ADDITION: every imported identity's disposition, so an identity that is
                // neither enumerated nor historyless is stated instead of falling through both.
                importedReferenceAccounting = ImportedReferenceAccounting.Receipt(importedAccounting),
                seedBounds = sceneSeedBounds.Select(s => new { reference = s.Reference.ToString(), space = s.Space.ToString(),
                    min = new[] { (double)s.Bounds.Min.X, s.Bounds.Min.Y, s.Bounds.Min.Z }, max = new[] { (double)s.Bounds.Max.X, s.Bounds.Max.Y, s.Bounds.Max.Z }, source = s.Source }).ToArray(),
                unknownSeedBounds = unknownSceneSeedBounds,
                scopeUnknownRecords = sceneUnknownScope.OrderBy(k => k.Key.ToString(), StringComparer.Ordinal)
                    .Select(k => new { formid = k.Key.ToString(), reason = k.Value }).ToArray(),
                exteriorMembershipUnresolved = sceneUnlocalizedExterior.OrderBy(k => k.Key.ToString(), StringComparer.Ordinal)
                    .Select(k => new { formid = k.Key.ToString(), reason = k.Value }).ToArray(),
                exteriorOutsideOriginNeighbourhood = sceneOutsideExterior.OrderBy(k => k.Key.ToString(), StringComparer.Ordinal)
                    .Select(k => new { formid = k.Key.ToString(), reason = k.Value }).ToArray(),
                knownSpatialMemberCount = candidates.Count,
                unresolvedSpatialMemberCount = sceneUnknownScope.Count,
                unresolvedExteriorMembershipCount = sceneUnlocalizedExterior.Count,
                outsideOriginNeighbourhoodUnassessedCount = sceneOutsideExterior.Count,
                retainedPopulation = "all eligible obstructing references whose placed NIF bounds meet the existing fixed seed envelope plus 4096-unit context margin, plus geometry-unknown references whose placed origins lie in that bounded neighbourhood; distant-origin and unresolved exterior identities are retained separately as physically unassessed and never expanded to the whole worldspace"
            },
            validity = new
            {
                status = unreadablePlugins.Count == 0 && !assets.ReadIncomplete ? "valid" : "unresolved",
                reasons = unreadablePlugins.Select(p => $"plugin unreadable: {p}")
                    .Concat(assets.BsaFailures.Select(p => $"archive unreadable: {p}" )).ToArray()
            }
        },
        total = recordRows.Count,
        complete = unreadablePlugins.Count == 0 && !assets.ReadIncomplete && sceneUnknownScope.Count == 0
            && unknownSceneSeedBounds.Count == 0 && sceneUnlocalizedExterior.Count == 0 && sceneOutsideExterior.Count == 0
            && pairRows.All(p => p.actualShape.intersects is not null),
        notes = new[]
        {
            "Actual-shape results come from symmetric intersection of placed strict decoded render triangles; record ObjectBounds and stored NIF spheres have zero influence.",
            "Bounds enclose the placed triangle soups and reject only provably separated geometry; their overlap magnitudes are diagnostics and never discovery or severity gates.",
            "LOD shapes are conservatively included; statically hidden shapes are excluded; unsupported live poses and partial reads are unmeasurable.",
            sceneCapture
                ? "The scene run resolves the full order, winner history, selected state and BOS layer, then applies its recorded fixed seed geometry and context scope."
                : "The scoped run resolves the full order, winner history, selected state and BOS layer before restricting candidates to the named responsible pair."
        },
        records = recordRows,
        assets = assetRows.Select((a, i) => WithWind(a, assetGeoms[i].Wind)).ToList(),
        pairs = pairRows,
        measurementAccounting = new
        {
            recordCount = recordRows.Count, pairCount = pairRows.Count,
            historicalRowCount = historicalPairs.Count, historicalPairCount = historicalGroups.Count,
            duplicateHistoricalRowCount = historicalPairs.Count - historicalGroups.Count,
            newCandidatePairCount = pairRows.Count(p => p.newCandidate),
            measuredPairCount = pairRows.Count(p => p.actualShape.intersects is not null),
            intersectingPairCount = pairRows.Count(p => p.actualShape.intersects == true),
            separatedPairCount = pairRows.Count(p => p.actualShape.intersects == false),
            unknownPairCount = pairRows.Count(p => p.actualShape.intersects is null),
            relationCounts = pairRows.GroupBy(p => p.actualShape.surfaceRelation).ToDictionary(g => g.Key, g => g.Count(), StringComparer.Ordinal),
            containmentCounts = pairRows.GroupBy(p => p.actualShape.containment).ToDictionary(g => g.Key, g => g.Count(), StringComparer.Ordinal)
        },
        workload = new
        {
            consumedBytes = assetRows.Sum(a => a.byteCount ?? 0L),
            decodedVertices = assetRows.Sum(a => (long)(a.totalVertices ?? 0)),
            decodedTriangles = assetRows.Sum(a => (long)(a.totalTriangles ?? 0)),
            cacheHits = geometryLookupHits + pathCacheHits + renderCache.Hits,
            cacheMisses = renderCache.Misses,
            baseCacheHits = geometryLookupHits,
            pathCacheHits,
            contentCacheHits = renderCache.Hits,
            trianglePairsTested = pairRows.Sum(p => p.actualShape.trianglePairsTested)
        },
        limitations = new { unreadablePlugins, archiveReadFailures = assets.BsaFailures,
            sceneDiscovery = !sceneCapture ? null : $"The declared scene uses the pre-existing 4096-unit search envelope around explicit/candidate/imported seed bounds; this correction adds no new distance weight. Every eligible placed mesh in each selected coordinate space was bounds-tested; geometry-unknown references are admitted only when their placed origin lies in that envelope. {sceneUnknownScope.Count} are scoped unknown sources, {sceneOutsideExterior.Count} have distant origins but unavailable extents, and {sceneUnlocalizedExterior.Count} lack a localizing seed footprint. Both excluded groups retain identity as physically unassessed, force incomplete coverage, and never expand to the whole worldspace. --candidate-mod seeds winning NIF providers and attributable BOS property sources; texture-only material changes and runtime asset selection beyond those layers require an imported/explicit seed scope." }
    };
    var manifestDir = Path.GetDirectoryName(Path.GetFullPath(geometryManifestPath));
    if (!string.IsNullOrEmpty(manifestDir)) Directory.CreateDirectory(manifestDir);
    __run.Feature("nif_bytes", manifest.workload.consumedBytes, "bytes", FeatureAt.Final)
         .Feature("nif_vertices", manifest.workload.decodedVertices, "vertices", FeatureAt.Final)
         .Feature("nif_triangles", manifest.workload.decodedTriangles, "triangles", FeatureAt.Final)
         .Feature("nif_cache_hits", manifest.workload.cacheHits, "lookups", FeatureAt.Final)
         .Feature("nif_cache_misses", manifest.workload.cacheMisses, "lookups", FeatureAt.Final);
    __run.Stage("manifest-artifact", "writing actual-shape manifest", 1, "artifacts");
    if (reachRules is null) File.WriteAllText(geometryManifestPath, arms.StampForced(JsonSerializer.Serialize(manifest, manifestJson)));
    else
    {
        var withLoose = JsonSerializer.SerializeToNode(manifest, manifestJson)!.AsObject();
        withLoose["reachLoose"] = JsonSerializer.SerializeToNode(reachLooseRows, manifestJson);
        File.WriteAllText(geometryManifestPath, arms.StampForced(withLoose.ToJsonString(manifestJson)));
    }
    __run.Advance().Artifact(geometryManifestPath);
    Console.WriteLine($"wrote geometry manifest: {recordRows.Count:N0} records, {pairRows.Count:N0} union pairs -> {geometryManifestPath}");
}

__run.Stage("finalize", "finalizing overlap run", 1, "runs").Advance();
Console.WriteLine($"\nActual render-surface relations are geometry evidence, not compatibility severity, intent or a game-verified defect.");
Console.WriteLine($"Limits: this measures static render surfaces; collision and runtime animation are unassessed; rotation- and scale-only re-placements are not provenance steps ({idx.SilentRePlacements:N0} counted above);");
Console.WriteLine($"        the arm without the later plugin uses the last placement and base outside that plugin's downstream side; unavailable historical geometry stays unknown.");
Console.WriteLine($"elapsed {sw.Elapsed.TotalSeconds:0.0}s");
return 0;

static float Rate(long hit, long total) => total == 0 ? 0 : 100f * hit / total;

// The --scene-preflight verdict as data, so a caller (evalrun plan) acts on the probe's own decision.
static void WritePreflightReceipt(string path, object receipt)
{
    var dir = Path.GetDirectoryName(Path.GetFullPath(path));
    if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
    File.WriteAllText(path, JsonSerializer.Serialize(receipt, new JsonSerializerOptions { WriteIndented = true }));
}

sealed record GeometryRead(string Status, string? Reason, string? RelativePath, string? AssetId,
                           PlacementResolution? Resolution, PlacementSource? Source,
                           NifRenderGeometryOutcome? Outcome, long? ByteCount = null, WindLabel? Wind = null);
sealed record ManifestBounds(double[] min, double[] max);
sealed record ReviewRef(FormKey key, string Eligibility, string EligibilityReason,
                        string? GeometryStatus, string? GeometryReason, FormKey? Base, Snap Pose, Aabb? WorldBounds,
                        TriangleSoup? WorldSoup, TriangleSoup? ModelSoup = null)
{
    /// <summary>The member as placed, for SurfaceQueryOutcome.QueryPlaced; null without its model.</summary>
    public SurfaceQueryOutcome.Placed? Placed => ModelSoup is null ? null : new(ModelSoup, Pose.Pos, Pose.Rot, Pose.Scale);
}
sealed record ManifestStatus(string status, string? reason);
sealed record ManifestBoxMeasure(string geometryStatus, string? reason, double[]? dimensions, double? volume);
sealed record ManifestZRelation(string relation, double signedIntersection, double gap);
sealed record ManifestMeasurements(string status, string? reason,
    ManifestBoxMeasure aBox, ManifestBoxMeasure bBox, double[]? intersectionExtents,
    double? intersectionVolume, double? xyIntersectionArea, double? overlapFractionSmaller,
    string? overlapFractionReason, ManifestZRelation? zRelation);
sealed record ManifestActualShape(string status, string? reason, string surfaceRelation, string containment,
                                  bool? intersects, long trianglePairsTested)
{
    /// <summary>The pair frame's origin when world placement collapsed a triangle; written only then.</summary>
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public double[]? frame { get; init; }
    /// <summary>Zero-area-in-model triangles left out, [a, b]; written only when any were.</summary>
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public int[]? zeroAreaSkipped { get; init; }
}
sealed record ManifestNarrowVerdict(string status, string? origin, string? sight, string reason,
                                    string? counterfactualStatus, string? counterfactualReason);
sealed record ManifestPair(string pairKey, string conflictIoRowIdentity, string a, string b,
    string[] historicalRowIds, bool newCandidate, ManifestStatus discovery, ManifestStatus eligibility,
    ManifestActualShape actualShape, ManifestMeasurements diagnosticBounds, ManifestNarrowVerdict oldNarrowVerdict,
    MeshContactEvidence contactEvidence);
sealed record HistoricalCrosswalk(int SchemaVersion, bool Complete, List<HistoricalPair>? Pairs);
sealed record HistoricalPair(string HistoricalRowId, string PairKey, string ConflictIoRowIdentity, string A, string B);
