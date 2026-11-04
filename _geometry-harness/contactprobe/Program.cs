using System.Diagnostics;
using System.Text.Json;
using HarnessShared;
using HousecarlCore;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Skyrim;
using HousecarlCore.Progress;

// PLACEMENT CONTACT — the geometry half of stranded support and orphaned attachment (roadmap 4.5 / 4.6;
// charter milestone 8 over milestone 6's object-object narrow phase, built here once for both).
//
// The two classes, in Piranha's words: mod A puts apples on a table, mod B moves the table, nothing
// moves the apples; mod A hangs a sconce on a wall, mod B moves the wall, the sconce is neither moved
// nor deleted. The records pass (depprobe) measured that no declared edge carries either shape — the
// apples and the sconce are not declared, so contact has to be COMPUTED. This is that computation:
// for every physical host a plugin moved, relocated or removed, every physical thing that RESTED ON or
// was MOUNTED AGAINST it in the order without the mover (SpatialOverlap.Contact on the collision soups,
// the host at its previous snap), and what holds it under the winner — the host at its new place,
// something else the room was rebuilt with, nothing (STRANDED / ORPHANED), or something it is now inside
// (SUNK). Differential, never absolute: an object that floated as shipped is the absolute floor's
// business and is measured beside the class, never counted in it.
//
// Two splits ride on every count. FIXED / MOVABLE: 4.7 Part C showed a movable CLUTTER object is shoved,
// not a blocker — so a movable dependent left in the air falls at cell load rather than floating; only
// FIXED rows are the class until an in-game arm says otherwise, and movable rows are a context list. The
// motion class reads the record type and, for a MoveableStatic, the NIF's own motion system (the decoder
// now reports it), never a guess from the type alone. INTERIOR / EXTERIOR: terrain is not a reference,
// so an exterior dependent may be standing on the ground and read Stranded; counts are read on interiors. The terrain as a
// support is the --terrain mode's question (TerrainReview.cs).
//
// The rule is DetectorModel.PlacementContact (proofprobe-owned, a synthetic order behind it); the arms
// and the pairing are _shared/ContactIndex.cs over OverlapIndex's history — the same changed-only
// history driftprobe, depprobe and overlapprobe run on. The primitive is SpatialOverlap.Contact
// (crossprobe-owned). Everything here is I/O, geometry choice and tallies. Standing rules applied: the
// geometry CHOICE (collision / render / OBND box) labelled on every row; floors and the labelled set
// measured before any count is read (rules 2, 12); unjudgeable counted, never cleared (rule 6);
// nothing verified in game.
//
// Modes (one run, one mode):
//   (default)              the differential pass over every moved host — the class, the floor, the ranker
//   --absolute             every clutter- and furniture-scale ref against its neighbours as SHIPPED: touching /
//                          floating / sunk — the absolute floor the charter predicted is large
//   --labelled-mover X.esp every ref X moved, contact BEFORE X vs AFTER X — USSEP's placement fixes as a
//                          labelled positive set for the primitive (no game, no judgement)
//   --empty-space FILE     LEFT IN EMPTY SPACE (2026-10-14): every dependent-scale fixture within reach of a moved host, against that
//                          host's mover: its assembly (what it touches within ε, transitively, and the items its racks mount) reached a
//                          floor or wall the mover removed or moved, and reaches none under the winner (DetectorModel.EmptySpace,
//                          _shared/ContactAssembly.cs); --emit writes the class as EMPTY-SPACE rows, --base re-measures an emit's rows
//   --overlap-json FILE    supplied overlap rows/pairs through the strict NIF render-surface narrow phase;
//                          requires --json OUTPUT, which persists all attempted and budget-unrun rows
//   --contact-evidence     with --overlap-json, enumerate the classified-contact support envelope for each supplied pair
//   --volume FILE          with --overlap-json, the measured overlap volume of each pair (validated closed solids only)
//   --surface FILE         with --overlap-json, how each pair's surfaces meet where the volume cannot be measured: the
//                          crossing length (an interval), coplanar areas (same-facing = z-fighting, opposite-facing =
//                          resting contact) and the area buried in the other member when it is a closed solid; render
//                          triangles tagged by material (only opaque x opaque may decide), collision separately
//   --alpha-visibility     with --surface: a render pair whose crossing lies on an alpha-tested / alpha-blended triangle gains a
//                          visibility block: the part of the crossing curve through drawn texels of both members (texture alpha,
//                          mip 0, bilinear; VFS winner textures, base-record alternate texture sets applied), and a swept witness
//                          whose crossing is not opaque x opaque gains visibleMotion; without the flag the file is unchanged
//   --live-pose MODE       with --surface: blanket (default; any controller on a shape's node chain, a skin, a switch
//                          node or a behaviour graph refuses) or targeted (refuses only what the file shows moving the
//                          shape: a skin, a switch node, a sequence's or controller's transform on its node chain, or a
//                          behaviour graph; other controllers leave the stored pose standing)
//   --motion MODE          with --surface or --volume: a pair member whose stored pose is not its live pose is measured
//                          against its motion instead of refused (animated-object swept volume): swept (the moving
//                          member's swept region over every animation the files can play: witness / never / cannot_tell,
//                          labelled with the animation, cycle, base record type and "trigger: not settled by the files";
//                          --volume adds the certified intersection with a closed partner) or pose:NAME (closed, open,
//                          state:NAME, <animation>@start|@end). Default off: outputs unchanged.
//   --motion-delta U       with --motion swept: target bound on how far the moving part moves between samples (default 0.5)
//   --motion-budget N      with --motion swept: samples per animation before the region is reported not converged (4096)
//                          (lane 3b) --motion swept also applies to --anchor (the body against every sample of a moving partner:
//                          witness / never / cannot_tell), and a member refused only for a NiPathInterpolator is measured as a tube
//                          around its path (never / cannot_tell, never a witness). A pair whose world placement collapses a triangle
//                          is measured centred on B, on the geometry or on A (pairFrame); particle systems set aside from a render surface are
//                          labelled particlesA / particlesB.
//   --terrain              TERRAIN SUPPORT (2026-10-20): every exterior placed reference that rested on the ground in its placement
//                          owner's view, against the winning LAND (both heightmap diagonals) and the winning arm's collision supports:
//                          floating or buried where a plugin the owner could not see reshaped the ground (TerrainReview.cs,
//                          _shared/TerrainContactIndex.cs, DetectorModel.TerrainSupport). Requires --float-threshold U and
//                          --burial-threshold U (frozen in a run plan; no default). With --absolute: the absolute floor as shipped first;
//                          --terrain-sample N reads one reference in N of that floor (deterministic by FormKey). --emit writes the class
//                          as TERRAIN rows, --base re-measures an emit's rows, --json writes every judged row.
//   --box-contact FILE     with --overlap-json: each input boxContact row's world boxes against the other member's render surface;
//                          with --motion swept a member refused for its live pose is tested through its swept region (witness / never /
//                          cannot_tell per box; opaqueMeets true / false / null)
//
// usage: contactprobe [--list N] [--eps U] [--reach U] [--json FILE] [--emit FILE] [--base EMIT] [--all-rows] [--interior-only]
//                     [--absolute] [--labelled-mover X.esp] [--overlap-json FILE]
//                     [--contact-evidence] [--contact-budget-seconds 1200] [--pair-budget-seconds 120] [--contact-max-pairs 128]
//                     [--volume FILE] [--surface FILE [--live-pose blanket|targeted] [--alpha-visibility]] [--motion off|swept|pose:NAME [--motion-delta U] [--motion-budget N]]
//                     [--only A.esm,B.esm,...] [--vanilla-assets] [--bos on|off]
//                     [--enable X.esp]* [--disable X.esp]* [--instance DIR] [--game DIR] [--profile NAME]
//   POPULATION      the LOAD-TIME state, and it always has been (R1, 2026-09-03): a reference exists in an arm when its
//                   XESP parent chain resolves enabled there, resolved PER ARM — the winner's, and the arm without a
//                   mover's. Q27 changed nothing here except that the emit now says so. --flags is refused: there is no
//                   flag-only contact population to reproduce.
//   --state SPEC    the world-state arm (roadmap 4.11): <switch>[,<switch>…], stage:<quest>:<n> (with --switch-map), or
//                   baseline — the runtime enable state every arm reads (R1) has those switches flipped, in the winner arm
//                   and in the arm without the mover alike. Without it, the load-time state, as every run before 2026-09-04.
//   --radius on|off the gap the rule reads is between the shapes grown by their Havok radii; normal and depth stay the core's
//                   (default on; rows gain AuthoringCoreGap / WinnerCoreGap); off = the core reading, byte-identical to before
//   --cache-budget SIZE|auto  the byte budget the per-placement contact caches share, and in the default pass the decoded meshes
//                   too (least-recently-used; an evicted mesh is decoded again from the same bytes; e.g. 2G, 512M;
//                   minimum 64M). auto (default) = the smaller of 20 % of the memory the runtime may use (physical, or the heap
//                   hard limit), 40 % of (memory free before the host loop - live data) and 1 GiB, or 2 GiB per thread with
//                   --threads above 1 (an explicit size may exceed it).
//                   Output does not depend on it; a smaller budget rebuilds evicted grids (slower, less memory)
//   --threads N     the host loop's worker threads (default 8; 1 = one thread, as before). Rows, their order and every count are the
//                   same at any N. Above 1 the automatic --cache-budget ceiling is 2 GiB per thread, still under its 20 % and 40 % terms.
//                   --geom-report, HC_MEMSTAGES and HC_STALEBASE record first-seen order, so they run on one thread
//   --phase-timers FILE  the contact code's own time split (ContactIndex BestContact / PairContact / Judge, their caches and the core
//                   contact steps; _shared/ContactIndex.cs ContactTimer) printed after the run, recorded as run features and written to
//                   FILE as JSON; for measuring where the time goes. Changes no output
//   --bos on|off    apply the Base Object Swapper layer (*_SWAP.ini) so every ref is judged by the base the game loads
//                   (default on; _shared/BosHook.cs); off = the record bases
//   --base EMIT     re-measure every record of that emit's rows under THIS order and carry the results in the emit as
//                   Predicates — the changed arm of a differential (conflictdiff reads both). See ConflictReport.cs.
//   --reverse-of BASELINE_EMIT  (default pass, with --base and --emit) a reverse run: carry the baseline emit's rows and
//                   coverage instead of judging every moved host again, and re-measure only the --base rows. Refused by name unless
//                   the baseline is this run's own: probe, configuration, population, state, measurement policy, instance and the
//                   coverage lines fixed before the host loop (_shared/ReverseFromBaseline.cs). The emit equals a full reverse's.
//   --only          keep only the named plugins of the profile's order (the vanilla masters, or vanilla + USSEP)
//   --vanilla-assets resolve meshes from the game's own archives only — no mod folder, no replacer — so a
//                   labelled set authored against vanilla meshes is tested against vanilla meshes
//   OVERLAP REVIEW  only decoded render triangles on both sides; partial/unsupported/missing NIFs stay unknown.
//                   Budgets apply cooperatively to the review stage; asset decode/grid construction can overrun
//                   a deadline before the next check. No contact/penetration result is a compatibility verdict.

// Nothing is compiled in — see _shared/HarnessTarget.cs. Flags win over the environment and over
// harness.local.json; Require() runs AFTER them.
if (args.Contains("--help") || args.Contains("-h"))
{
    Console.WriteLine("""
contactprobe: placement contact and explicit render-surface pair review
  --overlap-json INPUT --json OUTPUT [--contact-evidence]
  --contact-budget-seconds N --pair-budget-seconds N --contact-max-pairs N
  --surface-tolerance N --mesh-max-triangle-pairs N
  --volume OUTPUT (with --overlap-json): measured overlap volume per pair, render and collision separately
  --anchor OUTPUT (with --overlap-json): member A is an anchor (marker, idle marker, placed actor, furniture entry), B a
    partner: point, line and body readings of the actor at the anchor against B's opaque render surface, the body deciding
  --surface OUTPUT (with --overlap-json): crossing length, coplanar areas and buried area per pair, render and collision
    separately; --live-pose blanket|targeted chooses the live-pose rule (default targeted); --alpha-visibility adds the visible
    part of an alpha crossing (texture alpha along the crossing curve) to each such render pair
  --motion off|swept|pose:NAME (with --surface/--volume; swept also with --anchor): measure a moving member against its motion
    instead of refusing it (a NiPathInterpolator member: a tube around its path, never or cannot_tell); --motion-delta U and
    --motion-budget N tune the swept sampling (default 0.5 u, 4096)
  --box-contact OUTPUT (with --overlap-json): for each input boxContact row {pairKey, a, b, boxOwner A|B, boxes [{label, min,
    max}]}, whether each world box meets the other member's render surface (opaque, and any material); with --motion swept a
    member refused for its live pose is tested through its swept region (witness / never / cannot_tell per box)
  --instance DIR --game DIR --profile NAME --order CANDIDATE.json
Candidate Mods lists select asset priority. Surface/contact evidence is not a defect verdict.
Other modes: --absolute, --labelled-mover PLUGIN, or --empty-space OUTPUT (fixtures a mover left in empty space: their
  assembly reaches no floor or wall; --emit writes that class as EMPTY-SPACE rows); default is the moved-host pass.
Default pass only: --trace OUTPUT [--trace-keys KEYS.txt] writes the per-object trace (why each requested FormKey
was or was not flagged; without a key list, counting mode checks the coverage counters). Output is otherwise unchanged.
See contactprobe/Program.cs usage documentation for those modes. Help performs no load-order scan.
""");
    return 0;
}
using var __run = RunLog.BeginProcess("contactprobe", args);
// Known answers for the emit's class and the physical predicate (ContactSelfTest.cs, over ContactCredit.cs).
if (args.Length >= 1 && args[0] == "--self-test") return ContactSelfTest.Run();

