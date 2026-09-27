# leftbehindprobe — left-behind occlusion panels

For a mod combination, was the geometry each occlusion panel was authored against moved, removed, disabled
(directly or through its enable parent), base-swapped or replaced by the mod under test? An occlusion panel whose
wall is gone culls whatever is behind it with nothing left to hide it, so the player sees objects pop out.

## Usage

```
leftbehindprobe --self-test
leftbehindprobe --base-order BASE.json --city-order CITY.json --panel-plugin OCCLUSION.esp --json OUT.json
                --instance DIR --game DIR --profile NAME [--require-instance DIR] [--panel-list PANELS.json]
```

- `--base-order` / `--city-order`: order-candidate files (the `HarnessOrderCli` shape: `BaseProfile`, `Plugins`,
  `Mods`). The base order is the city order without the mod under test; its plugins must be a subset of the city's.
  Each arm reads only its own plugins and its own `Mods` list as the asset view (a mesh the mod under test replaces is
  seen in the city arm only); the run refuses if the resolved asset view is not exactly the order's `Mods`.
- `--instance`, `--game`, `--profile` are required; nothing falls back to the harness config. The run refuses unless
  the game directory is the one the MO2 instance's `ModOrganizer.ini` names, the profile is the `BaseProfile` of both
  order files, and (with `--require-instance`) the instance is that directory.
- `--panel-plugin`: panels are every placed reference with an XPRM primitive that this plugin defines or overrides.
  `--panel-list` (a JSON object with a `panels` array of `{formKey}`) restricts to that list and refuses if a listed
  key is not a panel in the records.
- Output: a JSON file (`housecarl.leftbehind.classes/1`) with one row per panel (each with `gainedBy`, empty unless
  BACKING-GAINED), the class counts, the counts at 81 threshold combinations (each with a `BACKING-GAINED` count), and the run's identity (`dllSha256`, `exeSha256`, commit, argv). The console log carries
  each arm's plugins, asset view, archives and Base Object Swapper inis.

## The rule

Each arm is its order's **winning** state. For each panel:

1. **Samples**: a PortalBox's mid-quad on a 9 × 9 cell-centred grid, a Box's volume on 5 × 5 × 5 (the panel shape as
   `StationRule` draws it; XPRM bounds are full extents). Other primitive types are not modelled (CANNOT-ASSESS).
2. **Backers** per sample and arm: an occluder reference whose mesh surface (render plus solid collision) is within
   δ, or whose closed collision solid contains the sample (odd number of crossings of the vertical line above it), or
   the terrain when the sample is at most δ above it. The nearest backer gets the credit. A base with no readable mesh
   falls back to its OBND box, labelled `obnd`.
3. **Occluders**: present at load, base not marker-flagged, `Physical` by `DetectorModel.ClassifyDependent`, of type
   Static, MoveableStatic, Door, Activator, Furniture or Container, with a world box at least 64 u in two extents.
4. **Enable state**: the load-time chain (`EnableArms.Winner` → core `EnableState`): the XESP parent replaces the
   own flag, opposite-state honoured. A chain that cannot be read to the end is **unknown**. The panel is classified
   with unknowns present and absent in each arm; if the class differs, it is CANNOT-ASSESS.
5. **Class** (first match): CANNOT-ASSESS (not measurable; the panel itself changed; a changed occluder-type reference
   within 1,024 u has no readable geometry; depends on unknowns) → BACKING-GAINED (b_B < τ_pair and b_C ≥ τ_pair) →
   UNPAIRED (b_B < τ_pair) → LEFT-BEHIND (lost ≥ τ_lost) → PARTIAL (lost ≥ τ_minor) → RE-BACKED (credit moved to a new
   or base-swapped reference for ≥ τ_lost of the backing) → INTACT. Proposed δ = 64 u, τ_pair = 0.5, τ_lost = 0.5,
   τ_minor = 0.2.
   - **BACKING-GAINED** (2026-09-27): nothing stood behind the panel in the base arm, and the city arm built up to it —
     the panel now culls whatever the city put there. `gainedBy` credits the samples unbacked in B and backed in C to
     their city backers, each with how the city arm produced it: `new`, `moved`, `enabled`, `parent-enabled`,
     `base-swapped`, `mesh-replaced`, `terrain`, `unknown-enable` (or `unchanged`). The credit is reported evidence,
     not a threshold, but it is checked: a gain credited to nothing the city arm changed (empty, or only `unchanged`)
     is CANNOT-ASSESS with a reason, never emitted as BACKING-GAINED. The class names one mechanism by which a panel
     comes to hide a mod's content, not every one: a panel already backed in the base arm can hide new content too,
     and stays INTACT here.
6. **Cause** per partner: deleted, unknown-enable, parent-disabled, disabled, base-swapped, moved (> 0.5 u, or any
   rotation or scale change), mesh-replaced, unchanged; `terrain` for the LAND partner when its heights differ.

Limits, stated: the Base Object Swapper layer is not applied (the log lists any `_SWAP.ini` in either view); terrain
height is bilinear between LAND vertices; trees, lights and clutter-sized objects never back a panel.

## Tests

`--self-test` runs synthetic fixtures (no game data): three-plugin orders written through `OverlapIndex.Record`, box
geometry through the same per-base provider the real run fills from meshes, classified by `LeftBehindRule.Run`, the
path the real run uses. Each case names what it pins (shape, each class, each cause, the enable-chain cases, the
unknown rule, the blind-reference rule). A change to the rule should turn at least one case red; check it by
sabotaging a copy of the source, never the live file.
