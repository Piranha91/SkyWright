# What an AI-side evaluation adds to SkyWright's evaluation bundle

**Class: LIVING.** The houseCARL integration module's part of the bundle contract. SkyWright's own part, what the
app's controller (`runcontrol`) plans, runs, finalizes, bundles and verifies, is
[`docs/evaluation-bundle.md`](../../docs/evaluation-bundle.md); this document holds only what an evaluation driven by a
language model adds to it: record and asset reads imported from houseCARL's server, the live switch-flip check, the
record rows derived from those reads, and supplied media. Paths are relative to a SkyWright checkout. The evaluation
that produces them is run by `integration/housecarl/evalpipeline.py` and driven by the `world-edit-compatibility`
skill (`integration/housecarl/claude-plugin/skills/`).

**Where the checks are today.** The import, live-receipt and media rules below are still applied by `runcontrol`
(`import-results`, `bundle --media`) in this version; the sections say which command applies each.

## 1. Importing an existing MCP result


`runcontrol import-results --run RUN --file result.json` completes one planned check whose registry adapter kind is
`mcp-only`. It is a generic evidence lane, not an adapter for a particular tool. Runnable checks and checks whose
detector is genuinely unsupported cannot be imported through it.

The schema-1 import document carries `planId`, `checkId`, `armId`, `state`, `capturedUtc`,
`tool: {name, version?}`, the full emitted `configuration`, a `result`, and `outputs`. `tool.name` may name the
capture workflow or an ordered batch when the check composes several existing MCP calls; it must not imply that
one tool performed the whole check when it did not. The result is
`{detector: finding | no-finding, findings, coverage, note?}`. Every coverage row names a non-empty population
and uses non-negative integer `Judged` and `Unjudgeable` counts, and may state `Members`: how many members of that population the capture found on this arm (a non-negative integer; only the importer knows it). Configuration uses the receipt schema's
`{schemaVersion, fingerprint, source, plugins, providers, overlays, population, state}` shape. Outputs must include
exactly `{role: request, path, sha256}` and `{role: response, path, sha256}`, and may add one
`{role: rows, path, sha256}`: the result's rows in the conflict-file shape (`Conflicts[{Kind, A, B, Summary,
Necessary[{FormKey, ...}]}]`), every `A` and `B` a plugin of the arm (else `rows-foreign`, naming the plugin; not a
conflict file: `rows-invalid`). It is copied as `NN-rows.json`, and the report compiler reads a check's rows from it. Each file may be an ordered batch,
but it must retain the actual raw MCP request or response entries without replacing them with an author summary.
Paths are relative to the import document unless absolute. Only the import document and its two outputs are
copied into the run and bundle (with the `rows` output when there is one): `result.note` is carried verbatim and
unchecked, so it must not point a reader at any other file (an analysis folder) as if it travelled with the
evidence — such a path resolves from the capture folder only, and nowhere in the run or bundle. Rows travel only as
the `rows` output or as a record-rows enrichment (§8); a note may say where other material was kept outside the
bundle only by naming that as outside it.

Every output, plugin and overlay hash is checked against the file on disk before acceptance. Copied evidence is
hashed again before the atomic directory rename, so a file that changes during import cannot acquire an accepted
receipt. The configuration
must match the arm's frozen plugin order and content hashes, provider/overlay fingerprint (the arm's OWN providers,
§3), population, state, and candidate source — that last one with §4.3's reuse exception: a declared reuse's capture names the candidate file of the arm it was measured against, and a difference in that NAME is recorded as an identity limit rather than refused, while everything else in this list still decides the import. Missing, foreign, stale, zero-assessed or contradictory evidence refuses without replacing the
existing `mcp-only` accounting receipt. A genuine known-empty population is never inferred: zero judged refuses
(`extraction-failed`, with a step that can succeed) unless two independent facts hold - every coverage row states
`Members: 0`, and the arm loads none of the plan's target plugins. Then the import is accepted as nothing to
judge: the receipt and the ledger entry read `not-applicable` with one sentence saying why, the copied evidence is
kept, and finalize counts the row answered. `Members: 0` on an arm that loads a target, members stated above 0,
or any unjudgeable count still refuses.

**Origin.** Every import document declares where its evidence came from. There is no default.

- `origin: "fresh"` — captured for this plan's arm. Every MCP `initialize` request in the request output must carry
  `params.clientInfo.name` equal to `evalrun:<planId>:<armId>` (this plan's id and the document's arm id, exact
  string equality; `clientInfo.version` is free for the capture wrapper's own name), and `capturedUtc` must not be
  earlier than the plan's `createdUtc`, compared as instants. The request output is read as JSON Lines — every
  non-blank line one JSON value, as a stdio capture writes one message per line; a line that is an array is a
  JSON-RPC batch whose object members are messages, and annotation lines such as batch markers are allowed. An
  `initialize` request is an object whose `method` is exactly `initialize`. The bytes read are the hash-checked
  bytes. A line that is not JSON, a file with no `initialize`, or any `initialize` without the exact token refuses;
  the token appearing anywhere else (a call's arguments, a marker line) does not count. `reusedFrom` must be absent.
- `origin: "reused"` — captured for another setup, in this evaluation or another one.
  `reusedFrom: {planId, armId, resultSha256}` is required; `resultSha256` is the sha256 of the import document the
  source evaluation **credited** — its receipt's `imported.sourceSha256`, which is the `result.json` in that
  receipt's own import folder (`imports/<key>/`, or `imports/<key>~<n>/` when that evaluation re-imported). A source
  naming the destination's own (plan, arm) refuses: that is not reuse. A source naming **another arm of this plan**
  is accepted — two setups with identical configurations are measured once — and the receipt records which statement
  it is in `imported.reusedWithin` (`this-evaluation` or `another-evaluation`), because "not measured for this
  evaluation" and "not measured for this setup" are different claims and every reader makes the right one.
  A **within-evaluation** source is additionally checked against the plan, because it is the one source this
  controller can check without reading another run: the named `armId` must be an arm of this plan, and its
  frozen `requested` plugins (names and bytes, in order), providers (kind and name, in order), overlays and
  population must equal the destination arm's. A reader of a within-evaluation reuse is told the source
  setup's tested configuration is the same; where the plan itself says otherwise the import refuses, rather
  than letting that sentence be printed. A cross-evaluation source is NOT checked this way — the other
  evaluation's plan is not read — and its wording claims correspondingly less. A receipt
  written before that field existed is read as `unstated` and keeps the cautious cross-evaluation wording. The token and capture-time checks do not apply; every other check does, against the DESTINATION arm — with
  the single, narrow exception of §4.3's **source** rule, which a reused capture cannot satisfy by
  construction. A destination arm whose candidate file merely has a different NAME is not an
  `identity-mismatch`: the difference is recorded as an identity limit and the capture keeps its own
  `Configuration.Source`. What credits the evidence is configuration equality — the emit's stated plugin names
  and order, their bytes, its overlays, and a fingerprint equal to the one recomputed from this arm's frozen
  request — never the filename and never the declaration itself.
  The source run is not read: the receipt records the source as declared (`reusedFromVerified: false`), never as
  verified provenance.

A missing or unknown `origin` refuses as `origin-missing`/`origin-invalid`. An envelope written before origin was
required has two honest paths: import it as `reused` naming the evaluation it was captured for, or capture again
with the token and import that as `fresh`. Relabelling it `fresh` is refused because its requests name another
capture.

These checks catch evidence relabelled by mistake — the V2 reproduction, where editing only `planId`, `armId` and
`configuration.source` let one plan's retained evidence pass as another plan's measurement. Two limits are part of
the policy, not oversights. The first is stated wherever imported evidence is described — this section, STATUS.md
and the report's evidence index; the second is stated here, and the artifacts keep to it by saying "captured for
this plan's arm" and never "for this check":

- **It is not proof of authenticity.** The token is public in `plan.json`, and a caller can rewrite a request file,
  its hash, the timestamp and the document.
- **The token binds the plan and the ARM, not the check.** One capture of a configuration serves several mcp-only
  checks of the same arm, so `fresh` says "captured for this plan's arm", never "captured for this check": evidence
  relabelled from one check to another of the same arm is not caught by the token. What a check's own statement
  rests on is its `result` and `coverage`. This is the scope of the token as decided, not a claim that a check
  dimension is impossible — a later design could, for instance, have a capture declare the checks it supports and
  bind that declaration — but nothing here checks one today.

**Not every row accepts an import.** Only a row the plan recorded `mcp-only` is open for one. A row the plan
answered otherwise — `not-applicable` for a check the registry says does not apply to this arm — refuses
`wrong-check`: an import completes a check the plan left open and never overrides the plan's answer.

**Prerequisites.** An import is refused, before anything is written, when `prereq_satisfied` (the gate the sweep and
derived checks use, §4.3/§4.6) names a prerequisite for the arm: a prerequisite measured and refused on the arm
decides for it even when the baseline's was accepted; one not attempted on the arm falls back to the baseline;
`not-applicable` is an answer. The refusal names the prerequisite and leaves the `mcp-only` entry and receipt as
they were. The gate applies to both origins.

On acceptance, the importer copies the manifest and raw request/response beneath
`RUN/imports/<checkId>__<armId>/`, records them as normal receipt outputs, and changes that ledger entry to
`completed`. The receipt's `imported` block records `origin`; `fresh` adds `originCheck {token,
initializeRequests, capturedUtc, planCreatedUtc}`, `reused` adds `reusedFrom`, `reusedFromVerified: false` and a
note. Resume, finalize, bundle and relocated-bundle verification then re-hash the same evidence and configuration
inputs. `disposition.json` lists every credited import under `importedEvidence` with its origin; a receipt written
before origin existed is listed as `unknown`, never as fresh. A `reused` row carries `sourceDisclosure`, which is
exactly one of **three** states and is read from §4.3's source limit rather than re-derived, so the
disposition and the receipt cannot disagree: `other-file` — the capture names a DIFFERENT candidate file,
given as `capturedAgainst`, with this arm's own as `armCandidate`; `unstated` — the capture names no
candidate file at all, which is a limitation in its own right and keeps `armCandidate` so a reader is told
what was not confirmed; or **no `sourceDisclosure` key**, when the two names agree and there is nothing
extra to say. Every reader must tell the three apart: filtering on a truthy filename silently merges
`unstated` into the no-difference case, which is the opposite of what it means. The executor re-using an accepted receipt on resume
(ledger `reused`) is a different thing from an import's declared origin `reused`.

`run` and `resume` re-apply the same gate to every credited import: when it no longer holds (typically an import
admitted on the baseline's answer before the arm's own prerequisite was measured and refused), the entry becomes
`blocked(prerequisite:<id>)` with the ordinary blocked accounting receipt, stays blocked by name on later resumes
and ledger rebuilds while the prerequisite still fails, and counts nowhere. An import that `resume` stops crediting
for the ordinary reason — its own or a prerequisite's evidence is not what the receipt states, so the entry is
`invalidated(...)` or `refused-reuse(...)` — keeps THAT cause on later invocations rather than flattening to a plain
`mcp-only` row, which would read as a row where nothing was ever imported. Either way the accounting receipt that
replaces the import's carries `supersededImport` (the origin it claimed, its declared source, the folders its copied
evidence is in), so the run still says an import was made and why it stopped counting. Its copied files stay on disk, backing
no receipt, and do not travel in bundles. Once the prerequisite is settled the entry is `mcp-only` again and the
evidence is imported again; a re-import is copied to the first free `RUN/imports/<checkId>__<armId>~<n>/`, leaving
the older folder untouched. A destination is refused while its entry is still credited.

The importer proves that the submitted files match the frozen request and remain unchanged. It cannot
cryptographically prove that a caller truthfully transcribed an MCP invocation; the capture wrapper must write
the tool's actual request, response and measured configuration rather than caller-invented context.


## 2. Live receipts (`live-import`)

A `live-import` row completes only through `import-results`, which hands it to its live importer (`_geometry-harness/runcontrol/LiveImport.cs` in this version). The
import document (schema 1) carries `planId`, `checkId`, `armId`, `capturedUtc`, `tool`, `state`, `configuration` and
`result {detector, findings, coverage}`, and `outputs` with roles `flip-plan` (exactly one), `launch-receipt`, `events`
and `crash-log`, each hash-checked. The importer refuses unless every launch receipt names this plan, check and arm
(written by the driver before the launch) and started after the plan was frozen; ran the supplied flip plan; carries
the import's configuration fingerprint; and has its events among the outputs. It **re-classifies every launch from its
events** (`switchflip_outcome.classify`) and refuses a stored classification that differs, then re-reads the arm per
condition (`arm_reading`): an unsettled condition is refused, and the import's stated detector must equal the re-read
one. The configuration is checked by the same identity rules as an emit (§4.2) against the arm's frozen request, and
the prerequisite gate of §4.8 applies. On acceptance the evidence is copied under `RUN/imports/<key>/`, the receipt's
adapter is `{kind: live-import}`, its `imported.origin` is `fresh` with `bindingRecordedAtLaunch: true`, and finalize's
`unassessed.runtimeObservations` names the checks with accepted live receipts instead of saying none was supplied.

**Not applicable from the arm's own census (`runtime.state-switch-flip`).** At `run`/`resume`, once the arm's
`state.switch-census` is accepted, the controller reads its census output against the plan's scope with
the switch-scope rule (`_geometry-harness/runcontrol/SwitchFlipScope.cs` in this version; `integration/housecarl/switchflip_scope.py` asks it). The census covers the whole order, so the check is about the switches that a
script drives (`trigger.status` `Triggered` or `BoundUnread`) and that touch a target: by plugin (owner, or a member's
placer or attacher, is a target) or by cell (the switch's or a member's `cellKey` is in a target's
`footprint.placed.cells`). When none does, and nothing leaves that unknown (no trigger map, unreadable plugins, an
incomplete scope, no targets, or a driven switch whose target cells are not listed), the row is accounted
`not-applicable` with the counts; the receipt's `switchScope` records the census path and sha256 it read. Finalize
counts it as answered. Otherwise the row stays `live-import`, its sentence naming what keeps it open. `import-results`
refuses a live import for an arm the same rule reads as not applicable: an import never overrides that answer.


## 3. Record rows (`record-rows`, `order-variant` enrichment)

- `record-rows`: for every credited import of a `records` check without a `rows` output, the rows `recordrows.py`
  derives from that import's own request and response transcripts: one `LOST-EDIT` row per record, top-level field
  and losing plugin, where the plugin changed the value it overrides and the setup's winning value differs from its
  own. Fields come from the generated schema reference; file, group and struct bookkeeping, generated bookkeeping (a
  worldspace's `LargeReferences`) and runtime-merged fields are named in
  SkyWright's `data/record-field-scope.json` by schema names; a worldspace's
  persistent cell (named by any capture's `TopCell`) is a container. A field a read did not cover (a field
  selection, a read cut off at the server's line limit, a different depth), a field the schema does not know and a
  failed read are unjudged with the reason, counted on the coverage line and listed in `Unjudged` (record, field,
  reason, and the plugins whose versions were compared). Each file cites its request and response by bundle path and sha256,
  and each row the reads it came from (`Reads: [{batch, id, plugin}]`).
  A plugin's master list, which decides whether a value is its own change, is the one the bundle's probe emits
  recorded for those bytes (`Configuration.Plugins[].Masters`); the file's header is read only for a plugin no emit
  records, and only at the recorded bytes. A 2.0 capture (`housecarl_records` tree answers and by-name reads) and a
  1.9.0 JSON record read are read as well as the pre-2.0 text reads.
- `order-variant` (roadmap 4.24 phase 3): for a pair whose order is free, every credited record import whose setup
  loads both evaluated plugins, derived again by `recordrows.py` from the same transcripts with the two plugins'
  places swapped (the engine's rule for which version wins). The pair's order is forced by a master relation or by
  exactly one master file (ESM flag, or `.esm`/`.esl`; the ESL flag alone does not make an `.esp` one), read from
  the emits' recorded `MasterFlag`/`Masters`; then, or when those facts are missing, or when the swap would load a
  plugin before a master, it is refused in one sentence and nothing is written. Each file carries the
  `configuration` it used, `order {pair, reversedFrom, rule}` and `readForm` (`text`, or `tree` when the rows come
  from tree answers and cite the transcripts rather than single reads).

## 4. Supplied media

**Media — media item schema 2.** `media.json` is a list (or `{items: [...]}`) of

```
{ path, mediaKind: "offline-render" | "native-screenshot" | "game-video" | "historical-illustration",
  subject: { checkId, armId, formKey?, description }, state, configurationFingerprint,
  fingerprintSource: "emitted" | "asserted",
  provenance: { tool, toolVersion?, argv?, capturedUtc, runId?, note? },
  sourceInterval?: { startSeconds, endSeconds, timebase?, sourcePath, sourceSha256 },
  original?: { path, sha256 }, transformation?: "<text>" }
