# The oracle for `evalreport` — what the report compiler must answer on the fixtures

**Written by agent O before `evalreport.py` existed** (Q45, roadmap 4.x; PLAN.md
`_claude/runs/2026-09-12-illustrated-reports/PLAN.md` §2 definitions D1–D8 and the packet's acceptance table).
Every value here is derived by hand from those definitions; where they leave a property open, the reading the
acceptance table needs is chosen and named below. The commit that adds this directory precedes the compiler's
code; its hash and time are recorded at the end of this file.

The machine-readable half is `common.json` (asserted on every case that compiles) plus one `R<n>-*.json` per case.
`test_evalreport.py` reads those, never this prose. If the code and the oracle disagree, **the oracle is not edited
to match the code**: the driver reports the disagreement with both values and the D-rule, and Fable rules.

## 0. The inputs — real bundles from the real controller

Every case bundle is generated at test time by driving `_geometry-harness/evalrun.py` (`plan` / `run` / `finalize`
/ `bundle`) with `test_evalrun.py`'s fixture engine: the tiny plugins, the trimmed registry (15 checks, of which
`loadability.dangling-references` and `records.cell-attributes` are `mcp-only`,
`functional.collective-route-connectivity` `unsupported`, `environment.generated-output-regeneration` `manual`,
`functional.linked-dependencies` unknown-applicability), the fake probes that write the real conflict-file format,
the real `conflictdiff.exe` and `ordersweep.py`. Consequences the oracle rests on:

- **No fixture evaluation is ever complete** (the mcp-only / unsupported / manual checks keep `missing` non-empty),
  so `claim` is `incomplete` on every case and the bundle verifies complete on every undamaged case — the
  acceptance table's first row is the standing state of every fixture.
- Arm ids are the candidate stems (`baseline`, `candidate-01-reorder`, `candidate-03-combined`, …).
- A disposition `findings` row is `(checkId, armId, part, findings: n, verdict, strength)`; the `diff` part carries
  conflictdiff's verdict word. A `missing` row is `(checkId, armId, part, status, cause, required, family)` where a
  rejected extraction has status `rejected(extraction-failed)` and cause `extraction-failed`. A
  `completedNullChecks` row records the NUMBER of coverage lines; the judged count (42) lives in the emit's
  `Coverage` under `evidence/` in the bundle.
- The manifest's evidence ids are 16 hex; emit rows carry `checkId`, `armId`, `strength`, `recordedBy`.

Nothing from the College bundle (`_claude/runs/2026-09-12-supplied-plugin-identity/lab/run/bundle`) is copied here;
it was read only to confirm the disposition and manifest shapes above.

## 1. Case map

| case | shape | acceptance row | what it discriminates |
|---|---|---|---|
| R1 intact-incomplete | case-15 (case-01 scenario) | intact incomplete evaluation | missing required checks and the sweep's verdicts survive into report and chapters; `bundleComplete: true` beside `evaluationComplete: false` |
| R2 null-vs-missing | case-03 | qualified null vs missing extraction | kind `null` (coverage 42) vs kind `missing`; both rowsTotal 0 |
| R3 many-findings-long-names | new (62 rows/arm, 4 groups, 80+ char names) | many findings and long names | grouping key, sort, rowsPerGroup abridgement with omissions, exhaustive expansion, print ≤ 4 pages |
| R4 repaired-plus-regression | case-07 | repaired rows plus a new regression | F-001 kind regression, verdict NEUTRAL verbatim, repaired findings separate and later |
| R5 media-mismatch-same-basenames | case-10 + D5 media.json | media mismatch and same basenames | two `M-` ids for one basename, three unassessed reasons by name, the asserted sentence |
| R6 changed-or-missing-source | damaged copies of R5's bundle | changed or missing source | `bundle-refused`, `schema-unsupported`, determinism, relocation |

## 2. Readings chosen where D1–D8 are open (and why)

