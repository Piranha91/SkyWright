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

---

# Q43 — the corrected evidence-integrity contract (cases 12-15)

**Written by agent O from `_claude/runs/2026-09-12-evaluation-evidence-integrity/PLAN.md`'s D1-D8 and amendment A-1,
and committed BEFORE any of this wave's driver code and without reading the implementer's branch.** The reviewed
controller (`evalrun.py` sha256 `86334e954a1a0e60...`) is the source these cases must be RED against: it is the one
the Q42 review reproduced R1, R2 and R3 on. A case that passes on the reviewed source for a corrected-contract
assertion tests nothing and is a defect in this oracle, not a pass.

Same posture as the Q42 half above: where a definition leaves a value undetermined, the expectation derived is
written down and marked `rulingNeeded` in the case JSON rather than guessed silently, and the machine-readable
assertion is narrowed to the part the definitions do settle.

## 12. `case-12-identity-binding` — the emit is bound to what the plan ASKED for (R1)

The review's `wrong-configuration` case fed the production `Validator` an emit whose `Configuration.Plugins` were the
plan's four plugins **reversed**, with a correct `Source`; it came back `accepted: true, rejections: []`. The cause is
structural: `_identity_checks` compares an emit to the arm's *earlier accepted receipts* and to a source string, so
the first receipt of an arm defines the arm.

Two consequences for the fixture, both load-bearing:

1. **Every damage is applied to every emit-writing probe of the arm**, so the arm stays internally consistent. A
   mutation on one probe alone is caught by the within-arm content rule that already exists (D2(g)) and would prove
   nothing about D2(b)-(e). This is also the honest reproduction of R1, whose emit was the only one of its arm.
2. **Each damage is driven end to end through the CLI** (`plan` then `run` with a fake-probe behaviour), never by a
   direct `Validator` call. The review's isolation was deliberate and it is exactly what leaves the question of
   whether the run path reaches the rule at all.

The case also carries the two controls that stop the contract from being "reject everything": the healthy baseline,
the healthy legal alternative with its reverse and diff parts, and `functional.quest-alias-split` (driftprobe), which
**declares a different population and state** (`n/a (placement history; no enable-state filter)` / `n/a`) from the
geometry probes over the same plugins. D2(g)/(h) says both must pass with different fingerprints; the registry trim
for this case therefore adds that check (a separate trim file, so cases 01-11 keep theirs).

`candidate-02-unstated` puts `PatchA.esp` — a plugin the fixture scope does not list — in the order. D1 then makes its
`hashSource` `unstated` with `sha256: null`, and D2(c) accepts the emit's own hash for it under
`validation.identityLimits`. That is what the supplied-combination arm exists to exercise; the
**`supplied-combination` kind itself is not re-fixtured here** because `cmd_plan` derives candidate arms from
`candidate-*.json` and supplied arms from `--supplied`, so one file would produce two arms with one emit stem.
Case-07 already carries the kind and its ESP-only limit sentence.

D3 is asserted on `geometry.bos-overlay` (a `probe-json` adapter, which states no identity of its own): on the
**candidate** arm the consumed block must be `configurationSource: "plan"` with `fingerprint: null` and the
**candidate's** plugin order; on the baseline it must be `configurationSource: "scope"` with the scope's fingerprint.
The reviewed source copies the scope's configuration — order and fingerprint — onto every arm.

## 13. `case-13-reuse-boundary` — absence is a change, on both verbs (R2)

The review deleted a consumed plugin and got `resumeExit: 0` with the baseline navmesh entry `reused`, because
`changed_input` reads `exists() and hash differs`: absence is "no change". Six steps, on one fully-run directory:

