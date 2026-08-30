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

- **Instance/profile:** `--instance DIR --profile NAME --game DIR`. **Nothing is compiled in** —
  see *Where it points* below. A run with none of it configured exits with guidance rather than
  walking a directory that isn't there.
- **Camera** (Skyrim coordinates; a `getpos`/`getangle` console line pastes straight in):
  `--eye X,Y,Z` | `--eye-ref FORMID` (+`--eye-height`, default 120), `--look X,Y,Z` |
  `--look-ref FORMID` | `--yaw DEG` (0 = north/+Y, clockwise) + `--pitch`, `--fov` (default 65).
  Textured mode requires an eye — with none given, the flat diagnostic mode runs instead.
- **Navmesh:** `--nav off|filled|wire|both` (defaults to `wire` when `--conflicts`/`--red` is
  given), `--nav-color auto|green|cyan|magenta|yellow|blue` (default auto).
- **Cell outline** (on by default, exteriors only): a WHITE DASHED line along the target cell's four
  edges, riding the terrain, always visible. The render covers the target cell AND its ring of
  neighbours, and nothing else in the picture says where the cell under examination stops.
  `--no-cell-outline` drops it — worth doing for eye-level shots, where it cuts across geometry at
  head height and reads as part of a building.
- **Ring refs** (on by default, exteriors only): placed refs load for the target cell AND its ±1
  ring, matching the terrain. Before this, neighbours contributed terrain and nothing else, so any
  eye-level shot read near objects → void → bare ring terrain, with everything between absent
  (fixed 2026-08-28; a building 700 u away in the next cell simply never loaded). Navmesh and the conflict
  overlay stay TARGET-CELL-ONLY — they are the annotation layer for the cell under examination,
  scoped like the cell outline — but implicated DOORS standing in ring cells now render and
  highlight, and the "implicated door is NOT in the rendered cell(s)" message fires only for doors
  beyond the whole loaded set. `--no-ring-refs` returns to target-cell-only refs (cheaper — a
  dense ring can be ~8× the shapes). Interiors have no ring.
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
  view.
- **Terrain:** `--no-terrain` (off entirely), `--no-terrain-layers` (base layer only — no painted
  alpha layers and no base stand-ins).
- **Water and coverings** (both ON by default — their ABSENCE was the bug, not their look):
  `--no-water` drops the water surfaces, `--no-coverings` drops the MATO projected snow/ash/moss.
  Both change every exterior render, so both announce themselves on the console. See *Water* and
  *Projected coverings* under *Known rough edges* — **the water HEIGHT is a known open defect.**
- **Scene filters:** `--fx` / `--fx-static` (see *Effect geometry*), `--markers` (translucent
  editor markers), `--list-meshes` (per-mesh census with its WHOLE-FILE class, `FX-file` vs
  `geometry`), `--out DIR`, `--size N` (default 2560; square, and the 3D render resolution as well
  as the PNG's — there is no downsample step, so `--size` IS the sharpness knob, with `--msaa` for
  the edges).
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

- **Walking route** (`--route X,Y`, repeatable): the path to WALK from this camera, drawn as
  straight segments with arrowheads pointing waypoint to waypoint. **X and Y only** — z is
  resolved from the winning navmesh. `--route-z Z` states the floor the route runs on (which
  storey), `--route-color NAME|R,G,B` overrides the reserved colour, `--route-arrows auto|end|N`
  sets the arrowhead spacing, `--no-route-discs` drops the fill disc at each waypoint. A worklist
  shot's own `route` is the same thing and overrides these. **An off-mesh waypoint is a hard
  error**, and so is a leg that leaves walkable ground BETWEEN its waypoints. Its own section:
  *Walking routes* below.
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
| `--refs-at X,Y[,R]` | every winning reference within R (default 256) of a world X,Y, each with its base, mesh, and whether the render draws it (and if not, why not) |
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
| `VP_WATER_Z=<z>` | force every water surface to one height. The other half of the pair above: if the surface appears here and not at the resolved height, the renderer is fine and the height is wrong |
| `VP_FXSTATIC_EMIT=<float>` | taste multiplier on `--fx-static` brightness (default 1 = the authored `baseColorScale`). Below ~0.25 the fire stops reading; above ~2 it blows out |
| `VP_ALPHA_DUMP=gx,gy` | every alpha layer of that cell as a 17×17 ASCII opacity grid |
| `VP_LAYER_ONLY=<substr>` + `VP_LAYER_SOLID=1` | draw only the painted layers whose diffuse matches, at full opacity — isolates one layer so "is that wash really this texture?" has an answer |
| `VP_OCCL_SLACK="rel,abs"` | override the behind-geometry depth-compare slack (default `1.002,1.5`) |
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
| `VP_FLY_HUD=1` | include the HUD bar in the capture. `F12` deliberately excludes it, so this is the only way a test can see the bar — at the cost of the burned caption overlapping its first line |

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
  Temple's pier and continuous floor while ordinary back-face culling remains enabled.
