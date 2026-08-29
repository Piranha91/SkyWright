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
  click-lock, the live navmesh-display and height-clip state, HUD).
- **Rendering library:** `CharacterViewer.Rendering` (project reference, currently an absolute path
  to a sibling checkout). It supplies `NifMeshBuilder` (NIF → Y-up meshes with texture paths),
  `GlRenderer`/`GlMesh`, `GlTextureManager` (DDS load + white fallback), `OrbitCamera`. viewprobe
  wires adapters over houseCARL's `AssetResolver` rather than reinventing them.
- **Build:** `dotnet build -c Release`; run with `dotnet run -c Release --no-build -- <args>`.
  Never edit sources while a background `dotnet run` is compiling — that has produced corrupted
  concurrent builds.
- **Verification helper:** `tools/imgcheck.py` — `crop` / `panel` / `diff` / `hist`. Every visual
  claim in this file was made with it.

---

## CLI reference (first positional arg = CellEditorID or FormID)

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
| `VP_FLY_CLIP=lo,hi` | set the height clip band before the capture; either side may be empty, so `,2048` is a ceiling with no floor |
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
  happens in Skyrim space (same Euler composition as the detectors, `SpatialOverlap.Euler` XYZ) and
  is swapped once at the end. Camera input is Skyrim-space.
- **REFR angles are clockwise-positive** (compass headings; `getangle z`: 0 = north, 90 = east)
  while `Matrix4x4.CreateRotation*` is right-handed counter-clockwise-positive — so the angles must
  be NEGATED before composing, or every rotated ref draws with its heading mirrored about north.
- **Persistent refs.** Exterior doors live in the worldspace persistent cell, which **masquerades as
  grid (0,0)** — they cannot be identified by cell record (and that masquerade is why the ring test
  admits only non-persistent refs from neighbour cells). viewprobe collects refs by their own
  `Persistent` flag and merges them into the scene by position against the whole ref-loaded area's
  bounds — the 3×3 ring by default, the target cell alone under `--no-ring-refs`.
- **Markers** are classified from the record's Is-Marker header flag (`0x00800000`) ONLY — the
  engine's own don't-render rule. Name heuristics (*marker* EditorID or model path) are wrong in
  both directions and were removed 2026-08-28: `Markers\BlackPlane01.nif` is the vanilla
  gap-sealing black plane (record flags 0, real lit geometry — the same job as the light-blocking
  shadow planes above) and dropping it as a marker opens sky-coloured holes at floor seams, while
  the CK-only marker meshes need no name rule at all — they contain zero drawable shapes, or a
  single effect-shader shape the per-shape unshadeable filter already drops.
- **Runtime enable state.** A ref with an XESP enable parent takes the PARENT's state (XOR the
  opposite-of-parent flag), chains followed, the player ref (ID 0x14) always enabled — the ref's
  own initially-disabled flag is CK bookkeeping, not the runtime answer (parent = player +
  opposite is the standard permanently-disabled idiom). Refs the runtime enables are drawn; refs
  it disables are dropped even when their own flag is clear. Every ref the scene drops is
  accounted for in ONE console block (disabled via enable parent / own flag / deleted / no
  model-bearing base) — a silent category is how a render missing a third of its content once
  read as fully accounted for.
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
rate, movement and clip alike. `N` cycles the navmesh display, `[`/`]` and `;`/`'` move the
height clip, `\` turns it off (all three below). `P` prints the current pose as a paste-ready
`--eye X,Y,Z --yaw N --pitch N --fov N` line. `F12` saves a captioned PNG (window resolution,
standard caption bar + `viewprobe` tEXt chunk with the pose) into `--out`. `H`/`F1` reprints
the controls with their current values; `Esc` quits. Closing the window prints the final pose,
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

**Historical note, still open.** Several of those six failures had one root cause: viewprobe drew
every rotated ref with its heading MIRRORED, because Skyrim REFR angles are clockwise-positive while
`Matrix4x4.CreateRotation*` is right-handed counter-clockwise-positive. Fixed in viewprobe (commit
`99e60ea`) — but `SpatialOverlap.ToWorld`, `blockprobe` and `navprobe` still carry the wrong-handed
composition, and recalibrating them is an OPEN item. The six-order `--euler` test missed it entirely
because all six orders shared the wrong sign; it was caught by annotating a render (an X pattern
between walkway and navmesh, and a mirrored elevation bulge). A test that varies one parameter
cannot find a bug in a term common to every variant.

---

## Fixed defects worth remembering

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
`Markers\BlackPlane01.nif` ref — the vanilla black plane mods use to seal floor seams and openings
— so interiors built over a void showed sky-blue bands at every seam, from legal standing
positions. The initially-disabled diagnosis this suggested was a red herring: enable-parent
evaluation (added in the same pass) proved the dropped-as-disabled refs were all *genuinely*
permanently disabled, and the holes were the marker filter's. See *Markers* above for the
evidence-based rule that replaced the heuristics.

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
- Flat lighting rig (key + fill + ambient); no shadows, no image-based anything. Water planes don't
  render as water. No grass (a runtime system, arguably correct to omit).
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
