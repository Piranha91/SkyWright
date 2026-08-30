using System.Diagnostics;
using System.Numerics;
using HousecarlCore;
using Mutagen.Bethesda;                 // ToImmutableLinkCache over the --npc overlays
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Plugins.Cache;
using Mutagen.Bethesda.Skyrim;
using OpenTK.Graphics.OpenGL4;
using OpenTK.Windowing.Common;
using OpenTK.Windowing.Desktop;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;
// WPF is pulled in by the CharacterViewer.Rendering reference and brings its own Path/Image/Color
// into scope; alias the ones this file means so the collisions cannot silently pick the wrong type.
using Path = System.IO.Path;
using Directory = System.IO.Directory;
using File = System.IO.File;
using Image = SixLabors.ImageSharp.Image;

// viewprobe — render a cell's WINNING geometry with its WINNING navmesh laid over it.
//
// Why this exists. Two sessions of milestone 4 produced percentages nobody could check by eye, and
// two open questions that numbers could not close: whether ToWorld composes Euler angles in the
// order the engine does, and whether a flagged reference is really blocking anything. Both are
// visual questions. This makes them visible.
//
// It is deliberately NOT a new renderer. The vertex layout below is byte-for-byte the one
// CharacterViewer.Rendering's GlMesh.Upload expects — position(3) normal(3) uv(2) colour(4)
// tangent(3) bitangent(3), 18 floats interleaved — so moving to that library's GlRenderer and
// NifMeshBuilder, and getting textures and its lighting rig with them, is a drop-in swap of the
// draw call rather than a reshaping of the data. This pass draws flat-lit geometry because gross
// structure is all the Euler question needs, and it lands tonight.
//
// usage: viewprobe <CellEditorID|FormID> [--out DIR] [--size N] [--smoke]
//                  [--angles a,b,c] [--elev N] [--no-nav] [--ref FORMID]

// WHERE THIS POINTS. Nothing is compiled in — see _shared/HarnessTarget.cs for the resolution
// order (flags, then environment, then harness.local.json). --instance / --game / --profile aim
// the render at a different MO2 layout, which is how a field-test shot shows the exact state the
// tester will see rather than the state of whatever list happens to be configured. Require() runs
// AFTER the flags, so passing --instance never trips it.
// LAUNCHED FROM EXPLORER — a double-click, or a worklist DROPPED ON THE ICON. Registered FIRST,
// ahead of the Require() calls just below, because an unconfigured harness exits from inside those:
// a window that closes on the one message explaining how to configure it has taught nobody
// anything. The console dies with the process on a GUI launch, so every exit path pauses.
bool guiLaunch = GuiLaunch();
if (guiLaunch)
    AppDomain.CurrentDomain.ProcessExit += (_, _) =>
    {
        Console.WriteLine("\n[press any key to close]");
        try { Console.ReadKey(true); } catch { }     // no keyboard to read: close rather than hang
    };

string Instance = HarnessTarget.Instance;

string GamePath = HarnessTarget.Game;
string profileName = HarnessTarget.Profile;
for (int i = 0; i < args.Length; i++)
{
    if (args[i] == "--instance" && i + 1 < args.Length) Instance = args[i + 1];
    else if (args[i] == "--game" && i + 1 < args.Length) GamePath = args[i + 1];
    else if (args[i] == "--profile" && i + 1 < args.Length) profileName = args[i + 1];
}
Instance = HarnessTarget.Require(Instance, "MO2 instance directory", "instance", "--instance");
GamePath = HarnessTarget.Require(GamePath, "game directory", "game", "--game");
profileName = HarnessTarget.Require(profileName, "MO2 profile name", "profile", "--profile");
string profileDir = Path.Combine(Instance, "profiles", profileName);
string modsDir = Path.Combine(Instance, "mods");
string overwriteDir = Path.Combine(Instance, "overwrite");
string dataDir = Path.Combine(GamePath, "Data");

if (args.Length > 0 && args[0] == "--smoke") return Smoke();
if (args.Length == 0)
{
    Console.Error.WriteLine("usage: viewprobe <CellEditorID|FormID> [--out DIR] [--size N] [--angles 0,90,180] [--elev 25] [--no-nav] [--ref FORMID] [--interactive [--fly-speed 600] [--look-sens 0.15]]");
    Console.Error.WriteLine("       viewprobe <worklist.json> [more.json ...]   — or DROP worklists onto this exe to fly through them.");
    return 1;
}

// DROPPED FILES. Explorer hands a drag-and-drop to the exe as bare positional paths with no flag
// anywhere in sight, so a worklist dropped on the icon lands exactly where a CellEditorID is
// expected — and used to die with "cell 'C:\...\tour.json' not found in the active load order".
// A leading positional that is an EXISTING .json FILE is therefore read as a worklist. The test
// cannot misfire: no CellEditorID and no FormID is ever a path to a file on disk. Several may be
// dropped at once, since Explorer passes the whole selection, and their cells concatenate in the
// order given — see WorklistIo.Merge for the one thing that has to agree between them.
var worklistPaths = new List<string>();
int argStart = 0;
while (argStart < args.Length && IsWorklistFile(args[argStart])) worklistPaths.Add(args[argStart++]);