- **Persistent refs.** Exterior doors live in the worldspace persistent cell, which **masquerades as
  grid (0,0)** — they cannot be identified by cell record (and that masquerade is why the ring test
  admits only non-persistent refs from neighbour cells). viewprobe collects refs by their own
  `Persistent` flag and merges them into the scene by position against the whole ref-loaded area's
  bounds — the 3×3 ring by default, the target cell alone under `--no-ring-refs`.
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
- **Interactive fly-through** (`--interactive`): the same scene in a visible window — WASD +
  mouse flight, hover-to-identify (refs, navmesh, terrain) with a click-to-lock readout, live
  navmesh display mode, a live height clip, pose echo, F12 capture. Its own section below.
- **Walking routes** (`--route`): waypoints in plan view become arrows on the ground, at heights
  the winning navmesh supplies, with every waypoint clearance-checked and every leg between them
  sampled onto the mesh. Pairs with a follower harness — that puts an actor behind you, this says
  where to walk them. Its own section below.
- **Worklists** (`--worklist`): a shot list drives both the fly-through's `PageUp`/`PageDown`
  paging and, through `--emit-bats`, the in-game console batch files — one set of coordinates, so
  the render and the tester's standing position cannot disagree. Its own section below.
- **Terrain:** winning LAND heightmaps for the target cell ± 1 ring, painted with each quadrant's
  winning base landscape texture (LTEX → TXST → diffuse, winner-takes-all), tiled ~256 u/repeat.
  The heightmaps are kept as a queryable field (`terrainHeightAt`, same 128 u quads and diagonal
  split the mesh is built from) so the overlay can ask what is buried.