string Instance = HarnessTarget.Instance;
string GamePath = HarnessTarget.Game;
string profileName = HarnessTarget.Profile;
int listN = 25;
double contactBudgetSeconds = 1200, pairBudgetSeconds = 120;
int contactMaxPairs = 128;
double surfaceTolerance = 1e-5;
long meshMaxTrianglePairs = 2_000_000;
string? stateSpec = null, switchMapPath = null;
var forceEnable = new List<string>();   // --force-enable: singletons read as enabled after load (EnableArms.Force)
float eps = 4f;            // touching: surfaces within ε and no vertex deeper than ε inside. A chosen number, stated.
float reach = 128f;        // how far from a moved host a dependent is looked for (the records pass's contact-plausible radius).
string? reverseOfPath = null;   // --reverse-of BASELINE_EMIT: carry the baseline's rows in a reverse run
string? phaseTimersPath = null;   // --phase-timers FILE: the contact code's own time split (changes no output)
string? jsonPath = null, emitPath = null, basePath = null, overlapJson = null, labelledMover = null, geomReport = null;
bool allRows = false, absolute = false, vanillaAssets = false, interiorOnly = false, contactEvidence = false;
HashSet<string>? only = null;
bool bosOn = true;
long? cacheBudget = null;   // --cache-budget: null = CacheBudget.Auto()
int threads = 8;            // --threads: the host loop's workers
bool radiusOn = true;   // --radius: read the Havok radius for the contact gap (default on; off = the core reading as before)
bool terrain = false; float? floatThreshold = null, burialThreshold = null, burialShare = null; int terrainSample = 1;   // --terrain (TerrainReview.cs)
var enablePlugins = new List<string>();
var disablePlugins = new List<string>();
string? orderFile = null;
// --population FILE (P25): the plan's population (_shared/PopulationScope.cs), default pass only; --population-audit EMIT OUT
// writes, for every row of EMIT, whether it is in that population (the equivalence check reads it).
string? populationPath = null, populationAuditEmit = null, populationAuditOut = null;
string? boxContactOut = null;   // --box-contact FILE (with --overlap-json): does a supplied world box meet a member's render surface (lane 3b)
string? emptySpaceOut = null;   // --empty-space FILE: the LEFT IN EMPTY SPACE pass (_shared/ContactAssembly.cs), its own mode
string? traceOut = null, traceKeysPath = null, volumeOut = null, surfaceOut = null, anchorOut = null;   // --surface FILE: measured surface contact (with --overlap-json); --anchor FILE: the anchor body test (G1 build list row 2)
LivePosePolicy? livePose = null;
bool alphaVisibility = false;   // --alpha-visibility (with --surface): texture alpha along alpha crossings (Stage D)
string? motionSpec = null; double? motionDelta = null; int? motionBudget = null;   // --live-pose blanket|targeted (with --surface)
   // --volume FILE: measured overlap volume (with --overlap-json)   // --trace FILE [--trace-keys FILE]: the per-object trace side file (default pass only)
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
        if (flag == "--list") { if (!int.TryParse(NeedValue(i, flag), out listN)) throw new ArgumentException("--list requires an integer"); i++; }
        else if (flag == "--eps") { if (!float.TryParse(NeedValue(i, flag), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out eps)) throw new ArgumentException("--eps requires a number"); i++; }
        else if (flag == "--reach") { if (!float.TryParse(NeedValue(i, flag), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out reach)) throw new ArgumentException("--reach requires a number"); i++; }
        else if (flag == "--json") { jsonPath = NeedValue(i, flag); i++; }
        else if (flag == "--phase-timers") { phaseTimersPath = NeedValue(i, flag); i++; }
        else if (flag == "--emit") { emitPath = NeedValue(i, flag); i++; }
        else if (flag == "--base") { basePath = NeedValue(i, flag); i++; }
        else if (flag == "--reverse-of") { reverseOfPath = NeedValue(i, flag); i++; }
        else if (flag == "--overlap-json") { overlapJson = NeedValue(i, flag); i++; }
        else if (flag == "--contact-evidence") contactEvidence = true;
        else if (flag == "--contact-budget-seconds") { if (!double.TryParse(NeedValue(i, flag), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out contactBudgetSeconds)) throw new ArgumentException("--contact-budget-seconds requires a number"); i++; }
        else if (flag == "--pair-budget-seconds") { if (!double.TryParse(NeedValue(i, flag), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out pairBudgetSeconds)) throw new ArgumentException("--pair-budget-seconds requires a number"); i++; }
        else if (flag == "--contact-max-pairs") { if (!int.TryParse(NeedValue(i, flag), out contactMaxPairs)) throw new ArgumentException("--contact-max-pairs requires an integer"); i++; }
        else if (flag == "--surface-tolerance") { if (!double.TryParse(NeedValue(i, flag), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out surfaceTolerance)) throw new ArgumentException("--surface-tolerance requires a number"); i++; }
        else if (flag == "--mesh-max-triangle-pairs") { if (!long.TryParse(NeedValue(i, flag), out meshMaxTrianglePairs)) throw new ArgumentException("--mesh-max-triangle-pairs requires an integer"); i++; }
        else if (flag == "--labelled-mover") { labelledMover = NeedValue(i, flag); i++; }
        else if (flag == "--geom-report") { geomReport = NeedValue(i, flag); i++; }
        else if (flag == "--only")
        {
            only = new HashSet<string>(NeedValue(i, flag).Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries), StringComparer.OrdinalIgnoreCase);
            if (only.Count == 0) throw new ArgumentException("--only requires at least one plugin name");
            i++;
        }
        else if (flag == "--all-rows") allRows = true;
        else if (flag == "--absolute") absolute = true;
        else if (flag == "--terrain") terrain = true;
        else if (flag == "--float-threshold") { if (!float.TryParse(NeedValue(i, flag), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var ft)) throw new ArgumentException("--float-threshold requires a number"); floatThreshold = ft; i++; }
        else if (flag == "--burial-share") { if (!float.TryParse(NeedValue(i, flag), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var bs) || bs < 0f || bs > 1f) throw new ArgumentException("--burial-share requires a number from 0 to 1"); burialShare = bs; i++; }
        else if (flag == "--burial-threshold") { if (!float.TryParse(NeedValue(i, flag), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var bt)) throw new ArgumentException("--burial-threshold requires a number"); burialThreshold = bt; i++; }
        else if (flag == "--terrain-sample") { if (!int.TryParse(NeedValue(i, flag), out terrainSample) || terrainSample < 1) throw new ArgumentException("--terrain-sample requires a positive integer"); i++; }
        else if (flag == "--vanilla-assets") vanillaAssets = true;
        else if (flag == "--interior-only") interiorOnly = true;
        else if (flag == "--order") { orderFile = NeedValue(i, flag); i++; }
        else if (flag == "--trace") { traceOut = NeedValue(i, flag); i++; }
        else if (flag == "--empty-space") { emptySpaceOut = NeedValue(i, flag); i++; }
        else if (flag == "--volume") { volumeOut = NeedValue(i, flag); i++; }
        else if (flag == "--surface") { surfaceOut = NeedValue(i, flag); i++; }
        else if (flag == "--anchor") { anchorOut = NeedValue(i, flag); i++; }
        else if (flag == "--box-contact") { boxContactOut = NeedValue(i, flag); i++; }
        else if (flag == "--live-pose")
        {
            livePose = NeedValue(i, flag).ToLowerInvariant() switch
            {
                "blanket" => LivePosePolicy.Blanket, "targeted" => LivePosePolicy.Targeted,
                _ => throw new ArgumentException("--live-pose takes blanket|targeted"),
            };
            i++;
        }
        else if (flag == "--alpha-visibility") alphaVisibility = true;
        else if (flag == "--motion") { motionSpec = NeedValue(i, flag); i++; }
        else if (flag == "--motion-delta") { motionDelta = double.Parse(NeedValue(i, flag), System.Globalization.CultureInfo.InvariantCulture); i++; }
        else if (flag == "--motion-budget") { motionBudget = int.Parse(NeedValue(i, flag), System.Globalization.CultureInfo.InvariantCulture); i++; }
        else if (flag == "--trace-keys") { traceKeysPath = NeedValue(i, flag); i++; }
        else if (flag == "--enable") { enablePlugins.Add(NeedValue(i, flag)); i++; }
        else if (flag == "--disable") { disablePlugins.Add(NeedValue(i, flag)); i++; }
        else if (flag == "--bos") { bosOn = BosHook.ParseFlag(NeedValue(i, flag)) ?? throw new ArgumentException("--bos takes on|off"); i++; }
        else if (flag == "--cache-budget")
        {
            var v = NeedValue(i, flag);
            // "auto" is the default (null), so the fit check's free-memory term applies to it as well
            cacheBudget = v.Trim().Equals("auto", StringComparison.OrdinalIgnoreCase) ? null
                        : CacheBudget.Parse(v) ?? throw new ArgumentException("--cache-budget takes a size (e.g. 2G, 512M, bytes) or auto");
            if (cacheBudget < CacheBudget.MinimumBytes) throw new ArgumentException($"--cache-budget {v} is below the minimum {CacheBudget.MinimumBytes >> 20} MiB");
            i++;
        }
        else if (flag == "--threads")
        {
            var v = NeedValue(i, flag);
            if (!int.TryParse(v, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out threads) || threads < 1)
                throw new ArgumentException($"--threads takes a whole number of at least 1, not {v}");
            i++;
        }
        else if (flag == "--radius") { radiusOn = NeedValue(i, flag) switch { "on" => true, "off" => false, _ => throw new ArgumentException("--radius takes on|off") }; i++; }
        else if (flag == "--state") { stateSpec = NeedValue(i, flag); i++; }
        else if (flag == "--force-enable") { forceEnable.AddRange(NeedValue(i, flag).Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)); i++; }
        // --flags is refused rather than added (Q27): contactprobe has resolved the enable chain since R1 (2026-09-03).
        else if (flag == "--flags")
        {
            try { _ = PopulationCli.Resolve("contactprobe", true, null, flagsSupported: false); }
            catch (ArgumentException ex) { throw new ArgumentException(ex.Message); }
            throw new ArgumentException("contactprobe does not support --flags");
        }
        else if (flag == "--switch-map") { switchMapPath = NeedValue(i, flag); i++; }
        else if (flag == "--population") { populationPath = NeedValue(i, flag); i++; }
        else if (flag == "--population-audit")
        {
            populationAuditEmit = NeedValue(i, flag);
            if (i + 2 >= args.Length || args[i + 2].StartsWith("--", StringComparison.Ordinal)) throw new ArgumentException("--population-audit requires EMIT and OUT");
            populationAuditOut = args[i + 2]; i += 2;
        }
        else if (flag == "--instance") { Instance = NeedValue(i, flag); i++; }
        else if (flag == "--game") { GamePath = NeedValue(i, flag); i++; }
        else if (flag == "--profile") { profileName = NeedValue(i, flag); i++; }
        else throw new ArgumentException($"unknown argument: {flag}");
    }
}
catch (ArgumentException ex) { Console.Error.WriteLine(ex.Message); return 2; }

if (listN < 0 || !float.IsFinite(eps) || eps < 0 || !float.IsFinite(reach) || reach < 0
    || !double.IsFinite(contactBudgetSeconds) || contactBudgetSeconds <= 0
    || !double.IsFinite(pairBudgetSeconds) || pairBudgetSeconds <= 0 || contactMaxPairs < 0
    || !double.IsFinite(surfaceTolerance) || surfaceTolerance < 0 || meshMaxTrianglePairs <= 0)
{
    Console.Error.WriteLine("list/contact limits must be nonnegative; time budgets must be finite positive; eps, reach and surface tolerance must be finite nonnegative; mesh triangle pairs must be positive");
    return 2;
}
if (overlapJson is not null && jsonPath is null) { Console.Error.WriteLine("--overlap-json requires --json OUTPUT"); return 2; }
if (contactEvidence && overlapJson is null)
{
    Console.Error.WriteLine("--contact-evidence is valid only with --overlap-json FILE.");
    return 2;
}
int selectedModes = (overlapJson is null ? 0 : 1) + (absolute && !terrain ? 1 : 0) + (labelledMover is null ? 0 : 1) + (emptySpaceOut is null ? 0 : 1) + (terrain ? 1 : 0);
if (selectedModes > 1) { Console.Error.WriteLine("--overlap-json, --absolute, --labelled-mover, --empty-space and --terrain select different modes; pass only one (--absolute with --terrain is the terrain floor)"); return 2; }
if (terrain && (floatThreshold is null || burialThreshold is null || burialShare is null)) { Console.Error.WriteLine("--terrain requires --float-threshold U, --burial-threshold U and --burial-share S: the thresholds are frozen in a run plan, never defaulted"); return 2; }
if (!terrain && (floatThreshold is not null || burialThreshold is not null || burialShare is not null || terrainSample != 1)) { Console.Error.WriteLine("--float-threshold, --burial-threshold, --burial-share and --terrain-sample are valid only with --terrain"); return 2; }
if (terrain && (traceOut is not null || populationPath is not null)) { Console.Error.WriteLine("--terrain takes no --trace or --population: it reads the whole order"); return 2; }
if (volumeOut is not null && overlapJson is null) { Console.Error.WriteLine("--volume is valid only with --overlap-json INPUT"); return 2; }
if (surfaceOut is not null && overlapJson is null) { Console.Error.WriteLine("--surface is valid only with --overlap-json INPUT"); return 2; }
if (anchorOut is not null && overlapJson is null) { Console.Error.WriteLine("--anchor is valid only with --overlap-json INPUT"); return 2; }
if (boxContactOut is not null && overlapJson is null) { Console.Error.WriteLine("--box-contact is valid only with --overlap-json INPUT"); return 2; }
if (livePose is not null && surfaceOut is null) { Console.Error.WriteLine("--live-pose is valid only with --surface OUTPUT"); return 2; }
if (alphaVisibility && surfaceOut is null) { Console.Error.WriteLine("--alpha-visibility is valid only with --surface OUTPUT"); return 2; }
if (traceKeysPath is not null && traceOut is null) { Console.Error.WriteLine("--trace-keys requires --trace OUTPUT"); return 2; }
MotionReview.Options? motionOptions;
try { motionOptions = MotionReview.Parse(motionSpec, motionDelta, motionBudget); }
catch (ArgumentException ex) { Console.Error.WriteLine(ex.Message); return 2; }
if (motionSpec is not null && surfaceOut is null && volumeOut is null && anchorOut is null && boxContactOut is null) { Console.Error.WriteLine("--motion is valid only with --surface, --volume, --anchor or --box-contact OUTPUT"); return 2; }
if (boxContactOut is not null && surfaceOut is null && volumeOut is null && anchorOut is null && motionOptions is { Mode: not "swept" }) { Console.Error.WriteLine("--box-contact takes --motion swept only (a box against every pose of a moving member)"); return 2; }
if (anchorOut is not null && motionOptions is { Mode: not "swept" }) { Console.Error.WriteLine("--anchor takes --motion swept only (a body against every pose of a moving partner)"); return 2; }
if ((motionDelta is not null || motionBudget is not null) && motionOptions?.Mode != "swept") { Console.Error.WriteLine("--motion-delta and --motion-budget are valid only with --motion swept"); return 2; }
if (motionDelta is { } md && !(md > 0 && double.IsFinite(md)) || motionBudget is { } mb && mb < 2) { Console.Error.WriteLine("--motion-delta must be positive and finite; --motion-budget at least 2"); return 2; }
if (traceOut is not null && selectedModes > 0) { Console.Error.WriteLine("--trace is valid only in the default moved-host pass"); return 2; }
if (populationPath is not null && selectedModes > 0 && emptySpaceOut is null) { Console.Error.WriteLine("--population is valid only in the default moved-host pass and with --empty-space"); return 2; }
if (populationPath is not null && emptySpaceOut is not null && basePath is not null) { Console.Error.WriteLine("--population with --empty-space takes no --base: the physical half of a row outside the population is not computed; run the predicate pass on the whole order"); return 2; }
if (populationAuditEmit is not null && populationPath is null) { Console.Error.WriteLine("--population-audit needs --population"); return 2; }
if (reverseOfPath is not null)
{
    string? clash = basePath is null ? "--reverse-of needs --base (the candidate emit whose rows the reverse re-measures)"
        : emitPath is null ? "--reverse-of needs --emit"
        : selectedModes > 0 ? "--reverse-of is valid only in the default moved-host pass"
        : jsonPath is not null || traceOut is not null || populationAuditEmit is not null || phaseTimersPath is not null
            ? "--reverse-of writes no judged rows, trace, population audit or contact timers (it judges no host); drop --json, --trace, --population-audit and --phase-timers"
        : null;
    if (clash is not null) { Console.Error.WriteLine(clash); return 2; }
}
List<FormKey>? traceKeys = null;
if (traceKeysPath is not null && File.Exists(traceKeysPath))
{
    try { traceKeys = ContactTraceWriter.ReadKeys(traceKeysPath); }
    catch (ArgumentException ex) { Console.Error.WriteLine(ex.Message); return 2; }
}
foreach (var input in new[] { (Flag: "--overlap-json", Path: overlapJson), (Flag: "--base", Path: basePath),
    (Flag: "--switch-map", Path: switchMapPath), (Flag: "--order", Path: orderFile), (Flag: "--trace-keys", Path: traceKeysPath),
    (Flag: "--population-audit", Path: populationAuditEmit), (Flag: "--reverse-of", Path: reverseOfPath) })
    if (input.Path is not null && !File.Exists(input.Path))
    { Console.Error.WriteLine($"{input.Flag} input does not exist: {input.Path}"); return 2; }
PopulationScope? population = null;
if (populationPath is not null)
{
    try { population = PopulationScope.Load(populationPath); }
    catch (PopulationRefused ex) { Console.Error.WriteLine(ex.Message); return 2; }
}

Instance = HarnessTarget.Require(Instance, "MO2 instance directory", "instance", "--instance");
GamePath = HarnessTarget.Require(GamePath, "game directory", "game", "--game");
profileName = HarnessTarget.Require(profileName, "MO2 profile name", "profile", "--profile");
string profileDir = Path.Combine(Instance, "profiles", profileName);
string modsDir = Path.Combine(Instance, "mods");
string overwriteDir = Path.Combine(Instance, "overwrite");
string dataDir = Path.Combine(GamePath, "Data");
var sw = Stopwatch.StartNew();
// WHAT THIS RUN EVALUATES, decided in one place (_shared/HarnessOrderCli.cs): the real profile, a --enable/--disable
// what-if, or a --order candidate file - and, in the same step, the ASSET VIEW that configuration implies, so a run
// whose plugins changed no longer reads the profile's meshes and BSAs regardless. The identity it produces is written
// into the emit, so a reader (and conflictdiff) can see exactly what was consumed instead of inferring it.
var cfg = HarnessOrderCli.Resolve(profileName, profileDir, modsDir, dataDir, overwriteDir, GamePath,
                                  orderFile, enablePlugins, disablePlugins);
if (cfg.Refused) { Console.Error.WriteLine(cfg.RefusedReason); return 2; }
var paths = cfg.OrderedPaths;
if (only is not null)
{
    paths = paths.Where(p => only.Contains(Path.GetFileName(p))).ToList();
    Console.WriteLine($"--only: {paths.Count} of the profile's plugins kept ({string.Join(", ", paths.Select(Path.GetFileName))})");
    foreach (var name in only.Where(n => !paths.Any(p => string.Equals(Path.GetFileName(p), n, StringComparison.OrdinalIgnoreCase))))
        Console.WriteLine($"   ! --only names '{name}', which the profile's order does not contain");
    // --only means this run read FEWER plugins than the configuration offers, so the identity is narrowed to what was
    // actually consumed. Left un-narrowed it would claim the whole order and two --only runs over different subsets
    // would be indistinguishable — the identity's whole job.
    cfg = cfg.NarrowedTo(paths, $"--only kept {paths.Count} plugin(s) of the configuration's {cfg.Plugins.Count}");
}
IReadOnlyList<string> enabledMods = vanillaAssets ? Array.Empty<string>() : cfg.EnabledMods;
var archives = vanillaAssets ? cfg.Archives.Archives.Where(a => a.Path.StartsWith(dataDir, StringComparison.OrdinalIgnoreCase)).ToList() : cfg.Archives.Archives;
// --vanilla-assets is a DIFFERENT asset view from the one the configuration resolved, so the identity says so rather
// than carrying the profile's providers over a run that deliberately read none of them.
if (vanillaAssets) cfg = cfg.WithVanillaAssets();
using var assets = AssetResolver.Build(vanillaAssets ? Path.Combine(Instance, "__no_overwrite__") : overwriteDir, modsDir, dataDir, enabledMods, archives);
Console.WriteLine($"load order: {paths.Count} plugins; meshes from {(vanillaAssets ? $"the game's own archives only ({archives.Count} archives)" : $"the profile's VFS ({enabledMods.Count} mods, {archives.Count} archives)")}");
HarnessOrderCli.Print(cfg);
Console.WriteLine();

