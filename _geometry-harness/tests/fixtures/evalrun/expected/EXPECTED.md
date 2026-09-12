# The oracle — what evalrun must answer on the fixtures, derived from PLAN.md D1–D8

**Written by agent C before reading `evalrun.py`, `evalregistry.py` or the registry.** Every value below is derived by
hand from the definitions in `_claude/runs/2026-09-12-evaluation-controller/PLAN.md` and the packet's "Minimum
discriminating cases", with the sentence of the rule that decides it quoted beside it. Where the definitions leave a
value undetermined, there is a **QUESTION** for Fable instead of a guess; the machine-readable expectation for that
field is then omitted (`null`) and the test asserts only the part the definitions do settle.

The machine-readable half is one JSON per case in this directory (`case-01-*.json` … `case-11-*.json`).
`test_evalrun.py` reads those, never this prose.

If the code and this file disagree, **this file is not edited to match the code**: the disagreement is reported to
Fable with both values and the D-rule (CLAUDE.md §5 #11, "Triage every finding against the source before folding" —
the same posture, one level up: an expectation is not folded to the implementation without a ruling).

---

## 0. The fixture registry — the oracle's premise

`fixtures/evalrun/registry/fixture-check-registry.json`, `schemaVersion: 1`, `registryVersion: 1`,
`skill: "world-edit-compatibility"`. Eight families — the packet's seven, plus `scope` for the footprint itself.
Twelve checks. Ids are the PLAN's spelling where the PLAN spells one (`geometry.navmesh-ownership`, `scope.footprint`,
`functional.linked-dependencies`) and the same dotted `family.thing` shape elsewhere.

| # | checkId | family | required | trigger | adapter kind | exe | inputs.arms / states |
|---|---|---|---|---|---|---|---|
| 1 | `scope.footprint` | scope | yes | `{"always": true}` | `scopeprobe` | scopeprobe | baseline / baseline |
| 2 | `loadability.masters-resolvable` | loadability | yes | `{"always": true}` | `mcp-only` | — | baseline / baseline |
| 3 | `record.semantics-differences` | record | yes | `{"anyType": ["CELL","QUST","NPC_"]}` | `manual` (`manualDecisionAllowed: true`) | — | baseline / baseline |
| 4 | `geometry.navmesh-ownership` | geometry | yes | `{"navmesh": true}` | `ordersweep-probe` | navownprobe | all / baseline |
| 5 | `geometry.placement-overlap` | geometry | yes | `{"placed": true}` | `ordersweep-probe` | overlapprobe | all / baseline |
| 6 | `geometry.support-contact` | geometry | yes | `{"placed": true}` | `ordersweep-probe` | contactprobe | all / baseline |
| 7 | `functional.linked-dependencies` | functional | yes | `{"overlays": true}` | `mcp-only` | — | baseline / baseline |
| 8 | `functional.state-alternatives` | functional | yes | `{"placed": true}` | `probe-json` | switchprobe | baseline / **all** |
| 9 | `environment.occlusion-regeneration` | environment | yes | `{"always": true}` | `unsupported` | — | baseline / baseline |
| 10 | `patch.order-evaluation` | patch | yes | `{"always": true}` | `derived` (prereqs: checks 4, 5, 6) | — | all / baseline |
| 11 | `patch.sources-and-advice` | patch | **no** | `{"nexusId": true}` | `adviceprobe` | adviceprobe | baseline / baseline |
| 12 | `runtime.supplied-observations` | runtime | yes | `{"always": true}` | `media` | — | baseline / baseline |

Deliberate properties of this registry, and why:

- **Only `patch.order-evaluation` declares prerequisites.** D4's `blocked-prerequisite` is exercised through it
  (case 04). The three geometry checks declare none, so case 02's `--select` of exactly those three is not
  self-blocking — which is what PLAN's case-02 row requires ("the sweep's classification retained and scoped").
- **Three families have no runnable adapter** (`mcp-only`, `manual`, `unsupported`). D1: "a family with no supported
  adapter still has checks (kind manual / mcp-only / unsupported) so the ledger accounts for it." The consequence is
  that **no fixture case can reach `evaluation.complete: true`** — by construction, and that is the packet's point
  ("An unsupported required check keeps the evaluation incomplete"). Completion is therefore never asserted true
  anywhere in this oracle; what is asserted is the *membership* of `missing` / `unknownApplicability`.
- **`functional.linked-dependencies` is the unknown-applicability check.** Its trigger reads `overlays`, and every
  fixture `scope.json` names `overlays` in `unmeasured`. D1: "unknown when scope.json is missing, `complete` is false,
  or the trigger reads a field scope.json lists under `unmeasured`. Unknown never becomes no."

