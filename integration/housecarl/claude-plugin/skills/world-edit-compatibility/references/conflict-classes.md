# Conflict classes for world-editing mods

What each class breaks in game, which fields decide it, and how to adjudicate. Ordered by how often
a class carries real damage rather than noise.

Every class here is winner-takes-all unless the row says otherwise — check
`runtime-merged-data.md` before diffing `LCTN`, `NAVI`, `SMQN`, `WRLD`, `DIAL`, `INFO`, `IDLE` or
`DOBJ`.

## Contents

- [CELL attributes](#cell-attributes) — the non-geometry half of a cell
- [NAVM](#navm-navmesh-geometry) — navmesh geometry
- [REFR](#refr-placed-objects) — placed objects and their links
- [FLST](#flst-form-lists) — registration into other mods' systems
- [ECZN](#eczn-encounter-zones) — scaling and respawn
- [LCTN](#lctn-locations) — quest targeting
- [ACHR and PACK](#achr-and-pack-actors-and-their-schedules) — who goes where
- [LAND and WRLD](#land-and-wrld-terrain-and-worldspace) — terrain seams
- [What to skip](#what-to-skip)

---

## CELL attributes

**Why it matters.** A cell is treated as a container for placements, but it is also a record with
its own fields, and an override rewrites all of them. This is the largest source of real conflict in
world-edit mods and the one least likely to be found by looking at the game, because nothing is in
the wrong place — the room simply looks or sounds wrong.

**Fields to compare.** `LTMP` lighting template · `XCLL` ambient lighting and fog · `XCIM` image
space · `XCCM` climate · `XCLW` water height · `XCWT` water type · `XWEM` water environment map ·
`XCMO` music type · `XCAS` acoustic space · `XOWN` owner · `XEZN` encounter zone · `XLCN` location
link.

**Symptoms.** An interior a quest mod re-lit reverts to default lighting. A temple loses its reverb.
Water sits at the wrong height at a shoreline. Cell ownership flips and quest props become stealable,
or a merchant's stock becomes free to take.

**Adjudication.** High record-rewrite noise — the Creation Kit writes every one of these fields
whether or not the author touched them, so a large contested count is normal and a large *differing*
count is not. Compare values, not presence.

**Note on `XEZN` and `XLCN`.** A cell override can re-point an encounter zone or a location link
without touching the `ECZN` or `LCTN` record at all. Check both paths.

---

## NAVM (navmesh geometry)

**Why it matters.** Navmesh does not merge. A plugin that re-cuts a navmesh replaces it outright,
and any earlier mod's edits to that navmesh are gone. That record-level fact stands — what in-game
testing retired is the automatic inference from it to broken pathing. Preserve bare lost-edit
history in the evidence index, not as an incompatibility in a report or video. A supported finding
requires a measured mismatch between the winning navmesh and winning geometry or links: for example
an obstructed route, an active broken edge link, or an actor/package witness stranded off the mesh.
Report that mismatch; use the earlier navmesh only to explain it. A lost-coverage sample alone is
not sufficient, and a missing corroborating result is not proof that the setup is compatible.

**Fields to compare.** The geometry itself, plus the cell it belongs to and which plugin owns the
winning version. Ownership and re-cut status establish history, not the compatibility decision.
The decision needs the winning geometry/link check and its named limitations.

**Symptoms.** This class was long credited with NPCs stopping short, loitering at a threshold,
taking long detours, refusing a doorway, or warping instead of walking. Instrumented in-game
testing on ground that had lost navmesh edits observed none of that: actors traversed repaired and
still-clobbered routes alike, continuously position-sampled, with no observable difference. Treat
that list as possible at some severity, not as the expected consequence of a lost edit.

**Adjudication.** Ask who owns the winning navmesh for each cell the mod touches, and whether that
owner is the same plugin that placed the geometry standing on it. That mismatch used to be taught
as *the* failure shape; in-game testing demoted it. A successful traverse proves a practical route
across the ground — it does not prove coverage, so the honest open question is whether a lost edit
leaves something *unreachable*. That is a narrower question than "is the navmesh clobbered", it is
answerable by in-game test, and it belongs in the verdict's unresolved section — not as a finding
raised from records alone.

**Related: door links — a retired check.** A door reference carries `XNDP`, a navmesh door portal
naming a mesh and a triangle index. A re-cut navmesh can leave that index dangling, and
`housecarl_check` (errors family) reports it — correctly, as a load-order integrity issue. Do not escalate
it to a pathing finding. The engine describes a door with three independent structures, and the
reference's own link is only one of them:

| # | structure | records name | engine name |
|---|---|---|---|
| 1 | the reference's own door link — what the dangling-index check inspects | `NavigationDoorLink` (`XNDP`) | `ExtraNavMeshPortal` |
| 2 | the mesh's own list of doors beside it | `NAVM` `DoorTriangles` | `BSNavmesh::doorPortals` |
| 3 | the coarse routing index | `NAVI` `LinkedDoors` | `connectedDoorsArray` |

A dangling `XNDP` does not strand the door, because the other two structures still carry the
connection. Tested in game: doors carrying dangling links were approached and transited in both
directions under continuous position sampling, without a failure. A *dropped* door link on a
`REFR` override is a different defect — a lost graph edge — and stays on the `REFR` comparison
list.

**Do not confuse with `NAVI`**, which merges completely and needs no patching.

---

## REFR (placed objects)

**Why it matters.** The obvious half is position, rotation and scale. The half that gets missed is
the link fields — a reference is a node in several graphs, and an override that drops a link breaks
the graph silently.

**Fields to compare.** `DATA` position and rotation · `XSCL` scale · `NAME` base object · `XESP`
enable parent · `XLKR` linked reference · `XOWN` owner · `XTEL` teleport destination · `XNDP`
navmesh door portal · `XLOC` lock · `XPRM` primitive · `XEMI` emittance · `XLIG` light data.

**Symptoms.** The enable-parent case is the common one: mods disable vanilla content by parenting it
to a marker, so a lost link leaves the original building, rock or clutter standing inside the new
one. A lost teleport destination makes a door go nowhere. A lost owner makes theft legal.

**Adjudication.** Position differences are usually intentional and usually visible; link differences
are usually accidental and invisible. Weight the link fields higher when triaging.

---

## FLST (form lists)

**Why it matters.** Form lists are how mods register content into someone else's system — museum
display slots, craftable sets, radiant target pools, faction rosters. **They do not union.** The
last plugin to override a list supplies the entire list.

**Fields to compare.** `LNAM` entries, as a set.

**Symptoms.** Content that exists and is reachable but is never selected: a display case stays empty,
a craftable never appears at the forge, a radiant quest never picks the new location.

**Adjudication.** Compare membership across the whole override chain, not just winner against base.
An entry present in any earlier override and absent from the winner is lost. This class has an
unusually high real-conflict rate because additive registration is exactly what these lists are for.

---

## ECZN (encounter zones)

**Why it matters.** Controls level scaling, minimum level, respawn behaviour and whether a zone ever
resets. Two mods disagreeing here changes difficulty and whether quest-placed content survives a
cell reset.

**Fields to compare.** `DATA` — owner quest, rank, minimum level, flags.

**Symptoms.** A dungeon that scales wrongly for its intended point in progression, or quest items
vanishing because a zone that should never reset now does.

**Adjudication.** Low volume and a high real-conflict rate — if two plugins touch the same `ECZN`
they usually mean different things by it. Small enough to inspect exhaustively, which makes it a good
first class to work through when learning a load order.

---

## LCTN (locations)

**Why it matters.** Location keywords and the parent-location link gate radiant quest targeting and
`HasKeyword` conditions. A city overhaul that re-parents or re-keywords a location changes which
radiant quests can select it.

**Fields to compare.** `KWDA` keywords (as a **set** — order is not semantic and gets re-sorted by
tooling) · `PNAM` parent location · `NAM1` music · `MNAM` world marker · `NAM0` horse marker ·
`RNAM` world radius · `CNAM` map colour.

**Do not compare** the `ACPR`→`LCEP` association lists. They merge. See `runtime-merged-data.md`.

**Symptoms.** A radiant quest that never fires for a city, or fires and sends the player to the wrong
building. Fast-travel or horse-marker placement landing somewhere unintended.

**Adjudication.** Keyword-set differences dominate. Comparing keywords as an ordered list roughly
doubles the apparent count with pure noise — compare membership.

---

## ACHR and PACK (actors and their schedules)

**Why it matters.** `ACHR` places an actor; `PACK` tells it where to go and what to do there. A
world edit that moves a marker or re-parents a location can break a package without touching it.

**Fields to compare.** `ACHR`: `DATA` position · `XESP` enable parent · `XLKR` linked ref · `XLCM`
level modifier · `XOWN` owner. `PACK`: `PLDT` location · `PTDA` target · `PSDT` schedule · `CTDA`
conditions.

**Symptoms.** An NPC that stops going to work, stands in the wrong place, or fails to appear for a
scheduled scene. Failure is total rather than partial, which makes it noticeable in play but hard to
trace to a cause.

**Adjudication.** Low contested volume. Worth checking specifically when the mod moves or replaces
markers, or edits `LCTN` parenting.

---

## LAND and WRLD (terrain and worldspace)

**Why it matters.** Landscape records carry height and texture data per cell; two mods reshaping
adjacent ground produce a visible seam or a floating edge.

**Fields to compare.** `LAND`: height data and layer textures. `WRLD`: everything **except** `NAM0`
and `NAM9`, which accumulate rather than replace.

**Symptoms.** Terrain seams, gaps under buildings, floating or buried objects at a boundary.

**Adjudication.** Editing `WRLD` bounds is actively risky — because they accumulate, a well-meant
edit can enlarge the worldspace rather than correct it. Leave them alone unless there is a specific
reason.

---

## What to skip

Report these as benign rather than as findings, and say why — a user who has seen them flagged
elsewhere will otherwise assume they were missed:

- `NAVI` — merges completely.
- Dangling `XNDP` door-portal indexes — `housecarl_check` (errors family) reports them correctly as an
  integrity issue, but doors carrying them work in game; two other engine structures still carry
  the connection. See the NAVM section.
- The inference from a lost navmesh edit to broken pathing — keep the lost edit in the evidence index; in-game
  testing found no traversal difference between repaired and still-clobbered ground. Whether a
  lost edit removes *reachability* is untested in general and belongs under "unresolved on disk".
- `LCTN` `ACPR`→`LCEP` — merges; those are deltas, not snapshots.
- `DIAL` `TIFC` — bookkeeping the engine recalculates. Most contested dialogue topics are contested
  on nothing else.
- `INFO` `PNAM` — ordering is reconstructed at runtime.
- `SMQN` / `SMBN` `SNAM` — child linkage merges. `SMQN\QNAM` does not.
- `IDLE` `ANAM`, `DOBJ` `DNAM`, `WRLD` `NAM0`/`NAM9` — merge or accumulate.
- Any record whose contested values are identical — a Creation Kit record-rewrite artifact.
- Unordered lists differing only in sequence.
- Generated plugins. The houseCARL server's instructions carry the rule: never copy generated output.