// A run normally opens with the cell to render. --worklist carries its own cells, so when the
// first argument is a FLAG there simply is no positional target and the parse starts at zero.
bool positionalTarget = worklistPaths.Count == 0 && args[0].Length > 0 && args[0][0] != '-';
string target = positionalTarget ? args[0] : "";
if (positionalTarget) argStart = 1;
// Dropping the FOLDER rather than the files in it is the other easy slip, and it used to cost a
// full load-order walk before reporting a missing cell whose name was a directory path. Not swept
// automatically: the .json files sitting next to a worklist are as likely to be conflict emits,
// and one of those would fail the whole run.
if (positionalTarget && Directory.Exists(target))
{
    var inside = Directory.GetFiles(target, "*.json");
    Console.Error.WriteLine($"'{target}' is a folder, not a cell or a worklist. Drop the .json files themselves"
                          + (inside.Length > 0 ? $" — it holds {inside.Length} of them." : " — it holds none."));
    return 1;
}
string outDir = Path.Combine(AppContext.BaseDirectory, "shots");
// Frame size, square, and the 3D render resolution as well as the PNG's — there is no
// downsample step, so this IS the sharpness knob. 2560 because 1280 fell apart under a zoom.
int size = 2560;
float elev = 28f;
bool drawNav = true;
string? highlight = null;
var angles = new List<float> { 35f, 125f };
// First-person textured mode ("see what the tester will see"): real textures via
// CharacterViewer.Rendering, camera INSIDE the cell at eye height, game-style FOV.
// --eye/--eye-ref implies --tex. Coordinates are SKYRIM's (a `getpos` line pastes straight in);
// yaw matches the game's Z angle (0 = north/+Y, clockwise), positive pitch looks down.
bool textured = false;
System.Numerics.Vector3? eye = null, look = null;
string? eyeRef = null, lookRef = null;
float eyeHeight = 120f;                       // standing eye level; the player is 128u tall
float? yaw = null; float pitch = 0f;
float fov = 65f;                              // Skyrim SE's default world FOV
// EFFECT GEOMETRY is filtered in TWO places, because it comes in two shapes:
//   • whole-file — a ref whose model path is under `effects\` (beams, mists, glows). Dropped
//     here, at the ref, before the scene is even assembled.
//   • per-shape — a shape carrying a BSEffectShaderProperty inside an otherwise solid mesh.
//     Dropped in TexturedScene, which is where the NIF is actually read.
// The second is not a refinement of the first, it is the case the first CANNOT express:
// `Clutter\WoodFires\FireplaceWood01Burning.nif` is a single file holding 5 solid shapes (logs,
// coals, ash) and 12 effect shapes (flames, smoke, heat warp), and no path rule can keep the
// logs while dropping the flames. Found 2026-08-28: the flames were drawing as an opaque
// iridescent slab across every lit hearth in the game, because NifMeshBuilder reads material
// values off BSLightingShaderProperty ONLY — an effect-shader shape arrives fully lit and opaque.
// --fx switches BOTH filters off.
bool keepFx = false;
// --fx-static is the middle setting between "drop the fire" and "draw it wrong": effect-shader
// shapes inside otherwise-solid meshes are DRAWN, frozen at the frame the NIF stores, additive and
// never writing depth. It rescues only the effect-shader kind — a normal map in the diffuse slot
// stays skipped, since no blend mode makes that shadeable. It does NOT bring back whole-file
// `effects\` refs either; those are ambient dust beams and mists, which is a different question.
// Off by default: an additive flame still paints over the navmesh behind it, and the navmesh is
// what this tool is for.
bool fxStatic = false;
// Navmesh render mode (textured mode): filled = the translucent green sheet; wire = transparent
// triangles with SOLID outlines so the surface underneath stays readable; both = faint fill +
// outlines; off = none. --no-nav remains an alias for off.
string navMode = "filled";
// Editor markers (dragon perches, heading markers, X markers...) are not world geometry: the
// game never draws them. Filtered by default; --markers renders them translucently as an extra
// layer. --list-meshes prints every distinct mesh with its classification, so what got filtered
// is inspectable rather than guessed.
bool showMarkers = false;
bool listMeshes = false;
// TROUBLESHOOTING OVERLAY. --conflicts <emit.json> reads a detector's emitted ConflictFile and
// paints the implicated navmeshes RED in this cell — whole records for CLOBBERED, the named
// navmesh ends for dangling links — and auto-highlights implicated doors orange. --red
// "FORMKEY[:tri]" does the same by hand for one navmesh (optionally a single triangle). When
// either is given and --nav was not, the mode defaults to WIRE: green outlines for healthy
// navmesh, red for the finding — the troubleshooting default.
string? conflictsPath = null;
var redSpecs = new List<string>();
bool navModeExplicit = false;
// --nav-plugin renders the navmesh AS ONE PLUGIN AUTHORED IT (that plugin's record versions,
// nothing else) instead of the winning state. --compare "A.esp,B.esp" renders three panels with
// one camera — A's navmesh, B's navmesh, the winning state — and stitches them side by side;
// the contested records stay red in every panel, so the re-cut is visible as a before/after.
string? navPlugin = null;
(string A, string B)? compare = null;
// Terrain: LAND heightmaps for the target cell and its ring-1 neighbours, textured with each
// quadrant's winning base landscape texture. --no-terrain turns it off.
bool drawTerrain = true;
// Landscape ALPHA LAYERS. A Skyrim road is not a placed mesh — it is a landscape texture
// (LDirtPath01 and friends) painted onto the terrain as an alpha layer with a per-vertex
// opacity map. Drawing only each quadrant's base layer drops every road, path and blend in the
// scene, so the layers are rendered by default; --no-terrain-layers goes back to base-only.
bool drawTerrainLayers = true;
// Healthy-navmesh wire colour: auto (default) samples the rendered pixels UNDER the navmesh in a
// first pass and picks the palette colour farthest from both the background and the reserved
// conflict red; or force one of: green, cyan, magenta, yellow, blue.
string navColor = "auto";
// Background. The frame clears to a pale sky blue: what is behind the geometry IS sky, and
// a sky-coloured gap reads as sky instead of as a hole in the render. --sky R,G,B overrides,
// in either 0-255 or 0-1.
var skyColor = new System.Numerics.Vector3(0.70f, 0.82f, 0.92f);
// Navmesh the camera cannot see is still navmesh. Both hidden categories are redrawn as
// ALWAYS-VISIBLE DASHED outlines by default — solid means "you can see this", dashed means
// "it is here, something is in front of it". --no-nav-underground drops the buried-under-the-
// terrain layer; --no-nav-under-statics drops the behind-geometry layer. Either one off puts
// those segments back under the depth buffer, i.e. back to being silently invisible.
bool dashUnderground = true, dashUnderStatics = true;
// Multisampling. --size raises detail; this is what stops the overlay's diagonals from reading
// as staircases at any size. 0 or 1 turns it off, and the count is clamped to what the GL
// context actually supports (reported in the per-render line).
int msaa = 4;
// --pick X,Y (repeatable) answers "what is at this pixel" by unprojecting the finished frame's
// depth buffer: image coordinates in, Skyrim coordinates out. It is the bridge from something
// noticed in a render to a query against the load order.
var picks = new List<(int X, int Y)>();
// --list-terrain prints what each cell quadrant is PAINTED with — base layer plus every alpha
// layer, with how many of its 289 points actually carry opacity. Roads in Skyrim are painted
// terrain, not placed meshes, so this is where a missing road shows up.
bool listTerrain = false;
// --nav-at X,Y answers "is there navmesh here" from the record rather than from the picture:
// every winning triangle whose 2D footprint contains the point, INCLUDING the ones the render
// filters out, and why each was filtered.
var navAt = new List<(float X, float Y)>();
// --find-ltex SUBSTR answers "where is this ground texture actually painted": the matching
// LTEX records, then a walk of the WHOLE target worldspace reporting the cells that paint
// them and how heavily. Picking a demonstration cell by memory of where roads are is guessing;
// this makes it a lookup.
string? findLtex = null;
// The render covers the target cell AND its ring of neighbours; a white dashed line along the
// target's four edges says which is which. --no-cell-outline drops it.
bool cellOutline = true;
// WATER and PROJECTED COVERINGS are ON by default, and that default is the point: their absence
// was actively misleading rather than merely incomplete. Sea with nothing drawn at the water
// height reads as sky, and this project's standing rule is that empty background is a defect
// signal; a snowless Winterhold reads as a texture failure. Both change what EVERY exterior
// render looks like, so both have an escape hatch for reproducing an older picture, and both
// announce themselves on the console when they draw anything.
bool drawWater = true;
bool drawCoverings = true;
// Exterior REFS load for the target cell and its ±1 ring, matching the terrain (defect fix,
// 2026-08-28: neighbours contributed terrain and nothing else, so any eye-level shot showed
// near objects, then void, then bare ring terrain — the College arcade 700 u away simply never
// loaded). Navmesh and the conflict overlay stay TARGET-CELL-ONLY: they are the annotation
// layer for the cell under examination, same scoping as the cell outline. --no-ring-refs goes
// back to target-cell-only refs (the old, cheaper behaviour). Interiors have no ring.
bool ringRefs = true;
// In --compare, mark the navmesh vertices one plugin's version has and the other's does not with
// small spheres, in every panel that contains them. --no-nav-diff drops them.
bool navDiff = true;
// --refs-at X,Y[,R] lists every winning reference near a spot and says, per ref, whether the
// render draws it and why not. "Is there an object here that isn't being rendered?" is a
// question the picture cannot answer, because the answer is a thing you cannot see.
var refsAt = new List<(float X, float Y, float R)>();
// --euler renders the SAME cell under all six Euler compositions. The numeric tests could not
// separate them (0.62%-0.69%, then 1.84%-2.08%), which is itself a finding — but a wrong order
// scatters kit pieces by hundreds of units, and that is trivially visible even when it is
// statistically invisible. This is the test the numbers could not run.
bool euler = false;
// --npc FORMKEY[@X,Y,Z[,HEADING]][=label] (repeatable, textured mode): place the NPC's actual
// character model — FaceGen head, skinned body, and the outfit the RUNTIME will dress it in
// (record winner + SkyPatcher outfitDefault + SPID Outfit distribution, layered the way the
// game layers them). The FormKey may be the NPC_ base or its placed ACHR; with no @position
// the ref's own winning placement is used (heading in degrees, `getangle z` convention).
// --npc-no-outfit renders the base character without the outfit layer.
// --npc-puck on|off|auto draws a translucent yellow disc + always-visible rim under each
// actor ("puck tracker") — auto (default) turns it on when the camera looks down steeply,
// where a standing figure is a few dozen pixels and needs the marker.
var npcSpecs = new List<string>();
bool npcNoOutfit = false;
string npcPuck = "auto";
// --cull-above Z (textured mode): dollhouse slice — static triangles whose vertices ALL sit
// above this Skyrim z are dropped, so an interior top-down shows the storey under the roof and
// upper floors instead of their outsides. Walls spanning the cut keep standing; terrain,
// navmesh, NPC actors and the overlay are never sliced. The caption names the cut.
float? cullAbove = null;
// --interactive (textured mode; implies it): instead of saving a PNG, open the scene in a
// visible window — WASD + RMB-mouse fly-through, hover to identify refs/navmesh/terrain
// (FormKey + runtime FormID in a HUD bar), P prints a paste-ready --eye/--yaw/--pitch line,
// F12 saves a captioned PNG. The scouting step; the reproducible static render stays the
// deliverable. Not combinable with --compare (three panels, one window — no).
bool interactiveMode = false;
// --fly-speed / --look-sens: the fly-through's feel, which is a matter of taste and of monitor,
// and used to be a pair of constants in the source. Both are also adjustable in the window
// (scroll, and , / .); these set where a session STARTS, so a value that turned out right does
// not have to be re-dialled every run. Defaults unchanged: 600 u/s and 0.15 deg/px.
float flySpeed = 600f, lookSens = 0.15f;
// --route X,Y (repeatable): the path to WALK from this camera, drawn as straight segments with
// arrowheads pointing waypoint to waypoint. X and Y ONLY — z comes from the winning navmesh, and
// each waypoint's clearance is checked with the same five-probe test --emit-bats uses. OFF-MESH IS
// A HARD ERROR: a path over unwalkable ground is worse than no path, because it looks
// authoritative. A worklist shot's own "route" is the same thing, and overrides this.
//   --route-color NAME|R,G,B   the route's colour; cyan by default, and RESERVED — the nav wire's
//                              auto-pick is told to avoid it, or the two come out the same hue over
//                              some patch of ground and the whole overlay stops being readable.
//   --route-arrows auto|end|N  arrowhead spacing: auto (constant size and spacing ON SCREEN, the
//                              default), one per segment at its far end, or every N world units.
//   --no-route-discs           drop the translucent fill disc drawn at each waypoint.
//   --route-z Z                the floor height the route runs on, used to pick the right STOREY
//                              when several cover a waypoint. Absent, it is the shot's measured
//                              floorZ, or eye minus --eye-height — and that last one is an
//                              assumption a top-down camera makes badly, since its eye is a
//                              thousand units above the ground the route is on.
var routeXY = new List<(float X, float Y)>();
string routeColorName = "cyan";
var routeColor = new System.Numerics.Vector4(0.05f, 0.85f, 0.95f, 1f);
float? routeZ = null;
float? routeArrowEvery = null;                // null = distance-scaled; 0 = one per segment, at the end
string routeArrowsSpec = "auto";
bool routeDiscs = true;
// --worklist <FILE>: a SHOT LIST (see _shared/Worklist.cs) — cells, and per cell the camera poses
// worth looking at, each with the sentence that says why. Under --interactive, PageUp/PageDown step
// the shots and Ctrl+PageUp/PageDown step the cells; without it, every shot is rendered to its own
// numbered PNG. The same file feeds --emit-bats, which is the point: the picture and the place the
// tester ends up standing come from ONE set of coordinates and cannot drift apart. REPEATABLE, and
// the flag may be left off entirely: a .json path given positionally — which is what dropping the
// file on the exe produces — is read the same way, and several concatenate into one run.
// --emit-bats <NAME|DIR>: write the worklist as console batch files instead of rendering anything.
// A bare name resolves to <instance>\mods\<NAME> — an MO2 mod folder, which is how the files end
// up scoped to a profile rather than loose in the game directory. A path is used as given.
string? emitBats = null;
// --bats-profiles <A,B>: enable that mod in these profiles and DISABLE it in every other profile of
// the instance. Opt-in, because it edits MO2's own modlist.txt.
var batsProfiles = new List<string>();
for (int i = argStart; i < args.Length; i++)
{
    if (args[i] == "--out" && i + 1 < args.Length) outDir = args[++i];
    else if (args[i] == "--size" && i + 1 < args.Length) size = int.Parse(args[++i]);
    else if (args[i] == "--elev" && i + 1 < args.Length) elev = float.Parse(args[++i]);
    else if (args[i] == "--no-nav") drawNav = false;
    else if (args[i] == "--ref" && i + 1 < args.Length) highlight = args[++i];
    else if (args[i] == "--euler") euler = true;
    else if (args[i] == "--angles" && i + 1 < args.Length)
        angles = args[++i].Split(',').Select(float.Parse).ToList();
    else if (args[i] == "--tex") textured = true;
    else if (args[i] == "--eye" && i + 1 < args.Length) { var p = args[++i].Split(','); eye = new(float.Parse(p[0]), float.Parse(p[1]), float.Parse(p[2])); textured = true; }
    else if (args[i] == "--eye-ref" && i + 1 < args.Length) { eyeRef = args[++i]; textured = true; }
    else if (args[i] == "--eye-height" && i + 1 < args.Length) eyeHeight = float.Parse(args[++i]);
    else if (args[i] == "--look" && i + 1 < args.Length) { var p = args[++i].Split(','); look = new(float.Parse(p[0]), float.Parse(p[1]), float.Parse(p[2])); }
    else if (args[i] == "--look-ref" && i + 1 < args.Length) lookRef = args[++i];
    else if (args[i] == "--yaw" && i + 1 < args.Length) yaw = float.Parse(args[++i]);
    else if (args[i] == "--pitch" && i + 1 < args.Length) pitch = float.Parse(args[++i]);
    else if (args[i] == "--fov" && i + 1 < args.Length) fov = float.Parse(args[++i]);
    else if (args[i] == "--fx") keepFx = true;
    else if (args[i] == "--fx-static") fxStatic = true;
    else if (args[i] == "--nav" && i + 1 < args.Length) { navMode = args[++i].ToLowerInvariant(); navModeExplicit = true; }
    else if (args[i] == "--conflicts" && i + 1 < args.Length) conflictsPath = args[++i];
    else if (args[i] == "--red" && i + 1 < args.Length) redSpecs.Add(args[++i]);
    else if (args[i] == "--nav-plugin" && i + 1 < args.Length) navPlugin = args[++i];
    else if (args[i] == "--compare" && i + 1 < args.Length)
    { var p = args[++i].Split(','); if (p.Length == 2) compare = (p[0].Trim(), p[1].Trim()); }
    else if (args[i] == "--no-terrain") drawTerrain = false;
    else if (args[i] == "--no-water") drawWater = false;
    else if (args[i] == "--no-coverings") drawCoverings = false;
    else if (args[i] == "--no-terrain-layers") drawTerrainLayers = false;
    else if (args[i] == "--sky" && i + 1 < args.Length)
    {
        var p = args[++i].Split(',');
        if (p.Length == 3)
        {
            var v = new System.Numerics.Vector3(float.Parse(p[0]), float.Parse(p[1]), float.Parse(p[2]));
            skyColor = v.X > 1f || v.Y > 1f || v.Z > 1f ? v / 255f : v;      // 0-255 or 0-1, both fine
        }
    }
    else if (args[i] == "--no-nav-underground") dashUnderground = false;
    else if (args[i] == "--no-nav-under-statics") dashUnderStatics = false;
    else if (args[i] == "--msaa" && i + 1 < args.Length) msaa = int.Parse(args[++i]);
    else if (args[i] == "--pick" && i + 1 < args.Length)
    { var p = args[++i].Split(','); if (p.Length == 2) picks.Add((int.Parse(p[0]), int.Parse(p[1]))); }
    else if (args[i] == "--list-terrain") listTerrain = true;
    else if (args[i] == "--find-ltex" && i + 1 < args.Length) findLtex = args[++i];
    else if (args[i] == "--no-cell-outline") cellOutline = false;
    else if (args[i] == "--no-ring-refs") ringRefs = false;
    else if (args[i] == "--no-nav-diff") navDiff = false;
    else if (args[i] == "--nav-at" && i + 1 < args.Length)
    { var p = args[++i].Split(','); if (p.Length == 2) navAt.Add((float.Parse(p[0]), float.Parse(p[1]))); }
    else if (args[i] == "--refs-at" && i + 1 < args.Length)
    { var p = args[++i].Split(','); if (p.Length >= 2) refsAt.Add((float.Parse(p[0]), float.Parse(p[1]), p.Length > 2 ? float.Parse(p[2]) : 256f)); }
    else if (args[i] == "--nav-color" && i + 1 < args.Length) navColor = args[++i].ToLowerInvariant();
    else if (args[i] == "--npc" && i + 1 < args.Length) { npcSpecs.Add(args[++i]); textured = true; }
    else if (args[i] == "--npc-no-outfit") npcNoOutfit = true;
    else if (args[i] == "--npc-puck" && i + 1 < args.Length) npcPuck = args[++i].ToLowerInvariant();
    else if (args[i] == "--route" && i + 1 < args.Length)
    {
        // Refused, not skipped. --nav-at can drop a malformed probe and lose one answer; a route
        // that quietly drops a waypoint draws a DIFFERENT PATH and says nothing about it.
        var spec = args[++i];
        var p = spec.Split(',');
        if (p.Length != 2 || !float.TryParse(p[0], out var rwx) || !float.TryParse(p[1], out var rwy))
        { Console.Error.WriteLine($"--route '{spec}' is not X,Y. Waypoints carry no z — it is resolved from the winning navmesh."); return 1; }
        routeXY.Add((rwx, rwy));
        textured = true;
    }
    else if (args[i] == "--route-color" && i + 1 < args.Length)
    {
        var spec = args[++i];
        var p = spec.Split(',');
        if (p.Length == 3)
        {
            var v = new System.Numerics.Vector3(float.Parse(p[0]), float.Parse(p[1]), float.Parse(p[2]));
            if (v.X > 1f || v.Y > 1f || v.Z > 1f) v /= 255f;      // 0-255 or 0-1, like --sky
            routeColor = new System.Numerics.Vector4(v, 1f);
            routeColorName = $"{v.X:0.##},{v.Y:0.##},{v.Z:0.##}";
        }
        else { routeColorName = spec.ToLowerInvariant(); routeColor = default; }   // resolved against the table below
    }
    else if (args[i] == "--route-z" && i + 1 < args.Length) routeZ = float.Parse(args[++i]);
    else if (args[i] == "--route-arrows" && i + 1 < args.Length) routeArrowsSpec = args[++i].ToLowerInvariant();
    else if (args[i] == "--no-route-discs") routeDiscs = false;
    else if (args[i] == "--cull-above" && i + 1 < args.Length) cullAbove = float.Parse(args[++i]);
    else if (args[i] == "--interactive") { interactiveMode = true; textured = true; }
    else if (args[i] == "--fly-speed" && i + 1 < args.Length) flySpeed = float.Parse(args[++i]);
    else if (args[i] == "--look-sens" && i + 1 < args.Length) lookSens = float.Parse(args[++i]);
    else if (args[i] == "--worklist" && i + 1 < args.Length) worklistPaths.Add(args[++i]);
    else if (args[i] == "--emit-bats" && i + 1 < args.Length) emitBats = args[++i];
    else if (args[i] == "--bats-profiles" && i + 1 < args.Length)
        batsProfiles.AddRange(args[++i].Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
    else if (args[i] == "--markers") showMarkers = true;
    else if (args[i] == "--list-meshes") listMeshes = true;
    else if (args[i] is "--instance" or "--game" or "--profile") i++;   // consumed above
    // A worklist path is a worklist WHEREVER it lands, not only in the leading run that a drop
    // produces: `viewprobe --interactive tour.json` is the obvious thing to type once dropping one
    // works. This loop has no unknown-argument arm, so without this the file would go on the floor
    // and the run would die with "no cell given" while the user was looking straight at the cell
    // they gave. Flag VALUES never reach here — every branch above consumes its own with args[++i].
    else if (IsWorklistFile(args[i])) worklistPaths.Add(args[i]);
}
// Replication args: the command line verbatim, minus this machine's paths (instance/profile/
// game/out). Embedded in every output PNG — a replay from these is BYTE-exact, which the
// resolved camera pose alone is not: a --look camera replayed as the equivalent --yaw/--pitch
// composes the same view direction but a sub-pixel-different frame (measured).
var pathFlags = new HashSet<string>(StringComparer.Ordinal) { "--instance", "--profile", "--game", "--out" };
var replArgs = new List<string>();
for (int ai = 0; ai < args.Length; ai++)
{
    if (pathFlags.Contains(args[ai])) { ai++; continue; }
    replArgs.Add(args[ai].Contains(' ') ? $"\"{args[ai]}\"" : args[ai]);
}
string replayArgs = string.Join(' ', replArgs);

if (!drawNav) navMode = "off";
if ((conflictsPath is not null || redSpecs.Count > 0) && !navModeExplicit) navMode = "wire";
if (navMode is not ("off" or "filled" or "wire" or "both"))
{ Console.Error.WriteLine($"--nav '{navMode}' is not one of: off, filled, wire, both."); return 1; }
if (npcPuck is not ("on" or "off" or "auto"))
{ Console.Error.WriteLine($"--npc-puck '{npcPuck}' is not one of: on, off, auto."); return 1; }
if (interactiveMode && compare is not null)
{ Console.Error.WriteLine("--interactive and --compare cannot combine: the compare is three fixed panels, the window is one free camera. Fly first, then run the compare from a pose it printed."); return 1; }
// A flag that silently does nothing is a trap, so say so rather than accepting it.
if (!interactiveMode && (Array.IndexOf(args, "--fly-speed") >= 0 || Array.IndexOf(args, "--look-sens") >= 0))
{ Console.Error.WriteLine("--fly-speed and --look-sens only mean anything with --interactive: they set the window's flight feel, and a static render has no flight."); return 1; }
if (flySpeed is < 1f or > 100000f)
{ Console.Error.WriteLine($"--fly-speed {flySpeed} is out of range: 1..100000 u/s."); return 1; }
if (lookSens is < 0.01f or > 2f)
{ Console.Error.WriteLine($"--look-sens {lookSens} is out of range: 0.01..2 degrees per pixel (default 0.15)."); return 1; }
// The route's named colours. Deliberately NOT the nav palette by another name: red is a conflict,
// orange is a door, violet is a nav diff and white is the cell outline, so those are the four this
// will not hand out. Whatever is chosen becomes reserved — the nav wire's auto-pick avoids it.
if (routeColor == default)
{
    var routeNames = new Dictionary<string, System.Numerics.Vector3>(StringComparer.OrdinalIgnoreCase)
    {
        ["cyan"] = new(0.05f, 0.85f, 0.95f),
        ["magenta"] = new(0.90f, 0.20f, 0.95f),
        ["yellow"] = new(1.00f, 0.88f, 0.10f),
        ["green"] = new(0.05f, 0.85f, 0.30f),
        ["blue"] = new(0.25f, 0.45f, 1.00f),
        ["pink"] = new(1.00f, 0.40f, 0.80f),
    };
    if (!routeNames.TryGetValue(routeColorName, out var rcv))
    {
        Console.Error.WriteLine($"--route-color '{routeColorName}' is not one of: {string.Join(", ", routeNames.Keys)} — or an R,G,B triple (0-255 or 0-1). "
                              + "Red, orange, violet and white are reserved for conflicts, doors, nav diffs and the cell outline.");
        return 1;
    }
    routeColor = new System.Numerics.Vector4(rcv, 1f);
}
if (routeArrowsSpec == "auto") routeArrowEvery = null;
else if (routeArrowsSpec == "end") routeArrowEvery = 0f;
else if (float.TryParse(routeArrowsSpec, out var rae) && rae > 0f) routeArrowEvery = rae;
else
{ Console.Error.WriteLine($"--route-arrows '{routeArrowsSpec}' is not auto, end, or a positive number of world units."); return 1; }
if (routeXY.Count == 1)
{ Console.Error.WriteLine("--route needs at least two waypoints: one point is a position, not a path."); return 1; }
// A flag that silently does nothing is a trap — the same rule --fly-speed is held to.
if (routeXY.Count == 0 && worklistPaths.Count == 0
    && (Array.IndexOf(args, "--route-color") >= 0 || Array.IndexOf(args, "--route-arrows") >= 0
        || Array.IndexOf(args, "--route-z") >= 0 || Array.IndexOf(args, "--no-route-discs") >= 0))
{ Console.Error.WriteLine("--route-color / --route-z / --route-arrows / --no-route-discs only mean anything with a route: pass --route X,Y (repeatable) or a worklist whose shots carry one."); return 1; }
if (emitBats is not null && worklistPaths.Count == 0)
{ Console.Error.WriteLine("--emit-bats needs --worklist: the bat files ARE the worklist, written for the console instead of the camera."); return 1; }
if (emitBats is not null && worklistPaths.Count > 1)
{ Console.Error.WriteLine("--emit-bats takes ONE worklist: the bat mod's MANIFEST.md names the file it was generated from, and a merge of several has no such file. Emit them one at a time."); return 1; }
if (batsProfiles.Count > 0 && emitBats is null)
{ Console.Error.WriteLine("--bats-profiles only means anything with --emit-bats: it enables the mod folder that --emit-bats writes."); return 1; }
if (!positionalTarget && worklistPaths.Count == 0)
{ Console.Error.WriteLine("no cell given. Pass a CellEditorID/FormID as the first argument, or --worklist FILE to take the cells from a shot list."); return 1; }

// The worklist is read and VALIDATED before the load walk. A malformed shot list found after a
// multi-minute walk is a multi-minute walk wasted.
HarnessShared.WorklistFile? worklist = null;
if (worklistPaths.Count > 0)
{
    var loaded = new List<(string Path, HarnessShared.WorklistFile File)>();
    foreach (var wp in worklistPaths)
    {
        try { loaded.Add((wp, HarnessShared.WorklistIo.Read(wp))); }
        catch (Exception ex) { Console.Error.WriteLine(ex.Message); return 1; }
    }
    try { worklist = HarnessShared.WorklistIo.Merge(loaded); }
    catch (Exception ex) { Console.Error.WriteLine(ex.Message); return 1; }

    string src = loaded.Count == 1
        ? worklistPaths[0]
        : $"{loaded.Count} files: {string.Join(", ", loaded.Select(l => Path.GetFileName(l.Path)))}";
    Console.WriteLine($"worklist '{worklist.Label}': {worklist.Cells.Count} cell(s), "
                    + $"{worklist.Cells.Sum(c => c.Shots.Count)} shot(s)  [{src}]");
}

// A DROP IS A HUMAN AT A MOUSE. Static mode writes numbered PNGs into a folder they would then have
// to go and find, and pays the load walk once per SHOT; the fly-through pays it once per CELL and
// puts the scene on a screen, which is what "open this" means when the gesture was dragging a file
// onto an icon. Only the GUI launch is touched — a shell run, with or without --interactive, keeps
// the behaviour it has always had.
if (guiLaunch && worklist is not null && !interactiveMode)
{
    interactiveMode = true;
    textured = true;
    Console.WriteLine("launched from Explorer — opening the fly-through: PageUp/PageDown steps the shots, "
                    + "Ctrl+PageUp/PageDown steps the cells.");
    Console.WriteLine("(run it from a shell without --interactive to render every shot to a PNG instead.)");
}
// Set when the fly-through's Ctrl+PageUp/PageDown asks for a different cell: the window cannot
// build one, so it closes and leaves the request here for the run loop to act on.
(int Cell, int Shot)? pendingSwitch = null;

// --npc spec parsing happens BEFORE the load walk: the walk watches for these
// FormKeys (as placed ACHRs and as ACHR bases) wherever they live, the same way
// implicated doors are watched.
var npcParsed = new List<(FormKey Key, System.Numerics.Vector3? Pos, float? HeadingDeg, string? Label, string Raw)>();
foreach (var rawSpec in npcSpecs)
{
    var s = rawSpec;
    string? label = null;
    int eqAt = s.IndexOf('=');                              // '=' — a ':' would clash with the FormKey's own
    if (eqAt >= 0) { label = s[(eqAt + 1)..].Trim(); s = s[..eqAt]; }
    System.Numerics.Vector3? npos = null;
    float? nheading = null;
    int atAt = s.IndexOf('@');
    if (atAt >= 0)
    {
        var nums = s[(atAt + 1)..].Split(',');
        s = s[..atAt];
        if (nums.Length is not (3 or 4)) { Console.Error.WriteLine($"--npc '{rawSpec}': position must be @X,Y,Z or @X,Y,Z,HEADING."); return 1; }
        try
        {
            npos = new System.Numerics.Vector3(float.Parse(nums[0]), float.Parse(nums[1]), float.Parse(nums[2]));
            if (nums.Length == 4) nheading = float.Parse(nums[3]);
        }
        catch { Console.Error.WriteLine($"--npc '{rawSpec}': position is not numeric."); return 1; }
    }
    try { npcParsed.Add((FormKey.Factory(s.Trim()), npos, nheading, label, rawSpec)); }
    catch { Console.Error.WriteLine($"--npc '{rawSpec}': '{s}' is not a FormKey (XXXXXX:Plugin.esp)."); return 1; }
}
var npcWatchKeys = new HashSet<FormKey>(npcParsed.Select(sp => sp.Key));
Directory.CreateDirectory(outDir);

// ---------------------------------------------------------------------------------------
// 1. Resolve the cell's winning state
// ---------------------------------------------------------------------------------------
var sw = Stopwatch.StartNew();
var comp = Mo2LoadOrder.ReadComposition(profileDir);
// Ordered BEFORE archive discovery so --emit-bats, which reads records and no assets at all, never
// pays for BSA indexing it has no use for.
var order = Mo2LoadOrder.Build(profileDir, modsDir, dataDir, overwriteDir);

// --emit-bats: the whole run is the bat files. It has its own compact walk (BatEmit.cs) covering
// EVERY cell in the worklist at once, so it exits here rather than falling through to a renderer
// that can only hold one cell.
if (emitBats is not null)
{
    // A bare mod NAME has no directory part, which is exactly what GetFileName round-tripping
    // tests — no separator literals needed, and it is right for both slash directions.
    bool asPath = Path.IsPathRooted(emitBats) || Path.GetFileName(emitBats) != emitBats;
    string batsDir = asPath ? Path.GetFullPath(emitBats) : Path.Combine(modsDir, emitBats);
    string batsName = Path.GetFileName(Path.TrimEndingDirectorySeparator(batsDir));
    return ViewProbe.BatEmit.Run(worklist!, worklistPaths[0], batsDir, batsName, order.OrderedPaths, Instance, batsProfiles);
}

var arch = ArchiveDiscovery.Discover(profileDir, modsDir, dataDir, overwriteDir, GamePath);
using var assets = AssetResolver.Build(overwriteDir, modsDir, dataDir, comp.EnabledMods, arch.Archives);

var winRef = new Dictionary<FormKey, PlacedRef>();
// Conflicts are parsed BEFORE the load walk so implicated doors can be located wherever they
// actually live — a door reported against a navmesh's cell is often placed in a neighbour cell
// or the worldspace persistent cell, and "not drawn, silently" hides exactly the thing the
// overlay exists to show.
HarnessShared.ConflictFile? conflictFile = null;
var doorWatchKeys = new HashSet<FormKey>();
var doorWhere = new Dictionary<FormKey, (FormKey Cell, string? CellEd, PlacedRef Ref)>();
if (conflictsPath is not null)
{
    try { conflictFile = HarnessShared.ConflictIo.Read(conflictsPath); }
    catch (Exception ex) { Console.Error.WriteLine($"could not read '{conflictsPath}': {ex.Message}"); return 1; }
    foreach (var c in conflictFile.Conflicts)
        foreach (var rec in c.Necessary.Concat(c.Alternative ?? Array.Empty<HarnessShared.ConflictRecord>()))
            if (rec.Role == "door")
            { try { doorWatchKeys.Add(FormKey.Factory(rec.FormKey)); } catch { } }
}
// Persistent worldspace refs (doors above all) live in the worldspace's PERSISTENT cell, not the
// exterior cell record — collect them separately, merge by position after the target is known.
var persistentWin = new Dictionary<FormKey, (FormKey Ws, PlacedRef Ref)>();
// Runtime enable state is GLOBAL: a ref's XESP parent can live in another cell entirely (the
// standard master-switch idiom), so the winning disabled-flag and enable-parent of EVERY ref in
// the load order are tracked before any drop decision. Winner-takes-all per ref, so a later
// override that clears the flag or drops the XESP must erase the entry, not just fail to add it.
var refDisabledFlag = new HashSet<FormKey>();
var refEnableParent = new Dictionary<FormKey, (FormKey Parent, bool Opposite)>();
// --npc placement watch, same shape as the door watch: every PlacedNpc whose own FormKey OR
// whose Base is a requested NPC, wherever it lives (per-ref "later plugin wins").
var npcRefWhere = new Dictionary<FormKey, (FormKey Cell, string? CellEd, PlacedRef Ref)>();
var npcBaseWhere = new Dictionary<FormKey, Dictionary<FormKey, (FormKey Cell, string? CellEd, PlacedRef Ref)>>();
var winNav = new Dictionary<FormKey, NavmeshGeometry>();
FormKey targetWorldspace = FormKey.Null;
(int X, int Y)? targetGrid = null;
// Record versions per plugin, collected only for the plugins a panel needs.
var navVersionPlugins = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
if (navPlugin is not null) navVersionPlugins.Add(navPlugin);
if (compare is { } cmp0) { navVersionPlugins.Add(cmp0.A); navVersionPlugins.Add(cmp0.B); }
var navByPlugin = new Dictionary<string, Dictionary<FormKey, NavmeshGeometry>>(StringComparer.OrdinalIgnoreCase);
var baseMesh = new Dictionary<FormKey, string>();
var baseIsMarker = new HashSet<FormKey>();
var baseEditorId = new Dictionary<FormKey, string>();
var ltexToTxst = new Dictionary<FormKey, FormKey>();
var ltexEditorId = new Dictionary<FormKey, string>();
var txstDiffuse = new Dictionary<FormKey, string>();

// ---- projected coverings (MATO) and water, both winner-takes-all over the load-order walk ----
// Values are COPIED out of the record getters, never referenced: every mod overlay is opened in a
// `using` and disposed at the end of its iteration, so a retained getter is a use-after-free that
// happens to work until it doesn't.
var baseMato = new Dictionary<FormKey, (FormKey Mato, float MaxAngle)>();
var matoRec = new Dictionary<FormKey, (string Ed, System.Numerics.Vector3 Projection, float Bias, float Scale,
                                       float UvScale, System.Numerics.Vector3 Color, bool SinglePass, string? Model)>();
var watrRec = new Dictionary<FormKey, (string Ed, string? Noise, float NoiseUv,
                                       System.Numerics.Vector3 Shallow, System.Numerics.Vector3 Deep,
                                       System.Numerics.Vector3 Reflect, float Reflectivity,
                                       byte Opacity, float Fresnel)>();
// Per-cell water, keyed by grid point, plus the worldspace fallbacks the cells defer to.
var cellWater = new Dictionary<(int X, int Y), (bool Has, float Height, FormKey? Water)>();
FormKey? wsWater = null;
float wsDefaultWaterHeight = float.MaxValue;

static System.Numerics.Vector3 Rgb(System.Drawing.Color c) =>
    new(c.R / 255f, c.G / 255f, c.B / 255f);
// CELL.WaterHeight / WRLD.DefaultWaterHeight hold float.MaxValue (3.4028235E+38) to mean "not set,
// use the level above". Winterhold's own cell is on that sentinel, which is why a naive read of
// the cell alone would put the sea at 3e38 and draw nothing anywhere near the world.
// Range, not just the sentinel. Skyrim's world z lives inside roughly ±60,000, and the field is
// used as a "not set" marker by more than one value: 3.4028235E+38 is the documented one, but
// Winterhold's sea cells came back with ~4.29e9 (2^32 − 14000 — a −14000 that has been through an
// unsigned reinterpretation somewhere upstream). Testing only for the documented sentinel let that
// second one through as a real height. Anything outside the world is not a water level.
static bool RealHeight(float h) => !float.IsNaN(h) && MathF.Abs(h) < 100_000f;
// A record's texture path is authored, not canonical: WATR ships "Data\Textures\Water\..." with
// a Data\ prefix the resolver does not want, while TXST ships bare "water\...". Strip the one and
// add the other, so either form resolves.
static string NormalizeTexPath(string p0)
{
    var p1 = p0.Replace('/', '\\').TrimStart('\\');
    if (p1.StartsWith("data\\", StringComparison.OrdinalIgnoreCase)) p1 = p1[5..];
    return p1.StartsWith("textures\\", StringComparison.OrdinalIgnoreCase) ? p1 : "textures\\" + p1;
}

FormKey? cellKey = null;
string cellLabel = target;
bool interior = false;

// Runtime enable state, hoisted out of RunCellTextured because the flat diagnostic mode calls it
// AFTER that function returns. enableMemo is per-cell state and is cleared with the rest of it.
var enableMemo = new Dictionary<FormKey, bool>();
bool RefEnabledInner(FormKey fk, HashSet<FormKey>? seen)
{
    if (fk.ID == 0x14) return true;                                  // PlayerRef
    if (enableMemo.TryGetValue(fk, out var known)) return known;
    bool en;
    if (refEnableParent.TryGetValue(fk, out var p))
    {
        seen ??= new HashSet<FormKey>();
        en = seen.Add(fk)
            ? RefEnabledInner(p.Parent, seen) ^ p.Opposite
            : !refDisabledFlag.Contains(fk);                         // cycle
    }
    else en = !refDisabledFlag.Contains(fk);
    enableMemo[fk] = en;
    return en;
}
bool RefEnabled(FormKey fk) => RefEnabledInner(fk, null);
// The three dispositions a drop report needs to tell apart.
string? DisabledWhy(PlacedRef r) =>
    RefEnabled(r.FormKey) ? null
    : refEnableParent.TryGetValue(r.FormKey, out var dp) ? $"disabled via enable parent {dp.Parent}"
    : "initially disabled";

// ---------------------------------------------------------------------------------------
// ONE CELL'S WORK, as a function: resolve the cell's identity, walk the load order for its
// winning state, build the scene, and either save a PNG or open the fly-through.
//
// It is a function so a WORKLIST can call it more than once. Ctrl+PageUp/PageDown in the window
// asks for another cell, and a different cell is a different SCENE — no camera move can reach it,
// the refs, terrain and navmesh all have to be walked again. Straight-line code could serve
// exactly one cell per process.
//
// Returns a process exit code, or -1 for "this run is not textured", in which case the flat
// diagnostic mode after the call runs on the state this call left behind — exactly as it did when
// this was straight-line code.
// ---------------------------------------------------------------------------------------
int RunCellTextured(string cellTarget, int wlCell, int wlShot)
{
    // Per-cell state is declared OUTSIDE this function, because the flat mode reads it too — so a
    // SECOND call has to clear what the first left. A collection missed here would not throw; it
    // would quietly render cell 2 holding cell 1's references.
    winRef.Clear(); persistentWin.Clear(); refDisabledFlag.Clear(); refEnableParent.Clear();
    npcRefWhere.Clear(); npcBaseWhere.Clear(); doorWhere.Clear();
    winNav.Clear(); navByPlugin.Clear();
    baseMesh.Clear(); baseIsMarker.Clear(); baseEditorId.Clear();
    ltexToTxst.Clear(); ltexEditorId.Clear(); txstDiffuse.Clear();
    baseMato.Clear(); matoRec.Clear(); watrRec.Clear(); cellWater.Clear();
    wsWater = null; wsDefaultWaterHeight = float.MaxValue;
    enableMemo.Clear();
    targetWorldspace = FormKey.Null; targetGrid = null;
    cellKey = null; cellLabel = cellTarget; interior = false;
    sw.Restart();

    // The shot's route, or the command line's. A LOCAL, not one of the mutated globals above:
    // navMode and cullAbove are set from the shot and never cleared, so shot 1's override leaks
    // into shot 2 — a route that leaked would draw the wrong path over the right cell, silently.
    IReadOnlyList<(float X, float Y)> shotRoute = routeXY;
    string routeLabel = "";
    float? shotFloorZ = null;

    // A worklist shot IS the camera: eye, aim and fov come from the shot and override whatever the
    // command line asked for, so one file drives both the picture and the bat that stands there.
    if (worklist is not null)
    {
        var wshot = worklist.Cells[wlCell].Shots[wlShot];
        eye = new System.Numerics.Vector3(wshot.Eye[0], wshot.Eye[1], wshot.Eye[2]);
        var (wsYaw, wsPitch) = HarnessShared.WorklistIo.Aim(wshot);
        yaw = wsYaw; pitch = wsPitch; fov = wshot.Fov;
        look = null; eyeRef = null; lookRef = null;      // the shot is the whole camera, not a base for one
        if (wshot.Nav is { } shotNav) navMode = shotNav;
        if (wshot.CullAbove is { } shotCull) cullAbove = shotCull;
        if (wshot.Route is { Count: > 1 })
        {
            shotRoute = wshot.Route.Select(w => (w[0], w[1])).ToList();
            routeLabel = wshot.Label;
        }
        shotFloorZ = wshot.FloorZ;      // the MEASURED floor, when the author had one
        textured = true;
        Console.WriteLine($"\n=== worklist cell {wlCell + 1}/{worklist.Cells.Count} "
                        + $"shot {wlShot + 1}/{worklist.Cells[wlCell].Shots.Count}: {wshot.Label} ===");
    }

    // ---------------------------------------------------------------------------------------
    // PRE-PASS: resolve the target cell's identity (FormKey, grid, worldspace) before the main
    // walk. The ring test needs the target's GRID, which the single-pass walk only learned when
    // it happened to meet the target cell — too late to have admitted a neighbour that came
    // earlier in the same plugin. Walks the load order with the same first-match semantics the
    // main walk used, and stops at the first hit (the defining master, usually plugin one).
    // ---------------------------------------------------------------------------------------
    bool TargetMatches(CellPlacements cp) =>
        string.Equals(cp.CellEditorId, cellTarget, StringComparison.OrdinalIgnoreCase)
        || cp.Cell.ToString().Equals(cellTarget, StringComparison.OrdinalIgnoreCase)
        || cp.Cell.ID.ToString("X6").Equals(cellTarget.TrimStart('0'), StringComparison.OrdinalIgnoreCase);
    foreach (var path in order.OrderedPaths)
    {
        ISkyrimModGetter m;
        try { m = SkyrimMod.CreateFromBinaryOverlay(path, SkyrimRelease.SkyrimSE); } catch { continue; }
        using (m as IDisposable)
        {
            foreach (var cp in PlacementExtract.FromMod(m))
            {
                if (!TargetMatches(cp)) continue;
                cellKey = cp.Cell;
                cellLabel = cp.CellEditorId ?? cp.Cell.ToString();
                interior = cp.Interior;
                targetWorldspace = cp.Worldspace;
                targetGrid = cp.Grid;
                break;
            }
        }
        if (cellKey is not null) break;
    }
    if (cellKey is null) { Console.Error.WriteLine($"cell '{cellTarget}' not found in the active load order."); return 1; }

    foreach (var path in order.OrderedPaths)
    {
        ISkyrimModGetter m;
        try { m = SkyrimMod.CreateFromBinaryOverlay(path, SkyrimRelease.SkyrimSE); } catch { continue; }
        using (m as IDisposable)
        {
            foreach (var rec in m.EnumerateMajorRecords())
            {
                // Landscape texture chain: LTEX -> TXST -> diffuse path, winner-takes-all in load
                // order, so terrain quadrants can be drawn with their real winning textures.
                if (rec is ILandscapeTextureGetter lt)
                {
                    if (lt.TextureSet.FormKeyNullable is { } ts) ltexToTxst[lt.FormKey] = ts;
                    if (!string.IsNullOrWhiteSpace(lt.EditorID)) ltexEditorId[lt.FormKey] = lt.EditorID!;
                    continue;
                }
                if (rec is ITextureSetGetter txst)
                { if (!string.IsNullOrWhiteSpace(txst.Diffuse?.GivenPath)) txstDiffuse[txst.FormKey] = txst.Diffuse!.GivenPath; continue; }
                // WATR — the water body's own colours and wave-noise layers. Values are COPIED
                // out, never held: the overlay is disposed at the end of this using block, and a
                // retained getter would read freed memory.
                if (rec is IWaterGetter wtr)
                {
                    watrRec[wtr.FormKey] = (wtr.EditorID ?? wtr.FormKey.ToString(),
                                            wtr.NoiseLayerOneTexture?.GivenPath,
                                            wtr.NoiseLayerOneUvScale,
                                            Rgb(wtr.ShallowColor), Rgb(wtr.DeepColor),
                                            Rgb(wtr.ReflectionColor), wtr.WaterReflectivity,
                                            wtr.Opacity, wtr.WaterFresnel);
                    continue;
                }
                // MATO — the projected covering itself (snow, ash, moss). Reached only through
                // STAT.Material, harvested just below.
                if (rec is IMaterialObjectGetter mato)
                {
                    var pvec = mato.ProjectionVector;
                    matoRec[mato.FormKey] = (mato.EditorID ?? mato.FormKey.ToString(),
                                             new System.Numerics.Vector3(pvec.X, pvec.Y, pvec.Z),
                                             mato.FalloffBias, mato.FalloffScale, mato.MaterialUvScale,
                                             Rgb(mato.SinglePassColor),
                                             mato.Flags.HasFlag(MaterialObject.Flag.SinglePass),
                                             mato.Model?.File.GivenPath);
                    // NOT continue: MATO is modeled, and the block below wants its model path too.
                }
                bool collectedModel = false;
                if (rec is IModeledGetter mo && mo.Model?.File is { } f && !string.IsNullOrWhiteSpace(f.GivenPath))
                {
                    var smr = (ISkyrimMajorRecordGetter)rec;
                    baseMesh[smr.FormKey] = f.GivenPath;
                    collectedModel = true;
                    // Editor-marker classification: the Is-Marker header flag (0x00800000) ONLY —
                    // that is the engine's own rule for not rendering a base. The old name
                    // heuristics (*marker* EditorID or model path) over-matched real drawn
                    // geometry: Markers\BlackPlane01.nif is the VANILLA gap-sealing black plane
                    // (record flags 0, nine lit BSTriShapes, the same job as Lux's shadow planes)
                    // and dropping it as a marker opened sky-blue holes in every Grand Solitude
                    // floor seam (2026-08-28). The cases the heuristics were added for need no
                    // rule at all: furniture-marker and dummy NIFs contain zero drawable shapes,
                    // and MarkerX/Teleport-style meshes are single effect-shader shapes the
                    // per-shape unshadeable filter already drops.
                    bool marker = (smr.MajorRecordFlagsRaw & 0x00800000) != 0;
                    if (marker) baseIsMarker.Add(smr.FormKey);
                    if (!string.IsNullOrWhiteSpace(smr.EditorID)) baseEditorId[smr.FormKey] = smr.EditorID!;
                }
                // STAT.Material -> MATO, with STAT.MaxAngle beside it. This is the ONLY path a
                // covering reaches the world by (Static is the only modeled record type carrying
                // a FormLink<IMaterialObjectGetter>), so this one line is the whole wiring.
                // Winner-takes-all like everything else here — and an override that CLEARS the
                // link has to win too, hence the explicit Remove rather than a bare assignment.
                if (rec is IStaticGetter stat)
                {
                    if (stat.Material.FormKeyNullable is { } mk && !mk.IsNull)
                        baseMato[stat.FormKey] = (mk, stat.MaxAngle);
                    else baseMato.Remove(stat.FormKey);
                }
                _ = collectedModel;
            }

            foreach (var cp in PlacementExtract.FromMod(m))
            {
                foreach (var r in cp.Refs)
                {
                    if (r.InitiallyDisabled) refDisabledFlag.Add(r.FormKey); else refDisabledFlag.Remove(r.FormKey);
                    if (r.EnableParent is { } epk) refEnableParent[r.FormKey] = (epk, r.EnableParentOpposite);
                    else refEnableParent.Remove(r.FormKey);
                }
                // Worldspace PERSISTENT refs (doors above all). Not detected by the holding cell —
                // the persistent cell masquerades as grid (0,0) — but by each ref's own Persistent
                // flag: the game loads these with the worldspace, so membership in a rendered cell
                // is a question of POSITION, resolved after the target is known.
                if (cp.Worldspace != FormKey.Null)
                    foreach (var r in cp.Refs)
                        if (r.Persistent) persistentWin[r.FormKey] = (cp.Worldspace, r);   // later plugin wins
                if (npcWatchKeys.Count > 0)
                    foreach (var r in cp.Refs)
                    {
                        if (r.RecordType != "PlacedNpc") continue;
                        if (npcWatchKeys.Contains(r.FormKey))
                            npcRefWhere[r.FormKey] = (cp.Cell, cp.CellEditorId, r);         // later plugin wins
                        if (r.Base is { } nb && npcWatchKeys.Contains(nb))
                        {
                            if (!npcBaseWhere.TryGetValue(nb, out var perRef)) npcBaseWhere[nb] = perRef = new();
                            perRef[r.FormKey] = (cp.Cell, cp.CellEditorId, r);              // later plugin wins per ref
                        }
                    }
                if (doorWatchKeys.Count > 0)
                    foreach (var r in cp.Refs)
                        if (doorWatchKeys.Contains(r.FormKey))
                            { doorWhere[r.FormKey] = (cp.Cell, cp.CellEditorId, r);     // later plugin wins
                              if (Environment.GetEnvironmentVariable("VP_DOOR_DEBUG") == "1")
                                  Console.WriteLine($"   [debug] door {r.FormKey} in cp: Cell={cp.Cell} Grid={(cp.Grid is { } gg ? $"{gg.X},{gg.Y}" : "null")} Interior={cp.Interior} Ws={cp.Worldspace} plugin={m.ModKey}"); }
                bool isTarget = cp.Cell == cellKey;
                // ±1-ring neighbours (exteriors): their refs are in frame from any eye-level
                // camera, so they load like the terrain always has. The worldspace persistent
                // cell masquerades as grid (0,0) and must not be admitted as a neighbour —
                // its refs are all Persistent, and only non-persistent refs are taken from
                // ring cells (persistent refs merge by position, below, ring-wide).
                bool inRing = !isTarget && ringRefs && !interior && !cp.Interior
                    && cp.Worldspace == targetWorldspace && cp.Worldspace != FormKey.Null
                    && targetGrid is { } tg && cp.Grid is { } cg
                    && Math.Abs(cg.X - tg.X) <= 1 && Math.Abs(cg.Y - tg.Y) <= 1;
                if (!isTarget && !inRing) continue;

                if (isTarget)
                {
                    cellLabel = cp.CellEditorId ?? cp.Cell.ToString();
                    foreach (var r in cp.Refs) winRef[r.FormKey] = r;
                    // Navmesh (and with it the conflict overlay) stays TARGET-CELL-ONLY: it is
                    // the annotation layer for the cell under examination, scoped like the cell
                    // outline. The ring contributes world geometry, not annotations.
                    foreach (var nav in cp.Navmeshes)
                    {
                        winNav[nav.FormKey] = nav;
                        var pn = m.ModKey.FileName.String;
                        if (navVersionPlugins.Contains(pn))
                        {
                            if (!navByPlugin.TryGetValue(pn, out var d)) navByPlugin[pn] = d = new();
                            d[nav.FormKey] = nav;
                        }
                    }
                }
                else
                    foreach (var r in cp.Refs)
                        if (!r.Persistent) winRef[r.FormKey] = r;
            }
        }
    }

    Console.WriteLine($"cell {cellLabel} {(interior ? "(interior)" : "(exterior)")}  {cellKey}");
    if (!interior && targetGrid is { } tgp)
    {
        // Persistent refs merge by position over the whole REF-LOADED area: the 3x3 ring when
        // ring refs are on, the target cell alone otherwise. A narrower test would silently
        // drop exterior doors standing in a neighbour cell that visibly renders.
        int ringR = ringRefs ? 1 : 0;
        float px0 = (tgp.X - ringR) * 4096f, py0 = (tgp.Y - ringR) * 4096f;
        float pspan = 4096f * (1 + 2 * ringR);
        int addedPersist = 0;
        foreach (var (pws, pr) in persistentWin.Values)
            if (pws == targetWorldspace
                && pr.Position.X >= px0 && pr.Position.X < px0 + pspan
                && pr.Position.Y >= py0 && pr.Position.Y < py0 + pspan
                && !winRef.ContainsKey(pr.FormKey))
            { winRef[pr.FormKey] = pr; addedPersist++; }
        if (addedPersist > 0)
            Console.WriteLine($"   +{addedPersist} persistent worldspace ref(s) inside the rendered {(ringR > 0 ? "3x3 cells'" : "cell's")} bounds (doors live here)");
    }
    Console.WriteLine($"winning state: {winRef.Count:N0} refs, {winNav.Count:N0} navmeshes  ({sw.Elapsed.TotalSeconds:0.0}s)");

    // ---------------------------------------------------------------------------------------
    // Runtime enable state. A ref with an XESP enable parent takes the PARENT's state (XOR the
    // opposite-of-parent flag) — its own initially-disabled flag is CK bookkeeping, not the
    // runtime answer. The player ref (ID 0x14) is always enabled at runtime even though the
    // record carries the disabled flag, which is exactly why "parent = player, opposite" is the
    // standard permanently-disabled idiom. Chains are followed with memoization; a cycle is an
    // authoring error and falls back to the ref's own flag.
    // ---------------------------------------------------------------------------------------
    // VP_ENABLE_DUMP=1 — every enable-parent group among this cell's refs, with how the chain
    // resolves. The check that a "disabled via enable parent" verdict rests on the chain the
    // data actually states, not on a wrong evaluation.
    if (Environment.GetEnvironmentVariable("VP_ENABLE_DUMP") == "1")
    {
        Console.WriteLine($"   ---- enable-parent groups among this cell's refs ----");
        var groups = winRef.Values
            .Where(r => refEnableParent.ContainsKey(r.FormKey))
            .GroupBy(r => refEnableParent[r.FormKey])
            .OrderByDescending(g => g.Count());
        foreach (var g in groups)
        {
            var (parent, opp) = g.Key;
            bool parentEnabled = RefEnabled(parent);
            string pflag = parent.ID == 0x14 ? "PlayerRef, engine-enabled"
                         : refDisabledFlag.Contains(parent) ? "flag-disabled" : "flag-enabled";
            string pchain = refEnableParent.TryGetValue(parent, out var pp)
                ? $", parented to {pp.Parent}{(pp.Opposite ? " OPPOSITE" : "")}" : "";
            Console.WriteLine($"   {g.Count(),5} ref(s)  parent {parent} [{pflag}{pchain} -> {(parentEnabled ? "ENABLED" : "DISABLED")}]"
                            + $"{(opp ? "  OPPOSITE" : "")}  => children {(parentEnabled ^ opp ? "ENABLED" : "DISABLED")}");
        }
    }

    // ---------------------------------------------------------------------------------------
    // First-person textured mode — see TexturedScene.cs. Exits here; the flat diagnostic mode
    // below is unchanged and stays the default.
    // ---------------------------------------------------------------------------------------
    if (textured)
    {
        FormKey? hiFp = null;
        if (highlight is not null) { try { hiFp = FormKey.Factory(highlight); } catch { } }

        System.Numerics.Vector3? RefPos(string spec, float lift)
        {
            try
            {
                var fk = FormKey.Factory(spec);
                if (winRef.TryGetValue(fk, out var rr))
                    return new(rr.Position.X, rr.Position.Y, rr.Position.Z + lift);
            }
            catch { }
            Console.Error.WriteLine($"'{spec}' is not a placed ref in this cell's winning state.");
            return null;
        }

        var eyePos = eye ?? (eyeRef is not null ? RefPos(eyeRef, eyeHeight) : null);
        if (eyePos is null)
        {
            Console.Error.WriteLine("first-person mode needs --eye X,Y,Z or --eye-ref FORMID (a `getpos` line pastes straight in).");
            return 1;
        }
        System.Numerics.Vector3? lookPos = look ?? (lookRef is not null ? RefPos(lookRef, 96f) : null);
        if (lookPos is null)
        {
            float yawDeg = yaw ?? 0f;                       // game convention: 0 = north (+Y), clockwise
            float yr = float.DegreesToRadians(yawDeg), pr = float.DegreesToRadians(pitch);
            var dir = new System.Numerics.Vector3(MathF.Sin(yr) * MathF.Cos(pr), MathF.Cos(yr) * MathF.Cos(pr), -MathF.Sin(pr));
            lookPos = eyePos.Value + dir * 512f;
        }

        // Which navmeshes (or single triangles) are the FINDING. null tri-set = the whole record.
        var redNav = new Dictionary<FormKey, HashSet<int>?>();
        var conflictDoors = new HashSet<FormKey>();
        void MarkRed(FormKey fk, int? tri)
        {
            if (tri is null) { redNav[fk] = null; return; }
            if (redNav.TryGetValue(fk, out var cur) && cur is null) return;   // whole-record already
            if (!redNav.TryGetValue(fk, out cur) || cur is null) redNav[fk] = cur = new HashSet<int>();
            cur.Add(tri.Value);
        }
        foreach (var spec in redSpecs)
        {
            var parts = spec.Split(':');
            int? tri = null;
            string keyText = spec;
            if (parts.Length >= 3 && int.TryParse(parts[^1], out var t))
            { tri = t; keyText = string.Join(':', parts[..^1]); }
            try { MarkRed(FormKey.Factory(keyText), tri); }
            catch { Console.Error.WriteLine($"--red '{spec}' is not FORMKEY[:tri]."); return 1; }
        }
        if (conflictFile is { } cf)
        {
            foreach (var c in cf.Conflicts)
                foreach (var rec in c.Necessary.Concat(c.Alternative ?? Array.Empty<HarnessShared.ConflictRecord>()))
                {
                    FormKey fk;
                    try { fk = FormKey.Factory(rec.FormKey); } catch { continue; }
                    if (rec.Role == "navmesh" && winNav.ContainsKey(fk)) MarkRed(fk, null);
                    else if (rec.Role == "door" && winRef.ContainsKey(fk)) conflictDoors.Add(fk);
                }
            int redHere = redNav.Count(kv => winNav.ContainsKey(kv.Key));
            Console.WriteLine($"   conflicts overlay: {redHere} navmesh record(s) in this cell painted red, "
                            + $"{conflictDoors.Count} door(s) highlighted (from {cf.Conflicts.Count} conflicts in {Path.GetFileName(conflictsPath)})");
            foreach (var dfk in conflictDoors)
                if (winRef.TryGetValue(dfk, out var dref))
                    Console.WriteLine($"   door {dfk} at {dref.Position.X:0},{dref.Position.Y:0},{dref.Position.Z:0}  (orange)");
            // "Not in the rendered cells" = not in the loaded ref set — the target cell plus its
            // ±1 ring when ring refs are on. A door in a ring cell renders and highlights like
            // one in the target cell; this message now fires only for doors beyond all of that.
            foreach (var dfk in doorWatchKeys)
                if (!winRef.ContainsKey(dfk))
                    Console.WriteLine(doorWhere.TryGetValue(dfk, out var dw)
                        ? $"   implicated door {dfk} is NOT in the rendered cell(s): it sits in {dw.CellEd ?? dw.Cell.ToString()} at {dw.Ref.Position.X:0},{dw.Ref.Position.Y:0},{dw.Ref.Position.Z:0}"
                        : $"   implicated door {dfk}: no placed ref with this FormKey anywhere in the load order");
            if (redHere == 0 && conflictDoors.Count == 0)
                Console.WriteLine($"   (no conflict in that file names a navmesh or door in THIS cell — nothing to paint)");
        }

        var scene = new List<ViewProbe.SceneRef>();
        int fxSkipped = 0, markerSkipped = 0;
        int deletedSkipped = 0, disabledFlagSkipped = 0, disabledParentSkipped = 0, enabledByParentDrawn = 0, noModel = 0;
        var meshCensus = listMeshes ? new Dictionary<string, (int Count, string Class)>(StringComparer.OrdinalIgnoreCase) : null;
        foreach (var r in winRef.Values)
        {
            if (r.Deleted) { deletedSkipped++; continue; }
            if (!RefEnabled(r.FormKey))
            {
                if (refEnableParent.ContainsKey(r.FormKey)) disabledParentSkipped++; else disabledFlagSkipped++;
                continue;
            }
            // The defect this counter watches: initially-disabled refs whose enable parent resolves
            // ENABLED at runtime — the standard way a mod swaps a whole set in. These used to be
            // silently dropped (a third of the Temple's content).
            if (r.InitiallyDisabled) enabledByParentDrawn++;
            if (r.Base is not { } bk || !baseMesh.TryGetValue(bk, out var rel)) { noModel++; continue; }
            // Effect meshes (beams, mists, glows) are translucent volumes that drown a first-person
            // shot; the game draws them subtly, this flat-lit pass cannot. Skipped by default,
            // counted, and --fx puts them back. This is the WHOLE-FILE rule only — a solid mesh that
            // merely CONTAINS effect shapes stays, and its effect shapes are dropped per-shape in
            // TexturedScene, which reports them on their own line (and names the meshes under
            // --list-meshes). So a census entry reading "geometry" means "not an effects\ file", not
            // "nothing in it is an effect".
            bool isMarker = baseIsMarker.Contains(bk);
            bool isFx = rel.Replace('/', '\\').Contains(@"effects\", StringComparison.OrdinalIgnoreCase);
            if (meshCensus is not null)
            {
                var cls = isMarker ? "MARKER" : isFx ? "FX-file" : "geometry";
                meshCensus[rel] = (meshCensus.TryGetValue(rel, out var c) ? c.Count + 1 : 1, cls);
            }
            if (!keepFx && isFx) { fxSkipped++; continue; }
            // Editor markers are not world geometry; the game never draws them. --markers keeps them
            // as a translucent extra layer instead of dropping them.
            if (isMarker && !showMarkers) { markerSkipped++; continue; }
            scene.Add(new ViewProbe.SceneRef(rel, r.Position, r.Rotation, r.Scale,
                                             (hiFp is { } h && r.FormKey == h) || conflictDoors.Contains(r.FormKey), isMarker,
                                             r.FormKey, bk, baseEditorId.TryGetValue(bk, out var bEd) ? bEd : null));
        }
        foreach (var (qx, qy, qr) in refsAt)
        {
            var near = winRef.Values
                .Select(r => (Ref: r, D: MathF.Sqrt((r.Position.X - qx) * (r.Position.X - qx) + (r.Position.Y - qy) * (r.Position.Y - qy))))
                .Where(x => x.D <= qr)
                .OrderBy(x => x.D)
                .ToList();
            Console.WriteLine($"   ---- refs within {qr:0} u of {qx:0},{qy:0}: {near.Count} ----");
            foreach (var (r, d) in near)
            {
                string why = r.Deleted ? "NOT DRAWN: deleted"
                           : DisabledWhy(r) is { } dw ? $"NOT DRAWN: {dw}"
                           : r.Base is not { } bk0 ? "NOT DRAWN: no base"
                           : !baseMesh.TryGetValue(bk0, out _) ? "NOT DRAWN: base carries no model"
                           : baseIsMarker.Contains(bk0) && !showMarkers ? "not drawn: editor marker (--markers)"
                           : baseMesh[bk0].Replace('/', (char)92).Contains("effects" + (char)92, StringComparison.OrdinalIgnoreCase) && !keepFx ? "not drawn: FX (--fx)"
                           : r.InitiallyDisabled ? "drawn (enable parent resolves ENABLED)"
                           : "drawn";
                string bse = r.Base is { } bk1
                    ? bk1 + (baseEditorId.TryGetValue(bk1, out var be) ? $" {be}" : "")
                      + (baseMesh.TryGetValue(bk1, out var bm) ? $"  {bm}" : "")
                    : "(none)";
                Console.WriteLine($"      {d,6:0} u  {r.FormKey}  at {r.Position.X:0},{r.Position.Y:0},{r.Position.Z:0}"
                                + $"  scale {r.Scale:0.00}  [{why}]  base {bse}");
            }
        }

        // Every ref the scene drops is accounted for HERE, in one block — a silent category is how a
        // render missing a third of its content once read as fully accounted for.
        Console.WriteLine($"   refs not drawn: {disabledParentSkipped:N0} disabled via enable parent, "
                        + $"{disabledFlagSkipped:N0} initially disabled (own flag, no parent), "
                        + $"{deletedSkipped:N0} deleted, {noModel:N0} with no model-bearing base");
        if (enabledByParentDrawn > 0)
            Console.WriteLine($"   {enabledByParentDrawn:N0} initially-disabled ref(s) DRAWN — their enable parent resolves ENABLED at runtime");
        if (fxSkipped > 0) Console.WriteLine($"   ({fxSkipped} effect-mesh refs skipped; --fx to include them)");
        if (markerSkipped > 0) Console.WriteLine($"   ({markerSkipped} editor-marker refs skipped; --markers to include them translucently)");
        if (meshCensus is not null)
        {
            Console.WriteLine($"   ---- distinct meshes in this cell ({meshCensus.Count}) ----");
            foreach (var kv in meshCensus.OrderByDescending(k => k.Value.Count))
                Console.WriteLine($"   {kv.Value.Count,5}  {kv.Value.Class,-8}  {kv.Key}");
        }

        // One navmesh SOURCE -> (green context, red finding) triangle sets. The red spec is applied
        // per source, so a single-triangle red lands on THAT version's triangle — in a compare, the
        // same record can be red-whole in every panel while its shape changes between them.
        (List<(System.Numerics.Vector3, System.Numerics.Vector3, System.Numerics.Vector3)> Green,
         List<(System.Numerics.Vector3, System.Numerics.Vector3, System.Numerics.Vector3)> Red)
        BuildNavSets(IReadOnlyDictionary<FormKey, NavmeshGeometry> navs)
        {
            var green = new List<(System.Numerics.Vector3, System.Numerics.Vector3, System.Numerics.Vector3)>();
            var red = new List<(System.Numerics.Vector3, System.Numerics.Vector3, System.Numerics.Vector3)>();
            if (navMode == "off") return (green, red);
            foreach (var nav in navs.Values)
            {
                var reds = redNav.TryGetValue(nav.FormKey, out var rset) ? rset : null;
                bool wholeRed = redNav.ContainsKey(nav.FormKey) && reds is null;
                for (int ti = 0; ti < nav.Triangles.Count; ti++)
                {
                    var tri = nav.Triangles[ti];
                    if ((tri.Flags & 0x08) != 0) continue;
                    if (tri.A < 0 || tri.B < 0 || tri.C < 0) continue;
                    if (tri.A >= nav.Vertices.Count || tri.B >= nav.Vertices.Count || tri.C >= nav.Vertices.Count) continue;
                    var t3 = (V(nav.Vertices[tri.A]), V(nav.Vertices[tri.B]), V(nav.Vertices[tri.C]));
                    if (wholeRed || (reds is not null && reds.Contains(ti))) red.Add(t3);
                    else green.Add(t3);
                }
            }
            return (green, red);
        }

        if (findLtex is not null)
        {
            string? Diffuse(FormKey lk)
            {
                if (!ltexToTxst.TryGetValue(lk, out var ts)) return null;
                if (!txstDiffuse.TryGetValue(ts, out var d)) return null;
                var pp = d.Replace('/', (char)92);
                return pp.StartsWith("textures", StringComparison.OrdinalIgnoreCase) ? pp : "textures" + (char)92 + pp;
            }
            var wanted = ltexEditorId
                .Where(kv => kv.Value.Contains(findLtex, StringComparison.OrdinalIgnoreCase))
                .ToDictionary(kv => kv.Key, kv => kv.Value);
            Console.WriteLine($"   ---- LTEX records matching '{findLtex}': {wanted.Count} ----");
            foreach (var (fk, ed) in wanted.OrderBy(k => k.Value))
                Console.WriteLine($"      {fk}  {ed}  ->  {Diffuse(fk) ?? "NO DIFFUSE RESOLVED"}");
            if (wanted.Count > 0)
            {
                // grid -> (painted points across all quadrants, whether any quadrant uses it as BASE)
                var hits = new Dictionary<(int X, int Y), (int Painted, bool Base, FormKey Cell, string? Ed)>();
                foreach (var path in order.OrderedPaths)
                {
                    ISkyrimModGetter ms;
                    try { ms = SkyrimMod.CreateFromBinaryOverlay(path, SkyrimRelease.SkyrimSE); } catch { continue; }
                    using (ms as IDisposable)
                        foreach (var ws in ms.Worldspaces.Records)
                        {
                            if (ws.FormKey != targetWorldspace) continue;
                            IEnumerable<ICellGetter> All()
                            {
                                if (ws.TopCell is { } tc) yield return tc;
                                foreach (var blk in ws.SubCells)
                                    foreach (var sub in blk.Items)
                                        foreach (var c in sub.Items)
                                            yield return c;
                            }
                            foreach (var c in All())
                            {
                                if (c.Grid?.Point is not { } gp || c.Landscape is not { } ls) continue;
                                int painted = 0; bool isBase = false;
                                try
                                {
                                    foreach (var layer in ls.Layers)
                                    {
                                        if (layer.Header?.Texture.FormKeyNullable is not { } lk || !wanted.ContainsKey(lk)) continue;
                                        if (layer is IAlphaLayerGetter al)
                                            painted += al.AlphaLayerData.Count(pt => pt.Opacity > 0.004f);
                                        else { isBase = true; painted += 289; }
                                    }
                                }
                                catch { }
                                // A later plugin's LAND for the same cell REPLACES the earlier one, so
                                // overwrite rather than accumulate — same winner rule as the terrain walk.
                                if (painted > 0 || isBase) hits[(gp.X, gp.Y)] = (painted, isBase, c.FormKey, c.EditorID);
                                else hits.Remove((gp.X, gp.Y));
                            }
                        }
                }
                Console.WriteLine($"   ---- cells painting it, top 20 of {hits.Count} ----");
                foreach (var ((gx, gy), (painted, isBase, ck, ced)) in hits.OrderByDescending(k => k.Value.Painted).Take(20))
                    Console.WriteLine($"      cell ({gx,4},{gy,4})  {painted,5} painted point(s){(isBase ? "  (BASE)" : "      ")}"
                                    + $"  centre {gx * 4096 + 2048},{gy * 4096 + 2048}"
                                    + $"  {ck}{(string.IsNullOrWhiteSpace(ced) ? "" : "  " + ced)}");
            }
            return 0;
        }

        // ---- TERRAIN: winning LAND heightmaps for the target cell + its ring-1 neighbours --------
        // The placed rocks were floating in space without this; exteriors get actual ground now.
        // texture path ("" = none, off-white snow fallback) -> that patch's triangles.
        var terrainPatches = new Dictionary<string, List<(System.Numerics.Vector3, System.Numerics.Vector3, System.Numerics.Vector3)>>(StringComparer.OrdinalIgnoreCase);
        // Painted layers, in draw order: diffuse path + triangles carrying a per-vertex opacity.
        var terrainLayers = new List<(string Diffuse, List<(System.Numerics.Vector3 A, System.Numerics.Vector3 B, System.Numerics.Vector3 C, float Oa, float Ob, float Oc)> Tris)>();
        // The same decoded heightmaps, kept as a queryable field: the navmesh overlay asks it
        // whether a triangle is BELOW the ground that gets drawn, which is what separates "buried"
        // from "behind a rock" and earns the two different dashed layers.
        var landHeights = new Dictionary<(int X, int Y), float[,]>();
        // --interactive: hovering bare ground answers with the quadrant's painted layers. Assigned
        // inside the terrain block (it captures the walked LAND census); null on interiors.
        Func<float, float, string?>? terrainDescribeAt = null;
        // Water rides this same cell walk (it is where CELL.WaterHeight is read), so the walk has
        // to run whenever EITHER is wanted — otherwise --no-terrain silently takes the sea with it,
        // which is a coupling nobody would guess from the flag's name.
        if ((drawTerrain || drawWater) && !interior && targetGrid is { } tg)
        {
            // grid -> winning heightmap + the base landscape texture per quadrant (LTEX FormKeys,
            // resolved to diffuse paths after the walk), by load order (later plugin wins).
            var landWin = new Dictionary<(int X, int Y), (float Offset, sbyte[,] Grad, FormKey?[] QuadLtex)>();
            // grid -> every layer the WINNING LAND for that cell carries, base and alpha alike.
            var landLayers = new Dictionary<(int X, int Y), List<(int Quadrant, int Layer, bool Alpha, FormKey? Tex, int Painted, float MaxOpacity, float[]? Opacity)>>();
            // Per cell, per quadrant: the alpha layer promoted to stand in for a MISSING base layer
            // (-1 = the quadrant had a real base). A quadrant with no BTXT states no ground at all;
            // the honest stand-in is the layer that covers most of it, not a blank slab.
            var landPromoted = new Dictionary<(int X, int Y), int[]>();
            foreach (var path in order.OrderedPaths)
            {
                ISkyrimModGetter m2;
                try { m2 = SkyrimMod.CreateFromBinaryOverlay(path, SkyrimRelease.SkyrimSE); } catch { continue; }
                using (m2 as IDisposable)
                {
                    foreach (var ws in m2.Worldspaces.Records)
                    {
                        if (ws.FormKey != targetWorldspace) continue;
                        // The worldspace's own water, and the height its cells fall back to. Read
                        // per plugin in load order, so the last one to state either of them wins.
                        if (ws.Water.FormKeyNullable is { } wsw && !wsw.IsNull) wsWater = wsw;
                        if (ws.LandDefaults?.DefaultWaterHeight is { } dwh && RealHeight(dwh)) wsDefaultWaterHeight = dwh;
                        IEnumerable<ICellGetter> Cells()
                        {
                            if (ws.TopCell is { } tc) yield return tc;
                            foreach (var blk in ws.SubCells)
                                foreach (var sub in blk.Items)
                                    foreach (var c in sub.Items)
                                        yield return c;
                        }
                        foreach (var c in Cells())
                        {
                            if (c.Grid?.Point is not { } gp) continue;
                            if (Math.Abs(gp.X - tg.X) > 1 || Math.Abs(gp.Y - tg.Y) > 1) continue;
                            // Water is harvested BEFORE the LAND gate below: a cell can be all
                            // sea with no heightmap at all, and that is exactly the cell whose
                            // water matters most.
                            cellWater[(gp.X, gp.Y)] = (c.Flags.HasFlag(Cell.Flag.HasWater),
                                                       c.WaterHeight ?? float.MaxValue,
                                                       c.Water.FormKeyNullable is { } cw && !cw.IsNull ? cw : null);
                            if (c.Landscape?.VertexHeightMap is not { } vh) continue;
                            var grad = new sbyte[33, 33];
                            try
                            {
                                for (int y = 0; y < 33; y++)
                                    for (int x = 0; x < 33; x++)
                                        grad[x, y] = vh.HeightMap[x, y];
                            }
                            catch { continue; }
                            // Base layer per quadrant (a non-alpha layer names the quadrant's ground
                            // texture; alpha layers blend on top and are out of scope for this pass).
                            var quad = new FormKey?[4];
                            var census = new List<(int Quadrant, int Layer, bool Alpha, FormKey? Tex, int Painted, float MaxOpacity, float[]? Opacity)>();
                            var promoted = new[] { -1, -1, -1, -1 };
                            try
                            {
                                foreach (var layer in c.Landscape.Layers)
                                {
                                    int qi = layer.Header is { } lh ? (int)lh.Quadrant : -1;
                                    var tex = layer.Header?.Texture.FormKeyNullable;
                                    if (layer is IAlphaLayerGetter al)
                                    {
                                        // 17x17 opacity points per quadrant, addressed by Position.
                                        var op = new float[289];
                                        int painted = 0; float maxOp = 0f;
                                        foreach (var pt in al.AlphaLayerData)
                                        {
                                            if (pt.Position < op.Length) op[pt.Position] = pt.Opacity;
                                            if (pt.Opacity > 0.004f) { painted++; if (pt.Opacity > maxOp) maxOp = pt.Opacity; }
                                        }
                                        census.Add((qi, layer.Header?.LayerNumber ?? -1, true, tex, painted, maxOp, op));
                                        continue;
                                    }
                                    census.Add((qi, layer.Header?.LayerNumber ?? -1, false, tex, 289, 1f, null));
                                    if (qi is >= 0 and < 4 && quad[qi] is null && tex is { } lf) quad[qi] = lf;
                                }
                                // No base layer for a quadrant: stand in the alpha layer that paints the
                                // most of it, and remember which, so it is not also drawn on top of itself.
                                // Gated on the same switch as the overlays — --no-terrain-layers means
                                // "ignore the alpha layers entirely", base stand-ins included, which is
                                // what makes it a clean revert to the base-only picture.
                                for (int q = 0; drawTerrainLayers && q < 4; q++)
                                {
                                    if (quad[q] is not null) continue;
                                    var best = census.Where(l => l.Alpha && l.Quadrant == q && l.Tex is not null)
                                                     .OrderByDescending(l => l.Painted).ToList();
                                    if (best.Count > 0 && best[0].Tex is { } bt) { quad[q] = bt; promoted[q] = best[0].Layer; }
                                }
                            }
                            catch { }
                            landWin[(gp.X, gp.Y)] = (vh.Offset, grad, quad);   // later plugin overwrites = wins
                            landLayers[(gp.X, gp.Y)] = census;
                            landPromoted[(gp.X, gp.Y)] = promoted;
                        }
                    }
                }
            }

            string? QuadDiffuse(FormKey? ltex)
            {
                if (ltex is not { } lk) return null;
                if (!ltexToTxst.TryGetValue(lk, out var ts)) return null;
                if (!txstDiffuse.TryGetValue(ts, out var d)) return null;
                var p = d.Replace('/', '\\');
                return p.StartsWith("textures\\", StringComparison.OrdinalIgnoreCase) ? p : "textures\\" + p;
            }

            int promotedQuads = 0;
            foreach (var pr in landPromoted.Values) promotedQuads += pr.Count(v => v >= 0);
            if (promotedQuads > 0)
                Console.WriteLine($"   terrain: {promotedQuads} quadrant(s) carry no base layer — "
                                + $"stood in with their most-painted alpha layer");

            foreach (var ((gx, gy), (off, grad, quadLtex)) in landWin)
            {
                // VHGT decode: row-start accumulator down the first column, then run along each row;
                // stored values are gradients in 1/8 world units.
                var h = new float[33, 33];
                float rowStart = off;
                for (int y = 0; y < 33; y++)
                {
                    rowStart += grad[0, y];
                    float acc = rowStart;
                    h[0, y] = acc;
                    for (int x = 1; x < 33; x++) { acc += grad[x, y]; h[x, y] = acc; }
                }
                landHeights[(gx, gy)] = h;
                System.Numerics.Vector3 P(int x, int y) =>
                    new(gx * 4096f + x * 128f, gy * 4096f + y * 128f, h[x, y] * 8f);
                // landHeights is kept either way — water's depth shading reads it through
                // terrainHeightAt — but the drawable triangles are terrain's own output.
                if (!drawTerrain) continue;
                for (int y = 0; y < 32; y++)
                    for (int x = 0; x < 32; x++)
                    {
                        // Quadrant enum order: BottomLeft, BottomRight, TopLeft, TopRight.
                        int qi = (y < 16 ? 0 : 2) + (x < 16 ? 0 : 1);
                        var tex = QuadDiffuse(quadLtex[qi]);
                        if (!terrainPatches.TryGetValue(tex ?? "", out var bag))
                            terrainPatches[tex ?? ""] = bag = new List<(System.Numerics.Vector3, System.Numerics.Vector3, System.Numerics.Vector3)>();
                        bag.Add((P(x, y), P(x + 1, y), P(x + 1, y + 1)));
                        bag.Add((P(x, y), P(x + 1, y + 1), P(x, y + 1)));
                    }

                // ---- the painted layers, in the record's own order ---------------------------
                // Each alpha layer is its own sheet over the base: the layer's texture, the layer's
                // per-vertex opacity in vertex alpha. Triangles nobody painted are dropped, which is
                // most of them — a road is a thin ribbon across a 16x16 quadrant.
                if (!drawTerrainLayers || !landLayers.TryGetValue((gx, gy), out var lyrs)) continue;
                var promotedHere = landPromoted.TryGetValue((gx, gy), out var pq) ? pq : new[] { -1, -1, -1, -1 };
                foreach (var l in lyrs.Where(l => l.Alpha && l.Opacity is not null && l.Quadrant is >= 0 and < 4)
                                      .OrderBy(l => l.Layer))
                {
                    if (promotedHere[l.Quadrant] == l.Layer) continue;      // it IS the base here
                    if (QuadDiffuse(l.Tex) is not { } lyrTex) continue;     // no resolvable texture: skip
                    int qx0 = (l.Quadrant & 1) * 16, qy0 = l.Quadrant >= 2 ? 16 : 0;
                    var op = l.Opacity!;
                    float Op(int lx, int ly) { int i = ly * 17 + lx; return i >= 0 && i < op.Length ? op[i] : 0f; }
                    var tris = new List<(System.Numerics.Vector3 A, System.Numerics.Vector3 B, System.Numerics.Vector3 C, float Oa, float Ob, float Oc)>();
                    for (int ly = 0; ly < 16; ly++)
                        for (int lx = 0; lx < 16; lx++)
                        {
                            float o00 = Op(lx, ly), o10 = Op(lx + 1, ly), o11 = Op(lx + 1, ly + 1), o01 = Op(lx, ly + 1);
                            if (o00 <= 0.004f && o10 <= 0.004f && o11 <= 0.004f && o01 <= 0.004f) continue;
                            int x = qx0 + lx, y = qy0 + ly;
                            tris.Add((P(x, y), P(x + 1, y), P(x + 1, y + 1), o00, o10, o11));
                            tris.Add((P(x, y), P(x + 1, y + 1), P(x, y + 1), o00, o11, o01));
                        }
                    if (tris.Count > 0) terrainLayers.Add((lyrTex, tris));
                }
            }
            // VP_ALPHA_DUMP=gx,gy prints every alpha layer of that cell as a 17x17 opacity grid, in
            // the SAME orientation the geometry builder reads it (lx fastest, ly down). It exists to
            // settle one question that no amount of looking at a render can: whether Position is
            // decoded x-fastest or y-fastest. A transposed map still draws a road-shaped ribbon —
            // it just draws it mirrored — so the test is CONTINUITY at a quadrant seam: TopLeft's
            // last column must agree with TopRight's first column where a paint crosses between them.
            if (Environment.GetEnvironmentVariable("VP_ALPHA_DUMP") is { } dumpSpec)
            {
                var ds = dumpSpec.Split(',');
                if (ds.Length == 2 && int.TryParse(ds[0], out int dgx) && int.TryParse(ds[1], out int dgy)
                    && landLayers.TryGetValue((dgx, dgy), out var dl))
                {
                    var qn = new[] { "BottomLeft", "BottomRight", "TopLeft", "TopRight" };
                    foreach (var l in dl.Where(l => l.Alpha && l.Opacity is not null).OrderBy(l => l.Quadrant).ThenBy(l => l.Layer))
                    {
                        Console.WriteLine($"   ---- cell ({dgx},{dgy}) {qn[Math.Clamp(l.Quadrant, 0, 3)]} alpha #{l.Layer} "
                                        + $"{(l.Tex is { } lk && ltexEditorId.TryGetValue(lk, out var le) ? le : "?")} ----");
                        for (int ly = 16; ly >= 0; ly--)     // print +Y upward, like a map
                        {
                            var sb = new System.Text.StringBuilder($"   ly{ly,2} ");
                            for (int lx = 0; lx <= 16; lx++)
                            {
                                float o = l.Opacity![ly * 17 + lx];
                                sb.Append(o <= 0.004f ? '.' : o >= 0.95f ? '#' : o >= 0.5f ? '+' : o >= 0.2f ? 'o' : ':');
                            }
                            Console.WriteLine(sb.ToString());
                        }
                    }
                }
            }
            if (listTerrain)
            {
                Console.WriteLine($"   ---- landscape layers, winning LAND per cell ({landLayers.Count} cell(s)) ----");
                string TexName(FormKey? fk)
                    => fk is not { } k ? "(none)"
                     : (ltexEditorId.TryGetValue(k, out var ed) ? ed : k.ToString())
                       + " -> " + (QuadDiffuse(k) ?? "NO DIFFUSE RESOLVED");
                foreach (var (grid, census) in landLayers.OrderBy(k => k.Key.Y).ThenBy(k => k.Key.X))
                {
                    var names = new[] { "BottomLeft", "BottomRight", "TopLeft", "TopRight" };
                    Console.WriteLine($"   cell ({grid.X},{grid.Y}){(grid.X == tg.X && grid.Y == tg.Y ? "  <- TARGET" : "")}");
                    for (int q = 0; q < 4; q++)
                    {
                        var inQ = census.Where(l => l.Quadrant == q).OrderBy(l => l.Alpha).ThenBy(l => l.Layer).ToList();
                        if (inQ.Count == 0) { Console.WriteLine($"      {names[q],-12} (no layers at all)"); continue; }
                        foreach (var l in inQ)
                            Console.WriteLine($"      {names[q],-12} {(l.Alpha ? $"alpha #{l.Layer,-2}" : "BASE      ")}"
                                            + $" {(l.Alpha ? $"{l.Painted,3}/289 pts, max {l.MaxOpacity:0.00}  " : "                     ")}"
                                            + TexName(l.Tex));
                    }
                }
            }
            int terrTotal = terrainPatches.Values.Sum(v => v.Count);
            if (terrTotal > 0)
                Console.WriteLine($"   terrain: {landWin.Count} cell(s) of LAND, {terrTotal:N0} triangles, "
                                + $"{terrainPatches.Count(kv => kv.Key.Length > 0)} distinct ground texture(s)");
            else
                Console.WriteLine($"   terrain: no LAND found for this cell (--no-terrain to silence)");
            if (terrainLayers.Count > 0)
                Console.WriteLine($"   terrain paint layers: {terrainLayers.Count} sheet(s), "
                                + $"{terrainLayers.Sum(l => l.Tris.Count):N0} triangles, "
                                + $"{terrainLayers.Select(l => l.Diffuse).Distinct(StringComparer.OrdinalIgnoreCase).Count()} distinct texture(s)");

            // A hover on bare ground has a real answer in the LAND census: which cell, which
            // quadrant, what base texture, and what each alpha layer paints AT THAT POINT (opacity
            // sampled from its own 17x17 map — the same decode the geometry uses).
            if (interactiveMode)
            {
                var qNames = new[] { "BottomLeft", "BottomRight", "TopLeft", "TopRight" };
                terrainDescribeAt = (wx, wy) =>
                {
                    int gx = (int)MathF.Floor(wx / 4096f), gy = (int)MathF.Floor(wy / 4096f);
                    if (!landLayers.TryGetValue((gx, gy), out var lyrs2)) return $"cell ({gx},{gy}) — no LAND";
                    float lxu = wx - gx * 4096f, lyu = wy - gy * 4096f;
                    int q = (lyu < 2048f ? 0 : 2) + (lxu < 2048f ? 0 : 1);
                    int px2 = Math.Clamp((int)MathF.Round((lxu - (q & 1) * 2048f) / 128f), 0, 16);
                    int py2 = Math.Clamp((int)MathF.Round((lyu - (q >= 2 ? 2048f : 0f)) / 128f), 0, 16);
                    string LtexName(FormKey? fk) => fk is { } k
                        ? (ltexEditorId.TryGetValue(k, out var ed) ? ed : k.ToString()) : "(none)";
                    var baseL = lyrs2.FirstOrDefault(l => !l.Alpha && l.Quadrant == q);
                    var painted = lyrs2
                        .Where(l => l.Alpha && l.Quadrant == q && l.Opacity is not null
                                    && l.Opacity[py2 * 17 + px2] > 0.004f)
                        .OrderBy(l => l.Layer)
                        .Select(l => $"{LtexName(l.Tex)} {l.Opacity![py2 * 17 + px2]:0.00}")
                        .ToList();
                    return $"cell ({gx},{gy}) {qNames[q]} — base {LtexName(baseL.Tex)}"
                         + (painted.Count > 0 ? $" — painted: {string.Join(", ", painted)}" : "");
                };
            }
        }

        // Ground height at a world X,Y, on the SAME 128-unit quads and the SAME diagonal split the
        // terrain mesh is built from — so "below the surface" here means below the surface that was
        // actually drawn, not below some second opinion about where the ground is. Null outside the
        // ring of cells that has LAND, which leaves those segments to the depth test instead.
        Func<float, float, float?>? terrainHeightAt = null;
        if (landHeights.Count > 0)
            terrainHeightAt = (wx, wy) =>
            {
                int gx = (int)MathF.Floor(wx / 4096f), gy = (int)MathF.Floor(wy / 4096f);
                if (!landHeights.TryGetValue((gx, gy), out var h)) return null;
                float fx = (wx - gx * 4096f) / 128f, fy = (wy - gy * 4096f) / 128f;
                int ix = Math.Clamp((int)MathF.Floor(fx), 0, 31), iy = Math.Clamp((int)MathF.Floor(fy), 0, 31);
                float u = fx - ix, v = fy - iy;
                float h00 = h[ix, iy], h10 = h[ix + 1, iy], h11 = h[ix + 1, iy + 1], h01 = h[ix, iy + 1];
                // Lower triangle (x,y)-(x+1,y)-(x+1,y+1) when u >= v, upper one otherwise.
                float z = u >= v ? h00 + (h10 - h00) * u + (h11 - h10) * v
                                 : h00 + (h11 - h01) * u + (h01 - h00) * v;
                return z * 8f;
            };

        // What the render DROPS, said out loud: a triangle the picture never shows because it was
        // filtered is indistinguishable from one that does not exist, unless the counts are printed.
        {
            int total = 0, deleted = 0, badIndex = 0;
            foreach (var nav in winNav.Values)
                foreach (var tri in nav.Triangles)
                {
                    total++;
                    if ((tri.Flags & 0x08) != 0) { deleted++; continue; }
                    if (tri.A < 0 || tri.B < 0 || tri.C < 0
                        || tri.A >= nav.Vertices.Count || tri.B >= nav.Vertices.Count || tri.C >= nav.Vertices.Count)
                        badIndex++;
                }
            Console.WriteLine($"   navmesh triangles: {total:N0} in the winning records, "
                            + $"{deleted:N0} flagged DELETED (0x08), {badIndex:N0} with out-of-range vertex indices, "
                            + $"{total - deleted - badIndex:N0} drawable");
        }

        foreach (var (qx, qy) in navAt)
        {
            int hits = 0;
            foreach (var nav in winNav.Values)
                for (int ti = 0; ti < nav.Triangles.Count; ti++)
                {
                    var tri = nav.Triangles[ti];
                    if (tri.A < 0 || tri.B < 0 || tri.C < 0
                        || tri.A >= nav.Vertices.Count || tri.B >= nav.Vertices.Count || tri.C >= nav.Vertices.Count) continue;
                    var a = V(nav.Vertices[tri.A]); var b = V(nav.Vertices[tri.B]); var c = V(nav.Vertices[tri.C]);
                    // 2D barycentric containment; the navmesh is a surface, so XY is the right test.
                    float d = (b.Y - c.Y) * (a.X - c.X) + (c.X - b.X) * (a.Y - c.Y);
                    if (MathF.Abs(d) < 1e-6f) continue;
                    float u = ((b.Y - c.Y) * (qx - c.X) + (c.X - b.X) * (qy - c.Y)) / d;
                    float v = ((c.Y - a.Y) * (qx - c.X) + (a.X - c.X) * (qy - c.Y)) / d;
                    float w = 1f - u - v;
                    if (u < 0f || v < 0f || w < 0f) continue;
                    float z = u * a.Z + v * b.Z + w * c.Z;
                    string why = (tri.Flags & 0x08) != 0 ? "NOT DRAWN: flagged deleted (0x08)" : "drawn";
                    Console.WriteLine($"   nav-at {qx:0},{qy:0}: {nav.FormKey} tri {ti} covers it, z {z:0}, "
                                    + $"flags 0x{tri.Flags:X} — {why}");
                    hits++;
                }
            if (hits == 0)
            {
                // Nothing covers it: say how far the nearest triangle is, so "no navmesh here" can be
                // told apart from "just outside a triangle".
                float best = float.MaxValue; FormKey bk = FormKey.Null; int bi = -1;
                foreach (var nav in winNav.Values)
                    for (int ti = 0; ti < nav.Triangles.Count; ti++)
                    {
                        var tri = nav.Triangles[ti];
                        if (tri.A < 0 || tri.B < 0 || tri.C < 0
                            || tri.A >= nav.Vertices.Count || tri.B >= nav.Vertices.Count || tri.C >= nav.Vertices.Count) continue;
                        var a = V(nav.Vertices[tri.A]); var b = V(nav.Vertices[tri.B]); var c = V(nav.Vertices[tri.C]);
                        var cen = (a + b + c) / 3f;
                        float dd = MathF.Sqrt((cen.X - qx) * (cen.X - qx) + (cen.Y - qy) * (cen.Y - qy));
                        if (dd < best) { best = dd; bk = nav.FormKey; bi = ti; }
                    }
                Console.WriteLine($"   nav-at {qx:0},{qy:0}: NO winning triangle covers this point"
                                + (bi >= 0 ? $"; nearest is {bk} tri {bi}, {best:0} u away (centroid)" : "; no navmesh at all in this cell"));
            }
        }

        // ---- the route: resolve every waypoint's z from the winning navmesh, then check it ----
        //
        // Z comes from the MESH, not from the file and not from a pick, for two reasons. A route
        // has to sit on WALKABLE ground rather than on whatever surface happened to be under the
        // point; and letting the mesh supply the height makes the arrows follow a sloping walkway
        // for free, with the author supplying nothing but a plan-view X,Y.
        //
        // The reference height CHAINS: waypoint 1 is resolved against the shot's own floor, and
        // each one after it against the height the PREVIOUS waypoint resolved to. A single fixed
        // reference would pick the wrong storey the moment a route climbed a staircase; chaining
        // walks up with it, since consecutive waypoints are far closer in z than the 192 u that
        // separates one floor from the next.
        ViewProbe.TexturedScene.RouteRender? routeRender = null;
        if (shotRoute.Count > 1)
        {
            var pts = new List<System.Numerics.Vector3>(shotRoute.Count);
            var offMesh = new List<string>();
            var tight = new List<string>();
            // --route-z beats the shot's measured floor beats eye-minus-eye-height. The last of
            // those is only sound for a camera STANDING on the route's own floor: from a top-down
            // a thousand units up it is nonsense, and it decides which storey waypoint 1 lands on.
            float refZ = routeZ ?? shotFloorZ ?? (eyePos.Value.Z - eyeHeight);
            string refWhy = routeZ is not null ? "stated: --route-z"
                          : shotFloorZ is not null ? "measured, from the shot's floorZ"
                          : $"ASSUMED: eye minus {eyeHeight:0} — pass --route-z if this camera is not standing on the route's floor";
            Console.WriteLine($"   route: {shotRoute.Count} waypoint(s)"
                            + (routeLabel.Length > 0 ? $" — {routeLabel}" : "")
                            + $"; floor reference z {refZ:0} ({refWhy})");
            for (int wi = 0; wi < shotRoute.Count; wi++)
            {
                var (wx, wy) = shotRoute[wi];
                float? z = ViewProbe.BatEmit.NearestCoverZ(winNav, wx, wy, refZ);
                // A multi-storey spot is where a route silently lands on the wrong floor, so the
                // OTHER candidates are printed rather than discarded. --nav-at says the same thing;
                // this says it about the height that was actually taken.
                var alts = ViewProbe.BatEmit.CoverHeights(winNav, wx, wy).Where(h => z is null || MathF.Abs(h - z.Value) > 1f).ToList();
                // The SAME five-probe test --emit-bats runs, not a second copy of it: the spot a
                // bat file calls standable and the spot the arrows walk over cannot disagree.
                var clear = ViewProbe.BatEmit.Clearance(winNav, wx, wy, z ?? refZ);
                string state = clear.Covered == 0 ? "OFF-MESH" : clear.Covered < 5 ? $"TIGHT {clear.Covered}/5" : "covered 5/5";
                Console.WriteLine($"      wpt {wi + 1}: {wx:0},{wy:0}"
                                + (z is { } zz ? $" -> z {zz:0}" : " -> NO z (nothing covers it)")
                                + $"  {state} — {clear.Why}"
                                + (alts.Count > 0 ? $"  [also covered at z {string.Join(", ", alts.Select(h => h.ToString("0")))} — nearest to the reference won]" : ""));
                if (clear.Covered == 0 || z is null) offMesh.Add($"wpt {wi + 1} ({wx:0},{wy:0}): {clear.Why}");
                else if (clear.Covered < 5) tight.Add($"wpt {wi + 1} ({wx:0},{wy:0}): {clear.Why}");
                pts.Add(new System.Numerics.Vector3(wx, wy, z ?? refZ));
                if (z is { } nz) refZ = nz;                       // the chain
            }
            // OFF-MESH IS FATAL, and this is the one place that differs from --emit-bats, which
            // writes a tight position as authored and warns. A route drawn through unwalkable
            // ground is worse than no route at all: it is a picture that tells a tester to walk
            // somewhere the game will not let them, and it looks authoritative while doing it.
            if (offMesh.Count > 0)
            {
                Console.Error.WriteLine($"   route REFUSED: {offMesh.Count} of {shotRoute.Count} waypoint(s) are not on walkable navmesh."
                                      + " A path over unwalkable ground looks authoritative and is not — fix the waypoints, or use --nav-at to find ground that is.");
                foreach (var m in offMesh) Console.Error.WriteLine($"      {m}");
                return 1;
            }
            if (tight.Count > 0)
            {
                Console.WriteLine($"   route: {tight.Count} TIGHT waypoint(s) — on navmesh, but the capsule may clip a wall there:");
                foreach (var m in tight) Console.WriteLine($"      {m}");
            }

            // ---- the legs: every segment sampled onto the ground, and CHECKED along its length --
            //
            // Checking the waypoints alone was defensible while a route carried one every couple of
            // hundred units. It is not once waypoints mark only direction changes and sight-line
            // breaks, because then a two-waypoint leg can be 700 u of ground nobody looked at, and
            // "off-mesh is a hard error" would be guarding the two ends of a line that crosses a
            // chasm in the middle.
            //
            // The same walk supplies the drawn geometry. A leg is straight IN PLAN and takes its z
            // from the mesh at every sample, so it rides an arch or a staircase instead of cutting
            // the chord — which on this project's own test walkway passes ~50 u under the surface.
            const float SampleEvery = 32f;
            var legs = new List<IReadOnlyList<System.Numerics.Vector3>>();
            var strayed = new List<string>();
            int sampleCount = 0, offSamples = 0;
            for (int li = 0; li + 1 < pts.Count; li++)
            {
                var a = pts[li]; var b = pts[li + 1];
                float planLen = MathF.Sqrt((b.X - a.X) * (b.X - a.X) + (b.Y - a.Y) * (b.Y - a.Y));
                int steps = Math.Clamp((int)MathF.Ceiling(planLen / SampleEvery), 1, 512);
                var leg = new List<System.Numerics.Vector3>(steps + 1);
                int legOff = 0; float firstOffAt = 0f;
                for (int si = 0; si <= steps; si++)
                {
                    float f = (float)si / steps;
                    float x = a.X + (b.X - a.X) * f, y = a.Y + (b.Y - a.Y) * f;
                    // The chord is the REFERENCE, not the answer: it says which storey to look on,
                    // and the mesh says how high the ground actually is there.
                    float chordZ = a.Z + (b.Z - a.Z) * f;
                    float? gz = ViewProbe.BatEmit.NearestCoverZ(winNav, x, y, chordZ);
                    bool onFloor = gz is { } g && MathF.Abs(g - chordZ) <= ViewProbe.BatEmit.SameFloorZ;
                    if (!onFloor)
                    {
                        if (legOff == 0) firstOffAt = planLen * f;
                        legOff++;
                    }
                    leg.Add(new System.Numerics.Vector3(x, y, onFloor ? gz!.Value : chordZ));
                }
                sampleCount += leg.Count; offSamples += legOff;
                if (legOff > 0)
                    strayed.Add($"leg {li + 1} (wpt {li + 1} -> {li + 2}, {planLen:0} u): {legOff} of {steps + 1} samples "
                              + $"are off walkable ground, first at {firstOffAt:0} u along it");
                legs.Add(leg);
            }
            Console.WriteLine($"   route: {legs.Count} leg(s) sampled every {SampleEvery:0} u onto the winning navmesh "
                            + $"— {sampleCount:N0} point(s), {sampleCount - offSamples:N0} on walkable ground");
            // Same rule as an off-mesh waypoint, and for the same reason: a line drawn across
            // ground the game will not let anyone walk looks exactly as authoritative as one that
            // is right. The waypoints being fine is not the claim the picture makes.
            if (strayed.Count > 0)
            {
                Console.Error.WriteLine($"   route REFUSED: {strayed.Count} leg(s) leave walkable navmesh BETWEEN their waypoints."
                                      + " The waypoints are fine; the straight line between them is not — add a waypoint where the"
                                      + " walkable ground actually turns, or move one.");
                foreach (var m in strayed) Console.Error.WriteLine($"      {m}");
                return 1;
            }
            routeRender = new ViewProbe.TexturedScene.RouteRender(legs, pts, routeColor, routeDiscs, routeArrowEvery);
        }

        var (navTris, redTris) = BuildNavSets(winNav);

        if (redTris.Count > 0)
        {
            var cen = System.Numerics.Vector3.Zero;
            foreach (var (a, b, c) in redTris) cen += (a + b + c) / 3f;
            cen /= redTris.Count;
            Console.WriteLine($"   red navmesh centroid: {cen.X:0},{cen.Y:0},{cen.Z:0}  ({redTris.Count} triangles)");
            Console.WriteLine($"   suggested view:  --eye {cen.X:0},{cen.Y - 450:0},{cen.Z + 260:0} --look {cen.X:0},{cen.Y:0},{cen.Z:0}");
        }

        var modFolders = comp.EnabledMods.Select(m => Path.Combine(modsDir, m)).ToList();

        // Runtime FormIDs for captions: the load-order prefix each plugin gets in THIS profile
        // (ESL-flagged plugins share the FE space). Console-clickable numbers, straight in the image.
        var fidPrefix = new Dictionary<string, uint>(StringComparer.OrdinalIgnoreCase);
        {
            int full = 0, light = 0;
            foreach (var pp in order.OrderedPaths)
            {
                bool esl = pp.EndsWith(".esl", StringComparison.OrdinalIgnoreCase);
                if (!esl)
                {
                    try
                    {
                        using var fs = System.IO.File.OpenRead(pp);
                        Span<byte> hdr = stackalloc byte[12];
                        if (fs.Read(hdr) == 12) esl = (BitConverter.ToUInt32(hdr[8..]) & 0x200) != 0;
                    }
                    catch { }
                }
                var name = Path.GetFileName(pp);
                if (esl) fidPrefix[name] = 0xFE000000u | ((uint)light++ << 12);
                else fidPrefix[name] = (uint)full++ << 24;
            }
        }
        string RuntimeId(FormKey fk)
            => fidPrefix.TryGetValue(fk.ModKey.FileName.String, out var pre)
                ? ((pre & 0xFE000000u) == 0xFE000000u ? (pre | (fk.ID & 0xFFFu)) : (pre | fk.ID)).ToString("X8")
                : "??" + fk.ID.ToString("X6");

        // A render missing its roof must SAY the roof was cut, or the absence reads as a finding.
        string CullCaption() => cullAbove is { } cca ? $" — SLICED above z {cca:0}" : "";

        string? DoorCaption()
            => conflictDoors.Count == 0 ? null
             : "ORANGE door " + string.Join("   ", conflictDoors.Select(fk => $"{fk} = FormID {RuntimeId(fk)}"));

        string RedCaption(IReadOnlyDictionary<FormKey, NavmeshGeometry> navs)
            => string.Join("   ", redNav.Keys.Select(fk =>
                   $"RED {fk} = FormID {RuntimeId(fk)}" +
                   (navs.TryGetValue(fk, out var nv) ? $" ({nv.Triangles.Count} tris)" : " (not provided)")));

        // ---- --npc: pin each spec to (NPC base record, world placement) -------------------
        // The spec FormKey may be the placed ACHR (a console click gives the ref) or the NPC_
        // base; the load walk watched for both. Explicit @X,Y,Z is the "should be HERE" case
        // and overrides the ref's own placement; explicit heading is degrees, `getangle z`
        // convention. No placement anywhere and no @position is an error, not a guess.
        var npcPlacements = new List<(FormKey NpcKey, ViewProbe.NpcPlacement Placement, string LabelHint)>();
        foreach (var (key, pos, headingDeg, label, raw) in npcParsed)
        {
            FormKey npcKey = key;
            System.Numerics.Vector3 p = default;
            Vec3 rot = default;
            float refScale = 1f;
            bool placed = false;
            if (npcRefWhere.TryGetValue(key, out var byRef))
            {
                if (byRef.Ref.Base is not { } nb)
                { Console.Error.WriteLine($"--npc '{raw}': placed ref {key} has no base NPC."); return 1; }
                npcKey = nb;
                p = new(byRef.Ref.Position.X, byRef.Ref.Position.Y, byRef.Ref.Position.Z);
                rot = byRef.Ref.Rotation;
                refScale = byRef.Ref.Scale;
                placed = true;
                if (byRef.Cell != cellKey)
                    Console.WriteLine($"   [npc] ref {key} lives in {(byRef.CellEd ?? byRef.Cell.ToString())}, not the rendered cell");
            }
            else if (npcBaseWhere.TryGetValue(key, out var instances) && instances.Count > 0)
            {
                var all = instances.Values.ToList();
                var inCell = all.Where(x => x.Cell == cellKey).ToList();
                var pick = (inCell.Count > 0 ? inCell : all)[0];
                if (all.Count > 1)
                    Console.WriteLine($"   [npc] {key} is placed {all.Count} time(s): " + string.Join("; ",
                        all.Select(x => $"{x.Ref.FormKey} in {(x.CellEd ?? x.Cell.ToString())} at {x.Ref.Position.X:0},{x.Ref.Position.Y:0},{x.Ref.Position.Z:0}"))
                        + $" — using {(inCell.Count > 0 ? "the one in the rendered cell" : "the first")}");
                p = new(pick.Ref.Position.X, pick.Ref.Position.Y, pick.Ref.Position.Z);
                rot = pick.Ref.Rotation;
                refScale = pick.Ref.Scale;
                placed = true;
                if (pick.Cell != cellKey && inCell.Count == 0)
                    Console.WriteLine($"   [npc] {key}'s placement is in {(pick.CellEd ?? pick.Cell.ToString())}, not the rendered cell");
            }
            if (pos is { } explicitPos) { p = explicitPos; placed = true; }
            if (!placed)
            { Console.Error.WriteLine($"--npc '{raw}': no placed instance of {key} anywhere in the load order — give an explicit @X,Y,Z[,HEADING]."); return 1; }
            if (headingDeg is { } hd) rot = new Vec3(0f, 0f, float.DegreesToRadians(hd));
            npcPlacements.Add((npcKey, new ViewProbe.NpcPlacement(p, rot, refScale), label ?? ""));
        }

        // One GL context for everything: the NPC actors are built on it, the scene is uploaded to
        // it, and — under --interactive — the fly-through runs on it (a visible, resizable window
        // with its own MSAA; the offscreen path keeps its hidden square). Born hidden either way;
        // InteractiveViewer shows it once the scene is resident, so no white window sits there
        // through a multi-minute load walk.
        var fpNws = new NativeWindowSettings
        {
            ClientSize = interactiveMode ? new OpenTK.Mathematics.Vector2i(1440, 900)
                                         : new OpenTK.Mathematics.Vector2i(size, size),
            StartVisible = false,
            StartFocused = false,
            WindowBorder = interactiveMode ? WindowBorder.Resizable : WindowBorder.Hidden,
            APIVersion = new Version(3, 3),
            Profile = ContextProfile.Core,
            NumberOfSamples = interactiveMode && msaa >= 2 ? Math.Min(msaa, 8) : 0,
        };
        using var fpGw = new GameWindow(GameWindowSettings.Default, fpNws);
        fpGw.MakeCurrent();

        // ---- --npc: build the actors on this context --------------------------------------
        // Record side first (a full-order link cache over fresh overlays — the walk above
        // disposed its own), then the runtime outfit stack, then one VM per NPC through the
        // character pipeline, world-baked. The VMs (and these overlays) must outlive every
        // panel: NpcCleanup() runs before each return below.
        var npcActors = new List<ViewProbe.NpcActorRender>();
        ViewProbe.NpcActorBuilder.Host? npcHost = null;
        var npcLcMods = new List<ISkyrimModGetter>();
        // Coloured caption lines: each actor's identity colour, and the route's. Drawn under the
        // white lines, each in the colour of the thing it names, so a picture with several marked
        // things says which is which without anyone counting rings.
        var colourCaptions = new List<(string Text, System.Numerics.Vector3 Color)>();
        // Per-actor IDENTITY colours, assigned in --npc order and worn by all three
        // faces of the marker at once — puck fill, rim, and the caption line — so a
        // multi-actor render answers "who is who" without counting rings. Chosen
        // bright (they sit on the dark caption bar) and clear of the reserved
        // semantic colours: conflict RED, door ORANGE, diff-sphere VIOLET. The nav
        // palette can still land on green/blue/yellow, but a filled ring plus a
        // matching caption line is not mistakable for a navmesh wire. Cycles past 5.
        var npcPalette = new (string Name, System.Numerics.Vector3 C)[]
        {
            ("yellow", new(1.00f, 0.88f, 0.10f)),
            ("pink",   new(1.00f, 0.40f, 0.80f)),
            ("green",  new(0.35f, 0.95f, 0.35f)),
            ("blue",   new(0.40f, 0.75f, 1.00f)),
            ("white",  new(1.00f, 1.00f, 1.00f)),
        };
        if (npcPlacements.Count > 0)
        {
            var swNpc = Stopwatch.StartNew();
            foreach (var pp2 in order.OrderedPaths)
            {
                try { npcLcMods.Add(SkyrimMod.CreateFromBinaryOverlay(pp2, SkyrimRelease.SkyrimSE)); }
                catch (Exception ex) { Console.Error.WriteLine($"   [npc] link cache: {Path.GetFileName(pp2)} skipped ({ex.Message})"); }
            }
            var npcLinkCache = npcLcMods.ToImmutableLinkCache<ISkyrimMod, ISkyrimModGetter>();
            var npcPluginNames = order.OrderedPaths.Select(pp3 => Path.GetFileName(pp3)!).ToList();
            var npcResolver = new ViewProbe.NpcRecordResolver(npcLcMods, npcPluginNames, npcLinkCache);
            var outfitStack = new ViewProbe.NpcOutfitStack(dataDir, modFolders, overwriteDir,
                npcResolver.IsPluginInstalled, npcResolver.ResolveSkyPatcherIdentifier,
                npcResolver.ResolveSpidFilterForm, npcResolver.ResolveSpidFormIdentifier, npcLinkCache);
            Console.WriteLine($"   [npc] runtime outfit configs: {outfitStack.SkyPatcherLineCount} SkyPatcher outfitDefault line(s) "
                            + $"in {outfitStack.SkyPatcherFileCount} npc ini file(s); {outfitStack.SpidEntryCount} SPID outfit entr(ies) "
                            + $"in {outfitStack.SpidFileCount} _DISTR file(s)");
            npcHost = ViewProbe.NpcActorBuilder.CreateHost(assets, dataDir, modFolders, overwriteDir);
            foreach (var (npcKey, placement, labelHint) in npcPlacements)
            {
                var rn = npcResolver.Resolve(npcKey);
                if (rn is null) continue;
                string label = string.IsNullOrEmpty(labelHint) ? rn.Name : labelHint;
                ViewProbe.NpcOutfitResult? outfit = null;
                List<CharacterViewer.Rendering.MeshOverride>? overrides = null;
                if (!npcNoOutfit)
                {
                    outfit = outfitStack.Resolve(rn.Winner, rn.Facts);
                    foreach (var note in outfit.RuntimeNotes) Console.WriteLine($"   [npc] {label}: {note}");
                    foreach (var apx in outfit.Approximations.Distinct()) Console.WriteLine($"   [npc] {label}: approximation: {apx}");
                    Console.WriteLine(outfit.OutfitKey is { } okey
                        ? $"   [npc] {label}: outfit {okey} via {outfit.SourceDetail}"
                        : $"   [npc] {label}: no outfit (record has none, no runtime layer assigns one)");
                    overrides = npcResolver.ResolveOutfitOverrides(rn.Winner, outfit.OutfitKey);
                    if (overrides.Count > 0) Console.WriteLine($"   [npc] {label}: {overrides.Count} attire piece(s)");
                }
                var (colourName, colour) = npcPalette[npcActors.Count % npcPalette.Length];
                var actor = ViewProbe.NpcActorBuilder.Build(npcHost, rn, overrides, placement, label, colour);
                if (actor is null) continue;
                npcActors.Add(actor);
                Console.WriteLine($"   [npc] {label}: identity colour {colourName} (puck + caption)");
                string outfitTag = npcNoOutfit ? "no outfit (--npc-no-outfit)"
                    : outfit?.OutfitKey is null ? "no outfit"
                    : outfit.Source switch
                    {
                        ViewProbe.NpcOutfitSource.SkyPatcher or ViewProbe.NpcOutfitSource.Spid => outfit.SourceDetail!,
                        ViewProbe.NpcOutfitSource.InventoryTemplate => "outfit via inventory template",
                        _ => "outfit: record DefaultOutfit",
                    };
                colourCaptions.Add(($"NPC {label} {rn.NpcKey} at {placement.Position.X:0},{placement.Position.Y:0},{placement.Position.Z:0}"
                              + $" heading {float.RadiansToDegrees(placement.Rotation.Z):0}° — {outfitTag}", colour));
            }
            Console.WriteLine($"   [npc] {npcActors.Count} actor(s) built  ({swNpc.Elapsed.TotalSeconds:0.0}s)");
        }
        // The route's caption, in the route's colour — the same rule the actors' lines follow. It
        // names the path and states the span it was validated over, so the picture carries its own
        // provenance: a reader can tell a drawn route from a decorative one by the numbers on it.
        if (routeRender is { Waypoints.Count: > 1 })
        {
            // Distance WALKED, summed along the ground-following legs — a chord between waypoints
            // would under-report every slope and staircase on the path.
            float walk = 0f;
            foreach (var leg in routeRender.Legs)
                for (int wi = 0; wi + 1 < leg.Count; wi++) walk += System.Numerics.Vector3.Distance(leg[wi], leg[wi + 1]);
            colourCaptions.Add(($"ROUTE {routeRender.Waypoints.Count} waypoints, {walk:0} u"
                             + (routeLabel.Length > 0 ? $" — {routeLabel}" : "")
                             + $" — z from the winning navmesh, every waypoint clearance-checked and every leg sampled onto it",
                              new System.Numerics.Vector3(routeColor.X, routeColor.Y, routeColor.Z)));
        }

        float PitchDownDeg()
        {
            var d = lookPos.Value - eyePos.Value;
            float horiz = MathF.Sqrt(d.X * d.X + d.Y * d.Y);
            return float.RadiansToDegrees(MathF.Atan2(-d.Z, MathF.Max(horiz, 1e-3f)));
        }
        bool npcPucksOn = npcActors.Count > 0 && npcPuck switch
        {
            "on" => true,
            "off" => false,
            // auto: a steep look-down is where a standing figure shrinks to a few
            // dozen pixels and needs the marker; eye-level shots read fine bare.
            _ => PitchDownDeg() > 60f,
        };
        if (npcActors.Count > 0)
            Console.WriteLine($"   [npc] pucks: {(npcPucksOn ? "on" : "off")} ({npcPuck}"
                            + (npcPuck == "auto" ? $", camera {PitchDownDeg():0}° down" : "") + ")");
        void NpcCleanup()
        {
            npcHost?.Disposer();
            foreach (var m2 in npcLcMods) (m2 as IDisposable)?.Dispose();
        }

        // The FULL camera pose travels with every render, so any shot can be replicated from the
        // image alone: burnt into the caption in the game's own conventions (yaw 0 = north/+Y,
        // clockwise; positive pitch looks down — the exact inverse of the --yaw/--pitch direction
        // math above), and embedded as a PNG tEXt chunk holding the arguments verbatim. A camera
        // given as --look round-trips as --yaw/--pitch; both compose the same view.
        float camYawDeg = (float.RadiansToDegrees(MathF.Atan2(lookPos.Value.X - eyePos.Value.X,
                                                              lookPos.Value.Y - eyePos.Value.Y)) + 360f) % 360f;
        float camPitchDeg = PitchDownDeg();
        string camPose = $"eye {eyePos.Value.X:0.##},{eyePos.Value.Y:0.##},{eyePos.Value.Z:0.##}"
                       + $" yaw {camYawDeg:0.#}° pitch {camPitchDeg:0.#}° fov {fov:0.#}°";
        // args = byte-exact replay; pose = the camera those args resolved to THIS run (matters
        // when the camera came from --eye-ref/--look-ref, whose positions move with the load order).
        string camMeta = $"args: {replayArgs} | pose: {camPose}"
                       + $" look {lookPos.Value.X:0.##},{lookPos.Value.Y:0.##},{lookPos.Value.Z:0.##}"
                       + $" | cell {cellLabel}";

        string safe = new string(cellLabel.Select(c => char.IsLetterOrDigit(c) ? c : '_').ToArray());
        // A worklist renders several shots of the SAME cell, so the cell name alone would have each
        // one overwrite the last. The number is the shot's position in the run — the same number the
        // fly-through's HUD and the bat manifest use — and the label follows it so the file is
        // findable without the worklist open beside it.
        if (worklist is not null)
        {
            var wsh = worklist.Cells[wlCell].Shots[wlShot];
            string slug = new string(wsh.Label.Select(c => char.IsLetterOrDigit(c) ? c : '_').ToArray()).Trim('_');
            if (slug.Length > 48) slug = slug[..48].TrimEnd('_');
            safe = $"{safe}-c{wlCell + 1}s{wlShot + 1}-{slug}";
        }

        // The target cell's own footprint, for the outline. Exteriors only — an interior has no grid
        // and no neighbours to be confused with.
        (float X0, float Y0, float X1, float Y1)? cellBounds = null;
        if (cellOutline && !interior && targetGrid is { } tgb)
            cellBounds = (tgb.X * 4096f, tgb.Y * 4096f, tgb.X * 4096f + 4096f, tgb.Y * 4096f + 4096f);

        // ---- WATER: one surface per loaded cell that has any -----------------------------------
        // Exteriors only. An interior's water is a placed basin with no grid footprint to span, so
        // there is nothing honest to draw a plane across; say so rather than inventing a rectangle.
        var waterPlanes = new List<ViewProbe.WaterPlane>();
        if (drawWater && !interior && targetGrid is { } wg)
        {
            var wref = wsWater;
            for (int dx = -1; dx <= 1; dx++)
                for (int dy = -1; dy <= 1; dy++)
                {
                    var key = (wg.X + dx, wg.Y + dy);
                    if (!cellWater.TryGetValue(key, out var cw) || !cw.Has) continue;
                    // The engine's own precedence: the cell's height when it states one, the
                    // worldspace default when it holds the sentinel.
                    float z = RealHeight(cw.Height) ? cw.Height : wsDefaultWaterHeight;
                    // VP_WATER_Z=<z> — force every surface to one height. A water plane at the
                    // real height is often correctly INVISIBLE (the sea floor around Winterhold
                    // sits above Tamriel's −14000, so the sea is genuinely underground there),
                    // and "invisible" and "not drawn" look identical. This separates them.
                    if (float.TryParse(Environment.GetEnvironmentVariable("VP_WATER_Z"), out var forcedZ)) z = forcedZ;
                    if (!RealHeight(z)) continue;                 // neither level states a height
                    var wk = cw.Water ?? wref;
                    var w = wk is { } k && watrRec.TryGetValue(k, out var wr)
                          ? wr
                          : ("(no WATR record)", (string?)null, 1024f,
                             new System.Numerics.Vector3(0.14f, 0.25f, 0.25f),
                             new System.Numerics.Vector3(0.03f, 0.13f, 0.13f),
                             new System.Numerics.Vector3(0.30f, 0.30f, 0.30f), 0.4f, (byte)50, 0.07f);
                    string? noise = w.Item2 is { Length: > 0 } np ? NormalizeTexPath(np) : null;
                    waterPlanes.Add(new ViewProbe.WaterPlane(
                        z, key.Item1 * 4096f, key.Item2 * 4096f, key.Item1 * 4096f + 4096f, key.Item2 * 4096f + 4096f,
                        noise, w.Item3, w.Item4, w.Item5, w.Item6, w.Item7,
                        // WATR.Opacity is a 0..100 percentage in the CK, not a 0..255 byte.
                        Math.Clamp(w.Item8 / 100f, 0f, 1f), w.Item9, w.Item1));
                }
            if (waterPlanes.Count == 0 && cellWater.Values.Any(v => v.Has))
                Console.WriteLine("   water: cell(s) flagged HasWater but no usable surface height "
                                + "in either the cell or the worldspace — nothing drawn");
            // Where each surface height CAME FROM, per cell. Worth a line of its own: a plane at
            // the wrong height is invisible rather than wrong-looking (the terrain simply covers
            // it), so a silent "9 surfaces drawn" can mean nine surfaces nobody will ever see.
            if (waterPlanes.Count > 0)
                foreach (var g in waterPlanes.GroupBy(w => w.Z).OrderBy(g => g.Key))
                {
                    int own = 0;
                    foreach (var w in g)
                    {
                        var k = ((int)MathF.Floor(w.X0 / 4096f), (int)MathF.Floor(w.Y0 / 4096f));
                        if (cellWater.TryGetValue(k, out var cwx) && RealHeight(cwx.Height)) own++;
                    }
                    Console.WriteLine($"      water z {g.Key:0}: {g.Count()} cell(s) — {own} from the cell's own "
                                    + $"XCLW, {g.Count() - own} inherited from the worldspace default");
                }
            // VP_WATER_DUMP=1 — the raw per-cell read, including the values RealHeight rejects.
            // A water plane at the wrong height is invisible, not wrong-looking, so when the sea
            // is missing this is the line that says whether the height or the drawing is at fault.
            if (Environment.GetEnvironmentVariable("VP_WATER_DUMP") == "1")
                for (int dx = -1; dx <= 1; dx++)
                    for (int dy = -1; dy <= 1; dy++)
                    {
                        var k = (wg.X + dx, wg.Y + dy);
                        if (!cellWater.TryGetValue(k, out var cwd))
                        { Console.WriteLine($"      cell ({k.Item1},{k.Item2}): no CELL record found in the walk"); continue; }
                        Console.WriteLine($"      cell ({k.Item1},{k.Item2}): HasWater={cwd.Has}  "
                                        + $"XCLW={cwd.Height:R} ({(RealHeight(cwd.Height) ? "used" : "rejected -> worldspace default")})  "
                                        + $"WATR={(cwd.Water?.ToString() ?? "(inherit)")}");
                    }
        }

        // ---- PROJECTED COVERINGS: STAT.Material -> MATO, for the bases actually in this scene ---
        var coverings = new Dictionary<FormKey, ViewProbe.Covering>();
        if (drawCoverings)
        {
            int coveringNoMato = 0;
            foreach (var bk in scene.Select(s => s.BaseKey).Distinct())
            {
                if (!baseMato.TryGetValue(bk, out var link)) continue;
                if (!matoRec.TryGetValue(link.Mato, out var mt)) { coveringNoMato++; continue; }
                // A SinglePass material states its colour outright; a multi-pass one leaves the
                // colour to its projected texture, so the tint has to be white or it would double.
                // A black SinglePassColor means "unset" in practice (every genuine single-pass
                // snow/ash record carries a real colour), so treat it as the texture case.
                bool tinted = mt.SinglePass && (mt.Color.X + mt.Color.Y + mt.Color.Z) > 0.01f;
                coverings[bk] = new ViewProbe.Covering(
                    mt.Projection, link.MaxAngle, mt.Bias, mt.Scale, mt.UvScale,
                    tinted ? new System.Numerics.Vector4(mt.Color.X, mt.Color.Y, mt.Color.Z, 1f)
                           : System.Numerics.Vector4.One,
                    mt.Model, mt.Ed);
            }
            if (coveringNoMato > 0)
                Console.WriteLine($"   coverings: {coveringNoMato} base(s) name a MaterialObject no active plugin defines");
        }

        // Violet: not in the healthy palette (green/cyan/magenta/yellow/blue), not the reserved red,
        // not the white of the cell outline, not the orange of an implicated door.
        var diffColor = new System.Numerics.Vector4(0.72f, 0.28f, 1f, 1f);
        var noDiff = Array.Empty<System.Numerics.Vector3>();

        int Render(IReadOnlyList<(System.Numerics.Vector3, System.Numerics.Vector3, System.Numerics.Vector3)> g,
                   IReadOnlyList<(System.Numerics.Vector3, System.Numerics.Vector3, System.Numerics.Vector3)> r,
                   string outFile, IReadOnlyList<string> captionLines,
                   IReadOnlyList<System.Numerics.Vector3>? diffs = null)
            => ViewProbe.TexturedScene.RenderToPng(
                   scene, assets, dataDir, modFolders, overwriteDir,
                   (eyePos.Value, lookPos.Value, fov), g, r, terrainPatches, terrainLayers, navMode, navColor,
                   skyColor, terrainHeightAt, dashUnderground, dashUnderStatics, msaa, cullAbove, cellBounds,
                   diffs ?? noDiff, diffColor, picks, captionLines, colourCaptions, npcActors, npcPucksOn, routeRender,
                   keepFx, fxStatic, listMeshes, waterPlanes, coverings, size, outFile,
                   camMeta);

        if (compare is { } cmp)
        {
            // Three panels, one camera: A's navmesh, B's navmesh, the winning state. The world
            // geometry and terrain are the winning state in all three — only the navmesh layer
            // changes, which is exactly the thing being compared.
            // Each side panel is the cell's FULL navmesh as if that plugin won: the winning state
            // with the plugin's own versions substituted in. Records neither plugin touches are then
            // IDENTICAL in all three panels, so any visible difference is the actual disagreement.
            IReadOnlyDictionary<FormKey, NavmeshGeometry> AsIfWon(string plg)
            {
                var d = new Dictionary<FormKey, NavmeshGeometry>(winNav);
                if (navByPlugin.TryGetValue(plg, out var own))
                    foreach (var kv in own) d[kv.Key] = kv.Value;
                return d;
            }
            var panels = new (string Label, IReadOnlyDictionary<FormKey, NavmeshGeometry> Navs)[]
            {
                (cmp.A, AsIfWon(cmp.A)),
                (cmp.B, AsIfWon(cmp.B)),
                ("winning state", winNav),
            };
            // (panel captions name the navmesh SOURCE; world geometry is the winning state in all three)
            foreach (var fk in redNav.Keys)
                Console.WriteLine($"   contested {fk}: " + string.Join("  |  ",
                    panels.Select(p => p.Label + ": " + (p.Navs.TryGetValue(fk, out var nv) ? nv.Triangles.Count + " tris" : "not provided"))));

            // ---- THE DIFF ------------------------------------------------------------------
            // Which navmesh VERTICES does one plugin's version have that the other's does not? Only
            // vertices a live (non-deleted, in-range) triangle actually uses count — an orphaned entry
            // in the vertex list is not something the picture shows. Positions are quantised to 1/8 u
            // so float noise cannot invent a difference; real navmesh vertices are units apart.
            static long Q(float v) => (long)MathF.Round(v * 8f);
            static IEnumerable<(long X, long Y, long Z, System.Numerics.Vector3 P)> LiveVerts(
                IReadOnlyDictionary<FormKey, NavmeshGeometry> navs)
            {
                foreach (var nav in navs.Values)
                    foreach (var tri in nav.Triangles)
                    {
                        if ((tri.Flags & 0x08) != 0) continue;
                        foreach (var idx in new[] { tri.A, tri.B, tri.C })
                        {
                            if (idx < 0 || idx >= nav.Vertices.Count) continue;
                            var p = V(nav.Vertices[idx]);
                            yield return (Q(p.X), Q(p.Y), Q(p.Z), p);
                        }
                    }
            }
            var keysA = new HashSet<(long, long, long)>(LiveVerts(panels[0].Navs).Select(v => (v.X, v.Y, v.Z)));
            var keysB = new HashSet<(long, long, long)>(LiveVerts(panels[1].Navs).Select(v => (v.X, v.Y, v.Z)));
            var changedKeys = new HashSet<(long, long, long)>(keysA);
            changedKeys.SymmetricExceptWith(keysB);
            // A panel shows the changed vertices IT has. For the two side panels that is exactly
            // "mine and not theirs"; for the winning panel it is whichever of the two it inherited.
            List<System.Numerics.Vector3> DiffFor(IReadOnlyDictionary<FormKey, NavmeshGeometry> navs)
            {
                var seen = new HashSet<(long, long, long)>();
                var outp = new List<System.Numerics.Vector3>();
                if (!navDiff) return outp;
                foreach (var (x, y, z, p) in LiveVerts(navs))
                    if (changedKeys.Contains((x, y, z)) && seen.Add((x, y, z))) outp.Add(p);
                return outp;
            }
            if (navDiff)
                Console.WriteLine($"   nav diff: {changedKeys.Count} vertex position(s) differ between "
                                + $"{cmp.A} ({keysA.Count} live vertices) and {cmp.B} ({keysB.Count})");

            var panelFiles = new List<string>();
            for (int pi = 0; pi < panels.Length; pi++)
            {
                var (label, navs) = panels[pi];
                if (pi < 2 && (!navByPlugin.TryGetValue(label, out var ownNv) || ownNv.Count == 0))
                    Console.WriteLine($"   panel '{label}': provides NO navmesh version in this cell -- panel equals the winning state");
                var (g, r) = BuildNavSets(navs);
                var diffs = DiffFor(navs);
                string pf = Path.Combine(outDir, $"{safe}-cmp{pi + 1}-{new string(label.Select(ch => char.IsLetterOrDigit(ch) ? ch : '_').ToArray())}.png");
                var cap = new List<string> { $"[{pi + 1}/3] {(pi == 2 ? "winning state (actual merged navmesh)" : $"cell navmesh if {label} won")} \u2014 {cellLabel} \u2014 {camPose}{CullCaption()}" };
                if (redNav.Count > 0) cap.Add(RedCaption(navs));
                if (DoorCaption() is { } dc) cap.Add(dc);
                // NPC caption lines ride the separate coloured channel (colourCaptions),
                // passed straight into the render call below.
                if (changedKeys.Count > 0 && navDiff)
                    cap.Add($"VIOLET spheres: {diffs.Count} navmesh vertex position(s) this version has and the other does not "
                          + $"({changedKeys.Count} differ in total)");
                Render(g, r, pf, cap, diffs);
                panelFiles.Add(pf);
                Console.WriteLine($"   panel {pi + 1} ({label}): {g.Count:N0} green + {r.Count:N0} red navmesh tris -> {pf}");
            }

            // Stitch left->right: A | B | winning.
            string stitched = Path.Combine(outDir, $"{safe}-compare.png");
            using (var canvas = new Image<Rgba32>(size * 3 + 8, size))
            {
                canvas.Mutate(ctx =>
                {
                    ctx.BackgroundColor(SixLabors.ImageSharp.Color.Black);
                    for (int pi = 0; pi < 3; pi++)
                        using (var img = Image.Load<Rgba32>(panelFiles[pi]))
                            ctx.DrawImage(img, new SixLabors.ImageSharp.Point(pi * (size + 4), 0), 1f);
                });
                // The stitch carries the same replication chunk as its panels (one camera, three panels).
                canvas.Metadata.GetPngMetadata().TextData.Add(
                    new SixLabors.ImageSharp.Formats.Png.Chunks.PngTextData("viewprobe", camMeta, string.Empty, string.Empty));
                canvas.SaveAsPng(stitched);
            }
            Console.WriteLine($"wrote:\n   {stitched}");
            Console.WriteLine($"   panels left->right: [1] {cmp.A}   [2] {cmp.B}   [3] winning state (+conflict red)");
            NpcCleanup();
            return 0;
        }

        if (navPlugin is not null)
        {
            var navs = navByPlugin.TryGetValue(navPlugin, out var d0) ? d0 : new Dictionary<FormKey, NavmeshGeometry>();
            Console.WriteLine($"   navmesh source: {navPlugin} ({navs.Count} record version(s) in this cell)");
            (navTris, redTris) = BuildNavSets(navs);
        }

        string fpOut = Path.Combine(outDir, $"{safe}-fp.png");
        var fpCap = new List<string> { $"{cellLabel} \u2014 {(navPlugin ?? "winning state")} \u2014 {camPose}{CullCaption()}" };
        if (redNav.Count > 0) fpCap.Add(RedCaption(navPlugin is not null && navByPlugin.TryGetValue(navPlugin, out var np0) ? np0 : winNav));
        if (DoorCaption() is { } fdc) fpCap.Add(fdc);

        if (interactiveMode)
        {
            // Same build, different ending: the scene stays resident and the window opens. The
            // navmesh identity list mirrors exactly what BuildNavSets admits to the overlay
            // (same source dict, same deleted/out-of-range filters), so a hover can only name
            // triangles that are actually drawn.
            var navSrc = navPlugin is not null && navByPlugin.TryGetValue(navPlugin, out var nsrc)
                ? (IReadOnlyDictionary<FormKey, NavmeshGeometry>)nsrc : winNav;
            var iopt = new ViewProbe.InteractiveOptions
            {
                Window = fpGw,
                RuntimeId = RuntimeId,
                TerrainDescribe = terrainDescribeAt,
                CellLabel = cellLabel,
                OutDir = outDir,
                SafeName = safe,
                RedNav = redNav.Keys.ToHashSet(),
                FlySpeed = flySpeed,
                LookSens = lookSens,
                // PageUp/PageDown step THIS cell's shots for free (everything is resident);
                // Ctrl+ steps cells, which only the run loop above can serve — and only when
                // the run actually has a worklist to step through.
                // The route rides into the window as overlay geometry (nothing extra to build) —
                // these are only what R and the HUD need to talk about it.
                RouteCount = routeRender?.Waypoints.Count ?? 0,
                RouteLabel = routeLabel,
                Worklist = worklist,
                WorklistCell = wlCell,
                WorklistShot = wlShot,
                CanSwitchCell = worklist is not null && worklist.Cells.Count > 1,
                // Announce the build-time knife in the HUD. The live clip keys cannot undo it, so
                // without this the bar can read "clip: off" over a scene whose roofs were removed
                // before the window existed.
                BuildCullAbove = cullAbove,
            };
            if (navMode != "off")
                foreach (var nav in navSrc.Values)
                    for (int ti = 0; ti < nav.Triangles.Count; ti++)
                    {
                        var tri = nav.Triangles[ti];
                        if ((tri.Flags & 0x08) != 0) continue;
                        if (tri.A < 0 || tri.B < 0 || tri.C < 0) continue;
                        if (tri.A >= nav.Vertices.Count || tri.B >= nav.Vertices.Count || tri.C >= nav.Vertices.Count) continue;
                        iopt.NavTris.Add((nav.FormKey, ti, V(nav.Vertices[tri.A]), V(nav.Vertices[tri.B]), V(nav.Vertices[tri.C])));
                    }

            int iShapes = ViewProbe.TexturedScene.RenderToPng(
                scene, assets, dataDir, modFolders, overwriteDir,
                (eyePos.Value, lookPos.Value, fov), navTris, redTris, terrainPatches, terrainLayers, navMode, navColor,
                skyColor, terrainHeightAt, dashUnderground, dashUnderStatics, msaa, cullAbove, cellBounds,
                noDiff, diffColor, picks, fpCap, colourCaptions, npcActors, npcPucksOn, routeRender, keepFx, fxStatic, listMeshes,
                waterPlanes, coverings, size, fpOut,
                camMeta, iopt);
            Console.WriteLine($"   interactive run over ({iShapes:N0} shapes were resident)");
        pendingSwitch = iopt.CellSwitch;
            NpcCleanup();
            return 0;
        }

        int drawnShapes = Render(navTris, redTris, fpOut, fpCap);
        Console.WriteLine($"wrote:\n   {fpOut}");
        Console.WriteLine($"   eye {eyePos.Value.X:0},{eyePos.Value.Y:0},{eyePos.Value.Z:0} -> look {lookPos.Value.X:0},{lookPos.Value.Y:0},{lookPos.Value.Z:0}"
                        + $"  yaw {camYawDeg:0.#}° pitch {camPitchDeg:0.#}° fov {fov:0}°  ({drawnShapes:N0} shapes)");
        NpcCleanup();
        return 0;
    }

    // Not a textured run: the flat diagnostic mode below is this call's continuation.
    return -1;
}

// ---------------------------------------------------------------------------------------
// THE RUN
// ---------------------------------------------------------------------------------------
if (worklist is not null)
{
    if (interactiveMode)
    {
        // One cell resident at a time. Ctrl+PageUp/PageDown sets CellSwitch and closes the window;
        // this loop is what "rebuilding the scene" actually means.
        int wlc = 0, wls = 0;
        while (true)
        {
            pendingSwitch = null;
            int rc = RunCellTextured(worklist.Cells[wlc].Cell, wlc, wls);
            if (pendingSwitch is not { } next) return rc < 0 ? 0 : rc;
            (wlc, wls) = next;
        }
    }

    // Static: every shot of every cell, each to its own PNG. One BUILD per shot — shots in the
    // same cell do not share a walk, because the render happens inside the build rather than
    // after it. Said out loud rather than hidden: on a multi-shot cell this is the dominant cost.
    int totalShots = worklist.Cells.Sum(c => c.Shots.Count);
    if (totalShots > worklist.Cells.Count)
        Console.WriteLine($"worklist: {totalShots} shots over {worklist.Cells.Count} cell(s) — "
                        + "the load walk runs once PER SHOT in static mode (use --interactive to pay it once per cell).");
    for (int wlc = 0; wlc < worklist.Cells.Count; wlc++)
        for (int wls = 0; wls < worklist.Cells[wlc].Shots.Count; wls++)
        {
            int rc = RunCellTextured(worklist.Cells[wlc].Cell, wlc, wls);
            if (rc > 0) return rc;
        }
    return 0;
}

int singleRc = RunCellTextured(target, 0, 0);
if (singleRc >= 0) return singleRc;


// ---------------------------------------------------------------------------------------
// 2. Build world geometry
// ---------------------------------------------------------------------------------------
var verts = new List<float>();      // 18 floats per vertex — GlMesh.Upload's exact layout
var idx = new List<int>();
var navVerts = new List<float>();
var navIdx = new List<int>();
var lineVerts = new List<float>();

var geoCache = new Dictionary<string, TriangleSoup?>(StringComparer.OrdinalIgnoreCase);
int drawn = 0, noGeo = 0, flatDisabled = 0;
long triCount = 0;
var bounds = Aabb.Empty;

FormKey? hi = null;
if (highlight is not null) { try { hi = FormKey.Factory(highlight); } catch { } }

var activeOrder = SpatialOverlap.EulerOrder.Xyz;

void Build(SpatialOverlap.EulerOrder order)
{
    activeOrder = order;
    verts.Clear(); idx.Clear(); navVerts.Clear(); navIdx.Clear(); lineVerts.Clear();
    drawn = 0; noGeo = 0; flatDisabled = 0; triCount = 0; bounds = Aabb.Empty;

    sw.Restart();
    foreach (var r in winRef.Values)
    {
        if (r.Deleted) continue;
        if (!RefEnabled(r.FormKey)) { flatDisabled++; continue; }
        if (r.Base is not { } bk || !baseMesh.TryGetValue(bk, out var rel)) { noGeo++; continue; }

        if (!geoCache.TryGetValue(rel, out var soup))
        {
            soup = null;
            var full = rel.StartsWith("meshes", StringComparison.OrdinalIgnoreCase) ? rel : Path.Combine("meshes", rel);
            var res = assets.ResolveForPlacement(full);
            if (res.Sources.Count > 0)
            {
                var (bytes, _) = AssetResolver.ReadPlacementSource(res.Sources[0]);
                if (bytes is not null)
                {
                    var o = NifGeometry.Measure(bytes, includeTriangles: true);
                    if (o.Model?.Soup is { TriangleCount: > 0 and <= 300_000 } s) soup = s;
                }
            }
            geoCache[rel] = soup;
        }
        if (soup is null) { noGeo++; continue; }

        // The SAME composition ToWorld uses, so what is drawn is what the detector measured. If the
        // order is wrong, this picture is wrong in exactly the way the numbers are — which is the point.
        var mat = SpatialOverlap.Euler(r.Rotation, activeOrder);
        var t = new Vector3(r.Position.X, r.Position.Y, r.Position.Z);

        bool isHi = hi is { } h && r.FormKey == h;
        // A stable per-object tint so separate objects read as separate objects; the highlight overrides.
        uint seed = (uint)(r.FormKey.ID * 2654435761u);
        var col = isHi ? new Vector4(1.0f, 0.35f, 0.1f, 1f)
                       : new Vector4(0.55f + ((seed >> 8) & 63) / 255f,
                                     0.52f + ((seed >> 16) & 63) / 255f,
                                     0.48f + ((seed >> 24) & 63) / 255f, 1f);

        int baseIdx = verts.Count / 18;
        var world = new Vector3[soup.Vertices.Length];
        for (int i = 0; i < world.Length; i++)
        {
            world[i] = Vector3.Transform(soup.Vertices[i] * r.Scale, mat) + t;
            bounds = bounds.Add(world[i]);
        }
        // Flat shading: a face normal per triangle needs its own vertices, which also keeps the kit
        // pieces legible instead of smoothing every edge away.
        for (int i = 0; i + 2 < soup.Indices.Length; i += 3)
        {
            var a = world[soup.Indices[i]]; var b = world[soup.Indices[i + 1]]; var c = world[soup.Indices[i + 2]];
            var n = Vector3.Cross(b - a, c - a);
            n = n.LengthSquared() > 1e-12f ? Vector3.Normalize(n) : Vector3.UnitZ;
            int at = verts.Count / 18;
            Push(verts, a, n, col); Push(verts, b, n, col); Push(verts, c, n, col);
            idx.Add(at); idx.Add(at + 1); idx.Add(at + 2);
            triCount++;
        }
        drawn++;
    }

    if (drawNav)
    {
        var navCol = new Vector4(0.20f, 0.95f, 0.45f, 0.42f);
        foreach (var nav in winNav.Values)
            foreach (var tri in nav.Triangles)
            {
                if ((tri.Flags & 0x08) != 0) continue;
                if (tri.A < 0 || tri.B < 0 || tri.C < 0) continue;
                if (tri.A >= nav.Vertices.Count || tri.B >= nav.Vertices.Count || tri.C >= nav.Vertices.Count) continue;
                var a = V(nav.Vertices[tri.A]); var b = V(nav.Vertices[tri.B]); var c = V(nav.Vertices[tri.C]);
                var n = Vector3.Cross(b - a, c - a);
                n = n.LengthSquared() > 1e-12f ? Vector3.Normalize(n) : Vector3.UnitZ;
                int at = navVerts.Count / 18;
                Push(navVerts, a, n, navCol); Push(navVerts, b, n, navCol); Push(navVerts, c, n, navCol);
                navIdx.Add(at); navIdx.Add(at + 1); navIdx.Add(at + 2);
                bounds = bounds.Add(a); bounds = bounds.Add(b); bounds = bounds.Add(c);

                var edge = new Vector4(0.05f, 0.55f, 0.25f, 1f);
                var lift = new Vector3(0, 0, 1.5f);   // hairline lift so edges are not z-fought away
                foreach (var (p, q) in new[] { (a, b), (b, c), (c, a) })
                { Push(lineVerts, p + lift, n, edge); Push(lineVerts, q + lift, n, edge); }
            }
    }
}

Build(activeOrder);

Console.WriteLine($"geometry: {drawn:N0} refs drawn ({noGeo:N0} without usable mesh, "
                + $"{flatDisabled:N0} disabled at runtime), {triCount:N0} triangles, "
                + $"{navIdx.Count / 3:N0} navmesh triangles  ({sw.Elapsed.TotalSeconds:0.0}s)");
if (bounds.IsEmpty) { Console.Error.WriteLine("nothing to draw."); return 1; }
Console.WriteLine($"bounds  : {bounds}");

// ---------------------------------------------------------------------------------------
// 3. Render
// ---------------------------------------------------------------------------------------
var nws = new NativeWindowSettings
{
    ClientSize = new OpenTK.Mathematics.Vector2i(size, size),
    StartVisible = false,
    StartFocused = false,
    WindowBorder = WindowBorder.Hidden,
    APIVersion = new Version(3, 3),
    Profile = ContextProfile.Core,
};
using var gw = new GameWindow(GameWindowSettings.Default, nws);
gw.MakeCurrent();

int prog = Shader();
int nOpaque;
int vaoOpaque = Upload(verts, idx, out nOpaque);
int vaoNav = navIdx.Count > 0 ? Upload(navVerts, navIdx, out _) : 0;
int nNavIdx = navIdx.Count;
int vaoLines = lineVerts.Count > 0 ? UploadLines(lineVerts, out _) : 0;
int nLineVerts = lineVerts.Count / 18;

int fbo = GL.GenFramebuffer(), colorTex = GL.GenTexture(), depthRb = GL.GenRenderbuffer();
GL.BindFramebuffer(FramebufferTarget.Framebuffer, fbo);
GL.BindTexture(TextureTarget.Texture2D, colorTex);
GL.TexImage2D(TextureTarget.Texture2D, 0, PixelInternalFormat.Rgba8, size, size, 0, PixelFormat.Rgba, PixelType.UnsignedByte, IntPtr.Zero);
GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMinFilter, (int)TextureMinFilter.Linear);
GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMagFilter, (int)TextureMagFilter.Linear);
GL.FramebufferTexture2D(FramebufferTarget.Framebuffer, FramebufferAttachment.ColorAttachment0, TextureTarget.Texture2D, colorTex, 0);
GL.BindRenderbuffer(RenderbufferTarget.Renderbuffer, depthRb);
GL.RenderbufferStorage(RenderbufferTarget.Renderbuffer, RenderbufferStorage.DepthComponent24, size, size);
GL.FramebufferRenderbuffer(FramebufferTarget.Framebuffer, FramebufferAttachment.DepthAttachment, RenderbufferTarget.Renderbuffer, depthRb);
if (GL.CheckFramebufferStatus(FramebufferTarget.Framebuffer) != FramebufferErrorCode.FramebufferComplete)
{ Console.Error.WriteLine("framebuffer incomplete."); return 1; }