1. **`scope.completion.byCause` is keyed by the missing row's `status` string** (`mcp-only`, `unsupported`,
   `manual`, `unknown-applicability`, `rejected(extraction-failed)`), with counts equal to the disposition's. D2's
   example key `mcp-only` is a status word, not the cause sentence ("the check did not produce an accepted detector
   result"), which could not group anything.
2. **A finding is one `(checkId, armId, part) × (Kind, A, B)` bucket; a group is the `(checkId, Kind, A, B)` key
   across arms and parts.** D2 gives a finding ONE `groupId` and a group MANY `findingIds`; D3's grouping key is
   `(checkId, Kind, A, B)`. `rowsTotal` on a finding is its bucket's emitted row count, so the sum over one
   `(checkId, armId, part)` equals the disposition's `findings` number — counts are complete whatever is shown.
3. **`rowsShown ≤ rowsPerGroup` is per finding**, `len(rows) == rowsShown`, and every finding with
   `rowsTotal > rowsShown` has an `omissions` row whose `where` starts with `evidence-index.md#` and names the
   finding or its group.
4. **Section (2) of D3 puts every required missing check in the report**, and D6's closing card lists them, so
   `report.md` and the union of the `missing` / `closing` chapters' captions name every required missing `checkId`.
5. **The classification word (`PASSES-COMPLETED-CHECKS`, `REGRESSION`) appears in `report.md`** — it is ordersweep's
   own scoped comparison result, which the acceptance table says must be preserved. `PASSES-COMPLETED-CHECKS` is
   the one token exempt from the forbidden-word scan, because it is quoted, not asserted; every other `pass` /
   `passes` / `passed` / `compatible` / `clean` (whole words, case-insensitive) fails.
6. **The null check's coverage number (42) is in the null finding's `counts`** and in `report.md` — D3 (5)
   "completed null checks with their coverage lines"; the compiler must read the emit, since the disposition row
   holds only the line count.
7. **The `[E-…]` / `[M-…]` token resolves to an anchor** when `evidence-index.md` has a markdown heading line
   containing the id, or `id="<id>"` / `name="<id>"` / `{#<id>}`. Any of those is a usable link after relocation.
8. **Unassessed media rows are matched by basename**, carry `assessed: false` and exactly the D5 reason, and are
   never in a chapter's `mediaIds`. Whether they get an `M-` id is not asserted (their file may be outside the
   bundle after relocation).
9. **Print status `within-limit` with `pages ≤ 4` is asserted for R1, R2, R4 and R3-concise** (the acceptance row
   for R3 asks for four pages). The driver counts the PDF's pages itself (`/Type /Page` objects, else the root
   `/Count`) and requires equality with `print.pages`. A machine with no Chrome/Edge fails these — the packet's
   inventory measured both present, and a missing browser is a failure to report, not a skip.
10. **Refusal exit codes are not fixed by D1**; non-zero is asserted, plus the name in the tool's own words with the
    case directory's name scrubbed out.
11. **Fidelity rule spellings are the oracle's** (`number-unbound`, `arm-unbound`, `name-unbound`, `check-unknown`,
    `forbidden-word`); the driver requires the refusal's `rule` to contain a keyword (`number`/`count`, `arm`,
    `name`/`plugin`, `check`, `word`/`compatible`/`vocabulary`/`claim`) and the refused id set to match exactly.
12. **`narrate-apply` rewrites the presentation file it is given** (`narration.refused` and `findings[].plain` live
    there); the driver runs it on a COPY per pair. An accepted answer replaces `plain`; a refused one leaves the
    template sentence.
13. **Wrong arm = an arm inside the plan that is not the finding's.** D7's letter only forbids arms *outside the
    plan*, but lists "wrong arm" as a fidelity case; the acceptance table needs a mis-attributed regression refused,
    so the finding's `armId` is read as one of its bound names (see `fidelity/03-wrong-arm.json`).

## 3. What the driver cannot fixture here, and why

- **The reel** (D6) — agent B's `test_evalreel.py`; only the chapters and captions the reel reads are asserted.
- **Real media bytes** — PNGs are 4×4 PIL images and the "video" is a stub file; the bundle side only hashes.
- **Case-06's RECLASSIFIED arm** (attribution non-zero) is not re-fixtured; R4 asserts `reclassified 0` /
  `attributed 0` as separate fields from `repaired 1` on the same finding.
- **The abridgement ladder** (D4, rowsPerGroup 3→2→1) is only exercised if R3's first render is over the limit;
  the assertion (`rowsShown ≤ 3`, `within-limit`) holds on either path.

14. **R5's mismatch item is A-3's trap** (PLAN.md §5 amendment, Fable 18:58, after this oracle's first commit):
    an item declared on the candidate arm carrying the baseline order's fingerprint — which the candidate's
    `reverse` receipt also consumed — is `fingerprint-mismatch`, because only the arm's own receipts (part null /
    `arm`) are compared. The pre-A-3 `bundle --media` accepts it (measured: it did), so the driver fails against
    the base behaviour and passes once agent B's rule is integrated.

