# viewprobe — the "see what the tester will see" renderer

**Class:** LIVING. Must reflect current HEAD — update it in the same commit that changes viewprobe's
behaviour, per `standards/HOUSECARL_DOC_HYGIENE.md` §1.

Textured, first-person rendering of a Skyrim cell straight out of an MO2 profile, with the winning
navmesh drawn over it. Built for the geometry-deconfliction work: a detector says two plugins
disagree about a navmesh, and this answers "what does that actually look like from the floor".

This file is the **generalizable** reference — CLI, conventions, feature state, known limits.
Instance-specific material (a particular load order's reference scenes, lab profiles, and the
findings they demonstrate) lives with that instance, not here.

---

## Code and build

- **Project:** `_geometry-harness/viewprobe/`. `Program.cs` (CLI, load-order walk, scene/terrain/nav
  assembly, compare orchestration), `TexturedScene.cs` (GL rendering, captions, adaptive colour),
  `InteractiveViewer.cs` (the `--interactive` fly-through: window loop, hover picking and the
  click-lock, the live navmesh-display and height-clip state, HUD, worklist paging),
  `BatEmit.cs` (`--emit-bats`: its own compact load-order walk, cell identity and navmesh only, so
  it is multi-cell for free and never touches the render pipeline). The worklist MODEL is shared —
  `_shared/Worklist.cs` — because a detector could emit one directly.
  Program.cs's per-cell work lives in a local function, `RunCellTextured`, so a worklist can call it
  once per cell; the load order, asset resolver and conflict parsing are resolved above it, once.
- **Rendering library:** `CharacterViewer.Rendering` (project reference, currently an absolute path
  to a sibling checkout). It supplies `NifMeshBuilder` (NIF → Y-up meshes with texture paths),
  `GlRenderer`/`GlMesh`, `GlTextureManager` (DDS load + white fallback), `OrbitCamera`. viewprobe
  wires adapters over houseCARL's `AssetResolver` rather than reinventing them.
- **Build:** `dotnet build -c Release`; run with `dotnet run -c Release --no-build -- <args>`.
  The built exe is `bin/Release/net9.0-windows10.0.19041.0/viewprobe.exe`, and it is
  self-sufficient there — worklists can be dropped straight onto it (see *Worklists*), and it finds
  its instance/profile the same way every other run does.
  Never edit sources while a background `dotnet run` is compiling — that has produced corrupted
  concurrent builds.
- **Verification helper:** `tools/imgcheck.py` — `crop` / `panel` / `diff` / `hist`. Every visual
  claim in this file was made with it.

---

## CLI reference (first positional arg = CellEditorID, FormID, or a worklist `.json`)

### Clean report and video capture

For controlled subject-view diagnostics, add `--ref-surface` and highlight one `--ref` at a time.
This opt-in mode overlays the original placed opaque triangles with depth testing, preserves the
normal texture path, and omits the usual inflated outline and through-occluder ghost. Unsupported
alpha, effect, marker or non-depth-writing shapes emit `subject-surface-refused` and leave that
participant's coverage incomplete. A coloured portion of a model alone cannot qualify its contact
elsewhere. Keep normal and independent participant/elsewhere repeats and use
`../subject_gate.py` for source-bound post-render qualification with a normal-view witness.
Capture success, diagnostic qualification and supported consequence are separate states.

`--ref-surface-opacity V` (opt-in, `0 < V <= 1`, requires `--ref-surface`) composites the same
depth-tested placed triangles at opacity `V` over the subject's **own material** instead of filling
them with flat colour. Everything else is unchanged: the same triangles, the same depth test, the
same back-face culling, the same refusals, no inflation and no through-occluder ghost. `1` is the
default and is pixel-exact with the flag omitted, so an existing capture is unaffected.

A marked pixel lands at `V*colour + (1-V)*material`, which is measurable: solve for `V` per pixel
against the normal frame and it must return what was asked. Two properties are worth knowing before
using it.

- **`V` is the REQUESTED opacity, not a guaranteed per-pixel result.** On simple subjects a marked
  pixel does land on the prediction, but this is not universal, and highlighting a single reference
  does not guarantee it — a geometrically complex reference can read stronger than `V` over part of
  its area.
- **Where two marks cover the same pixels, the nearer reads stronger than `V`.** Measured by
  highlighting each reference alone and then together: the deviation appears only when the other
  mark is present. An opaque mark cannot show this at all, so it is new to the translucent mode.
  Measure the frame if the exact delivered value matters.
- Overlap between two marks is a fact about the **picture**, not about where the objects are in the
  world. It is not evidence of contact between them.

The mode is **not opaque identity evidence** and says so: below opacity 1 the renderer stops
printing the `subject-surface-mode: exact placed opaque triangles` line that `../subject_gate.py`
requires, and the gate additionally refuses `--ref-surface-opacity` among its frozen render
arguments. Use it to make a diagnostic frame legible to a human reader, not to qualify a pair.


Pass `--report-mode` with any textured still, compare, worklist, or interactive run to preserve the camera,
projection, canvas, crop, scene geometry and diagnostic overlays while removing every text surface from the
pixels. Static PNGs omit caption bars, coloured NPC/route captions and grid numbers. Interactive windows and
F12 captures omit the control bar, hover/status HUD and help card. Console output, PNG `viewprobe` metadata,
camera pose and sidecars remain complete. Without the flag, output is unchanged.

In textured mode, `--ref` is repeatable: bare `--ref FORMKEY` keeps the orange halo; `--ref FORMKEY=cyan` and
`--ref FORMKEY=magenta` distinguish objects in one frame. Accepted colours are orange, cyan, magenta, yellow,
green and blue. Later entries for one FormKey win; an explicit colour overrides a conflict door's orange. Flat
diagnostic mode retains one bare `--ref FORMKEY` and refuses repeated or coloured forms because it has one
highlight channel.

Before publication, inspect report-mode frames at full size. Verify that no caption, control, hover, help or
grid-label pixels remain, requested overlays remain visible, and PNG metadata still carries the replay pose.
A successful save or size check alone does not establish clean pixels.

- **Instance/profile:** `--instance DIR --profile NAME --game DIR`. **Nothing is compiled in** —
  see *Where it points* below. A run with none of it configured exits with guidance rather than
  walking a directory that isn't there.
- **Evaluation candidate:** `--order FILE` reads the same candidate configuration as the probes,
  through `HarnessOrderCli`. Its plugin order and optional asset-provider list are used together;
  without a `Mods` list, assets still come from the supplied base profile. It does not edit MO2.
  Missing/illegal candidates refuse before rendering. Textured captures write a sibling
  `*-configuration.json` with the input fingerprint, plugin hashes/providers, candidate hash and
  view transformations. Compare that fingerprint with the accepted evaluation arm before qualifying
  the media. `--nav-plugin` and `--compare` remain historical navmesh substitutions within this
  input scene; the sidecar records them separately. Worklist shots each retain their declared state.
- **Camera** (Skyrim coordinates; a `getpos`/`getangle` console line pastes straight in):
  `--eye X,Y,Z` | `--eye-ref FORMID` (+`--eye-height`, default 120), `--look X,Y,Z` |
  `--look-ref FORMID` | `--yaw DEG` (0 = north/+Y, clockwise) + `--pitch`, `--fov` (default 65).
  The frustum's near plane is 15 u, the game's `fNearDistance` as read off the running engine
  (2026-09-05; it was 4 u before, which drew what the game clips in a close-up); the far plane is
  131,072 u. Neither is a flag: nothing in a profile ini sets the game's, and matching it is the point.
  Textured mode requires an eye — with none given, the flat diagnostic mode runs instead.
- **Navmesh:** `--nav off|filled|wire|both` (defaults to `wire` when `--conflicts`/`--red` is
  given), `--nav-color auto|green|cyan|magenta|yellow|blue` (default auto).
- **One storey's navmesh:** `--nav-z Z[,TOL]` (or `auto[,TOL]`, taking Z from `--route-z` or the
  worklist shot's `floorZ`) draws only the navmesh triangles whose own z RANGE meets the band
  `Z ± TOL` (default 64). Paired with `--cull-above` this turns a multi-storey interior top-down
  into a **walkable floor plan of one storey**, which is what route planning needs and what a
  sliced shot alone cannot produce — `--cull-above` slices statics only, so without this the cyan
  sheet is the union of every floor stacked on top of itself. Applies to the sheet, the wire, the
  dashed hidden layers and the fly-through's hover list alike. Prints what it kept AND lists every
  height band the cell has, so an empty band names the storeys that are not empty; an empty band
  is REFUSED. Caption says `NAVMESH z N ±T ONLY`. Full section: *One storey at a time* below.
- **World-coordinate grid:** `--grid N|auto` rules a labelled grid across the storey's ground
  plane, coordinate written where each line meets the frame edge, plus a legend naming the
  spacing and the plane. `--grid-color NAME|R,G,B` (default a warm grey) is RESERVED — the nav
  wire's auto-pick avoids it. Static renders only, and DECLINED with a note at eye level. Full
  section: *The grid* below.
- **Cell outline** (on by default, exteriors only): a WHITE DASHED line along the target cell's four
  edges, riding the terrain, always visible. The render covers the target cell AND its ring of
  neighbours, and nothing else in the picture says where the cell under examination stops.
  `--no-cell-outline` drops it — worth doing for eye-level shots, where it cuts across geometry at
  head height and reads as part of a building.
- **Ring refs** (on by default, exteriors only): placed refs load for the target cell AND its ±1
  ring, matching the terrain. Before this, neighbours contributed terrain and nothing else, so any
  eye-level shot read near objects → void → bare ring terrain, with everything between absent
  (fixed 2026-08-28; a building 700 u away in the next cell simply never loaded). Navmesh and the conflict
  overlay default to the target cell; `--nav-cell` explicitly adds neighbouring annotation layers.
  Implicated DOORS standing in ring cells render and
  highlight, and the "implicated door is NOT in the rendered cell(s)" message fires only for doors
  beyond the whole loaded set. `--no-ring-refs` returns to target-cell-only refs (cheaper — a
  dense ring can be ~8× the shapes). Interiors have no ring.
- **Ring edge** (with ring refs, exteriors only, 2026-10-10): a reference anchored more than one and at most
  `EdgeRadius` = 2 cells out joins the scene when its placed bounds reach into the 3×3. For a drawable base the bounds
  are its OBND; for an occlusion plane, its XPRM primitive. They are placed by scale, the engine's rotation
  (`SpatialOverlap.RefrRotation`) and position. Anchoring follows the ring rule: a temporary reference by its cell's
  grid, a persistent one by its position, the last sighting in load order deciding. An admitted reference then takes
  exactly the ring path: BOS, the enable chain, state arms, the plane set, drawing and every count. A large mesh
  anchored two cells away, such as the Windhelm bridge west of the docks, used to be absent while the content behind
  it was drawn, so a plane was blamed for hiding what the bridge hides.
  - **Why the 3×3 is enough.** The verdicts concern content inside the 3×3 seen from an eye inside it. A square is
    convex, so the segment from the eye to that content lies inside the 3×3. Anything that can come between them
    therefore has geometry inside the 3×3. Admitting every loaded reference whose bounds enter the 3×3 admits every
    possible occluder; a reference whose bounds do not enter cannot change a verdict. This is also why terrain and
    water need not widen.
  - **The ±2 grid is an inference, not a reading.** It is the engine's `uGridsToLoad` default of 5. The setting is
    unset in the lab INIs, and `Utility.GetINIInt("uGridsToLoad:General")` returned 0 in game (that call cannot read
    `u` settings). Beyond ±2 only listed large references are admitted (next item). The console line `+N
    reference(s) anchored two cells out …`, the planes report's `sceneScope` (`edgeRadius`, `edgeRadiusBasis:
    "inferred"`, `edgeAdmitted`, `edgePlanesAdmitted`) and its coverage line all say so.
  - **Deliberately unchanged at 3×3:** `--nav-cell`, terrain and water. Those answer questions about the cells' own
    ground. The `--stations` and route-station floors take the same ring (and the large references below) through
    `FloorFeed`'s reach: a reference beyond the 3×3 is a floor candidate when its placed box enters it (the rule lives
    in `_shared/RingReach.cs`, shared with this merge).
  - **Limits:** OBND is the record's stored bounds, not the mesh's; a drawable base without an OBND is not admitted.
  - **Tests:** `--winning-refs-selftest` cases E1–E10.
- **Large references** (with ring refs, exteriors only, 2026-10-11): the game 3D-loads the references a worldspace's
  large-reference lists (WRLD `RNAM`) name out to `(uLargeRefLODGridSize − 1) / 2` = 5 cells (11 in the lab INIs).
  A reference anchored 3–5 cells out joins the scene by the ring edge's own test (its placed box enters the 3×3; the
  same `EntersSquare` serves both merges) when the selected list names it, and then takes the same ring path.
  - **Which list.** A root worldspace (Tamriel) uses the **union of every plugin's list**: a city or eFPS plugin that
    overrides the WRLD record copies it without `RNAM`, and the game still loads the base list's references. A child
    worldspace (a record with a `Parent`, the walled cities) uses its own lists' union, or none, by
    `--large-refs-child union|none` (default `union`). The parent's list never applies inside a child: it names only
    parent references.
  - **Basis: measured in game.** Tamriel at one station: listed references 3D-loaded at rings 3–5 and none at 6–7,
    although the winning Tamriel record lists nothing. Both city worldspaces: the city's own listed references loaded
    beyond ring 2, the parent list not in memory. A later plugin's list can name an earlier plugin's reference, so
    every reference anchored 3–5 out is tracked by the last-sighting rule and the list filter runs after the walk.
  - **Occlusion primitives on a list are admitted by record**: their in-game activity at rings 3–5 has not been read.
  - **The report:** `sceneScope` gains `largeRadius` (5), `largeRadiusBasis`, `largeList` (`union`/`none`),
    `largeListBasis`, `largeListed`, `largeAdmitted`, `largePlanesAdmitted` and `largePrimitives: "by record"`; the
    console prints `+N large reference(s) anchored 3-5 cells out …` when any is admitted.
  - **Scene-scope limit: content beyond the 3×3 is not modelled for any ring.** A listed reference whose box misses
    the 3×3 is not drawn even when it stands in the view (distant mountains): the game draws it, this render does not.
    It cannot hide content inside the 3×3 (the convexity argument above), but a plane cannot be credited with hiding
    it either. On the G4/G5 predictive views such references reach the view wedge at about 4,078 of 4,556 views (a
    mean of 83 per view); admitting them would also need terrain and LOD beyond the 3×3.
  - **Tests:** `--winning-refs-selftest` cases L1–L12.
- **Navmesh diff spheres** (`--compare` only, on by default): small shaded VIOLET spheres at the
  navmesh vertex positions one plugin's version has and the other's does not, drawn in every panel
  that contains them, always visible. `--no-nav-diff` drops them.
- **Hidden navmesh** (on by default, both categories): navmesh the camera cannot see is redrawn as
  an always-visible DASHED outline. `--no-nav-underground` drops the layer for navmesh buried under
  the LAND surface; `--no-nav-under-statics` drops the layer for navmesh behind geometry. Either
  off puts those runs back in the solid, depth-tested layer — i.e. back to invisible. In a
  multi-storey interior at eye level the defaults are overwhelming (upper floors hang in mid-air
  with nothing anchoring them); turn both off for an in-game-like interior shot.
- **Look:** `--sky R,G,B` (0-255 or 0-1; default a pale sky blue `0.70,0.82,0.92`), `--msaa N`
  (default 4, clamped to the context's `GL_MAX_SAMPLES`; 0/1 = off).
- **Troubleshooting overlay:** `--conflicts emit.json` (paints implicated navmeshes red, doors
  orange, prints the red centroid and a suggested camera), `--red "FORMKEY[:tri]"` by hand.
- **Compare:** `--compare "A.esp,B.esp"` renders three panels with one camera and stitches them.
  Side panels are the winning state with that plugin's navmesh versions substituted — "the cell if
  this plugin won" — so records neither plugin touches are identical across panels and any visible
  difference IS the disagreement. `--nav-plugin X.esp` renders one plugin's versions in a single
  view. This is an exact-version view: records that plugin does not supply are absent.
- **Shared navmesh view:** repeat `--nav-cell FORMKEY[=COLOUR]` to add up to eight explicit
  neighbouring exterior cells' navmeshes. Full CELL FormKeys are required; participants must be
  in the same worldspace's existing ±1 geometry ring. Unresolved, distant, interior, duplicate or
  navmesh-empty participants are refused, as is `--no-ring-refs`. Optional colours are green,
  cyan, magenta, yellow or blue; the target retains `--nav-color`. Red findings retain priority.
  This does not expand terrain/geometry scope or change the target-cell outline. All ordinary
  navmesh filters, depth/dash rendering and historical comparison semantics apply to the selected
  layers. A selected cell need not fit in the camera; visually verify both participants.
- **One historical NAVM over winners:** repeat `--nav-version NAVM_FORMKEY=Plugin.esp` (at most
  32) to substitute exact record versions while retaining every other winning navmesh. Use this
  for a historical neighbouring endpoint with the current source held fixed. Unresolved versions
  or records outside the selected cells are refused; this option cannot combine with `--compare`
  or `--nav-plugin`. It changes only navmesh annotation, never the whole world scene.
  Example: `--nav-color cyan --nav-cell 001002:World.esm=magenta
  --nav-version "001010:World.esm=Earlier.esp" --red 001011:World.esm:3`.
  These explicit selections write `<cell>-navmeshes.json` with layer/cell/provider identities,
  triangle counts, requested red indices and their actual existence/admission, camera and replay
  arguments. With `--order` — or with `--config-identity`, below — it also carries the evaluated
  configuration fingerprint; otherwise configuration identity is explicitly null. An absent triangle is never reconstructed or painted
  from another version. Triangle admission is not proof of visible pixels or NPC behaviour.
  `viewprobe --nav-selection-smoke` runs production-path known-answer selection/refusal checks.
- **Unrecognised flags are refused:** an unmatched `--` token exits 2 before rendering. Use
  `--eye X,Y,Z`; the parser does not accept `--flag=value`. Dropping a camera argument could
  otherwise silently select an overview. The error also covers a trailing flag missing its value.
- **`--extraction-scratch-sweep <dir> [--delete-only <subdir>]`**: remove the extraction folders of
  renderer processes that are gone, and report — as JSON — everything kept and why. Needs no MO2
  instance, no game and no load order. See *The extraction scratch* below for the ownership rule
  that decides what it may touch; it is the only authority this entry point has.
- **`--asset-path-budget <path>…`**: what the path budget makes of each path — its length, whether
  the rendering library's reader can read a file there, how much room it would leave an asset if it
  were the extraction root, and the sentence an unreadable-asset entry would gain. See *The path
  budget* below. Needs no MO2 instance, no game and no load order.
- **`--extraction-scratch-fail claim|guard|guard-swallowed`**: take one of the three routes a
  scratch failure can travel, on purpose, and end the run the way it would end in a render — one
  line, no stack trace, exit 4. Needs no MO2 instance, no game and no load order. `claim` against a
  usable data directory is the green control and exits 0.
- **`--allow-unreadable-assets`**: do not fail the run when an asset was found and could not be read
  (see *An asset that was found and could not be read fails the run*, below). The environment
  variable `VP_ALLOW_UNREADABLE_ASSETS=1` does the same for a caller whose command line cannot
  change. Whichever one applied is named in the census, in the report and in the run record.
- **`--config-identity`**: report WHAT THIS RUN CONSUMED when no `--order`
  replaced it — the same `capture input configuration:` line and `<cell>-configuration.json`
  sidecar a candidate run emits, resolved from the profile itself. It is **opt-in** because
  resolving it hashes every loaded plugin; the planner requests it on each verification render
  rather than every exploratory probe. It is a REPORTING flag: it is held in its own
  variable, so asking the question never changes the order, the asset view or the archives the
  capture draws from. A run that predates the flag ignores it and reports no identity.
- **Terrain:** `--no-terrain` (off entirely), `--no-terrain-layers` (base layer only — no painted
  alpha layers and no base stand-ins).
- **Water and coverings** (both ON by default — their ABSENCE was the bug, not their look):
  `--no-water` drops the water surfaces, `--no-coverings` drops the MATO projected snow/ash/moss.
  Both change every exterior render, so both announce themselves on the console. See *Water* and
  *Projected coverings* under *Known rough edges* — **the water HEIGHT is a known open defect.**
- **Scene filters:** `--fx` / `--fx-static` (see *Effect geometry*), `--markers` (translucent
  editor markers), `--list-meshes` (per-mesh census with its WHOLE-FILE class, `FX-file` vs
  `geometry`), `--out DIR`, `--size N` (default 2560; the render resolution as well as the PNG's —
  there is no downsample step, so `--size` IS the sharpness knob, with `--msaa` for the edges).
- **Capture canvas shape:** `--aspect W:H` (or a positive decimal; default `1:1`) makes the
  first-person capture canvas the shape the camera was planned for. `--size` stays the **width**
  and the height follows, so the default is the historic square and every existing command renders
  the identical frame — verified bit-for-bit, not by inspection. **`--fov` is the VERTICAL angle**
  (it reaches OpenTK's `CreatePerspectiveFieldOfView`, which takes fovy), so widening the aspect
  widens the HORIZONTAL extent and leaves the vertical alone: `--fov 65 --aspect 16:9` is 65° tall
  by 97.11° wide. That is the same frame `shotplan.py` plans against with its own `--aspect`, and
  until 2026-09-14 viewprobe could not capture it — the projection was pinned to aspect 1, so a
  pose planned for 16:9 was delivered as a square that keeps the vertical angle and crops about a
  third off the horizontal. Nothing failed: the frame rendered, the subjects stayed in it, and the
  planner's framing numbers silently stopped describing the delivered picture. **Capture the shape
  you planned against.** Refused rather than ignored where it cannot apply: the flat diagnostic
  mode's orbit views are square, and `--interactive` takes the window's own shape.
- **Occlusion planes** (added 2026-09-04; its own section below): `--planes` draws every occlusion
  plane in the cell (+ ring) as a translucent quad with an outline — solid where visible, dashed
  where hidden — coloured by who put it there (grey vanilla, gold inherited, magenta added, teal
  moved); the caption carries the legend and the console the counts, drawn or not. `--pick` on a
  pixel a drawn plane covers reports the plane first, then what the depth buffer says is behind it.
  Off by default, and a render without it is byte-identical to one made before the layer existed
  (verified on an interior and an exterior). `--occlusion off|on|diff` previews what the planes
  predict under an explicit `--occlusion-rule FILE`: `on` removes predicted-culled shapes and
  `diff` tints them. `--planes-report` writes per-view research JSON with source bounds,
  same-shape raster visibility and explicit coverage; no rule means unassessed. The nearby
  collision listing is supporting evidence. See the research limitations below.
- **Camera stations for a cell's occlusion primitives:** `--stations SPEC.json --stations-out
  POSES.json` composes the stations a player could stand at to look at each primitive, writes the
  pose file with its yield header, and exits **without rendering anything**. Exteriors only. See
  *Camera stations for occlusion primitives* below.
- **Placed NPC actors:** `--npc FORMKEY[@X,Y,Z[,HEADING]][=label]` (repeatable; implies textured
  mode) renders the NPC's actual character model — FaceGen head, skinned body, and the outfit the
  RUNTIME will dress it in — at its winning placement or an explicit position (see *Placed NPC
  actors* below). `--npc-no-outfit` drops the outfit layer; `--npc-puck on|off|auto` controls the
  translucent ground disc + always-visible rim under each actor (auto = on when the camera looks
  down steeper than 60°). Each actor gets an identity colour shared by its puck and its caption
  line, assigned in `--npc` order.
- **Interactive fly-through:** `--interactive` (implies textured mode; still needs an eye) opens
  the scene in a visible window instead of saving a PNG — WASD + RMB-mouse to fly, hover to
  identify what is under the cursor. `--fly-speed U` (default 600 u/s) and `--look-sens DEG`
  (default 0.15° per pixel) set where the flight feel STARTS; both are also adjustable in the
  window, so these exist so a value that turned out right does not have to be re-dialled every
  run. Both are refused without `--interactive` rather than silently ignored. See *Interactive
  fly-through* below. Not combinable with `--compare`.
- **Dollhouse slice:** `--cull-above Z` drops every STATIC triangle whose vertices all sit above
  that Skyrim z, so an interior top-down shows the storey the actors stand on instead of the roof
  and upper floors over it. Walls spanning the cut keep standing (their lower vertices anchor
  them); terrain, navmesh, NPC actors and the overlay are never sliced. The caption says
  `SLICED above z N` — a render missing its roof must declare it, or the absence reads as a
  finding. Per-render console line counts what was removed. This is the BUILD-time, triangle-
  exact knife; `--interactive` also has a live shape-granular band on the clip keys, and it
  prints the `--cull-above Z` line for whatever slice flying found.

- **Base Object Swapper layer:** `--bos off|on|diff` (default `on`). The game's Base Object
  Swapper (`*_SWAP.ini`) replaces a reference's base object at load and can move, rotate, scale or
  flag it; `on` draws every ref with the base and transform the game loads (the record base is
  what the plugins say, not what the player sees), `diff` does the same and tints the swapped refs
  TEAL, `off` draws the record bases as every render did before 2026-09-04. A random line (several
  targets, or a chance under 100) draws its FIRST alternative and says so; hover a swapped ref for
  record base → effective base and the ini line. In the window `B` toggles `on` ↔ `diff`. Its own
  section: *The Base Object Swapper layer* below.
- **World state:** `--state <switch>[,<switch>…]` (an enable marker by editor id or FormKey),
  `--state stage:<quest>:<n>` (with `--switch-map <switchprobe --json>`), or `--state baseline`.
  Every ref's existence becomes its runtime enable state with those switches flipped — what a quest
  stage, a scene or a trigger does when it enables the marker later — instead of the load-time
  state. `--state-view on` (default) draws the state; `diff` also draws what the flip took out,
  arrivals tinted GREEN and departures RED. In the window `T` toggles `on` ↔ `diff`. Its own
  section: *The world state* below.
- **Walking route** (`--route X,Y`, repeatable): the path to WALK from this camera, drawn as
  straight segments with arrowheads pointing waypoint to waypoint. **X and Y only** — z is
  resolved from the winning navmesh. `--route-z Z` states the floor the route runs on (which
  storey), `--route-color NAME|R,G,B` overrides the reserved colour, `--route-arrows auto|end|N`
  sets the arrowhead spacing, `--no-route-discs` drops the fill disc at each waypoint. A worklist
  shot's own `route` is the same thing and overrides these. **An off-mesh waypoint is a hard
  error**, and so is a leg that leaves walkable ground BETWEEN its waypoints. `--route-solve`
  opts out of the straight-leg requirement: the waypoints become ENDPOINTS and each pair is joined
  by an actual walk over the navmesh's connectivity graph, refusing loudly when there is no path.
  Its own section: *Walking routes* below.
- **Worklist** (`--worklist FILE`): a SHOT LIST — cells, and per cell the camera poses worth
  looking at, each with the sentence that says why. It REPLACES the positional cell argument (a
  run may open with a flag when a worklist supplies the cells) and it overrides `--eye`/`--yaw`/
  `--pitch`/`--fov`, because a shot IS the camera. With `--interactive`, `PageUp`/`PageDown` step
  the shots and `Ctrl+PageUp`/`PageDown` step the cells; without it, every shot renders to its own
  PNG named `<cell>-c<N>s<M>-<label>-fp.png`. Schema and rationale: *Worklists* below.
- **Console bat files** (`--emit-bats NAME|DIR`, needs `--worklist`): write the worklist as Skyrim
  console batch files instead of rendering anything — see *Worklists* below. `--bats-profiles A,B`
  additionally enables that mod folder in those MO2 profiles and DISABLES it in every other profile
  of the instance.

### Asking the render questions

All repeatable, all print to the console.

| Flag | Answers |
|---|---|
| `--pick X,Y` | what is at this IMAGE pixel — unprojects the finished frame's depth to Skyrim coordinates, the cell, the distance, and the LAND surface height there |
| `--nav-at X,Y` | is there navmesh at this world X,Y — every winning triangle whose 2D footprint covers it, INCLUDING ones the render filters out and why; or the distance to the nearest triangle if none do. Reports every level, so a multi-storey interior returns one row per floor |
| `--refs-at X,Y[,R]` | every winning reference within R (default 256) of a world X,Y, each with its base's TYPE (`Door`, `Container`, `Static`…), FormKey, EditorID and mesh, and whether the render draws it (and if not, why not). The type is the field a caller closing doors before a shot looks for |
| `--inside-at X,Y,Z` | which physical winning refs' OBND boxes ENCLOSE a world point, each with its box dimensions and six world-axis ray distances against the ref's OWN mesh (nearest hit ±x, ±y, up, down; `-` beyond 4096 u). The box only nominates — a tower-kit wall piece's box (966×966×384 u) encloses every camera in the room it walls — and the rays decide: a bookcase reads hits on three or more axes within arm's reach, a room piece's rays run to its walls. Physical types only (statics, furniture, containers, doors, activators, trees, flora); markers, control objects and effect volumes are skipped. **Nomination is by the mesh's own NIF bounds since 2026-09-14**, not record OBND: a box that understates its mesh never nominates its ref, so the eye reads "nothing encloses it" while real geometry surrounds it — measured on unchanged vanilla data, where `RiftenRWHallArch02` encloses eyes its record box does not nominate. The header reports how many refs could be nominated only by an unverified record box and how many could not be read at all, because an EMPTY nomination over either is not clearance; `--inside-bounds obnd` restores the old nomination. Added 2026-09-03 for the shot planner's inside test |
| `--clear-at X,Y,Z` (+ `--clear-reach`, default 512) | what placed mesh geometry is really AROUND a world point, on each of the six world axes, whether or not any box encloses it: nearest hit distance and the owning ref per axis, from the candidates' own transformed meshes (broad phase over placed OBND boxes, then Moller-Trumbore against both faces). `--inside-at` only ray-tests refs whose box ALREADY contains the point, so a point nothing encloses gets zero rays and an empty answer that reads exactly like a clear one -- a camera in the void behind a corridor wall and a camera in the middle of the corridor both print "no physical ref's box encloses it". The header prints four population counts (candidates, readable meshes, unreadable, refs skipped for want of a usable OBND, refs with no model), because "nothing found" is only evidence of open space when the broad phase could see what would have been there. The broad phase is OBND-based and that limit is stated in the line itself. Outdoors the ground is a LAND heightmap rather than a placed ref, so the two VERTICAL rays read the height field directly (the four lateral rays do not) and the line reports which of three terrain states applies: a ground height, no heightmap at this XY, or terrain NOT BUILT for this run -- only the first is evidence, and an interior is the one case where absent terrain is expected. Terrain is dropped only by `--no-terrain` AND `--no-water` together, because the water pass loads the same heights. Candidates are selected by the mesh's OWN NIF bounds, not by record OBND: an authored box can be present and WRONG — undersized or offset — and selecting on it drops a ref silently, without touching the missing-box count, so an absence over "zero missing boxes" proves nothing. OBND survives only as a counted fallback for a mesh that cannot be read at all, and `--clear-bounds obnd` restores the old filter for comparison. Added 2026-09-14 for the shot planner's placement support test |
| `--depth-dump FILE` | write the finished frame's DEPTH BUFFER — the array `--pick` unprojects — to FILE as raw float32 window depth (size×size, rows bottom-up like the GL buffer, 1.0 = background), and print the near/far planes and fov it was encoded with. Opt-in; one explicit-camera capture only (refused with `--interactive`, `--compare` and worklists). A value becomes a distance along the view axis by the standard perspective inverse (near 15, far 131072) and a distance along the pixel's own ray by that pixel's angles — the same arithmetic `--pick` applies, for a caller that wants the whole frame instead of a pixel at a time. Added 2026-09-14 for the depth-survey demonstration |
| `--list-terrain` | per cell quadrant: the base layer and every alpha layer, with LTEX EditorID, resolved diffuse, and how many of its 289 opacity points are painted |
| `--find-ltex SUBSTR` | the LTEX records matching a name, then a walk of the WHOLE worldspace reporting which cells paint them and how heavily. Picking a demonstration cell from memory is guessing; this makes it a lookup |

**`--pick` is blind to the transparent pass.** It unprojects the DEPTH buffer, and blended geometry
draws with depth writes off — so a pixel covered by an alpha-blended plane reports *"background —
no geometry under this pixel"*. Precisely where a picture is most confusing, `--pick` may disclaim
all knowledge.

### Environment knobs

| Var | Effect |
|---|---|
| `VP_SHAPE_DUMP=<substr>` | for every mesh whose path matches, one line per BUILT shape: effect-vs-lighting shader, triangles, blend mode (Bethesda src→dst; `6->0` is additive), material alpha, colour × multiple, depth-write, slot-0 texture. The SHAPE-level companion to `--list-meshes` — that says which files are in the cell, this says why one of their shapes draws the way it does. Printed at build time, before any skip decision, so it shows skipped shapes too |
| `VP_WATER_DUMP=1` | the raw per-cell water read — HasWater, XCLW at full precision, the WATR link, and whether the height was used or rejected. A plane at the wrong height is INVISIBLE rather than wrong-looking (terrain simply covers it), so this is the line that says whether the height or the drawing is at fault |
| `VP_WATER_SHALLOW_ALPHA=<0..1>` | alpha at ZERO water depth. Default 1 = opaque everywhere, which is what the engine draws. Lower it for a see-through shore margin on a floor plan; below ~0.8 the sea starts reading as tinted glass |
| `VP_WATER_Z=<z>` | force every water surface to one height. The other half of the pair above: if the surface appears here and not at the resolved height, the renderer is fine and the height is wrong |
| `VP_FXSTATIC_EMIT=<float>` | taste multiplier on `--fx-static` brightness (default 1 = the authored `baseColorScale`). Below ~0.25 the fire stops reading; above ~2 it blows out |
| `VP_ALPHA_DUMP=gx,gy` | every alpha layer of that cell as a 17×17 ASCII opacity grid |
| `VP_LAYER_ONLY=<substr>` + `VP_LAYER_SOLID=1` | draw only the painted layers whose diffuse matches, at full opacity — isolates one layer so "is that wash really this texture?" has an answer |
| `VP_OCCL_SLACK="rel,abs"` | override the behind-geometry depth-compare slack (default `1.002,1.5`) |
| `VP_SOLVE_DEBUG=1` | with `--route-solve`: print each solved leg's funnel corners, after the inset. What you need when a solved leg is refused for straying — the corner list says whether the path took a shape you did not expect, and the refusal already names the coordinate to `--nav-at` |
| `VP_ENABLE_DUMP=1` | every enable-parent group among the loaded refs, with the parent's own state, its chain, and how the children resolve — the check that a "disabled via enable parent" verdict rests on what the data actually states |
| `VP_DOOR_DEBUG=1` | print each conflict-implicated door's holding cell during the load walk |
| `VP_CV_VERBOSE=1` | unmute the rendering library's logger |
| `VP_FLY_FRAMES=N` | `--interactive` smoke knob: render N frames, save a capture, close — the window loop is testable without a hand on the mouse. It also makes the loop INPUT-DEAF: the window takes focus when it opens, so without this two identical smoke runs captured different frames (measured: one drifted 138 u in x before frame 30). With it, a capture is a function of the arguments alone and repeat runs are byte-identical |
| `VP_FLY_PICK=x,y` | with `VP_FLY_FRAMES`: run one programmatic pick at those client coordinates and print what it hit — the ray path, testable headlessly |
| `VP_FLY_LOCK=1` | with `VP_FLY_PICK`: lock the HUD onto what it hit, exactly as `LMB` does |
| `VP_FLY_NAV=off\|filled\|wire\|both` | set the navmesh display mode before the capture — the `N` key's effect, headlessly |
| `VP_FLY_ROUTE=on\|off` | show or hide the walking route before the capture — the `R` key's effect, headlessly |
| `VP_FLY_CLIP=lo,hi` | set the height clip band before the capture; either side may be empty, so `,2048` is a ceiling with no floor |
| `VP_FLY_SHOT=N` | with a `--worklist`: open on shot N (1-based) of this cell — the `PageUp`/`PageDown` key's effect, headlessly |
| `VP_FLY_CELL=+1\|-1` | with `VP_FLY_FRAMES` and a multi-cell `--worklist`: after the capture, request the next/previous cell instead of closing — `Ctrl+PageUp`/`PageDown`'s effect. Without it the multi-cell rebuild is the one path in the feature no test can reach, and it is the one that tears a GL context down and builds another |
| `VP_FLY_HUD=1` | include the OVERLAYS — control bar, caption, help card — in the capture. `F12` deliberately excludes them, so this is the only way a test can see them, at the cost of the burned caption overlapping the bar's first row |
| `VP_FLY_HELP=1` | raise the in-window help card before the capture — the `H` key's effect, headlessly. Only visible with `VP_FLY_HUD=1`, since the card is an overlay |
| `VP_FLY_BAR=expanded\|collapsed` | collapse the control bar to its one-row strip before the capture — the chevron's effect, headlessly |
| `VP_FLY_POSE=1` | put the camera numbers on the caption before the capture — the bar's `pose` toggle, headlessly |
| `VP_FLY_GLOW=on\|off` | the hover highlight, headlessly. With `VP_FLY_PICK` the picked shape is the one lit, and `VP_FLY_HUD=1` keeps the lift in the capture (a hand-pressed `F12` never does) |
| `VP_FLY_PLANES=on\|off` | show or hide the occlusion-planes layer before the capture — the `L` key's effect, headlessly. The layer is resident in every fly-through whether or not `--planes` was given, so this works without it |
| `VP_FLY_OCCLUSION=off\|on\|diff` | set the occlusion preview before the capture — the `O` key's effect, headlessly |
| `VP_OCCL_TINT_ALL=1` | the occlusion preview's POSITIVE CONTROL: every drawn ref counts as culled, so `--occlusion diff` must come out washed red and `on` must come out empty. A well-placed plane's cull changes no pixel (its shadow lies inside the shadow of the wall it stands in), which looks exactly like a preview that failed; this is what tells them apart |
| `VP_RESIDENT_TELEMETRY_OUT=<file>` | on a static worklist: at the end of every cell, on every exit path, append one JSON line to the file — the cell's index and target, how its shot loop ended, its shot and render-pass counts, and (with reuse on) the resident texture cache's budget and repoll counter at that moment. The record describes how the shot loop ended, not how the cell tore down: it is written before the cell's graphics owners are disposed. Nothing per shot, nothing printed; a failed append prints one line and the run exits 2. Hand it a fresh file: lines are appended |
| `HOUSECARL_VERIFY_MASK_CACHE=1` | with `--planes-report`: re-encode every visibility mask the per-view cache serves from memory and compare it to what was cached, print the counts, and FAIL the run on a mismatch rather than writing a mask it cannot confirm. The cache's correctness is an argument about the key (see *Research preview and report*); this is the switch that puts the argument to a real render |

---

## Where it points, and where its output goes

This repo is the TOOL. Nothing about any particular mod list is compiled into it, and nothing a
run produces should land back in it.

**Target resolution** (`_shared/HarnessTarget.cs`, shared by every probe), first hit wins:

1. the probe's own flags — `--instance`, `--profile`, `--game`, `--roots`;
2. environment — `HOUSECARL_MO2_INSTANCE`, `HOUSECARL_MO2_PROFILE`, `HOUSECARL_GAME_PATH`,
   `HOUSECARL_DOWNLOAD_POOL`, `HOUSECARL_SEVENZIP`, `HOUSECARL_SEARCH_ROOTS`;
3. a JSON config — `HOUSECARL_HARNESS_CONFIG` names it outright, otherwise `harness.local.json`
   found by walking up from the running binary. Copy `harness.local.example.json` to start.
4. nothing → exit 2, naming every way to set it.

**The instance is never a default.** The config's `instance` is not used when a run names none: a
probe given no `--instance` (and no `HOUSECARL_MO2_INSTANCE` for that run) exits 2 with
`refused: instance-not-explicit`, before it reads anything. The config's `game` and `profile`
belong to the config's own instance and are used only when the run's instance is that one (same
path after normalising); for any other instance they are withheld and the probe refuses with
`game-not-for-this-instance` / `profile-not-for-this-instance` unless the run names them itself.
`HOUSECARL_MO2_INSTANCE` counts only when set for the run: a value equal to the one persisted at
User or Machine level refuses with `instance-env-persistent`.

**Keep the config outside this checkout.** A file naming your MO2 instance, profile and drive
layout is a fact about your mod setup, not about the tool; `HOUSECARL_HARNESS_CONFIG` exists so it
can live with that setup. `harness.local.json` beside the harness works too and is gitignored, but
it is the fallback, not the intent.

Windows paths in that JSON want **forward slashes** — a lone backslash is an invalid JSON escape,
and the parser error for it is unhelpful enough that the loader adds its own hint.

**Output belongs outside too.** `--out` takes a directory; point it at your own working area.
Renders are large (a five-cell set runs to hundreds of MB) and every one of them is a picture of
one specific load order, so they are neither reusable nor shareable. `_geometry-harness/_runs/` is
gitignored for the same reason: probe transcripts name the plugins of whatever list produced them.

### The extraction scratch, and why it is not beside the exe

The rendering library cannot read an asset out of a BSA in place: it extracts it to disk first. The
destination it picks is `AppContext.BaseDirectory\SkyRenderCache`, captured once when the
resolver is constructed — the same folder for **every** process started from one build. Two renders
started together therefore wrote the same files at the same moment, and because a failed read on
that path is swallowed (a mesh that will not parse becomes "no geometry", a texture that will not
decode becomes a white fallback) both runs still ended at exit 0 with a picture that was quietly
wrong. Nothing in the console said so.

**Each process now extracts into its own folder**, and removes it when it ends
(`ExtractionScratch.cs`). Where that folder lives, first hit wins — the same order the run store
uses, and never inside the installed binaries:

1. `HOUSECARL_DATA_DIR\viewprobe-extract`
2. `%LOCALAPPDATA%\houseCARL\viewprobe-extract` (Windows)
3. `$XDG_STATE_HOME/housecarl/viewprobe-extract`, else `~/.local/state/housecarl/viewprobe-extract`

**Nothing is held between runs.** The folder belongs to one process; no other process may use it,
and it is gone when that process exits. It is a scratch, not a cache, so a run's cost is what it
extracts — and an asset that changed on disk can never be read from a stale copy.

#### The path budget: how long that folder's name may be

The rendering library reads a mesh with a **native** loader, and a native open on Windows still
stops at `MAX_PATH`. Measured on the library's own reader, with one mesh copied to every length in
a range: it returns geometry at **259** characters of full path and an empty list from **260** —
without throwing, with the file present and readable by every managed API in the process. A second
sweep moved the same total between the directory and the file name and flipped at the same total,
so it is the whole path, not one component. houseCARL's own parse reads such a file fine, which is
why a mesh past the limit shows up as *found, parses with N drawable shape(s), but the renderer
produced no geometry* — and the entry now **says the path is why**, with its length, instead of
leaving you to find it.

The path the limit applies to is the extracted one, and it is built like this:

```
<scratch root>\SkyRenderCache\<8-char source token>\<the asset's own path>
    ^-- <data directory>\viewprobe-extract\vp-<pid>-<8 hex>