var c0 = bounds.Center;
var centre = new Vector3(c0.X, c0.Y, c0.Z);
var sz = bounds.Size;
float span = MathF.Max(sz.X, MathF.Max(sz.Y, sz.Z));

var shots = new List<string>();
if (euler)
{
    // One angle, six compositions. A wrong order does not shift a building subtly — it takes every
    // multi-axis reference and throws it hundreds of units, which the eye separates instantly and
    // the containment statistic could not separate at all.
    foreach (var ord in Enum.GetValues<SpatialOverlap.EulerOrder>())
    {
        Build(ord);
        GL.DeleteVertexArray(vaoOpaque);
        vaoOpaque = Upload(verts, idx, out nOpaque);
        if (vaoNav != 0) { GL.DeleteVertexArray(vaoNav); vaoNav = Upload(navVerts, navIdx, out _); nNavIdx = navIdx.Count; }
        if (vaoLines != 0) { GL.DeleteVertexArray(vaoLines); vaoLines = UploadLines(lineVerts, out _); nLineVerts = lineVerts.Count / 18; }
        var bc = bounds.Center; centre = new Vector3(bc.X, bc.Y, bc.Z);
        var bs = bounds.Size; span = MathF.Max(bs.X, MathF.Max(bs.Y, bs.Z));
        shots.Add(RenderOne(angles[0], elev, $"{Safe(cellLabel)}-euler-{ord.ToString().ToUpperInvariant()}.png"));
        Console.WriteLine($"   {ord,-4} bounds {bounds}");
    }
    GL.DeleteFramebuffer(fbo); GL.DeleteTexture(colorTex); GL.DeleteRenderbuffer(depthRb);
    Console.WriteLine();
    Console.WriteLine("wrote:");
    foreach (var sh in shots) Console.WriteLine($"   {sh}");
    return 0;
}