var Vanilla = BaseGamePlugins.Names;

// ---------------------------------------------------------------------------------------
// Geometry — measured NIFs through the VFS; the soup per role chosen and LABELLED; OBND box as the fallback.
// ---------------------------------------------------------------------------------------
ContactIndex? cidx = null;
// E1 (follow-ups item 1): the Tree_Anim shapes of each mesh read for contact geometry, for the dependent's wind note
var meshWind = new Dictionary<string, WindLabel?>(StringComparer.OrdinalIgnoreCase);
string? WindNoteOf(FormKey depBase)
{
    if (cidx!.Summary(depBase) is not { } g || !cidx.Index.BaseMesh.TryGetValue(depBase, out var rel)) return null;
    var full = rel.StartsWith("meshes", StringComparison.OrdinalIgnoreCase) ? rel : Path.Combine("meshes", rel);
    return NifWind.ContactNote(meshWind.GetValueOrDefault(full), g.DepSrc == 'r');
}
// --geom-report: one line per measured mesh — which soup each role got and why, every solid body's layer and motion.
var geomLines = geomReport is null ? null : new List<string> { "mesh\thostSrc\tdepSrc\thostTris\tdepTris\trenderTris\tcollisionComplete\tphantomOnly\tbodies" };
long meshesMeasured = 0, hostColl = 0, hostRender = 0, depColl = 0, depRender = 0, meshNoSoup = 0, obndFallback = 0, noGeometry = 0, phantomOnly = 0, incompleteCollision = 0;

// The default pass keeps no decoded mesh beyond the shared contact cache budget (MeshGeometryReader.cs): ContactIndex keeps a summary
// per base, and a mesh evicted from the budget is decoded again from the same bytes. The run's mesh counters, the wind labels and
// --geom-report lines are written on a mesh's first decode only. The other modes memoise every base's geometry, and their
// mesh store decodes each mesh once and keeps it, so bases sharing a mesh share one copy, as before.
bool boundedGeometry = selectedModes == 0 || terrain;
// One archive reader per archive for the default pass (housecarl-core ArchiveReadScope.cs): each archive's table is read once in the
// pass, not on every mesh read; disposed when the program ends (try/finally through using). The other modes read as before.
using var archiveScope = boundedGeometry ? new ArchiveReadScope() : null;
long readAlloc = 0, decodeAlloc = 0, readCount = 0, readBytes = 0;   // HC_MEMSTAGES counters
byte[]? ReadMesh(string full)
{
    long a0 = MemStages.On ? GC.GetAllocatedBytesForCurrentThread() : 0;
    var res = assets.ResolveForPlacement(full);
    var b = res.Sources.Count == 0 ? null
          : archiveScope is not null ? archiveScope.ReadPlacementSource(res.Sources[0]).Bytes
          : AssetResolver.ReadPlacementSource(res.Sources[0]).Bytes;
    if (MemStages.On)
    {
        long ra = GC.GetAllocatedBytesForCurrentThread() - a0;
        readAlloc += ra; readCount++; readBytes += b?.Length ?? 0;
        if (ra > 64L << 20)
            Console.WriteLine($"   [memstages] read {full}: allocated {ra / 1e6:0.0} MB for {b?.Length ?? 0:N0} bytes from {(res.Sources.Count > 0 ? res.Sources[0].ArchivePath ?? res.Sources[0].LooseFilePath : "(none)")}");
    }
    return b;
}
RefGeometry? DecodeMesh(string full, byte[] bytes, bool first)
{
    RefGeometry? g = null;
    if (first) meshWind[full] = NifWind.Label(bytes);
    if (NifGeometry.Measure(bytes, includeTriangles: true) is { Model: { } m })
    {
        if (first) meshesMeasured++;
        if (first && !m.CollisionComplete) incompleteCollision++;
        // The HOST surface: the solid bodies on the layers an actor (and a dropped item) meets — a canopy
        // leaves, a trunk stays. The DEPENDENT solid: every solid body but NONCOLLIDABLE (an item's WEAPON
        // or CLUTTER body is its shape). Either falls back to the render soup, labelled 'r'.
        var hc = m.CollisionSoupFor(b => b.Solid && CollisionLayers.IsActorSolid(b.Layer));
        var dc = m.CollisionSoupFor(b => b.Solid && b.Layer != "SKYL_NONCOLLIDABLE");
        TriangleSoup host, dep; char hs, ds;
        if (m.CollisionComplete && hc is { TriangleCount: > 0 }) { host = hc; hs = 'c'; if (first) hostColl++; }
        else if (m.Soup is { TriangleCount: > 0 }) { host = m.Soup; hs = 'r'; if (first) hostRender++; }
        else { host = TriangleSoup.Empty; hs = '-'; }
        if (m.CollisionComplete && dc is { TriangleCount: > 0 }) { dep = dc; ds = 'c'; if (first) depColl++; }
        else if (m.Soup is { TriangleCount: > 0 }) { dep = m.Soup; ds = 'r'; if (first) depRender++; }
        else { dep = TriangleSoup.Empty; ds = '-'; }
        var box = SpatialOverlap.SoupBounds(host).Union(SpatialOverlap.SoupBounds(dep));
        var bodies = m.CollisionBodies?.Where(b => b.Solid).Select(b => (b.Layer, b.MotionSystem)).ToList() ?? new List<(string, string)>();
        bool phantom = m.HasCollision && !m.HasSolidCollision;
        if (first && phantom) phantomOnly++;
        if (first && box.IsEmpty) meshNoSoup++;
        // A host must be real collision: the engine rests nothing on a mesh without it, and the inside test
        // needs a closed solid. A render fallback still serves as a DEPENDENT, labelled.
        g = box.IsEmpty ? null : new RefGeometry(box, host, dep, hs, ds, HostUsable: hs == 'c', bodies, phantom);
        if (g is not null && radiusOn)
        {
            // --radius on: the same soups read again with every Havok radius grown (box, convex vertices, compressed mesh),
            // the same role choice; the rule reads its gap from these and everything else from the core above
            var gm = NifGeometry.Measure(bytes, includeTriangles: true, collisionRadius: true).Model;
            TriangleSoup? hsh = null, dsh = null;
            if (gm is not null)
            {
                var hcs = gm.CollisionSoupFor(b => b.Solid && CollisionLayers.IsActorSolid(b.Layer));
                var dcs = gm.CollisionSoupFor(b => b.Solid && b.Layer != "SKYL_NONCOLLIDABLE");
                if (hs == 'c' && gm.CollisionComplete && hcs is { TriangleCount: > 0 }) hsh = hcs;
                if (ds == 'c' && gm.CollisionComplete && dcs is { TriangleCount: > 0 }) dsh = dcs;
            }
            g = g with { HostShell = hsh, DepShell = dsh };
        }
        if (first && geomLines is not null)
        {
            // The winding and closure audit per solid body: triangles whose normal points toward the body's
            // own vertex centroid (inward, for a convex body), and edges not shared by exactly two triangles
            // (an open shell). The inside test needs closed and outward; this says how often it has them.
            var audit = new List<string>();
            foreach (var b in m.CollisionBodies ?? Array.Empty<CollisionBodyInfo>())
            {
                string tag = $"{b.Layer}/{(b.Solid ? "solid" : "phantom")}/{(b.MotionSystem == "" ? "?" : b.MotionSystem)}/{string.Join("+", b.ShapeTypes.Where(t => t.StartsWith("bhk") && t.Contains("Shape")).Select(t => t.Replace("bhk", "").Replace("Shape", "")))}/{b.IndexCount / 3}tri{(b.Unread.Count > 0 ? "/UNREAD" : "")}";
                if (b.IndexCount >= 3 && m.CollisionSoup is { } cs)
                {
                    var cen = System.Numerics.Vector3.Zero; int nv = 0;
                    var vset = new HashSet<int>();
                    for (int k = b.IndexStart; k < b.IndexStart + b.IndexCount; k++) if (vset.Add(cs.Indices[k])) { cen += cs.Vertices[cs.Indices[k]]; nv++; }
                    if (nv > 0) cen /= nv;
                    int inward = 0, tris = 0;
                    var edges = new Dictionary<(int, int), int>();
                    for (int k = b.IndexStart; k + 2 < b.IndexStart + b.IndexCount; k += 3)
                    {
                        int i0 = cs.Indices[k], i1 = cs.Indices[k + 1], i2 = cs.Indices[k + 2];
                        var p0 = cs.Vertices[i0]; var p1 = cs.Vertices[i1]; var p2 = cs.Vertices[i2];
                        var n = System.Numerics.Vector3.Cross(p1 - p0, p2 - p0);
                        if (n.LengthSquared() < 1e-9f) continue;
                        tris++;
                        if (System.Numerics.Vector3.Dot(n, (p0 + p1 + p2) / 3f - cen) < 0) inward++;
                        foreach (var (a, bb) in new[] { (i0, i1), (i1, i2), (i2, i0) })
                        { var e = a < bb ? (a, bb) : (bb, a); edges[e] = edges.GetValueOrDefault(e) + 1; }
                    }
                    int open = edges.Count(e => e.Value != 2);
                    tag += $"/inward{inward}of{tris}/openEdges{open}of{edges.Count}";
                }
                audit.Add(tag);
            }
            geomLines.Add($"{full}\t{hs}\t{ds}\t{host.TriangleCount}\t{dep.TriangleCount}\t{m.Soup?.TriangleCount ?? 0}\t{m.CollisionComplete}\t{phantom}\t{string.Join(" ", audit)}");
        }
    }
    return g;
}
// HC_MEMSTAGES: a mesh file over 4 MB is named before its decode, and a decode that allocates over 256 MB after it (diagnostic only).
RefGeometry? DecodeMeshTraced(string full, byte[] bytes, bool first)
{
    if (!MemStages.On) return DecodeMesh(full, bytes, first);
    if (bytes.Length > 4 << 20 || Environment.GetEnvironmentVariable("HC_MEMSTAGES") == "every-decode")
        Console.WriteLine($"   [memstages] decoding {full}: {bytes.Length:N0} file bytes (first {first}), private {System.Diagnostics.Process.GetCurrentProcess().PrivateMemorySize64 / 1e9:0.000} GB");
    long a0 = GC.GetAllocatedBytesForCurrentThread(); var t0 = sw.Elapsed.TotalSeconds;
    var g = DecodeMesh(full, bytes, first);
    long a = GC.GetAllocatedBytesForCurrentThread() - a0;
    decodeAlloc += a;
    if (a > 256L << 20)
        Console.WriteLine($"   [memstages] decode {full}: {bytes.Length:N0} file bytes allocated {a / 1e9:0.000} GB in {sw.Elapsed.TotalSeconds - t0:0.0}s; host {g?.HostSoup.TriangleCount ?? 0:N0} tris ({g?.HostSrc}), dep {g?.DepSoup.TriangleCount ?? 0:N0} ({g?.DepSrc}); first {first}");
    return g;
}
var meshReader = new MeshGeometryReader(ReadMesh, DecodeMeshTraced);
var basesCounted = new HashSet<FormKey>();   // the OBND and no-geometry counters count a base once, however often it is asked for
RefGeometry? GeomOf(FormKey baseKey)
{
    RefGeometry? g = null;
    if (cidx!.Index.BaseMesh.TryGetValue(baseKey, out var rel))
    {
        var full = rel.StartsWith("meshes", StringComparison.OrdinalIgnoreCase) ? rel : Path.Combine("meshes", rel);
        try { g = meshReader.Get(full); }
        catch (MeshChangedOnDiskException ex) { Console.Error.WriteLine("contactprobe: " + ex.Message); Environment.Exit(2); }
    }
    if (g is null)
    {
        var ob = cidx.Index.ObndBox(baseKey);
        lock (basesCounted)
        {
            bool firstAsk = basesCounted.Add(baseKey);
            if (firstAsk) { if (!ob.IsEmpty) obndFallback++; else noGeometry++; }
        }
        if (!ob.IsEmpty) g = new RefGeometry(ob, ContactIndex.BoxSoup(ob), ContactIndex.BoxSoup(ob), 'o', 'o', HostUsable: false, Array.Empty<(string, string)>(), false);
    }
    return g;
}
// The material classes of a base's drawn render shapes, for --terrain's effect-only rule (Gate B review H1): read once per mesh
// through MeshSurface.ModelSurface (the one classifier, MeshVolume.ShapeMaterials); null when the surface is refused (a live
// pose, an unreadable shape) or the base has no mesh, and the body is then kept and counted.
var renderClassMemo = new Dictionary<string, IReadOnlyCollection<string>?>(StringComparer.OrdinalIgnoreCase);
IReadOnlyCollection<string>? RenderClasses(FormKey baseKey)
{
    if (!cidx!.Index.BaseMesh.TryGetValue(baseKey, out var rel)) return null;
    var full = rel.StartsWith("meshes", StringComparison.OrdinalIgnoreCase) ? rel : Path.Combine("meshes", rel);
    if (renderClassMemo.TryGetValue(full, out var known)) return known;
    IReadOnlyCollection<string>? classes = null;
    var bytes = ReadMesh(full);
    if (bytes is not null)
    {
        var surf = MeshSurface.ModelSurface(bytes, VolumeGeometry.Render);
        if (surf.Status == "measured" && surf.Tags is { } tags)
            classes = tags.Where(t => t != SurfaceMaterial.NotDrawn).Distinct().ToList();
    }
    return renderClassMemo[full] = classes;
}
if (threads > 1 && (geomReport is not null || MemStages.On || Environment.GetEnvironmentVariable("HC_STALEBASE") is not null))
{
    Console.WriteLine($"   --threads {threads} set to 1: --geom-report, HC_MEMSTAGES and HC_STALEBASE record first-seen order");
    threads = 1;
}
try { cidx = new ContactIndex(GeomOf, cacheBudget, memoiseGeometry: !boundedGeometry, automaticThreads: threads) { Epsilon = eps, Reach = reach, NearBand = 2f * eps, BoxBand = 2f * eps + 2f, ReadRadius = radiusOn }; }
catch (CacheBudget.AutomaticBudgetUnavailableException ex) { Console.Error.WriteLine(ex.Message + "; " + ex.Selection.Describe()); return 2; }
cidx.Budget.StableLargestTie = threads > 1;
meshReader.Store(boundedGeometry, cidx.Budget);
if (phaseTimersPath is not null) cidx.Timer = new ContactTimer();
var idx = cidx.Index;
// --bos on (default): the Base Object Swapper reader replaces each reference's record base with the base the
// game loads, and a moving override becomes a placement step under a pseudo-plugin after the last real one
// (_shared/BosHook.cs). --bos off: the record bases, as before 2026-09-04.
idx.Bos = bosOn ? new BosFormIndex() : null;

// ---------------------------------------------------------------------------------------
// Pass 1 — every plugin once, into the shared index.
// ---------------------------------------------------------------------------------------
var pluginKey = new ModKey[paths.Count];
var masterIdx = new HashSet<int>[paths.Count + (bosOn ? 1 : 0)];
var lightness = new List<(ModKey, bool)>();
var unreadable = new List<string>();
var landIndex = terrain ? new CellBoundaryIndex() : null;   // --terrain: every plugin's LAND, in the same pass
__run.Feature("plugins", paths.Count, "plugins").Stage("scan", "reading plugins", paths.Count, "plugins");

