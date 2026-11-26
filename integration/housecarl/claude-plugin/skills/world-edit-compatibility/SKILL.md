---
name: world-edit-compatibility
description: "Evaluate whether a mod that edits Skyrim's 3D world — a city or location overhaul, a quest mod that places content, a dungeon, road, or landscape mod — conflicts with the installed load order, and decide what to patch. Use when the user asks if a mod is compatible, what a new city/location/quest mod will break, whether two world-editing mods conflict, whether a patch is needed, or wants a conflict review of cells, placed objects, navmesh, or worldspaces. Load before the first conflict query — Skyrim is not uniformly winner-takes-all, and diffing runtime-merged subrecords reports noise as findings."
---

# World Edit Compatibility

## Overview

A mod that edits the 3D world — a city overhaul, a village or dungeon expansion, a quest mod that
places references, a road, landscape, or lighting mod — collides with every other mod that touched
the same ground. This skill evaluates that collision from files on disk, before or after install,
and produces a decision: install as-is, install with a patch, or don't install.

The work is a funnel. Start from what the mod touches, narrow to what is genuinely contested, and
only then spend effort on field-level adjudication. Most of what looks contested is not.

The derivation for these protocols is in the separate
[runtime-merge](references/runtime-merged-data.md), [conflict-class](references/conflict-classes.md)
and [material/controller](references/route-material-and-controller-scale.md) references.
**Before modifying a protocol, search the relevant reference first** and read its prior evidence
and limitations. Load only the reference needed for the current operation.

## First step — settle merge behaviour before diffing anything

Skyrim applies the Rule of One through `TESForm::ClearAllComponentData()`, which clears only the
data a form class explicitly clears. **Everything else survives across overrides and coalesces.** So
a handful of subrecords are not winner-takes-all, and a conflict sweep that reports them is emitting
noise dressed as findings.

| Record | Merged at runtime | What still needs a patch |
|---|---|---|
| `LCTN` | the `ACPR`→`LCEP` association range | name, keywords, parent location, encounter zone, markers |
| `NAVI` | the whole per-plugin contribution | — (`NAVM` geometry is separate and does **not** merge) |
| `DIAL` | `TIFC` info count | everything else on the topic |
| `INFO` | `PNAM` previous-INFO ordering | everything else on the line |
| `SMQN` / `SMBN` | `SNAM` child linkage | **`SMQN\QNAM` does not merge** — see below |
| `IDLE` | `ANAM` related idles | every other IDLE field |
| `WRLD` | `NAM0`, `NAM9` bounds | bounds *accumulate*; editing them can enlarge the worldspace |
| `DOBJ` | `DNAM` objects, keyed by Use ID | duplicate Use IDs still replace |

Two beliefs to correct because they cut both ways:

- **`FLST`, `LVLI`, `LVLN`, `LVSP` do not union their entries.** Additive edits from several plugins
  still need a patch. This is the most common wrong assumption in the other direction — people skip
  form-list patching because they think the engine merges it.
- **`SMQN\QNAM` does not merge.** Apply the current field rule; the contrary historical
  claim and its correction are documented in the runtime-merge reference.

Full enumeration, sources, and the reasoning: `references/runtime-merged-data.md`. Read it before
judging any `LCTN`, `NAVI`, `SMQN`, or `WRLD` conflict — the ranges are not guessable.

## Workflow

### 0 — Read this machine's evaluation settings

The evaluation's tools are SkyWright's, and this skill is SkyWright's houseCARL integration module's (the
`skywright` plugin). `SW` is the SkyWright checkout this plugin was loaded from: `${CLAUDE_PLUGIN_ROOT}/../../..`
(the plugin folder is `SW/integration/housecarl/claude-plugin/`, loaded in place, never as a copy). Python tools are `SW/integration/housecarl/<tool>.py`, and
`runcontrol` and the probes are in that Release build, where its `skywright-layout.json` puts them. Run both from
the same checkout, and do not rebuild it while an evaluation runs from it (the evaluation-reporting skill's
"Where the tools are" says why).