foreach (var az in angles) shots.Add(RenderOne(az, elev, $"{Safe(cellLabel)}-az{az:0}.png"));
shots.Add(RenderOne(0, 89.5f, $"{Safe(cellLabel)}-plan.png"));      // the view that reads unambiguously

GL.DeleteFramebuffer(fbo); GL.DeleteTexture(colorTex); GL.DeleteRenderbuffer(depthRb);
Console.WriteLine("\nwrote:");
foreach (var s in shots) Console.WriteLine($"   {s}");
return 0;

// ---------------------------------------------------------------------------------------
string RenderOne(float azDeg, float elDeg, string file)
{
    float az = azDeg * MathF.PI / 180f, el = elDeg * MathF.PI / 180f;
    float dist = span * 1.15f + 200f;
    var eye = centre + new Vector3(MathF.Cos(el) * MathF.Cos(az), MathF.Cos(el) * MathF.Sin(az), MathF.Sin(el)) * dist;
    var up = elDeg > 88f ? Vector3.UnitY : Vector3.UnitZ;

    var view = Matrix4x4.CreateLookAt(eye, centre, up);
    var proj = Matrix4x4.CreatePerspectiveFieldOfView(50f * MathF.PI / 180f, 1f, MathF.Max(1f, dist - span * 2f), dist + span * 3f);
    var mvp = view * proj;

    GL.BindFramebuffer(FramebufferTarget.Framebuffer, fbo);
    GL.Viewport(0, 0, size, size);
    GL.Enable(EnableCap.DepthTest);
    GL.ClearColor(0.10f, 0.11f, 0.13f, 1f);
    GL.Clear(ClearBufferMask.ColorBufferBit | ClearBufferMask.DepthBufferBit);

    GL.UseProgram(prog);
    SetMat(prog, "uMvp", mvp);
    var lightDir = Vector3.Normalize(new Vector3(0.45f, 0.35f, 0.82f));
    GL.Uniform3(GL.GetUniformLocation(prog, "uLight"), lightDir.X, lightDir.Y, lightDir.Z);

    // Opaque first, then navmesh blended with depth test but no depth write, so navmesh that is
    // genuinely buried inside geometry is HIDDEN by it — which is the whole thing being looked for.
    GL.Disable(EnableCap.Blend);
    GL.DepthMask(true);
    GL.BindVertexArray(vaoOpaque);
    GL.DrawElements(PrimitiveType.Triangles, nOpaque, DrawElementsType.UnsignedInt, 0);

    if (vaoNav != 0)
    {
        GL.Enable(EnableCap.Blend);
        GL.BlendFunc(BlendingFactor.SrcAlpha, BlendingFactor.OneMinusSrcAlpha);
        GL.DepthMask(false);
        GL.BindVertexArray(vaoNav);
        GL.DrawElements(PrimitiveType.Triangles, nNavIdx, DrawElementsType.UnsignedInt, 0);
        GL.DepthMask(true);
        GL.Disable(EnableCap.Blend);
    }
    if (vaoLines != 0)
    {
        GL.BindVertexArray(vaoLines);
        GL.DrawArrays(PrimitiveType.Lines, 0, nLineVerts);
    }

    var px = new byte[size * size * 4];
    GL.ReadPixels(0, 0, size, size, PixelFormat.Rgba, PixelType.UnsignedByte, px);
    // Opaque screenshot: the colour buffer's alpha is whatever the shader wrote (the navmesh
    // sheet ships 0.42), and a PNG that carries it composites over whatever a viewer puts
    // behind it. Same fix as the textured path.
    for (int i = 3; i < px.Length; i += 4) px[i] = 255;

    using var img = Image.LoadPixelData<Rgba32>(px, size, size);
    img.Mutate(x => x.Flip(SixLabors.ImageSharp.Processing.FlipMode.Vertical));   // GL origin is bottom-left
    string outPath = Path.Combine(outDir, file);
    // Replication chunk, same key the textured path writes: flat mode's camera is derived
    // (orbit around the geometry bounds), so the chunk carries BOTH the arguments that
    // reproduce the shot and the absolute pose they resolved to this run. The pose is also
    // printed per shot — bounds move when the loaded set does, so the numbers are the record.
    float flatYaw = (float.RadiansToDegrees(MathF.Atan2(centre.X - eye.X, centre.Y - eye.Y)) + 360f) % 360f;
    float flatPitch = float.RadiansToDegrees(MathF.Atan2(eye.Z - centre.Z,
                          MathF.Max(1e-3f, MathF.Sqrt((centre.X - eye.X) * (centre.X - eye.X) + (centre.Y - eye.Y) * (centre.Y - eye.Y)))));
    string flatMeta = $"args: {replayArgs} | flat az {azDeg:0.#} elev {elDeg:0.#}"
                    + $" | pose: eye {eye.X:0.##},{eye.Y:0.##},{eye.Z:0.##} look {centre.X:0.##},{centre.Y:0.##},{centre.Z:0.##}"
                    + $" yaw {flatYaw:0.#} pitch {flatPitch:0.#} fov 50 | cell {cellLabel}";
    img.Metadata.GetPngMetadata().TextData.Add(
        new SixLabors.ImageSharp.Formats.Png.Chunks.PngTextData("viewprobe", flatMeta, string.Empty, string.Empty));
    Console.WriteLine($"   {file}: az {azDeg:0.#}° elev {elDeg:0.#}° — eye {eye.X:0},{eye.Y:0},{eye.Z:0}"
                    + $" look {centre.X:0},{centre.Y:0},{centre.Z:0} yaw {flatYaw:0.#}° pitch {flatPitch:0.#}° fov 50°");
    img.SaveAsPng(outPath);
    return outPath;
}