### QUESTION Q-1 — how does a trigger field match an `unmeasured` entry?

D2 writes `unmeasured` as a list of English sentences (`["script-driven placement", "alias-filled references", "BOS
runtime swaps", …]`); D1's unknown rule speaks of "a field scope.json lists under `unmeasured`". Sentences are not
field names, so the two definitions do not meet. **The oracle's reading:** each fixture `unmeasured` entry is written
`"<field>: <one sentence>"`, and a trigger is unknown when the token before the first colon, lower-cased, equals a
field the trigger reads. The fixtures therefore carry `"overlays: BOS runtime swaps are not scanned; the ini list in
the configuration is not the set of swaps performed"`. If `evalregistry` matches differently (a separate
`unmeasuredFields` array, substring search over the sentence, …), the fixture is wrong and not the rule — say which
shape scopeprobe actually emits and the fixture follows it.

### QUESTION Q-2 — does `--select` outrank an adapter-derived status?

D3: "`--select` narrows what RUNS, never what is PLANNED: a required check not selected is `skipped-user`."
D3 also fixes `status` ∈ `planned | not-applicable | unsupported | mcp-only | manual | skipped-user | excluded |
unsupported-for-arm`. A check that is `mcp-only` never runs, so "not selected" changes nothing about it.
**The oracle's reading:** adapter-derived statuses (`not-applicable`, `unsupported`, `mcp-only`, `manual`) win;
`skipped-user` applies only to a check that would otherwise be `planned`. Case 02's expectations are written that way.

### QUESTION Q-3 — is an *optional* unselected check `skipped-user` too?

D3 names only the required case. **The oracle's reading:** yes — `skipped-user` is the visible record of a narrowing,
and `required` is what decides whether it blocks completion, not whether it is recorded. Case 02 expects
`patch.sources-and-advice` (required: false) `skipped-user` and NOT listed under `missing`.

### QUESTION Q-4 — does `--select` take check ids or exe names?

D3 says `--select CHECK,...`; PLAN's fixture table writes the case as `--select navownprobe,overlapprobe,contactprobe`
(exe names). The test drives check ids (D3's spelling). If B accepts both, nothing is lost; if B accepts only exe
names, D3 and the fixture table disagree and Fable should pick one.

### QUESTION Q-5 — what is the lever for `skipped-budget`?

D4 lists `skipped-budget` and D3 takes `--budget-minutes N`, but nothing defines when a check is skipped for budget
(wall-clock exhaustion is not reproducible in a fixture, and `--budget-minutes 0` could mean "no budget" or
"unlimited"). The oracle therefore tests the *candidate* budget instead, which D3 and D7 do define
(`budgets.candidatesHeldBack`, `unassessed` "candidates held back"), via `--max-candidates 1` over a two-candidate
directory. `skipped-budget` itself is left untested and named as a deviation from PLAN's case-04 row.

### QUESTION Q-6 — what execution status does a `media` check carry when no media identifies its configuration?

D4's execution vocabulary has no media entry; D7 puts "media unassessed" under `unassessed` and D8 puts the item under
`unassessedMedia`. `blocked(no-identified-media)` fits D4's `blocked(<what>)` shape but is not written anywhere.
**The oracle asserts only what D7 settles:** `runtime.supplied-observations` is required, has no accepted receipt with
a detector result, and therefore appears in `missing` and keeps `complete` false. Its exact execution string is
`null` in the case JSONs (not asserted).

### QUESTION Q-7 — is `unknownApplicability` also listed under `missing`?

D7: "`unknown` applicability is listed under `unknownApplicability` and keeps `complete` false", and separately
"`unsupported`, `mcp-only`, … are listed by name under `missing`". `functional.linked-dependencies` is both unknown
and mcp-only. **The oracle's reading:** both lists contain it (they answer different questions — why it could not be
settled, and what is outstanding). The case JSONs assert membership of `unknownApplicability` strictly and of
`missing` strictly, so a design that lists it in only one will show up as a disagreement rather than a silent pass.

---

## 1. `case-01-geometry-and-quest` — a target with placements and quest data

**Scenario.** `FixtureTarget.esp` contains NAVM, REFR, CELL and QUST records, interior and exterior placements,
masters, and a `nexusId` from its mod folder's `meta.ini`. `scope.json` is `complete: true` and names `overlays`
under `unmeasured`. Arms: `baseline` and `candidate-01-reorder`. Every fake probe behaves `ok`; the candidate arm
emits exactly the baseline's rows, so nothing moved.

