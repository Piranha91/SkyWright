# NIF-only overlap measurements and bounded review

Object overlap is a candidate for inspection. Bounds, render-surface contact and a demonstrated
visible defect are three separate observations; no one of them substitutes for the others.

## Current decision requirement

Object-interference findings must consider the actual placed three-dimensional mesh surfaces.
Box overlap, depth, area, fraction or volume alone must not establish interference or supply
a conflict-severity ranking. The Q46 bounds quantities below remain diagnostic accounting
and historical evidence, not an accepted conflict-detection strategy.

Spatial bounds may serve only as a conservative acceleration structure when their rejection
is proven safe for the requested geometric predicate and its tolerance. A box overlap sends
work to the actual-geometry test; it never establishes a positive. Box-overlap magnitude and
box-size thresholds must not silently discard genuine surface intersections. Unsupported,
incomplete or untested geometry remains unknown. Preserve source/pose and full pair accounting.

Keep surface crossing, touching/coplanar overlap, separation and valid closed-solid containment
distinct. An open surface does not automatically define an occupied solid volume. Actual-shape
evidence is necessary but not sufficient for a compatibility verdict; intentional-overlap
filtering is a subsequent, separately validated layer.
The production overlap path uses the symmetric `MeshIntersection` primitive; the support-oriented
contact helper remains separate. Preserve the Q46 bounds artifacts as historical diagnostics.

## Geometry and source contract

Use decoded NIF render vertices for overlap discovery and measurement. Record ObjectBounds/OBND
must influence no candidate gate, size limit, score, fallback, measurement or correctness oracle.
Stored NIF shape spheres belong to the separate occlusion-culling question; they are not vertex
bounds. Missing or partially decoded geometry is an explicit unknown, with null final measurements.

Resolve the arm's effective winning base and actual asset precedence. Keep the relative path,
provider, loose path or archive entry, consumed-byte hash and reader/policy version. Ordinary
multiple providers are provenance; do not confuse that with an incomplete archive scan. Freeze
plugin content/order, enable/deletion/BOS state and effective poses as well. Where older evidence
lacks consumed-asset hashes, label a current-source reconciliation rather than claiming exact replay.

The strict render policy excludes statically hidden shapes, conservatively includes visible LOD
shapes, and refuses unsupported skinned, controlled or switch-node geometry. Keep exclusions and
unsupported/partial outcomes distinct. The default generic NIF reader and collision semantics are
separate; an overlap policy change must not silently alter their measurements.

Model bounds enclose decoded vertices after the proven below-root node transforms. Current world
AABBs enclose the actual placed triangles. Historical bounds catalogues instead transformed the
model box's corners, producing a looser enclosure. Apply the
effective winning reference translation, rotation and uniform scale exactly once, even when a
scale-only or rotation-only override does not change placement-history attribution. Negative
finite scale is a mirror; zero scale is an explicit unknown for actual-surface testing. A flat
model with nondegenerate triangles remains eligible even when its bounds have zero volume.
Diagnostic box fractions use the actual box volume and never decide surface eligibility.
Non-finite/inverted bounds or transforms are refused by name. Cache decoded models by consumed
content and reader/policy, then place each reference separately.

## Historical bounds quantities and rankings

For each axis, intersection extent is `max(0, min(maxA,maxB)-max(minA,minB))`.

- Intersection volume: X × Y × Z extents, in Skyrim units cubed.
- XY projected intersection area: X × Y extents, in Skyrim units squared.
- Relative overlap: volume divided by the smaller box's volume; null with `zero_volume` when
  that denominator is zero.

Retain box dimensions/volumes and the signed Z intersection, touching/separated/overlapping
relation and gap. A vertically separated pair can have positive XY area and zero volume.
Historical bounds catalogues retain separately sortable absolute-volume, XY-area and relative views,
with stable unordered full-reference keys breaking ties. A legacy depth-times-fraction score is
neither area nor volume. These are diagnostic fields only; current object-interference decisions
and default review-example selection do not use any of these scores or the old thresholds.

Large hollow shells and rotated thin geometry can rank highly while their surfaces never meet.
These measures are not occupied-mesh volume, visible damage area, compatibility or severity.

## Accounting and review