```

So the budget for an asset's own path is `259 − len(scratch root) − 31`. At the default location on
a normal Windows profile that leaves comfortably over a hundred characters, and an ordinary game
asset path is well under that. **`HOUSECARL_DATA_DIR` is what can spend it**: point it somewhere
deep and ordinary assets stop being read. To shorten it, set `HOUSECARL_DATA_DIR` to a short
directory — near the root of a drive, for instance — or unset it and take the default.

`viewprobe --asset-path-budget <path>…` prints what the tool thinks of a path: its length, whether
the library's reader could read it, how much room it would leave an asset if it were the scratch
root, and the words an entry would gain. **A root that leaves an asset zero characters is refused
when it is claimed** (exit 4, above): that one is arithmetic rather than a guess — nothing in such a
run could be read. A root that is merely tight is not refused, because the tool does not know how
long the longest path in your load order is, and a guess would fail runs whose assets are all
short-named.

**The destination is guarded.** Every extraction destination is checked against this process's own
folder, and one outside it **refuses the run** instead of writing. If the library ever stops taking
the destination from `AppContext.BaseDirectory`, that is a loud failure on the first extraction
rather than a silent return to the shared folder.

**Any failure of the scratch ends the run at exit `4`**, with one line on stderr and no stack trace.
Two things are failures of the scratch: the guard refusing an extraction, and the scratch itself
being impossible to create — the data directory is a file, the path is refused, the disk is full.
There is one ending because a caller cannot control which route the failure takes: two of this
tool's own call sites resolve assets inside `catch (Exception)` blocks that log and carry on, so
whether the refusal's exception reaches the top is a matter of which call site tripped first. The
failure is therefore latched the instant it happens and spent on the way out of `Main` — swallowed
or not, the run says the same line and exits the same code. Exit `3` is a different thing entirely:
an asset that was **found and could not be read** (the next section). Exit `4` means nothing was
rendered at all.

`viewprobe --extraction-scratch-fail claim|guard|guard-swallowed` takes each of the three routes
deliberately — the claim throwing, the guard's refusal escaping, the same refusal swallowed by a
catch-all — which is how that single ending is tested with no game install. `claim` against a usable
data directory is the green control: it claims, prints the folder and exits 0.

**A killed process leaves its folder behind**, so the next run sweeps the parent for orphans. The
sweep is a deleter, and what it may never delete is the whole of its contract:

- a directory is viewprobe's **only** if it carries `viewprobe-extract.json` with a marker this
  build understands — its schema, its tool name, and both owner fields. A directory with exactly the
  right name and no marker is untouchable, and so is one whose marker came from a later schema or
  another tool;
- ownership is the process id **and** that process's start time (in UTC, so a daylight-saving
  transition cannot make a live process look like a recycled id), so a recycled id can neither get a
  live process's folder deleted nor hide the fact that an orphan was kept;
- an owner whose liveness cannot be determined means **keep** — "I could not tell" is not "it is
  dead" — and so does a marker that cannot be read;
- **a delete is authorised only when the owner is this process or is provably gone**, at exit and on
  the command line alike, and it says why when it refuses. `--delete-only` takes any path, so a rule
  that stopped at "it carries a marker" would let one command remove a sibling's folder in the middle
  of its render — this defect's own failure, coming back through the deleter.

`viewprobe --extraction-scratch-sweep <dir> [--delete-only <subdir>]` runs exactly that code from
the command line and prints one JSON object: what it deleted, and **everything it kept with the
reason**. A folder with no marker — what a crash between the `mkdir` and the marker leaves — is
reported as kept, so a leak is visible rather than silent. The ownership rule above is this entry
point's only authority: point it at the wrong directory and it will still refuse to touch anything
that is not viewprobe's. The start-up sweep runs the same code and prints nothing, so the console of
a render is unchanged; use the entry point when you want the report.

**A folder the sweep cannot account for is recorded in the run log.** A sibling that is still
rendering is normal and says nothing, but a folder with no marker, an unreadable marker, or an owner
whose id has been recycled is disk this tool can no longer reclaim, and the renderer can die out of
memory on a large cell, so this happens for real and costs gigabytes. The start-up sweep writes a
note on the run record — per-run by construction, so it cannot make two simultaneous consoles differ
— naming the count and the command that explains it.

The library's own `ClearExtractedFiles()` is deliberately not called. Deleting the process's whole
folder is strictly stronger, and the library documents that method as unsafe to call while a render
is in progress — which at process exit is not something the host can establish.

**An existing `SkyRenderCache` (or `CharacterViewerCache`, the folder's name before the library's rename) beside an older build is no longer read or written.** Nothing
deletes it for you, because it sits inside your binaries and this tool does not delete things it
did not create; remove it by hand when you want the space back.

### What a render says it is doing, while it is doing it

Every run opens a record (`standards/HOUSECARL_RUN_EVENTS.md`; point `HOUSECARL_RUNLOG_DIR` at a
directory to keep a job's history with the job). A render used to file itself under **one** stage —
`find-cell`, opened while the load order was walked and then never replaced — so a seventeen-second
render reported fifteen seconds of "walking the order for the target cell" at one plugin of five,
and a multi-shot run repeated that same stage once per shot.

Snapshot writes from one run are serialized with its terminal transition. A heartbeat already in
flight finishes before the terminal history record retires the active snapshot, and a liveness
callback arriving after completion is a no-op. Genuine store I/O failures still print the run-log
degradation warning and remain recorded; the ordering guard does not retry or suppress them.

It now opens a stage per phase, per shot, at the cuts the cost model measures: `find-cell`,
`record-walk`, `scene-build`, `report` (only when one is asked for) and `encode`. Each carries real
counters — plugins, plugins, references, shape draws, frames — and `find-cell` **revises** its total
when the cell turns up early, because a walk that stopped at the first plugin did not leave four
plugins unprocessed, it never had them to do. `housecarl-runlog history --stages` prints them, and
`stageSource` on the record is now `counted`, which is what the coverage manifest always claimed.

The record also carries `shots` (how many the run renders, so a reader knows how often the per-shot
phases should repeat) and, when the run had already resolved one, the configuration **fingerprint**
as an opaque resource — a hash that names no plugin, which is the form the contract permits for an
input. Nothing resolves a configuration merely to record it.

Weights are left at the default: an unmeasured weight would be a claim about the split this build
cannot make yet, and the estimator learns the shape from history instead.

### How many renders may run at once — computed, not typed

Renders that no longer corrupt each other can still exhaust the machine, and graphics and system
memory run out together: `tools/renderlimit.py` computes the limit and `tools/renderagree.py`
obeys it. The rule is the minimum of what each resource allows — `(free − reserve) ÷ what one
process of this job needs` — capped by the `-n` you asked for, which is a **ceiling, never a
floor**. What one process needs is measured from the solo render the driver already does, or
supplied with `--per-process-gpu-mib` / `--per-process-sys-mib`, which always wins; there is no
constant in the tree for it, because it depends on the cell, the frame size and the report.

The measured figure is the **peak drop in free memory over the baseline** taken before the solo
render — machine-wide and labelled as such, since Windows does not report per-process graphics
memory, so anything else running inflates it. The reserve is the one policy number (default 2000
MiB, `--reserve-mib`), and the plan prints it.

When the machine cannot be asked — no `nvidia-smi` on PATH, a reader that fails, a reader that
answers something that is not a number — the plan runs **one** process and prints the reason; it
never becomes "as many as asked". If not even one process fits, it refuses at exit 2 rather than
starting hopefully. `--dry-run` prints the plan and renders nothing, and `--gpu-free-mib` /
`--sys-free-mib` / `--no-memory-reader` stand in for the machine, which is how the rule is
exercised on hardware that has no such reader at all.

### Guarded campaign batches — `tools/renderbatch.py`

`renderbatch.py` is the opt-in path for resuming a static worklist campaign without renumbering
its shots or overwriting caller evidence. Its manifest must name one exact coordinate set in the
items, expected artifacts and publication receipts; a mismatch refuses before launch. Every shot
receipt is checked against the frozen readiness and worklist hashes, its original coordinate,
zero exit, exact artifacts, evidence masks and current file hashes. All receipts must pass before
the first caller file is copied.

Publication keeps each source's relative directory tree, uses durable intents and create-new
destinations, and starts only after the complete composite run validates. Resume uses the same
immutable manifest with a fresh `--json` result path. Missing or invalid process, system or
graphics telemetry stops the owned process tree. A correctness run made while the machine is busy
makes no timing claim.

### Does a shot rendered in a batch equal the same shot rendered alone?

One process can be asked for several shots — a worklist with many shots, and many cells — and that
is the path any sharing of work between shots would be built on. What such sharing risks is not a
crash but a quietly wrong picture, so `tools/rendertwin.py` compares outputs rather than exit codes:

- `twins` renders a worklist as one process, then renders **each of its shots alone** from a
  one-shot worklist of its own, and compares each batched shot with its fresh twin;
- `shuffle` renders the same shots in **two different orders** and compares each shot with itself —
  an answer that depends on what was rendered before it is state carried between shots;
- `control` renders one command twice into different folders, where the answer is known before it
  runs: a suite whose green control comes back red is a broken suite, not a finding;
- `fields` checks the worklist shot record's properties against the table of what the generated
  worklist varies, so a new shot field cannot quietly become a row nothing ever varies, and
  `makelist` writes that worklist; `selftest` is a known-answer run over synthetic folders.

Cutting one shot out of a batch is the part that could hide the answer, so both cuts are made by
something the renderer writes and both are controlled. Files carry `-c<cell>s<shot>-` — the shot's
position in the run — and are hard-linked into a folder per shot with that segment removed; the
console is cut at the `=== worklist cell i/n shot j/m: LABEL ===` line, whose counts are replaced
and whose **label is kept**. What a run prints before its first marker is the process preamble,
compared as its own part. What a batch prints once for the whole process (the base-object-swap layer
is loaded on the first shot and kept) is removed from the other side's block **by name and only when
this shot's block prints no line of the same kind**, so a line whose value changed is never removed.
Every arm then runs a cross-pair control — shot A against shot B's twin — and requires the decoded
pixels to differ, which is what proves the cut is not what is making shots equal.

The comparison itself is `renderequiv.py`'s: decoded pixels, reports under the enumerated-field
rule, the console, the census, the exit code, and a part that was not compared must be waived by
name. That report rule has **two** enumerated classes, not one. A field holding a path this run
*chose* (the frame, the scene depth, a candidate's visibility mask) is dropped. A field holding the
*command that reproduces the frame* is kept, with the paths inside it normalised: dropping it would
stop the comparison noticing a batch that handed a shot another shot's command, and comparing it
verbatim refuses every run given a path-valued flag — an order candidate, a rule file — because the
command carries that path. Any other line holding an absolute path still makes the comparison
refuse, and says which line.

### Keeping one cell's work across its shots — cell reuse, on by default

A worklist run builds its scene once per **shot**, which the run says out loud when it starts: two
shots of one cell walk the load order twice, resolve the same assets twice and parse the same
meshes twice. The shots of a cell differ in where the camera stands, and a built scene is
camera-independent, so that work can be done once for the cell.

Cell reuse does exactly that, and since 2026-09-24 it is **on by default** for a static worklist.
**`--no-reuse-cell`** turns it off: there is then no lease and every shot builds its own, the code
every run used before. `--reuse-cell` is still accepted and changes nothing; passing both is refused.
A single shot and `--interactive` never use the lease (a single shot has nothing to share with, and
`--interactive` keeps a cell resident by design); an explicit `--reuse-cell` on one of them says it
was ignored. With reuse on, the shots of a cell share a lease that is *constructed for the
cell and dropped when the cell is done* — a scope, not a cache that gets cleared, so nothing built
for one cell can still answer in the next.

What it shares today, as one pair made together and dropped together:

* the **asset resolver** (and with it the extraction index, which is why the second shot of a cell
  no longer re-extracts what the first shot already pulled out of the archives);
* the **decoded-pixel cache** built on that resolver — the BGRA32 pixels, cubemap faces and
  "fully transparent?" verdicts a texture decodes to, each keyed on the file's *resolved disk path*
  — which is why the second shot of a cell no longer decodes textures the first shot decoded. It is
  one slot with the resolver because it resolves every key through it, so the two cannot be paired
  wrongly or outlive each other;
* the **mesh builder and normalized parsed meshes**, keyed by game-relative mesh path. The retained
  value owns cloned vertex/index/material inputs and also records null, empty, missing-path and
  failed-build outcomes. Failures keep their per-shot console/audit projection. A readable file is
  retained only with its SHA-256 identity; an unavailable identity rebuilds instead of treating an
  empty hash as equal;
* the **complete record-walk facts**: copied winning and persistent placements, enable maps,
  occlusion-plane history, NPC/door watch results, navigation winners/versions/provenance, base and
  material tables, terrain/water/station results and the inputs BOS resolves against. Nested arrays
  are owned copies, and each shot receives a new mutable working copy. No Mutagen getter, lazy
  overlay enumeration or callback crosses the boundary. The process-owned BOS index/layer remains
  outside this value and is applied afresh to each working copy.

The prepared value is keyed by the normalized cell selection, watch sets and the ordered plugin
files that supplied it. A process-scope guard separately binds the asset/BOS readers to each
profile, plugin and BOS-rule path plus its observed size and last-write time; a detectable change
while the process is alive makes reuse refuse instead of rebuilding only the cell while leaving an
older process cache in place. This guard does not hash archive contents. A changed
camera does not invalidate record facts. A changed watch/selection key rebuilds the guarded slot,
and the next worklist cell always receives a new lease.

What it will not share, because the camera decides it: the route's ground z and `--npc-puck auto`.
What it will not hold at all: anything owning a GL handle. The GL side of a cell has its own owner,
described below. The rest of the list, and the reasoning, is in `CellScene.cs`;
`viewprobe --cell-scene-selftest` holds the lease to its three promises (one set of objects per
cell, a new set for the next cell, nothing after the cell) with no game, no instance and no GL
context.

Every change to what the lease holds is checked with the equivalence suite above, run with reuse
**on and off**, on an interior, an exterior with water, a multi-cell worklist and a fixture whose
load order carries a base-object swap.

`viewprobe --mesh-reuse-red <empty-output-directory>` is the renderer-path lifetime self-test used
while extending this lease. It renders synthetic mesh outcomes through `RenderToPng`, checks camera
and cut behavior, hashes every consumed array/material input, and exercises cell/disposal bounds.
It uses no game or MO2 data. Exit 0 means every declared lifetime contract holds; exit 1 lists the
contracts still unmet, and exit 2 is an unexpected test failure with its full stack.

#### The GL side of a cell: one context, one static scene

With reuse on, a worklist cell also owns one hidden GL context for all of its shots. It is
created when the cell starts and destroyed when the cell is done, on the thread that created it, and
the next cell gets a new one. **With `--no-reuse-cell` none of this exists**: every shot creates and
deletes its own context, as it did before.

For the life of the cell it keeps:

* the **context** and the **renderer**, created and initialised once;
* the **install texture manager** and its **resident texture cache**, which hold the textures the
  static scene draws with;
* the **static scene**: the uploaded meshes of the placed references, the terrain and its painted
  layers, and the coverings, with the textures they bind.

The static scene is keyed on the prepared cell identity (the record-walk key above) and on every
input that decides its geometry or materials without depending on the camera: each reference's
mesh, placement, scale, highlight, marker, BOS and state labels; the terrain patches and layers; the
coverings; `--fx` and `--fx-static`; `--cull-above` (the cut is applied before upload);
`--ref-surface` and its opacity; and the `VP_LAYER_ONLY`, `VP_LAYER_SOLID` and `VP_FXSTATIC_EMIT`
variables. A shot whose key differs from the retained one disposes that scene and builds a new one,
so no shot draws a scene built for other inputs.

Fresh in every shot: the camera and the frame target, and a per-shot texture manager for every
texture the static scene does not own. What that manager loads never joins the resident cache; it
is deleted with the shot.

Also built fresh in every shot and released at its end: the water; every overlay (navmesh wires and
fills, routes, the grid, NPC pucks and rims, occlusion-plane marks, seams and the other diagnostic
geometry); the placed NPC actors and the meshes they lend the renderer; and the subject masks and
the scene depth. The NPC actors are released by a scope the shot opens where it builds them, so a
shot that fails after building them still releases them before the cell goes on or ends. A
subject's isolation mask is measured against everything the renderer draws in that shot, the
retained static meshes included, so it is the same mask the per-shot path measures.

**Every shot starts from the same state.** A retained context keeps whatever the previous shot left
in it, so nothing is inherited:

* before each draw's clear, on both paths, the GL state the draw depends on is set explicitly: depth
  test, function, write mask and range; face culling and winding; blending; multisampling,
  alpha-to-coverage and sample coverage; clip distance, scissor, colour mask, stencil, polygon
  offset and sRGB writes; texture unit 0 active, and no program or vertex array bound;
* the retained renderer's settings as it was created (every settable property, its light slots and
  its overlay collections) are applied again at the start of every shot, before the shot sets its
  own;
* each retained mesh's visibility and full material state, captured once its bindings are complete
  and before any per-shot paint, are restored at the end of every shot, so an occlusion, BOS or
  state tint one shot paints never reaches the next.

The captured lists are explicit: a property the rendering library adds later is not covered until
it is added to them.

**What makes it refuse instead of drawing.** Each of these is printed and the run exits 2:

| what it prints | when |
|---|---|
| `--reuse-cell refused: a profile, plugin or BOS rule input changed while process-owned readers were live` | the process-scope guard above. The retained static scene is keyed on the same prepared identity, so a stale input identity refuses the GL side too |
| `retained graphics requires the prepared-cell identity` | a retained shot reached the renderer without the identity its static key is built from |
| `retained residency manifest refused before draw: …`, `populated residency manifest refused before draw: …` | before the static scene's first draw, and again once its bindings are complete, every texture it binds is checked against the resident cache. An entry that is **no longer resident (evicted)**, whose **resident binding changed** to another handle, or whose handle is **still mapped but no longer a live texture** refuses the shot, naming its category and path. It is never drawn with whatever that handle now holds |

When a changed key replaces the static scene, the install texture manager's own path map is emptied
with it, so the new scene's textures are looked up in the resident cache again: a resident texture
reuses its handle, and an evicted one is uploaded again.

**An allocation failure refuses the shot, with or without the flag.** After each texture the scene
itself loads (reference, terrain, covering and water textures), viewprobe reads the GL error queue.
A GL out-of-memory refuses the shot before its first draw with

```
texture upload refused before draw: the GL driver reported out of memory (GL_OUT_OF_MEMORY) during or before the upload of '<path>' (<category>); <budget>. The shot is refused rather than drawn with a substitute texture.
```

and the run exits 2. The asset is not reported missing or unreadable: it exists, and the driver
could not hold it. On the retained path `<budget>` names the resident cache's budget at that moment;
on the per-shot path it says that path keeps no resident texture cache. The check also runs with
`--no-reuse-cell` — a single shot, a worklist and `--interactive` alike — so this is the one change to
the per-shot path, and only in that failure case: the error used to be left unread and the shot was
drawn anyway.

With reuse on, a failure while the cell's GL side is set up, used or torn down is printed with its
full stack and the run exits 2, instead of raising the Windows application-error dialog.

**Measuring the resident cache.** `VP_RESIDENT_TELEMETRY_OUT=<file>` (see *Environment knobs*) appends one JSON line per
worklist cell at cell end, on every exit path: the cell's index and target, its shot and render-pass counts, the resident
cache's budget and repoll counter at that moment, and how the shot loop ended — `completed`; `stopped` with the exit code
of the shot that returned nonzero; or `fault` when an exception left the loop. The record describes how the shot loop
ended, not how the cell tore down: it is written before the cell's graphics owners are disposed, so a teardown failure
after it shows only in the process exit code, and a reader must check that the two agree. The repoll counter is read from
the library's public `ResidentTextureCache.RendersSinceRepoll` (`"counterField": "RendersSinceRepoll"`; earlier builds read
a private field and named it `"_rendersSinceRepoll"`). With `--no-reuse-cell` each cell still gets a line, with
the cache fields null. Unset, nothing is written or printed.

### An asset that was found and could not be read fails the run

The two census counts above — *refs without a resolvable mesh* and *textures the resolver could NOT
find* — each cover two very different situations, and the library gives them the same answer:

| what happened | what the picture is | what viewprobe does |
|---|---|---|
| **absent** — the resolver found it nowhere | a load order that does not contain the asset | counted, nothing more. Every campaign render has some. |
| **present, valid, nothing to draw** — found, parsed, no render geometry | correct | counted, nothing more. Collision-only meshes are exactly this and healthy exteriors are full of them. |
| **present, valid, not drawn by the renderer's rule** — found, parsed with drawable shapes, and every shape is one the rendering library skips by its own rule | lacks it **by design** | named under `meshes NOT DRAWN by the renderer's rule`, noted in the run record, and the run does **not** fail |
| **present and unreadable** — found, and the read failed | **wrong**: something that should be there was silently dropped | named, and the run **exits 3** |