| step | damage | what D5 fixes |
|---|---|---|
| `plugin-gone` | delete the generated `FixtureTarget.esp` | D5b names a consumed plugin **gone**; the transitive rule carries it to reverse, diff and the derived order evaluation, and the re-run cannot restore a completed claim because the fake then writes a zero hash that D2(c) rejects |
| `one-arm-input-changed` | append to `candidate-01-reorder.json` | that arm's entries invalidated **by name**, the baseline's still `reused` with no child work — "Independent entries stay reused" |
| `unverifiable-identity` | blank a consumed plugin's sha256 inside an accepted receipt | D5a: a gap, so `refused-reuse(<gap naming the plugin>)` — neither a silent `reused` nor a false `invalidated` |
| `unchanged-control` | none | every terminal-ok entry `reused`, a new runId, **no `running` transition under it** |
| `run-not-only-resume` | delete the plugin, call `run` | "no verb reuses without it": the same invalidation |
| `scope-changed` | append a byte to the scope file | `scope-changed` by name, with "re-run scopeprobe and plan again" |

`baseEmitDependency` asserts D4 on the same run: the candidate `arm` receipt must record the baseline emit
(`consumed.baseEmitSha256` is `null` on every receipt of the reviewed source), the reverse receipt the candidate
emit, and the diff receipt two rows under `consumed.inputs`.

### ruling needed — the exit code of a changed premise

D5 fixes the refusal **name** and the next action, not the exit code. `scope-changed` on `resume` is written as **2**
(refused input: the premise changed before any work), where case-09 has finalize's `registry-changed` at **3**
(a refused result). The name is asserted strictly; the code is asserted as 2 and flagged.

### ruling needed — what the diff receipt records

D4 gives the diff receipt `consumed.inputs: [{part, path, sha256}]` and does not say whether `baseEmitPath/Sha256`
are also filled for it. Only the presence of two input rows is asserted.

## 14. `case-14-evidence-chain` — a completed claim is derived from evidence that still matches (R3)

The review deleted an accepted receipt's recorded output and got `finalize` 0, `bundle` 0, `verify-bundle` 0 and
`verify.json` `complete: true, verified: 64`, with the check still listed as a completed null check. The scenario here
emits **zero rows with judged > 0** on every geometry probe, so the affected check really is a completed null check and
`completedNullChecks` is the list the deleted evidence must fall out of.

Two finalize damages (`evidence-missing`, `evidence-changed`), each asserting: exit 3,
`evaluation.integrity.ok: false` with the path in `defects`, the check **absent** from `completedNullChecks` and
**present** under `missing` with that status, `evaluation.refused: "evidence-integrity"`. The missing-evidence step
then runs `bundle` and asserts exit 3 naming the receipt and the path, no manifest written (or an unchanged one), and
that no `verify.json` in the run can claim a complete bundle.

Four verify-bundle damages, each on its own copy of a healthy bundle, one damage per answer: a deleted `required`
control file (`required`), a receipt output dropped from the manifest's evidence list while the file stays on disk
(`manifest-omits-required` — the R3 hole at the verify layer), altered evidence bytes (`mismatched`), and
`bundleSchema` forced to 1 (`schema-unsupported`, A-1).

**Q42's demo bundle** is verified as A-1's integration input, **copied to a temp directory first** — `verify-bundle`
writes `verify.json` into the directory it verifies, so the original artifact is never verified in place. It is
untracked and lives only in the main checkout; when the path (or `HOUSECARL_EVALRUN_DEMO_BUNDLE`) is absent the case
prints a NOTE and the rule stays covered by the synthetic `bundleSchema` 1 copy above.

## 15. `case-15-portable-incomplete` — portable, valid, and honestly incomplete

D8's two states in one answer. The case generates **its own** fixture root, runs and bundles, copies the bundle
elsewhere, then deletes every original source path — plugins, scope, candidates, registry — and verifies the copy:
exit 0, `bundle.schemaSupported` / `structurallyValid` / `complete` true with nothing missing or mismatched, and
`evaluation.complete: false` with a non-empty `missing` **read from the bundle's disposition, not recomputed**. The
manifest must carry `required`, every `required` path must be present in the bundle, and no `.esp` / `.esm` / `.esl` /
`.bsa` may be in it. Its own fixture root is why deleting the sources cannot damage another case.

