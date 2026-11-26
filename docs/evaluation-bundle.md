# The evaluation bundle — plan, receipts, disposition, bundle

**Class: LIVING.** The contract between SkyWright's built-in controller `runcontrol`
(`_geometry-harness/runcontrol/`: `plan`, `run`, `resume`, `finalize`, `bundle`, `verify-bundle`, `population`, `status`),
the check registry (`data/check-registry.json`, copied to the build's `Data/`; what the checklist is), and whatever
reads the result later: the app, the integration module's report and reel, a model benchmark, a person reviewing a
run. `_geometry-harness/evalrun.py` keeps one command of the controller's Python original, `enrich`. Paths are relative
to a SkyWright checkout. This document came from houseCARL's HOUSECARL_EVALUATION_BUNDLE standard; houseCARL gave
it up when the code it governs became SkyWright's.

**What only an AI-side evaluation adds** (imported record reads, the live switch-flip check, the record rows derived
from those reads, supplied media) is the houseCARL integration module's:
[`integration/housecarl/AI-EVALUATION.md`](../integration/housecarl/AI-EVALUATION.md). The places this document used to
describe them now point there.

Related contracts: houseCARL's run-event contract (`standards/HOUSECARL_RUN_EVENTS.md` in the pinned houseCARL:
lifecycle, timing, the run store) and its Nexus catalogue (`standards/HOUSECARL_NEXUS_CATALOG.md`: discovery
evidence), and, in this checkout, `_geometry-harness/_shared/ConflictReport.cs` (findings, coverage, row identity),
`_geometry-harness/_shared/Configuration.cs` (`ConfigurationIdentity`, `CandidateFile`), `conflictdiff` (the credit
rule and the verdict line), `_geometry-harness/ordersweep.py` (the candidate sweep and its classification). This
document adds links between those; it does not define a second finding, configuration or elapsed time.

---

## 1. What the controller is for, and what it refuses to be

The tools could already measure a load order. What still lived in the coordinating model was remembering which
checks an evaluation needs, collecting the outputs, and telling a successful narrow run from an incomplete
evaluation. `evalrun` makes that bookkeeping deterministic.

It **detects nothing itself**. Every conclusion it reports comes from a probe's own emit, from `conflictdiff`'s
own verdict line, or from `ordersweep`'s own classification, retained verbatim and scoped to the probes that ran.
It does not sum unlike coverage units, does not net a repair against a regression, and never promotes a bounded
verdict into a whole-evaluation pass. It launches no game, opens no network connection, and writes no MO2 profile.

**Completion is derived, never granted.** `finalize` reads the ledger and the validated receipts. There is no
input by which a caller — or a model writing the call — can assert that an evaluation is complete.

---

## 2. The registry

The registry is SkyWright's data: `data/check-registry.json` (copied to the build's `Data/`), read and validated by
`runcontrol` (`_geometry-harness/runcontrol/Registry.cs`, the port of the Python original's `evalregistry`) and by
nothing else. It carries an integer `registryVersion` and its
sha256 is recorded in every plan; a plan whose registry hash no longer matches cannot be finalized (§7).

```
{ "schemaVersion": 1, "registryVersion": 1,
  "families": [ { "id": "loadability", "title": "...", "packetRow": "..." }, ... ],
  "checks": [ { "id": "geometry.navmesh-ownership", "family": "geometry", "title": "...", "required": true,
                "applicability": { "trigger": <expr>, "note": "...", "dependsOnExtents": ["alias-fill", ...] },
                "prerequisites": ["loadability.masters-and-order"],
                "inputs": { "arms": "all" | "baseline", "states": "baseline" | "selected" | "all", "statesFlag": "--state" },
                "adapter": { "kind": <kind>, "exe": "...", "args": [...], "reverse": true|false, "commonFlags": true|false,
                             "output": "conflict-file" | "json" | "text" | null, "result": {...}, "population": "...", "state": "...",
                             "statesFlag": "--state", "statesFileFlag": "--states-file" },
                "evidence": { "strength": "record-fact" | "calibrated-inference" | "engine-observation",
                              "claim": "<CLAIMS.md row>" | null, "limits": "..." },
                "manualDecisionAllowed": false, "unsupportedReason": "..." } ] }
```

**Adapter kinds** (closed): `scopeprobe`, `ordersweep-probe`, `probe-emit`, `probe-json`, `probe-target`, `derived`,
`manual`, `unsupported`. The AI-side kinds (`media`, `mcp-only`, `live-import`, `adviceprobe`, `patchfind`) are the
integration module's: [`AI-EVALUATION.md`](../integration/housecarl/AI-EVALUATION.md). `patchprobe` is retired and
appears only in older bundles (§8). A kind that runs a child carries `commonFlags` (whether
the probe accepts `--instance/--profile/--game`; every child also receives them through `HOUSECARL_MO2_INSTANCE`,
`HOUSECARL_MO2_PROFILE`, `HOUSECARL_GAME_PATH`) and, for a `json`/`text` output, a `result` (§4.5). A kind that runs no child
carries `output: null`. A probe whose emit judges a population other than the arm's declares it (`adapter.population`,
`adapter.state`): driftprobe, seamprobe and waterprobe today.
An `ordersweep-probe` row that can be scoped to the plan's population declares `populationFlag: "--population"`
(overlapprobe and contactprobe today); the plan then sets that check's `adapter.population` to the scoped identity
(`plan.population.identity`), so its emits are validated against it. A `probe-emit` row may declare it too (the
empty-space and conditional-geometry checks): the plan names it in `population.scopedChecks`, sets its
`adapter.population` the same way, and the controller passes `--population RUN/population.json` to that probe itself,
blocking the check (`blocked(population-changed)`) when the file no longer has the plan's hash.

**Trigger vocabulary** (closed; each reads one scope field and nothing else): `{"always": true}` reads nothing;
`{"anyType": [...]}` → `recordTypes`; `{"placed": true}` → `placed.total`; `{"exterior": true}` → `placed.exterior`;
`{"land": true}` → `placed.land`; `{"navmesh": true}` → `placed.navm`; `{"overlays": true}` → `configuration.overlays`;
`{"nexusId": true}` → `nexusId`; `{"masters": true}` → `masters`; `{"anyOf": [...]}`, `{"allOf": [...]}`.

**Applicability** is `yes` when the trigger is true on a complete scope; `no`, with the counters that made it false quoted as
evidence, when it is false on a complete scope; `unknown` when the scope is missing, when its `complete` is false, or when an
extent named in `dependsOnExtents` appears in the scope's `unmeasured` list. Unknown never becomes no: the finalizer keeps an
evaluation incomplete on it. A trigger names record kinds because a check is ABOUT that kind of content; what a target contains
comes from `scopeprobe`, by Mutagen's own enumeration, never from a list in a tool.

**Validation refuses, by name:** a duplicate id; an unknown family or kind; a required check with no trigger; a prerequisite
naming an unknown id or forming a cycle; a check on every arm resting on a baseline-only prerequisite; a `manual`
or `unsupported` check without `unsupportedReason`; a `json`/`text` adapter without `result`, a `result` without a named group
or with a regex on json; a child-running kind without `commonFlags`; a no-child kind carrying one; a null `claim` without
`limits` saying why.




What the controller needs from it, and nothing more:

| field | how the controller uses it |
|---|---|
| `id` | the receipt key and the ledger key; stable, never renamed |
| `family` | grouping in the disposition and STATUS.md; every packet family must carry at least one check |
| `required` | whether a non-answer blocks `evaluation.complete` |
| `applicability` | `yes` / `no` / `unknown` per check, derived by the registry rule (`Registry.Applicability`) from a measured scope |
| `prerequisites` | execution order, and `blocked(prerequisite:<id>)` when one has no accepted receipt |
| `inputs.arms` | `all` (every arm) or `baseline` |
| `inputs.states` | `all` puts the check's unexplored states into `states.notExplored` |
| `adapter.kind` | which adapter runs it (§4) |
| `adapter.exe` | the program, its hashes in `plan.tools`, and the `Probe` name a conflict-file emit must carry |
| `adapter.args` | the probe-specific flags, as DATA, with the placeholders of §4 |
| `adapter.output` | `conflict-file` / `json` / `text` — which validation applies |
| `adapter.result` | required for a `json`/`text` output (except `scopeprobe`): `{kind, findings, judged, unjudgeable, unit}` — how the controller reads that output's numbers (§4.6) |
| `evidence.strength` | copied into the receipt; never folded into execution status |
| `evidence.regeneration` | optional; printed under STATUS.md's regeneration needs |
| `manualDecisionAllowed` | whether a `decisions/` file may satisfy this check |
| `unsupportedReason` | required for `manual` / `unsupported`; printed wherever the check is accounted |

---

## 3. The frozen plan — `RUN/plan.json` + `RUN/plan.sha256`

```
runcontrol plan --registry R [--scope scope.json] --candidates DIR [--alt FILE]... [--supplied SPEC]...
             [--depth standard|deep | --states baseline[,NAME...]]
             [--select TOKEN,...] [--exclude CHECK=reason]... [--opt-in CHECK]...
             [--budget-minutes N] [--max-candidates N] --out RUN
             [--instance D --profile N --game D --harness D --probe-args S --probe-cmd NAME=CMD
              --diff-tolerance T --no-reverse]
```