The third used to be invisible: a mesh that would not parse became "no geometry", a texture that
would not decode became a white fallback, and the run still ended at exit 0 with a picture missing a
building. Each swallowed path is re-resolved and re-read after the render to decide, so "found" is
the resolver's own answer and "unreadable" is a read that failed again. An extraction that fails is
unreadable too: the asset was located in an archive and could not be taken out of it.

**A mesh the renderer drew nothing from is dismissed only when a second, independent parse AGREES
there was nothing to draw.** Not when the parse merely succeeds. A mesh that parses with drawable
shapes and that the renderer produced no geometry from is an asset that was found and not drawn,
whatever the reason — and that is exactly the shape of the extraction race's own mesh mode, where
the library reads a file another process is rewriting, returns no geometry *without throwing*, and
the file is whole again by the time anything looks at it. A rule that accepted "it parses" would be
blind to the very defect above, and to any other transient failure inside the library. A parse that
finds zero drawable shapes does agree, so collision-only meshes — of which a healthy exterior is
full — stay silent. `viewprobe --unreadable-asset-smoke <file>…` runs that decision over files you
name and prints the verdict and the drawable-shape count for each, which is how the rule is tested
against real meshes of all three kinds.

**The renderer's own rule is asked the renderer's own way.** Before a disagreement is called one,
the mesh is re-read with the library's reader (`nifly`) and put through the checks its per-shape
build makes before it touches geometry, in the same order: the AppCulled bit, a missing shader
property, no diffuse and no normal texture with no alpha property (the library's "likely
physics/collision mesh" rule), and no vertices. Only when **every** shape is skipped by one of
these does the mesh move to the not-drawn-by-rule class, each shape named with its shader block
and the rule that skipped it. Skyrim's placed water (`BSWaterShaderProperty`, no texture set —
`Water\Water2048x1024.nif`, a rain barrel's water) and its `EditorMarker` shapes (invisible chairs,
critter markers) are this class; before 2026-09-29 each of them failed its whole view with exit 3.
It is a statement about the library, not the game: the game draws a barrel's water, and the line
says the picture lacks it. A mesh with any shape the rule would draw keeps the disagreement, so a
torn read of a textured mesh still fails the run; so does a mesh the re-read cannot load. The
planes report is unchanged by this class (its references still carry "renderer produced no
geometry" and the view's coverage issue). `--unreadable-asset-smoke` prints the verdict `not drawn
by the renderer's rule` with the per-shape reasons.

**The frame names what it lacks.** A frame saved from a shot with assets found and unreadable
carries a second PNG tEXt entry, `viewprobe-incomplete`, naming each of them and whether the run
failed or an opt-out let it succeed, so the picture keeps the fact after it is separated from its
console. A frame that lacks nothing gets no such entry. Interactive F12 captures do not carry it.

**Where the hole is.** Under each unreadable mesh the census lists every placed reference that
uses it — `at <FormKey>  x,y,z  r <radius>` — with the radius of the independent parse's render box
at that placement's scale (`?` when no parse measured it). That is what lets `shotplan.py
--allow-unreadable-assets` tell a hole beside the subject from one across the cell.

**When the path is the cause, the entry says so.** A disagreement of exactly that shape — houseCARL
parses it, the library draws nothing from it — is what an extracted path past the library reader's
limit looks like, and the entry names the length and the limit rather than leaving it to be found.
See *The path budget* above for the arithmetic and how to shorten it.

On a healthy load order none of this prints, so a console an existing caller compares is unchanged
to the character. **A report gains one line either way**: `"unreadableAssets": null`. That is the
one respect in which a healthy report from this build differs from one written before the check
existed, and it is deliberate — it is what lets a driver tell the three states apart:

| what the report carries | what it means |
|---|---|
| **no `unreadableAssets` key** | written by a build that never checked. Say nothing about the picture. |
| **`"unreadableAssets": null`** | checked, and nothing was found unreadable in that shot |
| **an `unreadableAssets` object** | assets were found and could not be read; `count` and `assets` name them, and `exitCode` there is 3 unless an opt-out was in force, which `suppressedBy` names |

When it does fire, the census names every asset and the reason too. **The census and the block are
per shot** — they describe the picture in front of you, not what an earlier shot in the same
worklist found — while **the exit code
is per process**: a multi-shot worklist runs every shot and spends the code once at the end, because
stopping at the first degraded shot would throw away the rest of the batch. The run record carries
the outcome `Partial`, opted out or not, so a degraded render is never filed as a clean one.

On a degraded run the white-fallback line says how many of the textures it counted were in fact
found and unreadable, because a line that said "could NOT find" about a file this tool has just
opened would be false. On a healthy run that line is unchanged to the character.

**The opt-out is deliberate and it says so.** `--allow-unreadable-assets`, or
`VP_ALLOW_UNREADABLE_ASSETS=1` in the environment so an existing caller can pass it through without
its command line changing. Either one turns the exit code back to 0 — and when it does, the census
block, the report block and the run record all say so and name **which of the two** did it. An
ambient variable set once in a profile is exactly the thing a reader has to be able to tell from a
flag someone typed, which is why neither is allowed to be silent.

---

## Conventions that bite

- **Coordinates.** The library is Y-up; viewer = `(x, z, −y)` of Skyrim's Z-up. All placement math
  happens in Skyrim space and is swapped once at the end. Camera input is Skyrim-space.
- **REFR rotation is `SpatialOverlap.RefrRotation`, and it is DERIVED, not calibrated.** Skyrim
  builds a reference's matrix with `NiMatrix3::SetEulerAnglesXYZ` (CommonLibVR
  `src/RE/N/NiMatrix3.cpp`), whose written-out entries are exactly `(Rz·Ry·Rx)ᵀ = Rx(−x)·Ry(−y)·Rz(−z)`.
  Two separate things come out of that transpose and BOTH are needed:
  **(1)** the angles are NEGATED — REFR angles are clockwise-positive compass headings (`getangle z`:
  0 = north, 90 = east) against a counter-clockwise-positive right-handed basis;
  **(2)** the axes compose **Z FIRST**, then Y, then X (`EulerOrder.Zyx` under
  `Vector3.Transform`'s row-vector convention, which reverses the written order).
  Part 2 hid for two days behind part 1, and the reason is worth internalising: **for a ref rotated
  about a SINGLE axis every composition order agrees**, and single-axis is nearly every static in
  the game. A convention calibrated on yaw-only geometry passes and stays wrong for everything
  tilted. Numerically: single-axis old-vs-new is bit-identical (max element delta `0.00e+00` over
  2,000 random angles per axis); two or three non-zero angles disagree by a median of 75–122° of
  rotation. If you touch this, test on a ref with all three angles non-zero or you are testing
  nothing.
- **A placed NIF's root transform is not part of its REFR placement.** `NifMeshBuilder` was built
  for portraits and composes every node through the root. viewprobe removes that final root
  transform before applying position/rotation/scale. The game-grounded proof is
  `SHouseIntDoor02`: root rotation −90°, placed heading 270°. Composing both makes the door
  edge-on; Skyrim draws it flush in the north-facing arch. The same correction restores the
  Temple's pier and continuous floor while ordinary back-face culling remains enabled. The
  collision reader the detectors use (housecarl-core `NifGeometry.WorldTransform`, a separate
  pipeline from this builder) got the same rule only on 2026-09-05 (Q28: a walker crossed the line
  its root-turned copy of a Solitude wall cut across a corridor, 9 of 9, three cells), and its
  node-rotation sense was found transposed and fixed the same day against the Creation Kit's
  bounds of 10,834 meshes. viewprobe's own builder was not changed by either.
- **Persistent refs.** Exterior doors live in the worldspace persistent cell, which **masquerades as
  grid (0,0)** — they cannot be identified by cell record (and that masquerade is why the ring test
  admits only non-persistent refs from neighbour cells). viewprobe collects refs by their own
  `Persistent` flag and merges them into the scene by position against the whole ref-loaded area's
  bounds — the 3×3 ring by default, the target cell alone under `--no-ring-refs`.
- **Which plugin's version of a ref is drawn: the last sighting decides** (`WinningRefs.cs`). After the
  load-order walk, a ref is in the winning state if and only if its **last sighting in load order** is
  admitted — in the target cell, or temporary in a ring cell — at that sighting's position and in that
  sighting's cell. It is a persistent-merge candidate if and only if its last sighting is a persistent
  ref of a worldspace. So a later plugin that files a ref in another cell, another worldspace or the
  persistent cell takes the earlier version out; the console says how many
  (`-N ref(s) an earlier plugin placed in the loaded cells and a later plugin moved elsewhere`). An
  override that deletes a ref and carries no placement data is a sighting too (`PlacementExtract`
  reports it in `CellPlacements.DeletedRefs`): the earlier version is not drawn, and a deletion is not
  counted as a move (`--winning-refs-selftest` K6).
  `viewprobe --winning-refs-selftest` holds the rule to known answers on Mutagen-written plugins
  (`VP_WINREF_KEEP=<empty dir>` keeps the fixture plugins there, so another reader can be checked
  against the same files). The merge box includes its low edges and excludes its high edges; a
  persistent ref filed under an ordinary exterior cell merges by position like one in the persistent
  cell; and, because the ring is chosen by grid, a second CELL record at the target's own grid loads
  like a ring cell (pinned by case C9, not verified against the engine).
  `--winning-refs-dump FILE` runs the real walk for a cell (a positional cell, or one per line of
  `--cells LIST`), appends one JSON line per target — its identity, the plugin files walked with their
  sha256, and every winning-state entry with position, cell, persistent, deleted and merge-candidate
  flags — and draws nothing. It filters nothing: it exists so the walk can be compared, reference by
  reference, with an independent reader over many cells without rendering. `--dump-terrain` (with
  `--eye`/`--look`) carries on through the terrain walk and adds each loaded grid square's winning
  heightmap digest and layer count.
- **A plugin, or a record, that cannot be read is refused, never skipped** (`PluginRead.cs`). Every
  walk over the load order used to open each plugin inside a `catch` that moved on to the next one,
  so an unreadable plugin left an earlier plugin's version of everything it overrides in the picture,
  with no warning. Now a plugin that will not open, or fails while a walk reads it, stops the run
  (exit 2) naming the plugin, its place in the order and the walk; so does a profile that lists an
  active plugin no file provides, and a LAND whose heightmap the render's terrain walk cannot read
  (naming the grid square). The terrain walk and the shared floor filing classify each LAND by one
  rule (`HousecarlCore.LandHeight.Classify`, via `TerrainLand.cs`): a LAND whose DATA flag says it
  has heights and that gives none is unreadable (refused, named); a LAND whose flag does NOT say so
  and that has no VHGT DECLARES no terrain, so the square's earlier terrain is removed and nothing is
  drawn or filed there, since the game draws no ground over such a square and has no terrain
  collision (manual lab playtest, 2026-09-27); a cell copy with no LAND leaves the earlier terrain.
  The `--stations` floor takes its heights from the shared floor filing
  (`FloorFeed.FloorIndex`), where a LAND that gives no heights is named
  (`CellPlacements.LandUnreadableReason`) and a station floor over it is refused (exit 2). Its floor
  candidates are the 3×3 by position plus the filing's reach (`FloorFeed.FloorReach`): references
  anchored two cells out, and listed large references three to five out (the scene's own lists and
  box test, `_shared/RingReach.cs`), each admitted when its placed OBND box enters the 3×3. Whether a
  large reference has actor collision at rings 3–5 is not read in game. A heightmap
  Mutagen misreads WITHOUT an exception (a VHGT cut short in the middle of a plugin reads the bytes
  that follow) is not seen there.
  A terrain texture layer that cannot be read changes paint, not ground, so it is not refused: it is
  named on the console and listed in the planes report as `terrainLayersUnreadable` (null when none).
  A conflict-file FormKey that does not parse is named and counted. `viewprobe --plugin-read-selftest`
  damages a synthetic override byte by byte and runs this executable against it, each case in both
  directions.
- **Markers** are classified from the record's Is-Marker header flag (`0x00800000`) ONLY — the
  engine's own don't-render rule. Name heuristics (*marker* EditorID or model path) are wrong in
  both directions and were removed 2026-08-28. `Markers\BlackPlane01.nif` is an unflagged,
  drawable opaque plane (one shape, two triangles), so its path is not evidence that the engine
  hides it. The earlier claim that these refs are floor-gap sealers was refuted by the in-game
  Temple shot: the game floor is continuously textured, and removing the erroneous NIF root
  transforms—not retaining black planes—closes the renderer's bands. Flag-only classification
  remains right; the old explanation of this particular asset does not.
- **Runtime enable state.** A ref with an XESP enable parent takes the PARENT's state (XOR the
  opposite-of-parent flag), chains followed, the player ref (ID 0x14) always enabled — the ref's
  own initially-disabled flag is CK bookkeeping, not the runtime answer (parent = player +
  opposite is the standard permanently-disabled idiom). Refs the runtime enables are drawn; refs
  it disables are dropped even when their own flag is clear. Every ref the scene drops is
  accounted for in ONE console block (disabled via enable parent / own flag / deleted / no
  model-bearing base) — a silent category is how a render missing a third of its content once
  read as fully accounted for. This is a record-derived runtime estimate. The Temple census
  (`1,257` parent-disabled refs, none admitted) has not been independently toggled or observed in
  game, so it is accounting evidence, not an in-game proof that every one stays disabled.
  **The rest of the suite now agrees with the renderer (Q27, 2026-09-06).** Until then viewprobe and
  the placement-contact pass resolved this chain while the obstacle, floor, actor-band and overlap
  populations read the record's own flag; the default population of every physical read is now the
  load-time chain, the old flag-only population is `--flags` on the probes that used to default to it,
  and `viewprobe --flags` is REFUSED — a flag-only scene would draw a room the game never shows.
- **A profile-local `Skyrim.ini` is REQUIRED** in each profile — archive discovery reads its
  `[Archive]` lists. Without it the base-game texture BSAs are invisible and renders silently lose
  all textures.
- **Top-down cameras must stay off vertical.** `TexturedScene` builds an `OrbitCamera` whose
  `Elevation = asin(camOff.Y / dist)`, so a straight-down camera hits gimbal lock. Working recipe:
  put the eye `0.105 × H` south of the target and `H` above it — ~84°, reads as plan, nowhere near
  the singularity. With `--fov 50` the ground span is `0.933 × H`, which also makes the finished
  frame a usable image→world ruler.
- **Interior top-downs work as floor plans.** Interior room shells are single-sided with inward
  normals, so from above the ceiling is back-face culled and you look straight down onto the floor
  plan with furniture and navmesh in place. The converse bites too: from *inside* a building shell
  the walls vanish, which is why an eye-level camera placed inside a tower's footprint sees the
  tower disappear.
- **A top-down sees what the player never can.** Culling the ceiling exposes geometry that mods
  deliberately hide inside walls and above ceilings — light-blocking planes, for one. Such things
  rendering is viewprobe being *correct*; do not "fix" them.

---

## Feature state

- Textured first-person rendering from the exact MO2 profile (BSA + loose, MO2 precedence).
- **World state (2026-09-04)** — `--state <switch>` flips an enable marker and draws the world the
  flip produces; `--state-view diff` tints what arrived green and what departed red. Own section below.
- **Landscape seams and the water arms (2026-09-04)** — `--seams` ribbons every shared cell edge whose
  LAND vertices disagree, coloured by the step; `--water-arm truth|winner` draws the cell water from
  the masters' tuple or the winner's and prints both arms per ring cell. Own section below.
- **Interactive fly-through** (`--interactive`): the same scene in a visible window — WASD +
  mouse flight, hover-to-identify (refs, navmesh, terrain) with a click-to-lock readout, live
  navmesh display mode, a live height clip, pose echo, F12 capture. Its own section below.
- **Walking routes** (`--route`): waypoints in plan view become arrows on the ground, at heights
  the winning navmesh supplies, with every waypoint clearance-checked and every leg between them
  sampled onto the mesh. Pairs with a follower harness — that puts an actor behind you, this says
  where to walk them. `--route-solve` finds the path instead of demanding it, over a connectivity
  graph read from `NAVM`'s own edge-link and door tables, and reports the walked length against the
  straight line. Its own section below.
- **One storey's navmesh** (`--nav-z`) **and a labelled world grid** (`--grid`): `--cull-above`
  slices statics only, so a multi-storey interior top-down carried a navmesh sheet that was the
  union of every floor — unreadable exactly where route planning needs it. Filtering the overlay
  to one storey makes the same shot a walkable floor plan; the grid puts coordinates on it so
  positions are read off the picture rather than derived from the caption pose. Their own section
  below.
- **Worklists** (`--worklist`): a shot list drives both the fly-through's `PageUp`/`PageDown`
  paging and, through `--emit-bats`, the in-game console batch files — one set of coordinates, so
  the render and the tester's standing position cannot disagree. Its own section below.
- **Occlusion planes** (`--planes`, added 2026-09-04): the engine's culling volumes — PlaneMarker
  refs with a PortalBox or Box primitive, which the marker filter drops and the game never draws —
  as a layer of their own, coloured by provenance, hover-identifiable in the window, counted on the
  console whether drawn or not. Its own section below.
- **Terrain:** winning LAND heightmaps for the target cell ± 1 ring, painted with each quadrant's
  winning base landscape texture (LTEX → TXST → diffuse, winner-takes-all), tiled ~256 u/repeat.
  The heightmaps are kept as a queryable field (`terrainHeightAt`, same 128 u quads and diagonal
  split the mesh is built from) so the overlay can ask what is buried.
- **Painted layers (roads) — rendered since 2026-08-27, on by default** (`--no-terrain-layers` turns them off). A Skyrim road is not a placed mesh — it is a landscape texture
  (`LDirtPath01` and friends) painted onto the terrain as a LAND alpha layer with a 17×17
  per-quadrant opacity map, so base-layer-only rendering drops every road, path and ground blend.
  Each alpha layer is its own textured sheet over the base, opacity carried in vertex alpha,
  triangles nobody painted dropped.
  - They ride the library's **decal** channel: blended decals draw after the distance-sorted
    transparent set, in insertion order — the one thing coplanar sheets need, since a distance sort
    between two layers painted on the same triangle is a coin flip and layer order is the author's
    stated intent. The navmesh sheet joins the same channel, added after them.
  - **Opacity `Position` is decoded x-fastest** (`ly*17 + lx`). Settled by seam continuity across a
    cell boundary, not by eye — a transposed decode still draws a road-shaped ribbon, so a picture
    cannot settle it; a seam can. `VP_ALPHA_DUMP` re-runs that test.
  - A quadrant with **no base layer** states no ground at all; the stand-in is its most-painted
    alpha layer.
- Where no texture resolves at all, terrain falls back to an **off-white slope tint**.
- **Navmesh overlay:** filled / wire / both; conflict red is reserved for findings and always wins.
  The wire is drawn by viewprobe itself, in its own flat-shaded pass, so an outline is the exact
  palette colour and the hidden layers can drop the depth test. Every edge is walked in short,
  distance-scaled steps and each step classified `visible` / `subterranean` (below the winning LAND
  surface at its own X,Y) / `hidden` (above ground but behind something, per a world-only depth
  prepass). Dash length tracks camera distance, so a dash is the same size on screen near or far.
- **Adaptive nav colour** (`--nav-color auto`): renders the world once without the overlay, samples
  the pixels under the navmesh triangle centroids, picks the palette colour farthest from
  background + reserved red, and from the route's and grid's colours when either is drawn (entries
  too close to a reserved colour are struck out outright). It projects through `OrbitCamera`'s own
  matrices — the same ones the rasteriser used — so it cannot drift.
- **Sky:** the frame clears to a pale sky blue. Note this is `renderer.ClearColor`, NOT the
  `GL.ClearColor` in `Draw()` — `GlRenderer.Render()` clears the frame again with its own, so
  setting only the GL one does nothing.
- **MSAA** (default 4): the scene is drawn into a multisampled pair and resolved by blit — colour
  AND depth, because the occlusion test reads the depth buffer back.
- **Opaque output:** the colour buffer's alpha is whatever the material shader wrote. Both render
  paths force alpha to 255 before saving — see *the compare stitch* below.
- **The overlay's vertex format** is 9 floats — position, colour, normal — and a ZERO normal means
  "flat, exactly this colour". Every line, ribbon and dash ships a zero normal. Spheres carry a real
  normal and are the one overlay layer drawn with BACK-FACE CULLING: a solid drawn with the depth
  test off paints whichever face the buffer received last, so without culling a sphere shows its far
  hemisphere and shades as if lit from behind.
- **In-image captions:** dark bar, up to 4 lines — panel identity + cell, each red navmesh as
  `FormKey = runtime FormID (N tris)` (ESL-aware for the exact profile rendered), implicated doors
  as `ORANGE door FormKey = FormID`.
- **Every render carries its camera** (2026-08-28). The caption's first line includes the full
  pose — `eye X,Y,Z yaw N° pitch N° fov N°`, in the game's conventions (yaw 0 = north/+Y,
  clockwise; positive pitch looks down), so it pastes back as `--eye`/`--yaw`/`--pitch`/`--fov`.
  Every output PNG (textured single view, compare panels, the stitch, and flat-mode shots) also
  embeds a `viewprobe` tEXt chunk: `args:` — the command line verbatim minus the machine-local
  paths (`--instance/--profile/--game/--out`) — then `pose:` (the resolved eye/yaw/pitch/fov/look)
  and the cell. Replay from `args:` is BYTE-exact; replay from the rounded pose composes the same
  view but a sub-pixel-different frame (measured), so `args:` is the regression-grade replay and
  `pose:` is the human-readable record — which also matters when the camera came from
  `--eye-ref`/`--look-ref`, whose positions move with the load order. Flat mode prints each
  shot's derived orbit pose per line (its camera follows the geometry bounds, so the numbers,
  not the arguments, are the durable record). Read the chunk with any PNG tool, e.g. Python:
  `Image.open(p).text["viewprobe"]`.
- **A worklist shot names its OWN command, not the run's.** `args:` used to be built once per
  process, so every frame of a multi-shot run carried the worklist's command line: replaying it
  re-rendered the whole worklist, and once the worklist file was gone — which is what a driver
  that writes one per render leaves behind — it reproduced nothing at all. Each shot now composes
  a standalone command from the inputs it rendered with: the cell, `--eye` with the shot's
  `--yaw`/`--pitch`/`--fov` (the inputs, at round-trip precision, so the replay takes the same
  code path and lands on the same pixels), the view settings this shot resolved to (`--nav`,
  `--aspect`, `--cull-above`, `--planes`, `--occlusion`, `--state`) and the rest of the process's
  arguments, still minus the machine-local paths. A single-shot run is unchanged: its command line
  already was that command.
  **The exception is a shot the command line cannot express** — synthetic `Props`, several
  coloured `Routes`, a measured path, shot-local `Npcs`. A camera-only command for one of those
  would render a *different* picture and claim to be that frame, which is worse than needing a
  side file, so such a shot keeps the worklist-based string and says so in the same breath:
  `[this shot needs the worklist file: routes 1]`.
  The same per-shot command is written into the shot's `-configuration.json`, its
  `-navmeshes.json` receipt **and the planes report** (`replayArgs`), because a report found on
  disk later needs to name its own frame exactly as the picture does. That is an added identity
  field: nothing about what the report says of the scene changes, and two runs of one shot still
  compare equal.
- **Highlighted refs** keep their own texture and get a three-part orange highlight from one
  centroid-inflated hull (winding normalized geometrically — some door NIFs carry inward normals):
  a solid depth-tested rim where directly visible; a light 0.22-alpha amber wash wherever the hull
  is not frontmost (this is what marks a visible door whose rim hides in a recessed jamb); and a
  deep-occlusion boost that fires only when the occluder is in front of the WHOLE hull — a wall — so
  a hidden door pops as a strong silhouette while a visible one keeps its readable texture.

---

## Interactive fly-through (`--interactive`)

The same scene `RenderToPng` builds, kept resident and drawn per frame into a visible window.
The SCOUTING step — fly to the view, read the identities, then press `P` and give the printed
`--eye/--yaw/--pitch` to a normal run: the reproducible static render stays the deliverable.

**Controls.** `WASD` move (`W/S` along the view direction, so pitching down and holding `W`
descends), `E`/`Space` up, `Q`/`C` down — hold `RMB` and move the mouse to look. Scroll sets
the base speed and `,`/`.` the look sensitivity, both starting from `--fly-speed`/`--look-sens`
and both echoed to the console as a paste-ready flag; `Shift` ×5, `Ctrl` ×0.2 applies to every
rate, movement and clip alike. `N` cycles the navmesh display, `R` shows/hides the
walking route, `L` shows/hides the occlusion planes (hover one for its identity and what stands
behind it; hover a wall and the caption names the planes the ray goes on to cross, since their
dashed outlines are in the picture), `O` cycles the occlusion preview `off → on → diff`
(re-evaluated as you fly), `[`/`]` and `;`/`'` move the
height clip, `\` turns it off (all four below). `P` prints the current pose as a paste-ready
`--eye X,Y,Z --yaw N --pitch N --fov N` line. `F12` saves a captioned PNG (window resolution,
standard caption bar + `viewprobe` tEXt chunk with the pose) into `--out`. `PageUp`/`PageDown` step a `--worklist`'s shots and `Ctrl+PageUp`/`PageDown` its cells
(below). `Ctrl+C` copies the selection. `H`/`F1` raises the help card; `Esc` closes the card, or
quits when it is already closed. Closing the window prints the final pose, so the last view is
never lost.

**Every one of those, except `WASD` movement itself, is also a widget on the control bar at the
top of the window** (added 2026-08-30), labelled with its key. See below.

**The control bar** (added 2026-08-30) — a panel across the top of the window carrying every
live control as a widget, each labelled with the key that also reaches it. Before it, the keys had
no on-screen presence at all: the HUD reported the RESULT of pressing one (`nav both`,
`clip z 128..2048`) but never that the control existed or which key reached it, so the feature set
was discoverable only by reading the source.

- **Toggles and modes.** The navmesh display is four buttons (`off`/`filled`/`wire`/`both`) with
  the current one lit — all four visible, rather than one keypress into a cycle you have to
  remember. The route is a lit/unlit toggle.
- **Numbers are typeable AND draggable.** Fly speed, look sensitivity and the two clip ends each
  get a slider and a number box driving ONE value: click the box and type (`Tab` moves to the next
  box, `Enter` commits, `Esc` cancels the edit), or drag the slider. A slider alone cannot hit
  "ceiling 2048"; a box alone cannot sweep a ceiling down through a roof and watch it melt. Speed
  and sensitivity are on log tracks — 20..20000 u/s linear would put the whole usable indoor range
  in the first two pixels. The clip tracks span the SCENE's own static z-extent, padded, so their
  ends are somewhere a roof actually is. The wheel works on whatever widget it is resting on
  (clip ends step 64 u and seed from the camera's z on first touch, exactly as `[`/`]`/`;`/`'` do);
  anywhere else it is the fly speed it has always been.
- **Steppers** for a `--worklist`'s cell and shot, with the shot's own label beside them.
- **Actions**: print pose, save PNG, help, quit, and two display toggles — `pose` (the camera
  numbers on the caption, default OFF) and `glow` (the hover highlight, default ON) — plus a
  chevron that collapses the bar to a one-row strip when the picture matters more than the
  controls.
- **The selection row** (added 2026-08-31). `LMB` on something puts its **FormKey**, **runtime
  FormID** and **Data-relative mesh path** on the bar as three fields; **clicking a field copies
  that value**, and `Ctrl+C` copies all three as one tab-separated row with no header, so repeated
  copies into a sheet accumulate a table. What a pick is FOR is the identity, and what anyone does
  with an identity is paste it — reading it off a caption and retyping it is where the digit gets
  dropped. Long values shorten on screen and copy WHOLE, and they shorten in the direction that
  keeps the part that identifies them: a mesh path loses its head (`...\whiterun\WRIntBanner.nif`
  — cut at a folder boundary, not mid-word), a FormKey loses its tail, because the hex id is at
  the front and the plugin name padding it can run to forty characters. Navmesh offers a FormKey,
  a FormID and its triangle index; bare terrain has no record identity and says so. The row takes
  itself off the bar when nothing is selected.
  - `Ctrl+C` costs `C` its Ctrl case as a descend key — `Ctrl` is also the slow modifier, so the
    two gestures collided. `Q` still descends with `Ctrl` held, at the same slow rate.
- **The bar and the keys are one piece of state, not two.** Every widget is a pair of delegates
  onto the loop's own variables rather than a copy of them, so pressing the key moves the widget
  and dragging the widget does exactly what the key would have done. There is no second
  implementation to keep in step.
- **What the run cannot do is drawn GREYED, not hidden** — a run with no navmesh still shows the
  display control, carrying the sentence the key already printed. A control that vanishes and a
  scene that is missing something look alike from the outside, and only one of them is a finding.
- **A cursor over the bar is not over the scene.** Picking is FROZEN while it is up there rather
  than reset, so reaching for a widget does not wipe the identity you were reading, and a click
  meant for a button cannot also lock whatever the panel is drawn over.
- **The hover highlight** (added 2026-08-31, `glow`, default on) lifts whatever is under the
  cursor so it is obvious what a click would select. It rides the material shader's own EMISSIVE
  term, which the renderer already feeds per mesh, so it costs five field writes and **not one
  extra draw call**. The shader's term is `finalColor += emissiveColor * emissiveMultiple *
  baseColor` — it brightens a surface in proportion to its OWN albedo rather than washing it flat,
  which is what lets a dark stone and a pale banner both read as picked without either becoming a
  silhouette. The mesh's real emissive settings are saved and handed back when the cursor leaves,
  including on the way out of the window. A projected covering lights with its base, the way the
  clip already hides them together. Refs only: navmesh and terrain carry no `GlMesh`, and they
  already answer on the caption. Measured on a two-arm capture: 10.1% of pixels change, and where
  they do the mean goes (62.8, 66.0, 61.3) → (71.5, 85.7, 94.2) — brighter, and blue-shifted.
- **It is not in a capture.** `F12` grabs the frame before any overlay AND with the hover
  highlight taken off, so a capture still looks like a render and not like a screenshot of a tool.
  `VP_FLY_HUD=1` is the only way to see either, and it deliberately keeps both.
- **Cost.** Layout runs every update frame (a rect that does not exist cannot be clicked) and the
  panel re-rasterises only when something visible changes. Measured on this machine, 1440-wide bar,
  18 widgets: layout 0.005 ms/frame, rebuild 2.1 ms. Text is the whole of that rebuild — 40
  `DrawText` calls cost 6.4 ms against 0.03 ms for the allocation, background, sixty box fills and
  the pixel copy together — so each string is rasterised once and thereafter blitted, which is what
  keeps a slider drag or a held clip key smooth.

**What the caption still carries, and why it is nearly empty.** The nav mode, the route, the clip
band, the fly speed, the look sensitivity and the worklist position are all WIDGETS now, so
repeating them under the bar was two readouts of one fact. What is left has no other home: the
camera pose (behind the `pose` toggle, default off — `P` prints it and `F12` burns it in, so
holding a row of the picture open for it permanently was clutter), and the two BUILD-time knives,
`--cull-above` and `--nav-z`, which no key can undo and which a scene missing its roofs would
otherwise read as a finding. With nothing hovered and the pose off, the caption is gone entirely
rather than sitting there as an empty band.

**`H` raised nothing in the window until 2026-08-30, and the report was fair.** The key worked; it
printed the controls to the CONSOLE, which is not the window the person flying is looking at. It
now raises a help card over the picture AND still prints, because the console is where a value gets
copied from. The two come from one list, so they cannot drift apart. `Esc` closes the card before
it quits anything — reflexively pressing `Esc` while reading the line that says `Esc` quits should
not cost you the pose you flew to.

**Navmesh display, live** (`N` cycles `off → filled → wire → both`). Under `--interactive` the
build makes BOTH the translucent sheet and the wire whatever `--nav` asked for, so switching is
a visibility flip, not a rebuild — at the cost of the wire's classification pass on a run that
only wanted a sheet, paid once at startup. `--nav` still chooses what the window OPENS showing,
and `--nav off` leaves nothing to cycle (the key says so). The finding's red outline stays on in
every mode that draws navmesh at all, exactly as in a static render. The sheet's alpha is the
fiddly part and is reproduced rather than approximated: the material shader multiplies baked
vertex-colour alpha by `MaterialAlpha` and the static path bakes the same number into both, so a
mode's on-screen alpha is its value SQUARED — the sheet is baked once at the `filled` value and
the loop sets `MaterialAlpha = target²/baked`. Measured: the ratio between `both` and `filled`
is 0.1959 in the window and 0.1958 in a static render of the same scene.

**Height clip, live** (`[`/`]` move the CEILING down/up, `;`/`'` the FLOOR — one row apart on the
keyboard, top row for the top clip). Each end starts disabled and, on first touch, at the
CAMERA's own z, so "clip off everything above my head" is one keypress from wherever the flight
reached — the top-floor case. `\` turns both off. The band sweeps at 512 u/s while held (`Shift`
×5, `Ctrl` ×0.2) and every release prints it, including the equivalent `--cull-above` line.

It is SHAPE-granular: a static is hidden only when its whole z-span lies outside the band, so a
wall spanning the cut keeps standing whole. `--cull-above` remains the triangle-exact knife and
runs at build time; this is the one you fly with, and its printed number is what you then give
`--cull-above` for the deliverable. Statics only, like `--cull-above` — terrain, navmesh, actors
and the overlay are never clipped. **The ray respects it**: a shape the clip removed, or navmesh
with the display off, is not pickable, because a HUD naming what the picture no longer shows
would be worse than no HUD.

**The build census prints for a fly-through too** (fixed 2026-08-30). Shape count, `--cull-above`
tally, covering census and the unshadeable-shape line used to sit after the PNG save, and
`--interactive` returned before reaching them — so the numbers a person uses to decide whether a
picture can be trusted were missing from exactly the mode where they cannot be read off a burned
caption. They now print BEFORE the window opens, which is when they are useful. The absence was
briefly mistaken for "no coverings were drawn"; a missing report and a missing feature look alike.

**Read `(N hidden)` for exactly what it is: the LIVE band's count, and nothing else.** It counts
the shapes this band is holding down right now — it says nothing about a build-time
`--cull-above`, whose triangles were dropped before the window opened and which no key can bring
back. A run carrying one now declares it on the bar as `SLICED at build: --cull-above N
(permanent)` (added 2026-08-30), because without that the bar could read `clip: off` over a scene
missing its roofs, and missing roofs read as a finding. `(0 hidden)` means "this band is currently
removing nothing" — never "nothing has been removed".

**Hover identification** (the cursor is free whenever RMB is up; a caption line under the control
bar shows the answer, and the shape under the cursor lights up). `LMB` **SELECTS** it: the full
detail prints to the console and the identity moves to the control bar's selection row as three
click-to-copy fields, where it stays while the camera moves. The next `LMB` anywhere, or the row's
`clear`, releases it. While something is selected, hover picking is suspended, so the readout
cannot drift.

The caption is 15 px, the burned PNG caption's own size. It had been doubled to 30 px so a FormID
would stay legible while flying; the size came back down on 2026-08-31 because a FormID is no
longer read off it — it is a field you click. The caption still WRAPS and grows to fit, because a
mesh path is long, and it takes NO space at all when it has nothing to say. What it reports:

- a placed ref answers `REFR FormKey = runtime FormID — base EditorID — mesh path`; selecting it
  adds the base FormKey/FormID, placement and scale to the console, and puts the three copyable
  values on the bar;
- navmesh answers `NAVM FormKey = FormID tri N` (tagged `[RED — the finding]` when implicated),
  and the click detail includes a ready-made `--red "FORMKEY:tri"` spec;
- bare ground answers the LAND census: cell, quadrant, base layer, and every painted alpha
  layer's opacity AT THAT POINT (sampled from its own 17×17 map).

Picking is a CPU raycast against the same world-baked triangles the GPU draws (per-shape AABB
shortlist, then ray-triangle), NOT a depth-buffer unproject — so unlike `--pick` it can name
alpha-blended geometry, and it knows WHICH shape it hit. It is geometric: a hit on the
transparent part of a leaf texture still reports the leaf's ref. Navmesh sits a hair above the
floor it covers, so a floor pixel usually answers navmesh first; nudge the cursor off the mesh
to name the floor, or press `N` twice to take the sheet off it. NPC actors are not pickable
(their CPU geometry lives in the character pipeline's VMs) — they already carry captions and
pucks.

**What stays frozen at the entry camera, and why that is sound.** The overlay's
camera-dependent work — wire dash sizing, the visible/hidden classification, the adaptive nav
colour, the cell outline's camera-facing ribbons, diff-sphere radii — is built ONCE, for the
camera the run started with. The depth-buffer-is-the-arbiter design is what makes that correct
rather than merely cheap: the solid wire is clipped per pixel per frame by the real depth
buffer, and the always-visible dashes cover what it clips, so flying somewhere new never makes
navmesh disappear — at most a dash is sized for where the camera used to be. Cosmetic, not
semantic.

**Scope.** One free camera, so `--compare` is refused (fly first, then run the compare from a
pose the flight printed). Everything else composes: `--conflicts`/`--red` paint red exactly as
in a static render, `--npc` actors stand in the scene, `--cull-above` slices before the window
opens, and the live clip then works on what it left. The window is resizable; `--msaa` sets its
multisampling (the F12 capture resolves it). The `VP_FLY_*` family (env table above) exercises
the loop, the ray, the display mode, the clip, the control bar and the help card headlessly — that
is how the feature is regression-tested, and `VP_FLY_FRAMES` runs are byte-identical between runs
(re-measured 2026-08-30 with the bar drawn: same md5 across repeat runs, and the bar contributes no
non-determinism because its only camera-independent input, the cursor, is parked off-panel under
`VP_FLY_FRAMES` along with everything else the loop refuses to read).

**What the loop can and cannot change.** Everything live is visibility or input state — the
navmesh display mode, the height band, the flight feel — so none of it rebuilds geometry and
none of it can change what a static re-render of the same pose produces. Verified: a static
render before and after this feature landed is byte-identical, interior (`--nav both
--cull-above`) and exterior (`--nav both --conflicts`, red navmesh + cell outline + terrain
layers) alike. Re-verified when the control bar landed (2026-08-30): same cell, same camera,
pre-change and post-change binaries, `cmp`-identical PNGs — the bar is an overlay on a window that
the static path never opens.

---

## Worklists (`--worklist`, `--emit-bats`)

A worklist is a SHOT LIST: cells, and per cell the camera poses worth looking at, each carrying the
sentence that says why. One file, two consumers — the fly-through and the in-game console — so the
picture a render shows and the place a tester ends up standing come from the SAME coordinates and
cannot drift apart. They did drift, by hand, in the 2026-08-28 field run.

Model and validation: `_shared/Worklist.cs`. Read and checked BEFORE the load walk, because a
malformed shot list found after a multi-minute walk is a multi-minute walk wasted.

```json
{
  "label": "what this list is for",
  "eyeHeight": 120,
  "cells": [
    { "cell": "FalkreathExterior03",
      "shots": [
        { "label": "the sentence that says why this shot is worth a look",
          "eye": [-26671, -84175, -2117],
          "floorZ": -2237,
          "feet": [-26671, -84175, -2237],
          "yaw": 0, "pitch": 0, "fov": 65,
          "route": [[-26671, -84175], [-26520, -84900], [-26372, -85500]],
          "doors": ["06149D:Some Plugin.esp"],
          "note": "optional; printed on arrival and in the bat manifest" }
      ] }
  ]
}
```

- **`eye` is the CAMERA, `floorZ` is the FLOOR.** They are separate fields because the two consumers
  need different ones — the render wants the eye, `player.setpos` wants the floor, and giving it the
  eye buries the tester's feet. Source `floorZ` from `--nav-at`, which answers *standable*; a
  `--pick` answers *some surface is here*, which is not the same question. With no `floorZ` the
  generator falls back to `eye.z − eyeHeight` and SAYS it assumed rather than measured.
- **`feet`** (added 2026-09-03) is where the PLAYER stands for the shot — `[X, Y, Z]` at the floor,
  the point the planner's placement test passed. `shotplan.py plan` writes it on every shot; a
  runner takes its spot from here rather than typing a coordinate at the storey's height (a
  multi-storey interior's floor can be a ring round a void, and a typed spot dropped a player 765 u
  onto the storey below). Absent means "the eye's X,Y at `floorZ`". The render ignores it.
- **`doors`** (added 2026-09-03) lists the FormKeys of the drawn Door refs near the shot's subjects,
  for a runner to close (`ObjectReference.SetOpen(false)`) before driving the camera: the records
  say a door is closed, and the game can leave its leaf standing open across the only clear
  bearing. `shotplan.py plan` fills it from `--refs-at` within `--door-radius`. The render ignores
  it.
- **`look: [X,Y,Z]`** may replace `yaw`/`pitch`, resolving the same way `--look` does.
- **`planes`** (true/false) and **`occlusion`** (`off|on|diff`) are optional per-shot overrides of
  `--planes` and `--occlusion` (added 2026-09-04), reset to the command line's values at the top
  of every shot like `nav` and `cullAbove`. A shot list can therefore turn the planes layer on for
  the shots that are about planes and leave the rest clean.
- **`nav`** and **`cullAbove`** are optional per-shot overrides. `nav` applies exactly (it is a
  visibility flip). `cullAbove` in the window drives the SHAPE-granular live band, not the
  triangle-exact knife — the console says so on arrival rather than letting the two quietly differ.
- **`route`** is the path to WALK from this shot — waypoints as `[X, Y]` pairs, at least two, **no
  z**: it is resolved from the winning navmesh against this shot's own `floorZ`, every waypoint is
  clearance-checked and every leg sampled onto the mesh before anything is drawn. Place a waypoint
  only at a direction change over ~10–15° or a sight-line break — see *Walking routes* for why, and
  for what the legs do in between. A shot's `route` overrides a command-line `--route`. No per-shot override leaks into the
  next shot: `nav` and `cullAbove` are reset to the command line's values at the top of every shot
  (since 2026-09-02 — before that they leaked, and an eye-level shot after a sliced top-down lost
  its ceiling).
- **`travel`** on a cell overrides the derived console travel command; normally absent.
- **`props`**, **`routes`**, **`npcs`** (added 2026-09-02) put a measured scene back into the picture —
  what a runtime test stood in the cell, where the actor actually walked, and the actor itself:

  ```json
  { "label": "block 64 u across, passage 0 u — STALLED",
    "eye": [-1000, -5462, 687], "yaw": 0, "pitch": 89, "floorZ": 47, "cullAbove": 190,
    "route":  [[-1320, -5450], [-680, -5450]],
    "props":  [{ "form": "0B11CE:Skyrim.esm", "pos": [-1000, -5502, 47], "yaw": 90, "scale": 1, "label": "the block" }],
    "routes": [{ "points": [[-1320, -5450, 40.1], [-1264, -5458, 33.2], [-1054, -5498, 34.9]],
                 "color": "magenta", "label": "attempt 1, stalled", "caption": "" }],
    "npcs":   [{ "form": "000007:Skyrim.esm", "pos": [-1320, -5450, 39], "heading": 90, "label": "walker at spawn" }] }
  ```

  - **`props[]`** — synthetic placed objects, one per entry: a BASE record's FormKey (STAT, MSTT, …
    — it must carry a model, or the shot is refused), `pos` `[X, Y, Z]` as the engine reported it
    (z is NOT taken from the navmesh), `yaw` in degrees (`getangle z` convention), `scale`. The ref
    is invented for the render (a high ID in the base's own plugin, printed as `+prop …
    (synthetic …)`) and exists nowhere else. Shot-local: shot 2 does not inherit shot 1's props.
  - **`routes[]`** — further paths, each in its own colour, drawn alongside `route`. **The width of
    a point decides the kind**, and a route is all one kind: `[X, Y]` points are a PLANNED route,
    resolved onto the navmesh and refused off-mesh exactly as `route` is; `[X, Y, Z]` points are a
    MEASURED path — positions the engine reported — drawn through exactly those points, never
    resolved against the mesh and never refused, because the mesh is what the measurement is being
    compared with and cannot be allowed to edit it. Samples the mesh does not cover (no navmesh
    within 96 u of the sample's z) are counted in the console and the caption, not hidden. Per
    route: `color` (a `--route-color` name or R,G,B; absent inherits `--route-color`), `label`,
    `discs` (absent: the run's setting for a planned route, off for a measured one), `arrows`
    (`auto` | `end` | units, as `--route-arrows`), and `caption` — absent draws the automatic
    coloured line (`ROUTE …` / `PATH …`), a string draws that instead, and `""` draws none, for a
    shot carrying twenty paths whose meaning one summary line already states. Every route's colour
    is reserved from the nav wire's auto-pick.
  - **`npcs[]`** — the worklist form of `--npc`, same rules: `form` is the placed ACHR or the NPC_
    base, `pos` `[X, Y, Z]` overrides the ref's own placement (and is required when the base is
    placed nowhere — the player base `000007:Skyrim.esm` is the standing case), `heading` in
    degrees, `label` for the caption. Added to the command line's `--npc` set for this shot only.

### Dropping one on the exe

`viewprobe.exe` takes worklists as bare positional arguments, so **dragging one or more `.json`
files onto the exe in Explorer opens them** — no shortcut, no flags, no shell. `viewprobe tour.json`
on the command line is the same thing, and `--worklist` still works and is now repeatable.

A leading positional is read as a worklist when it is an existing FILE on disk named `.json`. That
test cannot misfire, because no CellEditorID and no FormID is ever a path to a file — so
`viewprobe FalkreathExterior03` is untouched. A `.json` after a flag is picked up too, since
`viewprobe --interactive tour.json` is the obvious thing to type once the positional form works.

- **Several at once** concatenate, in the order Explorer passes them, into one run — cells appended,
  labels joined. The one thing that must AGREE is `eyeHeight`: it places every camera that carries
  no measured `floorZ`, so adopting the first file's value would move the other file's shots off
  the ground they were authored against. Mismatched, it refuses and names both files.
- **A drop opens the FLY-THROUGH**, not a batch render. Static mode writes numbered PNGs into a
  folder you would then have to go and find, and pays the load walk once per SHOT; the fly-through
  pays it once per CELL and puts the scene on screen, which is what dragging a file onto an icon
  means. Only the GUI launch is affected — every shell run keeps the behaviour it always had.
- **The console window stays open** on a GUI launch, on every exit path including the config-missing
  one, which exits from inside `HarnessTarget.Require` before the arguments are even parsed. The
  pause is registered first thing for exactly that reason: a window that closes on the one message
  explaining how to configure the harness has taught nobody anything.
- **The wrong `.json`** is the likeliest mistake here, since the only thing distinguishing a
  worklist is its extension. A file with no `cells` is named for what it actually is — a detector
  conflict emit says so, and points at `--conflicts`. A dropped FOLDER is refused up front, with a
  count of the `.json` files in it, rather than after a multi-minute load walk; folders are not
  swept, because the files beside a worklist are as likely to be conflict emits and one of those
  would fail the whole run.

**How a GUI launch is told apart from a shell:** the process owns its console ALONE, and neither
stream is redirected. Measured on 2026-08-29 — Explorer gives 1 console process and no redirection;
Git Bash gives 3 and redirects; `cmd` gives 4 and redirects. Both halves are load-bearing: mintty
redirects without adding a process, and `> out.txt` from a real console adds a process without
redirecting stdin. The pause runs from an `AppDomain.ProcessExit` handler, which was verified to
fire through `Environment.Exit` and to have no shutdown time limit on .NET 9.

### Stepping it

`--worklist` alone renders every shot to its own PNG — unless the run was a DROP, see above.
One BUILD per shot: shots in the same cell do not share a walk, because the render happens
inside the build rather than after it. The run says so when it matters.

`--worklist --interactive` is the cheap way round: one build per CELL, and `PageUp`/`PageDown` are
then free camera jumps with everything already resident. `Ctrl+PageUp`/`PageDown` change cells,
which no camera move can reach — the refs, terrain and navmesh all have to be walked again, so the
window closes, rebuilds and reopens on that cell's first shot (its LAST shot going backwards, the
way turning a page back works). The HUD carries `[cell i/N · shot j/M] label` throughout.

### Durable static selections (driver interface)

The render-batch driver can run part of one unchanged worklist with three paired operational
arguments:

```
--worklist-selection selection.json
--progress-dir receipts
--worklist-readiness-sha256 <64 hex characters>
```

All three are required together, with exactly one worklist in static mode. The selection pins the
worklist and readiness digests and names ordered zero-based `{cell, shot}` coordinates. Viewprobe
loads and validates the whole original worklist, then skips unselected coordinates in its existing
loops; it never slices or renumbers it. Filenames, console markers, reports and cell leases therefore
keep their original coordinates, and a cell with no selected shot is skipped before a `CellScene`
is constructed.

A successful selected shot gets one create-new `cNsN.json` receipt after every frame, report,
scene-depth file, mask and applicable sidecar writer has returned and closed. The receipt hashes the
exact writer-declared files. Report validation is streaming and bounded: its image, depth digest,
mask references, replay and label must close over those same artifacts. A nonzero shot, malformed
selection/report, stale worklist/source map, missing or extra artifact, or existing receipt is a
terminal nonzero refusal and never produces a success receipt. Unsuppressed unreadable assets are
also ineligible: because archive extraction failures are process-accounted rather than reliably
shot-local, once one occurs the selected process conservatively writes no later success receipts
and says why; the normal whole-worklist exit still spends exit 3 at the end.

Composite campaigns also carry an immutable source-map file and exact file-byte SHA-256. Every
selected row binds the copied shot to its original one-shot worklist/hash, canonical shot hash,
label, caller artifact list, output stem and safe source-ordinal scratch directory. Viewprobe writes
the original basenames directly before reports and masks are generated; a worklist-dependent replay
names the original source worklist and contains none of the transient selection, progress,
readiness or composite paths. Publishing the validated raw files to caller destinations remains the
external driver's create-new, hash-verified job; viewprobe never rewrites a report after rendering.
Every source must still be exactly one cell/one shot, and its worklist-level and cell-level options
must equal the composite's; scratch destinations are unique across the complete map.

### `--emit-bats`: the same list, for the console

`--emit-bats NAME` writes the worklist as Skyrim console batch files into `<instance>/mods/NAME` —
an MO2 mod folder, which is how the files end up scoped to a PROFILE instead of loose in the game
directory. A bare name resolves under `mods/`; anything with a directory part is used as a path.
The tester types `bat bat1`, `bat bat2`, … and the folder carries a `MANIFEST.md` mapping every
number to its shot.

This is the one consumer that takes exactly ONE worklist: the `MANIFEST.md` names the file it was
generated from, and a merge of several dropped lists has no such file. Emit them one at a time.

**Bat files do not survive a load screen** — the rest of the file is discarded when the cell
changes. That is the whole reason for the numbering: travel is its own file, and every pose after it
is another, so the second half of a batch is never lost to a loading door.

```
bat1.txt   cow Tamriel -7,-21          <- travel; derived from the cell's own worldspace + grid
bat2.txt   tcl / setpos x,y,z / setangle z,x / tcl    <- shot 1
bat3.txt   ...                                       <- shot 2
```

Two things it answers from DATA rather than memory, both of which went wrong in the field:

- **The travel command.** `coc <EditorID>` for an interior, `cow <WorldspaceEditorID> <gx>,<gy>` for
  an exterior, resolved from the cell record. A card once named a cell that does not exist; a
  worklist naming a cell the load order lacks is refused outright, before anything is written.
- **Whether the spot is standable.** Five probes — the point and ±64 u in x and y — all required on
  a drawable triangle at the same floor, because the player capsule is ~35 u in radius and a point
  technically on navmesh but hard against a wall still embeds you. A failure is REPORTED, in the
  console and the manifest, and the file is written as authored: silently moving the tester would
  put the render and the game back out of agreement, which is the one thing this exists to prevent.

`--bats-profiles A,B` enables the mod in those profiles and disables it in every other profile of
the instance, backing each `modlist.txt` up once to `modlist.txt.pre-viewprobe-bak`. **MO2 rewrites
`modlist.txt` from memory when it exits or switches profile**, so close MO2 first or press F5
afterwards, or the edits are silently clobbered.

### What the bat cannot do

`player.setangle z` (which way you face) is reliable. `player.setangle x` (how far up or down you
look) is written, but the first-person camera often overrides it almost immediately — if the
vertical aim does not stick, the manifest carries the intended pitch to look by hand. Most
eye-level shots are pitch 0, so this mostly does not bite.

---

## One storey at a time (`--nav-z`), and the grid (`--grid`)

These two exist because planning a route in a multi-storey interior used to cost half a dozen
render/probe round-trips and still end in a `route REFUSED`. Together with `--route-solve` they
make it a one-render job.

### `--nav-z Z[,TOL]` — the other half of the knife

`--cull-above` slices **statics only** — terrain, navmesh, actors and the overlay are never sliced.
So a sliced top-down of a five-storey interior shows one storey's architecture under a navmesh
sheet that is the **union of all five**, and *"where can I walk at z 1424"* cannot be read off the
picture at all. `--nav-z` filters the overlay to one storey, and the pair becomes a true walkable
floor plan.

```
--cull-above 1600 --nav-z 1424        # architecture of the ground floor, navmesh of the ground floor
--cull-above 1600 --nav-z auto        # ...taking the storey from --route-z or the shot's floorZ
--nav-z 1424,32                       # a tighter band
```

**Range, not centroid.** A triangle is on the storey when its own z RANGE meets `Z ± TOL`. A
staircase's bottom step belongs to the floor it rises from, and a centroid test drops it the moment
a step is taller than the tolerance; a triangle that genuinely spans two storeys is drawn on both,
which is what it is. `TOL` defaults to **64** — half the ~128 u between floors in most kits, so two
storeys can never both qualify.

**It carries its own positive control**, and this is not optional: a band with no triangles renders
exactly like a band that works (an interior with no cyan on it), and this project has twice
mistaken one for the other. So every run says how many triangles survived AND lists every height
band the cell actually has:

```
nav-z 1424 ±64: 199 of 576 drawable triangle(s) kept, 377 on other storeys
nav-z storeys in this cell (by triangle centroid): z -30000 (1), z 1424..1426 (197), z 1495 (1),
  z 1566 (1), z 1662..1840 (158), z 1904..2001 (164), z 2070 (1), z 2166..2192 (2), z 2288..2290 (51)
