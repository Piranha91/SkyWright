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

**Cut (NavCut).** `{"navmesh": K, "cut": {"outline": [[x, y], ...], "z": [bottom, top]}}` cuts the walkable surface out
where a convex obstacle stands.
- **What it cuts:** each triangle whose heights meet the band and whose plan overlaps the outline.
  - A triangle wholly inside goes.
  - A triangle partly inside is clipped into the convex pieces outside the outline, triangulated (a larger piece is
    fanned from its centre), with heights on the original triangle's plane.
- **Nothing is left half-joined.** The cut's lines can meet edges outside the hole, so an uncut neighbour on such an
  edge is split at the same point, and every point is put into each piece edge it lies on.
- **Pieces** keep their triangle's flags except the link bits, and have no cover.
- **Numbering, links and doors** are the triangle edit's (swap-remove), with its refusals.
- **Refused:**
  - an outline that is not convex (cut a concave obstacle as convex parts), has under three corners, or has no area;
  - an empty band;
  - a cut that touches nothing;
  - a cut reaching an edge that links into another navmesh.
- **Cut by a placed obstacle.** `{"navmesh": K, "cut": {"ref": "0E6420:Skyrim.esm", "margin": 0}}` takes the outline
  from the obstacle (`_shared/NavCutFootprint.cs`), each step through its owner:
  - its winning placement (`PlacementExtract.RefOf`);
  - its base's model (`OverlapIndex.ModelOf`, the rule `BaseMesh` uses);
  - the mesh through this load order's files (`AssetResolver`, from `HarnessOrderCli`'s mods and archives);
  - its collision on the layers an actor meets (`CollisionLayers.RoleSoups`);
  - placed in the world (`SpatialOverlap.TransformSoup`).

  That collision is sliced between step height (48 u) and a scale-1 humanoid's controller top (108.8 u) above the
  navmesh floor under the obstacle (taken on its own level), and its convex hull in plan, grown by the margin, is the
  outline. The band runs from step height below the floor to the controller's top above it. The plan row records the
  mesh, where it was read from, the floor, the band and the outline.
- **Refused:**
  - an obstacle that is deleted, starts disabled, or has an enable parent (whether it stands at load is not settled
    here);
  - a base with no model;
  - a mesh the order's files lack, or one with no collision on an actor's layers (a shrub);
  - an obstacle nothing of which stands in the actor's band;
  - no navmesh under it.
- **Pieces.** The collision is split into connected pieces (triangles sharing a corner); each piece is hulled and cut
  in turn (`NavEdit.CutMany`). An obstacle of separate parts keeps the ground between them.
- **Known limit: a connected concave obstacle is still cut as its hull.** Rock pile `0E6420`'s collision is one
  connected mesh, so it stays one piece. Vanilla left 673 of 2,033 sample points inside that hull walkable between
  the rocks, and the cut removes them (18,135 u²). Next: cut by the obstacle's own cross-sections in the actor band,
  split into convex parts.

**Every edit refuses an island navmesh**, whose own copy of the geometry in the navmesh info map would go stale. Fills
come later (`C:/hc-runs/2026-11-21-navgrid/PLAN.md`, the plan gate).

## Evidence

- `navedit --selftest`: C2513-C2539, run by `tests/test_navedit.py`. Twenty-one sabotage arms each turn their case red:
  - no fold, link, overlap, winding, third-triangle, door, last-link or cell-border check;
  - no coincident move;
  - no inbound re-point;
  - no self-link follow;
  - closing up instead of swap-remove;
  - for the cut: no neighbour split, no shared-point insertion, band ignored, no convex check, no linked-edge check;
  - for the obstacle outline: no band slice, margin ignored, floor ignoring the level, one hull instead of pieces.
- Real data, 2026-11-21, on the lab's vanilla profile: `0E6430:Skyrim.esm` vertex 517 moved 48 u west.
  - Planned and written; the read-back held.
  - The patch's navmesh data is byte-identical to the patch an actor walked across in game. There the rebuilt-grid arm
    arrived 4 of 6, the same as vanilla (`C:/hc-runs/2026-11-21-navgrid/LAB-RESULT.md`).
- Real data, triangles, on the same profile; each read-back held, and not yet seen in game:
  - `0E6430` triangle 3 removed and added back: 15 write steps, cover kept on the moved triangle.
  - `101F28` (USSEP's version) triangle 0 removed and added back. Triangle 241 moved into slot 0, and `101F29`'s link
    to it was re-pointed in the same patch, with `101F29`'s own grid kept. Two navmeshes written in-process, 24 s.
- Real data, NavCut, on the same profile: a 60 x 60 u hole cut from `0E6430` round the station's player start (band
  z 1700-1950).
  - 3 triangles became 25, with 12 new vertices, in 59 write steps. Written and read back.
  - Against vanilla the patch removes exactly 3,600.0 u², leaves the hole's centre and corners uncovered, and has no
    half-joined edge. Not yet seen in game.
- Real data, cut by a placed obstacle, on the same profile:
  - The shrub `0713F2` was refused (no collision on an actor's layers).
  - The rock pile `0E6420` was planned: its mesh came from USSEP's archive, as this order resolves it. 174 of its 255
    collision triangles stood in the band over a floor at z 2035.8; the 22-point hull replaced 10 triangles with 47.
    Written and read back, with no half-joined edge. Not yet seen in game.
- The checks caught two real cases on this profile:
  - an edit built from Skyrim.esm's triangle corners when USSEP's version wins (refused: the edges were already
    shared);
  - three island navmeshes (refused).