Keep the bulk-record-jobs canonical record envelope and identities. Preserve each original row,
its directional provenance and evidence identity alongside an unordered pair key. Account for
duplicates explicitly. Re-run discovery through the corrected NIF-only path; filtering an old
OBND-dependent list cannot establish complete discovery. Historical rows that become excluded,
non-overlapping or unmeasurable stay in the catalogue; new candidates form a separately counted set.

Measurement completeness, contact coverage, visual-review coverage and compatibility conclusions
need separate closed vocabularies and denominators. Not visually reviewed is the default. A
missing measurement is null with a reason. Unknown geometry cannot be credited as repaired or clear.
Differential emits must name matching measurement policies before receiving repair credit.
The measurement identity includes both reader and policy versions; a matching policy name alone
does not establish compatible geometry from different reader versions.

Use `overlapprobe --geometry-manifest OUTPUT` for a versioned actual-shape reconciliation. Set
`--responsible-pair A B` and `--historical-pairs INPUT` to retain the historical/fresh union in a
declared scope. `--surface-tolerance`, `--max-triangle-pairs` and `--pair-seconds` define the
predicate and resource limits. Old `--depth`, `--frac` and `--maxdim` flags are refused.

Use `contactprobe --overlap-json INPUT --json OUTPUT` for explicit pair mesh evidence. It accepts
overlap row arrays, a `pairs` envelope or an overlap conflict emit. Both sides use strict render
triangles; the output saves each measured, excluded, unknown, failed, timed-out or unrun row.
Set `--contact-max-pairs`, `--contact-budget-seconds`, `--pair-budget-seconds`,
`--mesh-max-triangle-pairs` and `--surface-tolerance` to the chosen workload. Cancellation is
cooperative; source reads and allocations can finish after a deadline. Both overlap entry points
use the same symmetric predicate, independent of winding and the support-contact helper.

For reviewed pairs that need a contact-support locator without rebuilding the whole scene manifest,
add `--contact-evidence` to that existing pair-review command:
`contactprobe --overlap-json INPUT --contact-evidence --json OUTPUT`. The opt-in reuses each pair's
already resolved placed render soups and the remaining pair/batch cancellation window. Each row keeps
its ordinary `actualShape` result and adds typed `contactEvidence`; the output budget records the
contact-evidence policy and whether the option was enabled. Without the flag, the Q46 pair-review
predicate and discovery behavior are unchanged. The evidence envelope remains a conservative locator,
not exact intersection extent, occupied volume, visibility, severity or ranking evidence.

`actualShape` keeps `status`, `reason`, `surfaceRelation`, `containment`, nullable `intersects`
and `trianglePairsTested`. Relations are `crossing`, `coplanar_overlap`, `touching`, `separated`
or `unknown`. Touching includes proximity within the explicit tolerance. Coplanar overlap means
positive area on exactly coplanar represented triangles; it supplies no area or volume quantity.
An open mesh can have measured separated surfaces with `not_applicable_open_mesh` containment;
that is no assertion about an occupied interior. Closed-solid containment requires validation;
invalid topology, unresolved numerical conditions and nested-shell fill rules remain unknown.
Budget exhaustion never becomes separation or repair credit. No occupied-solid intersection
volume, collision behavior, intentionality or compatibility verdict is calculated.

The state-change probe's old box-based overlap role is explicitly unmeasured pending a strict
surface implementation; its contact and route roles retain their separate contracts. Historical
box outputs cannot be compared with actual-shape emits for repair credit.

Freeze a purposeful pilot before judging images: shells, thin/rotated geometry, changed scales,
surface-relation categories, separated controls and prior ambiguous examples. Historical box
ranked selections remain historical exploratory samples, not a default severity ranking. Start
with 6–8 distinct pairs; expand only when informative and within the stated cap. Group repeated
mesh families for navigation, never propagate one placement's verdict to others. This sample
cannot estimate whole-population false-positive rates.

Use measured cameras and fixed framing: the review reel shows the place without the evaluated mod,
with it, then highlighted, at one camera. Have an image-capable reviewer describe the objects and their relationship before
receiving intended names or defects. Verify source identity separately and retain both judgments.
Open the actual images before describing them. Highlights establish identity, not damage; qualify
ordinary architectural junctions, visible clipping or insufficient evidence by the inspected view.
Keep exploratory cases distinct from any future held-out benchmark.