The unassessed-media accounting is deliberately **not** re-asserted here: D7 leaves the media rules unchanged and
case-10 already drives a matched and an unmatched item through `bundle --media`.

## Rulings A-2, A-3, A-4 — the three undetermined values, decided

The coordinator ruled on all three `rulingNeeded` items after the oracle was committed, and each ruling made an
assertion **stricter** rather than changing an expected outcome:

- **A-2 (D2(b))** — positions count from **1** and the detail reads `plugin order differs from the request at
  position N: expected X, found Y`, then `; the set differs (missing: …; extra: …)` or `; the same names in a
  different order`. Case-12's `wrong-order` therefore asserts `position 1`, `Skyrim.esm`, `FixtureBase.esp` and
  `the same names in a different order`; `reverse-wrong-reference` asserts `position 3` against the BASELINE's
  request, with `FixtureBase.esp` expected and `FixtureTarget.esp` found.
- **A-3 (D5 premise refusals)** — `run` / `resume` refuse a changed or missing registry/scope with **exit 2**
  (`REFUSED: scope-changed: …` on stderr, no work done); `finalize` refuses with **exit 3** and a refusal
  disposition. Case-13's derived 2 stands and now also asserts the stderr prefix and that no child ran.
- **A-4 (D4)** — the `diff` receipt's `consumed.inputs` has exactly two rows, `part: "arm"` and `part: "reverse"`,
  each with a path and a sha256, and its own `baseEmitPath/Sha256` stay **null**. Case-13 asserts the count, both
  part names, path+sha256 on each, and the null baseEmit.

---

## 16. `case-16-supplied-identity` — a supplied plugin's identity is stated at plan time (Q44)

**Written from Q44's `PLAN.md` (D1–D4 and its fixture section) before the fixture, the fake probe's resolver or
`case_16` existed** — the commit order is the evidence. The machine-readable half is
`case-16-supplied-identity.json`; this section is the reasoning behind it.

Q43 left one honest hole: the request is built from the measured scope, and the scope states no hash for a plugin
the profile does not load. Such a plugin is `hashSource: unstated`, so D2(c) can only record the emit's own hash as
a limit, `reuse_gap` refuses every receipt that consumed the plan's request, and the whole supplied arm re-runs on
every `run`/`resume`. D1 closes it by resolving the plugin at plan time **the way the probes resolve it**.

### The oracle's resolver is written from the C#, not from `evalrun.py`

`_shared/HarnessOrderCli.cs`'s `BuildLayerMap` is the definition: `overwrite`, the view's mod folders in priority
order, the game `Data` folder, then the profile's enabled mods that are not in the view, then its disabled ones —
first sighting of a filename wins, case-insensitive, `.esp/.esm/.esl` at a folder's top level only, an absent folder
skipped. `ProviderOf` gives the layer its name (`overwrite`, the mod folder's own name, `Data`).
`Mo2LoadOrder.ParseModlist` gives the two lists: `+` enabled, `-` disabled, `#` a comment, a `…_separator` name
skipped, **file order with the top line highest**. The fixture's fake probe implements that walk from those two
files. The controller implements the same definition independently. Agreement on the same tree is then evidence;
a copied implementation would only have been evidence of copying (the independent-oracle seam).

### The tree, and why every layer is exercised

`instance/overwrite` is empty (layer 1 falls through). The scope's only `mod` provider is `fixture-mods`, which has
no folder under `instance/mods` (layer 2: an absent folder is skipped, not an error). `game/Data` is empty (layer
3). `modlist.txt` carries `+Fixture Enabled Mod`, whose folder does not exist (layer 4, skipped), a `#` comment and
a `…_separator` line (both skipped by the parser), and then `-Supplied Patch A` above `-Supplied Patch B` — two
disabled folders that provide the SAME filename with DIFFERENT bytes. So the name resolves at layer 5, and
`Supplied Patch A` wins **because of the modlist's line order**, which a directory walk would match only by
accident. That is a real instance's shape (one patch filename provided by two mod folders) in miniature, with the
bytes deliberately different so the copy that was read is visible in the hash.

### What the plan must say

The supplied arm's request row for `FixtureSupplied.esp`: `hashSource: "instance:Supplied Patch A"`, the sha256 of
that folder's copy, a path ending `mods/Supplied Patch A/FixtureSupplied.esp`, and `providedBy` with **both**
copies in precedence order, each hashed — D1's "so a reader sees whether an ambiguity exists and whether it
matters". The other four names keep `scope`. `supplied.outOfView` is `["FixtureSupplied.esp"]` (layer 5: records
load, assets do not), `supplied.payloadMissing` is empty, and nothing on the arm rests at
`blocked-payload-missing`.