| field | meaning |
|---|---|
| `planId` | 12 hex of the sha256 over the canonical plan JSON with `planId` removed |
| `registry` | path, `registryVersion`, and the registry FILE's sha256 |
| `tools` | per registry exe, diagnostic: the apphost `exeSha256`, the managed `dllSha256`, the `coreDllSha256`, and `present` |
| `toolIdentity` | **the identity of every program this plan can launch, frozen with it** (§3.1): `{contract: 1, rule, programs: {"probe:<exe>" \| "helper:conflictdiff" \| "controller": {kind, state, digest, locator, files \| inputs}}}`. `run` and `resume` refuse a plan without it (`plan-identity-outdated`) |
| `runner` | in a plan made by the Python controller, the sha256 of its scripts (diagnostic; the gating record is `toolIdentity.programs.controller`) |
| `schemaVersion` | **2**. A plan that declares anything else is refused by name (`plan-schema-unsupported`) |
| `requestProviders` | **`arm-view`**: every `arms[].requested.providers` is that arm's own view (below). A plan without it is refused by name where it would bind a new receipt (`plan-request-outdated`) |
| `targets`, `scope` | the scope summary and the scope file's sha256; `complete`, `unmeasured`, `walkFailures` |
| `configuration` | the measured scope's configuration: fingerprint, source, population, plugins (name/path/sha256), **providers** (name/kind), overlays |
| `arms[]` | `armId`, `kind` ∈ baseline / candidate / supplied-combination, `candidateFile`, `candidateSha256`, `plugins`, `mods`, `changes`, `provenance`, `expected.{population,states}`, `heldBack`, `loadedInGame`, `requested` |
| `arms[].requested` | **the frozen request this arm's receipts are validated against**: `{plugins: [{name, sha256\|null, path\|null, hashSource: "scope" \| "instance:<layer>" \| "unstated", providedBy?: [{layer, path, sha256}], unstatedReason?}], overlays: [{kind, path, sha256}], providers: [{name, kind}], population, states}`. Plugin names in the candidate file's order; a name the measured scope lists keeps that scope's path and hash (`hashSource: "scope"`). Overlays are the scope's. **Providers are the ones THIS arm's configuration reads**, in `HarnessOrderCli.Resolve`'s order: a candidate that declares `Mods` gets the scope's `overwrite` rows, one `{name, kind: "mod"}` per entry of its own `Mods` in order, then the scope's remaining non-`mod` rows (`Data`); an **empty** `Mods` list is an empty view and contributes no `mod` row; a candidate with no `Mods` key reads the profile's view and gets the scope's providers as recorded. The non-`mod` rows come from the scope because the overwrite layer and the game's Data folder belong to the instance and the game path, not to the candidate |
| `arms[].requested.plugins[]`, resolved | A name the scope does **not** list — a supplied plugin, or one the profile does not load — is resolved at plan time when `--instance` was given, by the probes' own precedence (`HarnessOrderCli.BuildLayerMap`, reading the profile through `Mo2LoadOrder.ParseModlist`): `<instance>/overwrite`; the view's mod folders in priority order (the candidate's own `Mods` list if it has one, else the scope's providers of kind `mod` as recorded), each `<instance>/mods/<name>`; `<game>/Data`; the profile's enabled mods not in the view; its disabled mods. First sighting wins, names compare case-insensitively, only `.esp/.esm/.esl` at a folder's top level count, an absent or unreadable folder is skipped. The row then carries that copy's `path` and `sha256` with `hashSource: "instance:<layer>"` — `<layer>` being `overwrite`, the mod folder's own name, or `Data` — and `providedBy` lists **every** layer providing the name, in precedence order, each copy hashed, so a reader sees whether an ambiguity exists and whether it matters. A name resolved from a folder outside the view is in `supplied.outOfView`; one resolved from the view is neither out-of-view nor payload-missing |
| `arms[].requested.plugins[]`, unstated | Nothing is guessed: the plan states a hash only when it could walk every layer the probes walk. The row stays `{sha256: null, path: null, hashSource: "unstated"}` with an `unstatedReason` when `--instance` was not given; when `--game` was not given, since the probes always walk the game's `Data` and it outranks every folder outside the view, so no partial walk can name the copy they would read; when no layer provides the name (it is `supplied.payloadMissing` as before); when `--profile` was not given and the name is provided only by folders under `mods/` that the view, `overwrite` and `Data` do not cover, since those folders cannot even be listed; or when `modlist.txt` would not read and the name is provided only by such folders — the probes would refuse that order rather than rank those folders, so `providedBy` lists the copies with `layer: "(not in modlist.txt)"` and the plan states the ambiguity instead of picking one |
| `arms[].loadedInGame` | **always null**, with `loadedInGameNote`: "offline hypothetical configuration; not verified loaded in game" |
| `arms[].payload` | `{espOnly, staged, note}`. A supplied arm's note says its meshes, scripts and installer choices were NOT staged |
| `arms[].supplied.payloadMissing` | declared files absent from the configuration; dependent checks become `blocked(payload-missing)` |
| `states` | `selected` (default `["baseline"]`), `depth` (`standard`, `deep` or `explicit` when `--states` named them), `notExplored` (one row per check whose `inputs.states` is `all`) and, for `deep`, `fromCensus` {`generator`, `sha256`, `checks`}: each listed check's states are generated at run time by `stategen.py` from that arm's own accepted `state.switch-census` output and written to `outputs/<check>/<arm>.states.json` (the list, the not-explored rows with reasons, the census and generator hashes) and `<arm>.states.txt` (handed to the probe as `statesFileFlag`); the receipt's `states` block carries both files' hashes and the count. An arm whose census yields no state is accounted `not-applicable` with the reason. The disposition's `unassessed.statesNotExplored` adds every arm's generated rows, each with its `checkId` and `armId` |
| `checks[]` | `checkId`, `family`, `required`, `applicability`, `applicabilityEvidence`, `prerequisites`, `inputs`, `evidence`, `adapter`, `arms: [{armId, status}]`, `status` |
| `budgets` | `minutes`, `maxCandidates`, `candidatesHeldBack` (this plan's held-back arms AND `candidates.json`'s own `Budget`), `statesNotExplored` |
| `explicitDecisions` | resolved `--select`, the raw tokens, `--exclude` with reasons, and `touched` — every check a narrowing flag touched, whatever status won; `optIn` with `optInSource` (`flag` for each). `plan` reads no evaluation settings file: `--settings` is refused, and the measuring choices come by flag |
| `options` | instance / profile / game / harness / probeArgs / probeCmd / diffTolerance / noReverse / population; hashed into every receipt's `optionsSha256` |
| `population` | the plan's population (P25): `{scoped: true, file: "population.json", sha256, identity, plugins, mods, targets, scopedProbes, scopedChecks, wholeOrderChecks: {checkId: reason}, note, adoptedFrom?}` or `{scoped: false, reason, wholeOrderChecks}`. The population is the plan's targets plus every plugin and mod folder the arms differ by (presence, or relative order); `RUN/population.json` names each plugin by path and sha256 and each mod folder by path, and is frozen with the plan. The ordersweep adapter passes it to the `scopedProbes` only (`--population`, part of their request key by its hash), and the controller passes it to the `probe-emit` checks in `scopedChecks` (`scopedChecks` lists every scoped check by id); every other check runs on the whole order and is listed with its reason. The state check stays on the whole order when a delta plugin carries RACE records (every actor band reads them). A scoped emit's `Population` is `<population>+population:<first 12 of sha256>`. The plan is unscoped, with the reason, whenever a file cannot be stated (a plugin without one agreed path and hash, a mod folder without `--instance`, a delta mod folder shipping a `*_SWAP.ini`); `--population-from FILE` adopts another plan's file byte for byte when it covers this plan's own population, so a sibling evaluation is scoped identically. A plan without this key runs exactly as before, and a plan without `scopedChecks` runs its `probe-emit` checks on the whole order |

**`armId`** is short by construction (`baseline`, `candidate-NN`, `supplied-NN-<slug>`) so a receipt filename fits
inside a Windows path; the full candidate file name stays in the arm record.

**Planned status, first match wins.** `applicability: no` → `not-applicable`; an opt-in check (`optIn: true`) the
caller did not name with `--opt-in` → `not-opted-in`; adapter kind
`unsupported` / `manual` → that kind (the runner could not have run it, selected or not);
`unsupported-for-arm` per arm; `blocked-payload-missing`; then `--exclude` → `excluded`; then not in `--select` →
`skipped-user` (optional checks too — `required` decides whether a skip *blocks completion*, not whether it is
*recorded*); then a `--max-candidates` held-back arm → `skipped-budget`; else `planned`.

**`--select` narrows what RUNS, never what is PLANNED.** It takes check ids; an exe name is accepted as a
convenience and expands to every check whose adapter runs it, so `--select navownprobe,overlapprobe,contactprobe`
is the packet's "caller asked for only the three sweep probes" case. The plan records the resolved ids.

**The plan is immutable.** `run` / `resume` / `finalize` / `bundle` refuse a plan whose file no longer hashes to
`plan.sha256` (`plan-changed`). Planning writes no MO2 profile. Its only child is overlapprobe's scene preflight (when `--instance` is given), whose verdict and diagnostic build fields are recorded on each arm (`scenePreflight`).

**A schema-1 plan is refused by name, never read.** The same four verbs refuse a plan whose `schemaVersion` is not
2 as **`plan-schema-unsupported`**, with the next action "plan again from the same registry, scope and candidates".
A plan written before this schema carries no `arms[].requested`, so nothing in it could bind a receipt to the
configuration the arm asked for — and a reader that finds nothing to check must not report that as nothing wrong.

**A plan frozen before per-arm providers is refused where it would bind a new receipt.** Such a plan carries no
`requestProviders` and copied the scope's providers onto every arm, so an arm whose `Mods` differ from the scope's
view has a request no honest child of that arm can match — an emit of what it really read is refused
`identity-mismatch`, and one stating the scope's view would be accepted. `run` and `resume`
refuse it as **`plan-request-outdated`** (exit 2), with the next action "plan again from the same registry, scope
and candidates". The plan is never re-read under the new meaning: `arms[].mods` does not keep an empty `Mods`
apart from an absent one. `finalize`, `bundle`, `status` and `verify-bundle` bind nothing new and read such a plan
as before, so a bundle already built from one still verifies.

**The registry and the scope are premises of the frozen plan, not inputs a verb may re-read.** `run`, `resume` and
`finalize` refuse **`registry-missing`**, **`registry-changed`**, **`scope-missing`** and **`scope-changed`** by
name, each with the next action "re-run scopeprobe and plan again"; a plan made without `--scope` has no scope
premise. A checklist or a measured scope that changed after the plan froze changes which checks apply, and that is
a new plan rather than reuse of the old applicability. `run` and `resume` refuse these as a refused *input* (exit
2, no work done); `finalize` refuses them as a refused *result* (exit 3, with a refusal disposition written) —
the cause name and the next action are identical on every verb; only the exit class follows the verb. Bundle
verification reads no live path at all (§8).

### 3.1 Program identity — `plan.toolIdentity`

**An evaluation never mixes tool builds silently, and never labels a result with code that did not run.**
`toolidentity.py` holds the one rule; evalrun and ordersweep both use it.

| kind | what it is | identity |
|---|---|---|
| `package` | a probe's or conflictdiff's folder in a build (`Probes/<name>/`, where the build's `skywright-layout.json` puts it) | every top-level file except `*.pdb`, plus every file under `runtimes/`, by lower-cased relative path and sha256; digest `folder:<sha256>` over the sorted `path\0sha256\n` lines (ordersweep's cache key is the same string). Other subfolders are run-time output (`shots/`) and are not identity. **Every other top-level non-PDB file counts**, even a stray text file: the boundary is conservative, not a list of build outputs |
| `probe-cmd` | a `--probe-cmd NAME=TEXT` override | the command text and the bytes of every file its whitespace-split tokens name (resolved as the launch resolves them); a bare first token (`python`) is recorded as a locator only. Command text alone is never identity; a command that names no file is `unverifiable` and refused where it would run |
| `scripts` | the controller of a plan made by the Python controller (read, never written now) | the scripts whose code decided a run's, resume's or import's result. Today's controller is `runcontrol`, identified as a `package`: the folder it runs from (`Tools/runcontrol/` of a build) |

`programs` holds only what can launch under this plan: the controller; the probe of every child-launching check
(`scopeprobe`, `ordersweep-probe`, `probe-emit`, `probe-json`, `probe-target`) that has
a planned arm; conflictdiff when a swept check plans a non-baseline arm. A tool whose checks are all excluded,
skipped, not applicable, not opted in or never executable is not recorded and never gated. A package with no folder
is recorded `state: absent`; still absent at run time its checks are `blocked(tool-missing)` as before and nothing
can be accepted from it; present at run time it is a change. A file that cannot be read is refused by name
(`tool-unreadable`), at `plan` and at `run`, never read as a value.

