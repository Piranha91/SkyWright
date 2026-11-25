# Runtime-merged data — the exceptions to the Rule of One

Skyrim is winner-takes-all for almost everything. The exceptions are mostly *specific subrecords*,
not whole record types, and they matter in both directions: diffing a merged subrecord manufactures
findings, and assuming a merge that does not exist silently skips a patch that was needed.

There is no Bethesda specification for any of this. The evidence is engine reverse-engineering plus
controlled in-game testing, cited per row below.

## The mechanism

When the game loads an override, it asks the existing form to clear its data — but only the data
that form class explicitly clears is actually removed. Everything the class does not clear survives
and **coalesces** across every plugin that touched the record.

> "Data will be exempt from the Rule of One in every case where Bethesda does not consciously apply
> the rule."
> — David J Cobb, *How Skyrim loads forms*, <https://davidjcobb.github.io/articles/how-skyrim-loads-forms>

That is why the exception list looks arbitrary: it is not a designed merge feature, it is the
negative space left by which fields `ClearAllComponentData` happens to handle.

## The table

| Record | Merged | Limitation |
|---|---|---|
| `DOBJ` | `DNAM` Objects | Entries are keyed by Use ID; a duplicate Use ID still replaces. |
| `DIAL` | `TIFC` Info Count | The engine keeps or recalculates the count. This is bookkeeping — the rest of the topic is winner-takes-all. |
| `INFO` | `PNAM` Previous INFO | Ordering is reconstructed at runtime. All other INFO data needs resolution. |
| `SMQN` | `SNAM` Child | **`QNAM` Quests does NOT merge** — see the correction below. |
| `SMBN` | `SNAM` Child | Child-node linkage only. |
| `LCTN` | the `ACPR`→`LCEP` range | The location's name, keywords, parent location, encounter zone and map-marker data remain winner-takes-all. |
| `IDLE` | `ANAM` Related Idle Animations | Every other IDLE field needs resolution. |
| `WRLD` | `NAM0`, `NAM9` bounds | *Accumulated*, not replaced. Conflicting edits can enlarge the resulting bounds unexpectedly. |
| `NAVI` | the entire per-plugin contribution | The clearest whole-record exception: every plugin's navigation-map data consolidates. |

## The LCTN range, enumerated

`ACPR` `LCPR` `RCPR` `ACUN` `LCUN` `RCUN` `ACSR` `LCSR` `RCSR` `ACEC` `LCEC` `RCEC` `ACID` `LCID`
`ACEP` `LCEP`

These are the reference-association lists — which actors, markers and enable points belong to a
location. They follow a **Static / Added / Removed** protocol that is easy to misread:

- `LC*` — the defining record's own static list
- `AC*` — an override's *additive* contribution
- `RC*` — an override's *subtractive* contribution

So a base plugin writes `LC*` and every override writes `AC*` / `RC*`. Two overrides carrying
different `AC*` counts are **two deltas that both apply**, not two competing snapshots. Record
libraries that surface these as `…Static` / `…Added` / `…Removed` fields make the protocol visible;
the raw subrecord names hide it.

Everything outside this range on an `LCTN` is ordinary winner-takes-all: `FULL`, `KSIZ`/`KWDA`,
`PNAM`, `NAM1`, `MNAM`, `RNAM`, `NAM0`, `CNAM`.

## Two corrections worth carrying explicitly

**`SMQN\QNAM` does not merge.** Older community references state that it does. Controlled testing
demonstrated that a third plugin's `QNAM` eliminates a second plugin's quest additions, and xEdit
changed its conflict classification in **4.0.5 — "#1016 — [TES5/SSE] Quests/QNAM in SMQN shouldn't
show as benign conflict"**. Cite the issue rather than the older threads:
<https://github.com/TES5Edit/TES5Edit/issues/1016>

**`NAVM` does not merge; `NAVI` does.** Overridden navmesh geometry still needs a winning version
and still needs conflict resolution. It is only the navigation-map index each plugin contributes
that consolidates at runtime. Conflating the two leads to skipping navmesh patches that are needed.

## Not merged, despite common belief

`FLST`, `LVLI`, `LVLN` and `LVSP` do **not** union their entries. When several plugins make additive
edits to a form list or a leveled list, the last one wins outright and the others are lost — a
compatibility, Bashed, Smashed or Synthesis patch is required. This is the most consequential
misconception in the list, because it causes patches to be *skipped* rather than merely wasted.

Also winner-takes-all despite occasional claims otherwise: `RACE` records, `VMAD` script attachments,
and `WNAM` region areas.

## Sources

- David J Cobb, *How Skyrim loads forms* — the mechanism.
  <https://davidjcobb.github.io/articles/how-skyrim-loads-forms>
- AFK Mods, *TES5Edit — records that merge at runtime* — the enumeration. Outdated on `SMQN\QNAM`.
  <https://www.afkmods.com/index.php?/topic/3940-skyrim-classicle-tes5edit-records-that-merge-at-runtime/>
- STEP, *Which record types don't need conflict patches* — the `LCTN` subrecord enumeration.
  <https://stepmodifications.org/forum/topic/4468-tes5edit-which-record-types-dont-need-conflict-patches/>
- TES5Edit issue #1016 and the xEdit 4.0.5 changelog — the `SMQN\QNAM` correction.
  <https://github.com/TES5Edit/TES5Edit/issues/1016>