Inspect the renderer's highlight semantics before using coloured coverage as surface evidence.
An inflated outline or through-occluder ghost can mark the correct reference while appearing
to cover another visible object. Preserve a blind identity mistake and append the source-based
correction; do not silently rewrite the first description. Exact-pixel failures and narrower
manual observations are separate results. A normal-view gap is evidence about that projection,
not an independent proof of three-dimensional separation.

Write quantity definitions and expected controls before implementation. Compare actual-shape
results against independently defined analytic or exhaustive triangle cases, with symmetry,
winding, transform and wrong-method controls. Historical bounds arithmetic has its own oracle.
Neither comparison validates shared source inputs; reader, asset-resolution and transform
fixtures must independently support the geometry.

## Configuration comparisons and incident triage

Use a separate explicit plugin **and asset** view for the dependency baseline, source additions,
combined setup and patched setup. A candidate `--order` file must include `Mods` in strongest-first
asset priority for a scene capture; omitting it is refused before loading because inherited
profile assets cannot establish an isolated source baseline. These reconstruct installed versions,
never an author's historical setup. An explicit `Mods: []` is a valid
basegame-only asset view. `runcontrol plan` runs `overlapprobe --scene-preflight` on every arm when
it is given an instance and refuses an arm the scene capture would refuse; outside a plan, run
`overlapprobe --scene-preflight --order CANDIDATE` first: check
each selected provider against the intended profile name and directory. The candidate's `Mods`
order is its stated MO2 priority, strongest first, and every probe and scene capture reads assets
in that order; where it differs from the base profile's modlist.txt the preflight names the
re-ordered providers rather than refusing. A step that runs the real game (the live switch check)
needs a profile that holds that order and refuses one that does not. Deliberate omissions are
permitted; the candidate is an ordered subset, not the entire profile. Record the omitted
providers' purpose in the setup plan. A typo must be corrected before acquisition, never silently
inherited from profile assets.
A variant shipped as a plugin alone reuses its main mod's meshes: list the main mod's folder in
`Mods` after the variant's, or the preflight refuses the arm, naming the folder that has them. The
plan refuses for this only an arm where a planned check runs overlapprobe; on other arms it records
the verdict, and with the live switch check opted in the refusal also says whether that check's
profile rule would accept the remedied arm.
Keep source plugin/archive/consumed mesh hashes, population, enable/deletion/BOS state, reader,
predicate, tolerance and limits. Missing or invalid views remain unresolved.

`overlapprobe --scene-id ID --scene-role ROLE --order CANDIDATE --geometry-manifest OUTPUT`
captures a canonical scene. Scope it with candidate plugins/mods or a previously captured scene
using `--scene-scope-from FILE`; consult the CLI for the exact inputs. Scene captures retain
source relationships, including same-author and vanilla pairs needed as analogue examples.
Exterior persistent-cell filing is not locality: fixed placed-NIF footprints determine context.
Record unknown spatial membership with exact identities and reasons. A bounded context does not
prove that every possible consequence elsewhere was examined.

Generic entry point, with no profile-specific names:

```text
overlapprobe --scene-id SETUP_ID --scene-role ROLE \
  --order ORDER_WITH_EXPLICIT_MODS.json --geometry-manifest SETUP.json \
  [--candidate-plugin CANDIDATE.esp]... [--candidate-mod "Candidate Mod Folder"]... \
  [--scene-scope-from PRIOR_SCENE.json]... [--scene-cell CELL_FORMKEY]... \
  [--historical-pairs PAIRS.json] [--contact-evidence-pairs PILOT_PAIRS.json] \
  --surface-tolerance 0.00001 --max-triangle-pairs 2000000 --pair-seconds 5
```

Use candidate plugin/mod inputs to establish the initial seed scene. Use imported scopes for
candidate-off setups and closure passes; imported seed definitions retain absent references and
their fixed source footprints. `--scene-cell` is a bounded explicit seed only; exterior persistent
cells are refused.