static void Push(List<float> dst, Vector3 p, Vector3 n, Vector4 c)
{
    dst.Add(p.X); dst.Add(p.Y); dst.Add(p.Z);
    dst.Add(n.X); dst.Add(n.Y); dst.Add(n.Z);
    dst.Add(0); dst.Add(0);                                   // uv — unused until textures land
    dst.Add(c.X); dst.Add(c.Y); dst.Add(c.Z); dst.Add(c.W);
    dst.Add(0); dst.Add(0); dst.Add(0);                       // tangent
    dst.Add(0); dst.Add(0); dst.Add(0);                       // bitangent
}

static Vector3 V(Vec3 v) => new(v.X, v.Y, v.Z);
static string Safe(string s) => string.Concat(s.Select(ch => char.IsLetterOrDigit(ch) ? ch : '_'));

static int Upload(List<float> v, List<int> i, out int count)
{
    count = i.Count;
    int vao = GL.GenVertexArray(), vbo = GL.GenBuffer(), ebo = GL.GenBuffer();
    GL.BindVertexArray(vao);
    var va = v.ToArray(); var ia = i.ToArray();
    GL.BindBuffer(BufferTarget.ArrayBuffer, vbo);
    GL.BufferData(BufferTarget.ArrayBuffer, va.Length * sizeof(float), va, BufferUsageHint.StaticDraw);
    GL.BindBuffer(BufferTarget.ElementArrayBuffer, ebo);
    GL.BufferData(BufferTarget.ElementArrayBuffer, ia.Length * sizeof(int), ia, BufferUsageHint.StaticDraw);
    Attribs();
    GL.BindVertexArray(0);
    return vao;
}