**Expected plan** (D1's applicability rule; D3's status vocabulary):

| checkId | applicability | why (the deciding sentence) | status |
|---|---|---|---|
| `scope.footprint` | yes | `{"always": true}` on a complete scope — "Applicability is **yes** when the trigger is true on a COMPLETE scope" | planned |
| `loadability.masters-resolvable` | yes | same | mcp-only |
| `record.semantics-differences` | yes | CELL and QUST are present in `recordTypes` — "`{"anyType": [...]}` (any listed record type present in the target, new or override)" | manual |
| `geometry.navmesh-ownership` | yes | `navmesh` count > 0 | planned |
| `geometry.placement-overlap` | yes | `placed` totals > 0 | planned |
| `geometry.support-contact` | yes | `placed` totals > 0 | planned |
| `functional.linked-dependencies` | **unknown** | "or the trigger reads a field scope.json lists under `unmeasured`" | mcp-only |
| `functional.state-alternatives` | yes | `placed` > 0 | planned |
| `environment.occlusion-regeneration` | yes | `{"always": true}` | unsupported |
| `patch.order-evaluation` | yes | `{"always": true}` | planned |
| `patch.sources-and-advice` | yes | `nexusId` is non-null | planned |
| `runtime.supplied-observations` | yes | `{"always": true}` | planned |

Both branches of the packet's first case are therefore present: the geometry branch (checks 4–6) and the record/quest
branch (check 3), and the dependency extent that cannot be settled (check 7) is visible rather than resolved.

**Expected ledger** — key `<checkId>__<armId>[__<part>]`, D4's parts:

- `geometry.*__baseline__arm`, `…__candidate-01-reorder__arm`, `…__candidate-01-reorder__reverse`,
  `…__candidate-01-reorder__diff` — all `completed`. Detector on the baseline and candidate arms: `finding`
  (the fake emits one CLOBBERED/OVERLAP/CONTACT row per probe, coverage judged > 0). Detector on the `diff` part:
  `no-finding` is wrong for a diff — D4 says "A diff verdict of UNJUDGED, or created rows resting on unmeasured
  predicates, is `inconclusive`", and says nothing else about a diff's detector field, so the diff parts' detector is
  **not asserted** (`null`).
- `scope.footprint__baseline` — `completed`, detector `no-finding` (a footprint reports no conflict).
  *Not asserted:* D4's detector vocabulary is written for conflict files; a `probe-json` footprint has no findings.
  The case JSON asserts execution `completed` only.
- `functional.state-alternatives__baseline` — `completed`.
- `patch.sources-and-advice__baseline` — `completed`.
- `patch.order-evaluation` — derived from the three geometry checks, all accepted → `completed`.
- `loadability.masters-resolvable__baseline` → `mcp-only`; `record.semantics-differences__baseline` → `manual`;
  `functional.linked-dependencies__baseline` → `mcp-only`; `environment.occlusion-regeneration__baseline` →
  `unsupported`; `runtime.supplied-observations__baseline` → not asserted (Q-6).

**Expected disposition** (D7):

- `complete: false` — "`unsupported`, `mcp-only`, `manual` without a decision … all keep it false and are listed by
  name under `missing`."
- `claim: "incomplete"` — "`evaluation.claim` ∈ … `incomplete`."
- `missing` ⊇ {`loadability.masters-resolvable`, `record.semantics-differences`, `functional.linked-dependencies`,
  `environment.occlusion-regeneration`, `runtime.supplied-observations`} and must NOT contain any of
  {`scope.footprint`, `geometry.navmesh-ownership`, `geometry.placement-overlap`, `geometry.support-contact`,
  `functional.state-alternatives`, `patch.order-evaluation`}.
- `unknownApplicability` == [`functional.linked-dependencies`] exactly.
- `arms["candidate-01-reorder"].orderEvaluation.classification == "PASSES-COMPLETED-CHECKS"` — ordersweep's
  `classify`: no illegal arm, no `newDefect`, every probe exit 0, no UNJUDGED verdict, no unmeasured count.
  D7: "ordersweep's classification verbatim with its probe list". `regression: false` — "`regression: true` when any
  probe's `created.newDefect > 0`"; here created is 0.
- `arms["candidate-01-reorder"].orderEvaluation.probes` == the three geometry exes — "scoped to the probes it ran",
  and the packet's "wrapping it must not promote that into a pass for checks it never ran".
- `unassessed.statesNotExplored` is non-empty — check 8 has `inputs.states: all` and the plan's selected states are
  `["baseline"]`; D3: "`notExplored`: every state a check with `inputs.states: all` would need."