An imported scope must declare at least one explicit bound. A capture declares one in either of
two ways, and `--scene-scope-from` accepts either: a non-empty `scene.discovery.seedReferences[]`,
or a non-empty `scene.discovery.cells[]`. Declared cells are interior-only by construction, so a
cell-bounded scope carries a bounded footprint into a later configuration and is re-validated as
interior there; it never widens to a worldspace. A scope declaring neither bound is refused. An
imported bound is reported back in `scene.discovery.importedDeclaredCells[]` and per source in
`importedScopes[].declaredCellCount`, so which bound came from where stays auditable. An imported
cell whose owning plugin the setup neither loads nor masters does not exist in that setup (the same
rule attribution applies to objects): it bounds nothing there, is named in
`scene.discovery.absentDeclaredCells[]` with its owner and declaring scope, and does not refuse the
capture, so a setup without the candidate can import a scene that declares the candidate's own
cells. A typed `--scene-cell` whose owner is absent is refused. Importing a
scope also retains its pair and reference identities: every imported pair gets a row whose
relation is re-measured in the new configuration, so a pair whose members have separated, moved,
been disabled or been excluded keeps an explicit disposition instead of disappearing. A retained
pair whose member is ineligible reports `actualShape.intersects: null` with that member's reason —
it never reads as separated, and an empty result still never means absence.