static int UploadLines(List<float> v, out int count)
{
    count = v.Count / 18;
    int vao = GL.GenVertexArray(), vbo = GL.GenBuffer();
    GL.BindVertexArray(vao);
    var va = v.ToArray();
    GL.BindBuffer(BufferTarget.ArrayBuffer, vbo);
    GL.BufferData(BufferTarget.ArrayBuffer, va.Length * sizeof(float), va, BufferUsageHint.StaticDraw);
    Attribs();
    GL.BindVertexArray(0);
    return vao;
}

// The 18-float interleave CharacterViewer.Rendering's GlMesh uses, attribute for attribute.
static void Attribs()
{
    int stride = 18 * sizeof(float), off = 0;
    GL.VertexAttribPointer(0, 3, VertexAttribPointerType.Float, false, stride, off); GL.EnableVertexAttribArray(0); off += 3 * sizeof(float);
    GL.VertexAttribPointer(1, 3, VertexAttribPointerType.Float, false, stride, off); GL.EnableVertexAttribArray(1); off += 3 * sizeof(float);
    GL.VertexAttribPointer(2, 2, VertexAttribPointerType.Float, false, stride, off); GL.EnableVertexAttribArray(2); off += 2 * sizeof(float);
    GL.VertexAttribPointer(3, 4, VertexAttribPointerType.Float, false, stride, off); GL.EnableVertexAttribArray(3); off += 4 * sizeof(float);
    GL.VertexAttribPointer(4, 3, VertexAttribPointerType.Float, false, stride, off); GL.EnableVertexAttribArray(4); off += 3 * sizeof(float);
    GL.VertexAttribPointer(5, 3, VertexAttribPointerType.Float, false, stride, off); GL.EnableVertexAttribArray(5);
}