Run `python SW/integration/housecarl/evalsettings.py ensure` before planning. The first time on a machine it
opens a local settings window and waits for Save; afterwards it prints one line naming the settings
file. A run that cannot show a window (`--no-window`, `HOUSECARL_EVAL_UNATTENDED=1`) uses the built-in
defaults and says so. A bad settings file is refused, never replaced: report the sentence it prints.
Then read `evalsettings.py show --json` and follow it:

- **Depth** (`evaluation.depth`, default `deep`). `runcontrol plan` reads it: Deep judges every switched
  state each arm's own switch census yields (offline, per arm), and lists what the census could not
  express; Standard judges the load-time state only. A `--depth` flag overrides it for one evaluation.
- **Live switch check** (`evaluation.liveSwitchCheck`: off, ask, always; only under Deep). `always`
  makes `runcontrol plan` opt in by itself; `ask` means ask the user in chat before planning and pass
  `--opt-in runtime.state-switch-flip` only on a yes. An unattended run treats `ask` as off and says so.
- **Nexus lookups** (`evaluation.nexusLookups`). Off: do not seed or show pages; search only the
  stored catalogue, and say in the report that Nexus was not asked.
- **Models** (`models.*`). Before you delegate a record/asset check, the flip plan, image review or a
  narration rewrite, run `evalsettings.py route <key>`. If it prints an agent, start the Agent tool with
  that `subagent_type` and never pass `model`: the parameter overrides the agent's model. `session` means a
  `general-purpose` agent; `off` means skip the step. If it refuses, or the Agent tool says the agent is
  not found, report that sentence and do not run the step on another model.
- The report and video choices are read by `evalreport.py`, `evalreel.py` and `evalspeech.py`
  themselves; see the evaluation-reporting skill for `video.make`.

For placed-object overlap review, use the configuration-scene and incident workflow in
[overlap measurements](references/overlap-measurements.md). Capture an explicit dependency
baseline, required source setups and the combined setup with their own plugin and asset views;
capture a patch as a separate after-comparison. Preserve the scoped population and unknowns,
then run attribution, analogue indexing, ranking and local incident grouping. Ranking is a
review order. Inspect fixed-camera normal and identity-highlight views before assigning a
visible consequence, and keep unsupported functional or runtime questions unresolved.

### 1 — Scope the mod

Read the plugin itself first, whether or not it is installed. Two calls do it, and both read a
plugin inside a **disabled** mod or one not yet listed in MO2 — which is what makes "will this break
anything?" answerable before committing to an install:

```
housecarl_records(source="TheMod.esp", plugins={"names": ["TheMod.esp"]}, project={"form": "aggregate", "group_by": "type"})
housecarl_check(findings=["missing_masters", "injected"], plugins=["TheMod.esp"])
```

The first counts the plugin's records by type; the second lists any declared master the order lacks
and any record the plugin overrides that its master does not define (injected).
For a plugin already in the active order, drop `source=` from the first call. When two mods ship the
same filename, name the folder: `source={"file": "TheMod.esp", "mod": "<mod folder>"}`.

What to take from it:

- **Masters.** Anything declared but not installed is a hard blocker, and the check lists it.
- **Injected records.** A plugin whose overrides its installed master does not define was made for
  another build of that master when the check names a build mismatch; install the build its
  description names, or a patch for yours. Without a mismatch, the records are new in the master's
  space (some mods and the DLC do this on purpose). An injected placed reference whose base the
  order does not define either crashes its cell's load when its worldspace's large-reference list names it; unlisted,
  it loaded in game and draws nothing (measured 2026-11-16). Either way, remove it.
- **Record-type mix.** `CELL` + `REFR` + `NAVM` + `LAND` means exterior worldspace edits. `CELL` +
  `REFR` with no `LAND` usually means interiors. `LCTN`, `ECZN`, `FLST`, `SMQN` mean the mod
  registers itself into game systems, not just geometry.
- **Scale.** A few hundred `REFR` is a building; tens of thousands is a city overhaul that will
  contest ground with everything nearby.

