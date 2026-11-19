# Known-answer case map: the Python suites and runcontrol's self-test

One row per case of `tests/test_evalpipeline.py` (56), `tests/test_workbench_manual_pipeline.py` (26) and
`tests/test_evalrun.py` (85).

Status:

- `ported`: a case of `runcontrol --self-test`, named in the last column. Where the Python case replaced a Python
  internal (the Runner, `Context`, `STAGE_BODIES`), the C# case uses the matching seam: `ChildLaunch.RunJob`,
  `RunContext.ResolveWith`, `Driver.Bodies`.
- `black-box`: the case runs the built program as a child process. For `test_evalrun.py` that is the Python suite
  itself, started with `EVALRUN_CONTROLLER_CMD` naming `runcontrol.exe`, so one case text judges both controllers.
- `waits for slice N`: its subject is not built yet. N is the slice of the port plan: 3 plan, 4 evaluate, 5 records,
  6 bundle (with scenes and finalize), 7 resume and reuse.
- `dropped`: not ported, with the reason.

The "kind" column is the Python case's: U a pure function, M a replaced internal, S a child process, P a real probe.

## tests/test_evalpipeline.py

| Case | Name | Kind | Status | C# case, or reason |
|---|---|---|---|---|
| 1 | one partner one key is five arms | U | ported | `case 1 one partner one key is five arms`; also `pipeline: derive_arms, arms and refusals` |
| 2 | two partners two keys is thirteen arms | U | ported | `case 2 two partners two keys is thirteen arms` |
| 3 | partner group is one unit | U | ported | `case 3 partner group is one unit` |
| 4 | key without partner master has no pair arm | U | ported | `case 4 key without partner master has no pair arm` |
| 5 | extra arm and order variant | U | ported | `case 5 extra arm and order variant` |
| 7 | order follows source with masters first | U | ported | `case 7 order follows source with masters first`; also `pipeline: order_arm, orders and refusals` |
| 9 | esm flagged esp loads before esps | U | ported | `case 9 esm flagged esp loads before esps` |
| 11 | late master refused | U | ported | `case 11 late master refused` |
| 13 | missing from source refused | U | ported | `case 13 missing from source refused` |
| 15 | reverse variant that breaks masters refused | U | ported | `case 15 reverse variant that breaks masters refused` |
| 16 | reverse variant swaps independent plugins | U | ported | `case 16 reverse variant swaps independent plugins` |
| 17 | scope accepted | U | ported | `case 17 scope accepted`; also `pipeline: accept_scope, receipts and refusals` |
| 19 | incomplete scope refused | U | ported | `case 19 incomplete scope refused` |
| 21 | provider or bytes swap refused | U | ported | `case 21 provider or bytes swap refused` |
| 23 | missing census cell refused | U | ported | `case 23 missing census cell refused` |
| 25 | empty target footprint refused | U | ported | `case 25 empty target footprint refused` |
| 27 | stage reuse by inputs | U | ported | `stage ledger: the scripted sequence runs`, `stage ledger: decisions` (Python's own decisions for a longer sequence) |
| 29 | exit zero without output not accepted | U | ported | `stage ledger: exit 0 with a missing output is refused` |
| 31 | precedent refusal does not block but keeps run incomplete | U | ported | `stage outcome and run status` |
| 33 | observations wait until banked | U | dropped | a stage after the bundle; those stay in the Python pipeline, and the Python case keeps judging them |
| 35 | observations must match this capture | U | dropped | as 33 |
| 37 | no frames is a stated refusal not a pass | U | dropped | as 33 |
| 39 | precedent script absent is a visible refusal | U | dropped | as 33 |
| 41 | unbuilt scene stage is a stated refusal | U | ported | `case 41 unbuilt scene stage is a stated refusal` |
| 43 | captured frames reach the media bundle | U | dropped | as 33 |
| 45 | reel transitions are decoded by index | U | dropped | as 33 |
| 47 | generations go to new numbered folders | U | dropped | as 33 |
| 48 | a capture the final plan dropped is superseded | U | dropped | as 33 |
| 49 | a finished request is reused, an unfinished one is not | U | dropped | as 33 |
| 50 | a transient refusal is captured again | U | dropped | as 33 |
| 51 | a round captures at most the cap | U | dropped | as 33 |
| 52 | a master named in another case is one plugin | U (files) | ported | `case 52 a master named in another case is one plugin` |
| 53 | a plugin the source does not load takes its named place | U (files) | ported | `case 53 a plugin the source does not load takes its named place` |
| 54 | a place before the plugin's own master is refused | U (files) | ported | `case 54 a place before the plugin's own master is refused` |
| 55 | a place after a plugin the source does not load is refused | U (files) | ported | `case 55 a place after a plugin the source does not load is refused` |
| 56 | a place for a plugin the source loads is refused | U (files) | ported | `case 56 a place for a plugin the source loads is refused` |
| 57 | the report stage plans with the capture closed | U | dropped | as 33 |
| 58 | the records stage keeps finished arms and scales its deadline | U (source text) | waits for slice 5 | reads the Python function's source text; to be re-expressed over the C# stage's child launch |
| 59 | the records stage takes the machine slot for its backstop | M | waits for slice 5 | |
| 60 | a heavy child holds the slot for its own hold minutes | M | ported | `case 60 a heavy child holds the slot for its own hold minutes` (the child launch is built now) |
| 61 | evaluate stops itself at its budget with the slot held that long | M | ported | `case 61 evaluate stops itself at its budget with the slot held that long` |
| 62 | a heavy child stopped from outside leaves no lock | S | ported | `case 62 a heavy child stopped from outside leaves no lock when its tree is confirmed gone, and its lock when it is not`, with the runner stood in; a real process tree stopped at its deadline is the child runner's own case in run-log-guard |
| 63 | the outer deadline sits past the slot command's own bounds | M | ported | `case 63 the outer deadline sits past the slot command's own bounds` |
| 64 | the review packet names no media bundle the report did not use | M | dropped | as 33 |
| 65 | a run with no frame stops for a person with the true reason | M | dropped | as 33 |
| 66 | capture holds the machine slot once across its rounds | M | dropped | as 33 |
| 67 | an evaluation stopped at its budget is resumed by the next run, not reused | M | ported | `case 67 an evaluation stopped at its budget is resumed by the next run, not reused`; also `evaluate: a stopped child is started again as resume while the ledger still moves, five times at most; a child that failed is not` |
| 68 | a stage after the bundle runs again when its tools change | M | dropped | as 33 (stage tools bind only stages after the bundle) |
| 2320 | each stage binds the presentation keys its code reads | U | dropped | the derivation from Python source is replaced by a declared table and an accessor that refuses an undeclared read; its counterparts are `entry: ...` (the table and the refusals) and `stages: census, prepare and scope run over an entry carrying every presentation key, and read none` |
| 2321 | a patch search change reruns report-plan and later, not the bundle | M | ported | `case 2321 a patch search change does not rerun the bundle; a mod name change does` (through bundle; what reruns from report-plan on is the Python pipeline's) |
| 2322 | a ledger from the earlier rule is reused and then follows the new one | M | dropped | the re-keying of receipts written before 02d9ea09 is not ported: it only spares reruns of run folders made before that commit |
| 2322_b | the earlier rule never reuses a stage after one that ran | M | dropped | as 2322 |
| 2323 | a refused stage keeps its accepted receipt so restoring the input restores it | M | ported | `case 2323 a refused stage keeps its accepted receipt so restoring the input restores it` |
| 2326 | a stage receipt records the bytes it added with each file once | M | ported | `case 2326 a stage receipt records the bytes it added with each file once` |
| 2327 | the disk forecast uses measured figures, names them and keeps the floor | M | ported | `case 2327 the disk forecast uses measured figures, names them and keeps the floor` |
| 2324 | a run through report-plan needs every partner pair searched before census | M | dropped | this controller refuses `--through report-plan` or later outright (`driver: a stage with no body stops the run as an unbuilt stage, and report-plan or later is refused`); the patch-search check stays with the Python pipeline that runs those stages |

## tests/test_workbench_manual_pipeline.py

| Case | Name | Kind | Status | C# case, or reason |
|---|---|---|---|---|
| b5 | in-place arms are the profile and the profile without the selected folders | U (files) | ported | `case b5 in-place arms are the profile and the profile without the selected folders` |
| d1 | a selected disabled mod is enabled in the candidate only, with its plugins last | U (files) | ported | `case d1 a selected disabled mod is enabled in the candidate only, with its plugins last` |
| r1 | an earlier preparation is reused only when every input matches | U (files) | ported | `case r1 an earlier preparation is reused only when every input matches`; also `pipeline: evalprepreuse.key over a folder tree` and `stages: a second run in the Work Folder takes the first run's preparation over, and a changed file does not` |
| s1 | the scope line names the cells, the other plugins and a disabled mod to tick | U | ported | `pipeline: scope_line, the sentences` (rows 1 to 4 are this case's four calls, with Python's whole sentences) |
| c8 | a plugin plugins.txt does not list is force-loaded and an unchecked one is not | U (files) | ported | `case c8 a plugin plugins.txt does not list is force-loaded and an unchecked one is not` |
| b4a | prepare writes no profile into the instance | U (files) | ported | `case b4a prepare writes no profile into the instance` |
| b7 | select is closed over the registry prerequisites and derived checks | M | ported | `case b7 select is closed over the registry prerequisites and derived checks` (the plan child's command line, the child stood in); the closure itself is `pipeline: in_place_select, the closure over a registry` |
| b5b | the plan names the review arm | M | ported | `case b5b the plan names the review arm` |
| b17 | the primitive table is explicit, never this machine's harness config | M | ported | `case b17 the primitive table is explicit, never this machine's harness config` |
| b6b | an asset-only selection has no target and a population of its folders only | M | ported | `case b6b an asset-only selection plans without a scope` (the plan child's command line), `case b6b an asset-only selection has no target` (the resolve half) and `stages: an asset-only selection skips the census and its scope line says it places nothing`; `arm_delta` is `plan: arm_delta, the names a set of orders differ by` |
| b6 | a selected master with an active dependent is refused naming it | S, P | black-box | `case b6 a selected master with an active dependent is refused naming it` (skipped, visibly, when scopeprobe is not built); the sentence is also `stages: a scope that stops on the order reader's refusal names it for the profile without the selection` |
| b15 | a candidate mod folder outside the instance is read by the probes | P | dropped | a test of scopeprobe itself, not of a controller |
| b16 | the review arm's pairs are each target with each plugin it clashes with | U (files) | ported | `finalize: review_pairs, all_plan_pairs, relevant_plugins`; the pairs on a command line are `generic row: patchprobe: the baseline gets every pair of the plan` |
| h1 | the scope's configuration is not stamped on a baseline arm the scope was not measured on | U | ported | `case h1 and h1b: consumed_for, what a child read as strongly as it was stated` (row "the baseline, the scope measured on another candidate") |
| h1b | a scope measured on the baseline candidate is still recorded verbatim | U | ported | `case h1 and h1b: consumed_for, what a child read as strongly as it was stated` (row "the baseline, the scope measured on its candidate") |
| m4a | a plan without a review arm keeps today's pairs | U | ported | `generic row: patchprobe: the baseline gets every pair of the plan` and `generic row: patchprobe: a candidate gets its own pairs` |
| m4b | with no geometry receipt the launcher gets a sentence naming the missing input | U | ported | `generic row: patchprobe: a review arm with no clash to take a pair from` |
| l1a | plugin_layers finds a plugin in an absolute view folder first | U (files) | dropped | `evalrun.plugin_layers` is Python's copy of the probes' layer map; the controller calls the map itself (`HarnessOrderCli.PluginLayerMap`), whose reading of such a folder is b15's subject |
| l1b | a population takes a mod folder named by absolute path | U (files) | ported | `plan: population_file, the file's bytes and each reason a delta cannot be scoped` (row `case l1b a population takes a mod folder named by absolute path`) |
| l4 | an inactive plugin stays out and a plugin the game Data also provides stays in | U (files) | ported | `case l4 an inactive plugin stays out and a plugin the game Data also provides stays in` |
| m3a | plan --review-arm is frozen and moves the baseline-only checks | S | black-box | `plan command: case m3a plan --review-arm is frozen and moves the baseline-only checks`: `runcontrol plan` as a child against `evalrun.py plan`'s whole plan for the same command line |
| m3b | without a review arm the plan is as today | S | black-box | `plan command: no instance: the scene preflight is not run and the population needs its folder` (the same command line without `--review-arm`) |
| m3c | a review arm the plan does not have is refused in one sentence | S | black-box | `plan command: case m3c a review arm the plan does not have is refused in one sentence` |
| b8i | finalize records discovery relevance for every accepted discovery receipt | U (files) | ported | `sweep and derived rows: candidate qualification over supplied files and discovery hits` (finalize's `discovery_relevance` over the same receipts, and over a plan with no target and no pair) |
| e2e1 | patchprobe finds the run instance outside its search roots | P | dropped | a test of patchprobe itself, not of a controller |
| c7 | a missing loadorder or plugins file is refused, not read as an empty order | U (files) | ported | `case c7 a missing loadorder or plugins file is refused, not read as an empty order` |

## tests/test_evalrun.py

Every case but the ones marked starts the controller as a child process. Run against `runcontrol` with

    EVALRUN_CONTROLLER_CMD=<path of runcontrol.exe> python _geometry-harness/tests/test_evalrun.py [case prefix]

(unset, the suite starts `evalrun.py`, as before). A case that goes on to `run`, `resume`, `import-results`,
`finalize`, `bundle` or `verify-bundle` waits for the slice that builds the last of them; until then those commands are
refused by name and the checks after them fail, so the count of passing checks is what `plan` alone satisfies.
`evalrun.py run` refuses a plan `runcontrol` froze (the controller identity differs), so the two cannot be mixed
inside one case.

| Case | Asserts | Commands | Status | On runcontrol now, or reason |
|---|---|---|---|---|
| 01 geometry-and-quest | plan, ledger, disposition and verify exit against the oracle | full + verify-bundle | black-box | passes on runcontrol (80 of 80 checks) |
| 02 select-three-probes | `--select` by exe name expands to ids and prerequisites | full | black-box | passes on runcontrol (53 of 53 checks) |
| 03 null-and-extraction | a completed null and an extraction failure read differently | full | black-box | passes on runcontrol (28 of 28 checks) |
| 04 blocked-and-budget | `--max-candidates`, `--exclude`, blocked rows | full | black-box | passes on runcontrol (52 of 52 checks) |
| 05 rejections | receipt rejections by name | full | black-box | passes on runcontrol (23 of 23 checks) |
| 06 attribution | created rows split by the reverse measurement | full | black-box | passes on runcontrol (23 of 23 checks) |
| 07 combined-regression | a regression is not cancelled by repairs | full | black-box | passes on runcontrol (30 of 30 checks) |
| 08 interrupt-resume | a killed run resumes without redoing accepted work | plan, run, resume | black-box | passes on runcontrol (11 of 11 checks) |
| 09 finalize-refusals | each finalize refusal by name | full | black-box | passes on runcontrol (9 of 9 checks) |
| 10 discovery-media | discovery probes and supplied media | full | black-box | passes on runcontrol (22 of 22 checks) |
| 11 bundle-relocate | a moved bundle verifies | full + verify-bundle | black-box | passes on runcontrol (7 of 7 checks) |
| 12 identity-binding | receipts bind the consumed configuration | plan, run | black-box | passes on runcontrol (63 of 63 checks) |
| 13 reuse-boundary | what a resume may reuse | plan, run, resume, finalize | black-box | passes on runcontrol (54 of 54 checks) |
| 14 evidence-chain | finalize and verify-bundle over a broken chain | full + verify-bundle | black-box | passes on runcontrol (56 of 56 checks) |
| 15 portable-incomplete | an incomplete bundle verifies after every source path is deleted | full + verify-bundle | black-box | passes on runcontrol (24 of 24 checks) |
| 16 supplied-identity | hash of a supplied plugin at plan time, wrong copy, changed bytes | plan, run, resume | black-box | 99 of 103 of its checks pass on runcontrol; the four that do not judge the rows Python lists for mod folders `modlist.txt` did not rank (`(not in modlist.txt)`): slice 3b's owner differences 1 and 2, red on runcontrol by design |
| 17 | a generic MCP import is identity-bound, evidence-backed, relocatable | + import-results | waits for slice 5 | needs `import-results`; 16 of 23 of its checks pass on runcontrol now |
| 18 | an arm's providers are its own; a plan under the old meaning is refused | + import-results | waits for slice 5 | needs `import-results`; 29 of 34 of its checks pass on runcontrol now |
| 19 | a derived check derives only over settled prerequisites | plan, run, finalize | black-box | passes on runcontrol (42 of 42 checks) |
| 21 | a sweep running at the deadline is stopped whole, nothing credited | full, resume | black-box | passes on runcontrol (47 of 47 checks) |
| 22 | a descendant that outlives its probe keeps the job unfinished | plan, finalize | black-box | passes on runcontrol (21 of 21 checks) |
| 23 | a child that cannot be contained never runs; cause named on both launch paths | plan, run, resume, finalize | dropped | makes Python's Runner refuse containment through a `sitecustomize.py` on PYTHONPATH, a hook runcontrol does not load; 16 of 37 of its checks pass on runcontrol. Its subject is judged by `sweep and derived rows: a sweep the runner would not start, and one it could not contain`, `sweep and derived rows: every way one candidate's parts end, one candidate per way` (reverse-uncontained) and `executor: account_stop, account and finish, each step's receipt, ledger entry and journal line` |
| 20 | a target the arm does not load is a legal omission (real scopeprobe) | plan, run, finalize; P | black-box | passes on runcontrol (87 of 87 checks) |
| 24 | resume after a stopped sweep adopts nothing the stopped launch recorded | plan, run, resume | black-box | passes on runcontrol (11 of 11 checks) |
| 25 | a stopped attempt's receipt has no exit code of its own | in-process `Executor.account_stop` + run; M | ported | `executor: account_stop, account and finish, each step's receipt, ledger entry and journal line` and `sweep and derived rows: every way one candidate's parts end, one candidate per way` (arm-stopped, diff-stopped, arm-not-launched). The Python case loads evalrun.py as a module; 25 of 28 of its checks pass on runcontrol |
| 26 | the ordinary prerequisite gate follows the derived gate's rule | plan, run, resume | ported | `evaluate: the prerequisite rule (decided_on_arm, prereq_answer_arms, prereq_block_detail, prereq_satisfied, reuse_blocked_by)` and `sweep and derived rows: arms the plan does not measure, an arm its prerequisite keeps out, a changed and a missing candidate file`. The Python case loads evalrun.py as a module; 37 of 55 of its checks pass on runcontrol |
| 27 | an unstopped sweep credits only what its child produced or verifiably reused | in-process, Runner job wrapped; M | ported | `sweep and derived rows: two probes measured and accepted; run again; then two parts repaired from the cache` and `sweep and derived rows: every way one candidate's parts end, one candidate per way`. The Python case loads evalrun.py as a module |
| 28 | ordersweep caches only an emit its own launch wrote (real ordersweep.py, four launches) | S (ordersweep.py) | ported | `sweep: Cache.lookup` and `sweep: Cache.record` (an emit is cached only when its own launch wrote it) and `sweep and derived rows: every way one candidate's parts end, one candidate per way` (diff-no-row). The Python case runs evalrun.py in the test process after a plan through the seam; 15 of 19 of its checks pass on runcontrol |
| 29 | `patch.order-evaluation` derives only from accepted diff parts | run, finalize; M | ported | `sweep and derived rows: every way one candidate's parts end, one candidate per way` (an order evaluation blocked by each diff that is not accepted) and `sweep and derived rows: two probes measured and accepted; run again; then two parts repaired from the cache`. The Python case loads evalrun.py as a module |
| 30 | a zero exit that wrote nothing credits nothing | run, resume | dropped | plans through the seam, then runs evalrun.py inside the test process with a wrapped Python Runner; on a plan runcontrol froze that is crossing controllers, refused by design (tool-changed), so 11 of 24 of its checks pass on runcontrol. Its subject is judged by `sweep and derived rows: every way one candidate's parts end, one candidate per way` (arm-refusal-marker: exit 0, no emit) |
| 31 | a copied RUN folder credits only evidence inside the copy | run, resume | dropped | plans through the seam, then runs evalrun.py inside the test process with a wrapped Python Runner; on a plan runcontrol froze that is crossing controllers, refused by design (tool-changed), so 1 of 7 of its checks pass on runcontrol. Its subject is judged by no C# case that moves a run folder: the split reads every path from the launch's own job records (sweep and derived rows: two probes measured and accepted; ...`), and a moved bundle is slice 6's black-box case 11 |
| 32 | resume repairs a failed reverse | run, resume | dropped | plans through the seam, then runs evalrun.py inside the test process with a wrapped Python Runner; on a plan runcontrol froze that is crossing controllers, refused by design (tool-changed), so 5 of 22 of its checks pass on runcontrol. Its subject is judged by `sweep and derived rows: two probes measured and accepted; run again; then two parts repaired from the cache` |
| 33 | a sweep job's own exit code and cause survive | run | dropped | plans through the seam, then runs evalrun.py inside the test process with a wrapped Python Runner; on a plan runcontrol froze that is crossing controllers, refused by design (tool-changed), so 12 of 41 of its checks pass on runcontrol. Its subject is judged by `sweep and derived rows: every way one candidate's parts end, one candidate per way` |
| 34 | a later launch never rewrites a kept diff part's evidence | run, resume, finalize | dropped | plans through the seam, then runs evalrun.py inside the test process with a wrapped Python Runner; on a plan runcontrol froze that is crossing controllers, refused by design (tool-changed), so 5 of 13 of its checks pass on runcontrol. Its subject is judged by `sweep and derived rows: two probes measured and accepted; run again; then two parts repaired from the cache` (the kept parts' receipts are compared byte for byte after the later launch) |
| 35 | verified reuse is preserved | full, resume | dropped | plans through the seam, then runs evalrun.py inside the test process with a wrapped Python Runner; on a plan runcontrol froze that is crossing controllers, refused by design (tool-changed), so 20 of 25 of its checks pass on runcontrol. Its subject is judged by `sweep and derived rows: two probes measured and accepted; run again; then two parts repaired from the cache` |
| 36 | `--max-candidates` bounds the sweep | run, finalize | dropped | plans through the seam, then runs evalrun.py inside the test process with a wrapped Python Runner; on a plan runcontrol froze that is crossing controllers, refused by design (tool-changed), so 17 of 27 of its checks pass on runcontrol. Its subject is judged by `sweep and derived rows: arms the plan does not measure, an arm its prerequisite keeps out, a changed and a missing candidate file` |
| 37 | candidate parts are credited only over an accepted baseline arm | run, finalize | dropped | plans through the seam, then runs evalrun.py inside the test process with a wrapped Python Runner; on a plan runcontrol froze that is crossing controllers, refused by design (tool-changed), so 18 of 37 of its checks pass on runcontrol. Its subject is judged by `sweep and derived rows: a baseline whose emit is not accepted` |
| 38 | a cached emit edited afterwards is invalidated; further R1 folds | plan, run, resume | dropped | plans through the seam, then runs evalrun.py inside the test process with a wrapped Python Runner; on a plan runcontrol froze that is crossing controllers, refused by design (tool-changed), so 13 of 19 of its checks pass on runcontrol. Its subject is judged by `sweep: Cache.lookup` and `sweep: Cache.record` and `sweep and derived rows: two probes measured and accepted; run again; then two parts repaired from the cache` |
| 39 | an uncontained sweep job reads blocked(uncontained), not stopped(deadline) | S | dropped | makes Python's Runner refuse containment through a `sitecustomize.py` on PYTHONPATH, a hook runcontrol does not load; 9 of 14 of its checks pass on runcontrol. Its subject is judged by `sweep and derived rows: every way one candidate's parts end, one candidate per way` (reverse-uncontained) |
| 40 | a conflictdiff refusal before a verdict is rejected by its exit code | run | dropped | plans through the seam, then runs evalrun.py inside the test process with a wrapped Python Runner; on a plan runcontrol froze that is crossing controllers, refused by design (tool-changed), so 5 of 45 of its checks pass on runcontrol. Its subject is judged by `sweep: conflictdiff_refusal, conflictdiff's own reason read from its job log` and `sweep and derived rows: every way one candidate's parts end, one candidate per way` (diff-refused, diff-usage) |
| 41 | after a repair, the classification word is the current one | run, resume, finalize | dropped | plans through the seam, then runs evalrun.py inside the test process with a wrapped Python Runner; on a plan runcontrol froze that is crossing controllers, refused by design (tool-changed), so 5 of 19 of its checks pass on runcontrol. Its subject is judged by `finalize: order_classification` and `sweep and derived rows: two probes measured and accepted; run again; then two parts repaired from the cache` |
| 42 | an import declares its origin; `fresh` must name this plan's arm | full + import-results, resume, verify-bundle | waits for slice 5 | needs `import-results`; 12 of 17 of its checks pass on runcontrol now |
| 43 | import-results consults the prerequisite gate | same | waits for slice 5 | needs `import-results`; 52 of 86 of its checks pass on runcontrol now |
| 44 | the candidate filename is provenance, not identity | same | waits for slice 5 | needs `import-results`; 18 of 20 of its checks pass on runcontrol now |
| 45 | conflictdiff refuses credit across emits without one measurement policy | run | dropped | plans through the seam, then runs evalrun.py inside the test process with a wrapped Python Runner; on a plan runcontrol froze that is crossing controllers, refused by design (tool-changed), so 1 of 10 of its checks pass on runcontrol. Its subject is judged by `sweep and derived rows: every way one candidate's parts end, one candidate per way` (diff-refused); the refusal itself is conflictdiff's own |
| 46 | the suite never tests a conflictdiff older than its source | the test file itself, in a copied checkout | dropped | judges the Python suite's own rule for building conflictdiff, in a copied checkout; no controller is started |
| 47 | a supplied plugin is `qualified` only by hash | in-process `run_derived`; U | ported | `sweep and derived rows: candidate qualification over supplied files and discovery hits`. The Python case calls `run_derived` in the test process |
| 61 | `enrich --kind record-rows` cites its bytes and changes no count | full + import-results, enrich, verify-bundle | waits for slice 5 | needs `import-results`; 8 of 13 of its checks pass on runcontrol now |
| 63 | `enrich --kind names` reads only files at the recorded hash | same | waits for slice 5 | needs `import-results`; 10 of 19 of its checks pass on runcontrol now |
| 65 | an import may carry a `rows` output; rows naming a plugin outside the arm are refused | full + import-results | waits for slice 5 | needs `import-results`; 6 of 10 of its checks pass on runcontrol now |
| 81 | a real scopeprobe refuses a corrupt LAND instead of crashing | P | dropped | a test of scopeprobe itself (a corrupt LAND is refused, not a crash), not of a controller |
| 82 | further corruption kinds refuse the same way | P | dropped | as 81 |
| 83 | on that scope the shipped registry answers unknown, never no | P + in-process `Validator` | ported | the rule (an incomplete scope answers unknown, never no) is `registry: applicability of every check on 25 scopes` (slice 3a); the probe half is scopeprobe's own test, as 81 |
| 49 | every relevant discovery hit becomes one qualification row | in-process `run_derived`; U | ported | `sweep and derived rows: candidate qualification over supplied files and discovery hits`. The Python case calls `run_derived` in the test process |
| 51 | the same end to end | plan, run, finalize | black-box | passes on runcontrol (5 of 5 checks) |
| 55 | the headline claim on a complete evaluation | in-process `build_disposition`; U | ported | `case 55 the headline claim on a complete evaluation` |
| 57 | three receipt refusals (`coverage-missing`, `mixed-...`, ...) | in-process `Validator`; U | ported | `validate: no Coverage`, `validate: one plugin at two hashes within an arm`, `validate: the first population judged nothing and withheld some` and the other `validate: ...` cases, each against evalrun.py's own Validator |
| 71 | a withheld edge is not counted as judged (real depprobe) | P + in-process `Validator` | ported | the Validator half is `validate: a catalogue that judged nothing and withheld some` and `validate: a catalogue's rows are evidence`; the real depprobe run is the probe's own test |
| 412 | `--population` goes to scoped probes only and keys their reuse | plan, run | black-box | passes on runcontrol (25 of 25 checks) |
| 413 | population = targets plus the arms' delta, frozen by hash | plan, run | black-box | passes on runcontrol (19 of 19 checks): the population is the targets plus the arms' delta, frozen by hash |
| 414 | an emit that judged another population is rejected | plan, run | black-box | passes on runcontrol (2 of 2 checks) |
| 415 | probes without the flag stay on the whole order and the plan says so | plan, run | black-box | passes on runcontrol (3 of 3 checks): probes without the flag stay on the whole order and the plan says so |
| 416 | a plan frozen before P25 resumes as before | plan, run | black-box | passes on runcontrol (7 of 7 checks) |
| 428 | a scoped probe-emit check alone receives the frozen file | plan, run | black-box | passes on runcontrol (9 of 9 checks): plan and run, a scoped probe-emit check alone receives the frozen file |
| 429 | a scoped probe-emit emit of another population is rejected | plan, run | black-box | passes on runcontrol (1 of 1 checks): plan and run, an emit of another population is rejected |
| 430 | registry and plan rules for a scoped probe-emit check | in-process; U | ported | `case 430 a race-carrying delta leaves the state check on the whole order, the others scoped`; the registry half is `registry: validate names every problem of 92 broken registries` (slice 3a) |
| 560 | bundle runs the names step by default and says so | full + import-results, verify-bundle | waits for slice 5 | needs `import-results`; 13 of 14 of its checks pass on runcontrol now |
| 561 | `--no-names` skips and says so; `enrich --kind names` adds it later | + enrich | waits for slice 5 | needs `import-results`; 13 of 16 of its checks pass on runcontrol now |
| 562 | a reader that cannot start: bundle still made, refusal stated | same | waits for slice 5 | needs `import-results`; 13 of 14 of its checks pass on runcontrol now |
| 563 | changed plugin bytes: names refused by name, bundle verifies | same | waits for slice 5 | needs `import-results`; 13 of 14 of its checks pass on runcontrol now |
| 564 | a partial read: status partial with counts | same | waits for slice 5 | needs `import-results`; 14 of 15 of its checks pass on runcontrol now |
| 565 | verify-bundle checks the statement against the files | same | waits for slice 5 | needs `import-results`; 13 of 14 of its checks pass on runcontrol now |
| 566 | reader default order: flag, env, checkout build | in-process `default_reader_cmd`; U | ported | `case 566 the reader's default order: the flag, the environment, the checkout's build` |
| 590 | a rebundle after the registry changed is refused before writing | full + verify-bundle | black-box | passes on runcontrol (3 of 3 checks) |
| 591 | naming the frozen registry rebundles from another folder | same | black-box | passes on runcontrol (4 of 4 checks) |
| 592 | a named file that is not what the plan froze is refused | full | black-box | passes on runcontrol (9 of 9 checks) |
| 593 | an older plan with relative registry and scope paths | full + verify-bundle | black-box | passes on runcontrol (7 of 7 checks) |
| 800 | `enrich --kind order-variant` derives or refuses by rule | full + import-results, enrich, verify-bundle | waits for slice 5 | needs `import-results`; 7 of 9 of its checks pass on runcontrol now |
| 801 | every bundle states the pair's order status | full + import-results | waits for slice 5 | needs `import-results`; 6 of 7 of its checks pass on runcontrol now |
| 1730 | the harness config's table is frozen with its sha256 and passed | plan | black-box | passes on runcontrol (3 of 3 checks), its last check the stateprobe launch by `run`; the plan half is also `plan command: case 1730 the harness config's table is frozen with its sha256 and passed` |
| 1731 | no table: said in the plan; the check runs without the flag | plan | black-box | passes on runcontrol (4 of 4 checks); the plan half is also `plan command: case 1731 a harness config that names no table: said in the plan` |
| 1732 | a named table that is absent refuses the plan | plan | black-box | passes on runcontrol (1 of 1 checks); also `plan command: case 1732 a named table that is absent refuses the plan` |
| 1733 | the plan's copy changed afterwards: the state check is blocked | plan (+ run) | black-box | passes on runcontrol (1 of 1 checks): the plan's copy changed afterwards, and `run` blocks the state check; also `generic row: state check: a primitive table that changed` |
| 2328 | the plan freezes `candidates.json`'s sha256 and the bundle carries it | full | black-box | passes on runcontrol (4 of 4 checks) |
| 2329 | a changed `candidates.json` refuses the bundle; a changed copy fails verify | full + verify-bundle | black-box | passes on runcontrol (2 of 2 checks) |

## Totals

| Suite | Cases | ported | black-box | waits | dropped |
|---|---|---|---|---|---|
| test_evalpipeline.py | 56 | 34 | 0 | 2 | 20 |
| test_workbench_manual_pipeline.py | 26 | 19 | 4 | 0 | 3 |
| test_evalrun.py | 85 | 13 | 39 | 16 | 17 |
| All | 167 | 66 | 43 | 18 | 40 |

Cases of the self-test with no Python case behind them: the generated tables (`pipeline: ...`, `plan: ...`), the
`plan command: ...` cases without a case number (`runcontrol plan` as a child process against what `evalrun.py plan`
prints and writes for the same command line), the cases named `owner: ...` (one per place where the controller follows
a probe reader and Python's copy answers otherwise), the `driver: ...`, `child: ...`, `stages: ...`, `lab: ...`,
`prep reuse: ...` and `black box: ...` cases, and the live cases against a Python on PATH.