static void SetMat(int prog, string name, Matrix4x4 m)
{
    Span<float> f = stackalloc float[16]
    {
        m.M11, m.M12, m.M13, m.M14, m.M21, m.M22, m.M23, m.M24,
        m.M31, m.M32, m.M33, m.M34, m.M41, m.M42, m.M43, m.M44,
    };
    GL.UniformMatrix4(GL.GetUniformLocation(prog, name), 1, false, ref f[0]);
}

static int Shader()
{
    const string vs = @"#version 330 core
layout(location=0) in vec3 aPos;
layout(location=1) in vec3 aNrm;
layout(location=2) in vec2 aUv;
layout(location=3) in vec4 aCol;
uniform mat4 uMvp;
out vec3 vN; out vec4 vC;
void main(){ vN = aNrm; vC = aCol; gl_Position = uMvp * vec4(aPos,1.0); }";

    const string fs = @"#version 330 core
in vec3 vN; in vec4 vC;
uniform vec3 uLight;
out vec4 frag;
void main(){
    vec3 n = normalize(vN);
    // Two-sided: Skyrim kit meshes are full of inward-facing shells and a one-sided lambert
    // renders half of every room pitch black.
    float d = abs(dot(n, uLight));
    float sky = 0.35 + 0.35 * clamp(n.z * 0.5 + 0.5, 0.0, 1.0);
    frag = vec4(vC.rgb * (sky + 0.65 * d), vC.a);
}";
    int v = GL.CreateShader(ShaderType.VertexShader); GL.ShaderSource(v, vs); GL.CompileShader(v); Check(v, "vertex");
    int f = GL.CreateShader(ShaderType.FragmentShader); GL.ShaderSource(f, fs); GL.CompileShader(f); Check(f, "fragment");
    int p = GL.CreateProgram(); GL.AttachShader(p, v); GL.AttachShader(p, f); GL.LinkProgram(p);
    GL.GetProgram(p, GetProgramParameterName.LinkStatus, out int ok);
    if (ok == 0) throw new InvalidOperationException("link failed: " + GL.GetProgramInfoLog(p));
    GL.DeleteShader(v); GL.DeleteShader(f);
    return p;

    static void Check(int s, string what)
    {
        GL.GetShader(s, ShaderParameter.CompileStatus, out int ok);
        if (ok == 0) throw new InvalidOperationException($"{what} shader: {GL.GetShaderInfoLog(s)}");
    }
}