Then `housecarl_load_order_status` to confirm where it sorts. Position decides every winner below,
so a conflict verdict is only valid for one ordering.

### 2 — Clear the hard blockers

Missing masters and broken references sink a mod regardless of how elegant the patch situation is.
`housecarl_check` (its default errors family) reports dangling references and unscannable records over
the active order.

Judge the count against a baseline, not against zero. Base-game and Creation Club content carry
permanent dangling references of their own, so an absolute count says nothing — what matters is what
the new mod adds. Split the result by the plugin that introduced it.

### 3 — Find what is genuinely contested

Scope a cross-plugin query to the mod, restrict to contested records, and count by type:

```
housecarl_records(plugins={"names": ["TheMod.esp"]}, conflicts_only=true, project={"form": "aggregate", "group_by": "type"})
```

That gives the shape of the problem in one call. Then, per interesting type, ask who is winning:

```
housecarl_records(plugins={"names": ["TheMod.esp"]}, types=["CELL"], conflicts_only=true, project={"form": "aggregate", "group_by": "winner"})
```

A type dominated by a late-loading patch is usually already handled. A type where the mod itself
wins over an established overhaul is where damage happens.

**Then ask who builds near what the mod places.** A mod whose objects are all new contests nothing —
a notice board, a camp, a player home — yet another mod may build on the same spot. `conflicts_only`
cannot see that; SkyWright's `nearprobe` can. Write the mod's own placed references to a file with
houseCARL, then ask `nearprobe` what stands near them, on the same load order houseCARL's server reads
(`housecarl_load_order_status` names it):

```
housecarl_records(types=["REFR","ACHR"], plugins={"names": ["TheMod.esp"], "defined_in": true}, to_file="<abs>/seeds.jsonl")
nearprobe --instance <MO2 instance> --profile <profile> --game <game folder> --of @<abs>/seeds.jsonl --within 0
```

`nearprobe` is a program of the module's build. To find it, read
`SW/integration/housecarl/bin/Release/housecarl-integration-layout.json`: its `program` value, with `nearprobe` put
for `{name}`, is the program's path relative to that file's folder. It prints one JSON answer on standard output
(redirect it to a file for a large mod). Exit 0 is a complete answer; exit 3 is an answer with `complete: false`,
the plugins it could not read named in `unread` and `notIndexed`; exit 2 is a refusal, one sentence on standard
error (a reference not in the order, an unreadable seeds file, a radius past the cells it reads). `--types REFR`
keeps only records whose winning version is of that type. Instead of naming the seeds file, pass each reference
with its own `--of XXXXXX:Plugin.esp`.

Each record in `records` lists its hits: the seed it stands near, the `provider` whose version reaches it (a
vanilla object another mod moved there is credited to that mod), the `gap`, and the `rule`: `sphere` for a placed object,
`primitive` for one whose reach comes from its own primitive (an occlusion plane or box, a trigger),
`cell square` for terrain or navmesh in a cell the seed's extent reaches. Read it as a screen, not a
verdict:

- **Set primitive-only matches aside** unless the question is occlusion. Occlusion planes and trigger
  boxes are invisible and very large, so they reach almost every seed; a neighbour whose every match is
  `primitive` builds nothing there anyone can see.
- **It is a superset.** A reference's extent is a sphere sized by its base's bounds, so large meshes
  reach far; `Skyrim.esm`, `Update.esm` and the mod itself always appear. What matters is each
  **other installed mod** that reaches a seed — that is a neighbour.
- **Terrain counts.** A neighbour that only edits the cell's LAND or navmesh (`cell square`) can still
  be why a published patch moves an object; do not discard it because no object is near.
- **Group by provider.** Collect `hits[].provider` across `records`: each plugin there other than the
  mod, `Skyrim.esm` and `Update.esm` reaches a seed.
- **"No neighbour" means none within the stated bound** — the cells within 5 grid cells of each seed
  and the worldspace's persistent references, as `note` says. A `--within` that reaches past those cells
  is refused, naming the radius the answer is complete to (`completeWithin`).