```

That turns *"nothing showed up"* into *"you asked for 1424, the floors here are at 1712 and 2000"*
without a second run — and it is how you discover a cell's storeys in the first place: pass any Z
and read the list. **An empty band is refused outright**: a picture showing no navmesh where the
author asked for one storey's is a wrong answer, not a thin one.

The caption says `NAVMESH z N ±T ONLY`, for the same reason `--cull-above` says `SLICED above z N`:
navmesh missing from four fifths of a floor plan is a finding unless the picture says it was
filtered. The fly-through inherits the filter (the other storeys' triangles are never uploaded, so
`N` cycles the modes over one floor and no key widens it) and its HUD says so.

### `--grid N|auto` — coordinates read off the picture, not derived from it

Without a grid, turning something seen in a top-down into an X,Y means arithmetic done by eye
against a screenshot and the caption's pose. That arithmetic works and it is where silent errors
come from — a route planned off a mis-scaled pixel measurement looks exactly like one planned off a
correct one. The grid deletes the step, and it makes a plan far easier to *hand to someone*, which
matters when the output is "here is the path to walk".

```
--grid auto        # a 1-2-2.5-5 round spacing, ~9 lines across the frame
--grid 250         # exactly this
--grid-color pink  # reserved against the nav wire's auto-pick, like the route's
```

- **The plane matters.** A grid is a plane in the world, so one ruled at the wrong storey projects
  to the wrong pixels and every coordinate read off it is wrong by exactly the parallax the flag
  exists to remove. The chain is the storey the shot is about: `--nav-z`, then `--route-z`, then
  the shot's `floorZ`, then the aim point — and the legend burned bottom-left names which was used,
  labelling the last as `ASSUMED`.
- **Labels ride the frame edge**, found by projecting each line and clipping it to the frame, so
  they are right for a rotated camera where "constant X" does not run down the screen. Two labels
  that would collide are not overprinted — the later one is dropped, and its neighbours a spacing
  away are enough to interpolate from.
- **Declined, never drawn badly.** At eye level the lines converge on the horizon and the near
  cells swallow the frame: below 60° of downward pitch, or with the horizon in shot, the run prints
  why and draws nothing. A spacing that would rule more than 400 lines is declined the same way —
  that is a hatch, not a scale.
- **Static renders only.** Its labels are screen positions computed for ONE pose and would be wrong
  the instant the camera moved, so `--grid` with `--interactive` is refused, pointing at the hover
  HUD, which answers the same question continuously.

**Verifying it, which is worth doing once after any change here:** render the same camera with and
without `--grid`, diff the two PNGs to find the drawn line pixels, then `--pick` those pixels and
compare what comes back against the labels. Measured on a 2,532 u frame: every line sat within
~2 u of the coordinate its label claimed, which is well under one pixel.

---

## Walking routes (`--route`)

Waypoints in plan view, drawn as **straight segments with an arrowhead pointing from one waypoint
to the next** — painted flat on the ground at the height the navmesh gives, so the path conforms
to a sloping walkway or a staircase without the author measuring anything.

```
--route 116750,109100 --route 117400,110200 --route 119000,112700
```

or, as a worklist shot's own field (`_shared/Worklist.cs`):

```json
{ "label": "...", "eye": [...], "floorZ": -6912,
  "route": [[116750,109100], [117400,110200], [119000,112700]] }