15. **Ruling A-4 (Fable, 19:20): the forbidden-word rule binds the compiler's OWN sentences** — `plain`, section
    leads, chapter captions, claim sentences, template prose — not strings it quotes verbatim from the check
    registry (`title`, `evidence.claim`, `evidence.limits`). The driver reads those strings from the scanned
    bundle's registry copy and exempts them exactly as `PASSES-COMPLETED-CHECKS` is exempt (the registry's own
    words include "…orders passes the checks that were actually run" and "…unless --door-idx is passed").
16. **Oracle correction, sourced from the disposition (Fable, 19:23):** R1 and R2's `byCauseIncludes.unsupported`
    is **4**, not 2. `functional.linked-dependencies` is a `derived` check the controller reports as `unsupported`
    at run time (bundle standard §4.6), on both arms, beside `functional.collective-route-connectivity`; the
    literal 2 had been read off the registry's adapter kind. The rule (counts are the disposition's) was right and
    unchanged; the two literals were corrected to what the generated dispositions record (14 required missing rows
    on R1, 20 on R2).

17. **Ruling A-12 (Fable, 19:53): strings the compiler quotes verbatim from the DISPOSITION are quotations
    too** — every string leaf under `disposition.unassessed` and the scope copy's `unmeasured[].why` (R1's
    "…whether DynDOLOD or an occlusion pass must be regenerated…" is the scope's own wording; "pass" there is
    not a verdict). Both scans exempt them exactly as the registry strings and `PASSES-COMPLETED-CHECKS`.
18. **The no-netting check accepts the negation (Fable, 19:53).** "…which do not net against the new defect"
    is the packet's own wording. R4 now refuses only an AFFIRMATIVE netting claim: a `net` / `nets` / `netted` /
    `netting` word not preceded within three words by `not` or `never`, in F-001's sentence and in `report.md`.
    The substring rules `net`, `net repair` and `net effect` were replaced by that rule; `on balance` and
    `outweigh` stand.

19. **The scans normalise both sides (Fable, 20:12).** The report renderer escapes markdown characters and
    wraps or shortens a quoted sentence, so an exemption that matched the plan text missed its `report.md`
    rendering. Both scans now strip backslash escapes (`\_ \* \- \#` …) and collapse whitespace on the text and
    on every exemption, and an exemption longer than 60 characters also matches a truncated rendering by its
    first 60 characters. The forbidden words and the exemption sources are unchanged.

20. **A-14 (Fable, 20:42): media never sits on a `reverse` part.** The reverse part measured the baseline
    order, so a candidate-arm picture attached to it would illustrate the wrong configuration. R5 asserts no
    finding with `part: reverse` carries `mediaIds`, and the accepted candidate-arm item sits on that arm's
    `arm` (or `diff`) finding.
21. **A-11 (Fable, 20:42): an unreachable limit keeps the least abridged content-preserving step.** R4's
    `within-limit` / `pages ≤ 4` expectations stand (they record the open decision on a machine where R4 prints
    over the limit); when the status IS `over-limit`, `print.kept` names `layout: compact`, `rowsPerGroup` equal
    to `settings.rowsPerGroup`, `figuresPerFinding: 1`, and `print.reason` says the limit is unreachable.
22. **D5 gains `provenance-missing`** (checked after `fingerprint-source-missing`). Every R5 item carries
    `provenance: {tool, capturedUtc}`; a sixth, otherwise valid item with no provenance is unassessed by that
    name. The closed reason list in `common.json` carries it.
23. **Testing another tree's tools:** `HOUSECARL_EVALREPORT` names the compiler under test and
    `HOUSECARL_EVALRUN` the controller the fixtures are built with (an integration head, or a worktree carrying
    the media-boundary change); the defaults are this checkout's own files.

## 4. Provenance

- Expectation commit: `8222044d50e045f99cb0c423b21f22582d5abbed`, 2026-09-12T18:50:06-07:00 — the expected
  JSONs, this file and the six fidelity pairs, before any compiler code existed in any worktree read by this agent.
- The generator, the driver and the A-3 amendment to R5 follow in later commits on `q45-agent-o`.

## Amendment — the default page limit is 8 (Piranha, 2026-09-12)

The packet asked for at most four printed pages by default. On the real demonstration bundle and on the
two-arm fixtures the compiler could not reach four without hiding required content (6–7 pages in the compact
layout with a figure; the ladder and the kept step are recorded in the manifest). Piranha raised the default to
eight; `settingsDefault.maxPages`, `print.maxPages` and every case's `pagesAtMost` follow. The ladder, the
figure rule and the over-limit reporting are unchanged: a case that exceeds eight still says `over-limit` with
the count.
