# navedit

Edits a navmesh and writes the edit into a new patch plugin. This is the command line houseCARL drives (Piranha chose
it on 2026-11-21) and the Workbench will call. The edit model is `_shared/NavEdit.cs`; the grid it writes is
`_shared/NavGrid.cs`'s rebuild, never patched by hand.

## Running it

```
navedit plan  --edits EDITS.json --out PLAN.json [--instance DIR --game DIR --profile NAME]
navedit write --plan PLAN.json --out PATCH.esp
navedit --selftest
```

- **`plan`** reads the winning version of each named navmesh on the profile's load order. It plans the edits and writes
  PLAN.json; it writes nothing else. Each navmesh comes out `planned`, with the write ops, or `refused`, with one
  sentence per reason. Exit 0 when all are planned, 1 when any is refused, 2 when the inputs cannot be read.
- **`write`** writes the plan into a new patch plugin through houseCARL core's write engine, in this process, with no
  server. Each carried cell and worldspace is forwarded from its winner, and the patch is read back. Exit 0 only when
  the read-back holds. It refuses:
  - a plan made on a different load order;
  - a plan with any refused edit;
  - an existing PATCH.esp.

  The read-back checks:
  - the navmesh holds exactly the planned vertices, with triangles and links unchanged;
  - the box is the used vertices' extent, and the grid is `NavGrid`'s for those vertices;
  - every carried record equals its winner.

The profile comes from the flags, then the environment and `harness.local.json`, as for the other probes.

EDITS.json:

```json
{"edits": [{"navmesh": "0E6430:Skyrim.esm",
            "move": [{"vertex": 517, "by": [-48, 0, 0]}, {"vertex": 12, "to": [177800, -48900, 2250]}]}]}
```

## What an edit may do (this release: moving vertices)

- A vertex moves together with every other index at the same position, so the mesh does not tear.
- Refused:
  - a sideways move of a vertex on an edge that links into another navmesh, or on a cell border (a height-only move is
    allowed);
  - a move that folds or collapses a triangle;
  - a move that makes a triangle overlap another on the same level, within 32 u in height (a bridge over a floor is
    not an overlap);
  - an island navmesh, whose own copy of the geometry in the navmesh info map would go stale.
- Not changed: triangles, edge fields and flags, the link table, cover and door triangles, and the navmesh info map.

Adding and removing triangles, NavCut and fills come later
(`C:/hc-runs/2026-11-21-navgrid/PLAN.md`, the plan gate).

## Evidence

- `navedit --selftest`: C2513-C2519, run by `tests/test_navedit.py`. Five sabotage arms each turn their case red:
  no fold check, no link check, no overlap check, no coincident move, no cell-border check.
- Real data, 2026-11-21, on the lab's vanilla profile: `0E6430:Skyrim.esm` vertex 517 moved 48 u west.
  - Planned and written; the read-back held.
  - The patch's navmesh data is byte-identical to the patch an actor walked across in game. There the rebuilt-grid arm
    arrived 4 of 6, the same as vanilla (`C:/hc-runs/2026-11-21-navgrid/LAB-RESULT.md`).