- **Painted layers (roads).** A Skyrim road is not a placed mesh — it is a landscape texture
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
  background + reserved red. It projects through `OrbitCamera`'s own matrices — the same ones the
  rasteriser used — so it cannot drift.
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
walking route, `[`/`]` and `;`/`'` move the
height clip, `\` turns it off (all four below). `P` prints the current pose as a paste-ready
`--eye X,Y,Z --yaw N --pitch N --fov N` line. `F12` saves a captioned PNG (window resolution,
standard caption bar + `viewprobe` tEXt chunk with the pose) into `--out`. `PageUp`/`PageDown` step a `--worklist`'s shots and `Ctrl+PageUp`/`PageDown` its cells
(below). `H`/`F1` reprints the controls with their current values; `Esc` quits. Closing the window prints the final pose,
so the last view is never lost.

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

**Read `(N hidden)` for exactly what it is: the LIVE band's count, and nothing else.** It counts
the shapes this band is holding down right now — it says nothing about a build-time
`--cull-above`, whose triangles were dropped before the window opened and which no key can bring
back. A run carrying one now declares it on the bar as `SLICED at build: --cull-above N
(permanent)` (added 2026-08-30), because without that the bar could read `clip: off` over a scene
missing its roofs, and missing roofs read as a finding. `(0 hidden)` means "this band is currently
removing nothing" — never "nothing has been removed".

**Hover identification** (the cursor is free whenever RMB is up; a HUD bar at the top shows the
answer). `LMB` on something **LOCKS** the readout to it and prints the full detail to the
console; the next `LMB` anywhere unlocks, hit or miss. Locked, the identity stays on the bar
while the camera moves — reading a FormKey off a wall you are also trying to fly around meant
holding the cursor perfectly still — and hover picking is suspended, so the readout cannot
drift. A locked line reports the hit POINT and drops the range, which only the camera that did
the picking could speak for. The bar's text is 30 px (twice the burned caption's 15 px: it is
read at a glance, from further back than a saved image is) and WRAPS, growing to fit, because a
mesh path at that size runs off a 1440-wide window:

- a placed ref answers `REFR FormKey = runtime FormID — base EditorID — mesh path`, click adds
  the base FormKey/FormID, placement and scale;
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
the loop, the ray, the display mode, the clip and the HUD headlessly — that is how the feature
is regression-tested, and `VP_FLY_FRAMES` runs are byte-identical between runs.

**What the loop can and cannot change.** Everything live is visibility or input state — the
navmesh display mode, the height band, the flight feel — so none of it rebuilds geometry and
none of it can change what a static re-render of the same pose produces. Verified: a static
render before and after this feature landed is byte-identical, interior (`--nav both
--cull-above`) and exterior (`--nav both --conflicts`, red navmesh + cell outline + terrain
layers) alike.

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
          "yaw": 0, "pitch": 0, "fov": 65,
          "route": [[-26671, -84175], [-26520, -84900], [-26372, -85500]],
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
- **`look: [X,Y,Z]`** may replace `yaw`/`pitch`, resolving the same way `--look` does.
- **`nav`** and **`cullAbove`** are optional per-shot overrides. `nav` applies exactly (it is a
  visibility flip). `cullAbove` in the window drives the SHAPE-granular live band, not the
  triangle-exact knife — the console says so on arrival rather than letting the two quietly differ.
- **`route`** is the path to WALK from this shot — waypoints as `[X, Y]` pairs, at least two, **no
  z**: it is resolved from the winning navmesh against this shot's own `floorZ`, every waypoint is
  clearance-checked and every leg sampled onto the mesh before anything is drawn. Place a waypoint
  only at a direction change over ~10–15° or a sight-line break — see *Walking routes* for why, and
  for what the legs do in between. A shot's `route` overrides a command-line `--route`, and unlike
  `nav`/`cullAbove` it does NOT leak into the next shot.
- **`travel`** on a cell overrides the derived console travel command; normally absent.

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

---

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
*Fixed defects worth remembering*. `SpatialOverlap.ToWorld`, `blockprobe` and `navprobe` still carry
the old composition — now a KNOWN-wrong one with a one-line fix (`EulerZyx` → `RefrRotation`),
deliberately not taken because the detectors' false-positive floor is calibrated against the current
boxes and flipping it wants its own re-baseline. Still OPEN, but no longer unknown.

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
ring's refs. See *Ring refs* above for scoping (navmesh/overlay stay target-only) and the
`--no-ring-refs` opt-out. Re-baseline any byte-exact reference renders after picking this fix up —
exterior scenes gain geometry, and a camera composed before it may now sit behind a neighbour
cell's structure.

---

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
- Terrain quadrant boundaries show as hard rectangles THROUGH translucent water. Pre-existing
  (verified with `--no-water`: the same rectangles are there), and it is the painted-layer/
  untextured-quadrant rough edge listed above, just made more visible by something drawn over it.

### Water (`--no-water` to drop)

One grid per loaded cell that has water, at that cell's own surface height, depth-tested and never
depth-writing. A GRID, not a quad: the shallow→deep colour is a function of the real distance down
to the LAND underneath, so a shoreline needs vertices along it to shade against.

Height follows the engine's precedence — `CELL.WaterHeight` when the cell states one, the
worldspace's `LandDefaults.DefaultWaterHeight` when it holds the `3.4028235E+38` sentinel (Tamriel:
−14000, confirmed identical in vanilla `Skyrim.esm` and in the load-order winner).

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

Colour is `lerp(ShallowColor, DeepColor)` by real depth, plus a sky term reflected off WATR's own
`ReflectionColor` and weighted by **Schlick Fresnel with `WaterFresnel` as F0**. Alpha is
depth-driven: the record's `Opacity` is the SHALLOW end, rising to opaque at the bottom of the
shallow band — fitted, not engine-exact, because WATR states one opacity and the engine's depth
response lives in its shader. Both replaced a flat `lerp(body, sky, WaterReflectivity)` + flat alpha
that mixed 40% pale sky into every vertex regardless of angle: the water drew correctly and simply
did not READ as water, which is a failure mode worth naming because it looks exactly like "no water
was drawn". Camera-dependent and therefore baked at the ENTRY camera, like the rest of this
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