for (int p = 0; p < paths.Count; p++)
{
    __run.Advance();
    ISkyrimModGetter mod;
    try { mod = SkyrimMod.CreateFromBinaryOverlay(paths[p], SkyrimRelease.SkyrimSE); }
    catch { unreadable.Add(Path.GetFileName(paths[p])); pluginKey[p] = ModKey.Null; masterIdx[p] = new HashSet<int>(); lightness.Add((ModKey.Null, false)); continue; }
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
        idx.Ingest(p, mod);
        landIndex?.Ingest(p, mod);
    }
    if ((p + 1) % 150 == 0) Console.WriteLine($"   ... {p + 1}/{paths.Count} plugins, {sw.Elapsed.TotalSeconds:0}s");
}
int bosPlugin = bosOn ? paths.Count : -1;
if (bosOn) masterIdx[bosPlugin] = BosHook.PseudoMasters(paths.Count);
var closure = DetectorModel.MasterClosure(masterIdx);
Console.WriteLine($"indexed {idx.History.Count:N0} refs ({idx.Placements:N0} placements; {idx.SilentRePlacements:N0} rotation/scale-only overrides not recorded; {idx.Deletions.EverCount:N0} refs deleted somewhere)  ({sw.Elapsed.TotalSeconds:0.0}s)");
Console.WriteLine($"   plugins that could not be read (contribute nothing, counted per rule 6): {unreadable.Count:N0}" + (unreadable.Count > 0 ? "  — " + string.Join(", ", unreadable) : ""));
if (bosOn)
{
    idx.Bos!.SetLoadOrder(lightness);
    var bosLayer = BosHook.Load(assets.LooseRoots, idx.Bos);
    var bosRep = BosHook.Apply(idx, bosLayer, bosPlugin);
    Console.WriteLine($"   BOS: {bosLayer.Inis.Count} *_SWAP.ini read ({bosLayer.LinesRead:N0} lines, {bosLayer.LinesFailed:N0} the game would refuse, {bosLayer.Anomalies.Count:N0} anomalies — bosprobe lists them)");
    foreach (var l in bosRep.Summary()) Console.WriteLine("   " + l);
}
else Console.WriteLine("   BOS: off — record bases (--bos on to apply the *_SWAP.ini layer)");
if (stateSpec is not null)
{
    var sx = SwitchIndex.Build(idx);
    sx.NameSwitches(idx.RefEditorId);
    try { cidx.State = StateArm.Parse(stateSpec, sx, switchMapPath is null ? null : SwitchMap.Read(switchMapPath)); }
    catch (ArgumentException ex) { Console.Error.WriteLine(ex.Message); return 2; }
    var changes = cidx.State.Changes(sx).ToList();
    Console.WriteLine($"   state {cidx.State.Name}: {sx.Switches.Count:N0} switches in the order; {cidx.State.Flips.Count} flipped — {changes.Count(c => c.After):N0} refs arrive, {changes.Count(c => !c.After):N0} depart; "
                    + "every arm's runtime enable state (R1) carries the flip");
}

if (forceEnable.Count > 0)
{
    var keys = new List<FormKey>();
    foreach (var t in forceEnable)
    {
        if (!FormKey.TryFactory(t, out var fk)) { Console.Error.WriteLine($"--force-enable: '{t}' is not a FormKey (XXXXXX:Plugin.esp)"); return 2; }
        keys.Add(fk);
    }
    var refused = cidx.Arms.Force(keys);
    foreach (var (k, why) in refused) Console.Error.WriteLine($"--force-enable: {k} refused: {why}");
    if (refused.Count > 0) return 2;
    Console.WriteLine($"   force-enable: {cidx.Arms.Forced.Count} reference(s) {EnableArms.ForcedLabel}: {string.Join(", ", cidx.Arms.Forced)}");
}

if (population is not null)
{
    population.Bind(idx);
    Console.WriteLine("   " + population.Describe());
}
if (terrain)
{
    try
    {
        return TerrainReview.Run(cidx, landIndex!, closure, PluginName, BaseLabel, eps, floatThreshold!.Value, burialThreshold!.Value, burialShare!.Value, RenderClasses, absolute, terrainSample,
                                 emitPath, basePath, jsonPath, Instance, cfg.Identity(TerrainReview.Population, cidx.State?.Name ?? EnablePopulation.BaselineStateName), listN, sw,
                                 radiusOn, bosOn, forceEnable);
    }
    catch (Exception ex) { Console.Error.WriteLine($"contactprobe --terrain refused: {ex.GetType().Name}: {ex.Message}"); return 2; }
}
// The contact population rule: a host or a dependent that is a seed, or any placement either ever takes inside the region
// (an interior cell of the population, the 3x3 ring of a seed's exterior cell, a large delta placement's box). The ring is
// what keeps a row whose support another mod placed (or removed) beside the dependent: supports touch within a few units.
bool RefInPopulation(FormKey key) => population is null || population.Seeds.Contains(key)
    || (idx.History.Of(key) is { } hs && hs.Any(population.PlaceInScope));
string PluginName(int p) => p < 0 ? "(none)" : p == bosPlugin ? BosApplyReport.PseudoPluginName : pluginKey[p].FileName.String;
bool IsVanilla(int p) => p >= 0 && Vanilla.Contains(PluginName(p));
bool IsUssep(int p) => p >= 0 && BaseGamePlugins.IsUssep(PluginName(p));
string ClassOf(int p) => IsVanilla(p) ? "Bethesda" : IsUssep(p) ? "USSEP" : "mod";
string CellOf(FormKey c) => idx.CellName.TryGetValue(c, out var v) ? (v.Edid ?? c.ToString()) + (v.Interior ? " (int)" : " (ext)") : c.ToString();
string TypeOf(FormKey baseKey) => idx.BaseInfo.TryGetValue(baseKey, out var bi) ? bi.Type : "?";
string EdidOf(FormKey baseKey) => idx.BaseInfo.TryGetValue(baseKey, out var bi) && bi.Edid is { } e ? e : "";
string MeshOf(FormKey baseKey) => idx.BaseMesh.TryGetValue(baseKey, out var m) ? Path.GetFileName(m) : "";
string BaseLabel(FormKey baseKey) => $"{TypeOf(baseKey)} {EdidOf(baseKey)} <{MeshOf(baseKey)}>";
static float Rate(long hit, long total) => total == 0 ? 0 : 100f * hit / total;
string Src(ArmContact? a) => a is null ? "--" : $"{a.DepSrc}{a.HostSrc}";
string Contact(ArmContact? a) => a is null ? "(none within band)" : $"facing {a.Contact.FacingDistance:0.#}u (raw {a.Contact.Distance:0.#}u) pen {a.Contact.EffectivePenetration:0.#}u{(a.PreexistingDepth > 0f ? $" ({a.PreexistingDepth:0.#}u of it authored)" : "")}{(a.Contact.HostClosed ? "" : " (open host)")} up {a.Contact.NormalUp:0.00}";
void WriteGeomReport()
{
    if (geomReport is null || geomLines is null) return;
    Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(geomReport))!);
    File.WriteAllLines(geomReport, geomLines);
    Console.WriteLine($"wrote {geomLines.Count - 1:N0} mesh geometry lines -> {geomReport}");
}
string AgainstLabel(ArmContact? a) => a is null ? "" : $"{BaseLabel(cidx!.WinnerBase(a.Against))} {a.Against}";
string GeometryLine() => $"geometry: {meshesMeasured:N0} meshes measured (host soup collision {hostColl:N0} / render {hostRender:N0}; dependent soup collision {depColl:N0} / render {depRender:N0}; "
                       + $"no soup {meshNoSoup:N0}; incomplete collision decode {incompleteCollision:N0}; phantom-only {phantomOnly:N0}), {obndFallback:N0} bases on an OBND box (labelled 'o'), {noGeometry:N0} with nothing"
                       + $"; {BodilessLine()}";
// The placed lights, activators and doors whose base record has no model (ContactIndex.IsBodiless): not a dependent and not a
// host, counted once per run by type; the doors among them are no longer dependents without geometry (station AD).
(int Lights, int Activators, int Doors)? bodilessCount = null;
string BodilessLine()
{
    var (l, a, d) = bodilessCount ??= cidx?.BodilessReferences() ?? (0, 0, 0);
    return $"{l:N0} placed lights, {a:N0} activators and {d:N0} doors with no model: no body, not judged";
}

// ---------------------------------------------------------------------------------------
// --overlap-json: the milestone 6 ranker upgrade — 4.1's broad-phase hits through the narrow phase.
// ---------------------------------------------------------------------------------------
if (overlapJson is not null)
{
    try
    {
        int reviewRc = OverlapPairReview.Run(overlapJson, jsonPath!, contactBudgetSeconds, pairBudgetSeconds,
            contactMaxPairs, surfaceTolerance, meshMaxTrianglePairs, contactEvidence, assets, idx, cidx.Arms,
            cfg.Identity("load-time", cidx.State?.Name ?? "baseline"), idx.WinningGeometryPose, __run);
        // --volume: a separate file after the unchanged pair review (patch-miss packet, GATE-A §5).
        var motionCache = motionOptions is null ? null : new MotionReview.Cache(assets, idx, motionOptions);
        if (volumeOut is not null)
            OverlapVolumeReview.Run(overlapJson, volumeOut, assets, idx, cidx.Arms,
                cfg.Identity("load-time", cidx.State?.Name ?? "baseline"), idx.WinningGeometryPose, motionCache);
        // --surface: a separate file after the unchanged pair review and volume (patch-miss packet, session 6).
        if (surfaceOut is not null)
            OverlapSurfaceReview.Run(overlapJson, surfaceOut, livePose ?? LivePoseDefault.Policy, assets, idx, cidx.Arms,
                cfg.Identity("load-time", cidx.State?.Name ?? "baseline"), idx.WinningGeometryPose, motionCache,
                alphaVisibility ? new AlphaVisibility(assets, idx) : null);
        // --anchor: a separate file (G1 build list, Stage A, row 2): member A is the anchor, B the partner.
        if (anchorOut is not null)
            AnchorReview.Run(overlapJson, anchorOut, assets, idx, cidx.Arms,
                cfg.Identity("load-time", cidx.State?.Name ?? "baseline"), idx.WinningGeometryPose, motionCache);
        // --box-contact: a separate file (lane 3b): supplied world boxes (a particle region) against a member's render surface.
        if (boxContactOut is not null)
            BoxContactReview.Run(overlapJson, boxContactOut, assets, idx, cidx.Arms,
                cfg.Identity("load-time", cidx.State?.Name ?? "baseline"), idx.WinningGeometryPose, motionCache);
        return reviewRc;
    }
    catch (Exception ex)
    {
        Console.Error.WriteLine($"overlap contact review refused: {ex.GetType().Name}: {ex.Message}");
        return 2;
    }
}
// ---------------------------------------------------------------------------------------
// --labelled-mover: contact before vs after one plugin's moves — the labelled positive set for the primitive.
// ---------------------------------------------------------------------------------------
if (labelledMover is not null)
{
    int m = Array.FindIndex(pluginKey, k => string.Equals(k.FileName.String, labelledMover, StringComparison.OrdinalIgnoreCase));
    if (m < 0) { Console.Error.WriteLine($"--labelled-mover '{labelledMover}' is not in this order"); return 2; }
    Func<Snap, bool>? scope = interiorOnly ? s => s.Interior : null;
    var lskips = new ContactSkips();
    var before = cidx.PhysicalWithout(m, closure, scope, out var beforeIdx, lskips);
    long beforeDisabled = lskips.WinnerRuntimeDisabled, beforeUnresolved = lskips.WinnerEnableUnresolved;
    lskips.WinnerRuntimeDisabled = 0; lskips.WinnerEnableUnresolved = 0;
    var after = cidx.PhysicalWinners(scope, lskips);
    Console.WriteLine($"\n==== THE LABELLED SET — every ref {labelledMover} moved: contact BEFORE it vs AFTER it (ε {eps:0.#}u{(interiorOnly ? ", interiors only" : "")}) ====");
    Console.WriteLine($"   arms: {before.Count:N0} physical refs placed without the mover, {after.Count:N0} under the winner (any size — R3); left out as disabled at runtime (R1): {beforeDisabled:N0} / {lskips.WinnerRuntimeDisabled:N0}, chain unresolved {beforeUnresolved:N0} / {lskips.WinnerEnableUnresolved:N0}");
    string Class(ArmContact? a) => a is null ? "floating" : a.Contact.EffectivePenetration > eps ? "sunk" : a.Contact.FacingDistance <= eps ? "touching" : "floating";
    var conf = new Dictionary<(string Motion, string Before, string After), int>();
    var confSmall = new Dictionary<(string Before, string After), int>();
    var confVertical = new Dictionary<(string Before, string After), int>();
    var confVerticalFixed = new Dictionary<(string Before, string After), int>();
    int tested = 0, movedFar = 0, verticalOnly = 0;
    var rowsOut = new List<object>();
    var examples = new List<(string Key, float Dz, string Text)>();
    int seen = 0;
    foreach (var (key, snaps) in idx.History.Refs)
    {
        var win = snaps[^1];
        if (win.Plugin != m || win.Disabled || win.Deleted || idx.IsDeletedNow(key)) continue;
        if (!beforeIdx.TryGetValue(key, out var bi)) continue;
        var bs = snaps[bi];
        if (bs.Space != win.Space) continue;
        if (scope is not null && !scope(win)) continue;
        var wb = cidx.WinnerBase(key); var bb = cidx.ArmBase(key, m, closure);
        if (bb.IsNull || !cidx.IsDependentType(wb)) continue;
        var winBox = cidx.WorldBox(wb, win); var befBox = cidx.WorldBox(bb, bs);
        if (winBox.IsEmpty || befBox.IsEmpty) continue;
        if (ContactIndex.ScaleOf(winBox) == OverlapScale.Architecture) continue;
        if (++seen % 500 == 0) Console.WriteLine($"   ... {seen:N0} moved refs, {sw.Elapsed.TotalSeconds:0}s");
        var cb = cidx.BestContact(key, bi, bb, bs, befBox, before, k => beforeIdx.TryGetValue(k, out var i) ? (idx.History.Of(k)![i], i) : null, (k, _) => cidx.ArmBase(k, m, closure), out _);
        var ca = cidx.BestContact(key, snaps.Count - 1, wb, win, winBox, after, cidx.WinnerPlacement, (k, _) => cidx.WinnerBase(k), out _);
        tested++;
        float dx = win.Pos.X - bs.Pos.X, dy = win.Pos.Y - bs.Pos.Y, dz = win.Pos.Z - bs.Pos.Z;
        float dxy = MathF.Sqrt(dx * dx + dy * dy);
        bool far = PlacementHistory.Dist(bs.Pos, win.Pos) > PlacementHistory.MoveThreshold;
        if (far) movedFar++;
        bool vertical = dxy < 1f && MathF.Abs(dz) >= 1f;
        if (vertical) verticalOnly++;
        var motion = cidx.MotionOf(wb).ToString();
        string b = Class(cb), a = Class(ca);
        conf[(motion, b, a)] = conf.GetValueOrDefault((motion, b, a)) + 1;
        if (!far) confSmall[(b, a)] = confSmall.GetValueOrDefault((b, a)) + 1;
        if (vertical) { confVertical[(b, a)] = confVertical.GetValueOrDefault((b, a)) + 1; if (motion == "Fixed") confVerticalFixed[(b, a)] = confVerticalFixed.GetValueOrDefault((b, a)) + 1; }
        rowsOut.Add(new { key = key.ToString(), @base = BaseLabel(wb), cell = CellOf(win.Cell), motion, dx, dy, dz, far, before = b, after = a,
                          beforeDist = cb?.Contact.FacingDistance, beforePen = cb?.Contact.EffectivePenetration, beforeUp = cb?.Contact.NormalUp, beforeAgainst = cb?.Against.ToString(), beforeAgainstBase = AgainstLabel(cb), beforeSrc = Src(cb),
                          afterDist = ca?.Contact.FacingDistance, afterPen = ca?.Contact.EffectivePenetration, afterUp = ca?.Contact.NormalUp, afterAgainst = ca?.Against.ToString(), afterAgainstBase = AgainstLabel(ca), src = Src(ca) });
        if (examples.Count < 20000) examples.Add(($"{b}->{a}", dz, $"{BaseLabel(wb)} in {CellOf(win.Cell)} moved ({dx:0.#},{dy:0.#},{dz:0.#})  before {Contact(cb)} vs {AgainstLabel(cb)} [{Src(cb)}]  after {Contact(ca)} vs {AgainstLabel(ca)} [{Src(ca)}]  {key}"));
    }
    Console.WriteLine($"   refs tested: {tested:N0} (clutter- and furniture-scale, placed in both arms, same space); {movedFar:N0} moved over {PlacementHistory.MoveThreshold:0}u, {verticalOnly:N0} moved vertically only");
    var classes = new[] { "touching", "floating", "sunk" };
    void Confusion(string title, Func<string, string, int> get)
    {
        Console.WriteLine($"\n   {title}");
        Console.WriteLine($"   {"before \\ after",-18} " + string.Join(" ", classes.Select(c => $"{c,10}")) + $" {"total",8}");
        foreach (var b in classes)
            Console.WriteLine($"   {b,-18} " + string.Join(" ", classes.Select(a => $"{get(b, a),10:N0}")) + $" {classes.Sum(a => get(b, a)),8:N0}");
        int broken = classes.Where(b => b != "touching").Sum(b => classes.Sum(a => get(b, a)));
        int fixedNow = classes.Where(b => b != "touching").Sum(b => get(b, "touching"));
        int unbroken = classes.Where(a => a != "touching").Sum(a => get("touching", a));
        int okBefore = classes.Sum(a => get("touching", a));
        Console.WriteLine($"   agreement: of {broken:N0} floating/sunk before, {fixedNow:N0} touch after ({Rate(fixedNow, broken):0.0}%); of {okBefore:N0} touching before, {unbroken:N0} float/sink after ({Rate(unbroken, okBefore):0.0}%)");
    }
    Confusion("all tested refs", (b, a) => conf.Where(k => k.Key.Before == b && k.Key.After == a).Sum(k => k.Value));
    foreach (var mo in new[] { "Fixed", "Movable", "Unknown" })
        Confusion($"{mo} dependents", (b, a) => conf.GetValueOrDefault((mo, b, a)));
    Confusion($"moves of {PlacementHistory.MoveThreshold:0}u or less (the nudges driftprobe calls jitter)", (b, a) => confSmall.GetValueOrDefault((b, a)));
    Confusion("VERTICAL-ONLY moves (under 1 u sideways, 1 u or more up or down) — the shape a floating or sunken fix takes; the cleanest labelled subset", (b, a) => confVertical.GetValueOrDefault((b, a)));
    Confusion("...vertical-only, Fixed dependents", (b, a) => confVerticalFixed.GetValueOrDefault((b, a)));
    // Most of USSEP's vertical nudges are smaller than ε, and a float smaller than ε reads "touching" on both sides of
    // the fix. So the same subset is read again at an ε matched to the nudge, and threshold-free: did the fix bring the
    // thing CLOSER to a perfect contact (the larger of its gap and its penetration shrank)?
    var vrows = rowsOut.Select(o => (dynamic)o).Where(r => MathF.Sqrt((float)r.dx * (float)r.dx + (float)r.dy * (float)r.dy) < 1f && MathF.Abs((float)r.dz) >= 1f).ToList();
    static string ClassAt(float? dist, float? pen, float e) => dist is null ? "floating" : pen > e ? "sunk" : dist <= e ? "touching" : "floating";
    foreach (var e in new[] { 1f, 2f })
    {
        var cv = new Dictionary<(string, string), int>();
        foreach (var r in vrows)
        {
            string b = ClassAt((float?)r.beforeDist, (float?)r.beforePen, e), a = ClassAt((float?)r.afterDist, (float?)r.afterPen, e);
            cv[(b, a)] = cv.GetValueOrDefault((b, a)) + 1;
        }
        Confusion($"...vertical-only, read at ε = {e:0}u (the size of the nudge, not the rule's ε)", (b, a) => cv.GetValueOrDefault((b, a)));
    }
    static float Score(float? dist, float? pen) => dist is null ? 128f : MathF.Max((float)dist, (float)(pen ?? 0f));
    int closer = 0, further = 0, same = 0;
    foreach (var r in vrows)
    {
        float sb = Score((float?)r.beforeDist, (float?)r.beforePen), sa = Score((float?)r.afterDist, (float?)r.afterPen);
        if (sa < sb - 0.25f) closer++; else if (sa > sb + 0.25f) further++; else same++;
    }
    Console.WriteLine($"\n   vertical-only, threshold-free: the deviation from a perfect contact (the larger of gap and penetration) shrank for {closer:N0} of {vrows.Count:N0} ({Rate(closer, vrows.Count):0.0}%), grew for {further:N0} ({Rate(further, vrows.Count):0.0}%), stayed within 0.25 u for {same:N0}");
    Console.WriteLine($"\n   vertical-only moves by size: " + string.Join(", ", new[] { (0f, 2f), (2f, 4f), (4f, 8f), (8f, 16f), (16f, 1e9f) }.Select(band =>
        $"{band.Item1:0}–{(band.Item2 > 1e8 ? "" : band.Item2.ToString("0"))}u {rowsOut.Count(o => { dynamic r = o; return MathF.Sqrt(r.dx * r.dx + r.dy * r.dy) < 1f && MathF.Abs(r.dz) >= 1f && MathF.Abs(r.dz) >= band.Item1 && MathF.Abs(r.dz) < band.Item2; }):N0}")));
    foreach (var cls in new[] { "floating->touching", "sunk->touching", "floating->floating", "sunk->sunk", "touching->floating", "touching->sunk" })
    {
        Console.WriteLine($"\n   ---- {cls}: {Math.Min(listN, 8)} examples ----");
        foreach (var e in examples.Where(e => e.Key == cls).Take(Math.Min(listN, 8))) Console.WriteLine($"   {e.Text}");
    }
    if (jsonPath is not null)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(jsonPath))!);
        File.WriteAllText(jsonPath, cidx.Arms.StampForced(JsonSerializer.Serialize(new { mover = labelledMover, eps, tested, movedFar, verticalOnly, rows = rowsOut }, new JsonSerializerOptions { WriteIndented = false, NumberHandling = System.Text.Json.Serialization.JsonNumberHandling.AllowNamedFloatingPointLiterals })));
        Console.WriteLine($"\nwrote {jsonPath}");
    }
    Console.WriteLine($"\n{GeometryLine()}");
    WriteGeomReport();
    Console.WriteLine($"elapsed {sw.Elapsed.TotalSeconds:0.0}s");
    return 0;
}