```

**How many waypoints.** A waypoint is a *vertex*, not a breadcrumb. Place one only where one of
two things is true:

1. **The direction changes** by more than ~10–15° from the previous leg. Below that the path is
   straight enough that a waypoint adds a disc and tells the reader nothing.
2. **A sight line breaks** — the next waypoint would be over a horizon, round a corner, or behind
   an obstacle from this one. The crest of an arched walkway is the standard case: from the foot
   of the arch the far side is simply not visible, so the crest earns a vertex even though the
   walkway runs dead straight through it.

Rule 2 is checkable rather than felt: an eye at `floor + 120` at the start against the crest's
own surface height says whether the far leg is visible at all. On this project's own test
walkway the eye clears the crest by **8 u**, so everything past it is over the horizon — one
interior waypoint, no others, on a path that had seven.

Sparse waypoints are why the **legs** are validated and ground-sampled rather than drawn as
chords: with a vertex every 250 u a chord is harmless, and with a vertex only where the direction
turns it is a straight line through 700 u of ground nobody looked at.

**X and Y only. Z comes from the winning navmesh**, through the same lookup `--nav-at` answers
with. Two reasons, and the first is the load-bearing one: a route must sit on *walkable ground*,
not on whatever surface a pick happens to return. The second is free convenience — letting the
mesh supply z makes the arrows follow the floor across a slope, a ramp or a flight of stairs.

**Which storey.** Several floors can cover one X,Y, so the resolver needs a reference height and
takes the first of: `--route-z Z`, the shot's measured `floorZ`, then eye minus `--eye-height`.
That last one is an assumption and is labelled as one in the console — it is only sound for a
camera *standing on the route's own floor*, so **a top-down camera should pass `--route-z`**, or
it will pick the storey nearest its own eye-level-minus-120, which is meaningless from 1,600 u up.
After waypoint 1 the reference **chains**: each waypoint resolves against the height the previous
one landed on, so a route that climbs stairs walks up with them. Every waypoint whose X,Y is
covered by more than one storey prints the heights it passed over, so a wrong pick is visible in
the transcript instead of silent.

**Every waypoint is clearance-checked** — the same five-probe test `--emit-bats` runs (the point
plus ±64 u in x and y, all required to land on a drawable triangle on the same floor), calling the
same code, so a spot a bat file calls standable and a spot the arrows walk over cannot disagree.
Per waypoint the console reports `covered 5/5`, `TIGHT n/5`, or `OFF-MESH`.

**Every leg is sampled too**, every 32 u along its length, and each sample must find walkable
ground on the same floor. Checking only the endpoints was defensible while waypoints were dense;
it is not once they mark direction changes alone, because then two valid waypoints can be joined
by a line that steps off a walkway into thin air — a case where *both* endpoints report
`5/5` and the path between them is a fall. The sampling is not a second pass: it is the same walk
that produces the drawn geometry.

**OFF-MESH IS A HARD ERROR — for a waypoint AND for a leg.** The run refuses, writes no image, and
exits non-zero. This is the one place the route deliberately differs from `--emit-bats`, which
writes a tight position as authored and warns: a path drawn through unwalkable ground is *worse
than no path*, because it tells a tester to walk somewhere the game will not let them and looks
authoritative while doing it. `TIGHT` is reported and drawn — on navmesh, but with the capsule
possibly clipping a wall.

**Drawing.** A leg is **straight in plan and follows the ground in z** — it is drawn as the
polyline the sampler produced, not as a chord between its waypoints. Straight in 3D is the obvious
thing and is wrong: a chord from the foot of an arched walkway to its crest runs ~50 u *under* the
walkway, and with waypoints only at direction changes there is nothing left to hide that.

Otherwise no new machinery: the segments go through the navmesh wire's own `EmitEdge`, so they
arrive with its whole treatment — a solid depth-tested copy plus an always-visible dashed one.
That is what keeps a route readable where it passes behind a wall or a railing post, and dashes
appearing mid-path are the picture saying *"something is in front of this"*, not a defect. The
arrowheads and the waypoint rings are always-visible flat triangles; a direction you can only see
on open ground is not a direction anyone can follow. Everything is lifted 4 u, above the nav
sheet's own 2 u, so the route never z-fights the surface it is drawn over.

- **One width law.** The line's half-width is `distance × 0.0020`, clamped, which is ~5 px at any
  distance; the arrowhead is sized **from that** (34 half-widths long, 0.20 of that in half-span)
  rather than from a formula of its own. Two independent distance clamps is how the first version
  went wrong: close to the camera the line hit its floor and the head hit a different one, the head
  came out barely wider than the line it rode, and the arrows read as nicks in a fat band. If you
  retune either, retune the law, not one of its two consumers.
- **Length, half-span and spacing are separate knobs on purpose**, because each time one was
  expressed in terms of another it dragged that other one along. Scaling the head 4× in *both*
  dimensions produces heads wider than the walkway they sit on — a different picture, not a bigger
  one — so half-span is compensated whenever the length changes. And the default arrow *spacing* is
  in half-widths rather than head lengths: derived from the head it would halve whenever the head
  halved, pinning the head-to-gap ratio and leaving a shortened arrowhead exactly as cluttered as a
  long one. Spacing knows the head's length in one place only, as a floor that stops heads
  overlapping.
- **The low clamp must stay small.** Set generously it stops being a floor and becomes the width
  of every near segment — a route at the camera's feet then draws as a band wider than the arrows
  on it.
- **Arrowheads are laid out from the END of each segment backwards**, so one always lands pointing
  into the waypoint it leads to. That is the message the picture carries; it must survive whatever
  the spacing arithmetic does to the rest of the run. `--route-arrows end` keeps only that one;
  `--route-arrows N` spaces them every N world units; the default spaces them so the *rhythm* is
  constant on screen. A segment too short to hold a head at its end gets one at its midpoint.
- **Waypoint discs** are a translucent fill at 48 u radius (0.75 of the clearance probes' reach)
  with **no rim**. A picture that already carries a line, its arrows and a navmesh wire does not
  need a fourth hard-edged shape; a translucent fill marks the spot without covering the ground it
  marks. It rides the overlay pass's per-layer alpha and draws *before* the line, so the arrows sit
  on the marker. `--no-route-discs` drops them.

**Colour is reserved, and the reservation reaches the scorer.** The route is cyan by default, and
when a route is drawn the nav wire's `auto` pick both strikes out palette entries too close to it
and scores the rest away from it (it says so in the `nav colour:` line). Skipping that second half
is exactly how the route comes out the same hue as the navmesh over some patch of ground and the
whole overlay stops being readable. `--route-color` takes `cyan|magenta|yellow|green|blue|pink` or
an `R,G,B` triple; red, orange, violet and white are refused — they belong to conflicts, doors,
nav diffs and the cell outline. The caption line is written in the route's own colour, and the
drawn path is pre-divided by the overlay shader's ×1.5 flat lift so the two match exactly.

**The fly-through gets it for free**, because it is overlay geometry — which is what makes this a
live preview of a path rather than a picture of one. `R` hides it (what is *under* the route is
the question a drawn route stops you answering), the HUD carries `route N wpt (label)`, and
`VP_FLY_ROUTE=on|off` is the same switch headlessly.

**No `route.txt` bat.** A batch file that `setpos`-es to waypoint 1 is tempting and deliberately
absent: a follower harness plus this render is enough, and a waypoint-stepping bat should wait
until something actually needs one.

### `--route-solve` — waypoints as endpoints, and the walk found for you

The straight-leg rule above is the right default and is unchanged. What it is not is a good way to
*author* a route: placing waypoints so that every straight segment between them stays on the mesh
means solving the navigation problem by hand, one `--nav-at` probe at a time. `--route-solve` makes
the waypoints ENDPOINTS and finds the path.

```
--route <from X,Y> --route <to X,Y> --route-solve --route-z 1424
```

**Where the edges come from.** The record, not the shapes. Two triangles that share an edge on
paper are neighbours only if the navmesh says so — the game reads `NAVM`'s own link fields and
nothing else, so a graph built from shared vertices would answer a question about geometry while
claiming to answer one about walking. Three sources, all the record's (`src/housecarl-core/NavGraph.cs` — moved from viewprobe 2026-09-02 so the route-cut detector and the renderer share one pathfinder):

| Source | Field | Meaning |
|---|---|---|
| intra-mesh | `NavmeshTriangle.EdgeLink_0_1/_1_2/_2_0` | a triangle index in the SAME mesh, when the matching flag bit (`0x01`/`0x02`/`0x04`) is CLEAR; `-1` = mesh boundary |
| cross-mesh | the same fields with that bit SET | an index into `NAVM.Data.EdgeLinks`, which names another navmesh and a triangle in it |
| doors | `NAVM.Data.DoorTriangles` | the triangle in front of a placed door — how two navmeshes in DIFFERENT CELLS connect |

Doors are **recorded and deliberately not traversed**: the probe loads one cell, so the far side of
a door is not in the graph, and a solver that stepped through one would be inventing ground it has
never seen. They are counted in the refusal instead, so *"no path"* and *"no path without leaving
this cell"* are told apart. (`housecarl-core`'s `NavmeshGeometry`/`NavTri` carry all three now —
the extractor used to drop them, which produced a structure that looked complete and could not be
walked.)

**What it does per leg.** A* over the triangle graph (cost and heuristic are PLAN distance between
centroids, so the heuristic never overestimates), then the Simple Stupid Funnel Algorithm over the
channel's portal edges to pull the path taut, then the same 32 u sampling and the same off-mesh
check a straight leg gets. Without the funnel a solved route is a chain of centroids: it zigzags,
it draws badly, and it OVER-REPORTS the walk — which matters, because the length is half the point.

**The corner inset (6 u).** A taut path hugs the wall — that is what taut means — so its interior
corners land exactly ON the mesh boundary and the segment between two of them runs ALONG it. At
that precision "is this on navmesh" is decided by float rounding: a leg measured here grazed the
boundary by **0.13 u** and the off-mesh check, correctly by its own rule, called the path off the
mesh. A knife-edge that reports a FINDING about the navmesh is worse than no check. So each
interior corner is nudged 6 u off the boundary along the bisector of its two path directions —
**both** directions offered, with the MESH deciding which is free, because a funnel corner is not
always a peg to bend around: consecutive corners often lie along one boundary edge, and there the
bisector points at the wall (measured; reasoning it out got the side wrong twice). The nudge is
kept only if the moved corner is itself on walkable ground at that leg's storey, which is what
stops it pushing a path through a thin wall in a doorway. Endpoints are never moved — those are
the author's waypoints.

**The numbers it prints.** Per leg: triangles crossed, corners after the funnel, **u to walk vs u
straight line and the ratio**, and the leg's z span. The ratio is the point — *"1,800 u apart"* and
*"1,800 u to walk"* are different claims, and a leg that comes out ×1.00 is the solver returning
the straight line because the straight line was already walkable. The z span is there because a
solved leg resolves height by chaining sample to sample, and a chain is exactly the thing that can
drift onto the storey above without any single step looking wrong.

**Refusals, all loud.**

- *No path between a pair* → refused, with the cell's connected-component count and door-triangle
  count. An unreachable pair is a FINDING, not something to paper over with a straight line.
- *The graph came out with zero edges* → refused before any leg is attempted. A graph with no
  edges finds no path between ANY pair, and from the outside that is indistinguishable from a
  building whose rooms are genuinely separate. This is the build's own positive control.
- *A solved leg strays off the mesh anyway* → refused, naming the coordinate to `--nav-at`. The
  funnel is exact for a channel, so this means the record's links and the triangles' shapes
  disagree — worth refusing over.

When the mesh is in more than one piece the run says so and **locates the islands** (`island(s) 1
tri around 1103,-243,-30000`): a component count is a fact nobody can act on, an island's size and
position is a coordinate to probe.

**One branch is not exercised here, and the run says which.** viewprobe loads ONE cell, and in
practice cross-mesh `EdgeLinks` join navmeshes in DIFFERENT cells — three multi-navmesh cells
measured (Solitude Temple 2, Castle Dour 2, FalkreathExterior03 8) all report **0 traversable
cross-mesh links**, with every navmesh its own connected component. So the line that WIRES a
cross-mesh edge has no positive control at this scope; its consumer is the whole-load-order
reachability detector.

What is proven is everything up to that line, and it is reported separately for exactly that
reason: `N cross-mesh link(s) lead OUT of the loaded cell` (38 in FalkreathExterior03) means the
flag-bit switch fired, the `EdgeLinks` table was indexed, and each target navmesh was resolved and
found to be outside the loaded set. Without that counter a run that read the table perfectly and a
run that never read it both print `0 cross-mesh`, and a zero with no control is not evidence.

---

## Measured paths: an actor's own trajectory, drawn as sampled (`routes[]` with `[X, Y, Z]`)

A route is a claim about where someone COULD walk, and the navmesh is the authority on it — hence
the clearance checks and the refusals above. A measured path is the opposite kind of statement:
where an actor DID walk, sampled from the running engine, and for that the navmesh is not the
authority but the thing under test. So a worklist shot's `routes[]` entry whose points carry a z is
drawn through exactly those points — straight in 3D between samples, because the samples are the
measurement's own resolution and a mesh-following leg between them would be the renderer editing the
evidence — and is never refused. The console and the caption say how many samples sit over no
navmesh within 96 u; that number is a finding about the walk, not a defect in the picture.

The standing use is the 2026-09-02 obstacle matrix (Part C of the navmesh-blockage work): one shot
per arm with the obstacle as a `props[]` entry at the engine's own position, yaw and scale, the
walker's trajectory as a measured path coloured by outcome, the straight corridor line the walker was
asked to follow as the shot's planned `route`, and the walker itself as an `npcs[]` entry at its spawn
— so the picture shows the navmesh, the obstacle, the path and the actor's size in one frame. Where
the picture is a top-down, `cullAbove` has to sit above the actor's head (`floorZ` + ~150 for a
scale-1 human) or the console reports the actor `HIDDEN behind scene geometry` and only its puck shows.

## Placed NPC actors (`--npc`)

"NPC X should be HERE" used to be a caption; now it is the character standing there. Each
`--npc FORMKEY[@X,Y,Z[,HEADING]][=label]` puts the NPC's real model in the scene:

- **The FormKey may be the placed ACHR or the NPC_ base** — a console click gives the ref, a
  record query gives the base, and both work. With no `@position` the ref's own WINNING placement
  is used (position, heading, ref scale; when the base is placed more than once the instance in
  the rendered cell is preferred and all are listed). `@X,Y,Z[,HEADING]` is the "should be here"
  case and overrides the ref (heading in degrees, `getangle z` convention; omitted = north). The
  label after `=` names the actor in the caption and the console (`=` because a `:` would clash
  with the FormKey's own).
- **The character pipeline is `CharacterViewer.Rendering`'s `VM_CharacterViewer`, hosted
  headless** exactly the way `GameWindowOffscreenRenderer` hosts it for NPC2's mugshots (inline
  render-thread marshaller, `IsOffscreenRenderInstance`, `LoadAsync` +
  `ProcessPendingSceneToCompletion`, `ApplyMeshOverrides`) — FaceGen head with tint, CPU skinning
  to close the neck seam, weight morphs, wig/tail armatures, slot hiding, the works. The record
  side (`NpcResolve.cs`) is adapted from NPC2's `NpcMeshResolver`: Traits template terminus,
  WornArmor/Race.Skin ARMA walk race-filtered through `ArmorRace`, sex-appropriate world models,
  TXST and AlternateTextures, RACE skeleton, QNAM/HCLR/weight, and world scale =
  race height (sex) × NPC height × ref scale. A headless host must initialize ReactiveUI once
  (`RxAppBuilder…WithWpf().BuildApp()` + an immediate main-thread scheduler) or the VM's ctor
  throws — `NpcActorBuilder.CreateHost` owns that.
- **The outfit is the RUNTIME's answer, not just the record field** (`NpcRuntimeOutfits.cs`,
  adapted from NPC2's OutfitDistribution simulators, which were verified against the actual
  SkyPatcher and SPID sources): the record winner's `DefaultOutfit` — routed through the
  Inventory-template chain when that flag makes the record's own field dead — then SkyPatcher
  `outfitDefault=` lines (`SKSE\Plugins\SkyPatcher\npc\**`, BFS file order, LAST matching line
  wins, `hasPlugins`/`hasPluginsOr` line gates honoured), then SPID `Outfit`/`FinalOutfit` (and
  outfit-shaped generic `Form =`) entries from `*_DISTR.ini` (first matching entry wins,
  suspended when SkyPatcher moved the outfit away from the record's initial value — SPID's own
  `IsSuspendedReplacement` rule). Config discovery walks the RECONSTRUCTED MO2 VFS (data dir +
  enabled mod folders + overwrite, loose files only — both tools read the real filesystem, so
  BSA-packed INIs are invisible to them and to this). The winning source is printed and lands in
  the caption (`SkyPatcher: sub\file.ini (line N)` / `SPID: file.ini (line N)`); sub-100%-chance
  SPID entries that would otherwise match are reported as "possible at runtime" and never
  applied; record-level approximations (skill filters, teammate trait, …) are named.
- **Placement is BAKED into the vertices** (`NpcActor.cs`): the VM's installed meshes are
  rewritten in place (positions + normals/tangents/bitangents through the same negated-Euler
  composition every static uses), because `GlRenderer` has one global model matrix. The builder's
  Y-up character faces `-Z`, which un-swaps to Skyrim +Y = north — the ref convention — so no
  facing fudge exists. The meshes are BORROWED into each panel's renderer and removed again
  before it disposes (its `ClearMeshes` destroys what it holds); the owning VMs outlive every
  panel and are disposed last.
- **The puck** is two honest halves: a translucent fill disc on the decal channel (depth-tested —
  covered by what covers it) and a thin always-visible rim in the overlay pass — an actor behind
  a wall or under an upper floor still announces where it stands. Radius scales with the actor.
  On a steep top-down, where a standing figure is a few dozen pixels, the puck is what makes the
  placement legible; at eye level it stays out of the way (`auto`).
- **Each actor wears an IDENTITY colour** — fill, rim, and its caption line all share it, so a
  multi-actor render answers "who is who" from the caption alone. Assigned in `--npc` order from
  a 5-colour palette (yellow, pink, green, blue, white — bright for the dark caption bar, clear
  of the reserved conflict-RED / door-ORANGE / diff-VIOLET; cycles past five), and named on the
  console per actor. The rim pre-divides by the overlay shader's ×1.5 lift so all three faces of
  the marker land on the same colour.
- **An EMPTY ring means the actor is occluded, and the console says so.** The actor and the fill
  are depth-tested (honest), so an interior top-down shot from above the building shows only the
  rim — the figure is under the roof and upper floors. Every render with actors checks each one
  against the final frame's depth at its head (the same compare, same slack, as the navmesh dash
  classifier, plus the actor's own headroom so its hood never reads as an occluder) and prints
  `HIDDEN behind scene geometry … slice the view with --cull-above Z (e.g. N) or move the camera
  below the occluder`, with the suggested cut computed from that actor's height. `--cull-above`
  is the fix that keeps every actor in one frame when no interior camera can: slice the shell
  off and the top-down becomes a floor plan with the figures standing in it.
- **What it is not:** the figure stands in the NIF bind pose (A-pose) — there is no animation
  system, so it reads as a mannequin at the spot, not someone sitting or leaning. SleepOutfit is
  not simulated (the render depicts the awake actor), and a sub-100% SPID chance is reported, not
  rolled.

---

## Effect geometry: two filters, because it comes in two shapes

"Is this an effect?" cannot be answered from the model path — that describes where a file lives,
not what is in it. Effect geometry is filtered in two places, and `--fx` switches both off:

- **whole file, at the ref** (`Program.cs`) — the model path contains `effects\`. Beams, mists,
  glows: files that are nothing but effect. Dropped before the scene is assembled.
- **per shape, at the mesh** (`TexturedScene.cs`) — reported per render as
  `unshadeable shapes: N skipped on R ref(s) of M mesh(es)`, which `--list-meshes` expands to name
  the meshes.

The per-shape filter is not a refinement of the path rule, it is the case the path rule **cannot
express**: `Clutter\WoodFires\FireplaceWood01Burning.nif` is ONE file holding 5 solid shapes (logs,
coals, ash) and 12 effect shapes (flames, smoke, heat warp). No path rule keeps the logs and drops
the flames.

Two independent tests, both framed as **"this renderer provably cannot shade it"** rather than
"this looks like an effect":

1. **`BSEffectShaderProperty`** — `NifMeshBuilder` reads material values off
   `BSLightingShaderProperty` ONLY, so an effect-shader shape arrives with every shader field at
   its default: opaque, unflagged, fully lit. The material was never read, so there is nothing to
   compensate for downstream. Surfaced as `BuiltMesh.HasEffectShader` (CharacterViewer.Rendering
   ≥ 2.10.0).
2. **A normal map in the diffuse slot** — slot 0 is sampled as diffuse, so a shape whose slot 0 is
   a `_n.dds` paints the lilac/cyan/green of a tangent-space normal texture under any lighting.
   Test 1 does not catch these: the case that motivated the rule is a heat-shimmer plane authored
   on a *lighting* shader with a `...Normal_n.dds` in slots 0 and 1. Bethesda's `_n` suffix is a
   hard convention — no legitimate diffuse carries it — and the count is reported separately so the
   rule's reach stays visible rather than assumed.

### `--fx-static` — a frozen fire

The middle setting between dropping the fire and drawing it wrong: per-shape effect geometry is
DRAWN, frozen at the frame the NIF stores. **Off by default** — an additive flame still paints over
the navmesh behind it, and the navmesh is what this tool is for. Kind 2 stays skipped under it: a
normal map in the diffuse slot is unshadeable however it is blended.

"First frame" costs nothing to obtain: the block's stored values ARE the base state, and the
`BSEffectShaderPropertyFloatController`s that would animate UV/alpha are simply never run.

Three of the four ingredients were already present, which is why this was cheap:

- **Geometry** — flame quads are real `BSTriShape`/`BSLODTriShape`.
- **Texture** — already resolving: nifly's `GetTexturePathByIndex` maps slot 0 to
  `BSEffectShaderProperty.sourceTexture`.
- **Blend state** — `NiAlphaProperty` is a separate block, so the builder reads it fine: `6->0` =
  SRC_ALPHA → ONE, additive, depth-write off. Additive is order-independent, so no sorting work.
- **Missing:** the material, plus three smaller things:
  1. `DstBlendIndex` was never copied to `GlMesh` (only `Src`), so it kept its default
     INV_SRC_ALPHA and every additive shape in the game was composited as ordinary alpha blending.
     Invisible on solid geometry, fatal for a flame.
  2. `BSEffectShaderProperty` was never read — now `EffectBaseColor` / `EffectBaseColorScale` /
     `EffectBaseColorAlpha` on `BuiltMesh`.
  3. **Effect atlases are commonly greyscale.** A fire atlas is white flame shapes and the orange
     lives entirely in `baseColor`, so drawn with a white emissive the flames come out as white
     ghosts however bright you make them.
  4. `SLSF2_Double_Sided` lives on the shader block the builder never reads, so effect quads arrive
     single-sided and half a flame rosette is back-face culled. `--fx-static` forces double-sided.

**Intensity.** `baseColorScale` is an HDR number the engine tonemaps and viewprobe cannot, so it
clips — but it clips *along the baseColor hue*, which is what a fire core looks like anyway. A
hand-swept ladder (×1 invisible, ×2 faint, ×4 legible, ×8 right) landed on the authored value,
which is the reason to use the author's number rather than a chosen constant.

**Limits.** It is a stand-in, not the effect pipeline: the shader computes
`albedo * (diffuse + emissive)`, not `source * baseColor * baseColorScale`, so the result is paler
and less saturated than the game's. There is no billboarding — `NiBillboardNode` quads draw at their
authored angles rather than facing the camera (viewprobe *could* compute this; it knows the camera
before it builds the scene). And **particle systems can never be drawn**: a `NiParticleSystem` at
rest has zero live particles, so there is no first frame — no sparks, no smoke plume, no glow point.

---

## Occlusion planes (`--planes`)

Added 2026-09-04 for the occlusion-plane audit (roadmap 4.4): a location mod that rearranges
architecture invalidates the occlusion planes cut for the old arrangement, and nothing in the
suite looked at planes at all. This is the instrument — it draws them, says who put each one
where, and (below) previews what they cull. It reports; it never judges.

**What a plane is, in the records.** A placed object (REFR) whose base is `PlaneMarker`
(`000017:Skyrim.esm`, a STAT carrying the Is-Marker flag — the engine never draws it, and the
marker filter drops it, so planes are invisible in viewprobe and in game alike). The geometry is
the ref's `Primitive` (XPRM): `Type` **PortalBox** with `Bounds` (width, 2, height) — a two-unit
slab standing in the ref's local X–Z plane, normal along local Y — or **Box** with a real Y
extent, which the game culls with too (measured below) and which is counted apart. `Bounds` are
FULL extents about the ref's position: every unaltered vanilla plane that also carries the CK-era
`OcclusionPlane` subrecord stores exactly `Bounds.X/2` and `Bounds.Z/2` there as Width and Height
(measured over the rows that carry both; a few Dragonborn and Dawnguard rows disagree because
that subrecord went stale when the plane was resized — which is why it is never read here).
Placement is the REFR convention, `SpatialOverlap.RefrRotation`. A plane whose enable parent
resolves disabled at runtime is dropped and counted, like any ref.

**Verified by hand, the Euler way** (2026-09-04): a camera stood 1,200 u in front of a mod's
693×216 u plane, yaw 254°; `--pick` at the frame centre crossed that plane at 1,201 u, and eight
picks just inside and just outside its predicted left, right, top and bottom edges answered
inside/outside exactly as the record's extent predicts — position, rotation and the full-extent
reading of `Bounds` all hold.

**Provenance is the colour.** Every plugin's version of every PlaneMarker ref is recorded in load
order during the walk and compared with the version before it (position, rotation, bounds, kind,
scale, the disabled flag — the changed-only rule `PlacementHistory` uses):

| class | colour | meaning |
|---|---|---|
| vanilla | grey | defined by a base master (`Skyrim`/`Update`/`Dawnguard`/`HearthFires`/`Dragonborn.esm`), never changed, in a cell no other plugin places refs in |
| inherited | gold | the same, unchanged, in a cell some other plugin contests — the "left standing for architecture that is no longer there" candidate |
| added | magenta | defined by a plugin that is not a base master (Creation Club and the unofficial patch count as mods here — the audit asks what a MOD did) |
| moved | teal | an override changed it; the console and the hover say who |

The console prints the counts by kind and by class, who added and who moved, which plugins
contest the cell, and what was dropped and why — every run, drawn or not, because a plane the
scene holds and says nothing about is exactly the silent category this renderer refuses to have.
The caption carries the legend when the layer is drawn.

**How it is drawn.** A PortalBox is its mid-quad (drawing the slab would draw a line); a Box is its
six faces, fainter. The fill is translucent and depth-tested, so a plane inside a wall shows only
where the wall does not cover it; the outline gets the nav wire's treatment — a solid depth-tested
copy and an always-visible dashed one — so a plane behind a wall is drawn dashed where hidden and
never lost. Most planes ARE inside walls, so most of the layer reads dashed; that is the picture
being honest. Each drawn colour is reserved from the nav wire's auto-pick on the route's terms.
In the window (`L`, and the bar's toggle), hovering a plane names it — FormKey, runtime FormID,
kind, extent, provenance and the override chain — and the surface behind it; hovering a wall
names the planes the ray goes on to cross. The layer is resident in every fly-through, shown or
not, so `L` is a visibility flip like `N`.

**Not modelled, said plainly:** nothing here is a verdict. Whether a plane is well placed is the
audit's question, and the audit needs the game's own cull rule (measured below) plus a floor and a
control this instrument does not have.

### Research preview and report

The preview is a **research implementation, pending acceptance**. `--occlusion-rule FILE` supplies an
explicit JSON rule with `identity`, `minimumEyeDistance`, `maximumAbsNormalZ`, `frozen` and
nonempty `evidence` references, and an optional `boxSilhouette` (default **false**). The numeric
limits must be finite and in range. They describe the supplied evidence scope; the file's `frozen`
label is not independent validation. With no rule, engine culling is unassessed. A rule that omits
`boxSilhouette` leaves Box face behaviour unassessed, with the same refusal as before. The plane
layer itself remains available without a rule.

**Box primitives (`boxSilhouette: true`).** A Box is then predicted by its **view shadow**: the
region behind the box's silhouette as seen from the eye, together with the box's own volume —
exactly the points whose segment from the eye meets the box. `ConvexShadow.FromBox` builds it from
one plane through the eye and each silhouette edge (an edge whose two adjacent faces disagree about
facing the eye, so an oblique eye, where the silhouette is not the near face's outline, is handled
by construction) plus one half-space per front-facing face — the near cap that admits the volume.
A sphere bound is classified by the same `Classify` a rectangle uses, so exact contact is
`Crossing` and is never promoted to a cull. **A union of per-face shadows is NOT separated from the
silhouette by the supporting evidence and remains untested**; the reported `scope` says so whenever
a rule opts in. Outside the opted-in scope the Box is refused **by name**, never silently: a box
that is not upright within the rule's upright tolerance (below), an eye inside or on the box, a
degenerate box, non-positive bounds or scale, and an eye nearer than `minimumEyeDistance` each
give their own reason. The near-eye distance for a box is measured to its nearest surface point.

**Horizontal PortalBoxes, one class (`horizontalPortalBoxNormalDownEyeAbove: true`).** By default a
PortalBox is predicted only when it is upright: the rectangle's normal may leave level by at most
`maximumAbsNormalZ`. This opt-in admits exactly one further class: a **horizontal** rectangle, with
its authored normal (the reference's local +Y) within that same tolerance of straight **down** (−Z),
seen from **above** its plane. That is the in-situ form of panels laid just under walkable floors
(X about 90° with a Y yaw). It is predicted by the same rectangle shadow. **Everything else stays
refused, by name:**
- a horizontal rectangle with its normal **up**: "horizontal PortalBox with its normal up…";
- the normal-down rectangle seen from **below** or on its plane: "horizontal PortalBox seen from
  below…";
- every other tilt, including one just outside the tolerance: the sealed "plane orientation outside
  the supplied rule's evidence scope".

A rule that omits the field predicts exactly what it always did. The field is not written into a
report's `ruleConfiguration` unless it is on, so such a report is byte-identical to one written
before the field existed. The decision is `HarnessShared.PortalBoxOrientation`, and its cases are
`stationrule: H0…H9` in `proofprobe`. **Opting in is a claim about engine behaviour.** A rule that
sets it must cite in-game evidence for the class. The reported `scope` names the class whenever a
rule opts in.

**The upright tolerance (`uprightMaximumAbsNormalZ`, optional).** Both upright tests — a
PortalBox's normal and a Box's two horizontal axes — use this field when a rule states it.
`maximumAbsNormalZ` then governs only the horizontal class above. A rule that omits it uses
`maximumAbsNormalZ` for both, exactly as before, and the field is not written into
`ruleConfiguration`, so such a report is byte-identical. It must be finite and in [0, 1]. Both
upright decisions live in `HarnessShared.PortalBoxOrientation` (`Check`, `BoxUpright`). Their cases
are `stationrule: 6016…6020` in `proofprobe` and `--occlusion-rule-selftest DIR` (cases 6000–6014,
run by `tools/test_occlusion_rule.py`). The reported `scope` names the tolerance when a rule states
it. Widening it is a claim about engine behaviour: one lab test (2026-10-15) found PortalBoxes
tilted |n.z| 0.0065 and 0.021, in both lean senses, and a Box tilted 0.021 culling as their true
shape predicts. That test did not separate the true shape from an upright-snapped one.

The geometry primitive tests each source NIF shape's stored sphere against the finite
PortalBox shadow's surface and four side half-spaces — or, where a rule opts in, against that Box
view shadow. It applies below-root NIF node transforms
and the reference placement, retaining the original source block, raw bound, applied chain,
asset hash and resulting world sphere. It does not replace the sphere with an uploaded-vertex
box. Invalid bounds, ambiguous shape identities, unsupported poses and removed geometry retain
explicit reasons. Root-transform handling follows the existing placed-geometry convention.

`--occlusion on` removes the meshes belonging to predicted-culled shapes; `diff` tints those
meshes. Other shapes on that reference remain drawn. Interactive `O` reevaluates the prediction
at the current eye, but does not supply a captured visibility measurement. The preview-only
`VP_OCCL_TINT_ALL` canary never participates in report judgments.

`--planes-report` writes one `<render-stem>-planes.json` per rendered view and refuses to replace
existing reports or their evidence directory. Every candidate shape is retained in JSON; console
shortlists are not the candidate population.

**Schema `occlusion-view-research-v2`: each candidate written once.** A shape's description does not
depend on the plane, so v2 writes it once, in the top-level `candidateBodies` array, each with a
zero-based `id` equal to its index, in the order `v1` listed its rows. Every plane's `candidates` array
keeps that order, and entry N is an overlay for body N: `{ "candidate": N }` plus the only five values
that can differ by plane — `relation`, `predictedCulled`, `visibility`, `visibilityMask` and
`isolatedDepthSha256`. A reference with no source shapes has a sparse body (`subject`, `mesh`,
`assetSha256`, `status: "unassessed"`, `reason`) and an overlay that is only its `candidate`. A view with
no planes writes an empty `candidateBodies`, as `v1` wrote no rows for it. To get a
`v1` row back, take the body without `id` and add the overlay without `candidate`; the result equals
the `v1` row key for key, null for null. A reader dispatches on `schema`: it accepts exactly
`occlusion-view-research-v1` and `-v2`, refuses anything else, and refuses a non-contiguous or
duplicate `id`, a reference to a missing body, and a key present in both a body and its overlay —
never guessing the shape from which keys happen to be present. `--planes-report-selftest` holds the
factoring to the `v1` rows on a fabricated two-plane scene, including the sparse no-source row and a
real `unassessed: ` relation, and checks each of those refusals. `v1` reports stay readable; new
reports are `v2` only.

**Python readers:** `tools/planes_report_rows.py` materialises complete candidate rows from both
schemas and refuses unknown schemas, invalid body ids, missing references and body/overlay key
collisions. Its text scan reads the root schema structurally and, for v2, validates every overlay
while decoding complete bodies only for rows with visibility masks.

**The report is serialised straight to the file.** A busy exterior view's report runs to hundreds of
megabytes — how many depends entirely on the load order and the view, since the report lists every
source shape of every scene reference, and under `v1` repeated it under every plane — and it used to be built as one string and
handed to `File.WriteAllText`. A .NET string is UTF-16, so that copy was twice the file, and
encoding it back to UTF-8 to write it was the file's size again: three times the report, live at
once, at the end of the heaviest step a render has. It now goes through the file stream. **The bytes
are the same bytes**: the serialiser writes through the same writer either way and its indented
output does not depend on the destination, which matters because a reader of a city report scans
its text rather than decoding it. This removes the copies, not the document: the per-plane rows are
still built before the write, and they are most of what the process holds. Because a streamed file
exists on disk while it is still being written, it is written as `<render-stem>-planes.json.writing`
and renamed only once it is complete and flushed — **a run that fails or is killed mid-report leaves
nothing that globbing for `*-planes.json` can find**, and a leftover `.writing` file says what it
is. `--report-sink-selftest <dir>` checks the byte-identity, the refusal and the failure path. Each shape keeps every predicting plane, while
affected references are counted uniquely. The report is still research, not a completed defect
classifier or a compatibility attribution.

Visibility uses that **same shape's** isolated rasterized depth against the unculled scene from
the same camera and state. Both passes use the same single-sample layout and exact depth
comparison. Green mask samples are visible and red samples hidden; counts describe that finite
raster only. A culled rear shape entirely hidden by a drawn front shape is healthy. A hidden
centre cannot suppress visible edge samples. The scene depth and per-shape masks are saved.

**One encode per distinct mask.** A mask is a picture of one comparison, so it carries two bits per
pixel and most of a view's shapes produce a picture the view has already produced — an off-screen
or fully hidden shape gives the same mask as every other. The writer keeps a per-view cache and
writes a repeat from bytes it has already encoded. **Nothing about the output changes**: the same
file names, the same bytes, the same digests, so anything reading the masks or digesting them sees
exactly what it saw before. What changes is that a view stops encoding the same picture over and
over, and encoding — not creating the files — is nearly all of what that step costs.

The cache is keyed on the isolated depth buffer's SHA-256, which the report already carries as
`isolatedDepthSha256`, so the key is free. Within a view the scene depth is fixed, and a mask is
computed from that one buffer and nothing else (`MaskEvidence.BuildMask`), so a key that matches
means a mask that matches. The implication runs one way: two different depth buffers can produce
the same mask, and then the cache simply misses the repeat and encodes it, which costs what today
costs. The cache is created beside the depth buffers it is keyed on and dies with them, so a
process rendering several views cannot carry one view's pictures into another.
`HOUSECARL_VERIFY_MASK_CACHE=1` re-encodes every cache hit and compares it, prints the counts, and
fails the run rather than writing a mask it cannot confirm; `--mask-evidence-selftest <dir>` runs
the mechanism over built buffers and prints what it measured.

The per-view draw budget defaults to 256. For a bounded evaluation, pass
`--visibility-budget N` (integer 1..16384, with `--planes-report`, non-interactive only).
The budget resets for each view and is recorded in the report and its input identity.
It limits isolated shape draws, preserving all surrounding geometry in the scene depth pass.
Exhaustion remains explicitly unassessed; a larger budget does not make unsupported shapes
assessable or clear unvisited views. Freeze the intended subjects and camera/state before
evaluation, then check each subject's assessment or concrete refusal. Off-mode colour uses
its original rendering path after mesh visibility is restored.

Coverage names missing/unsupported geometry, alpha/effect materials, clipping, unsupported
poses, unpinned random state, interactive views and camera/rule limits. Missing texture fallbacks
are currently printed by the renderer; texture/material asset bytes are not yet fingerprinted
in report input identity. These limitations preclude a claim of complete game appearance.
The structured `appearance` status is `unassessed` for every report. Source-mesh identity
includes unreadable bytes and explicit missing-geometry entries; a detected mismatch between
the bounds input and the file checked after the renderer read refuses that reference's prediction.
These checks do not create an atomic snapshot of mutable assets.
Source shapes remain listed when the renderer produces no geometry; `sourceIssue` keeps
their bounds or pose refusal alongside the reference's rendering failure.
Plane results use scene-object identity, so lazy geometry caches cannot invalidate
the lookup of an unconfigured-rule or unsupported-Box refusal.
The report records camera, state, source bounds, record/mesh/assembly hashes and evidence paths.
Worklist `state` overrides reset for each shot. `control`, `viewpointKind` and `viewpointEvidence`
label constructed controls and the declared viewpoint evidence; a declaration does not certify
player reachability. A sampled view cannot certify a whole cell or mod clear.

The nearby-collision listing remains supporting evidence: a 5x5 ray sample with the existing
record-OBND broad phase and `--planes-reach` distance. No hit establishes neither absence of
architecture nor a visible defect. Provenance identifies record authorship, never blame or
repair credit. No automatic repair is authored.

The discarded whole-reference box predicate, sharp near-eye threshold and centre-pixel
visibility decision are not operational guidance. Historical investigation belongs in the
project's research corpus. The current bounded engine model, calibration and release acceptance
remain pending; passing geometry or renderer fixtures alone does not establish them.

#### `--depth-measurement`: the same measurement without the appearance work

`--depth-measurement` (with `--planes-report`, non-interactive) keeps the measurement and drops the work that only
serves the picture. Both depth buffers the report is read from (the whole scene, and each assessed shape drawn alone)
depend on exactly one texture input: the diffuse alpha of a shape drawn with the alpha test, which `basic.frag`
discards below its threshold. So under the flag a static shape's diffuse is loaded only when the shape is alpha-tested
(`DepthMeasurement.KeepsDiffuse`, keyed on the same NIF flag that sets `GlMesh.UseAlphaTest`). Normal maps, the terrain
base textures, the terrain paint layers (roads; they are blended decals and write no depth), the water's noise map
and the snow coverings' textures are not loaded or built. Placed NPCs (`--npc`) keep their own textures; the flag does
not touch their loading. An evidence mask is written only for a shape with `VisiblePixels > 0`, which are the only
masks the scorer opens; the other shapes' `visibilityMask` is null. `--keep-all-masks` writes every mask as before.
`scene-depth-f32.bin`, every `isolatedDepthSha256` and every count are unchanged. The report's `appearance` block
says `mode: "depth-measurement"` and carries `measurementMode` (mask rule, and the number and file bytes of the
alpha-tested diffuses kept); `replayArgs` carries the flag.

Refused with one sentence each: without `--planes-report`, in the interactive window (including a launch from
Explorer), with `--stations`, and `--keep-all-masks` without the flag. `--depth-measurement-selftest` gives the
known answers for the keep rule and the mask rule.

What a reader of the output sees differently, and must allow for:
- **The frame** (`image`, `-fp.png`) is lit without textures except the alpha-tested diffuses, with no roads. It is a
  picture of the geometry, not of the game's appearance: never score it or compare its pixels with a default-path frame.
- **The masks.** A tool that counts the masks in an evidence folder, lists the folder, compares mask sets between two
  runs, or opens a mask by name for a shape that may have no visible pixel needs `--keep-all-masks` output (or a
  default run). Readers that open masks only for visible shapes, or OR their green channel, are unaffected.
- `tools/planes_report_rows.py`'s `masked_rows_text` skips rows whose mask is null, so it skips invisible shapes in
  this mode; `rows_by_plane` does not.

#### `--occlusion-query`: count the isolated draws on the GPU

`--occlusion-query` (only with `--depth-measurement`) keeps every isolated draw as it is and replaces the read-back,
hash and CPU compare of shapes that turn out to have no visible pixel. After each isolated draw the shape's depth is
copied into a texture (the scene depth is copied once per view), and a full-screen pass evaluates
`ShapeVisibility.Compare`'s per-pixel rule on the GPU (`mine == 1` is skipped; covered pixels, and visible pixels where
`mine <= scene`), with one `SamplesPassed` occlusion query per count (`OcclusionQuery.cs`). One triangle covers each
pixel once and the pass writes nothing, so a passed sample is a pixel. A shape with visible pixels (or every shape
under `--keep-all-masks`) is still read back, hashed, compared on the CPU and masked as before, and the view is refused
if the CPU and the query disagree on its counts. The draw budget, the draw indices and the mask names do not change.

What changes in the report: for a shape the query finds invisible, `RasterPixels`, `VisiblePixels` (0) and
`HiddenPixels` come from the query and `isolatedDepthSha256` is **null** (not computed). `appearance.measurementMode`
gains an `occlusionQuery` block saying so, with the per-view conversion check (`pixels`, `mismatches`) and how many
shapes were queried and checked on the CPU. Readers of `isolatedDepthSha256`: `tools/renderequiv.py` digests it, so
it differs between a run with the flag and one without by design (compare such runs with a comparator that knows the
rule); `tools/test_planes_report_rows.py` lists the key; the mask cache keys on it, but only for shapes it writes a mask
for, which are all hashed. `scripts/planeassess` does not read it.

GL does not state that sampling a depth texture gives the same float as `ReadPixels`, so it is measured: every view
compares the sampled scene depth with the read-back scene depth bit for bit and refuses on any difference, and
`--occlusion-query-selftest` (needs a GL context, no instance) checks the conversion over a full-range depth fill
(printing how many of the 2^24 values it reached), the query's counts against the CPU compare on synthetic depth with
ties, uncovered, hidden and visible pixels, and that the pass puts back every binding it changes. Refused without
`--depth-measurement`.

---

## Camera stations for occlusion primitives (`--stations`)

A mode that **renders nothing**. It composes the camera stations a player could stand at to look
at each occlusion primitive in a cell, writes a pose file with its yield accounting, and exits. It
rides the load-order walk the renderer already does rather than starting a second one, which is
why it takes the same worklist, profile and `--order` arguments as a render.

```
viewprobe <worklist.json> --stations <spec.json> --stations-out <poses.json>
          --instance <MO2 instance> --profile <profile> --game <game dir>
          [--order <candidate order file>] [--occlusion-rule <rule.json>]