**Outside identity, stated:** the shared .NET runtime (`runtimeconfig.json` declares a framework requirement, not its
bytes), the Python interpreter and its standard library, and modules a `--probe-cmd` program imports. Paths are
locators: the same package at another root has the same identity, and moving an installation is not a supported plan
rewrite — a program whose folder is gone is `tool-missing`.

**Roles.** Every accepted local receipt binds the identity it was accepted under in `consumed.programs`, per role:
`emitter` (the probe a child-launching check ran), `comparator` (conflictdiff, a swept `diff` part only),
`controller` (always; the only role of a derived check and of an import). Each row is `{program, kind, digest,
checked}`, the digest **measured** (`checked: ["launch", "acceptance"]` for a child, `["preflight"]` for a derived
check or an import), never copied from the plan.

---

## 4. Adapters and receipts

### 4.1 Adapter kinds

| kind | what runs | arms |
|---|---|---|
| `scopeprobe` | the footprint probe; its output's `complete` is the detector (true → no-finding, false → inconclusive), except for a legal target omission (below) | as `inputs.arms` |
| `ordersweep-probe` | **not run directly**: every such check goes through ONE `ordersweep.py` child (§4.4) | all |
| `probe-emit` | an emitting probe with `--order` and `--emit`, no `--base`: per arm, no reverse arm | as `inputs.arms` |
| `probe-json` | a probe with `--json` | as `inputs.arms` |
| `probe-target` | takes the target positionally, no `--order`: baseline only; other arms `unsupported-for-arm` | baseline |
| `derived` | computed from other receipts; runs no child (§4.5) | all |
| `manual` | never run: execution `manual`; a `decisions/` file is read at finalize | as `inputs.arms` |
| `unsupported` | never run; **accounted**, with `unsupportedReason` | as `inputs.arms` |

The AI-side kinds (`adviceprobe`, `patchfind`, `media`, `mcp-only`, `live-import`) are run or accounted by the
integration module: [`AI-EVALUATION.md`](../integration/housecarl/AI-EVALUATION.md).

The probe-specific FLAGS live in `adapter.args` as registry data, with placeholders the controller fills:
`{order}`, `{emit}`, `{json}`, `{out}`, `{run}`, `{arm}`, `{target}`, `{switchMap}`. A
placeholder is substituted in the SHAPE the args give it (`--target {target}` repeats with its flag; a bare
`{target}` repeats positionally), and a placeholder whose source is absent blocks the check by name rather than
reaching the probe literally. `{nexusId}` and `{pair}` are filled only for the integration module's own checks
([`AI-EVALUATION.md`](../integration/housecarl/AI-EVALUATION.md)). A new probe is a registry row, not a new
verb in the controller.

**`scopeprobe` on an add/remove arm — a legal target omission.** Every arm's scopeprobe child is told every plan
target. An arm whose frozen request leaves a target out (a dependency-only arm, or `+A` without `B`) makes the
probe record that target as a walk failure and exit 1, although the order resolved. The controller accepts that
exit as this check's loadability answer **only** when all of these hold: the output's resolved configuration is
exactly the arm's frozen request (names, in order); every walk failure is the probe's own absent-target sentence
(`'<name>' is not in the resolved configuration (`) for a plan target the request does not load, one per such
target; and every plan target the request does load was measured. The receipt is then `completed`, detector
`no-finding`, with one coverage line `{Population: "target footprints", Judged: <targets measured>,
Unjudgeable: {"not-loaded-by-this-arm": <targets omitted>}}` whose note names the omitted targets as neither
requested nor measured on this arm; finalize carries that line into `unassessed.unjudgeablePopulations`. A
dependency-only arm is therefore a loadability answer with a footprint of zero measured targets, never a measured
footprint. Anything else stays a failure: a refused order (missing master, invalid order) is exit 2 with no output
and is `rejected(illegal-configuration)` with the child's refusal line in its detail; a target the arm requests but
the configuration lacks, any other walk failure, or a configuration that is not the request is
`rejected(exit-nonzero)` with the unexplained reason in its detail. An arm that loads every target behaves as
before.

`--probe-cmd NAME=<argv prefix>` (frozen into the plan) stands a different program in for a probe — the seam a
fixture uses to run a fake probe against the real controller. `ordersweep.py` carries the same flag.

### 4.2 The receipt — `RUN/receipts/<checkId>__<armId>[__<part>].json`

`part` ∈ `null` | `arm` | `reverse` | `diff`.

```
{ receiptId, planId, checkId, armId, part, adapter, argvShape, runId, parentRunId,
  startedUtc, endedUtc, exitCode,
  outputs: [{path (RUN-relative), sha256, bytes, mtimeUtc}], log,
  consumed: { candidateSha256, candidatePath,
              configuration: {fingerprint, source, plugins:[{name,path,sha256,hashSource?}], providers, overlays,
                              population, state, limits,
                              fingerprintNote?, identityLimits?, reuseGap?},
              configurationSource: "emit" | "scope" | "plan",
              baseEmitSha256, baseEmitPath, inputs?: [{part, path, sha256}], registrySha256,
              tool: {exe, exeSha256, dllSha256, coreDllSha256, present},   # measured when the result is accepted
              programs?: {emitter?|comparator?|controller: {program, kind, digest, checked}},   # §3.1
              optionsSha256, scopeSha256 },
  validation: { accepted, rejections: [{cause, detail}],
                identityLimits: [{plugin, recordedSha256, plannedPath?, emitPath?, detail}] },
  result: { execution, detector, findings, coverage, strength, verdict },
  sweep?: { folder, job, outcome, reusedFrom } }        # ordersweep parts: the launch folder and job it came from
```

`configurationSource` says where the identity came from, and the three values are three different strengths:

- **`emit`** — the CHILD recorded it, and it was validated against the arm's frozen request (§4.3). The strong case.
- **`scope`** — the BASELINE arm's block, which is the measured scope's configuration verbatim, fingerprint
  included. The baseline is the one arm for which the scope's configuration *is* this arm's.
- **`plan`** — a child that stated no identity (a `json` / `text` / `probe-target` adapter, or an accounting row
  that ran nothing) on a non-baseline arm. The block is THAT ARM's `requested` — the plan's plugin list in the
  arm's order with each row's stated hash and the `hashSource` that says where it came from, its overlays and
  providers, the plan's population and a null `state` — with `fingerprint: null` and `fingerprintNote: "the
  child stated no identity; reuse re-checks the plan's stated plugin hashes on disk"`. **The scope's baseline
  fingerprint is never copied onto another arm**: a thin adapter that cannot establish what it consumed states
  the limitation rather than borrowing the baseline's identity as proof of what a different order read.

`consumed.inputs` is the comparison a receipt *is*: the `diff` part carries two rows (`arm` and `reverse`, each with
its RUN-relative path and sha256 — the files the diff's job recorded reading) and leaves
`baseEmitPath`/`baseEmitSha256` null. The `reverse` row is absent when no reverse emit was read (`--no-reverse`, or
no reverse measured): a diff does not rest on a file it never read. A candidate
arm's `arm` part records the same check's BASELINE arm emit in `baseEmit*`, and its `reverse` part records the
candidate arm emit; neither carries `inputs`. Reuse follows all of them (§6).

`validation.identityLimits` names every request plugin whose hash the plan could not state, with the emit's own
hash recorded, and every plugin the emit read at a different path from the one the plan resolved though the bytes
are identical (`plannedPath` and `emitPath`) — the limit travels with the receipt instead of being smoothed over.

`argvShape` reduces absolute paths to `<RUN>/...` or `<abs>/<name>` so two machines' receipts are comparable
without pretending their paths were the same.

**An accounting receipt is rewritten only when what it states changes.** A row that launched nothing in an
invocation (not-applicable, skipped, unsupported, manual, blocked, stopped) is accounted on
every `run` and `resume`. When the receipt already at its path is this plan's and this (check, arm, part)'s and equals
the new one in every field but `receiptId`, `endedUtc` and `parentRunId`, the file is left byte-for-byte as it is. A
kept receipt's `receiptId`, `endedUtc` and `parentRunId` therefore name the invocation that FIRST wrote that account;
the journal line and the ledger entry's `runId` name the latest invocation that accounted it. Any other difference (a
status, cause, note, or a stopped child's `runId`, log or exit code) writes a new receipt with a new
id. An unchanged `run` or `resume` leaves `RUN/receipts/` byte-identical.

### 4.3 Rejection causes (closed) and execution statuses (closed)

A normal exit is not enough. Every cause below is decided from evidence on disk:

| cause | how it is detected |
|---|---|
| `exit-nonzero` | the child's real exit code is not 0 — except a `scopeprobe` exit 1 wholly explained by legal target omissions (§4.1); for any other scopeprobe exit 1 the detail names what was not explained |
| `refused` | `REFUSED`, `MISSING MASTER`, `CANNOT ENABLE` or `PROFILE MISMATCH` in the child's log |
| `illegal-configuration` | exit 2 **and** `REFUSED` **and** no output produced — the configuration itself was refused; the detail quotes the child's refusal line, so a missing master and an invalid order stay distinguishable on the receipt |
| `output-missing` | a declared output path does not exist |
| `output-stale` | the output's mtime precedes the child's `startedUtc`, **or** its sha256 equals the pre-run snapshot of a file that existed before the child started |
| `schema-unsupported` | the output is not a JSON object; for `conflict-file`, no `Probe` / `Conflicts` |
| `identity-missing` | a conflict file with no `Configuration`, an empty `Plugins` list, or a plugin with no name or no 64-hex sha256 — what it consumed cannot be stated. **An incomplete identity is never accepted, first receipt or not** |
| `identity-mismatch` | the emit does not describe the configuration the arm ASKED FOR: see below |
| `wrong-check` | the emit's `Probe` is not the adapter's exe |
| `duplicate-receipt` | a second receipt for one (check, arm, part) in one invocation, or a receipt file whose `receiptId` disagrees with the ledger |
| `foreign-run` | the receipt's `planId` is not this plan's |
| `population-mismatch` | the emit's `Population` is not the arm's, or its `State` is not one the plan selected |
| `coverage-missing` | a conflict file declaring no `Coverage`: what was judged is unknown |
| `extraction-failed` | the primary population was judged 0 while unjudgeable > 0, or every population was judged 0 — zero findings there is zero assessed subjects |
| `stale-inputs` | on re-validation, a consumed input is gone or no longer hashes to its recorded value, or cannot be verified at all (§6) |
| `mixed-snapshot` | two accepted receipts of one arm consumed different content for one plugin |
| `budget-exhausted` | the budget's deadline was reached before this check launched (`skipped-budget`), or while its child was still running (`stopped(deadline)`, §4.7 "The budget is a deadline") |
| `blocked-prerequisite` | a prerequisite has no accepted receipt |
| `completion-missing` | an ordersweep job has no record in the `jobs.jsonl` of THIS launch's folder, or a diff has no row in the `summary.json` that launch wrote (§4.4) |
| `tool-changed` | a program the result rests on (§3.1, by role) no longer had the plan's identity when the child returned: a deploy or an edit while it ran. The receipt is `rejected(tool-changed)` and the invocation stops (§6) |

**`identity-mismatch`, in full: the reference is the frozen REQUEST, not another receipt.** An emit is compared to
`arms[].requested` of its reference arm — the arm itself for a null or `arm` part, and the **baseline** arm for a
`reverse` part, because the reverse run is the probe on the baseline order with the candidate as `--base`. The
reverse part is validated against that reference; it is never exempted. Two outputs agreeing with each other, or a
matching `Configuration.Source`, is not agreement with the request. Each of these is `identity-mismatch`:

- **order and names** — the emit's plugin names in order must equal the request's, case-insensitively. The detail
  reads `plugin order differs from the request at position N: expected X, found Y` and then says whether the set
  differs (naming the missing and extra names) or whether they are the same names in a different order.
- **hashes** — every request plugin with a stated sha256 must be consumed at that sha256, a hash the scope
  measured (`hashSource: "scope"`) and one the plan resolved through the probes' layer precedence
  (`instance:<layer>`) alike; the mismatch detail names the plugin, the plan's hash and path and the emit's hash
  and path. Equal bytes at a different path are accepted and recorded under `validation.identityLimits`, not
  rejected: the binding is to content, and the reader is told the emit read an identical copy elsewhere. A plugin
  with `hashSource: "unstated"` is accepted with the emit's own hash recorded under `validation.identityLimits`:
  "the plan could not state a hash for X; the emit's hash H is recorded and is what reuse re-checks on disk."
- **overlays** — the emit's overlays (kind + sha256, as a multiset) must equal the request's.
- **fingerprint self-consistency** — the emit's `Fingerprint` must equal the canonical digest of its own plugins,
  providers, overlays, population and state by `ConfigurationIdentity.Compute`'s text (`_shared/Configuration.cs`
  — **one** fingerprint rule, spelled once in the controller). A mismatch reads "the fingerprint is not the digest
  of the stated configuration".