// ---------------------------------------------------------------------------------------
// --absolute: every clutter- and furniture-scale ref against its neighbours as shipped.
// ---------------------------------------------------------------------------------------
if (absolute)
{
    Func<Snap, bool>? scope = interiorOnly ? s => s.Interior : null;
    var after = cidx.PhysicalWinners(scope);
    Console.WriteLine($"\n==== THE ABSOLUTE FLOOR — every clutter- and furniture-scale ref against everything physical near it, as shipped (ε {eps:0.#}u{(interiorOnly ? ", interiors only" : "")}) ====");
    var tally = new Dictionary<(string Domain, string Motion, string Scale, string Class), int>();
    var cityTally = new Dictionary<(string City, string Class), int>();
    var meshTally = new Dictionary<(string Mesh, string Class), int>();
    string[] cities = { "Whiterun", "Solitude", "Riften", "Windhelm", "Markarth", "Falkreath", "Morthal", "Dawnstar", "Winterhold" };
    int tested = 0, seen = 0;
    var floating = new List<(float Dist, string Text)>();
    var sunk = new List<(float Pen, string Text)>();
    foreach (var (key, snaps) in idx.History.Refs)
    {
        var win = snaps[^1];
        if (win.Disabled || win.Deleted || idx.IsDeletedNow(key)) continue;
        if (scope is not null && !scope(win)) continue;
        var b = cidx.WinnerBase(key);
        if (!cidx.IsDependentType(b)) continue;
        var box = cidx.WorldBox(b, win);
        if (box.IsEmpty) continue;
        var scale = ContactIndex.ScaleOf(box);
        if (scale == OverlapScale.Architecture) continue;
        if (++seen % 20000 == 0) Console.WriteLine($"   ... {seen:N0} refs, {sw.Elapsed.TotalSeconds:0}s");
        var c = cidx.BestContact(key, snaps.Count - 1, b, win, box, after, cidx.WinnerPlacement, (k, _) => cidx.WinnerBase(k), out _);
        tested++;
        string cls = c is null ? "floating" : c.Contact.EffectivePenetration > eps ? "sunk" : c.Contact.FacingDistance <= eps ? "touching" : "floating";
        string domain = win.Interior ? "interior" : "exterior";
        string motion = cidx.MotionOf(b).ToString();
        tally[(domain, motion, scale.ToString(), cls)] = tally.GetValueOrDefault((domain, motion, scale.ToString(), cls)) + 1;
        var cellName = idx.CellName.TryGetValue(win.Cell, out var cn) ? cn.Edid ?? "" : "";
        var city = cities.FirstOrDefault(ci => cellName.StartsWith(ci, StringComparison.OrdinalIgnoreCase));
        if (city is not null && win.Interior) cityTally[(city, cls)] = cityTally.GetValueOrDefault((city, cls)) + 1;
        var mesh = MeshOf(b);
        meshTally[(mesh, cls)] = meshTally.GetValueOrDefault((mesh, cls)) + 1;
        if (cls == "floating" && c is not null && floating.Count < 50000) floating.Add((c.Contact.FacingDistance, $"{BaseLabel(b)} in {CellOf(win.Cell)} {Contact(c)} [{Src(c)}] {key}"));
        if (cls == "sunk" && sunk.Count < 50000) sunk.Add((c!.Contact.EffectivePenetration, $"{BaseLabel(b)} in {CellOf(win.Cell)} {Contact(c)} [{Src(c)}] {key}"));
    }
    var classes = new[] { "touching", "floating", "sunk" };
    Console.WriteLine($"   refs tested: {tested:N0}");
    void Table(string title, Func<string, int> get)
    {
        int tot = classes.Sum(get);
        Console.WriteLine($"   {title,-40} " + string.Join(" ", classes.Select(c => $"{c} {get(c),8:N0} ({Rate(get(c), tot),5:0.0}%)")) + $"   of {tot:N0}");
    }
    foreach (var d in new[] { "interior", "exterior" })
    {
        Console.WriteLine($"\n   ---- {d} ----");
        Table("all", c => tally.Where(k => k.Key.Domain == d && k.Key.Class == c).Sum(k => k.Value));
        foreach (var mo in new[] { "Fixed", "Movable", "Unknown" })
            Table($"{mo}", c => tally.Where(k => k.Key.Domain == d && k.Key.Motion == mo && k.Key.Class == c).Sum(k => k.Value));
        foreach (var sc in new[] { "Clutter", "Furniture" })
            Table($"{sc}-scale", c => tally.Where(k => k.Key.Domain == d && k.Key.Scale == sc && k.Key.Class == c).Sum(k => k.Value));
        foreach (var mo in new[] { "Fixed", "Movable" })
            foreach (var sc in new[] { "Clutter", "Furniture" })
                Table($"{mo} {sc}-scale", c => tally.GetValueOrDefault((d, mo, sc, c)));
    }
    Console.WriteLine($"\n   ---- city interiors (cell editor id prefix) ----");
    foreach (var ci in cities) Table(ci, c => cityTally.GetValueOrDefault((ci, c)));
    Console.WriteLine($"\n   ---- meshes most often floating / sunk (what the floor is made of) ----");
    foreach (var cls in new[] { "floating", "sunk" })
    {
        Console.WriteLine($"   {cls}:");
        foreach (var g in meshTally.Where(k => k.Key.Class == cls).OrderByDescending(k => k.Value).Take(Math.Min(listN, 15)))
        {
            int tot = classes.Sum(c => meshTally.GetValueOrDefault((g.Key.Mesh, c)));
            Console.WriteLine($"      {g.Value,7:N0} of {tot,7:N0} ({Rate(g.Value, tot),5:0.0}%)  {g.Key.Mesh}");
        }
    }
    Console.WriteLine($"\n   ---- floating, by gap (largest first — what 'floating' looks like at the top) ----");
    foreach (var f in floating.OrderByDescending(f => f.Dist).Take(Math.Min(listN, 12))) Console.WriteLine($"   {f.Text}");
    Console.WriteLine($"   ---- floating, by gap (smallest first — what a near miss looks like) ----");
    foreach (var f in floating.OrderBy(f => f.Dist).Take(Math.Min(listN, 12))) Console.WriteLine($"   {f.Text}");
    Console.WriteLine($"   ---- sunk, deepest first ----");
    foreach (var s in sunk.OrderByDescending(s => s.Pen).Take(Math.Min(listN, 12))) Console.WriteLine($"   {s.Text}");
    Console.WriteLine($"\n{GeometryLine()}");
    Console.WriteLine($"Limits: 'floating' means no physical reference within ε — terrain is not a reference, so exterior refs on the ground read floating; interiors are the floor (the terrain's own floor is --terrain --absolute).");
    Console.WriteLine($"elapsed {sw.Elapsed.TotalSeconds:0.0}s");
    return 0;
}

// ---------------------------------------------------------------------------------------
// The differential pass — every moved host, every dependent within reach, two arms.
// ---------------------------------------------------------------------------------------
var skips = new ContactSkips();
// --trace: attach the sink for the whole pass (keyed with --trace-keys, else counting mode); it never changes a verdict.
ProbeTraceCollector? traceSink = traceOut is null ? null : new ProbeTraceCollector(traceKeys);
cidx.Trace = traceSink;
MemStages.Print("index", cidx, 0, 0);
if (MemStages.On) cidx.StageProbe = (stage, refs) => { if (refs % 100_000 == 0) MemStages.Print($"{stage}, {refs:N0} refs", cidx, 0, 0); else MemStages.Light($"{stage}, {refs:N0} refs"); };
MemStages.Extra = () => $"archive tables read {archiveScope?.TablesRead ?? 0:N0}; mesh reads {readCount:N0} ({readBytes / 1e9:0.000} GB of files, {readAlloc / 1e9:0.000} GB allocated), decodes allocated {decodeAlloc / 1e9:0.000} GB, process total allocated {GC.GetTotalAllocatedBytes() / 1e9:0.000} GB";
var hosts = cidx.MovedHosts(skips, closure);
Console.WriteLine($"\n==== 1. PLUMBING ====");
Console.WriteLine($"   hosts (physical, furniture-scale or larger, whose last placement step is a move, a relocation or a removal): {hosts.Count:N0}");
foreach (var ch in Enum.GetValues<HostChange>())
    Console.WriteLine($"      {ch,-10} {hosts.Count(h => h.Change == ch),8:N0}   by mover class: " + string.Join(", ", new[] { "Bethesda", "USSEP", "mod" }.Select(c => $"{c} {hosts.Count(h => h.Change == ch && ClassOf(h.Mover) == c):N0}"))
                    + (ch == HostChange.Removed ? $"   (of which through an enable parent a later plugin disabled — R1: {hosts.Count(h => h.RemovedViaParent):N0})" : ""));
Console.WriteLine($"      set aside (rule 6): {skips.HostNoGeometry:N0} without geometry, {skips.HostNoCollision:N0} without solid collision (nothing rests on them in the engine either), {skips.HostVolume:N0} volume-scale, {skips.HostTooSmall:N0} under {cidx.MinHostDim:0}u (clutter that moved — a dependent, not a host)");
Console.WriteLine($"      runtime state (R1): {skips.HostRuntimeDisabled:N0} hosts disabled at runtime in the arm without their mover (never there to hold anything), {skips.HostEnableUnresolved:N0} whose enable chain the order cannot follow");
Console.WriteLine($"   top movers: " + string.Join(", ", hosts.GroupBy(h => h.Mover).OrderByDescending(g => g.Count()).Take(10).Select(g => $"{PluginName(g.Key)} {g.Count():N0}")));
{
    var (flagOnly, nameOnly) = cidx.AuditMarkers();
    Console.WriteLine($"   marker rule (R2): {idx.BaseIsMarker.Count:N0} bases carry the Is-Marker flag; the old name heuristics disagree on {flagOnly.Count:N0} flagged bases with ordinary names"
                    + $" and {nameOnly.Count:N0} named 'marker' (or MarkerX*.nif) without the flag — reported, not used");
    Console.WriteLine($"      flag only, by type: " + string.Join(", ", flagOnly.GroupBy(k => TypeOf(k)).OrderByDescending(g => g.Count()).Take(8).Select(g => $"{g.Key} {g.Count():N0}"))
                    + $"; e.g. " + string.Join("; ", flagOnly.Take(6).Select(k => $"{BaseLabel(k)} {k}")));
    Console.WriteLine($"      name only, by type: " + string.Join(", ", nameOnly.GroupBy(k => TypeOf(k)).OrderByDescending(g => g.Count()).Take(8).Select(g => $"{g.Key} {g.Count():N0}"))
                    + $"; e.g. " + string.Join("; ", nameOnly.Take(6).Select(k => $"{BaseLabel(k)} {k}")));
}
if (emptySpaceOut is not null)
    return EmptySpaceReview.Run(cidx, closure, hosts, PluginName, BaseLabel, CellOf, p => IsVanilla(p) || IsUssep(p), f => Vanilla.Contains(f),
                                EmptySpaceOutput.NodeCheck(idx, assets), emptySpaceOut, emitPath, basePath, Instance, paths.Count - unreadable.Count, unreadable.Count,
                                cfg.Identity(population?.Identity("load-time") ?? "load-time", cidx.State?.Name ?? EnablePopulation.BaselineStateName), listN, sw,
                                radiusOn, bosOn, forceEnable, population, populationAuditEmit, populationAuditOut);
var depHash = cidx.DependentSnaps(skips);
MemStages.Print("dependents hashed", cidx, 0, 0);
if (FitCheck() is { } fitRefusal) { Console.Error.WriteLine("contactprobe: " + fitRefusal); return 2; }
Func<Snap, bool>? winScope = null;
var winHash = cidx.PhysicalWinners(winScope, skips);
MemStages.Print("winner arm hashed", cidx, 0, 0);
Console.WriteLine($"   dependents hashed: {depHash.Count:N0} undeleted snaps of clutter- and furniture-scale physical refs — the arm decides which exist at runtime ({skips.DepNoGeometry:N0} winning refs without geometry, {skips.DepVolume:N0} over {cidx.MaxDependentDim:0}u set aside)");
// The refs left out as no body (ContactIndex.IsBodiless), by type: no coverage key, so they are counted here (station AD).
Console.WriteLine($"   no body, not judged: dependents {skips.DepBodilessLight:N0} lights, {skips.DepBodilessActivator:N0} activators, {skips.DepBodilessDoor:N0} doors with no model; moved hosts {skips.HostBodilessLight:N0} lights, {skips.HostBodilessActivator:N0} activators, {skips.HostBodilessDoor:N0} doors with no model");
Console.WriteLine($"   winner arm: {winHash.Count:N0} physical refs placed, undeleted and enabled at runtime under the winner, any size (R3); left out: {skips.WinnerRuntimeDisabled:N0} disabled at runtime (R1), {skips.WinnerEnableUnresolved:N0} with an unresolved chain  ({sw.Elapsed.TotalSeconds:0.0}s)");