// A dropped worklist, told apart from a cell name: an existing FILE on disk, named .json. Checked
// as a file rather than by extension alone so `viewprobe SomeCell.json` — a path that is not there,
// or a cell genuinely spelled that way — still reports the cell error it always did.
static bool IsWorklistFile(string a) =>
    a.Length > 0 && a[0] != '-'
    && a.EndsWith(".json", StringComparison.OrdinalIgnoreCase)
    && File.Exists(a);

// A GUI launch owns its console ALONE — a shell run has the shell on it too — and has real streams,
// since a redirected one means something is capturing the output rather than a person reading it.
// Measured 2026-08-29 on this machine: Explorer gives 1 and no redirection; Git Bash gives 3 and
// redirects; cmd gives 4 and redirects. Both halves are needed: mintty redirects without adding a
// process, and a `>out.txt` from a real console adds one without redirecting the input.
static bool GuiLaunch()
{
    try
    {
        if (Console.IsOutputRedirected || Console.IsInputRedirected) return false;
        var pids = new uint[4];
        return GetConsoleProcessList(pids, (uint)pids.Length) == 1;
    }
    catch { return false; }                     // no console at all is not a GUI launch either
}

[System.Runtime.InteropServices.DllImport("kernel32.dll", SetLastError = true)]
static extern uint GetConsoleProcessList(uint[] processList, uint count);

static int Smoke()
{
    var nws = new NativeWindowSettings
    {
        ClientSize = new OpenTK.Mathematics.Vector2i(64, 64),
        StartVisible = false,
        APIVersion = new Version(3, 3),
        Profile = ContextProfile.Core,
    };
    using var gw = new GameWindow(GameWindowSettings.Default, nws);
    gw.MakeCurrent();
    Console.WriteLine($"GL {GL.GetString(StringName.Version)} on {GL.GetString(StringName.Renderer)}");
    return 0;
}