Four variants, each on a copy of the generated tree so the case's own run reads an untouched one:

| variant | change | expected |
|---|---|---|
| `no-instance` | plan without `--instance` | `unstated`, null hash, an `unstatedReason` naming the instance |
| `overwrite-copy` | a third copy (bytes C) in `overwrite/` | `instance:overwrite`, `providedBy` three rows, `outOfView` empty |
| `modlist-removed` | delete `profiles/fixture/modlist.txt` | `unstated`, the reason names `modlist.txt`, `providedBy` both copies at layer `(not in modlist.txt)` |
| `modlist-reordered` | `-Supplied Patch B` above `-Supplied Patch A` | `instance:Supplied Patch B` — the ORDER decides, not the name |

`no-instance` is the standing control: its `unstated` row and null hash are today's answer and must stay green on
an unchanged controller. Only the `unstatedReason` field is new.

### What the run, the resume and the two damages must say

- The sweep `arm` receipt of `geometry.navmesh-ownership` on the supplied arm is **accepted with
  `validation.identityLimits == []`** — with the hash stated and met there is nothing left to record.
- Two receipts of that arm whose child states no identity of its own consume the arm's request rows: the
  `FixtureSupplied.esp` row carries the A path, sha(A) and its `hashSource` (D3), and no `identityLimits` entry
  names it. They are `geometry.bos-overlay` — which this fixture's scope makes **not-applicable** (its trigger is
  `{"overlays": true}` and the scope declares none), so its receipt is the accounting one D3 names word for word:
  "a row that ran nothing gets THIS ARM's frozen request" — and `loadability.masters-and-order`, the arm's json
  child that actually ran. The same row is asserted on both, so the rule is not read only off a row nothing
  measured. For the same reason `bos-overlay` is absent from the invalidation list below: an entry that never
  reached a terminal-ok status is never reused and never invalidated.
- `resume` on the unchanged run: every terminal-ok entry of the supplied arm `reused`, the printed
  `0 refused-reuse`, no `running` transition and no child — asserted from the journal exactly as case 13's
  `unchanged-control` does. This is outcome 3 of the packet, and the whole point of the change.
- `wrong-copy` (a new `identityDamage` kind: the fake reads the folder the rule names instead of the precedence
  winner): the arm receipt is `accepted: false`, cause `identity-mismatch`, and the detail names both folders and
  both hashes at 12 hex — D2's amended detail carries the path as well as the hash, which is what makes "a probe
  read a different copy" legible instead of a bare pair of digests.
- The bytes at A changed after the run, then `resume`: the arm's json receipts and its sweep `arm` part go
  `invalidated(…)` with "changed on disk" and the A path, the baseline's entries stay `reused`, and
  `0 refused-reuse` — a changed file is an invalidation, never a gap.

### `rulingNeeded` — five places the definitions do not settle a value

Written as expectations and flagged, never guessed silently and never narrowed to make a line pass:

