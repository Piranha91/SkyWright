# Evaluation presentation: reports for modders and narrated scene walkthroughs

**Class: LIVING.** The contract between `integration/housecarl/evalreport.py` (the compiler),
`integration/housecarl/evalreel.py` (the reel), the report's data
(`integration/housecarl/reporting-data/report-priorities.json`) and whatever reads the result - a person, a benchmark, a later
session. Its input is the bundle of [`docs/evaluation-bundle.md`](../../docs/evaluation-bundle.md), with what an
AI-side evaluation adds in [`AI-EVALUATION.md`](AI-EVALUATION.md); its runs follow houseCARL's run-event contract
(`standards/HOUSECARL_RUN_EVENTS.md` in the pinned houseCARL, which owns the run log).

**Where the code is.** Everything this contract names is SkyWright's: paths are relative to a SkyWright checkout, the
compiler, reel, templates, data and tests are in the houseCARL integration module (`integration/housecarl/`), and the
check registry is `data/check-registry.json`. This document is the module's own, beside the skills that drive these
tools (`integration/housecarl/claude-plugin/skills/`).

---

## 1. What the compiler is for, and what it refuses to be

The reader is a Skyrim modder deciding whether to use a mod combination or patch. The report names
the comparison in its title and scope, opens with the supported conclusion, and explains findings and
testing limits in ordinary language. The video speaks that conclusion over an establishing scene,
then guides attention through actual scene images or footage. It has required narration and no added
on-screen text. The skill's [reporting guidance](claude-plugin/skills/evaluation-reporting/SKILL.md) defines
the editorial workflow; these requirements supersede the original silent caption-card design.

The bundle remains the source of facts: findings, unfinished checks, comparisons, coverage, conflict
rows, media and limitations. The compiler owns joins, counts, selection and evidence links. Vocabulary
and conservative explanations live in skill data. Raw codes, hashes, command-line selections and
configuration IDs belong in the full evidence index and machine-readable artifacts, not the report's
opening or video narration. Missing comparison identities are stated as missing, never guessed.

An intact evidence bundle is not a complete evaluation. Incomplete testing cannot acquire a blanket
compatibility verdict; a null result supports only no problems found in the checks completed. Severity
cannot be inferred from row totals. A repair never cancels a new problem. Offline lost-edit or overlap
findings do not by themselves prove NPC obstruction or a player-visible defect. Images illustrate
only the configuration, state and subject they actually show. No model call, paid service or cloud
upload is required. Optional prose revisions retain the checks and limitations in §6.

---

## 2. Input and refusals (D1)

`evalreport plan --bundle DIR --out OUT [--exhaustive | --mode concise|exhaustive] [--max-pages N]
[--rows-per-group N] [--figures-per-finding N] [--location-scope ARM=FILE ...]
[--sibling-bundle DIR ...] [--order-setup ARM@PLAN=ROWS ...]`