- **Seeds must be in the active order.** For a mod not yet installed, build an evaluation profile
  that includes it first.

Each neighbour is a pair for step 5's patch search and for the geometry run, which measures the
meshes the screen only bounds.

**One mod against a block of neighbours: one small evaluation per comparison.** An evaluation is
scoped to the plugins its setups differ by, so a single plan with a "neighbours only" setup makes
every neighbour part of the population, and the scoped probes run over nearly the whole order. Plan
each comparison as its own two-setup evaluation scoped to the evaluated mod instead, and let the
report join them. Missives against 25 neighbouring city and town mods was run as three:

| Evaluation | Setups | Answers |
|---|---|---|
| E1a | everything (USSEP + neighbours + Missives) / the same without Missives | what Missives adds to this order |
| E1b | Missives with USSEP only / USSEP only | what Missives does to the base game on its own |
| E2b | everything / everything plus the three Missives patches | what the published patches fix |

Each was scoped to Missives (2.45 hours of machine time for all three). The report is compiled from
the evaluation that carries the patch, with the others as sibling bundles:
`evalreport plan --bundle E2b --sibling-bundle E1a --sibling-bundle E1b --declare-patch ...`. At
least one sibling must hold the setup without the patch itself (E1a here); a sibling that holds only
that setup with plugins left out (E1b) is joined when its population and location scope are the
same and it ran each check with the same probe build. With E1b joined, a clash between Missives and
a neighbour that E1b also shows with no neighbour loaded is reported as coming from Missives; without
it the report can only say whether it needs the neighbour was not measured.

### 4 — Adjudicate, field by field

This is the step that separates a real finding from noise, and it is where most of the value is.

A neighbour from step 3's `nearprobe` screen has no contested record to diff. Adjudicate it by what it
puts there instead: read the matched records with `housecarl_records` (`project={"form": "fields"}` or `"everything"`) to name
the objects, and hand the pair to the geometry probes, scoped to the seeds and the neighbour's matches,
rather than sweeping the whole order.

**A contested record is not a broken one.** The Creation Kit rewrites every field of a record it
touches, so two plugins routinely carry byte-identical values for fields neither author edited. Pull
the field-level diff — `housecarl_records` with `project={"form": "delta"}` (`source=` the subject,
`versus=` the reference), or `project={"form": "tree"}` for every provider — and apply three tests in order:

1. **Is the differing field runtime-merged?** If yes, it is not a conflict. Drop it.
2. **Do the values actually differ?** Identical values across the chain is a record-rewrite
   artifact. Drop it.
3. **Is the difference semantic?** An unordered list — keywords especially — that contains the same
   entries in a different order is not a conflict. Compare membership, not sequence.

What survives all three is a real conflict. `references/conflict-classes.md` covers what each class
breaks in game, and which fields to compare for it.

The classes that matter most for world edits, roughly in order of how often they carry real damage:

- **`CELL` attributes** — an override rewrites lighting template, ambient lighting, image space,
  climate, water height and type, acoustic space, music, owner, encounter zone, and the location
  link. None of these are about where objects sit, so a purely visual inspection never finds them.
- **`NAVM`** — navmesh geometry does not merge. Keep overwrite history in the evidence index;
  an overwrite or sampled coverage loss alone is not an incompatibility finding. Report a
  separately measured mismatch with the winning scene or links, and explain the actual supported
  consequence. Read the conflict-class reference for the evidence gate and door-portal limits.
- **`REFR`** — position and scale, but also enable-parent links, linked references, ownership,
  teleport destinations, and navmesh door portals. A dropped enable-parent link leaves vanilla
  content visible through a new building.
- **`FLST`** — registration into someone else's system. Does not merge.
- **`ECZN`** — level scaling, respawn, and whether a zone ever resets.
- **`LCTN`** — keywords and parent location gate radiant quest targeting; the association lists do
  not need patching.
- **`ACHR` / `PACK`** — placed actors and the packages that send them places.

### 5 — Check whether the patch already exists

