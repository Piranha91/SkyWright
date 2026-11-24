# Tools by question

Generated from the capability registry. Edit its metadata and run `tool-index --write`; `tool-index --check` checks this public index.

Default status: current means source present; it does not prove deployment or a successful run.


## Missing objects and world state

Not drawn / missing object / enable parent / initially disabled: start with [viewprobe draw verdicts](#tool-viewprobe.refs-at), then [switch census and trigger map](#tool-switchprobe), then [state findings](#tool-stateprobe) and [state rendering](#tool-viewprobe.state). Retain the baseline. Disabled singletons are counted separately; read a named one's enablers with [switchprobe --targets](#tool-switchprobe.targets). State-overlap is unmeasured.

## Questions

- [Are effect-shader shapes (clouds, glow, flames) left out of viewprobe's inside and clear queries on a real render, while a mixed mesh keeps its solid body?](#tool-_geometry-harness-tests-test_effect_shapes.py) — `_geometry-harness/tests/test_effect_shapes.py`
- [Are planes reports streamed with identical bytes and atomic publication?](#tool-_geometry-harness-tests-test_report_sink.py) — `_geometry-harness/tests/test_report_sink.py`
- [Are the report's artifacts, links, and page counts still valid?](#tool-evalreport.verify) — `evalreport.verify`
- [At which station gazes is a plane the patch changed in view and within reach, and which edits can a render decide?](#tool-planeassess.scope) — `planeassess.scope`
- [Can I isolate the render surface of the named reference?](#tool-viewprobe.ref-surface) — `viewprobe.ref-surface`
- [Can anything in the load order enable this disabled reference, including a singleton with no enable parent?](#tool-switchprobe.targets) — `switchprobe.targets`
- [Can this report request obtain a verified camera and binding receipt?](#tool-capture-acquire.acquire) — `capture-acquire.acquire`
- [Did two renderer runs produce the same frames, masks, reports and other declared artifacts?](#tool-renderequiv) — `renderequiv`
- [Do SkyWright's known-answer suites pass, and do its run-coverage manifest and tool index match the tree?](#tool-skywright.guards) — `SkyWright.Guards`
- [Do adjacent cells disagree at their landscape height or texture boundary?](#tool-seamprobe) — `seamprobe`
- [Do contactprobe's anchor, surface, volume and motion outputs name the solid rule their results depend on, and say none when no solid rule applied?](#tool-_geometry-harness-tests-test_contact_solid_rule.py) — `_geometry-harness/tests/test_contact_solid_rule.py`
- [Do motionprobe sweep and lightprobe outputs name the solid rule their results depend on, and say none when no solid rule applied?](#tool-_geometry-harness-tests-test_solid_rule_named.py) — `_geometry-harness/tests/test_solid_rule_named.py`
- [Do opt-in live checks leave defaults unchanged and refuse bad live imports?](#tool-_geometry-harness-tests-test_evallive.py) — `_geometry-harness/tests/test_evallive.py`
- [Do overlapprobe, contactprobe (default pass and --empty-space) and stateprobe with --population keep exactly the whole-order rows of the plan's population, in both arms?](#tool-_geometry-harness-tests-test_population_scope.py) — `_geometry-harness/tests/test_population_scope.py`
- [Do patchprobe, poolprobe and fomodprobe list every discovered patch with the sha256 of its bytes?](#tool-_geometry-harness-tests-test_discovery_probes.py) — `_geometry-harness/tests/test_discovery_probes.py`
- [Do placed obstacles cut walkable routes in this plugin or cell?](#tool-blockprobe) — `blockprobe`
- [Do quest targets and their supporting placement relationships still match the winning world?](#tool-driftprobe) — `driftprobe`
- [Do real renderer run records report their stages and workload correctly?](#tool-_geometry-harness-tests-test_run_records.py) — `_geometry-harness/tests/test_run_records.py`
- [Do shotplan's camera conventions and INI resolution match their known answers?](#tool-_geometry-harness-viewprobe-tools-test_shotplan_camera.py) — `_geometry-harness/viewprobe/tools/test_shotplan_camera.py`
- [Do simultaneous runs of the same render command produce the same output?](#tool-renderagree) — `renderagree`
- [Do stateprobe, contactprobe and its empty-space mode leave out a light whose base record has no model, and keep a light with a model or with a model that could not be read?](#tool-_geometry-harness-tests-test_modelless_light.py) — `_geometry-harness/tests/test_modelless_light.py`
- [Do synthetic depth arrays produce the expected projection and clearance results?](#tool-depthsurvey.selftest) — `depthsurvey.selftest`
- [Do the capture fallbacks (cutaway, top-down, see-through marks) give their known answers on a real render, and do the reuse, reel and caption rules hold?](#tool-integration-housecarl-tests-test_capture_fallbacks.py) — `integration/housecarl/tests/test_capture_fallbacks.py`
- [Do the depth-measurement mode's texture keep rule and mask rule give their known answers?](#tool-viewprobe.depth-measurement-selftest) — `viewprobe.depth-measurement-selftest`
- [Do the frustum test and the occlusion rule's shape classes still give their known answers?](#tool-planeassess.selftest) — `planeassess.selftest`
- [Do the geometry probes leave out activators and doors whose base record has no model, set editor markers aside by name, and keep a marker carrying a collision primitive unjudged?](#tool-_geometry-harness-tests-test_modelless_activators_doors.py) — `_geometry-harness/tests/test_modelless_activators_doors.py`
- [Do the load-order and geometry rules pass their constructed known-answer cases?](#tool-proofprobe) — `proofprobe`
- [Do the report and reel give a verdict and next step per patch, say unfinished testing first, name one patch per outcome, show every produced picture, and plan the reel's counts, labels, pages and closing in the report's terms?](#tool-integration-housecarl-tests-test_reader_rules.py) — `integration/housecarl/tests/test_reader_rules.py`
- [Do the report and reel say a single case in the singular, one reason per unchecked check, offline renders as renders, the location list's share of the load order, places by name or grid, forward-versus-carry-both, worse patch placements, a patch's lines on its own pair only, and relative PDF links?](#tool-integration-housecarl-tests-test_report_reel_batch.py) — `integration/housecarl/tests/test_report_reel_batch.py`
- [Do the report lane and orderleads read the shared record-flag table, do the report and reel name a mod the same way, does the patch search keep its unread listings, hub paths and build, does the author-advice count include stated clashes, and does the reel read media from every joined sibling?](#tool-integration-housecarl-tests-test_report_reel_batch_2.py) — `integration/housecarl/tests/test_report_reel_batch_2.py`
- [Do the winning mesh bounds disagree with the plugin's stored bounds?](#tool-geoprobe) — `geoprobe`
- [Do viewprobe's point queries (--nav-at, --clear-at, --inside-at, --refs-at) answer or refuse in one sentence, never print nothing, on a run without an eye or with a malformed value?](#tool-_geometry-harness-tests-test_point_queries_answer_or_refuse.py) — `_geometry-harness/tests/test_point_queries_answer_or_refuse.py`
- [Do winning cell records lose water data or disagree at a water boundary?](#tool-waterprobe) — `waterprobe`
- [Does a Deep evaluation judge every census state and name every one it cannot?](#tool-integration-housecarl-tests-test_stategen.py) — `integration/housecarl/tests/test_stategen.py`
- [Does a buried clash read as buried in the reel and the report, and does a top-down hide what covers the pair from above by the cutaway's rule?](#tool-integration-housecarl-tests-test_capture_fallbacks.py) — `integration/housecarl/tests/test_capture_fallbacks.py`
- [Does a fresh user get the settings window once, and is a saved choice honoured?](#tool-integration-housecarl-tests-test_evalsettings.py) — `integration/housecarl/tests/test_evalsettings.py`
- [Does a navmesh comparison request get its measured top-down camera, both states and its refusals offline?](#tool-integration-housecarl-tests-test_capture_navmesh.py) — `integration/housecarl/tests/test_capture_navmesh.py`
- [Does a navmesh vignette's earlier-version caption, on the reel strip and in the report, name the plugin the historical frame's own --nav-version draws (never the plan's re-cut history, and none when the frame names none), say which mod replaced that version, and keep a name ending in s free of an "s's" possessive, in the narration too?](#tool-integration-housecarl-tests-test_navmesh_caption.py) — `integration/housecarl/tests/test_navmesh_caption.py`
- [Does a neighbour mod's page state a clash with the evaluated mod?](#tool-adviceprobe) — `adviceprobe`
- [Does a patch edit on a light fix a lighting clash that only the combination of two mods produces (orphaned, leaking, doubled or over-limit lights)?](#tool-lightclash) — `lightclash`
- [Does a placed light shine through an opaque surface, sit behind a wall or inside a solid, and how many lights reach each drawn shape?](#tool-lightprobe) — `lightprobe`
- [Does a placed-object geometry row that holds in one order of a free pair and not the other reach the report as a load-order risk, with rows in both orders left out, unmeasured rows counted, drifted plugins named and mismatched orders refused?](#tool-integration-housecarl-tests-test_order_geometry.py) — `integration/housecarl/tests/test_order_geometry.py`
- [Does a refused camera plan leave a labelled top-down fallback and keep its refusal?](#tool-_geometry-harness-viewprobe-tools-test_shotplan_fallback.py) — `_geometry-harness/viewprobe/tools/test_shotplan_fallback.py`
- [Does a report join several separate evaluations of the same order, each lending the setups it can stand in for, and refuse one it cannot use by name?](#tool-integration-housecarl-tests-test_evalreport_siblings.py) — `integration/housecarl/tests/test_evalreport_siblings.py`
- [Does a run keep each reader transcript once: does import-results link the capture's request and response (a copy across volumes), refuse a changed transcript, do the bundle and media bundle link the import's files and still verify, and does a records rerun or resume leave every byte reachable through a link unchanged?](#tool-_geometry-harness-tests-test_transcript_links.py) — `_geometry-harness/tests/test_transcript_links.py`
- [Does a scene import skip a declared cell whose owning plugin the setup neither loads nor masters?](#tool-_geometry-harness-tests-test_declared_cell_presence.py) — `_geometry-harness/tests/test_declared_cell_presence.py`
- [Does a shot in a batch equal the same shot rendered in a fresh process?](#tool-rendertwin) — `rendertwin`
- [Does a vignette's capture camera pass the measured state's camera checks (inside test, the sightlines of the participants that setup places) in every other state it renders, try the next pose of the chain when one fails, refuse naming the state and the object when none passes, and keep its single camera when every state is clear?](#tool-integration-housecarl-tests-test_capture_every_state.py) — `integration/housecarl/tests/test_capture_every_state.py`
- [Does adviceprobe find a clash stated on a neighbour's page, and does the evaluation pass the neighbours and record the clash rows?](#tool-_geometry-harness-tests-test_adviceprobe.py) — `_geometry-harness/tests/test_adviceprobe.py`
- [Does an evaluation plan run the same way from any working folder?](#tool-_geometry-harness-tests-test_plan_paths.py) — `_geometry-harness/tests/test_plan_paths.py`
- [Does an exterior camera station stand on navmesh at its own level, eye at that level plus the eye height, while interiors keep one floor?](#tool-integration-housecarl-tests-test_exterior_stations.py) — `integration/housecarl/tests/test_exterior_stations.py`
- [Does any record of a plugin file refer to a given object (its FormID through the plugin's masters, compressed records and script properties included)?](#tool-refscan) — `refscan`
- [Does blockprobe --rows write every route-cut row its listing states, uncapped, with the passage share the rule states?](#tool-_geometry-harness-tests-test_route_cut_rows.py) — `_geometry-harness/tests/test_route_cut_rows.py`
- [Does buildlayout.py's fallback marker equal the one the build writes, and do its paths exist in that build?](#tool-_geometry-harness-tests-test_buildlayout.py) — `_geometry-harness/tests/test_buildlayout.py`
- [Does contactprobe --reverse-of give the full reverse's emit, refuse a baseline emit that is not this run's own, and does conflictdiff refuse contactprobe emits (sweep, --terrain or --empty-space) measured under different or unknown rules, or of another mode?](#tool-_geometry-harness-tests-test_contact_reverse.py) — `_geometry-harness/tests/test_contact_reverse.py`
- [Does contactprobe take the fast memory route when the memory is there, lean under a limit or when other programs hold the memory (warning by name, unattended default recorded), stop with exit 5 under --memory-route ask, and leave no emit or rows behind when it refuses or runs out of memory?](#tool-_geometry-harness-tests-test_contact_memory_route.py) — `_geometry-harness/tests/test_contact_memory_route.py`
- [Does depth-survey projection and lookup math satisfy its synthetic controls?](#tool-_geometry-harness-viewprobe-tools-test_depthsurvey.py) — `_geometry-harness/viewprobe/tools/test_depthsurvey.py`
- [Does each frame's embedded replay command reproduce that frame?](#tool-_geometry-harness-tests-test_replay_command.py) — `_geometry-harness/tests/test_replay_command.py`
- [Does evalreport plan verify --bundle and --sibling-bundle in place without writing a byte or mtime into either, without a copy in proportion to the bundle even when its evidence is hard-linked, leave no tree after a failure mid-verify, refuse a verify that wrote no result, does runcontrol verify-bundle --out write its result only to the named file, and does every caller check a bundle with runcontrol rather than Python?](#tool-integration-housecarl-tests-test_evalreport_verify_in_place.py) — `integration/housecarl/tests/test_evalreport_verify_in_place.py`
- [Does evalreport's reel refuse a capture-results file it cannot read, and still plan without one?](#tool-integration-housecarl-tests-test_capture_results_unreadable.py) — `integration/housecarl/tests/test_capture_results_unreadable.py`
- [Does every imported reference receive exactly one accounting disposition?](#tool-_geometry-harness-tests-test_imported_reference_accounting.py) — `_geometry-harness/tests/test_imported_reference_accounting.py`
- [Does every probe refuse a --enable it cannot place or an --order candidate built on another profile, in one sentence?](#tool-_geometry-harness-tests-test_order_input_refusal.py) — `_geometry-harness/tests/test_order_input_refusal.py`
- [Does every probe refuse, in one sentence, a load order whose plugin lacks a master?](#tool-_geometry-harness-tests-test_missing_master_refusal.py) — `_geometry-harness/tests/test_missing_master_refusal.py`
- [Does every probe refuse, with a named reason, when no MO2 instance is given instead of falling back to the harness config's instance?](#tool-_geometry-harness-tests-test_explicit_instance.py) — `_geometry-harness/tests/test_explicit_instance.py`
- [Does extraction scratch preserve other files and refuse failures consistently?](#tool-_geometry-harness-tests-test_extraction_scratch.py) — `_geometry-harness/tests/test_extraction_scratch.py`
- [Does flipping a mod's script-driven enable switches crash or stall the game under this load order?](#tool-switchflip) — `switchflip`
- [Does hiding these objects remove anything a clashing object touches or rests on?](#tool-viewprobe.protect) — `viewprobe.protect`
- [Does mask deduplication preserve exact output bytes and distinguish different masks?](#tool-_geometry-harness-tests-test_mask_evidence.py) — `_geometry-harness/tests/test_mask_evidence.py`
- [Does miss diagnosis cluster edits and preserve unknown reasons correctly?](#tool-_geometry-harness-tests-test_missdiag.py) — `_geometry-harness/tests/test_missdiag.py`
- [Does narration preparation enforce its contracts without model weights or network access?](#tool-integration-housecarl-tests-test_evalspeech.py) — `integration/housecarl/tests/test_evalspeech.py`
- [Does navedit plan, refuse and write navmesh edits, markers and repairs as its known answers say?](#tool-_geometry-harness-tests-test_navedit.py) — `_geometry-harness/tests/test_navedit.py`
- [Does navownprobe count a link into a deleted navmesh, emit mod deletions, judge border links by their geometry (no matching edge, no link back, ambiguous candidates), find navmesh left on ground a later LAND moved, and plan a relink that refuses an ambiguous match?](#tool-_geometry-harness-tests-test_navmesh_repair_rules.py) — `_geometry-harness/tests/test_navmesh_repair_rules.py`
- [Does navownprobe write one EDGE-IDX row per broken edge link, and does every identity-keyed reader (conflictdiff, ordersweep, evalreport) keep and credit each link?](#tool-integration-housecarl-tests-test_navown_edge_identity.py) — `integration/housecarl/tests/test_navown_edge_identity.py`
- [Does newcomer selection give its hand-derived answers on a synthetic instance?](#tool-selectprobe.selftest) — `selectprobe.selftest`
- [Does orderleads offer only clashes between mods, mask the compressed flag, tell dummies from the map, and reach a lead by added mod, place and pair?](#tool-_geometry-harness-tests-test_orderleads.py) — `_geometry-harness/tests/test_orderleads.py`
- [Does orderpass record a failed or missing stage as unassessed, carry on, and make an added mod's view the pass's first read?](#tool-_geometry-harness-tests-test_orderpass.py) — `_geometry-harness/tests/test_orderpass.py`
- [Does ordersweep carry contactprobe's memory route (route, unattended default, warning) into summary.json and its job records, and record a run without one as not reported rather than fast?](#tool-_geometry-harness-tests-test_ordersweep_memory_route.py) — `_geometry-harness/tests/test_ordersweep_memory_route.py`
- [Does ordersweep still catch every refusal a probe can print, and not read a count of refused items as a refusal?](#tool-_geometry-harness-tests-test_ordersweep_refused.py) — `_geometry-harness/tests/test_ordersweep_refused.py`
- [Does ordersweep's cache key tell two builds apart when only their dlls differ?](#tool-_geometry-harness-tests-test_ordersweep_program_key.py) — `_geometry-harness/tests/test_ordersweep_program_key.py`
- [Does overlap attribution satisfy the independent acceptance controls?](#tool-_geometry-harness-tests-test_overlap_attribution_contract.py) — `_geometry-harness/tests/test_overlap_attribution_contract.py`
- [Does overlap triage preserve locality, counterpart identity and incomplete reviews?](#tool-integration-housecarl-tests-test_overlap_triage.py) — `integration/housecarl/tests/test_overlap_triage.py`
- [Does overlapprobe --reverse-of give the full reverse's emit, and refuse a baseline emit that is not this run's own?](#tool-_geometry-harness-tests-test_reverse_reconcile.py) — `_geometry-harness/tests/test_reverse_reconcile.py`
- [Does planeassess class plane edits, read placed primitives and test the frustum and rule shapes correctly?](#tool-_geometry-harness-planeassess-test_planeassess.py) — `_geometry-harness/planeassess/test_planeassess.py`
- [Does reading a capture's arms side by side keep each arm's evidence its own, choose workers from memory, and report failure, stop and resume as one worker does?](#tool-_geometry-harness-tests-test_records_parallel.py) — `_geometry-harness/tests/test_records_parallel.py`
- [Does render-batch readiness enforce the offline planning and refusal contracts?](#tool-_geometry-harness-viewprobe-tools-test_renderbatch.py) — `_geometry-harness/viewprobe/tools/test_renderbatch.py`
- [Does runcontrol accept a judged-0 import only when it states no members on an arm without the targets, and refuse every other?](#tool-_geometry-harness-tests-test_import_empty_population.py) — `_geometry-harness/tests/test_import_empty_population.py`
- [Does runcontrol plan refuse an arm that overlapprobe's scene preflight would refuse?](#tool-_geometry-harness-tests-test_plan_scene_preflight.py) — `_geometry-harness/tests/test_plan_scene_preflight.py`
- [Does runcontrol population write, byte for byte, the population file the schema names for the delta, print its identity, refuse a delta mod folder with a Base Object Swapper ini in one sentence, and write nothing but --out?](#tool-_geometry-harness-tests-test_evalrun_population_verb.py) — `_geometry-harness/tests/test_evalrun_population_verb.py`
- [Does shotplan resolve a usable viewprobe and explain unavailable builds?](#tool-_geometry-harness-viewprobe-tools-test_shotplan_vp.py) — `_geometry-harness/viewprobe/tools/test_shotplan_vp.py`
- [Does state traversal retain the correct moved reference winner?](#tool-stateprobe.moved-ref-selftest) — `stateprobe.moved-ref-selftest`
- [Does stateprobe name the primitive table it was given, judge a collision-marker box measured solid as a route obstacle, and read placed actors a state brings in from their placement without ever writing a row for one?](#tool-_geometry-harness-tests-test_state_marker_box_and_actors.py) — `_geometry-harness/tests/test_state_marker_box_and_actors.py`
- [Does stateprobe write one row per pair of references in a state, whichever contact pass found it, and keep the deeper reading?](#tool-_geometry-harness-tests-test_state_row_identity.py) — `_geometry-harness/tests/test_state_row_identity.py`
- [Does subject qualification refuse wrong identities, invisible features and invalid evidence?](#tool-integration-housecarl-tests-test_subject_gate.py) — `integration/housecarl/tests/test_subject_gate.py`
- [Does the GPU occlusion query count the same pixels as the CPU depth compare on this machine?](#tool-viewprobe.occlusion-query-selftest) — `viewprobe.occlusion-query-selftest`
- [Does the actual overlap probe enforce the imported scene-scope contract?](#tool-_geometry-harness-tests-test_scene_scope_import.py) — `_geometry-harness/tests/test_scene_scope_import.py`
- [Does the actual planner verify and export the camera justified by its queries?](#tool-_geometry-harness-viewprobe-tools-test_shotplan_plan.py) — `_geometry-harness/viewprobe/tools/test_shotplan_plan.py`
- [Does the asset path length limits diagnostic satisfy its expected contract?](#tool-viewprobe.asset-path-budget) — `viewprobe.asset-path-budget`
- [Does the atomic report publication diagnostic satisfy its expected contract?](#tool-viewprobe.report-sink-selftest) — `viewprobe.report-sink-selftest`
- [Does the basic renderer startup diagnostic satisfy its expected contract?](#tool-viewprobe.smoke) — `viewprobe.smoke`
- [Does the batch-isolation harness pass its constructed control cases?](#tool-rendertwin.selftest) — `rendertwin.selftest`
- [Does the capture point picker return visible faces, the right floor and plain refusals on the shapes real requests were refused on?](#tool-integration-housecarl-tests-test_capture_points.py) — `integration/housecarl/tests/test_capture_points.py`
- [Does the capture ring stand on the navmesh storey rather than a floor high up a tall focus, does a flat partner box still find the floor, does the reel say how many camera spots a buried clash rests on, is a see-through partner never said to bury the other object, and does the reel picture a group's strongest crossing?](#tool-integration-housecarl-tests-test_capture_follow_ups.py) — `integration/housecarl/tests/test_capture_follow_ups.py`
- [Does the cell scene resource lifetime diagnostic satisfy its expected contract?](#tool-viewprobe.cell-scene-selftest) — `viewprobe.cell-scene-selftest`
- [Does the census report a count only from a completed read, and rank candidates by uncovered features?](#tool-_geometry-harness-tests-test_modcensus.py) — `_geometry-harness/tests/test_modcensus.py`
- [Does the configuration fingerprint have one owner, runcontrol, that the Python tools ask?](#tool-_geometry-harness-tests-test_fingerprint_owner.py) — `_geometry-harness/tests/test_fingerprint_owner.py`
- [Does the driver of the stages after the bundle reuse a stage only when nothing changed, continue a run folder the built-in controller carried through the bundle, and refuse one it did not?](#tool-integration-housecarl-tests-test_evalpipeline.py) — `integration/housecarl/tests/test_evalpipeline.py`
- [Does the edit-kind classifier give one edit per record per kind, absorb the disable idiom and drop bookkeeping?](#tool-_geometry-harness-tests-test_editkinds.py) — `_geometry-harness/tests/test_editkinds.py`
- [Does the equivalence comparator reject its deliberately unequal fixture outputs?](#tool-renderequiv.selftest) — `renderequiv.selftest`
- [Does the evaluation controller match the oracle through its real workflow?](#tool-_geometry-harness-tests-test_evalrun.py) — `_geometry-harness/tests/test_evalrun.py`
- [Does the flip-plan generator keep branches apart, keep source order and name every call it does not plan?](#tool-integration-housecarl-tests-test_flipplan.py) — `integration/housecarl/tests/test_flipplan.py`
- [Does the idle gate refuse probes by command line and blockers by name?](#tool-integration-housecarl-tests-test_machineidle.py) — `integration/housecarl/tests/test_machineidle.py`
- [Does the interrupted report publication diagnostic satisfy its expected contract?](#tool-viewprobe.report-sink-kill-arm) — `viewprobe.report-sink-kill-arm`
- [Does the light-clash fold attribute and decide orphaned, leaking, doubled and over-limit lights correctly?](#tool-_geometry-harness-tests-test_lightclash.py) — `_geometry-harness/tests/test_lightclash.py`
- [Does the live driver keep a launch's cleanup when the reading rule's owner cannot be asked?](#tool-integration-housecarl-tests-test_switchflip_rule_unavailable.py) — `integration/housecarl/tests/test_switchflip_rule_unavailable.py`
- [Does the live driver refuse a bad flip plan and export one arm's reading correctly?](#tool-integration-housecarl-tests-test_switchflip_plan.py) — `integration/housecarl/tests/test_switchflip_plan.py`
- [Does the live switch check's population rule keep unknowns open and answer not-applicable only when nothing driven touches a target?](#tool-integration-housecarl-tests-test_switchflip_scope.py) — `integration/housecarl/tests/test_switchflip_scope.py`
- [Does the measured outward depth survey admit or block this candidate eye?](#tool-depthsurvey) — `depthsurvey`
- [Does the mesh reuse ownership diagnostic satisfy its expected contract?](#tool-viewprobe.mesh-reuse-red) — `viewprobe.mesh-reuse-red`
- [Does the moved and deleted reference selection diagnostic satisfy its expected contract?](#tool-viewprobe.moved-ref-selftest) — `viewprobe.moved-ref-selftest`
- [Does the names read of a manual-mode bundle read the plan's review arm, not the baseline or a union of arms?](#tool-_geometry-harness-tests-test_workbench_manual_names.py) — `_geometry-harness/tests/test_workbench_manual_names.py`
- [Does the narration packet ask for plain words and carry the glossary, does narrate-apply accept a rewrite only where a fidelity judgement made for its exact text says it keeps the meaning, and does the judge's model setting never resolve to a Fable model?](#tool-integration-housecarl-tests-test_narration_fidelity.py) — `integration/housecarl/tests/test_narration_fidelity.py`
- [Does the navmesh grid rebuild reproduce the engine's divisor, bounds and cells?](#tool-_geometry-harness-tests-test_navgrid_rebuild.py) — `_geometry-harness/tests/test_navgrid_rebuild.py`
- [Does the navmesh link query give its known answers on synthetic records?](#tool-viewprobe.nav-link-smoke) — `viewprobe.nav-link-smoke`
- [Does the navmesh selection diagnostic satisfy its expected contract?](#tool-viewprobe.nav-selection-smoke) — `viewprobe.nav-selection-smoke`
- [Does the occlusion rule's orientation scope (upright tolerance, Box upright test, horizontal class) satisfy its known answers?](#tool-viewprobe.occlusion-rule-selftest) — `viewprobe.occlusion-rule-selftest`
- [Does the occlusion scorer library count only in-band, visible, plane-culled pixels and split them by owner?](#tool-_geometry-harness-planeassess-test_occlscore.py) — `_geometry-harness/planeassess/test_occlscore.py`
- [Does the order map give its hand-derived answers on a synthetic instance?](#tool-ordermap.self-test) — `ordermap.self-test`
- [Does the owned extraction scratch cleanup diagnostic satisfy its expected contract?](#tool-viewprobe.extraction-scratch-sweep) — `viewprobe.extraction-scratch-sweep`
- [Does the per-plane-edit verdict decide a new hide, a fix, the plane's owner, a visibility test that did not run and a held refusal correctly?](#tool-_geometry-harness-planeassess-test_verdict.py) — `_geometry-harness/planeassess/test_verdict.py`
- [Does the planes report candidate joins diagnostic satisfy its expected contract?](#tool-viewprobe.planes-report-selftest) — `viewprobe.planes-report-selftest`
- [Does the planes-report reader join both schema versions correctly and refuse malformed data?](#tool-_geometry-harness-viewprobe-tools-test_planes_report_rows.py) — `_geometry-harness/viewprobe/tools/test_planes_report_rows.py`
- [Does the planner refuse a camera eye inside a solid of any thickness on a real render, and accept one beside a thin wall?](#tool-integration-housecarl-tests-test_eye_inside_solid.py) — `integration/housecarl/tests/test_eye_inside_solid.py`
- [Does the predictive occlusion mode read ownership, the 16:9 band, the answer key and the shadow scope correctly?](#tool-_geometry-harness-planeassess-test_predict.py) — `_geometry-harness/planeassess/test_predict.py`
- [Does the proposed narration preserve every required factual binding, and did the fidelity judge find its meaning kept?](#tool-evalreport.narrate-apply) — `evalreport.narrate-apply`
- [Does the reader's document list only clashes between the two mods and give the right install advice?](#tool-integration-housecarl-tests-test_readerdoc.py) — `integration/housecarl/tests/test_readerdoc.py`
- [Does the real reporting request-to-camera path satisfy its offline contract?](#tool-integration-housecarl-tests-test_capture_acquire.py) — `integration/housecarl/tests/test_capture_acquire.py`
- [Does the record capture read only what an evaluated plugin touches, reread when an arm's inputs change, and does a stopped sweep or a raised budget keep finished work?](#tool-_geometry-harness-tests-test_records_keep_work.py) — `_geometry-harness/tests/test_records_keep_work.py`
- [Does the record capture's selection refuse empty and short reads and keep unjudged rows apart?](#tool-_geometry-harness-tests-test_evalrecords.py) — `_geometry-harness/tests/test_evalrecords.py`
- [Does the record-lane comparator emit a row only for a lost change, within the schema's field scope?](#tool-integration-housecarl-tests-test_recordrows.py) — `integration/housecarl/tests/test_recordrows.py`
- [Does the record-lane comparator leave a field unjudged, never equal, when a read printed it without its values (an item as its type name, a list as its summary), while a field read with its values or by content hash stays judged?](#tool-_geometry-harness-tests-test_record_unexpanded_leaves.py) — `_geometry-harness/tests/test_record_unexpanded_leaves.py`
- [Does the record-lane comparator list every field it cannot judge on a houseCARL 2.0 capture (a cut-off, unread, unnamed or depth-mismatched field, a failed read, a tree answer that could not compare a plugin in full or whose plugin list was cut off), with its reason and plugins, and never drop it?](#tool-_geometry-harness-tests-test_record_2_0_unjudged.py) — `_geometry-harness/tests/test_record_2_0_unjudged.py`
- [Does the reel pipeline enforce media, audio, cache and clip-cut contracts?](#tool-integration-housecarl-tests-test_evalreel.py) — `integration/housecarl/tests/test_evalreel.py`
- [Does the reel say and show a declared mod name or plugin file name as written (the round 2 subjects' patches, one with an all-capital word), still put an undeclared editor ID into words, and leave a count of 0 out of the unpictured line?](#tool-integration-housecarl-tests-test_reel_names.py) — `integration/housecarl/tests/test_reel_names.py`
- [Does the render concurrency calculation refuse absent or insufficient memory readings?](#tool-_geometry-harness-viewprobe-tools-test_renderlimit.py) — `_geometry-harness/viewprobe/tools/test_renderlimit.py`
- [Does the report and reel say a name ending in s with a bare apostrophe ("Interesting NPCs'"), a landscape record by the ground of its cell, a patch named after the evaluated mod by the rest of its name, a long plugin file name in full once with a gloss and short after, and a buried object by its item's words?](#tool-integration-housecarl-tests-test_report_words.py) — `integration/housecarl/tests/test_report_words.py`
- [Does the report and reel say why a clash was not captured (no camera for its kind, or the capture limit), say each patch fact in one wording, list each Nexus patch page once in What to do, keep form keys and editor IDs off still pages and report places, name both objects of a one-mark view, count navmesh links and groups in the intro, resolve evalpipeline's path arguments at parse time, and read the needs-some sentence in short sentences, and say in the reel the place the report names?](#tool-integration-housecarl-tests-test_report_reel_wording.py) — `integration/housecarl/tests/test_report_reel_wording.py`
- [Does the report compiler match the constructed bundle oracle?](#tool-integration-housecarl-tests-test_evalreport.py) — `integration/housecarl/tests/test_evalreport.py`
- [Does the report distinguish removing a mod from repairing a conflict?](#tool-integration-housecarl-tests-test_evalreport_comparison_kinds.py) — `integration/housecarl/tests/test_evalreport_comparison_kinds.py`
- [Does the report lead with what adding the mods will break, then coverage, then the patch?](#tool-integration-housecarl-tests-test_evalreport_prediction.py) — `integration/housecarl/tests/test_evalreport_prediction.py`
- [Does the report overlay policy diagnostic satisfy its expected contract?](#tool-viewprobe.report-mode-smoke) — `viewprobe.report-mode-smoke`
- [Does the report say each record in words (a placed object by its kind and base, a name, an editor ID in words, a navmesh by its place), keep two different values apart and number records named alike, say once per check across mod pairs what to check in game, keep a patch verdict's names out of the reel, say why a cell could not be read, and say a long mod name in full once and then by its own short form?](#tool-integration-housecarl-tests-test_report_record_labels.py) — `integration/housecarl/tests/test_report_record_labels.py`
- [Does the report show the frame that states a clash (the marked view, both halves of a split), and does the reel drop capital mod prefixes from names?](#tool-integration-housecarl-tests-test_report_figures.py) — `integration/housecarl/tests/test_report_figures.py`
- [Does the requested canvas aspect reach every worklist and capture path?](#tool-_geometry-harness-tests-test_worklist_aspect.py) — `_geometry-harness/tests/test_worklist_aspect.py`
- [Does the review reel plan and render Piranha's vignette format by rule: opening still, without/with/highlight vignettes, data stills, plain words, own-pair patch sentences and a closing still?](#tool-integration-housecarl-tests-test_reel_vignettes.py) — `integration/housecarl/tests/test_reel_vignettes.py`
- [Does the route-cut check read each arm's candidate and only the targets that arm loads?](#tool-_geometry-harness-tests-test_route_cut_adapter.py) — `_geometry-harness/tests/test_route_cut_adapter.py`
- [Does the run controller still write the JSON bytes, hashes, orderings and stage ledger its known answers record?](#tool-runcontrol) — `runcontrol`
- [Does the runtime switch reading rule classify crashes, stalls and completions correctly?](#tool-integration-housecarl-tests-test_switchflip_outcome.py) — `integration/housecarl/tests/test_switchflip_outcome.py`
- [Does the scene preflight refuse an arm whose view lacks meshes an installed folder outside it has, and pass the controls?](#tool-_geometry-harness-tests-test_plan_view_mesh_gaps.py) — `_geometry-harness/tests/test_plan_view_mesh_gaps.py`
- [Does the scratch refusal propagation diagnostic satisfy its expected contract?](#tool-viewprobe.extraction-scratch-fail) — `viewprobe.extraction-scratch-fail`
- [Does the setup without the evaluated mod show the navmesh comparison's broken crossing at the same camera?](#tool-capture-acquire.navmesh-control) — `capture-acquire.navmesh-control`
- [Does the surface-crossing primitive match hand-built known answers?](#tool-crossprobe) — `crossprobe`
- [Does the unreadable mesh classification diagnostic satisfy its expected contract?](#tool-viewprobe.unreadable-asset-smoke) — `viewprobe.unreadable-asset-smoke`
- [Does the unreadable plugin refusal diagnostic satisfy its expected contract?](#tool-viewprobe.plugin-read-selftest) — `viewprobe.plugin-read-selftest`
- [Does the visibility mask evidence diagnostic satisfy its expected contract?](#tool-viewprobe.mask-evidence-selftest) — `viewprobe.mask-evidence-selftest`
- [Does the winning reference selection diagnostic satisfy its expected contract?](#tool-viewprobe.winning-refs-selftest) — `viewprobe.winning-refs-selftest`
- [Does this camera point sit inside placed physical geometry?](#tool-viewprobe.inside-at) — `viewprobe.inside-at`
- [Does this captured subject satisfy the identity, visibility and normal-view witness requirements?](#tool-subject-gate) — `subject-gate`
- [Does this existing camera satisfy the subject visibility contract?](#tool-shotplan.check) — `shotplan.check`
- [Does viewprobe --route-solve accept a solved leg that drops down a walked ledge, across the ledge's plan gap, and still refuse a leg that really leaves the mesh?](#tool-_geometry-harness-tests-test_route_solve_ledge_drop.py) — `_geometry-harness/tests/test_route_solve_ledge_drop.py`
- [Does viewprobe --route-solve report the ledge, enable/disable, unknown-type and held cross-mesh link counts, and name the held links when a pair has no path?](#tool-_geometry-harness-tests-test_ledge_link_counts.py) — `_geometry-harness/tests/test_ledge_link_counts.py`
- [Does viewprobe judge a shape solid by its own data rather than by an effects\ folder, on a real render: lit effects meshes solid, hidden and refraction shapes not, an unreadable effects mesh counted?](#tool-_geometry-harness-tests-test_effects_path_rule.py) — `_geometry-harness/tests/test_effects_path_rule.py`
- [Does viewprobe's occlusion rule admit and refuse primitive orientations as its rule file states?](#tool-_geometry-harness-viewprobe-tools-test_occlusion_rule.py) — `_geometry-harness/viewprobe/tools/test_occlusion_rule.py`
- [From which camera stations is a plane the patch changed in view and within reach, and how many plane edits can an offline occlusion render decide?](#tool-planeassess) — `planeassess`
- [Has a moved or missing support stranded another object, and what contact or overlap-volume evidence exists?](#tool-contactprobe) — `contactprobe`
- [Has another mod's terrain left a placed object floating in the air or buried in the ground?](#tool-contactprobe) — `contactprobe`
- [Has this machine chosen its evaluation settings, and if not, can the user choose now?](#tool-evalsettings.ensure) — `evalsettings.ensure`
- [How are the review reel's opening, load-order, unpictured-count and closing stills and a split's label strip drawn, and in which font?](#tool-reel-stills) — `reel-stills`
- [How can I acquire a verified camera for one evidence-bound reporting request?](#tool-capture-acquire) — `capture-acquire`
- [How can I answer shotplan's geometry queries from a declared toy scene?](#tool-shotplan-test-fixture) — `shotplan-test-fixture`
- [How can I build the small plugin and registry fixtures for the evaluation controller?](#tool-_geometry-harness-tests-fixtures-evalrun-make_fixtures.py) — `_geometry-harness/tests/fixtures/evalrun/make_fixtures.py`
- [How can I construct controlled shot variations for a batch-isolation test?](#tool-rendertwin.makelist) — `rendertwin.makelist`
- [How can I create report fixtures by driving the actual evaluation controller?](#tool-_geometry-harness-tests-fixtures-evalreport-make_report_fixtures.py) — `_geometry-harness/tests/fixtures/evalreport/make_report_fixtures.py`
- [How can I crop, compare or inspect render pixels and metadata?](#tool-imgcheck) — `imgcheck`
- [How can I export these worklist shots as game console batches?](#tool-viewprobe.emit-bats) — `viewprobe.emit-bats`
- [How can I frame a subject intentionally excluded from depth testing?](#tool-shotplan.nodepth) — `shotplan.nodepth`
- [How can I inspect this winning scene interactively?](#tool-viewprobe.interactive) — `viewprobe.interactive`
- [How can I make controlled fake detectors emit the real conflict-file format?](#tool-_geometry-harness-tests-fixtures-evalrun-fakeprobe.py) — `_geometry-harness/tests/fixtures/evalrun/fakeprobe.py`
- [How can I plan and execute a static render campaign while preserving shot identity and resuming receipts?](#tool-_geometry-harness-viewprobe-tools-renderbatch.py) — `_geometry-harness/viewprobe/tools/renderbatch.py`
- [How can I rank and group overlap findings into local incidents while retaining every member?](#tool-overlap-triage) — `overlap-triage`
- [How can I read annotated overlap incidents as a finding catalogue?](#tool-evalreport.triage) — `evalreport.triage`
- [How can I read candidate rows exactly from either planes-report schema?](#tool-_geometry-harness-viewprobe-tools-planes_report_rows.py) — `_geometry-harness/viewprobe/tools/planes_report_rows.py`
- [How can I read world coordinates from a top-down view?](#tool-viewprobe.grid) — `viewprobe.grid`
- [How can I render report evidence without inspection overlays?](#tool-viewprobe.report-mode) — `viewprobe.report-mode`
- [How can I render the same declared shots reproducibly?](#tool-viewprobe.worklist) — `viewprobe.worklist`
- [How can I substitute adviceprobe in evaluation-controller fixture tests?](#tool-_geometry-harness-tests-fixtures-evalrun-fakeprobes-adviceprobe.py) — `_geometry-harness/tests/fixtures/evalrun/fakeprobes/adviceprobe.py`
- [How can I substitute blockprobe in evaluation-controller fixture tests?](#tool-_geometry-harness-tests-fixtures-evalrun-fakeprobes-blockprobe.py) — `_geometry-harness/tests/fixtures/evalrun/fakeprobes/blockprobe.py`
- [How can I substitute bosprobe in evaluation-controller fixture tests?](#tool-_geometry-harness-tests-fixtures-evalrun-fakeprobes-bosprobe.py) — `_geometry-harness/tests/fixtures/evalrun/fakeprobes/bosprobe.py`
- [How can I substitute contactprobe in evaluation-controller fixture tests?](#tool-_geometry-harness-tests-fixtures-evalrun-fakeprobes-contactprobe.py) — `_geometry-harness/tests/fixtures/evalrun/fakeprobes/contactprobe.py`
- [How can I substitute driftprobe in evaluation-controller fixture tests?](#tool-_geometry-harness-tests-fixtures-evalrun-fakeprobes-driftprobe.py) — `_geometry-harness/tests/fixtures/evalrun/fakeprobes/driftprobe.py`
- [How can I substitute navownprobe in evaluation-controller fixture tests?](#tool-_geometry-harness-tests-fixtures-evalrun-fakeprobes-navownprobe.py) — `_geometry-harness/tests/fixtures/evalrun/fakeprobes/navownprobe.py`
- [How can I substitute overlapprobe in evaluation-controller fixture tests?](#tool-_geometry-harness-tests-fixtures-evalrun-fakeprobes-overlapprobe.py) — `_geometry-harness/tests/fixtures/evalrun/fakeprobes/overlapprobe.py`
- [How can I substitute patchprobe in evaluation-controller fixture tests?](#tool-_geometry-harness-tests-fixtures-evalrun-fakeprobes-patchprobe.py) — `_geometry-harness/tests/fixtures/evalrun/fakeprobes/patchprobe.py`
- [How can I substitute scopeprobe in evaluation-controller fixture tests?](#tool-_geometry-harness-tests-fixtures-evalrun-fakeprobes-scopeprobe.py) — `_geometry-harness/tests/fixtures/evalrun/fakeprobes/scopeprobe.py`
- [How can I substitute stateprobe in evaluation-controller fixture tests?](#tool-_geometry-harness-tests-fixtures-evalrun-fakeprobes-stateprobe.py) — `_geometry-harness/tests/fixtures/evalrun/fakeprobes/stateprobe.py`
- [How can I substitute switchprobe in evaluation-controller fixture tests?](#tool-_geometry-harness-tests-fixtures-evalrun-fakeprobes-switchprobe.py) — `_geometry-harness/tests/fixtures/evalrun/fakeprobes/switchprobe.py`
- [How can I synthesize a local WAV from text using a Windows voice?](#tool-evalreel-speech) — `evalreel-speech`
- [How can I write the hand-derived expected evaluation outputs in a consistent schema?](#tool-_geometry-harness-tests-fixtures-evalrun-expected-make_expected.py) — `_geometry-harness/tests/fixtures/evalrun/expected/make_expected.py`
- [How can a test stand in for the browser on the settings window?](#tool-integration-housecarl-tests-fixtures-evalsettings-stub_opener.py) — `integration/housecarl/tests/fixtures/evalsettings/stub_opener.py`
- [How can a test stand in for the machine's slot command with a real lock holder a hard kill leaves behind?](#tool-integration-housecarl-tests-fixtures-standin_hold.py) — `integration/housecarl/tests/fixtures/standin_hold.py`
- [How can camera planning use this profile’s actual projection?](#tool-shotplan.profile) — `shotplan.profile`
- [How can the names enrichment be tested without a houseCARL server?](#tool-_geometry-harness-tests-fixtures-evalrun-fake_records_mcp.py) — `_geometry-harness/tests/fixtures/evalrun/fake_records_mcp.py`
- [How can the record capture be tested without a houseCARL server?](#tool-_geometry-harness-tests-fixtures-evalrun-fake_capture_mcp.py) — `_geometry-harness/tests/fixtures/evalrun/fake_capture_mcp.py`
- [How do I add derived evidence to an evaluation bundle in Python?](#tool-evalrun) — `evalrun`
- [How do I add record rows or in-game names to an evaluation bundle without changing its results?](#tool-evalrun.enrich) — `evalrun.enrich`
- [How do I browse any load order's worldspaces, cells and placed objects as a tree with each record's winning plugin, from an MO2 profile or a game Data folder, and open a cell in 3D?](#tool-workbench) — `workbench`
- [How do I build SkyWright and its rulebook into the bin layout?](#tool-build) — `build`
- [How do I capture an evaluation's record checks (dangling references, contested fields, cell attributes, registration and dialogue, missing assets) for every arm?](#tool-evalrecords) — `evalrecords`
- [How do I capture every planned arm's mcp-only checks and write their import documents?](#tool-evalrecords.capture) — `evalrecords.capture`
- [How do I change one evaluation setting without the window?](#tool-evalsettings.set) — `evalsettings.set`
- [How do I continue one mod's evaluation from its evidence bundle to the report and reel?](#tool-evalpipeline) — `evalpipeline`
- [How do I derive an evaluation's disposition and STATUS.md with the C# controller?](#tool-runcontrol.finalize) — `runcontrol.finalize`
- [How do I derive and freeze an evaluation's check plan?](#tool-runcontrol.plan) — `runcontrol.plan`
- [How do I get top-down pictures of cells, as the renderer draws them, for a map?](#tool-viewprobe.map-tiles) — `viewprobe.map-tiles`
- [How do I look at a cell's placed references in a window, move or turn one, and write the change as a placement override?](#tool-workbench) — `workbench`
- [How do I make a narrated diagnostic reel or storyboard from an evaluation presentation?](#tool-evalreel) — `evalreel`
- [How do I open a finished evaluation's results in a window and see what the patches addressed, did not address and changed that nothing predicted, each in its cell before or after the patch?](#tool-workbench) — `workbench`
- [How do I open an MO2 profile in place, pick mod folders and probes, run them and review the results in the workbench, writing only under an Output Folder I choose?](#tool-workbench) — `workbench`
- [How do I plan, write and read back a navmesh edit or a navownprobe repair plan as a new patch plugin?](#tool-navedit) — `navedit`
- [How do I prepare local narration audio for the diagnostic reel?](#tool-evalspeech) — `evalspeech`
- [How do I produce Markdown, HTML, PDF, and an evidence index from the presentation plan?](#tool-evalreport.report) — `evalreport.report`
- [How do I record a Nexus patch search for one plugin pair, with every request and response kept, in the shape the review reel and report read?](#tool-patchsearch) — `patchsearch`
- [How do I render a plane-patch group's kept gazes in one arm under the campaign memory caps, one process at a time?](#tool-planeassess.render) — `planeassess.render`
- [How do I render a review-reel vignette's without, with and highlight frames at its acquired camera?](#tool-capture-acquire.capture) — `capture-acquire.capture`
- [How do I render the presentation as a diagnostic video reel?](#tool-evalreel.render) — `evalreel.render`
- [How do I reopen the evaluation settings window?](#tool-evalsettings.window) — `evalsettings.window`
- [How do I resume an evaluation with the C# controller, reusing what still matches the disk?](#tool-runcontrol.resume) — `runcontrol.resume`
- [How do I run a frozen plan's checks?](#tool-runcontrol.run) — `runcontrol.run`
- [How do I run an evaluation from census to a verified evidence bundle without Python on the machine?](#tool-runcontrol) — `runcontrol`
- [How do I run every check over a whole load order once, and get its leads with an added mod's view first?](#tool-orderpass) — `orderpass`
- [How do I run every stage after the bundle of one mod's evaluation not yet accepted, in order?](#tool-evalpipeline.run) — `evalpipeline.run`
- [How do I run one mod's evaluation from census to its verified evidence bundle?](#tool-runcontrol.run) — `runcontrol.run`
- [How do I select many newcomers against one order while reading the order only once?](#tool-selectprobe.batch) — `selectprobe.batch`
- [How do I sweep alternative load orders through the swept probes with the C# controller?](#tool-runcontrol.ordersweep) — `runcontrol.ordersweep`
- [How do I turn a presentation plan into a short reader's page in ordinary words?](#tool-readerdoc) — `readerdoc`
- [How do I turn a verified evaluation bundle into reports, narration packets and an evidence index?](#tool-evalreport) — `evalreport`
- [How do I verify an evidence bundle from its own files with the C# controller?](#tool-runcontrol.verify-bundle) — `runcontrol.verify-bundle`
- [How do I write an explicit defaults file?](#tool-evalsettings.defaults) — `evalsettings.defaults`
- [How do I write the population file for one patch plugin and its mod folder?](#tool-runcontrol.population) — `runcontrol.population`
- [How do SkyWright's Python tools reach houseCARL's run-log adapter?](#tool-scripts-runlog.py) — `scripts/runlog.py`
- [How do the real detectors compare across candidate load orders?](#tool-ordersweep) — `ordersweep`
- [How do these worklist shots map to DevBench camera arguments?](#tool-shotplan.devbench) — `shotplan.devbench`
- [How do two plugin navmesh versions differ visually?](#tool-viewprobe.compare) — `viewprobe.compare`
- [How does a predicted occlusion clash list score against an existing patch (hit, open, blind spot)?](#tool-planeassess.answerkey) — `planeassess.answerkey`
- [How does an MCP client (Claude Code, Codex) read and drive the conflict review window: open a conflict, stage a move, write it and read the re-check?](#tool-workbench) — `workbench`
- [How does every candidate patch change the detected conflict set?](#tool-differential-sweep) — `differential-sweep`
- [How does the capture stage hold the machine slot once across all its rounds?](#tool-evalpipeline.capture-rounds) — `evalpipeline.capture-rounds`
- [How does the view look with the objects standing in front of a clash hidden?](#tool-viewprobe.hide) — `viewprobe.hide`
- [How far is an object from the nearest winning navmesh triangle (reach <Cell> <subjects.json> --json OUT)?](#tool-floorgapprobe) — `floorgapprobe`
- [How far is the nearest actor-solid surface from a standing body's column at a point (clear <Cell> <file>)?](#tool-floorgapprobe) — `floorgapprobe`
- [How far is the winning navmesh above or below the physical floor?](#tool-floorgapprobe) — `floorgapprobe`
- [How is a navmesh comparison's camera measured, accepted and refused?](#tool-capture-navmesh) — `capture-navmesh`
- [How is a plugin matched to its Nexus mod page from the MO2 meta.ini and the Nexus catalogue?](#tool-modnames) — `modnames`
- [How is one live switch-flip launch classified, and when does an arm's reading settle?](#tool-switchflip_outcome) — `switchflip_outcome`
- [How is the capture-fallback fixture laid out as an MO2 instance over a game's archives?](#tool-_geometry-harness-tests-capture_fixture_lab.py) — `_geometry-harness/tests/capture_fixture_lab.py`
- [How many of a capture's arms read at once, from what memory, and what happens to an arm stopped or refused beside others?](#tool-evalrecords_workers) — `evalrecords_workers`
- [How many pixels of which mod's content does each occlusion plane hide in one viewprobe planes report?](#tool-_geometry-harness-planeassess-occlscore.py) — `_geometry-harness/planeassess/occlscore.py`
- [How many renders can fit side by side given measured process needs and current memory?](#tool-renderlimit) — `renderlimit`
- [How much Havok collision can be decoded, and how does its unit scale compare with render geometry?](#tool-collprobe) — `collprobe`
- [Is a clash that appears only with the supplied patches loaded pictured with them loaded, beside the setup without them, and said as such in the reel and the report; is one world-state check's new clashes counted once; and does a terrain station's label reach the picture's caption and reel note?](#tool-integration-housecarl-tests-test_patch_clash_pictures.py) — `integration/housecarl/tests/test_patch_clash_pictures.py`
- [Is a compatibility patch installed or available elsewhere on this machine?](#tool-patchprobe) — `patchprobe`
- [Is a navmesh clash item 24's camera can draw pictured in the review reel as a vignette of its two navmesh states, bound to its capture request, captured by the pipeline with its control in the setup without the mod, refused without a clean control at the same camera, held for its narration, shown as one report figure with its own captions, and said with the control's result and no form key?](#tool-integration-housecarl-tests-test_navmesh_reel.py) — `integration/housecarl/tests/test_navmesh_reel.py`
- [Is a same-named plugin from another folder planned and reported as itself, not as the measured copy?](#tool-integration-housecarl-tests-test_same_name_variant.py) — `integration/housecarl/tests/test_same_name_variant.py`
- [Is a selection whose only effect is its meshes evaluated in place: do object overlap and placement contact apply when a selected folder's mesh wins for a placed object and differs (with a plugin that places nothing or none), does a mesh-only mod get its new crossings, and is the scope line built from the mesh reach?](#tool-_geometry-harness-tests-test_asset_only_selection.py) — `_geometry-harness/tests/test_asset_only_selection.py`
- [Is the machine idle enough to launch the game or time a heavy job?](#tool-machineidle) — `machineidle`
- [Is this evaluation settings file valid?](#tool-evalsettings.validate) — `evalsettings.validate`
- [Is this flip plan one the live driver can honour?](#tool-switchflip.validate) — `switchflip.validate`
- [Is this proposed eye admitted by the retained depth survey?](#tool-depthsurvey.lookup) — `depthsurvey.lookup`
- [Is viewprobe --route-solve's path taut through the navmesh, with no corner emitted twice and every interior corner inset?](#tool-_geometry-harness-tests-test_route_geometry.py) — `_geometry-harness/tests/test_route_geometry.py`
- [Was the geometry behind an occlusion panel moved, removed or replaced by the other mod?](#tool-leftbehindprobe) — `leftbehindprobe`
- [What appears or disappears when an enable-parent switch flips?](#tool-viewprobe.state) — `viewprobe.state`
- [What are the depth values behind this rendered frame?](#tool-viewprobe.depth-dump) — `viewprobe.depth-dump`
- [What are the widths of these navmesh corridors, and how do live paths compare with the route rule?](#tool-corridorprobe) — `corridorprobe`
- [What character appearance and runtime outfit occupy this pose?](#tool-viewprobe.npc) — `viewprobe.npc`
- [What does Base Object Swapper change in this scene?](#tool-viewprobe.bos) — `viewprobe.bos`
- [What does a lower storey look like with upper geometry sliced away?](#tool-viewprobe.cull-above) — `viewprobe.cull-above`
- [What does each candidate mod touch, which edit kinds does it and its key patches make, and which assets does it override?](#tool-modcensus.run) — `modcensus.run`
- [What does each edited plane hide, by owner, in each rendered view of a plane-patch group?](#tool-planeassess.score) — `planeassess.score`
- [What does the game do when this block's switch flips run under one lock?](#tool-switchflip.batch) — `switchflip.batch`
- [What does the geometry around a subject look like when every eye-level camera is refused?](#tool-shotplan.fallback) — `shotplan.fallback`
- [What does the geometry detector report on the vanilla calibration population?](#tool-navprobe) — `navprobe`
- [What does the script bound to this switch actually do?](#tool-switchprobe.script) — `switchprobe.script`
- [What does the survey's measured free region look like at plan scale?](#tool-depthsurvey.figure) — `depthsurvey.figure`
- [What does the target plugin contain, and which measured scope should drive evaluation?](#tool-scopeprobe) — `scopeprobe`
- [What does this cell's winning geometry look like, why is a reference not drawn, and what changes under a named state?](#tool-viewprobe) — `viewprobe`
- [What does this placed object touch and rest on, from its render triangles?](#tool-viewprobe.contacts-of) — `viewprobe.contacts-of`
- [What free space does an outward six-face depth survey measure here?](#tool-depthsurvey.survey) — `depthsurvey.survey`
- [What geometry does the occlusion preview cull?](#tool-viewprobe.occlusion) — `viewprobe.occlusion`
- [What import document carries one arm's live launches into an evaluation?](#tool-switchflip.export) — `switchflip.export`
- [What is a record's editor ID in a plugin file (its EDID), for a FormID a value names?](#tool-pluginread) — `pluginread`
- [What is the identity of the program an evaluation runs: a probe's Release folder, a --probe-cmd override, or the controller's scripts?](#tool-toolidentity) — `toolidentity`
- [What is the labelled sliced top-down of these subjects, without an eye-level search?](#tool-shotplan.top-down-only) — `shotplan.top-down-only`
- [What is the package identity digest of a probe's or the controller's build folder, without starting Python?](#tool-runcontrol) — `runcontrol`
- [What is the winning placement state without rendering frames?](#tool-viewprobe.winning-refs-dump) — `viewprobe.winning-refs-dump`
- [What moves in this mesh, through which animations, and what space does the moving part sweep?](#tool-motionprobe) — `motionprobe`
- [What ordering rules does the mod author's catalogue text actually support?](#tool-adviceprobe) — `adviceprobe`
- [What placed geometry surrounds this candidate camera point?](#tool-viewprobe.clear-at) — `viewprobe.clear-at`
- [What placement records does the extractor read from this plugin or cell?](#tool-placeprobe) — `placeprobe`
- [What presentation plan can be built from this verified evaluation bundle?](#tool-evalreport.plan) — `evalreport.plan`
- [What verdict does a plane patch's edit get from its scored views in the two arms?](#tool-_geometry-harness-planeassess-verdict.py) — `_geometry-harness/planeassess/verdict.py`
- [What verdict does each plane edit of a plane patch get from its rendered views?](#tool-planeassess.verdicts) — `planeassess.verdicts`
- [What walkable path connects these route endpoints?](#tool-viewprobe.route-solve) — `viewprobe.route-solve`
- [What world position lies under this frame pixel?](#tool-viewprobe.pick) — `viewprobe.pick`
- [When a plant made of alpha cards misses its mark at the composed camera, is the witness station's own camera tried before the top-down and kept only when the plant's point lands in its mark, is such a picture captioned as a station view, is a clash a station saw never said to be one no camera could see, does a kept image key never stand for a capture with other frames, are plugin names in capture requests compared without case, is a one-mark marked view named, does the report stage's media bundle use the frozen check registry, does the reel narration speak no form key or editor ID, do capture frames address the cell by its form key, and does every capture refusal code have its own reason?](#tool-integration-housecarl-tests-test_alpha_card_capture.py) — `integration/housecarl/tests/test_alpha_card_capture.py`
- [When motionprobe sweep or deltacheck, or contactprobe's swept review, runs out of memory, does it say so in one sentence that says what to try, and does that advice work?](#tool-_geometry-harness-tests-test_swept_oom_sentence.py) — `_geometry-harness/tests/test_swept_oom_sentence.py`
- [Where are the built probe, runcontrol and server programs for the Python tools?](#tool-buildlayout) — `buildlayout`
- [Where are the occlusion primitives in this scene?](#tool-viewprobe.planes) — `viewprobe.planes`
- [Where can I put a camera that verifiably sees these subjects?](#tool-shotplan) — `shotplan`
- [Where can a verified camera see all these subjects?](#tool-shotplan.plan) — `shotplan.plan`
- [Where do neighbouring landscape vertices disagree?](#tool-viewprobe.seams) — `viewprobe.seams`
- [Where do one navmesh's edges cross into another, and does the target triangle each crossing names exist?](#tool-viewprobe.nav-link) — `viewprobe.nav-link`
- [Where does a plugin's version of a placed reference stand (its DATA position and rotation)?](#tool-pluginread) — `pluginread`
- [Where does a see-through object (effect, blended or alpha-tested) visibly cover this frame?](#tool-viewprobe.coverage-of) — `viewprobe.coverage-of`
- [Where does the review reel get each mod's name, and how does it say a name is not the Nexus one?](#tool-modnames) — `modnames`
- [Where does this declared walking route cross the scene?](#tool-viewprobe.route) — `viewprobe.route`
- [Where is SkyWright's checkout, seen from the houseCARL integration module?](#tool-skywright_paths) — `skywright_paths`
- [Where is the evaluation settings file?](#tool-evalsettings.path) — `evalsettings.path`
- [Where is this ground texture painted in the worldspace?](#tool-viewprobe.find-ltex) — `viewprobe.find-ltex`
- [Where no navmesh stands an exterior camera, does it stand on bare terrain only when the LAND footprint is planar and at most the measured 43.7 degrees, with nothing beside, overhead or under it but the ground, while navmesh stations keep today's rule?](#tool-integration-housecarl-tests-test_terrain_stations.py) — `integration/housecarl/tests/test_terrain_stations.py`
- [Where on each object of a capture request can an eye-level camera outside the pair actually see a face?](#tool-capture-points) — `capture-points`
- [Which DATA flags (interior, has water, show sky) does a plugin's version of a cell record set?](#tool-pluginread) — `pluginread`
- [Which Euler rotation composition agrees with the measured geometry?](#tool-eulerprobe) — `eulerprobe`
- [Which Mutagen members expose the quest-to-objective-to-alias dependency graph?](#tool-questprobe) — `questprobe`
- [Which NIF transform and collision types and members exist in the pinned Nifly API?](#tool-niflyprobe) — `niflyprobe`
- [Which agent runs the model an evaluation's models.* setting names?](#tool-evalsettings.route) — `evalsettings.route`
- [Which archive listings did a patch search not read, and which plugin inside a hub file did it match?](#tool-patchsearch) — `patchsearch`
- [Which arms and load orders does an entry derive?](#tool-runcontrol.arms) — `runcontrol.arms`
- [Which base-object swaps apply, and how do the parsed rules compare with the game's log?](#tool-bosprobe) — `bosprobe`
- [Which bounded alternative load orders satisfy the stated constraints?](#tool-orderprobe) — `orderprobe`
- [Which camera sees this measured surface target without occlusion?](#tool-shotplan.surface) — `shotplan.surface`
- [Which camera stations cover this cell route?](#tool-viewprobe.stations) — `viewprobe.stations`
- [Which candidate mods touch record types, placements, edit kinds or asset overrides the shakedown has not yet evaluated?](#tool-modcensus) — `modcensus`
- [Which census candidates have features the mods already evaluated lack?](#tool-modcensus.novelty) — `modcensus.novelty`
- [Which choices does the worldspace compatibility evaluation use on this machine, and how do I change them?](#tool-evalsettings) — `evalsettings`
- [Which clashes between mods did a whole-order pass find for an added mod, at a place, or between two plugins, and where do I look first?](#tool-orderleads) — `orderleads`
- [Which clashes does the review reel show, as vignettes or data stills, and what does it say about each?](#tool-reel-plan) — `reel-plan`
- [Which code adds evidence derived after measurement to an evaluation bundle, and how is it verified?](#tool-bundleenrich) — `bundleenrich`
- [Which conflicts did a candidate patch remove, preserve or introduce?](#tool-conflictdiff) — `conflictdiff`
- [Which contact and route findings change in the selected state?](#tool-stateprobe.selected) — `stateprobe.selected`
- [Which contact or route findings appear or disappear when a world-state switch flips?](#tool-stateprobe) — `stateprobe`
- [Which dangling-reference findings are already present in the base-game floor?](#tool-floorprobe) — `floorprobe`
- [Which declared placement dependencies lost or moved their hosts?](#tool-depprobe) — `depprobe`
- [Which doors does a plugin's version of a navmesh record link (its door triangles)?](#tool-pluginread) — `pluginread`
- [Which downloaded archives contain compatibility patches that were never installed?](#tool-poolprobe) — `poolprobe`
- [Which effective inputs identify this renderer configuration?](#tool-viewprobe.config-identity) — `viewprobe.config-identity`
- [Which enable-parent switches and trigger bindings affect this cell?](#tool-switchprobe.census) — `switchprobe.census`
- [Which enable-parent switches gate this cell, and which bound scripts or quest stages flip them?](#tool-switchprobe) — `switchprobe`
- [Which evidence-bound views should an evaluation report ask to capture?](#tool-report-scenes) — `report-scenes`
- [Which findings change across every enumerated switch flip?](#tool-stateprobe.all) — `stateprobe.all`
- [Which fixture do the capture-fallback tests render?](#tool-viewprobe.write-capture-fixture) — `viewprobe.write-capture-fixture`
- [Which helpers do the evaluation tools on both sides of the houseCARL integration fence share?](#tool-evalbase) — `evalbase`
- [Which installed plugins should one new mod be evaluated against, and why is each other plugin left out?](#tool-selectprobe) — `selectprobe`
- [Which installer patch options were offered but not selected?](#tool-fomodprobe) — `fomodprobe`
- [Which kinds of edit does a patch make to a record, in the fixed vocabulary (ADD, DISABLE, MOVE, ENABLE-PARENT, FIELD:<name> and the rest)?](#tool-editkinds) — `editkinds`
- [Which leads does a pass hold for this mod, this cell or these two plugins?](#tool-orderleads.read) — `orderleads.read`
- [Which lights does a load order place in an interior cell, with what radius, shadow and portal-strict flags, and are they enabled at load?](#tool-lightprobe) — `lightprobe`
- [Which measured point on each participant does an eye-level camera see, and from which bearings?](#tool-capture-points.pick) — `capture-points.pick`
- [Which meshes contribute to this winning scene?](#tool-viewprobe.list-meshes) — `viewprobe.list-meshes`
- [Which navmesh belongs to the storey being inspected?](#tool-viewprobe.nav-z) — `viewprobe.nav-z`
- [Which numbers and names may a narrator use for each finding?](#tool-evalreport.narrate-packet) — `evalreport.narrate-packet`
- [Which objects stand in front of this object in this view, and over how many of its pixels?](#tool-viewprobe.occluders-of) — `viewprobe.occluders-of`
- [Which occlusion candidates cover the measured frame geometry?](#tool-viewprobe.planes-report) — `viewprobe.planes-report`
- [Which occlusion planes will hide a mod's content when it is added to a load order, and which of its planes will hide content already there?](#tool-_geometry-harness-planeassess-predict.py) — `_geometry-harness/planeassess/predict.py`
- [Which occlusion planes will hide a mod's content when it is added to an order, and which of its planes will hide content already there?](#tool-planeassess.predict) — `planeassess.predict`
- [Which occlusion-plane edits does a plane patch make (new, moved or resized, disabled, restored), and over whose version?](#tool-planeassess) — `planeassess`
- [Which occlusion-plane edits does this patch make, and over whose version?](#tool-planeassess.patch) — `planeassess.patch`
- [Which overlap findings require both mods rather than already occurring in a source setup?](#tool-overlap-attribution) — `overlap-attribution`
- [Which patches sit on disk not enabled, which runtime files point at plugins or records the order lacks, which two mods' swaps meet on one base, which scripts lack a parent or called class, and what names an empty dummy plugin?](#tool-lookprobe) — `lookprobe`
- [Which placed objects overlap across mods under this configuration?](#tool-overlapprobe) — `overlapprobe`
- [Which placement and navmesh types and members exist in the pinned Mutagen API?](#tool-mutagenprobe) — `mutagenprobe`
- [Which planes, of any owner, hide which shapes in each rendered view, by owner and 16:9 band pixels?](#tool-planeassess.rescore) — `planeassess.rescore`
- [Which planned scenes need human review before video assembly?](#tool-evalreel.storyboard-packet) — `evalreel.storyboard-packet`
- [Which plugins and mod folders in a whole load order meet, and how: shared records, cells, neighbouring cells, placed bases, form links and asset paths compared by bytes?](#tool-ordermap) — `ordermap`
- [Which record fields does one mod change that another mod's version throws away, from a record capture?](#tool-recordrows) — `recordrows`
- [Which required artifact classes agree between these two render outputs?](#tool-renderequiv.compare) — `renderequiv.compare`
- [Which resolution and field of view does this game profile use?](#tool-shotplan.frame) — `shotplan.frame`
- [Which rotation composition produces the expected placed geometry?](#tool-viewprobe.euler) — `viewprobe.euler`
- [Which route camera stations does the deployed viewprobe sample for each worldspace of a plane-patch group?](#tool-planeassess.stations) — `planeassess.stations`
- [Which stages does a run folder's ledger hold as accepted?](#tool-runcontrol.status) — `runcontrol.status`
- [Which stages of an evaluation run are accepted, and where did it stop?](#tool-evalpipeline.status) — `evalpipeline.status`
- [Which state changes affect this particular contested cell?](#tool-stateprobe.cell) — `stateprobe.cell`
- [Which station gazes must be rendered to judge a plane patch's edits?](#tool-planeassess.shots) — `planeassess.shots`
- [Which switch flips, in which order and where, should the live switch check launch for an evaluation?](#tool-flipplan) — `flipplan`
- [Which switched world states does a Deep evaluation judge on an arm, and which can it not express?](#tool-stategen) — `stategen`
- [Which switches is the runtime state-switch check about on an arm, and is it not applicable there?](#tool-switchflip_scope) — `switchflip_scope`
- [Which terrain layers contribute to this rendered cell?](#tool-viewprobe.list-terrain) — `viewprobe.list-terrain`
- [Which triangles of other navmeshes does a plugin's version of a navmesh record link to (its edge links)?](#tool-pluginread) — `pluginread`
- [Which value does each evaluation option take, and from where?](#tool-evalsettings.show) — `evalsettings.show`
- [Which winning navmesh triangles cover this coordinate?](#tool-viewprobe.nav-at) — `viewprobe.nav-at`
- [Which worklist fields define the per-shot state that must be isolated?](#tool-rendertwin.fields) — `rendertwin.fields`
- [Who supplies the winning navmesh, and whose navmesh was overwritten?](#tool-navownprobe) — `navownprobe`
- [Why did the detectors miss these patch edits, and what remains unknown?](#tool-missdiag) — `missdiag`
- [Why is a reference near this position not drawn?](#tool-viewprobe.refs-at) — `viewprobe.refs-at`

## Tools and modes

<a id="tool-skywright.guards"></a>

### SkyWright.Guards

**Do SkyWright's known-answer suites pass, and do its run-coverage manifest and tool index match the tree?**

Run as part of SkyWright's checks after a change, before a commit that touches probes, tools or their rows.

Inputs: A SkyWright checkout with its build and the houseCARL submodule.

Outputs: Per-check PASS/FAIL lines and an exit code.

Source: [src/SkyWright.Guards/Program.cs](../src/SkyWright.Guards/Program.cs) — `// SkyWright.Guards <guard> [options]: one guard per run, exit 0 when it passes.`


<a id="tool-_geometry-harness-planeassess-occlscore.py"></a>

### _geometry-harness/planeassess/occlscore.py

**How many pixels of which mod's content does each occlusion plane hide in one viewprobe planes report?**

Score a planes report the way the offline occlusion assessment does: in-band visible culled pixels per plane, split by owner, with unassessed rows counted.

Inputs: A viewprobe planes report (v1 or v2, optionally gzipped) and the content mod's placed record keys.

Outputs: Per plane: in-band and native hidden pixels by owner, census of culled/unassessed rows, visibility status counts.

Source: [_geometry-harness/planeassess/occlscore.py](../_geometry-harness/planeassess/occlscore.py) — `The offline occlusion scorer for viewprobe planes reports, as a library`


<a id="tool-_geometry-harness-planeassess-predict.py"></a>

### _geometry-harness/planeassess/predict.py

**Which occlusion planes will hide a mod's content when it is added to a load order, and which of its planes will hide content already there?**

Predict a mod's occlusion clashes from planes reports of the order with it, and score the prediction against an existing patch as an answer key.

Inputs: Planes reports of the order with the mod, plane winners and content owners from the records; optionally the order without the mod and the patched order.

Outputs: Predicted findings per plane (shown or machine record) and an answer-key outcome per finding.

Source: [_geometry-harness/planeassess/predict.py](../_geometry-harness/planeassess/predict.py) — `Predictive occlusion for a mod being added to an order`


<a id="tool-_geometry-harness-planeassess-test_occlscore.py"></a>

### _geometry-harness/planeassess/test_occlscore.py

**Does the occlusion scorer library count only in-band, visible, plane-culled pixels and split them by owner?**

Check a change to the occlusion scorer against synthetic reports that each fail one wrong scorer.

Inputs: Controlled fixtures for test_occlscore.

Outputs: Assertion results or generated fixture data for: Does the occlusion scorer library count only in-band, visible, plane-culled pixels and split them by owner?

Source: [_geometry-harness/planeassess/test_occlscore.py](../_geometry-harness/planeassess/test_occlscore.py) — `Known answers for the occlusion scorer library on synthetic reports`


<a id="tool-_geometry-harness-planeassess-test_planeassess.py"></a>

### _geometry-harness/planeassess/test_planeassess.py

**Does planeassess class plane edits, read placed primitives and test the frustum and rule shapes correctly?**

Check a change to the plane-patch records steps against known edit kinds, classes, a synthetic plugin and the geometry known answers.

Inputs: Controlled fixtures for test_planeassess.

Outputs: Assertion results or generated fixture data for: Does planeassess class plane edits, read placed primitives and test the frustum and rule shapes correctly?

Source: [_geometry-harness/planeassess/test_planeassess.py](../_geometry-harness/planeassess/test_planeassess.py) — `Known answers for planeassess's records steps`


<a id="tool-_geometry-harness-planeassess-test_predict.py"></a>

### _geometry-harness/planeassess/test_predict.py

**Does the predictive occlusion mode read ownership, the 16:9 band, the answer key and the shadow scope correctly?**

Check a change to the predictive mode against synthetic cases that each fail one wrong reading.

Inputs: Controlled fixtures for test_predict.

Outputs: Assertion results or generated fixture data for: Does the predictive occlusion mode read ownership, the 16:9 band, the answer key and the shadow scope correctly?

Source: [_geometry-harness/planeassess/test_predict.py](../_geometry-harness/planeassess/test_predict.py) — `Known answers for the predictive mode on synthetic inputs`


<a id="tool-_geometry-harness-planeassess-test_verdict.py"></a>

### _geometry-harness/planeassess/test_verdict.py

**Does the per-plane-edit verdict decide a new hide, a fix, the plane's owner, a visibility test that did not run and a held refusal correctly?**

Check a change to the plane-patch verdict rules against dict-only views that each fail one wrong rule.

Inputs: Controlled fixtures for test_verdict.

Outputs: Assertion results for: Does the per-plane-edit verdict decide a new hide, a fix, the plane's owner, a visibility test that did not run and a held refusal correctly?

Source: [_geometry-harness/planeassess/test_verdict.py](../_geometry-harness/planeassess/test_verdict.py) — `Known answers for the per-plane-edit verdict (verdict.decide) on dict-only views`


<a id="tool-_geometry-harness-planeassess-verdict.py"></a>

### _geometry-harness/planeassess/verdict.py

**What verdict does a plane patch's edit get from its scored views in the two arms?**

Decide each plane edit of a plane patch from scored u and p views (interface; the decision is filled at the reviewed gate).

Inputs: One plane edit with its versions and owners, and its scored views per gaze and arm.

Outputs: A verdict with its reason and whether it is shown to a reader.

Source: [_geometry-harness/planeassess/verdict.py](../_geometry-harness/planeassess/verdict.py) — `Per-plane-edit verdicts for a plane patch`


<a id="tool-_geometry-harness-tests-capture_fixture_lab.py"></a>

### _geometry-harness/tests/capture_fixture_lab.py

**How is the capture-fallback fixture laid out as an MO2 instance over a game's archives?**

Lay out the fixture for the capture-fallback tests, or render an overview of its cells by eye.

Inputs: A viewprobe build and a Skyrim SE game folder.

Outputs: The instance, candidates and (as a script) one top and one eye-level frame per cell.

Source: [_geometry-harness/tests/capture_fixture_lab.py](../_geometry-harness/tests/capture_fixture_lab.py) — `The capture-fallback fixture laid out as an MO2 instance`


<a id="tool-_geometry-harness-tests-fixtures-evalreport-make_report_fixtures.py"></a>

### _geometry-harness/tests/fixtures/evalreport/make_report_fixtures.py

**How can I create report fixtures by driving the actual evaluation controller?**

Refresh report-test inputs through the controller when the evaluation evidence schema changes.

Inputs: Controlled fixtures for make_report_fixtures.

Outputs: Assertion results or generated fixture data for: How can I create report fixtures by driving the actual evaluation controller?

Source: [_geometry-harness/tests/fixtures/evalreport/make_report_fixtures.py](../_geometry-harness/tests/fixtures/evalreport/make_report_fixtures.py) — `Build the evalreport fixtures: schema-2 bundles for cases R1-R7, made by driving the REAL controller`


Modifiers (do not select a separate mode):

- `--only` — one case by prefix (R6 also builds R1 and R5)
- `--out` — Set out for this invocation; consult the linked parser for accepted values.

<a id="tool-_geometry-harness-tests-fixtures-evalrun-expected-make_expected.py"></a>

### _geometry-harness/tests/fixtures/evalrun/expected/make_expected.py

**How can I write the hand-derived expected evaluation outputs in a consistent schema?**

Serialize the independent evaluation oracle after reviewing changes to the fixture expectations.

Inputs: Controlled fixtures for make_expected.

Outputs: Assertion results or generated fixture data for: How can I write the hand-derived expected evaluation outputs in a consistent schema?

Source: [_geometry-harness/tests/fixtures/evalrun/expected/make_expected.py](../_geometry-harness/tests/fixtures/evalrun/expected/make_expected.py) — `Dump the hand-derived oracle JSONs (one per case). The values are typed by hand from PLAN.md D1-D8;`


<a id="tool-_geometry-harness-tests-fixtures-evalrun-fake_capture_mcp.py"></a>

### _geometry-harness/tests/fixtures/evalrun/fake_capture_mcp.py

**How can the record capture be tested without a houseCARL server?**

Stand in for the record reader in evalrecords' capture tests.

Inputs: Controlled fixtures for fake_capture_mcp.

Outputs: Assertion results or generated fixture data for: How can the record capture be tested without a houseCARL server?

Source: [_geometry-harness/tests/fixtures/evalrun/fake_capture_mcp.py](../_geometry-harness/tests/fixtures/evalrun/fake_capture_mcp.py) — `A stand-in record reader for evalrecords' capture tests`


<a id="tool-_geometry-harness-tests-fixtures-evalrun-fake_records_mcp.py"></a>

### _geometry-harness/tests/fixtures/evalrun/fake_records_mcp.py

**How can the names enrichment be tested without a houseCARL server?**

Stand in for housecarl_records in evalrun's names enrichment case.

Inputs: Controlled fixtures for fake_records_mcp.

Outputs: Assertion results or generated fixture data for: How can the names enrichment be tested without a houseCARL server?

Source: [_geometry-harness/tests/fixtures/evalrun/fake_records_mcp.py](../_geometry-harness/tests/fixtures/evalrun/fake_records_mcp.py) — `A stand-in for the houseCARL MCP server's 'housecarl_records' read`


<a id="tool-_geometry-harness-tests-fixtures-evalrun-fakeprobe.py"></a>

### _geometry-harness/tests/fixtures/evalrun/fakeprobe.py

**How can I make controlled fake detectors emit the real conflict-file format?**

Exercise controller accounting offline with predetermined detector findings and error cases.

Inputs: Controlled fixtures for fakeprobe.

Outputs: Assertion results or generated fixture data for: How can I make controlled fake detectors emit the real conflict-file format?

Source: [_geometry-harness/tests/fixtures/evalrun/fakeprobe.py](../_geometry-harness/tests/fixtures/evalrun/fakeprobe.py) — `The fixture probe engine — a fake detector that writes the REAL conflict-file format.`


<a id="tool-_geometry-harness-tests-fixtures-evalrun-fakeprobes-adviceprobe.py"></a>

### _geometry-harness/tests/fixtures/evalrun/fakeprobes/adviceprobe.py

**How can I substitute adviceprobe in evaluation-controller fixture tests?**

Route the controller's ordering-advice invocation to controlled data during offline evaluation tests.

Inputs: Controlled fixtures for adviceprobe.

Outputs: Assertion results or generated fixture data for: How can I substitute adviceprobe in evaluation-controller fixture tests?

Source: [_geometry-harness/tests/fixtures/evalrun/fakeprobes/adviceprobe.py](../_geometry-harness/tests/fixtures/evalrun/fakeprobes/adviceprobe.py) — `Fixture stand-in for adviceprobe — see ../fakeprobe.py.`


<a id="tool-_geometry-harness-tests-fixtures-evalrun-fakeprobes-blockprobe.py"></a>

### _geometry-harness/tests/fixtures/evalrun/fakeprobes/blockprobe.py

**How can I substitute blockprobe in evaluation-controller fixture tests?**

Record the argv evalrun hands the route-cut check and print the lines its registry regexes read.

Inputs: EVALRUN_FAKE_ARGV_DIR, where each invocation's argv is written.

Outputs: Assertion results or generated fixture data for: How can I substitute blockprobe in evaluation-controller fixture tests?

Source: [_geometry-harness/tests/fixtures/evalrun/fakeprobes/blockprobe.py](../_geometry-harness/tests/fixtures/evalrun/fakeprobes/blockprobe.py) — `Fixture stand-in for blockprobe (route-cut)`


<a id="tool-_geometry-harness-tests-fixtures-evalrun-fakeprobes-bosprobe.py"></a>

### _geometry-harness/tests/fixtures/evalrun/fakeprobes/bosprobe.py

**How can I substitute bosprobe in evaluation-controller fixture tests?**

Supply a controlled swap-rule response when testing evaluation applicability and accounting.

Inputs: Controlled fixtures for bosprobe.

Outputs: Assertion results or generated fixture data for: How can I substitute bosprobe in evaluation-controller fixture tests?

Source: [_geometry-harness/tests/fixtures/evalrun/fakeprobes/bosprobe.py](../_geometry-harness/tests/fixtures/evalrun/fakeprobes/bosprobe.py) — `Fixture stand-in for bosprobe — see ../fakeprobe.py.`


<a id="tool-_geometry-harness-tests-fixtures-evalrun-fakeprobes-contactprobe.py"></a>

### _geometry-harness/tests/fixtures/evalrun/fakeprobes/contactprobe.py

**How can I substitute contactprobe in evaluation-controller fixture tests?**

Supply predetermined support findings while exercising the controller's contact-check arm.

Inputs: Controlled fixtures for contactprobe.

Outputs: Assertion results or generated fixture data for: How can I substitute contactprobe in evaluation-controller fixture tests?

Source: [_geometry-harness/tests/fixtures/evalrun/fakeprobes/contactprobe.py](../_geometry-harness/tests/fixtures/evalrun/fakeprobes/contactprobe.py) — `Fixture stand-in for contactprobe — see ../fakeprobe.py.`


<a id="tool-_geometry-harness-tests-fixtures-evalrun-fakeprobes-driftprobe.py"></a>

### _geometry-harness/tests/fixtures/evalrun/fakeprobes/driftprobe.py

**How can I substitute driftprobe in evaluation-controller fixture tests?**

Isolate controller behavior from real quest data with the drift fixture adapter.

Inputs: Controlled fixtures for driftprobe.

Outputs: Assertion results or generated fixture data for: How can I substitute driftprobe in evaluation-controller fixture tests?

Source: [_geometry-harness/tests/fixtures/evalrun/fakeprobes/driftprobe.py](../_geometry-harness/tests/fixtures/evalrun/fakeprobes/driftprobe.py) — `Fixture stand-in for driftprobe — see ../fakeprobe.py.`


<a id="tool-_geometry-harness-tests-fixtures-evalrun-fakeprobes-navownprobe.py"></a>

### _geometry-harness/tests/fixtures/evalrun/fakeprobes/navownprobe.py

**How can I substitute navownprobe in evaluation-controller fixture tests?**

Supply controlled navigation-ownership evidence to an offline evaluation plan.

Inputs: Controlled fixtures for navownprobe.

Outputs: Assertion results or generated fixture data for: How can I substitute navownprobe in evaluation-controller fixture tests?

Source: [_geometry-harness/tests/fixtures/evalrun/fakeprobes/navownprobe.py](../_geometry-harness/tests/fixtures/evalrun/fakeprobes/navownprobe.py) — `Fixture stand-in for navownprobe — see ../fakeprobe.py.`


<a id="tool-_geometry-harness-tests-fixtures-evalrun-fakeprobes-overlapprobe.py"></a>

### _geometry-harness/tests/fixtures/evalrun/fakeprobes/overlapprobe.py

**How can I substitute overlapprobe in evaluation-controller fixture tests?**

Exercise evaluation handling of overlap findings without loading real scene geometry.

Inputs: Controlled fixtures for overlapprobe.

Outputs: Assertion results or generated fixture data for: How can I substitute overlapprobe in evaluation-controller fixture tests?

Source: [_geometry-harness/tests/fixtures/evalrun/fakeprobes/overlapprobe.py](../_geometry-harness/tests/fixtures/evalrun/fakeprobes/overlapprobe.py) — `Fixture stand-in for overlapprobe — see ../fakeprobe.py.`


<a id="tool-_geometry-harness-tests-fixtures-evalrun-fakeprobes-patchprobe.py"></a>

### _geometry-harness/tests/fixtures/evalrun/fakeprobes/patchprobe.py

**How can I substitute patchprobe in evaluation-controller fixture tests?**

Supply controlled patch-discovery outcomes while testing the controller's decision path.

Inputs: Controlled fixtures for patchprobe.

Outputs: Assertion results or generated fixture data for: How can I substitute patchprobe in evaluation-controller fixture tests?

Source: [_geometry-harness/tests/fixtures/evalrun/fakeprobes/patchprobe.py](../_geometry-harness/tests/fixtures/evalrun/fakeprobes/patchprobe.py) — `Fixture stand-in for patchprobe — see ../fakeprobe.py.`


<a id="tool-_geometry-harness-tests-fixtures-evalrun-fakeprobes-scopeprobe.py"></a>

### _geometry-harness/tests/fixtures/evalrun/fakeprobes/scopeprobe.py

**How can I substitute scopeprobe in evaluation-controller fixture tests?**

Establish a synthetic target population for controller applicability tests.

Inputs: Controlled fixtures for scopeprobe.

Outputs: Assertion results or generated fixture data for: How can I substitute scopeprobe in evaluation-controller fixture tests?

Source: [_geometry-harness/tests/fixtures/evalrun/fakeprobes/scopeprobe.py](../_geometry-harness/tests/fixtures/evalrun/fakeprobes/scopeprobe.py) — `Fixture stand-in for scopeprobe — see ../fakeprobe.py.`


<a id="tool-_geometry-harness-tests-fixtures-evalrun-fakeprobes-stateprobe.py"></a>

### _geometry-harness/tests/fixtures/evalrun/fakeprobes/stateprobe.py

**How can I substitute stateprobe in evaluation-controller fixture tests?**

Exercise state-check accounting using predetermined transition findings.

Inputs: Controlled fixtures for stateprobe.

Outputs: Assertion results or generated fixture data for: How can I substitute stateprobe in evaluation-controller fixture tests?

Source: [_geometry-harness/tests/fixtures/evalrun/fakeprobes/stateprobe.py](../_geometry-harness/tests/fixtures/evalrun/fakeprobes/stateprobe.py) — `Fixture stand-in for stateprobe — see ../fakeprobe.py.`


<a id="tool-_geometry-harness-tests-fixtures-evalrun-fakeprobes-switchprobe.py"></a>

### _geometry-harness/tests/fixtures/evalrun/fakeprobes/switchprobe.py

**How can I substitute switchprobe in evaluation-controller fixture tests?**

Supply controlled trigger-map evidence before the offline controller plans its dependent checks.

Inputs: Controlled fixtures for switchprobe.

Outputs: Assertion results or generated fixture data for: How can I substitute switchprobe in evaluation-controller fixture tests?

Source: [_geometry-harness/tests/fixtures/evalrun/fakeprobes/switchprobe.py](../_geometry-harness/tests/fixtures/evalrun/fakeprobes/switchprobe.py) — `Fixture stand-in for switchprobe — see ../fakeprobe.py.`


<a id="tool-_geometry-harness-tests-fixtures-evalrun-make_fixtures.py"></a>

### _geometry-harness/tests/fixtures/evalrun/make_fixtures.py

**How can I build the small plugin and registry fixtures for the evaluation controller?**

Refresh the synthetic evaluation population after reviewing a controller schema or applicability change.

Inputs: Controlled fixtures for make_fixtures.

Outputs: Assertion results or generated fixture data for: How can I build the small plugin and registry fixtures for the evaluation controller?

Source: [_geometry-harness/tests/fixtures/evalrun/make_fixtures.py](../_geometry-harness/tests/fixtures/evalrun/make_fixtures.py) — `Build the evalrun fixtures: tiny plugins, a trimmed check registry, a scope.json and candidate files per`


Modifiers (do not select a separate mode):

- `--out` — where to write the generated fixtures (never the tracked tree)
- `--registry-from` — Set registry from for this invocation; consult the linked parser for accepted values.

<a id="tool-_geometry-harness-tests-test_adviceprobe.py"></a>

### _geometry-harness/tests/test_adviceprobe.py

**Does adviceprobe find a clash stated on a neighbour's page, and does the evaluation pass the neighbours and record the clash rows?**

Qualify a change to adviceprobe's --with pages, to S4 in _shared/OrderAdvice.cs (the controller's advice wiring is runcontrol's own `advice:` cases).

Inputs: adviceprobe built from this checkout; a scratch MO2 instance of two mods and two hand-written archived pages.

Outputs: Assertion results for: Does adviceprobe find a clash stated on a neighbour's page, and does the evaluation pass the neighbours and record the clash rows?

Source: [_geometry-harness/tests/test_adviceprobe.py](../_geometry-harness/tests/test_adviceprobe.py) — `Known answers for adviceprobe's neighbour pages ('--with') and the evaluation's wiring of them.`


<a id="tool-_geometry-harness-tests-test_asset_only_selection.py"></a>

### _geometry-harness/tests/test_asset_only_selection.py

**Is a selection whose only effect is its meshes evaluated in place: do object overlap and placement contact apply when a selected folder's mesh wins for a placed object and differs (with a plugin that places nothing or none), does a mesh-only mod get its new crossings, and is the scope line built from the mesh reach?**

Qualify a change to how runcontrol plans or evaluates a mesh-carrying selection (with or without a plugin), to the placedMeshes trigger, or to the scope line.

Inputs: PopulationFixture, written by overlapprobe --self-test; the Release runcontrol, scopeprobe, overlapprobe and conflictdiff.

Outputs: Assertion results for: Is a selection whose only effect is its meshes evaluated in place: do object overlap and placement contact apply when a selected folder's mesh wins for a placed object and differs (with a plugin that places nothing or none), does a mesh-only mod get its new crossings, and is the scope line built from the mesh reach?

Source: [_geometry-harness/tests/test_asset_only_selection.py](../_geometry-harness/tests/test_asset_only_selection.py) — `Known answers for a selection whose only effect on the world is its meshes`


<a id="tool-_geometry-harness-tests-test_buildlayout.py"></a>

### _geometry-harness/tests/test_buildlayout.py

**Does buildlayout.py's fallback marker equal the one the build writes, and do its paths exist in that build?**

Qualify a change to buildlayout.py or to the layout Directory.Build.props writes.

Inputs: The Release build and its skywright-layout.json.

Outputs: Assertion results for: Does buildlayout.py's fallback marker equal the one the build writes, and do its paths exist in that build?

Source: [_geometry-harness/tests/test_buildlayout.py](../_geometry-harness/tests/test_buildlayout.py) — `buildlayout.py against the build the solution wrote`


<a id="tool-_geometry-harness-tests-test_contact_memory_route.py"></a>

### _geometry-harness/tests/test_contact_memory_route.py

**Does contactprobe take the fast memory route when the memory is there, lean under a limit or when other programs hold the memory (warning by name, unattended default recorded), stop with exit 5 under --memory-route ask, and leave no emit or rows behind when it refuses or runs out of memory?**

Qualify a change to contactprobe's memory route (_shared/MemoryRoute.cs, the fit check), its sidecar, or its out-of-memory and stale-output handling. It starts the real probe several times (about 4.5 GB each): run it inside idle_lock hold.

Inputs: PopulationFixture, written by overlapprobe --self-test; the Release contactprobe; HC_MEMROUTE_TEST_READINGS and HC_CONTACT_FAULT for the contended and out-of-memory cases; DOTNET_GCHeapHardLimit for the absent case.

Outputs: Assertion results for: Does contactprobe take the fast memory route when the memory is there, lean under a limit or when other programs hold the memory (warning by name, unattended default recorded), stop with exit 5 under --memory-route ask, and leave no emit or rows behind when it refuses or runs out of memory?

Source: [_geometry-harness/tests/test_contact_memory_route.py](../_geometry-harness/tests/test_contact_memory_route.py) — `Known answers for contactprobe's memory route on a real probe run`


<a id="tool-_geometry-harness-tests-test_contact_reverse.py"></a>

### _geometry-harness/tests/test_contact_reverse.py

**Does contactprobe --reverse-of give the full reverse's emit, refuse a baseline emit that is not this run's own, and does conflictdiff refuse contactprobe emits (sweep, --terrain or --empty-space) measured under different or unknown rules, or of another mode?**

Qualify a change to contactprobe's reverse-from-baseline mode, the measurement policy of its sweep, --terrain or --empty-space emit, or the shared checks that guard them.

Inputs: PopulationFixture, written by overlapprobe --self-test; the Release contactprobe and conflictdiff.

Outputs: Assertion results for: Does contactprobe --reverse-of give the full reverse's emit, refuse a baseline emit that is not this run's own, and does conflictdiff refuse contactprobe emits (sweep, --terrain or --empty-space) measured under different or unknown rules, or of another mode?

Source: [_geometry-harness/tests/test_contact_reverse.py](../_geometry-harness/tests/test_contact_reverse.py) — `Known answers for contactprobe --reverse-of and the sweep emit's measurement policy`


<a id="tool-_geometry-harness-tests-test_contact_solid_rule.py"></a>

### _geometry-harness/tests/test_contact_solid_rule.py

**Do contactprobe's anchor, surface, volume and motion outputs name the solid rule their results depend on, and say none when no solid rule applied?**

Qualify a change to MeshIntersection.PolicyVersion, or to what contactprobe --anchor, --surface, --volume or --motion write about it.

Inputs: The Release contactprobe (or HC_CS_HARNESS); proofprobe --write-motion-fixture and stateprobe --write-modelless-light-fixture.

Outputs: Assertion results for: Do contactprobe's anchor, surface, volume and motion outputs name the solid rule their results depend on, and say none when no solid rule applied?

Source: [_geometry-harness/tests/test_contact_solid_rule.py](../_geometry-harness/tests/test_contact_solid_rule.py) — `contactprobe's anchor, surface, volume and motion outputs name the solid rule their results depend on (cases 1874-1879, 1886-1887).`


<a id="tool-_geometry-harness-tests-test_declared_cell_presence.py"></a>

### _geometry-harness/tests/test_declared_cell_presence.py

**Does a scene import skip a declared cell whose owning plugin the setup neither loads nor masters?**

Qualify scene imports for a setup without the candidate when the candidate adds cells.

Inputs: Controlled fixtures for test_declared_cell_presence.

Outputs: Assertion results or generated fixture data for: Does a scene import skip a declared cell whose owning plugin the setup neither loads nor masters?

Source: [_geometry-harness/tests/test_declared_cell_presence.py](../_geometry-harness/tests/test_declared_cell_presence.py) — `Known-answer tests for overlapprobe's declared-cell presence rule.`


<a id="tool-_geometry-harness-tests-test_discovery_probes.py"></a>

### _geometry-harness/tests/test_discovery_probes.py

**Do patchprobe, poolprobe and fomodprobe list every discovered patch with the sha256 of its bytes?**

Qualify a change to a discovery probe's --json hit list or to _shared/DiscoveryHits.cs.

Inputs: The three probes built from this checkout; a scratch machine of two MO2 instances, a two-archive pool and a seven-archive ordering pool, selected by environment variables; 7-Zip.

Outputs: Assertion results for: Do patchprobe, poolprobe and fomodprobe list every discovered patch with the sha256 of its bytes?

Source: [_geometry-harness/tests/test_discovery_probes.py](../_geometry-harness/tests/test_discovery_probes.py) — `Known answers for the discovery probes' machine-readable hit list ('--json'): patchprobe, poolprobe, fomodprobe.`


<a id="tool-_geometry-harness-tests-test_editkinds.py"></a>

### _geometry-harness/tests/test_editkinds.py

**Does the edit-kind classifier give one edit per record per kind, absorb the disable idiom and drop bookkeeping?**

Check editkinds changes against synthetic changed-field rows.

Inputs: Controlled fixtures for test_editkinds.

Outputs: Assertion results for: Does the edit-kind classifier give one edit per record per kind, absorb the disable idiom and drop bookkeeping?

Source: [_geometry-harness/tests/test_editkinds.py](../_geometry-harness/tests/test_editkinds.py) — `Known answers for editkinds.py, the patch edit-kind classifier.`


<a id="tool-_geometry-harness-tests-test_effect_shapes.py"></a>

### _geometry-harness/tests/test_effect_shapes.py

**Are effect-shader shapes (clouds, glow, flames) left out of viewprobe's inside and clear queries on a real render, while a mixed mesh keeps its solid body?**

Qualify changes to viewprobe --inside-at or --clear-at shape selection; needs HOUSECARL_FIXTURE_GAME (else NOT RUN, exit 3).

Inputs: CfFixture.esp from viewprobe --write-capture-fixture (cell CfCloud), a Skyrim SE game folder's archives.

Outputs: Assertion results per case; NOT RUN without game data.

Source: [_geometry-harness/tests/test_effect_shapes.py](../_geometry-harness/tests/test_effect_shapes.py) — `Known answers on a real render: effect-shader shapes are not solid`


<a id="tool-_geometry-harness-tests-test_effects_path_rule.py"></a>

### _geometry-harness/tests/test_effects_path_rule.py

**Does viewprobe judge a shape solid by its own data rather than by an effects\ folder, on a real render: lit effects meshes solid, hidden and refraction shapes not, an unreadable effects mesh counted?**

Qualify changes to which shapes --inside-at, --clear-at or the planes report treat as solid; needs HOUSECARL_FIXTURE_GAME (else NOT RUN, exit 3).

Inputs: CfFixture.esp from viewprobe --write-capture-fixture (cells CfFxLit, CfFxMissing, CfCloud), a Skyrim SE game folder's archives.

Outputs: Assertion results per case (C1720-C1727); NOT RUN without game data.

Source: [_geometry-harness/tests/test_effects_path_rule.py](../_geometry-harness/tests/test_effects_path_rule.py) — `Known answers on a real render: a shape is solid by its own data, not by its folder`


<a id="tool-_geometry-harness-tests-test_evallive.py"></a>

### _geometry-harness/tests/test_evallive.py

**Do opt-in live checks leave defaults unchanged and refuse bad live imports?**

Qualify the opt-in plan behaviour and the live import binding through the real controller.

Inputs: Controlled fixtures for test_evallive.

Outputs: Assertion results or generated fixture data for: Do opt-in live checks leave defaults unchanged and refuse bad live imports?

Source: [_geometry-harness/tests/test_evallive.py](../_geometry-harness/tests/test_evallive.py) — `Known answers for the opt-in live-import lane`


<a id="tool-_geometry-harness-tests-test_evalrecords.py"></a>

### _geometry-harness/tests/test_evalrecords.py

**Does the record capture's selection refuse empty and short reads and keep unjudged rows apart?**

Check evalrecords selection rule changes against synthetic rows.

Inputs: Controlled fixtures for test_evalrecords.

Outputs: Assertion results for: Does the record capture's selection refuse empty and short reads and keep unjudged rows apart?

Source: [_geometry-harness/tests/test_evalrecords.py](../_geometry-harness/tests/test_evalrecords.py) — `Known answers for evalrecords.py's selection rules (the record capture for the registry's mcp-only checks).`


<a id="tool-_geometry-harness-tests-test_evalrun.py"></a>

### _geometry-harness/tests/test_evalrun.py

**Does the evaluation controller match the oracle through its real workflow?**

Qualify controller changes through planning, execution, resumption, and evidence publication fixtures.

Inputs: Controlled fixtures for test_evalrun.

Outputs: Assertion results or generated fixture data for: Does the evaluation controller match the oracle through its real workflow?

Source: [_geometry-harness/tests/test_evalrun.py](../_geometry-harness/tests/test_evalrun.py) — `The evaluation controller against the oracle (Q42, roadmap 4.23).`


<a id="tool-_geometry-harness-tests-test_evalrun_population_verb.py"></a>

### _geometry-harness/tests/test_evalrun_population_verb.py

**Does runcontrol population write, byte for byte, the population file the schema names for the delta, print its identity, refuse a delta mod folder with a Base Object Swapper ini in one sentence, and write nothing but --out?**

Qualify changes to runcontrol's population verb (C2400-C2402); offline, no game.

Inputs: A minimal plugin file in a synthetic mod folder, written by runcontrol's population verb as a child.

Outputs: One GREEN, RED or CRASH line per check (C2400-C2402).

Source: [_geometry-harness/tests/test_evalrun_population_verb.py](../_geometry-harness/tests/test_evalrun_population_verb.py) — `Known answers for 'runcontrol population'`


<a id="tool-_geometry-harness-tests-test_explicit_instance.py"></a>

### _geometry-harness/tests/test_explicit_instance.py

**Does every probe refuse, with a named reason, when no MO2 instance is given instead of falling back to the harness config's instance?**

Qualify a change to HarnessTarget or to how a probe resolves its instance, game or profile.

Inputs: Release builds of switchprobe, geoprobe and corridorprobe; a temporary harness config naming empty folders.

Outputs: Assertion results for: Does every probe refuse, with a named reason, when no MO2 instance is given instead of falling back to the harness config's instance?

Source: [_geometry-harness/tests/test_explicit_instance.py](../_geometry-harness/tests/test_explicit_instance.py) — `Known answers for the explicit-instance rule in '_shared/HarnessTarget.cs'.`


<a id="tool-_geometry-harness-tests-test_extraction_scratch.py"></a>

### _geometry-harness/tests/test_extraction_scratch.py

**Does extraction scratch preserve other files and refuse failures consistently?**

Check cleanup changes against ownership boundaries and injected extraction failures.

Inputs: Controlled fixtures for test_extraction_scratch.

Outputs: Assertion results or generated fixture data for: Does extraction scratch preserve other files and refuse failures consistently?

Source: [_geometry-harness/tests/test_extraction_scratch.py](../_geometry-harness/tests/test_extraction_scratch.py) — `The extraction scratch's two contracts: what viewprobe may not DELETE, and how it FAILS.`


<a id="tool-_geometry-harness-tests-test_fingerprint_owner.py"></a>

### _geometry-harness/tests/test_fingerprint_owner.py

**Does the configuration fingerprint have one owner, runcontrol, that the Python tools ask?**

Qualify a change to ConfigurationIdentity or to a Python caller of evalrun.configuration_fingerprint.

Inputs: The built runcontrol and the harness Python modules.

Outputs: Assertion results for: Does the configuration fingerprint have one owner, runcontrol, that the Python tools ask?

Source: [_geometry-harness/tests/test_fingerprint_owner.py](../_geometry-harness/tests/test_fingerprint_owner.py) — `Known answers for the configuration fingerprint's one owner`


<a id="tool-_geometry-harness-tests-test_import_empty_population.py"></a>

### _geometry-harness/tests/test_import_empty_population.py

**Does runcontrol accept a judged-0 import only when it states no members on an arm without the targets, and refuse every other?**

Qualify a change to how runcontrol import-results reads an import's coverage.

Inputs: The evalrun fixtures, a trim of the shipped registry, and the fixture scopeprobe stand-in.

Outputs: Assertion results for: Does runcontrol accept a judged-0 import only when it states no members on an arm without the targets, and refuse every other?

Source: [_geometry-harness/tests/test_import_empty_population.py](../_geometry-harness/tests/test_import_empty_population.py) — `A judged-0 import is "nothing to judge" only on two independent facts (cases 1340-1346).`


<a id="tool-_geometry-harness-tests-test_imported_reference_accounting.py"></a>

### _geometry-harness/tests/test_imported_reference_accounting.py

**Does every imported reference receive exactly one accounting disposition?**

Qualify import handling before accepting an evaluation population with external references.

Inputs: Controlled fixtures for test_imported_reference_accounting.

Outputs: Assertion results or generated fixture data for: Does every imported reference receive exactly one accounting disposition?

Source: [_geometry-harness/tests/test_imported_reference_accounting.py](../_geometry-harness/tests/test_imported_reference_accounting.py) — `Known-answer tests for overlapprobe's imported-reference accounting.`


<a id="tool-_geometry-harness-tests-test_ledge_link_counts.py"></a>

### _geometry-harness/tests/test_ledge_link_counts.py

**Does viewprobe --route-solve report the ledge, enable/disable, unknown-type and held cross-mesh link counts, and name the held links when a pair has no path?**

Qualify changes to viewprobe's route-solve graph line or no-path refusal; needs HOUSECARL_FIXTURE_GAME (else NOT RUN, exit 3).

Inputs: CfFixture.esp from viewprobe --write-capture-fixture (cells CfLedge, CfCutaway), a Skyrim SE game folder's archives.

Outputs: Assertion results per case (C1990-C1993); NOT RUN without game data.

Source: [_geometry-harness/tests/test_ledge_link_counts.py](../_geometry-harness/tests/test_ledge_link_counts.py) — `Known answers: viewprobe --route-solve says which cross-mesh links it held`


<a id="tool-_geometry-harness-tests-test_lightclash.py"></a>

### _geometry-harness/tests/test_lightclash.py

**Does the light-clash fold attribute and decide orphaned, leaking, doubled and over-limit lights correctly?**

Check light-clash rule changes against constructed arrangements with known answers.

Inputs: Controlled fixtures for test_lightclash.

Outputs: Assertion results for: Does the light-clash fold attribute and decide orphaned, leaking, doubled and over-limit lights correctly?

Source: [_geometry-harness/tests/test_lightclash.py](../_geometry-harness/tests/test_lightclash.py) — `Known answers for lightclash.py`


<a id="tool-_geometry-harness-tests-test_mask_evidence.py"></a>

### _geometry-harness/tests/test_mask_evidence.py

**Does mask deduplication preserve exact output bytes and distinguish different masks?**

Verify a mask-storage optimization against byte identity and deliberately distinct subject evidence.

Inputs: Controlled fixtures for test_mask_evidence.

Outputs: Assertion results or generated fixture data for: Does mask deduplication preserve exact output bytes and distinguish different masks?

Source: [_geometry-harness/tests/test_mask_evidence.py](../_geometry-harness/tests/test_mask_evidence.py) — `The visibility-mask evidence writer: one encode per distinct mask, and identical bytes.`


<a id="tool-_geometry-harness-tests-test_missdiag.py"></a>

### _geometry-harness/tests/test_missdiag.py

**Does miss diagnosis cluster edits and preserve unknown reasons correctly?**

Check diagnostic classification changes with constructed patch edits and unresolved cases.

Inputs: Controlled fixtures for test_missdiag.

Outputs: Assertion results or generated fixture data for: Does miss diagnosis cluster edits and preserve unknown reasons correctly?

Source: [_geometry-harness/tests/test_missdiag.py](../_geometry-harness/tests/test_missdiag.py) — `Tests for missdiag.py — clustering, the miss-reason procedure and the table`


<a id="tool-_geometry-harness-tests-test_missing_master_refusal.py"></a>

### _geometry-harness/tests/test_missing_master_refusal.py

**Does every probe refuse, in one sentence, a load order whose plugin lacks a master?**

Qualify a change to how a probe reads its load order (_shared/HarnessOrderCli.cs, _shared/OrderMasters.cs or a probe that calls Mo2LoadOrder.Build).

Inputs: Release builds of the probes that read a load order; a temporary MO2 layout whose ticked plugin lacks its master.

Outputs: Assertion results for: Does every probe refuse, in one sentence, a load order whose plugin lacks a master?

Source: [_geometry-harness/tests/test_missing_master_refusal.py](../_geometry-harness/tests/test_missing_master_refusal.py) — `Known answers for ruling 12: every probe refuses an order whose plugin lacks a master, in one sentence, exit 2.`


<a id="tool-_geometry-harness-tests-test_modcensus.py"></a>

### _geometry-harness/tests/test_modcensus.py

**Does the census report a count only from a completed read, and rank candidates by uncovered features?**

Check modcensus changes against synthetic scopes and reads.

Inputs: Controlled fixtures for test_modcensus.

Outputs: Assertion results for: Does the census report a count only from a completed read, and rank candidates by uncovered features?

Source: [_geometry-harness/tests/test_modcensus.py](../_geometry-harness/tests/test_modcensus.py) — `Known answers for modcensus.py: a census row reports a count only when its read completed, and novelty ranks a`


<a id="tool-_geometry-harness-tests-test_modelless_activators_doors.py"></a>

### _geometry-harness/tests/test_modelless_activators_doors.py

**Do the geometry probes leave out activators and doors whose base record has no model, set editor markers aside by name, and keep a marker carrying a collision primitive unjudged?**

Qualify a change to what the contact index counts as a physical object (DetectorModel.HasNoBody, ContactIndex.IsBodiless / IsHostType) or to stateprobe's changed-reference coverage.

Inputs: The Release stateprobe, contactprobe and conflictdiff; ModellessActivatorFixture laid out as an MO2 instance.

Outputs: Assertion results for: Do the geometry probes leave out activators and doors whose base record has no model, set editor markers aside by name, and keep a marker carrying a collision primitive unjudged?

Source: [_geometry-harness/tests/test_modelless_activators_doors.py](../_geometry-harness/tests/test_modelless_activators_doors.py) — `Activators and doors with no model, and editor markers, in the geometry probes`


<a id="tool-_geometry-harness-tests-test_modelless_light.py"></a>

### _geometry-harness/tests/test_modelless_light.py

**Do stateprobe, contactprobe and its empty-space mode leave out a light whose base record has no model, and keep a light with a model or with a model that could not be read?**

Qualify a change to what the contact index counts as a physical object (ContactIndex.IsDependentType, DetectorModel.HasNoBody).

Inputs: The Release stateprobe and contactprobe; ModellessLightFixture laid out as an MO2 instance.

Outputs: Assertion results for: Do stateprobe, contactprobe and its empty-space mode leave out a light whose base record has no model, and keep a light with a model or with a model that could not be read?

Source: [_geometry-harness/tests/test_modelless_light.py](../_geometry-harness/tests/test_modelless_light.py) — `A light whose base record has no model is not a physical object to any geometry probe (cases 1430-1438).`


<a id="tool-_geometry-harness-tests-test_navedit.py"></a>

### _geometry-harness/tests/test_navedit.py

**Does navedit plan, refuse and write navmesh edits, markers and repairs as its known answers say?**

Qualify a change to NavEdit.cs, NavCutFootprint.cs, NavMarker.cs or navedit.

Inputs: The built navedit and its --selftest fixtures.

Outputs: Assertion results for: Does navedit plan, refuse and write navmesh edits, markers and repairs as its known answers say?

Source: [_geometry-harness/tests/test_navedit.py](../_geometry-harness/tests/test_navedit.py) — `Known answers for navmesh editing`


<a id="tool-_geometry-harness-tests-test_navgrid_rebuild.py"></a>

### _geometry-harness/tests/test_navgrid_rebuild.py

**Does the navmesh grid rebuild reproduce the engine's divisor, bounds and cells?**

Qualify a change to NavGrid.cs before trusting a rebuilt grid in a written patch.

Inputs: The built navownprobe; case 2512 also needs the game's Data folder in HC_NAVGRID_DATA.

Outputs: Assertion results for: Does the navmesh grid rebuild reproduce the engine's divisor, bounds and cells?

Source: [_geometry-harness/tests/test_navgrid_rebuild.py](../_geometry-harness/tests/test_navgrid_rebuild.py) — `Known answers for the navmesh grid rebuild`


<a id="tool-_geometry-harness-tests-test_navmesh_repair_rules.py"></a>

### _geometry-harness/tests/test_navmesh_repair_rules.py

**Does navownprobe count a link into a deleted navmesh, emit mod deletions, judge border links by their geometry (no matching edge, no link back, ambiguous candidates), find navmesh left on ground a later LAND moved, and plan a relink that refuses an ambiguous match?**

Qualify a change to navownprobe's deletion, border-link or navmesh-ground rules, or to its relink plan (C2450-C2489).

Inputs: The Release navownprobe; NavRepairFixture laid out as an MO2 instance.

Outputs: Assertion results for: Does navownprobe count a link into a deleted navmesh, emit mod deletions, judge border links by their geometry, find navmesh left on moved ground, and plan a relink that refuses an ambiguous match?

Source: [_geometry-harness/tests/test_navmesh_repair_rules.py](../_geometry-harness/tests/test_navmesh_repair_rules.py) — `Known answers for the navmesh repair rules and the relink plan (C2450-C2489), on navownprobe/NavRepairFixture.cs.`


<a id="tool-_geometry-harness-tests-test_order_input_refusal.py"></a>

### _geometry-harness/tests/test_order_input_refusal.py

**Does every probe refuse a --enable it cannot place or an --order candidate built on another profile, in one sentence?**

Qualify a change to how a probe reads its load order (_shared/HarnessOrderCli.cs, _shared/SyntheticOrder.cs) or to a probe's --enable/--order handling.

Inputs: Release builds of the probes that read a load order; a temporary MO2 layout with two profiles and a plugin loadorder.txt does not list.

Outputs: Assertion results for: Does every probe refuse a --enable it cannot place or an --order candidate built on another profile, in one sentence?

Source: [_geometry-harness/tests/test_order_input_refusal.py](../_geometry-harness/tests/test_order_input_refusal.py) — `Known answers: a probe refuses a requested order it cannot build as asked, in one sentence, exit 2 (cases 1320-1324).`


<a id="tool-_geometry-harness-tests-test_orderleads.py"></a>

### _geometry-harness/tests/test_orderleads.py

**Does orderleads offer only clashes between mods, mask the compressed flag, tell dummies from the map, and reach a lead by added mod, place and pair?**

After any change to orderleads' admission, mask, dummies, queries or output.

Inputs: Controlled fixtures for test_orderleads.

Outputs: Assertion results or generated fixture data for: Does orderleads offer only clashes between mods, mask the compressed flag, tell dummies from the map, and reach a lead by added mod, place and pair?

Source: [_geometry-harness/tests/test_orderleads.py](../_geometry-harness/tests/test_orderleads.py) — `Known answers for orderleads: a whole-order pass's rows as leads, queried by added mod, place and pair.`


<a id="tool-_geometry-harness-tests-test_orderpass.py"></a>

### _geometry-harness/tests/test_orderpass.py

**Does orderpass record a failed or missing stage as unassessed, carry on, and make an added mod's view the pass's first read?**

After any change to orderpass' stages or its outputs.

Inputs: Controlled fixtures for test_orderpass.

Outputs: Assertion results or generated fixture data for: Does orderpass record a failed or missing stage as unassessed, carry on, and make an added mod's view the pass's first read?

Source: [_geometry-harness/tests/test_orderpass.py](../_geometry-harness/tests/test_orderpass.py) — `Known answers for orderpass, the whole-order pass driver, over stand-in programs.`


<a id="tool-_geometry-harness-tests-test_ordersweep_memory_route.py"></a>

### _geometry-harness/tests/test_ordersweep_memory_route.py

**Does ordersweep carry contactprobe's memory route (route, unattended default, warning) into summary.json and its job records, and record a run without one as not reported rather than fast?**

Qualify a change to ordersweep's memory_route reader or to where its job records and summary rows carry it.

Inputs: Constructed sidecar files in a temporary folder; ordersweep.py's source.

Outputs: Assertion results for: Does ordersweep carry contactprobe's memory route (route, unattended default, warning) into summary.json and its job records, and record a run without one as not reported rather than fast?

Source: [_geometry-harness/tests/test_ordersweep_memory_route.py](../_geometry-harness/tests/test_ordersweep_memory_route.py) — `Known answers for ordersweep's memory-route record`


<a id="tool-_geometry-harness-tests-test_ordersweep_program_key.py"></a>

### _geometry-harness/tests/test_ordersweep_program_key.py

**Does ordersweep's cache key tell two builds apart when only their dlls differ?**

Qualify a change to how ordersweep keys a probe binary before trusting --reuse-cache across a deploy.

Inputs: Controlled fixtures for test_ordersweep_program_key.

Outputs: Assertion results or generated fixture data for: Does ordersweep's cache key tell two builds apart when only their dlls differ?

Source: [_geometry-harness/tests/test_ordersweep_program_key.py](../_geometry-harness/tests/test_ordersweep_program_key.py) — `Known answers for ordersweep's program key (the cache's binary identity).`


<a id="tool-_geometry-harness-tests-test_ordersweep_refused.py"></a>

### _geometry-harness/tests/test_ordersweep_refused.py

**Does ordersweep still catch every refusal a probe can print, and not read a count of refused items as a refusal?**

Qualify a change to ordersweep's refusal marker, or a new probe console line that says 'refused'.

Inputs: The refusal strings and count formats collected from the C# sources under _geometry-harness.

Outputs: Assertion results for: Does ordersweep still catch every refusal a probe can print, and not read a count of refused items as a refusal?

Source: [_geometry-harness/tests/test_ordersweep_refused.py](../_geometry-harness/tests/test_ordersweep_refused.py) — `Known answers for ordersweep's refusal marker (REFUSED_RE)`


<a id="tool-_geometry-harness-tests-test_overlap_attribution_contract.py"></a>

### _geometry-harness/tests/test_overlap_attribution_contract.py

**Does overlap attribution satisfy the independent acceptance controls?**

Qualify attribution changes against separate source and combined-scene controls.

Inputs: Controlled fixtures for test_overlap_attribution_contract.

Outputs: Assertion results or generated fixture data for: Does overlap attribution satisfy the independent acceptance controls?

Source: [_geometry-harness/tests/test_overlap_attribution_contract.py](../_geometry-harness/tests/test_overlap_attribution_contract.py) — `Independent Q47 acceptance controls, frozen before implementation inspection.`


<a id="tool-_geometry-harness-tests-test_plan_paths.py"></a>

### _geometry-harness/tests/test_plan_paths.py

**Does an evaluation plan run the same way from any working folder?**

Qualify a change to the paths runcontrol plan stores or run/resume read.

Inputs: The evalrun fixtures, a trim of the shipped registry, and the fixture scopeprobe stand-in.

Outputs: Assertion results for: Does an evaluation plan run the same way from any working folder?

Source: [_geometry-harness/tests/test_plan_paths.py](../_geometry-harness/tests/test_plan_paths.py) — `A plan runs the same way from any working folder (cases 1330-1334).`


<a id="tool-_geometry-harness-tests-test_plan_scene_preflight.py"></a>

### _geometry-harness/tests/test_plan_scene_preflight.py

**Does runcontrol plan refuse an arm that overlapprobe's scene preflight would refuse?**

Qualify the plan-time scene preflight through the real overlapprobe before trusting a plan's arms for scene capture.

Inputs: Temporary MO2 instances and candidate files built by the test.

Outputs: Assertion results for: Does runcontrol plan refuse an arm that overlapprobe's scene preflight would refuse?

Source: [_geometry-harness/tests/test_plan_scene_preflight.py](../_geometry-harness/tests/test_plan_scene_preflight.py) — `'evalrun plan' runs overlapprobe's scene preflight on every arm (cases 440-451).`


<a id="tool-_geometry-harness-tests-test_plan_view_mesh_gaps.py"></a>

### _geometry-harness/tests/test_plan_view_mesh_gaps.py

**Does the scene preflight refuse an arm whose view lacks meshes an installed folder outside it has, and pass the controls?**

Qualify a change to overlapprobe's scene preflight (ViewMeshGaps.cs) or to how an arm's asset view is built.

Inputs: Release builds of overlapprobe and runcontrol; temporary MO2 layouts with byte-written plugins and archives.

Outputs: Assertion results for: Does the scene preflight refuse an arm whose view lacks meshes an installed folder outside it has, and pass the controls?

Source: [_geometry-harness/tests/test_plan_view_mesh_gaps.py](../_geometry-harness/tests/test_plan_view_mesh_gaps.py) — `An arm whose plugins name meshes that only a folder outside its asset view provides is refused (cases 1350-1366).`


<a id="tool-_geometry-harness-tests-test_point_queries_answer_or_refuse.py"></a>

### _geometry-harness/tests/test_point_queries_answer_or_refuse.py

**Do viewprobe's point queries (--nav-at, --clear-at, --inside-at, --refs-at) answer or refuse in one sentence, never print nothing, on a run without an eye or with a malformed value?**

Qualify changes to how viewprobe parses or answers the point queries; needs HOUSECARL_FIXTURE_GAME (else NOT RUN, exit 3).

Inputs: CfFixture.esp from viewprobe --write-capture-fixture (cell CfCutaway), a Skyrim SE game folder's archives.

Outputs: Assertion results per case (C1940-C1945); NOT RUN without game data.

Source: [_geometry-harness/tests/test_point_queries_answer_or_refuse.py](../_geometry-harness/tests/test_point_queries_answer_or_refuse.py) — `Known answers: viewprobe's point queries answer or refuse, never print nothing`


<a id="tool-_geometry-harness-tests-test_population_scope.py"></a>

### _geometry-harness/tests/test_population_scope.py

**Do overlapprobe, contactprobe (default pass and --empty-space) and stateprobe with --population keep exactly the whole-order rows of the plan's population, in both arms?**

Qualify a change to the population scope (_shared/PopulationScope.cs) or to how overlapprobe or contactprobe apply it.

Inputs: PopulationFixture, written by overlapprobe --self-test; the Release overlapprobe, contactprobe and conflictdiff.

Outputs: Assertion results for: Do overlapprobe, contactprobe (default pass and --empty-space) and stateprobe with --population keep exactly the whole-order rows of the plan's population, in both arms?

Source: [_geometry-harness/tests/test_population_scope.py](../_geometry-harness/tests/test_population_scope.py) — `Known answers for the plan's population (P25)`


<a id="tool-_geometry-harness-tests-test_record_2_0_unjudged.py"></a>

### _geometry-harness/tests/test_record_2_0_unjudged.py

**Does the record-lane comparator list every field it cannot judge on a houseCARL 2.0 capture (a cut-off, unread, unnamed or depth-mismatched field, a failed read, a tree answer that could not compare a plugin in full or whose plugin list was cut off), with its reason and plugins, and never drop it?**

Qualify changes to recordrows' 2.0 path (read_chains, chains_for_setup, the tree and by-name readers) and how its unjudged list reaches the rows file (C2174-C2178); offline, no game.

Inputs: Hand-written 2.0 housecarl_records by-name and tree answers of one record, and the shipped schema reference and field scope.

Outputs: One PASS or FAIL line per case (C2174-C2178).

Source: [_geometry-harness/tests/test_record_2_0_unjudged.py](../_geometry-harness/tests/test_record_2_0_unjudged.py) — `a field it cannot judge is listed as unjudged with its reason`


<a id="tool-_geometry-harness-tests-test_record_unexpanded_leaves.py"></a>

### _geometry-harness/tests/test_record_unexpanded_leaves.py

**Does the record-lane comparator leave a field unjudged, never equal, when a read printed it without its values (an item as its type name, a list as its summary), while a field read with its values or by content hash stays judged?**

Qualify changes to recordrows' unexpanded-field rule, its read preference, the schema's grid and dictionary entries, or partly read fields (C2170, C2172, C2173); offline, no game.

Inputs: Hand-written 1.9.0 text reads and 2.0 by-name and tree answers of one cell's height grid, and the shipped schema reference and field scope.

Outputs: One PASS or FAIL line per case (C2170, C2172, C2173).

Source: [_geometry-harness/tests/test_record_unexpanded_leaves.py](../_geometry-harness/tests/test_record_unexpanded_leaves.py) — `a leaf that carries no value is unjudged with its reason, never equal`


<a id="tool-_geometry-harness-tests-test_records_keep_work.py"></a>

### _geometry-harness/tests/test_records_keep_work.py

**Does the record capture read only what an evaluated plugin touches, reread when an arm's inputs change, and does a stopped sweep or a raised budget keep finished work?**

Check changes to evalrecords' selection and key, or how the controller credits a stopped sweep.

Inputs: Controlled fixtures for test_records_keep_work.

Outputs: Assertion results for: Does the record capture read only what an evaluated plugin touches, reread when an arm's inputs change, and does a stopped sweep or a raised budget keep finished work?

Source: [_geometry-harness/tests/test_records_keep_work.py](../_geometry-harness/tests/test_records_keep_work.py) — `Known answers for the records stage's reads and for keeping finished work across a stop or a budget change.`


<a id="tool-_geometry-harness-tests-test_records_parallel.py"></a>

### _geometry-harness/tests/test_records_parallel.py

**Does reading a capture's arms side by side keep each arm's evidence its own, choose workers from memory, and report failure, stop and resume as one worker does?**

Check changes to evalrecords' workers, evalrecords_workers' choice and scheduler, or how the records stage asks for workers.

Inputs: Controlled fixtures for test_records_parallel.

Outputs: Assertion results for: Does reading a capture's arms side by side keep each arm's evidence its own, choose workers from memory, and report failure, stop and resume as one worker does?

Source: [_geometry-harness/tests/test_records_parallel.py](../_geometry-harness/tests/test_records_parallel.py) — `Known answers for reading a capture's arms side by side: what arms share, how many run, and what a failure leaves.`


<a id="tool-_geometry-harness-tests-test_replay_command.py"></a>

### _geometry-harness/tests/test_replay_command.py

**Does each frame's embedded replay command reproduce that frame?**

Qualify embedded command generation before relying on it to revisit evidence captures.

Inputs: Controlled fixtures for test_replay_command.

Outputs: Assertion results or generated fixture data for: Does each frame's embedded replay command reproduce that frame?

Source: [_geometry-harness/tests/test_replay_command.py](../_geometry-harness/tests/test_replay_command.py) — `Does a frame name a command that reproduces THAT frame?`


Modifiers (do not select a separate mode):

- `--cell` — Set cell for this invocation; consult the linked parser for accepted values.
- `--exe` — Set exe for this invocation; consult the linked parser for accepted values.
- `--eye` — Set eye for this invocation; consult the linked parser for accepted values.
- `--eye2` — Set eye2 for this invocation; consult the linked parser for accepted values.
- `--game` — Set game for this invocation; consult the linked parser for accepted values.
- `--instance` — Set instance for this invocation; consult the linked parser for accepted values.
- `--look` — Set look for this invocation; consult the linked parser for accepted values.
- `--profile` — Set profile for this invocation; consult the linked parser for accepted values.
- `--size` — Set size for this invocation; consult the linked parser for accepted values.
- `--work` — Set work for this invocation; consult the linked parser for accepted values.

<a id="tool-_geometry-harness-tests-test_report_sink.py"></a>

### _geometry-harness/tests/test_report_sink.py

**Are planes reports streamed with identical bytes and atomic publication?**

Check streamed report output and interruption handling after changing the publication path.

Inputs: Controlled fixtures for test_report_sink.

Outputs: Assertion results or generated fixture data for: Are planes reports streamed with identical bytes and atomic publication?

Source: [_geometry-harness/tests/test_report_sink.py](../_geometry-harness/tests/test_report_sink.py) — `The planes report goes to the file as it is serialised, and never half-written under its name.`


<a id="tool-_geometry-harness-tests-test_reverse_reconcile.py"></a>

### _geometry-harness/tests/test_reverse_reconcile.py

**Does overlapprobe --reverse-of give the full reverse's emit, and refuse a baseline emit that is not this run's own?**

Qualify a change to overlapprobe's reverse-from-baseline mode or to the checks that guard it.

Inputs: PopulationFixture, written by overlapprobe --self-test; the Release overlapprobe.

Outputs: Assertion results for: Does overlapprobe --reverse-of give the full reverse's emit, and refuse a baseline emit that is not this run's own?

Source: [_geometry-harness/tests/test_reverse_reconcile.py](../_geometry-harness/tests/test_reverse_reconcile.py) — `Known answers for overlapprobe --reverse-of (overlap acceleration P1): a reverse that takes the baseline emit's rows.`


<a id="tool-_geometry-harness-tests-test_route_cut_adapter.py"></a>

### _geometry-harness/tests/test_route_cut_adapter.py

**Does the route-cut check read each arm's candidate and only the targets that arm loads?**

Qualify evalrun's route-cut adapter and blockprobe's argument handling before trusting a route-cut receipt on a candidate arm.

Inputs: The evalrun fixtures, a trim of the shipped registry, and the built blockprobe.

Outputs: Assertion results for: Does the route-cut check read each arm's candidate and only the targets that arm loads?

Source: [_geometry-harness/tests/test_route_cut_adapter.py](../_geometry-harness/tests/test_route_cut_adapter.py) — `The route-cut check reads each arm's candidate (cases 452-455).`


<a id="tool-_geometry-harness-tests-test_route_cut_rows.py"></a>

### _geometry-harness/tests/test_route_cut_rows.py

**Does blockprobe --rows write every route-cut row its listing states, uncapped, with the passage share the rule states?**

Qualify a change to blockprobe's route-cut listing or its rows file (blockprobe/RouteCutRows.cs).

Inputs: The Release blockprobe; RouteCutFixture laid out as an MO2 instance.

Outputs: Assertion results for: Does blockprobe --rows write every route-cut row its listing states, uncapped, with the passage share the rule states?

Source: [_geometry-harness/tests/test_route_cut_rows.py](../_geometry-harness/tests/test_route_cut_rows.py) — `blockprobe writes its route-cut listing as a rows file the report can read (cases 1439-1442).`


<a id="tool-_geometry-harness-tests-test_route_geometry.py"></a>

### _geometry-harness/tests/test_route_geometry.py

**Is viewprobe --route-solve's path taut through the navmesh, with no corner emitted twice and every interior corner inset?**

Qualify changes to the funnel, the channel straightening or the corner inset (NavGraph.Funnel, TautPath, InsetCorners); needs HOUSECARL_FIXTURE_GAME (else NOT RUN, exit 3).

Inputs: CfFixture.esp from viewprobe --write-capture-fixture (cell CfLedgeGap), a Skyrim SE game folder's archives.

Outputs: Assertion results per case (C2300, C2301, C2306); NOT RUN without game data.

Source: [_geometry-harness/tests/test_route_geometry.py](../_geometry-harness/tests/test_route_geometry.py) — `Known answers: viewprobe --route-solve's path is taut through the navmesh`


<a id="tool-_geometry-harness-tests-test_route_solve_ledge_drop.py"></a>

### _geometry-harness/tests/test_route_solve_ledge_drop.py

**Does viewprobe --route-solve accept a solved leg that drops down a walked ledge, across the ledge's plan gap, and still refuse a leg that really leaves the mesh?**

Qualify changes to viewprobe's solved-leg sampling or the shared corner inset (NavGraph.ChannelTriangleAt, LedgeStep, InsetCorners); needs HOUSECARL_FIXTURE_GAME (else NOT RUN, exit 3).

Inputs: CfFixture.esp from viewprobe --write-capture-fixture (cells CfLedge, CfLedgeGap), a Skyrim SE game folder's archives.

Outputs: Assertion results per case (C2250-C2254); NOT RUN without game data.

Source: [_geometry-harness/tests/test_route_solve_ledge_drop.py](../_geometry-harness/tests/test_route_solve_ledge_drop.py) — `Known answers: viewprobe --route-solve accepts a solved leg that drops down a walked ledge`


<a id="tool-_geometry-harness-tests-test_run_records.py"></a>

### _geometry-harness/tests/test_run_records.py

**Do real renderer run records report their stages and workload correctly?**

Audit renderer instrumentation changes against actual workload receipts and stage accounting.

Inputs: Controlled fixtures for test_run_records.

Outputs: Assertion results or generated fixture data for: Do real renderer run records report their stages and workload correctly?

Source: [_geometry-harness/tests/test_run_records.py](../_geometry-harness/tests/test_run_records.py) — `What a viewprobe run says about itself in the run store, checked against the contract.`


Modifiers (do not select a separate mode):

- `--cell` — Set cell for this invocation; consult the linked parser for accepted values.
- `--exe` — Set exe for this invocation; consult the linked parser for accepted values.
- `--eye` — Set eye for this invocation; consult the linked parser for accepted values.
- `--game` — Set game for this invocation; consult the linked parser for accepted values.
- `--instance` — Set instance for this invocation; consult the linked parser for accepted values.
- `--look` — Set look for this invocation; consult the linked parser for accepted values.
- `--profile` — Set profile for this invocation; consult the linked parser for accepted values.
- `--render` — Set render for this invocation; consult the linked parser for accepted values.
- `--store` — Set store for this invocation; consult the linked parser for accepted values.

<a id="tool-_geometry-harness-tests-test_scene_scope_import.py"></a>

### _geometry-harness/tests/test_scene_scope_import.py

**Does the actual overlap probe enforce the imported scene-scope contract?**

Qualify scene import behavior through the production detector before trusting imported populations.

Inputs: Controlled fixtures for test_scene_scope_import.

Outputs: Assertion results or generated fixture data for: Does the actual overlap probe enforce the imported scene-scope contract?

Source: [_geometry-harness/tests/test_scene_scope_import.py](../_geometry-harness/tests/test_scene_scope_import.py) — `Known-answer tests for overlapprobe's --scene-scope-from bound contract.`


<a id="tool-_geometry-harness-tests-test_solid_rule_named.py"></a>

### _geometry-harness/tests/test_solid_rule_named.py

**Do motionprobe sweep and lightprobe outputs name the solid rule their results depend on, and say none when no solid rule applied?**

Qualify a change to MeshIntersection.PolicyVersion or MeshAnchor.PolicyVersion, or to what motionprobe sweep or lightprobe write about them.

Inputs: The Release motionprobe and lightprobe (or HC_SR_HARNESS); proofprobe --write-motion-fixture and stateprobe --write-modelless-light-fixture.

Outputs: Assertion results for: Do motionprobe sweep and lightprobe outputs name the solid rule their results depend on, and say none when no solid rule applied?

Source: [_geometry-harness/tests/test_solid_rule_named.py](../_geometry-harness/tests/test_solid_rule_named.py) — `motionprobe sweep and lightprobe outputs name the solid rule their results depend on (cases 1960-1968).`


<a id="tool-_geometry-harness-tests-test_state_marker_box_and_actors.py"></a>

### _geometry-harness/tests/test_state_marker_box_and_actors.py

**Does stateprobe name the primitive table it was given, judge a collision-marker box measured solid as a route obstacle, and read placed actors a state brings in from their placement without ever writing a row for one?**

Qualify a change to stateprobe's marker primitives, its primitive table identity, or how it reads placed actors a state changes.

Inputs: The Release stateprobe and conflictdiff (or HC_SL_HARNESS); StateLaneFixture laid out as an MO2 instance.

Outputs: Assertion results for: Does stateprobe name the primitive table it was given, judge a collision-marker box measured solid as a route obstacle, and read placed actors a state brings in from their placement without ever writing a row for one?

Source: [_geometry-harness/tests/test_state_marker_box_and_actors.py](../_geometry-harness/tests/test_state_marker_box_and_actors.py) — `stateprobe: the measured primitive table's identity in the emit, collision-marker boxes measured solid as route obstacles,`


<a id="tool-_geometry-harness-tests-test_state_row_identity.py"></a>

### _geometry-harness/tests/test_state_row_identity.py

**Does stateprobe write one row per pair of references in a state, whichever contact pass found it, and keep the deeper reading?**

Qualify a change to the state class's contact passes or to how its rows are merged (_shared/StateClass.cs).

Inputs: The Release stateprobe and conflictdiff, and ordersweep.py; RowIdentityFixture laid out as an MO2 instance.

Outputs: Assertion results for: Does stateprobe write one row per pair of references in a state, whichever contact pass found it, and keep the deeper reading?

Source: [_geometry-harness/tests/test_state_row_identity.py](../_geometry-harness/tests/test_state_row_identity.py) — `stateprobe writes one row per pair of references in a state, whichever contact pass found it (cases 1370-1381).`


<a id="tool-_geometry-harness-tests-test_swept_oom_sentence.py"></a>

### _geometry-harness/tests/test_swept_oom_sentence.py

**When motionprobe sweep or deltacheck, or contactprobe's swept review, runs out of memory, does it say so in one sentence that says what to try, and does that advice work?**

Qualify a change to how motionprobe or contactprobe report running out of memory on the swept paths.

Inputs: The Release motionprobe and contactprobe (or HC_OOM_HARNESS); the test_contact_solid_rule.py lab; DOTNET_GCHeapHardLimit caps.

Outputs: Assertion results for: When motionprobe sweep or deltacheck, or contactprobe's swept review, runs out of memory, does it say so in one sentence that says what to try, and does that advice work?

Source: [_geometry-harness/tests/test_swept_oom_sentence.py](../_geometry-harness/tests/test_swept_oom_sentence.py) — `Running out of memory on the swept paths ends in one sentence that says what to try (cases 1880-1885).`


<a id="tool-_geometry-harness-tests-test_transcript_links.py"></a>

### _geometry-harness/tests/test_transcript_links.py

**Does a run keep each reader transcript once: does import-results link the capture's request and response (a copy across volumes), refuse a changed transcript, do the bundle and media bundle link the import's files and still verify, and does a records rerun or resume leave every byte reachable through a link unchanged?**

Qualify changes to the controller's import-results transcript storage, bundle's copy-in, or evalrecords' transcript writers (TranscriptFile, Session.write) (C2160, C2162-C2165); offline, no game.

Inputs: The evalrun fixtures (case-01) with the shipped registry trimmed to three checks, and transcripts laid out as evalrecords lays them out.

Outputs: One PASS or FAIL line per case (C2160-C2165).

Source: [_geometry-harness/tests/test_transcript_links.py](../_geometry-harness/tests/test_transcript_links.py) — `A run keeps each reader transcript once`


<a id="tool-_geometry-harness-tests-test_workbench_manual_names.py"></a>

### _geometry-harness/tests/test_workbench_manual_names.py

**Does the names read of a manual-mode bundle read the plan's review arm, not the baseline or a union of arms?**

Qualify a change to bundleenrich's names read or to the plan's reviewArm.

Inputs: Typed plans and a synthetic world; no probe.

Outputs: Assertion results for: Does the names read of a manual-mode bundle read the plan's review arm, not the baseline or a union of arms?

Source: [_geometry-harness/tests/test_workbench_manual_names.py](../_geometry-harness/tests/test_workbench_manual_names.py) — `Known answer: in a manual-mode bundle, the names read reaches the selected mod's own references.`


<a id="tool-_geometry-harness-tests-test_worklist_aspect.py"></a>

### _geometry-harness/tests/test_worklist_aspect.py

**Does the requested canvas aspect reach every worklist and capture path?**

Qualify a framing change across planner exports, renderer worklists, and capture requests.

Inputs: Controlled fixtures for test_worklist_aspect.

Outputs: Assertion results or generated fixture data for: Does the requested canvas aspect reach every worklist and capture path?

Source: [_geometry-harness/tests/test_worklist_aspect.py](../_geometry-harness/tests/test_worklist_aspect.py) — `The capture canvas SHAPE travels with the shot: writer, schema, validator, precedence.`


<a id="tool-_geometry-harness-viewprobe-tools-planes_report_rows.py"></a>

### _geometry-harness/viewprobe/tools/planes_report_rows.py

**How can I read candidate rows exactly from either planes-report schema?**

Consume a retained occlusion report without assuming that its schema stores candidates inline.

Inputs: A v1 or v2 planes-report document.

Outputs: Exact joined candidate rows or schema/identity refusal.

Source: [_geometry-harness/viewprobe/tools/planes_report_rows.py](../_geometry-harness/viewprobe/tools/planes_report_rows.py) — `Exact v1/v2 candidate joins for viewprobe planes reports.`


<a id="tool-_geometry-harness-viewprobe-tools-renderbatch.py"></a>

### _geometry-harness/viewprobe/tools/renderbatch.py

**How can I plan and execute a static render campaign while preserving shot identity and resuming receipts?**

Run a fixed set of shots with durable receipts so interrupted campaigns can reuse only qualified outputs.

Inputs: Campaign manifest with concrete worklist, argv, output and readiness identity.

Outputs: Frozen batch plan and execution/resume receipts; unsafe or unsupported plans refused.

Source: [_geometry-harness/viewprobe/tools/renderbatch.py](../_geometry-harness/viewprobe/tools/renderbatch.py) — `Batch planning primitives for viewprobe's opt-in static-worklist campaign path.`


Modifiers (do not select a separate mode):

- `--json` — Set json for this invocation; consult the linked parser for accepted values.

<a id="tool-_geometry-harness-viewprobe-tools-test_depthsurvey.py"></a>

### _geometry-harness/viewprobe/tools/test_depthsurvey.py

**Does depth-survey projection and lookup math satisfy its synthetic controls?**

Check geometric projection and clearance lookup after changing depth-survey calculations.

Inputs: Controlled fixtures for test_depthsurvey.

Outputs: Assertion results or generated fixture data for: Does depth-survey projection and lookup math satisfy its synthetic controls?

Source: [_geometry-harness/viewprobe/tools/test_depthsurvey.py](../_geometry-harness/viewprobe/tools/test_depthsurvey.py) — `Arithmetic-only checks for depthsurvey.py's lookup math.`


<a id="tool-_geometry-harness-viewprobe-tools-test_occlusion_rule.py"></a>

### _geometry-harness/viewprobe/tools/test_occlusion_rule.py

**Does viewprobe's occlusion rule admit and refuse primitive orientations as its rule file states?**

Run viewprobe's occlusion-rule known answers against the built viewprobe after a rule or orientation change.

Inputs: The built viewprobe; a temporary folder.

Outputs: Assertion results for: Does viewprobe's occlusion rule admit and refuse primitive orientations as its rule file states?

Source: [_geometry-harness/viewprobe/tools/test_occlusion_rule.py](../_geometry-harness/viewprobe/tools/test_occlusion_rule.py) — `Runs viewprobe's occlusion-rule known answers`


<a id="tool-_geometry-harness-viewprobe-tools-test_planes_report_rows.py"></a>

### _geometry-harness/viewprobe/tools/test_planes_report_rows.py

**Does the planes-report reader join both schema versions correctly and refuse malformed data?**

Qualify an occlusion-report reader change against inline and normalized fixtures, including invalid joins.

Inputs: Controlled fixtures for test_planes_report_rows.

Outputs: Assertion results or generated fixture data for: Does the planes-report reader join both schema versions correctly and refuse malformed data?

Source: [_geometry-harness/viewprobe/tools/test_planes_report_rows.py](../_geometry-harness/viewprobe/tools/test_planes_report_rows.py) — `Small exact-output and refusal cases for both planes-report schemas.`


<a id="tool-_geometry-harness-viewprobe-tools-test_renderbatch.py"></a>

### _geometry-harness/viewprobe/tools/test_renderbatch.py

**Does render-batch readiness enforce the offline planning and refusal contracts?**

Exercise campaign planning and rejection paths before launching expensive renderer jobs.

Inputs: Controlled fixtures for test_renderbatch.

Outputs: Assertion results or generated fixture data for: Does render-batch readiness enforce the offline planning and refusal contracts?

Source: [_geometry-harness/viewprobe/tools/test_renderbatch.py](../_geometry-harness/viewprobe/tools/test_renderbatch.py) — `Renderer-free readiness controls; the renderer RED is a separately gated launch.`


<a id="tool-_geometry-harness-viewprobe-tools-test_renderlimit.py"></a>

### _geometry-harness/viewprobe/tools/test_renderlimit.py

**Does the render concurrency calculation refuse absent or insufficient memory readings?**

Check concurrency decisions against unknown capacity and deliberately constrained memory fixtures.

Inputs: Controlled fixtures for test_renderlimit.

Outputs: Assertion results or generated fixture data for: Does the render concurrency calculation refuse absent or insufficient memory readings?

Source: [_geometry-harness/viewprobe/tools/test_renderlimit.py](../_geometry-harness/viewprobe/tools/test_renderlimit.py) — `How many renders may run side by side, and what happens when the machine will not say.`


<a id="tool-_geometry-harness-viewprobe-tools-test_shotplan_camera.py"></a>

### _geometry-harness/viewprobe/tools/test_shotplan_camera.py

**Do shotplan's camera conventions and INI resolution match their known answers?**

Qualify projection conversions and configuration precedence before accepting camera-math changes.

Inputs: Controlled fixtures for test_shotplan_camera.

Outputs: Assertion results or generated fixture data for: Do shotplan's camera conventions and INI resolution match their known answers?

Source: [_geometry-harness/viewprobe/tools/test_shotplan_camera.py](../_geometry-harness/viewprobe/tools/test_shotplan_camera.py) — `Offline checks for shotplan's camera-convention and ini-resolution helpers.`


<a id="tool-_geometry-harness-viewprobe-tools-test_shotplan_fallback.py"></a>

### _geometry-harness/viewprobe/tools/test_shotplan_fallback.py

**Does a refused camera plan leave a labelled top-down fallback and keep its refusal?**

Check the planner's refusal artifacts against synthetic scenes refused at each stage.

Inputs: Controlled fixtures for test_shotplan_fallback.

Outputs: Assertion results or generated fixture data for: Does a refused camera plan leave a labelled top-down fallback and keep its refusal?

Source: [_geometry-harness/viewprobe/tools/test_shotplan_fallback.py](../_geometry-harness/viewprobe/tools/test_shotplan_fallback.py) — `Offline checks for shotplan's top-down FALLBACK: what a refused plan leaves behind.`


<a id="tool-_geometry-harness-viewprobe-tools-test_shotplan_plan.py"></a>

### _geometry-harness/viewprobe/tools/test_shotplan_plan.py

**Does the actual planner verify and export the camera justified by its queries?**

Check candidate selection through the real planner using a predictable synthetic scene.

Inputs: Controlled fixtures for test_shotplan_plan.

Outputs: Assertion results or generated fixture data for: Does the actual planner verify and export the camera justified by its queries?

Source: [_geometry-harness/viewprobe/tools/test_shotplan_plan.py](../_geometry-harness/viewprobe/tools/test_shotplan_plan.py) — `Offline checks for shotplan's PLANNING path: what it believes, and what it publishes.`


<a id="tool-_geometry-harness-viewprobe-tools-test_shotplan_vp.py"></a>

### _geometry-harness/viewprobe/tools/test_shotplan_vp.py

**Does shotplan resolve a usable viewprobe and explain unavailable builds?**

Exercise renderer discovery and failure reporting after changing executable resolution.

Inputs: Controlled fixtures for test_shotplan_vp.

Outputs: Assertion results or generated fixture data for: Does shotplan resolve a usable viewprobe and explain unavailable builds?

Source: [_geometry-harness/viewprobe/tools/test_shotplan_vp.py](../_geometry-harness/viewprobe/tools/test_shotplan_vp.py) — `Which viewprobe a plan launches, and what it says when it cannot use the built one.`


<a id="tool-adviceprobe"></a>

### adviceprobe

**What ordering rules does the mod author's catalogue text actually support?**

**Does a neighbour mod's page state a clash with the evaluated mod?**

Turn catalogue descriptions into source-backed ordering constraints before trying alternative mod arrangements.

Inputs: Catalogued or archived author page and target mod identity.

Outputs: OrderRules advice with source evidence and unresolved constraints.; Clashes the author states (S4), on the page or on a --with neighbour's page, each with its source page.

Source: [_geometry-harness/adviceprobe/Program.cs](../_geometry-harness/adviceprobe/Program.cs) — `// ADVICEPROBE — turn a mod page's own words into ordering rules for orderprobe`


Modifiers (do not select a separate mode):

- `--catalog` — Set catalog for this invocation; consult the linked parser for accepted values.
- `--game` — Set game for this invocation; consult the linked parser for accepted values.
- `--instance` — Set instance for this invocation; consult the linked parser for accepted values.
- `--max-requests` — Set max requests for this invocation; consult the linked parser for accepted values.
- `--mod` — Set mod for this invocation; consult the linked parser for accepted values.
- `--no-import` — Set no import for this invocation; consult the linked parser for accepted values.
- `--offline` — Set offline for this invocation; consult the linked parser for accepted values.
- `--out` — Set out for this invocation; consult the linked parser for accepted values.
- `--page` — Set page for this invocation; consult the linked parser for accepted values.
- `--plugin-map` — Set plugin map for this invocation; consult the linked parser for accepted values.
- `--profile` — Set profile for this invocation; consult the linked parser for accepted values.
- `--refresh` — Set refresh for this invocation; consult the linked parser for accepted values.
- `--with` — Name a neighbour mod (repeatable): its page name becomes known, and its page is read for clashes and rules naming the --mod mod.

<a id="tool-blockprobe"></a>

### blockprobe

**Do placed obstacles cut walkable routes in this plugin or cell?**

Investigate a doorway or passage where a placed obstacle may obstruct the navmesh.

Inputs: One or more target plugins, positionally (judged as one set, each listed row naming its target), optionally a --order candidate, the winning navmesh and placed obstacle geometry.

Outputs: Route widths, blocked routes and conflict-file evidence.

Limit: Not covered: whether several changes, each passable alone, together cut a route NPCs need between two places. This probe judges one placed obstacle at a time: whether that object's solid cuts the walkable path line through a window around it. The collective check (functional.collective-route-connectivity in the world-edit-compatibility check registry) is unbuilt, and evaluation-reporting's SKILL.md records that the local connectivity engine does not certify NPC access between destinations.

Source: [_geometry-harness/blockprobe/Program.cs](../_geometry-harness/blockprobe/Program.cs) — `// Milestone 4, second half: the KNOWN-POSITIVE test.`


Modifiers (do not select a separate mode):

- `--band` — Set band for this invocation; consult the linked parser for accepted values.
- `--bos` — Set bos for this invocation; consult the linked parser for accepted values.
- `--exclude` — Set exclude for this invocation; consult the linked parser for accepted values.
- `--flags` — Set flags for this invocation; consult the linked parser for accepted values.
- `--game` — Refused: blockprobe reads the game folder from HOUSECARL_GAME_PATH (or the harness config); the argument exits 2 with that sentence.
- `--geometry` — Set geometry for this invocation; consult the linked parser for accepted values.
- `--instance` — Refused: blockprobe reads the instance from HOUSECARL_MO2_INSTANCE (or the harness config); the argument exits 2 with that sentence.
- `--list` — Set list for this invocation; consult the linked parser for accepted values.
- `--moved-ref-selftest` — Set moved ref selftest for this invocation; consult the linked parser for accepted values.
- `--order` — Set order for this invocation; consult the linked parser for accepted values.
- `--profile` — Refused: blockprobe reads the profile from HOUSECARL_MO2_PROFILE (or the harness config); the argument exits 2 with that sentence.
- `--rows` — --rows FILE: also write every listed route-cut row (Blocked, LikelyBlocked, NoLine, Unjudgeable; objects and navcut boxes) to FILE as JSON, uncapped by --list (blockprobe/RouteCutRows.cs); the route-cut check's adapter passes it.
- `--self-test` — Run the known-answer self-test over the probe's synthetic fixture; exit 0 when every case passes.
- `--state` — Set state for this invocation; consult the linked parser for accepted values.
- `--switch-map` — Set switch map for this invocation; consult the linked parser for accepted values.
- `--write-route-cut-fixture` — --write-route-cut-fixture DIR: write the route-cut fixture (_shared/RouteCutFixture.cs) that tests/test_route_cut_rows.py runs the real probe on; test-only.

<a id="tool-bosprobe"></a>

### bosprobe

**Which base-object swaps apply, and how do the parsed rules compare with the game's log?**

Diagnose a scene whose runtime object bases differ from its plugin records, using swap configuration and observed log evidence.

Inputs: Base Object Swapper configuration and optional game log.

Outputs: Parsed swaps and differences from observed swaps.

Source: [_geometry-harness/bosprobe/Program.cs](../_geometry-harness/bosprobe/Program.cs) — `// BOSPROBE — the Base Object Swapper reader, run over a load order and read back against the game.`


Modifiers (do not select a separate mode):

- `--all-swapped` — Set all swapped for this invocation; consult the linked parser for accepted values.
- `--anomalies` — Set anomalies for this invocation; consult the linked parser for accepted values.
- `--cell` — Set cell for this invocation; consult the linked parser for accepted values.
- `--compare-log` — Set compare log for this invocation; consult the linked parser for accepted values.
- `--disable` — Set disable for this invocation; consult the linked parser for accepted values.
- `--dump-log` — Set dump log for this invocation; consult the linked parser for accepted values.
- `--enable` — Set enable for this invocation; consult the linked parser for accepted values.
- `--game` — Set game for this invocation; consult the linked parser for accepted values.
- `--instance` — Set instance for this invocation; consult the linked parser for accepted values.
- `--json` — Set json for this invocation; consult the linked parser for accepted values.
- `--list` — Set list for this invocation; consult the linked parser for accepted values.
- `--no-apply` — Set no apply for this invocation; consult the linked parser for accepted values.
- `--order` — Set order for this invocation; consult the linked parser for accepted values.
- `--profile` — Set profile for this invocation; consult the linked parser for accepted values.
- `--ref` — Set ref for this invocation; consult the linked parser for accepted values.
- `--resolve` — Set resolve for this invocation; consult the linked parser for accepted values.

<a id="tool-build"></a>

### build

**How do I build SkyWright and its rulebook into the bin layout?**

After any change, build Debug and Release before running the checks or the app.

Inputs: A SkyWright checkout with the houseCARL submodule and the .NET 9 SDK.

Outputs: bin/Debug and bin/Release with the app, probes, tools, data and the layout marker.

Source: [scripts/build.ps1](../scripts/build.ps1) — `# Builds SkyWright: houseCARL's rulebook when it is missing`


<a id="tool-buildlayout"></a>

### buildlayout

**Where are the built probe, runcontrol and server programs for the Python tools?**

Call from Python instead of spelling a path into bin, so every tool reads the build marker the same way.

Inputs: The checkout's build marker skywright-layout.json and SKYWRIGHT_CONFIG.

Outputs: Paths into the build of the chosen configuration.

Source: [_geometry-harness/buildlayout.py](../_geometry-harness/buildlayout.py) — `Where SkyWright's built programs are, for the Python tools.`


<a id="tool-bundleenrich"></a>

### bundleenrich

**Which code adds evidence derived after measurement to an evaluation bundle, and how is it verified?**

Read or change what evalrun enrich writes and what verify-bundle checks about it.

Inputs: A verified bundle; for names, the plan's plugin files and a houseCARL MCP server.

Outputs: enrichment/<kind>/ files and manifest rows; verify-bundle's enrichment defects.

Source: [_geometry-harness/bundleenrich.py](../_geometry-harness/bundleenrich.py) — `bundleenrich - evidence derived after measurement, added to an evaluation bundle`


<a id="tool-capture-acquire"></a>

### capture-acquire

**How can I acquire a verified camera for one evidence-bound reporting request?**

Turn a reporting capture request into a planner invocation while preserving its source and configuration bindings.

Inputs: Evidence-bound capture request and configured renderer/game access.

Outputs: Verified camera/capture receipt or named refusal.

Source: [integration/housecarl/capture_acquire.py](../integration/housecarl/capture_acquire.py) — `Acquire a verified camera for ONE reporting capture request, through the shared planner.`


<a id="tool-capture-acquire.acquire"></a>

### capture-acquire.acquire

**Can this report request obtain a verified camera and binding receipt?**

Resolve one report capture request through the planner and retain its qualification receipt.

Selector: `acquire`

Inputs: Evidence-bound capture request and configured renderer/game access.

Outputs: Verified camera/capture receipt or named refusal.

Source: [integration/housecarl/capture_acquire.py](../integration/housecarl/capture_acquire.py) — `add_parser(`


<a id="tool-capture-acquire.capture"></a>

### capture-acquire.capture

**How do I render a review-reel vignette's without, with and highlight frames at its acquired camera?**

Turn a camera-ready vignette request into the three frames and the media items each setup's bundle admits.

Selector: `capture`

Inputs: A camera-ready acquisition, its storyboard packet, and the candidate files of the setups with and without the evaluated mod.

Outputs: Frames per state, media-<arm>.json per setup and capture-result.json (captured, or refused with its sentence).

Source: [integration/housecarl/capture_acquire.py](../integration/housecarl/capture_acquire.py) — `def cmd_capture(`


<a id="tool-capture-acquire.navmesh-control"></a>

### capture-acquire.navmesh-control

**Does the setup without the evaluated mod show the navmesh comparison's broken crossing at the same camera?**

Control a navmesh comparison's picture: re-measure the links in the setup without the mod and render at the acquired camera.

Selector: `navmesh-control`

Inputs: A camera-ready navmesh acquisition, its packet and the candidate file of the setup without the mod.

Outputs: control-result.json (no-broken-crossing or broken-crossing-present, with pixel counts), the frame and the nav-link console.

Source: [integration/housecarl/capture_navmesh.py](../integration/housecarl/capture_navmesh.py) — `def control(`


Modifiers (do not select a separate mode):

- `--acquisition` — capture: the camera-ready result of acquire for this request
- `--bind-packet` — also write a copy of the packet with this request's camera.pose filled in
- `--bundle` — where the request's source path resolves (default: the packet's directory)
- `--control` — capture: a navmesh comparison's control-result.json (navmesh-control at the same acquisition); required for a navmesh comparison, which is refused unless that setup's links are whole at the same pose
- `--order` — the setup without the evaluated mod's candidate file (navmesh-control)
- `--out` — where the acquisition result is written
- `--packet` — storyboard packet (evalreel storyboard-packet --out) or a bare capture plan
- `--points` — supplied measured feature points (see below), or `auto` to measure them with capture_points.py first (written to <receipts>/points.json)
- `--points-grid` — with --points auto: pick every N px (default 10)
- `--points-size` — with --points auto: witness frame size (default 320)
- `--receipts` — directory for the planner's JSON, worklist and log (default: <out>-receipts)
- `--request` — the capture request id to acquire
- `--size` — capture: the frame width in pixels (default 1280)
- `--state-order` — another state the capture renders this camera in, with its setup's candidate file; repeatable. The published camera passes the measured state's checks in each (C2168)
- `--synthetic-points` — with --points auto: the renderer is a stand-in, so the measured points are labelled synthetic
- `--vp` — viewprobe command, passed straight to shotplan (capture: the viewprobe.exe to render with, default the production build)
- `--with-order` — capture: the prediction setup's candidate file (viewprobe --order)
- `--without-order` — capture: the setup without the evaluated mod's candidate file

<a id="tool-capture-navmesh"></a>

### capture-navmesh

**How is a navmesh comparison's camera measured, accepted and refused?**

Change how a navmesh-version-comparison request is framed, which flags each state draws, or what its receipt and pixel checks accept.

Inputs: A navmesh-version-comparison capture request and viewprobe --nav-link rows.

Outputs: The acquisition result, per-state navmesh flags, and refusals in one sentence.

Source: [integration/housecarl/capture_navmesh.py](../integration/housecarl/capture_navmesh.py) — `The camera for a 'navmesh-version-comparison' capture request`


<a id="tool-capture-points"></a>

### capture-points

**Where on each object of a capture request can an eye-level camera outside the pair actually see a face?**

Give capture_acquire measured surface points the planner can verify, instead of points picked by hand.

Inputs: A storyboard packet's overlap-pair capture request and configured renderer/game access.

Outputs: A points document (capture_acquire's --points schema) with floor, bearing window and witness, or a one-sentence refusal.

Source: [integration/housecarl/capture_points.py](../integration/housecarl/capture_points.py) — `Measured points on a face of each capture participant that an eye-level camera outside the pair can see.`


<a id="tool-capture-points.pick"></a>

### capture-points.pick

**Which measured point on each participant does an eye-level camera see, and from which bearings?**

Measure the points for one overlap-pair capture request before planning its camera.

Selector: `pick`

Inputs: A storyboard packet's overlap-pair capture request and configured renderer/game access.

Outputs: A points document with floor, bearing window and witness, or a one-sentence refusal.

Source: [integration/housecarl/capture_points.py](../integration/housecarl/capture_points.py) — `add_parser(`


Modifiers (do not select a separate mode):

- `--cell` — viewprobe's cell, when the request's location does not name one
- `--grid` — pick every N px of the witness frame (default 10)
- `--out` — the points document (capture_acquire's --points schema)
- `--packet` — storyboard packet or bare capture plan
- `--request` — capture request id
- `--size` — witness frame size in px (default 320)
- `--synthetic` — the renderer is a stand-in: label the points SYNTHETIC
- `--vp` — viewprobe command (default: the built in-tree one)
- `--work` — where the three runs' consoles and frames go (default <out>-work)

<a id="tool-collprobe"></a>

### collprobe

**How much Havok collision can be decoded, and how does its unit scale compare with render geometry?**

Establish collision-decoding coverage and coordinate units before interpreting a Havok-based measurement.

Inputs: NIF collision meshes and associated render geometry.

Outputs: Decoded collision statistics and scale comparisons.

Source: [_geometry-harness/collprobe/Program.cs](../_geometry-harness/collprobe/Program.cs) — `// collprobe — roadmap 4.7 Part A: PRICE the Havok collision decoder over the vanilla mesh set, and MEASURE the`


Modifiers (do not select a separate mode):

- `--chunk-mode` — Set chunk mode for this invocation; consult the linked parser for accepted values.
- `--convex-dump` — Set convex dump for this invocation; consult the linked parser for accepted values.
- `--dump` — Set dump for this invocation; consult the linked parser for accepted values.
- `--limit` — Set limit for this invocation; consult the linked parser for accepted values.
- `--mesh` — Set mesh for this invocation; consult the linked parser for accepted values.
- `--transform-transposed` — Set transform transposed for this invocation; consult the linked parser for accepted values.

<a id="tool-conflictdiff"></a>

### conflictdiff

**Which conflicts did a candidate patch remove, preserve or introduce?**

Evaluate a proposed patch against a retained baseline, with stable finding identities on both sides.

Inputs: Two conflict-file results with comparable scope.

Outputs: Removed, retained and introduced findings with accounting.

Source: [_geometry-harness/conflictdiff/Program.cs](../_geometry-harness/conflictdiff/Program.cs) — `// CONFLICT-SET DIFF — the second half of the differential chain`


Modifiers (do not select a separate mode):

- `--compare-states` — Set compare states for this invocation; consult the linked parser for accepted values.
- `--tolerance` — Set tolerance for this invocation; consult the linked parser for accepted values.

<a id="tool-contactprobe"></a>

### contactprobe

**Has a moved or missing support stranded another object, and what contact or overlap-volume evidence exists?**

**Has another mod's terrain left a placed object floating in the air or buried in the ground?**

Investigate floating clutter, unsupported furniture, or suspected interpenetration after two mods change the same space.

Inputs: Selected plugins/cells, placed geometry and contact criteria.

Outputs: Support/contact findings and requested strict overlap evidence; incomplete geometry remains named.

Limit: The default pass asks whether a host that a plugin moved, relocated or removed stranded what rested on or was mounted against it, so it reads a weapon rack held by its neighbours as supported even when another mod disables the room around it. That case is the --empty-space mode (LEFT IN EMPTY SPACE), which refuses exteriors because the land under a fixture is not read.

Limit: The --terrain mode reads the terrain as a support, but the engine's triangulation of a heightmap quad is not in the file: where the two possible diagonals disagree the row is cannot tell. Burial is exact for a uniform shift of the ground and a lower bound otherwise. A rotation-only override after the placement owner is not attributed. Nothing it reports is verified in game.

Source: [_geometry-harness/contactprobe/Program.cs](../_geometry-harness/contactprobe/Program.cs) — `// PLACEMENT CONTACT — the geometry half of stranded support and orphaned attachment`


Modifiers (do not select a separate mode):

- `--alpha-visibility` — With --surface, a render pair whose crossing lies on an alpha-tested or alpha-blended triangle gains a visibility block: the part of the crossing curve that passes through drawn texels of both members (texture alpha at mip 0, bilinear; alpha test as the interval > .. >= its threshold; blend-only shapes at 0.5 with readings at 0.1 and 0.9 and a threshold-sensitive flag; effect shaders an upper bound), with each texture's VFS source and the base record's alternate texture set applied. A swept witness whose crossing is not opaque x opaque gains visibleMotion (a visible witness at a sampled pose, or no visible crossing at N sampled poses, which is not a never). A missing or unreadable texture or an animated alpha or texture transform refuses the reading by name. Default off: outputs unchanged.
- `--anchor` — With --overlap-json, member A of each pair is an anchor (editor marker, idle marker, placed actor, or each furniture entry position) and B a partner: the point, the vertical line and the humanoid body (route constants) at the anchor against B's opaque render surface, the body deciding; Lean furniture excludes its declared support side. With --motion swept, a partner refused for its live pose is tested over every sample of its swept region: witness (a sampled pose meets the body), never (every sample clear by more than its displacement bound), cannot_tell otherwise. Default off: outputs unchanged.
- `--box-contact` — With --overlap-json, for each input boxContact row {pairKey, a, b, boxOwner A|B, boxes [{label, min, max}]}, whether each supplied world box (for example a particle region from an overlapprobe manifest's reach parts) meets the other member's placed render surface: opaque triangles, and any material (exact triangle-box test). With --motion swept, a member refused for its live pose is tested through its swept region: witness / never / cannot_tell per box (opaqueMeets true / false / null). Default off: no other output changes.
- `--burial-share` — With --terrain, the least share (0 to 1) of the body's surface in view as authored that the winning ground must cover by more than --burial-threshold for Buried, read on an area-weighted sampling of the body. No default: the value is frozen in a run plan.
- `--burial-threshold` — With --terrain, the least depth (u) by which the winning ground covers a body's surface that was at or above the authored ground (a part already below it counts nothing), that can read Buried. No default: the value is frozen in a run plan.
- `--cache-budget` — --cache-budget SIZE|auto: the byte budget the per-placement contact caches share, and in the default pass the decoded meshes too (least recently used out first; an evicted mesh is decoded again from the same bytes; e.g. 2G, 512M). auto (default) = the smaller of 20 % of the memory the runtime may use and 1 GiB, on either memory route (see --memory-route). Output does not depend on the budget (an evicted grid is rebuilt from the same inputs); a smaller budget can cost time. An explicit size is kept as asked and the memory route only warns.
- `--empty-space` — --empty-space FILE selects the empty-space mode: every dependent-scale fixture within reach of a moved host, against that host's mover. LEFT IN EMPTY SPACE when its assembly (what it touches within 4 u, transitively, and the items its weapon racks mount) reached a floor or wall the mover removed or moved, and reaches none under the winner. Interiors only. Rows go to FILE; --emit writes the class as EMPTY-SPACE rows; --base re-measures an emit's rows. A mode of its own: the default pass's outputs are unchanged.
- `--float-threshold` — With --terrain, the least gap (u) under a body, over the ground and every reference support, that reads Floating. No default: the value is frozen in a run plan.
- `--force-enable` — --force-enable FORMKEY[,FORMKEY...] reads each listed initially-disabled singleton (no enable parent, not itself an enable parent) as enabled after load in every arm, labelled 'enabled by hand (its enabler is not sourced by the switch census)'; a switch member, an enable-parented or already-enabled reference is refused by name (exit 2). The output names every forced reference (forcedEnabled). Default off: outputs unchanged.
- `--live-pose` — With --surface, choose the live-pose rule: targeted (default; refuses only what the file shows moving a shape) or blanket (the earlier rule: any controller on the chain refuses).
- `--memory-route` — auto|fast|lean|ask (default auto), default pass: decided before the host loop from live data, physical and free memory and any limit (GC heap hard limit, job object). auto: fast when the memory is there; lean when a limit or the machine is too small; when other programs hold the memory, a warning naming them, then lean as the unattended default. fast is the user's answer after closing them; lean the user's refusal; ask stops at that warning with exit 5 and one sentence. A session running contactprobe for a user passes ask, asks the user, and reruns with fast or lean; ordersweep and the evaluation controller never pass it. Nothing fits: exit 2, one sentence. The route, case, readings and warning are in the console and in <emit>.memory.json, which ordersweep copies into summary.json; emit and rows do not depend on the route.
- `--motion` — With --surface or --volume, measure a member whose stored pose is not its live pose against its motion instead of refusing it: swept (witness / never / cannot_tell over every animation the files can play, labelled with the animation, cycle, base record type and trigger) or pose:NAME (closed, open, rest - the placed reference's open-by-default flag decides it - state:NAME, <animation>@start|@end). With swept, a pair whose members both move is measured on independent clocks: never; witness only for a held pose (rest, a CLAMP end, a held graph state) against any time of the other with no XESP / XLKR / XAPR link between them in the files; cannot_tell otherwise, naming both times or the link; no volume. A member refused only for a NiPathInterpolator is measured as a tube around its spline (both tangent readings, grown by the subtree radius): never or cannot_tell, never a witness. Swept also applies to --anchor and --box-contact. Default off: outputs unchanged.
- `--motion-budget` — With --motion swept, the samples per animation before the region is reported not converged (default 4096).
- `--motion-delta` — With --motion swept, the target bound (u) on how far the moving part moves between samples (default 0.5).
- `--phase-timers` — FILE: the contact code's own time split, for measuring where the time goes: BestContact, PairContact and Judge by sub-phase (dependent soup, broad phase, the caller's placement and base lookups, host grid, the core contact, the radius shells, extent, the authored-depth callback, coverage), each per-placement cache's hits, misses and rebuilds after eviction, the core contact's steps, and how many Measure calls repeat a pair already measured; printed after the run, recorded as run features and written to FILE as JSON. Changes no output.
- `--population` — The plan's population file. Default pass: a moved host is judged only when it or a dependent within reach is in the population or its region (the 3x3 cell ring around every position a delta reference takes), so a support another mod placed or removed stays in. With --empty-space: only fixtures of the population's interior cells (closed over references relocated between interiors and rack links) are reported; a fixture ever placed in an exterior is judged too and not reported, so every reported row is the whole-order run's; --base is refused. The emit's population names the file's hash.
- `--population-audit` — With --population, EMIT OUT: for each row of another emit (a whole-order run's), whether it is in this population (and, with --empty-space, by which rule).
- `--reverse-of` — BASELINE_EMIT, with --base and --emit, default pass only: a reverse run that carries the baseline emit's rows and coverage instead of judging every moved host again, and re-measures only the --base rows; refused by name unless the baseline emit is this run's own (probe, configuration, population, state, measurement policy, instance, and the coverage lines fixed before the host loop). The emit equals a full reverse's.
- `--surface` — With --overlap-json, write each pair's measured surface contact (crossing length, coplanar areas, area buried in a closed solid), render and collision separately; refusals keep their reason. A pair row may carry aPose/bPose {"dz": d}: that member is measured translated by d in z (a rest pose) and the record echoes it; --volume reads it too, the --json pair review excludes such a row and --anchor refuses it. A pair whose single-precision world placement collapses a triangle is measured in B's frame and labelled pairFrame; particle systems are set aside from a render surface and named (particlesA / particlesB).
- `--terrain` — Selects the terrain-support mode: every exterior placed reference that rested on the ground (within 4 u) in its placement owner's view, against the winning LAND under both heightmap diagonals and the winning arm's collision supports (render never supports). A row floats or is buried where a plugin the owner could not see reshaped the ground; Introduced Blind Fixed rows of mod plugins are the class and the only emitted rows (TERRAIN); Seen, Inherited (Bethesda on Bethesda), USSEP, Movable and cannot-tell rows are counted. Prints the LAND plumbing and the Inherited floor at 8 / 32 / 128 u before any class count; with --absolute, the absolute floor as shipped first. --emit, --base and --json as in the other modes; the emit declares its population. Requires --float-threshold and --burial-threshold.
- `--terrain-sample` — With --terrain --absolute, read the absolute floor on one reference in N (deterministic by FormKey hash, stated on its line); the differential pass is always whole.
- `--threads` — N: the host loop's measuring threads (default 8; 1 is the sequential loop). Results do not depend on it. An explicit N is kept as asked by the memory route, which then only warns.

<a id="tool-corridorprobe"></a>

### corridorprobe

**What are the widths of these navmesh corridors, and how do live paths compare with the route rule?**

Evaluate a narrow passage using navmesh width and, when supplied, measured travel evidence.

Inputs: One plugin and selected cell navmesh, optional recorded live paths.

Outputs: Corridor widths and route-rule comparisons.

Source: [_geometry-harness/corridorprobe/Program.cs](../_geometry-harness/corridorprobe/Program.cs) — `// corridorprobe — choose and measure navmesh CORRIDORS from the records, for roadmap 4.7 Part C; and read the in-game`


Modifiers (do not select a separate mode):

- `--ascii` — Set ascii for this invocation; consult the linked parser for accepted values.
- `--at` — Set at for this invocation; consult the linked parser for accepted values.
- `--band` — Set band for this invocation; consult the linked parser for accepted values.
- `--band-top` — Set band top for this invocation; consult the linked parser for accepted values.
- `--bos` — Set bos for this invocation; consult the linked parser for accepted values.
- `--flags` — Set flags for this invocation; consult the linked parser for accepted values.
- `--heading` — Set heading for this invocation; consult the linked parser for accepted values.
- `--moved-ref-selftest` — Set moved ref selftest for this invocation; consult the linked parser for accepted values.
- `--plugin` — Set plugin for this invocation; consult the linked parser for accepted values.
- `--ref` — Set ref for this invocation; consult the linked parser for accepted values.
- `--span` — Set span for this invocation; consult the linked parser for accepted values.
- `--state` — Set state for this invocation; consult the linked parser for accepted values.
- `--step` — Set step for this invocation; consult the linked parser for accepted values.

<a id="tool-crossprobe"></a>

### crossprobe

**Does the surface-crossing primitive match hand-built known answers?**

Qualify the geometric crossing calculation with synthetic controls before trusting its downstream findings.

Inputs: Built-in synthetic surface geometry cases.

Outputs: Known-answer assertion results and process exit.

Source: [_geometry-harness/crossprobe/Program.cs](../_geometry-harness/crossprobe/Program.cs) — `// Milestone 4c — SYNTHETIC PROOF for the surface-crossing narrow phase.`


<a id="tool-depprobe"></a>

### depprobe

**Which declared placement dependencies lost or moved their hosts?**

Investigate objects attached to supports that another plugin deletes or relocates.

Inputs: Placement dependency rules and winning plugin records.

Outputs: Missing or moved host/dependent findings.

Source: [_geometry-harness/depprobe/Program.cs](../_geometry-harness/depprobe/Program.cs) — `// PLACEMENT DEPENDENCY — the records-layer pass for stranded support and orphaned attachment`


Modifiers (do not select a separate mode):

- `--bos` — Set bos for this invocation; consult the linked parser for accepted values.
- `--disable` — Set disable for this invocation; consult the linked parser for accepted values.
- `--enable` — Set enable for this invocation; consult the linked parser for accepted values.
- `--game` — Set game for this invocation; consult the linked parser for accepted values.
- `--instance` — Set instance for this invocation; consult the linked parser for accepted values.
- `--json` — Set json for this invocation; consult the linked parser for accepted values.
- `--list` — Set list for this invocation; consult the linked parser for accepted values.
- `--order` — Set order for this invocation; consult the linked parser for accepted values.
- `--profile` — Set profile for this invocation; consult the linked parser for accepted values.
- `--self-test` — Run the known-answer self-test over the probe's synthetic fixture; exit 0 when every case passes.
- `--threshold` — Set threshold for this invocation; consult the linked parser for accepted values.

<a id="tool-depthsurvey"></a>

### depthsurvey

**Does the measured outward depth survey admit or block this candidate eye?**

Investigate candidate-camera clearance using retained depth measurements around a subject point.

Inputs: Depth buffer, camera projection and candidate eye.

Outputs: Directional clearance measurements and admission/refusal.

Source: [_geometry-harness/viewprobe/tools/depthsurvey.py](../_geometry-harness/viewprobe/tools/depthsurvey.py) — `depthsurvey.py -- an outward six-face depth survey FROM a declared world point, a lookup that`


<a id="tool-depthsurvey.figure"></a>

### depthsurvey.figure

**What does the survey's measured free region look like at plan scale?**

Inspect the measured clearance region in a plan diagram before selecting a viewpoint.

Selector: `figure`

Inputs: Depth buffer, camera projection and candidate eye.

Outputs: Directional clearance measurements and admission/refusal.

Source: [_geometry-harness/viewprobe/tools/depthsurvey.py](../_geometry-harness/viewprobe/tools/depthsurvey.py) — `add_parser(`


<a id="tool-depthsurvey.lookup"></a>

### depthsurvey.lookup

**Is this proposed eye admitted by the retained depth survey?**

Test another proposed eye against an existing survey without acquiring a new depth set.

Selector: `lookup`

Inputs: Depth buffer, camera projection and candidate eye.

Outputs: Directional clearance measurements and admission/refusal.

Source: [_geometry-harness/viewprobe/tools/depthsurvey.py](../_geometry-harness/viewprobe/tools/depthsurvey.py) — `add_parser(`


<a id="tool-depthsurvey.selftest"></a>

### depthsurvey.selftest

**Do synthetic depth arrays produce the expected projection and clearance results?**

Qualify projection and clearance arithmetic with synthetic buffers before interpreting scene measurements.

Selector: `selftest`

Inputs: Depth buffer, camera projection and candidate eye.

Outputs: Directional clearance measurements and admission/refusal.

Source: [_geometry-harness/viewprobe/tools/depthsurvey.py](../_geometry-harness/viewprobe/tools/depthsurvey.py) — `add_parser(`


<a id="tool-depthsurvey.survey"></a>

### depthsurvey.survey

**What free space does an outward six-face depth survey measure here?**

Measure the space around a subject before testing candidate camera locations against it.

Selector: `survey`

Inputs: Depth buffer, camera projection and candidate eye.

Outputs: Directional clearance measurements and admission/refusal.

Source: [_geometry-harness/viewprobe/tools/depthsurvey.py](../_geometry-harness/viewprobe/tools/depthsurvey.py) — `add_parser(`


Modifiers (do not select a separate mode):

- `--camera` — a camera to look up (repeatable). The expectation, when given, is what makes a row a false discard or a false admit
- `--cap` — how far an open direction is drawn before it is called open (default 1500)
- `--check-dist` — self-check: how far the dump's ray distance may differ from viewprobe's printed whole-unit pick (default 1.0)
- `--check-pos` — self-check: per-axis world position tolerance (default 1.5)
- `--erode` — erode the survey depth (default 0)
- `--face-yaw-offset` — rotate the four side faces, so a bearing that sat at a face centre sits at a face edge instead (default 0)
- `--game` — game directory
- `--height` — Set height for this invocation; consult the linked parser for accepted values.
- `--instance` — MO2 instance directory
- `--label` — a name for this survey, kept in survey.json
- `--md` — also write a Markdown table
- `--offset` — signed offset from the declared point to the survey EYE (default 0,0,0)
- `--out` — directory for the renders, the dumps, the logs and survey.json
- `--point` — the DECLARED world point the survey is about
- `--profile` — MO2 profile
- `--radius` — half-width of the drawn window in world units (default: fits the cameras)
- `--size` — frame size (default 512)
- `--slice-elevation` — elevation slice (repeatable). Default: 0 and each camera's own elevation
- `--survey` — a survey.json (repeatable)
- `--title` — Set title for this invocation; consult the linked parser for accepted values.
- `--variant-label` — label for the Nth --survey (repeatable); default is its directory name
- `--vp` — Set vp for this invocation; consult the linked parser for accepted values.
- `--width` — Set width for this invocation; consult the linked parser for accepted values.

<a id="tool-differential-sweep"></a>

### differential-sweep

**How does every candidate patch change the detected conflict set?**

Compare proposed compatibility fixes against the same baseline before choosing a candidate.

Inputs: Candidate patch set, the MO2 instance (-Instance or HOUSECARL_MO2_INSTANCE; never the harness config) and configured probe paths.

Outputs: Per-patch before/after conflict results.

Source: [_geometry-harness/differential-sweep.ps1](../_geometry-harness/differential-sweep.ps1) — `# DIFFERENTIAL SWEEP`


<a id="tool-driftprobe"></a>

### driftprobe

**Do quest targets and their supporting placement relationships still match the winning world?**

Check whether quest-related placements remain usable after load-order winners alter the surrounding scene.

Inputs: Quest aliases/targets and winning placement relationships.

Outputs: Target drift evidence and unresolved dependencies.

Limit: Not covered: whether a scene still plays once its participants are moved apart. This probe reports quest aliases that a scene or package says belong together and that have drifted apart in the winning world; whether the scene then plays is a game question (world-edit-compatibility SKILL.md §7).

Source: [_geometry-harness/driftprobe/Program.cs](../_geometry-harness/driftprobe/Program.cs) — `// SCENARIO CONSISTENCY — does a quest still describe the world it is standing in?`


Modifiers (do not select a separate mode):

- `--actor` — Set actor for this invocation; consult the linked parser for accepted values.
- `--bos` — Set bos for this invocation; consult the linked parser for accepted values.
- `--emit` — Set emit for this invocation; consult the linked parser for accepted values.
- `--game` — Set game for this invocation; consult the linked parser for accepted values.
- `--generic` — Set generic for this invocation; consult the linked parser for accepted values.
- `--instance` — Set instance for this invocation; consult the linked parser for accepted values.
- `--list` — Set list for this invocation; consult the linked parser for accepted values.
- `--moved-ref-selftest` — Set moved ref selftest for this invocation; consult the linked parser for accepted values.
- `--near` — Set near for this invocation; consult the linked parser for accepted values.
- `--no-reach` — Set no reach for this invocation; consult the linked parser for accepted values.
- `--order` — Set order for this invocation; consult the linked parser for accepted values.
- `--profile` — Set profile for this invocation; consult the linked parser for accepted values.
- `--self-test` — Run the known-answer self-test over the probe's synthetic fixture; exit 0 when every case passes.

<a id="tool-editkinds"></a>

### editkinds

**Which kinds of edit does a patch make to a record, in the fixed vocabulary (ADD, DISABLE, MOVE, ENABLE-PARENT, FIELD:<name> and the rest)?**

Classify a patch's or a mod's changed fields against the record it overrides, one edit per record per kind.

Inputs: One record's changed fields against its unpatched winner, as a record reader names them.

Outputs: The record's edits, one per kind, with their field paths and whether the kind is bookkeeping.

Source: [_geometry-harness/editkinds.py](../_geometry-harness/editkinds.py) — `editkinds - the patch edit-kind vocabulary and its classifier, one place for every reader of patch edits.`


<a id="tool-eulerprobe"></a>

### eulerprobe

**Which Euler rotation composition agrees with the measured geometry?**

Resolve uncertainty about transform multiplication order before changing placement geometry math.

Inputs: Measured geometry and candidate rotation compositions.

Outputs: Comparison measurements for rotation order.

Source: [_geometry-harness/eulerprobe/Program.cs](../_geometry-harness/eulerprobe/Program.cs) — `// EULER ORDER — settling a question the charter has carried as "evidence, not proof" since day one.`


<a id="tool-evalbase"></a>

### evalbase

**Which helpers do the evaluation tools on both sides of the houseCARL integration fence share?**

Read a plugin's TES4 header, find the built-in controller, or build the bundle check's command from a Python tool.

Inputs: A plugin file, a machine file's runcontrol, or HOUSECARL_RUNCONTROL.

Outputs: A header dict, the controller's path, or the verify-bundle command; Refusal with one sentence.

Source: [_geometry-harness/evalbase.py](../_geometry-harness/evalbase.py) — `evalbase - what the evaluation tools on both sides of the houseCARL integration fence share.`


<a id="tool-evalpipeline"></a>

### evalpipeline

**How do I continue one mod's evaluation from its evidence bundle to the report and reel?**

Report on a mod after runcontrol has taken its run folder through the bundle.

Inputs: A selection entry, a machine file, and the run folder runcontrol carried through the bundle.

Outputs: The run folder's precedent, report-plan, capture, image-observations, report, reel and deliver stages; RUN/stages.json binds each stage.

Source: [integration/housecarl/evalpipeline.py](../integration/housecarl/evalpipeline.py) — `evalpipeline - the stages of one mod's evaluation after its evidence bundle`


<a id="tool-evalpipeline.capture-rounds"></a>

### evalpipeline.capture-rounds

**How does the capture stage hold the machine slot once across all its rounds?**

Run by the capture stage inside its one slot hold; not for direct use.

Selector: `capture-rounds`

Inputs: A selection entry, the machine file and the run folder of an evaluation at its capture stage.

Outputs: RUN/capture/captures.json, rounds.json and capture-rounds.json (accepted with the stage's binding, or the refusal sentence).

Source: [integration/housecarl/evalpipeline.py](../integration/housecarl/evalpipeline.py) — `add_parser(`


<a id="tool-evalpipeline.run"></a>

### evalpipeline.run

**How do I run every stage after the bundle of one mod's evaluation not yet accepted, in order?**

Report on a mod, or continue its report after a stop, on a run folder runcontrol carried through the bundle.

Selector: `run`

Inputs: A selection entry, a machine file, and the run folder runcontrol carried through the bundle.

Outputs: The run folder's precedent, report-plan, capture, image-observations, report, reel and deliver stages; RUN/stages.json binds each stage.

Source: [integration/housecarl/evalpipeline.py](../integration/housecarl/evalpipeline.py) — `add_parser(`


<a id="tool-evalpipeline.status"></a>

### evalpipeline.status

**Which stages of an evaluation run are accepted, and where did it stop?**

Read a run's progress without rerunning it.

Selector: `status`

Inputs: A selection entry, a machine file, and the run folder runcontrol carried through the bundle.

Outputs: The run folder's precedent, report-plan, capture, image-observations, report, reel and deliver stages; RUN/stages.json binds each stage.

Source: [integration/housecarl/evalpipeline.py](../integration/housecarl/evalpipeline.py) — `add_parser(`


Modifiers (do not select a separate mode):

- `--entry` — the mod's selection entry (JSON)
- `--machine` — the machine file: lab, game, heavy-stage prefix, run-local stage commands
- `--out` — the run folder
- `--through` — stop after this stage

<a id="tool-evalrecords"></a>

### evalrecords

**How do I capture an evaluation's record checks (dangling references, contested fields, cell attributes, registration and dialogue, missing assets) for every arm?**

Complete the registry's mcp-only checks of a planned evaluation with fresh, arm-bound reader evidence.

Inputs: An evaluation plan, the dependency baseline, the lab's Data and mods folders, the base profile's INIs and a houseCARL server.

Outputs: Per arm: the session's request and response transcripts; per check and arm: rows and an import document.

Source: [_geometry-harness/evalrecords.py](../_geometry-harness/evalrecords.py) — `evalrecords - the record capture for an evaluation's mcp-only checks, one reader session per arm.`


<a id="tool-evalrecords.capture"></a>

### evalrecords.capture

**How do I capture every planned arm's mcp-only checks and write their import documents?**

Complete the record checks of a frozen evaluation plan.

Selector: `capture`

Inputs: An evaluation plan, the dependency baseline, the lab's Data and mods folders, the base profile's INIs and a houseCARL server.

Outputs: Per arm: the session's request and response transcripts; per check and arm: rows and an import document.

Source: [_geometry-harness/evalrecords.py](../_geometry-harness/evalrecords.py) — `add_parser(`


Modifiers (do not select a separate mode):

- `--arm` — capture only this arm (repeatable)
- `--arm-deadline-min` — stop one arm's record read after this many minutes
- `--baseline-from` — the run folder's prepare/preparation.json (its dependency baseline)
- `--checkpoints` — a folder that keeps each finished arm across captures; an arm is reused only when its key (arm content, plan, cells, evalrecords' code, reader) matches
- `--data` — the game's Data folder
- `--mods` — the lab instance's mods folder
- `--out` — the capture folder
- `--profile-dir` — the base profile, whose INIs every view copies
- `--reader-cmd` — the houseCARL server command line
- `--reader-data` — a private HOUSECARL_DATA_DIR for the reader
- `--run` — the evaluation's run folder (plan.json)
- `--summary-to` — for one arm of a capture running several: write its result here and leave the capture's own files to the parent
- `--workers` — how many arms read at once: 1 (the default) in this process, a number as a ceiling, or auto from available memory; the choice and every attempt are in capture-workers.json

<a id="tool-evalrecords_workers"></a>

### evalrecords_workers

**How many of a capture's arms read at once, from what memory, and what happens to an arm stopped or refused beside others?**

Read or change how evalrecords chooses workers from memory, schedules arms and judges each attempt.

Inputs: The capture's arms and their estimated need, and the machine's memory readings.

Outputs: One capture process per arm through the shared Runner, and each attempt's record for capture-workers.json.

Source: [_geometry-harness/evalrecords_workers.py](../_geometry-harness/evalrecords_workers.py) — `evalrecords_workers - how many of a capture's arms read at once, and running them side by side.`


<a id="tool-evalreel"></a>

### evalreel

**How do I make a narrated diagnostic reel or storyboard from an evaluation presentation?**

Present evaluation findings as a reviewable sequence of scenes with bound evidence and narration.

Inputs: Evaluation presentation and prepared local narration/media.

Outputs: Diagnostic video reel or storyboard and media manifest.

Source: [integration/housecarl/evalreel.py](../integration/housecarl/evalreel.py) — `evalreel — the diagnostic reel of an evaluation presentation (Q45, D6).`


<a id="tool-evalreel.render"></a>

### evalreel.render

**How do I render the presentation as a diagnostic video reel?**

Assemble the reviewed presentation into ordered video segments with the chosen audio inputs.

Selector: `render`

Inputs: Evaluation presentation and prepared local narration/media.

Outputs: Diagnostic video reel or storyboard and media manifest.

Source: [integration/housecarl/evalreel.py](../integration/housecarl/evalreel.py) — `add_parser(`


<a id="tool-evalreel.storyboard-packet"></a>

### evalreel.storyboard-packet

**Which planned scenes need human review before video assembly?**

Prepare scenes for human review before accepting visual direction for the final reel.

Selector: `storyboard-packet`

Inputs: Evaluation presentation and prepared local narration/media.

Outputs: Diagnostic video reel or storyboard and media manifest.

Source: [integration/housecarl/evalreel.py](../integration/housecarl/evalreel.py) — `add_parser(`


Modifiers (do not select a separate mode):

- `--bundle` — where the plan's bundle-relative media paths resolve (default: source.bundleDir, relative to the presentation)
- `--chapters` — comma-separated chapter ids to include (default: all)
- `--dry-run` — print the segment plan and render nothing
- `--ffmpeg` — an explicit ffmpeg binary (first in the resolution order)
- `--font` — the TrueType font the reel's stills are drawn with (default: HOUSECARL_REEL_FONT, then the skill's reel.stillFont; never a built-in font)
- `--max-seconds` — drop trailing finding chapters over this budget (default: the plan's settings.reel.maxSeconds)
- `--out` — output directory (reel.mp4, reel.srt, reel-map.json, reel-status.json, contact-sheet.png, cache/)
- `--presentation` — OUT/presentation.json (schema 1)
- `--storyboard` — reviewed storyboard packet; overlays visual direction in memory without changing presentation.json
- `--voice` — explicitly opt into an installed Windows WinRT voice; otherwise supply narrationAudio from evalspeech.py

<a id="tool-evalreel-speech"></a>

### evalreel-speech

**How can I synthesize a local WAV from text using a Windows voice?**

Supply narration through an explicitly chosen installed Windows voice when that engine is the intended delivery choice.

Inputs: Text, installed Windows voice and output path.

Outputs: Local WAV narration.

Source: [integration/housecarl/winrt-tts/Program.cs](../integration/housecarl/winrt-tts/Program.cs) — `[DllImport("kernel32.dll")] static extern uint SetErrorMode(uint mode);`


<a id="tool-evalreport"></a>

### evalreport

**How do I turn a verified evaluation bundle into reports, narration packets and an evidence index?**

Prepare reader-facing evidence from a completed evaluation whose bundle integrity can be verified.

Inputs: Verified evaluation bundle and presentation options.

Outputs: Human reports, narration packets and linked evidence index.

Source: [integration/housecarl/evalreport.py](../integration/housecarl/evalreport.py) — `evalreport - compile a verified schema-2 evaluation bundle into one presentation plan and, from that`


<a id="tool-evalreport.narrate-apply"></a>

### evalreport.narrate-apply

**Does the proposed narration preserve every required factual binding, and did the fidelity judge find its meaning kept?**

Incorporate proposed prose only after checking its factual tokens against the supplied evidence bindings and a fidelity judgement made for its exact text.

Selector: `narrate-apply`

Inputs: Verified evaluation bundle and presentation options.

Outputs: Human reports, narration packets and linked evidence index.

Source: [integration/housecarl/evalreport.py](../integration/housecarl/evalreport.py) — `add_parser(`


<a id="tool-evalreport.narrate-packet"></a>

### evalreport.narrate-packet

**Which numbers and names may a narrator use for each finding?**

Give an optional writer the exact facts permitted in each finding's narrative, the plain-words instructions and the glossary; with --answers, give the fidelity judge each answer beside its finding.

Selector: `narrate-packet`

Inputs: Verified evaluation bundle and presentation options.

Outputs: Human reports, narration packets and linked evidence index.

Source: [integration/housecarl/evalreport.py](../integration/housecarl/evalreport.py) — `add_parser(`


<a id="tool-evalreport.plan"></a>

### evalreport.plan

**What presentation plan can be built from this verified evaluation bundle?**

Prepare the report structure only after the supplied evidence package passes integrity checks.

Selector: `plan`

Inputs: Verified evaluation bundle and presentation options.

Outputs: Human reports, narration packets and linked evidence index.

Source: [integration/housecarl/evalreport.py](../integration/housecarl/evalreport.py) — `add_parser(`


<a id="tool-evalreport.report"></a>

### evalreport.report

**How do I produce Markdown, HTML, PDF, and an evidence index from the presentation plan?**

Publish the planned findings and linked evidence in the requested document formats.

Selector: `report`

Inputs: Verified evaluation bundle and presentation options.

Outputs: Human reports, narration packets and linked evidence index.

Source: [integration/housecarl/evalreport.py](../integration/housecarl/evalreport.py) — `add_parser(`


<a id="tool-evalreport.triage"></a>

### evalreport.triage

**How can I read annotated overlap incidents as a finding catalogue?**

Review a large annotated overlap population as incidents with traceable member rows.

Selector: `triage`

Inputs: Verified evaluation bundle and presentation options.

Outputs: Human reports, narration packets and linked evidence index.

Source: [integration/housecarl/evalreport.py](../integration/housecarl/evalreport.py) — `add_parser(`


<a id="tool-evalreport.verify"></a>

### evalreport.verify

**Are the report's artifacts, links, and page counts still valid?**

Check a finished report for changed artifacts, broken references, or inconsistent pagination.

Selector: `verify`

Inputs: Verified evaluation bundle and presentation options.

Outputs: Human reports, narration packets and linked evidence index.

Source: [integration/housecarl/evalreport.py](../integration/housecarl/evalreport.py) — `add_parser(`


Modifiers (do not select a separate mode):

- `--also-bundle` — name a further evaluation of the same mods (repeatable); the report names it with the setups it measured, and its results are not joined into the counts
- `--answers` — narrate-apply: the narrator's answers; narrate-packet: write the fidelity judge's packet for these answers instead of the narrator's
- `--bundle` — Set bundle for this invocation; consult the linked parser for accepted values.
- `--capture-results` — capture_acquire.py capture results: a refused vignette becomes a still, its reason on the closing
- `--captures-final` — the capture stage is closed: the review reel pictures only clashes with captured frames and says in one sentence when a kind has none
- `--declare-patch` — declare a plugin as a patch under evaluation; repeat for several. Patch identity is never inferred from plugin membership, so an arm that differs only by an undeclared plugin is reported as an unstated difference. Use this for a plan whose candidates predate the declaration field. A file that shares its name with another (an author's same-named compatible version) is declared by its bytes, PLUGIN=sha256:<64 hex>, or by its mod folder, PLUGIN=folder:<folder>; a declaration that matches no setup's copy is refused.
- `--exhaustive` — list every row; no page limit (same as --mode exhaustive)
- `--figures-per-finding` — Set figures per finding for this invocation; consult the linked parser for accepted values.
- `--headline-unit` — what the verdict counts: relationships (the check's rows) or objects (the records they name); default: the reporting skill's headlineUnit
- `--judgements` — narrate-apply: the fidelity judge's answer to narrate-packet --answers; required, and a rewrite is accepted only where a judgement made for its exact text says it keeps the meaning
- `--location-scope` — bind a scopeprobe location-contributor document to an arm; repeat for multiple arms
- `--manifest` — Set manifest for this invocation; consult the linked parser for accepted values.
- `--max-pages` — Set max pages for this invocation; consult the linked parser for accepted values.
- `--mod-name` — the name the review reel speaks for a plugin's mod (PLUGIN=NAME), over its Nexus page's name and its MO2 mod folder
- `--mode` — Set mode for this invocation; consult the linked parser for accepted values.
- `--nexus-names` — the review reel's mod names from the Nexus catalogue: offline reads stored pages only, fetch also asks once for each page the catalogue lacks and records each fetch in evidence/mod-names.json
- `--nexus-reader-cmd` — the houseCARL server --nexus-names fetch asks through (default HOUSECARL_RECORD_READER_CMD, else this checkout's built server)
- `--no-print` — skip the browser; print.status is not-rendered
- `--order-setup` — bind the record lane's rows measured on the setup without the patch with its plugins in another order (ARM@PLAN=ROWS); changes lost there between a pair whose order differs are said as a load-order risk
- `--out` — Set out for this invocation; consult the linked parser for accepted values.
- `--patch-search` — the recorded Nexus patch search the review reel's patch sentences rest on; without it the reel plan is refused and the report still compiles
- `--policy` — Set policy for this invocation; consult the linked parser for accepted values.
- `--precedent` — the precedent stage's cards folder: the patch section counts each patch's own edits from its summary.json census and labels the cards' reasons as model-written
- `--presentation` — Set presentation for this invocation; consult the linked parser for accepted values.
- `--rows-per-group` — Set rows per group for this invocation; consult the linked parser for accepted values.
- `--sibling-bundle` — name a bundle of the same order without the patch, once per evaluation; its setups attribute the prediction when one matches the setup without the patch plugin for plugin and hash for hash (per check the adapter, probe build and rows match), or, for a further sibling, are that setup with plugins left out under the same population and scope (per check the adapter, probe build and recorded configuration match, and a sibling holding the setup itself joined that check); refused when no sibling holds the setup itself, or a sibling can stand in for nothing
- `--single-mod` — a clash one evaluated mod has with a third mod on its own: show under its own heading or set aside; never counted in the verdict (default: the reporting skill's singleModDefault)
- `--verify-copy` — no effect: plan never writes into a bundle it reads; kept so recorded commands still run

<a id="tool-evalrun"></a>

### evalrun

**How do I add derived evidence to an evaluation bundle in Python?**

Enrich a verified bundle; plan, run, bundle and verify an evaluation with runcontrol.

Inputs: A verified evidence bundle; for names, the houseCARL MCP server.

Outputs: enrichment/<kind>/ files and manifest rows, re-verified by runcontrol verify-bundle (its verify.json in the bundle).

Source: [_geometry-harness/evalrun.py](../_geometry-harness/evalrun.py) — `evalrun - what stays in Python of the evaluation controller`


<a id="tool-evalrun.enrich"></a>

### evalrun.enrich

**How do I add record rows or in-game names to an evaluation bundle without changing its results?**

Give a verified bundle the record checks' lost edits as rows, or the names of its places, objects and mods, derived after measurement from what the bundle and the plan already record.

Selector: `enrich`

Inputs: A verified evaluation bundle (a copy when it is retained evidence); for names, the houseCARL MCP server.

Outputs: enrichment/<kind>/ files cited by path and sha256, manifest rows of kind enrichment, re-verified.

Source: [_geometry-harness/evalrun.py](../_geometry-harness/evalrun.py) — `add_parser(`


Modifiers (do not select a separate mode):

- `--bundle` — enrich: the bundle to add derived evidence to (a copy, for retained evidence)
- `--kind` — enrich: record-rows (lost edits from the record imports' transcripts) or names (in-game and mod names)
- `--mod-name` — enrich names: a mod's declared ordinary name, PLUGIN=NAME (repeatable)
- `--reader-cmd` — enrich names: the houseCARL MCP server command line the records are read with (default: HOUSECARL_RECORD_READER_CMD, else this checkout's built server)
- `--reader-env` — enrich names: KEY=VALUE for the reader's environment; absent, a private instance with no setup plugin active is made
- `--sweep` — enrich order-geometry: an ordersweep out folder over the measured order and its reversal (repeatable; a probe is taken from the first folder that ran it)

<a id="tool-evalsettings"></a>

### evalsettings

**Which choices does the worldspace compatibility evaluation use on this machine, and how do I change them?**

Show, set or validate the evaluation's saved choices, or open the first-run window that sets them.

Inputs: The evaluation settings file, flags and the environment.

Outputs: The resolved settings, a saved settings file, or one sentence naming what is wrong.

Source: [integration/housecarl/evalsettings.py](../integration/housecarl/evalsettings.py) — `evalsettings — the first-run choices`


<a id="tool-evalsettings.defaults"></a>

### evalsettings.defaults

**How do I write an explicit defaults file?**

Prepare a scripted machine that must not show the window.

Selector: `defaults`

Inputs: The evaluation settings file, flags and the environment.

Outputs: The resolved settings, a saved settings file, or one sentence naming what is wrong.

Source: [integration/housecarl/evalsettings.py](../integration/housecarl/evalsettings.py) — `add_parser(`


<a id="tool-evalsettings.ensure"></a>

### evalsettings.ensure

**Has this machine chosen its evaluation settings, and if not, can the user choose now?**

Start an evaluation: open the first-run window once, or say the defaults apply.

Selector: `ensure`

Inputs: The evaluation settings file, flags and the environment.

Outputs: The resolved settings, a saved settings file, or one sentence naming what is wrong.

Source: [integration/housecarl/evalsettings.py](../integration/housecarl/evalsettings.py) — `add_parser(`


<a id="tool-evalsettings.path"></a>

### evalsettings.path

**Where is the evaluation settings file?**

Find the file every evaluation command reads.

Selector: `path`

Inputs: The evaluation settings file, flags and the environment.

Outputs: The resolved settings, a saved settings file, or one sentence naming what is wrong.

Source: [integration/housecarl/evalsettings.py](../integration/housecarl/evalsettings.py) — `add_parser(`


<a id="tool-evalsettings.route"></a>

### evalsettings.route

**Which agent runs the model an evaluation's models.* setting names?**

Before delegating a record/asset check, the flip plan, image review or narration rewrite: start the agent it prints, with no model parameter.

Selector: `route`

Inputs: One models.* key; the evaluation settings file and the environment.

Outputs: One JSON line naming the agent (or session, or off), or one sentence saying why the named model cannot run here.

Source: [integration/housecarl/evalsettings.py](../integration/housecarl/evalsettings.py) — `def cmd_route(`


<a id="tool-evalsettings.set"></a>

### evalsettings.set

**How do I change one evaluation setting without the window?**

Script a choice on an unattended machine.

Selector: `set`

Inputs: The evaluation settings file, flags and the environment.

Outputs: The resolved settings, a saved settings file, or one sentence naming what is wrong.

Source: [integration/housecarl/evalsettings.py](../integration/housecarl/evalsettings.py) — `add_parser(`


<a id="tool-evalsettings.show"></a>

### evalsettings.show

**Which value does each evaluation option take, and from where?**

Check what an evaluation will use before planning it.

Selector: `show`

Inputs: The evaluation settings file, flags and the environment.

Outputs: The resolved settings, a saved settings file, or one sentence naming what is wrong.

Source: [integration/housecarl/evalsettings.py](../integration/housecarl/evalsettings.py) — `add_parser(`


<a id="tool-evalsettings.validate"></a>

### evalsettings.validate

**Is this evaluation settings file valid?**

Check a hand-edited or copied settings file.

Selector: `validate`

Inputs: The evaluation settings file, flags and the environment.

Outputs: The resolved settings, a saved settings file, or one sentence naming what is wrong.

Source: [integration/housecarl/evalsettings.py](../integration/housecarl/evalsettings.py) — `add_parser(`


<a id="tool-evalsettings.window"></a>

### evalsettings.window

**How do I reopen the evaluation settings window?**

Change saved evaluation choices through the page.

Selector: `window`

Inputs: The evaluation settings file, flags and the environment.

Outputs: The resolved settings, a saved settings file, or one sentence naming what is wrong.

Source: [integration/housecarl/evalsettings.py](../integration/housecarl/evalsettings.py) — `add_parser(`


Modifiers (do not select a separate mode):

- `--json` — print the options as JSON with each value's source
- `--no-window` — never open the window; a missing file means the built-in defaults
- `--opener` — run `python SCRIPT URL` instead of the browser (tests)
- `--out` — where `defaults` writes the explicit defaults file
- `--page-only` — write the page without a server; it shows the set command to paste
- `--session-model` — route: this session's full model ID; required for models.checkExplanations, which never runs on a Fable model (a Fable session is routed to the Opus agent)
- `--settings` — the settings file to use (first in the resolution order)
- `--timeout-seconds` — stop the window's server after this long with nothing saved (an operational timeout)

<a id="tool-evalspeech"></a>

### evalspeech

**How do I prepare local narration audio for the diagnostic reel?**

Add neural speech to a reviewed storyboard using explicitly selected, already available local model assets.

Inputs: Narration text, local voice/model configuration and destination.

Outputs: Prepared audio files and narration metadata.

Source: [integration/housecarl/evalspeech.py](../integration/housecarl/evalspeech.py) — `Prepare local narration WAVs for evalreel's existing narrationAudio interface.`


Modifiers (do not select a separate mode):

- `--device` — cpu (default), cuda or cuda:N; no automatic fallback
- `--engine` — default: kokoro; no automatic fallback
- `--model-dir` — already downloaded local model with config and weights
- `--model-revision` — immutable repository commit hash for those local weights
- `--out` — new output directory; existing directories are refused
- `--seed` — seed reset per scene (default 42)
- `--speed` — Kokoro speed multiplier only; default 1
- `--storyboard` — reviewed evalreel scene packet; narration preserved verbatim
- `--style` — VoxCPM2 voice description, or optional Qwen 1.7B delivery instruction
- `--text-file` — UTF-8 sample script; synthesized verbatim
- `--verify` — verify sources, local model bytes, scene bindings and WAVs without loading a model
- `--voice` — Kokoro voice (default: bm_george), or required Qwen CustomVoice speaker

<a id="tool-flipplan"></a>

### flipplan

**Which switch flips, in which order and where, should the live switch check launch for an evaluation?**

Draft a flip plan for an opted-in runtime.state-switch-flip check instead of writing it by hand.

Inputs: Each arm's switch census (switchprobe --json) and MO2 profile, the scope, and the bound scripts' decompiled source.

Outputs: A flip plan switchflip.py validates, and a groups document naming what was planned, dropped, flagged, unmapped or not planned.

Source: [integration/housecarl/flipplan.py](../integration/housecarl/flipplan.py) — `flipplan — draft the flip plan of the runtime state-switch check`


Modifiers (do not select a separate mode):

- `--arm` — an arm: its id, MO2 profile and census (repeatable)
- `--canary-command` — a known-crash console command, sent after each arm's first condition
- `--evalrun-plan` — bind the plan to this evalrun plan's arms
- `--groups` — the groups document (JSON)
- `--neutral-cell` — the not-loaded arrival cell; without it that condition is omitted and named
- `--out` — the flip plan (JSON)
- `--scope` — the evaluation scope (targets and their placed cells)
- `--scripts` — a folder of switchprobe --show-script output, one <Script>.psc each

<a id="tool-floorgapprobe"></a>

### floorgapprobe

**How far is the winning navmesh above or below the physical floor?**

**How far is an object from the nearest winning navmesh triangle (reach <Cell> <subjects.json> --json OUT)?**

**How far is the nearest actor-solid surface from a standing body's column at a point (clear <Cell> <file>)?**

Investigate actors walking above a floor or beneath its surface by measuring navmesh-to-floor separation.

Inputs: Winning navmesh and physical floor surfaces for selected cells.

Outputs: Measured floor offsets and incomplete-floor evidence.; reach mode: per subject, the exact 3D distance from its placed geometry to the nearest non-deleted winning navmesh triangle, its horizontal and vertical parts, and the floor under it (an exterior cell's own navmesh only: an upper bound); opt-in --band LO,HI: the distance to the navmesh swept up from +LO to +HI (a standing body's reachable band, 0 when met); opt-in --under: the floor straight under the subject; a model-less subject with an XPRM box is measured from its box; a subject filed in its worldspace's persistent cell is found in the grid cell its position falls in, its row labelled placement.; clear mode: per point (x y floorZ), the horizontal distance from the point to the nearest surface of an actor-solid reference inside the column floorZ + LO .. floorZ + HI (default the humanoid band), the reference and its model named, geometry 'fallback' where a model's collision is incomplete; opt-in --within R: blocked or clear at R; opt-in --json OUT.

Source: [_geometry-harness/floorgapprobe/Program.cs](../_geometry-harness/floorgapprobe/Program.cs) — `// floorgapprobe — the navmesh-to-floor offset δ`


Modifiers (do not select a separate mode):

- `--at` — line: the point X,Y[,Z] the line passes through; without Z the lowest navmesh under it.
- `--band` — reach: LO,HI adds the distance to the navmesh swept up from +LO to +HI (a standing body's band); clear: the column above the floor (default the humanoid band).
- `--depth` — How far below the navmesh a floor may lie before it reads as a drop (NavmeshFloor default).
- `--every` — line: spacing (u) of the readings along the line (default 16).
- `--flags` — Floor candidates by the record's initially-disabled flag alone instead of the load-time enable chain.
- `--game` — Game directory (else the harness config, only for its own instance).
- `--heading` — line: yaw in degrees (0 = +Y, clockwise) of the line read through --at.
- `--instance` — MO2 instance directory (else HOUSECARL_MO2_INSTANCE; never the harness config: refused instance-not-explicit).
- `--json` — reach: write the per-subject navmesh distances to this path (required by reach); clear: write the per-point clearances to this path.
- `--moved-ref-selftest` — Set moved ref selftest for this invocation; consult the linked parser for accepted values.
- `--order` — With --winning: walk this candidate configuration (CandidateFile) instead of the profile's order.
- `--own` — An exterior cell's floor from its own references and LAND only, without the 3x3 neighbourhood.
- `--plugin` — The one plugin read without --winning (default Skyrim.esm).
- `--profile` — MO2 profile name (else the harness config, only for its own instance).
- `--reach` — How far above the navmesh a floor may lie, the step height (NavmeshFloor default).
- `--span` — line: how far (u) the line runs each way from --at (default 384).
- `--split` — Triangles longer than this (u) are read at four interior points instead of the centroid.
- `--state` — Refused: floorgapprobe reads one source and carries no switch census to flip.
- `--tris` — sweep and cell: write the per-triangle offsets to this path.
- `--tsv` — sweep: write the per-cell table to this path.
- `--under` — reach: adds the floor straight under each subject's position.
- `--verdicts` — sweep: read navprobe's route-cut verdict TSVs from this --dump directory and report the offset under each object.
- `--winning` — cell, line, points, reach and clear: the cell as the load order resolves it, every plugin walked in order.
- `--within` — clear: a point is blocked when an actor-solid surface stands nearer than this (u) to its column; also caps the search.

<a id="tool-floorprobe"></a>

### floorprobe

**Which dangling-reference findings are already present in the base-game floor?**

Separate inherited vanilla dangling links from findings introduced by a candidate configuration.

Inputs: Base-game calibration records and dangling-reference findings.

Outputs: Base-game floor counts for interpreting findings.

Source: [_geometry-harness/floorprobe/Program.cs](../_geometry-harness/floorprobe/Program.cs) — `// FLOOR MEASUREMENT for the dangling-reference split`


<a id="tool-fomodprobe"></a>

### fomodprobe

**Which installer patch options were offered but not selected?**

Audit an installed mod whose optional compatibility selections may have been skipped during setup.

Inputs: Installed mod metadata and installer option definitions.

Outputs: Offered patch options and recorded selection differences.

Source: [_geometry-harness/fomodprobe/Program.cs](../_geometry-harness/fomodprobe/Program.cs) — `// FOMOD OPTIONS NEVER SELECTED — what the installer offered you and you did not take.`


Modifiers (do not select a separate mode):

- `--jobs` — Set jobs for this invocation; consult the linked parser for accepted values.
- `--json` — PATH: write the hit list (schema 1, _shared/DiscoveryHits.cs): one hit per plugin an unselected option delivers, with the sha256 of that archive entry, its conditions and tier, and the added extraction time.
- `--list` — Set list for this invocation; consult the linked parser for accepted values.
- `--mod` — Set mod for this invocation; consult the linked parser for accepted values.

<a id="tool-geoprobe"></a>

### geoprobe

**Do the winning mesh bounds disagree with the plugin's stored bounds?**

Investigate culling or spatial-query anomalies where stored object bounds may not enclose the winning mesh.

Inputs: Winning mesh files and plugin-stored object bounds.

Outputs: Stored-versus-measured bound discrepancies.

Source: [_geometry-harness/geoprobe/Program.cs](../_geometry-harness/geoprobe/Program.cs) — `// Milestone 2+3 proof: resolve every Model.File through the REAL MO2 VFS, measure the winning`


Modifiers (do not select a separate mode):

- `--dump` — Set dump for this invocation; consult the linked parser for accepted values.

<a id="tool-imgcheck"></a>

### imgcheck

**How can I crop, compare or inspect render pixels and metadata?**

Inspect a suspicious image region or compare captures while retaining access to their embedded replay metadata.

Inputs: PNG images, crop/compare options and metadata requests.

Outputs: Reading copies, pixel differences and embedded metadata.

Source: [_geometry-harness/viewprobe/tools/imgcheck.py](../_geometry-harness/viewprobe/tools/imgcheck.py) — `imgcheck — the verification half of viewprobe's render discipline.`


<a id="tool-integration-housecarl-tests-fixtures-evalsettings-stub_opener.py"></a>

### integration/housecarl/tests/fixtures/evalsettings/stub_opener.py

**How can a test stand in for the browser on the settings window?**

Drive the settings window's Save, close and refusal paths without a browser.

Inputs: Controlled fixtures for stub_opener.

Outputs: Assertion results or generated fixture data for: How can a test stand in for the browser on the settings window?

Source: [integration/housecarl/tests/fixtures/evalsettings/stub_opener.py](../integration/housecarl/tests/fixtures/evalsettings/stub_opener.py) — `The browser stand-in for test_evalsettings.py: 'evalsettings.py ensure|window --opener THIS' runs 'python THIS URL'.`


<a id="tool-integration-housecarl-tests-fixtures-standin_hold.py"></a>

### integration/housecarl/tests/fixtures/standin_hold.py

**How can a test stand in for the machine's slot command with a real lock holder a hard kill leaves behind?**

Exercise evalpipeline's heavy-child stop and lock release without the machine's idle_lock.

Inputs: A lock path and a job command after --; the slot command's other options are accepted and ignored.

Outputs: The lock file while the job runs and idle_lock.log take and release lines beside it.

Source: [integration/housecarl/tests/fixtures/standin_hold.py](../integration/housecarl/tests/fixtures/standin_hold.py) — `A stand-in for the machine's slot command in tests`


<a id="tool-integration-housecarl-tests-test_alpha_card_capture.py"></a>

### integration/housecarl/tests/test_alpha_card_capture.py

**When a plant made of alpha cards misses its mark at the composed camera, is the witness station's own camera tried before the top-down and kept only when the plant's point lands in its mark, is such a picture captioned as a station view, is a clash a station saw never said to be one no camera could see, does a kept image key never stand for a capture with other frames, are plugin names in capture requests compared without case, is a one-mark marked view named, does the report stage's media bundle use the frozen check registry, does the reel narration speak no form key or editor ID, do capture frames address the cell by its form key, and does every capture refusal code have its own reason?**

Qualify changes to capture_acquire's routing after a refused composed pose (the alpha-card witness step), the station-view caption, the reel's reasons for refused clashes, the capture reuse rule for older pose refusals, the image-observations key, report_scenes' plugin-name comparisons, the marked-view and station-view captions, the report stage's media-bundle frozen inputs, a vignette's own capture request, the still pages' spoken wording, or the capture cell and its refusal reasons (C1890-C1899, C1930-C1933, C1935-C1939, C1960-C1961); offline, no game.

Inputs: capture_acquire with its viewprobe call answered by a stand-in and its top-down recorded, test_reel_vignettes' synthetic evaluation, a hand-built capture folder.

Outputs: One GREEN, RED or CRASH line per case (C1890-C1899, C1930-C1933, C1935-C1939, C1960-C1961).

Source: [integration/housecarl/tests/test_alpha_card_capture.py](../integration/housecarl/tests/test_alpha_card_capture.py) — `Known answers for the interim alpha-card capture rule and the stale image key`


<a id="tool-integration-housecarl-tests-test_capture_acquire.py"></a>

### integration/housecarl/tests/test_capture_acquire.py

**Does the real reporting request-to-camera path satisfy its offline contract?**

Qualify changes to reporting capture acquisition with offline binding and planner-refusal controls.

Inputs: Controlled fixtures for test_capture_acquire.

Outputs: Assertion results or generated fixture data for: Does the real reporting request-to-camera path satisfy its offline contract?

Source: [integration/housecarl/tests/test_capture_acquire.py](../integration/housecarl/tests/test_capture_acquire.py) — `Offline contract for 'capture_acquire.py': the request-to-camera path, end to end.`


<a id="tool-integration-housecarl-tests-test_capture_every_state.py"></a>

### integration/housecarl/tests/test_capture_every_state.py

**Does a vignette's capture camera pass the measured state's camera checks (inside test, the sightlines of the participants that setup places) in every other state it renders, try the next pose of the chain when one fails, refuse naming the state and the object when none passes, and keep its single camera when every state is clear?**

Qualify changes to capture_acquire's state_checks, --state-order, the pose, witness and top-down fallback chain, the cell each other state is rendered by, evalpipeline's acquire arguments or the state-camera-blocked reason (C2168-C2172); offline, no game.

Inputs: capture_acquire with its renderer answered per setup in viewprobe's own inside-at and pick line formats, judged by the real shotplan; hand-built order files.

Outputs: One GREEN, RED or CRASH line per check (C2168-C2172).

Source: [integration/housecarl/tests/test_capture_every_state.py](../integration/housecarl/tests/test_capture_every_state.py) — `Known answers for a capture camera checked in every state it renders`


<a id="tool-integration-housecarl-tests-test_capture_fallbacks.py"></a>

### integration/housecarl/tests/test_capture_fallbacks.py

**Do the capture fallbacks (cutaway, top-down, see-through marks) give their known answers on a real render, and do the reuse, reel and caption rules hold?**

**Does a buried clash read as buried in the reel and the report, and does a top-down hide what covers the pair from above by the cutaway's rule?**

Qualify changes to capture_points, capture_acquire, shotplan --top-down-only, viewprobe --hide/--protect/--contacts-of/--occluders-of/--coverage-of, the capture reuse rule or the buried-clash words; render cases need HOUSECARL_FIXTURE_GAME (else NOT RUN, exit 3); --code-only runs the code cases alone and exits 0 when they pass.

Inputs: CfFixture.esp from viewprobe --write-capture-fixture, a Skyrim SE game folder's archives.

Outputs: Assertion results per case; NOT RUN without game data; NOT ATTEMPTED with --code-only.

Source: [integration/housecarl/tests/test_capture_fallbacks.py](../integration/housecarl/tests/test_capture_fallbacks.py) — `Known answers for the capture fallbacks: cutaway, top-down and see-through marks`


<a id="tool-integration-housecarl-tests-test_capture_follow_ups.py"></a>

### integration/housecarl/tests/test_capture_follow_ups.py

**Does the capture ring stand on the navmesh storey rather than a floor high up a tall focus, does a flat partner box still find the floor, does the reel say how many camera spots a buried clash rests on, is a see-through partner never said to bury the other object, and does the reel picture a group's strongest crossing?**

Qualify changes to capture_points' ground cast or focus storey, the eye-level step's see-through field, the buried wording in the reel and report, or the reel's choice of example within a group and a buried clash in a pictured group (C1830-C1836); offline, no game.

Inputs: Toy box scenes through viewprobe/tools/fake_viewprobe.py (scene key contacts), test_reel_vignettes' synthetic evaluation, test_capture_fallbacks' refused capture result.

Outputs: One GREEN, RED or CRASH line per case (C1830-C1836).

Source: [integration/housecarl/tests/test_capture_follow_ups.py](../integration/housecarl/tests/test_capture_follow_ups.py) — `Known answers for the capture follow-ups after item 28`


<a id="tool-integration-housecarl-tests-test_capture_navmesh.py"></a>

### integration/housecarl/tests/test_capture_navmesh.py

**Does a navmesh comparison request get its measured top-down camera, both states and its refusals offline?**

Qualify changes to capture_navmesh.py, capture_acquire's navmesh path or viewprobe --nav-link's row shape.

Inputs: Controlled fixtures for test_capture_navmesh.

Outputs: Assertion results for: Does a navmesh comparison request get its measured top-down camera?

Source: [integration/housecarl/tests/test_capture_navmesh.py](../integration/housecarl/tests/test_capture_navmesh.py) — `Offline contract for a navmesh comparison's camera`


<a id="tool-integration-housecarl-tests-test_capture_points.py"></a>

### integration/housecarl/tests/test_capture_points.py

**Does the capture point picker return visible faces, the right floor and plain refusals on the shapes real requests were refused on?**

Qualify changes to capture_points, capture_acquire --points auto or shotplan --bearing-window; --sabotage removes the owner test and must go red.

Inputs: Controlled fixtures for test_capture_points.

Outputs: Assertion results for: Does the capture point picker return visible faces, the right floor and plain refusals?

Source: [integration/housecarl/tests/test_capture_points.py](../integration/housecarl/tests/test_capture_points.py) — `Offline known answers for 'capture_points.py': a measured point on a face an eye-level camera can see (C700-C716).`


<a id="tool-integration-housecarl-tests-test_capture_results_unreadable.py"></a>

### integration/housecarl/tests/test_capture_results_unreadable.py

**Does evalreport's reel refuse a capture-results file it cannot read, and still plan without one?**

Qualify a change to how evalreport plan reads --capture-results.

Inputs: Synthetic capture-results files.

Outputs: Assertion results for: Does evalreport's reel refuse a capture-results file it cannot read, and still plan without one?

Source: [integration/housecarl/tests/test_capture_results_unreadable.py](../integration/housecarl/tests/test_capture_results_unreadable.py) — `evalreport's reel refuses a --capture-results file it cannot read`


<a id="tool-integration-housecarl-tests-test_evalpipeline.py"></a>

### integration/housecarl/tests/test_evalpipeline.py

**Does the driver of the stages after the bundle reuse a stage only when nothing changed, continue a run folder the built-in controller carried through the bundle, and refuse one it did not?**

Check changes to evalpipeline.py, the driver of the stages after the bundle.

Inputs: Controlled fixtures for test_evalpipeline.

Outputs: Assertion results for: Does the driver of the stages after the bundle reuse a stage only when nothing changed, continue a run folder the built-in controller carried through the bundle, and refuse one it did not?

Source: [integration/housecarl/tests/test_evalpipeline.py](../integration/housecarl/tests/test_evalpipeline.py) — `Known answers for evalpipeline.py, the driver of the stages after the bundle`


<a id="tool-integration-housecarl-tests-test_evalreel.py"></a>

### integration/housecarl/tests/test_evalreel.py

**Does the reel pipeline enforce media, audio, cache and clip-cut contracts?**

Check reel assembly changes for stale media reuse, invalid audio, or incorrect segment boundaries.

Inputs: Controlled fixtures for test_evalreel.

Outputs: Assertion results or generated fixture data for: Does the reel pipeline enforce media, audio, cache and clip-cut contracts?

Source: [integration/housecarl/tests/test_evalreel.py](../integration/housecarl/tests/test_evalreel.py) — `End-to-end contract for narrated evidence reels: media identity, audio decode, cache, clip cuts and refusal.`


<a id="tool-integration-housecarl-tests-test_evalreport.py"></a>

### integration/housecarl/tests/test_evalreport.py

**Does the report compiler match the constructed bundle oracle?**

Check report generation against independent expected findings before publishing schema changes.

Inputs: Controlled fixtures for test_evalreport.

Outputs: Assertion results or generated fixture data for: Does the report compiler match the constructed bundle oracle?

Source: [integration/housecarl/tests/test_evalreport.py](../integration/housecarl/tests/test_evalreport.py) — `The report compiler against the oracle (Q45).`


<a id="tool-integration-housecarl-tests-test_evalreport_comparison_kinds.py"></a>

### integration/housecarl/tests/test_evalreport_comparison_kinds.py

**Does the report distinguish removing a mod from repairing a conflict?**

Prevent a mod-removal comparison from being presented as proof of a compatibility repair.

Inputs: Controlled fixtures for test_evalreport_comparison_kinds.

Outputs: Assertion results or generated fixture data for: Does the report distinguish removing a mod from repairing a conflict?

Source: [integration/housecarl/tests/test_evalreport_comparison_kinds.py](../integration/housecarl/tests/test_evalreport_comparison_kinds.py) — `Checks for how the report compiler classifies a setup and what it is then allowed to claim.`


Modifiers (do not select a separate mode):

- `--prove-gate` — force repair_claimable open in memory and require this suite to FAIL

<a id="tool-integration-housecarl-tests-test_evalreport_prediction.py"></a>

### integration/housecarl/tests/test_evalreport_prediction.py

**Does the report lead with what adding the mods will break, then coverage, then the patch?**

Check that a report states the prediction before any patch tally and never counts a base-game difference as a clash.

Inputs: Controlled fixtures for test_evalreport_prediction.

Outputs: Assertion results or generated fixture data for: Does the report lead with what adding the mods will break, then coverage, then the patch?

Source: [integration/housecarl/tests/test_evalreport_prediction.py](../integration/housecarl/tests/test_evalreport_prediction.py) — `Checks that an evaluation report leads with the prediction: what adding the mods will break.`


<a id="tool-integration-housecarl-tests-test_evalreport_siblings.py"></a>

### integration/housecarl/tests/test_evalreport_siblings.py

**Does a report join several separate evaluations of the same order, each lending the setups it can stand in for, and refuse one it cannot use by name?**

Check evalreport plan with --sibling-bundle given more than once: which sibling fills each role, the order given changes nothing, and each sub-order join criterion refuses what it should.

Inputs: Controlled fixtures for test_evalreport_siblings.

Outputs: Assertion results or generated fixture data for: Does a report join several separate evaluations of the same order, each lending the setups it can stand in for, and refuse one it cannot use by name?

Source: [integration/housecarl/tests/test_evalreport_siblings.py](../integration/housecarl/tests/test_evalreport_siblings.py) — `Checks that a report joins more than one separate evaluation of the same order`


<a id="tool-integration-housecarl-tests-test_evalreport_verify_in_place.py"></a>

### integration/housecarl/tests/test_evalreport_verify_in_place.py

**Does evalreport plan verify --bundle and --sibling-bundle in place without writing a byte or mtime into either, without a copy in proportion to the bundle even when its evidence is hard-linked, leave no tree after a failure mid-verify, refuse a verify that wrote no result, does runcontrol verify-bundle --out write its result only to the named file, and does every caller check a bundle with runcontrol rather than Python?**

Qualify changes to evalreport's run_verify_bundle, plan's --verify-copy, runcontrol verify-bundle's --out, or which program a caller starts to check a bundle (C2230-C2235); offline, no game.

Inputs: make_report_fixtures' R1 bundle, copied, with a folder of hard links to one 16 MiB blob outside it.

Outputs: One ok or RED line per check (C2230-C2234).

Source: [integration/housecarl/tests/test_evalreport_verify_in_place.py](../integration/housecarl/tests/test_evalreport_verify_in_place.py) — `Known answers for evalreport plan verifying a bundle without copying it`


<a id="tool-integration-housecarl-tests-test_evalsettings.py"></a>

### integration/housecarl/tests/test_evalsettings.py

**Does a fresh user get the settings window once, and is a saved choice honoured?**

Qualify the first-run settings, the window's server and the plan's frozen copy.

Inputs: Controlled fixtures for test_evalsettings.

Outputs: Assertion results or generated fixture data for: Does a fresh user get the settings window once, and is a saved choice honoured?

Source: [integration/housecarl/tests/test_evalsettings.py](../integration/housecarl/tests/test_evalsettings.py) — `Known answers for the evaluation's first-run settings and window (evalsettings.py), PROPOSAL section 7.`


<a id="tool-integration-housecarl-tests-test_evalspeech.py"></a>

### integration/housecarl/tests/test_evalspeech.py

**Does narration preparation enforce its contracts without model weights or network access?**

Exercise narration binding and refusal paths offline before testing a chosen speech engine.

Inputs: Controlled fixtures for test_evalspeech.

Outputs: Assertion results or generated fixture data for: Does narration preparation enforce its contracts without model weights or network access?

Source: [integration/housecarl/tests/test_evalspeech.py](../integration/housecarl/tests/test_evalspeech.py) — `Narration preparation contracts, without model imports, weights or network access.`


<a id="tool-integration-housecarl-tests-test_exterior_stations.py"></a>

### integration/housecarl/tests/test_exterior_stations.py

**Does an exterior camera station stand on navmesh at its own level, eye at that level plus the eye height, while interiors keep one floor?**

Qualify changes to capture_points' station placement or shotplan's exterior eye; offline, no game.

Inputs: Toy box scenes for fake_viewprobe.py; the real picker, capture_acquire and shotplan.

Outputs: One ok/FAIL line per check; exit 1 on any failure.

Source: [integration/housecarl/tests/test_exterior_stations.py](../integration/housecarl/tests/test_exterior_stations.py) — `Offline known answers for exterior camera stations`


<a id="tool-integration-housecarl-tests-test_eye_inside_solid.py"></a>

### integration/housecarl/tests/test_eye_inside_solid.py

**Does the planner refuse a camera eye inside a solid of any thickness on a real render, and accept one beside a thin wall?**

Qualify changes to viewprobe --inside-at (its rays or facing) or shotplan's inside test; needs HOUSECARL_FIXTURE_GAME (else NOT RUN, exit 3).

Inputs: CfFixture.esp from viewprobe --write-capture-fixture (cells CfThick, CfThickWalls), a Skyrim SE game folder's archives.

Outputs: Assertion results per case; NOT RUN without game data.

Source: [integration/housecarl/tests/test_eye_inside_solid.py](../integration/housecarl/tests/test_eye_inside_solid.py) — `Known answers for the planner's inside test on a real render`


<a id="tool-integration-housecarl-tests-test_flipplan.py"></a>

### integration/housecarl/tests/test_flipplan.py

**Does the flip-plan generator keep branches apart, keep source order and name every call it does not plan?**

Qualify a change to the flip-plan generator.

Inputs: Controlled fixtures for test_flipplan.

Outputs: Assertion results or generated fixture data for: Does the flip-plan generator keep branches apart, keep source order and name every call it does not plan?

Source: [integration/housecarl/tests/test_flipplan.py](../integration/housecarl/tests/test_flipplan.py) — `Known answers for the flip-plan generator (flipplan.py).`


<a id="tool-integration-housecarl-tests-test_machineidle.py"></a>

### integration/housecarl/tests/test_machineidle.py

**Does the idle gate refuse probes by command line and blockers by name?**

Qualify a change to the launch idle gate before it decides whether the game may start.

Inputs: Controlled fixtures for test_machineidle.

Outputs: Assertion results or generated fixture data for: Does the idle gate refuse probes by command line and blockers by name?

Source: [integration/housecarl/tests/test_machineidle.py](../integration/housecarl/tests/test_machineidle.py) — `Known answers for the idle gate (machineidle.py).`


<a id="tool-integration-housecarl-tests-test_narration_fidelity.py"></a>

### integration/housecarl/tests/test_narration_fidelity.py

**Does the narration packet ask for plain words and carry the glossary, does narrate-apply accept a rewrite only where a fidelity judgement made for its exact text says it keeps the meaning, and does the judge's model setting never resolve to a Fable model?**

Qualify changes to evalreport narrate-packet (instructions, glossary, the judge's packet), narrate-apply's judgement gate, the reporting skill's narration.json, or evalsettings models.checkExplanations and route --session-model, the judge packet's bound rows and the narrator's completion rule (C2020-C2029, C2050-C2053); offline, no game, no model call.

Inputs: A hand-built presentation with made-up plugins, hand-written stand-in judgements, the shipped skill data.

Outputs: One GREEN, RED or CRASH line per case (C2020-C2029, C2050-C2053).

Source: [integration/housecarl/tests/test_narration_fidelity.py](../integration/housecarl/tests/test_narration_fidelity.py) — `Known answers: the narration packet asks for plain words`


<a id="tool-integration-housecarl-tests-test_navmesh_caption.py"></a>

### integration/housecarl/tests/test_navmesh_caption.py

**Does a navmesh vignette's earlier-version caption, on the reel strip and in the report, name the plugin the historical frame's own --nav-version draws (never the plan's re-cut history, and none when the frame names none), say which mod replaced that version, and keep a name ending in s free of an "s's" possessive, in the narration too?**

Qualify changes to reel_plan's frame_label for navmesh frames, navmesh_earlier_plugin (shared with evalreport's figure captions), navmesh_sentence, or the reporting skill's navmesh caption, label or narration words (C2240-C2244); offline, no game.

Inputs: test_navmesh_reel's planner fixture with its earlier mod renamed, hand-built renderer commands, and the shipped skill data.

Outputs: One GREEN, RED or CRASH line per check (C2240-C2244).

Source: [integration/housecarl/tests/test_navmesh_caption.py](../integration/housecarl/tests/test_navmesh_caption.py) — `Known answers for the navmesh context caption`


<a id="tool-integration-housecarl-tests-test_navmesh_reel.py"></a>

### integration/housecarl/tests/test_navmesh_reel.py

**Is a navmesh clash item 24's camera can draw pictured in the review reel as a vignette of its two navmesh states, bound to its capture request, captured by the pipeline with its control in the setup without the mod, refused without a clean control at the same camera, held for its narration, shown as one report figure with its own captions, and said with the control's result and no form key?**

Qualify changes to reel_plan's navmesh vignette (camera shape, states, words, labels), report_scenes' reel-mode navmesh binding, evalpipeline.capture_request's navmesh branch, capture_acquire capture's --control gate, evalreel's vignette holds or evalreport's navmesh figure (C1970-C1978); offline, no game.

Inputs: A hand-built Clockwork-shaped presentation, the shipped skill data, test_capture_navmesh's synthetic scene with fake_viewprobe.py, evalpipeline.capture_request with a recording child runner.

Outputs: One GREEN, RED or CRASH line per case (C1970-C1978).

Source: [integration/housecarl/tests/test_navmesh_reel.py](../integration/housecarl/tests/test_navmesh_reel.py) — `Known answers for a navmesh clash pictured in the review reel`


<a id="tool-integration-housecarl-tests-test_navown_edge_identity.py"></a>

### integration/housecarl/tests/test_navown_edge_identity.py

**Does navownprobe write one EDGE-IDX row per broken edge link, and does every identity-keyed reader (conflictdiff, ordersweep, evalreport) keep and credit each link?**

Qualify a change to navownprobe's dangling-link rows (DanglingLinks.cs), to ConflictIo.RowIdentity or to a reader that keys rows by it.

Inputs: The Release navownprobe, conflictdiff and stateprobe, ordersweep.py and evalreport.py; EdgeLinkFixture laid out as an MO2 instance.

Outputs: Assertion results for: Does navownprobe write one EDGE-IDX row per broken edge link, and does every identity-keyed reader (conflictdiff, ordersweep, evalreport) keep and credit each link?

Source: [integration/housecarl/tests/test_navown_edge_identity.py](../integration/housecarl/tests/test_navown_edge_identity.py) — `navownprobe writes one EDGE-IDX row per broken edge link, and every identity-keyed reader keeps each one (1390-1405).`


<a id="tool-integration-housecarl-tests-test_order_geometry.py"></a>

### integration/housecarl/tests/test_order_geometry.py

**Does a placed-object geometry row that holds in one order of a free pair and not the other reach the report as a load-order risk, with rows in both orders left out, unmeasured rows counted, drifted plugins named and mismatched orders refused?**

Qualify a change to the order-geometry enrichment (bundleenrich.order_geometry_rows, enrich_order_geometry) or to evalreport's order-geometry lines and load-order geometry fact.

Inputs: Hand-built bundles carrying an order-geometry enrichment, and three hand-written ordersweep emits.

Outputs: Assertion results for: Does a placed-object geometry row that holds in one order of a free pair and not the other reach the report as a load-order risk?

Source: [integration/housecarl/tests/test_order_geometry.py](../integration/housecarl/tests/test_order_geometry.py) — `Known answers: a geometry row that holds in one order of a free pair and not in the other is said as a load-order risk`


<a id="tool-integration-housecarl-tests-test_overlap_triage.py"></a>

### integration/housecarl/tests/test_overlap_triage.py

**Does overlap triage preserve locality, counterpart identity and incomplete reviews?**

Check incident grouping changes for lost members, incorrect counterparts, and false review completeness.

Inputs: Controlled fixtures for test_overlap_triage.

Outputs: Assertion results or generated fixture data for: Does overlap triage preserve locality, counterpart identity and incomplete reviews?

Source: [integration/housecarl/tests/test_overlap_triage.py](../integration/housecarl/tests/test_overlap_triage.py) — `# 1||2 groups with 1||3; 3||4 cannot join through a transitive 1||3 link because it does not`


<a id="tool-integration-housecarl-tests-test_patch_clash_pictures.py"></a>

### integration/housecarl/tests/test_patch_clash_pictures.py

**Is a clash that appears only with the supplied patches loaded pictured with them loaded, beside the setup without them, and said as such in the reel and the report; is one world-state check's new clashes counted once; and does a terrain station's label reach the picture's caption and reel note?**

Qualify changes to reel_plan's patch vignettes or view_kind_line, evalreport's problems_text, SAY_SKIP_KEYS, patch_captions or view_kind_caption, the report templates' new-clashes figures, capture_acquire's standing or keep_refused_points, or the reporting skill's words for them (C2340-C2349); offline, no game.

Inputs: test_reel_vignettes' synthetic evaluation, test_evalreport_prediction's synthetic bundles, hand-built capture planner results, and the shipped skill data.

Outputs: One GREEN, RED or CRASH line per check (C2340-C2349).

Source: [integration/housecarl/tests/test_patch_clash_pictures.py](../integration/housecarl/tests/test_patch_clash_pictures.py) — `Known answers for pictures of clashes new with the supplied patches`


<a id="tool-integration-housecarl-tests-test_reader_rules.py"></a>

### integration/housecarl/tests/test_reader_rules.py

**Do the report and reel give a verdict and next step per patch, say unfinished testing first, name one patch per outcome, show every produced picture, and plan the reel's counts, labels, pages and closing in the report's terms?**

Qualify changes to the report's patch verdict, incompleteness, attribution, figure, missing-asset and setup wording, the reel's stills and labels, or the record-rows enrichment's masters.

Inputs: Controlled fixtures for test_reader_rules, and one recorded presentation where the machine holds it.

Outputs: Assertion results for: Do the report and reel give a verdict and next step per patch, say unfinished testing first, name one patch per outcome, show every produced picture, and plan the reel's counts, labels, pages and closing in the report's terms?

Source: [integration/housecarl/tests/test_reader_rules.py](../integration/housecarl/tests/test_reader_rules.py) — `Known answers for the report and reel rules a mod user acts on`


<a id="tool-integration-housecarl-tests-test_readerdoc.py"></a>

### integration/housecarl/tests/test_readerdoc.py

**Does the reader's document list only clashes between the two mods and give the right install advice?**

Check reader's-document changes against hand-built plans and one plan from the real compiler.

Inputs: Controlled fixtures for test_readerdoc.

Outputs: Assertion results for: Does the reader's document list only clashes between the two mods and give the right install advice?

Source: [integration/housecarl/tests/test_readerdoc.py](../integration/housecarl/tests/test_readerdoc.py) — `Tests for readerdoc.py — the reader's document rendered from a presentation plan.`


<a id="tool-integration-housecarl-tests-test_recordrows.py"></a>

### integration/housecarl/tests/test_recordrows.py

**Does the record-lane comparator emit a row only for a lost change, within the schema's field scope?**

Check recordrows changes against hand-written transcripts.

Inputs: Controlled fixtures for test_recordrows.

Outputs: Assertion results for: Does the record-lane comparator emit a row only for a lost change, within the schema's field scope?

Source: [integration/housecarl/tests/test_recordrows.py](../integration/housecarl/tests/test_recordrows.py) — `Tests for recordrows.py — record-lane rows derived from a record import's own MCP transcripts.`


<a id="tool-integration-housecarl-tests-test_reel_names.py"></a>

### integration/housecarl/tests/test_reel_names.py

**Does the reel say and show a declared mod name or plugin file name as written (the round 2 subjects' patches, one with an all-capital word), still put an undeclared editor ID into words, and leave a count of 0 out of the unpictured line?**

Qualify changes to reel_stills' spoken_text, shown_text or keep_names, evalreel's storyboard and render speech, reel_plan.account_counts or the reporting skill's unpicturedAccount and unpicturedCounts words (C2166-C2167); offline, no game.

Inputs: A hand-built presentation whose reel plan declares the round 2 patches' names, and the shipped skill data.

Outputs: One GREEN, RED or CRASH line per check (C2166-C2167).

Source: [integration/housecarl/tests/test_reel_names.py](../integration/housecarl/tests/test_reel_names.py) — `Known answers for declared names in the reel and the unpictured count line`


<a id="tool-integration-housecarl-tests-test_reel_vignettes.py"></a>

### integration/housecarl/tests/test_reel_vignettes.py

**Does the review reel plan and render Piranha's vignette format by rule: opening still, without/with/highlight vignettes, data stills, plain words, own-pair patch sentences and a closing still?**

Qualify changes to reel_plan.py, the reel's skill data, the capture plan or evalreel's stills and sequences; --presentation checks a compiled reel, --sabotage and --code-sabotage must go red.

Inputs: Controlled fixtures for test_reel_vignettes, or a compiled presentation.

Outputs: Assertion results for: Does the review reel plan and render the vignette format by rule?

Source: [integration/housecarl/tests/test_reel_vignettes.py](../integration/housecarl/tests/test_reel_vignettes.py) — `The review reel as clash vignettes:`


<a id="tool-integration-housecarl-tests-test_report_figures.py"></a>

### integration/housecarl/tests/test_report_figures.py

**Does the report show the frame that states a clash (the marked view, both halves of a split), and does the reel drop capital mod prefixes from names?**

Check figure selection and spoken-name changes against synthetic media rows.

Inputs: Controlled fixtures for test_report_figures.

Outputs: Assertion results for: Does the report show the frame that states a clash (the marked view, both halves of a split), and does the reel drop capital mod prefixes from names?

Source: [integration/housecarl/tests/test_report_figures.py](../integration/housecarl/tests/test_report_figures.py) — `Known answers for the report's figure units and the reel's spoken names and removals (Missives review, 2026-10-30).`


<a id="tool-integration-housecarl-tests-test_report_record_labels.py"></a>

### integration/housecarl/tests/test_report_record_labels.py

**Does the report say each record in words (a placed object by its kind and base, a name, an editor ID in words, a navmesh by its place), keep two different values apart and number records named alike, say once per check across mod pairs what to check in game, keep a patch verdict's names out of the reel, say why a cell could not be read, and say a long mod name in full once and then by its own short form?**

Qualify changes to evalreport's said_record, say_form_keys, record_object_name, said_place's unread cell, what_to_do's next steps or patch_acts, reel_plan's short_mod_names or say_short_forms, test_reel_vignettes' code-known-answer and code-split-left-arm arms, or the reporting skill's words for them (C2200-C2208); offline, no game.

Inputs: test_evalreport_prediction's synthetic bundles, test_navmesh_reel's planner fixture, hand-built sentences, and the shipped skill data.

Outputs: One GREEN, RED or CRASH line per check (C2200-C2208).

Source: [integration/housecarl/tests/test_report_record_labels.py](../integration/housecarl/tests/test_report_record_labels.py) — `Known answers for report record labels and What-to-do length`


<a id="tool-integration-housecarl-tests-test_report_reel_batch.py"></a>

### integration/housecarl/tests/test_report_reel_batch.py

**Do the report and reel say a single case in the singular, one reason per unchecked check, offline renders as renders, the location list's share of the load order, places by name or grid, forward-versus-carry-both, worse patch placements, a patch's lines on its own pair only, and relative PDF links?**

Qualify changes to the report's and reel's wording rules from regeneration 3 and item 18 (C1780-C1798); C1785 needs a browser and pypdf.

Inputs: Synthetic bundles from test_evalreport_prediction's builder, test_reel_vignettes' and test_reader_rules' fixtures, a browser for the PDF case.

Outputs: One PASS or FAIL line per case (C1780-C1798; C1797 held).

Source: [integration/housecarl/tests/test_report_reel_batch.py](../integration/housecarl/tests/test_report_reel_batch.py) — `Known answers for the report and reel wording batch`


<a id="tool-integration-housecarl-tests-test_report_reel_batch_2.py"></a>

### integration/housecarl/tests/test_report_reel_batch_2.py

**Do the report lane and orderleads read the shared record-flag table, do the report and reel name a mod the same way, does the patch search keep its unread listings, hub paths and build, does the author-advice count include stated clashes, and does the reel read media from every joined sibling?**

Qualify changes to the record-flag rule in the report lane, the reel's mod and patch names, the patch-search recorder and sentences, the author-advice count, or the reel's sibling media (C1800-C1814).

Inputs: Synthetic bundles from test_evalreport_prediction's builder, test_recordrows', test_reader_rules' and test_evalreport_siblings' fixtures, a stub houseCARL server.

Outputs: One PASS or FAIL line per case (C1800-C1814).

Source: [integration/housecarl/tests/test_report_reel_batch_2.py](../integration/housecarl/tests/test_report_reel_batch_2.py) — `Known answers for the second report and reel batch`


<a id="tool-integration-housecarl-tests-test_report_reel_wording.py"></a>

### integration/housecarl/tests/test_report_reel_wording.py

**Does the report and reel say why a clash was not captured (no camera for its kind, or the capture limit), say each patch fact in one wording, list each Nexus patch page once in What to do, keep form keys and editor IDs off still pages and report places, name both objects of a one-mark view, count navmesh links and groups in the intro, resolve evalpipeline's path arguments at parse time, and read the needs-some sentence in short sentences, and say in the reel the place the report names?**

Qualify changes to reel_plan's unpictured reasons, patch phrasing, navmesh intro or marked-view label, report_scenes.capturable_checks, reel_stills' drawn page text, evalreport's patch_search_leads, said_place or highlight_caption, pluginread.cell_grids, evalpipeline's path arguments, or the reporting skill's words for them (C2120-C2131); offline, no game.

Inputs: test_navmesh_reel's and test_reel_vignettes' hand-built presentations, a Clockwork-shaped patch search, the shipped skill data, and Skyrim.esm's own cell 00929E when the SSE install is present.

Outputs: One GREEN, RED, SKIP or CRASH line per check (C2120-C2131).

Source: [integration/housecarl/tests/test_report_reel_wording.py](../integration/housecarl/tests/test_report_reel_wording.py) — `Known answers for the report and reel wording batch`


<a id="tool-integration-housecarl-tests-test_report_words.py"></a>

### integration/housecarl/tests/test_report_words.py

**Does the report and reel say a name ending in s with a bare apostrophe ("Interesting NPCs'"), a landscape record by the ground of its cell, a patch named after the evaluated mod by the rest of its name, a long plugin file name in full once with a gloss and short after, and a buried object by its item's words?**

Qualify changes to reel_plan's fill, possessive, short_mod_names or say_short_forms, evalreport's bind, said_record's landscape words, say_report_short_forms or buried_lines, readerdoc's bind, pluginread.record_cells, or the reporting skill's words for them (C2310-C2319); offline, no game.

Inputs: test_reel_vignettes' synthetic evaluation, test_evalreport_prediction's synthetic bundles, a synthetic plugin file, hand-built sentences, and the shipped skill data.

Outputs: One GREEN, RED or CRASH line per check (C2310-C2319).

Source: [integration/housecarl/tests/test_report_words.py](../integration/housecarl/tests/test_report_words.py) — `Known answers for report and reel words`


<a id="tool-integration-housecarl-tests-test_same_name_variant.py"></a>

### integration/housecarl/tests/test_same_name_variant.py

**Is a same-named plugin from another folder planned and reported as itself, not as the measured copy?**

Qualify plan rows and report comparison kinds for arms that swap in an author's same-named variant.

Inputs: A temporary MO2 instance with two copies of one plugin, and hand-built report bundles.

Outputs: Assertion results for: Is a same-named plugin from another folder planned and reported as itself, not as the measured copy?

Source: [integration/housecarl/tests/test_same_name_variant.py](../integration/housecarl/tests/test_same_name_variant.py) — `A same-named plugin from another folder is planned and reported as itself (cases 451-459).`


<a id="tool-integration-housecarl-tests-test_stategen.py"></a>

### integration/housecarl/tests/test_stategen.py

**Does a Deep evaluation judge every census state and name every one it cannot?**

Qualify the state generator and the evaluation depth through the real controller.

Inputs: Controlled fixtures for test_stategen.

Outputs: Assertion results or generated fixture data for: Does a Deep evaluation judge every census state and name every one it cannot?

Source: [integration/housecarl/tests/test_stategen.py](../integration/housecarl/tests/test_stategen.py) — `Known answers for the Deep evaluation's state generator (stategen.py) and evalrun's evaluation depth.`


<a id="tool-integration-housecarl-tests-test_subject_gate.py"></a>

### integration/housecarl/tests/test_subject_gate.py

**Does subject qualification refuse wrong identities, invisible features and invalid evidence?**

Check capture acceptance rules with deliberately misbound subjects and unusable visual witnesses.

Inputs: Controlled fixtures for test_subject_gate.

Outputs: Assertion results or generated fixture data for: Does subject qualification refuse wrong identities, invisible features and invalid evidence?

Source: [integration/housecarl/tests/test_subject_gate.py](../integration/housecarl/tests/test_subject_gate.py) — `*case["viewArgs"]]`


<a id="tool-integration-housecarl-tests-test_switchflip_outcome.py"></a>

### integration/housecarl/tests/test_switchflip_outcome.py

**Does the runtime switch reading rule classify crashes, stalls and completions correctly?**

Qualify a change to the live classifier against known crash, stall, short-window and unreached-target streams.

Inputs: Controlled fixtures for test_switchflip_outcome.

Outputs: Assertion results or generated fixture data for: Does the runtime switch reading rule classify crashes, stalls and completions correctly?

Source: [integration/housecarl/tests/test_switchflip_outcome.py](../integration/housecarl/tests/test_switchflip_outcome.py) — `Known answers for the runtime state-switch reading rule`


<a id="tool-integration-housecarl-tests-test_switchflip_plan.py"></a>

### integration/housecarl/tests/test_switchflip_plan.py

**Does the live driver refuse a bad flip plan and export one arm's reading correctly?**

Check flip-plan validation, the block lock and the export before any launch.

Inputs: Controlled fixtures for test_switchflip_plan.

Outputs: Assertion results or generated fixture data for: Does the live driver refuse a bad flip plan and export one arm's reading correctly?

Source: [integration/housecarl/tests/test_switchflip_plan.py](../integration/housecarl/tests/test_switchflip_plan.py) — `Known answers for switchflip.py's launch-free parts`


<a id="tool-integration-housecarl-tests-test_switchflip_rule_unavailable.py"></a>

### integration/housecarl/tests/test_switchflip_rule_unavailable.py

**Does the live driver keep a launch's cleanup when the reading rule's owner cannot be asked?**

Qualify a change to switchflip.py or switchflip_outcome.py around runcontrol failures.

Inputs: switchflip.py and an unset or missing HOUSECARL_RUNCONTROL.

Outputs: Assertion results for: Does the live driver keep a launch's cleanup when the reading rule's owner cannot be asked?

Source: [integration/housecarl/tests/test_switchflip_rule_unavailable.py](../integration/housecarl/tests/test_switchflip_rule_unavailable.py) — `Known answers for the live driver when the reading rule's owner cannot be asked`


<a id="tool-integration-housecarl-tests-test_switchflip_scope.py"></a>

### integration/housecarl/tests/test_switchflip_scope.py

**Does the live switch check's population rule keep unknowns open and answer not-applicable only when nothing driven touches a target?**

Qualify a change to which switches the runtime switch check is about.

Inputs: Controlled fixtures for test_switchflip_scope.

Outputs: Assertion results or generated fixture data for: Does the live switch check's population rule keep unknowns open and answer not-applicable only when nothing driven touches a target?

Source: [integration/housecarl/tests/test_switchflip_scope.py](../integration/housecarl/tests/test_switchflip_scope.py) — `Known answers for which switches the runtime state-switch check is about`


<a id="tool-integration-housecarl-tests-test_terrain_stations.py"></a>

### integration/housecarl/tests/test_terrain_stations.py

**Where no navmesh stands an exterior camera, does it stand on bare terrain only when the LAND footprint is planar and at most the measured 43.7 degrees, with nothing beside, overhead or under it but the ground, while navmesh stations keep today's rule?**

Qualify changes to the terrain station rule in capture_points or shotplan (terrain_slope, terrain_verdict) (C1980-C1989); offline, no game.

Inputs: Toy box scenes and a LAND vertex grid for fake_viewprobe.py; the real picker, capture_acquire and shotplan.

Outputs: One ok/FAIL line per check; exit 1 on any failure.

Source: [integration/housecarl/tests/test_terrain_stations.py](../integration/housecarl/tests/test_terrain_stations.py) — `Offline known answers for terrain stations`


<a id="tool-leftbehindprobe"></a>

### leftbehindprobe

**Was the geometry behind an occlusion panel moved, removed or replaced by the other mod?**

Investigate stale occlusion after a mod replaces or relocates the objects a panel originally hid.

Inputs: Occlusion primitives, covered geometry and compared orders.

Outputs: Evidence that covered geometry moved, disappeared or was replaced.

Source: [_geometry-harness/leftbehindprobe/Program.cs](../_geometry-harness/leftbehindprobe/Program.cs) — `// LEFT-BEHIND OCCLUSION PANELS — for a mod combination, was the geometry each occlusion panel was authored against`


<a id="tool-lightclash"></a>

### lightclash

**Does a patch edit on a light fix a lighting clash that only the combination of two mods produces (orphaned, leaking, doubled or over-limit lights)?**

Judge patch edits on model-less lights with four-setup attribution instead of reading them as cannot tell.

Inputs: lightprobe JSON for the combined, single-mod, dependency-only and patched orders, and the patch edits.

Outputs: Per light and per edit: each class's reading and attribution, and a verdict with its reason.

Source: [_geometry-harness/lightclash.py](../_geometry-harness/lightclash.py) — `Light-clash fold over lightprobe outputs`


Modifiers (do not select a separate mode):

- `--arm` — NAME=lightprobe.json for u, plusa, plusb, dep and p (repeat).
- `--edits` — JSON list of {id, action, records} to judge.
- `--emit-threshold` — The smallest emissive value (max RGB x multiple) that counts as emitting (default 0.02).
- `--host-radius` — H: a light is hosted by the placed object whose emitting part (lightprobe emitters) is within this distance of it (default 64 u).
- `--leak-threshold` — Required: the leak magnitude's threshold from the calibration (printed only, never decides).
- `--mods` — The two mods' plugin file names, plusa's then plusb's.
- `--out` — Write the per-edit readings and verdicts here.

<a id="tool-lightprobe"></a>

### lightprobe

**Which lights does a load order place in an interior cell, with what radius, shadow and portal-strict flags, and are they enabled at load?**

**Does a placed light shine through an opaque surface, sit behind a wall or inside a solid, and how many lights reach each drawn shape?**

Investigate lighting clashes between mods (orphaned, leaking, doubled or over-limit lights) before judging a patch edit on a model-less light.

Inputs: A load order (profile or --order candidate) and interior cells by editor id.

Outputs: Per cell: every reference (base, type, editor id, model, pose, load-time enable state, deletion, responsible plugin) and every placed light (radius: base plus the reference's XRDS offset, flags, shadow, portal-strict, fade, colour).; With --geometry, per enabled light: facing (front / back / no first hit over 64 rays), leak (attenuated area lit through an opaque surface, by receiving reference) and point-in-solid; per lit shape whose placed box a light's radius reaches: the lights reaching it (count, shadow, strict; the bound-sphere count beside it); unreadable models named and lights near them labelled incomplete; whether Light Limit Fix is installed in the order's files (identity.llf); per enabled, drawn reference with a model, its placed emitting parts (emissive lighting-shader shapes with their value, self-lit effect shapes, self-lit particle emitter extents) or why its mesh could not be read for them.; identity.solidPolicy and identity.anchorPolicy: the solid rule (MeshIntersection.PolicyVersion) and the point reading (MeshAnchor.PolicyVersion) when the run wrote at least one point-in-solid row, else "none"; identity.pointInSolidRows counts the rows written, refused ones included.

Limit: Not settled: whether a lighting change reads as broken or merely different. This probe measures placement, radius, flags, light leaking through opaque surfaces and the lights reaching each shape; how the result looks is a judgement made in game (world-edit-compatibility SKILL.md §7).

Source: [_geometry-harness/lightprobe/Program.cs](../_geometry-harness/lightprobe/Program.cs) — `// lightprobe`


Modifiers (do not select a separate mode):

- `--cell` — An interior cell by editor id; repeatable.
- `--cells-file` — Interior cell editor ids, one per line ('#' comments).
- `--game` — Game directory (else the harness config).
- `--geometry` — Read the enabled references' render models and add the light readings (facing, leak, point-in-solid, per-shape counts).
- `--instance` — MO2 instance directory (else the harness config).
- `--json` — Write the per-cell references, lights and readings to this path (required).
- `--list-interiors` — List every interior cell of the order with its placed-light counts instead of measuring.
- `--max-mb` — Stop with exit 3 when private bytes pass this many MB at a checkpoint (0, the default, is no cap).
- `--order` — Walk this candidate configuration (CandidateFile) instead of the profile's order.
- `--profile` — MO2 profile name (else the harness config).
- `--self-test` — Run the light-reading known answers (synthetic geometry) and exit; with --meshes-from GAME_DATA also the emitter-reading known answers on vanilla meshes from that folder's Meshes archives.

<a id="tool-lookprobe"></a>

### lookprobe

**Which patches sit on disk not enabled, which runtime files point at plugins or records the order lacks, which two mods' swaps meet on one base, which scripts lack a parent or called class, and what names an empty dummy plugin?**

Before or beside a whole-order screen: cheap read-only looks at a load order's files for patches left off and runtime layers pointing at nothing, each row saying how its consequence is known.

Inputs: An MO2 instance, profile and game folder.

Outputs: looks.json (rows, set-aside counts, the per-line swap table, the CONFLICTS block, a plugin and folder index for the screen) and a text summary.

Source: [integration/housecarl/lookprobe/Program.cs](../integration/housecarl/lookprobe/Program.cs) — `// LOOKPROBE — the whole-order looks, run over a load order: a patch on disk and not enabled, a swap file pointing at a`


Modifiers (do not select a separate mode):

- `--game` — The game folder whose Data the order reads; required.
- `--instance` — The MO2 instance directory; required, never defaulted from the harness config.
- `--order` — Evaluate a candidate order file instead of the profile (_shared/HarnessOrderCli.cs).
- `--out` — Write the looks as JSON to FILE; refused under the instance or the game folder.
- `--profile` — The MO2 profile name; required.
- `--self-test` — Run the known-answer self-test over a synthetic MO2 instance; exit 0 when every case passes.
- `--text` — Also write the plain-text summary to FILE; refused under the instance or the game folder.

<a id="tool-machineidle"></a>

### machineidle

**Is the machine idle enough to launch the game or time a heavy job?**

Gate a game launch or a timing on free memory, running probes found by command line, and the game, Mod Organizer, recorder and build servers found by process name.

Inputs: The running processes, free physical memory and optional lock files.

Outputs: Idle or not, with one named reason per blocker.

Source: [_geometry-harness/machineidle.py](../_geometry-harness/machineidle.py) — `machineidle — is this machine idle enough to launch the game or time a heavy job?`


Modifiers (do not select a separate mode):

- `--json` — write the verdict here as JSON
- `--min-free-gb` — the free physical memory floor in GB (default 16)
- `--refuse-if` — a lock file whose presence refuses (repeatable)

<a id="tool-missdiag"></a>

### missdiag

**Why did the detectors miss these patch edits, and what remains unknown?**

Investigate edits made by an existing patch that the current detector set did not predict.

Inputs: Patch edits and retained detector results.

Outputs: Grouped missed edits and explicit unresolved causes.

Source: [_geometry-harness/missdiag.py](../_geometry-harness/missdiag.py) — `Miss diagnosis over a patch scorecard's unjudged edits`


<a id="tool-modcensus"></a>

### modcensus

**Which candidate mods touch record types, placements, edit kinds or asset overrides the shakedown has not yet evaluated?**

Choose which candidate mods earn a full evaluation, before preparing any of them.

Inputs: Candidate entries (mod folder and plugins), the source MO2 instance and profile, and a houseCARL server.

Outputs: One census row per candidate (counts only from completed reads, refusals otherwise) and a novelty ranking.

Source: [_geometry-harness/modcensus.py](../_geometry-harness/modcensus.py) — `modcensus - a no-game, no-render census of candidate mods, and which of them add something not yet evaluated.`


<a id="tool-modcensus.novelty"></a>

### modcensus.novelty

**Which census candidates have features the mods already evaluated lack?**

Rank census candidates for full evaluation against the covered set.

Selector: `novelty`

Inputs: Candidate entries (mod folder and plugins), the source MO2 instance and profile, and a houseCARL server.

Outputs: One census row per candidate (counts only from completed reads, refusals otherwise) and a novelty ranking.

Source: [_geometry-harness/modcensus.py](../_geometry-harness/modcensus.py) — `add_parser(`


<a id="tool-modcensus.run"></a>

### modcensus.run

**What does each candidate mod touch, which edit kinds does it and its key patches make, and which assets does it override?**

Census candidate mods on their source profile before choosing any for evaluation.

Selector: `run`

Inputs: Candidate entries (mod folder and plugins), the source MO2 instance and profile, and a houseCARL server.

Outputs: One census row per candidate (counts only from completed reads, refusals otherwise) and a novelty ranking.

Source: [_geometry-harness/modcensus.py](../_geometry-harness/modcensus.py) — `add_parser(`


Modifiers (do not select a separate mode):

- `--batch` — candidates per scopeprobe launch
- `--candidates` — JSON list of {id, mod, plugins}
- `--census` — a census folder to rank (repeatable)
- `--covered` — a census folder of mods already evaluated (repeatable)
- `--game` — the source game folder
- `--instance` — the source MO2 instance
- `--key-max-masters` — read edit kinds of key patches with at most this many masters (larger ones are listed only)
- `--out` — the census folder
- `--profile` — the source profile
- `--reader-cmd` — the houseCARL server command line

<a id="tool-modnames"></a>

### modnames

**Where does the review reel get each mod's name, and how does it say a name is not the Nexus one?**

**How is a plugin matched to its Nexus mod page from the MO2 meta.ini and the Nexus catalogue?**

Resolve the names the review reel speaks: declared, else the Nexus catalogue page of the plugin's mod, else its MO2 folder with the reason.

Inputs: The evaluation's plans (where each plugin was read), the scope's targets, declared names, the Nexus catalogue store and optionally a houseCARL server.

Outputs: Each plugin's name and source, and the evidence document evalreport writes to evidence/mod-names.json.

Source: [integration/housecarl/modnames.py](../integration/housecarl/modnames.py) — `The review reel's mod names: each plugin's Nexus page name`


<a id="tool-motionprobe"></a>

### motionprobe

**What moves in this mesh, through which animations, and what space does the moving part sweep?**

Read an animated object's motion (NIF sequences and controllers, behaviour-graph clips), pose it at a named animation time or rest state, or measure its swept region before judging a clash with it.

Inputs: A mesh, clip or behaviour-graph path, resolved through a load order or a loose Data-like folder.

Outputs: JSON: animations, named poses, posed geometry bounds, decoded clips, graph readings or swept-region intervals; refusals name their cause. Every sweep output carries solidPolicy: the solid rule (MeshIntersection.PolicyVersion) when the region built, else "none".

Source: [_geometry-harness/motionprobe/Program.cs](../_geometry-harness/motionprobe/Program.cs) — `// motionprobe — what moves a mesh, and where it goes (animated-object swept volume).`


Modifiers (do not select a separate mode):

- `--octree-threads` — With sweep, the threads that classify the swept-volume octree's cells (default: the processor count; 1 = serial). The answer does not depend on it; stderr reports the cells, near tests and triangle tests made.
- `--rules` — graph,book,physics,switch,skin: read the motion as overlapprobe --reach-rules does (inspect, pose, node, sweep, graph). The physics verb (no flag) prints a mesh's Havok bodies (mass, motion system, layer, movable), constraints (limits, model-frame pivots and axes) and kind (none, fixed, free, pinned, free-chain, mixed).

<a id="tool-mutagenprobe"></a>

### mutagenprobe

**Which placement and navmesh types and members exist in the pinned Mutagen API?**

Inspect the pinned library before implementing a placement or navigation reader against unfamiliar model interfaces.

Inputs: Pinned Mutagen assemblies; no game required for API inspection.

Outputs: Reflected placement/navmesh types and members.

Source: [_geometry-harness/mutagenprobe/Program.cs](../_geometry-harness/mutagenprobe/Program.cs) — `// Reflection probe over Mutagen.Bethesda.Skyrim 0.54.4 — the EXACT version houseCARL pins.`


<a id="tool-navedit"></a>

### navedit

**How do I plan, write and read back a navmesh edit or a navownprobe repair plan as a new patch plugin?**

Move, add or remove navmesh vertices and triangles, place navmesh markers, or write a repair plan that navownprobe produced.

Inputs: An edits, repair-plan or markers file and the MO2 instance, game folder and profile.

Outputs: A plan file, or a new patch plugin with its read-back verdict.

Source: [_geometry-harness/navedit/Program.cs](../_geometry-harness/navedit/Program.cs) — `// navedit — NAVMESH EDITING from the command line`


<a id="tool-navownprobe"></a>

### navownprobe

**Who supplies the winning navmesh, and whose navmesh was overwritten?**

Diagnose lost pathing after a later plugin replaces another mod's navigation records.

Inputs: Ordered plugins and selected navmesh scope.

Outputs: Winning and overwritten navmesh ownership.

Limit: Not covered: whether a lost navmesh edit leaves any place unreachable. This probe reports whose navmesh wins and whose edit was lost (CLOBBERED), never broken pathing; connectivity is its selector's stated blind spot both ways (world-edit-compatibility check registry), and SKILL.md §7 lists reachability as unresolved on disk.

Source: [_geometry-harness/navownprobe/Program.cs](../_geometry-harness/navownprobe/Program.cs) — `// NAVMESH OWNERSHIP — who actually supplies the navmesh the game will use, and whose work got`


Modifiers (do not select a separate mode):

- `--moved-ref-selftest` — Set moved ref selftest for this invocation; consult the linked parser for accepted values.
- `--nav-invariants` — --nav-invariants OUT PLUGIN...: measure each plugin's navmesh grid and box invariants through Mutagen and the core grid decode (navownprobe/NavInvariants.cs), written to OUT as JSON.
- `--navgrid-oracle` — rebuild every navmesh grid of the named plugins and compare it piece by piece with the stored grid (a measurement)
- `--navgrid-selftest` — run the navmesh grid rebuild's known answers (NavGrid.cs) and exit; tests/test_navgrid_rebuild.py drives it
- `--navrepair-selftest` — --navrepair-selftest: the navmesh repair plan (_shared/NavRepair.cs) on small in-memory navmeshes, one ok or FAIL line per case, for tests/test_navmesh_repair_rules.py; test-only.
- `--repair-readback` — --repair-readback PLAN PATCH OUT: hold every navmesh a written repair patch carries to its winner with exactly the plan's change applied (_shared/NavRepairReadBack.cs), written to OUT as JSON; exit 1 names each difference.
- `--write-edge-link-fixture` — --write-edge-link-fixture DIR: write the edge-link identity fixture (navownprobe/EdgeLinkFixture.cs) that tests/test_navown_edge_identity.py runs the real probe on; test-only.
- `--write-mover-fixture` — write the navmesh-repair mover fixture (optionally with doors or a neighbour) and print its path
- `--write-navi-master-fixture` — write the navmesh-repair fixture whose NAVI master case a test reads, and print its path
- `--write-navrepair-fixture` — --write-navrepair-fixture DIR: write the navmesh repair fixture (navownprobe/NavRepairFixture.cs) that tests/test_navmesh_repair_rules.py runs the real probe on; test-only.

<a id="tool-navprobe"></a>

### navprobe

**What does the geometry detector report on the vanilla calibration population?**

Calibrate detector output against vanilla scenes before assigning meaning to modded findings.

Inputs: Base-game calibration population and configured assets.

Outputs: Geometry detector calibration results.

Source: [_geometry-harness/navprobe/Program.cs](../_geometry-harness/navprobe/Program.cs) — `// Milestone 4 CALIBRATION.`


Modifiers (do not select a separate mode):

- `--band` — Set band for this invocation; consult the linked parser for accepted values.
- `--bos` — Set bos for this invocation; consult the linked parser for accepted values.
- `--dump` — Set dump for this invocation; consult the linked parser for accepted values.
- `--flags` — Set flags for this invocation; consult the linked parser for accepted values.
- `--geometry` — Set geometry for this invocation; consult the linked parser for accepted values.
- `--moved-ref-selftest` — Set moved ref selftest for this invocation; consult the linked parser for accepted values.
- `--state` — Set state for this invocation; consult the linked parser for accepted values.

<a id="tool-niflyprobe"></a>

### niflyprobe

**Which NIF transform and collision types and members exist in the pinned Nifly API?**

Inspect the pinned mesh library before coding against transform or collision structures.

Inputs: Pinned Nifly assembly.

Outputs: Reflected transform and collision API members.

Source: [_geometry-harness/niflyprobe/Program.cs](../_geometry-harness/niflyprobe/Program.cs) — `// Reflection probe over Nifly 1.1.0 — the version houseCARL pins.`


<a id="tool-orderleads"></a>

### orderleads

**Which clashes between mods did a whole-order pass find for an added mod, at a place, or between two plugins, and where do I look first?**

After a whole-order pass (orderpass.py), to read its leads for a mod you are adding, a cell you care about or a pair of plugins, with dummies, no-signal plugins and unassessed checks named.

Inputs: A pass folder: ordermap's map.json and the checks' outputs; optionally a dummy list or cell-name table for a map that lacks sizes or names.

Outputs: index.json (every group with its lead or set-aside reason, dummies, no-signal plugins, unassessed checks, the flag census), LEADS.md (the added mod's view when one was added) and LEADS-whole.md; read prints a filtered view.

Source: [_geometry-harness/orderleads.py](../_geometry-harness/orderleads.py) — `Leads from a whole-order pass, queryable by added mod, place and pair.`


<a id="tool-orderleads.read"></a>

### orderleads.read

**Which leads does a pass hold for this mod, this cell or these two plugins?**

Ask a pass by --added, --place (EditorID, FormKey, or worldspace and grid), --place-prefix, --pair or --family; --whole gives the whole-order list.

Selector: `read`

Inputs: A pass folder and the query flags.

Outputs: The matching leads in the whole-order list's order, each with its whole-order rank, or one sentence saying why nothing matches.

Source: [_geometry-harness/orderleads.py](../_geometry-harness/orderleads.py) — `def query(self, added=None, place=None, place_prefix=None, pair=None, family=None):`


Modifiers (do not select a separate mode):

- `--added` — An added mod's plugin or folder; repeat for several.
- `--cell-names` — A cell-name table for a map without cell names.
- `--dummies` — A measured dummy list for a map without plugin sizes.
- `--family` — One check family.
- `--format` — md or json.
- `--limit` — How many leads to print.
- `--out` — Where build writes (default the pass folder).
- `--pair` — Two plugins, or two mod folders.
- `--pass` — The pass folder.
- `--place` — A cell EditorID, FormKey, or worldspace and grid as the rows print it.
- `--place-prefix` — Every place whose name starts with this.
- `--rules` — A rules file (default orderleads-rules.json).
- `--whole` — The whole-order list, not the added mod's view.

<a id="tool-ordermap"></a>

### ordermap

**Which plugins and mod folders in a whole load order meet, and how: shared records, cells, neighbouring cells, placed bases, form links and asset paths compared by bytes?**

Before a whole-order pass (orderpass.py), or to see which installed plugins meet a given one: one index over every plugin and mod folder, with every cell's FormKey, EditorID, worldspace and grid, and each plugin's size so an empty header-only plugin (a dummy) can be told.

Inputs: An MO2 instance, profile and game folder (or a --order candidate file).

Outputs: map.json: plugins (folder, size, masters, record counts), plugin-pair edges by kind with examples, folder-pair asset edges (identical, different, the winner by the game's rule), runtime-layer files per folder, every cell by FormKey, and the stage times.

Source: [_geometry-harness/ordermap/Program.cs](../_geometry-harness/ordermap/Program.cs) — `// ORDERMAP - one index over a whole load order: which plugins and mod folders meet, and how.`


<a id="tool-ordermap.self-test"></a>

### ordermap.self-test

**Does the order map give its hand-derived answers on a synthetic instance?**

After any change to the map's edges, asset comparison, cell names or sizes.

Selector: `--self-test`

Inputs: None: Mutagen-written plugins in a temporary folder (ORDERMAP_TEST_DIR picks its parent).

Outputs: PASS/FAIL per check; exit 1 on any failure.

Source: [_geometry-harness/ordermap/SelfTest.cs](../_geometry-harness/ordermap/SelfTest.cs) — `// Hand-derived answers on a synthetic instance.`


Modifiers (do not select a separate mode):

- `--game` — The game folder.
- `--instance` — The MO2 instance folder.
- `--no-hash` — Do not read the bytes of shared asset paths; every shared path then counts as unknown.
- `--order` — A candidate order file instead of the profile's order.
- `--out` — Where map.json goes.
- `--profile` — The MO2 profile name.
- `--self-test` — Run the synthetic known answers; exit 0 when every check passes.
- `--selftest` — The same as --self-test.

<a id="tool-orderpass"></a>

### orderpass

**How do I run every check over a whole load order once, and get its leads with an added mod's view first?**

Screen a whole order (or the order plus a mod you are adding) without picking pairs first; read the result with orderleads.py.

Inputs: An MO2 instance, profile and game folder (or a --order file), the built probes, and the houseCARL MCP server for the record stage.

Outputs: A pass folder: map.json, each check's output, records.json, stages.json, pass.json, and orderleads' index.json, LEADS.md and LEADS-whole.md.

Source: [_geometry-harness/orderpass.py](../_geometry-harness/orderpass.py) — `One whole-order pass: the order's map, every check over the whole order, the record stage, then the leads.`


Modifiers (do not select a separate mode):

- `--added` — An added mod's plugin or folder: its view is the pass's first read.
- `--deadline-min` — Each stage's time limit.
- `--game` — The game folder.
- `--instance` — The MO2 instance folder.
- `--order` — A candidate order file.
- `--out` — The pass folder to write.
- `--profile` — The MO2 profile name.
- `--reader` — The houseCARL MCP server for the record stage (or HOUSECARL_READER).
- `--route-cap-targets` — 0: every plugin with cells is a route target.
- `--sample-memory` — Sample probe memory every 10 s into memory.csv (Windows).
- `--skip` — A stage to leave out; it is named unassessed.

<a id="tool-orderprobe"></a>

### orderprobe

**Which bounded alternative load orders satisfy the stated constraints?**

Explore feasible arrangements after collecting explicit precedence constraints and bounding the search.

Inputs: Bounded plugin set, constraints and comparison policy.

Outputs: Candidate orders satisfying the stated constraints.

Source: [_geometry-harness/orderprobe/Program.cs](../_geometry-harness/orderprobe/Program.cs) — `// ORDERPROBE — bounded, constrained alternatives to the user's load order`


Modifiers (do not select a separate mode):

- `--budget` — Set budget for this invocation; consult the linked parser for accepted values.
- `--game` — Set game for this invocation; consult the linked parser for accepted values.
- `--instance` — Set instance for this invocation; consult the linked parser for accepted values.
- `--min-contention` — Set min contention for this invocation; consult the linked parser for accepted values.
- `--out` — Set out for this invocation; consult the linked parser for accepted values.
- `--profile` — Set profile for this invocation; consult the linked parser for accepted values.
- `--rules` — Set rules for this invocation; consult the linked parser for accepted values.

<a id="tool-ordersweep"></a>

### ordersweep

**How do the real detectors compare across candidate load orders?**

Measure detector outcomes for several proposed plugin arrangements before accepting an ordering change.

Inputs: Candidate load orders and configured detector commands.

Outputs: Measured detector differences across orders.

Source: [_geometry-harness/ordersweep.py](../_geometry-harness/ordersweep.py) — `ordersweep — evaluate bounded alternative load orders through the real detectors`


Modifiers (do not select a separate mode):

- `--baseline` — the baseline CandidateFile (default: the one named *-baseline.json)
- `--candidates` — Set candidates for this invocation; consult the linked parser for accepted values.
- `--deadline-seconds` — the caller's remaining deadline: each probe is then finished only when its whole process tree is empty, and a probe is cached only after that (evalrun passes it under a budget)
- `--diff-tolerance` — conflictdiff --tolerance (points)
- `--full-reverse` — measure every reverse by sweeping the baseline order again, even for a probe that can take the baseline emit's rows (RECONCILE_REVERSE: overlapprobe --reverse-of)
- `--game` — Set game for this invocation; consult the linked parser for accepted values.
- `--harness` — Set harness for this invocation; consult the linked parser for accepted values.
- `--instance` — Set instance for this invocation; consult the linked parser for accepted values.
- `--max-candidates` — Set max candidates for this invocation; consult the linked parser for accepted values.
- `--no-pair-results` — run every probe without the placed-pair results of earlier jobs (PAIR_RESULTS: overlapprobe --pair-cache-read/--pair-cache-write); each job measures every pair
- `--no-reverse` — skip the reverse measurement (created rows then read unmeasured)
- `--out` — Set out for this invocation; consult the linked parser for accepted values.
- `--population` — The plan's population file: passed to the probes --population-probes names, and part of their request key by its sha256; the other probes run on the whole order with their keys unchanged.
- `--population-probes` — Comma-separated probes that take --population (from the registry's populationFlag).
- `--probe-args` — extra arguments passed to every probe, e.g. --state X
- `--probe-cmd` — run NAME (a probe, or conflictdiff) as <argv prefix> instead of its Release binary. Repeatable. A test seam: it changes which program runs, not how the result is read.
- `--probes` — Set probes for this invocation; consult the linked parser for accepted values.
- `--profile` — Set profile for this invocation; consult the linked parser for accepted values.
- `--reuse-cache` — an earlier launch's cache.json to reuse verified emits from, read-only; repeatable, searched in the order given after this --out's own cache (evalrun passes one per earlier launch)

<a id="tool-overlap-attribution"></a>

### overlap-attribution

**Which overlap findings require both mods rather than already occurring in a source setup?**

Decide whether a combined setup introduces a geometry conflict or inherits it from a source configuration.

Inputs: Separate source and combined overlap results with matching scope.

Outputs: Attribution to inherited or combined-mod overlap.

Source: [_geometry-harness/overlap_attribution.py](../_geometry-harness/overlap_attribution.py) — `Configuration-correct attribution for overlapprobe scene manifests.`


Modifiers (do not select a separate mode):

- `--candidate` — Set candidate for this invocation; consult the linked parser for accepted values.
- `--combined` — Set combined for this invocation; consult the linked parser for accepted values.
- `--compare` — Set compare for this invocation; consult the linked parser for accepted values.
- `--output` — Set output for this invocation; consult the linked parser for accepted values.
- `--setup` — Set setup for this invocation; consult the linked parser for accepted values.

<a id="tool-overlap-triage"></a>

### overlap-triage

**How can I rank and group overlap findings into local incidents while retaining every member?**

Reduce a large collision finding list to reviewable incidents without dropping the underlying pairs.

Inputs: Overlap findings with subject/counterpart identities.

Outputs: Local incident groups and complete member accounting.

Source: [_geometry-harness/overlap_triage.py](../_geometry-harness/overlap_triage.py) — `Add generic overlap features, analogues, ranking and local incidents to a canonical manifest.`


Modifiers (do not select a separate mode):

- `--manifest` — Set manifest for this invocation; consult the linked parser for accepted values.
- `--out` — Set out for this invocation; consult the linked parser for accepted values.
- `--policy` — Set policy for this invocation; consult the linked parser for accepted values.

<a id="tool-overlapprobe"></a>

### overlapprobe

**Which placed objects overlap across mods under this configuration?**

Start a geometry conflict survey where multiple mods place objects in a shared space.

Inputs: Winning placed geometry and selected order/cells.

Outputs: Inter-mod overlap findings and coverage/refusal information.

Limit: Not settled: whether a measured overlap is visible or broken in game. This probe measures where placed geometry overlaps from the files; visual review is a separate state, and not visually reviewed is the default (world-edit-compatibility references/overlap-measurements.md).

Source: [_geometry-harness/overlapprobe/Program.cs](../_geometry-harness/overlapprobe/Program.cs) — `// MILESTONE 5 — CROSS-PLUGIN OBJECT OVERLAP, differential. The geometry charter's own motivating`


Modifiers (do not select a separate mode):

- `--absent-models` — With --reach-rules absent, a JSON list of {modelPath, providerNote, evidence}: a placement whose model is missing from every active provider reads no_geometry naming the provider search and the measurement; no entry, the refusal stands.
- `--base` — An earlier emit this run is diffed against: each of its records is re-measured under this order and carried as Predicates.
- `--bos` — Base Object Swapper layer on or off (default on).
- `--candidate-mod` — An MO2 mod folder under evaluation in the scene capture (repeatable).
- `--candidate-plugin` — A plugin under evaluation in the scene capture (repeatable).
- `--contact-evidence-pairs` — Pair keys to read with contact evidence in a scene capture; the input's sha256 is recorded.
- `--depth` — Refused: actual-shape discovery has no box depth gate.
- `--disable` — Disable this plugin for the run (repeatable).
- `--dump` — Write one diagnostic line per candidate (key, plugin, base type, mesh, geometry source, transform, world box) to this path.
- `--emit` — Write the conflict report (the emit file a differential compares) to this path.
- `--enable` — Enable this plugin for the run (repeatable).
- `--flags` — Population by the record's initially-disabled flag alone instead of the load-time enable chain.
- `--force-enable` — --force-enable FORMKEY[,FORMKEY...] reads each listed initially-disabled singleton (no enable parent, not itself an enable parent) as enabled after load in every arm, labelled 'enabled by hand (its enabler is not sourced by the switch census)'; a switch member, an enable-parented or already-enabled reference is refused by name (exit 2). The output names every forced reference (forcedEnabled). Default off: outputs unchanged.
- `--frac` — Refused: actual-shape discovery has no box fraction gate.
- `--game` — Game directory (else the harness config, only for its own instance).
- `--geometry-manifest` — Write the scoped geometry manifest to this path; needs --responsible-pair or a scene capture (--scene-id).
- `--historical-pairs` — A complete pair crosswalk (pairs[]) whose reference identities are carried into the geometry manifest.
- `--instance` — MO2 instance directory (else HOUSECARL_MO2_INSTANCE; never the harness config: refused instance-not-explicit).
- `--json` — Write every judged row to this path, for spot-checks.
- `--list` — How many conflict groups the console summary lists (integer, default 25).
- `--max-triangle-pairs` — Work cap per pair: triangle pairs tested before the pair is reported unresolved (default 2,000,000).
- `--maxdim` — Refused: actual-shape discovery has no box size gate.
- `--measured-approximations` — With --reach-rules approx, a JSON list of {modelPath, sha256, modelBox {min, max}, label, evidence}: an in-game measured box in the model's frame, matched by path and exact bytes, reported as a labelled part beside the file bound and held by the region; an entry without evidence refuses the list.
- `--mesh-folder` — NAME (repeatable; * for every folder): without --population, the mod folders the folder rule may credit with a shape; with neither, none is credited and the emit says so.
- `--mesh-reach` — FILE, with --population: write this arm's mesh reach (the placed references whose model a population folder provides, and how many of them it wins with and changes) and stop before any pair work; the scope stage runs it on the combined arm and the plan's placedMeshes trigger reads it.
- `--motion` — swept: a base refused for its pose alone (unsupported_pose, or partial_geometry when every visible shape decoded and only the pose is refused) whose motion the files settle is discovered by a swept proxy (its sampled poses' boxes grown by their certified delta), an outer bound labelled in the geometry reason; default off, output unchanged.
- `--motion-delta` — With --motion swept, the target bound (u) on motion between samples (default 0.5).
- `--order` — Evaluate this candidate configuration (CandidateFile) instead of the profile's order.
- `--pair-cache-read` — FILE (repeatable): placed-pair results an earlier run of this same build wrote, keyed by every argument of the query; a file of another build or pair time limit is not read, and the run says so. Results do not depend on it.
- `--pair-cache-write` — FILE: write the placed-pair results this run measured (gzip JSON lines; time-outs and failures are never kept), for a later run's --pair-cache-read.
- `--pair-ledger` — FILE: one JSON line per pair the actual-shape sweep measured, in sweep order (both references, same or cross, the pair-frame path, every result field, the pair's seconds), plus FILE.slowest.json; for comparing two builds pair by pair.
- `--pair-seconds` — Time cap per pair in seconds before it is reported unresolved (default 120).
- `--population` — The plan's population file (evalrun writes it): pairs are measured only where a reference is in the population (a delta-plugin reference, its enable child, a reference whose base or model the delta supplies, or one filed in a delta cell); the whole-order index is kept and the emit's population names the file's hash.
- `--population-audit` — With --population, EMIT OUT: for each row of another emit (a whole-order run's), whether it is in this population; the equivalence check reads it.
- `--pose-confirmations` — With --reach-rules, a JSON list of {modelPath, sha256, evidence}: a model whose animation the files cannot settle, matched by path and exact bytes, is read at its stored pose, labelled as confirmed by in-game comparison. An entry may carry locals ({node: {T, R[9] rows, S}}: a confirmed non-stored pose such as a graph start state), a label, and a pose {clip, sweep "loop" or [t0, t1], label}: a confirmed clip read alone from the behaviour graph and swept on those bytes. No entry, no change.
- `--prepared-cache-mib` — N: the pair sweep prepares each candidate's world soup once and keeps it within about N MiB, least recently used first out (default 2048; 0 prepares every pair afresh). Results do not depend on it.
- `--primitive-solidity` — With --reach-rules primitive, a JSON list of measured primitive verdicts {kind (base or baseType), value, layer (editor id or none), verdict (solid or not_solid), stops, evidence}: a Box or PortalBox primitive the default rule refuses is decided by the matching (base, layer) entry; an unknown kind or verdict, or an entry without evidence, refuses the list.
- `--profile` — MO2 profile name (else the harness config, only for its own instance).
- `--reach` — Every enabled record without a render box gains a reach object: a world box guaranteed to hold its geometry (stored pose, swept region, or a switch node's every child), uncertified with labels and per-part boxes where a wind-swayed shape or the particle model enters, refused with the reason otherwise, no_geometry when it has neither model nor primitive. Default off: the manifest is unchanged.
- `--reach-rules` — With --reach, physics,graph,switch,book,skin,particles,primitive,rest,clipnames,transfer,blend,missingseq,path,emitter,gravity,absent,approx: modelling rules and readers that turn reach refusals into labelled regions, never certified. physics: a body pinned by a constraint to a massless body swings within the file's limits; a free body is dropped straight down, rigidly, to first contact with a supporting body of a reference enabled in the state (COLL CollidesWith; not trigger or sensor layers; a Static base held, labelled as measured on one model in the lab; one load-settle rule: an object drops only if the engine settles it at cell load, otherwise it rests where placed until disturbed, the exemptions read as data from --settle-exemptions, default load-settle-exemptions.json beside the exe); rows gain drop {distance, placed, landed, supportId}. graph: binding member paths decide, reference-pose generators and missing sequences with no transform source are read. switch: animated switch children swept, Tree_Anim labelled. book: BOOK bases and Statics a BOOK names as InventoryArt read closed (stored pose). skin: a skin whose two weight sources (NiSkinData, the skin partition) disagree only on vertices no triangle draws is read; one that disagrees on drawn vertices is posed under both sources, labelled (renderer's source untested); a drawn vertex with no weight under one source still refuses. particles: emitter and modifier-active controller blocks do not make an emitter move (only transform blocks do); the particle extent is read per axis from the file's modifiers (particle_model_v2: speed and lifetime with variations; planar gravity along its axis in the gravity object's frame, World Aligned holding both readings; spherical forces, bombs and any collider on every axis; drag read per second along its axis, the weakest reading; the largest particle size from the scale, LOD and grow-fade multipliers with a turned quad's corner), labelled, never certified; an unknown modifier or force type refuses by name. primitive: a primitive on a model-less activator reads no_geometry whatever its stated layer (not solid, measured in the lab: stations T and AD); other bases keep the refusal. rest (needs physics): a settling body with no support below is an outcome, drop lost (no_geometry; enabled after load, the placed box with both readings; measured in the lab: the engine keeps it falling and returns it to its placed height every few seconds), an exempt body records its gap to the support below (rest-gap, not applied) or floats; a landed body's region is grown by its tilt margin (drop.tiltMargin) and the drop records how far its render mesh reaches below its collision (drop.renderBelowCollision); its drops and supports read every collision shape grown by its Havok radius (convex hulls, boxes, compressed meshes), as the physics drop now does with or without rest; a body that overlaps a support at placement and is pushed off it is refused by name when it settles (it moves on load; where it comes to rest is not read; drop.lowerBound marks the reader's drop as a lower bound only for push causes a lab station measured), a push down through an up-facing surface or a deep near-tie downward push (an impalement) keeps no drop and gives drop.pushDepth / drop.pushUpDepth, an exempt body keeps its region with the same marks on its gap, and a drop that reads longer with the radius than without it is refused with the radius-off drop (drop.shellCore); a render-boxed MoveableStatic with a free body carries its drop; carryable Lights join reachLoose. Adds reachLoose (item-type references, report only). Default off: the manifest is unchanged. Part 3b keys: primitive also reads the measured (base, layer) table from --primitive-solidity, which decides a stated layer or another base: a solid entry reads certified (the box is geometry, measured), a not-solid entry no_geometry; a scaled reference, an unnamed layer or a shape other than Box and PortalBox (the shapes measured) keeps the refusal; clipnames: a behaviour clip path resolves through the character's clip list; blend: a graph blend is bounded by its blend envelope; missingseq: a graph naming a missing sequence holds the stored pose and every sequence in the file; path: a NiPathInterpolator node is bounded by its path's control-polygon hull grown by the subtree radius; emitter: a moving particle emitter is bounded by its swept origins grown by the particle offset box; transfer: a refused graph model takes a stored-pose confirmation from a confirmed model with identical graph files; gravity: settled-gravity observations from --settled-gravity; absent: a model missing from every provider reads no_geometry when --absent-models holds a measurement for it; approx: a measured approximation from --measured-approximations is a labelled part beside the file bound, and the region holds it. Particle extents use particle_model_v3 (gravity x 1.6, turbulence, drag per 1/30 s, controller keys, a support plane under the emitter when supports are known).
- `--reach-shapes` — With --reach, FILE: one JSON line per reach region that holds a particle part or a measured approximation, {formid, region, shapes}: the same region read again with particles set aside (the render shapes alone; particles are runtime state, not a body). The manifest is unchanged.
- `--responsible-pair` — Two plugin names (PLUGIN_A PLUGIN_B, repeatable) that scope the legacy geometry manifest to their pair.
- `--reverse-of` — BASELINE_EMIT, with --base: a reverse run that takes this order's baseline emit's rows, coverage and class instead of sweeping again and re-measures only the --base rows; refused by name unless the baseline emit is this run's own (configuration, population, state, policy, instance, stage-independent coverage); measures the full reverse when the --base rows reach a base record the candidates did not.
- `--scene-cell` — A bounded cell FormKey that joins the scene scope (repeatable).
- `--scene-id` — Scene capture identity; needs --geometry-manifest.
- `--scene-preflight` — Validate the scene setup (MO2 names, folders, which providers the candidate re-orders, and meshes the arm's plugins name that its Mods lack while an installed folder outside them has them) against --order without capturing; with --json, write the verdict (code, reason, what to try).
- `--scene-role` — The role this configuration plays in the scene capture.
- `--scene-scope-from` — Import a bounded scope (seed references or declared cells) from an earlier manifest (repeatable).
- `--scene-space` — Refused: an exterior worldspace is unbounded; use bounded --scene-cell FormKeys.
- `--self-check-declared-cells` — Run the declared-cell presence rule on a fixture and print each cell's outcome; needs no load order.
- `--self-check-imported-accounting` — Run the imported-reference accounting on a fixture and print its receipt; needs no load order.
- `--self-test` — Run the known-answer self-test over the probe's synthetic fixture; exit 0 when every case passes.
- `--settle-exemptions` — With --reach-rules physics, a JSON list of {kind, value, evidence}: the load-settle exemptions (kinds refRecordFlag, bsxFlags with set=/clear= masks and graph=present|absent, enabledAfterLoad). Default: load-settle-exemptions.json beside the exe; missing, the run fails loud.
- `--settled-gravity` — With --reach-rules gravity, a JSON list of {modelPath, sha256, systems (empty = every system), towardWorldNegativeZ: true, evidence}: an in-game observation settling a particle model's gravity frame and sign, matched by path and exact bytes, labelled; only toward world -z is modelled and an entry without evidence refuses the list.
- `--state` — Read references under this runtime enable state arm instead of the winning flag.
- `--surface-tolerance` — Distance (u) at which two strict render surfaces count as touching (default 1e-5).
- `--switch-map` — A switch map file the --state arm resolves through.
- `--threads` — N: the pair sweep's measuring threads (default 8; 1 is the sequential sweep). Results do not depend on it.

<a id="tool-patchprobe"></a>

### patchprobe

**Is a compatibility patch installed or available elsewhere on this machine?**

Check existing local compatibility options before deciding to author a new patch.

Inputs: Target plugin pair and configured installed/download locations.

Outputs: Installed or available compatibility-patch evidence.

Source: [_geometry-harness/patchprobe/Program.cs](../_geometry-harness/patchprobe/Program.cs) — `// PATCH AVAILABILITY — "is there a patch for this anywhere on this machine?"`


Modifiers (do not select a separate mode):

- `--json` — PATH: write the hit list (schema 1, _shared/DiscoveryHits.cs) for --pair and the survey: each plugin with the sha256 of its file, each download matched by name with no hash and the reason; refused with --verify.
- `--list` — Set list for this invocation; consult the linked parser for accepted values.
- `--pair` — Set pair for this invocation; consult the linked parser for accepted values.
- `--roots` — Set roots for this invocation; consult the linked parser for accepted values.
- `--verify` — Set verify for this invocation; consult the linked parser for accepted values.

<a id="tool-patchsearch"></a>

### patchsearch

**How do I record a Nexus patch search for one plugin pair, with every request and response kept, in the shape the review reel and report read?**

**Which archive listings did a patch search not read, and which plugin inside a hub file did it match?**

Before evalreport plan --patch-search: record each mod pair's search, with its known answer, so the reel's patch sentences rest on what was read.

Inputs: A houseCARL server command, the two mods' plugins, Nexus ids and search terms, optionally a known answer and an existing search to merge.

Outputs: patch-search.json (pairs with hits and their archive paths, unreadSources, the known answer, the server build) and every query's request, response and hash.

Source: [integration/housecarl/patchsearch.py](../integration/housecarl/patchsearch.py) — `Record a Nexus patch search for one plugin pair through the houseCARL server`


Modifiers (do not select a separate mode):

- `--known` — The search's own known answer: PLUGIN_A,PLUGIN_B=PATCH.esp=PAGEID.
- `--merge` — An existing patch-search.json whose pairs and known answer are kept.
- `--no-catalog-add` — Do not read the pair's own pages' archive listings first.
- `--out` — The folder the search is written to.
- `--pair` — PLUGIN=MODID=TERMS, once per mod of the pair.
- `--reader-cmd` — The houseCARL server command.

<a id="tool-placeprobe"></a>

### placeprobe

**What placement records does the extractor read from this plugin or cell?**

Inspect raw extraction results before attributing a geometry finding to the detector itself.

Inputs: Plugin path and selected cell.

Outputs: Extracted placement record details.

Source: [_geometry-harness/placeprobe/Program.cs](../_geometry-harness/placeprobe/Program.cs) — `// Milestone-1 proof harness: run PlacementExtract over a REAL plugin and report what came out.`


Modifiers (do not select a separate mode):

- `--cell` — Set cell for this invocation; consult the linked parser for accepted values.
- `--ref` — Set ref for this invocation; consult the linked parser for accepted values.

<a id="tool-planeassess"></a>

### planeassess

**Which occlusion-plane edits does a plane patch make (new, moved or resized, disabled, restored), and over whose version?**

**From which camera stations is a plane the patch changed in view and within reach, and how many plane edits can an offline occlusion render decide?**

Scope an occlusion assessment of a patch that edits occlusion planes before rendering anything: count the gazes to render and name the edits no render can decide.

Inputs: A group definition: the orders without and with the patch, each plugin's file, the patch plugin, the occlusion rule, and per worldspace a route-station file (or the spec to sample one).

Outputs: patch.json: every plane edit with its kind, class and prior owner, reconciled against an expected edit list.; scope.json: per worldspace the eligible and kept gazes, per plane edit the kept gazes that see it, and the ceiling with every other edit named by reason.

Source: [_geometry-harness/planeassess/planeassess.py](../_geometry-harness/planeassess/planeassess.py) — `Occlusion assessment for plane patches: a repeatable driver for any patch that edits occlusion planes.`


<a id="tool-planeassess.answerkey"></a>

### planeassess.answerkey

**How does a predicted occlusion clash list score against an existing patch (hit, open, blind spot)?**

Validate a prediction against an expert patch used as the answer key.

Selector: `answerkey`

Inputs: A group definition, a findings file, p facts folders, the patch step's patch.json and optional evidence.

Outputs: Per shown finding an outcome, and the patch's unexplained plane edits.

Source: [_geometry-harness/planeassess/planeassess.py](../_geometry-harness/planeassess/planeassess.py) — `a = sub.add_parser("answerkey"`


<a id="tool-planeassess.patch"></a>

### planeassess.patch

**Which occlusion-plane edits does this patch make, and over whose version?**

Class a plane patch's edits and reconcile them with an expected edit list before scoping.

Selector: `patch`

Inputs: A group definition.

Outputs: patch.json with every plane edit, its class, prior owner and the reconciliation.

Source: [_geometry-harness/planeassess/planeassess.py](../_geometry-harness/planeassess/planeassess.py) — `a = sub.add_parser("patch"`


<a id="tool-planeassess.predict"></a>

### planeassess.predict

**Which occlusion planes will hide a mod's content when it is added to an order, and which of its planes will hide content already there?**

Predict a mod's occlusion clashes from re-scored views of the order with it, before any patch exists.

Selector: `predict`

Inputs: A group definition, u facts folders, the direction, and optionally facts of the order without the new mod.

Outputs: Findings per plane: shown (inter-mod) or machine record, status, views, pixels and a statement.

Source: [_geometry-harness/planeassess/planeassess.py](../_geometry-harness/planeassess/planeassess.py) — `a = sub.add_parser("predict"`


<a id="tool-planeassess.render"></a>

### planeassess.render

**How do I render a plane-patch group's kept gazes in one arm under the campaign memory caps, one process at a time?**

Render the u or p arm of a plane-patch assessment with lock, PAUSE, free-RAM and cap checks, recording time and peak memory per chunk.

Selector: `render`

Inputs: A group definition, a shots list, the deployed viewprobe and the caps.

Outputs: Planes reports per gaze and ledger.jsonl with wall time, peak commit and peak working set per chunk.

Source: [_geometry-harness/planeassess/planeassess.py](../_geometry-harness/planeassess/planeassess.py) — `a = sub.add_parser("render"`


<a id="tool-planeassess.rescore"></a>

### planeassess.rescore

**Which planes, of any owner, hide which shapes in each rendered view, by owner and 16:9 band pixels?**

Re-score planes reports (new or retained, masks kept or thinned) for every plane before predicting a mod's occlusion clashes.

Selector: `rescore`

Inputs: A group definition, an arm (or another order), and render folders holding planes reports.

Outputs: One facts file per view: every plane's owner, refusal and hidden shapes with in-band pixel ranges.

Source: [_geometry-harness/planeassess/planeassess.py](../_geometry-harness/planeassess/planeassess.py) — `a = sub.add_parser("rescore"`


<a id="tool-planeassess.scope"></a>

### planeassess.scope

**At which station gazes is a plane the patch changed in view and within reach, and which edits can a render decide?**

Count the gazes to render and name the undecidable edits before any render.

Selector: `scope`

Inputs: A group definition and the patch step's patch.json.

Outputs: scope.json with per-worldspace gaze counts, per-edit kept gazes and the ceiling.

Source: [_geometry-harness/planeassess/planeassess.py](../_geometry-harness/planeassess/planeassess.py) — `a = sub.add_parser("scope"`


<a id="tool-planeassess.score"></a>

### planeassess.score

**What does each edited plane hide, by owner, in each rendered view of a plane-patch group?**

Score a render block's reports chunk by chunk, then gzip them and keep masks only where an edited plane hides content.

Selector: `score`

Inputs: A group definition, the arm's shots list and its render output folder.

Outputs: One score file per shot: per edited plane in view, its report entry, hidden px by owner, census, visibility status and rule refusal.

Source: [_geometry-harness/planeassess/planeassess.py](../_geometry-harness/planeassess/planeassess.py) — `a = sub.add_parser("score"`


<a id="tool-planeassess.selftest"></a>

### planeassess.selftest

**Do the frustum test and the occlusion rule's shape classes still give their known answers?**

Run before trusting a scope count after a geometry change.

Selector: `selftest`

Inputs: Nothing.

Outputs: Each known answer and PASS or FAIL.

Source: [_geometry-harness/planeassess/planeassess.py](../_geometry-harness/planeassess/planeassess.py) — `sub.add_parser("selftest"`


<a id="tool-planeassess.shots"></a>

### planeassess.shots

**Which station gazes must be rendered to judge a plane patch's edits?**

List the kept judgeable gazes with their cells before a render block, or a small timed sample.

Selector: `shots`

Inputs: A group definition and the scope step's scope.json.

Outputs: shots.json: label, eye, look, cell and the edited planes in view per gaze.

Source: [_geometry-harness/planeassess/planeassess.py](../_geometry-harness/planeassess/planeassess.py) — `a = sub.add_parser("shots"`


<a id="tool-planeassess.stations"></a>

### planeassess.stations

**Which route camera stations does the deployed viewprobe sample for each worldspace of a plane-patch group?**

Sample stations for a group that has none, or recheck retained stations on the current renderer.

Selector: `stations`

Inputs: A group definition, the deployed viewprobe and the MO2 instance.

Outputs: stations-<worldspace>.json per worldspace and a summary with the renderer hashes.

Source: [_geometry-harness/planeassess/planeassess.py](../_geometry-harness/planeassess/planeassess.py) — `a = sub.add_parser("stations"`


<a id="tool-planeassess.verdicts"></a>

### planeassess.verdicts

**What verdict does each plane edit of a plane patch get from its rendered views?**

Decide every plane record a patch carries from the gazes scored in both arms, after a render block.

Selector: `verdicts`

Inputs: A group definition, patch.json and the u and p score folders.

Outputs: planes.json: per plane record its edits, verdict, statement with its basis, unjudged share, Box label and gazes.

Source: [_geometry-harness/planeassess/planeassess.py](../_geometry-harness/planeassess/planeassess.py) — `a = sub.add_parser("verdicts"`


Modifiers (do not select a separate mode):

- `--arm` — Render the order without the patch (u) or with it (p).
- `--caps` — Process commit, system available floor and graphics used caps in MiB, comma separated.
- `--chunk` — Shots per viewprobe process (default 9).
- `--constrained-chunk` — Shots per viewprobe process in the constrained mode (default 1).
- `--constrained-heap-mib` — Managed heap cap per viewprobe process in the constrained mode (MiB, DOTNET_GCHeapHardLimit).
- `--constrained-need-mib` — Free RAM the constrained mode needs before each shot (MiB).
- `--copy-to` — Folder that receives the compact station outputs and a summary with renderer hashes.
- `--depth-measurement` — Pass viewprobe --depth-measurement (same measurement, no appearance work); off by default, named in the render identity.
- `--direction` — predict: F when the content mod is the one added, R when the plane mod is.
- `--evidence` — answerkey: JSON of plane formKey to evidence text (engine observations); the only route to confirmed or false alarm.
- `--exempt` — A process path that does not make the machine busy (repeat).
- `--facts` — predict: a folder of per-view facts from rescore for the order with the new mod (repeat).
- `--fallback-chunk` — Shots per process after a probe-commit crossing (default 3).
- `--findings` — answerkey: the findings JSON written by predict.
- `--game` — Game folder.
- `--group` — Group definition JSON (kept with the run, not in the tree).
- `--gzip` — rescore: gzip each raw report after its facts are written; masks are kept.
- `--heavy-lock` — Lock file held for the render block; an existing one refuses the run.
- `--include` — Add this station:gazeKind to the shots list (repeat).
- `--instance` — MO2 instance folder, read only.
- `--lock` — A lock file whose presence refuses the run (repeat).
- `--max-hours` — Hold the heavy lock at most this long: the running chunk's process tree is stopped at the limit and no chunk starts after it (default: no limit).
- `--memory-mode` — full, constrained (one shot per process with a capped managed heap) or auto (picked from free memory before each chunk).
- `--need-free-mib` — Free RAM required before each chunk starts.
- `--no-reuse-cell` — Build each shot's scene afresh (viewprobe reuses a cell's scene by default).
- `--occlusion-query` — With --depth-measurement: pass viewprobe --occlusion-query (GPU pixel counts; isolated depth hashed only for shapes with visible pixels); off by default, named in the render identity.
- `--only` — Sample only this worldspace label (repeat).
- `--order` — rescore: the order JSON for an arm other than u and p (for example the order without the plane mod).
- `--out` — Output folder: patch.json is written here and scope reads it from here.
- `--p-facts` — answerkey: a folder of per-view facts for the patched order (repeat).
- `--patch-json` — answerkey: patch.json from the patch step (the patch's edits per plane record).
- `--pause` — PAUSE file: while it exists no new chunk starts.
- `--per-worldspace` — Timed sample: take N kept gazes from each worldspace's busiest cell.
- `--probe-commit-mib` — Re-chunk a worldspace's remaining shots when its first full chunk peaks above this commit (MiB).
- `--render-out` — Folder for reports, worklists and the per-chunk ledger.
- `--renders` — rescore: a render output folder holding planes reports, raw or gzipped (repeat).
- `--scores` — Folder holding the u and p score folders the verdicts step reads.
- `--scores-out` — Folder for one score file per rendered shot.
- `--shots` — The shots.json the shots step wrote.
- `--viewprobe` — The deployed viewprobe.exe to sample stations with (never rebuilt).
- `--without` — predict: facts of the order without the new mod, for its overrides of master planes (repeat).
- `--work` — viewprobe output folder for station sampling; large files stay here.
- `--worldspace` — Limit the shots list to this worldspace label (repeat).

<a id="tool-pluginread"></a>

### pluginread

**Which triangles of other navmeshes does a plugin's version of a navmesh record link to (its edge links)?**

**Which DATA flags (interior, has water, show sky) does a plugin's version of a cell record set?**

**Where does a plugin's version of a placed reference stand (its DATA position and rotation)?**

**Which doors does a plugin's version of a navmesh record link (its door triangles)?**

**What is a record's editor ID in a plugin file (its EDID), for a FormID a value names?**

Decide who made a broken navmesh link (the base game's own version of the linking navmesh, or the mod that carries it), read a cell flag the record imports do not carry, or locate a placed reference whose place is only a persistent cell, from the plugin files a bundle names.

Inputs: A plugin file whose bytes match the bundle's recorded sha256, and the navmesh, cell or placed records' form keys.

Outputs: Per navmesh, its triangle count and edge links; per cell, its DATA flags by name; per placed reference, its position and rotation; recorded in the prediction's pluginReads.

Source: [_geometry-harness/pluginread.py](../_geometry-harness/pluginread.py) — `Read-only reads of record data the record imports do not carry`


<a id="tool-poolprobe"></a>

### poolprobe

**Which downloaded archives contain compatibility patches that were never installed?**

Search the download cache when a compatibility option may be available without another download.

Inputs: Configured download archive pool and target mod identities.

Outputs: Archive-contained patch candidates.

Source: [_geometry-harness/poolprobe/Program.cs](../_geometry-harness/poolprobe/Program.cs) — `// DOWNLOADS NEVER INSTALLED — the patches you already own and have never unpacked.`


Modifiers (do not select a separate mode):

- `--jobs` — Set jobs for this invocation; consult the linked parser for accepted values.
- `--json` — PATH: write the hit list (schema 1, _shared/DiscoveryHits.cs): each patch-shaped plugin in a never-installed archive, one hit per archive version, with the sha256 of the extracted bytes.
- `--list` — Set list for this invocation; consult the linked parser for accepted values.
- `--max` — Set max for this invocation; consult the linked parser for accepted values.

<a id="tool-proofprobe"></a>

### proofprobe

**Do the load-order and geometry rules pass their constructed known-answer cases?**

Qualify detector and precedence-rule changes against synthetic controls with predetermined outcomes.

Inputs: Built-in constructed load-order and geometry fixtures.

Outputs: Known-answer pass/fail results.

Source: [_geometry-harness/proofprobe/Program.cs](../_geometry-harness/proofprobe/Program.cs) — `// SYNTHETIC PROOF for the load-order-integrity probes.`


<a id="tool-questprobe"></a>

### questprobe

**Which Mutagen members expose the quest-to-objective-to-alias dependency graph?**

Inspect the pinned quest model before writing a reader that follows objective and alias links.

Inputs: Pinned Mutagen types.

Outputs: Reflected quest, objective and alias dependency members.

Source: [_geometry-harness/questprobe/Program.cs](../_geometry-harness/questprobe/Program.cs) — `// Reflection probe over Mutagen.Bethesda.Skyrim 0.54.4 — the EXACT version houseCARL pins.`


<a id="tool-readerdoc"></a>

### readerdoc

**How do I turn a presentation plan into a short reader's page in ordinary words?**

Give a modder a one-to-two-page answer (what goes wrong, where, install the patch or not) after evalreport plan has compiled the evaluation.

Inputs: An evalreport presentation plan, optional ordinary mod names, in-game names and the load-order fact.

Outputs: reader.md, reader.html, reader.pdf, summary.txt and reader-manifest.json.

Source: [integration/housecarl/readerdoc.py](../integration/housecarl/readerdoc.py) — `readerdoc - the reader's document: one or two pages in ordinary words, rendered from a presentation plan.`


<a id="tool-recordrows"></a>

### recordrows

**Which record fields does one mod change that another mod's version throws away, from a record capture?**

Turn a record import's request and response transcripts into lost-edit rows a report can attribute.

Inputs: A record import's request and response transcripts and the setup's plugin order.

Outputs: A conflict-file emit of LOST-EDIT rows with coverage, citing the transcripts by sha256.

Source: [_geometry-harness/recordrows.py](../_geometry-harness/recordrows.py) — `recordrows - record-lane rows derived from a record import's own MCP transcripts.`


<a id="tool-reel-plan"></a>

### reel-plan

**Which clashes does the review reel show, as vignettes or data stills, and what does it say about each?**

Plan a review reel by rule from a compiled presentation, the names enrichment, the masters, the skill's vignette data and a recorded patch search.

Inputs: A compiled presentation, its bundle's names and masters, the skill's vignette policy and words, a patch-search file and mod names.

Outputs: The presentation's reel block, or a one-sentence refusal.

Source: [integration/housecarl/reel_plan.py](../integration/housecarl/reel_plan.py) — `The review reel's plan: which clashes it shows, how, and what it says`


<a id="tool-reel-stills"></a>

### reel-stills

**How are the review reel's opening, load-order, unpictured-count and closing stills and a split's label strip drawn, and in which font?**

Draw a reel still page from the plan's page data in a named TrueType font.

Inputs: A still page from the reel plan and the named font files.

Outputs: A 1280x720 image, or a one-sentence refusal when the font is absent.

Source: [integration/housecarl/reel_stills.py](../integration/housecarl/reel_stills.py) — `The review reel's stills: the opening infographic, the load order, the statement of clashes it cannot picture`


<a id="tool-refscan"></a>

### refscan

**Does any record of a plugin file refer to a given object (its FormID through the plugin's masters, compressed records and script properties included)?**

Decide whether a losing mod's own records could enable an object it only starts disabled, before setting such a lost change aside as having no effect.

Inputs: A plugin file whose bytes match the bundle's recorded sha256, and the objects' form keys.

Outputs: Per object, the records that refer to it (type, FormID, subrecord), recorded in the prediction's referenceReads.

Source: [_geometry-harness/refscan.py](../_geometry-harness/refscan.py) — `Read-only scan of one plugin file for records that refer to given objects`


<a id="tool-renderagree"></a>

### renderagree

**Do simultaneous runs of the same render command produce the same output?**

Investigate nondeterminism or resource interference when identical renders execute at the same time.

Inputs: Identical concrete renderer command and concurrency settings.

Outputs: Simultaneous-run outputs and equivalence result.

Source: [_geometry-harness/viewprobe/tools/renderagree.py](../_geometry-harness/viewprobe/tools/renderagree.py) — `Run one viewprobe command SIMULTANEOUSLY n times and check the runs agree.`


Modifiers (do not select a separate mode):

- `--dry-run` — print the memory plan and exit without rendering anything
- `--exe` — Set exe for this invocation; consult the linked parser for accepted values.
- `--gpu-free-mib` — use this instead of asking the machine for free graphics memory
- `--json` — write the whole result here
- `--no-memory-reader` — do not ask the machine at all: the unreadable case, on purpose
- `--per-process-gpu-mib` — graphics memory ONE render of this job needs. Supplied wins over the figure measured from the solo run
- `--per-process-sys-mib` — system memory one render of this job needs
- `--reserve-mib` — memory held back from the plan (default %(default)s)
- `--sys-free-mib` — use this instead of asking the machine for free system memory
- `--trials` — Set trials for this invocation; consult the linked parser for accepted values.
- `--waive` — parts not to compare, by NAME (e.g. reports,masks)
- `--work` — a directory for the outputs; it is rewritten

<a id="tool-renderequiv"></a>

### renderequiv

**Did two renderer runs produce the same frames, masks, reports and other declared artifacts?**

Qualify a renderer change against retained output directories, with any unmeasured artifact class explicitly waived.

Inputs: Two renderer output directories and comparison settings.

Outputs: Artifact-by-artifact equality and explicit missing/incomplete differences.

Source: [_geometry-harness/viewprobe/tools/renderequiv.py](../_geometry-harness/viewprobe/tools/renderequiv.py) — `renderequiv - decide whether two viewprobe runs produced the same answer.`


<a id="tool-renderequiv.compare"></a>

### renderequiv.compare

**Which required artifact classes agree between these two render outputs?**

Check retained render outputs before declaring two builds or execution strategies equivalent.

Selector: `compare`

Inputs: Two renderer output directories and comparison settings.

Outputs: Artifact-by-artifact equality and explicit missing/incomplete differences.

Source: [_geometry-harness/viewprobe/tools/renderequiv.py](../_geometry-harness/viewprobe/tools/renderequiv.py) — `add_parser(`


<a id="tool-renderequiv.selftest"></a>

### renderequiv.selftest

**Does the equivalence comparator reject its deliberately unequal fixture outputs?**

Qualify the comparator's acceptance and refusal behavior before trusting an equivalence result.

Selector: `selftest`

Inputs: Two renderer output directories and comparison settings.

Outputs: Artifact-by-artifact equality and explicit missing/incomplete differences.

Source: [_geometry-harness/viewprobe/tools/renderequiv.py](../_geometry-harness/viewprobe/tools/renderequiv.py) — `add_parser(`


Modifiers (do not select a separate mode):

- `--a-console` — Set a console for this invocation; consult the linked parser for accepted values.
- `--a-exit` — Set a exit for this invocation; consult the linked parser for accepted values.
- `--b-console` — Set b console for this invocation; consult the linked parser for accepted values.
- `--b-exit` — Set b exit for this invocation; consult the linked parser for accepted values.
- `--json` — Set json for this invocation; consult the linked parser for accepted values.
- `--masks` — Set masks for this invocation; consult the linked parser for accepted values.
- `--tmp` — Set tmp for this invocation; consult the linked parser for accepted values.
- `--waive` — comma-separated part names not to require

<a id="tool-renderlimit"></a>

### renderlimit

**How many renders can fit side by side given measured process needs and current memory?**

Choose render concurrency before launching a campaign, using observed process demand and available system capacity.

Inputs: Measured process memory needs and current system/GPU free memory.

Outputs: Allowed concurrency or refusal on absent/insufficient readings.

Source: [_geometry-harness/viewprobe/tools/renderlimit.py](../_geometry-harness/viewprobe/tools/renderlimit.py) — `How many renders may run side by side on this machine, and why that many.`


<a id="tool-rendertwin"></a>

### rendertwin

**Does a shot in a batch equal the same shot rendered in a fresh process?**

Check for state leaking between worklist shots before relying on a batch-render optimization.

Inputs: Worklist and renderer executable under test.

Outputs: Batch versus fresh-process equivalence receipts.

Source: [_geometry-harness/viewprobe/tools/rendertwin.py](../_geometry-harness/viewprobe/tools/rendertwin.py) — `rendertwin - a shot rendered in a BATCH must equal the same shot rendered by a fresh process.`


<a id="tool-rendertwin.fields"></a>

### rendertwin.fields

**Which worklist fields define the per-shot state that must be isolated?**

Audit which per-shot settings must be reset when extending the worklist model.

Selector: `fields`

Inputs: Worklist and renderer executable under test.

Outputs: Batch versus fresh-process equivalence receipts.

Source: [_geometry-harness/viewprobe/tools/rendertwin.py](../_geometry-harness/viewprobe/tools/rendertwin.py) — `add_parser(`


<a id="tool-rendertwin.makelist"></a>

### rendertwin.makelist

**How can I construct controlled shot variations for a batch-isolation test?**

Prepare controlled worklist variations for a batch-isolation experiment.

Selector: `makelist`

Inputs: Worklist and renderer executable under test.

Outputs: Batch versus fresh-process equivalence receipts.

Source: [_geometry-harness/viewprobe/tools/rendertwin.py](../_geometry-harness/viewprobe/tools/rendertwin.py) — `add_parser(`


<a id="tool-rendertwin.selftest"></a>

### rendertwin.selftest

**Does the batch-isolation harness pass its constructed control cases?**

Check the batch comparison harness using controlled fixtures before spending time on real rendering.

Selector: `selftest`

Inputs: Worklist and renderer executable under test.

Outputs: Batch versus fresh-process equivalence receipts.

Source: [_geometry-harness/viewprobe/tools/rendertwin.py](../_geometry-harness/viewprobe/tools/rendertwin.py) — `add_parser(`


Modifiers (do not select a separate mode):

- `--cell` — Set cell for this invocation; consult the linked parser for accepted values.
- `--cell2` — a second cell, for the cross-cell arm
- `--exe` — Set exe for this invocation; consult the linked parser for accepted values.
- `--eye` — X,Y,Z
- `--eye2` — X,Y,Z in the second cell
- `--json` — Set json for this invocation; consult the linked parser for accepted values.
- `--look` — X,Y,Z
- `--nav-on` — the navmesh mode the varying shot uses
- `--no-masks` — do not compare the evidence masks (then `masks` must be waived)
- `--npc` — FormKey of an ACHR or NPC_
- `--occlusion` — on|diff, where the cell has planes
- `--out` — Set out for this invocation; consult the linked parser for accepted values.
- `--prop` — FormKey of a base record with a model
- `--seed` — Set seed for this invocation; consult the linked parser for accepted values.
- `--source` — Set source for this invocation; consult the linked parser for accepted values.
- `--state` — a --state spec, if this setup has one
- `--tmp` — Set tmp for this invocation; consult the linked parser for accepted values.
- `--waive` — parts not to compare, BY NAME
- `--with-route` — add a shot carrying a route and a measured path
- `--work` — a directory for the outputs; it is rewritten
- `--worklist` — Set worklist for this invocation; consult the linked parser for accepted values.

<a id="tool-report-scenes"></a>

### report-scenes

**Which evidence-bound views should an evaluation report ask to capture?**

Select capture requests from bound evaluation evidence before asking a camera planner to acquire them.

Inputs: Evaluation evidence and presentation requirements.

Outputs: Evidence-bound scene/capture requests.

Source: [integration/housecarl/report_scenes.py](../integration/housecarl/report_scenes.py) — `Evidence-bound capture requests; domain selection rules live in the reporting skill.`


<a id="tool-runcontrol"></a>

### runcontrol

**Does the run controller still write the JSON bytes, hashes, orderings and stage ledger its known answers record?**

**What is the package identity digest of a probe's or the controller's build folder, without starting Python?**

**How do I run an evaluation from census to a verified evidence bundle without Python on the machine?**

Evaluate a mod from census to a verified evidence bundle, qualify a change against the known answers, or print the identity an evaluation plan would freeze for a build folder.

Inputs: A selection entry and a machine file for run and arms; a run folder for status; a registry, a candidates folder and a new run folder for plan; an evaluation's RUN folder (--run) for run, resume, status, import-results, finalize and bundle; an import document (--file) for import-results; a bundle folder for verify-bundle; plugin paths for population; ordersweep.py's arguments for ordersweep. Nothing for --self-test (the known answers are built in; Python on PATH adds the live cases with the Python tools that remain). A build folder for identity. A JSON document on standard input for canon. bundle takes, besides --run and --out: --media, --registry, --scope, --search, --reader-cmd, --reader-env NAME=VALUE, --mod-name PLUGIN=NAME and --no-names.

Outputs: For run with --entry: the run folder through the stage named (arms.json, stages.json, status.json, preparation-key.json, disk-forecast.json, census/, prepare/, scope/, plan/, evaluate/, records/, evaluation/, bundle/, names-instance/, runlog/), the same stage lines, exit 0, 2 or 3. For arms and status: one line per arm or stage. For plan, run, resume, import-results, finalize, bundle, verify-bundle, population and ordersweep: the files the evaluation bundle standard names for that command, and its lines. For --self-test: one GREEN, RED or SKIP line per case and a count, exit 0 or 1. A package identity as JSON; the sha256 of a document's two canonical forms.

Source: [_geometry-harness/runcontrol/Program.cs](../_geometry-harness/runcontrol/Program.cs) — `// RUNCONTROL: the evaluation run controller, the one owner of census to bundle.`

Source: [_geometry-harness/runcontrol/SelfTest.cs](../_geometry-harness/runcontrol/SelfTest.cs) — `The known answers: every value here was computed by the Python controller`


<a id="tool-runcontrol.arms"></a>

### runcontrol.arms

**Which arms and load orders does an entry derive?**

Check a new mod's arm set by eye before anything runs.

Selector: `arms`

Inputs: A selection entry and, for a lab entry, the machine file that names the lab.

Outputs: The baseline line and two lines per arm (its evaluated plugins, its mods).

Source: [_geometry-harness/runcontrol/Driver.cs](../_geometry-harness/runcontrol/Driver.cs) — `Prints the arms the entry derives, before anything runs.`


<a id="tool-runcontrol.finalize"></a>

### runcontrol.finalize

**How do I derive an evaluation's disposition and STATUS.md with the C# controller?**

Finalize a run: the disposition from the ledger, never from a claim.

Selector: `finalize`

Inputs: An evaluation's RUN folder (--run).

Outputs: RUN/disposition.json and STATUS.md; exit 0, or 3 with a refusal disposition or an evidence-integrity refusal.

Source: [_geometry-harness/runcontrol/Finalize.cs](../_geometry-harness/runcontrol/Finalize.cs) — `derives disposition.json and STATUS.md from`


<a id="tool-runcontrol.ordersweep"></a>

### runcontrol.ordersweep

**How do I sweep alternative load orders through the swept probes with the C# controller?**

Sweep alternative load orders through the swept probes (the run's swept checks start it as a child).

Selector: `ordersweep`

Inputs: ordersweep.py's arguments: --candidates DIR --out DIR --probes a,b and the rest.

Outputs: The sweep's out folder as ordersweep.py writes it (jobs.jsonl, summary.json, cache.json, logs/, one folder per probe).

Source: [_geometry-harness/runcontrol/OrderSweep.cs](../_geometry-harness/runcontrol/OrderSweep.cs) — `'runcontrol ordersweep': ordersweep.py in C#.`


<a id="tool-runcontrol.plan"></a>

### runcontrol.plan

**How do I derive and freeze an evaluation's check plan?**

Freeze a plan for a registry, a scope and a candidates folder.

Selector: `plan`

Inputs: The check registry, a candidates folder, optionally a measured scope and the instance, profile and game the arms resolve in.

Outputs: RUN/plan.json and plan.sha256, population.json when the scoped checks can be scoped, inputs/primitive-solidity.json when a table was given, an empty ledger.json and journal.jsonl, in the bundle standard's schemas; the plan's lines; exit 0, or 2 with one REFUSED sentence.

Source: [_geometry-harness/runcontrol/Plan.cs](../_geometry-harness/runcontrol/Plan.cs) — `'plan': derives the check plan from the registry and a measured scope and freezes it in a new run`


<a id="tool-runcontrol.population"></a>

### runcontrol.population

**How do I write the population file for one patch plugin and its mod folder?**

Scope a re-check of one patch the way an evaluation scopes its probes.

Selector: `population`

Inputs: One or more --plugin PATH, optionally --mod NAME (with --instance DIR), --target NAME and --enable-population, and --out FILE.

Outputs: The population file at --out and one line naming its sha256 and identity; exit 0, or 2 with one sentence when the delta cannot be scoped.

Source: [_geometry-harness/runcontrol/Population.cs](../_geometry-harness/runcontrol/Population.cs) — `the population file for an ad-hoc delta`


<a id="tool-runcontrol.resume"></a>

### runcontrol.resume

**How do I resume an evaluation with the C# controller, reusing what still matches the disk?**

Continue a run that stopped at its budget or was interrupted, or re-check a finished one: every completed entry is re-checked against the disk and reused, invalidated or refused reuse, then what is open is measured.

Selector: `resume`

Inputs: The RUN folder of a plan this program froze (--run), optionally --budget-minutes N and --rebuild-ledger (the ledger rebuilt from journal.jsonl and the receipts).

Outputs: The same files as run --run, with the line `resume: N receipt(s) reused, N invalidated, N refused-reuse`; exit 0, or 2 with one REFUSED sentence.

Source: [_geometry-harness/runcontrol/EvaluateRun.cs](../_geometry-harness/runcontrol/EvaluateRun.cs) — `the plan's checks executed in`


<a id="tool-runcontrol.run"></a>

### runcontrol.run

**How do I run one mod's evaluation from census to its verified evidence bundle?**

**How do I run a frozen plan's checks?**

With --entry: take a run folder through the bundle (evalpipeline.py continues it from there). With --run RUN [--budget-minutes N]: run a plan this program froze, which the pipeline's evaluate stage starts as a child.

Selector: `run`

Inputs: A selection entry (lab or in-place) and a machine file; or, with --run, the RUN folder of a plan this program froze.

Outputs: With --entry: the run folder through the stage named; RUN/stages.json binds each stage, and evalpipeline.py reuses its accepted stages. With --run: RUN/ledger.json, journal.jsonl, receipts/, outputs/ and logs/, the ledger table on standard output, exit 0, or 2 with one REFUSED sentence.

Source: [_geometry-harness/runcontrol/Driver.cs](../_geometry-harness/runcontrol/Driver.cs) — `The pipeline driver: every stage not yet accepted, in order`


<a id="tool-runcontrol.status"></a>

### runcontrol.status

**Which stages does a run folder's ledger hold as accepted?**

With --out: read a run folder's stage states. With --run: print an evaluation's ledger as a table.

Selector: `status`

Inputs: A run folder; or, with --run, an evaluation's RUN folder.

Outputs: One line per stage (accepted with its time, not accepted with the reason, or not run), then status.json on one line.

Source: [_geometry-harness/runcontrol/Driver.cs](../_geometry-harness/runcontrol/Driver.cs) — `Prints each stage's state from the run's ledger, then the run's status.`


<a id="tool-runcontrol.verify-bundle"></a>

### runcontrol.verify-bundle

**How do I verify an evidence bundle from its own files with the C# controller?**

Check a bundle from its own files (the workbench does, before it opens one).

Selector: `verify-bundle`

Inputs: A bundle folder, optionally --out FILE so the folder is only read.

Outputs: verify.json in the bundle (or at --out); exit 0 when the bundle is complete, else 3.

Source: [_geometry-harness/runcontrol/VerifyBundle.cs](../_geometry-harness/runcontrol/VerifyBundle.cs) — `'runcontrol verify-bundle DIR [--out FILE]' (evalrun.py's cmd_verify_bundle): structural validity, then`


Modifiers (do not select a separate mode):

- `--alt` — plan: evaluate only these candidate files (repeatable)
- `--budget-minutes` — plan: the wall-time limit each run or resume of this plan gets (0 or absent: none); run --run and resume: this invocation's limit in place of the plan's
- `--candidates` — plan: the folder of candidate-*.json files, exactly one of them the baseline
- `--depth` — plan: standard (the load-time state only) or deep (every switched state each arm's census yields); default the saved settings, else deep
- `--diff-tolerance` — plan: the tolerance conflictdiff gets; frozen into the plan
- `--entry` — the mod's selection entry (JSON), for run and arms
- `--exclude` — plan: CHECK=reason (repeatable); the reason is not optional
- `--game` — plan: the game folder, whose Data is a layer the probes always walk
- `--harness` — plan: the folder the probe builds are under (default: this checkout's _geometry-harness)
- `--instance` — plan: the MO2 instance the arms resolve in; population: the instance its --mod folders are in
- `--machine` — the machine file: lab, game, heavy-stage prefix, lock folder, disk figures
- `--max-candidates` — plan: hold back the candidate arms after this many; a held-back arm stays in the plan as skipped-budget
- `--mesh-reach` — plan: the combined arm's mesh reach (scope/mesh-reach.json); the placedMeshes trigger reads it, and the plan refuses a reach about other folders than its population credits.
- `--no-reverse` — plan: no reverse comparison of the swept probes
- `--opt-in` — plan: plan an opt-in check and make it required for this run (repeatable)
- `--out` — the run folder, for run and status (arms takes it and reads nothing from it); for plan, the new evaluation folder; for bundle, the bundle folder (default RUN/bundle); for verify-bundle and population, the file to write
- `--population-from` — plan: use another plan's population.json byte for byte; refused unless it names every plugin and mod folder this plan's own population needs
- `--primitive-solidity` — plan: station Q's measured primitive table for stateprobe (else HOUSECARL_PRIMITIVE_SOLIDITY, else primitiveSolidity in the harness config); the plan freezes a copy
- `--probe-args` — plan: extra arguments every probe gets, split on spaces; frozen into the plan
- `--probe-cmd` — plan: NAME=<argv prefix>, a program standing in for a probe (fixtures); frozen into the plan (repeatable)
- `--profile` — plan: the profile whose mod folders rank the plugin copies outside an arm's view
- `--registry` — plan: the check registry; bundle: the registry file the plan froze, when it has moved
- `--reuse-preparation-from` — run, in-place entries: take the census, prepare and scope stages over from an earlier run under this Work Folder when every input they were built from is the same; otherwise run them
- `--review-arm` — plan: the arm under review; checks that run on one arm only run on it (default the baseline)
- `--scope` — plan: the scope the plan is frozen over; bundle: the scope file the plan froze, when it has moved
- `--select` — plan: CHECK,... or an adapter exe name; narrows what runs, never what is planned (repeatable)
- `--self-test` — Run every known answer; exit 0 when all pass. The live cases with the Python tools that remain are skipped, visibly, when python is not on PATH.
- `--settings` — plan: the evaluation settings file (default: HOUSECARL_EVAL_SETTINGS, then the data folder)
- `--states` — plan: name the states by hand (baseline[,NAME...]); overrides the depth
- `--supplied` — plan: PLUGIN[,PLUGIN...]=candidate-NN.json, an ESP-only supplied-combination arm (repeatable)
- `--through` — run: stop after this stage; census to bundle run here, report-plan and later are refused

<a id="tool-scopeprobe"></a>

### scopeprobe

**What does the target plugin contain, and which measured scope should drive evaluation?**

Establish the target population before choosing checks for a mod evaluation.

Inputs: Target plugin and configured load order.

Outputs: Content counts and measured evaluation scope.

Source: [_geometry-harness/scopeprobe/Program.cs](../_geometry-harness/scopeprobe/Program.cs) — `// SCOPEPROBE — what a target plugin actually CONTAINS, measured, so a checklist is never guessed (Q42, roadmap 4.23).`


Modifiers (do not select a separate mode):

- `--also-scan` — Read a plugin file outside the configuration (a disabled mod's) for CELL records in the footprint or its ring, without loading it; repeatable. Results under nearby.alsoScanned.
- `--assets` — Set assets for this invocation; consult the linked parser for accepted values.
- `--disable` — Set disable for this invocation; consult the linked parser for accepted values.
- `--enable` — Set enable for this invocation; consult the linked parser for accepted values.
- `--game` — Set game for this invocation; consult the linked parser for accepted values.
- `--instance` — Set instance for this invocation; consult the linked parser for accepted values.
- `--json` — Set json for this invocation; consult the linked parser for accepted values.
- `--order` — Set order for this invocation; consult the linked parser for accepted values.
- `--profile` — Set profile for this invocation; consult the linked parser for accepted values.
- `--ring` — Also name the plugins whose CELL records sit within N grid cells (0 to 5) of an exterior footprint cell, under nearby.plugins.
- `--self-test` — Run the known-answer self-test over the probe's synthetic fixture; exit 0 when every case passes.
- `--target` — Set target for this invocation; consult the linked parser for accepted values.

<a id="tool-scripts-runlog.py"></a>

### scripts/runlog.py

**How do SkyWright's Python tools reach houseCARL's run-log adapter?**

Import it as runlog from a SkyWright tool; it loads the pinned submodule's module so the run-event contract keeps one owner.

Inputs: The checked-out houseCARL submodule.

Outputs: The real runlog module's names.

Source: [scripts/runlog.py](../scripts/runlog.py) — `houseCARL's run log, loaded from the pinned submodule`


<a id="tool-seamprobe"></a>

### seamprobe

**Do adjacent cells disagree at their landscape height or texture boundary?**

Investigate visible steps or paint discontinuities where adjacent landscape cells meet.

Inputs: Adjacent winning landscape records.

Outputs: Height and texture boundary differences.

Source: [_geometry-harness/seamprobe/Program.cs](../_geometry-harness/seamprobe/Program.cs) — `// LANDSCAPE SEAMS — mismatched heights and textures where two cells meet`


Modifiers (do not select a separate mode):

- `--base` — Set base for this invocation; consult the linked parser for accepted values.
- `--disable` — Set disable for this invocation; consult the linked parser for accepted values.
- `--emit` — Set emit for this invocation; consult the linked parser for accepted values.
- `--enable` — Set enable for this invocation; consult the linked parser for accepted values.
- `--eps` — Set eps for this invocation; consult the linked parser for accepted values.
- `--game` — Set game for this invocation; consult the linked parser for accepted values.
- `--instance` — Set instance for this invocation; consult the linked parser for accepted values.
- `--json` — Set json for this invocation; consult the linked parser for accepted values.
- `--labelled` — Set labelled for this invocation; consult the linked parser for accepted values.
- `--list` — Set list for this invocation; consult the linked parser for accepted values.
- `--near` — Set near for this invocation; consult the linked parser for accepted values.
- `--order` — Set order for this invocation; consult the linked parser for accepted values.
- `--profile` — Set profile for this invocation; consult the linked parser for accepted values.
- `--self-test` — Run the known-answer self-test over the probe's synthetic fixture; exit 0 when every case passes.
- `--worldspace` — Set worldspace for this invocation; consult the linked parser for accepted values.

<a id="tool-selectprobe"></a>

### selectprobe

**Which installed plugins should one new mod be evaluated against, and why is each other plugin left out?**

Before evaluating a mod a user wants to add to a working order: pick the installed plugins it shares records, cells, neighbouring cells, asset paths or placed bases with.

Inputs: A newcomer (a mod folder or plugin files) and an installed order (an MO2 profile read by path, or a --order candidate file).

Outputs: selection.json: candidates with evidence per kind and rank (v2: also form links, features, score and the control rank R-0), the left-out plugins with a reason each, dependencies, plugins leaving with the newcomer, packaged patches, asset-only folders, what was not measured, and the cost.

Source: [_geometry-harness/selectprobe/Program.cs](../_geometry-harness/selectprobe/Program.cs) — `// SELECTPROBE — which installed plugins one new mod should be evaluated against (roadmap 4.26, part 1).`

Source: [_geometry-harness/_shared/NewcomerSelection.cs](../_geometry-harness/_shared/NewcomerSelection.cs) — `// NEWCOMER SELECTION — which installed plugins one new mod should be evaluated against (roadmap 4.26, part 1).`


<a id="tool-selectprobe.batch"></a>

### selectprobe.batch

**How do I select many newcomers against one order while reading the order only once?**

Measure selection over many labelled newcomers; results resume per newcomer and stop at a newcomer boundary on a stop file.

Selector: `--batch`

Inputs: JSON lines {id, newcomer: [paths]} and the order flags.

Outputs: One <id>.json per line and batch-summary.json with time, bytes read and peak memory.

Source: [_geometry-harness/selectprobe/Program.cs](../_geometry-harness/selectprobe/Program.cs) — `--batch`


<a id="tool-selectprobe.selftest"></a>

### selectprobe.selftest

**Does newcomer selection give its hand-derived answers on a synthetic instance?**

After any change to the selection rule or the newcomer and order handling.

Selector: `--self-test`

Inputs: None: Mutagen-written plugins in a temporary folder.

Outputs: PASS/FAIL per check; exit 1 on any failure.

Source: [_geometry-harness/selectprobe/SelfTest.cs](../_geometry-harness/selectprobe/SelfTest.cs) — `// SELF-TEST BY CONSTRUCTION`


Modifiers (do not select a separate mode):

- `--game` — Set game for this invocation; consult the linked parser for accepted values.
- `--instance` — Set instance for this invocation; consult the linked parser for accepted values.
- `--newcomer` — A mod folder or a plugin file; repeat for several files of one newcomer, or for further folders of one mod (assets only).
- `--no-reverse-links` — Do not read installed plugins' form links; the noted reverse-link count becomes null.
- `--order` — Set order for this invocation; consult the linked parser for accepted values.
- `--out` — Set out for this invocation; consult the linked parser for accepted values.
- `--profile` — Set profile for this invocation; consult the linked parser for accepted values.
- `--rule` — v1, v2 (default) or both: v1 is the part-1 rule with its output unchanged; v2 adds form links as a selecting kind, per-candidate features, the frozen R-A order and the newcomer-blind control rank R-0; both writes <id>.v1.json and <id>.v2.json from one order index.
- `--self-test` — Run the synthetic known answers; exit 0 when every check passes.
- `--selftest` — The same as --self-test.
- `--stop-file` — Batch only, repeatable: stop before the next newcomer when any named file exists (exit 3); a rerun resumes.

<a id="tool-shotplan"></a>

### shotplan

**Where can I put a camera that verifiably sees these subjects?**

Establish framing, visibility, and clearance from measured geometry before committing to a camera pose.

Inputs: Cell, subjects, target order and profile/camera constraints.

Outputs: Verified candidate camera/worklist or explicit refusal.

Source: [_geometry-harness/viewprobe/tools/shotplan.py](../_geometry-harness/viewprobe/tools/shotplan.py) — `shotplan -- choose a camera from the records, instead of guessing one.`


<a id="tool-shotplan.check"></a>

### shotplan.check

**Does this existing camera satisfy the subject visibility contract?**

Qualify a supplied eye before treating it as a reliable capture control.

Selector: `check`

Inputs: Cell, explicit eye and subject definitions.

Outputs: Camera checks and explicit refusal reasons.

Source: [_geometry-harness/viewprobe/tools/shotplan.py](../_geometry-harness/viewprobe/tools/shotplan.py) — `add_parser("check"`


<a id="tool-shotplan.devbench"></a>

### shotplan.devbench

**How do these worklist shots map to DevBench camera arguments?**

Reproduce a verified pose through the live camera endpoint.

Selector: `devbench`

Inputs: Worklist, optional shot selection and endpoint port.

Outputs: DevBench camera arguments.

Source: [_geometry-harness/viewprobe/tools/shotplan.py](../_geometry-harness/viewprobe/tools/shotplan.py) — `add_parser("devbench"`


<a id="tool-shotplan.fallback"></a>

### shotplan.fallback

**What does the geometry around a subject look like when every eye-level camera is refused?**

Inspect an object the camera planner cannot reach at eye level, such as one stranded below rebuilt floors, from a labelled sliced overhead view.

Selector: `--fallback-cut --fallback-extent`

Inputs: The plan's own subjects and refusal; optional cut height and minimum frame width.

Outputs: A refused plan whose worklist carries one labelled top-down fallback shot, its refusal record and its render flags.

Source: [_geometry-harness/viewprobe/tools/shotplan.py](../_geometry-harness/viewprobe/tools/shotplan.py) — `the top-down fallback: what a refused plan leaves behind`


<a id="tool-shotplan.frame"></a>

### shotplan.frame

**Which resolution and field of view does this game profile use?**

Resolve game INI precedence when the expected framing differs from the rendered crop.

Selector: `frame`

Inputs: MO2 instance/profile and game/INI paths.

Outputs: Resolution, effective FOV and optional explanation.

Source: [_geometry-harness/viewprobe/tools/shotplan.py](../_geometry-harness/viewprobe/tools/shotplan.py) — `add_parser("frame"`


<a id="tool-shotplan.nodepth"></a>

### shotplan.nodepth

**How can I frame a subject intentionally excluded from depth testing?**

Frame a declared target whose geometry intentionally contributes no depth, retaining that omission in the result.

Selector: `--subject-nodepth`

Inputs: Subject point and label; disclosure remains in the result.

Outputs: Framing checks with that subject’s depth test omitted.

Source: [_geometry-harness/viewprobe/tools/shotplan.py](../_geometry-harness/viewprobe/tools/shotplan.py) — `--subject-nodepth`


<a id="tool-shotplan.plan"></a>

### shotplan.plan

**Where can a verified camera see all these subjects?**

Find a usable pose when the required subjects are known but the eye position is not.

Selector: `plan`

Inputs: Cell, subjects and projection/placement constraints.

Outputs: Ranked candidates and optional verified worklist.

Source: [_geometry-harness/viewprobe/tools/shotplan.py](../_geometry-harness/viewprobe/tools/shotplan.py) — `add_parser("plan"`


<a id="tool-shotplan.profile"></a>

### shotplan.profile

**How can camera planning use this profile’s actual projection?**

Carry the selected game's INI settings into candidate planning or camera validation.

Selector: `--from-profile`

Inputs: Profile/game/INI locations.

Outputs: Effective aspect and FOV used by planning.

Source: [_geometry-harness/viewprobe/tools/shotplan.py](../_geometry-harness/viewprobe/tools/shotplan.py) — `--from-profile`


<a id="tool-shotplan.surface"></a>

### shotplan.surface

**Which camera sees this measured surface target without occlusion?**

Target a measured point on geometry and require a depth witness for its visibility.

Selector: `--surface`

Inputs: Surface coordinates and geometric verification settings.

Outputs: Qualified view or visibility refusal.

Source: [_geometry-harness/viewprobe/tools/shotplan.py](../_geometry-harness/viewprobe/tools/shotplan.py) — `--surface`


<a id="tool-shotplan.top-down-only"></a>

### shotplan.top-down-only

**What is the labelled sliced top-down of these subjects, without an eye-level search?**

Ask for the top-down directly when the caller already knows no eye-level camera can be had (the capture fallbacks).

Selector: `--top-down-only`

Inputs: Subjects, the floor z and the reason to record as the stage.

Outputs: The same fallback shot, refusal record and exit 1 a refused search leaves.

Source: [_geometry-harness/viewprobe/tools/shotplan.py](../_geometry-harness/viewprobe/tools/shotplan.py) — `--top-down-only: the caller already knows`


Modifiers (do not select a separate mode):

- `--aim` — aim here instead of the centroid
- `--aim-height` — height above a subject's feet that the camera aims at (default 100, a standing head). Aim at the MIDDLE of what should be centred -- a seated actor centred wants roughly half this, or it frames low
- `--allow-unreadable-assets` — plan through viewprobe's exit 3 (an asset found and unreadable): viewprobe is run with the same flag, every such asset is named, and a pose is refused unless each unreadable mesh is placed at least --unreadable-margin from the eye and from every sightline. The shot is labelled INCOMPLETE
- `--aspect` — frame aspect W:H (default 1:1, viewprobe's square frame). Set it to the frame you will actually take -- a 16:9 game frame is WIDER horizontally and, at the same --fov, is not the same crop
- `--bearing-step` — angular resolution of the clearance map, degrees (default 5)
- `--bearing-window` — consider only eye bearings (from the subjects' centroid, 0 = +Y, clockwise) within HALF degrees of CENTRE, e.g. the sector a point picker witnessed; default all bearings
- `--clear-reach` — how far --clear-at's axis rays look for real placed geometry around the eye (default 512). It must cover the storey band under the eye, or 'nothing below' is a statement about the reach and reads UNRESOLVED
- `--comfort` — clearance beyond the camera that scores full marks (default 200)
- `--door-radius` — Include drawn doors within this radius of each subject in the worklist for the runner to close first.
- `--dump` — write EVERY candidate and its verdict as JSON -- what you need to ask why a particular position was or was not offered
- `--dump-survivors` — with --dump, dump only the nav-probed survivors instead of everything
- `--edge-margin` — degrees of each probe frustum left unsampled at the edges (default 2)
- `--elevations` — elevations sampled per bearing, degrees (default -4,0,4)
- `--emit-refused` — write the worklist even when the final check REFUSED the camera (it is labelled REFUSED in the file). Without this a refused camera writes no worklist at all
- `--explain` — print how the angle is derived and measured
- `--eye` — the camera to judge
- `--eye-height` — camera height above the floor (default 120, viewprobe's own default)
- `--fill` — how much of the half-frame the subjects should reach (default 0.55)
- `--floor-z` — the storey the subjects stand on (default: the lowest subject z)
- `--fov` — the DELIVERABLE camera's VERTICAL fov (default 65, viewprobe's own)
- `--fov-map` — fov of the CLEARANCE probe renders (default 100; four of them tile 360 deg)
- `--fov-setting` — the game's fDefaultWorldFOV, when no ini sets it and the runtime's own default is not the measured SE one (see FOV_ENGINE_DEFAULT)
- `--frame-pad` — fraction of the half-frame kept clear at the edges (default 0.12)
- `--game` — game dir, for --from-profile
- `--inside-back` — an eye is INSIDE a solid when the nearest face on at least this many of the six axis rays, across every enclosing ref, is a back face (default 2; any thickness)
- `--inside-cap` — FALLBACK when a nominating ref's mesh cannot be read: its box refuses the eye when its largest side is at most this, and is taken as a room shell above it (default 1024)
- `--inside-rays` — how many of the six axis rays must hit within --inside-reach (default 3)
- `--inside-reach` — an eye whose nominating ref (the one whose OBND box encloses it) has geometry within this distance on --inside-rays of the six world axes is ENCLOSED and refused (default 96: a shelf above, a shelf below, a back panel; a room's walls are further)
- `--instance` — MO2 instance dir
- `--json` — write the verdict, its pose id and its per-target verification as JSON
- `--label` — worklist label
- `--map-yaws` — the probe yaws that tile the circle (default 0,90,180,270)
- `--my-games` — Documents\My Games\<game> dir, when LocalSettings is off
- `--nav-probe-max` — cap on batched --nav-at/--inside-at probe points (two flags per point since the inside test; 250 keeps the command line under Windows's limit); anything dropped is REPORTED
- `--no-bind-scene` — do not ask the renderer for its configuration identity on the verification render. The pose is then bound to the caller's DECLARED inputs only, and the artifact says so
- `--no-fallback` — when every eye-level camera is refused, write NO top-down fallback shot: the refusal artifacts exactly as before the fallback existed
- `--no-verify` — skip the final check. The result is then a PROPOSAL with its verification MISSING: status `unverified`, exit code 2, and the worklist label carries UNVERIFIED. It never means the check passed
- `--offmesh-tol` — how far off navmesh a camera point may sit and still count as inside the room (default 160 -- navmesh is cut around furniture)
- `--out` — where probe PNGs go
- `--port` — DevBench port for --subject-from-game (default: DEVBENCH_PORT, then the port in the file DEVBENCH_RUNTIME_JSON names, then 8920)
- `--profile` — MO2 profile name
- `--quiet` — do not echo each invocation
- `--refs-radius` — Set refs radius for this invocation; consult the linked parser for accepted values.
- `--shot` — just this shot (1-based)
- `--show-refs` — print what is around the camera
- `--sight-height` — height above a subject's feet that the SIGHTLINE probes aim at (default 100). Keep this at head height even when --aim-height is lower: a ray fired at a seated actor's middle hits the chair, and reports the chair as an obstruction
- `--sight-margin` — how much further than the subject the first geometry must be (default 24)
- `--size` — probe render size (default 320)
- `--standoff-max` — Set standoff max for this invocation; consult the linked parser for accepted values.
- `--standoff-min` — Set standoff min for this invocation; consult the linked parser for accepted values.
- `--standoff-step` — Set standoff step for this invocation; consult the linked parser for accepted values.
- `--storey-tol` — how far a walkable level may sit from the floor and still be it (default 96)
- `--subject` — a thing that must be in frame, at its FEET. Repeatable. Feed RUNTIME positions (DevBench inspect kind=refs) when the game is up -- an actor is placed at its AI package anchor, not at its placement record
- `--subject-from-game` — a subject whose position is read off the RUNNING game (DevBench inspect kind=refs): a runtime 0x........ id or a FormKey XXXXXX:Plugin.esp. Repeatable. REFUSES when no game answers -- a movable item has dropped by the time the cell is loaded, so it is planned from the game, never from its record
- `--subject-height` — how tall a subject is, for the framing test (default 128)
- `--subject-width` — half-width of a subject, for the framing test (default 40)
- `--surface-extent` — half-extent of context kept in frame around each --surface point (default 0: the point alone). This is the framing/context extent; the point itself is what gets verified
- `--surface-max` — how many --surface points are accepted (default 8). Each costs a pick in the verification render and widens the frame they must all fit in
- `--surface-obliquity` — tan of the worst face tilt assumed when converting a lateral sampling miss into a range error (default 1.73 = 60 deg)
- `--surface-tol` — the CONSTANT term of the surface-hit tolerance, in units (default 2). viewprobe prints hit positions and distances as whole units and a declared point is itself rounded; the distance-dependent terms (pixel centre, pose rounding, face slant) are computed -- see surface_tolerance
- `--top` — Set top for this invocation; consult the linked parser for accepted values.
- `--unreadable-margin` — clearance, in units, required between an unreadable mesh's bounding sphere and the eye or any eye-to-target segment (default 128)
- `--verify-candidates` — how far down the ranked list the final check may walk when the top candidate fails (default 3, one render each). Exhausting them is a SAMPLED search, never a proof that no camera exists
- `--verify-fov-max` — widest PROBE frustum the final check may open to reach an off-axis target (default 150). The probe canvas is square and a wide deliverable frame is wider still; widening the probe does NOT move the camera and does not change what the delivered frame contains
- `--vp` — the viewprobe command. Anything that takes viewprobe's args, e.g. a wrapper script. Default: the built bin/Release viewprobe.exe, falling back to `dotnet run` — with the reason printed — when there is none or a source in this repository is newer than it
- `--worklist` — write the chosen shot as a viewprobe worklist

<a id="tool-shotplan-test-fixture"></a>

### shotplan-test-fixture

**How can I answer shotplan's geometry queries from a declared toy scene?**

Exercise camera-planning logic offline with predictable boxes and explicit query responses.

Inputs: Declared toy scene and requested geometry queries.

Outputs: Synthetic viewprobe-compatible query responses for planner tests.

Source: [_geometry-harness/viewprobe/tools/fake_viewprobe.py](../_geometry-harness/viewprobe/tools/fake_viewprobe.py) — `A stand-in for viewprobe that answers shotplan's queries from a declared toy scene.`


<a id="tool-skywright_paths"></a>

### skywright_paths

**Where is SkyWright's checkout, seen from the houseCARL integration module?**

Find SkyWright's Python tools, run log, data or the houseCARL pin from a script or suite inside integration/housecarl/.

Inputs: None: the module file's own location.

Outputs: CHECKOUT, HARNESS, SCRIPTS, DATA and HOUSECARL paths; SkyWright's tools and run log on sys.path.

Source: [integration/housecarl/skywright_paths.py](../integration/housecarl/skywright_paths.py) — `Where SkyWright's checkout is, seen from the houseCARL integration module (integration/housecarl/).`


<a id="tool-stategen"></a>

### stategen

**Which switched world states does a Deep evaluation judge on an arm, and which can it not express?**

List the states a census yields before or after an evaluation, with every unexplored one named.

Inputs: One arm's switch census (switchprobe --json).

Outputs: The states a Deep evaluation judges and the not-explored rows with reasons.

Source: [_geometry-harness/stategen.py](../_geometry-harness/stategen.py) — `stategen — the world states a Deep evaluation judges`


Modifiers (do not select a separate mode):

- `--census` — switchprobe --json output for one arm
- `--out` — the state document (JSON)
- `--specs` — also write one stateprobe --state spec per line

<a id="tool-stateprobe"></a>

### stateprobe

**Which contact or route findings appear or disappear when a world-state switch flips?**

Measure whether an identified enable-parent or quest-stage transition changes a contact or navigation diagnosis.

Inputs: Selected switches/cells, winning geometry and optional switch-map JSON.

Outputs: State contact/route findings; state-overlap remains unmeasured.

Limit: State-overlap is withheld and must not be reported as measured.

Source: [_geometry-harness/stateprobe/Program.cs](../_geometry-harness/stateprobe/Program.cs) — `// STATEPROBE — the state-conditional class`


<a id="tool-stateprobe.all"></a>

### stateprobe.all

**Which findings change across every enumerated switch flip?**

Survey the enumerated transition population before narrowing an investigation to one trigger.

Selector: `--all`

Inputs: Winning order and optional switch map.

Outputs: Per-state contact/route results.

Source: [_geometry-harness/stateprobe/Program.cs](../_geometry-harness/stateprobe/Program.cs) — `--all`


<a id="tool-stateprobe.cell"></a>

### stateprobe.cell

**Which state changes affect this particular contested cell?**

Keep a state investigation focused on the contested location instead of surveying unrelated switches.

Selector: `--cell`

Inputs: Cell editor ID prefix or FormKey.

Outputs: Cell-relevant state results.

Source: [_geometry-harness/stateprobe/Program.cs](../_geometry-harness/stateprobe/Program.cs) — `--cell`


<a id="tool-stateprobe.moved-ref-selftest"></a>

### stateprobe.moved-ref-selftest

**Does state traversal retain the correct moved reference winner?**

Qualify winner traversal after changing how a reference relocated between cells is resolved.

Selector: `--moved-ref-selftest`

Inputs: Built-in synthetic cases.

Outputs: Assertions and exit status.

Source: [_geometry-harness/stateprobe/Program.cs](../_geometry-harness/stateprobe/Program.cs) — `--moved-ref-selftest`


<a id="tool-stateprobe.selected"></a>

### stateprobe.selected

**Which contact and route findings change in the selected state?**

Compare a specific transition once its switch identities or joint stage definition are known.

Selector: `--state`

Inputs: State specification and optional switch map.

Outputs: State contact and route evidence; no state-overlap claim.

Source: [_geometry-harness/stateprobe/Program.cs](../_geometry-harness/stateprobe/Program.cs) — `--state`


Modifiers (do not select a separate mode):

- `--disable` — Set disable for this invocation; consult the linked parser for accepted values.
- `--emit` — Set emit for this invocation; consult the linked parser for accepted values.
- `--empty-space` — --empty-space FILE: LEFT IN EMPTY SPACE for each state's arriving references: a fixture whose assembly (what it touches within 4 u, transitively, and the items its weapon racks mount) reached a floor or wall that a plugin removed or moved, and reaches none in that state under the winner. Interiors only. Rows go to FILE, the class to --emit as STATE-EMPTY @ state. Default off: outputs unchanged.
- `--enable` — Set enable for this invocation; consult the linked parser for accepted values.
- `--game` — Set game for this invocation; consult the linked parser for accepted values.
- `--instance` — Set instance for this invocation; consult the linked parser for accepted values.
- `--json` — Set json for this invocation; consult the linked parser for accepted values.
- `--list` — Set list for this invocation; consult the linked parser for accepted values.
- `--order` — Set order for this invocation; consult the linked parser for accepted values.
- `--phase-timers` — FILE: wall seconds per phase and the state evaluator's own split (setup, route, host leaving, host arriving, dependent arriving) printed and recorded as run features, and one JSON line per state in FILE (its milliseconds, changes, rows and a digest of its rows and tally); with it, the contact code's own split (as contactprobe --phase-timers) printed, recorded and written to FILE.contact.json; for measuring where the time goes. Changes no output.
- `--population` — The plan's population file: a changed reference is processed only when the population reaches it - a population reference or one within contact reach of it; one in an interior population cell or a delta terrain/navmesh cell; one in an exterior cell within 5 cells of a seed's position, a delta terrain/navmesh cell or a reference a delta worldspace lists as large - and per-state empty space reports fixtures of the population's interior cells only. The whole-order index is kept, so every kept row is the whole-order run's; a delta carrying RACE records is refused (every actor band reads it). The emit's population names the file's hash.
- `--population-audit` — With --population, EMIT OUT: for each row of another emit (a whole-order run's), whether this run keeps it and by which rule (contact, route-interior, route-ring, large-ref, empty-space).
- `--primitive-solidity` — FILE: station Q's measured primitive table (the overlapprobe --primitive-solidity format and reader): a changed marker whose reference carries a Box or PortalBox primitive, at scale 1, with no layer or a named one, is decided by its (base, layer) entry: measured not solid is set aside (MarkerPrimitiveNotSolid); measured solid gets its primitive box and is judged as a route obstacle (an invisible collision box; NPC pathing through it is inferred from the player measurement); any other stays MarkerPrimitive. The emit's policy names the table (primitive-table=<sha12>, or none without it) and the box line's note says which; a table entry without evidence refuses the run.
- `--profile` — Set profile for this invocation; consult the linked parser for accepted values.
- `--route` — Set route for this invocation; consult the linked parser for accepted values.
- `--self-test` — Run the known-answer self-test over the probe's synthetic fixture; exit 0 when every case passes.
- `--states-file` — one --state SPEC per line (a Deep evaluation's list); every line is parsed as --state is
- `--switch-map` — Set switch map for this invocation; consult the linked parser for accepted values.
- `--threads` — N: the state loop's and empty-space pass's measuring threads (default 8; 1 is the sequential loop). Results do not depend on it.
- `--write-modelless-activator-fixture` — --write-modelless-activator-fixture DIR: write the modelless activator, door and marker fixture (_shared/ModellessActivatorFixture.cs) that tests/test_modelless_activators_doors.py runs the real stateprobe and contactprobe on; test-only.
- `--write-modelless-light-fixture` — --write-modelless-light-fixture DIR: write the modelless light fixture (_shared/ModellessLightFixture.cs) that tests/test_modelless_light.py runs the real stateprobe and contactprobe on; test-only.
- `--write-row-identity-fixture` — --write-row-identity-fixture DIR: write the row identity fixture (_shared/RowIdentityFixture.cs) that tests/test_state_row_identity.py runs the real probe on; test-only.
- `--write-scope-fixture` — --write-scope-fixture DIR: write the population scope fixture (_shared/ScopeFixture.cs) that tests/test_population_scope.py runs the real probes on; test-only.
- `--write-state-lane-fixture` — --write-state-lane-fixture DIR: write the marker-box and placed-actor fixture (_shared/StateLaneFixture.cs) that tests/test_state_marker_box_and_actors.py runs the real stateprobe on; test-only.

<a id="tool-subject-gate"></a>

### subject-gate

**Does this captured subject satisfy the identity, visibility and normal-view witness requirements?**

Decide whether a rendered finding has enough correctly bound visual evidence to support its reported conclusion.

Inputs: Captured subject identity, depth/visibility evidence and witness contract.

Outputs: Qualification or explicit identity/visibility refusal.

Source: [integration/housecarl/subject_gate.py](../integration/housecarl/subject_gate.py) — `Post-render qualification. Pixel eligibility never substitutes for a normal-view witness.`


Modifiers (do not select a separate mode):

- `--input` — Set input for this invocation; consult the linked parser for accepted values.
- `--output` — Set output for this invocation; consult the linked parser for accepted values.

<a id="tool-switchflip"></a>

### switchflip

**Does flipping a mod's script-driven enable switches crash or stall the game under this load order?**

Run the opt-in live check of an evaluation on a scratch game instance, after writing a flip plan from the switch census and the bound scripts' source.

Inputs: A flip plan, a scratch MO2 instance, its launch shortcut, the game path and a lock file.

Outputs: Per launch a receipt, events and crash logs with a classification; per block the arm readings and an import document for the live check (not imported until evallive.py is ported).

Source: [integration/housecarl/switchflip.py](../integration/housecarl/switchflip.py) — `switchflip — the live driver of the runtime state-switch check`


<a id="tool-switchflip.batch"></a>

### switchflip.batch

**What does the game do when this block's switch flips run under one lock?**

Run a block of live launches on a scratch instance after the idle gate.

Selector: `batch`

Inputs: A flip plan, a scratch instance, its shortcut, the game path and a lock file.

Outputs: One receipt, events, crash logs and a recording per launch, and the block's arm readings.

Source: [integration/housecarl/switchflip.py](../integration/housecarl/switchflip.py) — `add_parser(`


<a id="tool-switchflip.export"></a>

### switchflip.export

**What import document carries one arm's live launches into an evaluation?**

Bind a finished bound block's launches to the evaluation arm they were run for.

Selector: `export`

Inputs: A block folder and an arm id.

Outputs: A schema-1 live import document (not imported until evallive.py is ported to the built-in controller).

Source: [integration/housecarl/switchflip.py](../integration/housecarl/switchflip.py) — `add_parser(`


<a id="tool-switchflip.validate"></a>

### switchflip.validate

**Is this flip plan one the live driver can honour?**

Check a hand-written flip plan before any launch.

Selector: `validate`

Inputs: A flip plan JSON.

Outputs: Valid, or every problem named.

Source: [integration/housecarl/switchflip.py](../integration/housecarl/switchflip.py) — `add_parser(`


Modifiers (do not select a separate mode):

- `--arm` — export: the flip plan's arm whose launches form one import document
- `--block` — export: the block folder batch wrote
- `--condition` — launch (a batch child): the condition id
- `--crash-dir` — where the crash logger writes crash-*.log (default: the user's Documents game SKSE folder)
- `--dry` — batch: print the schedule and the idle verdict; launch nothing
- `--evalrun-plan` — the evaluation plan.json a bound flip plan is launched for
- `--folder` — launch (a batch child): the launch folder
- `--game-exe` — the game executable's full path (always explicit)
- `--idle-json` — launch (a batch child): the idle verdict measured before it
- `--instance` — the MO2 instance folder (a scratch instance)
- `--launch-minutes` — batch: the deadline of one launch
- `--lock` — batch: the lock file held for the whole block, created only when absent
- `--min-free-gb` — batch: the free physical memory floor of the idle gate
- `--out` — batch: the block folder; export: the import document
- `--plan` — the flip plan JSON
- `--plan-sha` — launch (a batch child): the flip plan hash the block froze
- `--port` — the DevBench port
- `--refuse-if` — batch: another lock whose presence refuses (repeatable)
- `--replicate` — launch (a batch child): the replicate number
- `--shortcut` — the instance's launch shortcut (.lnk)

<a id="tool-switchflip_outcome"></a>

### switchflip_outcome

**How is one live switch-flip launch classified, and when does an arm's reading settle?**

Re-derive a live receipt's classification from its recorded events rather than trusting its label.

Inputs: One launch's recorded events, or an arm's per-launch classifications.

Outputs: Crash, stall, complete or not-measured with the reason; finding, no-finding or unsettled.

Source: [integration/housecarl/switchflip_outcome.py](../integration/housecarl/switchflip_outcome.py) — `switchflip_outcome — the live driver's client for the runtime state-switch check's reading rule.`


<a id="tool-switchflip_scope"></a>

### switchflip_scope

**Which switches is the runtime state-switch check about on an arm, and is it not applicable there?**

Decide from an arm's switch census and the scope whether any script-driven switch touches a target.

Inputs: One arm's switch census (switchprobe --json) and the evaluation scope.

Outputs: not-applicable or open with each cause named; the population with how each switch touches a target.

Source: [integration/housecarl/switchflip_scope.py](../integration/housecarl/switchflip_scope.py) — `switchflip_scope — which switches the runtime state-switch check`


<a id="tool-switchprobe"></a>

### switchprobe

**Which enable-parent switches gate this cell, and which bound scripts or quest stages flip them?**

Trace missing or changing geometry from its enable parent to script and quest-stage trigger evidence.

Inputs: Winning order, optional cell/switch filters and trigger-read policy.

Outputs: Switch sets, trigger bindings, joint flips and separate disabled singleton counts.

Limit: Initially disabled singletons are counted separately; the census trigger map does not identify every singleton enabler (--targets reads the enablers of named references). Records only; console, SKSE plugins, reference-walking scripts and runtime-filled aliases are listed, never cleared.

Source: [_geometry-harness/switchprobe/Program.cs](../_geometry-harness/switchprobe/Program.cs) — `// SWITCHPROBE — the world-state switch census and the trigger map`


<a id="tool-switchprobe.census"></a>

### switchprobe.census

**Which enable-parent switches and trigger bindings affect this cell?**

Start with membership and trigger bindings when a location changes after a quest event.

Selector: `--triggers --cell --switch`

Inputs: Cell/switch filters; triggers on or off.

Outputs: On/off sets, trigger status, joint flips and separate singletons.

Source: [_geometry-harness/switchprobe/Program.cs](../_geometry-harness/switchprobe/Program.cs) — `--triggers`


<a id="tool-switchprobe.script"></a>

### switchprobe.script

**What does the script bound to this switch actually do?**

Read the bound implementation when a trigger map cannot establish whether a script actually enables an object.

Selector: `--show-script`

Inputs: Script name resolved through the configured VFS.

Outputs: Decompiled script text or read failure.

Source: [_geometry-harness/switchprobe/Program.cs](../_geometry-harness/switchprobe/Program.cs) — `--show-script`


<a id="tool-switchprobe.targets"></a>

### switchprobe.targets

**Can anything in the load order enable this disabled reference, including a singleton with no enable parent?**

Settle whether a named reference is enabled at load and what, if anything, enables it later, when it is not an enable parent the census maps.

Selector: `--targets`

Inputs: Named references (list or @file); optional fixture plugin; script-scan policy.

Outputs: Per reference: load-time enable state, verdict, bindings and their calls, residual routes listed.

Source: [_geometry-harness/_shared/TargetTriggers.cs](../_geometry-harness/_shared/TargetTriggers.cs) — `TARGET TRIGGERS`


Modifiers (do not select a separate mode):

- `--disable` — Set disable for this invocation; consult the linked parser for accepted values.
- `--enable` — Set enable for this invocation; consult the linked parser for accepted values.
- `--extra-plugin` — Load this plugin file after the order, as a test fixture; repeatable.
- `--game` — Set game for this invocation; consult the linked parser for accepted values.
- `--instance` — Set instance for this invocation; consult the linked parser for accepted values.
- `--json` — Set json for this invocation; consult the linked parser for accepted values.
- `--list` — Set list for this invocation; consult the linked parser for accepted values.
- `--order` — Set order for this invocation; consult the linked parser for accepted values.
- `--pex-scan` — With --targets: on (default) scans every script in the view that calls GetFormFromFile for a target's plugin and id; off skips it.
- `--profile` — Set profile for this invocation; consult the linked parser for accepted values.
- `--self-test` — Run the known-answer self-test over the probe's synthetic fixture; exit 0 when every case passes.
- `--targets-json` — With --targets: write the full reading (verdicts, load-time enable state, bindings, residual routes) to this file.

<a id="tool-toolidentity"></a>

### toolidentity

**What is the identity of the program an evaluation runs: a probe's Release folder, a --probe-cmd override, or the controller's scripts?**

Compare the code an evaluation would run with the code its plan recorded, by content.

Inputs: A Release folder, an override's command text, or a repository root.

Outputs: A digest with the per-file rows that explain a difference; Unreadable for a file that cannot be read.

Source: [_geometry-harness/toolidentity.py](../_geometry-harness/toolidentity.py) — `toolidentity — the one identity rule for the programs an evaluation runs.`


<a id="tool-viewprobe"></a>

### viewprobe

**What does this cell's winning geometry look like, why is a reference not drawn, and what changes under a named state?**

Investigate a contested scene visually, then choose a query or render mode for the suspected visibility, navigation, or state problem.

Inputs: Cell/worklist, target order/assets and camera/query options.

Outputs: Rendered frames, geometry queries and requested evidence; named incomplete/refusal outcomes.

Source: [_geometry-harness/viewprobe/Program.cs](../_geometry-harness/viewprobe/Program.cs) — `// WPF is pulled in by the CharacterViewer.Rendering reference and brings its own Path/Image/Color`


<a id="tool-viewprobe.asset-path-budget"></a>

### viewprobe.asset-path-budget

**Does the asset path length limits diagnostic satisfy its expected contract?**

Exercise boundary-length asset names before accepting a path handling change.

Selector: `--asset-path-budget`

Inputs: Arguments of the linked standalone diagnostic branch; use disposable test destinations.

Outputs: Diagnostic assertions and exit status; fault arms deliberately refuse.

Source: [_geometry-harness/viewprobe/Program.cs](../_geometry-harness/viewprobe/Program.cs) — `--asset-path-budget`


<a id="tool-viewprobe.bos"></a>

### viewprobe.bos

**What does Base Object Swapper change in this scene?**

Inspect a scene under the declared runtime swap configuration and identify references whose bases change.

Selector: `--bos`

Inputs: Mode off, on or diff and the configured swap layer.

Outputs: Selected swap rendering; diff tints swapped references.

Source: [_geometry-harness/viewprobe/Program.cs](../_geometry-harness/viewprobe/Program.cs) — `--bos off | on | diff`


<a id="tool-viewprobe.cell-scene-selftest"></a>

### viewprobe.cell-scene-selftest

**Does the cell scene resource lifetime diagnostic satisfy its expected contract?**

Exercise scene ownership controls after changing disposal or reuse of renderer resources.

Selector: `--cell-scene-selftest`

Inputs: Arguments of the linked standalone diagnostic branch; use disposable test destinations.

Outputs: Diagnostic assertions and exit status; fault arms deliberately refuse.

Source: [_geometry-harness/viewprobe/Program.cs](../_geometry-harness/viewprobe/Program.cs) — `--cell-scene-selftest`


<a id="tool-viewprobe.clear-at"></a>

### viewprobe.clear-at

**What placed geometry surrounds this candidate camera point?**

Test whether an apparently empty candidate eye is in usable room space or outside its shell.

Selector: `--clear-at`

Inputs: World X,Y,Z and reach/bounds settings.

Outputs: Directional distances, floor query and missing-bound coverage; a shape is solid by its own data (effect-shader, APP_CULLED-hidden and lit refraction shapes are not; a shape with no shader is), whatever folder its mesh is in, and refs with no solid shape are counted, not tested.

Source: [_geometry-harness/viewprobe/Program.cs](../_geometry-harness/viewprobe/Program.cs) — `--clear-at`


<a id="tool-viewprobe.compare"></a>

### viewprobe.compare

**How do two plugin navmesh versions differ visually?**

Inspect navigation edits between two plugin versions with their vertex differences visible.

Selector: `--compare`

Inputs: Compared plugin versions and cell.

Outputs: Comparison panels with optional difference markers.

Source: [_geometry-harness/viewprobe/Program.cs](../_geometry-harness/viewprobe/Program.cs) — `--compare`


<a id="tool-viewprobe.config-identity"></a>

### viewprobe.config-identity

**Which effective inputs identify this renderer configuration?**

Bind an evidence comparison to the actual plugin order and winning asset inputs.

Selector: `--config-identity`

Inputs: Configured order and asset view.

Outputs: Configuration identity on stdout.

Source: [_geometry-harness/viewprobe/Program.cs](../_geometry-harness/viewprobe/Program.cs) — `--config-identity`


<a id="tool-viewprobe.contacts-of"></a>

### viewprobe.contacts-of

**What does this placed object touch and rest on, from its render triangles?**

Decide which objects a cutaway may hide, or check a contact before describing it.

Selector: `--contacts-of`

Inputs: A FormKey.

Outputs: The touching refs (gap 8 u or less, crossing included), the supports below it, the unassessed refs near it, and its drawn box.

Source: [_geometry-harness/viewprobe/CaptureViews.cs](../_geometry-harness/viewprobe/CaptureViews.cs) — `--contacts-of FORMKEY what the ref touches`


<a id="tool-viewprobe.coverage-of"></a>

### viewprobe.coverage-of

**Where does a see-through object (effect, blended or alpha-tested) visibly cover this frame?**

Mark an object --ref-surface cannot mark, by its measured drawn coverage rather than its bounds.

Selector: `--coverage-of --coverage-dump`

Inputs: A FormKey, optionally @X,Y,Z to test a point; --coverage-dump keeps the black-clear frame.

Outputs: A state line, the blend, the mask and alpha pictures, coverage picks at --pick pixels and a coverage-at verdict.

Source: [_geometry-harness/viewprobe/CaptureViews.cs](../_geometry-harness/viewprobe/CaptureViews.cs) — `--coverage-of FORMKEY[@X,Y,Z] a see-through ref's drawn coverage`


<a id="tool-viewprobe.cull-above"></a>

### viewprobe.cull-above

**What does a lower storey look like with upper geometry sliced away?**

Inspect lower interior levels when ceilings and upper floors obscure the view.

Selector: `--cull-above`

Inputs: World Z cutoff.

Outputs: Static triangles above the cutoff omitted; spanning walls retained.

Source: [_geometry-harness/viewprobe/Program.cs](../_geometry-harness/viewprobe/Program.cs) — `--cull-above`


<a id="tool-viewprobe.depth-dump"></a>

### viewprobe.depth-dump

**What are the depth values behind this rendered frame?**

Retain the camera's depth buffer for later visibility or position calculations.

Selector: `--depth-dump`

Inputs: Frame settings and depth destination.

Outputs: Depth buffer data for the finished frame.

Source: [_geometry-harness/viewprobe/Program.cs](../_geometry-harness/viewprobe/Program.cs) — `--depth-dump`


<a id="tool-viewprobe.depth-measurement-selftest"></a>

### viewprobe.depth-measurement-selftest

**Do the depth-measurement mode's texture keep rule and mask rule give their known answers?**

Check the depth-measurement rules after changing which textures or masks the mode keeps.

Selector: `--depth-measurement-selftest`

Inputs: No arguments; needs no MO2 instance, game or GL context.

Outputs: One line per known answer and exit status 0 when all hold.

Source: [_geometry-harness/viewprobe/Program.cs](../_geometry-harness/viewprobe/Program.cs) — `--depth-measurement-selftest`


<a id="tool-viewprobe.emit-bats"></a>

### viewprobe.emit-bats

**How can I export these worklist shots as game console batches?**

Reproduce measured worklist poses through the game's console interface.

Selector: `--emit-bats`

Inputs: Worklist and destination name/directory.

Outputs: Console batches; optional profile enable changes are explicit.

Source: [_geometry-harness/viewprobe/Program.cs](../_geometry-harness/viewprobe/Program.cs) — `--emit-bats`


<a id="tool-viewprobe.euler"></a>

### viewprobe.euler

**Which rotation composition produces the expected placed geometry?**

Visually qualify transform ordering when rotated placements appear incorrectly oriented.

Selector: `--euler`

Inputs: Cell and view settings.

Outputs: Frames for candidate Euler compositions.

Source: [_geometry-harness/viewprobe/Program.cs](../_geometry-harness/viewprobe/Program.cs) — `--euler`


<a id="tool-viewprobe.extraction-scratch-fail"></a>

### viewprobe.extraction-scratch-fail

**Does the scratch refusal propagation diagnostic satisfy its expected contract?**

Confirm a refused scratch operation reaches the caller after changing extraction error handling.

Selector: `--extraction-scratch-fail`

Inputs: Arguments of the linked standalone diagnostic branch; use disposable test destinations.

Outputs: Diagnostic assertions and exit status; fault arms deliberately refuse.

Source: [_geometry-harness/viewprobe/Program.cs](../_geometry-harness/viewprobe/Program.cs) — `--extraction-scratch-fail`


<a id="tool-viewprobe.extraction-scratch-sweep"></a>

### viewprobe.extraction-scratch-sweep

**Does the owned extraction scratch cleanup diagnostic satisfy its expected contract?**

Verify cleanup ownership after changing temporary extraction lifecycle code.

Selector: `--extraction-scratch-sweep`

Inputs: Arguments of the linked standalone diagnostic branch; use disposable test destinations.

Outputs: Diagnostic assertions and exit status; fault arms deliberately refuse.

Source: [_geometry-harness/viewprobe/Program.cs](../_geometry-harness/viewprobe/Program.cs) — `--extraction-scratch-sweep`


<a id="tool-viewprobe.find-ltex"></a>

### viewprobe.find-ltex

**Where is this ground texture painted in the worldspace?**

Find affected terrain patches before choosing a location to inspect a landscape material change.

Selector: `--find-ltex`

Inputs: LTEX name substring.

Outputs: Matching texture records and cells using them.

Source: [_geometry-harness/viewprobe/Program.cs](../_geometry-harness/viewprobe/Program.cs) — `--find-ltex`


<a id="tool-viewprobe.grid"></a>

### viewprobe.grid

**How can I read world coordinates from a top-down view?**

Recover scene positions from an overhead inspection image using coordinate labels.

Selector: `--grid`

Inputs: Spacing or auto and storey/view settings.

Outputs: Coordinate grid or refusal for unsuitable eye-level views.

Source: [_geometry-harness/viewprobe/Program.cs](../_geometry-harness/viewprobe/Program.cs) — `--grid`


<a id="tool-viewprobe.hide"></a>

### viewprobe.hide

**How does the view look with the objects standing in front of a clash hidden?**

Render a labelled cutaway of a pair no eye-level camera sees, the placement queries still judging the whole scene.

Selector: `--hide`

Inputs: FormKeys to leave out of every draw of the frame.

Outputs: A frame without them, each printed as hidden, with the cutaway label burned in (report mode too) and in the PNG metadata.

Source: [_geometry-harness/viewprobe/CaptureViews.cs](../_geometry-harness/viewprobe/CaptureViews.cs) — `--hide FORMKEY        the ref is drawn in no pass`


<a id="tool-viewprobe.inside-at"></a>

### viewprobe.inside-at

**Does this camera point sit inside placed physical geometry?**

Reject a camera pose that may be embedded in a wall, shelf, or other solid object.

Selector: `--inside-at`

Inputs: World X,Y,Z and bounds/ray settings.

Outputs: Enclosing candidates and geometry-ray evidence, with which way the nearest face on each ray is turned (back, front, two-sided, or unknown); a shape is solid by its own data (effect-shader, APP_CULLED-hidden and lit refraction shapes are not; a shape with no shader is), whatever folder its mesh is in, and a ref with no solid shape is listed (effect-only, or no solid shape).

Source: [_geometry-harness/viewprobe/Program.cs](../_geometry-harness/viewprobe/Program.cs) — `--inside-at`


<a id="tool-viewprobe.interactive"></a>

### viewprobe.interactive

**How can I inspect this winning scene interactively?**

Explore a scene manually while choosing useful viewpoints for later reproducible captures.

Selector: `--interactive`

Inputs: Cell or worklist and interactive controls.

Outputs: Visible scene, hover identities and optional saved frame/pose.

Source: [_geometry-harness/viewprobe/Program.cs](../_geometry-harness/viewprobe/Program.cs) — `--interactive`


<a id="tool-viewprobe.list-meshes"></a>

### viewprobe.list-meshes

**Which meshes contribute to this winning scene?**

Audit model participation when a rendered scene seems incomplete or unexpectedly expensive.

Selector: `--list-meshes`

Inputs: Cell and target asset configuration.

Outputs: Mesh census with counts and classification.

Source: [_geometry-harness/viewprobe/Program.cs](../_geometry-harness/viewprobe/Program.cs) — `--list-meshes`


<a id="tool-viewprobe.list-terrain"></a>

### viewprobe.list-terrain

**Which terrain layers contribute to this rendered cell?**

Investigate ground appearance by inspecting contributing landscape materials.

Selector: `--list-terrain`

Inputs: Cell and target terrain configuration.

Outputs: Terrain layer diagnostics.

Source: [_geometry-harness/viewprobe/Program.cs](../_geometry-harness/viewprobe/Program.cs) — `--list-terrain`


<a id="tool-viewprobe.map-tiles"></a>

### viewprobe.map-tiles

**How do I get top-down pictures of cells, as the renderer draws them, for a map?**

Draw map tiles of an exterior block from the built scene; the workbench's World-tab map runs it as a child process.

Selector: `--map-tiles`

Inputs: The normal scene arguments (load order, target cell, ring) plus a JSON file giving the height range and each tile's world rectangle, units per pixel and output PNG path.

Outputs: One RGBA PNG per listed rectangle, north at the top, transparent where nothing is drawn; a SkyRender 3.x build refuses the flag in one sentence.

Source: [_geometry-harness/viewprobe/Program.cs](../_geometry-harness/viewprobe/Program.cs) — `--map-tiles`

Source: [_geometry-harness/viewprobe/MapTiles.cs](../_geometry-harness/viewprobe/MapTiles.cs) — `--map-tiles`


<a id="tool-viewprobe.mask-evidence-selftest"></a>

### viewprobe.mask-evidence-selftest

**Does the visibility mask evidence diagnostic satisfy its expected contract?**

Qualify subject-mask calculations with constructed visibility controls before trusting capture eligibility.

Selector: `--mask-evidence-selftest`

Inputs: Arguments of the linked standalone diagnostic branch; use disposable test destinations.

Outputs: Diagnostic assertions and exit status; fault arms deliberately refuse.

Source: [_geometry-harness/viewprobe/Program.cs](../_geometry-harness/viewprobe/Program.cs) — `--mask-evidence-selftest`


<a id="tool-viewprobe.mesh-reuse-red"></a>

### viewprobe.mesh-reuse-red

**Does the mesh reuse ownership diagnostic satisfy its expected contract?**

Exercise the deliberate ownership-failure arm when reviewing geometry reuse guards.

Selector: `--mesh-reuse-red`

Inputs: Arguments of the linked standalone diagnostic branch; use disposable test destinations.

Outputs: Diagnostic assertions and exit status; fault arms deliberately refuse.

Source: [_geometry-harness/viewprobe/Program.cs](../_geometry-harness/viewprobe/Program.cs) — `--mesh-reuse-red`


<a id="tool-viewprobe.moved-ref-selftest"></a>

### viewprobe.moved-ref-selftest

**Does the moved and deleted reference selection diagnostic satisfy its expected contract?**

Check relocation and deletion controls when modifying scene winner traversal.

Selector: `--moved-ref-selftest`

Inputs: Arguments of the linked standalone diagnostic branch; use disposable test destinations.

Outputs: Diagnostic assertions and exit status; fault arms deliberately refuse.

Source: [_geometry-harness/viewprobe/Program.cs](../_geometry-harness/viewprobe/Program.cs) — `--moved-ref-selftest`


<a id="tool-viewprobe.nav-at"></a>

### viewprobe.nav-at

**Which winning navmesh triangles cover this coordinate?**

Check the traversable surface beneath a location, including reasons candidate triangles were excluded.

Selector: `--nav-at`

Inputs: World X,Y.

Outputs: Covering triangles, heights and filter reasons.

Source: [_geometry-harness/viewprobe/Program.cs](../_geometry-harness/viewprobe/Program.cs) — `--nav-at`


<a id="tool-viewprobe.nav-link"></a>

### viewprobe.nav-link

**Where do one navmesh's edges cross into another, and does the target triangle each crossing names exist?**

Locate an edge-link mismatch on the seam before drawing it, or check which cell and provider a navmesh's winning version has.

Selector: `--nav-link`

Inputs: SOURCE_NAVM=TARGET_NAVM FormKey pairs (at most 16).

Outputs: One nav-link JSON row per pair: last-sighting cells and providers, the target's triangle count, link-table counts and every crossing with its endpoints and whether it is past the count.

Source: [_geometry-harness/viewprobe/NavmeshLinks.cs](../_geometry-harness/viewprobe/NavmeshLinks.cs) — `--nav-link SOURCE=TARGET`


<a id="tool-viewprobe.nav-link-smoke"></a>

### viewprobe.nav-link-smoke

**Does the navmesh link query give its known answers on synthetic records?**

Check the crossing finder after changing navmesh edge-link reading or the last-sighting rule.

Selector: `--nav-link-smoke`

Inputs: None; the records are synthetic.

Outputs: PASS/FAIL lines per known answer and exit status (1 on any FAIL).

Source: [_geometry-harness/viewprobe/Program.cs](../_geometry-harness/viewprobe/Program.cs) — `--nav-link-smoke`


<a id="tool-viewprobe.nav-selection-smoke"></a>

### viewprobe.nav-selection-smoke

**Does the navmesh selection diagnostic satisfy its expected contract?**

Check the triangle-filtering controls after changing navigation overlay selection.

Selector: `--nav-selection-smoke`

Inputs: Arguments of the linked standalone diagnostic branch; use disposable test destinations.

Outputs: Diagnostic assertions and exit status; fault arms deliberately refuse.

Source: [_geometry-harness/viewprobe/Program.cs](../_geometry-harness/viewprobe/Program.cs) — `--nav-selection-smoke`


<a id="tool-viewprobe.nav-z"></a>

### viewprobe.nav-z

**Which navmesh belongs to the storey being inspected?**

Restrict the navigation overlay to the level under investigation in a stacked interior.

Selector: `--nav-z`

Inputs: World Z or auto, optional tolerance.

Outputs: Storey-filtered overlay with the filter disclosed.

Source: [_geometry-harness/viewprobe/Program.cs](../_geometry-harness/viewprobe/Program.cs) — `--nav-z`


<a id="tool-viewprobe.npc"></a>

### viewprobe.npc

**What character appearance and runtime outfit occupy this pose?**

Inspect an actor at a chosen pose with resolved appearance assets and the supplied runtime clothing data.

Selector: `--npc`

Inputs: NPC or placed actor FormKey, optional pose and label.

Outputs: Character render with optional puck or outfit suppression.

Source: [_geometry-harness/viewprobe/Program.cs](../_geometry-harness/viewprobe/Program.cs) — `--npc`


<a id="tool-viewprobe.occluders-of"></a>

### viewprobe.occluders-of

**Which objects stand in front of this object in this view, and over how many of its pixels?**

Find what a cutaway would have to hide at one camera.

Selector: `--occluders-of`

Inputs: A FormKey and a camera.

Outputs: Its covered and visible pixels, each nearer ref with its pixel count (translucent ones marked), and the rest of the scene's count.

Source: [_geometry-harness/viewprobe/CaptureViews.cs](../_geometry-harness/viewprobe/CaptureViews.cs) — `--occluders-of FORMKEY the drawn refs nearer than the named ref`


<a id="tool-viewprobe.occlusion"></a>

### viewprobe.occlusion

**What geometry does the occlusion preview cull?**

Compare culling enabled, disabled, and difference views for a suspected panel-related artifact.

Selector: `--occlusion`

Inputs: Mode off, on or diff and configured rule.

Outputs: Culling preview; engine confirmation remains separate.

Source: [_geometry-harness/viewprobe/Program.cs](../_geometry-harness/viewprobe/Program.cs) — `--occlusion`


<a id="tool-viewprobe.occlusion-query-selftest"></a>

### viewprobe.occlusion-query-selftest

**Does the GPU occlusion query count the same pixels as the CPU depth compare on this machine?**

Check --occlusion-query on a new GPU, driver or build: the sampled-depth conversion against ReadPixels over a full-range fill, the counts against ShapeVisibility.Compare, and the bindings the pass restores.

Selector: `--occlusion-query-selftest`

Inputs: No arguments; needs a GL context but no MO2 instance or game.

Outputs: One line per known answer, the number of the 2^24 depth values the fill reached, and exit status 0 when all hold.

Source: [_geometry-harness/viewprobe/Program.cs](../_geometry-harness/viewprobe/Program.cs) — `--occlusion-query-selftest`


<a id="tool-viewprobe.occlusion-rule-selftest"></a>

### viewprobe.occlusion-rule-selftest

**Does the occlusion rule's orientation scope (upright tolerance, Box upright test, horizontal class) satisfy its known answers?**

Check the rule loader and Shadow's orientation refusals after changing an occlusion rule field or PortalBoxOrientation.

Selector: `--occlusion-rule-selftest`

Inputs: A disposable folder for the rule fixtures it writes.

Outputs: Known-answer results (cases 6000-6014) and exit status.

Source: [_geometry-harness/viewprobe/Program.cs](../_geometry-harness/viewprobe/Program.cs) — `--occlusion-rule-selftest`


<a id="tool-viewprobe.pick"></a>

### viewprobe.pick

**What world position lies under this frame pixel?**

Translate an observed image feature into a scene coordinate using the frame's measured buffer.

Selector: `--pick`

Inputs: Pixel X,Y in the finished frame.

Outputs: Unprojected world coordinate and pick diagnostics.

Source: [_geometry-harness/viewprobe/Program.cs](../_geometry-harness/viewprobe/Program.cs) — `--pick`


<a id="tool-viewprobe.planes"></a>

### viewprobe.planes

**Where are the occlusion primitives in this scene?**

Inspect occlusion panel locations while diagnosing unexplained disappearance of scene geometry.

Selector: `--planes`

Inputs: Cell and camera settings.

Outputs: Translucent primitive overlays coloured by owner.

Source: [_geometry-harness/viewprobe/Program.cs](../_geometry-harness/viewprobe/Program.cs) — `--planes`


<a id="tool-viewprobe.planes-report"></a>

### viewprobe.planes-report

**Which occlusion candidates cover the measured frame geometry?**

Bind occlusion candidate measurements to the exact camera used for an evidence image.

Selector: `--planes-report`

Inputs: Camera/cell, --planes-report switch and optional reach/visibility budget; output follows --out.

Outputs: Planes report with candidate and visibility evidence.

Source: [_geometry-harness/viewprobe/Program.cs](../_geometry-harness/viewprobe/Program.cs) — `--planes-report`


<a id="tool-viewprobe.planes-report-selftest"></a>

### viewprobe.planes-report-selftest

**Does the planes report candidate joins diagnostic satisfy its expected contract?**

Check normalized candidate-to-primitive joins after changing the occlusion report schema.

Selector: `--planes-report-selftest`

Inputs: Arguments of the linked standalone diagnostic branch; use disposable test destinations.

Outputs: Diagnostic assertions and exit status; fault arms deliberately refuse.

Source: [_geometry-harness/viewprobe/Program.cs](../_geometry-harness/viewprobe/Program.cs) — `--planes-report-selftest`


<a id="tool-viewprobe.plugin-read-selftest"></a>

### viewprobe.plugin-read-selftest

**Does the unreadable plugin refusal diagnostic satisfy its expected contract?**

Exercise damaged-input controls before accepting changes to plugin loading and refusal handling.

Selector: `--plugin-read-selftest`

Inputs: Arguments of the linked standalone diagnostic branch; use disposable test destinations.

Outputs: Diagnostic assertions and exit status; fault arms deliberately refuse.

Source: [_geometry-harness/viewprobe/Program.cs](../_geometry-harness/viewprobe/Program.cs) — `--plugin-read-selftest`


<a id="tool-viewprobe.protect"></a>

### viewprobe.protect

**Does hiding these objects remove anything a clashing object touches or rests on?**

Guard a cutaway: the render refuses rather than hide a protected object's contact or support in its own setup.

Selector: `--protect`

Inputs: FormKeys that must keep everything they touch and rest on (with --hide).

Outputs: A refusal naming the hidden object and the relation, or a line saying the protected object kept everything.

Source: [_geometry-harness/viewprobe/CaptureViews.cs](../_geometry-harness/viewprobe/CaptureViews.cs) — `--protect FORMKEY     with --hide: refuse`


<a id="tool-viewprobe.ref-surface"></a>

### viewprobe.ref-surface

**Can I isolate the render surface of the named reference?**

Qualify a subject's drawn geometry before using it as a capture or visibility target.

Selector: `--ref-surface`

Inputs: Selected reference and subject-surface options.

Outputs: Isolated subject surface or explicit unsupported-shape refusal.

Source: [_geometry-harness/viewprobe/Program.cs](../_geometry-harness/viewprobe/Program.cs) — `--ref-surface`


<a id="tool-viewprobe.refs-at"></a>

### viewprobe.refs-at

**Why is a reference near this position not drawn?**

Diagnose absent geometry around a known coordinate before changing meshes or plugin records.

Selector: `--refs-at`

Inputs: X,Y and optional radius under the same configuration.

Outputs: Nearby references and draw/refusal reasons.

Source: [_geometry-harness/viewprobe/Program.cs](../_geometry-harness/viewprobe/Program.cs) — `--refs-at`


<a id="tool-viewprobe.report-mode"></a>

### viewprobe.report-mode

**How can I render report evidence without inspection overlays?**

Publish a finding image under the evidence overlay policy after finishing scene inspection.

Selector: `--report-mode`

Inputs: Cell/worklist and report mode options.

Outputs: Report-ready frame under the explicit overlay policy.

Source: [_geometry-harness/viewprobe/Program.cs](../_geometry-harness/viewprobe/Program.cs) — `--report-mode`


<a id="tool-viewprobe.report-mode-smoke"></a>

### viewprobe.report-mode-smoke

**Does the report overlay policy diagnostic satisfy its expected contract?**

Qualify a change to evidence overlays before producing reader-facing frames.

Selector: `--report-mode-smoke`

Inputs: Arguments of the linked standalone diagnostic branch; use disposable test destinations.

Outputs: Diagnostic assertions and exit status; fault arms deliberately refuse.

Source: [_geometry-harness/viewprobe/Program.cs](../_geometry-harness/viewprobe/Program.cs) — `--report-mode-smoke`


<a id="tool-viewprobe.report-sink-kill-arm"></a>

### viewprobe.report-sink-kill-arm

**Does the interrupted report publication diagnostic satisfy its expected contract?**

Exercise an interrupted writer to verify that incomplete evidence cannot appear as a completed report.

Selector: `--report-sink-kill-arm`

Inputs: Arguments of the linked standalone diagnostic branch; use disposable test destinations.

Outputs: Diagnostic assertions and exit status; fault arms deliberately refuse.

Source: [_geometry-harness/viewprobe/Program.cs](../_geometry-harness/viewprobe/Program.cs) — `--report-sink-kill-arm`


<a id="tool-viewprobe.report-sink-selftest"></a>

### viewprobe.report-sink-selftest

**Does the atomic report publication diagnostic satisfy its expected contract?**

Check publication and output integrity after changing report streaming code.

Selector: `--report-sink-selftest`

Inputs: Arguments of the linked standalone diagnostic branch; use disposable test destinations.

Outputs: Diagnostic assertions and exit status; fault arms deliberately refuse.

Source: [_geometry-harness/viewprobe/Program.cs](../_geometry-harness/viewprobe/Program.cs) — `--report-sink-selftest`


<a id="tool-viewprobe.route"></a>

### viewprobe.route

**Where does this declared walking route cross the scene?**

Inspect a declared set of walking waypoints against the scene before drawing conclusions about passage.

Selector: `--route`

Inputs: Repeated waypoint coordinates and route style.

Outputs: Route overlay with clearance and off-mesh refusal.

Source: [_geometry-harness/viewprobe/Program.cs](../_geometry-harness/viewprobe/Program.cs) — `--route`


<a id="tool-viewprobe.route-solve"></a>

### viewprobe.route-solve

**What walkable path connects these route endpoints?**

Follow connected walkable triangles when a straight waypoint segment would misrepresent the path.

Selector: `--route-solve`

Inputs: Route endpoints and storey selection.

Outputs: Path and length, or unreachable refusal.

Limit: Not covered: whether several mods' changes together cut NPC access between two places. This mode finds a path between endpoints you give over the winning navmesh's own triangle links in the loaded cell; placed objects are not cut into that graph, door triangles are not crossed, and nothing is compared with the order without the changes, so a path or a refusal attributes nothing to any edit. The collective check is unbuilt (world-edit-compatibility check registry, functional.collective-route-connectivity).

Source: [_geometry-harness/viewprobe/Program.cs](../_geometry-harness/viewprobe/Program.cs) — `--route-solve`


<a id="tool-viewprobe.seams"></a>

### viewprobe.seams

**Where do neighbouring landscape vertices disagree?**

Make terrain boundary discontinuities visible while adjusting the inspection tolerance.

Selector: `--seams`

Inputs: Optional epsilon in world units.

Outputs: Coloured boundary ribbons.

Source: [_geometry-harness/viewprobe/Program.cs](../_geometry-harness/viewprobe/Program.cs) — `--seams`


<a id="tool-viewprobe.smoke"></a>

### viewprobe.smoke

**Does the basic renderer startup diagnostic satisfy its expected contract?**

Establish that a build can initialize its rendering context before running a longer capture campaign.

Selector: `--smoke`

Inputs: Arguments of the linked standalone diagnostic branch; use disposable test destinations.

Outputs: Diagnostic assertions and exit status; fault arms deliberately refuse.

Source: [_geometry-harness/viewprobe/Program.cs](../_geometry-harness/viewprobe/Program.cs) — `--smoke`


<a id="tool-viewprobe.state"></a>

### viewprobe.state

**What appears or disappears when an enable-parent switch flips?**

Inspect a known switch or stage transition alongside its retained baseline view.

Selector: `--state`

Inputs: State specification; switch map for stage selectors.

Outputs: Selected state render and optional arrivals/departures diff.

Source: [_geometry-harness/viewprobe/Program.cs](../_geometry-harness/viewprobe/Program.cs) — `--state`


<a id="tool-viewprobe.stations"></a>

### viewprobe.stations

**Which camera stations cover this cell route?**

Cover a declared route with a reproducible set of views from a station specification.

Selector: `--stations`

Inputs: Station specification JSON.

Outputs: Generated station/worklist output or refusal.

Source: [_geometry-harness/viewprobe/Program.cs](../_geometry-harness/viewprobe/Program.cs) — `--stations`


<a id="tool-viewprobe.unreadable-asset-smoke"></a>

### viewprobe.unreadable-asset-smoke

**Does the unreadable mesh classification diagnostic satisfy its expected contract?**

Exercise corrupt-mesh controls after changing asset loading or failure classification.

Selector: `--unreadable-asset-smoke`

Inputs: Arguments of the linked standalone diagnostic branch; use disposable test destinations.

Outputs: Diagnostic assertions and exit status; fault arms deliberately refuse.

Source: [_geometry-harness/viewprobe/Program.cs](../_geometry-harness/viewprobe/Program.cs) — `--unreadable-asset-smoke`


<a id="tool-viewprobe.winning-refs-dump"></a>

### viewprobe.winning-refs-dump

**What is the winning placement state without rendering frames?**

Retain scene placement identities for downstream comparisons that do not need image output.

Selector: `--winning-refs-dump`

Inputs: Output file, optional cell list and terrain switch.

Outputs: JSON lines of winning reference state.

Source: [_geometry-harness/viewprobe/Program.cs](../_geometry-harness/viewprobe/Program.cs) — `--winning-refs-dump`


<a id="tool-viewprobe.winning-refs-selftest"></a>

### viewprobe.winning-refs-selftest

**Does the winning reference selection diagnostic satisfy its expected contract?**

Qualify placement traversal after changing override resolution in the scene extractor.

Selector: `--winning-refs-selftest`

Inputs: Arguments of the linked standalone diagnostic branch; use disposable test destinations.

Outputs: Diagnostic assertions and exit status; fault arms deliberately refuse.

Source: [_geometry-harness/viewprobe/Program.cs](../_geometry-harness/viewprobe/Program.cs) — `--winning-refs-selftest`


<a id="tool-viewprobe.worklist"></a>

### viewprobe.worklist

**How can I render the same declared shots reproducibly?**

Revisit a saved camera campaign for repeatable inspection or batch output.

Selector: `--worklist`

Inputs: One or more worklist JSON files.

Outputs: Per-shot frames with retained cell/shot identity.

Source: [_geometry-harness/viewprobe/Program.cs](../_geometry-harness/viewprobe/Program.cs) — `--worklist`


<a id="tool-viewprobe.write-capture-fixture"></a>

### viewprobe.write-capture-fixture

**Which fixture do the capture-fallback tests render?**

Write the test fixture (interior cells of vanilla meshes, two plugins) for tests/test_capture_fallbacks.py.

Selector: `--write-capture-fixture`

Inputs: An output folder.

Outputs: CfFixture.esp and CfFixtureMod.esp.

Source: [_geometry-harness/viewprobe/CaptureFixture.cs](../_geometry-harness/viewprobe/CaptureFixture.cs) — `THE CAPTURE-FALLBACK FIXTURE`


Modifiers (do not select a separate mode):

- `--adapt-lum` — The image space's adapted luminance (the contrast pivot); default 0.367, read from the retail adaptation texture at SkyrimClear noon.
- `--allow-unreadable-assets` — Opt out of the failure exit for found-but-unreadable assets; omissions remain disclosed in the frame, report and run record.
- `--angles` — Set angles for this invocation; consult the linked parser for accepted values.
- `--aspect` — Set aspect for this invocation; consult the linked parser for accepted values.
- `--bats-profiles` — Set bats profiles for this invocation; consult the linked parser for accepted values.
- `--cell-lighting` — Interiors lit by their own XCLL, image space and placed lights; already the default, kept for older command lines.
- `--cells` — Set cells for this invocation; consult the linked parser for accepted values.
- `--clear-bounds` — Set clear bounds for this invocation; consult the linked parser for accepted values.
- `--clear-reach` — Set clear reach for this invocation; consult the linked parser for accepted values.
- `--conflicts` — Set conflicts for this invocation; consult the linked parser for accepted values.
- `--depth-measurement` — With --planes-report: the same depth measurement without appearance work (only alpha-tested diffuses load; masks only for shapes with visible pixels).
- `--depth-measurement-selftest` — Known answers for the depth-measurement mode's texture keep rule and mask rule; needs no instance or GL context.
- `--dump-terrain` — Set dump terrain for this invocation; consult the linked parser for accepted values.
- `--elev` — Set elev for this invocation; consult the linked parser for accepted values.
- `--eye` — Set eye for this invocation; consult the linked parser for accepted values.
- `--eye-height` — Set eye height for this invocation; consult the linked parser for accepted values.
- `--eye-ref` — Set eye ref for this invocation; consult the linked parser for accepted values.
- `--flags` — Unsupported compatibility switch: the renderer refuses it; load-time enable chains are already applied.
- `--fly-speed` — Set fly speed for this invocation; consult the linked parser for accepted values.
- `--fov` — Set fov for this invocation; consult the linked parser for accepted values.
- `--fx` — Set fx for this invocation; consult the linked parser for accepted values.
- `--fx-static` — Set fx static for this invocation; consult the linked parser for accepted values.
- `--game` — Set game for this invocation; consult the linked parser for accepted values.
- `--game-hour` — The hour the sun is placed for on the worldspace climate's path (default 12).
- `--grass` — Draw the near-field grass; already the default, kept for explicit command lines.
- `--grid-color` — Set grid color for this invocation; consult the linked parser for accepted values.
- `--inside-bounds` — Set inside bounds for this invocation; consult the linked parser for accepted values.
- `--instance` — Set instance for this invocation; consult the linked parser for accepted values.
- `--keep-all-masks` — With --depth-measurement: write every assessed shape's evidence mask, as the default path does.
- `--large-refs-child` — Which large-reference list a child worldspace (a walled city) uses for references 3-5 cells out: union (default; its own lists, as read in game) or none.
- `--live` — with --ring N under a host: the host adds and drops the square's cells in the live renderer (LiveScene.cs)
- `--lod` — Exteriors: draw the worldspace's distant terrain, object, tree and water LOD beyond the loaded cells (opt-in: adds load time; far plane 409600).
- `--look` — Set look for this invocation; consult the linked parser for accepted values.
- `--look-ref` — Set look ref for this invocation; consult the linked parser for accepted values.
- `--look-sens` — Set look sens for this invocation; consult the linked parser for accepted values.
- `--markers` — Set markers for this invocation; consult the linked parser for accepted values.
- `--msaa` — Set msaa for this invocation; consult the linked parser for accepted values.
- `--nav` — Set nav for this invocation; consult the linked parser for accepted values.
- `--nav-cell` — Set nav cell for this invocation; consult the linked parser for accepted values.
- `--nav-color` — Set nav color for this invocation; consult the linked parser for accepted values.
- `--nav-link` — Ask where SOURCE_NAVM's edges cross into TARGET_NAVM; consult the linked parser for accepted values.
- `--nav-plugin` — Set nav plugin for this invocation; consult the linked parser for accepted values.
- `--nav-version` — Set nav version for this invocation; consult the linked parser for accepted values.
- `--no-cell-lighting` — Interiors keep the fixed studio lights instead of their own XCLL, image space and placed lights.
- `--no-cell-outline` — Set no cell outline for this invocation; consult the linked parser for accepted values.
- `--no-coverings` — Set no coverings for this invocation; consult the linked parser for accepted values.
- `--no-fx` — Drop effect-shader shapes (fire, smoke, glows), which are drawn by default.
- `--no-grass` — Exteriors: leave out the near-field grass (the GRAS the drawn cells' landscape textures list), which is drawn by default; its placement is seeded, not the game's exact tufts.
- `--no-imagespace` — Leave the frame ungraded: no weather or cell image space (tone map and cinematic grade).
- `--no-nav` — Set no nav for this invocation; consult the linked parser for accepted values.
- `--no-nav-diff` — Set no nav diff for this invocation; consult the linked parser for accepted values.
- `--no-nav-under-statics` — Set no nav under statics for this invocation; consult the linked parser for accepted values.
- `--no-nav-underground` — Set no nav underground for this invocation; consult the linked parser for accepted values.
- `--no-reuse-cell` — Set no reuse cell for this invocation; consult the linked parser for accepted values.
- `--no-ring-refs` — Set no ring refs for this invocation; consult the linked parser for accepted values.
- `--no-route-discs` — Set no route discs for this invocation; consult the linked parser for accepted values.
- `--no-shadows` — No sun shadow map, which is on whenever a weather lights the frame.
- `--no-terrain` — Set no terrain for this invocation; consult the linked parser for accepted values.
- `--no-terrain-layers` — Set no terrain layers for this invocation; consult the linked parser for accepted values.
- `--no-water` — Set no water for this invocation; consult the linked parser for accepted values.
- `--no-weather` — Exteriors keep the fixed studio lights instead of their region's weather (sky, sun, ambient, fog, image space, sun shadows).
- `--npc-no-outfit` — Set npc no outfit for this invocation; consult the linked parser for accepted values.
- `--npc-puck` — Actor marker mode on, off or auto: controls the yellow disc and rim; auto enables it for steep overhead views.
- `--occlusion-query` — With --depth-measurement: count each isolated draw's covered and visible pixels with a GPU occlusion query; only shapes with visible pixels are read back, hashed and checked on the CPU.
- `--occlusion-query-selftest` — Known answers for --occlusion-query on this GPU: depth conversion over a full-range fill, counts against the CPU compare, restored bindings; needs a GL context, no instance.
- `--occlusion-rule` — Set occlusion rule for this invocation; consult the linked parser for accepted values.
- `--order` — Set order for this invocation; consult the linked parser for accepted values.
- `--out` — Set out for this invocation; consult the linked parser for accepted values.
- `--pitch` — Set pitch for this invocation; consult the linked parser for accepted values.
- `--planes-reach` — Set planes reach for this invocation; consult the linked parser for accepted values.
- `--prepare-out` — write the prepared --ring square as a live-scene area (LiveArea.cs) and stop, for a host to load later
- `--profile` — Set profile for this invocation; consult the linked parser for accepted values.
- `--progress-dir` — Set progress dir for this invocation; consult the linked parser for accepted values.
- `--red` — Set red for this invocation; consult the linked parser for accepted values.
- `--ref` — Set ref for this invocation; consult the linked parser for accepted values.
- `--ref-surface-opacity` — Set ref surface opacity for this invocation; consult the linked parser for accepted values.
- `--reuse-cell` — Set reuse cell for this invocation; consult the linked parser for accepted values.
- `--ring` — Loaded area for exteriors: 1 (default, the 3x3), N (radius N) or visible (every cell whose ground the frame sees); opt-in because it multiplies load time.
- `--ring-cap` — With --ring visible: the most cells loaded (default 160), nearest the eye kept.
- `--ring-reach` — With --ring visible: how far from the eye's cell, in cells, the frame is searched (default 8).
- `--route-arrows` — Set route arrows for this invocation; consult the linked parser for accepted values.
- `--route-color` — Set route color for this invocation; consult the linked parser for accepted values.
- `--route-z` — Set route z for this invocation; consult the linked parser for accepted values.
- `--shadow-distance` — How far from the eye the sun's shadow map reaches (default 10000).
- `--shadows` — Force the sun shadow map on, even without a weather.
- `--size` — Set size for this invocation; consult the linked parser for accepted values.
- `--sky` — Set sky for this invocation; consult the linked parser for accepted values.
- `--state-view` — Set state view for this invocation; consult the linked parser for accepted values.
- `--stations-out` — Set stations out for this invocation; consult the linked parser for accepted values.
- `--switch-map` — Set switch map for this invocation; consult the linked parser for accepted values.
- `--tex` — Set tex for this invocation; consult the linked parser for accepted values.
- `--visibility-budget` — Set visibility budget for this invocation; consult the linked parser for accepted values.
- `--water-arm` — Set water arm for this invocation; consult the linked parser for accepted values.
- `--weather` — Light an exterior from this WTHR (EditorID or FormKey) instead of its region's most likely weather.
- `--weather-slot` — The weather's time of day: day (default), sunrise, sunset or night.
- `--worklist-readiness-sha256` — Set worklist readiness sha256 for this invocation; consult the linked parser for accepted values.
- `--worklist-selection` — Set worklist selection for this invocation; consult the linked parser for accepted values.
- `--yaw` — Set yaw for this invocation; consult the linked parser for accepted values.

<a id="tool-waterprobe"></a>

### waterprobe

**Do winning cell records lose water data or disagree at a water boundary?**

Investigate dry areas or discontinuous water surfaces after cell overrides change the winning scene.

Inputs: Winning cell water records and adjacent-cell scope.

Outputs: Lost water data and boundary disagreements.

Source: [_geometry-harness/waterprobe/Program.cs](../_geometry-harness/waterprobe/Program.cs) — `// CELL WATER — missing water and water seams at cell boundaries`


Modifiers (do not select a separate mode):

- `--base` — Set base for this invocation; consult the linked parser for accepted values.
- `--cell` — Set cell for this invocation; consult the linked parser for accepted values.
- `--disable` — Set disable for this invocation; consult the linked parser for accepted values.
- `--emit` — Set emit for this invocation; consult the linked parser for accepted values.
- `--enable` — Set enable for this invocation; consult the linked parser for accepted values.
- `--eps` — Set eps for this invocation; consult the linked parser for accepted values.
- `--game` — Set game for this invocation; consult the linked parser for accepted values.
- `--instance` — Set instance for this invocation; consult the linked parser for accepted values.
- `--json` — Set json for this invocation; consult the linked parser for accepted values.
- `--list` — Set list for this invocation; consult the linked parser for accepted values.
- `--min-visible` — Set min visible for this invocation; consult the linked parser for accepted values.
- `--near` — Set near for this invocation; consult the linked parser for accepted values.
- `--no-land` — Set no land for this invocation; consult the linked parser for accepted values.
- `--order` — Set order for this invocation; consult the linked parser for accepted values.
- `--profile` — Set profile for this invocation; consult the linked parser for accepted values.
- `--self-test` — Run the known-answer self-test over the probe's synthetic fixture; exit 0 when every case passes.
- `--water-mod-cells` — Set water mod cells for this invocation; consult the linked parser for accepted values.
- `--worldspace` — Set worldspace for this invocation; consult the linked parser for accepted values.

<a id="tool-workbench"></a>

### workbench

**How do I look at a cell's placed references in a window, move or turn one, and write the change as a placement override?**

**How does an MCP client (Claude Code, Codex) read and drive the conflict review window: open a conflict, stage a move, write it and read the re-check?**

**How do I open an MO2 profile in place, pick mod folders and probes, run them and review the results in the workbench, writing only under an Output Folder I choose?**

**How do I open a finished evaluation's results in a window and see what the patches addressed, did not address and changed that nothing predicted, each in its cell before or after the patch?**

**How do I browse any load order's worldspaces, cells and placed objects as a tree with each record's winning plugin, from an MO2 profile or a game Data folder, and open a cell in 3D?**

Judge a placed-object conflict by eye and fix it by moving or turning a reference, with the edit written to a new patch plugin through the houseCARL server.

Inputs: With no arguments: the launcher (Start tab; workbench_act open_profile, select_mods, select_probes, set_output, set_work_folder, run), in free mode on any MO2 instance or houseCARL mode (WB_HOUSECARL_LAUNCH or the housecarl entry of ~/.claude.json); WB_MACHINE for the pipeline's machine file. Or --bundle DIR [--arm A] (a verified evaluation bundle; WB_OUTPUT names the Output Folder writes go under), then viewprobe's command line; optionally WB_SCRIPT steps for a headless run, and WB_MCP_PORT for the MCP state tools on 127.0.0.1.

Outputs: A window with the launcher, the run's ranked Conflicts and Findings tabs, a Records tree and the built scene; staged edits; a new patch plugin under the Output Folder, or a one-sentence refusal; with WB_MCP_PORT, workbench_state, workbench_act and the forwarded houseCARL tools over Streamable HTTP.

Source: [_geometry-harness/workbench/Program.cs](../_geometry-harness/workbench/Program.cs) — `/// <summary>Entry point: viewprobe's own command line builds the scene`