```

The worklist names the target cell, because that is what drives the load-order walk. Nothing is
drawn and no image is written; `--stations-out` is required, and its directory is created.

The rule itself is not in this file: composition, the closed refusal set and the precedence are
`_shared/StationRule.cs`, the cell-and-water lookup is `_shared/StationLookup.cs`, the pose file
and its closure assertion are `_shared/StationPoseFile.cs`, and all three have known-answer cases
in `proofprobe` (`stationrule: ...`). This mode is the plumbing between them and the load order.

### The spec

```jsonc
{
  "setup": "A3",                       // required: the arm this pose set belongs to. The yield
                                       //   is kept per setup, and a pose carries it.
  "label": "…",                        // optional, defaults to the setup
  "primitives": ["000ABC:Some.esp"],   // optional: FormKeys to compose for. Omitted = every
                                       //   occlusion primitive the winning state places in the
                                       //   cell and its ring. A named primitive the arm does
                                       //   not place REFUSES the run.
  "k":        [0.25, 0.5, 1.0],        // optional; the sealed ladder is the default
  "laterals": [-0.35, 0.0, 0.35],      // optional; likewise
  "fovDeg": 80.0,                      // optional; the field of view carried on every pose
  "mode": "observing",                 // optional; ABSENT (or "face-on") is the face-on rule and
                                       //   its pose file is unchanged to the byte. "observing"
                                       //   composes the ring below; "route" is the route mode,
                                       //   with its own spec (below). Anything else is refused.
  "eyeHeight": 120.5                   // optional, and CHECKED rather than applied: the eye
                                       //   height is part of the rule. A spec that asks for a
                                       //   different one is refused, not obeyed.
}
```

A ladder that is not the sealed one is composed, but the run prints a warning and the pose file
records `ladder.isTheSealedLadder: false` — a yield composed on a changed ladder is not comparable
with one composed on the sealed ladder, and that has to be visible in the file rather than
remembered.

### The ladder

Per primitive face: `k` × `side` (+1, −1) × `lateral`, so eighteen stations for a `PortalBox`
and thirty-six for a `Box`. The standoff is `clamp(k × min(halfWidth, halfHeight), 250, 3000)`
measured from the face along its outward normal; the lateral offset is `lateral × halfWidth` along
the face. A `PortalBox` is a rectangle with one authored normal and stands off its mid-plane; a
`Box` is a volume with four upright faces, so it gets a ladder per face and a pose names the face
it stands off (`faceAxis` X or Y).

### The pose file

```jsonc
{
  "schema": "occl-transfer/station-poses/1",
  "label": …, "setup": …, "worldspace": …, "worldspaceEditorId": …, "targetCell": …,
  "composedAtUtc": …,
  "ladder":    { "k": […], "laterals": […], "side": [1,-1], "standoffFloor": …,
                 "standoffCap": …, "eyeHeight": …, "fovDeg": …, "isTheSealedLadder": true },
  "floorRule": { "maxDepthBelowNavmesh": …, "upReachAboveNavmesh": …, "neighbourhood": …,
                 "anchor": "max(navmesh, terrain) at the station's own XY" },
  "primitives": 1, "primitivesTiltedOffZ": 0, "groundReadsWhereTerrainIsAnotherStorey": 0,
  "stationsWithMoreThanOneCoveringTriangle": 0,
  "spec":       { "path": …, "sha256": … },
  "instrument": { "assembly": …, "sha256": …, "builtUtc": … },
  "commandLine": …,
  "yield": {
    "bySetup":     { "<setup>": { "composed": n, "accepted": n,
                                  "rejected": { "<reason>": n }, "stationsAnchoredOnTerrain": n,
                                  "stationsWhereTerrainIsAnotherStorey": n,
                                  "primitivesWithAStation": n } },
    "byPrimitive": { "<formKey>": { "<setup>": { "composed": n, "accepted": n,
                                                 "rejected": { "<reason>": n },
                                                 "stationsAnchoredOnTerrain": n,
                                                 "stationsWhereTerrainIsAnotherStorey": n } } }
  },
  "poses": [ {
    "primitive": …, "kind": "PortalBox|Box", "setup": …,
    "cell": …, "worldspace": …,               // the cell the STATION stands in, not the primitive's
    "stationIndex": n, "faceAxis": "Y|X", "k": …, "side": …, "lateral": …, "standoff": …,
    "eye": [x,y,z], "lookAt": [x,y,z],        // lookAt.z == eye.z by construction
    "pitchDeg": …, "bearingDeg": …,           // bearing: 0 = +Y, clockwise
    "angleToPlaneNormalDeg": …, "fovDeg": …,
    "navZ": …, "floorZ": …, "floorSource": "Mesh|Land|None", "floorOffset": …,
    "floorAnchor": "Navmesh|Terrain", "navmeshBurial": …, "terrainIsAnotherStorey": bool,
    "eyeHeight": …, "feetAboveSurface": …, "planeZLow": …, "planeZHigh": …,
    "water": { "verdict": "Real|WorldspaceDefault|KnownDryNoSurface|Unresolved",
               "surfaceZ": …, "cellHeight": …, "kind": …, "worldDefault": …,
               "fromWorldspace": …, "how": "…" },
    "accepted": bool, "rejectionReason": "None|…", "rejectionDetail": "…"
  } ]
}
```

**Every composed station is published, accepted or refused**, so a pose file can be judged before
anything is rendered. **A number the rule never measured is `null`, never a sentinel**: a refused
station has no eye (there was no floor to stand it on) and therefore no pitch or offsets either,
and `null` says so where a number would be read as a height. `cellHeight` is the exception that
proves it — the engine's no-value markers are finite floats and stay numbers, because the raw
value is what a reader checks the verdict against.

**The yield closes or the file is not written.** For every bucket, per primitive and per setup and
in the aggregate, `accepted + Σ rejected == composed`; a bucket that does not close refuses the
write with the bucket named. The table is generated from one keyed structure and checked, never
typed.

### What a station has to satisfy

| # | condition | number |
|---|---|---|
| C1 | **Level gaze.** `lookAt.z == eye.z`, and the published `pitchDeg` is measured from the eye and the aim point rather than asserted. | \|pitch\| ≤ 1e-4° |
| C2 | **The eye stands on the physical floor**, not on the navmesh triangle: `eye.z = floorZ + eyeHeight`. A missing floor refuses; there is no fallback to the navmesh height. | 120.5 u |
| C3 | **The feet are above the applicable water surface**: `floorZ ≥ waterSurfaceZ`. Every pose publishes `feetAboveSurface`. | 0 u |
| C4 | **The surface is known.** Unresolvable refuses. | — |
| C5 | **The station is standable**: a winning, drawn navmesh triangle covers the XY. | — |
| C6 | **The eye lies within the primitive's vertical extent**, so the primitive's silhouette bounds the view. | — |

The floor is the highest up-facing actor-solid surface (or the terrain) within `maxDepth` below and
`upReach` above **the anchor**, read over the 3×3 grid-square neighbourhood of the station's own
square — a floor mesh placed in the next cell reaches under this one. Both numbers are published in
the pose file's `floorRule`. **C3 tests the FEET, not the eye**: a rule that tests the eye passes a
station standing in water up to its neck.

**The anchor is the terrain where the navmesh is BURIED under it, and the navmesh everywhere
else**, and every pose says which of the two it used (`floorAnchor`), how far the landscape stands
over the navmesh there (`navmeshBurial`) and whether that gap is a whole storey
(`terrainIsAnotherStorey`).

The reason is physical, not a fitted number. In places the navmesh is authored **under the
landscape** by more than a body can step, and a band opened around a buried navmesh opens
underground — the ground the body actually stands on falls outside it and the rule answers with the
highest thing it can see, which can be a placed mesh far below the terrain. So where a body
standing at the navmesh height would **intersect** the landscape — the terrain above the navmesh by
less than the actor's standing height, which is this tree's own engine-measured constant
(`ActorBand`, the controller's top above the navmesh) — the read is anchored at the terrain
instead. Inside that case **a floor below the terrain is never chosen**, and that follows from the
anchor rather than being bolted on: a surface under the terrain can only lie inside the band when
the terrain lies inside it too, and the read takes the highest, so the terrain wins. A surface
within the step reach **above** the terrain — a street mesh, a rock standing on it — still wins,
because the band is read upward from the anchor exactly as before.

**Beyond that bound the two are different storeys.** A navmesh a whole actor's height or more below
the landscape is not ground the landscape was raised over — a mod can move a navmesh thousands of
units down, into empty space, and the terrain above it says nothing about whether a body can stand
where the navmesh is. There the navmesh anchor stays, the floor and the water decide exactly as
they always did, and the gap is published and counted (`stationsWhereTerrainIsAnotherStorey`) so
those stations are visible rather than silently re-grounded.

`NavmeshFloor`'s own defaults and semantics are untouched; every other detector reads the floor
exactly as it did.

Where several drawn navmesh triangles cover one XY — storeys that overlap, a deck over a path —
the **highest** is taken, and the number of such stations is reported in the header. The lower
surface would put the eye inside whatever is above it.

### Refusal reasons, and their precedence

A station can fail several conditions at once, and the yield table depends on which one it is
counted under, so the order is stated rather than left to the order the tests happen to run in.
Most decisive first:

| order | reason | when |
|---|---|---|
| 1 | `DegeneratePlane` | the primitive's extent, position or rotation is zero or non-finite |
| 2 | `NoNavmeshAnswer` | the navmesh was asked and returned nothing at all — an instrument failure, never a geometric miss |
| 3 | `NoCoveringTriangle` | no winning navmesh triangle covers the XY |
| 4 | `AllCoveringTrianglesDeleted` | every covering triangle carries the deleted flag (0x08) |
| 4a | `Unreachable` | route mode only: the covering triangle is not walkable (see Route mode) |
| 4b | `OutsideArea` | route mode only: the covering triangle is not in the area A_W |
| 4c | `InChildWorldspaceFootprint` | route mode only: the XY lies under a child worldspace's navmesh |
| 4d | `TightClearance` | route mode only: fewer than 5 of 5 clearance probes on the same floor |
| 5 | `FloorUnknown` | a navmesh answer exists but no physical floor was found under it |
| 6 | `WaterUnknown` | the applicable surface could not be resolved. Refused, never read as dry |
| 7 | `StationUnderwater` | the feet stand below the applicable surface |
| 8 | `EyeInsidePrimitive` | the eye lies inside or on the primitive, where it has no view shadow to be ranked by |
| 9 | `EyeOutsidePlaneExtent` | the eye is outside the primitive's vertical extent (face-on mode only) |
| 10 | `EdgeOn` | observing mode only: the view is within 10° of the primitive's plane |
| 11 | `ShadowOutOfFrame` | observing mode only: no part of the near face is inside the frame |
| 12 | `NoContentInShadow` | observing mode only: no content bound lies inside the shadow at this eye |
| 13 | `NonLevelGaze` | a guard. Level gaze is by construction, so this cannot fire on a composed pose; it exists so a later change cannot reintroduce a tilted aim in silence |

Why this order: an instrument that said nothing has not measured a miss, so instrument failures
outrank geometry. A surface can only be compared with feet that have a floor, so the ground
outranks the water. "Unknown" outranks "under water", because a station whose surface cannot be
resolved is not known to be under anything. The primitive-relative tests come last because they
need a finished eye. Every refusal carries the numbers that decided it in `rejectionDetail`.

### The water, and what it does about worldspaces

One resolver decides — `CellBoundaryIndex.Resolve` over `DetectorModel`, the same one the water
detectors use — and it is asked about **the cell the station's own XY falls in**, never the
primitive's. One ladder reaches up to the standoff cap either side of its primitive while an
exterior cell is 4,096 u across, so a station over the border stands in the neighbour's cell and is
judged by the neighbour's surface.

- A cell that states a usable height of its own gets it (`Real`).
- A cell whose height is one of the engine's "use the level above" markers defers to the
  worldspace default (`WorldspaceDefault`). **"No water height logged" is not dry** — it is the
  marker, and the default applies.
- A worldspace that itself defers is followed to its **parent** worldspace, and the pose's
  `fromWorldspace` names the worldspace the height actually came from, so an inherited surface is
  visible rather than implied. A chain that ends, loops or runs past its hop limit without a usable
  height resolves to nothing. **This parent walk is a records-based reading, not an engine
  measurement:** nothing in this project has watched the engine follow a worldspace parent for
  water. It is what the records say, and it is stated here so a result that depends on it can be
  weighed accordingly.
- **A failed READ is never an answer.** A grid square the order defines no cell for, a worldspace
  record that could not be read, or a parent link that leads to a record that could not be read
  all resolve to `Unresolved`, and the station is refused `WaterUnknown` with a sentence naming
  what failed. It is never read as dry — that substitution is the defect this rule was written
  against.
- Where a cell's own height settles the surface, an unreadable worldspace record does **not**
  refuse the station: it could not have changed the answer.

**What the one resolver does with the flag and the unmodelled markers**, recorded because it is
counter-intuitive and because a written plan for this work said otherwise:

- An **exterior** cell that clears its has-water flag but defers to the worldspace still has the
  sea (measured on a constructed harbour fixture — the swimmer swam). The flag is honoured where
  the cell states its own height.
- An unmodelled out-of-range marker is `Unresolved` — refused — **when the has-water flag is
  set**. With the flag **clear**, the resolver answers *known dry*, and this rule follows it
  rather than overruling it from the outside. Both rows are pinned by cases in `proofprobe`
  (`stationrule: B2 …`), which is where to look before assuming either.

### Observing mode (`"mode": "observing"`)

The face-on ladder stands along each face's normal. For a panel set in a wall, that aims into the
wall: nothing behind a panel that culls nothing is visible, so a **miss**, where the game hides
something the rule called clean, cannot be seen from those stations. The observing mode composes
stations that **can** see content inside the primitive's predicted shadow.

- **A ring, not a line.** Per face, `k` × bearing × `lateral`, with bearings every 30° (0…330)
  measured clockwise in plan from the face's outward normal: 108 stations for a `PortalBox` and
  216 for a `Box`. The station stands at `centre + dir(θ)·(faceHalf + d) + lateral·halfWidth`
  along the face-on lateral axis. `d` is the face-on standoff, so **bearings 0 and 180 are the
  face-on +1 and −1 stations exactly**. The gaze is level, at the primitive centre's XY.
  `angleToPlaneNormalDeg` is `acos|view·n|`, in 0…90, and `side` is the side of the plane the eye
  stands on.
- **The player constraints are the face-on rule's, unchanged:** C1–C5, the one water resolver, the
  eye outside the primitive, one closed refusal set and one precedence. **C6 is dropped in this
  mode only.** C6 is a primitive-relative test, not a player constraint, and dropping it is what
  lets a station stand above a panel's top and look down behind it.
- **Two geometric refusals.**
  - `EdgeOn`: the view is within 10° of the plane.
  - `ShadowOutOfFrame`: no part of the near face lies inside the frame. The near face is a
    PortalBox's mid-quad, or a Box's front-facing faces. It is clipped against a level camera's
    frustum: horizontal `fovDeg` at 16:9, near plane 15 u.
- **Content in the shadow.** The shadow is `HousecarlCore.ConvexShadow` from the eye, built as the
  renderer builds it. A content bound counts when `ConvexShadow.Classify` calls it `Inside`, the
  renderer's own test. A pose with none is refused `NoContentInShadow`. Accepted poses carry
  `observingRank`: most content first, ties to the smaller angle to the normal.
- **Content is a pre-filter, and it can only undercount.** Content is every placed, enabled,
  undeleted reference with a **collision solid**, taken as the sphere around that solid's world
  box.
  - A reference with no collision solid (foliage, collision-less clutter) is not counted.
  - The sphere is larger than the renderer's per-shape stored sphere, so fewer references come
    out Inside than the render would call so.
  - A pose can therefore be refused for want of content that the render would have shown.
  - It can never be accepted for content that is not there.

  The header counts the primitives this cost:
  `observing.primitivesWithNoAcceptedPoseForWantOfContent`.
- **Visibility is not decided here.** This mode renders nothing. Whether the content inside the
  shadow is drawn and visible from the eye is decided from each pose's render (the planes
  report's per-shape `relation`, `predictedCulled` and `visibility.VisiblePixels`), outside
  viewprobe.
- **Tilted primitives** are composed. The shadow here is built from the full rotation, but the
  renderer's rule refuses a normal off level beyond its tolerance, so their renders predict
  nothing. The header lists them (`observing.primitivesNormalOffLevelBeyondRenderRule`) so a caller
  counts them as refused by the render rule, not as unobservable.
- **The file.** Each observing pose adds `mode`, `ringBearingDeg`, `contentInShadow`,
  `contentKeys` and `observingRank`. The header adds `stationsMode` and an `observing` block.
  A face-on run writes none of these, so its file is byte-identical to what it always was
  (`proofprobe` M7, M9).

The known answers are `stationrule: M1 … M9` in `proofprobe`. M1, M3 and M5 were checked against
an independent sampling oracle that shares no code with `ConvexShadow`.

### Route mode (`"mode": "route"`)

The face-on and observing modes stand stations off one primitive. The route mode samples **player
stations from the walkable ground around what one plugin changes in a worldspace**, and measures
how much of that ground the stations cover. The rule is `HarnessShared.RouteStations`; its known
answers are `stationrule: RT0 … RT17` in `proofprobe`. The positional cell only names the
worldspace; one run samples one worldspace.

```jsonc
{
  "mode": "route",
  "setup": "U",                             // required, as for the other modes
  "changedPlugin": "Some City.esp",         // required: whose changes the area is built around
  "childWorldspaces": ["01A26F:Skyrim.esm"],// optional: a child worldspace whose winning navmesh
                                            //   refuses a station of this one (see below)
  "fileMarginSquares": 4,                   // optional: how far around the plugin's own content
                                            //   the floor is filed; a loaded square outside it
                                            //   REFUSES the run rather than composing on no floor
  "dumpAllNavmeshes": false,                // optional: the raw dump holds every winning navmesh
  "coverageLevels": {                       // optional: coverage of later station sets (L2-L4), by
    "L2": { "stations": ["<id>", "..."],    //   the same geometry; inline or a path to a file of
            "droppedReasons": { "<id>": "<why>" } }   // this shape. An accepted station not in the
  }                                         //   set drops out as "NotInLevel" unless a reason is given
  // "rArea", "spacing", "coverageDistance", "eyeHeight", "retryAlternates", "fovDeg": optional,
  //   and CHECKED, never applied - the sampling parameters are sealed in the rule
}
```

- **The changed content, X_W.** Every placed record the plugin defines or overrides in the
  worldspace, at the plugin's own position; for an override, the previous plugin's position too
  when the plugin moved it by more than 1 u, or when its version sets the initially-disabled
  (0x0800) or deleted (0x0020) header flag. Plan points; markers are kept, and the count whose base
  has no model is published. **Known limit:** the versions are read from this worldspace's cells
  only, so when an earlier plugin had moved the record to another worldspace or an interior, "the
  previous plugin's position" is the latest one still in this worldspace, not that plugin's.
- **Loading (one graph per worldspace).** The area squares are the 4,096 u squares any disc of
  2,048 u around an X_W point touches, plus the squares holding the plugin's own navmesh
  triangles; the loaded set is the winning navmeshes filed in those squares plus a one-square ring.
  The renderer's walk is one-cell shaped, so the mode runs its own pass over the worldspace's cells
  after it, reading the plugin's placed records raw (a deleted version with no position is kept).
  The floor and the occlusion planes of that pass follow each reference's last sighting over every
  cell of every plugin (`_shared/LastSighting.cs`, `FloorFeed.FloorIndex`): a reference a later
  plugin moves out of the bound, to another worldspace or an interior, re-bases or deletes is not
  kept at its earlier place, and a station floor over a grid whose last LAND gives no heights is
  refused (exit 2) rather than read from an earlier plugin's heights. The floor's candidates include
  the same reach as `--stations` (references beyond the 3×3 whose placed box enters it; see above).
  Cross-cell links are traversed inside the loaded set; `ExternalLinks` counts only links that
  leave it.
- **Walkable.** A triangle is a node when it is not deleted (0x08) and its vertices are in range
  (NavGraph). Components are undirected over the record's own links (`EdgeLink` with the flag bit
  clear = same mesh, set = the `EdgeLinks` table, traversed when the target mesh is loaded). A
  component is reachable when it holds a `DoorTriangles` entry or an exit (a cross-mesh link to a
  mesh outside the loaded set). Doors are recorded, never traversed.
- **The area A_W** is the walkable ground within 2,048 u (plan) of an X_W point, clipped
  **exactly** (triangle ∩ union of discs), plus the whole of every walkable triangle of the
  plugin's own navmeshes.
- **Route candidates.** Endpoints are door and exit triangles in A_W. Each door is paired with its
  4 nearest doors and its nearest exit (centroid plan distance, ties by mesh FormKey and index);
  each pair is solved as `--route-solve` solves a leg (NavGraph A* on centroid distance, the
  funnel, the 6 u corner inset). A path within 64 u of an already-kept path at every 32 u sample
  is dropped. Candidates stand at 256 u along each kept path, then every 512 u. Note what the
  method is: the funnel is taut **within A*'s channel**, which on a fine mesh can be a staircase,
  so a solved path is not the shortest walk. Each route station publishes `fwdVsAheadDeg`, the
  angle between its path direction and the path 512 u ahead, so that effect is measured.
- **Fill candidates.** Every A_W piece with any point farther than 512 u from an accepted station
  is split by 4-way midpoint subdivision until each part is at most 512² u²; the parts' centroids
  are tested in the order of `sha256("g5-fill:20260925:<mesh>:<tri>:<subPath>")`, each only if it
  is at least 512 u from every accepted station. A refused one gets up to 6 seeded alternates in
  the same part (`g5-retry:20260925:…:<j>`).
- **The player checks are the station rule's own** (`StationRule.CheckGround`, `CheckWater`: the
  navmesh answer, the covering triangle, the physical floor + 120.5 u, the water through the one
  resolver), plus the route refusals: `Unreachable` (the covering triangle - the highest drawn one,
  searched over **every** winning navmesh of the worldspace - is not walkable, or lies in a navmesh
  outside the loaded set), `OutsideArea`, `InChildWorldspaceFootprint` (a parent-worldspace XY
  under any winning triangle of a named child worldspace, any flags: such an XY can arrive in the
  child in the game), `TightClearance` (`NavCover.Clearance`, the test `--route` uses) and
  `EyeInsidePrimitive` (any winning PortalBox or Box of the loaded set, under its full rotation).
  A candidate within 128 u of an accepted station is dropped as `Deduplicated`.
- **Gazes** per accepted station, level at the eye, 80° at 16:9: along the path both ways (a fill
  station takes the first segment of its solved path to its nearest endpoint by walked length),
  and the 5° bearing whose frustum holds the most X_W points with a model within 4,096 u; a gaze
  within 30° of an earlier one is dropped.
- **Coverage** is the plan area of A_W within 512 u of an accepted station, exactly (Green's
  theorem over the triangle-edge pieces and the discs' boundary arcs), per worldspace. **Plan
  distance is not line of sight.** The header adds the route-only share, the share on the
  plugin's own navmesh, the uncovered area by the refusal reason found in each uncovered piece,
  and the largest uncovered pieces located.
- **The files**, all beside `--stations-out`:
  - the pose file (`occl-transfer/station-poses-route/1`): one row per gaze of every accepted
    station and one row per refused candidate, with the numbers that decided it; the per-family
    yield (route, fill, fill-retry) must close - accepted + refused + dropped = composed - or the
    write is refused;
  - `stations-<worldspace>.json`: every composed candidate, accepted, refused or dropped;
  - `classify-<worldspace>.json`: every triangle's verdict (walkable, component, in A_W, clipped
    area, covered area), for reconciliation;
  - `navdump-<worldspace>.json`: the raw record fields (navmesh vertices, triangles, link fields,
    `EdgeLinks`, `DoorTriangles`; the plugin's placed records with every version's position and
    header flags), written by code that classifies nothing, so an independent implementation can
    recompute the area and the coverage.

### What it deliberately does not check, and does not do

- **Interiors.** Both the water and the floor are looked up by exterior grid square, and an
  interior cell has no grid. An interior target is **refused with a message** rather than run
  through a path nothing has tested.
- **Whether the eye is inside a solid reference's bounds.** `shotplan.py`'s `inside_verdict` does
  that for interiors; this mode does not.
- **Whether the body would slide after landing.** A pose that is standable is not the same as a
  pose a landed body keeps; the in-game landing assertion is the gate for that.
- **Whether the station is reachable on foot** (face-on and observing modes). A station that
  passes C1–C6 is one a body can *stand at*, which is what a render needs. "Walk to" is a stronger
  claim and those modes never make it; the route mode asks only that the station's triangle be in
  a component with a door or an exit, which is still not a walk from anywhere in particular.
- **Primitives tilted off the vertical.** The rule models an upright primitive turned about Z only.
  A primitive carrying a rotation about X or Y is composed on that upright reading, counted in the
  header as `primitivesTiltedOffZ` and named on the console — stated, not hidden.
- **It does not pick a station.** It publishes every station it composed with the numbers that
  decided its verdict. The face-on mode does not rank stations. The observing mode ranks accepted
  poses by content in the shadow, but choosing among them, and rendering them, is the caller's.

---

## Landscape seams and the water arms (`--seams`, `--water-arm`)

Added 2026-09-04 as the render side of two detectors (roadmap 4.9): `seamprobe` measures where two
adjacent cells' LAND records disagree about the vertices they share, and `waterprobe` measures where a
winning CELL override carries a different water tuple from the one the game's own masters set. Both
draw here so a row can be looked at from a planned camera. They report; the probes judge.

**`--seams [EPS]`** — the engine draws each cell's LAND on its own from its 33×33 heightmap, and vertex
32 of one cell is vertex 0 of the next. Where the two records disagree by more than EPS (default 8 u,
one heightmap unit — the smallest non-zero step the format can express) the render draws a ribbon along
that run of the edge, lifted 6 u over the higher side: yellow under 32 u (a lip), orange under 128 u
(a knee-high step), red beyond (a wall). The console lists every mismatched edge in the ring with its
worst step and how many of the 33 vertices are over EPS. The decode is `HousecarlCore.LandHeight` —
the same function the detector reads, moved out of this file on the same day so the two cannot hold
different opinions about where the ground is. Winners only: whose seam it is (Introduced / Inherited,
Blind / Seen) is `seamprobe`'s answer.

**`--water-arm winner|truth`** — the cell-water surfaces are drawn from the winner's tuple (the default,
what the render always did) or from the TRUTH's: the last statement by a Bethesda master or the
Unofficial Patch, by filename. The console prints every ring cell under both arms — the WATR, the
resolved height, and the share of the cell's LAND vertices under that surface (the number that says
whether a surface is drawn under the ground) — and flags the cells where the arms differ. A water
mod's deliberate retype shows as a difference here; `waterprobe` tells it apart from a port's revert.

```
viewprobe KatariahExterior01 --eye -51200,101000,-9500 --look -51200,108544,-13800 --seams --water-arm truth --no-nav
viewprobe SolitudeExterior01 --eye -67584,103000,-1500 --look -67584,106496,-5500 --seams --no-nav
```

## The Base Object Swapper layer (`--bos off|on|diff`)

Added 2026-09-04 (roadmap 4.10). Base Object Swapper (powerofthree's SKSE plugin, Nexus 60805) reads
`*_SWAP.ini` files and, as each reference initialises, replaces its base object with another and
optionally overrides its position, rotation, scale and record flags. Furniture replacers, animated
tents, "no more statics" addons, city-wall re-placements and destructible clutter are all BOS-driven,
so a render of the record bases shows a room the player never sees. The reader lives in
`housecarl-core/BaseObjectSwaps.cs` (one function for this renderer and every geometry detector —
`_shared/BosHook.cs` applies it to the shared indexes, `bosprobe` runs it on its own), and its rules
are transcribed from the plugin's source, not its mod page; the by-construction proof is
`proofprobe` (`bos/*` cases) and the parse-level proof is the game's own `po3_BaseObjectSwapper.log`,
which the reader reproduces line for line (`bosprobe --compare-log`).

**What the game does, as the source says it** (the reader mirrors each point; the findings note has
the argument for every one):

- **Discovery:** every file directly in `Data\` — NOT in subfolders — whose extension is exactly
  `.ini` and whose name contains `_SWAP`, both case-sensitive, read in ordinal order. Under MO2 the
  Data root is the VFS: overwrite, then the enabled mods by priority, then the game's Data; the first
  provider of a filename wins and the rest are shadowed. `X_swap.ini`, `X_SWAP.INI` and
  `sub\X_SWAP.ini` are never read; the run says so when it sees one.
- **Reading:** CSimpleIni's rules — `;`/`#` comments, `[…]` headers to the first `]` (an unterminated
  one is skipped), keys split at a first `=`, duplicate section names merged, duplicate keys in a
  section collapsed to the first, ONE leading byte-order mark skipped (a second is text). Keys before
  the first header form a section named `[]`, read as a References table.
- **Sections:** `[Forms]` swaps by base, `[References]` by reference, `[Transforms]`/`[Properties]`
  overrides without a swap, `[Forms|f1,f2,…]` swaps gated by filters — a location (the ref's or an
  ancestor of it), a region, a keyword (on the location OR on the base), a cell, a worldspace (own or
  parent); `-f` excludes. Any other `[X|…]` is filtered overrides; any other plain `[X]` is a
  References table.
- **Order:** References (by ref) → filtered Forms → plain Forms; filtered sections in REVERSE
  ordinal order of `IniName|conditions` (a `std::map`, not file order); within a table the LAST line
  first; the first line whose chance passes and whose drawn target names a record wins. Properties:
  the winning swap line's own, else References → filtered → plain transforms by the ORIGINAL base.
- **Randomness:** `chance(p)` (`S` per ref across sessions — the default; `L` per location and base;
  `R` re-rolled per session unless a seed is given), `a|x,y,z` one member per ref, `a,b|x,y,z` one
  member per original assigned at load. The reader never rolls: it returns the SET with
  probabilities; the renderer draws the FIRST (BOS's own order — a set's lowest runtime FormID) and
  flags it in the hover, the caption and the console. A swap to a leveled item is a set of its
  entries (an even split, labelled an approximation) unless the ref or its cell carries an encounter
  zone, which cancels it.
- **Transforms:** `posR`/`posA`, `rotR`/`rotA` (degrees), `scale(a/b)` multiplicative and `scaleA`
  absolute in the engine's percent domain (100 = 1.0), `flags`/`flagsC`; a `min/max` component is a
  range drawn at runtime — the renderer takes the minimum and says so. `flags(0x800)` at init makes
  the ref initially disabled; the renderer drops it and counts it.
- **Scope:** the hooks are on the TESObjectREFR, Hazard and ArrowProjectile vtables — a placed
  ACTOR is never swapped. Three quirks of the plugin the reader keeps on purpose: a `flags(…)` token
  whose hex digits contain a `C` is read as a CLEAR (`contains("C")`), `PosR` with a capital P is
  ignored (`contains("pos")`), and `0xID~Plugin` is accepted whenever the plugin is loaded, whether
  or not the record exists.

**What the picture shows.** `on`: the effective base's mesh at the effective transform, nothing
marked. `diff`: the same scene, swapped refs tinted teal (the occlusion preview's emissive-wash
mechanism with its own saved state; a mesh both previews touch takes the last one applied). `off`:
the record bases. The console prints the ini count read from the VFS, the swapped/random/override
tallies for the drawn refs and the swaps per ini; the caption carries `BOS on|diff: N swapped ref(s),
M random`. The hover names `BOS <record> -> <effective> (<ini>:<line>) [random: k alternatives, the
first drawn] [pos rot scale flags from <ini>:<line>]`. In the window `B` toggles the paint
`on` ↔ `diff`; a scene built `off` says so rather than pretending to toggle (off is a different scene).

**Verified** (2026-09-04): an inn on a furniture-replacer order — 18 chairs and beds swapped, all
random lines — rendered `off` and `diff` from the same eye; 4.7 % of the frame differs and the
difference is the chairs (the swapped mesh, teal in `diff`); a first camera that framed a wall differed
in the caption only, which is what a render with the swaps out of frame must do.

```
viewprobe SomeInnCell --eye 330,-1180,170 --look 140,-960,50 --bos diff
viewprobe SomeInnCell --eye 330,-1180,170 --look 140,-960,50 --bos off     # the record bases, for the diff
bosprobe --cell SomeInnCell                                                  # every ref's answer, with the alternatives
```

## The world state (`--state`, `--state-view on|diff`)

Added 2026-09-04 (roadmap 4.11). The renderer's "does this thing exist" has been the load-time
state since 2026-08-28: a ref's own initially-disabled flag when it has no XESP enable parent, the
parent chain's state otherwise (`EnableState`, shared with the placement-contact index). Nothing
modelled a later flip — a quest stage, a scene or a trigger volume calling `Enable()` / `Disable()`
on a marker whose children then swap in one instant: the on-set (same state as the parent) arrives
as the off-set (opposite flag) leaves. `--state` draws that world.

- **A switch** is any placed reference that is the winning XESP parent of another (the player, ID
  `0x14`, is never one: parent = player + opposite is the permanently-disabled idiom). Its census —
  every switch, its on-set and off-set with chains followed by parity, the navcut boxes in each
  set, the plugin that placed the marker and the plugins that attached each child, the vanilla floor
  per cell and region, and the trigger map (which quest stage, scene or event flips it, read from
  the script bindings and the decompiled scripts) — is `switchprobe`; the class across a flip
  (StateConditional / StateResolved rows over overlap, contact and the route rule) is `stateprobe`;
  the same arm is `--state` on overlapprobe, contactprobe and blockprobe. All of it reads
  `_shared/WorldState.cs` (`SwitchIndex`, `StateArm`); this renderer takes the arm alone
  (`_shared/StateArm.cs`) and resolves it over its own whole-order XESP maps.
- **A flip** sets the switch's own runtime state to the opposite of its load-time state and cuts its
  own parent link (`Enable()`/`Disable()` on the marker itself); every descendant follows through the
  unchanged chain rule. A joint flip (`stage:<quest>:<n>`) applies every switch that quest stage's
  fragment touches, read from switchprobe's JSON. Nothing else changes: no placement, no base, no
  navmesh. A token that names no switch in the order is refused, never ignored — a state that
  silently flipped nothing would read as the baseline.
- **What the picture shows.** `on`: the state — refs enabled under the flip are drawn, the rest
  are not; the console's "refs not drawn" line counts the departed with the disabled. `diff`: the
  state AND the refs the flip took out, arrivals tinted GREEN and departures RED (the occlusion
  preview's emissive-wash mechanism with its own saved state, applied after the BOS paint — a mesh
  both touch takes the last applied). The console prints `STATE <name> <view>: N ref(s) in this
  cell arrive with the flip, M depart`; the caption carries the same; the hover names
  `STATE <name>: arrives|departs with the flip of <switch>` — the switch at the top of the ref's
  chain among those flipped. In the window `T` toggles `on` ↔ `diff` when the scene was built
  `diff` (the departed refs are resident, hidden in `on` through the same visibility flag the
  height clip and the occlusion preview write); a scene built `on` holds no departed ref and says
  so rather than pretending to toggle.
- **What it does not do.** The bar does not cycle the CELL's switches — a scene is built for one
  state; another state is another run (or a worklist of runs). The trigger map is not consulted
  here: an Unknown-trigger switch flips like any other, and whether the state is reachable is the
  map's business (switchprobe prints Triggered / Bound-but-unread / Unknown per switch).

**Verified** (2026-09-04, the lab's College of Winterhold overhaul, a furnished room whose marker
is enabled at load): rendered baseline, `on` and `diff` from one eye — `on` shows the room stripped
(the 46 furnishings gone, the bare frame the off-set brings in present), `diff` shows the
furnishings tinted red where they stood and the bare frame green; 1.8 % of the frame differs between
baseline and `on`, 2.5 % between baseline and `diff`, and the difference is the furniture.

```
viewprobe SomeHallCell --eye 1150,350,-120 --look 652,-94,-384 --state SomeRoomMarker --state-view diff
viewprobe SomeHallCell --eye 1150,350,-120 --look 652,-94,-384 --state SomeRoomMarker              # the state alone
viewprobe SomeHallCell --eye ... --state stage:SomeQuest:200 --switch-map switches.json             # a quest stage's joint flip
switchprobe --cell SomeHallCell                                                                     # which switches stand here, and what flips them
```

## The depth buffer is the arbiter

**The defect.** An edge classified `visible` was drawn SOLID and depth-tested, so a wrong
classification did not merely pick the wrong style — the edge was clipped by the very thing that hid
it and **disappeared entirely**. Any slack in the occlusion test is a blind zone, and a rug, a plank
edge and a shallow rise all fit inside one. Tightening the threshold recovered most of it but could
not close it: a threshold cannot be both safe against self-occlusion and exact.

**The fix is structural.** Every segment now gets BOTH a solid depth-tested copy — so it draws
wherever it is genuinely visible and is clipped wherever it is not, per pixel, exactly — and an
always-visible DASHED copy, which is precisely what remains where the solid copy was clipped. Over a
segment that really is visible the dashes land on the solid line, same colour, same width, and
change nothing. So with both dash categories on (the default) **the classification has no effect on
the picture at all**: solid where visible, dashed where hidden, and the GPU decides which. The
classification survives only to name the CATEGORY, for the two toggles and the counts.

**The general shape of it:** a classification that decides *style* is forgiving of error; one that
also decides *whether the GPU will clip it* is not. Don't tune the threshold — remove its authority
over visibility.

---

## Determinism

viewprobe is bit-reproducible — two runs of the same command produce byte-identical PNGs —
**except with terrain paint layers and MSAA both on**, where the driver's per-sample blending of
many coplanar sheets varies by 1/255 on a handful of pixels (5–25 of 6.5M, measured). Isolated by
control: layers with `--msaa 0` is byte-exact, and MSAA with `--no-terrain-layers` is byte-exact.

So **run the compare regression with `--msaa 0`** and keep the byte-exact check, rather than
loosening the check to a tolerance. Deliverable renders keep MSAA.

`--npc` actors are deterministic (measured: a three-actor interior render is byte-identical
across runs at `--msaa 0`); the puck's fill disc rides the same decal channel as the painted
layers, so under MSAA it belongs to the same known 1/255 nondeterminism class.

---

## Verification discipline (standing, learned the hard way)

**Console counts describe the pipeline, not the picture.** After ANY render change: view the image
and confirm it shows what the claim says, and back identical/different claims with a pixel diff.
This has caught the same class of error repeatedly — most recently a console line reading
"52 effect shapes DRAWN" while the frame showed no fire at all, twice, for two different reasons.

Three checks must pass after a render change (all via `tools/imgcheck.py`):

1. **Compare semantics** — panel 2 must be byte-identical to panel 3 *below the caption bar* (the
   captions differ by design; the bar is `10*s + 22*s*lines` px tall with `s = size/1280`). The
   winner's panel MUST equal the winning panel — and since the diff spheres are part of the panel,
   this also proves both panels resolved the same set of changed vertices.
2. **The disagreement is still visible** — panel 1 vs panel 3 must NOT be identical, or the compare
   is showing nothing.
3. **The stitch is lossless** — a raw panel PNG must be byte-identical to its slice of the stitched
   file. This is what the alpha defect below broke.

Run checks 1 and 2 on a `--msaa 0` compare (see *Determinism*).

**When a change is meant to be invisible, prove it:** render with the new flag off and diff against
the previous build's output.

**Shape arithmetic is the strong check for a filter change:** drawn-after + skipped must equal
drawn-before, exactly. That is what proves nothing *else* was dropped, which a picture cannot.

### Compose cameras from measurements, not memory

**This section is the single source for camera placement.** Guessing world coordinates or ground
height fails almost every time in an unfamiliar scene — six consecutive bad renders across
Winterhold College and the Solitude Temple before the method below was adopted.

The working sequence:

1. **`--eye-ref FORMID` / `--look-ref FORMID`** when the shot is about an actor or a door — the
   camera lands exactly where the record says, at eye height. Always prefer this to coordinates.
2. **Flat diagnostic mode** (no `--eye`) for the `bounds :` line and ref counts.
3. **A cheap probe render** (`--size 320 --msaa 0 --nav off`) carrying `--nav-at X,Y` for the
   walkable surface height and `--refs-at` / `--pick` to identify what is at a spot.
4. Only then the deliverable camera.

What that protects against:

- **Ground heights vary wildly, including inside one cell.** A navmesh vertex from the same cell can
  sit thousands of units below the deck the player walks on — one measured exterior had its
  courtyard at z ≈ −7750, the shore below it at −12300 and tower features at −4600. "Somewhere
  around ground level" is not a number, and any figure quoted here was measured on ONE load order:
  measure your own.
- **Interior top-downs must slot the camera BETWEEN the target floor and the next storey.** Above
  that you render rafters. `--nav-at` returns one row per level, which is how you find the gap — a
  measured multi-storey interior returned 1424 for the gallery and 1712 / 2000 / 2288 above it.
- **Never put a first-person camera inside a building's footprint.** Exterior shells are
  single-sided, so from inside, their walls back-face cull to nothing and the structure vanishes —
  a door then reads as free-standing. Shoot from where a character would actually stand;
  `--refs-at` at the camera spot shows which shell you may be standing inside.
- **The `--conflicts` suggested view has no occlusion check.** It aims at the red centroid and can
  land inside a boulder.
- **The conflicts overlay paints red only for navmeshes in the TARGET cell**, and persistent refs
  merge only within that cell's bounds — so pick the target cell to match what the shot must show.
  Large statics are often persistent refs belonging to a neighbouring cell.
- **Finding the right cell is its own step.** Ref count and persistent-ref count from the flat probe
  are a good proxy for "is this the interesting cell"; a cell's EditorID is not a reliable guide to
  what is in it.

### Matching this square frame to the game's 16:9 one

viewprobe renders SQUARE, and `--fov` is the VERTICAL angle of that square. The game renders at the
output resolution, and `fDefaultWorldFOV` is the HORIZONTAL angle at a fixed 16:9 base, so the
game's vertical angle is fixed by the setting alone:

    tan(fov_v / 2) = tan(setting / 2) / (16 / 9)
    tan(fov_h / 2) = tan(fov_v / 2) * (width / height)

`tools/shotplan.py frame --from-profile` reads the setting and the resolution and prints both.

Choose a square that contains the native region you intend to evaluate:

* **A square at the game's VERTICAL angle** covers the central square of a landscape frame.
  It excludes the native frame's outer sides. Counts remain scoped to that region; they cannot
  establish visibility of an entire subject that extends outside it. Off-frame is not hidden.
* **A square at the game's HORIZONTAL angle** (equal to the setting at 16:9) contains the whole landscape frame
  as its central band, of height

      band = tan(fov_v / 2) / tan(fov_h / 2)          # exactly 0.5625 at 16:9

  with the same pinhole pose, so every angle maps into the native frame by a single scale.

For a whole landscape-frame comparison, use the second and record the band alongside the render.
Recompute the angles from the effective setting and output dimensions. A portrait frame instead
fits in a square at the vertical angle, with a central width band. Two practical notes:

* **Pick a `--size` that preserves the focal scale** you were working at — near the frame centre,
  pixels per radian is `size / (2 * tan(fov / 2))`. A close match keeps subject footprints
  comparable across the projection change; record the residual scale difference.
* **`--pick` coordinates are projection-dependent.** Recompute every one of them when the frame
  changes; a stale pick silently samples a different world point.

Verify the result against an actual game frame: crop the band, scale it to the native still, and
check registration with a declared tolerance appropriate to the resolutions and scene. Keep pixels
outside the matched native region unassessed. A projection match does not establish physical
footing, enabled scene state, subject identity, or independent evidence of engine culling.

`--nav-at` can print multiple covering triangles for one coordinate, including different storeys.
Consumers must associate answers with the printed coordinates and preserve that multiplicity;
pairing requests and output lines by position can silently assign the next point's floor. Printed
coordinates are rounded, so ambiguous matches must be refused. Navmesh coverage alone does not
prove that the loaded scene supplies a floor at that height.

### Getting there fast: the arithmetic, and what the render tells you for free

The section above stops you shooting blind. This one stops you *iterating*. Most wasted renders
are not wrong-place errors — they are right-place, wrong-distance errors, and framing is
arithmetic, not taste.

**One number does most of the work.** The image is square and `--fov` is the full vertical angle,
so at the default 65°:

```
half-span = distance x tan(fov/2) = 0.637 x distance      (fov 65)
```

- **Top-down.** To cover a region of half-width `H` centred on subject height `z`:
  `eye z = z + H / 0.637`. A 1,500 u half-span wants the camera 2,354 u up. This lands the frame
  correctly on the first try, every time.
- **Eye level.** A subject `W` wide fills fraction `f` of the frame at
  `distance = W / (1.27 x f)`. A ~200 u doorway at a quarter of frame width wants ~630 u of
  standoff. Measured failure mode: 300 u puts a door edge-to-edge and the navmesh wire goes
  billboard-thick; 600 u shows the door *in its approach*, which is what a "where do I stand"
  shot is for.

**Eye height is floor + `--eye-height` (default 120).** `--nav-at` hands you the floor `z`
directly, so the standing camera is `nav z + 120` with no guessing. Give `player.setpos` the
floor `z`, not the eye `z` — that trips people every time.

**Batch every question into one invocation.** The load walk dominates the cost; the queries are
free once it has run. `--nav-at`, `--refs-at` and `--pick` are all repeatable, so ask six to ten
questions at once — a cross of candidate standing spots, the subject's surroundings, and the
level structure — instead of paying the walk per question.

**For an unfamiliar interior, render the plan FIRST, then `--pick` off it.** Probing blind
coordinates in a building you have not seen is guesswork; a sliced top-down (`--cull-above` just
above the subject's floor) is a floor plan, and `--pick` converts any pixel in it into real world
coordinates you can then stand at. Plan → pick → eye-level beats guess → render → adjust by a
wide margin.

**One `--nav-at` reveals the whole vertical structure.** It returns a row per covering triangle,
so a single probe at one X,Y tells you how many walkable storeys exist and their heights — the
fastest way to learn a multi-level interior, and the way to find the gap a top-down camera must
sit in.

#### Standing positions for a human, not just a camera

A camera can float; a player cannot. When the output of a shot is a `player.setpos` a person will
paste into a console, two separate failures put them inside a wall — both observed in play:

- **The point was never walkable.** `--pick` returns whatever *surface* a pixel hit. `--nav-at`
  returns *standable ground*. They are not the same, and a pick onto a floor-looking surface can
  be metres from any navmesh. **Source standing spots from `--nav-at` only**, and
  **clearance-test**: probe the point plus ±64 u in x and y and require all five covered at the
  same z. A player capsule is roughly 35 u in radius, so a point technically on navmesh but hard
  against a wall still embeds them. One measured failure: a spot taken from a pick had its
  nearest navmesh triangle 52 u away.
- **The intermediate states.** Three `player.setpos` calls move one axis at a time, so the player
  passes through two positions nobody checked, and either can be solid geometry. A destination
  that clearance-tests clean can still trap them on the way in. **Wrap the sequence in `tcl` …
  `tcl`** — noclip off at the end drops them onto the floor.

Hand over the floor `z` from `--nav-at`, not the eye `z` the camera used; the eye is
`floor + --eye-height`.

#### The render answers back — read it before you look at pixels

Four channels of free feedback, all on stdout or burnt into the image:

- **The caption carries the resolved pose** (`eye X,Y,Z yaw ...° pitch ...° fov ...°`), and every
  PNG embeds its replay args. A top-down reading `yaw 0° pitch 90°` *is* the confirmation that the
  frame is north-up — no `--pick` round-trip needed. Any shot re-aims straight from its own image.
- **An occluded actor prints its own fix**, including a suggested `--cull-above Z` computed from
  that actor's height. Take the suggestion rather than bisecting cut heights by hand; if it still
  reports hidden, the occluder straddles the cut (the all-verts-above rule keeps straddling
  triangles, which is what keeps walls standing).
- **`outside the frame at this camera`** is a framing failure reported before you open the image.
  Treat any actor line other than a clean build as a re-shoot.
- **`--conflicts` prints a suggested camera** from the red centroid — a free starting point, but
  it has no occlusion check and can land inside a rock. Use it as a seed, not an answer.

#### Two ordering traps that cost a round each

- **The overlay and captions exist only on the textured single-camera path.** With no `--eye` /
  `--look` the run takes the flat multi-view path instead: no conflict overlay, no red, no
  caption. If `--conflicts` seems to have done nothing, check that a camera was given.
- **Pick the target cell for the FINDING, not for the camera.** Ring cells now supply refs, so the
  camera no longer has to stand in the target cell — but navmesh, the conflict overlay and the
  cell outline stay target-cell scoped. Aim the target at whatever must be painted; put the camera
  wherever it reads best.

#### Letting the tool pick the camera (`tools/shotplan.py`)

Everything above is a method a person applies. `tools/shotplan.py` is that method as a script: give
it a cell and one or more subject positions and it returns a camera, in both this tool's
conventions and a game-side one, **or refuses and says why**. It adds no verb here — it drives
`--pick`, `--nav-at` and `--refs-at`, and the only new thing is the arithmetic between them.

The trick that makes it affordable is the **clearance map**. Testing candidate cameras one at a
time costs a cell load each. Instead it puts the probe camera AT a subject, opens the field of view
and picks a fan of rays around the horizon; every pick returns the world coordinates of the first
geometry along that ray, so ONE render yields the clearance in every direction around that subject
— and therefore every camera position that can see it. Occlusion is symmetric: subject → point
clear means point → subject clear. One map per subject, intersected, is the set of cameras that see
everybody. Measured on a Solitude interior: four sector renders per subject at `--size 320
--msaa 0`, ~22 s each, and 720 candidate poses scored afterwards for free.

Three passes, each paying only for what it needs:

1. **clearance maps** — `--map-yaws` renders per subject (default four at `--fov-map 100`, which
   tile 360° with margin). Pixels are solved FROM the desired bearings rather than laid out as a
   uniform grid, so the angular step is exactly `--bearing-step` everywhere including the frame
   edges.
2. **scoring** — sightline and framing are pure arithmetic over pass 1, so the whole polar grid of
   candidates is judged without another render. Framing is the exact rectilinear test, `|x/z|` and
   `|y/z|` against `tan(fov/2)`; the `0.637 × distance` rule above is that same test at fov 65.
3. **placement** — one render carrying `--nav-at` for the survivors. Batched here rather than with
   pass 1 because the full grid is ~1,400 probes and that many flags overruns a Windows command
   line; anything dropped by `--nav-probe-max` is named, never silently cut.

Then the **final check**: one render using the chosen camera's eye, yaw and pitch, with a `--pick`
projected for each target. This replaces re-aiming the camera separately at each subject. The
map is an interpolation between sampled bearings probed from the target's XY at eye height.

**The final check governs the result.** It used to print its refusal and then write a successful
worklist, a `chosen.ok: true` and exit 0 anyway; that cost a session. Now:

| situation | `status` | `chosen.ok` | exit | worklist |
|---|---|---|---|---|
| verified | `verified` | `true` | 0 | written |
| the check refused it | `refused` | `false` | 1 | a **refusal record with no shots** (`--emit-refused` writes the shot itself, marked `REFUSED -- `) |
| `--no-verify` | `unverified` | `false` | 2 | written, marked `UNVERIFIED -- ` |

`chosen.ok` carries the FINAL answer in every row — a SKIPPED check leaves it `false` just as a
failed one does, because a proposal whose verification is missing has not been shown to be ok. The
pre-verification verdict survives beside it as `preVerificationOk`. The **worklist** carries
`status`, `poseId` and `scene` at its top level in every row of that table, so one read answers
*what is this file* whether it holds a shot or a refusal. When the top candidate fails,
the check walks down the ranked list (`--verify-candidates`, default 3, one render each) — a
SAMPLED search over the candidates this grid produced, never a proof that no camera exists.

**Every requested output path is claimed before the inputs are read.** Declining to WRITE a
success artifact is not enough when a caller reuses a path: an earlier run's verified worklist
otherwise sits there byte-identical, still saying `verified` and carrying the earlier run's pose
id, while the new run exits 1. Nor is claiming them once the run is under way: a refusal raised
while *reading* the inputs — a malformed `--surface`, a surface-only plan with no `--floor-z`,
more points than `--surface-max`, a `--from-profile` whose ini is missing — exits before every
writer, and each of those was measured leaving the previous run's verified JSON and its
renderable worklist in place. So the claim is the first statement of the command: every path the
caller NAMED is stamped `incomplete` up front and overwritten by the real result. Nothing else is
discovered, moved or deleted. The one case it cannot cover is argparse rejecting the command line
itself — the command never runs, and nothing at those paths is touched, which is correct.

**The pose is canonicalised once, before anything reads it.** Every consumer here formats a pose
to one decimal; projecting a target pixel from the UNROUNDED angles while submitting the rounded
ones samples a different ray than the renderer takes, and near a surface edge that lands on the
other side of it — a declared point 0.15 u inside a panel came back `not-surface` against a wall
550 u beyond, carrying the SAME exported pose and the SAME pose id as the run that confirms it. A
tolerance cannot repair that, because the error is *which surface was sampled*, not how far along
the ray. `canonical_pose` settles the numbers once and projection, containment, target distance,
identity and command generation all read those and no others.

Every artifact carries a `poseId` digest over the pose, its targets, the policy **and the scene**,
and **each candidate attempt carries its own**, so a saved camera, a saved verification and a saved
worklist can be checked to be the same candidate against the same scene. The scene half keeps two
things deliberately apart:

- **`declared`** — the caller's inputs (instance, profile, order, the viewprobe command). A
  declaration, never proof of bytes: `vp` is a command path, not a build hash, and a profile NAME
  is not its contents.
- **`renderer`** — viewprobe's own `capture input configuration:` fingerprint. The verification
  render *asks* for it (`--config-identity`, above) rather than hoping the caller passed
  `--order`, because that render's answers are what the verdict rests on; it is asked once per
  attempt, not once per probe. `rendererCovers` travels with it and says what the hash is OF:
  the plugin set and order with their content hashes, the provider list, the BOS overlays, the
  population and the state — **not** mesh or material bytes, not archive contents, not the
  renderer build. Two runs sharing a fingerprint read the same records; that is a narrower claim
  than "drew the same pixels", and it is written narrowly on purpose.

When the renderer reports none — an older binary, `--no-bind-scene`, or a refused resolve — the
field is `null` and reads **ABSENT**, and the binding is declaration-only. That is a limit the
artifact states, not one it hides.

**An identity belongs to the render that printed it.** It is carried on the probe result, never
cached on the runner: a cache lends one render's identity to another, and the candidate-retry path
makes that reachable — a first candidate whose verification reports a fingerprint, a second whose
verification reports none, and the second inheriting the first's (reviewed 2026-09-14; viewprobe
lets a refused resolve carry on rendering, so it is not hypothetical). Absence is the default,
because a caller that does not have a render's own identity must not gain one by omission. Each
candidate attempt records the identity its own render reported, and the plan exports the
verification's scene verbatim rather than recomputing a second opinion about one render.

A query that was never answered is not a pass: a silent `--nav-at` or `--inside-at` now refuses
the point by name rather than filing it as a note.

##### Subjects that do not write depth (`--subject-nodepth`)

An effect plane, a light beam or an alpha card writes no depth, so the depth ray never stops on it
and a `--subject` there reads UNPROVEN however clear the view is. `--subject-nodepth X,Y[,Z][=label]`
places and frames it exactly as `--subject` does and changes only what the aimed ray may conclude:

| depth hit `h` vs subject distance `d` | verdict |
|---|---|
| `h ≥ d − --sight-margin`, or no depth-writing hit at all | **framed** — in the delivered frame with nothing opaque before it |
| `h < d − --sight-margin` | **blocked** |

`framed` passes like `clear`, and says what it does not establish: the subject's own surface (the
renderer draws effect shapes only with `--fx`), and any alpha-blended geometry in front of it. It is
never reported as `clear` or `confirmed`.

##### Planning through an unreadable asset (`--allow-unreadable-assets`)

By default any viewprobe exit other than 0 stops the planner, exit 3 included. With
`--allow-unreadable-assets` the planner passes the same flag to every viewprobe call, reads the
census, and judges each hole against the pose being verified: every placement of an unreadable
mesh must keep `--unreadable-margin` (default 128 u) between its bounding sphere and the eye, and
between it and every eye-to-target segment — the depth probe cannot see what a mesh it never drew
would hide. A mesh with no listed placement, an unmeasured radius or a failed archive extraction
cannot be placed and refuses the pose. An unreadable texture leaves its geometry drawn (white), so
it is named, not judged. An accepted pose carries `incomplete` in its verification, the plan JSON
and the worklist shot; the shot's label, which becomes the rendered file's name, starts
`INCOMPLETE (not drawn: <file>, ...) -- `; and the render instruction carries the flag. The frame
itself says so too: viewprobe writes a PNG tEXt entry keyed `viewprobe-incomplete` naming every
asset found and unreadable in that shot and whether exit 3 was suppressed. Nothing is
drawn or claimed for a missing asset. An exit 0 whose census lists unreadable assets without this
flag (an ambient `VP_ALLOW_UNREADABLE_ASSETS`) is refused. Meshes viewprobe reports as not drawn by
the renderer's rule are listed as `notDrawnByRule` and never fail a pose.

##### When every eye-level camera is refused: the top-down fallback (`--no-fallback` to drop)

A refusal used to leave nothing to look at: the claim stub at the first two refusal points
(sightline + framing, placement) and a shot-less refused worklist at the third (the final
verification). Whoever read the refusal then had to guess at the geometry. Since 2026-09-30 the
planner emits, by default, ONE sliced top-down shot of the subjects instead. **The refusal is still
the answer:** exit 1, `status: refused` in the worklist and the JSON, the base refusal message
printed, and the reasons recorded. The shot is never offered as a camera.

- **How it is marked.** The worklist carries `fallback: "top-down"` and a `refusal` record:
  - `stage`: `sightline+framing` | `placement` | `verification`;
  - `kind`: `geometry` or `evidence incomplete`. An evidence-incomplete refusal lists its `causes`,
    for example `unreadable inside coverage`, `missing coverage statement`, `legacy comparison arm`,
    `floor support unresolved` or `unreadable asset near the camera or a sightline`;
  - `reasons`, as the console prints them, with counts;
  - at the verification stage, the refused camera's own `poseId` and `verification`, verbatim.

  The shot's `status` is `fallback`. Its label starts `FALLBACK TOP-DOWN (eye-level refused at
  <stage>; not an eye-level view)`, and its `note` repeats the kind and causes. It has **no
  `feet`**, and `shotplan.py devbench` refuses it by name: it is a picture of the geometry around
  a refused plan, not a player viewpoint. The `--json` result adds `refusedAt`, `refusal`,
  `fallback`, `fallbackDetail` and `renderFlags` to the refused plan's usual keys. A verified plan
  carries none of them.
- **The pose.** Yaw 0, pitch 89, north-up: viewprobe's worklist validator accepts -89..89. The eye
  is set back 1° south, so the view axis meets the subjects' floor at their centroid. The frame is
  `--fallback-extent` wide at the floor (default 2048 u), grown to the subjects' spread + 1024 u.
  The eye height follows from `--fov`.
- **The cut.** The shot's `cullAbove` defaults to the highest subject's z + `--subject-height`
  + 64. `--fallback-cut Z` sets it absolutely; a caller that knows the object's world-box top
  should pass it. `cullAboveBasis` records which was used. The usual `--cull-above` semantics
  apply: statics wholly above the cut go, spanning statics stay, and terrain, navmesh, actors and
  overlays are never sliced.
- **The navmesh band.** One probe render at the fallback pose carries `--nav-at` on a 9 × 9 grid
  over the frame, plus each subject's XY. Only drawn triangles count (flag 0x08 off: the ones
  `--nav-z` keeps).
  - If one lies within ±64 u of the subjects' floor, the render flags carry `--nav-z <floor>,64`
    and the shot draws the overlay as `wire`. That band cannot hit `--nav-z`'s empty-band refusal.
  - Otherwise the overlay is `off`, and `fallbackDetail.storeysSampled` lists the walkable heights
    found in the frame. Another storey's band is never substituted, because the grid is ruled on
    the `--nav-z` plane first and would then sit on the wrong storey.
- **The grid and the subjects.**
  - The render flags carry `--grid auto`, ruled on the subjects' floor.
  - Each subject is ringed by a measured `routes[]` path at its own z (magenta, captioned
    `SUBJECT <label> at x,y,z (ring)`).
  - `fallbackDetail.subjectPixels` gives each subject's pixel in the delivered frame.
  - Like any route, the ring is classified per run as visible / subterranean / behind geometry. In
    the first real fallback render, the ring of a marker in an empty void, all four runs were
    visible. How a ring under geometry below the cut looks is not yet observed.
- **From above.** The same probe picks each subject's pixel through the cut, and
  `fallbackDetail.fromAbove` records `clear` / `blocked` / `unproven` with the hit. This is
  evidence about what covers the subject below the cut, not a verification. If the probe fails,
  the shot is still written with the overlay off, and `fallbackDetail.probe` carries the failure.
- **Rendering it.** The worklist cannot carry `--nav-z` or `--grid` per shot, so they are in its
  top-level `renderFlags`, and the printed command includes them:
  `viewprobe --worklist <wl> <renderFlags> <the planner's pass-through flags>`.
- `--no-fallback` restores the refusal artifacts byte for byte. `--emit-refused` at the
  verification stage keeps its own behaviour (the refused shot, labelled REFUSED, no fallback).

##### Declared surface targets (`--surface`)

`--subject` is a thing standing at its FEET, proved by a ray reaching AT LEAST as far as it.
Sometimes what must be exposed is a point ON a face — the lip of a step, a point on a contact
seam, the corner of a table — and for that the rule inverts: the ray has to STOP there. A ray that
runs past a declared point establishes nothing.

```
--subject X,Y[,Z][=label]    extent from --subject-height / --subject-width
--surface X,Y,Z[=label]      context extent from --surface-extent; bounded by --surface-max
```

Repeatable and mixable. A negative coordinate needs the `--surface=-852,...` form, or argparse
reads the leading minus as a flag. **The points are supplied by the caller from its own source
evidence** — this tool reads no meshes and discovers no features. With only surface targets,
`--floor-z` is REQUIRED: a surface point's Z is a feature height, not a storey, and taking it for
the floor puts the probe eye above the feature and aims the camera at the ceiling.

Four verdicts, where a space subject has two:

| depth hit `h` vs declared distance `d` | verdict |
|---|---|
| within `tol(d)` | **confirmed** |
| `h < d − tol` | **blocked** — something is in front of it |
| `h > d + tol` | **not-surface** — the ray ran past it; nothing opaque is there. Either the point is not where it was declared, or the face is alpha-blended. Unproven, NOT clear |
| no hit | **unproven** — no depth-writing geometry; alpha reads exactly this way |

`tol(d) = --surface-tol + d × (half_pixel + pose_round) × --surface-obliquity` — derived from the
instrument, not from a scene: a pick samples a pixel CENTRE (`fov/size/2`), the pose is exported
rounded to 0.1°, and a lateral miss on a face tilted `t` reads as a range error of `L·tan(t)`.
`--surface-tol` (2 u) covers this renderer printing hit positions and distances as whole units.
Around 3 u at 250–280 u on a 512px canvas at fov 65. That is the width of the band in which
"stopped here" and "stopped at the declared point" cannot be told apart — **not** a claim that a
3 u feature can be resolved.

**An eye-height clearance map cannot certify a feature at another height.** A step's lip hides
behind a chest the eye-height ray flies over; a lintel is clear while the eye-height ray hits the
wall beneath it. So for a surface target the map RANKS — it moves the score, never the verdict —
and the aimed in-pose probe decides. For a space subject the map still refuses as before.

**Containment and sightline are reported separately.** A target can be confirmed on its ray and
still be outside the picture. Containment is computed against the DELIVERED frame (`--fov` by
`--aspect`); the probe's own canvas is square, so the probe frustum may be widened (capped by
`--verify-fov-max`) to reach an off-axis pixel. Widening the probe does not move the camera and
does not change what the delivered frame contains, and the report prints both.

The baseline offline regression is `tools/test_shotplan_plan.py`, which drives the real CLI
against `tools/fake_viewprobe.py` — a stand-in that replays this tool's own line formats over a
declared toy scene, so the real parsers, scoring, verification and writers all run. Its scenes are
synthetic known answers and are evidence about the planner, never about a cell.

**The navmesh test is deliberately not "must be on navmesh".** Navmesh is cut out around furniture,
and a measured good vantage stood on a bed footprint with its nearest triangle 80 u away. What IS a
refusal is navmesh present only at a storey far from the camera's own floor: a point whose only
walkable level sits 288 u above it is under another floor, not in the room. `--storey-tol` and
`--offmesh-tol` set the two bands.

**Which viewprobe it launches.** `--vp` always wins: give it any command that takes viewprobe's
arguments. With no `--vp` the planner prefers the **built executable** under
`bin/Release/*/viewprobe.exe`, because a planning pass makes many invocations and `dotnet run`
pays a build check for each one. It **refuses** — with the reason on stderr and a pointer to
`--vp` — when there is no built executable, or when a source file is newer than it. It never falls
back to `dotnet run -c Release`, because that builds into `bin/Release`, which is the production
viewprobe. That staleness
question covers every project in this repository the executable is built from (the probe, the
shared harness sources, the core library); build output under `bin/` is not a source. The rendering
library is a project reference into another repository, so nothing here can tell whether the
executable predates a change in it, and the refusal says so rather than leaving it assumed.
The notice goes to stderr, where the per-invocation echo already goes, so a caller parsing stdout
is unaffected.

```bash
# plan
python tools/shotplan.py plan SomeInteriorCell \
  --subject 1765.3,1897.5,1424=first --subject 1768.9,1697.2,1424=second \
  --worklist shots.json -- --instance "..." --profile "..."

