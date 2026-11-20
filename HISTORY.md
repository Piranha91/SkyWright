# Where this history comes from

SkyWright was cut from Piranha's houseCARL fork (branch `piranharim-harness`) at commit `461681f7` on 2026-11-20, with `git filter-repo`. Every commit that touched a moved path is kept with its message, author and date; the other paths dropped out. The history runs from 2026-08-27.

| houseCARL path | SkyWright path |
|---|---|
| `_geometry-harness/` | `_geometry-harness/` |
| `scripts/planeassess/` | `_geometry-harness/planeassess/` |
| `.claude/skills/world-edit-compatibility/references/check-registry.json` | `data/check-registry.json` |
| `.claude/skills/world-edit-compatibility/references/workbench-severity.json` | `data/workbench-severity.json` |
| `.claude/skills/world-edit-compatibility/references/record-field-scope.json` | `data/record-field-scope.json` |
| `.claude/skills/world-edit-compatibility/references/overlap-triage-policy.json` | `data/overlap-triage-policy.json` |

Commit hashes differ from houseCARL's, because each rewritten commit holds less. To find a commit's houseCARL original, search houseCARL's log for the same subject and date. git's rename detection found no file renamed into these paths from elsewhere, so no file's history starts at a rename.

houseCARL itself is the submodule `external/houseCARL`, at the commit its gitlink names.