- **source** — `Configuration.Source` must name the reference arm's candidate. The one exception, for an
  AI-side import that declares it reuses an earlier capture, is the integration module's:
  [`AI-EVALUATION.md`](../integration/housecarl/AI-EVALUATION.md), "Importing an existing MCP result".
- **within one arm** — a fingerprint that differs from this arm's other accepted receipts declaring the same
  population and state. Keyed on (reference arm, population, state), so two supported probes declaring different
  populations over the same plugins carry different fingerprints and **both pass**: content is compared to the
  request, fingerprints only within one population and state.

Execution status (closed): `completed`, `rejected(<cause>)`, `failed`, `refused`, `skipped-user`,
`skipped-budget`, `stopped(deadline)`, `blocked(<what>)`, `unsupported`, `manual`, `not-applicable`,
`pending`, `running`, `interrupted`, `invalidated(<what>)`, `refused-reuse(<gap>)`, `reused`.

Detector result (closed): `finding`, `no-finding`, `catalogue`, `inconclusive`, `null` (undetermined).

**Execution status, detector result, evidence strength and coverage are four separate fields.** They are never
folded. A completed check with an empty `Conflicts` and a judged primary population is `no-finding`; empty
because nothing was judged is `rejected(extraction-failed)`. A diff verdict of `DIFFERENTIAL-UNJUDGED`, or rows
resting on an unmeasured predicate, is `inconclusive`.

### 4.4 The ordersweep composition

`evalrun` assembles `RUN/candidates/` (every PLANNED arm's candidate file, copied and hash-checked against the plan),
runs `ordersweep.py --candidates RUN/candidates --out RUN/sweep/inv-NNNN --probes <the plan's supported
ordersweep-probe checks> [--reuse-cache <earlier launch>/cache.json ...]` as **one** child at the position of the
first such check in prerequisite order, then splits what that launch recorded into receipts: the candidate emit
(`arm`), the reverse emit (`reverse`), the diff (`diff`). Each is validated as above. The reverse arm runs the probe
on the BASELINE order with the candidate as `--base`, so its own `Configuration` *is* the baseline's — which is why
it is validated against the **baseline arm's** frozen request rather than exempted from the identity check (§4.3).

Each of the three records the emits it was measured against, so reuse follows the comparison and not only the
output: the candidate `arm` receipt records this check's baseline arm emit in `baseEmitPath`/`baseEmitSha256`, the
`reverse` receipt records the candidate arm emit there, and the `diff` receipt carries the arm emit and (when one was
read) the reverse emit under `consumed.inputs` as `{part, path, sha256}` rows (§4.2).

**One folder per launch; only that launch's job records are credited.** Every ordersweep launch writes into a
folder of its own, `RUN/sweep/inv-NNNN` (the next number, created exclusively); no later launch writes there, and an
invocation that launches nothing creates none (a launch the Runner refuses before its child runs removes its empty
folder and its record). The folder's name is recorded on the ledger's invocation row
(`runIds[].sweepFolders`) and on each receipt validated from a job record (`sweep: {folder, job, outcome, reusedFrom}`;
an accounting-only receipt — stopped, blocked, skipped — carries none). Files an older layout left directly under `RUN/sweep` are never read
for credit or reuse. ordersweep appends one line per job to the folder's `jobs.jsonl` as soon as the job has a
result — `{job, kind: probe|diff, outcome: ran|reused|stopped, exitCode, stopped, emit, emitWritten, emitSha256,
reusedFrom, log, inputs}` — and the split reads THOSE records, never a file's presence at a conventional path, an
mtime, a log trailer or a cache-log offset:

- **No record for a job** → `rejected(completion-missing)`, whatever its log or emit holds (a job that did not finish
  has no record).
- Records are trusted to name files inside the RUN: evalrun passes ordersweep a fresh `--out` inside the RUN and only
  earlier launch caches inside the RUN, and cache entries resolve inside their own folder. A hand-edited record naming a
  path elsewhere is not detected by the split.
- The receipt's `exitCode` is **the job's own exit code** from its record, never ordersweep's, so a job that fails —
  on its first execution as on any other — keeps its real cause and code.
- A probe job that exited 0 **without writing** its emit is not a measurement (`emitWritten: false`): ordersweep builds
  no reverse or diff on it and its candidate reads UNRESOLVED; the part is `rejected(output-missing)`.
- **Credit rests only on accepted emits.** Every part of a candidate arm is credited only while the same check's
  BASELINE arm part is accepted (the baseline arm is settled first); a reverse and a diff only on the accepted arm part;
  a diff only on the accepted reverse its created rows were split by. A part is still validated first, so its own
  failure keeps its own cause; a part that would otherwise be accepted reads `blocked(baseline-not-accepted)`,
  `blocked(arm-not-accepted)` or `blocked(reverse-not-accepted)` (cause `blocked-prerequisite`).
- **A part whose job did not run names the job that stopped it.** When this launch's arm (or baseline) job ran and
  produced no emit, the reverse part reads `blocked(no-reverse-measurement)` (a candidate arm with no job of its own,
  `blocked(no-baseline-measurement)`) and the diff part `rejected(output-missing)`, each naming that job and its exit
  code — also when the arm part stays `reused` on an earlier measurement. With no reverse run on an arm that was not
  accepted, the reverse part is `blocked(no-reverse-measurement)` naming the arm part's status.
- **A stopped job record is accounted exactly as evalrun's own children are** (`Executor.account_stop`, with the
  record's `stop` details from ordersweep's Runner): a job the Runner could not contain reads `blocked(uncontained)`,
  a launch the deadline refused `skipped-budget`, a job stopped at the deadline `stopped(deadline)` — never a budget
  stop for a job that could not be contained.
- **A diff job that exited non-zero** (conflictdiff exits 3 when it refuses a comparison — different populations,
  states or configurations — and 2 on a usage or read error, before any verdict) is `rejected(exit-nonzero)` quoting
  conflictdiff's own `!` refusal line, with the job's exit code on the receipt.
- A diff with no row in the `summary.json` this launch wrote is `rejected(completion-missing)`: its verdict and
  created rows are read from that row.

**A part accepted earlier stays `reused`, untouched,** unless this launch produced different bytes for its job (then
the new result is validated and receipted): its receipt keeps pointing at its own launch folder's files, which no
later launch rewrites — including a diff log, although `conflictdiff` is never cached. Each part is decided on its
own, so a resume after a partial sweep (arm accepted, reverse or diff not) repairs the missing parts without
discarding the accepted one. The sweep is launched when an arm the prerequisites allow, and whose live candidate
file still hashes to the plan's value, has a planned part — the baseline's `arm`; a candidate's `arm`, `diff` and,
unless `--no-reverse`, `reverse` — that is not terminal-ok. An arm whose candidate file changed or is gone is accounted
`blocked(candidate-changed)` / `blocked(candidate-missing)` without a launch (plan again), so an unchanged `run` does
not relaunch for it.

An arm kept out of the sweep by its prerequisite (§4.6) has any earlier copy removed from `RUN/candidates`, is not
measured, and keeps `blocked(prerequisite:<id>)`; parts an earlier invocation accepted for it are blocked too, never
reused (§4.6). It is accounted **without** launching the sweep, so an unchanged `run` does not relaunch for it. An
arm the plan does not measure — held back by `--max-candidates` (`skipped-budget`), or `blocked-payload-missing` — is
never copied into `RUN/candidates`, never handed to ordersweep and never marked `running`, on a stopped sweep and a
finished one alike: its parts carry its plan status's cause, its required checks stay `missing`, and it is named
under `unassessed.candidatesHeldBack`.

**The cache.** ordersweep caches a probe's emit only when that launch wrote it, under a deadline only once the job's
tree is empty. An entry names the emit **relative to its cache file's folder** with the emit's sha256
(`cacheVersion: 2`), so a copied RUN resolves every entry inside the copy. A launch reads its own `--out` cache
first, then each `--reuse-cache` file in the order given (evalrun passes every earlier launch folder's
`cache.json`, newest first) and writes only its own. An entry is reused only when its path stays inside that cache's
folder, the file still hashes to the recorded sha256 and every plugin and overlay of its identity re-hashes; an older
entry (absolute path, no emit hash) is `INVALIDATED` and never reused. A reused emit is read in place, in the earlier
launch's folder.

`ordersweep`'s per-candidate `classification`, credit rule and verdicts are consumed **verbatim**, retained in
the arm's `orderEvaluation` block scoped to the probes named, and never promoted to a whole-evaluation pass.