```

An accepted item is copied to `media/<sha256[:12]>-<basename>` (two different files with one basename never
collide) as evidence with strength `supplied-media`; its manifest row carries **every field of the item** plus
`id`, `sha256` and `bytes`, with `path` the bundle-relative copy. `fingerprintSource: "asserted"` is accepted —
the contract accepts a caller-stated fingerprint — and a report renders it as "identity asserted by the supplier
from the arm's accepted receipt, not computed by the capturing tool"; `"emitted"` means the capturing tool wrote
the fingerprint itself. A rejected item goes to `unassessedMedia` **verbatim** with `reason` — exactly one of
the closed list below — and a plain `detail` sentence, and **is not evidence**: missing or mismatched media is
unassessed, not an illustration of a verified result. The checks run in this order and the first failure names
the reason:

1. `file-missing` — no `path`, or the file is not on disk;
2. `kind-missing` — `mediaKind` absent or not in the vocabulary;
3. `subject-missing` — `subject` lacks `checkId`, `armId` or `description`;
4. `state-missing`; 5. `fingerprint-missing`; 6. `fingerprint-source-missing` — absent or not in the vocabulary;
7. `provenance-missing` — `provenance` absent, not an object, or lacking `tool` or `capturedUtc`;
8. `no-accepted-receipt-on-arm` — no accepted receipt measured `subject.armId`'s own configuration;
9. `fingerprint-mismatch` — the fingerprint equals no accepted receipt's configuration fingerprint on that arm.
   **The comparison set is the arm's own configuration**: its accepted receipts whose `part` is null or `arm`.
   A `reverse` or `diff` part was measured on the other order and its receipt carries that order's
   fingerprint, so a picture of the baseline order declared for a candidate arm with the baseline fingerprint is
   refused here (the detail says which order the fingerprint belongs to) — it illustrates the baseline arm.
   **A picture taken in a world state** (`state` names a switch, not `baseline`) is compared with the arm's own
   accepted configurations with only the state line set to that state (and the population line also read as the
   renderer reads it, a scoped population's base), and only when that state is one this run measured — a switch a
   state row's `Kind` names in an accepted `state.conditional-geometry` output. State checks measure every flip in
   one run, so no receipt records the configuration in one state; any other state stays a mismatch;
10. `source-missing` — a clip's `sourceInterval.sourcePath` is absent, not on disk, names no `sourceSha256`, or
    hashes differently;
11. `original-missing` — `original.path` is absent, not on disk, names no `sha256`, or hashes differently.

A schema-1 item (no `mediaKind`, no `fingerprintSource`) is therefore unassessed by the first missing field's name
(`kind-missing`), never silently accepted. A media file with a plugin or asset suffix (never copied by policy)
refuses the whole `--media` document by name rather than dropping the item.