// The emit's coverage lines fixed before the host loop: a --reverse-of baseline must carry these equal to this run's own.
List<CoverageLine> PreLoopLines() => new()
{
    new("plugins", paths.Count - unreadable.Count, new Dictionary<string, long> { ["UnreadablePlugin"] = unreadable.Count }),
    // A host without solid collision is SET ASIDE by definition — the engine rests nothing on it either (the plumbing line
    // above says the same) — not unjudgeable: the coverage rule's first real case (2026-09-03, the lab's JK's–OCW patch, which
    // moves ~490 more collision-less hosts than the order without it) refused its verdict on this line until it was classed so.
    // A host whose mesh could not be read at all stays unjudgeable.
    new("moved hosts", hosts.Count,
        new Dictionary<string, long> { ["NoGeometry"] = skips.HostNoGeometry, ["EnableChainUnresolved"] = skips.HostEnableUnresolved },
        new Dictionary<string, long> { ["NoSolidCollision"] = skips.HostNoCollision, ["VolumeScale"] = skips.HostVolume, ["TooSmallToHost"] = skips.HostTooSmall, ["RuntimeDisabledInArm"] = skips.HostRuntimeDisabled }),
    new("dependent snaps hashed", depHash.Count, new Dictionary<string, long> { ["NoGeometry"] = skips.DepNoGeometry }, new Dictionary<string, long> { ["OverDependentSize"] = skips.DepVolume }),
    new("winner-arm physical refs", winHash.Count, new Dictionary<string, long> { ["EnableChainUnresolved"] = skips.WinnerEnableUnresolved }, new Dictionary<string, long> { ["RuntimeDisabled"] = skips.WinnerRuntimeDisabled }),
};

// --base: every record of the named emit re-measured under this arm (2026-09-03); a full run and --reverse-of share it.
List<Predicate> MeasureBasePredicates(string path)
{
    var baseFile = ConflictIo.Read(path);
    var list = new List<Predicate>();
    foreach (var c in baseFile.Conflicts)
    {
        string? other = c.Alternative is { Count: > 0 } alt ? alt[0].FormKey : null;
        foreach (var rec in c.Necessary)
        {
            if (c.Kind != "CONTACT") { list.Add(new Predicate(c.Kind, c.A, c.B, rec.FormKey, other, PhysicalState.Unmeasured, "not a contact row")); continue; }
            var (st, why) = ContactPredicate(FormKey.Factory(rec.FormKey));
            list.Add(new Predicate(c.Kind, c.A, c.B, rec.FormKey, other, st, why));
        }
    }
    Console.WriteLine($"\n   --base {Path.GetFileName(path)}: {list.Count:N0} base-row predicate(s) re-measured under this order — "
                    + string.Join(", ", list.GroupBy(p => p.Physical).OrderBy(g => g.Key).Select(g => $"{g.Key} {g.Count():N0}")));
    return list;
}

string sweepPopulation = population?.Identity("load-time") ?? "load-time";
string sweepState = cidx.State?.Name ?? EnablePopulation.BaselineStateName;
string sweepPolicy = ContactCredit.SweepPolicy(eps, reach, radiusOn, bosOn, allRows, forceEnable);
// --reverse-of: the baseline order's rows and coverage are the baseline emit's, once it is shown to be this run's own; only the
// --base rows are measured, with the code a full reverse uses.
if (reverseOfPath is not null)
{
    var baseline = ConflictIo.Read(reverseOfPath);
    var identity = cfg.Identity(sweepPopulation, sweepState);
    string? refusal = ReverseFromBaseline.Refusal("contactprobe", baseline, reverseOfPath, Instance, identity, sweepPopulation, sweepState,
                                                  sweepPolicy, null, PreLoopLines());
    if (refusal is not null) { Console.Error.WriteLine(refusal); return 2; }
    var carried = MeasureBasePredicates(basePath!);
    ConflictIo.Write(emitPath!, new ConflictFile("contactprobe", Instance, DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm:ssZ"), baseline.Conflicts,
                                                 baseline.Coverage, carried, basePath, sweepPopulation, sweepState, identity, MeasurementPolicy: sweepPolicy));
    Console.WriteLine($"configuration: {identity.Short}");
    Console.WriteLine($"\nreverse of {Path.GetFileName(reverseOfPath)}: its {baseline.Conflicts.Count:N0} Fixed Stranded + Orphaned conflicts and coverage "
                    + $"carried (no host judged), {carried.Count:N0} --base predicate(s) measured -> {emitPath}");
    Console.WriteLine($"elapsed {sw.Elapsed.TotalSeconds:0.0}s");
    return 0;
}

var rows = new List<ContactRow>();
int done = 0, hostsOutsidePopulation = 0, rowsOutsidePopulation = 0;
long hostLoop0 = Stopwatch.GetTimestamp();
var orderedHosts = hosts.OrderBy(h => h.Previous.Space.ToString()).ThenBy(h => h.Previous.Cell.ToString()).ToList();
// A host is judged only when one of the dependents Judge would consider (the same search) is in the population.
bool HostOutsidePopulation(MovedHost h)
{
    if (population is null || RefInPopulation(h.Key)) return false;
    var hb = cidx.WorldBox(h.PrevBase, h.Previous);
    return !(!hb.IsEmpty && depHash.Near(h.Previous.Space, hb, cidx.Reach).Any(d => RefInPopulation(d.Item.Key)));
}
void TakeHostRows(List<ContactRow> judged)
{
    if (population is not null)
    {
        int before = judged.Count;
        judged = judged.Where(r => RefInPopulation(r.Host.Key) || RefInPopulation(r.Dependent)).ToList();
        rowsOutsidePopulation += before - judged.Count;
    }
    rows.AddRange(judged);
    if (++done % 2000 == 0) Console.WriteLine($"   ... {done:N0}/{hosts.Count:N0} hosts, {rows.Count:N0} rows, {sw.Elapsed.TotalSeconds:0}s");
    if (done % 500 == 0) MemStages.Print("host loop", cidx, rows.Count, done);
}
if (threads <= 1)
{
    foreach (var h in orderedHosts)
    {
        if (HostOutsidePopulation(h)) { hostsOutsidePopulation++; done++; continue; }
        TakeHostRows(cidx.Judge(h, depHash, winHash, closure, skips, keepNoRelation: allRows));
    }
}
else
{
    // Workers judge hosts in any order; each host's rows, counters and trace events are taken strictly in host order.
    Console.WriteLine($"   host loop on {threads} threads");
    var outside = orderedHosts.Select(HostOutsidePopulation).ToArray();
    var judgedAt = Enumerable.Range(0, orderedHosts.Count).Where(i => !outside[i]).ToList();
    var reachFloors = cidx.HostReachFloors(judgedAt.Select(i => orderedHosts[i]).ToList(), depHash, closure);
    var finished = new (List<ContactRow> Rows, ContactSkips Skips, List<TraceEvent>? Trace, Dictionary<(string Role, string Outcome, string Rule), long>? TraceCounts)?[orderedHosts.Count];
    int nextToTake = 0;
    var takeGate = new object();
    void TakeFinished()
    {
        for (; nextToTake < finished.Length; nextToTake++)
        {
            if (outside[nextToTake]) { hostsOutsidePopulation++; done++; continue; }
            if (finished[nextToTake] is not { } f) return;
            finished[nextToTake] = null;
            skips.Add(f.Skips);
            if (f.Trace is not null) foreach (var ev in f.Trace) traceSink!.Emit(ev);
            if (f.TraceCounts is not null) foreach (var (k, n) in f.TraceCounts) traceSink!.Counts[k] = traceSink.Counts.GetValueOrDefault(k) + n;
            TakeHostRows(f.Rows);
        }
    }
    try
    {
        System.Threading.Tasks.Parallel.ForEach(
            System.Collections.Concurrent.Partitioner.Create(Enumerable.Range(0, judgedAt.Count), System.Collections.Concurrent.EnumerablePartitionerOptions.NoBuffering),
            new System.Threading.Tasks.ParallelOptions { MaxDegreeOfParallelism = threads },
            j =>
            {
                var hostSkips = new ContactSkips();
                // Counting mode stores no events, so a host waiting its turn holds only its counts.
                var traceBuffer = traceSink is { CountOnly: false } ? new List<TraceEvent>() : null;
                var traceCounts = traceSink is { CountOnly: true } ? new Dictionary<(string Role, string Outcome, string Rule), long>() : null;
                var judged = cidx.Judge(orderedHosts[judgedAt[j]], depHash, winHash, closure, hostSkips, allRows, reachFloors[j], traceBuffer, traceCounts);
                lock (takeGate) { finished[judgedAt[j]] = (judged, hostSkips, traceBuffer, traceCounts); TakeFinished(); }
            });
    }
    catch (AggregateException ex) when (ex.InnerExceptions.Count > 0)
    {
        System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(ex.InnerExceptions[0]).Throw();
    }
    TakeFinished();
    cidx.MergeThreadTimers();
}
double hostLoopSeconds = Stopwatch.GetElapsedTime(hostLoop0).TotalSeconds;
Console.WriteLine($"   pairs within the box band: {rows.Count + skips.BoxesApart:N0} tested; set aside: {skips.BoxesApart:N0} boxes further apart than {cidx.BoxBand:0}u, "
                + $"{skips.DepIntroducedByMover:N0} dependents the mover introduced, {skips.DepOtherSpace:N0} in another space, "
                + $"{skips.DepAbsentInArm:N0} dependents disabled at runtime in the arm without the mover (R1), {skips.DepEnableUnresolved:N0} with an unresolved chain  ({sw.Elapsed.TotalSeconds:0.0}s)");
Console.WriteLine($"   {GeometryLine()}");
Console.WriteLine($"   {cidx.Budget.Report()}");
if (boundedGeometry) Console.WriteLine($"   decoded meshes: {meshReader.Decodes - meshReader.Redecodes:N0} first decodes, {meshReader.Redecodes:N0} decoded again after eviction from the budget");
if (cidx.LargestHostGrid is { } lg)
    Console.WriteLine($"   largest host grid: {lg.Key.Key} snap {lg.Key.Snap}, base {lg.Key.Base} {BaseLabel(lg.Key.Base)}, {lg.Triangles:N0} triangles, {lg.Bytes / 1e9:0.000} GB");
if (Environment.GetEnvironmentVariable("HC_CACHEACCURACY") is not null)
{
    // Diagnostic: how close the counted bytes are to what dropping the caches frees (compacting full collections either side).
    static long Live() { System.Runtime.GCSettings.LargeObjectHeapCompactionMode = System.Runtime.GCLargeObjectHeapCompactionMode.CompactOnce; GC.Collect(2, GCCollectionMode.Forced, true, true); GC.WaitForPendingFinalizers(); GC.Collect(2, GCCollectionMode.Forced, true, true); return GC.GetTotalMemory(false); }
    long counted = cidx.Budget.Bytes, before = Live();
    cidx.Budget.EvictAll();
    long after = Live();
    Console.WriteLine($"   [cacheaccuracy] counted {counted / 1e9:0.000} GB, freed by dropping the caches {(before - after) / 1e9:0.000} GB (live {before / 1e9:0.000} -> {after / 1e9:0.000} GB), counted/freed {(before > after ? (double)counted / (before - after) : double.NaN):0.000}");
    GC.KeepAlive(rows);
}
StaleBaseReport();
var mstt = idx.BaseInfo.Where(kv => kv.Value.Type == "MoveableStatic").Select(kv => cidx.Summary(kv.Key)).Where(g => g is not null).ToList();
var motions = mstt.SelectMany(g => g!.SolidBodies.Select(b => b.MotionSystem == "" ? "(unread)" : b.MotionSystem)).GroupBy(x => x).OrderByDescending(g => g.Count()).ToList();
Console.WriteLine($"   MoveableStatic bases with geometry: {mstt.Count:N0}; their solid bodies' motion systems: " + string.Join(", ", motions.Select(g => $"{g.Key} {g.Count():N0}"))
                + $"; classified movable {idx.BaseInfo.Count(kv => kv.Value.Type == "MoveableStatic" && cidx.MotionOf(kv.Key) == DependentMotion.Movable):N0}, fixed {idx.BaseInfo.Count(kv => kv.Value.Type == "MoveableStatic" && cidx.MotionOf(kv.Key) == DependentMotion.Fixed):N0}");

var rel = rows.Where(r => r.Relation != ContactRelation.None).ToList();
var near = rows.Where(r => r.Relation == ContactRelation.None && r.Verdict == ContactVerdict.NoRelation).ToList();
var unj = rows.Where(r => r.Verdict == ContactVerdict.Unjudgeable).ToList();
DependencyPopulation Pop(ContactRow r) => DetectorModel.ClassifyDependencyPopulation(IsVanilla(r.Host.Mover), IsVanilla(r.DepAuthor), IsUssep(r.Host.Mover));
bool Fixed(ContactRow r) => r.Motion == DependentMotion.Fixed;
bool IsClass(ContactRow r) => r.Verdict is ContactVerdict.Stranded or ContactVerdict.Orphaned;
bool Interior(ContactRow r) => r.DepAuthoring.Interior;
float RankOf(ContactRow r) => r.Verdict == ContactVerdict.Sunk ? r.Facts.WinnerPenetration : r.Facts.WinnerDistance;

Console.WriteLine($"\n==== 2. RELATIONSHIPS AS AUTHORED — {rel.Count:N0} pairs in contact in the arm without the mover ({near.Count:N0} near misses within {cidx.NearBand:0}u kept for sensitivity; {unj.Count:N0} unjudgeable) ====");
foreach (var rr in new[] { ContactRelation.Supported, ContactRelation.Attached, ContactRelation.Hung })
    Console.WriteLine($"   {rr,-10} {rel.Count(r => r.Relation == rr),8:N0}   Fixed {rel.Count(r => r.Relation == rr && Fixed(r)),7:N0}  Movable {rel.Count(r => r.Relation == rr && r.Motion == DependentMotion.Movable),7:N0}  Unknown {rel.Count(r => r.Relation == rr && r.Motion == DependentMotion.Unknown),5:N0}"
                    + $"   interior {rel.Count(r => r.Relation == rr && Interior(r)),7:N0}  exterior {rel.Count(r => r.Relation == rr && !Interior(r)),7:N0}");
Console.WriteLine($"   geometry sources on the relationship rows (dependent, host): " + string.Join(", ", rel.GroupBy(r => Src(r.Authoring)).OrderByDescending(g => g.Count()).Select(g => $"{g.Key} {g.Count():N0}")));
Console.WriteLine($"   unjudgeable by reason: " + string.Join(", ", unj.GroupBy(r => r.Reason).Select(g => $"{g.Key} {g.Count():N0}")));

Console.WriteLine($"\n==== 3. VERDICTS over the relationship rows ====");
Console.WriteLine($"   {"verdict",-14} {"all",8} {"Fixed",8} {"Movable",8} {"Unknown",8} {"interior",9} {"exterior",9}   {"Fixed interior",15}");
foreach (var v in new[] { ContactVerdict.Stranded, ContactVerdict.Orphaned, ContactVerdict.Sunk, ContactVerdict.ReSupported, ContactVerdict.MovedTogether, ContactVerdict.Removed, ContactVerdict.Unjudgeable })
{
    var vr = rel.Where(r => r.Verdict == v).ToList();
    Console.WriteLine($"   {v,-14} {vr.Count,8:N0} {vr.Count(Fixed),8:N0} {vr.Count(r => r.Motion == DependentMotion.Movable),8:N0} {vr.Count(r => r.Motion == DependentMotion.Unknown),8:N0} {vr.Count(Interior),9:N0} {vr.Count(r => !Interior(r)),9:N0}   {vr.Count(r => Fixed(r) && Interior(r)),15:N0}");
}
Console.WriteLine($"   ReSupported by what: host at its new place {rel.Count(r => r.Verdict == ContactVerdict.ReSupported && r.Facts.WinnerContactIsHost):N0}, another reference {rel.Count(r => r.Verdict == ContactVerdict.ReSupported && !r.Facts.WinnerContactIsHost):N0}; "
                + $"Sunk into: the host {rel.Count(r => r.Verdict == ContactVerdict.Sunk && r.Facts.WinnerContactIsHost):N0}, something else {rel.Count(r => r.Verdict == ContactVerdict.Sunk && !r.Facts.WinnerContactIsHost):N0}");