The compiler first runs `runcontrol verify-bundle DIR` (the built-in controller: `HOUSECARL_RUNCONTROL`, else this
checkout's build) as a subprocess; the `verify.json` it writes is the
authority and is copied to `OUT/verify.json`. It refuses, by name, exit 2, nothing compiled:

| refusal | when |
|---|---|
| `bundle-missing` / `manifest-unreadable` | no `manifest.json`, or not JSON |
| `schema-unsupported` | `bundleSchema` is not 2 (the bundle predates evidence-chain verification; rebuild it from its run) |
| `bundle-refused` | `verify.json` says `bundle.complete: false`; the defects are listed by kind and path |
| `verify-failed` | verify-bundle could not run or wrote no readable verify.json |
| `sibling-unreadable` / `sibling-refused` | `--sibling-bundle` names no schema-2 bundle, or verify-bundle does not find it complete |
| `sibling-foreign` | one sibling: it has no setup whose plugins (names, order, sha256), population and states equal the prediction setup's. Several: a sibling holding no such setup also has no setup that is it with plugins left out, or its population or location scope is not this bundle's; the sentence names the bundle |
| `sibling-unanchored` | several siblings, none of which holds a setup equal to the prediction setup |
| `sibling-repeated` | a `--sibling-bundle` names the same evaluation (path, or plan id and manifest) as an earlier one |
| `order-setup-invalid` | `--order-setup` is not `ARM@PLAN=ROWS`, PLAN has no arm ARM, or ROWS holds no record rows naming their check |
| `order-setup-foreign` / `order-setup-same-order` | the order setup's plugins (names, sha256) are not the prediction setup's, or are in the same order |

Evaluation completeness, the claim and the missing list are **read from `disposition.json`, never
recomputed**. No bundle plan reads is written to or copied: `verify-bundle DIR --out OUT/verify.json` reads the
bundle in place and writes its verdict under OUT (each sibling's under `OUT/sibling-verify*/`). An earlier verdict
there is removed first, so a verify that writes none is refused. `--verify-copy` is accepted and has no effect.

**Local load-order context.** `scopeprobe` records plugins contributing world records to each target
CELL or exterior grid location. The report retains their actual relative load order for each measured
setup, including contributors that produced no conflict. It names the locations and the scope of that
list. A worldspace container holding edits elsewhere is not by itself a local contribution; parent-field
changes and local child records must be distinguished. Asset-only replacers and runtime placements are
outside the contributor map. The bundle's matching
baseline map is automatic; `--location-scope ARM=FILE` supplies a measured map for another setup. Its
plugin order and hashes, providers, overlays, population and state must match the frozen arm. The
compiler retains the source document and hash. Legacy conflict evidence can provide an explicitly
partial list; absence of a contributor map never becomes a claim that all contributors were listed.

**What a location lists.** A location lists only evidence indexed to that cell or grid coordinate:
CELL, NAVM, LAND and placed records, persistent references whose position falls in the grid cell, and
the `LargeReferences` rows and `MaxHeight` entry at that coordinate. A WRLD parent-field change applies to
the whole worldspace and is listed under `locationContributors.worldspaceWide`, never at each location; so
is a `MaxHeight` table added or removed as a whole. Record-header version bookkeeping (VCS1, VCS2, form
version), the rebuildable OFST seek table, WorldChildren group metadata and unchanged containers are not
contributions. `locationContributors.complete` is the map's own completeness and names each unclassified
input under `missing`; the top-level `complete` still describes only the target footprint. A report reads
the map's flag, and an incomplete map stays a partial list.

---

## 3. The presentation plan - `OUT/presentation.json` (D2)

Schema 1. Deterministic: the same bundle and settings give identical bytes apart from `generatedUtc`;
`presentationId` is the first 12 hex of the sha256 over the canonical plan without `presentationId` and
`generatedUtc`.

```
{ schemaVersion: 1, presentationId, generatedUtc,
  source: { bundleDir (as given), planId, bundleSchema, manifestSha256, dispositionSha256, ledgerSha256,
            verify: { bundleComplete, evaluationComplete, claim },        // exactly these three keys
            verifiedFiles: n },                                           // verify.json's bundle.verified
  compiler: { name: "evalreport", version, sourceSha256: { "evalreport.py", "templates/...", "report-priorities.json" } },
  settings: { mode, layout: "normal", maxPages, page: { size, marginMm, baseFontPt }, rowsPerGroup, figuresPerFinding, reel: {...} },
  scope: { targets[], registryVersion, planId, planCreatedUtc, runStartedUtc, bundleCreatedUtc, scopeGeneratedUtc,
           arms: [ { armId, kind, displayLabel, readerLabel, setupOrdinal?, candidateFile, pluginsAdded, pluginsRemoved, payloadNote,
                     espOnly, loadedInGameNote,
                     locationPluginOrder[], locationPluginScope: { basis, complete, missingInput, locations[], sameAs },
                     orderEvaluation (verbatim), identity: { pluginCount, unstatedRows: [{name, unstatedReason}], identityLimits } } ],
           states: { selected, notExplored }, completion: { complete, claim, missingRequired, byCause, unknownApplicability },
           integrity: { ok, defects }, explicitDecisions, checks: { <checkId>: { title, family, required, strength, claim, limits } } },
  reader: { title, scopeLine, verdict, limitations[], comparisonMods[], suppliedPatches[],
            sectionHeadings: { regressions, changes, prediction, coverage, validation },
            prediction: { subject, mods[], predictionArm, withoutArm, singleArms: {<mod>: armId|null}, verdict,
                          status: found|none-found, verdictCounts: [ { checkId, kind, unit, count, relationships,
                          objects, phrase } ], counts: { needsBoth|singleMod|notSeparable: [ { checkId, kind,
                          relationships, objects } ] }, missingFacts[], noneFoundBasis,
                          items: [ { checkId, kind, mods: [A, B], count, attribution: needs-both|single-mod|not-separable,
                                     attributionMod, attributionWhy, title, inGame, inGameText, plain, findingIds[], examples[] } ],
                          setAside: { baseGame, sameMod, alreadyPresent, history, notInvolving }, setAsideLine,
                          record: [ { checkId, kind, A, B, formKey, summary, admission, attribution?, attributionMod?, findingId } ] },
            coverage: { lines: [ { reason, count, unit?, subject } ], notes[], setAsideLine, checksOnPrediction },
            validation: { status: compared|no-patch, headline, patches[], hits, open, falseAlarms, patchProblems,
                          notMeasured, blindSpots: [ { admission, count, why } ], sentences[], missingFacts[],
                          withPatchArm?, withoutPatchArm?, otherComparisons?, sentence?, other? } },
  reelNarration: { opening: { narration, mediaId, storyboard }, closing: { narration, mediaId, storyboard } },
  reel: { status: planned|refused, reason, chapters: [ { id, kind: opening|vignette|data-still|closing, narration,
          still?: { pages[] }, frames?: [ { state: without|with|highlight, armId, mediaId } ], clash?, patch?, patches? } ],
          modNames: { plugin: { name, source: declared|nexus-catalogue|mo2-folder, nexusId?, reason? } },
          modNamesEvidence, media, patchSearch, captureResults, sequence, holdSeconds, policySha256, wordsSha256 },
  claimSentence,
  imports: { source: "disposition" | "receipts", limit,
             rows: [ { checkId, armId, receiptId, receiptEvidenceId, receiptPath, origin: fresh|reused|unknown,
                       capturedUtc, tool, sourceSha256, originCheck?, reusedFrom?, reusedFromVerified?,
                       reusedFromNote?, indexOrigin, readerProvenance, findingIds[] } ] },
  findings: [ { id: "F-NNN", kind, priority, checkId, family, armId, part, title, strength, verdict, plain, counts,
                evidenceIds: ["E-..."], mediaIds: ["M-..."], groupId: "G-NNN" | null,
                rows: [ { formKey, label, summary, physical, physicalDetail, evidenceId } ],   // the SHOWN subset: len(rows) == rowsShown
                allRows: [ ...every row, what the evidence index renders... ], rowsTotal, rowsShown } ],
  groups:   [ { id: "G-NNN", title, checkId, key: "Kind|A|B", findingIds, rowsTotal } ],
  evidence: [ { id: "E-<manifest id>", kind, path, sha256, bytes, checkId, armId, part, strength, recordedBy } ],
  media:    [ { id: "M-<sha256[:12]>", manifestId, path, sha256, mediaKind, subject, state, configurationFingerprint,
                fingerprintSource, provenance, sourceInterval, original, transformation, assessed, reason } ],
  unassessed: { <disposition.unassessed verbatim>, media: [...] },
  omissions: [ { what, why, where: "evidence-index.md#f-nnn" } ],
  chapters: [ { id: "C-NN", kind: opening|finding|comparison|missing|closing, title, findingIds, mediaIds,
                captions: [ { text, evidenceIds, mediaId, narration, storyboard } ], plannedSeconds } ],
  narration: { source: "template" | "answers:<sha256[:12]>", validated, refused: [...] } }
```

**The report leads with the prediction.** Every report opens, after the title and scope line, with three
sections in this order: the prediction, the coverage, the validation against the patch. `reader.verdict` is the
prediction's verdict ("Adding X will introduce these clashes between mods unless a patch resolves them: N …; and
M …", or "will not introduce … that the completed checks can see"), so the reel's opening speaks it first.

- *The numbers count what needs both mods, one kind at a time.* With two or more evaluated mods, the verdict states
  the `needs-both` items only, one clause per kind of clash (a check and its kind), each in its own unit and never
  summed with another kind (`prediction.verdictCounts`): objects and record fields are different things. A kind's
  unit is the headline unit (`prediction.headlineUnit`: `relationships`, the rows, by default, or `objects`, the
  distinct records the rows name; skill data `reader.prediction.headlineUnit`, overridden by `plan
  --headline-unit`) unless skill data `verdictKinds` fixes it: the record checks count records, because a field is
  a path, not a thing, and each record item's own line gives its field count ("N record fields whose change is
  lost, on M records"). Every item carries both (`relationships`, `objects`), and `prediction.counts` gives each
  attribution per kind in both units; nothing sums across kinds. Unseparated items add one clause, per kind
  (`verdictNotSeparable`). With one evaluated mod every admitted item is its own and is counted.
- *A clash one mod has with a third mod on its own* is shown under its own heading (`shownAs: alone`,
  `singleModHeading`) or set aside with the admission `singleMod` and counted in `setAside` — the setting
  `singleMod` (`show`|`aside`; skill default `singleModDefault`, overridden by `plan --single-mod`), recorded as
  `prediction.singleModMode` — and never counted in the number.
- *The scope line names the evaluated pair* (`reader.scopePair`, `scopeWords.pair`) when the counted clashes need
  both, and lists the third mods of the shown one-mod clashes after it (`reader.comparisonMods`); it no longer
  names the mod with the most rows.
- *Record lost edits* (kind `LOST-EDIT`, from an import's rows or a record-rows enrichment, bundle standard §8) are
  admitted and attributed like any row; the same lost edit reported by two record checks is counted once (the
  other set aside as `repeat`). Against the patch, a lost edit is resolved when no change to the field is lost
  with the patch, or a patch wins with a value other than the winning value without it; kept on purpose
  (`validation.kept`) when a patch wins with that same value, whichever mod's value it is; left when the field's
  winner with the patch is not a patch; cannot tell (`validation.valueUnrecorded`) when a patch wins but a winning
  value is absent or one of skill data `unrecordedValues` on either side. Each count is printed. A lost edit whose
  winner is the patch is the patch's choice (`validation.patchChoices`), never a problem it causes. Each admitted
  record entry says `withPatch` (`resolved`, `kept`, `left`, `cannot-tell`, `cannot-tell-value`, `not-measured`).
- *Names.* A names enrichment is copied into `reader.names` (places and objects by editor ID, each evaluated mod's
  and patch's ordinary name with its source), so a reader's page needs no side input. It also carries `refs` (each
  object a row, its partner or a re-measured predicate names: its base and the plugins read that carry a version of
  it, in load order) and `bases` (each base's type and model, `""` when the record has none).
- *Measured navmesh loss.* Overwrite history is never a clash, but where one evaluated mod's navmesh replaces the
  other's in P and the row's predicate holds (ground the replaced navmesh covered lies more than 120 units from the
  winner's), the prediction section says so as a measured fact (`prediction.historyLossLines`, skill data
  `historyLossLine`): how many records, how many with the loss, the three largest triangle drops, and the broken links
  counted above that point into a replaced navmesh. It makes no claim about NPC behaviour.
- *Concise page, each repeat once (P53, round 3).* The page states once, after the items, the attribution every
  shown item shares (`concise.onceNeedsBoth`), the reuse of the results counts rest on (`onceReused`) and the checks
  whose in-game look is not described (`onceInGameMissing`); a no-problem line is in its short form (`nullShort`); a
  provenance sentence the detail list already printed is said in short (`provenanceAgain`); a setup's pair groups in
  one check are one entry per plugin pair, each group still linked; more than two patches are named in the title
  only; the page lists up to `locationOrder.maxLocations` (10) locations per setup. The plan keeps every full
  sentence. Per-setup no-problem lines are never merged. `verify` refuses a rendition still over the limit.
- *Each object once in the headline.* A check whose row is a state of its own object (`identityByObject`) counts an
  object another such check already counted only once: the later check states its other rows and how many objects it
  shares (`verdictSharedObjects`), or that it names the same objects (`verdictSameObjects`). The items keep their own
  counts.
- *Examples.* A record item's examples take a field skill data prefers first (`examplePreferredFields`: a placed
  object's `Placement`), then any other.
- *Path case.* A lost edit whose two values name the same file, differing only in letter case or slashes (the game
  reads paths without regard to either), is not a lost edit: set aside as `pathCase` with its own sentence; the
  record-rows deriver compares paths the same way.
- *The setup behind "needs both".* With one evaluated mod, the setup without each partner mod that decided the
  attribution is named (`prediction.withoutPartnerArms`, and among the sibling's roles when it is the sibling's).
- *Still there is not "no problem".* A no-finding check whose comparison counts rows still physically there is not
  listed under the checks that found no problem; the still-there sentence is said once, with the comparison.
- *Left overlaps, split.* A geometry clash left by its own predicate (still intersecting with the patch) is counted
  per item as still reported between two mods (`openListed`), no longer reported with a declared patch's version of
  one of the two objects loading last among the plugins read (`openPatchHeld`), or no longer reported for a reason
  the record data does not give (`openNotAttributed`, "still intersect, not attributed"); the item line prints the
  split whenever either of the last two is non-zero. Every enrichment is listed in
  the evidence index under "Derived after measurement" with what it was derived from.

- *Prediction setup.* P is the setup with the evaluated mods and no patch: the arm that removes a declared patch
  when one does, else the baseline. W is the arm whose plugins equal P's minus every evaluated mod; S(m) the arm
  with only mod m of them. A row is keyed by check, kind, plugin A, plugin B, record and summary; presence on another
  setup is read on the row's identity: a record lost edit is its plugin pair, record and field; a geometry row with a
  partner object is the two objects, whoever owns them; a check whose row is a state of its own object (skill data
  `identityByObject`: a fixture left hanging, an object left without support) is that object. So an object both
  evaluated mods break is already broken with one alone when that one breaks it, whichever host the probe names.
- *Admission.* Only clashes between mods are predicted. A row is set aside (counted in `setAside`, listed in the
  evidence index's prediction record, one line on the page) when it is overwrite history (`contextOnlyKinds`), one
  mod against itself, present on W, has a base-game file on one side (the skill's `baseGamePlugins` and
  Creation Club `cc*` plugins, mirroring `PluginKinds.IsBethesda`), or involves no evaluated mod. A record value a
  plugin sets over a plugin it lists as a master (`ownMaster`) is that plugin building on its master: its change wins
  as its author intended, so it is no clash. A geometry clash between their objects (they intersect, lose their
  support, hang) shows in game whoever built on whom: it is counted, in the prediction and as a new problem with a
  patch, and labelled (`itemOwnMasterGeometry`, `newByCheckOwnMaster`). The master lists are the plugins' own, as the probes record them in each emit's
  configuration (`Configuration.Plugins[].Masters`), never names; such rows are said in one line per mod and master
  (`ownMasterLine`: "<mod> changes <master>'s value (its master)") and are never counted, in the headline, the patch
  tally or the new clashes with the patch. With no master list recorded at all, the coverage section says so
  (`mastersNotRead`) and the clashes are counted as found. A geometry row whose
  object or partner is a light whose base record has no model (`noBody`, read from the names enrichment's `bases`,
  never from a name) has no body to clash: it is set aside with its own sentence (`setAsideNoBody`) and never counted
  as a new clash with the patch (`validationNoBody`).
  Whether an object has a body is never assumed. With no names enrichment carrying `bases`, the coverage section
  says the base records were not read and how many clashes of the checks naming placed objects (skill data
  `bodyChecks`) are counted without that test (`bodyNotRead`); with it, a clash whose object's or partner's base it
  did not read is "cannot tell" and not counted (`bodyUnread`, `setAsideBodyUnread`).
- *Companions.* A plugin loaded in exactly the setups (of this bundle and the sibling) that load a given mod travels
  with it (its resources or masters); a setup without that mod and without its companions fills the same role as one
  without the mod alone. A target or a declared patch never travels with anything.
- *Attribution.* With one evaluated mod t, a row between t and another mod m is `needs-both` (t and m) when the setup
  without m was measured for that check and does not show it, `single-mod` (from t) when it does, and `with-target`
  ("appears when t is added; whether it also needs m was not measured") when no such setup was measured; never "comes
  from t alone" without that setup. With several, it is `needs-both` only when
  every S(m) was measured for that check and none shows it, and every evaluated mod is shown needed: it owns a record
  on the row (its object or a partner object whose FormKey names it), or another mod's S(m) leaves out exactly it
  (plugin names compared without case). An S(m) that leaves out more (plugins taken as companions, or a third
  evaluated mod) separates nothing: the row is `not-separable` with `onlyLeavesOutMore`, naming that setup
  (`noSetupLeavesOut` where none leaves it out).
  `single-mod` when S(m) shows it; otherwise `not-separable`, with the reason. A row is never called "needs both"
  from its plugin pair alone.
- *World states.* A world-state row (a quest stage or switch, its kind `STATE-...@<state>@...`, skill data
  `stateKinds`) is counted once however many states show it: the same object against the same other object between
  the same two mods is one clash, its item says in how many states, and the further states are set aside as
  `sameClash`. Rows against different objects stay separate clashes, even when their words are the same. The clash
  is the pair of objects: the same object in a base-game object with one mod alone is another clash. With the patch
  it is left if it is still there in any state.
- *Counted once.* A pair of objects an earlier check counts is not counted again by a later check (`samePair`; the
  earlier item says how many the other check also found), and one lost value two plugins carry is one field, counted
  under the plugin loaded first (`sameValue`). A quest alias split is one conflict per quest and alias pair
  (`identityBySummary`), however many aliases the probe lists it under.
- *Base-game geometry.* An evaluated mod's geometry against base-game objects no other mod edits is not a clash
  between mods, but it is said at the top by check; a clash from the mod alone against base-game objects another mod
  only edits says so.
- *By-object units.* A row of a check whose row is a state of its own object (a fixture hanging, an object left
  without support) is that object against one host or support, so its count is in such pairs (skill data
  `byObjectUnits`), never objects; the
  headline counts the objects apart.
- *Results from the sibling only.* A check this bundle has no result for on P, counted from the sibling's identical
  setup, says so in its item (the sibling bundle and file) and in the line that lists its failure on P.
- *New with the patch.* A clash the setup with the patch has and P (the same setup without it) lacks is new with the
  patch; nothing else decides it. A new clash another measured setup without the patch also has is still new, and is
  said as brought back from that setup.
- *Load-order risk.* `--order-setup ARM@PLAN=ROWS` gives the record lane's rows measured on PLAN's arm ARM, the
  setup P with the same plugins and bytes in another order. A lost change there between two plugins whose order
  differs from P's, involving an evaluated mod, that P does not lose (and not a path that differs only in case) is
  said at the top, one line per pair with its count, records and fields, as a measured record fact with no claim
  about the game, naming the setup it was measured on and linking `evidence/order-setup-<ARM>.json`.
- *The order of the pair* (`reader.prediction.loadOrder`). Whether the two evaluated mods' order is a choice (rule
  `master`, `ESM flag`, `free (both ESM-flagged)`, `free (both non-master)`, from the bundle's recorded header
  facts), the orders evaluated, and for a free pair whose bundle (or sibling) holds an `order-variant` enrichment of
  the setup without the patch, `changeHands` (the union of the pair's lost record fields over the two orders, with
  each order's losses by plugin and the reversed rows' path). With a declared patch and an order-variant of the
  patched setup, `withPatch` says whether the order still matters there. One sentence (skill words `loadOrderWords`)
  states each order's losses and never ranks the orders; the report, the evidence index (`#load-order-pair`), the
  reader's page and the narration packet read it; the reel does not.
- *Contributor lists.* A setup without its own measured contributor list that differs from the measured setup only by
  declared patches says so in one sentence; the measured list names plugins the findings name that it does not
  cover.
- *Sibling join.* A patched bundle has no W or S(m). `--sibling-bundle DIR` names the evaluation of the same order
  without the patch: its setup identical to P (plugin names, order and sha256, population, states) is the anchor,
  and its setups equal to P minus the evaluated mods (W) or minus all but m (S(m)) fill the roles this bundle lacks.
  A check is joined only when both bundles ran it with the same registry adapter, the anchor's and P's receipts
  record the same probe build, and the anchor's rows equal P's; otherwise its rows stay `not-separable` with
  `siblingDisagrees`. The build is compared by the emitter's bound program digest (`consumed.programs.emitter`) where
  both receipts carry one (`buildBasis: program`); otherwise by `consumed.tool` dll and core dll where both record
  them (`dll`, with a `buildBasisNote` when one side predates program identity or binds none); a check whose adapter
  names no probe and whose receipts record none has no build to compare (`no-probe`). Any other pair is
  `not-comparable`: not joined, with `siblingNotComparable` on the page. The controller's digest is not compared. A check this bundle did not measure on P but the sibling measured on the
  anchor is joined `how: from-sibling-only` — P's rows are the anchor's — only when the configuration the sibling's
  own receipt records for it equals P's plugin for plugin, in order, byte for byte; otherwise it is not joined and
  the reason names the configuration. Such rows are stamped `source: sibling` and link no finding of this plan. No anchor: refused `sibling-foreign`. The page says in one sentence that
  the single-mod setups come from a separate evaluation; the evidence index records the sibling's path, plan id,
  manifest sha256, the setup used for each role and each check joined or not with why. Validation still reads this
  bundle alone.
- *Several siblings* (item 21). `--sibling-bundle` may be given once per evaluation. The first that holds an anchor
  is the primary: everything above applies to it unchanged, and `reader.prediction.sibling` is its record. A further
  sibling is joined in one of two ways (`joinBasis`). `shared-setup`: it holds an anchor too, and a check is joined
  as above, its rows compared with the rows the prediction reads. `sub-order`: it holds no anchor, but its plan's
  population identity is this bundle's, its scope's locations include this bundle's (an unscoped bundle is covered
  only by an unscoped sibling), and it has setups that are P with plugins left out (same order, sha256, population,
  states); only those setups are read. A check is joined from it when it ran the check with the same adapter, the
  same probe build as the receipt the prediction reads (compared as above), and its receipt's recorded configuration
  is that setup's plugins, and only where a shared-setup sibling joined the same check (its rows agreed on the shared
  setup); no rows of its own are compared, since it shares no setup. A setup that fails is listed in
  `unusableSetups` with why. Roles (W, S(m), the setup without one partner, and the fewer-plugins setups that keep the
  evaluated mod and leave the partner out) are taken from shared-setup siblings before sub-order ones, and among equals
  from the earlier `--sibling-bundle`; a sibling whose identical setup another sibling's copy stands in for records it
  under `offered` with `usedFrom`. Where both measured that setup for a check and their rows differ, neither is used
  for it: the row is `not-separable` with `siblingsDisagree`. A role from a further sibling whose check is not joined
  gives `siblingNotJoined`. `reader.prediction.siblings` lists every sibling's record in the order given (only when
  more than one is given); the page's source sentence is `siblingSetupsMany`, naming each evaluation by its folder with
  the setups taken from it, and the evidence index lists each further sibling's path, plan id, manifest sha256, basis,
  roles, offers, unusable setups and checks. A one-target attribution decided on a setup that leaves out more than the
  partner mod (its companions, or a fewer-plugins setup) names that setup on the page (the `-fewer` attribution
  words), never "the setup without" the partner. Absence there shows only that the clash needs one or more of the
  plugins that setup leaves out: it is `needs-both` only where the row's other side is the partner's own record (its
  object or a partner object whose FormKey names that plugin), otherwise `needs-some` ("It needs {mod} together with
  one or more of the mods that the setup measured with ... leaves out, {other} among them"). Presence there is
  `single-mod`, as before. The reel reads media and masters from the primary sibling only.
- *Coverage* lists, on the page, every check with no result on the setups the prediction and validation read
  (by reason), each cannot-tell reason with its count on P, the fields a check's rows file lists as unjudged on P
  (`Unjudged`, by reason, with how many lie on records both evaluated mods change, where a clash between them is
  not ruled out), clashes that could not be separated, count-only results, and the unexplored states, scope and
  checks. Every count is P's own: read from P's own receipt (the disposition's list also carries the comparison
  receipts of the same setup), each population in its own unit and never summed with another. Reasons are words
  (skill data `unjudgeableWords`, `unjudgeableByCheck` for a count with no reason); a reason that names another
  check ("judged by X") is its own line, "judged by another check", by title. World states not examined are the
  census's classes in words with counts (`stateClassWords`), and the context line uses the same list. A check missing
  for several reasons on the same setups is one line naming them; `blocked(no-pair-source)`,
  `rejected(exit-nonzero)` and a completed check that reached no verdict are each worded (`missingReasons`). Checks
  the evaluator cannot run are named, and only those no missing line already names.
- *Validation*, when a declared patch comparison exists: predicted rows absent with the patch (hits), still present
  (open; never a false alarm without a recorded review), cannot tell (their state with the patch was not measured). A
  geometry row whose probe re-measures the other setup's rows (an emit's `Predicates`: the candidate's emit when the
  prediction setup is the baseline, the candidate's reverse emit when the baseline carries the patch) is judged by
  its own predicate: Gone resolved, Holds left whoever owns the objects now, anything else cannot tell; other rows by
  presence of their identity; new admitted rows with the patch, and rows the prediction
  did not admit that the patch removed (blind spots, each with why). What a bundle cannot say about the patch (its
  own edits, taste, set-aside edits) is one sentence each. The earlier comparison sentence is
  `validation.headline`, unchanged. With no patch: one sentence saying so, then that sentence.

**Units are never summed across kinds.** The headline states each kind in its own unit, an overlap kind with the
objects its pairs involve (`verdictObjectsToo`), and a cell-settings kind with the fields it is (`verdictFieldsNamed`).
The summary under the headline gives one line per kind with its own total and outcomes (`withPatchLines`), then what
stays with the patch in plain words (`withPatchStays`: the largest items still there with their counts, objects and
place, and every object one mod removes that another still brings back, counted with examples). A total in the patch
section over clashes of several units is given once per unit. The separate evaluation's setups are named in plugins
(`siblingSetups`), never as "each mod on its own". On the concise page an item line that only repeats its kind line
is said once, at the top.

**Units are never summed across kinds.** The headline states each kind in its own unit, an overlap kind with the
objects its pairs involve (`verdictObjectsToo`), and a cell-settings kind with the fields it is (`verdictFieldsNamed`).
The summary under the headline gives one line per kind with its own total and outcomes (`withPatchLines`), then what
stays with the patch in plain words (`withPatchStays`: the largest items still there with their counts, objects and
place, and every object one mod removes that another still brings back, counted with examples). A total in the patch
section over clashes of several units is given once per unit. The separate evaluation's setups are named in plugins
(`siblingSetups`), never as "each mod on its own". On the concise page an item line that only repeats its kind line
is said once, at the top.

**The patch section is actionable.** After the totals, one line per prediction item says what the patch does to
it (resolved, kept on purpose, left, cannot tell, not measured; `validation.itemLines`), with one line for all the
clashes already there with one mod alone; new clashes with the patch are counted by check with a link to each finding
(`validation.newByCheck`), by what the clash is: an object of a check whose row is a state of its own object
(`identityByObject`) once however many rows or plugin pairs name it, and an object with no body left out once; the
comparison's own "introduced N new problems" counts the same way where its created rows are known; the changes a patch
undoes are linked to their findings (`validation.undoneFindingIds`); the overwrite history the patch removes says how many of its rows held a measured loss
(`validationHistoryHolds`). A prediction item says which way a record edit is lost when every row goes the same way,
gives up to two example records (record and field) or the places most of its rows are in (every row's place counts,
one named by form key included, so "and N more places" and "All are in" are computed from the listed rows), carries the reuse
qualification of the result it rests on, and links every finding it counts. In the detail list a lost edit whose
winner is a declared patch reads as the patch's own choice (`patchChoiceLine`). Patches are named in load order.

**Concise pages say each fact once.** In concise mode: the clashes already there with one mod alone are one line
per check, grouped by the mod they come from, the unit and in-game sentence once (`prediction.aloneLines`); in the
detail list a setup's several pair groups of one check are one line, each pair with its count and link (skill data
`concise.compactGroupsOver`), and a setup's lost edits that a declared patch wins are one line counting them as the
patches' own choices (`concise.patchChoicesCollapsed`); more than two declared patches are named once and then "the N
patches"; plugin lists are one comma-separated line; the areas not measured and the objects not judged are stated
under the coverage heading only; "What still needs checking" lists only the missing checks on setups the coverage
section does not list (`reader.missingShown`; `missingSummary` stays complete in the plan) and one sentence points to
the coverage section for the rest. The evidence index keeps every group in full; exhaustive mode is unchanged.

**A comparison is stated once, from its own record.** Each comparison of a setup with the baseline is read from
its order evaluation verdict, its log and the controller's missing list (`comparisonState` in skill data). A
comparison that refused a verdict (an `UNJUDGED` verdict, a `REFUSED:` line in its log) prints no repaired or created
count anywhere (its findings carry `readerEvidenceReason: comparison-refused`); the change section and "what still
needs checking" say it refused and why (the share it could not judge, before and after, against its limit), and the
predicted clashes of that check are "cannot tell" against the patch. Counts the controller did not accept (a missing
`diff` row whose comparison was not refused, here because rows could not be measured on both setups) are printed with
that qualifier, and the missing line gives the same reason instead of "has no accepted result". A verdict naming new
problems gives their counts by check, one count per setup. Rows that left a check's class with their defect still
there ("left the class still holding N") are named beside that setup's no-problem line.

**The detail list is clashes between mods.** "What the checks found" lists a finding group only when it names two
different plugins and neither is a base-game file. A group within one mod, against the base game, or naming no pair
(a count only) carries `readerDetail: false` and `readerDetailAside` (`sameMod`, `baseGame`, `countOnly`); the page
counts them in one sentence and the evidence index lists each under `#detail-set-aside` with its reason, pair,
sentence and any origin qualification. `readerRelevant` is unchanged by this, so the verdict, opening, reel and the
regression and change sections read what they read before.

**Import notes are stated once.** The notes importing tools record (`imports.notes`) reach the page as one bullet
when they share sentences (the shared ones, in order) and, with more than three that share none, as one bullet with
their count, the clauses they all share, and each opening clause at least half of them share with its count (say,
reused against freshly measured); fewer are printed as they are. Every note is in the evidence index in full.

**A shared location list is printed once, and a long one is bounded.** When several setups' load-order lists cover
the same locations, the locations are printed at the first setup that has them; each later setup says so in one line
(`locationPluginScope.sameAs`). Each setup's plugin list and coverage sentence stay its own. The page prints at most
`locationOrder.maxLocations` locations (`locationPluginScope.shown`) and counts the rest in one line
(`moreLine`); the evidence index's technical scope lists them all.

A presentation without `reader.prediction`, `coverage` and `validation` is refused `presentation-needs-replan`.

**Patch naming and comparison direction.** An arm's change is its requested plugin list against the baseline
arm's: `pluginsAdded`, `pluginsRemoved`. `suppliedPatches` is every non-target plugin any alternative adds or
removes, whatever the arm's kind - a candidate may add the patch, or the baseline may carry it and the
candidate remove it. When it is non-empty the title reads `<targets> with <patches> — patch review` and the
opening names the patch and whether the comparison adds it, removes it from the measured load order, or both;
otherwise the setups differ only by the target mods, the title reads `<targets> within the measured load order —
conflict review`, and the opening says no separate patch plugin was included. The mods the target's recorded
conflicts involve are named in the opening as the main conflicts found, never in the title as the comparison.
A reader sentence about a comparison (a finding's `readerPlain`, the verdict, `sectionHeadings`) names the arm's
change - "Adding X", "Removing Y" - and falls back to "The supplied setup" only for an arm that adds
and removes nothing. Sentences covering several arms with different changes say "A tested alternative setup"
when any of them removes a plugin. A removal arm is never called the supplied setup. The skill's overlap
wording templates (`reader.comparisonTemplates`, `reader.verdictTemplates`) take the same subject as
`{subject}` at a sentence start and `{subjectLower}` elsewhere. A presentation without `sectionHeadings` is
refused `presentation-needs-replan`.

**Telling alternatives apart.** `displayLabel` names a setup's kind ("Tested alternative setup", with
`: <added>` when it adds plugins) and heads its row under "Setups compared". Several alternatives can share
it - every removal-only arm has the same one. `readerLabel` is the label every per-setup sentence and
heading uses (`findings[].readerArmLabel`, the no-problem and comparison lines, the local load-order
headings): the `displayLabel` itself when no other arm carries it, otherwise the `displayLabel` followed
by the arm's change in the setups-compared words, e.g. `Tested alternative setup (removes X.esp)`, and, if
two arms also make the same change, `; <n> of <m> setups with this change` (`with no plugin change` when the
change is empty) inside the parentheses, numbered in setups-compared order. That arm's `setupOrdinal` carries the
same words and its setups-compared row ends with them, so the label points at one row without a technical setup
id. A no-problem line therefore names each clean alternative and is never merged across alternatives whose
results differ. A presentation whose arms lack `readerLabel` is refused `presentation-needs-replan`.

When a setup's local load-order list falls back to plugins named by target-conflict evidence, its
`missingInput` says so, and names each patch in `suppliedPatches` that the setup loads but the list leaves
out.

**Finding kinds** and where each comes from - nothing is derived twice:

| kind | source | one finding per |
|---|---|---|
| `regression` | `arms[].orderEvaluation.perProbe` with `newDefect > 0`; rows are the groups conflictdiff's own log line marks `NEW` / `GREW` with `created n`, pulled from the arm emit | (arm, probe) |
| `finding` | `evaluation.findings` rows; a conflict-file emit is split into its groups by `(Kind, A, B)` with every `Necessary` row; a diff part with no regression, or a non-conflict output, is one count | (check, arm, part, group) |
| `repaired` / `reclassified` | `perProbe` with repaired or reclassified `> 0`, both numbers on one finding, never netted | (arm, probe) |
| `null` | `evaluation.completedNullChecks`, with the receipt's coverage lines | (check, arm, part) |
| `catalogue` | `evaluation.completedCatalogues` | (check, arm) |
| `missing` | `evaluation.missing`, status and cause verbatim | (check, arm) |
| `unassessed` | `manifest.unassessedMedia` (area `media`) and every list under `disposition.unassessed` (areas `states`, `candidates`, `unjudgeable`, `families`, `scope`, `runtime`) | item |

IDs: `F-NNN` in priority order; `E-` is the bundle manifest's own 16-hex evidence id; `M-` is the first 12
hex of the media file's sha256 (two files with one basename never collide); `G-NNN` per `(checkId, Kind, A,
B)` across arms and parts. A finding's `plain` sentence is the skill template for its kind, bound to its
`counts`; the check's `title` is the registry copy's. **Anchors and links carry the exact token, case
preserved**: `evidence-index.md` has `<a id="F-001">`, `<a id="E-03e9…">`, `<a id="M-…">`, `<a id="G-001">`,
and `evidence-index.html` anchors the same id set — the same tokens as `id=` attributes on the element the
reader lands on (a finding's own heading, a group's, media's or evidence file's own row) — plus the named
`top`, `missing-results` and `imported-evidence`. Each report links into the index of its own format:
`report.md` targets `evidence-index.md#E-…` or the finding's anchor, `report.html` the same fragments of
`evidence-index.html`. A `null` finding has
`rows: []`, `rowsShown: 0`. The reader projection adds display labels and explanations without replacing
raw fields, counts or identities. Unknown detector vocabulary stays identifiable in the index rather
than acquiring an invented domain explanation.

Navmesh overwrite prose names the later winning plugin and the plugin whose edits it replaces only
when the probe's directional evidence agrees with the measured order. A generic conflict's A/B fields
are not a universal direction convention. Object-overlap totals count candidate pairs, not distinct
objects or confirmed gameplay problems. A comparison with no new pairs can retain thousands of existing
pairs: results for a setup and changes relative to another setup remain separate statements.

**Totals beside a comparison name what they count.** A no-additional-problem comparison line states `In total
this check reported <n> records with a problem in the measured load order and <m> in this setup` (a kind's own
unit where the skill's `kindPhrases` give one, e.g. overlap pairs). Each number sums the listed rows of the
check's findings on one setup - the measured load order's arm part, and this setup's own measurement - across
every plugin pair the check reported. When some counted pair involves neither a target nor a compared patch, the
sentence says the report does not list those conflicts. Rows of a context-only kind (navmesh overwrite history)
are not counted, and the sentence says how many it left out on each setup. The evidence index's `out of N
conflict(s) the probe reported on that part` is the probe's own total, overwrite history included, so it can
exceed the report's count without contradicting it; the report's sentence must make that difference readable.

**Overlap triage catalogue.** `evalreport triage --manifest TRIAGED.json --out OUT` consumes the additive
`overlap-triage-v1` envelope. It verifies the recorded triage-policy hash and refuses duplicate, dangling or
unaccounted incident membership before writing `overlap-triage-catalogue.json` and
`overlap-triage-catalogue.html`. Its identity binds the source-manifest hash, triage schema, policy hash and
report compiler version; a changed source or policy requires regeneration.

The catalogue names candidate plugins and comparison setups when supplied. Source, combined and patched setup
roles remain explicit, with source paths and hashes attached to cited analogue placements. Patch direction comes
from the validated before/after setup IDs and changed fields. A patched aggregate scene is never presented as a
candidate-off source analogue. Every incident keeps its representative and complete pair membership, readable
effective-base objects, contributing mods, ranking reasons, review state, limits and next action. All, unknown and
unreviewed filters retain visible denominators and change presentation only.

Ranking determines review order; image review determines only what the inspected view supports. Neither is an
impact probability or compatibility verdict, and a judgment on one pair does not propagate to its incident.
Analogues with incomplete contact-location evidence cannot lower priority. Unsupported exact intersection extent,
floor/navmesh context, quest context, runtime state and visual exposure remain visible with reasons. Patch repair
credit still requires explicit comparison direction and independent consequence evidence.

This catalogue supplements the verified evaluation report. It cannot turn an overlap-only receipt, bounded scene
scope or reviewed sample into a complete compatibility evaluation. Registry completion, missing checks,
unassessed media and the strongest-null sentence continue to come from the bundle disposition.

**Navmesh evidence gate.** The skill's `reader.contextOnlyKinds` keeps bare `CLOBBERED` rows in the
evidence index. Neither an overwrite nor its per-record coverage-loss predicate establishes a
mismatch with the winning scene. They cannot drive reader/video incompatibility verdicts or count
as repaired/new compatibility problems. Raw bundle findings, differential totals and identities stay
intact; reader counts use only attributable, non-context results, and inseparable aggregates remain
in the index. An independently measured active link or geometry problem is reported under its own
evidence. Exhaustive reports retain the same gate. No corroboration does not mean compatibility.

Setup labels describe what was evaluated, not what caused a result. A statement that a problem
already existed before the patch needs matching baseline evidence for the same record and conflict;
without that match its history is unresolved. A claim that a patch contains no override requires
record contents or a complete provider history, not the generic A/B conflict fields.

**Media binding.** A media item declared for arm X illustrates X's own configuration: it binds (by
`subject.checkId`, `subject.armId` and, when given, `subject.formKey`) to X's findings with part null, `arm`
or `diff` - never to X's `reverse` part, which is the baseline order measured with X as `--base` (the same
principle as the bundle side's arm rule for accepted receipts).

**Imported evidence and where it came from.** A result `runcontrol import-results` bound to a frozen arm declares an
origin, and the report says what that means for the reader beside the result it affects. Which imports are described
comes from the disposition's `importedEvidence` list; a bundle finalized before that list existed has none, and then
an accepted receipt carrying an `imported` block whose (check, arm) is a completed result in the disposition counts,
with `imports.source: "receipts"`. What the origin is comes from the receipt the bundle carries - the controller's own
recovery authority - and anything that is not `fresh` or `reused`, a receipt written before an origin had to be
declared included, is `unknown`. **Absence is never read as fresh.**

| origin | what the reader is told, beside the affected result |
|---|---|
| `fresh` | nothing: the result was measured for this evaluation, which is the ordinary case |
| `reused` | that it was not measured for this evaluation but reused from an earlier one, and that the source was stated by whoever supplied it rather than re-checked here |
| `unknown` | that it is not recorded whether it was captured for this evaluation |

The sentences are skill data (`reader.importOrigin`), bound to no numbers; `findings[].importOrigin` and
`findings[].readerProvenance` carry them to the result's own line in report.md / report.html, to its no-problem or
catalogue line, and to that chapter's narration. Source plan id, source arm id, `resultSha256`, receipt id, capture
time and the capture-token check stay in the evidence index's **Imported evidence** section, which also states, once,
that the labels catch evidence filed under the wrong evaluation by mistake and are not proof of where the evidence
came from (the capture token and the timestamps are public and rewritable). The controller's ledger status `reused` -
this run re-using its own accepted receipt on a resume - is not an origin and is never described to a reader. An
import that is not credited (its prerequisite failed after it was accepted) has no origin row and no result line: it
is a missing check, reported as one. Origin information changes no count: findings, their rows, the completion block
and every total are what the same bundle gives with the origin fields removed. A presentation planned before this
existed carries no `imports` and is refused `presentation-needs-replan` rather than rendered silently.

**A blocked check names what it waited for.** A missing row whose status is `blocked(prerequisite:<checkId>)` is
summarized with `reader.missingPrerequisiteReason`, which names that prerequisite by its reader title, so "not
checked" says which check has to succeed first. The status and cause stay verbatim in the evidence index.

**Chapters and scenes.** Chapters preserve finding/evidence references and support the reel's selection.
Their legacy `captions` field is text data, never an instruction to burn text into a video. A human-facing
summary supplies the spoken opening and testing limits. A storyboard selects qualified scene media,
binds narration to its finding, and declares any crop or attention guide; its executable schema is in
[reel-layout.md](claude-plugin/skills/evaluation-reporting/references/reel-layout.md).
Missing media stays a recorded omission or a named prerequisite failure, never a text card. A duration
budget may shorten the visual inventory but cannot hide a new problem or the limits of the evaluation.

`storyboard-packet` also produces source-verified `capturePlan` requests using the skill's
scene policy (SkyWright's `integration/housecarl/reporting-data/scene-policy.json`). Navmesh requests
identify both versions of the same record for comparison at one measured camera pose only when a
separate active link mismatch is bound to that exact record in the same setup's source emit. Both
ends of the broken link and its evidence remain in the request; the version change is context, not
the claimed cause. Overlap requests
identify both objects, assign distinct colours and select representative pairs using reported metrics.
Rank is a selection aid, never an impact verdict; eligible, omitted and unranked counts remain visible
in the packet. Capture requests do not qualify media. The driver captures and visually reviews the
requested subjects, adds their evidence through the normal media workflow, then rebuilds the presentation.
Use viewprobe `--report-mode` to remove captions and controls at source while retaining scene overlays,
projection, framing and replay metadata. Related shots share one camera and canvas; clean captures need
no independently chosen caption crops.

---

## 4. Priority, grouping, abridgement (D3) - skill data

`report-priorities.json` owns report vocabulary, section priorities and concise settings. Findings retain
their stable technical ordering and IDs for evidence joins; the reader sees a conclusion, comparison,
new problems, findings, patch results and remaining testing in plain language. Every required missing
check is accounted for, grouped by subject and reason; exact check IDs and execution causes remain in
the index. Findings on the baseline and each alternative remain distinct, and a reverse comparison
does not become an additional set of independent defects.

**Concise mode** summarizes relevant findings and limits figures; reverse comparisons (the baseline order
re-measured with the candidate as `--base`) are index-only. The plan retains selected row samples under
`rows` and the complete inventory under `allRows`; raw record tables live in the evidence index. Legacy
`rowsPerGroup` and `rowsShown` fields describe that selected sample, not a row table in the reader-facing
report. **Exhaustive mode** retains all figures and comparison parts and has no page limit; it keeps the
same reader-facing explanations and full linked evidence inventory.
Independent dimensions stay separate fields: strength, verdict, counts, applicability, evidence kind.

---

## 5. Report outputs and the print check (D4)

`evalreport report --presentation OUT/presentation.json --out OUT [--no-print]` writes:

| file | what |
|---|---|
| `report.md` | portable, modder-facing prose; figures by relative path under `OUT/media/`; readable evidence links resolve to exact finding/evidence anchors in the index |
| `report.html` | the same content; `@page { size: A4; margin: 16mm }`, base 10.5 pt, no font below 9 pt, figures at most 100 mm tall, tables wrap (fixed layout, break-word), rows and figures never split across pages |
| `report.pdf` | Chrome or Edge headless: `--headless=new --disable-gpu --no-pdf-header-footer --print-to-pdf`; resolution order `HOUSECARL_BROWSER`, PATH `chrome` / `msedge` / `chromium`, the standard Windows install paths |
| `evidence-index.md` | complete in both modes: technical scope and selections, every finding with all its rows, every group, media, hashes, imported evidence with its origin and declared source, original registry claims/limits, omissions and chapters |
| `evidence-index.html` | the same index rendered for a browser, from its own template beside the Markdown one — the same content, the same ids as working anchors, `report.css` inlined. A static server hands `.md` to a reader as text, where no heading renders and no fragment resolves; `report.html` therefore links here, `report.md` to the Markdown index, which stays the data artifact |
| `presentation-manifest.json` | source identity, compiler hashes, settings, the effective ladder step, produced artifacts with sha256, referenced media with sha256, omissions, and `print: { browser, version, pages, maxPages, status, reason, ladder: [...], rootCount }` |

The overlap-triage entry point separately writes a version-bound `overlap-triage-catalogue.json` with all pair
membership and evidence, and a readable `overlap-triage-catalogue.html` with filters and expandable source detail.

**The page count** is read from the PDF itself: the `/Type /Page` objects (not `/Pages`) are counted and
cross-checked against the root `/Pages` node's `/Count`. `print.status` is one of:

| status | meaning |
|---|---|
| `within-limit` | concise mode, pages <= `maxPages` |
| `over-limit` | concise mode, the abridgement ladder ended above the limit; the count is recorded |
| `rendered` | exhaustive mode, no limit; the count is recorded |
| `not-rendered` | no browser, or the browser wrote no PDF; the reason is recorded, Markdown and HTML are still written, **no page count is claimed** |
| `count-unverified` | a PDF exists but its page tree did not yield a count that cross-checks; the reason is recorded |

Concise mode over the limit walks the abridgement ladder deterministically (skill data, one row per step
with `layout`, `rowsPerGroup`, `figuresPerFinding`). The compact layout adjusts spacing, never the base
font size. Legacy sample-size steps may have no effect on the reader report; every actual printed page
count is still measured. The last step reduces figures. **Figures are dropped only when that step alone brings the report within the limit**,
and **an abridgement that does not buy the limit is pure loss**: when no step reaches the limit the kept
rendition is the least abridged step that loses no content - the compact layout (chrome, not content) at the
setting's own rows per group and figures per finding - `print.status` stays `over-limit` with that count,
`print.reason` says the limit was unreachable without hiding required content and lists the page count at
each step, and the kept step is re-rendered so the three renditions on disk agree with each other. A
figure-dropping step is skipped when no finding has a figure. Every step is recorded under `print.ladder` with its layout and page count, the kept
step under `print.kept`, and the manifest's `effective` names the kept layout, rows and figures. In concise
mode the original registry claims and limits stay in the evidence index, with plain-language limitations
in the report.
**The font is never reduced.** The measured count lives in the manifest, never in a rendition (a PDF cannot
truthfully describe its own page count before it is printed).

A shareable rendition carries no machine path: an unassessed media item is named by its basename, media id,
declared kind and subject arm, with its reason; the evidence index shows the declared path as
`<supplier folder>/<basename>`; the plan and manifest keep the declared path as data.

`evalreport verify --out OUT` re-hashes the artifacts and referenced media against the manifest, resolves
every relative link and anchor from OUT (so a relocated OUT verifies identically), recounts the PDF's pages
and compares them to the manifest, and writes `presentation-verify.json`; exit 3 with the defects named
(`artifact-changed`, `link-unresolved`, `anchor-unresolved`, `page-count-mismatch`, ...). It is what the tests
and the judgment call.

---

## 6. The narration seam (D7)

`evalreport narrate-packet --presentation P --out FILE` writes every finding's id, template sentence,
`counts`, `boundNumbers` and `boundNames`, plus the plan's check ids and arm ids, the instructions (which ask for
plain words and name the tool's own words to translate) and the `glossary` of those words' plain meanings, both from
the reporting skill's `narration.json`. An optional narrator - a model, a person - answers
`{ "presentationId", "answers": [ { "id": "F-001", "text": "..." } ] }`.

`evalreport narrate-packet --presentation P --answers FILE --out J` writes the fidelity judge's packet instead: each
answer's `text` and `textSha256` beside its finding's template sentence, counts and bound rows (`rows`: every row of
the finding, not only those the report shows, with form key, cell, summary and physical detail), the glossary, and the
judge's instructions. The judge runs in the agent `evalsettings.py route models.checkExplanations --session-model ID` names
(never a Fable model: a Fable session is routed to Opus, and says so) and answers
`{ "presentationId", "judgements": [ { "id", "textSha256", "faithful", "facts", "added", "reason" } ] }`.

`evalreport narrate-apply --presentation P --answers FILE --judgements J [--out P2]` refuses the run without
`--judgements` (`judgements-missing`) or with judgements for another presentation (`judgements-foreign`), and accepts an
answer only when:

- every number in the prose is one of the finding's bound numbers (`unbound-number`);
- every plugin, cell label and form key it names is bound to the finding (`unbound-plugin`,
  `unknown-plugin`, `unbound-cell`, `unbound-formkey`, `unknown-formkey`);
- it names no check or arm outside the plan or outside the finding (`unknown-check`, `unbound-check`,
  `unknown-arm`, `unbound-arm` - naming the baseline instead of the finding's own arm is `unbound-arm`);
- it contains none of pass / compatible / clean / verified-in-game (`forbidden-word`);
- and then, a judgement for its id says `faithful`, made for its exact text (`unjudged`, `judgement-stale`,
  `judged-unfaithful`). A faithful judgement never overrides one of the rules above.

A refused answer keeps the template sentence and is listed under `narration.refused` with the rule; an
answers file for another presentation is `answers-foreign`. The plan is re-stamped (new `presentationId`)
because its sentences changed. Fidelity fixtures - correct, wrong count, wrong arm, invented plugin, invented
check, a completion claim - are machine-checkable; a wrong rewrite that keeps every bound number and name (a
regression told as a fix, a dropped "not") is caught only by the judge, which no lexical pattern replaces.

---

## 7. The narrated reel — `evalreel render`

`evalreport plan` writes the reel's plan into `presentation.reel` through `integration/housecarl/reel_plan.py`, and
`integration/housecarl/evalreel.py` cuts it. The plan's chapters, the stills, the vignette frames, the commands, the
cache identity and the output fields have one home:
[reel-layout.md](claude-plugin/skills/evaluation-reporting/references/reel-layout.md).

The reel is a tour of clash vignettes. An opening still lists the clash types the prediction counts and the
analysed plugins in load order; each selected clash is shown without the evaluated mod, with it and highlighted, at
one camera, or as a data still when there is no picture of it; when some check has no result, a closing still says
one sentence pointing to the report. The reel says no caveat; every gap and caveat is in the report. The narration is the skill's templates filled with typed names only, so no report
sentence, check name, internal term or disclaimer is spoken. Text appears only on the generated stills, which are
drawn in a named font; evidence frames carry none.

Narration is mandatory: prepared local speech or an explicitly chosen Windows voice, measured before scene timing is
final. A refused plan, missing or changed media, unmatched poses or absent narration refuses the render; no partial or
silent reel is a success. The workflow keeps source hashes, the media's setup and pose, the narration and its audio,
exact frame intervals, a separate transcript and output verification (decoded frame count, size, frame rate and audio).
A blind image review of each vignette's frames and a plain-language read of the narration are separate from that
check. The bundle's media admission rules are in `AI-EVALUATION.md` (supplied media).

---

## 8. Runs and coverage (D8)

`evalreport` opens `evalreport-plan` (features findings, groups, media, evidence), `evalreport-triage`
(features incidents and pairs; one counted catalogue compile), `evalreport-report`
(features findings, media, figures, pages; stages `render` and `print` with one attempt per ladder step),
`evalreport-verify` (feature links, pages) and `evalreport-narrate` (mode `packet` / `apply`); `evalreel`
opens `evalreel-render` with a counted `segments` stage and a `concat` stage (features segments,
sourceSeconds, outputSeconds, outputFrames, reused). Both are counted python rows in
SkyWright's `docs/run-coverage.json`. Set `HOUSECARL_RUNLOG_DIR` to the run's own store when a session's
timing is evidence.

---

## 9. Homes

Code `integration/housecarl/evalreport.py`, `integration/housecarl/evalreel.py`, templates
`integration/housecarl/evalreport_templates/` (report.md.j2, report.html.j2, evidence-index.md.j2,
evidence-index.html.j2, report.css);
skill `integration/housecarl/claude-plugin/skills/evaluation-reporting/` (SKILL.md, `integration/housecarl/claude-plugin/skills/evaluation-reporting/references/reel-layout.md`); data in
`integration/housecarl/reporting-data/` (`report-priorities.json`, `scene-policy.json`, `narration.json`; the review's
grouping keys are SkyWright's own `data/scene-policy.json`); tests `integration/housecarl/tests/test_evalreport.py`,
`integration/housecarl/tests/test_evalreel.py`, fixtures under `_geometry-harness/tests/fixtures/evalreport/`. Nothing instance-specific is tracked: a plan records the bundle
directory as given, and the report prints plugin names and form keys only as the bundle carries them.

---

## 10. Guarantees, and the limits

**Guaranteed.**

1. A bundle that does not verify is not compiled, and the refusal is named.
2. The same bundle and settings give the same plan, byte for byte apart from the timestamp.
3. Every factual number and name traces to a bundle field through readable evidence links and the index.
4. Evidence integrity and evaluation completion remain separate; technical integrity detail lives in the index.
5. A regression is never netted; an ESP-only arm and an unverified-in-game arm say so wherever the arm is introduced.
6. A page count is measured from the PDF and cross-checked, or it is not claimed; the font never shrinks.
7. A relocated OUT verifies identically.
8. Applied prose revisions are checked for unbound numbers/names; their meaning still needs evidence review.
9. A video uses qualified scene media and required narration, with no added on-screen text or silent-card fallback.
10. A result that was not measured for this evaluation says so beside itself, and a result with no recorded origin is
    never presented as one that was.

**Limits stated rather than worked around.**

- The concise report's page limit is a target the ladder tries to meet, not a promise. The default is **8 pages**
  (`report-priorities.json` `concise.maxPages`; `--max-pages N` overrides it).
  A rendition over the limit is still reported `over-limit` with the count at
  every step, and the least abridged content-preserving step is kept. `verify` never reports `ok` for it: an
  `over-limit` print status is a defect (kind `over-limit`) and verify exits refused.
- The validator's plugin and cell recognition is lexical: it refuses what it can recognise (a `.esp/.esm/.esl`
  token, a bound label, a check id, an arm id) and cannot catch a paraphrase that names nothing.
- Without a browser there is no PDF and no count; without ffmpeg, usable scenes or narration there is no reel.
  None is emulated by another kind of output.
- An import's origin catches evidence filed under the wrong evaluation by mistake. It is not authenticated
  provenance: the capture token and the timestamps are public and rewritable, and a `reused` import's source is the
  importer's caller's statement, which the compiler reports as declared and never verifies. A reused capture also
  keeps the candidate file name it was measured against rather than the destination setup's; the receipt's
  `consumed.configuration.source`, its `validation.identityLimits` row and the disposition's `capturedAgainst`
  carry it, and no reader presents such a result as measured for this setup.
- The compiler reads the emits' `Conflicts[].Necessary[]` rows; a probe output of another shape is presented
  as a count with its receipt, never as invented rows.
