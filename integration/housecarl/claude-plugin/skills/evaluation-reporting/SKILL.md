---
name: evaluation-reporting
description: "Turn a verified world-edit evaluation bundle into a report for Skyrim modders and a narrated visual walkthrough. Use for evaluation summaries, illustrated Markdown/HTML/PDF reports, or diagnostic videos. Names the compared mods and patch, explains supported findings in ordinary language, and keeps technical detail in a linked evidence index."
---

# Evaluation Reporting

For overlap subject/contact illustrations, keep acquisition, post-render qualification and supported
consequence separate. Follow the controlled subject gate in the world-edit-compatibility skill's
`../world-edit-compatibility/references/overlap-measurements.md`: separate normal/A/B/known-elsewhere repeats, exact input and
render receipts, and a hash-bound normal-view feature witness. Inflated/ghost highlights or a
nonzero pixel difference alone cannot qualify exposed contact. Show refusals and unresolved views
in the evidence packet; incident-level review never covers its unreviewed members.

## The reader and the result

Write for a Skyrim modder deciding whether to use a combination of mods or a patch. Lead with the
answer, explain what the player or NPC could encounter, and distinguish observed behaviour from a
problem suggested by the files. The reader should not need to understand houseCARL to use the result.

The report and video share one evidence-backed presentation plan. `SW/integration/housecarl/evalreport.py`
handles verified inputs, counts, grouping, evidence links and printing; `SW/integration/housecarl/evalreel.py`
assembles the narrated scenes. Use those paths rather than hand-editing a generated report: improvements
to default wording belong in the skill's data or templates, so they survive regeneration.

**Where the tools are.** These tools are SkyWright's, and this skill is SkyWright's houseCARL integration module's
(the `skywright` plugin). `SW` below is the SkyWright checkout this plugin was loaded from: `${CLAUDE_PLUGIN_ROOT}/../../..`
(the plugin folder is `SW/integration/housecarl/claude-plugin/`; the plugin is loaded in place, never as a copy, so
that path is the checkout). Its Python tools are
`SW/integration/housecarl/<tool>.py`; its programs (`runcontrol`, the probes, the record reader) are in its Release
build, each where that build's `skywright-layout.json` puts it. The Python tools find that build themselves
(`buildlayout.py` looks in their own checkout), so run them from the same checkout as the programs, never from
another one. Do not rebuild `SW` while an evaluation runs from it: a build replaces the programs in use.
The report's data (`report-priorities.json`, `scene-policy.json`, `narration.json`) lives in SkyWright's integration
module, `SW/integration/housecarl/reporting-data/`, and the model agents a `models.*` setting routes a step to are this
plugin's own agents (`SW/integration/housecarl/claude-plugin/agents/`, named `skywright:model-claude-…`);
an edit to them is a SkyWright change.

The contract is [EVALUATION-PRESENTATION.md](../../../EVALUATION-PRESENTATION.md), the module's own document.
Running the evaluation itself belongs to `world-edit-compatibility`.