**The classification word is one launch's, and only a launch that measured all the accepted evidence gives it.**
ordersweep classifies a candidate over every probe of ONE launch, while a resume keeps accepted diffs from earlier
launches. So the order evaluation (the derived receipt and the finalizer's per-arm block, through one function) takes
the word from the newest launch among the arm's accepted diff receipts only when that launch's own job records and
summary show it measured every one of those diffs again — the same arm and reverse emits by sha256, the same diff and
created rows — and names it in `classificationSource.launch`. Otherwise `classification` is null,
`classificationNote` says which accepted diffs no single launch measured, and every launch's word is kept under
`historicalClassifications`; the per-probe numbers and `regression` are unaffected. Receipts from before per-launch
folders give their word only when they all carry the same one.

### 4.5 Reading a `json` or `text` output — `adapter.result`

A conflict-file output needs no `result`: the detector reading is implicit (§4.3). Any other output is read
**only** where the registry says its numbers live, so nothing is inferred from a shape the controller was never
told about.

```
adapter.result = { kind: "detector" | "catalogue",
                   findings: <spec>, judged: <spec>, unjudgeable: <spec>, unit: "<what judged counts>" }
```

A `<spec>` is a dotted path into the JSON output (`summary.switches`; a list or object resolves to its length),
or — for a `text` output — `regex:<pattern>` with a named group `n`, applied to the CHILD'S LOG
(`regex:indexed (?P<n>[\d,]+) plugins`).

- a spec that does not resolve → **`schema-unsupported`**. It is never read as a zero.
- `judged` 0 with `unjudgeable` > 0 → **`extraction-failed`**, the same rule a conflict file's coverage gets.
- `kind: detector` → `finding` when findings > 0, else `no-finding`.
- `kind: catalogue` → detector **`catalogue`**: a completed measurement whose rows are EVIDENCE,
  not defects (a switch census, a dependency census). A `catalogue`
  **satisfies completion**, and the disposition lists it under `evaluation.completedCatalogues`
  `[{checkId, armId, count, unit, kind}]` — **never** under `findings` and never under `completedNullChecks`.
  The result kind `discovery` (patch discovery, the author's advice) is the integration module's:
  [`AI-EVALUATION.md`](../integration/housecarl/AI-EVALUATION.md).

The registry validator (`Registry.Validate`) refuses a `json`/`text` adapter with no `result`, except kind `scopeprobe`, whose
`complete` field D2 itself defines.

### 4.6 Derived checks

| `adapter.derives` | what it computes |
|---|---|
| `order-evaluation` | per arm, from that arm's ACCEPTED `diff` receipts — when any ordersweep check planned on the arm has a `diff` part that is not `completed`/`reused` (rejected, stopped, blocked, absent) the evaluation is `blocked(prerequisite:<that check>)` naming the unaccepted diffs, and the finalizer's per-arm summary reads accepted receipts only: the classification verbatim when one launch measured all of them (§4.4; else null with `classificationNote` and `historicalClassifications`), per-probe repaired / reclassified / created / newDefect / attributed / unmeasured, and `regression: true` when ANY probe's `created.newDefect > 0`. `not-applicable` on the baseline — the baseline is the reference, not a thing measured against itself |
| `candidate-qualification` | a plugin is `qualified` only when its content hash is in an evaluated arm; anything else is `unqualified`, with the sentence that a downloadable hit stays a lead until its payload is measured. Rows: each supplied arm's declared files (the hash its request recorded) and each `candidates.json` order file (`catalogued`). A plan none of whose arms supplies a patch file is `not-applicable`, with that reason. Qualifying the patches discovered on the machine is the integration module's ([`AI-EVALUATION.md`](../integration/housecarl/AI-EVALUATION.md)) |

A `derives` value the controller does not compute is `unsupported`, named — never silently skipped.

A derived check passes the same prerequisite gate as every other adapter: once the derivation is one the controller
computes (and, for `order-evaluation`, the arm is not the baseline, which stays `not-applicable`), a prerequisite
that is not settled on this arm or on the baseline makes it **`blocked(prerequisite:<id>)`** with cause
`blocked-prerequisite`, naming the first unsettled one. A prerequisite that was measured on THIS arm and failed
there (`rejected(...)` or `failed`) is decided by this arm alone — a `loadability.masters-and-order` refused on a
candidate arm blocks that arm's `order-evaluation` even though the baseline's is accepted. So is a prerequisite
that is itself `blocked(prerequisite:…)` on this arm, which carries the rule down a chain: a `state.switch-census`
blocked by the arm's refused order blocks that arm's `state.conditional-geometry`, rather than borrowing the
baseline's answer one step down. The
baseline still answers for a prerequisite the arm has no entry for, or one not attempted there for another
reason. The same rule decides which arms enter the ordersweep and whether any other adapter's child is launched
on an arm: a probe whose prerequisite decides against its arm is not launched there, and the blocked entry names
that arm's own status. It also governs reuse: an entry accepted in an earlier invocation (for example on the
baseline's answer, before the arm's own prerequisite was measured) is blocked, not reused, once a prerequisite's
own status on that arm decides against it — for ordinary and derived checks and for every ordersweep part of that
arm, and the sweep is then not skipped as all-reusable. It is never derived over the absence: a
derived check computed with its prerequisite excluded would read the absence as a
measurement, and its `no-finding` would be counted under `completedNullChecks`. The blocked entry is `missing`
and keeps the evaluation incomplete.

---

### 4.7 Scheduling: required-first among the checks that are READY

The executor walks the prerequisite graph topologically. At each step it looks at the checks whose
prerequisites are already satisfied and takes a **required** one first, registry order breaking ties;
an optional check with no prerequisites waits while any required check is ready.

Required-first means required-first among the READY checks, not required-first by prerequisite depth.
Ordering by depth puts every optional check that has no prerequisites ahead of a required check that
has one — which is how an optional download-pool probe spent six minutes of a run's budget before the
required geometry sweep had started. Budget exhaustion is a scheduling outcome, so the order a runner
picks decides which checks get to be measured at all: it must spend the budget on what the evaluation
requires before what it merely prefers.

**The budget is a deadline.** `--budget-minutes` (frozen into the plan; 0 or absent = no budget, and nothing
below applies) is a wall-time deadline for EACH `run` or `resume` invocation, measured with a monotonic
high-resolution clock from the moment that invocation opens its executor — after the plan, ledger and premise
checks, before re-validation, so re-validation time counts. `resume` does not inherit what an earlier
invocation spent: it gets the plan's minutes afresh.

- **Before launch.** No child is launched once the deadline has passed: the check is `skipped-budget`
  (`budget-exhausted`), and nothing runs. The budget is tested before a check's prerequisites, so a check
  whose prerequisite is missing only because the deadline stopped or skipped it is named `skipped-budget`,
  not `blocked(prerequisite:…)`.
- **While running.** The unit of work is a child's whole launched process tree, identified only from launch
  evidence (on Windows a Job Object the child joins while still suspended, before it can run or start
  anything; on POSIX its own session), never by executable name. The whole-tree guarantee is verified on
  Windows only: the POSIX branch has no executed controls, and a POSIX descendant that starts its own session
  escapes it (`HOUSECARL_RUN_EVENTS.md` §4, "Platform limit"). A child whose process exits while its
  descendants still run is still running: the controller keeps watching the tree, and a probe that exits 0
  but leaves a descendant running at the deadline is stopped, never accepted. A tree not empty at the deadline
  is stopped and must be confirmed gone within a **15 s cleanup allowance** past the deadline. If containment
  cannot be established the child is terminated before it runs and is not run uncontained: the RunLog attempt
  is `cancelled` with `stoppedBy: "containment"`, and every planned (check, arm, part) it was to measure that
  is not already terminal-ok is `blocked(uncontained)` — not a budget outcome, so no `budget-exhausted` cause —
  with a detail quoting the refusal and saying the child never ran; it is unsettled (`missing`, never a
  completed null) and `resume` re-runs it. The RunLog attempt for a stop is `cancelled` with `stoppedBy: "deadline"`
  (`HOUSECARL_RUN_EVENTS.md` §4), its log carries a stop trailer instead of the `(exit N, …)` completion
  trailer, and every planned (check, arm, part) it was measuring that is not already terminal-ok is
  `stopped(deadline)` (`budget-exhausted`) with a receipt that is not accepted and whose `exitCode` is null —
  a stopped child returned nothing; when its launched process had exited on its own before the tree was
  stopped, that process's code is named in the detail only. A stopped child's output is
  never read: for the ordersweep child that means its `summary.json` (absent, or an earlier invocation's), its
  per-job logs and its emits (possibly written by a probe that never finished) are all ignored. A part the
  plan was never going to measure keeps its own cause rather than the deadline's: a sweep arm kept out of the
  sweep by its prerequisite stays `blocked(prerequisite:…)` and none of its parts is marked `running`; an arm
  whose plan status is `skipped-budget` or `blocked-payload-missing` keeps that status; and under
  `--no-reverse` a candidate's reverse part is `blocked(no-reverse-measurement)`, as it is without a stop.
- **What survives.** Entries already terminal-ok before the stop stay so (and are marked `reused` as usual).
  Probe jobs inside a stopped sweep that finished before the stop are not receipts — the sweep's comparison
  was never written — but ordersweep's own cache keeps each one. Under a budget the controller passes its
  remaining deadline to ordersweep (`--deadline-seconds`), whose probes then run under the same tree rule: a
  probe is cached only once its whole process tree is empty, with its emit on disk and the identity hashes the
  cache re-verifies. A probe that exited 0 while a descendant still ran is never cached, so `resume` reuses
  only probes whose tree emptied (`cache-log.txt`: `REUSED`) and re-measures the rest (`RECORDED`).
- **After.** `finalize`, `bundle` and `verify-bundle` treat `stopped(deadline)` like any unsettled required
  entry: the evaluation is valid and incomplete, and `missing` names each stopped entry. `resume` re-runs them
  under its own deadline; the journal keeps the stop's history.

A cycle, or a prerequisite naming an id the registry does not define, cannot stall the pass: whatever
remains is appended in registry order rather than dropped, and the prerequisite gate still refuses it
at execution with `blocked(prerequisite:<id>)`.

### 4.8 Imported evidence (an AI-side evaluation)

Checks that only a language model's tools can fill (record and asset reads through houseCARL's server) are not in
the app's registry; an AI-side evaluation imports them over the app's run. How, and how each import is checked:
the integration module's [`AI-EVALUATION.md`](../integration/housecarl/AI-EVALUATION.md), "Importing an existing MCP
result".

### 4.9 Opt-in checks

A registry check may carry `optIn: true` (only with `required: false`). It is
planned only when the caller names it: `runcontrol plan --opt-in CHECK` (repeatable; naming a check that is not opt-in is
refused). Opting in makes the check **required for that run** (`required: true`, `optedIn: true` on the plan row, and
`explicitDecisions.optIn` lists it), so an evaluation that asked for it cannot finalize complete without it. Not opted
in, its rows are `not-opted-in`: `run` accounts them `skipped-user` with a sentence naming `--opt-in`, they never block,
they never count as unknown applicability, and finalize lists them under `unassessed.notOptedIn`. A plan that opts in
to nothing carries no `optIn` key, so the plans of registries without opt-in checks are unchanged.

The live switch-flip check, which launches the game, is an AI-side check: see the module's
[`AI-EVALUATION.md`](../integration/housecarl/AI-EVALUATION.md), "Live receipts".

## 5. Ledger and journal

`RUN/journal.jsonl` is append-only, one line per transition: `{t, checkId, armId, part, from, to, cause,
receiptId, runId}`. **It is the recovery source.**

`RUN/ledger.json` is a view of it plus the receipts, replaced atomically (`.tmp` + `os.replace`) after every
transition. Its head carries `planId`, `registrySha256` and `runIds` (one per `run`/`resume` invocation, each
with its runlog id). On `run`/`resume` the journal is read first: a `running` entry with no terminal line becomes
`interrupted` and is re-queued, never counted as done. A corrupt or missing ledger is refused **by name**;
`resume --rebuild-ledger` rebuilds it from the journal and the receipts, re-validating each. The journal
carries the transition; the per-entry facts a transition also writes — `detector` and `validation` (an AI-side
import's own facts are the integration module's) — live in the receipt, and the rebuild restores them from the receipt
the entry NAMES, refused unless that receipt's `receiptId`, `planId`, check, arm and part all match. This is
load-bearing rather than cosmetic: `finalize` reads `detector` from the ledger, so an entry that keeps its
status through a reconstruction but loses its detector is finalized as `detector-undetermined` — a check with
accepted evidence on disk reported as missing. A reconstruction preserves a usable RESULT, not just a status.

---

## 6. Resume and reuse

**`run` and `resume` offer the same guarantee.** A `run` over a ledger that already holds terminal-ok entries is a
reuse decision whatever the caller typed, so it re-validates exactly as `resume` does. **No verb reuses measured
work without re-checking it**, and which word the caller chose never decides whether evidence is re-read.

Per terminal-ok entry, in this order: the receipt gone → `invalidated(the receipt file is gone)`; a foreign
`planId` → `invalidated`; a **gap** → `refused-reuse(<gap>)`; a **change** → `invalidated(<what>)`; else `reused`
with `reusedFromRunId`. Every transition names its reason and, in the ledger entry's `detail` and `nextAction`, what
to do about it (re-run, re-plan, restore the named file).

**Gaps — inputs that cannot be verified at all.** Every one is named, not the first found: `configuration.reuseGap`;
no consumed plugins at all; any consumed plugin with no path or no 64-hex hash; any consumed overlay with no path
or hash; any output with no hash; a child-running adapter whose `tool.present` is true while its `dllSha256` is
null; a recorded base emit or `diff` comparison input (§4.4) with no 64-hex hash (the file was not there when the
receipt was written — finalize's walk reads the same receipt as `evidence-missing`, and the two verbs agree). A gap
is not a change — it is the admission that this entry cannot be re-checked, which is why reuse is refused rather
than granted.

**A plan-resolved plugin is re-checked at the path the plan named** — a row whose `hashSource` is `instance:<layer>`
carries a path and a hash like a scope-measured one, so it is a change or a gap on the same terms, and the arm that
supplied it reuses like any other.

**Changes — each named with its path.** A file that is **GONE is a change**, not an absence of one: an output gone
or hashing differently; the candidate file gone or changed; a consumed plugin gone or changed at its recorded path;
an overlay gone or changed; the probe's managed dll or `housecarl-core.dll` gone or changed when `tool.present`;
the base emit and the `diff`'s two comparison inputs (§4.4) gone or changed. (`exists() and hash-differs` read a
deleted plugin as "no change", which is how a receipt naming a file nobody could read any more stayed reusable.)

**Transitively:** `reverse` ← the arm emit; `diff` ← both; **every part of a non-baseline arm of check C ← C's
baseline `arm`**, because that is what it was measured against; a derived check ← every receipt it consumed; a
check ← its prerequisites on this arm and on the baseline. Independent entries stay `reused`. These edges are
computed in **one** place, so `resume`, `finalize` and `bundle` cannot disagree about what a result rests on.

**The frozen candidate hash gates every child LAUNCH, not just the receipt.** Before the sweep assembles an arm and
before any generic adapter's child starts, the **live** file at `arms[].candidatePath` is compared to
`candidateSha256`; a difference is `blocked(candidate-changed)` with cause `stale-inputs` and the next action "plan
again", and the sweep drops that arm's copy rather than handing `ordersweep` an order the plan never approved. The
assembled copy matching the plan says nothing about the file the plan points at, and a child that reads a changed
order while its receipt records the plan's hash files a measurement under an order nobody approved.

An arm `ordersweep` will serve from its own cache is **not** transitioned to `running`: only entries that are not
already terminal-ok are, so reused work keeps its `reused` status instead of becoming a fresh `completed` with a new
receipt for a child that never ran.

**Files a receipt names are never rewritten by a later invocation.** Every ordersweep launch writes its own
`RUN/sweep/inv-NNNN` folder (§4.4), so re-validating a reused part re-reads exactly the files it was validated from,
and a resumed COPY of a RUN reads and credits only files inside the copy: receipts carry RUN-relative paths, and the
ordersweep cache names emits relative to its own folder and verifies their hashes before reuse.

Two accepted receipts of one arm that consumed different content for one plugin are both `mixed-snapshot`.

**The programs are the plan's, checked before anything is written.** `run` and `resume` share one preflight. After
`plan-changed` / `plan-schema-unsupported` / `plan-request-outdated`, a plan without the identity contract is refused
**`plan-identity-outdated`**; then every program that can launch under the plan, and every program a retained
receipt of this plan names, is measured and compared with `toolIdentity` by digest (never by path or by the whole
metadata). A difference is **`tool-changed`**, a recorded program now gone **`tool-missing`**, an unreadable file
**`tool-unreadable`**, an override naming no file **`tool-unverifiable`**, a launchable program the plan records no
identity for **`tool-identity-missing`** — exit 2, one line naming the program, what differs (file, old and new hash)
and the action: "restore the recorded build, or make a new plan in a new directory". The preflight comes before the
premise check, `--rebuild-ledger`, the run store, the invocation record, healing, revalidation, invalidation, any child
and any output write, so a refused RUN is byte-for-byte what it was: plan, seal, ledger, journal, receipts and
evidence. Nothing rewrites `plan.json`, its seal or an old receipt to make a changed tool look unchanged.
**After a controller change (any file of its package, §3.1), every evaluation planned before it is refused, by design:
re-plan in a new directory.**

**And again at each launch and before each result is accepted.** Immediately before a child starts (a generic
adapter's probe, or the one ordersweep child with every swept probe, conflictdiff and the controller) its programs
are measured again: a difference launches nothing, writes nothing for that job and stops the invocation with exit 2.
After the child returns and before any receipt can be accepted they are measured once more: a receipt resting, by
role, on a program that changed is `rejected(tool-changed)`, then the invocation stops. Results accepted earlier
stand; a later run refuses at the preflight until the build is restored or a new plan is made. **The boundary:** these
checks see a change that is still present at one of them. A change made and undone while a child runs, or between two
jobs inside one ordersweep launch, is not detected, and nothing here makes the machine immutable. ordersweep records,
on every `jobs.jsonl` line, the program key it computed at its start (`program`, diagnostic).

The registry and the scope are premises, not inputs: a changed or missing one is refused by name before any of this
(§3). File hashing is memoised per invocation by (resolved path, size, mtime), so re-checking 80 plugins across 60
receipts reads each file once.

`run` or `resume` on an unchanged RUN performs **no child work** — every entry reads `reused`, the sweep is not
re-run — and writes no MO2 profile. `bundle` writes only under its output directory.

---

## 7. Finalize — `RUN/disposition.json` + `RUN/STATUS.md`

**Refusals first** (exit 3; a disposition is still written, with `complete: false` and the cause):
`plan-changed`, `plan-schema-unsupported`, `registry-missing`, `registry-changed`, `scope-missing`, `scope-changed`
(§3), `receipt-missing` (a ledger entry's receipt file is gone), `foreign-run` (a receipt's planId is not this
plan's).

**A completed claim is derived from evidence that still matches.** For every terminal-ok ledger entry the receipt
must exist, carry the ledger's `receiptId`, be `validation.accepted`, and every `outputs[]` file **plus every
run-internal input the receipt records** (`consumed.baseEmitPath`/`Sha256` and the `diff`'s two `consumed.inputs`
rows) must exist and hash to the recorded sha256 — a diff whose arm emit is gone is not a diff of anything. The walk
then follows the **same edges `resume` uses** (§6): an entry whose input's claim is void has no claim of its own.
An entry that fails is **not completed**: it is listed under `evaluation.missing` with status
`evidence-missing(<path>)`, `evidence-changed(<path>)`, `receipt-mismatch` or `evidence-dependency(<key>)`, and its
detector is counted **nowhere** — not under `findings`, not under `completedNullChecks`, not under
`completedCatalogues`. Only a terminal-ok entry can carry a detector row at all.

**A completed claim is derived from the programs the plan recorded.** For every terminal-ok entry whose receipt is accepted, each role of §3.1 must be bound in `consumed.programs` with a digest equal to the plan's for that program (a program the plan records `absent`, `unverifiable` or not at all is never equal; two missing digests are never equal). A role with no digest is `tool-identity-missing`, a different one `tool-identity-mismatch`; the entry is listed under `evaluation.missing` as `tool-identity-missing(<role>)` / `tool-identity-mismatch(<role>)`, is credited nowhere, and every entry resting on it follows (`evidence-dependency`). This reads retained metadata only: a consistent evaluation still finalizes, bundles and verifies after its tools change or are deleted. `evaluation.toolIdentity` is `{guarantee: "verified" | "legacy", detail}` (also a STATUS.md line): `legacy` for a plan without the identity contract, whose results finalize under their earlier guarantee and are never labelled verified.

`evaluation.integrity` is `{ok, defects: [{key, kind, path, recorded, found, nextAction}]}`. When any defect exists
finalize writes the full disposition and `STATUS.md` with the defects **listed first**, sets
`evaluation.refused: "evidence-integrity"`, and exits 3. An evaluation that is merely *incomplete* — checks that
were not run — is unchanged: disposition, exit 0. **Finalize never rewrites the ledger**; recovery is `resume`,
which invalidates and re-runs the affected entries.

Every disposition records what it was derived from: `derivedFrom: {ledgerSha256, receipts (one sha256 over the
sorted (ledger key, receipt file hash) pairs), journalLines}`. `bundle` refuses a disposition whose `derivedFrom`
no longer matches the run (§8), so yesterday's claims cannot be packaged as today's.

`evaluation.complete` is true only when **every** registry check that is `required` and whose applicability is
`yes` **or `unknown`** has an accepted receipt with `detector ∈ {finding, no-finding, catalogue}` on every arm it applies to
— or an arm-level `not-applicable`, or an admitted manual decision. `unknown` applicability is listed under BOTH
`unknownApplicability` and `missing`, and keeps `complete` false: unknown never becomes not-applicable.
`unsupported`, `manual` with no decision, `skipped-*`, `blocked`, `failed`, `rejected`,
`inconclusive`, `invalidated`, `interrupted`, `pending` and a completed check whose detector is
**undetermined** all keep it false and are listed by name under `missing`.

`evaluation.claim` is **exactly one of three strings**:

| claim | when |
|---|---|
| `no-findings-in-completed-checks` | complete, and every detector was `no-finding` |
| `findings` | complete, and some detector was `finding` |
| `incomplete` | anything else |

The words **pass**, **compatible** and **clean** are not claims this finalizer makes. A completed check that
found a defect is a completed check with a finding.

`arms[armId].orderEvaluation` carries ordersweep's classification verbatim with its probe list, per-probe
repaired / reclassified / created / newDefect / attributed / unmeasured, and `regression: true` when any probe's
`created.newDefect > 0`. **A supplied-combination arm with a regression stays a regression whatever its repaired
counts say** — no sum across probes, no net.

`manualDecisions` reads `RUN/decisions/<checkId>__<armId>.json`:
`{checkId, armId, decidedBy, decidedUtc, decision: completed-with-finding | completed-no-finding |
not-applicable, evidence: [RUN-relative paths], note}`. Accepted only when the registry check has
`manualDecisionAllowed: true`, the decision is one of the three words, `decidedBy` is present, and every evidence
path exists (hashes recorded). Listed with strength `attributed-decision`. A decision on an
`unsupported` check is **recorded but never satisfies completion**.

`unassessed` names, always: states not explored, candidates held back (this plan's and `candidates.json`'s own
`Budget`), populations with unjudgeable counts per coverage line, families unsupported or manual, the scope's own
`unmeasured` extents, and runtime observations. `explicitDecisions` carries `--select` / `--exclude`.
Media, patch discovery and the author's advice are the integration module's, and its own disposition carries them:
[`AI-EVALUATION.md`](../integration/housecarl/AI-EVALUATION.md).

`STATUS.md` is the **review index, not the report**: findings per check and arm, completed null checks, missing
checks with cause, order evaluation per arm, limitations, regeneration needs, explicit decisions, manual
decisions, and the ledger counts. The limitations list every `unassessed.unjudgeablePopulations` row by check and
arm, so a completed null check that left part of its population unjudged (a target footprint an arm does not
load, §4.1) is stated there and not only counted as a coverage line.

---

## 8. The bundle

`runcontrol bundle --run RUN [--out DIR] [--registry FILE] [--scope FILE]` writes `<out>/manifest.json`:

```
{ bundleSchema: 2, planId, createdUtc, runIds,
  required: [paths a reader may demand],
  evidence: [{id, kind, path (bundle-relative, forward slashes), sha256, bytes, checkId, armId,
              strength, provenance, recordedBy}],
  plan, ledger, journal, disposition, status, registry, search, scope,
  excluded: [{what: "plugin and asset binaries", why: "identities only"}] }
```

and copies plan / ledger / journal / receipts / decisions / candidates / emits / logs / sweep outputs / scope /
registry / disposition / STATUS.md, plus every accepted manual decision's cited evidence under `evidence/`.
Sweep outputs are copied at their RUN-relative paths — every launch folder, `sweep/inv-NNNN/...`, and any files an
older layout left directly under `sweep/` — and a receipt's log under `sweep/` is referenced there rather than
flattened into `logs/` (two launches write job logs of the same name).

**The registry and scope copies.** They are the plan's frozen premises, so before anything is written each must hash
to what the plan recorded. By default bundle reads the plan's stored path; `--registry FILE` / `--scope FILE` name the
frozen file instead, such as an earlier bundle's `registry/check-registry.json`, which is how an older run is
rebundled after the shipped registry changed, or from a folder its relative paths do not resolve from. A stored file
that changed, is missing or does not resolve is refused (exit 3: `registry-changed`, `registry-missing`,
`plan-path-relative`, and the `scope-` equivalents) and names the flag; a named file that does not hash to the plan is
a refused input (exit 2, `registry-mismatch` / `scope-mismatch`). No bundle that could only fail `verify-bundle` on
these copies is written.

`verify-bundle` compares every terminal-ok accepted receipt's `consumed.programs` with the bundle's own `plan.json`
by the rule of §7 (`tool-identity-missing` / `tool-identity-mismatch` defects; the bundle is then not complete, exit 3),
reading no tool and no workspace path, and writes `toolIdentity: {guarantee, defects}` into `verify.json`. A bundle
whose plan predates the contract verifies as before with `guarantee: "legacy"`, never `verified`.

### Evidence derived after measurement (enrichment)

`evalrun enrich --bundle DIR --kind names|order-geometry [--sweep DIR ...] [--reader-cmd CMD] [--reader-env K=V ...] [--mod-name P=N ...]`
adds files derived after measurement to a bundle that verifies (refused otherwise). It derives; it never measures.

- `record-rows`, `order-variant`: refused here. They are derived from imported record reads, which only an AI-side
  evaluation holds; see the module's [`AI-EVALUATION.md`](../integration/housecarl/AI-EVALUATION.md), "Record rows".
- `order-geometry`: the geometry rows of the setup without the patch in both orders of a free evaluated pair, from
  the ordersweep folders `--sweep` names (the measured order and its reversal). Refused when the pair's order is not
  a choice. Each probe's emits are copied into the bundle and cited by sha256.
- `names`: in-game names for the places and objects the bundle's rows name, read with `housecarl_records` from the
  plugin files the plan records — each hashed first and refused by name, before any read, when its bytes differ —
  on a private instance in which no plugin of the setup is active, so every read is an off-order read of the file at
  that path; an answer from an active load order, or cut short, is refused. Each evaluated mod's and patch's name is
  the declared one, else the Nexus catalogue cache's (offline), else the plugin name, with its source. The reads'
  request and response travel with the names. No network read. The objects read include each row's partner and
  every re-measured predicate's two objects; `refs` gives each object's base and the plugins read that carry a version
  of it in load order, and `bases` each base's type and model (`""` when the record reads the model as absent).

**The order step of `bundle`.** After the names step, `runcontrol bundle` says in the manifest's `order`
`{status, pair, rule, sentence}` whether the evaluated pair's order is a choice: `forced` (with `loadsFirst`),
`unknown` (a header fact missing) or `refused` (free: this controller does not derive the reversed setup's record
rows, and the sentence says so; the module derives them on its own bundle, see
[`AI-EVALUATION.md`](../integration/housecarl/AI-EVALUATION.md), "Record rows").

**The names step of `bundle`.** `runcontrol bundle` runs the `names` enrichment on the bundle it has just written (after
the bundle verifies), so every bundle carries what the report's body rule needs without a hand step. The reader is
`--reader-cmd`, else the command line in `HOUSECARL_RECORD_READER_CMD`, else this checkout's built server
(houseCARL's records tool compiled into `runcontrol`, run in process with no program started); `--reader-env` and `--mod-name` are passed through as
for `enrich`. `--no-names` skips the step. Whatever happens, the manifest says so in `enrichmentStatus.names`
`{status, sentence, reader, counts, seconds}`: `enriched` (every object the rows name and every base read),
`partial` (something could not be read: names.json's `unread` lists the objects and bases, `counts` gives
`objectsNamed`, `objectsUnread`, `bases`, `basesUnread`, and the report counts those clashes as cannot-tell),
`refused` (no reader, a reader that cannot start, a plugin whose bytes differ from the plan's, or any other
refusal, with nothing of the names written) or `skipped` (`--no-names`). The sentence is one plain sentence and is
also printed. A refused or skipped step leaves the bundle valid and the exit 0; `enrich --kind names` can add the
names later, and then rewrites the statement. verify-bundle checks the statement against the files: a status
outside the four or no sentence (`enrichment-status-unstated`); `enriched`/`partial` without the names files or
`refused`/`skipped` with them (`enrichment-status-contradiction`); `partial` not exactly when names.json lists
something unread, or counts that differ from its lists (`enrichment-status-mismatch`). A manifest with no statement
predates the step and is not refused for it.

Each kind writes `enrichment/<kind>/…` and an `enrichment.json` recording the tool's sha256, the base manifest's
sha256 and the disposition's sha256; its manifest rows are `kind: enrichment` with `derivedFrom`, and enter
`required`. A bundle is enriched once per kind (`already enriched` otherwise). **An enrichment may not change the
disposition, a receipt, the ledger, a claim or a count.** verify-bundle checks it: every enrichment file against its
manifest hash; an order-geometry file whose cited emit is absent from the bundle or hashes otherwise
(`enrichment-source-changed`); names read from bytes the plan
does not record (`enrichment-source-foreign`); and a disposition that is not the one recorded beside the enrichment
(`enrichment-stale`). Record-rows and order-variant files are checked by the module.

### Additive overlap triage evidence (`overlap-triage-v1`)

An actual-shape overlap scene may be enriched after measurement without changing `bundleSchema: 2` or
replacing its canonical `records`, `assets` and `pairs`. The enriched manifest retains every original
unordered `pairKey`, both directional object identities and the producer evidence, then adds
`triageSchemaVersion: "overlap-triage-v1"`, setup/source identities, attribution, status-bearing features,
cited analogues, ranking, incident membership and review state. Every original pair belongs to exactly one
incident and remains individually recoverable. Duplicate, dangling or unaccounted incident membership is a
refusal, never a silently dropped row.

Its accounting has three independent branches:

```
accounting: {
  measurement: <the producer's measurement accounting>,
  attribution: <configuration-comparison counts>,
  triage: <feature, ranking, incident and review counts>
}
```

The branches have their own denominators. Fewer incidents than pairs describes review grouping; it does not
describe fewer measured contacts or saved review effort. An image review count cannot be inferred from a
ranking count. Unknown, unsupported, unreviewed, inherited and patch-unchanged pairs remain counted.

Each setup records its role, configuration and validity plus the source manifest path and sha256 when a source
scene is used. An analogue cites the actual source setup and pair; a path without its matching hash is not
source evidence. Source, combined and patched roles remain distinct. Patch direction comes from explicit
before/after setup IDs and changed fields, not pair order, disappearance from a filter, or the patch's presence.

Every feature is `{status, value, reason, ...source metadata}`. Unsupported or incomplete quantities keep a
null or explicitly partial value and their reason. The classified-contact support envelope is a conservative
envelope around all classified triangle-contact support, not exact intersection extent, occupied volume,
visibility, collision or consequence. Bounds magnitude does not rank a pair. Exact intersection extent,
floor-relative height, winning-navmesh distance and quest context remain unsupported until independent,
identity-bound evidence supplies them.

`maxTrianglePairs` caps classified triangle-pair work; it is not a total allocation, mesh-preparation or
wall-time budget. Per-pair cancellation/timeout remains the wall-work guard, and an interrupted result retains
only `PartialValue` with `Value: null`.

`maxTrianglePairs` caps classified triangle-pair work; it is not a total allocation, mesh-preparation or
wall-time budget. Per-pair cancellation/timeout remains the wall-work guard, and an interrupted result retains
only `PartialValue` with `Value: null`.

When this manifest is carried by an accepted receipt, its path and hash enter the ordinary bundle evidence
chain. The extension remains a catalogue and review-order input. It cannot satisfy a missing registry check,
upgrade evaluation completion, establish that a patch repaired a physical problem, or make the bundle a full
compatibility result. The bundle's existing verification and strongest-null rules still govern every claim.

**Refusals (exit 3, by name, before anything is assembled):** `control-missing` (one of `plan.json`,
`plan.sha256`, `ledger.json`, `journal.jsonl` or `STATUS.md` is not in the run — a control file never drops out of
the required set because nothing was copied under it; STATUS.md is rewritten by `finalize`, the other four cannot be
regenerated); `disposition-missing` (no readable
`disposition.json` — finalize first); `disposition-foreign` (its planId is not the plan's); `disposition-stale`
(its `derivedFrom` is not the run's current ledger and receipts — finalize again); `disposition-refused` (it
records a refusal and makes no claim); `receipt-missing` (a terminal-ok ledger entry's receipt file is gone);
`evidence-missing` / `evidence-changed` (an accepted receipt's output is absent or no longer hashes to the value
the receipt recorded — naming the receipt and the path); `decision-evidence-missing` (an accepted decision cites a
file that is not there). A refused plan or ledger is a refused *input* (exit 2), as on `run`.

**A receipt's output is copied at the hash the receipt recorded, or not at all.** The manifest row carries that
**recorded** hash and `recordedBy: <receiptId>`; a freshly computed hash is never adopted for a receipt's output,
so a file altered after measurement cannot be certified under whatever it contains now.

**`manifest.required`** is what a reader may demand of the bundle, so a list that omits a required file cannot
prove completeness: the six control files (`plan.json`, `plan.sha256`, `ledger.json`, `journal.jsonl`,
`disposition.json`, `STATUS.md`), the registry copy, the scope copy when the plan had one, every terminal-ok
receipt, every accepted receipt's outputs, and every accepted decision's evidence. It lists what was actually
copied: a plugin or asset binary is never copied by policy (below) and so is never required — `bundle` has already
refused the *missing* and *changed* cases above, which is what `required` would otherwise be catching.

**Never a plugin or asset binary.** Files with a plugin, archive, mesh, texture, animation, audio or executable
suffix are referred to by name and hash and are not copied.

**Media.** Supplied screenshots, video and schematics are an AI-side addition, and `runcontrol bundle` refuses
`--media`; their item schema and admission
rules are in the module's [`AI-EVALUATION.md`](../integration/housecarl/AI-EVALUATION.md), "Supplied media".

`runcontrol verify-bundle DIR` reads **nothing outside the bundle** and writes `verify.json`. Its steps, in order,
each with a named defect:

1. `manifest.json` present and JSON (`manifest-missing`); `bundleSchema` is 2 — a schema-1 manifest is
   **`schema-unsupported`**: "this bundle predates evidence-chain verification; rebuild it from its run"; the
   manifest's required keys present (`manifest-incomplete`).
2. every `required` path present (`required-missing`); every `evidence` row present and hashing to its recorded
   sha256 (`missing` / `mismatched`).
3. `plan.json` hashes to `plan.sha256` (`plan-changed`); manifest, plan, ledger and disposition agree on `planId`
   (`planid-disagreement`).
4. the registry copy hashes to `plan.registry.sha256` and the scope copy to `plan.scope.sha256`
   (`registry-mismatch` / `scope-mismatch`, or `*-missing`).
5. the chain, walked from the bundle's **own ledger**: every terminal-ok entry's receipt present with the same
   `receiptId` and `accepted` (`receipt-missing` / `receipt-mismatch`); each of its outputs **and each emit it was
   compared against** (`consumed.baseEmitPath`, `consumed.inputs[]`) present under `evidence/` at the receipt's
   recorded hash (`evidence-missing` / `evidence-changed`), with every output also listed in the manifest
   (`manifest-omits-required`); each accepted decision's evidence present (`decision-evidence-missing`).
6. the disposition agrees with that ledger: every `findings` / `completedNullChecks` / `completedCatalogues` row
   names a terminal-ok entry with that detector (`disposition-unsupported-claim`); `evaluation.complete` is false
   whenever `missing` or `unknownApplicability` is non-empty (`disposition-complete-contradiction`); its
   `derivedFrom` matches the bundle's ledger and receipts (`disposition-stale`).

**Rows of kinds this SkyWright does not check.** A bundle whose plan records a registry version below 5 may hold
receipts and enrichments of the AI-side kinds or of the retired `patchprobe`: they are hashed and chained as above,
listed under `bundle.notChecked` and not judged. In a bundle of registry version 5 such a row is refused
(`plan-kind-unknown`, `receipt-kind-unknown`, `evidence-kind-unknown`), and a plan of a newer registry is refused
(`registry-version-newer`).

```
verify.json: { schemaVersion, checkedUtc, dir, bundleSchema, planId,
               bundle: {schemaSupported, structurallyValid, complete, verified, missing, mismatched,
                        defects: [{kind, path, detail}]},
               evaluation: {complete, claim, missing} }
```

`evaluation` is **read from the bundle's disposition, never recomputed**: `verify-bundle` says whether the package
is intact, not whether the evaluation was finished. Exit 0 only when `bundle.complete`. A relocated bundle validates
identically, and no plugin or asset binary is ever required or copied, so verification needs no installed mod and no
original workspace.

**Bundle integrity and evaluation completion are two different states.** Both are written. A structurally valid
bundle of an **incomplete** evaluation exits 0 with `bundle.complete: true` and `evaluation.complete: false`: a
correctly accounted incomplete evaluation is a valid deliverable.

---

## 9. Run store, timing and coverage

`evalrun` sets `HOUSECARL_RUNLOG_DIR=RUN/runlog` for itself and its children **unless the caller set it**, so a
run's timing history stays with the run rather than mixing into the shared store. Every child — `ordersweep`,
each probe, each accounted adapter — goes through `scripts/runlog.py`'s `Runner`, one job per child, and its
runlog id is written into the receipt. The parent run's features are checks / arms / probes; its stages are
`plan`, `checks`, `finalize`, `bundle`. `runcontrol` and `evalrun.py` are rows of
SkyWright's `docs/run-coverage.json`.

---

## 10. Guarantees, and the limits

**Guaranteed.**

1. A plan that changed after it froze cannot certify a run, and a plan of an older schema is refused by name rather than read.
2. A registry or scope that changed or went missing after the plan froze cannot certify a run — on every verb, by name.
3. A zero exit is never sufficient: twenty named causes stand between a child finishing and a receipt being accepted.
4. Zero findings with assessed coverage and zero findings because extraction failed are different results.
5. Nothing a caller can pass makes an incomplete evaluation complete.
6. A narrowed request stays visible: `--select` and `--exclude` change what runs, never what is planned.
7. **An accepted measurement describes the configuration its arm ASKED FOR** — ordered names, stated hashes,
   overlays, and a fingerprint that is the digest of what the emit itself states. Two outputs agreeing with each
   other never establishes agreement with the request, and an incomplete identity is not accepted for being first.
8. **An adapter that cannot state what it consumed says so** — the arm's own request with a null fingerprint and a
   note. The baseline's identity is never presented as proof of what another arm read.
9. Reuse is by content hash of every stated input, including the managed assembly, the base emit and a diff's two
   comparison inputs; a file that is **gone** is a change; an unstateable input refuses reuse by name; and `run`
   and `resume` give the same answer, so the verb a caller picks cannot buy a weaker check.
10. An interrupted run leaves a resumable, honest ledger — never fabricated success.
11. **A completed claim is derived from evidence that still hashes to what was measured.** Evidence gone, altered,
    or belonging to a different receipt is a named defect that completes nothing, and finalize refuses rather than
    reporting a claim it cannot support. The ledger is not rewritten to make that go away.
12. **A bundle accounts for every artifact its claims require**, copies a receipt's output only at the hash the
    receipt recorded, and declares in `manifest.required` what a reader may demand — so an omission cannot hide
    behind a list of what happened to be copied.
13. A relocated bundle verifies identically with no installed mod and no original workspace, a bundle never carries
    a plugin or asset binary, and bundle integrity and evaluation completion are reported as two separate states.
14. **Results come from the programs the plan recorded.** `run` and `resume` refuse, before writing
    anything, when a program the plan can launch or retained work came from is not the recorded build (content, not
    path); every accepted receipt binds the measured identity per role; finalize, bundle and verify-bundle reject a
    receipt bound to another build, offline.

**Limits this runner states rather than works around.**

- **It has no record check.** Checks that only houseCARL's record tools can answer are not in its registry; an
  AI-side evaluation adds them ([`AI-EVALUATION.md`](../integration/housecarl/AI-EVALUATION.md), "Importing an
  existing MCP result").
- **It cannot observe the engine.** No game is launched. Runtime, appearance, performance and crash behaviour are
  accounted as checks it does not run (`unsupported` or `manual`), never inferred from static checks; supplied media
  is the module's.
- **It cannot fetch.** It opens no network connection; patch discovery and the author's advice, which read a stored
  catalogue offline, are the module's.
- **It cannot stage a supplied patch's payload.** A supplied arm is ESP-only, and says so on the arm, in the
  disposition and in STATUS.md.
- **It cannot resolve a supplied plugin without the instance the probes read.** The measured scope states no
  hash for a plugin the profile does not load, so the plan resolves it the way the probes do — the layer
  precedence of §3, from `--instance`, the arm's view and the profile's modlist — and the arm's request then
  carries its path, its hash and the layer it came from, so a supplied arm's receipts bind and reuse like any
  other arm's. What that resolution needs is what bounds it, and each bound is stated on the arm rather than
  worked around: with no `--instance` there is nothing to resolve against; with no `--game` the layer that
  outranks every folder outside the view was not walked; with no `--profile` those folders cannot be listed;
  a name no layer provides stays `unstated` and `payloadMissing`; and when `modlist.txt` will not read, the
  folders outside the view have no priority order, so a name only they provide stays `unstated` with every
  copy listed under `providedBy` — the probes would refuse that order rather than rank those folders, and the
  plan does not rank them either. An `unstated` row is accepted with the emit's own hash under
  `validation.identityLimits`, and a child that states no identity is `refused-reuse(<gap>)` on that plugin,
  as before.
- **Program identity has a boundary.** It covers the files §3.1 names, not the shared .NET runtime, the Python interpreter or its standard library, nor what an override's program imports. A change made and undone while a child runs, or between two jobs of one ordersweep launch, is not detected (§6).
- **Enrichment readers are outside the identity guarantee.** `enrich` and the names step derive evidence after measurement and change no result: `enrichment/<kind>/enrichment.json` records the sha256 of the program that wrote it (`bundleenrich.py`, with `ordersweep.py` for order geometry; `runcontrol.dll` for the names step `bundle` runs), and `manifest.enrichmentStatus.names.reader` records only where the record reader's command came from, not the reader's own identity.
- **It cannot enumerate world states.** `states.notExplored` says the extent is unknown, not empty.
- **It cannot read a finding count out of an output shape it has not been told about.** A `json`/`text` adapter
  with no `adapter.result` is refused by the registry's own validation, and a `result` spec that does not resolve
  is `schema-unsupported` — never a zero. "We do not know" is deliberately more expensive than a wrong "clean".