1. **The `unstatedReason` wording.** D1 fixes what it must say, not how. Only the naming token is asserted
   (`instance`; `modlist.txt`).
2. **Ruled, not open — `payloadMissing` with no `--instance`.** Ruling A-1 (coordinator, 2026-09-12): the name
   stays in `supplied.payloadMissing` and the arm's checks stay `blocked-payload-missing` exactly as before Q44,
   because D1's "no layer provides the name" covers the case where nothing could be consulted. Asserted strictly.
3. **The `modlist-removed` variant's `supplied` block.** Whether a name whose copies exist but cannot be ordered
   belongs in `payloadMissing`, in `outOfView`, or in neither is undetermined. Nothing is asserted about it — the
   row, the reason and `providedBy` are.
4. **`providedBy` on a `scope` row.** D1 gives the field in the shape of an instance-resolved row. A scope row is
   expected to be unchanged from Q43; nothing is asserted.
5. **`hashSource` on the consumed copy of a `scope` row.** D3 says the consumed block carries the request rows
   with their `hashSource`; only the supplied row's is asserted.

### Three folds from the verifier's read (rulings A-3, A-4, A-5)

**A-3 (findings F1, F5) — a partial walk is not a resolution.** `HarnessOrderCli` always walks the game `Data`
folder, and Data sits ABOVE the out-of-view block, so a Data copy outranks both mod folders. The plan can only
walk it when `--game` was given. Two more variants: `no-game` (instance + profile, no game) and `no-profile`
(instance + game, no profile) both stay `unstated` with a null hash, and the `unstatedReason` names what is
actually missing — the token `game` in the first, `profile` in the second, never `modlist.txt` for a profile that
was never named. `providedBy` still lists the copies the partial walk did find (as their folders in `no-game`,
where the modlist was read; as `(not in modlist.txt)` in `no-profile`, where it was not), so the reader sees what
was seen rather than nothing at all. `provides()` is untouched, so `supplied.outOfView` stays today's answer.

**A-4 (finding F4) — an empty `Mods` list is a real empty view.** `HarnessOrderCli.cs:147` is
`cand.Mods ?? comp.EnabledMods`: only a null or absent `Mods` falls back. Both Python resolvers used truthiness,
which silently turns "this candidate stages no mods" into "use the profile's". The fake's `resolve_ctx` is
null-coalescing now, and the discriminating pair is fixtured: on a copy of the tree with a fourth copy (bytes D)
planted in `mods/fixture-mods/` — the scope's own mod provider — the supplied candidate with `Mods` **absent**
resolves `instance:fixture-mods` at layer 2 and is IN the view (`outOfView` empty), while the same candidate with
`"Mods": []` cannot see that folder at all and resolves `instance:Supplied Patch A` at layer 5. Same tree, same
bytes on disk; only the presence of a key differs, which is the only way a truthiness resolver is visible.

**A-5 (finding F3) — the identical-copy limit, fired.** D2 accepts an emit whose hash matches at a different path
and records an `identityLimits` row instead of rejecting. Nothing in case 16 made that branch fire: its two
folders carry different bytes by design, so the verifier could delete the branch and the case stayed green — while
the real shape seen in an instance is identical bytes in two folders (one combo patch installed twice). A run-level
arm on a copy of the tree adds `Supplied Patch A copy` with bytes identical to A's, listed below A so A still
wins, and points a `wrong-copy` damage at it: the sweep arm receipt is **accepted**, `validation.identityLimits`
has exactly one row for `FixtureSupplied.esp` whose `recordedSha256` is sha(A), whose `plannedPath` ends in
`Supplied Patch A/FixtureSupplied.esp` and whose `emitPath` ends in `Supplied Patch A copy/FixtureSupplied.esp`,
with both folders named in the detail; the plan for that tree lists three `providedBy` rows. With the `wrong-copy`
step above — the same damage over different bytes, which rejects — the pair shows the rule turning on content
rather than on the path.