- The real `conflictdiff` verdict on each probe's candidate arm: `DIFFERENTIAL-INERT` (rows identical; repaired 0,
  created 0, reclassified 0, unmeasured 0 → `DetectorModel.DifferentialVerdict`'s last line). The configuration delta
  is non-empty (the candidate reorders plugins), so `ConfigurationGate.Nondeterministic` is false and the verdict is
  not UNJUDGED.

**verify-bundle: 0** (case 11 re-tests the bundle explicitly).

---

## 2. `case-02-select-three-probes` — the caller asks for only the order-sweep probes

**Scenario.** Case 01's fixtures, planned with `--select geometry.navmesh-ownership,geometry.placement-overlap,geometry.support-contact`.

**Expected plan.** Applicability is unchanged from case 01 — `--select` is not an applicability input. Statuses:

- the three selected: `planned`.
- `scope.footprint`, `functional.state-alternatives`, `patch.order-evaluation`: `skipped-user` (required, planned,
  not selected — "a required check not selected is `skipped-user` (visible, blocks completion)").
- `patch.sources-and-advice`: `skipped-user` (optional; Q-3).
- `loadability.masters-resolvable`, `functional.linked-dependencies`: `mcp-only`;
  `record.semantics-differences`: `manual`; `environment.occlusion-regeneration`: `unsupported` (Q-2).
- `runtime.supplied-observations`: not asserted (Q-6 — `media` is neither clearly runnable nor clearly not).

**Expected disposition.**

- `complete: false`, `claim: "incomplete"`.
- `missing` ⊇ {`scope.footprint`, `functional.state-alternatives`, `patch.order-evaluation`} — the packet's
  "editing the selected-probe list cannot silently remove a registry requirement" — and must NOT contain
  `patch.sources-and-advice` (optional).
- `explicitDecisions` records the `--select` narrowing — D7: "`explicitDecisions`: the plan's `--select` / `--exclude`
  with their reasons." Asserted as: the JSON mentions all three selected ids.
- `arms["candidate-01-reorder"].orderEvaluation.classification == "PASSES-COMPLETED-CHECKS"` with
  `probes == [navownprobe, overlapprobe, contactprobe]` — retained and scoped, never promoted.

---

## 3. `case-03-null-and-extraction` — a valid null check against a failed extraction

**Scenario.** Arms `baseline` and `candidate-01-reorder`. On both arms:
`navownprobe` emits **zero** `Conflicts` with `Coverage` `[{Population: "navmesh records", Judged: 42, Unjudgeable: {}}]`;
`overlapprobe` emits zero `Conflicts` with `Coverage` `[{Population: "reference pairs", Judged: 0, Unjudgeable:
{"GeometryUnreadable": 17}}]`; `contactprobe` emits zero `Conflicts` with every population judged 0 and nothing
unjudgeable.

**Expected ledger.**

- `geometry.navmesh-ownership__baseline__arm` and `__candidate-01-reorder__arm`: `completed`, detector `no-finding`
  — D4: "A completed check with an empty `Conflicts` whose primary population was judged > 0 is `no-finding`".
- `geometry.placement-overlap__*`: `rejected(extraction-failed)` — D4: "`extraction-failed` (the check's primary
  population judged 0 while unjudgeable > 0 …)".
- `geometry.support-contact__*`: `rejected(extraction-failed)` — the same rule's second clause, "or every population
  judged 0".

**Expected disposition.**

- `complete: false`; `missing` ⊇ {`geometry.placement-overlap`, `geometry.support-contact`} and must NOT contain
  `geometry.navmesh-ownership` (a completed null check is completed).
- `patch.order-evaluation` → `blocked(blocked-prerequisite)`; it is in `missing`. D4: `blocked-prerequisite`;
  D1: "`prerequisites`: check ids whose receipt must be **accepted** first."
- `arms["candidate-01-reorder"].orderEvaluation.classification == "PASSES-COMPLETED-CHECKS"` — **retained verbatim
  even though two of its three probes produced rejected receipts.** ordersweep's `classify` sees exit 0, no created
  rows, no UNJUDGED verdict, and its coverage test does not fire (the unjudgeable fraction did not *rise*). This is
  the sharpest version of the packet's "wrapping it must not promote that into a pass for checks it never ran": the
  bounded verdict is kept word for word and the evaluation is still incomplete.
- `regression: false`.

---

## 4. `case-04-blocked-and-budget` — a failure, an exclusion, a held-back candidate

**Scenario.** Two candidates in the directory (`candidate-01-reorder`, `candidate-02-reorder-b`), planned with
`--max-candidates 1` and `--exclude geometry.support-contact=not-relevant-to-this-review`.
`navownprobe` exits 4 on the candidate arm; `overlapprobe` behaves `ok`.

