# workbench_state and workbench_act, argument by argument

The app's endpoint serves these two tools and nothing else (`_geometry-harness/workbench/StateTools.cs`, served by
`_geometry-harness/workbench/StateServer.cs`). Each argument below is the tool's own; the descriptions the tools publish say the same in fewer
words. Refusals are quoted as the code writes them; `{x}` is a value filled in.

## workbench_state (read only)

Always answers with `window`: `{cell, loading, banner, status, openConflict}`. Add parts with `project`:

| `project` part | What it adds |
|---|---|
| `bundle` | the evaluation bundle open: folder, plan id, arm, targets, the verify line, notes on what it does not show |
| `conflicts` | the ranked clashes the Conflicts tab lists: `{total, listed, hiddenDecided, matched, offset, limit, rows[]}`; a row has `rank, open, kind, a, b, ref, refBase, refType, other, otherBase, cell, summary, physical, evidence, isNew, severity, verdict, reason, standing` |
| `selection` | `openConflict` (its row) and `selection`: the selected reference's `drawn`, `loadOrder` and `written` poses, its parts, staged edits and what the view shows |
| `pending` | each staged edit (`ref, sentence, before, after, moved`) and each written one (`ref, patch, pose`) |
| `recheck` | the last re-check: running, refused, the row's word and sentence, rows it created, the verdict |
| `repair` | the last previewed navmesh repair and its written patch |
| `findings` | the Findings tab: every other selected check's rows, ranked, each saying whether it opens the viewer |
| `launcher` | manual mode: instance, profile, game, the profile's mods, the probes, Output and Work Folders, the run |
| `view` | originals shown, ghost, hidden and see-through references |
| `decisions` | each clash's decision and whether it still applies |
| `scenes` | the clashes by cell, in clusters and groups (needs the grouping data `scene-policy.json`) |
| `context` | the selected reference's surroundings: doors near it, what names it, its enable parent, what rests on it, the navmesh under it, what stays behind a move (`"reading"` while it reads) |
| `placements` | `find_clear`'s candidate positions and their re-check words |
| `scene` | what the 3D view shows or is loading |
| `results` | the Results tab of a finished evaluation (`limit`/`offset` page its rows) |
| `world` | the World tab; with `cell`, that cell's records |
| `layers` | the 3D view's toolbar layers |
| `load_order` | the load-order panel |
| `live` | adding and dropping cells around the view |

With no `project`, the parts are `selection`, `pending`, `recheck`. Other arguments: `limit` (default 20) and `offset`
(default 0) page `conflicts`, `findings`, `results`, `world`; `filter` keeps rows whose kind, plugins, references or
base EditorIDs contain the text; `check` keeps one check's Findings rows; `include_decided` lists clashes decided
intentional; `include_findings_only` lists cells with only Findings rows; `cell` names a cell for `world`;
`wait_seconds` (at most 600) waits for a running re-check, `find_clear` trial or launcher run before answering.

Refusals: `'{part}' is not a part of the state; project any of bundle, conflicts, findings, selection, pending, recheck,
repair, launcher, view, decisions, scenes, context, placements, scene, results, world, layers, load_order, live.` and
`limit must be 1 or more and offset 0 or more.`

## workbench_act (one action)

`verb` is required; the other arguments belong to the verbs named below. A success answers `{said, ...}` with the state
the verb changes; a refusal is one sentence. An unknown verb: `'{verb}' is not a verb; use select, stage, undo, revert,
drop, view, decide, commit, recheck, move_with, find_clear, choose, repair_preview, repair_write, layer, frame_log,
load_order, map, results, world, open_profile, select_mods, select_probes, set_output, set_work_folder, run, abort,
capture_window, show_tab, search_mods or open.`

**Opening things** (answer `{said, launcher}`):