Every imported reference identity receives exactly one disposition in
`scene.discovery.importedReferenceAccounting`. The imported population is every `records[]` formid
AND both endpoints of every `pairs[]` row, so a pair-only endpoint is accounted like any other
identity. The dispositions are `emitted` (a record row enumerates it), `no_placement_history`
(`missingImportedReferences`, unchanged in meaning and now derived from the same partition),
`no_winning_pose` (placement history resolved but no winning pose, so position and geometry are
unknown here) and `out_of_scope` (a winning placement outside this capture's bound). The partition
is total: `importedCount` equals the imported identity count, the dispositions sum to it, and
`unaccounted` is zero. Read the dispositions as statements about PLACEMENT only.
`no_placement_history` does not mean the order declares no record — a placed record with no
Placement subrecord, which a deletion typically strips, never enters the placement history at all.
No disposition establishes presence, absence, deletion, disablement or geometric separation; those
come from an enumerated record row's own fields, never from a reference's failure to appear. The scene path retains the same-responsible results already measured by the
main surface sweep rather than querying those pairs again; this changes retention, not the pair
predicate or the default non-scene pass. Large scenes commonly retain many exact
`scopeUnknownRecords` because missing or unsupported geometry cannot establish spatial membership.
Those rows and counts make the manifest incomplete and must remain in attribution/report accounting.
For unknown exterior geometry, a finite placed origin inside a known same-space seed footprint
expanded by the existing 4096-unit context margin establishes bounded origin membership only.
Persistent-cell filing does not admit a whole worldspace. Unknown membership is retained in
`exteriorMembershipUnresolved`; distant origins remain named in
`exteriorOutsideOriginNeighbourhood`. Their unknown extent could still reach the scene, so neither
excluded group establishes physical clearance and either keeps completeness false. Legacy exterior
populations must be labelled as retained-population replays until this scope contract is recaptured.

Freeze the union of discovered pair identities, then measure that same union in every required
setup by importing the source scopes. Repeat only the required closure pass when another setup
adds pairs. An absent output row is unresolved, not separation. An absent reference can establish
absence only in a valid scope which explicitly tested its membership, or when the setup's plugin
list neither loads nor masters the plugin that owns its FormKey (no loaded plugin can declare it, so
it is not placed in that setup). A loaded or mastered owner with no record row stays unresolved.
Preserve all original rows and the new source-only identities. Geometry completeness and enumeration completeness differ.

Run `overlap_attribution.py` with repeated `--setup ID=MANIFEST`, `--combined ID`, `--compare ID`
and the named `--candidate PLUGIN` set, then `--output FILE`. Both effective objects are compared
in both scenes. New, inherited, changed-existing, absent/excluded and unresolved relationships
remain distinct. Provider/record-winner history and query-work counters alone are not geometric
change. A prior intersection that changes stays reviewable. An unchanged inherited relation is
not automatically benign. The retained narrow previous-placement result is compared separately;
missing narrow evidence is not inferred from a new-candidate flag.

Run `overlap_triage.py --manifest ATTRIBUTED --out TRIAGED`, then
`evalreport.py triage --manifest TRIAGED --out REPORT_DIR`. The versioned extension preserves
records/assets/pairs and separates measurement, attribution and triage accounting. Ranking,
features, analogue evidence, incident membership and adjudicated consequences have separate fields.
All pairs remain recoverable. Filters show their counts and exclusions; no default suppression
or calibrated probability is implied by a score.
Report member-level consequences with fully reviewed, partially reviewed and unreviewed incident
states. Outstanding-review filters include partial groups. Show comparison labels, changed field
categories, full before/after evidence, confidence basis and a next action for every outstanding
member. New/changed pairs with unknown geometry have a separately counted queue; it can overlap
the broader unknown-feature view. Aggregate comparison status deterministically as unresolved,
new, changed-existing, absent/excluded, inherited, while keeping the per-comparison labels.

Analogue examples come from hash-verified source scenes and cite their exact placement pair.
Consumed-mesh identity is stronger than an explicitly declared mesh family or base identity.
Compare relative position, orientation, scale and available contact evidence; the same meshes
assembled differently are not equivalent relationships. An exact reference pair in another setup
is a configuration comparison, not a fresh analogue. Missing contact-location evidence limits a
match and cannot silently earn a benign decision. Family definitions and functional priorities
live in SkyWright's `data/overlap-triage-policy.json`, not tool code. Freeze this policy before reserved validation.

The optional `--contact-evidence-pairs FILE` pass enumerates **every classified triangle contact**
within its budget for nominated pairs. Its contact-evidence envelope unions the intersections of
the participating triangles' tolerance-expanded bounds, after actual contact is established.
It is a conservative locator, not the exact contact locus, occupied intersection volume,
penetration depth, visibility or severity. Never rank by its box magnitude. A partial result keeps
its partial value separately and its final value null; unrequested rows remain unmeasured.
Exact geometric intersection extent, floor height, winning-navmesh distance and quest context
remain null with reasons unless their independently qualified inputs support those quantities.
Functional object types establish presence of a function, never its obstruction.

Incidents group shared-reference pairs directly around a fixed representative in the same place.
Locality uses complete contact-support envelopes, otherwise the intersection of measured placed
NIF bounds, solely as a location check. Every member must fit one combined support box whose
diagonal stays within the policy's 384-unit maximum. Unknown, disjoint or individually oversized
supports remain singletons. This conservative grouping parameter is not a severity weight or a
calibrated distance to a consequence. Shared-reference chains cannot grow the group. Every member
is inspectable, and one placement's verdict never propagates to the rest. Use measured, fixed
cameras across source/combined/patched views. Inspect normal scenes before predicted defects;
then use on/off/on highlighting to establish identity. Insufficient views stay unresolved.

Keep development examples separate from reserved scenes, freeze policy before evaluation, and
report sampling scope, dependence and denominators. A patch's edit/disappearing row is comparison
evidence only; credit a physical repair only when independent visual/runtime evidence supports the
actual consequence. Report review effort separately from raw pair/group reduction. Within-family
utility does not establish transfer to other mod families or unreviewed populations.

## Post-render subject/contact qualification

Use `subject_gate.py --input CASE.json --output NEW_RESULT.json` after acquisition. Capture
normal, A-only, B-only and a source-verified known-elsewhere reference twice each, using identical
camera, canvas, source configuration and rendering options. The input binds exact records, contact
evidence, renderer binaries, actual command/log/sidecar identities and image hashes. The CLI refuses
an existing output. A capture can be usable while its measurement is refused; consequence remains
unassessed by this gate.

`viewprobe --ref-surface` is an opt-in diagnostic using depth-tested placed opaque triangles without
an inflated hull or through-occluder ghost. It keeps the normal material path and reports incomplete
coverage for unsupported alpha/effect/marker/non-depth-writing shapes. Those refusals invalidate
the subject measurement; do not bypass them because another opaque portion is visible. This is
a limited diagnostic, not a general segmentation mask or a fix for all renderer materials.

The projected contact-support box is an eligibility rectangle, never exact contact pixels. Declare
the feature and sensitivity before evaluation. Require repeat-supported signal for each participant,
quiet repeats and elsewhere control, and an image-hash-bound normal-view assessment of the actual
contact/edge. A large object's visible remote portion cannot qualify its hidden junction. A missing,
partial, clipped or behind-camera envelope refuses this contact test. Explicit source absence and
unresolved presence remain distinct from zero visible response on an expected-present participant.

Keep a verified interior eye and aim/fit from all measured contact-envelope corners; the framing
proposal is advisory until normal room context and the post-render gate agree. Do not infer room
membership from a planner score, move through the shell, or fall back to OBND. Preserve failed
camera/coverage/control outcomes. A bounded known-case regression with no qualified contact view
is an unresolved validation result, not permission to lower the gate or expand the campaign.