**Expected plan.** `geometry.support-contact` → `excluded` (D3: "`--exclude` records the reason under the same rule").
The rest as case 01.

**Expected ledger.**

- `geometry.navmesh-ownership__candidate-01-reorder__arm`: `rejected(exit-nonzero)` — D4's first rejection cause.
- `geometry.placement-overlap__*`: `completed`.
- `patch.order-evaluation`: `blocked(blocked-prerequisite)` — two of its three prerequisites have no accepted receipt.

**Expected disposition.**

- `complete: false`; `claim: "incomplete"`;
  `missing` ⊇ {`geometry.navmesh-ownership`, `geometry.support-contact`, `patch.order-evaluation`}.
- `budgets.maxCandidates == 1` and `unassessed.candidatesHeldBack` non-empty, naming `candidate-02-reorder-b` —
  D7: "`unassessed`: … candidates held back (from candidates.json's Budget)"; the packet: "State and candidate
  budgets must say what was not explored."
- `explicitDecisions` carries the exclusion reason string `not-relevant-to-this-review`.
- **The bundle is still produced** and `verify-bundle` exits 0 — D8: "Bundle assembly succeeds while the evaluation
  is incomplete: two states, both written."

*Deviation:* `skipped-budget` is not exercised (Q-5).

---

## 5. `case-05-rejections` — a zero exit that is not a result

**Scenario.** Baseline arms all `ok`. Candidate arm `candidate-01-stale`: `navownprobe` `stale-output` (a pre-existing
emit is left untouched and the process exits 0), `overlapprobe` `wrong-state` (a valid emit whose `State` is `"night"`
and whose `Configuration.State` is `"night"`), `contactprobe` `illegal` (exit 2, `REFUSED` in the log, no emit).
Candidate arm `candidate-02-broken`: `navownprobe` `no-emit` (exit 0, nothing written), `overlapprobe`
`wrong-probe-name` (a valid emit whose `Probe` is `"driftprobe"`), `contactprobe` `no-configuration` (a valid emit
with `Configuration: null`).