For a versioned overlap incident manifest, use `evalreport.py triage --manifest FILE --out DIR`.
This catalogue preserves the configuration comparisons, all member pairs, source evidence,
explained ranking and unknown/unreviewed counts. It is a scoped geometry-review artifact, not a
completed compatibility evaluation bundle. Follow the
[overlap comparison contract](../world-edit-compatibility/references/overlap-measurements.md#configuration-comparisons-and-incident-triage)
for source scenes, analogue limits and independent visual adjudication. Generated incident text
must be corrected in its source review annotation or compiler, never patched by hand in HTML.

## A report a modder can read

- **Title and scope name both sides.** Say which mod is being evaluated against which other mods or
  load order, and name each evaluated patch. If other mods are inferred from recorded conflicts,
  label them as the main conflicts found, not as an exhaustive description of the load order.
  With no separate patch, the report is not a patch review: title it as the target within the measured
  load order, and keep incidental conflict partners out of the title.
  If a comparison identity is absent, say so; never invent a mod name from a screenshot.
- **Show the relevant load order.** List only plugins contributing local world records to the
  CELLs or exterior coordinates discussed, in their actual order for each tested setup. Include
  one plugin per bullet. Put the scope locations in a compact, wrapping code block with multiple
  entries per line, separated by semicolons; print a list several setups share once and let the later
  setups point at it. Include
  contributors with no flagged conflict. Use the scope measurement and its recorded identity;
  a plugin pair from a finding is not a complete list of contributors. State unavailable or partial
  location coverage explicitly, and measure each changed setup before claiming its list is complete.
  `evalreport plan` uses the bundle's matching baseline scope; supply each other measured map with
  `--location-scope ARM=FILE`. The compiler checks that it describes that setup and retains its source.
  Read `locationContributors.complete`, not the top-level flag, and keep `worldspaceWide` changes out
  of the per-location list; the
  [presentation contract](../../../EVALUATION-PRESENTATION.md#2-input-and-refusals-d1)
  says what a location lists. A listed plugin has records at that place; that alone does not prove it
  changes what the player sees there.
- **Lead with the prediction, then coverage, then the patch.** The reader's question is what adding
  the mod (or the pair together) will break. Open with that: the clashes between mods the checks
  find on the setup without a patch, by kind, with how each shows in game and whether it needs both
  mods, comes from one, or could not be separated - or “will not introduce inconsistencies between
  mods that the completed checks can see”. Then what the checks could not judge, by reason and count,
  on the page, so “none found” never reads as “none exist”. Then the patch, when one was compared, as
  the answer key: predicted and resolved, left, new clashes, and blind spots with why. Never lead with
  the patch tally. Only clashes between mods are predicted; a mod differing from the base game, one
  mod against itself, overwrite history and clashes already present without the mods are counted
  in one line and kept in the evidence index. The detail list ("What the checks found") likewise
  shows only clashes between two different mods; a result within one mod, against the base game, or
  naming no pair is counted in one sentence and listed in the evidence index with its reason. Where
  the bundle lacks a fact (the patch's own edit list), say so in one sentence; never infer it. A
  patched bundle has no single-mod setups of its own: pass the unpatched evaluation with
  `--sibling-bundle` (once per evaluation) and the compiler joins it only where the shared setup matches exactly, check by
  check; otherwise the rows read "could not be separated" with the reason. A clash a setup without the patch
  already has is never "new with the patch". When the record lane measured the same plugins in another order (a
  load-order check), pass it with `--order-setup ARM@PLAN=ROWS`: the changes lost only in that order are said at
  the top as a measured load-order risk. A successful evidence-file
  check is not a compatibility result, and a limited check finding nothing never supports “This mod
  is compatible.” The wording lives in `reader.prediction` and `reader.inGame` in
  `SW/integration/housecarl/reporting-data/report-priorities.json`; a check with no `inGame` entry says
  it is not described rather than guessing.
- **Use familiar words.** “Setup including the supplied patch,” “NPC walking paths,” “overlapping objects,” and
  “still needs an in-game check” replace arm IDs, probe names and execution statuses. Explain navmesh
  once as the surfaces NPCs use to plan walking routes. A lost navmesh edit does not by itself prove
  an NPC gets stuck. A reported object overlap does not by itself prove a visible obstruction.
- **Name the direction.** Say which plugin's navmesh version takes precedence over whose edit.
  Read the probe's explicit attribution and the measured order; the generic conflict format's A/B
  fields do not universally mean winner/loser. If direction is unresolved, say what evidence is
  missing. For an overlap, name both placed objects; a pair count is not a count of unique objects.
- **An overwrite is history, not an incompatibility.** Do not report a navmesh overwrite as a
  problem merely because its geometry differs or the later plugin lacks a master dependency.
  Even the probe's `Holds` coverage-loss result compares navmesh versions, not the winning scene.
  Require evidence of a mismatch with winning geometry or links, such as a measured route cut,
  an active broken link, or a stranded actor/package witness. Report that supported problem under
  its own evidence and use the overwritten version as explanatory context only. Bare overwrite
  candidates remain in the evidence index; they cannot inflate report/video verdicts, regressions
  or repair counts. Missing corroboration does not prove compatibility.
- **Separate setup membership from cause.** When the same supported record-level problem is present
  in the baseline, say “Already present before adding the patch; still present in this setup.”
  If that comparison was not measured, say it was found in the setup including the patch without
  saying the patch caused it. Claim that a patch does not cover a record only from its actual
  record contents or a complete winning-provider history, not a generic conflict pair.
- **Explain each issue.** Name the place and mods involved, what differs, why it might matter, what
  the evidence actually establishes, and the next useful check or remedy. Link supporting detail
  with readable labels such as “Evidence” or “Full results.” Do not dump detector output into prose.
- **Keep results separate.** Show the original combination and each tested alternative distinctly.
  State fixes, remaining issues and new problems separately; fixes never cancel a regression.
  Explain whether only the patch's plugin was evaluated, and whether its assets, installer choices
  and behaviour in the running game were checked.
- **Separate current totals from new problems.** “No additional overlap introduced” describes a
  comparison with the starting setup; it does not mean the patched setup has no overlaps. State the
  measured starting and resulting totals alongside that result when available, and put comparison
  results in the patch-changes section rather than a list headed “Checks that found no problem.”
  Say what each total counts: its unit, whether it includes conflicts the report does not list, and
  any overwrite history left out — otherwise it reads as contradicting the probe's own total.
- **Summarize unfinished testing by subject and consequence.** Keep the complete checklist in the
  evidence index. The report still names every unassessed area and its reason in ordinary language;
  it must not imply an omitted check passed. Do not lead with booleans, hashes, command-line options,
  required-check/arm arithmetic, `CLOBBERED`, `EDGE-IDX`, or similar internal labels.

Exact plugin filenames, versions when supplied, complete rows, configuration identities, raw detector
codes, CLI selections, reverse comparisons, technical caveats and audit hashes remain available in
`evidence-index.md` and the machine-readable plan/manifest. “Exhaustive” increases detail; it does not
change the audience to houseCARL developers.

## A video worth watching

The video is a **fast tour of clash vignettes** a modder can watch in a few minutes. The generator decides it
by rule (`SW/integration/housecarl/reel_plan.py`, called by `evalreport plan`); nobody writes a storyboard by hand. Read
[reel-layout.md](references/reel-layout.md) before producing one.

1. **Opening still, about ten seconds.** An infographic page lists the clash types that adding the evaluated mod
   predicts, with their counts and the mod pairs. A second page, "Plugins analysed", lists the plugins in load order
   and draws the mod being added and every plugin taking part in a counted clash in the highlight colour, under an
   "(affected)" legend in the same colour, plus any mod whose runtime layer (Base Object Swapper) changes a scene shown. The narrator says one fixed sentence:
   "Here are the types of clashes that are predicted from adding <mod>." The numbers are on screen to pause on, never
   read aloud.
2. **Vignettes, about ten seconds each.** Every clash a picture can show is pictured, or a few per type when there are
   too many: of a type's drawable clashes, at most `allUpTo` are all shown, otherwise `perType`, one per mod pair first
   (`scene-policy.json` `vignettes`). The first scene of each pictured type says how many cases the type has (the
   opening still's count) and whether what follows is a representative example: "There are 6 cases of objects in the
   same space. Here is a representative example." Then:
   - **Objects in the same space, touching, or clashing in a world state:** the same camera three times: the place
     **without** the mod (a render of the evaluation's own setup without it), **with** it, then the **highlight** that
     marks the objects (cyan and magenta, the through-occluder ghost). When one object is `volumeFactor` times larger
     than the other (a landscape rock), only the smaller is marked. A world-state clash is rendered in the state it was
     measured in (viewprobe `--state` with the switch its row names), and the narration names the condition: the quest
     and stage by in-game names when a quest switches the clash on, otherwise "In one state of the world, when these
     objects appear in <place>". An editor ID is never spoken.
   - **A placement clash (an object or an NPC placed in different spots):** a split screen. On the left, the setup
     without the mod at the other mod's spot; on the right, the prediction setup at the winner's spot; the object
     highlighted in each (an NPC by its puck), over a label strip "Without <mod>" / "With <mod>". One shared camera when
     the planner verifies one seeing both spots; otherwise each half takes the same pose relative to its own placement.
     For NPCs the narration says the mods place the NPC in different spots, never where it stands in play. The
     narration orients the viewer from data only: which mod moved it (a version that differs from the base game's),
     whether that mod moved or replaced the structure around it (its own overrides in that cell), how far and which
     way the other spot is (metres) and, from the right half's downward probe, whether that spot has no floor, is
     below the ground or in the air. A clause whose data is missing is not said.
   Instances of one problem (the same type, mod pair, cell and, for a record clash, field: `vignettes.groupKey`) are
   pictured once; the count is still said and the other members are named once ("Freir and Silana Petreia are placed
   the same way.").
   The narrator then says which patch to get. There is no repeated normal/highlight cycle.
3. **Clashes the reel cannot picture** are counted in one sentence, spoken and on screen once, after the pictured
   scenes: "4 clashes can't be visualized; find them in the accompanying report." This covers cell settings, record
   fields other than a placement, navmesh clashes, visual-effect participants and captures no camera could take. Why
   each one is not pictured is recorded in the plan (`vignettes.stillReasons`) and said in the report (the plan's
   `reel.reportLines`), never in the reel. Never invent a picture that
   shows nothing. A field with no effect in play (`noInGameEffectFields`, such as an editor ID) is left out of the scenes
   and the counts; how many were left out is on the opening still only. A clash on an editor-only marker is left out
   the same way. Every count on the opening is pictured, represented by its type's "There are N cases" sentence, or in
   the unpictured count (`reel.accounting`).
4. **Plain words, by construction.** The narration is the skill's templates (`report-priorities.json` `reel.words`)
   filled with typed slots only: mod names, in-game place and object names (else the editor ID in words), field labels
   and patch names. No report sentence, check name or internal term is spoken. Skyrim terms are fine.
5. **Patches.** A supplied patch speaks only about clashes of its own pair (its masters include both plugins): "Get
   <patch>; it fixes this." Each patch case is a pool of phrasings in `reel.words.patch`; the k-th sentence of a case
   takes phrasing k mod the pool's size, so a reel never repeats a phrasing back to back and is the same every time. Otherwise the reel reads the recorded Nexus patch search (`evalreport plan
   --patch-search`): a listed file is named; "No patch exists for these two mods." is said only when that search,
   run the same way, found its own known-answer patch and read every source for the pair; otherwise "No patch for
   these two mods was found." Never say a patch "was not compared". Without a patch-search file the reel plan refuses
   and the report still compiles.
6. **No disclaimers and no caveats.** The modder started the review and knows it is an offline render. Play-time
   evidence is mentioned only when some was supplied for that clash. No scene says what was not checked, measured or
   decided (Piranha, 2026-11-10): the clauses `reel.words.caveats` names are cut from every report sentence and template
   the reel says or draws, and each cut is recorded in the plan's `movedToReport` with the text the report carries for it.
7. **Closing still.** One sentence, said and shown, as the last scene: "Some tests didn't produce a result; see the
   accompanying document for details." It is there only when some check has no result, on the measured load order or
   another tested setup; otherwise the reel has no closing. Every gap it used to list (checks with no result, items a check
   could not judge, areas not measured) is in the report's gap sections and listed in the plan's `movedToReport`.

Text appears only on the generated stills; evidence frames carry none (viewprobe `--report-mode`). Mod names come
from `evalreport plan --mod-name PLUGIN=NAME`, else the mod's Nexus page name in houseCARL's Nexus catalogue (its Nexus
id from the scope, else the MO2 mod folder's meta.ini; `--nexus-names fetch`, the pipeline's default, asks for a page
the catalogue lacks once), else the MO2 mod folder's name with the reason no Nexus name was found. Each lookup is kept
in the plan's `evidence/mod-names.json`, and the evidence index says each name's source in plain words. With no folder,
the reel plan refuses naming the plugin, and a raw plugin file name is never spoken.

Capture each vignette and split with the shipped steps: `capture_acquire.py acquire --points auto` on the prediction
setup, then `capture_acquire.py capture` for the without / with / highlight frames or the split's left and right
halves; a refused capture is passed back with `--capture-results` and the next drawable clash of its type is tried. Admit each frame in the bundle that measured
its setup (`runcontrol bundle --media`); the "without" frames of a sibling setup are admitted in the sibling bundle and
joined by `--sibling-bundle`. Review each vignette's decoded frames with an image-capable model **without the expected
object names, mod names, concern or narration**, bank the response, and only then compare it with the known identities
(`scene-policy.json` overlap `review.imageModel`). Keep identity, visibility, comprehension and defect support separate:
recognition alone cannot prove clipping or player impact.

Generating a report/video does not itself authorize a new game experiment or load-order change.
Use existing qualified media first. When more acquisition is authorized, use the existing viewprobe
and DevBench/OBS workflows, preserve originals and bind each result to the measured configuration.

## Compile, inspect, deliver

For a real evaluation, run all applicable available checks before compiling; a short `--select`
run is an explicitly partial evaluation. The process driver cannot invoke MCP tools itself.
Run the registry's existing houseCARL record/asset checks through those tools, retaining requests,
responses, tested configuration and scope. Import their identity-bound result receipts through
`evalcontrol import` (into the AI-side overlay, `RUN/ai`) before finalizing its bundle. Missing imported results mean “not checked
in this evaluation,” not “houseCARL cannot check this.” A review of only sampled asset paths is
not an exhaustive missing-assets check. Follow the bundle contract for the receipt format.

Each import declares where its evidence came from, and the report carries that to the reader beside the
result: a result reused from an earlier evaluation says it was not measured here and that its source was
declared rather than re-checked; a receipt written before an origin had to be declared says its origin is
not recorded, never that it is fresh; a result captured for this evaluation is not remarked on. Source
plan, arm, result hash and receipt id belong to the evidence index, which also states that these labels
catch evidence filed under the wrong evaluation by mistake and are not proof of where it came from. Don't
describe imported evidence by hand in the prose - the wording is `reader.importOrigin` in
`SW/integration/housecarl/reporting-data/report-priorities.json`, so a regenerated report keeps it.

Distinguish the existing check for a single object cutting a route from the unresolved question
of several changes jointly disconnecting a route. The presence of the local connectivity engine
does not certify NPC access between all relevant destinations.

```text
python SW/integration/housecarl/evalreport.py plan --bundle RUN/bundle --out RUN/report
python SW/integration/housecarl/evalreport.py report --presentation RUN/report/presentation.json --out RUN/report
python SW/integration/housecarl/evalreport.py verify --out RUN/report
```

The saved evaluation settings (`python SW/integration/housecarl/evalsettings.py show --json`) decide the defaults:
`report.*` (length, PDF, page target) are read by `evalreport.py` itself, and `video.*` by `evalreel.py` and
`evalspeech.py`; a flag still wins. Make a video when `video.make` is `always`, ask the user in chat when it is
`ask`, and do not when it is `off` unless the user asks now. Use in-game footage only when `video.footage` is
`offline-and-game`. Run the blind image review in the agent `evalsettings.py route models.imageReview` names, as
the world-edit-compatibility skill's step 0 says (no `model` parameter; a refusal stops the step).

For the video, follow the storyboard, narration and render commands in
[reel-layout.md](references/reel-layout.md). Keep outputs in a fresh run directory; don't overwrite a
prior report just to demonstrate a workflow change. Set `HOUSECARL_RUNLOG_DIR` to the run's log directory
when timing is evidence. The report defaults to an eight-page target; `--exhaustive` removes the limit.
PDF pages are counted from the actual file. Without a browser, keep the Markdown/HTML and state that
the PDF was not produced; never invent a page count or shrink the font to force a fit.

Read the report from its title through its limitations as the intended reader. Open rendered pages
when printing is part of the deliverable. For video, use `visual-test-designer` to inspect representative
decoded scenes and each claimed before/after transition: identify the place, highlighted subject and
configuration. Review actual decoded object frames with an image-capable model, inspect each vignette's
without, with and highlight frames using the exact frame indices in `reel-map.json`, and obtain the intended
viewer's assessment of object and concern comprehension. Listen to the opening and scene narration where playback is available. Decoder success,
an audio stream or a contact sheet alone does not establish an understandable walkthrough. Record what
was actually reviewed and any playback limitation. Don't declare a deliverable finished just because
an MP4 or PDF exists.

Optional prose revisions use `narrate-packet` / `narrate-apply`; read their bounds in the presentation
contract. The rewrite runs in the agent `evalsettings.py route models.rewriteExplanations` names, and not at
all when it prints `off`. Then write the judge's packet (`narrate-packet --answers`) and run the fidelity judge in
the agent `evalsettings.py route models.checkExplanations --session-model <this session's model ID>` names, a fresh
agent that did not write the rewrites; pass its answer to `narrate-apply --judgements`. The lexical validator
catches invented numbers and names; only the judge catches a changed meaning, and a rewrite without a faithful
judgement keeps its template. Review explanations against the cited result before delivery.

## Acquiring a request's camera

A capture request names the objects and the evidence; it does not know where to stand. One command
turns a request into a verified camera through the shared planner -- no scene-specific script and no
copied camera mathematics:

```
python SW/integration/housecarl/capture_acquire.py acquire   --packet OUT/storyboard.json --request capture-<id>   --points points.json --out OUT/acquisition.json   --bind-packet OUT/storyboard-with-camera.json
```

`--points auto` measures the points with `capture_points.py`: from eye-level stations around the pair
it takes, for each highlighted participant, a depth pick at a pixel that carries that participant's
own `--ref-surface` mark, so the point is on a face a camera outside the pair sees; the floor is the
measured storey the stations share; the bearings that saw both objects become the planner's
`bearingWindow`. It refuses in one sentence when no such face exists (one object inside the other,
an alpha or effect participant, objects standing in water, a reference not placed in the cell). A
`points.json` you supply instead is the same schema: one **measured world point on an opaque face of
each highlighted participant**, plus the `cell` and `floorZ` the camera stands on and any camera
constraints (`capture_acquire.py acquire --help` prints the schema). Two rules that are not
negotiable:

- The point must be a real point on the object's surface. An overlap **envelope's centre lies inside
  the interpenetration volume**, so a camera aimed there is blocked from every position by
  construction. Take a point on the intersection curve or on a face, never a centre or a centroid.
- A missing point or placement input is a prerequisite failure, by design. The adapter does not find
  features, discover rooms or default a storey -- it refuses and names what is missing.

The result is published only when this invocation's own planner run exited successfully and wrote
the result being read, **and** the plan describes the points that were submitted, **and** the planner
returned `verified`, **and** the verification render reported its own configuration fingerprint,
**and** that observed fingerprint equals the one recorded in the request's source evidence.
Reusing one `--receipts` directory across sequential invocations is safe: the planner's result paths are
claimed before it starts, so a failed run cannot leave the previous run's verified plan to be read
as the new answer. Concurrent acquisitions need separate receipts and output paths.
The request's expected fingerprint is never substituted for a
missing observed one, and a declaration-only scene binding -- valid planner output elsewhere -- does
not satisfy a request here. The fingerprint's scope is unchanged: plugin set/order and content,
provider list, BOS overlays, population, state. **Not** mesh or material bytes, archive contents or
the renderer build.

`--bind-packet` writes the packet back with `camera.pose`, `poseId`, the verification, the
configuration match and the renderable worklist on that one request, changing nothing about what any
request selects or claims. `capture_acquire.py capture` then renders every state of the sequence at that exact
pose -- without the mod, with it, highlighted -- and refuses unless the pose each PNG reports and the canvas are
identical across them; only the setup rendered and the highlight differ. A ready camera is **geometry, not
qualified media**: `visualQualification` stays `pending`, and subject recognition, object
comprehension and defect support still go through the review and admission workflow above. Nothing
about a planned camera admits its frames.

## Maintained resources

- `SW/integration/housecarl/reporting-data/report-priorities.json`: report vocabulary, grouping, priorities,
  conservative verdict text and concise layout settings. Domain explanations live here, not in detector code.
- [reel-layout.md](references/reel-layout.md): the vignette reel's plan, stills, capture, speech and render
  commands, timing, output verification and source mapping.
- `SW/integration/housecarl/reporting-data/scene-policy.json`: the reel's vignette selection (`vignettes`), what a picture can
  show and why a clash is not pictured, the split's geometry, the patch-search coverage, the without / with / highlight sequence and the image
  comprehension review requirements.
- Templates: `SW/integration/housecarl/evalreport_templates/`. Keep Markdown and print renditions consistent.
- `SW/integration/housecarl/capture_acquire.py`: the request-to-camera adapter.
  Calls `SW/_geometry-harness/viewprobe/tools/shotplan.py plan`; owns only the request/source/configuration binding. Its cases are in
  `SW/integration/housecarl/tests/test_capture_acquire.py`.
- Tests: `SW/integration/housecarl/tests/test_evalreport.py` and `SW/integration/housecarl/tests/test_evalreel.py`. Exercise reader-facing
  behaviour and evidence fidelity, missing-media/audio refusals and a decoded narrated scene.
