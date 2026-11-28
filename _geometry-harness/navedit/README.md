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
  - an existing PATCH.esp (never touched);
  - a patch whose read-back fails: it is left unwritten (see Undo below).

  Before writing, `write` plans the same edits again and refuses if they no longer come out the same. The read-back
  checks, for every navmesh the plan changes:
  - its vertices, triangles (corners, edge fields, flags), link table and door table are exactly as planned, and each
    kept triangle's cover fields are its winner's;
  - an edited navmesh's box is the used vertices' extent and its grid is `NavGrid`'s; a navmesh whose links alone
    changed keeps its winner's grid and box;
  - the patch carries no other navmesh, and every carried record equals its winner.

The profile comes from the flags, then the environment and the config `HOUSECARL_HARNESS_CONFIG` names, as for the other probes.

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
- **What it cuts:** each triangle whose heights meet the band and whose plan reaches more than 0.05 u into the outline
  (a triangle that only shares a line with it stays whole).
  - A triangle wholly inside goes.
  - A triangle partly inside is clipped into the convex pieces outside the outline, triangulated (a larger piece is
    fanned from its centre), with heights on the original triangle's plane.
- **Several outlines cut in one pass** (`NavEdit.CutMany`, an obstacle cut as convex parts). Each triangle is split
  outline by outline before anything is triangulated, and uncut neighbours are split once at the end. Cutting part after
  part instead re-cut each result, rounding new points to float every time; on rock pile `0E6420`'s 40 parts that left
  pieces that no longer joined.
- **New points are welded.** A cut point within 0.05 u of an existing point is that point: along an original edge only
  to that edge's ends and points, inside a triangle to nearby new points on the same level (a fan's centre never). Two
  outlines' lines crossing near one spot otherwise made points a float step apart, and pieces between them that turned
  over when written.
- **Hairlines.** An outline's edges are taken in an order that leaves for last any whose outside piece would be under
  0.1 u wide, so its line does not run on across the triangle; a piece still under 0.1 u wide is ground too narrow to
  stand on and goes with the hole. A piece under 0.001 u² is dropped as a sliver; any larger one is kept however thin
  (the 1 u² floor applies to triangles added by hand).
- **Nothing is left half-joined.** The cut's lines can meet edges outside the hole, so an uncut neighbour on such an
  edge is split at the same points, on all its edges at once (it can get points on two, from two outlines), and every
  point is put into the one piece edge it lies closest to.
- **Pieces** keep their triangle's flags except the link bits, and have no cover.
- **Numbering, links and doors** are the triangle edit's (swap-remove), with its refusals.
- **Refused:**
  - an outline that is not convex (cut a concave obstacle as convex parts), has under three corners, or has no area;
  - an empty band;
  - a cut that touches nothing;
  - a cut reaching an edge that links into another navmesh.
- **Cut by a placed obstacle.** `{"navmesh": K, "cut": {"ref": "0E6420:Skyrim.esm", "margin": 0}}` takes the cut from
  the obstacle (`_shared/NavCutFootprint.cs`), each step through its owner:
  - its winning placement (`PlacementExtract.RefOf`);
  - its base's model (`OverlapIndex.ModelOf`, the rule `BaseMesh` uses);
  - the mesh through this load order's files (`AssetResolver`, from `HarnessOrderCli`'s mods and archives);
  - its collision on the layers an actor meets (`CollisionLayers.RoleSoups`);
  - placed in the world (`SpatialOverlap.TransformSoup`).
- **The rule: a spot is cut where an actor standing on it would walk into the obstacle.** That is, where some of the
  collision lies between step height (48 u) and a scale-1 humanoid's controller top (108.8 u) above that spot, or the spot
  lies under the inside of a solid across that whole band.
  - **Each navmesh triangle is measured on its own plane.** Navmesh lying on a rock (vanilla drapes it over low rocks)
    is not blocked by the rock it lies on; the ground beside the rock is. A bridge and the floor under it are each
    measured on themselves, and so is sloping ground.
  - **Inside a solid:** an odd number of the collision's surfaces above the band's top, counted per connected piece
    (triangles sharing a corner). A buried rock, open at the bottom, counts as solid, and two overlapping rocks do not
    cancel.
  - **Parts.** Each triangle's blocked region, in a window round it, comes out as convex parts: the plan is cut into
    vertical slabs at every end and crossing of the collision's edges there, each gap between edges is classified at its
    middle, and blocked gaps merge while they stay convex. A gap under 0.1 u, between two edges a float step apart, goes
    with a blocked gap beside it. Each part is grown by the margin and cut from its own triangle (with a margin, from the
    triangles sharing its edges too), all in one `CutMany` pass.
  - The plan row records the mesh, where it was read from, the band, how many triangles were measured, the parts with
    the triangle each was measured on, and the area removed.
