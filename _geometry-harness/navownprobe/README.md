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