| Verb | Arguments | Notes and refusals |
|---|---|---|
| `open_profile` | `mode` (`free` or `housecarl`), `instance` (full path), `profile` (default: the instance's selected), `game` | `open_profile in free mode needs instance, the MO2 instance folder by its full path.`; `'{inst}' is not an MO2 instance: it has no ModOrganizer.ini and no profiles folder.` |
| `set_output`, `set_work_folder` | `path` | where patches and run folders go. Choose the Output Folder **before** opening a bundle: an open scene keeps its write target |
| `open` | `path` | a bundle folder, an evaluation results folder, a clash list, an MO2 instance or profile folder, or a game or Data folder. `'{p}' is not a full path; give the folder or file by its full path.`; `'{p}' does not exist; choose a folder or file that does.` A bundle needs its profile open first |
| `select_mods`, `select_probes`, `search_mods`, `run`, `abort`, `show_tab`, `capture_window` | `mods`/`how`, `checks`, `search`, —, —, `tab`, `path` (an absolute `.png`) | the Start tab's own controls; `run` measures the ticked mods with the ticked probes (`wait_seconds` on `launcher` follows it) |

**Looking:**

| Verb | Arguments | Notes and refusals |
|---|---|---|
| `select` | exactly one of `conflict` (a rank), `ref` (`XXXXXX:Plugin.esp`, or `none`), or `scene` (a number from `scenes`), or a `finding` rank | `select takes exactly one of conflict (a rank) or ref (a reference).`; `no conflict ranked {n} is listed; read workbench_state with project ["conflicts"] to see the ranks listed.` |
| `view` | `op`: `show_original`, `show_edited`, `ghost_on`, `ghost_off`, `hide`, `hide_part` (`part`), `hide_in_front`, `see_through` (`ref`, `mod` or `plugin`), `isolate`, `restore` | answers `{said, view}` |
| `layer` | `layer` (navmesh, borders, markers, planes, terrain, water, grass, trees, clutter, statics, marks, boxes, outlines), `show` | |
| `load_order` | `plugin`, `show` (and `ref` for one object) | switches a plugin off or on in the view |
| `results`, `world`, `map` | `pair`/`groups`/`categories`/`setup`/`search`/`row`; `cell`/`ref`/`search`; `worldspace`/`map_centre`/`pixels_per_cell`/`textured` | the Results, World and map views (read-only scenes) |
| `frame_log` | `path` (absent: stop) | |

**Editing** (refused on a read-only scene: `this scene is read-only (it was opened from the Results or World tab): nothing is moved or written here.`):

| Verb | Arguments | Notes and refusals |
|---|---|---|
| `stage` | `delta` `[dx,dy,dz]` and/or `turn_degrees`; or `position` and/or `rotation_degrees`; or `offset` (`"+120 x"`, `"15°"`); `axis`, `snap`, `angle_snap`; `ref` (default: the selection) | `nothing is selected; select a reference first, or name it with ref.`; `stage needs a move or a turn: …` |
| `drop` | `ref` | lowers onto drawn surfaces (render triangles), not collision |
| `move_with` | `refs` | moves those references with the selected one |
| `find_clear`, `choose` | `max` (1 to 6), `snap`; `n` | candidate positions judged by the open clash's re-check |
| `undo` | — | the last staged edit: `there is no staged edit to undo.` (with a written edit: `…; a written edit is undone with revert, which removes it from its patch.`) |
| `commit` | `patch` (a new plugin name; `.esp` is added), `dry_run` (default true) | `commit needs patch, the new plugin's name (for example WbFixBarrel).`; a dry run answers `dry run, nothing written: …`; a write answers `written: …` and starts the open clash's re-check |
| `recheck` | `patch` | `re-checking conflict {n} against {patch} (about 70-80 s); read workbench_state with wait_seconds 150 for its result.` |
| `revert` | `ref` (default: the selection) | drops staged edits, or removes a written edit from its patch |
| `decide` | `conflict`, `verdict` (`real problem`, `intentional`, `unsure`), `reason` | needs a Work Folder and a bound instance and profile; opens the next undecided clash |
| `repair_preview`, `repair_write` | `finding` (a navmesh Findings rank), `patch` | navmesh repair |

A write needs a write target: the launcher's instance, profile, game and Output Folder when the bundle was opened, or
`WB_INSTANCE`, `WB_PROFILE`, `WB_GAME_DATA` (and `WB_OUTPUT`). Without an Output Folder: `writes to instance '{x}',
profile '{p}' are refused: no Output Folder is chosen; choose one in the launcher or set WB_OUTPUT (the instance's mods
folder itself is allowed).`
