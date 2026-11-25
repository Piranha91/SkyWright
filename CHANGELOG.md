# Changelog

Changes to SkyWright that a modder would notice. One bold sentence saying what the tool now does or refuses, and at
most one more saying how to check it.

## Unreleased

- **runcontrol's plan reads no evaluation settings file and looks for no agent files: a model, report, video or narration setting, or an agents folder beside the program, no longer changes a scan's plan or stops it, and `plan --settings` is refused in one sentence; the measuring choices come by flag or by the entry (`evaluation.reverseComparison`, `evaluation.optIn`).** Run `runcontrol --self-test` (the `identity I1`-`I8` cases). A run folder made before this change is read as it is when finished (its bundle verifies); an unfinished one is planned again in a new folder, as after any runcontrol update.
- **An AI-driven evaluation of a lab entry is run by `integration/housecarl/evalpipeline.py run` alone: it folds the evaluation settings' reverse comparison and live check into `RUN/measuring-entry.json`, records the settings in `RUN/evaluation-settings.json`, runs runcontrol through evaluate, captures the records with houseCARL's reader, and runs runcontrol on to the bundle; runcontrol's records stage starts no child and lists the record checks left open.** Run `integration/housecarl/tests/test_record_capture.py` and `test_measuring_entry.py`. A lab run folder taken through the bundle before this change keeps its measuring stages when the settings are the defaults; the module's stages that bind runcontrol's build (report-plan, capture, report) run again; with reverse comparison off or the live check always in the settings, census on are redone.
- **The review's grouping (which rows are one problem) is SkyWright's own `data\scene-policy.json`, copied to `Data\`; the report's words and scene policy and the model agents are the integration module's own (`integration/housecarl/reporting-data/`, `model-agents/`), and nothing reads them from houseCARL's skills any more.** Run `SkyWright.Guards.exe fence`.
- **A whole-order pass (`orderpass.py`) leaves its records stage to the integration module's `orderpass_records.py`, which reads it over the finished pass folder and builds the leads again; until then the leads name it unassessed with that reason.** Run `integration/housecarl/tests/test_orderpass_records.py`.
- **SkyWright's build holds no houseCARL program: `Tools\housecarl-mcp\` is gone (a rebuild removes an older one), and houseCARL's server program, for the AI-facing tools, is built into the integration module's `integration\housecarl\bin\<Configuration>\Tools\housecarl-mcp\`.** Run `BuildLayoutTests` in `workbench.tests` and `integration/housecarl/tests/test_navmesh_repair_write.py`.
- **`evalrun.py` refuses the names step in one sentence when no reader is named (`--reader-cmd` or `HOUSECARL_RECORD_READER_CMD`) instead of starting a server from the build; `runcontrol` reads the names in process.** Run `_geometry-harness/tests/test_evalrun.py case-566`.
- **SkyWright saves, undoes and repairs a navmesh, reads placements and a reference's context, and reads a run's names with houseCARL's code compiled in: it starts no houseCARL program, and its endpoint serves `workbench_state` and `workbench_act` only (the forwarded `housecarl_*` tools are gone).** Run `NoHousecarlProgramTests` in `workbench.tests`.
- **A houseCARL refusal reaches you as one sentence with houseCARL's own reason: an undo it refused no longer reads as removed, and a refused navmesh-repair write stops the repair at once.** Run `HousecarlAnswerTests` in `workbench.tests`.
- **A read of more than 1 MiB of records is read whole: the server path broke such an answer with a line break every 1 MiB, so a long decision or context read could fail or read a wrong value.** It went with the server path.
- **A navmesh repair after you switch profiles is written under the new profile's Output Folder (it went through the first profile's houseCARL before).** Run `HousecarlWritersTests` in `workbench.tests`.
- **A stored decision now names the houseCARL build that read it by that build's code, so it says "the houseCARL reader changed" once after an update; the old name was the server's launcher, the same for every build.** Run `InProcessStrictTests` in `workbench.tests`.
- **houseCARL's rulebook is `Data\corpus.json` in the build.**
- **The report, reel, narration and capture tools, the in-game switch flips and `lookprobe` now live in the houseCARL integration module, `integration/housecarl/`, which builds to its own folder and which nothing in the app, the probes or the run controller refers into.** Run `bin\Release\net9.0-windows10.0.19041.0\Tools\SkyWright.Guards\SkyWright.Guards.exe fence`; an evaluation run folder made before this change re-runs its stages after the bundle, because their tools' recorded paths moved.

## From houseCARL, before the split

These entries were in houseCARL's unreleased changelog (`plugin/CHANGELOG.md`) when the probes, the workbench, the
run controller and the evaluation tools moved to SkyWright (split M3, 2026-11-24). They are kept as written, so
"Fork:" means houseCARL's fork and paths are as they were in houseCARL (`scripts/planeassess/` is now
`_geometry-harness/planeassess/`).

- **Fork: the Workbench's World tab draws a map of a worldspace from the load order (terrain, building outlines, and place names that appear as you zoom in), and opens a cell that holds only terrain.** Click a square of the map to open its cell.
- **Fork: the Workbench's Open control takes a finished evaluation's results, an evaluation bundle, an MO2 instance or profile, or a game Data folder, and refuses anything else in one sentence.** Results filters what the patches addressed, did not address and changed unpredicted; World browses cells and objects as a tree.
- **Fork: evaluation bundles are checked only by `runcontrol verify-bundle`: the report and `evalrun enrich` start it, and `evalrun.py verify-bundle` is gone.** Run `_geometry-harness/tests/test_evalreport_verify_in_place.py`.
- **Fork: census to bundle are run only by the built-in controller, `runcontrol`: the Workbench's Run engine setting is gone, and `evalpipeline run` continues a run folder from its bundle, refusing in one sentence one that runcontrol has not carried that far.** Run `_geometry-harness/tests/test_evalpipeline.py`.
- **Fork: the Workbench previews and writes a navmesh row's repair: a broken border link is relinked to the one edge that meets it (two that meet are refused by name, none is cut and said), every link written both ways, and navmesh left on moved ground follows it in height with its box recomputed; the patch goes to a new plugin under the Output Folder, is read back against the winner, each cell and worldspace it carries is held to its own winner (one the write copied from the navmesh's plugin is forwarded from its winner first), and it is re-checked. A relink that joins two navmeshes names each in the other's NAVI entry (the entry of the last plugin that states it, added whole when the NAVI winner lacks it); the read-back holds every other NAVI entry to the winner's, and a relink whose entry no plugin states is refused by name. The repair acts on a navmesh row of the Findings tab, which opens no scene (`Preview repair` and `Write repair` there; `repair_preview` and `repair_write` with `finding`). A link off by 8-16 u in plan or 63-128 u in height is reported apart as unproven in game, not as a finding. An EDGE-NAV row now names its source and target triangle, one row per link.** Run `_geometry-harness/tests/test_navmesh_repair_rules.py`.
- **Fork: `navownprobe` reports navmesh border links that do not meet or do not link back, links into a deleted navmesh, a mod's deleted or emptied navmesh, and navmesh left on ground a later plugin moved.** Run `_geometry-harness/tests/test_navmesh_repair_rules.py`.
- **Fork: the evaluation reel and report picture a clash that appears only with the supplied patches loaded, with them loaded beside the setup without them, and say so; a world-state check's new clashes are counted once ("5 clashes in …"); a camera standing on bare terrain says its slope in the caption and reel note; world-state pictures are requested again (a row's kind kept as written).** Run `_geometry-harness/tests/test_patch_clash_pictures.py`.
- **Fork: `evalpipeline run` refuses before census when the entry's patch search does not cover the subject with every partner, naming every missing pair; the reel plan's refusal names every uncovered pair at once.** Run `_geometry-harness/tests/test_evalpipeline.py`.
- **Fork: `evalpipeline run` reruns only the stages that read a changed entry key: a patch search reruns report-plan and later, not the bundle; a refused stage keeps its accepted receipt, so restoring the input restores it.** Run `_geometry-harness/tests/test_evalpipeline.py`.
- **Fork: `evalpipeline run` forecasts the disk the remaining stages need from measured per-record figures, names where each was measured, and refuses when the volume would fall below the slot's floor.** Run `_geometry-harness/tests/test_evalpipeline.py`.
- **Fork: `evalrun plan` freezes the candidate search's hash or says it is absent; `evalrun bundle` copies it only when it matches and `verify-bundle` checks the copy.** Run `_geometry-harness/tests/test_evalrun.py case-232`.
- **Fork: a report joins a separate evaluation whose rows spell a plugin name in another case (a Creation Club file named as on disk), since the game reads plugin names without regard to case.** Run `_geometry-harness/tests/test_evalreport_siblings.py`.
- **Fork: the evaluation report and reel say "Interesting NPCs'" rather than "NPCs's", a landscape record as the ground of its cell, and a long plugin file name in full once, then by a short form.** Run `_geometry-harness/tests/test_report_words.py`.
- **Fork: viewprobe's solved routes are taut through the navmesh: a path no longer bends at a vertex the mesh surrounds (the leg line says when it was re-routed and how much shorter), never lists a corner twice, and a route station is tested on the storey of the path it stands on, not a deck above it.** Run `_geometry-harness/tests/test_route_geometry.py`; the NavGraph and station cases are `NavGraphFunnelTests` and proofprobe's RT18.
- **Fork: viewprobe's `--route-solve` accepts a solved leg down a walked ledge, counting samples over the ledge's gap as in the air; a leg that really leaves the mesh is still refused.** Run `_geometry-harness/tests/test_route_solve_ledge_drop.py`.
- **Fork: the capture camera's check in the setup without the mod addresses the cell by its form key, as the capture does, and a setup without the plugin that defines the cell is refused as a place only the mod has.** Run `_geometry-harness/tests/test_capture_every_state.py`.
- **Fork: the workbench Start tab is dark, shows probes as named tiles with switches, runs or aborts one probe without stopping the others, shows real progress and an ETA, and remembers its instance, profile and Output Folder.** Run `dotnet test _geometry-harness/workbench.tests`.
- **Fork: the conflict review workbench groups conflicts into scenes by cell, shows what a move leaves behind (doors, linked objects, resting objects, navmesh), and finds clear positions it previews and judges by re-checking each as a trial patch.** Run `dotnet test _geometry-harness/workbench.tests`.
- **Fork: the conflict review workbench shows originals while Space is held, draws a ghost, hides or tints for the view only, snaps and drops moves, lists edits with revert, keeps the session, and flags decisions when objects change.** Run `dotnet test _geometry-harness/workbench.tests`.
- **Fork: a navmesh vignette's earlier-version caption names the plugin its frame draws, says which mod replaced that version, and no longer writes "NPCs's".** Run `_geometry-harness/tests/test_navmesh_caption.py`.
- **Fork: `evalreport plan` verifies each bundle it reads in place, writing nothing into it and copying nothing; the verdict goes to OUT/verify.json.** Run `_geometry-harness/tests/test_evalreport_verify_in_place.py`.
- **Fork: the report and reel say records in words, not form keys; What to do says each check once across mod pairs; a long mod name is said in full once, then by its own short form.** Run `_geometry-harness/tests/test_report_record_labels.py`.
- **Fork: a vignette's capture camera must pass the same checks in the setup without the mod as in the setup it was measured in; when no pose does, the capture is refused, naming the setup and the object in the way.** Run `_geometry-harness/tests/test_capture_every_state.py`.
- **Fork: on a houseCARL 2.0 capture the record lane lists every field it cannot judge (a read cut off at its line limit, a failed read, a tree answer that could not compare a plugin in full) with its reason, instead of leaving it out.** Run `_geometry-harness/tests/test_record_2_0_unjudged.py`.
- **Fork: the review reel says and shows a declared mod name or plugin file name as written, and its not-pictured line leaves out a count of 0.** Run `_geometry-harness/tests/test_reel_names.py`.
- **Fork: the record lane no longer calls a field unchanged when a read printed it without its values (a height grid entry shown only as its type name, a list only as its count); the report lists it among the fields that could not be judged.** Run `_geometry-harness/tests/test_record_unexpanded_leaves.py`.
- **Fork: an evaluation keeps each record-reader transcript once: `evalrun import-results` and `evalrun bundle` hard-link it where the run is on one volume, and copy it otherwise.** Run `_geometry-harness/tests/test_transcript_links.py`.
- **Fork: `narrate-apply` accepts a rewrite that quotes one of its finding's own rows, with the form keys, plugins and cells written inside it; a plugin the plan does not name, quoted on its own outside a row, is still refused.** Run `_geometry-harness/tests/test_narration_fidelity.py`.
- **Fork: the review reel and report say why a clash has no picture (no camera for that kind, or the capture limit), say each patch fact one way, and list each Nexus patch page once in What to do.** Run `_geometry-harness/tests/test_report_reel_wording.py`.
- **Fork: places in the report and on the reel's still pages are said as names (a cell's name, its worldspace and grid cell, or its editor ID in words), never as a form key or a bare editor ID; a one-mark view names the other object too.** Run `_geometry-harness/tests/test_report_reel_wording.py`.
- **Fork: evalpipeline resolves --entry, --machine and --out to absolute paths when it parses them.** Run `_geometry-harness/tests/test_report_reel_wording.py`.
- **Fork: the narration fidelity judge now sees every row a finding counts, so it can check a rewrite's detail against them, and the narrator is no longer told never to mention completion: it keeps "the check ran to completion" and still may not call the evaluation complete or the mods compatible.** Run `_geometry-harness/tests/test_narration_fidelity.py`.
- **Fork: `narrate-apply` accepts a rewritten finding sentence only where the fidelity judge (`models.checkExplanations`, never a Fable model) says it keeps the meaning; the narration packet asks for plain words and carries a glossary.** Run `_geometry-harness/tests/test_narration_fidelity.py`.
- **Fork: viewprobe's `--route-solve` now prints the ledge, enable/disable, unknown-type and held link counts, and a no-path refusal names the held links; the `--stations` navdump writes each edge link's type.**
- **Fork: outdoors, where no navmesh stands a capture camera, the capture point picker and the shot planner may stand it on bare terrain whose footprint is planar and at most 43.7° (the measured standing limit), with nothing beside, over or under it but the ground; the station says so ("terrain, slope X°, measured limit 43.7°"), though reports and reels do not show that label yet.**
- **Fork: the review reel pictures a navmesh clash where its camera can draw it: the winning navmeshes with the broken links in red, then the earlier version at the same camera, and says whether the setup without the mod has the same broken links.**
- **Fork: a navmesh comparison capture is refused unless its control, the setup without the mod at the same camera, finds the links whole; the pipeline runs that control before every navmesh capture.**
- **Fork: `evalrun plan` no longer refuses a plan over meshes only a scene capture would need when no planned check captures scenes; with the live switch check opted in, that refusal says whether the check's profile rule would accept the suggested fix.**
- **Fork: the flip-plan draft never sends an exterior switch path to an interior arrival, and each live switch launch records where the player arrived.**
- **Fork: viewprobe's `--nav-at`, `--clear-at`, `--inside-at` and `--refs-at` now refuse in one sentence instead of printing nothing, when the run has no camera (`--eye` or `--eye-ref`) or a value is not the query's numbers.**
- **Fork: a clash a plant drawn with see-through cards hides from the final camera is now pictured from the station camera that saw it, captioned as a station view.**
- **Fork: the review reel never says "No camera could see the clash" for a clash a camera saw; each capture refusal has its own reason, and a test fails on any refusal code without one.**
- **Fork: the review reel never speaks or subtitles a form key or an editor ID, and never says "overlap candidate".**
- **Fork: capture requests match a report's pictures by the clash's whole pair, and compare plugin names without case.**
- **Fork: a report's media bundle uses the evaluation's own frozen copies of every input the plan pins by hash, so a moved or deleted original no longer stops it.**
- **Fork: contactprobe's `--anchor`, `--surface` and `--volume` files and every motion object name the solid rule their answers depend on (`solidPolicy`), or "none" when no solid was checked.**
- **Fork: motionprobe and contactprobe now say in one sentence what to try when a swept sweep or review runs out of memory: a smaller sample budget, a coarser step, or more free memory.**
- **Fork: fomodprobe lists its hits in the same order on every run.**
- **Fork:** the review reel's example of a kind of clash is its strongest crossing (the most crossing triangle pairs), not the first one listed; a clash the capture found buried is said as buried even when another clash of its group is pictured.
- **Fork:** a buried clash whose partner is see-through (a cloud, a glow) is no longer said to be buried inside that partner, and the reel's reason for buried clashes in the report says how many camera spots they rest on.
- **Fork:** capture cameras take their storey from the navmesh rather than from a floor high up a tall object, and a flat effect partner no longer hides the floor under the object it crosses.
- **Fork: reports no longer count a record-flags difference that is only the compressed-storage bit, or one flag change twice under its two names.** This applies when they read evaluations captured before the record stage masked them.
- **Fork: the report and the review reel now name each mod the same way, and a patch with no Nexus name is said by its own plugin name.**
- **Fork: a patch the Nexus search found inside a patch-collection file is now named with that file, and the reel never says "no patch exists" from a search that does not record what it left unread.**
- **Fork: new `patchsearch.py` records a Nexus patch search with the archive listings it did not read, the paths a hub file matched by, and the server build.**
- **Fork: the author-advice check now counts the clashes an author's page states beside its load-order rules.** This is check registry version 4.
- **Fork: the review reel now shows media from every sibling evaluation joined to a report, not only the first.**
- **Fork: reports say places to look by their in-game name, or by worldspace and grid**, instead of an editor ID or a form key.
- **Fork: reports tell you to forward the losing mod's value where the winning mod only keeps an earlier value**, and to rebuild distant scenery and occlusion when the mod changes exterior cells.
- **Fork: reports say which of a patch's own placements cross more with the patch than without it.**
- **Fork: report PDFs link the evidence index by a relative path**, so the links work after the folder moves.
- **Fork: figure captions say when a picture is an offline render**, not a screenshot from the game.
- **Fork: the reel no longer puts one patch's search lines on another pair's clashes**, and says "1 case".
- **Fork:** When neither of two evaluated mods has to load first, the report now also checks placed objects, overlaps and navmesh in both orders, and says any problem that appears in only one order as a load-order risk.
- **Fork: an evaluation report counts a broken navmesh link that appears only with both mods as a clash between them**, not as one mod's break of the base game. Its evidence index lists such a link as admitted, needing both mods.
- **Fork: stateprobe names the base of each placed hazard and trap it leaves unjudged**, in its console and `--json` output; placeprobe shows each placed arm's base link.
- viewprobe's `--inside-at`, `--clear-at` and planes report no longer skip every mesh under an `effects\` folder. Each shape is judged by its own data. Lit solids there (frozen statues, ice sheets, web sacs, ore veins) now count. Hidden shapes and lit refraction (heat-haze) shapes on any mesh no longer count.
- World-state checks now use the lab's collision-box measurements when the evaluation finds them (set `primitiveSolidity` in your harness config): an invisible collision box that a quest brings in across a walkway is reported as a possible blocked route. Characters a quest brings in or takes away are counted on their own line, with the ones placed cleanly on the navmesh and floor judged from where they are placed; none is reported as a clash yet. Evaluations without the measurements say so.
- **Fork: with several evaluated mods, a clash is said to need them all only where each mod's own setup shows it.** Where the setup with one mod alone also leaves out another plugin, or a third evaluated mod, the report now says which of them it needs is not separated, and names that setup.
- **Fork: overlap triage turns a reference's local offset the way the engine does.** Earlier versions mirrored it for any rotated reference. No verdict or rank changed on the retained evaluations.
- **Fork: the review reel says no caveats; it ends with one sentence pointing to the report, and only when a check has no result.** Every gap and caveat it used to say is in the report.
- **Fork: a world-state clash is pictured even when the switch belongs to the mod under test.** The picture without that mod used to ask the renderer for a switch that order cannot hold, so the whole clash went unpictured; the without frame now shows that world as it loads. A failed frame's refusal now quotes the renderer's own sentence.
- **Fork: an evaluation run's stages after the evidence bundle run again when the code of a tool they run changes, so a capture, report or reel is never reused across a tool change.** After this change each of those stages runs once in an existing run folder; capture still reuses each request whose tools are unchanged.
- **Fork: the review reel names each mod by its Nexus page name instead of its MO2 folder name, with no hand-declared names needed.** The name is read from the local Nexus catalogue through the mod's id (its scope footprint or MO2's `meta.ini`), and one page the catalogue lacks is fetched; the evidence index says where each name came from, and a mod with no Nexus id keeps its folder name with the reason.
- **Fork: a clash where one object is buried, so no camera at a player's eye height around it can see it and the sliced view from above cannot show the pair, is now said as buried in the report and the review reel, instead of "no camera could see".** The report line gives the number of camera spots it rests on. A sliced top-down view now hides what covers the pair from above when nothing rests on it.
- **A report can join several separate evaluations of the same order (`evalreport plan --sibling-bundle` given once per evaluation).** A mod evaluated against a block of neighbours as several small evaluations, one per comparison, now gets one report: a setup with the mod alone measured in another evaluation answers whether a clash with a neighbour needs that neighbour, instead of "was not measured". At least one sibling must hold the setup without the patch; the others may hold it with plugins left out. A sibling that cannot be used is refused by name, and the report names which evaluation each borrowed setup came from.
- **Fork: evaluation reports say whether the two mods' load order is a choice and, when it is, weigh both orders: the record fields that change hands and whether the patch still needs one order.** `evalrun enrich --kind order-variant` derives the reversed order's rows.
- **Fork: record lost-edit rows judge a plugin's change against the master lists the evaluation recorded, so a game file updated later no longer hides them, and every field a record check could not judge is counted in the report.**
- **Fork: an author's same-named compatible plugin can be declared as the patch by its sha256 or its mod folder, so the report compares it as a patch without marking the original file of that name.** `--declare-patch PLUGIN=sha256:<hex>` or `PLUGIN=folder:<folder>`, or `{name, sha256, folder}` in the plan's `patches` or a setup's provenance; a declaration that matches no setup's file is refused, and a name alone that stands for two different files is said so instead of being read as the patch.
- **Fork: evaluation reports open with a verdict and next step for each patch and say unfinished testing first; no all-clear is printed over world checks that did not finish.** `evalreport plan --precedent DIR` adds each patch's own edit counts.
- **Fork: the review reel labels its vignette frames, marks every plugin in a counted clash, counts in the report's units, and splits a closing page too full to read.**
- **Fork: each still page of the review reel is narrated with its own words and held for them; the render refuses silence on a page, and subtitles are one short cue per sentence.** The closing counts the same checks as the opening, and says items a check could not judge as parts of a check that ran.
- **Fork: a patch's verdict says what it switches off or strips of another mod and keeps a value it chose as its choice; each record clash says its own in-game meaning.** The reel accounts for every clash it does not picture, names each setup by its own plugins, uses one name per kind of clash, keeps the report's attribution caveat, and its subtitles never overlap.
- **Fork: report verdicts measure each patch on a setup with it alone where one exists, name what a patch puts back of another mod's changes, and add up to their totals.** Plugin names keep their load-order spelling, records and cells are said by name where the plugin files give one, and a patch's census adds up.
- **Fork: the record-rows enrichment judges a plugin's change against its own masters, read from its header; an unreadable header is counted unjudged.**
- **Fork: `evalrun bundle --registry FILE --scope FILE` rebundles an older run with the registry and scope its plan froze; a bundle whose copies would not match the plan is now refused, naming the flag, instead of written and failing `verify-bundle`.**
- **The record stage no longer reports a record-flags difference that is only the compressed-storage flag, and reports a flag change once rather than under both MajorFlags and SkyrimMajorRecordFlags.**
- **Fork: poolprobe's `--json` lists its hits in a fixed order, by archive and then the plugin's path inside it.**
- **The record stage judges a plugin's removal of a field its masters hold, so a later mod dropping an earlier one's enable parent is reported;** the masters come from each plugin's own header, and one that can't be read is counted as unassessed.
- **The record stage reads every delta line the reader writes, so a lost edit is no longer hidden behind an unread line;** a list reordered where the game reads order is counted as unassessed.
- **The overlap, contact and state checks run several times faster, with the same rows: they use all cores (`--threads`, default 8; 1 is sequential) and do far less allocation. They need more memory: the contact check peaked about twice as high in testing.**
- **The records stage reads a capture's arms side by side when the machine has the memory, warns when other programs hold it, and reads one at a time when it doesn't or when `recordWorkers` is 1.** `records.json` says how many read at once and why.
- **Fork: motionprobe's `sweep` and lightprobe's outputs name the solid rule their results depend on,** or say `"none"`
  when the run applied none: `solidPolicy` in a sweep, `identity.solidPolicy` and `anchorPolicy` in lightprobe.
- **Fork: a report joined to a sibling evaluation compares the two probe builds by their whole program identity, and no
  longer joins a check whose probe build one side does not record;** the evidence index says how each check's builds
  were compared.
- **Fork: an unchanged `evalrun run` or `resume` leaves every receipt byte-identical, including those of rows that never
  ran;** such a receipt is rewritten only when what it states changes.
- **Fork: `evalrun run`, `resume` and `import-results` refuse a plan whose probes, conflictdiff or controller scripts
  changed since planning, and finalize rejects results from another build;** after any controller update, re-plan in a
  new directory. `verify.json` says `toolIdentity.guarantee`.
- **Fork: the geometry probes no longer report an activator or door whose base record has no model as sunk or left floating,
  and the report no longer sets aside a collision marker's route rows as having no body;** stateprobe names editor markers
  instead of counting them unjudged. `_geometry-harness/tests/test_modelless_activators_doors.py` checks it.
- **Fork: stateprobe, contactprobe and its empty-space mode no longer report a light whose base record has no model as
  sunk or left floating;** such a light has no body. Each run prints how many it left out. `_geometry-harness/tests/test_modelless_light.py`
  checks it.
- **Fork: when the route-cut check reaches no verdict, the report lists the objects it rated as blocking or likely to
  block a route as leads for an in-game check, never as clashes,** from blockprobe's new `--rows` file.
  `_geometry-harness/tests/test_route_cut_rows.py` and `test_evalreport_prediction.py` check it.
- **Fork: navownprobe lists broken navmesh edge links one per crossing, and conflictdiff and ordersweep keep and credit
  each.** conflictdiff refuses an emit written before this against one written after. `_geometry-harness/tests/test_navown_edge_identity.py`
  checks it.
- **Fork: stateprobe writes one row per pair of references in a state,** where an arriving object both encloses and sits in
  another, or two arrive together; the row keeps the deeper reading. `_geometry-harness/tests/test_state_row_identity.py`
  checks it.
- **Fork: overlapprobe's scene preflight refuses an arm whose plugins name meshes its `Mods` lack while an installed
  folder outside them has them,** such as a variant shipped as a plugin alone listed without its main mod. The sentence
  names the plugin, how many distinct meshes, and the folder to add at the end of `Mods`. `evalrun plan` runs it on every
  arm only when given an instance (without one it notes the preflight was skipped); a scene capture always runs it;
  other geometry probes do not. Only model paths are read; a mesh no installed folder has stays with the missing-assets
  check. `_geometry-harness/tests/test_plan_view_mesh_gaps.py` checks it.
- **Fork: every probe refuses a `--enable` it cannot place and an `--order` candidate built on another profile, in one
  sentence, exit 2.** Before, the run measured the order without the plugin, or through the other profile's lists.
  `_geometry-harness/tests/test_order_input_refusal.py` checks every probe.
- **Fork: `evalrun plan` stores absolute input paths, so a plan runs the same way from any folder.** An older plan whose
  relative paths do not resolve is refused by name.
- **Fork: an import that judged nothing is accepted as not applicable when it states `Members: 0` on an arm that loads
  none of the plan's targets.** Any other judged-0 import is still refused, with a step that can succeed.
  `_geometry-harness/tests/test_import_empty_population.py` checks both.
- **Fork: evaluation reports set aside a lost value the winning mod's rebuild supersedes, and say why.** A changed effect
  type or a move to another spot after a small seating fix is said on its own line, not counted as a clash to forward.
- **Fork: the patch section of an evaluation report opens with what the patch resolves, leaves and brings.** New problems
  are named per check; records and objects are named by editor ID, and each still-there item gives its unit.
- **Fork: contactprobe's `--terrain` and `--empty-space` emits now record the rule they were measured under, and conflictdiff
  refuses to compare them across different rules or modes.** `_geometry-harness/tests/test_contact_reverse.py` checks it.
- **Fork: motionprobe's swept volume stores the parts of a mesh that never move once, instead of once per sample, with the
  same results.** A large animated mesh such as a windmill no longer needs tens of gigabytes. proofprobe cases MV4-MV6 check it.
- **Fork: an order sweep's contactprobe reverse runs reuse the baseline's rows instead of judging every moved host again.**
  contactprobe `--reverse-of` refuses by name a baseline that is not its own; its emits now record the contact rule, and
  conflictdiff refuses emits measured under another. `_geometry-harness/tests/test_contact_reverse.py` checks both.
- **Fork: contactprobe and stateprobe spend less time in the contact test, with the same findings.** contactprobe no longer
  computes depth readings for the radius-grown shells, which it never used, and both probes measure each pair once per run.
  On a large evaluation this takes a substantial share off both probes' time. `--phase-timers FILE`
  (contactprobe, and stateprobe's existing flag) now also prints where the contact test spends its time.
- **Fork: the actual-shape solid check now tests every pair of a mesh's own triangles for self-intersection.** It used to skip
  some pairs that lay in different parts of the mesh, so a mesh that crosses itself could pass as a valid solid and be judged
  for containment. Such a mesh now answers unknown with the self-intersection reason. Results carry policy
  `actual_triangle_mesh_v2`, and comparisons with emits measured under v1 are refused by name.
- **Fork: contactprobe `--terrain` reports placed objects another mod's terrain leaves floating or buries, and needs `--burial-share`.**
  Only a clash nobody saw is emitted; `proofprobe --only terrain` runs its known answers.
- **Fork: an order sweep no longer marks an overlapprobe run REFUSED because its timer line counts refused pairs.**
  A real refusal is still marked; `_geometry-harness/tests/test_ordersweep_refused.py` checks every refusal string.
- **Fork: an order sweep's overlap runs reuse the pair results measured by its earlier runs of the same build, and
  overlapprobe --reverse-of refuses a baseline measured under other pair limits.** Findings are unchanged; a run prints how
  many pairs it served from earlier runs.
- **Fork: overlapprobe prepares each placed object's mesh once per run and keeps each mesh's solid check, and an order
  sweep's reverse overlap run takes the baseline run's rows instead of measuring them again.** The findings are unchanged; a run
  prints its pair-loop timings.
- **Fork: an order sweep keys a probe's cached results by every file of its build, not by its launcher alone, so a
  deploy that replaces only a probe's libraries no longer reuses the older build's results.** Every cached result misses once.
- **Fork: an evaluation report compares a list field item by item, says a mod putting back the value from
  before its own master's change as undoing that master's fix, on its own line, decides each row before counting a shared lost value once,
  and applies the same set-aside rules to the changes a patch undoes.** A lost change the winner also changed
  itself is said with what each side has.
- **Fork: an evaluation report counts a mod's change only where its value differs from the one it inherits from its
  masters, and calls a clash new with a mod only where the same field or object is not already affected without it,
  whichever plugins the check names.** A water seam only one side makes is said as that mod against the base game.
- **Fork: a capture request's camera no longer needs points picked by hand.** `capture_acquire.py acquire --points
  auto` measures, for each of the two objects, a point on a face an eye-level camera around them can see (the
  renderer's own object marks and depth picks), takes the floor from the measured storey rather than the ground under
  an arch, and keeps the planner on the bearings that saw both objects (`shotplan.py plan --bearing-window`). When no
  such face exists it says why in one sentence. Navmesh comparison requests are marked as having no camera path yet
  and are refused before any render.
- **Fork: an evaluation report no longer says a patch drops a mod's change where that mod only carried the earlier
  value; the patch restoring the other mod's change counts as resolved.** Record rows whose values show no change in
  game are set aside and said, not counted: lists holding the same items in another order, a list the winner keeps in
  full, cell Grid flags differing only in their unused bytes (the record comparison now reads only the land-flag byte),
  and a navmesh's geometry where the navmesh check judged it. A value a base-game plugin holds is no edit of the mod
  that carries it. The load-order line names the plugin moved and every plugin it passed, the scope line names only
  mods with a counted clash, and a sentence every item repeats is said once.
- **Fork: an evaluation report counts an object a quest or switch changes once, however many world states show it,
  and no longer calls a clash new with the patch when a setup without the patch already has it.** Rows with the same
  words against different objects are no longer merged, a count of an object's hosts is no longer called objects, and
  a clash counted from the separate evaluation without the patch says so. New with the patch now means new against
  the same order without the patch; a pair of objects or a lost value is counted once across checks and plugins. A load-order risk
  the record checks measured on the same plugins in another order is said at the top (`evalreport plan
  --order-setup`).
- **Fork: an evaluation report no longer counts a mod changing a record value of a plugin it lists as a master as a
  clash between mods, and counts a patch's new problems by object, not by row.** Such changes get their own line; a
  geometry clash between their objects is still counted, labelled with the master.
- **Fork: an evaluation report states each kind of clash in its own unit and says at the top what stays broken with
  the patches.** Overlaps also count their objects, cell clashes name their fields, and the setups a separate
  evaluation measured are named.
- **Fork: `evalrun bundle` now adds the names enrichment itself, so a report can tell which objects have a body.**
  The manifest's `enrichmentStatus.names` says in one sentence whether it was read, read in part, refused or skipped
  (`--no-names`).
- **Fork: overlap attribution now counts a pair as new when one object belongs to a plugin the earlier setup does not load.**
  Such an object cannot exist in that setup; before, the pair was left unresolved. An object whose plugin is loaded
  but which the scene did not list stays unresolved.
- **Fork: the route-cut check now evaluates every arm's own candidate and all of the plan's target plugins together,
  naming which target places each listed object; it used to read the selected profile and silently judge only the
  first target.** An arm that loads no target is reported not applicable.
- **Fork: an evaluation plan now accepts a candidate whose `Mods` order differs from its base profile: the probes read
  assets in the candidate's order and the plan names the re-ordered folders.** The live switch check refuses to launch a
  profile that does not hold that order, and so does `viewprobe --emit-bats --bats-profiles`.
- **Fork: the evaluation report reads what still loses navmesh ground with a patch from the patched setup's own
  re-measure, says "the patch keeps A's value; B's change is dropped" instead of "resolved" for record fields, counts
  record changes a patch newly undoes as new clashes, and states record fields beside records in the headline.**
- **Fork: the evaluation report says whose change stays lost where a patch only carries the winning value, and no
  longer counts a lost value that merely undoes an earlier mod's change.** Overwrite history a patch no longer flags is
  not called removed, and a navmesh that still loses ground with the patch is named; missing meshes are named with the
  mods and setups; with one evaluated mod the patch tally matches the headline and its navmesh loss is said at the top;
  the limits are in plain words.
- **Fork: the evaluation report attributes each geometry clash from every setup's own re-measure, so a clash one mod
  already has alone is never called "needs both" or credited to a patch, and says what stays with the patch at the top.**
  Fixtures at 0u are "cannot tell"; lost edits on objects both mods disable are not counted.
- **Fork: the evaluation report no longer counts any object whose record has no model, in any section, and says
  what a patch carries for each lost edit (the lost value, its own value, or the winning value) without claiming intent.**
  Overlaps are called candidates in the headline; a refused comparison is said in the patch summary.
- **Fork: the evaluation report now says, per predicted clash, whether the patch resolves it, keeps the existing winner
  on purpose, leaves it, or cannot be judged, and states its limits in words.** A clash already broken with one mod
  alone is no longer called "needs both".
- **Fork: the evaluation report no longer counts a light with no model as a physical clash, splits overlaps a patch
  leaves by who holds the objects, states a measured navmesh loss in its prediction, and counts each object once.**
  A difference only in a file path's letter case is not a lost edit; verify refuses a report over its page limit.
- **Fork: an evaluation whose geometry checks ran on the evaluated mods' population can get its video cameras again.**
  The camera step compares the renderer's load order with the one the finding was measured in, ignoring only which part
  of it the check measured; a different load order is still refused.
- **Fork: a world-edit evaluation now also checks fixtures left in empty space and geometry that appears once a switch
  flips only where the evaluated mods can reach, instead of across the whole load order.** Every kept finding is the one
  the whole-order check finds; the plan's `population` block lists these two checks under `scopedChecks`.
- **Fork: every probe now refuses a load order whose plugin lacks a master, in one sentence naming the plugin, the
  missing master and what to try, and exits 2 without writing anything.** This covers the profile, `--enable`/`--disable`
  and `--order`.
- **Fork: `evalrun plan` resolves each arm's plugins through that arm's own mod list, so a same-named variant plugin is
  planned with its own bytes, and evalreport names a same-name replacement instead of calling it a control.**
- **Fork: `evalrun plan` now runs overlapprobe's scene preflight on every arm and refuses an arm whose asset view the
  base profile cannot produce, with the reason and what to try; candidate `Provenance` values may be any JSON.**
- **Fork: a world-edit evaluation now measures object overlap and placement contact only where the evaluated mods
  and the load orders it compares can reach, instead of the whole load order.** The plan's `population` block names
  what was scoped and why.
- **Fork: a world state's route check no longer reports a Bethesda object that a Bethesda switch brings in as a
  conflict between mods; it is withheld as the base game's own.**
- **The world-edit evaluation no longer stops when a target plugin holds a record that cannot be read: it names that
  record in one sentence and treats every check that depends on the plugin's contents as unknown.**
- **Fork: a world state's route check now stands on that state's own floor when the switch brings in a floor piece
  from a neighbouring exterior cell.** Before, such a state was judged on the load-time floor.
- **Fork: an evaluation model setting (`models.*`) now runs exactly the model it names, through the plugin's agent
  for it, or refuses in one sentence.** A short name such as `haiku` is refused, and every evaluation command names
  it until you run `evalsettings.py set models.<key>=claude-haiku-5-5` (or `session`).
- **The world-edit evaluation now reports a fixture left hanging in empty space because another mod removed the floor and walls
  around it, including fixtures that appear only once a quest flips their switch.** Read the rows under "Fixtures left hanging
  in empty space".
- **Fork: an evaluation report's verdict now counts only the clashes that need both evaluated mods, and its opening
  names that pair.** A clash one of them already has with another mod on its own is listed under its own heading
  (or set aside with `evalreport plan --single-mod aside`) and never counted. `--headline-unit objects` counts the
  objects instead of the check's rows. Record checks now show their lost edits as rows: `evalrun enrich --bundle
  DIR --kind record-rows` derives them from an import's own transcripts, and `--kind names` adds in-game and mod
  names from the plugin files the plan records, so the reader's page needs no names file. verify-bundle checks what
  an enrichment cites.
- **Fork: an evaluation report's verdict states each kind of clash in its own unit and never adds kinds together;
  record checks count records, with each item's field count beside it.** The page lists record fields left
  unjudged, keeps record clashes with a third mod and long location lists in the evidence index, and counts them.
- **contactprobe `--contact-evidence` and overlapprobe `--contact-evidence-pairs` measure pairs with zero-area triangles.** A
  triangle with no area in its own model is left out and counted (`zeroAreaSkipped`); a pair whose placement collapses a
  triangle is measured in the pair frame (`frame`), with its envelope in world coordinates.
- **Fork: the first evaluation on a machine now opens a settings window, and an evaluation judges every switched world
  state its switch census finds unless you choose the Standard depth.** `evalsettings.py show` prints each choice
  and where it came from.
- **Fork: an evaluation report now opens with what adding the mods will break, then what the checks could not
  judge, then how the patch compares; differences with the base game are counted, never predicted.** Re-plan an
  older presentation to get the new opening.
- **Fork: an evaluation report's detail list now shows only clashes between two mods, a location list several setups
  share is printed once, and a patched report can attribute its prediction from the unpatched evaluation
  (`evalreport plan --sibling-bundle DIR`).** Results within one mod, against the base game or naming no pair move
  to the evidence index with their reason.
- **Animated objects' swept regions hold one copy of their triangle topology.** Samples of a swept region that
  share the same triangle indices and material tags now share one array of each instead of one per sample, which
  lowers the memory of whole-scene overlap runs with many animated objects. Geometry, sample count and every
  reported value are unchanged.
- **contactprobe `--surface` envelopes sit on the crossing curve.** `envelopeMin` / `envelopeMax` now bound the
  crossing curve itself; interior crossing segments were reported from a triangle vertex off the curve, up to a
  triangle's size away. Crossing lengths and every other value are unchanged. (The `contactEvidence` box that
  `subject_gate.py` and `overlap_triage.py` read is a different quantity and did not change.)
- **contactprobe measures surface pairs whose meshes carry zero-area triangles or empty shapes.** A triangle with no
  area in its own model is left out and counted (`zeroAreaSkipped`); a shape that declares no geometry is read as
  empty and named (`emptyShapesA` / `emptyShapesB`). A triangle that has area in the model but none once placed still
  refuses, naming the collapse, as does a member made only of zero-area triangles.
- **Contact grids no longer drop very large faces.** A host triangle spanning more grid cells than the grid indexes
  was skipped, so a contact against it went unmeasured; such faces are now searched on every query. A host grid
  whose fixed 32-unit cells would take more than half the contact cache budget is stored in coarser cells, which
  lowers memory under a tight budget and can make those queries slower; with memory to spare a grid keeps fixed
  cells. Answers are the same either way.
- **contactprobe fits a 16 GB machine.** Its per-placement collision grids and soups now share one byte budget
  (least recently used out first) instead of an entry count that let one exterior block of grids grow past 11 GB.
  `--cache-budget SIZE|auto` sets it; `auto`, the default, takes the smallest of a fifth of the memory the runtime may
  use, 40 % of the memory free now less the live data, and 4 GiB, and the run prints which term won. Free memory is
  read on Windows and Linux; where it cannot be read, `auto` uses the other two terms, skips the fit check, and the
  printed budget line says the free term was not applied. stateprobe shares the same budget. A run whose live
  data cannot fit beside the smallest budget refuses (exit 2) and says so. Output is identical at any budget: an
  evicted grid is rebuilt from the same inputs. The cache keys also carry the base object, so an arm that swaps a
  reference's base no longer reads the other base's cached geometry. Measured bounds: `--help` and the tool index.
- **Geometry probes refuse a run that names no MO2 instance.** A probe given no `--instance` (and no
  `HOUSECARL_MO2_INSTANCE` set for that run) exits 2 with `refused: instance-not-explicit` before reading
  anything, instead of taking the harness config's instance. The config's game folder and profile are used only
  when the run's instance is the config's own; for another instance the probe refuses with
  `game-not-for-this-instance` / `profile-not-for-this-instance` unless the run names them. A variable value
  stored at User or Machine level refuses with `instance-env-persistent`. `differential-sweep.ps1` takes
  `-Instance`, `-Game` and `-Profile`. The rules: `_geometry-harness/_shared/HarnessTarget.cs` and
  `viewprobe/README.md`.
- **A report no longer says "no patch was declared" when a mixed setup changes declared patches.** A setup
  that adds and removes plugins at once is still evaluated as mixed, with no patch judged on its own; its opening
  now names the declared patches it changes instead. The no-patch sentence appears only when no changed plugin
  is a declared patch. Patch identity rules: the `patches` entry under "A setup" in this file's earlier
  evalreport notes.
- **An imported result's note reaches every rendition, once.** The HTML evidence index renders it
  alongside the Markdown one, and the reader-facing sentence carries the text every note shares -
  computed over sentences, so wording on either side of a per-import figure survives - with each
  import's own note kept in full in the evidence index.
- **A result that says what it is now says it on the page, and a conclusion no longer denies a
  patch comparison that exists.** An imported result's own note is carried into the report's
  limits and the evidence index, once per distinct note: reuse provenance says where evidence
  came from, a note says what was done with it, and a reader shown only the first cannot tell
  that numbers were recomputed. Separately, the sentence "No setup containing a patch was
  compared" was appended whenever a reduction came from a non-patch arm, which denied a
  declared patch comparison that had been measured; the conclusion now states which arm
  produced the reduction and what happened to the patch comparison separately.
- **Patch identity is declared, never inferred, and load order is compared as order.** A setup
  whose only difference is an undeclared plugin is reported as an unstated difference, naming
  the plugin as one that differs rather than as an evaluated patch; declare it with a plan's
  `patches` list, an arm's `provenance.patch`, or `evalreport plan --declare-patch`. A setup
  holding the same plugins in a different order is a reordered alternative, not a control, and
  a change there is reported as a load-order effect. Only a declared patch comparison can carry
  a repair reading.
- **A setup that removes a mod is no longer reported as resolving conflicts, and a mod's own
  files are no longer named as the patch under evaluation.** Each alternative setup is now
  classified by what kind of comparison it is - a patch comparison, a mod removal, a dependency
  control or a same-order control - and only a patch comparison can carry a repair reading. A
  reduction produced by removing content is stated as that, with the reason it is not a fix to
  install. A plugin is named as an evaluated patch only when it is the difference in a patch
  comparison, in either direction, so companion master files that travel with a removed mod are
  no longer presented as patches. Check which kind a setup was classified as in
  `presentation.json` under `scope.arms[].comparisonKind`.
- **Bare navmesh overwrites no longer become report or video incompatibilities.** Reader verdicts
  and comparison counts require evidence beyond overwrite history; paired navmesh captures retain
  the independently measured link mismatch and its endpoints. Setup labels and baseline matches
  distinguish a remaining problem from one introduced by a patch. The evidence gate and bounds are in
  [`HOUSECARL_EVALUATION_PRESENTATION.md`](../standards/HOUSECARL_EVALUATION_PRESENTATION.md#3-the-presentation-plan---outpresentationjson-d2).
- **Evaluation reports explain what a comparison changes and plan diagnostic scenes.** Reports
  name the winning navmesh plugin,
  distinguish overlap pairs from individual objects and retain existing findings when a comparison
  adds none. Capture plans select paired navmesh views and two-colour overlap examples. Viewprobe's
  `--report-mode` removes captions and controls while keeping scene framing. Scope binding, selection
  and capture limits, including the open local-contributor producer acceptance, are defined in
  [`HOUSECARL_EVALUATION_PRESENTATION.md`](../standards/HOUSECARL_EVALUATION_PRESENTATION.md).
  Saved results from existing houseCARL tools can enter normal evaluation accounting through
  `evalrun import-results`; its evidence and identity requirements are in
  [`HOUSECARL_EVALUATION_BUNDLE.md` §4.8](../standards/HOUSECARL_EVALUATION_BUNDLE.md#48-importing-an-existing-mcp-result).
- **Evaluation reports explain the mod comparison, and videos walk through narrated scenes.**
  `evaluation-reporting` compiles a verified bundle into Markdown, HTML and a printable PDF with a complete
  evidence index. Reports name the target, comparison and evaluated patch up front, explain findings in modder
  language and preserve incomplete testing and new problems separately from repairs. Diagnostic videos use
  qualified scene images or footage with required voiceover, a spoken opening verdict and no added on-screen
  text. The workflow retains source/time mapping and refuses missing media or narration prerequisites instead
  of substituting silent text cards. Inputs, print limits, speech dependencies and verification are defined in
  [`HOUSECARL_EVALUATION_PRESENTATION.md`](../standards/HOUSECARL_EVALUATION_PRESENTATION.md).
- **The world-edit compatibility checklist is now data the skill ships, and a controller enforces it.**
  `world-edit-compatibility/references/check-registry.json` lists every check a location-mod evaluation must account for,
  across eight families, with its applicability trigger, prerequisites, the tool that runs it or the stated reason none
  can, and what it may claim. A footprint probe (`scopeprobe`) measures what a plugin contains by construction, and the
  harness controller (`_geometry-harness/evalrun.py`) derives the plan from the two, runs the supported checks through
  the existing probes and the order sweep, validates every receipt against a closed list of rejection causes (a stale
  output, a missing identity, a wrong state, an empty extraction, a refused arm), resumes only what still hashes the same,
  and writes one evidence bundle whose status index says what is established and what remains unassessed. Completion is
  derived from the ledger: an unsupported, manual, skipped, failed or unknown-applicability required check keeps the
  evaluation incomplete, and the strongest null is "no findings in the completed checks". Contract in
  `standards/HOUSECARL_EVALUATION_BUNDLE.md`.
- **The evaluation controller now binds its evidence to what was asked for, and refuses what it cannot re-check.**
  An independent review of the controller above found three ways a result could detach from its evidence while still
  reading as accepted. Now: a receipt is accepted only when the emit's plugin order, content hashes, overlays and
  fingerprint describe the configuration the plan froze for that arm (the reverse arm against the baseline's), never
  merely because it agrees with the arm's first receipt; `run` and `resume` both re-check every consumed input, treat a
  missing file as a change, refuse to reuse an input they cannot state, and invalidate dependents transitively; and
  `finalize`, `bundle` and `verify-bundle` walk the same receipt-to-evidence chain, so a claim whose emit is gone or
  changed is an integrity refusal rather than a completed null check, a bundle never drops a required file from its
  manifest or adopts a fresh hash for a receipt's output, and a relocated bundle verifies from its own files alone with
  structural validity and evaluation completion reported as two states. Plans and bundles written before this change
  are refused by name (schema 1); plan again from the same inputs. Contract sections 3, 4, 6, 7, 8 and 10 in
  `standards/HOUSECARL_EVALUATION_BUNDLE.md`.
- **The evaluation plan now states a supplied patch's own file and hash, so its arm's evidence is reusable.**
  A plugin the measured profile does not load — the patch you hand the evaluation to try — had no hash in the plan, so
  every child of that arm that does not state its own configuration (the footprint probe, the json and text readers, the
  diff) could not be re-checked, and a second `run` or `resume` re-measured them all. The plan now resolves such a name
  the way the probes resolve it — the overwrite folder, the arm's mod folders in priority order, the game's Data folder,
  then the profile's other mod folders, first sighting winning — and records the copy's path, its hash and the folder it
  came from, plus every other folder providing that name with each copy's hash, so a name two folders provide shows the
  ambiguity and whether the bytes differ. A probe that read a different copy is now a named mismatch; identical bytes at
  another path are accepted and recorded. Pass `--instance`, `--profile` and `--game` to plan for this; the cases where
  the plan still states no hash, and what it does then, are the supplied-plugin limit in
  `standards/HOUSECARL_EVALUATION_BUNDLE.md` section 10.