Before proposing one, look for plugins that master both sides of the conflict — that is what a
compatibility patch looks like structurally. Also check whether the mod's own distribution ships
optional patches that were never installed, and whether an installed patch is inactive.

A patch that exists but sorts too early to win is worth catching here: it will look installed and
do nothing.

A patch made for another build of a mod it joins can also look installed and miss. Check it before
qualifying it:

```
housecarl_check(findings=["missing_masters", "injected"], plugins=["ThePatch.esp"])
```

**Injected records.** A patch whose overrides its installed master does not define was made for
another build of that master when the check names a build mismatch; install the build its
description names, or a patch for yours. Without a mismatch, the records are new in the master's
space (some mods and the DLC do this on purpose). An injected placed reference whose base the
order does not define either crashes its cell's load when its worldspace's large-reference list names it; unlisted,
it loaded in game and draws nothing (measured 2026-11-16). Either way, remove it.

**Search a patch hub's files by name, rather than reading a truncated list.** Most one-off patches
live on a hub page holding hundreds of uploaded files, and `housecarl_nexus_mod files=true` has to
cut that list to stay bounded. Put the hub in the local catalogue once — `housecarl_nexus_catalog
add=<id>` — and then:

- `housecarl_nexus_file_search terms='Second Great War|SGW'` finds the files whose names carry that
  mod, across every catalogued hub, with the aliases you supply (the tool ships none: the community
  spellings are yours to give). It never fetches, and it names the hubs it searched and any you
  asked for that are not in the store.
- `housecarl_nexus_catalog page=<id> offset=… limit=…` reads that hub's COMPLETE file list a window
  at a time — the way to see an archived or optional variant the summary cut.

Showing or seeding a page asks Nexus each time by default; the stored copy is served only when Nexus
does not answer, and the answer's first line says which happened. The search never asks — it reads the
store — so seed a hub before searching it, and read the coverage section for hubs it could not read.

**A hit is a lead, not a verdict.** The source exposes no per-file requirements at all, so the other
mod names in a filename ("… - PCE - Second Great War - SFCO Patch" also names PCE and SFCO) are
things to VERIFY against the load order, never resolved requirements. Two files that both match are
two candidates; nothing collapses them. And when a page's own description says to use another mod
instead, the catalogue reports that sentence with the page it points at — note it, then check that
page yourself, because the author says which PAGE replaces this one and never which file.

Locating a download proves neither that it applies to this load order nor that it repairs the
conflict you found. The file still has to be adjudicated like any other plugin: what it contests,
which order it needs, and whether its own requirements are installed.

