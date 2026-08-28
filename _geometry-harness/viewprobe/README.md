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
  assembly, compare orchestration), `TexturedScene.cs` (GL rendering, captions, adaptive colour).
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

- **Instance/profile:** `--instance DIR --profile NAME --game DIR`. The compiled-in defaults point
  at the author's own VR instance, so **pass all three explicitly** for any other setup.
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
| `VP_DOOR_DEBUG=1` | print each conflict-implicated door's holding cell during the load walk |
| `VP_CV_VERBOSE=1` | unmute the rendering library's logger |

---

## Conventions that bite

- **Coordinates.** The library is Y-up; viewer = `(x, z, −y)` of Skyrim's Z-up. All placement math
  happens in Skyrim space (same Euler composition as the detectors, `SpatialOverlap.Euler` XYZ) and
  is swapped once at the end. Camera input is Skyrim-space.
- **REFR angles are clockwise-positive** (compass headings; `getangle z`: 0 = north, 90 = east)
  while `Matrix4x4.CreateRotation*` is right-handed counter-clockwise-positive — so the angles must
  be NEGATED before composing, or every rotated ref draws with its heading mirrored about north.
- **Persistent refs.** Exterior doors live in the worldspace persistent cell, which **masquerades as
  grid (0,0)** — they cannot be identified by cell record. viewprobe collects refs by their own
  `Persistent` flag and merges them into the scene by position against the target cell's bounds.
  Anything position-based must remember these exist.
- **Markers** are classified from record evidence (header flag `0x00800000`, `marker` in the
  EditorID or model path); the game never draws them.
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
- **Highlighted refs** keep their own texture and get a three-part orange highlight from one
  centroid-inflated hull (winding normalized geometrically — some door NIFs carry inward normals):
  a solid depth-tested rim where directly visible; a light 0.22-alpha amber wash wherever the hull
  is not frontmost (this is what marks a visible door whose rim hides in a recessed jamb); and a
  deep-occlusion boost that fires only when the occluder is in front of the WHOLE hull — a wall — so
  a hidden door pops as a strong silhouette while a visible one keeps its readable texture.

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
  sit thousands of units below the deck the player walks on (Winterhold College courtyard z ≈ −7750,
  shore below at −12300, tower features at −4600). "Somewhere around ground level" is not a number.
- **Interior top-downs must slot the camera BETWEEN the target floor and the next storey.** Above
  that you render rafters. `--nav-at` returns one row per level, which is how you find the gap
  (Solitude Temple: gallery 1424, next floors 1712 / 2000 / 2288).
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