- **Why not one floor.** Measured from the navmesh under the obstacle's origin, rock pile `0E6420`'s cut (as a hull, or
  as cross-sections) took 87-100 % of the ground vanilla keeps walkable inside its hull. 674 of vanilla's 728 sample
  points there lie 48 u or more above that floor, on the rocks, and none has collision in its own band.
- **Refused:**
  - an obstacle that is deleted, starts disabled, or has an enable parent (whether it stands at load is not settled
    here);
  - a base with no model;
  - a mesh the order's files lack, or one with no collision on an actor's layers (a shrub);
  - no navmesh triangle under or beside it;
  - an obstacle that blocks no actor standing on any triangle by it.
- **Known limits:**
  - An actor is treated as a vertical line: a gap narrower than its body is kept. Give a margin to cut round it.
  - A single connected mesh that overlaps itself counts its overlap as outside, where none of its surfaces is in the
    band there.
  - A cut can leave a few thin, valid triangles: on the real rock moved onto open ground, 6 of 293 new triangles were
    under 0.05 u high (the least 0.003 u). Not yet seen in game.

**Use a version.** `{"navmesh": K, "use_version": "Plugin.esp"}` carries K as that plugin has it (houseCARL's R2: the fix for
a clobber, where a later mod's version replaced an earlier one's).
- **Carried whole:** the plugin's Data is copied onto the patch's override (a CopyFrom): vertices, triangles, flags, cover
  and doors. The grid is rebuilt.
- **Its border links are matched again** against this order's winning neighbours, by edge position, as the relink
  matches (`NavBorderMatch`, the fitted tolerance in `navownprobe/NavRuleDefaults.cs`): the one neighbour triangle with an
  open edge along each linked edge; of several, the one the link already names.
- **Every neighbour's link into K is re-pointed** to the version's triangle along that edge, in the same patch, so no
  index names the old winner's numbering (an EDGE-IDX finding).
- **Ledge links** (Ledge Up and Down) are not made by edge geometry, so they are not matched. The version's own are carried
  as they are while they name a triangle the neighbour has, and counted as unverified. A neighbour's ledge link into K is
  kept where the version's triangle of that index stands on the winner's corners.