**Read the patch's page for a stated load order before inferring one.** Patch authors often state
the order their patch was built for ("Load order: A, then B, then this patch"; "load after";
"must load before"), and some name the symptom the other order produces. That statement is the
primary source on ordering; what the files show is the fallback. `housecarl_nexus_mod` with
`description=true` returns the page text, by id or URL. When it states an order: evaluate and
recommend that order, record where it came from (the page, the date you read it, and the versions
the statement names or the patch's own version), and report anything the files disagree with as
a disagreement between the author and the records, never by silently overriding the author. Check
the dates: a patch uploaded years before the installed versions of the mods it joins may state an
order that was true of versions no longer on Nexus, so say that the statement may be stale rather
than treating it as current. When the page states nothing about order, say so, and then — and
only then — work out from the files whether the order matters: which records the two mods
contest, whether both orders are legal, and what flips between them.

### 6 — Say what has to be regenerated

World edits invalidate generated output. LOD, occlusion, grass and parallax caches are rebuilt from
the load order, so a new city mod means re-running the generators that cover it. The houseCARL
server's instructions carry the standing rule (never copy generated output): generated plugins are
never a source to patch from, because the next run overwrites them.

Name what needs regenerating in the verdict. It is often the largest cost of installing the mod, and
the user should hear it before they commit rather than after.

### 7 — Name what only the game can answer

For object-overlap measurement or pair review, read
[`overlap-measurements.md`](references/overlap-measurements.md) first. It fixes NIF-only geometry,
complete historical/new-pair accounting, honest area/volume names and bounded visual adjudication.

For the route probes' collision-material exception and per-cell actor height, read
`references/route-material-and-controller-scale.md`. It defines which facts must travel with
the geometry and where the measured behaviour has not yet been shown to transfer.

**Say which world state was judged.** A cell reaches several: the one it loads in, and the ones a
quest stage, a scene or a trigger flips it into by enabling a marker. The diagnostic answers the
LOAD-TIME state first — every geometry read judges a reference present when its enable-parent
chain resolves enabled at load, which is what the engine does. A Deep evaluation (the default depth)
also judges every switched state each arm's census yields — each marker flipped alone and each quest
stage's joint flip — and lists the states it could not express (`unassessed.statesNotExplored`); a
Standard evaluation judges the load-time state only, and a mod that hangs references on a vanilla
enable marker is then judged against that marker's other states only when you name them (`runcontrol plan --states`). Two things follow for a report: name the state the finding is in, and, where a mod hangs
content on someone else's marker, say whether the other states were looked at. Use the flag-only
population only when reproducing historical results. For a current verdict, evaluate the
enable-parent chain; the reference's own flag does not decide its presence wherever
that reference has a parent.

**Flip the switches the mods' scripts flip, in game (opt-in).** A static state check renders the
geometry of a flipped enable marker. It cannot show what the engine does while it switches a set on.
A quest script that enables a garrison loads actors, objects and scripts in one go, and two mods that
each work alone can, together, make that moment fail: a script stall, a freeze or a crash, with no
record that looks wrong. The check `runtime.state-switch-flip` runs it: `evalcontrol plan --opt-in
runtime.state-switch-flip` (the AI-side overlay over a finished app scan) (or a saved `liveSwitchCheck: always` under Deep), then `SW/integration/housecarl/switchflip.py batch` on a scratch game instance,
then `switchflip.py export`, and `evalcontrol import` imports that export, re-reading every launch
it records; until then the check stays waiting for its live receipt. It is opt-in because it launches the game;
an evaluation that did not opt in lists it as not opted in, and that list is part of the verdict.
An opted-in arm whose census has no switch that a script drives and that touches a target (by plugin or
by a target's cells) is answered not applicable by the controller; there is nothing to launch for it.

- **What is flipped.** The census groups markers by the quest stage that flips them together. A
  script function that flips several markers in one call is flipped in the function's own order,
  with the target state the script sets; read the order from the script source (`switchprobe
  --show-script`), because the census does not group by function. The flip plan names, for each
  call, where its order was read. A call that would not change a marker from its load-time state is
  dropped and named. `SW/integration/housecarl/flipplan.py` drafts the plan from each arm's census and the
  bound scripts' `--show-script` source: one group per function branch, in source order, with every
  dropped, flagged, unmapped or unplanned call named beside it. Review the draft (exposure, which
  groups matter, the arrival cell) before `switchflip.py validate`; it is a draft, not a verdict.
- **Where.** Each group twice: with the cell holding most of the flipped members just loaded (a new
  game, one `coc`, the flip right after arrival), and with that cell not loaded. A group with an
  exterior marker arrives at an exterior cell, never an interior. The receipt records where the
  player arrived (position and exterior grid cell). Add a settled variant
  (a pause after arrival) when the immediate flip fails: it says whether waiting protects the sequence.
- **Each call is checked.** The marker's state is read back after each call. A call that does not
  reach its target (an enable-parented marker's own `Enable()` is inert) is reported as not reaching
  it and is never counted as a flip.
- **What counts as a failure.** A stall (two consecutive liveness timeouts while the game process is
  alive) or a crash (the process gone and a crash log newer than the flip). Anything else short of a
  clean completion over the whole observation window is not measured, with the reason, and is never
  a pass.
- **What counts as a finding.** At least two measured failures on an arm; a completion is at least
  two measured completions and no failure. One of each is unsettled: run another launch. Attribute as
  the static checks do: failing with the patch only is the patch; failing in the combined order but
  with neither mod alone is inter-mod; failing with one mod alone is that mod's own defect, named but
  outside an inter-mod verdict. A completion means "no failure under this condition", never safe.
- **The reading is about the order; the cause is a separate question.** Which record causes a
  failure needs isolation runs. Isolate by replacement, not by override: a test plugin that
  "reverts" records by overriding them back is itself a later override of those records. Put a
  stand-in under the suspect's exact filename, in its slot, carrying only the part under test;
  confirm in game which file loaded; keep a positive and a negative control in every batch.
- **Exposure is read from the scripts.** Say who calls the flipping function, under what condition,
  and where the player must be; many quest scripts wait until the player is away, so say which paths
  skip the wait.
- **Before the scored runs,** put the detector through a known crash (the flip plan's `canary`) so a
  broken detector cannot pass for a clean order. Measure the machine idle before every launch; the
  driver refuses a probe found by command line and the game, MO2, the recorder or a build server by
  process name.

Everything above is a statement about what files say. Some things stay unresolved on disk:

- whether a lost navmesh edit leaves anything unreachable — traversal itself has been tested in
  game (NPCs pathed normally across contested and re-cut navmesh), but coverage has not, and
  reachability is the part of the navmesh question still open
- whether two objects occupy the same space (record conflicts and geometric overlap are independent
  — two mods can edit different cells and still collide, or edit the same cell and not)
- whether a lighting change reads as broken or merely different
- whether a scene still plays when its participants have been moved apart

State these plainly rather than rounding them up to a verdict. A checked item and an unchecked one
are worth the same only if the report distinguishes them.

## Common mistakes

- **Diffing a merged subrecord.** The `LCTN` association lists are the classic trap: an override
  carries an *additive delta*, not a snapshot, so two plugins showing different counts are two
  contributions that both apply. `LC*` is the base record's static list, `AC*` an override's
  additions, `RC*` its removals. Reading a delta as a competing snapshot inverts the finding.
- **Counting contested records as findings.** Contested means "more than one plugin touched it".
  Most of those agree. Report what disagrees on a field that matters.
- **Comparing ordered lists that are unordered.** Keyword arrays get re-sorted by tooling. A
  membership comparison and a sequence comparison give very different counts, and only one is real.
- **Treating a clean record check as compatibility.** No missing masters and no dangling references
  means the mod loads, not that it coexists.
- **Patching from generated output.** DynDOLOD, Occlusion, ParallaxGen, Synthesis and similar
  regenerate per machine and per run. A fix copied out of one is stale immediately.
- **Giving a verdict without naming the load order it holds for.** Every winner above depends on
  position. Say which ordering was evaluated.
- **Inferring an order the author already stated.** If the patch page says which order it was built
  for, that statement comes first and the files are checked against it; reporting an inferred order
  as the recommendation when the page says otherwise hands the user a contradiction without saying so.

## The mandatory checklist

The checks a world-edit evaluation must account for are data, not memory: SkyWright's check registry,
the `registry` file its build's `skywright-layout.json` names (today `Data/check-registry.json`; the
controller reads that one). SkyWright also owns the severity weights its app ranks clashes by, the
`severity` file of the same layout (today `Data/workbench-severity.json`). The registry has eight families (loadability, record semantics, geometry and
placement, functional and state-dependent behaviour, environmental boundaries, patch and order
evaluation, runtime evidence), each check with its applicability trigger, prerequisites, the tool
that runs it or the reason none can, what it may claim and where it stops. A verdict accounts for
EVERY applicable check in it: completed with a finding, completed with no finding, a catalogue,
or — named, never dropped — unsupported, manual, skipped or not measurable. Several families have
no detector behind them yet. Record-level checks are available through the MCP tools and must be run
there; retain their tool requests/responses and import results through the identity-bound
`evalcontrol import` lane (the AI-side overlay's). A missing import is an execution omission, not an unavailable houseCARL capability.
The built route-cut check measures one object's obstruction; collective route connectivity across
several changes and required destinations remains unbuilt. Listing absent results as unassessed is
the honest state, and no wording in a verdict
turns an unrun check into a clear one. Applicability comes from what the plugin measurably contains;
a check whose applicability could not be measured stays open, it does not become "not applicable".

Where the harness controller is available it plans, runs, validates and finalizes this checklist from
the registry and writes one evidence bundle whose status index says what is established and what is
not (`SW/docs/evaluation-bundle.md`, and for what an AI-side evaluation adds `SW/integration/housecarl/AI-EVALUATION.md`); without it, walk the registry by hand and state each
check's status in the verdict. The controller binds every accepted receipt to the plugin order and content
the arm requested, refuses to reuse a measurement whose inputs are missing or cannot be re-checked, and
refuses to finalize or package a claim whose evidence no longer matches its receipt — so a status index that
says a check completed is one whose evidence is still there, not one whose receipt merely says so.

Use all applicable available checks for a real evaluation. Restricting `runcontrol plan --select` to a short list is
an explicit partial/demo run and must be stated near the verdict. The four record/asset checks are
not optional simply because the process runner cannot call MCP directly. Enumerate the complete
declared scope for a claimed complete check, and never substitute a limited path sample or conflict
count for a full asset-resolution or field-difference result.

## Verdict shape

For a report or video from a verified evaluation bundle, load
[`evaluation-reporting`](../evaluation-reporting/SKILL.md) and use its compiler and presentation
workflow. The title and scope name the target, the mods/load order it was compared against, and
any evaluated patch. Explain findings for Skyrim modders; put raw check IDs and execution statuses
in the linked evidence index. Videos are narrated scene walkthroughs without added on-screen text.

Report in this order, so the user can stop reading as soon as they have their answer:

1. **Verdict** — install as-is / install with patch / don't install when the evidence supports that
   decision; otherwise say what problems were found and what still prevents a decision.
2. **Blockers** — missing masters, broken references. Empty is a result worth stating.
3. **Real conflicts** — what disagrees, on which field, and which plugin wins. Name the in-game
   consequence, not just the record type.
4. **Patch situation** — exists and active / exists but inactive or mis-sorted / must be written.
5. **Regeneration required** — which generators, and roughly what that costs.
6. **Unresolved on disk** — what needs the game to settle.
7. **Checklist accounting** — which registry checks completed, which found something, and which
   remain unsupported, manual, skipped or unmeasured. "No findings in the completed checks" is the
   strongest null; it is never "compatible".

## Notes

- The evaluation is only as good as the ordering it was run against. Re-run after any sort change.
- A mod can be perfectly compatible at the record level and still look wrong, because geometric
  overlap is invisible to record conflict detection. Absence of record conflicts is not absence of
  collisions. Step 3's `nearprobe` screen finds the neighbours to check; only a geometry run settles them.
- For a mod that distributes through SPID, KID, or SkyPatcher rather than records, the conflict
  surface is different and mostly outside the plugin graph — route to `spid-authoring`,
  `kid-authoring`, or `skypatcher-authoring` for those layers.
- A mod may ship **Base Object Swapper** files (`*_SWAP.ini`, sitting directly in the mod's root,
  read by that SKSE plugin at game start) that replace, move, or switch off placed objects without
  touching a record. A cell review is incomplete until that layer is read: a swapped reference's
  record base is an object that is not there, and a verdict on it is a verdict on nothing. Recognise
  the layer by the plugin in the order and by `_SWAP` inis in enabled mods; do not read the files by
  eye — sections apply in reverse order of their names, the last line wins, filters resolve through
  locations and keywords, and a chance line yields a set — and say plainly that the swap layer is
  unread until a tool reads it.
- Dialogue carried by a world-edit mod is worth a separate look: most contested `DIAL` records are
  contested only on the merged `TIFC` count. The real dialogue conflict surface is the merged line
  order (`housecarl_records` with `project={"form": "info_order"}`) and the rules in houseCARL's own
  dialogue document (`docs/dialogue.md` in houseCARL's repository).
