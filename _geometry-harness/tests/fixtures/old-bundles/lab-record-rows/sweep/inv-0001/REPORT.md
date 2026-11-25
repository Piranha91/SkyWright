# ordersweep report

baseline: `candidate-00-baseline.json`; probes: contactprobe, navownprobe, overlapprobe; candidates evaluated: 1; reverse measurement: on; population 7f65e2f5011a for contactprobe, overlapprobe, the other probes on the whole order

| candidate | changes | contactprobe | navownprobe | overlapprobe | rules | classification |
|---|---|---|---|---|---|---|
| candidate-01-reorder | moved FixtureTarget.esp above FixtureBase.esp | INERT r0 c0(new 0/attr 0/unm 0) q0 u0 | INERT r0 c0(new 0/attr 0/unm 0) q0 u0 | INERT r0 c0(new 0/attr 0/unm 0) q0 u0 | — | **PASSES-COMPLETED-CHECKS** |

Cells read: conflictdiff verdict; r = repaired (physical predicate Holds -> Gone); c = created (entered with the predicate holding), split by the reverse measurement into NEW physical defects (a regression), ATTRIBUTED (the same physical state under the baseline order — the class's history-dependent attribution, not an engine difference) and UNMEASURED; q = reclassified (changed hands, defect intact); u = unmeasured.

- `candidate-01-reorder` configuration delta vs baseline: - plugin order changed: 1 pair(s) flipped, first FixtureBase.esp / FixtureTarget.esp

## What was established

- 1 candidate(s) pass the completed checks within this scope: `candidate-01-reorder`.
  Passing the completed checks is a statement about the probes named above, in the population and state they ran in; it is not a compatibility claim beyond them.
- No passing candidate repairs anything the completed checks flag in the baseline: the passing candidates are alternatives that do not regress, and the user's own order stands.

## Search outcome (from the manifest)

- Budget: {"evaluated": 1, "legalNotEvaluated": 0, "note": "fixture manifest: the search was not run"}
- Contradictions: []

Cache decisions: `cache-log.txt`; per-run logs: `logs/`; the RunLog history carries every child's timing.