- **The navmesh info map:** a neighbour the version gains is added to both entries (`NavRepairNavi.ForPairs`, the
  relink's rule).
- **Door references:** each must resolve in the order.
- **Masters:** the patch masters every plugin whose version of K it supersedes (each after the source in load order), so a
  re-sort cannot load it above them and let one win K again (houseCARL's ruling, 2026-11-22; the clobber rule is
  unchanged). A neighbour whose links are only re-pointed adds none. The core derives masters from what records
  reference, so `write` re-writes the patch in place with them. The text is kept in its own lane, the FormID floor and
  the atomic swap are the core's. The read-back checks the master list against the plan, in load order.
  - **Copied core code:** the Mutagen write call and staging of `WriteEngine.WritePatchStaged` (the core keeps them
    internal). `tests/test_navedit.py`'s "core write drift" case fingerprints that code, and the public pieces called,
    in the pinned core. It fails when a pin move changes them, or when the core gains its own extra-masters option (then
    use that instead).
- **Refused, by name:**
  - a plugin not in the order, the winner itself, or a plugin without a version of K (or deleting it);
  - an island whose version's plugin states no navmesh info map entry for it, or gives its island a box that is not its
    navmesh's (R7, under Islands below);
  - a linked edge that meets no neighbour triangle, or several none of which it names (the edges are listed; a link is
    never cut silently);
  - a neighbour's link into K that meets no edge of the version;
  - a ledge link out of range, or one into K that the version renumbers;
  - a version that no longer links into a neighbour that still links into K (the border would be one-way).
  - a border link the neighbour does not return (the review of R7). Each border refusal ends with what to do: carry the
    neighbour with it (`"with_neighbours": true`), or, where the plugin has no version of the neighbour, keep the winner's.
- **A set of navmeshes (R10, 2026-11-27).** A mod that re-cut several adjacent navmeshes and lost all of them cannot be
  carried one at a time: each version's borders fit only the others' versions. Two edit shapes carry them in one patch:
  `{"navmeshes": [K1, K2, ...], "use_version": "Plugin.esp"}` carries exactly those; `{"navmesh": K, "use_version":
  "Plugin.esp", "with_neighbours": true}` starts with K and adds each neighbour a border refusal names, while the plugin has
  its own version of it that the game does not already take, until nothing more can join (navownprobe's repair plan grows
  every use_version row this way). The plan row's `useVersion.members` lists every member, `useVersion.set` what was asked
  and what growth added. One planner (`NavUseVersionPlan.PlanSet`; plain use_version is a set of one, never grown):
  - a link between two members is matched against the other member's version; a link to anything else against the navmesh
    the game takes (a navmesh the plugin already wins is then its own version). A ledge link into a member from a member or
    a navmesh the plugin wins is kept as the plugin has it while its index is in range;
  - an outside navmesh linked into several members gets one merged change; the navmesh info map is changed once for the set
    (`NavRepairNavi.ForVersions`); the patch masters every plugin whose version of any member it supersedes, and each
    re-pointed neighbour's winner;
  - one refused member refuses the whole set, nothing written; in a set each sentence starts with its member. Refused too:
    a navmesh named twice, a stated member the plugin already wins or has no version of;
  - the read-back adds a two-way check on the written patch (`NavRepairReadBack.TwoWay`): every border link out of a member,
    and every link into a member from any navmesh of the order with the patch last, must be returned by the triangle it
    names (ledges are not judged). A link between two navmeshes neither of which is a member is not judged, even when the
    patch holds one of them to re-point its links: that border is as the order had it, and navownprobe's re-check files it
    (review of Gate C, 2026-11-27). In a repair plan a row whose navmesh an earlier set of the same plugin carries is planned
    "carried with" that set, with no edits of its own.
- **The navmesh info map follows the version** (2026-11-22, houseCARL's items 2 and 3). The entry's linked doors become the
  version's door table, in order, and its MergedTo and PreferredMerges together the version's link-table targets: a
  neighbour it drops is removed (by value, from the list naming it), one it gains is added to MergedTo, and the neighbour's
  own entry gains it back. These are the base game's rules (all 15,461 Skyrim.esm and 1,863 Dawnguard.esm entries). The
  read-back holds the entry the game takes, with the patch last, to the version's own record. Refused by name: an entry to
  change that no plugin's NAVI states, and a whole interior entry (the write engine cannot compose one yet). Not measured:
  which targets the base game keeps in PreferredMerges rather than MergedTo, and its PreferredMergesFlag (copied as it is).
- **Doors named.** The plan row's `useVersion.doors` names each door the version adds and each it lacks, with the triangle
  before it, door by door (a door can stand before several triangles), and `useVersion.lostNeighbours` each neighbour it
  no longer links into. `doors` lists `added` and
  `dropped`, each with the door reference, the plugin whose version of it wins (`placedBy`), `triangleBeforeDoor`, and
  where it leads (`leadsTo`: its teleport's door, that door's cell and the cell's editor ID). On NR2-V this showed that
  USSEP's versions of `04C081` and `019F24` lack Legacy of the Dragonborn's doors into `DBMJournalDungeon01` and
  `DBMVQStendarrsRefuge` (C2564).
- **The plan row** (`useVersion`) gives the source plugin, vertex and triangle counts before and after, links matched,
  changed and cut (always 0), ledge links carried unverified, neighbour links re-pointed and the navmeshes they are in,
  doors checked, neighbours gained, the NAVI change, and the rebuilt grid.
- Not yet: later edits in the same plan applying to the carried version.

**Add a door.** `{"navmesh": K, "door": {"ref": "<placed door FormKey>"}}` lists a teleport door in K's door table and in
its navmesh info map entry's linked doors (houseCARL's R9 and the linked-doors half of R6).
- **The triangle:** the one under the door's ARRIVAL MARKER (the teleport position its partner door names), nearest in
  height. In Skyrim.esm (2026-11-22) that rule gives the table's own triangle in 1,703 of 1,703 door entries (119 markers
  stand over more than one triangle). The door itself stands a median 41 u from its triangle, up to 1,483 u, so it is not
  the door's position.
- **What is written:** the table entry, the triangle's Door flag (every base-game door triangle has it, no other does),
  and the linked door. Both carry the base game's one Unknown value (`NavRepairNavi.LinkedDoorUnknown`, on all 1,703).
  An entry's linked doors are its navmesh's door table in 1,103 of 1,103 base-game entries. The geometry, grid and box
  stay the winner's, and the patch masters the door's plugin because it names it.
- **The read-back** holds the door table, the flag and the patch's NAVI (the winner's plus exactly the planned entries,
  `NavRepairReadBack.NaviCheck`). Every edit's NAVI change in a plan is checked together; two edits changing one entry
  are refused.
