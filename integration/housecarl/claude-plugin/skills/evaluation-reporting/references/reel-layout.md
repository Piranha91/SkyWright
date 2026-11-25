# The review reel: a tour of clash vignettes

`evalreel.py render` cuts a 1280×720, 30 fps H.264/AAC reel from the presentation's `reel` block, which
`evalreport plan` writes through `SW/integration/housecarl/reel_plan.py`. The reel is decided by rule from the prediction
record and the skill's data; no storyboard is written by hand. `reel.srt` is a separate transcript and is never
burned into the video.

## What the plan holds

`presentation.reel` = `{status: planned | refused, reason, chapters[], modNames, modNamesEvidence, media, patchSearch, captureResults,
captureCandidates, accounting, switchCensus, policySha256, wordsSha256}`. A refused plan (no patch-search file, a pair the search does not cover, a spoken mod
with no name) names its reason in one sentence; the report still compiles, and `storyboard-packet` and `render`
refuse with that sentence. Chapters, in order:

| kind | shows | says |
|---|---|---|
| `opening` | still pages `types` (clash types with counts and mod pairs; how many no-effect fields and editor-only marker clashes were left out) and `load-order` (title "Plugins analysed", the "(affected)" legend, the prediction setup's plugins in order with the mod being added and every plugin of a counted clash in the highlight colour, runtime-layer mods) | `reel.words.opening` only |
| `vignette` | `frames` `without` → `with` → `highlight` at one camera; a world-state vignette in its switch's state (`clash.state`) | the first of its type: "There are N cases of …" and a lead; then the clash in one template sentence (a world state with its condition), then the patch sentence |
| `split` | `frames` `left` (the setup without the mod, the other mod's spot) and `right` (the prediction, the winner's spot), side by side over a label strip | as a vignette, with `placementSplit` |
| `unpictured` | `statement` page: the one sentence | "N clashes can't be visualized; find them in the accompanying report." (singular for one) |
| `closing` | `statement` page: the one sentence, only when some check has no result (the opening's incomplete checks, or `reader.missingSummary` on another setup); no closing otherwise. The gaps it used to list are the plan's `movedToReport`, each with the report text that carries it | `reel.words.closing` only |

Selection, picture rules (`pictureTypes`, `drawableFields`, `volumeFactor`, `captureAttemptsPerType`), the reasons a
clash is not pictured, hold times, the split's geometry and the patch-search coverage are `scene-policy.json`
`vignettes`; every sentence and on-screen title is `report-priorities.json` `reel.words`. Every count on the opening is
pictured, represented by its type's "There are N cases" sentence, or in the unpictured count; `reel.accounting` lists
which, by form key. `reel.captureCandidates` lists, per pictured type, the clashes capture may try in order: a refused
capture is counted unpictured and the next one takes its place; a refused member of a group another member pictures is represented by that picture, not also counted unpictured. A clash on an editor-only marker (the
report's marker rule: skill data `markerModels`, or the `markerFlags` record flag read from the base's own plugin) is no
clash a player meets: it is left out of the scenes and counts, and the number left out is on the opening still. No still
shows a form key, a capture request or a setup role label. The narration takes only
typed slots: mod names (`--mod-name`, else the mod's Nexus page name from the catalogue, else the MO2 mod folder's name with its reason; `SW/integration/housecarl/modnames.py`), in-game
place and object names from the bundle's names enrichment (else the editor ID in words, its mod prefix and numbers
dropped), field labels and patch names. Nothing from the report's sentences is read, so nothing from them is spoken.

## The three frames of a vignette

- **without:** the evaluation's own setup without the evaluated mod (`reader.prediction.withoutArm`), rendered with
  viewprobe `--order` at the vignette's camera. It is a different measured setup, not an object hidden in the
  with-setup; hiding or removing an object in a setup stays forbidden.
- **with:** the prediction setup (`reader.prediction.predictionArm`) at the same camera, normal materials.
- **highlight:** the same, both objects marked cyan and magenta with the through-occluder ghost (coloured `--ref`, no
  `--ref-surface`), which also draws each object's hidden part. It is a picture aid, not contact qualification:
  `subject_gate.py` keeps its own frozen arguments.

The camera, projection and canvas are identical across the three; `capture_acquire.py capture` refuses otherwise,
and `evalreel` refuses a vignette whose frames carry different pose ids or sizes. The hold times are skill data: the
without and with frames briefly, the highlight until the narration ends. Every evidence frame is captured with
`--no-cell-outline` (an exterior otherwise carries the cell's dashed border).

A **world-state vignette** renders all three frames with `--state <switch>`, the switch its row's `Kind` names; its
camera is planned and verified in that state. The request's scene identity is the stateprobe emit's configuration with
the renderer's population and that state, which equals the renderer's own fingerprint for the same state; `evalrun
bundle` admits its frames on the same rule (the bundle standard, media check 9). When only the smaller participant is
marked, the capture request names it as `focus` and the point picker rings it, at its own level; a navmesh more than the
storey tolerance below the ground under the stations is another storey, never the camera's floor.

## The split of a placement clash

**What it says.** `placementSplit` gives way to the orienting sentences when the data allows. Each part has its own
source, and a part whose data is missing is not said:
- **Who moved it:** `moverOne` / `moverBoth`. The source is each carrier's own `Placement` (names enrichment `refs[].placements`),
  compared with the defining master's. A reference one of the two mods adds, or one whose base value is unreadable, has
  no base spot: `placementSplit` names the two spots and no mover.
- **The structure:** `structureReplaced` when at least `structureShare` of the mover's overrides in that cell are
  turned off; `structureUp`/`Down` when that share of the rest shift by the object's height within
  `storeyTolerance`. The source is `refs[].cellOverrides`, read from the mover's file.
- **Where the other spot is:** `rightSpot` + `where` in metres (`metresPerUnit`, rounded to 5 m; under 5 m "close to").
  The source is the row's two positions.
- **What is there:** `spotExtra`. The source is the right half's `--clear-at` probe, kept as `support` in its media
  provenance.

Instances of one problem are one group (`groupKey`): the representative is pictured, the others are named
(`groupOthers`) and counted under `represented`, and a group's next member is its capture fallback.

A placement clash of a drawn object or an NPC (`drawableFields`: Placement; a base with a model, or an actor) is
pictured only when the other mod's version wins once the mod being added is gone (the reference's last carrier in the
prediction order, leaving out the mod, is the row's other plugin). Each half renders its own setup at its own cell
(the cell each version is filed in, read by the names enrichment), 638×680, the one participant marked: an NPC by
`--npc` with its puck, an object by a cyan `--ref`. `evalreel` sets them side by side with a 4 px divider over a
40 px label strip ("Without <mod>" / "With <mod>"), 1280×720, no evidence pixel scaled. The camera: one shared camera
when the planner verifies one seeing both spots; otherwise the left placement's camera on the prediction setup, the
right half moved by the placements' difference (`relativePose` on both halves, required labels); when even that is
refused, the planner's labelled top-down fallback, moved the same way, and the blind review decides whether it reads
(a fallback that does not read counts the clash as unpictured).

## Commands

`SW` is the SkyWright checkout the evaluation runs from, and `runcontrol` is the one in its Release build, where that
build's `skywright-layout.json` puts it (the skill's "Where the tools are").

```text
python SW/integration/housecarl/evalreport.py plan --bundle B --sibling-bundle S [--sibling-bundle S_OTHER ...] --out OUT --patch-search PS.json [--mod-name PLUGIN=NAME ...] [--nexus-names offline|fetch]
python SW/integration/housecarl/evalreel.py storyboard-packet --presentation OUT/presentation.json --out OUT/storyboard.json --bundle B
python SW/integration/housecarl/capture_acquire.py acquire --packet OUT/storyboard.json --request capture-<id> --points auto --bundle B --out CAP/acquisition.json --receipts CAP/receipts -- <lab args> --order WITH.json
python SW/integration/housecarl/capture_acquire.py capture --acquisition CAP/acquisition.json --packet OUT/storyboard.json --request capture-<id> --with-order WITH.json --without-order WITHOUT.json --out CAP -- <lab args>
runcontrol bundle --run MAIN_RUN --media CAP/media-<with arm>.json --out B2
runcontrol bundle --run SIBLING_RUN --media CAP/media-<without arm>.json --out S2
python SW/integration/housecarl/evalreport.py plan --bundle B2 --sibling-bundle S2 --out OUT2 --patch-search PS.json --capture-results CAP/capture-result.json
python SW/integration/housecarl/evalreel.py storyboard-packet --presentation OUT2/presentation.json --out OUT2/storyboard.json
python SW/integration/housecarl/evalspeech.py --model-dir MODELS/kokoro --model-revision COMMIT_HASH --storyboard OUT2/storyboard.json --out OUT2/speech
python SW/integration/housecarl/evalreel.py render --presentation OUT2/presentation.json --storyboard OUT2/speech/storyboard-with-audio.json --out OUT2/reel
```

A capture refusal is a result: pass its `capture-result.json` with `--capture-results`; the clash is counted in the
unpictured sentence and the next drawable clash of its type is planned in its place (at most
`captureAttemptsPerType`), which is then captured the same way. The storyboard overlay applies audio; narration text it changes is
recorded as `narrationOverlaid` in `reel-map.json` and must be listed with its reason in the run's record.

## The patch search

`--patch-search` is the recorded Nexus search (`scene-policy.json` `vignettes.patchSearch`): for each pair the reel
speaks about, both Nexus pages with files and description (`housecarl_nexus_mod`) and a search of every catalogued
page for a file naming both mods (`housecarl_nexus_file_search`), each query with its tool, arguments, response
hash and date. The file also carries `knownAnswer`: the same search run for a patch known to exist (the evaluation's
supplied patch), with `found`. "No patch exists for these two mods." is said only when the known answer was found and
the pair has no `unreadSources` (a description the tool cut short, for example); otherwise "No patch for these two
mods was found." houseCARL's Nexus tools read file names and descriptions, not archive content listings, so a patch
shipped as a FOMOD option inside a hub is invisible to them.

## Stills

`reel_stills.py` draws each page (`types`, `load-order`, `statement`, `checks`) and a split's label strip with Pillow
from the page data, in a named TrueType font: `--font`, else
`HOUSECARL_REEL_FONT`, else `report-priorities.json` `reel.stillFont`. An absent font refuses in one sentence naming
the file; Pillow's built-in font is never used. `reel-map.json` records the font file's sha256 (`stillFont`) and each
still's data hash. Stills are the only frames of the reel that carry text.

## Narration

Use [narration-tools.md](narration-tools.md) to prepare reproducible local speech with `evalspeech.py`; it speaks
each reel chapter's narration verbatim and emits a storyboard copy with `narrationAudio`. The renderer refuses
unprepared narration unless `--voice "INSTALLED VOICE NAME"` explicitly selects Windows speech through the shipped
WinRT helper, and it never substitutes a voice by itself. Each scene's length is set by its audio. If the reel
exceeds `--max-seconds` (default `reel.maxSeconds`, 600), rendering refuses; it never drops scenes silently.

## Review before delivery

Review each vignette's and split's decoded frames, selected by the exact frame intervals in `reel-map.json`
(`segments[].frames[].reelInterval`), with an image-capable model **before** it sees the expected names, mods,
concern or narration; bank the frame path and hash, the exact prompt, the model and the full response, and only
then compare it with the known identities. Read the full narration once as the intended viewer: plain Skyrim words,
no internal terms, no disclaimers. Listen where playback is available; an audio stream, a waveform or a successful
synthesis does not establish listening quality. Keep identity, visibility, comprehension and defect support as
separate judgments.

## Refusal and outputs

A refused plan, missing or changed media, a vignette frame with no captured media, frames at different poses, an
evidence frame not shown to be clean (no `--report-mode` capture and no reviewed storyboard), an absent font, absent
narration audio or an over-budget reel exits 2 with one sentence. No partial reel or substitute is presented.

Outputs are `reel.mp4`, transcript-only `reel.srt`, `reel-map.json` (each scene's kind, narration, frames with their
media ids and exact frame intervals, still data hashes, the font hash, audio hashes), `reel-status.json`, the drawn
stills under `stills/` and fingerprinted segments under `cache/`. The reel is accepted only when it decodes with an
audio stream and its decoded frame count, size and frame rate match the plan.

## Older presentations

A presentation compiled before the vignette format has no `reel` block; `evalreel render` still cuts it from its
chapters and the reviewed storyboard overlay as the older contract in the repository history describes. New reels
use the vignette format.
