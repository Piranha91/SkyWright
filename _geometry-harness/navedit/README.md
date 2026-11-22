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

  Before writing, `write` plans the same edits again and refuses if they no longer come out the same. The read-back
  checks, for every navmesh the plan changes:
  - its vertices, triangles (corners, edge fields, flags), link table and door table are exactly as planned, and each
    kept triangle's cover fields are its winner's;
  - an edited navmesh's box is the used vertices' extent and its grid is `NavGrid`'s; a navmesh whose links alone
    changed keeps its winner's grid and box;
  - the patch carries no other navmesh, and every carried record equals its winner.

The profile comes from the flags, then the environment and `harness.local.json`, as for the other probes.

EDITS.json holds one edit per navmesh, each of one kind:

```json
{"edits": [
  {"navmesh": "0E6430:Skyrim.esm", "move": [{"vertex": 517, "by": [-48, 0, 0]}, {"vertex": 12, "to": [177800, -48900, 2250]}]},
  {"navmesh": "101F28:Skyrim.esm", "remove": [0], "add": {"vertices": [[25000, 29000, -5000]], "triangles": [[35, 112, 36]]}}
]}
```

An added triangle's corners index the navmesh's vertices, then the added ones (n, n+1, ...). Two edits in one plan may
not change the same navmesh; plan them separately.

## What an edit may do

**Move vertices.**
- A vertex moves together with every other index at the same position, so the mesh does not tear.
- Refused:
  - a sideways move of a vertex on an edge that links into another navmesh, or on a cell border (a height-only move is
    allowed);
  - a move that folds or collapses a triangle;
  - a move that makes a triangle overlap another on the same level, within 32 u in height (a bridge over a floor is
    not an overlap).
- Not changed: triangles, edge fields and flags, the link table, cover and door triangles, and the navmesh info map.

**Remove and add triangles.**
- **Removal is swap-remove.** The navmesh's last triangle takes the removed one's place (highest removal first), so
  only moved triangles change index. The write touches a few triangles, not every later one; closing the numbering up
  took 2,392 write steps for one triangle of `0E6430`, swap-remove takes 15.
- **Everything that names a triangle by index follows it:**
  - this navmesh's neighbour fields (one naming a removed triangle becomes an open edge);
  - its door table;
  - its own link-table entries that name itself (vanilla has these);
  - every other navmesh's links into it, from a scan of every winning navmesh on the order. A link into a removed
    triangle is cut on the other side: its entry, edge field and flag go.
- **A moved triangle is written whole:** corners, fields, flags and cover.
- **Added triangles** are appended. Each edge joins an open edge at the same full position (x, y and z; an edge
  directly above another is not its neighbour), both ways.
- Refused:
  - removing a triangle whose edge links into another navmesh, or one before a door;
  - a cut that leaves two navmeshes with no link between them (their navmesh info map entries would go stale);
  - an added triangle that is flat or under 1 u², wound against its neighbours (or, alone, against most of the mesh),
    names a missing vertex, lands on an edge that already has two triangles or a link, or overlaps a triangle on the
    same level;
  - passing 32,768 vertices or triangles.
- An added triangle has no flags and no cover.

**Every edit refuses an island navmesh**, whose own copy of the geometry in the navmesh info map would go stale. NavCut
and fills come later (`C:/hc-runs/2026-11-21-navgrid/PLAN.md`, the plan gate).

## Evidence

- `navedit --selftest`: C2513-C2529, run by `tests/test_navedit.py`. Twelve sabotage arms each turn their case red:
  - no fold, link, overlap, winding, third-triangle, door, last-link or cell-border check;
  - no coincident move;
  - no inbound re-point;
  - no self-link follow;
  - closing up instead of swap-remove.
- Real data, 2026-11-21, on the lab's vanilla profile: `0E6430:Skyrim.esm` vertex 517 moved 48 u west.
  - Planned and written; the read-back held.
  - The patch's navmesh data is byte-identical to the patch an actor walked across in game. There the rebuilt-grid arm
    arrived 4 of 6, the same as vanilla (`C:/hc-runs/2026-11-21-navgrid/LAB-RESULT.md`).
- Real data, triangles, on the same profile; each read-back held, and not yet seen in game:
  - `0E6430` triangle 3 removed and added back: 15 write steps, cover kept on the moved triangle.
  - `101F28` (USSEP's version) triangle 0 removed and added back. Triangle 241 moved into slot 0, and `101F29`'s link
    to it was re-pointed in the same patch, with `101F29`'s own grid kept. Two navmeshes written in-process, 24 s.
- The checks caught two real cases on this profile:
  - an edit built from Skyrim.esm's triangle corners when USSEP's version wins (refused: the edges were already
    shared);
  - three island navmeshes (refused).