- **Refused, by name:**
  - not a placed object, or not a teleport door;
  - a partner that does not lead back;
  - disabled at load by the one enable rule (`EnableState`), or whose enable chain cannot be followed, unless the edit
    gives `"atLoad": "any"`. The base game lists such doors too: 166 of its 2,165 door-table entries (84 through an enable
    parent, Skyrim.esm and the DLC) are doors switched off at load, a quest-gated door among them. The row records the
    door's state either way;
  - already listed;
  - no triangle under the marker (the nearest is named, with how far off it is: graft the floor first);
  - a marker more than 800 u off its triangle (past every base-game door, 781 u);
  - an entry the NAVI winner does not state, which would be added whole, when it is an interior entry. **Known core
    gap:** the pinned write engine's compose of a whole interior entry leaves a required field null, though the same entry
    built through Mutagen writes. It is refused until houseCARL's core composes it.
- A refused door's row still names the door, its placer, where it leads and how it is switched (`enabledAtLoad`, with
  its enable parent).
- **Real data, 2026-11-22:**
  - **EP-helgi, Helgi's doors on COTN's Alva's house** (houseCARL's case): `0008AA` is disabled at load through its enable
    parent's chain (quest-gated), so it is refused. Trapdoor `000802`'s marker lies 99.9 u from COTN's cellar floor
    (triangle 52), so it is refused too.
  - **NR2-V:** 60 teleport doors are listed in no winning navmesh's table, most of them the base game's own (Update,
    Dawnguard, Dragonborn). Adding one (Dawnguard `011C65` on `011CAB`, or Dragonborn `035A4D` on `035ACB`) plans. Its
    door-table write, with the navmesh info map left out, was written and read back. With the map, both need a whole
    interior entry, so they are refused now.
- Known answers C2565 (navedit) and C2566 (`navownprobe --navrepair-selftest`); sabotage arms red
  (`C:/hc-runs/2026-11-21-navgrid/sabotage_doors.py`).

**Islands (R7, 2026-11-27).** An entry of the navmesh info map can hold island data: a box and a small mesh of its own,
the Creation Kit's simplified outline of the navmesh (not a copy of it), with the entry's first Unknown 32 exactly when it
does. On every entry of the base game's five files the island box is the navmesh's own box, bit for bit
(`tests/navi_floor.py`). The rules have one owner, `_shared/NavIsland.cs`:
- a door (and a relink) leaves the island data as it is;
- `use_version` carries the version plugin's own entry's flag and island data whole. Refused by name: a version whose
  plugin states no entry for an island navmesh, and a version whose island box is not its own navmesh's box. Two plain
  entries stay as they are;
- a height-only vertex move (and following the ground, in a repair plan) makes the island box the navmesh's rebuilt box
  and moves each island vertex in height by what the navmesh surface under it moved (at its nearest point of the navmesh
  in plan when it stands off it). This is a design choice, not a measured law. An empty mesh stays empty;
- a sideways move, a cut, or triangles added or removed: allowed on an island with an empty mesh (the box follows),
  refused by name on one with a mesh.

The read-back holds the entry the game takes, with the patch last, to the plan: its box bit for bit the box of the
navmesh the game takes, its flag agreeing with its island data, its mesh vertex for vertex.