**Expected ledger** — each receipt rejected for **its own** cause (D4's closed list, quoted):

| receipt | rejection cause | the sentence |
|---|---|---|
| `geometry.navmesh-ownership__candidate-01-stale__arm` | `output-stale` | "the emit's mtime or `GeneratedUtc` is before the child's `startedUtc`, or its sha256 equals a file that existed before the child started" |
| `geometry.placement-overlap__candidate-01-stale__arm` | `population-mismatch` | "Population/State not the plan's for that arm" |
| `geometry.support-contact__candidate-01-stale__arm` | `illegal-configuration` | "exit 2 + REFUSED + no emit" |
| `geometry.navmesh-ownership__candidate-02-broken__arm` | `output-missing` | D4's `output-missing` |
| `geometry.placement-overlap__candidate-02-broken__arm` | `wrong-check` | "the emit's `Probe` is not the adapter's exe" |
| `geometry.support-contact__candidate-02-broken__arm` | `identity-missing` | "no `Configuration`" |

Every one of these is `accepted: false`, and `execution` is `rejected(<that cause>)`.

**Expected disposition.** `complete: false`; all three geometry checks in `missing`; `patch.order-evaluation`
`blocked(blocked-prerequisite)`. The arms' classifications are ordersweep's own and are **not asserted** — a stale
emit is invisible to ordersweep by construction (it reads the file, not its freshness), which is exactly why the
receipt layer exists; asserting a value there would be asserting a coincidence.

---

## 6. `case-06-attribution` — a row that changes hands is not a repair

**Scenario.** Arms `baseline`, `candidate-01-sham`. `navownprobe` baseline emits one CLOBBERED conflict, one
necessary record `0A0001:FixtureBase.esp`, `Physical: "Holds"`. On the candidate arm the same probe emits **no**
conflicts but carries `Predicates: [{Kind: "CLOBBERED", …, FormKey: "0A0001:FixtureBase.esp", Physical: "Holds"}]` —
the patch took the record over without changing the geometry. `overlapprobe` and `contactprobe` are inert on both arms.

**Expected diff (the real `conflictdiff.exe`).** `DetectorModel.Credit(inBase: true, Holds, inChanged: false,
changedPhysical: Holds)` → `Reclassified`; `DifferentialVerdict(repaired 0, created 0, reclassified 1, unmeasured 0,
refusals 0)` → **`DIFFERENTIAL-RECLASSIFIED`**, counts `repaired 0`, `reclassified 1`, `created 0`.

**Expected disposition.**

- `arms["candidate-01-sham"].orderEvaluation.perProbe["navownprobe"].repaired == 0` and `.reclassified == 1` —
  D7: "Per-probe repaired / reclassified / created / unmeasured retained per arm." *(The exact field spelling under
  `orderEvaluation` is B's; the test locates the numbers by searching the arm's retained block, and the disagreement
  is reported if they are not there at all.)*
- `regression: false` (no `newDefect`), `classification == "PASSES-COMPLETED-CHECKS"` — ordersweep's own rule, kept
  verbatim; it means "no new physical defect", not "repaired".
- Nothing in the disposition credits a repair: the assertion is `repaired == 0` everywhere on that arm.

---

## 7. `case-07-combined-regression` — two patches that pass alone and regress together

**Scenario.** Arms `baseline`; `candidate-01-patcha` (PatchA.esp only); `candidate-02-patchb` (PatchB.esp only);
`candidate-03-combined` (`kind: supplied-combination`, both patches, ESP-only payload).

- `candidate-01-patcha`: `navownprobe`'s baseline row R1 is gone from the emit and its predicate re-measures `Gone`
  → repaired 1, created 0 → `DIFFERENTIAL-VERIFIED`, `newDefect: 0`.
- `candidate-02-patchb`: `overlapprobe`'s baseline row R2 likewise → `DIFFERENTIAL-VERIFIED`, `newDefect: 0`.
- `candidate-03-combined`: R1 and R2 both repaired, **and** `navownprobe` emits a new row R3 with `Physical: "Holds"`;
  the reverse arm (baseline with `--base` the combined emit) measures R3 `Gone` → `created_breakdown` puts it in
  `newDefect`. Per-probe on navownprobe: repaired 1, created 1 → `DIFFERENTIAL-NEUTRAL`, net 0.

**Expected disposition.**

- `arms["candidate-01-patcha"].orderEvaluation.classification == "PASSES-COMPLETED-CHECKS"`, `regression: false`;
  same for `candidate-02-patchb`.
- `arms["candidate-03-combined"].orderEvaluation.classification == "REGRESSION"` and `regression: true` — ordersweep's
  `classify`: "if any(r["created"]["newDefect"] > 0 …): return "REGRESSION"", and D7: "`regression: true` when any
  probe's `created.newDefect > 0`; a supplied-combination arm with a regression stays a regression whatever its
  repaired counts (no sum across probes, no net)."
- The favourable numbers are present and do not change the verdict: the combined arm's navownprobe row reads
  repaired 1 / created 1 / `DIFFERENTIAL-NEUTRAL` and the arm is still a regression. The packet: "Pair patches pass
  separately but combined arm regresses | Combined result stays a regression; favorable counts cannot conceal it."
- The combined arm's plan entry carries the ESP-only limit sentence — D3: "an ESP-only arm carries the limit sentence
  that its meshes, scripts and installer choices are not staged". Asserted as: the arm's `payload.espOnly` is true and
  its note is a non-empty string.

---

## 8. `case-08-interrupt-resume` — kill it, resume it, then change a plugin

**Scenario.** Case 01's fixtures. `evalrun run` is started as a subprocess and killed once at least one receipt file
exists. Then `evalrun resume`. Then one fixture plugin's bytes are changed and `evalrun resume` runs again.

**Expected after the kill + first resume** (D5, D6):

- The journal contains at least one `interrupted` transition — D5: "a `running` entry with no terminal line becomes
  `interrupted` and is re-queued, never counted."
- Every receipt that was written and valid before the kill is `reused` with a `reusedFromRunId` — D6: "Unchanged →
  `reused`, with `reusedFromRunId`."
- The ledger head lists two `runIds` (the `run` and the `resume`) — D5: "`runIds` (one per `run`/`resume`
  invocation …)". *Asserted as: at least two.*
- No check is `completed` on the strength of a partial write: nothing in the ledger is `completed` whose receipt file
  is absent.

**Expected after the plugin change** (D6):

- Every check whose accepted receipt's `consumed.configuration.plugins` names the changed plugin is
  `invalidated(<what changed>)` — "A mismatch → `invalidated(<what changed>)`, and dependents are invalidated
  transitively". The three geometry checks and `scope.footprint` read it.
- `patch.sources-and-advice` consumes the catalogue snapshot and no plugin, and stays `reused` — "Unchanged →
  `reused`". This is the "unchanged work stays reusable" half of the packet's case.
- The change is visible in the journal as a transition whose `to` begins `invalidated`.

