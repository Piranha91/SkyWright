# Evaluation status — plan `63e36b97b433`

- registry version 4 (`92c0b41d82b8`)
- arms: `baseline` (baseline), `candidate-01-reorder` (candidate)
- states selected: baseline
- **evaluation.complete: False**   claim: `incomplete`
- tool identity: verified — every accepted local result is bound, per role, to the program identity this plan froze; evaluation.integrity lists any receipt that is not

This is the review index, not the report. Every line below is derived from the ledger and the validated receipts; nothing here is a judgement this runner made about the mod.

## Findings

| check | arm | part | findings | verdict | strength |
|---|---|---|---|---|---|
| `records.cell-attributes` | baseline | - | 1 | - | record-fact |
| `geometry.navmesh-ownership` | baseline | arm | 1 | - | calibrated-inference |
| `geometry.navmesh-ownership` | candidate-01-reorder | arm | 1 | - | calibrated-inference |
| `geometry.navmesh-ownership` | candidate-01-reorder | reverse | 1 | - | calibrated-inference |
| `geometry.object-overlap` | baseline | arm | 1 | - | calibrated-inference |
| `geometry.object-overlap` | candidate-01-reorder | arm | 1 | - | calibrated-inference |
| `geometry.object-overlap` | candidate-01-reorder | reverse | 1 | - | calibrated-inference |
| `geometry.placement-contact` | baseline | arm | 1 | - | calibrated-inference |
| `geometry.placement-contact` | candidate-01-reorder | arm | 1 | - | calibrated-inference |
| `geometry.placement-contact` | candidate-01-reorder | reverse | 1 | - | calibrated-inference |

## Completed null checks

- `loadability.masters-and-order` [baseline] — no finding, 0 coverage line(s) declared
- `loadability.masters-and-order` [candidate-01-reorder] — no finding, 0 coverage line(s) declared
- `geometry.navmesh-ownership` [candidate-01-reorder/diff] — no finding, 0 coverage line(s) declared
- `geometry.object-overlap` [candidate-01-reorder/diff] — no finding, 0 coverage line(s) declared
- `geometry.placement-contact` [candidate-01-reorder/diff] — no finding, 0 coverage line(s) declared
- `patch.order-evaluation` [candidate-01-reorder] — no finding, 0 coverage line(s) declared

## Completed catalogues (rows that are evidence, not defects)

- `state.switch-census` [baseline] — 3 rows catalogued (catalogue); a catalogue completes its check and is never a finding
- `state.switch-census` [candidate-01-reorder] — 3 rows catalogued (catalogue); a catalogue completes its check and is never a finding
- `patch.author-advice` [baseline] — 1 rows catalogued (discovery); a catalogue completes its check and is never a finding

## Imported evidence and where it came from

Origin is checked for accidental relabelling only: a capture token and timestamps are public and rewritable, so this is not proof of authenticity.

| check | arm | origin |
|---|---|---|
| `records.cell-attributes` | baseline | fresh: captured for this plan's arm (capture token and time checked) |

## Missing checks (each keeps the evaluation incomplete)

| check | arm | status | cause |
|---|---|---|---|
| `loadability.dangling-references` | baseline | `mcp-only` | the check did not produce an accepted detector result |
| `loadability.dangling-references` | candidate-01-reorder | `mcp-only` | the check did not produce an accepted detector result |
| `records.cell-attributes` | candidate-01-reorder | `mcp-only` | the check did not produce an accepted detector result |
| `functional.linked-dependencies` | baseline | `unknown-applicability` | applicability is unknown, and unknown never becomes not-applicable: the scope lists script-placement under unmeasured: "a reference a Papyrus script moves or creates at runtime is in no static walk". |
| `functional.linked-dependencies` | candidate-01-reorder | `unknown-applicability` | applicability is unknown, and unknown never becomes not-applicable: the scope lists script-placement under unmeasured: "a reference a Papyrus script moves or creates at runtime is in no static walk". |
| `functional.linked-dependencies` | baseline | `unsupported` | the check did not produce an accepted detector result |
| `functional.linked-dependencies` | candidate-01-reorder | `unsupported` | the check did not produce an accepted detector result |
| `functional.collective-route-connectivity` | baseline | `unsupported` | the check did not produce an accepted detector result |
| `functional.collective-route-connectivity` | candidate-01-reorder | `unsupported` | the check did not produce an accepted detector result |
| `environment.generated-output-regeneration` | baseline | `manual` | the check did not produce an accepted detector result |
| `environment.generated-output-regeneration` | candidate-01-reorder | `manual` | the check did not produce an accepted detector result |

## Unknown applicability (never read as not-applicable)

- `functional.linked-dependencies` — the scope lists script-placement under unmeasured: "a reference a Papyrus script moves or creates at runtime is in no static walk".

## Order evaluation per arm

- `baseline` (baseline): classification **n/a**, regression: False, probes: none
- `candidate-01-reorder` (candidate): classification **PASSES-COMPLETED-CHECKS**, regression: False, probes: contactprobe, navownprobe, overlapprobe
  - navownprobe: DIFFERENTIAL-INERT — repaired 0, reclassified 0, created 0 (new 0 / attributed 0), unmeasured 0
  - overlapprobe: DIFFERENTIAL-INERT — repaired 0, reclassified 0, created 0 (new 0 / attributed 0), unmeasured 0
  - contactprobe: DIFFERENTIAL-INERT — repaired 0, reclassified 0, created 0 (new 0 / attributed 0), unmeasured 0

## Limitations and what was not assessed

- states not explored: [{"armId":"baseline","checkId":"state.conditional-geometry","code":"no-key","count":1,"kind":"single","reason":"The census row names no FormKey for this switch, so no state can name it.","subject":"(unnamed switch)"},{"armId":"baseline","checkId":"state.conditional-geometry","code":"no-key","count":1,"kind":"single","reason":"The census row names no FormKey for this switch, so no state can name it
- candidates held back: {"byMaxCandidates":[],"count":0,"fromManifestBudget":{"evaluated":1,"legalNotEvaluated":0,"note":"fixture manifest: the search was not run"}}
- families unsupported or manual: ["environment","functional","loadability","records","runtime"]
- scope extents the footprint probe does not read: [{"extent":"script-placement","why":"a reference a Papyrus script moves or creates at runtime is in no static walk"},{"extent":"alias-fill","why":"a quest alias fill is decided at runtime, so the object it points at is not known statically"},{"extent":"bos-swaps","why":"the Base Object Swapper ini layer is not scanned; the ini list is not the set of swaps"},{"extent":"asset-replacers","why":"a mes
- media: media is identified and assessed by `bundle --media`; see the bundle manifest
- runtime: no runtime observation was supplied to this run: nothing here says what the engine did at play time

## Regeneration needs

- none declared by the registry for the checks in this plan

## Explicit decisions

- --select: not used
- --exclude: not used

## Ledger

| status | entries |
|---|---|
| `blocked(...)` | 2 |
| `completed` | 19 |
| `manual` | 2 |
| `mcp-only` | 3 |
| `not-applicable` | 5 |
| `unsupported` | 4 |