# judge a camera you already have -- the positive control. A planner that never refuses
# anything is not validated, so point it at one you know is bad before trusting one it chose.
python tools/shotplan.py check SomeInteriorCell --eye 1770,1380,1560 --show-refs \
  --subject 1765.3,1897.5,1424=first -- --instance "..." --profile "..."

# a worklist IS a shot list, so it is also a camera script for a running game
python tools/shotplan.py devbench shots.json
```

**Plan against the frame you are going to take, not against this one.** `--fov` is the VERTICAL
angle and `--aspect` (default `1:1`, this renderer's square frame) widens the horizontal limit.
A game camera is neither square nor at your chosen FOV -- its field of view is the player's own
`.ini` setting. At the SE default the game frame is about 50.5 deg vertical against a plan's
65 deg square: materially narrower, and a shot the planner calls comfortable can fit with
nothing to spare. This is a silent cropping hazard -- the numbers stay right while a subject's
head leaves the picture.

**The setting is the HORIZONTAL angle at a fixed 16:9 base**, so the vertical angle is fixed by
the setting alone and only the horizontal one moves with the frame shape:

```
vertical   = 2 * atan( tan(setting/2) / (16/9) )        # independent of the output aspect
horizontal = 2 * atan( tan(vertical/2) * width/height ) # equals the setting only at 16:9
```

Measured 2026-09-09 across three settings, two output aspects and both camera modes; worst
residual 0.032 deg. It is a RULE: a changed setting or resolution is a recalculation, not a
re-measurement. Two earlier readings died here and are worth knowing about, because both failed
the same way -- a single output aspect cannot identify the base aspect. "Horizontal at a 4:3
base" survived a 2026-09-01 check only because it was paired with a wrong assumed setting (65
where the game held 80) and the errors cancelled; "horizontal at the frame's own aspect" fits
every 16:9 frame to 0.005 deg and is refuted at 1600x1200 by 14 deg. The evidence, the engine
default, the ini precedence and the method for measuring another runtime are in the
[camera contract](../../.claude/skills/devbench-screenshots/references/camera-and-capture.md#the-projection-rule),
which is the authoritative home for all of it.

**Read the target frame; do not assume it.** `frame --instance ... --profile ...` resolves
which ini files an MO2 profile actually hands the game -- its own `settings.ini`
`LocalSettings` decides between the profile's copies and the ones under Documents -- reads
`iSize W`/`iSize H` and `fDefaultWorldFOV`, and prints the `--fov`/`--aspect` to plan with.
`--from-profile` on `plan` or `check` applies them directly, reusing the instance and profile
already given as target flags; `--explain` adds the derivation and the method for measuring the
angle on a runtime where it is not established. Any key defined in more than one file is
reported with every value, because a value set twice is exactly where a single answer would be
confidently wrong -- and the winner is the FIRST file in load order, measured, not the last.
With the key in no file at all the engine's own default is used, reported with its provenance
(80 on SE 1.7.104, not the widely repeated 65, which is LE's); `--fov-setting` overrides it for
a runtime where that default is not established.

**The `camera get` readback is not the commanded angles.** `shotplan.py devbench` prints the
`camYaw`/`camPitch` a driven pose should report, so a driver can assert the pose without
comparing a readback to a command -- which would call a correct yaw-90 pitch-30 pose 30 degrees
wrong. The contract, and the engine source it comes from, are in the
[camera contract](../../.claude/skills/devbench-screenshots/references/camera-and-capture.md#the-commandreadback-contract).

**Two heights, not one.** `--aim-height` is where the camera POINTS (composition); `--sight-height`
is where the sightline probes AIM, and it defaults to head height on purpose. A ray fired at the
middle of a SEATED actor hits the chair it is sitting in and reports the chair as an obstruction --
measured 2026-08-31, and reproduced here at 244 u against a 274 u subject the moment the two were
coupled. Compose low if the shot wants it; probe at head height regardless.

**Feed it RUNTIME positions when a game is available.** An actor is placed at its governing AI
package's anchor on cell load, not at its placement record — one measured NPC stood 1,617 u from
hers. `--subject-from-game FORMID[=label]` reads a subject's position off the running game
(DevBench `inspect kind=refs`; a runtime `0x........` id or a FormKey resolved through the game's
own load index) and REFUSES, naming where the port comes from, when no game answers. A movable
item whose host was taken away has dropped by the time the cell is loaded (2026-09-03, a chitin's
frame found a barrel), so the rule is: **plan a movable subject from the game, after the load
read.** `--subject` still takes coordinates from anywhere else.

**Three things the GAME decides at runtime, and what the planner does about each** (all three put
a wrong thing in a planned frame on 2026-09-03). A **door leaf standing open** across the clear
bearing — the records say closed: every drawn Door ref within `--door-radius` (300 u, the radius
that fixed the frame) of a subject goes into the worklist shot as `doors`, printed under the
chosen pose, for the runner to close before the shot. A **camera inside furniture** — navmesh
runs up to a bookcase, the placement test allows a camera off-mesh, and the depth probe from a
knee-height eye looks out through the case's open back: pass 3 now carries `--inside-at` for
every survivor, and an eye whose nominating ref has geometry within `--inside-reach` (96 u) on
`--inside-rays` (3) of the six axes is refused as ENCLOSED, by name; `check` prints the same
verdict for a camera you already have. The nominating box alone cannot refuse (a kit wall piece's
box encloses the room it walls), so a ref whose mesh cannot be read falls back to the box with
`--inside-cap` and says so. The **moved subject** is the paragraph above. And one thing the planner
could always have known: **plan against the ARM's profile.** Both bookcase cameras of 2026-09-03
were planned on the patch-ON profile for patch-OFF frames; the bookcase the camera sat in is one
the patch disables, so the ON records did not contain it and no test on them could have refused
the eye. Against the OFF profile the same eyes read ENCLOSED on 5 and 4 of 6 axes within 14 u.

**What it cannot see is what `--pick` cannot see:** the transparent pass. Alpha-blended geometry
draws with depth writes off, so a camera can be sited behind a curtain and reported clear. The
consequence is a bad picture rather than a wrong verdict — which is what looking at the frame is
for.

#### Shell quoting, on paths with spaces

Instance and game paths routinely contain spaces. Do not collect them into one shell variable and
expand it unquoted — word-splitting silently hands `--instance` a fragment and the run falls back
to the default instance, producing a plausible-looking render of the *wrong load order*. It is
only detectable by noticing that flags you passed had no effect. Pass such paths as individually
quoted arguments.

**Historical note, and what finally closed it.** Several of those six failures had one root cause:
viewprobe drew every rotated ref with its heading MIRRORED, because Skyrim REFR angles are
clockwise-positive while `Matrix4x4.CreateRotation*` is right-handed counter-clockwise-positive.
The SIGN was fixed in viewprobe first (commit `99e60ea`), caught by annotating a render — an X
pattern between walkway and navmesh, and a mirrored elevation bulge. The six-order `--euler` test
missed it entirely because all six orders shared the wrong sign; a test that varies one parameter
cannot find a bug in a term common to every variant.

The sign was only half of it. The composition ORDER was still reversed, and that half survived the
first fix because the Falkreath walkway it was calibrated on is yaw-only — the case where order
provably cannot matter. It took a scene with genuinely tilted refs to expose (2026-08-30, the
College of Winterhold domes), and it was then settled by DERIVATION from CommonLibVR's own
`NiMatrix3::SetEulerAnglesXYZ` rather than by another calibration. See *Conventions that bite* and
*Fixed defects worth remembering*. `SpatialOverlap.ToWorld`, `blockprobe` and `navprobe` carried the
old composition until 2026-09-01, when the one-line fix (`EulerZyx` → `RefrRotation`) was taken with its
re-baseline: crossprobe pins the transform with four hand-computed boxes (a centred box under a single yaw
agrees under both compositions — the case that hid the bug — an off-centre box under a plain yaw proves
the sign, a two-axis box proves the order), and the detectors' floors were re-measured the same day. The
old matrix was the TRANSPOSE of the engine's, so the flip moved 61 % of the detectors' candidate boxes,
yaw-only refs included, and cut the vanilla navmesh floor five-fold. CLOSED.

---

## Fixed defects worth remembering

**Euler composition order was reversed, so every TILTED ref was flung off its placement**
(fixed 2026-08-30). Reported as four separate symptoms at the College of Winterhold — a large tower
with a hole in it, turret roofs missing, a gatehouse roof missing, and arch pieces free-floating in
the sky near the turrets. All four were one bug: refs with more than one non-zero rotation angle
were composed X-first instead of Z-first, rotating them by a median 75–122° away from where the
engine puts them. The roofs were never missing; they were lying somewhere else.

Diagnosis worth copying. The claim was settled on DATA before a line changed and before a
re-render: the twelve `XJKCoWArchSlimShort` refs of one dome are a ring, so the correct convention
must make them radially symmetric. Under the engine's composition all twelve lean 45° from vertical
at a CONSTANT +29.6° off inward-radial — one 12-gon segment, the arches spanning corner to corner.
Under the old one the same twelve scatter over −177°..+145°. That is a decisive test that costs one
query and no render, and it names the answer instead of just rejecting a guess.
Verification: same camera, both builds — **5,954 shapes in each**, so the fix moved geometry and
neither added nor dropped any; 33% of pixels changed; the tower ring closed, the stray slabs left
the sky, and the turret domes match the in-game screenshot. Regression bound is algebraic, not
hopeful: single-axis refs are bit-identical (`0.00e+00`), so nothing that was right could move.

**Placed NIF root transforms were composed twice in effect** (fixed 2026-08-29). The shared
`NifMeshBuilder` correctly returns geometry with the full NIF node chain applied for its portrait
renderer, but viewprobe fed that root-composed geometry into REFR placement. Skyrim places world
objects from the geometry below the root. On the Temple proof case this turned
`SHouseIntDoor02`'s −90° root plus its 270° REFR heading into an edge-on door, rotated modular
architecture away from its frame, exposed back faces that normal culling removed, and opened
floor bands. viewprobe now inverses the root transform before applying the REFR transform. Three
game captures close the claim: the original wolf-head camera now has the solid left pier,
continuous floor and framed door; a south-side camera retains the wall's authored front; and the
junction camera now sees the same closed door head-on as the game. Broad two-sided rendering was
diagnostic only and is not part of the fix.

**The compare stitch was darkening every panel.** `--compare` writes three panel PNGs and then
composites them onto a black canvas. ImageSharp honours the source alpha, and the panels' alpha was
the shader's, not 255 — most of the foreground came back around alpha 50, alpha-blended canopy at 0.
The stitched file (the one anyone actually looks at) therefore rendered ~38% of its pixels
multiplied down toward black, and nothing in the console said so. Fixed by forcing alpha to 255
after readback in both render paths. The check that catches a relapse is check 3 above.

**`DstBlendIndex` was never copied to `GlMesh`** — see *Effect geometry*. Every additive shape in
the game was composited as ordinary alpha blending; invisible on solid geometry, fatal for a flame.

**The marker name heuristics swallowed real geometry** (fixed 2026-08-28). "Marker" in an EditorID
or model path classified the base as an editor marker, which silently dropped every
`Markers\BlackPlane01.nif` ref despite its clear lack of a marker flag and its real drawable geometry.
Replacing the name rule with the Is-Marker header flag is still correct. The accompanying causal
story was not: the in-game Temple floor has no black seam bands, and the holes vanished only when
placed NIF root transforms were corrected. Likewise, enable-parent evaluation showed what the
records imply but did not prove the state of all dropped refs in the running game. See *Markers*
and *Runtime enable state* above for the narrower claims that the evidence supports.

**Only the target cell's objects loaded** (fixed 2026-08-28). Neighbouring exterior cells
contributed terrain and nothing else, so eye-level shots showed near objects, void, then distant
ring terrain. The target's grid is now resolved in a pre-pass and the main walk admits the ±1
ring's refs. See *Ring refs* above for scoping (navmesh/overlay default to target-only) and the
`--no-ring-refs` opt-out. Re-baseline any byte-exact reference renders after picking this fix up —
exterior scenes gain geometry, and a camera composed before it may now sit behind a neighbour
cell's structure.

---

- **A worklist shot's `nav` / `cullAbove` leaked into the next shot** (fixed 2026-09-02). Both were
  set from the shot into the run-wide variables and never cleared, so an eye-level shot that stated
  neither inherited the previous top-down's dollhouse slice: no ceiling, and the overhead beam the
  shot existed to show reduced to a plank against sky. Every worklist shot now starts from the
  command line's values and applies its own overrides on top.

- **A ref a later plugin moved to another cell was drawn at the earlier plugin's position**
  (fixed 2026-09-27). Admission was decided per sighting: the walk wrote a ref's version when its cell
  was the target or a ring cell, and nothing removed it when a later plugin filed the same ref outside
  the loaded cells (or made a ring ref persistent, or a persistent ref temporary elsewhere). The stale
  version was drawn at its old position, still labelled "changed by" the later plugin, with no warning.
  Found in an offline occlusion assessment, where occlusion panels a patch moved into another cell were
  still drawn and scored where the earlier plugin had them. Fixed by the last-sighting rule above.
  Renders before the fix may show such refs at a prior position; re-render a view whose conclusion
  rests on a ref a later plugin moved between cells.

## Known rough edges (candidate work)

- Terrain beyond the ±1-cell ring doesn't exist → distant hills show as sky or the off-white
  fallback at frame edges. Ring radius is hardcoded. Small worldspaces show bare sky where the
  worldspace itself ends.
- The off-white fallback is keyed on "no base texture resolved", not on distance, so a near-field
  untextured quadrant reads as a bright slab in the foreground.
- The behind-geometry test still carries a little slack, so a navmesh a unit or two behind a surface
  is attributed to the wrong dash CATEGORY. With both categories dashed (the default) that has no
  visual effect at all — but it does show up in the counts, and it decides what a single
  `--no-nav-*` toggle suppresses.
- Painted layers composite as N separately-lit transparent sheets, not as one weighted blend the way
  the engine does it. Where four or five overlap the result washes out. Baking the layers into one
  per-cell texture on the CPU would fix that and remove the MSAA non-determinism with it.
- Layer sheets are coplanar with the base and rely on LEQUAL depth; not z-fight-proof if the base
  and layer vertices are ever computed differently.
- Terrain vertex colours (LAND `VertexColors`) are read by the API but not applied. The 256 u/repeat
  tiling is an eyeballed constant.
- `--nav both` drops the fill to alpha 0.16, which is present in the data but invisible in the
  picture: measured against `--nav wire` it differs on ~31% of pixels by at most 6/255. Effectively
  a synonym for `wire`.
- Flat lighting rig (key + fill + ambient); no shadows, no image-based anything. No grass (a runtime
  system, arguably correct to omit).
- **Water reads DARK from directly overhead, and that is the honest answer, not a bug.** With no
  reflection buffer the sky term is Fresnel-weighted off WATR's own `WaterFresnel` (0.07 for vanilla
  water), so a top-down camera gets almost no sky and sees the record's `DeepColor` — near-black
  teal. The game looks brighter there because it reflects a lit sky and adds sun sparkle. Do not
  "fix" this by raising the sky mix: that is exactly what made the first version a grey-green film.

### Water (`--no-water` to drop)

One grid per loaded cell that has water, at that cell's own surface height, depth-tested and never
depth-writing. A GRID, not a quad: the shallow→deep colour is a function of the real distance down
to the LAND underneath, so a shoreline needs vertices along it to shade against.

Height follows the engine's precedence — `CELL.WaterHeight` when the cell states one, the
worldspace's `LandDefaults.DefaultWaterHeight` when it holds the `3.4028235E+38` sentinel (Tamriel:
−14000, confirmed identical in vanilla `Skyrim.esm` and in the load-order winner). Since 2026-09-04
`--water-arm truth` draws the surfaces from the masters' tuple instead of the winner's, and the console
prints both arms per cell with the share of LAND under each — see *Landscape seams and the water arms*.

**The sentinels, as the game reads them (measured 2026-09-04, `DetectorModel.SurfaceHeight`).** The
"anything outside the world is a sentinel, use the default" rule this section carried since 2026-08-27
was half right. In game the value behaves like a saturating float→int32 conversion: `3.4028235E+38`
and Winterhold's `4.2949532E+09` both mean the worldspace default (a swimmer in a 4.29e9 cell sits at
the default's depth), while `−2.1474836E+09` and Blackreach's `−3.4028235E+38` mean **no surface at
all** — the player stands on the floor of such a cell, and `Cell.GetActualWaterLevel` reads int-min.
The render now draws no plane for the negative sentinels (Blackreach's cells were drawn at the
worldspace default before; they are not water) and the default for the positive ones. `VP_WATER_Z`
still forces a plane anywhere.

**A missing sea is almost never a wrong height, and there is now a number that proves which it is.**
The console reports what percentage of the water footprint has LAND *below* the surface. A water
plane at the wrong height is INVISIBLE rather than wrong-looking — terrain simply covers it — so
"9 surfaces drawn" is not evidence that one pixel of water is visible. Winterhold reports **18.8%**
submerged and Riverwood **12.6%**: both are real seas/rivers, and when they first looked absent the
cause was appearance, not placement. That was worth a full round trip to establish (2026-08-30):
the height resolution was blamed first, the College cell's XCLW was walked down all 14 plugins that
touch it — every one, vanilla included, reads the same sentinel — and the LAND decode was
cross-checked against navmesh (LAND 618 vs nav 626 at the same point, 8 u apart, as it should be).
Both were right; the water was simply too faint to see. `VP_WATER_DUMP=1` and `VP_WATER_Z=<z>` are
the two knobs for redoing that separation quickly.

The noise layer is bound as a **wave NORMAL map**, and that is a schema fact rather than a guess
about the art: WATR carries no diffuse texture field at all — only `NoiseLayerOne/Two/ThreeTexture`
and `FlowNormalsNoiseTexture` — so colour has to come from the record's own `ShallowColor`/
`DeepColor` and the noise layers are normals. Binding one as a diffuse would repaint the sea in
normal-map lilac, the exact defect class this renderer already carries a filter for.

**The surface is OPAQUE, because that is what the engine draws.** Settled against the game's own
water shader (Community Shaders, `package/Shaders/Water.hlsl`) rather than reasoned about, after
guesswork got this wrong twice. Three facts from that source, each killing a plausible assumption:

- `psout.Lighting = float4(finalColor, isSpecular);` — **the alpha channel carries a specular flag,
  not opacity.** Skyrim's water is an opaque draw. It does not alpha-blend.
- What looks like transparency happens INSIDE the shader:
  `lerp(refractionColor, finalColorPreFog, refractionMul)` against a refraction sample. Framebuffer
  blending is the wrong instrument; it is used here only because this renderer has no refraction
  buffer, and a blend IS that same lerp with the framebuffer standing in for the sample.
- `VarAmounts` is documented in its own cbuffer as *"alpha in z"*, and that value is a **floor, not
  a level**: `distanceMul = saturate(lerp(VarAmounts.z, 1, -(distanceFactor - 1)))` starts at the
  record's alpha and rises to **1 — fully opaque**. So `WATR.Opacity` is the most transparent water
  ever gets, not how transparent it is. Reading it as a flat 50% alpha made an ocean out of
  tinted air.

Depth information is not lost to opacity — it rides the COLOUR ramp, which is the shader's own
`lerp(ShallowColor, DeepColor, distanceMul)`. What IS lost is seeing the bed through a ford, and
that is a refraction effect this renderer cannot do honestly.
`VP_WATER_SHALLOW_ALPHA=<0..1>` puts a see-through shore margin back for a plan that wants one.

Honest divergence, stated rather than buried: in the engine the driving factor is camera distance
and fog, not depth to the bottom. Depth is used here because this renders floor plans and site
pictures, where "shallow reads lighter than deep" is the readable, camera-independent behaviour,
and because a camera-distance ramp would bake into vertex colours and freeze at the entry camera.

Opaque water joins the OPAQUE pass and writes depth. Not tidiness — it fixed a layering bug the
opaque default exposed: terrain paint layers ride the DECAL channel, and decals draw AFTER the
distance-sorted blend set, so while water was blended the roads and ground blends composited
straight over the river as quadrant-shaped rectangles of differing tint. (Consequence worth knowing:
`--pick` over water now reports the water surface, where a blended one used to report the bed.)

Colour is `lerp(ShallowColor, DeepColor)` by real depth, plus a sky term reflected off WATR's own
`ReflectionColor` and weighted by **Schlick Fresnel with `WaterFresnel` as F0**. That replaced a flat
`lerp(body, sky, WaterReflectivity)` which mixed 40% pale sky into every vertex regardless of angle:
the water drew correctly and simply did not READ as water — a failure mode worth naming, because it
looks exactly like "no water was drawn". Camera-dependent and therefore baked at the ENTRY camera, like the rest of this
renderer's view-dependent overlay work — exact for a static render, frozen in the fly-through.

Not modelled: no reflection or refraction buffer, no flow, no sun sparkle.

### Projected coverings — MATO (`--no-coverings` to drop)

The snow on Winterhold, the ash on Solstheim, the moss on Markarth. **Not an effect shader**, which
is why no amount of `--fx` ever produced it — the renderer simply did not read the record.

The wiring is narrow, and knowing that bounds the feature: a MATO reaches the world through ONE
field on ONE record type, `STAT.Material`, with `STAT.MaxAngle` beside it. That is verified by
construction against the whole modeled record library — Static is the only type carrying a
`FormLink<IMaterialObjectGetter>` — so coverings land on statics and nothing else, and a bare patch
on a non-STAT is correct rather than a gap.

Each covered shape gets a second shell sharing its triangles, per SHAPE not per ref, because
coverage is a function of each face's normal — one mesh can have a snowed roof and bare undersides
and that difference IS the effect. Coverage is `d = dot(faceNormal, −ProjectionVector)`, hard-gated
by the STAT's `MaxAngle`, then ramped. **The bias/scale ramp is FITTED, not engine-exact**, and the
shape was read off the data's own spread rather than guessed: vanilla snow is bias 0.40, and the
deliberately-thinner variants climb monotonically (Windhelm's `Light` 0.92, `EvenLess` 0.94,
`Minimal` 0.98), so a higher bias means LESS coverage — bias is a threshold on `d`, scale the ramp
width. A single-pass material states its colour in `SinglePassColor`; a multi-pass one leaves it to
the diffuse of its own model NIF's first shape, which is where the snow grain comes from.

A shell is NOT a pick shape of its own — hovering should name the roof, not the snow on it — so it
hangs off its base's `PickShape` as `CoverMesh` and the live height clip toggles both together.
Without that link, clipping a roof away left its snow hanging in the air.

Shells are decals (drawn after the sorted blend set, never writing depth) and lifted 2 u along the
normal, because `IsDecal` keeps geometry out of the depth buffer but does not offset it and exactly
coplanar shells stipple at any real camera range. A shape where nothing catches the projection emits
no shell at all, which is what keeps the pass from doubling the scene: at Winterhold, 2,598 shells
over 5,954 shapes.
- Trees: full-model NIFs render; SpeedTree-style billboard LOD is absent (fine up close).
- The compare stitches panels side by side at full size (wide file); no layout options.
- NPC actors stand in the NIF bind pose — no animation, no furniture use. SPID `FinalOutfit` vs
  regular `Outfit` evaluate in one interleaved first-match list (mirroring NPC2's source-verified
  simulation) rather than as two passes; SleepOutfit is not simulated; a sub-100% SPID chance is
  reported, never rolled. Runtime outfit configs are read loose-only from the reconstructed VFS —
  faithful to both tools, but it means an INI a mod ships only inside a BSA is invisible.
- `--cull-above` is all-three-verts-above, so a triangle STRADDLING the cut survives whole — a
  wall keeps its full height (intended), but so does a mezzanine slab whose skirt dips below the
  cut, which can leave an actor covered at a cut that "should" clear it. The occlusion advisory
  catches exactly this (it fired on a straddling slab at the first cut tried); lower the cut
  toward its suggested value. Navmesh above the cut still draws — slicing annotation layers is a
  judgement the slice deliberately doesn't make.