*Not asserted (undetermined):* whether `resume` re-runs an invalidated check in the same invocation or leaves it
`pending` for the next `run`. D6 does not say. The test asserts the invalidation transition, not what follows it.

---

## 9. `case-09-finalize-refusals` — three ways a finalized answer goes stale

**Scenario.** A completed run directory from case 01 is copied three times and damaged once each:
(a) a byte is appended to the fixture registry file; (b) one receipt file is deleted; (c) one receipt's `planId` is
rewritten to `"0000deadbeef"`.

**Expected** (D7's first bullet: "Refusals first (exit 3, `complete: false` with the cause)"):

| damage | `finalize` exit | cause |
|---|---|---|
| registry edited | 3 | `registry-changed` ("the registry's hash ≠ the plan's") |
| receipt deleted | 3 | `receipt-missing` ("a ledger entry's receipt file is gone") |
| receipt's planId rewritten | 3 | `foreign-run` ("a receipt's planId is not this plan's") |

In all three, `disposition.complete` is `false` and the cause appears by name. No damaged run may yield a `claim`
other than `incomplete`.

---

## 10. `case-10-discovery-media` — a lead is not a repair, a screenshot is not an observation

**Scenario.** `adviceprobe` (fake) returns one catalogued file, `CommunityPatch.esp`, sha256 `f00d…`, which is in no
evaluated arm; plus author advice with a `provenance.capturedUtc` in the past. `media.json` lists two items: one whose
`configurationFingerprint` equals the baseline receipt's accepted fingerprint (computed by the test at run time), and
one whose fingerprint is `"deadbeefdeadbeef…"`.

**Expected disposition / bundle** (D7, D8):

- `discovery.candidates[0].qualification == "unqualified"` — D7: "candidates with provenance and `qualification:
  qualified` only for a plugin whose hash is in an evaluated arm, else `unqualified`"; the packet: "a downloadable hit
  remains unqualified until its actual payload has the needed evidence."
- `discovery.sourcesNotSearched` is present (a list, possibly empty but the field exists) — D7: "sources searched …,
  sources not searched".
- The matching media item is bundle evidence with `strength == "supplied-media"` — D8: "an item whose fingerprint
  equals an accepted receipt's fingerprint on that arm is copied under `media/` as evidence with strength
  `supplied-media`".
- The non-matching item appears in `manifest.unassessedMedia` with a reason and **is not** in `manifest.evidence` —
  "anything else is listed under `unassessedMedia` with the reason and is not evidence."
- `complete: false`, and no discovery or media entry appears in `missing` as satisfied.

---

## 11. `case-11-bundle-relocate` — the bundle survives a move and reports a deletion

**Scenario.** Case 01's finished run is bundled; `verify-bundle` runs on it; the bundle directory is copied to a
different path and `verify-bundle` runs there; then one evidence file is deleted from the copy and `verify-bundle`
runs a third time.

**Expected** (D8):

| step | exit | `verify.json` |
|---|---|---|
| verify in place | 0 | `bundle.complete: true` |
| verify after relocation | 0 | `bundle.complete: true` — "A relocated bundle validates identically" (bundle-relative paths, forward slashes) |
| verify after deleting one evidence file | 3 | `bundle.complete: false`, the missing path listed — "a missing or mismatched file → exit 3 and `bundle.complete: false` listing them" |

Also asserted: `manifest.excluded` says plugin and asset binaries are out — D8: "Never a plugin or asset binary" /
`excluded: [{what: "plugin and asset binaries", why: "identities only"}]` — and no file under the bundle has a
`.esp`/`.esm`/`.esl`/`.bsa` extension.

---

## Summary of what is asserted strictly vs. loosely

**Strict** (exact value, a disagreement is a defect in one of the two): applicability per check; plan status per
check; rejection cause per receipt; `complete`; `claim`; membership of `missing` and `unknownApplicability`;
`orderEvaluation.classification`; `regression`; `discovery.candidates[].qualification`; `verify-bundle` exit codes;
the real `conflictdiff` verdict strings.

**Loose** (presence and shape, because D1–D8 do not fix the spelling): the location of the per-probe retained counts
inside `orderEvaluation`; the `unassessed` sub-field names; the execution string of a `media` check (Q-6); the
detector field of a `diff` part; what `resume` does *after* invalidating.

---

## Amendment 1 (after the oracle was committed) — the fixture registry is a trim of the real one

The oracle above was written against a registry agent C invented from D1, because A's had not landed. A's registry
(`2d4ba074`, 29 checks over 8 families) then landed, and the fixture registry became a **verbatim trim of it** — the
honest premise, since the controller under test reads that file's shapes. What changed is only *which checks exist*;
every rule quoted above is unchanged, and no expected value was altered to match code (no controller code has been
read at this point).

The id mapping applied to the case JSONs, and the three substantive consequences:

| oracle's invented id | the real check it became | note |
|---|---|---|
| `scope.footprint` | `loadability.masters-and-order` | kind `scopeprobe`, the shared prerequisite of almost everything |
| `loadability.masters-resolvable` | `loadability.dangling-references` | mcp-only |
| `record.semantics-differences` | `records.cell-attributes` | **mcp-only, not manual** |
| `geometry.placement-overlap` / `geometry.support-contact` | `geometry.object-overlap` / `geometry.placement-contact` | |
| `functional.linked-dependencies` | `geometry.bos-overlay` | **the unknown-applicability check is this one**: its trigger is `{"overlays": true}` and the real `functional.linked-dependencies` is `derived` with a `{"placed": true}` trigger |
| `functional.state-alternatives` | `state.switch-census` | plus `state.conditional-geometry`, added because it is the only check with a real prerequisite chain (`prerequisites: ["state.switch-census"]`) |
| `environment.occlusion-regeneration` | `functional.collective-route-connectivity` (unsupported) + `environment.generated-output-regeneration` (manual) | the invented check stood for both; the real registry separates them |
| `patch.sources-and-advice` | `patch.author-advice` | required **true** in the real registry, so case 02 now lists it under `missing` |

1. **The unknown check now RUNS.** `geometry.bos-overlay` is `probe-json` (`bosprobe`), so its receipt is accepted and
   it is still `unknown`. D7's two sentences then bite independently: it is in `unknownApplicability`, and that alone
   keeps `complete: false`. That is a sharper test than the mcp-only version it replaces.
2. **`runtime.supplied-observations` is `required: false`** in the real registry, so it leaves `missing` and appears
   only under `unassessed` / `unassessedMedia`. Q-6 is therefore no longer load-bearing.
3. **No check in the trim has `inputs.states: "all"`**, so D3's `states.notExplored` may legitimately be empty and
   case 01 no longer asserts it non-empty. *Deviation from PLAN's budget line, recorded rather than engineered
   around: the two state-dependent checks in the real registry use `states: "selected"`.*

### QUESTION Q-8 — does `--select` pull in a selected check's prerequisites?

Every runnable check in A's registry declares `prerequisites: ["loadability.masters-and-order"]`. Under D3
(`--select` narrows what runs) plus D4 (`blocked-prerequisite`) plus D1 ("check ids whose receipt must be **accepted**
first"), selecting only the three ordersweep checks blocks all three — which would make PLAN's case-02 row ("the
sweep's classification retained and scoped") unreachable. Either `--select` implies the transitive prerequisites, or
the packet's "caller requests only the three default order-sweep probes" case ends in three blocked checks and no
sweep at all. **The oracle sidesteps it** by selecting the prerequisite too, and asks Fable to rule: implied
prerequisites, or blocked-by-design?

---

## Amendment 2 — two things the fixtures and the assertions learned from the arms

**D6 does not forbid invalidate-then-rerun inside one `resume`, and the journal transition is the evidence.**
The controller invalidates a receipt whose consumed plugin changed (`invalidated(plugin FixtureBase.esp changed on
disk)`) and then re-runs it to `completed` in the same invocation, journalling both. Case 08's assertion therefore
reads a journal line for THAT check whose new status begins `invalidated`, not the entry's resting status. Two wrong
shapes were tried first and both were caught by the `skip-resume-fingerprint` sabotage arm rather than by reading:
"the word `invalidated` appears anywhere in the journal" passes for the wrong reason (the interrupt phase puts it
there), and "the entry's final status is `invalidated`" fails against correct behaviour.

**A fake probe's output must be the shape the REAL probe writes, not a shape of its own.** The registry's
`adapter.result` spec names the fields a json or text check is read by, so a fake that invents its own document is
`rejected(schema-unsupported)` — correctly, and the fixture is what is wrong. The fakes now write:
`switchprobe --json` → `generated / instance / profile / plugins / unreadable / summary{switches,…} / perPlugin /
switches / jointFlips` (the college-case emit's shape; the spec reads `summary.switches`, `plugins`, `unreadable`);
`bosprobe --json` → `profile / plugins / inis / rows`; `adviceprobe --out` → `report-<id>.json` with `rules`,
`resolutions`, `notes`, `staleness`, `noOrderStated`, `pagesRead`, `catalogue`, `warnings`, `choices`;
`scopeprobe --json` → the case's own scope.json, re-stamped.
