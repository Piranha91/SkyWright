# Known-answer case map: the Python suites and runcontrol's self-test

One row per case of `tests/test_evalpipeline.py` (56) and `tests/test_workbench_manual_pipeline.py` (26).
`test_evalrun.py` is mapped when its subjects are ported.

Status:

- `ported`: a case of `runcontrol --self-test`, named in the last column. Where the Python case replaced a Python
  internal (the Runner, `Context`, `STAGE_BODIES`), the C# case uses the matching seam: `ChildLaunch.RunJob`,
  `RunContext.ResolveWith`, `Driver.Bodies`.
- `black-box`: the case runs the built program as a child process.
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
| 41 | unbuilt scene stage is a stated refusal | U | waits for slice 6 | the scenes body; the driver's handling of such a refusal is `driver: a non-blocking refusal lets the run go on and keeps it incomplete; an awaited judgement stops it` |
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
| 61 | evaluate stops itself at its budget with the slot held that long | M | waits for slice 4 | |
| 62 | a heavy child stopped from outside leaves no lock | S | ported | `case 62 a heavy child stopped from outside leaves no lock when its tree is confirmed gone, and its lock when it is not`, with the runner stood in; a real process tree stopped at its deadline is the child runner's own case in run-log-guard |
| 63 | the outer deadline sits past the slot command's own bounds | M | ported | `case 63 the outer deadline sits past the slot command's own bounds` |
| 64 | the review packet names no media bundle the report did not use | M | dropped | as 33 |
| 65 | a run with no frame stops for a person with the true reason | M | dropped | as 33 |
| 66 | capture holds the machine slot once across its rounds | M | dropped | as 33 |
| 67 | an evaluation stopped at its budget is resumed by the next run, not reused | M | waits for slice 4 | the driver's budget rule (`BudgetDiffers`, `budgetInForce`) is built; its case needs the evaluate stage |
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
| b7 | select is closed over the registry prerequisites and derived checks | M | waits for slice 3 | the plan stage's argv; the closure itself is `pipeline: in_place_select, the closure over a registry` |
| b5b | the plan names the review arm | M | waits for slice 3 | |
| b17 | the primitive table is explicit, never this machine's harness config | M | waits for slice 3 | |
| b6b | an asset-only selection has no target and a population of its folders only | M | waits for slice 3 | the plan stage's argv; the resolve half is `case b6b an asset-only selection has no target`, and the stages are `stages: an asset-only selection skips the census and its scope line says it places nothing` |
| b6 | a selected master with an active dependent is refused naming it | S, P | black-box | `case b6 a selected master with an active dependent is refused naming it` (skipped, visibly, when scopeprobe is not built); the sentence is also `stages: a scope that stops on the order reader's refusal names it for the profile without the selection` |
| b15 | a candidate mod folder outside the instance is read by the probes | P | dropped | a test of scopeprobe itself, not of a controller |
| b16 | the review arm's pairs are each target with each plugin it clashes with | U (files) | waits for slice 4 | |
| h1 | the scope's configuration is not stamped on a baseline arm the scope was not measured on | U | waits for slice 4 | the executor |
| h1b | a scope measured on the baseline candidate is still recorded verbatim | U | waits for slice 4 | the executor |
| m4a | a plan without a review arm keeps today's pairs | U | waits for slice 4 | |
| m4b | with no geometry receipt the launcher gets a sentence naming the missing input | U | waits for slice 4 | |
| l1a | plugin_layers finds a plugin in an absolute view folder first | U (files) | dropped | `evalrun.plugin_layers` is Python's copy of the probes' layer map; the controller calls the map itself (`HarnessOrderCli.PluginLayerMap`), whose reading of such a folder is b15's subject |
| l1b | a population takes a mod folder named by absolute path | U (files) | waits for slice 3 | |
| l4 | an inactive plugin stays out and a plugin the game Data also provides stays in | U (files) | ported | `case l4 an inactive plugin stays out and a plugin the game Data also provides stays in` |
| m3a | plan --review-arm is frozen and moves the baseline-only checks | S | waits for slice 3 | black-box against `runcontrol plan` |
| m3b | without a review arm the plan is as today | S | waits for slice 3 | black-box against `runcontrol plan` |
| m3c | a review arm the plan does not have is refused in one sentence | S | waits for slice 3 | black-box against `runcontrol plan` |
| b8i | finalize records discovery relevance for every accepted discovery receipt | U (files) | waits for slice 6 | |
| e2e1 | patchprobe finds the run instance outside its search roots | P | dropped | a test of patchprobe itself, not of a controller |
| c7 | a missing loadorder or plugins file is refused, not read as an empty order | U (files) | ported | `case c7 a missing loadorder or plugins file is refused, not read as an empty order` |

## Totals

| Suite | Cases | ported | black-box | waits | dropped |
|---|---|---|---|---|---|
| test_evalpipeline.py | 56 | 31 | 0 | 5 | 20 |
| test_workbench_manual_pipeline.py | 26 | 8 | 1 | 14 | 3 |
| Both | 82 | 39 | 1 | 19 | 23 |

Cases of the self-test with no Python case behind them: the generated tables (`pipeline: ...`), the cases named
`owner: ...` (one per place where the controller follows a probe reader and Python's copy answers otherwise), the
`driver: ...`, `child: ...`, `stages: ...`, `lab: ...`, `prep reuse: ...` and `black box: ...` cases, and the live
cases against a Python on PATH.