// Stage 2 review F1 (depth parity): a reference whose authoring placement (the arm without the mover) is its winning placement, with the
// same base — for such a dependent and against-ref the authored depth and the winner depth are the same measurement, so the new depth
// must be 0 within float noise. Reported on each row; scored by closed-slabs/score_stage2.py.
// Diagnostic (K-R3' attribution, 2026-10-04): a reference's placement snaps and its base changes, by plugin, when its base was ever
// changed — the shape the (ref, snap) cache key could confuse before 6e9e397f. Null for a reference whose base never changed.
object? BaseStory(FormKey k)
{
    if (!idx.BaseChanges.TryGetValue(k, out var bc) || bc.Count < 2) return null;
    var hs = idx.History.Of(k);
    return new { Snaps = hs?.Select(x => PluginName(x.Plugin)).ToList(), Bases = bc.Select(c => $"{PluginName(c.Plugin)}: {BaseLabel(c.Base)}").ToList() };
}
bool Unmoved(FormKey k, int mover)
{
    var hs = idx.History.Of(k);
    if (hs is null || hs.Count == 0) return false;
    int ai = ContactIndex.AuthoringSnapIndex(hs, mover, closure);
    if (ai < 0 || hs[ai].Deleted) return false;
    var a = hs[ai]; var w = hs[^1];
    return a.SameAs(w) && a.Rot.Equals(w.Rot) && a.Scale == w.Scale && cidx.ArmBase(k, mover, closure) == cidx.WinnerBase(k);
}
bool PreOnly(ContactRow r) => r.Verdict == ContactVerdict.ReSupported && r.Facts.WinnerPreexistingOnly;
// The differential Sunk rule (closed-slabs lane, 2026-10-04): only depth beyond what the dependent already had in the same reference as
// authored can sink a row. A row whose only contact is such an overlap is counted apart (Gate A review F1): the rest of it may float.
Console.WriteLine($"   differential Sunk: {DetectorModel.PreexistingOnlyLabel} {rel.Count(PreOnly):N0} (Fixed {rel.Count(r => PreOnly(r) && Fixed(r)):N0}, Fixed mod-mover {rel.Count(r => PreOnly(r) && Fixed(r) && Pop(r) == DependencyPopulation.ModMover):N0}, interior {rel.Count(r => PreOnly(r) && Interior(r)):N0}) — inside the ReSupported count above, listed apart; "
                + $"Sunk rows with part of the depth already authored {rel.Count(r => r.Verdict == ContactVerdict.Sunk && r.Facts.WinnerPreexistingPenetration > 0f):N0}; other ReSupported rows beside a pre-existing overlap {rel.Count(r => r.Verdict == ContactVerdict.ReSupported && !r.Facts.WinnerPreexistingOnly && r.Facts.WinnerPreexistingPenetration > 0f):N0}");
bool OverCap(ContactRow r) => r.Winner is { } w && w.AgainstMaxDim > cidx.MaxHostDim;
Console.WriteLine($"   the folds, over the relationship rows: R1 Unjudgeable / RuntimeDisabled {rel.Count(r => r.Reason == ContactBlocker.RuntimeDisabled):N0} (Fixed mod-mover {rel.Count(r => r.Reason == ContactBlocker.RuntimeDisabled && Fixed(r) && Pop(r) == DependencyPopulation.ModMover):N0}), "
                + $"EnableChainUnresolved {rel.Count(r => r.Reason == ContactBlocker.EnableChainUnresolved):N0}; "
                + $"R3 ReSupported by an against-ref over the {cidx.MaxHostDim:0}u moved-host cap {rel.Count(r => r.Verdict == ContactVerdict.ReSupported && OverCap(r)):N0} (Fixed mod-mover {rel.Count(r => r.Verdict == ContactVerdict.ReSupported && OverCap(r) && Fixed(r) && Pop(r) == DependencyPopulation.ModMover):N0}), "
                + $"Sunk into one {rel.Count(r => r.Verdict == ContactVerdict.Sunk && OverCap(r)):N0}; "
                + $"R4 Unjudgeable / DepthExceedsObject {rel.Count(r => r.Reason == ContactBlocker.DepthExceedsObject):N0} (Fixed mod-mover {rel.Count(r => r.Reason == ContactBlocker.DepthExceedsObject && Fixed(r) && Pop(r) == DependencyPopulation.ModMover):N0})");
Console.WriteLine($"   by host change: " + string.Join("; ", Enum.GetValues<HostChange>().Select(ch => $"{ch}: class {rel.Count(r => r.Host.Change == ch && IsClass(r)):N0} of {rel.Count(r => r.Host.Change == ch):N0}")));

Console.WriteLine($"\n==== 4. POPULATION SPLIT (rule 12) — (mover class, dependent's author class) over the relationship rows ====");
Console.WriteLine($"   {"population",-30} {"rows",7} {"Stranded",9} {"Orphaned",9} {"Sunk",6} {"ReSupp.",8} {"MovedTog.",9} {"Removed",8} {"Unjudg.",8}   {"FIXED Stranded",14} {"FIXED Orphaned",14} {"FIXED Sunk",10}   {"..interior",10}");
foreach (var pop in Enum.GetValues<DependencyPopulation>())
{
    var pr = rel.Where(r => Pop(r) == pop).ToList();
    Console.WriteLine($"   {pop,-30} {pr.Count,7:N0} {pr.Count(r => r.Verdict == ContactVerdict.Stranded),9:N0} {pr.Count(r => r.Verdict == ContactVerdict.Orphaned),9:N0} {pr.Count(r => r.Verdict == ContactVerdict.Sunk),6:N0}"
                    + $" {pr.Count(r => r.Verdict == ContactVerdict.ReSupported),8:N0} {pr.Count(r => r.Verdict == ContactVerdict.MovedTogether),9:N0} {pr.Count(r => r.Verdict == ContactVerdict.Removed),8:N0} {pr.Count(r => r.Verdict == ContactVerdict.Unjudgeable),8:N0}"
                    + $"   {pr.Count(r => Fixed(r) && r.Verdict == ContactVerdict.Stranded),14:N0} {pr.Count(r => Fixed(r) && r.Verdict == ContactVerdict.Orphaned),14:N0} {pr.Count(r => Fixed(r) && r.Verdict == ContactVerdict.Sunk),10:N0}"
                    + $"   {pr.Count(r => Fixed(r) && IsClass(r) && Interior(r)),10:N0}");
}
Console.WriteLine($"   by mover class x author class (class rows, Fixed): " + string.Join("; ", rel.Where(r => Fixed(r) && IsClass(r)).GroupBy(r => (ClassOf(r.Host.Mover), ClassOf(r.DepAuthor))).OrderByDescending(g => g.Count()).Select(g => $"{g.Key.Item1} moved / {g.Key.Item2} authored {g.Count():N0}")));
var classRows = rel.Where(r => Fixed(r) && IsClass(r) && Pop(r) == DependencyPopulation.ModMover).ToList();
Console.WriteLine($"   the class (mod mover, Fixed, Stranded + Orphaned): {classRows.Count:N0} — Seen {classRows.Count(r => r.Seen):N0} (the dependent was in the mover's editor) / Blind {classRows.Count(r => !r.Seen):N0}; "
                + $"author after the mover {classRows.Count(r => r.AuthorAfterMover):N0}; mover's side overrode the dependent without moving it {classRows.Count(r => r.DepTouchedByMoverSide):N0}");

// The fit check before the host loop fills the contact caches: one compacting full collection, the live heap, and the memory
// free now. Refuses (exit 2) when the live data plus the minimum cache cannot fit; lowers an automatic budget to what fits.
string? FitCheck()
{
    var t0 = sw.Elapsed.TotalSeconds;
    System.Runtime.GCSettings.LargeObjectHeapCompactionMode = System.Runtime.GCLargeObjectHeapCompactionMode.CompactOnce;
    GC.Collect(2, GCCollectionMode.Forced, blocking: true, compacting: true);
    long live = GC.GetTotalMemory(false);
    long? free = CacheBudget.AvailablePhysicalBytes();
    string line = $"   memory before the host loop: live {live / 1e9:0.00} GB, free now {(free is { } f0 ? $"{f0 / 1e9:0.00} GB" : "unknown")}, contact cache budget {cidx.Budget.Limit / 1e9:0.00} GB";
    if (free is not { } f)
    {
        if (cacheBudget is null)
        {
            cidx.RefreshAutomaticBudget(() => CacheBudget.SelectAutomatic(
                GC.GetGCMemoryInfo().TotalAvailableMemoryBytes, null, live, "managed bytes after compacting full collection"));
            line += " -> " + cidx.AutomaticBudgetReport();
        }
        Console.WriteLine(line + $" (fit check skipped: free memory unreadable on this platform)  ({sw.Elapsed.TotalSeconds - t0:0.0}s)");
        return null;
    }
    if (CacheBudget.FitRefusal(live, f, "--radius off lowers the need; a larger machine or freeing memory raises what fits") is { } refusal) return refusal;
    if (cacheBudget is null)
    {
        cidx.RefreshAutomaticBudget(() => CacheBudget.SelectAutomatic(
            GC.GetGCMemoryInfo().TotalAvailableMemoryBytes, f, live, "managed bytes after compacting full collection", threads));
        line += " -> " + cidx.AutomaticBudgetReport();
        if (threads > 1 && cidx.AutomaticBudgetSelection?.MemoryHeldWarning() is { } held) line += " (" + held + ")";
    }
    else if ((long)(f * 0.9) - live < cidx.Budget.Limit)
        line += $" (warning: the --cache-budget exceeds the {((long)(f * 0.9) - live) / 1e9:0.00} GB that fits; kept as asked)";
    Console.WriteLine(line + $"  ({sw.Elapsed.TotalSeconds - t0:0.0}s)");
    return null;
}

void StaleBaseReport()
{
    foreach (var (cache, count, keys) in cidx.StaleBase())
        if (count > 0)
        {
            Console.WriteLine($"   [stalebase] {cache}: {count:N0} lookups asked a (ref, snap) for a different base than the last lookup");
            foreach (var k in keys.Distinct().Take(200)) Console.WriteLine($"   [stalebase]   {cache} {k.Key} snap {k.Snap}: {k.Was} -> {k.Now}");
        }
}

