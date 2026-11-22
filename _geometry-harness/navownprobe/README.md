# navownprobe

Who supplies the navmesh the game will use, and whose navmesh work was thrown away getting there. It reads the records
only (no geometry test) and reports three classes: CLOBBERED (a later plugin's navmesh replaces another mod's re-cut
without declaring a dependency on it), DELETED or EMPTIED (the winning navmesh record is deleted, or arrives without
geometry where an earlier provider had some) and UNCUT (a plugin introduces obstructing references into a cell whose
navmesh nothing re-cut afterwards). The full description, and every flag, is the usage comment at the top of
`Program.cs`.

## Running it

```
navownprobe [--instance DIR --game DIR --profile NAME] [--order FILE | --enable X.esp --disable Y.esp]
            [--emit out.json] [--base emit.json] [--list N] [--plugin Name.esp] [--list-master-deletions] ...
navownprobe --moved-ref-selftest
```

The instance, game and profile come from the flags, then the environment, then the harness configuration
(`_shared/HarnessTarget.cs`). An argument the probe does not know is refused by name with exit 2 before anything is read.
A misspelt flag does not fall back to the default report.

## Deletions by the shipped game: `--list-master-deletions`

Bethesda's own masters delete navmeshes: the DLCs and Update delete base-game navmeshes they replace. Those deletions
are the shipped game, not a clash between mods, so the DELETED section counts them on a line of their own and does not
list them:

```
==== DELETED or EMPTIED navmesh (hard break) ====
   findings: <mods' deletions and emptied navmeshes>
   deleted by a Bethesda master (the shipped game; not findings): <count>
   DELETED  <navmesh> in <cell> by <mod plugin> ...
```

`--list-master-deletions` lists them after the findings, under their own heading, with each line labelled:

```
   shipped-game deletions (--list-master-deletions; not findings):
   SHIPPED  <navmesh> in <cell> by <Bethesda master> (no geometry left) - a shipped-game deletion (Bethesda master), not a mod clash
```

The findings line, the count line and the emit are the same with or without the flag. A mod's deletion is a finding in
both modes. `--plugin` filters this listing as it filters the findings. The listing is not capped by `--list`: asking for
it is asking for all of it.

## Broken edge links: one EDGE-IDX row per link

A navmesh's edge links are its crossings into neighbouring navmeshes, each naming one triangle on the far side. When a
link names a triangle past the end of the neighbour's winning version, the emit writes one EDGE-IDX row for that link,
and the row's other end carries the target triangle (`"Triangle"` on its `Alternative` record; `--base` predicates carry
it as `"OtherTriangle"`). The row's identity is the linking navmesh, the target and that triangle, so conflictdiff and
ordersweep keep every link. A patch that repairs some links of a navmesh is credited per link, and a patch that renumbers
the linking navmesh but keeps its links reads unchanged. Links into the same missing triangle are one row that says how
many ("(2 links)"). What the game does at a broken crossing is not measured; the probe judges the link table, not whether
any triangle edge reaches an entry.

conflictdiff refuses (exit 3) to compare an emit written before 2026-10-24, whose EDGE-IDX rows carry no triangle, with
one whose rows do; re-run the older arm. `--write-edge-link-fixture DIR` writes the fixture
`_geometry-harness/tests/test_navown_edge_identity.py` runs the probe on.

## Self-test

`navownprobe --moved-ref-selftest` runs the probe's own code on a synthetic load order (`_shared/MovedRefFixture.cs`).
It covers:

- the needs-witness census;
- the navmesh version history;
- the DELETED section with and without the toggle (N4);
- the command line (N5): every argument shape the tracked callers pass, and the refusals;
- the UNCUT population.

## The navmesh grid: `--navgrid-oracle` and `--navgrid-selftest`

A navmesh stores a spatial grid after its cover triangles (xEdit's "Navmesh Grid" in NVNM; the engine's
`BSNavmeshGrid`). Mutagen names the header fields and hands the cells over as raw bytes:

| field | Mutagen | type | what it holds |
|---|---|---|---|
| Divisor | `NavmeshGridDivisor` | u32 | the grid is Divisor × Divisor cells |
| Grid Size X, Y | `MaxDistanceX`, `MaxDistanceY` | float | one cell's width in x and in y |
| Bounds Min, Max | `Min`, `Max` | 3 floats each | the box the cells divide |
| Cells | `NavmeshGrid` | bytes | Divisor² cells, row by row (row = y, column = x), each a u32 count and that many u16 triangle indices |

houseCARL core's `NavmeshGridDecode` reads the cells. No format document says how the grid is filled.
`_shared/NavGrid.cs` is that rule, fitted on all 19,986 navmeshes with geometry in Skyrim.esm, Update.esm, Dawnguard.esm,
HearthFires.esm and Dragonborn.esm:

- **Bounds:** the extent of the vertices that triangles use; an unused vertex is left out. Bit for bit in 19,986 of
  19,986.
- **Divisor:** from the triangle count. It is 1 up to 16 triangles; otherwise 2 plus one per 50 triangles, at most 12
  (17-49 → 2, 50-99 → 3, … 500 and over → 12). Exact in 19,986 of 19,986. Each step's triangle range meets the next with
  no gap and no overlap.
- **Cell size:** the extent times the divisor's reciprocal, in float. Bit for bit in 19,986 of 19,986. The plain
  quotient misses the last bit in 2,037.
- **Cells:** each triangle is listed, in index order, in every cell between the cells its low and high x/y bounds fall in
  (`floor((v - min) / size)`) that the triangle itself meets in plan. Touching counts. A triangle that is flat in plan
  counts as its line.
  - 11,357 navmeshes' cells match byte for byte, and 335 more differ only within float rounding at a cell edge.
  - In 8,081 the game lists some triangles in cells they do not reach. These are 61,979 entries, 1 to 1,600 u away. No
    rule on the stored geometry picks them out; they look like a grid built before the vertices last moved, but that is
    not established.
  - In 210 the game leaves out 255 entries for triangles that do overlap a cell, and 6 entries name a triangle whose box
    does not reach its cell.
  - The rebuilt grid always lists every triangle in every cell it meets.

`navownprobe --navgrid-oracle OUT_DIR PLUGIN...` measures this. It rebuilds every navmesh's grid and compares bounds,
divisor and sizes bit for bit, and cells byte for byte under the stored header. It puts every differing entry in a class
(`order`, `edge-touch`, `game-loose`, `game-outside-box`, `game-omits`) and exits 1 if any entry is `unclassified`.

`navownprobe --navgrid-selftest [DATA]` holds the known answers (C2507-C2512, run by
`tests/test_navgrid_rebuild.py`):

- the divisor's steps;
- a hand-computed 3 × 3 navmesh, in which a rule by boxes alone fails;
- an off-by-one cell size that must fail on a cell;
- the bytes decoded back by the core decode;
- the refusals;
- vanilla `05CB2A:Skyrim.esm` reproduced byte for byte. This case needs the game's Data folder.