**Undo.** Both write paths write and read back in a staging folder beside `--out` (`.navedit-stage-<process>`). The patch
(and the repair plan's `readback.json`) moves to `--out` only when the read-back holds; otherwise the output folder is
left as it was and the read-back's lines are on the console. `NAVEDIT_FAIL_READBACK=1` counts a read-back as failed, for
the suites' undo cases only.

## Evidence

- `navedit --selftest`: C2513-C2550 and C2556-C2562, run by `tests/test_navedit.py`. Sabotage arms (`C:/hc-runs/2026-11-21-navgrid/sabotage_navedit.py`
  and `sabotage_footprint.py`) each turn their case red on its own assertion:
  - no fold, link, overlap, winding, third-triangle, door, last-link or cell-border check;
  - no coincident move;
  - no inbound re-point;
  - no self-link follow;
  - closing up instead of swap-remove;
  - for the cut: no neighbour split, no shared-point insertion, band ignored, no convex check, no linked-edge check, no
    weld (C2550: the notched roof's cut leaves a sliver), neighbour split on one edge only, a shallow overlap spared, a thin piece kept, outline edges taken in order,
    no triangle filter;
  - for use_version (`sabotage_useversion.py`): no keeping of the named triangle, ambiguity taken by the nearest, no
    inbound re-point, no lost-neighbour refusal, no door check, either ledge link matched by geometry, no CopyFrom;
  - for the obstacle: the slope ignored, one floor for every triangle, no inside, inside counted over all bodies, no
    band top, margin ignored, merging without the convexity check.
- Real data, 2026-11-21, on the lab's vanilla profile: `0E6430:Skyrim.esm` vertex 517 moved 48 u west.
  - Planned and written; the read-back held.
  - The patch's navmesh data is byte-identical to the patch an actor walked across in game. There the rebuilt-grid arm
    arrived 4 of 6, the same as vanilla (`C:/hc-runs/2026-11-21-navgrid/LAB-RESULT.md`).
- Real data, triangles, on the same profile; each read-back held, and not yet seen in game:
  - `0E6430` triangle 3 removed and added back: 15 write steps, cover kept on the moved triangle.
  - `101F28` (Skyrim.esm's version wins on this profile) triangle 0 removed and added back. Triangle 241 moved into slot 0, and `101F29`'s link
    to it was re-pointed in the same patch, with `101F29`'s own grid kept. Two navmeshes written in-process, 24 s.
- Real data, NavCut, on the same profile: a 60 x 60 u hole cut from `0E6430` round the station's player start (band
  z 1700-1950).
  - 3 triangles became 25, with 12 new vertices, in 59 write steps. Written and read back.
  - Against vanilla the patch removes exactly 3,600.0 u², leaves the hole's centre and corners uncovered, and has no
    half-joined edge. Not yet seen in game.
- Real data, cut by a placed obstacle, on the same profile:
  - The shrub `0713F2` was refused (no collision on an actor's layers).
  - The rock pile `0E6420` (one connected mesh; USSEP's archive supplies it on this order): 15 triangles measured. Some
    of its collision reaches a band in the windows round them, but no blocked part of 1 u² falls on a triangle, so the
    cut is refused as having nothing to cut. Vanilla
    already keeps exactly the ground the rule keeps; an independent per-point check finds 0 of its 728 walkable sample
    points inside the rock's hull blocked. The earlier rules took all of that ground (one hull) or 87 % of it (cross-
    sections from one floor).
  - The same rock's collision moved onto open ground of `0E6430` (2,100 u west, on its own floor): 27 parts on 3
    triangles, planned in 0.3 s, no half-joined edge. An independent per-point check (each column tested directly, with
    no slabs) agrees at every one of 17,226 grid points 2.5 u apart: all 7,823 it finds blocked are cut, all 9,403 it
    finds walkable are kept (90 points within 0.3 u of its own edge are left out).
- Real data, use a version, on the same profile (2026-11-22):
  - USSEP's version of `0FC127`, which 3DNPC.esp's replaces without depending on USSEP: 4 border links matched again, 1
    ledge link carried, 5 doors checked. Written by a CopyFrom and read back against USSEP's version, cover included.
    `navownprobe --base` then finds the clobber's predicate Gone: USSEP's geometry wins.
  - Written first without masters, the patch showed as a new CLOBBERED row (3DNPC.esp re-cut by a patch that did not
    master it). It now masters 3DNPC.esp, and the re-check shows the clobber gone with no new row.
  - The other 13 clobbered navmeshes on this profile were refused, each by name: 3 islands, 5 versions with other doors,
    and 5 whose borders no longer meet the winning neighbours (Legacy of the Dragonborn's new navmeshes link into the
    winners; one through a ledge link the version renumbers).
  - **The matcher against the border rule:** each navmesh's own winning version, carried, plans in 13 of 15. The 2
    refused are exactly the 2 the base emit reports as EDGE-NOMATCH.
- The checks caught two real cases on this profile:
  - an edit built from Skyrim.esm's triangle corners when USSEP's version wins (refused: the edges were already
    shared);
  - three island navmeshes (refused).