void PrintRow(ContactRow r)
{
    Console.WriteLine($"      {(PreOnly(r) ? DetectorModel.PreexistingOnlyLabel : r.Verdict.ToString()),-12} {r.Relation,-9} {r.Motion,-7} {(Interior(r) ? "int" : "ext"),-3} rank {RankOf(r),6:0.#}u  dependent {BaseLabel(r.DepBase)} {r.Dependent}");
    Console.WriteLine($"         host {BaseLabel(r.Host.PrevBase)} {r.Host.Key} {r.Host.Change} by {PluginName(r.Host.Mover)}{(r.Host.Change == HostChange.Moved ? $" ({PlacementHistory.Dist(r.Host.Previous.Pos, r.Host.Winner.Pos):0}u)" : "")} in {CellOf(r.DepAuthoring.Cell)}; dependent placed by {PluginName(r.DepAuthor)}"
                    + $" [{(r.Seen ? "seen" : "blind")}{(r.AuthorAfterMover ? ", author after mover" : "")}{(r.DepTouchedByMoverSide ? ", mover's side overrode it" : "")}]");
    Console.WriteLine($"         authoring {Contact(r.Authoring)} [{Src(r.Authoring)}]; winner {Contact(r.Winner)}{(r.Winner is { } w ? (w.Against == r.Host.Key ? " (the host)" : $" (against {BaseLabel(cidx!.WinnerBase(w.Against))})") : "")} [{Src(r.Winner)}]");
}

var floor = rel.Where(r => Pop(r) == DependencyPopulation.BethesdaFloor).ToList();
Console.WriteLine($"\n==== 5. THE FLOOR — Bethesda-on-Bethesda relationship rows: {floor.Count:N0} (Fixed Stranded {floor.Count(r => Fixed(r) && r.Verdict == ContactVerdict.Stranded):N0}, Orphaned {floor.Count(r => Fixed(r) && r.Verdict == ContactVerdict.Orphaned):N0}, Sunk {floor.Count(r => Fixed(r) && r.Verdict == ContactVerdict.Sunk):N0}; Movable class {floor.Count(r => !Fixed(r) && IsClass(r)):N0}) — control candidates ====");
foreach (var r in floor.Where(r => IsClass(r) || r.Verdict == ContactVerdict.Sunk).OrderByDescending(Fixed).ThenByDescending(RankOf).Take(listN)) PrintRow(r);

var ussep = rel.Where(r => Pop(r) == DependencyPopulation.UssepMover).ToList();
Console.WriteLine($"\n==== 6. USSEP AS MOVER — {ussep.Count:N0} relationship rows (Fixed Stranded {ussep.Count(r => Fixed(r) && r.Verdict == ContactVerdict.Stranded):N0}, Orphaned {ussep.Count(r => Fixed(r) && r.Verdict == ContactVerdict.Orphaned):N0}, Sunk {ussep.Count(r => Fixed(r) && r.Verdict == ContactVerdict.Sunk):N0}, ReSupported {ussep.Count(r => r.Verdict == ContactVerdict.ReSupported):N0}, MovedTogether {ussep.Count(r => r.Verdict == ContactVerdict.MovedTogether):N0}) ====");
foreach (var r in ussep.Where(r => Fixed(r) && (IsClass(r) || r.Verdict == ContactVerdict.Sunk)).OrderByDescending(Interior).ThenByDescending(RankOf).Take(Math.Min(listN, 12))) PrintRow(r);

Console.WriteLine($"\n==== 7. THE CLASS — mod mover, FIXED dependents, Stranded + Orphaned: {classRows.Count:N0} rows ({classRows.Count(Interior):N0} interior) over {classRows.Select(r => r.Dependent).Distinct().Count():N0} distinct dependents; Sunk beside: {rel.Count(r => Fixed(r) && r.Verdict == ContactVerdict.Sunk && Pop(r) == DependencyPopulation.ModMover):N0} ====");
Console.WriteLine($"   what a Fixed Stranded row IS, in game (2026-09-03, 3 of 3 runtime-real rows): an object hanging in the air where its host was — it FLOATS IN AIR; what a Fixed Orphaned row looks like in game is unseen");
Console.WriteLine($"   ---- by (mover, dependent's author) pair; CANDIDATES, each row's runtime existence resolved from the records (R1), not watched in game ----");
var pairs = rel.Where(r => Fixed(r) && Pop(r) == DependencyPopulation.ModMover).GroupBy(r => (Mover: PluginName(r.Host.Mover), Author: PluginName(r.DepAuthor)))
               .OrderByDescending(g => g.Count(IsClass)).ThenByDescending(g => g.Count()).ToList();
Console.WriteLine($"   {"Stranded",8} {"Orphaned",8} {"Sunk",6} {"ReSupp",7} {"MovedTog",8} {"distinct deps",13}  {"int/ext of class",16}  mover  moved a host under a dependent placed by  author");
foreach (var g in pairs.Where(g => g.Any(IsClass)).Take(listN))
    Console.WriteLine($"   {g.Count(r => r.Verdict == ContactVerdict.Stranded),8:N0} {g.Count(r => r.Verdict == ContactVerdict.Orphaned),8:N0} {g.Count(r => r.Verdict == ContactVerdict.Sunk),6:N0} {g.Count(r => r.Verdict == ContactVerdict.ReSupported),7:N0} {g.Count(r => r.Verdict == ContactVerdict.MovedTogether),8:N0} {g.Where(IsClass).Select(r => r.Dependent).Distinct().Count(),13:N0}  {g.Count(r => IsClass(r) && Interior(r)),7:N0}/{g.Count(r => IsClass(r) && !Interior(r)),-7:N0}  {g.Key.Mover}  x  {g.Key.Author}");
Console.WriteLine($"\n   ---- the ranker: top {listN} Fixed Stranded + Orphaned, interiors first, by how far the nearest thing is (nothing within ε; the number is the gap to whatever is nearest within {reach:0}u) ----");
foreach (var r in classRows.OrderByDescending(Interior).ThenByDescending(RankOf).Take(listN)) PrintRow(r);
Console.WriteLine($"\n   ---- Orphaned only (attachment): top {Math.Min(listN, 10)} ----");
foreach (var r in classRows.Where(r => r.Verdict == ContactVerdict.Orphaned).OrderByDescending(Interior).ThenByDescending(RankOf).Take(Math.Min(listN, 10))) PrintRow(r);
Console.WriteLine($"\n   ---- Sunk (mod mover, Fixed): top {Math.Min(listN, 10)} by penetration ----");
foreach (var r in rel.Where(r => Fixed(r) && r.Verdict == ContactVerdict.Sunk && Pop(r) == DependencyPopulation.ModMover).OrderByDescending(RankOf).Take(Math.Min(listN, 10))) PrintRow(r);
Console.WriteLine($"\n   ---- ReSupported (mod mover, Fixed): {Math.Min(listN, 6)} examples — the room rebuilt around the dependent ----");
foreach (var r in rel.Where(r => Fixed(r) && r.Verdict == ContactVerdict.ReSupported && Pop(r) == DependencyPopulation.ModMover && !r.Facts.WinnerContactIsHost).Take(Math.Min(listN, 6))) PrintRow(r);

var mov = rel.Where(r => r.Motion == DependentMotion.Movable && Pop(r) == DependencyPopulation.ModMover).ToList();
Console.WriteLine($"\n==== 8. MOVABLE DEPENDENTS (mod mover) — the context list, not the class: physics drops or shoves these at cell load (4.7 Part C) ====");
Console.WriteLine($"   " + string.Join(", ", new[] { ContactVerdict.Stranded, ContactVerdict.Orphaned, ContactVerdict.Sunk, ContactVerdict.ReSupported, ContactVerdict.MovedTogether, ContactVerdict.Removed }.Select(v => $"{v} {mov.Count(r => r.Verdict == v):N0}")));
Console.WriteLine($"   Movable Stranded + Orphaned : Fixed Stranded + Orphaned = {mov.Count(IsClass):N0} : {classRows.Count:N0}" + (classRows.Count > 0 ? $" ({(float)mov.Count(IsClass) / classRows.Count:0.0}x)" : ""));
foreach (var r in mov.Where(IsClass).OrderByDescending(Interior).ThenByDescending(RankOf).Take(Math.Min(listN, 8))) PrintRow(r);

Console.WriteLine($"\n==== 9. SENSITIVITY — every measured row re-judged at other ε (the class = mod mover, Fixed, Stranded + Orphaned) ====");
foreach (var e in new[] { eps / 2, eps, eps * 2 })
{
    int nCls = 0, nRel = 0, nSunk = 0, nRe = 0;
    foreach (var r in rows.Where(r => r.Facts.AuthoringMeasured && (r.Facts.WinnerMeasured || r.Verdict is ContactVerdict.Removed or ContactVerdict.MovedTogether)))
    {
        var v = DetectorModel.PlacementContact(r.Facts, e, cidx.SupportCos, cidx.AttachCos);
        if (v.Relation == ContactRelation.None) continue;
        nRel++;
        if (!Fixed(r) || Pop(r) != DependencyPopulation.ModMover) continue;
        if (v.Verdict is ContactVerdict.Stranded or ContactVerdict.Orphaned) nCls++;
        else if (v.Verdict == ContactVerdict.Sunk) nSunk++;
        else if (v.Verdict == ContactVerdict.ReSupported) nRe++;
    }
    Console.WriteLine($"   ε {e,4:0.#}u  relationships {nRel,8:N0}   class {nCls,7:N0}   Sunk {nSunk,6:N0}   ReSupported {nRe,7:N0}{(e > eps ? "   (rows first in contact beyond the near band are not here — a re-run with --eps would find them)" : "")}");
}

// THE PHYSICAL PREDICATE of a CONTACT row: ContactCredit.Physical and ContactCredit.Predicate (ContactCredit.cs).
(PhysicalState State, string Detail) ContactPhysical(bool measured, float dist, float pen, string against)
    => ContactCredit.Physical(measured, dist, pen, eps, against);
(PhysicalState State, string Detail) ContactPredicate(FormKey dep) => ContactCredit.Predicate(cidx!, winHash, dep, eps, BaseLabel);

// --emit: the class only — Fixed Stranded + Orphaned with a mod or USSEP mover, the Bethesda floor withheld. The
// dependent is the necessary record (a patch re-places or deletes it); moving the host back is the alternative. Every
// record carries its physical predicate under this arm (Holds by construction for a class row); with --base, every record
// of the named emit is re-measured under this arm too (2026-09-03).
if (emitPath is not null)
{
    var conflicts = new List<Conflict>();
    foreach (var r in rel.Where(r => ContactCredit.Emitted(r.Motion, r.Verdict, Pop(r))))
    {
        var (st, why) = ContactPhysical(r.Facts.WinnerMeasured, r.Facts.WinnerDistance, r.Facts.WinnerPenetration, r.Winner is { } w ? BaseLabel(cidx!.WinnerBase(w.Against)) : "(nothing)");
        conflicts.Add(new Conflict("CONTACT", PluginName(r.DepAuthor), PluginName(r.Host.Mover),
            $"{r.Verdict}: {BaseLabel(r.DepBase)} {r.Relation} on {BaseLabel(r.Host.PrevBase)} which {PluginName(r.Host.Mover)} {r.Host.Change}; nearest thing now {r.Facts.WinnerDistance:0.#}u  {r.Dependent} / {r.Host.Key} in {CellOf(r.DepAuthoring.Cell)}",
            new List<ConflictRecord> { new(r.Dependent.ToString(), "placed-ref", CellOf(r.DepAuthoring.Cell), st, why, Wind: WindNoteOf(r.DepBase)) },
            new List<ConflictRecord> { new(r.Host.Key.ToString(), "placed-ref", null) }));
    }
    List<Predicate>? predicates = basePath is not null ? MeasureBasePredicates(basePath) : null;
    var coverage = new List<CoverageLine>
    {
        // The population this run judged, and the chains it could not read to the end — the contact pass has resolved
        // the chain since R1, but until Q27 nothing said so in the emit, and conflictdiff could not see the unresolved
        // share move. Counted off the winner memo, so no answer changes and the console output is what it always was.
        new("enable chains (winner arm)", cidx.Arms.WinnerQueried - cidx.Arms.WinnerUnresolved,
            cidx.Arms.WinnerUnresolved > 0 ? new Dictionary<string, long> { ["EnableChainUnresolved"] = cidx.Arms.WinnerUnresolved } : new Dictionary<string, long>(),
            new Dictionary<string, long> { ["NotPresentAtLoad"] = cidx.Arms.WinnerAbsent },
            $"population load-time / state {cidx.State?.Name ?? EnablePopulation.BaselineStateName}"),
        new("dependent-host pairs", rows.Count - unj.Count,
            unj.GroupBy(r => r.Reason.ToString()).ToDictionary(g => g.Key, g => (long)g.Count()).Concat(new[] { new KeyValuePair<string, long>("DepEnableChainUnresolved", skips.DepEnableUnresolved) }).ToDictionary(kv => kv.Key, kv => kv.Value),
            new Dictionary<string, long> { ["BoxesApart"] = skips.BoxesApart, ["DepIntroducedByMover"] = skips.DepIntroducedByMover, ["DepOtherSpace"] = skips.DepOtherSpace, ["DepAbsentInArm"] = skips.DepAbsentInArm },
            $"relationships {rel.Count:N0}, class {classRows.Count:N0}"),
    };
    coverage.InsertRange(1, PreLoopLines());
    string populationIdentity = sweepPopulation;
    if (population is not null)
        coverage.Add(new("population scope", hosts.Count - hostsOutsidePopulation, new Dictionary<string, long>(),
            new Dictionary<string, long> { ["HostOutsidePopulation"] = hostsOutsidePopulation, ["RowOutsidePopulation"] = rowsOutsidePopulation },
            $"hosts judged only where a host or a dependent is in the plan's population; {population.Describe()}"));
    var identity = cfg.Identity(populationIdentity, cidx.State?.Name ?? EnablePopulation.BaselineStateName);
    ConflictIo.Write(emitPath, new ConflictFile("contactprobe", Instance, DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm:ssZ"), conflicts, coverage, predicates, basePath,
                                                populationIdentity, cidx.State?.Name ?? EnablePopulation.BaselineStateName, identity, MeasurementPolicy: sweepPolicy));
    Console.WriteLine($"configuration: {identity.Short}");
    Console.WriteLine($"\nemitted {conflicts.Count:N0} Fixed Stranded + Orphaned conflicts -> {emitPath}");
}
if (population is not null)
    Console.WriteLine($"   population: {hostsOutsidePopulation:N0} of {hosts.Count:N0} hosts and {rowsOutsidePopulation:N0} judged rows outside it were not measured");
if (population is not null && populationAuditEmit is not null)
{
    // For each row of another emit (the whole-order run's): the dependent is the necessary record, the host the alternative.
    var auditRows = new List<object>();
    foreach (var c in ConflictIo.Read(populationAuditEmit).Conflicts)
    {
        string? other = c.Alternative is { Count: > 0 } alt ? alt[0].FormKey : null;
        foreach (var rec in c.Necessary)
            auditRows.Add(new { identity = ConflictIo.RowIdentity(c.Kind, rec.FormKey, other, c.A),
                                inScope = RefInPopulation(FormKey.Factory(rec.FormKey)) || (other is not null && RefInPopulation(FormKey.Factory(other))) });
    }
    Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(populationAuditOut!))!);
    File.WriteAllText(populationAuditOut!, JsonSerializer.Serialize(new { probe = "contactprobe", population = population.Identity("load-time"), emit = Path.GetFullPath(populationAuditEmit), rows = auditRows },
        new JsonSerializerOptions { WriteIndented = true }));
    Console.WriteLine($"population audit: {auditRows.Count:N0} row(s) of {Path.GetFileName(populationAuditEmit)} -> {populationAuditOut}");
}

if (jsonPath is not null)
{
    var dir = Path.GetDirectoryName(Path.GetFullPath(jsonPath));
    if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
    var dumpOptions = new JsonSerializerOptions { WriteIndented = false, NumberHandling = System.Text.Json.Serialization.JsonNumberHandling.AllowNamedFloatingPointLiterals };
    // E1: a row whose dependent's contact geometry is its render fallback with a Tree_Anim shape gains "Wind" (no other change)
    // --radius on: each row also carries the core-surface gaps, so what the radius changed is on the row (no silent reading)
    object WithCore(object row, ContactRow r)
    {
        if (!radiusOn) return row;
        var node = JsonSerializer.SerializeToNode(row, row.GetType(), dumpOptions)!.AsObject();
        node["AuthoringCoreGap"] = r.Authoring?.CoreGap;
        node["WinnerCoreGap"] = r.Winner?.CoreGap;
        if (r.Winner?.CoverageNeighbours is { Count: > 0 } neighbours)
            node["ShallowCoverage"] = JsonSerializer.SerializeToNode(new
            {
                Direction = "+Z", Method = "floating-point polygon union", Certified = false,
                AreaBasis = "sum of dependent triangle projections, including multiplicity; union of host coverage per dependent triangle",
                CoordinateAndGapTolerance = 1e-5, HostFaceMinimumUpCosine = cidx.SupportCos,
                MaximumGap = eps, ThinExtentMaximum = eps,
                SelectedDecisionInputs = new { r.Facts.WinnerCoverage, r.Facts.WinnerAuthoredCoverage, r.Facts.WinnerCoverageInputsExactlyUnchanged },
                Neighbours = neighbours.Select(c => new { Against = c.Against.ToString(), c.Winner, c.Authored, c.InputComparison }).ToList(),
            }, dumpOptions);
        return node;
    }
    object WithWind(object row, FormKey depBase)
    {
        if (WindNoteOf(depBase) is not { } w) return row;
        var node = JsonSerializer.SerializeToNode(row, row.GetType(), dumpOptions)!.AsObject();
        node["Wind"] = w;
        return node;
    }
    var dump = rows.Where(r => r.Relation != ContactRelation.None || r.Facts.WinnerMeasured).Select(r => WithWind(WithCore(new
    {
        Dependent = r.Dependent.ToString(), DepBase = BaseLabel(r.DepBase), DepAuthor = PluginName(r.DepAuthor), DepScale = r.DepScale.ToString(), Motion = r.Motion.ToString(),
        Host = r.Host.Key.ToString(), HostBase = BaseLabel(r.Host.PrevBase), HostChange = r.Host.Change.ToString(), Mover = PluginName(r.Host.Mover), HostScale = r.HostScale.ToString(),
        HostMoved = PlacementHistory.Dist(r.Host.Previous.Pos, r.Host.Winner.Pos), HostPrev = r.Host.Previous.Pos.ToString(), HostNow = r.Host.Winner.Pos.ToString(),
        Cell = CellOf(r.DepAuthoring.Cell), CellKey = r.DepAuthoring.Cell.ToString(), Interior = Interior(r), DependentPos = r.DepAuthoring.Pos.ToString(),
        Population = Pop(r).ToString(), r.Seen, r.AuthorAfterMover, r.DepTouchedByMoverSide,
        Relation = r.Relation.ToString(), Verdict = r.Verdict.ToString(), Reason = r.Reason.ToString(), r.Why,
        AuthoringDistance = r.Facts.AuthoringDistance, AuthoringPenetration = r.Facts.AuthoringPenetration, AuthoringNormalUp = r.Facts.AuthoringNormalUp, AuthoringSrc = Src(r.Authoring),
        WinnerMeasured = r.Facts.WinnerMeasured, WinnerDistance = r.Facts.WinnerDistance, WinnerPenetration = r.Facts.WinnerPenetration, WinnerContactIsHost = r.Facts.WinnerContactIsHost,
        WinnerAgainst = r.Winner?.Against.ToString(), WinnerSrc = Src(r.Winner),
        WinnerAgainstExtent = r.Facts.WinnerAgainstExtent, WinnerAgainstMaxDim = r.Winner?.AgainstMaxDim,
        WinnerPreexistingPenetration = r.Facts.WinnerPreexistingPenetration, WinnerPreexistingOnly = r.Facts.WinnerPreexistingOnly,
        Label = PreOnly(r) ? DetectorModel.PreexistingOnlyLabel : r.Verdict.ToString(),
        ParityUnmoved = r.Winner is { } wp && Unmoved(r.Dependent, r.Host.Mover) && Unmoved(wp.Against, r.Host.Mover),
        DeepNeighbours = r.Winner?.DeepNeighbours?.Select(d => new { Against = d.Against.ToString(), d.Depth, d.Authored }).ToList(),
        DepBaseStory = BaseStory(r.Dependent), AgainstBaseStory = r.Winner is { } wb ? BaseStory(wb.Against) : null,
        DependentDisabled = r.Facts.DependentDisabled, DependentDisabledViaParent = r.Facts.DependentDisabledViaParent, HostRemovedViaParent = r.Host.RemovedViaParent,
        AuthoringRawDistance = r.Authoring?.Contact.Distance, WinnerRawDistance = r.Winner?.RawDistance, WinnerRawAgainst = r.Winner is { } wr && !wr.RawAgainst.IsNull ? wr.RawAgainst.ToString() : null,
    }, r), r.DepBase));
    File.WriteAllText(jsonPath, cidx.Arms.StampForced(JsonSerializer.Serialize(dump, dumpOptions)));
    Console.WriteLine($"wrote {jsonPath}");
}

if (traceSink is not null)
{
    cidx.Trace = null;
    var byPair = rel.GroupBy(r => (r.Dependent, r.Host.Key)).ToDictionary(g => g.Key, g => g.First());
    (bool, string)? Admission(FormKey dep, FormKey host) => byPair.TryGetValue((dep, host), out var r)
        ? (Fixed(r) && IsClass(r) && Pop(r) != DependencyPopulation.BethesdaFloor,
           !IsClass(r) ? $"{r.Verdict} is not an emitted class (Stranded, Orphaned)" : !Fixed(r) ? $"motion {r.Motion} (only Fixed is emitted)"
           : Pop(r) == DependencyPopulation.BethesdaFloor ? "Bethesda floor withheld" : "admitted")
        : null;
    ContactTraceWriter.Write(traceOut!, traceSink, traceKeysPath, traceKeys, cidx, skips,
                             cfg.Identity("load-time", cidx.State?.Name ?? EnablePopulation.BaselineStateName), "moved-host differential",
                             BaseLabel, Admission);
    Console.WriteLine($"wrote trace ({(traceSink.CountOnly ? "counting mode" : $"{traceKeys!.Count:N0} key(s)")}) -> {traceOut}");
}

Console.WriteLine($"\nA Stranded / Orphaned row says the dependent touched its host in the order without the mover and touches nothing within ε now, both of them existing at runtime in their arm;");
Console.WriteLine($"a Fixed Stranded row FLOATS IN AIR (in game 2026-09-03, 3 of 3 runtime-real rows); a Fixed Orphaned row's appearance in game is unseen. These rows are not watched in game.");
Console.WriteLine($"Limits: ε {eps:0.#}u and the 30° bands are chosen numbers; rotation- and scale-only host moves are invisible ({idx.SilentRePlacements:N0} such overrides);");
Console.WriteLine($"        terrain is not a reference (exterior rows are split out); the arm without the mover steps back ONE placement; a render-soup row ('r') is labelled, a box row ('o') is a box;");
Console.WriteLine($"        runtime existence is the enable chain as the records state it (R1) — a script that enables or disables a reference is invisible here; a marker is the Is-Marker flag (R2).");
WriteGeomReport();
MemStages.Print("end", cidx, rows.Count, done);
if (cidx.Timer is { } contactTimer)
{
    Console.WriteLine("\n==== CONTACT TIMERS (--phase-timers) ====");
    Console.WriteLine($"   host loop {hostLoopSeconds:0.0}s");
    foreach (var ln in contactTimer.Lines()) Console.WriteLine(ln);
    Console.WriteLine($"   measure memo: {cidx.MeasureMemoCount:N0} entries, about {cidx.MeasureMemoApproxBytes / 1e6:0.0} MB");
    __run.Feature("contact.memo.entries", cidx.MeasureMemoCount, "count", FeatureAt.Final).Feature("contact.memo.bytes", cidx.MeasureMemoApproxBytes, "bytes", FeatureAt.Final);
    __run.Feature("host-loop.seconds", hostLoopSeconds, "seconds", FeatureAt.Final);
    foreach (var (n, v, u) in contactTimer.Features()) __run.Feature(n, v, u, FeatureAt.Final);
    var timerDir = Path.GetDirectoryName(Path.GetFullPath(phaseTimersPath!));
    if (!string.IsNullOrEmpty(timerDir)) Directory.CreateDirectory(timerDir);
    File.WriteAllText(phaseTimersPath!, contactTimer.ToJson(new Dictionary<string, object> { ["hostLoopSeconds"] = hostLoopSeconds }));
    Console.WriteLine($"   wrote the contact timer table -> {phaseTimersPath}");
}
Console.WriteLine($"elapsed {sw.Elapsed.TotalSeconds:0.0}s");
return 0;
